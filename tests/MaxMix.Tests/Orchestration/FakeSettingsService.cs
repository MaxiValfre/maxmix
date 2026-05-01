namespace MaxMix.Tests.Orchestration;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MaxMix.Core.Abstractions;
using MaxMix.Core.Models;

/// <summary>
/// Test double for <see cref="ISettingsService"/>. Holds an in-memory
/// <see cref="AppSettings"/> snapshot and exposes a <see cref="Raise"/>
/// helper so tests can drive <see cref="ISettingsService.SettingsChanged"/>
/// without going through file I/O.
/// </summary>
internal sealed class FakeSettingsService : ISettingsService
{
    public AppSettings Current { get; set; }

    public event EventHandler<AppSettings>? SettingsChanged;

    public FakeSettingsService(AppSettings? initial = null)
    {
        Current = initial ?? AppSettings.Defaults;
    }

    public Task LoadAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task SaveAsync(AppSettings next, CancellationToken cancellationToken = default)
    {
        Current = next;
        SettingsChanged?.Invoke(this, next);
        return Task.CompletedTask;
    }

    /// <summary>Raises <see cref="SettingsChanged"/> with <paramref name="next"/> and
    /// updates <see cref="Current"/> in lock-step (mirrors <c>JsonSettingsService.SaveAsync</c>).</summary>
    public void Raise(AppSettings next)
    {
        Current = next;
        SettingsChanged?.Invoke(this, next);
    }
}
