namespace MaxMix.Core.Models;

/// <summary>
/// Per-session volume payload. Wire format = 2 bytes:
///   byte 0: bits 0-6 = id (0..127), bit 7 = isDefault
///   byte 1: bits 0-6 = volume (0..100), bit 7 = isMuted
/// </summary>
public readonly record struct VolumeData(byte Id, bool IsDefault, byte Volume, bool IsMuted);
