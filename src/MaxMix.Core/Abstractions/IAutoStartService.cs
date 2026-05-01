namespace MaxMix.Core.Abstractions;

/// <summary>
/// Abstraction over the "start MaxMix when Windows starts" mechanism. The
/// concrete adapter writes to <c>HKCU\Software\Microsoft\Windows\CurrentVersion\Run</c>
/// (per-user, no elevation), but <c>SettingsViewModel</c> and <c>App</c> only
/// see this contract — that lets unit tests substitute a Moq instance instead
/// of touching the real registry.
///
/// Failure semantics: implementations MUST NOT throw on registry permission
/// errors. They MUST log a warning via <c>ILogger</c> and return <c>false</c>
/// from <see cref="SetEnabled"/> so callers can react if they care.
/// </summary>
public interface IAutoStartService
{
    /// <summary>
    /// True if the registry currently has a MaxMix entry under the Run key.
    /// On read failure, returns <c>false</c> (and logs a warning).
    /// </summary>
    bool IsEnabled { get; }

    /// <summary>
    /// Add (when <paramref name="enabled"/> is <c>true</c>) or remove (when
    /// <c>false</c>) the registry value that makes Windows launch MaxMix at
    /// user logon.
    /// </summary>
    /// <returns>
    /// <c>true</c> if the registry now reflects the requested state;
    /// <c>false</c> if the registry write failed (e.g. policy lockdown).
    /// Implementations never throw — they log and return <c>false</c>.
    /// </returns>
    bool SetEnabled(bool enabled);
}
