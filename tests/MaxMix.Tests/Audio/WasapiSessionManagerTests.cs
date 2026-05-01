namespace MaxMix.Tests.Audio;

using MaxMix.Audio;
using MaxMix.Audio.Sessions;
using MaxMix.Audio.Wasapi;
using MaxMix.Core.Abstractions;
using MaxMix.Core.Events;

/// <summary>
/// Tests for <see cref="WasapiSessionManager"/> — the IAudioSessionManager impl
/// that composes <see cref="SessionRegistry"/> with an <see cref="IAudioEnumerator"/>
/// and a reconciliation timer.
///
/// Real WASAPI calls are integration-test territory (need an audio device on
/// the host). Here we drive the manager with a fake enumerator that returns
/// scripted snapshots, asserting that:
///   - StartAsync triggers an initial reconciliation,
///   - the IAudioSessionManager events are forwarded from the registry,
///   - the reconciliation tick re-queries the enumerator and emits diffs,
///   - SetVolumeAsync delegates to the enumerator's volume setter,
///   - StopAsync stops the timer and unsubscribes.
/// </summary>
public class WasapiSessionManagerTests
{
    private static RawAudioSession Raw(string exe, string name = "App", byte vol = 50, bool muted = false)
        => new(exe, name, vol, muted, false, AudioSessionKind.Application);

    [Fact]
    public async Task StartAsync_PerformsInitialReconciliation_AndFiresAddEvents()
    {
        var enumerator = new FakeEnumerator();
        enumerator.Snapshots.Enqueue(new[] { Raw(@"C:\spotify.exe", "Spotify") });

        using var sut = new WasapiSessionManager(enumerator);
        var added = new List<SessionAddedEvent>();
        sut.SessionAdded += (_, e) => added.Add(e);

        await sut.StartAsync();

        Assert.Single(added);
        Assert.Equal("Spotify", added[0].Session.DisplayName);
    }

    [Fact]
    public async Task ReconcileTick_DiscoversSessionMissedByEvents()
    {
        // The KEY fix described in design: 2s reconciliation timer catches
        // sessions that WASAPI never raised an event for.
        var enumerator = new FakeEnumerator();
        enumerator.Snapshots.Enqueue(new[] { Raw(@"C:\spotify.exe", "Spotify") });
        enumerator.Snapshots.Enqueue(new[]
        {
            Raw(@"C:\spotify.exe", "Spotify"),
            Raw(@"C:\chrome.exe", "Chrome"),
        });

        using var sut = new WasapiSessionManager(enumerator);
        var added = new List<SessionAddedEvent>();
        sut.SessionAdded += (_, e) => added.Add(e);

        await sut.StartAsync();
        Assert.Single(added);

        sut.ReconcileNow(); // simulate timer tick deterministically

        Assert.Equal(2, added.Count);
        Assert.Equal("Chrome", added[1].Session.DisplayName);
    }

    [Fact]
    public async Task ReconcileTick_RemovesExpiredSessions()
    {
        var enumerator = new FakeEnumerator();
        enumerator.Snapshots.Enqueue(new[] { Raw(@"C:\spotify.exe") });
        enumerator.Snapshots.Enqueue(Array.Empty<RawAudioSession>());

        using var sut = new WasapiSessionManager(enumerator);
        var removed = new List<SessionRemovedEvent>();
        sut.SessionRemoved += (_, e) => removed.Add(e);

        await sut.StartAsync();
        sut.ReconcileNow();

        Assert.Single(removed);
    }

    [Fact]
    public async Task ReconcileTick_PropagatesVolumeChanges()
    {
        var enumerator = new FakeEnumerator();
        enumerator.Snapshots.Enqueue(new[] { Raw(@"C:\spotify.exe", vol: 50) });
        enumerator.Snapshots.Enqueue(new[] { Raw(@"C:\spotify.exe", vol: 80) });

        using var sut = new WasapiSessionManager(enumerator);
        var changes = new List<VolumeChangedEvent>();
        sut.VolumeChanged += (_, e) => changes.Add(e);

        await sut.StartAsync();
        sut.ReconcileNow();

        Assert.Single(changes);
        Assert.Equal((byte)80, changes[0].Volume);
    }

    [Fact]
    public async Task ReconcileTick_PropagatesMuteChanges()
    {
        var enumerator = new FakeEnumerator();
        enumerator.Snapshots.Enqueue(new[] { Raw(@"C:\spotify.exe", muted: false) });
        enumerator.Snapshots.Enqueue(new[] { Raw(@"C:\spotify.exe", muted: true) });

        using var sut = new WasapiSessionManager(enumerator);
        var changes = new List<MuteChangedEvent>();
        sut.MuteChanged += (_, e) => changes.Add(e);

        await sut.StartAsync();
        sut.ReconcileNow();

        Assert.Single(changes);
        Assert.True(changes[0].IsMuted);
    }

