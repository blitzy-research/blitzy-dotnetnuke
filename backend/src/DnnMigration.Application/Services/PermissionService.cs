using System.Globalization;
using DnnMigration.Application.Abstractions;
using DnnMigration.Application.Dtos.Module;
using DnnMigration.Application.Options;
using DnnMigration.Domain.Abstractions.Repositories;
using DnnMigration.Domain.Abstractions.Services;
using DnnMigration.Domain.Common;
using DnnMigration.Domain.Entities;
using DnnMigration.Domain.Enums;

namespace DnnMigration.Application.Services;

/// <summary>
/// Answers permission questions within a portal: what the catalogue defines, what a caller effectively
/// holds, whether a caller holds one named key on one module or page, and the cleanup a deleted account
/// requires.
/// </summary>
/// <remarks>
/// Every member is tenant-scoped and every caller is named by argument, so no answer here depends on
/// ambient request state.
/// </remarks>
public sealed class PermissionService : IPermissionService
{
    /// <summary>Reported when a catalogue filter is supplied in a form that cannot match anything.</summary>
    private const string FilterInvalidCode = "permission.filter_invalid";

    /// <summary>Reported when the named portal does not exist.</summary>
    private const string PortalNotFoundCode = "permission.portal_not_found";

    /// <summary>Reported when the named module does not exist within the portal.</summary>
    private const string ModuleNotFoundCode = "permission.module_not_found";

    /// <summary>Reported when the named page does not exist within the portal.</summary>
    private const string TabNotFoundCode = "permission.tab_not_found";

    /// <summary>
    /// Reason code reported when the addressed module EXISTS but belongs to a different tenant than the one
    /// the request acts on.
    /// </summary>
    /// <remarks>
    /// ⚠ DISTINCT FROM <see cref="ModuleNotFoundCode"/> ON PURPOSE, AND THE DISTINCTION IS A
    /// TENANT-ISOLATION BOUNDARY RATHER THAN A NICETY. One consumer - the API's permission authorisation
    /// handler - is allowed to let a request through when the item genuinely does not exist, so that the
    /// endpoint can answer 404 like every other entity in this API instead of a misleading "not permitted".
    /// </remarks>
    private const string ModuleForeignTenantCode = "permission.module_foreign_tenant";

    /// <summary>
    /// Reason code reported when the addressed page EXISTS but belongs to a different tenant than the one
    /// the request acts on.
    /// </summary>
    private const string TabForeignTenantCode = "permission.tab_foreign_tenant";

    /// <summary>Reported when the named caller does not exist.</summary>
    private const string UserNotFoundCode = "permission.user_not_found";

    /// <summary>Reported when the named role does not exist within the portal.</summary>
    private const string RoleNotFoundCode = "permission.role_not_found";

    /// <summary>Reported when the submitted key is not a defined member of the closed key set.</summary>
    private const string KeyInvalidCode = "permission.key_invalid";

    /// <summary>
    /// Reported when a grant replacement is malformed in a way no individual reference explains - a missing
    /// body, a principal named twice, or a cell naming neither a role nor an account.
    /// </summary>
    /// <remarks>
    /// A code of its own rather than a reuse of <see cref="KeyInvalidCode"/>, which says "this permission
    /// does not apply here" and is corrected by choosing a different column. This one says "the shape of
    /// what you sent is wrong" and is corrected by rebuilding the request.
    /// </remarks>
    private const string RequestInvalidCode = "permission.request_invalid";

    /// <summary>Largest number of grants one module may carry in a single replacement.</summary>
    /// <remarks>
    /// A net-new bound with no legacy counterpart, and generous by construction: the grid that submits it
    /// has one row per portal role plus two pseudo-roles, times at most four declared columns, so a portal
    /// would need more than two hundred roles to approach it. It is stated to bound the row count of one
    /// transaction, not to constrain any real configuration.
    /// </remarks>
    private const int GrantsPerModuleMaximum = 1000;

    /// <summary>
    /// The value the grant readers treat as "every permission" in their permission argument, so that one
    /// module's whole grid can be read in a single query.
    /// </summary>
    /// <remarks>
    /// <c>Permission.PermissionID</c> is <c>IDENTITY (1, 1)</c>, so no catalogue entry can ever carry it and
    /// the wildcard can never collide with a real definition.
    /// </remarks>
    private const int AnyPermissionId = -1;

    /// <summary>
    /// Lowest value <c>dbo.ModuleDefinitions.ModuleDefID</c> can take, the column being <c>IDENTITY (1,
    /// 1)</c>, so a smaller value cannot name a row.
    /// </summary>
    private const int LowestModuleDefinitionId = 1;

    /// <summary>
    /// The view key, named once because the inherit-view rule is the only place a key is special-cased.
    /// </summary>
    private const string ViewPermissionKey = "VIEW";

    /// <summary>Minutes a cached catalogue projection is held for before the multiplier is applied.</summary>
    /// <remarks>
    /// The measured legacy value. Both permission timeouts were declared as 20 -
    /// <c>DataCache.ModulePermissionCacheTimeOut</c> and <c>DataCache.TabPermissionCacheTimeOut</c> - and
    /// each was multiplied by the installation-wide performance setting at its point of use.
    /// </remarks>
    private const int CatalogueCacheTimeOutMinutes = 20;

    /// <summary>
    /// Cache key format for the module-scoped catalogue definitions, keyed by the MODULE DEFINITION the
    /// answer actually depends on rather than by the module that was asked about.
    /// </summary>
    /// <remarks>
    /// MIGRATION: a NEW key family, and deliberately not one of the two legacy permission families. The
    /// legacy keys <c>ModulePermissions{0}</c> (tab-keyed) and <c>TabPermissions{0}</c> (portal-keyed) held
    /// GRANT ROW SETS, and this contract exposes no grant-set read at all - the grant-management surface is
    /// deliberately absent - so neither key has a counterpart read here to attach to.
    /// </remarks>
    private const string ModuleDefinitionsCacheKeyFormat = "PermissionDefinitionsByModuleDefinition|{0}";

    /// <summary>
    /// The invariant prefix of <see cref="ModuleDefinitionsCacheKeyFormat"/>, so the family can be evicted
    /// without enumerating the definitions it is keyed by.
    /// </summary>
    private static readonly string ModuleDefinitionsCacheKeyPrefix =
        ModuleDefinitionsCacheKeyFormat[..ModuleDefinitionsCacheKeyFormat.IndexOf('{', StringComparison.Ordinal)];

    /// <summary>Cache key for the page-scoped catalogue definitions, which are installation-wide.</summary>
    /// <remarks>
    /// ONE ENTRY, WITH NO PAGE DIMENSION, and that is a measurement rather than a simplification: the
    /// terminal page-scoped catalogue statement filters on the product-wide page scope code and never
    /// references its page argument at all, so every page receives the same rows and the repository
    /// documents that it accepts the argument unused.
    /// </remarks>
    private const string TabDefinitionsCacheKey = "PermissionDefinitionsByTab|all";

    /// <summary>Every permission key THIS SOLUTION names, which is the enumeration itself.</summary>
    /// <remarks>
    /// <para>
    /// This is emphatically NOT a claim about what the column can hold. <c>Permission.PermissionKey</c> is
    /// free-text <c>varchar(50)</c> with no check constraint, and an installation carrying a third-party
    /// module can already hold a key outside this set; the entity models it as a plain string precisely so
    /// that such a row materialises intact. What this sequence bounds is the set of keys the target can
    /// ASK ABOUT - the ones its authorisation policies are written against.
    /// </para>
    /// <para>
    /// It is used in exactly two places, and each is bounded for its own reason. The host-account answer is
    /// settled from it because a host holds every right by definition and reading rows to prove that would
    /// be a round trip with a foregone conclusion. The catalogue question uses it as the PROBE CANDIDATE
    /// LIST wherever the caller has not named a module definition, because the legacy catalogue exposes no
    /// all-rows reader at all - <c>DataProvider.vb</c> lines 280-288 declare readers by identifier, by
    /// module definition, by module, by folder path, by scope-code-and-key and by page, and nothing else -
    /// so a scope can only be interrogated one candidate key at a time. Naming a module definition takes
    /// the store-backed path instead, and that path reports the stored spellings whatever they are.
    /// </para>
    /// </remarks>
    private static readonly IReadOnlyList<PermissionKey> AllPermissionKeys = Enum.GetValues<PermissionKey>();

    private readonly IPermissionRepository _permissions;
    private readonly IPermissionEvaluator _evaluator;
    private readonly IPortalRepository _portals;
    private readonly IModuleRepository _modules;
    private readonly ITabRepository _tabs;
    private readonly IUserRepository _users;

    /// <summary>Role assignments, read only to answer whether a caller administers a portal.</summary>
    private readonly IRoleRepository _roles;

    private readonly IUnitOfWork _unitOfWork;
    private readonly ICacheService _cache;
    private readonly IClock _clock;
    private readonly CachingOptions _caching;

