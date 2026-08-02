using DnnMigration.Domain.Enums;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace DnnMigration.Infrastructure.Persistence.ValueConverters;

// MIGRATION: this folder exists so that one value conversion can be shared by every column that
// stores the same legacy vocabulary, instead of each entity configuration restating it. The
// namespace is deliberately spelled ValueConverters rather than ValueConversion so that it never
// shadows Microsoft.EntityFrameworkCore.Storage.ValueConversion, which a configuration file has to
// import alongside it.

/// <summary>
/// Converts <see cref="BillingFrequency"/> to and from the single legacy character held by the
/// <c>char(1)</c> columns <c>dbo.Roles.BillingFrequency</c> and <c>dbo.Roles.TrialFrequency</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>The one-character codes are load-bearing data, not an internal encoding.</b> The evidence is
/// that SQL branches on them: <c>04.08.00.SqlDataProvider</c> gates the trial fee, trial period and
/// trial frequency behind <c>case when R.TrialFrequency &lt;&gt; 'N'</c>, and
/// <c>Library/Components/Security/Roles/RoleController.vb</c> line 521 tests
/// <c>TrialFrequency.ToString() &lt;&gt; "N"</c>. The enumeration declares
/// <c>ushort</c> as its backing type and gives each member the code point of its own legacy
/// character, so a DEFAULT enumeration mapping would store the number — 78 for <c>'N'</c> — into a
/// one-character column, which would either fail outright or silently corrupt every row, and every
/// one of those SQL predicates would stop matching. This converter is what prevents that.
/// </para>
/// <para>
/// <b>Both columns provably share one reading.</b> <see cref="Instance"/> is the single shared
/// instance that every configuration binds, so the two <c>char(1)</c> columns cannot drift apart:
/// there is one object, one pair of expressions, and no second copy for a later change to miss. The
/// conversion previously lived inline in
/// <c>Persistence/Configurations/RoleConfiguration.cs</c> as two duplicated lambda pairs over a
/// shared private helper, which achieved the same sharing within that one file but could not extend
/// it to any other configuration, and could not be exercised without building an entire model.
/// </para>
/// <para>
/// <b>An unrecognised character does not throw.</b> AAP Rule T4 makes the existing database
/// authoritative. The schema constrains only ONE of the two columns against the legacy vocabulary
/// table — <c>FK_Roles_CodeFrequency</c> at <c>01.00.05:L2801-2808</c> covers
/// <c>BillingFrequency</c> and nothing covers <c>TrialFrequency</c> — so an arbitrary character in
/// <c>TrialFrequency</c> is a row a legacy installation already accepts. Throwing while
/// materialising it would make a legitimate row unreadable and take an entire result set with it.
/// Falling back to <see cref="BillingFrequency.None"/> degrades one field instead, which is what
/// the legacy code did in effect: it compared the raw character against the codes it knew and
/// treated anything else as no recurrence.
/// </para>
/// <para>
/// <b>This is the persistence half of the concern only.</b> The wire half — emitting and accepting
/// the same single character over JSON rather than the numeric code point — belongs to
/// <c>DnnMigration.Application.Serialization.BillingFrequencyJsonConverter</c>, because the Domain
/// layer takes no dependency on any persistence, mapping or serialisation technology and the
/// Infrastructure layer owns no wire contract. The two halves are deliberately separate types in
/// separate assemblies that happen to agree on the same six characters; the vocabulary they both
/// read is the enumeration itself, so neither can define a code the other does not know.
/// </para>
/// <para>
/// <b>The read direction is case-SENSITIVE, and deliberately unlike the wire converter.</b> A
/// stored <c>'m'</c> resolves to <see cref="BillingFrequency.None"/> here, not to
/// <see cref="BillingFrequency.Month"/>, because that is what the legacy application did: VB
/// compares strings with <c>Option Compare Binary</c> by default, so
/// <c>RoleController.vb</c>'s <c>Select Case</c> over the codes matched only the upper-case
/// spellings and treated anything else as no recurrence. SQL Server's default collation is
/// case-INSENSITIVE, so a legacy installation could and can hold <c>'m'</c> — and the schema's own
/// <c>FK_Roles_CodeFrequency</c> would accept it — yet the application never read it as a month.
/// Resolving it here would therefore CHANGE behaviour rather than preserve it. The wire converter
/// takes the opposite position and upper-cases an inbound character, because caller text is not
/// stored data and no round-trip through this column can ever produce a lower-case code.
/// </para>
/// <para>
/// <b>Nulls are not routed through the expressions.</b> The converter is constructed with Entity
/// Framework Core's default null behaviour, so the framework short-circuits a null column value to
/// a null property and never invokes either expression for it. The two conversion methods still
/// handle null correctly, because they are reachable directly and their null behaviour is part of
/// what the tests assert; relying on the framework to filter nulls would leave that behaviour
/// unstated.
/// </para>
/// </remarks>
internal sealed class BillingFrequencyToStringConverter : ValueConverter<BillingFrequency?, string?>
{
    /// <summary>
    /// The single shared instance bound by every entity configuration that maps a frequency column.
    /// </summary>
    /// <remarks>
    /// Exposed as a shared instance rather than constructed per property so that the sharing is a
    /// structural fact — the same object — rather than a convention two call sites happen to
    /// follow. The type holds no state and its expressions are immutable, so the instance is safe
    /// to reuse across every model and every thread.
    /// </remarks>
    internal static BillingFrequencyToStringConverter Instance { get; } = new BillingFrequencyToStringConverter();

