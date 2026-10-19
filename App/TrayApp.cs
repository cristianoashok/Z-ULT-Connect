// TrayApp.cs
// ---------------------------------------------------------------------------
// The app itself: lives in the system tray (by the clock).
//   Left-click the icon  -> open / close the panel
//   Right-click the icon -> quick menu (noise mode, settings, log, quit)
// The icon's ear cups show the noise mode, and hovering shows the battery.
// ---------------------------------------------------------------------------
using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows.Forms;
using ZUltConnect.Protocol;
using ZUltConnect.UI;

namespace ZUltConnect.App
{
    internal sealed class TrayApp : ApplicationContext
    {
        private readonly NotifyIcon _tray;
        private readonly HeadphoneConnection _conn;
        private readonly PanelForm _panel;
        private readonly ContextMenuStrip _menu;
        private readonly System.Windows.Forms.Timer _batteryTimer = new() { Interval = 60_000 };
        private LogForm? _log;
        private Icon? _currentIcon;
        private string _iconKey = "";

        public TrayApp(bool demo, bool startHidden = false)
        {
            _menu = new ContextMenuStrip { Renderer = new DarkMenuRenderer(), ShowImageMargin = false, ShowCheckMargin = true };
            _menu.Opening += (_, _) => BuildMenu();

            // Creating the first window installs the UI "thread context" we post events to.
            var placeholder = new Control();
            placeholder.CreateControl();
            var ui = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();

            Theme.Scale = placeholder.DeviceDpi / 96f;
            _conn = new HeadphoneConnection(ui, demo);
            _panel = new PanelForm(_conn, ShowLog, Quit);

            _tray = new NotifyIcon
            {
                Text = "Z ULT Connect",
                ContextMenuStrip = _menu,
                Visible = true,
            };
            UpdateTray();
            _tray.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) TogglePanel(); };

            _conn.Changed += UpdateTray;
            _conn.StateChanged += UpdateTray;
            _conn.Changed += () => { if (_conn.IsConnected) _batteryTimer.Start(); else _batteryTimer.Stop(); };
            _batteryTimer.Tick += (_, _) => _conn.Session?.RefreshBattery();