    /// <summary>Initialises the service with the collaborators it resolves callers and scopes through.</summary>
    /// <param name="permissions">
    /// The permission aggregate's persistence contract: the catalogue and the grant rows.
    /// </param>
    /// <param name="evaluator">The single authority on allow-and-deny precedence.</param>
    /// <param name="portals">Portal existence.</param>
    /// <param name="modules">Module existence and, for the inherit-view rule, module placement.</param>
    /// <param name="tabs">Page existence.</param>
    /// <param name="users">Caller resolution and the caller's role names.</param>
    /// <param name="roles">The caller's role ASSIGNMENTS, read only by the portal-administration test.</param>
    /// <param name="unitOfWork">The commit boundary.</param>
    /// <param name="cache">
    /// Cache reads for the catalogue and the eviction the account cleanup owes its two grant tables.
    /// </param>
    /// <param name="clock">The instant role assignments are evaluated as of.</param>
    /// <param name="caching">
    /// Bound configuration supplying the performance multiplier that scales every cache lifetime.
    /// </param>
    public PermissionService(
        IPermissionRepository permissions,
        IPermissionEvaluator evaluator,
        IPortalRepository portals,
        IModuleRepository modules,
        ITabRepository tabs,
        IUserRepository users,
        IRoleRepository roles,
        IUnitOfWork unitOfWork,
        ICacheService cache,
        IClock clock,
        CachingOptions caching)
    {
        _permissions = permissions ?? throw new ArgumentNullException(nameof(permissions));
        _evaluator = evaluator ?? throw new ArgumentNullException(nameof(evaluator));
        _portals = portals ?? throw new ArgumentNullException(nameof(portals));
        _modules = modules ?? throw new ArgumentNullException(nameof(modules));
        _tabs = tabs ?? throw new ArgumentNullException(nameof(tabs));
        _users = users ?? throw new ArgumentNullException(nameof(users));
        _roles = roles ?? throw new ArgumentNullException(nameof(roles));
        _unitOfWork = unitOfWork ?? throw new ArgumentNullException(nameof(unitOfWork));
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _caching = caching ?? throw new ArgumentNullException(nameof(caching));
    }

    /// <inheritdoc />
    /// <remarks>
    /// The role designated by the portal is read from the portal NAMED IN THE ARGUMENT rather than from the
    /// resolved tenant snapshot, because "does this caller administer portal X" and "does this caller
    /// administer the tenant they arrived through" are different questions and only the first is being
    /// asked.
    /// </remarks>
    /// <inheritdoc />
    public async Task<Result<bool>> HasAnyTabPermissionInPortalAsync(
        int portalId,
        int? userId,
        PermissionKey permissionKey,
        CancellationToken cancellationToken = default)
    {
        if (!Enum.IsDefined(permissionKey))
        {
            return Result<bool>.Failure(
                KeyInvalidCode,
                FormattableString.Invariant($"Permission key {(int)permissionKey} is not defined."));
        }

        // THE ADMINISTRATION ARM IS ASKED FIRST, AND ITS ORDER IS THE CHEAP-AND-DECISIVE ONE. It reads the
        // account and the portal row; the grant arm below reads the tenant's whole page set plus the
        // permission catalogue plus the grant rows.
        Result<bool> administers = await IsPortalAdministratorAsync(portalId, userId, cancellationToken)
            .ConfigureAwait(false);

        if (administers.IsSuccess && administers.Value)
        {
            return Result<bool>.Success(true);
        }

        CallerIdentity caller = await ResolveCallerAsync(portalId, userId, cancellationToken)
            .ConfigureAwait(false);

        if (!caller.Found)
        {
            return Result<bool>.Success(false);
        }

        if (caller.IsSuperUser)
        {
            return Result<bool>.Success(true);
        }

        IReadOnlyList<Tab> pages = await _tabs.GetByPortalIdAsync(portalId, cancellationToken)
            .ConfigureAwait(false);

        if (pages.Count == 0)
        {
            // A tenant with no pages grants nothing to anybody, which is an ordinary state and not a fault.
            // No grant read is issued for it.
            return Result<bool>.Success(false);
        }

        // ONE QUESTION OVER THE WHOLE PAGE SET, never one per page.
        Result<bool> granted = await _evaluator
            .HasAnyTabPermissionAsync(
                pages.Select(page => page.TabId).Distinct().ToList(),
                permissionKey,
                userId,
                caller.RoleNames,
                cancellationToken)
            .ConfigureAwait(false);

        return Result<bool>.Success(VerdictOf(granted));
    }

    /// <inheritdoc />
    public async Task<Result<IReadOnlyList<int>>> ListTabsWithPermissionAsync(
        int portalId,
        int? userId,
        IReadOnlyCollection<int> tabIds,
        PermissionKey permissionKey,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tabIds);

        if (!Enum.IsDefined(permissionKey))
        {
            return Result<IReadOnlyList<int>>.Failure(
                KeyInvalidCode,
                FormattableString.Invariant($"Permission key {(int)permissionKey} is not defined."));
        }

        if (tabIds.Count == 0)
        {
            return Result<IReadOnlyList<int>>.Success([]);
        }

        // EVERY NAMED PAGE, for a caller who administers the tenant or the installation.
        Result<bool> administers = await IsPortalAdministratorAsync(portalId, userId, cancellationToken)
            .ConfigureAwait(false);

        if (administers.IsSuccess && administers.Value)
        {
            return Result<IReadOnlyList<int>>.Success(tabIds.ToList());
        }

        CallerIdentity caller = await ResolveCallerAsync(portalId, userId, cancellationToken)
            .ConfigureAwait(false);

        if (!caller.Found)
        {
            return Result<IReadOnlyList<int>>.Success([]);
        }

        if (caller.IsSuperUser)
        {
            return Result<IReadOnlyList<int>>.Success(tabIds.ToList());
        }

        Result<IReadOnlyList<int>> granting = await _evaluator
            .ListTabsWithPermissionAsync(tabIds, permissionKey, userId, caller.RoleNames, cancellationToken)
            .ConfigureAwait(false);

