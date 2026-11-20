// Battery Guard - Copyright (C) 2026 Dion Fabian. All rights reserved. See LICENSE.
// Scenario tests. They run the real service code (Core and FuelGauge) against a simulated laptop
// battery and charger, with the clock fast-forwarded, through the situations a laptop goes
// through: new installs, chargers that stop early, power cuts, sleep, restarts, upgrades.
// Each scenario checks what the service did. Run with tests\run-tests.ps1.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace BatteryGuard.Tests
{
    // A 3-cell laptop battery and its charger, minute by minute. The battery's own gauge (what
    // Windows reads) believes a full charge is 4.35 Wh; the cells really hold 34 Wh.
    class SimBattery
    {
        public double CapacityWh = 34, EnergyWh = 17;
        public double CutoffCellMv = 3250;      // the pack cuts the power here, under load
        public double LoadW = 9;                // use on battery
        public double ChargeW = 30;
        public double ChargerStopsAtSoc = 1.0;  // a faulty gauge stops the charger here
        public double FirmwareOffCellMv;        // some laptops switch themselves off (cleanly) here instead
        public double FirmwareHibernateCellMv;  // ...or hibernate here
        public bool OnAC, ChargerOn, Dead, SwitchedOff, ForcedHibernate;
        public double CounterMwh = 2000, CounterFullMwh = 4351;
        const int Cells = 3;

        static readonly double[] SocPoints = { 0, 0.02, 0.05, 0.10, 0.20, 0.30, 0.40, 0.50, 0.60, 0.70, 0.80, 0.90, 1.0 };
        static readonly double[] OcvPoints = { 3000, 3300, 3450, 3550, 3620, 3660, 3700, 3750, 3800, 3870, 3950, 4050, 4180 };

        public double Soc { get { return EnergyWh / CapacityWh; } }

        public static double Ocv(double soc)
        {
            if (soc <= 0) return OcvPoints[0];
            for (int i = 1; i < SocPoints.Length; i++)
                if (soc <= SocPoints[i])
                    return OcvPoints[i - 1] + (OcvPoints[i] - OcvPoints[i - 1]) * (soc - SocPoints[i - 1]) / (SocPoints[i] - SocPoints[i - 1]);
            return OcvPoints[OcvPoints.Length - 1];
        }

        public double CellMv
        {
            get
            {
                if (!OnAC) return Ocv(Soc) - 45 * LoadW / 9;   // sags under load
                return ChargerOn ? Math.Min(4200, Ocv(Soc) + 60) : Ocv(Soc);
            }
        }

        public void Advance(double minutes, bool awake)
        {
            if (Dead) return;
            double h = minutes / 60;
            if (OnAC)
            {
                if (!ChargerOn) return;
                double w = Soc < 0.85 ? ChargeW : ChargeW * Math.Max(0.08, (1 - Soc) / 0.15);
                EnergyWh = Math.Min(CapacityWh, EnergyWh + w * h);
                CounterMwh = Math.Min(CounterFullMwh, CounterMwh + w * h * 1000);
                if (Soc >= ChargerStopsAtSoc - 0.001 || Soc >= 0.999) ChargerOn = false;
                return;
            }
            double drain = awake ? LoadW : 0.8;
            EnergyWh = Math.Max(0, EnergyWh - drain * h);
            CounterMwh = Math.Max(0, CounterMwh - drain * h * 1000);
            if (awake && CellMv <= CutoffCellMv) Dead = true; // the pack's protection cuts the power
            else if (awake && CellMv <= FirmwareOffCellMv) SwitchedOff = true;
            else if (awake && CellMv <= FirmwareHibernateCellMv) ForcedHibernate = true;
        }

        public void PlugIn()
        {
            OnAC = true;
            ChargerOn = Soc < ChargerStopsAtSoc - 0.01 && Soc < 0.995;
        }

        public void Unplug()
        {
            OnAC = false;
            ChargerOn = false;
        }

        public BatteryReading Reading()
        {
            return new BatteryReading
            {
                OnAC = OnAC,
                Charging = ChargerOn,
                VoltageMv = (int)(CellMv * Cells),
                RateMw = OnAC ? (ChargerOn ? (int)(ChargeW * 1000 * (Soc < 0.85 ? 1 : Math.Max(0.08, (1 - Soc) / 0.15))) : 0) : -11577, // the discharge rate is frozen, as on the real battery
                RemainingMwh = (int)CounterMwh,
                WindowsPercent = (int)Math.Round(Math.Min(100, CounterMwh / CounterFullMwh * 100)),
                Source = "sim",
            };
        }
    }

    // One laptop: the battery, the clock, and the service running on it.
    class Laptop
    {
        public readonly SimBattery Battery = new SimBattery();
        public readonly string Dir;
        public DateTime Now = new DateTime(2026, 11, 20, 8, 0, 0, DateTimeKind.Utc);
        public int Hibernations;
        public bool Hibernated;
        public double HibernatedAtCellMv = -1, HibernatedWithMinutesLeft = -1;
        public KeyValuePair<Guid, uint>[] PluggedInSettings;
        Core core;
        ulong totalMs, awakeMs;
        bool powerCutBeforeBoot, userShutdownBeforeBoot;

        public Laptop(string name)
        {
            Dir = Path.Combine(Path.GetTempPath(), "bg-scenarios", name);
            if (Directory.Exists(Dir)) Directory.Delete(Dir, true);
            Directory.CreateDirectory(Dir);
            File.WriteAllText(Path.Combine(Dir, "settings.ini"), "Enabled=1\r\nFloorPerCellMv=3500\r\nWarnMinutes=5\r\nCountdownSeconds=60\r\nAllowPostpone=1\r\nHideWindowsWarnings=1\r\nCells=3\r\nDryRun=1\r\n");
        }

        void Hook()
        {
            Paths.Data = Dir;
            Paths.Custom = true;
            Clock.Source = () => Now;
            TestHooks.Read = () => Battery.Reading();
            TestHooks.TotalMs = totalMs;
            TestHooks.AwakeMs = awakeMs;
            TestHooks.DesignMwh = 48840;
            TestHooks.FullMwh = 4351;
            bool cut = powerCutBeforeBoot;
            TestHooks.PowerCutAfter = delegate(DateTime last) { return cut; };
            bool user = userShutdownBeforeBoot;
            TestHooks.UserShutdownAfter = delegate(DateTime last) { return user; };
            TestHooks.Hibernated = delegate
            {
                Hibernations++;
                Hibernated = true;
                HibernatedAtCellMv = Battery.CellMv;
                HibernatedWithMinutesLeft = MinutesToCutoff();
            };
            TestHooks.PowerSettings = delegate(KeyValuePair<Guid, uint>[] dc, KeyValuePair<Guid, uint>[] ac) { PluggedInSettings = ac; };
        }

        // Minutes of use left before the pack would cut out, at the current load.
        public double MinutesToCutoff()
        {
            var copy = new SimBattery { CapacityWh = Battery.CapacityWh, EnergyWh = Battery.EnergyWh, CutoffCellMv = Battery.CutoffCellMv, LoadW = Battery.LoadW };
            double m = 0;
            while (!copy.Dead && m < 1000) { copy.Advance(0.25, true); m += 0.25; }
            return m;
        }

        // Switches the laptop on (a fresh start of the service).
        public void Boot(bool afterPowerCut) { Boot(afterPowerCut, false); }

        void Boot(bool afterPowerCut, bool afterUserShutdown)
        {
            powerCutBeforeBoot = afterPowerCut;
            userShutdownBeforeBoot = afterUserShutdown;
            Battery.Dead = Battery.SwitchedOff = Battery.ForcedHibernate = false;
            Hibernated = false;
            totalMs = awakeMs = 0;
            Hook();
            core = new Core();
            core.Start();
        }

        void Pass(double seconds, bool awake)
        {
            Battery.Advance(seconds / 60, awake);
            Now = Now.AddSeconds(seconds);
            totalMs += (ulong)(seconds * 1000);
            if (awake) awakeMs += (ulong)(seconds * 1000);
            TestHooks.TotalMs = totalMs;
            TestHooks.AwakeMs = awakeMs;
        }

        // Uses the laptop for this long (or until the battery gives out or it hibernates).
        public void Use(double minutes)
        {
            double left = minutes * 60;
            while (left > 0.01 && !Battery.Dead && !Battery.SwitchedOff && !Battery.ForcedHibernate && !Hibernated)
            {
                double step = Math.Min(left, core.NextWaitMs / 1000.0);
                Pass(step, true);
                if (Battery.Dead || Battery.SwitchedOff || Battery.ForcedHibernate) break;
                core.Step();
                left -= step;
            }
        }

        public void UseUntilItStops(double maxMinutes) { Use(maxMinutes); }

        public void Sleep(double minutes)
        {
            Pass(minutes * 60, false);
            core.Step();
        }

        // After a hibernation: off for a while, then switched on again.
        public void Resume(double minutesOff)
        {
            Now = Now.AddMinutes(minutesOff);
            totalMs += (ulong)(minutesOff * 60000);
            TestHooks.TotalMs = totalMs;
            Hibernated = Battery.ForcedHibernate = false;
            core.Step();
        }

        // Off with no clean shutdown (or the battery gave out): time passes, then it starts again.
        public void PowerCutThenBoot(double minutesOff, bool plugIn)
        {
            Now = Now.AddMinutes(minutesOff);
            if (plugIn) Battery.PlugIn();
            Boot(true);
        }

        // A normal shutdown (from the Start menu), then it starts again later.
        public void ShutdownThenBoot(double minutesOff)
        {
            Now = Now.AddMinutes(minutesOff);
            Boot(false, true);
        }

        // The laptop's firmware switched it off cleanly (no power cut, nobody shut it down).
        public void SwitchedOffThenBoot(double minutesOff, bool plugIn)
        {
            Now = Now.AddMinutes(minutesOff);
            if (plugIn) Battery.PlugIn();
            Boot(false, false);
        }

        // Only the service restarts (an update, a crash): Windows keeps running.
        public void RestartService()
        {
            Now = Now.AddSeconds(5);
            totalMs += 5000;
            awakeMs += 5000;
            Hook();
            core = new Core();
            core.Start();
        }

        public string LearnRunFile { get { return Path.Combine(Dir, "learn-run.csv"); } }

        // What 2.1.1 left behind when it didn't recognise how a run ended: it had already started
        // up once (time passing), dropped the run, and the run's file was overwritten.
        public void ForgetHowTheRunEnded(double minutesLater)
        {
            Now = Now.AddMinutes(minutesLater);
            string state = Path.Combine(Dir, "state.ini");
            string text = File.ReadAllText(state);
            text = System.Text.RegularExpressions.Regex.Replace(text, @"(?m)^LastOnBattery=.*$", "LastOnBattery=0");
            text = System.Text.RegularExpressions.Regex.Replace(text, @"(?m)^LearnRunMin=.*$", "LearnRunMin=-1");
            text = System.Text.RegularExpressions.Regex.Replace(text, @"(?m)^LearnSince=.*\r?\n", "");
            File.WriteAllText(state, text);
            File.WriteAllText(LearnRunFile, "0.00,3700\r\n");
        }

        public void Command(string text)
        {
            Commands.Send(text);
            core.Step();
        }

        public void PlugIn() { Battery.PlugIn(); core.Step(); }
        public void Unplug() { Battery.Unplug(); core.Step(); }

        public Dictionary<string, string> Ini(string file) { return BatteryGuard.Ini.Read(Path.Combine(Dir, file)) ?? new Dictionary<string, string>(); }
        public string Val(string file, string key) { string v; return Ini(file).TryGetValue(key, out v) ? v : ""; }
        public string LogText { get { string p = Path.Combine(Dir, "log.txt"); return File.Exists(p) ? File.ReadAllText(p) : ""; } }
        public int FloorMv { get { return int.Parse(Val("settings.ini", "FloorPerCellMv"), CultureInfo.InvariantCulture); } }
        public bool Learning { get { return Val("state.ini", "Learn") != ""; } }
    }

    static class Program
    {
        static int passed, failed;
        static readonly StringBuilder report = new StringBuilder();
        static string current;

        static void Check(bool ok, string what)
        {
            if (ok) passed++; else failed++;
            report.AppendLine((ok ? "  PASS  " : "  FAIL  ") + what);
        }

        static string[] only; // scenario numbers given on the command line: run just those

        static void Scenario(string name, Action body)
        {
            if (only != null && !Array.Exists(only, n => name.StartsWith(n + ".", StringComparison.Ordinal))) return;
            current = name;
            report.AppendLine();
            report.AppendLine(name);
            try { body(); }
            catch (Exception ex)
            {
                failed++;
                report.AppendLine("  FAIL  crashed: " + ex);
            }
            TestHooks.Reset();
            Clock.Source = null;
        }

        static string Mv(double mv) { return (mv / 1000).ToString("0.000", CultureInfo.InvariantCulture) + " V"; }

        static int Main(string[] args)
        {
            if (args.Length > 0) only = args;
            // Charge fully, unplug, use it until the battery gives out, start it on the charger.
            Scenario("1. New PC, normal charger: learns on its own from the first run-down", delegate
            {
                var pc = new Laptop("s1");
                pc.Battery.EnergyWh = 17;
                pc.Battery.PlugIn();
                pc.Boot(false);
                Check(pc.Learning, "learning starts by itself on a new PC");
                pc.Use(120);
                Check(!pc.Battery.ChargerOn, "charger finished");
                pc.Unplug();
                pc.Use(400);
                Check(pc.Battery.Dead, "the run went on until the battery gave out (" + Mv(pc.Battery.CellMv) + ")");
                Check(pc.Hibernations == 0, "no hibernation while learning");
                Check(!pc.LogText.Contains("Countdown"), "no countdown while learning");
                Check(pc.LogText.Contains("close to empty"), "warned near the end");
                pc.PowerCutThenBoot(30, true);
                Check(!pc.Learning, "learning complete after the power cut");
                Check(pc.Val("settings.ini", "FloorLearned") == "1", "hibernate point set from the learned limit");
                int floor = pc.FloorMv, cut = int.Parse(pc.Val("state.ini", "CutoffMv"));
                Check(Math.Abs(cut - pc.Battery.CutoffCellMv) < 60, "true limit learned: " + Mv(cut) + " (pack cuts at " + Mv(pc.Battery.CutoffCellMv) + ")");
                Check(floor >= 3300 && floor > cut + 40, "hibernates at " + Mv(floor) + ", above the limit, not below 3.30 V");
                double full = double.Parse(pc.Val("gauge.ini", "FullCellMv"), CultureInfo.InvariantCulture);
                Check(full > 4050 && full < 4200, "maximum voltage learned: " + Mv(full));
                Check(pc.Val("gauge.ini", "Calibrated") == "1", "runtime curve learned");
                Check(pc.PluggedInSettings != null && Array.Exists(pc.PluggedInSettings, kv => kv.Key == new Guid("637ea02f-bbcb-4015-8e2c-a1c7b9c0b546") && kv.Value == 0),
                    "Windows' plugged-in critical action set to do nothing");
                // On the charger from empty: nothing may hibernate it.
                pc.Use(30);
                Check(pc.Hibernations == 0, "no hibernation while charging from empty");
                pc.Use(120);
                // Normal protection now: it hibernates before the battery gives out.
                pc.Unplug();
                pc.Use(400);
                Check(pc.Hibernations == 1 && !pc.Battery.Dead, "after learning it hibernates before the battery gives out");
                Check(pc.HibernatedWithMinutesLeft >= 1.5, string.Format(CultureInfo.InvariantCulture, "hibernated with {0:0.0} min of use to spare ({1})", pc.HibernatedWithMinutesLeft, Mv(pc.HibernatedAtCellMv)));
                Check(pc.HibernatedWithMinutesLeft <= 12, "...and not much earlier than needed");
            });

            Scenario("2. Charger stops early (faulty gauge, stops at 70%): learning doesn't wait for 100%", delegate
            {
                var pc = new Laptop("s2");
                pc.Battery.ChargerStopsAtSoc = 0.70;
                pc.Battery.EnergyWh = 10;
                pc.Battery.PlugIn();
                pc.Boot(false);
                pc.Use(90);
                Check(!pc.Battery.ChargerOn && pc.Battery.Soc < 0.72, "charger stopped at " + (pc.Battery.Soc * 100).ToString("0") + "% (Windows says " + pc.Battery.Reading().WindowsPercent + "%)");
                pc.Unplug();
                pc.Use(400);
                Check(pc.Battery.Dead && pc.Hibernations == 0, "ran until it gave out, no hibernation");
                pc.PowerCutThenBoot(20, true);
                Check(!pc.Learning, "learned from a run that started at the charger's stop");
                double full = double.Parse(pc.Val("gauge.ini", "FullCellMv"), CultureInfo.InvariantCulture);
                Check(Math.Abs(full - (SimBattery.Ocv(0.70) - 45)) < 60, "maximum voltage = where this charger stops: " + Mv(full));
                Check(pc.Val("settings.ini", "FloorLearned") == "1", "hibernate point learned: " + Mv(pc.FloorMv));
            });

            Scenario("3. Installed while on battery at 60%, never charged first", delegate
            {
                var pc = new Laptop("s3");
                pc.Battery.EnergyWh = 0.6 * 34;
                pc.Boot(false);
                pc.Use(400);
                Check(pc.Battery.Dead && pc.Hibernations == 0, "no hibernation, ran until it gave out");
                pc.PowerCutThenBoot(10, true);
                Check(!pc.Learning, "learned from that run");
                Check(pc.Val("settings.ini", "FloorLearned") == "1", "hibernate point learned: " + Mv(pc.FloorMv));
            });

            Scenario("4. Short run (installed at 25%): the limit is learned from it, the runtime from the next long run", delegate
            {
                var pc = new Laptop("s4");
                pc.Battery.EnergyWh = 0.25 * 34;
                pc.Boot(false);
                pc.Use(400);
                Check(pc.Battery.Dead && pc.Hibernations == 0, "ran until it gave out");
                pc.PowerCutThenBoot(10, true);
                Check(!pc.Learning, "learning complete: the battery gave out, that's what it needed");
                int cut = int.Parse(pc.Val("state.ini", "CutoffMv"));
                Check(Math.Abs(cut - pc.Battery.CutoffCellMv) < 60, "true limit learned from the short run: " + Mv(cut));
                Check(pc.FloorMv > cut && pc.Val("settings.ini", "FloorLearned") == "1", "hibernate point set above it: " + Mv(pc.FloorMv));
                Check(pc.Val("gauge.ini", "Calibrated") == "0", "full runtime not claimed from a short run");
                pc.Use(180);
                pc.Unplug();
                pc.Use(600);
                Check(pc.Hibernations == 1 && !pc.Battery.Dead, string.Format(CultureInfo.InvariantCulture, "next full run: hibernates before the battery gives out ({0:0.0} min to spare)", pc.HibernatedWithMinutesLeft));
                Check(pc.Val("gauge.ini", "Calibrated") == "1", "and the runtime curve is learned from that run");
            });

            Scenario("5. Plugged in half-way through a run, then unplugged again", delegate
            {
                var pc = new Laptop("s5");
                pc.Battery.EnergyWh = 34;
                pc.Battery.PlugIn();
                pc.Boot(false);
                pc.Use(10);
                pc.Unplug();
                pc.Use(60);
                pc.PlugIn();
                pc.Use(15);
                pc.Unplug();
                pc.Use(400);
                Check(pc.LogText.Contains("paused"), "the first run was paused when plugged in");
                Check(pc.Battery.Dead && pc.Hibernations == 0, "second run went until the battery gave out");
                pc.PowerCutThenBoot(10, true);
                Check(!pc.Learning, "learned from the second run");
            });

            Scenario("6. Shut down normally in the middle of a run (not a power cut)", delegate
            {
                var pc = new Laptop("s6");
                pc.Battery.EnergyWh = 34;
                pc.Boot(false);
                pc.Use(60);
                pc.ShutdownThenBoot(120);
                Check(pc.Learning, "still learning: a normal shutdown teaches nothing");
                Check(pc.LogText.Contains("the laptop was off"), "the interrupted run is noted");
                pc.Use(400);
                Check(pc.Battery.Dead && pc.Hibernations == 0, "the next run goes until the battery gives out");
                pc.PowerCutThenBoot(10, true);
                Check(!pc.Learning, "learned");
            });

            Scenario("7. Sleeps in the middle of the learning run", delegate
            {
                var pc = new Laptop("s7");
                pc.Battery.EnergyWh = 34;
                pc.Boot(false);
                pc.Use(60);
                pc.Sleep(180);
                pc.Use(400);
                Check(pc.Battery.Dead && pc.Hibernations == 0, "ran until it gave out after waking");
                pc.PowerCutThenBoot(10, true);
                Check(!pc.Learning, "learned");
                double runMinutes = 0;
                foreach (string line in File.ReadAllLines(Path.Combine(pc.Dir, "learn-run.csv")))
                    runMinutes = double.Parse(line.Split(',')[0], CultureInfo.InvariantCulture);
                Check(runMinutes > 150 && runMinutes < 260, "only awake time counted in the run: " + runMinutes.ToString("0") + " min");
            });

            Scenario("8. The service restarts mid-run (an update): the run carries on", delegate
            {
                var pc = new Laptop("s8");
                pc.Battery.EnergyWh = 34;
                pc.Boot(false);
                pc.Use(90);
                pc.ShutdownThenBoot(0.5);
                pc.Use(400);
                pc.PowerCutThenBoot(10, true);
                Check(!pc.Learning, "learned");
                string[] lines = File.ReadAllLines(Path.Combine(pc.Dir, "learn-run.csv"));
                Check(double.Parse(lines[0].Split(',')[0], CultureInfo.InvariantCulture) < 3, "the run kept its first minutes across the restart");
            });

            Scenario("9. Power cut while the battery still had plenty left (not empty)", delegate
            {
                var pc = new Laptop("s9");
                pc.Battery.EnergyWh = 34;
                pc.Boot(false);
                pc.Use(40);
                pc.PowerCutThenBoot(5, false);
                Check(pc.Learning, "not taken as the battery's limit (" + Mv(pc.Battery.CellMv) + ")");
                Check(pc.LogText.Contains("too high to be an empty battery"), "says so");
            });

            Scenario("10. Stop learning: normal protection, hibernates only at the voltage limit", delegate
            {
                var pc = new Laptop("s10");
                pc.Battery.EnergyWh = 34;
                pc.Boot(false);
                pc.Command("learnstop");
                Check(!pc.Learning, "learning stopped");
                pc.Use(400);
                Check(pc.Hibernations == 1 && !pc.Battery.Dead, "hibernated before the battery gave out");
                Check(!pc.LogText.Contains("About a minute of battery left"), "not early on an estimate (curve not learned yet)");
                Check(pc.HibernatedAtCellMv <= 3500 + 20, "hibernated at the voltage limit: " + Mv(pc.HibernatedAtCellMv));
                pc.Resume(60);
                pc.Use(0.5);
                Check(pc.Hibernations == 1, "switched back on without the charger: no instant re-hibernation");
                pc.Use(10);
                Check(pc.Hibernations == 2 && !pc.Battery.Dead, "...but still empty, so after a countdown it hibernates again (by design)");
            });

            Scenario("17. The reported case: hibernated at the limit, then switched on while charging", delegate
            {
                var pc = new Laptop("s17");
                pc.Battery.EnergyWh = 34;
                pc.Boot(false);
                pc.Use(400);
                pc.PowerCutThenBoot(10, true);
                pc.Use(150);
                pc.Unplug();
                pc.Use(400);
                Check(pc.Hibernations == 1 && !pc.Battery.Dead, "hibernated at the limit");
                pc.Battery.PlugIn();
                pc.Resume(30);
                Check(pc.Battery.Reading().WindowsPercent <= 5, "Windows' gauge reads " + pc.Battery.Reading().WindowsPercent + "% at switch-on");
                pc.Use(45);
                Check(pc.Hibernations == 1, "stays on while charging - Battery Guard doesn't hibernate it");
                Check(pc.PluggedInSettings != null && Array.Exists(pc.PluggedInSettings, kv => kv.Key == new Guid("637ea02f-bbcb-4015-8e2c-a1c7b9c0b546") && kv.Value == 0),
                    "and Windows is told not to either (plugged-in critical action: do nothing)");
            });

            Scenario("11. Learn my battery again on a learned PC (battery now gives out higher)", delegate
            {
                var pc = new Laptop("s11");
                pc.Battery.EnergyWh = 34;
                pc.Boot(false);
                pc.Use(400);
                pc.PowerCutThenBoot(10, true);
                int firstFloor = pc.FloorMv;
                pc.Use(150);
                pc.Command("learn");
                Check(pc.Learning, "learning again");
                pc.Battery.CutoffCellMv = 3400;
                pc.Unplug();
                pc.Use(400);
                Check(pc.Battery.Dead && pc.Hibernations == 0, "no hibernation during the new run");
                pc.PowerCutThenBoot(10, true);
                Check(!pc.Learning, "learned again");
                Check(pc.FloorMv > firstFloor, "hibernate point moved up with the battery: " + Mv(firstFloor) + " -> " + Mv(pc.FloorMv));
            });

            Scenario("12. Upgrade from 2.0 (curve already measured): no learning run forced", delegate
            {
                var pc = new Laptop("s12");
                File.WriteAllText(Path.Combine(pc.Dir, "state.ini"), "Cells=3\r\nLastChargeEnd=0\r\nBestFullGapH=9.5\r\nWorstFailGapH=4.7\r\n");
                File.WriteAllText(Path.Combine(pc.Dir, "gauge.ini"), "Version=3\r\nMinutes=0,0.3,0.6,0.9,1.2,1.5,1.8,2.1,2.4,2.7,3,3.5,4,5.8,13,23,34.6,52.5,61,64,71,80,106,130.5,148,174,198,212,219.4,227,230,233,238.5,240,240.6,241,241.5\r\nWeight=0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0\r\nRuns=0\r\nCapacityMwh=34000\r\n");
                pc.Battery.EnergyWh = 34;
                pc.Boot(false);
                Check(!pc.Learning, "not learning");
                pc.Use(400);
                Check(pc.Hibernations == 1 && !pc.Battery.Dead, "protects as before: hibernated before it gave out");
            });

            Scenario("13. Upgrade from 2.1 alpha 1 that was stuck at \"charge to 100%\"", delegate
            {
                var pc = new Laptop("s13");
                File.WriteAllText(Path.Combine(pc.Dir, "state.ini"), "Cells=3\r\nLearn=charge\r\nLearnMode=cutoff\r\nLearnSeq=1\r\n");
                File.WriteAllText(Path.Combine(pc.Dir, "gauge.ini"), "Version=3\r\nCalibrated=0\r\nLearningRun=1\r\nRuns=0\r\nCapacityMwh=39072\r\n");
                pc.Battery.EnergyWh = 0.7 * 34;
                pc.Boot(false);
                pc.Use(1);
                Check(pc.Val("status.ini", "Learn") == "learning", "carries on learning");
                pc.Use(400);
                Check(pc.Battery.Dead && pc.Hibernations == 0, "no hibernation during the run");
                pc.PowerCutThenBoot(10, true);
                Check(!pc.Learning && pc.Val("settings.ini", "FloorLearned") == "1", "learned: hibernates at " + Mv(pc.FloorMv));
            });

            Scenario("14. Heavy use during the learning run (20 W), normal use after", delegate
            {
                var pc = new Laptop("s14");
                pc.Battery.EnergyWh = 34;
                pc.Battery.LoadW = 20;
                pc.Boot(false);
                pc.Use(400);
                pc.PowerCutThenBoot(10, true);
                Check(!pc.Learning, "learned from a heavy-use run");
                pc.Use(150);
                pc.Battery.LoadW = 9;
                pc.Unplug();
                pc.Use(600);
                Check(pc.Hibernations == 1 && !pc.Battery.Dead, string.Format(CultureInfo.InvariantCulture, "then at normal use it still hibernates in time ({0:0.0} min to spare)", pc.HibernatedWithMinutesLeft));
            });

            Scenario("15. Light use during the learning run (5 W), heavy use after (18 W)", delegate
            {
                var pc = new Laptop("s15");
                pc.Battery.EnergyWh = 34;
                pc.Battery.LoadW = 5;
                pc.Boot(false);
                pc.Use(800);
                pc.PowerCutThenBoot(10, true);
                Check(!pc.Learning, "learned from a light-use run");
                pc.Use(150);
                pc.Battery.LoadW = 18;
                pc.Unplug();
                pc.Use(400);
                Check(pc.Hibernations == 1 && !pc.Battery.Dead, string.Format(CultureInfo.InvariantCulture, "under heavy use it still hibernates before giving out ({0:0.0} min to spare)", pc.HibernatedWithMinutesLeft));
            });

            Scenario("16. After learning: switched on while charging from empty, then unplugged early", delegate
            {
                var pc = new Laptop("s16");
                pc.Battery.EnergyWh = 34;
                pc.Boot(false);
                pc.Use(400);
                pc.PowerCutThenBoot(60, true);
                pc.Use(10);
                Check(pc.Hibernations == 0, "doesn't hibernate on the charger at about 0%");
                pc.Unplug();
                pc.Use(60);
                Check(pc.Hibernations == 1 && !pc.Battery.Dead, "unplugged nearly empty: hibernates safely");
            });

            // Some laptops don't let the power fail: their firmware shuts Windows down (or hibernates
            // it) when the battery is nearly empty. That's the end of the learning run all the same.
            Scenario("18. The laptop switches itself off near empty (no power cut), then started on the charger", delegate
            {
                var pc = new Laptop("s18");
                pc.Battery.CutoffCellMv = 3000;
                pc.Battery.FirmwareOffCellMv = 3350;
                pc.Battery.EnergyWh = 34;
                pc.Battery.PlugIn();
                pc.Boot(false);
                pc.Use(30);
                pc.Unplug();
                pc.Use(600);
                Check(pc.Battery.SwitchedOff && pc.Hibernations == 0, "ran until the laptop switched itself off at " + Mv(pc.Battery.CellMv));
                pc.SwitchedOffThenBoot(20, true);
                Check(!pc.Learning, "learning complete: no power cut needed");
                int cut = int.Parse(pc.Val("state.ini", "CutoffMv"));
                Check(Math.Abs(cut - 3350) < 60, "the voltage it switched off at is the limit: " + Mv(cut));
                Check(pc.FloorMv > cut && pc.Val("settings.ini", "FloorLearned") == "1", "hibernate point set above it: " + Mv(pc.FloorMv));
                Check(pc.Val("gauge.ini", "Calibrated") == "1", "runtime curve learned from that run");
                pc.Use(150);
                pc.Unplug();
                pc.Use(600);
                Check(pc.Hibernations == 1 && !pc.Battery.SwitchedOff, string.Format(CultureInfo.InvariantCulture, "next run: Battery Guard hibernates before the laptop would switch off ({0:0.0} min to spare)", pc.HibernatedWithMinutesLeft));
            });

            Scenario("19. The laptop hibernates itself near empty, then is switched on while charging", delegate
            {
                var pc = new Laptop("s19");
                pc.Battery.CutoffCellMv = 3000;
                pc.Battery.FirmwareHibernateCellMv = 3330;
                pc.Battery.EnergyWh = 34;
                pc.Boot(false);
                pc.Use(600);
                Check(pc.Battery.ForcedHibernate && pc.Hibernations == 0, "the laptop hibernated itself at " + Mv(pc.Battery.CellMv) + " (not Battery Guard)");
                pc.Battery.PlugIn();
                pc.Resume(40);
                Check(!pc.Learning, "learning complete on waking up on the charger");
                int cut = int.Parse(pc.Val("state.ini", "CutoffMv"));
                Check(Math.Abs(cut - 3330) < 60, "limit learned: " + Mv(cut));
                Check(pc.Val("settings.ini", "FloorLearned") == "1", "hibernate point learned: " + Mv(pc.FloorMv));
            });

            Scenario("20. Shut down from the Start menu nearly empty: not the battery's limit", delegate
            {
                var pc = new Laptop("s20");
                pc.Battery.EnergyWh = 34;
                pc.Boot(false);
                while (pc.Battery.CellMv > 3420 && !pc.Battery.Dead) pc.Use(1);
                pc.ShutdownThenBoot(30);
                Check(pc.Learning, "still learning after a shutdown at " + Mv(pc.Battery.CellMv));
                Check(pc.LogText.Contains("so not by the battery"), "says why");
            });

            Scenario("21. The service restarts nearly empty (Windows keeps running): the run carries on", delegate
            {
                var pc = new Laptop("s21");
                pc.Battery.EnergyWh = 34;
                pc.Boot(false);
                while (pc.Battery.CellMv > 3420 && !pc.Battery.Dead) pc.Use(1);
                pc.RestartService();
                Check(pc.Learning, "still learning: the laptop never went off");
                pc.Use(400);
                Check(pc.Battery.Dead && pc.Hibernations == 0, "ran on until the battery gave out");
                pc.PowerCutThenBoot(10, true);
                Check(!pc.Learning, "learned");
                int cut = int.Parse(pc.Val("state.ini", "CutoffMv"));
                Check(Math.Abs(cut - pc.Battery.CutoffCellMv) < 60, "at the battery's real limit: " + Mv(cut));
                Check(pc.Val("gauge.ini", "Calibrated") == "1", "from the whole run");
            });

            Scenario("22. Plugged in for a few minutes near the end, then the battery gave out", delegate
            {
                var pc = new Laptop("s22");
                pc.Battery.EnergyWh = 34;
                pc.Boot(false);
                while (pc.Battery.CellMv > 3620 && !pc.Battery.Dead) pc.Use(1);
                pc.PlugIn();
                pc.Use(5);
                pc.Unplug();
                pc.Use(400);
                Check(pc.Battery.Dead && pc.Hibernations == 0, "ran until it gave out");
                pc.PowerCutThenBoot(10, true);
                Check(!pc.Learning, "learning complete (the last run started low, but it went all the way down)");
                int cut = int.Parse(pc.Val("state.ini", "CutoffMv"));
                Check(Math.Abs(cut - pc.Battery.CutoffCellMv) < 60, "true limit learned: " + Mv(cut));
                Check(pc.Val("settings.ini", "FloorLearned") == "1", "hibernate point learned: " + Mv(pc.FloorMv));
            });

            Scenario("23. The run's own record is lost: rebuilt from the battery log", delegate
            {
                var pc = new Laptop("s23");
                pc.Battery.EnergyWh = 34;
                pc.Battery.PlugIn();
                pc.Boot(false);
                pc.Use(20);
                pc.Unplug();
                pc.Use(600);
                Check(pc.Battery.Dead, "ran until it gave out");
                File.Delete(pc.LearnRunFile);
                pc.PowerCutThenBoot(10, true);
                Check(!pc.Learning, "learning complete");
                Check(pc.LogText.Contains("rebuilt it from the battery log"), "the run was rebuilt from the battery log");
                Check(pc.Val("gauge.ini", "Calibrated") == "1", "the whole curve learned from it");
                int cut = int.Parse(pc.Val("state.ini", "CutoffMv"));
                Check(Math.Abs(cut - pc.Battery.CutoffCellMv) < 60, "true limit learned: " + Mv(cut));
            });

            Scenario("24. Ran out of battery while asleep (lid closed near the end)", delegate
            {
                var pc = new Laptop("s24");
                pc.Battery.EnergyWh = 34;
                pc.Boot(false);
                while (pc.Battery.CellMv > 3450 && !pc.Battery.Dead) pc.Use(1);
                pc.Sleep(30);
                pc.PowerCutThenBoot(600, true);
                Check(!pc.Learning, "learning complete");
                int cut = int.Parse(pc.Val("state.ini", "CutoffMv"));
                Check(cut <= 3500 && cut >= pc.Battery.CutoffCellMv - 60, "limit taken from the last reading before it went off: " + Mv(cut) + " (on the safe side)");
            });

            // Updating from 2.1.1 on a laptop where that version missed how the run ended: the
            // run is in the battery log, so it's learned at once instead of asking for another.
            Scenario("25. Updated after an older version missed the power cut: learned from the battery log", delegate
            {
                var pc = new Laptop("s25");
                pc.Battery.EnergyWh = 34;
                pc.Battery.PlugIn();
                pc.Boot(false);
                pc.Use(20);
                pc.Unplug();
                pc.Use(600);
                Check(pc.Battery.Dead, "ran until it gave out");
                pc.Battery.PlugIn();
                pc.ForgetHowTheRunEnded(60);
                pc.Boot(true);
                Check(!pc.Learning, "learning complete at the first start-up of the new version");
                Check(pc.LogText.Contains("Found a finished learning run in the battery log"), "found the run in the battery log");
                Check(pc.Val("state.ini", "LearnSummary").StartsWith("Learned from the run on"), "the summary says which run it learned from");
                int cut = int.Parse(pc.Val("state.ini", "CutoffMv"));
                Check(Math.Abs(cut - pc.Battery.CutoffCellMv) < 60, "true limit learned: " + Mv(cut));
                Check(pc.Val("gauge.ini", "Calibrated") == "1", "runtime curve learned from it");
            });

            Scenario("26. Updated after an older version missed a firmware switch-off", delegate
            {
                var pc = new Laptop("s26");
                pc.Battery.CutoffCellMv = 3000;
                pc.Battery.FirmwareOffCellMv = 3360;
                pc.Battery.EnergyWh = 34;
                pc.Boot(false);
                pc.Use(600);
                Check(pc.Battery.SwitchedOff, "the laptop switched itself off at " + Mv(pc.Battery.CellMv));
                pc.Battery.PlugIn();
                pc.ForgetHowTheRunEnded(30);
                pc.SwitchedOffThenBoot(0, false);
                Check(!pc.Learning, "learning complete");
                int cut = int.Parse(pc.Val("state.ini", "CutoffMv"));
                Check(Math.Abs(cut - 3360) < 60, "limit learned: " + Mv(cut));
            });

            Scenario("27. Updated while learning, last run ended with a Start-menu shutdown: keeps learning", delegate
            {
                var pc = new Laptop("s27");
                pc.Battery.EnergyWh = 34;
                pc.Boot(false);
                while (pc.Battery.CellMv > 3420 && !pc.Battery.Dead) pc.Use(1);
                pc.ShutdownThenBoot(30);
                Check(pc.Learning, "still learning");
                pc.RestartService();
                Check(pc.Learning, "still learning after the update too");
            });

            // Learn my battery on a PC that has learned before: the old run that ended in a power
            // cut is in the battery log, but it mustn't end the new learning.
            Scenario("28. Learn my battery again, then a restart before any new run: keeps learning, no hibernation", delegate
            {
                var pc = new Laptop("s28");
                pc.Battery.EnergyWh = 34;
                pc.Boot(false);
                pc.Use(400);
                pc.PowerCutThenBoot(10, true);
                Check(!pc.Learning, "first learning done");
                pc.Use(150);
                pc.Command("learn");
                Check(pc.Learning, "learning again");
                pc.ShutdownThenBoot(5);
                Check(pc.Learning, "still learning after a restart (the old run isn't reused)");
                pc.Unplug();
                pc.Use(600);
                Check(pc.Battery.Dead && pc.Hibernations == 0, "no hibernation: the run went until the battery gave out (" + Mv(pc.Battery.CellMv) + ")");
                pc.PowerCutThenBoot(10, true);
                Check(!pc.Learning, "learned from the new run");
            });

            // The case reported on the developer's laptop: "Learn my battery" pressed part-way through
            // a run. That run mustn't be the one learned from: the next one, after a charge, is.
            Scenario("29. Learn my battery pressed on battery: this run protected, the next run after a charge is learned", delegate
            {
                var pc = new Laptop("s29");
                pc.Battery.EnergyWh = 34;
                pc.Boot(false);
                pc.Use(400);
                pc.PowerCutThenBoot(10, true);
                Check(!pc.Learning, "first learning done");
                int firstCut = int.Parse(pc.Val("state.ini", "CutoffMv"));
                pc.Use(150);
                pc.Unplug();
                pc.Use(90);
                pc.Command("learn");
                Check(!pc.Learning && pc.Val("state.ini", "LearnNext") == "1", "waiting for the next run (not learning this one)");
                Check(pc.Val("status.ini", "LearnNext") == "1", "the tray app is told so");
                pc.Battery.CutoffCellMv = 3150;
                pc.Use(600);
                Check(pc.Hibernations == 1 && !pc.Battery.Dead, "this run is protected: it hibernated before the battery gave out");
                pc.Battery.PlugIn();
                pc.Resume(20);
                Check(pc.Learning && pc.Val("state.ini", "LearnNext") == "0", "learning starts when the charger is connected");
                pc.Use(150);
                pc.Unplug();
                pc.Use(600);
                Check(pc.Battery.Dead && pc.Hibernations == 1, "the next run went until the battery gave out (" + Mv(pc.Battery.CellMv) + "), no hibernation");
                pc.PowerCutThenBoot(10, true);
                Check(!pc.Learning, "learned from that run");
                int cut = int.Parse(pc.Val("state.ini", "CutoffMv"));
                Check(cut < firstCut - 50 && Math.Abs(cut - 3150) < 60, "new limit from the new run: " + Mv(firstCut) + " -> " + Mv(cut));
            });

            Scenario("30. Learn my battery pressed on battery, then cancelled", delegate
            {
                var pc = new Laptop("s30");
                pc.Battery.EnergyWh = 34;
                pc.Boot(false);
                pc.Use(400);
                pc.PowerCutThenBoot(10, true);
                pc.Use(150);
                pc.Unplug();
                pc.Use(30);
                pc.Command("learn");
                pc.Command("learnstop");
                Check(pc.Val("state.ini", "LearnNext") == "0" && !pc.Learning, "cancelled");
                pc.PlugIn();
                pc.Use(5);
                Check(!pc.Learning, "plugging in doesn't start it any more");
            });

            Console.WriteLine(report.ToString());
            Console.WriteLine(string.Format("{0} passed, {1} failed", passed, failed));
            return failed == 0 ? 0 : 1;
        }
    }
}
