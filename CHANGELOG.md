# Changelog

## 2.1.3 — 2026-11-20

The first stable release since 2.0. It brings battery learning (tested in the 2.1 alpha builds below) to everyone, plus the changes listed here.

- **New license.** Battery Guard is now **copyrighted, all rights reserved**, instead of GPL-3.0. It stays free to download and use, at home or at work, but the program and its source code may not be copied, shared, sold, modified or reused without written permission. See [LICENSE](LICENSE), which is also installed as `LICENSE.txt` next to the programs. Versions up to 2.1.2 remain under the GPL-3.0.
- **Updating from 2.0** keeps your protection and settings as they are. To learn the battery's real range, choose **Settings → Learn my battery**. New installs learn it on their own.
- **Power mode flyout.** Clicking the tray icon opens a small panel above the taskbar, like Windows' own battery flyout. It shows the real charge, the time left (or time to full) and what Battery Guard is doing. It also has Windows' **power mode** slider: Battery saver (on battery only), Better battery, Better performance and Best performance.
  - Drag or click the slider, or use the arrow keys; Esc or a click elsewhere closes it.
  - It follows the taskbar's light/dark setting and uses your Windows accent colour.
  - It's drawn in a single window, so it opens in about a tenth of a second and redraws in about 2 ms.
  - Battery saver is switched through its own "turn on automatically at" level, and your previous level is put back afterwards.
  - On a plan other than Balanced (where Windows has no power modes) it offers to switch to Balanced.
  - The dashboard opens from the flyout's *Battery Guard* button or the right-click menu.
- **New voice.** "Charging stopped early." is now a short, natural female voice (a built-in clip made with the open-source Kokoro voice, Apache-2.0) instead of Windows' robotic one. The tray app no longer loads Windows' speech engine.
- **Learn my battery learns from the next run, never an earlier one.**
  - Pressed while plugged in: the next time you unplug starts the learning run.
  - Pressed on battery: that run is protected as usual; learning starts at the next plug-in, and the run after that is the one learned.
  - Stop learning cancels it either way.
- **Fixed: learning could finish without a word.** 2.1.2 could finish learning at start-up from an earlier run, and the "learning complete" notice was skipped when the tray app had just restarted. Every new result is now announced once per user. It also says which run it came from, and the dashboard shows the learned limit (where the battery gives out, and where it now hibernates).
- The look-back over the battery log (for learning carried over from older versions) only uses a run that began after learning did.

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
