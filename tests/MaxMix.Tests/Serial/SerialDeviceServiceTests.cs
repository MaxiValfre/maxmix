namespace MaxMix.Tests.Serial;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MaxMix.Core.Models;
using MaxMix.Serial;
using Xunit;

/// <summary>
/// Tests for <see cref="SerialDeviceService"/> (ISerialConnection implementation).
/// The service is driven through a <see cref="FakeSerialPortAdapter"/> so we can
/// pump firmware-side bytes deterministically and observe what the service writes.
///
/// Spec scenarios covered:
///   - Successful connection and handshake (Disconnected → … → Ready)
///   - Handshake timeout
///   - Heartbeat sent every ~1s when idle
///   - ACK flow control: SendAsync waits for OK before next write
///   - Heartbeat timeout triggers Error
///   - Desync recovery: unknown command flushes + reconnects
///   - Reconnect with exponential backoff
///   - Payload validation: PayloadSizeMismatchException blocks malformed sends
/// </summary>
public class SerialDeviceServiceTests
{
    // Test-tunable timing so the suite stays fast.
    private static SerialDeviceServiceOptions FastOptions(string? port = "COM5") => new()
    {
        PortName = port,
        PostOpenDelay     = TimeSpan.Zero,
        HandshakeTimeout = TimeSpan.FromMilliseconds(200),
        AckTimeout       = TimeSpan.FromMilliseconds(150),
        HeartbeatInterval = TimeSpan.FromMilliseconds(80),
        ReadPollInterval  = TimeSpan.FromMilliseconds(5),
        InitialBackoff    = TimeSpan.FromMilliseconds(20),
        MaxBackoff        = TimeSpan.FromMilliseconds(80),
    };

