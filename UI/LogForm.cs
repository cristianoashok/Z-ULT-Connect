// LogForm.cs - a simple window showing everything the headphones say.
using System;
using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;
using ZUltConnect.App;

namespace ZUltConnect.UI
{
    internal sealed class LogForm : Form
    {
        private readonly TextBox _box = new()
        {
            Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, WordWrap = true,
            Dock = DockStyle.Fill, Font = new Font("Consolas", 9f),
            BackColor = Color.FromArgb(12, 13, 16), ForeColor = Theme.Dim, BorderStyle = BorderStyle.None,
        };

        public LogForm()
        {
            Text = "Z ULT Connect - log";
            Icon = AppIcon.Window;
            BackColor = Theme.Bg;
            StartPosition = FormStartPosition.CenterScreen;
            Size = new Size(Theme.S(760), Theme.S(520));

            var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = Theme.S(46), BackColor = Theme.Bg, Padding = new Padding(Theme.S(8)) };
            var copy = new FlatButton("Copy all") { Width = Theme.S(110) };
            var open = new FlatButton("Open log file") { Width = Theme.S(130) };
            copy.Click += (_, _) => { if (_box.TextLength > 0) Clipboard.SetText(_box.Text); };
            open.Click += (_, _) =>
            {
                try { Process.Start(new ProcessStartInfo(AppLog.FilePath) { UseShellExecute = true }); }
                catch (Exception ex) { MessageBox.Show(this, ex.Message, Text); }
            };
            buttons.Controls.Add(copy);
            buttons.Controls.Add(open);

            Controls.Add(_box);
            Controls.Add(buttons);

            _box.Lines = AppLog.Snapshot();
            AppLog.LineAdded += OnLine;
        }

        private void OnLine(string line)
        {
            if (IsDisposed || !IsHandleCreated) return;
            BeginInvoke(new Action(() =>
            {
                if (_box.TextLength > 300_000) _box.Clear();
                _box.AppendText(line + Environment.NewLine);
            }));
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            _box.SelectionStart = _box.TextLength;
            _box.ScrollToCaret();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; Hide(); }
            base.OnFormClosing(e);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) AppLog.LineAdded -= OnLine;
            base.Dispose(disposing);
        }
    }
}
