namespace MaxMix.App.Services;

using System;
using System.Diagnostics;
using MaxMix.Core.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Win32;

/// <summary>
/// Toggles "start with Windows" by writing/removing a value under
/// <c>HKCU\Software\Microsoft\Windows\CurrentVersion\Run</c>.
///
/// HKCU is deliberate: it doesn't need elevation, it's per-user (matches
/// install scope of an unpackaged WPF app), and it's the standard location
/// for user-mode autostart.
///
/// <para>
/// Implements <see cref="IAutoStartService"/> so consumers (SettingsViewModel,
/// App) can be unit-tested with a Moq instance. The internal ctor overload
/// lets tests target a unique GUID-based value name and never touch the real
/// "MaxMix" autostart entry — see <c>AutoStartServiceTests</c>.
/// </para>
///
/// <para>
/// Failure handling: a registry permission denial (group policy, AV lockdown)
/// is logged at Warning level and surfaced as <c>false</c> from
/// <see cref="SetEnabled"/>. The caller (Save flow, App.OnStartup) stays
/// resilient — auto-start is best-effort, not blocking.
/// </para>
/// </summary>
public sealed class AutoStartService : IAutoStartService
{
    private const string DefaultRunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string DefaultName       = "MaxMix";

    private readonly string _runKeyPath;
    private readonly string _valueName;
    private readonly ILogger<AutoStartService> _log;

    /// <summary>Production ctor: real "MaxMix" value name, no logger.</summary>
    public AutoStartService()
        : this(DefaultName, DefaultRunKeyPath, null) { }

    /// <summary>DI-friendly ctor: real value name + injected logger.</summary>
    public AutoStartService(ILogger<AutoStartService>? logger)
        : this(DefaultName, DefaultRunKeyPath, logger) { }

    /// <summary>
    /// Test seam: lets tests pass a unique value name so the real "MaxMix"
    /// entry is never touched. Internal so it doesn't widen the public API
    /// surface; <c>InternalsVisibleTo("MaxMix.Tests")</c> exposes it to the
    /// test assembly.
    /// </summary>
    internal AutoStartService(string valueName, ILogger<AutoStartService>? logger)
        : this(valueName, DefaultRunKeyPath, logger) { }

    /// <summary>
    /// Test seam (failure-path): lets tests substitute a poisoned subkey path
    /// to force <c>OpenSubKey</c>/<c>CreateSubKey</c> to throw, exercising
    /// the logging branch without polluting any real registry location.
    /// </summary>
    internal AutoStartService(
        string valueName,
        string runKeyPath,
        ILogger<AutoStartService>? logger)
    {
        if (string.IsNullOrEmpty(valueName))
            throw new ArgumentException("Value name must be non-empty.", nameof(valueName));
        if (string.IsNullOrEmpty(runKeyPath))
            throw new ArgumentException("Run key path must be non-empty.", nameof(runKeyPath));
        _valueName  = valueName;
        _runKeyPath = runKeyPath;
        _log = logger ?? NullLogger<AutoStartService>.Instance;
    }

    /// <inheritdoc />
    public bool IsEnabled
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(_runKeyPath, writable: false);
                var value = key?.GetValue(_valueName) as string;
                return !string.IsNullOrEmpty(value);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex,
                    "Failed to read auto-start registry value '{ValueName}' under HKCU\\{Path}.",
                    _valueName, _runKeyPath);
                return false;
            }
        }
    }

    /// <inheritdoc />
    public bool SetEnabled(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(_runKeyPath, writable: true);
            if (key is null)
            {
                _log.LogWarning(
                    "Could not open or create HKCU\\{Path} for writing.", _runKeyPath);
                return false;
            }

            if (enabled)
            {
                var exe = ResolveExecutablePath();
                if (string.IsNullOrEmpty(exe))
                {
                    _log.LogWarning(
                        "Cannot enable auto-start: failed to resolve executable path.");
                    return false;
                }
                // Wrap in quotes so paths with spaces work.
                key.SetValue(_valueName, $"\"{exe}\"", RegistryValueKind.String);
            }
            else
            {
                if (key.GetValue(_valueName) is not null)
                {
                    key.DeleteValue(_valueName, throwOnMissingValue: false);
                }
            }

            return true;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex,
                "Failed to {Action} auto-start registry value '{ValueName}' under HKCU\\{Path}.",
                enabled ? "write" : "remove", _valueName, _runKeyPath);
            return false;
        }
    }

    private static string ResolveExecutablePath()
    {
        // Process.MainModule is the canonical way to find the running .exe
        // for unpackaged apps. Process.GetCurrentProcess() never throws.
        try
        {
            return Process.GetCurrentProcess().MainModule?.FileName ?? string.Empty;
        }
        catch
        {
            return Environment.ProcessPath ?? string.Empty;
        }
    }
}
