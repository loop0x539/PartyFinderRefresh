using System;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Components;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin.Services;
using PartyFinderRefresh.Game;
using PartyFinderRefresh.Models;

namespace PartyFinderRefresh.UI;

public sealed class MainWindow : Window, IDisposable
{
    // Packed ImGui colors are ABGR.
    private const uint ColorRed = 0xFF3344FFu;
    private const uint ColorGreen = 0xFF44DD44u;
    private const uint ColorYellow = 0xFF44DDDDu;
    private const uint ColorTank = 0xFFE0A050u;
    private const uint ColorHealer = 0xFF60D060u;
    private const uint ColorDps = 0xFF5060E0u;

    private const string SavePopupId = "Save current setup###pfrSavePreset";
    private const int NameMaxLength = 64;
    private const int CommentMaxLength = 192;

    private readonly Configuration config;
    private readonly PartyFinderManager pfm;
    private readonly RefreshScheduler scheduler;
    private readonly DutyNames duties;
    private readonly JobMasks jobMasks;
    private readonly IChatGui chat;

    private string newPresetName = string.Empty;
    private bool focusNewPresetName;
    private Guid? editingId;

    public MainWindow(Configuration config, PartyFinderManager pfm, RefreshScheduler scheduler,
                      DutyNames duties, JobMasks jobMasks, IChatGui chat, Action openConfig)
        : base("Party Finder Refresh###PartyFinderRefreshMain")
    {
        this.config = config;
        this.pfm = pfm;
        this.scheduler = scheduler;
        this.duties = duties;
        this.jobMasks = jobMasks;
        this.chat = chat;

        Size = new Vector2(460, 540);
        SizeCondition = ImGuiCond.FirstUseEver;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(360, 280),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
        };