    [Fact]
    public async Task SessionsProperty_ExposesCurrentRegistry()
    {
        var enumerator = new FakeEnumerator();
        enumerator.Snapshots.Enqueue(new[]
        {
            Raw(@"C:\spotify.exe", "Spotify", vol: 60),
            Raw(@"C:\chrome.exe", "Chrome", vol: 40),
        });

        using var sut = new WasapiSessionManager(enumerator);
        await sut.StartAsync();

        Assert.Equal(2, sut.Sessions.Count);
        Assert.Contains(sut.Sessions, s => s.DisplayName == "Spotify" && s.Volume == 60);
        Assert.Contains(sut.Sessions, s => s.DisplayName == "Chrome" && s.Volume == 40);
    }

    [Fact]
    public async Task SetVolumeAsync_DelegatesToEnumerator()
    {
        var enumerator = new FakeEnumerator();
        enumerator.Snapshots.Enqueue(new[] { Raw(@"C:\spotify.exe", "Spotify", vol: 50) });

        using var sut = new WasapiSessionManager(enumerator);
        await sut.StartAsync();
        var spotifyId = sut.Sessions.First().Id;

        await sut.SetVolumeAsync(spotifyId, 25, isMuted: true);

        Assert.Single(enumerator.SetVolumeCalls);
        var call = enumerator.SetVolumeCalls[0];
        Assert.Equal(spotifyId, call.id);
        Assert.Equal((byte)25, call.volume);
        Assert.True(call.muted);
    }

    [Fact]
    public async Task StopAsync_PreventsFurtherReconcile()
    {
        var enumerator = new FakeEnumerator();
        enumerator.Snapshots.Enqueue(new[] { Raw(@"C:\spotify.exe") });
        enumerator.Snapshots.Enqueue(new[] { Raw(@"C:\spotify.exe"), Raw(@"C:\chrome.exe") });

        var sut = new WasapiSessionManager(enumerator);
        var added = new List<SessionAddedEvent>();
        sut.SessionAdded += (_, e) => added.Add(e);

        await sut.StartAsync();
        await sut.StopAsync();

        sut.ReconcileNow(); // should be a no-op after Stop

        Assert.Single(added); // only the initial reconcile fired
    }

    [Fact]
    public async Task DefaultDeviceChange_PropagatesToSnapshot()
    {
        var enumerator = new FakeEnumerator();
        enumerator.Snapshots.Enqueue(new[]
        {
            new RawAudioSession(@"\\Device\\Speakers", "Speakers", 50, false, true,
                AudioSessionKind.OutputDevice),
            new RawAudioSession(@"\\Device\\Headphones", "Headphones", 50, false, false,
                AudioSessionKind.OutputDevice),
        });
        enumerator.Snapshots.Enqueue(new[]
        {
            new RawAudioSession(@"\\Device\\Speakers", "Speakers", 50, false, false,
                AudioSessionKind.OutputDevice),
            new RawAudioSession(@"\\Device\\Headphones", "Headphones", 50, false, true,
                AudioSessionKind.OutputDevice),
        });

        using var sut = new WasapiSessionManager(enumerator);
        await sut.StartAsync();
        Assert.True(sut.Sessions.Single(s => s.DisplayName == "Speakers").IsDefault);

        sut.ReconcileNow();
        Assert.True(sut.Sessions.Single(s => s.DisplayName == "Headphones").IsDefault);
        Assert.False(sut.Sessions.Single(s => s.DisplayName == "Speakers").IsDefault);
    }

    // ---------- Test double ----------

    private sealed class FakeEnumerator : IAudioEnumerator
    {
        public Queue<IReadOnlyList<RawAudioSession>> Snapshots { get; } = new();
        public List<(byte id, byte volume, bool muted)> SetVolumeCalls { get; } = new();
        private IReadOnlyList<RawAudioSession> _last = Array.Empty<RawAudioSession>();

        public IReadOnlyList<RawAudioSession> EnumerateSessions()
        {
            if (Snapshots.Count > 0) _last = Snapshots.Dequeue();
            return _last;
        }

        public Task SetVolumeAsync(byte sessionId, byte volume, bool isMuted, CancellationToken ct = default)
        {
            SetVolumeCalls.Add((sessionId, volume, isMuted));
            return Task.CompletedTask;
        }
    }
}
