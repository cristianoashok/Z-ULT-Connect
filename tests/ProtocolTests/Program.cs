// Test harness: a simulated ULT Wear that speaks MDR v2, so the protocol
// core can be checked without real headphones or Windows.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using ZUltConnect.Protocol;

int failures = 0;
void Check(bool ok, string what)
{
    Console.WriteLine((ok ? "  PASS  " : "  FAIL  ") + what);
    if (!ok) failures++;
}

// ---------------------------------------------------------------- framing
Console.WriteLine("Framing");
{
    // From XM Connect's README: GetProtocolInfo frame
    var packed = FrameCodec.Pack(FrameType.DataMdr, 0, new byte[] { 0x00, 0x00 });
    // 0C 00 00000002 00 00 checksum = 0x0C+0x02 = 0x0E
    Check(Session.Hex(packed) == "3E 0C 00 00 00 00 02 00 00 0E 3C", "pack GetProtocolInfo = " + Session.Hex(packed));

    var withSpecials = new byte[] { 0x3C, 0x3D, 0x3E, 0x01 };
    var esc = FrameCodec.Pack(FrameType.DataMdr, 1, withSpecials);
    var back = FrameCodec.Unpack(esc);
    Check(back != null && back.Payload.SequenceEqual(withSpecials) && back.Seq == 1, "escape round-trip");
    Check(!esc.Skip(1).Take(esc.Length - 2).Any(b => b is 0x3C or 0x3E), "no raw markers inside escaped frame");

    var corrupt = (byte[])esc.Clone(); corrupt[^2] ^= 0xFF;
    Check(FrameCodec.Unpack(corrupt) == null, "bad checksum rejected");

    var reader = new FrameReader();
    var a = FrameCodec.Pack(FrameType.Ack, 1, Array.Empty<byte>());
    var b2 = FrameCodec.Pack(FrameType.DataMdr, 0, new byte[] { 0x23, 0x00, 0x4E, 0x00 });
    var joined = new byte[] { 0x99 }.Concat(a).Concat(b2).ToArray();
    var got = reader.Feed(joined.AsSpan(0, 5)).ToList();
    got.AddRange(reader.Feed(joined.AsSpan(5)));
    Check(got.Count == 2 && got[0].Type == FrameType.Ack && got[1].Payload[2] == 0x4E, "split + merged stream decoded");
}

// ---------------------------------------------------------------- session vs fake ULT Wear
Console.WriteLine("Session against simulated ULT Wear");
var fake = new FakeUltWear();
var session = new Session(fake);
var log = new List<string>();
session.Log += l => { lock (log) log.Add(l); };
await session.StartAsync();
await fake.WaitIdleAsync();

var st = session.State;
Check(st.ModelName == "ULT WEAR", $"model name = {st.ModelName}");
Check(st.FirmwareVersion == "2.1.0", $"firmware = {st.FirmwareVersion}");
Check(st.SupportListReceived && st.Supported.Contains(0x6B), "feature list parsed");
Check(st.BatteryPercent == 78 && !st.Charging, $"battery = {st.BatteryPercent}");
Check(st.NcAsmType == 0x17, $"NC variant = {st.NcAsmType:X2}");
Check(st.Noise == NoiseMode.Off, $"noise mode = {st.Noise}");
Check(st.AmbientLevel == 15 && st.VoicePassthrough, "ambient level 15 + voice passthrough");
Check(st.EqPreset == 0x10, $"EQ preset = {st.EqPreset:X2} (Bright)");
Check(st.UltMode == 0, $"ULT mode read = {st.UltMode}");
Check(st.Dsee == true, "DSEE on");
Check(st.Volume == 15, $"volume = {st.Volume}");
Check(st.PauseWhenRemoved == true, "pause when removed");
Check(st.AutoPowerOff == 0x11, $"auto power off = {st.AutoPowerOff:X2} (never)");
Check(fake.UnackedDataFrames == 0, "every frame from the headset got an ACK");
Check(fake.OutOfOrder == 0, "never sent a new command before the previous ACK");

