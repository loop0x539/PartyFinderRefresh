# Party Finder Refresh

A Dalamud plugin for FFXIV that lets you **save Party Finder recruitment presets**, **deploy any
preset in one click**, and **keep the listing alive automatically** by re-recruiting before the
60‑minute timer expires.

Built against the current API: **Dalamud v15 / API 15** (`Dalamud.NET.Sdk/15.0.0`, .NET 10).

## What it does

- **Capture** – Set up a recruitment in the normal in‑game Party Finder window, then save that exact
  configuration (category, duty, conditions, loot rule, language, job‑slot requirements, comment) as
  a named preset. Capture works by snapshotting the game's own `AgentLookingForGroup.StoredRecruitmentInfo`,
  so whatever the game accepts, the preset stores faithfully.
- **Deploy** – One click re-creates the listing from a preset and starts recruiting.
- **Auto-renew** – While a preset is active, the plugin re-recruits before the listing's 60‑minute
  hard expiry (default every 55 min), so your PF stays up indefinitely without manual re-posting.
  Job‑slot requirements and the comment are re‑applied on each renew (a plain re-recruit would
  otherwise reset slots to your current party).
- **Optional bump** – Re-recruit on a short interval to keep the listing near the top of the search
  list. Pauses while you have the Party Finder window open.

## Build

Requires the .NET 10 SDK and a local Dalamud dev install (XIVLauncher). The SDK resolves Dalamud
libraries from `%AppData%\XIVLauncher\addon\Hooks\dev`.

```
dotnet build -c Release
```

Output: `bin/Release/PartyFinderRefresh.dll` (dev-loadable) and `bin/Release/PartyFinderRefresh/latest.zip`.

## Install (dev plugin)

1. XIVLauncher → Dalamud Settings → **Experimental** → *Dev Plugin Locations*.
2. Add the full path to `bin/Release/PartyFinderRefresh.dll`.
3. Save; the plugin appears under *Dev Tools → Installed Dev Plugins*.

## Usage

- `/pfr` – open the main window.
- `/pfr config` – open settings.

In the main window: type a name and **Capture** your current in‑game recruitment setup, then
**Deploy** any saved preset. The status block shows whether you're recruiting and the countdown to
the next refresh.

## Maintenance note — game signatures

ClientStructs wraps the recruitment *data* (`StoredRecruitmentInfo`) and reading state, but not the
three native functions used to drive recruitment. Those are resolved by signature in
[`Game/PartyFinderManager.cs`](Game/PartyFinderManager.cs):

- `OpenRecruitmentWindow`
- `StartRecruiting`
- `UpdatePartyFinderListings`

If a game patch shifts these, deploy/refresh disable themselves and a message is logged to `/xllog`
(capture/save still work). Update the three `Sig*` constants at the top of that file to restore them.
Signatures are based on [AutoPartyFinder](https://github.com/Paparogue/AutoPartyFinder).

This is a third‑party plugin for personal use.
