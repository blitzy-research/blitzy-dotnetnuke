using System.Globalization;

namespace DnnMigration.Application.Validation;

/// <summary>
/// Shared rules that refuse text a record can hold but no operator can read, review or reproduce:
/// control characters and invisible formatting characters.
/// </summary>
/// <remarks>
/// <para>
/// WHY THIS EXISTS, measured rather than assumed. Runtime testing of the migrated administration console
/// established two things about names and descriptions. A NUL byte (U+0000) submitted in a role name was
/// stored and served back verbatim, so the record carried a character that terminates a string in most
/// consumers of this data and displays as nothing in all of them. Zero-width characters (U+200B and its
/// family) were likewise preserved, which makes two records with visibly identical names distinct rows -
/// an invisible-character homograph - and makes the duplicate-name rule that guards this very field
/// unenforceable by inspection. Neither value can be typed deliberately, read back, or told apart from its
/// clean equivalent by the operator who has to administer it.
/// </para>
/// <para>
/// MIGRATION: THIS IS A DELIBERATE ADDITION WITH NO LEGACY COUNTERPART, and it is the one place where this
/// migration's validation rules are stricter than the screens they replace rather than equal to them. The
/// legacy administration screens validated these fields with <c>RequiredFieldValidator</c> and
/// <c>RegularExpressionValidator</c> controls that tested presence and length only, so any character the
/// column could store was accepted. The addition is justified on data-integrity grounds and recorded in
/// <c>MIGRATION_NOTES.md</c> as a divergence, per Rule T5. It is narrow on purpose: it refuses characters
/// that carry no visible content and nothing else. Every printable character the legacy screens accepted -
/// including the full supplementary plane, combining marks, right-to-left text and emoji - is still
/// accepted here, because those are legible values an operator can choose on purpose.
/// </para>
/// <para>
/// The distinction between the two predicates is the shape of the field, not its importance. A name occupies
/// one line, so a line break in it is a control character like any other. A description is a text area whose
/// line breaks are content the operator typed, so those three characters - tab, carriage return and line
/// feed - are admitted there and nowhere else.
/// </para>
/// </remarks>
internal static class TextIntegrityRules
{
    /// <summary>
    /// The refusal reported for a single-line value that carries a control or invisible character.
    /// </summary>
    /// <remarks>
    /// Phrased for an operator who cannot see the offending character, which is the whole difficulty: it
    /// names what to do (retype the value) rather than describing a code point they have no way to locate.
    /// </remarks>
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
    /// <para>
    /// Enumerated rather than derived from <see cref="UnicodeCategory.Format"/> wholesale, because that
    /// category also contains characters that DO carry meaning an operator chose - most importantly the
    /// zero-width joiner (U+200D), which composes emoji sequences such as a family or a profession, and the
    /// bidirectional marks a right-to-left name legitimately needs. The joiner is therefore ADMITTED and only
    /// the characters that are invisible on their own are refused.
    /// </para>
    /// <para>
    /// U+200B zero-width space, U+200C zero-width non-joiner, U+2060 word joiner, U+FEFF byte-order mark and
    /// the U+180E Mongolian vowel separator all render as nothing in isolation and were all measurable in the
    /// stored data during testing or are the obvious substitutions for the one that was.
    /// </para>
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
    /// <remarks>
    /// Absence and emptiness pass: whether the field is required is a different rule, declared separately, and
    /// answering "contains unreadable characters" for an empty field would be false and confusing.
    /// </remarks>
    internal static bool IsSingleLineSafe(string? value) => IsSafe(value, admitLineBreaks: false);

    /// <summary>
    /// Answers whether a multi-line value carries only characters an operator can see, admitting tabs and
    /// line breaks.
    /// </summary>
    /// <param name="value">The value, which may be <see langword="null"/> or empty.</param>
    /// <returns>
    /// <see langword="true"/> when the value is absent, empty, or contains no control character other than a
    /// tab or line break and no invisible character.
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
