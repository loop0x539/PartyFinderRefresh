using System;
using System.Collections.Generic;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.Game.Group;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Client.UI.Info;
using FFXIVClientStructs.FFXIV.Component.GUI;
using PartyFinderRefresh.Models;

namespace PartyFinderRefresh.Game;

/// <summary>
/// All interaction with the game's Party Finder. Reads/writes the verified
/// <c>AgentLookingForGroup.StoredRecruitmentInfo</c> struct and posts recruitment via native
/// functions resolved by signature.
/// </summary>
public sealed unsafe class PartyFinderManager : IDisposable
{
    private const string SigStartRecruiting = "E8 ?? ?? ?? ?? 48 8B CD E8 ?? ?? ?? ?? 33 DB";
    private const string SigLeaveDuty = "E8 ?? ?? ?? ?? 84 C0 0F 84 ?? ?? ?? ?? 41 83 8F";
    private const string SigUpdateListings = "E8 ?? ?? ?? ?? EB ?? 8B 9E ?? ?? ?? ?? 48 8D 8D";
    private const string SigGetActiveRecruiterContentId = "E8 ?? ?? ?? ?? 48 85 C0 75 ?? F6 83";

    // Delay after re-posting before restoring the preset's slot roles (the re-recruit resets them).
    private const int RoleRestoreDelayMs = 2000;

    // LeaveDuty operates on the cross-world party object whose pointer lives at this agent offset.
    private const int LeaveDutyPointerOffset = 0x1C;
    // Wait after leaving the party before re-posting, so the server registers the leave first.
    private const int ReListDelayMs = 3000;

    // The native post/update functions only register while the LFG agent/addon is active AND fully started
    // up — which takes a few seconds after Show()ing a closed Party Finder. So after opening it we wait at
    // least a minimum settle time AND until the addon reports ready before posting (giving up at the timeout).
    private const int AgentReadyPollMs = 150;
    private const int AgentStartupMinDelayMs = 2500;
    private const int AgentReadyTimeoutMs = 6000;

    private readonly IFramework framework;
    private readonly IPluginLog log;
    private readonly IGameGui gameGui;
    private readonly IPlayerState playerState;

    // OnlineStatus Excel row id for "Recruiting Party Members" (0 if not resolved).
    private readonly byte recruitingStatusId;

    // long StartRecruiting(AgentLookingForGroup* agent)  -> 0 == success
    private readonly delegate* unmanaged<nint, long> startRecruiting;
    // void LeaveDuty(void* crossRealmPartyObject, byte skipCrossRealmCheck)
    private readonly delegate* unmanaged<nint, byte, void> leaveDuty;
    private readonly bool hasLeave;
    // void UpdatePartyFinderListings(AgentLookingForGroup* agent, byte unk, byte preserveSelection)
    private readonly delegate* unmanaged<nint, byte, byte, void> updateListings;
    private readonly bool hasUpdateListings;
    // ulong GetActiveRecruiterContentId(AgentLookingForGroup* agent) -> content id of the current recruiter
    private readonly delegate* unmanaged<nint, ulong> getActiveRecruiterContentId;
    private readonly bool hasRecruiterCheck;

    // Time-delayed actions executed on the framework thread.
    private readonly List<(DateTime due, Action action)> pending = new();

    public bool Available { get; }
    public string? UnavailableReason { get; }

    /// <summary>True while a leave -> re-post sequence is in flight (listing briefly down).</summary>
    public bool Busy => pending.Count > 0;

