using DnnMigration.Application.Abstractions;
using DnnMigration.Application.Dtos.Common;
using DnnMigration.Application.Dtos.Role;
using DnnMigration.Application.Dtos.User;
using DnnMigration.Application.Mapping;
using DnnMigration.Domain.Abstractions.Repositories;
using DnnMigration.Domain.Abstractions.Services;
using DnnMigration.Domain.Common;
using DnnMigration.Domain.Entities;
using DnnMigration.Domain.Enums;

namespace DnnMigration.Application.Services;

/// <summary>
/// Manages a portal's roles, its role groups and the assignments that place members in roles.
/// </summary>
/// <remarks>
/// <para>
/// MIGRATION: absorbs <c>Library/Components/Security/Roles/RoleController.vb</c> and the business rules
/// of <c>Website/admin/Security/{Roles,EditRoles,SecurityRoles,EditGroups}.ascx.vb</c>. The one
/// reference to the Visual Basic runtime that exists anywhere in the migration's scope
/// (<c>Imports Microsoft.VisualBasic</c>, L25) is removed here: the <c>DateAdd</c> calls it supported
/// become <see cref="DateTime.AddDays(double)"/>, <see cref="DateTime.AddMonths(int)"/> and
/// <see cref="DateTime.AddYears(int)"/>, selected by the billing frequency.
/// </para>
/// <para>
/// MIGRATION: the single-character frequency codes are load-bearing stored data, not an implementation
/// detail - the column is <c>Roles.BillingFrequency char(1)</c> - so the enumeration preserves them
/// verbatim and no code is renamed. Two of the six do not advance a date at all: the never code yields
/// no expiry and the one-off code yields the explicit perpetual date the legacy store used.
/// </para>
/// <para>
/// MIGRATION: sentinel dates are honoured at the boundary rather than in the model. The legacy store
/// expressed "never expires" as the minimum date value, which is absence and is carried here as a null
/// date; it expressed "perpetual" as the literal 9999-12-31, which is a real and externally observable
/// value and is carried through unchanged. Neither is silently converted into the other.
/// </para>
/// <para>
/// MIGRATION: the notification switch the legacy assignment members carried is not reproduced, because
/// no mail subsystem is in scope. It is not merely ignored either - accepting a switch that can never
/// be honoured invites a caller to believe a notification was sent - so <see cref="RoleAssignmentRequest"/>
/// declares no such member and the operation reports only what it actually did.
/// </para>
/// </remarks>
public sealed class RoleService : IRoleService
{
    /// <summary>Reason code reported when no portal carries the supplied identifier.</summary>
    private const string PortalNotFoundCode = "portal.not_found";

    /// <summary>Reason code reported when the portal has no such role.</summary>
    private const string RoleNotFoundCode = "role.not_found";

    /// <summary>Reason code reported when a role name is already used in the portal.</summary>
    private const string RoleNameDuplicateCode = "role.name_duplicate";

    /// <summary>Reason code reported when a role could not be created.</summary>
    private const string RoleCreateFailedCode = "role.create_failed";

    /// <summary>Reason code reported when the portal has no such role group.</summary>
    private const string RoleGroupNotFoundCode = "role_group.not_found";

    /// <summary>Reason code reported when a role group name is already used in the portal.</summary>
    private const string RoleGroupNameDuplicateCode = "role_group.name_duplicate";

    /// <summary>Reason code reported when a role group still classifies at least one role.</summary>
    private const string RoleGroupInUseCode = "role_group.in_use";

    /// <summary>Reason code reported when the portal has no such member.</summary>
    private const string UserNotFoundCode = "user.not_found";

    /// <summary>Reason code reported when the member does not hold the role.</summary>
    private const string AssignmentNotFoundCode = "role_assignment.not_found";

    /// <summary>Reason code reported when the protected-assignment rule refuses a removal.</summary>
    private const string AssignmentProtectedCode = "role_assignment.protected";

    /// <summary>
    /// Informational reason carried by a successful removal that expired an assignment instead of
    /// deleting it, so that a caller which must report the difference can.
    /// </summary>
    private const string AssignmentExpiredNotRemovedCode = "role_assignment.expired_not_removed";

    /// <summary>Maximum stored length of a role name, measured from the legacy screen's validator.</summary>
    private const int RoleNameMaximumLength = 50;

    /// <summary>Maximum stored length of a role description.</summary>
    private const int DescriptionMaximumLength = 1000;

    /// <summary>Maximum stored length of a subscription code.</summary>
    private const int RsvpCodeMaximumLength = 50;

    /// <summary>Maximum stored length of an icon reference.</summary>
    private const int IconFileMaximumLength = 100;

