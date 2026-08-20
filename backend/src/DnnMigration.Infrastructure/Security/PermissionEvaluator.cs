using DnnMigration.Application.Abstractions;
using DnnMigration.Domain.Abstractions.Repositories;
using DnnMigration.Domain.Common;
using DnnMigration.Domain.Entities;
using DnnMigration.Domain.Enums;

namespace DnnMigration.Infrastructure.Security;

/// <summary>The single place in this solution where permission grants are turned into an access decision.</summary>
internal sealed class PermissionEvaluator : IPermissionEvaluator
{
    /// <summary>The role identifier that admits every caller, authenticated or not.</summary>
    /// <remarks>
    /// Aliased from <see cref="SpecialRoleIds"/> rather than restated as a literal, so that the grants this
    /// evaluator honours and the rows the module permission grid offers can never drift apart.
    /// </remarks>
    private const int AllUsersRoleId = SpecialRoleIds.AllUsers;

    /// <summary>The role identifier reserved for host accounts, which matches nobody here.</summary>
    private const int SuperUserRoleId = SpecialRoleIds.SuperUser;

    /// <summary>The role identifier that admits only callers who have not signed in.</summary>
    private const int UnauthenticatedRoleId = SpecialRoleIds.Unauthenticated;

    /// <summary>The scope code shared by the catalogue entries every module definition inherits.</summary>
    private const string ModuleDefinitionScopeCode = "SYSTEM_MODULE_DEFINITION";

    /// <summary>The scope code carried by the catalogue entries every page shares.</summary>
    private const string TabScopeCode = "SYSTEM_TAB";

    /// <summary>The value both grant readers treat as "every permission" in their permission argument.</summary>
    /// <remarks>
    /// The terminal grant procedures accept <c>-1</c> in the permission position as a wildcard, which the
    /// repository contract documents. <c>Permission.PermissionID</c> is <c>IDENTITY(1, 1)</c>, so no
    /// catalogue entry can ever carry it and the guard below can never reject a real entry.
    /// </remarks>
    private const int AnyPermissionId = -1;

    /// <summary>Advisory code reported when a module identifier names no module.</summary>
    /// <remarks>
    /// Deliberately NOT spelled with a token the API edge's status mapper screens for. It travels on a
    /// successful outcome, so it is never translated into a status code at all, and a spelling the mapper
    /// recognised would become a trap the moment some future caller propagated it onto a failure.
    /// </remarks>
    private const string UnknownModuleCode = "permission.module_unknown";

    /// <summary>Advisory code reported when a page identifier names no page.</summary>
    private const string UnknownTabCode = "permission.tab_unknown";

    private readonly IPermissionRepository _permissions;

    private readonly IRoleRepository _roles;

    private readonly IModuleRepository _modules;

    private readonly ITabRepository _tabs;

    /// <summary>Initialises a new instance of the <see cref="PermissionEvaluator"/> class.</summary>
    /// <param name="permissions">Reads the permission catalogue and the recorded grants.</param>
    /// <param name="roles">Resolves role names to identifiers within one portal.</param>
    /// <param name="modules">Establishes which portal and definition own a module under evaluation.</param>
    /// <param name="tabs">Establishes which portal owns a page under evaluation.</param>
    /// <exception cref="ArgumentNullException">Any argument is <see langword="null"/>.</exception>
    /// <remarks>
    /// Five collaborators, and each earns its place. <see cref="IModuleRepository"/> and <see
    /// cref="ITabRepository"/> supply the one fact the scoped members are not given and cannot do without -
    /// the portal that owns the module or page - because the contract's scoped members name only the module
    /// or the page, and resolving role names outside the owning portal is the cross-tenant escalation the
    /// contract explicitly forbids.
    /// </remarks>
    public PermissionEvaluator(
        IPermissionRepository permissions,
        IRoleRepository roles,
        IModuleRepository modules,
        ITabRepository tabs)
    {
        _permissions = permissions ?? throw new ArgumentNullException(nameof(permissions));
        _roles = roles ?? throw new ArgumentNullException(nameof(roles));
        _modules = modules ?? throw new ArgumentNullException(nameof(modules));
        _tabs = tabs ?? throw new ArgumentNullException(nameof(tabs));
    }

