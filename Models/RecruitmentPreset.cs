using System;

namespace PartyFinderRefresh.Models;

/// <summary>
/// A serializable snapshot of <c>AgentLookingForGroup.RecruitmentSub</c>. Values are stored as raw
/// primitives (not FFXIVClientStructs enums) so saved presets survive CS enum renames across patches;
/// they are cast back to the game enums when applied. See <see cref="Game.PartyFinderManager"/> for
/// the capture/apply marshalling.
/// </summary>
[Serializable]
public class RecruitmentPreset
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "New preset";

    // RecruitmentSub fields (raw values).
    public uint Category { get; set; }            // DutyCategory
    public ushort DutyId { get; set; }
    public byte Objective { get; set; }
    public byte BeginnerFriendly { get; set; }
    public byte CompletionStatus { get; set; }
    public byte DutyFinderSettingFlags { get; set; }
    public byte LootRule { get; set; }
    public ushort Password { get; set; }
    public byte LanguageFlags { get; set; }
    public byte NumberOfSlotsInMainParty { get; set; }
    public byte LimitRecruitingToWorld { get; set; }
    public byte OnePlayerPerJob { get; set; }
    public byte NumberOfGroups { get; set; }

    /// <summary>Per-slot allowed-job bitmasks (index = party slot). Up to 48 for alliance content.</summary>
    public ulong[] SlotFlags { get; set; } = Array.Empty<ulong>();

    public string Comment { get; set; } = string.Empty;

    public RecruitmentPreset Clone()
    {
        var c = (RecruitmentPreset)MemberwiseClone();
        c.Id = Guid.NewGuid();
        c.SlotFlags = (ulong[])SlotFlags.Clone();
        return c;
    }

    /// <summary>Overwrite every recruitment setting with <paramref name="source"/>'s, keeping this preset's Id and Name.</summary>
    public void CopySettingsFrom(RecruitmentPreset source)
    {
        Category = source.Category;
        DutyId = source.DutyId;
        Objective = source.Objective;
        BeginnerFriendly = source.BeginnerFriendly;
        CompletionStatus = source.CompletionStatus;
        DutyFinderSettingFlags = source.DutyFinderSettingFlags;
        LootRule = source.LootRule;
        Password = source.Password;
        LanguageFlags = source.LanguageFlags;
        NumberOfSlotsInMainParty = source.NumberOfSlotsInMainParty;
        LimitRecruitingToWorld = source.LimitRecruitingToWorld;
        OnePlayerPerJob = source.OnePlayerPerJob;
        NumberOfGroups = source.NumberOfGroups;
        SlotFlags = (ulong[])source.SlotFlags.Clone();
        Comment = source.Comment;
    }
}
