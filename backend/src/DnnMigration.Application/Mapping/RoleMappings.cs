using DnnMigration.Application.Dtos.Role;
using DnnMigration.Domain.Entities;

namespace DnnMigration.Application.Mapping;

/// <summary>
/// Hand-written projections between the <see cref="Role"/> and <see cref="RoleGroup"/> aggregates and
/// the role transfer contracts.
/// </summary>
/// <remarks>
/// <para>
/// MIGRATION: the five paid-membership members of the legacy role entity - the service fee, the
/// billing period and frequency, and the trial fee, period and frequency - are carried through
/// unchanged. They are stored data with live rules behind them, not decoration: the frequency column
/// is a single character and its values drive the expiry arithmetic that the assignment path performs,
/// so the enumeration that models it keeps the legacy character values rather than renumbering them.
/// </para>
/// <para>
/// The role group name and the member tally are supplied as arguments rather than read from a
/// navigation. Neither is a column on the role: the name belongs to the group the role points at, and
/// the tally is a count of assignment rows. Passing them in keeps this type free of data access and
/// keeps a list read from having to load a graph it does not need, since the list contract carries
/// neither value.
/// </para>
/// </remarks>
public static class RoleMappings
{
    /// <summary>
    /// Projects a role onto the row shape the role list screen renders.
    /// </summary>
    /// <param name="role">The role to project.</param>
    /// <returns>The list row.</returns>
    public static RoleListItemDto ToListItem(Role role)
    {
        ArgumentNullException.ThrowIfNull(role);

        return new RoleListItemDto
        {
            RoleId = role.RoleId,
            RoleName = role.RoleName,
            Description = role.Description,
            ServiceFee = role.ServiceFee,
            BillingPeriod = role.BillingPeriod,
            BillingFrequency = role.BillingFrequency,
            TrialFee = role.TrialFee,
            TrialPeriod = role.TrialPeriod,
            TrialFrequency = role.TrialFrequency,
            IsPublic = role.IsPublic,
            AutoAssignment = role.AutoAssignment,
        };
    }

    /// <summary>
    /// Projects a role onto the full detail contract.
    /// </summary>
    /// <param name="role">The role to project.</param>
    /// <param name="roleGroupName">Name of the group the role belongs to, or <see langword="null"/> when it belongs to none.</param>
    /// <param name="userCount">The number of members holding the role.</param>
    /// <returns>The detail contract.</returns>
    public static RoleDetailDto ToDetail(Role role, string? roleGroupName, int userCount)
    {
        ArgumentNullException.ThrowIfNull(role);

        return new RoleDetailDto
        {
            RoleId = role.RoleId,
            PortalId = role.PortalId,
            RoleName = role.RoleName,
            Description = role.Description,
            ServiceFee = role.ServiceFee,
            BillingPeriod = role.BillingPeriod,
            BillingFrequency = role.BillingFrequency,
            TrialFee = role.TrialFee,
            TrialPeriod = role.TrialPeriod,
            TrialFrequency = role.TrialFrequency,
            IsPublic = role.IsPublic,
            AutoAssignment = role.AutoAssignment,
            RoleGroupId = role.RoleGroupId,
            RoleGroupName = roleGroupName,
            RsvpCode = role.RsvpCode,
            IconFile = role.IconFile,
            UserCount = userCount,
        };
    }

    /// <summary>
    /// Projects a role group onto its transfer contract.
    /// </summary>
    /// <param name="roleGroup">The group to project.</param>
    /// <returns>The group contract.</returns>
    public static RoleGroupDto ToDto(RoleGroup roleGroup)
    {
        ArgumentNullException.ThrowIfNull(roleGroup);

        return new RoleGroupDto
        {
            RoleGroupId = roleGroup.RoleGroupId,
            PortalId = roleGroup.PortalId,
            RoleGroupName = roleGroup.RoleGroupName,
            Description = roleGroup.Description,
        };
    }