        return Result<IReadOnlyList<int>>.Success(
            granting.IsSuccess ? granting.Value : []);
    }

    public async Task<Result<bool>> IsPortalAdministratorAsync(
        int portalId,
        int? userId,
        CancellationToken cancellationToken = default)
    {
        if (userId is not int callerId)
        {
            return Result<bool>.Success(false);
        }

        // The host account is resolved without a portal scope, exactly as ResolveCallerAsync does, because
        // a host account belongs to no tenant and a portal-scoped read would not find it.
        User? account = await _users.GetAsync(portalId, callerId, cancellationToken).ConfigureAwait(false)
            ?? await _users.GetAsync(portalId: null, callerId, cancellationToken).ConfigureAwait(false);

        if (account is null)
        {
            return Result<bool>.Success(false);
        }

        if (account.IsSuperUser)
        {
            return Result<bool>.Success(true);
        }

        // Aliases are not requested: the administrator role identifier is a column on the portal row, and
        // loading the alias collection to read it would fetch rows this question never looks at.
        Portal? portal = await _portals
            .GetByIdAsync(portalId, includeAliases: false, cancellationToken)
            .ConfigureAwait(false);

        if (portal?.AdministratorRoleId is not int administratorRoleId)
        {
            return Result<bool>.Success(false);
        }

        DateTime asOfUtc = _clock.UtcNow;

        IReadOnlyList<UserRole> assignments = await _roles
            .GetUserRolesAsync(portalId, callerId, cancellationToken)
            .ConfigureAwait(false);

        bool administers = assignments.Any(assignment =>
            assignment.RoleId == administratorRoleId
            && assignment.GetStatus(asOfUtc) == RoleStatus.Active);

        return Result<bool>.Success(administers);
    }

    /// <inheritdoc />
    /// <remarks>
    /// The account is read portal-scoped first and then unscoped, which is the same two-step <see
    /// cref="IsPortalAdministratorAsync"/> and <c>ResolveCallerAsync</c> perform: a host account belongs to
    /// no tenant, so a portal-scoped read alone would not find it, while trying the scoped read first keeps
    /// the ordinary case - a member of this tenant - to one query.
    /// </remarks>
    public async Task<Result<bool>> IsHostAccountAsync(
        int portalId,
        int? userId,
        CancellationToken cancellationToken = default)
    {
        if (userId is not int callerId)
        {
            return Result<bool>.Success(false);
        }

        User? account = await _users.GetAsync(portalId, callerId, cancellationToken).ConfigureAwait(false)
            ?? await _users.GetAsync(portalId: null, callerId, cancellationToken).ConfigureAwait(false);

        return Result<bool>.Success(account?.IsSuperUser ?? false);
    }

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// The three filters compose conjunctively, so supplying none returns the whole catalogue. The scope
    /// code is deliberately matched as free text rather than against an enumeration, because the column it
    /// lives in is free text and an installation carrying a code this codebase has never seen must still
    /// round-trip intact.
    /// </para>
    /// <para>
    /// The KEY column is free text on exactly the same footing, and every filter shape now reports the
    /// stored spellings verbatim rather than only the definition-scoped one. The key FILTER remains typed,
    /// so a spelling outside the enumeration is refused at the boundary with a bad request rather than
    /// silently matching nothing.
    /// </para>
    /// </remarks>
    public async Task<Result<IReadOnlyList<PermissionDto>>> GetPermissionCatalogueAsync(
        string? permissionCode = null,
        int? moduleDefinitionId = null,
        PermissionKey? permissionKey = null,
        CancellationToken cancellationToken = default)
    {
        // A SUPPLIED-BUT-BLANK code is refused rather than treated as "no filter", because the two mean
        // different things to a caller: omitting the parameter asks for the whole catalogue, while sending
        // an empty one asks for the codes that are blank, and there are none.
        if (permissionCode is not null && string.IsNullOrWhiteSpace(permissionCode))
        {
            return Result<IReadOnlyList<PermissionDto>>.Failure(
                FilterInvalidCode,
                "The permission code filter must not be blank; omit it to place no restriction.");
        }

        // WHAT THIS GUARD IS AND IS NOT. It is NOT the HTTP boundary's defence.
        if (permissionKey is PermissionKey wantedKeyFilter && !Enum.IsDefined(wantedKeyFilter))
        {
            return Result<IReadOnlyList<PermissionDto>>.Failure(
                KeyInvalidCode,
                FormattableString.Invariant(
                    $"Permission key {(int)wantedKeyFilter} is not defined; omit the filter to place no restriction."));
        }

        if (moduleDefinitionId is int definitionId && definitionId < LowestModuleDefinitionId)
        {
            return Result<IReadOnlyList<PermissionDto>>.Failure(
                FilterInvalidCode,
                FormattableString.Invariant(
                    $"Module definition {definitionId} cannot name a row; identifiers start at {LowestModuleDefinitionId}."));
        }

        IReadOnlyList<PermissionDto> definitions = Project(
            await ReadCatalogueAsync(permissionCode, moduleDefinitionId, permissionKey, cancellationToken)
                .ConfigureAwait(false));

        return Result<IReadOnlyList<PermissionDto>>.Success(definitions);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Supplying neither scope answers at portal level - the union of everything the caller holds anywhere
    /// in the portal - which is the set an administration shell needs to decide which sections to offer and
    /// the set that populates an access token's permission claims. Supplying either scope, or both, narrows
    /// the answer to it.
    /// </remarks>
    public async Task<Result<IReadOnlyList<string>>> GetEffectivePermissionKeysAsync(
        int portalId,
        int? userId,
        int? moduleId = null,
        int? tabId = null,
        CancellationToken cancellationToken = default)
    {
        if (!await _portals.ExistsAsync(portalId, cancellationToken).ConfigureAwait(false))
        {
            return Result<IReadOnlyList<string>>.Failure(
                PortalNotFoundCode,
                FormattableString.Invariant($"Portal {portalId} does not exist."));
        }

        CallerIdentity caller = await ResolveCallerAsync(portalId, userId, cancellationToken)
            .ConfigureAwait(false);

        if (!caller.Found)
        {
            return Result<IReadOnlyList<string>>.Failure(
                UserNotFoundCode,
                FormattableString.Invariant($"Account {userId} does not exist in portal {portalId}."));
        }

        if (caller.IsSuperUser)
        {
            // A host account is answered from the closed PermissionKey enumeration rather than by reading
            // the catalogue table.
            return Result<IReadOnlyList<string>>.Success(Normalise(
                AllPermissionKeys.Select(key => key.ToString())));
        }

        Module? module = null;
        if (moduleId is int scopedModuleId)
        {
            module = await _modules
                .GetByIdAsync(scopedModuleId, cancellationToken)
                .ConfigureAwait(false);

            if (module is null || !BelongsToPortal(module.PortalId, portalId))
            {
                return Result<IReadOnlyList<string>>.Failure(
                    ModuleNotFoundCode,
                    FormattableString.Invariant(
                        $"Module {scopedModuleId} does not exist in portal {portalId}."));
            }
        }

        if (tabId is int scopedTabId)
        {
            Tab? tab = await _tabs.GetByIdAsync(scopedTabId, cancellationToken).ConfigureAwait(false);
            if (tab is null || !BelongsToPortal(tab.PortalId, portalId))
            {
                return Result<IReadOnlyList<string>>.Failure(
                    TabNotFoundCode,
                    FormattableString.Invariant($"Page {scopedTabId} does not exist in portal {portalId}."));
            }
        }

        if (module is null && tabId is null)
        {
            Result<IReadOnlyList<string>> portalWide = await _evaluator
                .ListEffectivePortalPermissionKeysAsync(portalId, userId, caller.RoleNames, cancellationToken)
                .ConfigureAwait(false);

            return Result<IReadOnlyList<string>>.Success(Normalise(KeysOf(portalWide)));
        }

        var keys = new List<string>();

        if (module is not null)
        {
            IReadOnlyList<string> moduleKeys = KeysOf(await _evaluator
                .ListEffectiveModulePermissionKeysAsync(
                    module.ModuleId,
                    userId,
                    caller.RoleNames,
                    cancellationToken)
                .ConfigureAwait(false));

            if (module.InheritViewPermissions == true)
            {
                keys.AddRange(moduleKeys.Where(key =>
                    !string.Equals(key, ViewPermissionKey, StringComparison.OrdinalIgnoreCase)));

                IReadOnlyList<TabModule> placements =
                    await ReadPlacementsAsync(module, cancellationToken).ConfigureAwait(false);

                if (await InheritedViewGrantedAsync(
                        placements,
                        tabId,
                        placementTabModuleId: null,
                        userId,
                        caller.RoleNames,
                        cancellationToken)
                    .ConfigureAwait(false))
                {
                    keys.Add(ViewPermissionKey);
                }
            }
            else
            {
                keys.AddRange(moduleKeys);
            }
        }

        if (tabId is int pageId)
        {
            IReadOnlyList<string> tabKeys = KeysOf(await _evaluator
                .ListEffectiveTabPermissionKeysAsync(pageId, userId, caller.RoleNames, cancellationToken)
                .ConfigureAwait(false));

            keys.AddRange(tabKeys);
        }

        return Result<IReadOnlyList<string>>.Success(Normalise(keys));
    }

    /// <inheritdoc />
    public async Task<Result<bool>> HasModulePermissionAsync(
        int portalId,
        int? userId,
        int moduleId,
        PermissionKey permissionKey,
        int? placementTabId = null,
        int? placementTabModuleId = null,
        CancellationToken cancellationToken = default)
    {
        if (!Enum.IsDefined(permissionKey))
        {
            return Result<bool>.Failure(
                KeyInvalidCode,
                FormattableString.Invariant($"Permission key {(int)permissionKey} is not defined."));
        }

        Module? module = await _modules
            .GetByIdAsync(moduleId, cancellationToken)
            .ConfigureAwait(false);

        if (module is null)
        {
            return Result<bool>.Failure(
                ModuleNotFoundCode,
                FormattableString.Invariant($"Module {moduleId} does not exist."));
        }

        if (!BelongsToPortal(module.PortalId, portalId))
        {
            return Result<bool>.Failure(
                ModuleForeignTenantCode,
                FormattableString.Invariant($"Module {moduleId} does not belong to portal {portalId}."));
        }

        IReadOnlyList<TabModule>? placements = null;

        if (placementTabId is int namedTabId && placementTabModuleId is int namedTabModuleId)
        {
            placements = await ReadPlacementsAsync(module, cancellationToken).ConfigureAwait(false);

            if (AddressesDisagree(placements, namedTabId, namedTabModuleId))
            {
                return Result<bool>.Success(false);
            }
        }

        CallerIdentity caller = await ResolveCallerAsync(portalId, userId, cancellationToken)
            .ConfigureAwait(false);

        if (!caller.Found)
        {
            return Result<bool>.Success(false);
        }

        if (caller.IsSuperUser)
        {
            return Result<bool>.Success(true);
        }

        if (permissionKey == PermissionKey.VIEW && module.InheritViewPermissions == true)
        {
            // Reuses the set the reconciliation above already read when both addresses were named, and
            // reads it here only when they were not.
            placements ??= await ReadPlacementsAsync(module, cancellationToken).ConfigureAwait(false);

            bool inherited = await InheritedViewGrantedAsync(
                    placements,
                    placementTabId,
                    placementTabModuleId,
                    userId,
                    caller.RoleNames,
                    cancellationToken)
                .ConfigureAwait(false);

            return Result<bool>.Success(inherited);
        }

        Result<bool> granted = await _evaluator
            .HasModulePermissionAsync(moduleId, permissionKey, userId, caller.RoleNames, cancellationToken)
            .ConfigureAwait(false);

        if (VerdictOf(granted))
        {
            return Result<bool>.Success(true);
        }

        if (permissionKey == PermissionKey.EDIT)
        {
            if (await HoldsPortalAdministratorRoleAsync(portalId, caller.RoleNames, cancellationToken)
                .ConfigureAwait(false))
            {
                return Result<bool>.Success(true);
            }

            // Read only now that the cheap arm has declined. A shape that reconciled two named addresses
            // above already holds the set and does not read it twice.
            placements ??= await ReadPlacementsAsync(module, cancellationToken).ConfigureAwait(false);

            if (await PageEditGrantedAsync(placements, placementTabId, userId, caller.RoleNames, cancellationToken)
                .ConfigureAwait(false))
            {
                return Result<bool>.Success(true);
            }
        }

        return Result<bool>.Success(false);
    }

    /// <inheritdoc />
    public async Task<Result<bool>> HasTabPermissionAsync(
        int portalId,
        int? userId,
        int tabId,
        PermissionKey permissionKey,
        CancellationToken cancellationToken = default)
    {
        if (!Enum.IsDefined(permissionKey))
        {
            return Result<bool>.Failure(
                KeyInvalidCode,
                FormattableString.Invariant($"Permission key {(int)permissionKey} is not defined."));
        }

        Tab? tab = await _tabs.GetByIdAsync(tabId, cancellationToken).ConfigureAwait(false);
        if (tab is null)
        {
            return Result<bool>.Failure(
                TabNotFoundCode,
                FormattableString.Invariant($"Page {tabId} does not exist."));
        }

        if (!BelongsToPortal(tab.PortalId, portalId))
        {
            return Result<bool>.Failure(
                TabForeignTenantCode,
                FormattableString.Invariant($"Page {tabId} does not belong to portal {portalId}."));
        }

        CallerIdentity caller = await ResolveCallerAsync(portalId, userId, cancellationToken)
            .ConfigureAwait(false);

        if (!caller.Found)
        {
            return Result<bool>.Success(false);
        }

        if (caller.IsSuperUser)
        {
            return Result<bool>.Success(true);
        }

        // ⚠ TENANT ADMINISTRATION IS ASKED BEFORE ANY GRANT ROW, AND ITS ABSENCE HERE WAS MEASURED AS A
        // DEFECT. Legacy `PortalSecurity.HasNecessaryPermission` -
        // `Library/Components/Security/PortalSecurity.vb:L519-L548` - read `isAdmin = IsInRole(PortalSettings.
        // AdministratorRoleName)` and admitted the View, Edit AND Admin access levels on that alone,
        // IRRESPECTIVE of any permission row: a tenant's administrator administers its pages whether or not
        // a grant row happens to name their role. This member went straight to the row-based evaluator, so
        // an administrator of a portal whose page grants were never populated - the ordinary state of a
        // freshly created page - was refused both the read and the update of their own page, while the two
        // page-LISTING members beside it (`HasAnyTabPermissionInPortalAsync` and
        // `ListTabsWithPermissionAsync`) already asked this question first and answered correctly. One
        // installation therefore reported that the administrator may act on a page in the listing and then
        // refused the page itself.
        //
        // The already-resolved role names are reused rather than re-reading the account and its assignments:
        // `ResolveCallerAsync` above resolved them AS OF NOW, so an assignment whose validity window has
        // closed is already absent, and the name comparison is the same one the legacy test performed. This
        // is the identical arm the module EDIT path uses, for the identical reason.
        //
        // ⚠ THE AUTHORITY IS OVER THE TENANT'S OWN PAGES, AND `BelongsToPortal` ABOVE IS DELIBERATELY WIDER
        // THAN THAT. That test admits a page bearing NO tenant, because a host-level page is in scope for
        // every portal - which is what makes the host administration pages addressable from inside one. But
        // a page belonging to no tenant is not a page this tenant's administrator administers, and the
        // suite records that as a deliberate divergence from the legacy ownership test, which accepted
        // `Tabs.PortalId is null` unconditionally. So the ownership required HERE is exact equality, and a
        // host-level page falls through to the grant rows exactly as before.
        //
        // AN ANONYMOUS CALLER IS EXCLUDED BEFORE THE NAME COMPARISON IS EVEN REACHED. Its resolved names are
        // the two pseudo-roles rather than assignments, so the comparison could only ever match through an
        // installation that had renamed its administrators role to a pseudo-role's wording - and admitting
        // an unauthenticated caller on a naming coincidence is not a risk worth carrying for two reads this
        // branch does not need.
        if (userId is not null
            && tab.PortalId == portalId
            && await HoldsPortalAdministratorRoleAsync(portalId, caller.RoleNames, cancellationToken)
                .ConfigureAwait(false))
        {
            return Result<bool>.Success(true);
        }

        Result<bool> granted = await _evaluator
            .HasTabPermissionAsync(tabId, permissionKey, userId, caller.RoleNames, cancellationToken)
            .ConfigureAwait(false);

        return Result<bool>.Success(VerdictOf(granted));
    }

    /// <inheritdoc />
    public async Task<Result> DeleteUserPermissionsAsync(
        int portalId,
        int userId,
        CancellationToken cancellationToken = default)
    {
        Result staged;

        await using (ITransactionScope transaction = await _unitOfWork
            .BeginTransactionAsync(TransactionIsolation.Default, cancellationToken)
            .ConfigureAwait(false))
        {
            staged = await StageUserPermissionRemovalAsync(portalId, userId, cancellationToken)
                .ConfigureAwait(false);

            if (staged.IsFailure)
            {
                // Both guards run ahead of both deletes, so a refusal has issued no statement at all and
                // the scope is abandoned without having changed anything.
                return staged;
            }

            await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }

        // Ordered after the commit, deliberately, and delegated to the member the enclosing-operation
        // caller also uses, so the two paths cannot evict different things.
        InvalidateUserPermissionCaches();

        return Result.Success();
    }

    /// <inheritdoc />
    /// <remarks>
    /// Only grants made directly to the account are removed. Grants the account receives through a role
    /// belong to the role rather than to the account, so removing them would strip every other holder of
    /// that role as well.
    /// </remarks>
    public async Task<Result> StageUserPermissionRemovalAsync(
        int portalId,
        int userId,
        CancellationToken cancellationToken = default)
    {
        if (!await _portals.ExistsAsync(portalId, cancellationToken).ConfigureAwait(false))
        {
            return Result.Failure(
                PortalNotFoundCode,
                FormattableString.Invariant($"Portal {portalId} does not exist."));
        }

        CallerIdentity caller = await ResolveCallerAsync(portalId, userId, cancellationToken)
            .ConfigureAwait(false);

        if (!caller.Found)
        {
            return Result.Failure(
                UserNotFoundCode,
                FormattableString.Invariant($"Account {userId} does not exist in portal {portalId}."));
        }

        // Both guards are ahead of both removals, so a refusal leaves the store and the change tracker
        // exactly as they were and the caller's own unit of work is unaffected by having asked.

        await using ITransactionScope grants = await _unitOfWork
            .JoinOrBeginTransactionAsync(TransactionIsolation.Default, cancellationToken)
            .ConfigureAwait(false);

        await _permissions.DeleteModulePermissionsByUserIdAsync(portalId, userId, cancellationToken)
            .ConfigureAwait(false);

        await _permissions.DeleteTabPermissionsByUserIdAsync(portalId, userId, cancellationToken)
            .ConfigureAwait(false);

        await grants.CommitAsync(cancellationToken).ConfigureAwait(false);

        return Result.Success();
    }

    /// <inheritdoc />
    /// <remarks>
    /// Only grants ADDRESSED TO the role are removed, and the rule lives in one place - the repository
    /// members compare the grant's role column to this one identifier. A grant addressed to an account is
    /// the account's own and belongs to the account cleanup; a grant addressed to one of the negative
    /// pseudo-principals names no role row at all, so no role removal can be the reason to discard it.
    /// </remarks>
    public async Task<Result> StageRolePermissionRemovalAsync(
        int portalId,
        int roleId,
        CancellationToken cancellationToken = default)
    {
        if (!await _portals.ExistsAsync(portalId, cancellationToken).ConfigureAwait(false))
        {
            return Result.Failure(
                PortalNotFoundCode,
                FormattableString.Invariant($"Portal {portalId} does not exist."));
        }

        Role? role = await _roles.GetByIdAsync(roleId, portalId, cancellationToken).ConfigureAwait(false);

        if (role is null)
        {
            return Result.Failure(
                RoleNotFoundCode,
                FormattableString.Invariant($"Portal {portalId} has no role bearing identifier {roleId}."));
        }

        await _permissions.DeleteFolderPermissionsByRoleIdAsync(roleId, cancellationToken)
            .ConfigureAwait(false);

        await _permissions.DeleteModulePermissionsByRoleIdAsync(roleId, cancellationToken)
            .ConfigureAwait(false);

        await _permissions.DeleteTabPermissionsByRoleIdAsync(roleId, cancellationToken)
            .ConfigureAwait(false);

        return Result.Success();
    }

    /// <inheritdoc />
    public void InvalidateUserPermissionCaches()
    {
        // What it evicts now is exactly what it writes. Both members are synchronous, in-memory and
        // infallible, which is why this method no longer takes a cancellation token or returns a task: a
        // post-commit step must not be able to fail.
        _cache.Remove(TabDefinitionsCacheKey);
        _cache.RemoveByPrefix(ModuleDefinitionsCacheKeyPrefix);
    }

    /// <summary>
    /// Reads the catalogue definitions matching an optional scope code, an optional module definition and
    /// an optional key.
    /// </summary>
    /// <param name="permissionCode">The scope code filter, or <see langword="null"/> for no restriction.</param>
    /// <param name="moduleDefinitionId">
    /// The module definition filter, or <see langword="null"/> for no restriction.
    /// </param>
    /// <param name="permissionKey">The key filter, or <see langword="null"/> for no restriction.</param>
    /// <param name="cancellationToken">Token observed for cancellation.</param>
    /// <returns>
    /// The rows the catalogue reports for that combination, unprojected; the caller applies distinctness and
    /// ordering.
    /// </returns>
    /// <remarks>
    /// <para>
    /// <b>ONE READ PER SHAPE, AND EVERY SHAPE READS THE STORE.</b> Naming a module definition still takes
    /// the definition-scoped reader, because that is the narrowest read that answers it and the store should
    /// do the narrowing it can. Every other shape takes the catalogue-wide reader and narrows in memory
    /// over a table of bounded reference data.
    /// </para>
    /// <para>
    /// <b>What this replaced, and why.</b> The unscoped shape used to be answered from
    /// <see cref="PermissionKey"/> itself with no store read at all, and the code-scoped shape used to probe
    /// the code-and-key reader once per enumeration member. Both were bounded by this solution's vocabulary
    /// rather than by the data, so a definition registered under any other spelling was invisible to them
    /// while remaining perfectly visible to the definition-scoped shape, to the module permission matrices
    /// and to the evaluator. Two answers to one question is not a narrowing, it is a contradiction, and the
    /// probe additionally cost four round trips to answer less than one does.
    /// </para>
    /// </remarks>
    private async Task<IReadOnlyList<Permission>> ReadCatalogueAsync(
        string? permissionCode,
        int? moduleDefinitionId,
        PermissionKey? permissionKey,
        CancellationToken cancellationToken)
    {
        string? wantedCode = permissionCode?.Trim();

        IReadOnlyList<Permission> rows = moduleDefinitionId is int definitionId
            ? await _permissions
                .GetByModuleDefinitionIdAsync(definitionId, cancellationToken)
                .ConfigureAwait(false)
            : await _permissions
                .GetCatalogueAsync(cancellationToken)
                .ConfigureAwait(false);

        IEnumerable<Permission> matching = rows;

        if (wantedCode is not null)
        {
            matching = matching.Where(entry => string.Equals(
                entry.PermissionCode,
                wantedCode,
                StringComparison.OrdinalIgnoreCase));
        }

        if (permissionKey is PermissionKey wantedKey)
        {
            // The FILTER is a named member, but the comparison is against the STORED spelling and is
            // case-insensitive, so a row holding "edit" is matched by a filter of EDIT. It is the same
            // comparison the evaluator applies when it decides whether a grant names a key.
            string wantedSpelling = wantedKey.ToString();

            matching = matching.Where(entry => string.Equals(
                entry.PermissionKey,
                wantedSpelling,
                StringComparison.OrdinalIgnoreCase));
        }

        return matching.ToList();
    }

    /// <summary>Decides whether the two forms of addressing a module placement contradict each other.</summary>
    /// <param name="placements">The module's placements, already read by the caller.</param>
    /// <param name="placementTabId">The page the caller named.</param>
    /// <param name="placementTabModuleId">The placement the caller named by its own key.</param>
    /// <returns>
    /// <see langword="true"/> when the named placement does not sit on the named page, which is the
    /// contradiction the contract refuses.
    /// </returns>
    private static bool AddressesDisagree(
        IReadOnlyList<TabModule> placements,
        int placementTabId,
        int placementTabModuleId)
    {
        TabModule? addressed = placements
            .FirstOrDefault(placement => placement.TabModuleId == placementTabModuleId);

        return addressed is not null && addressed.TabId != placementTabId;
    }

    /// <summary>Reads a module's placements, preferring the collection the entity already carries.</summary>
    /// <param name="module">The module whose placements are wanted.</param>
    /// <param name="cancellationToken">Token observed for cancellation.</param>
    /// <returns>The placements, which may legitimately be empty for a module that sits on no page.</returns>
    /// <remarks>
    /// Named once and shared by the two members that need placements, so the "loaded collection first,
    /// store second" rule cannot drift between them. An empty collection on the entity is treated as "not
    /// loaded" rather than as "no placements", which is the same reading the read it replaced took: the
    /// store is then asked, and it is the store's empty answer that means the module sits on no page.
    /// </remarks>
    private async Task<IReadOnlyList<TabModule>> ReadPlacementsAsync(
        Module module,
        CancellationToken cancellationToken)
        => module.TabModules.Count > 0
            ? module.TabModules.ToList()
            : await _modules
                .GetTabModulesByModuleIdAsync(module.ModuleId, cancellationToken)
                .ConfigureAwait(false);

    /// <summary>
    /// Decides whether a module configured to inherit its view permission is viewable by the caller, at the
    /// placement the caller addressed or - when none was addressed - at every placement it occupies.
    /// </summary>
    /// <param name="placements">The module's placements, already read by the caller.</param>
    /// <param name="placementTabId">
    /// The page the module is being addressed on, or <see langword="null"/> when the caller named none.
    /// </param>
    /// <param name="placementTabModuleId">
    /// The placement being addressed, named by its own key, or <see langword="null"/> when the caller named
    /// none.
    /// </param>
    /// <param name="userId">The caller, or <see langword="null"/> when anonymous.</param>
    /// <param name="roleNames">The role names the caller holds.</param>
    /// <param name="cancellationToken">Token observed for cancellation.</param>
    /// <returns>
    /// When a placement was addressed: whether its page grants the view key, and <see langword="false"/>
    /// when the module does not occupy that placement or the two forms of address contradict each other.
    /// </returns>
    /// <remarks>
    /// A MODULE THAT SITS ON NO PAGE IS NOT VIEWABLE. The conjunction over an empty set is vacuously true,
    /// so the empty case is stated rather than left to the loop - a module that inherits its view
    /// permission from its pages and has no pages inherits nothing, and must not thereby become visible to
    /// everyone.
    /// </remarks>
    private async Task<bool> InheritedViewGrantedAsync(
        IReadOnlyList<TabModule> placements,
        int? placementTabId,
        int? placementTabModuleId,
        int? userId,
        IReadOnlyCollection<string> roleNames,
        CancellationToken cancellationToken)
    {
        if (placementTabModuleId is int addressedTabModuleId)
        {
            // Addressed by the placement's own key, which is the precise form: a module may be placed on one
            // page more than once, so the page identifier alone cannot always name a single placement.
            TabModule? addressed = placements
                .FirstOrDefault(placement => placement.TabModuleId == addressedTabModuleId);

            if (addressed is null)
            {
                return false;
            }

            Result<bool> addressedPlacementGrant = await _evaluator
                .HasTabPermissionAsync(
                    addressed.TabId,
                    PermissionKey.VIEW,
                    userId,
                    roleNames,
                    cancellationToken)
                .ConfigureAwait(false);

            return VerdictOf(addressedPlacementGrant);
        }

        if (placementTabId is int addressedTabId)
        {
            // Both -1 and 0 are genuine identifiers in this schema, so this is a real comparison rather
            // than a sentinel test.
            if (!placements.Any(placement => placement.TabId == addressedTabId))
            {
                return false;
            }

            Result<bool> addressedPageGrant = await _evaluator
                .HasTabPermissionAsync(
                    addressedTabId,
                    PermissionKey.VIEW,
                    userId,
                    roleNames,
                    cancellationToken)
                .ConfigureAwait(false);

            return VerdictOf(addressedPageGrant);
        }

        // Stated rather than left to the loop below, whose conjunction over an empty set would be true.
        if (placements.Count == 0)
        {
            return false;
        }

        foreach (TabModule placement in placements)
        {
            Result<bool> granted = await _evaluator
                .HasTabPermissionAsync(
                    placement.TabId,
                    PermissionKey.VIEW,
                    userId,
                    roleNames,
                    cancellationToken)
                .ConfigureAwait(false);

            if (!VerdictOf(granted))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Reports whether the caller holds the edit grant on the page a module is being administered from.
    /// </summary>
    /// <param name="placements">The module's placements.</param>
    /// <param name="addressedTabId">The page the request named, or <see langword="null"/> when it named none.</param>
    /// <param name="userId">The caller, or <see langword="null"/> when anonymous.</param>
    /// <param name="roleNames">The caller's role names, including the applicable pseudo-roles.</param>
    /// <param name="cancellationToken">Token observed for cancellation.</param>
    /// <returns><see langword="true"/> when a qualifying page grants edit to the caller.</returns>
    /// <remarks>
    /// The quantifier differs from the inherited-view rule on purpose, and the difference follows the
    /// legacy meaning of each. Inherited VIEW asks whether the module is visible wherever it is placed, so
    /// every placement must grant it - a conjunction.
    /// </remarks>
    private async Task<bool> PageEditGrantedAsync(
        IReadOnlyList<TabModule> placements,
        int? addressedTabId,
        int? userId,
        IReadOnlyList<string> roleNames,
        CancellationToken cancellationToken)
    {
        if (addressedTabId is int namedTabId)
        {
            // Both -1 and 0 are genuine identifiers in this schema, so this is a real comparison rather than
            // a sentinel test.
            if (!placements.Any(placement => placement.TabId == namedTabId))
            {
                return false;
            }

            Result<bool> namedPageGrant = await _evaluator
                .HasTabPermissionAsync(namedTabId, PermissionKey.EDIT, userId, roleNames, cancellationToken)
                .ConfigureAwait(false);

            return VerdictOf(namedPageGrant);
        }

        // The unaddressed shape asks about EVERY page the module sits on, and it asks in ONE call rather
        // than once per page. Asking per page cost a fixed read set per page, so a module placed across a
        // tenant's page tree made a single authorisation check proportional to that tree.
        IReadOnlyCollection<int> placementTabIds = placements
            .Select(placement => placement.TabId)
            .Distinct()
            .ToList();

        Result<bool> anyPageGrant = await _evaluator
            .HasAnyTabPermissionAsync(placementTabIds, PermissionKey.EDIT, userId, roleNames, cancellationToken)
            .ConfigureAwait(false);

        return VerdictOf(anyPageGrant);
    }

    /// <summary>Reports whether the caller holds the addressed tenant's own administrators role.</summary>
    /// <param name="portalId">The portal the question is asked within.</param>
    /// <param name="roleNames">The caller's role names, including the applicable pseudo-roles.</param>
    /// <param name="cancellationToken">Token observed for cancellation.</param>
    /// <returns><see langword="true"/> when the caller is one of the tenant's administrators.</returns>
    /// <remarks>
    /// The comparison is by NAME rather than by identifier because that is what the legacy test compared
    /// and because the caller's identity is carried as role names throughout this service. It is ordinal
    /// and case-insensitive, matching <c>PortalSecurity.IsInRoles</c>, which compared with the Visual Basic
    /// string equality operator under the file's default (text-insensitive) comparison.
    /// </remarks>
    private async Task<bool> HoldsPortalAdministratorRoleAsync(
        int portalId,
        IReadOnlyList<string> roleNames,
        CancellationToken cancellationToken)
    {
        if (roleNames.Count == 0)
        {
            return false;
        }

        Portal? portal = await _portals
            .GetByIdAsync(portalId, includeAliases: false, cancellationToken)
            .ConfigureAwait(false);

        if (portal?.AdministratorRoleId is not int administratorRoleId)
        {
            return false;
        }

        Role? administratorsRole = await _roles
            .GetByIdAsync(administratorRoleId, portalId, cancellationToken)
            .ConfigureAwait(false);

        if (administratorsRole is null || string.IsNullOrWhiteSpace(administratorsRole.RoleName))
        {
            return false;
        }

        return roleNames.Any(name =>
            string.Equals(name, administratorsRole.RoleName, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Resolves a caller into the role names the evaluator must consider.</summary>
    /// <param name="portalId">The portal the question is asked within.</param>
    /// <param name="userId">The caller, or <see langword="null"/> when anonymous.</param>
    /// <param name="cancellationToken">Token observed for cancellation.</param>
    /// <returns>Whether the caller was found, whether it is a host account, and its role names.</returns>
    /// <remarks>
    /// A host account is accepted even when it holds no membership of the portal, because a host account is
    /// installation-wide and the legacy test short-circuited on it before examining any role.
    /// </remarks>
    private async Task<CallerIdentity> ResolveCallerAsync(
        int portalId,
        int? userId,
        CancellationToken cancellationToken)
    {
        if (userId is not int callerId)
        {
            return new CallerIdentity(
                Found: true,
                IsSuperUser: false,
                RoleNames: [SpecialRoleNames.AllUsers, SpecialRoleNames.Unauthenticated]);
        }

        User? account = await _users.GetAsync(portalId, callerId, cancellationToken).ConfigureAwait(false);
        if (account is null)
        {
            account = await _users.GetAsync(portalId: null, callerId, cancellationToken).ConfigureAwait(false);
            if (account is null || !account.IsSuperUser)
            {
                return new CallerIdentity(Found: false, IsSuperUser: false, RoleNames: []);
            }
        }

        if (account.IsSuperUser)
        {
            return new CallerIdentity(Found: true, IsSuperUser: true, RoleNames: []);
        }

        IReadOnlyList<string> assigned = await _users
            .ListRoleNamesAsync(portalId, callerId, _clock.UtcNow, cancellationToken)
            .ConfigureAwait(false);

        var roleNames = new List<string>(assigned.Count + 1);
        roleNames.AddRange(assigned);
        roleNames.Add(SpecialRoleNames.AllUsers);

        return new CallerIdentity(Found: true, IsSuperUser: false, RoleNames: roleNames);
    }

    /// <summary>Tests whether an entity whose portal is optional belongs to the portal in question.</summary>
    /// <param name="owningPortalId">The portal recorded on the entity, absent for a host-level one.</param>
    /// <param name="portalId">The portal the question is asked within.</param>
    /// <returns><see langword="true"/> when the entity is in scope.</returns>
    /// <remarks>
    /// A host-level module or page carries no portal, and is in scope for every portal, which is what makes
    /// the host administration pages reachable from within a portal. Both zero and minus one are genuine
    /// identifiers in this schema, so the comparison is a real comparison rather than a sentinel test.
    /// </remarks>
    private static bool BelongsToPortal(int? owningPortalId, int portalId)
        => owningPortalId is null || owningPortalId.Value == portalId;

    /// <summary>
    /// Reads an evaluator verdict without letting an unanswerable question become a thrown exception.
    /// </summary>
    /// <param name="verdict">The outcome the evaluator reported.</param>
    /// <returns>The verdict when the evaluator answered, and <see langword="false"/> when it could not.</returns>
    /// <remarks>
    /// Reading <c>Value</c> on a failed outcome throws, so every verdict this service consumes is read
    /// through here instead.
    /// </remarks>
    private static bool VerdictOf(Result<bool> verdict) => verdict.IsSuccess && verdict.Value;

    /// <summary>
    /// Reads an evaluator key listing without letting an unanswerable question become a thrown exception.
    /// </summary>
    /// <param name="listing">The outcome the evaluator reported.</param>
    /// <returns>
    /// The keys when the evaluator answered, and an empty sequence when it could not - the same closed
    /// default a caller holding no reachable grant receives.
    /// </returns>
    private static IReadOnlyList<string> KeysOf(Result<IReadOnlyList<string>> listing)
        => listing.IsSuccess ? listing.Value : [];

    /// <summary>Reduces a key sequence to the distinct, upper-cased keys in a stable order.</summary>
    /// <param name="keys">The keys to reduce.</param>
    /// <returns>The normalised keys.</returns>
    /// <remarks>
    /// The repository already upper-cases what it returns; normalising again here costs nothing and makes
    /// the guarantee hold for the catalogue projection too, whose column is stored as authored.
    /// </remarks>
    private static IReadOnlyList<string> Normalise(IEnumerable<string> keys)
        => keys
            .Where(key => !string.IsNullOrWhiteSpace(key))
            .Select(key => key.Trim().ToUpperInvariant())
            .Distinct(StringComparer.Ordinal)
            .OrderBy(key => key, StringComparer.Ordinal)
            .ToList();

    /// <inheritdoc />
    public async Task<Result<PermissionDto?>> GetPermissionAsync(
        int permissionId,
        CancellationToken cancellationToken = default)
    {
        Permission? definition = await _permissions
            .GetByIdAsync(permissionId, cancellationToken)
            .ConfigureAwait(false);

        // No bound test on the identifier, here or anywhere in this solution: an unknown identifier is
        // reported as absent by the read itself, and a bound would be a second, weaker copy of that answer.
        return Result<PermissionDto?>.Success(definition is null ? null : ToDto(definition));
    }

    /// <inheritdoc />
    /// <remarks>
    /// The module is resolved first, and it is resolved twice over: for the tenant check and then for the
    /// cache. a module that does not exist, or exists in another portal, is refused with
    /// <c>module.not_found</c> before the cache is consulted, so no entry can be reached through a
    /// neighbouring tenant's identifier.
    /// </remarks>
    public async Task<Result<IReadOnlyList<PermissionDto>>> GetModulePermissionDefinitionsAsync(
        int portalId,
        int moduleId,
        CancellationToken cancellationToken = default)
    {
        Module? module = await _modules
            .GetByIdAsync(moduleId, cancellationToken)
            .ConfigureAwait(false);

        if (module is null || !BelongsToPortal(module.PortalId, portalId))
        {
            return Result<IReadOnlyList<PermissionDto>>.Failure(
                ModuleNotFoundCode,
                FormattableString.Invariant($"Module {moduleId} does not exist in portal {portalId}."));
        }

        // the key space in one step.
        string cacheKey = string.Format(
            CultureInfo.InvariantCulture,
            ModuleDefinitionsCacheKeyFormat,
            module.ModuleDefinitionId);

        IReadOnlyList<PermissionDto> definitions = await ReadThroughCacheAsync(
                cacheKey,
                async token => Project(
                    await _permissions.GetByModuleIdAsync(moduleId, token).ConfigureAwait(false)),
                cancellationToken)
            .ConfigureAwait(false);

        return Result<IReadOnlyList<PermissionDto>>.Success(definitions);
    }

    /// <inheritdoc />
    /// <remarks>
    /// The page is resolved first and for the tenant check alone - so a page that does not exist, or exists
    /// in another portal, is refused with <c>tab.not_found</c> rather than answered.
    /// </remarks>
    public async Task<Result<IReadOnlyList<PermissionDto>>> GetTabPermissionDefinitionsAsync(
        int portalId,
        int tabId,
        CancellationToken cancellationToken = default)
    {
        Tab? tab = await _tabs
            .GetByIdAsync(tabId, cancellationToken)
            .ConfigureAwait(false);

        if (tab is null || !BelongsToPortal(tab.PortalId, portalId))
        {
            return Result<IReadOnlyList<PermissionDto>>.Failure(
                TabNotFoundCode,
                FormattableString.Invariant($"Page {tabId} does not exist in portal {portalId}."));
        }
        IReadOnlyList<PermissionDto> definitions = await ReadThroughCacheAsync(
                TabDefinitionsCacheKey,
                async token => Project(
                    await _permissions.GetByTabIdAsync(tabId, token).ConfigureAwait(false)),
                cancellationToken)
            .ConfigureAwait(false);

        return Result<IReadOnlyList<PermissionDto>>.Success(definitions);
    }

    /// <inheritdoc />
    public async Task<Result<ModulePermissionsDto>> GetModulePermissionsAsync(
        int portalId,
        int moduleId,
        CancellationToken cancellationToken = default)
    {
        Module? module = await _modules
            .GetByIdAsync(moduleId, cancellationToken)
            .ConfigureAwait(false);

        // ⚠ A RECYCLED MODULE IS ABSENT HERE, AND ADMITTING ONE WAS A MEASURED INCONSISTENCY. The detail and
        // settings reads select a module through a live placement, so they report a recycled module as absent;
        // this read resolves the row directly and so used to answer 200 for the same identifier - one module,
        // two contradictory answers about whether it exists. One existence rule is used everywhere: a module
        // is addressable while it lives in this tenant and is not recycled.
        if (module is null || !BelongsToPortal(module.PortalId, portalId) || module.IsDeleted)
        {
            return Result<ModulePermissionsDto>.Failure(
                ModuleNotFoundCode,
                FormattableString.Invariant($"Module {moduleId} does not exist in portal {portalId}."));
        }

        // The columns. Read through the same cache the definition catalogue read uses, so the grid and the
        // chip list above it can never disagree about which keys this definition declares.
        string cacheKey = string.Format(
            CultureInfo.InvariantCulture,
            ModuleDefinitionsCacheKeyFormat,
            module.ModuleDefinitionId);

        IReadOnlyList<PermissionDto> catalogue = await ReadThroughCacheAsync(
                cacheKey,
                async token => Project(
                    await _permissions.GetByModuleIdAsync(moduleId, token).ConfigureAwait(false)),
                cancellationToken)
            .ConfigureAwait(false);

        List<ModulePermissionDefinitionDto> definitions = catalogue
            .Select(entry => new ModulePermissionDefinitionDto
            {
                PermissionId = entry.PermissionId,
                PermissionKey = entry.PermissionKey,
                PermissionName = entry.PermissionName,
            })
            .ToList();

        IReadOnlyList<ModulePermission> grants = await _permissions
            .GetModulePermissionsByModuleIdAsync(moduleId, AnyPermissionId, cancellationToken)
            .ConfigureAwait(false);

        // The administrator row is identified from the PORTAL, never from a role name: RoleName is data an
        // installation may rename, whereas Portals.AdministratorRoleId is the column the legacy grid itself
        // compared against.
        Portal? portal = await _portals
            .GetByIdAsync(portalId, includeAliases: false, cancellationToken)
            .ConfigureAwait(false);

        int? administratorRoleId = portal?.AdministratorRoleId;

        // Modules.InheritViewPermissions is bit NULL, and an unset column is NOT inheritance: the legacy
        // reader mapped DBNull to Null.NullBoolean, which is False. Resolved once here so the row builders
        // cannot each answer it differently.
        bool inheritsView = module.InheritViewPermissions ?? false;

        List<ModulePermissionRoleDto> roles = await BuildPermissionRoleRowsAsync(
                portalId,
                inheritsView,
                definitions,
                grants,
                administratorRoleId,
                cancellationToken)
            .ConfigureAwait(false);

        List<ModulePermissionUserDto> users = await BuildPermissionUserRowsAsync(
                portalId,
                inheritsView,
                definitions,
                grants,
                cancellationToken)
            .ConfigureAwait(false);

        return Result<ModulePermissionsDto>.Success(new ModulePermissionsDto
        {
            ModuleId = module.ModuleId,
            InheritViewPermissions = inheritsView,
            InheritedPermissionKey = definitions
                .Any(definition => IsViewKey(definition.PermissionKey))
                    ? ViewPermissionKey
                    : null,
            Definitions = definitions,
            Roles = roles,
            Users = users,
        });
    }

    /// <inheritdoc />
    public async Task<Result> ReplaceModulePermissionsAsync(
        int portalId,
        int moduleId,
        ReplaceModulePermissionsRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request is null)
        {
            return Result.Failure(
                RequestInvalidCode,
                "A permission replacement request is required and was not supplied.");
        }

        IReadOnlyList<ModulePermissionGrantRequest> submitted =
            request.Grants ?? Array.Empty<ModulePermissionGrantRequest>();

        if (submitted.Count > GrantsPerModuleMaximum)
        {
            return Result.Failure(
                RequestInvalidCode,
                FormattableString.Invariant(
                    $"A module may carry no more than {GrantsPerModuleMaximum} grants."));
        }

        Module? module = await _modules
            .GetByIdAsync(moduleId, cancellationToken)
            .ConfigureAwait(false);

        // Recycled is absent, for the reason recorded on the read above: one existence rule, applied by every
        // endpoint addressing a module.
        if (module is null || !BelongsToPortal(module.PortalId, portalId) || module.IsDeleted)
        {
            return Result.Failure(
                ModuleNotFoundCode,
                FormattableString.Invariant($"Module {moduleId} does not exist in portal {portalId}."));
        }

        // EVERY REFERENCE IS RESOLVED BEFORE ANY STATEMENT IS ISSUED. The write is a delete-then-insert, so
        // a reference discovered to be invalid halfway through would leave the module with no grants at all
        // - which is precisely the failure mode a permission surface must not have.
        IReadOnlyList<Permission> declared = await _permissions
            .GetByModuleIdAsync(moduleId, cancellationToken)
            .ConfigureAwait(false);

        HashSet<int> declaredIds = declared.Select(entry => entry.PermissionId).ToHashSet();

        // The stored key is FREE TEXT, not a member of the closed vocabulary - the column is `varchar(50)`
        // and a site may hold a key this application has never heard of - so the lookup carries the text and
        // the comparison goes through IsViewKey, exactly as every other view-inheritance decision here does.
        Dictionary<int, string> declaredKeys = declared
            .GroupBy(entry => entry.PermissionId)
            .ToDictionary(group => group.Key, group => group.First().PermissionKey);

        IReadOnlyList<Role> portalRoles = await _roles
            .GetByPortalIdAsync(portalId, cancellationToken)
            .ConfigureAwait(false);

        HashSet<int> assignableRoleIds = portalRoles.Select(role => role.RoleId).ToHashSet();
        assignableRoleIds.Add(SpecialRoleIds.AllUsers);
        assignableRoleIds.Add(SpecialRoleIds.Unauthenticated);

        var accepted = new List<ModulePermission>(submitted.Count);
        var seen = new HashSet<(int PermissionId, int? RoleId, int? UserId)>();

        foreach (ModulePermissionGrantRequest grant in submitted)
        {
            if (!declaredIds.Contains(grant.PermissionId))
            {
                return Result.Failure(
                    KeyInvalidCode,
                    FormattableString.Invariant(
                        $"Permission {grant.PermissionId} is not declared by module {moduleId}."));
            }

            bool namesRole = grant.RoleId is not null;
            bool namesUser = grant.UserId is not null;

            if (namesRole == namesUser)
            {
                return Result.Failure(
                    RequestInvalidCode,
                    "Each grant must name exactly one of a role or an account.");
            }

            if (grant.RoleId is int roleId)
            {
                if (roleId == SpecialRoleIds.SuperUser)
                {
                    // Refused rather than silently dropped. The host pseudo-role matches nobody during
                    // evaluation, so storing a grant against it would be a row that can never take effect,
                    // and an operator who ticked it would be told nothing.
                    return Result.Failure(
                        RoleNotFoundCode,
                        "The host pseudo-role cannot hold a module grant.");
                }

                if (!assignableRoleIds.Contains(roleId))
                {
                    return Result.Failure(
                        RoleNotFoundCode,
                        FormattableString.Invariant(
                            $"Portal {portalId} has no role bearing identifier {roleId}."));
                }
            }

            if (grant.UserId is int userId)
            {
                User? account = await _users
                    .GetAsync(portalId, userId, cancellationToken)
                    .ConfigureAwait(false);

                if (account is null)
                {
                    return Result.Failure(
                        UserNotFoundCode,
                        FormattableString.Invariant(
                            $"Account {userId} does not exist in portal {portalId}."));
                }
            }

            // A repeated cell is a contradiction rather than a duplicate: the same principal cannot both
            // hold and not hold one permission, and the stored table has no ordering that would let a
            // "last one wins" rule be defensible.
            if (!seen.Add((grant.PermissionId, grant.RoleId, grant.UserId)))
            {
                return Result.Failure(
                    RequestInvalidCode,
                    FormattableString.Invariant(
                        $"Permission {grant.PermissionId} is named more than once for the same principal."));
            }

            // WITHHELD, NOT REFUSED. The legacy grid rendered a view cell as cleared and disabled while
            // inheritance was on, and its save path then removed the row - so a submission that carries a
            // view grant alongside inheritance is describing a state the legacy screen could not express.
            // Dropping the grant reproduces the legacy outcome exactly; refusing the request would make the
            // screen unusable, because the operator's tick and the switch are saved together.
            if (request.InheritViewPermissions
                && declaredKeys.TryGetValue(grant.PermissionId, out string? declaredKey)
                && IsViewKey(declaredKey))
            {
                continue;
            }

            accepted.Add(new ModulePermission
            {
                ModuleId = moduleId,
                PermissionId = grant.PermissionId,
                RoleId = grant.RoleId,
                UserId = grant.UserId,
                AllowAccess = grant.AllowAccess,
            });
        }

        await using (ITransactionScope transaction = await _unitOfWork
            .BeginTransactionAsync(TransactionIsolation.Default, cancellationToken)
            .ConfigureAwait(false))
        {
            await _permissions.DeleteModulePermissionsByModuleIdAsync(moduleId, cancellationToken)
                .ConfigureAwait(false);

            foreach (ModulePermission grant in accepted)
            {
                await _permissions.AddModulePermissionAsync(grant, cancellationToken)
                    .ConfigureAwait(false);
            }

            // Saved in the SAME commit as the grants, because the legacy screen saved them together and
            // because the switch decides whether the view grants above mean anything: a partial outcome
            // here is a module whose stored rights contradict its stored inheritance.
            if ((module.InheritViewPermissions ?? false) != request.InheritViewPermissions)
            {
                module.InheritViewPermissions = request.InheritViewPermissions;
                await _modules.UpdateAsync(module, cancellationToken).ConfigureAwait(false);
            }

            await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }

        // Ordered after the commit. A grant change alters what every caller may do, so the evaluator's
        // cached answers are evicted through the same member every other grant-changing path uses.
        InvalidateUserPermissionCaches();

        return Result.Success();
    }

    /// <summary>Builds the role rows of a module's grant grid.</summary>
    /// <param name="portalId">The portal whose roles form the rows.</param>
    /// <param name="inheritsView">
    /// Whether the module currently inherits its view rights from its page, which collapses the view column.
    /// </param>
    /// <param name="definitions">The columns, in render order.</param>
    /// <param name="grants">Every grant recorded against the module.</param>
    /// <param name="administratorRoleId">
    /// The portal's administrator role, or <see langword="null"/> when the portal declares none.
    /// </param>
    /// <param name="cancellationToken">Token observed for cancellation.</param>
    /// <returns>The role rows, ordered case-insensitively by name.</returns>
    /// <remarks>
    /// MIGRATION: reproduces <c>PermissionsGrid.GetRoles</c> at
    /// <c>Library/Controls/DataGrids/Permissions Grids/PermissionsGrid.vb:L450-L475</c>. That method
    /// defaulted its group filter to <c>-2</c>, which took every portal role and, because the filter was
    /// negative, appended the two built-in pseudo-roles before sorting by name through <c>RoleComparer</c>.
    /// The group selector itself is not reproduced - this screen shows one module, and a filter that hides
    /// rows on a grid whose save is a REPLACE would let an operator withdraw grants they could not see.
    /// </remarks>
    private async Task<List<ModulePermissionRoleDto>> BuildPermissionRoleRowsAsync(
        int portalId,
        bool inheritsView,
        IReadOnlyList<ModulePermissionDefinitionDto> definitions,
        IReadOnlyList<ModulePermission> grants,
        int? administratorRoleId,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<Role> portalRoles = await _roles
            .GetByPortalIdAsync(portalId, cancellationToken)
            .ConfigureAwait(false);

        var rows = new List<ModulePermissionRoleDto>(portalRoles.Count + 2);

        foreach (Role role in portalRoles)
        {
            rows.Add(BuildRoleRow(
                role.RoleId,
                role.RoleName,
                isPseudoRole: false,
                isAdministrator: administratorRoleId is int adminId && adminId == role.RoleId,
                inheritsView,
                definitions,
                grants));
        }

        rows.Add(BuildRoleRow(
            SpecialRoleIds.Unauthenticated,
            SpecialRoleNames.Unauthenticated,
            isPseudoRole: true,
            isAdministrator: false,
            inheritsView,
            definitions,
            grants));

        rows.Add(BuildRoleRow(
            SpecialRoleIds.AllUsers,
            SpecialRoleNames.AllUsers,
            isPseudoRole: true,
            isAdministrator: false,
            inheritsView,
            definitions,
            grants));

        rows.Sort(static (left, right) =>
            string.Compare(left.RoleName, right.RoleName, StringComparison.OrdinalIgnoreCase));

        return rows;
    }

    /// <summary>Builds one role row of a module's grant grid.</summary>
    /// <param name="roleId">The role identifier.</param>
    /// <param name="roleName">The role's display name.</param>
    /// <param name="isPseudoRole">Whether the row is one of the two built-in pseudo-roles.</param>
    /// <param name="isAdministrator">Whether the row is the portal's administrator role.</param>
    /// <param name="inheritsView">Whether the module inherits its view rights from its page.</param>
    /// <param name="definitions">The columns, in render order.</param>
    /// <param name="grants">Every grant recorded against the module.</param>
    /// <returns>The row.</returns>
    private static ModulePermissionRoleDto BuildRoleRow(
        int roleId,
        string roleName,
        bool isPseudoRole,
        bool isAdministrator,
        bool inheritsView,
        IReadOnlyList<ModulePermissionDefinitionDto> definitions,
        IReadOnlyList<ModulePermission> grants)
    {
        var cells = new List<ModulePermissionCellDto>(definitions.Count);

        foreach (ModulePermissionDefinitionDto definition in definitions)
        {
            bool inheritedColumn = inheritsView && IsViewKey(definition.PermissionKey);

            bool allowAccess;
            bool editable;

            if (inheritedColumn)
            {
                // ModulePermissionsGrid.vb:L296-L300 and :L241-L243 - cleared AND disabled.
                allowAccess = false;
                editable = false;
            }
            else if (isAdministrator)
            {
                // ModulePermissionsGrid.vb:L303-L305 and :L243-L247 - always granted, never editable.
                allowAccess = true;
                editable = false;
            }
            else
            {
                ModulePermission? recorded = grants.FirstOrDefault(grant =>
                    grant.PermissionId == definition.PermissionId
                    && grant.UserId is null
                    && grant.RoleId == roleId);

                allowAccess = recorded?.AllowAccess ?? false;
                editable = true;
            }

            cells.Add(new ModulePermissionCellDto
            {
                PermissionId = definition.PermissionId,
                PermissionKey = definition.PermissionKey,
                AllowAccess = allowAccess,
                Editable = editable,
            });
        }

        return new ModulePermissionRoleDto
        {
            RoleId = roleId,
            RoleName = roleName,
            IsAdministrator = isAdministrator,
            IsPseudoRole = isPseudoRole,
            Cells = cells,
        };
    }

    /// <summary>Builds the account rows of a module's grant grid.</summary>
    /// <param name="portalId">The portal the accounts must belong to.</param>
    /// <param name="inheritsView">Whether the module inherits its view rights from its page.</param>
    /// <param name="definitions">The columns, in render order.</param>
    /// <param name="grants">Every grant recorded against the module.</param>
    /// <param name="cancellationToken">Token observed for cancellation.</param>
    /// <returns>The account rows, ordered case-insensitively by display name.</returns>
    /// <remarks>
    /// Only accounts a grant already names appear. An account whose row cannot be resolved - deleted since
    /// the grant was made - is omitted rather than rendered nameless, and its grants are still carried back
    /// by a subsequent read of the same module, so nothing is silently destroyed by omitting the row: the
    /// grid's replace only withdraws what the caller was shown, because the caller resubmits what it saw.
    /// </remarks>
    private async Task<List<ModulePermissionUserDto>> BuildPermissionUserRowsAsync(
        int portalId,
        bool inheritsView,
        IReadOnlyList<ModulePermissionDefinitionDto> definitions,
        IReadOnlyList<ModulePermission> grants,
        CancellationToken cancellationToken)
    {
        var rows = new List<ModulePermissionUserDto>();

        IEnumerable<int> namedAccounts = grants
            .Where(grant => grant.UserId is not null)
            .Select(grant => grant.UserId!.Value)
            .Distinct();

        foreach (int userId in namedAccounts)
        {
            User? account = await _users
                .GetAsync(portalId, userId, cancellationToken)
                .ConfigureAwait(false);

            if (account is null)
            {
                continue;
            }

            var cells = new List<ModulePermissionCellDto>(definitions.Count);

            foreach (ModulePermissionDefinitionDto definition in definitions)
            {
                bool inheritedColumn = inheritsView && IsViewKey(definition.PermissionKey);

                ModulePermission? recorded = grants.FirstOrDefault(grant =>
                    grant.PermissionId == definition.PermissionId
                    && grant.UserId == userId);

                cells.Add(new ModulePermissionCellDto
                {
                    PermissionId = definition.PermissionId,
                    PermissionKey = definition.PermissionKey,

                    // ModulePermissionsGrid.vb:L331-L340 - the account overload carries no administrator
                    // special case, so inheritance is the only thing that collapses a cell here.
                    AllowAccess = !inheritedColumn && (recorded?.AllowAccess ?? false),
                    Editable = !inheritedColumn,
                });
            }

            rows.Add(new ModulePermissionUserDto
            {
                UserId = account.UserId,
                DisplayName = string.IsNullOrWhiteSpace(account.DisplayName)
                    ? account.Username
                    : account.DisplayName,
                Cells = cells,
            });
        }

        rows.Sort(static (left, right) =>
            string.Compare(left.DisplayName, right.DisplayName, StringComparison.OrdinalIgnoreCase));

        return rows;
    }

    /// <summary>Determines whether a catalogue key is the view key whose inheritance is special-cased.</summary>
    /// <param name="permissionKey">The key as it travels across the boundary.</param>
    /// <returns><see langword="true"/> when the key is <c>VIEW</c>; otherwise <see langword="false"/>.</returns>
    private static bool IsViewKey(string permissionKey) =>
        string.Equals(permissionKey, ViewPermissionKey, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Reads a catalogue projection through the cache, or straight from the store when caching is off.
    /// </summary>
    /// <typeparam name="T">The projection type, which is always a read-only sequence.</typeparam>
    /// <param name="cacheKey">The entry's key.</param>
    /// <param name="read">Reads the projection from the store; invoked at most once per miss.</param>
    /// <param name="cancellationToken">Token observed for cancellation.</param>
    /// <returns>The projection, from the cache when one was warm and from the store otherwise.</returns>
    private async Task<T> ReadThroughCacheAsync<T>(
        string cacheKey,
        Func<CancellationToken, Task<T>> read,
        CancellationToken cancellationToken)
        where T : notnull
    {
        TimeSpan expiration = TimeSpan.FromMinutes(
            CatalogueCacheTimeOutMinutes * _caching.PerformanceMultiplier);

        if (expiration <= TimeSpan.Zero)
        {
            return await read(cancellationToken).ConfigureAwait(false);
        }

        return await _cache
            .GetOrCreateAsync(cacheKey, read, expiration, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Projects catalogue rows into their wire shape, de-duplicated and in a stable order.</summary>
    /// <param name="definitions">The rows read from the store.</param>
    /// <returns>The projection.</returns>
    /// <remarks>
    /// Distinctness is asserted rather than assumed because the widest of the three reads behind this
    /// projection is a UNION: the module-scoped read takes the entries of the module's own definition
    /// together with every entry carrying the product-wide module-definition scope code, and a definition
    /// that satisfies both arms is one definition, not two.
    /// </remarks>
    private static IReadOnlyList<PermissionDto> Project(IEnumerable<Permission> definitions)
        => definitions
            .GroupBy(definition => definition.PermissionId)
            .Select(group => group.First())
            .OrderBy(definition => definition.PermissionId)
            .Select(ToDto)
            .ToList();

    /// <summary>Projects one catalogue row into its wire shape.</summary>
    /// <param name="definition">The row to project.</param>
    /// <returns>The projection.</returns>
    /// <remarks>
    /// The key travels VERBATIM - exactly the spelling the column holds, neither re-cased nor validated
    /// against the enumeration this solution writes its own policies against. That is what lets a
    /// definition registered by a third-party module be read back as itself, and it is the same treatment
    /// the neighbouring scope code has always had.
    /// </remarks>
    private static PermissionDto ToDto(Permission definition) => new()
    {
        PermissionId = definition.PermissionId,
        PermissionCode = definition.PermissionCode,
        ModuleDefId = definition.ModuleDefinitionId,
        PermissionKey = definition.PermissionKey,
        PermissionName = definition.PermissionName,
    };

    /// <summary>
    /// The outcome of resolving a caller: whether it exists, whether it is a host account, and the role
    /// names the evaluator must consider for it.
    /// </summary>
    /// <param name="Found">Whether the caller exists and is in scope.</param>
    /// <param name="IsSuperUser">Whether the caller is a host account and therefore holds everything.</param>
    /// <param name="RoleNames">The role names, including the applicable pseudo-roles.</param>
    private readonly record struct CallerIdentity(
        bool Found,
        bool IsSuperUser,
        IReadOnlyList<string> RoleNames);
}
