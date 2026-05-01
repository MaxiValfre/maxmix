namespace MaxMix.Tests.Services;

using System;
using MaxMix.App.Services;
using MaxMix.Core.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Win32;
using Moq;
using Xunit;

/// <summary>
/// Adapter-level tests for <see cref="AutoStartService"/>. Exercise the real
/// HKCU\...\Run registry against a unique GUID-based value name so the user's
/// real "MaxMix" autostart entry is never touched. Each test cleans up its
/// value in a finally block.
///
/// Spec coverage (sdd/start-with-windows/spec):
///  - "Registry read succeeds with no entry" → IsEnabled returns false.
///  - "User enables auto-start" → SetEnabled(true) writes a quoted exe path.
///  - "User disables auto-start" → SetEnabled(false) deletes the value.
///  - "Registry read succeeds with entry present" → IsEnabled returns true.
///  - "SetEnabled is idempotent" → SetEnabled(true) twice does not error.
/// </summary>
public class AutoStartServiceTests
{
    // The HKCU subkey path that AutoStartService writes to. Tests use a
    // unique value name under this same key; cleanup falls back to deleting
    // the value directly via Registry to avoid orphaning entries when an
    // assertion fails mid-test.
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

    private static string UniqueValueName() =>
        "MaxMix.Tests." + Guid.NewGuid().ToString("N");

    private static void DeleteValue(string valueName)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            key?.DeleteValue(valueName, throwOnMissingValue: false);
        }
        catch
        {
            // Best-effort cleanup; CI may not even have written the value.
        }
    }

    [Fact]
    public void IsEnabled_ReturnsFalse_WhenNoEntry()
    {
        var name = UniqueValueName();
        try
        {
            var svc = new AutoStartService(name, NullLogger<AutoStartService>.Instance);

            Assert.False(svc.IsEnabled);
        }
        finally
        {
            DeleteValue(name);
        }
    }

    [Fact]
    public void SetEnabled_True_WritesRegistryValue()
    {
        var name = UniqueValueName();
        try
        {
            var svc = new AutoStartService(name, NullLogger<AutoStartService>.Instance);

            var ok = svc.SetEnabled(true);

            Assert.True(ok);
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
            Assert.NotNull(key);
            var written = key!.GetValue(name) as string;
            Assert.False(string.IsNullOrEmpty(written));
            // Path must be wrapped in quotes (paths with spaces).
            Assert.StartsWith("\"", written);
            Assert.EndsWith("\"", written);
        }
        finally
        {
            DeleteValue(name);
        }
    }

    [Fact]
    public void SetEnabled_False_RemovesRegistryValue()
    {
        var name = UniqueValueName();
        try
        {
            var svc = new AutoStartService(name, NullLogger<AutoStartService>.Instance);
            // Arrange: write the value first so we have something to remove.
            Assert.True(svc.SetEnabled(true));
            using (var verify = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false))
            {
                Assert.NotNull(verify!.GetValue(name));
            }

            var ok = svc.SetEnabled(false);

            Assert.True(ok);
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
            Assert.Null(key!.GetValue(name));
        }
        finally
        {
            DeleteValue(name);
        }
    }

    [Fact]
    public void SetEnabled_True_Idempotent()
    {
        var name = UniqueValueName();
        try
        {
            var svc = new AutoStartService(name, NullLogger<AutoStartService>.Instance);

            Assert.True(svc.SetEnabled(true));
            Assert.True(svc.SetEnabled(true));

            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
            var written = key!.GetValue(name) as string;
            Assert.False(string.IsNullOrEmpty(written));
        }
        finally
        {
            DeleteValue(name);
        }
    }

    [Fact]
    public void IsEnabled_ReturnsTrue_WhenEntryExists()
    {
        var name = UniqueValueName();
        try
        {
            var svc = new AutoStartService(name, NullLogger<AutoStartService>.Instance);
            Assert.True(svc.SetEnabled(true));

            Assert.True(svc.IsEnabled);
        }
        finally
        {
            DeleteValue(name);
        }
    }

    [Fact]
    public void Implements_IAutoStartService()
    {
        // Locks in the interface contract — design decision: AutoStartService
        // implements IAutoStartService so SettingsViewModel and App can depend
        // on the abstraction.
        var svc = new AutoStartService();

        Assert.IsAssignableFrom<IAutoStartService>(svc);
    }

    // ----------------------------------------------------------------------
    //  Logging — failure paths must call ILogger.LogWarning, not silently
    //  swallow. The poisoned subkey path forces CreateSubKey/OpenSubKey to
    //  throw (Registry refuses path components containing illegal chars on
    //  some surfaces, and even when it doesn't, the path under HKCU does not
    //  exist + cannot be created without write access at the root). We
    //  verify the LogWarning call against a Moq<ILogger<AutoStartService>>.
    // ----------------------------------------------------------------------

    /// <summary>
    /// A subkey path that the registry API will refuse: each component must
    /// be ≤ 255 chars (Win32 registry constraint). 300 'A' chars overflows
    /// that and forces RegCreateKeyEx/RegOpenKeyEx to throw a known exception
    /// type before any value is touched. This drives the LogWarning branch
    /// without polluting any real registry location.
    /// </summary>
    private static readonly string PoisonedSubKey = new('A', 300);

    [Fact]
    public void SetEnabled_LogsWarning_OnRegistryFailure()
    {
        // Arrange: poisoned subkey path forces CreateSubKey to throw.
        var name = UniqueValueName();
        var logger = new Mock<ILogger<AutoStartService>>();

        var svc = new AutoStartService(name, PoisonedSubKey, logger.Object);

        // Act: SetEnabled must NOT throw, must return false.
        var ok = svc.SetEnabled(true);

        // Assert: LogWarning called at least once on the failure path.
        Assert.False(ok);
        logger.Verify(
            x => x.Log(
                LogLevel.Warning,
                It.IsAny<EventId>(),
                It.IsAny<It.IsAnyType>(),
                It.IsAny<Exception?>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.AtLeastOnce);
    }

    [Fact]
    public void IsEnabled_LogsWarning_OnRegistryFailure()
    {
        // Arrange: poisoned subkey path forces OpenSubKey to throw.
        var name = UniqueValueName();
        var logger = new Mock<ILogger<AutoStartService>>();

        var svc = new AutoStartService(name, PoisonedSubKey, logger.Object);

        // Act: IsEnabled must NOT throw, must return false.
        var enabled = svc.IsEnabled;

        // Assert: false + LogWarning called.
        Assert.False(enabled);
        logger.Verify(
            x => x.Log(
                LogLevel.Warning,
                It.IsAny<EventId>(),
                It.IsAny<It.IsAnyType>(),
                It.IsAny<Exception?>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.AtLeastOnce);
    }
}
