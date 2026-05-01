namespace MaxMix.Audio.Sessions;

using MaxMix.Audio.Identity;
using MaxMix.Core.Abstractions;
using MaxMix.Core.Events;

/// <summary>
/// Pure-logic core of the audio session manager.
///
/// <para>
/// <see cref="Reconcile"/> takes a snapshot of "what WASAPI says is alive right now"
/// and:
///   1. groups raw sessions by stable id (SHA1 of exe path → 7 bits),
///   2. emits <see cref="SessionAdded"/> for ids never seen before,
///   3. emits <see cref="SessionRemoved"/> for ids that disappeared,
///   4. emits <see cref="VolumeChanged"/>/<see cref="MuteChanged"/> when state on
///      a known id changes.
/// </para>
///
/// <para>
/// Both push (WASAPI events) and pull (2s reconciliation timer) call this same
/// method. Idempotent: calling Reconcile twice with the same snapshot fires
/// no duplicate events. This is the "belt and suspenders" fix for the original
/// "apps not detected after startup" bug — even if WASAPI never raises an event
/// for a new session, the next reconciliation tick still picks it up.
/// </para>
///
/// <para>
/// Not thread-safe — callers must serialize Reconcile invocations (the manager
/// runs them on a dedicated dispatcher thread).
/// </para>
/// </summary>
public sealed class SessionRegistry
{
    private readonly Dictionary<byte, AudioSessionSnapshot> _sessions = new();

    public event EventHandler<SessionAddedEvent>? SessionAdded;
    public event EventHandler<SessionRemovedEvent>? SessionRemoved;
    public event EventHandler<VolumeChangedEvent>? VolumeChanged;
    public event EventHandler<MuteChangedEvent>? MuteChanged;

    /// <summary>Current registry contents, keyed by stable id, deterministically ordered.</summary>
    public IReadOnlyList<AudioSessionSnapshot> Snapshot()
        => _sessions.Values.OrderBy(s => s.Id).ToList();

    /// <summary>
    /// Diff <paramref name="raw"/> against the current state and emit events.
    /// </summary>
    public void Reconcile(IEnumerable<RawAudioSession> raw)
    {
        var grouped = GroupByStableId(raw);
        EmitAddsAndUpdates(grouped);
        EmitRemovals(grouped);
    }

    // ---- internals ----

    /// <summary>
    /// Collapse multiple raw sessions sharing an exe path into one logical entry.
    /// First raw session wins for display name; aggregation strategy for state:
    ///   - Volume: max across the group (loudest stream represents the app)
    ///   - IsMuted: true only if ALL streams are muted (any unmuted = app audible)
    ///   - IsDefault: any group member flagged default → group is default
    /// </summary>
    private static Dictionary<byte, AudioSessionSnapshot> GroupByStableId(
        IEnumerable<RawAudioSession> raw)
    {
        var result = new Dictionary<byte, AudioSessionSnapshot>();

        foreach (var r in raw)
        {
            var id = SessionIdentity.ComputeId(r.ExecutablePath);

            if (!result.TryGetValue(id, out var existing))
            {
                result[id] = new AudioSessionSnapshot(
                    Id: id,
                    DisplayName: r.DisplayName,
                    ExecutablePath: r.ExecutablePath,
                    Volume: r.Volume,
                    IsMuted: r.IsMuted,
                    IsDefault: r.IsDefault,
                    Kind: r.Kind);
                continue;
            }

            result[id] = existing with
            {
                Volume = Math.Max(existing.Volume, r.Volume),
                IsMuted = existing.IsMuted && r.IsMuted,
                IsDefault = existing.IsDefault || r.IsDefault,
            };
        }

        return result;
    }

    private void EmitAddsAndUpdates(Dictionary<byte, AudioSessionSnapshot> grouped)
    {
        foreach (var (id, snap) in grouped)
        {
            if (!_sessions.TryGetValue(id, out var prev))
            {
                _sessions[id] = snap;
                SessionAdded?.Invoke(this, new SessionAddedEvent(snap));
                continue;
            }

            // Update stored snapshot first so handlers querying Snapshot() see fresh state.
            _sessions[id] = snap;

            if (prev.Volume != snap.Volume)
            {
                VolumeChanged?.Invoke(this, new VolumeChangedEvent(id, snap.Volume));
            }
            if (prev.IsMuted != snap.IsMuted)
            {
                MuteChanged?.Invoke(this, new MuteChangedEvent(id, snap.IsMuted));
            }
        }
    }

    private void EmitRemovals(Dictionary<byte, AudioSessionSnapshot> grouped)
    {
        var removedIds = _sessions.Keys.Where(id => !grouped.ContainsKey(id)).ToList();
        foreach (var id in removedIds)
        {
            _sessions.Remove(id);
            SessionRemoved?.Invoke(this, new SessionRemovedEvent(id));
        }
    }
}
