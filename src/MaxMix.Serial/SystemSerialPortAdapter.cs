namespace MaxMix.Serial;

using System;
using System.Collections.Generic;
using System.IO.Ports;
using System.Linq;
using System.Management;
using System.Runtime.Versioning;

/// <summary>
/// Production adapter wrapping <see cref="SerialPort"/>. The MaxMix firmware
/// runs at 76800 baud, 8 data bits, no parity, 1 stop bit (8N1), no flow
/// control. Read/write timeouts come from the configured
/// <see cref="SerialDeviceServiceOptions"/>.
/// </summary>
public sealed class SystemSerialPortAdapter : ISerialPortAdapter
{
    // CH340 USB-Serial chip identifiers — used to prioritise the right port
    // when multiple COM ports exist. VID 1A86, PID 7523 = QinHeng CH340.
    private const string Ch340Hardware = "VID_1A86&PID_7523";

    private readonly int _baudRate;
    private readonly int _readTimeoutMs;
    private readonly int _writeTimeoutMs;

    private SerialPort? _port;

    public SystemSerialPortAdapter(int baudRate = 76800, int readTimeoutMs = 20, int writeTimeoutMs = 200)
    {
        _baudRate       = baudRate;
        _readTimeoutMs  = readTimeoutMs;
        _writeTimeoutMs = writeTimeoutMs;
    }

    public bool IsOpen => _port?.IsOpen ?? false;
    public string? PortName => _port?.PortName;
    public int BytesToRead => IsOpen ? _port!.BytesToRead : 0;

    public void Open(string portName)
    {
        _port = new SerialPort(portName, _baudRate, Parity.None, 8, StopBits.One)
        {
            ReadTimeout  = _readTimeoutMs,
            WriteTimeout = _writeTimeoutMs,
            DtrEnable    = true,
            RtsEnable    = true,
            Handshake    = Handshake.None,
        };
        _port.Open();
    }

    public void Close()
    {
        var p = _port;
        if (p == null) return;
        try { if (p.IsOpen) p.Close(); }
        catch { /* ignore on close */ }
        _port = null;
    }

    public int Read(byte[] buffer, int offset, int count)
    {
        var p = _port;
        if (p == null || !p.IsOpen) return 0;
        try { return p.Read(buffer, offset, count); }
        catch (TimeoutException) { return 0; }
    }

    public void Write(byte[] buffer, int offset, int count)
    {
        var p = _port ?? throw new InvalidOperationException("Port is not open.");
        p.Write(buffer, offset, count);
    }

    public void DiscardInBuffer()
    {
        try { _port?.DiscardInBuffer(); } catch { /* ignore */ }
    }

    /// <summary>
    /// Lists candidate COM ports, ordering known CH340 devices first so the
    /// MaxMix appears before unrelated dongles. Falls back to plain
    /// <see cref="SerialPort.GetPortNames"/> if WMI is unavailable.
    /// </summary>
    public IReadOnlyList<string> GetAvailablePortNames()
    {
        var all = SerialPort.GetPortNames();
        if (all.Length == 0) return Array.Empty<string>();

        try
        {
            if (!OperatingSystem.IsWindows()) return all;
            var ch340 = QueryCh340Ports();
            if (ch340.Count == 0) return all;

            // CH340 first, then the rest preserving original order.
            var ordered = new List<string>(ch340);
            foreach (var p in all) if (!ordered.Contains(p)) ordered.Add(p);
            return ordered;
        }
        catch
        {
            return all;
        }
    }

    [SupportedOSPlatform("windows")]
    private static List<string> QueryCh340Ports()
    {
        var result = new List<string>();
        // Win32_PnPEntity Caption typically reads "USB-SERIAL CH340 (COM5)"
        // and the DeviceID contains "VID_1A86&PID_7523".
        using var searcher = new ManagementObjectSearcher(
            "SELECT Caption, DeviceID FROM Win32_PnPEntity WHERE Caption LIKE '%(COM%'");
        foreach (var obj in searcher.Get())
        {
            var caption = obj["Caption"]?.ToString();
            var deviceId = obj["DeviceID"]?.ToString();
            if (caption == null) continue;

            bool isCh340 =
                (deviceId != null && deviceId.IndexOf(Ch340Hardware, StringComparison.OrdinalIgnoreCase) >= 0) ||
                caption.IndexOf("CH340", StringComparison.OrdinalIgnoreCase) >= 0;

            if (!isCh340) continue;

            int open = caption.LastIndexOf("(COM", StringComparison.Ordinal);
            int close = caption.LastIndexOf(')');
            if (open < 0 || close <= open) continue;
            var name = caption.Substring(open + 1, close - open - 1);
            if (!result.Contains(name)) result.Add(name);
        }
        return result;
    }

    public void Dispose() => Close();
}
