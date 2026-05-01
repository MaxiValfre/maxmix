namespace MaxMix.Tests.Settings;

using MaxMix.Core.Abstractions;
using MaxMix.Core.Models;
using MaxMix.Core.Settings;

/// <summary>
/// Tests for the built-in named color presets.
/// Spec: Section 4 "Preset Color Schemes" — Ocean, Sunset, Mono, etc.
/// </summary>
public class ColorPresetsTests
{
    [Fact]
    public void Default_HasGreenToRedGradient()
    {
        var p = ColorPresets.Default;

        // Green = (0,255,0) → Red = (255,0,0). Distinct R/G/B per spec scenario.
        Assert.Equal(new RgbColor(0, 255, 0),   p.VolumeMinColor);
        Assert.Equal(new RgbColor(255, 0, 0),   p.VolumeMaxColor);
        Assert.Equal(new RgbColor(0, 0, 255),   p.MixChannelAColor);
        Assert.Equal(new RgbColor(255, 165, 0), p.MixChannelBColor);
    }

    [Fact]
    public void Ocean_HasCyanToBlueGradient()
    {
        var p = ColorPresets.Ocean;

        // Cyan family → blue family. Min must differ from Max.
        Assert.NotEqual(p.VolumeMinColor, p.VolumeMaxColor);
        // Cyan is high G+B, low R.
        Assert.True(p.VolumeMinColor.B > p.VolumeMinColor.R);
        Assert.True(p.VolumeMinColor.G > p.VolumeMinColor.R);
        // Blue Max: B should be the dominant channel.
        Assert.True(p.VolumeMaxColor.B > p.VolumeMaxColor.R);
        Assert.True(p.VolumeMaxColor.B > p.VolumeMaxColor.G);
    }

    [Fact]
    public void Sunset_HasYellowToPurpleGradient()
    {
        var p = ColorPresets.Sunset;

        // Yellow = high R + high G + low B.
        Assert.True(p.VolumeMinColor.R > 200);
        Assert.True(p.VolumeMinColor.G > 100);
        Assert.True(p.VolumeMinColor.B < 100);

        // Purple = high R + low G + high B.
        Assert.True(p.VolumeMaxColor.R > 100);
        Assert.True(p.VolumeMaxColor.B > 100);
        Assert.True(p.VolumeMaxColor.G < p.VolumeMaxColor.R);
        Assert.True(p.VolumeMaxColor.G < p.VolumeMaxColor.B);
    }

    [Fact]
    public void Monochrome_HasOnlyGrayValues()
    {
        var p = ColorPresets.Monochrome;

        // Every color must satisfy R == G == B (pure gray).
        Assert.Equal(p.VolumeMinColor.R, p.VolumeMinColor.G);
        Assert.Equal(p.VolumeMinColor.G, p.VolumeMinColor.B);

        Assert.Equal(p.VolumeMaxColor.R, p.VolumeMaxColor.G);
        Assert.Equal(p.VolumeMaxColor.G, p.VolumeMaxColor.B);

        // Min should be dimmer than Max.
        Assert.True(p.VolumeMinColor.R < p.VolumeMaxColor.R);
    }

    [Fact]
    public void Fire_GoesFromYellowToRed()
    {
        var p = ColorPresets.Fire;

        // Yellow start: high R+G, low B.
        Assert.True(p.VolumeMinColor.R > 200);
        Assert.True(p.VolumeMinColor.G > 100);
        Assert.True(p.VolumeMinColor.B < 100);

        // Red end: R dominates, G and B near zero.
        Assert.True(p.VolumeMaxColor.R > 200);
        Assert.True(p.VolumeMaxColor.G < 100);
        Assert.True(p.VolumeMaxColor.B < 100);
    }

    [Fact]
    public void Ice_GoesFromLightBlueToDarkBlue()
    {
        var p = ColorPresets.Ice;

        // Both ends are blue-dominant.
        Assert.True(p.VolumeMinColor.B >= p.VolumeMinColor.R);
        Assert.True(p.VolumeMaxColor.B >= p.VolumeMaxColor.R);
        // Min is brighter than Max overall (light → dark).
        int minSum = p.VolumeMinColor.R + p.VolumeMinColor.G + p.VolumeMinColor.B;
        int maxSum = p.VolumeMaxColor.R + p.VolumeMaxColor.G + p.VolumeMaxColor.B;
        Assert.True(minSum > maxSum);
    }

    [Fact]
    public void Neon_GoesFromMagentaToCyan()
    {
        var p = ColorPresets.Neon;

        // Magenta = high R + low G + high B.
        Assert.True(p.VolumeMinColor.R > 200);
        Assert.True(p.VolumeMinColor.G < 100);
        Assert.True(p.VolumeMinColor.B > 200);

        // Cyan = low R + high G + high B.
        Assert.True(p.VolumeMaxColor.R < 100);
        Assert.True(p.VolumeMaxColor.G > 200);
        Assert.True(p.VolumeMaxColor.B > 200);
    }

    [Theory]
    [InlineData("Default")]
    [InlineData("Ocean")]
    [InlineData("Sunset")]
    [InlineData("Monochrome")]
    [InlineData("Fire")]
    [InlineData("Ice")]
    [InlineData("Neon")]
    public void GetByName_ReturnsExpectedPreset(string name)
    {
        var preset = ColorPresets.GetByName(name);

        Assert.NotNull(preset);
        // VolumeMin and VolumeMax differ for every gradient preset.
        Assert.NotEqual(preset!.Value.VolumeMinColor, preset.Value.VolumeMaxColor);
    }

    [Fact]
    public void GetByName_IsCaseInsensitive()
    {
        Assert.Equal(ColorPresets.Ocean, ColorPresets.GetByName("ocean"));
        Assert.Equal(ColorPresets.Ocean, ColorPresets.GetByName("OCEAN"));
        Assert.Equal(ColorPresets.Ocean, ColorPresets.GetByName("Ocean"));
    }

    [Fact]
    public void GetByName_ReturnsNull_ForUnknown()
    {
        Assert.Null(ColorPresets.GetByName("DoesNotExist"));
    }

    [Fact]
    public void All_ContainsEveryNamedPreset()
    {
        var names = ColorPresets.All.Keys;
        Assert.Contains("Default",    names);
        Assert.Contains("Ocean",      names);
        Assert.Contains("Sunset",     names);
        Assert.Contains("Monochrome", names);
        Assert.Contains("Fire",       names);
        Assert.Contains("Ice",        names);
        Assert.Contains("Neon",       names);
        Assert.Contains("Aurora",     names);
        Assert.Contains("Toxic",      names);
        Assert.Equal(9, ColorPresets.All.Count);
    }
}
