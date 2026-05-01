namespace MaxMix.Tests.Protocol;

using System;
using System.Linq;
using System.Text;
using MaxMix.Core.Models;
using MaxMix.Core.Protocol;
using MaxMix.Serial;
using Xunit;

/// <summary>
/// Byte-level tests for the binary serial protocol codec.
///
/// The protocol has NO framing, NO CRC, and NO length field. Every byte must
/// match what the firmware expects exactly — one wrong byte poisons every
/// subsequent read until reconnect. These tests are the contract.
/// </summary>
public class BinaryProtocolCodecTests
{
    private readonly BinaryProtocolCodec _codec = new();

    // ----- Command-byte / no-payload commands -----

    [Fact]
    public void EncodeTest_produces_single_command_byte()
    {
        var bytes = _codec.EncodeTest();

        Assert.Single(bytes);
        Assert.Equal((byte)Command.Test, bytes[0]);
        Assert.Equal((byte)1, bytes[0]);
    }

    [Fact]
    public void EncodeOk_produces_single_command_byte()
    {
        var bytes = _codec.EncodeOk();

        Assert.Single(bytes);
        Assert.Equal((byte)Command.Ok, bytes[0]);
        Assert.Equal((byte)2, bytes[0]);
    }

    [Theory]
    [InlineData(Command.Test, 0)]
    [InlineData(Command.Ok, 0)]
    [InlineData(Command.Settings, 14)]
    [InlineData(Command.SessionInfo, 5)]
    [InlineData(Command.CurrentSession, 32)]
    [InlineData(Command.AlternateSession, 32)]
    [InlineData(Command.PreviousSession, 32)]
    [InlineData(Command.NextSession, 32)]
    [InlineData(Command.VolumeCurrChange, 2)]
    [InlineData(Command.VolumeAltChange, 2)]
    [InlineData(Command.VolumePrevChange, 2)]
    [InlineData(Command.VolumeNextChange, 2)]
    [InlineData(Command.ModeStates, 5)]
    public void ExpectedPayloadSize_matches_firmware_contract(Command cmd, int expected)
    {
        Assert.Equal(expected, _codec.ExpectedPayloadSize(cmd));
    }

    // ----- VolumeData (2 bytes, packed bitfield) -----

    [Theory]
    // id, isDefault, volume, isMuted,  byte0,  byte1
    [InlineData(0,    false,    0,     false,   0x00,  0x00)]
    [InlineData(1,    false,    50,    false,   0x01,  0x32)]
    [InlineData(127,  false,    100,   false,   0x7F,  0x64)]
    [InlineData(0,    true,     0,     false,   0x80,  0x00)]
    [InlineData(0,    false,    0,     true,    0x00,  0x80)]
    [InlineData(127,  true,     100,   true,    0xFF,  0xE4)]
    [InlineData(42,   true,     75,    false,   0xAA,  0x4B)]
    [InlineData(42,   false,    75,    true,    0x2A,  0xCB)]
    public void EncodeVolumeChange_packs_id_default_volume_muted_into_2_bytes(
        byte id, bool isDefault, byte volume, bool isMuted,
        byte expectedByte0, byte expectedByte1)
    {
        var data = new VolumeData(id, isDefault, volume, isMuted);

        var frame = _codec.EncodeVolumeChange(Command.VolumeCurrChange, data);

        Assert.Equal(3, frame.Length); // 1 cmd + 2 payload
        Assert.Equal((byte)Command.VolumeCurrChange, frame[0]);
        Assert.Equal(expectedByte0, frame[1]);
        Assert.Equal(expectedByte1, frame[2]);
    }

    [Theory]
    [InlineData(Command.VolumeCurrChange)]
    [InlineData(Command.VolumeAltChange)]
    [InlineData(Command.VolumePrevChange)]
    [InlineData(Command.VolumeNextChange)]
    public void EncodeVolumeChange_uses_caller_provided_command_byte(Command cmd)
    {
        var data = new VolumeData(5, false, 50, false);

        var frame = _codec.EncodeVolumeChange(cmd, data);

        Assert.Equal((byte)cmd, frame[0]);
    }

