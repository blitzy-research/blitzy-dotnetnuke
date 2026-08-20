using DnnMigration.Domain.Common;

namespace DnnMigration.Domain.Entities;

// Re-authors DotNetNuke.Security.Permissions.TabPermissionInfo as a plain persistence POCO over the
// terminal six-column dbo.TabPermission table. The legacy class declares nine private fields at lines 33-41
// and eight Property Get/Set blocks at lines 56-130.

/// <summary>
/// One grant or denial of a catalogued permission on one page, made either to a role or to an individual
/// account.
/// </summary>
/// <remarks>
/// <para>
/// A row of the legacy <c>dbo.TabPermission</c> table - singular, unlike the plural
/// <c>vw_TabPermissions</c> view that reads over it. Six persisted scalars and four navigations is the
/// whole of this type: it records the row and interprets nothing.
/// </para>
/// <para>
/// The subject of a grant is <see cref="RoleId"/> or <see cref="UserId"/>. Both are nullable and neither is
/// exclusive of the other at the schema level, which is why this type asserts no invariant between them;
/// the deduplication pass at <c>04.05.02.SqlDataProvider</c> lines 183-211 compares the pair with explicit
/// <c>IS NULL</c> handling precisely because either may be absent.
/// </para>
/// </remarks>
public sealed class TabPermission : Entity<int>
{
    /// <summary>Gets or sets the surrogate key of this grant.</summary>
    /// <value>
    /// The <c>TabPermissionID</c> column, declared <c>int IDENTITY(1, 1) NOT NULL</c> at
    /// <c>02.02.00.SqlDataProvider</c> line 694 and made the clustered primary key at lines 730-735.
    /// </value>
    public int TabPermissionId { get; set; }

    /// <inheritdoc />
    public override int Identity => TabPermissionId;

    /// <summary>Gets or sets the page this grant applies to.</summary>
    /// <value>
    /// The <c>TabID</c> column, <c>int NOT NULL</c> at <c>02.02.00.SqlDataProvider</c> line 695, with a
    /// cascading foreign key to <c>dbo.Tabs</c> (lines 783-788, re-added at <c>03.00.09.SqlDataProvider</c>
    /// line 489), so deleting a page deletes its grants.
    /// </value>
    public int TabId { get; set; }

    /// <summary>Gets or sets the catalogue entry being granted or denied.</summary>
    /// <value>
    /// The <c>PermissionID</c> column, <c>int NOT NULL</c> at <c>02.02.00.SqlDataProvider</c> line 696,
    /// with a cascading foreign key to <c>dbo.Permission</c> (lines 777-782, re-added at
    /// <c>03.00.09.SqlDataProvider</c> line 488).
    /// </value>
    public int PermissionId { get; set; }

    /// <summary>
    /// Gets or sets the role the grant is made to, or <see langword="null"/> when the grant is not
    /// role-scoped.
    /// </summary>
    /// <value>The <c>RoleID</c> column.</value>
    public int? RoleId { get; set; }

    /// <summary>Gets or sets whether this row confers the permission or explicitly withholds it.</summary>
    /// <value>
    /// The <c>AllowAccess</c> column, <c>bit NOT NULL</c> at <c>02.02.00.SqlDataProvider</c> line 698. <see
    /// langword="true"/> grants; <see langword="false"/> denies.
    /// </value>
    public bool AllowAccess { get; set; }

    /// <summary>
    /// Gets or sets the individual account the grant is made to, or <see langword="null"/> when the grant
    /// is not account-scoped.
    /// </summary>
    /// <value>
    /// The <c>UserID</c> column, added as <c>int NULL</c> by <c>04.05.00.SqlDataProvider</c> lines 480-493
    /// with a foreign key to <c>dbo.Users</c> that is declared NOT FOR REPLICATION and carries no ON DELETE
    /// clause, so removing an account does not cascade to its grants.
    /// </value>
    public int? UserId { get; set; }

    /// <summary>Gets or sets the page this grant applies to, the loaded form of <see cref="TabId"/>.</summary>
    /// <remarks>
    /// Required rather than optional, because <c>TabID</c> is <c>NOT NULL</c>: every grant is on a page. It
    /// is nonetheless left <see langword="null"/> by any query that does not load it, so reading it
    /// defensively remains correct.
    /// </remarks>
    public Tab Tab { get; set; }

    /// <summary>
    /// Gets or sets the catalogue entry this grant cites, the loaded form of <see cref="PermissionId"/>.
    /// </summary>
    /// <remarks>
    /// Required rather than optional, because <c>PermissionID</c> is <c>NOT NULL</c>: a grant that cites
    /// nothing grants nothing. As with <see cref="Tab"/>, a query that does not load it leaves it <see
    /// langword="null"/>.
    /// </remarks>
    public Permission Permission { get; set; }

    /// <summary>Gets or sets the role the grant is made to, the loaded form of <see cref="RoleId"/>.</summary>
    /// <remarks>
    /// Optional, and optional for two distinct reasons: the column is nullable, and a non-null <see
    /// cref="RoleId"/> naming one of the -1, -2 or -3 pseudo-principals has no <c>dbo.Roles</c> row to
    /// load. The inverse is <see cref="Entities.Role.TabPermissions"/>.
    /// </remarks>
    public Role? Role { get; set; }

    /// <summary>Gets or sets the account the grant is made to, the loaded form of <see cref="UserId"/>.</summary>
    public User? User { get; set; }
}
