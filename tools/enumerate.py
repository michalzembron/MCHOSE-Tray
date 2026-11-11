"""Lists all HID interfaces of an MCHOSE device (default VID 0x3837)."""
import sys
import hid

VID = int(sys.argv[1], 16) if len(sys.argv) > 1 else 0x3837

for d in hid.enumerate(VID):
    print(
        f"PID={d['product_id']:04X} if={d['interface_number']} "
        f"usage_page=0x{d['usage_page']:04X} usage=0x{d['usage']:04X} "
        f"prod={d['product_string']!r} manuf={d['manufacturer_string']!r} "
        f"serial={d['serial_number']!r}"
    )
    print(f"    path={d['path'].decode(errors='replace')}")
