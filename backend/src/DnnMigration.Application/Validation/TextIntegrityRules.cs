using System.Globalization;

namespace DnnMigration.Application.Validation;

/// <summary>
/// Shared rules that refuse text a record can hold but no operator can read, review or reproduce: control
/// characters, invisible formatting characters, and the bidirectional controls that make stored text render
/// as something other than itself.
/// </summary>
/// <remarks>
/// <para>
/// THIS IS A DELIBERATE ADDITION WITH NO LEGACY COUNTERPART, and it is the one place where this migration's
/// validation rules are stricter than the screens they replace rather than equal to them.
/// </para>
/// <para>
/// <b>ONE POLICY, EVERY COMPARABLE MEMBER.</b> The rule is applied by every write contract that accepts an
/// administrator-authored label or free-text block: the page contract (name, title, description, keywords,
/// head text, icon and link target), the security-role and role-group contracts (name, description, icon),
/// the module contracts (title, icon), the tenant create and settings contracts (name, footer, currency,
/// description, keywords, language, processor fields, logo, background, home directory, template file and
/// the initial administrator's names), the account contracts (login name, given name, family name, display
/// name) and the profile-definition contracts (category). Applying it to one contract and not its siblings
/// is what let a page name accept the four characters a role name refused, so the reach is stated here and
/// asserted as a policy rather than left to each validator's author to remember.
/// </para>
/// <para>
/// <b>Two members are deliberately outside the policy, and both for a reason.</b> A profile definition's
/// property NAME is already confined to an ASCII subset by its own pattern, so the rule would be
/// unreachable there. A module's header and footer carry authored HTML fragments and have never been
/// validated at all, so bringing them in would impose a brand-new rule on an unbounded member rather than
/// make two existing rules agree.
/// </para>
/// </remarks>
internal static class TextIntegrityRules
{
    /// <summary>
    /// The refusal reported for a single-line value that carries a control character, an invisible
    /// character, or a bidirectional control.
    /// </summary>
    internal const string SingleLineMessage =
        "This value contains characters that cannot be displayed, or that change the direction the "
        + "surrounding text reads in, such as control, zero-width or bidirectional characters. Retype it "
        + "using visible characters only.";

    /// <summary>
    /// The refusal reported for a multi-line value that carries a control character other than a tab or a
    /// line break, an invisible character, or a bidirectional control.
    /// </summary>
    internal const string MultiLineMessage =
        "This text contains characters that cannot be displayed, or that change the direction the "
        + "surrounding text reads in, such as control, zero-width or bidirectional characters. Retype it "
        + "using visible characters, tabs and line breaks only.";

    /// <summary>Characters that occupy no width and therefore cannot be seen or verified by an operator.</summary>
    /// <remarks>
    /// <para>
    /// U+200B zero-width space, U+200C zero-width non-joiner, U+2060 word joiner, U+FEFF byte-order mark
    /// and the U+180E Mongolian vowel separator all render as nothing in isolation and were all measurable
    /// in the stored data during testing or are the obvious substitutions for the one that was.
    /// </para>
    /// <para>
    /// The remainder are the eleven characters carrying the Unicode <c>Bidi_Control</c> property: U+061C
    /// Arabic letter mark, U+200E and U+200F the left-to-right and right-to-left marks, U+202A to U+202E
    /// the embedding, override and pop codes, and U+2066 to U+2069 the isolate and pop-isolate codes. The
    /// set is named by that property rather than assembled by hand so it is complete and so a reader can
    /// check it against the standard instead of against this author's judgement.
    /// </para>
    /// <para>
    /// <b>These are refused for a different reason from the zero-width group, and the difference matters.</b>
    /// A bidirectional control is not merely unreadable; it REORDERS the characters around it, so a stored
    /// value can render as text that is not the text that was stored. U+202E, the right-to-left override,
    /// is the well-known instance: a name submitted as one sequence of characters displays reversed, which
    /// makes a role, a page or an account presentable to an operator as something other than what an
    /// authorisation decision will actually be taken against. A value nobody can review is bad; a value
    /// that reviews as something else is worse.
    /// </para>
    /// <para>
    /// <b>U+200D, the zero-width joiner, is deliberately NOT listed.</b> It is invisible on its own, so it
    /// would otherwise qualify, but it is load-bearing inside legitimate emoji sequences - a family or a
    /// flag glyph is several code points joined by it - and this project's stored data already carries
    /// emoji in page and module titles. Refusing it would refuse text an operator can see perfectly well.
    /// </para>
    /// </remarks>
    private static readonly char[] InvisibleCharacters =
    {
        // Zero-width and no-width formatting characters.
        '\u200B', '\u200C', '\u2060', '\uFEFF', '\u180E',

        // Unicode Bidi_Control: marks, embeddings, overrides and isolates.
        '\u061C', '\u200E', '\u200F',
        '\u202A', '\u202B', '\u202C', '\u202D', '\u202E',
        '\u2066', '\u2067', '\u2068', '\u2069',
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
