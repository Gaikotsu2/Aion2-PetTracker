# Aion 2 Pet Tracker

A small Windows app for **Aion 2 (Global)** that keeps track of your pet collection while you play: every pet's level,
how many souls it still needs, and what you gained this session. Pin the pets you are working on and they show up in a
compact overlay on top of the game.

![The collection window: every pet with its genus, level, souls progress and session gains](docs/screenshots/app-collection.png)

## Is it safe?

- It only **listens** to the data the game server sends to your PC, the same way a network monitor does.
- It never opens, reads or changes the game program or its files.
- It sends nothing to the internet. It only goes online when you click a link button, which opens
  [TH.GL](https://aion2.th.gl) or [PetSouls.dev](https://petsouls.dev/) in your browser.
- It needs no administrator rights.

This is an unofficial fan tool, not affiliated with or endorsed by NCSOFT.

## What you need

There is no ready-made download yet, so your PC builds the app from this code during installation. That sounds
harder than it is: you install two free tools once, then run one command.

- [ ] **Windows 11, 64-bit.**
- [ ] **.NET 10 SDK** from Microsoft: <https://dotnet.microsoft.com/download/dotnet/10.0>. In the **SDK** column, pick
      **Windows x64** under "Installers". This is what builds the app.
- [ ] **Npcap**: <https://npcap.com>. This is what lets the app see the game's network traffic. During installation:
  - **tick** "Install Npcap in WinPcap API-compatible Mode",
  - leave "Restrict Npcap driver's access to Administrators only" **unticked**.
- [ ] **Aion 2 in Borderless Windowed mode** (game display settings), so the overlay can appear above the game.

> [!WARNING]
> **Smart App Control.** If Windows Smart App Control is turned on (Windows Security → App & browser control), Windows
> refuses to run programs you built yourself, and you will see error `0x800711C7`. Windows offers no way to make an
> exception for a single program. See [docs/TECHNICAL.md](docs/TECHNICAL.md#smart-app-control) for details.

## Install

1. Install the **.NET 10 SDK** and **Npcap** from the list above.
2. Get the code: at the top of this page click the green **Code** button → **Download ZIP**, then right-click the
   downloaded file → **Extract All**. (If you use Git, `git clone` works too.)
3. Open the extracted folder (the one that contains `README.md` and a `scripts` folder). Right-click an empty spot in
   the folder → **Open in Terminal**.
4. Copy this line into the terminal and press Enter:

   ```powershell
   powershell -ExecutionPolicy Bypass -File scripts\install.ps1
   ```

   Windows blocks downloaded scripts by default; `-ExecutionPolicy Bypass` lets just this one script run, just this
   once. The first build takes a minute or two.
5. Start **Aion 2 Pet Tracker** from the Start Menu.

## Using it

**First run.** Start the tracker, then log in to your character. The game sends your full pet list at login and the
collection window fills in. Without Npcap the tracker still opens and shows your last saved collection, with a banner
linking to the Npcap download.

**Collection window.** All your pets with genus, level, souls progress and this session's gains. Filter and sort with
the buttons at the top. Tick **Pin** to show a pet on the overlay; right-click a pet to rename it or open it on TH.GL.

**Overlay.** Shows your pinned pets and recent events above the game.

<img src="docs/screenshots/app-overlay.png" alt="The overlay with three pinned pets and their souls progress" width="300">

Default hotkeys: **Ctrl+Shift+P** shows or hides the overlay, **Ctrl+Shift+L** turns click-through on and off (so
your clicks go to the game instead). Opacity, size and hotkeys are in Settings → Overlay.

**Settings.** Pick a section on the left.

![Settings, General section: Start with Windows and session reset](docs/screenshots/app-settings-general.png)

- **General:** *Start with Windows* starts the tracker with only the overlay showing; the collection window waits in
  the system tray. *Reset session stats* starts the session counters from zero.
- **Overlay:** opacity, size and hotkeys.
- **Capture:** only needed with a VPN or ping reducer (see [Troubleshooting](#troubleshooting)).
- **Opcodes:** only needed after a game update (see [Troubleshooting](#troubleshooting)).
- **Data:** PetSouls.dev and backups, below.

**PetSouls.dev and backups.** Settings → Data.

![Settings, Data section: PetSouls.dev export and import, backup and restore](docs/screenshots/app-settings-data.png)

- **Copy PetSouls code**, then on [PetSouls.dev](https://petsouls.dev/) open Settings → Import and paste it. You can
  also paste a code from the site into the tracker; the next time you log in, the game's real levels replace it.
- **Export backup** saves your collection, pet names, pins and settings in one file; **Restore backup** loads it,
  for example on another PC.

## Updating

Download the new ZIP (or `git pull`) and run the same install command again. Your collection and settings are kept.

## Uninstalling

From the code folder, run:

```powershell
powershell -ExecutionPolicy Bypass -File scripts\install.ps1 -Uninstall
```

This removes the app, its Start Menu shortcut and the "Start with Windows" setting, but keeps your collection in case
you come back. Add `-RemoveData` at the end to delete your collection too.

## Troubleshooting

**The banner says "Live capture is not running".** Npcap is missing, or it was installed with the
"Administrators only" option. Reinstall Npcap with the settings from [What you need](#what-you-need).

**The overlay is hidden behind the game.** Switch Aion 2 to Borderless Windowed mode.

**Nothing updates and the status says "Unknown version" after a game patch.** A patch can change how the game labels
its messages. The tracker usually spots the new labels itself and offers an **Apply** button in the banner. If it does
not, see [docs/PATCH-DAY.md](docs/PATCH-DAY.md), or wait for an update to this project.

**I use a VPN or ping reducer and nothing shows up.** In Settings → Capture, type part of the VPN adapter's name or its
IP address into the adapter box.

**Windows refuses to start the tracker (error `0x800711C7`).** That is Smart App Control; see
[What you need](#what-you-need).

**The install command fails with an error about `dotnet`.** The .NET 10 SDK is not installed, or the terminal was open
before you installed it. Install the SDK, close the terminal, and open a new one.

## More technical information

- [docs/TECHNICAL.md](docs/TECHNICAL.md): building by hand, install script options, where your data is stored, how
  the pet data was collected.
- [docs/PROTOCOL.md](docs/PROTOCOL.md): what is known about the game's network messages.
- [docs/PATCH-DAY.md](docs/PATCH-DAY.md): getting the tracker working again after a game update.
- [docs/RESEARCH-GUIDE.md](docs/RESEARCH-GUIDE.md) and [docs/RESEARCH-PCAPNG.md](docs/RESEARCH-PCAPNG.md): helping
  with research recordings.

## Credits and license

Pet names and genera come from the community database [TH.GL](https://aion2.th.gl/db/pets).

Data: TH.GL (aion2.th.gl), game data © NCSOFT.

The source code is released under the [MIT License](LICENSE), © 2026 Gaikotsu2.

The pet catalog in `data/` is compiled from [TH.GL](https://aion2.th.gl) and community guides; AION 2 names and game
data belong to NCSOFT. This is an unofficial fan tool, not affiliated with or endorsed by NCSOFT.
