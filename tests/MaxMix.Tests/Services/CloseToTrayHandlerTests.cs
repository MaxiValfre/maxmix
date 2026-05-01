namespace MaxMix.Tests.Services;

using System;
using MaxMix.App.Services;
using Xunit;

/// <summary>
/// Tests for <see cref="CloseToTrayHandler"/> — the pure policy that drives
/// <c>MainWindow.OnClosing</c>. Extracted so the close-to-tray decision can
/// be exercised without spinning up a WPF dispatcher.
///
/// Spec coverage (sdd/start-with-windows/spec §Minimize to Tray):
///   - "Close with MinimizeToTray enabled" → cancel + hide, do NOT shutdown.
///   - "Close with MinimizeToTray disabled" → no cancel, no hide, shutdown.
///   - "Tray exit always shuts down" → ExitFromTray ignores MinimizeToTray.
/// </summary>
public class CloseToTrayHandlerTests
{
    private sealed class CallRecorder
    {
        public int CancelCalls;
        public int HideCalls;
        public int ShutdownCalls;
    }

    private static (CloseToTrayHandler handler, CallRecorder rec) BuildHandler()
    {
        var rec = new CallRecorder();
        var handler = new CloseToTrayHandler();
        return (handler, rec);
    }

    // ---- HandleClose: MinimizeToTray=true ----

    [Fact]
    public void HandleClose_WhenMinimizeToTrayTrue_CancelsAndHides_DoesNotShutdown()
    {
        var (handler, rec) = BuildHandler();

        handler.HandleClose(
            minimizeToTray: true,
            cancel:   () => rec.CancelCalls++,
            hide:     () => rec.HideCalls++,
            shutdown: () => rec.ShutdownCalls++);

        Assert.Equal(1, rec.CancelCalls);
        Assert.Equal(1, rec.HideCalls);
        Assert.Equal(0, rec.ShutdownCalls);
    }

    // ---- HandleClose: MinimizeToTray=false ----

    [Fact]
    public void HandleClose_WhenMinimizeToTrayFalse_ShutsDown_DoesNotCancelOrHide()
    {
        var (handler, rec) = BuildHandler();

        handler.HandleClose(
            minimizeToTray: false,
            cancel:   () => rec.CancelCalls++,
            hide:     () => rec.HideCalls++,
            shutdown: () => rec.ShutdownCalls++);

        Assert.Equal(0, rec.CancelCalls);
        Assert.Equal(0, rec.HideCalls);
        Assert.Equal(1, rec.ShutdownCalls);
    }

    // ---- HandleTrayExit: always shuts down ----

    [Fact]
    public void HandleTrayExit_AlwaysShutsDown_WhenMinimizeToTrayTrue()
    {
        // Spec scenario "Tray exit always shuts down":
        // GIVEN MinimizeToTray is either true or false
        // WHEN the user selects "Exit" from the tray context menu
        // THEN the application shuts down completely regardless of the setting.
        var (handler, rec) = BuildHandler();

        handler.HandleTrayExit(
            minimizeToTray: true,
            shutdown: () => rec.ShutdownCalls++);

        Assert.Equal(1, rec.ShutdownCalls);
    }

    [Fact]
    public void HandleTrayExit_AlwaysShutsDown_WhenMinimizeToTrayFalse()
    {
        // Triangulation: even with the setting OFF, tray exit still shuts down.
        var (handler, rec) = BuildHandler();

        handler.HandleTrayExit(
            minimizeToTray: false,
            shutdown: () => rec.ShutdownCalls++);

        Assert.Equal(1, rec.ShutdownCalls);
    }

    // ---- Argument validation ----

    [Fact]
    public void HandleClose_NullCancel_Throws()
    {
        var (handler, _) = BuildHandler();
        Assert.Throws<ArgumentNullException>(() =>
            handler.HandleClose(true, cancel: null!, hide: () => { }, shutdown: () => { }));
    }

    [Fact]
    public void HandleClose_NullHide_Throws()
    {
        var (handler, _) = BuildHandler();
        Assert.Throws<ArgumentNullException>(() =>
            handler.HandleClose(true, cancel: () => { }, hide: null!, shutdown: () => { }));
    }

    [Fact]
    public void HandleClose_NullShutdown_Throws()
    {
        var (handler, _) = BuildHandler();
        Assert.Throws<ArgumentNullException>(() =>
            handler.HandleClose(false, cancel: () => { }, hide: () => { }, shutdown: null!));
    }

    [Fact]
    public void HandleTrayExit_NullShutdown_Throws()
    {
        var (handler, _) = BuildHandler();
        Assert.Throws<ArgumentNullException>(() =>
            handler.HandleTrayExit(true, shutdown: null!));
    }
}
