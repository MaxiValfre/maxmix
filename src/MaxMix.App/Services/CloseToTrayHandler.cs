namespace MaxMix.App.Services;

using System;

/// <summary>
/// Pure policy class that decides what to do when the user closes the main
/// window or picks "Exit" from the tray menu. Extracted from
/// <see cref="MaxMix.App.Views.MainWindow.OnClosing"/> and
/// <see cref="App.ShutdownApp"/> so the close-to-tray policy can be exercised
/// by unit tests without spinning up a WPF dispatcher.
///
/// Spec coverage (sdd/start-with-windows/spec §Minimize to Tray):
///   - <see cref="HandleClose"/>: when MinimizeToTray is <c>true</c>, cancel
///     the close event and hide the window; when <c>false</c>, let the close
///     proceed and shut the application down.
///   - <see cref="HandleTrayExit"/>: ALWAYS shuts down regardless of the
///     setting — the tray "Exit" item is the only true quit affordance.
///
/// Side-effecting work (registering the cancel, hiding the WPF window, calling
/// <see cref="System.Windows.Application.Shutdown()"/>) is delegated to
/// <see cref="Action"/> callbacks the caller supplies so the policy itself
/// stays pure and trivially testable.
/// </summary>
public sealed class CloseToTrayHandler
{
    /// <summary>
    /// Apply the close-to-tray decision when the user clicks the X button.
    /// </summary>
    /// <param name="minimizeToTray">
    /// Current value of <c>AppSettings.MinimizeToTray</c>. When <c>true</c>,
    /// the close becomes "hide to tray"; when <c>false</c>, the close is
    /// allowed to proceed and the app shuts down.
    /// </param>
    /// <param name="cancel">
    /// Invoked to cancel the close event (sets <c>e.Cancel = true</c> in WPF).
    /// Only called when <paramref name="minimizeToTray"/> is <c>true</c>.
    /// </param>
    /// <param name="hide">
    /// Invoked to hide the window. Only called when
    /// <paramref name="minimizeToTray"/> is <c>true</c>.
    /// </param>
    /// <param name="shutdown">
    /// Invoked to shut the application down. Only called when
    /// <paramref name="minimizeToTray"/> is <c>false</c>.
    /// </param>
    public void HandleClose(
        bool minimizeToTray,
        Action cancel,
        Action hide,
        Action shutdown)
    {
        if (cancel   is null) throw new ArgumentNullException(nameof(cancel));
        if (hide     is null) throw new ArgumentNullException(nameof(hide));
        if (shutdown is null) throw new ArgumentNullException(nameof(shutdown));

        if (minimizeToTray)
        {
            cancel();
            hide();
            return;
        }

        shutdown();
    }

    /// <summary>
    /// Tray "Exit" handler. Always shuts the application down regardless of
    /// <paramref name="minimizeToTray"/> — the parameter is accepted for
    /// symmetry with <see cref="HandleClose"/> and to make the
    /// "ignore the setting" contract explicit at call sites.
    /// </summary>
    public void HandleTrayExit(bool minimizeToTray, Action shutdown)
    {
        if (shutdown is null) throw new ArgumentNullException(nameof(shutdown));
        // Intentionally ignores `minimizeToTray` — tray Exit is the unconditional quit.
        _ = minimizeToTray;
        shutdown();
    }
}
