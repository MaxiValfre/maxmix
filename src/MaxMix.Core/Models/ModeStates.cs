namespace MaxMix.Core.Models;

/// <summary>
/// Per-mode enabled flags. Wire format = 5 bytes (one byte per mode).
/// Order matches DisplayMode enum: splash, output, input, application, game.
/// </summary>
public readonly record struct ModeStates(
    byte Splash,
    byte Output,
    byte Input,
    byte Application,
    byte Game);