    /// <inheritdoc/>
    /// <remarks>
    /// The union spans every module and page of the tenant. Soft-deleted ones are excluded, because a grant
    /// on something the caller can no longer reach confers nothing.
    /// </remarks>
    public async Task<Result<IReadOnlyList<string>>> ListEffectivePortalPermissionKeysAsync(
        int portalId,
        int? userId,
        IReadOnlyCollection<string> roleNames,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(roleNames);
        cancellationToken.ThrowIfCancellationRequested();

        // The portal is the scope here, so role names resolve against it directly rather than through an
        // owning module or page.
        Principal principal = await BuildPrincipalAsync(portalId, userId, roleNames, cancellationToken)
            .ConfigureAwait(false);

        // Live modules, indexed to their definitions: the definition bounds which catalogue entries apply to
        // a module, and it is needed again below when each reached grant is checked against its scope.
        IReadOnlyList<Module> modules = await _modules
            .GetByPortalIdAsync(portalId, cancellationToken)
            .ConfigureAwait(false);

        Dictionary<int, int> liveModuleDefinitions = new(modules.Count);
        foreach (Module module in modules)
        {
            if (!module.IsDeleted)
            {
                liveModuleDefinitions[module.ModuleId] = module.ModuleDefinitionId;
            }
        }

        IReadOnlyList<Tab> tabs = await _tabs
            .GetByPortalIdAsync(portalId, cancellationToken)
            .ConfigureAwait(false);

        HashSet<int> liveTabs = new(tabs.Count);
        foreach (Tab tab in tabs)
        {
            if (!tab.IsDeleted)
            {
                liveTabs.Add(tab.TabId);
            }
        }

        IReadOnlyList<ModulePermission> moduleGrants = await _permissions
            .GetModulePermissionsByPortalIdAsync(portalId, cancellationToken)
            .ConfigureAwait(false);

        IReadOnlyList<TabPermission> tabGrants = await _permissions
            .GetTabPermissionsByPortalIdAsync(portalId, cancellationToken)
            .ConfigureAwait(false);

        // Reachability is decided before the catalogue is consulted, so the catalogue is only read for the
        // permissions that can still affect the answer. Denying grants are kept: a key that is never seen
        // cannot be suppressed.
        List<ReachedGrant> reached = new(moduleGrants.Count + tabGrants.Count);

        foreach (ModulePermission grant in moduleGrants)
        {
            if (liveModuleDefinitions.ContainsKey(grant.ModuleId)
                && Matches(grant.RoleId, grant.UserId, principal))
            {
                reached.Add(new ReachedGrant(
                    PermissionScope.Module,
                    grant.ModuleId,
                    grant.PermissionId,
                    grant.AllowAccess));
            }
        }

        foreach (TabPermission grant in tabGrants)
        {
            if (liveTabs.Contains(grant.TabId) && Matches(grant.RoleId, grant.UserId, principal))
            {
                reached.Add(new ReachedGrant(
                    PermissionScope.Tab,
                    grant.TabId,
                    grant.PermissionId,
                    grant.AllowAccess));
            }
        }

        // The key is resolved from the catalogue by the grant's own PermissionID column rather than from a
        // navigation property.
        Dictionary<int, Permission> catalogue = await ReadCatalogueAsync(reached, cancellationToken)
            .ConfigureAwait(false);

        List<MatchedGrant> matched = new(reached.Count);
        foreach (ReachedGrant grant in reached)
        {
            Permission? entry = catalogue.GetValueOrDefault(grant.PermissionId);
            if (entry is null)
            {
                // The catalogue entry the grant names does not exist, so there is no key to confer. That is
                // a broken row rather than a denial, and failing closed is the only safe reading of it.
                continue;
            }

            bool inScope = grant.Scope == PermissionScope.Module
                ? IsModuleScoped(entry, liveModuleDefinitions[grant.ScopeId])
                : IsTabScoped(entry);

            if (inScope)
            {
                matched.Add(new MatchedGrant(
                    grant.Scope,
                    grant.ScopeId,
                    entry.PermissionKey,
                    grant.AllowAccess));
            }
        }

        return Result<IReadOnlyList<string>>.Success(Names(Survivors(matched)));
    }

    /// <inheritdoc/>
    public async Task<Result<IReadOnlyList<string>>> ListEffectiveModulePermissionKeysAsync(
        int moduleId,
        int? userId,
        IReadOnlyCollection<string> roleNames,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(roleNames);
        cancellationToken.ThrowIfCancellationRequested();

        List<MatchedGrant>? matched = await CollectModuleGrantsAsync(
                moduleId,
                permissionKey: null,
                userId,
                roleNames,
                cancellationToken)
            .ConfigureAwait(false);

        return matched is null
            ? Result<IReadOnlyList<string>>.Success(Array.Empty<string>(), UnknownModule(moduleId))
            : Result<IReadOnlyList<string>>.Success(Names(Survivors(matched)));
    }

