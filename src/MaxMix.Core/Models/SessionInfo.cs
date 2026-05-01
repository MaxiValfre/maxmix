namespace MaxMix.Core.Models;

/// <summary>
/// Aggregate counters telling the firmware how many sessions of each type exist
/// and which mode is currently active. Wire format = 5 bytes.
/// </summary>
public readonly record struct SessionInfo(
    DisplayMode Mode,
    byte Current,
    byte OutputCount,
    byte InputCount,
    byte ApplicationCount);
