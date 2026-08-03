using DnnMigration.Domain.Common;

namespace DnnMigration.Domain.Entities;

// MIGRATION: re-authors DotNetNuke.Security.Permissions.TabPermissionInfo
// (Library/Components/Security/Permissions/TabPermission.vb, class declared at line 29) as a plain
// persistence POCO over the terminal six-column dbo.TabPermission table. The legacy class declares
// nine private fields at lines 33-41 and eight Property Get/Set blocks at lines 56-130. Only five of
// those fields are columns of this table; the other four produce no member here and each omission is
// accounted for individually below, so nothing is dropped silently.
//
// MIGRATION: this type carries no attribute of any kind, and none is replaced. All eight legacy
// properties were decorated <XmlElement(...)> (TabPermission.vb lines 58, 67, 76, 85, 94, 103, 112
// and 121) because that one class doubled as its own wire format, and the file imported System.Data
// and System.Xml.Serialization (lines 21 and 23) to do it. Rule T1 leaves the Domain project with no
// reference of any kind, so serialisation, validation and column-mapping attributes have nowhere to
// come from and no business being here: the wire contract belongs to the Application DTOs and the
// column mapping to the Infrastructure Fluent configuration.
//
// MIGRATION: the legacy INHERITANCE IS DELETED RATHER THAN FLATTENED. TabPermission.vb line 30
// declares "Inherits PermissionInfo", so the legacy object surfaced PermissionID, PermissionCode,
// ModuleDefID, PermissionKey and PermissionName as inherited members and presented one object where
// the database holds two rows in two tables. The terminal schema stores a PermissionID FOREIGN KEY -
// declared at 02.02.00.SqlDataProvider line 696 and constrained at lines 776-782, then re-added
// qualifier-templated at 03.00.09.SqlDataProvider line 488 - and a foreign key is a reference, not a
// base class. This type therefore carries PermissionId plus a Permission navigation and derives from
// Entity<int> alone.
//   OBLIGATION ON INFRASTRUCTURE: map the six columns of dbo.TabPermission and nothing else. Do not
//   infer table-per-hierarchy or table-per-type from the legacy class shape - there is no
//   discriminator column and no shared key, so reproducing the inheritance would fabricate a
//   hierarchy the database does not have.
//
// MIGRATION: the terminal column set was reconstructed by replaying the upgrade chain, because the
// chain is destructive. 02.02.00.SqlDataProvider lines 693-699 create the table with FIVE columns -
// TabPermissionID int IDENTITY(1, 1) NOT NULL, TabID int NOT NULL, PermissionID int NOT NULL,
// RoleID int NOT NULL and AllowAccess bit NOT NULL. 04.05.00.SqlDataProvider then REWRITES the
// subject columns at lines 456-478: it adds TempRoleID, copies RoleID into it, DROPS COLUMN RoleID,
// re-adds RoleID as int NULL, copies the values back and drops the temporary column. The terminal
// RoleID is therefore NULLABLE and, having been re-added after the drop, carries no foreign key to
// dbo.Roles at all. Lines 480-493 then add UserID int NULL beside it, with a foreign key to
// dbo.Users declared NOT FOR REPLICATION and with no ON DELETE clause. Six columns is the terminal
// count: a search of all 88 upgrade scripts finds no other column ever added to or dropped from this
// table, only constraint churn at 03.00.09 lines 453-489.
//   Consequence: RoleId and UserId MUST both stay nullable. Reading the baseline CREATE TABLE alone
//   would make RoleID required and would miss UserID entirely.
//
// MIGRATION: the legacy constructor's sentinels are NOT ported. TabPermission.vb lines 44-54 assign
// Null.NullInteger to _TabPermissionID, _TabID and _userID, Null.NullString to _permissionKey,
// _RoleName, _Username and _DisplayName, and Integer.Parse(glbRoleNothing) to _roleID -
// Library/Components/Shared/Null.vb lines 41-45 define NullInteger as -1 and lines 71-75 define
// NullString as the empty string, while Library/Components/Shared/Globals.vb line 98 defines
// glbRoleNothing as "-4". Those are IN-MEMORY MARKERS FOR "NOT SET", not database defaults: no
// column of dbo.TabPermission carries a DEFAULT constraint anywhere in the chain. Under Rule T7
// absence is expressed by the nullable CLR type, so no property here is initialised to a negative
// value. Initialising RoleId to -4 or UserId to -1 would write a fabricated principal reference into
// a real row.
//
// MIGRATION: NEGATIVE RoleID VALUES ARE REAL DATA, NOT MARKERS, AND ARE NOT NORMALISED HERE. The
// terminal view at 04.05.00.SqlDataProvider lines 503-530 resolves the role name with
// "CASE TP.RoleID WHEN -1 THEN 'All Users' WHEN -2 THEN 'Superuser' WHEN -3 THEN 'Unauthenticated
// Users' ELSE R.RoleName END" over a LEFT OUTER JOIN to dbo.Roles - matching the pseudo-principal
// constants at Globals.vb lines 95-97 - so -1, -2 and -3 are externally observable stored values
// that deliberately resolve to no dbo.Roles row, while -4 never reaches storage because it only ever
// meant "no role chosen" in memory. RoleId is consequently kept as a raw int?: it is NOT remapped
// onto an enumeration, NOT translated to null, and NOT assumed to resolve through the Role
// navigation. Any consumer that treats a non-null RoleId as a guaranteed dbo.Roles row is wrong for
// three of its values, and 04.05.02.SqlDataProvider lines 144-180 show the platform itself rewriting
// RoleID = -1 to NULL on user-scoped rows rather than trusting the join.

