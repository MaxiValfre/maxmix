namespace MaxMix.Core.Models;

/// <summary>
/// Settings sent to firmware. Wire format = 14 bytes:
///   byte 0     : sleepAfterSeconds
///   byte 1     : bits 0-6 = accelerationPercentage, bit 7 = continuousScroll
///   bytes 2-4  : volumeMinColor RGB
///   bytes 5-7  : volumeMaxColor RGB
///   bytes 8-10 : mixChannelAColor RGB
///   bytes 11-13: mixChannelBColor RGB
/// </summary>
public readonly record struct DeviceSettings(
    byte SleepAfterSeconds,
    byte AccelerationPercentage,
    bool ContinuousScroll,
    RgbColor VolumeMinColor,
    RgbColor VolumeMaxColor,
    RgbColor MixChannelAColor,
    RgbColor MixChannelBColor);
