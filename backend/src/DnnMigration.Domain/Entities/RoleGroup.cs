using DnnMigration.Domain.Common;

namespace DnnMigration.Domain.Entities;

// MIGRATION: replaces DotNetNuke.Security.Roles.RoleGroupInfo
// (Library/Components/Security/Roles/RoleGroupInfo.vb lines 41-107). The legacy class declares four
// private backing fields and four Property Get/Set blocks that do nothing but read and write them,
// so the target is four auto-properties and the fields disappear. It carries no attribute of any
// kind, and every one of its five Imports - the base library, untyped collections, configuration,
// ADO.NET and XML serialisation - is unused by any member of it, so not one of them produces an
// import here. The single import below is the base entity; the Domain project references nothing
// beyond the framework by design.
//
// MIGRATION: ZERO IS A LEGITIMATE RoleGroupID. The column is declared IDENTITY(0, 1) in
// Website/Providers/DataProviders/SqlDataProvider/03.02.03.SqlDataProvider line 18, so the first
// group ever inserted into a portal is numbered 0 and every subsequent group counts up from there.
// Nothing anywhere may read 0 - or the default of int, which is the same value - as "no group", "not
// saved yet" or "absent". Concretely: never write `RoleGroupId == 0`, `RoleGroupId <= 0`,
// `RoleGroupId == default`, nor add an IsNew or IsTransient member to this type or infer one from it.
// Whether a group has been written to the database is DECLARED through
// Entity<int>.MarkIdentityPersisted by code that already knows it - nothing declares it automatically,
// so a materialised group reports IdentityIsPersisted as false - and read back through
// IdentityIsPersisted; it is never deduced from the key. The absence of a group is expressed where it is genuinely optional - by the nullable
// Role.RoleGroupId foreign key on the referencing side - and never by a reserved value here.
//
// MIGRATION: the legacy Null sentinel table (Library/Components/Shared/Null.vb) is not honoured by
// this entity. dbo.RoleGroups has exactly one nullable column, Description, and it is the only
// property here declared nullable; the two int columns and RoleGroupName are NOT NULL, so no value
// of theirs stands in for absence.
//
// MIGRATION: RoleGroupCollection has no counterpart. The pre-generics collection wrappers that the
// legacy tree derived from CollectionBase are subsumed by the framework's own generic collection
// interfaces, so Roles below is an ICollection<Role> and no wrapper type is introduced.

/// <summary>
/// A named grouping of roles within one portal, used to organise the administrative role lists.
/// </summary>
/// <remarks>
/// <para>
/// A group is purely organisational: it gathers roles for presentation and carries no permission of
/// its own, because permissions are granted to a <see cref="Role"/> and never to its group.
/// Membership is optional for a role, so an ungrouped role is entirely normal.
/// </para>
/// <para>
/// The table arrived complete and was never widened. It is created in
/// <c>Website/Providers/DataProviders/SqlDataProvider/03.02.03.SqlDataProvider</c> (lines 14-38) and
/// re-issued identically, under the same <c>IF NOT EXISTS</c> guard, in <c>04.00.04</c> (lines
/// 47-70) for installations that skipped the earlier script. Across the whole 88-script upgrade
/// chain no statement adds, alters or drops a column of it, so the four properties below are the
/// terminal shape of <c>dbo.RoleGroups</c> and this entity neither needs nor implies a schema change.
/// </para>
/// <para>
/// The mapping contract that the Infrastructure layer binds through its Fluent configuration:
/// </para>
/// <list type="table">
///   <listheader>
///     <term>Property and column</term>
///     <description>Terminal definition and consequence</description>
///   </listheader>
///   <item>
///     <term><see cref="RoleGroupId"/> maps <c>RoleGroupID</c></term>
///     <description>
///     <c>int IDENTITY(0, 1) NOT NULL</c>, the primary key under <c>PK_RoleGroups</c>. Generated on
///     insert, seeded at 0, so 0 is a real key and never a marker - see the migration note above.
///     </description>
///   </item>
///   <item>
///     <term><see cref="PortalId"/> maps <c>PortalID</c></term>
///     <description>
///     <c>int NOT NULL</c>, the required tenant reference, constrained by
///     <c>FK_RoleGroups_Portals</c> against <c>dbo.Portals(PortalID)</c> <c>ON DELETE CASCADE</c>.
///     Unlike a role, a group has no host-level form: there is no portal-less group, and deleting a
///     portal deletes its groups with it.
///     </description>
///   </item>
///   <item>
///     <term><see cref="RoleGroupName"/> maps <c>RoleGroupName</c></term>
///     <description><c>nvarchar(50) NOT NULL</c>, the administrative display name.</description>
///   </item>
///   <item>
///     <term><see cref="Description"/> maps <c>Description</c></term>
///     <description>
///     <c>nvarchar(1000) NULL</c> - the one nullable column of the table, and therefore the one
///     nullable property of this entity.
///     </description>
///   </item>
///   <item>
///     <term>Unique key</term>
///     <description>
///     <c>IX_RoleGroupName</c>, <c>UNIQUE NONCLUSTERED (PortalID ASC, RoleGroupName ASC)</c>. A name
///     is unique within its portal and not across the installation, so two portals may each own a
///     group of the same name.
///     </description>
///   </item>
/// </list>
/// <para>
/// MIGRATION: the referencing side is asymmetric with the owning side, and the difference is
/// load-bearing. The same script adds <c>RoleGroupID int NULL</c> to <c>dbo.Roles</c> and constrains
/// it with <c>FK_Roles_RoleGroups</c>, which carries <b>no</b> cascade (03.02.03 lines 33-37). So
/// deleting a portal removes its groups by cascade, but deleting a group on its own does not remove
/// or detach the roles pointing at it: the referencing roles have to be reassigned or cleared
/// deliberately first, or the delete is refused by the constraint. That is legacy behaviour and is
/// preserved rather than smoothed over.
/// </para>
/// </remarks>
public sealed class RoleGroup : Entity<int>
{
    /// <summary>
    /// Gets or sets the surrogate key of this group (<c>RoleGroupID</c>).
    /// </summary>
    /// <value>
    /// The database-generated identity, seeded at 0. A value of 0 identifies the first group of a
    /// portal and must never be interpreted as an unset, missing or unsaved key.
    /// </value>
    public int RoleGroupId { get; set; }

