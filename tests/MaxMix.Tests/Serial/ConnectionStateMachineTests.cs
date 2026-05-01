namespace MaxMix.Tests.Serial;

using System;
using System.Collections.Generic;
using MaxMix.Core.Models;
using MaxMix.Serial;
using Xunit;

/// <summary>
/// Unit tests for <see cref="ConnectionStateMachine"/>. Drives transitions and
/// asserts both the resulting state and the exponential backoff schedule.
///
/// Spec references:
///   - "Connection State Machine" requirement (Disconnected→Connecting→Handshake→Ready)
///   - "Reconnect with Exponential Backoff" requirement (500ms → 1s → 2s … 30s cap)
///   - "Heartbeat Monitoring" requirement (timeout → Error)
///   - Brief expansion: states include Scanning and Backoff explicitly.
/// </summary>
public class ConnectionStateMachineTests
{
    // ---------- Initial state ----------

    [Fact]
    public void New_machine_starts_in_Disconnected()
    {
        var sm = new ConnectionStateMachine();
        Assert.Equal(ConnectionState.Disconnected, sm.State);
    }

    // ---------- Happy path transitions ----------

    [Fact]
    public void Disconnected_to_Scanning_to_Connecting_to_Handshaking_to_Ready_is_valid()
    {
        var sm = new ConnectionStateMachine();

        sm.TransitionTo(ConnectionState.Scanning);
        Assert.Equal(ConnectionState.Scanning, sm.State);

        sm.TransitionTo(ConnectionState.Connecting);
        Assert.Equal(ConnectionState.Connecting, sm.State);

        sm.TransitionTo(ConnectionState.Handshaking);
        Assert.Equal(ConnectionState.Handshaking, sm.State);

        sm.TransitionTo(ConnectionState.Ready);
        Assert.Equal(ConnectionState.Ready, sm.State);
    }

    [Fact]
    public void Disconnected_can_skip_Scanning_and_go_directly_to_Connecting_with_known_port()
    {
        var sm = new ConnectionStateMachine();
        // When the caller already knows the port (e.g., last-known good), skip Scanning.
        sm.TransitionTo(ConnectionState.Connecting);
        Assert.Equal(ConnectionState.Connecting, sm.State);
    }

    // ---------- Failure transitions ----------

    [Fact]
    public void Any_active_state_can_transition_to_Error()
    {
        foreach (var from in new[] {
            ConnectionState.Scanning,
            ConnectionState.Connecting,
            ConnectionState.Handshaking,
            ConnectionState.Ready
        })
        {
            var sm = new ConnectionStateMachine();
            DriveTo(sm, from);
            sm.TransitionTo(ConnectionState.Error);
            Assert.Equal(ConnectionState.Error, sm.State);
        }
    }

    [Fact]
    public void Error_transitions_to_Backoff_to_schedule_retry()
    {
        var sm = new ConnectionStateMachine();
        DriveTo(sm, ConnectionState.Error);

        sm.TransitionTo(ConnectionState.Backoff);
        Assert.Equal(ConnectionState.Backoff, sm.State);
    }

    [Fact]
    public void Backoff_transitions_to_Scanning_after_delay()
    {
        var sm = new ConnectionStateMachine();
        DriveTo(sm, ConnectionState.Backoff);

        sm.TransitionTo(ConnectionState.Scanning);
        Assert.Equal(ConnectionState.Scanning, sm.State);
    }

    // ---------- Invalid transitions ----------

    [Fact]
    public void Disconnected_to_Ready_is_rejected()
    {
        var sm = new ConnectionStateMachine();
        Assert.Throws<InvalidOperationException>(() => sm.TransitionTo(ConnectionState.Ready));
    }

    [Fact]
    public void Disconnected_to_Handshaking_is_rejected()
    {
        var sm = new ConnectionStateMachine();
        Assert.Throws<InvalidOperationException>(() => sm.TransitionTo(ConnectionState.Handshaking));
    }

    [Fact]
    public void Ready_to_Scanning_is_rejected_must_go_through_Error_first()
    {
        var sm = new ConnectionStateMachine();
        DriveTo(sm, ConnectionState.Ready);

        Assert.Throws<InvalidOperationException>(() => sm.TransitionTo(ConnectionState.Scanning));
    }

    [Fact]
    public void Connecting_to_Ready_is_rejected_handshake_must_run_first()
    {
        var sm = new ConnectionStateMachine();
        DriveTo(sm, ConnectionState.Connecting);

        Assert.Throws<InvalidOperationException>(() => sm.TransitionTo(ConnectionState.Ready));
    }

    // ---------- Disconnect from any state ----------

    [Fact]
    public void Any_state_can_transition_to_Disconnected_for_explicit_shutdown()
    {
        foreach (var from in new[] {
            ConnectionState.Scanning,
            ConnectionState.Connecting,
            ConnectionState.Handshaking,
            ConnectionState.Ready,
            ConnectionState.Error,
            ConnectionState.Backoff
        })
        {
            var sm = new ConnectionStateMachine();
            DriveTo(sm, from);
            sm.TransitionTo(ConnectionState.Disconnected);
            Assert.Equal(ConnectionState.Disconnected, sm.State);
        }
    }

    // ---------- StateChanged event ----------

