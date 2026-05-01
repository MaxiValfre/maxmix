namespace MaxMix.App.Services;

using System;
using System.Drawing;
using System.Windows;
using H.NotifyIcon;

/// <summary>
/// Owns the system-tray icon and its context menu. Wraps
/// <see cref="TaskbarIcon"/> so the rest of the App talks to a tiny stable
/// surface (Show / Settings / Exit handlers).
///
/// Lifecycle: built once at app startup, disposed at app exit. Safe to call
/// <see cref="Initialise"/> exactly once.
/// </summary>
public sealed class SystemTrayService : IDisposable
{
    private TaskbarIcon? _icon;
    private bool _disposed;

    /// <summary>
    /// Build the tray icon and its menu. Pass the three callbacks the menu
    /// items invoke; they run on whatever thread the click arrives on, which
    /// for H.NotifyIcon is the UI thread.
    /// </summary>
    public void Initialise(Action onShow, Action onSettings, Action onExit)
    {
        if (_icon is not null) return;

        var menu = new System.Windows.Controls.ContextMenu();

        var showItem     = new System.Windows.Controls.MenuItem { Header = "Show MaxMix" };
        var settingsItem = new System.Windows.Controls.MenuItem { Header = "Settings…" };
        var separator    = new System.Windows.Controls.Separator();
        var exitItem     = new System.Windows.Controls.MenuItem { Header = "Exit" };

        showItem.Click     += (_, _) => onShow();
        settingsItem.Click += (_, _) => onSettings();
        exitItem.Click     += (_, _) => onExit();

        menu.Items.Add(showItem);
        menu.Items.Add(settingsItem);
        menu.Items.Add(separator);
        menu.Items.Add(exitItem);

        _icon = new TaskbarIcon
        {
            ToolTipText = "MaxMix — disconnected",
            ContextMenu = menu,
            Visibility  = Visibility.Visible,
        };

        // Single click on the tray icon opens the main window. (H.NotifyIcon
        // routes left-clicks through LeftClickCommand.)
        _icon.LeftClickCommand = new RelayCommand(_ => onShow());
        _icon.NoLeftClickDelay = true;

        var iconHandle = LoadDefaultIcon();
        if (iconHandle is not null)
            _icon.Icon = iconHandle;
    }

    /// <summary>
    /// Update the tooltip — useful for surfacing connection status without
    /// opening the window.
    /// </summary>
    public void SetTooltip(string text)
    {
        if (_icon is not null) _icon.ToolTipText = text;
    }

    /// <summary>
    /// Try to load the running .exe's associated icon. Falls back to the
    /// system "application" icon if extraction fails.
    /// </summary>
    private static Icon? LoadDefaultIcon()
    {
        try
        {
            var path = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName
                       ?? Environment.ProcessPath;
            if (!string.IsNullOrEmpty(path))
            {
                var icon = Icon.ExtractAssociatedIcon(path);
                if (icon is not null) return icon;
            }
        }
        catch { /* fall through */ }

        return SystemIcons.Application;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _icon?.Dispose();
        _icon = null;
    }
}

/// <summary>Tiny <see cref="System.Windows.Input.ICommand"/> for tray clicks.</summary>
file sealed class RelayCommand : System.Windows.Input.ICommand
{
    private readonly Action<object?> _execute;
    public RelayCommand(Action<object?> execute) => _execute = execute;

    public bool CanExecute(object? parameter) => true;
    public void Execute(object? parameter) => _execute(parameter);
    public event EventHandler? CanExecuteChanged
    {
        add    { /* never changes */ }
        remove { /* never changes */ }
    }
}
