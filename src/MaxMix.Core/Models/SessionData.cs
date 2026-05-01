namespace MaxMix.Core.Models;

/// <summary>
/// One audio session as understood by the firmware. Wire format = 32 bytes:
///   bytes 0..29: name (UTF-8, null-padded; firmware uses strlen so MUST contain null within 30 bytes)
///   bytes 30..31: VolumeData
/// </summary>
public readonly record struct SessionData(string Name, VolumeData Volume);
