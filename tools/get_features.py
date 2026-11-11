"""Reads (GET_FEATURE) the feature reports declared in the descriptors.
Read-only - writes nothing to the mouse."""
import hid

VID, PID = 0x3837, 0x100B
# usage_page -> [(report_id, data size)]
FEATURES = {
    0xFF01: [(0x11, 20), (0x12, 64), (0x14, 20)],
    0xFF0B: [(0x2A, 60), (0x2B, 60)],
}


def hexs(b):
    return " ".join(f"{x:02X}" for x in b)


for d in hid.enumerate(VID, PID):
    reports = FEATURES.get(d["usage_page"])
    if not reports:
        continue
    h = hid.device()
    h.open_path(d["path"])
    for rid, size in reports:
        try:
            data = h.get_feature_report(rid, size + 1)
            print(f"[{d['usage_page']:04X}] feature 0x{rid:02X}: {hexs(data)}")
        except Exception as e:
            print(f"[{d['usage_page']:04X}] feature 0x{rid:02X}: error {e}")
    h.close()