    /// <inheritdoc />
    public async Task<Result<IReadOnlyList<string>>> ListEffectiveTabPermissionKeysAsync(
        int tabId,
        int? userId,
        IReadOnlyCollection<string> roleNames,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(roleNames);
        cancellationToken.ThrowIfCancellationRequested();

        List<MatchedGrant>? matched = await CollectTabGrantsAsync(
                tabId,
                permissionKey: null,
                userId,
                roleNames,
                cancellationToken)
            .ConfigureAwait(false);

        return matched is null
            ? Result<IReadOnlyList<string>>.Success(Array.Empty<string>(), UnknownTab(tabId))
            : Result<IReadOnlyList<string>>.Success(Names(Survivors(matched)));
    }

    /// <inheritdoc/>
    public async Task<Result<bool>> HasModulePermissionAsync(
        int moduleId,
        PermissionKey permissionKey,
        int? userId,
        IReadOnlyCollection<string> roleNames,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(roleNames);
        cancellationToken.ThrowIfCancellationRequested();

        List<MatchedGrant>? matched = await CollectModuleGrantsAsync(
                moduleId,
                permissionKey,
                userId,
                roleNames,
                cancellationToken)
            .ConfigureAwait(false);

        return matched is null
            ? Result<bool>.Success(false, UnknownModule(moduleId))
            : Result<bool>.Success(Holds(matched, permissionKey));
    }

    /// <inheritdoc/>
    public async Task<Result<bool>> HasTabPermissionAsync(
        int tabId,
        PermissionKey permissionKey,
        int? userId,
        IReadOnlyCollection<string> roleNames,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(roleNames);
        cancellationToken.ThrowIfCancellationRequested();

        List<MatchedGrant>? matched = await CollectTabGrantsAsync(
                tabId,
                permissionKey,
                userId,
                roleNames,
                cancellationToken)
            .ConfigureAwait(false);

        return matched is null
            ? Result<bool>.Success(false, UnknownTab(tabId))
            : Result<bool>.Success(Holds(matched, permissionKey));
    }

    /// <inheritdoc/>
    /// <remarks>
    /// FOUR READS, WHATEVER THE PAGE COUNT, and each one is the set-based form of a read the single-page
    /// collector performs once per page: the pages themselves, the caller's roles within the owning tenant,
    /// the page-scope catalogue, and the grants.
    /// </remarks>
    public async Task<Result<bool>> HasAnyTabPermissionAsync(
        IReadOnlyCollection<int> tabIds,
        PermissionKey permissionKey,
        int? userId,
        IReadOnlyCollection<string> roleNames,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tabIds);
        ArgumentNullException.ThrowIfNull(roleNames);
        cancellationToken.ThrowIfCancellationRequested();

        List<int> granting = await CollectTabsWithPermissionAsync(
            tabIds,
            permissionKey,
            userId,
            roleNames,
            cancellationToken).ConfigureAwait(false);

