namespace MaxMix.Tests.Settings;

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using MaxMix.Core.Abstractions;
using MaxMix.Core.Models;
using MaxMix.Core.Settings;

/// <summary>
/// Tests for JsonSettingsService. Uses an isolated temp directory per test to
/// avoid polluting %LOCALAPPDATA%.
///
/// Spec coverage:
///  - 5.1 Settings file location & format (JSON, schema version, sensible defaults).
///  - 5.2 First launch creates defaults if file missing.
///  - 5.3 Corrupted JSON → backup .bak + new defaults.
///  - 5.4 Round-trip save/load preserves all fields.
///  - 5.5 Schema migration v0 → v1.
///  - SettingsChanged event fires on Save.
/// </summary>
public class JsonSettingsServiceTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly string _settingsPath;

    public JsonSettingsServiceTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "MaxMixTests-" + Guid.NewGuid().ToString("N"));
        _settingsPath = Path.Combine(_tempRoot, "settings.json");
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempRoot))
        {
            try { Directory.Delete(_tempRoot, recursive: true); } catch { /* best effort */ }
        }
    }

    [Fact]
    public async Task Load_FirstLaunch_CreatesDirectoryAndReturnsDefaults()
    {
        Assert.False(Directory.Exists(_tempRoot));
        var svc = new JsonSettingsService(_settingsPath);

        await svc.LoadAsync();

        Assert.Equal(AppSettings.Defaults.SchemaVersion, svc.Current.SchemaVersion);
        Assert.Equal(AppSettings.Defaults.StartupMode,   svc.Current.StartupMode);
        Assert.True(Directory.Exists(_tempRoot));
    }

    [Fact]
    public async Task SaveAsync_PersistsToDisk_AsPrettyJson()
    {
        var svc = new JsonSettingsService(_settingsPath);
        await svc.LoadAsync();

        var customised = svc.Current with
        {
            AccelerationPercentage = 80,
            SleepAfterSeconds = 17,
            LastConnectedPort = "COM7",
            IdleLightingEnabled = true,
            IdleLightingAfterSeconds = 45,
            IdleLightingPresetName = "Aurora",
            IdleLightingColorA = new RgbColor(1, 2, 3),
            IdleLightingColorB = new RgbColor(4, 5, 6),
        };
        await svc.SaveAsync(customised);

        Assert.True(File.Exists(_settingsPath));
        var text = await File.ReadAllTextAsync(_settingsPath);
        // Pretty-printed JSON must contain at least one newline.
        Assert.Contains("\n", text);
        Assert.Contains("\"AccelerationPercentage\"", text);
        Assert.Contains("80", text);
        Assert.Contains("COM7", text);
        Assert.Contains("IdleLightingEnabled", text);
        Assert.Contains("Aurora", text);
    }

    [Fact]
    public async Task RoundTrip_PreservesAllFields_IncludingPerModeColorProfiles()
    {
        // Save with a unique-per-mode profile palette so we can verify each mode
        // restored its own colors (not a single shared default).
        var profiles = new Dictionary<DisplayMode, ColorProfile>
        {
            [DisplayMode.Output]      = new ColorProfile(new(1,2,3),     new(4,5,6),     new(7,8,9),     new(10,11,12)),
            [DisplayMode.Input]       = new ColorProfile(new(13,14,15),  new(16,17,18),  new(19,20,21),  new(22,23,24)),
            [DisplayMode.Application] = new ColorProfile(new(25,26,27),  new(28,29,30),  new(31,32,33),  new(34,35,36)),
            [DisplayMode.Game]        = new ColorProfile(new(37,38,39),  new(40,41,42),  new(43,44,45),  new(46,47,48)),
        };

        var saved = AppSettings.Defaults with
        {
            AccelerationPercentage = 42,
            SleepAfterSeconds = 9,
            ContinuousScroll = false,
            LastConnectedPort = "COM3",
            StartupMode = DisplayMode.Application,
            IdleLightingEnabled = true,
            IdleLightingAfterSeconds = 25,
            IdleLightingPresetName = "Toxic",
            IdleLightingColorA = new RgbColor(101, 102, 103),
            IdleLightingColorB = new RgbColor(104, 105, 106),
            ColorProfiles = profiles,
        };

        var writer = new JsonSettingsService(_settingsPath);
        await writer.LoadAsync();
        await writer.SaveAsync(saved);

        var reader = new JsonSettingsService(_settingsPath);
        await reader.LoadAsync();

        Assert.Equal(42,                             reader.Current.AccelerationPercentage);
        Assert.Equal((byte)9,                        reader.Current.SleepAfterSeconds);
        Assert.False(reader.Current.ContinuousScroll);
        Assert.Equal("COM3",                         reader.Current.LastConnectedPort);
        Assert.Equal(DisplayMode.Application,        reader.Current.StartupMode);
        Assert.True(reader.Current.IdleLightingEnabled);
        Assert.Equal((byte)25,                       reader.Current.IdleLightingAfterSeconds);
        Assert.Equal("Toxic",                       reader.Current.IdleLightingPresetName);
        Assert.Equal(new RgbColor(101, 102, 103),   reader.Current.IdleLightingColorA);
        Assert.Equal(new RgbColor(104, 105, 106),   reader.Current.IdleLightingColorB);

        Assert.Equal(profiles[DisplayMode.Output].VolumeMinColor,
                     reader.Current.ColorProfiles[DisplayMode.Output].VolumeMinColor);
        Assert.Equal(profiles[DisplayMode.Game].MixChannelBColor,
                     reader.Current.ColorProfiles[DisplayMode.Game].MixChannelBColor);
        Assert.Equal(profiles[DisplayMode.Input].VolumeMaxColor,
                     reader.Current.ColorProfiles[DisplayMode.Input].VolumeMaxColor);
    }

    [Fact]
    public async Task Load_CorruptedJson_BacksUpAndUsesDefaults()
    {
        Directory.CreateDirectory(_tempRoot);
        await File.WriteAllTextAsync(_settingsPath, "{ this is not valid JSON ]]");

        var svc = new JsonSettingsService(_settingsPath);
        await svc.LoadAsync();

        // Defaults applied in memory.
        Assert.Equal(AppSettings.Defaults.SchemaVersion, svc.Current.SchemaVersion);
        // Original corrupted file must be backed up to *.bak.
        Assert.True(File.Exists(_settingsPath + ".bak"));
        // A fresh defaults file must now exist on disk.
        Assert.True(File.Exists(_settingsPath));
        var fresh = await File.ReadAllTextAsync(_settingsPath);
        Assert.Contains("\"SchemaVersion\"", fresh);
    }

    [Fact]
    public async Task Load_OlderSchemaVersion_AppliesMigration_AndPersists()
    {
        // Simulate a v0 file: missing StartMinimized + idle-lighting fields.
        Directory.CreateDirectory(_tempRoot);
        const string v0Json = """
        {
          "SchemaVersion": 0,
          "StartupMode": 1,
          "AccelerationPercentage": 33,
          "ContinuousScroll": true,
          "SleepAfterSeconds": 7,
          "AutoStartWithWindows": false,
          "MinimizeToTray": true,
          "LastConnectedPort": "COM9",
          "ColorProfiles": {}
        }
        """;
        await File.WriteAllTextAsync(_settingsPath, v0Json);

        var svc = new JsonSettingsService(_settingsPath);
        await svc.LoadAsync();

        // Migration must bump the version and PRESERVE existing values.
        Assert.Equal(AppSettings.CurrentSchemaVersion, svc.Current.SchemaVersion);
        Assert.Equal(33,        svc.Current.AccelerationPercentage);
        Assert.Equal((byte)7,   svc.Current.SleepAfterSeconds);
        Assert.Equal("COM9",    svc.Current.LastConnectedPort);
        // New fields get their defaults. Per the start-with-windows spec
        // (§Defaults applied on missing flags), StartMinimized defaults to
        // `false` when missing from a legacy file — NOT `true`.
        Assert.False(svc.Current.StartMinimized);
        Assert.False(svc.Current.IdleLightingEnabled);
        Assert.Equal((byte)30, svc.Current.IdleLightingAfterSeconds);
        Assert.Equal("Aurora", svc.Current.IdleLightingPresetName);
        Assert.Equal(ColorPresets.Aurora.VolumeMinColor, svc.Current.IdleLightingColorA);
        Assert.Equal(ColorPresets.Aurora.VolumeMaxColor, svc.Current.IdleLightingColorB);

        // File on disk must now reflect the migration (version bumped).
        var migrated = await File.ReadAllTextAsync(_settingsPath);
        Assert.Contains($"\"SchemaVersion\": {AppSettings.CurrentSchemaVersion}", migrated);
    }

    [Fact]
    public async Task SaveAsync_RaisesSettingsChangedEvent()
    {
        var svc = new JsonSettingsService(_settingsPath);
        await svc.LoadAsync();

        AppSettings? observed = null;
        svc.SettingsChanged += (_, s) => observed = s;

        var next = svc.Current with { AccelerationPercentage = 99 };
        await svc.SaveAsync(next);

        Assert.NotNull(observed);
        Assert.Equal(99, observed!.AccelerationPercentage);
    }

    [Fact]
    public async Task Current_ReflectsLatestSavedValue()
    {
        var svc = new JsonSettingsService(_settingsPath);
        await svc.LoadAsync();

        var next = svc.Current with { SleepAfterSeconds = 22 };
        await svc.SaveAsync(next);

        Assert.Equal((byte)22, svc.Current.SleepAfterSeconds);
    }
}
