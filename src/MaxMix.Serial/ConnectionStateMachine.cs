namespace MaxMix.Serial;

using System;
using System.Collections.Generic;
using MaxMix.Core.Models;

/// <summary>
/// Thread-safe state machine for the serial transport lifecycle.
///
/// Allowed transitions (anything not listed is rejected):
///   Disconnected → Scanning, Connecting, Disconnected
///   Scanning     → Connecting, Error, Disconnected
///   Connecting   → Handshaking, Error, Disconnected
///   Handshaking  → Ready, Error, Disconnected
///   Ready        → Error, Disconnected
///   Error        → Backoff, Disconnected
///   Backoff      → Scanning, Connecting, Disconnected
///
/// Backoff schedule (per "Reconnect with Exponential Backoff" requirement):
///   500ms, 1s, 2s, 4s, 8s, 16s, then capped at 30s.
///   Resets to 500ms once <see cref="ConnectionState.Ready"/> is reached.
///
/// All public members are safe to call from multiple threads. Transitions are
/// guarded by a single lock; the <see cref="StateChanged"/> event is invoked
/// after the lock is released to keep handlers from re-entering the machine.
/// </summary>
public sealed class ConnectionStateMachine
{
    private static readonly TimeSpan InitialBackoff = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan MaxBackoff     = TimeSpan.FromSeconds(30);

    private static readonly IReadOnlyDictionary<ConnectionState, HashSet<ConnectionState>> Allowed =
        new Dictionary<ConnectionState, HashSet<ConnectionState>>
        {
            [ConnectionState.Disconnected] = new() { ConnectionState.Scanning,    ConnectionState.Connecting },
            [ConnectionState.Scanning]     = new() { ConnectionState.Connecting,  ConnectionState.Error },
            [ConnectionState.Connecting]   = new() { ConnectionState.Handshaking, ConnectionState.Error },
            [ConnectionState.Handshaking]  = new() { ConnectionState.Ready,       ConnectionState.Error },
            [ConnectionState.Ready]        = new() { ConnectionState.Error },
            [ConnectionState.Error]        = new() { ConnectionState.Backoff },
            [ConnectionState.Backoff]      = new() { ConnectionState.Scanning,    ConnectionState.Connecting },
        };

    private readonly object _gate = new();
    private ConnectionState _state = ConnectionState.Disconnected;
    private TimeSpan _nextBackoff = InitialBackoff;

    public ConnectionState State
    {
        get { lock (_gate) { return _state; } }
    }

    /// <summary>Fires AFTER the transition is committed (lock released).</summary>
    public event EventHandler<StateChangedEventArgs>? StateChanged;

    /// <summary>
    /// Attempts to transition to <paramref name="next"/>.
    /// Throws <see cref="InvalidOperationException"/> if the transition is not allowed.
    /// Disconnect is always allowed as an explicit shutdown path.
    /// </summary>
    public void TransitionTo(ConnectionState next)
    {
        ConnectionState previous;
        lock (_gate)
        {
            if (_state == next)
            {
                return; // idempotent — no-op, no event
            }

            // Disconnected is the universal exit — always allowed.
            bool isShutdown = next == ConnectionState.Disconnected;
            if (!isShutdown && !Allowed[_state].Contains(next))
            {
                throw new InvalidOperationException(
                    $"Invalid state transition: {_state} → {next}.");
            }

            previous = _state;
            _state = next;

            if (next == ConnectionState.Ready)
            {
                _nextBackoff = InitialBackoff;
            }
        }

        StateChanged?.Invoke(this, new StateChangedEventArgs(previous, next));
    }

    /// <summary>
    /// Returns the next backoff delay and advances the schedule. Caps at 30s.
    /// Does NOT change <see cref="State"/> — the caller decides when to wait
    /// and when to transition through Backoff.
    /// </summary>
    public TimeSpan NextBackoffDelay()
    {
        lock (_gate)
        {
            var current = _nextBackoff;
            // Pre-compute the next value, capped.
            var doubled = TimeSpan.FromMilliseconds(current.TotalMilliseconds * 2);
            _nextBackoff = doubled > MaxBackoff ? MaxBackoff : doubled;
            return current;
        }
    }
}
