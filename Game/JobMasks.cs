using System.Linq;

namespace PartyFinderRefresh.Game;

/// <summary>
/// Helpers for describing per-slot job restriction bitmasks. Presets capture and replay the raw
/// masks verbatim, so the plugin never needs to interpret individual job bits for correctness —
/// these helpers exist only to render a readable summary in the UI.
/// </summary>
public static class JobMasks
{
    /// <summary>A slot mask of 0 means "any job may apply".</summary>
    public static bool IsOpen(ulong mask) => mask == 0UL;

    /// <summary>Short human-readable summary of a slot list for the preset row.</summary>
    public static string Summarize(ulong[] slots)
    {
        if (slots.Length == 0)
            return "no slots captured";

        var used = slots.Count(s => s != 0UL || slots.Length <= 8); // alliance arrays are mostly 0-padded
        var restricted = slots.Count(s => !IsOpen(s));
        return restricted == 0
            ? $"{slots.Length} slots, all open"
            : $"{slots.Length} slots, {restricted} job-restricted";
    }
}