    /// <summary>
    /// Builds a new role aggregate from a creation request.
    /// </summary>
    /// <param name="portalId">Identifier of the portal the role belongs to.</param>
    /// <param name="request">The submitted creation request.</param>
    /// <returns>An unsaved role aggregate.</returns>
    /// <remarks>
    /// The portal identifier comes from the route, never from the payload, so a caller cannot create a
    /// role inside another tenant.
    /// </remarks>
    public static Role ToNewRole(int portalId, CreateRoleRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var role = new Role { PortalId = portalId };
        ApplyCore(
            role,
            request.RoleName,
            request.Description,
            request.RoleGroupId,
            request.IsPublic,
            request.AutoAssignment,
            request.ServiceFee,
            request.BillingPeriod,
            request.BillingFrequency,
            request.TrialFee,
            request.TrialPeriod,
            request.TrialFrequency,
            request.RsvpCode,
            request.IconFile);
        return role;
    }

    /// <summary>
    /// Applies a submitted update to a tracked role aggregate.
    /// </summary>
    /// <param name="role">The tracked role to modify.</param>
    /// <param name="request">The submitted values.</param>
    /// <remarks>
    /// Neither the role's own identifier nor its portal is written from the request: both arrive from
    /// the route, and the request contract deliberately carries neither.
    /// </remarks>
    public static void ApplyUpdate(Role role, UpdateRoleRequest request)
    {
        ArgumentNullException.ThrowIfNull(role);
        ArgumentNullException.ThrowIfNull(request);

        ApplyCore(
            role,
            request.RoleName,
            request.Description,
            request.RoleGroupId,
            request.IsPublic,
            request.AutoAssignment,
            request.ServiceFee,
            request.BillingPeriod,
            request.BillingFrequency,
            request.TrialFee,
            request.TrialPeriod,
            request.TrialFrequency,
            request.RsvpCode,
            request.IconFile);
    }

    /// <summary>
    /// Builds a new role group aggregate from a submitted contract.
    /// </summary>
    /// <param name="portalId">Identifier of the portal the group belongs to.</param>
    /// <param name="request">The submitted group.</param>
    /// <returns>An unsaved role group aggregate.</returns>
    public static RoleGroup ToNewGroup(int portalId, RoleGroupDto request)
    {
        ArgumentNullException.ThrowIfNull(request);

        return new RoleGroup
        {
            PortalId = portalId,
            RoleGroupName = request.RoleGroupName,
            Description = request.Description,
        };
    }

    /// <summary>
    /// Applies a submitted update to a tracked role group aggregate.
    /// </summary>
    /// <param name="roleGroup">The tracked group to modify.</param>
    /// <param name="request">The submitted values.</param>
    public static void ApplyGroupUpdate(RoleGroup roleGroup, RoleGroupDto request)
    {
        ArgumentNullException.ThrowIfNull(roleGroup);
        ArgumentNullException.ThrowIfNull(request);

        roleGroup.RoleGroupName = request.RoleGroupName;
        roleGroup.Description = request.Description;
    }

    /// <summary>
    /// Writes the member set that the creation and update requests share, so the two paths cannot
    /// drift apart.
    /// </summary>
    /// <remarks>
    /// The two request contracts declare identical member sets on purpose - the legacy edit screen was
    /// one screen serving both operations - so the assignment order and the clamping rules are stated
    /// once here rather than twice. Monetary values are clamped so a negative submission is stored as
    /// zero, reproducing the legacy role-creation clamp; the legacy edit screen's own comparison
    /// validators refused a negative fee before it reached the store, so the clamp changes no accepted
    /// input and only removes a way to store a nonsensical value.
    /// </remarks>
    private static void ApplyCore(
        Role role,
        string roleName,
        string? description,
        int? roleGroupId,
        bool isPublic,
        bool autoAssignment,
        decimal? serviceFee,
        int? billingPeriod,
        Domain.Enums.BillingFrequency? billingFrequency,
        decimal? trialFee,
        int? trialPeriod,
        Domain.Enums.BillingFrequency? trialFrequency,
        string? rsvpCode,
        string? iconFile)
    {
        role.RoleName = roleName;
        role.Description = description;
        role.RoleGroupId = roleGroupId;
        role.IsPublic = isPublic;
        role.AutoAssignment = autoAssignment;
        role.ServiceFee = serviceFee is null ? null : PortalMappings.ClampFee(serviceFee.Value);
        role.BillingPeriod = billingPeriod;
        role.BillingFrequency = billingFrequency;
        role.TrialFee = trialFee is null ? null : PortalMappings.ClampFee(trialFee.Value);
        role.TrialPeriod = trialPeriod;
        role.TrialFrequency = trialFrequency;
        role.RsvpCode = rsvpCode;
        role.IconFile = iconFile;
    }
}
