namespace MaxMix.Audio;

using MaxMix.Audio.Sessions;
using MaxMix.Audio.Wasapi;
using MaxMix.Core.Abstractions;
using MaxMix.Core.Events;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

/// <summary>
/// <see cref="IAudioSessionManager"/> implementation that combines:
///   - an <see cref="IAudioEnumerator"/> (the WASAPI surface, mockable),
///   - a <see cref="SessionRegistry"/> (pure diff/event logic),
///   - a 2-second reconciliation timer (catches sessions WASAPI didn't event for).
///
/// <para>
/// The reconciliation timer is the key fix for the original "apps not detected
/// after startup" bug. Even if <c>IAudioSessionNotification</c> never fires for
/// a new session, the next tick walks the live snapshot and the registry diffs
/// it against current state.
/// </para>
///
/// <para>
/// All event forwarding is just a re-raise from the registry; the manager owns
/// the timer lifecycle and the public API.
/// </para>
/// </summary>
public sealed class WasapiSessionManager : IAudioSessionManager, IDisposable
{
    /// <summary>Reconciliation interval. Spec section 2 quotes "every 2 seconds".</summary>
    public static readonly TimeSpan ReconcileInterval = TimeSpan.FromSeconds(2);

    private readonly IAudioEnumerator _enumerator;
    private readonly SessionRegistry _registry = new();
    private readonly object _gate = new();
    private readonly ILogger<WasapiSessionManager> _log;

    private Timer? _timer;
    private bool _started;
    private bool _disposed;

    public WasapiSessionManager(
        IAudioEnumerator enumerator,
        ILogger<WasapiSessionManager>? logger = null)
    {
        _enumerator = enumerator ?? throw new ArgumentNullException(nameof(enumerator));
        _log = logger ?? NullLogger<WasapiSessionManager>.Instance;

        // Forward registry events as-is, with audit-level logging at each edge.
        _registry.SessionAdded += (s, e) =>
        {
            _log.LogInformation(
                "Audio session added: {DisplayName} (id={Id}, kind={Kind}).",
                e.Session.DisplayName, e.Session.Id, e.Session.Kind);
            SessionAdded?.Invoke(this, e);
        };
        _registry.SessionRemoved += (s, e) =>
        {
            _log.LogInformation("Audio session removed: id={Id}.", e.SessionId);
            SessionRemoved?.Invoke(this, e);
        };
        _registry.VolumeChanged += (s, e) =>
        {
            _log.LogDebug("Audio volume changed: id={Id}, volume={Volume}.", e.SessionId, e.Volume);
            VolumeChanged?.Invoke(this, e);
        };
        _registry.MuteChanged += (s, e) =>
        {
            _log.LogDebug("Audio mute changed: id={Id}, muted={IsMuted}.", e.SessionId, e.IsMuted);
            MuteChanged?.Invoke(this, e);
        };
    }

    // ---- IAudioSessionManager ----

    public IReadOnlyCollection<AudioSessionSnapshot> Sessions => _registry.Snapshot();

    public event EventHandler<SessionAddedEvent>? SessionAdded;
    public event EventHandler<SessionRemovedEvent>? SessionRemoved;
    public event EventHandler<VolumeChangedEvent>? VolumeChanged;
    public event EventHandler<MuteChangedEvent>? MuteChanged;

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        lock (_gate)
        {
            if (_started) return Task.CompletedTask;
            _started = true;

            _log.LogInformation(
                "Audio session manager starting; reconciliation every {Interval}.",
                ReconcileInterval);

            // Initial reconciliation: surface anything already running.
            ReconcileLocked();

            // Periodic reconciliation: catches missed events.
            _timer = new Timer(_ => SafeReconcileNow(), null, ReconcileInterval, ReconcileInterval);
        }
        return Task.CompletedTask;
    }

    /// <summary>
    /// Wrapper around <see cref="ReconcileNow"/> that swallows COM/WASAPI
    /// exceptions so a transient device error doesn't take the timer down.
    /// </summary>
    private void SafeReconcileNow()
    {
        try { ReconcileNow(); }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Reconciliation pass threw; will retry on next tick.");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            _started = false;
            _timer?.Dispose();
            _timer = null;
        }
        return Task.CompletedTask;
    }

    public Task SetVolumeAsync(byte sessionId, byte volume, bool isMuted, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return _enumerator.SetVolumeAsync(sessionId, volume, isMuted, cancellationToken);
    }

    // ---- Public for tests + WASAPI callbacks ----

    /// <summary>
    /// Performs a reconciliation pass right now. Called by the timer, and also
    /// by the real WASAPI notification callbacks so that pushed events go
    /// through the same diff path as pulled snapshots.
    /// </summary>
    public void ReconcileNow()
    {
        lock (_gate)
        {
            if (!_started) return;
            ReconcileLocked();
        }
    }

    private void ReconcileLocked()
    {
        var snapshot = _enumerator.EnumerateSessions();
        _registry.Reconcile(snapshot);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _timer?.Dispose();
        _timer = null;
    }

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(WasapiSessionManager));
    }
}