Console.WriteLine("Writes");
session.SetNoiseMode(NoiseMode.NoiseCancelling);
await fake.WaitIdleAsync();
Check(fake.LastSet(0x68) == "68 17 01 01 00 01 0F", "NC on  → " + fake.LastSet(0x68));
session.SetNoiseMode(NoiseMode.Ambient);
session.SetAmbientLevel(20);
await fake.WaitIdleAsync();
Check(fake.LastSet(0x68) == "68 17 01 01 01 01 14", "ambient 20 → " + fake.LastSet(0x68));
Check(st.Noise == NoiseMode.Ambient, "state follows notify");
session.SetNoiseMode(NoiseMode.Off);
await fake.WaitIdleAsync();
Check(fake.LastSet(0x68) == "68 17 01 00 01 01 14", "off → " + fake.LastSet(0x68));

await session.SetEqPreset(0x16);
await fake.WaitIdleAsync();
Check(fake.LastSet(0x58) == "58 03 16 00 00", "EQ Bass (via ULT message) → " + fake.LastSet(0x58));
Check(st.EqPreset == 0x16, "EQ re-read shows Bass");

await session.SetUltMode(2);
await fake.WaitIdleAsync();
Check(fake.LastSet(0x58) == "58 03 16 02 06 10 0E 0A 0A 0A 0A", "ULT 2 (bands echoed) → " + fake.LastSet(0x58));
Check(st.UltMode == 2, "ULT re-read shows 2");

await session.SetEqPreset(0x14);
await fake.WaitIdleAsync();
Check(fake.LastSet(0x58) == "58 03 14 02 00" && st.EqPreset == 0x14, $"Vocal sticks (not Custom) → {fake.LastSet(0x58)}, now {st.EqPreset:X2}");
Check(st.UltMode == 2, "ULT mode kept when changing preset");
await session.SetCustomBands(new byte[] { 16, 10, 8, 10, 12, 14 });
await fake.WaitIdleAsync();
Check(fake.LastSet(0x58) == "58 03 A0 02 06 10 0A 08 0A 0C 0E", "custom EQ → " + fake.LastSet(0x58));
Check(st.EqPreset == 0xA0 && Session.Hex(st.CurrentBands) == "10 0A 08 0A 0C 0E", $"custom EQ read back: {st.EqPreset:X2} {Session.Hex(st.CurrentBands)}");
session.SetDsee(false);
session.SetVolume(22);
session.SetPauseWhenRemoved(false);
session.SetAutoPowerOff(0x01);
session.PlayPause();
session.Next();
await fake.WaitIdleAsync();
Check(fake.LastSet(0xE8) == "E8 01 00", "DSEE off → " + fake.LastSet(0xE8));
Check(fake.LastSet(0xA8) == "A8 20 16", "volume 22 → " + fake.LastSet(0xA8));
Check(fake.LastSet(0xF8) == "F8 01 01", "pause-when-removed off → " + fake.LastSet(0xF8));
Check(fake.LastSet(0x28) == "28 04 01 00", "auto off 30 min → " + fake.LastSet(0x28));
Check(fake.LastSet(0xA4) == "A4 01 00 02", "next track → " + fake.LastSet(0xA4));
Check(st.Dsee == false && st.PauseWhenRemoved == false && st.AutoPowerOff == 0x01, "settings re-read");
Check(fake.OutOfOrder == 0 && fake.UnackedDataFrames == 0, "still one-at-a-time + all ACKed");

session.Dispose();

Console.WriteLine("Live info (bytes from Victor's log)");
{
    static byte[] H(string hex) => hex.Split(' ').Select(x => Convert.ToByte(x, 16)).ToArray();
    var live = new Session(new FakeUltWear());
    live.Simulate(H("C9 01 08 75 6E 69 74 57 65 61 72 00 00"));
    Check(live.State.Worn == true, "unitWear -> on your head");
    live.Simulate(H("C9 01 0A 75 6E 69 74 52 65 6D 6F 76 65 00 00"));
    Check(live.State.Worn == false, "unitRemove -> taken off");
    live.Simulate(H("A9 01 02 14 45 6E 63 6F 72 65 2F 43 75 72 74 61 69 6E 73 20 44 6F 77 6E 02 17 45 6E 63 6F 72 65 20 28 44 65 6C 75 78 65 20 56 65 72 73 69 6F 6E 29 02 06 45 6D 69 6E 65 6D 01 00"));
    Check(live.State.TrackTitle == "Encore/Curtains Down" && live.State.TrackAlbum == "Encore (Deluxe Version)" && live.State.TrackArtist == "Eminem",
          $"now playing: {live.State.TrackTitle} / {live.State.TrackAlbum} / {live.State.TrackArtist}");
    live.Simulate(H("A9 01 02 06 E7 B4 85 E8 93 AE 02 04 44 4F 45 53 02 04 44 4F 45 53 01 00"));
    Check(live.State.TrackTitle == "紅蓮" && live.State.TrackArtist == "DOES", $"Japanese title: {live.State.TrackTitle} - {live.State.TrackArtist}");
    live.Simulate(H("A9 20 0A"));
    Check(live.State.Volume == 10, "volume notify still works");
    live.Simulate(H("25 00 4B 00"));
    Check(live.State.BatteryPercent == 75, "battery notify 75%");
    live.Dispose();
}