    [Fact]
    public void EncodeVolumeChange_rejects_non_volume_commands()
    {
        var data = new VolumeData(1, false, 50, false);
        Assert.Throws<ArgumentException>(() => _codec.EncodeVolumeChange(Command.Settings, data));
        Assert.Throws<ArgumentException>(() => _codec.EncodeVolumeChange(Command.CurrentSession, data));
    }

    [Theory]
    [InlineData(0,    false, 0,   false,  0x00, 0x00)]
    [InlineData(127,  true,  100, true,   0xFF, 0xE4)]
    [InlineData(42,   true,  75,  false,  0xAA, 0x4B)]
    public void DecodeVolumeData_unpacks_2_bytes_into_struct(
        byte id, bool isDefault, byte volume, bool isMuted,
        byte byte0, byte byte1)
    {
        var payload = new byte[] { byte0, byte1 };

        var data = _codec.DecodeVolumeData(payload);

        Assert.Equal(id, data.Id);
        Assert.Equal(isDefault, data.IsDefault);
        Assert.Equal(volume, data.Volume);
        Assert.Equal(isMuted, data.IsMuted);
    }

    [Fact]
    public void DecodeVolumeData_rejects_wrong_length()
    {
        Assert.Throws<PayloadSizeMismatchException>(
            () => _codec.DecodeVolumeData(new byte[] { 0x01 }));
        Assert.Throws<PayloadSizeMismatchException>(
            () => _codec.DecodeVolumeData(new byte[] { 0x01, 0x02, 0x03 }));
    }

    // ----- SessionData (32 bytes: name[30] + VolumeData[2]) -----

    [Fact]
    public void EncodeSessionData_short_name_is_null_terminated_within_30_bytes()
    {
        // Bug #3 fix: firmware uses strlen, so name MUST contain a null terminator.
        var session = new SessionData("Spotify", new VolumeData(2, false, 75, false));

        var frame = _codec.EncodeSessionData(Command.CurrentSession, session);

        Assert.Equal(33, frame.Length); // 1 + 30 + 2
        Assert.Equal((byte)Command.CurrentSession, frame[0]);

        // Name region is bytes 1..30 (30 bytes).
        var nameRegion = frame.AsSpan(1, 30);
        var expected = Encoding.UTF8.GetBytes("Spotify");
        for (int i = 0; i < expected.Length; i++)
        {
            Assert.Equal(expected[i], nameRegion[i]);
        }
        // The next byte after "Spotify" (length=7) must be the null terminator.
        Assert.Equal((byte)0, nameRegion[7]);
        // Everything after the null must also be zero (clean buffer).
        for (int i = 8; i < 30; i++)
        {
            Assert.Equal((byte)0, nameRegion[i]);
        }

        // VolumeData region is bytes 31..32.
        Assert.Equal(0x02, frame[31]);   // id=2, isDefault=false
        Assert.Equal((byte)75, frame[32]); // volume=75, isMuted=false
    }

    [Fact]
    public void EncodeSessionData_truncates_long_name_and_keeps_null_terminator()
    {
        // 41 chars — must be truncated so the final byte at index 29 is null.
        var longName = "Adobe Premiere Pro 2025 - Project1.prproj";
        var session = new SessionData(longName, new VolumeData(0, false, 0, false));

        var frame = _codec.EncodeSessionData(Command.CurrentSession, session);

        var nameRegion = frame.AsSpan(1, 30);
        Assert.Equal((byte)0, nameRegion[29]); // last byte MUST be null
        // The null must exist somewhere in the 30 bytes — verify by indexOf.
        var nullIndex = nameRegion.IndexOf((byte)0);
        Assert.InRange(nullIndex, 0, 29);
    }

