namespace MaxMix.Core.Protocol;

using MaxMix.Core.Models;

/// <summary>
/// Encodes/decodes the binary serial protocol. The protocol is 1 command byte
/// followed by a fixed-size payload (no framing, no CRC, little-endian).
/// </summary>
public interface IProtocolCodec
{
    /// <summary>Encodes a TEST command (no payload). Returns a 1-byte frame.</summary>
    byte[] EncodeTest();

    /// <summary>Encodes an OK command (no payload). Returns a 1-byte frame.</summary>
    byte[] EncodeOk();

    /// <summary>Encodes SETTINGS (1 + 14 = 15 bytes).</summary>
    byte[] EncodeSettings(DeviceSettings settings);

    /// <summary>Encodes SESSION_INFO (1 + 5 = 6 bytes).</summary>
    byte[] EncodeSessionInfo(SessionInfo info);

    /// <summary>
    /// Encodes one of the four SESSION_DATA commands (CURRENT/ALTERNATE/PREVIOUS/NEXT).
    /// Result is 1 + 32 = 33 bytes. Throws if <paramref name="command"/> is not 5..8.
    /// </summary>
    byte[] EncodeSessionData(Command command, SessionData session);

    /// <summary>
    /// Encodes one of the four VOLUME_*_CHANGE commands (9..12).
    /// Result is 1 + 2 = 3 bytes. Throws if <paramref name="command"/> is not 9..12.
    /// </summary>
    byte[] EncodeVolumeChange(Command command, VolumeData volume);

    /// <summary>Encodes MODE_STATES (1 + 5 = 6 bytes).</summary>
    byte[] EncodeModeStates(ModeStates states);

    /// <summary>
    /// Returns the fixed payload size firmware expects for the given command.
    /// Returns 0 for TEST/OK (no payload). Throws for invalid command bytes.
    /// </summary>
    int ExpectedPayloadSize(Command command);

    /// <summary>
    /// Parses a VolumeData from exactly 2 bytes.
    /// </summary>
    VolumeData DecodeVolumeData(ReadOnlySpan<byte> payload);

    /// <summary>
    /// Parses a SessionData from exactly 32 bytes (30-byte name + 2-byte VolumeData).
    /// Name is read up to the first null terminator (firmware uses strlen).
    /// </summary>
    SessionData DecodeSessionData(ReadOnlySpan<byte> payload);

    /// <summary>
    /// Parses a SessionInfo from exactly 5 bytes.
    /// </summary>
    SessionInfo DecodeSessionInfo(ReadOnlySpan<byte> payload);

    /// <summary>
    /// Parses a ModeStates from exactly 5 bytes.
    /// </summary>
    ModeStates DecodeModeStates(ReadOnlySpan<byte> payload);

    /// <summary>
    /// Parses a DeviceSettings from exactly 14 bytes.
    /// </summary>
    DeviceSettings DecodeSettings(ReadOnlySpan<byte> payload);
}
