namespace MaxMix.Audio.Wasapi;

using MaxMix.Audio.Sessions;

/// <summary>
/// Abstraction over the WASAPI / NAudio session enumeration surface.
///
/// <para>
/// The real implementation (<see cref="NAudioWasapiEnumerator"/>) walks the
/// MMDeviceEnumerator + AudioSessionManager2 + IAudioSessionControl COM tree
/// on an STA thread. Tests inject a lightweight fake to stay deterministic.
/// </para>
///
/// <para>
/// This interface is intentionally pull-only: it returns "what's alive right now"
/// rather than raising events. Event-driven detection is layered on top by the
/// real implementation through native WASAPI callbacks that simply call
/// <see cref="WasapiSessionManager.ReconcileNow"/>.
/// </para>
/// </summary>
public interface IAudioEnumerator
{
    /// <summary>
    /// Returns a snapshot of every active session at this moment: output devices,
    /// input devices, and per-app sessions.
    /// </summary>
    IReadOnlyList<RawAudioSession> EnumerateSessions();

    /// <summary>
    /// Sets master volume and mute on every WASAPI session whose stable id matches
    /// <paramref name="sessionId"/>. Multi-tab apps (e.g. Chrome) get all their
    /// underlying streams updated.
    /// </summary>
    Task SetVolumeAsync(byte sessionId, byte volume, bool isMuted, CancellationToken ct = default);
}
