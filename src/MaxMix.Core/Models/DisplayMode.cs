namespace MaxMix.Core.Models;

/// <summary>
/// Display modes shown on the firmware device. Wire-format byte values — DO NOT renumber.
/// </summary>
public enum DisplayMode : byte
{
    Splash = 0,
    Output = 1,
    Input = 2,
    Application = 3,
    Game = 4
}
