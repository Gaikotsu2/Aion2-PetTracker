# Technical details

Background for developers and curious users. The player-facing install guide is the [README](../README.md).

## How it works

The tracker is a passive packet reader. It captures the server-to-client traffic of Aion 2 with Npcap, decodes the
pet messages and keeps a local collection. It never touches the game process; to tell which connection belongs to
the game it only looks at the Windows TCP table. What is known about the protocol is in [PROTOCOL.md](PROTOCOL.md).

## Building from source

You need the **.NET 10 SDK** (it includes the Desktop Runtime the app runs on).

```powershell
dotnet build -c Release                # build everything, including tests
dotnet test                            # run the test suite
scripts\publish.ps1                    # publish the tracker and the research tool
scripts\publish.ps1 -SkipResearch      # publish only the tracker
scripts\publish.ps1 -SelfContained     # bundle the .NET runtime into the tracker (about 70 MB)
```

`publish.ps1` writes:

- `artifacts\PetTracker\PetTracker.App.exe`: win-x64, one file, with a `data\` folder next to it (the app finds its
  data through the exe's folder). Framework-dependent by default, so it needs the .NET 10 Desktop Runtime;
  `-SelfContained` bundles the runtime instead.
- `artifacts\pt-research\`: the self-contained research tool (see [RESEARCH-PCAPNG.md](RESEARCH-PCAPNG.md)).

Extra arguments are passed on to every `dotnet publish`, for example `scripts\publish.ps1 -p:Version=0.2.0`.

## install.ps1 reference

Everything installs for the current user; no admin rights are needed.

```powershell
scripts\install.ps1                  # publish, copy to %LOCALAPPDATA%\Programs\Aion2PetTracker, add a Start Menu shortcut
scripts\install.ps1 -SelfContained   # same, with the .NET runtime bundled (about 70 MB, no runtime install needed)
scripts\install.ps1 -NoPublish       # install what is already in artifacts\PetTracker
scripts\install.ps1 -WhatIf          # show what it would do, change nothing
scripts\install.ps1 -Force           # do not ask before closing a running tracker
scripts\install.ps1 -Uninstall       # remove the program folder, Start Menu shortcut and "Start with Windows" entry
scripts\install.ps1 -Uninstall -RemoveData   # also delete %AppData%\Aion2PetTracker
```

If the tracker is running, the script asks before closing it. It only ever looks at a process named
`PetTracker.App`. Updating is running the script again; your data folder is never touched.

If PowerShell's execution policy blocks the script, run it as
`powershell -ExecutionPolicy Bypass -File scripts\install.ps1`, which relaxes the policy for that one process only.

## Where files live

| What | Where |
| --- | --- |
| Program | `%LOCALAPPDATA%\Programs\Aion2PetTracker\` |
| Start Menu shortcut | `%AppData%\Microsoft\Windows\Start Menu\Programs\Aion 2 Pet Tracker.lnk` |
| Your data | `%AppData%\Aion2PetTracker\` |

The data folder holds:

- `collection.json`: your pets' current state,
- `events.jsonl`: the event history,
- `settings.json`: overlay look, hotkeys, adapter override, pins and other settings,
- `pet-names.json`: your own pet names, which always win over the shipped catalog,
- `opcodes.json`: opcode overrides made in Settings → Opcodes,
- `backups\`: the state saved automatically before every PetSouls import or backup restore.

**Start with Windows** (Settings → General) adds one value, `Aion2PetTracker`, under
`HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Run`. The tracker then starts minimized: the overlay only,
the collection window stays in the tray. The app is single-instance: launching it a second time opens the collection
window of the first.

## Pet data

Pet ids, names and genera in `data/pets.json` were imported once from the community database **TH.GL**
(<https://aion2.th.gl/db/pets>), which lists the same pet ids the game server sends. `data/pets.source.json` records
the source, fetch date and credit line.

Pets limited to one place carry a hand-maintained `"source"` field: `"Dungeon"` (dungeon drops only, for example
from bosses) or `"Shop"` (only sold in a shop), e.g. `{"id":1123,"name":"...","genus":"Fera","source":"Dungeon"}`.
It is independent of the genus, does not change the Collection Status count, and drives the Dungeon / Shop chips and
filters in the collection window. Any other value is ignored. Re-running `import-names` keeps it.

The import was a one-time, polite fetch of a single list page (`pt-research import-names`, descriptive User-Agent, at
most one request per second, hard cap of 220 requests). The tracker app itself makes no network calls: the per-pet
link button only opens `https://aion2.th.gl/db/pets/pet_<id>` in your browser, and Settings → Data can open
`https://petsouls.dev/`.

## PetSouls.dev code

**Copy PetSouls code** puts your pet levels on the clipboard in the format [PetSouls.dev](https://petsouls.dev/)
imports (its Settings → Import). Unregistered pets are left out, and the site knows levels only, not souls progress.

Pasting a code exported from the site sets levels in the tracker; the next login snapshot from the game overwrites
them with the real state. The targets and watch list from that code are kept and sent back on the next export, so a
round trip does not wipe them.

**Export backup** writes one file with the collection, your pet names, pins, overlay look, hotkeys and adapter; the
event history (`events.jsonl`) is not included.

## Smart App Control

The release downloads are signed with Azure Artifact Signing (a Public Trust certificate), so they run on a PC with
Smart App Control enforced. A tracker you build from source is not signed: Windows refuses to run it (error
`0x800711C7`) and does not let you allow-list a single file, so a self-built tracker only runs on a PC where Smart App
Control is off.

`scripts\publish.ps1 -Sign` signs our own binaries (`PetTracker.App.exe`, `pt-research.exe`, `PetTracker.*.dll`; never
packaged third-party libraries). It needs an Azure login with the *Certificate Profile Signer* role on the account in
`signing\metadata.json`, so only the project owner can produce signed builds. For research work,
[RESEARCH-PCAPNG.md](RESEARCH-PCAPNG.md) describes a workaround with Wireshark's signed `dumpcap` and Windows Sandbox.

## After a game update

A patch can renumber the network opcodes. The tracker notices ("Unknown version"), offers detected new codes with an
**Apply** button, and has an opcode editor in Settings → Opcodes. The whole routine, including the
`pt-research detect-opcodes` and `verify` commands, is in [PATCH-DAY.md](PATCH-DAY.md).

## Project layout

| Path | Contents |
| --- | --- |
| `src/PetTracker.App` | the WPF app: collection window, overlay, settings |
| `src/PetTracker.Core` | capture, decoding, domain model and persistence |
| `src/PetTracker.Research` | `pt-research`, the command-line research tool |
| `tests/PetTracker.Tests` | unit tests |
| `data/` | shipped pet catalog and default opcodes |
| `scripts/` | publish and install scripts, Windows Sandbox config |
| `docs/` | protocol notes, patch-day and research guides |
