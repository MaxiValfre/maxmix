namespace MaxMix.App.Orchestration;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MaxMix.Core.Abstractions;
using MaxMix.Core.Events;
using MaxMix.Core.Models;
using MaxMix.Core.Protocol;
using MaxMix.Core.Settings;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

/// <summary>
/// The glue layer between WASAPI (<see cref="IAudioSessionManager"/>) and the
/// firmware (<see cref="ISerialConnection"/>). Holds the authoritative view
/// of "what is on the device right now" and reconciles changes from either
/// side. See spec section 3 (Device Controller).
///
/// <para>Threading: all event handlers run on the caller's thread. Mutations
/// to internal state go through the <see cref="_gate"/> lock. Outbound serial
/// writes are fire-and-forget against the transport's own queue.</para>
///
/// <para>Feedback loop guard: when the device tells us volume=N for a session
/// we record (sessionId, expectedVolume) in <see cref="_pendingDeviceEchoes"/>.
/// The next VolumeChanged event from the audio manager that matches within
/// <see cref="StateOrchestratorOptions.EchoVolumeTolerance"/> consumes the
/// entry without forwarding the change back to the device. Same scheme for
/// mute changes.</para>
/// </summary>
public sealed class StateOrchestrator : IDeviceController, IDisposable
{
    /// <summary>
    /// User-visible mode cycle. Constrained to <see cref="DisplayMode.Application"/>
    /// only — Output / Input / Game are firmware-only modes that the desktop
    /// no longer drives. <see cref="CycleModeAsync"/> is a no-op as a result
    /// (single-element cycle wraps to itself), but still resyncs MODE_STATES /
    /// SETTINGS to the firmware (Bug #5 workaround).
    /// </summary>
    public static readonly DisplayMode[] UserModeCycle =
    {
        DisplayMode.Application,
    };

    /// <summary>
    /// Mode-states payload pinned to Application-only. Each slot is a sub-state:
    ///   0 = STATE_NAVIGATE (browse sessions), 1 = STATE_EDIT (adjust volume).
    /// Application starts in NAVIGATE so the user sees the app list first.
    /// Output / Input / Game are zeroed (not used by desktop).
    /// Sent on initial sync and on every <see cref="CycleModeAsync"/> call so
    /// firmware stays in lock-step.
    /// </summary>
    private static readonly ModeStates ApplicationOnlyModeStates =
        new(Splash: 0, Output: 0, Input: 0, Application: 0, Game: 0);

    private readonly IAudioSessionManager _audio;
    private readonly ISerialConnection _serial;
    private readonly IProtocolCodec _codec;
    private readonly StateOrchestratorOptions _options;
    private readonly ISettingsService? _settingsService;
    private readonly ILogger<StateOrchestrator> _log;
    private readonly object _gate = new();
    private Timer? _idleTimer;

    // Per-session "we asked Windows to set this volume/mute, ignore the echo".
    private readonly Dictionary<byte, byte> _pendingVolumeEchoes = new();
    private readonly Dictionary<byte, bool> _pendingMuteEchoes = new();

    private DisplayMode _currentMode;
    private int _currentIndex;
    private DateTime _lastUserActivityUtc = DateTime.UtcNow;
    private DateTime _idleLightingSinceUtc = DateTime.UtcNow;
    private bool _idleLightingActive;
    private bool _started;
    private bool _disposed;

    public StateOrchestrator(
        IAudioSessionManager audio,
        ISerialConnection serial,
        IProtocolCodec codec,
        StateOrchestratorOptions? options = null,
        ILogger<StateOrchestrator>? logger = null,
        ISettingsService? settingsService = null)
    {
        _audio = audio ?? throw new ArgumentNullException(nameof(audio));
        _serial = serial ?? throw new ArgumentNullException(nameof(serial));
        _codec = codec ?? throw new ArgumentNullException(nameof(codec));
        _options = options ?? new StateOrchestratorOptions();
        _log = logger ?? NullLogger<StateOrchestrator>.Instance;
        _settingsService = settingsService;
        // Always start in Application mode regardless of persisted/option value.
        // Output / Input / Game are no longer driven by the desktop (Batch 2).
        _currentMode = DisplayMode.Application;
    }

    // ---------- IDeviceController ----------

    public DisplayMode CurrentMode
    {
        get { lock (_gate) return _currentMode; }
    }

