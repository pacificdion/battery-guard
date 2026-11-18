// Battery Guard - Copyright (C) 2026 Dion Fabian. Free software under the GNU General Public License v3.0; see LICENSE.
// Build tool: draws the Battery Guard logo (Theme.DrawLogo in UI.cs) at the sizes Windows uses
// and writes two icon files:
//   app.ico        16-256 px, the programs' file icons (Explorer, Start menu, installer)
//   app-small.ico  16-48 px, window icons and the background service
// Usage: MakeIcon.exe <output folder>
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;

static class MakeIcon
{
    static void Main(string[] args)
    {
        string dir = args.Length > 0 ? args[0] : ".";
        Directory.CreateDirectory(dir);
        Write(Path.Combine(dir, "app.ico"), new[] { 16, 20, 24, 32, 40, 48, 64, 128, 256 });
        Write(Path.Combine(dir, "app-small.ico"), new[] { 16, 24, 32, 48 });
        using (Bitmap preview = Draw(256)) preview.Save(Path.Combine(dir, "app-256.png"), ImageFormat.Png);
    }

    static Bitmap Draw(int size)
    {
        var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using (Graphics g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.Transparent);
            BatteryGuard.Theme.DrawLogo(g, 0, 0, size);
        }
        return bmp;
    }

    // Up to 48 px as 32-bit bitmaps (readable by everything), larger ones as PNG.
    static void Write(string path, int[] sizes)
    {
        var images = new List<byte[]>();
        foreach (int s in sizes)
            using (Bitmap b = Draw(s))
                images.Add(s > 48 ? Png(b) : Dib(b));
        using (var fs = File.Create(path))
        using (var w = new BinaryWriter(fs))
        {
            w.Write((short)0); w.Write((short)1); w.Write((short)sizes.Length);
            int offset = 6 + 16 * sizes.Length;
            for (int i = 0; i < sizes.Length; i++)
            {
                w.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
                w.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
                w.Write((byte)0); w.Write((byte)0);
                w.Write((short)1); w.Write((short)32);
                w.Write(images[i].Length); w.Write(offset);
                offset += images[i].Length;
            }
            foreach (byte[] img in images) w.Write(img);
        }
    }

    static byte[] Png(Bitmap b)
    {
        using (var ms = new MemoryStream())
        {
            b.Save(ms, ImageFormat.Png);
            return ms.ToArray();
        }
    }

    static byte[] Dib(Bitmap b)
    {
        int w = b.Width, h = b.Height, maskStride = (w + 31) / 32 * 4;
        using (var ms = new MemoryStream())
        using (var bw = new BinaryWriter(ms))
        {
            bw.Write(40); bw.Write(w); bw.Write(h * 2); bw.Write((short)1); bw.Write((short)32);
            bw.Write(0); bw.Write(w * h * 4); bw.Write(0); bw.Write(0); bw.Write(0); bw.Write(0);
            for (int y = h - 1; y >= 0; y--)
                for (int x = 0; x < w; x++)
                {
                    Color p = b.GetPixel(x, y);
                    bw.Write(p.B); bw.Write(p.G); bw.Write(p.R); bw.Write(p.A);
                }
            // AND mask: 1 where fully transparent, for anything that ignores the alpha channel.
            for (int y = h - 1; y >= 0; y--)
            {
                var row = new byte[maskStride];
                for (int x = 0; x < w; x++)
                    if (b.GetPixel(x, y).A == 0) row[x / 8] |= (byte)(0x80 >> (x % 8));
                bw.Write(row);
            }
            bw.Flush();
            return ms.ToArray();
        }
    }
}
