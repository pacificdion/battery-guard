// Battery Guard - Copyright (C) 2026 Dion Fabian. All rights reserved. See LICENSE.
// The flyout that opens from the tray icon: the real battery level and Windows' power mode
// (Battery saver, Better battery, Better performance, Best performance), like Windows 10's own
// battery flyout. Drawn by hand into one layered window: no child controls, created once and
// reused, and only redrawn when something on it changes.
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Win32;

namespace BatteryGuard
{
    // Windows' power slider. The three performance modes are the overlays the battery flyout
    // itself sets. Battery saver has no switch an app can use, so it's turned on through its own
    // "turn on automatically at" level (set to 100%), and the previous level is put back after.
    static class PowerModes
    {
        public const int Saver = 0, BetterBattery = 1, BetterPerformance = 2, BestPerformance = 3;
        public static readonly string[] Names = { "Battery saver", "Better battery", "Better performance", "Best performance" };
        public static readonly string[] Notes =
        {
            "Dims the screen and limits background activity.",
            "Longer battery life, still quick to respond.",
            "A balance of speed and battery life.",
            "The fastest. Uses the most power.",
        };

        static readonly Guid BetterBatteryOverlay = new Guid("961cc777-2547-4f9d-8174-7d86181b8a7a");
        static readonly Guid BestPerformanceOverlay = new Guid("ded574b5-45a0-4f42-8737-46345c09c238");
        public static readonly Guid Balanced = new Guid("381b4222-f694-41f0-9685-ff5bb260df2e");
        static Guid subEnergySaver = new Guid("de830923-a562-41af-a086-e3a2c6bad2da");
        static Guid saverLevel = new Guid("e69653ca-cf7f-4f05-aa73-cb833fa90ad4");

        [StructLayout(LayoutKind.Sequential)]
        struct SYSTEM_POWER_STATUS { public byte ACLineStatus, BatteryFlag, BatteryLifePercent, SystemStatusFlag; public int BatteryLifeTime, BatteryFullLifeTime; }

        [DllImport("kernel32.dll")] static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS status);
        [DllImport("kernel32.dll")] static extern IntPtr LocalFree(IntPtr p);
        [DllImport("powrprof.dll")] static extern uint PowerGetEffectiveOverlayScheme(out Guid overlay);
        [DllImport("powrprof.dll")] static extern uint PowerSetActiveOverlayScheme(Guid overlay);
        [DllImport("powrprof.dll")] static extern uint PowerGetActiveScheme(IntPtr root, out IntPtr scheme);
        [DllImport("powrprof.dll")] static extern uint PowerSetActiveScheme(IntPtr root, ref Guid scheme);
        [DllImport("powrprof.dll")] static extern uint PowerReadDCValueIndex(IntPtr root, ref Guid scheme, ref Guid sub, ref Guid setting, out uint value);
        [DllImport("powrprof.dll")] static extern uint PowerWriteDCValueIndex(IntPtr root, ref Guid scheme, ref Guid sub, ref Guid setting, uint value);
        [DllImport("powrprof.dll")] static extern uint PowerReadFriendlyName(IntPtr root, ref Guid scheme, IntPtr sub, IntPtr setting, byte[] buffer, ref uint size);

        public static bool OnBattery()
        {
            SYSTEM_POWER_STATUS s;
            return GetSystemPowerStatus(out s) && s.ACLineStatus == 0;
        }

        public static bool SaverOn()
        {
            SYSTEM_POWER_STATUS s;
            return GetSystemPowerStatus(out s) && s.SystemStatusFlag == 1;
        }

        public static Guid ActivePlan()
        {
            IntPtr p;
            if (PowerGetActiveScheme(IntPtr.Zero, out p) != 0) return Guid.Empty;
            var g = (Guid)Marshal.PtrToStructure(p, typeof(Guid));
            LocalFree(p);
            return g;
        }

        public static string PlanName(Guid plan)
        {
            try
            {
                uint size = 0;
                PowerReadFriendlyName(IntPtr.Zero, ref plan, IntPtr.Zero, IntPtr.Zero, null, ref size);
                if (size == 0) return "another plan";
                var buffer = new byte[size];
                if (PowerReadFriendlyName(IntPtr.Zero, ref plan, IntPtr.Zero, IntPtr.Zero, buffer, ref size) != 0) return "another plan";
                return System.Text.Encoding.Unicode.GetString(buffer, 0, (int)size).TrimEnd('\0');
            }
            catch { return "another plan"; }
        }

