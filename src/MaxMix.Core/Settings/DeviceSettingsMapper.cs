namespace MaxMix.Core.Settings;

using MaxMix.Core.Abstractions;
using MaxMix.Core.Models;

/// <summary>
/// Pure projection: takes the user's <see cref="AppSettings"/> plus the active
/// <see cref="DisplayMode"/> and produces the 14-byte <see cref="DeviceSettings"/>
/// payload sent to the firmware via the SETTINGS command.
///
/// Spec section 4: each mode owns its own ColorProfile; switching modes must
/// re-send SETTINGS with that mode's colors. Spec also notes Bug #4 (R/G/B
/// transposition in the original) — this mapper preserves channels exactly.
/// </summary>
public static class DeviceSettingsMapper
{
    /// <summary>
    /// Build the firmware payload for the given mode.
    /// Falls back to <see cref="ColorPresets.Default"/> if the mode has no
    /// configured profile (e.g. a freshly-extended schema or
    /// <see cref="DisplayMode.Splash"/> which the user can't configure).
    /// </summary>
    public static DeviceSettings Map(AppSettings settings, DisplayMode mode, ColorProfile? overrideProfile = null)
    {
        var profile = overrideProfile
            ?? (settings.ColorProfiles.TryGetValue(mode, out var p)
                ? p
                : ColorPresets.Default);

        return new DeviceSettings(
            SleepAfterSeconds:      settings.IdleLightingEnabled ? (byte)0 : settings.SleepAfterSeconds,
            AccelerationPercentage: settings.AccelerationPercentage,
            ContinuousScroll:       settings.ContinuousScroll,
            VolumeMinColor:         profile.VolumeMinColor,
            VolumeMaxColor:         profile.VolumeMaxColor,
            MixChannelAColor:       profile.MixChannelAColor,
            MixChannelBColor:       profile.MixChannelBColor);
    }
}
