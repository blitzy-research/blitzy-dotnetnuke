using DnnMigration.Domain.Common;

namespace DnnMigration.Domain.Entities;

// This entity is the row that bag stood in front of. The seven properties below are the seven columns of
// dbo.UserProfile and nothing else, so a fixed nineteen-slot surface becomes an open key-and-value set
// carrying whatever definitions a tenant has declared.

/// <summary>
/// One account's stored answer to one profile property definition - a single row of the legacy seven-column
/// <c>dbo.UserProfile</c> key-and-value table.
/// </summary>
/// <remarks>
/// <para>
/// The type is a plain persistence row and holds exactly the seven columns the table declares, under the
/// natural key <see cref="UserId"/> plus <see cref="PropertyDefinitionId"/> and the surrogate key <see
/// cref="ProfileId"/>. It answers one question - what did this account store for this property, when, and
/// who may see it - and answers nothing else.
/// </para>
/// <para>
/// The table it maps to is named <c>UserProfile</c> in the singular even though a row is one value rather
/// than one profile, which is why this type is named for what it actually holds. A whole profile is the set
/// of rows a <see cref="UserId"/> owns, reachable through <see cref="User.UserProfileValues"/>, and the
/// Application layer pairs that set with the tenant's definitions to produce a profile contract.
/// </para>
/// </remarks>
public sealed class UserProfileValue : Entity<int>
{
    /// <summary>Gets or sets the surrogate key of this row.</summary>
    /// <value>
    /// The <c>ProfileID</c> column: <c>int IDENTITY(1, 1) NOT NULL</c>, and the single-column primary key
    /// <c>PK_UserProfile</c>, declared non-clustered (<c>03.02.03.SqlDataProvider</c> lines 1366 and 1376).
    /// </value>
    /// <remarks>
    /// Seeding at 1 makes this the one in-scope identity column with no seed collision - <c>dbo.Portals</c>
    /// seeds at -1 and <c>dbo.Roles</c>, <c>dbo.Tabs</c> and <c>dbo.Modules</c> at 0, so for those a
    /// default <see cref="int"/> is a genuine key.
    /// </remarks>
    public int ProfileId { get; set; }

    /// <inheritdoc />
    public override int Identity => ProfileId;

    /// <summary>Gets or sets the account that stored this value.</summary>
    /// <value>
    /// The <c>UserID</c> column: <c>int NOT NULL</c>, and the dependent end of <c>FK_UserProfile_Users</c>,
    /// which cascades on delete (<c>04.00.04.SqlDataProvider</c> lines 1425-1426).
    /// </value>
    /// <remarks>
    /// The legacy facade carried the account as a private field nothing ever read - the provider passed the
    /// account key to each write separately and the read procedure filtered on it. Here it is the real,
    /// mapped foreign key, and the cascade means deleting an account removes its answers without the
    /// repository having to.
    /// </remarks>
    public int UserId { get; set; }

    /// <summary>Gets or sets the profile property definition this value answers.</summary>
    /// <value>
    /// The <c>PropertyDefinitionID</c> column: <c>int NOT NULL</c>, and the dependent end of
    /// <c>FK_UserProfile_ProfilePropertyDefinition</c>, which also cascades on delete
    /// (<c>04.00.04.SqlDataProvider</c> lines 1428-1429).
    /// </value>
    /// <remarks>
    /// MIGRATION: the cascade is also why a definition is withdrawn by flag rather than deleted - see <see
    /// cref="ProfilePropertyDefinition.IsDeleted"/> - since removing one would take every account's answer
    /// with it.
    /// </remarks>
    public int PropertyDefinitionId { get; set; }

    /// <summary>Gets or sets the stored value when it fits the bounded column.</summary>
    /// <value>
    /// The <c>PropertyValue</c> column: <c>nvarchar(3750) NULL</c> (<c>04.00.04.SqlDataProvider</c> line
    /// 1416). <see langword="null"/> when the row has no value at all, and also when the value was long
    /// enough to be diverted into <see cref="PropertyText"/>.
    /// </value>
    /// <remarks>
    /// The write procedure stores the submitted value here only when <c>DATALENGTH(@PropertyValue)</c> is
    /// 7500 or less, and nulls this column otherwise (<c>04.00.04.SqlDataProvider</c> lines 1626 and 1647).
    /// </remarks>
    public string? PropertyValue { get; set; }

    /// <summary>Gets or sets the stored value when it exceeds the bounded column.</summary>
    /// <value>
    /// The <c>PropertyText</c> column: SQL <c>ntext NULL</c> (<c>03.02.03.SqlDataProvider</c> line 1370,
    /// <c>04.00.04.SqlDataProvider</c> line 1417). <see langword="null"/> whenever the value fitted <see
    /// cref="PropertyValue"/>, which is the ordinary case.
    /// </value>
    /// <remarks>
    /// This column is real, terminal, and must not be dropped.
    /// </remarks>
    public string? PropertyText { get; set; }

    /// <summary>Gets or sets the audience permitted to see this value.</summary>
    /// <value>
    /// The <c>Visibility</c> column: <c>int NOT NULL DEFAULT 0</c> (<c>04.00.04.SqlDataProvider</c> line
    /// 1418).
    /// </value>
    /// <remarks>
    /// The column default is a database fact, and rows exist that rely on it - the upgrade that moved the
    /// flat address columns into this table inserts only <c>UserID</c>, <c>PropertyDefinitionID</c>,
    /// <c>PropertyValue</c> and <c>LastUpdatedDate</c> (<c>03.02.03.SqlDataProvider</c> line 2094), leaving
    /// this column to the default.
    /// </remarks>
    public int Visibility { get; set; }

    /// <summary>Gets or sets the instant this value was last written.</summary>
    /// <value>
    /// The <c>LastUpdatedDate</c> column: <c>datetime NOT NULL</c> with no store default
    /// (<c>04.00.04.SqlDataProvider</c> line 1419).
    /// </value>
    /// <remarks>
    /// A real legacy column with its own semantics, which is why this type is a plain entity and not an
    /// audited one.
    /// </remarks>
    public DateTime LastUpdatedDate { get; set; }

    /// <summary>Gets or sets the account that stored this value.</summary>
    public User User { get; set; }

    /// <summary>Gets or sets the profile property definition this value answers.</summary>
    /// <remarks>
    /// Worth loading with the value in most cases: an answer cannot be presented or validated without the
    /// name, data type and validation expression the definition carries, and it cannot be ordered without
    /// the definition's view order.
    /// </remarks>
    public ProfilePropertyDefinition PropertyDefinition { get; set; }
}
