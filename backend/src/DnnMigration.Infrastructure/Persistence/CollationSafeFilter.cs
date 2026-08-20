namespace DnnMigration.Infrastructure.Persistence;

/// <summary>
/// Answers whether a caller's text filter can discriminate between rows at all under the collation the
/// legacy schema is bound to, so that one which cannot is made to match NOTHING rather than everything.
/// </summary>
/// <remarks>
/// <para>
/// ⚠ THIS EXISTS TO MAKE A FILTER FAIL CLOSED RATHER THAN OPEN, and the distinction is a safety one rather
/// than a nicety. The database this maps onto is collated <c>SQL_Latin1_General_CP1_CI_AS</c>, which predates
/// supplementary-character support. In that collation a supplementary character carries NO WEIGHT, so it
/// compares equal to the empty string: measured directly, <c>N'🎉🎉🎉' = N''</c> evaluates true, and
/// <c>PortalName LIKE N'🌍%'</c> therefore degrades to <c>LIKE N'%'</c> and returns EVERY ROW.
/// </para>
/// <para>
/// The observed consequence, on three separate listings, was a search that ANNOUNCED ITSELF AS FILTERED while
/// presenting the complete record set: searching the tenants for <c>🌍</c> returned every tenant, the module
/// placements for <c>🚀</c> returned all eight, and the roles for <c>🚀</c> returned all of them. That is the
/// unsafe direction to fail in: an operator who believes a listing is narrowed to one record may act on a
/// row - amend it, remove it - in the belief that it is the record they searched for. Returning no rows for a
/// filter that cannot filter is both honest and safe, and it is what a person typing characters no record
/// contains expects to see.
/// </para>
/// <para>
/// TWO REMEDIES WERE REJECTED, and the reasons are worth keeping. The column collation is NOT changed:
/// Rule T4 makes the legacy schema immutable and no <c>ALTER</c> reaches a production database from this
/// work. The predicate is NOT forced onto a supplementary-aware collation either - doing so would make it
/// non-sargable on a listing path, and would silently alter matching for all other text on every row the
/// query touches.
/// </para>
/// <para>
/// Only a filter composed ENTIRELY of weightless characters is caught. A filter mixing them with ordinary
/// text still discriminates on the ordinary part and is left exactly as it was, because the basic-plane
/// characters in it do carry weight and the pattern really does narrow the result.
/// </para>
/// <para>
/// SHARED RATHER THAN COPIED, and that is the point of the type. The rule was implemented once, on the
/// account listing, and the three listings that lacked it each shipped the open failure - so a single
/// definition is what stops the next listing shipping it again.
/// </para>
/// </remarks>
internal static class CollationSafeFilter
{
    /// <summary>
    /// Reports whether a text filter is composed entirely of characters the database collation cannot weigh,
    /// and therefore cannot filter on.
    /// </summary>
    /// <param name="filter">The trimmed filter text, in the case the caller typed it.</param>
    /// <returns>
    /// <see langword="true"/> when the filter cannot discriminate between rows and must therefore be treated
    /// as matching nothing.
    /// </returns>
    internal static bool CannotDiscriminate(string filter)
    {
        ArgumentNullException.ThrowIfNull(filter);

        if (filter.Length == 0)
        {
            return false;
        }

        foreach (char unit in filter)
        {
            // A surrogate code unit is half of a supplementary character, which is the weightless class.
            // Anything else - letter, digit, punctuation, symbol in the basic plane - carries weight and makes
            // the filter discriminating.
            if (!char.IsSurrogate(unit))
            {
                return false;
            }
        }

        return true;
    }
}
