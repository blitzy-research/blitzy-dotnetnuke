using DnnMigration.Domain.Common;

namespace DnnMigration.Domain.Entities;

/// <summary>A named grouping of roles within one portal, used to organise the administrative role lists.</summary>
/// <remarks>
/// The two foreign keys are deliberately asymmetric, and the asymmetry is legacy behaviour that is
/// preserved rather than smoothed over. <c>FK_RoleGroups_Portals</c> cascades, so deleting a portal deletes
/// its groups; <c>FK_Roles_RoleGroups</c> does not, so deleting a group while roles still reference it is
/// refused by the constraint until those roles are reassigned or cleared.
/// </remarks>
public sealed class RoleGroup : Entity<int>
{
    /// <summary>Gets or sets the surrogate key of this group (<c>RoleGroupID</c>).</summary>
    /// <value>The database-generated identity, seeded at 0, where 0 is a real key.</value>
    public int RoleGroupId { get; set; }

    /// <inheritdoc />
    public override int Identity => RoleGroupId;

    /// <summary>Gets or sets the portal that owns this group (<c>PortalID</c>, required, cascade delete).</summary>
    /// <value>
    /// The owning portal's identity. <c>dbo.Portals.PortalID</c> is <c>IDENTITY(-1, 1)</c>, so -1 and 0 are
    /// both real portals here and neither means "no portal".
    /// </value>
    public int PortalId { get; set; }

    /// <summary>
    /// Gets or sets the display name of this group, unique within its portal (<c>RoleGroupName</c>,
    /// required, 50 characters).
    /// </summary>
    /// <remarks>
    /// The initialiser only keeps a freshly constructed instance non-null. It is not a sentinel for
    /// absence: the column is <c>NOT NULL</c>, and an empty name is rejected by the Application layer.
    /// </remarks>
    public string RoleGroupName { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the administrative description of this group (<c>Description</c>, 1000 characters).
    /// </summary>
    /// <value>The description, or <see langword="null"/> for a <c>NULL</c> column.</value>
    public string? Description { get; set; }

    /// <summary>Gets or sets the portal that owns this group.</summary>
    /// <value>The owning portal; only a query that loads the principal populates it.</value>
    public Portal Portal { get; set; }

    /// <summary>Gets the roles that belong to this group.</summary>
    /// <value>The inverse of <c>Role.RoleGroup</c>, empty for a group no role has joined.</value>
    public ICollection<Role> Roles { get; } = [];
}
