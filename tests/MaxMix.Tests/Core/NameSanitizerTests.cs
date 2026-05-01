namespace MaxMix.Tests.Core;

using MaxMix.Core.Sanitization;

/// <summary>
/// Tests for <see cref="NameSanitizer"/>.
/// Spec: Domain "ascii-transliteration" — OLED-Safe Name Sanitization.
///
/// Pipeline contract:
///   1. NFD-decompose the input.
///   2. Strip Unicode combining marks (category M / NonSpacingMark).
///   3. Keep only printable ASCII (0x20-0x7E); replace anything else with empty.
///   4. Truncate to 29 characters max (codec owns the 30th-byte null terminator).
///
/// Edge-case rules:
///   - null   → ""
///   - ""     → ""
///   - non-empty input that fully strips → "App" fallback
///   - never adds its own null terminator
/// </summary>
public class NameSanitizerTests
{
    [Theory]
    [InlineData("Spotify",       "Spotify")]      // ASCII-only passthrough
    [InlineData("Médialécteur",  "Medialecteur")] // accented Latin → stripped diacritics
    [InlineData("café",          "cafe")]         // single accented char
    [InlineData("Niño",          "Nino")]         // tilde-n decomposes and strips
    public void ToAscii_TransliteratesAccentedLatin(string input, string expected)
    {
        Assert.Equal(expected, NameSanitizer.ToAscii(input));
    }

    [Fact]
    public void ToAscii_StripsEmojiAndSymbolsButKeepsAscii()
    {
        // 🎵 (U+1F3B5) and ™ (U+2122) are non-ASCII and have no decomposition
        // into ASCII. They MUST be replaced with empty, leaving only "Music".
        var result = NameSanitizer.ToAscii("🎵Music™");

        Assert.Equal("Music", result);
        // Belt-and-suspenders: every char must be printable ASCII.
        Assert.All(result, c => Assert.InRange(c, (char)0x20, (char)0x7E));
    }

    [Fact]
    public void ToAscii_NullInput_ReturnsEmpty()
    {
        Assert.Equal(string.Empty, NameSanitizer.ToAscii(null));
    }

    [Fact]
    public void ToAscii_EmptyInput_ReturnsEmpty()
    {
        Assert.Equal(string.Empty, NameSanitizer.ToAscii(string.Empty));
    }

    [Fact]
    public void ToAscii_AllCombiningMarks_ReturnsFallback()
    {
        // Pure combining marks: U+0301 (combining acute), U+0308 (combining diaeresis),
        // U+0327 (combining cedilla). All NonSpacingMark — get stripped → empty result
        // → fallback kicks in because input was non-empty.
        var input = "\u0301\u0308\u0327";

        var result = NameSanitizer.ToAscii(input);

        Assert.Equal("App", result);
    }

    [Fact]
    public void ToAscii_AllNonTransliterable_ReturnsFallback()
    {
        // Pure emoji input: nothing survives sanitization, but input was non-empty
        // → fallback "App". Distinguishes from the empty-input case which returns "".
        var result = NameSanitizer.ToAscii("🎵🎶🎤");

        Assert.Equal("App", result);
    }

    [Fact]
    public void ToAscii_LongInput_TruncatedTo29Characters()
    {
        // 40 ASCII letters → must be cut to exactly 29.
        var input = new string('A', 40);

        var result = NameSanitizer.ToAscii(input);

        Assert.Equal(29, result.Length);
        Assert.Equal(new string('A', 29), result);
    }

    [Fact]
    public void ToAscii_ExactlyMaxLength_NotTruncated()
    {
        // Exactly 29 chars must pass through untouched (no off-by-one).
        var input = new string('B', 29);

        var result = NameSanitizer.ToAscii(input);

        Assert.Equal(29, result.Length);
        Assert.Equal(input, result);
    }

    [Fact]
    public void ToAscii_DoesNotAppendNullTerminator()
    {
        // Codec owns the null terminator. Sanitizer MUST NOT add '\0'.
        var result = NameSanitizer.ToAscii("Spotify");

        Assert.DoesNotContain('\0', result);
    }

    [Fact]
    public void ToAscii_TruncationCountedAfterSanitization()
    {
        // 35 accented chars → after NFD strip, 35 ASCII letters → truncated to 29.
        // This proves truncation runs AFTER sanitization, not on the raw input.
        var input = new string('é', 35);

        var result = NameSanitizer.ToAscii(input);

        Assert.Equal(29, result.Length);
        Assert.Equal(new string('e', 29), result);
    }

    [Fact]
    public void ToAscii_KeepsPrintableAsciiPunctuationAndSpaces()
    {
        // Spaces (0x20), digits (0x30-0x39), punctuation, and tilde (0x7E) are all
        // inside the printable range and must survive verbatim.
        var input = "App 1.0 - v2_~";

        var result = NameSanitizer.ToAscii(input);

        Assert.Equal("App 1.0 - v2_~", result);
    }

    [Fact]
    public void ToAscii_StripsControlCharacters()
    {
        // Tab (0x09), newline (0x0A), DEL (0x7F) are NOT in 0x20-0x7E → stripped.
        var input = "Hi\tThere\nDEL\u007F!";

        var result = NameSanitizer.ToAscii(input);

        Assert.Equal("HiThereDEL!", result);
    }
}
