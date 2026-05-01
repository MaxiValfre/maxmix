namespace MaxMix.Audio.Identity;

using System.Security.Cryptography;
using System.Text;

/// <summary>
/// Stable 7-bit session identity derived from the executable path.
///
/// The firmware uses 7 bits for session id (high bit is isDefault flag in the
/// VolumeData payload), so id range MUST be 0..127.
///
/// Original bug: the previous codebase used <c>string.GetHashCode()</c> which is
/// randomized per-process in .NET Core. That meant the same app got a different
/// id every restart, breaking the firmware's slot tracking. This implementation
/// uses SHA1 over the lowercased UTF-8 path so the id is:
///   - stable across app restarts (deterministic)
///   - case-insensitive (Windows paths are)
///   - fits in 7 bits (truncated)
///
/// Collisions exist (5 distinct paths share 128 buckets), but the
/// <see cref="Sessions.SessionRegistry"/> handles uniqueness disambiguation.
/// </summary>
public static class SessionIdentity
{
    /// <summary>
    /// Computes a stable 0..127 session id from the executable path.
    /// Returns the same id for null and empty paths (treated as the same
    /// "system / unknown" session bucket).
    /// </summary>
    public static byte ComputeId(string? executablePath)
    {
        var normalized = (executablePath ?? string.Empty).ToLowerInvariant();
        var bytes = Encoding.UTF8.GetBytes(normalized);
        Span<byte> hash = stackalloc byte[20]; // SHA1 = 160 bits
        var written = SHA1.HashData(bytes, hash);
        if (written != 20)
        {
            throw new InvalidOperationException("SHA1.HashData returned unexpected length.");
        }
        return (byte)(hash[0] & 0x7F);
    }
}
