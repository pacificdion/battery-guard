// Battery Guard - Copyright (C) 2026 Dion Fabian. Free software under the GNU General Public License v3.0; see LICENSE.
// The learning part. This battery's own gauge is wrong, and while discharging it also reports a
// frozen power reading, so energy can only be counted while charging. So:
//  - On battery, the charge is read from the voltage through a learned "minutes of normal use
//    left" curve. Every run that ends at the hibernate floor teaches that curve. How fast the
//    curve is being used up right now (heavier or lighter use than normal) scales the time left.
//  - While charging, the reported charge power (which is live) is counted against the learned
//    capacity; the final top-up phase and the voltage after resting correct the count.
//  - It notices when the charger stops before the battery is full.
// Only the displayed %, time left and early warnings come from here; the hibernate trigger itself
// stays on the measured voltage.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace BatteryGuard
{
    struct GaugeInput
    {
        public bool OnAC, Charging;
        public int PackMv;          // latest reading
        public double MedianCellMv; // ~1 min median per cell while on battery, else 0
        public int RateMw;          // + charging, - discharging, int.MinValue = unknown
        public int Cells, FloorMv;  // floor is per cell
        public double AwakeHours, SleptHours;
    }

    class FuelGauge
    {
        const int MinMv = 3300, StepMv = 25, Bins = 37;  // loaded volts per cell, 3.300 - 4.200 V
        const double FullMv = 4150;                      // per cell, just after unplugging a full battery
        const double SleepDrainMw = 800;                 // typical draw while asleep (not measurable)
        const double Efficiency = 0.95;                  // energy stored per energy counted while charging

        // Minutes of normal use left above 3.30 V/cell under load, measured on a 3-cell, 34 Wh
        // battery at about 8.5 W: 238 min from full down to 3.55 V/cell. Its shape fits Li-ion
        // cells in general; on another laptop the first run down to the safety limit stretches
        // or shrinks it to that laptop's runtime, and every later run refines it.
        static readonly double[] SeedMinutes =
        {
            0, 0.3, 0.6, 0.9, 1.2, 1.5, 1.8, 2.1, 2.4, 2.7,         // 3.300 - 3.525 (steep end)
            3.0, 3.5, 4.0, 5.8, 13, 23, 34.6, 52.5, 61, 64,         // 3.550 - 3.775
            71, 80, 106, 130.5, 148, 174, 198, 212, 219.4, 227,     // 3.800 - 4.025
            230, 233, 238.5, 240, 240.6, 241, 241.5,                // 4.050 - 4.200
        };

        // Learned (persisted).
        readonly double[] minutes = (double[])SeedMinutes.Clone();
        readonly double[] weight = new double[Bins];
        public int Runs;                       // runs down to the floor learned from
        public bool Calibrated;                // the curve's time scale fits this laptop
        public bool Fresh;                     // nothing saved yet (first run on this PC)
        public double CapacityMwh = 34000;     // energy from the floor to full (charging side)
        public bool CapacityLearned;
        public double CvShare = 18;            // % of capacity added during the final top-up
        public double CvEntryMw = 15000;       // charge power when the final top-up starts
        public double RestSagMv = 40;          // resting volts minus volts under normal use, per cell (38 measured 4 Nov)

        // Current estimate (persisted so a restart doesn't lose it).
        double percent = double.NaN;           // the counted / blended value
        double display = double.NaN;           // what's shown (smoothed)
        DateTime savedUtc;

        // Within one run.
        bool? wasOnAC;
        bool wasCharging, sleptOnAC;
        double awakeMin, sinceUnplugMin, restMin;
        readonly List<double[]> run = new List<double[]>();        // { cell mV, awake minute }, 1/min
        readonly List<double[]> speedSamples = new List<double[]>(); // { awake minute, curve minutes left }
        double runAccum, speed = 1;
        bool runFromFull;
        double chargeStartPct = double.NaN, chargeInMwh, cvRefMw, cvInMwh;
        bool cvSeenEntry;
        int lastCellMv;
        double restCellMv, sagFromCellMv;    // resting voltage on AC, and the one to compare with after unplugging

        // Outputs.
        public double Percent = -1, MinutesLeft = -1, WarnMinutesLeft = -1, MinutesToFull = -1, CurvePercent = -1, Speed = 1, SigmaPct = -1;
        public string ChargeState = "";        // Discharging | Charging | NotCharging | Full
        public int StoppedEarlySeq;            // counts chargers stopping before full
        public string Event;

        public double FullRuntimeMinutes(int floorMv) { return Math.Max(30, MinutesAt(FullMv) - MinutesAt(floorMv)); }

        public void Update(GaugeInput g)
        {
            Event = null;
            if (g.Cells <= 0 || g.PackMv <= 0) { Percent = MinutesLeft = WarnMinutesLeft = MinutesToFull = -1; return; }
            double dtMin = g.AwakeHours * 60;
            awakeMin += dtMin;
            int cellMv = g.PackMv / g.Cells;
            double rate = g.RateMw == int.MinValue ? 0 : g.RateMw;
            double span = FullRuntimeMinutes(g.FloorMv), floorT = MinutesAt(g.FloorMv);

            if (g.SleptHours > 0.01)
            {
                if (!g.OnAC && !double.IsNaN(percent)) percent -= g.SleptHours * SleepDrainMw / CapacityMwh * 100;
                if (g.OnAC) sleptOnAC = true; // it may have charged while asleep
                speedSamples.Clear();
                sinceUnplugMin = 0;           // voltage reads high for a while after resting
                restMin = 0;
                if (!g.OnAC) display = percent;
            }

            if (!g.OnAC) Discharging(g, cellMv, span, floorT, dtMin);
            else if (g.Charging && rate > 0) Charging(g, cellMv, rate, dtMin);
            else IdleOnAC(g, cellMv, span, floorT, dtMin);

            wasOnAC = g.OnAC;
            wasCharging = g.OnAC && g.Charging && rate > 0;
            lastCellMv = cellMv;
            if (!double.IsNaN(percent)) percent = Clamp(percent, 0, 100);
            Percent = double.IsNaN(display) ? -1 : Clamp(display, 0, 100);
            Speed = speed;
        }

        void Discharging(GaugeInput g, int cellMv, double span, double floorT, double dtMin)
        {
            if (wasOnAC != false)
            {
                sinceUnplugMin = 0;
                run.Clear();
                speedSamples.Clear();
                runFromFull = !double.IsNaN(percent) && percent >= 97;
                sagFromCellMv = restCellMv;
                restCellMv = 0;
            }
            sinceUnplugMin += dtMin;
            if (sagFromCellMv > 0 && sinceUnplugMin >= 0.75 && g.MedianCellMv > 0)
            {
                // Voltage drop when the laptop starts running from the battery = sag under normal use.
                double sag = sagFromCellMv - g.MedianCellMv;
                if (sag > 5 && sag < 200) RestSagMv += 0.3 * (sag - RestSagMv);
                sagFromCellMv = 0;
            }
            ChargeState = "Discharging";
            MinutesToFull = -1;
            double cell = g.MedianCellMv > 0 ? g.MedianCellMv : cellMv;
            double curveLeft = Math.Max(0, MinutesAt(cell) - floorT);
            CurvePercent = Clamp(curveLeft / span * 100, 0, 100);

            // Counted value keeps falling at the normal rate; right after unplugging or waking the
            // voltage reads high, so ease from the counted value to the voltage over 10 minutes.
            if (double.IsNaN(percent)) percent = CurvePercent;
            else percent -= dtMin / span * 100 * speed;
            double w = Math.Min(1, sinceUnplugMin / 10);
            percent = w * CurvePercent + (1 - w) * percent;

            if (double.IsNaN(display)) display = percent;
            double step = (percent - display) * Math.Min(1, dtMin / 4);
            if (w >= 1 && step > 0) step = Math.Min(step, 0.25 * dtMin); // on battery the % shouldn't climb back quickly
            display += step;

            // How fast the curve is being used up vs. normal use (1 = like the run it was learned from).
            // Load bursts make the voltage flicker, so compare the averages at the two ends of a
            // window of up to 30 minutes rather than reacting to single readings.
            speedSamples.Add(new[] { awakeMin, curveLeft });
            while (speedSamples.Count > 0 && awakeMin - speedSamples[0][0] > 30) speedSamples.RemoveAt(0);
            if (sinceUnplugMin >= 10 && awakeMin - speedSamples[0][0] >= 10)
            {
                double t0 = speedSamples[0][0], t1 = awakeMin;
                double[] early = Average(speedSamples, t0, t0 + 3), late = Average(speedSamples, t1 - 3, t1);
                double raw = Clamp((early[1] - late[1]) / Math.Max(1, late[0] - early[0]), Calibrated ? 0.5 : 0.3, Calibrated ? 2 : 3);
                speed += (raw - speed) * Math.Min(1, dtMin / 5);
            }
            // The shown % is smoothed; near empty, follow the voltage directly so time left isn't late.
            double shown = display / 100 * span;
            if (curveLeft < 20) shown = Math.Min(shown, curveLeft);
            MinutesLeft = shown / speed;
            // For warnings: near empty the voltage drops steeply, so 5 minutes left and 2 minutes
            // left are only ~10 mV apart - about the size of normal flicker. Allow for that margin.
            WarnMinutesLeft = Math.Min(MinutesLeft, Math.Max(0, MinutesAt(cell - 15) - floorT) / speed);
            SigmaPct = Runs == 0 ? 8 : Math.Max(3, 8 / Math.Sqrt(1 + Runs));

            runAccum += dtMin;
            if (runAccum >= 1 && run.Count < 1000)
            {
                runAccum = 0;
                run.Add(new[] { cell, awakeMin });
            }
        }

        void Charging(GaugeInput g, int cellMv, double rate, double dtMin)
        {
            if (!wasCharging)
            {
                if (double.IsNaN(percent) || sleptOnAC) percent = CurveAt(cellMv - rate / 1000.0 / (g.PackMv / 1000.0) * 250 / g.Cells - RestSagMv, g.FloorMv);
                chargeStartPct = sleptOnAC ? double.NaN : percent;
                chargeInMwh = cvInMwh = 0;
                cvSeenEntry = false;
                sleptOnAC = false;
            }
            ChargeState = "Charging";
            restMin = 0;
            restCellMv = 0;
            double inMwh = rate * dtMin / 60;
            chargeInMwh += inMwh;
            percent += inMwh * Efficiency / CapacityMwh * 100;

            // Final top-up: charge power fades as the battery fills, close to linearly with what's left.
            if (cellMv >= 4150)
            {
                if (!cvSeenEntry && lastCellMv > 0 && lastCellMv < 4150) { cvSeenEntry = true; cvRefMw = rate; cvInMwh = 0; }
                if (cvSeenEntry) cvRefMw = Math.Max(cvRefMw, rate);
                cvInMwh += inMwh;
                double reference = cvSeenEntry ? cvRefMw : Math.Max(CvEntryMw, rate);
                double topUp = 100 - CvShare * Math.Min(1, rate / reference);
                percent += (topUp - percent) * Math.Min(1, dtMin / 5);
            }
            percent = Math.Min(99.5, percent); // 100 only once the charger actually finishes
            // Rough time to full: the main phase at the current power, plus ~20 min of final top-up
            // (or, once in the top-up, twice what the fading power alone would suggest).
            double toFill = (100 - percent) / 100 * CapacityMwh, power = Math.Max(1000, rate * Efficiency);
            MinutesToFull = cellMv >= 4150 ? Math.Max(5, toFill / power * 60 * 2) : toFill / power * 60 + 20;
            display = double.IsNaN(display) ? percent : display + (percent - display) * Math.Min(1, dtMin);
            MinutesLeft = WarnMinutesLeft = -1;
            SigmaPct = CapacityLearned ? 4 : 8;
        }

        void IdleOnAC(GaugeInput g, int cellMv, double span, double floorT, double dtMin)
        {
            if (wasCharging)
            {
                // The charger stopped. Full only if it got to the top of the voltage range.
                if (cellMv >= 4100 || (cvSeenEntry && percent >= 95)) Finished();
                else
                {
                    StoppedEarlySeq++;
                    Event = string.Format(CultureInfo.InvariantCulture,
                        "Charger stopped early at about {0:0}% ({1:0.000} V/cell, {2:0.0} Wh put in) - not full.",
                        percent, cellMv / 1000.0, chargeInMwh / 1000);
                }
            }
            restMin += dtMin;
            // An idle battery's voltage settles to its true level within ~20 minutes (sooner if it
            // wasn't just charged). From then on it's the best measurement there is, so follow it.
            double resting = CurveAt(cellMv - RestSagMv, g.FloorMv);
            if (double.IsNaN(percent)) percent = resting;
            else if (sleptOnAC && restMin >= 1) { percent = display = resting; sleptOnAC = false; } // it may have charged while off
            else if (restMin >= 20) percent += (resting - percent) * Math.Min(1, dtMin / 10);
            if (restMin >= 20) sleptOnAC = false;
            if (restMin >= 5) restCellMv = cellMv;
            display = double.IsNaN(display) ? percent : display + (percent - display) * Math.Min(1, dtMin);
            ChargeState = percent >= 97 ? "Full" : "NotCharging";
            MinutesLeft = WarnMinutesLeft = MinutesToFull = -1;
            SigmaPct = restMin >= 20 ? 5 : 8;
        }

        void Finished()
        {
            if (!double.IsNaN(chargeStartPct) && 100 - chargeStartPct >= 30 && chargeInMwh > 0)
            {
                double measured = Clamp(chargeInMwh * Efficiency / ((100 - chargeStartPct) / 100), 10000, 100000);
                CapacityMwh = CapacityLearned ? CapacityMwh + 0.3 * (measured - CapacityMwh) : measured;
                CapacityLearned = true;
                Event = string.Format(CultureInfo.InvariantCulture, "Charging finished: {0:0.0} Wh put in from about {1:0}%, capacity now {2:0.0} Wh.",
                    chargeInMwh / 1000, chargeStartPct, CapacityMwh / 1000);
            }
            else Event = "Charging finished: battery full.";
            if (cvSeenEntry && cvInMwh > 0)
            {
                CvShare += 0.3 * (Clamp(cvInMwh * Efficiency / CapacityMwh * 100, 5, 40) - CvShare);
                CvEntryMw += 0.3 * (cvRefMw - CvEntryMw);
            }
            percent = display = 100;
        }

        // The voltage reached the hibernate floor (or is predicted to within minutesStillLeft):
        // teach the curve from this run.
        public void ReachedFloor(int floorMv, double minutesStillLeft)
        {
            double end = awakeMin + Math.Max(0, minutesStillLeft) * speed, floorT = MinutesAt(floorMv);
            // The curve is in minutes of normal use, so a run-down much faster or slower than the
            // curve expects (heavy or very light use) would skew it: note it, but don't learn from it.
            double pace = run.Count > 0 ? (MinutesAt(run[0][0]) - floorT) / Math.Max(1, end - run[0][1]) : 1;
            // New laptop: the first long run sets the curve's time scale (its shape is kept).
            double covered = run.Count > 0 ? (MinutesAt(run[0][0]) - floorT) / FullRuntimeMinutes(floorMv) : 0;
            if (!Calibrated && run.Count >= 20 && covered >= 0.4 && pace >= 0.2 && pace <= 5)
            {
                for (int i = 0; i < Bins; i++) minutes[i] /= pace;
                floorT = MinutesAt(floorMv);
                Calibrated = true;
                pace = 1;
                Event = string.Format(CultureInfo.InvariantCulture, "Fitted the runtime curve to this laptop: a full charge lasts about {0:0} min of normal use.",
                    FullRuntimeMinutes(floorMv));
            }
            if (run.Count >= 20 && (pace < 0.8 || pace > 1.25))
                Event = string.Format(CultureInfo.InvariantCulture, "Ran down to the safety limit in {0:0} min, at {1:0.0}x normal use: too far from normal to learn the curve from.", end - run[0][1], pace);
            else if (run.Count >= 20)
            {
                foreach (double[] p in run)
                {
                    int i = (int)Math.Round((p[0] - MinMv) / StepMv);
                    if (i < 0 || i >= Bins) continue;
                    double observed = end - p[1] + floorT;
                    minutes[i] += (observed - minutes[i]) / (1 + weight[i]);
                    weight[i] = Math.Min(4, weight[i] + 1);
                }
                for (int i = 1; i < Bins; i++) minutes[i] = Math.Max(minutes[i], minutes[i - 1]); // more volts never means less time
                Runs++;
                Event = (Event != null ? Event + " " : "") + string.Format(CultureInfo.InvariantCulture, "Learned from a {0:0}-minute run down to the safety limit{1}.",
                    end - run[0][1], runFromFull ? " that started full" : "");
            }
            run.Clear();
            percent = display = 0;
        }

        double MinutesAt(double cellMv)
        {
            double x = (cellMv - MinMv) / StepMv;
            if (x <= 0) return minutes[0];
            if (x >= Bins - 1) return minutes[Bins - 1];
            int i = (int)x;
            return minutes[i] + (minutes[i + 1] - minutes[i]) * (x - i);
        }

        // The charge an idle battery's voltage points to.
        public double RestingPercent(double cellMv, int floorMv) { return CurveAt(cellMv - RestSagMv, floorMv); }

        double CurveAt(double cellMv, int floorMv)
        {
            return Clamp((MinutesAt(cellMv) - MinutesAt(floorMv)) / FullRuntimeMinutes(floorMv) * 100, 0, 100);
        }

        // First run on a PC: start from the capacity the battery was made with, allowing for some
        // wear (it's corrected after the first full charge).
        public void SeedCapacity(int designMwh)
        {
            if (designMwh >= 10000 && designMwh <= 150000) CapacityMwh = Clamp(designMwh * 0.8, 10000, 100000);
        }

        public void ResetLearning()
        {
            Array.Copy(SeedMinutes, minutes, Bins);
            Array.Clear(weight, 0, Bins);
            Runs = 0;
            Calibrated = false;
            CapacityMwh = 34000;
            CapacityLearned = false;
            CvShare = 18;
            CvEntryMw = 15000;
            RestSagMv = 40;
        }

        public string Summary(int floorMv)
        {
            double full = FullRuntimeMinutes(floorMv);
            string runtime = string.Format(CultureInfo.InvariantCulture, "A full charge lasts about {0}h {1:00}m of normal use", (int)full / 60, (int)full % 60);
            string from = Runs > 0 ? string.Format(CultureInfo.InvariantCulture, " (learned from {0} run{1}).", Runs, Runs == 1 ? "" : "s")
                : Calibrated ? " (measured, refined every run)." : " (a first estimate until it has run down once).";
            string capacity = string.Format(CultureInfo.InvariantCulture, " Capacity {0}{1:0} Wh.", CapacityLearned ? "" : "about ", CapacityMwh / 1000);
            return runtime + from + capacity;
        }

        public string Save()
        {
            var sb = new StringBuilder();
            Action<string, object> add = delegate(string k, object v)
            {
                sb.Append(k).Append('=').Append(Convert.ToString(v, CultureInfo.InvariantCulture)).Append("\r\n");
            };
            add("Version", 3);
            add("Minutes", string.Join(",", minutes.Select(m => m.ToString("0.##", CultureInfo.InvariantCulture)).ToArray()));
            add("Weight", string.Join(",", weight.Select(m => m.ToString("0.##", CultureInfo.InvariantCulture)).ToArray()));
            add("Runs", Runs);
            add("Calibrated", Calibrated ? 1 : 0);
            add("CapacityMwh", Ini.F(CapacityMwh));
            add("CapacityLearned", CapacityLearned ? 1 : 0);
            add("CvShare", Ini.F(CvShare));
            add("CvEntryMw", Ini.F(CvEntryMw));
            add("RestSagMv", Ini.F(RestSagMv));
            add("Percent", double.IsNaN(percent) ? "" : Ini.F(percent));
            add("SavedUtc", DateTime.UtcNow.Ticks);
            return sb.ToString();
        }

        public void Load(Dictionary<string, string> kv)
        {
            Fresh = kv == null || kv.Count == 0;
            int version = kv == null ? 0 : Ini.I(kv, "Version", 1);
            // Earlier versions only ran on the laptop the starting curve was measured on.
            Calibrated = !Fresh && Ini.I(kv, "Calibrated", 1) == 1;
            if (version < 2) return; // older format: start from the seed
            double[] m = ParseList(Ini.S(kv, "Minutes", "")), w = ParseList(Ini.S(kv, "Weight", ""));
            // Version 2 learned from fast run-downs too, which skewed the curve: keep the measured seed.
            if (version >= 3 && m.Length == Bins && w.Length == Bins)
            {
                Array.Copy(m, minutes, Bins);
                Array.Copy(w, weight, Bins);
                Runs = Ini.I(kv, "Runs", 0);
            }
            CapacityMwh = Clamp(Ini.D(kv, "CapacityMwh", 34000), 10000, 100000);
            CapacityLearned = Ini.I(kv, "CapacityLearned", 0) == 1;
            CvShare = Clamp(Ini.D(kv, "CvShare", 18), 5, 40);
            CvEntryMw = Clamp(Ini.D(kv, "CvEntryMw", 15000), 1000, 100000);
            RestSagMv = Clamp(Ini.D(kv, "RestSagMv", 40), 5, 200);
            savedUtc = new DateTime(Ini.L(kv, "SavedUtc", 0), DateTimeKind.Utc);
            // A recent save is still good; after longer it's re-measured.
            if ((DateTime.UtcNow - savedUtc).TotalMinutes < 10) percent = display = Ini.D(kv, "Percent", double.NaN);
        }

        // Mean { x, y } of the points with x in [from, to].
        static double[] Average(List<double[]> points, double from, double to)
        {
            double n = 0, sx = 0, sy = 0;
            foreach (double[] p in points)
            {
                if (p[0] < from || p[0] > to) continue;
                n++; sx += p[0]; sy += p[1];
            }
            return n > 0 ? new[] { sx / n, sy / n } : new[] { from, 0.0 };
        }

        static double[] ParseList(string s)
        {
            var list = new List<double>();
            foreach (string part in s.Split(','))
            {
                double d;
                if (double.TryParse(part, NumberStyles.Float, CultureInfo.InvariantCulture, out d)) list.Add(d);
            }
            return list.ToArray();
        }

        static double Clamp(double v, double min, double max)
        {
            return Math.Max(min, Math.Min(max, v));
        }
    }
}
