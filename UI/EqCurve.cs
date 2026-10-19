// EqCurve.cs
// ---------------------------------------------------------------------------
// The custom EQ editor: a bass bar on the left and a smooth curve through
// five frequency points. Drag any point up or down (-10 to +10). When a
// preset changes, the curve "morphs" to the new shape.
// ---------------------------------------------------------------------------
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Windows.Forms;

namespace ZUltConnect.UI
{
    internal sealed class EqCurve : ThemedControl
    {
        public static readonly string[] Labels = { "BASS", "400", "1k", "2.5k", "6.3k", "16k" };
        private const int Min = -10, Max = 10, Count = 6;

        private readonly float[] _shown = new float[Count];
        private readonly float[] _from = new float[Count];
        private int[] _target = new int[Count];
        private readonly Timer _anim = new() { Interval = 15 };
        private DateTime _animStart;
        private const double AnimMs = 280;
        private int _drag = -1, _hover = -1;
        private bool _hasData;
        private string _emptyText = "EQ appears when connected";

        /// <summary>Curve color; follows the app's mode color unless set.</summary>
        public Color? ColorOverride;
        private Color Color => ColorOverride ?? Theme.Accent;
        /// <summary>Raised when the user lets go of a point. Values are -10..+10.</summary>
        public event Action<int[]>? BandsCommitted;
        /// <summary>The small "Reset" text in the top-right corner was clicked.</summary>
        public event Action? ResetClicked;
        public bool Dragging => _drag >= 0;

        public EqCurve()
        {
            Height = S(112);
            Cursor = Cursors.Hand;
            _anim.Tick += (_, _) => Animate();
        }

        /// <summary>Show new values (-10..+10). Ignored while the user is dragging.</summary>
        public void SetValues(int[] values, bool animate)
        {
            if (_drag >= 0 || values.Length != Count) return;
            var clamped = values.Select(v => Math.Clamp(v, Min, Max)).ToArray();
            if (_hasData && clamped.SequenceEqual(_target)) return;
            _target = clamped;
            if (!animate || !_hasData)
            {
                for (int i = 0; i < Count; i++) _shown[i] = _target[i];
                _hasData = true;
                _anim.Stop();
                Invalidate();
                return;
            }
            Array.Copy(_shown, _from, Count);
            _animStart = DateTime.UtcNow;
            _anim.Start();
        }

        public void ClearValues(string message)
        {
            _hasData = false;
            _emptyText = message;
            _anim.Stop();
            Invalidate();
        }

        private void Animate()
        {
            double t = Math.Min(1, (DateTime.UtcNow - _animStart).TotalMilliseconds / AnimMs);
            float ease = (float)(1 - Math.Pow(1 - t, 3));
            for (int i = 0; i < Count; i++) _shown[i] = _from[i] + (_target[i] - _from[i]) * ease;
            if (t >= 1) _anim.Stop();
            Invalidate();
        }

