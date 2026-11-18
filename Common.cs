// Battery Guard - Copyright (C) 2026 Dion Fabian. Free software under the GNU General Public License v3.0; see LICENSE.
// Shared by BatteryGuardCore.exe (the protection service, runs as SYSTEM) and
// BatteryGuard.exe (the tray app in each user's session). They talk through small files in
// C:\ProgramData\BatteryGuard. Only the service (and admins) can write there; the tray apps read
// status.ini and the logs, and ask for changes by dropping cmd-*.txt files into inbox\, the one
// folder users can write to.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Threading;

namespace BatteryGuard
{
    static class Paths
    {
        public static string Data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "BatteryGuard");
        public static bool Custom;

        public static void Init(string[] args)
        {
            int i = Array.IndexOf(args, "--data");
            if (i >= 0 && i + 1 < args.Length)
            {
                Data = Path.GetFullPath(args[i + 1]);
                Custom = true;
            }
            try { Directory.CreateDirectory(Data); }
            catch { }
        }

        public static string Settings { get { return Path.Combine(Data, "settings.ini"); } }
        public static string Status { get { return Path.Combine(Data, "status.ini"); } }
        public static string State { get { return Path.Combine(Data, "state.ini"); } }
        public static string Log { get { return Path.Combine(Data, "log.txt"); } }
        public static string History { get { return Path.Combine(Data, "battery-log.csv"); } }
        public static string Inbox { get { return Path.Combine(Data, "inbox"); } }

