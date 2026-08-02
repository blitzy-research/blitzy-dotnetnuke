using DnnMigration.Domain.Common;

namespace DnnMigration.Domain.Entities;

/// <summary>
/// A named grouping of roles, used only to organise the administrative role lists.
/// </summary>
/// <remarks>
/// MIGRATION: replaces the four-property <c>DotNetNuke.Security.Roles.RoleGroupInfo</c>. Bound to
/// <c>dbo.RoleGroups</c> (03.02.03). <c>RoleGroupID</c> is <c>IDENTITY(0, 1)</c>, so 0 is a real
/// group. Membership of a group is optional for a role, and <c>Roles.RoleGroupID</c> carries no
/// cascade, so deleting a group must clear the referencing roles deliberately.
/// </remarks>
public sealed class RoleGroup : Entity<int>
{
    /// <summary>Gets or sets the surrogate key (<c>RoleGroupID</c>, identity seeded at 0).</summary>
    public int RoleGroupId { get; set; }

    /// <inheritdoc />
    public override int Identity => RoleGroupId;

    /// <summary>Gets or sets the owning portal (<c>PortalID</c>, required, cascade delete).</summary>
    public int PortalId { get; set; }

    /// <summary>Gets or sets the group name, unique within its portal (<c>RoleGroupName</c>, required, 50 characters).</summary>
    public string RoleGroupName { get; set; } = string.Empty;

    /// <summary>Gets or sets the administrative description (<c>Description</c>, 1000 characters).</summary>
    public string? Description { get; set; }

    /// <summary>Gets or sets the owning portal.</summary>
    public Portal? Portal { get; set; }

    /// <summary>Gets the roles that belong to this group.</summary>
    public ICollection<Role> Roles { get; } = new List<Role>();
}
