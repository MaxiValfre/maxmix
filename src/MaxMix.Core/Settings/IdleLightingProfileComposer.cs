namespace MaxMix.Core.Settings;

using System;
using MaxMix.Core.Abstractions;
using MaxMix.Core.Models;

/// <summary>
/// Composes the idle-lighting override profile from the active Application
/// profile, the configured idle colors, the chosen mode, and elapsed idle time.
/// This stays pure so animation behavior can be tested without timers.
/// </summary>
public static class IdleLightingProfileComposer
{
    public static ColorProfile Compose(
        AppSettings settings,
        DisplayMode _,
        DateTime idleSinceUtc,
        DateTime nowUtc)
    {
        var idleGradient = new ColorProfile(
            settings.IdleLightingColorA,
            settings.IdleLightingColorB,
            settings.IdleLightingColorA,
            settings.IdleLightingColorB);

        return settings.IdleLightingMode switch
        {
            IdleLightingMode.Static => idleGradient,
            IdleLightingMode.Pulse  => Scale(idleGradient, PulseFactor(settings.IdleLightingSpeed, idleSinceUtc, nowUtc)),
            IdleLightingMode.Fade   => SolidColorProfile(Lerp(settings.IdleLightingColorA, settings.IdleLightingColorB, Wave(settings.IdleLightingSpeed, idleSinceUtc, nowUtc))),
            _ => idleGradient,
        };
    }

    private static double PulseFactor(byte speed, DateTime idleSinceUtc, DateTime nowUtc)
    {
        // Keep the pulse visible even at its dimmest point.
        return 0.25 + (0.75 * Wave(speed, idleSinceUtc, nowUtc));
    }

    private static double Wave(byte speed, DateTime idleSinceUtc, DateTime nowUtc)
    {
        var elapsedSeconds = Math.Max(0, (nowUtc - idleSinceUtc).TotalSeconds);
        var periodSeconds = PeriodSeconds(speed);
        var phase = (elapsedSeconds % periodSeconds) / periodSeconds;
        // Cosine wave remapped to [0..1]: start at 0, halfway at 1, loop back to 0.
        return 0.5 - (0.5 * Math.Cos(phase * 2 * Math.PI));
    }

    private static double PeriodSeconds(byte speed)
    {
        var clamped = Math.Clamp((int)speed, 1, 10);
        return 11 - clamped;
    }

    private static ColorProfile Scale(ColorProfile profile, double factor) => new(
        Scale(profile.VolumeMinColor, factor),
        Scale(profile.VolumeMaxColor, factor),
        Scale(profile.MixChannelAColor, factor),
        Scale(profile.MixChannelBColor, factor));

    private static ColorProfile SolidColorProfile(RgbColor color) => new(color, color, color, color);

    private static ColorProfile Lerp(ColorProfile a, ColorProfile b, double amount) => new(
        Lerp(a.VolumeMinColor, b.VolumeMinColor, amount),
        Lerp(a.VolumeMaxColor, b.VolumeMaxColor, amount),
        Lerp(a.MixChannelAColor, b.MixChannelAColor, amount),
        Lerp(a.MixChannelBColor, b.MixChannelBColor, amount));

    private static RgbColor Scale(RgbColor color, double factor) => new(
        ScaleByte(color.R, factor),
        ScaleByte(color.G, factor),
        ScaleByte(color.B, factor));

    private static RgbColor Lerp(RgbColor a, RgbColor b, double amount) => new(
        LerpByte(a.R, b.R, amount),
        LerpByte(a.G, b.G, amount),
        LerpByte(a.B, b.B, amount));

    private static byte ScaleByte(byte value, double factor) =>
        (byte)Math.Clamp((int)Math.Round(value * factor), 0, 255);

    private static byte LerpByte(byte a, byte b, double amount) =>
        (byte)Math.Clamp((int)Math.Round(a + ((b - a) * amount)), 0, 255);
}
