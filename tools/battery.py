"""Battery reading for MCHOSE A7 V2 Ultra - "native 0x11" protocol.

Channel: usage page 0xFF01, feature report 0x11 (20 data bytes).
Every data byte is XOR-ed with 0xFF.
Request: command 0x06 (status), rest 0x00 (after XOR: F9 FF FF ...).
Reply (after XOR): cmd, vid(LE16), pid(LE16), fw(LE32), flags, battery, charge, ...
Protocol from: github.com/QPRS-6/mchose_battery (src/protocol/native11.rs).
"""
import struct
import sys
import time
import hid

VID = 0x3837  # 0x100B = 2.4 GHz receiver, 0x4019 = mouse on the cable
REPORT_ID = 0x11
DATA_LEN = 20
CMD_STATUS = 0x06
CONN = {0: "USB (cable)", 1: "2.4 GHz", 2: "Bluetooth", 6: "asleep", 7: "off"}


def hexs(b):
    return " ".join(f"{x:02X}" for x in b)


def channels():
    return [d for d in hid.enumerate(VID) if d["usage_page"] == 0xFF01]


def query(h, cmd, verbose=False):
    req = bytes([REPORT_ID]) + bytes(b ^ 0xFF for b in [cmd] + [0] * (DATA_LEN - 1))
    h.set_nonblocking(True)
    while h.read(64):  # drain stale input reports
        pass
    h.send_feature_report(req)
    deadline = time.time() + 2.5
    next_feature = time.time() + 0.25
    while time.time() < deadline:
        inp = h.read(64)
        if inp:
            inp = bytes(inp)
            dec = bytes(b ^ 0xFF for b in inp[1:])
            if verbose:
                print(f"[input] raw:       {hexs(inp)}\n        after XOR: {hexs(dec)}")
            if inp[0] == REPORT_ID and dec[:1] == bytes([cmd]) and any(dec[1:5]):
                return dec
        if time.time() >= next_feature:
            next_feature = time.time() + 0.4
            raw = bytes(h.get_feature_report(REPORT_ID, DATA_LEN + 1))
            data = bytes(b ^ 0xFF for b in raw[1:])
            if verbose:
                print(f"[feature] raw:       {hexs(raw)}\n          after XOR: {hexs(data)}")
            # the echo of the request has VID/PID = 0 - a real reply has a non-zero VID
            if data[:1] == bytes([cmd]) and any(data[1:5]):
                return data
        time.sleep(0.01)
    return None


def main():
    verbose = "-v" in sys.argv
    chans = channels()
    if not chans:
        raise SystemExit("No 0xFF01 channel found - is the mouse/receiver connected?")
    for d in chans:
        print(f"== device {VID:04X}:{d['product_id']:04X}")
        h = hid.device()
        h.open_path(d["path"])
        try:
            data = query(h, CMD_STATUS, verbose)
        finally:
            h.close()
        if data is None:
            print("   no response")
            continue
        cmd, vid, pid, fw, flags, battery, charge = struct.unpack_from("<BHHIBBB", data)
        print(f"   Mouse VID/PID: {vid:04X}:{pid:04X}  firmware: 0x{fw:08X}")
        print(f"   Connection:    {CONN.get(flags & 0x07, f'unknown ({flags & 0x07})')}  (flags=0x{flags:02X})")
        print(f"   Battery:       {battery}%")
        print(f"   Charging:      {charge} (0x{charge:02X})")
        print(f"   Full reply:    {hexs(data)}")


if __name__ == "__main__":
    main()
