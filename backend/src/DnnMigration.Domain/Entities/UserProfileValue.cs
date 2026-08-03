using DnnMigration.Domain.Common;

namespace DnnMigration.Domain.Entities;

// =====================================================================================
// MIGRATION: re-authored from Library/Components/Users/Profile/UserProfile.vb, and the
// decisive fact about that 583-line class is that NOT ONE of its members is a column. It
// declares nineteen public properties - Cell (line 103), City (123), Country (143), Fax
// (163), FirstName (183), FullName (203), IM (217), IsDirty (237), LastName (251),
// ObjectHydrated (271), PostalCode (288), PreferredLocale (308), ProfileProperties (329),
// Region (346), Street (366), Telephone (386), TimeZone (406), Unit (431) and Website
// (451) - of which fifteen are accessor pairs over the ProfilePropertyDefinitionCollection
// held in a private field (line 82), reached through GetPropertyValue (507) and
// SetProfileProperty (567). Of the remaining four, FullName concatenates two of the fifteen
// (205), IsDirty (237) and ObjectHydrated (271) are change-tracking bookkeeping, and
// ProfileProperties (329) hands out the bag itself. TimeZone (406) goes further still and
// parses the stored string with Integer.Parse, answering -1 when it is absent. The class is
// a typed facade over a property bag, not a persistence shape.
//
// MIGRATION: this entity is the row that bag stood in front of. The seven properties below
// are the seven columns of dbo.UserProfile and nothing else, so a fixed nineteen-slot
// surface becomes an open key-and-value set carrying whatever definitions a tenant has
// declared. None of those nineteen names appears here and none may be added back: a Street
// property on this type would be a second, unmapped spelling of a value the table already
// stores generically under a PropertyDefinitionID, and the moment a tenant declared one
// more property the fixed surface could not represent it at all.
//
// MIGRATION: the facade's responsibilities move outward rather than away. The bag becomes
// the PropertyDefinition relationship and its two inverse collections
// (User.UserProfileValues and ProfilePropertyDefinition.ProfileValues); the dirty tracking
// becomes the change tracker's job; the input filtering DNNProfileProvider.vb ran before
// every write (line 149) belongs to the Application layer, as does the Now() it stamped
// each row with (line 151) - which reaches this type through LastUpdatedDate, assigned by
// the caller from the injected clock rather than read here.
//
// MIGRATION: the terminal table has SEVEN columns, taken from CREATE TABLE UserProfile in
// Website/Providers/DataProviders/SqlDataProvider/03.02.03.SqlDataProvider lines 1364-1373
// and re-issued unchanged for fresh 4.0 installs by 04.00.04.SqlDataProvider lines
// 1411-1420. A case-insensitive sweep of all eighty-eight upgrade scripts for CREATE, ALTER
// or DROP TABLE against this table finds those two occurrences and no others, so the shape
// below is terminal rather than a way-point.
//
// MIGRATION: PropertyText is a real column and must never be dropped, for three
// independent reasons. It is declared in that CREATE TABLE (03.02.03 line 1370, 04.00.04
// line 1417). The write procedure UpdateUserProfileProperty splits every submitted value
// across the two columns on DATALENGTH(@PropertyValue) > 7500, in both its UPDATE arm
// (04.00.04 lines 1626-1627) and its INSERT arm (lines 1647-1648), so a long answer lives
// ONLY in PropertyText. And the search procedure GetUsersByProfileProperty matches both raw
// columns - "PropertyValue LIKE @PropertyValue OR PropertyText LIKE @PropertyValue"
// (04.00.04 line 946) - so a model that collapsed them would silently stop finding long
// answers.
//
// MIGRATION: the read procedure GetUserProfile does not return PropertyText at all. It
// returns one coalesced column aliased PropertyValue: "case when (PropertyValue Is Null)
// then PropertyText else PropertyValue end" (04.00.04 line 1592, identical at 03.02.03 line
// 1533). The behaviour-preserving projection is therefore PropertyValue ?? PropertyText, in
// that order, and it belongs to the Application mapper - deliberately NOT to a computed
// member on this type. Three reasons. The entity's contract is the row, so a derived member
// would be a second truth the entity configuration then has to be told to ignore. Hiding
// the coalesce would conceal which column a round trip actually wrote, which is precisely
// the fact a caller submitting a long value needs. And a hidden order is an unverifiable
// order: the reverse, PropertyText ?? PropertyValue, diverges from the procedure for any row
// holding both columns, and the search procedure above proves that is a shape the store can
// hold.
//
// MIGRATION: Visibility stays a plain int. The legacy reader cast it to UserVisibilityMode
// (DNNProfileProvider.vb line 111), but that enumeration
// (Library/Components/Users/UserVisibilityMode.vb line 23) names only AllUsers 0,
// MembersOnly 1 and AdminOnly 2, while the code interpreting it folds 0, 1, 2 and the legacy
// -1 null marker onto those three members and leaves every other stored integer unmapped
// (ProfilePropertyDefinition.vb lines 353-358). Declaring an enum here would publish an
// incomplete contract and let a value the database genuinely holds arrive as an undeclared
// member, so the raw persisted integer is preserved instead.
//
// MIGRATION OBLIGATION FOR INFRASTRUCTURE: three property names differ from their columns
// and the entity configuration must bridge them - ProfileId to ProfileID, UserId to UserID,
// PropertyDefinitionId to PropertyDefinitionID. It must also hold PropertyText at ntext
// rather than let a max-length convention turn it into nvarchar, cap PropertyValue at 3750
// characters, seed ProfileID as IDENTITY(1, 1), leave the Visibility default in the database
// rather than duplicating it in code, and retain both foreign keys with their cascade delete
// (FK_UserProfile_Users and FK_UserProfile_ProfilePropertyDefinition, 04.00.04 lines
// 1425-1429).
//
// MIGRATION: nothing here carries an attribute, hydrates itself, reaches a clock or touches
// I/O. The legacy read path built these values by hand from an IDataReader
// (DNNProfileProvider.vb lines 104-113); the object-relational mapper materialises them now,
// so there is no Fill, no CBO, no IHydratable and no sentinel translation left to port.
// =====================================================================================