        // ---------------- geometry ----------------
        private RectangleF Card => new(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
        private float PlotTop => F(20);
        private RectangleF ResetRect => new(Width - F(58), F(3), F(50), F(15));
        private bool _hoverReset;
        private float PlotBottom => Height - F(26);
        private RectangleF BassColumn => new(F(12), PlotTop, F(34), PlotBottom - PlotTop);
        private float Divider => F(58);
        private RectangleF Plot => new(Divider + F(8), PlotTop, Width - Divider - F(20), PlotBottom - PlotTop);

        private float Y(float v) => PlotBottom - (v - Min) / (Max - Min) * (PlotBottom - PlotTop);

        private float X(int i) => i == 0
            ? BassColumn.Left + BassColumn.Width / 2
            : Plot.Left + F(14) + (i - 1) * (Plot.Width - F(28)) / 4f;

        private PointF Pt(int i) => new(X(i), Y(_shown[i]));

        // ---------------- painting ----------------
        protected override void OnPaint(PaintEventArgs e)
        {
            var g = Prepare(e);
            Theme.FillRound(g, Theme.Card, Card, F(12));

            if (!_hasData)
            {
                Theme.DrawText(g, _emptyText, Theme.Small, Theme.Dim, ClientRectangle,
                               TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                return;
            }

            Color color = Enabled ? Color : Theme.Faint;
            Theme.DrawText(g, "Reset", Theme.Small, !Enabled ? Theme.Faint : _hoverReset ? Theme.Text : Theme.Dim,
                           Rectangle.Round(ResetRect), TextFormatFlags.Right | TextFormatFlags.VerticalCenter);

            // grid: +10, 0, -10
            using (var grid = new Pen(Theme.Alpha(Theme.Border, 160), 1f))
            using (var zero = new Pen(Theme.Faint, 1f) { DashStyle = DashStyle.Dash })
            {
                g.DrawLine(grid, F(10), Y(Max), Width - F(10), Y(Max));
                g.DrawLine(grid, F(10), Y(Min), Width - F(10), Y(Min));
                g.DrawLine(zero, F(10), Y(0), Width - F(10), Y(0));
                g.DrawLine(grid, Divider, PlotTop - F(4), Divider, PlotBottom + F(4));
            }

            // bass bar
            var col = BassColumn;
            Theme.FillRound(g, Theme.Alpha(Theme.Border, 120), col, F(8));
            float y0 = Y(0), yb = Y(_shown[0]);
            var bar = new RectangleF(col.Left + F(6), Math.Min(y0, yb), col.Width - F(12), Math.Abs(yb - y0));
            if (bar.Height > 0.5f) Theme.FillRound(g, Theme.Alpha(color, 200), bar, F(5));

            // curve through points 1..5, flat to the edges
            var pts = Enumerable.Range(1, 5).Select(Pt).ToArray();
            using (var path = new GraphicsPath())
            {
                path.AddLine(Plot.Left, pts[0].Y, pts[0].X, pts[0].Y);
                for (int i = 0; i < pts.Length - 1; i++)
                {
                    PointF p0 = pts[Math.Max(i - 1, 0)], p1 = pts[i], p2 = pts[i + 1], p3 = pts[Math.Min(i + 2, pts.Length - 1)];
                    var c1 = new PointF(p1.X + (p2.X - p0.X) / 6f, p1.Y + (p2.Y - p0.Y) / 6f);
                    var c2 = new PointF(p2.X - (p3.X - p1.X) / 6f, p2.Y - (p3.Y - p1.Y) / 6f);
                    path.AddBezier(p1, c1, c2, p2);
                }
                path.AddLine(pts[^1].X, pts[^1].Y, Plot.Right, pts[^1].Y);

                using (var fill = (GraphicsPath)path.Clone())
                {
                    fill.AddLine(Plot.Right, pts[^1].Y, Plot.Right, PlotBottom);
                    fill.AddLine(Plot.Right, PlotBottom, Plot.Left, PlotBottom);
                    fill.CloseFigure();
                    using var brush = new LinearGradientBrush(new PointF(0, PlotTop - 1), new PointF(0, PlotBottom + 1),
                                                              Theme.Alpha(color, 90), Theme.Alpha(color, 0));
                    g.FillPath(brush, fill);
                }
                using var stroke = new Pen(color, F(2.5f)) { LineJoin = LineJoin.Round };
                g.DrawPath(stroke, path);
            }

            // points + labels
            for (int i = 0; i < Count; i++)
            {
                var p = Pt(i);
                bool active = i == _drag || (i == _hover && _drag < 0);
                float r = active ? F(7.5f) : F(6);
                using (var fill = new SolidBrush(active ? color : Theme.Card))
                    g.FillEllipse(fill, p.X - r, p.Y - r, r * 2, r * 2);
                using (var ring = new Pen(color, F(2)))
                    g.DrawEllipse(ring, p.X - r, p.Y - r, r * 2, r * 2);

                var labelRect = new Rectangle((int)(p.X - F(24)), (int)(PlotBottom + F(6)), S(48), S(16));
                Theme.DrawText(g, Labels[i], Theme.Small, i == _drag ? Theme.Text : Theme.Dim, labelRect,
                               TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            }

            // value bubble
            int bubble = _drag >= 0 ? _drag : _hover;
            if (bubble >= 0 && Enabled)
            {
                var p = Pt(bubble);
                int v = (int)Math.Round(_shown[bubble]);
                string text = v > 0 ? $"+{v}" : $"{v}";
                var size = TextRenderer.MeasureText(text, Theme.Strong);
                float bw = size.Width + F(12), bh = F(20);
                float by = p.Y - F(12) - bh < F(2) ? p.Y + F(12) : p.Y - F(12) - bh;
                var br = new RectangleF(Math.Clamp(p.X - bw / 2, F(2), Width - bw - F(2)), by, bw, bh);
                Theme.FillRound(g, color, br, F(6));
                Theme.DrawText(g, text, Theme.Strong, Color.White, Rectangle.Round(br),
                               TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            }
        }

        // ---------------- mouse ----------------
        private int HitTest(Point p)
        {
            if (p.X < Divider) return 0;
            int best = -1;
            float bestDist = float.MaxValue;
            for (int i = 1; i < Count; i++)
            {
                float d = Math.Abs(p.X - X(i));
                if (d < bestDist) { bestDist = d; best = i; }
            }
            return bestDist <= (Plot.Width - F(28)) / 8f + F(14) ? best : -1;
        }

        private void SetFromY(int i, int y)
        {
            int v = (int)Math.Round(Min + (PlotBottom - y) / (PlotBottom - PlotTop) * (Max - Min));
            v = Math.Clamp(v, Min, Max);
            if (_target[i] == v && Math.Abs(_shown[i] - v) < 0.01f) return;
            _target[i] = v;
            _shown[i] = v;
            Invalidate();
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (!Enabled || !_hasData || e.Button != MouseButtons.Left) return;
            if (ResetRect.Contains(e.Location)) { ResetClicked?.Invoke(); return; }
            int i = HitTest(e.Location);
            if (i < 0) return;
            // finish any running morph so we drag from the real values
            _anim.Stop();
            for (int k = 0; k < Count; k++) _shown[k] = _target[k];
            _drag = i;
            Capture = true;
            SetFromY(i, e.Y);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (_drag >= 0) { SetFromY(_drag, e.Y); return; }
            bool overReset = _hasData && Enabled && ResetRect.Contains(e.Location);
            if (overReset != _hoverReset) { _hoverReset = overReset; Invalidate(); }
            int h = _hasData && Enabled ? HitTest(e.Location) : -1;
            if (h != _hover) { _hover = h; Invalidate(); }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (_drag < 0) return;
            _drag = -1;
            Capture = false;
            Invalidate();
            BandsCommitted?.Invoke(_target.ToArray());
        }

        protected override void OnMouseLeave(EventArgs e) { _hover = -1; base.OnMouseLeave(e); }

        protected override void Dispose(bool disposing)
        {
            if (disposing) _anim.Dispose();
            base.Dispose(disposing);
        }
    }
}