    [Fact]
    public void EncodeSessionData_accented_name_is_sanitized_to_pure_ASCII()
    {
        // Spec (ascii-transliteration → "Codec receives pre-sanitized name"):
        // GIVEN a session with a Unicode name
        // WHEN the codec encodes the session for serial transmission
        // THEN the name has already been sanitized to ASCII by NameSanitizer.
        //
        // The codec MUST internally call NameSanitizer.ToAscii on the name
        // before writing to the 30-byte field, so every byte in the name
        // region is printable ASCII (0x00 for the null pad / unused tail,
        // 0x20-0x7E for the body).
        var session = new SessionData("Médialécteur", new VolumeData(1, false, 50, false));

        var frame = _codec.EncodeSessionData(Command.CurrentSession, session);

        var nameRegion = frame.AsSpan(1, 30);

        // Every byte must be either NUL (zero pad) or printable ASCII.
        for (int i = 0; i < nameRegion.Length; i++)
        {
            byte b = nameRegion[i];
            Assert.True(b == 0x00 || (b >= 0x20 && b <= 0x7E),
                $"Byte at index {i} is 0x{b:X2}, not ASCII-clean. Name region must be sanitized.");
        }

        // And the body before the null terminator must read as the transliteration.
        var nullIndex = nameRegion.IndexOf((byte)0);
        Assert.True(nullIndex > 0, "Expected sanitized text before the null terminator.");
        var decoded = Encoding.ASCII.GetString(nameRegion.Slice(0, nullIndex));
        Assert.Equal("Medialecteur", decoded);
    }

    [Fact]
    public void EncodeSessionData_emoji_and_symbols_are_stripped_to_ASCII_or_fallback()
    {
        // Triangulation: a name that strips entirely should fall back to "App"
        // (NameSanitizer's contract for non-empty input that fully strips).
        var session = new SessionData("🎵", new VolumeData(2, false, 25, false));

        var frame = _codec.EncodeSessionData(Command.CurrentSession, session);

        var nameRegion = frame.AsSpan(1, 30);
        for (int i = 0; i < nameRegion.Length; i++)
        {
            byte b = nameRegion[i];
            Assert.True(b == 0x00 || (b >= 0x20 && b <= 0x7E),
                $"Byte at index {i} is 0x{b:X2}, not ASCII-clean.");
        }

        var nullIndex = nameRegion.IndexOf((byte)0);
        Assert.Equal(3, nullIndex); // "App" fallback is 3 bytes
        var decoded = Encoding.ASCII.GetString(nameRegion.Slice(0, nullIndex));
        Assert.Equal("App", decoded);
    }

    [Fact]
    public void EncodeSessionData_empty_name_produces_all_zero_name_region()
    {
        var session = new SessionData(string.Empty, new VolumeData(0, true, 100, true));

        var frame = _codec.EncodeSessionData(Command.CurrentSession, session);

        var nameRegion = frame.AsSpan(1, 30);
        for (int i = 0; i < 30; i++)
        {
            Assert.Equal((byte)0, nameRegion[i]);
        }
        // VolumeData: id=0|isDefault → 0x80, volume=100|isMuted → 100|0x80 = 0xE4
        Assert.Equal(0x80, frame[31]);
        Assert.Equal(0xE4, frame[32]);
    }

    [Theory]
    [InlineData(Command.CurrentSession)]
    [InlineData(Command.AlternateSession)]
    [InlineData(Command.PreviousSession)]
    [InlineData(Command.NextSession)]
    public void EncodeSessionData_uses_caller_provided_command_byte(Command cmd)
    {
        var session = new SessionData("App", new VolumeData(1, false, 50, false));

        var frame = _codec.EncodeSessionData(cmd, session);

        Assert.Equal((byte)cmd, frame[0]);
        Assert.Equal(33, frame.Length);
    }

    [Fact]
    public void EncodeSessionData_rejects_non_session_commands()
    {
        var session = new SessionData("X", new VolumeData(0, false, 0, false));
        Assert.Throws<ArgumentException>(() => _codec.EncodeSessionData(Command.Settings, session));
        Assert.Throws<ArgumentException>(() => _codec.EncodeSessionData(Command.VolumeCurrChange, session));
    }

    [Fact]
    public void DecodeSessionData_reads_name_up_to_null_terminator()
    {
        var payload = new byte[32];
        var nameBytes = Encoding.UTF8.GetBytes("Discord");
        Array.Copy(nameBytes, 0, payload, 0, nameBytes.Length);
        // bytes 7..29 stay zero
        payload[30] = 0x05; // id=5, isDefault=false
        payload[31] = 0x14; // volume=20, isMuted=false

        var session = _codec.DecodeSessionData(payload);

        Assert.Equal("Discord", session.Name);
        Assert.Equal(5, session.Volume.Id);
        Assert.False(session.Volume.IsDefault);
        Assert.Equal(20, session.Volume.Volume);
        Assert.False(session.Volume.IsMuted);
    }

