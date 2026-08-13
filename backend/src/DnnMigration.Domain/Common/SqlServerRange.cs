namespace DnnMigration.Domain.Common;

/// <summary>
/// The representable ranges of the legacy column types this schema uses, expressed as CLR values so that
/// request validation can refuse a value the column cannot hold.
/// </summary>
/// <remarks>
/// <para>
/// This type exists because a security review found that the CLR type was being treated as a sufficient
/// bound throughout request validation, and it is not: <see cref="decimal"/> is far wider than
/// <c>money</c>, and <see cref="DateTime"/> begins nearly eight centuries before <c>datetime</c> does.
/// </para>
/// <para>
/// The values are the schema's, not this migration's, so they belong in the domain: the application layer
/// consumes them in validators, and nothing may narrow them without narrowing what the legacy application
/// could store. They are declared once for the same reason the paging arithmetic is - a bound copied into
/// each validator is a bound that comes to disagree with itself.
/// </para>
/// </remarks>
public static class SqlServerRange
{
    /// <summary>The most negative value a <c>money</c> column can hold.</summary>
    public const decimal MinimumMoney = -922_337_203_685_477.5808m;

    /// <summary>The largest value a <c>money</c> column can hold. See <see cref="MinimumMoney"/>.</summary>
    public const decimal MaximumMoney = 922_337_203_685_477.5807m;

    /// <summary>The number of fractional digits a <c>money</c> column retains.</summary>
    public const int MoneyScale = 4;

    /// <summary>Returns the value a <c>money</c> column would hold for the supplied amount.</summary>
    /// <param name="amount">The amount as it stands in memory.</param>
    /// <returns>The amount rounded to the column's four fractional digits.</returns>
    /// <remarks>
    /// WHY A DOMAIN CONCERN AND NOT AN INFRASTRUCTURE ONE. A <c>money</c> column cannot hold more than four
    /// fractional digits, so an aggregate carrying five is carrying a value the store will silently change.
    /// </remarks>
    public static decimal ToStoredMoney(decimal amount)
        => Math.Round(amount, MoneyScale, MidpointRounding.AwayFromZero);

    /// <summary>
    /// Returns the value a <c>money</c> column would hold for the supplied amount, or <see
    /// langword="null"/> when there is no amount.
    /// </summary>
    /// <param name="amount">The amount as it stands in memory, or <see langword="null"/>.</param>
    /// <returns>The rounded amount, or <see langword="null"/>.</returns>
    public static decimal? ToStoredMoney(decimal? amount)
        => amount.HasValue ? ToStoredMoney(amount.Value) : null;

    /// <summary>The earliest instant a <c>datetime</c> column can hold.</summary>
    /// <remarks>
    /// <c>datetime</c> begins on 1753-01-01, which is why the legacy absence sentinel for a date could not
    /// be <see cref="DateTime.MinValue"/> in the database: 0001-01-01 is representable in the CLR and not
    /// in the column. Any request carrying a date earlier than this is refusable on sight.
    /// </remarks>
    public static readonly DateTime MinimumDateTime = new(1753, 1, 1, 0, 0, 0, DateTimeKind.Unspecified);

    /// <summary>The latest instant a <c>datetime</c> column can hold.</summary>
    /// <remarks>
    /// The last representable value is 9999-12-31 23:59:59.997, the accuracy of <c>datetime</c> being
    /// roughly 3.33 milliseconds. The bound is stated at that instant rather than at midnight so that the
    /// preserved perpetual-expiry sentinel of 9999-12-31 is admitted rather than refused by the very rule
    /// meant to protect it.
    /// </remarks>
    public static readonly DateTime MaximumDateTime = new(9999, 12, 31, 23, 59, 59, 997, DateTimeKind.Unspecified);

    /// <summary>Reports whether an instant can be stored in a <c>datetime</c> column.</summary>
    /// <param name="value">The instant to test.</param>
    /// <returns><see langword="true"/> when the column can hold it.</returns>
    /// <remarks>
    /// The comparison is on the instant alone and ignores <see cref="DateTime.Kind"/>, because the column
    /// records no offset and carries no kind: two values that differ only in kind are the same stored
    /// value, so admitting one and refusing the other would be arbitrary.
    /// </remarks>
    public static bool CanStore(DateTime value)
        => value >= MinimumDateTime && value <= MaximumDateTime;

    /// <summary>Reports whether an amount can be stored in a <c>money</c> column.</summary>
    /// <param name="value">The amount to test.</param>
    /// <returns><see langword="true"/> when the column can hold it.</returns>
    /// <remarks>
    /// Scale is deliberately not tested. <c>money</c> holds four decimal places and the provider rounds a
    /// finer value rather than refusing it, so refusing one here would be this migration inventing a rule
    /// the legacy application did not have.
    /// </remarks>
    public static bool CanStore(decimal value)
        => value >= MinimumMoney && value <= MaximumMoney;

    /// <summary>
    /// Reports whether an optional instant can be stored in a <c>datetime</c> column, treating absence as
    /// storable.
    /// </summary>
    /// <param name="value">The instant to test, or <see langword="null"/> for an absent one.</param>
    /// <returns><see langword="true"/> when the value is absent or the column can hold it.</returns>
    /// <remarks>
    /// This overload exists so that a rule can be stated against the OPTIONAL member itself rather than
    /// against its unwrapped value, and that distinction is not cosmetic. A rule written over
    /// <c>request.StartDate!.Value</c> reports its field as <c>StartDate.Value</c>, so the name a caller
    /// reads back in a validation response stops matching the name it submitted.
    /// </remarks>
    public static bool CanStore(DateTime? value)
        => !value.HasValue || CanStore(value.Value);

    /// <summary>
    /// Reports whether an optional amount can be stored in a <c>money</c> column, treating absence as
    /// storable.
    /// </summary>
    /// <param name="value">The amount to test, or <see langword="null"/> for an absent one.</param>
    /// <returns><see langword="true"/> when the value is absent or the column can hold it.</returns>
    /// <remarks>
    /// Exists for the same reason as the optional instant overload above: it keeps a validation failure
    /// reported against the member a caller submitted rather than against its unwrapped value.
    /// </remarks>
    public static bool CanStore(decimal? value)
        => !value.HasValue || CanStore(value.Value);
}