        // Windows only offers power modes on the Balanced plan.
        public static bool Available() { return ActivePlan() == Balanced; }

        public static int Current(bool onBattery)
        {
            if (onBattery && SaverOn()) return Saver;
            Guid overlay;
            if (PowerGetEffectiveOverlayScheme(out overlay) != 0) return BetterPerformance;
            if (overlay == BetterBatteryOverlay) return BetterBattery;
            if (overlay == BestPerformanceOverlay) return BestPerformance;
            return BetterPerformance; // no overlay: the Balanced plan as it is
        }

        // Returns null, or what went wrong.
        public static string Set(int mode)
        {
            try
            {
                Guid plan = ActivePlan();
                if (mode == Saver)
                {
                    uint level;
                    if (PowerReadDCValueIndex(IntPtr.Zero, ref plan, ref subEnergySaver, ref saverLevel, out level) == 0 && level < 100) UserPrefs.SaverLevel = (int)level;
                    uint rc = PowerWriteDCValueIndex(IntPtr.Zero, ref plan, ref subEnergySaver, ref saverLevel, 100);
                    if (rc != 0) return "Windows didn't allow turning on Battery saver (error " + rc + ").";
                    PowerSetActiveScheme(IntPtr.Zero, ref plan);
                    return null;
                }
                RestoreSaverLevel(plan);
                Guid overlay = mode == BetterBattery ? BetterBatteryOverlay : mode == BestPerformance ? BestPerformanceOverlay : Guid.Empty;
                uint r = PowerSetActiveOverlayScheme(overlay);
                return r == 0 ? null : "Windows didn't allow changing the power mode (error " + r + ").";
            }
            catch (Exception ex) { return ex.Message; }
        }

        // Battery saver was turned on here: put its automatic level back as it was. Also when it
        // was turned off somewhere else (Windows' own flyout), so it doesn't come back by itself.
        public static void SyncSaver(bool onBattery)
        {
            if (UserPrefs.SaverLevel < 0) return;
            if (onBattery && !SaverOn()) RestoreSaverLevel(ActivePlan());
        }

        static void RestoreSaverLevel(Guid plan)
        {
            int keep = UserPrefs.SaverLevel;
            if (keep < 0) return;
            uint level;
            if (PowerReadDCValueIndex(IntPtr.Zero, ref plan, ref subEnergySaver, ref saverLevel, out level) == 0 && level == 100)
            {
                PowerWriteDCValueIndex(IntPtr.Zero, ref plan, ref subEnergySaver, ref saverLevel, (uint)keep);
                PowerSetActiveScheme(IntPtr.Zero, ref plan);
            }
            UserPrefs.SaverLevel = -1;
        }

