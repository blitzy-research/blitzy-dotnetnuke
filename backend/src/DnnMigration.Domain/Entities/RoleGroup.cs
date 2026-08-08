using DnnMigration.Domain.Common;

namespace DnnMigration.Domain.Entities;

// MIGRATION: ZERO IS A LEGITIMATE RoleGroupID. The column is IDENTITY(0, 1), so the first group of a portal
// is numbered 0 and neither 0 nor default(int) may be read as "no group", "absent" or "not saved yet".
// Whether a group has been written is declared through Entity<int>.MarkIdentityPersisted and read back
// through IdentityIsPersisted; it is never deduced from the key. Optionality is expressed by the nullable
// Role.RoleGroupId on the referencing side, never by a reserved value here.

/// <summary>
/// A named grouping of roles within one portal, used to organise the administrative role lists.
/// </summary>
/// <remarks>
/// A group is purely organisational: permissions are granted to a <see cref="Role"/> and never to
/// its group, and membership is optional, so an ungrouped role is normal. <c>IX_RoleGroupName</c>
/// makes <see cref="RoleGroupName"/> unique within a portal but not across the installation, so two
/// portals may each own a group of the same name.
/// <para>
/// MIGRATION: the two foreign keys are deliberately asymmetric, and the asymmetry is legacy
/// behaviour that is preserved rather than smoothed over. <c>FK_RoleGroups_Portals</c> cascades, so
/// deleting a portal deletes its groups; <c>FK_Roles_RoleGroups</c> does not, so deleting a group
/// while roles still reference it is refused by the constraint until those roles are reassigned or
/// cleared.
/// </para>
/// </remarks>
public sealed class RoleGroup : Entity<int>
{
    /// <summary>Gets or sets the surrogate key of this group (<c>RoleGroupID</c>).</summary>
    /// <value>The database-generated identity, seeded at 0, where 0 is a real key.</value>
    public int RoleGroupId { get; set; }

    /// <inheritdoc />
    public override int Identity => RoleGroupId;

    /// <summary>
    /// Gets or sets the portal that owns this group (<c>PortalID</c>, required, cascade delete).
    /// </summary>
    /// <value>
    /// The owning portal's identity. <c>dbo.Portals.PortalID</c> is <c>IDENTITY(-1, 1)</c>, so -1
    /// and 0 are both real portals here and neither means "no portal".
    /// </value>
    public int PortalId { get; set; }

    /// <summary>
    /// Gets or sets the display name of this group, unique within its portal (<c>RoleGroupName</c>,
    /// required, 50 characters).
    /// </summary>
    /// <remarks>
    /// The initialiser only keeps a freshly constructed instance non-null. It is not a sentinel for
    /// absence: the column is <c>NOT NULL</c>, and an empty name is rejected by the Application
    /// layer.
    /// </remarks>
    public string RoleGroupName { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the administrative description of this group (<c>Description</c>, 1000
    /// characters).
    /// </summary>
    /// <value>
    /// The description, or <see langword="null"/> for a <c>NULL</c> column. This is the table's
    /// only nullable column, so a null and an empty description stay distinguishable.
    /// </value>
    public string? Description { get; set; }

    /// <summary>Gets or sets the portal that owns this group.</summary>
    /// <value>The owning portal; only a query that loads the principal populates it.</value>
    /// <remarks>
    /// The C# <c>required</c> modifier is deliberately not applied: it would oblige every
    /// construction site, including the Application mapper that builds an unsaved group from a
    /// submitted contract, to supply a whole portal aggregate that the object-relational mapper
    /// would then track as a second, spurious portal insert.
    /// </remarks>
    public Portal Portal { get; set; }

    /// <summary>Gets the roles that belong to this group.</summary>
    /// <value>
    /// The inverse of <c>Role.RoleGroup</c>, empty for a group no role has joined. Never
    /// <see langword="null"/>, so callers and the materialiser can add without a null check.
    /// </value>
    public ICollection<Role> Roles { get; } = [];
}
