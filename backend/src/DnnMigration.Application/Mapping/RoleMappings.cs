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
/// Neither role projection carries a join denormalisation. The group's name is not a column on the
/// role - it belongs to the group the role points at, and therefore to <see cref="RoleGroupDto"/> -
/// and a member tally is a count of assignment rows that no legacy role screen displayed. Excluding
/// both is what keeps every projection here a pure function of the aggregate it is handed, so no
/// mapping call can provoke a further read, and a caller never has to load a graph the contract does
/// not expose.
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
    /// <returns>The detail contract.</returns>
    /// <remarks>
    /// Every one of the detail contract's fourteen members is drawn from a column on this one
    /// aggregate, so the projection needs no argument beyond the role itself and can never trigger a
    /// further read.
    /// </remarks>
    // MIGRATION: this projection deliberately carries NEITHER the owning portal identifier NOR any
    // join denormalisation, matching the fourteen members RoleDetailDto actually declares.
    //   * The portal identifier is omitted because the legacy editor never posted it either -
    //     EditRoles.ascx.vb L232 assigned it from ambient page state - and the migrated route
    //     /api/v1/portals/{portalId}/roles/{roleId} already carries it. The list projection above
    //     omits it for the same reason, so the two role contracts stay consistent.
    //   * The group's NAME and a member TALLY were both carried by an earlier revision of this
    //     method, which took them as arguments because neither is a column on dbo.Roles. Their
    //     removal is a behavioural improvement rather than a loss: the group name belongs to
    //     RoleGroupDto and the legacy editor resolved it client-side from the group drop-down it had
    //     already bound (BindGroups, EditRoles.ascx.vb L75-L78), while no legacy role screen showed
    //     a member tally at all. Supplying them obliged the caller to issue two further reads per
    //     single-role request - one for the group, one for the total of a one-row page of
    //     assignments consulted purely for its count - so a detail read is now a single-row query.
    public static RoleDetailDto ToDetail(Role role)
    {
        ArgumentNullException.ThrowIfNull(role);

        return new RoleDetailDto
        {
            RoleId = role.RoleId,
            RoleGroupId = role.RoleGroupId,
            RoleName = role.RoleName,
            Description = role.Description,
            BillingFrequency = role.BillingFrequency,
            ServiceFee = role.ServiceFee,
            TrialFrequency = role.TrialFrequency,
            TrialPeriod = role.TrialPeriod,
            BillingPeriod = role.BillingPeriod,
            TrialFee = role.TrialFee,
            IsPublic = role.IsPublic,
            AutoAssignment = role.AutoAssignment,
            RsvpCode = role.RsvpCode,
            IconFile = role.IconFile,
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
    /// <para>
    /// Neither the role's own identifier nor its portal is written from the request: both arrive from
    /// the route, and the request contract deliberately carries neither.
    /// </para>
    /// <para>
    /// The role's NAME is likewise not written from the request, because the update contract does not
    /// carry one. The tracked entity's existing name is passed straight back through, so an update
    /// preserves it.
    /// </para>
    /// </remarks>
    public static void ApplyUpdate(Role role, UpdateRoleRequest request)
    {
        ArgumentNullException.ThrowIfNull(role);
        ArgumentNullException.ThrowIfNull(request);

        // MIGRATION: the stored name is passed through unchanged rather than taken from the request,
        // because renaming a role was never a legacy workflow. The edit screen revealed a read-only
        // label and hid the name textbox whenever it was editing an existing role
        // (Website/admin/Security/EditRoles.ascx.vb L131-L134), the legacy membership data contract
        // declared no parameter for the name on its update member
        // (Library/Providers/MembershipProviders/DataProvider/DataProvider.vb L97), the provider
        // never passed one (DNNRoleProvider.vb L325), and the terminal stored procedure omits the
        // column from its assignment list altogether
        // (Website/Providers/DataProviders/SqlDataProvider/04.00.04.SqlDataProvider L454). Passing
        // the entity's own value keeps this projection total - every writable member of the role is
        // still assigned exactly once - without inventing a rename capability the application never
        // had.
        ApplyCore(
            role,
            role.RoleName,
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
