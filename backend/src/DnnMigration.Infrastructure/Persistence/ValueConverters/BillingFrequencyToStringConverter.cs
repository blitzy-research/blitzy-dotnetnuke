using DnnMigration.Domain.Enums;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace DnnMigration.Infrastructure.Persistence.ValueConverters;

// This folder exists so that one value conversion can be shared by every column that stores the same legacy
// vocabulary, instead of each entity configuration restating it.

/// <summary>
/// Converts <see cref="BillingFrequency"/> to and from the single legacy character held by the
/// <c>char(1)</c> columns <c>dbo.Roles.BillingFrequency</c> and <c>dbo.Roles.TrialFrequency</c>.
/// </summary>
/// <remarks>
/// <b>Nulls are not routed through the expressions.</b> The converter is constructed with Entity Framework
/// Core's default null behaviour, so the framework short-circuits a null column value to a null property
/// and never invokes either expression for it.
/// </remarks>
internal sealed class BillingFrequencyToStringConverter : ValueConverter<BillingFrequency?, string?>
{
    /// <summary>
    /// The single shared instance bound by every entity configuration that maps a frequency column.
    /// </summary>
    /// <remarks>
    /// Exposed as a shared instance rather than constructed per property so that the sharing is a
    /// structural fact — the same object — rather than a convention two call sites happen to follow. The
    /// type holds no state and its expressions are immutable, so the instance is safe to reuse across every
    /// model and every thread.
    /// </remarks>
    internal static BillingFrequencyToStringConverter Instance { get; } = new BillingFrequencyToStringConverter();

    /// <summary>Initialises a new instance of the <see cref="BillingFrequencyToStringConverter"/> class.</summary>
    /// <remarks>
    /// Left accessible rather than private so that the conversion can be constructed directly in a test
    /// without reaching through <see cref="Instance"/>, which would otherwise make every test depend on the
    /// same shared object.
    /// </remarks>
    internal BillingFrequencyToStringConverter()
        : base(
            frequency => ToStoredValue(frequency),
            stored => FromStoredValue(stored))
    {
    }

    /// <summary>Maps a frequency member to the single character its column stores.</summary>
    /// <param name="frequency">The member to store, or <see langword="null"/> for an absent value.</param>
    /// <returns>
    /// A one-character string carrying the member's legacy code, or <see langword="null"/> when <paramref
    /// name="frequency"/> is <see langword="null"/>.
    /// </returns>
    /// <remarks>
    /// The member's own value IS its character, so the cast is the whole conversion and there is no lookup
    /// table to keep in step with the enumeration.
    /// </remarks>
    internal static string? ToStoredValue(BillingFrequency? frequency) =>
        frequency.HasValue ? ((char)frequency.Value).ToString() : null;

    /// <summary>Maps a single stored frequency character back to its <see cref="BillingFrequency"/> member.</summary>
    /// <param name="stored">
    /// The value read from the <c>BillingFrequency</c> or <c>TrialFrequency</c> column, which may be <see
    /// langword="null"/>, empty, or a character the enumeration does not declare.
    /// </param>
    /// <returns>
    /// The matching member, <see langword="null"/> when <paramref name="stored"/> is null or empty, or —
    /// for a character the enumeration does not declare — that character carried as an undeclared value of
    /// the enumeration, so that nothing about the stored row is lost.
    /// </returns>
    /// <remarks>
    /// <b>No <c>Enum.IsDefined</c> filter, deliberately.</b> The conversion must be lossless in both
    /// directions because a role update rewrites this column from whatever was materialised, so any
    /// normalisation applied here becomes a silent, irreversible edit to a legacy row the next time
    /// anything about that role changes.
    /// </remarks>
    internal static BillingFrequency? FromStoredValue(string? stored)
    {
        if (string.IsNullOrEmpty(stored))
        {
            return null;
        }

        // The column is char(1), so only the first character can carry meaning. Reading it positionally
        // rather than comparing the whole string also absorbs the trailing blank that a fixed-width char
        // column pads shorter values with.
        return (BillingFrequency)stored[0];
    }
}
