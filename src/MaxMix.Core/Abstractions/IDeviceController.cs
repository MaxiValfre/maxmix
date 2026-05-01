namespace MaxMix.Core.Abstractions;

using MaxMix.Core.Models;

/// <summary>
/// Orchestrates the bridge between IAudioSessionManager and ISerialConnection.
/// Maps WASAPI sessions onto the firmware's 4-slot display model and handles
/// bidirectional volume/mute/mode sync. See spec section 3.
/// </summary>
public interface IDeviceController
{
    /// <summary>The mode currently shown on the device.</summary>
    DisplayMode CurrentMode { get; }

    /// <summary>Index into the active mode's session list pointing at the slot
    /// shown as "current". Wraps around the count of sessions for that mode.</summary>
    int CurrentIndex { get; }

    Task StartAsync(CancellationToken cancellationToken = default);
    Task StopAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Pushes the current snapshot of audio sessions and settings to the device
    /// (full SESSION_INFO + SESSION_DATA + MODE_STATES + SETTINGS sweep).
    /// Used after handshake and on mode change.
    /// </summary>
    Task SyncStateAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Cycles to the next mode in the user-visible cycle:
    /// Output → Input → Application → Game → Output. Splash is a transient
    /// firmware-internal mode and is NOT part of the user cycle.
    /// Bug #2 fix: uses ((index + 1) % count) operator precedence — NOT
    /// (index + 1 % count) which is a no-op.
    /// </summary>
    Task CycleModeAsync(CancellationToken cancellationToken = default);

    /// <summary>Navigates to the next session in the current mode, with wraparound.</summary>
    Task NextSessionAsync(CancellationToken cancellationToken = default);

    /// <summary>Navigates to the previous session in the current mode, with wraparound.</summary>
    Task PreviousSessionAsync(CancellationToken cancellationToken = default);
}
