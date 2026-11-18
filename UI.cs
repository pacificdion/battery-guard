// Battery Guard - Copyright (C) 2026 Dion Fabian. Free software under the GNU General Public License v3.0; see LICENSE.
// Look and feel shared by the Battery Guard app and its installer: colours that follow Windows'
// light/dark setting, fonts, flat buttons, switches, and the app icon.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Win32;

namespace BatteryGuard
{
    static class Fmt
    {
        public static string Minutes(double minutes)
        {
            if (minutes < 0) return "measuring";
            int m = (int)Math.Round(minutes);
            if (m >= 600) return "10h+";
            if (m >= 60) return string.Format("{0}h {1:00}m", m / 60, m % 60);
            return Math.Max(1, m) + " min";
        }

        public static string Volts(int mv)
        {
            return (mv / 1000.0).ToString("0.00", CultureInfo.InvariantCulture) + " V";
        }

        public static string Watts(int mw)
        {
            return (Math.Abs(mw) / 1000.0).ToString("0.0", CultureInfo.InvariantCulture) + " W";
        }

        // "21:40 today", "04:15 tomorrow", "Thu 09:30"
        public static string When(DateTime utc)
        {
            DateTime t = utc.ToLocalTime(), today = DateTime.Now.Date;
            string day = t.Date == today ? " today" : t.Date == today.AddDays(1) ? " tomorrow" : "";
            return day.Length > 0 ? t.ToString("HH:mm", CultureInfo.InvariantCulture) + day : t.ToString("ddd HH:mm", CultureInfo.InvariantCulture);
        }

        public static string Hours(double h)
        {
            return h.ToString("0.#", CultureInfo.InvariantCulture) + " hours";
        }

        public static string Pct(double p)
        {
            return p < 0 ? "\u2013" : Math.Round(p).ToString(CultureInfo.InvariantCulture) + "%";
        }
    }

    // Colours, fonts and shapes, following Windows' light/dark app setting.
    static class Theme
    {
        public static readonly bool Dark = ReadDark();
        public static readonly float Scale = ReadScale();
        public static readonly Color Bg, Card, Text, Sub, Line;
        public static readonly Color Green = Color.FromArgb(34, 197, 94), Blue = Color.FromArgb(59, 130, 246),
            Orange = Color.FromArgb(245, 158, 11), Red = Color.FromArgb(239, 68, 68), Gray = Color.FromArgb(148, 163, 184);
        static readonly Dictionary<string, Font> fonts = new Dictionary<string, Font>();
        static Icon appIcon;

        static Theme()
        {
            Bg = Dark ? Color.FromArgb(24, 24, 27) : Color.FromArgb(243, 244, 246);
            Card = Dark ? Color.FromArgb(39, 39, 42) : Color.White;
            Text = Dark ? Color.FromArgb(244, 244, 245) : Color.FromArgb(24, 24, 27);
            Sub = Dark ? Color.FromArgb(161, 161, 170) : Color.FromArgb(107, 114, 128);
            Line = Dark ? Color.FromArgb(63, 63, 70) : Color.FromArgb(229, 231, 235);
        }