    [Fact]
    public void StateChanged_event_fires_with_old_and_new_state()
    {
        var sm = new ConnectionStateMachine();
        var changes = new List<(ConnectionState From, ConnectionState To)>();
        sm.StateChanged += (_, e) => changes.Add((e.OldState, e.NewState));

        sm.TransitionTo(ConnectionState.Scanning);
        sm.TransitionTo(ConnectionState.Connecting);

        Assert.Equal(2, changes.Count);
        Assert.Equal((ConnectionState.Disconnected, ConnectionState.Scanning), changes[0]);
        Assert.Equal((ConnectionState.Scanning,     ConnectionState.Connecting), changes[1]);
    }

    [Fact]
    public void StateChanged_event_does_not_fire_when_transition_is_rejected()
    {
        var sm = new ConnectionStateMachine();
        int fired = 0;
        sm.StateChanged += (_, _) => fired++;

        try { sm.TransitionTo(ConnectionState.Ready); } catch (InvalidOperationException) { }

        Assert.Equal(0, fired);
        Assert.Equal(ConnectionState.Disconnected, sm.State);
    }

    // ---------- Exponential backoff ----------

    [Fact]
    public void First_backoff_delay_is_500ms()
    {
        var sm = new ConnectionStateMachine();
        Assert.Equal(TimeSpan.FromMilliseconds(500), sm.NextBackoffDelay());
    }

    [Theory]
    [InlineData(1, 500)]      // 1st retry: 500ms
    [InlineData(2, 1000)]     // 2nd: 1s
    [InlineData(3, 2000)]     // 3rd: 2s
    [InlineData(4, 4000)]     // 4th: 4s
    [InlineData(5, 8000)]     // 5th: 8s
    [InlineData(6, 16000)]    // 6th: 16s
    [InlineData(7, 30000)]    // 7th: capped at 30s
    [InlineData(8, 30000)]    // stays at cap
    [InlineData(20, 30000)]   // stays at cap forever
    public void Backoff_doubles_each_call_and_caps_at_30s(int callIndex, int expectedMs)
    {
        var sm = new ConnectionStateMachine();
        TimeSpan last = TimeSpan.Zero;
        for (int i = 1; i <= callIndex; i++)
        {
            last = sm.NextBackoffDelay();
        }
        Assert.Equal(TimeSpan.FromMilliseconds(expectedMs), last);
    }

    [Fact]
    public void Backoff_resets_when_state_reaches_Ready()
    {
        var sm = new ConnectionStateMachine();
        sm.NextBackoffDelay(); // 500
        sm.NextBackoffDelay(); // 1000
        sm.NextBackoffDelay(); // 2000

        DriveTo(sm, ConnectionState.Ready); // success — reset
        // Force a fresh failure cycle
        sm.TransitionTo(ConnectionState.Error);

        Assert.Equal(TimeSpan.FromMilliseconds(500), sm.NextBackoffDelay());
    }

    // ---------- Thread safety ----------

    [Fact]
    public async System.Threading.Tasks.Task Concurrent_transitions_do_not_corrupt_state()
    {
        var sm = new ConnectionStateMachine();
        sm.TransitionTo(ConnectionState.Scanning);

        // Two threads racing — at most one should win, the other should throw or retry.
        var t1 = System.Threading.Tasks.Task.Run(() =>
        {
            try { sm.TransitionTo(ConnectionState.Connecting); }
            catch (InvalidOperationException) { /* lost the race */ }
        });
        var t2 = System.Threading.Tasks.Task.Run(() =>
        {
            try { sm.TransitionTo(ConnectionState.Disconnected); }
            catch (InvalidOperationException) { /* lost the race */ }
        });

        await System.Threading.Tasks.Task.WhenAll(t1, t2);

        // Final state must be one of the two valid targets, never some hybrid.
        Assert.Contains(sm.State, new[] { ConnectionState.Connecting, ConnectionState.Disconnected });
    }

    // ---------- Helpers ----------

    private static void DriveTo(ConnectionStateMachine sm, ConnectionState target)
    {
        // Walk a known valid path to the target state.
        switch (target)
        {
            case ConnectionState.Disconnected: return;
            case ConnectionState.Scanning:
                sm.TransitionTo(ConnectionState.Scanning); return;
            case ConnectionState.Connecting:
                sm.TransitionTo(ConnectionState.Scanning);
                sm.TransitionTo(ConnectionState.Connecting); return;
            case ConnectionState.Handshaking:
                sm.TransitionTo(ConnectionState.Scanning);
                sm.TransitionTo(ConnectionState.Connecting);
                sm.TransitionTo(ConnectionState.Handshaking); return;
            case ConnectionState.Ready:
                sm.TransitionTo(ConnectionState.Scanning);
                sm.TransitionTo(ConnectionState.Connecting);
                sm.TransitionTo(ConnectionState.Handshaking);
                sm.TransitionTo(ConnectionState.Ready); return;
            case ConnectionState.Error:
                sm.TransitionTo(ConnectionState.Scanning);
                sm.TransitionTo(ConnectionState.Error); return;
            case ConnectionState.Backoff:
                sm.TransitionTo(ConnectionState.Scanning);
                sm.TransitionTo(ConnectionState.Error);
                sm.TransitionTo(ConnectionState.Backoff); return;
        }
    }
}
