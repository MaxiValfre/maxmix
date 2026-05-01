namespace MaxMix.Tests.Settings;

using System.Collections.Generic;
using MaxMix.Core.Abstractions;
using MaxMix.Core.Models;
using MaxMix.Core.Settings;

/// <summary>
/// Tests for DeviceSettingsMapper — the pure function that picks the active mode's
/// ColorProfile from AppSettings and produces the 14-byte DeviceSettings payload.
///
/// Spec coverage:
///  - Section 4 "RGB Control": per-mode profiles, R/G/B not transposed (Bug #4).
///  - Section 5 "Settings Persistence": acceleration / continuousScroll / sleep mapping.
/// </summary>
public class DeviceSettingsMapperTests
{
    private static AppSettings BuildSettings(
        DisplayMode startup = DisplayMode.Output,
        IReadOnlyDictionary<DisplayMode, ColorProfile>? profiles = null,
        byte sleep = 5,
        byte accel = 60,
        bool continuous = true,
        bool idleLightingEnabled = false,
        string? idlePresetName = null)
    {
        var idlePreset = ColorPresets.GetByName(idlePresetName ?? string.Empty) ?? ColorPresets.Aurora;

        return new AppSettings(
            SchemaVersion: 1,
            StartupMode: startup,
            AccelerationPercentage: accel,
            ContinuousScroll: continuous,
            SleepAfterSeconds: sleep,
            AutoStartWithWindows: false,
            StartMinimized: true,
            MinimizeToTray: true,
            LastConnectedPort: null,
            ColorProfiles: profiles ?? new Dictionary<DisplayMode, ColorProfile>())
        {
            IdleLightingEnabled = idleLightingEnabled,
            IdleLightingAfterSeconds = 30,
            IdleLightingPresetName = idlePresetName,
            IdleLightingColorA = idlePreset.VolumeMinColor,
            IdleLightingColorB = idlePreset.VolumeMaxColor,
        };
    }

    [Fact]
    public void Map_PicksProfileForCurrentMode_Output()
    {
        var outputProfile = new ColorProfile(
            VolumeMinColor:   new RgbColor(10,  20,  30),
            VolumeMaxColor:   new RgbColor(40,  50,  60),
            MixChannelAColor: new RgbColor(70,  80,  90),
            MixChannelBColor: new RgbColor(100, 110, 120));

        var inputProfile = new ColorProfile(
            VolumeMinColor:   new RgbColor(1, 1, 1),
            VolumeMaxColor:   new RgbColor(2, 2, 2),
            MixChannelAColor: new RgbColor(3, 3, 3),
            MixChannelBColor: new RgbColor(4, 4, 4));

        var settings = BuildSettings(profiles: new Dictionary<DisplayMode, ColorProfile>
        {
            [DisplayMode.Output] = outputProfile,
            [DisplayMode.Input]  = inputProfile,
        });

        var mapped = DeviceSettingsMapper.Map(settings, DisplayMode.Output);

        Assert.Equal(outputProfile.VolumeMinColor,   mapped.VolumeMinColor);
        Assert.Equal(outputProfile.VolumeMaxColor,   mapped.VolumeMaxColor);
        Assert.Equal(outputProfile.MixChannelAColor, mapped.MixChannelAColor);
        Assert.Equal(outputProfile.MixChannelBColor, mapped.MixChannelBColor);
    }

    [Fact]
    public void Map_PicksProfileForCurrentMode_Application()
    {
        var output = new ColorProfile(
            new RgbColor(99, 99, 99), new RgbColor(99, 99, 99),
            new RgbColor(99, 99, 99), new RgbColor(99, 99, 99));
        var application = new ColorProfile(
            VolumeMinColor:   new RgbColor(11, 22, 33),
            VolumeMaxColor:   new RgbColor(44, 55, 66),
            MixChannelAColor: new RgbColor(77, 88, 99),
            MixChannelBColor: new RgbColor(100, 110, 120));

        var settings = BuildSettings(profiles: new Dictionary<DisplayMode, ColorProfile>
        {
            [DisplayMode.Output]      = output,
            [DisplayMode.Application] = application,
        });

        var mapped = DeviceSettingsMapper.Map(settings, DisplayMode.Application);

        // Verifies we picked Application, NOT Output.
        Assert.Equal(application.VolumeMinColor,   mapped.VolumeMinColor);
        Assert.Equal(application.MixChannelBColor, mapped.MixChannelBColor);
        Assert.NotEqual(output.VolumeMinColor,     mapped.VolumeMinColor);
    }

