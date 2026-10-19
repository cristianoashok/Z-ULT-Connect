// StageView.cs
// ---------------------------------------------------------------------------
// The "screen" at the top of the panel.
//
//   Not connected  -> grey, still headphones ("Click to connect")
//   Connected      -> the 3D headphones come alive: they sway, float and glow
//   On your head   -> your companion appears wearing them (cross-fade)
//   Taken off      -> back to just the headphones
//
// The companion (golden retriever, cat or wolf dog) only acts up while the
// headphones are connected and on your head:
//   music playing  -> bobs its head to the music
//   now and then   -> smiles with its tongue out, or barks / meows / howls
//   put them on    -> says hello
//   click it       -> it barks back
//
// Shooting stars streak across the screen behind it while connected.
//
// All 3D frames are pre-rendered (Assets/render.py, Assets/companions.py) and
// played like a flip-book.
// ---------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Windows.Forms;
using ZUltConnect.Protocol;

namespace ZUltConnect.UI
{
    /// <summary>The companions you can pick in Settings.</summary>
    internal static class Companions
    {
        public static readonly (string Id, string Name, string Call)[] All =
        {
            ("golden", "Golden Retriever", "Woof!"),
            ("cat", "Cat", "Meow!"),
            ("wolf", "Wolf Dog", "Awoo!"),
            ("alien", "Alien", "Beep boop!"),
            ("none", "None (just headphones)", ""),
        };

        /// <summary>True if this companion's animation frames are built into the app.</summary>
        public static bool IsInstalled(string id) =>
            id == "none" || typeof(Companions).Assembly.GetManifestResourceInfo($"ZUltConnect.Assets.{id}_idle_00.png") != null;

        public static string CallOf(string id)
        {
            foreach (var c in All) if (c.Id == id) return c.Call;
            return "";
        }
    }

    internal static class Sprites
    {
        private static Bitmap[]? _hp;
        private static Bitmap? _grey;
        private static string _loadedCompanion = "";
        private static readonly Dictionary<string, Bitmap[]> _companion = new();

        public static Bitmap[] Headphones => _hp ??= Load("hp", 40);
        public static Bitmap? Grey => _grey ??= MakeGrey(Headphones.Length > 0 ? Headphones[0] : null);

        /// <summary>Frames for one animation of one companion (idle, music, happy, call).</summary>
        public static Bitmap[] Companion(string id, string set)
        {
            if (id != _loadedCompanion)
            {
                foreach (var frames in _companion.Values) foreach (var b in frames) b.Dispose();
                _companion.Clear();
                _loadedCompanion = id;
            }
            if (!_companion.TryGetValue(set, out var list))
                _companion[set] = list = Load($"{id}_{set}", 40);
            return list;
        }

        private static Bitmap[] Load(string name, int max)
        {
            var list = new List<Bitmap>();
            var asm = typeof(Sprites).Assembly;
            for (int i = 0; i < max; i++)
            {
                using var stream = asm.GetManifestResourceStream($"ZUltConnect.Assets.{name}_{i:00}.png");
                if (stream == null) break;
                using var img = new Bitmap(stream);
                list.Add(new Bitmap(img));
            }
            return list.ToArray();
        }

        private static Bitmap? MakeGrey(Bitmap? src)
        {
            if (src == null) return null;
            var bmp = new Bitmap(src.Width, src.Height);
            using var g = Graphics.FromImage(bmp);
            var cm = new ColorMatrix(new[]
            {
                new[] { 0.30f, 0.30f, 0.30f, 0, 0 },
                new[] { 0.59f, 0.59f, 0.59f, 0, 0 },
                new[] { 0.11f, 0.11f, 0.11f, 0, 0 },
                new[] { 0f, 0, 0, 0.55f, 0 },
                new[] { 0.10f, 0.10f, 0.11f, 0, 1 },
            });
            using var attrs = new ImageAttributes();
            attrs.SetColorMatrix(cm);
            g.DrawImage(src, new Rectangle(0, 0, src.Width, src.Height), 0, 0, src.Width, src.Height, GraphicsUnit.Pixel, attrs);
            return bmp;
        }
    }

    internal sealed class StageView : ThemedControl
    {
        // ---- what to show ----
        private bool _connected, _busy, _playing;
        private bool? _worn;
        private NoiseMode? _mode;
        private int? _battery;
        private bool _charging;
        private string _title = "ULT WEAR";
        private string _companion = "golden";
        private bool _showClose;