/// <summary>
/// One account's stored answer to one profile property definition - a single row of the legacy
/// seven-column <c>dbo.UserProfile</c> key-and-value table.
/// </summary>
/// <remarks>
/// <para>
/// The type is a plain persistence row and holds exactly the seven columns the table declares,
/// under the natural key <see cref="UserId"/> plus <see cref="PropertyDefinitionId"/> and the
/// surrogate key <see cref="ProfileId"/>. It answers one question - what did this account store
/// for this property, when, and who may see it - and answers nothing else.
/// </para>
/// <para>
/// The table it maps to is named <c>UserProfile</c> in the singular even though a row is one
/// value rather than one profile, which is why this type is named for what it actually holds.
/// A whole profile is the set of rows a <see cref="UserId"/> owns, reachable through
/// <see cref="User.UserProfileValues"/>, and the Application layer pairs that set with the
/// tenant's definitions to produce a profile contract.
/// </para>
/// <para>
/// Both storage columns are modelled. <see cref="PropertyValue"/> holds a value the bounded
/// column can take and <see cref="PropertyText"/> holds one that outgrew it; the legacy write
/// procedure fills exactly one of the two per row and nulls the other, so they are mutually
/// exclusive in practice, and the effective value is <see cref="PropertyValue"/> when it is
/// present and <see cref="PropertyText"/> otherwise. That coalesce is the Application mapper's
/// to perform and no member here performs it - the reasoning is set out in the migration notes
/// above this type, and the short version is that the entity reports what the row holds while
/// the DTO reports what the caller asked for.
/// </para>
/// <para>
/// Absence is modelled with <see langword="null"/>, not with the empty string. The legacy read
/// path collapsed the two: <c>Convert.ToString(dr("PropertyValue"))</c>
/// (<c>DNNProfileProvider.vb</c> line 110) turns a SQL <c>NULL</c> into <c>""</c>, which is also
/// the legacy <c>Null.NullString</c> sentinel, so a column that had never been written and one
/// deliberately blanked were indistinguishable downstream. Here they are distinct, and where the
/// wire contract still needs the legacy empty string the DTO boundary supplies it - sentinels
/// survive where they are externally observable, not in the model.
/// </para>
/// <para>
/// The two navigations are required rather than optional, because a value row without an owning
/// account and a defining property is meaningless: both foreign keys are non-nullable and both
/// cascade on delete, so the row cannot outlive either principal. They are left unassigned by
/// this type - CS8618 is suppressed across the solution for materialised types - so a caller
/// that needs a principal must include it rather than assume it is loaded.
/// </para>
/// </remarks>
/// <example>
/// Recording an answer, with the column choice made by the caller that knows the value:
/// <code>
/// var answer = new UserProfileValue
/// {
///     UserId = account.UserId,
///     PropertyDefinitionId = definition.PropertyDefinitionId,
///     PropertyValue = submitted.Length &lt;= 3750 ? submitted : null,
///     PropertyText = submitted.Length &gt; 3750 ? submitted : null,
///     Visibility = definition.DefaultVisibility,
///     LastUpdatedDate = clock.UtcNow,
/// };
/// </code>
/// </example>
public sealed class UserProfileValue : Entity<int>
{
    /// <summary>
    /// Gets or sets the surrogate key of this row.
    /// </summary>
    /// <value>
    /// The <c>ProfileID</c> column: <c>int IDENTITY(1, 1) NOT NULL</c>, and the single-column
    /// primary key <c>PK_UserProfile</c>, declared non-clustered
    /// (<c>03.02.03.SqlDataProvider</c> lines 1366 and 1376).
    /// </value>
    /// <remarks>
    /// <para>
    /// MIGRATION: no legacy member corresponds to it. The facade addressed a value by property
    /// name - <c>GetPropertyValue(cStreet)</c> and its fourteen siblings - and never saw this key,
    /// which is why the write procedure treats <c>@ProfileID</c> as optional and looks the row up
    /// by the <see cref="UserId"/> and <see cref="PropertyDefinitionId"/> pair when it arrives
    /// null or -1 (<c>04.00.04.SqlDataProvider</c> lines 1616-1620). That pair is the natural key;
    /// this column is the surrogate the table was given.
    /// </para>
    /// <para>
    /// MIGRATION: seeding at 1 makes this the one in-scope identity column with no seed collision -
    /// <c>dbo.Portals</c> seeds at -1 and <c>dbo.Roles</c>, <c>dbo.Tabs</c> and <c>dbo.Modules</c>
    /// at 0, so for those a default <see cref="int"/> is a genuine key. Zero is still not read as
    /// "not written yet" here either: persisted state is declared through
    /// <see cref="Entity{TId}.MarkIdentityPersisted"/> by code that already knows the row exists, by
    /// nothing automatically, and never deduced from this value.
    /// </para>
    /// </remarks>
    public int ProfileId { get; set; }