    public int CurrentIndex
    {
        get { lock (_gate) return _currentIndex; }
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        lock (_gate)
        {
            if (_started) return;
            _started = true;
        }

        _audio.SessionAdded   += OnSessionAdded;
        _audio.SessionRemoved += OnSessionRemoved;
        _audio.VolumeChanged  += OnAudioVolumeChanged;
        _audio.MuteChanged    += OnAudioMuteChanged;
        _serial.FrameReceived += OnSerialFrameReceived;
        _serial.StateChanged  += OnSerialStateChanged;

        // Live settings pipeline: when the user saves settings the service
        // raises SettingsChanged; we re-push SETTINGS + MODE_STATES so the
        // device picks up the new values immediately. Subscription is optional —
        // tests that construct the orchestrator without a settings service
        // continue to work via _options.Initial* fallbacks.
        if (_settingsService is not null)
        {
            _settingsService.SettingsChanged += OnSettingsServiceChanged;
        }

        lock (_gate)
        {
            _lastUserActivityUtc = DateTime.UtcNow;
            _idleLightingActive = false;
        }
        ConfigureIdleTimer();

        await _audio.StartAsync(cancellationToken).ConfigureAwait(false);
        await _serial.StartAsync(cancellationToken: cancellationToken).ConfigureAwait(false);

        _log.LogInformation("Orchestrator started. Waiting for device connection...");

        // If the device is already ready (unlikely but possible), sync now.
        if (_serial.IsConnected)
        {
            await SyncStateAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (!_started) return Task.CompletedTask;
            _started = false;
        }

        _serial.StateChanged  -= OnSerialStateChanged;
        _audio.SessionAdded   -= OnSessionAdded;
        _audio.SessionRemoved -= OnSessionRemoved;
        _audio.VolumeChanged  -= OnAudioVolumeChanged;
        _audio.MuteChanged    -= OnAudioMuteChanged;
        _serial.FrameReceived -= OnSerialFrameReceived;

        if (_settingsService is not null)
        {
            _settingsService.SettingsChanged -= OnSettingsServiceChanged;
        }

        lock (_gate)
        {
            _idleTimer?.Dispose();
            _idleTimer = null;
            _idleLightingActive = false;
        }

        return _audio.StopAsync(cancellationToken);
    }

