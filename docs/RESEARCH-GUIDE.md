# Research guide (for players, not developers)

These sessions record what the game's server sends to your PC so we can work out where pet and soul data lives.
You play normally; the tool only **listens** to network traffic. It never opens, reads, or changes the game, and it
never sends anything to the network. Recordings stay on your computer until you choose to share one.

> A recording contains the game data the server sent to your character (names, inventory, nearby players, chat-like text).
> It does not contain your password (the login is encrypted and we ignore everything your PC sends). Do not share a recording
> you are not comfortable sharing.

Wherever you see `pt-research`, use the full path from step 0.3.

---

## R0. Setup (once)

### 0.1 Install Npcap
1. Download Npcap from <https://npcap.com> and run the installer.
2. Tick **"Install Npcap in WinPcap API-compatible Mode"**.
3. Leave **"Restrict Npcap driver's access to Administrators only"** **unticked**, so you do not need to run anything as administrator.

### 0.2 Game settings
In Aion 2 set the display mode to **Borderless Windowed** (needed later for the overlay).

### 0.3 Build the tool (needs the .NET 10 SDK)
Open **PowerShell** in the project folder (`D:\Development\GitHub\Aion2-PetTracker`) and run:

```powershell
dotnet build -c Release
$pt = "D:\Development\GitHub\Aion2-PetTracker\src\PetTracker.Research\bin\Release\net10.0-windows\pt-research.exe"
```

Everything below uses `& $pt ...`. (The `$pt` line has to be repeated each time you open a new PowerShell window.)

### 0.4 Check that the tool can see your network and the game
```powershell
& $pt adapters
```
You should see a list of network adapters. If you instead see "Install Npcap from https://npcap.com (WinPcap API-compatible mode)",
go back to 0.1.

Start the game (any screen), then:
```powershell
& $pt connections
```
You should see `AION2` process ids and one or more lines like `192.168.x.x:12345 <-> 203.0.113.5:13328`.
If it says no process is running, check the process name in Task Manager and tell us (it can be set with `--process`).

If you use a VPN or ping reducer, add `--adapter "name or IP"` (copy it from the `adapters` list) to every `record` and `live` command.

### 0.5 Write down what you see in game (for R2 and later)
For 8-10 distinctive pets, note: pet name, souls you hold, and level. A screenshot of the pet collection page also works.
Do this right before each recording session.

---

## R1. Stream and framing

Goal: prove that the tool can read the game's message stream from start to finish with **0 desyncs**, and learn the exact
message format.

1. **Before you start the game launcher**, open PowerShell and run:
   ```powershell
   & $pt record -o r1.a2cap
   ```
   The status line shows what it is doing. "Waiting for AION2" is normal until the game starts.
2. Start the launcher, press Play, and go through character select into the world. Press **F9** at each step:
   - when you press **Play**
   - when you reach **character select**
   - when you click **Enter World** (or the equivalent)
   - when you are **standing in the world** (then stand still)
3. Stand still for **2 minutes**. Press **F9** once when you start waiting.
4. **Teleport or change zone once.** Press **F9** just before and **F9** just after.
5. Go back to the PowerShell window and press **Ctrl+C**. It prints "Saved r1.a2cap".

Hotkeys: **F9** = numbered marker. **F10** = marker plus a short note you type in the PowerShell window and confirm with Enter.

### Check the result
```powershell
& $pt frames r1.a2cap --limit 40 $pt frames r1.a2cap --limit 40
& $pt flows r1.a2cap
```
Look at the last lines:
- `desyncs 0` and a `heartbeats` count above 0 means **pass**.
- If `desyncs` is above 0, run `flows` and send us its output. The framing (`fixed:-4`) is already the default.
- The `Bundles:` line says whether compressed bundles were found and which LZ4 format worked.
- `flows` lists every connection with its classification: `game` (decoded), `TLS` (launcher/web traffic, ignored) or `unknown`. The game normally shows a lobby flow and a world flow.