            FirstRunTip();
            if (!startHidden) _panel.ShowNearTray();   // started with Windows -> stay quietly in the tray
            _ = _conn.ConnectAsync();
        }

        private void TogglePanel()
        {
            if (_panel.Visible) { _panel.Hide(); return; }
            // The click that hid the panel (by stealing focus) shouldn't reopen it.
            if ((DateTime.UtcNow - _panel.HiddenAt).TotalMilliseconds < 350) return;
            _panel.ShowNearTray();
        }

        private void UpdateTray()
        {
            var s = _conn.Session?.State;
            bool on = _conn.IsConnected;
            NoiseMode? mode = on ? s?.Noise : null;

            string key = $"{on}{mode}";
            if (key != _iconKey)
            {
                var old = _currentIcon;
                _currentIcon = AppIcon.Tray(mode, on);
                _tray.Icon = _currentIcon;
                old?.Dispose();
                _iconKey = key;
            }

            string text = !on ? $"Z ULT Connect - {_conn.Status}" :
                $"{s?.ModelName ?? "ULT WEAR"}" +
                (s?.BatteryPercent is int b ? $" - {b}%" : "") +
                (mode switch { NoiseMode.NoiseCancelling => " - Noise Cancelling", NoiseMode.Ambient => " - Ambient", NoiseMode.Off => " - Off", _ => "" });
            _tray.Text = text.Length > 120 ? text[..120] : text;
        }

        // ===================================================================
        // Menu (right-click on the icon, or ••• in the panel)
        // ===================================================================

        private void BuildMenu()
        {
            _menu.Items.Clear();
            var session = _conn.Session;
            var s = session?.State;
            bool on = session != null;

            _menu.Items.Add(Item("Open panel", () => _panel.ShowNearTray(), bold: true));
            _menu.Items.Add(new ToolStripSeparator());

            if (on && s != null)
            {
                _menu.Items.Add(Item("Noise Cancelling", () => session!.SetNoiseMode(NoiseMode.NoiseCancelling), s.Noise == NoiseMode.NoiseCancelling));
                _menu.Items.Add(Item("Ambient Sound", () => session!.SetNoiseMode(NoiseMode.Ambient), s.Noise == NoiseMode.Ambient));
                _menu.Items.Add(Item("Off", () => session!.SetNoiseMode(NoiseMode.Off), s.Noise == NoiseMode.Off));
                _menu.Items.Add(new ToolStripSeparator());

                if (s.PauseWhenRemoved is bool pause)
                    _menu.Items.Add(Item("Pause when taken off", () => session!.SetPauseWhenRemoved(!pause), pause));

                if (s.AutoPowerOff is byte current)
                {
                    var sub = new ToolStripMenuItem("Auto power off");
                    var choices = AutoPowerOffChoices.All.ToList();
                    if (s.AutoPowerOffWearingVariant) choices.Insert(1, (AutoPowerOffChoices.WhenTakenOff, "When taken off"));
                    foreach (var (id, name) in choices)
                        sub.DropDownItems.Add(Item(name, () => session!.SetAutoPowerOff(id), id == current));
                    StyleDropDown(sub);
                    _menu.Items.Add(sub);
                }

                _menu.Items.Add(Item("Turn off headphones…", () =>
                {
                    if (MessageBox.Show("Turn the headphones off?", "Z ULT Connect", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
                        session!.PowerOff();
                }));
                _menu.Items.Add(new ToolStripSeparator());
                _menu.Items.Add(Item("Disconnect", () => _conn.Disconnect("Disconnected")));
            }
            else
            {
                _menu.Items.Add(Item(_conn.IsBusy ? "Connecting…" : "Connect", () => _ = _conn.ConnectAsync()));
            }

            _menu.Items.Add(Item("Show log", ShowLog));
            _menu.Items.Add(new ToolStripSeparator());
            _menu.Items.Add(Item("Quit Z ULT Connect", Quit));
        }

        private static ToolStripMenuItem Item(string text, Action action, bool isChecked = false, bool bold = false)
        {
            var item = new ToolStripMenuItem(text) { Checked = isChecked, ForeColor = Theme.Text };
            if (bold) item.Font = new Font(Theme.Body, FontStyle.Bold);
            item.Click += (_, _) => action();
            return item;
        }

        private static void StyleDropDown(ToolStripMenuItem item)
        {
            if (item.DropDown is ToolStripDropDownMenu dd)
            {
                dd.ShowImageMargin = false;
                dd.ShowCheckMargin = true;
                dd.Renderer = new DarkMenuRenderer();
            }
        }

        private void ShowLog()
        {
            _log ??= new LogForm();
            _log.Show();
            _log.Activate();
        }

        private void FirstRunTip()
        {
            string flag = Path.Combine(AppLog.Folder, "first-run-done");
            if (File.Exists(flag)) return;
            _tray.ShowBalloonTip(6000, "Z ULT Connect is running",
                "Click the headphone icon by the clock to open it. If you can't see it, click ^ and drag it onto the taskbar.",
                ToolTipIcon.Info);
            try { File.WriteAllText(flag, DateTime.Now.ToString()); } catch { }
        }

        private void Quit()
        {
            _tray.Visible = false;
            _conn.Dispose();
            _panel.Dispose();
            _log?.Dispose();
            ExitThread();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _tray.Dispose();
                _currentIcon?.Dispose();
                _batteryTimer.Dispose();
                _menu.Dispose();
            }
            base.Dispose(disposing);
        }
    }

    /// <summary>Dark colors for the right-click menu.</summary>
    internal sealed class DarkMenuRenderer : ToolStripProfessionalRenderer
    {
        public DarkMenuRenderer() : base(new DarkColors()) { RoundedEdges = false; }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = e.Item.Enabled ? Theme.Text : Theme.Faint;
            base.OnRenderItemText(e);
        }

        protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
        {
            e.ArrowColor = Theme.Dim;
            base.OnRenderArrow(e);
        }

        protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            var r = e.ImageRectangle;
            float d = Math.Min(r.Width, r.Height) * 0.45f;
            using var brush = new SolidBrush(Theme.Accent);
            g.FillEllipse(brush, r.X + (r.Width - d) / 2, r.Y + (r.Height - d) / 2, d, d);
        }

        private sealed class DarkColors : ProfessionalColorTable
        {
            public override Color ToolStripDropDownBackground => Theme.Card;
            public override Color ImageMarginGradientBegin => Theme.Card;
            public override Color ImageMarginGradientMiddle => Theme.Card;
            public override Color ImageMarginGradientEnd => Theme.Card;
            public override Color MenuBorder => Theme.Card;
            public override Color MenuItemBorder => Theme.CardHover;
            public override Color ToolStripBorder => Theme.Card;
            public override Color MenuItemSelected => Theme.CardHover;
            public override Color MenuItemSelectedGradientBegin => Theme.CardHover;
            public override Color MenuItemSelectedGradientEnd => Theme.CardHover;
            public override Color SeparatorDark => Theme.Border;
            public override Color SeparatorLight => Theme.Card;
            public override Color CheckBackground => Theme.Card;
            public override Color CheckSelectedBackground => Theme.CardHover;
            public override Color CheckPressedBackground => Theme.CardHover;
        }
    }
}