    public async Task SyncStateAsync(CancellationToken cancellationToken = default)
    {
        // Order matters for firmware: SETTINGS first (LED state), then
        // MODE_STATES, then SESSION_INFO + per-slot SESSION_DATA.
        await SendSettingsAsync(cancellationToken).ConfigureAwait(false);
        await SendModeStatesAsync(cancellationToken).ConfigureAwait(false);
        await SendSessionSnapshotAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task CycleModeAsync(CancellationToken cancellationToken = default)
    {
        RecordUserActivity();

        DisplayMode previous, next;
        lock (_gate)
        {
            previous = _currentMode;
            int idx = Array.IndexOf(UserModeCycle, _currentMode);
            if (idx < 0) idx = 0; // current mode is Splash or unknown — start cycle from Output
            // Bug #2 fix: parentheses are MANDATORY; without them % binds tighter than +.
            int nextIdx = SlotMapper.NextIndex(idx, UserModeCycle.Length);
            _currentMode = UserModeCycle[nextIdx];
            _currentIndex = 0; // reset slot pointer when switching mode
            next = _currentMode;
        }
        _log.LogInformation("Mode cycle: {OldMode} -> {NewMode}.", previous, next);
        // Firmware needs MODE_STATES + SETTINGS resent on every mode change
        // (Bug #5 workaround: dirty flag is always true).
        await SendModeStatesAsync(cancellationToken).ConfigureAwait(false);
        await SendSettingsAsync(cancellationToken).ConfigureAwait(false);
        await SendSessionSnapshotAsync(cancellationToken).ConfigureAwait(false);
    }

    public Task NextSessionAsync(CancellationToken cancellationToken = default)
    {
        RecordUserActivity();

        var sessions = ActiveSessions();
        if (sessions.Count == 0) return Task.CompletedTask;
        lock (_gate)
        {
            _currentIndex = SlotMapper.NextIndex(_currentIndex, sessions.Count);
        }
        return SendSessionSnapshotAsync(cancellationToken);
    }

    public Task PreviousSessionAsync(CancellationToken cancellationToken = default)
    {
        RecordUserActivity();

        var sessions = ActiveSessions();
        if (sessions.Count == 0) return Task.CompletedTask;
        lock (_gate)
        {
            _currentIndex = SlotMapper.PreviousIndex(_currentIndex, sessions.Count);
        }
        return SendSessionSnapshotAsync(cancellationToken);
    }

    // ---------- Audio → Device ----------

    private void OnSessionAdded(object? sender, SessionAddedEvent e)
    {
        // Reset slot pointer if the session list grew while index was past the end.
        _ = SendSessionSnapshotAsync();
    }

    private void OnSessionRemoved(object? sender, SessionRemovedEvent e)
    {
        lock (_gate)
        {
            // Clamp index to remaining session count.
            var sessions = ActiveSessionsLocked();
            if (sessions.Count == 0) _currentIndex = 0;
            else if (_currentIndex >= sessions.Count) _currentIndex = sessions.Count - 1;
        }
        _ = SendSessionSnapshotAsync();
    }

    private void OnAudioVolumeChanged(object? sender, VolumeChangedEvent e)
    {
        // Feedback loop guard: if we just told Windows to set this volume
        // because the device asked, do NOT echo it back to the device.
        if (TryConsumeVolumeEcho(e.SessionId, e.Volume)) return;

        var slotCommand = ResolveSlotCommandForSession(e.SessionId);
        if (slotCommand is null) return;

        // Look up current mute state from the audio manager so the wire frame
        // carries both volume and mute consistently.
        var snap = _audio.Sessions.FirstOrDefault(s => s.Id == e.SessionId);
        var volumeData = new VolumeData(
            Id: e.SessionId,
            IsDefault: snap.IsDefault,
            Volume: e.Volume,
            IsMuted: snap.IsMuted);

        SafeSendVolumeChange(slotCommand.Value, volumeData);
    }

    private void OnAudioMuteChanged(object? sender, MuteChangedEvent e)
    {
        if (TryConsumeMuteEcho(e.SessionId, e.IsMuted)) return;

        var slotCommand = ResolveSlotCommandForSession(e.SessionId);
        if (slotCommand is null) return;

        var snap = _audio.Sessions.FirstOrDefault(s => s.Id == e.SessionId);
        var volumeData = new VolumeData(
            Id: e.SessionId,
            IsDefault: snap.IsDefault,
            Volume: snap.Volume,
            IsMuted: e.IsMuted);

        SafeSendVolumeChange(slotCommand.Value, volumeData);
    }

    /// <summary>
    /// Fire-and-forget wrapper around <see cref="ISerialConnection.SendAsync"/>
    /// for VOLUME_*_CHANGE frames. Failures are logged but never thrown back —
    /// transport dropouts must not crash audio event handlers.
    /// </summary>
    private void SafeSendVolumeChange(Command slotCommand, VolumeData volumeData)
    {
        try
        {
            var frame = _codec.EncodeVolumeChange(slotCommand, volumeData);
            _ = _serial.SendAsync(frame).AsTask().ContinueWith(t =>
            {
                if (t.Exception is { } ex)
                    _log.LogWarning(ex.GetBaseException(),
                        "Failed to send {Command} frame for session {Id}.",
                        slotCommand, volumeData.Id);
            }, TaskScheduler.Default);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Failed to encode {Command} frame for session {Id}.",
                slotCommand, volumeData.Id);
        }
    }

    // ---------- Serial connection state ----------

    private async void OnSerialStateChanged(object? sender, ConnectionState newState)
    {
        _log.LogInformation("Serial connection state changed to {State}.", newState);
        if (newState == ConnectionState.Ready)
        {
            _log.LogInformation("Device connected! Syncing state...");
            try
            {
                await SyncStateAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Failed to sync state after device connection.");
            }
        }
    }

    // ---------- Settings → Device ----------