    /// <summary>Maximum stored length of a role group name.</summary>
    private const int RoleGroupNameMaximumLength = 50;

    /// <summary>Number of days in a week, used by the weekly billing offset.</summary>
    private const int DaysPerWeek = 7;

    /// <summary>
    /// The perpetual expiry the legacy store wrote for a one-off subscription, preserved verbatim
    /// because a legacy consumer reading the same row expects to see exactly this value.
    /// </summary>
    private static readonly DateTime PerpetualExpiry = new(9999, 12, 31, 0, 0, 0, DateTimeKind.Utc);

    private readonly IRoleRepository _roles;
    private readonly IPortalRepository _portals;
    private readonly IUserRepository _users;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IClock _clock;
    private readonly ICacheService _cache;

    /// <summary>
    /// Initialises a new instance of the <see cref="RoleService"/> class.
    /// </summary>
    /// <param name="roles">Role, role group and assignment repository.</param>
    /// <param name="portals">Portal repository, consulted for tenancy and for the two protected identifiers.</param>
    /// <param name="users">Account repository, consulted to prove membership before an assignment.</param>
    /// <param name="unitOfWork">Commits each write exactly once.</param>
    /// <param name="clock">Supplies the current instant, so the expiry arithmetic is testable.</param>
    /// <param name="cache">Invalidates the portal and member entries a role change affects.</param>
    public RoleService(
        IRoleRepository roles,
        IPortalRepository portals,
        IUserRepository users,
        IUnitOfWork unitOfWork,
        IClock clock,
        ICacheService cache)
    {
        _roles = roles ?? throw new ArgumentNullException(nameof(roles));
        _portals = portals ?? throw new ArgumentNullException(nameof(portals));
        _users = users ?? throw new ArgumentNullException(nameof(users));
        _unitOfWork = unitOfWork ?? throw new ArgumentNullException(nameof(unitOfWork));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
    }

    /// <inheritdoc />
    public async Task<Result<PagedResult<RoleListItemDto>>> ListRolesAsync(
        int portalId,
        PagedRequest request,
        int? roleGroupId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!await _portals.ExistsAsync(portalId, cancellationToken).ConfigureAwait(false))
        {
            return Result<PagedResult<RoleListItemDto>>.Failure(
                PortalNotFoundCode,
                $"No portal bears identifier {portalId}.");
        }

        if (roleGroupId is int scopedGroupId)
        {
            RoleGroup? group = await _roles.GetGroupAsync(scopedGroupId, cancellationToken).ConfigureAwait(false);
            if (group is null || group.PortalId != portalId)
            {
                return Result<PagedResult<RoleListItemDto>>.Failure(
                    RoleGroupNotFoundCode,
                    $"Portal {portalId} has no role group bearing identifier {scopedGroupId}.");
            }
        }

        PagedResult<Role> page = await _roles.ListAsync(
            portalId,
            roleGroupId,
            request.PageIndex,
            request.PageSize,
            request.Query,
            cancellationToken).ConfigureAwait(false);

        IReadOnlyList<RoleListItemDto> rows = page.Items.Select(RoleMappings.ToListItem).ToList();

        PagedResult<RoleListItemDto> projected = request.PageSize == 0
            ? PagedResult<RoleListItemDto>.Unpaged(rows)
            : PagedResult<RoleListItemDto>.Create(rows, page.TotalCount, page.PageIndex, page.PageSize);

