namespace MaxMix.App.Services;

using System;
using System.Threading;
using System.Threading.Tasks;
using MaxMix.Core.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

/// <summary>
/// Testable extract of <c>App.OnStartup</c> steps 5–8: applies the auto-start
/// preference, initialises the tray, conditionally shows the main window, and
/// kicks off the device orchestrator.
///
/// The coordinator depends only on Core abstractions plus the WPF-facing
/// <see cref="IAppShell"/>, so unit tests can substitute a fake shell + mock
/// <see cref="IAutoStartService"/> + mock <see cref="IDeviceController"/>.
///
/// Spec coverage (sdd/start-with-windows/spec):
///   - "Application launches at Windows startup": tray + orchestrator, no window.
///   - "Start with StartMinimized true": tray init, NO window show, orchestrator start.
///   - "Start with StartMinimized false": tray init, window show + activate, orchestrator start.
///   - "Auto-start registry applied on startup": <see cref="IAutoStartService.SetEnabled"/>
///     called with <c>settings.AutoStartWithWindows</c>; failure is logged and swallowed.
/// </summary>
public sealed class StartupCoordinator
{
    private readonly IAutoStartService _autoStart;
    private readonly IAppShell _shell;
    private readonly IDeviceController _controller;
    private readonly ILogger<StartupCoordinator> _log;

    public StartupCoordinator(
        IAutoStartService autoStart,
        IAppShell shell,
        IDeviceController controller,
        ILogger<StartupCoordinator>? logger = null)
    {
        _autoStart  = autoStart  ?? throw new ArgumentNullException(nameof(autoStart));
        _shell      = shell      ?? throw new ArgumentNullException(nameof(shell));
        _controller = controller ?? throw new ArgumentNullException(nameof(controller));
        _log        = logger ?? NullLogger<StartupCoordinator>.Instance;
    }

    /// <summary>
    /// Run the post-settings-load portion of startup. Caller has already
    /// configured logging, opened the single-instance gate, built the DI
    /// container, and loaded settings.
    /// </summary>
    public async Task RunStartupAsync(
        AppSettings settings,
        CancellationToken cancellationToken = default)
    {
        if (settings is null) throw new ArgumentNullException(nameof(settings));

        // 1. Apply auto-start preference. Registry failure is best-effort:
        //    log + swallow so the rest of startup proceeds.
        try
        {
            _autoStart.SetEnabled(settings.AutoStartWithWindows);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex,
                "Auto-start SetEnabled({Enabled}) threw; continuing startup.",
                settings.AutoStartWithWindows);
        }

        // 2. Initialise the tray ALWAYS — even when StartMinimized=true the
        //    tray is the only UI affordance the user has.
        _shell.InitializeTray();

        // 3. Show the main window only when the user has NOT opted into a
        //    minimised start (auto-start launches typically pair with this).
        if (!settings.StartMinimized)
        {
            _shell.ShowMainWindow();
        }

        // 4. Kick off the device orchestrator. Failures are logged and do
        //    not propagate — the app should stay usable even if hardware
        //    isn't yet attached.
        try
        {
            await _controller.StartAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Device controller start failed during startup.");
        }
    }
}
