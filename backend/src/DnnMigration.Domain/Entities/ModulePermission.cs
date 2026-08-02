using DnnMigration.Domain.Common;

namespace DnnMigration.Domain.Entities;

/// <summary>
/// One grant or denial of a catalogued permission on one module instance, to a role or to a user.
/// </summary>
/// <remarks>
/// <para>
/// MIGRATION: replaces <c>DotNetNuke.Security.Permissions.ModulePermissionInfo</c>. Bound to
/// <c>dbo.ModulePermission</c> - singular - created by 02.02.00.
/// </para>
/// <para>
/// MIGRATION: 04.05.00 dropped and recreated <c>RoleID</c> as <b>nullable</b> and added a nullable
/// <c>UserID</c>, which is what makes a grant to an individual user possible. Exactly one of the two
/// is normally set; the unique index over <c>(ModuleID, PermissionID, RoleID, UserID)</c> stops the
/// same subject being granted the same permission twice.
/// </para>
/// </remarks>
public sealed class ModulePermission : Entity<int>
{
    /// <summary>Gets or sets the surrogate key (<c>ModulePermissionID</c>, identity from 1).</summary>
    public int ModulePermissionId { get; set; }

    /// <inheritdoc />
    public override int Identity => ModulePermissionId;

    /// <summary>Gets or sets the module instance the grant applies to (<c>ModuleID</c>, required, cascade delete).</summary>
    public int ModuleId { get; set; }

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

    /// <summary>Gets or sets the module instance the grant applies to.</summary>
    public Module? Module { get; set; }

    /// <summary>Gets or sets the catalogue entry granted.</summary>
    public Permission? Permission { get; set; }

    /// <summary>Gets or sets the role the grant is made to.</summary>
    public Role? Role { get; set; }

    /// <summary>Gets or sets the individual user the grant is made to.</summary>
    public User? User { get; set; }
}