    /// <summary>
    /// Re-pushes SETTINGS + MODE_STATES whenever the user saves settings.
    /// <para>
    /// Guard: when the device is not currently in <see cref="ConnectionState.Ready"/>
    /// we silently skip the send. The next reconnect runs <see cref="SyncStateAsync"/>
    /// which already picks up <see cref="_settingsService"/>.<see cref="ISettingsService.Current"/>,
    /// so the settings reach the device on the next handshake without any
    /// pending-buffer logic here.
    /// </para>
    /// <para>
    /// Async-void is acceptable here: this is the final subscriber on
    /// <see cref="ISettingsService.SettingsChanged"/> and exceptions are
    /// caught + logged inside the inner <c>Send*Async</c> helpers.
    /// </para>
    /// </summary>
    private async void OnSettingsServiceChanged(object? sender, AppSettings _)
    {
        try
        {
            RecordUserActivity(pushSettingsWhenRestored: false);
            ConfigureIdleTimer();

            // Disconnected-device guard: skip the wire push. SyncStateAsync on
            // the next StateChanged → Ready will resend via the same code path.
            if (!_serial.IsConnected)
            {
                _log.LogDebug("SettingsChanged received while serial is disconnected; skipping push.");
                return;
            }

            await SendSettingsAsync().ConfigureAwait(false);
            await SendModeStatesAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Never let a settings-push failure poison the UI thread.
            _log.LogError(ex, "Failed to push settings change to device.");
        }
    }

    // ---------- Device → Audio ----------

    private void OnSerialFrameReceived(object? sender, ReadOnlyMemory<byte> frame)
    {
        if (frame.Length < 1) return;
        var cmd = (Command)frame.Span[0];
        var payload = frame.Slice(1);

        switch (cmd)
        {
            case Command.VolumeCurrChange:
            case Command.VolumeAltChange:
            case Command.VolumePrevChange:
            case Command.VolumeNextChange:
                if (payload.Length == 2)
                {
                    var v = _codec.DecodeVolumeData(payload.Span);
                    ApplyDeviceVolumeChange(v);
                }
                break;

            case Command.SessionInfo:
                if (payload.Length == _codec.ExpectedPayloadSize(Command.SessionInfo))
                {
                    var info = _codec.DecodeSessionInfo(payload.Span);
                    ApplyDeviceSessionNavigation(info);
                }
                break;

            // Other inbound commands (OK heartbeat, TEST, DEBUG) are ignored here.
        }
    }

    private void ApplyDeviceVolumeChange(VolumeData v)
    {
        RecordUserActivity();

        // Record the expected echo BEFORE we call SetVolumeAsync so the audio
        // manager's notification fires after our marker is in place.
        lock (_gate)
        {
            _pendingVolumeEchoes[v.Id] = v.Volume;
            _pendingMuteEchoes[v.Id]   = v.IsMuted;
        }

        // Fire-and-forget. WASAPI throws COM exceptions when the target
        // session disappeared between our marker and the SetVolume call;
        // log and continue.
        _ = _audio.SetVolumeAsync(v.Id, v.Volume, v.IsMuted)
                  .ContinueWith(t =>
                  {
                      if (t.Exception is { } ex)
                          _log.LogWarning(ex.GetBaseException(),
                              "WASAPI SetVolume failed for session {Id}; session may have ended.", v.Id);
                   }, TaskScheduler.Default);
    }

    /// <summary>
    /// Handles an inbound SESSION_INFO from the firmware. This happens when the
    /// user rotates the encoder on the device — the firmware updates its own
    /// <c>current</c> index and sends a SESSION_INFO back to the desktop with
    /// the new position. We update <see cref="_currentIndex"/> to match and
    /// re-send the session data (current/previous/next) so the firmware has
    /// fresh display-names and volume data for the newly selected session window.
    /// </summary>
    private void ApplyDeviceSessionNavigation(SessionInfo info)
    {
        RecordUserActivity();

        lock (_gate)
        {
            _currentIndex = info.Current;
        }

        _log.LogDebug(
            "Firmware navigated to session index {Index} (mode={Mode}).",
            info.Current, info.Mode);

        // Re-send the session snapshot so firmware gets fresh data for the
        // newly selected session and its neighbours.
        _ = SendSessionSnapshotAsync().ContinueWith(t =>
        {
            if (t.Exception is { } ex)
                _log.LogWarning(ex.GetBaseException(),
                    "Failed to send session snapshot after device navigation.");
        }, TaskScheduler.Default);
    }

    // ---------- Feedback guard helpers ----------

    private bool TryConsumeVolumeEcho(byte sessionId, byte volume)
    {
        lock (_gate)
        {
            if (_pendingVolumeEchoes.TryGetValue(sessionId, out var expected) &&
                Math.Abs(expected - volume) <= _options.EchoVolumeTolerance)
            {
                _pendingVolumeEchoes.Remove(sessionId);
                _log.LogDebug(
                    "Feedback-loop guard consumed volume echo: id={Id}, expected={Expected}, got={Got}.",
                    sessionId, expected, volume);
                return true;
            }
            return false;
        }
    }

