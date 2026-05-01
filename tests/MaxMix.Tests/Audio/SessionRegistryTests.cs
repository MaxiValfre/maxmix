namespace MaxMix.Tests.Audio;

using MaxMix.Audio.Sessions;
using MaxMix.Core.Abstractions;
using MaxMix.Core.Events;

/// <summary>
/// Tests for <see cref="SessionRegistry"/> — the pure-logic core of the audio
/// session manager. Takes snapshots of "what WASAPI says is alive right now"
/// and produces add/remove/volume/mute events while keeping a stable id model.
///
/// This layer is what powers both event-driven detection (push) and the 2s
/// reconciliation timer (pull). Both call <see cref="SessionRegistry.Reconcile"/>
/// with the latest snapshot.
/// </summary>
public class SessionRegistryTests
{
    private static RawAudioSession Raw(
        string exePath, string display = "App", byte volume = 50, bool muted = false,
        bool isDefault = false, AudioSessionKind kind = AudioSessionKind.Application)
        => new(exePath, display, volume, muted, isDefault, kind);

    // ---------- Add detection ----------

    [Fact]
    public void Reconcile_FirstSnapshotWithOneSession_FiresOneAddEvent()
    {
        var registry = new SessionRegistry();
        var added = new List<SessionAddedEvent>();
        registry.SessionAdded += (_, e) => added.Add(e);

        registry.Reconcile(new[] { Raw(@"C:\Spotify.exe", "Spotify", volume: 60) });

        Assert.Single(added);
        Assert.Equal("Spotify", added[0].Session.DisplayName);
        Assert.Equal((byte)60, added[0].Session.Volume);
    }

    [Fact]
    public void Reconcile_SameSessionInTwoSnapshots_DoesNotFireDuplicateAdd()
    {
        var registry = new SessionRegistry();
        var added = new List<SessionAddedEvent>();
        registry.SessionAdded += (_, e) => added.Add(e);

        var snapshot = new[] { Raw(@"C:\Spotify.exe") };
        registry.Reconcile(snapshot);
        registry.Reconcile(snapshot);

        Assert.Single(added); // only first snapshot triggers add
    }

    [Fact]
    public void Reconcile_NewSessionAppearsInLaterSnapshot_FiresAdd()
    {
        // This is the KEY reconciliation behavior. Spec section 2:
        // "Periodic reconciliation catches missed event."
        var registry = new SessionRegistry();
        var added = new List<SessionAddedEvent>();
        registry.SessionAdded += (_, e) => added.Add(e);

        registry.Reconcile(new[] { Raw(@"C:\Spotify.exe", "Spotify") });
        registry.Reconcile(new[]
        {
            Raw(@"C:\Spotify.exe", "Spotify"),
            Raw(@"C:\chrome.exe", "Chrome"),
        });

        Assert.Equal(2, added.Count);
        Assert.Equal("Spotify", added[0].Session.DisplayName);
        Assert.Equal("Chrome", added[1].Session.DisplayName);
    }

    // ---------- Remove detection ----------

    [Fact]
    public void Reconcile_SessionDisappearsFromSnapshot_FiresRemove()
    {
        var registry = new SessionRegistry();
        var removed = new List<SessionRemovedEvent>();
        registry.SessionRemoved += (_, e) => removed.Add(e);

        registry.Reconcile(new[] { Raw(@"C:\Spotify.exe", "Spotify") });
        var spotifyId = registry.Snapshot()[0].Id;

        registry.Reconcile(Array.Empty<RawAudioSession>());

        Assert.Single(removed);
        Assert.Equal(spotifyId, removed[0].SessionId);
    }

    [Fact]
    public void Reconcile_RemovedSession_DisappearsFromSnapshotProperty()
    {
        var registry = new SessionRegistry();
        registry.Reconcile(new[] { Raw(@"C:\A.exe", "A"), Raw(@"C:\B.exe", "B") });
        Assert.Equal(2, registry.Snapshot().Count);

        registry.Reconcile(new[] { Raw(@"C:\A.exe", "A") });

        var current = registry.Snapshot();
        Assert.Single(current);
        Assert.Equal("A", current[0].DisplayName);
    }

    // ---------- Grouping (multiple WASAPI sessions per app) ----------

    [Fact]
    public void Reconcile_TwoSessionsFromSameExe_GroupedIntoSingleEntry()
    {
        // Spec: "Two Chrome tabs produce one session."
        var registry = new SessionRegistry();
        var added = new List<SessionAddedEvent>();
        registry.SessionAdded += (_, e) => added.Add(e);

        registry.Reconcile(new[]
        {
            Raw(@"C:\Program Files\Chrome\chrome.exe", "Chrome - Tab 1", volume: 70),
            Raw(@"C:\Program Files\Chrome\chrome.exe", "Chrome - Tab 2", volume: 80),
        });

        Assert.Single(added);
        Assert.Single(registry.Snapshot());
    }

