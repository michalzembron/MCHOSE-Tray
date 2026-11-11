# Third-party notices

MCHOSE Tray uses or includes the following third-party work.

| Component | Use | License |
|---|---|---|
| [HidSharp](https://www.zer7.com/software/hidsharp) | USB HID access (NuGet package) | Apache License 2.0 |
| [H.NotifyIcon](https://github.com/HavenDV/H.NotifyIcon) | System tray icon (NuGet package) | MIT |
| [Material Design Icons](https://github.com/google/material-design-icons) — "bolt" | Charging indicator shape in the tray icon (`TrayIconRenderer.cs`) | Apache License 2.0 |

The HID protocol description in `docs/PROTOCOL.md` builds on the reverse-engineering work of
[QPRS-6/mchose_battery](https://github.com/QPRS-6/mchose_battery),
[alexfrih/mchose-linux](https://github.com/alexfrih/mchose-linux) and
[sbeuwu1/offline-mchose](https://github.com/sbeuwu1/offline-mchose), and was verified on real
hardware for this project. No code from those projects is included.

"MCHOSE" is a trademark of its owner. This project is not affiliated with or endorsed by MCHOSE.
