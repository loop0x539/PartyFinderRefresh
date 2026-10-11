using System;
using System.Collections.Generic;
using Dalamud.Configuration;
using Dalamud.Plugin;
using PartyFinderRefresh.Models;

namespace PartyFinderRefresh;

[Serializable]
public class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 1;

    /// <summary>Saved recruitment presets.</summary>
    public List<RecruitmentPreset> Presets { get; set; } = new();

    /// <summary>Re-recruit this many minutes after deploy/renew (game listings hard-expire at 60).</summary>
    public int RenewIntervalMinutes { get; set; } = 55;

    /// <summary>Master switch: watch the deployed listing (renew, bump, leaver fix, stop when full).</summary>
    public bool AutoRenewEnabled { get; set; } = true;

    /// <summary>Also bump the listing on a short interval to keep it near the top of the search list.</summary>
    public bool EnableBumpRefresh { get; set; } = false;

    public int BumpIntervalSeconds { get; set; } = 90;

    /// <summary>Skip refreshes while the PF detail/edit window is open, to avoid fighting the UI.</summary>
    public bool PauseWhileDetailOpen { get; set; } = true;

    /// <summary>Stop keeping the listing alive once the party is full (nothing left to recruit).</summary>
    public bool StopWhenFull { get; set; } = true;

    /// <summary>Re-apply the active listing when a party member leaves (reopens the slot + job mask).</summary>
    public bool FixOnMemberLeave { get; set; } = true;

    /// <summary>When fixing a leaver, use the game's Recommended Roles button instead of replaying preset slot masks.</summary>
    public bool UseRecommendedRolesOnLeave { get; set; } = false;

    /// <summary>Id of the preset currently being kept alive (null = none active).</summary>
    public Guid? ActivePresetId { get; set; }

    // Not serialized — wired up at load.
    [NonSerialized] private IDalamudPluginInterface? pluginInterface;

    public void Initialize(IDalamudPluginInterface pi) => this.pluginInterface = pi;

    public void Save() => this.pluginInterface?.SavePluginConfig(this);
}