        return Result<bool>.Success(granting.Count > 0);
    }

    /// <inheritdoc />
    public async Task<Result<IReadOnlyList<int>>> ListTabsWithPermissionAsync(
        IReadOnlyCollection<int> tabIds,
        PermissionKey permissionKey,
        int? userId,
        IReadOnlyCollection<string> roleNames,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tabIds);
        ArgumentNullException.ThrowIfNull(roleNames);
        cancellationToken.ThrowIfCancellationRequested();

        List<int> granting = await CollectTabsWithPermissionAsync(
            tabIds,
            permissionKey,
            userId,
            roleNames,
            cancellationToken).ConfigureAwait(false);

        return Result<IReadOnlyList<int>>.Success(granting);
    }

    /// <summary>
    /// Judges every named page against one permission key and returns the identifiers of those that grant
    /// it.
    /// </summary>
    /// <param name="tabIds">The pages to judge.</param>
    /// <param name="permissionKey">The key to test.</param>
    /// <param name="userId">Account identifier, or <see langword="null"/> for an anonymous caller.</param>
    /// <param name="roleNames">The role names the caller holds.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The granting page identifiers, in the order the pages were read.</returns>
    /// <remarks>
    /// ONE BODY SERVES BOTH PUBLIC MEMBERS, and that is a correctness property rather than a tidiness one.
    /// </remarks>
    private async Task<List<int>> CollectTabsWithPermissionAsync(
        IReadOnlyCollection<int> tabIds,
        PermissionKey permissionKey,
        int? userId,
        IReadOnlyCollection<string> roleNames,
        CancellationToken cancellationToken)
    {
        var granting = new List<int>();

        ArgumentNullException.ThrowIfNull(tabIds);
        ArgumentNullException.ThrowIfNull(roleNames);
        cancellationToken.ThrowIfCancellationRequested();

        if (tabIds.Count == 0)
        {
            // No page can grant anything, so no read is issued. This is an ordinary state rather than an
            // error: a module placed nowhere is administered from nowhere.
            return granting;
        }

        IReadOnlyList<Tab> pages = await _tabs.GetByIdsAsync(tabIds, cancellationToken).ConfigureAwait(false);

        if (pages.Count == 0)
        {
            // Every named page is absent, which is the same closed default the single-page member reports
            // for one absent page.
            return granting;
        }

        // One principal per distinct owning tenant. Cached so a set of pages sharing a tenant - which is
        // every set this member is asked about - resolves the caller's roles exactly once.
        Dictionary<int, Principal> principalsByPortal = new();
        Principal? hostScopePrincipal = null;

        IReadOnlyList<Permission> catalogue = await _permissions
            .GetByTabIdAsync(pages[0].TabId, cancellationToken)
            .ConfigureAwait(false);

        Dictionary<int, string> applicable = NarrowCatalogue(catalogue, permissionKey, IsTabScoped);

        if (applicable.Count == 0)
        {
            // Nothing in the catalogue confers the key being asked about, so no grant could match it and the
            // grant read is not issued at all.
            return granting;
        }

        IReadOnlyList<TabPermission> grants = await _permissions
            .GetTabPermissionsByTabIdsAsync(
                pages.Select(page => page.TabId).ToList(),
                AnyPermissionId,
                cancellationToken)
            .ConfigureAwait(false);

        var grantsByTabId = new Dictionary<int, List<TabPermission>>();
        foreach (TabPermission grant in grants)
        {
            if (!grantsByTabId.TryGetValue(grant.TabId, out List<TabPermission>? group))
            {
                group = new List<TabPermission>();
                grantsByTabId[grant.TabId] = group;
            }

            group.Add(grant);
        }

        foreach (Tab page in pages)
        {
            if (!grantsByTabId.TryGetValue(page.TabId, out List<TabPermission>? pageGrants))
            {
                // A page with no grant at all confers nothing, which is the same verdict the single-page
                // member reaches from an empty matched set.
                continue;
            }

            Principal principal;
            if (page.PortalId is int owningPortalId)
            {
                if (!principalsByPortal.TryGetValue(owningPortalId, out principal))
                {
                    principal = await BuildPrincipalAsync(owningPortalId, userId, roleNames, cancellationToken)
                        .ConfigureAwait(false);
                    principalsByPortal[owningPortalId] = principal;
                }
            }
            else
            {
                hostScopePrincipal ??= await BuildPrincipalAsync(null, userId, roleNames, cancellationToken)
                    .ConfigureAwait(false);
                principal = hostScopePrincipal.Value;
            }

            var matched = new List<MatchedGrant>(pageGrants.Count);
            foreach (TabPermission grant in pageGrants)
            {
                if (!applicable.TryGetValue(grant.PermissionId, out string? key))
                {
                    continue;
                }

                if (Matches(grant.RoleId, grant.UserId, principal))
                {
                    matched.Add(new MatchedGrant(
                        PermissionScope.Tab,
                        grant.TabId,
                        key,
                        grant.AllowAccess));
                }
            }

            // Judged with the SAME rule and within the SAME page as the single-page member applies, so deny
            // precedence stays a within-page decision.
            if (Holds(matched, permissionKey))
            {
                granting.Add(page.TabId);
            }
        }

        return granting;
    }

    /// <summary>Collects the grants on one module that the given caller reaches.</summary>
    /// <param name="moduleId">The module being evaluated.</param>
    /// <param name="permissionKey">
    /// The single key to narrow the catalogue to, or <see langword="null"/> to consider every key the
    /// module's catalogue declares.
    /// </param>
    /// <param name="userId">The caller's account identifier, or <see langword="null"/> when anonymous.</param>
    /// <param name="roleNames">The role names the caller declares.</param>
    /// <param name="cancellationToken">Token observed for cancellation.</param>
    /// <returns>The reached grants, allowing and denying alike, in no significant order.</returns>
    /// <remarks>
    /// The reads happen in a fixed order, and the order is the point. The module is read first because it
    /// names the portal that role names must resolve within - which is what <see
    /// cref="BuildPrincipalAsync"/> then reads the roles of - and the definition that bounds its catalogue.
    /// </remarks>
    private async Task<List<MatchedGrant>?> CollectModuleGrantsAsync(
        int moduleId,
        PermissionKey? permissionKey,
        int? userId,
        IReadOnlyCollection<string> roleNames,
        CancellationToken cancellationToken)
    {
        List<MatchedGrant> matched = new();

        Module? module = await _modules.GetByIdAsync(moduleId, cancellationToken).ConfigureAwait(false);
        if (module is null)
        {
            return null;
        }

        Principal principal = await BuildPrincipalAsync(module.PortalId, userId, roleNames, cancellationToken)
            .ConfigureAwait(false);

        IReadOnlyList<Permission> catalogue = await _permissions
            .GetByModuleIdAsync(moduleId, cancellationToken)
            .ConfigureAwait(false);

        Dictionary<int, string> applicable = NarrowCatalogue(
            catalogue,
            permissionKey,
            entry => IsModuleScoped(entry, module.ModuleDefinitionId));

        if (applicable.Count == 0)
        {
            return matched;
        }

        IReadOnlyList<ModulePermission> grants = await _permissions
            .GetModulePermissionsByModuleIdAsync(moduleId, AnyPermissionId, cancellationToken)
            .ConfigureAwait(false);

        foreach (ModulePermission grant in grants)
        {
            if (grant.ModuleId != moduleId)
            {
                continue;
            }

            if (!applicable.TryGetValue(grant.PermissionId, out string? key))
            {
                continue;
            }

            if (Matches(grant.RoleId, grant.UserId, principal))
            {
                matched.Add(new MatchedGrant(
                    PermissionScope.Module,
                    grant.ModuleId,
                    key,
                    grant.AllowAccess));
            }
        }

        return matched;
    }

    /// <summary>Collects the grants on one page that the given caller reaches.</summary>
    /// <param name="tabId">The page being evaluated.</param>
    /// <param name="permissionKey">
    /// The single key to narrow the catalogue to, or <see langword="null"/> to consider every key the page
    /// catalogue declares.
    /// </param>
    /// <param name="userId">The caller's account identifier, or <see langword="null"/> when anonymous.</param>
    /// <param name="roleNames">The role names the caller declares.</param>
    /// <param name="cancellationToken">Token observed for cancellation.</param>
    /// <returns>The reached grants, allowing and denying alike, in no significant order.</returns>
    private async Task<List<MatchedGrant>?> CollectTabGrantsAsync(
        int tabId,
        PermissionKey? permissionKey,
        int? userId,
        IReadOnlyCollection<string> roleNames,
        CancellationToken cancellationToken)
    {
        List<MatchedGrant> matched = new();

        Tab? tab = await _tabs.GetByIdAsync(tabId, cancellationToken).ConfigureAwait(false);
        if (tab is null)
        {
            // Null rather than an empty list, for the reason given in CollectModuleGrantsAsync.
            return null;
        }

        Principal principal = await BuildPrincipalAsync(tab.PortalId, userId, roleNames, cancellationToken)
            .ConfigureAwait(false);

        IReadOnlyList<Permission> catalogue = await _permissions
            .GetByTabIdAsync(tabId, cancellationToken)
            .ConfigureAwait(false);

        Dictionary<int, string> applicable = NarrowCatalogue(catalogue, permissionKey, IsTabScoped);

        if (applicable.Count == 0)
        {
            return matched;
        }

        IReadOnlyList<TabPermission> grants = await _permissions
            .GetTabPermissionsByTabIdAsync(tabId, AnyPermissionId, cancellationToken)
            .ConfigureAwait(false);

        foreach (TabPermission grant in grants)
        {
            if (grant.TabId != tabId)
            {
                continue;
            }

            if (!applicable.TryGetValue(grant.PermissionId, out string? key))
            {
                continue;
            }

            if (Matches(grant.RoleId, grant.UserId, principal))
            {
                matched.Add(new MatchedGrant(
                    PermissionScope.Tab,
                    grant.TabId,
                    key,
                    grant.AllowAccess));
            }
        }

        return matched;
    }

    /// <summary>
    /// Reduces a catalogue to the permission identifiers a grant may legitimately be judged against, and
    /// the key each one confers.
    /// </summary>
    /// <param name="catalogue">The entries the catalogue reader returned.</param>
    /// <param name="permissionKey">
    /// The single key to narrow to, or <see langword="null"/> to keep every key the catalogue declares.
    /// </param>
    /// <param name="inScope">The scope test for the collector calling this - definition-bound or page-wide.</param>
    /// <returns>The surviving keys, indexed by permission identifier.</returns>
    /// <remarks>
    /// <para>
    /// Extracted so the module and page collectors cannot drift apart on the three rules that decide which
    /// catalogue entries are eligible, since a divergence here is an authorisation defect rather than an
    /// inconsistency. The rules are, in order: the entry must carry the key being asked about, it must
    /// belong to the scope being evaluated, and its identifier must not be the wildcard.
    /// </para>
    /// <para>
    /// The value side carries the STORED SPELLING of the key rather than an enumeration member, because
    /// <see cref="Permission.PermissionKey"/> is free text and an installation may legitimately declare a
    /// key this solution does not name. Such an entry is carried through here and simply matches no
    /// requested key further down, which is the closed default; it is never rejected at materialisation and
    /// never collapsed onto a member it is not.
    /// </para>
    /// </remarks>
    private static Dictionary<int, string> NarrowCatalogue(
        IReadOnlyList<Permission> catalogue,
        PermissionKey? permissionKey,
        Func<Permission, bool> inScope)
    {
        Dictionary<int, string> applicable = new(catalogue.Count);

        foreach (Permission entry in catalogue)
        {
            if (!Applies(entry, permissionKey) || !inScope(entry))
            {
                continue;
            }

            if (entry.PermissionId == AnyPermissionId)
            {
                continue;
            }

            applicable.TryAdd(entry.PermissionId, entry.PermissionKey);
        }

        return applicable;
    }

    /// <summary>Reads the catalogue entry behind each distinct permission a set of grants names.</summary>
    /// <param name="reached">The grants whose permissions need resolving.</param>
    /// <param name="cancellationToken">Token observed for cancellation.</param>
    /// <returns>The entries that exist, indexed by permission identifier.</returns>
    private async Task<Dictionary<int, Permission>> ReadCatalogueAsync(
        IReadOnlyCollection<ReachedGrant> reached,
        CancellationToken cancellationToken)
    {
        HashSet<int> permissionIds = new(reached.Count);
        foreach (ReachedGrant grant in reached)
        {
            permissionIds.Add(grant.PermissionId);
        }

        IReadOnlyList<Permission> entries = await _permissions
            .GetByIdsAsync(permissionIds, cancellationToken)
            .ConfigureAwait(false);

        Dictionary<int, Permission> catalogue = new(entries.Count);
        foreach (Permission entry in entries)
        {
            catalogue[entry.PermissionId] = entry;
        }

        return catalogue;
    }

    /// <summary>Builds the effective principal a set of grants is matched against.</summary>
    /// <param name="owningPortalId">
    /// The portal that owns the scope under evaluation, or <see langword="null"/> for a host-level scope
    /// that belongs to no portal.
    /// </param>
    /// <param name="userId">The caller's account identifier, or <see langword="null"/> when anonymous.</param>
    /// <param name="roleNames">The role names the caller declares.</param>
    /// <param name="cancellationToken">Token observed for cancellation.</param>
    /// <returns>The account identifier together with the role identifiers those names resolve to.</returns>
    private async Task<Principal> BuildPrincipalAsync(
        int? owningPortalId,
        int? userId,
        IReadOnlyCollection<string> roleNames,
        CancellationToken cancellationToken)
    {
        HashSet<string> declared = new(StringComparer.Ordinal);

        foreach (string name in roleNames)
        {
            if (!string.IsNullOrWhiteSpace(name))
            {
                declared.Add(name);
            }
        }

        declared.Add(SpecialRoleNames.AllUsers);

        if (userId is null)
        {
            declared.Add(SpecialRoleNames.Unauthenticated);
        }

        HashSet<int> roleIds = new();

        if (owningPortalId is int portalId)
        {
            IReadOnlyList<Role> portalRoles = await _roles
                .GetByPortalIdAsync(portalId, cancellationToken)
                .ConfigureAwait(false);

            foreach (Role role in portalRoles)
            {
                if (declared.Contains(role.RoleName))
                {
                    roleIds.Add(role.RoleId);
                }
            }
        }

        return new Principal(userId, roleIds);
    }

    /// <summary>Decides whether one grant row reaches the given caller.</summary>
    /// <param name="roleId">The grant's role identifier, or <see langword="null"/> when it names none.</param>
    /// <param name="userId">The grant's account identifier, or <see langword="null"/> when it names none.</param>
    /// <param name="principal">The caller, as resolved by <see cref="BuildPrincipalAsync"/>.</param>
    /// <returns><see langword="true"/> when the grant applies to the caller.</returns>
    /// <remarks>
    /// The legacy account test could not distinguish an account from an absence. The terminal column is
    /// nullable, so absence is now <see langword="null"/> and every stored number is a real account.
    /// </remarks>
    private static bool Matches(int? roleId, int? userId, in Principal principal)
    {
        if (userId is int grantedUserId)
        {
            return principal.UserId is int callerId && callerId == grantedUserId;
        }

        if (roleId is not int grantedRoleId)
        {
            return false;
        }

        if (grantedRoleId == AllUsersRoleId)
        {
            return true;
        }

        if (grantedRoleId == UnauthenticatedRoleId)
        {
            return principal.IsAnonymous;
        }

        if (grantedRoleId == SuperUserRoleId)
        {
            return false;
        }

        return principal.RoleIds.Contains(grantedRoleId);
    }

    /// <summary>Reduces reached grants to the keys that survive allow-and-deny precedence.</summary>
    /// <param name="matched">
    /// Every grant the caller reached, in any order, possibly spanning several modules and pages.
    /// </param>
    /// <returns>The surviving keys, without duplicates.</returns>
    /// <remarks>
    /// <para>
    /// A key survives when some grant allows it on a scope where no grant denies it.
    /// </para>
    /// <para>
    /// Keys are compared WITHOUT REGARD TO CASE, in both the deny index and the surviving set, because
    /// <see cref="Permission.PermissionKey"/> is free text held in a column whose collation is
    /// case-insensitive: two grants naming <c>EDIT</c> and <c>edit</c> on the same scope are two grants on
    /// one key, so a deny recorded under either spelling must suppress an allow recorded under the other.
    /// Folding the deny index through <see cref="CanonicalKey"/> is what makes that true, since a value
    /// tuple compares its string component ordinally.
    /// </para>
    /// </remarks>
    private static HashSet<string> Survivors(IReadOnlyCollection<MatchedGrant> matched)
    {
        HashSet<(PermissionScope Scope, int ScopeId, string Key)> denied = new();

        foreach (MatchedGrant grant in matched)
        {
            if (!grant.AllowAccess)
            {
                denied.Add((grant.Scope, grant.ScopeId, CanonicalKey(grant.Key)));
            }
        }

        HashSet<string> held = new(StringComparer.OrdinalIgnoreCase);

        foreach (MatchedGrant grant in matched)
        {
            if (grant.AllowAccess && !denied.Contains((grant.Scope, grant.ScopeId, CanonicalKey(grant.Key))))
            {
                held.Add(grant.Key);
            }
        }

        return held;
    }

    /// <summary>Folds a stored permission key to the single form the deny index is built over.</summary>
    /// <param name="key">The key exactly as the catalogue row holds it.</param>
    /// <returns>The case-folded form, used for comparison only and never returned to a caller.</returns>
    /// <remarks>
    /// Invariant rather than current-culture, so the fold cannot vary with the host's locale - the Turkish
    /// dotless i being the case that makes a culture-sensitive fold wrong here.
    /// </remarks>
    private static string CanonicalKey(string key) => key.ToUpperInvariant();

    /// <summary>Builds the advisory reason for a module identifier that names no module.</summary>
    /// <param name="moduleId">The identifier the caller supplied.</param>
    /// <returns>The advisory reason accompanying the closed-default verdict.</returns>
    private static ResultReason UnknownModule(int moduleId)
    {
        return new ResultReason(
            UnknownModuleCode,
            FormattableString.Invariant(
                $"No module carries identifier {moduleId}, so the caller holds nothing on it."));
    }

    /// <summary>Builds the advisory reason for a page identifier that names no page.</summary>
    /// <param name="tabId">The identifier the caller supplied.</param>
    /// <returns>The advisory reason accompanying the closed-default verdict.</returns>
    private static ResultReason UnknownTab(int tabId)
    {
        return new ResultReason(
            UnknownTabCode,
            FormattableString.Invariant(
                $"No page carries identifier {tabId}, so the caller holds nothing on it."));
    }

    /// <summary>Decides whether reached grants confer one particular key.</summary>
    /// <param name="matched">Every grant the caller reached for the scope being decided.</param>
    /// <param name="permissionKey">The key being asked about.</param>
    /// <returns><see langword="true"/> when the caller holds it.</returns>
    private static bool Holds(IReadOnlyCollection<MatchedGrant> matched, PermissionKey permissionKey)
    {
        // The surviving set compares without regard to case, so the member's own spelling is the whole
        // question and a stored value cased differently still answers it.
        return Survivors(matched).Contains(permissionKey.ToString());
    }

    /// <summary>Renders surviving keys as the strings the contract returns.</summary>
    /// <param name="keys">The surviving keys, as the catalogue rows spell them.</param>
    /// <returns>The keys, distinct, in a stable ordinal order.</returns>
    /// <remarks>
    /// The STORED SPELLING is the wire value, so nothing is re-cased on the way out and a key this solution
    /// does not name travels exactly as its row holds it. The ordering is ordinal so that the sequence is
    /// reproducible between calls whatever the host's locale.
    /// </remarks>
    private static IReadOnlyList<string> Names(HashSet<string> keys)
    {
        List<string> names = new(keys);

        names.Sort(StringComparer.Ordinal);

        return names;
    }

    /// <summary>Decides whether a catalogue entry carries the key being asked about.</summary>
    /// <param name="entry">The catalogue entry.</param>
    /// <param name="permissionKey">The key to narrow to, or <see langword="null"/> to accept every key.</param>
    /// <returns><see langword="true"/> when the entry is in play.</returns>
    /// <remarks>
    /// A member's identifier IS the spelling the column stores, so the test is a text comparison against
    /// that identifier - case-insensitively, matching the collation the legacy procedures compared under.
    /// An entry carrying a key outside the enumeration simply fails this test whenever a key is named, and
    /// is kept whenever none is.
    /// </remarks>
    private static bool Applies(Permission entry, PermissionKey? permissionKey)
    {
        return permissionKey is not PermissionKey wanted
            || string.Equals(entry.PermissionKey, wanted.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Decides whether a catalogue entry belongs to the module scope being evaluated.</summary>
    /// <param name="entry">The catalogue entry.</param>
    /// <param name="moduleDefinitionId">The definition the module under evaluation was built from.</param>
    /// <returns><see langword="true"/> when the entry applies to that module.</returns>
    private static bool IsModuleScoped(Permission entry, int moduleDefinitionId)
    {
        return string.Equals(entry.PermissionCode, ModuleDefinitionScopeCode, StringComparison.Ordinal)
            || entry.ModuleDefinitionId == moduleDefinitionId;
    }

    /// <summary>Decides whether a catalogue entry belongs to the page scope.</summary>
    /// <param name="entry">The catalogue entry.</param>
    /// <returns><see langword="true"/> when the entry applies to pages.</returns>
    private static bool IsTabScoped(Permission entry)
    {
        return string.Equals(entry.PermissionCode, TabScopeCode, StringComparison.Ordinal);
    }

    /// <summary>Distinguishes the two kinds of thing a permission grant can be recorded against.</summary>
    private enum PermissionScope
    {
        /// <summary>Recorded against a module instance.</summary>
        Module,

        /// <summary>Recorded against a page.</summary>
        Tab,
    }

    /// <summary>A caller, reduced to what a grant can be matched against.</summary>
    /// <param name="UserId">The account identifier, or <see langword="null"/> for an anonymous caller.</param>
    /// <param name="RoleIds">
    /// The identifiers the caller's declared role names resolved to within the owning portal, including
    /// <c>0</c> and any negative identifier a genuine role happens to hold.
    /// </param>
    private readonly record struct Principal(int? UserId, HashSet<int> RoleIds)
    {
        /// <summary>Gets a value indicating whether the caller has not signed in.</summary>
        /// <remarks>
        /// An absent account identifier is the anonymous caller, which is what the contract documents its
        /// nullable account parameter to mean.
        /// </remarks>
        internal bool IsAnonymous => UserId is null;
    }

    /// <summary>A grant that reached the caller, before its permission has been resolved to a key.</summary>
    /// <param name="Scope">Which kind of thing the grant was recorded against.</param>
    /// <param name="ScopeId">The module or page the grant was recorded against.</param>
    /// <param name="PermissionId">The catalogue entry the grant names.</param>
    /// <param name="AllowAccess">
    /// The stored allow-or-deny flag, carried through uninterpreted: interpreting it is <see
    /// cref="Survivors"/>'s job.
    /// </param>
    private readonly record struct ReachedGrant(
        PermissionScope Scope,
        int ScopeId,
        int PermissionId,
        bool AllowAccess);

    /// <summary>A grant that reached the caller, with its permission resolved to a key.</summary>
    /// <param name="Scope">Which kind of thing the grant was recorded against.</param>
    /// <param name="ScopeId">The module or page the grant was recorded against.</param>
    /// <param name="Key">
    /// The key the grant confers or denies, exactly as its catalogue row spells it - free text, because the
    /// column is, so a key this solution does not name still travels through the decision intact.
    /// </param>
    /// <param name="AllowAccess"><see langword="true" /> allows, <see langword="false" /> denies.</param>
    private readonly record struct MatchedGrant(
        PermissionScope Scope,
        int ScopeId,
        string Key,
        bool AllowAccess);
}
