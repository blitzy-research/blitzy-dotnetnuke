namespace DnnMigration.Domain.Enums;

/// <summary>
/// Identifies how often a paid role is billed, and how long its trial period
/// runs, using the single-character codes that DotNetNuke persists in the
/// <c>BillingFrequency</c> and <c>TrialFrequency</c> columns of <c>dbo.Roles</c>.
/// </summary>
/// <remarks>
/// <para>
/// THE CHARACTER VALUES ARE THE CONTRACT, NOT THE MEMBER NAMES. Each member's
/// underlying value is the code point of its legacy <c>char(1)</c> code, so a
/// member converts to and from the persisted character without loss:
/// <c>(char)BillingFrequency.Month</c> is <c>'M'</c>. Those six characters are the
/// literal bytes already sitting in the two role columns of every existing
/// DotNetNuke database, and the values the terminal schema joins against to
/// resolve a display description, which makes them load-bearing data rather than
/// an internal numbering this application is free to choose. Renaming or
/// renumbering one would not fail a build; it would silently mis-read live rows.
/// The member identifiers are idiomatic C# spellings adopted purely for
/// readability.
/// </para>
/// <para>
/// SCHEMA AUTHORITY, AND WHERE THE CODES ACTUALLY LIVE IN THE TERMINAL SCHEMA.
/// Only the terminal state of the 88-script DDL chain is meaningful, because that
/// chain is destructive. The codes began as the key of a dedicated
/// <c>dbo.CodeFrequency</c> lookup table, declared
/// <c>[Code] [char] (1) NOT NULL</c> in <c>01.00.00.SqlDataProvider</c> and
/// enforced against the billing column by <c>FK_Roles_CodeFrequency</c>. BOTH ARE
/// GONE by the end of the chain: <c>03.00.01.SqlDataProvider</c> copies the six
/// rows into the generic <c>Lists</c> table via
/// <c>SELECT 'Frequency', Code, Description FROM CodeFrequency</c>, then drops the
/// foreign key, drops the lookup table, and drops both
/// <c>GetBillingFrequencyCode</c> accessor procedures. Nothing recreates any of
/// them. The terminal home of a code is therefore a <c>Lists</c> row whose
/// <c>ListName</c> is <c>'Frequency'</c>, whose <c>Value</c> is the character and
/// whose <c>Text</c> is the description - joined in
/// <c>04.08.00.SqlDataProvider</c> as
/// <c>LEFT OUTER JOIN Lists L1 ON R.BillingFrequency = L1.Value AND L1.ListName='Frequency'</c>.
/// </para>
/// <para>
/// THE PRACTICAL CONSEQUENCE. Because that foreign key was dropped and never
/// replaced, the terminal <c>Roles.BillingFrequency</c> and
/// <c>Roles.TrialFrequency</c> columns carry no foreign key and no check
/// constraint: the database will accept any single character and will not reject
/// an out-of-set one. Validity is the application's responsibility, and this type
/// is what supplies it. Confining the two columns to these six members restores
/// the guarantee the schema itself stopped providing when it folded the lookup
/// table into a generic list.
/// </para>
/// <para>
/// ONE ENUM SERVES BOTH COLUMNS, AND THERE IS DELIBERATELY NO SEPARATE TRIAL
/// FREQUENCY TYPE. The schema joins one frequency lookup twice from a single role
/// row, and does so in BOTH eras: early on as
/// <c>join CodeFrequency C1 on Roles.BillingFrequency = C1.Code</c> alongside
/// <c>left outer join CodeFrequency C2 on Roles.TrialFrequency = C2.Code</c>, and
/// terminally as two <c>Lists</c> joins on the same <c>'Frequency'</c> list, one
/// per column. The legacy property documentation likewise lists the same six codes
/// against each of the two properties. Both columns therefore draw from one shared
/// code set, which is why one type covers them both. Both columns are
/// <c>char(1) NULL</c>; that nullability belongs to the consuming entity
/// property, which is declared as a nullable <c>BillingFrequency?</c>, and never
/// to this member list. No member is added here to stand in for a null.
/// </para>
/// <para>
/// WARNING TO A FUTURE READER OF THE DDL. The baseline script
/// <c>01.00.00.SqlDataProvider</c> seeds the lookup table with NUMERIC codes
/// <c>'0'</c> through <c>'5'</c>, written in upper-case, bracketed,
/// <c>dbo.</c>-qualified SQL. Those numeric codes are SUPERSEDED and dead:
/// <c>01.00.08.SqlDataProvider</c> inserts the letter code, rewrites both role
/// columns onto it, and then deletes the digit row from the lookup table - once
/// for each of the six digits - in lower-case, unqualified SQL. No later script
/// re-seeds the table. A case-sensitive search for a single naming form
/// therefore finds only the dead numerics and yields a completely wrong code
/// set, so any inspection of this DDL chain must be case-insensitive and must
/// cover all four naming forms the chain uses: bare, <c>dbo.</c>-qualified,
/// <c>[dbo].[...]</c>-bracketed, and
/// <c>{databaseOwner}{objectQualifier}</c>-templated.
/// </para>
/// <para>
/// EXPIRY CONTRACT FOR THE APPLICATION LAYER. Role expiry is computed from a
/// code and an integer period. This type carries the codes only; the arithmetic
/// belongs to <c>Application/Services/RoleService.cs</c>, which must reproduce
/// the legacy outcome exactly:
/// </para>
/// <list type="table">
///   <listheader>
///     <term>Member (code)</term>
///     <description>Expiry derived from the current expiry date and the period</description>
///   </listheader>
///   <item>
///     <term><c>None</c> ('N')</term>
///     <description>No expiry; the legacy path assigns <c>Null.NullDate</c>, which this model represents as a null expiry date.</description>
///   </item>
///   <item>
///     <term><c>OneTime</c> ('O')</term>
///     <description>Perpetual; the legacy path assigns the far-future sentinel date 9999-12-31.</description>
///   </item>
///   <item>
///     <term><c>Day</c> ('D')</term>
///     <description><c>AddDays(period)</c></description>
///   </item>
///   <item>
///     <term><c>Week</c> ('W')</term>
///     <description><c>AddDays(period * 7)</c></description>
///   </item>
///   <item>
///     <term><c>Month</c> ('M')</term>
///     <description><c>AddMonths(period)</c></description>
///   </item>
///   <item>
///     <term><c>Year</c> ('Y')</term>
///     <description><c>AddYears(period)</c></description>
///   </item>
/// </list>
/// <para>
/// A period equal to the legacy <c>Null.NullInteger</c> sentinel short-circuits
/// the whole table and yields no expiry, so that guard runs before the code is
/// examined at all.
/// </para>
/// </remarks>
// MIGRATION: the legacy DateAdd(DateInterval.Day/Month/Year, ...) calls supplied by
//   the Microsoft.VisualBasic runtime - imported at Library/Components/Security/
//   Roles/RoleController.vb L25 and applied in the L540-L547 Select Case - are
//   replaced by DateTime.AddDays/AddMonths/AddYears in
//   Application/Services/RoleService.cs. This enum carries only the char(1) codes
//   and declares no members other than those codes, because the date arithmetic is
//   Application-layer behaviour rather than domain vocabulary.
// MIGRATION: the legacy columns were surfaced as System.String properties carrying
//   serialisation and editor-presentation attributes (RoleInfo.vb L149 and L188).
//   Replacing that stringly-typed pair with this enum is the purpose of this file.
//   Those attributes are deliberately not carried across: the wire contract belongs
//   to the DTO and API boundary, the char(1) column mapping belongs to
//   Infrastructure/Persistence/Configurations/RoleConfiguration.cs, and the Domain
//   layer takes no dependency capable of expressing either.
public enum BillingFrequency : ushort
{
    /// <summary>
    /// No billing frequency - legacy code <c>'N'</c>, whose lookup row reads
    /// <c>'N', 'None'</c>. A role at this frequency never expires: the legacy
    /// path assigns <c>Null.NullDate</c>, which this model represents as a null
    /// expiry date.
    /// </summary>
    /// <remarks>
    /// This is a REAL legacy code, not an unset or default marker, and it must
    /// never be treated as one. It carries a second, independent responsibility:
    /// it is the no-trial guard. The legacy role-assignment path tests
    /// <c>role.TrialFrequency.ToString() &lt;&gt; "N"</c> to decide whether the
    /// trial period or the billing period governs expiry (RoleController.vb
    /// L521-L527), and the SQL applies the very same test when projecting a role -
    /// right through to the terminal schema, where
    /// <c>04.08.00.SqlDataProvider</c> gates the trial fee, trial period and trial
    /// frequency behind <c>case when R.TrialFrequency &lt;&gt; 'N'</c>. Dropping
    /// this member would silently break trial-period selection in both layers.
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
