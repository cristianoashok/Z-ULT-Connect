// Controls.cs - the small custom controls the panel is built from.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace ZUltConnect.UI
{
    /// <summary>A section label like "NOISE CONTROL", with an optional value on the right.</summary>
    internal sealed class CaptionView : ThemedControl
    {
        private string _title, _value = "";
        public CaptionView(string title) { _title = title; Height = S(18); }
        public string Value { get => _value; set { if (_value != value) { _value = value; Invalidate(); } } }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = Prepare(e);
            var r = new Rectangle(0, 0, Width, Height);
            Theme.DrawText(g, _title, Theme.Caption, Theme.Dim, r);
            Theme.DrawText(g, _value, Theme.Small, Theme.Text, r, TextFormatFlags.Right | TextFormatFlags.VerticalCenter);
        }
    }

    /// <summary>A horizontal slider (volume, ambient strength).</summary>
    internal sealed class FlatSlider : ThemedControl
    {
        private int _min, _max = 100, _value;
        private bool _drag;
        public Color? Fill;
        public event Action<int>? ValueChanging;
        public event Action<int>? ValueCommitted;
        public bool Dragging => _drag;

        public FlatSlider(int min, int max) { _min = min; _max = max; _value = min; Height = S(22); Cursor = Cursors.Hand; TabStop = true; }

        public int Value
        {
            get => _value;
            set { int v = Math.Clamp(value, _min, _max); if (v != _value) { _value = v; Invalidate(); } }
        }

        private RectangleF Track => new(F(9), Height / 2f - F(3), Width - F(18), F(6));

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = Prepare(e);
            var t = Track;
            float frac = _max == _min ? 0 : (_value - _min) / (float)(_max - _min);
            float x = t.Left + frac * t.Width;
            Theme.FillRound(g, Theme.Border, t, t.Height / 2);
            var filled = new RectangleF(t.Left, t.Top, Math.Max(t.Height, x - t.Left), t.Height);
            if (Enabled && Fill == null) Theme.FillAccent(g, filled, t.Height / 2);
            else Theme.FillRound(g, Enabled ? Fill ?? Theme.Accent : Theme.Faint, filled, t.Height / 2);
            float r = (_drag || Hovered) && Enabled ? F(9) : F(8);
            using var knob = new SolidBrush(Enabled ? Theme.Text : Theme.Dim);
            g.FillEllipse(knob, x - r, Height / 2f - r, r * 2, r * 2);
        }

        private void SetFromX(int x)
        {
            var t = Track;
            float frac = Math.Clamp((x - t.Left) / t.Width, 0f, 1f);
            int v = (int)Math.Round(_min + frac * (_max - _min));
            if (v == _value) return;
            _value = v;
            Invalidate();
            ValueChanging?.Invoke(v);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (!Enabled || e.Button != MouseButtons.Left) return;
            _drag = true; Capture = true; Focus();
            SetFromX(e.X);
        }

        protected override void OnMouseMove(MouseEventArgs e) { base.OnMouseMove(e); if (_drag) SetFromX(e.X); }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (!_drag) return;
            _drag = false; Capture = false; Invalidate();
            ValueCommitted?.Invoke(_value);
        }

        protected override bool IsInputKey(Keys keyData) => keyData is Keys.Left or Keys.Right || base.IsInputKey(keyData);

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            int delta = e.KeyCode == Keys.Left ? -1 : e.KeyCode == Keys.Right ? 1 : 0;
            if (delta == 0 || !Enabled) return;
            int before = _value;
            Value = _value + delta;
            if (_value != before) { ValueChanging?.Invoke(_value); ValueCommitted?.Invoke(_value); }
        }
    }

    /// <summary>A row of mutually exclusive buttons (noise mode, ULT mode).</summary>
    internal sealed class Segmented : ThemedControl
    {
        private readonly string[] _items;
        private int _selected = -1, _hover = -1;
        public Color[]? ItemColors;
        /// <summary>This item is drawn grey when selected (e.g. "ULT off").</summary>
        public int MutedIndex = -1;
        public event Action<int>? SelectionChanged;

        public Segmented(params string[] items) { _items = items; Height = S(30); Cursor = Cursors.Hand; }

        public int SelectedIndex
        {
            get => _selected;
            set { if (_selected != value) { _selected = value; Invalidate(); } }
        }

        private RectangleF Segment(int i)
        {
            float pad = F(3);
            float w = (Width - pad * 2) / _items.Length;
            return new RectangleF(pad + i * w, pad, w, Height - pad * 2);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = Prepare(e);
            var outer = new RectangleF(0, 0, Width - 1, Height - 1);
            Theme.FillRound(g, Theme.Card, outer, F(10));
            for (int i = 0; i < _items.Length; i++)
            {
                var r = Segment(i);
                bool sel = i == _selected;
                Color accent = i == MutedIndex ? Theme.CardHover
                             : ItemColors != null && i < ItemColors.Length ? ItemColors[i] : Theme.Accent;
                if (sel && Enabled && i != MutedIndex && ItemColors == null) Theme.FillAccent(g, r, F(8));
                else if (sel) Theme.FillRound(g, Enabled ? accent : Theme.Faint, r, F(8));
                else if (i == _hover && Enabled) Theme.FillRound(g, Theme.CardHover, r, F(8));
                Color text = !Enabled ? Theme.Faint : sel ? Color.White : Theme.Dim;
                Theme.DrawText(g, _items[i], sel ? Theme.Strong : Theme.Body, text, Rectangle.Round(r),
                               TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            }
        }

        private int HitTest(Point p)
        {
            for (int i = 0; i < _items.Length; i++) if (Segment(i).Contains(p)) return i;
            return -1;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            int h = HitTest(e.Location);
            if (h != _hover) { _hover = h; Invalidate(); }
        }

        protected override void OnMouseLeave(EventArgs e) { _hover = -1; base.OnMouseLeave(e); }

        protected override void OnMouseClick(MouseEventArgs e)
        {
            base.OnMouseClick(e);
            if (!Enabled) return;
            int i = HitTest(e.Location);
            if (i < 0 || i == _selected) return;
            _selected = i;
            Invalidate();
            SelectionChanged?.Invoke(i);
        }
    }

    /// <summary>Wrapping "pill" buttons for the EQ presets.</summary>
    internal sealed class ChipPanel : ThemedControl
    {
        private readonly (byte Id, string Name)[] _items;
        private readonly List<RectangleF> _rects = new();
        private byte? _selected;
        private int _hover = -1;
        public event Action<byte>? ChipClicked;

        public ChipPanel((byte Id, string Name)[] items) { _items = items; Cursor = Cursors.Hand; Height = S(64); }

        public byte? Selected
        {
            get => _selected;
            set { if (_selected != value) { _selected = value; Invalidate(); } }
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            Arrange();
        }

        private void Arrange()
        {
            _rects.Clear();
            if (Width <= 0) return;
            float x = 0, y = 0, h = F(28), gap = F(6);
            foreach (var item in _items)
            {
                float w = TextRenderer.MeasureText(item.Name, Theme.Small).Width + F(20);
                if (x > 0 && x + w > Width) { x = 0; y += h + gap; }
                _rects.Add(new RectangleF(x, y, w, h));
                x += w + gap;
            }
            int height = (int)Math.Ceiling(y + h + 1);
            if (Height != height) Height = height;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = Prepare(e);
            if (_rects.Count != _items.Length) Arrange();
            for (int i = 0; i < _items.Length; i++)
            {
                var r = _rects[i];
                r.Width -= 1; r.Height -= 1;
                bool sel = _items[i].Id == _selected;
                if (sel)
                    Theme.FillRound(g, Enabled ? Theme.Accent : Theme.Faint, r, r.Height / 2);
                else
                {
                    Theme.FillRound(g, i == _hover && Enabled ? Theme.CardHover : Theme.Card, r, r.Height / 2);
                    Theme.StrokeRound(g, Theme.Border, r, r.Height / 2);
                }
                Color text = !Enabled ? Theme.Faint : sel ? Color.White : Theme.Text;
                Theme.DrawText(g, _items[i].Name, sel ? Theme.Strong : Theme.Small, text, Rectangle.Round(r),
                               TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            }
        }

        private int HitTest(Point p)
        {
            for (int i = 0; i < _rects.Count; i++) if (_rects[i].Contains(p)) return i;
            return -1;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            int h = HitTest(e.Location);
            if (h != _hover) { _hover = h; Invalidate(); }
        }

        protected override void OnMouseLeave(EventArgs e) { _hover = -1; base.OnMouseLeave(e); }

        protected override void OnMouseClick(MouseEventArgs e)
        {
            base.OnMouseClick(e);
            if (!Enabled) return;
            int i = HitTest(e.Location);
            if (i < 0) return;
            _selected = _items[i].Id;
            Invalidate();
            ChipClicked?.Invoke(_items[i].Id);
        }
    }

    /// <summary>A label with an on/off switch on the right.</summary>
    internal sealed class ToggleRow : ThemedControl
    {
        private bool _checked;
        private readonly string _label;
        public event Action<bool>? Toggled;

        public ToggleRow(string label) { _label = label; Height = S(26); Cursor = Cursors.Hand; }

        public bool Checked
        {
            get => _checked;
            set { if (_checked != value) { _checked = value; Invalidate(); } }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = Prepare(e);
            var track = new RectangleF(Width - F(40), Height / 2f - F(11), F(38), F(22));
            Theme.DrawText(g, _label, Theme.Body, Enabled ? Theme.Text : Theme.Faint,
                           new Rectangle(0, 0, (int)track.Left - S(8), Height));
            Color on = Enabled ? Theme.Accent : Theme.Faint;
            if (_checked && Enabled) Theme.FillAccent(g, track, track.Height / 2);
            else Theme.FillRound(g, _checked ? on : Theme.Border, track, track.Height / 2);
            float d = track.Height - F(6);
            float kx = _checked ? track.Right - d - F(3) : track.Left + F(3);
            using var knob = new SolidBrush(Enabled ? Color.White : Theme.Dim);
            g.FillEllipse(knob, kx, track.Top + F(3), d, d);
        }

        protected override void OnMouseClick(MouseEventArgs e)
        {
            base.OnMouseClick(e);
            if (!Enabled) return;
            _checked = !_checked;
            Invalidate();
            Toggled?.Invoke(_checked);
        }
    }

    /// <summary>A rounded button. Subtle = text only (like a link).</summary>
    internal sealed class FlatButton : ThemedControl
    {
        private string _label;
        private bool _down;
        public bool Subtle;
        public bool AlignLeft;
        public Color? TextColor;

        public FlatButton(string label) { _label = label; Height = S(30); Cursor = Cursors.Hand; }

        public string Label { get => _label; set { if (_label != value) { _label = value; Invalidate(); } } }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = Prepare(e);
            var r = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
            if (!Subtle)
            {
                Color fill = !Enabled ? Theme.Card : _down ? Theme.Border : Hovered ? Theme.CardHover : Theme.Card;
                Theme.FillRound(g, fill, r, F(9));
            }
            Color text = !Enabled ? Theme.Faint : TextColor ?? (Subtle ? (Hovered ? Theme.Text : Theme.Accent) : Theme.Text);
            Theme.DrawText(g, _label, Subtle ? Theme.Small : Theme.Body, text, Rectangle.Round(r),
                           (AlignLeft ? TextFormatFlags.Left : Subtle ? TextFormatFlags.Right : TextFormatFlags.HorizontalCenter) | TextFormatFlags.VerticalCenter);
        }

        protected override void OnMouseDown(MouseEventArgs e) { _down = true; Invalidate(); base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e) { _down = false; Invalidate(); base.OnMouseUp(e); }
    }

    /// <summary>A full-width button showing a title on the left and the current choice on the right; opens a list.</summary>
    internal sealed class DropdownButton : ThemedControl
    {
        private readonly string _title;
        private (byte Id, string Name)[] _choices;
        private byte? _selected;
        private readonly ContextMenuStrip _list = new() { ShowImageMargin = false, ShowCheckMargin = true };
        private DateTime _closedAt;
        public event Action<byte>? ChoicePicked;

        public DropdownButton(string title, (byte Id, string Name)[] choices)
        {
            _title = title;
            _choices = choices;
            Height = S(38);
            Cursor = Cursors.Hand;
            _list.Renderer = new App.DarkMenuRenderer();
            _list.Closed += (_, _) => { _closedAt = DateTime.UtcNow; Invalidate(); };
        }

        public void SetChoices((byte Id, string Name)[] choices) { _choices = choices; Invalidate(); }

        public byte? Selected
        {
            get => _selected;
            set { if (_selected != value) { _selected = value; Invalidate(); } }
        }

        /// <summary>Pill mode: a small centered pill showing only the selected name.</summary>
        public bool Pill;
        /// <summary>Shown before anything has been read from the headphones.</summary>
        public string Placeholder = "–";

        private string SelectedName =>
            _selected is byte id ? (_choices.FirstOrDefault(c => c.Id == id).Name ?? $"0x{id:X2}") : Placeholder;

        private RectangleF PillRect
        {
            get
            {
                float w = TextRenderer.MeasureText(SelectedName, Theme.Strong).Width + F(46);
                return new RectangleF((Width - w) / 2, 0.5f, w, Height - 1.5f);
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = Prepare(e);
            if (Pill)
            {
                var pr = PillRect;
                Theme.FillRound(g, Hovered && Enabled ? Theme.CardHover : Theme.Card, pr, pr.Height / 2);
                var textRect = new Rectangle((int)(pr.X + F(16)), 0, (int)(pr.Width - F(38)), Height);
                Theme.DrawText(g, SelectedName, Theme.Strong, Enabled ? Theme.Text : Theme.Dim, textRect,
                               TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                DrawChevron(g, pr.Right - F(18), Height / 2f);
                return;
            }
            var r = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
            Theme.FillRound(g, Hovered && Enabled ? Theme.CardHover : Theme.Card, r, F(11));
            int pad = S(14);
            Theme.DrawText(g, _title, Theme.Caption, Theme.Dim, new Rectangle(pad, 0, Width / 2, Height));
            var valueRect = new Rectangle(Width / 3, 0, Width - Width / 3 - S(34), Height);
            Theme.DrawText(g, SelectedName, Theme.Strong, Enabled ? Theme.Text : Theme.Faint, valueRect,
                           TextFormatFlags.Right | TextFormatFlags.VerticalCenter);
            DrawChevron(g, Width - F(20), Height / 2f);
        }

        private void DrawChevron(Graphics g, float cx, float cy)
        {
            using var pen = new Pen(Enabled ? Theme.Accent : Theme.Faint, F(2)) { StartCap = System.Drawing.Drawing2D.LineCap.Round, EndCap = System.Drawing.Drawing2D.LineCap.Round };
            g.DrawLines(pen, new[] { new PointF(cx - F(4.5f), cy - F(2.2f)), new PointF(cx, cy + F(2.2f)), new PointF(cx + F(4.5f), cy - F(2.2f)) });
        }

        protected override void OnMouseClick(MouseEventArgs e)
        {
            base.OnMouseClick(e);
            if (!Enabled) return;
            // The click that closed the list (by clicking this button again) must not reopen it.
            if (_list.Visible || (DateTime.UtcNow - _closedAt).TotalMilliseconds < 300) { _list.Close(); return; }
            if (Pill && !PillRect.Contains(e.Location)) return;
            _list.Items.Clear();
            foreach (var (id, name) in _choices)
            {
                var item = new ToolStripMenuItem(name) { Checked = id == _selected, ForeColor = Theme.Text };
                byte pick = id;
                item.Click += (_, _) => { Selected = pick; ChoicePicked?.Invoke(pick); };
                _list.Items.Add(item);
            }
            if (Pill)
            {
                var pr = PillRect;
                _list.MinimumSize = new Size((int)Math.Max(pr.Width, S(150)), 0);
                _list.Show(this, new Point((int)(pr.X + pr.Width / 2 - _list.MinimumSize.Width / 2f), Height + S(4)));
            }
            else
            {
                _list.MinimumSize = new Size(Width, 0);
                _list.Show(this, new Point(0, Height + S(4)));
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) _list.Dispose();
            base.Dispose(disposing);
        }
    }

    /// <summary>One line of small text (e.g. now playing).</summary>
    internal sealed class InfoLine : ThemedControl
    {
        private string _text = "";
        public InfoLine() { Height = S(18); }
        public string Line { get => _text; set { if (_text != value) { _text = value; Invalidate(); } } }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = Prepare(e);
            Theme.DrawText(g, _text, Theme.Small, Theme.Dim, ClientRectangle,
                           TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }
    }

    /// <summary>A thin scrollbar drawn inside the panel (instead of the white Windows one).</summary>
    internal sealed class ThinScrollBar : ThemedControl
    {
        private int _content = 1, _view = 1, _offset;
        private bool _drag;
        private int _dragStartY, _dragStartOffset;
        public event Action<int>? Scrolled;

        public ThinScrollBar() { Width = S(8); Cursor = Cursors.Hand; }

        public void Set(int content, int view, int offset)
        {
            _content = Math.Max(1, content); _view = Math.Max(1, view); _offset = offset;
            Invalidate();
        }

        private RectangleF Thumb
        {
            get
            {
                float track = Height;
                float h = Math.Max(F(28), track * _view / _content);
                float maxOff = Math.Max(1, _content - _view);
                float y = (track - h) * _offset / maxOff;
                return new RectangleF(F(1.5f), y, Width - F(3), h);
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = Prepare(e);
            if (_content <= _view) return;
            Theme.FillRound(g, Theme.Alpha(Color.White, _drag || Hovered ? 90 : 45), Thumb, F(3));
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            _drag = true; Capture = true;
            _dragStartY = e.Y; _dragStartOffset = _offset;
            if (!Thumb.Contains(e.Location))
            {
                // jump so the thumb centres on the click
                float frac = e.Y / (float)Height;
                Scrolled?.Invoke((int)(frac * _content - _view / 2f));
                _dragStartOffset = _offset;
            }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (!_drag) return;
            float perPixel = (_content - _view) / Math.Max(1f, Height - Thumb.Height);
            Scrolled?.Invoke(_dragStartOffset + (int)((e.Y - _dragStartY) * perPixel));
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            _drag = false; Capture = false; Invalidate();
        }
    }
}
