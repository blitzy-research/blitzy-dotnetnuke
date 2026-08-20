using System.Diagnostics.CodeAnalysis;
using DnnMigration.Domain.Common;

namespace DnnMigration.Domain.ValueObjects;

/// <summary>
/// An e-mail address whose shape has been checked against the single rule DotNetNuke 4.9.0 applied, and
/// which is therefore safe to pass around without being re-checked.
/// </summary>
/// <remarks>
/// <para>
/// This type validates SHAPE ONLY. It never asserts that the address is unclaimed, that its domain
/// resolves, or that a message sent to it would arrive, and it applies no rule the legacy system did not
/// apply.
/// </para>
/// <para>
/// There are two ways in, and choosing between them is a design decision rather than a matter of taste.
/// <see cref="Create"/> throws when the value is malformed and suits a caller that is asserting an
/// invariant - code that already knows the string is an address and would be defective if it were not. <see
/// cref="TryCreate"/> reports failure as a return value and suits untrusted input and, critically, existing
/// rows: DotNetNuke ships accounts whose stored address does not satisfy its own validator, so a reader
/// that could only throw would be unable to load them at all.
/// </para>
/// </remarks>
public sealed record EmailAddress : IEquatable<EmailAddress>
{
    /// <summary>
    /// The greatest number of characters an address may contain, taken from the terminal <c>Email
    /// nvarchar(256) NULL</c> column of <c>dbo.Users</c> as added at
    /// <c>03.00.13.SqlDataProvider:L109-110</c>.
    /// </summary>
    /// <remarks>
    /// See 5 above. The column is the authority for this number, not this file, and the number is the width
    /// of the column that exists TODAY rather than the <c>nvarchar(100)</c> one that the nine-column drop
    /// at <c>02.02.01:L50-51</c> removed.
    /// </remarks>
    private const int MaximumLength = 256;

    /// <summary>The fewest characters the final domain label may contain.</summary>
    private const int MinimumFinalLabelLength = 2;

    /// <summary>The greatest number of characters any single domain label may contain.</summary>
    /// <remarks>
    /// Sixty-three, the label limit of RFC 1035. See 3 above: this replaces the legacy four-character cap
    /// on the FINAL label, which refused every modern top-level domain longer than four letters, and it
    /// applies to every label rather than only the last so that the scan stays bounded without relying on
    /// the overall address length.
    /// </remarks>
    private const int MaximumLabelLength = 63;

    /// <summary>The greatest number of characters the whole domain may contain.</summary>
    /// <remarks>
    /// Two hundred and fifty-three, the fully-qualified-name limit of RFC 1035. It is stated independently
    /// of <see cref="MaximumLength"/> because the two bound different things: one is what the storage
    /// column holds, the other is what can resolve.
    /// </remarks>
    private const int MaximumDomainLength = 253;

    /// <summary>
    /// Initialises a new instance from a value that has ALREADY been normalised and validated. Private by
    /// design: every route in validates first, so no instance of this type can exist without having
    /// satisfied the legacy rule.
    /// </summary>
    /// <param name="value">The trimmed, validated address.</param>
    private EmailAddress(string value)
    {
        Value = value;
    }

    /// <summary>
    /// Gets the address as it will be stored: trimmed of surrounding whitespace, with the letter case
    /// exactly as it was supplied.
    /// </summary>
    public string Value { get; }

    /// <summary>
    /// Creates an address from <paramref name="value"/>, throwing when the value does not satisfy the
    /// legacy shape rule.
    /// </summary>
    /// <param name="value">The candidate address.</param>
    /// <returns>A validated address.</returns>
    /// <exception cref="DomainException">
    /// Thrown when the value is absent, empty, whitespace only, longer than the column permits, or
    /// malformed in any way the legacy rule rejected.
    /// </exception>
    public static EmailAddress Create(string value)
    {
        string candidate = Normalise(value);
        string? violation = DescribeViolation(candidate);

        if (violation is not null)
        {
            throw new DomainException(violation);
        }

        return new EmailAddress(candidate);
    }

    /// <summary>
    /// Attempts to create an address from <paramref name="value"/>, reporting failure as a return value
    /// instead of throwing.
    /// </summary>
    /// <param name="value">The candidate address, which may be null.</param>
    /// <param name="result">
    /// When this method returns <see langword="true"/>, the validated address; when it returns <see
    /// langword="false"/>, <see langword="null"/>.
    /// </param>
    /// <returns>
    /// <see langword="true"/> when the value satisfies the legacy shape rule; otherwise <see
    /// langword="false"/>.
    /// </returns>
    public static bool TryCreate(string? value, [NotNullWhen(true)] out EmailAddress? result)
    {
        string candidate = Normalise(value);

        if (DescribeViolation(candidate) is not null)
        {
            result = null;
            return false;
        }

        result = new EmailAddress(candidate);
        return true;
    }

