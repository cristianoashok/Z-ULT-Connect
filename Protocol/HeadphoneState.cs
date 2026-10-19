// HeadphoneState.cs
// ---------------------------------------------------------------------------
// Everything we currently know about the headphones. The Session fills this
// in from the headphones' replies; the window reads it to draw the controls.
// A null value means "not received yet / not supported".
// ---------------------------------------------------------------------------
using System.Collections.Generic;

namespace ZUltConnect.Protocol
{
    public enum NoiseMode { NoiseCancelling, Ambient, Off }

    public sealed class HeadphoneState
    {
        // Identity
        public string? ModelName;
        public string? FirmwareVersion;
        public bool HasTable1 = true;

        /// <summary>Feature IDs the headphones say they support (see <see cref="Feature"/>).</summary>
        public readonly HashSet<byte> Supported = new();
        public bool SupportListReceived;

        // Battery
        public int? BatteryPercent;
        public bool Charging;

        // Noise cancelling / ambient sound
        /// <summary>Which message variant this model uses: 0x17, 0x19 or 0x22.</summary>
        public byte NcAsmType = 0x17;
        public bool? NcAsmOn;
        public bool NcAsmAmbient;      // true = Ambient Sound, false = Noise Cancelling
        public bool VoicePassthrough;  // "Focus on Voice"
        public byte AmbientLevel = 15; // 1-20
        public byte AdaptiveOn;
        public byte AdaptiveSensitivity;

        public NoiseMode? Noise => NcAsmOn switch
        {
            null => null,
            false => NoiseMode.Off,
            true => NcAsmAmbient ? NoiseMode.Ambient : NoiseMode.NoiseCancelling,
        };

        // Equalizer
        public byte? EqPreset;
        public byte[] EqBands = System.Array.Empty<byte>();

        // ULT mode (experimental)
        public byte? UltMode;          // 0 off, 1 ULT 1, 2 ULT 2
        public byte UltPreset;
        public byte[] UltBands = System.Array.Empty<byte>();

        /// <summary>The EQ band values the headphones last reported (raw 0-20, 10 = flat).</summary>
        public byte[] CurrentBands => UltBands.Length > 0 ? UltBands : EqBands;

        // Live info
        /// <summary>true = on your head, false = taken off, null = not reported yet.</summary>
        public bool? Worn;
        public string? TrackTitle, TrackAlbum, TrackArtist;

        // Other settings
        public bool? Dsee;
        public int? Volume;            // 0-30
        public bool? Playing;
        public bool? PauseWhenRemoved;
        public byte? AutoPowerOff;
        public bool AutoPowerOffWearingVariant;

        public bool Supports(byte feature) => !SupportListReceived || Supported.Contains(feature);
    }
}
