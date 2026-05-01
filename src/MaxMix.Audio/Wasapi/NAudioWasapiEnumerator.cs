namespace MaxMix.Audio.Wasapi;

using System.Diagnostics;
using System.Runtime.Versioning;
using MaxMix.Audio.Identity;
using MaxMix.Audio.Sessions;
using MaxMix.Core.Abstractions;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

/// <summary>
/// Real <see cref="IAudioEnumerator"/> backed by NAudio's <see cref="MMDeviceEnumerator"/>.
///
/// <para>
/// On every <see cref="EnumerateSessions"/> call:
///   1. enumerates all active output endpoints (render),
///   2. enumerates all active input endpoints (capture),
///   3. for each output endpoint, enumerates per-app audio sessions.
/// </para>
///
/// <para>
/// The result is a flat list of <see cref="RawAudioSession"/> values; grouping
/// by exe path is the registry's job.
/// </para>
///
/// <para>
/// Also wires WASAPI <see cref="IMMNotificationClient"/> + <see cref="IAudioSessionNotification"/>
/// so push events trigger an immediate reconciliation via the supplied callback.
/// The 2s timer in <see cref="WasapiSessionManager"/> is the safety net if any
/// of these native callbacks miss.
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class NAudioWasapiEnumerator : IAudioEnumerator, IDisposable
{
    private readonly MMDeviceEnumerator _deviceEnumerator;
    private readonly Action _onWasapiNotification;
    private readonly Notifications _notifications;
    private readonly HashSet<string> _sessionManagerSubscriptions = new(StringComparer.OrdinalIgnoreCase);

    private bool _disposed;

    /// <summary>
    /// Creates a new enumerator. Pass a callback that triggers reconciliation
    /// (typically <c>WasapiSessionManager.ReconcileNow</c>) — the enumerator
    /// invokes it whenever WASAPI raises a session/device change.
    /// </summary>
    public NAudioWasapiEnumerator(Action onWasapiNotification)
    {
        _onWasapiNotification = onWasapiNotification ?? throw new ArgumentNullException(nameof(onWasapiNotification));
        _deviceEnumerator = new MMDeviceEnumerator();
        _notifications = new Notifications(onWasapiNotification);
        _deviceEnumerator.RegisterEndpointNotificationCallback(_notifications);
    }

    public IReadOnlyList<RawAudioSession> EnumerateSessions()
    {
        ThrowIfDisposed();

        var result = new List<RawAudioSession>(32);
        AppendDeviceEndpoints(result, DataFlow.Render, AudioSessionKind.OutputDevice);
        AppendDeviceEndpoints(result, DataFlow.Capture, AudioSessionKind.InputDevice);
        AppendApplicationSessions(result);
        return result;
    }

    public Task SetVolumeAsync(byte sessionId, byte volume, bool isMuted, CancellationToken ct = default)
    {
        ThrowIfDisposed();

        // Multi-tab apps share an exe path → share an id. Update every match.
        var devices = _deviceEnumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);
        foreach (var device in devices)
        {
            try
            {
                var sessions = device.AudioSessionManager.Sessions;
                for (int i = 0; i < sessions.Count; i++)
                {
                    var session = sessions[i];
                    var path = TryGetExecutablePath(session);
                    var id = SessionIdentity.ComputeId(path);
                    if (id != sessionId) continue;

                    session.SimpleAudioVolume.Volume = Math.Clamp((int)volume, 0, 100) / 100f;
                    session.SimpleAudioVolume.Mute = isMuted;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[MaxMix.Audio] SetVolumeAsync error on device {device.FriendlyName}: {ex.Message}");
            }
        }

        // Endpoint volume (output device): the id maps to a device path.
        // Walk endpoints for matching id and adjust master volume.
        var allDevices = _deviceEnumerator.EnumerateAudioEndPoints(DataFlow.All, DeviceState.Active);
        foreach (var device in allDevices)
        {
            var devicePath = $@"\\Device\\{device.ID}";
            var devId = SessionIdentity.ComputeId(devicePath);
            if (devId != sessionId) continue;

            try
            {
                device.AudioEndpointVolume.MasterVolumeLevelScalar =
                    Math.Clamp((int)volume, 0, 100) / 100f;
                device.AudioEndpointVolume.Mute = isMuted;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[MaxMix.Audio] Endpoint volume set error on {device.FriendlyName}: {ex.Message}");
            }
        }

        return Task.CompletedTask;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _deviceEnumerator.UnregisterEndpointNotificationCallback(_notifications); } catch { }
        _deviceEnumerator.Dispose();
    }

    // ---- helpers ----

    private void AppendDeviceEndpoints(
        List<RawAudioSession> result, DataFlow flow, AudioSessionKind kind)
    {
        MMDevice? defaultDevice = null;
        try
        {
            defaultDevice = _deviceEnumerator.GetDefaultAudioEndpoint(flow, Role.Multimedia);
        }
        catch
        {
            // No default device of this kind (e.g. no microphone). Skip.
        }

        var defaultId = defaultDevice?.ID;
        var devices = _deviceEnumerator.EnumerateAudioEndPoints(flow, DeviceState.Active);
        foreach (var device in devices)
        {
            try
            {
                // Use a synthetic path so the stable-id hash is based on the device id.
                var devicePath = $@"\\Device\\{device.ID}";
                byte volume = 50;
                bool muted = false;
                try
                {
                    var v = device.AudioEndpointVolume.MasterVolumeLevelScalar;
                    volume = (byte)Math.Round(Math.Clamp(v, 0f, 1f) * 100f);
                    muted = device.AudioEndpointVolume.Mute;
                }
                catch { /* ignore — some virtual devices don't expose endpoint volume */ }

                result.Add(new RawAudioSession(
                    ExecutablePath: devicePath,
                    DisplayName: device.FriendlyName,
                    Volume: volume,
                    IsMuted: muted,
                    IsDefault: device.ID == defaultId,
                    Kind: kind));
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[MaxMix.Audio] Failed to read endpoint {device.FriendlyName}: {ex.Message}");
            }
        }

        defaultDevice?.Dispose();
    }

    private void AppendApplicationSessions(List<RawAudioSession> result)
    {
        var devices = _deviceEnumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);
        foreach (var device in devices)
        {
            AudioSessionManager? manager = null;
            try
            {
                manager = device.AudioSessionManager;
                // Subscribe to OnSessionCreated exactly once per device — re-registering
                // on every snapshot would leak handlers (NAudio multicasts internally).
                if (_sessionManagerSubscriptions.Add(device.ID))
                {
                    manager.OnSessionCreated += (_, _) => _onWasapiNotification();
                }

                var sessions = manager.Sessions;
                for (int i = 0; i < sessions.Count; i++)
                {
                    var session = sessions[i];
                    if (session.State == AudioSessionState.AudioSessionStateExpired) continue;

                    var exePath = TryGetExecutablePath(session);
                    var name = string.IsNullOrWhiteSpace(session.DisplayName)
                        ? TryGetProcessName(session)
                        : session.DisplayName;

                    var v = session.SimpleAudioVolume.Volume;
                    var volume = (byte)Math.Round(Math.Clamp(v, 0f, 1f) * 100f);

                    result.Add(new RawAudioSession(
                        ExecutablePath: exePath,
                        DisplayName: name,
                        Volume: volume,
                        IsMuted: session.SimpleAudioVolume.Mute,
                        IsDefault: false,
                        Kind: AudioSessionKind.Application));
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[MaxMix.Audio] Session enumeration error on {device.FriendlyName}: {ex.Message}");
            }
        }
    }

    private static string TryGetExecutablePath(AudioSessionControl session)
    {
        try
        {
            var pid = (int)session.GetProcessID;
            if (pid <= 0) return string.Empty;
            using var p = Process.GetProcessById(pid);
            return p.MainModule?.FileName ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    /// <summary>
    /// Resolves a user-friendly display name for the audio session's process.
    /// Fallback chain (matches the original MaxMix app behaviour):
    ///   1. <see cref="FileVersionInfo.FileDescription"/> — e.g. "Google Chrome"
    ///   2. <see cref="FileVersionInfo.ProductName"/> — e.g. "Google Chrome"
    ///   3. <see cref="Process.MainWindowTitle"/> — e.g. "New Tab - Google Chrome"
    ///   4. <see cref="Process.ProcessName"/> capitalised — e.g. "Chrome"
    ///   5. "Unknown"
    /// </summary>
    private static string TryGetProcessName(AudioSessionControl session)
    {
        try
        {
            var pid = (int)session.GetProcessID;
            if (pid <= 0) return "System";
            using var p = Process.GetProcessById(pid);

            // Try FileVersionInfo first (most reliable for friendly names).
            var exePath = p.MainModule?.FileName;
            if (!string.IsNullOrEmpty(exePath))
            {
                try
                {
                    var versionInfo = FileVersionInfo.GetVersionInfo(exePath);
                    if (!string.IsNullOrWhiteSpace(versionInfo.FileDescription))
                        return versionInfo.FileDescription;
                    if (!string.IsNullOrWhiteSpace(versionInfo.ProductName))
                        return versionInfo.ProductName;
                }
                catch { /* access denied or missing — fall through */ }
            }

            // Try window title.
            if (!string.IsNullOrWhiteSpace(p.MainWindowTitle))
                return p.MainWindowTitle;

            // Last resort: process name with first letter capitalised.
            var name = p.ProcessName;
            if (!string.IsNullOrEmpty(name))
                return char.ToUpper(name[0]) + name.Substring(1);

            return "Unknown";
        }
        catch
        {
            return "Unknown";
        }
    }

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(NAudioWasapiEnumerator));
    }

    /// <summary>
    /// Bridges WASAPI <see cref="IMMNotificationClient"/> callbacks to a single
    /// "something changed, please reconcile" delegate.
    /// </summary>
    private sealed class Notifications : IMMNotificationClient
    {
        private readonly Action _notify;
        public Notifications(Action notify) => _notify = notify;

        public void OnDeviceStateChanged(string deviceId, DeviceState newState) => _notify();
        public void OnDeviceAdded(string pwstrDeviceId) => _notify();
        public void OnDeviceRemoved(string deviceId) => _notify();
        public void OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId) => _notify();
        public void OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key) { /* noisy, skip */ }
    }
}
