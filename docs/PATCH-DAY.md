# Patch day

A game update can renumber the network opcodes (it has happened to every packet tool), and less likely change the frame
format. The tracker is built so that this costs minutes, not a rewrite. Nothing here touches the game: everything works on
captured bytes.

## What you will see

* The status pill turns amber and says **Unknown version**: the game is connected and sending heartbeats, but no pet message
  has arrived for 60 seconds after login.
* If the tracker finds a pet collection snapshot **by its shape** in the last few minutes of traffic, a banner appears in the
  overlay and in the collection window: *"Pet messages seem to have moved after a game update. Detected new codes. Apply?"*
  **Apply** writes `%AppData%\Aion2PetTracker\opcodes.json`, reloads the opcodes right away (no restart) and re-processes the
  buffered traffic, so your collection loads at once. Nothing is ever applied without your click, and the whole thing is logged
  in `events.jsonl` (`OpcodeDetection*` lines).
* If desyncs keep coming (more than 5 in 30 seconds), the status pill's tooltip adds **"Framing changed? Run
  pt-research frames --auto-detect"**. The framing is never changed automatically.

If the banner appears and the applied codes work, you are done. The steps below are for when you want to check, or when
the automatic detection does not fire (for example you only play long sessions with no login traffic captured).

## The manual routine

1. **Record a login.** Start the game launcher, then
   `pt-research record -o captures\after-patch.a2cap` (see [RESEARCH-GUIDE.md](RESEARCH-GUIDE.md)), log in, wait until your
   pet window has loaded, kill one monster that drops a soul if you can, then stop the recording. More is better: a soul drop
   and a registration or level-up let the detector find all five messages, not just the snapshot.

2. **Detect the new opcodes.**

   ```
   pt-research detect-opcodes captures\after-patch.a2cap
   ```

   It looks for the messages by shape (see below), prints each candidate with a confidence, the frame counts and sample
   bytes, and a table with the current registry next to what it found:

   ```
   message                current        detected       result
   PetCollectionSnapshot  0090           1A2B           DIFFERENT
   PetLevelChanged        0990, 1490     0A91, 1591     DIFFERENT
   PetSoulsConsumed       0E90           0F91           DIFFERENT
   PetSoulsGained         0D90           0E91           DIFFERENT
   heartbeat              0036           0036           same
   ```

   Names it could not find (no soul drop in the recording, say) show "not detected (unchanged)". Add `--write opcodes.json`
   to write an override with just the names that differ.

3. **Install the override.** Copy that file to `%AppData%\Aion2PetTracker\opcodes.json`, or open the collection window,
   Settings, **Opcodes**, type the codes in (hex, comma separated; "Save and reload" applies them live). "Reset to defaults"
   removes the override again. Once the new codes are confirmed, also update `data/opcodes.default.json` (and
   `docs/PROTOCOL.md`) so every install gets them.

4. **Verify against what the game showed.** Replay your captures through the same code the app uses and compare with the
   expectations you wrote down:

   ```
   pt-research verify captures\after-patch.a2cap --expect captures\expect.after-patch.json --user-overrides
   ```

   `--user-overrides` makes the command use your `%AppData%` override like the app does. Exit code 0 means every pet level,
   progress number and game-style count matches. Non-zero lists each mismatch.

   Expectation files list what you saw in the game: the collection count ("Collection Status 162/200") and a few pets with
   their level and progress. See [expect.example.json](expect.example.json) for the format. To start from the current replay
   instead of typing it all, run `verify ... --write-expect captures\expect.new.json` and then check the numbers against the
   game before you rely on them. `captures/` is gitignored, because recordings are personal.

5. **Fix what is still wrong.** `verify` failing after the opcodes are right means the *layout* changed, not just the
   numbers. Use `pt-research hex`, `search` and `hist` (RESEARCH-GUIDE.md) on the capture, update the parsers in
   `src/PetTracker.Core/Messages/`, add the finding to PROTOCOL.md, and keep the expectation file as a regression test.
   The xUnit test `PersonalCapturesMatchTheirExpectationsIfPresent` replays every `captures\expect*.json` whose capture files
   exist (in `captures\` or the repo root) on every `dotnet test`. Without them it passes and prints that it skipped.

## How the shape detector works

It ignores opcodes entirely and looks at body layouts (all little-endian, ids checked against `data/pets.json`):

| Message | Shape |
|---|---|
| PetCollectionSnapshot | `u32`, varint N (50 to 400), N x {`u32` id, `u32` same id, `u32` level 1 to 3}, varint M, M x {`u32` id, `u32` progress below 75}, anything after. At least 90% of the ids must be in the catalog. |
| PetSoulsGained / PetSoulsConsumed | `u8` n (1 to 10), n x {`u32` id in the catalog, `u32` value 1 to 200}, body length exactly 1 + 8n. **Consumed** = value 5, 25 or 75 within 1 second after a level change of the same pet. **Gained** = the rest; it also appears without any level change. |
| PetLevelChanged | 23 bytes, `u32` id at offset 2, `u32` level 1 to 3 at offset 6. Two opcodes have this shape today (registration and insight level-up); both are detected. |
| heartbeat | 8-byte body with a Unix-millisecond timestamp, repeated often. |

Candidates are scored by how many frames of that opcode match, and how consistent that is. The snapshot shape is so specific
that one match is enough to propose new codes. If the *layout* inside a message changes, the shape rules may stop matching;
then the manual research routine in RESEARCH-GUIDE.md applies.

## If the framing changed

The frame format is `varint V` + payload of `V - 4` bytes (PROTOCOL.md, C1). If a patch changes it, nothing decodes and the
tracker reports desyncs. Run `pt-research frames <capture> --auto-detect` on a fresh recording: it tries every length mode
and says which one chains the whole stream. Then pass it with `--length-mode` to every research command, and change the
default in `FrameDecoderOptions` once it is confirmed.
