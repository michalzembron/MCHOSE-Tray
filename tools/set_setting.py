"""WRITE TEST for single mouse settings. Writes one value, re-reads the whole config block
and prints every byte that changed, so any side effect is visible immediately.

Usage:
  python set_setting.py debounce <0-20>
  python set_setting.py sleep <0-100>          (0 = never sleep)
  python set_setting.py rate-24g <0-5>         (125/500/1000/2000/4000/8000 Hz, via receiver)
  python set_setting.py rate-usb <0-5>         (same, on the cable)
  python set_setting.py lod <0-2>              (0.7 / 1 / 2 mm)
  python set_setting.py ripple|angle-snap|motion-sync <0|1>
  python set_setting.py mode <0-2>             (Performance / Gaming / Extreme)
  python set_setting.py rotation <-30..30>     (degrees)
  python set_setting.py dpi1 <200-42000>       (first DPI stage, X and Y)

Only the commands listed in SETTINGS can be sent. Never add 0x0B (reset), 0x57 (whole-config
write) or anything on the 0xFF0B collection (firmware update).
"""
import sys
import time
import hid

sys.path.insert(0, __file__.rsplit("\\", 1)[0].rsplit("/", 1)[0])
import read_config as rc  # noqa: E402  (read-only helpers)

VID = 0x3837


def sensor(field, value, cfg):
    """0x42 args: lod ripple line motionSync 0 0 gameMode rotateOpen rotateVal.
    Like M HUB: LOD is always the current value (0 means 0.7 mm, not "unchanged");
    toggles 1 = on, 2 = off, 0 = unchanged; gameMode 0 = unchanged; rotation only with rotateOpen = 1."""
    args = [cfg[17] & 0x03, 0, 0, 0, 0, 0, 0, 0, 0]
    if field == "lod":
        args[0] = value
    elif field in ("ripple", "angle-snap", "motion-sync"):
        args[{"ripple": 1, "angle-snap": 2, "motion-sync": 3}[field]] = 1 if value else 2
    elif field == "mode":
        args[6] = value + 1  # 0/1/2 = Performance/Gaming/Extreme -> 1/2/3
    elif field == "rotation":
        args[7], args[8] = 1, value & 0xFF
    return args


def dpi_first_stage(value, cfg):
    """0x12 0x40 args (as M HUB): usbIdx gIdx 00 dpi0..5 (u16 LE, X) count FF dpiY0..5 (u16 LE).
    Copies every stage from the current config and only replaces stage 1 (X and Y)."""
    x = [cfg[4 + 2 * i] | cfg[5 + 2 * i] << 8 for i in range(6)]
    y = [cfg[51 + 2 * i] | cfg[52 + 2 * i] << 8 for i in range(6)]
    x[0] = y[0] = value
    usb_idx, g_idx = cfg[1] & 0x0F, cfg[2] & 0x0F
    args = [usb_idx, g_idx, 0]
    for v in x:
        args += [v & 0xFF, v >> 8]
    args += [cfg[16], 0xFF]
    for v in y:
        args += [v & 0xFF, v >> 8]
    return args


# name -> (command, valid range, args builder(value, current config), config byte offset to show)
SETTINGS = {
    "debounce": (0x43, range(0, 21), lambda v, c: [v], 18),
    "sleep": (0x0A, range(0, 101), lambda v, c: [1 if v else 0, v], 19),
    # polling rate index 0-5 = 125/500/1000/2000/4000/8000 Hz; M HUB sends (usb, 0) on the cable
    # and (0, 2.4G) on the receiver
    "rate-usb": (0x41, range(0, 6), lambda v, c: [v, 0], 1),
    "rate-24g": (0x41, range(0, 6), lambda v, c: [0, v], 2),
    "lod": (0x42, range(0, 3), lambda v, c: sensor("lod", v, c), 17),            # 0.7 / 1 / 2 mm
    "ripple": (0x42, range(0, 2), lambda v, c: sensor("ripple", v, c), 17),
    "angle-snap": (0x42, range(0, 2), lambda v, c: sensor("angle-snap", v, c), 17),
    "motion-sync": (0x42, range(0, 2), lambda v, c: sensor("motion-sync", v, c), 17),
    "mode": (0x42, range(0, 3), lambda v, c: sensor("mode", v, c), 17),          # Performance/Gaming/Extreme
    "rotation": (0x42, range(-30, 31), lambda v, c: sensor("rotation", v, c), 49),
    "dpi1": (0x40, range(200, 42001), dpi_first_stage, 4),   # sent on report 0x12
}
LONG_REPORT_CMDS = {0x40}


def write(h, cmd, args):
    rid, size = (0x12, 64) if cmd in LONG_REPORT_CMDS else (0x11, 20)
    payload = [cmd, *args] + [0] * (size - 1 - len(args))
    h.set_nonblocking(True)
    while h.read(64):
        pass
    h.send_feature_report(bytes([rid]) + bytes(b ^ 0xFF for b in payload))
    deadline = time.time() + 1.5
    while time.time() < deadline:
        inp = h.read(64)
        if inp:
            dec = bytes(b ^ 0xFF for b in inp[1:])
            if dec[:2] == bytes([0xE5, cmd]):
                return True
        else:
            time.sleep(0.005)
    return False


def main():
    name, value = sys.argv[1], int(sys.argv[2])
    cmd, valid, build, offset = SETTINGS[name]
    if value not in valid:
        raise SystemExit(f"{name}: value must be in {valid.start}-{valid.stop - 1}")

    for d in [d for d in hid.enumerate(VID) if d["usage_page"] == 0xFF01]:
        h = hid.device()
        h.open_path(d["path"])
        try:
            st = rc.query(h, 0x11, 0x06)
            if st is None or not any(st[:4]):
                continue
            print(f"== device {VID:04X}:{d['product_id']:04X}")
            time.sleep(0.03)
            before = rc.query(h, 0x12, 0x67)
            args = build(value, before)
            time.sleep(0.03)
            ack = write(h, cmd, args)
            print(f"   write {cmd:02X} {' '.join(f'{a:02X}' for a in args)}: {'acknowledged' if ack else 'NO ACK'}")
            time.sleep(0.1)
            after = rc.query(h, 0x12, 0x67)
            print(f"   {name}: byte {offset} {before[offset]} -> {after[offset]}")
            diff = [(i, f"{before[i]:02X}->{after[i]:02X}") for i in range(len(before)) if before[i] != after[i]]
            print(f"   changed bytes: {diff or 'none'}")
            return
        finally:
            h.close()
    raise SystemExit("mouse not found")


if __name__ == "__main__":
    main()