Console.WriteLine("Built-in demo headset");
var demo = new Session(new DemoHeadset());
await demo.StartAsync();
await Task.Delay(800);
Check(demo.State.ModelName == "ULT WEAR" && demo.State.BatteryPercent == 77 || demo.State.BatteryPercent == 76, $"demo: {demo.State.ModelName} {demo.State.BatteryPercent}%");
Check(demo.State.Noise == NoiseMode.NoiseCancelling && demo.State.CurrentBands.Length == 6, "demo: NC on, 6 bands");
await demo.SetEqPreset(0x16);
Check(demo.State.EqPreset == 0x16 && demo.State.CurrentBands[0] == 17, "demo: Bass preset curve");
await demo.SetCustomBands(new byte[] { 20, 10, 10, 10, 10, 0 });
await Task.Delay(300);
Check(demo.State.EqPreset == 0xA0 && demo.State.CurrentBands[5] == 0, "demo: custom bands");
Check(demo.State.TrackTitle == "Midnight Drive", "demo: now playing");
await Task.Delay(2000);
Check(demo.State.Worn == true, "demo: put on after 2 s");
demo.Dispose();
Console.WriteLine(failures == 0 ? "\nALL TESTS PASSED" : $"\n{failures} FAILURE(S)");
Console.WriteLine("\nSample log:");
lock (log) foreach (var l in log.Take(14)) Console.WriteLine("   " + l);
return failures == 0 ? 0 : 1;

// =====================================================================
// A pretend ULT Wear: decodes our frames, ACKs them, answers like the
// real headphones would (values taken from Victor's screenshots).
// =====================================================================
sealed class FakeUltWear : IByteLink
{
    private readonly Channel<byte[]> _toPc = Channel.CreateUnbounded<byte[]>();
    private readonly FrameReader _reader = new();
    private readonly Dictionary<byte, string> _lastSet = new();
    private byte _seq = 0;
    private DateTime _lastActivity = DateTime.UtcNow;

    public int UnackedDataFrames;
    byte[] custom = { 10, 10, 10, 10, 10, 10 };
    byte[] Bands() => eq == 0xA0 ? custom : eq switch { 0x14 => new byte[] { 10, 16, 14, 12, 13, 9 }, 0x16 => new byte[] { 16, 14, 10, 10, 10, 10 }, _ => new byte[] { 9, 10, 15, 17, 17, 19 } };
    public int OutOfOrder;

    // device state
    byte nc = 0, amb = 1, voice = 1, level = 15, eq = 0x10, ult = 0, dsee = 1, vol = 15, pause = 0, apo = 0x11;

    public string LastSet(byte cmd) => _lastSet.TryGetValue(cmd, out var v) ? v : "(none)";

    public async Task<int> ReadAsync(byte[] buffer, CancellationToken ct)
    {
        var data = await _toPc.Reader.ReadAsync(ct);
        data.CopyTo(buffer, 0);
        return data.Length;
    }

    private readonly object _gate = new();
    private bool _ackPending;

    public Task WriteAsync(byte[] data, CancellationToken ct)
    {
        _lastActivity = DateTime.UtcNow;
        foreach (var f in _reader.Feed(data))
        {
            if (f.Type == FrameType.Ack) { lock (_gate) UnackedDataFrames--; continue; }
            lock (_gate)
            {
                if (_ackPending) OutOfOrder++;   // PC sent again before our ACK arrived
                _ackPending = true;
            }
            var payload = f.Payload; var seq = f.Seq;
            _ = Task.Run(async () =>
            {
                await Task.Delay(20);             // real headphones take a moment
                lock (_gate)
                {
                    Push(FrameType.Ack, (byte)(1 - seq), Array.Empty<byte>());
                    _ackPending = false;
                    foreach (var reply in Respond(payload)) SendData(reply);
                }
                _lastActivity = DateTime.UtcNow;
            });
        }
        return Task.CompletedTask;
    }

