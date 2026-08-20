using DnnMigration.Application.Common;
using DnnMigration.Application.Dtos.Role;
using DnnMigration.Domain.Common;
using DnnMigration.Domain.Entities;

namespace DnnMigration.Application.Mapping;

/// <summary>
/// Hand-written projections between the <see cref="Role"/>, <see cref="RoleGroup"/> and <see
/// cref="UserRole"/> aggregates and the six role transfer contracts.
/// </summary>
/// <remarks>
/// The five paid-membership members of the legacy role entity - the service fee, the billing period and
/// frequency, and the trial fee, period and frequency - are carried through unchanged.
/// </remarks>
public static class RoleMappings
{
    /// <summary>Projects a role onto the row shape the role list screen renders.</summary>
    /// <param name="role">The role to project.</param>
    /// <returns>The list row.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="role"/> is null.</exception>
    // RoleId is copied verbatim and is never tested for a sign. dbo.Roles.RoleID is declared IDENTITY(0, 1)
    // (01.00.00.SqlDataProvider L115), so ZERO identifies the first role a DotNetNuke database ever created
    // and is a wholly ordinary key.
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

    /// <summary>Projects a role onto the full detail contract.</summary>
    /// <param name="role">The role to project.</param>
    /// <returns>The detail contract.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="role"/> is null.</exception>
    /// <remarks>
    /// Every one of the detail contract's fourteen members is drawn from a column on this one aggregate, so
    /// the projection needs no argument beyond the role itself and can never trigger a further read.
    /// </remarks>
    // MIGRATION: this projection deliberately carries NEITHER the owning portal identifier NOR any join
    // denormalisation, matching the fourteen members RoleDetailDto actually declares. * The portal
    // identifier is omitted because the legacy editor never posted it either EditRoles.ascx.vb L232
    // assigned it from ambient page state - and the migrated request resolves it from the host before
    // /api/v1/roles/{roleId} runs.
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
            ConcurrencyToken = ConcurrencyTokenFor(role),
        };
    }

    /// <summary>
    /// Derives the optimistic-concurrency token a caller round-trips to prove it is replacing the record it
    /// read.
    /// </summary>
    /// <param name="role">The role as it currently stands.</param>
    /// <returns>The token.</returns>
    /// <remarks>
    /// ⚠ THE MEMBER ORDER IS PART OF THE CONTRACT. The token published by a read and the token verified by
    /// a write are both produced here, so a reordering changes both together and stays self-consistent -
    /// but a token already in a browser's hands would stop matching, and every open editor would be refused
    /// once. Adding a member has the same effect.
    /// </remarks>
    internal static string ConcurrencyTokenFor(Role role)
    {
        ArgumentNullException.ThrowIfNull(role);

        return ConcurrencyToken.From(
            role.RoleGroupId,
            role.RoleName,
            role.Description,
            role.BillingFrequency,
            role.ServiceFee,
            role.TrialFrequency,
            role.TrialPeriod,
            role.BillingPeriod,
            role.TrialFee,
            role.IsPublic,
            role.AutoAssignment,
            role.RsvpCode,
            role.IconFile);
    }

    /// <summary>Projects a role group onto its transfer contract.</summary>
    /// <remarks>
    /// ⚠ THE CLASSIFIED-ROLE COUNT IS A REQUIRED ARGUMENT RATHER THAN AN OPTIONAL ONE, AND THAT IS
    /// DELIBERATE. Defaulting it would let a caller project a group without deciding what its count is, and
    /// the value that would travel is zero - the one value that means "this group can be deleted". A caller
    /// who has not looked would therefore be publishing a claim it never checked, on the side that grants
    /// permission. Making it explicit forces every projection site to answer the question.
    /// </remarks>
    /// <param name="roleGroup">The group to project.</param>
    /// <param name="classifiedRoleCount">How many roles the group classifies.</param>
    /// <returns>The group contract.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="roleGroup"/> is null.</exception>
    public static RoleGroupDto ToDto(RoleGroup roleGroup, int classifiedRoleCount)
    {
        ArgumentNullException.ThrowIfNull(roleGroup);

        return new RoleGroupDto
        {
            RoleGroupId = roleGroup.RoleGroupId,
            PortalId = roleGroup.PortalId,
            RoleGroupName = roleGroup.RoleGroupName,
            Description = roleGroup.Description,
            ClassifiedRoleCount = classifiedRoleCount,
        };
    }

    /// <summary>
    /// Projects one role membership, together with the account and role it joins, into its wire contract.
    /// </summary>
    /// <param name="assignment">The assignment to project.</param>
    /// <returns>The membership contract.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="assignment"/> is null.</exception>
    public static RoleMembershipDto ToMembership(UserRole assignment)
    {
        ArgumentNullException.ThrowIfNull(assignment);

        ArgumentNullException.ThrowIfNull(assignment.User);
        ArgumentNullException.ThrowIfNull(assignment.Role);

        return new RoleMembershipDto
        {
            UserRoleId = assignment.UserRoleId,
            UserId = assignment.UserId,
            Username = assignment.User.Username,
            DisplayName = assignment.User.DisplayName,
            RoleId = assignment.RoleId,
            RoleName = assignment.Role.RoleName,
            EffectiveDate = assignment.EffectiveDate,
            ExpiryDate = assignment.ExpiryDate,
        };
    }

    /// <summary>
    /// Projects one role membership from parts the caller has already resolved, rather than from the
    /// assignment's navigations.
    /// </summary>
    /// <param name="assignment">The assignment to project.</param>
    /// <param name="role">The role it names.</param>
    /// <param name="account">The account it names.</param>
    /// <returns>The membership contract.</returns>
    /// <exception cref="ArgumentNullException">Thrown when any argument is null.</exception>
    /// <remarks>
    /// <b>Why an explicit overload exists.</b> The sibling overload requires both navigations to be loaded,
    /// which is true of the read behind the membership LISTING - one statement projecting columns from all
    /// three tables.
    /// </remarks>
    public static RoleMembershipDto ToMembership(UserRole assignment, Role role, User account)
    {
        ArgumentNullException.ThrowIfNull(assignment);
        ArgumentNullException.ThrowIfNull(role);
        ArgumentNullException.ThrowIfNull(account);

        if (assignment.RoleId != role.RoleId)
        {
            throw new ArgumentException(
                FormattableString.Invariant(
                    $"Assignment {assignment.UserRoleId} names role {assignment.RoleId}, not {role.RoleId}."),
                nameof(role));
        }

        if (assignment.UserId != account.UserId)
        {
            throw new ArgumentException(
                FormattableString.Invariant(
                    $"Assignment {assignment.UserRoleId} names account {assignment.UserId}, not {account.UserId}."),
                nameof(account));
        }

        return new RoleMembershipDto
        {
            UserRoleId = assignment.UserRoleId,
            UserId = assignment.UserId,
            Username = account.Username,
            DisplayName = account.DisplayName,
            RoleId = assignment.RoleId,
            RoleName = role.RoleName,
            EffectiveDate = assignment.EffectiveDate,
            ExpiryDate = assignment.ExpiryDate,
        };
    }

    /// <summary>Builds a new role aggregate from a creation request.</summary>
    /// <param name="portalId">Identifier of the portal the role belongs to.</param>
    /// <param name="request">The submitted creation request.</param>
    /// <returns>An unsaved role aggregate.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="request"/> is null.</exception>
    /// <remarks>
    /// The portal identifier comes from the route, never from the payload, so a caller cannot create a role
    /// inside another tenant.
    /// </remarks>
    // MIGRATION: portalId is assigned exactly as supplied, including -1. That is not a sentinel
    // here: it is the host portal, per the IDENTITY(-1, 1) seed cited on ToDetail above.
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

    /// <summary>Applies a submitted update to a tracked role aggregate.</summary>
    /// <param name="role">The tracked role to modify.</param>
    /// <param name="request">The submitted values.</param>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="role"/> or <paramref name="request"/> is null.
    /// </exception>
    /// <remarks>
    /// The role's NAME <em>is</em> written from the request, so an update replaces it like every other
    /// member. Uniqueness of the new name within the portal is not this projection's concern - it needs a
    /// read, so it is settled by <c>Application/Services/RoleService.cs</c> before this member is called.
    /// </remarks>
    public static void ApplyUpdate(Role role, UpdateRoleRequest request)
    {
        ArgumentNullException.ThrowIfNull(role);
        ArgumentNullException.ThrowIfNull(request);

        // MIGRATION - DOCUMENTED BEHAVIOURAL DIFFERENCE: the submitted name is applied, where the legacy
        // edit path could not change one.
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

    /// <summary>Builds a new role group aggregate from a submitted contract.</summary>
    /// <param name="portalId">Identifier of the portal the group belongs to.</param>
    /// <param name="request">The submitted group.</param>
    /// <returns>An unsaved role group aggregate.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="request"/> is null.</exception>
    // MIGRATION: the tenant comes from the route, never from the payload, and the request contract no
    // longer carries one to be ignored.
    public static RoleGroup ToNewGroup(int portalId, CreateRoleGroupRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        return new RoleGroup
        {
            PortalId = portalId,
            RoleGroupName = request.RoleGroupName,
            Description = request.Description,
        };
    }

    /// <summary>Applies a submitted update to a tracked role group aggregate.</summary>
    /// <param name="roleGroup">The tracked group to modify.</param>
    /// <param name="request">The submitted values.</param>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="roleGroup"/> or <paramref name="request"/> is null.
    /// </exception>
    public static void ApplyGroupUpdate(RoleGroup roleGroup, UpdateRoleGroupRequest request)
    {
        ArgumentNullException.ThrowIfNull(roleGroup);
        ArgumentNullException.ThrowIfNull(request);

        roleGroup.RoleGroupName = request.RoleGroupName;
        roleGroup.Description = request.Description;
    }

    /// <summary>
    /// Builds a new membership row from a submitted assignment, the role the route named and the two bounds
    /// the caller has already derived.
    /// </summary>
    /// <param name="roleId">Identifier of the role being granted, taken from the route.</param>
    /// <param name="request">The submitted assignment.</param>
    /// <param name="effectiveDate">
    /// The moment the membership begins, or <see langword="null"/> when it begins immediately.
    /// </param>
    /// <param name="expiryDate">
    /// The moment the membership lapses, or <see langword="null"/> when it never does.
    /// </param>
    /// <returns>An unsaved membership row.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="request"/> is null.</exception>
    // Flattening pattern F2 - the legacy UserRoleInfo reached its identity through VB INHERITANCE rather
    // than composition, and unpicking that is what this method exists to do.
    public static UserRole ToNewAssignment(
        int roleId,
        RoleAssignmentRequest request,
        DateTime? effectiveDate,
        DateTime? expiryDate)
    {
        ArgumentNullException.ThrowIfNull(request);

        return new UserRole
        {
            UserId = request.UserId,
            RoleId = roleId,
            EffectiveDate = effectiveDate,
            ExpiryDate = expiryDate,
            IsTrialUsed = false,
        };
    }

    /// <summary>Rewrites the two bounds of a membership the member already holds.</summary>
    /// <param name="assignment">The tracked membership to revise.</param>
    /// <param name="effectiveDate">The derived start bound, or <see langword="null"/> for none.</param>
    /// <param name="expiryDate">The derived expiry bound, or <see langword="null"/> for none.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="assignment"/> is null.</exception>
    public static void ApplyAssignmentUpdate(
        UserRole assignment,
        DateTime? effectiveDate,
        DateTime? expiryDate)
    {
        ArgumentNullException.ThrowIfNull(assignment);

        assignment.EffectiveDate = effectiveDate;
        assignment.ExpiryDate = expiryDate;
    }

    /// <summary>
    /// Writes the member set that the creation and update requests share, so the two paths cannot drift
    /// apart.
    /// </summary>
    /// <remarks>
    /// The two request contracts declare identical member sets on purpose - the legacy edit screen was one
    /// screen serving both operations - so the assignment order and the flooring rule are stated once here
    /// rather than twice.
    /// </remarks>
    // MIGRATION: Single -> decimal is a widening WITH a precision-semantics change - binary floating point
    // to base-10 fixed point.
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
        role.RoleName = roleName.Trim();
        role.Description = description;
        role.RoleGroupId = roleGroupId;
        role.IsPublic = isPublic;
        role.AutoAssignment = autoAssignment;

        role.ServiceFee = serviceFee is null
            ? null
            : SqlServerRange.ToStoredMoney(PortalMappings.ClampFee(serviceFee.Value));
        role.BillingPeriod = billingPeriod;
        role.BillingFrequency = billingFrequency;
        role.TrialFee = trialFee is null
            ? null
            : SqlServerRange.ToStoredMoney(PortalMappings.ClampFee(trialFee.Value));
        role.TrialPeriod = trialPeriod;
        role.TrialFrequency = trialFrequency;
        role.RsvpCode = rsvpCode;
        role.IconFile = iconFile;
    }
}
