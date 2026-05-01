namespace MaxMix.Serial;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using MaxMix.Core.Abstractions;
using MaxMix.Core.Models;
using MaxMix.Core.Protocol;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

/// <summary>
/// Orchestrates a serial conversation with the MaxMix firmware:
///   1. Scans / opens the COM port (76800 baud, 8N1).
///   2. Performs the TEST/OK handshake.
///   3. Pumps an outbound queue with strict ACK flow control (one frame at a
///      time; nothing is sent until the previous frame got an OK).
///   4. Sends an OK heartbeat ~1s while idle so the firmware knows we're alive.
///   5. Decodes inbound commands into <see cref="FrameReceived"/> events.
///   6. On any I/O exception or desync (unknown command byte, payload underrun)
///      the port is flushed, closed, and a reconnect cycle begins with
///      exponential backoff (500ms → 1s → 2s … capped at 30s).
///
/// All I/O happens on a single dedicated thread so reads and writes never race.
/// External callers (<see cref="StartAsync"/>, <see cref="StopAsync"/>,
/// <see cref="SendAsync"/>) are safe to call from any thread.
/// </summary>
public sealed class SerialDeviceService : ISerialConnection, IAsyncDisposable
{
    private readonly ISerialPortAdapter _port;
    private readonly IProtocolCodec _codec;
    private readonly SerialDeviceServiceOptions _opts;
    private readonly ILogger<SerialDeviceService> _log;