        public static bool UseBalanced()
        {
            Guid plan = Balanced;
            return PowerSetActiveScheme(IntPtr.Zero, ref plan) == 0;
        }
    }

    class PowerFlyout : Form
    {
        [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; }
        [StructLayout(LayoutKind.Sequential)] struct SIZE { public int W, H; }
        [StructLayout(LayoutKind.Sequential, Pack = 1)] struct BLENDFUNCTION { public byte Op, Flags, Alpha, Format; }

        [DllImport("user32.dll")] static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr dst, ref POINT dstPos, ref SIZE size, IntPtr src, ref POINT srcPos, int key, ref BLENDFUNCTION blend, int flags);
        [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr hwnd);
        [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
        [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr dc);
        [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr dc);
        [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
        [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr obj);
        [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr hwnd);
        [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();

        const float W = 344, H = 318, Pad = 18, Radius = 12; // the card, and room around it for the shadow
        const int OpenMs = 150;

        readonly float scale = Theme.Scale;
        readonly Action openApp;
        readonly System.Windows.Forms.Timer anim;
        Status status;
        bool stale = true;

        // Colours, picked each time it opens: it follows the taskbar's light/dark setting.
        bool dark;
        Color text, sub, line, hoverFill, track, card1, card2, border, accent;

        bool onBattery, available;
        string planName = "", error = "";
        int mode;                 // the power mode now
        float thumb, thumbTarget; // the slider's knob, in stops (animated)
        bool dragging;
        int hover = -1, pressed = -1; // 0 slider, 1 Battery Guard, 2 power settings, 3 use Balanced
        int openedTick;
        float appear = 1;         // 0..1 while opening
        int slideFrom;            // pixels below (or above) where it settles, at the start
        Point home;               // where it settles
        DateTime hiddenUtc;
        int verifySaverAt;        // when to check that Battery saver really came on (0: no need)
        Bitmap frame;
        bool dirty = true;

        static readonly Color SaverGreen = Color.FromArgb(34, 197, 94), Teal = Color.FromArgb(20, 184, 166), Amber = Color.FromArgb(249, 115, 22);

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= 0x80000 /* LAYERED */ | 0x8 /* TOPMOST */ | 0x80 /* TOOLWINDOW */;
                return cp;
            }
        }

        public PowerFlyout(Action openApp)
        {
            this.openApp = openApp;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            Text = "Battery Guard";
            Size = new Size((int)Math.Ceiling((W + 2 * Pad) * scale), (int)Math.Ceiling((H + 2 * Pad) * scale));
            anim = new System.Windows.Forms.Timer { Interval = 15 };
            anim.Tick += delegate { Animate(); };
            Deactivate += delegate { HideFlyout(); };
        }

        public bool Showing { get { return Visible; } }

        // Just closed by the click that would open it again (the tray icon takes the focus first).
        public bool JustClosed { get { return (DateTime.UtcNow - hiddenUtc).TotalMilliseconds < 300; } }

        public void Open(Status s, bool isStale)
        {
            status = s;
            stale = isStale;
            ReadColours();
            ReadModes();
            thumb = thumbTarget = SliderIndex(mode);
            error = "";
            hover = pressed = -1;
            dragging = false;
            Place();
            appear = 0;
            openedTick = Environment.TickCount;
            dirty = true;
            if (!IsHandleCreated) CreateHandle();
            Push();
            Show();
            SetForegroundWindow(Handle);
            Activate();
            Kick();
        }

        void Kick()
        {
            anim.Interval = 15;
            anim.Start();
        }

        public void SetStatus(Status s, bool isStale)
        {
            status = s;
            stale = isStale;
            if (!Visible) return;
            bool battery = PowerModes.OnBattery();
            if (battery != onBattery && !dragging) { ReadModes(); thumb = thumbTarget = SliderIndex(mode); }
            dirty = true;
            Push();
        }

        public void HideFlyout()
        {
            if (!Visible) return;
            anim.Stop();
            dragging = false;
            verifySaverAt = 0;
            Hide();
            hiddenUtc = DateTime.UtcNow;
            if (frame != null) { frame.Dispose(); frame = null; }
        }

        void ReadColours() { SetColours(!Theme.LightTaskbar()); }

        void SetColours(bool darkLook)
        {
            dark = darkLook;
            text = dark ? Color.FromArgb(245, 245, 247) : Color.FromArgb(24, 24, 27);
            sub = dark ? Color.FromArgb(160, 161, 171) : Color.FromArgb(98, 102, 114);
            line = dark ? Color.FromArgb(22, 255, 255, 255) : Color.FromArgb(16, 0, 0, 0);
            hoverFill = dark ? Color.FromArgb(18, 255, 255, 255) : Color.FromArgb(11, 0, 0, 0);
            track = dark ? Color.FromArgb(46, 255, 255, 255) : Color.FromArgb(30, 0, 0, 0);
            card1 = dark ? Color.FromArgb(40, 40, 45) : Color.FromArgb(253, 253, 254);
            card2 = dark ? Color.FromArgb(31, 31, 35) : Color.FromArgb(244, 245, 248);
            border = dark ? Color.FromArgb(30, 255, 255, 255) : Color.FromArgb(26, 0, 0, 0);
            accent = Theme.Blue;
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\DWM"))
                {
                    object v = k == null ? null : k.GetValue("AccentColor");
                    if (v != null)
                    {
                        int c = Convert.ToInt32(v);
                        accent = Color.FromArgb(c & 0xFF, (c >> 8) & 0xFF, (c >> 16) & 0xFF);
                    }
                }
            }
            catch { }
            // Keep it bright enough to stand out on a dark flyout.
            if (dark && accent.R * 0.299 + accent.G * 0.587 + accent.B * 0.114 < 110) accent = Theme.Mix(accent, Color.White, 0.3);
        }

        void ReadModes()
        {
            onBattery = PowerModes.OnBattery();
            PowerModes.SyncSaver(onBattery);
            available = PowerModes.Available();
            planName = available ? "" : PowerModes.PlanName(PowerModes.ActivePlan());
            mode = PowerModes.Current(onBattery);
            if (!onBattery && mode == PowerModes.Saver) mode = PowerModes.BetterBattery;
        }

        // Battery saver is a stop on battery only, as in Windows.
        int Stops { get { return onBattery ? 4 : 3; } }
        int SliderIndex(int m) { return onBattery ? m : Math.Max(0, m - 1); }
        int ModeAt(int index) { return onBattery ? index : index + 1; }

        Color ModeColor(int m)
        {
            return m == PowerModes.Saver ? SaverGreen : m == PowerModes.BetterBattery ? Teal : m == PowerModes.BetterPerformance ? accent : Amber;
        }

        // Near the tray: the corner of the screen where the taskbar is.
        void Place()
        {
            Screen screen = Screen.FromPoint(Cursor.Position);
            Rectangle b = screen.Bounds, wa = screen.WorkingArea;
            bool top = wa.Y > b.Y, left = wa.X > b.X && wa.Width < b.Width;
            if (wa == b) wa = new Rectangle(wa.X, wa.Y, wa.Width, wa.Height - (int)(48 * scale)); // taskbar hides itself
            int gap = (int)((12 - Pad) * scale);
            int x = left ? wa.X + gap : wa.Right - Width - gap;
            int y = top ? wa.Y + gap : wa.Bottom - Height - gap;
            home = new Point(x, y);
            slideFrom = (int)((top ? -12 : 12) * scale);
            Location = new Point(x, y + slideFrom);
        }

        // ---- layout, in the card's own units ----
        RectangleF Card { get { return new RectangleF(Pad, Pad, W, H); } }
        float TrackLeft { get { return Pad + 34; } }
        float TrackRight { get { return Pad + W - 34; } }
        float TrackY { get { return Pad + 196; } }
        RectangleF SliderZone { get { return new RectangleF(TrackLeft - 18, TrackY - 20, TrackRight - TrackLeft + 36, 40); } }
        RectangleF AppButton { get { return new RectangleF(Pad + 12, Pad + H - 54, W / 2 - 18, 42); } }
        RectangleF SettingsButton { get { return new RectangleF(Pad + W / 2 + 6, Pad + H - 54, W / 2 - 18, 42); } }
        RectangleF BalancedButton { get { return new RectangleF(Pad + 22, Pad + 196, 170, 34); } }

        float StopX(float index) { return TrackLeft + (TrackRight - TrackLeft) * index / (Stops - 1); }

        int HitTest(Point p)
        {
            var q = new PointF(p.X / scale, p.Y / scale);
            if (available && SliderZone.Contains(q)) return 0;
            if (AppButton.Contains(q)) return 1;
            if (SettingsButton.Contains(q)) return 2;
            if (!available && BalancedButton.Contains(q)) return 3;
            return -1;
        }

        // ---- input ----
        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (dragging)
            {
                thumb = thumbTarget = Math.Max(0, Math.Min(Stops - 1, IndexAt(e.X)));
                dirty = true;
                Push();
                return;
            }
            int h = HitTest(e.Location);
            Cursor = h >= 0 ? Cursors.Hand : Cursors.Default;
            if (h != hover) { hover = h; dirty = true; Push(); }
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (hover != -1 && !dragging) { hover = -1; dirty = true; Push(); }
        }

        float IndexAt(int x)
        {
            return (x / scale - TrackLeft) / (TrackRight - TrackLeft) * (Stops - 1);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left) return;
            pressed = HitTest(e.Location);
            if (pressed == 0)
            {
                dragging = true;
                Capture = true;
                thumb = thumbTarget = Math.Max(0, Math.Min(Stops - 1, IndexAt(e.X)));
            }
            dirty = true;
            Push();
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (dragging)
            {
                dragging = false;
                Capture = false;
                Choose((int)Math.Round(Math.Max(0, Math.Min(Stops - 1, IndexAt(e.X)))));
            }
            else if (pressed > 0 && HitTest(e.Location) == pressed)
            {
                if (pressed == 1) { HideFlyout(); if (openApp != null) openApp(); }
                else if (pressed == 2) { HideFlyout(); OpenSettings(); }
                else if (pressed == 3)
                {
                    if (!PowerModes.UseBalanced()) error = "Couldn't switch to the Balanced plan.";
                    ReadModes();
                    thumb = thumbTarget = SliderIndex(mode);
                }
            }
            pressed = -1;
            dirty = true;
            Push();
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == Keys.Escape) { HideFlyout(); return true; }
            if (available && (keyData == Keys.Left || keyData == Keys.Down)) { Choose(SliderIndex(mode) - 1); return true; }
            if (available && (keyData == Keys.Right || keyData == Keys.Up)) { Choose(SliderIndex(mode) + 1); return true; }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        void Choose(int index)
        {
            index = Math.Max(0, Math.Min(Stops - 1, index));
            thumbTarget = index;
            int wanted = ModeAt(index);
            if (wanted != mode)
            {
                string problem = PowerModes.Set(wanted);
                error = problem ?? "";
                if (problem == null) mode = wanted;
                else thumbTarget = SliderIndex(mode);
                verifySaverAt = problem == null && wanted == PowerModes.Saver ? Environment.TickCount + 2000 : 0;
            }
            dirty = true;
            Kick();
        }

        static void OpenSettings()
        {
            try { System.Diagnostics.Process.Start("ms-settings:powersleep"); }
            catch { try { System.Diagnostics.Process.Start("control.exe", "powercfg.cpl"); } catch { } }
        }

        // ---- animation: opening, and the knob gliding to its stop ----
        void Animate()
        {
            bool busy = false;
            if (appear < 1)
            {
                float t = Math.Min(1, (Environment.TickCount - openedTick) / (float)OpenMs);
                appear = 1 - (1 - t) * (1 - t) * (1 - t); // ease out
                busy = appear < 1;
            }
            if (!dragging && Math.Abs(thumb - thumbTarget) > 0.001f)
            {
                thumb += (thumbTarget - thumb) * 0.35f;
                if (Math.Abs(thumb - thumbTarget) < 0.01f) thumb = thumbTarget;
                dirty = true;
                busy = true;
            }
            if (verifySaverAt != 0 && Environment.TickCount - verifySaverAt >= 0)
            {
                verifySaverAt = 0;
                if (!PowerModes.SaverOn())
                {
                    error = "Windows didn't turn Battery saver on.";
                    ReadModes();
                    thumbTarget = SliderIndex(mode);
                    dirty = true;
                    busy = true;
                }
            }
            Push();
            // If Windows didn't hand it the focus, it can't notice a click elsewhere: check.
            if (Visible && !dragging && Environment.TickCount - openedTick > 500 && GetForegroundWindow() != Handle) { HideFlyout(); return; }
            anim.Interval = !busy && Environment.TickCount - openedTick > 500 ? 250 : 15; // when idle, just the checks
        }

        // ---- drawing ----
        void Push()
        {
            if (!IsHandleCreated) return;
            if (dirty || frame == null)
            {
                if (frame == null) frame = new Bitmap(Width, Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                Render(frame);
                dirty = false;
            }
            IntPtr screen = GetDC(IntPtr.Zero), mem = CreateCompatibleDC(screen), hbmp = frame.GetHbitmap(Color.FromArgb(0)), old = SelectObject(mem, hbmp);
            try
            {
                var size = new SIZE { W = frame.Width, H = frame.Height };
                var src = new POINT();
                int y = home.Y + (int)Math.Round(slideFrom * (1 - appear));
                var dst = new POINT { X = home.X, Y = y };
                var blend = new BLENDFUNCTION { Op = 0, Flags = 0, Alpha = (byte)(255 * Math.Max(0, Math.Min(1, appear))), Format = 1 /* AC_SRC_ALPHA */ };
                UpdateLayeredWindow(Handle, screen, ref dst, ref size, mem, ref src, 0, ref blend, 2 /* ULW_ALPHA */);
            }
            finally
            {
                SelectObject(mem, old);
                DeleteObject(hbmp);
                DeleteDC(mem);
                ReleaseDC(IntPtr.Zero, screen);
            }
        }

        // The flyout as a picture with transparency (also used to check its look).
        public Bitmap Snapshot()
        {
            var bmp = new Bitmap(Width, Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            Render(bmp);
            return bmp;
        }

        // The shadow and the card change only with the light/dark look: drawn once, then copied.
        Bitmap backdrop;
        bool backdropDark;

        void Render(Bitmap bmp)
        {
            if (backdrop == null || backdropDark != dark)
            {
                if (backdrop != null) backdrop.Dispose();
                backdrop = new Bitmap(Width, Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                backdropDark = dark;
                using (Graphics g = Graphics.FromImage(backdrop))
                {
                    g.Clear(Color.Transparent);
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    g.ScaleTransform(scale, scale);
                    DrawBackdrop(g);
                }
            }
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.CompositingMode = CompositingMode.SourceCopy;
                g.DrawImageUnscaled(backdrop, 0, 0);
                g.CompositingMode = CompositingMode.SourceOver;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                g.ScaleTransform(scale, scale);
                Draw(g);
            }
        }

        // For a picture of it without showing it (the same as opening it, minus the window).
        public void Prepare(Status s, bool isStale, bool battery, int currentMode, bool modesAvailable, string plan, bool darkLook)
        {
            status = s;
            stale = isStale;
            SetColours(darkLook);
            onBattery = battery;
            mode = currentMode;
            available = modesAvailable;
            planName = plan;
            thumb = thumbTarget = SliderIndex(mode);
        }

        public int Hover { set { hover = value; } }

        void DrawBackdrop(Graphics g)
        {
            RectangleF card = Card;
            // Soft shadow, then the card.
            for (int i = 10; i >= 1; i--)
                using (GraphicsPath p = Theme.Rounded(new RectangleF(card.X - i, card.Y - i + 4, card.Width + 2 * i, card.Height + 2 * i), Radius + i))
                using (var b = new SolidBrush(Color.FromArgb(dark ? 13 : 6, 0, 0, 0)))
                    g.FillPath(b, p);
            using (GraphicsPath p = Theme.Rounded(card, Radius))
            {
                using (var fill = new LinearGradientBrush(new RectangleF(card.X, card.Y - 1, card.Width, card.Height + 2), card1, card2, 90f))
                    g.FillPath(fill, p);
                using (var pen = new Pen(border)) g.DrawPath(pen, p);
            }
        }

        void Draw(Graphics g)
        {
            RectangleF card = Card;
            DrawHeader(g, card);
            using (var pen = new Pen(line)) g.DrawLine(pen, card.X + 20, card.Y + 98, card.Right - 20, card.Y + 98);
            DrawModes(g, card);
            using (var pen = new Pen(line)) g.DrawLine(pen, card.X + 20, card.Bottom - 66, card.Right - 20, card.Bottom - 66);
            DrawButtons(g);
        }

        void DrawHeader(Graphics g, RectangleF card)
        {
            Status s = status;
            bool ok = !stale && s != null;
            double pct = ok ? s.Percent : -1;
            bool charging = ok && s.OnAC && s.ChargeState == "Charging";
            Color level = !ok ? sub : charging ? SaverGreen : s.Phase == "Countdown" || (pct >= 0 && pct <= 15 && !s.OnAC) ? Theme.Red
                : s.OnAC && s.ChargeState == "NotCharging" ? Theme.Orange : text;

            // A battery, filled to the level, with a bolt while charging.
            var body = new RectangleF(card.X + 22, card.Y + 30, 46, 25);
            using (GraphicsPath p = Theme.Rounded(body, 6.5f))
            using (var pen = new Pen(Theme.Alpha(text, dark ? 150 : 120), 2f))
                g.DrawPath(pen, p);
            using (GraphicsPath nub = Theme.Rounded(new RectangleF(body.Right + 2.5f, body.Y + 7.5f, 3.5f, 10), 1.7f))
            using (var b = new SolidBrush(Theme.Alpha(text, dark ? 150 : 120)))
                g.FillPath(b, nub);
            if (pct > 0)
            {
                var inner = new RectangleF(body.X + 3.5f, body.Y + 3.5f, (body.Width - 7) * (float)Math.Min(1, pct / 100), body.Height - 7);
                if (inner.Width >= 2)
                    using (GraphicsPath p = Theme.Rounded(inner, 3.5f))
                    using (var b = new SolidBrush(level))
                        g.FillPath(b, p);
            }
            if (charging)
            {
                float cx = body.X + body.Width / 2, cy = body.Y + body.Height / 2;
                var bolt = new[]
                {
                    new PointF(cx + 2.5f, cy - 10), new PointF(cx - 6, cy + 1.5f), new PointF(cx - 0.5f, cy + 1.5f),
                    new PointF(cx - 2.5f, cy + 10), new PointF(cx + 6, cy - 1.5f), new PointF(cx + 0.5f, cy - 1.5f),
                };
                using (var outline = new Pen(card1, 3f) { LineJoin = LineJoin.Round }) g.DrawPolygon(outline, bolt);
                using (var b = new SolidBrush(Color.White)) g.FillPolygon(b, bolt);
            }

            Theme.Draw(g, ok ? Fmt.Pct(pct) : "–", Theme.Px(34, true), text, card.X + 84, card.Y + 16);
            Theme.Draw(g, Describe(s, ok), Theme.Px(12.5f, false), sub, card.X + 87, card.Y + 62);

            // What Battery Guard is doing, as a small pill.
            string label = !ok ? "Not running" : s.Learn.Length > 0 ? "Learning" : s.LearnNext ? "Learning next run" : s.Protection == "Active" ? "Protected" : s.Protection == "Fallback" ? "Windows rules" : "Off";
            Color pill = !ok ? Theme.Red : s.Learn.Length > 0 ? Theme.Orange : s.LearnNext ? Theme.Blue : s.Protection == "Active" ? SaverGreen : Theme.Gray;
            Font pf = Theme.Px(11, true);
            SizeF ts = Theme.Measure(g, label, pf);
            var chip = new RectangleF(card.Right - 20 - ts.Width - 26, card.Y + 22, ts.Width + 26, 24);
            using (GraphicsPath p = Theme.Rounded(chip, 12))
            using (var b = new SolidBrush(Theme.Alpha(pill, dark ? 46 : 30)))
                g.FillPath(b, p);
            using (var b = new SolidBrush(pill)) g.FillEllipse(b, chip.X + 10, chip.Y + 9, 6, 6);
            Theme.Draw(g, label, pf, dark ? Theme.Mix(pill, Color.White, 0.35) : Theme.Mix(pill, Color.Black, 0.25), chip.X + 20, chip.Y + (chip.Height - ts.Height) / 2);
        }

        static string Describe(Status s, bool ok)
        {
            if (!ok) return "Battery Guard isn't running";
            if (s.Phase == "Countdown" || s.Phase == "Hibernating") return "Hibernating soon";
            if (s.OnAC)
            {
                if (s.ChargeState == "Charging") return s.MinutesToFull > 0 ? "Charging · full in " + Fmt.Minutes(s.MinutesToFull) : "Charging";
                if (s.ChargeState == "Full") return "Fully charged";
                if (s.StopsEarly && s.ReadyAtUtc > DateTime.UtcNow) return "Not charging · full charge after " + s.ReadyAtUtc.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture);
                return "Plugged in, not charging";
            }
            return s.MinutesLeft >= 0 ? Fmt.Minutes(s.MinutesLeft) + " left" : "On battery";
        }

        void DrawModes(Graphics g, RectangleF card)
        {
            float x = card.X + 22;
            Theme.Draw(g, "Power mode", Theme.Px(12, false), sub, x, card.Y + 114);
            if (!available)
            {
                Theme.Draw(g, "Not available on this plan", Theme.Px(16.5f, true), text, x, card.Y + 134);
                Theme.Draw(g, "Windows offers power modes on the Balanced plan only.", Theme.Px(11.5f, false), sub, x, card.Y + 160);
                Theme.Draw(g, "Now: " + planName, Theme.Px(11.5f, false), sub, x, card.Y + 176);
                RectangleF bb = BalancedButton;
                using (GraphicsPath p = Theme.Rounded(bb, 8))
                using (var b = new SolidBrush(hover == 3 ? Theme.Mix(accent, Color.White, 0.12) : accent))
                    g.FillPath(b, p);
                Font bf = Theme.Px(12, true);
                SizeF bs = Theme.Measure(g, "Switch to Balanced", bf);
                Theme.Draw(g, "Switch to Balanced", bf, Color.White, bb.X + (bb.Width - bs.Width) / 2, bb.Y + (bb.Height - bs.Height) / 2);
                if (error.Length > 0) Theme.Draw(g, error, Theme.Px(11, false), Theme.Red, x, card.Y + 236);
                return;
            }

            // The mode it's on now (or would be, while dragging), with its colour.
            int shown = ModeAt((int)Math.Round(Math.Max(0, Math.Min(Stops - 1, thumb))));
            Color mc = ModeColor(shown);
            using (var b = new SolidBrush(mc)) g.FillEllipse(b, x, card.Y + 140, 9, 9);
            Theme.Draw(g, PowerModes.Names[shown], Theme.Px(17, true), text, x + 15, card.Y + 131);
            Theme.Draw(g, error.Length > 0 ? error : PowerModes.Notes[shown], Theme.Px(11.5f, false), error.Length > 0 ? Theme.Red : sub, x, card.Y + 159);

            // The slider: a track, the stops, and a knob; the part up to the knob is filled from
            // green (battery) to the mode's own colour.
            float y = TrackY, left = TrackLeft, right = TrackRight, kx = StopX(thumb);
            using (GraphicsPath p = Theme.Rounded(new RectangleF(left, y - 3, right - left, 6), 3))
            using (var b = new SolidBrush(track))
                g.FillPath(b, p);
            if (kx - left > 1)
            {
                var filled = new RectangleF(left, y - 3, kx - left, 6);
                using (GraphicsPath p = Theme.Rounded(filled, 3))
                using (var b = new LinearGradientBrush(new RectangleF(left - 1, y - 3, kx - left + 2, 6), Theme.Mix(SaverGreen, mc, 0.25), mc, 0f))
                    g.FillPath(b, p);
            }
            for (int i = 0; i < Stops; i++)
            {
                float sx = StopX(i);
                bool passed = sx <= kx + 0.5f;
                using (var b = new SolidBrush(passed ? Color.FromArgb(220, 255, 255, 255) : Theme.Alpha(text, dark ? 90 : 70)))
                    g.FillEllipse(b, sx - 2.5f, y - 2.5f, 5, 5);
            }
            bool active = hover == 0 || dragging;
            float r = dragging ? 12.5f : active ? 11.5f : 10.5f;
            if (active)
                using (var b = new SolidBrush(Theme.Alpha(mc, dark ? 60 : 45))) g.FillEllipse(b, kx - r - 6, y - r - 6, 2 * r + 12, 2 * r + 12);
            using (var b = new SolidBrush(Color.FromArgb(dark ? 70 : 40, 0, 0, 0))) g.FillEllipse(b, kx - r, y - r + 1.5f, 2 * r, 2 * r);
            using (var b = new SolidBrush(Color.White)) g.FillEllipse(b, kx - r, y - r, 2 * r, 2 * r);
            using (var b = new SolidBrush(mc)) g.FillEllipse(b, kx - r + 5, y - r + 5, 2 * r - 10, 2 * r - 10);

            Font ef = Theme.Px(11, false);
            Theme.Draw(g, "Best battery life", ef, sub, left - 8, y + 18);
            SizeF es = Theme.Measure(g, "Best performance", ef);
            Theme.Draw(g, "Best performance", ef, sub, right + 8 - es.Width, y + 18);
        }

        void DrawButtons(Graphics g)
        {
            DrawButton(g, AppButton, "Battery Guard", 1, true);
            DrawButton(g, SettingsButton, "Power & sleep", 2, false);
        }

        void DrawButton(Graphics g, RectangleF r, string label, int id, bool logo)
        {
            if (hover == id || pressed == id)
                using (GraphicsPath p = Theme.Rounded(r, 8))
                using (var b = new SolidBrush(pressed == id ? Theme.Alpha(text, dark ? 30 : 20) : hoverFill))
                    g.FillPath(b, p);
            Font f = Theme.Px(12.5f, false);
            SizeF ts = Theme.Measure(g, label, f);
            float icon = 18, gap = 9, total = icon + gap + ts.Width;
            float x = r.X + (r.Width - total) / 2, cy = r.Y + r.Height / 2;
            if (logo) Theme.DrawLogo(g, x, cy - icon / 2, icon);
            else DrawGear(g, x + icon / 2, cy, icon / 2);
            Theme.Draw(g, label, f, text, x + icon + gap, cy - ts.Height / 2);
        }

        void DrawGear(Graphics g, float cx, float cy, float r)
        {
            using (var path = new GraphicsPath())
            {
                const int teeth = 8;
                float outer = r * 0.95f, inner = r * 0.72f;
                var pts = new PointF[teeth * 4];
                for (int i = 0; i < teeth; i++)
                {
                    double a0 = (i - 0.22) * 2 * Math.PI / teeth, a1 = (i + 0.22) * 2 * Math.PI / teeth;
                    double b0 = (i + 0.36) * 2 * Math.PI / teeth, b1 = (i + 0.64) * 2 * Math.PI / teeth;
                    pts[i * 4] = new PointF(cx + outer * (float)Math.Cos(a0), cy + outer * (float)Math.Sin(a0));
                    pts[i * 4 + 1] = new PointF(cx + outer * (float)Math.Cos(a1), cy + outer * (float)Math.Sin(a1));
                    pts[i * 4 + 2] = new PointF(cx + inner * (float)Math.Cos(b0), cy + inner * (float)Math.Sin(b0));
                    pts[i * 4 + 3] = new PointF(cx + inner * (float)Math.Cos(b1), cy + inner * (float)Math.Sin(b1));
                }
                path.AddPolygon(pts);
                using (var pen = new Pen(text, 1.6f) { LineJoin = LineJoin.Round }) g.DrawPath(pen, path);
                using (var pen = new Pen(text, 1.6f)) g.DrawEllipse(pen, cx - r * 0.3f, cy - r * 0.3f, r * 0.6f, r * 0.6f);
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                anim.Dispose();
                if (frame != null) frame.Dispose();
                if (backdrop != null) backdrop.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
