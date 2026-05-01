namespace MaxMix.App.Orchestration;

using System;
using MaxMix.Core.Models;

/// <summary>
/// Tunables for <see cref="StateOrchestrator"/>. Defaults are reasonable for
/// production; tests may override individual properties.
/// </summary>
public sealed class StateOrchestratorOptions
{
    /// <summary>
    /// Mode shown on the device immediately after StartAsync().
    /// Pinned to Application — Output / Input / Game are no longer driven
    /// by the desktop (Batch 2 simplification).
    /// </summary>
    public DisplayMode StartupMode { get; init; } = DisplayMode.Application;

    /// <summary>
    /// Settings pushed to the firmware on initial sync. Until Batch 5 wires up
    /// <see cref="MaxMix.Core.Abstractions.ISettingsService"/>, callers can
    /// pass a literal value here.
    /// </summary>
    public DeviceSettings InitialDeviceSettings { get; init; } = DefaultSettings;

    /// <summary>
    /// Mode-states payload the firmware uses to decide which screens to show.
    /// All four user-visible modes enabled by default.
    /// </summary>
    public ModeStates InitialModeStates { get; init; } = new(
        Splash: 0, Output: 1, Input: 1, Application: 1, Game: 1);

    /// <summary>
    /// Tolerance for the feedback-loop guard: when the device tells us volume=N
    /// and we set Windows to N, the audio manager's echo within this delta is
    /// suppressed. ±2 is enough to absorb WASAPI rounding (volume is a float).
    /// </summary>
    public byte EchoVolumeTolerance { get; init; } = 2;

    /// <summary>
    /// Poll interval for desktop-side idle lighting transitions. Kept short so
    /// the ring can swap presets promptly after the configured idle timeout.
    /// Tests override this with a smaller value for faster feedback.
    /// </summary>
    public TimeSpan IdleCheckInterval { get; init; } = TimeSpan.FromMilliseconds(100);

    private static DeviceSettings DefaultSettings { get; } = new(
        SleepAfterSeconds: 5,
        AccelerationPercentage: 50,
        ContinuousScroll: true,
        VolumeMinColor:   new RgbColor(0x00, 0x00, 0x80),
        VolumeMaxColor:   new RgbColor(0x00, 0xFF, 0xFF),
        MixChannelAColor: new RgbColor(0xFF, 0x00, 0x80),
        MixChannelBColor: new RgbColor(0x00, 0x80, 0xFF));
}
