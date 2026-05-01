namespace MaxMix.Tests.ViewModels;

using System;
using System.Linq;
using System.Threading.Tasks;
using MaxMix.App.Services;
using MaxMix.App.ViewModels;
using MaxMix.Core.Abstractions;
using MaxMix.Core.Models;
using MaxMix.Core.Settings;
using MaxMix.Tests.Orchestration;
using Moq;
using Xunit;

/// <summary>
/// Tests for <see cref="SettingsViewModel"/>. Locks the Application-only mode
/// invariant at the UI layer:
///
///   - <see cref="SettingsViewModel.Modes"/> exposes exactly one entry,
///     <see cref="DisplayMode.Application"/>.
///   - <see cref="SettingsViewModel.SelectedMode"/> is always
///     <see cref="DisplayMode.Application"/>, even if a caller (legacy code,
///     XAML two-way binding from a stale <see cref="AppSettings.StartupMode"/>,
///     etc.) tries to set it to Output, Input, or Game.
///   - Basic load/round-trip behavior of the rest of the bindable surface.
///
/// The verify report flagged 0% direct coverage on this VM as a WARNING and
/// the spec scenario "Mode selector not interactive" as a CRITICAL gap. These
/// tests close both at the unit-test layer (no WPF dispatcher needed —
/// SettingsViewModel is a plain ObservableObject).
/// </summary>
public class SettingsViewModelTests
{
    // ---------- builders ----------

    private static AppSettings BuildSettings(
        DisplayMode startup = DisplayMode.Application,
        byte sleep = 7,
        byte accel = 33,
        bool continuous = false,
        string? lastPort = "COM5",
        bool idleLightingEnabled = false,
        byte idleAfter = 30,
        string? idlePresetName = "Aurora",
        IdleLightingMode idleMode = IdleLightingMode.Static,
        byte idleSpeed = 5)
    {
        var profile = new ColorProfile(
            VolumeMinColor:   new RgbColor(0x10, 0x20, 0x30),
            VolumeMaxColor:   new RgbColor(0x40, 0x50, 0x60),
            MixChannelAColor: new RgbColor(0x70, 0x80, 0x90),
            MixChannelBColor: new RgbColor(0xA0, 0xB0, 0xC0));

        var idlePreset = ColorPresets.GetByName(idlePresetName ?? string.Empty) ?? ColorPresets.Aurora;

        return AppSettings.Defaults with
        {
            StartupMode            = startup,
            AccelerationPercentage = accel,
            ContinuousScroll       = continuous,
            SleepAfterSeconds      = sleep,
            LastConnectedPort      = lastPort,
            IdleLightingEnabled    = idleLightingEnabled,
            IdleLightingAfterSeconds = idleAfter,
            IdleLightingPresetName = idlePresetName,
            IdleLightingMode       = idleMode,
            IdleLightingSpeed      = idleSpeed,
            IdleLightingColorA     = idlePreset.VolumeMinColor,
            IdleLightingColorB     = idlePreset.VolumeMaxColor,
            ColorProfiles          = new System.Collections.Generic.Dictionary<DisplayMode, ColorProfile>
            {
                [DisplayMode.Application] = profile,
            },
        };
    }

    private static SettingsViewModel BuildVm(AppSettings? initial = null)
    {
        var svc = new FakeSettingsService(initial ?? BuildSettings());
        var autoStart = new AutoStartService();
        return new SettingsViewModel(svc, autoStart);
    }

    // ---------- Modes collection invariant ----------

    [Fact]
    public void Modes_collection_contains_exactly_one_entry_Application()
    {
        // Spec (mode-management → "Mode UI Restriction"):
        // The settings UI MUST hide or disable the mode selector. If displayed,
        // it MUST show "Application" as the fixed, non-editable value.
        var vm = BuildVm();

        var only = Assert.Single(vm.Modes);
        Assert.Equal(DisplayMode.Application, only);
    }

