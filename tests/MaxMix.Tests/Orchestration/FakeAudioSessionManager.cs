namespace MaxMix.Tests.Orchestration;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MaxMix.Core.Abstractions;
using MaxMix.Core.Events;

/// <summary>
/// Test double for <see cref="IAudioSessionManager"/>. Holds an in-memory list
/// of sessions and exposes raise* helpers that fire the corresponding events,
/// just as the real WASAPI manager would after detecting a Windows-side change.
/// </summary>
internal sealed class FakeAudioSessionManager : IAudioSessionManager
{
    public List<AudioSessionSnapshot> SessionList { get; } = new();
    public List<(byte id, byte volume, bool muted)> SetVolumeCalls { get; } = new();
    public bool Started { get; private set; }
    public bool Stopped { get; private set; }

    public IReadOnlyCollection<AudioSessionSnapshot> Sessions => SessionList;

    public event EventHandler<SessionAddedEvent>? SessionAdded;
    public event EventHandler<SessionRemovedEvent>? SessionRemoved;
    public event EventHandler<VolumeChangedEvent>? VolumeChanged;
    public event EventHandler<MuteChangedEvent>? MuteChanged;

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        Started = true;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        Stopped = true;
        return Task.CompletedTask;
    }

    public Task SetVolumeAsync(byte sessionId, byte volume, bool isMuted, CancellationToken cancellationToken = default)
    {
        SetVolumeCalls.Add((sessionId, volume, isMuted));
        // Mirror to local state so subsequent reconciliations reflect the change.
        for (int i = 0; i < SessionList.Count; i++)
        {
            if (SessionList[i].Id == sessionId)
            {
                SessionList[i] = SessionList[i] with { Volume = volume, IsMuted = isMuted };
                break;
            }
        }
        return Task.CompletedTask;
    }

    // ----- Test helpers -----

    public void AddAndRaise(AudioSessionSnapshot s)
    {
        SessionList.Add(s);
        SessionAdded?.Invoke(this, new SessionAddedEvent(s));
    }

    public void RemoveAndRaise(byte id)
    {
        SessionList.RemoveAll(x => x.Id == id);
        SessionRemoved?.Invoke(this, new SessionRemovedEvent(id));
    }

    public void RaiseVolumeChange(byte id, byte volume)
    {
        for (int i = 0; i < SessionList.Count; i++)
        {
            if (SessionList[i].Id == id)
            {
                SessionList[i] = SessionList[i] with { Volume = volume };
                break;
            }
        }
        VolumeChanged?.Invoke(this, new VolumeChangedEvent(id, volume));
    }

    public void RaiseMuteChange(byte id, bool isMuted)
    {
        for (int i = 0; i < SessionList.Count; i++)
        {
            if (SessionList[i].Id == id)
            {
                SessionList[i] = SessionList[i] with { IsMuted = isMuted };
                break;
            }
        }
        MuteChanged?.Invoke(this, new MuteChangedEvent(id, isMuted));
    }
}