    /// <summary>
    /// Initialises a new instance of the <see cref="BillingFrequencyToStringConverter"/> class.
    /// </summary>
    /// <remarks>
    /// Left accessible rather than private so that the conversion can be constructed directly in a
    /// test without reaching through <see cref="Instance"/>, which would otherwise make every test
    /// depend on the same shared object.
    /// </remarks>
    internal BillingFrequencyToStringConverter()
        : base(
            frequency => ToStoredValue(frequency),
            stored => FromStoredValue(stored))
    {
    }

    /// <summary>
    /// Maps a frequency member to the single character its column stores.
    /// </summary>
    /// <param name="frequency">The member to store, or <see langword="null"/> for an absent value.</param>
    /// <returns>
    /// A one-character string carrying the member's legacy code, or <see langword="null"/> when
    /// <paramref name="frequency"/> is <see langword="null"/>.
    /// </returns>
    /// <remarks>
    /// The member's own value IS its character, so the cast is the whole conversion and there is no
    /// lookup table to keep in step with the enumeration. A value that names no declared member is
    /// not filtered here: it cannot arise from a read, because
    /// <see cref="FromStoredValue(string?)"/> resolves every unrecognised character to
    /// <see cref="BillingFrequency.None"/>, and the database's own
    /// <c>FK_Roles_CodeFrequency</c> constraint rejects an invalid code on
    /// <c>BillingFrequency</c> at the point of the insert. Silently substituting a value here would
    /// hide a caller's mistake instead of letting the database report it.
    /// </remarks>
    internal static string? ToStoredValue(BillingFrequency? frequency) =>
        frequency.HasValue ? ((char)frequency.Value).ToString() : null;

    /// <summary>
    /// Maps a single stored frequency character back to its <see cref="BillingFrequency"/> member.
    /// </summary>
    /// <param name="stored">
    /// The value read from the <c>BillingFrequency</c> or <c>TrialFrequency</c> column, which may be
    /// <see langword="null"/>, empty, or a character the enumeration does not declare.
    /// </param>
    /// <returns>
    /// The matching member, <see langword="null"/> when <paramref name="stored"/> is null or empty,
    /// or <see cref="BillingFrequency.None"/> when the character is not one the enumeration
    /// declares.
    /// </returns>
    internal static BillingFrequency? FromStoredValue(string? stored)
    {
        if (string.IsNullOrEmpty(stored))
        {
            return null;
        }

        // The column is char(1), so only the first character can carry meaning. Reading it
        // positionally rather than comparing the whole string also absorbs the trailing blank that a
        // fixed-width char column pads shorter values with.
        BillingFrequency candidate = (BillingFrequency)stored[0];

        return Enum.IsDefined(candidate) ? candidate : BillingFrequency.None;
    }
}
