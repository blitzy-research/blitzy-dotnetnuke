using DnnMigration.Domain.Common;
using DnnMigration.Domain.Enums;

namespace DnnMigration.Domain.Entities;

/// <summary>
/// A security role: the named grouping that permissions are granted to and that user accounts are assigned
/// to, optionally carrying the paid-membership terms under which an assignment expires.
/// </summary>
/// <remarks>
/// <para>
/// A role is either a tenant role, owned by one portal, or a host role owned by the installation itself -
/// the distinction is carried by <see cref="PortalId"/> being present or absent, and both forms are normal.
/// Membership of a <see cref="RoleGroup"/> is organisational and optional, so an ungrouped role is normal
/// too.
/// </para>
/// <para>
/// There is no <c>RoleStatus</c> property, and none may be added. Status classifies a user's
/// <i>assignment</i> to a role - it is derived from the effective and expiry dates on <c>dbo.UserRoles</c>
/// - so a role definition has no status and <c>dbo.Roles</c> has no such column.
/// </para>
/// </remarks>
public sealed class Role : Entity<int>
{
    // ZERO IS A LEGITIMATE RoleID. The column is declared IDENTITY(0, 1) in
    // Website/Providers/DataProviders/SqlDataProvider/01.00.00.SqlDataProvider line 115, and both later
    // rebuilds of the table restate that seed (01.00.04 line 1322 and 01.00.05 line 2748), so the first
    // role ever inserted is numbered 0 - and in a freshly provisioned installation that row is the
    // Administrators role, the most privileged one there is.

    /// <summary>Gets or sets the surrogate key of this role (<c>RoleID</c>).</summary>
    /// <value>The database-generated identity, seeded at 0.</value>
    public int RoleId { get; set; }

    /// <inheritdoc />
    /// <remarks>
    /// Never mapped to a column of its own; the entity configuration names <see cref="RoleId"/> as the key.
    /// Zero is a real identity here, so see the migration note above before comparing it against any
    /// reserved value.
    /// </remarks>
    public override int Identity => RoleId;

    /// <summary>Gets or sets the portal that owns this role (<c>PortalID</c>, cascade delete).</summary>
    /// <value>The identity of the owning portal.</value>
    public int? PortalId { get; set; }

    /// <summary>Gets or sets the role group that this role belongs to (<c>RoleGroupID</c>).</summary>
    /// <value>
    /// The identity of the owning group, or <see langword="null"/> when the role is ungrouped, which is the
    /// ordinary case.
    /// </value>
    public int? RoleGroupId { get; set; }