    /// <inheritdoc />
    public override int Identity => RoleGroupId;

    /// <summary>
    /// Gets or sets the portal that owns this group (<c>PortalID</c>, required, cascade delete).
    /// </summary>
    /// <value>
    /// The identity of the owning portal. Non-nullable because the column is <c>NOT NULL</c>; note
    /// that <c>dbo.Portals.PortalID</c> is itself <c>IDENTITY(-1, 1)</c>, so -1 and 0 are both real
    /// portal identities here and neither means "no portal".
    /// </value>
    public int PortalId { get; set; }

    /// <summary>
    /// Gets or sets the display name of this group, unique within its portal
    /// (<c>RoleGroupName</c>, required, 50 characters).
    /// </summary>
    /// <remarks>
    /// The initialiser keeps a freshly constructed instance non-null until the caller or the
    /// materialiser assigns the real name. It is not a sentinel for absence: the column is
    /// <c>NOT NULL</c>, so this entity has no way to represent a group without a name, and an empty
    /// name is rejected by the Application layer rather than stored.
    /// </remarks>
    public string RoleGroupName { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the administrative description of this group (<c>Description</c>, 1000
    /// characters).
    /// </summary>
    /// <value>
    /// The description, or <see langword="null"/> when the column is <c>NULL</c>. Nullability
    /// mirrors the column exactly, so a null description and an empty one stay distinguishable
    /// instead of collapsing onto the legacy empty-string sentinel.
    /// </value>
    public string? Description { get; set; }

    /// <summary>
    /// Gets or sets the portal that owns this group.
    /// </summary>
    /// <value>
    /// The owning portal. The relationship is required - <see cref="PortalId"/> is
    /// <c>NOT NULL</c> and its constraint cascades - so this reference is declared non-nullable to
    /// state that a persisted group always belongs to exactly one portal.
    /// </value>
    /// <remarks>
    /// Only a query that explicitly loads the principal populates this navigation; the write path
    /// sets <see cref="PortalId"/> and leaves the navigation to the persistence layer. The C#
    /// <c>required</c> modifier is deliberately not applied: it would oblige every construction
    /// site, including the Application-layer mapper that builds an unsaved group from a submitted
    /// contract, to supply a whole portal aggregate that the object-relational mapper would then
    /// track as a second, spurious portal insert.
    /// </remarks>
    public Portal Portal { get; set; }

    /// <summary>
    /// Gets the roles that belong to this group.
    /// </summary>
    /// <value>
    /// The inverse of <c>Role.RoleGroup</c>, empty for a group no role has joined yet. Never
    /// <see langword="null"/>: the collection is initialised on construction so that callers and the
    /// materialiser can add to it without a null check, and the reference itself is fixed for the
    /// lifetime of the instance.
    /// </value>
    public ICollection<Role> Roles { get; } = [];
}