    /// <summary>
    /// Determines whether this address and <paramref name="other"/> denote the same address, ignoring
    /// differences of letter case.
    /// </summary>
    /// <param name="other">The address to compare with, which may be null.</param>
    /// <returns>
    /// <see langword="true"/> when both denote the same address; otherwise <see langword="false"/>.
    /// </returns>
    public bool Equals(EmailAddress? other) =>
        other is not null && string.Equals(Value, other.Value, StringComparison.OrdinalIgnoreCase);

    /// <summary>Returns a hash code consistent with <see cref="Equals(EmailAddress)"/>.</summary>
    /// <returns>A hash code computed with letter case folded.</returns>
    /// <remarks>
    /// The comparer used here is the counterpart of the comparison used by <see
    /// cref="Equals(EmailAddress)"/>, so two addresses that differ only in case hash alike and therefore
    /// collide correctly in a set or a dictionary. Changing one of the two without the other is a defect no
    /// compiler reports.
    /// </remarks>
    public override int GetHashCode() => StringComparer.OrdinalIgnoreCase.GetHashCode(Value);

    /// <summary>Returns the address itself.</summary>
    /// <returns>The stored address, trimmed and in its original letter case.</returns>
    /// <remarks>
    /// The value is returned unmasked and untruncated on purpose. Keeping sensitive values away from a log
    /// is the responsibility of the request-logging middleware at the edge of the process, which can see
    /// what is being written and where; a domain type that silently redacted itself would instead corrupt
    /// every legitimate use, including persistence and comparison during debugging.
    /// </remarks>
    public override string ToString() => Value;

    /// <summary>
    /// Converts an address to its string form. Deliberately explicit, for symmetry with the conversion in
    /// the other direction.
    /// </summary>
    /// <param name="address">The address to convert.</param>
    /// <returns>The stored address.</returns>
    public static explicit operator string(EmailAddress address) => address.Value;

    /// <summary>Converts a string to an address, validating it on the way.</summary>
    /// <param name="value">The candidate address.</param>
    /// <returns>A validated address.</returns>
    /// <exception cref="DomainException">Thrown when the value does not satisfy the legacy shape rule.</exception>
    public static explicit operator EmailAddress(string value) => Create(value);

    /// <summary>
    /// Trims the candidate, mapping a null argument onto the empty string so that both are rejected
    /// identically. See 8 above for why that mapping is faithful rather than merely convenient.
    /// </summary>
    /// <param name="value">The raw candidate, which may be null.</param>
    /// <returns>The trimmed candidate, or the empty string when the argument was null.</returns>
    private static string Normalise(string? value) => value is null ? string.Empty : value.Trim();

