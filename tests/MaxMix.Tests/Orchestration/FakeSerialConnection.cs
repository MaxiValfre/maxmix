namespace MaxMix.Tests.Orchestration;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MaxMix.Core.Abstractions;
using MaxMix.Core.Models;

/// <summary>
/// Test double for <see cref="ISerialConnection"/>. Captures every byte the
/// orchestrator sends, and exposes a hook to fire FrameReceived as if firmware
/// had pushed a knob/button event.
///
/// <para>Thread-safety: writes via <see cref="SendAsync"/>, reads via
/// <see cref="FramesWithCommand"/>, <see cref="SentFrames"/> snapshot, and
/// <see cref="Clear"/> all serialize through <see cref="_framesGate"/>. The
/// concurrency stress tests fire SettingsChanged from multiple threads in
/// parallel with serial state flapping, so an unsynchronized
/// <see cref="List{T}"/> would throw <see cref="NullReferenceException"/>
/// during enumeration. Mirrors the queue semantics of the real
/// <c>SerialDeviceService</c>.</para>
/// </summary>
internal sealed class FakeSerialConnection : ISerialConnection
{
    private readonly object _framesGate = new();
    private readonly List<byte[]> _sentFrames = new();

    /// <summary>
    /// Snapshot of all frames sent so far. Returned as an immutable copy so
    /// callers can safely iterate without taking <see cref="_framesGate"/>.
    /// </summary>
    public IReadOnlyList<byte[]> SentFrames
    {
        get { lock (_framesGate) return _sentFrames.ToArray(); }
    }

    private ConnectionState _state = ConnectionState.Ready;

    public ConnectionState State
    {
        get => _state;
        set
        {
            var old = _state;
            _state = value;
            if (old != value) StateChanged?.Invoke(this, value);
        }
    }

    public bool IsConnected => _state == ConnectionState.Ready;
    public string? PortName { get; set; } = "COMTEST";

    public event EventHandler<ConnectionState>? StateChanged;
    public event EventHandler<ReadOnlyMemory<byte>>? FrameReceived;

    public Task StartAsync(string? portName = null, CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    public ValueTask SendAsync(ReadOnlyMemory<byte> frame, CancellationToken cancellationToken = default)
    {
        var copy = frame.ToArray();
        lock (_framesGate) _sentFrames.Add(copy);
        return ValueTask.CompletedTask;
    }

    /// <summary>Raises FrameReceived synchronously so tests can drive firmware → desktop input.</summary>
    public void PushFrame(byte[] frame) => FrameReceived?.Invoke(this, frame.AsMemory());

    public IEnumerable<byte[]> FramesWithCommand(Command command)
    {
        // Snapshot under the lock, then iterate the copy lock-free so a
        // mid-enumeration SendAsync from another thread can't tear the
        // underlying List.
        byte[][] snapshot;
        lock (_framesGate) snapshot = _sentFrames.ToArray();
        foreach (var f in snapshot)
        {
            if (f.Length > 0 && f[0] == (byte)command) yield return f;
        }
    }

    public void Clear()
    {
        lock (_framesGate) _sentFrames.Clear();
    }
}
