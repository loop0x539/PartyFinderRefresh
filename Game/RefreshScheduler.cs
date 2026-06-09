using System;
using System.Linq;
using Dalamud.Plugin.Services;
using PartyFinderRefresh.Models;

namespace PartyFinderRefresh.Game;

/// <summary>
/// Keeps the active preset's listing alive. Runs on every framework tick (throttled to ~1s) and,
/// while a preset is marked active and recruiting, re-recruits before the 60-minute hard expiry
/// (default 55 min). Optionally bumps the listing on a shorter interval to keep it near the top.
/// </summary>
public sealed class RefreshScheduler : IDisposable
{
    // Grace window after a (re)deploy before we trust IsRecruiting to decide the listing is gone.
    private const double StartupGraceSeconds = 15.0;
    // Require recruiting to read "off" continuously for this long before stopping, so a transient
    // blip (e.g. navigating PF windows) can never kill auto-renew.
    private const double EndedConfirmSeconds = 20.0;
    private const int MinBumpSeconds = 15;
    // Minimum gap between automatic re-posts. Rapid re-posts desync the cross-world party
    // ("unable to retrieve party member information"), so never auto-deploy more often than this.
    private const double RepostDebounceSeconds = 30.0;

    private readonly IFramework framework;
    private readonly IClientState clientState;
    private readonly IPartyList partyList;
    private readonly IPluginLog log;
    private readonly Configuration config;
    private readonly PartyFinderManager pfm;

    private DateTime lastActionUtc = DateTime.UtcNow;
    private DateTime lastTickUtc = DateTime.MinValue;
    private DateTime lastRepostUtc = DateTime.MinValue; // last automatic re-post (debounce floor)
    private DateTime? notRecruitingSinceUtc; // when recruiting first read "off" (null = currently on)
    private int lastPartyCount = -1; // -1 = unknown (not currently tracking)

    // Change-logged diagnostics so a real test can be confirmed from /xllog.
    private bool loggedRecruiting;
    private int loggedPartyCount = int.MinValue;

    private bool RepostDebounced => (DateTime.UtcNow - lastRepostUtc).TotalSeconds < RepostDebounceSeconds;

    /// <summary>UTC time of the most recent successful deploy/renew (for UI countdown). Null if inactive.</summary>
    public DateTime? LastActionUtc => config.ActivePresetId.HasValue ? lastActionUtc : null;

    public bool IsActive => config.ActivePresetId != null;

    public RefreshScheduler(IFramework framework, IClientState clientState, IPartyList partyList,
                            IPluginLog log, Configuration config, PartyFinderManager pfm)
    {
        this.framework = framework;
        this.clientState = clientState;
        this.partyList = partyList;
        this.log = log;
        this.config = config;
        this.pfm = pfm;
        this.framework.Update += OnUpdate;
    }

    /// <summary>Mark a preset as the one to keep alive and (re)start the renew clock.</summary>
    public void MarkActive(RecruitmentPreset preset)
    {
        config.ActivePresetId = preset.Id;
        lastActionUtc = DateTime.UtcNow;
        lastRepostUtc = DateTime.UtcNow; // a manual deploy starts the debounce window
        config.Save();
    }

    /// <summary>Stop keeping any preset alive (does not end the in-game listing).</summary>
    public void Stop()
    {
        if (config.ActivePresetId == null)
            return;
        config.ActivePresetId = null;
        config.Save();
    }

    /// <summary>Seconds until the next scheduled renew/bump, or null when inactive.</summary>
    public double? SecondsUntilNextAction()
    {
        if (config.ActivePresetId == null || !config.AutoRenewEnabled)
            return null;
        var elapsed = (DateTime.UtcNow - lastActionUtc).TotalSeconds;
        return Math.Max(0, EffectiveIntervalSeconds() - elapsed);
    }

    private double EffectiveIntervalSeconds()
    {
        var renewSec = Math.Max(60, config.RenewIntervalMinutes * 60.0);
        if (config.EnableBumpRefresh)
            renewSec = Math.Min(renewSec, Math.Max(MinBumpSeconds, config.BumpIntervalSeconds));
        return renewSec;
    }

