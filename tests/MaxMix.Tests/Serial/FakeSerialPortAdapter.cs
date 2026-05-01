namespace MaxMix.Tests.Serial;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using MaxMix.Serial;

/// <summary>
/// In-memory serial port for unit tests. The test pushes bytes the firmware
/// would emit via <see cref="EnqueueIncoming"/>; the service reads them through
/// <see cref="Read"/>. Bytes the service writes are recorded in
/// <see cref="OutgoingBytes"/> for assertions.
/// </summary>
public sealed class FakeSerialPortAdapter : ISerialPortAdapter
{
    private readonly object _gate = new();
    private readonly Queue<byte> _incoming = new();
    private readonly List<byte> _outgoing = new();
    private readonly List<string> _portsAvailable = new() { "COM5" };
    private bool _isOpen;
    private string? _portName;

    // Auto-handshake support: when armed, the fake injects a handshake response
    // (TEST + version + \n) the FIRST time the service writes a TEST byte. This
    // mirrors the real firmware contract — and survives the DiscardInBuffer the
    // service calls right before it sends TEST during handshake.
    private byte[]? _pendingHandshakeResponse;

    /// <summary>If true, <see cref="Open"/> throws — simulates a missing device.</summary>
    public bool FailOnOpen { get; set; }

    /// <summary>If non-null, <see cref="Write"/> throws this on every call (simulates I/O loss).</summary>
    public Exception? WriteException { get; set; }

    /// <summary>Snapshot of every byte the service wrote, in order.</summary>
    public IReadOnlyList<byte> OutgoingBytes
    {
        get { lock (_gate) return _outgoing.ToArray(); }
    }

    /// <summary>Number of times <see cref="DiscardInBuffer"/> was invoked.</summary>
    public int DiscardCount { get; private set; }

    /// <summary>Number of <see cref="Open"/> calls (handshake + reconnect counters).</summary>
    public int OpenCount { get; private set; }

    /// <summary>Push bytes the "firmware" should send to the service.</summary>
    public void EnqueueIncoming(params byte[] bytes)
    {
        lock (_gate)
        {
            foreach (var b in bytes) _incoming.Enqueue(b);
        }
    }

    /// <summary>
    /// Arms the fake to inject the firmware handshake response (TEST byte +
    /// version string + \n) the FIRST time the service writes a TEST byte.
    ///
    /// Why not just enqueue directly? Because the service calls
    /// <see cref="DiscardInBuffer"/> at the start of the handshake to flush
    /// stale bootloader noise. Pre-enqueued bytes would be wiped. Auto-injecting
    /// on TEST-write mirrors the real firmware (which only replies AFTER it
    /// receives the desktop's TEST byte) and is robust against discards.
    /// </summary>
    public void EnqueueHandshakeResponse(string version = "1.5.6")
    {
        var versionBytes = System.Text.Encoding.ASCII.GetBytes(version + "\n");
        var response = new byte[1 + versionBytes.Length];
        response[0] = (byte)MaxMix.Core.Models.Command.Test;
        Array.Copy(versionBytes, 0, response, 1, versionBytes.Length);
        lock (_gate)
        {
            _pendingHandshakeResponse = response;
        }
    }

    /// <summary>Configures which port names the discovery scan will find.</summary>
    public void SetAvailablePorts(params string[] ports)
    {
        lock (_gate)
        {
            _portsAvailable.Clear();
            _portsAvailable.AddRange(ports);
        }
    }

    public bool IsOpen { get { lock (_gate) return _isOpen; } }
    public string? PortName { get { lock (_gate) return _portName; } }
    public int BytesToRead { get { lock (_gate) return _incoming.Count; } }

    public void Open(string portName)
    {
        if (FailOnOpen) throw new System.IO.IOException("simulated open failure");
        lock (_gate)
        {
            _isOpen = true;
            _portName = portName;
            OpenCount++;
        }
    }

    public void Close()
    {
        lock (_gate) { _isOpen = false; _portName = null; }
    }

    public int Read(byte[] buffer, int offset, int count)
    {
        // Block briefly waiting for data, then time out by returning 0.
        // The service's read loop will retry — that matches the real SerialPort
        // ReadTimeout behavior (throws TimeoutException, but 0 is sufficient
        // for the service contract since we treat 0 as "no data right now").
        var deadline = DateTime.UtcNow.AddMilliseconds(50);
        while (DateTime.UtcNow < deadline)
        {
            lock (_gate)
            {
                if (!_isOpen) return 0;
                if (_incoming.Count > 0)
                {
                    int n = Math.Min(count, _incoming.Count);
                    for (int i = 0; i < n; i++) buffer[offset + i] = _incoming.Dequeue();
                    return n;
                }
            }
            Thread.Sleep(2);
        }
        return 0;
    }

    public void Write(byte[] buffer, int offset, int count)
    {
        if (WriteException is { } ex) throw ex;
        lock (_gate)
        {
            if (!_isOpen) throw new InvalidOperationException("port not open");
            for (int i = 0; i < count; i++) _outgoing.Add(buffer[offset + i]);

            // Auto-handshake: if a TEST byte was written and a handshake
            // response is armed, inject it now so the service's next Read()
            // picks it up. Once consumed, the arming clears so subsequent
            // reconnect attempts within a single test won't auto-respond
            // unless re-armed.
            if (_pendingHandshakeResponse != null)
            {
                for (int i = 0; i < count; i++)
                {
                    if (buffer[offset + i] == (byte)MaxMix.Core.Models.Command.Test)
                    {
                        foreach (var rb in _pendingHandshakeResponse) _incoming.Enqueue(rb);
                        _pendingHandshakeResponse = null;
                        break;
                    }
                }
            }
        }
    }

    public void DiscardInBuffer()
    {
        lock (_gate) { _incoming.Clear(); DiscardCount++; }
    }

    public IReadOnlyList<string> GetAvailablePortNames()
    {
        lock (_gate) return _portsAvailable.ToArray();
    }

    public void Dispose() => Close();
}
