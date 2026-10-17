# Research while code signing is pending (pcapng workflow)

`pt-research record` needs our own, unsigned, program to capture live traffic, and Windows Smart App Control may refuse to
run it (see [SIGNING.md](SIGNING.md)). While the Artifact Signing account is being set up (the identity check takes 1 to 20 business
days) you can still collect research data this way:

1. **Record** the game's traffic with **Wireshark's `dumpcap`**. It is signed by the Wireshark Foundation, so Smart App Control
   allows it. The result is a `.pcapng` file.
2. **Write down marker times** in a text file while you play (what you did and when).
3. **Import** both with `pt-research import`. This gives the same `.a2cap` file as `record` does, and the rest of the
   [research guide](RESEARCH-GUIDE.md) (`flows`, `frames`, `hist`, `search`, `diff`) works on it unchanged.

Everything stays passive: dumpcap only listens to network traffic, nothing touches the game.

## 1. Install Wireshark (once)

```powershell
winget install -e --id WiresharkFoundation.Wireshark
```

The installer offers to install **Npcap**. If Npcap is already installed (it is if you followed the research guide), **untick/skip
the bundled Npcap** so the existing one is kept. Otherwise let it install Npcap and keep **"Install Npcap in WinPcap API-compatible Mode"**
ticked and **"Restrict Npcap driver's access to Administrators only"** unticked, as in the research guide.

## 2. Find your network interface

```powershell
& "C:\Program Files\Wireshark\dumpcap.exe" -D
```

It prints a numbered list such as:

```
1. \Device\NPF_{3F2A...}  (Ethernet)
2. \Device\NPF_{91C4...}  (Wi-Fi)
```

Use the number (or the name in brackets, in quotes) of the adapter your PC uses for internet. If you are not sure, open
Windows **Settings, Network & internet**, see whether you are on Ethernet or Wi-Fi, and choose that one. A VPN adds its own entry;
capture on the VPN adapter if the game runs through it.

## 3. Record

Start the recording **before** you start the game (or at least before you log in and pick a character), so the connection set-up is
captured and the flow can be recognised from its start. Leave it running while you play.

```powershell
& "C:\Program Files\Wireshark\dumpcap.exe" -i <interface> -f "tcp and not port 443 and not port 80" -w r2.pcapng
```

Replace `<interface>` with the number from step 2 (for example `-i 1`). The filter keeps only non-web TCP traffic, which is what the game
uses and keeps the file small; it matches what `pt-research record` captures. Press **Ctrl+C** to stop.

If it says "permission denied" or finds no interfaces, run PowerShell as administrator once, or reinstall Npcap without the
administrators-only option.

Put the file in the `captures` folder of the repository (it is ignored by git, as are `*.pcapng` and `*.pcap`).

## 4. Markers: write the times down

A pcapng file cannot hold the F9 markers. Instead keep a plain text file, for example `captures\markers.txt`, with **one marker per line**:
the **wall-clock time** and an optional note.

```
# time (local, 24 hour)   note
19:42:10        opened pet window
19:42:55.500    fed pet #1
19:43:20        released a soul
```

* Format: `HH:mm:ss` or `HH:mm:ss.fff`, then a space and anything you want to remember. Blank lines and lines starting with `#` are ignored.
* Use the **time on this PC**: the Windows clock in the taskbar (right-click it, **Adjust date and time**, to show seconds) or the clock
  shown in the game, if it is the same time zone. The capture timestamps come from the same PC clock, so the markers line up.
* Add the time as you act, or note it right after (to the second is fine). The marker is matched against what the server sent *before* it,
  so write the time of the action itself, not the time you finished typing.
* You can type the lines by hand afterwards from a note on paper. Times are local time; a capture that runs past midnight is handled.

## 5. Import

```powershell
$pt = "D:\Development\GitHub\Aion2-PetTracker\src\PetTracker.Research\bin\Release\net10.0-windows\pt-research.exe"
& $pt import captures\r2.pcapng -o captures\r2.a2cap --markers captures\markers.txt
```

(or `dotnet ...\pt-research.dll import ...`). It prints the number of packets, the game flows it found and the frames decoded. Then
continue as in the research guide: `& $pt flows captures\r2.a2cap`, `& $pt frames ...`, and so on.

Options:

| Option | Meaning |
| --- | --- |
| `-o <file.a2cap>` | Output file (required). |
| `--markers <markers.txt>` | Wall-clock markers as above. A warning is printed if any fall outside the capture (wrong date/clock). |
| `--server-port N` (repeatable) | Treat flows whose server uses port N as the game's. Use it if the automatic detection finds nothing. |

How the game flow is found: an offline file has no list of the game's connections, so, as in `record` when it has no process match,
flows are recognised by the **heartbeat / framing** (the `0E 00 36` heartbeat in the server stream) and TLS-looking flows are rejected. The
server side of each connection is worked out from the TCP handshake (or the heartbeat, or the busier side). If the file started
after the game connected ("joined mid-stream") the decoder resynchronises on the heartbeat. `--server-port` forces a flow if the heartbeat is
not seen. `import` needs neither Npcap nor Wireshark.

## If our CLI is blocked on this PC: use Windows Sandbox

If Smart App Control also stops `pt-research` from running (the file loads in the sandbox but not on your desktop), run the analysis in
**Windows Sandbox**: a throw-away, isolated copy of Windows. It is a Windows 11 **Pro/Enterprise/Education** feature (not Home).

1. Turn it on once: open **Turn Windows features on or off** (Start menu search), tick **Windows Sandbox**, press OK and restart.
2. Publish the CLI as a self-contained folder (needs no .NET installation in the sandbox):

   ```powershell
   .\scripts\publish-research.ps1
   ```

   This runs `dotnet publish src/PetTracker.Research -c Release -r win-x64 --self-contained -o artifacts/pt-research` (and signs the result if signing is configured).
3. Put your `.pcapng` and `markers.txt` into the `captures` folder, then double-click `scripts\sandbox.wsb`. It maps `artifacts\pt-research` and
   `captures` **read-only** into the sandbox and opens a PowerShell window there with `pt-research` on the PATH. (If the repository is not at
   `D:\Development\GitHub\Aion2-PetTracker`, edit the two `HostFolder` lines in the file first.)
4. In that window:

   ```powershell
   pt-research import C:\Captures\r2.pcapng -o $HOME\Desktop\r2.a2cap --markers C:\Captures\markers.txt
   pt-research flows $HOME\Desktop\r2.a2cap
   ```

The folders are read-only on purpose, so output goes to the sandbox's own Desktop. **When you close the sandbox everything in it is deleted.**
Copy anything you want to keep (copy in the sandbox, paste on your desktop) before closing, or share the printed output.
The sandbox has no network access in this configuration. It does not inherit your desktop's Smart App Control setting, but if it is
blocked there as well, signing is the only way forward.

## When signing is ready

Switch to the normal `pt-research record` workflow in the [research guide](RESEARCH-GUIDE.md). It also records the F9 markers by itself, so the
marker text file is no longer needed. Old imported captures keep working.
