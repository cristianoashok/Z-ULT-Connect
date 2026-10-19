// Frame.cs
// ---------------------------------------------------------------------------
// The "envelope" every message travels in between the PC and the headphones.
//
//   3E | type | seq | length (4 bytes) | payload ... | checksum | 3C
//
//   - 3E is the start marker ('>'), 3C is the end marker ('<').
//   - If the bytes 3C, 3D or 3E appear inside the message, they are "escaped"
//     as 3D 2C / 3D 2D / 3D 2E so they can't be mistaken for markers.
//   - checksum = sum of type, seq, length and payload bytes, kept to 8 bits.
//
// Ported from the MIT-licensed SonyHeadphonesClient (libmdr/src/Command.cpp).
// ---------------------------------------------------------------------------
using System;
using System.Collections.Generic;

namespace ZUltConnect.Protocol
{
    /// <summary>What kind of frame this is (first byte after the start marker).</summary>
    public enum FrameType : byte
    {
        Ack = 0x01,
        /// <summary>Main ("table 1") messages: battery, noise cancelling, EQ...</summary>
        DataMdr = 0x0C,
        /// <summary>Secondary ("table 2") messages: pairing, voice guidance...</summary>
        DataMdrNo2 = 0x0E,
    }

    /// <summary>One decoded frame.</summary>
    public sealed record Frame(FrameType Type, byte Seq, byte[] Payload);

    public static class FrameCodec
    {
        public const byte StartMarker = 0x3E;
        public const byte EndMarker = 0x3C;
        private const byte EscapeByte = 0x3D;

        /// <summary>Wraps a payload into a ready-to-send frame.</summary>
        public static byte[] Pack(FrameType type, byte seq, ReadOnlySpan<byte> payload)
        {
            var body = new List<byte>(payload.Length + 7)
            {
                (byte)type,
                seq,
                (byte)(payload.Length >> 24),
                (byte)(payload.Length >> 16),
                (byte)(payload.Length >> 8),
                (byte)payload.Length,
            };
            foreach (var b in payload) body.Add(b);

            byte checksum = 0;
            foreach (var b in body) checksum += b;
            body.Add(checksum);

            var output = new List<byte>(body.Count * 2 + 2) { StartMarker };
            foreach (var b in body)
            {
                if (b == 0x3C || b == 0x3D || b == 0x3E)
                {
                    output.Add(EscapeByte);
                    output.Add((byte)(b - 0x10)); // 3C->2C, 3D->2D, 3E->2E
                }
                else
                {
                    output.Add(b);
                }
            }
            output.Add(EndMarker);
            return output.ToArray();
        }

        /// <summary>
        /// Decodes one complete frame (including both markers).
        /// Returns null if the frame is damaged.
        /// </summary>
        public static Frame? Unpack(ReadOnlySpan<byte> raw)
        {
            if (raw.Length < 2 || raw[0] != StartMarker || raw[^1] != EndMarker)
                return null;

            // Undo the escaping
            var body = new List<byte>(raw.Length);
            for (int i = 1; i < raw.Length - 1; i++)
            {
                byte b = raw[i];
                if (b == EscapeByte)
                {
                    if (++i >= raw.Length - 1) return null;
                    byte next = raw[i];
                    if (next is not (0x2C or 0x2D or 0x2E)) return null;
                    body.Add((byte)(next + 0x10));
                }
                else
                {
                    body.Add(b);
                }
            }

            // type + seq + 4 length bytes + checksum = at least 7 bytes
            if (body.Count < 7) return null;

            byte expected = 0;
            for (int i = 0; i < body.Count - 1; i++) expected += body[i];
            if (expected != body[^1]) return null;

            int length = body[2] << 24 | body[3] << 16 | body[4] << 8 | body[5];
            if (length != body.Count - 7) return null;

            var payload = body.GetRange(6, length).ToArray();
            return new Frame((FrameType)body[0], body[1], payload);
        }
    }

    /// <summary>
    /// Collects bytes as they arrive over Bluetooth and hands back complete frames.
    /// Bluetooth can split or merge frames arbitrarily, so we buffer.
    /// </summary>
    public sealed class FrameReader
    {
        private readonly List<byte> _buffer = new();

        public IEnumerable<Frame> Feed(ReadOnlySpan<byte> data)
        {
            foreach (var b in data) _buffer.Add(b);
            var frames = new List<Frame>();

            while (true)
            {
                int start = _buffer.IndexOf(FrameCodec.StartMarker);
                if (start < 0) { _buffer.Clear(); break; }
                if (start > 0) _buffer.RemoveRange(0, start); // drop junk before a frame

                int end = _buffer.IndexOf(FrameCodec.EndMarker, 1);
                if (end < 0) break; // wait for more bytes

                var raw = _buffer.GetRange(0, end + 1).ToArray();
                _buffer.RemoveRange(0, end + 1);

                var frame = FrameCodec.Unpack(raw);
                if (frame != null) frames.Add(frame);
            }
            return frames;
        }
    }
}
