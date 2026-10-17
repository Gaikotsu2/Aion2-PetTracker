# Protocol notes

What we know about the Aion 2 (Global NA/EU) network protocol, and the evidence for it. The tool never decrypts
anything and never touches the game process. It only reads server-to-client (S2C) TCP bytes.

## Confirmed (R1, `r1.a2cap`, 2026-10-17)

| # | Fact | Evidence |
|---|------|----------|
| C1 | Frame = unsigned LEB128 varint `V` + payload, where **payload length = V − 4**. Payload = 2-byte opcode + body. Tool mode `fixed:-4`. | All 297,837 world-stream bytes chain into 9,301 frames with zero errors. |
| C2 | Bundle = payload with opcode `FF FF`, then `u32` **little-endian** decompressed size, then the **LZ4 block**. It decompresses to frames under the same V−4 rule. No nested bundles seen. | 1,005 inner frames, all decoded. |
| C3 | Heartbeat = opcode `00 36`, body = 8-byte LE Unix epoch **milliseconds** (server time). On the wire: `0E 00 36 <8 bytes>`. Sent several times a second. | 4,440 heartbeats. |
| C4 | Connections: lobby `193.202.112.47:13700`, then a redirect to world `193.202.112.61:13328`. Changing zone did **not** open a new connection. AION2.exe also holds many TLS (port 443) connections for the launcher, web and CDN, which must be ignored. | r1 flow table: flows 16, 18, 1–41. |
| C5 | S2C world traffic is plaintext apart from LZ4. The first lobby S2C frame looks encrypted or high-entropy after a short header. Lobby traffic isn't needed. | flow 16. |
| C6 | Opcodes are 2 bytes in wire order. The public anchors show up with plausible sizes: `04 38` (damage, 485×), `45 36` (players, 1.3–2.2 KB), `33 36` (character, 2.3 KB, 2× at world entry), `41 36` / `34 36` (spawns), `35 36`, `1e 56`, `01 91` (boss list, 469 B), `06 38`. | `ana chain` opcode histogram. |

## Still open

| # | Hypothesis | Plan |
|---|------------|------|
| H9 | The process is named `AION2`. | It matched pid 23592 in R1. Assumed correct. |
| H10 | Patches renumber opcodes. | Opcodes live in JSON, with a list per message. |
| H11 | Pet, collection, inventory and mail opcodes are undocumented. | R2–R5. Candidate large login frames: `33 36` (2,289 B), `45 36`, `4a 36` (up to 407 B). |

## Wire format

```
frame   = V:varint  payload[V-4]
payload = opcode:2  body
bundle  = payload with opcode FF FF:  size:u32le  LZ4_block(frames...)
opcode  = the two wire bytes as 4 hex digits in wire order ("33 36" -> 3336)
```

## .a2cap capture format (our own)

```
header : "A2CAP" | version u8 | reserved u16 | created i64 (UTC ticks)
record : kind u8 | ticks i64 | payload length u32 | payload      (all little-endian)
kinds  : 1 S2C chunk        payload = flowId i32 + reassembled bytes
         2 marker           payload = number i32 + optional UTF-8 note
         3 connection event payload = event kind u8 + flowId i32 + UTF-8 detail
```

## Pet collection (R2, `r2.a2cap`, 2026-10-17)

