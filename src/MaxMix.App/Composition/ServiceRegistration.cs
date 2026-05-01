namespace MaxMix.App.Composition;

using MaxMix.App.Orchestration;
using MaxMix.App.Services;
using MaxMix.App.ViewModels;
using MaxMix.App.Views;
using MaxMix.Audio;
using MaxMix.Audio.Wasapi;
using MaxMix.Core.Abstractions;
using MaxMix.Core.Protocol;
using MaxMix.Core.Settings;
using MaxMix.Serial;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Serilog;

/// <summary>
/// Composition root. Wires every Core abstraction to its concrete adapter and
/// registers ViewModels + Views as transient/singleton as appropriate.
///
/// This is the ONLY place in MaxMix.App that knows about every layer at once.
/// The rest of the App talks through Core abstractions.
/// </summary>
public static class ServiceRegistration
{
    public static ServiceProvider Build()
    {
        var services = new ServiceCollection();

        // ---- Logging (Serilog already configured by App.OnStartup) ----
        services.AddLogging(b =>
        {
            b.ClearProviders();
            b.AddSerilog(dispose: false);
        });

        // ---- Core / shared ----
        services.AddSingleton<IProtocolCodec, BinaryProtocolCodec>();

        // ---- Settings (singleton — single source of truth for AppSettings) ----
        services.AddSingleton<ISettingsService, JsonSettingsService>();

        // ---- Serial transport ----
        services.AddSingleton<ISerialPortAdapter, SystemSerialPortAdapter>();
        services.AddSingleton(_ => new SerialDeviceServiceOptions());
        services.AddSingleton<ISerialConnection, SerialDeviceService>();

        // ---- Audio ----
        // NAudioWasapiEnumerator needs an Action callback that triggers reconciliation.
        // WasapiSessionManager owns that callback (ReconcileNow), but it needs the enumerator.
        // Break the circular dependency with a lazy factory:
        services.AddSingleton<IAudioSessionManager>(sp =>
        {
            // Create enumerator with a placeholder callback first
            Action? reconcileCallback = null;
            var enumerator = new NAudioWasapiEnumerator(() => reconcileCallback?.Invoke());
            var logger = sp.GetService<ILogger<WasapiSessionManager>>();
            var manager = new WasapiSessionManager(enumerator, logger);
            // Now wire the real callback
            reconcileCallback = manager.ReconcileNow;
            return manager;
        });

        // ---- Orchestration ----
        services.AddSingleton(_ => new StateOrchestratorOptions());
        // Hand-resolve so we can pass the optional ISettingsService positionally
        // (the ctor exposes it as a named optional parameter; the DI container
        // can match it from the service collection too, but being explicit here
        // documents the wiring).
        services.AddSingleton<IDeviceController>(sp => new StateOrchestrator(
            audio:           sp.GetRequiredService<IAudioSessionManager>(),
            serial:          sp.GetRequiredService<ISerialConnection>(),
            codec:           sp.GetRequiredService<IProtocolCodec>(),
            options:         sp.GetRequiredService<StateOrchestratorOptions>(),
            logger:          sp.GetService<ILogger<StateOrchestrator>>(),
            settingsService: sp.GetRequiredService<ISettingsService>()));

        // ---- App services ----
        // AutoStartService is registered against its abstraction so consumers
        // (SettingsViewModel, App.OnStartup) depend on IAutoStartService and
        // can be tested with Mock<IAutoStartService>.
        services.AddSingleton<IAutoStartService, AutoStartService>();
        services.AddSingleton<SystemTrayService>();

        // ---- ViewModels ----
        services.AddSingleton<ShellViewModel>();
        services.AddTransient<SettingsViewModel>();

        // ---- Views ----
        services.AddSingleton<MainWindow>();
        services.AddTransient<SettingsWindow>();

        return services.BuildServiceProvider(validateScopes: true);
    }
}