    /// <summary>
    /// Gets or sets the display name of this role, unique within its portal (<c>RoleName</c>, required, 50
    /// characters).
    /// </summary>
    /// <remarks>
    /// The initialiser keeps a freshly constructed instance non-null until the caller or the materialiser
    /// assigns the real name. It is not the legacy empty-string null sentinel and not a marker for absence:
    /// the column is <c>NOT NULL</c>, so this entity has no way to represent a role without a name, and an
    /// empty name is rejected by the Application layer rather than stored.
    /// </remarks>
    public string RoleName { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the administrative description of this role (<c>Description</c>, 1000 characters).
    /// </summary>
    /// <value>The description, or <see langword="null"/> when the column is <c>NULL</c>.</value>
    public string? Description { get; set; }

    /// <summary>
    /// Gets or sets the recurring subscription fee for this role (<c>ServiceFee</c>, SQL <c>money</c>,
    /// database default 0).
    /// </summary>
    /// <value>
    /// The fee charged once per <see cref="BillingPeriod"/> units of <see cref="BillingFrequency"/>, or
    /// <see langword="null"/> when the column is <c>NULL</c>.
    /// </value>
    public decimal? ServiceFee { get; set; }

    // OBLIGATION ON INFRASTRUCTURE: persist through a STRING conversion over those six codes. Persisting
    // the enum ordinal instead would write a number into a char(1) column and silently mis-read every
    // existing row.

    /// <summary>
    /// Gets or sets the unit in which the billing cycle of this role is counted (<c>BillingFrequency</c>,
    /// SQL <c>char(1)</c>).
    /// </summary>
    /// <value>One of the six stored codes, or <see langword="null"/> when the column is <c>NULL</c>.</value>
    public BillingFrequency? BillingFrequency { get; set; }

    /// <summary>
    /// Gets or sets how many <see cref="TrialFrequency"/> units the trial period of this role runs for
    /// (<c>TrialPeriod</c>, SQL <c>int</c>).
    /// </summary>
    /// <value>The count of trial units, or <see langword="null"/> when the column is <c>NULL</c>.</value>
    public int? TrialPeriod { get; set; }

    /// <summary>
    /// Gets or sets the unit in which the trial period of this role is counted (<c>TrialFrequency</c>, SQL
    /// <c>char(1)</c>).
    /// </summary>
    /// <value>
    /// One of the same six stored codes as <see cref="BillingFrequency"/>, or <see langword="null"/> when
    /// the column is <c>NULL</c>.
    /// </value>
    public BillingFrequency? TrialFrequency { get; set; }

    // MIGRATION: int?, and the legacy provider's String declaration is a defect that is deliberately NOT
    // reproduced. Library/Providers/MembershipProviders/DataProvider/DataProvider.vb declares `ByVal
    // BillingPeriod As String` in both AddRole (line 95) and UpdateRole (line 97).

    /// <summary>
    /// Gets or sets how many <see cref="BillingFrequency"/> units the billing cycle of this role runs for
    /// (<c>BillingPeriod</c>, SQL <c>int</c>).
    /// </summary>
    /// <value>The count of billing units, or <see langword="null"/> when the column is <c>NULL</c>.</value>
    public int? BillingPeriod { get; set; }

    // Legacy Single becomes decimal? here too.

    /// <summary>
    /// Gets or sets the one-off fee charged for the trial period of this role (<c>TrialFee</c>, SQL
    /// <c>money</c>).
    /// </summary>
    /// <value>The trial fee, or <see langword="null"/> when the column is <c>NULL</c>.</value>
    public decimal? TrialFee { get; set; }

    /// <summary>
    /// Gets or sets whether users may subscribe themselves to this role (<c>IsPublic</c>, required,
    /// database default <see langword="false"/>).
    /// </summary>
    /// <value><see langword="true"/> when the role is offered for self-subscription.</value>
    public bool IsPublic { get; set; }

    /// <summary>
    /// Gets or sets whether every newly registered user is assigned to this role automatically
    /// (<c>AutoAssignment</c>, required, database default <see langword="false"/>).
    /// </summary>
    /// <value><see langword="true"/> when membership is granted on registration.</value>
    public bool AutoAssignment { get; set; }

    /// <summary>
    /// Gets or sets the invitation code a user presents in order to join this role (<c>RSVPCode</c>, 50
    /// characters).
    /// </summary>
    /// <value>The code, or <see langword="null"/> when the role is not joinable by invitation.</value>
    public string? RsvpCode { get; set; }

    /// <summary>
    /// Gets or sets the icon displayed beside this role in the administrative user interface
    /// (<c>IconFile</c>, 100 characters).
    /// </summary>
    /// <value>The raw stored file reference, or <see langword="null"/> when none is set.</value>
    public string? IconFile { get; set; }

    /// <summary>Gets or sets the portal that owns this role.</summary>
    /// <value>
    /// The owning portal, or <see langword="null"/> either because the role is a host role with no portal
    /// at all or because the navigation was simply not loaded.
    /// </value>
    public Portal? Portal { get; set; }

    /// <summary>Gets or sets the group that this role belongs to.</summary>
    /// <value>
    /// The owning group, or <see langword="null"/> either because the role is ungrouped or because the
    /// navigation was not loaded - a distinction carried by <see cref="RoleGroupId"/>.
    /// </value>
    public RoleGroup? RoleGroup { get; set; }

    /// <summary>Gets the assignments that join user accounts to this role.</summary>
    /// <value>The inverse of <c>UserRole.Role</c>, empty for a role nobody has been assigned to.</value>
    public ICollection<UserRole> UserRoles { get; } = new List<UserRole>();

    /// <summary>Gets the module permission grants made to this role.</summary>
    /// <value>
    /// The inverse of <c>ModulePermission.Role</c>, empty when the role has been granted nothing on any
    /// module.
    /// </value>
    public ICollection<ModulePermission> ModulePermissions { get; } = new List<ModulePermission>();

    /// <summary>Gets the page permission grants made to this role.</summary>
    /// <value>
    /// The inverse of <c>TabPermission.Role</c>, empty when the role has been granted nothing on any page.
    /// </value>
    public ICollection<TabPermission> TabPermissions { get; } = new List<TabPermission>();
}
