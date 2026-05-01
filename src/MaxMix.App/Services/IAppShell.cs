namespace MaxMix.App.Services;

/// <summary>
/// Thin abstraction over WPF-specific UI side effects that the
/// <see cref="StartupCoordinator"/> needs to drive: tray initialisation and
/// "show + activate" of the main window. Lets the coordinator be unit-tested
/// with a fake implementation that records the calls instead of touching the
/// real WPF dispatcher.
///
/// Implemented by <see cref="App"/> at runtime — the methods route to
/// <see cref="App.ShowMainWindow"/> and the resolved <see cref="SystemTrayService"/>.
/// </summary>
public interface IAppShell
{
    /// <summary>
    /// Initialise the system-tray icon and wire its menu callbacks. MUST be
    /// called regardless of <c>StartMinimized</c> — the tray is the only way
    /// to bring the app back to the foreground when the window is hidden.
    /// </summary>
    void InitializeTray();

    /// <summary>
    /// Show and activate the main window. Called only when
    /// <c>StartMinimized</c> is <c>false</c>.
    /// </summary>
    void ShowMainWindow();
}