        return Result<PagedResult<RoleListItemDto>>.Success(projected);
    }

    /// <inheritdoc />
    public async Task<Result<RoleDetailDto?>> GetRoleAsync(
        int portalId,
        int roleId,
        CancellationToken cancellationToken = default)
    {
        if (!await _portals.ExistsAsync(portalId, cancellationToken).ConfigureAwait(false))
        {
            return Result<RoleDetailDto?>.Failure(PortalNotFoundCode, $"No portal bears identifier {portalId}.");
        }

        Role? role = await _roles.GetAsync(roleId, cancellationToken).ConfigureAwait(false);
        if (role is null || role.PortalId != portalId)
        {
            // A role that the portal does not own is indistinguishable from one that does not exist, and
            // absence on a read is a success carrying no value rather than a fabricated failure.
            return Result<RoleDetailDto?>.Success(null);
        }

        RoleDetailDto detail = RoleMappings.ToDetail(role);
        return Result<RoleDetailDto?>.Success(detail);
    }

    /// <inheritdoc />
    public async Task<Result<RoleDetailDto>> CreateRoleAsync(
        int portalId,
        CreateRoleRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!await _portals.ExistsAsync(portalId, cancellationToken).ConfigureAwait(false))
        {
            return Result<RoleDetailDto>.Failure(PortalNotFoundCode, $"No portal bears identifier {portalId}.");
        }

        if (request.RoleGroupId is int requestedGroupId)
        {
            RoleGroup? group = await _roles.GetGroupAsync(requestedGroupId, cancellationToken).ConfigureAwait(false);
            if (group is null || group.PortalId != portalId)
            {
                return Result<RoleDetailDto>.Failure(
                    RoleGroupNotFoundCode,
                    $"Portal {portalId} has no role group bearing identifier {requestedGroupId}.");
            }
        }

        bool nameTaken = await _roles
            .RoleNameExistsAsync(portalId, request.RoleName, null, cancellationToken)
            .ConfigureAwait(false);
        if (nameTaken)
        {
            return Result<RoleDetailDto>.Failure(
                RoleNameDuplicateCode,
                $"Portal {portalId} already has a role named '{request.RoleName}'.");
        }

        Role role = RoleMappings.ToNewRole(portalId, request);
        _roles.Add(role);

        // MIGRATION: the legacy creation member enrolled the portal's existing members immediately after
        // a successful insert when the auto-assignment flag was set (RoleController.vb L106 calling the
        // private helper at L68), using an absent effective date and an absent expiry date. The
        // enrolment is built into the same object graph here, so the role and its enrolments commit
        // together: the assignments reach the new role through its navigation, so no identifier the
        // database has yet to assign is needed beforehand.
        if (request.AutoAssignment)
        {
            PagedResult<User> members = await _users.ListAsync(
                portalId,
                pageIndex: 0,
                pageSize: 0,
                query: null,
                userNamePrefix: null,
                emailPrefix: null,
                profilePropertyDefinitionId: null,
                profilePropertyValuePrefix: null,
                isApproved: null,
                includeUnauthorised: true,
                includeSuperUsers: false,
                cancellationToken).ConfigureAwait(false);

            foreach (User member in members.Items)
            {
                _roles.AddAssignment(new UserRole
                {
                    UserId = member.UserId,
                    Role = role,
                    EffectiveDate = null,
                    ExpiryDate = null,
                    IsTrialUsed = false,
                });
            }
        }

        await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        _cache.InvalidatePortal(portalId);

        Role? stored = await _roles.GetAsync(role.RoleId, cancellationToken).ConfigureAwait(false);
        if (stored is null)
        {
            return Result<RoleDetailDto>.Failure(
                RoleCreateFailedCode,
                "The role was created but could not be read back.");
        }

        RoleDetailDto detail = RoleMappings.ToDetail(stored);
        return Result<RoleDetailDto>.Success(detail);
    }

    /// <inheritdoc />
    public async Task<Result<RoleDetailDto>> UpdateRoleAsync(
        int portalId,
        int roleId,
        UpdateRoleRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!await _portals.ExistsAsync(portalId, cancellationToken).ConfigureAwait(false))
        {
            return Result<RoleDetailDto>.Failure(PortalNotFoundCode, $"No portal bears identifier {portalId}.");
        }

        Role? role = await _roles.GetAsync(roleId, cancellationToken).ConfigureAwait(false);
        if (role is null || role.PortalId != portalId)
        {
            return Result<RoleDetailDto>.Failure(
                RoleNotFoundCode,
                $"Portal {portalId} has no role bearing identifier {roleId}.");
        }

        // The update path carries the shape rules itself, because the request contract has no dedicated
        // validator: the legacy edit screen used one set of validator controls for both creating and
        // editing a role, so the rules are identical on both paths and are enforced here for the edit.
        EnsureRoleShapeIsValid(
            request.RoleName,
            request.Description,
            request.RsvpCode,
            request.IconFile,
            request.ServiceFee,
            request.BillingPeriod,
            request.BillingFrequency,
            request.TrialFee,
            request.TrialPeriod,
            request.TrialFrequency);

        if (request.RoleGroupId is int requestedGroupId)
        {
            RoleGroup? group = await _roles.GetGroupAsync(requestedGroupId, cancellationToken).ConfigureAwait(false);
            if (group is null || group.PortalId != portalId)
            {
                return Result<RoleDetailDto>.Failure(
                    RoleGroupNotFoundCode,
                    $"Portal {portalId} has no role group bearing identifier {requestedGroupId}.");
            }
        }

        // The role being edited is excluded from the uniqueness read, so that saving a role without
        // renaming it does not collide with itself.
        bool nameTaken = await _roles
            .RoleNameExistsAsync(portalId, request.RoleName, roleId, cancellationToken)
            .ConfigureAwait(false);
        if (nameTaken)
        {
            return Result<RoleDetailDto>.Failure(
                RoleNameDuplicateCode,
                $"Portal {portalId} already has a different role named '{request.RoleName}'.");
        }

        RoleMappings.ApplyUpdate(role, request);

        // MIGRATION: the legacy update member wrote the role and nothing else. Turning the
        // auto-assignment flag on during an edit therefore did not retrospectively enrol existing
        // members, and changing the billing or trial terms did not re-compute expiries already in
        // force; both are preserved deliberately, and the terms are re-read on the next assignment.
        await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        _cache.InvalidatePortal(portalId);

        RoleDetailDto detail = RoleMappings.ToDetail(role);
        return Result<RoleDetailDto>.Success(detail);
    }

    /// <inheritdoc />
    public async Task<Result> DeleteRoleAsync(
        int portalId,
        int roleId,
        CancellationToken cancellationToken = default)
    {
        if (!await _portals.ExistsAsync(portalId, cancellationToken).ConfigureAwait(false))
        {
            return Result.Failure(PortalNotFoundCode, $"No portal bears identifier {portalId}.");
        }

        Role? role = await _roles.GetAsync(roleId, cancellationToken).ConfigureAwait(false);
        if (role is null || role.PortalId != portalId)
        {
            return Result.Failure(RoleNotFoundCode, $"Portal {portalId} has no role bearing identifier {roleId}.");
        }

        // Assignments are removed explicitly rather than left to a cascade, so that the write is
        // expressed entirely through the repository contract and is identical on every provider.
        PagedResult<UserRole> assignments = await _roles
            .ListAssignmentsAsync(roleId, 0, 0, cancellationToken)
            .ConfigureAwait(false);

        foreach (UserRole assignment in assignments.Items)
        {
            _roles.RemoveAssignment(assignment);
        }

        _roles.Remove(role);

        await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        _cache.InvalidatePortal(portalId);
        _cache.InvalidateTabPermissions(portalId);

        return Result.Success();
    }

    /// <inheritdoc />
    public async Task<Result<PagedResult<UserListItemDto>>> ListRoleUsersAsync(
        int portalId,
        int roleId,
        PagedRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!await _portals.ExistsAsync(portalId, cancellationToken).ConfigureAwait(false))
        {
            return Result<PagedResult<UserListItemDto>>.Failure(
                PortalNotFoundCode,
                $"No portal bears identifier {portalId}.");
        }

        Role? role = await _roles.GetAsync(roleId, cancellationToken).ConfigureAwait(false);
        if (role is null || role.PortalId != portalId)
        {
            return Result<PagedResult<UserListItemDto>>.Failure(
                RoleNotFoundCode,
                $"Portal {portalId} has no role bearing identifier {roleId}.");
        }

        PagedResult<UserRole> page = await _roles
            .ListAssignmentsAsync(roleId, request.PageIndex, request.PageSize, cancellationToken)
            .ConfigureAwait(false);

        var rows = new List<UserListItemDto>(page.Items.Count);
        foreach (UserRole assignment in page.Items)
        {
            User? member = await _users
                .GetAsync(portalId, assignment.UserId, cancellationToken)
                .ConfigureAwait(false);
            if (member is null)
            {
                // An assignment whose account is no longer a member of this portal is skipped rather
                // than surfaced as a hole in the projection.
                continue;
            }

            // The legacy grid on this screen bound the identifier, the display name and the two
            // assignment dates only, so no profile value is read here; the address and telephone
            // members of the projection stay absent rather than costing a read per row.
            rows.Add(UserMappings.ToListItem(member, portalId, address: null, telephone: null));
        }

        PagedResult<UserListItemDto> projected = request.PageSize == 0
            ? PagedResult<UserListItemDto>.Unpaged(rows)
            : PagedResult<UserListItemDto>.Create(rows, page.TotalCount, page.PageIndex, page.PageSize);

        return Result<PagedResult<UserListItemDto>>.Success(projected);
    }

    /// <inheritdoc />
    public async Task<Result<IReadOnlyList<RoleListItemDto>>> ListUserRolesAsync(
        int portalId,
        int userId,
        CancellationToken cancellationToken = default)
    {
        if (!await _portals.ExistsAsync(portalId, cancellationToken).ConfigureAwait(false))
        {
            return Result<IReadOnlyList<RoleListItemDto>>.Failure(
                PortalNotFoundCode,
                $"No portal bears identifier {portalId}.");
        }

        User? member = await _users.GetAsync(portalId, userId, cancellationToken).ConfigureAwait(false);
        if (member is null)
        {
            return Result<IReadOnlyList<RoleListItemDto>>.Failure(
                UserNotFoundCode,
                $"Portal {portalId} has no member bearing identifier {userId}.");
        }

        IReadOnlyList<UserRole> assignments = await _roles
            .ListUserAssignmentsAsync(portalId, userId, cancellationToken)
            .ConfigureAwait(false);

        // The portal's roles are read once, unpaged, and indexed, so the projection costs two round
        // trips rather than one per assignment. The answer is bounded by the number of roles the portal
        // defines, which the legacy screen also rendered whole.
        PagedResult<Role> portalRoles = await _roles
            .ListAsync(portalId, null, 0, 0, null, cancellationToken)
            .ConfigureAwait(false);

        Dictionary<int, Role> rolesById = portalRoles.Items.ToDictionary(entry => entry.RoleId);

        var rows = new List<RoleListItemDto>(assignments.Count);
        foreach (UserRole assignment in assignments)
        {
            if (rolesById.TryGetValue(assignment.RoleId, out Role? held))
            {
                rows.Add(RoleMappings.ToListItem(held));
            }
        }

        return Result<IReadOnlyList<RoleListItemDto>>.Success(rows);
    }

    /// <inheritdoc />
    public async Task<Result> AssignUserToRoleAsync(
        int portalId,
        int roleId,
        RoleAssignmentRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!await _portals.ExistsAsync(portalId, cancellationToken).ConfigureAwait(false))
        {
            return Result.Failure(PortalNotFoundCode, $"No portal bears identifier {portalId}.");
        }

        Role? role = await _roles.GetAsync(roleId, cancellationToken).ConfigureAwait(false);
        if (role is null || role.PortalId != portalId)
        {
            return Result.Failure(RoleNotFoundCode, $"Portal {portalId} has no role bearing identifier {roleId}.");
        }

        User? member = await _users.GetAsync(portalId, request.UserId, cancellationToken).ConfigureAwait(false);
        if (member is null)
        {
            return Result.Failure(
                UserNotFoundCode,
                $"Portal {portalId} has no member bearing identifier {request.UserId}.");
        }

        UserRole? existing = await _roles
            .GetAssignmentAsync(roleId, request.UserId, cancellationToken)
            .ConfigureAwait(false);

        (DateTime? effectiveDate, DateTime? expiryDate) = DeriveAssignmentDates(
            role,
            request.EffectiveDate,
            request.ExpiryDate,
            existing?.IsTrialUsed ?? false);

        if (existing is null)
        {
            _roles.AddAssignment(new UserRole
            {
                UserId = request.UserId,
                RoleId = roleId,
                EffectiveDate = effectiveDate,
                ExpiryDate = expiryDate,
                IsTrialUsed = false,
            });
        }
        else
        {
            // MIGRATION: the legacy member was an upsert (L295-L315) - it inserted when the member did
            // not yet hold the role and otherwise revised the two dates - so this member is idempotent
            // in exactly the same way. The trial-used fact is never reset by a renewal.
            existing.EffectiveDate = effectiveDate;
            existing.ExpiryDate = expiryDate;
        }

        await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        _cache.InvalidateUser(portalId, member.Username);

        return Result.Success();
    }

    /// <inheritdoc />
    public async Task<Result> RemoveUserFromRoleAsync(
        int portalId,
        int roleId,
        int userId,
        CancellationToken cancellationToken = default)
    {
        Portal? portal = await _portals
            .GetByIdAsync(portalId, includeAliases: false, cancellationToken)
            .ConfigureAwait(false);
        if (portal is null)
        {
            return Result.Failure(PortalNotFoundCode, $"No portal bears identifier {portalId}.");
        }

        Role? role = await _roles.GetAsync(roleId, cancellationToken).ConfigureAwait(false);
        if (role is null || role.PortalId != portalId)
        {
            return Result.Failure(RoleNotFoundCode, $"Portal {portalId} has no role bearing identifier {roleId}.");
        }

        User? member = await _users.GetAsync(portalId, userId, cancellationToken).ConfigureAwait(false);
        if (member is null)
        {
            return Result.Failure(UserNotFoundCode, $"Portal {portalId} has no member bearing identifier {userId}.");
        }

        UserRole? assignment = await _roles
            .GetAssignmentAsync(roleId, userId, cancellationToken)
            .ConfigureAwait(false);
        if (assignment is null)
        {
            return Result.Failure(
                AssignmentNotFoundCode,
                $"Member {userId} does not hold role {roleId} in portal {portalId}.");
        }

        // MIGRATION: the protected-assignment rule, measured at RoleController.vb L741 and duplicated
        // at L764, refuses exactly two cases and no others - removing the portal's designated
        // administrator from that portal's administrator role, and removing any member at all from that
        // portal's registered-members role. It is enforced here rather than exposed as a question a
        // caller may ask and then ignore.
        bool removingPortalAdministratorFromAdminRole =
            portal.AdministratorId == userId && portal.AdministratorRoleId == roleId;
        bool removingFromRegisteredRole = portal.RegisteredRoleId == roleId;

        if (removingPortalAdministratorFromAdminRole || removingFromRegisteredRole)
        {
            return Result.Failure(
                AssignmentProtectedCode,
                "This assignment is protected and cannot be removed.");
        }

        // MIGRATION: the expire-rather-than-delete rule, measured at RoleController.vb L495-L496. When
        // the role carries a service fee and the trial has already been consumed, the expiry is
        // back-dated by one day instead of the row being deleted, so the trial-used fact survives and a
        // cancelled subscriber cannot restart a trial.
        bool expireInsteadOfDelete = role.ServiceFee is decimal serviceFee
            && serviceFee > 0m
            && (assignment.IsTrialUsed ?? false);

        if (expireInsteadOfDelete)
        {
            assignment.ExpiryDate = _clock.UtcNow.Date.AddDays(-1);
        }
        else
        {
            _roles.RemoveAssignment(assignment);
        }

        await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        _cache.InvalidateUser(portalId, member.Username);

        return expireInsteadOfDelete
            ? Result.Success(new ResultReason(
                AssignmentExpiredNotRemovedCode,
                "The assignment was expired rather than deleted, because its paid trial had already been used."))
            : Result.Success();
    }

    /// <inheritdoc />
    public async Task<Result<IReadOnlyList<RoleGroupDto>>> ListRoleGroupsAsync(
        int portalId,
        CancellationToken cancellationToken = default)
    {
        if (!await _portals.ExistsAsync(portalId, cancellationToken).ConfigureAwait(false))
        {
            return Result<IReadOnlyList<RoleGroupDto>>.Failure(
                PortalNotFoundCode,
                $"No portal bears identifier {portalId}.");
        }

        IReadOnlyList<RoleGroup> groups = await _roles
            .ListGroupsAsync(portalId, cancellationToken)
            .ConfigureAwait(false);

        IReadOnlyList<RoleGroupDto> rows = groups.Select(RoleMappings.ToDto).ToList();
        return Result<IReadOnlyList<RoleGroupDto>>.Success(rows);
    }

    /// <inheritdoc />
    public async Task<Result<RoleGroupDto?>> GetRoleGroupAsync(
        int portalId,
        int roleGroupId,
        CancellationToken cancellationToken = default)
    {
        if (!await _portals.ExistsAsync(portalId, cancellationToken).ConfigureAwait(false))
        {
            return Result<RoleGroupDto?>.Failure(PortalNotFoundCode, $"No portal bears identifier {portalId}.");
        }

        RoleGroup? group = await _roles.GetGroupAsync(roleGroupId, cancellationToken).ConfigureAwait(false);

        return group is null || group.PortalId != portalId
            ? Result<RoleGroupDto?>.Success(null)
            : Result<RoleGroupDto?>.Success(RoleMappings.ToDto(group));
    }

    /// <inheritdoc />
    public async Task<Result<RoleGroupDto>> CreateRoleGroupAsync(
        int portalId,
        RoleGroupDto request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        EnsureRoleGroupShapeIsValid(request);

        if (!await _portals.ExistsAsync(portalId, cancellationToken).ConfigureAwait(false))
        {
            return Result<RoleGroupDto>.Failure(PortalNotFoundCode, $"No portal bears identifier {portalId}.");
        }

        bool nameTaken = await _roles
            .GroupNameExistsAsync(portalId, request.RoleGroupName, null, cancellationToken)
            .ConfigureAwait(false);
        if (nameTaken)
        {
            return Result<RoleGroupDto>.Failure(
                RoleGroupNameDuplicateCode,
                $"Portal {portalId} already has a role group named '{request.RoleGroupName}'.");
        }

        RoleGroup group = RoleMappings.ToNewGroup(portalId, request);
        _roles.AddGroup(group);

        await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        _cache.InvalidatePortal(portalId);

        return Result<RoleGroupDto>.Success(RoleMappings.ToDto(group));
    }

    /// <inheritdoc />
    public async Task<Result<RoleGroupDto>> UpdateRoleGroupAsync(
        int portalId,
        int roleGroupId,
        RoleGroupDto request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        EnsureRoleGroupShapeIsValid(request);

        if (!await _portals.ExistsAsync(portalId, cancellationToken).ConfigureAwait(false))
        {
            return Result<RoleGroupDto>.Failure(PortalNotFoundCode, $"No portal bears identifier {portalId}.");
        }

        RoleGroup? group = await _roles.GetGroupAsync(roleGroupId, cancellationToken).ConfigureAwait(false);
        if (group is null || group.PortalId != portalId)
        {
            return Result<RoleGroupDto>.Failure(
                RoleGroupNotFoundCode,
                $"Portal {portalId} has no role group bearing identifier {roleGroupId}.");
        }

        bool nameTaken = await _roles
            .GroupNameExistsAsync(portalId, request.RoleGroupName, roleGroupId, cancellationToken)
            .ConfigureAwait(false);
        if (nameTaken)
        {
            return Result<RoleGroupDto>.Failure(
                RoleGroupNameDuplicateCode,
                $"Portal {portalId} already has a different role group named '{request.RoleGroupName}'.");
        }

        RoleMappings.ApplyGroupUpdate(group, request);

        await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        _cache.InvalidatePortal(portalId);

        return Result<RoleGroupDto>.Success(RoleMappings.ToDto(group));
    }

    /// <inheritdoc />
    public async Task<Result> DeleteRoleGroupAsync(
        int portalId,
        int roleGroupId,
        CancellationToken cancellationToken = default)
    {
        if (!await _portals.ExistsAsync(portalId, cancellationToken).ConfigureAwait(false))
        {
            return Result.Failure(PortalNotFoundCode, $"No portal bears identifier {portalId}.");
        }

        RoleGroup? group = await _roles.GetGroupAsync(roleGroupId, cancellationToken).ConfigureAwait(false);
        if (group is null || group.PortalId != portalId)
        {
            return Result.Failure(
                RoleGroupNotFoundCode,
                $"Portal {portalId} has no role group bearing identifier {roleGroupId}.");
        }

        // One page of size one is read purely for its total, because a group that still classifies a
        // role may not be removed and the repository exposes no bare count.
        PagedResult<Role> classified = await _roles
            .ListAsync(portalId, roleGroupId, 0, 1, null, cancellationToken)
            .ConfigureAwait(false);

        if (classified.TotalCount > 0)
        {
            return Result.Failure(
                RoleGroupInUseCode,
                $"Role group {roleGroupId} still classifies {classified.TotalCount} role(s) and cannot be removed.");
        }

        _roles.RemoveGroup(group);

        await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        _cache.InvalidatePortal(portalId);

        return Result.Success();
    }

    /// <summary>
    /// Derives the effective and expiry dates of an assignment from the role's trial and billing terms.
    /// </summary>
    /// <param name="role">The role being assigned.</param>
    /// <param name="requestedEffectiveDate">The submitted effective date, or <see langword="null"/>.</param>
    /// <param name="requestedExpiryDate">The submitted expiry date, or <see langword="null"/>.</param>
    /// <param name="trialUsed">Whether the member has already consumed this role's trial.</param>
    /// <returns>The dates to store.</returns>
    /// <remarks>
    /// MIGRATION: reproduces RoleController.vb L503-L558 in the same order. The trial terms govern only
    /// when the trial has not already been consumed and the trial frequency is not the never code;
    /// otherwise the billing terms govern. An effective date already in the past is cleared, so the
    /// assignment carries no start gate, and an expiry date already in the past is advanced to the
    /// current instant so that the offset runs forward from now - which is also how an absent expiry
    /// behaved, because the legacy absent-date sentinel was the minimum date value and therefore always
    /// in the past. An absent period yields no expiry at all. The current instant comes from the
    /// injected clock, never from an ambient reading.
    /// </remarks>
    private (DateTime? EffectiveDate, DateTime? ExpiryDate) DeriveAssignmentDates(
        Role role,
        DateTime? requestedEffectiveDate,
        DateTime? requestedExpiryDate,
        bool trialUsed)
    {
        DateTime now = _clock.UtcNow;

        bool trialGoverns = !trialUsed
            && role.TrialFrequency is BillingFrequency trialFrequency
            && trialFrequency != BillingFrequency.None;

        int? period = trialGoverns ? role.TrialPeriod : role.BillingPeriod;
        BillingFrequency? frequency = trialGoverns ? role.TrialFrequency : role.BillingFrequency;

        DateTime? effectiveDate = requestedEffectiveDate;
        if (effectiveDate is DateTime submittedEffective && submittedEffective < now)
        {
            effectiveDate = null;
        }

        DateTime? expiryDate = requestedExpiryDate;
        if (expiryDate is DateTime submittedExpiry && submittedExpiry < now)
        {
            expiryDate = now;
        }

        if (period is not int units)
        {
            return (effectiveDate, null);
        }

        DateTime offsetBase = expiryDate ?? now;

        expiryDate = frequency switch
        {
            BillingFrequency.None => null,
            BillingFrequency.OneTime => PerpetualExpiry,
            BillingFrequency.Day => offsetBase.AddDays(units),
            BillingFrequency.Week => offsetBase.AddDays(units * DaysPerWeek),
            BillingFrequency.Month => offsetBase.AddMonths(units),
            BillingFrequency.Year => offsetBase.AddYears(units),

            // The legacy selection had no default branch, so an unrecognised or absent frequency left
            // the date exactly as the clamping above had set it.
            _ => expiryDate,
        };

        return (effectiveDate, expiryDate);
    }

    /// <summary>
    /// Refuses a role whose submitted shape breaks a rule the legacy edit screen enforced.
    /// </summary>
    /// <param name="roleName">Submitted role name.</param>
    /// <param name="description">Submitted description.</param>
    /// <param name="rsvpCode">Submitted subscription code.</param>
    /// <param name="iconFile">Submitted icon reference.</param>
    /// <param name="serviceFee">Submitted service fee.</param>
    /// <param name="billingPeriod">Submitted billing period.</param>
    /// <param name="billingFrequency">Submitted billing frequency.</param>
    /// <param name="trialFee">Submitted trial fee.</param>
    /// <param name="trialPeriod">Submitted trial period.</param>
    /// <param name="trialFrequency">Submitted trial frequency.</param>
    /// <exception cref="DomainException">Thrown when any rule is broken.</exception>
    /// <remarks>
    /// MIGRATION: the nine validator controls on <c>Website/admin/Security/editroles.ascx</c> guarded
    /// both the create and the edit posts of one screen, so the same rules apply to both requests. The
    /// fee-versus-period asymmetry is genuine and is preserved: a fee may be zero, because a free role
    /// is legitimate, while a period may not, because a cycle of zero units cannot advance an expiry.
    /// A shape violation is reported by exception because this operation's contract names no reason code
    /// for one; the API edge renders it as a bad request alongside the validator's own problems.
    /// </remarks>
    private static void EnsureRoleShapeIsValid(
        string roleName,
        string? description,
        string? rsvpCode,
        string? iconFile,
        decimal? serviceFee,
        int? billingPeriod,
        BillingFrequency? billingFrequency,
        decimal? trialFee,
        int? trialPeriod,
        BillingFrequency? trialFrequency)
    {
        if (string.IsNullOrWhiteSpace(roleName))
        {
            throw new DomainException("Role Name Is Required.");
        }

        if (roleName.Length > RoleNameMaximumLength)
        {
            throw new DomainException($"A role name may not exceed {RoleNameMaximumLength} characters.");
        }

        if (description is not null && description.Length > DescriptionMaximumLength)
        {
            throw new DomainException($"A role description may not exceed {DescriptionMaximumLength} characters.");
        }

        if (rsvpCode is not null && rsvpCode.Length > RsvpCodeMaximumLength)
        {
            throw new DomainException($"A subscription code may not exceed {RsvpCodeMaximumLength} characters.");
        }

        if (iconFile is not null && iconFile.Length > IconFileMaximumLength)
        {
            throw new DomainException($"An icon reference may not exceed {IconFileMaximumLength} characters.");
        }

        if (serviceFee is decimal fee && fee < 0m)
        {
            throw new DomainException("Service Fee Must Be Greater Than or Equal to Zero");
        }

        if (trialFee is decimal trial && trial < 0m)
        {
            throw new DomainException("Trial Fee Must Be Greater Than or Equal to Zero");
        }

        if (billingPeriod is int period && period <= 0)
        {
            throw new DomainException("Billing Period Must Be Greater Than Zero");
        }

        if (trialPeriod is int trialUnits && trialUnits <= 0)
        {
            throw new DomainException("Trial Period Must Be Greater Than Zero");
        }

        if (billingFrequency is BillingFrequency billing && !Enum.IsDefined(typeof(BillingFrequency), billing))
        {
            throw new DomainException("The billing frequency is not one of the recognised codes.");
        }

        if (trialFrequency is BillingFrequency trialCode && !Enum.IsDefined(typeof(BillingFrequency), trialCode))
        {
            throw new DomainException("The trial frequency is not one of the recognised codes.");
        }
    }

    /// <summary>
    /// Refuses a role group whose submitted shape breaks a stored-length or presence rule.
    /// </summary>
    /// <param name="request">The submitted role group.</param>
    /// <exception cref="DomainException">Thrown when the name is absent or too long.</exception>
    /// <remarks>
    /// The role group contract has no dedicated validator, because it doubles as both the request and
    /// the response shape, so its two rules are enforced here.
    /// </remarks>
    private static void EnsureRoleGroupShapeIsValid(RoleGroupDto request)
    {
        if (string.IsNullOrWhiteSpace(request.RoleGroupName))
        {
            throw new DomainException("A role group name is required.");
        }

        if (request.RoleGroupName.Length > RoleGroupNameMaximumLength)
        {
            throw new DomainException($"A role group name may not exceed {RoleGroupNameMaximumLength} characters.");
        }
    }
}