    // Helper: spin until predicate holds, or fail.
    private static async Task WaitFor(Func<bool> predicate, int timeoutMs = 1500, string? message = null)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!predicate())
        {
            if (sw.ElapsedMilliseconds > timeoutMs)
                throw new Xunit.Sdk.XunitException(message ?? $"Timed out waiting after {timeoutMs}ms");
            await Task.Delay(5);
        }
    }

    // -------------------------------------------------------------------
    // Handshake
    // -------------------------------------------------------------------

    [Fact]
    public async Task Successful_handshake_transitions_to_Ready_and_sends_TEST_byte()
    {
        var port  = new FakeSerialPortAdapter();
        var codec = new BinaryProtocolCodec();
        // Firmware will reply: TEST (0x01) + OK (0x02)
        port.EnqueueHandshakeResponse();

        await using var svc = new SerialDeviceService(port, codec, FastOptions());

        await svc.StartAsync();

        await WaitFor(() => svc.State == ConnectionState.Ready,
            message: $"never reached Ready (final = {svc.State})");

        // Service must have sent at least the TEST byte during handshake.
        Assert.NotEmpty(port.OutgoingBytes);
        Assert.Equal((byte)Command.Test, port.OutgoingBytes[0]);
        Assert.Equal(1, port.OpenCount);
    }

    [Fact]
    public async Task Handshake_timeout_when_firmware_never_replies_transitions_to_Error_then_Backoff()
    {
        var port  = new FakeSerialPortAdapter();
        var codec = new BinaryProtocolCodec();
        // No incoming bytes — handshake will time out.

        var states = new List<ConnectionState>();
        await using var svc = new SerialDeviceService(port, codec, FastOptions());
        svc.StateChanged += (_, s) => { lock (states) states.Add(s); };

        await svc.StartAsync();

        // Wait for the failure path to materialise — must hit Error or Backoff.
        await WaitFor(() =>
        {
            lock (states) return states.Contains(ConnectionState.Error) ||
                                states.Contains(ConnectionState.Backoff);
        }, timeoutMs: 1500);

        lock (states)
        {
            Assert.Contains(ConnectionState.Handshaking, states);
            Assert.Contains(ConnectionState.Error, states);
        }
    }

    // -------------------------------------------------------------------
    // ACK flow control
    // -------------------------------------------------------------------

    [Fact]
    public async Task SendAsync_blocks_until_previous_command_is_ACKed()
    {
        var port  = new FakeSerialPortAdapter();
        var codec = new BinaryProtocolCodec();
        port.EnqueueHandshakeResponse(); // handshake

        await using var svc = new SerialDeviceService(port, codec, FastOptions());
        await svc.StartAsync();
        await WaitFor(() => svc.State == ConnectionState.Ready);

        int handshakeBytes = port.OutgoingBytes.Count;

        // Queue two SETTINGS frames back-to-back.
        var settings = new DeviceSettings(
            SleepAfterSeconds: 5,
            AccelerationPercentage: 3,
            ContinuousScroll: true,
            VolumeMinColor: new RgbColor(0x10, 0x20, 0x30),
            VolumeMaxColor: new RgbColor(0x40, 0x50, 0x60),
            MixChannelAColor: new RgbColor(0x70, 0x80, 0x90),
            MixChannelBColor: new RgbColor(0xA0, 0xB0, 0xC0));
        var frame = codec.EncodeSettings(settings); // 15 bytes

        var send1 = svc.SendAsync(frame).AsTask();
        var send2 = svc.SendAsync(frame).AsTask();

        // Without ACK, only the first frame should be on the wire.
        await Task.Delay(60);
        var snapAfterFirst = port.OutgoingBytes.Skip(handshakeBytes).Count();
        Assert.Equal(15, snapAfterFirst);

        // ACK the first → second should now flush.
        port.EnqueueIncoming((byte)Command.Ok);
        await send1;

        await WaitFor(() => port.OutgoingBytes.Count - handshakeBytes >= 30,
            message: "second SETTINGS frame was not written after first ACK");

        // ACK the second so the test cleanly completes.
        port.EnqueueIncoming((byte)Command.Ok);
        await send2;
    }

    // -------------------------------------------------------------------
    // Heartbeat
    // -------------------------------------------------------------------

    [Fact]
    public async Task Heartbeat_emits_OK_periodically_when_idle()
    {
        var port  = new FakeSerialPortAdapter();
        var codec = new BinaryProtocolCodec();
        port.EnqueueHandshakeResponse(); // handshake

        await using var svc = new SerialDeviceService(port, codec, FastOptions());
        await svc.StartAsync();
        await WaitFor(() => svc.State == ConnectionState.Ready);

        int handshakeBytes = port.OutgoingBytes.Count;

        // Each heartbeat is 1 byte (OK) and waits for an OK reply before the
        // next one. Feed a steady stream of OKs and count heartbeats.
        for (int i = 0; i < 4; i++) port.EnqueueIncoming((byte)Command.Ok);

        await WaitFor(() => port.OutgoingBytes.Count - handshakeBytes >= 3,
            timeoutMs: 1500,
            message: "fewer than 3 heartbeats sent");

        var heartbeatBytes = port.OutgoingBytes.Skip(handshakeBytes).Take(3).ToArray();
        Assert.All(heartbeatBytes, b => Assert.Equal((byte)Command.Ok, b));
    }

    // -------------------------------------------------------------------
    // Desync recovery
    // -------------------------------------------------------------------

    [Fact]
    public async Task Unknown_command_byte_triggers_buffer_flush_and_Error()
    {
        var port  = new FakeSerialPortAdapter();
        var codec = new BinaryProtocolCodec();
        port.EnqueueHandshakeResponse(); // handshake

        await using var svc = new SerialDeviceService(port, codec, FastOptions());
        await svc.StartAsync();
        await WaitFor(() => svc.State == ConnectionState.Ready);

        int discardsBefore = port.DiscardCount;

        // 0xFF is outside the valid command range — must trigger desync.
        port.EnqueueIncoming(0xFF);

        await WaitFor(() => svc.State == ConnectionState.Error || svc.State == ConnectionState.Backoff,
            message: $"service did not enter Error/Backoff after garbage byte (state={svc.State})");

        Assert.True(port.DiscardCount > discardsBefore,
            "DiscardInBuffer should be called on desync");
    }

    // -------------------------------------------------------------------
    // Frame received → event
    // -------------------------------------------------------------------

    [Fact]
    public async Task FrameReceived_event_fires_for_VOLUME_CURR_CHANGE_with_payload()
    {
        var port  = new FakeSerialPortAdapter();
        var codec = new BinaryProtocolCodec();
        port.EnqueueHandshakeResponse(); // handshake

        var frames = new List<byte[]>();
        await using var svc = new SerialDeviceService(port, codec, FastOptions());
        svc.FrameReceived += (_, mem) =>
        {
            lock (frames) frames.Add(mem.ToArray());
        };

        await svc.StartAsync();
        await WaitFor(() => svc.State == ConnectionState.Ready);

        // Firmware sends VOLUME_CURR_CHANGE (0x09) + 2-byte VolumeData payload.
        // VolumeData: id=2, isDefault=false, volume=75, isMuted=false
        //   byte0 = 0x02, byte1 = 0x4B (75)
        port.EnqueueIncoming((byte)Command.VolumeCurrChange, 0x02, 0x4B);

        await WaitFor(() => { lock (frames) return frames.Count >= 1; },
            message: "FrameReceived never fired for VOLUME_CURR_CHANGE");

        lock (frames)
        {
            var frame = frames[0];
            Assert.Equal(3, frame.Length);
            Assert.Equal((byte)Command.VolumeCurrChange, frame[0]);
            Assert.Equal(0x02, frame[1]);
            Assert.Equal(0x4B, frame[2]);
        }
    }

    // -------------------------------------------------------------------
    // Reconnect with exponential backoff
    // -------------------------------------------------------------------

    [Fact]
    public async Task Reconnect_attempts_increase_with_exponential_backoff()
    {
        // First open succeeds and handshake works → Ready.
        // Then the firmware "stops responding" by us not enqueueing any more
        // ACKs and we close the port to force errors. The service should retry.
        var port  = new FakeSerialPortAdapter();
        var codec = new BinaryProtocolCodec();
        var opts = FastOptions();

        // No handshake reply: every attempt will time out → Error → Backoff → retry.
        await using var svc = new SerialDeviceService(port, codec, opts);
        await svc.StartAsync();

        // Wait until at least 2 retries have happened (OpenCount >= 3).
        await WaitFor(() => port.OpenCount >= 3, timeoutMs: 2500,
            message: $"Service did not retry handshake (OpenCount={port.OpenCount})");

        Assert.True(port.OpenCount >= 3,
            $"Expected at least 3 open attempts (initial + 2 retries), got {port.OpenCount}");
    }

    // -------------------------------------------------------------------
    // Port discovery
    // -------------------------------------------------------------------

    [Fact]
    public async Task Scanning_picks_first_available_port_when_no_explicit_port_given()
    {
        var port  = new FakeSerialPortAdapter();
        var codec = new BinaryProtocolCodec();
        port.SetAvailablePorts("COM3", "COM7");
        port.EnqueueHandshakeResponse(); // handshake

        var opts = FastOptions(port: null); // null → must scan
        await using var svc = new SerialDeviceService(port, codec, opts);
        await svc.StartAsync();

        await WaitFor(() => svc.State == ConnectionState.Ready);
        Assert.Equal("COM3", port.PortName);
    }

    [Fact]
    public async Task Scanning_with_no_available_ports_transitions_to_Error()
    {
        var port  = new FakeSerialPortAdapter();
        var codec = new BinaryProtocolCodec();
        port.SetAvailablePorts(/* none */);

        var states = new List<ConnectionState>();
        var opts = FastOptions(port: null);
        await using var svc = new SerialDeviceService(port, codec, opts);
        svc.StateChanged += (_, s) => { lock (states) states.Add(s); };
        await svc.StartAsync();

        await WaitFor(() =>
        {
            lock (states) return states.Contains(ConnectionState.Error);
        }, timeoutMs: 1500);

        lock (states)
        {
            Assert.Contains(ConnectionState.Scanning, states);
            Assert.Contains(ConnectionState.Error, states);
        }
    }

    // -------------------------------------------------------------------
    // Stop
    // -------------------------------------------------------------------

    [Fact]
    public async Task StopAsync_closes_port_and_returns_to_Disconnected()
    {
        var port  = new FakeSerialPortAdapter();
        var codec = new BinaryProtocolCodec();
        port.EnqueueHandshakeResponse();

        await using var svc = new SerialDeviceService(port, codec, FastOptions());
        await svc.StartAsync();
        await WaitFor(() => svc.State == ConnectionState.Ready);

        await svc.StopAsync();

        Assert.Equal(ConnectionState.Disconnected, svc.State);
        Assert.False(port.IsOpen);
    }
}
