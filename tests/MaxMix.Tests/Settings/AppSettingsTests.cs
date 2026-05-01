namespace MaxMix.Tests.Settings;

using MaxMix.Core.Abstractions;
using MaxMix.Core.Models;
using MaxMix.Core.Settings;

/// <summary>
/// Tests for AppSettings.Defaults — the factory used on first launch and on
/// corrupted-file recovery. Spec section 5: "First launch creates defaults".
/// </summary>
public class AppSettingsTests
{
    [Fact]
    public void Defaults_HasCurrentSchemaVersion()
    {
        Assert.Equal(AppSettings.CurrentSchemaVersion, AppSettings.Defaults.SchemaVersion);
    }

    [Fact]
    public void Defaults_StartupModeIsApplication()
    {
        // After Batch 2: desktop only drives Application mode.
        Assert.Equal(DisplayMode.Application, AppSettings.Defaults.StartupMode);
    }

    [Fact]
    public void Defaults_ContainsProfileForEveryPlayableMode()
    {
        var profiles = AppSettings.Defaults.ColorProfiles;

        Assert.True(profiles.ContainsKey(DisplayMode.Output));
        Assert.True(profiles.ContainsKey(DisplayMode.Input));
        Assert.True(profiles.ContainsKey(DisplayMode.Application));
        Assert.True(profiles.ContainsKey(DisplayMode.Game));
    }

    [Fact]
    public void Defaults_ConservativeStartupBehavior()
    {
        // First launch should NOT auto-start with Windows nor enable surprising features.
        // StartMinimized defaults to false so the window is visible on first launch.
        Assert.False(AppSettings.Defaults.AutoStartWithWindows);
        Assert.True(AppSettings.Defaults.MinimizeToTray);
        Assert.False(AppSettings.Defaults.StartMinimized);
        Assert.True(AppSettings.Defaults.ContinuousScroll);
    }

    [Fact]
    public void Defaults_IdleLighting_is_disabled_but_preconfigured()
    {
        Assert.False(AppSettings.Defaults.IdleLightingEnabled);
        Assert.Equal((byte)30, AppSettings.Defaults.IdleLightingAfterSeconds);
        Assert.Equal("Aurora", AppSettings.Defaults.IdleLightingPresetName);
        Assert.Equal(IdleLightingMode.Static, AppSettings.Defaults.IdleLightingMode);
        Assert.Equal((byte)5, AppSettings.Defaults.IdleLightingSpeed);
        Assert.Equal(new RgbColor(20, 220, 120), AppSettings.Defaults.IdleLightingColorA);
        Assert.Equal(new RgbColor(40, 200, 255), AppSettings.Defaults.IdleLightingColorB);
    }

    [Fact]
    public void Defaults_NoLastConnectedPort()
    {
        Assert.Null(AppSettings.Defaults.LastConnectedPort);
    }
}
