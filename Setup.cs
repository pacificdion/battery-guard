// Battery Guard - Copyright (C) 2026 Dion Fabian. Free software under the GNU General Public License v3.0; see LICENSE.
// Battery Guard Setup: installs, updates or removes Battery Guard for every user on this PC.
// A single file with both programs inside; asks for administrator rights once (UAC).
//
//   BatteryGuard-Setup.exe                 install or update (window)
//   BatteryGuard-Setup.exe /S              install or update, no window
//   uninstall.exe --uninstall [--quiet] [--remove-data]
//
// What it sets up:
//   C:\Program Files\BatteryGuard        the programs (only admins can change them)
//   C:\ProgramData\BatteryGuard          settings, status, logs and history; only the service
//                                        writes there, users can read it
//   C:\ProgramData\BatteryGuard\inbox    where the tray apps leave requests for the service
//   Task "BatteryGuard Core"             runs the service as SYSTEM at start-up, re-checked every 5 min
//   Start menu "Battery Guard", and an entry in Apps & features
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Security;
using System.Security.AccessControl;
using System.ServiceProcess;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

[assembly: AssemblyTitle("Battery Guard Setup")]
[assembly: AssemblyDescription("Installs Battery Guard for every user on this PC")]

namespace BatteryGuard
{
    static class SetupProgram
    {
        [STAThread]
        static int Main(string[] args)
        {
            bool quiet = Has(args, "/s", "/quiet", "--quiet", "/silent", "-s");
            bool uninstall = Has(args, "--uninstall", "/uninstall", "/u");
            bool removeData = Has(args, "--remove-data");

            // The uninstaller can't delete itself while it runs: carry on from a temporary copy.
            string self = Assembly.GetExecutingAssembly().Location;
            if (uninstall && !Has(args, "--from-temp") &&
                string.Equals(Path.GetDirectoryName(self), Installer.Bin, StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    string temp = Path.Combine(Path.GetTempPath(), "BatteryGuard-uninstall-" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".exe");
                    File.Copy(self, temp);
                    Installer.DeleteAtRestart(temp);
                    Process.Start(temp, string.Join(" ", args) + " --from-temp");
                    return 0;
                }
                catch { } // fall through: run from here and leave uninstall.exe for the restart
            }

            if (quiet)
            {
                string error = uninstall ? Installer.Uninstall(removeData, null) : Installer.Install(null);
                return error == null ? 0 : 1;
            }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            string page = Arg(args, "--page");
            Application.Run(new SetupForm(uninstall, page));
            return 0;
        }

