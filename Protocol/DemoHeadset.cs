// DemoHeadset.cs
// ---------------------------------------------------------------------------
// A pretend ULT Wear that runs inside the app ("--demo"). It answers exactly
// like the real headphones did in Victor's logs (same feature list, same EQ
// curves for each preset, same half-second EQ delay), so every control can be
// tried - and tested - without headphones.
// ---------------------------------------------------------------------------
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ZUltConnect.Protocol
{
    public sealed class DemoHeadset : IByteLink
    {
        private readonly BlockingCollection<byte[]> _toPc = new();
        private readonly FrameReader _reader = new();
        private readonly object _gate = new();
        private byte _seq;

        // Starting state (from Victor's headphones)
        private byte _ncOn = 1, _ambient = 0, _voice = 1, _level = 15;
        private byte _eq = 0x10, _ult = 0, _dsee = 1, _volume = 16, _pauseOff = 0, _autoOff = 0x11;
        private byte _battery = 77;
        private byte[] _custom = { 10, 10, 10, 10, 10, 10 };

        // Real preset curves reported by the ULT Wear (raw values, 10 = flat)
        private static readonly Dictionary<byte, byte[]> PresetBands = new()
        {
            [0x00] = new byte[] { 10, 10, 10, 10, 10, 10 }, // Off
            [0x10] = new byte[] { 9, 10, 15, 17, 17, 19 },  // Bright
            [0x11] = new byte[] { 18, 9, 11, 10, 13, 15 },  // Excited
            [0x12] = new byte[] { 12, 11, 10, 9, 8, 7 },    // Mellow (not captured yet - guess)
            [0x13] = new byte[] { 1, 7, 9, 7, 5, 2 },       // Relaxed
            [0x14] = new byte[] { 10, 16, 14, 12, 13, 9 },  // Vocal
            [0x15] = new byte[] { 10, 10, 10, 12, 16, 20 }, // Treble
            [0x16] = new byte[] { 17, 10, 10, 10, 10, 10 }, // Bass
            [0x17] = new byte[] { 0, 14, 13, 11, 12, 0 },   // Speech
        };

        private bool _worn = true;
        private Timer? _wearTimer;

        private static byte[] Track(string title, string album, string artist)
        {
            var bytes = new List<byte> { 0xA9, 0x01 };
            foreach (var t in new[] { title, album, artist })
            {
                var b = Encoding.UTF8.GetBytes(t);
                bytes.Add(0x02); bytes.Add((byte)b.Length); bytes.AddRange(b);
            }
            bytes.Add(0x01); bytes.Add(0x00);
            return bytes.ToArray();
        }

        private static byte[] WearLog(bool worn)
        {
            var text = Encoding.ASCII.GetBytes(worn ? "unitWear" : "unitRemove");
            return new byte[] { 0xC9, 0x01, (byte)text.Length }.Concat(text).Concat(new byte[] { 0, 0 }).ToArray();
        }

        /// <summary>In demo mode the "headphones" are put on and taken off every 20 seconds.</summary>
        private void StartWearCycle()
        {
            _wearTimer ??= new Timer(_ =>
            {
                lock (_gate) { Send(WearLog(_worn)); _worn = !_worn; }
            }, null, 2000, 20000);
        }

        private byte[] Bands() => PresetBands.TryGetValue(_eq, out var b) ? b : _custom;

        public Task<int> ReadAsync(byte[] buffer, CancellationToken ct) => Task.Run(() =>
        {
            byte[] data = _toPc.Take(ct);
            int n = Math.Min(data.Length, buffer.Length);
            Array.Copy(data, buffer, n);
            return n;
        }, ct);

        public Task WriteAsync(byte[] data, CancellationToken ct)
        {
            foreach (var frame in _reader.Feed(data))
            {
                if (frame.Type == FrameType.Ack) continue;
                var payload = frame.Payload;
                var seq = frame.Seq;
                _ = Task.Run(async () =>
                {
                    await Task.Delay(25).ConfigureAwait(false);
                    lock (_gate)
                    {
                        Push(FrameType.Ack, (byte)(1 - seq), Array.Empty<byte>());
                        foreach (var reply in Respond(payload)) Send(reply);
                    }
                });
            }
            return Task.CompletedTask;
        }

        private void Send(byte[] payload)
        {
            Push(FrameType.DataMdr, _seq, payload);
            _seq ^= 1;
        }

        private void Push(FrameType type, byte seq, byte[] payload)
        {
            if (!_toPc.IsAddingCompleted) _toPc.Add(FrameCodec.Pack(type, seq, payload));
        }

        private byte[] EqReply(byte cmd, byte type) =>
            new byte[] { cmd, type, _eq, _ult, 6 }.Concat(Bands()).ToArray();

        private IEnumerable<byte[]> Respond(byte[] p)
        {
            static byte[] Text(byte type, string s) =>
                new byte[] { 0x05, type, (byte)s.Length }.Concat(Encoding.ASCII.GetBytes(s)).ToArray();

            switch (p[0])
            {
                case 0x00: yield return new byte[] { 0x01, 0x00, 0x03, 0x00, 0x20, 0x02, 0x00, 0x00 }; break;
                case 0x02: yield return new byte[] { 0x03, 0x00, 0x00 }; break;
                case 0x04 when p[1] == 1: yield return Text(0x01, "ULT WEAR"); break;
                case 0x04 when p[1] == 2: yield return Text(0x02, "2.1.0"); break;
                case 0x06:
                    byte[] features = { 0x10, 0x12, 0x13, 0x14, 0x15, 0x20, 0x23, 0x25, 0x27, 0x32, 0x53, 0x69, 0x6B,
                                        0x70, 0x90, 0x93, 0xA1, 0xB2, 0xB7, 0xC1, 0xC2, 0xD1, 0xD2, 0xD3, 0xE2, 0xE3,
                                        0xF1, 0xF4, 0xF9, 0xFD };
                    yield return new byte[] { 0x07, 0x00, (byte)features.Length }
                        .Concat(features.SelectMany(f => new byte[] { f, 0xFF })).ToArray();
                    break;
                case 0x22:
                    if (_battery > 5 && DateTime.Now.Second % 7 == 0) _battery--;
                    yield return new byte[] { 0x23, 0x00, _battery, 0x00 };
                    break;
                case 0x66: yield return new byte[] { 0x67, 0x17, 0x01, _ncOn, _ambient, _voice, _level }; break;
                case 0x68:
                    _ncOn = p[3];
                    if (p[3] == 1) { _ambient = p[4]; _voice = p[5]; _level = p[6]; }
                    yield return new byte[] { 0x69, 0x17, 0x01, _ncOn, _ambient, _voice, _level };
                    break;
                case 0x56: yield return EqReply(0x57, p[1]); break;
                case 0x58 when p[1] == 0x03:
                    {
                        byte preset = p[2], ult = p[3];
                        bool hasBands = p.Length > 5 && p[4] > 0;
                        if (hasBands && (preset == 0xA0 || preset != _eq))
                        {
                            _custom = p.Skip(5).Take(p[4]).ToArray();
                            preset = 0xA0;
                        }
                        // The real headphones apply EQ changes after ~half a second
                        _ = Task.Run(async () =>
                        {
                            await Task.Delay(450).ConfigureAwait(false);
                            lock (_gate)
                            {
                                _eq = preset; _ult = ult;
                                Send(EqReply(0x59, 0x03));
                            }
                        });
                        break;
                    }
                case 0xE6: yield return new byte[] { 0xE7, 0x01, _dsee }; break;
                case 0xE8: _dsee = p[2]; yield return new byte[] { 0xE9, 0x01, _dsee }; break;
                case 0xA6 when p[1] == 0x01: yield return Track("Midnight Drive", "Neon Hours", "Z ULT Demo"); break;
                case 0xA6: yield return new byte[] { 0xA7, 0x20, _volume }; break;
                case 0xA8: _volume = p[2]; yield return new byte[] { 0xA9, 0x20, _volume }; break;
                case 0xA2: yield return new byte[] { 0xA3, 0x01, 0x00, 0x01, 0x00 }; break;
                case 0xA4:
                    if (p[3] == 0x07) yield return new byte[] { 0xA5, 0x01, 0x00, 0x01, 0x00 };
                    if (p[3] == 0x01) yield return new byte[] { 0xA5, 0x01, 0x00, 0x02, 0x00 };
                    break;
                case 0xF6: yield return new byte[] { 0xF7, 0x01, _pauseOff }; break;
                case 0xF8: _pauseOff = p[2]; yield return new byte[] { 0xF9, 0x01, _pauseOff }; break;
                case 0xC4: StartWearCycle(); break;
                case 0x26: yield return new byte[] { 0x27, p[1], _autoOff, 0x11 }; break;
                case 0x28: _autoOff = p[2]; yield return new byte[] { 0x29, p[1], _autoOff, 0x11 }; break;
            }
        }

        public void Dispose()
        {
            _wearTimer?.Dispose();
            _toPc.CompleteAdding();
        }
    }
}