/// <summary>
/// One grant or denial of a catalogued permission on one page, made either to a role or to an
/// individual account.
/// </summary>
/// <remarks>
/// <para>
/// A row of the legacy <c>dbo.TabPermission</c> table - singular, unlike the plural
/// <c>vw_TabPermissions</c> view that reads over it. Six persisted scalars and four navigations is
/// the whole of this type: it records the row and interprets nothing. Deciding what a caller may do
/// with a page, including the rule that a denial outranks every allow for the same subject, belongs
/// to the Infrastructure permission evaluator driven by the API layer's authorisation policies.
/// </para>
/// <para>
/// The subject of a grant is <see cref="RoleId"/> or <see cref="UserId"/>. Both are nullable and
/// neither is exclusive of the other at the schema level, which is why this type asserts no
/// invariant between them; the deduplication pass at
/// <c>04.05.02.SqlDataProvider</c> lines 183-211 compares the pair with explicit
/// <c>IS NULL</c> handling precisely because either may be absent.
/// </para>
/// <para>
/// OBLIGATION ON INFRASTRUCTURE - the mapping this type deliberately does not express, and which
/// must not be recreated by convention:
/// </para>
/// <list type="bullet">
///   <item>
///   Bind the idiomatic property names to their legacy columns one by one -
///   <see cref="TabPermissionId"/> to <c>TabPermissionID</c>, <see cref="TabId"/> to <c>TabID</c>,
///   <see cref="PermissionId"/> to <c>PermissionID</c>, <see cref="RoleId"/> to <c>RoleID</c> and
///   <see cref="UserId"/> to <c>UserID</c>. <see cref="AllowAccess"/> alone already matches its
///   column name.
///   </item>
///   <item>
///   Configure the EXISTING unique constraint over the four-column tuple
///   <c>(TabID, PermissionID, RoleID, UserID)</c>, added as
///   <c>IX_{objectQualifier}TabPermission UNIQUE NONCLUSTERED</c> by
///   <c>04.05.02.SqlDataProvider</c> lines 214-221. It is what stops the same subject being granted
///   the same permission on the same page twice. Configure it, never author it: Rule T4 holds the
///   schema immutable, so the baseline migration must not emit this constraint against a live
///   database.
///   </item>
///   <item>
///   Preserve the constraint identities the chain settled on - <c>PK_TabPermission</c> clustered on
///   <c>TabPermissionID</c> with the identity seed and increment both 1, the cascading
///   <c>FK_TabPermission_Tabs</c> and <c>FK_TabPermission_Permission</c>, and the non-cascading
///   <c>FK_TabPermission_Users</c>. There is no foreign key on <c>RoleID</c>.
///   </item>
///   <item>
///   Bind by column NAME only. The order the properties are declared in below is the logical order
///   of the contract, and it deliberately does not claim to be the physical order of the table: the
///   terminal ordinal order is <c>TabPermissionID</c>, <c>TabID</c>, <c>PermissionID</c>,
///   <c>AllowAccess</c>, <c>RoleID</c>, <c>UserID</c>, because <c>04.05.00</c> dropped <c>RoleID</c>
///   and re-added it after <c>AllowAccess</c>. Nothing may depend on ordinal position.
///   </item>
/// </list>
/// <para>
/// OBLIGATION ON THE APPLICATION LAYER - the joined values that are absent here on purpose.
/// <c>vw_TabPermissions</c> (<c>04.05.00.SqlDataProvider</c> lines 503-530) projects
/// <c>RoleName</c>, <c>Username</c> and <c>DisplayName</c> alongside the row, and the legacy class
/// cached all three as its own properties (TabPermission.vb lines 85-92 and 112-128) even though
/// none is a column of this table. They belong on the read DTOs an Application service assembles,
/// together with the <c>PermissionCode</c>, <c>ModuleDefID</c>, <c>PermissionKey</c> and
/// <c>PermissionName</c> the view lifts from <c>dbo.Permission</c> - reachable here through
/// <see cref="Permission"/> when a query loads it. Adding any of them to this type would invent a
/// column.
/// </para>
/// <para>
/// One further legacy member has no counterpart at all: <c>_permissionKey</c>, declared at
/// TabPermission.vb line 35 and initialised to the empty-string sentinel at line 47, is a dead
/// private field. The class exposes no property over it - lines 56-130 declare eight properties and
/// none is a permission key - so it never reached the database and never left the object. It must not
/// become a column, a property or a shadow property. Permission-key data lives on
/// <see cref="Entities.Permission.PermissionKey"/> and is reached through the reference.
/// </para>
/// </remarks>
public sealed class TabPermission : Entity<int>
{
    /// <summary>
    /// Gets or sets the surrogate key of this grant.
    /// </summary>
    /// <value>
    /// The <c>TabPermissionID</c> column, declared <c>int IDENTITY(1, 1) NOT NULL</c> at
    /// <c>02.02.00.SqlDataProvider</c> line 694 and made the clustered primary key at lines 730-735.
    /// Because the seed is 1 rather than 0, zero is never a persisted grant identity - unlike
    /// <c>dbo.Tabs.TabID</c> and <c>dbo.Roles.RoleID</c>, which both seed at 0
    /// (<c>01.00.00.SqlDataProvider</c> lines 140 and 115), so a zero on this row means something
    /// quite different from a zero on the rows it points at.
    /// </value>
    public int TabPermissionId { get; set; }

