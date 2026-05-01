namespace MaxMix.Core.Models;

/// <summary>
/// Command IDs that flow over the serial wire between desktop and firmware.
/// Values are wire-format bytes — DO NOT renumber.
/// </summary>
public enum Command : byte
{
    None = 0,
    Test = 1,
    Ok = 2,
    Settings = 3,
    SessionInfo = 4,
    CurrentSession = 5,
    AlternateSession = 6,
    PreviousSession = 7,
    NextSession = 8,
    VolumeCurrChange = 9,
    VolumeAltChange = 10,
    VolumePrevChange = 11,
    VolumeNextChange = 12,
    ModeStates = 13,
    Debug = 14
}
