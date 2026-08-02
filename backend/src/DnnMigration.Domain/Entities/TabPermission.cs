using DnnMigration.Domain.Common;

namespace DnnMigration.Domain.Entities;

/// <summary>
/// One grant or denial of a catalogued permission on one page, to a role or to a user.
/// </summary>
/// <remarks>
/// MIGRATION: replaces <c>DotNetNuke.Security.Permissions.TabPermissionInfo</c>. Bound to
/// <c>dbo.TabPermission</c> - singular - created by 02.02.00 and widened by 04.05.00 in the same way
/// as <see cref="ModulePermission"/>: <c>RoleID</c> became nullable and a nullable <c>UserID</c> was
/// added, so a page permission can be granted to an individual.
/// </remarks>
public sealed class TabPermission : Entity<int>
{
    /// <summary>Gets or sets the surrogate key (<c>TabPermissionID</c>, identity from 1).</summary>
    public int TabPermissionId { get; set; }

    /// <inheritdoc />
    public override int Identity => TabPermissionId;

    /// <summary>Gets or sets the page the grant applies to (<c>TabID</c>, required, cascade delete).</summary>
    public int TabId { get; set; }

    /// <summary>Gets or sets the catalogue entry granted (<c>PermissionID</c>, required, cascade delete).</summary>
    public int PermissionId { get; set; }

    /// <summary>Gets or sets the role the grant is made to (<c>RoleID</c>, nullable since 04.05.00).</summary>
    public int? RoleId { get; set; }

    /// <summary>Gets or sets the individual user the grant is made to (<c>UserID</c>, nullable, added by 04.05.00).</summary>
    public int? UserId { get; set; }

    /// <summary>
    /// Gets or sets whether this row allows the permission (<see langword="true"/>) or explicitly
    /// denies it (<see langword="false"/>) (<c>AllowAccess</c>, required). A deny outranks every
    /// allow for the same subject.
    /// </summary>
    public bool AllowAccess { get; set; }

    /// <summary>Gets or sets the page the grant applies to.</summary>
    public Tab? Tab { get; set; }

    /// <summary>Gets or sets the catalogue entry granted.</summary>
    public Permission? Permission { get; set; }

    /// <summary>Gets or sets the role the grant is made to.</summary>
    public Role? Role { get; set; }

    /// <summary>Gets or sets the individual user the grant is made to.</summary>
    public User? User { get; set; }
}
