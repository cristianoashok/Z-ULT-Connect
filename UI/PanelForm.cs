// PanelForm.cs
// ---------------------------------------------------------------------------
// The pop-up panel that opens from the tray icon. Top to bottom:
//
//   Screen    - animated 3D headphones / companion, battery, ••• (settings), mode pills
//   Ambient   - strength + focus on voice (only in Ambient mode)
//   Equalizer - the selected preset in a small pill, then the draggable EQ curve
//   ULT       - ULT off / ULT 1 / ULT 2
//   Volume, Now playing, Prev / Play / Next   (DSEE lives in Settings)
//
// The whole panel fades to green (Noise Cancelling), blue (Ambient) or black (Off).
// ••• swaps to the Settings page. If the content is taller than the screen,
// a thin scrollbar appears inside the panel (mouse wheel works anywhere).
// ---------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Forms;
using ZUltConnect.App;
using ZUltConnect.Protocol;

namespace ZUltConnect.UI
{
    internal sealed class PanelForm : Form
    {
        private readonly HeadphoneConnection _conn;
        private readonly Action _showLog, _quit;
        private readonly FlowLayoutPanel _main, _settings;
        private readonly ThinScrollBar _scrollBar = new();
        private readonly List<Control> _needsConnection = new();

        // main page
        private readonly StageView _stage = new();
        private readonly Panel _ambientGroup = new();
        private readonly CaptionView _ambientCaption = new("AMBIENT STRENGTH");
        private readonly FlatSlider _ambientLevel = new(1, 20);
        private readonly ToggleRow _voice = new("Focus on voice");
        private readonly DropdownButton _preset = new("EQUALIZER", EqPresets.All) { Pill = true };
        private readonly EqCurve _eq = new();
        private readonly Segmented _ult = new("ULT off", "ULT 1", "ULT 2") { MutedIndex = 0 };
        private readonly ToggleRow _dsee = new("DSEE (restores detail in music)");
        private readonly CaptionView _volumeCaption = new("VOLUME");
        private readonly FlatSlider _volume = new(0, 30);
        private readonly InfoLine _nowPlaying = new();
        private readonly FlatButton _prev = new("Prev");
        private readonly FlatButton _playPause = new("Play");
        private readonly FlatButton _next = new("Next");

        // settings page
        private readonly FlatButton _back = new("‹  Back") { Subtle = true, AlignLeft = true };
        private readonly DropdownButton _companion = new("COMPANION",
            Companions.All.Select((c, i) => ((byte)i, c.Name)).Where((c, i) => Companions.IsInstalled(Companions.All[i].Id)).ToArray());
        private readonly ToggleRow _closeOutside = new("Close when clicking outside");
        private readonly ToggleRow _startWithWindows = new("Start with Windows");
        private readonly ToggleRow _pauseWhenOff = new("Pause music when taken off");
        private readonly DropdownButton _autoOff = new("AUTO POWER OFF", AutoPowerOffChoices.All);
        private readonly InfoLine _deviceInfo = new();
        private readonly FlatButton _connectBtn = new("Connect");
        private readonly FlatButton _logBtn = new("Show log");
        private readonly FlatButton _powerOffBtn = new("Turn off headphones") { TextColor = Color.FromArgb(255, 140, 140) };
        private readonly FlatButton _quitBtn = new("Quit Z ULT Connect");

        // color fade
        private readonly Timer _fade = new() { Interval = 15 };
        private Theme.Palette _fadeFrom, _fadeTo;
        private DateTime _fadeStart;
        private string _paletteKey = "";

        private int PanelWidth => Theme.S(340);
        private int Pad => Theme.S(12);
        private int Inner => PanelWidth - Pad * 2;
        private bool _ambientShown = true;
        private bool _onSettings;
        private int _scroll, _contentHeight, _viewHeight;

        public DateTime HiddenAt { get; private set; }

        /// <summary>Set false by the screenshot harness; the user's choice lives in AppSettings.</summary>
        public bool AutoHide { get; set; } = true;

        public PanelForm(HeadphoneConnection conn, Action showLog, Action quit)
        {
            _conn = conn;
            _showLog = showLog;
            _quit = quit;

            Text = "Z ULT Connect";
            Icon = AppIcon.Window;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            AutoScaleMode = AutoScaleMode.None;
            BackColor = Theme.Bg;
            ForeColor = Theme.Text;
            KeyPreview = true;
            DoubleBuffered = true;

            _main = NewPage();
            _settings = NewPage();
            _settings.Visible = false;
            Controls.Add(_main);
            Controls.Add(_settings);
            Controls.Add(_scrollBar);
            _scrollBar.BringToFront();
            _scrollBar.Scrolled += ScrollTo;

            BuildMain();
            BuildSettings();
            Wire();
            HookMouseWheel(this);
            Relayout();
            Redraw();

            _fade.Tick += (_, _) => FadeStep();
            _conn.Changed += Redraw;
            _conn.StateChanged += Redraw;
            VisibleChanged += (_, _) => _stage.Animate = Visible;
        }

