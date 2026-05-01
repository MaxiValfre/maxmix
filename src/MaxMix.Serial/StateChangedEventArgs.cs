namespace MaxMix.Serial;

using System;
using MaxMix.Core.Models;

/// <summary>
/// Raised by <see cref="ConnectionStateMachine"/> after a successful transition.
/// Carries both the previous and the new state so subscribers can react to
/// specific edges (e.g., Ready→Error means "connection just dropped").
/// </summary>
public sealed class StateChangedEventArgs : EventArgs
{
    public ConnectionState OldState { get; }
    public ConnectionState NewState { get; }

    public StateChangedEventArgs(ConnectionState oldState, ConnectionState newState)
    {
        OldState = oldState;
        NewState = newState;
    }
}
