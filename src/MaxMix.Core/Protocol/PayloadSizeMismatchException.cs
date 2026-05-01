namespace MaxMix.Core.Protocol;

using MaxMix.Core.Models;

/// <summary>
/// Thrown when a payload presented to the codec does not match the firmware's
/// expected fixed size for that command. The protocol has no length field nor
/// CRC — sending the wrong size desyncs the firmware permanently. Original
/// bug #1 (32-byte SessionData sent for VOLUME, expects 2) — never again.
/// </summary>
public sealed class PayloadSizeMismatchException : Exception
{
    public Command Command { get; }
    public int ExpectedSize { get; }
    public int ActualSize { get; }

    public PayloadSizeMismatchException(Command command, int expectedSize, int actualSize)
        : base($"Payload size mismatch for command {command}: expected {expectedSize} bytes, got {actualSize} bytes.")
    {
        Command = command;
        ExpectedSize = expectedSize;
        ActualSize = actualSize;
    }
}