    private bool TryConsumeMuteEcho(byte sessionId, bool isMuted)
    {
        lock (_gate)
        {
            if (_pendingMuteEchoes.TryGetValue(sessionId, out var expected) &&
                expected == isMuted)
            {
                _pendingMuteEchoes.Remove(sessionId);
                _log.LogDebug(
                    "Feedback-loop guard consumed mute echo: id={Id}, muted={Muted}.",
                    sessionId, isMuted);
                return true;
            }
            return false;
        }
    }

    // ---------- Wire output ----------
    //
    // All Send*Async helpers swallow non-cancellation exceptions and log them.
    // Rationale: an individual frame failing (e.g. transport just dropped to
    // Backoff) must NOT take the orchestrator down. The next state-change or
    // sync cycle will retry naturally.

    private async Task SendSettingsAsync(CancellationToken ct = default)
    {
        try
        {
            // Prefer live user settings when an ISettingsService is wired in
            // (production path). Fall back to _options.InitialDeviceSettings
            // for tests that construct the orchestrator without DI.
            DisplayMode mode;
            bool idleLightingActive;
            DateTime idleLightingSinceUtc;
            lock (_gate)
            {
                mode = _currentMode;
                idleLightingActive = _idleLightingActive;
                idleLightingSinceUtc = _idleLightingSinceUtc;
            }

            var deviceSettings = _settingsService is not null
                ? DeviceSettingsMapper.Map(
                    _settingsService.Current,
                    mode,
                    idleLightingActive
                        ? IdleLightingProfileComposer.Compose(
                            _settingsService.Current,
                            mode,
                            idleLightingSinceUtc,
                            DateTime.UtcNow)
                        : null)
                : _options.InitialDeviceSettings;

            var frame = _codec.EncodeSettings(deviceSettings);
            await _serial.SendAsync(frame, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Failed to send SETTINGS frame; will retry on next sync.");
        }
    }

    private async Task SendModeStatesAsync(CancellationToken ct = default)
    {
        try
        {
            // Application-only after Batch 2; ignore _options.InitialModeStates
            // so legacy callers that still pass an all-modes-enabled payload
            // can't accidentally re-enable Output / Input / Game on the firmware.
            var frame = _codec.EncodeModeStates(ApplicationOnlyModeStates);
            await _serial.SendAsync(frame, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Failed to send MODE_STATES frame; will retry on next sync.");
        }
    }

    private async Task SendSessionSnapshotAsync(CancellationToken ct = default)
    {
        SessionInfo info;
        SessionSlots slots;
        DisplayMode mode;

        lock (_gate)
        {
            mode = _currentMode;
            var sessions = ActiveSessionsLocked();
            slots = SlotMapper.Map(sessions, _currentIndex);

            info = new SessionInfo(
                Mode: mode,
                Current: (byte)(sessions.Count == 0 ? 0 : ((_currentIndex % sessions.Count) + sessions.Count) % sessions.Count),
                OutputCount:      (byte)CountByKind(AudioSessionKind.OutputDevice),
                InputCount:       (byte)CountByKind(AudioSessionKind.InputDevice),
                ApplicationCount: (byte)CountByKind(AudioSessionKind.Application));
        }

        await _serial.SendAsync(_codec.EncodeSessionInfo(info), ct).ConfigureAwait(false);

        // Per-slot SESSION_DATA frames.
        if (slots.Current is { } cur)
            await _serial.SendAsync(_codec.EncodeSessionData(Command.CurrentSession, ToSessionData(cur)), ct).ConfigureAwait(false);
        if (mode == DisplayMode.Game && slots.Alternate is { } alt)
            await _serial.SendAsync(_codec.EncodeSessionData(Command.AlternateSession, ToSessionData(alt)), ct).ConfigureAwait(false);
        if (slots.Previous is { } prev)
            await _serial.SendAsync(_codec.EncodeSessionData(Command.PreviousSession, ToSessionData(prev)), ct).ConfigureAwait(false);
        if (slots.Next is { } next)
            await _serial.SendAsync(_codec.EncodeSessionData(Command.NextSession, ToSessionData(next)), ct).ConfigureAwait(false);
    }

    private static SessionData ToSessionData(AudioSessionSnapshot s) =>
        new(s.DisplayName, new VolumeData(s.Id, s.IsDefault, s.Volume, s.IsMuted));

    // ---------- Session selection ----------

    /// <summary>Returns the sessions visible in the current mode.</summary>
    private IReadOnlyList<AudioSessionSnapshot> ActiveSessions()
    {
        lock (_gate) return ActiveSessionsLocked();
    }

    /// <summary>
    /// Returns only <see cref="AudioSessionKind.Application"/> sessions,
    /// unconditionally. Output / Input device sessions and Game-only filtering
    /// were removed in Batch 2: the desktop now manages Application sessions
    /// only, regardless of <see cref="_currentMode"/>. The mode value is still
    /// stored (it is a wire-format byte sent in SESSION_INFO) but does not
    /// gate session selection.
    /// </summary>
    private IReadOnlyList<AudioSessionSnapshot> ActiveSessionsLocked() =>
        _audio.Sessions.Where(s => s.Kind == AudioSessionKind.Application).ToList();

    private int CountByKind(AudioSessionKind kind) =>
        _audio.Sessions.Count(s => s.Kind == kind);

    /// <summary>
    /// If <paramref name="sessionId"/> is present in the active mode's session
    /// list, returns the matching VOLUME_*_CHANGE command for the slot it
    /// occupies (current / alternate / prev / next). Returns null if the
    /// session is not currently visible.
    /// </summary>
    private Command? ResolveSlotCommandForSession(byte sessionId)
    {
        var sessions = ActiveSessions();
        if (sessions.Count == 0) return null;

        int idx;
        DisplayMode mode;
        lock (_gate)
        {
            idx = ((_currentIndex % sessions.Count) + sessions.Count) % sessions.Count;
            mode = _currentMode;
        }

        var slots = SlotMapper.Map(sessions, idx);

        if (slots.Current?.Id == sessionId)   return Command.VolumeCurrChange;
        if (mode == DisplayMode.Game && slots.Alternate?.Id == sessionId) return Command.VolumeAltChange;
        if (slots.Previous?.Id == sessionId)  return Command.VolumePrevChange;
        if (slots.Next?.Id == sessionId)      return Command.VolumeNextChange;
        return null;
    }

    // ---------- IDisposable ----------

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { StopAsync().GetAwaiter().GetResult(); } catch { /* best effort */ }
    }

    private void ConfigureIdleTimer()
    {
        if (_settingsService is null)
        {
            return;
        }

        var idleEnabled = _settingsService.Current.IdleLightingEnabled
            && _settingsService.Current.IdleLightingAfterSeconds > 0;

        lock (_gate)
        {
            _idleTimer?.Dispose();
            _idleTimer = null;

            if (!idleEnabled)
            {
                _idleLightingActive = false;
                return;
            }

            _idleTimer = new Timer(OnIdleTimerTick, null, _options.IdleCheckInterval, _options.IdleCheckInterval);
        }
    }

    private void OnIdleTimerTick(object? _)
    {
        try
        {
            if (_settingsService is null)
            {
                return;
            }

            var settings = _settingsService.Current;
            if (!settings.IdleLightingEnabled || settings.IdleLightingAfterSeconds == 0)
            {
                return;
            }

            bool shouldPushIdleSettings = false;
            lock (_gate)
            {
                if (!_started || _disposed)
                {
                    return;
                }

                if (_idleLightingActive)
                {
                    shouldPushIdleSettings = settings.IdleLightingMode != IdleLightingMode.Static;
                    goto SendIfNeeded;
                }

                if (DateTime.UtcNow - _lastUserActivityUtc < TimeSpan.FromSeconds(settings.IdleLightingAfterSeconds))
                {
                    return;
                }

                _idleLightingActive = true;
                _idleLightingSinceUtc = DateTime.UtcNow;
                shouldPushIdleSettings = true;

            SendIfNeeded:
                ;
            }

            if (shouldPushIdleSettings && _serial.IsConnected)
            {
                _ = SendSettingsAsync();
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Idle lighting timer tick failed.");
        }
    }

    private void RecordUserActivity(bool pushSettingsWhenRestored = true)
    {
        bool shouldRestoreSettings;
        lock (_gate)
        {
            _lastUserActivityUtc = DateTime.UtcNow;
            shouldRestoreSettings = _idleLightingActive;
            _idleLightingActive = false;
        }

        if (pushSettingsWhenRestored && shouldRestoreSettings && _serial.IsConnected)
        {
            _ = SendSettingsAsync();
        }
    }

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(StateOrchestrator));
    }
}
