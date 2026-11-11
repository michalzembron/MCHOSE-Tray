"""Opens the vendor channels (usage_page >= 0xFF00), prints their report descriptors
and listens for a few seconds for reports the mouse sends on its own.
Sends NOTHING to the device."""
import sys
import time
import hid

VID, PID = 0x3837, 0x100B
LISTEN_S = float(sys.argv[1]) if len(sys.argv) > 1 else 5.0


def hexs(b):
    return " ".join(f"{x:02X}" for x in b)


devs = [d for d in hid.enumerate(VID, PID) if d["usage_page"] >= 0xFF00]
handles = []
for d in devs:
    h = hid.device()
    try:
        h.open_path(d["path"])
    except OSError as e:
        print(f"[FF{d['usage_page']:04X}] cannot open: {e}")
        continue
    h.set_nonblocking(True)
    tag = f"{d['usage_page']:04X}/{d['usage']:04X}"
    print(f"== {tag} opened")
    try:
        desc = h.get_report_descriptor()
        print(f"   descriptor ({len(desc)} B): {hexs(desc)}")
    except Exception as e:
        print(f"   descriptor unavailable: {e}")
    handles.append((tag, h))

print(f"\nListening {LISTEN_S}s (move the mouse / click)...")
end = time.time() + LISTEN_S
while time.time() < end:
    for tag, h in handles:
        data = h.read(1024)
        if data:
            print(f"[{tag}] {len(data)} B: {hexs(data)}")
    time.sleep(0.005)

for _, h in handles:
    h.close()
