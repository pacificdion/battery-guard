# Changelog

## 2.1.2 alpha — 2026-11-20 (test build 3)

- **Fixed: learning didn't finish when the battery ran out.** It kept asking for a run down to empty although one had just happened. Now whatever ends a learning run near empty finishes the learning, and the **lowest voltage logged at the end of the run** becomes the new low limit:
  - a sudden power cut (as before);
  - the laptop **switching itself off** (some firmware shuts Windows down cleanly near empty, so there's no power cut for Windows to record);
  - the laptop **hibernating itself** and then being switched on while charging.
  A shutdown from the Start menu, or the service merely restarting, doesn't count.
- **Fixed: a run that started low was thrown away** ("too short to learn from"), e.g. after plugging in for a few minutes near the end. The limit is now learned from it anyway; how long a full charge lasts is learned from the next long run.
- If the run's own record is missing, the run is **rebuilt from the battery log**.
- **Updating mid-learning:** if the battery log already shows a run since learning began that ended with the battery giving out (one 2.1.1 didn't count), it's learned from at the first start-up. No second run-down is needed.
- **Mini countdown:** the "Hibernating soon" window can be shrunk to a small, cute timer that you can drag anywhere. It keeps the Postpone and Hibernate now buttons.
- **Voice (optional):** says "Charging stopped early" with the percentage, and when a full charge will be possible. Turn it on in Settings.
- 27 scenario tests now, adding firmware shutdowns, firmware hibernation, Start-menu shutdowns, service restarts, runs that start low, a lost run record and updating mid-learning.

## 2.1.1 alpha — 2026-11-19 (test build 2)

Fixes from testing 2.1 alpha on a second laptop, plus fully automatic learning.

- **Learning runs on its own.** There are no steps to follow and no "charge to 100%". Until the battery has run all the way down once, Battery Guard doesn't hibernate. Every run on battery is recorded, and whichever one ends with the laptop switching itself off teaches it the battery's real limit.
- **No safeguard during learning.** There's no hibernation at all until it has learned (the 3.0 V stop is gone), so it finds where the battery itself really gives out.
- **Maximum voltage** is taken wherever the charger stops. With a faulty gauge that's often short of 100%, which used to block learning.
- **Fixed: after a run-down, switching on while charging hibernated the laptop again.** Windows' *plugged-in* critical-battery action (often "Hibernate") fired on its wrong ~0% reading. Battery Guard now sets it to do nothing, as on battery. The uninstaller restores Windows' default for both.
- **Fixed:** with the old steps, a battery whose charger stops early never started its learning run. It was then protected normally, so nothing was learned and the limit stayed at 3.42 V.
- **New scenario tests** (`tests\run-tests.ps1`): the real service code against a simulated battery and charger, covering 17 situations and 69 checks, all passing.

## 2.1 alpha — 2026-11-19 (test build)

**Battery Guard now learns each battery's real range.**

- **Learning cycle on every new PC:** charge to 100%, use it on battery until the laptop **switches itself off**, then charge to 100% again. The installer warns about this before installing and again when it finishes; the app guides you through each step.
- From that run it sets the battery's **maximum voltage** (per cell, when full) and **minimum voltage** (where it really gives out). It learns how long a full charge lasts with your typical use, and the capacity from the charge afterwards.
- It then **hibernates just above where the battery really gives out**, leaving time for the countdown and hibernation, but never below 3.30 V per cell.
- **Settings → Learn my battery** runs the cycle again, and becomes **Stop learning** while one is running. *Hibernate at* shows the learned limit.
- Safety net during the run: at 3.0 V per cell (the lowest a lithium cell should go) it hibernates anyway and uses that as the limit.
- Until the battery has been learned, it only hibernates when the voltage really reaches the limit, never early on an estimate.
- The run is recorded to disk every minute, so the power cut at the end loses nothing.

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
