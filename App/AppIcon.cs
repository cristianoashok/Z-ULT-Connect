// AppIcon.cs - the window icon (from app.ico) and the tray icon, which is drawn
// live so its ear cups show the noise mode: green = Noise Cancelling,
// blue = Ambient, white = Off, grey = not connected.
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using ZUltConnect.Protocol;
using ZUltConnect.UI;

namespace ZUltConnect.App
{
    internal static class AppIcon
    {
        private static Icon? _window;

        public static Icon Window
        {
            get
            {
                if (_window != null) return _window;
                try
                {
                    using var stream = typeof(AppIcon).Assembly.GetManifestResourceStream("ZUltConnect.app.ico");
                    if (stream != null) _window = new Icon(stream);
                }
                catch { }
                return _window ??= Tray(NoiseMode.NoiseCancelling, true);
            }
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool DestroyIcon(IntPtr handle);

        public static Icon Tray(NoiseMode? mode, bool connected)
        {
            int size = Math.Max(16, SystemInformation.SmallIconSize.Width);
            using var bmp = new Bitmap(size, size);
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(Color.Transparent);
                float u = size / 16f;
                Color band = connected ? Color.White : Color.FromArgb(150, 150, 150);
                Color cups = !connected ? Color.FromArgb(150, 150, 150) : mode switch
                {
                    NoiseMode.NoiseCancelling => Theme.NcGreen,
                    NoiseMode.Ambient => Theme.AmbientBlue,
                    _ => Color.White,
                };
                using (var pen = new Pen(band, 1.7f * u))
                    g.DrawArc(pen, 2.6f * u, 1.6f * u, 10.8f * u, 11f * u, 180, 180);
                using var brush = new SolidBrush(cups);
                using (var left = Theme.Round(new RectangleF(1.2f * u, 7.6f * u, 4f * u, 7.2f * u), 1.6f * u))
                    g.FillPath(brush, left);
                using (var right = Theme.Round(new RectangleF(10.8f * u, 7.6f * u, 4f * u, 7.2f * u), 1.6f * u))
                    g.FillPath(brush, right);
            }

            IntPtr handle = bmp.GetHicon();
            try
            {
                using var temp = Icon.FromHandle(handle);
                return (Icon)temp.Clone();
            }
            finally
            {
                try { DestroyIcon(handle); } catch { }
            }
        }
    }
}
