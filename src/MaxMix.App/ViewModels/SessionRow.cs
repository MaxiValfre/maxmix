namespace MaxMix.App.ViewModels;

using CommunityToolkit.Mvvm.ComponentModel;

/// <summary>
/// Row VM for a single audio session as shown in the main window's list.
/// Pure data — no command logic; the surrounding ShellViewModel owns
/// navigation/cycling.
/// </summary>
public sealed partial class SessionRow : ObservableObject
{
    [ObservableProperty] private byte _id;
    [ObservableProperty] private string _name = string.Empty;
    [ObservableProperty] private byte _volume;
    [ObservableProperty] private bool _isMuted;
    [ObservableProperty] private bool _isCurrent;
}
