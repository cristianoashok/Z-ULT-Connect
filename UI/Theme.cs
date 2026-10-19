// Theme.cs - colors, fonts and drawing helpers shared by every control.
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace ZUltConnect.UI
{
    internal static class Theme
    {
        /// <summary>Screen scale (1.0 at 100%, 1.25 at 125%...). Set once at start-up.</summary>
        public static float Scale = 1f;
        public static int S(float px) => (int)Math.Round(px * Scale);
        public static float F(float px) => px * Scale;

        /// <summary>One set of app colors. The app fades between these when the noise mode changes.</summary>
        public readonly record struct Palette(Color Bg, Color Card, Color CardHover, Color Border, Color Accent, Color Glow)
        {
            public static Palette Lerp(Palette a, Palette b, float t) => new(
                Mix(a.Bg, b.Bg, t), Mix(a.Card, b.Card, t), Mix(a.CardHover, b.CardHover, t),
                Mix(a.Border, b.Border, t), Mix(a.Accent, b.Accent, t), Mix(a.Glow, b.Glow, t));
        }

        // One shared night-blue base for every mode (like XM Connect); the mode only
        // changes a soft tint, the glow and the accent colour, so everything blends.
        private static Palette Tinted(Color accent, Color glow, float tint) => new(
            Mix(Color.FromArgb(18, 20, 31), accent, tint),
            Mix(Color.FromArgb(29, 32, 48), accent, tint * 1.2f),
            Mix(Color.FromArgb(39, 43, 63), accent, tint * 1.4f),
            Mix(Color.FromArgb(47, 51, 73), accent, tint * 1.4f),
            accent, glow);

        /// <summary>Off / not connected: night blue with a soft periwinkle accent.</summary>
        public static readonly Palette Black = Tinted(Color.FromArgb(128, 140, 255), Color.FromArgb(104, 116, 255), 0.0f);

        /// <summary>Ambient Sound: blue accent and glow.</summary>
        public static readonly Palette Blue = Tinted(Color.FromArgb(94, 172, 255), Color.FromArgb(60, 140, 255), 0.05f);

        /// <summary>Noise Cancelling: green accent and glow.</summary>
        public static readonly Palette Green = Tinted(Color.FromArgb(58, 217, 145), Color.FromArgb(40, 210, 140), 0.04f);

        /// <summary>The colors in use right now (changes during a fade).</summary>
        public static Palette Current = Black;

        public static Color Bg => Current.Bg;
        public static Color Card => Current.Card;
        public static Color CardHover => Current.CardHover;
        public static Color Border => Current.Border;
        public static Color Accent => Current.Accent;
        public static Color Glow => Current.Glow;
        /// <summary>Lighter end of the accent gradient.</summary>
        public static Color AccentLight => Mix(Current.Accent, Color.White, 0.28f);

        public static readonly Color Orange = Color.FromArgb(255, 122, 69);   // ULT orange
        public static readonly Color NcGreen = Color.FromArgb(58, 217, 145);
        public static readonly Color AmbientBlue = Color.FromArgb(94, 172, 255);
        public static readonly Color Text = Color.FromArgb(238, 240, 244);
        public static readonly Color Dim = Color.FromArgb(150, 158, 172);
        public static readonly Color Faint = Color.FromArgb(84, 90, 104);
        public static readonly Color Good = Color.FromArgb(76, 217, 123);
        public static readonly Color Bad = Color.FromArgb(255, 107, 107);

        public static readonly Font Body = new("Segoe UI", 9.5f);
        public static readonly Font Small = new("Segoe UI", 8.25f);
        public static readonly Font Caption = new("Segoe UI Semibold", 7.5f);
        public static readonly Font Strong = new("Segoe UI Semibold", 9.5f);
        public static readonly Font Title = new("Segoe UI Semibold", 14f);

        public static GraphicsPath Round(RectangleF r, float radius)
        {
            var path = new GraphicsPath();
            float d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
            if (d <= 0.5f) { path.AddRectangle(r); return path; }
            path.AddArc(r.X, r.Y, d, d, 180, 90);
            path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        public static void FillRound(Graphics g, Color color, RectangleF r, float radius)
        {
            using var path = Round(r, radius);
            using var brush = new SolidBrush(color);
            g.FillPath(brush, path);
        }

        /// <summary>Fills a rounded shape with the accent gradient (used for anything "selected" / "on").</summary>
        public static void FillAccent(Graphics g, RectangleF r, float radius, int alpha = 255)
        {
            if (r.Width < 1 || r.Height < 1) return;
            using var path = Round(r, radius);
            using var brush = new LinearGradientBrush(new RectangleF(r.X - 1, r.Y - 1, r.Width + 2, r.Height + 2),
                Alpha(AccentLight, alpha), Alpha(Current.Accent, alpha), 35f);
            g.FillPath(brush, path);
        }

        public static void StrokeRound(Graphics g, Color color, RectangleF r, float radius, float width = 1f)
        {
            using var path = Round(r, radius);
            using var pen = new Pen(color, width);
            g.DrawPath(pen, path);
        }

        public static Color Alpha(Color c, int alpha) => Color.FromArgb(Math.Clamp(alpha, 0, 255), c.R, c.G, c.B);

        public static Color Mix(Color a, Color b, float t) => Color.FromArgb(
            (int)(a.A + (b.A - a.A) * t), (int)(a.R + (b.R - a.R) * t),
            (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));

        public static void DrawText(Graphics g, string text, Font font, Color color, Rectangle bounds,
                                    TextFormatFlags flags = TextFormatFlags.Left | TextFormatFlags.VerticalCenter)
        {
            TextRenderer.DrawText(g, text, font, bounds, color,
                flags | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine);
        }
    }

    /// <summary>Base for our custom-drawn controls: flicker-free, dark background, hover tracking.</summary>
    internal abstract class ThemedControl : Control
    {
        protected bool Hovered;

        protected ThemedControl()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            BackColor = Theme.Bg;
            ForeColor = Theme.Text;
            Font = Theme.Body;
        }

        protected override void OnMouseEnter(EventArgs e) { Hovered = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { Hovered = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }

        protected Graphics Prepare(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Theme.Bg);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            return g;
        }

        protected static int S(float px) => Theme.S(px);
        protected static float F(float px) => Theme.F(px);
    }
}
