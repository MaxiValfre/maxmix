namespace MaxMix.Core.Events;

using MaxMix.Core.Abstractions;
using MaxMix.Core.Models;

public readonly record struct SessionAddedEvent(AudioSessionSnapshot Session);
public readonly record struct SessionRemovedEvent(byte SessionId);
public readonly record struct VolumeChangedEvent(byte SessionId, byte Volume);
public readonly record struct MuteChangedEvent(byte SessionId, bool IsMuted);
public readonly record struct KnobTurnedEvent(Command Command, VolumeData Volume);
public readonly record struct ModeChangeEvent(DisplayMode NewMode);
public readonly record struct ConnectionStateChanged(ConnectionState OldState, ConnectionState NewState);
