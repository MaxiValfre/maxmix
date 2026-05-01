namespace MaxMix.Tests.Logging;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using MaxMix.App.Orchestration;
using MaxMix.Audio;
using MaxMix.Audio.Sessions;
using MaxMix.Audio.Wasapi;
using MaxMix.Core.Abstractions;
using MaxMix.Core.Models;
using MaxMix.Serial;
using MaxMix.Tests.Audio;
using MaxMix.Tests.Orchestration;
using MaxMix.Tests.Serial;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

/// <summary>
/// Verifies that each layer service accepts an <see cref="ILogger{T}"/> and
/// emits structured log entries at the expected lifecycle/error points.
///
/// We capture log calls on a Moq <see cref="ILogger"/> (via a custom helper
/// because <c>ILogger.Log</c> is an extension method on <c>ILogger.Log&lt;TState&gt;</c>).
/// Assertions check that AT LEAST ONE log entry was made at the right level
/// for each scenario — we deliberately don't pin exact message text so the
/// tests survive routine wording polish.
/// </summary>
public class LoggingIntegrationTests
{
    // -------------------------------------------------------------------
    // SerialDeviceService — connection state changes log
    // -------------------------------------------------------------------

    [Fact]
    public async Task SerialDeviceService_logs_state_changes_at_Information_level()
    {
        var logger = new RecordingLogger<SerialDeviceService>();
        var port   = new FakeSerialPortAdapter();
        var codec  = new BinaryProtocolCodec();

        // Successful handshake.
        port.EnqueueHandshakeResponse();

        var opts = new SerialDeviceServiceOptions
        {
            PortName          = "COM5",
            PostOpenDelay     = TimeSpan.Zero,
            HandshakeTimeout  = TimeSpan.FromMilliseconds(200),
            AckTimeout        = TimeSpan.FromMilliseconds(100),
            HeartbeatInterval = TimeSpan.FromMilliseconds(50),
            ReadPollInterval  = TimeSpan.FromMilliseconds(5),
            InitialBackoff    = TimeSpan.FromMilliseconds(20),
            MaxBackoff        = TimeSpan.FromMilliseconds(80),
        };

        await using var svc = new SerialDeviceService(port, codec, opts, logger);

        await svc.StartAsync();

        await WaitFor(() => svc.State == ConnectionState.Ready,
            message: "never reached Ready");

        // Must have logged the Connecting/Handshaking/Ready state changes at Info.
        Assert.True(
            logger.Entries.Any(e => e.Level == LogLevel.Information),
            $"expected at least one Info log; got: {string.Join(", ", logger.Entries.Select(e => e.Level + ":" + e.Message))}");

        // Specifically, we expect Ready to be mentioned somewhere.
        Assert.Contains(logger.Entries, e => e.Message.Contains("Ready", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task SerialDeviceService_logs_handshake_failure_at_Warning_or_Error()
    {
        var logger = new RecordingLogger<SerialDeviceService>();
        var port   = new FakeSerialPortAdapter();
        var codec  = new BinaryProtocolCodec();

        // No firmware reply → handshake will time out.

        var opts = new SerialDeviceServiceOptions
        {
            PortName         = "COM5",
            PostOpenDelay    = TimeSpan.Zero,
            HandshakeTimeout = TimeSpan.FromMilliseconds(60),
            ReadPollInterval = TimeSpan.FromMilliseconds(5),
            InitialBackoff   = TimeSpan.FromMilliseconds(20),
            MaxBackoff       = TimeSpan.FromMilliseconds(40),
        };

        await using var svc = new SerialDeviceService(port, codec, opts, logger);
        await svc.StartAsync();

        await WaitFor(() =>
            logger.Entries.Any(e => e.Level == LogLevel.Warning || e.Level == LogLevel.Error),
            message: "no Warning/Error log emitted for handshake failure");

        Assert.Contains(logger.Entries,
            e => (e.Level == LogLevel.Warning || e.Level == LogLevel.Error)
                 && (e.Message.Contains("handshake", StringComparison.OrdinalIgnoreCase)
                     || e.Message.Contains("desync", StringComparison.OrdinalIgnoreCase)
                     || e.Message.Contains("error", StringComparison.OrdinalIgnoreCase)));
    }

    // -------------------------------------------------------------------
    // WasapiSessionManager — session add/remove logs
    // -------------------------------------------------------------------

    [Fact]
    public async Task WasapiSessionManager_logs_session_added_at_Information_level()
    {
        var logger     = new RecordingLogger<WasapiSessionManager>();
        var enumerator = new MaxMix.Tests.Audio.FakeAudioEnumerator();
        enumerator.SetSessions(new RawAudioSession(
            ExecutablePath: @"c:\spotify.exe",
            DisplayName: "Spotify",
            Volume: 50,
            IsMuted: false,
            IsDefault: false,
            Kind: AudioSessionKind.Application));

        using var mgr = new WasapiSessionManager(enumerator, logger);

        await mgr.StartAsync();

        Assert.Contains(logger.Entries,
            e => e.Level == LogLevel.Information
                 && e.Message.Contains("session", StringComparison.OrdinalIgnoreCase));
    }

    // -------------------------------------------------------------------
    // StateOrchestrator — mode changes log
    // -------------------------------------------------------------------

    [Fact]
    public async Task StateOrchestrator_logs_mode_change_at_Information_level()
    {
        var logger = new RecordingLogger<StateOrchestrator>();
        var audio  = new FakeAudioSessionManager();
        var serial = new FakeSerialConnection();
        var codec  = new BinaryProtocolCodec();

        using var orch = new StateOrchestrator(audio, serial, codec,
            options: null,
            logger: logger);

        await orch.StartAsync();
        await orch.CycleModeAsync();

        Assert.Contains(logger.Entries,
            e => e.Level == LogLevel.Information
                 && (e.Message.Contains("mode", StringComparison.OrdinalIgnoreCase)
                     || e.Message.Contains("cycle", StringComparison.OrdinalIgnoreCase)));
    }

    // -------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------

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
}

/// <summary>
/// Captures log entries in-memory so tests can assert on level and message
/// content. Implementing <see cref="ILogger{T}"/> directly is more reliable
/// than mocking it via Moq because <c>ILogger.Log</c> is generic and
/// <c>LogInformation</c> et al. are extension methods.
/// </summary>
internal sealed class RecordingLogger<T> : ILogger<T>
{
    public sealed record LogEntry(LogLevel Level, string Message, Exception? Exception);

    public List<LogEntry> Entries { get; } = new();

    public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        Entries.Add(new LogEntry(logLevel, formatter(state, exception), exception));
    }

    private sealed class NullScope : IDisposable
    {
        public static readonly NullScope Instance = new();
        public void Dispose() { }
    }
}


