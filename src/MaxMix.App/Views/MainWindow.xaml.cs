namespace MaxMix.App.Views;

using System.ComponentModel;
using System.Windows;
using MaxMix.App.Services;
using MaxMix.App.ViewModels;
using MaxMix.Core.Abstractions;

/// <summary>
/// Main app window. Hosts the <see cref="ShellViewModel"/> as DataContext.
///
/// Close behaviour: the X button delegates to <see cref="CloseToTrayHandler"/>,
/// which encapsulates the policy: when <c>MinimizeToTray</c> is enabled the
/// window hides; when disabled, the close proceeds and the app shuts down.
/// The tray menu's Exit item is the only unconditional quit affordance.
/// </summary>
public partial class MainWindow : Window
{
    private readonly ISettingsService _settings;
    private readonly CloseToTrayHandler _closeHandler;

    public MainWindow(ShellViewModel viewModel, ISettingsService settings)
        : this(viewModel, settings, new CloseToTrayHandler())
    {
    }

    // Test seam: lets unit tests inject a custom handler if ever needed.
    internal MainWindow(
        ShellViewModel viewModel,
        ISettingsService settings,
        CloseToTrayHandler closeHandler)
    {
        _settings = settings;
        _closeHandler = closeHandler;
        InitializeComponent();
        DataContext = viewModel;
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        // Delegate the close decision to the pure policy. Side effects
        // (cancel, hide, shutdown) are wired here so the handler itself
        // stays trivial to test.
        _closeHandler.HandleClose(
            minimizeToTray: _settings.Current.MinimizeToTray,
            cancel:   () => e.Cancel = true,
            hide:     Hide,
            shutdown: () =>
            {
                base.OnClosing(e);
                Application.Current.Shutdown();
            });
    }

    private void OnSettingsClicked(object sender, RoutedEventArgs e)
    {
        // Defer to App-level navigation logic so we don't have to know about
        // the SettingsWindow lifetime here.
        ((App)Application.Current).ShowSettings();
    }
}