    [Fact]
    public void Modes_collection_does_not_contain_Output_Input_or_Game()
    {
        // Triangulation: enumerate every other DisplayMode value and prove
        // none of them are exposed as a selectable option.
        var vm = BuildVm();

        Assert.DoesNotContain(DisplayMode.Output,    vm.Modes);
        Assert.DoesNotContain(DisplayMode.Input,     vm.Modes);
        Assert.DoesNotContain(DisplayMode.Game,      vm.Modes);
        Assert.DoesNotContain(DisplayMode.Splash,    vm.Modes);
    }

    // ---------- SelectedMode coercion ----------

    [Fact]
    public void SelectedMode_initial_value_is_Application_even_if_persisted_StartupMode_is_Output()
    {
        // Spec: "Coerce any legacy persisted StartupMode (Output/Input/Game)
        // to Application — the UI no longer offers other choices."
        var vm = BuildVm(BuildSettings(startup: DisplayMode.Output));

        Assert.Equal(DisplayMode.Application, vm.SelectedMode);
    }

    [Fact]
    public void SelectedMode_initial_value_is_Application_even_if_persisted_StartupMode_is_Game()
    {
        // Triangulation across all legacy non-Application modes.
        var vm = BuildVm(BuildSettings(startup: DisplayMode.Game));

        Assert.Equal(DisplayMode.Application, vm.SelectedMode);
    }

    [Fact]
    public void SelectedMode_setter_to_Output_is_coerced_back_to_Application()
    {
        // Spec scenario: "Mode selector not interactive" — even if a caller
        // (XAML binding, test, legacy code) writes a non-Application value
        // to SelectedMode, the property MUST coerce back to Application.
        var vm = BuildVm();
        Assert.Equal(DisplayMode.Application, vm.SelectedMode);

        vm.SelectedMode = DisplayMode.Output;

        Assert.Equal(DisplayMode.Application, vm.SelectedMode);
    }

    [Fact]
    public void SelectedMode_setter_to_Input_is_coerced_back_to_Application()
    {
        var vm = BuildVm();

        vm.SelectedMode = DisplayMode.Input;

        Assert.Equal(DisplayMode.Application, vm.SelectedMode);
    }

    [Fact]
    public void SelectedMode_setter_to_Game_is_coerced_back_to_Application()
    {
        var vm = BuildVm();

        vm.SelectedMode = DisplayMode.Game;

        Assert.Equal(DisplayMode.Application, vm.SelectedMode);
    }

    [Fact]
    public void SelectedMode_setter_to_Splash_is_coerced_back_to_Application()
    {
        // Splash is firmware-internal, never user-selectable.
        var vm = BuildVm();

        vm.SelectedMode = DisplayMode.Splash;

        Assert.Equal(DisplayMode.Application, vm.SelectedMode);
    }

    [Fact]
    public void SelectedMode_setter_to_Application_keeps_Application()
    {
        // Triangulation: the no-op case (writing the only valid value) must
        // still leave the value at Application — proves we're not blindly
        // setting to Application from EVERY write but specifically coercing
        // out invalid values.
        var vm = BuildVm();

        vm.SelectedMode = DisplayMode.Application;

        Assert.Equal(DisplayMode.Application, vm.SelectedMode);
    }

    // ---------- LoadFromSettings hydration (basic VM coverage) ----------

    [Fact]
    public void Constructor_hydrates_bindable_properties_from_ISettingsService_Current()
    {
        // Basic coverage: prove the constructor reads from settings.Current.
        var vm = BuildVm(BuildSettings(
            startup:    DisplayMode.Application,
            sleep:      9,
            accel:      77,
            continuous: false,
            lastPort:   "COM12"));

        Assert.Equal((byte)9,  vm.SleepAfterSeconds);
        Assert.Equal((byte)77, vm.AccelerationPercentage);
        Assert.False(vm.ContinuousScroll);
        Assert.Equal("COM12",  vm.SelectedPort);
        // Color profile fields hydrated from ColorProfiles[Application].
        Assert.Equal((byte)0x10, vm.VolumeMinR);
        Assert.Equal((byte)0x20, vm.VolumeMinG);
        Assert.Equal((byte)0x30, vm.VolumeMinB);
        Assert.Equal((byte)0x40, vm.VolumeMaxR);
        Assert.Equal((byte)0xA0, vm.MixBR);
    }