| # | Fact | Evidence |
|---|------|----------|
| P1 | **Opcode `00 90` = pet collection snapshot.** It's sent once per login inside the login bundle, about 8 s after world entry. Opening the Pets window sends **nothing**, because the client already has the data. | Present once in r1 (len 4119) and once in r2 (len 4119). No pet traffic around r2 markers M2–M10. |
| P2 | Layout: `u32 ?` (`99 04 00 00` in both captures, so probably account- or character-scoped) · `varint N` · N × `{u32 petId, u32 petId (repeated), u32 level}` · `varint M` · M × `{u32 petId, u32 progress}` · then more (see P5). | N=162, M=134. |
| P3 | Pet IDs are small integers from 1001 up to about 1219. Level is 1–3. **Progress = souls toward the next level**: L1 shows x/25, L2 x/75, L3 is MAX with progress 0. Pets missing from list 2 have progress 0. IDs that appear **only in list 2** are unregistered pets with fewer than 5 souls (1091:4, 1195:4, 1208:2). | Matches the screenshot: L2 44/75 (1171), 8/75 (1170), 25/75 ×4. L1 24/25 (1010), 23/25 ×3, 19/25 (1093), 15/25 (1215), 14/25 ×3. Level counts: L1 82, L2 39, L3 41. |
| P4 | Souls per level: 5 to register, 25 for L1→L2, 75 for L2→L3 (105 total). | Progress bars in the UI. Max seen: L1 24, L2 71. |
| P5 | After list 2 comes `06` + 6 × `{u8 index, u32 value}` (344, 632, 1100, 487, 1000, 46470), then `AA`-delimited blocks. Probably Genus Insight. Not decoded yet. | |
| P6 | **Resolved:** the snapshot holds 162 registered pets, including Special pet 1204 (Pagati). The in-game "Collection Status x/200" excludes the 9 Special pets (cash shop or external), so 162 − 1 = 161. The total of 200 = 209 pets on aion2.th.gl minus 9 Special. Pet IDs match aion2.th.gl `pet_NNNN`. | Game shows 161/200; th.gl lists Special 9, Cogni 41, Natura 48, Fera 65, Varian 46. |
| P7 | Live updates: see R3 below. |

## Live pet events (R3, `r3.a2cap`, 2026-10-17)

Dracuni Herbalist = **pet 1195** (it was 4/5 unregistered in the snapshot). The user received 2 souls and the pet registered (Lv 0 → 1).

| # | Opcode | Layout | Meaning | Evidence |
|---|--------|--------|---------|----------|
| L1 | `0D 90` | `u8 count(=1)` · count × `{u32 petId, u32 amount}` | **Souls gained** (amount is a delta) | 29.829 s `01 AB040000 01000000` and 69.777 s, same bytes, one per soul |
| L2 | `09 90` | `varint entityId (92 09)` · `u32 petId` · `u32 newLevel` · `u8 2` · `u32 6` · `u32 2200` · 6 × `00` | **Pet registered / leveled up** to newLevel. The trailing values may be owned-effect stats. | 29.927 s, right after soul #1 took it to 5/5 |
| L3 | `0E 90` | `u8 count(=1)` · `{u32 petId, u32 value}` | Sent with L2: value 5 = souls consumed by registration (hypothesis). Could also be cumulative souls. | 29.927 s `01 AB040000 05000000` |
| L4 | `08 90` | `varint entityId` · `u16 0` | Acknowledgement sent alongside L2 | 29.927 s |
| L5 | `11 90` | `01 04 01000000 02` | Sent just before the second soul. Possibly Genus Insight. Unknown. | 69.777 s |

Working model: on L1, progress += amount. On L2, level = newLevel and progress −= L3.value (5 for registration, then presumably 25 and 75). The login snapshot (`00 90`) re-syncs everything.
**Confirmed:** the Herbalist shows 1/25 in the UI after R3, so L3 = souls consumed (registration = 5). Overshoot (e.g. 24 + 2 souls) and L2→L3 haven't been observed yet.

## Level 2 → 3 (R4, `r4.a2cap`, 2026-10-17)

Agrint = **pet 1207** (L2 71/75 in the r2 snapshot; 74/75 at the start of r4). One soul took it to MAX.

| Time | Opcode | Bytes | Meaning |
|------|--------|-------|---------|
| 11.184 s | `0D 90` | `01 B7040000 01000000` | +1 soul (75/75) |
| 11.284 s | **`14 90`** | `0000 B7040000 03000000 04 06000000 B8240000 00000000` | **Level up to 3** |
| 11.284 s | `0E 90` | `01 B7040000 4B000000` | 75 souls consumed |

- **Registration (Lv0→1) uses `09 90`, while an insight level-up (here 2→3) uses `14 90`.** Both have the same 23-byte layout: `2 fixed bytes` (`92 09` or `00 00`, not a varint) · `u32 petId` · `u32 newLevel` · `u8` (2 / 4) · `u32 6` · `u32` (2200 / 9400, maybe an owned-effect value) · `u32 0`. L1→L2 hasn't been seen yet; it's presumably `14 90`.
- `0E 90` is confirmed as **souls consumed** (5 for registration, 75 for L2→L3). The rule is: progress += gained; on level-up, progress −= consumed.
- Overshoot (a drop that passes the threshold) still hasn't been observed. The rule above handles it naturally if the server behaves the same way.
