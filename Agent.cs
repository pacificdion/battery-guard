// Battery Guard - Copyright (C) 2026 Dion Fabian. All rights reserved. See LICENSE.
// BatteryGuard.exe - the tray app. One runs in each user's session (the core service starts
// it). It shows the real battery level, the warnings and the countdown, and a small dashboard
// with the details and settings. All decisions are made by BatteryGuardCore.exe.
// Everything is drawn only while it's on screen, so it costs nothing in the background.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

[assembly: System.Reflection.AssemblyTitle("Battery Guard")]
[assembly: System.Reflection.AssemblyDescription("Battery Guard: real battery level, warnings, charging guide and history")]

namespace BatteryGuard
{
    static class AgentProgram
    {
        [STAThread]
        static void Main(string[] args)
        {
            Paths.Init(args);
            // log.txt belongs to the service; the tray app's notes go to it through the inbox.
            Log.Sink = delegate(string message) { Commands.Send("log\n" + message); };
            bool show = Array.IndexOf(args, "--show") >= 0;
            bool created;
            using (var mutex = new Mutex(true, "Local\\BatteryGuardAgent" + Paths.Suffix, out created))
            using (var showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, "Local\\BatteryGuardAgentShow" + Paths.Suffix))
            {
                if (!created)
                {
                    if (show) showEvent.Set();
                    return;
                }
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
                Application.ThreadException += delegate(object s, ThreadExceptionEventArgs e)
                {
                    Log.Write("Tray app error (" + Environment.UserName + "): " + e.Exception);
                };
                Application.Run(new Agent(showEvent, show));
                GC.KeepAlive(mutex);
            }
        }
    }

    // Each user's own preferences (HKCU).
    static class UserPrefs
    {
        const string Key = @"Software\BatteryGuard";

        static int Get(string name, int fallback)
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(Key))
                {
                    object v = k == null ? null : k.GetValue(name);
                    return v == null ? fallback : Convert.ToInt32(v);
                }
            }
            catch { return fallback; }
        }

        static void Set(string name, int value)
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.CreateSubKey(Key)) k.SetValue(name, value, RegistryValueKind.DWord);
            }
            catch { }
        }

        public static bool ShowTray
        {
            get { return Get("ShowTray", 1) != 0; }
            set { Set("ShowTray", value ? 1 : 0); }
        }

        // A voice says so when charging stops before the battery is full.
        public static bool VoiceChargeStop
        {
            get { return Get("VoiceChargeStop", 0) != 0; }
            set { Set("VoiceChargeStop", value ? 1 : 0); }
        }

        // Battery saver's own "turn on automatically at" level, kept while the power flyout has it
        // at 100% to switch Battery saver on (-1 = not changed).
        public static int SaverLevel
        {
            get { return Get("SaverLevel", -1); }
            set { Set("SaverLevel", value); }
        }

        // The last learning result (LearnSeq) this user has been shown, so a result that arrived
        // while the tray app wasn't running (an update, a restart) is still announced once.
        public static int SeenLearnSeq
        {
            get { return Get("SeenLearnSeq", -1); }
            set { Set("SeenLearnSeq", value); }
        }

        // Where the small countdown timer was last left (-1, -1 = not yet).
        public static Point MiniPosition
        {
            get { return new Point(Get("MiniX", -1), Get("MiniY", -1)); }
            set { Set("MiniX", value.X); Set("MiniY", value.Y); }
        }
    }

    // The spoken alert (only if the user switched it on): a short, natural female voice saying
    // "Charging stopped early.", recorded into the app (made with Kokoro, an open-source voice,
    // Apache-2.0). Played by Windows itself, so nothing heavy is loaded.
    static class Voice
    {
        static System.Media.SoundPlayer player;

        public static void ChargingStopped()
        {
            try
            {
                if (player == null)
                {
                    Stream clip = System.Reflection.Assembly.GetEntryAssembly().GetManifestResourceStream("charging-stopped.wav");
                    if (clip == null) return;
                    player = new System.Media.SoundPlayer(clip);
                    player.Load();
                }
                player.Play(); // in the background
            }
            catch (Exception ex) { Log.Write("Couldn't play the voice alert (" + Environment.UserName + "): " + ex.Message); }
        }
    }

    class Agent : ApplicationContext
    {
        readonly NotifyIcon tray;
        readonly System.Windows.Forms.Timer timer, soon;
        readonly Control invoker;
        FileSystemWatcher watcher;
        Status status;
        DateTime statusStamp;
        bool stale = true;
        int shownWarningSeq = -1, shownCountdownSeq = -1, shownCycleSeq = -1, shownLearnSeq = -1, polls;
        bool lightTaskbar;
        string seenChargeState;
        string shownTip;
        CountdownForm countdown;
        NoticeForm notice;
        MainForm main;
        HistoryForm history;
        PowerFlyout flyout;
        string iconKey;
        Icon icon;

        public Agent(EventWaitHandle showEvent, bool showNow)
        {
            invoker = new Control();
            IntPtr forceHandle = invoker.Handle;

            var menu = new ContextMenuStrip();
            menu.Items.Add("Open Battery Guard", null, delegate { ShowMain(); });
            menu.Items.Add("Hide this icon", null, delegate { SetTrayVisible(false); });
            tray = new NotifyIcon { ContextMenuStrip = menu, Text = "Battery Guard", Visible = UserPrefs.ShowTray };
            tray.MouseClick += delegate(object s, MouseEventArgs e) { if (e.Button == MouseButtons.Left) ToggleFlyout(); };

            ThreadPool.RegisterWaitForSingleObject(showEvent, delegate { invoker.BeginInvoke((MethodInvoker)ShowMain); }, null, -1, false);

            // The service rewrites status.ini whenever something changes; react to that at once
            // instead of checking every few seconds. The timer is only a slow safety net (and
            // notices if the service stops).
            soon = new System.Windows.Forms.Timer { Interval = 150 };
            soon.Tick += delegate { soon.Stop(); Refresh(); };
            try
            {
                watcher = new FileSystemWatcher(Paths.Data, "status.ini")
                {
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName,
                    IncludeSubdirectories = false,
                    SynchronizingObject = invoker,
                };
                FileSystemEventHandler changed = delegate { if (!soon.Enabled) soon.Start(); };
                watcher.Changed += changed;
                watcher.Created += changed;
                watcher.Renamed += delegate { if (!soon.Enabled) soon.Start(); };
                watcher.EnableRaisingEvents = true;
            }
            catch { watcher = null; }

            timer = new System.Windows.Forms.Timer { Interval = 30000 };
            timer.Tick += delegate { Refresh(); };
            timer.Start();
            Refresh();
            if (showNow) ShowMain();
            else Memory.Trim(); // start-up code isn't needed again
            var settle = new System.Windows.Forms.Timer { Interval = 30000 };
            settle.Tick += delegate
            {
                settle.Dispose();
                if (main == null && history == null) Memory.Trim();
            };
            settle.Start();
        }

        public void SetTrayVisible(bool visible)
        {
            if (visible == tray.Visible) return;
            UserPrefs.ShowTray = visible;
            tray.Visible = visible;
            if (!visible)
                ShowNotice("Icon hidden", "Protection and warnings stay on. To open Battery Guard again: Start menu > Battery Guard.", Theme.Blue, 15);
        }

        void Refresh()
        {
            try
            {
                var fi = new FileInfo(Paths.Status);
                if (fi.Exists && fi.LastWriteTimeUtc != statusStamp)
                {
                    Status s = Status.Load();
                    if (s != null)
                    {
                        status = s;
                        statusStamp = fi.LastWriteTimeUtc;
                    }
                }
            }
            catch { }
            stale = status == null || Math.Abs((DateTime.UtcNow - status.UpdatedUtc).TotalSeconds) > 75;

            // The charger just stopped although the battery isn't full: say so, if wanted.
            if (!stale)
            {
                if (seenChargeState == "Charging" && status.OnAC && status.ChargeState == "NotCharging" && status.Percent >= 0 && status.Percent < 90 && UserPrefs.VoiceChargeStop)
                    Voice.ChargingStopped();
                seenChargeState = status.ChargeState;
            }

            UpdateTray();
            HandlePhase();
            bool open = main != null && main.Visible;
            if (open) main.ShowStatus(status, stale);
            if (flyout != null && flyout.Showing) flyout.SetStatus(status, stale);
            // With the watcher, the timer only keeps "x min ago" texts fresh and spots a stopped
            // service; without it (it couldn't start), poll like before.
            int interval = countdown != null ? 1000 : watcher == null ? (open ? 2000 : 5000) : open ? 10000 : 30000;
            if (timer.Interval != interval) timer.Interval = interval;
        }

        void HandlePhase()
        {
            if (stale || status.Phase != "Countdown") CloseCountdown();
            if (stale) return;

            if (status.CycleSeq != shownCycleSeq)
            {
                shownCycleSeq = status.CycleSeq;
                if (status.Cycle == "wait" && status.OnAC)
                    ShowNotice("Charging stopped at about " + Fmt.Pct(status.Percent),
                        string.Format("This battery only takes a full charge about {0} after its last one. The next full charge is possible after {1}. Until then, plugging in again only adds about 20% and restarts the wait, so leave it as it is.",
                            Fmt.Hours(status.NeedGapHours), Fmt.When(status.ReadyAtUtc)),
                        Theme.Orange, 900);
                else if (status.Cycle == "unplug" && status.OnAC)
                    ShowNotice("Ready for a full charge - unplug first",
                        string.Format("Enough time has passed. Unplug the charger and use the laptop on battery for about {0}; Battery Guard will then tell you to plug it back in, and that charge should go all the way to full.",
                            Fmt.Minutes(status.CycleEtaMin)),
                        Theme.Blue, 1800);
                else if (status.Cycle == "ready" && !status.OnAC)
                    ShowNotice("Plug in the charger now",
                        "This charge should go all the way to full. Leave it plugged in until Battery Guard says Full.",
                        Theme.Blue, 3600);
            }
            // Learning the battery: one notice per step (held back during a countdown, so it isn't
            // lost behind it; shown again at each sign-in while a step is waiting).
            if (status.LearnSeq != shownLearnSeq && status.Phase == "Normal")
            {
                shownLearnSeq = status.LearnSeq;
                bool unseen = status.LearnSeq != UserPrefs.SeenLearnSeq;
                UserPrefs.SeenLearnSeq = status.LearnSeq;
                string[] note = LearnNotice(status, unseen);
                if (note != null) ShowNotice(note[0], note[1], Theme.Blue, 1800);
            }
            if (status.Phase == "Warning" && status.WarningSeq != shownWarningSeq && status.Learn.Length > 0)
            {
                shownWarningSeq = status.WarningSeq;
                ShowNotice("Save your work now", status.Message + " Plug in if you want to stop learning.", Theme.Orange, 300);
            }
            if (status.Phase == "Warning" && status.WarningSeq != shownWarningSeq)
            {
                shownWarningSeq = status.WarningSeq;
                string plug = !status.StopsEarly ? " Plug in the charger now."
                    : status.ReadyForFull ? " Plug in now: this charge should go all the way to full."
                    : " Plug in now. It will only add about 20% this time; a full charge is possible after " + Fmt.When(status.ReadyAtUtc) + ".";
                ShowNotice("Battery low - plug in now", status.Message + plug + " If it hibernates first, your work stays open.",
                    Theme.Orange, 45);
            }

            if (status.Phase == "Countdown")
            {
                if (countdown == null || shownCountdownSeq != status.CountdownSeq)
                {
                    CloseCountdown();
                    if (notice != null && !notice.IsDisposed) notice.Close();
                    shownCountdownSeq = status.CountdownSeq;
                    countdown = new CountdownForm();
                    countdown.Show();
                    Log.Write("Countdown shown to " + Environment.UserName + ".");
                }
                countdown.SetState(status);
            }
        }

        // Title and text for the current learning step, or null.
        // While learning: at every sign-in. A finished result: once per user, also when it arrived
        // while the tray app wasn't running (so learning never ends without a word).
        static string[] LearnNotice(Status s, bool unseen)
        {
            if (s.Learn.Length > 0)
                return new[] { "Battery Guard is learning this battery",
                    "Until this battery has run all the way down once, Battery Guard won't hibernate: when the battery gives out, the laptop will switch itself off. That's how it learns how low this battery can really go. Save your work often until then. Nothing else to do - use and charge it as usual." };
            if (s.LearnNext)
                return unseen ? new[] { "Learning starts with the next run",
                    "This run on battery is protected as usual. Learning starts when you plug in: from the next time you unplug, Battery Guard won't hibernate until the battery gives out, so it learns exactly where that is. Save your work often during that run." } : null;
            if (unseen && s.LearnSummary.Length > 0)
                return new[] { "Learning complete - normal protection is back",
                    s.LearnSummary + " From now on it hibernates there instead of letting the battery run out. To learn it again (best from a full charge), open Settings and choose Learn my battery." };
            return null;
        }
        void CloseCountdown()
        {
            if (countdown == null) return;
            CountdownForm f = countdown;
            countdown = null;
            f.Close();
            f.Dispose();
        }

        void ShowNotice(string title, string text, Color accent, int seconds)
        {
            if (notice != null && !notice.IsDisposed) notice.Close();
            Log.Write("Popup shown to " + Environment.UserName + ": " + title + " - " + text);
            notice = new NoticeForm(title, text, accent, seconds);
            notice.FormClosed += delegate { Memory.Trim(); };
            notice.Show();
        }

        // The tray icon's flyout: battery level and Windows' power mode. Made once, then reused.
        void ToggleFlyout()
        {
            if (flyout == null || flyout.IsDisposed) flyout = new PowerFlyout(ShowMain);
            if (flyout.Showing) { flyout.HideFlyout(); return; }
            if (flyout.JustClosed) return; // this click is what closed it
            flyout.Open(status, stale);
        }

        void ShowMain()
        {
            if (main == null || main.IsDisposed)
            {
                main = new MainForm(this);
                main.FormClosed += delegate { main = null; Memory.Trim(); };
            }
            main.ShowStatus(status, stale);
            main.Show();
            if (main.WindowState == FormWindowState.Minimized) main.WindowState = FormWindowState.Normal;
            main.Activate();
        }

        public void ShowHistory()
        {
            if (history == null || history.IsDisposed)
            {
                history = new HistoryForm();
                history.FormClosed += delegate { history = null; Memory.Trim(); };
            }
            history.Show();
            if (history.WindowState == FormWindowState.Minimized) history.WindowState = FormWindowState.Normal;
            history.Activate();
        }

        void UpdateTray()
        {
            Theme.Look look = Theme.State(status, stale);
            string text, tip;
            if (stale)
            {
                text = "-";
                tip = "service isn't running";
            }
            else
            {
                if (status.Phase == "Countdown" || status.Phase == "Hibernating") text = "!";
                else if (status.Percent < 0) text = "";
                else text = Math.Max(0, Math.Min(100, (int)Math.Round(status.Percent))).ToString(CultureInfo.InvariantCulture);
                string detail = !status.OnAC ? Fmt.Minutes(status.MinutesLeft) + " left" : look.Label.ToLowerInvariant();
                tip = Fmt.Pct(status.Percent) + " \u00b7 " + Fmt.Volts(status.VoltageMv) + " \u00b7 " + detail;
            }
            tip = "Battery Guard: " + tip;
            if (tip.Length > 63) tip = tip.Substring(0, 63);
            if (tip != shownTip) tray.Text = shownTip = tip;

            if (polls++ % 2 == 0) lightTaskbar = Theme.LightTaskbar();
            double level = stale || status.Percent < 0 ? 0 : Math.Max(0, Math.Min(100, status.Percent));
            Color fill;
            if (stale) fill = Color.FromArgb(142, 142, 147);
            else if (status.Phase == "Countdown" || status.Phase == "Hibernating") fill = IconRed;
            else if (status.OnAC && status.ChargeState != "NotCharging") fill = IconGreen;
            else if (status.OnAC) fill = IconAmber;
            else if (level <= 20) fill = IconRed;
            else fill = lightTaskbar ? Color.FromArgb(58, 58, 60) : Color.FromArgb(245, 245, 247);
            string key = text + "|" + Math.Round(14 * level / 100) + "|" + fill.ToArgb() + "|" + lightTaskbar;
            if (key == iconKey) return;
            iconKey = key;
            Icon old = icon;
            icon = MakeIcon(text, level, fill, lightTaskbar);
            tray.Icon = icon;
            if (old != null) old.Dispose();
        }

        static readonly Color IconGreen = Color.FromArgb(52, 199, 89), IconRed = Color.FromArgb(255, 69, 58), IconAmber = Color.FromArgb(255, 204, 0);
        static FontFamily iconFont;

        // Battery icon: rounded translucent body, a coloured fill for the charge, and the percentage
        // on top in Segoe UI Bold. Drawn 4x larger and scaled down so the edges come out smooth.
        public static Icon MakeIcon(string text, double level, Color fill, bool lightTaskbar)
        {
            const int k = 4;
            if (iconFont == null) iconFont = new FontFamily("Segoe UI");
            Color track = lightTaskbar ? Color.FromArgb(70, 0, 0, 0) : Color.FromArgb(125, 255, 255, 255);
            Color ink = Color.FromArgb(16, 16, 18);
            Color onFill = fill.R * 0.299 + fill.G * 0.587 + fill.B * 0.114 < 110 ? Color.White : ink;
            using (var big = new Bitmap(16 * k, 16 * k, System.Drawing.Imaging.PixelFormat.Format32bppArgb))
            {
                using (Graphics g = Graphics.FromImage(big))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    var body = new RectangleF(0, 3 * k, 14 * k, 10 * k);
                    var filled = new RectangleF(0, body.Y, (float)Math.Round(14 * level / 100) * k, body.Height);
                    using (GraphicsPath shape = Theme.Rounded(body, 3 * k))
                    using (var trackBrush = new SolidBrush(track))
                    using (var fillBrush = new SolidBrush(fill))
                    {
                        g.FillPath(trackBrush, shape);
                        g.SetClip(shape);
                        g.FillRectangle(fillBrush, filled);
                        g.ResetClip();
                    }
                    using (GraphicsPath nub = Theme.Rounded(new RectangleF(14.6f * k, 6.2f * k, 1.4f * k, 3.6f * k), 0.7f * k))
                    using (var nubBrush = new SolidBrush(track))
                        g.FillPath(nubBrush, nub);

                    if (text.Length > 0)
                        using (var digits = new GraphicsPath())
                        {
                            digits.AddString(text, iconFont, (int)FontStyle.Bold, 100, PointF.Empty, StringFormat.GenericTypographic);
                            RectangleF b = digits.GetBounds();
                            float scale = Math.Min(6.6f * k / b.Height, 11.5f * k / b.Width);
                            using (var m = new Matrix())
                            {
                                m.Scale(scale, scale);
                                m.Translate(7 * k - (b.X + b.Width / 2) * scale, 8 * k - (b.Y + b.Height / 2) * scale, MatrixOrder.Append);
                                digits.Transform(m);
                            }
                            // Contrasting colour over the fill, dark over the empty part.
                            using (var onFillBrush = new SolidBrush(onFill))
                            using (var inkBrush = new SolidBrush(ink))
                            using (var rest = new Region(new RectangleF(0, 0, 16 * k, 16 * k)))
                            {
                                g.SetClip(filled);
                                g.FillPath(onFillBrush, digits);
                                rest.Exclude(filled);
                                g.Clip = rest;
                                g.FillPath(inkBrush, digits);
                                g.ResetClip();
                            }
                        }
                }
                using (var small = new Bitmap(16, 16, System.Drawing.Imaging.PixelFormat.Format32bppArgb))
                {
                    using (Graphics g = Graphics.FromImage(small))
                    {
                        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                        g.DrawImage(big, new Rectangle(0, 0, 16, 16));
                    }
                    return Theme.ToIcon(small);
                }
            }
        }
    }
    // Borderless, topmost card that doesn't steal keyboard focus from what the user is typing.
    class PopupForm : Form
    {
        protected Color Accent = Theme.Blue;

        protected override bool ShowWithoutActivation { get { return true; } }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= 0x8;       // WS_EX_TOPMOST
                cp.ClassStyle |= 0x20000; // CS_DROPSHADOW
                return cp;
            }
        }

        public PopupForm()
        {
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            ShowInTaskbar = false;
            BackColor = Theme.Card;
            DoubleBuffered = true;
            AutoScaleDimensions = new SizeF(96F, 96F);
            AutoScaleMode = AutoScaleMode.Dpi;
            Font = Theme.Pt(9.5f, false);
            Text = "Battery Guard";
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            using (var pen = new Pen(Theme.Line)) e.Graphics.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
            using (var b = new SolidBrush(Accent)) e.Graphics.FillRectangle(b, 0, 0, 5, Height);
        }

        protected void PlaceBottomRight()
        {
            Rectangle wa = FreeArea();
            Location = new Point(wa.Right - Width - 16, wa.Bottom - Height - 16);
        }

        // The screen minus the taskbar - also when the taskbar hides itself (Windows then counts
        // the whole screen as free, and a popup in the corner would sit under it).
        public static Rectangle FreeArea()
        {
            Screen screen = Screen.PrimaryScreen;
            Rectangle wa = screen.WorkingArea;
            if (wa != screen.Bounds) return wa;
            int bar = (int)(48 * Theme.Scale);
            return new Rectangle(wa.X, wa.Y, wa.Width, wa.Height - bar);
        }

        protected FlatButton AddButton(string text, bool primary, int x, int y, int width)
        {
            var b = new FlatButton(text, primary) { Location = new Point(x, y), Size = new Size(width, 32), BackColor = Theme.Card };
            Controls.Add(b);
            return b;
        }
    }

    class NoticeForm : PopupForm
    {
        public NoticeForm(string title, string message, Color accent, int autoCloseSeconds)
        {
            Accent = accent;
            SuspendLayout();
            int width = 400;
            Size textSize = TextRenderer.MeasureText(message, Theme.Pt(9.5f, false), new Size(width - 44, 0), TextFormatFlags.WordBreak);
            ClientSize = new Size(width, 92 + textSize.Height);
            Controls.Add(new Label { Text = title, Font = Theme.Pt(11, true), ForeColor = Theme.Text, BackColor = Theme.Card, Location = new Point(22, 16), AutoSize = true });
            Controls.Add(new Label { Text = message, Font = Theme.Pt(9.5f, false), ForeColor = Theme.Sub, BackColor = Theme.Card, Location = new Point(22, 44), Size = new Size(width - 44, textSize.Height + 4) });
            FlatButton ok = AddButton("OK", false, width - 112, ClientSize.Height - 46, 90);
            ok.Click += delegate { Close(); };
            ResumeLayout(false);
            PlaceBottomRight();
            var t = new System.Windows.Forms.Timer { Interval = autoCloseSeconds * 1000 };
            t.Tick += delegate { t.Stop(); Close(); };
            t.Start();
            FormClosed += delegate { t.Dispose(); };
        }
    }

    // Ring countdown before hibernating. It can be shrunk to a little floating timer.
    class CountdownForm : PopupForm
    {
        readonly Label sub;
        readonly FlatButton now, later, shrink;
        readonly System.Windows.Forms.Timer tick;
        readonly DateTime openedUtc = DateTime.UtcNow;
        DateTime endUtc;
        MiniCountdown mini;
        Status last;

        public CountdownForm()
        {
            Accent = Theme.Red;
            ShowInTaskbar = true;
            SuspendLayout();
            ClientSize = new Size(440, 176);
            Controls.Add(new Label { Text = "Hibernating soon", Font = Theme.Pt(13, true), ForeColor = Theme.Red, BackColor = Theme.Card, Location = new Point(158, 20), AutoSize = true });
            sub = new Label { Font = Theme.Pt(9.5f, false), ForeColor = Theme.Sub, BackColor = Theme.Card, Location = new Point(160, 52), Size = new Size(262, 64) };
            Controls.Add(sub);
            now = AddButton("Hibernate now", false, 160, 124, 130);
            later = AddButton("+2 min", true, 300, 124, 118);
            shrink = new FlatButton("\u2013", false) { Location = new Point(398, 12), Size = new Size(30, 26), BackColor = Theme.Card };
            Controls.Add(shrink);
            new ToolTip().SetToolTip(shrink, "Shrink to a small timer");
            now.Click += delegate { Send("hibernate"); now.Enabled = later.Enabled = false; };
            later.Click += delegate { Send("postpone"); later.Enabled = false; };
            shrink.Click += delegate { Shrink(); };
            ResumeLayout(false);
            PlaceBottomRight();
            tick = new System.Windows.Forms.Timer { Interval = 200 };
            tick.Tick += delegate { if (Visible) Invalidate(new Rectangle(0, 0, (int)(150 * Theme.Scale), Height)); };
            tick.Start();
            FormClosed += delegate
            {
                tick.Dispose();
                if (mini != null) { mini.Close(); mini.Dispose(); }
            };
        }

        public void SetState(Status s)
        {
            last = s;
            endUtc = s.CountdownEndUtc;
            later.Visible = s.PostponeAvailable;
            sub.Text = (s.Emergency ? "The battery is critically low. " : "") +
                "Save your work, or plug in the charger to cancel. Your open apps are kept - it hibernates, it doesn't shut down.";
            if (mini != null) mini.SetState(s);
        }

        void Shrink()
        {
            if (mini == null)
            {
                mini = new MiniCountdown(openedUtc);
                mini.ExpandRequested += delegate { mini.Hide(); Show(); };
            }
            if (last != null) mini.SetState(last);
            Hide();
            mini.Show();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            g.ScaleTransform(Theme.Scale, Theme.Scale);
            double left = Math.Max(0, (endUtc - DateTime.UtcNow).TotalSeconds);
            double total = Math.Max(1, (endUtc - openedUtc).TotalSeconds);
            var ring = new RectangleF(30, 33, 110, 110);
            using (var track = new Pen(Theme.Line, 9)) g.DrawEllipse(track, ring);
            if (left > 0)
                using (var pen = new Pen(Theme.Red, 9) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                    g.DrawArc(pen, ring, -90, (float)(360 * Math.Min(1, left / total)));
            int sec = (int)Math.Ceiling(left);
            string text = string.Format("{0}:{1:00}", sec / 60, sec % 60);
            Font f = Theme.Px(30, true);
            SizeF size = Theme.Measure(g, text, f);
            Theme.Draw(g, text, f, Theme.Text, ring.X + (ring.Width - size.Width) / 2, ring.Y + (ring.Height - size.Height) / 2);
        }

        public static void Send(string command)
        {
            try { Commands.Send(command); }
            catch (Exception ex) { MessageBox.Show("Battery Guard couldn't send that: " + ex.Message, "Battery Guard"); }
        }
    }

    // The countdown as a little floating pill: a ring with a sleepy moon, the time left, a "+2"
    // chip and an expand arrow. Drag it anywhere (it remembers where); double-click or the arrow
    // brings the full window back; right-click for the rest. Drawn with real transparency, so the
    // rounded edges and shadow are smooth.
    class MiniCountdown : Form
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
        [DllImport("user32.dll")] static extern bool ReleaseCapture();
        [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr hwnd, int msg, IntPtr wp, IntPtr lp);

        const float W = 224, H = 64, Pad = 12;   // the pill, and room around it for the shadow
        public event EventHandler ExpandRequested;
        readonly System.Windows.Forms.Timer tick;
        readonly DateTime openedUtc;
        readonly float scale = Theme.Scale;
        DateTime endUtc;
        bool postpone, emergency, postponeSent;
        int hover; // 0 nothing, 1 the +2 chip, 2 the expand arrow

        static readonly Color Moon = Color.FromArgb(255, 196, 70), Coral = Color.FromArgb(255, 99, 99);

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= 0x80000 /* LAYERED */ | 0x8 /* TOPMOST */ | 0x80 /* TOOLWINDOW */ | 0x08000000 /* NOACTIVATE */;
                return cp;
            }
        }

        protected override bool ShowWithoutActivation { get { return true; } }

        public MiniCountdown(DateTime opened)
        {
            openedUtc = opened;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            Text = "Battery Guard countdown";
            Size = new Size((int)((W + 2 * Pad) * scale), (int)((H + 2 * Pad) * scale));
            Rectangle wa = PopupForm.FreeArea();
            Point saved = UserPrefs.MiniPosition;
            Location = saved.X >= 0 && Screen.FromPoint(saved).WorkingArea.Contains(new Rectangle(saved, Size))
                ? saved : new Point(wa.Right - Width - 8, wa.Bottom - Height - 8);
            tick = new System.Windows.Forms.Timer { Interval = 100 };
            tick.Tick += delegate { if (Visible) Render(); };
            tick.Start();
            var menu = new ContextMenuStrip();
            menu.Items.Add("Show the full window", null, delegate { Expand(); });
            menu.Items.Add("+2 min", null, delegate { Postpone(); });
            menu.Items.Add("Hibernate now", null, delegate { CountdownForm.Send("hibernate"); });
            menu.Opening += delegate { menu.Items[1].Enabled = postpone && !postponeSent; };
            ContextMenuStrip = menu;
            FormClosed += delegate { tick.Dispose(); };
        }

        public void SetState(Status s)
        {
            endUtc = s.CountdownEndUtc;
            postpone = s.PostponeAvailable;
            emergency = s.Emergency;
            if (!postpone) postponeSent = false;
            if (Visible) Render();
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            Render();
        }

        protected override void OnVisibleChanged(EventArgs e)
        {
            base.OnVisibleChanged(e);
            if (Visible) Render();
        }

        void Expand()
        {
            if (ExpandRequested != null) ExpandRequested(this, EventArgs.Empty);
        }

        void Postpone()
        {
            if (!postpone || postponeSent) return;
            postponeSent = true;
            CountdownForm.Send("postpone");
            Render();
        }

        // Where things are, in the pill's own units.
        RectangleF Chip { get { return new RectangleF(Pad + W - 76, Pad + 18, 40, 28); } }
        RectangleF Arrow { get { return new RectangleF(Pad + W - 30, Pad + 21, 22, 22); } }

        int HitTest(Point p)
        {
            var q = new PointF(p.X / scale, p.Y / scale);
            if (postpone && !postponeSent && Chip.Contains(q)) return 1;
            if (Arrow.Contains(q)) return 2;
            return 0;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            int h = HitTest(e.Location);
            Cursor = h != 0 ? Cursors.Hand : Cursors.SizeAll;
            if (h != hover) { hover = h; Render(); }
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (hover != 0) { hover = 0; Render(); }
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left) return;
            int h = HitTest(e.Location);
            if (h == 1) { Postpone(); return; }
            if (h == 2) { Expand(); return; }
            if (e.Clicks >= 2) { Expand(); return; }
            // Anywhere else: drag it around (Windows moves it, like a title bar).
            ReleaseCapture();
            SendMessage(Handle, 0xA1 /* WM_NCLBUTTONDOWN */, new IntPtr(2) /* HTCAPTION */, IntPtr.Zero);
            UserPrefs.MiniPosition = Location;
            Render();
        }

        void Render()
        {
            if (!IsHandleCreated || Width <= 0) return;
            using (Bitmap bmp = Snapshot()) Push(bmp);
        }

        // The pill as a picture with transparency (also used to check its look).
        public Bitmap Snapshot()
        {
            var bmp = new Bitmap(Width, Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.Transparent);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.TextRenderingHint = TextRenderingHint.AntiAlias;
                g.ScaleTransform(scale, scale);
                Draw(g);
            }
            return bmp;
        }

        void Draw(Graphics g)
        {
            var pill = new RectangleF(Pad, Pad, W, H);
            // Soft shadow.
            for (int i = 6; i >= 1; i--)
                using (GraphicsPath p = Theme.Rounded(new RectangleF(pill.X - i, pill.Y - i + 3, pill.Width + 2 * i, pill.Height + 2 * i), H / 2 + i))
                using (var b = new SolidBrush(Color.FromArgb(Theme.Dark ? 16 : 9, 0, 0, 0)))
                    g.FillPath(b, p);
            Color top = Theme.Dark ? Color.FromArgb(52, 52, 58) : Color.White, bottom = Theme.Dark ? Color.FromArgb(38, 38, 42) : Color.FromArgb(246, 247, 250);
            using (GraphicsPath p = Theme.Rounded(pill, H / 2))
            using (var fill = new LinearGradientBrush(new RectangleF(pill.X, pill.Y - 1, pill.Width, pill.Height + 2), top, bottom, 90f))
            using (var border = new Pen(Theme.Dark ? Color.FromArgb(70, 70, 78) : Color.FromArgb(226, 228, 234)))
            {
                g.FillPath(fill, p);
                g.DrawPath(border, p);
            }

            double left = Math.Max(0, (endUtc - DateTime.UtcNow).TotalSeconds);
            double total = Math.Max(1, (endUtc - openedUtc).TotalSeconds);
            // The ring, pulsing gently in the last ten seconds.
            var ring = new RectangleF(pill.X + 10, pill.Y + 10, H - 20, H - 20);
            int pulse = left < 10 ? (int)(70 + 70 * Math.Sin(Environment.TickCount / 160.0)) : 0;
            using (var track = new Pen(Theme.Dark ? Color.FromArgb(70, 70, 78) : Color.FromArgb(232, 233, 238), 5)) g.DrawEllipse(track, ring);
            if (left > 0)
                using (var arc = new Pen(Color.FromArgb(255 - pulse, emergency ? Theme.Red : Coral), 5) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                    g.DrawArc(arc, ring, -90, (float)(360 * Math.Min(1, left / total)));
            // A sleepy little moon, with a "z".
            var moon = new RectangleF(ring.X + 12, ring.Y + 12, ring.Width - 24, ring.Height - 24);
            using (var crescent = new GraphicsPath())
            using (var bitePath = new GraphicsPath())
            {
                crescent.AddEllipse(moon);
                bitePath.AddEllipse(moon.X + moon.Width * 0.38f, moon.Y - moon.Height * 0.18f, moon.Width, moon.Height);
                using (var bite = new Region(bitePath))
                using (var shape = new Region(crescent))
                {
                    shape.Exclude(bite);
                    using (var b = new SolidBrush(Moon)) g.FillRegion(b, shape);
                }
            }
            Theme.Draw(g, "z", Theme.Px(8.5f, true), Theme.Dark ? Color.FromArgb(190, 190, 200) : Color.FromArgb(150, 150, 165), moon.Right - 3, moon.Y - 5);

            int sec = (int)Math.Ceiling(left);
            Theme.Draw(g, string.Format("{0}:{1:00}", sec / 60, sec % 60), Theme.Px(23, true), Theme.Text, pill.X + 64, pill.Y + 9);
            Theme.Draw(g, emergency ? "battery critical" : "until it hibernates", Theme.Px(10, false), Theme.Sub, pill.X + 66, pill.Y + 40);

            if (postpone)
            {
                RectangleF chip = Chip;
                Color c = postponeSent ? Theme.Gray : hover == 1 ? Theme.Mix(Theme.Blue, Color.White, 0.15) : Theme.Blue;
                using (GraphicsPath p = Theme.Rounded(chip, chip.Height / 2))
                using (var b = new SolidBrush(c)) g.FillPath(b, p);
                Font cf = Theme.Px(12.5f, true);
                string label = postponeSent ? "\u2713" : "+2";
                SizeF ts = Theme.Measure(g, label, cf);
                Theme.Draw(g, label, cf, Color.White, chip.X + (chip.Width - ts.Width) / 2, chip.Y + (chip.Height - ts.Height) / 2 - 0.5f);
            }
            // Expand arrow: a small chevron pointing up, in a circle on hover.
            RectangleF a = Arrow;
            if (hover == 2)
                using (var b = new SolidBrush(Theme.Dark ? Color.FromArgb(70, 70, 80) : Color.FromArgb(234, 236, 242))) g.FillEllipse(b, a);
            using (var pen = new Pen(Theme.Sub, 2f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
                g.DrawLines(pen, new[] { new PointF(a.X + 6.5f, a.Y + 13.5f), new PointF(a.X + 11, a.Y + 8.5f), new PointF(a.X + 15.5f, a.Y + 13.5f) });
        }

        // Hands the picture, with its transparency, to Windows.
        void Push(Bitmap bmp)
        {
            IntPtr screen = GetDC(IntPtr.Zero), mem = CreateCompatibleDC(screen), hbmp = bmp.GetHbitmap(Color.FromArgb(0)), old = SelectObject(mem, hbmp);
            try
            {
                var size = new SIZE { W = bmp.Width, H = bmp.Height };
                var src = new POINT();
                var dst = new POINT { X = Left, Y = Top };
                var blend = new BLENDFUNCTION { Op = 0, Flags = 0, Alpha = 255, Format = 1 /* AC_SRC_ALPHA */ };
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
    }
    // Every reading the charts need, kept compact, plus what happened when (from the event column).
    class ChartData
    {
        public const double GapMinutes = 5; // longer than this between readings: asleep or off
        public static readonly long GapTicks = (long)(GapMinutes * TimeSpan.TicksPerMinute);

        public long[] T = new long[0];       // UTC ticks
        public float[] Pct = new float[0];   // real level
        public sbyte[] Win = new sbyte[0];   // what Windows said, -1 unknown
        public short[] Mv = new short[0];    // pack millivolts, 0 unknown
        public byte[] State = new byte[0];   // 0 on battery, 1 charging, 2 plugged in not charging, 3 full on charger
        public int Count;
        public readonly List<Mark> Marks = new List<Mark>();

        // Kind: 0 plugged in, 1 unplugged, 2 reached full, 3 stopped early, 4 hibernated, 5 low-battery warning
        public struct Mark { public long T; public byte Kind; public string Text; }

        struct Row { public long T; public float Pct; public sbyte Win; public short Mv; public byte State; }

        // tailBytes > 0: only the end of the current file (enough for a few hours).
        public static ChartData Load(DateTime sinceUtc, long tailBytes)
        {
            var rows = new List<Row>();
            var data = new ChartData();
            if (tailBytes == 0)
            {
                try
                {
                    var older = new List<string>(Directory.GetFiles(Paths.Data, "battery-log-*.csv"));
                    if (Directory.Exists(Archive.Dir)) older.AddRange(Directory.GetFiles(Archive.Dir, "battery-log-*.csv.gz"));
                    foreach (string file in older)
                        if (File.GetLastWriteTimeUtc(file) >= sinceUtc) Read(file, sinceUtc.Ticks, 0, rows, data.Marks);
                }
                catch { }
            }
            Read(Paths.History, sinceUtc.Ticks, tailBytes, rows, data.Marks);
            rows.Sort(delegate(Row a, Row b) { return a.T.CompareTo(b.T); });
            data.Marks.Sort(delegate(Mark a, Mark b) { return a.T.CompareTo(b.T); });
            int n = rows.Count;
            data.T = new long[n]; data.Pct = new float[n]; data.Win = new sbyte[n]; data.Mv = new short[n]; data.State = new byte[n];
            for (int i = 0; i < n; i++)
            {
                data.T[i] = rows[i].T; data.Pct[i] = rows[i].Pct; data.Win[i] = rows[i].Win; data.Mv[i] = rows[i].Mv; data.State[i] = rows[i].State;
            }
            data.Count = n;
            return data;
        }

        static void Read(string path, long since, long tailBytes, List<Row> into, List<Mark> marks)
        {
            try
            {
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    bool zipped = path.EndsWith(".gz", StringComparison.OrdinalIgnoreCase);
                    using (var reader = new StreamReader(zipped ? (Stream)new GZipStream(fs, CompressionMode.Decompress) : fs))
                    {
                        string[] h = (reader.ReadLine() ?? "").Split(',');
                        int iTime = Array.IndexOf(h, "time_utc"), iAc = Array.IndexOf(h, "on_ac"), iChg = Array.IndexOf(h, "charging"),
                            iPct = Array.IndexOf(h, "pct"), iState = Array.IndexOf(h, "charge_state"), iWin = Array.IndexOf(h, "windows_pct"),
                            iMv = Array.IndexOf(h, "pack_mv"), iEvent = Array.IndexOf(h, "event");
                        if (iTime < 0 || iPct < 0) return;
                        if (!zipped && tailBytes > 0 && fs.Length > tailBytes)
                        {
                            fs.Seek(-tailBytes, SeekOrigin.End);
                            reader.DiscardBufferedData();
                            reader.ReadLine(); // the rest of a cut-off line
                        }
                        int needed = Math.Max(iTime, iPct) + 1;
                        string line;
                        while ((line = reader.ReadLine()) != null)
                        {
                            string[] f = line.Split(',');
                            DateTime t;
                            float pct;
                            if (f.Length < needed || !DateTime.TryParseExact(f[iTime], "yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture,
                                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out t) || t.Ticks < since) continue;
                            if (iEvent >= 0 && iEvent < f.Length && f[iEvent].Length > 0) AddMarks(t.Ticks, f[iEvent], marks);
                            if (!float.TryParse(f[iPct], NumberStyles.Float, CultureInfo.InvariantCulture, out pct) || pct < 0) continue;
                            byte state;
                            if (iState >= 0 && iState < f.Length)
                                state = f[iState] == "Discharging" ? (byte)0 : f[iState] == "Charging" ? (byte)1 : f[iState] == "Full" ? (byte)3 : (byte)2;
                            else if (iAc >= 0 && f[iAc] == "1") state = iChg >= 0 && f[iChg] == "1" ? (byte)1 : pct >= 97 ? (byte)3 : (byte)2;
                            else state = 0;
                            int win, mv;
                            if (iWin < 0 || iWin >= f.Length || !int.TryParse(f[iWin], out win) || win > 100) win = -1;
                            if (iMv < 0 || iMv >= f.Length || !int.TryParse(f[iMv], out mv) || mv > 30000) mv = 0;
                            into.Add(new Row { T = t.Ticks, Pct = pct, Win = (sbyte)win, Mv = (short)mv, State = state });
                        }
                    }
                }
            }
            catch { }
        }

        static void AddMarks(long t, string events, List<Mark> marks)
        {
            foreach (string part in events.Split(new[] { " | " }, StringSplitOptions.RemoveEmptyEntries))
            {
                byte kind;
                string text;
                if (part.StartsWith("Charger connected")) { kind = 0; text = "Charger plugged in"; }
                else if (part.StartsWith("Charger disconnected")) { kind = 1; text = "Charger unplugged"; }
                else if (part.StartsWith("Charged all the way to full") || part.StartsWith("Gauge: Charging finished") || (part.StartsWith("Charge: ") && part.EndsWith(" full")))
                { kind = 2; text = "Reached full"; }
                else if (part.Contains("stopped early") || part.StartsWith("Stopped at about"))
                {
                    kind = 3;
                    Match m = Regex.Match(part, @"at about (\d+)%");
                    text = "Charging stopped early" + (m.Success ? " at " + m.Groups[1].Value + "%" : "");
                }
                else if (part.StartsWith("Hibernating")) { kind = 4; text = "Hibernated at the safety limit"; }
                else if (part.StartsWith("Warning:")) { kind = 5; text = "Low-battery warning"; }
                else continue;
                // The same thing is often logged twice within a minute or two: keep one.
                bool repeat = false;
                for (int i = marks.Count - 1; i >= 0 && Math.Abs(t - marks[i].T) < TimeSpan.TicksPerMinute * 3; i--)
                    if (marks[i].Kind == kind) { repeat = true; break; }
                if (!repeat) marks.Add(new Mark { T = t, Kind = kind, Text = text });
            }
        }

        // First reading at or after t.
        public int Index(long t)
        {
            int lo = 0, hi = Count;
            while (lo < hi)
            {
                int mid = (lo + hi) / 2;
                if (T[mid] < t) lo = mid + 1; else hi = mid;
            }
            return lo;
        }

        public struct Totals
        {
            public double Battery, Charging, NotCharging, Full, Off; // minutes
            public float Lowest;
            public int Plugs, Fulls, Early;
        }

        public Totals Sum(long from, long to)
        {
            var s = new Totals { Lowest = -1 };
            for (int i = Math.Max(0, Index(from) - 1); i < Count - 1 && T[i] < to; i++)
            {
                long a = Math.Max(T[i], from), b = Math.Min(T[i + 1], to);
                if (b <= a) continue;
                double minutes = (b - a) / (double)TimeSpan.TicksPerMinute;
                if (T[i + 1] - T[i] > GapTicks) s.Off += minutes;
                else if (State[i] == 0) s.Battery += minutes;
                else if (State[i] == 1) s.Charging += minutes;
                else if (State[i] == 2) s.NotCharging += minutes;
                else s.Full += minutes;
                if (T[i] >= from && (s.Lowest < 0 || Pct[i] < s.Lowest)) s.Lowest = Pct[i];
            }
            foreach (Mark m in Marks)
            {
                if (m.T < from || m.T > to) continue;
                if (m.Kind == 0) s.Plugs++;
                else if (m.Kind == 2) s.Fulls++;
                else if (m.Kind == 3) s.Early++;
            }
            return s;
        }
    }

    // The battery level over time. Hover anywhere for the details at that moment. In the History
    // window: scroll to zoom, drag to move, double-click to go back. On the dashboard (Compact):
    // hover for details, click to open History.
    class TimelineChart : Control
    {
        public ChartData Data = new ChartData();
        public bool Compact, ShowWindows;
        public string Title = "";
        public event EventHandler ViewChanged;

        long viewFrom, viewTo, homeFrom, homeTo, markFrom, markTo; // UTC ticks; mark = highlighted span
        int hoverX = -1, hoverY = -1, dragX;
        bool dragging;
        long dragFrom, dragTo;
        RectangleF plot;    // design units, set while painting
        float lane;         // y of the event markers

        public TimelineChart()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
            BackColor = Theme.Bg;
        }

        public long ViewFrom { get { return viewFrom; } }
        public long ViewTo { get { return viewTo; } }

        public void SetRange(DateTime fromUtc, DateTime toUtc)
        {
            homeFrom = fromUtc.Ticks;
            homeTo = toUtc.Ticks;
            markFrom = markTo = 0;
            Show(homeFrom, homeTo);
        }

        // Zooms to [from, to] and highlights [markStart, markEnd].
        public void ShowSpan(DateTime fromUtc, DateTime toUtc, DateTime markStartUtc, DateTime markEndUtc)
        {
            markFrom = markStartUtc.Ticks;
            markTo = markEndUtc.Ticks;
            Show(fromUtc.Ticks, toUtc.Ticks);
        }

        void Show(long from, long to)
        {
            long latest = DateTime.UtcNow.Ticks + TimeSpan.TicksPerMinute * 2;
            long earliest = Math.Min(homeFrom, Data.Count > 0 ? Data.T[0] : homeFrom);
            long minSpan = TimeSpan.TicksPerMinute * 10, maxSpan = Math.Max(latest - earliest, homeTo - homeFrom);
            if (to - from < minSpan) { long mid = (from + to) / 2; from = mid - minSpan / 2; to = mid + minSpan / 2; }
            if (to - from > maxSpan) { from = latest - maxSpan; to = latest; }
            if (to > latest) { from -= to - latest; to = latest; }
            if (from < latest - maxSpan) { to += latest - maxSpan - from; from = latest - maxSpan; }
            viewFrom = from;
            viewTo = to;
            Invalidate();
            if (ViewChanged != null) ViewChanged(this, EventArgs.Empty);
        }

        long TimeAt(int px)
        {
            return viewFrom + (long)((px / Theme.Scale - plot.X) / plot.Width * (viewTo - viewFrom));
        }

        float X(long t) { return plot.X + (float)((t - viewFrom) / (double)(viewTo - viewFrom)) * plot.Width; }
        float Y(double pct) { return plot.Bottom - (float)(Math.Max(0, Math.Min(100, pct)) / 100) * plot.Height; }

        protected override void OnMouseEnter(EventArgs e) { if (!Compact) Focus(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hoverX = -1; Invalidate(); base.OnMouseLeave(e); }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (!Compact && e.Button == MouseButtons.Left)
            {
                dragging = true;
                dragX = e.X;
                dragFrom = viewFrom;
                dragTo = viewTo;
            }
            base.OnMouseDown(e);
        }

        protected override void OnMouseUp(MouseEventArgs e) { dragging = false; Cursor = Compact ? Cursors.Hand : Cursors.Default; base.OnMouseUp(e); }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            if (dragging && Math.Abs(e.X - dragX) > 2)
            {
                Cursor = Cursors.SizeWE;
                long shift = (long)((dragX - e.X) / (plot.Width * Theme.Scale) * (dragTo - dragFrom));
                Show(dragFrom + shift, dragTo + shift);
            }
            hoverX = e.X;
            hoverY = e.Y;
            if (!dragging) Cursor = Compact ? Cursors.Hand : Cursors.Default;
            Invalidate();
            base.OnMouseMove(e);
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            if (Compact || Data.Count == 0) return;
            double f = e.Delta > 0 ? 0.7 : 1 / 0.7;
            long at = Math.Max(viewFrom, Math.Min(viewTo, TimeAt(e.X)));
            Show(at - (long)((at - viewFrom) * f), at + (long)((viewTo - at) * f));
        }

        protected override void OnDoubleClick(EventArgs e)
        {
            if (!Compact)
            {
                markFrom = markTo = 0;
                Show(homeFrom, homeTo);
            }
            base.OnDoubleClick(e);
        }

        static Color ColorOf(byte state) { return state == 0 ? Theme.Green : state == 2 ? Theme.Orange : Theme.Blue; }

        static string StateText(byte state)
        {
            return state == 0 ? "On battery" : state == 1 ? "Charging" : state == 2 ? "Plugged in, not charging" : "Full, on the charger";
        }

        static Color MarkColor(byte kind)
        {
            return kind == 0 ? Theme.Blue : kind == 1 ? Theme.Gray : kind == 2 ? Theme.Green : kind == 4 ? Theme.Red : Theme.Orange;
        }

        public static string Duration(double minutes)
        {
            int m = (int)Math.Round(minutes);
            if (m >= 24 * 60) return string.Format("{0}d {1}h", m / 1440, m % 1440 / 60);
            if (m >= 60) return string.Format("{0}h {1:00}m", m / 60, m % 60);
            return Math.Max(1, m) + " min";
        }

        string RangeText()
        {
            DateTime a = new DateTime(viewFrom, DateTimeKind.Utc).ToLocalTime(), b = new DateTime(viewTo, DateTimeKind.Utc).ToLocalTime();
            bool toNow = (DateTime.UtcNow.Ticks - viewTo) < TimeSpan.TicksPerMinute * 3;
            string end = toNow ? "now" : a.Date == b.Date ? b.ToString("HH:mm", CultureInfo.InvariantCulture) : b.ToString("ddd d MMM, HH:mm", CultureInfo.InvariantCulture);
            return a.ToString("ddd d MMM, HH:mm", CultureInfo.InvariantCulture) + "  \u2013  " + end;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(BackColor);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            g.ScaleTransform(Theme.Scale, Theme.Scale);
            float w = ClientSize.Width / Theme.Scale - 1, h = ClientSize.Height / Theme.Scale - 1;
            Theme.FillCard(g, new RectangleF(0, 0, w, h));
            if (viewTo <= viewFrom) return;
            Font small = Theme.Px(11, false), label = Theme.Px(11, true);

            // Heading and legend
            if (Compact)
            {
                Theme.Draw(g, Title, Theme.Px(14, true), Theme.Text, 18, 14);
                Theme.Draw(g, "click for full history \u203a", small, Theme.Sub, 18 + Theme.Measure(g, Title, Theme.Px(14, true)).Width + 10, 17);
                plot = new RectangleF(46, 46, w - 46 - 18, h - 46 - 26);
                lane = plot.Y - 8;
            }
            else
            {
                Theme.Draw(g, RangeText(), Theme.Px(14, true), Theme.Text, 18, 14);
                string hint = "Scroll to zoom  \u00b7  drag to move  \u00b7  double-click to go back";
                Theme.Draw(g, hint, small, Theme.Sub, w - 18 - Theme.Measure(g, hint, small).Width, 17);
                DrawLegend(g, 18, 42);
                plot = new RectangleF(50, 96, w - 50 - 18, h - 96 - 54);
                lane = plot.Y - 16;
            }

            // Grid: level lines and time ticks
            using (var grid = new Pen(Theme.Line) { DashStyle = DashStyle.Dash })
            {
                int[] levels = Compact ? new[] { 0, 50, 100 } : new[] { 0, 25, 50, 75, 100 };
                foreach (int p in levels)
                {
                    g.DrawLine(grid, plot.X, Y(p), plot.Right, Y(p));
                    string s = p + "%";
                    Theme.Draw(g, s, small, Theme.Sub, plot.X - 8 - Theme.Measure(g, s, small).Width, Y(p) - 7);
                }
                DrawTimeAxis(g, grid, small, label);
            }

            if (markTo > markFrom)
                using (var b = new SolidBrush(Theme.Alpha(Theme.Blue, 28)))
                {
                    float x1 = Math.Max(plot.X, X(markFrom)), x2 = Math.Min(plot.Right, X(markTo));
                    if (x2 > x1) g.FillRectangle(b, x1, plot.Y, x2 - x1, plot.Height);
                }

            if (Data.Count < 2 || Data.Index(viewTo) == 0 || Data.T[Data.Count - 1] < viewFrom)
            {
                Theme.Draw(g, "Nothing recorded in this period", Theme.Px(12, false), Theme.Sub, plot.X + plot.Width / 2 - 90, plot.Y + plot.Height / 2 - 8);
                return;
            }

            int i0 = Math.Max(0, Data.Index(viewFrom) - 1), i1 = Math.Min(Data.Count - 1, Data.Index(viewTo));
            g.SetClip(new RectangleF(plot.X, plot.Y - 2, plot.Width, plot.Height + 4));
            DrawGaps(g, i0, i1, small);
            DrawSeries(g, i0, i1);
            if (ShowWindows) DrawWindows(g, i0, i1);
            g.ResetClip();
            DrawMarks(g);
            DrawHover(g, w, h);
        }

        void DrawLegend(Graphics g, float x, float y)
        {
            Font small = Theme.Px(11, false);
            Action<string> text = delegate(string s) { Theme.Draw(g, s, small, Theme.Sub, x, y); x += Theme.Measure(g, s, small).Width + 18; };
            foreach (var item in new[] { new KeyValuePair<string, Color>("On battery", Theme.Green), new KeyValuePair<string, Color>("Charging / full", Theme.Blue),
                new KeyValuePair<string, Color>("Plugged in, not charging", Theme.Orange) })
            {
                using (var b = new SolidBrush(item.Value)) g.FillRectangle(b, x, y + 5, 14, 4);
                x += 20;
                text(item.Key);
            }
            using (var hatch = new HatchBrush(HatchStyle.BackwardDiagonal, Theme.Alpha(Theme.Sub, 110), Color.Transparent))
                g.FillRectangle(hatch, x, y + 1, 14, 12);
            x += 20;
            text("Asleep or off (no readings)");
            if (ShowWindows)
            {
                using (var pen = new Pen(Theme.Sub, 1.5f) { DashStyle = DashStyle.Dash }) g.DrawLine(pen, x, y + 7, x + 16, y + 7);
                x += 22;
                text("What Windows said");
            }

            // Marker key, on its own line under the chart
            float mx = 18, my = ClientSize.Height / Theme.Scale - 26;
            foreach (byte kind in new byte[] { 0, 1, 2, 3, 4 })
            {
                string s = kind == 0 ? "Plugged in" : kind == 1 ? "Unplugged" : kind == 2 ? "Reached full" : kind == 3 ? "Stopped early" : "Hibernated";
                DrawMarker(g, mx + 6, my + 7, kind, 6);
                Theme.Draw(g, s, small, Theme.Sub, mx + 16, my);
                mx += 16 + Theme.Measure(g, s, small).Width + 18;
            }
        }

        void DrawTimeAxis(Graphics g, Pen grid, Font small, Font bold)
        {
            long span = viewTo - viewFrom;
            long[] minutes = { 5, 10, 15, 30, 60, 120, 180, 360, 720, 1440, 2880, 10080, 20160, 43200 };
            int most = Math.Max(2, (int)(plot.Width / (Compact ? 70 : 90)));
            long step = minutes[minutes.Length - 1] * TimeSpan.TicksPerMinute;
            foreach (long m in minutes) if (span / (m * TimeSpan.TicksPerMinute) <= most) { step = m * TimeSpan.TicksPerMinute; break; }

            // Ticks fall on round local times: every hour, every 6 hours, every midnight...
            DateTime start = new DateTime(viewFrom, DateTimeKind.Utc).ToLocalTime(), end = new DateTime(viewTo, DateTimeKind.Utc).ToLocalTime();
            DateTime t = step >= TimeSpan.TicksPerDay ? start.Date : start.Date.AddTicks(((start - start.Date).Ticks / step) * step);
            float lastRight = -1000;
            for (; t <= end; t = t.AddTicks(step))
            {
                if (t < start) continue;
                float x = X(t.ToUniversalTime().Ticks);
                bool midnight = t.TimeOfDay == TimeSpan.Zero;
                using (var pen = new Pen(midnight ? Theme.Alpha(Theme.Sub, 90) : Theme.Line) { DashStyle = midnight ? DashStyle.Solid : DashStyle.Dash })
                    g.DrawLine(pen, x, plot.Y, x, plot.Bottom);
                string s = step >= TimeSpan.TicksPerDay ? t.ToString("ddd d MMM", CultureInfo.InvariantCulture)
                    : midnight ? t.ToString("ddd d", CultureInfo.InvariantCulture) : t.ToString("HH:mm", CultureInfo.InvariantCulture);
                Font f = midnight ? bold : small;
                float sw = Theme.Measure(g, s, f).Width, sx = Math.Max(plot.X - 4, Math.Min(plot.Right - sw + 4, x - sw / 2));
                if (sx < lastRight + 6) continue;
                Theme.Draw(g, s, f, midnight ? Theme.Text : Theme.Sub, sx, plot.Bottom + 6);
                lastRight = sx + sw;
            }
        }

        // Asleep or off: no readings for more than a few minutes.
        void DrawGaps(Graphics g, int i0, int i1, Font small)
        {
            using (var hatch = new HatchBrush(HatchStyle.BackwardDiagonal, Theme.Alpha(Theme.Sub, Theme.Dark ? 60 : 70), Color.Transparent))
            using (var bg = new SolidBrush(Theme.Card))
            {
                Action<long, long, string> band = delegate(long a, long b, string s)
                {
                    float x1 = X(a), x2 = X(b);
                    g.FillRectangle(hatch, x1, plot.Y, x2 - x1, plot.Height);
                    float sw = Theme.Measure(g, s, small).Width;
                    float left = Math.Max(x1, plot.X), right = Math.Min(x2, plot.Right), cx = (left + right) / 2;
                    if (right - left > sw + 16)
                    {
                        g.FillRectangle(bg, cx - sw / 2 - 4, plot.Y + plot.Height / 2 - 9, sw + 8, 18);
                        Theme.Draw(g, s, small, Theme.Sub, cx - sw / 2, plot.Y + plot.Height / 2 - 7);
                    }
                };
                for (int i = i0; i < i1; i++)
                    if (Data.T[i + 1] - Data.T[i] > ChartData.GapTicks) band(Data.T[i], Data.T[i + 1], "asleep or off");
                // Nothing loaded before the first reading or after the last one.
                if (i0 == 0 && Data.T[0] - viewFrom > ChartData.GapTicks) band(viewFrom, Data.T[0], "no readings");
                if (i1 == Data.Count - 1 && viewTo - Data.T[i1] > ChartData.GapTicks * 2) band(Data.T[i1], viewTo, "no readings");
            }
        }

        // One filled stretch per run of the same state; asleep/off breaks it. About one point per pixel.
        void DrawSeries(Graphics g, int i0, int i1)
        {
            var pts = new List<PointF>();
            byte state = Data.State[i0];
            float lastX = float.MinValue;
            for (int i = i0; i <= i1; i++)
            {
                bool gap = i > i0 && Data.T[i] - Data.T[i - 1] > ChartData.GapTicks;
                if (i > i0 && (gap || Data.State[i] != state))
                {
                    if (!gap) pts.Add(new PointF(X(Data.T[i]), Y(Data.Pct[i]))); // join to the next run
                    DrawRun(g, pts, ColorOf(state));
                    pts.Clear();
                    if (!gap) pts.Add(new PointF(X(Data.T[i - 1]), Y(Data.Pct[i - 1])));
                    state = Data.State[i];
                    lastX = float.MinValue;
                }
                float x = X(Data.T[i]);
                if (x - lastX >= 1 || i == i1 || (i + 1 <= i1 && Data.State[i + 1] != state))
                {
                    pts.Add(new PointF(x, Y(Data.Pct[i])));
                    lastX = x;
                }
            }
            DrawRun(g, pts, ColorOf(state));
        }

        void DrawRun(Graphics g, List<PointF> line, Color color)
        {
            if (line.Count < 2) return;
            var area = new PointF[line.Count + 2];
            line.CopyTo(area, 0);
            area[line.Count] = new PointF(line[line.Count - 1].X, plot.Bottom);
            area[line.Count + 1] = new PointF(line[0].X, plot.Bottom);
            using (var fill = new LinearGradientBrush(new RectangleF(plot.X, plot.Y - 1, plot.Width, plot.Height + 2), Theme.Alpha(color, 80), Theme.Alpha(color, 0), 90f))
                g.FillPolygon(fill, area);
            using (var pen = new Pen(color, 2f) { LineJoin = LineJoin.Round })
                g.DrawLines(pen, line.ToArray());
        }

        void DrawWindows(Graphics g, int i0, int i1)
        {
            using (var pen = new Pen(Theme.Alpha(Theme.Sub, 200), 1.5f) { DashStyle = DashStyle.Dash })
            {
                var pts = new List<PointF>();
                float lastX = float.MinValue;
                for (int i = i0; i <= i1; i++)
                {
                    bool gap = i > i0 && Data.T[i] - Data.T[i - 1] > ChartData.GapTicks;
                    if (Data.Win[i] < 0 || gap)
                    {
                        if (pts.Count > 1) g.DrawLines(pen, pts.ToArray());
                        pts.Clear();
                        lastX = float.MinValue;
                        if (Data.Win[i] < 0) continue;
                    }
                    float x = X(Data.T[i]);
                    if (x - lastX >= 1 || i == i1) { pts.Add(new PointF(x, Y(Data.Win[i]))); lastX = x; }
                }
                if (pts.Count > 1) g.DrawLines(pen, pts.ToArray());
            }
        }

        static void DrawMarker(Graphics g, float cx, float cy, byte kind, float r)
        {
            using (var b = new SolidBrush(MarkColor(kind))) g.FillEllipse(b, cx - r, cy - r, 2 * r, 2 * r);
            if (r < 5) return;
            using (var pen = new Pen(Color.White, 1.6f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            {
                float a = r * 0.5f;
                if (kind == 0) { g.DrawLine(pen, cx - a, cy, cx + a, cy); g.DrawLine(pen, cx, cy - a, cx, cy + a); }                 // +
                else if (kind == 1) g.DrawLine(pen, cx - a, cy, cx + a, cy);                                                           // -
                else if (kind == 2) { g.DrawLine(pen, cx - a, cy, cx - a * 0.2f, cy + a * 0.7f); g.DrawLine(pen, cx - a * 0.2f, cy + a * 0.7f, cx + a, cy - a * 0.6f); } // tick
                else if (kind == 4) { g.DrawLine(pen, cx - a, cy - a, cx + a, cy - a); g.DrawLine(pen, cx + a, cy - a, cx - a, cy + a); g.DrawLine(pen, cx - a, cy + a, cx + a, cy + a); } // z
                else { g.DrawLine(pen, cx, cy - a, cx, cy + a * 0.15f); g.DrawLine(pen, cx, cy + a * 0.7f, cx, cy + a * 0.75f); }     // !
            }
        }

        void DrawMarks(Graphics g)
        {
            using (var pen = new Pen(Theme.Alpha(Theme.Sub, 70)) { DashStyle = DashStyle.Dot })
            {
                foreach (ChartData.Mark m in Data.Marks)
                {
                    if (m.T < viewFrom || m.T > viewTo || (m.Kind == 5 && Compact)) continue;
                    float x = X(m.T);
                    if (!Compact) g.DrawLine(pen, x, lane + 7, x, plot.Bottom);
                    DrawMarker(g, x, lane, m.Kind, Compact ? 3.5f : 6.5f);
                }
            }
        }

        // The details for the moment under the mouse.
        void DrawHover(Graphics g, float w, float h)
        {
            if (hoverX < 0 || dragging) return;
            float hx = hoverX / Theme.Scale, hy = hoverY / Theme.Scale;
            if (hx < plot.X || hx > plot.Right || hy < lane - 10 || hy > plot.Bottom + 4) return;
            long t = TimeAt(hoverX);
            int k = Data.Index(t);
            var rows = new List<object[]>(); // { label, value, color }
            string head;
            float lineX;

            if ((k == 0 && hx < X(Data.T[0]) - 4) || (k >= Data.Count && hx > X(Data.T[Data.Count - 1]) + 4 && viewTo - Data.T[Data.Count - 1] > ChartData.GapTicks * 2))
            {
                head = "No readings";
                bool before = k == 0;
                DateTime edge = new DateTime(before ? Data.T[0] : Data.T[Data.Count - 1], DateTimeKind.Utc).ToLocalTime();
                rows.Add(new object[] { before ? "Next reading" : "Last reading", edge.ToString("ddd d MMM, HH:mm", CultureInfo.InvariantCulture), Theme.Text });
                rows.Add(new object[] { "", "The laptop was asleep or off, or this", Theme.Sub });
                rows.Add(new object[] { "", "is before Battery Guard was recording.", Theme.Sub });
                lineX = hx;
            }
            else if (k > 0 && k < Data.Count && Data.T[k] - Data.T[k - 1] > ChartData.GapTicks
                && hx > X(Data.T[k - 1]) + 4 && hx < X(Data.T[k]) - 4)
            {
                DateTime a = new DateTime(Data.T[k - 1], DateTimeKind.Utc).ToLocalTime(), b = new DateTime(Data.T[k], DateTimeKind.Utc).ToLocalTime();
                head = "Asleep or off";
                rows.Add(new object[] { "From", a.ToString("ddd d MMM, HH:mm", CultureInfo.InvariantCulture), Theme.Text });
                rows.Add(new object[] { "To", b.ToString("ddd d MMM, HH:mm", CultureInfo.InvariantCulture), Theme.Text });
                rows.Add(new object[] { "For", Duration((b - a).TotalMinutes), Theme.Text });
                rows.Add(new object[] { "Level", string.Format("{0:0}% \u2192 {1:0}%", Data.Pct[k - 1], Data.Pct[k]), Theme.Text });
                lineX = hx;
            }
            else
            {
                if (k >= Data.Count || (k > 0 && t - Data.T[k - 1] < Data.T[k] - t)) k--;
                if (k < 0) return;
                DateTime at = new DateTime(Data.T[k], DateTimeKind.Utc).ToLocalTime();
                head = at.ToString("ddd d MMM, HH:mm", CultureInfo.InvariantCulture);
                rows.Add(new object[] { "Real level", Data.Pct[k].ToString("0", CultureInfo.InvariantCulture) + "%", Theme.Text });
                if (Data.Win[k] >= 0) rows.Add(new object[] { "Windows said", Data.Win[k] + "%", Theme.Sub });
                if (Data.Mv[k] > 0) rows.Add(new object[] { "Voltage", (Data.Mv[k] / 1000.0).ToString("0.00", CultureInfo.InvariantCulture) + " V", Theme.Text });
                rows.Add(new object[] { "", StateText(Data.State[k]), ColorOf(Data.State[k]) });
                lineX = X(Data.T[k]);
                using (var pen = new Pen(Theme.Alpha(Theme.Sub, 140))) g.DrawLine(pen, lineX, plot.Y, lineX, plot.Bottom);
                float y = Y(Data.Pct[k]);
                using (var ring = new SolidBrush(Theme.Card)) g.FillEllipse(ring, lineX - 6, y - 6, 12, 12);
                using (var dot = new SolidBrush(ColorOf(Data.State[k]))) g.FillEllipse(dot, lineX - 4, y - 4, 8, 8);
                if (ShowWindows && Data.Win[k] >= 0)
                    using (var dot = new SolidBrush(Theme.Sub)) g.FillEllipse(dot, lineX - 3, Y(Data.Win[k]) - 3, 6, 6);
            }

            // Events within a few pixels of the mouse
            foreach (ChartData.Mark m in Data.Marks)
            {
                if (m.T < viewFrom || m.T > viewTo || Math.Abs(X(m.T) - hx) > 8) continue;
                rows.Add(new object[] { "", m.Text + ", " + new DateTime(m.T, DateTimeKind.Utc).ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture), MarkColor(m.Kind) });
            }

            // The card
            Font headFont = Theme.Px(12, true), labelFont = Theme.Px(11.5f, false), valueFont = Theme.Px(11.5f, true);
            float labelW = 0, valueW = Theme.Measure(g, head, headFont).Width;
            foreach (object[] r in rows)
            {
                string l = (string)r[0], v = (string)r[1];
                if (l.Length > 0) { labelW = Math.Max(labelW, Theme.Measure(g, l, labelFont).Width); valueW = Math.Max(valueW - 0, Theme.Measure(g, v, valueFont).Width); }
            }
            float boxW = 24 + Math.Max(labelW + 12 + valueW, 0);
            foreach (object[] r in rows) if (((string)r[0]).Length == 0) boxW = Math.Max(boxW, 24 + Theme.Measure(g, (string)r[1], valueFont).Width);
            boxW = Math.Max(boxW, 24 + Theme.Measure(g, head, headFont).Width);
            float boxH = 34 + rows.Count * 19;
            float bx = lineX + 14;
            if (bx + boxW > w - 6) bx = lineX - 14 - boxW;
            float by = Math.Max(4, Math.Min(h - boxH - 4, plot.Y + 4));
            using (GraphicsPath box = Theme.Rounded(new RectangleF(bx, by, boxW, boxH), 9))
            using (var fill = new SolidBrush(Theme.Card))
            using (var border = new Pen(Theme.Alpha(Theme.Sub, 120)))
            {
                g.FillPath(fill, box);
                g.DrawPath(border, box);
            }
            Theme.Draw(g, head, headFont, Theme.Text, bx + 12, by + 9);
            float ry = by + 31;
            foreach (object[] r in rows)
            {
                string l = (string)r[0], v = (string)r[1];
                if (l.Length > 0)
                {
                    Theme.Draw(g, l, labelFont, Theme.Sub, bx + 12, ry);
                    Theme.Draw(g, v, valueFont, (Color)r[2], bx + 12 + labelW + 12, ry);
                }
                else Theme.Draw(g, v, valueFont, (Color)r[2], bx + 12, ry);
                ry += 19;
            }
        }
    }

    // Everything recorded: the battery level over any period, and every charge.
    class HistoryForm : Form
    {
        static readonly string[] Names = { "4 hours", "24 hours", "7 days", "30 days", "Everything" };
        static readonly double[] Hours = { 4, 24, 168, 720, 0 };
        static readonly Regex ChargeLine = new Regex(@"^(\d{4}-\d\d-\d\d \d\d:\d\d:\d\d)  Charge: \w+ (\d\d:\d\d)-\d\d:\d\d \((.+?)\): (\d+)% to (\d+)%, ([\d.]+) Wh in, (.+)$");

        class Charge { public DateTime Start, End; public string Took, From, To, Wh, Result; public double WaitedH = -1; }

        readonly TimelineChart chart = new TimelineChart();
        readonly FlatButton[] ranges = new FlatButton[Names.Length];
        readonly Label stats, summary;
        readonly ListView list;
        readonly List<Charge> charges = new List<Charge>();
        readonly double needGapH;
        readonly bool stopsEarly;

        public HistoryForm()
        {
            Status st = Status.Load();
            needGapH = st != null && st.NeedGapHours > 0 ? st.NeedGapHours : 9.5;
            stopsEarly = st != null && st.StopsEarly;
            SuspendLayout();
            AutoScaleDimensions = new SizeF(96F, 96F);
            AutoScaleMode = AutoScaleMode.Dpi;
            Text = "Battery Guard - history";
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Theme.Bg;
            Font = Theme.Pt(9.5f, false);
            Icon = Theme.AppIcon;
            ClientSize = new Size(940, 684);

            for (int i = 0; i < Names.Length; i++)
            {
                int index = i;
                ranges[i] = new FlatButton(Names[i], false) { Location = new Point(16 + i * 112, 12), Size = new Size(104, 34) };
                ranges[i].Click += delegate { Select(index); };
                Controls.Add(ranges[i]);
            }
            var windows = new ToggleSwitch { Location = new Point(924 - 42, 17) };
            windows.CheckedChanged += delegate { chart.ShowWindows = windows.Checked; chart.Invalidate(); };
            Controls.Add(windows);
            Controls.Add(new Label { Text = "Show what Windows said", ForeColor = Theme.Sub, AutoSize = true, Location = new Point(724, 20) });

            chart.Location = new Point(16, 56);
            chart.Size = new Size(908, 372);
            chart.ViewChanged += delegate { UpdateStats(); };
            Controls.Add(chart);

            stats = new Label { ForeColor = Theme.Text, Location = new Point(18, 434), Size = new Size(904, 40), Font = Theme.Pt(9.5f, false) };
            Controls.Add(stats);

            Controls.Add(new Label { Text = "Every charge", Font = Theme.Pt(11, true), ForeColor = Theme.Text, Location = new Point(16, 480), AutoSize = true });
            summary = new Label { ForeColor = Theme.Sub, Location = new Point(130, 484), Size = new Size(794, 36) };
            Controls.Add(summary);
            list = new ListView
            {
                Location = new Point(16, 524), Size = new Size(908, 146), View = View.Details, FullRowSelect = true, MultiSelect = false,
                HeaderStyle = ColumnHeaderStyle.Nonclickable, BorderStyle = BorderStyle.None, BackColor = Theme.Card, ForeColor = Theme.Text,
                UseCompatibleStateImageBehavior = false, HideSelection = false,
            };
            list.Columns.Add("Started", 200);
            list.Columns.Add("Waited before", 130);
            list.Columns.Add("Took", 100);
            list.Columns.Add("Battery", 150);
            list.Columns.Add("Energy in", 110);
            list.Columns.Add("Result", 190);
            list.SelectedIndexChanged += delegate { ShowSelectedCharge(); };
            Controls.Add(list);
            ResumeLayout(false);

            Cursor = Cursors.WaitCursor;
            chart.Data = ChartData.Load(DateTime.MinValue, 0); // everything, once; zooming and moving reuse it
            Cursor = Cursors.Default;
            LoadCharges();
            Select(1);
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Theme.DarkTitleBar(this);
        }

        void Select(int index)
        {
            for (int i = 0; i < ranges.Length; i++) ranges[i].Selected = i == index;
            DateTime now = DateTime.UtcNow;
            DateTime from = Hours[index] > 0 ? now.AddHours(-Hours[index])
                : chart.Data.Count > 0 ? new DateTime(chart.Data.T[0], DateTimeKind.Utc) : now.AddHours(-4);
            chart.SetRange(from, now);
        }

        void UpdateStats()
        {
            ChartData.Totals s = chart.Data.Sum(chart.ViewFrom, chart.ViewTo);
            var parts = new List<string>();
            if (s.Battery >= 1) parts.Add("on battery " + TimelineChart.Duration(s.Battery));
            if (s.Charging >= 1) parts.Add("charging " + TimelineChart.Duration(s.Charging));
            if (s.Full >= 1) parts.Add("full on the charger " + TimelineChart.Duration(s.Full));
            if (s.NotCharging >= 1) parts.Add("plugged in but not charging " + TimelineChart.Duration(s.NotCharging));
            if (s.Off >= 1) parts.Add("asleep or off " + TimelineChart.Duration(s.Off));
            string line1 = parts.Count == 0 ? "Nothing recorded in this view." : "In this view: " + string.Join("  \u00b7  ", parts.ToArray()) + ".";
            var more = new List<string>();
            if (s.Lowest >= 0) more.Add("lowest " + s.Lowest.ToString("0", CultureInfo.InvariantCulture) + "%");
            if (s.Plugs > 0) more.Add(string.Format("plugged in {0} time{1}", s.Plugs, s.Plugs == 1 ? "" : "s"));
            if (s.Fulls > 0) more.Add(string.Format("reached full {0} time{1}", s.Fulls, s.Fulls == 1 ? "" : "s"));
            if (s.Early > 0) more.Add(string.Format("stopped early {0} time{1}", s.Early, s.Early == 1 ? "" : "s"));
            stats.Text = line1 + (more.Count > 0 ? "\n" + Capital(string.Join("  \u00b7  ", more.ToArray())) + "." : "");
        }

        static string Capital(string s) { return s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s.Substring(1); }

        // Every "Charge:" line the service has written; newest first, with the wait before each.
        void LoadCharges()
        {
            var lines = new List<string>();
            try
            {
                if (Directory.Exists(Archive.Dir))
                    foreach (string file in Directory.GetFiles(Archive.Dir, "log-*.txt.gz"))
                        using (var reader = new StreamReader(new GZipStream(File.OpenRead(file), CompressionMode.Decompress)))
                            AddChargeLines(reader, lines);
                using (var reader = new StreamReader(new FileStream(Paths.Log, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete)))
                    AddChargeLines(reader, lines);
            }
            catch { }
            lines.Sort(StringComparer.Ordinal);
            foreach (string line in lines)
            {
                Match m = ChargeLine.Match(line);
                DateTime end;
                if (!m.Success || !DateTime.TryParseExact(m.Groups[1].Value, "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out end)) continue;
                DateTime start = DateTime.ParseExact(end.ToString("yyyy-MM-dd ", CultureInfo.InvariantCulture) + m.Groups[2].Value, "yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
                if (start > end) start = start.AddDays(-1);
                var c = new Charge { Start = start, End = end, Took = m.Groups[3].Value, From = m.Groups[4].Value, To = m.Groups[5].Value, Wh = m.Groups[6].Value, Result = m.Groups[7].Value };
                if (charges.Count > 0) c.WaitedH = (start - charges[charges.Count - 1].End).TotalHours;
                charges.Add(c);
            }

            int full = 0, early = 0;
            list.BeginUpdate();
            for (int i = charges.Count - 1; i >= 0; i--)
            {
                Charge c = charges[i];
                bool isFull = c.Result == "full", isEarly = c.Result == "stopped early";
                if (isFull) full++;
                if (isEarly) early++;
                var item = new ListViewItem(c.Start.ToString("ddd d MMM, HH:mm", CultureInfo.InvariantCulture)) { UseItemStyleForSubItems = false, Tag = c };
                string waited = c.WaitedH < 0 ? "\u2013" : c.WaitedH >= 48 ? (c.WaitedH / 24).ToString("0.#", CultureInfo.InvariantCulture) + " days"
                    : c.WaitedH.ToString(c.WaitedH < 10 ? "0.0" : "0", CultureInfo.InvariantCulture) + " h";
                item.SubItems.Add(waited, c.WaitedH < 0 ? Theme.Sub : !stopsEarly ? Theme.Text : c.WaitedH >= needGapH ? Theme.Green : Theme.Orange, Theme.Card, list.Font);
                item.SubItems.Add(c.Took);
                item.SubItems.Add(c.From + "% \u2192 " + c.To + "%");
                item.SubItems.Add(c.Wh + " Wh");
                item.SubItems.Add(isFull ? "Reached full" : isEarly ? "Stopped early" : "Unplugged", isFull ? Theme.Green : isEarly ? Theme.Orange : Theme.Sub, Theme.Card, list.Font);
                list.Items.Add(item);
            }
            list.EndUpdate();
            summary.Text = charges.Count == 0 ? "None recorded yet. Each charge appears here once it ends."
                : string.Format("{0} recorded \u00b7 {1} reached full \u00b7 {2} stopped early \u00b7 click one to see it on the chart\n{3}",
                    charges.Count, full, early, stopsEarly
                        ? string.Format(CultureInfo.InvariantCulture, "\"Waited before\" is the time since the previous charge ended. About {0:0.#} h or more (green) is what a full charge needs.", needGapH)
                        : "\"Waited before\" is the time since the previous charge ended.");
        }

        void ShowSelectedCharge()
        {
            if (list.SelectedItems.Count == 0) return;
            var c = (Charge)list.SelectedItems[0].Tag;
            DateTime a = c.Start.ToUniversalTime(), b = c.End.ToUniversalTime();
            TimeSpan pad = TimeSpan.FromMinutes(Math.Max(30, (b - a).TotalMinutes * 0.6));
            foreach (FlatButton r in ranges) r.Selected = false;
            chart.ShowSpan(a - pad, b + pad, a, b);
        }

        static void AddChargeLines(StreamReader reader, List<string> into)
        {
            string line;
            while ((line = reader.ReadLine()) != null)
                if (line.Length > 30 && line.IndexOf("  Charge: ", 19, StringComparison.Ordinal) == 19) into.Add(line);
        }
    }

    // The dashboard: ring gauge, facts, chart and what's been learned.
    class DashboardView : Control
    {
        readonly TimelineChart chart = new TimelineChart { Compact = true, Title = "Last 4 hours" };
        Status status;
        bool stale = true;
        DateTime chartLoaded;
        public event EventHandler HistoryRequested;

        public DashboardView()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            BackColor = Theme.Bg;
            chart.Click += delegate { if (HistoryRequested != null) HistoryRequested(this, EventArgs.Empty); };
            Controls.Add(chart);
        }

        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e);
            float s = Theme.Scale;
            chart.Bounds = Rectangle.Round(new RectangleF(16 * s, 224 * s, ClientSize.Width - 32 * s, 172 * s));
        }

        public void Show(Status s, bool isStale)
        {
            status = s;
            stale = isStale;
            if ((DateTime.UtcNow - chartLoaded).TotalSeconds > 60)
            {
                chartLoaded = DateTime.UtcNow;
                chart.Data = ChartData.Load(chartLoaded.AddHours(-4.2), 400000);
                chart.SetRange(chartLoaded.AddHours(-4), chartLoaded);
            }
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(BackColor);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            g.ScaleTransform(Theme.Scale, Theme.Scale);
            float w = ClientSize.Width / Theme.Scale, h = ClientSize.Height / Theme.Scale;
            Theme.Look look = Theme.State(status, stale);

            var head = new RectangleF(16, 16, w - 32, 196);
            Theme.FillCard(g, head);
            DrawRing(g, new PointF(head.X + 96, head.Y + 98), 72, look);
            DrawFacts(g, head.X + 196, head.Y + 18, head.Right - 18, look);

            // The chart (a child control) sits at y 224-396.
            var info = new RectangleF(16, 408, w - 32, h - 408);
            Theme.FillCard(g, info);
            DrawSummary(g, info);
        }

        void DrawRing(Graphics g, PointF c, float r, Theme.Look look)
        {
            var rect = new RectangleF(c.X - r, c.Y - r, 2 * r, 2 * r);
            using (var track = new Pen(Theme.Line, 13)) g.DrawEllipse(track, rect);
            double pct = status != null && !stale ? status.Percent : -1;
            if (pct > 0)
                using (var pen = new Pen(look.Color, 13) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                    g.DrawArc(pen, rect, -90, (float)(Math.Min(100, Math.Max(1.5, pct)) * 3.6));

            string number = pct >= 0 ? Math.Round(pct).ToString(CultureInfo.InvariantCulture) : "\u2013";
            Font big = Theme.Px(44, true), unit = Theme.Px(18, true);
            SizeF nSize = Theme.Measure(g, number, big), uSize = pct >= 0 ? Theme.Measure(g, "%", unit) : SizeF.Empty;
            float x = c.X - (nSize.Width + uSize.Width) / 2, y = c.Y - nSize.Height / 2 - 8;
            Theme.Draw(g, number, big, Theme.Text, x, y);
            if (pct >= 0) Theme.Draw(g, "%", unit, Theme.Sub, x + nSize.Width + 1, y + 8);

            Font label = Theme.Px(10.5f, true);
            SizeF lSize = Theme.Measure(g, look.Label, label);
            Theme.Draw(g, look.Label, label, look.Color, c.X - lSize.Width / 2, c.Y + nSize.Height / 2 - 4);
            if (status != null && !stale && status.SigmaPct > 0 && pct >= 0)
            {
                string acc = "\u00b1" + Math.Round(status.SigmaPct).ToString(CultureInfo.InvariantCulture) + "%" + (status.Learn.Length > 0 ? " \u00b7 learning" : "");
                SizeF aSize = Theme.Measure(g, acc, Theme.Px(10.5f, false));
                Theme.Draw(g, acc, Theme.Px(10.5f, false), Theme.Sub, c.X - aSize.Width / 2, c.Y + nSize.Height / 2 + 11);
            }
        }

        void DrawFacts(Graphics g, float x, float y, float right, Theme.Look look)
        {
            // Protection pill.
            string pill;
            Color pillColor;
            if (stale || status == null) { pill = "Service stopped"; pillColor = Theme.Red; }
            else if (status.Protection == "Active" && status.Learn.Length > 0) { pill = "Learning \u00b7 no hibernation"; pillColor = Theme.Orange; }
            else if (status.Protection == "Active") { pill = status.Learn.Length > 0 ? "Protected \u00b7 learning" : "Protected"; pillColor = Theme.Green; }
            else if (status.Protection == "Off") { pill = "Protection off"; pillColor = Theme.Gray; }
            else { pill = "No voltage reading"; pillColor = Theme.Orange; }
            if (status != null && status.DryRun) pill += " \u00b7 test";
            Font pf = Theme.Px(11.5f, true);
            SizeF ps = Theme.Measure(g, pill, pf);
            var pr = new RectangleF(x, y, ps.Width + 30, 24);
            using (GraphicsPath p = Theme.Rounded(pr, 12))
            using (var b = new SolidBrush(Theme.Alpha(pillColor, Theme.Dark ? 45 : 32)))
                g.FillPath(b, p);
            using (var dot = new SolidBrush(pillColor)) g.FillEllipse(dot, pr.X + 10, pr.Y + 9, 7, 7);
            Theme.Draw(g, pill, pf, Theme.Dark ? Theme.Mix(pillColor, Color.White, 0.2) : Theme.Mix(pillColor, Color.Black, 0.25), pr.X + 22, pr.Y + 4);

            if (stale || status == null)
            {
                Fact(g, x, y + 40, right, "STATUS", "Not running", "restarts by itself", Theme.Red);
                return;
            }
            Status s = status;
            if (s.Phase == "Countdown" || s.Phase == "Hibernating") Fact(g, x, y + 40, right, "STATUS", "Hibernating soon", "plug in to cancel", Theme.Red);
            else if (!s.OnAC) Fact(g, x, y + 40, right, "TIME LEFT", Fmt.Minutes(s.MinutesLeft), "at current use", Theme.Text);
            else if (s.ChargeState == "Charging") Fact(g, x, y + 40, right, "CHARGING AT", Fmt.Watts(s.RateMw), s.MinutesToFull >= 0 ? "full in ~" + Fmt.Minutes(s.MinutesToFull) : "", Theme.Blue);
            else if (s.ChargeState == "Full") Fact(g, x, y + 40, right, "STATUS", "Fully charged", "", Theme.Green);
            else Fact(g, x, y + 40, right, "CHARGER", "Not charging", "", Theme.Orange);

            Fact(g, x, y + 88, right, "VOLTAGE", Fmt.Volts(s.VoltageMv), s.PerCellMv > 0 ? Fmt.Volts(s.PerCellMv) + " per cell" : "", Theme.Text);
            bool wrong = s.WindowsPercent >= 0 && s.Percent >= 0 && Math.Abs(s.WindowsPercent - s.Percent) > 10;
            Fact(g, x, y + 136, right, "WINDOWS SAYS", s.WindowsPercent >= 0 ? s.WindowsPercent + "%" : "\u2013", wrong ? "its gauge is wrong" : "", Theme.Text);
        }

        static void Fact(Graphics g, float x, float y, float right, string label, string value, string extra, Color color)
        {
            Theme.Draw(g, label, Theme.Px(10.5f, true), Theme.Sub, x, y);
            Font vf = Theme.Px(20, true);
            Theme.Draw(g, value, vf, color, x, y + 14);
            float ex = x + Theme.Measure(g, value, vf).Width + 8;
            if (extra.Length > 0 && ex + Theme.Measure(g, extra, Theme.Px(11.5f, false)).Width <= right)
                Theme.Draw(g, extra, Theme.Px(11.5f, false), Theme.Sub, ex, y + 22);
        }

        void DrawSummary(Graphics g, RectangleF r)
        {
            Theme.Draw(g, "Summary", Theme.Px(14, true), Theme.Text, r.X + 18, r.Y + 14);
            var text = new RectangleF(r.X + 18, r.Y + 40, r.Width - 36, r.Height - 48);
            if (stale || status == null)
            {
                using (var b = new SolidBrush(Theme.Sub))
                    g.DrawString("The Battery Guard service isn't running. Windows restarts it by itself within a minute; if this stays, restart the PC or run the Battery Guard setup again.", Theme.Px(12, false), b, text);
                return;
            }
            Status s = status;

            // What to do right now.
            string advice;
            Color color;
            string wait = "a full charge is possible after " + Fmt.When(s.ReadyAtUtc);
            if (s.Phase == "Countdown" || s.Phase == "Hibernating") { advice = "Plug in now to stop it hibernating."; color = Theme.Red; }
            else if (s.Learn.Length > 0 && !s.OnAC)
            {
                advice = s.Phase == "Warning" ? "Learning: the battery is nearly empty and the laptop will switch itself off soon. Save your work now."
                    : "Learning: it won't hibernate until this battery has run all the way down once. Save your work often.";
                color = s.Phase == "Warning" ? Theme.Red : Theme.Orange;
            }
            else if (s.Learn.Length > 0) { advice = "Learning: use and charge it as usual. The first time the battery runs all the way down, the laptop will switch itself off."; color = Theme.Blue; }
            else if (!s.OnAC)
            {
                if (s.Phase == "Warning") { advice = !s.StopsEarly || s.ReadyForFull ? "Battery low - plug in now." : "Plug in now. This charge will only add about 20% (" + wait + ")."; color = Theme.Orange; }
                else if (s.Cycle == "ready") { advice = "Plug in whenever you like: the charge should go all the way to full."; color = Theme.Green; }
                else if (s.Cycle == "drain") { advice = "About " + Fmt.Minutes(s.CycleEtaMin) + " more on battery, and a plug-in will charge to full."; color = Theme.Blue; }
                else if (s.Percent >= 85) { advice = "On battery. Plugging in now just tops it up."; color = Theme.Sub; }
                else if (!s.StopsEarly) { advice = "On battery. Battery Guard will warn you well before it runs out."; color = Theme.Sub; }
                else { advice = "Plugging in now would only add about 20% and restart the wait: " + wait + "."; color = Theme.Sub; }
            }
            else if (s.ChargeState == "Charging")
            {
                if (s.ExpectFull) { advice = "Charging to full - leave it plugged in until it says Full."; color = Theme.Green; }
                else if (s.Limited) { advice = string.Format("This charge will stop early, around {0:0}%: it began too soon after the last one.", s.StopPct); color = Theme.Orange; }
                else { advice = "Topping up - should reach full."; color = Theme.Green; }
            }
            else if (s.ChargeState == "Full") { advice = "Fully charged - fine to unplug."; color = Theme.Green; }
            else if (s.Cycle == "wait") { advice = "Charging has stopped, and " + wait + ". Leave it as it is: plugging in again sooner only adds about 20% and restarts the wait."; color = Theme.Orange; }
            else if (s.Cycle == "unplug") { advice = "Enough time has passed for a full charge. Unplug, use it on battery for about " + Fmt.Minutes(s.CycleEtaMin) + ", then plug back in."; color = Theme.Blue; }
            else if (s.Percent >= 0 && s.Percent < 90) { advice = "Plugged in, but the charger isn't charging right now."; color = Theme.Orange; }
            else { advice = "Not charging - close to full."; color = Theme.Sub; }
            Font adviceFont = Theme.Px(12, true);
            using (var b = new SolidBrush(color)) g.DrawString(advice, adviceFont, b, text);
            float y = text.Y + g.MeasureString(advice, adviceFont, (int)text.Width).Height + 10;

            // Times and the last charge.
            var rows = new List<string[]>();
            if (s.Learn.Length > 0)
            {
                string so = s.LearnRunMin >= 0 && !s.OnAC ? " (this run: " + Fmt.Minutes(s.LearnRunMin) + " so far)" : "";
                rows.Add(new[] { "Learning", "Waiting for the battery to run all the way down once" + so });
            }
            else if (s.LearnNext)
                rows.Add(new[] { "Learning", "Starts with the next run: plug in, then unplug" });
            else if (s.CutoffMv > 0)
                rows.Add(new[] { "Learned limit", "Gives out at " + Fmt.Volts(s.CutoffMv) + " per cell · hibernates at " + Fmt.Volts(s.FloorPerCellMv) });
            if (!s.OnAC) rows.Add(new[] { "On battery for", Ago(s.OnBatterySinceUtc) });
            else if (s.ChargeState == "Charging")
            {
                rows.Add(new[] { "Charging for", Ago(s.ChargingSinceUtc) + " \u00b7 " + s.ChargeInWh.ToString("0.0", CultureInfo.InvariantCulture) + " Wh in so far" });
                if (s.MinutesToFull >= 0) rows.Add(new[] { "Full in about", Fmt.Minutes(s.MinutesToFull) });
            }
            else rows.Add(new[] { "Plugged in for", Ago(s.PluggedSinceUtc) });
            rows.Add(new[] { "Last full charge", s.LastFullUtc.Ticks > 0 ? Ago(s.LastFullUtc) + " ago" : "not seen yet" });
            if (s.LastCharge.Length > 0) rows.Add(new[] { "Last charge", s.LastCharge });
            rows.Add(new[] { "Full charge lasts", Fmt.Minutes(s.FullRuntimeMin) + " of normal use" });
            if (s.Learned.Length > 0) rows.Add(new[] { "Safety", s.Learned });

            Font label = Theme.Px(11.5f, true), value = Theme.Px(11.5f, false);
            float valueX = text.X + 122, valueW = text.Right - valueX;
            using (var sub = new SolidBrush(Theme.Sub))
            using (var main = new SolidBrush(Theme.Text))
            {
                foreach (string[] row in rows)
                {
                    float hgt = Math.Max(16, g.MeasureString(row[1], value, (int)valueW).Height);
                    if (y + hgt > r.Bottom - 8) break;
                    g.DrawString(row[0], label, sub, text.X, y);
                    g.DrawString(row[1], value, main, new RectangleF(valueX, y, valueW, hgt + 2));
                    y += hgt + 5;
                }
            }
        }

        static string Ago(DateTime utc)
        {
            if (utc.Ticks == 0) return "unknown";
            double minutes = Math.Max(0, (DateTime.UtcNow - utc).TotalMinutes);
            if (minutes >= 24 * 60) return string.Format("{0}d {1}h", (int)(minutes / 1440), (int)(minutes % 1440 / 60));
            return Fmt.Minutes(minutes);
        }
    }
    class MainForm : Form
    {
        readonly Agent agent;
        readonly DashboardView view;

        public MainForm(Agent agent)
        {
            this.agent = agent;
            SuspendLayout();
            AutoScaleDimensions = new SizeF(96F, 96F);
            AutoScaleMode = AutoScaleMode.Dpi;
            Text = "Battery Guard";
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Theme.Bg;
            Font = Theme.Pt(9, false);
            Icon = Theme.AppIcon;
            ClientSize = new Size(480, 680);

            view = new DashboardView { Location = new Point(0, 0), Size = new Size(480, 620) };
            view.HistoryRequested += delegate { agent.ShowHistory(); };
            Controls.Add(view);
            var settings = new FlatButton("Settings", true) { Location = new Point(344, 630), Size = new Size(120, 36) };
            settings.Click += delegate
            {
                using (var f = new SettingsForm(agent)) f.ShowDialog(this);
            };
            var history = new FlatButton("History", false) { Location = new Point(16, 630), Size = new Size(104, 36) };
            history.Click += delegate { agent.ShowHistory(); };
            var log = new FlatButton("Battery log", false) { Location = new Point(128, 630), Size = new Size(116, 36) };
            log.Click += delegate { OpenLogCopy(); };
            Controls.Add(settings);
            Controls.Add(history);
            Controls.Add(log);

            ResumeLayout(false);
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Theme.DarkTitleBar(this);
        }

        public void ShowStatus(Status s, bool stale)
        {
            Text = s != null && s.DryRun ? "Battery Guard (test mode)" : "Battery Guard";
            view.Show(s, stale);
        }

        // Opens a copy, so a spreadsheet app can't lock the live log.
        static void OpenLogCopy()
        {
            try
            {
                string copy = Path.Combine(Path.GetTempPath(), "BatteryGuard log " + DateTime.Now.ToString("yyyy-MM-dd HH.mm") + ".csv");
                using (var src = new FileStream(Paths.History, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var dst = File.Create(copy)) src.CopyTo(dst);
                Process.Start(copy);
            }
            catch (Exception ex) { MessageBox.Show("Couldn't open the log: " + ex.Message, "Battery Guard"); }
        }
    }

    class SettingsForm : Form
    {
        static readonly int[] FloorChoices = { 3550, 3500, 3420 };
        int floorLearned;
        static readonly string[] FloorNames = { "Extra safe \u00b7 3.55 V", "Balanced \u00b7 3.50 V", "Longest runtime \u00b7 3.42 V" };

        readonly Agent agent;
        readonly ToggleSwitch enabled, postpone, hideWindows, showTray, voice;
        readonly ComboBox floorBox;
        readonly NumericUpDown warnBox, countdownBox;
        int customFloor;
        int y = 84;

        public SettingsForm(Agent agent)
        {
            this.agent = agent;
            SuspendLayout();
            AutoScaleDimensions = new SizeF(96F, 96F);
            AutoScaleMode = AutoScaleMode.Dpi;
            Text = "Battery Guard settings";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            BackColor = Theme.Bg;
            ForeColor = Theme.Text;
            Font = Theme.Pt(9.5f, false);
            ClientSize = new Size(440, 574);

            Controls.Add(new Label { Text = "Settings", Font = Theme.Pt(15, true), ForeColor = Theme.Text, Location = new Point(22, 18), AutoSize = true });
            Controls.Add(new Label { Text = "These apply to every user on this PC.", ForeColor = Theme.Sub, Location = new Point(24, 52), AutoSize = true });
            Controls.Add(new Label
            {
                Text = Product.Name + " " + Product.Version + "\n\u00a9 2026 " + Product.Author, ForeColor = Theme.Sub, Font = Theme.Pt(8.5f, false),
                TextAlign = ContentAlignment.TopRight, Location = new Point(240, 24), Size = new Size(178, 34),
            });

            enabled = Row("Battery protection", "Hibernate safely before the battery runs out", new ToggleSwitch());
            floorBox = Row("Hibernate at", "Per cell; lower runs longer", new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList, FlatStyle = FlatStyle.Flat, DrawMode = DrawMode.OwnerDrawFixed,
                BackColor = Theme.Card, ForeColor = Theme.Text, Width = 190, ItemHeight = 22,
            });
            floorBox.Items.AddRange(FloorNames);
            floorBox.DrawItem += DrawFloorItem;
            warnBox = Row("Warn me", "Minutes before hibernating", Number(1, 30, 1));
            countdownBox = Row("Countdown", "Seconds on screen before it hibernates", Number(15, 300, 15));
            postpone = Row("Allow one \"+2 min\"", "A single postpone during the countdown", new ToggleSwitch());
            hideWindows = Row("Hide Windows' warnings", "Its low-battery popups are wrong for this battery", new ToggleSwitch());
            showTray = Row("Show the tray icon", "Just for you", new ToggleSwitch());
            voice = Row("Speak when charging stops early", "A voice says \"Charging stopped early\"", new ToggleSwitch());

            Status current = Status.Load();
            bool learning = current != null && (current.Learn.Length > 0 || current.LearnNext);
            var reset = new FlatButton(learning ? "Stop learning" : "Learn my battery", false) { Location = new Point(22, 520), Size = new Size(156, 36) };
            reset.Click += delegate { if (learning) StopLearning(); else LearnBattery(); };
            var cancel = new FlatButton("Cancel", false) { Location = new Point(206, 520), Size = new Size(100, 36) };
            cancel.Click += delegate { Close(); };
            var save = new FlatButton("Save", true) { Location = new Point(316, 520), Size = new Size(100, 36) };
            save.Click += delegate { Save(); };
            Controls.Add(reset);
            Controls.Add(cancel);
            Controls.Add(save);

            LoadSettings();
            ResumeLayout(false);
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Theme.DarkTitleBar(this);
        }

        T Row<T>(string title, string hint, T control) where T : Control
        {
            Controls.Add(new Label { Text = title, Font = Theme.Pt(10, true), ForeColor = Theme.Text, Location = new Point(22, y), AutoSize = true });
            Controls.Add(new Label { Text = hint, Font = Theme.Pt(8.5f, false), ForeColor = Theme.Sub, Location = new Point(22, y + 21), AutoSize = true });
            control.Location = new Point(418 - control.Width, y + 6);
            Controls.Add(control);
            y += 54;
            return control;
        }

        static NumericUpDown Number(int min, int max, int step)
        {
            return new NumericUpDown
            {
                Minimum = min, Maximum = max, Increment = step, Width = 70, BackColor = Theme.Card, ForeColor = Theme.Text,
                BorderStyle = BorderStyle.FixedSingle, TextAlign = HorizontalAlignment.Center,
            };
        }

        void DrawFloorItem(object sender, DrawItemEventArgs e)
        {
            if (e.Index < 0) return;
            bool selected = (e.State & DrawItemState.Selected) != 0 && (e.State & DrawItemState.ComboBoxEdit) == 0;
            using (var b = new SolidBrush(selected ? Theme.Blue : Theme.Card)) e.Graphics.FillRectangle(b, e.Bounds);
            TextRenderer.DrawText(e.Graphics, floorBox.Items[e.Index].ToString(), floorBox.Font, e.Bounds, selected ? Color.White : Theme.Text,
                TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
        }

        void LoadSettings()
        {
            Settings s = Settings.Load() ?? new Settings();
            enabled.Checked = s.Enabled == 1;
            int index = Array.IndexOf(FloorChoices, s.FloorPerCellMv);
            if (index < 0)
            {
                customFloor = s.FloorPerCellMv;
                floorLearned = s.FloorLearned;
                floorBox.Items.Add(string.Format(CultureInfo.InvariantCulture, s.FloorLearned == 1 ? "Learned limit \u00b7 {0:0.00} V" : "Custom \u00b7 {0:0.000} V", s.FloorPerCellMv / 1000.0));
                index = floorBox.Items.Count - 1;
            }
            floorBox.SelectedIndex = index;
            warnBox.Value = (decimal)Math.Max(1, Math.Min(30, s.WarnMinutes));
            countdownBox.Value = Math.Max(15, Math.Min(300, s.CountdownSeconds));
            postpone.Checked = s.AllowPostpone == 1;
            hideWindows.Checked = s.HideWindowsWarnings == 1;
            showTray.Checked = UserPrefs.ShowTray;
            voice.Checked = UserPrefs.VoiceChargeStop;
        }

        void Save()
        {
            Settings s = Settings.Load() ?? new Settings();
            s.Enabled = enabled.Checked ? 1 : 0;
            bool preset = floorBox.SelectedIndex < FloorChoices.Length;
            s.FloorPerCellMv = preset ? FloorChoices[floorBox.SelectedIndex] : customFloor;
            s.FloorLearned = preset ? 0 : floorLearned;
            s.WarnMinutes = (double)warnBox.Value;
            s.CountdownSeconds = (int)countdownBox.Value;
            s.AllowPostpone = postpone.Checked ? 1 : 0;
            s.HideWindowsWarnings = hideWindows.Checked ? 1 : 0;
            try
            {
                // The service checks the values and saves them (only it can write settings.ini).
                Commands.Send("settings\n" + s.ToText());
                Log.Write("Settings changed by " + Environment.UserName + ".");
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Couldn't save the settings: " + ex.Message, "Battery Guard");
                return;
            }
            agent.SetTrayVisible(showTray.Checked);
            if (voice.Checked && !UserPrefs.VoiceChargeStop) Voice.ChargingStopped(); // a preview of what it sounds like
            UserPrefs.VoiceChargeStop = voice.Checked;
            Close();
        }

        // Learns the battery's true limit from the next run on battery (never from an earlier one).
        void LearnBattery()
        {
            bool onBattery = SystemInformation.PowerStatus.PowerLineStatus == PowerLineStatus.Offline;
            string start = onBattery
                ? "It learns from the next run on battery, not this one: this run is protected as usual. Learning starts when you plug in, and the run after you unplug again is the one it learns from (best from a full charge).\n\n"
                : "It learns from the next run on battery: the next time you unplug (best from a full charge).\n\n";
            string text =
                "Battery Guard will learn this battery again: how long it really lasts and where it really gives out.\n\n" + start +
                "During that run Battery Guard won't hibernate. When the battery gives out, the laptop will switch itself off - suddenly, so save your work often. It warns you when the end is near.\n\n" +
                "Afterwards it hibernates a few minutes before the point where this battery gives out, so you get nearly all of it.\n\nStart learning?";
            if (MessageBox.Show(this, text, "Learn my battery", MessageBoxButtons.YesNo, MessageBoxIcon.Information) != DialogResult.Yes) return;
            try
            {
                Commands.Send("learn");
                Log.Write("Learning the battery's true limit, started by " + Environment.UserName + ".");
                Close();
            }
            catch (Exception ex) { MessageBox.Show(this, "Couldn't start learning: " + ex.Message, "Battery Guard"); }
        }

        void StopLearning()
        {
            if (MessageBox.Show(this, "Stop learning? Protection goes back to normal; what was learned so far is kept.",
                "Battery Guard", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            try
            {
                Commands.Send("learnstop");
                Close();
            }
            catch (Exception ex) { MessageBox.Show(this, "Couldn't stop learning: " + ex.Message, "Battery Guard"); }
        }
    }
}
