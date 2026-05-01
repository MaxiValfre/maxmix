namespace MaxMix.Core.Models;

/// <summary>
/// Lifecycle states of the serial transport connection.
/// </summary>
public enum ConnectionState
{
    /// <summary>No connection attempt in progress, no port owned.</summary>
    Disconnected,
    /// <summary>Scanning available COM ports for the device.</summary>
    Scanning,
    /// <summary>A port has been selected; opening it.</summary>
    Connecting,
    /// <summary>Port open; exchanging TEST/OK with firmware.</summary>
    Handshaking,
    /// <summary>Handshake complete; bidirectional traffic flowing.</summary>
    Ready,
    /// <summary>I/O failure or protocol desync detected; closing.</summary>
    Error,
    /// <summary>Waiting for the exponential backoff delay before retrying.</summary>
    Backoff
}