**Send us:** `r1.a2cap` and the text printed by the `frames` and `flows` commands.

---

## R2. Collection snapshot

Goal: find the message that tells the game your whole pet collection.

1. Write down your pets' souls and levels (step 0.5).
2. Start recording **before** logging in:
   ```powershell
   & $pt record -o r2.a2cap
   ```
3. Log in from the launcher to the world, stand still. Press **F9** the moment you are in the world.
4. Wait 30 seconds, then press **F10** and type `about to open pet ui` + Enter.
5. Open the **pet collection window**, wait 10 seconds, then press **F10** and type `pet ui open` + Enter.
6. Close the window. Press **Ctrl+C** in PowerShell.

### Look for your numbers
Pick a soul count or level you wrote down (say pet X has 17 souls) and search for it:
```powershell
& $pt search r2.a2cap --int 17 --as u8,u16le,u32le,varint
```
The last section lists the most frequent locations (opcode and byte offset). Repeat for two or three other numbers.
If you know a pet's name in English, try:
```powershell
& $pt search r2.a2cap --str "Kerubim" --enc utf16le,utf8
```
An opcode that keeps showing up for several different numbers is very likely the collection snapshot.

See which messages appear only when you opened the pet window:
```powershell
& $pt hist r2.a2cap --around "note:pet ui open" --control "note:about to open" --window 10s
```

**Send us:** `r2.a2cap`, your written list of pets/souls/levels, and the search output.

---

## R3. Soul drops

Goal: find the message that says "you got a soul".

1. Pick a monster that drops souls for one pet. Start recording:
   ```powershell
   & $pt record -o r3.a2cap
   ```
2. Log in and go to the monster.
3. Kill monsters one after another:
   - **Right after a soul drops** press **F9**.
   - After a kill that **does not** drop a soul press **F10** and type `no drop` + Enter. The typed note is what separates the two groups, so do not leave it empty.
   - Do about **10 of each**. Write down the soul count before you start and after the last soul.
4. Press **Ctrl+C** in PowerShell.

### Find the soul message
Compare the moments just before each F9 with those just before each F10 "no drop":
```powershell
& $pt hist r3.a2cap --around nonote --control "note:no drop" --window 2s
```
Rows marked `<-- candidate` show up around almost every drop but almost never around a non-drop. Take the best opcode, for example `2601`, and look at its bytes near the drops:
```powershell
& $pt hex r3.a2cap --opcode 2601 --around nonote --limit 10
```
The `*` marks bytes that change from drop to drop. Compare them with your before/after counts.
You can also confirm with:
```powershell
& $pt search r3.a2cap --int 17 --as u8,u16le,u32le,varint
```
(replace 17 with the soul count after the first drop).

**Send us:** `r3.a2cap`, your before/after soul counts, and the `hist` and `hex` output.

---

## If something goes wrong

| Message or symptom | What to do |
|---|---|
| "Install Npcap from https://npcap.com" | Install Npcap with WinPcap API-compatible mode (step 0.1). Reboot if the installer asks. |
| `record` shows "Waiting for AION2" forever | Run `& $pt connections` while in game. If no process appears, tell us the process name from Task Manager and use `--process Name`. |
| Status says "also watching non-web TCP for the heartbeat" | The game is not in the TCP table yet; the tool looks at all non-web TCP for the game's heartbeat and usually still works. Tell us if it never reaches "Locked". |
| "Could not register the F9/F10 hotkeys" | Another program uses F9/F10 (overlay software, recorders). Close it and try again. |
| Many `desyncs` | Run `flows <file>` and send us the output. |
| Using a VPN | Add `--adapter "name or IP"` to `record`. |
| F10 prompt appears but nothing seems to happen | Click the PowerShell window, type the note and press Enter. |

Nothing in these steps needs administrator rights. If a command only works as administrator, tell us.
