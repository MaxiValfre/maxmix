namespace MaxMix.Serial;

using System;
using System.Text;
using MaxMix.Core.Models;
using MaxMix.Core.Protocol;
using MaxMix.Core.Sanitization;

/// <summary>
/// Binary serial protocol codec. The wire format is documented in the
/// firmware spec: 1 command byte followed by a fixed-size payload, no framing,
/// no CRC, little-endian. Misaligned bytes desync the firmware permanently —
/// every encode/decode here is size-strict by design.
/// </summary>
public sealed class BinaryProtocolCodec : IProtocolCodec
{
    // Wire-format payload sizes per command.
    public const int SettingsPayloadSize    = 14;
    public const int SessionInfoPayloadSize = 5;
    public const int SessionDataPayloadSize = 32;
    public const int VolumeDataPayloadSize  = 2;
    public const int ModeStatesPayloadSize  = 5;
    public const int SessionNameMaxBytes    = 30;

    // Bit masks for the packed VolumeData fields.
    private const byte SevenBitMask = 0x7F;
    private const byte HighBit      = 0x80;

    // ---------- Encoding ----------

    public byte[] EncodeTest() => new[] { (byte)Command.Test };
    public byte[] EncodeOk()   => new[] { (byte)Command.Ok };

    public byte[] EncodeSettings(DeviceSettings settings)
    {
        var frame = new byte[1 + SettingsPayloadSize];
        frame[0] = (byte)Command.Settings;

        frame[1] = settings.SleepAfterSeconds;
        frame[2] = (byte)((settings.AccelerationPercentage & SevenBitMask)
                          | (settings.ContinuousScroll ? HighBit : 0));

        WriteRgb(frame, 3,  settings.VolumeMinColor);
        WriteRgb(frame, 6,  settings.VolumeMaxColor);
        WriteRgb(frame, 9,  settings.MixChannelAColor);
        WriteRgb(frame, 12, settings.MixChannelBColor);

        return frame;
    }

    public byte[] EncodeSessionInfo(SessionInfo info)
    {
        var frame = new byte[1 + SessionInfoPayloadSize];
        frame[0] = (byte)Command.SessionInfo;
        frame[1] = (byte)info.Mode;
        frame[2] = info.Current;
        frame[3] = info.OutputCount;
        frame[4] = info.InputCount;
        frame[5] = info.ApplicationCount;
        return frame;
    }

    public byte[] EncodeSessionData(Command command, SessionData session)
    {
        if (!IsSessionDataCommand(command))
        {
            throw new ArgumentException(
                $"Command {command} is not a SESSION_DATA command (5..8).",
                nameof(command));
        }

        var frame = new byte[1 + SessionDataPayloadSize];
        frame[0] = (byte)command;

        // Name region: bytes 1..30. Always zero-initialised by `new byte[]`,
        // so unused trailing bytes are already null. We only need to write
        // the name (truncated so a null terminator fits within the 30-byte field).
        WriteNullTerminatedName(frame.AsSpan(1, SessionNameMaxBytes), session.Name);

        // VolumeData region: bytes 31..32.
        WriteVolumeData(frame.AsSpan(31, VolumeDataPayloadSize), session.Volume);

        return frame;
    }

    public byte[] EncodeVolumeChange(Command command, VolumeData volume)
    {
        if (!IsVolumeChangeCommand(command))
        {
            throw new ArgumentException(
                $"Command {command} is not a VOLUME_*_CHANGE command (9..12).",
                nameof(command));
        }

        var frame = new byte[1 + VolumeDataPayloadSize];
        frame[0] = (byte)command;
        WriteVolumeData(frame.AsSpan(1, VolumeDataPayloadSize), volume);
        return frame;
    }

    public byte[] EncodeModeStates(ModeStates states)
    {
        var frame = new byte[1 + ModeStatesPayloadSize];
        frame[0] = (byte)Command.ModeStates;
        frame[1] = states.Splash;
        frame[2] = states.Output;
        frame[3] = states.Input;
        frame[4] = states.Application;
        frame[5] = states.Game;
        return frame;
    }

    public int ExpectedPayloadSize(Command command) => command switch
    {
        Command.Test             => 0,
        Command.Ok               => 0,
        Command.Settings         => SettingsPayloadSize,
        Command.SessionInfo      => SessionInfoPayloadSize,
        Command.CurrentSession or
        Command.AlternateSession or
        Command.PreviousSession or
        Command.NextSession      => SessionDataPayloadSize,
        Command.VolumeCurrChange or
        Command.VolumeAltChange  or
        Command.VolumePrevChange or
        Command.VolumeNextChange => VolumeDataPayloadSize,
        Command.ModeStates       => ModeStatesPayloadSize,
        Command.None             => 0,
        Command.Debug            => 0,
        _ => throw new ArgumentOutOfRangeException(nameof(command), command, "Unknown command.")
    };

    // ---------- Decoding ----------

    public VolumeData DecodeVolumeData(ReadOnlySpan<byte> payload)
    {
        EnsureSize(Command.VolumeCurrChange, payload.Length, VolumeDataPayloadSize);
        return ReadVolumeData(payload);
    }