    public PartyFinderManager(ISigScanner sigScanner, IFramework framework, IPluginLog log,
                              IGameGui gameGui, IPlayerState playerState, IDataManager dataManager)
    {
        this.framework = framework;
        this.log = log;
        this.gameGui = gameGui;
        this.playerState = playerState;

        // Resolve the "Recruiting Party Members" online-status id by name.
        try
        {
            foreach (var row in dataManager.GetExcelSheet<Lumina.Excel.Sheets.OnlineStatus>())
            {
                if (row.Name.ExtractText().Contains("Recruiting Party", StringComparison.OrdinalIgnoreCase))
                {
                    recruitingStatusId = (byte)row.RowId;
                    break;
                }
            }
            log.Information("[PFR] Recruiting online-status id = {Id}", recruitingStatusId);
        }
        catch (Exception ex)
        {
            log.Warning(ex, "[PFR] Couldn't resolve the recruiting online-status id.");
        }

        try
        {
            startRecruiting = (delegate* unmanaged<nint, long>)Resolve(sigScanner, SigStartRecruiting, nameof(SigStartRecruiting));
            Available = true;
            log.Information("[PFR] Party Finder native functions resolved successfully.");
        }
        catch (Exception ex)
        {
            Available = false;
            UnavailableReason =
                "Couldn't resolve the Party Finder game functions for this game version. " +
                "Deploy/refresh are disabled until the signatures are updated for the current patch.";
            log.Error(ex, "[PFR] Failed to resolve Party Finder signatures — native operations disabled.");
        }

        try
        {
            getActiveRecruiterContentId = (delegate* unmanaged<nint, ulong>)Resolve(sigScanner, SigGetActiveRecruiterContentId, nameof(SigGetActiveRecruiterContentId));
            hasRecruiterCheck = true;
        }
        catch (Exception ex)
        {
            hasRecruiterCheck = false;
            log.Warning(ex, "[PFR] GetActiveRecruiterContentId sig failed; falling back to OwnListingId for recruiting detection.");
        }

        try
        {
            leaveDuty = (delegate* unmanaged<nint, byte, void>)Resolve(sigScanner, SigLeaveDuty, nameof(SigLeaveDuty));
            hasLeave = true;
        }
        catch (Exception ex)
        {
            hasLeave = false;
            log.Warning(ex, "[PFR] LeaveDuty sig failed; auto-refresh/leaver re-post disabled.");
        }

        try
        {
            updateListings = (delegate* unmanaged<nint, byte, byte, void>)Resolve(sigScanner, SigUpdateListings, nameof(SigUpdateListings));
            hasUpdateListings = true;
        }
        catch (Exception ex)
        {
            hasUpdateListings = false;
            log.Warning(ex, "[PFR] UpdatePartyFinderListings sig failed; role-restore commit disabled.");
        }

        this.framework.Update += OnFrameworkUpdate;
    }

    /// <summary>
    /// True while the local player has an active recruitment.
    /// </summary>
    public bool IsRecruiting
    {
        get
        {
            var agent = AgentLookingForGroup.Instance();
            if (agent == null)
                return false;

            if (agent->OwnListingId != 0)
                return true;

            if (hasRecruiterCheck)
            {
                var me = playerState.ContentId;
                if (me != 0 && getActiveRecruiterContentId((nint)agent) == me)
                    return true;
            }

            if (recruitingStatusId != 0)
            {
                var player = Control.GetLocalPlayer();
                if (player != null && ((Character*)player)->OnlineStatus == recruitingStatusId)
                    return true;
            }

            return false;
        }
    }

    /// <summary>Game-reported seconds remaining for our own listing, if it's the last one viewed.</summary>
    public uint? GameTimeLeftSeconds
    {
        get
        {
            var agent = AgentLookingForGroup.Instance();
            if (agent == null || agent->OwnListingId == 0)
                return null;
            ref var last = ref agent->LastViewedListing;
            return last.ListingId != 0 && last.ListingId == agent->OwnListingId ? last.TimeLeft : null;
        }
    }

    /// <summary>True if the in-game recruitment condition or detail window is open.</summary>
    public bool IsDetailWindowOpen =>
        gameGui.GetAddonByName("LookingForGroupCondition") != nint.Zero ||
        gameGui.GetAddonByName("LookingForGroupDetail") != nint.Zero;

    /// <summary>Cross-realm party member count. 0 if unavailable.</summary>
    public int CrossRealmPartyCount
    {
        get { try { return InfoProxyCrossRealm.GetPartyMemberCount(); } catch { return 0; } }
    }

    /// <summary>Local/in-instance party member count from GroupManager. 0 if unavailable.</summary>
    public int LocalPartyCount
    {
        get
        {
            try
            {
                var gm = GroupManager.Instance();
                return gm != null ? gm->MainGroup.MemberCount : 0;
            }
            catch { return 0; }
        }
    }

    /// <summary>True if the game reports we're in a party by any signal (cross-realm or local).</summary>
    public bool InAnyParty
    {
        get
        {
            try { if (InfoProxyCrossRealm.IsLocalPlayerInParty()) return true; } catch { /* not cross-realm */ }
            return LocalPartyCount > 1 || CrossRealmPartyCount > 1;
        }
    }