    /// <inheritdoc />
    public override int Identity => TabPermissionId;

    /// <summary>
    /// Gets or sets the page this grant applies to.
    /// </summary>
    /// <value>
    /// The <c>TabID</c> column, <c>int NOT NULL</c> at <c>02.02.00.SqlDataProvider</c> line 695,
    /// with a cascading foreign key to <c>dbo.Tabs</c> (lines 783-788, re-added at
    /// <c>03.00.09.SqlDataProvider</c> line 489), so deleting a page deletes its grants. Required,
    /// and legitimately 0 because <c>dbo.Tabs.TabID</c> seeds at 0.
    /// </value>
    public int TabId { get; set; }

    /// <summary>
    /// Gets or sets the catalogue entry being granted or denied.
    /// </summary>
    /// <value>
    /// The <c>PermissionID</c> column, <c>int NOT NULL</c> at <c>02.02.00.SqlDataProvider</c>
    /// line 696, with a cascading foreign key to <c>dbo.Permission</c> (lines 777-782, re-added at
    /// <c>03.00.09.SqlDataProvider</c> line 488).
    /// </value>
    /// <remarks>
    /// MIGRATION: this property, together with <see cref="Permission"/>, is what replaces the legacy
    /// <c>Inherits PermissionInfo</c> at TabPermission.vb line 30. The inherited
    /// <c>PermissionCode</c>, <c>ModuleDefID</c>, <c>PermissionKey</c> and <c>PermissionName</c>
    /// members are deliberately not redeclared here: they are columns of <c>dbo.Permission</c>, and
    /// duplicating them would let a grant disagree with the catalogue it cites.
    /// </remarks>
    public int PermissionId { get; set; }

