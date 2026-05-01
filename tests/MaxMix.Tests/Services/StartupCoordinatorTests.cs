namespace MaxMix.Tests.Services;

using System;
using System.Threading;
using System.Threading.Tasks;
using MaxMix.App.Services;
using MaxMix.Core.Abstractions;
using MaxMix.Core.Models;
using MaxMix.Core.Settings;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

/// <summary>
/// Tests for <see cref="StartupCoordinator"/> — the testable extract of
/// <c>App.OnStartup</c> steps 5–8. Drives:
///   - applying the auto-start setting via <see cref="IAutoStartService"/>
///   - initialising the tray icon (always, regardless of StartMinimized)
///   - showing/activating the main window when <c>StartMinimized=false</c>
///   - NOT showing the window when <c>StartMinimized=true</c> (auto-start case)
///   - starting the device orchestrator
///
/// Spec coverage (sdd/start-with-windows/spec):
///   - "Application launches at Windows startup" — StartMinimized=true: no window, but tray + orchestrator.
///   - "Start with StartMinimized true" — tray init + no show + orchestrator start.
///   - "Start with StartMinimized false" — tray init + show + activate + orchestrator start.
///   - "Auto-start registry applied on startup" — SetEnabled(settings.AutoStartWithWindows) called.
/// </summary>
public class StartupCoordinatorTests
{
    private static AppSettings BuildSettings(bool startMinimized, bool autoStart)
    {
        return AppSettings.Defaults with
        {
            StartMinimized       = startMinimized,
            AutoStartWithWindows = autoStart,
        };
    }

    private sealed class FakeAppShell : IAppShell
    {
        public int InitializeTrayCalls;
        public int ShowMainWindowCalls;

        public void InitializeTray() => InitializeTrayCalls++;
        public void ShowMainWindow() => ShowMainWindowCalls++;
    }

    /// <summary>
    /// Tracks calls + lets a test trigger a synchronous throw on StartAsync.
    /// Wrapped around a permissive Moq so we don't have to stub every member of
    /// <see cref="IDeviceController"/>.
    /// </summary>
    private sealed class TrackingController
    {
        public int StartCalls;
        public bool ThrowOnStart;
        public IDeviceController AsInterface { get; }

        public TrackingController()
        {
            var mock = new Mock<IDeviceController>(MockBehavior.Loose);
            mock.Setup(c => c.StartAsync(It.IsAny<CancellationToken>()))
                .Returns(() =>
                {
                    StartCalls++;
                    if (ThrowOnStart) throw new InvalidOperationException("orchestrator start failed");
                    return Task.CompletedTask;
                });
            AsInterface = mock.Object;
        }
    }

    private static StartupCoordinator BuildCoordinator(
        out Mock<IAutoStartService> autoStart,
        out FakeAppShell shell,
        out TrackingController controller)
    {
        autoStart = new Mock<IAutoStartService>(MockBehavior.Strict);
        autoStart.Setup(s => s.SetEnabled(It.IsAny<bool>())).Returns(true);

        shell = new FakeAppShell();
        controller = new TrackingController();

        return new StartupCoordinator(
            autoStart: autoStart.Object,
            shell: shell,
            controller: controller.AsInterface,
            logger: NullLogger<StartupCoordinator>.Instance);
    }

    // ---- Spec scenario: Start with StartMinimized=true (gap 5 + gap 1) ----

    [Fact]
    public async Task RunStartupAsync_StartMinimizedTrue_InitsTray_DoesNotShowWindow_StartsOrchestrator()
    {
        var coord = BuildCoordinator(out var autoStart, out var shell, out var controller);
        var settings = BuildSettings(startMinimized: true, autoStart: false);

        await coord.RunStartupAsync(settings);

        Assert.Equal(1, shell.InitializeTrayCalls);
        Assert.Equal(0, shell.ShowMainWindowCalls);
        Assert.Equal(1, controller.StartCalls);
        autoStart.Verify(s => s.SetEnabled(false), Times.Once);
    }

    // ---- Spec scenario: Start with StartMinimized=false (gap 6) ----

    [Fact]
    public async Task RunStartupAsync_StartMinimizedFalse_InitsTray_ShowsAndActivatesWindow_StartsOrchestrator()
    {
        var coord = BuildCoordinator(out var autoStart, out var shell, out var controller);
        var settings = BuildSettings(startMinimized: false, autoStart: false);

        await coord.RunStartupAsync(settings);

        Assert.Equal(1, shell.InitializeTrayCalls);
        Assert.Equal(1, shell.ShowMainWindowCalls);
        Assert.Equal(1, controller.StartCalls);
        autoStart.Verify(s => s.SetEnabled(false), Times.Once);
    }