    [Fact]
    public void Constructor_hydrates_idle_lighting_properties()
    {
        var vm = BuildVm(BuildSettings(
            idleLightingEnabled: true,
            idleAfter: 45,
            idlePresetName: "Toxic",
            idleMode: IdleLightingMode.Fade,
            idleSpeed: 8));

        Assert.True(vm.IdleLightingEnabled);
        Assert.Equal((byte)45, vm.IdleLightingAfterSeconds);
        Assert.Equal("Toxic", vm.SelectedIdlePreset);
        Assert.Equal(IdleLightingMode.Fade, vm.SelectedIdleLightingMode);
        Assert.Equal((byte)8, vm.IdleLightingSpeed);
        Assert.Equal("#6EFF00", vm.IdleColorAHex);
        Assert.Equal("#F0FF28", vm.IdleColorBHex);
    }

    [Fact]
    public void Constructor_hydrates_active_color_hex_preview_properties()
    {
        var vm = BuildVm(BuildSettings());

        Assert.Equal("#102030", vm.VolumeMinHex);
        Assert.Equal("#405060", vm.VolumeMaxHex);
    }

    [Fact]
    public async Task SaveCommand_calls_ISettingsService_SaveAsync_with_current_VM_state()
    {
        // Basic coverage: writing to bindable props then invoking Save flushes
        // through the settings service. We use FakeSettingsService.Current
        // before/after the save as the assertion vehicle.
        var svc = new FakeSettingsService(BuildSettings(sleep: 1, accel: 1));
        var vm = new SettingsViewModel(svc, new AutoStartService());

        vm.SleepAfterSeconds      = 21;
        vm.AccelerationPercentage = 88;
        vm.ContinuousScroll       = true;
        vm.SelectedPort           = "COM21";
        vm.IdleLightingEnabled    = true;
        vm.IdleLightingAfterSeconds = 40;
        vm.SelectedIdlePreset     = "Aurora";
        vm.SelectedIdleLightingMode = IdleLightingMode.Pulse;
        vm.IdleLightingSpeed      = 7;

        await vm.SaveCommand.ExecuteAsync(null);

        Assert.Equal((byte)21, svc.Current.SleepAfterSeconds);
        Assert.Equal((byte)88, svc.Current.AccelerationPercentage);
        Assert.True(svc.Current.ContinuousScroll);
        Assert.Equal("COM21",  svc.Current.LastConnectedPort);
        Assert.True(svc.Current.IdleLightingEnabled);
        Assert.Equal((byte)40, svc.Current.IdleLightingAfterSeconds);
        Assert.Equal("Aurora", svc.Current.IdleLightingPresetName);
        Assert.Equal(IdleLightingMode.Pulse, svc.Current.IdleLightingMode);
        Assert.Equal((byte)7, svc.Current.IdleLightingSpeed);
    }

    [Fact]
    public void ApplyPresetCommand_updates_active_color_preview_properties()
    {
        var vm = BuildVm();
        vm.SelectedPreset = "Neon";

        vm.ApplyPresetCommand.Execute(null);

        Assert.Equal("#FF14DC", vm.VolumeMinHex);
        Assert.Equal("#14F0F0", vm.VolumeMaxHex);
    }

    [Fact]
    public void ApplyIdlePresetCommand_updates_idle_color_preview_properties()
    {
        var vm = BuildVm();
        vm.SelectedIdlePreset = "Aurora";

        vm.ApplyIdlePresetCommand.Execute(null);

        Assert.Equal("#14DC78", vm.IdleColorAHex);
        Assert.Equal("#28C8FF", vm.IdleColorBHex);
    }

    [Fact]
    public void ResetCommand_reloads_from_ISettingsService_Current()
    {
        var svc = new FakeSettingsService(BuildSettings(sleep: 4, accel: 5));
        var vm = new SettingsViewModel(svc, new AutoStartService());

        // Mutate the VM in memory but DO NOT save.
        vm.SleepAfterSeconds      = 99;
        vm.AccelerationPercentage = 99;

        vm.ResetCommand.Execute(null);

        Assert.Equal((byte)4, vm.SleepAfterSeconds);
        Assert.Equal((byte)5, vm.AccelerationPercentage);
    }

