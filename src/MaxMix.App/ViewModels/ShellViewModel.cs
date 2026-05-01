namespace MaxMix.App.ViewModels;

using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MaxMix.Core.Abstractions;
using MaxMix.Core.Events;
using MaxMix.Core.Models;

/// <summary>
/// Top-level VM for the main window. Mirrors the orchestrator's view of the
/// world (current mode, current sessions, connection state) into observable
/// properties that XAML binds to.
///
/// <para>
/// Threading: event handlers may be called from arbitrary threads (audio
/// notifications, serial I/O). Mutating <see cref="ObservableCollection{T}"/>
/// off the UI thread is unsafe; we marshal via the Application dispatcher
/// when present, falling back to direct mutation for non-WPF tests.
/// </para>
/// </summary>
public sealed partial class ShellViewModel : ObservableObject, IDisposable
{
    private readonly IDeviceController _controller;
    private readonly IAudioSessionManager _audio;
    private readonly ISerialConnection _serial;
    private bool _disposed;

    [ObservableProperty] private DisplayMode _currentMode;
    [ObservableProperty] private string _currentModeLabel = string.Empty;
    [ObservableProperty] private ConnectionState _connectionState;
    [ObservableProperty] private string _connectionStatusText = "Disconnected";
    [ObservableProperty] private string? _portName;

    public ObservableCollection<SessionRow> Sessions { get; } = new();

    public ShellViewModel(
        IDeviceController controller,
        IAudioSessionManager audio,
        ISerialConnection serial)
    {
        _controller = controller ?? throw new ArgumentNullException(nameof(controller));
        _audio      = audio      ?? throw new ArgumentNullException(nameof(audio));
        _serial     = serial     ?? throw new ArgumentNullException(nameof(serial));

        _audio.SessionAdded   += OnSessionAdded;
        _audio.SessionRemoved += OnSessionRemoved;
        _audio.VolumeChanged  += OnVolumeChanged;
        _audio.MuteChanged    += OnMuteChanged;
        _serial.StateChanged  += OnConnectionStateChanged;

        // Seed current state.
        foreach (var s in _audio.Sessions) Sessions.Add(ToRow(s, isCurrent: false));
        ConnectionState     = _serial.State;
        ConnectionStatusText = _serial.State.ToString();
        PortName             = _serial.PortName;
        CurrentMode          = _controller.CurrentMode;
        CurrentModeLabel     = CurrentMode.ToString();
    }

    [RelayCommand] private Task CycleMode()       => _controller.CycleModeAsync();
    [RelayCommand] private Task NextSession()     => _controller.NextSessionAsync();
    [RelayCommand] private Task PreviousSession() => _controller.PreviousSessionAsync();

    partial void OnCurrentModeChanged(DisplayMode value) => CurrentModeLabel = value.ToString();
    partial void OnConnectionStateChanged(ConnectionState value) => ConnectionStatusText = value.ToString();

    private void OnSessionAdded(object? sender, SessionAddedEvent e)
        => Sessions.Add(ToRow(e.Session, isCurrent: false));

    private void OnSessionRemoved(object? sender, SessionRemovedEvent e)
    {
        var existing = Sessions.FirstOrDefault(s => s.Id == e.SessionId);
        if (existing is not null) Sessions.Remove(existing);
    }

    private void OnVolumeChanged(object? sender, VolumeChangedEvent e)
    {
        var row = Sessions.FirstOrDefault(s => s.Id == e.SessionId);
        if (row is not null) row.Volume = e.Volume;
    }

    private void OnMuteChanged(object? sender, MuteChangedEvent e)
    {
        var row = Sessions.FirstOrDefault(s => s.Id == e.SessionId);
        if (row is not null) row.IsMuted = e.IsMuted;
    }

    private void OnConnectionStateChanged(object? sender, ConnectionState state)
    {
        ConnectionState = state;
        PortName        = _serial.PortName;
    }

    private static SessionRow ToRow(AudioSessionSnapshot s, bool isCurrent) => new()
    {
        Id        = s.Id,
        Name      = s.DisplayName,
        Volume    = s.Volume,
        IsMuted   = s.IsMuted,
        IsCurrent = isCurrent,
    };

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _audio.SessionAdded   -= OnSessionAdded;
        _audio.SessionRemoved -= OnSessionRemoved;
        _audio.VolumeChanged  -= OnVolumeChanged;
        _audio.MuteChanged    -= OnMuteChanged;
        _serial.StateChanged  -= OnConnectionStateChanged;
    }
}
