// Session.cs
// ---------------------------------------------------------------------------
// The conversation with the headphones:
//   1. One message at a time: send, then wait for the headphones' ACK.
//   2. Every message the headphones send us gets an ACK back.
//   3. On connect, run the same "hello" sequence Sony's app uses, then read
//      every setting so the window can show it.
// It doesn't know anything about Bluetooth: it just reads and writes bytes
// through an IByteLink, so it can be tested without real headphones.
// ---------------------------------------------------------------------------
using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ZUltConnect.Protocol
{
    /// <summary>A two-way byte pipe (a Bluetooth socket in the real app, a fake in tests).</summary>
    public interface IByteLink : IDisposable
    {
        /// <summary>Reads some bytes. Returns 0 when the connection is closed.</summary>
        Task<int> ReadAsync(byte[] buffer, CancellationToken ct);
        Task WriteAsync(byte[] data, CancellationToken ct);
    }

    public sealed class Session : IDisposable
    {
        private static readonly TimeSpan AckTimeout = TimeSpan.FromMilliseconds(3000);
        private static readonly TimeSpan ReplyTimeout = TimeSpan.FromSeconds(3);

        private readonly IByteLink _link;
        private readonly CancellationTokenSource _cts = new();
        private readonly FrameReader _reader = new();
        private readonly SemaphoreSlim _writeLock = new(1, 1);
        private readonly BlockingCollection<byte[]> _outbox = new();
        private readonly ConcurrentDictionary<byte, TaskCompletionSource<byte[]>> _waitingFor = new();
        private TaskCompletionSource<bool>? _ackWaiter;
        private byte _seq;

        public HeadphoneState State { get; } = new();

        /// <summary>Something in <see cref="State"/> changed.</summary>
        public event Action? StateChanged;
        /// <summary>A line for the log box (raw bytes, info, errors).</summary>
        public event Action<string>? Log;
        /// <summary>The connection dropped.</summary>
        public event Action? Disconnected;

        public Session(IByteLink link) => _link = link;

        // ===================================================================
        // Start-up
        // ===================================================================

        /// <summary>Starts reading and sending, then runs the hello sequence.</summary>
        public async Task StartAsync()
        {
            _ = Task.Run(ReadLoopAsync);
            _ = Task.Run(SendLoopAsync);
            await HandshakeAsync().ConfigureAwait(false);
        }

        private async Task HandshakeAsync()
        {
            var s = State;

            var protocol = await RequestAndWaitAsync(Requests.ProtocolInfo(), Cmd.RetProtocolInfo);
            if (protocol == null) Info("No protocol reply (continuing anyway).");
            else if (!s.HasTable1) Info("Warning: headphones say they don't support MDR v2 table 1.");

            Enqueue(Requests.CapabilityInfo());
            Enqueue(Requests.ModelName());
            Enqueue(Requests.FirmwareVersion());

            var support = await RequestAndWaitAsync(Requests.SupportFunctions(), Cmd.RetSupportFunction);
            if (support == null) Info("No feature list reply - will try every feature.");

            ReadEverything();
            Enqueue(Requests.LogSetStatus());
        }

        /// <summary>Asks the headphones for every setting we show.</summary>
        public void ReadEverything()
        {
            var s = State;
            RefreshBattery();

            if (s.Supported.Contains(Feature.NcAsmDualWithNoiseAdaptation)) s.NcAsmType = 0x19;
            else if (s.Supported.Contains(Feature.NcAsmDual)) s.NcAsmType = 0x17;
            else if (s.Supported.Contains(Feature.AmbientSoundOnly)) s.NcAsmType = 0x22;
            Enqueue(Requests.GetNcAsm(s.NcAsmType));

            Enqueue(Requests.GetEq());
            if (s.Supported.Contains(Feature.PresetEqAndUltMode) || !s.SupportListReceived)
                Enqueue(Requests.GetEqAndUlt());

            if (s.Supports(Feature.Dsee)) Enqueue(Requests.GetDsee());
            Enqueue(Requests.GetVolume());
            Enqueue(Requests.GetPlayStatus());
            Enqueue(Requests.GetNowPlaying());
            if (s.Supports(Feature.PauseWhenRemoved)) Enqueue(Requests.GetPauseWhenRemoved());

            s.AutoPowerOffWearingVariant = s.Supported.Contains(Feature.AutoPowerOffWithWearingDetection)
                                           && !s.Supported.Contains(Feature.AutoPowerOff);
            if (s.Supports(Feature.AutoPowerOff) || s.Supported.Contains(Feature.AutoPowerOffWithWearingDetection))
                Enqueue(Requests.GetAutoPowerOff(s.AutoPowerOffWearingVariant));
        }

        public void RefreshBattery() =>
            Enqueue(Requests.GetBattery(State.Supported.Contains(Feature.BatteryLevelWithThreshold)
                                        && !State.Supported.Contains(Feature.BatteryLevel)));

        // ===================================================================
        // Things the window can ask for
        // ===================================================================

        public void SetNoiseMode(NoiseMode mode)
        {
            var s = State;
            s.NcAsmOn = mode != NoiseMode.Off;
            if (mode != NoiseMode.Off) s.NcAsmAmbient = mode == NoiseMode.Ambient;
            SendNcAsm();
        }

        public void SetAmbientLevel(byte level)
        {
            State.AmbientLevel = Math.Clamp(level, (byte)1, (byte)20);
            SendNcAsm();
        }

        public void SetVoicePassthrough(bool on)
        {
            State.VoicePassthrough = on;
            SendNcAsm();
        }

        private void SendNcAsm()
        {
            var s = State;
            Enqueue(Requests.SetNcAsm(s.NcAsmType, s.NcAsmOn ?? true, s.NcAsmAmbient, s.VoicePassthrough,
                                      s.AmbientLevel, s.AdaptiveOn, s.AdaptiveSensitivity));
        }

        /// <summary>
        /// The ULT Wear only lists "EQ + ULT mode" (0x53), not plain "EQ" (0x50), and ignores
        /// plain preset changes. So on those headphones the preset goes through the ULT message.
        /// </summary>
        private bool EqUsesUltMessage =>
            State.Supported.Contains(Feature.PresetEqAndUltMode) && !State.Supported.Contains(Feature.PresetEq);

        public async Task SetEqPreset(byte presetId)
        {
            var s = State;
            if (!EqUsesUltMessage)
            {
                Enqueue(Requests.SetEqPreset(presetId));
                Enqueue(Requests.GetEq());
                if (s.UltMode != null) Enqueue(Requests.GetEqAndUlt());
                return;
            }

            // Preset only, NO band values. (Sending band values turns it into "Custom" -
            // seen in Victor's log on 19 Oct.) The headphones need ~0.5 s to apply it,
            // so we wait before reading it back.
            Enqueue(Requests.SetEqAndUlt(presetId, s.UltMode ?? 0, Array.Empty<byte>()));
            await Task.Delay(EqSettleTime, _cts.Token).ConfigureAwait(false);
            await RequestAndWaitAsync(Requests.GetEqAndUlt(), Cmd.EqRetParam).ConfigureAwait(false);
            Info(s.UltPreset == presetId
                ? $"EQ is now {EqPresets.NameOf(presetId)}."
                : $"Headphones report {EqPresets.NameOf(s.UltPreset)} instead of {EqPresets.NameOf(presetId)}.");
        }

        /// <summary>
        /// Custom EQ. bands are raw values 0-20 (10 = flat): [bass, 400, 1k, 2.5k, 6.3k, 16k] on the ULT Wear.
        /// Sent as preset "Custom" (0xA0) with the band values.
        /// </summary>
        public async Task SetCustomBands(byte[] bands)
        {
            var s = State;
            if (bands.Length == 0) return;
            s.EqPreset = 0xA0;
            Info("Custom EQ: " + string.Join(" ", bands.Select(b => (b - 10).ToString("+0;-0;0"))));
            if (EqUsesUltMessage || s.UltMode != null)
            {
                s.UltPreset = 0xA0;
                s.UltBands = bands;
                Enqueue(Requests.SetEqAndUlt(0xA0, s.UltMode ?? 0, bands));
                await Task.Delay(EqSettleTime, _cts.Token).ConfigureAwait(false);
                await RequestAndWaitAsync(Requests.GetEqAndUlt(), Cmd.EqRetParam).ConfigureAwait(false);
            }
            else
            {
                s.EqBands = bands;
                Enqueue(Requests.SetEqPresetWithBands(0xA0, bands));
                Enqueue(Requests.GetEq());
            }
        }

        private static readonly TimeSpan EqSettleTime = TimeSpan.FromMilliseconds(800);

        /// <summary>EXPERIMENTAL ULT bass mode: 0 = off, 1 = ULT 1, 2 = ULT 2.</summary>
        public async Task SetUltMode(byte mode)
        {
            var s = State;
            byte preset = s.UltMode != null ? s.UltPreset : (s.EqPreset ?? 0x00);
            // This exact form (current preset + current bands) is confirmed working on the ULT Wear.
            Enqueue(Requests.SetEqAndUlt(preset, mode, s.UltBands));
            await Task.Delay(EqSettleTime, _cts.Token).ConfigureAwait(false);
            await RequestAndWaitAsync(Requests.GetEqAndUlt(), Cmd.EqRetParam).ConfigureAwait(false);
        }

        public void SetDsee(bool on)
        {
            Enqueue(Requests.SetDsee(on));
            Enqueue(Requests.GetDsee());
        }

        public void SetVolume(int volume)
        {
            State.Volume = Math.Clamp(volume, 0, 30);
            Enqueue(Requests.SetVolume((byte)State.Volume));
        }

        public void PlayPause()
        {
            bool playing = State.Playing ?? false;
            Enqueue(Requests.PressKey(playing ? PlaybackKey.Pause : PlaybackKey.Play));
            State.Playing = !playing;
            Enqueue(Requests.GetPlayStatus());
        }

        public void Next() => Enqueue(Requests.PressKey(PlaybackKey.Next));
        public void Previous() => Enqueue(Requests.PressKey(PlaybackKey.Previous));

        public void SetPauseWhenRemoved(bool on)
        {
            Enqueue(Requests.SetPauseWhenRemoved(on));
            Enqueue(Requests.GetPauseWhenRemoved());
        }

        public void SetAutoPowerOff(byte choice)
        {
            Enqueue(Requests.SetAutoPowerOff(State.AutoPowerOffWearingVariant, choice));
            Enqueue(Requests.GetAutoPowerOff(State.AutoPowerOffWearingVariant));
        }

        public void PowerOff() => Enqueue(Requests.PowerOff());

        // ===================================================================
        // Sending: one message at a time, each waits for its ACK
        // ===================================================================

        private void Enqueue(byte[] payload)
        {
            if (!_outbox.IsAddingCompleted) _outbox.Add(payload);
        }

        private async Task<byte[]?> RequestAndWaitAsync(byte[] payload, byte replyCommand)
        {
            var tcs = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
            _waitingFor[replyCommand] = tcs;
            Enqueue(payload);
            var done = await Task.WhenAny(tcs.Task, Task.Delay(ReplyTimeout, _cts.Token)).ConfigureAwait(false);
            _waitingFor.TryRemove(replyCommand, out _);
            return done == tcs.Task ? tcs.Task.Result : null;
        }

        private async Task SendLoopAsync()
        {
            try
            {
                foreach (var payload in _outbox.GetConsumingEnumerable(_cts.Token))
                {
                    var ack = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                    _ackWaiter = ack;
                    await WriteFrameAsync(FrameType.DataMdr, _seq, payload).ConfigureAwait(false);

                    var done = await Task.WhenAny(ack.Task, Task.Delay(AckTimeout, _cts.Token)).ConfigureAwait(false);
                    if (done != ack.Task) Info($"(no ACK for {Hex(payload, 2)} - moving on)");
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { Fail("send", ex); }
        }

        private async Task WriteFrameAsync(FrameType type, byte seq, byte[] payload)
        {
            var bytes = FrameCodec.Pack(type, seq, payload);
            await _writeLock.WaitAsync(_cts.Token).ConfigureAwait(false);
            try
            {
                if (type != FrameType.Ack) Log?.Invoke($"→ {Hex(payload)}");
                await _link.WriteAsync(bytes, _cts.Token).ConfigureAwait(false);
            }
            finally { _writeLock.Release(); }
        }

        // ===================================================================
        // Receiving
        // ===================================================================

        private async Task ReadLoopAsync()
        {
            var buffer = new byte[2048];
            try
            {
                while (!_cts.IsCancellationRequested)
                {
                    int n = await _link.ReadAsync(buffer, _cts.Token).ConfigureAwait(false);
                    if (n <= 0) { Info("Connection closed by the headphones."); break; }
                    foreach (var frame in _reader.Feed(buffer.AsSpan(0, n)))
                        await HandleFrameAsync(frame).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex) { Fail("receive", ex); return; }
            Disconnected?.Invoke();
        }

        private async Task HandleFrameAsync(Frame frame)
        {
            // Same rule as SonyHeadphonesClient: our next sequence number follows theirs.
            _seq = frame.Seq;

            if (frame.Type == FrameType.Ack)
            {
                _ackWaiter?.TrySetResult(true);
                return;
            }

            // Acknowledge everything they send us
            await WriteFrameAsync(FrameType.Ack, (byte)(1 - frame.Seq), Array.Empty<byte>()).ConfigureAwait(false);

            if (frame.Type != FrameType.DataMdr || frame.Payload.Length == 0)
            {
                if (frame.Payload.Length > 0) Log?.Invoke($"← (table 2) {Hex(frame.Payload)}");
                return;
            }

            var p = frame.Payload;
            Log?.Invoke($"← {Hex(p)}");
            bool changed;
            try { changed = Parse(p); }
            catch (Exception ex) { Info($"Couldn't read that reply: {ex.Message}"); changed = false; }

            if (_waitingFor.TryGetValue(p[0], out var waiter)) waiter.TrySetResult(p);
            if (changed) StateChanged?.Invoke();
        }

        /// <summary>Feeds a message as if the headphones had sent it (demo mode and tests).</summary>
        public void Simulate(byte[] payload)
        {
            if (Parse(payload)) StateChanged?.Invoke();
        }

        /// <summary>Turns one reply into state. Returns true if something changed.</summary>
        internal bool Parse(byte[] p)
        {
            var s = State;
            byte cmd = p[0];
            byte type = p.Length > 1 ? p[1] : (byte)0;

            switch (cmd)
            {
                case Cmd.RetProtocolInfo when p.Length >= 8:
                    s.HasTable1 = p[6] == 0; // 0 = enabled
                    return false;

                case Cmd.RetSupportFunction when p.Length >= 3:
                    {
                        int count = p[2];
                        for (int i = 0; i < count && 3 + i * 2 < p.Length; i++)
                            s.Supported.Add(p[3 + i * 2]);
                        s.SupportListReceived = true;
                        Info("Features: " + string.Join(" ", s.Supported.OrderBy(x => x).Select(x => x.ToString("X2"))));
                        return true;
                    }

                case Cmd.RetDeviceInfo when p.Length >= 3:
                    {
                        int len = Math.Min(p[2], p.Length - 3);
                        var text = Encoding.ASCII.GetString(p, 3, len).TrimEnd('\0');
                        if (type == 0x01) s.ModelName = text;
                        else if (type == 0x02) s.FirmwareVersion = text;
                        return true;
                    }

                case Cmd.PowerRetStatus or Cmd.PowerNtfyStatus when (type is 0x00 or 0x08) && p.Length >= 4:
                    s.BatteryPercent = p[2];
                    s.Charging = p[3] == 1;
                    return true;

                case Cmd.PowerRetParam or Cmd.PowerNtfyParam when (type is 0x04 or 0x05) && p.Length >= 3:
                    s.AutoPowerOff = p[2];
                    s.AutoPowerOffWearingVariant = type == 0x05;
                    return true;

                case Cmd.NcAsmRetParam or Cmd.NcAsmNtfyParam:
                    return ParseNcAsm(p);

                case Cmd.EqRetParam or Cmd.EqNtfyParam:
                    // On the ULT Wear the plain EQ reply has the ULT layout too (preset, ult, count, bands).
                    if (type == 0x00 && p.Length >= 5 && p.Length == 5 + p[4])
                        type = 0x03;
                    if (type == 0x00 && p.Length >= 3)
                    {
                        s.EqPreset = p[2];
                        s.EqBands = p.Length >= 4 ? p.Skip(4).Take(p[3]).ToArray() : Array.Empty<byte>();
                        return true;
                    }
                    if (type == 0x03 && p.Length >= 4)
                    {
                        s.UltPreset = p[2];
                        s.EqPreset = p[2];
                        s.UltMode = p[3];
                        s.UltBands = p.Length >= 5 ? p.Skip(5).Take(p[4]).ToArray() : Array.Empty<byte>();
                        return true;
                    }
                    return false;

                case Cmd.AudioRetParam or Cmd.AudioNtfyParam when type == 0x01 && p.Length >= 3:
                    s.Dsee = p[2] == 1;
                    return true;

                case Cmd.PlayRetParam or Cmd.PlayNtfyParam when (type is 0x20 or 0x30) && p.Length >= 3:
                    s.Volume = p[2];
                    return true;

                case Cmd.PlayRetParam or Cmd.PlayNtfyParam when type == 0x01:
                    {
                        // Track info: [02 len title] [02 len album] [02 len artist] ...
                        var texts = new string[3];
                        int i = 2;
                        for (int k = 0; k < 3 && i + 1 < p.Length; k++)
                        {
                            int len = p[i + 1];
                            if (i + 2 + len > p.Length) break;
                            texts[k] = Encoding.UTF8.GetString(p, i + 2, len);
                            i += 2 + len;
                        }
                        s.TrackTitle = string.IsNullOrWhiteSpace(texts[0]) ? null : texts[0];
                        s.TrackAlbum = string.IsNullOrWhiteSpace(texts[1]) ? null : texts[1];
                        s.TrackArtist = string.IsNullOrWhiteSpace(texts[2]) ? null : texts[2];
                        return true;
                    }

                case Cmd.LogNtfyParam when p.Length >= 3:
                    {
                        // The headphones log "unitWear" / "unitRemove" when put on / taken off.
                        int len = Math.Min(p[2], p.Length - 3);
                        string text = Encoding.ASCII.GetString(p, 3, len).TrimEnd('\0');
                        if (text.Contains("unitWear")) { s.Worn = true; return true; }
                        if (text.Contains("unitRemove")) { s.Worn = false; return true; }
                        return false;
                    }

                case Cmd.PlayRetStatus or Cmd.PlayNtfyStatus when type == 0x01 && p.Length >= 4:
                    s.Playing = p[3] == 0x01;
                    return true;

                case Cmd.SystemRetParam or Cmd.SystemNtfyParam when type == 0x01 && p.Length >= 3:
                    s.PauseWhenRemoved = p[2] == 0; // 0 = enabled
                    return true;
            }
            return false;
        }

        private bool ParseNcAsm(byte[] p)
        {
            var s = State;
            byte type = p[1];
            switch (type)
            {
                case 0x17 when p.Length >= 7:
                case 0x19 when p.Length >= 9:
                    s.NcAsmType = type;
                    s.NcAsmOn = p[3] == 1;
                    s.NcAsmAmbient = p[4] == 1;
                    s.VoicePassthrough = p[5] == 1;
                    if (p[6] > 0) s.AmbientLevel = p[6];
                    if (type == 0x19) { s.AdaptiveOn = p[7]; s.AdaptiveSensitivity = p[8]; }
                    return true;
                case 0x22 when p.Length >= 6:
                    s.NcAsmType = type;
                    s.NcAsmOn = p[3] == 1;
                    s.NcAsmAmbient = true;
                    s.VoicePassthrough = p[4] == 1;
                    if (p[5] > 0) s.AmbientLevel = p[5];
                    return true;
            }
            return false;
        }

        // ===================================================================
        // Helpers
        // ===================================================================

        private void Info(string message) => Log?.Invoke("• " + message);

        private void Fail(string where, Exception ex)
        {
            if (_cts.IsCancellationRequested) return;
            Info($"Connection problem while trying to {where}: {ex.Message}");
            Disconnected?.Invoke();
        }

        public static string Hex(byte[] data, int max = int.MaxValue) =>
            string.Join(" ", data.Take(max).Select(b => b.ToString("X2")));

        public void Dispose()
        {
            _cts.Cancel();
            _outbox.CompleteAdding();
            _link.Dispose();
        }
    }
}