        public event Action? SettingsClicked;
        public event Action? CloseClicked;
        public event Action? ConnectClicked;
        public event Action<NoiseMode>? ModeClicked;

        // ---- animation ----
        private readonly Timer _timer = new() { Interval = 33 };
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private double _lastT;
        private float _wornBlend;   // 0 = headphones, 1 = companion
        private float _alive;       // 0 = grey/still, 1 = fully alive
        private readonly List<Star> _stars = new();
        private double _nextStar;
        private readonly Random _rng = new();
        private bool _animate;
        private int _hoverPill = -1;
        private bool _hoverMore, _hoverClose;

        // ---- companion behaviour ----
        private enum Act { None, Happy, Call }
        private Act _act = Act.None;
        private double _actStart, _nextRandomAct = 6, _greetAt = -1;
        private string _bubble = "";
        private double _bubbleStart = -10;
        private string _drawnSet = "", _prevSet = "";
        private int _prevFrame;
        private double _switchAt = -10;
        private NoiseMode? _lastMode;

        /// <summary>A shooting star: a short bright line that streaks across and fades away.</summary>
        private sealed class Star { public float X, Y, DX, DY, Speed, Length, Age, Life; }

        private static readonly string[] PillNames = { "Noise Cancelling", "Ambient", "Off" };
        private const double HappySeconds = 3.2, CallFps = 12;

        public StageView()
        {
            Height = S(204);
            _timer.Tick += (_, _) => Step();
        }

        public void Set(bool connected, bool busy, bool? worn, NoiseMode? mode, int? battery, bool charging,
                        string title, bool playing)
        {
            double t = _clock.Elapsed.TotalSeconds;
            bool wasWorn = _connected && _worn == true;
            bool isWorn = connected && worn == true;
            if (isWorn && !wasWorn) _greetAt = t + 0.6;             // say hello once it appears
            if (isWorn && mode != _lastMode && _lastMode != null && mode != null) StartAct(Act.Happy, t);
            _lastMode = mode;

            _connected = connected; _busy = busy; _worn = worn; _mode = mode;
            _battery = battery; _charging = charging; _title = title; _playing = playing;
            UpdateTimer();
            Invalidate();
        }

        public string Companion
        {
            get => _companion;
            set { _companion = value; _act = Act.None; _bubble = ""; _bubbleStart = -10; Invalidate(); }
        }

        /// <summary>Show a close (×) button - used when "close when clicking outside" is off.</summary>
        public bool ShowClose
        {
            get => _showClose;
            set { _showClose = value; Invalidate(); }
        }

        /// <summary>Only animate while the panel is on screen (saves battery).</summary>
        public bool Animate
        {
            get => _animate;
            set { _animate = value; UpdateTimer(); }
        }

        private bool CompanionShown => _connected && _worn == true && _companion != "none";

        private void UpdateTimer()
        {
            if (_animate && !IsDisposed) { if (!_timer.Enabled) { _lastT = _clock.Elapsed.TotalSeconds; _timer.Start(); } }
            else _timer.Stop();
        }

        // ===================================================================
        // Animation step
        // ===================================================================

        private void Step()
        {
            double t = _clock.Elapsed.TotalSeconds;
            float dt = (float)Math.Min(0.1, t - _lastT);
            _lastT = t;

            float targetWorn = _connected && _worn == true ? 1 : 0;
            _wornBlend = Approach(_wornBlend, targetWorn, dt / 0.45f);
            _alive = Approach(_alive, _connected ? 1 : 0, dt / 0.6f);

            UpdateBehaviour(t);

            // shooting stars: a new one every so often, each streaks across and fades
            if (_connected && t >= _nextStar && _stars.Count < 4)
            {
                _stars.Add(NewStar());
                _nextStar = t + 0.45 + _rng.NextDouble() * 1.1;
            }
            foreach (var st in _stars)
            {
                st.Age += dt;
                st.X += st.DX * st.Speed * dt;
                st.Y += st.DY * st.Speed * dt * Width / Math.Max(1f, Height);
            }
            _stars.RemoveAll(st => st.Age >= st.Life);
            Invalidate();
        }