    [Fact]
    public void Map_FallsBackToDefaults_WhenModeMissing()
    {
        var settings = BuildSettings(profiles: new Dictionary<DisplayMode, ColorProfile>());

        var mapped = DeviceSettingsMapper.Map(settings, DisplayMode.Game);

        // Default profile from ColorPresets.Default — used when the mode isn't configured.
        var fallback = ColorPresets.Default;
        Assert.Equal(fallback.VolumeMinColor,   mapped.VolumeMinColor);
        Assert.Equal(fallback.VolumeMaxColor,   mapped.VolumeMaxColor);
        Assert.Equal(fallback.MixChannelAColor, mapped.MixChannelAColor);
        Assert.Equal(fallback.MixChannelBColor, mapped.MixChannelBColor);
    }

    [Fact]
    public void Map_CarriesSleepAccelAndContinuousScroll()
    {
        var settings = BuildSettings(sleep: 42, accel: 75, continuous: false);

        var mapped = DeviceSettingsMapper.Map(settings, DisplayMode.Output);

        Assert.Equal((byte)42, mapped.SleepAfterSeconds);
        Assert.Equal((byte)75, mapped.AccelerationPercentage);
        Assert.False(mapped.ContinuousScroll);
    }

    [Fact]
    public void Map_DisablesFirmwareSleep_WhenIdleLightingIsEnabled()
    {
        var settings = BuildSettings(sleep: 42, idleLightingEnabled: true);

        var mapped = DeviceSettingsMapper.Map(settings, DisplayMode.Application);

        Assert.Equal((byte)0, mapped.SleepAfterSeconds);
    }

    [Fact]
    public void Map_UsesOverrideProfile_WhenIdlePresetIsRequested()
    {
        var appProfile = new ColorProfile(
            new RgbColor(1, 2, 3),
            new RgbColor(4, 5, 6),
            new RgbColor(7, 8, 9),
            new RgbColor(10, 11, 12));
        var idleProfile = new ColorProfile(
            new RgbColor(101, 102, 103),
            new RgbColor(104, 105, 106),
            new RgbColor(107, 108, 109),
            new RgbColor(110, 111, 112));

        var settings = BuildSettings(
            startup: DisplayMode.Application,
            profiles: new Dictionary<DisplayMode, ColorProfile>
            {
                [DisplayMode.Application] = appProfile,
            },
            idleLightingEnabled: true,
            idlePresetName: "Aurora");

        var mapped = DeviceSettingsMapper.Map(settings, DisplayMode.Application, idleProfile);

        Assert.Equal(idleProfile.VolumeMinColor, mapped.VolumeMinColor);
        Assert.Equal(idleProfile.VolumeMaxColor, mapped.VolumeMaxColor);
        Assert.NotEqual(appProfile.VolumeMinColor, mapped.VolumeMinColor);
    }

    /// <summary>
    /// BUG #4 REGRESSION: the original software set `b = g`, swapping the blue
    /// channel with green for some color fields. The mapper must preserve R/G/B
    /// in their original positions for every one of the 4 RGB fields.
    /// </summary>
    [Fact]
    public void Map_DoesNotSwapRgbChannels_Bug4()
    {
        // Asymmetric values per channel so any transposition is detectable.
        var profile = new ColorProfile(
            VolumeMinColor:   new RgbColor(0xAA, 0xBB, 0xCC),
            VolumeMaxColor:   new RgbColor(0x11, 0x22, 0x33),
            MixChannelAColor: new RgbColor(0x44, 0x55, 0x66),
            MixChannelBColor: new RgbColor(0x77, 0x88, 0x99));

        var settings = BuildSettings(profiles: new Dictionary<DisplayMode, ColorProfile>
        {
            [DisplayMode.Output] = profile,
        });

        var mapped = DeviceSettingsMapper.Map(settings, DisplayMode.Output);

        Assert.Equal((byte)0xAA, mapped.VolumeMinColor.R);
        Assert.Equal((byte)0xBB, mapped.VolumeMinColor.G);
        Assert.Equal((byte)0xCC, mapped.VolumeMinColor.B);

        Assert.Equal((byte)0x11, mapped.VolumeMaxColor.R);
        Assert.Equal((byte)0x22, mapped.VolumeMaxColor.G);
        Assert.Equal((byte)0x33, mapped.VolumeMaxColor.B);

        Assert.Equal((byte)0x44, mapped.MixChannelAColor.R);
        Assert.Equal((byte)0x55, mapped.MixChannelAColor.G);
        Assert.Equal((byte)0x66, mapped.MixChannelAColor.B);

        Assert.Equal((byte)0x77, mapped.MixChannelBColor.R);
        Assert.Equal((byte)0x88, mapped.MixChannelBColor.G);
        Assert.Equal((byte)0x99, mapped.MixChannelBColor.B);
    }
}