    [Fact]
    public void DecodeSessionData_rejects_wrong_length()
    {
        Assert.Throws<PayloadSizeMismatchException>(
            () => _codec.DecodeSessionData(new byte[31]));
        Assert.Throws<PayloadSizeMismatchException>(
            () => _codec.DecodeSessionData(new byte[33]));
    }

    [Fact]
    public void SessionData_roundtrip_preserves_short_name_and_volume()
    {
        var original = new SessionData("Chrome", new VolumeData(99, true, 42, true));

        var frame = _codec.EncodeSessionData(Command.AlternateSession, original);
        var decoded = _codec.DecodeSessionData(frame.AsSpan(1)); // skip command byte

        Assert.Equal("Chrome", decoded.Name);
        Assert.Equal(99, decoded.Volume.Id);
        Assert.True(decoded.Volume.IsDefault);
        Assert.Equal(42, decoded.Volume.Volume);
        Assert.True(decoded.Volume.IsMuted);
    }

    // ----- SessionInfo (5 bytes) -----

    [Fact]
    public void EncodeSessionInfo_writes_5_bytes_in_mode_current_out_in_app_order()
    {
        var info = new SessionInfo(DisplayMode.Application, Current: 1,
            OutputCount: 2, InputCount: 1, ApplicationCount: 4);

        var frame = _codec.EncodeSessionInfo(info);

        Assert.Equal(6, frame.Length); // 1 + 5
        Assert.Equal((byte)Command.SessionInfo, frame[0]);
        Assert.Equal((byte)DisplayMode.Application, frame[1]);
        Assert.Equal((byte)1, frame[2]);
        Assert.Equal((byte)2, frame[3]);
        Assert.Equal((byte)1, frame[4]);
        Assert.Equal((byte)4, frame[5]);
    }

    [Fact]
    public void SessionInfo_roundtrip()
    {
        var original = new SessionInfo(DisplayMode.Game, 3, 1, 2, 7);
        var frame = _codec.EncodeSessionInfo(original);
        var decoded = _codec.DecodeSessionInfo(frame.AsSpan(1));

        Assert.Equal(original, decoded);
    }

    [Fact]
    public void DecodeSessionInfo_rejects_wrong_length()
    {
        Assert.Throws<PayloadSizeMismatchException>(
            () => _codec.DecodeSessionInfo(new byte[4]));
    }

    // ----- ModeStates (5 bytes) -----

    [Fact]
    public void EncodeModeStates_writes_5_bytes_in_splash_out_in_app_game_order()
    {
        var states = new ModeStates(Splash: 1, Output: 1, Input: 0, Application: 1, Game: 0);

        var frame = _codec.EncodeModeStates(states);

        Assert.Equal(6, frame.Length); // 1 + 5
        Assert.Equal((byte)Command.ModeStates, frame[0]);
        Assert.Equal((byte)1, frame[1]);
        Assert.Equal((byte)1, frame[2]);
        Assert.Equal((byte)0, frame[3]);
        Assert.Equal((byte)1, frame[4]);
        Assert.Equal((byte)0, frame[5]);
    }

    [Fact]
    public void ModeStates_roundtrip()
    {
        var original = new ModeStates(0, 1, 1, 0, 1);
        var frame = _codec.EncodeModeStates(original);
        var decoded = _codec.DecodeModeStates(frame.AsSpan(1));

        Assert.Equal(original, decoded);
    }

    // ----- DeviceSettings (14 bytes) — Bug #4 RGB transposition fix -----

