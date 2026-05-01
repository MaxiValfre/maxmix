namespace MaxMix.Audio.Sessions;

using MaxMix.Core.Abstractions;

/// <summary>
/// Raw view of one audio session as observed in a single WASAPI snapshot.
/// Multiple raw sessions can share an executable path (e.g. two Chrome tabs);
/// <see cref="SessionRegistry"/> groups them into a single logical entry.
/// </summary>
/// <param name="ExecutablePath">
/// Lowercased absolute path. For OS endpoint sessions (output / input devices)
/// this is a stable device identifier rather than a real exe path.
/// </param>
/// <param name="DisplayName">User-visible name (firmware truncates to 30 bytes).</param>
/// <param name="Volume">0..100, master volume on the session.</param>
/// <param name="IsMuted">True if the session is muted at the WASAPI level.</param>
/// <param name="IsDefault">
/// True if this represents the current default device for its kind.
/// Always false for <see cref="AudioSessionKind.Application"/>.
/// </param>
/// <param name="Kind">Output device, input device, or application session.</param>
public readonly record struct RawAudioSession(
    string ExecutablePath,
    string DisplayName,
    byte Volume,
    bool IsMuted,
    bool IsDefault,
    AudioSessionKind Kind);
