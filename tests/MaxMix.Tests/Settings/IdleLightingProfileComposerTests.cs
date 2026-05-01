namespace MaxMix.Tests.Settings;

using System;
using System.Collections.Generic;
using MaxMix.Core.Abstractions;
using MaxMix.Core.Models;
using MaxMix.Core.Settings;

public class IdleLightingProfileComposerTests
{
    private static AppSettings BuildSettings(
        string presetName,
        IdleLightingMode mode,
        byte speed = 5)
    {
        var preset = ColorPresets.GetByName(presetName) ?? ColorPresets.Aurora;
        return AppSettings.Defaults with
        {
            IdleLightingEnabled = true,
            IdleLightingPresetName = presetName,
            IdleLightingMode = mode,
            IdleLightingSpeed = speed,
            IdleLightingColorA = preset.VolumeMinColor,
            IdleLightingColorB = preset.VolumeMaxColor,
        };
    }

    [Fact]
    public void Compose_Static_returns_selected_idle_preset_exactly()
    {
        var settings = BuildSettings("Aurora", IdleLightingMode.Static);

        var profile = IdleLightingProfileComposer.Compose(
            settings,
            DisplayMode.Application,
            new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 1, 1, 0, 0, 1, DateTimeKind.Utc));

        Assert.Equal(ColorPresets.Aurora.VolumeMinColor, profile.VolumeMinColor);
        Assert.Equal(ColorPresets.Aurora.VolumeMaxColor, profile.VolumeMaxColor);
    }

    [Fact]
    public void Compose_Pulse_changes_brightness_over_time()
    {
        var settings = BuildSettings("Neon", IdleLightingMode.Pulse, speed: 10);
        var start = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        var dim = IdleLightingProfileComposer.Compose(settings, DisplayMode.Application, start, start);
        var bright = IdleLightingProfileComposer.Compose(settings, DisplayMode.Application, start, start.AddMilliseconds(500));

        Assert.NotEqual(dim.VolumeMinColor, bright.VolumeMinColor);
        Assert.True(bright.VolumeMinColor.R >= dim.VolumeMinColor.R);
    }

    [Fact]
    public void Compose_Fade_moves_between_active_and_idle_profiles()
    {
        var settings = BuildSettings("Toxic", IdleLightingMode.Fade, speed: 10);
        var start = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        var atStart = IdleLightingProfileComposer.Compose(settings, DisplayMode.Application, start, start);
        var atHalfWave = IdleLightingProfileComposer.Compose(settings, DisplayMode.Application, start, start.AddMilliseconds(500));

        Assert.Equal(ColorPresets.Toxic.VolumeMinColor, atStart.VolumeMinColor);
        Assert.Equal(ColorPresets.Toxic.VolumeMaxColor, atHalfWave.VolumeMinColor);
        Assert.Equal(atHalfWave.VolumeMinColor, atHalfWave.VolumeMaxColor);
    }
}
