namespace MaxMix.App;

using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using MaxMix.App.Composition;
using MaxMix.App.Services;
using MaxMix.App.Views;
using MaxMix.Core.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Events;

/// <summary>
/// WPF entry point. Owns the DI container, the single-instance gate, the
/// system-tray icon, and the shutdown sequence.
///
/// Lifecycle:
///   1. <see cref="OnStartup"/> — configure Serilog, wire crash handlers,
///      single-instance check, build container, resolve <see cref="MainWindow"/>,
///      delegate the post-DI portion to <see cref="StartupCoordinator"/>.
///   2. <see cref="OnExit"/> — stop the orchestrator, dispose tray, dispose
///      the container, flush Serilog.
///
/// Window-close behaviour: the X button on <see cref="MainWindow"/> hides
/// the window. The app keeps running in the tray until the user picks
/// "Exit" from the tray menu (or calls <see cref="ShutdownApp"/>).
///
/// Implements <see cref="IAppShell"/> so <see cref="StartupCoordinator"/> can
/// drive tray initialisation and main-window visibility without needing
/// direct knowledge of WPF.
/// </summary>
public partial class App : Application, IAppShell
{
    private ServiceProvider? _services;
    private SingleInstanceService? _singleInstance;
    private SystemTrayService? _tray;
    private MainWindow? _mainWindow;
    private ILogger<App>? _log;
    private readonly CloseToTrayHandler _closeHandler = new();

    public new static App Current => (App)Application.Current;

    public IServiceProvider Services =>
        _services ?? throw new InvalidOperationException("Service provider not initialised.");

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // 0. Configure Serilog FIRST so any subsequent failure is logged.
        ConfigureSerilog();

        // Global crash handlers — anything that escapes a try/catch on a
        // background thread or in WPF dispatcher dispatch is at least logged.
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandled;
        TaskScheduler.UnobservedTaskException += OnUnobservedTask;
        DispatcherUnhandledException += OnDispatcherUnhandled;

        // 1. Single-instance gate.
        _singleInstance = new SingleInstanceService();
        if (!_singleInstance.TryAcquire(SingleInstanceService.MutexName))
        {
            _singleInstance.NotifyExistingInstance();
            Shutdown(0);
            return;
        }

        // 2. Build the DI container (ILoggerFactory is wired inside).
        _services = ServiceRegistration.Build();
        _log = _services.GetRequiredService<ILogger<App>>();
        _log.LogInformation("MaxMix starting up.");

        // 3. Cross-instance window-show signal.
        _singleInstance.SecondInstanceLaunched += (_, _) =>
            Dispatcher.Invoke(ShowMainWindow);

        // 4. Load settings — non-fatal failure.
        var settings = _services.GetRequiredService<ISettingsService>();
        try
        {
            await settings.LoadAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Settings load failed; using defaults.");
        }

        // 5. Resolve the main window + tray service up-front so InitializeTray
        //    (called by StartupCoordinator) has them available.
        _mainWindow = _services.GetRequiredService<MainWindow>();
        MainWindow = _mainWindow;
        _tray = _services.GetRequiredService<SystemTrayService>();

        // 6. Delegate post-DI startup (auto-start apply, tray init,
        //    conditional window-show, orchestrator start) to the coordinator.
        //    Behaviour is unit-tested in StartupCoordinatorTests.
        var coordinator = new StartupCoordinator(
            autoStart: _services.GetRequiredService<IAutoStartService>(),
            shell:     this,
            controller: _services.GetRequiredService<IDeviceController>(),
            logger:    _services.GetService<ILogger<StartupCoordinator>>());