    /// <inheritdoc />
    /// <remarks>
    /// Forwards <see cref="ProfileId"/>. Equality therefore follows the surrogate key rather than
    /// the natural one, which is what the change tracker keys on as well.
    /// </remarks>
    public override int Identity => ProfileId;

    /// <summary>
    /// Gets or sets the account that stored this value.
    /// </summary>
    /// <value>
    /// The <c>UserID</c> column: <c>int NOT NULL</c>, and the dependent end of
    /// <c>FK_UserProfile_Users</c>, which cascades on delete
    /// (<c>04.00.04.SqlDataProvider</c> lines 1425-1426).
    /// </value>
    /// <remarks>
    /// MIGRATION: the legacy facade carried the account as a private <c>UserID</c> field
    /// (<c>UserProfile.vb</c> line 79) that nothing ever read - the provider passed
    /// <c>user.UserID</c> to each write separately (<c>DNNProfileProvider.vb</c> line 151) and the
    /// read procedure filtered on it (<c>04.00.04.SqlDataProvider</c> line 1596). Here it is the
    /// real, mapped foreign key, and the cascade means deleting an account removes its answers
    /// without the repository having to.
    /// </remarks>
    public int UserId { get; set; }

    /// <summary>
    /// Gets or sets the profile property definition this value answers.
    /// </summary>
    /// <value>
    /// The <c>PropertyDefinitionID</c> column: <c>int NOT NULL</c>, and the dependent end of
    /// <c>FK_UserProfile_ProfilePropertyDefinition</c>, which also cascades on delete
    /// (<c>04.00.04.SqlDataProvider</c> lines 1428-1429).
    /// </value>
    /// <remarks>
    /// <para>
    /// MIGRATION: this is the column that makes the nineteen fixed properties unnecessary. Which
    /// property a value belongs to is data, not shape, so a tenant can declare any set of
    /// definitions and the table carries answers to all of them.
    /// </para>
    /// <para>
    /// MIGRATION: the cascade is also why a definition is withdrawn by flag rather than deleted -
    /// see <see cref="ProfilePropertyDefinition.IsDeleted"/> - since removing one would take every
    /// account's answer with it.
    /// </para>
    /// </remarks>
    public int PropertyDefinitionId { get; set; }

