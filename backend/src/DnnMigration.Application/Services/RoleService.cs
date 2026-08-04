using System.Globalization;
using DnnMigration.Application.Abstractions;
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
/// MIGRATION: the notification the legacy assignment members sent is not reproduced, because no mail
/// subsystem is in scope. The <em>switch</em> is preserved on the contract even so:
/// <see cref="RoleAssignmentRequest.NotifyUser"/> reproduces the legacy "Send Notification?" checkbox,
/// which was a real caller input and was pre-selected by default, so deleting it would remove a
/// user-facing choice rather than an implementation detail. This service does not act on it. That
/// restraint is the point rather than an oversight - a switch that cannot be honoured must not be
/// allowed to imply a notification was sent - so the operation reports only what it actually did, and
/// success is never evidence of a notification. Keeping the member also makes supplying a notifier
/// later a purely additive change instead of a breaking one.
/// </para>
/// <para>
/// MIGRATION: the legacy file closes with a region of members retained only for binary compatibility,
/// and not one of them is ported. The count is stated as measured rather than as cited: the region
/// spans <c>RoleController.vb:L846-L888</c> and carries <strong>EIGHT</strong>
/// <c>&lt;Obsolete&gt;</c> wrappers, at L848, L853, L858, L863, L868, L873, L878 and L883 - the plan's
/// prose says nine while its own table lists eight, so the measurement is reported here and the plan is
/// left as it stands. Every one of the eight is a one-line delegation to a member that IS ported, so
/// nothing is lost by omitting them: the standing rule is that no obsolete member appears in the
/// target. Two of them additionally carried latent faults, which is a further reason not to carry them:
/// the role-creation wrapper at L849 has no <c>Return</c> statement at all, so it discarded the
/// identifier it delegated for and always answered 0, and the paid-services wrapper at L864 passed the
/// literal -1 described below.
/// </para>
/// <para>
/// MIGRATION: three legacy switches disappear with that region, because the region was their only
/// carrier. <c>SynchronizationMode</c> (L849) and <c>SynchronizeRoles</c> (L854) toggled the legacy
/// membership-and-role provider synchronisation model, which this migration replaces outright rather
/// than reproduces, so neither has anything left to switch; both wrappers ignored the flag entirely and
/// delegated to the single-argument member regardless. <c>includePrivate</c> (L408) was a real
/// parameter of the assignment listing, but its every non-obsolete caller passed <see langword="true"/>
/// (L393) while only the obsolete paid-services wrappers passed <see langword="false"/> (L865, L870),
/// so the migrated listing reproduces the surviving behaviour and offers no switch.
/// </para>
/// <para>
/// MIGRATION: the -1 all-users sentinel is gone from every surface. <c>GetServices(PortalId)</c> at
/// L864 reached the assignment listing as <c>GetUserRoles(PortalId, -1, False)</c>, borrowing the
/// absence marker as an account identifier to mean "every account in the portal", and L376-L377 did the
/// same for its own single-argument overload. That overloading is unsafe here as well as ugly: -1 is
/// <c>Null.NullInteger</c>, and this migration must keep absence and identity distinguishable
/// (Rule T7). The two answers are therefore separate members that name what they return - one account's
/// roles through <see cref="ListUserRolesAsync"/>, one role's members through
/// <see cref="ListRoleUsersAsync"/> - and no magic number reaches a parameter. Neither member accepts
/// a negative identifier as a wildcard, and an identifier that names nothing is reported as not found.
/// </para>
/// <para>
/// MIGRATION: no read on this service caches, and the omission is measured rather than convenient.
/// <c>RoleController.vb</c> contains ZERO cache sites of its own - the role reads went to the provider
/// on every call - so reproducing the legacy behaviour means not adding a cache, and adding one would be
/// the divergence. The cache abstraction is injected for the reverse duty: a role write invalidates the
/// portal entries and a membership write invalidates the account entry, so no OTHER subsystem's cached
/// projection outlives a role change. One legacy eviction has no counterpart, and it is bounded:
/// <c>PortalController.vb:L1131</c> called <c>DataCache.RemoveCache("GetRoles")</c> after inserting a new
/// portal's stock roles, evicting an entry keyed by that bare literal, while
/// this layer holds no such key - <c>ICacheService</c> publishes twelve members and not one of them
/// evicts roles, and the key itself is composed inside the infrastructure layer, so passing the legacy
/// spelling from here would be a guess that fails silently if the two ever diverged. Nothing in the
/// target writes that entry either, so there is no stale role list for the missing eviction to leave
/// behind. Recorded in <c>MIGRATION_NOTES.md</c> rather than papered over, and the multiplier that
/// would scale a cache lifetime is consequently not read here: a service that stores nothing has no
/// lifetime to scale.
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

    /// <summary>
    /// Reported when the page coordinates are well formed yet still cannot be honoured, which on this
    /// service means only one thing: a caller named an ordering this listing does not apply.
    /// </summary>
    private const string PagingInvalidCode = "role.paging_invalid";

    /// <summary>Reason code reported when a role group name is already used in the portal.</summary>
    private const string RoleGroupNameDuplicateCode = "role_group.name_duplicate";

    /// <summary>Reason code reported when a role group still classifies at least one role.</summary>
    private const string RoleGroupInUseCode = "role_group.in_use";

    /// <summary>
    /// Reason code reported when the two role-listing narrowing arguments contradict each other.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Raised only for the genuinely contradictory pair - one named group together with a request for the
    /// roles in no group. The contradiction is refused rather than resolved by preferring one argument,
    /// because a precedence rule would answer with a page the caller never asked for.
    /// </para>
    /// <para>
    /// The <c>_invalid</c> suffix is load-bearing rather than stylistic. The central translator derives a
    /// status from the token after the last separator, and this condition must answer 400: the request is
    /// one the caller can correct by dropping either argument, and it conflicts with nothing about the
    /// stored state. Naming it <c>scope_conflict</c> would have routed it to 409 through that table's
    /// <c>conflict</c> token, which would tell the caller the installation was in a conflicting state
    /// when in fact their own two query values disagreed. The suffix also matches its nearest siblings -
    /// <c>permission.filter_invalid</c> and <c>portal.paging_invalid</c> - which describe the same class
    /// of unusable filter combination.
    /// </para>
    /// </remarks>
    private const string RoleGroupScopeInvalidCode = "role_group.scope_invalid";

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

    /// <summary>Resource kind published on an audit record describing a role.</summary>
    /// <remarks>
    /// Spelled as the domain entity's own type name so a reader of the trail can go straight from a record
    /// to the type that produced it, and so the three kinds this service records cannot drift apart.
    /// </remarks>
    private const string RoleResourceType = "Role";

    // MIGRATION: role GROUPS are deliberately not audited, and the omission is measured rather than
    // arbitrary. The legacy event-log vocabulary declares forty-three members and not one of them names a
    // role group (EventLogController.vb:L38-L77), so the legacy screens at
    // Website/admin/Security/EditGroups.ascx.vb wrote no audit record for a group change and there is
    // nothing to preserve. Minting a name the legacy trail never contained would put an event into the
    // stream that no existing operator search expects, which is a worse outcome than the silence.

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

    /// <summary>
    /// Number of months in a year, used to express the yearly billing offset as a month offset.
    /// </summary>
    /// <remarks>
    /// The yearly offset is applied as a month count rather than through the framework's year addition
    /// so that both calendar frequencies share one range check. Adding twelve months is equivalent to
    /// adding a year for every date the column can hold, including the twenty-ninth of February, where
    /// both operations truncate to the twenty-eighth in a common year.
    /// </remarks>
    private const int MonthsPerYear = 12;

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
    private readonly ICurrentUser _currentUser;
    private readonly IAuditSink _audit;

    /// <summary>
    /// Initialises a new instance of the <see cref="RoleService"/> class.
    /// </summary>
    /// <param name="roles">Role, role group and assignment repository.</param>
    /// <param name="portals">Portal repository, consulted for tenancy and for the two protected identifiers.</param>
    /// <param name="users">Account repository, consulted to prove membership before an assignment.</param>
    /// <param name="unitOfWork">Commits each write exactly once.</param>
    /// <param name="clock">Supplies the current instant, so the expiry arithmetic is testable.</param>
    /// <param name="cache">Invalidates the portal and member entries a role change affects.</param>
    /// <param name="currentUser">
    /// Identifies the operator performing the change, so an audit record can attribute it.
    /// </param>
    /// <param name="audit">Receives the business audit record for every committed role change.</param>
    /// <remarks>
    /// MIGRATION: the last two collaborators exist to preserve the legacy audit trail. The legacy role
    /// screens reached the event log through <c>EventLogController.AddLog</c> with the event keys
    /// <c>ROLE_CREATED</c>, <c>ROLE_UPDATED</c>, <c>ROLE_DELETED</c>, <c>USER_ROLE_CREATED</c> and
    /// <c>USER_ROLE_DELETED</c> (<c>EventLogController.vb:L57-L61</c>), and every such record carried the
    /// acting account's identifier and name. The store behind it is out of scope, so the record is
    /// emitted through <see cref="IAuditSink"/> instead; the acting account still has to come from the
    /// credential rather than from a request body, which is what <see cref="ICurrentUser"/> supplies.
    /// </remarks>
    public RoleService(
        IRoleRepository roles,
        IPortalRepository portals,
        IUserRepository users,
        IUnitOfWork unitOfWork,
        IClock clock,
        ICacheService cache,
        ICurrentUser currentUser,
        IAuditSink audit)
    {
        _roles = roles ?? throw new ArgumentNullException(nameof(roles));
        _portals = portals ?? throw new ArgumentNullException(nameof(portals));
        _users = users ?? throw new ArgumentNullException(nameof(users));
        _unitOfWork = unitOfWork ?? throw new ArgumentNullException(nameof(unitOfWork));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
        _currentUser = currentUser ?? throw new ArgumentNullException(nameof(currentUser));
        _audit = audit ?? throw new ArgumentNullException(nameof(audit));
    }

    /// <summary>
    /// Records one committed role change on the audit trail.
    /// </summary>
    /// <param name="eventName">The stable event name, from <see cref="AuditEventNames"/>.</param>
    /// <param name="portalId">The tenant the change was made within.</param>
    /// <param name="resourceType">The kind of record changed - <c>Role</c>, <c>RoleGroup</c> or <c>UserRole</c>.</param>
    /// <param name="resourceId">The identifier of the record changed.</param>
    /// <param name="subjectUserId">
    /// The account a membership change was made against, or <see langword="null"/> for a change that
    /// names no account.
    /// </param>
    /// <param name="properties">Short, non-sensitive descriptive facts, or <see langword="null"/> for none.</param>
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
            ActorUserName = _currentUser.UserName,
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

        // MIGRATION: an UNDEFINED scope is refused before anything else, because this member's own
        // contract cannot honour one. RoleGroupScope is a closed pair - All and Ungrouped - but a CLR
        // enumeration is an integer at run time, so (RoleGroupScope)999 is a constructible value, and the
        // narrowing further down tests only for equality with Ungrouped: without this guard an undefined
        // scope fell through every branch and this member answered the FULL role list reporting success.
        // A caller asking for a scope this API does not implement would be told nothing was wrong and
        // handed a wider set than it asked for.
        //
        // MIGRATION - WHAT THIS GUARD IS AND IS NOT. It is NOT the HTTP boundary's defence, and a claim
        // that it was would be false. MEASURED at run time against this API: "?scope=999" is refused by
        // MVC model binding with 400 and errors["scope"] = ["The value '999' is invalid."] before this
        // action body runs, because EnumTypeModelBinder tests DEFINED membership for a non-flags
        // enumeration; "?scope=0" and "?scope=1" bind and answer 200, which proves numeric binding works
        // and that the refusal is specifically the membership check. So no HTTP caller ever reached the
        // fall-through. This guard exists because the Application layer is a public API in its own right,
        // reachable from callers that never touch MVC - other services, hosted work and tests - and an
        // invariant belongs to the layer that owns it. That is precisely why the two permission-evaluation
        // members of PermissionService already carry the identical Enum.IsDefined test.
        //
        // The test is placed above the contradiction check deliberately: pairing a group identifier with
        // an undefined scope must be reported as the undefined scope it is, not as a contradiction between
        // two meaningful arguments.
        if (!Enum.IsDefined(scope))
        {
            return Result<PagedResult<RoleListItemDto>>.Failure(
                RoleGroupScopeInvalidCode,
                FormattableString.Invariant(
                    $"Role group scope {(int)scope} is not defined; omit the scope to list every role."));
        }

        // The contradictory pair is refused before any store is touched: asking for one named group and
        // for the roles belonging to no group at all cannot both be satisfied, and no ordering of the two
        // arguments is more correct than the other. Pairing an identifier with the DEFAULT scope is not a
        // contradiction - that is what a caller which has never heard of the scope sends - so only the
        // explicitly ungrouped combination is refused.
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

        // MIGRATION: the PER-COLLECTION ordering set is enforced HERE, before anything is read. The
        // shared request validator applies nothing narrower than the union of every collection's set,
        // because one PagedRequest contract serves every listing, so on its own it would admit a
        // portal-only or account-only field name for this listing. Enforcing the narrow set at the point
        // of dispatch closes that for every caller, not only an HTTP one, and the set is exactly what the
        // ordering below honours.
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

        // MIGRATION: the narrowing below is applied here rather than in the repository because the
        // legacy membership provider exposed no paged, filtered or group-scoped role read. Its whole
        // role-listing surface was GetPortalRoles(PortalId) (DataProvider.vb:L91), which returned every
        // row; the group restriction and the name search were the admin screen's own work. A portal
        // holds tens of roles, so materialising its set and narrowing it in memory is faithful to the
        // legacy shape and costs nothing measurable.
        IReadOnlyList<Role> visible = await _roles
            .GetByPortalIdAsync(portalId, cancellationToken)
            .ConfigureAwait(false);

        // The repository read admits the installation-wide roles that carry no owning portal, because
        // the terminal GetPortalRoles did (04.08.00.SqlDataProvider:L40). This screen lists only the
        // roles the portal itself owns, which is the behaviour this endpoint has always had, so the
        // strict ownership test is reapplied here rather than weakened in the repository.
        IEnumerable<Role> matching = visible.Where(candidate => candidate.PortalId == portalId);

        if (roleGroupId is int filteredGroupId)
        {
            // RoleGroupID is IDENTITY(0, 1), so zero is a legitimate group key; the presence of a
            // value selects the filter, never its magnitude.
            matching = matching.Where(candidate => candidate.RoleGroupId == filteredGroupId);
        }
        else if (scope == RoleGroupScope.Ungrouped)
        {
            // MIGRATION: the legacy "< Global Roles >" selection, restored. Roles.RoleGroupID is a
            // NULLABLE column (03.02.03.SqlDataProvider:L34) and an ungrouped role stores SQL null there,
            // so the test is for ABSENCE of a group and not for any particular number. The legacy screen
            // sent -1 for this, which MembershipProviders/DataProvider/SqlDataProvider.vb:L231 converted
            // to DBNull through Null.GetNull before the terminal statement's
            // "RoleGroupId IS NULL AND @RoleGroupId IS NULL" arm matched; that sentinel round trip is
            // gone, and the intent it encoded is now stated directly.
            matching = matching.Where(candidate => candidate.RoleGroupId is null);
        }

        if (!string.IsNullOrWhiteSpace(request.Query))
        {
            string wanted = request.Query.Trim();
            matching = matching.Where(candidate =>
                candidate.RoleName.Contains(wanted, StringComparison.OrdinalIgnoreCase));
        }

        List<Role> ordered = OrderRoles(matching, request).ToList();

        int totalCount = ordered.Count;

        IReadOnlyList<RoleListItemDto> rows = (request.PageSize == 0
                ? ordered
                : ordered.Skip(Paging.SkipCount(request.PageIndex, request.PageSize)).Take(request.PageSize).ToList())
            .Select(RoleMappings.ToListItem)
            .ToList();

        PagedResult<RoleListItemDto> projected = request.PageSize == 0
            ? PagedResult<RoleListItemDto>.Unpaged(rows)
            : PagedResult<RoleListItemDto>.Create(rows, totalCount, request.PageIndex, request.PageSize);

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

        // MIGRATION: uniqueness is settled by the legacy name lookup itself. IX_RoleName is unique
        // over (PortalID, RoleName), so GetRoleByName (membership DataProvider.vb:L94) can match at
        // most one row and a non-null answer IS the duplicate report - no separate existence member is
        // needed on the repository contract.
        Role? clashing = await _roles
            .GetByNameAsync(portalId, request.RoleName, cancellationToken)
            .ConfigureAwait(false);
        if (clashing is not null)
        {
            return Result<RoleDetailDto>.Failure(
                RoleNameDuplicateCode,
                $"Portal {portalId} already has a role named '{request.RoleName}'.");
        }

        Role role = RoleMappings.ToNewRole(portalId, request);
        await _roles.AddAsync(role, cancellationToken).ConfigureAwait(false);

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

        await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        _cache.InvalidatePortal(portalId);

        Role? stored = await _roles.GetByIdAsync(role.RoleId, portalId, cancellationToken).ConfigureAwait(false);
        if (stored is null)
        {
            return Result<RoleDetailDto>.Failure(
                RoleCreateFailedCode,
                "The role was created but could not be read back.");
        }

        // MIGRATION: reproduces the legacy ROLE_CREATED audit entry (EventLogController.vb:L59), which the
        // legacy screen wrote after a successful insert. Recorded after the commit and after the read-back,
        // so the identifier on the record is the one the database actually assigned.
        RecordAudit(
            AuditEventNames.RoleCreated,
            portalId,
            RoleResourceType,
            stored.RoleId,
            properties: new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["RoleName"] = stored.RoleName,
                ["AutoAssignment"] = request.AutoAssignment.ToString(CultureInfo.InvariantCulture),
            });

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

        Role? role = await _roles.GetByIdAsync(roleId, portalId, cancellationToken).ConfigureAwait(false);
        if (role is null)
        {
            return Result<RoleDetailDto>.Failure(
                RoleNotFoundCode,
                $"Portal {portalId} has no role bearing identifier {roleId}.");
        }

        // The shape rules are re-asserted here as well as at the boundary. UpdateRoleRequestValidator is
        // registered and runs first for an HTTP caller, but this member is also reachable from a
        // background job, a console tool or a test, and a rule that only an HTTP caller meets is not a
        // rule. The legacy edit screen used one set of validator controls for both creating and editing
        // a role, so both paths carry the same checks.
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
            RoleGroup? group = await _roles.GetRoleGroupAsync(portalId, requestedGroupId, cancellationToken).ConfigureAwait(false);
            if (group is null)
            {
                return Result<RoleDetailDto>.Failure(
                    RoleGroupNotFoundCode,
                    $"Portal {portalId} has no role group bearing identifier {requestedGroupId}.");
            }
        }

        // MIGRATION: the portal-scoped uniqueness read runs on this path too, EXCLUDING the role being
        // edited. The legacy screen applied its duplicate-name guard only when inserting - at
        // Website/admin/Security/EditRoles.ascx.vb L251-L257 the add branch looks the name up first and
        // refuses on a hit, while the edit branch calls the update member with no such check - and that
        // asymmetry was coherent only because the name could not change on an edit. This contract can
        // rename, so the guard has to cover both paths or the rename would be the one way to manufacture
        // a duplicate. The exclusion is what makes an unchanged resubmitted name a no-op rather than a
        // self-collision, and it is compared on identifier rather than on text because the name is
        // precisely the value in question.
        //
        // The single read answers the question outright: IX_RoleName is unique over
        // (PortalID, RoleName) (03.00.09.SqlDataProvider L304), so GetByNameAsync - the legacy
        // GetRoleByName at membership DataProvider.vb L94 - can match at most one row, and a match whose
        // identifier differs from the edited role IS the duplicate report. Without it a rename onto an
        // existing name would reach the provider and surface as a server fault naming no field.
        Role? clashing = await _roles
            .GetByNameAsync(portalId, request.RoleName, cancellationToken)
            .ConfigureAwait(false);
        if (clashing is not null && clashing.RoleId != roleId)
        {
            return Result<RoleDetailDto>.Failure(
                RoleNameDuplicateCode,
                $"Portal {portalId} already has a role named '{request.RoleName}'.");
        }

        RoleMappings.ApplyUpdate(role, request);

        // MIGRATION: the legacy update member wrote the role and nothing else. Turning the
        // auto-assignment flag on during an edit therefore did not retrospectively enrol existing
        // members, and changing the billing or trial terms did not re-compute expiries already in
        // force; both are preserved deliberately, and the terms are re-read on the next assignment.
        await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        _cache.InvalidatePortal(portalId);

        // MIGRATION: reproduces the legacy ROLE_UPDATED audit entry (EventLogController.vb:L60). The name
        // is recorded because it is what a reader identifies the role by - the identifier alone would
        // force a second lookup to interpret the record - and it is read from the tracked entity after
        // the projection, so a record of a rename carries the name the update actually stored.
        RecordAudit(
            AuditEventNames.RoleUpdated,
            portalId,
            RoleResourceType,
            role.RoleId,
            properties: new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["RoleName"] = role.RoleName,
            });

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

        Role? role = await _roles.GetByIdAsync(roleId, portalId, cancellationToken).ConfigureAwait(false);
        if (role is null)
        {
            return Result.Failure(RoleNotFoundCode, $"Portal {portalId} has no role bearing identifier {roleId}.");
        }

        // MIGRATION: a role's assignments go with it. FK_UserRoles_Roles is declared ON DELETE CASCADE
        // in the schema this migration binds to, and UserRoleConfiguration declares the same behaviour,
        // so DeleteAsync loads the assignments and stages their removal with the role - one traversal
        // rather than a role-scoped assignment read this contract deliberately does not expose. The
        // permission rows are NOT swept: FK_ModulePermission_Roles_RoleID and
        // FK_TabPermission_Roles_RoleID carry no cascade and are configured NoAction, exactly as before.
        string removedRoleName = role.RoleName;

        await _roles.DeleteAsync(roleId, cancellationToken).ConfigureAwait(false);

        await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        _cache.InvalidatePortal(portalId);
        _cache.InvalidateTabPermissions(portalId);

        // MIGRATION: reproduces the legacy ROLE_DELETED audit entry (EventLogController.vb:L61). The name is
        // captured BEFORE the removal, because after the commit the row it came from no longer exists and
        // the record would be reduced to a bare identifier nothing can resolve.
        RecordAudit(
            AuditEventNames.RoleDeleted,
            portalId,
            RoleResourceType,
            roleId,
            properties: new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["RoleName"] = removedRoleName,
            });

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

        // MIGRATION: this reads the ASSIGNMENT rows, not the accounts, because the two assignment dates
        // the legacy grid rendered live on the assignment and exist nowhere else. The legacy screen took
        // the same route: SecurityRoles.ascx.vb:L246 binds GetUserRolesByRoleName when a role is
        // selected, DNNRoleProvider.vb:L520-L522 defines that as GetUserRoles(portalId, Nothing,
        // roleName), and the terminal GetUserRolesByUsername statement answers a null login name by
        // returning every assignment in the portal joined to its account and its role. So one read
        // composes all three records, exactly as the legacy result set did, and there is no read per row.
        //
        // An earlier revision read accounts through IUserRepository.ListByRoleNameAsync instead. That
        // read cannot carry the dates - an account has no effective or expiry date, the membership does -
        // which is why the projection it fed had to declare them absent.
        IReadOnlyList<UserRole> assignments = await _roles
            .GetUserRolesByUsernameAsync(portalId, username: null, roleName: role.RoleName, cancellationToken)
            .ConfigureAwait(false);

        IEnumerable<UserRole> matching = assignments;

        if (!string.IsNullOrWhiteSpace(request.Query))
        {
            // The filter matches the account, because that is the column the legacy screen searched and
            // the role is already fixed by the route. Both the display name and the login name are
            // considered: the grid shows the former, while a caller who knows the account knows the
            // latter, and refusing one of the two would make the same account findable only by luck.
            string wanted = request.Query.Trim();
            matching = matching.Where(assignment =>
                assignment.User is not null
                    && (assignment.User.DisplayName.Contains(wanted, StringComparison.OrdinalIgnoreCase)
                        || assignment.User.Username.Contains(wanted, StringComparison.OrdinalIgnoreCase)));
        }

        List<UserRole> ordered = OrderRoleMemberships(matching, request).ToList();

        int totalCount = ordered.Count;

        IReadOnlyList<RoleMembershipDto> rows = (request.PageSize == 0
                ? ordered
                : ordered.Skip(Paging.SkipCount(request.PageIndex, request.PageSize)).Take(request.PageSize).ToList())
            .Select(RoleMappings.ToMembership)
            .ToList();

        PagedResult<RoleMembershipDto> projected = request.PageSize == 0
            ? PagedResult<RoleMembershipDto>.Unpaged(rows)
            : PagedResult<RoleMembershipDto>.Create(rows, totalCount, request.PageIndex, request.PageSize);

        return Result<PagedResult<RoleMembershipDto>>.Success(projected);
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

        // The portal's roles are read once, unpaged, and indexed, so the projection costs two round
        // trips rather than one per assignment. The answer is bounded by the number of roles the portal
        // defines, which the legacy screen also rendered whole.
        IReadOnlyList<Role> portalRoles = await _roles
            .GetByPortalIdAsync(portalId, cancellationToken)
            .ConfigureAwait(false);

        Dictionary<int, Role> rolesById = portalRoles.ToDictionary(entry => entry.RoleId);

        var rows = new List<RoleListItemDto>(assignments.Count);
        foreach (UserRole assignment in assignments)
        {
            // One indexed lookup per assignment, and an assignment naming a role the portal does not
            // own is skipped rather than projected: the tenant scope is the answer's boundary, and a
            // dangling reference is not something a caller can act on.
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

        if (!await _portals.ExistsAsync(portalId, cancellationToken).ConfigureAwait(false))
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

        (DateTime? effectiveDate, DateTime? expiryDate) = DeriveAssignmentDates(
            role,
            request.EffectiveDate,
            request.ExpiryDate,
            existing?.IsTrialUsed ?? false);

        // MIGRATION: the legacy assignment member took the portal identifier as its first argument
        // (RoleController.vb:L295) and the repository member this one calls does NOT, because
        // dbo.UserRoles has no PortalID column at all - the assignment row is scoped only through the
        // role it names, whose own PortalID carries the tenancy. The legacy argument was therefore never
        // stored; it existed to reach the ambient per-request composite and to look the account up. Both
        // the tenant and the account are consequently proved HERE, above the repository: the portal, the
        // role's ownership of it and the account's membership of it are all resolved before anything is
        // staged, so tenant scope is enforced by this service rather than by the row. The same holds on
        // the removal path, whose repository member is likewise portal-free.
        if (existing is null)
        {
            await _roles.AddUserRoleAsync(
                RoleMappings.ToNewAssignment(roleId, request, effectiveDate, expiryDate),
                cancellationToken).ConfigureAwait(false);
        }
        else
        {
            // MIGRATION: the legacy member was an upsert (L295-L315) - it inserted when the member did
            // not yet hold the role and otherwise revised the two dates - so this member is idempotent
            // in exactly the same way. The trial-used fact is never reset by a renewal.
            RoleMappings.ApplyAssignmentUpdate(existing, effectiveDate, expiryDate);
        }

        await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        _cache.InvalidateUser(portalId, member.Username);

        // MIGRATION: reproduces the legacy USER_ROLE_CREATED audit entry (EventLogController.vb:L57). The
        // legacy member was an upsert and raised the same key for both arms, so a renewal is recorded under
        // it too; the Renewed property is what distinguishes the two without inventing a second event name
        // the legacy vocabulary does not contain. The two dates are recorded because they are the whole
        // substance of a renewal, and they are rendered round-trippably so a record can be compared
        // textually across hosts.
        RecordAudit(
            AuditEventNames.UserRoleCreated,
            portalId,
            UserRoleResourceType,
            roleId,
            subjectUserId: request.UserId,
            properties: new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["RoleName"] = role.RoleName,
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
            // MIGRATION: replaces `DateAdd(DateInterval.Day, -1, Date.Today())` (RoleController.vb:L496)
            // exactly - a date-only value, one day behind, with no time component. Two substitutions are
            // made and both are recorded rather than absorbed. First, the Visual Basic runtime's date
            // intrinsic becomes the framework's own day offset, which is the only reason the L25
            // `Imports Microsoft.VisualBasic` could be removed at all. Second, `Date.Today()` was
            // server-LOCAL whereas the injected clock is UTC-ONLY, so `UtcNow.Date` can name a DIFFERENT
            // CALENDAR DAY from the one the legacy code would have produced for the same real instant:
            // west of Greenwich the back-dated expiry can land a day earlier, east of it a day later.
            // That is accepted deliberately - a local-zone stamp is not comparable between hosts and
            // cannot be read without knowing the machine that wrote it - and the injected clock is also
            // what makes this whole engine testable. The `.Date` truncation is preserved because the
            // legacy value carried no time either, and because the row must read as already expired for
            // the whole of the current day rather than only after the current hour.
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

        // MIGRATION: reproduces the legacy USER_ROLE_DELETED audit entry (EventLogController.vb:L58). It is
        // raised for BOTH arms, including the expire-rather-than-delete arm, because the legacy code took
        // that arm inside the same removal member and reported one outcome to its caller; the Expired
        // property records which arm ran, so the trail can distinguish a withdrawn membership from a
        // back-dated one without a second event name.
        RecordAudit(
            AuditEventNames.UserRoleDeleted,
            portalId,
            UserRoleResourceType,
            roleId,
            subjectUserId: userId,
            properties: new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["RoleName"] = role.RoleName,
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

        // MIGRATION: group-name uniqueness is settled over the portal's own group list. The membership
        // provider exposed GetRoleGroups(portalId) (DataProvider.vb:L104) and no existence procedure, so
        // the comparison was always the caller's; a portal defines a handful of groups at most.
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

        await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

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

        // One page of size one is read purely for its total, because a group that still classifies a
        // role may not be removed and the repository exposes no bare count.
        // GetRolesByGroup (membership DataProvider.vb:L105) is the legacy read for exactly this
        // question, so the in-use check is expressed through it rather than through a filtered list.
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
        // MIGRATION: this single read replaces the FOUR ambient `Now` readings the legacy engine took -
        // the expiry seed at RoleController.vb:L505, the effective-date comparison at L530, and the
        // expiry comparison and assignment at L533-L534. Reading once rather than four times is not a
        // liberty: four separate readings of a moving clock can disagree with one another, so a request
        // that crossed a tick between L530 and L533 could clear an effective date against one instant
        // and seed an expiry from another. One reading makes the whole derivation internally consistent
        // and gives every branch below the same reference point.
        //
        // MIGRATION: `Now` was server-LOCAL; the injected clock is UTC-ONLY. Every comparison and every
        // offset below therefore runs against a UTC instant, so a derived expiry can fall on a DIFFERENT
        // CALENDAR DAY from the one a legacy installation would have computed for the same real instant -
        // by up to the host's offset from Greenwich. Accepted for the reasons recorded on the removal
        // path above, and pinned by the unit suite precisely because it is a clock and not a constant. A
        // caller needing a calendar date rather than an instant asks the clock for one, as the removal
        // path does; no line in this file reads the ambient system clock directly.
        DateTime now = _clock.UtcNow;

        bool trialGoverns = !trialUsed
            && role.TrialFrequency is BillingFrequency trialFrequency
            && trialFrequency != BillingFrequency.None;

        // MIGRATION: both period properties are `int?`, resolving a three-way disagreement in favour of
        // the schema as Rule T4 requires. The legacy membership provider typed the billing period as a
        // String, `RoleInfo.vb:L218` typed it as a non-nullable Integer, and the terminal column is
        // `[BillingPeriod] [int] NULL` (`01.00.08.SqlDataProvider:L6829`), with `[TrialPeriod] [int] NULL`
        // alongside it (`01.00.00.SqlDataProvider:L121`). The store wins, so absence is a null int and
        // never a small number - which is what lets the guard below test for absence honestly.
        int? period = trialGoverns ? role.TrialPeriod : role.BillingPeriod;
        BillingFrequency? frequency = trialGoverns ? role.TrialFrequency : role.BillingFrequency;

        // MIGRATION: MEASURED LATENT LEGACY DEFECT, ANNOTATED AND DELIBERATELY NOT FIXED.
        // `Dim Period As Integer` at RoleController.vb:L508 carries NO initialiser, so the legacy runtime
        // seeded it with 0 rather than with the absence sentinel -1. Consequently, whenever the role
        // lookup at L518 came back Nothing, the sentinel guard at L537 (`If Period = Null.NullInteger`)
        // could not fire, the frequency stayed the empty string the L509 declaration gave it, the L540
        // selection matched none of its six cases, and the assignment was written with the expiry L534
        // had just set to `Now` - an assignment that expired the instant it was created, where the
        // evident intent was no expiry at all. AAP 0.9.1 requires such a fault to be annotated in place
        // and not corrected, so it is recorded here rather than repaired.
        // It is also UNREACHABLE in the migrated shape, and that is a structural consequence rather than
        // a silent fix: `AssignUserToRoleAsync` resolves the role first and answers `role.not_found`
        // before this method is entered, so `role` is non-null on every path that reaches this line and
        // the defect's own precondition cannot arise. Nothing here depends on that, and nothing here
        // re-creates the zero: an absent period is `null` and is tested as `null` below, never against -1
        // and never against 0 - a role that legitimately declares a period of zero is refused by the
        // shape check rather than mistaken for one that declares none.

        // MIGRATION: a submitted bound carrying the legacy absent-date marker means "no bound", and is
        // read as one HERE, where the request enters the layer that interprets it. The marker is
        // Null.NullDate - Date.MinValue - and a caller built against the legacy contract had no other
        // way to say "unbounded", because the legacy property was a non-nullable VB Date. Under Rule T7
        // the boundary translates it and nothing below this line carries sentinel knowledge.
        //
        // This changes no outcome, which is the point: it makes an existing accident explicit. The
        // marker is always in the past, so the clamping immediately below already turned a submitted
        // effective marker into null, and a submitted expiry marker into "now" - which is the same
        // offset base an absent expiry produces, and which is discarded entirely when the role names no
        // period. Every branch below therefore reaches the value it reached before. What is gained is
        // that the reason is now stated rather than inferred, and a future change to the clamping
        // cannot silently turn the marker back into a real 0001-01-01 bound.
        requestedEffectiveDate = NormalizeLegacyDateMarker(requestedEffectiveDate);
        requestedExpiryDate = NormalizeLegacyDateMarker(requestedExpiryDate);

        // MIGRATION: the two bounds are primed from the REQUEST, where the legacy billing member primed
        // them from the row it had just read (RoleController.vb:L513-L514 assigning
        // `userRole.EffectiveDate` and `userRole.ExpiryDate`). The consequence is confined to one case and
        // is stated rather than hidden: renewing an assignment whose stored expiry is still in the FUTURE
        // while submitting no expiry of one's own now offsets from the present instant rather than from
        // that stored expiry, so the unexpired remainder of the term is not carried forward. A caller
        // that wants the legacy behaviour submits the stored expiry, which is exactly what the screen this
        // member serves did - `SecurityRoles.ascx.vb:L273-L303` read the existing assignment solely to
        // pre-fill the two inputs it then posted back.
        // The divergence is a consequence of the consolidation, not a choice made against the legacy: two
        // legacy members are collapsed into this one, and they disagreed with each other. L295 stored the
        // caller's two dates VERBATIM and ran no derivation at all, while L489 ran the derivation and
        // ignored the caller entirely, having no date parameters to ignore. One member cannot reproduce
        // both, so the derivation is kept - it is the behaviour that carries the paid-membership rules the
        // migration must preserve - and the caller's dates are honoured as its input. The trial-used fact
        // is still primed from the stored row, as L515 did, because nothing a caller submits may reset it.
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

            // MIGRATION: every offset goes through the clamping helper rather than calling the date
            // arithmetic directly. The period is an unbounded stored integer, and the four direct calls
            // this replaces each failed on a large one: the week case multiplied by seven in unchecked
            // 32-bit arithmetic and could WRAP to a negative day count, silently moving an expiry into
            // the past, while the day, month and year cases raised an out-of-range fault that surfaced
            // as a server error naming no field. The helper reproduces the legacy offsets exactly for
            // every period an installation can plausibly hold and states what happens beyond that.
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
    /// Returns <see langword="null"/> when a submitted membership bound is the legacy absent-date
    /// marker, and the bound itself otherwise.
    /// </summary>
    /// <param name="bound">A bound as the caller submitted it.</param>
    /// <returns>The bound, with the marker read as absence.</returns>
    /// <remarks>
    /// The marker is <c>Null.NullDate</c>, which is <c>Date.MinValue</c> (<c>Null.vb</c> lines 66-70),
    /// and the comparison is on the date part alone, matching the legacy emptiness tests at
    /// <c>Null.vb</c> lines 183-186 and 222-224 - both of which compare <c>.Date</c> against
    /// <c>NullDate.Date</c> and carry the source comment "this avoids subtle time differences". A
    /// caller that copied the marker out of a legacy object may have a time component attached to it,
    /// so an exact-equality test would let such a value through.
    /// <para>
    /// The Infrastructure write path applies the same rule again, deliberately, and the duplication is
    /// not an oversight: this one exists so a submitted marker is INTERPRETED as absence by the layer
    /// that derives the stored dates from the role's terms, while that one exists so no bound reaches
    /// the store as a value the <c>datetime</c> columns cannot hold, whatever path produced it.
    /// </para>
    /// </remarks>
    private static DateTime? NormalizeLegacyDateMarker(DateTime? bound) =>
        bound is DateTime value && value.Date == DateTime.MinValue.Date ? null : bound;

    /// <summary>
    /// Advances an instant by a whole number of days, clamping rather than overflowing when the result
    /// would fall outside the range the terminal <c>datetime</c> column can hold.
    /// </summary>
    /// <param name="offsetBase">The instant the offset runs forward from.</param>
    /// <param name="days">
    /// The offset in days, already widened to 64 bits by the caller so that the week multiplication
    /// cannot wrap.
    /// </param>
    /// <returns>
    /// The advanced instant, or the perpetual-expiry value when the offset runs past the storable
    /// maximum, or the storable minimum when a negative stored period runs back past it.
    /// </returns>
    /// <remarks>
    /// <para>
    /// Clamping upwards to the perpetual-expiry value rather than to the column's last instant is
    /// deliberate: that value is already this domain's encoding of "no expiry" and is what the one-time
    /// frequency yields, so a membership whose term runs beyond the calendar the column can express is
    /// recorded as the perpetual term it effectively is, in the form a legacy reader recognises.
    /// </para>
    /// <para>
    /// A negative offset is not reachable from a validated request - both period members are refused at
    /// or below zero - but a period stored before those rules existed can be negative, and this method
    /// is reached with stored values. It therefore clamps in both directions rather than assuming the
    /// sign it is given.
    /// </para>
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
    /// The advanced instant, or the perpetual-expiry value when the offset runs past the storable
    /// maximum, or the storable minimum when a negative stored period runs back past it.
    /// </returns>
    /// <remarks>
    /// The bound is tested on the calendar rather than by catching the arithmetic's own failure, because
    /// the framework method refuses a month count beyond a fixed limit before it ever considers the
    /// resulting date - so a large period fails on the argument rather than on the result, and the two
    /// need the same answer. Testing the resulting month ordinal gives it to them. The day-of-month
    /// truncation the framework performs for a shorter target month is left to the framework, so a term
    /// beginning on the thirty-first still lands where the legacy call put it.
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
    /// Refuses a role-group name that breaks a stored-length or presence rule.
    /// </summary>
    /// <param name="roleGroupName">The submitted group name.</param>
    /// <exception cref="DomainException">Thrown when the name is absent or too long.</exception>
    /// <remarks>
    /// The same two rules the boundary validators declare, re-asserted here for callers that do not
    /// arrive over HTTP. <c>CreateRoleGroupRequestValidator</c> and
    /// <c>UpdateRoleGroupRequestValidator</c> are registered and run first for an HTTP caller, but this
    /// member is also reachable from a background job, a console tool or a test, and a rule that only an
    /// HTTP caller meets is not a rule.
    /// </remarks>
    // MIGRATION: the parameter is the NAME rather than a whole contract, because the two write verbs now
    // bind two separate request types. Taking the shared value keeps one implementation of the rule for
    // both of them, in the same way EnsureRoleShapeIsValid above takes the role's own values rather than
    // either role contract.
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

    /// <summary>Orders a portal's roles by the field the caller named.</summary>
    /// <param name="roles">The narrowed roles, before paging.</param>
    /// <param name="request">The paging request carrying the ordering preference.</param>
    /// <returns>The ordered sequence.</returns>
    /// <remarks>
    /// <para>
    /// An ordering is applied unconditionally, and every arm ends on the key, so paging a set that shares
    /// a sort value still assigns each row to exactly one page. The default arm preserves the order this
    /// listing has always had - role name, then key - which is also the order the terminal
    /// <c>GetPortalRoles</c> procedure produced, so a caller who names nothing sees no change.
    /// </para>
    /// <para>
    /// MIGRATION: THE ARMS BELOW ARE EXACTLY <c>SortableFields.Roles</c>, and keeping the two identical is
    /// the point rather than a nicety - an allowlist entry with no arm accepts a name and then silently
    /// orders by something else. Ordering happens in memory because this listing already materialises the
    /// portal's whole role set to narrow it: the legacy membership provider exposed no paged, filtered or
    /// group-scoped role read, so the narrowing is this layer's work and the ordering belongs with it. A
    /// portal holds tens of roles, so the cost is not measurable.
    /// </para>
    /// <para>
    /// Names are compared case-insensitively and ordinally, matching the allowlist's own comparer, and the
    /// text arms sort case-insensitively for the same reason the legacy grid did: an operator reading a
    /// list of names does not expect capitalisation to decide position.
    /// </para>
    /// </remarks>
    private static IEnumerable<Role> OrderRoles(IEnumerable<Role> roles, PagedRequest request)
    {
        bool descending = request.SortDir == SortDirection.Descending;
        string field = request.HasSort ? request.SortBy!.Trim().ToUpperInvariant() : string.Empty;

        IOrderedEnumerable<Role> ordered = field switch
        {
            "ROLEID" => Order(roles, candidate => candidate.RoleId, descending),
            "DESCRIPTION" => Order(roles, candidate => candidate.Description ?? string.Empty, descending, StringComparer.OrdinalIgnoreCase),
            "SERVICEFEE" => Order(roles, candidate => candidate.ServiceFee, descending),
            "BILLINGFREQUENCY" => Order(roles, candidate => candidate.BillingFrequency, descending),
            "BILLINGPERIOD" => Order(roles, candidate => candidate.BillingPeriod, descending),
            "TRIALFEE" => Order(roles, candidate => candidate.TrialFee, descending),
            "TRIALFREQUENCY" => Order(roles, candidate => candidate.TrialFrequency, descending),
            "TRIALPERIOD" => Order(roles, candidate => candidate.TrialPeriod, descending),
            "ISPUBLIC" => Order(roles, candidate => candidate.IsPublic, descending),
            "AUTOASSIGNMENT" => Order(roles, candidate => candidate.AutoAssignment, descending),
            _ => Order(roles, candidate => candidate.RoleName, descending, StringComparer.OrdinalIgnoreCase),
        };

        return descending
            ? ordered.ThenByDescending(candidate => candidate.RoleId)
            : ordered.ThenBy(candidate => candidate.RoleId);
    }

    /// <summary>Orders a role's memberships by the field the caller named.</summary>
    /// <param name="memberships">The role's assignment rows, before paging.</param>
    /// <param name="request">The paging request carrying the ordering preference.</param>
    /// <returns>The ordered sequence.</returns>
    /// <remarks>
    /// The membership counterpart of <see cref="OrderRoles"/>, and the arms are exactly
    /// <c>SortableFields.RoleUsers</c>. Ordering in memory is what makes the ordering possible at all: the
    /// rows are assignments composed with their accounts by one repository read, so the account columns the
    /// vocabulary names are reachable here in a way no ordering clause over <c>dbo.UserRoles</c> alone
    /// could reach. The default arm preserves this listing's established order - display name, then the
    /// assignment key, which is what the legacy grid rendered in.
    /// </remarks>
    private static IEnumerable<UserRole> OrderRoleMemberships(
        IEnumerable<UserRole> memberships,
        PagedRequest request)
    {
        bool descending = request.SortDir == SortDirection.Descending;
        string field = request.HasSort ? request.SortBy!.Trim().ToUpperInvariant() : string.Empty;

        // The sort keys read the composed ACCOUNT, because the vocabulary the allowlist publishes for this
        // collection names account columns - the legacy grid rendered the account and sorted on what it
        // rendered. An assignment whose account failed to compose sorts as the empty value rather than
        // faulting the read, which is the same defensive stance the projection takes.
        IOrderedEnumerable<UserRole> ordered = field switch
        {
            "USERID" => Order(memberships, membership => membership.UserId, descending),
            "USERNAME" => Order(memberships, membership => Account(membership).Username, descending, StringComparer.OrdinalIgnoreCase),
            "FIRSTNAME" => Order(memberships, membership => Account(membership).FirstName, descending, StringComparer.OrdinalIgnoreCase),
            "LASTNAME" => Order(memberships, membership => Account(membership).LastName, descending, StringComparer.OrdinalIgnoreCase),
            "EMAIL" => Order(memberships, membership => Account(membership).Email ?? string.Empty, descending, StringComparer.OrdinalIgnoreCase),
            "CREATEDDATE" => Order(memberships, membership => Account(membership).CreatedDate, descending),
            "LASTLOGINDATE" => Order(memberships, membership => Account(membership).LastLoginDate, descending),
            "ISAPPROVED" => Order(memberships, membership => Account(membership).IsApproved, descending),
            "ISSUPERUSER" => Order(memberships, membership => Account(membership).IsSuperUser, descending),
            _ => Order(memberships, membership => Account(membership).DisplayName, descending, StringComparer.OrdinalIgnoreCase),
        };

        return descending
            ? ordered.ThenByDescending(membership => membership.UserRoleId)
            : ordered.ThenBy(membership => membership.UserRoleId);
    }

    /// <summary>The account an assignment composes, or an empty account when it composed none.</summary>
    /// <param name="membership">The assignment row.</param>
    /// <returns>The composed account, never null.</returns>
    private static User Account(UserRole membership) => membership.User ?? new User();

    /// <summary>Applies one ordering in the requested direction.</summary>
    /// <typeparam name="TItem">The item type being ordered.</typeparam>
    /// <typeparam name="TKey">The sort key type.</typeparam>
    /// <param name="items">The items to order.</param>
    /// <param name="key">Selects the sort key.</param>
    /// <param name="descending">Whether the ordering is descending.</param>
    /// <param name="comparer">An optional comparer for the key.</param>
    /// <returns>The ordered sequence, still open for a tie-breaking key.</returns>
    /// <remarks>
    /// Exists so that each arm above states its key once instead of stating it twice under a conditional,
    /// which is where an ascending and a descending arm drift apart. The return type stays
    /// <see cref="IOrderedEnumerable{TElement}"/> so the caller can append the key that breaks ties.
    /// </remarks>
    private static IOrderedEnumerable<TItem> Order<TItem, TKey>(
        IEnumerable<TItem> items,
        Func<TItem, TKey> key,
        bool descending,
        IComparer<TKey>? comparer = null)
        => descending
            ? items.OrderByDescending(key, comparer)
            : items.OrderBy(key, comparer);
}
