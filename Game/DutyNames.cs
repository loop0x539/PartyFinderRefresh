using System;
using System.Collections.Generic;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using Lumina.Excel.Sheets;
using PartyFinderRefresh.Models;

namespace PartyFinderRefresh.Game;

/// <summary>
/// Readable names for a preset's PF category and duty. For duty categories the stored duty id is a
/// ContentFinderCondition row (ContentRoulette for roulettes); other categories (FATEs, hunts, ...) have
/// no single duty, so only the category is shown.
/// </summary>
public sealed class DutyNames
{
    private readonly IDataManager data;
    private readonly IPluginLog log;
    private readonly Dictionary<(uint Category, ushort DutyId), string?> cache = new();

    public DutyNames(IDataManager data, IPluginLog log)
    {
        this.data = data;
        this.log = log;
    }

    /// <summary>"High-end Duty · The Epic of Alexander (Ultimate)" style one-liner.</summary>
    public string Describe(RecruitmentPreset preset)
    {
        var category = Category(preset.Category);
        var duty = Duty(preset.Category, preset.DutyId);
        return duty == null ? category : $"{category} · {duty}";
    }

    /// <summary>Just the duty's name, or null when the category has no specific duty.</summary>
    public string? Duty(uint category, ushort dutyId)
    {
        if (dutyId == 0)
            return null;
        if (cache.TryGetValue((category, dutyId), out var cached))
            return cached;

        string? name = null;
        try
        {
            name = (AgentLookingForGroup.DutyCategory)category switch
            {
                AgentLookingForGroup.DutyCategory.Roulette =>
                    data.GetExcelSheet<ContentRoulette>().GetRowOrDefault(dutyId)?.Name.ExtractText(),
                AgentLookingForGroup.DutyCategory.Dungeons or
                AgentLookingForGroup.DutyCategory.GuildQuests or
                AgentLookingForGroup.DutyCategory.Trials or
                AgentLookingForGroup.DutyCategory.Raids or
                AgentLookingForGroup.DutyCategory.HighEndDuty or
                AgentLookingForGroup.DutyCategory.PvP or
                AgentLookingForGroup.DutyCategory.DeepDungeons or
                AgentLookingForGroup.DutyCategory.FieldOperations or
                AgentLookingForGroup.DutyCategory.VCDungeonFinder =>
                    data.GetExcelSheet<ContentFinderCondition>().GetRowOrDefault(dutyId)?.Name.ExtractText(),
                _ => null,
            };
        }
        catch (Exception ex)
        {
            log.Debug(ex, "[PFR] Couldn't resolve duty {Id} in category {Category}.", dutyId, category);
        }

        // Sheet names are often lower-case ("the Epic of Alexander"); capitalize for display.
        name = string.IsNullOrWhiteSpace(name) ? null : char.ToUpperInvariant(name[0]) + name[1..];
        cache[(category, dutyId)] = name;
        return name;
    }

    public static string Category(uint category) => (AgentLookingForGroup.DutyCategory)category switch
    {
        AgentLookingForGroup.DutyCategory.None => "No category",
        AgentLookingForGroup.DutyCategory.Roulette => "Duty Roulette",
        AgentLookingForGroup.DutyCategory.Dungeons => "Dungeons",
        AgentLookingForGroup.DutyCategory.GuildQuests => "Guildhests",
        AgentLookingForGroup.DutyCategory.Trials => "Trials",
        AgentLookingForGroup.DutyCategory.Raids => "Raids",
        AgentLookingForGroup.DutyCategory.HighEndDuty => "High-end Duty",
        AgentLookingForGroup.DutyCategory.PvP => "PvP",
        AgentLookingForGroup.DutyCategory.GoldSaucer => "Gold Saucer",
        AgentLookingForGroup.DutyCategory.FATEs => "FATEs",
        AgentLookingForGroup.DutyCategory.TreasureHunts => "Treasure Hunt",
        AgentLookingForGroup.DutyCategory.TheHunt => "The Hunt",
        AgentLookingForGroup.DutyCategory.GatheringForays => "Gathering Forays",
        AgentLookingForGroup.DutyCategory.DeepDungeons => "Deep Dungeons",
        AgentLookingForGroup.DutyCategory.FieldOperations => "Field Operations",
        AgentLookingForGroup.DutyCategory.VCDungeonFinder => "V&C Dungeon Finder",
        var other => other.ToString(),
    };
}