    /// <summary>
    /// Gets or sets the stored value when it fits the bounded column.
    /// </summary>
    /// <value>
    /// The <c>PropertyValue</c> column: <c>nvarchar(3750) NULL</c>
    /// (<c>04.00.04.SqlDataProvider</c> line 1416). <see langword="null"/> when the row has no
    /// value at all, and also when the value was long enough to be diverted into
    /// <see cref="PropertyText"/>.
    /// </value>
    /// <remarks>
    /// <para>
    /// MIGRATION: the write procedure stores the submitted value here only when
    /// <c>DATALENGTH(@PropertyValue)</c> is 7500 or less, and nulls this column otherwise
    /// (<c>04.00.04.SqlDataProvider</c> lines 1626 and 1647). The parameter is <c>ntext</c>
    /// (line 1611) and the data is UCS-2, so <c>DATALENGTH</c> counts two bytes per character and
    /// the 7500-byte threshold is exactly this column's 3750-character capacity - the split fires
    /// precisely when the value would not fit.
    /// </para>
    /// <para>
    /// MIGRATION: <see langword="null"/> here means SQL <c>NULL</c> and nothing else. It is not the
    /// legacy <c>Null.NullString</c> empty-string sentinel, and it must not be normalised to one:
    /// the read procedure branches on <c>PropertyValue Is Null</c> to decide whether to fall back
    /// to <see cref="PropertyText"/> (line 1592), so an empty string written in place of a null
    /// would suppress that fallback and lose a long value entirely.
    /// </para>
    /// </remarks>
    public string? PropertyValue { get; set; }

    /// <summary>
    /// Gets or sets the stored value when it exceeds the bounded column.
    /// </summary>
    /// <value>
    /// The <c>PropertyText</c> column: SQL <c>ntext NULL</c>
    /// (<c>03.02.03.SqlDataProvider</c> line 1370, <c>04.00.04.SqlDataProvider</c> line 1417).
    /// <see langword="null"/> whenever the value fitted <see cref="PropertyValue"/>, which is the
    /// ordinary case.
    /// </value>
    /// <remarks>
    /// <para>
    /// MIGRATION: this column is real, terminal, and must not be dropped. It receives the
    /// submitted value exactly when <c>DATALENGTH(@PropertyValue)</c> exceeds 7500 and is nulled
    /// otherwise (<c>04.00.04.SqlDataProvider</c> lines 1627 and 1648), so for a long answer this
    /// is the only place the data exists. The search procedure confirms it independently by
    /// matching both raw columns (line 946).
    /// </para>
    /// <para>
    /// MIGRATION OBLIGATION FOR INFRASTRUCTURE: the store type is <c>ntext</c>, so the entity
    /// configuration must say so explicitly. Leaving it to convention would produce
    /// <c>nvarchar(max)</c>, and pairing it with a maximum length would produce a bounded
    /// <c>nvarchar</c> - either of which would contradict the schema this migration is forbidden
    /// to alter.
    /// </para>
    /// <para>
    /// MIGRATION: no member here coalesces this column with <see cref="PropertyValue"/>. The
    /// legacy read procedure prefers <see cref="PropertyValue"/> and falls back to this one, and
    /// reproducing that order is the Application mapper's job, so that which column was written
    /// stays visible to anyone reading the entity.
    /// </para>
    /// </remarks>
    public string? PropertyText { get; set; }

