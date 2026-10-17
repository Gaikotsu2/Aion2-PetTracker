# Aion 2 Pet Tracker

A passive pet tracker for Aion 2 (Global). It only reads server-to-client network traffic; it never touches the game
process. See `docs/PROTOCOL.md` for what is known about the protocol.

## Data credits

Pet ids, names and genera in `data/pets.json` were imported once from the community database **TH.GL**
(<https://aion2.th.gl/db/pets>), which lists the same pet ids the game server sends. See `data/pets.source.json` for the
source, fetch date and credit line.

Data: TH.GL (aion2.th.gl), game data © NCSOFT.

The import is a one-time, polite fetch of a single list page (`pt-research import-names`, descriptive User-Agent, at most
one request per second, hard cap of 220 requests). The tracker app itself makes no network calls; the per-pet link
button only opens `https://aion2.th.gl/db/pets/pet_<id>` in your browser. Your own names in
`%AppData%\Aion2PetTracker\pet-names.json` always win over the shipped data.

## Install, update, uninstall

You need Windows 11 (x64) and the **.NET 10 Desktop Runtime** (or build with `-SelfContained`, below) plus **Npcap** for live
capture. Everything below installs for the current user only; no admin rights are needed.

**Npcap.** The tracker reads the game's network traffic with Npcap (<https://npcap.com>). Install it once and tick
**"Install Npcap in WinPcap API-compatible Mode"**; leave "Restrict Npcap driver's access to Administrators only" unticked, so
the tracker does not need elevation. Without Npcap the tracker starts and shows your last saved collection with a banner that
links to the download. Run the game in borderless windowed mode so the overlay can show above it.

**Install** (from a clone of this repository, with the .NET 10 SDK):

```powershell
scripts\install.ps1                  # publish, copy to %LOCALAPPDATA%\Programs\Aion2PetTracker, add a Start Menu shortcut
scripts\install.ps1 -SelfContained   # same, with the .NET runtime bundled (about 70 MB, no runtime install needed)
scripts\install.ps1 -WhatIf          # show what it would do, change nothing
```

If the tracker is running, the script asks before closing it (it only ever looks at a process named `PetTracker.App`).
To just build without installing: `scripts\publish.ps1` writes `artifacts\PetTracker\PetTracker.App.exe` (one file, with a
`data\` folder next to it) and `artifacts\pt-research\`.

**Update.** `git pull`, then run `scripts\install.ps1` again. Your data in `%AppData%\Aion2PetTracker` is never touched.

**Uninstall.** `scripts\install.ps1 -Uninstall` removes the program folder, the Start Menu shortcut and the "Start with
Windows" entry. It keeps your collection, history, settings and pet names; add `-RemoveData` to delete those too.

**Start with Windows** is a tick box in the collection window, Settings. It adds one value under
`HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Run` and starts the tracker minimized: the overlay only, the
collection window stays in the tray. Launching the tracker a second time just opens the collection window of the first.

**Smart App Control.** On a PC with Smart App Control enforced, Windows refuses to run unsigned programs, which includes a
freshly built tracker (error `0x800711C7`). You cannot allow-list a single file. Signing is opt-in and covered in
[docs/SIGNING.md](docs/SIGNING.md); until then see [docs/RESEARCH-WITHOUT-SIGNING.md](docs/RESEARCH-WITHOUT-SIGNING.md).

**After a game update.** Opcodes can be renumbered by a patch. The tracker notices ("Unknown version"), offers detected new
codes with an Apply button, and has an opcode editor in Settings. The whole routine, including the `pt-research
detect-opcodes` and `verify` commands, is in [docs/PATCH-DAY.md](docs/PATCH-DAY.md).

## License

The source code is released under the [MIT License](LICENSE), © 2026 Gaikotsu2.

The pet catalog in `data/` is compiled from [TH.GL](https://aion2.th.gl) and community guides (see Data credits); AION 2
names and game data belong to NCSOFT. This is an unofficial fan tool, not affiliated with or endorsed by NCSOFT.
