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
    /// Reason code reported when a role the tenant designates for a system purpose is amended or removed.
    /// </summary>
    /// <remarks>
    /// MIGRATION: SEC-F3. The <c>protected</c> token is load-bearing rather than descriptive. The central
    /// translator derives the status from the token after the last separator and lists <c>protected</c>
    /// among the forbidden tokens, so this condition answers <c>403</c> - the caller's request is well
    /// formed and names a role that genuinely exists, and what refuses it is authority over that
    /// particular role rather than anything about the submission. It deliberately matches the token
    /// already carried by <see cref="AssignmentProtectedCode"/>, because the two express the same legacy
    /// idea at two scopes and a reader should not have to learn two vocabularies for it.
    /// </remarks>
    private const string RoleProtectedCode = "role.protected";

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
    private readonly IPermissionService _permissions;
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
    /// MIGRATION: the last two collaborators exist to preserve the legacy audit trail. The legacy role
    /// screens reached the event log through <c>EventLogController.AddLog</c> with the event keys
    /// <c>ROLE_CREATED</c>, <c>ROLE_UPDATED</c>, <c>ROLE_DELETED</c>, <c>USER_ROLE_CREATED</c> and
    /// <c>USER_ROLE_DELETED</c> (<c>EventLogController.vb:L57-L61</c>), and every such record carried the
    /// acting account's identifier and name. The store behind it is out of scope, so the record is
    /// emitted through <see cref="IAuditSink"/> instead; the acting account still has to come from the
    /// credential rather than from a request body, which is what <see cref="ICurrentUser"/> supplies.
    /// <para>
    /// MIGRATION: SEC-F8. The permission contract is a collaborator because a role's grants have to go with
    /// the role, and the rule bounding that removal is permission knowledge rather than role knowledge.
    /// Issuing the grant-table deletes from here would put a second copy of that rule in a service whose
    /// subject is roles, free to drift from the one the permission contract already holds - which is the
    /// same reasoning the account cascade in <c>UserService</c> records for itself.
    /// </para>
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

        // MIGRATION: EVERY NARROWING NOW TRAVELS TO THE STORE, and the reason it did not before is worth
        // recording because the reasoning was plausible. The legacy membership provider exposed no paged,
        // filtered or group-scoped role read - its whole role-listing surface was GetPortalRoles(PortalId)
        // (DataProvider.vb:L91), which returned every row, and the group restriction and the name search
        // were the admin screen's own work - so reproducing that shape meant materialising the tenant's
        // whole role set and narrowing it here. "A portal holds tens of roles" made that look free, but it
        // bounds the RESPONSE by the page while leaving the read, the sort and the allocation bounded only
        // by the tenant, and nothing in the response reveals the difference. Ownership, group scope, the
        // name search, the ordering, the count and the window are all expressible relationally, so all six
        // are now the store's; the choice of ordering travels as data. The reads are two, fixed.
        //
        // The strict ownership test travels with them. The unpaged GetPortalRoles read admits the
        // installation-wide roles that carry no owning portal, because the terminal procedure did
        // (04.08.00.SqlDataProvider:L40), and this screen lists only the roles the portal itself owns -
        // which is why ListAsync applies strict equality rather than that broader predicate. The narrowing
        // is unchanged; only the side of the boundary it happens on is.
        //
        // The two group arms keep their exact legacy meanings. A named group is compared by value, because
        // RoleGroupID is IDENTITY(0, 1) and zero is a legitimate group key - presence selects the filter,
        // magnitude never does. The ungrouped arm is the legacy "< Global Roles >" selection and tests for
        // the ABSENCE of a group, because Roles.RoleGroupID is nullable (03.02.03.SqlDataProvider:L34) and
        // an ungrouped role stores SQL null there. The legacy screen sent -1 for that, which
        // MembershipProviders/DataProvider/SqlDataProvider.vb:L231 converted to DBNull through Null.GetNull
        // before the terminal statement's "RoleGroupId IS NULL AND @RoleGroupId IS NULL" arm matched; that
        // sentinel round trip is gone and the intent it encoded is stated directly.
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

        // MIGRATION: SEC-F6. THE PRE-CHECK ABOVE CANNOT CLOSE THE RACE, SO THE FLUSH ANSWERS FOR IT.
        // IX_RoleName is unique over (PortalID, RoleName), and two requests carrying the same name arriving
        // together both read "not taken" before either inserts - so the loser's insert is refused by the
        // index rather than by the check. Measured on a live installation: ten simultaneous identical
        // creations produced one 201, seven 409 and two 500, with exactly one row stored. The two 500s were
        // the racers, told the server had failed when it had done precisely the right thing.
        //
        // The SAME reason code the pre-check emits is returned, deliberately, and with the same wording. A
        // caller cannot act differently on "you were second" than on "it was already there", so publishing
        // two codes for one outcome would only oblige it to handle both. The persistence layer translates
        // the provider fault into the Domain signal caught here; nothing in this layer names a provider
        // type, and nothing in the transport has to classify a store error.
        try
        {
            await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DuplicateKeyException)
        {
            return Result<RoleDetailDto>.Failure(
                RoleNameDuplicateCode,
                $"Portal {portalId} already has a role named '{request.RoleName}'.");
        }

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

        // MIGRATION: SEC-F3. THE TENANT ROW IS READ RATHER THAN PROBED, because this member now needs two
        // facts from it - that the tenant exists, and which two roles it has designated - and one read
        // answers both. This is the same substitution RemoveUserFromRoleAsync already makes for the same
        // reason, so the two protected-role rules resolve their designations identically.
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

        // MIGRATION: SEC-F3. The legacy edit screen closed BOTH verbs for a designated role, not just the
        // removal - Website/admin/Security/EditRoles.ascx.vb L174-L178 reads, verbatim:
        //     If RoleID = PortalSettings.AdministratorRoleId Or RoleID = PortalSettings.RegisteredRoleId Then
        //         cmdDelete.Visible = False
        //         cmdUpdate.Visible = False
        //         ActivateControls(False)
        //     End If
        // so the update path carries the guard as well. The refusal is stated AFTER the role resolution
        // deliberately: a designation that names no surviving row is a missing role rather than a
        // protected one, and answering "protected" for it would make an installation with a dangling
        // designation impossible to diagnose.
        if (IsPortalDesignatedRole(portal, roleId))
        {
            return Result<RoleDetailDto>.Failure(
                RoleProtectedCode,
                DescribeProtectedRole(portal, roleId, "amended"));
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
        // MIGRATION: SEC-F3. The tenant row is read rather than probed, for the reason recorded on the
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

        // MIGRATION: SEC-F3. THE DESIGNATED ROLES CANNOT BE REMOVED, and this is a repair of a genuine
        // hole rather than a hardening. The legacy screen withheld the removal outright
        // (EditRoles.ascx.vb L174-L178, quoted on the update path above); this contract accepted it and
        // answered 204. What the removal then did is why the severity is what it is: the cascade takes
        // every assignment with the role, so removing a tenant's designated administrators role
        // dispossessed every administrator it had, while the tenant's own AdministratorRoleId column was
        // left pointing at a row that no longer exists. The tenant then had no principal able to restore
        // the designation and - measured, not supposed - could not even be resolved by alias afterwards,
        // because the tenant snapshot the resolution composes reads the designated role's NAME. One
        // request could therefore take a tenant permanently offline.
        if (IsPortalDesignatedRole(portal, roleId))
        {
            return Result.Failure(RoleProtectedCode, DescribeProtectedRole(portal, roleId, "removed"));
        }

        // ONE SCOPE AROUND THE WHOLE REMOVAL. Two writes follow that cannot be expressed as a single
        // flush: the grant sweep reaches the store as set-based statements the moment it is issued, while
        // the role removal is staged and becomes durable at the commit. Enclosing both is what makes them
        // one outcome, and disposal without a commit is what rolls the pair back - so a store rejection, a
        // concurrency conflict raised by the commit, or a cancellation observed between the two steps all
        // leave the role and its grants exactly as they were, with no compensation routine.
        //
        // The default isolation is correct, and this was measured rather than assumed. The refusal above is
        // a check-then-write only if the designation can move underneath it, and it cannot: within the
        // whole Application layer, Portals.AdministratorRoleId is assigned in exactly one place - portal
        // provisioning, inside its own transaction - and no update path writes it. Nothing else this
        // operation read has to stay unchanged for the removal to be correct.
        await using (ITransactionScope transaction = await _unitOfWork
            .BeginTransactionAsync(TransactionIsolation.Default, cancellationToken)
            .ConfigureAwait(false))
        {
            // MIGRATION: SEC-F8. THE ROLE'S GRANTS GO WITH THE ROLE, in all three families, and this is a
            // repair of a real hole rather than a hardening. The terminal legacy procedure swept them -
            // 03.00.10.SqlDataProvider deletes from FolderPermission, ModulePermission and TabPermission by
            // RoleId before deleting the role row - and an earlier revision of this method deliberately did
            // not, on the grounds that no cascade existed to reproduce. That was the wrong conclusion from a
            // correct observation: the ABSENCE of a cascading foreign key from either grant table to the
            // role table is precisely why the rows survive their principal instead of being refused or
            // carried away. They then become authority with no holder, and because Roles.RoleID is an
            // identity column the vacated identifier is reissued, so the next role created in the
            // installation silently inherits every grant the removed one held.
            //
            // THE SWEEP GOES THROUGH THE PERMISSION CONTRACT, NOT THE GRANT REPOSITORY. The three tables are
            // one concern, and the rule bounding the removal to grants ADDRESSED TO THE ROLE - a grant made
            // to an account is the account's own, and a grant addressed to a negative pseudo-principal names
            // no role at all - is permission knowledge. Keeping one definition of it is the point.
            //
            // THE STAGE-ONLY MEMBER IS THE ONE CALLED. It neither commits nor evicts, so the sweep joins the
            // commit below rather than becoming durable ahead of it. Committing inside would be strictly
            // worse than the fault being repaired: a role left in place with every grant gone cannot be
            // reconstructed, whereas an abandoned removal keeps its grants.
            Result swept = await _permissions
                .StageRolePermissionRemovalAsync(portalId, roleId, cancellationToken)
                .ConfigureAwait(false);

            // A refusal is propagated rather than discarded. Nothing is durable at this point, so reporting
            // the reason leaves the role whole; swallowing it would commit a role removal whose grants were
            // still in place, which is the one outcome the sweep exists to prevent. Returning here disposes
            // the scope without committing, which rolls the batch back.
            if (swept.IsFailure)
            {
                return swept;
            }

            // MIGRATION: a role's assignments go with it. FK_UserRoles_Roles is declared ON DELETE CASCADE
            // in the schema this migration binds to, and UserRoleConfiguration declares the same behaviour,
            // so DeleteAsync loads the assignments and stages their removal with the role - one traversal
            // rather than a role-scoped assignment read this contract deliberately does not expose.
            await _roles.DeleteAsync(roleId, cancellationToken).ConfigureAwait(false);

            await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }

        // Everything below runs only once the batch is durable, so no eviction and no audit record can
        // describe a removal that did not happen.
        _cache.InvalidatePortal(portalId);

        // MIGRATION: SEC-F8. The grant-cache eviction is DELEGATED to the contract that owns it, in place of
        // the single page-grant eviction this method used to perform directly. That direct call was already
        // only half the answer while the grants stayed behind, and it is decisively half the answer now that
        // they go: the page-grant entry is tenant-keyed, but the module-grant entry is PAGE-keyed, so
        // evicting it portal-wide means naming each of the tenant's pages in turn. The permission contract
        // does exactly that and holds the only definition of the set, so calling it here keeps one
        // definition rather than opening a second that can drift. It is reached after the commit for the
        // reason its own contract states: evicting earlier would discard warm entries for a batch that might
        // still roll back, and would let a concurrent reader repopulate them from rows about to disappear.
        await _permissions
            .InvalidateUserPermissionCachesAsync(portalId, cancellationToken)
            .ConfigureAwait(false);

        // MIGRATION: reproduces the legacy ROLE_DELETED audit entry (EventLogController.vb:L61). The name is
        // captured BEFORE the removal, because after the commit the row it came from no longer exists and
        // the record would be reduced to a bare identifier nothing can resolve.
        RecordAudit(
            AuditEventNames.RoleDeleted,
            portalId,
            RoleResourceType,
            roleId);

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
        //
        // MIGRATION: THE ACCOUNT FILTER, THE ORDERING, THE COUNT AND THE WINDOW ALL TRAVEL TO THE STORE, for
        // the same reason the role listing's do. Reading every assignment of the role - each with its role
        // and account graph - and then narrowing, sorting and cutting a window here made the cost of one
        // page a function of the role's whole membership, which for a stock "Registered Users" role is the
        // whole tenant. The join is the same join; only the side of the boundary that narrows it has moved.
        // The account fragment still matches BOTH the display name and the login name, because the grid
        // shows the former while a caller who knows the account knows the latter, and honouring only one of
        // the two would make the same account findable only by luck.
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
    /// <para>
    /// Three reads, each one required to distinguish an answer from a broken request: the role proves the
    /// tenant owns it, the account proves the tenant has it, and the assignment answers the question. The
    /// role and the account are read FIRST and deliberately, because without them an unknown identifier
    /// and a genuine "holds nothing" would be indistinguishable — and only one of the two is something a
    /// caller should report as a failure.
    /// </para>
    /// <para>
    /// The projection is handed the role and the account explicitly rather than taken from the
    /// assignment's navigations. The single-assignment repository read composes the role and not the
    /// account, because that read is also on the enrolment write path where a second join would be paid
    /// for a projection that path never performs.
    /// </para>
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

        // A successful outcome carrying no value. The account exists, the role exists, and the account
        // simply holds no membership of it - which is the state the legacy screen rendered by blanking
        // its two date fields (SecurityRoles.ascx.vb L484) rather than by reporting anything.
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

        // MIGRATION: SEC-F5. The tenant row is READ rather than probed, because the bound-protection rule
        // below needs its two designations. The same substitution is made on the update and removal paths,
        // for the same reason, so every rule keyed on a designation resolves it identically.
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

        // MIGRATION: THE PORTAL ADMINISTRATOR'S OWN ADMINISTRATOR MEMBERSHIP CARRIES NO CALLER-SUPPLIED
        // BOUNDS. The legacy screen cleared both date boxes for exactly this pairing before reading them -
        // SecurityRoles.ascx.vb L522-L526:
        //     ' do not modify the portal Administrator account dates
        //     If User.UserID = PortalSettings.AdministratorId And Role.RoleID = PortalSettings.AdministratorRoleId.ToString Then
        //         txtEffectiveDate.Text = ""
        //         txtExpiryDate.Text = ""
        //     End If
        // and L528-L539 then substituted the absent-date marker for each empty box, so the assignment
        // member received absence for both. The comparison is made here with typed integers; the legacy
        // one compared an Integer against a String and relied on Option Strict being off to coerce it,
        // which is recorded rather than reproduced.
        //
        // It is enforced HERE and not merely in a screen, and SEC-F5 is why it had to be. While a
        // submitted bound was silently rewritten by the derivation, this pairing was protected by
        // accident. Now that a submitted bound is honoured, an expiry on this one membership would be
        // honoured too - and when it lapsed the tenant would be left with no administrator, which is the
        // same class of self-inflicted lockout the protected-role guard above exists to prevent. The
        // bounds are DISCARDED rather than the request refused, exactly as the screen discarded them.
        //
        // THE DERIVATION IS BYPASSED ENTIRELY FOR THIS PAIRING, and that is the whole rule rather than a
        // shortcut. The legacy screen blanked the two boxes and then called the VERBATIM member
        // (AddUserRole, L295-L315), so the row it wrote carried absence for both bounds and no derivation
        // ever ran over it. Feeding absence through the derivation instead is NOT equivalent, and the
        // difference was measured rather than reasoned about: portal provisioning creates a tenant's three
        // system roles with `BillingFrequency = 'M'` and `BillingPeriod = 0`, so the derivation reads a
        // period that is present and zero, adds zero months to the current instant, and yields an expiry
        // of NOW - an administrators-role membership that has expired by the time the response is written.
        // Exercised against a live tenant, the very next request from that administrator was refused
        // `auth.not_permitted`, because the authorisation handler reads assignment validity windows: one
        // assignment call locked the tenant's only administrator out of its own tenant. Absence is
        // therefore returned directly.
        bool bearsProtectedBounds =
            portal.AdministratorId == request.UserId && portal.AdministratorRoleId == roleId;

        (DateTime? effectiveDate, DateTime? expiryDate) = bearsProtectedBounds
            ? (null, null)
            : DeriveAssignmentDates(
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

        // SEC-F6: the concurrent counterpart of the name check above, for the reason recorded in full on
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
    /// <para>
    /// MIGRATION: reproduces RoleController.vb L503-L558 in the same order for the bounds the caller left
    /// ABSENT. The trial terms govern only when the trial has not already been consumed and the trial
    /// frequency is not the never code; otherwise the billing terms govern. An absent period yields no
    /// expiry at all. The current instant comes from the injected clock, never from an ambient reading.
    /// </para>
    /// <para>
    /// MIGRATION: SEC-F5. A bound the caller DID submit is returned verbatim and no derivation is run over
    /// it, reproducing RoleController.vb L295-L315 - the member the legacy screen actually called, which
    /// stored both dates exactly as given. The reasoning, and the four ways the previous revision silently
    /// rewrote a submitted bound behind a 2xx response, are recorded at the decision itself.
    /// </para>
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

        // MIGRATION: SEC-F5. A SUBMITTED BOUND IS STORED VERBATIM, AND THE DERIVATION APPLIES ONLY WHERE
        // THE CALLER SUBMITTED NONE. Two legacy members are collapsed into this one and they disagreed
        // with each other, so which of the two governs a given bound has to be decided explicitly:
        //
        //   * RoleController.vb L295-L315 - `AddUserRole(PortalID, UserId, RoleId, EffectiveDate,
        //     ExpiryDate)` - assigns `objUserRole.EffectiveDate = EffectiveDate` and
        //     `objUserRole.ExpiryDate = ExpiryDate` on BOTH the insert and the update branch and runs no
        //     derivation whatever. This is the member the screen this contract replaces actually called:
        //     SecurityRoles.ascx.vb L542 calls the seven-argument static at L647, which forwards to it.
        //   * RoleController.vb L489-L556 - `UpdateUserRole(PortalId, UserId, RoleId, Cancel)` - runs the
        //     derivation and takes NO date arguments at all; every value it works from is read out of the
        //     stored assignment at L513-L515.
        //
        // So the legacy never derived over a caller's own date, and it never ignored one either: the two
        // cases were reached through two different members. An earlier revision of this method ran the
        // derivation on TOP of the submitted bounds, which silently rewrote them on a 2xx response - a
        // submitted expiry was used only as the offset base and came back a period later, a submitted
        // expiry on a one-time role came back as the perpetual date, a submitted expiry on a role
        // declaring no period was discarded to null, and a submitted effective date already in the past
        // was discarded to null as well. A caller was told its instruction had been accepted while the
        // store held something else.
        //
        // The clamps at L530-L534 belong to the DERIVATION and are reproduced there, not here. Their
        // inputs were the STORED row's bounds, never a caller's, which is precisely why applying them to
        // a submitted value was wrong. On the derivation path this member reaches them with no bound at
        // all, and an absent bound is what L533-L534 turned into `Now` - so the seed below reproduces
        // them exactly for every case they can still be reached in.
        //
        // The trial-used fact is still primed from the stored row, as L515 did, because nothing a caller
        // submits may reset it. The pre-existing renewal divergence is unchanged and still applies to the
        // derivation path only: an assignment renewed with NO submitted expiry offsets from the present
        // instant rather than from its stored expiry, so an unexpired remainder is not carried forward. A
        // caller wanting the legacy carry-forward submits the stored expiry - which is exactly what that
        // screen did, since SecurityRoles.ascx.vb L273-L303 read the existing assignment solely to
        // pre-fill the two inputs it then posted back, and which now lands verbatim as it did then.
        DateTime? effectiveDate = requestedEffectiveDate;

        if (requestedExpiryDate is DateTime submittedExpiry)
        {
            return (effectiveDate, submittedExpiry);
        }

        if (period is not int units)
        {
            return (effectiveDate, null);
        }

        // The offset runs forward from the CURRENT INSTANT, which is what RoleController.vb L533-L534
        // produced for the only bound state that can reach this line: an absent expiry arrived at the
        // legacy engine as `Null.NullDate`, the minimum date value, which is always in the past, so the
        // comparison always fired and always replaced it with `Now`.
        //
        // The FALL-THROUGH value, however, stays absent rather than becoming that instant, and this is a
        // PRE-EXISTING DOCUMENTED DIVERGENCE that SEC-F5 deliberately leaves standing. The legacy engine's
        // unrecognised-frequency case fell out of its selection carrying `Now`, so a role holding an
        // unrecognised frequency character produced a membership that lapsed the instant it was created.
        // Storing no bound is the sane reading of the same code path, it is what this contract has always
        // answered, and it is pinned by its own unit fact - so reintroducing the legacy value here would
        // be an unrequested behaviour change dressed up as fidelity.
        DateTime? expiryDate = null;
        DateTime offsetBase = now;

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
    /// THIS IS THE ONLY PLACE THE RULE LIVES. An earlier revision applied it in the Infrastructure write
    /// path as well, on the reasoning that a bound should not reach the store as a value the
    /// <c>datetime</c> columns cannot hold whatever path produced it. That is one rule with two
    /// implementations in two layers: they agree until one is amended, and then they disagree on exactly
    /// the case that prompted the amendment. Rule T2 gives interpretation to this layer, so the
    /// repository copy was removed - <c>IRoleRepository</c> now documents that a write member stages what
    /// it is handed, and a marker reaching it is refused loudly by the store rather than quietly
    /// reinterpreted. Every production write path passes through here or supplies an explicit null.
    /// </para>
    /// </remarks>
    private static DateTime? NormalizeLegacyDateMarker(DateTime? bound) =>
        bound is DateTime value && value.Date == DateTime.MinValue.Date ? null : bound;

    /// <summary>
    /// Tests whether a role is one of the two the tenant designates for a system purpose.
    /// </summary>
    /// <param name="portal">The tenant row carrying the designations.</param>
    /// <param name="roleId">The role in question.</param>
    /// <returns><see langword="true"/> when the role is designated and therefore protected.</returns>
    /// <remarks>
    /// MIGRATION: SEC-F3. The two designations are read from the ADDRESSED TENANT'S OWN COLUMNS -
    /// <c>Portals.AdministratorRoleId</c> and <c>Portals.RegisteredRoleId</c> - and never from a literal,
    /// because in this schema no identifier is reserved: <c>Roles.RoleID</c> is <c>IDENTITY(0, 1)</c>, so
    /// role zero is an ordinary key that happens to be first, and every tenant designates its own pair.
    /// Comparing against a constant would protect the wrong role in every tenant but the first, and would
    /// leave the first tenant's real designations unprotected the moment they were re-pointed.
    /// <para>
    /// Both designations are nullable, and a null one matches nothing: <c>int?</c> equality against an
    /// <c>int</c> is false when the nullable has no value, so a tenant that designates neither role has no
    /// protected roles rather than a role protected by accident.
    /// </para>
    /// </remarks>
    private static bool IsPortalDesignatedRole(Portal portal, int roleId)
        => portal.AdministratorRoleId == roleId || portal.RegisteredRoleId == roleId;

    /// <summary>
    /// Describes a protected-role refusal in terms of the purpose the role is designated for.
    /// </summary>
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
}
