// Mdr.cs
// ---------------------------------------------------------------------------
// The actual "words" we say to the headphones (Sony's MDR v2 protocol, table 1).
// Every message starts with a command byte, then an "inquired type" byte that
// says which feature we mean, then the values.
//
// Byte layouts come from the MIT-licensed SonyHeadphonesClient
// (libmdr/include/mdr/ProtocolV2T1.hpp) by mos9527 and contributors.
// ---------------------------------------------------------------------------
namespace ZUltConnect.Protocol
{
    /// <summary>Command bytes (first byte of every table-1 payload).</summary>
    public static class Cmd
    {
        public const byte GetProtocolInfo = 0x00, RetProtocolInfo = 0x01;
        public const byte GetCapabilityInfo = 0x02;
        public const byte GetDeviceInfo = 0x04, RetDeviceInfo = 0x05;
        public const byte GetSupportFunction = 0x06, RetSupportFunction = 0x07;

        public const byte PowerGetStatus = 0x22, PowerRetStatus = 0x23, PowerSetStatus = 0x24, PowerNtfyStatus = 0x25;
        public const byte PowerGetParam = 0x26, PowerRetParam = 0x27, PowerSetParam = 0x28, PowerNtfyParam = 0x29;

        public const byte EqGetStatus = 0x52, EqRetStatus = 0x53, EqNtfyStatus = 0x55;
        public const byte EqGetParam = 0x56, EqRetParam = 0x57, EqSetParam = 0x58, EqNtfyParam = 0x59;

        public const byte NcAsmGetParam = 0x66, NcAsmRetParam = 0x67, NcAsmSetParam = 0x68, NcAsmNtfyParam = 0x69;

        public const byte PlayGetStatus = 0xA2, PlayRetStatus = 0xA3, PlaySetStatus = 0xA4, PlayNtfyStatus = 0xA5;
        public const byte PlayGetParam = 0xA6, PlayRetParam = 0xA7, PlaySetParam = 0xA8, PlayNtfyParam = 0xA9;

        public const byte LogSetStatus = 0xC4, LogNtfyParam = 0xC9;

        public const byte AudioGetParam = 0xE6, AudioRetParam = 0xE7, AudioSetParam = 0xE8, AudioNtfyParam = 0xE9;

        public const byte SystemGetParam = 0xF6, SystemRetParam = 0xF7, SystemSetParam = 0xF8, SystemNtfyParam = 0xF9;
    }

    /// <summary>
    /// Feature IDs the headphones list in their "supported functions" reply.
    /// We only use a feature if the headphones say they have it.
    /// </summary>
    public static class Feature
    {
        public const byte PresetEq = 0x50;
        public const byte PresetEqNonCustomizable = 0x52;
        public const byte PresetEqAndUltMode = 0x53;
        public const byte BatteryLevel = 0x20;
        public const byte PowerOff = 0x23;
        public const byte AutoPowerOff = 0x24;
        public const byte AutoPowerOffWithWearingDetection = 0x25;
        public const byte BatteryLevelWithThreshold = 0x28;
        public const byte AmbientSoundOnly = 0x67;                     // ambient only, no NC
        public const byte NcAsmDual = 0x6B;                            // NC + ambient + level
        public const byte NcAsmDualWithNoiseAdaptation = 0x6D;         // same + "adaptive"
        public const byte Dsee = 0xE2;                                 // "UPSCALING_AUTO_OFF"
        public const byte PauseWhenRemoved = 0xF1;
    }

    /// <summary>Equalizer presets, with the names Sony's app uses.</summary>
    public static class EqPresets
    {
        public static readonly (byte Id, string Name)[] All =
        {
            (0x00, "Off"),
            (0x10, "Bright"),
            (0x11, "Excited"),
            (0x12, "Mellow"),
            (0x13, "Relaxed"),
            (0x14, "Vocal"),
            (0x15, "Treble"),
            (0x16, "Bass"),
            (0x17, "Speech"),
            (0xA0, "Custom"),
            (0xA1, "User 1"),
            (0xA2, "User 2"),
        };

        public static string NameOf(byte id)
        {
            foreach (var p in All) if (p.Id == id) return p.Name;
            return $"Preset 0x{id:X2}";
        }
    }

    /// <summary>Auto power off choices.</summary>
    public static class AutoPowerOffChoices
    {
        public static readonly (byte Id, string Name)[] All =
        {
            (0x11, "Never"),
            (0x00, "After 5 min"),
            (0x04, "After 15 min"),
            (0x01, "After 30 min"),
            (0x02, "After 1 hour"),
            (0x03, "After 3 hours"),
        };
        /// <summary>Only on headphones with wearing detection.</summary>
        public const byte WhenTakenOff = 0x10;
    }

    public enum PlaybackKey : byte
    {
        Pause = 0x01,
        Next = 0x02,      // TRACK_UP
        Previous = 0x03,  // TRACK_DOWN
        Play = 0x07,
    }

