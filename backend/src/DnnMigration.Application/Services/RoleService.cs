using System.Globalization;
using DnnMigration.Application.Abstractions;
using DnnMigration.Application.Common;
using DnnMigration.Application.Dtos.Common;
using DnnMigration.Application.Dtos.Role;
using DnnMigration.Application.Mapping;
using DnnMigration.Application.Validation;
using DnnMigration.Domain.Abstractions.Repositories;
using DnnMigration.Domain.Abstractions.Services;
using DnnMigration.Domain.Common;
using DnnMigration.Domain.Entities;
using DnnMigration.Domain.Enums;

namespace DnnMigration.Application.Services;

/// <summary>Manages a portal's roles, its role groups and the assignments that place members in roles.</summary>
/// <remarks>
/// <para>
/// MIGRATION: absorbs <c>Library/Components/Security/Roles/RoleController.vb</c> and the business rules of
/// <c>Website/admin/Security/{Roles,EditRoles,SecurityRoles,EditGroups}.ascx.vb</c>.
/// </para>
/// <para>
/// The single-character frequency codes are load-bearing stored data, not an implementation detail - the
/// column is <c>Roles.BillingFrequency char(1)</c> - so the enumeration preserves them verbatim and no code
/// is renamed. Two of the six do not advance a date at all: the never code yields no expiry and the one-off
/// code yields the explicit perpetual date the legacy store used.
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

    /// <summary>
    /// Reason code reported when a caller's optimistic-concurrency token no longer matches the record, so
    /// the write would have replaced values committed by someone else.
    /// </summary>
    /// <remarks>
    /// The reason token <c>concurrency_conflict</c> is what the API surface maps to <c>409 Conflict</c>,
    /// and 409 rather than 412 is deliberate: the request carried no HTTP precondition header, so there is
    /// no precondition for the framework to have failed - the conflict is with the resource's current
    /// state, which is exactly what 409 states.
    /// </remarks>
    private const string RoleConcurrencyConflictCode = "role.concurrency_conflict";

    /// <summary>Reason code reported when a role could not be created.</summary>
    private const string RoleCreateFailedCode = "role.create_failed";

    /// <summary>Reason code reported when the portal has no such role group.</summary>
    private const string RoleGroupNotFoundCode = "role_group.not_found";

    /// <summary>
    /// Reported when the page coordinates are well formed yet still cannot be honoured, which on this
    /// service means only one thing: a caller named an ordering this listing does not apply.
    /// </summary>
    private const string PagingInvalidCode = "role.paging_invalid";

    /// <summary>Reason code reported when a role group name is already used in the portal.</summary>
    private const string RoleGroupNameDuplicateCode = "role_group.name_duplicate";

    /// <summary>Reason code reported when a role group still classifies at least one role.</summary>
    private const string RoleGroupInUseCode = "role_group.in_use";

    /// <summary>Reason code reported when the two role-listing narrowing arguments contradict each other.</summary>
    /// <remarks>
    /// Raised only for the genuinely contradictory pair - one named group together with a request for the
    /// roles in no group. The contradiction is refused rather than resolved by preferring one argument,
    /// because a precedence rule would answer with a page the caller never asked for.
    /// </remarks>
    private const string RoleGroupScopeInvalidCode = "role_group.scope_invalid";

    /// <summary>Reason code reported when the portal has no such member.</summary>
    private const string UserNotFoundCode = "user.not_found";

    /// <summary>Reason code reported when the member does not hold the role.</summary>
    private const string AssignmentNotFoundCode = "role_assignment.not_found";

    /// <summary>Reason code reported when the protected-assignment rule refuses a removal.</summary>
    private const string AssignmentProtectedCode = "role_assignment.protected";

    /// <summary>
    /// Reason code reported when a role the tenant designates for a system purpose is amended or removed.
    /// </summary>
    /// <remarks>
    /// The <c>protected</c> token is load-bearing rather than descriptive.
    /// </remarks>
    private const string RoleProtectedCode = "role.protected";

    /// <summary>
    /// Reported when an update tries to REPLACE an invitation code with one too easily guessed.
    /// </summary>
    /// <remarks>
    /// SEC: the strength rule is a rule about authoring, so it is applied to the value being written and not
    /// to the value already stored. An update that submits the stored code back unchanged authors nothing and
    /// is admitted, which is what keeps a role whose code predates the rule editable; an update that supplies
    /// a DIFFERENT weak code is authoring one and is refused here exactly as creation refuses it.
    /// </remarks>
    private const string RsvpCodeTooWeakCode = "role.rsvp_code_too_weak";

    /// <summary>
    /// Informational reason carried by a successful removal that expired an assignment instead of deleting
    /// it, so that a caller which must report the difference can.
    /// </summary>
    private const string AssignmentExpiredNotRemovedCode = "role_assignment.expired_not_removed";

    /// <summary>Resource kind published on an audit record describing a role.</summary>
    private const string RoleResourceType = "Role";

    /// <summary>Resource kind published on an audit record describing a membership.</summary>
    /// <remarks>
    /// The identifier carried alongside it is the ROLE's, not the assignment row's: the legacy audit
    /// entries for this pair were keyed by the role and the account, the assignment row's own surrogate key
    /// is meaningless to an operator, and on the removal path the row may no longer exist at all.
    /// </remarks>
    private const string UserRoleResourceType = "UserRole";

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

    /// <summary>Number of months in a year, used to express the yearly billing offset as a month offset.</summary>
    /// <remarks>
    /// The yearly offset is applied as a month count rather than through the framework's year addition so
    /// that both calendar frequencies share one range check. Adding twelve months is equivalent to adding a
    /// year for every date the column can hold, including the twenty-ninth of February, where both
    /// operations truncate to the twenty-eighth in a common year.
    /// </remarks>
    private const int MonthsPerYear = 12;

    /// <summary>
    /// The perpetual expiry the legacy store wrote for a one-off subscription, preserved verbatim because a
    /// legacy consumer reading the same row expects to see exactly this value.
    /// </summary>
    private static readonly DateTime PerpetualExpiry = new(9999, 12, 31, 0, 0, 0, DateTimeKind.Utc);

    private readonly IRoleRepository _roles;
    private readonly IPortalRepository _portals;
    private readonly IUserRepository _users;
    private readonly IPermissionService _permissions;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IClock _clock;
    private readonly ICacheService _cache;
    private readonly ICurrentUser _currentUser;
    private readonly IAuditSink _audit;

    /// <summary>Initialises a new instance of the <see cref="RoleService"/> class.</summary>
    /// <param name="roles">Role, role group and assignment repository.</param>
    /// <param name="portals">Portal repository, consulted for tenancy and for the two protected identifiers.</param>
    /// <param name="users">Account repository, consulted to prove membership before an assignment.</param>
    /// <param name="permissions">
    /// Permission contract, which owns the removal of a role's grants and the eviction of the cached grant
    /// entries that removal stales.
    /// </param>
    /// <param name="unitOfWork">Commits each write exactly once.</param>
    /// <param name="clock">Supplies the current instant, so the expiry arithmetic is testable.</param>
    /// <param name="cache">Invalidates the portal and member entries a role change affects.</param>
    /// <param name="currentUser">
    /// Identifies the operator performing the change, so an audit record can attribute it.
    /// </param>
    /// <param name="audit">Receives the business audit record for every committed role change.</param>
    /// <remarks>
    /// The permission contract is a collaborator because a role's grants have to go with the role, and the
    /// rule bounding that removal is permission knowledge rather than role knowledge.
    /// </remarks>
    public RoleService(
        IRoleRepository roles,
        IPortalRepository portals,
        IUserRepository users,
        IPermissionService permissions,
        IUnitOfWork unitOfWork,
        IClock clock,
        ICacheService cache,
        ICurrentUser currentUser,
        IAuditSink audit)
    {
        _roles = roles ?? throw new ArgumentNullException(nameof(roles));
        _portals = portals ?? throw new ArgumentNullException(nameof(portals));
        _users = users ?? throw new ArgumentNullException(nameof(users));
        _permissions = permissions ?? throw new ArgumentNullException(nameof(permissions));
        _unitOfWork = unitOfWork ?? throw new ArgumentNullException(nameof(unitOfWork));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
        _currentUser = currentUser ?? throw new ArgumentNullException(nameof(currentUser));
        _audit = audit ?? throw new ArgumentNullException(nameof(audit));
    }

    /// <summary>Records one committed role change on the audit trail.</summary>
    /// <param name="eventName">The stable event name, from <see cref="AuditEventNames"/>.</param>
    /// <param name="portalId">The tenant the change was made within.</param>
    /// <param name="resourceType">
    /// The kind of record changed - <c>Role</c>, <c>RoleGroup</c> or <c>UserRole</c>.
    /// </param>
    /// <param name="resourceId">The identifier of the record changed.</param>
    /// <param name="subjectUserId">
    /// The account a membership change was made against, or <see langword="null"/> for a change that names
    /// no account.
    /// </param>
    /// <param name="properties">
    /// Short, non-sensitive machine-readable facts, or <see langword="null"/> for none.
    /// </param>
    /// <remarks>
    /// Called only after the change has been committed, so no record can describe a write that was later
    /// abandoned. The acting account is read from the credential, never from a request.
    /// </remarks>
    private void RecordAudit(
        string eventName,
        int portalId,
        string resourceType,
        int resourceId,
        int? subjectUserId = null,
        IReadOnlyDictionary<string, string?>? properties = null)
    {
        AuditEvent record = new(eventName)
        {
            PortalId = portalId,
            ActorUserId = _currentUser.UserId,
            SubjectUserId = subjectUserId,
            ResourceType = resourceType,
            ResourceId = resourceId.ToString(CultureInfo.InvariantCulture),
        };

        if (properties is not null)
        {
            record = record with { Properties = properties };
        }

        _audit.Record(record);
    }

    /// <inheritdoc />
    public async Task<Result<PagedResult<RoleListItemDto>>> ListRolesAsync(
        int portalId,
        PagedRequest request,
        int? roleGroupId,
        RoleGroupScope scope = RoleGroupScope.All,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        // An UNDEFINED scope is refused before anything else, because this member's own contract cannot
        // honour one.
        if (!Enum.IsDefined(scope))
        {
            return Result<PagedResult<RoleListItemDto>>.Failure(
                RoleGroupScopeInvalidCode,
                FormattableString.Invariant(
                    $"Role group scope {(int)scope} is not defined; omit the scope to list every role."));
        }

        // The contradictory pair is refused before any store is touched: asking for one named group and for
        // the roles belonging to no group at all cannot both be satisfied, and no ordering of the two
        // arguments is more correct than the other.
        if (roleGroupId is not null && scope == RoleGroupScope.Ungrouped)
        {
            return Result<PagedResult<RoleListItemDto>>.Failure(
                RoleGroupScopeInvalidCode,
                "A role group identifier cannot be combined with a request for the ungrouped roles: "
                    + "supply the identifier to read that one group, or the ungrouped scope on its own.");
        }

        if (!await _portals.ExistsAsync(portalId, cancellationToken).ConfigureAwait(false))
        {
            return Result<PagedResult<RoleListItemDto>>.Failure(
                PortalNotFoundCode,
                $"No portal bears identifier {portalId}.");
        }

        // The PER-COLLECTION ordering set is enforced HERE, before anything is read.
        if (!SortableFields.IsPermittedFor(request.SortBy, SortableFields.Roles))
        {
            return Result<PagedResult<RoleListItemDto>>.Failure(
                PagingInvalidCode,
                $"Roles cannot be ordered by '{request.SortBy}'.");
        }

        if (roleGroupId is int scopedGroupId)
        {
            RoleGroup? group = await _roles.GetRoleGroupAsync(portalId, scopedGroupId, cancellationToken).ConfigureAwait(false);
            if (group is null)
            {
                return Result<PagedResult<RoleListItemDto>>.Failure(
                    RoleGroupNotFoundCode,
                    $"Portal {portalId} has no role group bearing identifier {scopedGroupId}.");
            }
        }

        // EVERY NARROWING NOW TRAVELS TO THE STORE, and the reason it did not before is worth recording
        // because the reasoning was plausible.
        PagedResult<Role> window = await _roles.ListAsync(
            portalId,
            roleGroupId,
            scope == RoleGroupScope.Ungrouped,
            request.HasQuery ? request.Query : null,
            request.HasSort ? request.SortBy : null,
            request.SortDir == SortDirection.Descending,
            request.PageIndex,
            request.PageSize,
            cancellationToken).ConfigureAwait(false);

        IReadOnlyList<RoleListItemDto> rows = window.Items
            .Select(RoleMappings.ToListItem)
            .ToList();

        PagedResult<RoleListItemDto> projected = request.PageSize == 0
            ? PagedResult<RoleListItemDto>.Unpaged(rows)
            : PagedResult<RoleListItemDto>.Create(rows, window.TotalCount, request.PageIndex, request.PageSize);

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

        Role? role = await _roles.GetByIdAsync(roleId, portalId, cancellationToken).ConfigureAwait(false);
        if (role is null)
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
            RoleGroup? group = await _roles.GetRoleGroupAsync(portalId, requestedGroupId, cancellationToken).ConfigureAwait(false);
            if (group is null)
            {
                return Result<RoleDetailDto>.Failure(
                    RoleGroupNotFoundCode,
                    $"Portal {portalId} has no role group bearing identifier {requestedGroupId}.");
            }
        }

        // Uniqueness is settled by the legacy name lookup itself. IX_RoleName is unique over (PortalID,
        // RoleName), so GetRoleByName can match at most one row and a non-null answer IS the duplicate
        // report - no separate existence member is needed on the repository contract.
        string requestedName = request.RoleName.Trim();

        Role? clashing = await _roles
            .GetByNameAsync(portalId, requestedName, cancellationToken)
            .ConfigureAwait(false);
        if (clashing is not null)
        {
            return Result<RoleDetailDto>.Failure(
                RoleNameDuplicateCode,
                DescribeNameClash(requestedName, clashing));
        }

        Role role = RoleMappings.ToNewRole(portalId, request);
        await _roles.AddAsync(role, cancellationToken).ConfigureAwait(false);

        // The legacy creation member enrolled the portal's existing members immediately after a successful
        // insert when the auto-assignment flag was set, using an absent effective date and an absent expiry
        // date.
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
                // No sort field: this is an unpaged enrolment sweep, not a listing, so every matching
                // member is enrolled and the order in which they are enrolled is not observable.
                sortBy: null,
                descending: false,
                cancellationToken: cancellationToken).ConfigureAwait(false);

            foreach (User member in members.Items)
            {
                await _roles.AddUserRoleAsync(
                    new UserRole
                    {
                        UserId = member.UserId,
                        Role = role,
                        EffectiveDate = null,
                        ExpiryDate = null,
                        IsTrialUsed = false,
                    },
                    cancellationToken).ConfigureAwait(false);
            }
        }

        // THE PRE-CHECK ABOVE CANNOT CLOSE THE RACE, SO THE FLUSH ANSWERS FOR IT. IX_RoleName is unique
        // over (PortalID, RoleName), and two requests carrying the same name arriving together both read
        // "not taken" before either inserts - so the loser's insert is refused by the index rather than by
        // the check.
        try
        {
            await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DuplicateKeyException)
        {
            // The SAME wording the pre-check reports for the same outcome, which is the point made above
            // about publishing one code rather than two: the loser of the race and the caller who arrived
            // second are told the same thing because they can act on it the same way.
            return Result<RoleDetailDto>.Failure(
                RoleNameDuplicateCode,
                RoleTermsRules.DuplicateRoleMessage);
        }

        // MIGRATION: RECORDED IMMEDIATELY AFTER THE FLUSH, AND NO LONGER AFTER THE READ-BACK. The record
        // used to sit behind a cache eviction and a re-read that returns early when it yields nothing - so
        // a role that WAS inserted and committed, but that the read-back could not find, was created with
        // no trace of its creation.
        RecordAudit(
            AuditEventNames.RoleCreated,
            portalId,
            RoleResourceType,
            role.RoleId,
            properties: new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["AutoAssignment"] = request.AutoAssignment.ToString(CultureInfo.InvariantCulture),
            });

        _cache.InvalidatePortal(portalId);

        Role? stored = await _roles.GetByIdAsync(role.RoleId, portalId, cancellationToken).ConfigureAwait(false);
        if (stored is null)
        {
            // The role exists and the record above already says so. This refusal is about the RESPONSE
            // rather than about the work, which is why the record no longer waits behind it.
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

        // THE WHOLE READ-JUDGE-WRITE SEQUENCE IS ONE SERIALISABLE TRANSACTION, and it had none at all.
        await using ITransactionScope transaction = await _unitOfWork
            .BeginTransactionAsync(TransactionIsolation.Serializable, cancellationToken)
            .ConfigureAwait(false);

        // THE TENANT ROW IS READ RATHER THAN PROBED, because this member now needs two facts from it - that
        // the tenant exists, and which two roles it has designated - and one read answers both.
        Portal? portal = await _portals
            .GetByIdAsync(portalId, includeAliases: false, cancellationToken)
            .ConfigureAwait(false);
        if (portal is null)
        {
            return Result<RoleDetailDto>.Failure(PortalNotFoundCode, $"No portal bears identifier {portalId}.");
        }

        Role? role = await _roles.GetByIdAsync(roleId, portalId, cancellationToken).ConfigureAwait(false);
        if (role is null)
        {
            return Result<RoleDetailDto>.Failure(
                RoleNotFoundCode,
                $"Portal {portalId} has no role bearing identifier {roleId}.");
        }

        if (IsPortalDesignatedRole(portal, roleId))
        {
            return Result<RoleDetailDto>.Failure(
                RoleProtectedCode,
                DescribeProtectedRole(portal, roleId, "amended"));
        }

        // OPTIMISTIC CONCURRENCY, CHECKED BEFORE ANY FIELD RULE RUNS. The order is deliberate: a caller
        // holding a stale snapshot must be told that the record moved under it, not that some field of the
        // snapshot it is trying to restore is invalid - and it is checked before the duplicate-name probe
        // too, because a rename that now clashes is a symptom of the staleness rather than a separate
        // fault.
        if (!ConcurrencyToken.Matches(request.ConcurrencyToken, RoleMappings.ConcurrencyTokenFor(role)))
        {
            return Result<RoleDetailDto>.Failure(
                RoleConcurrencyConflictCode,
                FormattableString.Invariant(
                    $"Role {roleId} was changed by someone else after you read it, so nothing was written. Reload the role to see the current values, then apply your change again."));
        }

        // The shape rules are re-asserted here as well as at the boundary. UpdateRoleRequestValidator is
        // registered and runs first for an HTTP caller, but this member is also reachable from a background
        // job, a console tool or a test, and a rule that only an HTTP caller meets is not a rule.
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

        // ⚠ THE INVITATION-CODE STRENGTH RULE LIVES HERE RATHER THAN IN THE VALIDATOR, because deciding it
        // needs the stored value and a validator cannot see one. Held against every update it made a role
        // carrying a code from before the rule permanently un-editable, while leaving that code in place - so
        // it is held against the value being WRITTEN instead. Submitting the stored code back unchanged
        // authors nothing and passes; supplying a different weak one is authoring and is refused, on the same
        // terms creation refuses it.
        //
        // It is judged AFTER the shape guard above deliberately. Strength and well-formedness are different
        // questions, and the more basic one must answer first: an over-long or multi-line code carries only
        // one character class, so judging strength first would report every malformed value as "too weak"
        // and hide the defect the caller actually has to fix.
        if (!RsvpCodeIsUnchanged(role.RsvpCode, request.RsvpCode)
            && !RoleTermsRules.IsStrongAuthoredRsvpCode(request.RsvpCode))
        {
            return Result<RoleDetailDto>.Failure(
                RsvpCodeTooWeakCode,
                RoleTermsRules.RsvpCodeTooWeakMessage);
        }

        if (request.RoleGroupId is int requestedGroupId)
        {
            RoleGroup? group = await _roles.GetRoleGroupAsync(portalId, requestedGroupId, cancellationToken).ConfigureAwait(false);
            if (group is null)
            {
                return Result<RoleDetailDto>.Failure(
                    RoleGroupNotFoundCode,
                    $"Portal {portalId} has no role group bearing identifier {requestedGroupId}.");
            }
        }

        // The single read answers the question outright: IX_RoleName is unique over (PortalID, RoleName)
        // (03.00.09.SqlDataProvider L304), so GetByNameAsync - the legacy GetRoleByName at membership
        // DataProvider.vb L94 - can match at most one row, and a match whose identifier differs from the
        // edited role IS the duplicate report.
        string requestedName = request.RoleName.Trim();

        Role? clashing = await _roles
            .GetByNameAsync(portalId, requestedName, cancellationToken)
            .ConfigureAwait(false);
        if (clashing is not null && clashing.RoleId != roleId)
        {
            return Result<RoleDetailDto>.Failure(
                RoleNameDuplicateCode,
                DescribeNameClash(requestedName, clashing));
        }

        RoleMappings.ApplyUpdate(role, request);

        try
        {
            await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (ConcurrencyConflictException)
        {
            // The token comparison closes the window a caller can OBSERVE; it cannot close the window
            // between that comparison and the write. The store can still refuse the update as a lost
            // update, and under serialisable isolation it can abort this participant as a deadlock victim.
            return Result<RoleDetailDto>.Failure(
                RoleConcurrencyConflictCode,
                FormattableString.Invariant(
                    $"Role {roleId} was changed by someone else after you read it, so nothing was written. Reload the role to see the current values, then apply your change again."));
        }

        // Everything below runs only once the write is durable, so no eviction and no audit record can
        // describe an amendment that was rolled back.
        _cache.InvalidatePortal(portalId);

        // Reproduces the legacy ROLE_UPDATED audit entry.
        RecordAudit(
            AuditEventNames.RoleUpdated,
            portalId,
            RoleResourceType,
            role.RoleId);

        RoleDetailDto detail = RoleMappings.ToDetail(role);
        return Result<RoleDetailDto>.Success(detail);
    }

    /// <inheritdoc />
    public async Task<Result> DeleteRoleAsync(
        int portalId,
        int roleId,
        CancellationToken cancellationToken = default)
    {
        // MIGRATION: the tenant row is read rather than probed, for the reason recorded on the
        // update path: the designations live on it, and one read answers both existence and designation.
        Portal? portal = await _portals
            .GetByIdAsync(portalId, includeAliases: false, cancellationToken)
            .ConfigureAwait(false);
        if (portal is null)
        {
            return Result.Failure(PortalNotFoundCode, $"No portal bears identifier {portalId}.");
        }

        Role? role = await _roles.GetByIdAsync(roleId, portalId, cancellationToken).ConfigureAwait(false);
        if (role is null)
        {
            return Result.Failure(RoleNotFoundCode, $"Portal {portalId} has no role bearing identifier {roleId}.");
        }

        // THE DESIGNATED ROLES CANNOT BE REMOVED, and this is a repair of a genuine hole rather than a
        // hardening. The legacy screen withheld the removal outright; this contract accepted it and
        // answered 204.
        if (IsPortalDesignatedRole(portal, roleId))
        {
            return Result.Failure(RoleProtectedCode, DescribeProtectedRole(portal, roleId, "removed"));
        }

        // ONE SCOPE AROUND THE WHOLE REMOVAL. Two writes follow that cannot be expressed as a single flush:
        // the grant sweep reaches the store as set-based statements the moment it is issued, while the role
        // removal is staged and becomes durable at the commit.
        await using (ITransactionScope transaction = await _unitOfWork
            .BeginTransactionAsync(TransactionIsolation.Default, cancellationToken)
            .ConfigureAwait(false))
        {
            Result swept = await _permissions
                .StageRolePermissionRemovalAsync(portalId, roleId, cancellationToken)
                .ConfigureAwait(false);

            if (swept.IsFailure)
            {
                return swept;
            }

            // A role's assignments go with it.
            await _roles.DeleteAsync(roleId, cancellationToken).ConfigureAwait(false);

            await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }

        // Everything below runs only once the batch is durable, so no eviction and no audit record can
        // describe a removal that did not happen.

        // MIGRATION: reproduces the legacy ROLE_DELETED audit entry. The name is captured BEFORE the
        // removal, because after the commit the row it came from no longer exists and the record would be
        // reduced to a bare identifier nothing can resolve.
        RecordAudit(
            AuditEventNames.RoleDeleted,
            portalId,
            RoleResourceType,
            roleId);

        _cache.InvalidatePortal(portalId);

        _permissions.InvalidateUserPermissionCaches();

        return Result.Success();
    }

    /// <inheritdoc />
    public async Task<Result<PagedResult<RoleMembershipDto>>> ListRoleUsersAsync(
        int portalId,
        int roleId,
        PagedRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!await _portals.ExistsAsync(portalId, cancellationToken).ConfigureAwait(false))
        {
            return Result<PagedResult<RoleMembershipDto>>.Failure(
                PortalNotFoundCode,
                $"No portal bears identifier {portalId}.");
        }

        // The role-membership set is narrower than the account listing's for the reasons SortableFields
        // records, and it is enforced here for the same reason the role listing enforces its own.
        if (!SortableFields.IsPermittedFor(request.SortBy, SortableFields.RoleUsers))
        {
            return Result<PagedResult<RoleMembershipDto>>.Failure(
                PagingInvalidCode,
                $"Role members cannot be ordered by '{request.SortBy}'.");
        }

        Role? role = await _roles.GetByIdAsync(roleId, portalId, cancellationToken).ConfigureAwait(false);
        if (role is null)
        {
            return Result<PagedResult<RoleMembershipDto>>.Failure(
                RoleNotFoundCode,
                $"Portal {portalId} has no role bearing identifier {roleId}.");
        }

        PagedResult<UserRole> window = await _roles.ListRoleMembershipsAsync(
            portalId,
            role.RoleName,
            request.HasQuery ? request.Query : null,
            request.HasSort ? request.SortBy : null,
            request.SortDir == SortDirection.Descending,
            request.PageIndex,
            request.PageSize,
            cancellationToken).ConfigureAwait(false);

        IReadOnlyList<RoleMembershipDto> rows = window.Items
            .Select(RoleMappings.ToMembership)
            .ToList();

        PagedResult<RoleMembershipDto> projected = request.PageSize == 0
            ? PagedResult<RoleMembershipDto>.Unpaged(rows)
            : PagedResult<RoleMembershipDto>.Create(rows, window.TotalCount, request.PageIndex, request.PageSize);

        return Result<PagedResult<RoleMembershipDto>>.Success(projected);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Three reads, each one required to distinguish an answer from a broken request: the role proves the
    /// tenant owns it, the account proves the tenant has it, and the assignment answers the question.
    /// </remarks>
    public async Task<Result<RoleMembershipDto?>> GetRoleMembershipAsync(
        int portalId,
        int roleId,
        int userId,
        CancellationToken cancellationToken = default)
    {
        if (!await _portals.ExistsAsync(portalId, cancellationToken).ConfigureAwait(false))
        {
            return Result<RoleMembershipDto?>.Failure(
                PortalNotFoundCode,
                $"No portal bears identifier {portalId}.");
        }

        Role? role = await _roles.GetByIdAsync(roleId, portalId, cancellationToken).ConfigureAwait(false);
        if (role is null)
        {
            return Result<RoleMembershipDto?>.Failure(
                RoleNotFoundCode,
                $"Portal {portalId} has no role bearing identifier {roleId}.");
        }

        User? member = await _users.GetAsync(portalId, userId, cancellationToken).ConfigureAwait(false);
        if (member is null)
        {
            return Result<RoleMembershipDto?>.Failure(
                UserNotFoundCode,
                $"Portal {portalId} has no member bearing identifier {userId}.");
        }

        UserRole? assignment = await _roles
            .GetUserRoleAsync(portalId, userId, roleId, cancellationToken)
            .ConfigureAwait(false);

        return assignment is null
            ? Result<RoleMembershipDto?>.Success(null)
            : Result<RoleMembershipDto?>.Success(RoleMappings.ToMembership(assignment, role, member));
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
            .GetUserRolesAsync(portalId, userId, cancellationToken)
            .ConfigureAwait(false);

        IReadOnlyList<Role> portalRoles = await _roles
            .GetByPortalIdAsync(portalId, cancellationToken)
            .ConfigureAwait(false);

        Dictionary<int, Role> rolesById = portalRoles.ToDictionary(entry => entry.RoleId);

        var rows = new List<RoleListItemDto>(assignments.Count);
        foreach (UserRole assignment in assignments)
        {
            // One indexed lookup per assignment, and an assignment naming a role the portal does not own is
            // skipped rather than projected: the tenant scope is the answer's boundary, and a dangling
            // reference is not something a caller can act on.
            Role? held = rolesById.GetValueOrDefault(assignment.RoleId);
            if (held is not null)
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

        // The tenant row is READ rather than probed, because the bound-protection rule below needs its two
        // designations. The same substitution is made on the update and removal paths, for the same reason,
        // so every rule keyed on a designation resolves it identically.
        Portal? portal = await _portals
            .GetByIdAsync(portalId, includeAliases: false, cancellationToken)
            .ConfigureAwait(false);
        if (portal is null)
        {
            return Result.Failure(PortalNotFoundCode, $"No portal bears identifier {portalId}.");
        }

        Role? role = await _roles.GetByIdAsync(roleId, portalId, cancellationToken).ConfigureAwait(false);
        if (role is null)
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
            .GetUserRoleAsync(portalId, request.UserId, roleId, cancellationToken)
            .ConfigureAwait(false);

        bool bearsProtectedBounds =
            portal.AdministratorId == request.UserId && portal.AdministratorRoleId == roleId;

        (DateTime? effectiveDate, DateTime? expiryDate) = bearsProtectedBounds
            ? (null, null)
            : DeriveAssignmentDates(
                role,
                request.EffectiveDate,
                request.ExpiryDate,
                existing?.IsTrialUsed ?? false);

        // The legacy assignment member took the portal identifier as its first argument and the repository
        // member this one calls does NOT, because dbo.UserRoles has no PortalID column at all - the
        // assignment row is scoped only through the role it names, whose own PortalID carries the tenancy.
        if (existing is null)
        {
            await _roles.AddUserRoleAsync(
                RoleMappings.ToNewAssignment(roleId, request, effectiveDate, expiryDate),
                cancellationToken).ConfigureAwait(false);
        }
        else
        {
            RoleMappings.ApplyAssignmentUpdate(existing, effectiveDate, expiryDate);
        }

        await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        _cache.InvalidateUser(portalId, member.Username);

        // USER_ROLE_UPDATED is net-new, and deliberately so.
        RecordAudit(
            existing is null ? AuditEventNames.UserRoleCreated : AuditEventNames.UserRoleUpdated,
            portalId,
            UserRoleResourceType,
            roleId,
            subjectUserId: request.UserId,
            properties: new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["Renewed"] = (existing is not null).ToString(CultureInfo.InvariantCulture),
                ["EffectiveDate"] = effectiveDate?.ToString("O", CultureInfo.InvariantCulture),
                ["ExpiryDate"] = expiryDate?.ToString("O", CultureInfo.InvariantCulture),
            });

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

        Role? role = await _roles.GetByIdAsync(roleId, portalId, cancellationToken).ConfigureAwait(false);
        if (role is null)
        {
            return Result.Failure(RoleNotFoundCode, $"Portal {portalId} has no role bearing identifier {roleId}.");
        }

        User? member = await _users.GetAsync(portalId, userId, cancellationToken).ConfigureAwait(false);
        if (member is null)
        {
            return Result.Failure(UserNotFoundCode, $"Portal {portalId} has no member bearing identifier {userId}.");
        }

        UserRole? assignment = await _roles
            .GetUserRoleAsync(portalId, userId, roleId, cancellationToken)
            .ConfigureAwait(false);
        if (assignment is null)
        {
            return Result.Failure(
                AssignmentNotFoundCode,
                $"Member {userId} does not hold role {roleId} in portal {portalId}.");
        }

        bool removingPortalAdministratorFromAdminRole =
            portal.AdministratorId == userId && portal.AdministratorRoleId == roleId;
        bool removingFromRegisteredRole = portal.RegisteredRoleId == roleId;

        if (removingPortalAdministratorFromAdminRole || removingFromRegisteredRole)
        {
            return Result.Failure(
                AssignmentProtectedCode,
                "This assignment is protected and cannot be removed.");
        }

        bool expireInsteadOfDelete = role.ServiceFee is decimal serviceFee
            && serviceFee > 0m
            && (assignment.IsTrialUsed ?? false);

        if (expireInsteadOfDelete)
        {
            assignment.ExpiryDate = _clock.UtcNow.Date.AddDays(-1);
        }
        else
        {
            await _roles
                .DeleteUserRoleAsync(userId, roleId, cancellationToken)
                .ConfigureAwait(false);
        }

        await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        _cache.InvalidateUser(portalId, member.Username);

        // MIGRATION: reproduces the legacy USER_ROLE_DELETED audit entry.
        RecordAudit(
            AuditEventNames.UserRoleDeleted,
            portalId,
            UserRoleResourceType,
            roleId,
            subjectUserId: userId,
            properties: new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["Expired"] = expireInsteadOfDelete.ToString(CultureInfo.InvariantCulture),
            });

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
            .GetRoleGroupsAsync(portalId, cancellationToken)
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

        RoleGroup? group = await _roles.GetRoleGroupAsync(portalId, roleGroupId, cancellationToken).ConfigureAwait(false);

        return group is null
            ? Result<RoleGroupDto?>.Success(null)
            : Result<RoleGroupDto?>.Success(RoleMappings.ToDto(group));
    }

    /// <inheritdoc />
    public async Task<Result<RoleGroupDto>> CreateRoleGroupAsync(
        int portalId,
        CreateRoleGroupRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        EnsureRoleGroupShapeIsValid(request.RoleGroupName);

        if (!await _portals.ExistsAsync(portalId, cancellationToken).ConfigureAwait(false))
        {
            return Result<RoleGroupDto>.Failure(PortalNotFoundCode, $"No portal bears identifier {portalId}.");
        }

        bool nameTaken = (await _roles.GetRoleGroupsAsync(portalId, cancellationToken).ConfigureAwait(false))
            .Any(candidate => string.Equals(
                candidate.RoleGroupName.Trim(),
                request.RoleGroupName.Trim(),
                StringComparison.OrdinalIgnoreCase));
        if (nameTaken)
        {
            return Result<RoleGroupDto>.Failure(
                RoleGroupNameDuplicateCode,
                $"Portal {portalId} already has a role group named '{request.RoleGroupName}'.");
        }

        RoleGroup group = RoleMappings.ToNewGroup(portalId, request);
        await _roles.AddRoleGroupAsync(group, cancellationToken).ConfigureAwait(false);

        // The concurrent counterpart of the name check above, for the reason recorded in full on
        // CreateRoleAsync. Same code, same wording, so one outcome has one taxonomy.
        try
        {
            await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DuplicateKeyException)
        {
            return Result<RoleGroupDto>.Failure(
                RoleGroupNameDuplicateCode,
                $"Portal {portalId} already has a role group named '{request.RoleGroupName}'.");
        }

        _cache.InvalidatePortal(portalId);

        return Result<RoleGroupDto>.Success(RoleMappings.ToDto(group));
    }

    /// <inheritdoc />
    public async Task<Result<RoleGroupDto>> UpdateRoleGroupAsync(
        int portalId,
        int roleGroupId,
        UpdateRoleGroupRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        EnsureRoleGroupShapeIsValid(request.RoleGroupName);

        if (!await _portals.ExistsAsync(portalId, cancellationToken).ConfigureAwait(false))
        {
            return Result<RoleGroupDto>.Failure(PortalNotFoundCode, $"No portal bears identifier {portalId}.");
        }

        RoleGroup? group = await _roles.GetRoleGroupAsync(portalId, roleGroupId, cancellationToken).ConfigureAwait(false);
        if (group is null)
        {
            return Result<RoleGroupDto>.Failure(
                RoleGroupNotFoundCode,
                $"Portal {portalId} has no role group bearing identifier {roleGroupId}.");
        }

        bool nameTaken = (await _roles.GetRoleGroupsAsync(portalId, cancellationToken).ConfigureAwait(false))
            .Any(candidate => candidate.RoleGroupId != roleGroupId
                && string.Equals(
                    candidate.RoleGroupName.Trim(),
                    request.RoleGroupName.Trim(),
                    StringComparison.OrdinalIgnoreCase));
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

        RoleGroup? group = await _roles.GetRoleGroupAsync(portalId, roleGroupId, cancellationToken).ConfigureAwait(false);
        if (group is null)
        {
            return Result.Failure(
                RoleGroupNotFoundCode,
                $"Portal {portalId} has no role group bearing identifier {roleGroupId}.");
        }

        IReadOnlyList<Role> classified = await _roles
            .GetRolesByGroupAsync(roleGroupId, portalId, cancellationToken)
            .ConfigureAwait(false);

        if (classified.Count > 0)
        {
            return Result.Failure(
                RoleGroupInUseCode,
                $"Role group {roleGroupId} still classifies {classified.Count} role(s) and cannot be removed.");
        }

        await _roles.DeleteRoleGroupAsync(group.RoleGroupId, cancellationToken).ConfigureAwait(false);

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
    private (DateTime? EffectiveDate, DateTime? ExpiryDate) DeriveAssignmentDates(
        Role role,
        DateTime? requestedEffectiveDate,
        DateTime? requestedExpiryDate,
        bool trialUsed)
    {
        // `Now` was server-LOCAL; the injected clock is UTC-ONLY. Every comparison and every offset below
        // therefore runs against a UTC instant, so a derived expiry can fall on a DIFFERENT CALENDAR DAY
        // from the one a legacy installation would have computed for the same real instant by up to the
        // host's offset from Greenwich.
        DateTime now = _clock.UtcNow;

        bool trialGoverns = !trialUsed
            && role.TrialFrequency is BillingFrequency trialFrequency
            && trialFrequency != BillingFrequency.None;

        int? period = trialGoverns ? role.TrialPeriod : role.BillingPeriod;
        BillingFrequency? frequency = trialGoverns ? role.TrialFrequency : role.BillingFrequency;

        // This changes no outcome, which is the point: it makes an existing accident explicit.
        requestedEffectiveDate = NormalizeLegacyDateMarker(requestedEffectiveDate);
        requestedExpiryDate = NormalizeLegacyDateMarker(requestedExpiryDate);

        DateTime? effectiveDate = requestedEffectiveDate;

        if (requestedExpiryDate is DateTime submittedExpiry)
        {
            return (effectiveDate, submittedExpiry);
        }

        if (period is not int units)
        {
            return (effectiveDate, null);
        }

        // The FALL-THROUGH value, however, stays absent rather than becoming that instant, and this is a
        // PRE-EXISTING DOCUMENTED DIVERGENCE that deliberately leaves standing.
        DateTime? expiryDate = null;
        DateTime offsetBase = now;

        expiryDate = frequency switch
        {
            BillingFrequency.None => null,
            BillingFrequency.OneTime => PerpetualExpiry,

            BillingFrequency.Day => AddOffsetWithinStorableRange(offsetBase, units),
            BillingFrequency.Week => AddOffsetWithinStorableRange(offsetBase, (long)units * DaysPerWeek),
            BillingFrequency.Month => AddMonthsWithinStorableRange(offsetBase, units),
            BillingFrequency.Year => AddMonthsWithinStorableRange(offsetBase, (long)units * MonthsPerYear),

            // The legacy selection had no default branch, so an unrecognised or absent frequency left
            // the date exactly as the clamping above had set it.
            _ => expiryDate,
        };

        return (effectiveDate, expiryDate);
    }

    /// <summary>
    /// Returns <see langword="null"/> when a submitted membership bound is the legacy absent-date marker,
    /// and the bound itself otherwise.
    /// </summary>
    /// <param name="bound">A bound as the caller submitted it.</param>
    /// <returns>The bound, with the marker read as absence.</returns>
    private static DateTime? NormalizeLegacyDateMarker(DateTime? bound) =>
        bound is DateTime value && value.Date == DateTime.MinValue.Date ? null : bound;

    /// <summary>Tests whether a role is one of the two the tenant designates for a system purpose.</summary>
    /// <param name="portal">The tenant row carrying the designations.</param>
    /// <param name="roleId">The role in question.</param>
    /// <returns><see langword="true"/> when the role is designated and therefore protected.</returns>
    private static bool IsPortalDesignatedRole(Portal portal, int roleId)
        => portal.AdministratorRoleId == roleId || portal.RegisteredRoleId == roleId;

    /// <summary>Describes a protected-role refusal in terms of the purpose the role is designated for.</summary>
    /// <param name="portal">The tenant row carrying the designations.</param>
    /// <param name="roleId">The designated role the request named.</param>
    /// <param name="attempted">The verb the caller attempted, for the sentence this composes.</param>
    /// <returns>The refusal detail.</returns>
    /// <remarks>
    /// The purpose is named rather than merely asserted, because "this role is protected" leaves an
    /// operator to guess which of the tenant's settings is protecting it. Nothing a caller submitted is
    /// interpolated - only the tenant's own designation and an authored verb - so the detail is safe to
    /// publish in a problem document.
    /// </remarks>
    private static string DescribeProtectedRole(Portal portal, int roleId, string attempted)
    {
        string purpose = portal.AdministratorRoleId == roleId
            ? "administrators"
            : "registered members";

        return FormattableString.Invariant(
            $"Role {roleId} is the portal's designated {purpose} role and cannot be {attempted}.");
    }

    /// <summary>
    /// Advances an instant by a whole number of days, clamping rather than overflowing when the result
    /// would fall outside the range the terminal <c>datetime</c> column can hold.
    /// </summary>
    /// <param name="offsetBase">The instant the offset runs forward from.</param>
    /// <param name="days">
    /// The offset in days, already widened to 64 bits by the caller so that the week multiplication cannot
    /// wrap.
    /// </param>
    /// <returns>
    /// The advanced instant, or the perpetual-expiry value when the offset runs past the storable maximum,
    /// or the storable minimum when a negative stored period runs back past it.
    /// </returns>
    /// <remarks>
    /// Clamping upwards to the perpetual-expiry value rather than to the column's last instant is
    /// deliberate: that value is already this domain's encoding of "no expiry" and is what the one-time
    /// frequency yields, so a membership whose term runs beyond the calendar the column can express is
    /// recorded as the perpetual term it effectively is, in the form a legacy reader recognises.
    /// </remarks>
    private static DateTime AddOffsetWithinStorableRange(DateTime offsetBase, long days)
    {
        if (days >= 0)
        {
            long daysAvailable = (PerpetualExpiry - offsetBase).Days;
            return days > daysAvailable ? PerpetualExpiry : offsetBase.AddDays(days);
        }

        long daysBehind = (offsetBase - SqlServerRange.MinimumDateTime).Days;
        return -days > daysBehind ? SqlServerRange.MinimumDateTime : offsetBase.AddDays(days);
    }

    /// <summary>
    /// Advances an instant by a whole number of months, clamping rather than overflowing when the result
    /// would fall outside the range the terminal <c>datetime</c> column can hold.
    /// </summary>
    /// <param name="offsetBase">The instant the offset runs forward from.</param>
    /// <param name="months">
    /// The offset in months, already widened to 64 bits by the caller so that the year multiplication
    /// cannot wrap.
    /// </param>
    /// <returns>
    /// The advanced instant, or the perpetual-expiry value when the offset runs past the storable maximum,
    /// or the storable minimum when a negative stored period runs back past it.
    /// </returns>
    /// <remarks>
    /// The bound is tested on the calendar rather than by catching the arithmetic's own failure, because
    /// the framework method refuses a month count beyond a fixed limit before it ever considers the
    /// resulting date - so a large period fails on the argument rather than on the result, and the two need
    /// the same answer. Testing the resulting month ordinal gives it to them.
    /// </remarks>
    private static DateTime AddMonthsWithinStorableRange(DateTime offsetBase, long months)
    {
        // Month ordinals counted from year one, which makes the comparison a single subtraction and
        // avoids reasoning about calendar carries twice.
        long ordinalNow = ((long)offsetBase.Year * MonthsPerYear) + offsetBase.Month;
        long ordinalTarget = ordinalNow + months;

        if (ordinalTarget > ((long)PerpetualExpiry.Year * MonthsPerYear) + PerpetualExpiry.Month)
        {
            return PerpetualExpiry;
        }

        if (ordinalTarget
            < ((long)SqlServerRange.MinimumDateTime.Year * MonthsPerYear) + SqlServerRange.MinimumDateTime.Month)
        {
            return SqlServerRange.MinimumDateTime;
        }

        // Now provably within range, so the framework call cannot fail: the ordinal fits the storable
        // calendar, which is narrower than the type's own, and the cast is safe for the same reason.
        return offsetBase.AddMonths((int)months);
    }

    /// <summary>
    /// Determines whether an update leaves the stored invitation code exactly as it is.
    /// </summary>
    /// <param name="stored">The code currently held against the role, which may be <see langword="null"/>.</param>
    /// <param name="submitted">The code the request carries, which may be <see langword="null"/>.</param>
    /// <returns>
    /// <see langword="true"/> when the request authors no new code; otherwise <see langword="false"/>.
    /// </returns>
    /// <remarks>
    /// Absent and empty are treated as the SAME state, because they are indistinguishable to an operator and
    /// the column stores both: a screen that renders a null code as an empty box and posts the empty box back
    /// has changed nothing, and reading that as a change would refuse the update it is meant to admit.
    /// The comparison is ordinal and case-SENSITIVE, because the stored value is a shared secret rather than a
    /// name - altering only its casing produces a different secret and is therefore authoring a new code.
    /// </remarks>
    private static bool RsvpCodeIsUnchanged(string? stored, string? submitted)
        => string.Equals(stored ?? string.Empty, submitted ?? string.Empty, StringComparison.Ordinal);

    /// <summary>Refuses a role whose submitted shape breaks a rule the legacy edit screen enforced.</summary>
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
    /// The nine validator controls on <c>Website/admin/Security/editroles.ascx</c> guarded both the create
    /// and the edit posts of one screen, so the same rules apply to both requests. The fee-versus-period
    /// asymmetry is genuine and is preserved: a fee may be zero, because a free role is legitimate, while a
    /// period may not WHERE A CYCLE IS DECLARED, because a cycle of zero units cannot advance an expiry.
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

        if (!RoleTermsRules.IsPeriodAdmissibleForFrequency(billingPeriod, billingFrequency))
        {
            throw new DomainException(RoleTermsRules.BillingPeriodNotPositiveMessage);
        }

        if (!RoleTermsRules.IsPeriodAdmissibleForFrequency(trialPeriod, trialFrequency))
        {
            throw new DomainException(RoleTermsRules.TrialPeriodNotPositiveMessage);
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

    /// <summary>Refuses a role-group name that breaks a stored-length or presence rule.</summary>
    /// <param name="roleGroupName">The submitted group name.</param>
    /// <exception cref="DomainException">Thrown when the name is absent or too long.</exception>
    /// <remarks>
    /// The same two rules the boundary validators declare, re-asserted here for callers that do not arrive
    /// over HTTP. <c>CreateRoleGroupRequestValidator</c> and <c>UpdateRoleGroupRequestValidator</c> are
    /// registered and run first for an HTTP caller, but this member is also reachable from a background
    /// job, a console tool or a test, and a rule that only an HTTP caller meets is not a rule.
    /// </remarks>
    private static void EnsureRoleGroupShapeIsValid(string roleGroupName)
    {
        if (string.IsNullOrWhiteSpace(roleGroupName))
        {
            throw new DomainException("A role group name is required.");
        }

        if (roleGroupName.Length > RoleGroupNameMaximumLength)
        {
            throw new DomainException($"A role group name may not exceed {RoleGroupNameMaximumLength} characters.");
        }
    }

    /// <summary>
    /// Describes a role-name clash in terms of the role that ALREADY holds the name, rather than in terms
    /// of the name the caller typed.
    /// </summary>
    /// <param name="requestedName">The name the caller submitted.</param>
    /// <param name="clashing">The role that already holds a name the store treats as equal.</param>
    /// <returns>A sentence naming the existing role.</returns>
    /// <remarks>
    /// The collation arm keeps its explanation, because withdrawing it would restore the defect it was
    /// written for - a refusal an operator cannot reconcile with anything on their screen. It names the
    /// STORED NAME only, which is not an internal detail at all: it is the text the roles listing already
    /// renders in the row above or below the one the operator is trying to create.
    /// </remarks>
    private static string DescribeNameClash(string requestedName, Role clashing)
    {
        ArgumentNullException.ThrowIfNull(clashing);

        return string.Equals(clashing.RoleName, requestedName, StringComparison.Ordinal)
            ? RoleTermsRules.DuplicateRoleMessage
            : FormattableString.Invariant(
                $"A role named '{clashing.RoleName}' already exists, and the database treats that name as identical to '{requestedName}'. Choose a name that differs in visible, sortable characters. The role was not added.");
    }
}