        private void UpdateBehaviour(double t)
        {
            if (!CompanionShown) { _act = Act.None; _greetAt = -1; return; }

            if (_greetAt > 0 && t >= _greetAt) { _greetAt = -1; StartAct(Act.Call, t); }

            // finish the current act
            if (_act == Act.Happy && t - _actStart > HappySeconds) _act = Act.None;
            if (_act == Act.Call && (t - _actStart) * CallFps >= Math.Max(1, Sprites.Companion(_companion, "call").Length)) _act = Act.None;

            // now and then, do something cute (less often while music plays)
            if (_act == Act.None && t >= _nextRandomAct)
            {
                StartAct(_rng.NextDouble() < (_playing ? 0.3 : 0.6) ? Act.Happy : Act.Call, t);
            }
        }

        private void StartAct(Act act, double t)
        {
            _act = act;
            _actStart = t;
            _nextRandomAct = t + (_playing ? 14 : 8) + _rng.NextDouble() * 8;
            if (act == Act.Call)
            {
                _bubble = Companions.CallOf(_companion);
                _bubbleStart = t;
            }
        }

        private static float Approach(float v, float target, float step) =>
            v < target ? Math.Min(target, v + step) : Math.Max(target, v - step);

        private Star NewStar()
        {
            double angle = (200 + _rng.NextDouble() * 22) * Math.PI / 180;   // heading down and to the left
            return new Star
            {
                X = 0.35f + (float)_rng.NextDouble() * 0.8f,
                Y = -0.05f + (float)_rng.NextDouble() * 0.45f,
                DX = (float)Math.Cos(angle),
                DY = -(float)Math.Sin(angle),
                Speed = 0.75f + (float)_rng.NextDouble() * 0.6f,
                Length = 0.14f + (float)_rng.NextDouble() * 0.16f,
                Life = 0.7f + (float)_rng.NextDouble() * 0.5f,
            };
        }

        // ===================================================================
        // Geometry
        // ===================================================================

        private RectangleF Screen => new(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);

        private RectangleF ModelRect(float bob)
        {
            float h = Height - F(58);
            float w = h * 480f / 336f;
            return new RectangleF((Width - w) / 2, F(18) + bob, w, h);
        }

        private PointF Center
        {
            get { var r = ModelRect(0); return new PointF(r.X + r.Width / 2, r.Y + r.Height * 0.50f); }
        }

        private RectangleF MoreRect => new(Width - F(38), F(8), F(30), F(24));
        private RectangleF CloseRect => new(Width - F(70), F(8), F(28), F(24));

        private RectangleF[] PillRects()
        {
            float h = F(26), gap = F(4), y = Height - h - F(10);
            var widths = new float[3];
            float total = gap * 2;
            for (int i = 0; i < 3; i++)
            {
                widths[i] = TextRenderer.MeasureText(PillNames[i], Theme.Small).Width + F(20);
                total += widths[i];
            }
            float x = (Width - total) / 2;
            var rects = new RectangleF[3];
            for (int i = 0; i < 3; i++) { rects[i] = new RectangleF(x, y, widths[i], h); x += widths[i] + gap; }
            return rects;
        }

        // ===================================================================
        // Painting
        // ===================================================================

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = Prepare(e);
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            double t = _clock.Elapsed.TotalSeconds;
            float alive = _alive;

            var screen = Screen;
            using (var path = Theme.Round(screen, F(16)))
            {
                var top = Theme.Mix(Theme.Card, Theme.Glow, 0.07f * alive);
                using (var bg = new LinearGradientBrush(screen, top, Theme.Mix(Theme.Bg, Color.Black, 0.35f), 90f))
                    g.FillPath(bg, path);
                g.SetClip(path);

                var c = Center;
                if (alive > 0.01f)
                {
                    float pulse = 0.85f + 0.15f * (float)Math.Sin(t * 1.7);
                    float gr = Width * 0.42f;
                    using var glowPath = new GraphicsPath();
                    glowPath.AddEllipse(c.X - gr, c.Y - gr * 0.75f, gr * 2, gr * 1.5f);
                    using var glow = new PathGradientBrush(glowPath)
                    {
                        CenterColor = Theme.Alpha(Theme.Glow, (int)(95 * alive * pulse)),
                        SurroundColors = new[] { Theme.Alpha(Theme.Glow, 0) },
                    };
                    g.FillPath(glow, glowPath);
                    DrawStars(g, alive);
                }

                float bob = (float)Math.Sin(t * 1.6) * F(3) * alive;
                var model = ModelRect(bob);
                float sw = model.Width * (0.48f - bob / F(80));
                using (var shadowPath = new GraphicsPath())
                {
                    var sr = new RectangleF(c.X - sw / 2, model.Bottom - F(22), sw, F(13));
                    shadowPath.AddEllipse(sr);
                    using var shadow = new PathGradientBrush(shadowPath)
                    {
                        CenterColor = Color.FromArgb(alive > 0.5f ? 120 : 70, 0, 0, 0),
                        SurroundColors = new[] { Color.FromArgb(0, 0, 0, 0) },
                    };
                    g.FillPath(shadow, shadowPath);
                }

                DrawModel(g, model, t, alive);
                DrawBubble(g, model, t);
                g.ResetClip();
            }