    [Fact]
    public void EncodeSettings_writes_14_byte_payload_with_correct_layout()
    {
        // Distinct values per channel so transposition is detectable.
        var settings = new DeviceSettings(
            SleepAfterSeconds: 5,
            AccelerationPercentage: 3,
            ContinuousScroll: true,
            VolumeMinColor:   new RgbColor(0x10, 0x20, 0x30),
            VolumeMaxColor:   new RgbColor(0x40, 0x50, 0x60),
            MixChannelAColor: new RgbColor(0x70, 0x80, 0x90),
            MixChannelBColor: new RgbColor(0xA0, 0xB0, 0xC0));

        var frame = _codec.EncodeSettings(settings);

        Assert.Equal(15, frame.Length); // 1 + 14
        Assert.Equal((byte)Command.Settings, frame[0]);
        Assert.Equal((byte)5, frame[1]); // sleepAfterSeconds

        // byte 2: bit 7 = continuousScroll (true), bits 0-6 = accel (3) → 0x83
        Assert.Equal(0x83, frame[2]);

        // RGB triples — verify R, G, B are NOT transposed (Bug #4).
        Assert.Equal(0x10, frame[3]);  Assert.Equal(0x20, frame[4]);  Assert.Equal(0x30, frame[5]);
        Assert.Equal(0x40, frame[6]);  Assert.Equal(0x50, frame[7]);  Assert.Equal(0x60, frame[8]);
        Assert.Equal(0x70, frame[9]);  Assert.Equal(0x80, frame[10]); Assert.Equal(0x90, frame[11]);
        Assert.Equal(0xA0, frame[12]); Assert.Equal(0xB0, frame[13]); Assert.Equal(0xC0, frame[14]);
    }

    [Fact]
    public void EncodeSettings_continuousScroll_false_clears_high_bit()
    {
        var settings = new DeviceSettings(
            SleepAfterSeconds: 0,
            AccelerationPercentage: 127, // max 7-bit value
            ContinuousScroll: false,
            VolumeMinColor: default, VolumeMaxColor: default,
            MixChannelAColor: default, MixChannelBColor: default);

        var frame = _codec.EncodeSettings(settings);

        // accel=127 (0x7F), contScroll=false → 0x7F
        Assert.Equal(0x7F, frame[2]);
    }

    [Fact]
    public void EncodeSettings_clamps_accel_to_7_bits()
    {
        // If a caller passed 200 (>127), the high bit MUST come from
        // continuousScroll alone, not bleed in from accel.
        var settings = new DeviceSettings(
            SleepAfterSeconds: 0,
            AccelerationPercentage: 200,
            ContinuousScroll: false,
            VolumeMinColor: default, VolumeMaxColor: default,
            MixChannelAColor: default, MixChannelBColor: default);

        var frame = _codec.EncodeSettings(settings);

        // 200 & 0x7F = 0x48; high bit must be 0 (contScroll=false).
        Assert.Equal(0x48, frame[2]);
    }

    [Fact]
    public void Settings_roundtrip_preserves_all_fields()
    {
        var original = new DeviceSettings(
            SleepAfterSeconds: 7,
            AccelerationPercentage: 50,
            ContinuousScroll: true,
            VolumeMinColor:   new RgbColor(0xFF, 0x80, 0x00),
            VolumeMaxColor:   new RgbColor(0x00, 0xFF, 0x80),
            MixChannelAColor: new RgbColor(0x80, 0x00, 0xFF),
            MixChannelBColor: new RgbColor(0x12, 0x34, 0x56));

        var frame = _codec.EncodeSettings(original);
        var decoded = _codec.DecodeSettings(frame.AsSpan(1));

        Assert.Equal(original, decoded);
    }

    [Fact]
    public void DecodeSettings_rejects_wrong_length()
    {
        Assert.Throws<PayloadSizeMismatchException>(
            () => _codec.DecodeSettings(new byte[13]));
        Assert.Throws<PayloadSizeMismatchException>(
            () => _codec.DecodeSettings(new byte[15]));
    }

    // ----- Boundary values -----

    [Fact]
    public void VolumeData_boundaries_zero_and_max_volume_are_preserved()
    {
        var zero = new VolumeData(0, false, 0, false);
        var max  = new VolumeData(127, true, 100, true);

        var zeroFrame = _codec.EncodeVolumeChange(Command.VolumeCurrChange, zero);
        var maxFrame  = _codec.EncodeVolumeChange(Command.VolumeCurrChange, max);

        Assert.Equal(0x00, zeroFrame[1]); Assert.Equal(0x00, zeroFrame[2]);
        Assert.Equal(0xFF, maxFrame[1]);  Assert.Equal(0xE4, maxFrame[2]); // 100 | 0x80 = 0xE4
    }
}
