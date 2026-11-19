// Battery Guard - Copyright (C) 2026 Dion Fabian. Free software under the GNU General Public License v3.0; see LICENSE.
// BatteryGuardCore.exe - the protection service. Runs as a Windows service (SYSTEM), so it
// protects the laptop for every user, on the lock screen and with nobody logged in. (Not a
// scheduled task: Task Scheduler holds back background tasks while Battery Saver is on, and
// Windows turns that on early when the battery gauge is wrong.)
// It judges the charge from the battery's real voltage (the % gauge is wrong), warns through
// the tray app in each user's session, and hibernates before the battery cuts out.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Eventing.Reader;
using System.Globalization;
using System.IO;
using System.Management;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.ServiceProcess;
using System.Text;
using System.Threading;
using Microsoft.Win32.SafeHandles;

[assembly: AssemblyTitle("Battery Guard service")]
[assembly: AssemblyDescription("Battery Guard background service: watches the battery voltage and hibernates safely before it runs out")]

namespace BatteryGuard
{
    static class Native
    {
        // Includes time spent in sleep/hibernate.
        [DllImport("kernel32.dll")]
        public static extern ulong GetTickCount64();

        // Excludes time spent in sleep/hibernate (100 ns units).
        [DllImport("kernel32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool QueryUnbiasedInterruptTime(out ulong time);

        [DllImport("powrprof.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.U1)]
        public static extern bool SetSuspendState([MarshalAs(UnmanagedType.U1)] bool hibernate, [MarshalAs(UnmanagedType.U1)] bool force, [MarshalAs(UnmanagedType.U1)] bool disableWakeEvent);

        [DllImport("kernel32.dll")]
        public static extern bool CloseHandle(IntPtr handle);
    }

    struct BatteryReading
    {
        public bool OnAC;
        public bool Charging;
        public int VoltageMv;       // 0 = unknown
        public int RateMw;          // + charging, - discharging, int.MinValue = unknown
        public int RemainingMwh;    // the battery's own (wrong) count, -1 = unknown
        public int WindowsPercent;  // what the (wrong) gauge says
        public string Source;
    }

    // Reads the battery straight from its driver (IOCTL_BATTERY_QUERY_STATUS), WMI as fallback.
    class BatteryReader
    {
        static Guid batteryClass = new Guid("72631e54-78a4-11d0-bcf7-00aa00b7b32a");
        const uint IOCTL_BATTERY_QUERY_TAG = 0x294040;
        const uint IOCTL_BATTERY_QUERY_STATUS = 0x29404C;
        const uint IOCTL_BATTERY_QUERY_INFORMATION = 0x294044;

        [StructLayout(LayoutKind.Sequential)]
        struct SP_DEVICE_INTERFACE_DATA { public int cbSize; public Guid InterfaceClassGuid; public int Flags; public IntPtr Reserved; }

        [StructLayout(LayoutKind.Sequential)]
        struct BATTERY_WAIT_STATUS { public uint BatteryTag; public uint Timeout; public uint PowerState; public uint LowCapacity; public uint HighCapacity; }

        [StructLayout(LayoutKind.Sequential)]
        struct BATTERY_STATUS { public uint PowerState; public uint Capacity; public uint Voltage; public int Rate; }

        [StructLayout(LayoutKind.Sequential)]
        struct BATTERY_QUERY_INFORMATION { public uint BatteryTag; public int InformationLevel; public int AtRate; }

        [StructLayout(LayoutKind.Sequential)]
        struct BATTERY_INFORMATION
        {
            public uint Capabilities;
            public byte Technology, Reserved1, Reserved2, Reserved3;
            public uint Chemistry, DesignedCapacity, FullChargedCapacity, DefaultAlert1, DefaultAlert2, CriticalBias, CycleCount;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct SYSTEM_POWER_STATUS { public byte ACLineStatus, BatteryFlag, BatteryLifePercent, SystemStatusFlag; public int BatteryLifeTime, BatteryFullLifeTime; }

        [DllImport("kernel32.dll")]
        static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS status);

        [DllImport("setupapi.dll", SetLastError = true)]
        static extern IntPtr SetupDiGetClassDevs(ref Guid classGuid, IntPtr enumerator, IntPtr parent, uint flags);

        [DllImport("setupapi.dll", SetLastError = true)]
        static extern bool SetupDiEnumDeviceInterfaces(IntPtr devInfo, IntPtr devInfoData, ref Guid classGuid, uint index, ref SP_DEVICE_INTERFACE_DATA data);

        [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr devInfo, ref SP_DEVICE_INTERFACE_DATA data, IntPtr detail, int detailSize, out int required, IntPtr devInfoData);

        [DllImport("setupapi.dll")]
        static extern bool SetupDiDestroyDeviceInfoList(IntPtr devInfo);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool DeviceIoControl(SafeFileHandle device, uint code, ref uint input, int inputSize, out uint output, int outputSize, out int returned, IntPtr overlapped);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool DeviceIoControl(SafeFileHandle device, uint code, ref BATTERY_WAIT_STATUS input, int inputSize, out BATTERY_STATUS output, int outputSize, out int returned, IntPtr overlapped);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool DeviceIoControl(SafeFileHandle device, uint code, ref BATTERY_QUERY_INFORMATION input, int inputSize, out BATTERY_INFORMATION output, int outputSize, out int returned, IntPtr overlapped);

        SafeFileHandle device;
        uint tag;

        // The capacity the battery was designed for (mWh), or 0 if it doesn't say.
        public int DesignMwh()
        {
            try
            {
                if (device == null && !Open()) return 0;
                var q = new BATTERY_QUERY_INFORMATION { BatteryTag = tag, InformationLevel = 0 /* BatteryInformation */ };
                BATTERY_INFORMATION info;
                int returned;
                if (!DeviceIoControl(device, IOCTL_BATTERY_QUERY_INFORMATION, ref q, Marshal.SizeOf(typeof(BATTERY_QUERY_INFORMATION)),
                    out info, Marshal.SizeOf(typeof(BATTERY_INFORMATION)), out returned, IntPtr.Zero)) return 0;
                return info.DesignedCapacity > 0 && info.DesignedCapacity < 200000 ? (int)info.DesignedCapacity : 0;
            }
            catch { return 0; }
        }

        // What the battery itself calls full (mWh) - wrong on this battery, but the charger obeys it.
        public int FullMwh()
        {
            try
            {
                if (device == null && !Open()) return 0;
                var q = new BATTERY_QUERY_INFORMATION { BatteryTag = tag, InformationLevel = 0 /* BatteryInformation */ };
                BATTERY_INFORMATION info;
                int returned;
                if (!DeviceIoControl(device, IOCTL_BATTERY_QUERY_INFORMATION, ref q, Marshal.SizeOf(typeof(BATTERY_QUERY_INFORMATION)),
                    out info, Marshal.SizeOf(typeof(BATTERY_INFORMATION)), out returned, IntPtr.Zero)) return 0;
                return info.FullChargedCapacity > 0 && info.FullChargedCapacity < 200000 ? (int)info.FullChargedCapacity : 0;
            }
            catch { return 0; }
        }

        public BatteryReading Read()
        {
            var r = new BatteryReading { WindowsPercent = -1, RateMw = int.MinValue, RemainingMwh = -1, Source = "none" };
            SYSTEM_POWER_STATUS sps;
            bool acKnown = false;
            if (GetSystemPowerStatus(out sps))
            {
                acKnown = sps.ACLineStatus != 255;
                r.OnAC = sps.ACLineStatus == 1;
                r.Charging = (sps.BatteryFlag & 8) != 0;
                if (sps.BatteryLifePercent != 255) r.WindowsPercent = sps.BatteryLifePercent;
            }
            if (ReadDriver(ref r, acKnown))
            {
                r.Source = "driver";
                if (r.RateMw == int.MinValue && !r.OnAC)
                {
                    // Driver didn't give the power draw this time; WMI sometimes still has it.
                    var w = r;
                    if (ReadWmi(ref w, true) && w.RateMw != 0) r.RateMw = w.RateMw;
                }
            }
            else if (ReadWmi(ref r, acKnown)) r.Source = "wmi";
            return r;
        }

        bool ReadDriver(ref BatteryReading r, bool acKnown)
        {
            try
            {
                if (device == null && !Open()) return false;
                var wait = new BATTERY_WAIT_STATUS { BatteryTag = tag };
                BATTERY_STATUS bs;
                int returned;
                if (!DeviceIoControl(device, IOCTL_BATTERY_QUERY_STATUS, ref wait, Marshal.SizeOf(typeof(BATTERY_WAIT_STATUS)),
                    out bs, Marshal.SizeOf(typeof(BATTERY_STATUS)), out returned, IntPtr.Zero))
                {
                    Close(); // the tag changes if the battery is removed/re-detected
                    return false;
                }
                if (!acKnown) r.OnAC = (bs.PowerState & 1) != 0;
                r.Charging = (bs.PowerState & 4) != 0;
                r.VoltageMv = bs.Voltage == 0xFFFFFFFF ? 0 : (int)bs.Voltage;
                r.RateMw = bs.Rate; // int.MinValue = BATTERY_UNKNOWN_RATE
                r.RemainingMwh = bs.Capacity == 0xFFFFFFFF ? -1 : (int)bs.Capacity;
                return r.VoltageMv > 0;
            }
            catch
            {
                Close();
                return false;
            }
        }

        bool Open()
        {
            IntPtr devs = SetupDiGetClassDevs(ref batteryClass, IntPtr.Zero, IntPtr.Zero, 0x12); // DIGCF_PRESENT | DIGCF_DEVICEINTERFACE
            if (devs == new IntPtr(-1)) return false;
            try
            {
                var did = new SP_DEVICE_INTERFACE_DATA();
                did.cbSize = Marshal.SizeOf(did);
                if (!SetupDiEnumDeviceInterfaces(devs, IntPtr.Zero, ref batteryClass, 0, ref did)) return false;
                int size;
                SetupDiGetDeviceInterfaceDetail(devs, ref did, IntPtr.Zero, 0, out size, IntPtr.Zero);
                if (size <= 0) return false;
                IntPtr detail = Marshal.AllocHGlobal(size);
                try
                {
                    Marshal.WriteInt32(detail, IntPtr.Size == 8 ? 8 : 6); // cbSize of SP_DEVICE_INTERFACE_DETAIL_DATA_W
                    if (!SetupDiGetDeviceInterfaceDetail(devs, ref did, detail, size, out size, IntPtr.Zero)) return false;
                    string path = Marshal.PtrToStringUni(new IntPtr(detail.ToInt64() + 4));
                    device = CreateFile(path, 0x80000000 /* GENERIC_READ */, 3, IntPtr.Zero, 3 /* OPEN_EXISTING */, 0x80, IntPtr.Zero);
                    if (device.IsInvalid) { Close(); return false; }
                    uint timeout = 0, t;
                    int returned;
                    if (!DeviceIoControl(device, IOCTL_BATTERY_QUERY_TAG, ref timeout, 4, out t, 4, out returned, IntPtr.Zero) || t == 0)
                    {
                        Close();
                        return false;
                    }
                    tag = t;
                    return true;
                }
                finally { Marshal.FreeHGlobal(detail); }
            }
            finally { SetupDiDestroyDeviceInfoList(devs); }
        }

        void Close()
        {
            if (device != null) device.Dispose();
            device = null;
        }

        static bool ReadWmi(ref BatteryReading r, bool acKnown)
        {
            try
            {
                using (var searcher = new ManagementObjectSearcher(@"root\wmi", "SELECT PowerOnline, Charging, Voltage, ChargeRate, DischargeRate FROM BatteryStatus"))
                {
                    foreach (ManagementObject o in searcher.Get())
                    {
                        using (o)
                        {
                            if (!acKnown) r.OnAC = Convert.ToBoolean(o["PowerOnline"]);
                            r.Charging = Convert.ToBoolean(o["Charging"]);
                            r.VoltageMv = Convert.ToInt32(o["Voltage"]);
                            r.RateMw = Convert.ToInt32(o["ChargeRate"]) - Convert.ToInt32(o["DischargeRate"]);
                            return r.VoltageMv > 0;
                        }
                    }
                }
            }
            catch { }
            return false;
        }

        public static int DesignCells()
        {
            try
            {
                using (var searcher = new ManagementObjectSearcher("SELECT DesignVoltage FROM Win32_Battery"))
                {
                    foreach (ManagementObject o in searcher.Get())
                    {
                        using (o)
                        {
                            object v = o["DesignVoltage"];
                            if (v != null && Convert.ToDouble(v) > 0) return (int)Math.Round(Convert.ToDouble(v) / 3700.0);
                        }
                    }
                }
            }
            catch { }
            return 0;
        }

        public static int CellsFromVoltage(int mv)
        {
            if (mv < 6000) return 0;
            if (mv < 9000) return 2;
            if (mv < 13300) return 3;
            if (mv < 17600) return 4;
            return 0;
        }
    }

    // Reads/writes the active power plan's on-battery settings directly (no powercfg spawns).
    static class PowerPlan
    {
        public static Guid SubBattery = new Guid("e73a048d-bf27-4f12-9731-8b2076e8891f");
        public static Guid CritAction = new Guid("637ea02f-bbcb-4015-8e2c-a1c7b9c0b546");
        public static Guid LowAction = new Guid("d8742dcb-3e6a-4b3c-b3fe-374623cdcf06");
        public static Guid CritLevel = new Guid("9a66d8d7-4ff7-4ef9-b5a2-5a326ca2a469");
        public static Guid LowNotify = new Guid("bcded951-187b-4d05-bccc-f7e51960c258");
        public static Guid CritNotify = new Guid("5dbb7c9f-38e9-40d2-9749-4f8a0e9f640f");

        [DllImport("powrprof.dll")]
        static extern uint PowerGetActiveScheme(IntPtr root, out IntPtr scheme);

        [DllImport("powrprof.dll")]
        static extern uint PowerReadDCValueIndex(IntPtr root, ref Guid scheme, ref Guid sub, ref Guid setting, out uint value);

        [DllImport("powrprof.dll")]
        static extern uint PowerWriteDCValueIndex(IntPtr root, ref Guid scheme, ref Guid sub, ref Guid setting, uint value);

        [DllImport("powrprof.dll")]
        static extern uint PowerSetActiveScheme(IntPtr root, ref Guid scheme);

        [DllImport("kernel32.dll")]
        static extern IntPtr LocalFree(IntPtr p);

        // Makes the active plan match; returns true if anything had to change.
        public static bool Ensure(KeyValuePair<Guid, uint>[] wanted, out string error)
        {
            error = null;
            IntPtr p;
            if (PowerGetActiveScheme(IntPtr.Zero, out p) != 0) { error = "Could not read the active power plan."; return false; }
            Guid scheme = (Guid)Marshal.PtrToStructure(p, typeof(Guid));
            LocalFree(p);
            bool changed = false;
            foreach (var w in wanted)
            {
                Guid setting = w.Key;
                uint current;
                if (PowerReadDCValueIndex(IntPtr.Zero, ref scheme, ref SubBattery, ref setting, out current) == 0 && current == w.Value) continue;
                uint rc = PowerWriteDCValueIndex(IntPtr.Zero, ref scheme, ref SubBattery, ref setting, w.Value);
                if (rc != 0) error = "Could not change power setting " + setting + " (error " + rc + ").";
                else changed = true;
            }
            if (changed) PowerSetActiveScheme(IntPtr.Zero, ref scheme);
            return changed;
        }
    }

    // Instant notice of sleep, wake and charger changes, instead of waiting for the next reading.
    static class PowerEvents
    {
        public const uint Suspend = 4; // PBT_APMSUSPEND

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        public delegate uint Callback(IntPtr context, uint type, IntPtr setting);

        [StructLayout(LayoutKind.Sequential)]
        struct SubscribeParameters { public Callback Callback; public IntPtr Context; }

        [DllImport("powrprof.dll")]
        static extern uint PowerRegisterSuspendResumeNotification(uint flags, ref SubscribeParameters recipient, out IntPtr handle);

        [DllImport("powrprof.dll")]
        static extern uint PowerSettingRegisterNotification(ref Guid setting, uint flags, ref SubscribeParameters recipient, out IntPtr handle);

        static Callback keepAlive; // must outlive the subscription
        static Guid acDcSource = new Guid("5d3e9a59-e9d5-4b00-a6bd-ff34ff516548"); // GUID_ACDC_POWER_SOURCE

        public static bool Subscribe(Callback callback)
        {
            keepAlive = callback;
            var p = new SubscribeParameters { Callback = callback };
            IntPtr sleepHandle, chargerHandle;
            bool sleep = PowerRegisterSuspendResumeNotification(2 /* DEVICE_NOTIFY_CALLBACK */, ref p, out sleepHandle) == 0;
            bool charger = PowerSettingRegisterNotification(ref acDcSource, 2, ref p, out chargerHandle) == 0;
            return sleep && charger;
        }
    }

    static class Privileges
    {
        [StructLayout(LayoutKind.Sequential, Pack = 4)]
        struct TOKEN_PRIVILEGES { public int Count; public long Luid; public int Attributes; }

        [DllImport("advapi32.dll", SetLastError = true)]
        static extern bool OpenProcessToken(IntPtr process, int access, out IntPtr token);

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern bool LookupPrivilegeValue(string system, string name, out long luid);

        [DllImport("advapi32.dll", SetLastError = true)]
        static extern bool AdjustTokenPrivileges(IntPtr token, bool disableAll, ref TOKEN_PRIVILEGES state, int length, IntPtr previous, IntPtr returned);

        [DllImport("kernel32.dll")]
        static extern IntPtr GetCurrentProcess();

        public static void EnableShutdown()
        {
            IntPtr token;
            if (!OpenProcessToken(GetCurrentProcess(), 0x28 /* ADJUST_PRIVILEGES | QUERY */, out token)) return;
            try
            {
                var tp = new TOKEN_PRIVILEGES { Count = 1, Attributes = 2 /* SE_PRIVILEGE_ENABLED */ };
                if (LookupPrivilegeValue(null, "SeShutdownPrivilege", out tp.Luid))
                    AdjustTokenPrivileges(token, false, ref tp, 0, IntPtr.Zero, IntPtr.Zero);
            }
            finally { Native.CloseHandle(token); }
        }
    }

    // Starts the tray app in every logged-on user's session, and restarts it if it stops.
    class SessionLauncher
    {
        [StructLayout(LayoutKind.Sequential)]
        struct WTS_SESSION_INFO { public int SessionId; public IntPtr WinStationName; public int State; }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct STARTUPINFO
        {
            public int cb;
            public string lpReserved, lpDesktop, lpTitle;
            public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
            public short wShowWindow, cbReserved2;
            public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct PROCESS_INFORMATION { public IntPtr hProcess, hThread; public int dwProcessId, dwThreadId; }

        [DllImport("wtsapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern bool WTSEnumerateSessions(IntPtr server, int reserved, int version, out IntPtr info, out int count);

        [DllImport("wtsapi32.dll")]
        static extern void WTSFreeMemory(IntPtr memory);

        [DllImport("wtsapi32.dll", SetLastError = true)]
        static extern bool WTSQueryUserToken(int sessionId, out IntPtr token);

        [DllImport("advapi32.dll", SetLastError = true)]
        static extern bool DuplicateTokenEx(IntPtr existing, uint access, IntPtr attributes, int impersonationLevel, int tokenType, out IntPtr newToken);

        [DllImport("userenv.dll", SetLastError = true)]
        static extern bool CreateEnvironmentBlock(out IntPtr environment, IntPtr token, bool inherit);

        [DllImport("userenv.dll")]
        static extern bool DestroyEnvironmentBlock(IntPtr environment);

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern bool CreateProcessAsUser(IntPtr token, string application, StringBuilder commandLine, IntPtr processAttributes,
            IntPtr threadAttributes, bool inheritHandles, uint flags, IntPtr environment, string directory, ref STARTUPINFO startup, out PROCESS_INFORMATION info);

        readonly string exe;
        readonly Dictionary<int, DateTime> lastLaunch = new Dictionary<int, DateTime>();

        public SessionLauncher(string exe) { this.exe = exe; }

        public void EnsureRunning()
        {
            if (!File.Exists(exe)) return;
            var running = new HashSet<int>();
            foreach (Process p in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(exe)))
            {
                using (p) running.Add(p.SessionId);
            }
            IntPtr info;
            int count;
            if (!WTSEnumerateSessions(IntPtr.Zero, 0, 1, out info, out count)) return;
            try
            {
                int size = Marshal.SizeOf(typeof(WTS_SESSION_INFO));
                for (int i = 0; i < count; i++)
                {
                    var s = (WTS_SESSION_INFO)Marshal.PtrToStructure(new IntPtr(info.ToInt64() + i * size), typeof(WTS_SESSION_INFO));
                    bool userSession = s.State == 0 || s.State == 4; // WTSActive, WTSDisconnected
                    if (s.SessionId == 0 || !userSession || running.Contains(s.SessionId)) continue;
                    DateTime last;
                    if (lastLaunch.TryGetValue(s.SessionId, out last) && (DateTime.UtcNow - last).TotalMinutes < 5) continue;
                    Launch(s.SessionId);
                }
            }
            finally { WTSFreeMemory(info); }
        }

        void Launch(int sessionId)
        {
            IntPtr userToken;
            if (!WTSQueryUserToken(sessionId, out userToken)) return; // nobody signed in to this session yet
            lastLaunch[sessionId] = DateTime.UtcNow;
            IntPtr primary = IntPtr.Zero, env = IntPtr.Zero;
            try
            {
                if (!DuplicateTokenEx(userToken, 0x02000000 /* MAXIMUM_ALLOWED */, IntPtr.Zero, 2, 1 /* primary */, out primary))
                {
                    Log.Write("Could not start the tray app in session " + sessionId + ": token error " + Marshal.GetLastWin32Error());
                    return;
                }
                CreateEnvironmentBlock(out env, primary, false);
                var si = new STARTUPINFO { lpDesktop = "winsta0\\default" };
                si.cb = Marshal.SizeOf(typeof(STARTUPINFO));
                PROCESS_INFORMATION pi;
                var cmd = new StringBuilder("\"" + exe + "\"");
                if (CreateProcessAsUser(primary, exe, cmd, IntPtr.Zero, IntPtr.Zero, false, 0x400 /* CREATE_UNICODE_ENVIRONMENT */,
                    env, Path.GetDirectoryName(exe), ref si, out pi))
                {
                    Native.CloseHandle(pi.hProcess);
                    Native.CloseHandle(pi.hThread);
                    Log.Write("Started the tray app in session " + sessionId + ".");
                }
                else Log.Write("Could not start the tray app in session " + sessionId + ": error " + Marshal.GetLastWin32Error());
            }
            finally
            {
                if (env != IntPtr.Zero) DestroyEnvironmentBlock(env);
                if (primary != IntPtr.Zero) Native.CloseHandle(primary);
                Native.CloseHandle(userToken);
            }
        }
    }

    class Core
    {
        const int MedianReadings = 6;       // ~1 minute of readings on battery

        readonly BatteryReader reader = new BatteryReader();
        readonly FuelGauge gauge = new FuelGauge();
        readonly SessionLauncher launcher;
        Settings cfg = new Settings();
        DateTime settingsStamp;
        int cells;

        readonly List<int> volts = new List<int>();
        ulong lastTotalMs, lastUnbiased;
        double awakeMin, simStartMin = -1, notChargingMin;
        double historyAccum = 999, launchAccum = 999, stateAccum = 999, gaugeAccum;
        bool? lastOnAC;
        int failedReads, criticalReads, lowReads, ticks, savedCells;
        string lastEvent = "";
        bool forceHistory;
        readonly object tickLock = new object();
        readonly AutoResetEvent wake = new AutoResetEvent(false);
        readonly ManualResetEvent halt = new ManualResetEvent(false);
        string haltReason = "Service stopped for an update or uninstall.";
        long lastWindowsEventTicks;
        double importAccum = 999;
        // Charge prediction and the guided full-charge cycle (see TrackCharging).
        bool counterEmpty, expectFull, limited, modeDecided, attachedAwake, extraUsed;
        // Whether this PC's charger has been seen stopping because the battery's own gauge says
        // full while it really isn't. Until then there's nothing to predict or guide.
        bool chargerQuirk, chargeEndKnown, attachGapKnown;
        int cycleSeq, fullMwh, lastBatteryCounter = -1, attachCounter = -1;
        double attachPct = -1, attachInMwh, stopPct = -1, limitExtraMwh = 3000, cycleEtaMin = -1;
        // What decides a full charge: time since the last charge ended, and energy used since.
        long lastChargeEndTicks;
        double dischargedMwh, drainMw = 9000, runStartMin, attachGapH = -1, attachDischargedMwh, batteryMinLeft;
        // Starting points (from the battery it was developed on); learned on each PC.
        double needGapHours = 9.5, bestFullGapH = 9.5, worstFailGapH = 4.7;
        double needDischargeMwh = 2600, bestFullDischargeMwh = 2600, worstFailDischargeMwh = 550;
        int runStartCounter = -1, lastCounter = -1;
        bool readyForFull, pendingCycle, captured;
        string lastCycle = "";
        DateTime readyAtUtc;
        string cycle = "";   // "" | wait | unplug | drain | ready
        string lastStatusBody = "";
        DateTime lastStatusUtc;
        // Readings wait here for up to a minute and go to disk together; events, low battery and
        // sleep write straight away.
        readonly StringBuilder historyBuffer = new StringBuilder();
        DateTime historyBufferSince;
        FileSystemWatcher inboxWatcher;
        EventWaitHandle stopSignal;

        // Charging sessions and times, for the summary (persisted).
        long onBatterySinceTicks, pluggedSinceTicks, chargingSinceTicks, lastFullTicks;
        double sessionStartPct = -1, sessionInMwh;
        string lastCharge = "", lastChargeState = "";
        string protection = "", lastProfileError;

        // Learning the battery: automatically on a new PC (down to the safety limit), or with
        // "Learn my battery" in Settings (until the battery itself gives out, to find its true limit).
        //   charge -> drain -> recharge -> done
        const int AbsoluteMinMv = 3000;     // lowest a lithium cell should ever go, under load
        string learnState = "", learnMode = "", learnSummary = "";
        int learnSeq, cutoffMv, absoluteReads, pendingCutoffMv;
        double learnRunMin = -1, learnPointAccum;
        bool learnWarned;
        string LearnRunPath { get { return Path.Combine(Paths.Data, "learn-run.csv"); } }
        bool? profileArmed;
        bool hibernateRequested;

        string phase = "Normal";
        int warningSeq, countdownSeq;
        DateTime countdownEndUtc;
        bool emergency, postponeUsed;
        string message = "";

        // This tick's measurements, for the status file.
        BatteryReading reading;
        int packMv, rawMv, floorMv;
        double perCell;

        // Persisted in state.ini.
        bool lastOnBattery;
        int lastPerCellMv, learnedFloorMv;
        long lastUtcTicks;
        string learned = "";

        public Core()
        {
            bool isSystem;
            using (WindowsIdentity id = WindowsIdentity.GetCurrent()) isSystem = id.IsSystem;
            if (isSystem && !Paths.Custom)
                launcher = new SessionLauncher(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "BatteryGuard.exe"));
            stopSignal = CreateStopSignal();
        }

        // The installer sets this to stop the service cleanly (nothing buffered is lost). Only
        // SYSTEM and administrators may set it.
        static EventWaitHandle CreateStopSignal()
        {
            try
            {
                var security = new EventWaitHandleSecurity();
                foreach (WellKnownSidType sid in new[] { WellKnownSidType.LocalSystemSid, WellKnownSidType.BuiltinAdministratorsSid })
                    security.AddAccessRule(new EventWaitHandleAccessRule(new SecurityIdentifier(sid, null), EventWaitHandleRights.FullControl, AccessControlType.Allow));
                bool created;
                var signal = new EventWaitHandle(false, EventResetMode.ManualReset, "Global\\BatteryGuardCoreStop" + Paths.Suffix, out created, security);
                if (!created) signal.Reset(); // left over from the copy that just stopped
                return signal;
            }
            catch { return null; }
        }

        public void Run()
        {
            Settings s = Settings.Load();
            if (s != null) cfg = s;
            if (!File.Exists(Paths.Settings)) cfg.Save();
            settingsStamp = File.GetLastWriteTimeUtc(Paths.Settings);
            foreach (string f in Directory.GetFiles(Paths.Data, "cmd-*.txt")) TryDelete(f); // from older versions

            LoadState();
            LearnFromSuddenShutdown();
            cells = cfg.Cells > 0 ? cfg.Cells : savedCells > 0 ? savedCells : BatteryReader.DesignCells();
            BatteryReading r = reader.Read();
            if (cells <= 0) cells = BatteryReader.CellsFromVoltage(r.VoltageMv);
            savedCells = cells;
            int design = reader.DesignMwh(), full = reader.FullMwh();
            if (full > 0) fullMwh = full;
            gauge.Load(Ini.Read(GaugePath));
            bool firstRun = gauge.Fresh;
            if (firstRun) gauge.SeedCapacity(design); // first run on this PC
            if (pendingCutoffMv > 0) FinishTrueLimit(pendingCutoffMv, true); // the learning run ended as it should: the battery gave out
            floorMv = Math.Max(cfg.FloorPerCellMv, learnedFloorMv);
            Event(string.Format("Core started{0} (version {9}). Battery {1} mV, {2} mW via {3}, {4} cells, design {5} mWh, {6}, Windows gauge {7}%, hibernate floor {8} mV/cell.",
                cfg.DryRun == 1 ? " [TEST MODE]" : "", r.VoltageMv, r.RateMw == int.MinValue ? "?" : r.RateMw.ToString(CultureInfo.InvariantCulture),
                r.Source, cells, design, r.OnAC ? "on AC" : "on battery", r.WindowsPercent, floorMv, Product.Version));
            Event("Gauge: " + gauge.Summary(floorMv));
            bool canSleepDeep;
            if (!PowerCaps.CanHibernate(out canSleepDeep))
                Event(canSleepDeep
                    ? "Hibernation is turned off on this PC, so at the safety limit it will shut down instead. To turn it on again, run as administrator: powercfg /hibernate on"
                    : "This PC can't hibernate, so at the safety limit it will shut down instead.");
            // A new PC learns its battery's real range first: full, then until it gives out.
            if (firstRun && learnState == "") StartLearning("cutoff");
            WatchInbox();

            Archive.Recover();
            // Was there a stretch with no readings (laptop off, service restarting or updated)?
            Status previous = Status.Load();
            if (previous != null && previous.UpdatedUtc.Ticks > 0 && (DateTime.UtcNow - previous.UpdatedUtc).TotalMinutes > 2)
            {
                TimeSpan gap = DateTime.UtcNow - previous.UpdatedUtc;
                Event(string.Format(CultureInfo.InvariantCulture, "No readings since {0:yyyy-MM-dd HH:mm:ss} ({1:0}h {2:00}m): the service wasn't running. Windows' own power events for that time follow.",
                    previous.UpdatedUtc.ToLocalTime(), Math.Floor(gap.TotalHours), gap.Minutes));
            }
            if (previous == null || (DateTime.UtcNow - previous.UpdatedUtc).TotalMinutes > 2)
            {
                // The laptop was off: whatever charge is running now began without us watching.
                modeDecided = expectFull = limited = attachedAwake = false;
                attachCounter = -1;
                attachPct = stopPct = -1;
                attachInMwh = 0;
                captured = false;
                if (learnState == "drain" && learnRunMin >= 0)
                {
                    // It was switched off or restarted in the middle of a learning run (not by the battery).
                    Event(string.Format(CultureInfo.InvariantCulture, "Learning run stopped after {0:0} min: the laptop was off. It starts again the next time it's unplugged.", learnRunMin));
                    learnRunMin = -1;
                }
            }
            if (!PowerEvents.Subscribe(OnPowerEvent)) Event("Couldn't subscribe to instant power notifications; relying on regular readings.");

            Native.QueryUnbiasedInterruptTime(out lastUnbiased);
            lastTotalMs = Native.GetTickCount64();
            WaitHandle[] waits = stopSignal != null ? new WaitHandle[] { wake, halt, stopSignal } : new WaitHandle[] { wake, halt };
            while (true)
            {
                lock (tickLock)
                {
                    try { Tick(); }
                    catch (Exception ex) { Event("Tick error: " + ex); }
                }
                // Every second in a countdown, 10 s on battery, 15 s while charging, 30 s when idle on the charger.
                int wait = phase == "Countdown" ? 1000 : lastOnAC != true ? 10000 : gauge.ChargeState == "Charging" || cycle != "" ? 15000 : 30000;
                int woken = WaitHandle.WaitAny(waits, wait);
                if (woken != WaitHandle.WaitTimeout && woken >= 1) break; // halt or stop signal

            }
            lock (tickLock)
            {
                Event(haltReason);
                WriteHistory();
                SaveState();
                SaveGauge();
            }
        }

        // From the service: stop after saving everything.
        public void Stop(string reason)
        {
            haltReason = reason;
            halt.Set();
        }

        // Someone signed in or came back: start their tray app now rather than at the next check.
        public void SessionChanged()
        {
            launchAccum = 999;
            wake.Set();
        }

        // Requests from the tray apps are handled the moment they arrive.
        void WatchInbox()
        {
            try
            {
                if (!Directory.Exists(Paths.Inbox))
                {
                    Directory.CreateDirectory(Paths.Inbox);
                    if (launcher != null) InboxAccess.Open(Paths.Inbox); // installed copy: let the tray apps in
                }
                inboxWatcher = new FileSystemWatcher(Paths.Inbox, "cmd-*.txt") { NotifyFilter = NotifyFilters.FileName, IncludeSubdirectories = false };
                inboxWatcher.Created += delegate { wake.Set(); };
                inboxWatcher.Renamed += delegate { wake.Set(); };
                inboxWatcher.EnableRaisingEvents = true;
            }
            catch (Exception ex) { Event("Couldn't watch for requests from the tray apps (" + ex.Message + "); they're picked up at each reading instead."); }
        }

        void Tick()
        {
            ulong totalMs = Native.GetTickCount64();
            ulong unbiased;
            Native.QueryUnbiasedInterruptTime(out unbiased);
            double totalSec = (totalMs - lastTotalMs) / 1000.0;
            double awakeSec = Math.Min(totalSec, (unbiased - lastUnbiased) / 1e7);
            lastTotalMs = totalMs;
            lastUnbiased = unbiased;
            awakeMin += awakeSec / 60;
            ticks++;
            bool slept = totalSec - awakeSec > 5;
            if (slept)
            {
                // Voltage recovers while resting, so start the readings afresh after sleep.
                Event(string.Format("Resumed after {0:0.0} min asleep.", (totalSec - awakeSec) / 60));
                ResetWindow();
                phase = "Normal";
                importAccum = 999;
            }

            ReloadSettings();
            reading = reader.Read();
            bool onAC = reading.OnAC;
            rawMv = reading.VoltageMv;
            if (cfg.SimulateBattery == 1)
            {
                if (simStartMin < 0) simStartMin = awakeMin;
                onAC = false;
                reading.Charging = false;
                rawMv = (int)(cfg.SimStartMv - cfg.SimDropMvPerMin * (awakeMin - simStartMin));
                reading.RateMw = -cfg.SimRateMw - (int)(awakeMin * 60) % 7 * 13; // jitter like a real reading
            }
            reading.OnAC = onAC;

            bool acChanged = lastOnAC.HasValue && lastOnAC != onAC;
            if (lastOnAC != onAC)
            {
                if (lastOnAC.HasValue) Event(onAC ? "Charger connected." : string.Format("Charger disconnected ({0} mV).", rawMv));
                if (onAC && phase == "Countdown") Event("Countdown cancelled: charger connected.");
                if (lastOnAC.HasValue)
                {
                    if (onAC) pluggedSinceTicks = DateTime.UtcNow.Ticks;
                    else onBatterySinceTicks = DateTime.UtcNow.Ticks;
                }
                lastOnAC = onAC;
                ResetWindow();
                phase = "Normal";
                if (onAC) postponeUsed = false;
                stateAccum = 999;
            }

            failedReads = rawMv > 0 ? 0 : failedReads + 1;
            if (cells <= 0 && rawMv > 0) cells = BatteryReader.CellsFromVoltage(rawMv);
            if (!onAC && rawMv > 0)
            {
                volts.Add(rawMv);
                if (volts.Count > MedianReadings) volts.RemoveAt(0);
            }
            packMv = !onAC && volts.Count > 0 ? Median(volts) : rawMv;
            perCell = cells > 0 && packMv > 0 ? (double)packMv / cells : 0;
            floorMv = Math.Max(cfg.FloorPerCellMv, learnedFloorMv);

            gauge.Update(new GaugeInput
            {
                OnAC = onAC,
                Charging = reading.Charging,
                PackMv = rawMv,
                MedianCellMv = !onAC ? perCell : 0,
                RateMw = reading.RateMw,
                Cells = cells,
                FloorMv = floorMv,
                AwakeHours = awakeSec / 3600,
                SleptHours = Math.Max(0, totalSec - awakeSec) / 3600,
            });
            NoteGaugeEvent();
            TrackCharging(onAC, acChanged, slept, awakeSec);
            TrackLearning(onAC, awakeSec);
            TrackSessions(onAC, awakeSec);

            string prot = cfg.Enabled != 1 ? "Off" : failedReads >= 3 || cells <= 0 ? "Fallback" : "Active";
            if (prot != protection)
            {
                Event("Protection: " + (prot == "Active" ? "active (voltage based)"
                    : prot == "Off" ? "turned off in settings (Windows' own battery rules apply)"
                    : "fallback - can't read the battery voltage, so Windows' own battery rules apply"));
                protection = prot;
            }
            EnsurePowerProfile(prot == "Active");

            ProcessCommands();
            if (prot == "Active" && !onAC && perCell > 0) UpdatePhase();
            else if (phase != "Normal") phase = "Normal";

            if (phase == "Countdown" && (hibernateRequested || DateTime.UtcNow >= countdownEndUtc)) Hibernate();
            hibernateRequested = false;

            WriteStatus();

            historyAccum += awakeSec;
            bool idleOnAC = onAC && gauge.ChargeState != "Charging";
            // Every reading is kept. Most wait in memory for up to a minute and are written
            // together (one disk write instead of six); anything that happened, a low battery
            // and the countdown go to disk at once.
            bool trueLimitRun = !onAC && learnState == "drain" && learnMode == "cutoff";
            bool urgent = forceHistory || phase != "Normal" || trueLimitRun || (!onAC && (gauge.Percent < 15 || perCell <= floorMv + 100));
            if (forceHistory || historyAccum >= (idleOnAC ? 60 : 0)) { WriteHistory(urgent); historyAccum = 0; forceHistory = false; }

            stateAccum += awakeSec;
            if (stateAccum >= (onAC ? 300 : gauge.Percent < 15 || trueLimitRun ? 10 : 60))
            {
                lastOnBattery = !onAC;
                if (!onAC && rawMv > 0 && cells > 0) lastPerCellMv = rawMv / cells;
                lastUtcTicks = DateTime.UtcNow.Ticks;
                SaveState();
                stateAccum = 0;
            }
            gaugeAccum += awakeSec;
            if (gaugeAccum >= 60) { SaveGauge(); gaugeAccum = 0; }

            importAccum += awakeSec;
            if (importAccum >= 1800) { importAccum = 0; ImportWindowsEvents(); Memory.Trim(); }
            if (ticks == 3) Memory.Trim();

            launchAccum += awakeSec;
            if (launcher != null && launchAccum >= 60)
            {
                launchAccum = 0;
                try { launcher.EnsureRunning(); }
                catch (Exception ex) { Event("Tray app launcher error: " + ex.Message); }
            }
        }

        // Something happened: into log.txt, and stamped on a battery-log line written right away.
        void Event(string message)
        {
            Log.Write(message);
            lastEvent = lastEvent.Length == 0 ? message : lastEvent + " | " + message;
            forceHistory = true;
        }

        // Windows tells us the moment it's about to sleep, wakes up, or the charger changes.
        uint OnPowerEvent(IntPtr context, uint type, IntPtr setting)
        {
            try
            {
                if (type == PowerEvents.Suspend)
                {
                    // Record the exact state before it sleeps; Windows waits briefly for this.
                    if (Monitor.TryEnter(tickLock, 1500))
                    {
                        try
                        {
                            Tick();
                            Event("Going to sleep or hibernate.");
                            WriteHistory(); // and everything waiting with it
                            forceHistory = false;
                            SaveState();
                            SaveGauge();
                        }
                        finally { Monitor.Exit(tickLock); }
                    }
                    else Log.Write("Going to sleep or hibernate.");
                }
                else wake.Set(); // woke up, or the charger was plugged in / unplugged: read now
            }
            catch { }
            return 0;
        }

        // Copies Windows' own power events (sleep, wake, shutdown, start-up, power loss, charger
        // changes) into log.txt, including the ones from times this service wasn't running.
        void ImportWindowsEvents()
        {
            DateTime since = lastWindowsEventTicks > 0 ? new DateTime(lastWindowsEventTicks, DateTimeKind.Utc) : DateTime.UtcNow.AddDays(-2);
            string query = "*[System[(" +
                "(Provider[@Name='Microsoft-Windows-Kernel-Power'] and (EventID=41 or EventID=42 or EventID=105 or EventID=107 or EventID=109 or EventID=524)) or " +
                "(Provider[@Name='User32'] and EventID=1074) or (Provider[@Name='EventLog'] and EventID=6008) or " +
                "(Provider[@Name='Microsoft-Windows-Kernel-General'] and (EventID=12 or EventID=13))" +
                ") and TimeCreated[@SystemTime>'" + since.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture) + "']]]";
            try
            {
                int count = 0;
                using (var events = new EventLogReader(new EventLogQuery("System", PathType.LogName, query)))
                {
                    EventRecord e;
                    while (count < 500 && (e = events.ReadEvent()) != null)
                    {
                        using (e)
                        {
                            if (!e.TimeCreated.HasValue) continue;
                            Log.Write("Windows event at " + e.TimeCreated.Value.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + ": " + Describe(e));
                            lastWindowsEventTicks = Math.Max(lastWindowsEventTicks, e.TimeCreated.Value.ToUniversalTime().Ticks + 1);
                            count++;
                        }
                    }
                }
                if (lastWindowsEventTicks == 0) lastWindowsEventTicks = DateTime.UtcNow.Ticks;
                if (count > 0) SaveState();
            }
            catch (Exception ex) { Log.Write("Couldn't read Windows' power events: " + ex.Message); }
        }

        static string Describe(EventRecord e)
        {
            string text = null;
            try { text = e.FormatDescription(); }
            catch { }
            string[] lines = (text ?? "").Replace("\r", "").Split('\n');
            string summary = lines[0].Trim();
            foreach (string l in lines)
                if (l.Contains("Sleep Reason") || l.Contains("Reason Code") || l.Contains("Shutdown Type")) summary += " " + l.Trim();
            if (summary.Length > 240) summary = summary.Substring(0, 240) + "...";
            return summary + " [" + e.ProviderName.Replace("Microsoft-Windows-", "") + " " + e.Id + "]";
        }

        void NoteGaugeEvent()
        {
            if (gauge.Event == null) return;
            Event("Gauge: " + gauge.Event);
        }

        void UpdatePhase()
        {
            int rawPerCell = rawMv / cells;
            if (learnState == "drain" && learnMode == "cutoff")
            {
                TrueLimitRun(rawPerCell);
                return;
            }
            // Far below the limit (a sudden collapse under load): hibernate within seconds. Once the
            // battery's true cut-out point is known, that point sets how far below is too far.
            int emergencyMv = cutoffMv > 0 ? Math.Max(floorMv - 150, (floorMv + cutoffMv) / 2) : floorMv - 150;
            criticalReads = rawMv > 0 && rawPerCell <= emergencyMv ? criticalReads + 1 : 0;
            double left = (countdownEndUtc - DateTime.UtcNow).TotalSeconds;
            if (criticalReads >= 2 && (phase != "Countdown" || (!emergency && left > 10)))
            {
                StartCountdown(10, true, string.Format(CultureInfo.InvariantCulture, "Battery voltage is critically low ({0:0.00} V per cell).", rawPerCell / 1000.0));
                return;
            }
            if (phase == "Countdown") return;

            bool settled = volts.Count >= 4; // ~40 s of readings since unplug/resume
            if (!settled) return;
            if (perCell <= floorMv)
            {
                StartCountdown(cfg.CountdownSeconds, false, "The battery has reached its safety limit.");
                return;
            }
            // Time left comes from the learned curve, scaled by how hard the laptop is working now.
            // Both the countdown and the warning need it to hold for two readings, so a brief
            // voltage dip under a burst of load doesn't set them off.
            double minutesLeft = gauge.WarnMinutesLeft;
            // Until this battery has been learned, its "time left" is only an estimate: then the
            // countdown starts only when the voltage itself reaches the limit, never early.
            bool soon = gauge.Calibrated && minutesLeft >= 0 && minutesLeft * 60 <= cfg.CountdownSeconds;
            bool low = minutesLeft >= 0 && minutesLeft <= cfg.WarnMinutes;
            lowReads = soon || low ? lowReads + 1 : 0;
            if (soon && lowReads >= 2)
            {
                StartCountdown(cfg.CountdownSeconds, false, "About a minute of battery left.");
                return;
            }
            if (phase == "Normal" && low && lowReads >= 2)
            {
                phase = "Warning";
                warningSeq++;
                message = string.Format("About {0:0} min of battery left.", Math.Max(1, minutesLeft));
                Event(string.Format(CultureInfo.InvariantCulture, "Warning: {0:0}% left, ~{1:0.0} min at {2:0.0}x normal use ({3:0.000} V/cell).",
                    gauge.Percent, minutesLeft, gauge.Speed, perCell / 1000));
            }
            else if (phase == "Warning" && minutesLeft > cfg.WarnMinutes + 5)
            {
                phase = "Normal";
                Event("Warning cleared: battery recovered.");
            }
        }

        void StartCountdown(int seconds, bool isEmergency, string why)
        {
            StartCountdown(seconds, isEmergency, why, true);
        }

        void StartCountdown(int seconds, bool isEmergency, string why, bool teachGauge)
        {
            phase = "Countdown";
            countdownSeq++;
            emergency = isEmergency;
            countdownEndUtc = DateTime.UtcNow.AddSeconds(seconds);
            message = why;
            Event(string.Format(CultureInfo.InvariantCulture, "Countdown {0} s: {1} ({2:0.000} V/cell, gauge said {3:0}%)", seconds, why, perCell / 1000, gauge.Percent));
            if (!teachGauge) return;
            // The battery is (about) empty: a known point the gauge learns from.
            gauge.ReachedFloor(floorMv, isEmergency || perCell <= floorMv ? 0 : Math.Max(0, gauge.WarnMinutesLeft));
            NoteGaugeEvent();
            SaveGauge();
            // First-use learning (to the safety limit) is done once a run down to it has been learned.
            if ((learnState == "charge" || learnState == "drain") && learnMode == "safe" && gauge.LastRunLearned)
            {
                learnState = "recharge";
                learnSeq++;
                Event("Learning: the run down to the safety limit is learned. Last step: charge to 100%.");
                SaveState();
            }
        }

        // "Learn my battery": keeps going until the battery itself gives out, to find its true limit.
        // Battery Guard doesn't hibernate - except below the lowest voltage a lithium cell should
        // ever reach, where the run ends there instead.
        void TrueLimitRun(int rawPerCell)
        {
            if (phase == "Countdown") return;
            absoluteReads = rawMv > 0 && rawPerCell <= AbsoluteMinMv ? absoluteReads + 1 : 0;
            if (absoluteReads >= 2)
            {
                FinishTrueLimit(AbsoluteMinMv, false);
                StartCountdown(10, true, string.Format(CultureInfo.InvariantCulture,
                    "The battery reached {0:0.00} V per cell, the lowest a lithium cell should go: learning stops here.", AbsoluteMinMv / 1000.0), false);
                return;
            }
            bool settled = volts.Count >= 4;
            if (settled && !learnWarned && phase == "Normal" && (perCell <= floorMv || (gauge.Percent >= 0 && gauge.Percent < 8)))
            {
                learnWarned = true;
                phase = "Warning";
                warningSeq++;
                message = "Learning the battery's true limit: the laptop will switch itself off soon, without hibernating. Save your work now.";
                Event(string.Format(CultureInfo.InvariantCulture, "Learning: close to empty ({0:0.000} V/cell, gauge {1:0}%); warned that it will switch off by itself.", perCell / 1000, gauge.Percent));
                SaveState();
            }
        }

        string GaugePath { get { return Path.Combine(Paths.Data, "gauge.ini"); } }

        void SaveGauge()
        {
            try { Ini.Write(GaugePath, gauge.Save()); }
            catch (Exception ex) { Event("Gauge save error: " + ex.Message); }
        }

        void Hibernate()
        {
            Event("Hibernating.");
            phase = "Hibernating";
            WriteStatus();
            WriteHistory(); // the last reading before it sleeps, stamped now rather than after waking
            forceHistory = false;
            lastOnBattery = true;
            lastUtcTicks = DateTime.UtcNow.Ticks;
            SaveState();
            SaveGauge();
            if (cfg.DryRun == 1)
            {
                Event("[test mode] would hibernate now.");
                simStartMin = awakeMin;
            }
            else
            {
                bool ok = false;
                int error = 0;
                try
                {
                    Privileges.EnableShutdown();
                    ok = Native.SetSuspendState(true, true, false);
                    if (!ok) error = Marshal.GetLastWin32Error();
                }
                catch (Exception ex) { Event("SetSuspendState error: " + ex.Message); }
                if (!ok)
                {
                    Event("SetSuspendState failed (" + error + "), using shutdown /h.");
                    int code = RunTool("shutdown.exe", "/h");
                    if (code != 0)
                    {
                        // No hibernation on this PC: a proper shutdown beats the battery cutting out.
                        Event("Hibernation isn't available (shutdown /h returned " + code + "), so shutting down instead.");
                        RunTool("shutdown.exe", "/s /t 0");
                    }
                    Thread.Sleep(20000);
                }
            }
            phase = "Normal";
            ResetWindow();
        }

        int RunTool(string exe, string args)
        {
            try
            {
                using (Process p = Process.Start(new ProcessStartInfo(exe, args) { CreateNoWindow = true, UseShellExecute = false }))
                    return p.WaitForExit(15000) ? p.ExitCode : -1;
            }
            catch (Exception ex)
            {
                Event(exe + " " + args + " error: " + ex.Message);
                return -1;
            }
        }

        // Requests from the tray apps: inbox\cmd-*.txt, first line the command, then any details.
        // Users can write to inbox\, so it's read with care: never through a link, small files only.
        void ProcessCommands()
        {
            string[] files;
            try
            {
                var dir = new DirectoryInfo(Paths.Inbox);
                if (!dir.Exists || (dir.Attributes & FileAttributes.ReparsePoint) != 0) return;
                files = Directory.GetFiles(Paths.Inbox, "cmd-*.txt");
            }
            catch { return; }
            int handled = 0;
            foreach (string f in files)
            {
                if (handled++ >= 20) break;
                string text = null;
                try
                {
                    var fi = new FileInfo(f);
                    if ((fi.Attributes & FileAttributes.ReparsePoint) == 0 && fi.Length <= 16384) text = File.ReadAllText(f);
                }
                catch { continue; } // still being written: next time
                TryDelete(f);
                if (text == null) continue;
                text = text.Trim();
                int nl = text.IndexOf('\n');
                string cmd = (nl < 0 ? text : text.Substring(0, nl)).Trim().ToLowerInvariant();
                string body = nl < 0 ? "" : text.Substring(nl + 1);
                if (cmd == "log")
                {
                    string note = Ini.OneLine(body).Trim();
                    if (note.Length > 600) note = note.Substring(0, 600) + "...";
                    if (note.Length > 0) Log.Write(note);
                }
                else if (cmd == "settings")
                {
                    try
                    {
                        Settings.From(Ini.Parse(body.Split('\n'))).Save(); // re-written in full, values checked
                        ReloadSettings();
                    }
                    catch (Exception ex) { Event("Couldn't save settings from the tray app: " + ex.Message); }
                }
                else if (cmd == "hibernate" && phase == "Countdown")
                {
                    Event("Hibernate now: requested from the countdown window.");
                    hibernateRequested = true;
                }
                else if (cmd == "postpone" && phase == "Countdown" && PostponeAvailable)
                {
                    postponeUsed = true;
                    countdownEndUtc = countdownEndUtc.AddMinutes(2);
                    Event("Countdown postponed by 2 minutes.");
                }
                else if (cmd == "learn") StartLearning("cutoff");
                else if (cmd == "learnsafe") StartLearning("safe");
                else if (cmd == "learnstop") StopLearning();
                else if (cmd == "resetlearned")
                {
                    learnedFloorMv = 0;
                    learned = "";
                    SaveState();
                    gauge.ResetLearning();
                    SaveGauge();
                    Event("Everything learned was reset (safety floor and battery gauge).");
                }
            }
        }

        // Batteries whose own gauge is wrong often make the charger stop early: it obeys the
        // battery's idea of "full". On the battery this was developed on (21 charging episodes):
        //  - A charge that starts within about 5 hours of the previous charge ending stops once the
        //    pack's counter reads full plus about 3 Wh - at most ~7.4 Wh - whether the laptop was
        //    awake, asleep, hibernated or off, and whether or not the counter was at 0%.
        //  - A charge that starts 9.5 hours or more after the previous one ended, with at least
        //    ~2.6 Wh used from the battery in between, goes all the way to full.
        //  - Long after the previous charge but with very little used since: stopped early.
        // Nothing is assumed on a new PC. Once its charger is seen stopping early while the battery
        // reports itself full, the time since the last charge and the energy used since are
        // tracked, each charge is predicted when it starts, both thresholds are learned from every
        // outcome (starting from the values above), and after a charge stops short the user is
        // told when the next full charge is possible.
        void TrackCharging(bool onAC, bool acChanged, bool slept, double awakeSec)
        {
            int counter = reading.RemainingMwh;
            DateTime now = DateTime.UtcNow;
            string state = gauge.ChargeState;
            bool charging = onAC && state == "Charging";
            if (lastChargeEndTicks == 0) lastChargeEndTicks = now.Ticks; // not known: assume just now
            double gapH = Math.Max(0, (now - new DateTime(lastChargeEndTicks, DateTimeKind.Utc)).TotalHours);

            // The charger just went in, or a charge is already running when we first look: note
            // how long it had been and how much had been used, before this charge resets them.
            if (!onAC) captured = false;
            else if ((acChanged || (charging && !modeDecided)) && !captured)
            {
                captured = true;
                attachGapH = gapH;
                attachGapKnown = chargeEndKnown;
                attachDischargedMwh = dischargedMwh;
                attachCounter = lastCounter;
                attachPct = gauge.Percent;
                attachInMwh = 0;
                modeDecided = false;
            }

            // Energy taken out since the last charge, by the pack's own count (its energy count is
            // right even though its idea of "full" is wrong); and the real power draw from it.
            if (!onAC && counter >= 0)
            {
                if (acChanged || runStartCounter < 0) { runStartMin = awakeMin; runStartCounter = counter; }
                if (lastCounter > counter) dischargedMwh += lastCounter - counter;
                else if (counter == 0 && !slept) dischargedMwh += drainMw * awakeSec / 3600;
                if (counter > 0 && runStartCounter - counter > 300 && awakeMin - runStartMin > 2)
                    drainMw = Math.Max(3000, Math.Min(60000, (runStartCounter - counter) / (awakeMin - runStartMin) * 60));
            }
            else runStartCounter = -1;

            // Charging now, or the counter rose without us seeing it (it charged while asleep or
            // off): the wait for the next full charge starts again.
            bool unseen = !charging && lastCounter >= 0 && counter - lastCounter > 150;
            if (charging || unseen)
            {
                lastChargeEndTicks = now.Ticks;
                chargeEndKnown = true;
                dischargedMwh = 0;
                if (unseen)
                {
                    Event(string.Format(CultureInfo.InvariantCulture, "It charged while asleep or off (battery's counter {0} -> {1} mWh).{2}",
                        lastCounter, counter, chargerQuirk ? " The wait for a full charge starts again." : ""));
                    SaveState();
                }
            }
            if (counter >= 0) lastCounter = counter;

            bool gapMet = gapH >= needGapHours, dischargeMet = dischargedMwh >= needDischargeMwh;
            readyForFull = chargerQuirk && !onAC && gapMet && dischargeMet;
            readyAtUtc = new DateTime(lastChargeEndTicks, DateTimeKind.Utc).AddHours(needGapHours);
            batteryMinLeft = Math.Max(0, (needDischargeMwh - dischargedMwh) / drainMw * 60);

            if (!onAC)
            {
                expectFull = limited = modeDecided = false;
                stopPct = -1;
                notChargingMin = 0;
            }
            else if (charging)
            {
                if (reading.RateMw > 0) attachInMwh += reading.RateMw * awakeSec / 3600;
                // What the charger will put in before it stops, unless it's a full charge.
                double budget = (attachCounter >= 0 ? Math.Max(0, fullMwh - attachCounter) : fullMwh) + (extraUsed ? 0 : limitExtraMwh);
                if (!modeDecided)
                {
                    modeDecided = true;
                    if (attachPct < 0) attachPct = gauge.Percent;
                    bool gapOk = attachGapH >= needGapHours, usedOk = attachDischargedMwh >= needDischargeMwh;
                    double need = (100 - attachPct) / 100 * gauge.CapacityMwh / 0.95;
                    if (!chargerQuirk || fullMwh <= 0) { } // nothing known about this charger yet
                    else if (gapOk && usedOk)
                    {
                        expectFull = true;
                        pendingCycle = false;
                        Event(string.Format(CultureInfo.InvariantCulture,
                            "Charging {0:0.0} h after the last charge, with {1:0.0} Wh used since: this one should go all the way to full.", attachGapH, attachDischargedMwh / 1000));
                    }
                    else
                    {
                        limited = need > budget + 500;
                        stopPct = limited ? Math.Min(99, attachPct + budget * 0.95 / gauge.CapacityMwh * 100) : -1;
                        string why = !gapOk
                            ? string.Format(CultureInfo.InvariantCulture, "only {0:0.0} h since the last charge; a full charge needs about {1:0.#} h", attachGapH, needGapHours)
                            : string.Format(CultureInfo.InvariantCulture, "only {0:0.0} Wh used since the last charge; a full charge needs about {1:0.0} Wh", attachDischargedMwh / 1000, needDischargeMwh / 1000);
                        Event(limited
                            ? string.Format(CultureInfo.InvariantCulture, "This charge will probably stop early, near {0:0}% ({1}).", stopPct, why)
                            : "Top-up charge: should reach full.");
                    }
                    SaveState();
                }
                else if (chargerQuirk && fullMwh > 0 && !expectFull && attachInMwh > budget + 2000)
                {
                    // It has taken clearly more than a limited charge ever does.
                    expectFull = true;
                    limited = false;
                    pendingCycle = false;
                    stopPct = -1;
                    Event("Still charging well past where a limited charge stops: this one is going to full.");
                    LearnRule(true);
                    SaveState();
                }
            }
            else if (lastChargeState == "Charging")
            {
                if (state == "Full")
                {
                    extraUsed = false;
                    pendingCycle = false;
                    if (expectFull) Event("Charged all the way to full.");
                    if (attachInMwh > 12000) LearnRule(true);
                }
                else
                {
                    if (expectFull) Event(string.Format(CultureInfo.InvariantCulture, "Stopped at about {0:0}% although this charge was expected to reach full.", gauge.Percent));
                    else if (attachCounter >= 0 && !extraUsed)
                    {
                        // Learn how far past a full counter the charger goes before it stops.
                        double extra = attachInMwh - (fullMwh - attachCounter);
                        if (extra > 500 && extra < 8000) limitExtraMwh += 0.3 * (extra - limitExtraMwh);
                    }
                    if (gauge.Percent < 90 && attachInMwh > 1000)
                    {
                        if (!chargerQuirk && reading.WindowsPercent >= 95) NoteChargerQuirk();
                        LearnRule(false);
                    }
                    extraUsed = true;
                }
                expectFull = limited = false;
                stopPct = -1;
                SaveState();
            }

            // Plugged in, stopped short and staying stopped: from here the user gets told when the
            // next full charge is possible.
            bool stuck = onAC && state == "NotCharging" && gauge.Percent >= 0 && gauge.Percent < 90;
            notChargingMin = stuck ? notChargingMin + awakeSec / 60 : 0;
            // Only a battery that calls itself full counts: a charge limit set in the BIOS or by
            // the maker's app is something else, and shows as less than full in Windows too.
            if (stuck && !chargerQuirk && notChargingMin >= 2 && reading.WindowsPercent >= 95) NoteChargerQuirk();
            if (stuck && chargerQuirk && !pendingCycle && notChargingMin >= 2)
            {
                pendingCycle = true;
                lastCycle = ""; // so the first step is announced
                Event(string.Format(CultureInfo.InvariantCulture, "Plugged in but not charging, at about {0:0}% ({1:0.000} V/cell). The next full charge is possible after {2:ddd HH:mm}.",
                    gauge.Percent, rawMv / 1000.0 / Math.Max(1, cells), readyAtUtc.ToLocalTime()));
            }

            // Where things stand: wait (too soon after the last charge) -> unplug (time's up, but it
            // has to be used on battery a little first) -> drain -> ready.
            if (!chargerQuirk || charging || state == "Full") cycle = "";
            else if (!gapMet) cycle = "wait";
            else if (onAC) cycle = gauge.Percent < 90 ? "unplug" : "";
            else cycle = dischargeMet ? "ready" : "drain";
            cycleEtaMin = batteryMinLeft;
            if (cycle != lastCycle)
            {
                // Popups only while a charge that stopped short is waiting to be finished.
                if (pendingCycle && ((cycle == "wait" && onAC) || cycle == "unplug" || cycle == "ready")) cycleSeq++;
                if (cycle == "ready") Event(string.Format(CultureInfo.InvariantCulture, "{0:0.0} h since the last charge and {1:0.0} Wh used: a plug-in now should charge to full.", gapH, dischargedMwh / 1000));
                lastCycle = cycle;
                SaveState();
            }
        }

        // Every charge that needed more than a top-up teaches the two thresholds: at least what
        // has worked, and more than what hasn't.
        void StartLearning(string mode)
        {
            gauge.StartLearning();
            learnMode = mode;
            learnState = gauge.ChargeState == "Full" ? "drain" : "charge";
            learnRunMin = -1;
            learnWarned = false;
            absoluteReads = 0;
            learnSeq++;
            TryDelete(LearnRunPath);
            Event(mode == "cutoff"
                ? "Learning this battery's true limit: charge to 100%, then use it on battery until it switches itself off (Battery Guard won't hibernate it), then charge to 100% again."
                : "Learning this battery: charge to 100%, use it on battery as usual until it hibernates at the safety limit, then charge to 100% again. Until then it only hibernates at the safety voltage.");
            SaveState();
            SaveGauge();
        }

        void StopLearning()
        {
            if (learnState == "") return;
            learnState = learnMode = "";
            gauge.LearningRun = false;
            learnRunMin = -1;
            learnSeq++;
            Event("Learning stopped. Protection is back to normal.");
            SaveState();
            SaveGauge();
        }

        void TrackLearning(bool onAC, double awakeSec)
        {
            if (learnState == "") return;
            string state = gauge.ChargeState;
            if (learnState == "charge" && ((onAC && state == "Full") || (!onAC && learnRunMin < 0 && gauge.Percent >= 97)))
            {
                learnState = "drain";
                learnSeq++;
                Event(learnMode == "cutoff"
                    ? "Learning: fully charged. Now use the laptop on battery until it switches itself off."
                    : "Learning: fully charged. Now use the laptop on battery as usual until it hibernates at the safety limit.");
                SaveState();
            }
            if (learnState == "drain")
            {
                if (onAC)
                {
                    if (learnRunMin >= 0) Event(string.Format(CultureInfo.InvariantCulture, "Learning run paused after {0:0} min: plugged in. It starts again the next time it's unplugged.", learnRunMin));
                    learnRunMin = -1;
                }
                else
                {
                    // Every minute of the run, to disk: if it ends in a power cut, it's all still there.
                    if (learnRunMin < 0)
                    {
                        learnRunMin = 0;
                        learnPointAccum = 999;
                        learnWarned = false;
                        TryDelete(LearnRunPath);
                    }
                    learnRunMin += awakeSec / 60;
                    learnPointAccum += awakeSec;
                    if (learnPointAccum >= 60 && perCell > 0 && volts.Count >= 4)
                    {
                        learnPointAccum = 0;
                        DurableFile.Append(LearnRunPath, string.Format(CultureInfo.InvariantCulture, "{0:0.00},{1:0}\r\n", learnRunMin, perCell), 5);
                    }
                }
            }
            if (learnState == "recharge" && onAC && lastChargeState == "Charging" && state != "Charging")
                FinishLearning(state == "Full");
        }

        // The run that went until the battery gave out: learn the curve from it, and move the
        // hibernate point down to just above where it gave out (leaving time for the countdown and
        // for hibernating), but never below 3.30 V per cell.
        void FinishTrueLimit(int cutMv, bool afterPowerCut)
        {
            var points = new List<double[]>();
            try
            {
                if (File.Exists(LearnRunPath))
                    foreach (string line in File.ReadAllLines(LearnRunPath))
                    {
                        string[] parts = line.Split(',');
                        double t, mv;
                        if (parts.Length == 2 && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out t) &&
                            double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out mv)) points.Add(new[] { mv, t });
                    }
            }
            catch (Exception ex) { Event("Couldn't read the learning run: " + ex.Message); }
            double end = Math.Max(learnRunMin, points.Count > 0 ? points[points.Count - 1][1] : 0);
            if (!gauge.LearnToCutoff(points, end, cutMv))
            {
                Event(string.Format(CultureInfo.InvariantCulture,
                    "The learning run ended {0} at {1:0.00} V per cell, but it was too short to learn from ({2} minutes recorded from {3:0.00} V). Charge to 100% and run it again.",
                    afterPowerCut ? "in a power cut" : "", cutMv / 1000.0, points.Count, points.Count > 0 ? points[0][0] / 1000 : 0));
                learnState = "charge";
                learnRunMin = -1;
                learnSeq++;
                SaveState();
                return;
            }
            NoteGaugeEvent();
            cutoffMv = cutMv;
            int newFloor = Math.Max(3300, Math.Min(3800, gauge.FloorBefore(cutMv, (cfg.CountdownSeconds / 60.0 + 1) * 1.5, 3300)));
            learnedFloorMv = 0;
            cfg.FloorPerCellMv = newFloor;
            cfg.FloorLearned = 1;
            try { cfg.Save(); }
            catch (Exception ex) { Event("Couldn't save the learned limit: " + ex.Message); }
            floorMv = newFloor;
            learned = string.Format(CultureInfo.InvariantCulture,
                "This battery gives out at about {0:0.00} V per cell, so Battery Guard hibernates at {1:0.00} V, a few minutes before that.", cutMv / 1000.0, newFloor / 1000.0);
            Event("Learned: " + learned);
            learnState = "recharge";
            learnRunMin = -1;
            learnSeq++;
            SaveState();
            SaveGauge();
        }

        void FinishLearning(bool reachedFull)
        {
            double full = gauge.FullRuntimeMinutes(floorMv);
            learnSummary = string.Format(CultureInfo.InvariantCulture, "A full charge lasts about {0}h {1:00}m of typical use: from {2:0.00} V per cell when full, down to {3:0.00} V where it hibernates{4}. {5}",
                (int)full / 60, (int)full % 60, gauge.FullCellMv / 1000, floorMv / 1000.0,
                cutoffMv > 0 ? string.Format(CultureInfo.InvariantCulture, " (it gives out at {0:0.00} V)", cutoffMv / 1000.0) : "",
                gauge.CapacityLearned ? string.Format(CultureInfo.InvariantCulture, "Capacity {0:0.0} Wh.", gauge.CapacityMwh / 1000)
                    : reachedFull ? "" : "Its capacity is learned at the next charge that goes all the way to full.");
            learnState = learnMode = "";
            learnSeq++;
            Event("Learning complete. " + learnSummary);
            SaveState();
            SaveGauge();
        }

        void NoteChargerQuirk()
        {
            chargerQuirk = true;
            Event(string.Format(CultureInfo.InvariantCulture,
                "The charger stopped at about {0:0}% because the battery's own gauge says it's full (Windows shows {1}%). From now on Battery Guard predicts each charge and says when a full one is possible.",
                gauge.Percent, reading.WindowsPercent));
            SaveState();
        }

        void LearnRule(bool reachedFull)
        {
            if (attachGapH < 0) return;
            if (!attachGapKnown) { attachGapH = -1; return; } // the previous charge wasn't seen, so the wait isn't known
            bool gapOk = attachGapH >= needGapHours, usedOk = attachDischargedMwh >= needDischargeMwh;
            if (reachedFull)
            {
                bestFullGapH = Math.Min(bestFullGapH, attachGapH);
                bestFullDischargeMwh = Math.Min(bestFullDischargeMwh, Math.Max(300, attachDischargedMwh));
            }
            else if (!gapOk && usedOk) worstFailGapH = Math.Max(worstFailGapH, attachGapH);
            else if (gapOk && !usedOk) worstFailDischargeMwh = Math.Max(worstFailDischargeMwh, attachDischargedMwh);
            else if (gapOk && usedOk) worstFailGapH = Math.Max(worstFailGapH, attachGapH); // the rule didn't hold: ask for longer
            needGapHours = worstFailGapH < bestFullGapH ? bestFullGapH : worstFailGapH + 2;
            needDischargeMwh = worstFailDischargeMwh < bestFullDischargeMwh ? bestFullDischargeMwh : worstFailDischargeMwh + 1000;
            Event(string.Format(CultureInfo.InvariantCulture,
                "Learned: a charge {0:0.0} h after the previous one, with {1:0.0} Wh used in between, {2}.{3}",
                attachGapH, attachDischargedMwh / 1000, reachedFull ? "went to full" : "stopped early",
                chargerQuirk ? string.Format(CultureInfo.InvariantCulture, " A full charge now needs {0:0.#} h and {1:0.0} Wh.", needGapHours, needDischargeMwh / 1000) : ""));
            attachGapH = -1; // counted once
        }
        void TrackSessions(bool onAC, double awakeSec)
        {
            long now = DateTime.UtcNow.Ticks;
            if (onAC && pluggedSinceTicks == 0) pluggedSinceTicks = now;
            if (!onAC && onBatterySinceTicks == 0) onBatterySinceTicks = now;
            string state = gauge.ChargeState;
            if (state == "Charging")
            {
                if (lastChargeState != "Charging")
                {
                    chargingSinceTicks = now;
                    sessionStartPct = gauge.Percent;
                    sessionInMwh = 0;
                }
                if (reading.RateMw > 0) sessionInMwh += reading.RateMw * awakeSec / 3600;
            }
            else if (lastChargeState == "Charging" && chargingSinceTicks > 0)
            {
                // A charging session ended: keep a one-line record of it.
                DateTime start = new DateTime(chargingSinceTicks, DateTimeKind.Utc).ToLocalTime(), end = DateTime.Now;
                TimeSpan took = end - start;
                lastCharge = string.Format(CultureInfo.InvariantCulture, "{0:ddd HH:mm}-{1:HH:mm} ({2}): {3:0}% to {4:0}%, {5:0.0} Wh in, {6}",
                    start, end, took.TotalHours >= 1 ? string.Format("{0}h {1:00}m", (int)took.TotalHours, took.Minutes) : Math.Max(1, (int)took.TotalMinutes) + " min",
                    Math.Max(0, sessionStartPct), gauge.Percent, sessionInMwh / 1000,
                    state == "Full" ? "full" : state == "NotCharging" ? "stopped early" : "unplugged");
                Event("Charge: " + lastCharge);
                chargingSinceTicks = 0;
                SaveState();
            }
            if (state == "Full" && lastChargeState != "Full")
            {
                lastFullTicks = now;
                SaveState();
            }
            lastChargeState = state;
        }

        bool PostponeAvailable
        {
            get { return phase == "Countdown" && !emergency && !postponeUsed && cfg.AllowPostpone == 1; }
        }

        void EnsurePowerProfile(bool armed)
        {
            KeyValuePair<Guid, uint>[] wanted;
            if (armed)
            {
                // Battery Guard decides when to hibernate: Windows must ignore its wrong gauge.
                uint notify = cfg.HideWindowsWarnings == 1 ? 0u : 1u;
                wanted = new[]
                {
                    new KeyValuePair<Guid, uint>(PowerPlan.CritAction, 0),
                    new KeyValuePair<Guid, uint>(PowerPlan.LowAction, 0),
                    new KeyValuePair<Guid, uint>(PowerPlan.LowNotify, notify),
                    new KeyValuePair<Guid, uint>(PowerPlan.CritNotify, notify),
                };
            }
            else
            {
                wanted = new[]
                {
                    new KeyValuePair<Guid, uint>(PowerPlan.CritAction, 2), // hibernate
                    new KeyValuePair<Guid, uint>(PowerPlan.CritLevel, 5),
                    new KeyValuePair<Guid, uint>(PowerPlan.LowNotify, 1),
                    new KeyValuePair<Guid, uint>(PowerPlan.CritNotify, 1),
                };
            }
            if (cfg.DryRun == 1)
            {
                if (profileArmed != armed) Event("[test mode] would set Windows' critical battery action to " + (armed ? "do nothing." : "hibernate."));
                profileArmed = armed;
                return;
            }
            string error;
            bool changed = PowerPlan.Ensure(wanted, out error);
            if (changed) Event("Windows' critical battery action set to " + (armed ? "do nothing (Battery Guard is in charge)." : "hibernate at 5%."));
            if (error != null && error != lastProfileError) Event(error);
            lastProfileError = error;
            profileArmed = armed;
        }

        void ReloadSettings()
        {
            try
            {
                DateTime stamp = File.GetLastWriteTimeUtc(Paths.Settings);
                if (stamp == settingsStamp) return;
                Settings s = Settings.Load();
                if (s == null) return;
                settingsStamp = stamp;
                if (s.Cells > 0 && s.Cells != cfg.Cells) cells = s.Cells;
                if (s.SimulateBattery != cfg.SimulateBattery) simStartMin = -1;
                cfg = s;
                Event(string.Format(CultureInfo.InvariantCulture, "Settings changed: protection {0}, floor {1} mV/cell, warn {2} min, countdown {3} s, postpone {4}, hide Windows warnings {5}.",
                    cfg.Enabled == 1 ? "on" : "off", cfg.FloorPerCellMv, cfg.WarnMinutes, cfg.CountdownSeconds, cfg.AllowPostpone, cfg.HideWindowsWarnings));
            }
            catch (Exception ex) { Event("Settings error: " + ex.Message); }
        }

        // If the laptop lost power on battery last time, the battery died at about the last voltage
        // we saw: raise the hibernate floor above it so it can't happen again.
        void LearnFromSuddenShutdown()
        {
            if (!lastOnBattery || lastPerCellMv <= 0 || lastUtcTicks == 0) return;
            var last = new DateTime(lastUtcTicks, DateTimeKind.Utc);
            try
            {
                var query = new EventLogQuery("System", PathType.LogName,
                    "*[System[Provider[@Name='Microsoft-Windows-Kernel-Power'] and (EventID=41)]]") { ReverseDirection = true };
                using (var events = new EventLogReader(query))
                {
                    EventRecord e = events.ReadEvent();
                    if (e != null)
                    {
                        using (e)
                        {
                            bool after = e.TimeCreated.HasValue && e.TimeCreated.Value.ToUniversalTime() > last;
                            bool powerLoss = e.Properties.Count > 0 && Convert.ToInt64(e.Properties[0].Value) == 0; // no bugcheck
                            if (after && powerLoss)
                            {
                                if (lastPerCellMv >= 3750)
                                    Event(string.Format("Sudden power loss at {0} mV/cell - too high to be an empty battery, not learning from it.", lastPerCellMv));
                                else if (learnState == "drain" && learnMode == "cutoff" && learnRunMin >= 0)
                                {
                                    // Exactly what the learning run was waiting for.
                                    pendingCutoffMv = lastPerCellMv;
                                    Event(string.Format(CultureInfo.InvariantCulture, "The battery gave out at {0:0.000} V per cell ({1:HH:mm}), after {2:0} min of the learning run: that's its true limit.",
                                        lastPerCellMv / 1000.0, e.TimeCreated.Value, learnRunMin));
                                }
                                else
                                {
                                    int floor = Math.Min(3750, lastPerCellMv + 80);
                                    if (floor > Math.Max(learnedFloorMv, cfg.FloorPerCellMv))
                                    {
                                        learnedFloorMv = floor;
                                        learned = string.Format(CultureInfo.InvariantCulture, "The battery cut out at {0:0.00} V/cell on {1:d MMM}, so hibernation now happens at {2:0.00} V/cell.",
                                            lastPerCellMv / 1000.0, e.TimeCreated.Value, floor / 1000.0);
                                        Event("Learned: " + learned);
                                    }
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception ex) { Event("Could not check for sudden shutdowns: " + ex.Message); }
            lastOnBattery = false;
            SaveState();
        }

        void ResetWindow()
        {
            volts.Clear();
            criticalReads = 0;
            lowReads = 0;
        }

        static int Median(List<int> values)
        {
            var sorted = new List<int>(values);
            sorted.Sort();
            return sorted[sorted.Count / 2];
        }

        // Written when something changed, or every 30 s as a sign of life for the tray apps.
        void WriteStatus()
        {
            var s = new Status
            {
                Protection = protection,
                OnAC = reading.OnAC,
                Charging = reading.Charging,
                DryRun = cfg.DryRun == 1,
                VoltageMv = packMv,
                PerCellMv = (int)perCell,
                FloorPerCellMv = floorMv,
                WindowsPercent = reading.WindowsPercent,
                Percent = gauge.Percent,
                SigmaPct = gauge.SigmaPct,
                MinutesLeft = gauge.MinutesLeft,
                RateMw = reading.RateMw == int.MinValue ? 0 : reading.RateMw,
                ChargeState = gauge.ChargeState,
                StopsEarly = chargerQuirk,
                ExpectFull = expectFull,
                Limited = limited,
                StopPct = stopPct,
                ReadyForFull = readyForFull,
                ReadyAtUtc = readyAtUtc,
                NeedGapHours = needGapHours,
                Cycle = cycle,
                CycleSeq = cycleSeq,
                CycleEtaMin = cycleEtaMin,
                OnBatterySinceUtc = new DateTime(onBatterySinceTicks, DateTimeKind.Utc),
                PluggedSinceUtc = new DateTime(pluggedSinceTicks, DateTimeKind.Utc),
                ChargingSinceUtc = new DateTime(chargingSinceTicks, DateTimeKind.Utc),
                LastFullUtc = new DateTime(lastFullTicks, DateTimeKind.Utc),
                ChargeInWh = chargingSinceTicks > 0 ? sessionInMwh / 1000 : 0,
                LastCharge = lastCharge,
                FullRuntimeMin = gauge.FullRuntimeMinutes(floorMv),
                MinutesToFull = gauge.MinutesToFull,
                Phase = phase,
                WarningSeq = warningSeq,
                CountdownSeq = countdownSeq,
                CountdownEndUtc = countdownEndUtc,
                Emergency = emergency,
                PostponeAvailable = PostponeAvailable,
                Message = message,
                Learned = learned,
                Learn = learnState,
                LearnMode = learnMode,
                LearnSeq = learnSeq,
                LearnSummary = learnSummary,
                LearnRunMin = learnRunMin,
                CutoffMv = cutoffMv,
                FullCellMv = (int)gauge.FullCellMv,
                Calibrated = gauge.Calibrated,
            };
            string body = s.Body();
            DateTime now = DateTime.UtcNow;
            if (body == lastStatusBody && Math.Abs((now - lastStatusUtc).TotalSeconds) < 30) return;
            s.UpdatedUtc = now;
            try
            {
                s.Save(body);
                lastStatusBody = body;
                lastStatusUtc = now;
            }
            catch (Exception ex) { Event("Status write error: " + ex.Message); }
        }
        const string HistoryHeader = "time_utc,on_ac,charging,charge_state,pack_mv,rate_mw,cell_mv,battery_gauge_mwh,windows_pct,pct,curve_pct,minutes_left,speed,phase,event";
        bool historyChecked;

        // One line per reading (every 10-15 s while on battery or charging, every minute otherwise,
        // and immediately at every event). Lines are written straight through to disk - at once
        // for events, otherwise at least once a minute. If the file is locked (e.g. open in
        // Excel), lines go to battery-log.pending.csv and are moved in, in order, once it's free.
        // Full files are archived (compressed), never deleted.
        void WriteHistory() { WriteHistory(true); }

        void WriteHistory(bool now)
        {
            string line = string.Format(CultureInfo.InvariantCulture,
                "{0:yyyy-MM-ddTHH:mm:ssZ},{1},{2},{3},{4},{5},{6:0},{7},{8},{9:0.0},{10:0.0},{11:0.0},{12:0.00},{13},{14}\r\n",
                DateTime.UtcNow, reading.OnAC ? 1 : 0, reading.Charging ? 1 : 0, gauge.ChargeState, rawMv,
                reading.RateMw == int.MinValue ? "" : reading.RateMw.ToString(CultureInfo.InvariantCulture),
                perCell, reading.RemainingMwh, reading.WindowsPercent, gauge.Percent, gauge.CurvePercent,
                gauge.MinutesLeft, gauge.Speed, phase, Ini.OneLine(lastEvent).Replace(',', ';'));
            lastEvent = "";
            if (historyBuffer.Length == 0) historyBufferSince = DateTime.UtcNow;
            historyBuffer.Append(line);
            if (now || historyBuffer.Length > 32768 || Math.Abs((DateTime.UtcNow - historyBufferSince).TotalSeconds) >= 60) FlushHistory();
        }

        void FlushHistory()
        {
            if (historyBuffer.Length == 0) return;
            string line = historyBuffer.ToString();
            historyBuffer.Length = 0;
            string pending = Path.Combine(Paths.Data, "battery-log.pending.csv");
            try
            {
                if (!historyChecked && File.Exists(Paths.History))
                {
                    // A file in an older layout is archived rather than mixed with the new one.
                    string first;
                    using (var r = new StreamReader(new FileStream(Paths.History, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))) first = r.ReadLine();
                    if (first != HistoryHeader) Archive.Store(Paths.History);
                }
                historyChecked = true;
                Archive.Rotate(Paths.History, 5 * 1024 * 1024);
                string head = File.Exists(Paths.History) ? "" : HistoryHeader + "\r\n";
                string waiting = File.Exists(pending) ? File.ReadAllText(pending) : "";
                if (DurableFile.Append(Paths.History, head + waiting + line, 3))
                {
                    if (waiting.Length > 0) TryDelete(pending);
                    return;
                }
            }
            catch { }
            DurableFile.Append(pending, line, 10);
        }
        void LoadState()
        {
            var kv = Ini.Read(Paths.State) ?? new Dictionary<string, string>();
            lastOnBattery = Ini.I(kv, "LastOnBattery", 0) == 1;
            lastPerCellMv = Ini.I(kv, "LastPerCellMv", 0);
            lastUtcTicks = Ini.L(kv, "LastUtcTicks", 0);
            learnedFloorMv = Ini.I(kv, "LearnedFloorMv", 0);
            learned = Ini.S(kv, "Learned", "");
            savedCells = Ini.I(kv, "Cells", 0);
            lastWindowsEventTicks = Ini.L(kv, "LastWindowsEvent", 0);
            onBatterySinceTicks = Ini.L(kv, "OnBatterySince", 0);
            pluggedSinceTicks = Ini.L(kv, "PluggedSince", 0);
            chargingSinceTicks = Ini.L(kv, "ChargingSince", 0);
            lastFullTicks = Ini.L(kv, "LastFull", 0);
            sessionStartPct = Ini.D(kv, "SessionStartPct", -1);
            sessionInMwh = Ini.D(kv, "SessionInMwh", 0);
            lastCharge = Ini.S(kv, "LastCharge", "");
            if (chargingSinceTicks > 0) lastChargeState = "Charging"; // carry on the session after a restart
            counterEmpty = Ini.I(kv, "CounterEmpty", 0) == 1;
            expectFull = Ini.I(kv, "ExpectFull", 0) == 1;
            limited = Ini.I(kv, "Limited", 0) == 1;
            stopPct = Ini.D(kv, "StopPct", -1);
            modeDecided = Ini.I(kv, "ModeDecided", 0) == 1;
            attachedAwake = Ini.I(kv, "AttachedAwake", 0) == 1;
            attachCounter = Ini.I(kv, "AttachCounter", -1);
            attachPct = Ini.D(kv, "AttachPct", -1);
            attachInMwh = Ini.D(kv, "AttachInMwh", 0);
            extraUsed = Ini.I(kv, "ExtraUsed", 0) == 1;
            limitExtraMwh = Math.Max(500, Math.Min(8000, Ini.D(kv, "LimitExtraMwh", 3000)));
            drainMw = Math.Max(3000, Math.Min(60000, Ini.D(kv, "DrainMw", 9000)));
            lastChargeEndTicks = Ini.L(kv, "LastChargeEnd", 0);
            chargeEndKnown = Ini.I(kv, "ChargeEndKnown", lastChargeEndTicks > 0 ? 1 : 0) == 1;
            // Older versions only ran on a PC whose charger was known to stop early.
            chargerQuirk = Ini.I(kv, "ChargerStopsEarly", kv.ContainsKey("BestFullGapH") ? 1 : 0) == 1;
            dischargedMwh = Ini.D(kv, "DischargedMwh", 0);
            attachGapH = Ini.D(kv, "AttachGapH", -1);
            attachGapKnown = Ini.I(kv, "AttachGapKnown", attachGapH >= 0 ? 1 : 0) == 1;
            attachDischargedMwh = Ini.D(kv, "AttachDischargedMwh", 0);
            bestFullGapH = Ini.D(kv, "BestFullGapH", 9.5);
            worstFailGapH = Ini.D(kv, "WorstFailGapH", 4.7);
            bestFullDischargeMwh = Ini.D(kv, "BestFullDischargeMwh", 2600);
            worstFailDischargeMwh = Ini.D(kv, "WorstFailDischargeMwh", 550);
            needGapHours = worstFailGapH < bestFullGapH ? bestFullGapH : worstFailGapH + 2;
            needDischargeMwh = worstFailDischargeMwh < bestFullDischargeMwh ? bestFullDischargeMwh : worstFailDischargeMwh + 1000;
            pendingCycle = Ini.I(kv, "PendingCycle", 0) == 1;
            captured = Ini.I(kv, "Captured", 0) == 1;
            lastCounter = Ini.I(kv, "LastCounter", -1);
            lastCycle = Ini.S(kv, "Cycle", "");
            cycle = Ini.S(kv, "Cycle", "");
            cycleSeq = Ini.I(kv, "CycleSeq", 0);
            learnState = Ini.S(kv, "Learn", "");
            learnMode = Ini.S(kv, "LearnMode", "");
            learnSeq = Ini.I(kv, "LearnSeq", 0);
            learnSummary = Ini.S(kv, "LearnSummary", "");
            learnRunMin = Ini.D(kv, "LearnRunMin", -1);
            learnWarned = Ini.I(kv, "LearnWarned", 0) == 1;
            cutoffMv = Ini.I(kv, "CutoffMv", 0);
            lastBatteryCounter = Ini.I(kv, "LastBatteryCounter", -1);
        }

        void SaveState()
        {
            var sb = new StringBuilder();
            Action<string, object> add = delegate(string k, object v)
            {
                sb.Append(k).Append('=').Append(Convert.ToString(v, CultureInfo.InvariantCulture)).Append("\r\n");
            };
            add("LastOnBattery", lastOnBattery ? 1 : 0);
            add("LastPerCellMv", lastPerCellMv);
            add("LastUtcTicks", lastUtcTicks);
            add("LearnedFloorMv", learnedFloorMv);
            add("Learned", Ini.OneLine(learned));
            add("Cells", savedCells);
            add("LastWindowsEvent", lastWindowsEventTicks);
            add("OnBatterySince", onBatterySinceTicks);
            add("PluggedSince", pluggedSinceTicks);
            add("ChargingSince", chargingSinceTicks);
            add("LastFull", lastFullTicks);
            add("SessionStartPct", Ini.F(sessionStartPct));
            add("SessionInMwh", Ini.F(sessionInMwh));
            add("LastCharge", Ini.OneLine(lastCharge));
            add("CounterEmpty", counterEmpty ? 1 : 0);
            add("ExpectFull", expectFull ? 1 : 0);
            add("Limited", limited ? 1 : 0);
            add("StopPct", Ini.F(stopPct));
            add("ModeDecided", modeDecided ? 1 : 0);
            add("AttachedAwake", attachedAwake ? 1 : 0);
            add("AttachCounter", attachCounter);
            add("AttachPct", Ini.F(attachPct));
            add("AttachInMwh", Ini.F(attachInMwh));
            add("ExtraUsed", extraUsed ? 1 : 0);
            add("LimitExtraMwh", Ini.F(limitExtraMwh));
            add("DrainMw", Ini.F(drainMw));
            add("LastChargeEnd", lastChargeEndTicks);
            add("ChargeEndKnown", chargeEndKnown ? 1 : 0);
            add("ChargerStopsEarly", chargerQuirk ? 1 : 0);
            add("DischargedMwh", Ini.F(dischargedMwh));
            add("AttachGapH", Ini.F(attachGapH));
            add("AttachGapKnown", attachGapKnown ? 1 : 0);
            add("AttachDischargedMwh", Ini.F(attachDischargedMwh));
            add("BestFullGapH", Ini.F(bestFullGapH));
            add("WorstFailGapH", Ini.F(worstFailGapH));
            add("BestFullDischargeMwh", Ini.F(bestFullDischargeMwh));
            add("WorstFailDischargeMwh", Ini.F(worstFailDischargeMwh));
            add("PendingCycle", pendingCycle ? 1 : 0);
            add("Captured", captured ? 1 : 0);
            add("LastCounter", lastCounter);
            add("Cycle", cycle);
            add("Learn", learnState);
            add("LearnMode", learnMode);
            add("LearnSeq", learnSeq);
            add("LearnSummary", Ini.OneLine(learnSummary));
            add("LearnRunMin", Ini.F(learnRunMin));
            add("LearnWarned", learnWarned ? 1 : 0);
            add("CutoffMv", cutoffMv);
            add("CycleSeq", cycleSeq);
            add("LastBatteryCounter", lastBatteryCounter);
            try { Ini.Write(Paths.State, sb.ToString()); }
            catch (Exception ex) { Event("State write error: " + ex.Message); }
        }
        static void TryDelete(string path)
        {
            try { File.Delete(path); }
            catch { }
        }
    }

    // The Windows service around Core: Windows starts it at boot, restarts it if it ever fails,
    // and tells it when it's stopping or shutting down so nothing is lost.
    class CoreService : ServiceBase
    {
        public const string Name = "BatteryGuard";
        Thread worker;
        volatile Core core;
        volatile bool stopping;

        public CoreService()
        {
            ServiceName = Name;
            CanStop = true;
            CanShutdown = true;
            CanHandleSessionChangeEvent = true;
            AutoLog = false;
        }

        protected override void OnStart(string[] args)
        {
            worker = new Thread(Work) { Name = "core", IsBackground = true };
            worker.Start();
        }

        void Work()
        {
            using (var mutex = new Mutex(false, "Global\\BatteryGuardCore"))
            {
                try { mutex.WaitOne(); } // an older copy may still be finishing
                catch (AbandonedMutexException) { }
                try
                {
                    core = new Core();
                    core.Run();
                }
                catch (Exception ex) { Log.Write("Core crashed: " + ex); }
                finally { mutex.ReleaseMutex(); }
            }
            if (!stopping) Environment.Exit(1); // ended by itself: Windows starts it again
        }

        protected override void OnStop() { Halt("Service stopped (update, uninstall or by an administrator)."); }

        protected override void OnShutdown() { Halt("Windows is shutting down."); }

        void Halt(string reason)
        {
            stopping = true;
            Core c = core;
            if (c != null) c.Stop(reason);
            if (worker != null) worker.Join(15000);
        }

        protected override void OnSessionChange(SessionChangeDescription change)
        {
            Core c = core;
            if (c != null && (change.Reason == SessionChangeReason.SessionLogon || change.Reason == SessionChangeReason.SessionUnlock ||
                change.Reason == SessionChangeReason.ConsoleConnect || change.Reason == SessionChangeReason.RemoteConnect))
                c.SessionChanged();
        }
    }

    static class CoreProgram
    {
        static void Main(string[] args)
        {
            Paths.Init(args);
            if (Array.IndexOf(args, "--service") >= 0 && !Paths.Custom)
            {
                ServiceBase.Run(new CoreService());
                return;
            }
            bool created;
            using (var mutex = new Mutex(true, "Global\\BatteryGuardCore" + Paths.Suffix, out created))
            {
                if (!created) return;
                try { new Core().Run(); }
                catch (Exception ex)
                {
                    Log.Write("Core crashed: " + ex);
                    Environment.Exit(1);
                }
                GC.KeepAlive(mutex);
            }
        }
    }
}