        static bool ReadDark()
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                    return k != null && Convert.ToInt32(k.GetValue("AppsUseLightTheme", 1)) == 0;
            }
            catch { return false; }
        }

        public static bool LightTaskbar()
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                    return k != null && Convert.ToInt32(k.GetValue("SystemUsesLightTheme", 0)) == 1;
            }
            catch { return false; }
        }

        static float ReadScale()
        {
            try { using (Graphics g = Graphics.FromHwnd(IntPtr.Zero)) return g.DpiX / 96f; }
            catch { return 1; }
        }

        // Size in design pixels, for drawing under a Scale transform.
        public static Font Px(float px, bool semibold)
        {
            return Get((semibold ? "Segoe UI Semibold" : "Segoe UI") + "|px|" + px, semibold, px, GraphicsUnit.Pixel);
        }

        // Size in points, for ordinary controls (Windows scales these itself).
        public static Font Pt(float pt, bool semibold)
        {
            return Get((semibold ? "Segoe UI Semibold" : "Segoe UI") + "|pt|" + pt, semibold, pt, GraphicsUnit.Point);
        }

        static Font Get(string key, bool semibold, float size, GraphicsUnit unit)
        {
            Font f;
            if (!fonts.TryGetValue(key, out f)) fonts[key] = f = new Font(semibold ? "Segoe UI Semibold" : "Segoe UI", size, FontStyle.Regular, unit);
            return f;
        }

        public static Color Alpha(Color c, int alpha) { return Color.FromArgb(alpha, c.R, c.G, c.B); }

        public static Color Mix(Color a, Color b, double t)
        {
            return Color.FromArgb((int)(a.R + (b.R - a.R) * t), (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));
        }

        public static GraphicsPath Rounded(RectangleF r, float radius)
        {
            var p = new GraphicsPath();
            float d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        public static void FillCard(Graphics g, RectangleF r)
        {
            using (GraphicsPath p = Rounded(r, 14))
            using (var b = new SolidBrush(Card))
            {
                g.FillPath(b, p);
                if (!Dark) using (var pen = new Pen(Line)) g.DrawPath(pen, p);
            }
        }

        public static void Draw(Graphics g, string text, Font font, Color color, float x, float y)
        {
            using (var b = new SolidBrush(color)) g.DrawString(text, font, b, x, y, StringFormat.GenericTypographic);
        }

        public static SizeF Measure(Graphics g, string text, Font font)
        {
            return g.MeasureString(text, font, PointF.Empty, StringFormat.GenericTypographic);
        }

        [DllImport("dwmapi.dll")]
        static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

        public static void DarkTitleBar(Form f)
        {
            if (!Dark) return;
            int on = 1;
            if (DwmSetWindowAttribute(f.Handle, 20, ref on, 4) != 0) DwmSetWindowAttribute(f.Handle, 19, ref on, 4);
        }

        public struct Look { public Color Color; public string Label; }

        public static Look State(Status s, bool stale)
        {
            if (stale || s == null) return L(Red, "SERVICE STOPPED");
            if (s.Phase == "Countdown" || s.Phase == "Hibernating") return L(Red, "HIBERNATING SOON");
            if (s.OnAC)
            {
                if (s.ChargeState == "Charging") return L(Blue, "CHARGING");
                if (s.ChargeState == "Full") return L(Green, "FULLY CHARGED");
                return L(Orange, "NOT CHARGING");
            }
            if (s.Cycle == "ready" && s.Percent < 85) return L(Blue, "READY TO CHARGE");
            if (s.Protection == "Fallback") return L(Orange, "NO VOLTAGE READING");
            if (s.Protection == "Off") return L(Gray, "ON BATTERY");
            return L(s.Percent > 30 || s.Percent < 0 ? Green : s.Percent > 10 ? Orange : Red, "ON BATTERY");
        }

        static Look L(Color c, string label) { return new Look { Color = c, Label = label }; }

        // Builds a real 32-bit icon in memory. (Bitmap.GetHicon loses partial transparency, which
        // turns soft edges and translucent parts dark.)
        public static Icon ToIcon(Bitmap bmp)
        {
            int w = bmp.Width, h = bmp.Height, maskStride = (w + 31) / 32 * 4;
            using (var ms = new MemoryStream())
            using (var bw = new BinaryWriter(ms))
            {
                bw.Write((short)0); bw.Write((short)1); bw.Write((short)1);                // ICONDIR: icon, 1 image
                bw.Write((byte)w); bw.Write((byte)h); bw.Write((byte)0); bw.Write((byte)0);
                bw.Write((short)1); bw.Write((short)32);
                bw.Write(40 + w * h * 4 + maskStride * h); bw.Write(22);                     // size, offset
                bw.Write(40); bw.Write(w); bw.Write(h * 2); bw.Write((short)1); bw.Write((short)32); // BITMAPINFOHEADER
                bw.Write(0); bw.Write(w * h * 4); bw.Write(0); bw.Write(0); bw.Write(0); bw.Write(0);
                for (int y = h - 1; y >= 0; y--)                                             // BGRA, bottom-up
                    for (int x = 0; x < w; x++)
                    {
                        Color p = bmp.GetPixel(x, y);
                        bw.Write(p.B); bw.Write(p.G); bw.Write(p.R); bw.Write(p.A);
                    }
                bw.Write(new byte[maskStride * h]);                                           // AND mask (unused with alpha)
                bw.Flush();
                ms.Position = 0;
                return new Icon(ms);
            }
        }

        // A small battery on a green tile: the program's own icon (every size), else drawn here.
        public static Icon AppIcon
        {
            get
            {
                if (appIcon != null) return appIcon;
                try
                {
                    using (Stream s = System.Reflection.Assembly.GetEntryAssembly().GetManifestResourceStream("app.ico"))
                        if (s != null) return appIcon = new Icon(s);
                }
                catch { }
                using (var bmp = new Bitmap(32, 32))
                {
                    using (Graphics g = Graphics.FromImage(bmp)) DrawLogo(g, 0, 0, 32);
                    appIcon = ToIcon(bmp);
                }
                return appIcon;
            }
        }

        // The logo, drawn as shapes so it's sharp at any size: a battery on a green tile.
        public static void DrawLogo(Graphics g, float x, float y, float size)
        {
            GraphicsState saved = g.Save();
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            float u = size / 32f;
            using (GraphicsPath p = Rounded(new RectangleF(x + 1 * u, y + 1 * u, 30 * u, 30 * u), 7.5f * u))
            using (var fill = new LinearGradientBrush(new RectangleF(x, y, size, size + 1), Color.FromArgb(52, 211, 153), Color.FromArgb(21, 160, 72), 90f))
                g.FillPath(fill, p);
            using (GraphicsPath p = Rounded(new RectangleF(x + 1 * u, y + 1 * u, 30 * u, 15 * u), 7.5f * u))
            using (var shine = new LinearGradientBrush(new RectangleF(x, y, size, 16 * u + 1), Color.FromArgb(40, 255, 255, 255), Color.FromArgb(0, 255, 255, 255), 90f))
                g.FillPath(shine, p);
            using (var pen = new Pen(Color.White, Math.Max(1.15f, 2.2f * u)) { LineJoin = LineJoin.Round })
            using (GraphicsPath body = Rounded(new RectangleF(x + 6 * u, y + 10 * u, 18 * u, 12 * u), 3 * u))
                g.DrawPath(pen, body);
            using (var white = new SolidBrush(Color.White))
            {
                using (GraphicsPath nub = Rounded(new RectangleF(x + 25.2f * u, y + 13.3f * u, 2.6f * u, 5.4f * u), 1.1f * u))
                    g.FillPath(white, nub);
                using (GraphicsPath bar = Rounded(new RectangleF(x + 9.2f * u, y + 13.2f * u, 8.6f * u, 5.6f * u), 1.2f * u))
                    g.FillPath(white, bar);
            }
            g.Restore(saved);
        }
    }

    // Flat rounded button.
    class FlatButton : Control
    {
        readonly bool primary;
        bool hover, down, selected;

        public bool Selected
        {
            get { return selected; }
            set { selected = value; Invalidate(); }
        }

        public FlatButton(string text, bool primary)
        {
            this.primary = primary;
            Text = text;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            Cursor = Cursors.Hand;
            Font = Theme.Pt(9.5f, true);
            BackColor = Theme.Bg;
            Size = new Size(120, 34);
        }

        protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = down = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) { down = true; Invalidate(); base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e) { down = false; Invalidate(); base.OnMouseUp(e); }
        protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(BackColor);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            bool accent = primary || Selected;
            Color fill = accent ? Theme.Blue : Theme.Card;
            if (hover) fill = Theme.Mix(fill, Theme.Dark || accent ? Color.White : Color.Black, 0.08);
            if (down) fill = Theme.Mix(fill, Color.Black, 0.12);
            Color text = accent ? Color.White : Theme.Text;
            if (!Enabled) { fill = Theme.Mix(fill, BackColor, 0.5); text = Theme.Sub; }
            using (GraphicsPath p = Theme.Rounded(new RectangleF(0.5f, 0.5f, Width - 1, Height - 1), 9))
            using (var b = new SolidBrush(fill))
            {
                g.FillPath(b, p);
                if (!accent) using (var pen = new Pen(Theme.Line)) g.DrawPath(pen, p);
            }
            TextRenderer.DrawText(g, Text, Font, ClientRectangle, text, fill,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
        }
    }

    // On/off switch.
    class ToggleSwitch : Control
    {
        bool on;
        public event EventHandler CheckedChanged;

        public bool Checked
        {
            get { return on; }
            set
            {
                if (on == value) return;
                on = value;
                Invalidate();
                if (CheckedChanged != null) CheckedChanged(this, EventArgs.Empty);
            }
        }

        public ToggleSwitch()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            Cursor = Cursors.Hand;
            BackColor = Theme.Bg;
            Size = new Size(42, 24);
        }

        protected override void OnClick(EventArgs e) { Checked = !Checked; base.OnClick(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(BackColor);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            float h = Height - 2, w = Width - 2;
            using (GraphicsPath track = Theme.Rounded(new RectangleF(1, 1, w, h), h / 2))
            using (var b = new SolidBrush(on ? Theme.Blue : Theme.Line))
                g.FillPath(b, track);
            float knob = h - 6;
            using (var k = new SolidBrush(Color.White))
                g.FillEllipse(k, on ? w - knob - 2 : 4, 4, knob, knob);
        }
    }
}
