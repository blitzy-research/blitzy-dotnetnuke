using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace DnnMigration.Application.Common;

/// <summary>
/// Derives an optimistic-concurrency token from the values a record currently holds, so that a write can be
/// refused when the record changed after the caller read it.
/// </summary>
/// <remarks>
/// <para>
/// WHY THE TOKEN IS DERIVED RATHER THAN STORED, which is the whole design and is not a matter of taste. The
/// migration's Rule T4 makes the legacy DotNetNuke schema immutable: the entity configurations bind to the
/// table and column names the 88-script DDL chain terminates in, and no <c>ALTER TABLE</c> reaches a
/// production database from this work.
/// </para>
/// <para>
/// MIGRATION: THIS HAS NO LEGACY COUNTERPART AND IS A DELIBERATE DIVERGENCE. The legacy administration
/// screens read a record into a Web Forms page and posted it back in full with no version check of any
/// kind, so last-write-wins was legacy behaviour rather than a legacy defect.
/// </para>
/// </remarks>
public static class ConcurrencyToken
{
    /// <summary>Separates the contributing values inside the canonical string that is hashed.</summary>
    private const char MemberSeparator = '\u001F';

    /// <summary>Marks a member that carries no value, distinctly from a member that is empty.</summary>
    /// <remarks>
    /// A null description and an empty description are different states of a record, and the legacy
    /// sentinel discussion in the plan's Rule T7 is precisely about not conflating them, so the token must
    /// not conflate them either. The marker contains the separator's sibling control character for the same
    /// reason the separator was chosen: no legitimate value can reproduce it.
    /// </remarks>
    private const string NullMarker = "\u001E<null>";

    /// <summary>The number of leading hash bytes the token carries.</summary>
    /// <remarks>
    /// Sixteen bytes - 128 bits - rendered as 22 base64url characters. The token is a change detector, not
    /// a security boundary: it is neither secret nor authenticating, and a caller who forges one can do no
    /// more than it could already do by omitting it.
    /// </remarks>
    private const int TokenByteCount = 16;

    /// <summary>Computes the token for a record from the values that a write would replace.</summary>
    /// <param name="members">
    /// The record's mutable values, in a FIXED order that the caller must not vary between the read that
    /// publishes a token and the write that verifies one.
    /// </param>
    /// <returns>A stable, process-independent token.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="members"/> is <see langword="null"/>.</exception>
    /// <remarks>
    /// Stability across processes is why this hashes rather than combining <c>GetHashCode</c>: string
    /// hashing is randomised per process by default, so a token issued by one instance of the API would not
    /// verify against another, and a load-balanced deployment would refuse writes at random.
    /// </remarks>
    public static string From(params object?[] members)
    {
        ArgumentNullException.ThrowIfNull(members);

        var canonical = new StringBuilder();

        foreach (object? member in members)
        {
            if (canonical.Length > 0)
            {
                canonical.Append(MemberSeparator);
            }

            canonical.Append(Format(member));
        }

        byte[] digest = SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString()));

        // Base64Url rather than Base64: the token is carried in JSON today and may be carried in an ETag or
        // a query string tomorrow, and a value containing '+', '/' or '=' would have to be escaped there.
        return Base64UrlEncode(digest.AsSpan(0, TokenByteCount));
    }

    /// <summary>Answers whether a token supplied by a caller still matches the record's current values.</summary>
    /// <param name="supplied">
    /// The token the caller sent, or <see langword="null"/> or blank when the caller sent none.
    /// </param>
    /// <param name="current">The token computed from the record as it stands now.</param>
    /// <returns>
    /// <see langword="true"/> when the write may proceed - either the caller supplied no token, or the one
    /// it supplied still matches.
    /// </returns>
    /// <remarks>
    /// ⚠ AN ABSENT TOKEN IS PERMISSIVE, DELIBERATELY. Making it refusing would break every caller that
    /// predates the token, including the legacy-equivalent scripted clients this API replaced, and would
    /// convert an improvement into a compatibility break. A client that wants the protection asks for it by
    /// round-tripping the token it was given; the migration notes record the consequence.
    /// </remarks>
    public static bool Matches(string? supplied, string current) =>
        string.IsNullOrWhiteSpace(supplied)
        || string.Equals(supplied.Trim(), current, StringComparison.Ordinal);

    /// <summary>Renders one contributing value into its canonical form.</summary>
    /// <param name="member">The value.</param>
    /// <returns>The canonical text for the value.</returns>
    private static string Format(object? member) => member switch
    {
        null => NullMarker,
        string text => text,
        bool flag => flag ? "1" : "0",
        DateTime moment => moment.ToUniversalTime().Ticks.ToString(CultureInfo.InvariantCulture),
        DateTimeOffset moment => moment.UtcTicks.ToString(CultureInfo.InvariantCulture),
        decimal number => number.ToString("G29", CultureInfo.InvariantCulture),
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => member.ToString() ?? NullMarker,
    };

    /// <summary>Renders bytes as unpadded base64url.</summary>
    /// <param name="bytes">The bytes to render.</param>
    /// <returns>The base64url text.</returns>
    private static string Base64UrlEncode(ReadOnlySpan<byte> bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