        static bool Has(string[] args, params string[] names)
        {
            foreach (string a in args)
                foreach (string n in names)
                    if (string.Equals(a, n, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        static string Arg(string[] args, string name)
        {
            int i = Array.FindIndex(args, a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));
            return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
        }
    }

    // The actual work. Each step reports progress (0-100) and a short line for the window.
    static class Installer
    {
        public const string ServiceName = "BatteryGuard";
        const string OldTask = "BatteryGuard Core"; // how versions before 2.0 ran
        const string UninstallKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\BatteryGuard";
        public static readonly string Bin = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "BatteryGuard");
        public static readonly string Data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "BatteryGuard");
        static readonly string Shortcut = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), "Battery Guard.lnk");
        const string GcConfig = "<?xml version=\"1.0\" encoding=\"utf-8\"?><configuration><runtime><gcConcurrent enabled=\"false\"/></runtime></configuration>";

        public delegate void Progress(int percent, string step);

        public static string InstalledVersion()
        {
            try
            {
                using (RegistryKey k = Registry.LocalMachine.OpenSubKey(UninstallKey))
                {
                    object v = k == null ? null : k.GetValue("DisplayVersion");
                    if (v != null) return v.ToString();
                }
            }
            catch { }
            return File.Exists(Path.Combine(Bin, "BatteryGuardCore.exe")) ? "1" : null;
        }

        public static bool HasBattery()
        {
            SYSTEM_POWER_STATUS s;
            return !GetSystemPowerStatus(out s) || (s.BatteryFlag != 128 && s.BatteryFlag != 255);
        }

        // On battery with Windows' own gauge at or below its hibernate level (plus a margin).
        public static bool OnBatteryAndLow(out int windowsPercent)
        {
            SYSTEM_POWER_STATUS s;
            windowsPercent = -1;
            if (!GetSystemPowerStatus(out s) || s.ACLineStatus != 0 || s.BatteryLifePercent == 255) return false;
            windowsPercent = s.BatteryLifePercent;
            return windowsPercent <= 10;
        }

        public static string Install(Progress progress)
        {
            if (progress == null) progress = delegate { };
            var log = new StringBuilder();
            string step = "";
            Action<int, string> at = delegate(int pct, string text) { step = text; progress(pct, text); log.Append("  ").Append(text).Append("\r\n"); };
            try
            {
                at(5, "Stopping the running version");
                StopRunning();

                at(20, "Copying the programs");
                Directory.CreateDirectory(Bin);
                PutFile(Path.Combine(Bin, "BatteryGuardCore.exe"), Payload("payload.core"));
                PutFile(Path.Combine(Bin, "BatteryGuard.exe"), Payload("payload.agent"));
                PutFile(Path.Combine(Bin, "BatteryGuardCore.exe.config"), Encoding.UTF8.GetBytes(GcConfig));
                PutFile(Path.Combine(Bin, "BatteryGuard.exe.config"), Encoding.UTF8.GetBytes(GcConfig));
                string self = Assembly.GetExecutingAssembly().Location, uninstaller = Path.Combine(Bin, "uninstall.exe");
                if (!string.Equals(self, uninstaller, StringComparison.OrdinalIgnoreCase)) PutFile(uninstaller, File.ReadAllBytes(self));

                at(35, "Preparing the data folder");
                SecureData();

                at(45, "Compiling for this PC, so it starts fast and uses less memory");
                string ngen = Path.Combine(RuntimeEnvironment.GetRuntimeDirectory(), "ngen.exe");
                if (File.Exists(ngen))
                    foreach (string exe in new[] { "BatteryGuardCore.exe", "BatteryGuard.exe" })
                        Run(ngen, "install \"" + Path.Combine(Bin, exe) + "\" /nologo /silent", 180000);

                bool supported;
                if (!PowerCaps.CanHibernate(out supported) && supported)
                {
                    at(75, "Turning on hibernation, so open work is saved before the battery runs out");
                    Run("powercfg.exe", "/hibernate on", 60000);
                }

                at(80, "Registering the background service");
                RegisterService();

                at(88, "Adding it to the Start menu and Apps & features");
                CreateShortcut();
                Register(uninstaller);

                at(94, "Starting Battery Guard");
                StartService(30000);

                at(100, "Done");
                Note("Installed version " + Product.Version + ".\r\n" + log);
                return null;
            }
            catch (Exception ex)
            {
                Note("Install failed at \"" + step + "\": " + ex + "\r\n" + log);
                return step + ": " + ex.Message;
            }
        }

        public static string Uninstall(bool removeData, Progress progress)
        {
            if (progress == null) progress = delegate { };
            string step = "";
            Action<int, string> at = delegate(int pct, string text) { step = text; progress(pct, text); };
            try
            {
                at(10, "Stopping Battery Guard");
                StopRunning();
                if (ServiceExists())
                {
                    string output;
                    if (Run("sc.exe", "delete " + ServiceName, 30000, out output) != 0 && ServiceExists())
                        throw new Exception("Couldn't remove the background service: " + output.Trim());
                }

                at(35, "Giving Windows back its own low-battery rules");
                RestorePowerSettings();

                at(55, "Removing the programs");
                string ngen = Path.Combine(RuntimeEnvironment.GetRuntimeDirectory(), "ngen.exe");
                if (File.Exists(ngen))
                    foreach (string exe in new[] { "BatteryGuardCore.exe", "BatteryGuard.exe" })
                        Run(ngen, "uninstall \"" + Path.Combine(Bin, exe) + "\" /nologo /silent", 120000);
                RemoveFolder(Bin);
                TryDelete(Shortcut);
                try { Registry.LocalMachine.DeleteSubKeyTree(UninstallKey, false); }
                catch { }
                try { Registry.CurrentUser.DeleteSubKeyTree(@"Software\BatteryGuard", false); }
                catch { }

                if (removeData)
                {
                    at(85, "Deleting history and logs");
                    RemoveFolder(Data);
                }
                else Note("Uninstalled. History and logs were kept.");
                at(100, "Done");
                return null;
            }
            catch (Exception ex)
            {
                if (Directory.Exists(Data)) Note("Uninstall failed at \"" + step + "\": " + ex);
                return step + ": " + ex.Message;
            }
        }

        // Ask the service to stop cleanly (it saves everything first), then end whatever's left.
        static void StopRunning()
        {
            try
            {
                using (var sc = new ServiceController(ServiceName))
                {
                    if (sc.Status != ServiceControllerStatus.Stopped)
                    {
                        if (sc.Status != ServiceControllerStatus.StopPending) sc.Stop();
                        sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(20));
                    }
                }
            }
            catch { } // not installed (yet), or already stopped

            // Versions before 2.0 ran from a scheduled task; it's replaced by the service.
            Run("schtasks.exe", "/Change /TN \"" + OldTask + "\" /DISABLE", 30000);
            try
            {
                using (EventWaitHandle stop = EventWaitHandle.OpenExisting("Global\\BatteryGuardCoreStop", EventWaitHandleRights.Modify | EventWaitHandleRights.Synchronize))
                    stop.Set();
                WaitExit("BatteryGuardCore", 10000);
            }
            catch { }
            Run("schtasks.exe", "/End /TN \"" + OldTask + "\"", 30000);
            Run("schtasks.exe", "/Delete /TN \"" + OldTask + "\" /F", 30000);
            Run("schtasks.exe", "/Delete /TN \"BatteryGuard\" /F", 30000); // a very early version

            Run("taskkill.exe", "/F /IM BatteryGuardCore.exe /IM BatteryGuard.exe", 30000);
            WaitExit("BatteryGuardCore", 5000);
            WaitExit("BatteryGuard", 5000);
        }

        static bool ServiceExists()
        {
            try
            {
                using (var sc = new ServiceController(ServiceName))
                {
                    ServiceControllerStatus unused = sc.Status;
                    return true;
                }
            }
            catch { return false; }
        }

        static void WaitExit(string name, int ms)
        {
            foreach (Process p in Process.GetProcessesByName(name))
                using (p)
                {
                    try { p.WaitForExit(ms); }
                    catch { }
                }
        }

        // Starts the service and waits until it has taken a first reading.
        static void StartService(int ms)
        {
            string status = Path.Combine(Data, "status.ini");
            DateTime before = File.Exists(status) ? File.GetLastWriteTimeUtc(status) : DateTime.MinValue;
            using (var sc = new ServiceController(ServiceName))
            {
                if (sc.Status != ServiceControllerStatus.Running && sc.Status != ServiceControllerStatus.StartPending) sc.Start();
                sc.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromMilliseconds(ms));
            }
            DateTime start = DateTime.UtcNow;
            while ((DateTime.UtcNow - start).TotalMilliseconds < ms)
            {
                if (File.Exists(status) && File.GetLastWriteTimeUtc(status) > before) return;
                Thread.Sleep(250);
            }
            throw new Exception("The background service started but hasn't read the battery yet. Restarting the PC usually fixes it.");
        }

        // Only the service and admins may change what's in the data folder (it runs as SYSTEM, so
        // anything users could plant there would be a way in). Users read it, and may leave
        // requests in inbox\.
        static void SecureData()
        {
            Directory.CreateDirectory(Data);
            string inbox = Path.Combine(Data, "inbox");
            Directory.CreateDirectory(inbox);
            Run("icacls.exe", "\"" + Data + "\" /inheritance:r /grant:r *S-1-5-18:(OI)(CI)F *S-1-5-32-544:(OI)(CI)F *S-1-5-32-545:(OI)(CI)RX /Q", 60000);
            Run("icacls.exe", "\"" + Path.Combine(Data, "*") + "\" /reset /T /C /Q", 120000);
            Run("icacls.exe", "\"" + Data + "\" /setowner *S-1-5-32-544 /T /C /Q", 120000);
            InboxAccess.Open(inbox);
            foreach (string f in Directory.GetFiles(Data, "cmd-*.txt")) TryDelete(f); // older versions' requests
        }

        // A Windows service: starts at boot (before anyone signs in), restarts within seconds if it
        // ever fails, runs whatever the battery level - unlike a scheduled task, which Windows holds
        // back while Battery Saver is on.
        static void RegisterService()
        {
            string command = "\"" + Path.Combine(Bin, "BatteryGuardCore.exe") + "\" --service";
            string output;
            string config = (ServiceExists() ? "config " : "create ") + ServiceName +
                " binPath= \"" + command.Replace("\"", "\\\"") + "\" start= auto DisplayName= \"" + Product.Name + "\" obj= LocalSystem";
            if (Run("sc.exe", config, 30000, out output) != 0)
                throw new Exception("Windows refused the background service: " + output.Trim());
            Run("sc.exe", "description " + ServiceName + " \"" + Product.Name + " by " + Product.Author +
                ": watches the battery voltage and hibernates safely before the battery runs out, for every user on this PC.\"", 30000);
            Run("sc.exe", "failure " + ServiceName + " reset= 86400 actions= restart/2000/restart/5000/restart/10000", 30000);
            Run("sc.exe", "failureflag " + ServiceName + " 1", 30000);
        }

        static void Register(string uninstaller)
        {
            long bytes = 0;
            foreach (string f in Directory.GetFiles(Bin)) bytes += new FileInfo(f).Length;
            using (RegistryKey k = Registry.LocalMachine.CreateSubKey(UninstallKey))
            {
                k.SetValue("DisplayName", Product.Name);
                k.SetValue("DisplayVersion", Product.Version);
                k.SetValue("Publisher", Product.Author);
                k.SetValue("DisplayIcon", Path.Combine(Bin, "BatteryGuard.exe") + ",0");
                k.SetValue("InstallLocation", Bin);
                k.SetValue("UninstallString", "\"" + uninstaller + "\" --uninstall");
                k.SetValue("QuietUninstallString", "\"" + uninstaller + "\" --uninstall --quiet");
                k.SetValue("InstallDate", DateTime.Now.ToString("yyyyMMdd", CultureInfo.InvariantCulture));
                k.SetValue("Comments", "Keeps laptops with a faulty battery gauge running until the battery is really empty, then hibernates safely.");
                k.SetValue("EstimatedSize", (int)Math.Max(1, bytes / 1024), RegistryValueKind.DWord);
                k.SetValue("NoModify", 1, RegistryValueKind.DWord);
                k.SetValue("NoRepair", 1, RegistryValueKind.DWord);
            }
        }

        static void CreateShortcut()
        {
            var link = (IShellLinkW)new ShellLink();
            try
            {
                string exe = Path.Combine(Bin, "BatteryGuard.exe");
                link.SetPath(exe);
                link.SetArguments("--show");
                link.SetWorkingDirectory(Bin);
                link.SetDescription("Real battery level, warnings, charging and history");
                link.SetIconLocation(exe, 0);
                ((IPersistFile)link).Save(Shortcut, true);
            }
            finally { Marshal.ReleaseComObject(link); }
        }

        // Windows' defaults on every power plan: hibernate at 5%, low-battery notices on.
        static void RestorePowerSettings()
        {
            Guid sub = new Guid("e73a048d-bf27-4f12-9731-8b2076e8891f");
            var values = new[]
            {
                new KeyValuePair<Guid, uint>(new Guid("637ea02f-bbcb-4015-8e2c-a1c7b9c0b546"), 2), // critical action: hibernate
                new KeyValuePair<Guid, uint>(new Guid("9a66d8d7-4ff7-4ef9-b5a2-5a326ca2a469"), 5), // critical level
                new KeyValuePair<Guid, uint>(new Guid("bcded951-187b-4d05-bccc-f7e51960c258"), 1), // low notification
                new KeyValuePair<Guid, uint>(new Guid("5dbb7c9f-38e9-40d2-9749-4f8a0e9f640f"), 1), // critical notification
                new KeyValuePair<Guid, uint>(new Guid("d8742dcb-3e6a-4b3c-b3fe-374623cdcf06"), 0), // low action: do nothing
            };
            for (uint i = 0; ; i++)
            {
                byte[] buffer = new byte[16];
                uint size = 16;
                if (PowerEnumerate(IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 16 /* ACCESS_SCHEME */, i, buffer, ref size) != 0) break;
                Guid scheme = new Guid(buffer);
                foreach (var v in values)
                {
                    Guid setting = v.Key;
                    PowerWriteDCValueIndex(IntPtr.Zero, ref scheme, ref sub, ref setting, v.Value);
                }
            }
            IntPtr active;
            if (PowerGetActiveScheme(IntPtr.Zero, out active) == 0)
            {
                Guid scheme = (Guid)Marshal.PtrToStructure(active, typeof(Guid));
                LocalFree(active);
                PowerSetActiveScheme(IntPtr.Zero, ref scheme);
            }
        }

        static byte[] Payload(string name)
        {
            using (Stream s = Assembly.GetExecutingAssembly().GetManifestResourceStream(name))
            {
                if (s == null) throw new Exception("This setup file is incomplete (" + name + " is missing).");
                var ms = new MemoryStream();
                s.CopyTo(ms);
                return ms.ToArray();
            }
        }

        // Writes a file; if the old one is still in use, moves it aside (allowed even while it
        // runs) and deletes it at the next restart.
        static void PutFile(string path, byte[] data)
        {
            for (int attempt = 0; attempt < 8; attempt++)
            {
                try
                {
                    File.WriteAllBytes(path, data);
                    return;
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
                try
                {
                    string aside = path + "." + Guid.NewGuid().ToString("N").Substring(0, 6) + ".old";
                    File.Move(path, aside);
                    DeleteAtRestart(aside);
                }
                catch { Thread.Sleep(500); }
            }
            File.WriteAllBytes(path, data);
        }

        static void RemoveFolder(string dir)
        {
            if (!Directory.Exists(dir)) return;
            for (int attempt = 0; attempt < 10; attempt++)
            {
                try
                {
                    Directory.Delete(dir, true);
                    return;
                }
                catch { Thread.Sleep(500); }
            }
            // Whatever is still in use goes at the next restart.
            foreach (string f in Directory.GetFiles(dir, "*", SearchOption.AllDirectories))
                try { File.Delete(f); }
                catch { DeleteAtRestart(f); }
            foreach (string d in Directory.GetDirectories(dir, "*", SearchOption.AllDirectories)) DeleteAtRestart(d);
            DeleteAtRestart(dir);
        }

        public static void DeleteAtRestart(string path)
        {
            MoveFileEx(path, null, 4 /* MOVEFILE_DELAY_UNTIL_REBOOT */);
        }

        static void TryDelete(string path)
        {
            try { File.Delete(path); }
            catch { }
        }

        static void Note(string text)
        {
            try
            {
                if (!Directory.Exists(Data)) return;
                File.AppendAllText(Path.Combine(Data, "setup.log"),
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + "  " + text.TrimEnd() + "\r\n", Encoding.UTF8);
            }
            catch { }
        }

        static int Run(string exe, string args, int ms)
        {
            string output;
            return Run(exe, args, ms, out output);
        }

        static int Run(string exe, string args, int ms, out string output)
        {
            var text = new StringBuilder();
            output = "";
            try
            {
                var psi = new ProcessStartInfo(exe, args) { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
                using (Process p = Process.Start(psi))
                {
                    DataReceivedEventHandler collect = delegate(object s, DataReceivedEventArgs e) { if (e.Data != null) lock (text) text.Append(e.Data).Append(' '); };
                    p.OutputDataReceived += collect;
                    p.ErrorDataReceived += collect;
                    p.BeginOutputReadLine();
                    p.BeginErrorReadLine();
                    bool done = p.WaitForExit(ms);
                    if (done) p.WaitForExit(); // let the output finish
                    else { try { p.Kill(); } catch { } }
                    lock (text) output = text.ToString();
                    return done ? p.ExitCode : -1;
                }
            }
            catch (Exception ex)
            {
                output = ex.Message;
                return -1;
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        struct SYSTEM_POWER_STATUS { public byte ACLineStatus, BatteryFlag, BatteryLifePercent, SystemStatusFlag; public int BatteryLifeTime, BatteryFullLifeTime; }

        [DllImport("kernel32.dll")]
        static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS status);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        static extern bool MoveFileEx(string existing, string replacement, int flags);

        [DllImport("powrprof.dll")]
        static extern uint PowerEnumerate(IntPtr root, IntPtr scheme, IntPtr sub, uint access, uint index, byte[] buffer, ref uint size);

        [DllImport("powrprof.dll")]
        static extern uint PowerWriteDCValueIndex(IntPtr root, ref Guid scheme, ref Guid sub, ref Guid setting, uint value);

        [DllImport("powrprof.dll")]
        static extern uint PowerGetActiveScheme(IntPtr root, out IntPtr scheme);

        [DllImport("powrprof.dll")]
        static extern uint PowerSetActiveScheme(IntPtr root, ref Guid scheme);

        [DllImport("kernel32.dll")]
        static extern IntPtr LocalFree(IntPtr p);

        [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
        class ShellLink { }

        [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("000214F9-0000-0000-C000-000000000046")]
        interface IShellLinkW
        {
            void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder file, int max, IntPtr findData, int flags);
            void GetIDList(out IntPtr idList);
            void SetIDList(IntPtr idList);
            void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder name, int max);
            void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string name);
            void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder dir, int max);
            void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string dir);
            void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder args, int max);
            void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string args);
            void GetHotkey(out short hotkey);
            void SetHotkey(short hotkey);
            void GetShowCmd(out int showCmd);
            void SetShowCmd(int showCmd);
            void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder path, int max, out int index);
            void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string path, int index);
            void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string path, int reserved);
            void Resolve(IntPtr hwnd, int flags);
            void SetPath([MarshalAs(UnmanagedType.LPWStr)] string file);
        }
    }

    // Thin progress line.
    class ProgressLine : Control
    {
        int value;
        public int Value { get { return value; } set { this.value = Math.Max(0, Math.Min(100, value)); Invalidate(); } }

        public ProgressLine()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            BackColor = Theme.Bg;
            Height = 6;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(BackColor);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var track = new RectangleF(0, 0, Width - 1, Height - 1);
            using (GraphicsPath p = Theme.Rounded(track, Height / 2f))
            using (var b = new SolidBrush(Theme.Line)) g.FillPath(b, p);
            if (value > 0)
                using (GraphicsPath p = Theme.Rounded(new RectangleF(0, 0, Math.Max(Height, (Width - 1) * value / 100f), Height - 1), Height / 2f))
                using (var b = new SolidBrush(Theme.Blue)) g.FillPath(b, p);
        }
    }

    class SetupForm : Form
    {
        readonly bool uninstallMode;
        readonly string installed;
        readonly Label status;
        readonly ProgressLine bar;
        readonly FlatButton primary, secondary, extra;
        readonly ToggleSwitch removeData;
        readonly Label removeDataLabel;
        string headline, body;
        Color headlineColor = Theme.Text;
        List<string[]> checks = new List<string[]>(); // { colour key, text }
        bool busy, finished;

        public SetupForm(bool uninstall, string page)
        {
            uninstallMode = uninstall;
            installed = page == "new" ? null : Installer.InstalledVersion(); // "new": how a first install looks
            SuspendLayout();
            AutoScaleDimensions = new SizeF(96F, 96F);
            AutoScaleMode = AutoScaleMode.Dpi;
            Text = uninstall ? "Uninstall " + Product.Name : Product.Name + " Setup";
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Theme.Bg;
            Font = Theme.Pt(9.5f, false);
            Icon = Theme.AppIcon;
            ClientSize = new Size(560, 420);
            DoubleBuffered = true;

            status = new Label { ForeColor = Theme.Sub, Location = new Point(32, 314), Size = new Size(496, 20), Visible = false };
            bar = new ProgressLine { Location = new Point(32, 340), Size = new Size(496, 6), Visible = false };
            removeData = new ToggleSwitch { Location = new Point(32, 332), Visible = false };
            removeDataLabel = new Label { Text = "Also delete the history and logs", ForeColor = Theme.Text, AutoSize = true, Location = new Point(84, 335), Visible = false };
            primary = new FlatButton("Install", true) { Location = new Point(408, 364), Size = new Size(120, 36) };
            secondary = new FlatButton("Cancel", false) { Location = new Point(288, 364), Size = new Size(104, 36) };
            extra = new FlatButton("Uninstall", false) { Location = new Point(32, 364), Size = new Size(104, 36), Visible = false };
            primary.Click += delegate { OnPrimary(); };
            secondary.Click += delegate { Close(); };
            extra.Click += delegate { ShowUninstall(); };
            Controls.AddRange(new Control[] { status, bar, removeData, removeDataLabel, primary, secondary, extra });
            ResumeLayout(false);

            if (page == "done") ShowDone(false);
            else if (page == "error") ShowError("Copying the programs: access is denied.");
            else if (uninstall || page == "uninstall") ShowUninstall();
            else ShowWelcome();
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Theme.DarkTitleBar(this);
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (busy) e.Cancel = true; // never stop half-way
            base.OnFormClosing(e);
        }

        void ShowWelcome()
        {
            bool update = installed != null;
            headline = update ? (installed == Product.Version ? "Battery Guard is installed" : "Update Battery Guard") : "Install Battery Guard";
            headlineColor = Theme.Text;
            body = "Keeps a laptop with a faulty battery gauge running until the battery is really empty, then hibernates safely, with warnings first. " +
                   "Shows the real charge, the voltage, and every charge in a clear history.";
            checks.Clear();
            if (Installer.HasBattery()) checks.Add(new[] { "ok", "Battery found." });
            else checks.Add(new[] { "warn", "No battery found. Battery Guard is for laptops; it will wait until it can see a battery." });
            bool supported;
            if (PowerCaps.CanHibernate(out supported)) checks.Add(new[] { "ok", "Hibernation is available, so open work is kept when the battery is empty." });
            else if (supported) checks.Add(new[] { "info", "Hibernation is off. Setup turns it on, so open work is kept when the battery is empty." });
            else checks.Add(new[] { "warn", "This PC can't hibernate, so it will shut down safely instead." });
            checks.Add(new[] { "ok", update ? "Your settings, history and what it has learned are kept." : "For every user on this PC. Runs quietly in the background." });
            primary.Text = update ? (installed == Product.Version ? "Reinstall" : "Update") : "Install";
            secondary.Text = "Cancel";
            extra.Visible = update;
            Invalidate();
        }

        void ShowUninstall()
        {
            if (installed == null && !uninstallMode)
            {
                ShowWelcome();
                return;
            }
            Text = "Uninstall " + Product.Name;
            headline = "Uninstall Battery Guard";
            headlineColor = Theme.Text;
            body = "Removes Battery Guard from this PC. Windows' own low-battery warnings and hibernation come back. " +
                   "Note: with a faulty battery gauge, Windows will hibernate long before the battery is really empty.";
            checks.Clear();
            int windowsPct;
            if (Installer.OnBatteryAndLow(out windowsPct))
                checks.Add(new[] { "warn", "Plug in the charger first: Windows thinks the battery is at " + windowsPct + "%, so it would hibernate straight after." });
            checks.Add(new[] { "info", "History and logs stay in " + Installer.Data + " unless you choose to delete them." });
            primary.Text = "Uninstall";
            secondary.Text = "Cancel";
            extra.Visible = false;
            removeData.Visible = removeDataLabel.Visible = true;
            Invalidate();
        }

        void ShowDone(bool removed)
        {
            finished = true;
            headline = removed ? "Battery Guard has been removed" : "Battery Guard is ready";
            headlineColor = Theme.Green;
            body = removed
                ? "Windows' own low-battery rules are back in charge."
                : "It's running now and starts by itself with Windows, for every user. Its battery icon sits next to the clock; " +
                  "click it for the real charge, the voltage, the charging guide and the full history.";
            checks.Clear();
            if (!removed)
            {
                checks.Add(new[] { "ok", "Protection is on: it warns 5 minutes before the battery is empty, then counts down and hibernates." });
                checks.Add(new[] { "ok", "The real charge level gets more accurate with every charge and every run down." });
            }
            status.Visible = bar.Visible = removeData.Visible = removeDataLabel.Visible = extra.Visible = false;
            primary.Text = removed ? "Close" : "Open Battery Guard";
            if (!removed)
            {
                primary.Bounds = new Rectangle(primary.Right - 168, primary.Top, 168, primary.Height);
                secondary.Left = primary.Left - 8 - secondary.Width;
            }
            primary.Enabled = secondary.Enabled = true;
            secondary.Text = "Close";
            secondary.Visible = !removed;
            Invalidate();
        }

        void ShowError(string error)
        {
            finished = true;
            headline = "Setup couldn't finish";
            headlineColor = Theme.Red;
            body = error + "\n\nNothing has been left half-done that would stop Windows working normally. Details are in " +
                   Path.Combine(Installer.Data, "setup.log") + ". Restarting the PC and running Setup again usually fixes it.";
            checks.Clear();
            status.Visible = bar.Visible = removeData.Visible = removeDataLabel.Visible = extra.Visible = false;
            primary.Text = "Close";
            primary.Enabled = true;
            secondary.Visible = false;
            Invalidate();
        }

        void OnPrimary()
        {
            if (finished)
            {
                if (primary.Text == "Open Battery Guard") OpenApp();
                Close();
                return;
            }
            bool remove = primary.Text == "Uninstall";
            bool wipe = removeData.Checked;
            busy = true;
            primary.Enabled = secondary.Enabled = extra.Enabled = removeData.Enabled = false;
            status.Visible = bar.Visible = true;
            removeData.Visible = removeDataLabel.Visible = false;
            checks.Clear();
            body = remove ? "This takes a few seconds." : "This takes up to a minute.";
            headline = remove ? "Uninstalling\u2026" : installed != null ? "Updating\u2026" : "Installing\u2026";
            Invalidate();

            Installer.Progress report = delegate(int pct, string text)
            {
                BeginInvoke((MethodInvoker)delegate { bar.Value = pct; status.Text = text + (pct < 100 ? "\u2026" : ""); });
            };
            var worker = new Thread(delegate()
            {
                string error = remove ? Installer.Uninstall(wipe, report) : Installer.Install(report);
                BeginInvoke((MethodInvoker)delegate
                {
                    busy = false;
                    if (error == null) ShowDone(remove);
                    else ShowError(error);
                });
            });
            worker.SetApartmentState(ApartmentState.STA); // for the Start-menu shortcut
            worker.IsBackground = true;
            worker.Start();
        }

        // Opens the main window of the tray app the service just started in this session (it runs
        // as the signed-in user, not as admin like Setup).
        static void OpenApp()
        {
            for (int i = 0; i < 40; i++)
            {
                try
                {
                    using (EventWaitHandle show = EventWaitHandle.OpenExisting("Local\\BatteryGuardAgentShow", EventWaitHandleRights.Modify | EventWaitHandleRights.Synchronize))
                    {
                        show.Set();
                        return;
                    }
                }
                catch { Thread.Sleep(250); }
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            g.ScaleTransform(Theme.Scale, Theme.Scale);

            Theme.DrawLogo(g, 32, 30, 56);
            Theme.Draw(g, Product.Name, Theme.Px(24, true), Theme.Text, 102, 30);
            Theme.Draw(g, "Version " + Product.Version + "  \u00b7  by " + Product.Author, Theme.Px(12.5f, false), Theme.Sub, 104, 64);

            float y = 112;
            Theme.Draw(g, headline ?? "", Theme.Px(16, true), headlineColor, 32, y);
            y += 30;
            using (var b = new SolidBrush(Theme.Sub))
            {
                Font f = Theme.Px(12.5f, false);
                SizeF size = g.MeasureString(body ?? "", f, 496);
                g.DrawString(body ?? "", f, b, new RectangleF(32, y, 496, size.Height + 4));
                y += size.Height + 14;
            }
            if (checks.Count > 0)
            {
                Font f = Theme.Px(12.5f, false);
                float h = 16;
                foreach (string[] c in checks) h += g.MeasureString(c[1], f, 440).Height + 8;
                var card = new RectangleF(32, y, 496, h + 4);
                Theme.FillCard(g, card);
                float cy = card.Y + 12;
                foreach (string[] c in checks)
                {
                    Color dot = c[0] == "ok" ? Theme.Green : c[0] == "warn" ? Theme.Orange : Theme.Blue;
                    DrawMark(g, c[0], dot, card.X + 16, cy + 1);
                    SizeF size = g.MeasureString(c[1], f, 440);
                    using (var b = new SolidBrush(Theme.Text)) g.DrawString(c[1], f, b, new RectangleF(card.X + 42, cy, 440, size.Height + 2));
                    cy += size.Height + 8;
                }
            }
        }

        // A small round badge: a tick, an exclamation mark or an "i".
        static void DrawMark(Graphics g, string kind, Color color, float x, float y)
        {
            using (var b = new SolidBrush(color)) g.FillEllipse(b, x, y, 16, 16);
            using (var pen = new Pen(Color.White, 2f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
            {
                if (kind == "ok") g.DrawLines(pen, new[] { new PointF(x + 4.5f, y + 8.3f), new PointF(x + 7.2f, y + 11f), new PointF(x + 11.8f, y + 5.6f) });
                else if (kind == "warn")
                {
                    g.DrawLine(pen, x + 8, y + 4.2f, x + 8, y + 9);
                    using (var w = new SolidBrush(Color.White)) g.FillEllipse(w, x + 6.9f, y + 10.6f, 2.2f, 2.2f);
                }
                else
                {
                    g.DrawLine(pen, x + 8, y + 7.4f, x + 8, y + 11.8f);
                    using (var w = new SolidBrush(Color.White)) g.FillEllipse(w, x + 6.9f, y + 3.4f, 2.2f, 2.2f);
                }
            }
        }
    }
}
