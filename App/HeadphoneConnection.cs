// HeadphoneConnection.cs
// ---------------------------------------------------------------------------
// Owns the link to the headphones: find -> connect -> session -> disconnect.
// The tray icon and the panel both listen to it. All events arrive on the
// UI thread, so the windows can update themselves directly.
// ---------------------------------------------------------------------------
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ZUltConnect.Protocol;

namespace ZUltConnect.App
{
    internal sealed class HeadphoneConnection : IDisposable
    {
        private readonly SynchronizationContext _ui;
        private readonly bool _demo;

        public Session? Session { get; private set; }
        public bool IsConnected => Session != null;
        public bool IsBusy { get; private set; }
        public string Status { get; private set; } = "Not connected";
        public string? DeviceName { get; private set; }

        /// <summary>Connected / disconnected / status text changed.</summary>
        public event Action? Changed;
        /// <summary>A headphone setting changed (battery, noise mode, EQ...).</summary>
        public event Action? StateChanged;

        public HeadphoneConnection(SynchronizationContext ui, bool demo)
        {
            _ui = ui;
            _demo = demo;
        }

        public async Task ConnectAsync()
        {
            if (IsBusy || Session != null) return;
            IsBusy = true;
            SetStatus("Looking for your headphones…");
            try
            {
                IByteLink link;
                string name;
                if (_demo)
                {
                    link = new DemoHeadset();
                    name = "ULT WEAR";
                    AppLog.Add("• Demo mode: using a simulated ULT Wear.");
                }
                else
                {
                    var found = await Bluetooth.WindowsBluetooth.FindAsync();
                    AppLog.Add($"• Found: {(found.Count == 0 ? "nothing" : string.Join(", ", found.Select(f => f.Name)))}");
                    if (found.Count == 0)
                    {
                        SetStatus("Not found - connect them in Windows Bluetooth settings");
                        return;
                    }
                    var target = found[0];
                    name = target.Name;
                    SetStatus($"Connecting to {name}…");
                    link = await Bluetooth.WindowsBluetooth.ConnectAsync(target, AppLog.Add);
                }

                var session = new Session(link);
                session.Log += AppLog.Add;
                session.StateChanged += () => Post(() => StateChanged?.Invoke());
                session.Disconnected += () => Post(() => Disconnect("Connection lost"));
                Session = session;
                DeviceName = name;
                SetStatus(_demo ? "Demo mode (simulated headphones)" : "Connected");
                await session.StartAsync();
                StateChanged?.Invoke();
            }
            catch (Exception ex)
            {
                AppLog.Add("• " + ex.Message);
                Session?.Dispose();
                Session = null;
                SetStatus("Couldn't connect - are they on and connected to this PC?");
            }
            finally
            {
                IsBusy = false;
                Changed?.Invoke();
            }
        }

        public void Disconnect(string message)
        {
            if (Session == null) return;
            Session.Dispose();
            Session = null;
            AppLog.Add("• " + message);
            SetStatus(message);
            StateChanged?.Invoke();
        }

        private void SetStatus(string status)
        {
            Status = status;
            Changed?.Invoke();
        }

        private void Post(Action action) => _ui.Post(_ => action(), null);

        public void Dispose() => Session?.Dispose();
    }
}
