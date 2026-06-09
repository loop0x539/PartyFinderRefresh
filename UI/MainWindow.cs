using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using PartyFinderRefresh.Game;
using PartyFinderRefresh.Models;

namespace PartyFinderRefresh.UI;

public sealed class MainWindow : Window, IDisposable
{
    private const uint ColorRed = 0xFF3344FFu;
    private const uint ColorGreen = 0xFF44DD44u;
    private const uint ColorYellow = 0xFF44DDDDu;

    private readonly Configuration config;
    private readonly PartyFinderManager pfm;
    private readonly RefreshScheduler scheduler;
    private readonly IChatGui chat;
    private readonly Action openConfig;

    private string captureName = string.Empty;

    public MainWindow(Configuration config, PartyFinderManager pfm, RefreshScheduler scheduler,
                      IChatGui chat, Action openConfig)
        : base("Party Finder Refresh###PartyFinderRefreshMain")
    {
        this.config = config;
        this.pfm = pfm;
        this.scheduler = scheduler;
        this.chat = chat;
        this.openConfig = openConfig;

        Size = new Vector2(440, 520);
        SizeCondition = ImGuiCond.FirstUseEver;
    }

    public void Dispose() { }

    public override void Draw()
    {
        DrawStatus();
        ImGui.Separator();
        DrawCapture();
        ImGui.Separator();
        DrawPresetList();
    }

    private void DrawStatus()
    {
        if (!pfm.Available)
        {
            ImGui.TextColored(ColorRed, "Native Party Finder functions unavailable");
            ImGui.TextWrapped(pfm.UnavailableReason ?? "Deploy/refresh are disabled.");
            ImGui.TextDisabled("You can still capture and save presets.");
            return;
        }

        var recruiting = pfm.IsRecruiting;
        ImGui.Text("Status: ");
        ImGui.SameLine();
        if (recruiting)
            ImGui.TextColored(ColorGreen, "Recruiting");
        else
            ImGui.TextColored(ColorYellow, "Not recruiting");

        var active = config.ActivePresetId.HasValue
            ? config.Presets.FirstOrDefault(p => p.Id == config.ActivePresetId)
            : null;

        if (active != null)
        {
            var next = scheduler.SecondsUntilNextAction();
            var when = next.HasValue ? FormatDuration(next.Value) : "—";
            ImGui.Text($"Auto-renew active: {active.Name}");
            ImGui.Text($"Next refresh in: {when}");
            if (ImGui.Button("Stop auto-renew"))
                scheduler.Stop();
        }
        else
        {
            ImGui.TextDisabled("No preset is being kept alive.");
        }

        ImGui.SameLine(ImGui.GetWindowWidth() - 80);
        if (ImGui.Button("Settings"))
            openConfig();
    }

    private void DrawCapture()
    {
        ImGui.Text("Capture current setup");
        ImGui.SetNextItemWidth(220);
        ImGui.InputTextWithHint("##captureName", "Preset name", ref captureName, 64);
        ImGui.SameLine();

        var hasData = pfm.HasRecruitmentData;
        using (ImRaii.Disabled(!hasData || string.IsNullOrWhiteSpace(captureName)))
        {
            if (ImGui.Button("Capture"))
            {
                try
                {
                    var preset = pfm.Capture(captureName.Trim());
                    config.Presets.Add(preset);
                    config.Save();
                    chat.Print($"[PFR] Saved preset '{preset.Name}'.");
                    captureName = string.Empty;
                }
                catch (Exception ex)
                {
                    chat.PrintError($"[PFR] Capture failed: {ex.Message}");
                }
            }
        }

        ImGui.TextDisabled(hasData
            ? "Set up a recruitment in-game, type a name, then Capture."
            : "Open the Party Finder and configure a recruitment first, then Capture.");
    }

    private void DrawPresetList()
    {
        ImGui.Text($"Presets ({config.Presets.Count})");

        RecruitmentPreset? toDelete = null;
        RecruitmentPreset? toDuplicate = null;

        foreach (var preset in config.Presets)
        {
            ImGui.PushID(preset.Id.ToString());
            var isActive = config.ActivePresetId == preset.Id;

            if (ImGui.CollapsingHeader($"{(isActive ? "* " : string.Empty)}{preset.Name}###hdr"))
            {
                // Name
                var name = preset.Name;
                ImGui.SetNextItemWidth(240);
                if (ImGui.InputText("Name", ref name, 64))
                    preset.Name = name;
                if (ImGui.IsItemDeactivatedAfterEdit())
                    config.Save();

                // Summary
                var category = ((AgentLookingForGroup.DutyCategory)preset.Category).ToString();
                ImGui.TextDisabled($"{category}  ·  duty #{preset.DutyId}  ·  {JobMasks.Summarize(preset.SlotFlags)}");

                // Comment editor
                var comment = preset.Comment;
                if (ImGui.InputTextMultiline("Comment", ref comment, 192, new Vector2(-1, 60)))
                    preset.Comment = comment;
                if (ImGui.IsItemDeactivatedAfterEdit())
                    config.Save();

                // Actions
                using (ImRaii.Disabled(!pfm.Available))
                {
                    if (ImGui.Button("Deploy"))
                        DeployPreset(preset);
                }

                ImGui.SameLine();
                if (ImGui.Button("Duplicate"))
                    toDuplicate = preset;

                ImGui.SameLine();
                if (ImGui.Button("Delete"))
                    toDelete = preset;

                if (isActive)
                {
                    ImGui.SameLine();
                    ImGui.TextColored(ColorGreen, "keeping alive");
                }
            }

            ImGui.PopID();
        }

        if (toDuplicate != null)
        {
            var copy = toDuplicate.Clone();
            copy.Name = toDuplicate.Name + " (copy)";
            config.Presets.Add(copy);
            config.Save();
        }

        if (toDelete != null)
        {
            if (config.ActivePresetId == toDelete.Id)
                scheduler.Stop();
            config.Presets.Remove(toDelete);
            config.Save();
        }
    }

    private void DeployPreset(RecruitmentPreset preset)
    {
        if (pfm.Deploy(preset))
        {
            scheduler.MarkActive(preset);
            chat.Print($"[PFR] Deployed '{preset.Name}'. Auto-renew is on.");
        }
        else
        {
            chat.PrintError($"[PFR] Failed to deploy '{preset.Name}'. See /xllog for details.");
        }
    }

    private static string FormatDuration(double seconds)
    {
        var ts = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return ts.TotalMinutes >= 1 ? $"{(int)ts.TotalMinutes}m {ts.Seconds}s" : $"{ts.Seconds}s";
    }
}
