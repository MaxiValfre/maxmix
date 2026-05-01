namespace MaxMix.Core.Abstractions;

using MaxMix.Core.Models;

/// <summary>
/// Transport-layer contract for the serial connection to the MaxMix firmware.
/// Owns the COM port, the connection state machine, the heartbeat, and the
/// outbound ACK queue. Consumers see frames; framing is the implementation's
/// concern.
/// </summary>
public interface ISerialConnection
{
    ConnectionState State { get; }
    bool IsConnected { get; }
    string? PortName { get; }

    event EventHandler<ConnectionState>? StateChanged;
    event EventHandler<ReadOnlyMemory<byte>>? FrameReceived;

    Task StartAsync(string? portName = null, CancellationToken cancellationToken = default);
    Task StopAsync(CancellationToken cancellationToken = default);
    ValueTask SendAsync(ReadOnlyMemory<byte> frame, CancellationToken cancellationToken = default);
}
