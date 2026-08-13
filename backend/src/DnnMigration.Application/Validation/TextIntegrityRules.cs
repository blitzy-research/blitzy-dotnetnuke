using System.Globalization;

namespace DnnMigration.Application.Validation;

/// <summary>
/// Shared rules that refuse text a record can hold but no operator can read, review or reproduce: control
/// characters and invisible formatting characters.
/// </summary>
/// <remarks>
/// THIS IS A DELIBERATE ADDITION WITH NO LEGACY COUNTERPART, and it is the one place where this migration's
/// validation rules are stricter than the screens they replace rather than equal to them.
/// </remarks>
internal static class TextIntegrityRules
{
    /// <summary>The refusal reported for a single-line value that carries a control or invisible character.</summary>
    internal const string SingleLineMessage =
        "This value contains characters that cannot be displayed, such as control or zero-width characters. Retype it using visible characters only.";

    /// <summary>
    /// The refusal reported for a multi-line value that carries a control or invisible character other than
    /// a tab or a line break.
    /// </summary>
    internal const string MultiLineMessage =
        "This text contains characters that cannot be displayed, such as control or zero-width characters. Retype it using visible characters, tabs and line breaks only.";

    /// <summary>Characters that occupy no width and therefore cannot be seen or verified by an operator.</summary>
    /// <remarks>
    /// U+200B zero-width space, U+200C zero-width non-joiner, U+2060 word joiner, U+FEFF byte-order mark
    /// and the U+180E Mongolian vowel separator all render as nothing in isolation and were all measurable
    /// in the stored data during testing or are the obvious substitutions for the one that was.
    /// </remarks>
    private static readonly char[] InvisibleCharacters =
    {
        '\u200B', '\u200C', '\u2060', '\uFEFF', '\u180E',
    };

    /// <summary>Answers whether a one-line value carries only characters an operator can see.</summary>
    /// <param name="value">The value, which may be <see langword="null"/> or empty.</param>
    /// <returns>
    /// <see langword="true"/> when the value is absent, empty, or contains no control and no invisible
    /// character.
    /// </returns>
    internal static bool IsSingleLineSafe(string? value) => IsSafe(value, admitLineBreaks: false);

    /// <summary>
    /// Answers whether a multi-line value carries only characters an operator can see, admitting tabs and
    /// line breaks.
    /// </summary>
    /// <param name="value">The value, which may be <see langword="null"/> or empty.</param>
    /// <returns>
    /// <see langword="true"/> when the value is absent, empty, or contains no control character other than
    /// a tab or line break and no invisible character.
    /// </returns>
    internal static bool IsMultiLineSafe(string? value) => IsSafe(value, admitLineBreaks: true);

    /// <summary>Applies the shared test.</summary>
    /// <param name="value">The value under test.</param>
    /// <param name="admitLineBreaks">
    /// Whether a tab, carriage return and line feed count as content rather than as control characters.
    /// </param>
    /// <returns><see langword="true"/> when the value carries nothing invisible.</returns>
    private static bool IsSafe(string? value, bool admitLineBreaks)
    {
        if (string.IsNullOrEmpty(value))
        {
            return true;
        }

        foreach (char character in value)
        {
            if (admitLineBreaks && (character is '\t' or '\r' or '\n'))
            {
                continue;
            }

            // char.IsControl covers C0 (U+0000-U+001F, U+007F) and C1 (U+0080-U+009F), which is exactly the
            // range whose members display as nothing or as a replacement glyph. The NUL byte measured in
            // stored data is the first of them.
            if (char.IsControl(character))
            {
                return false;
            }

            if (Array.IndexOf(InvisibleCharacters, character) >= 0)
            {
                return false;
            }
        }

        return true;
    }
}
