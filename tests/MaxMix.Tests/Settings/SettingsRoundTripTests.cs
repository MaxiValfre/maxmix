namespace MaxMix.Tests.Settings;

using System;
using System.IO;
using System.Threading.Tasks;
using MaxMix.Core.Abstractions;
using MaxMix.Core.Settings;

/// <summary>
/// Round-trip tests for the auto-start / start-minimized / minimize-to-tray
/// trio (start-with-windows change, spec §Settings Persistence).
///
/// Spec scenarios covered:
///   - Round-trip of auto-start flags: save → load preserves all three
///     booleans exactly.
///   - Defaults applied on missing flags: a settings file from an older
///     schema missing <c>StartMinimized</c> migrates with the field defaulted
///     to <c>false</c> (no data loss for other fields).
/// </summary>
public class SettingsRoundTripTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly string _settingsPath;

    public SettingsRoundTripTests()
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
    public async Task RoundTrip_AutoStartFlags_PreserveValues()
    {
        // GIVEN settings with AutoStartWithWindows=true, StartMinimized=true,
        // MinimizeToTray=false (a non-default permutation that exercises every
        // flag).
        var writer = new JsonSettingsService(_settingsPath);
        await writer.LoadAsync();

        var saved = writer.Current with
        {
            AutoStartWithWindows = true,
            StartMinimized       = true,
            MinimizeToTray       = false,
        };

        // WHEN SaveAsync is called followed by LoadAsync (in a new instance, so
        // we read from disk, not from the in-memory cache).
        await writer.SaveAsync(saved);

        var reader = new JsonSettingsService(_settingsPath);
        await reader.LoadAsync();

        // THEN all three flags retain their saved values exactly.
        Assert.True(reader.Current.AutoStartWithWindows);
        Assert.True(reader.Current.StartMinimized);
        Assert.False(reader.Current.MinimizeToTray);
    }

    [Fact]
    public async Task RoundTrip_AutoStartFlags_PreserveOppositeValues()
    {
        // Triangulation: a different permutation must also round-trip exactly.
        // This proves the persistence is not just hardcoding `true` everywhere.
        var writer = new JsonSettingsService(_settingsPath);
        await writer.LoadAsync();

        var saved = writer.Current with
        {
            AutoStartWithWindows = false,
            StartMinimized       = false,
            MinimizeToTray       = true,
        };

        await writer.SaveAsync(saved);

        var reader = new JsonSettingsService(_settingsPath);
        await reader.LoadAsync();

        Assert.False(reader.Current.AutoStartWithWindows);
        Assert.False(reader.Current.StartMinimized);
        Assert.True(reader.Current.MinimizeToTray);
    }

    [Fact]
    public async Task Defaults_AppliedOnMissingFlags_StartMinimizedIsFalse()
    {
        // GIVEN a settings file from an older schema (v0) MISSING StartMinimized.
        // The other fields are present so we can verify "no data loss for other fields".
        Directory.CreateDirectory(_tempRoot);
        const string legacyJson = """
        {
          "SchemaVersion": 0,
          "StartupMode": 2,
          "AccelerationPercentage": 44,
          "ContinuousScroll": true,
          "SleepAfterSeconds": 5,
          "AutoStartWithWindows": false,
          "MinimizeToTray": true,
          "LastConnectedPort": "COM4",
          "ColorProfiles": {}
        }
        """;
        await File.WriteAllTextAsync(_settingsPath, legacyJson);

        var svc = new JsonSettingsService(_settingsPath);

        // WHEN LoadAsync runs the migration.
        await svc.LoadAsync();

        // THEN StartMinimized defaults to `false` (per spec §Defaults applied
        // on missing flags). It must NOT be silently set to `true` by the
        // migration — the new contract is that the omitted flag falls back to
        // AppSettings.Defaults.StartMinimized (= false).
        Assert.False(svc.Current.StartMinimized);

        // AND no data loss occurs for other fields.
        Assert.Equal(44,        svc.Current.AccelerationPercentage);
        Assert.Equal((byte)5,   svc.Current.SleepAfterSeconds);
        Assert.True(svc.Current.ContinuousScroll);
        Assert.False(svc.Current.AutoStartWithWindows);
        Assert.True(svc.Current.MinimizeToTray);
        Assert.Equal("COM4",    svc.Current.LastConnectedPort);

        // AND the migration bumped the schema version.
        Assert.Equal(AppSettings.CurrentSchemaVersion, svc.Current.SchemaVersion);
    }
}
