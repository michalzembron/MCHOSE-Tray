# MCHOSE "native 0x11" HID protocol (A7 V2 Ultra)

Status of each item: **verified** = tested on an A7 V2 Ultra (mouse fw 5.44.2.4, receiver fw 5.17.2.0)
and matched against the official M HUB; **vendor** = taken from the official M HUB web app code,
not yet exercised by this project.

Sources: the official M HUB web bundle (`mchose.com.cn`, `main-*.js`: `readMap$1`, `setMap$1`,
`mouseAllReadParser`, `mouseDiffConfig$1`), [alexfrih/mchose-linux](https://github.com/alexfrih/mchose-linux)
(`PROTOCOL.md`), [sbeuwu1/offline-mchose](https://github.com/sbeuwu1/offline-mchose),
[QPRS-6/mchose_battery](https://github.com/QPRS-6/mchose_battery).

## Devices

| USB ID | What |
|---|---|
| `3837:100B` | 2.4 GHz receiver (8K) |
| `3837:100A` | 2.4 GHz receiver (1K variant) — *vendor* |
| `3837:4019` | the mouse itself, over the USB cable |

Each exposes a vendor HID collection with usage page `0xFF01` (the config channel) and one with
`0xFF0B` (firmware update — **never touch**).

## Framing — verified

- Feature report `0x11` carries 20 data bytes, `0x12` carries 64.
- Every data byte is XOR-ed with `0xFF` in both directions.
- Request: `SET_FEATURE [reportId, cmd ^ FF, arg0 ^ FF, …, FF…]`.
- The device signals "reply ready" with input report `0x13` whose de-XOR-ed data starts with `E5 <cmd>`.
- Reply: `GET_FEATURE reportId` → after XOR, byte 0 = `cmd`, the body follows.
  Before the reply is ready the device returns an echo of the request.
- Leave ≥ 25 ms between transactions. No checksum, no "config mode", no save command — writes persist.

## Read commands — verified

| Request (pre-XOR) | Reply body |
|---|---|
| `11 06` status | vid u16, pid u16, fw u32, flags (bits 0-2 connection: 0 USB, 1 2.4G, 2 BT, 6 asleep, 7 off), battery %, charging (0 = on battery, 1 = charging — verified; a separate "full" value has not been observed yet, the app treats 2 or 1 at 100 % as full) |
| `11 03` basics | bond u8, vid u16, pid u16, connect u8, game u8 |
| `11 04` | mouse firmware: len u8 + ASCII (e.g. `5.44.2.4`) |
| `11 04 AA` | receiver firmware: len u8 + ASCII (e.g. `5.17.2.0`) |
| `12 67` | full config block, 63 bytes — see below |

### Config block (`12 67`), little-endian

| Offset | Field |
|---|---|
| 0 | profile index |
| 1 | low nibble: USB (cable) DPI stage, high nibble: USB polling rate index |
| 2 | low nibble: 2.4G DPI stage, high nibble: 2.4G polling rate index |
| 3 | reserved |
| 4–15 | DPI stage 0–5, u16 (X axis) |
| 16 | number of enabled DPI stages |
| 17 | sensor bits: 0-1 LOD (0 = 0.7 mm, 1 = 1 mm, 2 = 2 mm), 2 ripple control, 3 angle snapping, 4 motion sync, 7/6 mode (0x = Performance, 10 = Gaming, 11 = Extreme) |
| 18 | key debounce, ms |
| 19 | sleep, minutes (0 = never) |
| 20–43 | 6 buttons × 4 bytes (index/type nibbles + 3-byte value; e.g. `E9`/`EA` = consumer volume up/down) |
| 49 | sensor rotation, signed degrees |
| 50 | unknown (128 observed) |
| 51–62 | DPI stage 0–5, u16 (Y axis) |

Byte 1 vs byte 2: **verified** by writing the 2.4G polling rate through the receiver — only byte 2
changed. The M HUB read parser labels them the other way round (alexfrih's L7 Pro notes are right).

Polling rate index → Hz: `0 = 125, 1 = 500, 2 = 1000, 3 = 2000, 4 = 4000, 5 = 8000`
(the 1K receiver only offers the first three).

## Value ranges (from the M HUB UI)

| Setting | Range |
|---|---|
| DPI | 200 – 42000, up to 6 stages, X and Y can differ |
| Polling rate | 125 / 500 / 1000 / 2000 / 4000 / 8000 Hz (8K receiver and cable) |
| LOD | 0.7 / 1 / 2 mm |
| Key debounce | 0 – 20 ms (confirmed in M HUB) |
| Sleep | 0 – 100 min (confirmed in M HUB) plus a "never sleep" switch (= 0) |
| Sensor rotation | −30° … +30°, 1° steps |
| Mode | Performance / Gaming / Extreme — offered from mouse firmware 5.44.2.4 |
| Motion sync, angle snapping, ripple control | on / off |

## Write commands

Each write is acknowledged with input report `0x13` `E5 <cmd>`. **Verified** = sent through the
2.4 GHz receiver; the whole config block was diffed before/after and only the listed bytes changed.

| Request (pre-XOR) | Effect | Status |
|---|---|---|
| `11 43 ms` | key debounce 0–20 → byte 18 | verified |
| `11 0A status minutes` | sleep: `01 m` (1–100 min), `00 00` = never → byte 19 | verified |
| `11 41 usbRate freeRate` | polling rate index: `(0, idx)` through the receiver → byte 2 high nibble; `(idx, 0)` on the cable → byte 1 high nibble | verified on both links |
| `11 42 lod ripple line motionSync 0 0 gameMode rotateOpen rotateVal` | see below | verified |
| `12 40 usbIdx gIdx 00 dpi0..5 count FF dpiY0..5` | DPI stages (u16 LE, X then Y), enabled count, active stage → bytes 1/2 low nibble, 4–16, 51–62 | verified (stage value change) |
| `11 40 usbIdx gIdx 00 dpi0..5 count` | DPI without Y axis (M HUB uses `12 40`) | vendor |
| `11 58 idx` | switch onboard profile | vendor |
| `12 52 btn 00 type value(u24)` | button mapping | vendor |

`11 42` details (as M HUB builds it): `lod` is **always** the current LOD index (0 = 0.7 mm, so it
can't mean "unchanged"); `ripple`/`line` (angle snapping)/`motionSync`: 1 = on, 2 = off, 0 = unchanged;
`gameMode`: 1/2/3 = Performance/Gaming/Extreme, 0 = unchanged; rotation is applied only with
`rotateOpen = 1`, `rotateVal` is a signed byte (−30…30). Verified effects on byte 17: ripple `0x04`,
angle snapping `0x08`, motion sync `0x10`, mode Performance `0x00` / Gaming `0x80` / Extreme `0xC0`,
LOD bits 0–1; rotation → byte 49.

## Never send

- `11 0B …` — reset; `11 0B 00 AA` is a **factory recovery**.
- `12 57 …` — whole-config write; the vendor itself guards against it remapping the left button.
- Anything on the `0xFF0B` collection (reports `0x2A`–`0x2D`) — firmware update (DFU).
