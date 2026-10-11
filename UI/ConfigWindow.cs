using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Components;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;

namespace PartyFinderRefresh.UI;

public sealed class ConfigWindow : Window, IDisposable
{
    private const float SliderWidth = 180;

    private readonly Configuration config;
    private bool changed;

    public ConfigWindow(Configuration config)
        : base("Party Finder Refresh Settings###PartyFinderRefreshConfig", ImGuiWindowFlags.AlwaysAutoResize)
    {
        this.config = config;
    }

    public void Dispose() { }

    public override void Draw()
    {
        changed = false;

        var automation = config.AutoRenewEnabled;
        if (ImGui.Checkbox("Keep deployed listings alive", ref automation))
            Set(() => config.AutoRenewEnabled = automation);
        ImGuiComponents.HelpMarker(
            "After you deploy a preset, watch the listing: renew it before it expires, reopen\n" +
            "slots when people leave, and stop once the party is full.\n" +
            "Turn this off to only post listings.");

        using (ImRaii.Disabled(!config.AutoRenewEnabled))
        {
            Section("Renewing");
            DrawRenewing();

            Section("When the party changes");
            DrawPartyChanges();
        }

        if (changed)
            config.Save();
    }

    private void DrawRenewing()
    {
        var renew = config.RenewIntervalMinutes;
        ImGui.SetNextItemWidth(SliderWidth * ImGuiHelpers.GlobalScale);
        if (ImGui.SliderInt("Renew every", ref renew, 1, 59, "%d min", ImGuiSliderFlags.AlwaysClamp))
            Set(() => config.RenewIntervalMinutes = Math.Clamp(renew, 1, 59));
        ImGuiComponents.HelpMarker("Listings expire after 60 minutes. Re-posting a few minutes early keeps it up without a gap.");

        var bump = config.EnableBumpRefresh;
        if (ImGui.Checkbox("Bump to the top of the list", ref bump))
            Set(() => config.EnableBumpRefresh = bump);
        ImGuiComponents.HelpMarker(
            "Re-post on a short timer so the listing stays near the top of the search results.\n" +
            "Each bump re-posts the listing, so frequent bumping is more disruptive.");

        using (ImRaii.Disabled(!config.EnableBumpRefresh))
        using (ImRaii.PushIndent())
        {
            var bumpSec = config.BumpIntervalSeconds;
            ImGui.SetNextItemWidth(SliderWidth * ImGuiHelpers.GlobalScale);
            if (ImGui.SliderInt("Bump every", ref bumpSec, 15, 600, "%d sec", ImGuiSliderFlags.AlwaysClamp))
                Set(() => config.BumpIntervalSeconds = Math.Clamp(bumpSec, 15, 600));

            var pause = config.PauseWhileDetailOpen;
            if (ImGui.Checkbox("Hold bumps while the Party Finder window is open", ref pause))
                Set(() => config.PauseWhileDetailOpen = pause);
            ImGuiComponents.HelpMarker("Avoids re-posting while you're looking at or editing a listing.\nThe safety renew still happens.");
        }
    }

    private void DrawPartyChanges()
    {
        var stopFull = config.StopWhenFull;
        if (ImGui.Checkbox("Stop once the party is full", ref stopFull))
            Set(() => config.StopWhenFull = stopFull);
        ImGuiComponents.HelpMarker(
            "When every slot is filled, stop keeping the listing alive.\n" +
            "Turn off to stay armed and re-post if someone leaves the full party later.");

        var fixLeave = config.FixOnMemberLeave;
        if (ImGui.Checkbox("Reopen the slot when someone leaves", ref fixLeave))
            Set(() => config.FixOnMemberLeave = fixLeave);
        ImGuiComponents.HelpMarker(config.StopWhenFull
            ? "If someone leaves before the party fills, re-post so their slot\nreopens with the preset's job restrictions."
            : "If someone leaves, re-post so their slot reopens with the preset's\njob restrictions, even after the party filled and recruitment ended.");

        using (ImRaii.Disabled(!config.FixOnMemberLeave))
        using (ImRaii.PushIndent())
        {
            var recRoles = config.UseRecommendedRolesOnLeave;
            if (ImGui.Checkbox("Use the game's Recommended Roles", ref recRoles))
                Set(() => config.UseRecommendedRolesOnLeave = recRoles);
            ImGuiComponents.HelpMarker(
                "On: clicks Edit > Recommended Roles > Apply in-game (good for high-level duties).\n" +
                "Off: restores the preset's saved slot restrictions.");
        }
    }

    private static void Section(string title)
    {
        ImGuiHelpers.ScaledDummy(6);
        ImGui.TextDisabled(title);
        ImGui.Separator();
    }

    private void Set(Action apply)
    {
        apply();
        changed = true;
    }
}