    /// <summary>True if the in-game Recruitment Criteria editor is currently open.</summary>
    public bool ConditionAddonOpen => gameGui.GetAddonByName("LookingForGroupCondition") != nint.Zero;

    /// <summary>True if the live recruitment struct currently has a duty/category set.</summary>
    public bool HasRecruitmentData
    {
        get
        {
            var agent = AgentLookingForGroup.Instance();
            return agent != null &&
                   ((uint)agent->StoredRecruitmentInfo.SelectedCategory != 0 || agent->StoredRecruitmentInfo.SelectedDutyId != 0);
        }
    }

    /// <summary>
    /// Snapshot the current recruitment setup into a new preset.
    /// </summary>
    public RecruitmentPreset Capture(string name)
    {
        var agent = AgentLookingForGroup.Instance();
        if (agent == null)
            throw new InvalidOperationException("Party Finder agent is not available.");

        ref var info = ref agent->StoredRecruitmentInfo;
        return new RecruitmentPreset
        {
            Name = name,
            Category = (uint)info.SelectedCategory,
            DutyId = info.SelectedDutyId,
            Objective = (byte)info.Objective,
            BeginnerFriendly = info.BeginnerFriendly,
            CompletionStatus = (byte)info.CompletionStatus,
            DutyFinderSettingFlags = (byte)info.DutyFinderSettingFlags,
            LootRule = (byte)info.LootRule,
            Password = info.Password,
            LanguageFlags = (byte)info.LanguageFlags,
            NumberOfSlotsInMainParty = info.NumberOfSlotsInMainParty,
            LimitRecruitingToWorld = info.LimitRecruitingToWorld,
            OnePlayerPerJob = info.OnePlayerPerJob,
            NumberOfGroups = info.NumberOfGroups,
            SlotFlags = info.SlotFlags.ToArray(),
            Comment = info.CommentString,
        };
    }

    /// <summary>
    /// Post (or re-post) a preset's listing. If already recruiting it ends the current listing and
    /// re-posts a fresh one (new 60m timer, reopened slot); otherwise it posts cold. Either way the PF
    /// agent is opened first if needed, because the native post/update functions only register while it
    /// is active — and closed again afterward if we were the ones who opened it.
    /// </summary>
    public bool Deploy(RecruitmentPreset preset, string context = "deploy")
    {
        if (!Available)
        {
            log.Warning("[PFR] {Context} requested but native functions are unavailable.", context);
            return false;
        }

        var agent = AgentLookingForGroup.Instance();
        if (agent == null)
        {
            log.Warning("[PFR] {Context} aborted: agent unavailable.", context);
            return false;
        }

        if (IsRecruiting)
        {
            // For cross-world parties, tear down the listing headlessly and re-post a clean one
            if (hasLeave && TryLeaveDuty((nint)agent))
            {
                log.Information("[PFR] {Context}: ended cross-world recruitment; re-posting '{Name}' in {Delay}ms.", context, preset.Name, ReListDelayMs);
                Schedule(ReListDelayMs, () => RunListingAction(preset, context, repost: true));
            }
            else
            {
                // Solo or local same-world party listings cannot be cancelled headlessly.
                // Instead of trying to re-post (which the game rejects), update the existing live listing in-place!
                log.Information("[PFR] {Context}: updating active local/solo recruitment for '{Name}' in-place.", context, preset.Name);
                RunListingAction(preset, context, repost: false);
            }
        }
        else
        {
            // Posting completely cold (e.g., party just filled up and broke, now a member left)
            RunListingAction(preset, context, repost: true);
        }

        return true;
    }

