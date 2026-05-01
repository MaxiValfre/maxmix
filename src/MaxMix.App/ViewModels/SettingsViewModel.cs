namespace MaxMix.App.ViewModels;

using System;
using System.Collections.ObjectModel;
using System.IO.Ports;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MaxMix.Core.Abstractions;
using MaxMix.Core.Models;
using MaxMix.Core.Settings;

/// <summary>
/// ViewModel for the settings window. Exposes every <see cref="AppSettings"/>
/// field as a bindable property plus per-mode color profile editing and
/// preset selection.
///
/// "Current" snapshot is the in-memory edit buffer; <see cref="SaveCommand"/>
/// pushes it through <see cref="ISettingsService.SaveAsync"/>.
/// </summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly ISettingsService _settings;
    private readonly IAutoStartService _autoStart;

    public SettingsViewModel(ISettingsService settings, IAutoStartService autoStart)
    {
        _settings  = settings  ?? throw new ArgumentNullException(nameof(settings));
        _autoStart = autoStart ?? throw new ArgumentNullException(nameof(autoStart));

        AvailablePorts = new ObservableCollection<string>(SerialPort.GetPortNames());
        AvailablePresets = new ObservableCollection<string>(ColorPresets.All.Keys);
        IdleLightingModes = new ObservableCollection<IdleLightingMode>(
            Enum.GetValues<IdleLightingMode>());
        // After the Application-only mode simplification (Batch 2), the UI no
        // longer exposes Output / Input / Game as selectable modes. The
        // collection is kept for XAML binding compatibility but contains a
        // single fixed entry.
        Modes = new ObservableCollection<DisplayMode>(new[] { DisplayMode.Application });

        LoadFromSettings(settings.Current);
    }

    [ObservableProperty] private bool _autoStartWithWindows;
    [ObservableProperty] private bool _startMinimized;
    [ObservableProperty] private bool _minimizeToTray;
    [ObservableProperty] private bool _continuousScroll;
    [ObservableProperty] private byte _accelerationPercentage;
    [ObservableProperty] private byte _sleepAfterSeconds;
    [ObservableProperty] private bool _idleLightingEnabled;
    [ObservableProperty] private byte _idleLightingAfterSeconds;
    [ObservableProperty] private byte _idleLightingSpeed;
    [ObservableProperty] private byte _idleColorAR;
    [ObservableProperty] private byte _idleColorAG;
    [ObservableProperty] private byte _idleColorAB;
    [ObservableProperty] private byte _idleColorBR;
    [ObservableProperty] private byte _idleColorBG;
    [ObservableProperty] private byte _idleColorBB;
    [ObservableProperty] private string? _selectedPort;
    [ObservableProperty] private string? _selectedPreset;
    [ObservableProperty] private string? _selectedIdlePreset;
    [ObservableProperty] private IdleLightingMode _selectedIdleLightingMode;
    [ObservableProperty] private string _statusMessage = string.Empty;

    private DisplayMode _selectedMode = DisplayMode.Application;

    /// <summary>
    /// Currently selected display mode. After the Application-only mode
    /// simplification the UI exposes only Application; legacy bindings,
    /// persisted v0/v1 settings with <c>StartupMode = Output</c>, or stale
    /// XAML two-way bindings might still try to assign Output / Input / Game /
    /// Splash — all of those must silently snap back to Application so the
    /// rest of the VM (color profile, Save) reasons over a single, valid
    /// value. The setter coerces every non-Application write back to
    /// Application BEFORE storing or raising the change notification, so the
    /// public property never momentarily holds an invalid value.
    /// </summary>
    public DisplayMode SelectedMode
    {
        get => _selectedMode;
        // Coerce any non-Application write back to Application. The UI no
        // longer offers other choices and all settings paths assume
        // SelectedMode == Application. SetProperty raises the same
        // PropertyChanging/PropertyChanged notifications the [ObservableProperty]
        // generator would have produced.
        set => SetProperty(ref _selectedMode,
            value == DisplayMode.Application ? value : DisplayMode.Application);
    }

    [ObservableProperty] private byte _volumeMinR;
    [ObservableProperty] private byte _volumeMinG;
    [ObservableProperty] private byte _volumeMinB;
    [ObservableProperty] private byte _volumeMaxR;
    [ObservableProperty] private byte _volumeMaxG;
    [ObservableProperty] private byte _volumeMaxB;
    [ObservableProperty] private byte _mixAR;
    [ObservableProperty] private byte _mixAG;
    [ObservableProperty] private byte _mixAB;
    [ObservableProperty] private byte _mixBR;
    [ObservableProperty] private byte _mixBG;
    [ObservableProperty] private byte _mixBB;

    public string VolumeMinHex => ToHex(VolumeMinR, VolumeMinG, VolumeMinB);
    public string VolumeMaxHex => ToHex(VolumeMaxR, VolumeMaxG, VolumeMaxB);
    public string IdleColorAHex => ToHex(IdleColorAR, IdleColorAG, IdleColorAB);
    public string IdleColorBHex => ToHex(IdleColorBR, IdleColorBG, IdleColorBB);

    public ObservableCollection<string> AvailablePorts    { get; }
    public ObservableCollection<string> AvailablePresets  { get; }
    public ObservableCollection<IdleLightingMode> IdleLightingModes { get; }
    public ObservableCollection<DisplayMode> Modes        { get; }

    [RelayCommand]
    private void RefreshPorts()
    {
        AvailablePorts.Clear();
        foreach (var p in SerialPort.GetPortNames()) AvailablePorts.Add(p);
    }

    [RelayCommand]
    private void ApplyPreset()
    {
        if (string.IsNullOrEmpty(SelectedPreset)) return;
        var profile = ColorPresets.GetByName(SelectedPreset);
        if (profile is not { } p) return;
        VolumeMinR = p.VolumeMinColor.R; VolumeMinG = p.VolumeMinColor.G; VolumeMinB = p.VolumeMinColor.B;
        VolumeMaxR = p.VolumeMaxColor.R; VolumeMaxG = p.VolumeMaxColor.G; VolumeMaxB = p.VolumeMaxColor.B;
        MixAR = p.MixChannelAColor.R; MixAG = p.MixChannelAColor.G; MixAB = p.MixChannelAColor.B;
        MixBR = p.MixChannelBColor.R; MixBG = p.MixChannelBColor.G; MixBB = p.MixChannelBColor.B;
    }

    [RelayCommand]
    private void ApplyIdlePreset()
    {
        if (string.IsNullOrEmpty(SelectedIdlePreset)) return;
        var profile = ColorPresets.GetByName(SelectedIdlePreset);
        if (profile is not { } p) return;
        IdleColorAR = p.VolumeMinColor.R; IdleColorAG = p.VolumeMinColor.G; IdleColorAB = p.VolumeMinColor.B;
        IdleColorBR = p.VolumeMaxColor.R; IdleColorBG = p.VolumeMaxColor.G; IdleColorBB = p.VolumeMaxColor.B;
    }

    [RelayCommand]
    private async Task Save()
    {
        var profile = new ColorProfile(
            VolumeMinColor:   new RgbColor(VolumeMinR, VolumeMinG, VolumeMinB),
            VolumeMaxColor:   new RgbColor(VolumeMaxR, VolumeMaxG, VolumeMaxB),
            MixChannelAColor: new RgbColor(MixAR, MixAG, MixAB),
            MixChannelBColor: new RgbColor(MixBR, MixBG, MixBB));

        var profiles = _settings.Current.ColorProfiles.ToDictionary(kv => kv.Key, kv => kv.Value);
        profiles[SelectedMode] = profile;

        var next = _settings.Current with
        {
            AutoStartWithWindows   = AutoStartWithWindows,
            StartMinimized         = StartMinimized,
            MinimizeToTray         = MinimizeToTray,
            ContinuousScroll       = ContinuousScroll,
            AccelerationPercentage = AccelerationPercentage,
            SleepAfterSeconds      = SleepAfterSeconds,
            IdleLightingEnabled    = IdleLightingEnabled,
            IdleLightingAfterSeconds = IdleLightingAfterSeconds,
            IdleLightingMode       = SelectedIdleLightingMode,
            IdleLightingSpeed      = IdleLightingSpeed,
            IdleLightingPresetName = SelectedIdlePreset,
            IdleLightingColorA     = new RgbColor(IdleColorAR, IdleColorAG, IdleColorAB),
            IdleLightingColorB     = new RgbColor(IdleColorBR, IdleColorBG, IdleColorBB),
            LastConnectedPort      = SelectedPort,
            ColorProfiles          = profiles,
        };

        try
        {
            await _settings.SaveAsync(next).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            StatusMessage = $"Save failed: {ex.Message}";
            return;
        }

        // Auto-start is a best-effort side effect — registry writes can fail
        // (HKCU policy denial, locked hive). The settings already persisted;
        // surface the partial failure in StatusMessage instead of throwing.
        try
        {
            _autoStart.SetEnabled(AutoStartWithWindows);
            StatusMessage = "Settings saved.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Settings saved, but auto-start update failed: {ex.Message}";
        }
    }

    [RelayCommand]
    private void Reset()
    {
        LoadFromSettings(_settings.Current);
        StatusMessage = "Reverted to saved values.";
    }

    private void LoadFromSettings(AppSettings s)
    {
        AutoStartWithWindows   = s.AutoStartWithWindows;
        StartMinimized         = s.StartMinimized;
        MinimizeToTray         = s.MinimizeToTray;
        ContinuousScroll       = s.ContinuousScroll;
        AccelerationPercentage = s.AccelerationPercentage;
        SleepAfterSeconds      = s.SleepAfterSeconds;
        IdleLightingEnabled    = s.IdleLightingEnabled;
        IdleLightingAfterSeconds = s.IdleLightingAfterSeconds;
        IdleLightingSpeed      = s.IdleLightingSpeed;
        SelectedIdleLightingMode = s.IdleLightingMode;
        SelectedIdlePreset     = string.IsNullOrWhiteSpace(s.IdleLightingPresetName)
            ? "Aurora"
            : s.IdleLightingPresetName;
        IdleColorAR           = s.IdleLightingColorA.R;
        IdleColorAG           = s.IdleLightingColorA.G;
        IdleColorAB           = s.IdleLightingColorA.B;
        IdleColorBR           = s.IdleLightingColorB.R;
        IdleColorBG           = s.IdleLightingColorB.G;
        IdleColorBB           = s.IdleLightingColorB.B;
        SelectedPort           = s.LastConnectedPort;
        // Coerce any legacy persisted StartupMode (Output/Input/Game) to
        // Application — the UI no longer offers other choices.
        SelectedMode           = DisplayMode.Application;

        var profile = s.ColorProfiles.TryGetValue(SelectedMode, out var p) ? p : ColorPresets.Default;
        VolumeMinR = profile.VolumeMinColor.R; VolumeMinG = profile.VolumeMinColor.G; VolumeMinB = profile.VolumeMinColor.B;
        VolumeMaxR = profile.VolumeMaxColor.R; VolumeMaxG = profile.VolumeMaxColor.G; VolumeMaxB = profile.VolumeMaxColor.B;
        MixAR = profile.MixChannelAColor.R; MixAG = profile.MixChannelAColor.G; MixAB = profile.MixChannelAColor.B;
        MixBR = profile.MixChannelBColor.R; MixBG = profile.MixChannelBColor.G; MixBB = profile.MixChannelBColor.B;
    }

    partial void OnVolumeMinRChanged(byte value) => OnPropertyChanged(nameof(VolumeMinHex));
    partial void OnVolumeMinGChanged(byte value) => OnPropertyChanged(nameof(VolumeMinHex));
    partial void OnVolumeMinBChanged(byte value) => OnPropertyChanged(nameof(VolumeMinHex));
    partial void OnVolumeMaxRChanged(byte value) => OnPropertyChanged(nameof(VolumeMaxHex));
    partial void OnVolumeMaxGChanged(byte value) => OnPropertyChanged(nameof(VolumeMaxHex));
    partial void OnVolumeMaxBChanged(byte value) => OnPropertyChanged(nameof(VolumeMaxHex));
    partial void OnIdleColorARChanged(byte value) => OnPropertyChanged(nameof(IdleColorAHex));
    partial void OnIdleColorAGChanged(byte value) => OnPropertyChanged(nameof(IdleColorAHex));
    partial void OnIdleColorABChanged(byte value) => OnPropertyChanged(nameof(IdleColorAHex));
    partial void OnIdleColorBRChanged(byte value) => OnPropertyChanged(nameof(IdleColorBHex));
    partial void OnIdleColorBGChanged(byte value) => OnPropertyChanged(nameof(IdleColorBHex));
    partial void OnIdleColorBBChanged(byte value) => OnPropertyChanged(nameof(IdleColorBHex));

    private static string ToHex(byte r, byte g, byte b) => $"#{r:X2}{g:X2}{b:X2}";
}
