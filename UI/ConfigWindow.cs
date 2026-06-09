using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;

namespace PartyFinderRefresh.UI;

public sealed class ConfigWindow : Window, IDisposable
{
    private readonly Configuration config;

    public ConfigWindow(Configuration config)
        : base("Party Finder Refresh — Settings###PartyFinderRefreshConfig")
    {
        this.config = config;
        Size = new Vector2(400, 280);
        SizeCondition = ImGuiCond.FirstUseEver;
    }

    public void Dispose() { }

    public override void Draw()
    {
        var changed = false;

        var autoRenew = config.AutoRenewEnabled;
        if (ImGui.Checkbox("Enable auto-renew", ref autoRenew))
        {
            config.AutoRenewEnabled = autoRenew;
            changed = true;
        }
        ImGui.TextDisabled("Keeps the deployed listing alive by re-recruiting before it expires.");

        var renew = config.RenewIntervalMinutes;
        ImGui.SetNextItemWidth(200);
        if (ImGui.SliderInt("Renew interval (min)", ref renew, 1, 59, "%d", ImGuiSliderFlags.AlwaysClamp))
        {
            config.RenewIntervalMinutes = Math.Clamp(renew, 1, 59);
            changed = true;
        }
        ImGui.TextDisabled("Listings hard-expire at 60 minutes; keep this below that.");

        ImGui.Separator();

        var bump = config.EnableBumpRefresh;
        if (ImGui.Checkbox("Periodic bump (keep listing near the top)", ref bump))
        {
            config.EnableBumpRefresh = bump;
            changed = true;
        }

        using (ImRaii.Disabled(!config.EnableBumpRefresh))
        {
            var bumpSec = config.BumpIntervalSeconds;
            ImGui.SetNextItemWidth(200);
            if (ImGui.SliderInt("Bump interval (sec)", ref bumpSec, 15, 600, "%d", ImGuiSliderFlags.AlwaysClamp))
            {
                config.BumpIntervalSeconds = Math.Clamp(bumpSec, 15, 600);
                changed = true;
            }
        }
        ImGui.TextDisabled("Re-recruits on this interval. Frequent bumping is more disruptive.");

        ImGui.Separator();

        var pause = config.PauseWhileDetailOpen;
        if (ImGui.Checkbox("Pause bumps while the PF window is open", ref pause))
        {
            config.PauseWhileDetailOpen = pause;
            changed = true;
        }

        ImGui.Separator();

        var fixLeave = config.FixOnMemberLeave;
        if (ImGui.Checkbox("Re-open listing when a member leaves", ref fixLeave))
        {
            config.FixOnMemberLeave = fixLeave;
            changed = true;
        }
        ImGui.TextDisabled("Reopens the vacated slot with the preset's job restriction — including re-deploying\nafter the party filled and ended recruitment, when someone then leaves.");

        using (ImRaii.Disabled(!config.FixOnMemberLeave))
        {
            var recRoles = config.UseRecommendedRolesOnLeave;
            if (ImGui.Checkbox("Use Recommended Roles on leaver fix", ref recRoles))
            {
                config.UseRecommendedRolesOnLeave = recRoles;
                changed = true;
            }
            ImGui.TextDisabled("ON: replays Edit -> Recommended Roles -> Apply in-game (for high-level duties).\nOFF: replays the preset's saved slot masks.");
        }

        if (changed)
            config.Save();
    }
}