    [Fact]
    public void Reconcile_GroupedSession_StableIdComesFromExePath()
    {
        var registry = new SessionRegistry();
        registry.Reconcile(new[]
        {
            Raw(@"C:\Program Files\Chrome\chrome.exe", "Chrome A"),
            Raw(@"C:\Program Files\Chrome\chrome.exe", "Chrome B"),
        });

        var entry = registry.Snapshot().Single();
        var expected = MaxMix.Audio.Identity.SessionIdentity.ComputeId(
            @"C:\Program Files\Chrome\chrome.exe");
        Assert.Equal(expected, entry.Id);
    }

    // ---------- Volume / mute change detection ----------

    [Fact]
    public void Reconcile_VolumeChanged_FiresVolumeChangedEvent()
    {
        var registry = new SessionRegistry();
        var changes = new List<VolumeChangedEvent>();
        registry.VolumeChanged += (_, e) => changes.Add(e);

        registry.Reconcile(new[] { Raw(@"C:\Spotify.exe", volume: 50) });
        registry.Reconcile(new[] { Raw(@"C:\Spotify.exe", volume: 75) });

        Assert.Single(changes);
        Assert.Equal((byte)75, changes[0].Volume);
    }

    [Fact]
    public void Reconcile_VolumeUnchanged_DoesNotFireEvent()
    {
        var registry = new SessionRegistry();
        var changes = new List<VolumeChangedEvent>();
        registry.VolumeChanged += (_, e) => changes.Add(e);

        registry.Reconcile(new[] { Raw(@"C:\Spotify.exe", volume: 50) });
        registry.Reconcile(new[] { Raw(@"C:\Spotify.exe", volume: 50) });

        Assert.Empty(changes);
    }

    [Fact]
    public void Reconcile_MuteChanged_FiresMuteChangedEvent()
    {
        var registry = new SessionRegistry();
        var changes = new List<MuteChangedEvent>();
        registry.MuteChanged += (_, e) => changes.Add(e);

        registry.Reconcile(new[] { Raw(@"C:\Spotify.exe", muted: false) });
        registry.Reconcile(new[] { Raw(@"C:\Spotify.exe", muted: true) });

        Assert.Single(changes);
        Assert.True(changes[0].IsMuted);
    }

    [Fact]
    public void Reconcile_MuteUnchanged_DoesNotFireEvent()
    {
        var registry = new SessionRegistry();
        var changes = new List<MuteChangedEvent>();
        registry.MuteChanged += (_, e) => changes.Add(e);

        registry.Reconcile(new[] { Raw(@"C:\Spotify.exe", muted: true) });
        registry.Reconcile(new[] { Raw(@"C:\Spotify.exe", muted: true) });

        Assert.Empty(changes);
    }

    // ---------- Default device tracking ----------

    [Fact]
    public void Reconcile_DefaultDeviceFlagChange_PropagatesToSnapshot()
    {
        // Spec: "User switches default output device" → snapshot updates.
        var registry = new SessionRegistry();
        registry.Reconcile(new[]
        {
            Raw(@"\\Device\\Speakers", "Speakers", isDefault: true,
                kind: AudioSessionKind.OutputDevice),
            Raw(@"\\Device\\Headphones", "Headphones", isDefault: false,
                kind: AudioSessionKind.OutputDevice),
        });

        registry.Reconcile(new[]
        {
            Raw(@"\\Device\\Speakers", "Speakers", isDefault: false,
                kind: AudioSessionKind.OutputDevice),
            Raw(@"\\Device\\Headphones", "Headphones", isDefault: true,
                kind: AudioSessionKind.OutputDevice),
        });

        var snapshot = registry.Snapshot();
        Assert.False(snapshot.Single(s => s.DisplayName == "Speakers").IsDefault);
        Assert.True(snapshot.Single(s => s.DisplayName == "Headphones").IsDefault);
    }

    // ---------- Stable id across remove/add ----------

    [Fact]
    public void Reconcile_AppRestartsWithSameExePath_KeepsSameId()
    {
        // Spec: "Same app produces consistent ID across restarts."
        var registry = new SessionRegistry();
        registry.Reconcile(new[] { Raw(@"C:\Spotify.exe", "Spotify") });
        var idBefore = registry.Snapshot().Single().Id;

        registry.Reconcile(Array.Empty<RawAudioSession>());      // app closes
        registry.Reconcile(new[] { Raw(@"C:\Spotify.exe", "Spotify") }); // restarts

        var idAfter = registry.Snapshot().Single().Id;
        Assert.Equal(idBefore, idAfter);
    }
}