        TitleBarButtons.Add(new TitleBarButton
        {
            Icon = FontAwesomeIcon.Cog,
            IconOffset = new Vector2(2, 1),
            Click = _ => openConfig(),
            ShowTooltip = () => ImGui.SetTooltip("Settings"),
        });
    }

    public void Dispose() { }

    public override void Draw()
    {
        DrawStatus();

        ImGuiHelpers.ScaledDummy(2);
        ImGui.Separator();

        DrawPresetHeader();
        DrawSavePopup();

        using var list = ImRaii.Child("##presets", Vector2.Zero);
        if (list)
            DrawPresetList();
    }

    // ---------------------------------------------------------------- status

    private void DrawStatus()
    {
        if (!pfm.Available)
        {
            ImGui.TextColored(ColorRed, "Party Finder functions unavailable");
            ImGui.TextWrapped(pfm.UnavailableReason ?? "Deploy and refresh are disabled.");
            ImGui.TextDisabled("You can still save and edit presets.");
            return;
        }

        var active = ActivePreset();
        var recruiting = pfm.IsRecruiting;

        // Headline: is a listing up right now?
        var (dotColor, headline) = pfm.Busy ? (ColorYellow, "Re-posting listing...")
            : recruiting ? (ColorGreen, "Recruiting")
            : (ImGui.GetColorU32(ImGuiCol.TextDisabled), "Not recruiting");
        DrawIcon(FontAwesomeIcon.Circle, dotColor);
        ImGui.SameLine();
        ImGui.TextUnformatted(headline);

        if (active == null)
        {
            DrawIdleHint();
            return;
        }

        // Party fill, right-aligned on the headline row.
        var slots = RefreshScheduler.PartySlots(active);
        var members = Math.Max(1, scheduler.PartyCount); // a solo player reads as an empty party list
        var partyText = $"Party {members}/{slots}";
        SameLineRightAligned(ImGui.CalcTextSize(partyText).X);
        if (members >= slots)
            ImGui.TextColored(ColorGreen, partyText);
        else
            ImGui.TextUnformatted(partyText);

        ImGui.TextDisabled("Keeping alive:");
        ImGui.SameLine();
        ImGui.TextColored(ColorGreen, active.Name);

        DrawActiveState(recruiting);

        using (ImRaii.Disabled(pfm.Busy))
        {
            if (ImGuiComponents.IconButtonWithText(FontAwesomeIcon.Redo, "Renew now"))
                DeployPreset(active);
        }
        Tooltip("Re-post the listing now and restart the timer.");

        ImGui.SameLine();
        if (ImGuiComponents.IconButtonWithText(FontAwesomeIcon.Stop, "Stop"))
            scheduler.Stop();
        Tooltip("Stop keeping this listing alive.\nThe in-game listing stays up until it expires or you end it.");
    }

    private void DrawActiveState(bool recruiting)
    {
        if (!config.AutoRenewEnabled)
        {
            using (ImRaii.PushColor(ImGuiCol.Text, ColorYellow))
                ImGui.TextWrapped("Automation is turned off, so this listing won't be renewed or stopped automatically.");
            if (ImGui.SmallButton("Turn automation on"))
            {
                config.AutoRenewEnabled = true;
                config.Save();
            }
            return;
        }

        if (pfm.Busy)
        {
            ImGui.TextDisabled("Working in the Party Finder window...");
            return;
        }

        if (recruiting)
        {
            var interval = scheduler.ActionIntervalSeconds;
            var left = scheduler.SecondsUntilNextAction() ?? 0;
            var action = scheduler.NextActionIsBump ? "Bump" : "Renew";
            var overlay = left > 0 ? $"{action} in {FormatDuration(left)}" : $"{action} pending...";
            using (ImRaii.PushColor(ImGuiCol.PlotHistogram, 0xFF2E8B57u))
                ImGui.ProgressBar((float)(left / interval), new Vector2(-1, 0), overlay);
            return;
        }

        if (scheduler.InStartupGrace)
            ImGui.TextDisabled("Waiting for the listing to go up...");
        else if (pfm.InAnyParty)
            ImGui.TextDisabled(config.FixOnMemberLeave
                ? "Listing is down. It'll be re-posted if someone leaves."
                : "Listing is down.");
        else
            ImGui.TextDisabled("Listing is down. Stopping shortly.");
    }

    private void DrawIdleHint()
    {
        if (scheduler.LastAutoStopReason is { } reason)
        {
            TextDisabledWrapped(
                $"Stopped keeping '{scheduler.LastAutoStopPreset}' alive {Ago(scheduler.LastAutoStopUtc)}: {reason}.");
            return;
        }

        TextDisabledWrapped(!config.AutoRenewEnabled
            ? "Deploy a preset to post it."
            : config.StopWhenFull
                ? "Deploy a preset to post it. It's kept alive until the party fills."
                : "Deploy a preset to post it and keep it alive.");
    }

    // ---------------------------------------------------------------- presets

    private void DrawPresetHeader()
    {
        const string saveLabel = "Save current setup";

        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted(config.Presets.Count == 0 ? "Presets" : $"Presets ({config.Presets.Count})");

        SameLineRightAligned(ImGuiComponents.GetIconButtonWithTextWidth(FontAwesomeIcon.Plus, saveLabel));
        var hasData = pfm.HasRecruitmentData;
        using (ImRaii.Disabled(!hasData))
        {
            if (ImGuiComponents.IconButtonWithText(FontAwesomeIcon.Plus, saveLabel))
            {
                newPresetName = SuggestedName();
                focusNewPresetName = true;
                ImGui.OpenPopup(SavePopupId);
            }
        }
        Tooltip(hasData
            ? "Save your current in-game recruitment setup as a new preset."
            : "Open the Party Finder, click Recruit Members and set up your listing first.");
    }

    private void DrawSavePopup()
    {
        using var popup = ImRaii.Popup(SavePopupId);
        if (!popup)
            return;

        ImGui.TextUnformatted("Preset name");
        if (focusNewPresetName)
        {
            ImGui.SetKeyboardFocusHere();
            focusNewPresetName = false;
        }
        ImGui.SetNextItemWidth(260 * ImGuiHelpers.GlobalScale);
        var submitted = ImGui.InputTextWithHint("##newPresetName", "Preset name", ref newPresetName, NameMaxLength,
                                                ImGuiInputTextFlags.EnterReturnsTrue);

        var canSave = !string.IsNullOrWhiteSpace(newPresetName);
        using (ImRaii.Disabled(!canSave))
        {
            if (ImGui.Button("Save") || (submitted && canSave))
            {
                SaveNewPreset(newPresetName.Trim());
                ImGui.CloseCurrentPopup();
            }
        }

        ImGui.SameLine();
        if (ImGui.Button("Cancel") || ImGui.IsKeyPressed(ImGuiKey.Escape))
            ImGui.CloseCurrentPopup();
    }

    private void DrawPresetList()
    {
        if (config.Presets.Count == 0)
        {
            ImGuiHelpers.ScaledDummy(4);
            TextDisabledWrapped("No presets yet.");
            TextDisabledWrapped("1. In-game, open the Party Finder and click Recruit Members.");
            TextDisabledWrapped("2. Pick the duty, roles and comment you want.");
            TextDisabledWrapped("3. Click \"Save current setup\" above.");
            return;
        }

        RecruitmentPreset? toDelete = null;
        RecruitmentPreset? toDuplicate = null;

        for (var i = 0; i < config.Presets.Count; i++)
        {
            if (i > 0)
                ImGui.Separator();
            DrawPreset(config.Presets[i], ref toDelete, ref toDuplicate);
        }

        if (toDuplicate != null)
        {
            var copy = toDuplicate.Clone();
            copy.Name = toDuplicate.Name + " (copy)";
            config.Presets.Insert(config.Presets.IndexOf(toDuplicate) + 1, copy);
            config.Save();
            editingId = copy.Id;
        }

        if (toDelete != null)
        {
            if (config.ActivePresetId == toDelete.Id)
                scheduler.Stop();
            config.Presets.Remove(toDelete);
            config.Save();
            chat.Print($"[PFR] Deleted preset '{toDelete.Name}'.");
        }
    }

    private void DrawPreset(RecruitmentPreset preset, ref RecruitmentPreset? toDelete, ref RecruitmentPreset? toDuplicate)
    {
        using var id = ImRaii.PushId(preset.Id.ToString());
        var isActive = config.ActivePresetId == preset.Id;
        var editing = editingId == preset.Id;

        // Row 1: name on the left, Deploy + edit toggle on the right.
        ImGui.AlignTextToFramePadding();
        if (isActive)
        {
            DrawIcon(FontAwesomeIcon.BroadcastTower, ColorGreen);
            ImGui.SameLine();
            ImGui.TextColored(ColorGreen, preset.Name);
            Tooltip("This preset is being kept alive.");
        }
        else
        {
            ImGui.TextUnformatted(preset.Name);
        }

        var deployLabel = isActive ? "Re-post" : "Deploy";
        var editIcon = editing ? FontAwesomeIcon.ChevronUp : FontAwesomeIcon.Pen;
        SameLineRightAligned(ImGuiComponents.GetIconButtonWithTextWidth(FontAwesomeIcon.Play, deployLabel)
                             + ImGui.GetStyle().ItemSpacing.X + IconButtonWidth(editIcon));

        using (ImRaii.Disabled(!pfm.Available || pfm.Busy))
        {
            if (ImGuiComponents.IconButtonWithText(FontAwesomeIcon.Play, deployLabel))
                DeployPreset(preset);
        }
        Tooltip(!pfm.Available ? "Party Finder functions are unavailable on this game version."
            : pfm.Busy ? "Waiting for the current re-post to finish."
            : isActive ? "Re-post this listing now and restart the timer."
            : "Post this preset to the Party Finder.");

        ImGui.SameLine();
        if (ImGuiComponents.IconButton(editIcon))
            editingId = editing ? null : preset.Id;
        Tooltip(editing ? "Close editor" : "Edit, duplicate or delete");

        // Row 2+: what the listing is.
        ImGui.TextDisabled(duties.Describe(preset));
        DrawSlots(preset);
        if (!string.IsNullOrWhiteSpace(preset.Comment))
            TextDisabledWrapped($"\"{preset.Comment}\"");

        if (editing)
            DrawEditor(preset, ref toDelete, ref toDuplicate);
    }

    private void DrawSlots(RecruitmentPreset preset)
    {
        var count = Math.Min(RefreshScheduler.PartySlots(preset), preset.SlotFlags.Length);
        if (count == 0)
        {
            ImGui.TextDisabled("No job slots captured");
            return;
        }

        using (ImRaii.PushStyle(ImGuiStyleVar.ItemSpacing, new Vector2(8 * ImGuiHelpers.GlobalScale, ImGui.GetStyle().ItemSpacing.Y)))
        {
            for (var i = 0; i < count; i++)
            {
                var slot = jobMasks.Describe(preset.SlotFlags[i]);
                if (i > 0)
                    ImGui.SameLine();
                ImGui.TextColored(SlotColor(slot.Kind), slot.Label);
                Tooltip($"Slot {i + 1}\n{slot.Tooltip}");
            }
        }

        if (preset.NumberOfGroups > 1)
        {
            ImGui.SameLine();
            ImGui.TextDisabled($"x{preset.NumberOfGroups} parties");
        }
    }

    private void DrawEditor(RecruitmentPreset preset, ref RecruitmentPreset? toDelete, ref RecruitmentPreset? toDuplicate)
    {
        ImGuiHelpers.ScaledDummy(2);
        using var indent = ImRaii.PushIndent();

        var name = preset.Name;
        ImGui.SetNextItemWidth(-1);
        if (ImGui.InputTextWithHint("##name", "Preset name", ref name, NameMaxLength) && !string.IsNullOrWhiteSpace(name))
            preset.Name = name;
        if (ImGui.IsItemDeactivatedAfterEdit())
            config.Save();

        var comment = preset.Comment;
        if (ImGui.InputTextMultiline("##comment", ref comment, CommentMaxLength,
                                     new Vector2(-1, ImGui.GetTextLineHeight() * 3.5f)))
            preset.Comment = comment;
        if (ImGui.IsItemDeactivatedAfterEdit())
            config.Save();
        if (string.IsNullOrEmpty(preset.Comment) && !ImGui.IsItemActive())
        {
            // InputTextMultiline has no hint; draw one over the empty box.
            var min = ImGui.GetItemRectMin() + ImGui.GetStyle().FramePadding;
            ImGui.GetWindowDrawList().AddText(min, ImGui.GetColorU32(ImGuiCol.TextDisabled), "Listing comment");
        }

        var ctrl = ImGui.GetIO().KeyCtrl;

        using (ImRaii.Disabled(!ctrl || !pfm.HasRecruitmentData))
        {
            if (ImGuiComponents.IconButtonWithText(FontAwesomeIcon.FileImport, "Update from game"))
                UpdateFromGame(preset);
        }
        Tooltip(!pfm.HasRecruitmentData
            ? "Open the Party Finder and set up a recruitment first."
            : "Hold Ctrl and click to replace this preset's settings with your\ncurrent in-game recruitment setup. The name is kept.");

        ImGui.SameLine();
        if (ImGuiComponents.IconButtonWithText(FontAwesomeIcon.Copy, "Duplicate"))
            toDuplicate = preset;

        ImGui.SameLine();
        using (ImRaii.Disabled(!ctrl))
        {
            using (ImRaii.PushColor(ImGuiCol.Text, ColorRed, ctrl))
            {
                if (ImGuiComponents.IconButtonWithText(FontAwesomeIcon.Trash, "Delete"))
                    toDelete = preset;
            }
        }
        Tooltip("Hold Ctrl and click to delete this preset.");

        ImGuiHelpers.ScaledDummy(2);
    }

    // ---------------------------------------------------------------- actions

    private RecruitmentPreset? ActivePreset() =>
        config.ActivePresetId is { } id ? config.Presets.FirstOrDefault(p => p.Id == id) : null;

    private void DeployPreset(RecruitmentPreset preset)
    {
        if (!pfm.Deploy(preset))
        {
            chat.PrintError($"[PFR] Failed to deploy '{preset.Name}'. See /xllog for details.");
            return;
        }

        scheduler.MarkActive(preset);
        var followUp = !config.AutoRenewEnabled ? "Automation is off, so it won't be renewed."
            : config.StopWhenFull ? "Keeping it alive until the party fills."
            : "Keeping it alive.";
        chat.Print($"[PFR] Deployed '{preset.Name}'. {followUp}");
    }

    private void SaveNewPreset(string name)
    {
        try
        {
            var preset = pfm.Capture(name);
            config.Presets.Add(preset);
            config.Save();
            chat.Print($"[PFR] Saved preset '{preset.Name}'.");
        }
        catch (Exception ex)
        {
            chat.PrintError($"[PFR] Save failed: {ex.Message}");
        }
    }

    private void UpdateFromGame(RecruitmentPreset preset)
    {
        try
        {
            preset.CopySettingsFrom(pfm.Capture(preset.Name));
            config.Save();
            chat.Print($"[PFR] Updated '{preset.Name}' from your current Party Finder setup.");
        }
        catch (Exception ex)
        {
            chat.PrintError($"[PFR] Update failed: {ex.Message}");
        }
    }

    /// <summary>Default name for a new preset: the duty being recruited for, made unique.</summary>
    private string SuggestedName()
    {
        string baseName;
        try
        {
            var live = pfm.Capture(string.Empty);
            baseName = duties.Duty(live.Category, live.DutyId) ?? DutyNames.Category(live.Category);
        }
        catch
        {
            return string.Empty;
        }

        var name = baseName;
        for (var n = 2; config.Presets.Any(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase)); n++)
            name = $"{baseName} ({n})";
        return name;
    }

    // ---------------------------------------------------------------- helpers

    private static uint SlotColor(JobMasks.SlotKind kind) => kind switch
    {
        JobMasks.SlotKind.Tank => ColorTank,
        JobMasks.SlotKind.Healer => ColorHealer,
        JobMasks.SlotKind.Dps => ColorDps,
        JobMasks.SlotKind.Mixed => ColorYellow,
        JobMasks.SlotKind.Empty => ImGui.GetColorU32(ImGuiCol.TextDisabled),
        _ => ImGui.GetColorU32(ImGuiCol.Text),
    };

    private static void DrawIcon(FontAwesomeIcon icon, uint color)
    {
        using (ImRaii.PushFont(UiBuilder.IconFont))
            ImGui.TextColored(color, icon.ToIconString());
    }

    private static float IconButtonWidth(FontAwesomeIcon icon)
    {
        using (ImRaii.PushFont(UiBuilder.IconFont))
            return ImGui.CalcTextSize(icon.ToIconString()).X + ImGui.GetStyle().FramePadding.X * 2;
    }

    /// <summary>Continue the current line, pushed right so an item of <paramref name="width"/> ends at the edge.</summary>
    private static void SameLineRightAligned(float width)
    {
        ImGui.SameLine();
        var free = ImGui.GetContentRegionAvail().X - width;
        if (free > 0)
            ImGui.SetCursorPosX(ImGui.GetCursorPosX() + free);
    }

    /// <summary>Tooltip for the last item, shown even when it's disabled.</summary>
    private static void Tooltip(string text)
    {
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(text);
    }

    private static void TextDisabledWrapped(string text)
    {
        using (ImRaii.PushColor(ImGuiCol.Text, ImGui.GetColorU32(ImGuiCol.TextDisabled)))
            ImGui.TextWrapped(text);
    }

    private static string FormatDuration(double seconds)
    {
        var ts = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return ts.TotalMinutes >= 1 ? $"{(int)ts.TotalMinutes}m {ts.Seconds:00}s" : $"{ts.Seconds}s";
    }

    private static string Ago(DateTime utc)
    {
        var elapsed = DateTime.UtcNow - utc;
        return elapsed.TotalMinutes < 1 ? "just now"
            : elapsed.TotalHours < 1 ? $"{(int)elapsed.TotalMinutes}m ago"
            : $"{(int)elapsed.TotalHours}h ago";
    }
}
