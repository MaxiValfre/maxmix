namespace MaxMix.Core.Settings;

using System;
using System.Collections.Generic;
using MaxMix.Core.Abstractions;
using MaxMix.Core.Models;

/// <summary>
/// Built-in named color presets. Each preset populates all four RGB fields of a
/// <see cref="ColorProfile"/>:
///   - VolumeMinColor   → low-volume LED colour
///   - VolumeMaxColor   → high-volume LED colour
///   - MixChannelAColor → mix-mode left/A channel
///   - MixChannelBColor → mix-mode right/B channel
///
/// Spec: section 4 "Preset Color Schemes" — the system SHOULD offer named
/// presets and MAY allow user-created custom ones. These are the named ones.
/// </summary>
public static class ColorPresets
{
    /// <summary>Green → red gradient. The firmware's historical default.</summary>
    public static ColorProfile Default { get; } = new(
        VolumeMinColor:   new RgbColor(  0, 255,   0),  // green
        VolumeMaxColor:   new RgbColor(255,   0,   0),  // red
        MixChannelAColor: new RgbColor(  0,   0, 255),  // blue
        MixChannelBColor: new RgbColor(255, 165,   0)); // orange

    /// <summary>Cyan → blue gradient.</summary>
    public static ColorProfile Ocean { get; } = new(
        VolumeMinColor:   new RgbColor(  0, 200, 220),  // cyan
        VolumeMaxColor:   new RgbColor(  0,  40, 200),  // blue
        MixChannelAColor: new RgbColor( 30, 144, 255),  // dodger blue
        MixChannelBColor: new RgbColor(  0, 105, 148)); // deep sea blue

    /// <summary>Yellow → purple gradient.</summary>
    public static ColorProfile Sunset { get; } = new(
        VolumeMinColor:   new RgbColor(255, 200,  40),  // yellow
        VolumeMaxColor:   new RgbColor(140,  20, 200),  // purple
        MixChannelAColor: new RgbColor(255, 100,  20),  // orange
        MixChannelBColor: new RgbColor(200,  40, 140)); // magenta-ish

    /// <summary>Dim white → bright white. Pure grays only (R == G == B).</summary>
    public static ColorProfile Monochrome { get; } = new(
        VolumeMinColor:   new RgbColor( 40,  40,  40),  // dim white
        VolumeMaxColor:   new RgbColor(255, 255, 255),  // bright white
        MixChannelAColor: new RgbColor(120, 120, 120),  // mid gray A
        MixChannelBColor: new RgbColor(180, 180, 180)); // mid gray B

    /// <summary>Yellow → red. Hot palette.</summary>
    public static ColorProfile Fire { get; } = new(
        VolumeMinColor:   new RgbColor(255, 200,  20),  // yellow
        VolumeMaxColor:   new RgbColor(220,  20,  20),  // red
        MixChannelAColor: new RgbColor(255, 120,   0),  // orange
        MixChannelBColor: new RgbColor(180,  40,   0)); // burnt red

    /// <summary>Light blue → dark blue. Cold palette.</summary>
    public static ColorProfile Ice { get; } = new(
        VolumeMinColor:   new RgbColor(180, 220, 255),  // light blue
        VolumeMaxColor:   new RgbColor( 10,  30, 120),  // dark blue
        MixChannelAColor: new RgbColor(120, 180, 240),  // mid light blue
        MixChannelBColor: new RgbColor( 40,  90, 180)); // mid dark blue

    /// <summary>Magenta → cyan. High-contrast neon palette.</summary>
    public static ColorProfile Neon { get; } = new(
        VolumeMinColor:   new RgbColor(255,  20, 220),  // magenta
        VolumeMaxColor:   new RgbColor( 20, 240, 240),  // cyan
        MixChannelAColor: new RgbColor(180,   0, 255),  // electric purple
        MixChannelBColor: new RgbColor(  0, 255, 140)); // mint green

    /// <summary>Green → cyan with cooler auxiliary colors.</summary>
    public static ColorProfile Aurora { get; } = new(
        VolumeMinColor:   new RgbColor( 20, 220, 120),
        VolumeMaxColor:   new RgbColor( 40, 200, 255),
        MixChannelAColor: new RgbColor(110, 255, 180),
        MixChannelBColor: new RgbColor(  0, 140, 255));

    /// <summary>Lime → toxic yellow-green. Loud arcade palette.</summary>
    public static ColorProfile Toxic { get; } = new(
        VolumeMinColor:   new RgbColor(110, 255,   0),
        VolumeMaxColor:   new RgbColor(240, 255,  40),
        MixChannelAColor: new RgbColor( 60, 255, 120),
        MixChannelBColor: new RgbColor(180, 255,   0));

    /// <summary>
    /// All named presets, indexed by their human-readable name (case-insensitive
    /// at lookup via <see cref="GetByName"/>).
    /// </summary>
    public static IReadOnlyDictionary<string, ColorProfile> All { get; } =
        new Dictionary<string, ColorProfile>(StringComparer.Ordinal)
        {
            ["Default"]    = Default,
            ["Ocean"]      = Ocean,
            ["Sunset"]     = Sunset,
            ["Monochrome"] = Monochrome,
            ["Fire"]       = Fire,
            ["Ice"]        = Ice,
            ["Neon"]       = Neon,
            ["Aurora"]     = Aurora,
            ["Toxic"]      = Toxic,
        };

    private static readonly IReadOnlyDictionary<string, ColorProfile> CaseInsensitive =
        new Dictionary<string, ColorProfile>(All, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Resolve a preset by name (case-insensitive). Returns null if not found.
    /// </summary>
    public static ColorProfile? GetByName(string name)
    {
        if (string.IsNullOrEmpty(name)) return null;
        return CaseInsensitive.TryGetValue(name, out var profile) ? profile : null;
    }
}
