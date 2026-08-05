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
/// <b>An unrecognised character neither throws nor is discarded — it is CARRIED.</b> AAP Rule T4
/// makes the existing database authoritative, and the database really does hold characters outside
/// the vocabulary: the installation seed inserts the Administrators role with
/// <c>BillingFrequency = '4'</c> and the Registered Users role with <c>'0'</c>
/// (<c>01.00.00.SqlDataProvider</c> L7192 and L7194), and the only constraint that ever policed the
/// column — <c>FK_Roles_CodeFrequency</c>, <c>01.00.05:L2801-2808</c>, which covered
/// <c>BillingFrequency</c> and never <c>TrialFrequency</c> — is dropped for good at
/// <c>03.00.01:L1297</c> with nothing in its place. So every installation ships two such rows, and
/// nothing prevents more.
/// </para>
/// <para>
/// Throwing while materialising one would make a legitimate row unreadable and take an entire
/// result set with it, so that is not done. Neither, however, is the character NORMALISED: an
/// earlier revision resolved anything undeclared to <see cref="BillingFrequency.None"/>, and
/// because a role update rewrites the column from the materialised value, an edit to something
/// entirely unrelated — a description — rewrote a stored <c>'4'</c> as <c>'N'</c> and destroyed
/// authoritative legacy data that nothing in the target had any business changing. The read is
/// therefore LOSSLESS: the enumeration declares <c>ushort</c> as its backing type and gives each
/// member the code point of its own character, so <c>(BillingFrequency)stored[0]</c> represents ANY
/// character exactly and <see cref="ToStoredValue"/> casts the identical character back. A
/// read-modify-write round trip is byte-for-byte, which is precisely what Rule T4 requires of a
/// schema this migration does not own.
/// </para>
/// <para>
/// <b>Carrying the character preserves the legacy READING of it as well.</b> The legacy
/// <c>Select Case</c> over the codes had no <c>Case Else</c>
/// (<c>RoleController.vb</c> L537-L548), so an unrecognised frequency matched no arm and left the
/// expiry date untouched — and the trial test at L521 was
/// <c>TrialFrequency.ToString() &lt;&gt; "N"</c>, which an unrecognised character SATISFIES. Both
/// behaviours survive only because the character survives: <c>RoleService</c>'s expiry switch ends
/// in a default arm that leaves the date as it found it, and its trial predicate compares against
/// <see cref="BillingFrequency.None"/> rather than testing for declaredness. Normalising <c>'4'</c>
/// to <c>None</c> silently flipped that trial predicate, so the lossy fallback was not even
/// behaviour-preserving in the direction it was chosen for.
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
/// stored <c>'m'</c> is carried as the undeclared value <c>'m'</c> here, and is NOT resolved to
/// <see cref="BillingFrequency.Month"/>, because that is what the legacy application did: VB
/// compares strings with <c>Option Compare Binary</c> by default, so
/// <c>RoleController.vb</c>'s <c>Select Case</c> over the codes matched only the upper-case
/// spellings and treated anything else as no recurrence. SQL Server's default collation is
/// case-INSENSITIVE, so a legacy installation could and can hold <c>'m'</c> — and the schema's own
/// <c>FK_Roles_CodeFrequency</c> would accept it — yet the application never read it as a month.
/// Up-casing it here would therefore CHANGE behaviour rather than preserve it, and would also
/// rewrite the stored byte on the next update. The wire converter takes the opposite position and
/// upper-cases an inbound character, because caller text is not stored data and no round trip
/// through this column can ever produce a lower-case code.
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
    /// deliberately NOT filtered here, and that is what makes the pair lossless: it is exactly what
    /// <see cref="FromStoredValue(string?)"/> produces for a character a legacy installation
    /// already stores, so writing it back reproduces the byte that was read. Substituting a declared
    /// member instead would silently rewrite authoritative legacy data on any update, which Rule T4
    /// forbids, and would equally hide a caller's mistake rather than letting the database report
    /// it — the vocabulary a caller may SUBMIT is policed at the wire boundary and by the request
    /// validators, which is where a caller's input belongs.
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
    /// or — for a character the enumeration does not declare — that character carried as an
    /// undeclared value of the enumeration, so that nothing about the stored row is lost.
    /// </returns>
    /// <remarks>
    /// <para>
    /// <b>No <c>Enum.IsDefined</c> filter, deliberately.</b> The conversion must be lossless in both
    /// directions because a role update rewrites this column from whatever was materialised, so any
    /// normalisation applied here becomes a silent, irreversible edit to a legacy row the next time
    /// anything about that role changes. The two roles every DotNetNuke installation ships with
    /// store <c>'4'</c> and <c>'0'</c> (<c>01.00.00.SqlDataProvider</c> L7192 and L7194), so this is
    /// the ordinary case rather than a hypothetical one.
    /// </para>
    /// <para>
    /// Carrying an undeclared value is safe rather than merely convenient, and each consumer was
    /// checked: <c>RoleService</c>'s expiry switch ends in a default arm that leaves the date
    /// untouched, reproducing the legacy <c>Select Case</c> that had no <c>Case Else</c>; its trial
    /// predicate tests <c>!= None</c>, reproducing the legacy
    /// <c>TrialFrequency.ToString() &lt;&gt; "N"</c>; the wire converter writes the character rather
    /// than refusing it; and the request validators keep the vocabulary a CALLER may submit closed.
    /// </para>
    /// </remarks>
    internal static BillingFrequency? FromStoredValue(string? stored)
    {
        if (string.IsNullOrEmpty(stored))
        {
            return null;
        }

        // The column is char(1), so only the first character can carry meaning. Reading it
        // positionally rather than comparing the whole string also absorbs the trailing blank that a
        // fixed-width char column pads shorter values with.
        return (BillingFrequency)stored[0];
    }
}
