namespace MaxMix.Serial;

using System;
using System.Collections.Generic;
using System.Threading;

/// <summary>
/// Minimal abstraction over <see cref="System.IO.Ports.SerialPort"/> so the
/// transport service can be unit-tested without a real COM port.
///
/// Production: <see cref="SystemSerialPortAdapter"/> wraps the real port.
/// Tests:      a fake adapter pushes pre-canned bytes and records writes.
///
/// All methods are synchronous and called from a dedicated I/O thread —
/// implementations do NOT need to be thread-safe across multiple threads,
/// only across the single reader/writer used by the service.
/// </summary>
public interface ISerialPortAdapter : IDisposable
{
    bool IsOpen { get; }
    string? PortName { get; }
    int BytesToRead { get; }

    /// <summary>Opens <paramref name="portName"/> at 76800 baud, 8N1. Throws on failure.</summary>
    void Open(string portName);

    /// <summary>Closes the port if open. Idempotent.</summary>
    void Close();

    /// <summary>
    /// Reads up to <paramref name="count"/> bytes into <paramref name="buffer"/>.
    /// Returns the number of bytes read (0 if the port is closed).
    /// Honors <see cref="System.IO.Ports.SerialPort.ReadTimeout"/> on the underlying port.
    /// </summary>
    int Read(byte[] buffer, int offset, int count);

    /// <summary>Writes <paramref name="count"/> bytes. Blocks until written or throws.</summary>
    void Write(byte[] buffer, int offset, int count);

    /// <summary>Discards any pending bytes in the read buffer.</summary>
    void DiscardInBuffer();

    /// <summary>Lists candidate COM port names (e.g., from <see cref="System.IO.Ports.SerialPort.GetPortNames"/>).</summary>
    IReadOnlyList<string> GetAvailablePortNames();
}
