namespace MaxMix.Audio.Sessions;

using System.Runtime.Versioning;
using MaxMix.Audio.Identity;
using MaxMix.Core.Abstractions;
using NAudio.CoreAudioApi;

/// <summary>
/// Thin wrapper over NAudio's <see cref="AudioSessionControl"/> that exposes
/// MaxMix-domain properties (stable id, normalized volume 0..100, etc.) without
/// leaking COM types upward.
///
/// <para>
/// Lifetime: an instance is valid only while the underlying <see cref="AudioSessionControl"/>
/// is alive. The enumerator builds a fresh wrapper on every snapshot pass and
/// disposes the previous batch — there is no long-lived caching here. Stability
/// across enumerations comes from the stable id (SHA1 of exe path), not from the
/// wrapper instance.
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class AudioSession
{
    private readonly AudioSessionControl _control;
    private readonly string _executablePath;

    public AudioSession(AudioSessionControl control, string executablePath, string fallbackName)
    {
        _control = control ?? throw new ArgumentNullException(nameof(control));
        _executablePath = executablePath ?? string.Empty;

        Id = SessionIdentity.ComputeId(_executablePath);
        DisplayName = string.IsNullOrWhiteSpace(control.DisplayName)
            ? fallbackName
            : control.DisplayName;
    }

    /// <summary>Stable 0..127 id derived from the executable path.</summary>
    public byte Id { get; }

    public string DisplayName { get; }

    public string ExecutablePath => _executablePath;

    /// <summary>Master volume 0..100, rounded from the simple volume float.</summary>
    public byte Volume
    {
        get
        {
            var v = _control.SimpleAudioVolume.Volume;
            if (float.IsNaN(v)) return 0;
            var clamped = Math.Clamp(v, 0f, 1f);
            return (byte)Math.Round(clamped * 100f);
        }
    }

    public bool IsMuted => _control.SimpleAudioVolume.Mute;

    /// <summary>
    /// Apps don't have a "default" concept; only output/input endpoints do.
    /// Always false for application sessions.
    /// </summary>
    public bool IsDefault => false;

    public AudioSessionKind Kind => AudioSessionKind.Application;

    public RawAudioSession ToRaw() =>
        new(ExecutablePath, DisplayName, Volume, IsMuted, IsDefault, Kind);

    public void SetVolume(byte volume, bool isMuted)
    {
        var clamped = Math.Clamp((int)volume, 0, 100);
        _control.SimpleAudioVolume.Volume = clamped / 100f;
        _control.SimpleAudioVolume.Mute = isMuted;
    }
}
