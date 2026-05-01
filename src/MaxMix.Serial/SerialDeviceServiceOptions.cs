namespace MaxMix.Serial;

using System;

/// <summary>
/// Tunable timing knobs for <see cref="SerialDeviceService"/>.
/// Production defaults match the firmware contract (see "MaxMix serial protocol
/// specification"); tests override them for fast deterministic runs.
/// </summary>
public sealed class SerialDeviceServiceOptions
{
    /// <summary>If non-null, skip COM port discovery and use this port directly.</summary>
    public string? PortName { get; set; }

    /// <summary>USB-Serial baud rate for the MaxMix firmware. Fixed by the device.</summary>
    public int BaudRate { get; set; } = 76800;

    /// <summary>
    /// Delay after opening the serial port to allow the Arduino to finish
    /// its bootloader reset cycle. The CH340 asserts DTR on open, which
    /// resets the ATmega328P. The bootloader runs for ~1-2 seconds before
    /// the firmware's setup() starts listening for serial.
    /// </summary>
    public TimeSpan PostOpenDelay { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>How long to wait for the firmware's TEST+OK reply during handshake.</summary>
    public TimeSpan HandshakeTimeout { get; set; } = TimeSpan.FromSeconds(3);

    /// <summary>How long to wait for an OK ACK after sending a command frame.</summary>
    public TimeSpan AckTimeout { get; set; } = TimeSpan.FromMilliseconds(200);

    /// <summary>Idle interval between heartbeat OKs while in Ready state.</summary>
    public TimeSpan HeartbeatInterval { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>Polling cadence used by the I/O reader between non-blocking reads.</summary>
    public TimeSpan ReadPollInterval { get; set; } = TimeSpan.FromMilliseconds(10);

    /// <summary>Read timeout passed to the underlying SerialPort.</summary>
    public TimeSpan ReadTimeout { get; set; } = TimeSpan.FromMilliseconds(20);

    /// <summary>Write timeout passed to the underlying SerialPort.</summary>
    public TimeSpan WriteTimeout { get; set; } = TimeSpan.FromMilliseconds(200);

    /// <summary>Initial backoff after an Error before re-scanning. Doubles up to <see cref="MaxBackoff"/>.</summary>
    public TimeSpan InitialBackoff { get; set; } = TimeSpan.FromMilliseconds(500);

    /// <summary>Cap on backoff delay. Spec says 30s.</summary>
    public TimeSpan MaxBackoff { get; set; } = TimeSpan.FromSeconds(30);
}
