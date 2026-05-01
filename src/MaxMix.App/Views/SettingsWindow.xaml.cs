namespace MaxMix.App.Views;

using System.ComponentModel;
using System.Windows;
using MaxMix.App.ViewModels;

/// <summary>
/// Modal-ish settings dialog. Closing it just hides; the VM lives only as
/// long as the window so opening it again resets edits to saved values.
/// </summary>
public partial class SettingsWindow : Window
{
    public SettingsWindow(SettingsViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        // We hide instead of closing so DI re-resolution isn't needed each time.
        // Actually — we DO want a fresh VM next time so unsaved edits don't
        // linger. Let the close happen normally; the SettingsWindow registration
        // is Transient.
        base.OnClosing(e);
    }
}
