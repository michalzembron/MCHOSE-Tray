"""READ-ONLY: dumps the mouse configuration (DPI, polling rate, LOD, ...).

Commands used (all reads):
  0x11 0x03  basic info
  0x11 0x04  version string
  0x12 0x67  full config block (63 bytes)

Framing: [reportId, cmd^FF, args^FF, FF...]; reply via GET_FEATURE on the same report:
raw[1]^FF == cmd, body[i] = raw[i+2]^FF. The device signals "reply ready" with an input
report 0x13 whose de-XOR-ed data starts with E5 <cmd>.
Never send 0x11 0x0B (reset / factory recovery) or anything on the 0xFF0B collection (DFU).
"""
import sys
import time
import hid

VID = 0x3837
SIZES = {0x11: 20, 0x12: 64}
RATES = ["125", "500", "1000", "2000", "4000", "8000"]
READ_ONLY_CMDS = {(0x11, 0x03), (0x11, 0x04), (0x11, 0x06), (0x12, 0x67)}


def hexs(b):
    return " ".join(f"{x:02X}" for x in b)


def query(h, rid, cmd, args=(), verbose=False):
    assert (rid, cmd) in READ_ONLY_CMDS, "only whitelisted read commands allowed"
    n = SIZES[rid]
    payload = [cmd, *args] + [0] * (n - 1 - len(args))
    h.set_nonblocking(True)
    while h.read(64):
        pass
    h.send_feature_report(bytes([rid]) + bytes(b ^ 0xFF for b in payload))
    deadline = time.time() + 2.0
    ready = False
    while time.time() < deadline and not ready:
        inp = h.read(64)
        if inp:
            dec = bytes(b ^ 0xFF for b in inp[1:])
            if verbose:
                print(f"   [input 0x{inp[0]:02X}] {hexs(dec[:8])}")
            if dec[:2] == bytes([0xE5, cmd]):
                ready = True
        else:
            time.sleep(0.005)
    if verbose and not ready:
        print("   (no ready signal, reading anyway)")
    for _ in range(6):
        raw = bytes(h.get_feature_report(rid, n + 1))
        dec = bytes(b ^ 0xFF for b in raw[1:])
        if dec[0] == cmd:
            return dec[1:]
        time.sleep(0.05)
    return None


def parse_config(b):
    print(f"   profile index : {b[0]}")
    for i, name in ((1, "byte1"), (2, "byte2")):
        print(f"   {name}         : rate idx {b[i] >> 4} ({RATES[b[i] >> 4] if b[i] >> 4 < len(RATES) else '?'} Hz), "
              f"dpi stage {b[i] & 0x0F}")
    dpis = [b[4 + 2 * i] | b[5 + 2 * i] << 8 for i in range(6)]
    print(f"   dpi stages    : {dpis}  (enabled: {b[16]})")
    s = b[17]
    lod = {0: "0.7 mm", 1: "1 mm", 2: "2 mm"}.get(s & 3, f"? ({s & 3})")
    print(f"   sensor bits   : 0x{s:02X}  LOD {lod}, ripple {s >> 2 & 1}, angle snap {s >> 3 & 1}, "
          f"motion sync {s >> 4 & 1}")
    mode = "Performance" if not s & 0x80 else ("Extreme" if s & 0x40 else "Gaming")
    print(f"   mode          : {mode}  (bits 7/6 = {s >> 7 & 1}/{s >> 6 & 1})")
    print(f"   debounce      : {b[18]} ms")
    print(f"   sleep         : {b[19]} min")
    for i in range(6):
        btn = b[20 + 4 * i: 24 + 4 * i]
        print(f"   button {i}      : type {btn[0] >> 4} idx {btn[0] & 15} value {hexs(btn[1:])}")
    rot = b[49] - 256 if b[49] > 30 else b[49]
    print(f"   rotate        : {rot}")
    print(f"   byte50 (val)  : {b[50]}")
    print(f"   dpi Y         : {[b[51 + 2 * i] | b[52 + 2 * i] << 8 for i in range(6)]}")


def main():
    verbose = "-v" in sys.argv
    chans = [d for d in hid.enumerate(VID) if d["usage_page"] == 0xFF01]
    for d in chans:
        print(f"== device {VID:04X}:{d['product_id']:04X}")
        h = hid.device()
        h.open_path(d["path"])
        try:
            st = query(h, 0x11, 0x06, verbose=verbose)
            if st is None or not any(st[:4]):
                print("   no response (skipping)")
                continue
            time.sleep(0.03)
            basic = query(h, 0x11, 0x03, verbose=verbose)
            print(f"   basics  : {hexs(basic) if basic else 'no reply'}")
            time.sleep(0.03)
            ver = query(h, 0x11, 0x04, verbose=verbose)
            if ver:
                print(f"   version : {ver[1:1 + ver[0]].decode('ascii', 'replace')!r}  raw {hexs(ver)}")
            time.sleep(0.03)
            cfg = query(h, 0x12, 0x67, verbose=verbose)
            if cfg is None:
                print("   config  : no reply")
                continue
            print(f"   config raw ({len(cfg)} B): {hexs(cfg)}")
            parse_config(cfg)
        finally:
            h.close()


if __name__ == "__main__":
    main()