    /// <summary>
    /// Gets or sets the role the grant is made to, or <see langword="null"/> when the grant is not
    /// role-scoped.
    /// </summary>
    /// <value>
    /// The <c>RoleID</c> column. Nullable since <c>04.05.00.SqlDataProvider</c> lines 456-478
    /// dropped and re-added it, which also left it without a foreign key to <c>dbo.Roles</c>.
    /// </value>
    /// <remarks>
    /// <para>
    /// MIGRATION: kept as a raw <see langword="int"/>? on purpose. <see langword="null"/> means "not
    /// role-scoped" and replaces the legacy in-memory -4 marker, but -1, -2 and -3 are stored values
    /// that name the All Users, Superuser and Unauthenticated Users pseudo-principals, so they are
    /// preserved exactly as read. Do not translate them, do not model them as an enumeration, and do
    /// not assume a non-null value resolves through <see cref="Role"/> - for those three it does
    /// not, which is why the terminal view reaches <c>dbo.Roles</c> through a LEFT OUTER JOIN and
    /// supplies the three names from a CASE expression instead.
    /// </para>
    /// <para>
    /// Because <see cref="Entities.Role.RoleId"/> seeds at 0, zero is a real role - the
    /// administrator role of a fresh installation - and must never be read as "no role".
    /// </para>
    /// </remarks>
    public int? RoleId { get; set; }

    /// <summary>
    /// Gets or sets whether this row confers the permission or explicitly withholds it.
    /// </summary>
    /// <value>
    /// The <c>AllowAccess</c> column, <c>bit NOT NULL</c> at <c>02.02.00.SqlDataProvider</c>
    /// line 698. <see langword="true"/> grants; <see langword="false"/> denies.
    /// </value>
    /// <remarks>
    /// Left at <see langword="false"/> for a freshly constructed grant, which withholds access until
    /// something explicitly confers it. That matches the legacy constructor, which assigned
    /// <c>False</c> at TabPermission.vb line 49, and no default is asserted here because the column
    /// carries none.
    /// </remarks>
    public bool AllowAccess { get; set; }

    /// <summary>
    /// Gets or sets the individual account the grant is made to, or <see langword="null"/> when the
    /// grant is not account-scoped.
    /// </summary>
    /// <value>
    /// The <c>UserID</c> column, added as <c>int NULL</c> by <c>04.05.00.SqlDataProvider</c>
    /// lines 480-493 with a foreign key to <c>dbo.Users</c> that is declared NOT FOR REPLICATION and
    /// carries no ON DELETE clause, so removing an account does not cascade to its grants.
    /// </value>
    /// <remarks>
    /// MIGRATION: nullable, replacing the legacy -1 marker assigned at TabPermission.vb line 51. It
    /// is what makes a grant to one person rather than to a role possible; before 04.05.00 the table
    /// could express only role-scoped authority. Zero is not special here either -
    /// <see cref="Entities.User.UserId"/> is an identity column in its own right.
    /// </remarks>
    public int? UserId { get; set; }

    /// <summary>
    /// Gets or sets the page this grant applies to, the loaded form of <see cref="TabId"/>.
    /// </summary>
    /// <remarks>
    /// Required rather than optional, because <c>TabID</c> is <c>NOT NULL</c>: every grant is on a
    /// page. It is nonetheless left <see langword="null"/> by any query that does not load it, so
    /// reading it defensively remains correct. The inverse is
    /// <see cref="Entities.Tab.TabPermissions"/>.
    /// </remarks>
    public Tab Tab { get; set; }

    /// <summary>
    /// Gets or sets the catalogue entry this grant cites, the loaded form of
    /// <see cref="PermissionId"/>.
    /// </summary>
    /// <remarks>
    /// Required rather than optional, because <c>PermissionID</c> is <c>NOT NULL</c>: a grant that
    /// cites nothing grants nothing. As with <see cref="Tab"/>, a query that does not load it leaves
    /// it <see langword="null"/>. The inverse is
    /// <see cref="Entities.Permission.TabPermissions"/>, and this is the reference across which the
    /// permission code, key and name are reached now that they are no longer inherited members.
    /// </remarks>
    public Permission Permission { get; set; }

    /// <summary>
    /// Gets or sets the role the grant is made to, the loaded form of <see cref="RoleId"/>.
    /// </summary>
    /// <remarks>
    /// Optional, and optional for two distinct reasons: the column is nullable, and a non-null
    /// <see cref="RoleId"/> naming one of the -1, -2 or -3 pseudo-principals has no
    /// <c>dbo.Roles</c> row to load. The inverse is <see cref="Entities.Role.TabPermissions"/>.
    /// </remarks>
    public Role? Role { get; set; }

    /// <summary>
    /// Gets or sets the account the grant is made to, the loaded form of <see cref="UserId"/>.
    /// </summary>
    /// <remarks>
    /// Optional, because <c>UserID</c> is nullable: a role-scoped grant names no account. The
    /// inverse is <see cref="Entities.User.TabPermissions"/>.
    /// </remarks>
    public User? User { get; set; }
}