    private void RunListingAction(RecruitmentPreset preset, string context, bool repost)
    {
        var agent = AgentLookingForGroup.Instance();
        if (agent == null)
            return;
        
        if (!agent->IsAgentActive())
        {
            agent->Show();
            log.Information("[PFR] {Context}: PF agent was closed; opening main board.", context);
        }

        // Stage 1: Wait for the main Party Finder board to be initialized
        WhenAgentReady(context, 1000, () =>
        {
            var a = AgentLookingForGroup.Instance();
            if (a == null) return;

            if (repost)
            {
                // Stage 2: If the recruitment sub-window isn't open, open it via standard UI callback
                var lfgAddon = gameGui.GetAddonByName("LookingForGroup");
                if (gameGui.GetAddonByName("LookingForGroupCondition").Address == nint.Zero)
                {
                    if (lfgAddon.Address != nint.Zero)
                    {
                        log.Information("[PFR] Opening recruitment configuration window via UI callback.");
                        // FIXED: Access .Address property before casting to unmanaged pointer
                        ((AtkUnitBase*)lfgAddon.Address)->FireCallbackInt(2); // Simulates clicking "Recruit Members"
                    }
                }

                // Stage 3: Wait until the recruitment menu is fully open before applying data
                WaitForConditionWindow(() =>
                {
                    var a2 = AgentLookingForGroup.Instance();
                    if (a2 == null) return;

                    log.Information("[PFR] Recruitment setup menu active. Applying preset '{Name}'.", preset.Name);
                    Apply(preset, a2);

                    // Stage 4: Submit the recruitment listing
                    var r = startRecruiting((nint)a2);
                    if (r != 0)
                    {
                        log.Warning("[PFR] startRecruiting returned {Result}. Falling back to automated UI click.", r);
                        var condAddon = gameGui.GetAddonByName("LookingForGroupCondition");
                        if (condAddon.Address != nint.Zero)
                        {
                            // FIXED: Access .Address property before casting to unmanaged pointer
                            ((AtkUnitBase*)condAddon.Address)->FireCallbackInt(0); // Falls back to clicking the "Recruit" button
                        }
                    }
                    else
                    {
                        log.Information("[PFR] Successfully posted preset '{Name}' via native call.", preset.Name);
                    }

                    // Stage 5: Restore custom job roles/slot flags after server registration completes
                    Schedule(RoleRestoreDelayMs, () =>
                    {
                        var a3 = AgentLookingForGroup.Instance();
                        if (a3 == null) return;
                        ApplySlotRoles(preset, a3);
                        if (hasUpdateListings)
                            updateListings((nint)a3, 1, 1);
                        
                        log.Information("[PFR] Restored custom preset slots and closing editing window.");
                        
                        // Clean up the UI by auto-closing the configuration sub-window
                        var finalAddon = gameGui.GetAddonByName("LookingForGroupCondition");
                        if (finalAddon.Address != nint.Zero)
                        {
                            // FIXED: Access .Address property before casting to unmanaged pointer
                            ((AtkUnitBase*)finalAddon.Address)->FireCallbackInt(1); // Simulates clicking "Cancel/Close"
                        }
                    });
                });
            }
            else
            {
                // In-place live update (e.g. routine refreshing/bumps): Can process immediately
                log.Information("[PFR] Applying live in-place update to active listing.");
                Apply(preset, a);
                if (hasUpdateListings)
                    updateListings((nint)a, 1, 1);
            }
        });
    }

    /// <summary>
    /// Helper to poll framework frames until the recruitment addon is present in memory.
    /// </summary>
    private void WaitForConditionWindow(Action onOpen)
    {
        var start = DateTime.UtcNow;
        var deadline = start.AddMilliseconds(4000); // 4-second safety boundary

        void CheckCondition()
        {
            if (gameGui.GetAddonByName("LookingForGroupCondition").Address != nint.Zero)
            {
                onOpen();
                return;
            }

            if (DateTime.UtcNow >= deadline)
            {
                log.Warning("[PFR] Timed out waiting for LookingForGroupCondition. Forcing application execution branch.");
                onOpen();
                return;
            }

            Schedule(100, CheckCondition);
        }

        Schedule(100, CheckCondition);
    }

    /// <summary>
    /// Poll the framework thread until at least <paramref name="minDelayMs"/> has elapsed AND the LFG
    /// agent/addon is active and ready, then run <paramref name="onReady"/>. The minimum delay covers the
    /// few seconds a freshly opened Party Finder needs before it is postable; we run anyway at the timeout.
    /// </summary>
    private void WhenAgentReady(string context, int minDelayMs, Action onReady)
    {
        var start = DateTime.UtcNow;
        var minUntil = start.AddMilliseconds(minDelayMs);
        var deadline = start.AddMilliseconds(Math.Max(minDelayMs, AgentReadyTimeoutMs));

        void Check()
        {
            var now = DateTime.UtcNow;

            // Never post before the minimum settle time, even if the addon claims it is already ready.
            if (now >= minUntil)
            {
                var a = AgentLookingForGroup.Instance();
                var ready = a != null && a->IsAgentActive() && a->IsAddonReady();
                if (ready || now >= deadline)
                {
                    if (!ready)
                        log.Warning("[PFR] {Context}: PF addon still not ready after {Timeout}ms; posting anyway.", context, AgentReadyTimeoutMs);
                    onReady();
                    return;
                }
            }

            Schedule(AgentReadyPollMs, Check); // re-check next interval
        }

        Schedule(AgentReadyPollMs, Check);
    }

