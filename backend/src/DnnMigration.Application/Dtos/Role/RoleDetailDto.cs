using DnnMigration.Domain.Enums;

namespace DnnMigration.Application.Dtos.Role;

// MIGRATION: the member set is the legacy edit screen's field set, measured from
// Website/admin/Security/editroles.ascx, which declares txtRoleName, txtDescription, cboRoleGroups,
// chkIsPublic, chkAutoAssignment, txtServiceFee with cboBillingFrequency and txtBillingPeriod,
// txtTrialFee with cboTrialFrequency and txtTrialPeriod, txtRSVPCode and the icon picker ctlIcon.
// txtRSVPLink is deliberately absent because it is a generated display value rather than stored
// state, and the terminal Roles table declares no column for it.
//
// MIGRATION: the paid-membership members are preserved rather than dropped. ServiceFee, BillingPeriod,
// BillingFrequency, TrialFee, TrialPeriod and TrialFrequency are all real terminal columns on
// dbo.Roles and all six were editable on the legacy screen, so removing them would be a silent loss
// of functionality even though no payment provider is in scope for this migration.
//
// MIGRATION: the single-character frequency codes are load-bearing data, not an implementation
// detail. Roles.BillingFrequency and Roles.TrialFrequency are char(1) columns, and
// RoleController.vb:L543-L546 switched on the literals D, W, M and Y to advance an expiry date. They
// are therefore exchanged as a domain enumeration whose members carry those exact persisted values,
// so the codes survive a round trip and are never renamed.
//
// MIGRATION: RoleGroupName is a read-only projection from dbo.RoleGroups, present so the detail
// screen can name the group without a second request. It is not accepted on either request contract;
// the group is chosen by identifier.
//
// MIGRATION: RoleID is IDENTITY (0, 1) and RoleGroupID is IDENTITY (0, 1), so 0 is a genuine
// identifier for both. No consumer may test either against 0 or -1 to decide whether it is present;
// an unassigned group is expressed by RoleGroupId being null.

/// <summary>
/// The full state of one role, returned by <c>GET /api/v1/roles/{roleId}</c> and by the create and
/// update endpoints so a caller sees the stored result of its own write.
/// </summary>
/// <remarks>
/// A boundary contract only: no navigation property, no tracked state and no behaviour. The grid
/// projection is <see cref="RoleListItemDto"/>; the editable subsets are <see cref="CreateRoleRequest"/>
/// and <see cref="UpdateRoleRequest"/>.
/// </remarks>
public sealed class RoleDetailDto
{
    /// <summary>The role's identity, mapped from <c>Roles.RoleID</c> (<c>IDENTITY (0, 1)</c>, so 0 is real).</summary>
    public int RoleId { get; set; }

    /// <summary>
    /// The portal the role belongs to, mapped from <c>Roles.PortalID</c>, or <see langword="null"/> for
    /// a host-level role that belongs to no portal. The column is genuinely nullable.
    /// </summary>
    public int? PortalId { get; set; }

    /// <summary>The role's name, mapped from <c>Roles.RoleName</c> (<c>nvarchar(50) NOT NULL</c>), unique within its portal.</summary>
    public string RoleName { get; set; } = string.Empty;

    /// <summary>The role's description, mapped from <c>Roles.Description</c> (<c>nvarchar(1000) NULL</c>).</summary>
    public string? Description { get; set; }

    /// <summary>The recurring subscription fee, mapped from <c>Roles.ServiceFee</c> (<c>decimal(5, 2)</c>), or <see langword="null"/> when the role is free.</summary>
    public decimal? ServiceFee { get; set; }

    /// <summary>How many <see cref="BillingFrequency"/> units one billing cycle spans, mapped from <c>Roles.BillingPeriod</c>.</summary>
    public int? BillingPeriod { get; set; }

    /// <summary>The unit the billing cycle is measured in, mapped from the <c>char(1)</c> column <c>Roles.BillingFrequency</c>.</summary>
    public BillingFrequency? BillingFrequency { get; set; }

    /// <summary>The one-off fee charged for the trial, mapped from <c>Roles.TrialFee</c>, or <see langword="null"/> when the trial is free.</summary>
    public decimal? TrialFee { get; set; }

    /// <summary>How many <see cref="TrialFrequency"/> units the trial spans, mapped from <c>Roles.TrialPeriod</c>.</summary>
    public int? TrialPeriod { get; set; }

    /// <summary>The unit the trial period is measured in, mapped from the <c>char(1)</c> column <c>Roles.TrialFrequency</c>.</summary>
    public BillingFrequency? TrialFrequency { get; set; }

    /// <summary>Whether users may subscribe to the role themselves, mapped from <c>Roles.IsPublic</c> (column default 0).</summary>
    public bool IsPublic { get; set; }

    /// <summary>Whether every new member of the portal receives the role automatically, mapped from <c>Roles.AutoAssignment</c> (column default 0).</summary>
    public bool AutoAssignment { get; set; }

    /// <summary>The role group this role is filed under, mapped from <c>Roles.RoleGroupID</c>, or <see langword="null"/> when it is ungrouped.</summary>
    public int? RoleGroupId { get; set; }

    /// <summary>The name of <see cref="RoleGroupId"/>, projected read-only from <c>dbo.RoleGroups</c>, or <see langword="null"/> when the role is ungrouped.</summary>
    public string? RoleGroupName { get; set; }

    /// <summary>The invitation code that lets a user self-assign the role, mapped from <c>Roles.RSVPCode</c> (<c>nvarchar(50) NULL</c>).</summary>
    public string? RsvpCode { get; set; }

    /// <summary>The role's icon, mapped from <c>Roles.IconFile</c> (<c>nvarchar(100) NULL</c>).</summary>
    public string? IconFile { get; set; }

    /// <summary>
    /// How many users currently hold the role.
    /// </summary>
    /// <remarks>
    /// Computed rather than stored: no count column exists on <c>dbo.Roles</c>. Counts every assignment
    /// row, including one whose effective window has not opened or has already closed, which is what
    /// the legacy membership screen listed.
    /// </remarks>
    public int UserCount { get; set; }
}