    private readonly ConnectionStateMachine _stateMachine = new();
    private readonly Channel<OutboundFrame> _outbound =
        Channel.CreateUnbounded<OutboundFrame>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });

    // Signalled whenever an OK byte arrives from firmware. Used by the I/O
    // thread to release a pending ACK wait.
    private readonly SemaphoreSlim _ackSignal = new(0, int.MaxValue);

    private readonly CancellationTokenSource _shutdownCts = new();
    private Thread? _ioThread;
    private string? _negotiatedPort;
    private bool _disposed;

    public SerialDeviceService(
        ISerialPortAdapter port,
        IProtocolCodec codec,
        SerialDeviceServiceOptions options,
        ILogger<SerialDeviceService>? logger = null)
    {
        _port  = port  ?? throw new ArgumentNullException(nameof(port));
        _codec = codec ?? throw new ArgumentNullException(nameof(codec));
        _opts  = options ?? throw new ArgumentNullException(nameof(options));
        _log   = logger ?? NullLogger<SerialDeviceService>.Instance;

        _stateMachine.StateChanged += (_, e) =>
        {
            // Connection state changes are user-relevant — log at Information.
            _log.LogInformation(
                "Serial connection state: {OldState} -> {NewState}",
                e.OldState, e.NewState);
            StateChanged?.Invoke(this, e.NewState);
        };
    }

    // ---------- ISerialConnection ----------

    public ConnectionState State => _stateMachine.State;
    public bool IsConnected => State == ConnectionState.Ready;
    public string? PortName => _negotiatedPort;

    public event EventHandler<ConnectionState>? StateChanged;
    public event EventHandler<ReadOnlyMemory<byte>>? FrameReceived;

    public Task StartAsync(string? portName = null, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (_ioThread != null)
        {
            return Task.CompletedTask; // idempotent
        }

        if (portName != null) _opts.PortName = portName;

        _ioThread = new Thread(IoLoop)
        {
            IsBackground = true,
            Name = "MaxMix-Serial-IO"
        };
        _ioThread.Start();
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        if (_disposed) return;
        _shutdownCts.Cancel();
        _outbound.Writer.TryComplete();

        var thread = _ioThread;
        if (thread != null)
        {
            // Wait for the I/O loop to drain. Don't block forever — give it 1s.
            await Task.Run(() => thread.Join(TimeSpan.FromSeconds(1)), cancellationToken);
        }

        try { _port.Close(); } catch { /* swallow on shutdown */ }

        // Force final state to Disconnected so callers see a clean shutdown.
        if (_stateMachine.State != ConnectionState.Disconnected)
        {
            try { _stateMachine.TransitionTo(ConnectionState.Disconnected); }
            catch (InvalidOperationException) { /* already there */ }
        }
    }

    public ValueTask SendAsync(ReadOnlyMemory<byte> frame, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ValidateFrame(frame.Span);

        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var item = new OutboundFrame(frame.ToArray(), tcs);

        if (!_outbound.Writer.TryWrite(item))
        {
            tcs.SetException(new InvalidOperationException("Outbound channel is closed."));
        }
        else if (cancellationToken.CanBeCanceled)
        {
            cancellationToken.Register(() => tcs.TrySetCanceled(cancellationToken));
        }

        return new ValueTask(tcs.Task);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        try { await StopAsync().ConfigureAwait(false); }
        catch { /* swallow */ }
        _shutdownCts.Dispose();
        _ackSignal.Dispose();
        try { _port.Dispose(); } catch { /* swallow */ }
    }

    // ---------- I/O thread ----------

    private void IoLoop()
    {
        var ct = _shutdownCts.Token;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                // ---- Phase 1: open port ----
                if (!TryOpenPort(ct)) { EnterBackoff(ct); continue; }

                // ---- Phase 2: handshake ----
                if (!TryHandshake(ct)) { CloseAndBackoff(ct); continue; }

                // ---- Phase 3: ready loop ----
                _stateMachine.TransitionTo(ConnectionState.Ready);
                bool desync = ReadyLoop(ct);

                // Loop ended — either shutdown or desync. Either way, close.
                try { _port.Close(); } catch { /* ignore */ }
                if (ct.IsCancellationRequested) break;

                // Desync or I/O error → Error → Backoff.
                if (desync) CloseAndBackoff(ct);
            }
        }
        catch (OperationCanceledException) { /* shutdown */ }
        catch (Exception ex)
        {
            // Defensive — never let the I/O thread crash silently.
            _log.LogError(ex, "Serial I/O thread exited with unhandled exception.");
            try { _stateMachine.TransitionTo(ConnectionState.Error); }
            catch (InvalidOperationException) { }
        }

        try { _port.Close(); } catch { /* ignore */ }
    }

    // ---------- Phase 1: discover + open ----------

    private bool TryOpenPort(CancellationToken ct)
    {
        // Resolve the target port: explicit option, or scan.
        string? target = _opts.PortName;
        if (target == null)
        {
            try { _stateMachine.TransitionTo(ConnectionState.Scanning); }
            catch (InvalidOperationException) { /* already there */ }

            var ports = _port.GetAvailablePortNames();
            if (ports.Count == 0)
            {
                try { _stateMachine.TransitionTo(ConnectionState.Error); }
                catch (InvalidOperationException) { }
                return false;
            }
            target = ports[0];
        }

        try { _stateMachine.TransitionTo(ConnectionState.Connecting); }
        catch (InvalidOperationException) { /* already there */ }

        try
        {
            _port.Open(target);
            _negotiatedPort = target;
            // CH340 triggers Arduino reset via DTR on port open.
            // Wait for the bootloader to finish and firmware to start.
            _log.LogDebug("Port {Port} opened; waiting {Delay} for Arduino reset/bootloader.",
                target, _opts.PostOpenDelay);
            Thread.Sleep(_opts.PostOpenDelay);
            return true;
        }
        catch (Exception)
        {
            try { _stateMachine.TransitionTo(ConnectionState.Error); }
            catch (InvalidOperationException) { }
            return false;
        }
    }

    // ---------- Phase 2: handshake ----------

    private bool TryHandshake(CancellationToken ct)
    {
        try { _stateMachine.TransitionTo(ConnectionState.Handshaking); }
        catch (InvalidOperationException) { return false; }

        try
        {
            // Flush any stale data from previous connection attempts.
            try { _port.DiscardInBuffer(); } catch { }

            // Send TEST.
            _log.LogDebug("Handshake: sending TEST byte to {Port}.", _negotiatedPort);
            var testFrame = _codec.EncodeTest();
            _port.Write(testFrame, 0, testFrame.Length);

            // Firmware handshake response:
            //   1. TEST byte (0x01)
            //   2. Version string terminated by \n (e.g. "1.5.6\r\n")
            //   3. OK byte (0x02) — but only after we send OK first
            //
            // The original desktop sends TEST, receives TEST + version, then
            // considers the handshake done. We follow the same pattern.
            var deadline = DateTime.UtcNow + _opts.HandshakeTimeout;
            bool sawTest = false;
            bool readingVersion = false;
            var versionBuf = new List<byte>(16);
            byte[] buf = new byte[1];

            while (DateTime.UtcNow < deadline)
            {
                if (ct.IsCancellationRequested) return false;
                int n = _port.Read(buf, 0, 1);
                if (n == 0) { Thread.Sleep((int)_opts.ReadPollInterval.TotalMilliseconds); continue; }

                byte b = buf[0];

                if (!sawTest)
                {
                    if (b == (byte)Command.Test)
                    {
                        sawTest = true;
                        readingVersion = true;
                        continue;
                    }
                    // Skip any garbage before the TEST byte (bootloader noise).
                    continue;
                }

                if (readingVersion)
                {
                    if (b == (byte)'\n')
                    {
                        // Version string complete.
                        var version = System.Text.Encoding.ASCII.GetString(versionBuf.ToArray()).Trim();
                        _log.LogInformation("Firmware version: {Version} on {Port}.", version, _negotiatedPort);
                        readingVersion = false;

                        // Handshake complete — we got TEST + version.
                        // Send OK to acknowledge and start the communication loop.
                        var okFrame = new byte[] { (byte)Command.Ok };
                        _port.Write(okFrame, 0, okFrame.Length);
                        _log.LogDebug("Handshake complete on {Port}.", _negotiatedPort);
                        return true;
                    }
                    versionBuf.Add(b);
                    continue;
                }

                // Past version, any byte means handshake is done.
                break;
            }

            if (sawTest)
            {
                // Got TEST but version string was incomplete — still try to proceed.
                _log.LogWarning("Handshake on {Port}: received TEST but version was incomplete.", _negotiatedPort);
                var okFrame = new byte[] { (byte)Command.Ok };
                _port.Write(okFrame, 0, okFrame.Length);
                return true;
            }

            _log.LogWarning("Handshake timeout on {Port}; firmware did not reply within {Timeout}.",
                _negotiatedPort, _opts.HandshakeTimeout);
            return false;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Handshake failed on {Port}.", _negotiatedPort);
            return false;
        }
    }

    // ---------- Phase 3: ready loop ----------

    /// <summary>
    /// Pumps reads, heartbeats, and outbound frames while in Ready state.
    /// Returns <c>true</c> if a desync/I-O error broke the loop, <c>false</c>
    /// if shutdown ended it.
    /// </summary>
    private bool ReadyLoop(CancellationToken ct)
    {
        var lastSendUtc = DateTime.UtcNow;
        // Reset ACK signal — leftover signals from handshake would let the
        // first send claim a phantom ACK.
        DrainSemaphore(_ackSignal);

        while (!ct.IsCancellationRequested)
        {
            // ---- 1. Drain any inbound bytes (frame parse + ACK signal) ----
            try
            {
                if (!ReadInboundOnce(ct))
                {
                    return true; // desync detected
                }
            }
            catch (Exception)
            {
                return true;
            }

            // ---- 2. If an outbound frame is queued, send it & await ACK ----
            if (_outbound.Reader.TryRead(out var frame))
            {
                try
                {
                    _port.Write(frame.Bytes, 0, frame.Bytes.Length);
                    lastSendUtc = DateTime.UtcNow;
                }
                catch (Exception ex)
                {
                    frame.Completion.TrySetException(ex);
                    return true;
                }

                if (!WaitForAck(ct))
                {
                    frame.Completion.TrySetException(new TimeoutException(
                        "No OK ACK received within AckTimeout."));
                    return true;
                }
                frame.Completion.TrySetResult();
                continue; // tight loop while frames remain
            }

            // ---- 3. Heartbeat: send OK if idle for >= HeartbeatInterval ----
            if (DateTime.UtcNow - lastSendUtc >= _opts.HeartbeatInterval)
            {
                try
                {
                    var ok = _codec.EncodeOk();
                    _port.Write(ok, 0, ok.Length);
                    lastSendUtc = DateTime.UtcNow;
                }
                catch (Exception)
                {
                    return true;
                }

                if (!WaitForAck(ct))
                {
                    _log.LogWarning("Heartbeat OK not acknowledged within {Timeout}; reconnecting.",
                        _opts.AckTimeout);
                    return true; // heartbeat lost → error
                }
                continue;
            }

            // ---- 4. Idle sleep so we don't hot-spin ----
            Thread.Sleep((int)_opts.ReadPollInterval.TotalMilliseconds);
        }
        return false;
    }

    /// <summary>
    /// Reads up to one full inbound frame (command byte + fixed payload) and
    /// raises <see cref="FrameReceived"/>. Stand-alone OK bytes are consumed
    /// silently and used to release pending ACK waits. Returns <c>false</c>
    /// on desync (unknown command byte or short read).
    /// </summary>
    private bool ReadInboundOnce(CancellationToken ct)
    {
        if (_port.BytesToRead <= 0) return true; // nothing to do

        byte[] cmdBuf = new byte[1];
        int n = _port.Read(cmdBuf, 0, 1);
        if (n == 0) return true;

        byte cmdByte = cmdBuf[0];
        if (!Enum.IsDefined(typeof(Command), cmdByte))
        {
            // Desync — flush everything pending so we don't propagate corruption.
            _log.LogWarning(
                "Serial desync: unknown command byte 0x{Byte:X2}; flushing buffer and reconnecting.",
                cmdByte);
            try { _port.DiscardInBuffer(); } catch { }
            return false;
        }

        var command = (Command)cmdByte;

        // OK is the ACK signal — no payload, just release pending waiters.
        if (command == Command.Ok)
        {
            _ackSignal.Release();
            // Also surface to consumers as a frame (for visibility).
            FrameReceived?.Invoke(this, new ReadOnlyMemory<byte>(new[] { cmdByte }));
            return true;
        }

        int expected;
        try { expected = _codec.ExpectedPayloadSize(command); }
        catch (ArgumentOutOfRangeException) { return false; }

        var frameBuf = new byte[1 + expected];
        frameBuf[0] = cmdByte;

        if (expected > 0)
        {
            int read = 0;
            var deadline = DateTime.UtcNow + _opts.AckTimeout;
            while (read < expected && DateTime.UtcNow < deadline)
            {
                if (ct.IsCancellationRequested) return true;
                int got = _port.Read(frameBuf, 1 + read, expected - read);
                if (got == 0) { Thread.Sleep((int)_opts.ReadPollInterval.TotalMilliseconds); continue; }
                read += got;
            }
            if (read < expected)
            {
                // Read timeout mid-payload → desync.
                _log.LogWarning(
                    "Serial desync: payload underrun for {Command} (got {Got}/{Expected} bytes); flushing.",
                    command, read, expected);
                try { _port.DiscardInBuffer(); } catch { }
                return false;
            }
        }

        FrameReceived?.Invoke(this, new ReadOnlyMemory<byte>(frameBuf));
        return true;
    }

    /// <summary>
    /// Waits for an OK signal up to <see cref="SerialDeviceServiceOptions.AckTimeout"/>,
    /// while continuing to drain inbound bytes. Returns <c>false</c> on timeout
    /// or desync.
    /// </summary>
    private bool WaitForAck(CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + _opts.AckTimeout;
        while (DateTime.UtcNow < deadline)
        {
            if (ct.IsCancellationRequested) return false;

            // Pump inbound — this is what releases the ack signal.
            try
            {
                if (!ReadInboundOnce(ct)) return false;
            }
            catch (Exception)
            {
                return false;
            }

            if (_ackSignal.Wait(0, ct)) return true;
            Thread.Sleep((int)_opts.ReadPollInterval.TotalMilliseconds);
        }
        return false;
    }

    // ---------- Backoff ----------

    /// <summary>
    /// Transitions Error → Backoff and waits the next backoff interval.
    /// If the machine is not yet in Error (e.g., the caller skipped it),
    /// the transition is forced via Error first so the state graph is honored.
    /// </summary>
    private void EnterBackoff(CancellationToken ct)
    {
        // Drive through Error so the state machine accepts Backoff.
        if (_stateMachine.State != ConnectionState.Error &&
            _stateMachine.State != ConnectionState.Backoff)
        {
            try { _stateMachine.TransitionTo(ConnectionState.Error); }
            catch (InvalidOperationException) { /* already past it */ }
        }

        var delay = SafeNextBackoff();
        try { _stateMachine.TransitionTo(ConnectionState.Backoff); }
        catch (InvalidOperationException) { /* already there */ }

        try { Task.Delay(delay, ct).Wait(ct); }
        catch (OperationCanceledException) { }
        catch (AggregateException) { }
    }

    private void CloseAndBackoff(CancellationToken ct)
    {
        try { _port.Close(); } catch { }
        EnterBackoff(ct);
    }

    private TimeSpan SafeNextBackoff()
    {
        var raw = _stateMachine.NextBackoffDelay();
        // Honor caller-overridden bounds (tests use 20ms initial / 80ms max).
        var clamped = raw < _opts.InitialBackoff ? _opts.InitialBackoff : raw;
        if (clamped > _opts.MaxBackoff) clamped = _opts.MaxBackoff;
        return clamped;
    }

    // ---------- Validation ----------

    /// <summary>
    /// Validates the frame's command byte and payload size BEFORE it ever
    /// touches the wire. The protocol has no recovery from a wrong-size
    /// payload (Bug #1 in the original) — we'd rather throw early than
    /// poison the firmware's parser.
    /// </summary>
    private void ValidateFrame(ReadOnlySpan<byte> frame)
    {
        if (frame.Length == 0)
            throw new ArgumentException("Frame must contain at least the command byte.", nameof(frame));

        var cmdByte = frame[0];
        if (!Enum.IsDefined(typeof(Command), cmdByte))
            throw new ArgumentOutOfRangeException(nameof(frame),
                $"Unknown command byte 0x{cmdByte:X2}.");

        var command = (Command)cmdByte;
        int expected = _codec.ExpectedPayloadSize(command);
        int actual = frame.Length - 1;
        if (actual != expected)
            throw new PayloadSizeMismatchException(command, expected, actual);
    }

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(SerialDeviceService));
    }

    private static void DrainSemaphore(SemaphoreSlim s)
    {
        while (s.Wait(0)) { /* drain */ }
    }

    // ---------- Outbound queue item ----------

    private readonly record struct OutboundFrame(byte[] Bytes, TaskCompletionSource Completion);
}
