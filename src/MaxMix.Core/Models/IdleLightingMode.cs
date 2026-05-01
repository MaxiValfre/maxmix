namespace MaxMix.Core.Models;

/// <summary>
/// Desktop-side lighting behavior used while MaxMix is idle.
/// The firmware still only receives static colors; animated modes are emulated
/// by the desktop re-sending SETTINGS periodically with interpolated colors.
/// </summary>
public enum IdleLightingMode : byte
{
    Static = 0,
    Pulse = 1,
    Fade = 2,
}
