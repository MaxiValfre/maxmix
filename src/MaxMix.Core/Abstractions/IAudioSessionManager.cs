namespace MaxMix.Core.Abstractions;

using MaxMix.Core.Events;

/// <summary>
/// Enumerates and tracks WASAPI audio sessions: output devices, input devices,
/// and application sessions. Event-driven via WASAPI notifications, with a
/// reconciliation timer to catch missed events. See spec section 2.
/// </summary>
public interface IAudioSessionManager
{
    IReadOnlyCollection<AudioSessionSnapshot> Sessions { get; }

    event EventHandler<SessionAddedEvent>? SessionAdded;
    event EventHandler<SessionRemovedEvent>? SessionRemoved;
    event EventHandler<VolumeChangedEvent>? VolumeChanged;
    event EventHandler<MuteChangedEvent>? MuteChanged;

    Task StartAsync(CancellationToken cancellationToken = default);
    Task StopAsync(CancellationToken cancellationToken = default);

    /// <summary>Sets a session's volume (0..100) and mute by stable id.</summary>
    Task SetVolumeAsync(byte sessionId, byte volume, bool isMuted, CancellationToken cancellationToken = default);
}

/// <summary>
/// Immutable snapshot of one audio session as the orchestrator sees it.
/// </summary>
public readonly record struct AudioSessionSnapshot(
    byte Id,
    string DisplayName,
    string ExecutablePath,
    byte Volume,
    bool IsMuted,
    bool IsDefault,
    AudioSessionKind Kind);

public enum AudioSessionKind
{
    OutputDevice,
    InputDevice,
    Application
}