        // Keeps test instances (--data) apart from the installed one in mutex names.
        public static string Suffix
        {
            get { return Custom ? "_" + ((uint)Data.ToLowerInvariant().GetHashCode()).ToString("x") : ""; }
        }
    }

    static class Ini
    {
        // Returns null if the file exists but could not be read (e.g. mid-replace).
        public static Dictionary<string, string> Read(string path)
        {
            var kv = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            string[] lines = null;
            for (int attempt = 0; attempt < 5 && lines == null; attempt++)
            {
                try
                {
                    if (!File.Exists(path)) return kv;
                    lines = File.ReadAllLines(path);
                }
                catch (IOException) { Thread.Sleep(40); }
                catch (UnauthorizedAccessException) { Thread.Sleep(40); }
            }
            if (lines == null) return null;
            return Parse(lines);
        }

        public static Dictionary<string, string> Parse(string[] lines)
        {
            var kv = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (string raw in lines)
            {
                string line = raw.Trim();
                if (line.Length == 0 || line[0] == '#' || line[0] == ';') continue;
                int eq = line.IndexOf('=');
                if (eq > 0) kv[line.Substring(0, eq).Trim()] = line.Substring(eq + 1).Trim();
            }
            return kv;
        }

        // Writes via a temp file + replace, so readers never see a half-written file.
        public static void Write(string path, string text)
        {
            string tmp = path + "." + Guid.NewGuid().ToString("N").Substring(0, 8) + ".tmp";
            File.WriteAllText(tmp, text, Encoding.UTF8);
            for (int attempt = 0; attempt < 5; attempt++)
            {
                try
                {
                    if (File.Exists(path)) File.Replace(tmp, path, null);
                    else File.Move(tmp, path);
                    return;
                }
                catch (IOException) { Thread.Sleep(40); }
                catch (UnauthorizedAccessException) { Thread.Sleep(40); }
            }
            try { File.Delete(tmp); }
            catch { }
            throw new IOException("Could not write " + path);
        }

        public static string S(Dictionary<string, string> kv, string key, string def)
        {
            string s;
            return kv.TryGetValue(key, out s) ? s : def;
        }

        public static double D(Dictionary<string, string> kv, string key, double def)
        {
            string s; double d;
            return kv.TryGetValue(key, out s) && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out d) ? d : def;
        }

        public static int I(Dictionary<string, string> kv, string key, int def)
        {
            string s; int i;
            return kv.TryGetValue(key, out s) && int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out i) ? i : def;
        }

        public static long L(Dictionary<string, string> kv, string key, long def)
        {
            string s; long l;
            return kv.TryGetValue(key, out s) && long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out l) ? l : def;
        }

        public static string F(double d)
        {
            return d.ToString("0.###", CultureInfo.InvariantCulture);
        }

        public static string OneLine(string s)
        {
            return (s ?? "").Replace("\r", " ").Replace("\n", " ");
        }
    }

    class Settings
    {
        public int Enabled = 1;
        public int FloorPerCellMv = 3500;
        public double WarnMinutes = 5;
        public int CountdownSeconds = 60;
        public int AllowPostpone = 1;
        public int HideWindowsWarnings = 1;
        public int Cells = 0;
        // Test mode: never touches power settings or hibernates; can fake a draining battery.
        public int DryRun = 0;
        public int SimulateBattery = 0;
        public int SimStartMv = 11400;
        public double SimDropMvPerMin = 150;
        public int SimRateMw = 15000;

        public static Settings Load()
        {
            var kv = Ini.Read(Paths.Settings);
            return kv == null ? null : From(kv);
        }

        public static Settings From(Dictionary<string, string> kv)
        {
            var s = new Settings();
            s.Enabled = Ini.I(kv, "Enabled", s.Enabled);
            s.FloorPerCellMv = Ini.I(kv, "FloorPerCellMv", s.FloorPerCellMv);
            s.WarnMinutes = Ini.D(kv, "WarnMinutes", s.WarnMinutes);
            s.CountdownSeconds = Ini.I(kv, "CountdownSeconds", s.CountdownSeconds);
            s.AllowPostpone = Ini.I(kv, "AllowPostpone", s.AllowPostpone);
            s.HideWindowsWarnings = Ini.I(kv, "HideWindowsWarnings", s.HideWindowsWarnings);
            s.Cells = Ini.I(kv, "Cells", s.Cells);
            s.DryRun = Ini.I(kv, "DryRun", s.DryRun);
            s.SimulateBattery = Ini.I(kv, "SimulateBattery", s.SimulateBattery);
            s.SimStartMv = Ini.I(kv, "SimStartMv", s.SimStartMv);
            s.SimDropMvPerMin = Ini.D(kv, "SimDropMvPerMin", s.SimDropMvPerMin);
            s.SimRateMw = Ini.I(kv, "SimRateMw", s.SimRateMw);
            s.Clamp();
            return s;
        }

        void Clamp()
        {
            FloorPerCellMv = Math.Max(3300, Math.Min(3800, FloorPerCellMv));
            WarnMinutes = Math.Max(1, Math.Min(30, WarnMinutes));
            CountdownSeconds = Math.Max(15, Math.Min(300, CountdownSeconds));
            if (Cells < 0 || Cells > 8) Cells = 0;
            if (DryRun != 1) SimulateBattery = 0;
        }

        public void Save()
        {
            Ini.Write(Paths.Settings, ToText());
        }

        public string ToText()
        {
            var sb = new StringBuilder();
            sb.Append("# Battery Guard settings - shared by every user on this PC.\r\n");
            sb.Append("# Easiest to change from the Battery Guard window (Start menu > Battery Guard).\r\n");
            sb.AppendFormat(CultureInfo.InvariantCulture, "Enabled={0}\r\n", Enabled);
            sb.Append("# Hibernate when the battery's voltage per cell falls to this (millivolts).\r\n");
            sb.AppendFormat(CultureInfo.InvariantCulture, "FloorPerCellMv={0}\r\n", FloorPerCellMv);
            sb.AppendFormat(CultureInfo.InvariantCulture, "WarnMinutes={0}\r\n", Ini.F(WarnMinutes));
            sb.AppendFormat(CultureInfo.InvariantCulture, "CountdownSeconds={0}\r\n", CountdownSeconds);
            sb.AppendFormat(CultureInfo.InvariantCulture, "AllowPostpone={0}\r\n", AllowPostpone);
            sb.AppendFormat(CultureInfo.InvariantCulture, "HideWindowsWarnings={0}\r\n", HideWindowsWarnings);
            sb.Append("# Cells in series; 0 = detect automatically.\r\n");
            sb.AppendFormat(CultureInfo.InvariantCulture, "Cells={0}\r\n", Cells);
            if (DryRun == 1)
            {
                sb.AppendFormat(CultureInfo.InvariantCulture, "DryRun={0}\r\nSimulateBattery={1}\r\nSimStartMv={2}\r\nSimDropMvPerMin={3}\r\nSimRateMw={4}\r\n",
                    DryRun, SimulateBattery, SimStartMv, Ini.F(SimDropMvPerMin), SimRateMw);
            }
            return sb.ToString();
        }
    }

    // What the core service last measured and decided; written by the core, read by tray apps.
    class Status
    {
        public DateTime UpdatedUtc;
        public string Protection = "Off";   // Active | Fallback | Off
        public bool OnAC, Charging, DryRun;
        public int VoltageMv, PerCellMv, FloorPerCellMv, WindowsPercent = -1;
        public double Percent = -1, MinutesLeft = -1, SigmaPct = -1;
        public int RateMw;                  // + charging, - discharging
        public string ChargeState = "";     // Discharging | Charging | NotCharging | Full
        public bool StopsEarly;             // this PC's charger has been seen stopping before full
        public bool ExpectFull;             // this charge should go all the way to full
        public bool Limited;                // this charge is expected to stop early...
        public double StopPct = -1;         // ...at about this level
        public bool ReadyForFull;           // on battery, and plugging in now (laptop on) gives a full charge
        public DateTime ReadyAtUtc;         // when enough time has passed since the last charge for a full one
        public double NeedGapHours;         // how long that is
        public string Cycle = "";           // towards the next full charge: "" | wait | unplug | drain | ready
        public int CycleSeq;                // changes whenever the user needs to do something
        public double CycleEtaMin = -1;     // minutes of battery use until it's ready to plug in
        public double MinutesToFull = -1, ChargeInWh, FullRuntimeMin;
        public DateTime OnBatterySinceUtc, PluggedSinceUtc, ChargingSinceUtc, LastFullUtc;
        public string LastCharge = "";      // summary of the last charging session
        public string Phase = "Normal";     // Normal | Warning | Countdown | Hibernating
        public int WarningSeq, CountdownSeq;
        public DateTime CountdownEndUtc;
        public bool Emergency, PostponeAvailable;
        public string Message = "", Learned = "";

        public static Status Load()
        {
            var kv = Ini.Read(Paths.Status);
            if (kv == null || kv.Count == 0) return null;
            var s = new Status();
            s.UpdatedUtc = Time(kv, "UpdatedUtc");
            s.Protection = Ini.S(kv, "Protection", s.Protection);
            s.OnAC = Ini.I(kv, "OnAC", 0) == 1;
            s.Charging = Ini.I(kv, "Charging", 0) == 1;
            s.DryRun = Ini.I(kv, "DryRun", 0) == 1;
            s.VoltageMv = Ini.I(kv, "VoltageMv", 0);
            s.PerCellMv = Ini.I(kv, "PerCellMv", 0);
            s.FloorPerCellMv = Ini.I(kv, "FloorPerCellMv", 0);
            s.WindowsPercent = Ini.I(kv, "WindowsPercent", -1);
            s.Percent = Ini.D(kv, "Percent", -1);
            s.MinutesLeft = Ini.D(kv, "MinutesLeft", -1);
            s.SigmaPct = Ini.D(kv, "SigmaPct", -1);
            s.RateMw = Ini.I(kv, "RateMw", 0);
            s.ChargeState = Ini.S(kv, "ChargeState", "");
            s.StopsEarly = Ini.I(kv, "StopsEarly", 0) == 1;
            s.ExpectFull = Ini.I(kv, "ExpectFull", 0) == 1;
            s.Limited = Ini.I(kv, "Limited", 0) == 1;
            s.StopPct = Ini.D(kv, "StopPct", -1);
            s.ReadyForFull = Ini.I(kv, "ReadyForFull", 0) == 1;
            s.ReadyAtUtc = Time(kv, "ReadyAtUtc");
            s.NeedGapHours = Ini.D(kv, "NeedGapHours", 0);
            s.Cycle = Ini.S(kv, "Cycle", "");
            s.CycleSeq = Ini.I(kv, "CycleSeq", 0);
            s.CycleEtaMin = Ini.D(kv, "CycleEtaMin", -1);
            s.MinutesToFull = Ini.D(kv, "MinutesToFull", -1);
            s.ChargeInWh = Ini.D(kv, "ChargeInWh", 0);
            s.FullRuntimeMin = Ini.D(kv, "FullRuntimeMin", 0);
            s.OnBatterySinceUtc = Time(kv, "OnBatterySinceUtc");
            s.PluggedSinceUtc = Time(kv, "PluggedSinceUtc");
            s.ChargingSinceUtc = Time(kv, "ChargingSinceUtc");
            s.LastFullUtc = Time(kv, "LastFullUtc");
            s.LastCharge = Ini.S(kv, "LastCharge", "");
            s.Phase = Ini.S(kv, "Phase", s.Phase);
            s.WarningSeq = Ini.I(kv, "WarningSeq", 0);
            s.CountdownSeq = Ini.I(kv, "CountdownSeq", 0);
            s.CountdownEndUtc = Time(kv, "CountdownEndUtc");
            s.Emergency = Ini.I(kv, "Emergency", 0) == 1;
            s.PostponeAvailable = Ini.I(kv, "PostponeAvailable", 0) == 1;
            s.Message = Ini.S(kv, "Message", "");
            s.Learned = Ini.S(kv, "Learned", "");
            return s;
        }

        static DateTime Time(Dictionary<string, string> kv, string key)
        {
            return new DateTime(Ini.L(kv, key, 0), DateTimeKind.Utc);
        }

        // Everything except the time stamp, so the core can tell whether anything changed.
        public string Body()
        {
            var sb = new StringBuilder();
            Action<string, object> add = delegate(string k, object v)
            {
                sb.Append(k).Append('=').Append(Convert.ToString(v, CultureInfo.InvariantCulture)).Append("\r\n");
            };
            add("Protection", Protection);
            add("OnAC", OnAC ? 1 : 0);
            add("Charging", Charging ? 1 : 0);
            add("DryRun", DryRun ? 1 : 0);
            add("VoltageMv", VoltageMv);
            add("PerCellMv", PerCellMv);
            add("FloorPerCellMv", FloorPerCellMv);
            add("WindowsPercent", WindowsPercent);
            add("Percent", Percent.ToString("0.#", CultureInfo.InvariantCulture));
            add("MinutesLeft", MinutesLeft.ToString("0", CultureInfo.InvariantCulture));
            add("SigmaPct", SigmaPct.ToString("0", CultureInfo.InvariantCulture));
            add("RateMw", RateMw);
            add("ChargeState", ChargeState);
            add("StopsEarly", StopsEarly ? 1 : 0);
            add("ExpectFull", ExpectFull ? 1 : 0);
            add("Limited", Limited ? 1 : 0);
            add("StopPct", StopPct.ToString("0", CultureInfo.InvariantCulture));
            add("ReadyForFull", ReadyForFull ? 1 : 0);
            add("ReadyAtUtc", ReadyAtUtc.Ticks);
            add("NeedGapHours", Ini.F(NeedGapHours));
            add("Cycle", Cycle);
            add("CycleSeq", CycleSeq);
            add("CycleEtaMin", CycleEtaMin.ToString("0", CultureInfo.InvariantCulture));
            add("MinutesToFull", MinutesToFull.ToString("0", CultureInfo.InvariantCulture));
            add("ChargeInWh", ChargeInWh.ToString("0.0", CultureInfo.InvariantCulture));
            add("FullRuntimeMin", FullRuntimeMin.ToString("0", CultureInfo.InvariantCulture));
            add("OnBatterySinceUtc", OnBatterySinceUtc.Ticks);
            add("PluggedSinceUtc", PluggedSinceUtc.Ticks);
            add("ChargingSinceUtc", ChargingSinceUtc.Ticks);
            add("LastFullUtc", LastFullUtc.Ticks);
            add("LastCharge", Ini.OneLine(LastCharge));
            add("Phase", Phase);
            add("WarningSeq", WarningSeq);
            add("CountdownSeq", CountdownSeq);
            add("CountdownEndUtc", CountdownEndUtc.Ticks);
            add("Emergency", Emergency ? 1 : 0);
            add("PostponeAvailable", PostponeAvailable ? 1 : 0);
            add("Message", Ini.OneLine(Message));
            add("Learned", Ini.OneLine(Learned));
            return sb.ToString();
        }

        public void Save(string body)
        {
            Ini.Write(Paths.Status, "UpdatedUtc=" + UpdatedUtc.Ticks.ToString(CultureInfo.InvariantCulture) + "\r\n" + body);
        }
    }
    // inbox\ is the one place users can write: they may add files there (and change or delete
    // files), but not create folders or links, nor remove the folder itself.
    static class InboxAccess
    {
        public static void Open(string dir)
        {
            var info = new DirectoryInfo(dir);
            DirectorySecurity security = info.GetAccessControl();
            var users = new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null);
            security.AddAccessRule(new FileSystemAccessRule(users, FileSystemRights.ReadAndExecute | FileSystemRights.CreateFiles,
                InheritanceFlags.None, PropagationFlags.None, AccessControlType.Allow));
            security.AddAccessRule(new FileSystemAccessRule(users, FileSystemRights.Modify,
                InheritanceFlags.ObjectInherit, PropagationFlags.InheritOnly, AccessControlType.Allow));
            info.SetAccessControl(security);
        }
    }

    static class PowerCaps
    {
        [DllImport("powrprof.dll")]
        [return: MarshalAs(UnmanagedType.U1)]
        static extern bool GetPwrCapabilities(IntPtr capabilities);

        // Whether this PC can hibernate right now (supported, and the hibernation file is on).
        public static bool CanHibernate(out bool supported)
        {
            supported = true;
            IntPtr p = Marshal.AllocHGlobal(256);
            try
            {
                for (int i = 0; i < 256; i++) Marshal.WriteByte(p, i, 0);
                if (!GetPwrCapabilities(p)) return true; // can't tell: assume it can
                supported = Marshal.ReadByte(p, 6) != 0;       // SystemS4
                return supported && Marshal.ReadByte(p, 8) != 0; // HiberFilePresent
            }
            catch { return true; }
            finally { Marshal.FreeHGlobal(p); }
        }
    }

    static class Memory
    {
        [DllImport("kernel32.dll")]
        static extern bool SetProcessWorkingSetSize(IntPtr process, IntPtr min, IntPtr max);

        [DllImport("kernel32.dll")]
        static extern IntPtr GetCurrentProcess();

        // Hands back to Windows the memory that isn't being used right now (start-up code,
        // closed windows, loaded history).
        public static void Trim()
        {
            try
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
                SetProcessWorkingSetSize(GetCurrentProcess(), new IntPtr(-1), new IntPtr(-1));
            }
            catch { }
        }
    }

    static class Commands
    {
        // Tray apps ask the core to act by dropping a small file into inbox\; the core reads it
        // straight away and deletes it. Written under another name first, so the core never sees
        // half a file.
        public static void Send(string command)
        {
            string id = Guid.NewGuid().ToString("N");
            if (!Directory.Exists(Paths.Inbox)) Directory.CreateDirectory(Paths.Inbox);
            string part = Path.Combine(Paths.Inbox, id + ".part");
            File.WriteAllText(part, command, Encoding.UTF8);
            File.Move(part, Path.Combine(Paths.Inbox, "cmd-" + id + ".txt"));
        }
    }

    static class DurableFile
    {
        // Appends straight through to disk (survives a crash or power loss right after), sharing
        // the file so the service and the tray apps can all write to it at once.
        public static bool Append(string path, string text, int attempts)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(text);
            for (int i = 0; i < attempts; i++)
            {
                try
                {
                    using (var fs = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.WriteThrough))
                        fs.Write(bytes, 0, bytes.Length);
                    return true;
                }
                catch (IOException) { Thread.Sleep(30); }
                catch (UnauthorizedAccessException) { Thread.Sleep(30); }
            }
            return false;
        }
    }

    // Full logs move into archive\ as compressed files. Nothing is ever deleted.
    static class Archive
    {
        public static string Dir { get { return Path.Combine(Paths.Data, "archive"); } }

        public static void Rotate(string path, long maxBytes)
        {
            try
            {
                var fi = new FileInfo(path);
                if (fi.Exists && fi.Length >= maxBytes) Store(path);
            }
            catch { }
        }

        // Throws if the file is in use; the caller tries again later.
        public static void Store(string path)
        {
            string moving = path + "." + Guid.NewGuid().ToString("N").Substring(0, 6) + ".rotating";
            File.Move(path, moving);
            Compress(moving, Path.GetFileNameWithoutExtension(path), Path.GetExtension(path));
        }

        // Finishes any archiving that a crash interrupted.
        public static void Recover()
        {
            try
            {
                foreach (string moving in Directory.GetFiles(Paths.Data, "*.rotating"))
                {
                    string original = Path.GetFileNameWithoutExtension(Path.GetFileNameWithoutExtension(moving)); // drop .xxxxxx.rotating
                    Compress(moving, Path.GetFileNameWithoutExtension(original), Path.GetExtension(original));
                }
            }
            catch { }
        }

        static void Compress(string source, string name, string ext)
        {
            Directory.CreateDirectory(Dir);
            string target = Path.Combine(Dir, name + "-" + File.GetLastWriteTime(source).ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + ext + ".gz");
            using (FileStream src = File.OpenRead(source))
            using (FileStream dst = File.Create(target))
            using (var gz = new GZipStream(dst, CompressionMode.Compress))
                src.CopyTo(gz);
            File.Delete(source);
        }
    }

    static class Log
    {
        static readonly object gate = new object();

        // Set by the tray app: its notes go through the core, which owns log.txt.
        public static Action<string> Sink = null;

        public static void Write(string message)
        {
            if (Sink != null)
            {
                try { Sink(message); }
                catch { }
                return;
            }
            string line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss  ", CultureInfo.InvariantCulture) + message + "\r\n";
            lock (gate)
            {
                Archive.Rotate(Paths.Log, 1024 * 1024);
                DurableFile.Append(Paths.Log, line, 10);
            }
        }
    }
}