        // Fire-and-forget — the coordinator awaits the orchestrator start
        // internally and swallows non-fatal errors. We don't await here so
        // OnStartup returns promptly and the WPF dispatcher can pump messages.
        _ = coordinator.RunStartupAsync(settings.Current);
    }

    // ----------------------- IAppShell implementation -----------------------
    // These two methods make App testable through StartupCoordinator without
    // exposing WPF internals to the coordinator.

    /// <inheritdoc />
    void IAppShell.InitializeTray()
    {
        if (_tray is null) return;
        _tray.Initialise(
            onShow: ShowMainWindow,
            onSettings: ShowSettings,
            onExit: ShutdownApp);
    }

    /// <inheritdoc />
    void IAppShell.ShowMainWindow() => ShowMainWindow();

    public void ShowMainWindow()
    {
        if (_mainWindow is null) return;
        if (!_mainWindow.IsVisible) _mainWindow.Show();
        if (_mainWindow.WindowState == WindowState.Minimized)
            _mainWindow.WindowState = WindowState.Normal;
        _mainWindow.Activate();
        _mainWindow.Topmost = true;
        _mainWindow.Topmost = false;
        _mainWindow.Focus();
    }

    public void ShowSettings()
    {
        if (_services is null) return;
        var settingsWindow = _services.GetRequiredService<SettingsWindow>();
        if (!settingsWindow.IsVisible)
        {
            settingsWindow.Owner = _mainWindow;
            settingsWindow.Show();
        }
        settingsWindow.Activate();
    }

    /// <summary>
    /// User-initiated shutdown (tray menu Exit). Stops the orchestrator and
    /// disposes everything cleanly before returning control to WPF.
    ///
    /// Routes through <see cref="CloseToTrayHandler.HandleTrayExit"/> so the
    /// "tray Exit always shuts down" contract (spec §Minimize to Tray) is
    /// enforced by the same policy class that <see cref="MainWindow"/> uses.
    /// </summary>
    public void ShutdownApp()
    {
        var minimizeToTray =
            _services?.GetService<ISettingsService>()?.Current.MinimizeToTray ?? true;
        _closeHandler.HandleTrayExit(minimizeToTray, () => Shutdown(0));
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        try
        {
            _log?.LogInformation("MaxMix shutting down.");
            if (_services is not null)
            {
                var controller = _services.GetService<IDeviceController>();
                if (controller is not null)
                {
                    try { await controller.StopAsync().ConfigureAwait(false); }
                    catch (Exception ex) { _log?.LogError(ex, "Orchestrator stop failed."); }
                }
            }

            _tray?.Dispose();
            _services?.Dispose();
            _singleInstance?.Dispose();
        }
        catch (Exception ex)
        {
            _log?.LogError(ex, "Error during shutdown.");
        }
        finally
        {
            try { Log.CloseAndFlush(); } catch { /* swallow */ }
        }

        base.OnExit(e);
    }

    /// <summary>
    /// Configure Serilog with the spec's required sinks: rolling daily file at
    /// <c>%LOCALAPPDATA%/MaxMix/logs/maxmix-.log</c> + console (Information+).
    /// File sink captures Debug-and-up so traces are available during
    /// post-mortem.
    /// </summary>
    private static void ConfigureSerilog()
    {
        var logDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MaxMix",
            "logs");

        try { Directory.CreateDirectory(logDir); } catch { /* best effort */ }

        var logFile = Path.Combine(logDir, "maxmix-.log");

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .Enrich.FromLogContext()
            .WriteTo.Console(restrictedToMinimumLevel: LogEventLevel.Information)
            .WriteTo.File(
                path: logFile,
                rollingInterval: RollingInterval.Day,
                restrictedToMinimumLevel: LogEventLevel.Debug,
                retainedFileCountLimit: 14,
                shared: false,
                outputTemplate:
                    "[{Timestamp:yyyy-MM-dd HH:mm:ss.fff} {Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}")
            .CreateLogger();
    }

    // ---- Crash handlers ----

    private void OnDomainUnhandled(object sender, UnhandledExceptionEventArgs e)
    {
        var ex = e.ExceptionObject as Exception;
        Log.Fatal(ex, "Unhandled exception escaped — terminating={IsTerminating}.", e.IsTerminating);
        try
        {
            // Best-effort user-facing message before the process dies.
            MessageBox.Show(
                $"MaxMix encountered an unrecoverable error and will close.\n\n{ex?.Message}",
                "MaxMix",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        catch { /* on terminating threads, MessageBox may itself throw */ }
        try { Log.CloseAndFlush(); } catch { }
    }

    private void OnUnobservedTask(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        Log.Error(e.Exception, "Unobserved task exception.");
        e.SetObserved(); // don't tear the process down for this
    }

    private void OnDispatcherUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error(e.Exception, "Unhandled dispatcher exception.");
        // Show a non-fatal dialog and continue. If the exception is genuinely
        // fatal it will surface again.
        try
        {
            MessageBox.Show(
                $"MaxMix hit an error in the UI:\n\n{e.Exception.Message}\n\nThe app will keep running.",
                "MaxMix",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
        catch { /* best effort */ }
        e.Handled = true;
    }
}