    // ---- Spec scenario: Auto-start registry applied on startup ----

    [Fact]
    public async Task RunStartupAsync_AutoStartTrue_CallsSetEnabledTrue()
    {
        var coord = BuildCoordinator(out var autoStart, out _, out _);
        var settings = BuildSettings(startMinimized: false, autoStart: true);

        await coord.RunStartupAsync(settings);

        autoStart.Verify(s => s.SetEnabled(true), Times.Once);
    }

    [Fact]
    public async Task RunStartupAsync_AutoStartFalse_CallsSetEnabledFalse()
    {
        var coord = BuildCoordinator(out var autoStart, out _, out _);
        var settings = BuildSettings(startMinimized: false, autoStart: false);

        await coord.RunStartupAsync(settings);

        autoStart.Verify(s => s.SetEnabled(false), Times.Once);
    }

    // ---- Resilience: registry failure does NOT block startup ----

    [Fact]
    public async Task RunStartupAsync_AutoStartSetEnabledThrows_StillInitsTrayAndStartsOrchestrator()
    {
        // Spec: "registry failure does not prevent the rest of startup".
        var autoStart = new Mock<IAutoStartService>(MockBehavior.Strict);
        autoStart.Setup(s => s.SetEnabled(It.IsAny<bool>()))
                 .Throws(new UnauthorizedAccessException("registry locked"));

        var shell = new FakeAppShell();
        var controller = new TrackingController();
        var coord = new StartupCoordinator(
            autoStart.Object, shell, controller.AsInterface, NullLogger<StartupCoordinator>.Instance);

        var settings = BuildSettings(startMinimized: false, autoStart: true);

        // Must not throw.
        await coord.RunStartupAsync(settings);

        Assert.Equal(1, shell.InitializeTrayCalls);
        Assert.Equal(1, shell.ShowMainWindowCalls);
        Assert.Equal(1, controller.StartCalls);
    }

    // ---- Spec scenario: Application launched at Windows startup (gap 1) ----
    // GIVEN auto-start registry entry exists AND StartMinimized=true
    // WHEN Windows starts and launches MaxMix
    // THEN MaxMix starts minimized to tray (no visible window)
    //  AND begins connecting to the device immediately

    [Fact]
    public async Task RunStartupAsync_LaunchedByWindowsAutoStart_StartsTrayOnly_AndConnectsImmediately()
    {
        // GIVEN: Both auto-start and start-minimized are on (Windows-launched scenario).
        var coord = BuildCoordinator(out _, out var shell, out var controller);
        var settings = BuildSettings(startMinimized: true, autoStart: true);

        // WHEN
        await coord.RunStartupAsync(settings);

        // THEN: tray initialised, NO window shown, device controller started.
        Assert.Equal(1, shell.InitializeTrayCalls);
        Assert.Equal(0, shell.ShowMainWindowCalls);
        Assert.Equal(1, controller.StartCalls);
    }

    // ---- Resilience: orchestrator start failure does not crash startup ----

    [Fact]
    public async Task RunStartupAsync_OrchestratorStartThrows_DoesNotPropagate()
    {
        var coord = BuildCoordinator(out _, out var shell, out var controller);
        controller.ThrowOnStart = true;
        var settings = BuildSettings(startMinimized: false, autoStart: false);

        // Must not throw — orchestrator failure is logged, app keeps running.
        await coord.RunStartupAsync(settings);

        Assert.Equal(1, shell.InitializeTrayCalls);
        Assert.Equal(1, shell.ShowMainWindowCalls);
        Assert.Equal(1, controller.StartCalls);
    }

    // ---- Argument validation ----

    [Fact]
    public void Ctor_NullAutoStart_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new StartupCoordinator(
            null!, new FakeAppShell(), new TrackingController().AsInterface,
            NullLogger<StartupCoordinator>.Instance));
    }

    [Fact]
    public void Ctor_NullShell_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new StartupCoordinator(
            new Mock<IAutoStartService>().Object, null!, new TrackingController().AsInterface,
            NullLogger<StartupCoordinator>.Instance));
    }

    [Fact]
    public void Ctor_NullController_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new StartupCoordinator(
            new Mock<IAutoStartService>().Object, new FakeAppShell(), null!,
            NullLogger<StartupCoordinator>.Instance));
    }

    [Fact]
    public async Task RunStartupAsync_NullSettings_Throws()
    {
        var coord = BuildCoordinator(out _, out _, out _);
        await Assert.ThrowsAsync<ArgumentNullException>(() => coord.RunStartupAsync(null!));
    }
}