    private bool TryLeaveDuty(nint agent)
    {
        var sub = *(nint*)(agent + LeaveDutyPointerOffset);
        if (sub <= 0x10000)
            return false; // no cross-world party to leave (recruiting solo)
        leaveDuty(sub, 0);
        return true;
    }

    /// <summary>
    /// Headlessly replicate the manual flow: Open PF → open own listing → Edit → Recommended Roles → Apply.
    /// This is the leaver-recovery path: no re-post, no timer reset, just fixes the open slot's role mask
    /// to whatever the game's "Recommended Roles" button computes for the current duty/party comp.
    /// </summary>
    public bool ApplyRecommendedRoles(string context = "rec-roles")
    {
        var agent = AgentLookingForGroup.Instance();
        if (agent == null)
        {
            log.Warning("[PFR] {Context}: agent unavailable.", context);
            return false;
        }

        var myId = playerState.ContentId;
        if (myId == 0)
        {
            log.Warning("[PFR] {Context}: can't get local ContentId; aborting.", context);
            return false;
        }

        var weOpened = !agent->IsAgentActive();
        if (weOpened)
        {
            agent->Show();
            log.Information("[PFR] {Context}: PF board was closed; opening.", context);
        }

        WhenAgentReady(context, weOpened ? AgentStartupMinDelayMs : 500, () =>
        {
            var a = AgentLookingForGroup.Instance();
            if (a == null) return;

            // Step 1: navigate to own listing — this opens LookingForGroupDetail.
            log.Information("[PFR] {Context}: step 1 — OpenListingByContentId({Id}).", context, myId);
            a->OpenListingByContentId(myId);

            // Step 2: wait for LookingForGroupDetail to appear, then settle before firing Edit.
            WaitForAddon("LookingForGroupDetail", context, () =>
            {
                Schedule(600, () =>  // settle: addon is open but may not be interactive yet
                {
                    var detail = gameGui.GetAddonByName("LookingForGroupDetail");
                    if (detail.Address == nint.Zero)
                    {
                        log.Warning("[PFR] {Context}: LookingForGroupDetail gone after settle; aborting.", context);
                        if (weOpened) { var _a = AgentLookingForGroup.Instance(); if (_a != null) _a->Hide(); }
                        return;
                    }

                    // Step 3: fire Edit (id 0) on the detail popup.
                    log.Information("[PFR] {Context}: step 2 — Edit (FireCallbackInt 0) on LookingForGroupDetail.", context);
                    ((AtkUnitBase*)detail.Address)->FireCallbackInt(0);

                    // Step 4: wait for LookingForGroupCondition to appear, then settle.
                    WaitForAddon("LookingForGroupCondition", context, () =>
                    {
                        Schedule(800, () =>  // criteria window needs extra time to fully load duty data
                        {
                            var cond = gameGui.GetAddonByName("LookingForGroupCondition");
                            if (cond.Address == nint.Zero)
                            {
                                log.Warning("[PFR] {Context}: LookingForGroupCondition gone after settle; aborting.", context);
                                if (weOpened) { var _a = AgentLookingForGroup.Instance(); if (_a != null) _a->Hide(); }
                                return;
                            }
                            // Step 5: click Recommended Roles — must use ReceiveEvent(ButtonClick) not FireCallbackInt.
                            // FireCallbackInt sends a Change/Lua event; this button is a native C++ handler that
                            // only responds to the real ButtonClick event type (confirmed via event logger).
                            log.Information("[PFR] {Context}: step 3 — Recommended Roles (ButtonClick 18).", context);
                            FireButtonClick((AtkUnitBase*)cond.Address, 18);

                            // Step 6: settle so game writes slot flags, then Apply.
                            Schedule(700, () =>
                            {
                                var c = gameGui.GetAddonByName("LookingForGroupCondition");
                                if (c.Address == nint.Zero)
                                {
                                    log.Warning("[PFR] {Context}: condition window closed before Apply; aborting.", context);
                                    if (weOpened) { var _a = AgentLookingForGroup.Instance(); if (_a != null) _a->Hide(); }
                                    return;
                                }

                                // Step 7: Apply (id 0).
                                log.Information("[PFR] {Context}: step 4 — Apply (FireCallbackInt 0).", context);
                                ((AtkUnitBase*)c.Address)->FireCallbackInt(0);

                                // Step 8: if we opened the board, close it after Apply settles.
                                if (weOpened)
                                    Schedule(800, () => { var _a = AgentLookingForGroup.Instance(); if (_a != null) _a->Hide(); });
                                else
                                    log.Information("[PFR] {Context}: done — roles applied.", context);
                            });
                        });
                    });
                });
            });
        });

        return true;
    }

