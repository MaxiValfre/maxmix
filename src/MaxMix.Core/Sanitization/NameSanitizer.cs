namespace MaxMix.Core.Sanitization;

using System.Globalization;
using System.Text;

/// <summary>
/// OLED-safe ASCII transliteration for session/device names.
///
/// Pipeline:
///   1. Decompose to NFD so accented characters split into base + combining mark.
///   2. Drop combining marks (Unicode category <see cref="UnicodeCategory.NonSpacingMark"/>).
///   3. Keep only printable ASCII (0x20-0x7E); replace anything else with empty.
///   4. Truncate the final string to <see cref="MaxLength"/> characters.
///
/// Rationale: the firmware renders names on a small OLED with a US-ASCII font.
/// Any byte outside 0x20-0x7E either renders as a glyph the user does not expect
/// or breaks alignment. The codec owns the 30th-byte null terminator, so this
/// utility MUST NOT append '\0' itself.
///
/// Edge cases:
///   - <c>null</c> input  → empty string (nothing to send).
///   - empty input        → empty string (nothing to send).
///   - non-empty input that fully strips → <see cref="Fallback"/> ("App") so the
///     OLED shows something instead of a blank slot.
///
/// Spec: domain "ascii-transliteration" — OLED-Safe Name Sanitization.
/// </summary>
public static class NameSanitizer
{
    /// <summary>
    /// Maximum output length in characters. The wire format reserves a 30-byte
    /// fixed-width name field; the codec writes the trailing null terminator,
    /// leaving 29 bytes for the sanitized name.
    /// </summary>
    public const int MaxLength = 29;

    /// <summary>
    /// Substituted when a non-empty input fully strips away (e.g. pure emoji or
    /// pure combining marks). Distinguishes "nothing to render" from "user gave
    /// us nothing" — only the former triggers the fallback.
    /// </summary>
    public const string Fallback = "App";

    /// <summary>
    /// Returns an ASCII-only, length-limited transliteration of <paramref name="input"/>.
    /// </summary>
    /// <param name="input">Arbitrary Unicode string, possibly null.</param>
    /// <returns>
    /// A string containing only printable ASCII (0x20-0x7E), at most
    /// <see cref="MaxLength"/> characters long. Never null.
    /// </returns>
    public static string ToAscii(string? input)
    {
        if (input is null) return string.Empty;
        if (input.Length == 0) return string.Empty;

        // 1. NFD — accented characters become base char + combining mark.
        var decomposed = input.Normalize(NormalizationForm.FormD);

        // 2 + 3. Walk the decomposed string, dropping combining marks and any
        // non-printable / non-ASCII characters in a single pass.
        var sb = new StringBuilder(decomposed.Length);
        foreach (var c in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark)
                continue;

            if (c >= 0x20 && c <= 0x7E)
                sb.Append(c);
        }

        // 4. Non-empty input that stripped to nothing → fallback. Empty input
        // already returned above, so reaching here with sb.Length == 0 means
        // the user supplied something we couldn't render.
        if (sb.Length == 0)
            return Fallback;

        // 5. Truncate. StringBuilder.ToString(0, len) avoids an extra allocation
        // when the string is already short enough we skip the copy.
        if (sb.Length > MaxLength)
            return sb.ToString(0, MaxLength);

        return sb.ToString();
    }
}