    /// <summary>
    /// Applies the legacy shape rule to an already-trimmed candidate and describes the first clause it
    /// fails, or returns <see langword="null"/> when it satisfies every clause.
    /// </summary>
    /// <param name="candidate">The trimmed candidate.</param>
    /// <returns>
    /// A description of the failed clause, or <see langword="null"/> when the candidate is well formed.
    /// </returns>
    /// <remarks>
    /// <b>With two documented exceptions, and only two.</b> The final label's four-character ceiling is
    /// replaced by the RFC 1035 label limit, and the domain acquires per-label and whole-domain bounds the
    /// legacy pattern did not have. Both are set out in MIGRATION 3 with their consequences in both
    /// directions, and both are recorded in <c>MIGRATION_NOTES.md</c>.
    /// </remarks>
    private static string? DescribeViolation(ReadOnlySpan<char> candidate)
    {
        // The value must exist at all. A null argument arrives here as the empty string, and a
        // whitespace-only argument has already been trimmed away to the same thing, so one message covers
        // all three inputs honestly - which is also how the legacy sentinel module saw them.
        if (candidate.IsEmpty)
        {
            return "An e-mail address must be supplied: the value was absent, empty, or whitespace only.";
        }

        if (candidate.Length > MaximumLength)
        {
            return "An e-mail address must be no longer than 256 characters, the width of the column that stores it.";
        }

        // The '@' of the pattern. Because neither character class contains an at-sign, a
        // value matching the pattern holds exactly one, so both absence and repetition fail.
        int atIndex = candidate.IndexOf('@');

        if (atIndex < 0)
        {
            return "An e-mail address must contain an at-sign separating the local part from the domain.";
        }

        ReadOnlySpan<char> localPart = candidate[..atIndex];
        ReadOnlySpan<char> domain = candidate[(atIndex + 1)..];

        if (domain.Contains('@'))
        {
            return "An e-mail address must contain exactly one at-sign.";
        }

        // The '+' quantifier on [a-zA-Z0-9._%\-+'] requires at least one character.
        if (localPart.IsEmpty)
        {
            return "An e-mail address must have a local part before the at-sign.";
        }

        // The LEADING \b. This clause exists only because the match had to begin at offset zero, which \b
        // permits solely when the first character is a word character.
        if (!IsWordCharacter(localPart[0]))
        {
            return "An e-mail address must begin with a letter, a digit, or an underscore.";
        }

        foreach (char character in localPart)
        {
            if (!IsLocalPartCharacter(character))
            {
                return "The local part of an e-mail address may contain only letters, digits, and the characters dot, underscore, percent, hyphen, plus, and apostrophe.";
            }
        }

        // The '+' quantifier on [a-zA-Z0-9.\-] requires at least one character.
        if (domain.IsEmpty)
        {
            return "An e-mail address must have a domain after the at-sign.";
        }

        foreach (char character in domain)
        {
            if (!IsDomainCharacter(character))
            {
                return "The domain of an e-mail address may contain only letters, digits, dots, and hyphens.";
            }
        }

        if (domain.Length > MaximumDomainLength)
        {
            return "The domain of an e-mail address must be no longer than 253 characters.";
        }

        // The literal \. before the final label, plus the '+' quantifier on the label preceding it.
        int lastDotIndex = domain.LastIndexOf('.');

        if (lastDotIndex < 1)
        {
            return "The domain of an e-mail address must contain a dot with at least one character before it.";
        }

        // Per-label bounds. DELIBERATE DIVERGENCE - see MIGRATION 3.
        int labelStart = 0;

        for (int index = 0; index <= lastDotIndex; index++)
        {
            if (index < lastDotIndex && domain[index] != '.')
            {
                continue;
            }

            int labelLength = index - labelStart;

            if (labelLength < 1)
            {
                return "Every part of an e-mail address domain must contain at least one character.";
            }

            if (labelLength > MaximumLabelLength)
            {
                return "No part of an e-mail address domain may be longer than 63 characters.";
            }

            labelStart = index + 1;
        }

        ReadOnlySpan<char> finalLabel = domain[(lastDotIndex + 1)..];

        if (finalLabel.Length < MinimumFinalLabelLength)
        {
            return "The final part of an e-mail address domain must be at least 2 characters long.";
        }

        if (finalLabel.Length > MaximumLabelLength)
        {
            return "The final part of an e-mail address domain must be no longer than 63 characters.";
        }

        foreach (char character in finalLabel)
        {
            if (!char.IsAsciiLetter(character))
            {
                return "The final part of an e-mail address domain may contain only letters.";
            }
        }

        return null;
    }

    /// <summary>
    /// Reports whether a character is admitted by the legacy local-part class <c>[a-zA-Z0-9._%\-+']</c>.
    /// </summary>
    /// <param name="character">The character to test.</param>
    /// <returns><see langword="true"/> when the character is admitted; otherwise <see langword="false"/>.</returns>
    private static bool IsLocalPartCharacter(char character) =>
        char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '%' or '-' or '+' or '\'';

    /// <summary>Reports whether a character is admitted by the legacy domain class <c>[a-zA-Z0-9.\-]</c>.</summary>
    /// <param name="character">The character to test.</param>
    /// <returns><see langword="true"/> when the character is admitted; otherwise <see langword="false"/>.</returns>
    private static bool IsDomainCharacter(char character) =>
        char.IsAsciiLetterOrDigit(character) || character is '.' or '-';

    /// <summary>
    /// Reports whether a character is a word character in the sense the legacy pattern's leading <c>\b</c>
    /// assertion used, namely <c>[A-Za-z0-9_]</c>.
    /// </summary>
    /// <param name="character">The character to test.</param>
    /// <returns>
    /// <see langword="true"/> when the character is a word character; otherwise <see langword="false"/>.
    /// </returns>
    private static bool IsWordCharacter(char character) =>
        char.IsAsciiLetterOrDigit(character) || character == '_';
}