        private FlowLayoutPanel NewPage() => new()
        {
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoScroll = false,
            BackColor = Theme.Bg,
            Padding = new Padding(Pad, Pad, Pad, Theme.S(10)),
            Location = new Point(0, 0),
        };

        // ===================================================================
        // Layout
        // ===================================================================

        private static void Add(FlowLayoutPanel page, Control c, int gapAbove, int width)
        {
            c.Width = width;
            c.Margin = new Padding(0, Theme.S(gapAbove), 0, 0);
            page.Controls.Add(c);
        }

        private void BuildMain()
        {
            Add(_main, _stage, 0, Inner);

            _ambientGroup.BackColor = Theme.Bg;
            _ambientGroup.Width = Inner;
            foreach (var c in new Control[] { _ambientCaption, _ambientLevel, _voice }) c.Width = Inner;
            _ambientCaption.Location = new Point(0, 0);
            _ambientLevel.Location = new Point(0, _ambientCaption.Bottom);
            _voice.Location = new Point(0, _ambientLevel.Bottom);
            _ambientGroup.Height = _voice.Bottom;
            _ambientGroup.Controls.AddRange(new Control[] { _ambientCaption, _ambientLevel, _voice });
            Add(_main, _ambientGroup, 8, Inner);

            // the selected preset, as a small pill in the middle
            _preset.Height = Theme.S(30);
            Add(_main, _preset, 8, Inner);
            Add(_main, _eq, 6, Inner);
            Add(_main, _ult, 8, Inner);
            Add(_main, _volumeCaption, 6, Inner);
            Add(_main, _volume, 0, Inner);
            Add(_main, _nowPlaying, 2, Inner);

            var transport = new TableLayoutPanel { ColumnCount = 3, RowCount = 1, Height = Theme.S(30), BackColor = Theme.Bg, Margin = Padding.Empty };
            transport.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 30));
            transport.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 40));
            transport.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 30));
            foreach (var b in new[] { _prev, _playPause, _next })
            {
                b.Dock = DockStyle.Fill;
                b.Margin = new Padding(Theme.S(3), 0, Theme.S(3), 0);
                transport.Controls.Add(b);
            }
            Add(_main, transport, 4, Inner);

            _needsConnection.AddRange(new Control[] { _ambientLevel, _voice, _preset, _eq, _ult, _dsee, _volume, _prev, _playPause, _next, _pauseWhenOff, _autoOff, _powerOffBtn });
        }

        private void BuildSettings()
        {
            var header = new Panel { Height = Theme.S(32), BackColor = Theme.Bg };
            var title = new CaptionView("SETTINGS") { Dock = DockStyle.Fill, Padding = new Padding(Theme.S(8), 0, 0, 0) };
            _back.Dock = DockStyle.Left;
            _back.Width = Theme.S(80);
            header.Controls.Add(title);
            header.Controls.Add(_back);
            Add(_settings, header, 0, Inner);

            Add(_settings, new CaptionView("APP"), 10, Inner);
            Add(_settings, _companion, 4, Inner);
            Add(_settings, _closeOutside, 8, Inner);
            Add(_settings, _startWithWindows, 2, Inner);

            Add(_settings, new CaptionView("HEADPHONES"), 14, Inner);
            Add(_settings, _dsee, 2, Inner);
            Add(_settings, _pauseWhenOff, 4, Inner);
            Add(_settings, _autoOff, 6, Inner);
            Add(_settings, _deviceInfo, 8, Inner);

            Add(_settings, _connectBtn, 14, Inner);
            Add(_settings, _logBtn, 6, Inner);
            Add(_settings, _powerOffBtn, 6, Inner);
            Add(_settings, _quitBtn, 6, Inner);
        }

        private int PageHeight(FlowLayoutPanel page)
        {
            // Note: c.Visible reads false for every child while the panel itself is hidden,
            // so the optional ambient section is tracked separately.
            int h = page.Padding.Vertical;
            foreach (Control c in page.Controls)
                if (c != _ambientGroup || _ambientShown) h += c.Height + c.Margin.Vertical;
            return h;
        }

        private void Relayout()
        {
            var page = _onSettings ? _settings : _main;
            int mainHeight = PageHeight(_main);
            _contentHeight = PageHeight(page);
            int wanted = Math.Max(_contentHeight, _onSettings ? mainHeight : 0);

            var area = System.Windows.Forms.Screen.FromPoint(Visible ? Location : Cursor.Position).WorkingArea;
            int max = MaxHeightOverride > 0 ? MaxHeightOverride : area.Height - Theme.S(24);
            _viewHeight = Math.Min(wanted, max);
            ClientSize = new Size(PanelWidth, _viewHeight);

            foreach (var p in new[] { _main, _settings })
                p.Size = new Size(PanelWidth, Math.Max(PageHeight(p), _viewHeight));

            bool scroll = _contentHeight > _viewHeight;
            _scrollBar.Visible = scroll;
            _scrollBar.Bounds = new Rectangle(PanelWidth - Theme.S(11), Theme.S(8), Theme.S(8), _viewHeight - Theme.S(16));
            ScrollTo(_scroll);
            if (Visible) PlaceNearTray();
        }

        private void ScrollTo(int offset)
        {
            var page = _onSettings ? _settings : _main;
            _scroll = Math.Clamp(offset, 0, Math.Max(0, _contentHeight - _viewHeight));
            page.Top = -_scroll;
            _scrollBar.Set(_contentHeight, _viewHeight, _scroll);
        }

        /// <summary>Mouse wheel scrolls the panel wherever the pointer is.</summary>
        private void HookMouseWheel(Control parent)
        {
            parent.MouseWheel += OnWheel;
            foreach (Control c in parent.Controls) HookMouseWheel(c);
        }

        private void OnWheel(object? sender, MouseEventArgs e)
        {
            if (_contentHeight <= _viewHeight) return;
            ScrollTo(_scroll - Math.Sign(e.Delta) * Theme.S(60));
            if (e is HandledMouseEventArgs h) h.Handled = true;
        }

        public void ShowNearTray()
        {
            Relayout();
            PlaceNearTray();
            Show();
            Activate();
        }

        private void PlaceNearTray()
        {
            var screen = System.Windows.Forms.Screen.FromPoint(Cursor.Position);
            Rectangle wa = screen.WorkingArea, full = screen.Bounds;
            int m = Theme.S(12);
            int x = wa.Right - Width - m, y = wa.Bottom - Height - m;
            if (wa.Top > full.Top) y = wa.Top + m;
            else if (wa.Left > full.Left) x = wa.Left + m;
            Location = new Point(x, y);
        }

        private void ShowPage(bool settings)
        {
            if (_onSettings == settings) return;
            _onSettings = settings;
            _settings.Visible = settings;
            _main.Visible = !settings;
            _scroll = 0;
            Relayout();
            Redraw();
        }

        // ===================================================================
        // Window behaviour
        // ===================================================================

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ClassStyle |= 0x20000;   // CS_DROPSHADOW
                cp.ExStyle |= 0x80;         // WS_EX_TOOLWINDOW: no Alt+Tab entry
                return cp;
            }
        }

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            try
            {
                int round = 2; // Windows 11 rounded corners (ignored on Windows 10)
                DwmSetWindowAttribute(Handle, 33, ref round, sizeof(int));
                int noBorder = unchecked((int)0xFFFFFFFE); // no window outline (Windows 11)
                DwmSetWindowAttribute(Handle, 34, ref noBorder, sizeof(int));
            }
            catch { }
        }

        protected override void OnDeactivate(EventArgs e)
        {
            base.OnDeactivate(e);
            if (!Visible || !AutoHide || !AppSettings.CloseOnClickOutside) return;
            Hide();
            HiddenAt = DateTime.UtcNow;
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.KeyCode == Keys.Escape) { if (_onSettings) ShowPage(false); else Hide(); }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; Hide(); }
            base.OnFormClosing(e);
        }

        // ===================================================================
        // Controls -> headphones
        // ===================================================================

        private Session? S => _conn.Session;

        private static void Fire(Func<Task>? action)
        {
            if (action == null) return;
            action().ContinueWith(t => AppLog.Add("• " + t.Exception?.GetBaseException().Message),
                                  TaskContinuationOptions.OnlyOnFaulted);
        }

        private void Wire()
        {
            _stage.SettingsClicked += () => ShowPage(true);
            _stage.CloseClicked += () => { Hide(); HiddenAt = DateTime.UtcNow; };
            _stage.ConnectClicked += () => Fire(_conn.ConnectAsync);
            _stage.ModeClicked += mode =>
            {
                S?.SetNoiseMode(mode);
                Redraw();
            };

            _ambientLevel.ValueChanging += v => _ambientCaption.Value = v.ToString();
            _ambientLevel.ValueCommitted += v => S?.SetAmbientLevel((byte)v);
            _voice.Toggled += on => S?.SetVoicePassthrough(on);

            _preset.ChoicePicked += id => { var s = S; if (s != null) Fire(() => s.SetEqPreset(id)); };
            _eq.BandsCommitted += SendBands;
            _eq.ResetClicked += () =>
            {
                var flat = new int[6];
                _eq.SetValues(flat, animate: true);
                SendBands(flat);
            };
            _ult.SelectionChanged += i => { var s = S; if (s != null) Fire(() => s.SetUltMode((byte)i)); };
            _dsee.Toggled += on => S?.SetDsee(on);

            _volume.ValueChanging += v => _volumeCaption.Value = $"{v} / 30";
            _volume.ValueCommitted += v => S?.SetVolume(v);
            _prev.Click += (_, _) => S?.Previous();
            _next.Click += (_, _) => S?.Next();
            _playPause.Click += (_, _) => { S?.PlayPause(); Redraw(); };

            // settings
            _back.Click += (_, _) => ShowPage(false);
            _companion.ChoicePicked += i =>
            {
                AppSettings.Companion = Companions.All[i].Id;
                _stage.Companion = AppSettings.Companion;
            };
            _closeOutside.Toggled += on =>
            {
                AppSettings.CloseOnClickOutside = on;
                _stage.ShowClose = !on;
            };
            _pauseWhenOff.Toggled += on => S?.SetPauseWhenRemoved(on);
            _autoOff.ChoicePicked += id => S?.SetAutoPowerOff(id);
            _connectBtn.Click += (_, _) =>
            {
                if (_conn.IsConnected) _conn.Disconnect("Disconnected");
                else Fire(_conn.ConnectAsync);
            };
            _logBtn.Click += (_, _) => _showLog();
            _powerOffBtn.Click += (_, _) =>
            {
                bool keep = AutoHide;
                AutoHide = false;
                if (MessageBox.Show(this, "Turn the headphones off?", "Z ULT Connect",
                        MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
                    S?.PowerOff();
                AutoHide = keep;
            };
            _quitBtn.Click += (_, _) => _quit();

            // saved choices
            _preset.Placeholder = AppSettings.LastPreset >= 0 ? EqPresets.NameOf((byte)AppSettings.LastPreset) : "Off";
            if (!Companions.IsInstalled(AppSettings.Companion)) AppSettings.Companion = "golden";
            _stage.Companion = AppSettings.Companion;
            _companion.Selected = (byte)Math.Max(0, Array.FindIndex(Companions.All, c => c.Id == AppSettings.Companion));
            _closeOutside.Checked = AppSettings.CloseOnClickOutside;
            _startWithWindows.Checked = AppSettings.StartWithWindows;
            _startWithWindows.Toggled += on => AppSettings.StartWithWindows = on;
            _stage.ShowClose = !AppSettings.CloseOnClickOutside;
        }

        private void SendBands(int[] values)
        {
            var s = S;
            if (s == null) return;
            _preset.Selected = 0xA0; // "Custom"
            var raw = values.Select(v => (byte)Math.Clamp(v + 10, 0, 20)).ToArray();
            Fire(() => s.SetCustomBands(raw));
        }

        private void SetAmbientVisible(bool visible)
        {
            if (_ambientShown == visible) return;
            _ambientShown = visible;
            _ambientGroup.Visible = visible;
            Relayout();
        }

        // ===================================================================
        // Colors: fade the whole panel between black / green / blue
        // ===================================================================

        private void ApplyPalette(bool connected, NoiseMode? mode, bool animate)
        {
            var target = !connected ? Theme.Black : mode switch
            {
                NoiseMode.NoiseCancelling => Theme.Green,
                NoiseMode.Ambient => Theme.Blue,
                _ => Theme.Black,
            };
            string key = $"{connected}{mode}";
            if (key == _paletteKey) return;
            _paletteKey = key;
            if (!animate || !Visible)
            {
                Theme.Current = target;
                Repaint();
                return;
            }
            _fadeFrom = Theme.Current;
            _fadeTo = target;
            _fadeStart = DateTime.UtcNow;
            _fade.Start();
        }

        private void FadeStep()
        {
            float t = (float)Math.Min(1, (DateTime.UtcNow - _fadeStart).TotalMilliseconds / 450);
            float ease = 1 - (1 - t) * (1 - t) * (1 - t);
            Theme.Current = Theme.Palette.Lerp(_fadeFrom, _fadeTo, ease);
            Repaint();
            if (t >= 1) _fade.Stop();
        }

        private void Repaint()
        {
            BackColor = Theme.Bg;
            SetBackColors(this);
            Invalidate(true);
        }

        private static void SetBackColors(Control parent)
        {
            foreach (Control c in parent.Controls)
            {
                c.BackColor = Theme.Bg;
                if (c.HasChildren) SetBackColors(c);
            }
        }

        // ===================================================================
        // Headphones -> controls
        // ===================================================================

        private void Redraw()
        {
            if (IsDisposed) return;
            var s = S?.State;
            bool on = _conn.IsConnected && s != null;

            _stage.Set(on, _conn.IsBusy, s?.Worn, on ? s!.Noise : null, on ? s!.BatteryPercent : null,
                       s?.Charging ?? false, s?.ModelName ?? _conn.DeviceName ?? "ULT WEAR", on && s!.Playing == true);
            ApplyPalette(on, on ? s!.Noise : null, animate: true);

            foreach (var c in _needsConnection) c.Enabled = on;
            _connectBtn.Label = on ? "Disconnect" : _conn.IsBusy ? "Connecting…" : "Connect";
            _connectBtn.Enabled = !_conn.IsBusy;

            if (!on)
            {
                _eq.ClearValues(_conn.IsBusy ? "Connecting…" : "EQ appears when connected");
                _nowPlaying.Line = _conn.Status;
                _deviceInfo.Line = "";
                SetAmbientVisible(false);
                return;
            }

            SetAmbientVisible(s!.Noise == NoiseMode.Ambient);
            if (!_ambientLevel.Dragging) _ambientLevel.Value = s.AmbientLevel;
            _ambientCaption.Value = _ambientLevel.Value.ToString();
            _voice.Checked = s.VoicePassthrough;

            _preset.Selected = s.EqPreset;
            if (s.EqPreset is byte seen) AppSettings.LastPreset = seen;
            var bands = s.CurrentBands;
            if (bands.Length == 6) _eq.SetValues(bands.Select(b => b - 10).ToArray(), animate: true);
            else if (bands.Length > 0) _eq.ClearValues($"Custom EQ not supported yet for {bands.Length} bands");
            else _eq.ClearValues("Reading EQ…");
            _eq.Enabled = bands.Length == 6;

            _ult.Enabled = s.UltMode != null;
            _ult.SelectedIndex = s.UltMode is byte u && u <= 2 ? u : -1;

            _dsee.Enabled = s.Dsee != null;
            _dsee.Checked = s.Dsee ?? false;

            if (!_volume.Dragging && s.Volume is int vol) _volume.Value = vol;
            _volumeCaption.Value = s.Volume is int v ? $"{v} / 30" : "";

            _nowPlaying.Line = s.TrackTitle == null ? "Nothing playing"
                : "♪  " + s.TrackTitle + (s.TrackArtist != null ? "  ·  " + s.TrackArtist : "");
            _playPause.Label = s.Playing == true ? "Pause" : "Play";

            _pauseWhenOff.Enabled = s.PauseWhenRemoved != null;
            _pauseWhenOff.Checked = s.PauseWhenRemoved ?? false;
            var choices = AutoPowerOffChoices.All.ToList();
            if (s.AutoPowerOffWearingVariant) choices.Insert(1, (AutoPowerOffChoices.WhenTakenOff, "When taken off"));
            _autoOff.SetChoices(choices.ToArray());
            _autoOff.Selected = s.AutoPowerOff;
            _autoOff.Enabled = s.AutoPowerOff != null;
            _deviceInfo.Line = $"{s.ModelName ?? "Sony headphones"}  ·  firmware {s.FirmwareVersion ?? "?"}";
        }

        // ---- for screenshots/tests ----
        internal void SettleForSnapshot()
        {
            _fade.Stop();
            _paletteKey = "";
            var s = S?.State;
            bool on = _conn.IsConnected && s != null;
            ApplyPalette(on, on ? s!.Noise : null, animate: false);
            _stage.SettleForSnapshot();
        }

        internal void OpenSettingsForSnapshot(bool open) => ShowPage(open);
        internal StageView StageForSnapshot => _stage;
        internal void ScrollForSnapshot(int offset) => ScrollTo(offset);
        internal int MaxHeightOverride { get; set; }
        internal void RelayoutForSnapshot() => Relayout();

        protected override void Dispose(bool disposing)
        {
            if (disposing) _fade.Dispose();
            base.Dispose(disposing);
        }
    }
}
