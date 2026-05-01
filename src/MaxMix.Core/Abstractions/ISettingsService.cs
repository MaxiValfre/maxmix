namespace MaxMix.Core.Abstractions;

using MaxMix.Core.Models;

/// <summary>
/// Loads/saves user preferences as JSON at %LOCALAPPDATA%\MaxMix\settings.json.
/// Exposes the in-memory current settings and a change event. See spec section 5.
/// </summary>
public interface ISettingsService
{
    AppSettings Current { get; }
    event EventHandler<AppSettings>? SettingsChanged;

    Task LoadAsync(CancellationToken cancellationToken = default);
    Task SaveAsync(AppSettings next, CancellationToken cancellationToken = default);
}

/// <summary>
/// Top-level user-visible settings. SchemaVersion is the migration hook.
/// Per-mode color profiles are stored as a dictionary keyed by mode.
///
/// `StartMinimized` is a UX preference distinct from `MinimizeToTray`:
///   - `StartMinimized`: window starts hidden (or auto-start launches hidden)
///   - `MinimizeToTray`: clicking X minimises to tray instead of exiting
///
/// Idle lighting is a desktop-side alternative to firmware sleep:
///   - `IdleLightingEnabled`: when true, the desktop swaps the ring to a named
///     preset after a period of inactivity instead of letting firmware sleep
///   - `IdleLightingAfterSeconds`: inactivity timeout before the swap
///   - `IdleLightingPresetName`: built-in preset used to seed custom idle colors
/// </summary>
public sealed record AppSettings(
    int SchemaVersion,
    DisplayMode StartupMode,
    byte AccelerationPercentage,
    bool ContinuousScroll,
    byte SleepAfterSeconds,
    bool AutoStartWithWindows,
    bool StartMinimized,
    bool MinimizeToTray,
    string? LastConnectedPort,
    IReadOnlyDictionary<DisplayMode, ColorProfile> ColorProfiles)
{
    public bool IdleLightingEnabled { get; init; }
    public byte IdleLightingAfterSeconds { get; init; } = 30;
    public string? IdleLightingPresetName { get; init; } = "Aurora";
    public IdleLightingMode IdleLightingMode { get; init; } = IdleLightingMode.Static;
    public byte IdleLightingSpeed { get; init; } = 5;
    public RgbColor IdleLightingColorA { get; init; } = new(20, 220, 120);
    public RgbColor IdleLightingColorB { get; init; } = new(40, 200, 255);

    /// <summary>
    /// Conservative defaults applied on first launch and on corrupted-file
    /// recovery. Spec section 5: "First launch creates defaults".
    /// </summary>
    public static AppSettings Defaults { get; } = new(
        SchemaVersion:          CurrentSchemaVersion,
        StartupMode:            DisplayMode.Application,
        AccelerationPercentage: 50,
        ContinuousScroll:       true,
        SleepAfterSeconds:      0,
        AutoStartWithWindows:   false,
        StartMinimized:         false,
        MinimizeToTray:         true,
        LastConnectedPort:      null,
        ColorProfiles: new Dictionary<DisplayMode, ColorProfile>
        {
            [DisplayMode.Output]      = MaxMix.Core.Settings.ColorPresets.Default,
            [DisplayMode.Input]       = MaxMix.Core.Settings.ColorPresets.Default,
            [DisplayMode.Application] = MaxMix.Core.Settings.ColorPresets.Default,
            [DisplayMode.Game]        = MaxMix.Core.Settings.ColorPresets.Neon,
        })
    {
        IdleLightingEnabled = false,
        IdleLightingAfterSeconds = 30,
        IdleLightingPresetName = "Aurora",
        IdleLightingMode = IdleLightingMode.Static,
        IdleLightingSpeed = 5,
        IdleLightingColorA = new RgbColor(20, 220, 120),
        IdleLightingColorB = new RgbColor(40, 200, 255),
    };

    /// <summary>
    /// Current schema version. Bump and add a migration in JsonSettingsService
    /// whenever the wire format changes.
    /// </summary>
    public const int CurrentSchemaVersion = 4;
}

/// <summary>
/// The 4 RGB fields the firmware understands, named after their SETTINGS slots.
/// </summary>
public readonly record struct ColorProfile(
    RgbColor VolumeMinColor,
    RgbColor VolumeMaxColor,
    RgbColor MixChannelAColor,
    RgbColor MixChannelBColor);