    /// <summary>
    /// Gets or sets the audience permitted to see this value.
    /// </summary>
    /// <value>
    /// The <c>Visibility</c> column: <c>int NOT NULL DEFAULT 0</c>
    /// (<c>04.00.04.SqlDataProvider</c> line 1418).
    /// </value>
    /// <remarks>
    /// <para>
    /// MIGRATION: deliberately an <see cref="int"/> and not an enumeration, even though the legacy
    /// reader cast the column to <c>UserVisibilityMode</c> (<c>DNNProfileProvider.vb</c> line
    /// 111). That enumeration declares three members - <c>AllUsers</c> 0, <c>MembersOnly</c> 1 and
    /// <c>AdminOnly</c> 2 (<c>UserVisibilityMode.vb</c> line 23) - while the code that interprets
    /// the stored number maps 0, 1, 2 and the legacy -1 null marker onto those three and leaves
    /// every other value unmapped (<c>ProfilePropertyDefinition.vb</c> lines 353-358). An
    /// enumeration here would publish that gap as though it were a complete contract and would let
    /// a number the database really holds surface as an undeclared member, so the raw persisted
    /// value is kept and its interpretation belongs to the layer that presents it.
    /// </para>
    /// <para>
    /// MIGRATION: the column default is a database fact, and rows exist that rely on it - the
    /// upgrade that moved the flat address columns into this table inserts only <c>UserID</c>,
    /// <c>PropertyDefinitionID</c>, <c>PropertyValue</c> and <c>LastUpdatedDate</c>
    /// (<c>03.02.03.SqlDataProvider</c> line 2094), leaving this column to the default. No default
    /// is therefore restated here: a value assigned in code would be written on every insert and
    /// would stop being the database's answer.
    /// </para>
    /// <para>
    /// This is per-value visibility, distinct from the per-definition
    /// <see cref="ProfilePropertyDefinition"/> flags and from the tenant's default hint, so an
    /// account can restrict one answer without affecting the property or anyone else's answer.
    /// </para>
    /// </remarks>
    public int Visibility { get; set; }

    /// <summary>
    /// Gets or sets the instant this value was last written.
    /// </summary>
    /// <value>
    /// The <c>LastUpdatedDate</c> column: <c>datetime NOT NULL</c> with no store default
    /// (<c>04.00.04.SqlDataProvider</c> line 1419).
    /// </value>
    /// <remarks>
    /// <para>
    /// MIGRATION: a real legacy column with its own semantics, which is why this type is a plain
    /// entity and not an audited one. The legacy write procedure takes the instant as a parameter
    /// and the provider supplied <c>Now()</c> for it at the call site
    /// (<c>DNNProfileProvider.vb</c> line 151) - local server time, and the only audit column this
    /// table has: there is no created-on, no created-by and no updated-by to pair it with, so
    /// borrowing a general audit base would invent three columns the schema does not have.
    /// </para>
    /// <para>
    /// MIGRATION: required and never defaulted here. The column has no store default, so every
    /// insert must carry a value, and the caller assigns it from the injected clock - which is
    /// also what makes a write deterministic under test. Reading an ambient clock from this
    /// property would put I/O in the domain and make the value untestable.
    /// </para>
    /// </remarks>
    public DateTime LastUpdatedDate { get; set; }

    // MIGRATION: both navigations are required, and neither replaces a legacy member - the facade
    // held no reference to an account or to a definition, only a private user id it never read and
    // a bag of definition objects the provider had already filled in. Left unassigned on purpose:
    // CS8618 is suppressed solution-wide for materialised types, so code that needs a principal
    // must include it rather than assume it is loaded.

    /// <summary>
    /// Gets or sets the account that stored this value.
    /// </summary>
    /// <remarks>
    /// The principal end of <c>FK_UserProfile_Users</c>, keyed by <see cref="UserId"/> and
    /// cascading on delete (<c>04.00.04.SqlDataProvider</c> lines 1425-1426). Its inverse is
    /// <see cref="User.UserProfileValues"/>. Setting this instead of <see cref="UserId"/> is the
    /// natural way to attach an answer to an account that has not been written yet, since the
    /// database assigns the account key on insert.
    /// </remarks>
    public User User { get; set; }

    /// <summary>
    /// Gets or sets the profile property definition this value answers.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The principal end of <c>FK_UserProfile_ProfilePropertyDefinition</c>, keyed by
    /// <see cref="PropertyDefinitionId"/> and cascading on delete
    /// (<c>04.00.04.SqlDataProvider</c> lines 1428-1429). Its inverse is
    /// <see cref="ProfilePropertyDefinition.ProfileValues"/>.
    /// </para>
    /// <para>
    /// Worth loading with the value in most cases: an answer cannot be presented or validated
    /// without the name, data type and validation expression the definition carries, and it cannot
    /// be ordered without the definition's view order.
    /// </para>
    /// </remarks>
    public ProfilePropertyDefinition PropertyDefinition { get; set; }
}