    private void SendData(byte[] payload)
    {
        UnackedDataFrames++;
        Push(FrameType.DataMdr, _seq, payload);
        _seq ^= 1;
    }

    private void Push(FrameType t, byte seq, byte[] payload) =>
        _toPc.Writer.TryWrite(FrameCodec.Pack(t, seq, payload));

    private IEnumerable<byte[]> Respond(byte[] p)
    {
        static byte[] Str(byte cmd, byte type, string s) =>
            new[] { cmd, type, (byte)s.Length }.Concat(Encoding.ASCII.GetBytes(s)).ToArray();
        byte c = p[0];
        if (c is 0x68 or 0x58 or 0xE8 or 0xA8 or 0xF8 or 0x28 or 0xA4) _lastSet[c] = Session.Hex(p);

        switch (c)
        {
            case 0x00: yield return new byte[] { 0x01, 0x00, 0, 0, 0x02, 0x00, 0x00, 0x00 }; break;
            case 0x02: yield return new byte[] { 0x03, 0x00, 0x02 }; break;
            case 0x04 when p[1] == 1: yield return Str(0x05, 0x01, "ULT WEAR"); break;
            case 0x04 when p[1] == 2: yield return Str(0x05, 0x02, "2.1.0"); break;
            case 0x06:
                byte[] feats = { 0x10, 0x20, 0x23, 0x24, 0x53, 0x6B, 0x69, 0xA1, 0xE2, 0xF1 };
                yield return new byte[] { 0x07, 0x00, (byte)feats.Length }.Concat(feats.SelectMany(f => new byte[] { f, 0 })).ToArray();
                break;
            case 0x22: yield return new byte[] { 0x23, 0x00, 78, 0x00 }; break;
            case 0x66: yield return new byte[] { 0x67, 0x17, 0x01, nc, amb, voice, level }; break;
            case 0x68: nc = p[3]; amb = p[4]; voice = p[5]; level = p[6];
                yield return new byte[] { 0x69, 0x17, 0x01, nc, amb, voice, level }; break;
            case 0x56 when p[1] == 0: yield return new byte[] { 0x57, 0x00, eq, ult, 6 }.Concat(Bands()).ToArray(); break;
            case 0x56 when p[1] == 3: yield return new byte[] { 0x57, 0x03, eq, ult, 6 }.Concat(Bands()).ToArray(); break;
            case 0x58 when p[1] == 0: break; // real ULT Wear ignores this (seen in Victor's log)
            case 0x58 when p[1] == 3:
                {
                    byte newPreset = p[2], newUlt = p[3];
                    bool sentBands = p[4] > 0;
                    if (sentBands && (newPreset == 0xA0 || newPreset != eq)) { newPreset = 0xA0; custom = p.Skip(5).Take(p[4]).ToArray(); } // what the real ULT Wear did
                    _ = Task.Run(async () => { await Task.Delay(500); lock (_gate) { eq = newPreset; ult = newUlt;
                        SendData(new byte[] { 0x59, 0x03, eq, ult, 6 }.Concat(Bands()).ToArray()); } _lastActivity = DateTime.UtcNow; });
                    break;
                }
            case 0xE6: yield return new byte[] { 0xE7, 0x01, dsee }; break;
            case 0xE8: dsee = p[2]; break;
            case 0xA6: yield return new byte[] { 0xA7, 0x20, vol }; break;
            case 0xA8: vol = p[2]; break;
            case 0xA2: yield return new byte[] { 0xA3, 0x01, 0x00, 0x02, 0x00 }; break;
            case 0xF6: yield return new byte[] { 0xF7, 0x01, pause }; break;
            case 0xF8: pause = p[2]; break;
            case 0x26: yield return new byte[] { 0x27, p[1], apo, 0x00 }; break;
            case 0x28: apo = p[2]; break;
        }
    }

    public async Task WaitIdleAsync()
    {
        _lastActivity = DateTime.UtcNow;
        while (DateTime.UtcNow - _lastActivity < TimeSpan.FromMilliseconds(400)) await Task.Delay(50);
    }

    public void Dispose() => _toPc.Writer.TryComplete();
}