            DrawOverlays(g);
        }

        private void DrawStars(Graphics g, float alive)
        {
            Color tint = Theme.Mix(Color.White, Theme.Accent, 0.35f);
            foreach (var st in _stars)
            {
                float fade = Math.Clamp(st.Age / 0.12f, 0, 1) * Math.Clamp((st.Life - st.Age) / (st.Life * 0.45f), 0, 1);
                int a = (int)(230 * fade * alive);
                if (a < 4) continue;
                var head = new PointF(st.X * Width, st.Y * Height);
                var tail = new PointF(head.X - st.DX * st.Length * Width, head.Y - st.DY * st.Length * Width);
                using (var brush = new LinearGradientBrush(tail, head, Theme.Alpha(tint, 0), Theme.Alpha(tint, a)))
                using (var pen = new Pen(brush, F(1.6f)) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                    g.DrawLine(pen, tail, head);
                float r = F(2.2f);
                using (var glow = new SolidBrush(Theme.Alpha(Color.White, (int)(a * 0.9f))))
                    g.FillEllipse(glow, head.X - r, head.Y - r, r * 2, r * 2);
            }
        }

        /// <summary>Which companion animation should play right now, and which frame of it.</summary>
        private (string Set, int Frame) CompanionFrame(double t)
        {
            switch (_act)
            {
                case Act.Call:
                    {
                        int n = Math.Max(1, Sprites.Companion(_companion, "call").Length);
                        return ("call", Math.Min(n - 1, (int)((t - _actStart) * CallFps)));
                    }
                case Act.Happy:
                    return ("happy", (int)((t - _actStart) * 12));
                default:
                    return _playing ? ("music", (int)(t * 12)) : ("idle", (int)(t * 9));
            }
        }

        private void DrawModel(Graphics g, RectangleF r, double t, float alive)
        {
            var hp = Sprites.Headphones;
            if (hp.Length == 0) return;

            if (alive < 0.99f && Sprites.Grey is Bitmap grey)
                DrawWithAlpha(g, grey, r, 1 - alive);
            if (alive <= 0.01f) return;

            float compAlpha = _companion == "none" ? 0 : alive * _wornBlend;
            float hpAlpha = alive - compAlpha;
            if (hpAlpha > 0.01f) DrawWithAlpha(g, hp[(int)(t * 12) % hp.Length], r, hpAlpha);
            if (compAlpha <= 0.01f) return;

            var (set, frame) = CompanionFrame(t);
            if (set != _drawnSet)
            {
                // cross-fade briefly between animations so the head doesn't jump
                _prevSet = _drawnSet;
                _prevFrame = _lastFrame;
                _switchAt = t;
                _drawnSet = set;
            }
            var frames = Sprites.Companion(_companion, set);
            if (frames.Length == 0) return;
            int index = ((frame % frames.Length) + frames.Length) % frames.Length;
            _lastFrame = index;

            float k = (float)Math.Clamp((t - _switchAt) / 0.22, 0, 1);
            if (k < 1 && _prevSet != "")
            {
                var prev = Sprites.Companion(_companion, _prevSet);
                if (prev.Length > 0) DrawWithAlpha(g, prev[Math.Min(_prevFrame, prev.Length - 1)], r, compAlpha * (1 - k));
            }
            DrawWithAlpha(g, frames[index], r, compAlpha * (k < 1 && _prevSet != "" ? k : 1));
        }

        private int _lastFrame;

        private void DrawBubble(Graphics g, RectangleF model, double t)
        {
            double age = t - _bubbleStart;
            if (!CompanionShown || age > 1.6 || _bubble == "") return;
            float a = (float)Math.Clamp(Math.Min(age / 0.12, (1.6 - age) / 0.35), 0, 1);
            float pop = 0.85f + 0.15f * (float)Math.Min(1, age / 0.15);
            var size = TextRenderer.MeasureText(_bubble, Theme.Strong);
            float bw = (size.Width + F(16)) * pop, bh = F(24) * pop;
            float x = model.Right - model.Width * 0.22f, y = model.Top + model.Height * 0.08f;
            var r = new RectangleF(x, y, bw, bh);
            Theme.FillRound(g, Theme.Alpha(Color.White, (int)(235 * a)), r, bh / 2);
            using (var tail = new SolidBrush(Theme.Alpha(Color.White, (int)(235 * a))))
                g.FillPolygon(tail, new[] { new PointF(r.X + F(8), r.Bottom - F(4)), new PointF(r.X + F(18), r.Bottom - F(2)), new PointF(r.X - F(2), r.Bottom + F(7)) });
            Theme.DrawText(g, _bubble, Theme.Strong, Theme.Alpha(Color.FromArgb(25, 28, 34), (int)(255 * a)), Rectangle.Round(r),
                           TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }

        private static void DrawWithAlpha(Graphics g, Bitmap img, RectangleF r, float alpha)
        {
            if (alpha >= 0.99f) { g.DrawImage(img, r); return; }
            var cm = new ColorMatrix { Matrix33 = Math.Clamp(alpha, 0, 1) };
            using var attrs = new ImageAttributes();
            attrs.SetColorMatrix(cm);
            g.DrawImage(img, Rectangle.Round(r), 0, 0, img.Width, img.Height, GraphicsUnit.Pixel, attrs);
        }

        private void DrawOverlays(Graphics g)
        {
            int pad = S(14);

            Theme.DrawText(g, "Z ULT CONNECT", Theme.Caption, Theme.Alpha(Theme.Accent, 230), new Rectangle(pad, S(10), S(180), S(14)));
            Theme.DrawText(g, _title, Theme.Strong, Theme.Text, new Rectangle(pad, S(24), S(180), S(18)));
            string status = !_connected ? (_busy ? "Connecting…" : "Not connected")
                          : _worn == true ? "On your head" : _worn == false ? "Taken off" : "Connected";
            Color dot = !_connected ? (_busy ? Theme.Orange : Theme.Faint) : Theme.Good;
            using (var b = new SolidBrush(dot)) g.FillEllipse(b, pad, F(47) - F(3.5f), F(7), F(7));
            Theme.DrawText(g, status, Theme.Small, Theme.Dim, new Rectangle(pad + S(12), S(40), S(170), S(14)));

            // top-right: battery, ×, •••
            var more = MoreRect;
            if (_hoverMore) Theme.FillRound(g, Theme.Alpha(Color.White, 22), more, F(8));
            using (var b = new SolidBrush(Theme.Text))
                for (int i = -1; i <= 1; i++)
                    g.FillEllipse(b, more.X + more.Width / 2 + i * F(6) - F(1.6f), more.Y + more.Height / 2 - F(1.6f), F(3.2f), F(3.2f));

            float batteryRight = more.Left - F(6);
            if (_showClose)
            {
                var close = CloseRect;
                if (_hoverClose) Theme.FillRound(g, Theme.Alpha(Color.White, 22), close, F(8));
                float cx = close.X + close.Width / 2, cy = close.Y + close.Height / 2, d = F(4.5f);
                using var pen = new Pen(Theme.Text, F(1.6f)) { StartCap = LineCap.Round, EndCap = LineCap.Round };
                g.DrawLine(pen, cx - d, cy - d, cx + d, cy + d);
                g.DrawLine(pen, cx - d, cy + d, cx + d, cy - d);
                batteryRight = close.Left - F(4);
            }

            if (_connected && _battery is int pct)
            {
                string text = $"{pct}%";
                var size = TextRenderer.MeasureText(text, Theme.Small);
                float bw = F(20), bh = F(10);
                float textX = batteryRight - size.Width;
                var body = new RectangleF(textX - F(6) - bw, more.Y + more.Height / 2 - bh / 2, bw, bh);
                using (var pen = new Pen(Theme.Dim, F(1.2f)))
                using (var outline = Theme.Round(body, F(2.5f)))
                    g.DrawPath(pen, outline);
                using (var cap = new SolidBrush(Theme.Dim)) g.FillRectangle(cap, body.Right + F(0.5f), body.Y + bh * 0.3f, F(2), bh * 0.4f);
                Color fill = _charging ? Theme.Good : pct <= 20 ? Theme.Bad : Theme.Text;
                var level = new RectangleF(body.X + F(2), body.Y + F(2), (bw - F(4)) * pct / 100f, bh - F(4));
                if (level.Width > 0.5f) Theme.FillRound(g, fill, level, F(1.5f));
                Theme.DrawText(g, text, Theme.Small, Theme.Text,
                               new Rectangle((int)textX, (int)more.Y, size.Width + S(2), (int)more.Height));
            }

            if (_connected)
            {
                var rects = PillRects();
                var container = RectangleF.FromLTRB(rects[0].Left - F(4), rects[0].Top - F(4), rects[2].Right + F(4), rects[0].Bottom + F(4));
                Theme.FillRound(g, Theme.Alpha(Color.Black, 70), container, container.Height / 2);
                int selected = _mode switch { NoiseMode.NoiseCancelling => 0, NoiseMode.Ambient => 1, NoiseMode.Off => 2, _ => -1 };
                for (int i = 0; i < 3; i++)
                {
                    var r = rects[i];
                    bool sel = i == selected;
                    if (sel && i == 2) Theme.FillRound(g, Theme.Alpha(Color.White, 46), r, r.Height / 2);
                    else if (sel) Theme.FillAccent(g, r, r.Height / 2);
                    else if (i == _hoverPill) Theme.FillRound(g, Theme.Alpha(Color.White, 20), r, r.Height / 2);
                    Theme.DrawText(g, PillNames[i], sel ? Theme.Strong : Theme.Small, sel ? Color.White : Theme.Dim,
                                   Rectangle.Round(r), TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                }
            }
            else
            {
                string hint = _busy ? "Looking for your ULT Wear…" : "Click to connect";
                Theme.DrawText(g, hint, Theme.Small, Theme.Dim, new Rectangle(0, Height - S(36), Width, S(22)),
                               TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            }
        }

        // ===================================================================
        // Mouse
        // ===================================================================

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            bool more = MoreRect.Contains(e.Location);
            bool close = _showClose && CloseRect.Contains(e.Location);
            int pill = -1;
            if (_connected)
            {
                var rects = PillRects();
                for (int i = 0; i < 3; i++) if (rects[i].Contains(e.Location)) pill = i;
            }
            if (more != _hoverMore || pill != _hoverPill || close != _hoverClose)
            {
                _hoverMore = more; _hoverPill = pill; _hoverClose = close;
                Invalidate();
            }
            bool overModel = ModelRect(0).Contains(e.Location);
            Cursor = more || close || pill >= 0 || overModel ? Cursors.Hand : Cursors.Default;
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            _hoverMore = false; _hoverPill = -1; _hoverClose = false;
            base.OnMouseLeave(e);
        }

        protected override void OnMouseClick(MouseEventArgs e)
        {
            base.OnMouseClick(e);
            if (e.Button != MouseButtons.Left) return;
            if (MoreRect.Contains(e.Location)) { SettingsClicked?.Invoke(); return; }
            if (_showClose && CloseRect.Contains(e.Location)) { CloseClicked?.Invoke(); return; }

            if (!_connected)
            {
                if (!_busy) ConnectClicked?.Invoke();
                return;
            }

            var rects = PillRects();
            for (int i = 0; i < 3; i++)
                if (rects[i].Contains(e.Location)) { ModeClicked?.Invoke(ModeFor(i)); return; }

            if (ModelRect(0).Contains(e.Location))
            {
                if (CompanionShown)
                {
                    StartAct(Act.Call, _clock.Elapsed.TotalSeconds);   // pet it -> it barks back
                    return;
                }
                var next = _mode switch
                {
                    NoiseMode.NoiseCancelling => NoiseMode.Ambient,
                    NoiseMode.Ambient => NoiseMode.Off,
                    _ => NoiseMode.NoiseCancelling,
                };
                ModeClicked?.Invoke(next);
            }
        }

        private static NoiseMode ModeFor(int i) => i switch { 0 => NoiseMode.NoiseCancelling, 1 => NoiseMode.Ambient, _ => NoiseMode.Off };

        // ---- for screenshots/tests ----
        public void SettleForSnapshot()
        {
            _alive = _connected ? 1 : 0;
            _wornBlend = _connected && _worn == true ? 1 : 0;
            _switchAt = -10;
            Invalidate();
        }

        public void ForceAct(string act)
        {
            double t = _clock.Elapsed.TotalSeconds;
            if (act == "call") { StartAct(Act.Call, t); _actStart = t - 2 / CallFps; }
            else if (act == "happy") StartAct(Act.Happy, t);
            else _act = Act.None;
            _switchAt = -10;
            _drawnSet = "";
            _prevSet = "";
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) _timer.Dispose();
            base.Dispose(disposing);
        }
    }
}
