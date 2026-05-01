namespace MaxMix.Core.Models;

/// <summary>
/// 24-bit RGB color. Wire format on SETTINGS = 3 bytes in R,G,B order.
/// IMPORTANT: original software had bug #4 (b = g) — keep R,G,B distinct.
/// </summary>
public readonly record struct RgbColor(byte R, byte G, byte B);
