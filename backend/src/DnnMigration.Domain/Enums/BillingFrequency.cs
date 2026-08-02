namespace DnnMigration.Domain.Enums;

/// <summary>
/// Identifies how often a paid role is billed, and how long its trial period runs, using the
/// single-character codes DotNetNuke persists in the <c>BillingFrequency</c> and
/// <c>TrialFrequency</c> columns of <c>dbo.Roles</c>.
/// </summary>
/// <remarks>
/// <para>
/// The character values are the contract, not the member names. Each member's underlying value is
/// the code point of its legacy <c>char(1)</c> code, so <c>(char)BillingFrequency.Month</c> is
/// <c>'M'</c> and a member converts to and from the persisted character without loss. Those six
/// characters are the literal bytes already sitting in the two role columns of every existing
/// database, so renaming or renumbering a member would not fail a build - it would silently
/// mis-read live rows. The identifiers are idiomatic C# spellings adopted purely for readability.
/// </para>
/// <para>
/// Where the codes live in the terminal schema, and why this type must enforce them. The codes
/// began as the key of a dedicated <c>dbo.CodeFrequency</c> lookup table with a foreign key from
/// the billing column, and both are gone by the end of the destructive 88-script chain:
/// <c>03.00.01.SqlDataProvider</c> copies the six rows into the generic <c>Lists</c> table, then
/// drops the foreign key, the lookup table and both accessor procedures, and nothing recreates
/// them. A code's terminal home is a <c>Lists</c> row whose <c>ListName</c> is <c>'Frequency'</c>.
/// Consequently the terminal columns carry no foreign key and no check constraint: the database
/// accepts any single character, so confining the two columns to these six members is the
/// application's responsibility and restores the guarantee the schema stopped providing.
/// </para>
/// <para>
/// One type serves both columns, and there is deliberately no separate trial-frequency type: the
/// schema joins the same frequency list twice from a single role row, once per column, in both the
/// early and the terminal era. Both columns are <c>char(1) NULL</c>; that nullability belongs to
/// the consuming entity property, declared <c>BillingFrequency?</c>, and never to this member list,
/// so no member stands in for a null.
/// </para>
/// <para>
/// Warning to a future reader of the DDL. The baseline script seeds the lookup table with numeric
/// codes <c>'0'</c> through <c>'5'</c> in upper-case, bracketed, <c>dbo.</c>-qualified SQL;
/// <c>01.00.08.SqlDataProvider</c> then inserts the letter codes, rewrites both role columns onto
/// them and deletes each digit row, in lower-case unqualified SQL, and no later script re-seeds the
/// table. A case-sensitive search for one naming form therefore finds only the dead numerics and
/// yields a completely wrong code set: any inspection of this chain must be case-insensitive and
/// must cover all four naming forms it uses - bare, <c>dbo.</c>-qualified,
/// <c>[dbo].[...]</c>-bracketed and <c>{databaseOwner}{objectQualifier}</c>-templated.
/// </para>
/// <para>
/// This type carries the codes only. Role-expiry arithmetic belongs to the Application layer, which
/// is obliged to reproduce the legacy outcome per member exactly as each member below documents,
/// and to short-circuit to no expiry when the period equals the legacy null-integer sentinel,
/// before the code is examined at all.
/// </para>
/// </remarks>
// MIGRATION: the legacy DateAdd(DateInterval.Day/Month/Year, ...) calls supplied by the
// Microsoft.VisualBasic runtime - imported at Library/Components/Security/Roles/RoleController.vb
// L25 and applied in the L540-L547 Select Case - become DateTime.AddDays/AddMonths/AddYears in the
// Application layer. The legacy columns were surfaced as System.String properties carrying
// serialisation and editor attributes (RoleInfo.vb L149 and L188); replacing that stringly-typed
// pair with this enum is the purpose of this file, and those attributes are deliberately not
// carried across because the Domain layer takes no dependency capable of expressing either the
// wire contract or the column mapping.
public enum BillingFrequency : ushort
{
    /// <summary>
    /// No billing frequency - legacy code <c>'N'</c>, whose lookup row reads <c>'N', 'None'</c>. A
    /// role at this frequency never expires; the legacy path assigned the null-date sentinel, which
    /// this model represents as a null expiry date.
    /// </summary>
    /// <remarks>
    /// This is a real legacy code, not an unset or default marker, and it carries a second,
    /// independent responsibility: it is the no-trial guard. The legacy role-assignment path tests
    /// <c>role.TrialFrequency.ToString() &lt;&gt; "N"</c> to decide whether the trial period or the
    /// billing period governs expiry (<c>RoleController.vb</c> L521-L527), and the terminal SQL
    /// applies the same test when projecting a role, gating the trial fee, period and frequency
    /// behind <c>case when R.TrialFrequency &lt;&gt; 'N'</c>. Dropping this member would silently
    /// break trial-period selection in both layers.
    /// </remarks>
    None = 'N',

    /// <summary>
    /// A single, one-off fee - legacy code <c>'O'</c>, whose lookup row reads
    /// <c>'O', 'One-time Fee'</c>. Access is perpetual: the legacy path assigns
    /// the far-future sentinel date 9999-12-31 instead of offsetting the current
    /// expiry, so the period is not consulted.
    /// </summary>
    OneTime = 'O',

    /// <summary>
    /// Billed every <c>period</c> days - legacy code <c>'D'</c>, whose lookup row
    /// reads <c>'D', 'Day(s)'</c>. Expiry advances by <c>AddDays(period)</c>.
    /// </summary>
    Day = 'D',

    /// <summary>
    /// Billed every <c>period</c> weeks - legacy code <c>'W'</c>, whose lookup
    /// row reads <c>'W', 'Week(s)'</c>. Expiry advances by
    /// <c>AddDays(period * 7)</c>. The legacy path deliberately used a day
    /// interval multiplied by seven rather than any week interval, so the
    /// migrated arithmetic must multiply and add days to stay equivalent.
    /// </summary>
    Week = 'W',

    /// <summary>
    /// Billed every <c>period</c> months - legacy code <c>'M'</c>, whose lookup
    /// row reads <c>'M', 'Month(s)'</c>. Expiry advances by
    /// <c>AddMonths(period)</c>, which preserves the legacy calendar-aware
    /// clamping when the target month is shorter than the source month.
    /// </summary>
    Month = 'M',

    /// <summary>
    /// Billed every <c>period</c> years - legacy code <c>'Y'</c>, whose lookup
    /// row reads <c>'Y', 'Year(s)'</c>. Expiry advances by
    /// <c>AddYears(period)</c>, which preserves the legacy clamping of 29
    /// February onto a non-leap year.
    /// </summary>
    Year = 'Y'
}
