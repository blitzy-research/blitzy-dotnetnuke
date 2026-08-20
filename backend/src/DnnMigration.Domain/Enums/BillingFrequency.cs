namespace DnnMigration.Domain.Enums;

/// <summary>
/// Identifies how often a paid role is billed, and how long its trial period runs, using the
/// single-character codes DotNetNuke persists in the <c>BillingFrequency</c> and <c>TrialFrequency</c>
/// columns of <c>dbo.Roles</c>.
/// </summary>
/// <remarks>
/// Where the codes live in the terminal schema, and why this type must enforce them.
/// </remarks>
public enum BillingFrequency : ushort
{
    /// <summary>
    /// No billing frequency - legacy code <c>'N'</c>, whose lookup row reads <c>'N', 'None'</c>. A role at
    /// this frequency never expires; the legacy path assigned the null-date sentinel, which this model
    /// represents as a null expiry date.
    /// </summary>
    None = 'N',

    /// <summary>
    /// A single, one-off fee - legacy code <c>'O'</c>, whose lookup row reads <c>'O', 'One-time Fee'</c>.
    /// Access is perpetual: the legacy path assigns the far-future sentinel date 9999-12-31 instead of
    /// offsetting the current expiry, so the period is not consulted.
    /// </summary>
    OneTime = 'O',

    /// <summary>
    /// Billed every <c>period</c> days - legacy code <c>'D'</c>, whose lookup row reads <c>'D',
    /// 'Day(s)'</c>. Expiry advances by <c>AddDays(period)</c>.
    /// </summary>
    Day = 'D',

    /// <summary>
    /// Billed every <c>period</c> weeks - legacy code <c>'W'</c>, whose lookup row reads <c>'W',
    /// 'Week(s)'</c>. Expiry advances by <c>AddDays(period * 7)</c>.
    /// </summary>
    Week = 'W',

    /// <summary>
    /// Billed every <c>period</c> months - legacy code <c>'M'</c>, whose lookup row reads <c>'M',
    /// 'Month(s)'</c>. Expiry advances by <c>AddMonths(period)</c>, which preserves the legacy
    /// calendar-aware clamping when the target month is shorter than the source month.
    /// </summary>
    Month = 'M',

    /// <summary>
    /// Billed every <c>period</c> years - legacy code <c>'Y'</c>, whose lookup row reads <c>'Y',
    /// 'Year(s)'</c>. Expiry advances by <c>AddYears(period)</c>, which preserves the legacy clamping of 29
    /// February onto a non-leap year.
    /// </summary>
    Year = 'Y'
}