    public SessionData DecodeSessionData(ReadOnlySpan<byte> payload)
    {
        EnsureSize(Command.CurrentSession, payload.Length, SessionDataPayloadSize);

        var nameSpan = payload.Slice(0, SessionNameMaxBytes);
        var nullIndex = nameSpan.IndexOf((byte)0);
        var nameLength = nullIndex >= 0 ? nullIndex : SessionNameMaxBytes;
        var name = Encoding.UTF8.GetString(nameSpan.Slice(0, nameLength));

        var volume = ReadVolumeData(payload.Slice(30, VolumeDataPayloadSize));
        return new SessionData(name, volume);
    }

    public SessionInfo DecodeSessionInfo(ReadOnlySpan<byte> payload)
    {
        EnsureSize(Command.SessionInfo, payload.Length, SessionInfoPayloadSize);
        return new SessionInfo(
            Mode: (DisplayMode)payload[0],
            Current: payload[1],
            OutputCount: payload[2],
            InputCount: payload[3],
            ApplicationCount: payload[4]);
    }

    public ModeStates DecodeModeStates(ReadOnlySpan<byte> payload)
    {
        EnsureSize(Command.ModeStates, payload.Length, ModeStatesPayloadSize);
        return new ModeStates(payload[0], payload[1], payload[2], payload[3], payload[4]);
    }

    public DeviceSettings DecodeSettings(ReadOnlySpan<byte> payload)
    {
        EnsureSize(Command.Settings, payload.Length, SettingsPayloadSize);

        byte sleep = payload[0];
        byte accelByte = payload[1];
        byte accel = (byte)(accelByte & SevenBitMask);
        bool continuous = (accelByte & HighBit) != 0;

        return new DeviceSettings(
            SleepAfterSeconds: sleep,
            AccelerationPercentage: accel,
            ContinuousScroll: continuous,
            VolumeMinColor:   ReadRgb(payload.Slice(2, 3)),
            VolumeMaxColor:   ReadRgb(payload.Slice(5, 3)),
            MixChannelAColor: ReadRgb(payload.Slice(8, 3)),
            MixChannelBColor: ReadRgb(payload.Slice(11, 3)));
    }

    // ---------- Helpers ----------

    private static bool IsSessionDataCommand(Command c) =>
        c is Command.CurrentSession or Command.AlternateSession
          or Command.PreviousSession or Command.NextSession;

    private static bool IsVolumeChangeCommand(Command c) =>
        c is Command.VolumeCurrChange or Command.VolumeAltChange
          or Command.VolumePrevChange or Command.VolumeNextChange;

    private static void WriteVolumeData(Span<byte> dst, VolumeData v)
    {
        dst[0] = (byte)((v.Id & SevenBitMask) | (v.IsDefault ? HighBit : 0));
        dst[1] = (byte)((v.Volume & SevenBitMask) | (v.IsMuted ? HighBit : 0));
    }

    private static VolumeData ReadVolumeData(ReadOnlySpan<byte> src)
    {
        byte id        = (byte)(src[0] & SevenBitMask);
        bool isDefault = (src[0] & HighBit) != 0;
        byte volume    = (byte)(src[1] & SevenBitMask);
        bool isMuted   = (src[1] & HighBit) != 0;
        return new VolumeData(id, isDefault, volume, isMuted);
    }

    private static void WriteRgb(Span<byte> dst, int offset, RgbColor c)
    {
        dst[offset]     = c.R;
        dst[offset + 1] = c.G;
        dst[offset + 2] = c.B;
    }

    private static RgbColor ReadRgb(ReadOnlySpan<byte> src) => new(src[0], src[1], src[2]);

    /// <summary>
    /// Writes <paramref name="name"/> into <paramref name="dst"/> (length 30) as
    /// pure ASCII, guaranteeing a null terminator within the field.
    ///
    /// <para>The name is first sanitized via <see cref="NameSanitizer.ToAscii"/>
    /// which returns at most 29 ASCII characters (one byte each). The codec
    /// owns the trailing null byte. Sanitization is the codec's responsibility
    /// because the firmware OLED can only render printable ASCII (0x20-0x7E);
    /// passing UTF-8 multi-byte sequences corrupts the OLED display.</para>
    /// </summary>
    private static void WriteNullTerminatedName(Span<byte> dst, string name)
    {
        // dst is already zero-initialised by the caller's `new byte[]`.
        var ascii = NameSanitizer.ToAscii(name);
        if (ascii.Length == 0) return;

        // ASCII guarantees one byte per char. NameSanitizer caps the output at
        // MaxLength (29), leaving dst[29] as the null terminator. Defensive
        // clamp here covers any future change to MaxLength.
        int maxBytes = dst.Length - 1; // reserve final byte for null terminator
        int bytesToCopy = Math.Min(ascii.Length, maxBytes);

        // ASCII path: every char is in [0x20, 0x7E], fits in a single byte.
        // Encoding.ASCII.GetBytes is sufficient and avoids the encoder ceremony.
        Encoding.ASCII.GetBytes(ascii.AsSpan(0, bytesToCopy), dst.Slice(0, bytesToCopy));

        // dst[bytesToCopy] is already 0 (zero-init); a null terminator exists
        // either right after the body or at dst[dst.Length - 1] worst case.
    }

    private static void EnsureSize(Command command, int actual, int expected)
    {
        if (actual != expected)
        {
            throw new PayloadSizeMismatchException(command, expected, actual);
        }
    }
}