    /// <summary>Poll until a named addon is present in memory, then call <paramref name="onOpen"/>.</summary>
    private static void FireButtonClick(AtkUnitBase* addon, int param)
    {
        // ReceiveEvent(ButtonClick) is required for native C++ button handlers; FireCallbackInt
        // sends a Change/Lua event that those handlers don't respond to.
        var evt = stackalloc AtkEvent[1];
        System.Runtime.CompilerServices.Unsafe.InitBlockUnaligned(evt, 0, (uint)sizeof(AtkEvent));
        addon->ReceiveEvent(AtkEventType.ButtonClick, param, evt);
    }

    private void WaitForAddon(string addonName, string context, Action onOpen)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(4000);

        void Check()
        {
            if (gameGui.GetAddonByName(addonName).Address != nint.Zero)
            {
                onOpen();
                return;
            }
            if (DateTime.UtcNow >= deadline)
            {
                log.Warning("[PFR] {Context}: timed out waiting for {Addon}.", context, addonName);
                onOpen(); // run anyway — let inner code handle the null check
                return;
            }
            Schedule(150, Check);
        }

        Schedule(150, Check);
    }

    private static void Apply(RecruitmentPreset p, AgentLookingForGroup* agent)
    {
        ref var info = ref agent->StoredRecruitmentInfo;
        info.SelectedCategory = (AgentLookingForGroup.DutyCategory)p.Category;
        info.SelectedDutyId = p.DutyId;
        info.Objective = (AgentLookingForGroup.Objective)p.Objective;
        info.BeginnerFriendly = p.BeginnerFriendly;
        info.CompletionStatus = (AgentLookingForGroup.CompletionStatus)p.CompletionStatus;
        info.DutyFinderSettingFlags = (AgentLookingForGroup.DutyFinderSetting)p.DutyFinderSettingFlags;
        info.LootRule = (AgentLookingForGroup.LootRule)p.LootRule;
        info.Password = p.Password;
        info.LanguageFlags = (AgentLookingForGroup.Language)p.LanguageFlags;
        info.NumberOfSlotsInMainParty = p.NumberOfSlotsInMainParty;
        info.LimitRecruitingToWorld = p.LimitRecruitingToWorld;
        info.OnePlayerPerJob = p.OnePlayerPerJob;
        info.NumberOfGroups = p.NumberOfGroups;

        ApplySlotRoles(p, agent);

        info.CommentString = p.Comment;
    }

    /// <summary>Write just the per-slot job masks (roles) from the preset into the live recruitment.</summary>
    private static void ApplySlotRoles(RecruitmentPreset p, AgentLookingForGroup* agent)
    {
        var slots = agent->StoredRecruitmentInfo.SlotFlags;
        for (var i = 0; i < slots.Length && i < p.SlotFlags.Length; i++)
            slots[i] = p.SlotFlags[i];
    }

    private void Schedule(int delayMs, Action action) =>
        pending.Add((DateTime.UtcNow.AddMilliseconds(delayMs), action));

    private void OnFrameworkUpdate(IFramework fw)
    {
        if (pending.Count == 0)
            return;

        var now = DateTime.UtcNow;
        for (var i = pending.Count - 1; i >= 0; i--)
        {
            if (now < pending[i].due)
                continue;
            var action = pending[i].action;
            pending.RemoveAt(i);
            try { action(); }
            catch (Exception ex) { log.Error(ex, "[PFR] Scheduled Party Finder action failed."); }
        }
    }

    private nint Resolve(ISigScanner scanner, string signature, string name)
    {
        var addr = scanner.ScanText(signature);
        if (addr == nint.Zero)
            throw new InvalidOperationException($"{name} resolved to a null address.");

        log.Information("[PFR] Resolved {Name} -> 0x{Addr:X}", name, addr);
        return addr;
    }

    public void Dispose() => framework.Update -= OnFrameworkUpdate;
}