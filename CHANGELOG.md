# Changelog

## 2.0 — 2026-11-19

First public release.

**Install:** download `BatteryGuard-Setup-2.0.exe`, run it, click **More info → Run anyway** if SmartScreen appears, approve the admin prompt and click **Install**. Full instructions are in the [README](README.md#install).

### What's in it
- One-file installer (357 KB) for every user on the PC, listed in Apps & features, with an uninstaller that restores Windows' own battery rules.
- Runs as the Windows service **Battery Guard**: starts at boot, keeps working while Battery Saver is on, restarts within seconds if it ever fails, and saves everything when Windows shuts down.
- Voltage-based protection: a warning about 5 minutes before empty, then a countdown, then hibernation, so open work is kept.
- Real charge level and time left from a gauge that learns each battery: it fits the runtime curve to the laptop on the first run-down and counts energy while charging.
- Detects chargers that stop early because the battery claims to be full, predicts each charge and guides you to a full one. This only switches on once it's actually seen on that PC.
- Dashboard, tray icon with the real percentage, interactive history (hover, zoom, drag, every charge) and a complete CSV log with compressed archives.
- Light: about 2–3 MB of memory for the service and under a second of CPU per hour for the tray app, with batched disk writes on battery and instant updates.
- Hardened: only the service and administrators can change its data; users send requests through a restricted inbox folder.
