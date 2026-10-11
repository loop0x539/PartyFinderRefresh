using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Game.Gui.PartyFinder.Types;
using Dalamud.Plugin.Services;

namespace PartyFinderRefresh.Game;

/// <summary>
/// Decodes per-slot job restriction bitmasks for display. Presets capture and replay the raw masks
/// verbatim, so the plugin never needs to interpret individual job bits for correctness — this exists
/// only to render readable role chips in the UI. The bits use the same layout as Dalamud's
/// <see cref="JobFlags"/>, so jobs (and their roles) are resolved through it from the ClassJob sheet.
/// </summary>
public sealed class JobMasks
{
    public enum SlotKind { Empty, Tank, Healer, Dps, Mixed, Any }

    public readonly record struct Slot(SlotKind Kind, string Label, string Tooltip);

    private enum Role { Tank, Healer, Dps }

    // Indexed by bit position; null where the bit isn't a known combat job.
    private readonly (string Abbr, Role Role)?[] jobs = new (string, Role)?[64];

    public JobMasks(IDataManager data, IPluginLog log)
    {
        for (var bit = 0; bit < jobs.Length; bit++)
        {
            try
            {
                if (((JobFlags)(1UL << bit)).ClassJob(data) is not { } job)
                    continue;
                // ClassJob.Role: 1 = tank, 2 = melee, 3 = ranged, 4 = healer.
                Role? role = job.Role switch { 1 => Role.Tank, 4 => Role.Healer, 2 or 3 => Role.Dps, _ => null };
                if (role != null)
                    jobs[bit] = (job.Abbreviation.ExtractText(), role.Value);
            }
            catch (Exception ex)
            {
                log.Debug(ex, "[PFR] No ClassJob for job-mask bit {Bit}.", bit);
            }
        }
    }

    /// <summary>Describe one slot: a short role/job label plus a tooltip listing the allowed jobs.</summary>
    public Slot Describe(ulong mask)
    {
        var allowed = new List<(string Abbr, Role Role)>();
        for (var bit = 0; bit < jobs.Length; bit++)
        {
            if ((mask & (1UL << bit)) != 0 && jobs[bit] is { } job)
                allowed.Add(job);
        }

        if (allowed.Count == 0)
            return new Slot(SlotKind.Empty, "-", "No job restriction recorded");

        if (allowed.Count == 1)
        {
            var only = allowed[0];
            return new Slot(KindOf(only.Role), only.Abbr, $"{RoleName(only.Role)}: {only.Abbr} only");
        }

        var roles = allowed.Select(j => j.Role).Distinct().OrderBy(r => r).ToList();
        var tooltip = string.Join("\n", roles.Select(r =>
            $"{RoleName(r)}: {string.Join(" ", allowed.Where(j => j.Role == r).Select(j => j.Abbr))}"));

        if (roles.Count == 1)
            return new Slot(KindOf(roles[0]), RoleLetter(roles[0]), tooltip);
        if (roles.Count == 3)
            return new Slot(SlotKind.Any, "Any", tooltip);
        return new Slot(SlotKind.Mixed, string.Join("/", roles.Select(RoleLetter)), tooltip);
    }

    private static SlotKind KindOf(Role role) => role switch
    {
        Role.Tank => SlotKind.Tank,
        Role.Healer => SlotKind.Healer,
        _ => SlotKind.Dps,
    };

    private static string RoleName(Role role) => role switch
    {
        Role.Tank => "Tank",
        Role.Healer => "Healer",
        _ => "DPS",
    };

    private static string RoleLetter(Role role) => role switch
    {
        Role.Tank => "T",
        Role.Healer => "H",
        _ => "D",
    };
}