    /// <summary>Builds the payload bytes for each request we send.</summary>
    public static class Requests
    {
        // ---- Handshake ----
        public static byte[] ProtocolInfo() => new byte[] { Cmd.GetProtocolInfo, 0x00 };
        public static byte[] CapabilityInfo() => new byte[] { Cmd.GetCapabilityInfo, 0x00 };
        public static byte[] SupportFunctions() => new byte[] { Cmd.GetSupportFunction, 0x00 };
        public static byte[] ModelName() => new byte[] { Cmd.GetDeviceInfo, 0x01 };
        public static byte[] FirmwareVersion() => new byte[] { Cmd.GetDeviceInfo, 0x02 };
        /// <summary>Sony's own app sends this once at the end of setup.</summary>
        public static byte[] LogSetStatus() => new byte[] { Cmd.LogSetStatus, 0x01, 0x00 };

        // ---- Reads ----
        public static byte[] GetBattery(bool withThreshold) =>
            new byte[] { Cmd.PowerGetStatus, withThreshold ? (byte)0x08 : (byte)0x00 };
        public static byte[] GetNcAsm(byte inquiredType) => new byte[] { Cmd.NcAsmGetParam, inquiredType };
        public static byte[] GetEq() => new byte[] { Cmd.EqGetParam, 0x00 };
        public static byte[] GetEqAndUlt() => new byte[] { Cmd.EqGetParam, 0x03 };
        public static byte[] GetDsee() => new byte[] { Cmd.AudioGetParam, 0x01 };
        public static byte[] GetVolume() => new byte[] { Cmd.PlayGetParam, 0x20 };
        public static byte[] GetNowPlaying() => new byte[] { Cmd.PlayGetParam, 0x01 };
        public static byte[] GetPlayStatus() => new byte[] { Cmd.PlayGetStatus, 0x01 };
        public static byte[] GetPauseWhenRemoved() => new byte[] { Cmd.SystemGetParam, 0x01 };
        public static byte[] GetAutoPowerOff(bool wearingVariant) =>
            new byte[] { Cmd.PowerGetParam, wearingVariant ? (byte)0x05 : (byte)0x04 };

        // ---- Writes ----

        /// <summary>
        /// Noise control. type is 0x17 (NC+ambient), 0x19 (same + adaptive) or 0x22 (ambient only).
        /// on=false means "Off". ambient=true means Ambient Sound, false means Noise Cancelling.
        /// </summary>
        public static byte[] SetNcAsm(byte type, bool on, bool ambient, bool voice, byte level,
                                      byte adaptiveOn = 0, byte adaptiveSensitivity = 0)
        {
            byte total = on ? (byte)1 : (byte)0;
            byte voiceByte = voice ? (byte)1 : (byte)0;
            return type switch
            {
                0x22 => new byte[] { Cmd.NcAsmSetParam, 0x22, 0x01, total, voiceByte, level },
                0x19 => new byte[] { Cmd.NcAsmSetParam, 0x19, 0x01, total, ambient ? (byte)1 : (byte)0, voiceByte, level,
                                     adaptiveOn, adaptiveSensitivity },
                _ => new byte[] { Cmd.NcAsmSetParam, 0x17, 0x01, total, ambient ? (byte)1 : (byte)0, voiceByte, level },
            };
        }

        /// <summary>Choose an EQ preset (no custom bands).</summary>
        public static byte[] SetEqPreset(byte presetId) => new byte[] { Cmd.EqSetParam, 0x00, presetId, 0x00 };

        /// <summary>Preset plus custom band values (plain EQ message, for non-ULT models).</summary>
        public static byte[] SetEqPresetWithBands(byte presetId, byte[] bands)
        {
            var p = new byte[4 + bands.Length];
            p[0] = Cmd.EqSetParam; p[1] = 0x00; p[2] = presetId; p[3] = (byte)bands.Length;
            bands.CopyTo(p, 4);
            return p;
        }

        /// <summary>Preset + ULT mode (0 = off, 1 = ULT 1, 2 = ULT 2). Bands are echoed back unchanged.</summary>
        public static byte[] SetEqAndUlt(byte presetId, byte ultMode, byte[] bands)
        {
            var p = new byte[5 + bands.Length];
            p[0] = Cmd.EqSetParam; p[1] = 0x03; p[2] = presetId; p[3] = ultMode; p[4] = (byte)bands.Length;
            bands.CopyTo(p, 5);
            return p;
        }

        /// <summary>DSEE: true = Auto, false = Off.</summary>
        public static byte[] SetDsee(bool on) => new byte[] { Cmd.AudioSetParam, 0x01, on ? (byte)1 : (byte)0 };

        /// <summary>Headphone volume 0-30.</summary>
        public static byte[] SetVolume(byte volume) => new byte[] { Cmd.PlaySetParam, 0x20, volume };

        /// <summary>Play / pause / next / previous.</summary>
        public static byte[] PressKey(PlaybackKey key) => new byte[] { Cmd.PlaySetStatus, 0x01, 0x00, (byte)key };

        /// <summary>Pause when taken off. Note: Sony uses 0 = enabled, 1 = disabled.</summary>
        public static byte[] SetPauseWhenRemoved(bool enabled) =>
            new byte[] { Cmd.SystemSetParam, 0x01, enabled ? (byte)0 : (byte)1 };

        public static byte[] SetAutoPowerOff(bool wearingVariant, byte choice) =>
            new byte[] { Cmd.PowerSetParam, wearingVariant ? (byte)0x05 : (byte)0x04, choice, 0x00 };

        /// <summary>Turns the headphones off.</summary>
        public static byte[] PowerOff() => new byte[] { Cmd.PowerSetStatus, 0x03, 0x01 };
    }
}
