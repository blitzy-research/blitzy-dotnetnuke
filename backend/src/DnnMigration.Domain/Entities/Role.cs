using DnnMigration.Domain.Common;
using DnnMigration.Domain.Enums;

namespace DnnMigration.Domain.Entities;

/// <summary>
/// A security role: the grouping to which permissions are granted and users are assigned.
/// </summary>
/// <remarks>
/// <para>
/// MIGRATION: replaces the 15-property <c>DotNetNuke.Security.Roles.RoleInfo</c>
/// (Library/Components/Security/Roles/RoleInfo.vb). Bound to <c>dbo.Roles</c>; the paid-membership
/// columns are preserved because <c>UserRole</c> expiry arithmetic is driven from them.
/// </para>
/// <para>
/// MIGRATION: <c>RoleID</c> is <c>IDENTITY(0, 1)</c>, so 0 is the first real role and must never be
/// read as "absent". The legacy <c>FK_Roles_CodeFrequency</c> constraint over
/// <c>BillingFrequency</c> was dropped in 03.00.01, so the <c>char(1)</c> codes are validated by
/// <see cref="Enums.BillingFrequency"/> in the target rather than by the database.
/// </para>
/// </remarks>
public sealed class Role : Entity<int>
{
    /// <summary>Gets or sets the surrogate key (<c>RoleID</c>, identity seeded at 0).</summary>
    public int RoleId { get; set; }

    /// <inheritdoc />
    public override int Identity => RoleId;

    /// <summary>Gets or sets the owning portal (<c>PortalID</c>, nullable for a host role), cascade delete.</summary>
    public int? PortalId { get; set; }

    /// <summary>Gets or sets the role name, unique within its portal (<c>RoleName</c>, required, 50 characters).</summary>
    public string RoleName { get; set; } = string.Empty;

    /// <summary>Gets or sets the administrative description (<c>Description</c>, 1000 characters).</summary>
    public string? Description { get; set; }

    /// <summary>Gets or sets the recurring fee (<c>ServiceFee decimal(5, 2)</c>, default 0).</summary>
    public decimal? ServiceFee { get; set; }

    /// <summary>Gets or sets the number of <see cref="BillingFrequency"/> units in a billing cycle (<c>BillingPeriod</c>).</summary>
    public int? BillingPeriod { get; set; }

    /// <summary>Gets or sets the billing cycle unit (<c>BillingFrequency char(1)</c>).</summary>
    public BillingFrequency? BillingFrequency { get; set; }

    /// <summary>Gets or sets the one-off fee charged for a trial (<c>TrialFee money</c>).</summary>
    public decimal? TrialFee { get; set; }

    /// <summary>Gets or sets the number of <see cref="TrialFrequency"/> units in a trial (<c>TrialPeriod</c>).</summary>
    public int? TrialPeriod { get; set; }

    /// <summary>Gets or sets the trial period unit (<c>TrialFrequency char(1)</c>).</summary>
    public BillingFrequency? TrialFrequency { get; set; }

    /// <summary>Gets or sets whether users may subscribe themselves (<c>IsPublic</c>, required, default false).</summary>
    public bool IsPublic { get; set; }

    /// <summary>Gets or sets whether new users are assigned automatically (<c>AutoAssignment</c>, required, default false).</summary>
    public bool AutoAssignment { get; set; }

    /// <summary>Gets or sets the owning role group (<c>RoleGroupID</c>, nullable: ungrouped roles are the norm).</summary>
    public int? RoleGroupId { get; set; }

    /// <summary>Gets or sets the code a user presents to join by invitation (<c>RSVPCode</c>, 50 characters).</summary>
    public string? RsvpCode { get; set; }

    /// <summary>Gets or sets the icon shown beside the role (<c>IconFile</c>, 100 characters).</summary>
    public string? IconFile { get; set; }

    /// <summary>Gets or sets the owning portal.</summary>
    public Portal? Portal { get; set; }

    /// <summary>Gets or sets the owning role group.</summary>
    public RoleGroup? RoleGroup { get; set; }

    /// <summary>Gets the user assignments to this role.</summary>
    public ICollection<UserRole> UserRoles { get; } = new List<UserRole>();

    /// <summary>Gets the module permission grants made to this role.</summary>
    public ICollection<ModulePermission> ModulePermissions { get; } = new List<ModulePermission>();

    /// <summary>Gets the page permission grants made to this role.</summary>
    public ICollection<TabPermission> TabPermissions { get; } = new List<TabPermission>();
}