    private void OnUpdate(IFramework fw)
    {
        var now = DateTime.UtcNow;
        if ((now - lastTickUtc).TotalSeconds < 1.0)
            return;
        lastTickUtc = now;

        if (config.ActivePresetId == null || !config.AutoRenewEnabled || !clientState.IsLoggedIn)
            return;

        // A leave -> re-post sequence is mid-flight (listing briefly down). Don't interfere.
        if (pfm.Busy)
            return;

        var preset = config.Presets.FirstOrDefault(p => p.Id == config.ActivePresetId);
        if (preset == null)
        {
            Stop();
            return;
        }

        var elapsed = (now - lastActionUtc).TotalSeconds;
        var recruiting = pfm.IsRecruiting;

        // Party size from every available source so a leaver is detectable in any party type (same-world,
        // cross-world, or in-instance) and regardless of recruiting state.
        var partyCount = Math.Max(partyList.Length, Math.Max(pfm.CrossRealmPartyCount, pfm.LocalPartyCount));

        if (recruiting != loggedRecruiting || partyCount != loggedPartyCount)
        {
            log.Information("[PFR][watch] recruiting={Rec} party={Count} (list={List} xr={Xr} gm={Gm}) tracked={Tracked} inParty={In}",
                recruiting, partyCount, partyList.Length, pfm.CrossRealmPartyCount, pfm.LocalPartyCount, lastPartyCount, pfm.InAnyParty);
            loggedRecruiting = recruiting;
            loggedPartyCount = partyCount;
        }

        // --- SECTION 1: TIMER CHECKS ---
        // Evaluate countdown timers first so background tasks fire regardless of closed UI visibility states.
        // Only renew/bump a listing that is actually live: renewing nothing makes no sense, and while we stay
        // armed in a full party (Section 2) an ungated renew would spuriously re-open a listing mid-duty.
        var renewSec = Math.Max(60, config.RenewIntervalMinutes * 60.0);
        var mustRenew = elapsed >= renewSec;
        var bumpDue = config.EnableBumpRefresh && elapsed >= Math.Max(MinBumpSeconds, config.BumpIntervalSeconds);

        if (recruiting && (mustRenew || bumpDue))
        {
            // Hold a mere bump while the user has the PF window open, but never skip the safety renew.
            if (!mustRenew && config.PauseWhileDetailOpen && pfm.IsDetailWindowOpen)
            {
                lastActionUtc = now; // Push clock forward to prevent freeze
                return;
            }

            if (RepostDebounced)
                return; // too soon since the last re-post; wait (avoids party desync)

            log.Information("[PFR] Scheduled refresh executing (mustRenew: {0}, bumpDue: {1}) for '{2}'", mustRenew, bumpDue, preset.Name);
            pfm.Deploy(preset, mustRenew ? "renew" : "bump");
            lastActionUtc = now;
            lastRepostUtc = now;
            return;
        }

        // --- SECTION 2: LEAVER RECOVERY & VISIBILITY MONITORING ---

        // Ignore a momentary all-zero read: the count sources flip between cross-realm and local depending
        // on recruiting state, so a single transient 0 during that flip must not corrupt the tracked count
        // or trip the stop logic while the game still says we're in a party.
        if (partyCount == 0 && pfm.InAnyParty)
            return;

        if (!recruiting)
        {
            notRecruitingSinceUtc ??= now;

            // The party filled (recruitment ended) and a member then left: re-open the listing with a real
            // Deploy (StartRecruiting). Requires >= 2 remaining — in FFXIV a 2-person party losing a member
            // dissolves to solo, which we treat as "done" (stop) below rather than a slot to backfill.
            if (config.FixOnMemberLeave && lastPartyCount >= 2 && partyCount >= 2 && partyCount < lastPartyCount)
            {
                if (RepostDebounced)
                {
                    log.Information("[PFR] Leaver detected ({Old} -> {New}) but re-deploy is debounced; will retry.",
                        lastPartyCount, partyCount); // hold lastPartyCount high so we retry after the window
                    return;
                }

                log.Information("[PFR] Member left a full party ({Old} -> {New}); fixing listing for '{Name}' (recRoles={Rec}).",
                    lastPartyCount, partyCount, preset.Name, config.UseRecommendedRolesOnLeave);
                if (config.UseRecommendedRolesOnLeave)
                    pfm.ApplyRecommendedRoles("leaver-recRoles");
                else
                    pfm.Deploy(preset, "leaver");
                lastPartyCount = partyCount;
                lastActionUtc = now;
                lastRepostUtc = now;
                notRecruitingSinceUtc = null;
                return;
            }

            lastPartyCount = partyCount;

            // Only give up when genuinely done: not recruiting AND not in any party by ANY signal. While
            // still grouped we stay active and armed, waiting for someone to leave.
            if (!pfm.InAnyParty && partyCount <= 1 &&
                elapsed > StartupGraceSeconds &&
                (now - notRecruitingSinceUtc.Value).TotalSeconds > EndedConfirmSeconds)
            {
                log.Information("[PFR] Listing down and not in a party; stopping auto-renew for '{Name}'.", preset.Name);
                Stop();
            }

            return;
        }

        notRecruitingSinceUtc = null; // recruiting confirmed active

        // A member left while the listing is still up: re-deploy it (the "Deploy" button) so the vacated
        // slot reopens with the preset's settings and a fresh timer. Debounced so rapid leaves can't desync
        // the cross-world party.
        if (config.FixOnMemberLeave && lastPartyCount >= 2 && partyCount >= 1 && partyCount < lastPartyCount)
        {
            if (RepostDebounced)
            {
                log.Information("[PFR] Leaver detected ({Old} -> {New}) but re-deploy is debounced; will retry.",
                    lastPartyCount, partyCount);
                return;
            }

            log.Information("[PFR] Party shrank while recruiting ({Old} -> {New}); fixing listing for '{Name}' (recRoles={Rec}).",
                lastPartyCount, partyCount, preset.Name, config.UseRecommendedRolesOnLeave);
            if (config.UseRecommendedRolesOnLeave)
                pfm.ApplyRecommendedRoles("leaver-recRoles");
            else
                pfm.Deploy(preset);
            lastPartyCount = partyCount;
            lastActionUtc = now;
            lastRepostUtc = now;
            return;
        }
        lastPartyCount = partyCount;
    }

    public void Dispose() => framework.Update -= OnUpdate;
}