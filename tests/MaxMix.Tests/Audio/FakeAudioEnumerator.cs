namespace MaxMix.Tests.Audio;

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MaxMix.Audio.Sessions;
using MaxMix.Audio.Wasapi;

/// <summary>
/// Test double for <see cref="IAudioEnumerator"/>. Holds a mutable list of
/// raw sessions so tests can simulate WASAPI snapshots over time without
/// touching real device APIs.
/// </summary>
internal sealed class FakeAudioEnumerator : IAudioEnumerator
{
    public List<RawAudioSession> Sessions { get; } = new();
    public List<(byte id, byte volume, bool muted)> SetVolumeCalls { get; } = new();

    public void SetSessions(params RawAudioSession[] sessions)
    {
        Sessions.Clear();
        Sessions.AddRange(sessions);
    }

    public IReadOnlyList<RawAudioSession> EnumerateSessions() => Sessions;

    public Task SetVolumeAsync(byte sessionId, byte volume, bool isMuted, CancellationToken cancellationToken = default)
    {
        SetVolumeCalls.Add((sessionId, volume, isMuted));
        return Task.CompletedTask;
    }
}