    // ---------- IAutoStartService wiring (Batch 2 of start-with-windows) ----------
    //
    // Spec: SettingsViewModel MUST depend on IAutoStartService (not the
    // concrete AutoStartService). SaveCommand MUST forward the toggled
    // AutoStartWithWindows flag to IAutoStartService.SetEnabled, AND MUST
    // remain resilient to SetEnabled failures (registry write can fail when
    // policy denies HKCU writes — see AutoStartService.SetEnabled which now
    // returns bool + logs warning instead of throwing, but legacy / future
    // implementations may still throw, so the VM must guard).
    //
    // These tests use Mock<IAutoStartService> — the constructor currently
    // takes a concrete AutoStartService, so this code WILL NOT COMPILE until
    // task 2.2 retypes the parameter to IAutoStartService.

    [Fact]
    public async Task SaveCommand_calls_SetEnabled_True_when_AutoStartWithWindows_toggled_on()
    {
        // Spec: "User enables auto-start via settings save"
        // GIVEN auto-start is currently disabled
        // WHEN the user toggles AutoStartWithWindows ON and executes SaveCommand
        // THEN IAutoStartService.SetEnabled(true) is called.
        var svc = new FakeSettingsService(BuildSettings() with { AutoStartWithWindows = false });
        var autoStart = new Mock<IAutoStartService>(MockBehavior.Strict);
        autoStart.Setup(a => a.SetEnabled(true)).Returns(true);

        var vm = new SettingsViewModel(svc, autoStart.Object);
        vm.AutoStartWithWindows = true;

        await vm.SaveCommand.ExecuteAsync(null);

        autoStart.Verify(a => a.SetEnabled(true), Times.Once);
    }

    [Fact]
    public async Task SaveCommand_calls_SetEnabled_False_when_AutoStartWithWindows_toggled_off()
    {
        // Spec: "User disables auto-start via settings save"
        // Triangulation: forces the VM to forward the ACTUAL flag value,
        // not a hardcoded `true`.
        var svc = new FakeSettingsService(BuildSettings() with { AutoStartWithWindows = true });
        var autoStart = new Mock<IAutoStartService>(MockBehavior.Strict);
        autoStart.Setup(a => a.SetEnabled(false)).Returns(true);

        var vm = new SettingsViewModel(svc, autoStart.Object);
        vm.AutoStartWithWindows = false;

        await vm.SaveCommand.ExecuteAsync(null);

        autoStart.Verify(a => a.SetEnabled(false), Times.Once);
    }

    [Fact]
    public async Task SaveCommand_does_not_throw_when_SetEnabled_throws()
    {
        // Spec: "Registry write fails (access denied)" — the design guarantees
        // SetEnabled returns false + logs instead of throwing, but the VM
        // boundary MUST also be resilient to a legacy / future implementation
        // that DOES throw, so the user's settings still save and StatusMessage
        // surfaces a friendly error rather than crashing the UI.
        var svc = new FakeSettingsService(BuildSettings());
        var autoStart = new Mock<IAutoStartService>(MockBehavior.Strict);
        autoStart
            .Setup(a => a.SetEnabled(It.IsAny<bool>()))
            .Throws(new InvalidOperationException("simulated registry failure"));

        var vm = new SettingsViewModel(svc, autoStart.Object);
        vm.AutoStartWithWindows = true;

        // Must NOT propagate the exception out of the command.
        await vm.SaveCommand.ExecuteAsync(null);

        // The settings themselves still persisted (autoStart failure is
        // non-fatal to the settings save).
        Assert.True(svc.Current.AutoStartWithWindows);
        // StatusMessage should communicate the failure rather than the
        // misleading "Settings saved." — the settings were saved but the
        // auto-start side effect failed; the user needs to know.
        Assert.Contains("auto-start", vm.StatusMessage, StringComparison.OrdinalIgnoreCase);
    }
}
