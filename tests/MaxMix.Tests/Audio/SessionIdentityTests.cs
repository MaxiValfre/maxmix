namespace MaxMix.Tests.Audio;

using MaxMix.Audio.Identity;

public class SessionIdentityTests
{
    [Fact]
    public void ComputeId_SamePath_ReturnsSameId()
    {
        const string path = @"C:\Program Files\Spotify\Spotify.exe";

        var first = SessionIdentity.ComputeId(path);
        var second = SessionIdentity.ComputeId(path);

        Assert.Equal(first, second);
    }

    [Fact]
    public void ComputeId_IsCaseInsensitive_SoWindowsPathsCollapse()
    {
        // Windows paths are case-insensitive; same exe shouldn't get two ids
        // depending on how a process reports its image path.
        var lower = SessionIdentity.ComputeId(@"c:\program files\spotify\spotify.exe");
        var upper = SessionIdentity.ComputeId(@"C:\Program Files\Spotify\Spotify.exe");

        Assert.Equal(lower, upper);
    }

    [Fact]
    public void ComputeId_AlwaysInRange_0To127()
    {
        // 7-bit id field; firmware uses high bit for isDefault flag.
        // Run a wide variety of inputs and assert every result fits.
        string[] paths = new[]
        {
            @"C:\Program Files\Spotify\Spotify.exe",
            @"C:\Program Files\Google\Chrome\Application\chrome.exe",
            @"C:\Program Files (x86)\Steam\Steam.exe",
            @"C:\Windows\System32\notepad.exe",
            @"D:\Games\Valorant\valorant.exe",
            @"C:\Users\test\AppData\Local\Discord\app-1.0\Discord.exe",
            @"C:\Program Files\Mozilla Firefox\firefox.exe",
            @"C:\Windows\explorer.exe",
            "",                       // empty edge case
            "a",
            "C:/forward/slashes.exe", // forward slashes
        };

        foreach (var path in paths)
        {
            var id = SessionIdentity.ComputeId(path);
            Assert.InRange(id, (byte)0, (byte)127);
        }
    }

    [Fact]
    public void ComputeId_DifferentPaths_GenerallyProduceDifferentIds()
    {
        // Probabilistic: 7-bit hash has 128 buckets, so collisions exist,
        // but a small handful of distinct paths should not all collide.
        string[] paths = new[]
        {
            @"C:\Program Files\Spotify\Spotify.exe",
            @"C:\Program Files\Google\Chrome\Application\chrome.exe",
            @"C:\Windows\System32\notepad.exe",
            @"C:\Program Files\Mozilla Firefox\firefox.exe",
            @"D:\Games\Valorant\valorant.exe",
        };

        var ids = paths.Select(SessionIdentity.ComputeId).Distinct().ToArray();

        // For 5 distinct paths into 128 buckets, the probability of all
        // colliding to <=1 distinct value is astronomically small.
        // Require at least 4 distinct ids out of 5 inputs.
        Assert.True(ids.Length >= 4,
            $"Expected at least 4 distinct ids out of {paths.Length} paths, got {ids.Length}");
    }

    [Fact]
    public void ComputeId_DoesNotUseStringGetHashCode()
    {
        // Bug regression: the original used string.GetHashCode() which is
        // randomized per-process in .NET Core. We need stability across runs.
        // Verify by checking against a known SHA1-based expected value:
        // SHA1("C:\\test.exe")[0] & 0x7F.
        const string path = @"C:\test.exe";
        using var sha1 = System.Security.Cryptography.SHA1.Create();
        var bytes = System.Text.Encoding.UTF8.GetBytes(path.ToLowerInvariant());
        var hash = sha1.ComputeHash(bytes);
        var expected = (byte)(hash[0] & 0x7F);

        var actual = SessionIdentity.ComputeId(path);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void ComputeId_NullPath_TreatedAsEmpty_DoesNotThrow()
    {
        // Some WASAPI sessions have no exe path (system sounds).
        var idEmpty = SessionIdentity.ComputeId("");
        var idNull = SessionIdentity.ComputeId(null!);

        Assert.Equal(idEmpty, idNull);
        Assert.InRange(idNull, (byte)0, (byte)127);
    }
}
