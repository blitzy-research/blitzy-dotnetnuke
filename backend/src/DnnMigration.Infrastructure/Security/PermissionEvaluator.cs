using DnnMigration.Application.Abstractions;
using DnnMigration.Domain.Abstractions.Repositories;
using DnnMigration.Domain.Common;
using DnnMigration.Domain.Entities;
using DnnMigration.Domain.Enums;

// MIGRATION: no interface is declared in this file, and that is a decision rather than an omission. The
// public abstraction this type implements already exists and is already consumed:
// Application/Abstractions/IPermissionEvaluator.cs declares it, Application/Services/PermissionService.cs
// depends on it, and Infrastructure/DependencyInjection.cs binds this implementation to it.
//
// MIGRATION: this file supersedes logic the legacy codebase duplicated across three static controllers -
// PermissionController.vb, ModulePermissionController.vb and TabPermissionController.vb, carrying 9, 18 and
// 15 public members - each of which re-derived the same reachability and precedence rules over its own grant
// table.
namespace DnnMigration.Infrastructure.Security;

/// <summary>
/// The single place in this solution where permission grants are turned into an access decision.
/// </summary>
/// <remarks>
/// <para>
/// <strong>What it owns.</strong> Two things, and deliberately only two:
/// <em>which grants a caller reaches</em>, and <em>what those grants consequently confer</em>.
/// </para>
/// <para>
/// <strong>What it does not own.</strong> Host-account short-circuiting, existence checking, the
/// module-inherits-its-view-permission-from-its-page composition, and the shape of the answer
/// handed to an HTTP caller all belong to the application service above. That is why no member here
/// accepts a superuser flag: the legacy test returned true for a host account at
/// <c>PortalSecurity.vb</c> before examining a single role, so the decision belongs to the caller
/// that knows the account, and asking this type would be asking a question already answered.
/// </para>
/// </remarks>
internal sealed class PermissionEvaluator : IPermissionEvaluator
{
    /// <summary>The role identifier that admits every caller, authenticated or not.</summary>
    /// <remarks>
    /// MIGRATION: <c>glbRoleAllUsers = "-1"</c> at <c>Library/Components/Shared/Globals.vb</c>,
    /// displayed as "All Users". It is emphatically not an absence marker: <c>Null.NullInteger</c>
    /// is also <c>-1</c>, and conflating the two would discard every public grant in the
    /// installation.
    /// </remarks>
    private const int AllUsersRoleId = -1;

    /// <summary>
    /// The role identifier reserved for host accounts, which matches nobody here.
    /// </summary>
    /// <remarks>
    /// MIGRATION: <c>glbRoleSuperUser = "-2"</c> at <c>Library/Components/Shared/Globals.vb</c>. It
    /// is matched explicitly and refused, rather than left to fall through, because a reader who
    /// finds a <c>-2</c> in a grant row deserves to find the rule that governs it.
    /// </remarks>
    private const int SuperUserRoleId = -2;

    /// <summary>The role identifier that admits only callers who have not signed in.</summary>
    /// <remarks>
    /// MIGRATION: <c>glbRoleUnauthUser = "-3"</c> at <c>Library/Components/Shared/Globals.vb</c>,
    /// displayed as "Unauthenticated Users". The contract carries no authentication flag of its
    /// own: an absent account identifier <em>is</em> the anonymous caller, which is what the
    /// contract documents its nullable account parameter to mean.
    /// </remarks>
    private const int UnauthenticatedRoleId = -3;

    /// <summary>
    /// The scope code shared by the catalogue entries every module definition inherits.
    /// </summary>
    /// <remarks>
    /// MIGRATION: the literal <c>SYSTEM_MODULE_DEFINITION</c> carried by the terminal
    /// <c>GetPermissionsByModuleID</c> and <c>GetModulePermissionsByModuleID</c> bodies.
    /// </remarks>
    private const string ModuleDefinitionScopeCode = "SYSTEM_MODULE_DEFINITION";

    /// <summary>The scope code carried by the catalogue entries every page shares.</summary>
    /// <remarks>
    /// MIGRATION: the literal <c>SYSTEM_TAB</c> carried by the terminal
    /// <c>GetPermissionsByTabID</c> and <c>GetTabPermissionsByTabID</c> bodies.
    /// </remarks>
    private const string TabScopeCode = "SYSTEM_TAB";

    /// <summary>
    /// The value both grant readers treat as "every permission" in their permission argument.
    /// </summary>
    /// <remarks>
    /// MIGRATION: the terminal grant procedures accept <c>-1</c> in the permission position as a
    /// wildcard, which the repository contract documents. <c>Permission.PermissionID</c> is
    /// <c>IDENTITY(1, 1)</c>, so no catalogue entry can ever carry it and the guard below can never
    /// reject a real entry.
    /// </remarks>
    private const int AnyPermissionId = -1;

    /// <summary>Advisory code reported when a module identifier names no module.</summary>
    /// <remarks>
    /// Deliberately NOT spelled with a token the API edge's status mapper screens for. It travels
    /// on a successful outcome, so it is never translated into a status code at all, and a spelling
    /// the mapper recognised would become a trap the moment some future caller propagated it onto a
    /// failure.
    /// </remarks>
    private const string UnknownModuleCode = "permission.module_unknown";

    /// <summary>Advisory code reported when a page identifier names no page.</summary>
    /// <remarks>Spelled under the same restriction as <see cref="UnknownModuleCode" />.</remarks>
    private const string UnknownTabCode = "permission.tab_unknown";

    private readonly IPermissionRepository _permissions;

    private readonly IRoleRepository _roles;

    private readonly IModuleRepository _modules;

    private readonly ITabRepository _tabs;

    /// <summary>
    /// Initialises a new instance of the <see cref="PermissionEvaluator"/> class.
    /// </summary>
    /// <param name="permissions">Reads the permission catalogue and the recorded grants.</param>
    /// <param name="roles">Resolves role names to identifiers within one portal.</param>
    /// <param name="modules">
    /// Establishes which portal and definition own a module under evaluation.
    /// </param>
    /// <param name="tabs">Establishes which portal owns a page under evaluation.</param>
    /// <exception cref="ArgumentNullException">Any argument is <see langword="null"/>.</exception>
    /// <remarks>
    /// <para>
    /// MIGRATION: five collaborators, and each earns its place. <see cref="IModuleRepository"/> and
    /// <see cref="ITabRepository"/> supply the one fact the scoped members are not given and cannot
    /// do without - the portal that owns the module or page - because the contract's scoped members
    /// name only the module or the page, and resolving role names outside the owning portal is the
    /// cross-tenant escalation the contract explicitly forbids.
    /// </para>
    /// <para>
    /// Both values are used verbatim, never trimmed, folded or localised, because the name in the
    /// roles table is the value that must match.
    /// </para>
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
    /// <para>
    /// The union spans every module and page of the tenant. Soft-deleted ones are excluded, because
    /// a grant on something the caller can no longer reach confers nothing.
    /// </para>
    /// <para>
    /// MIGRATION: this member exists instead of a loop over the two scoped reads, and the reason is
    /// the round-trip count: a portal carrying hundreds of pages and modules would cost hundreds of
    /// reads to answer one question.
    /// </para>
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

        // MIGRATION: the key is resolved from the catalogue by the grant's own PermissionID column rather
        // than from a navigation property. Whether the repository loaded that reference is the repository's
        // business, and a decision that silently returned "holds nothing" because a reference happened to be
        // unloaded would be an authorisation defect that no test of this type could see.
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
    /// <remarks>
    /// Answered from the module's own grants alone. A module that defers its view permission to the
    /// pages it sits on is resolved by the application service, which composes this answer with the
    /// page answers; that composition is not performed here, because a module's grant rows are all
    /// this member is asked about.
    /// </remarks>
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
    /// <remarks>The page counterpart of <see cref="ListEffectiveModulePermissionKeysAsync" />.</remarks>
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
    /// <remarks>
    /// <para>
    /// A caller with no reachable grant on the module holds nothing, so the answer is
    /// <see langword="false"/> rather than an error: an absent grant denies, which is the closed
    /// default and the same answer an explicit denial produces.
    /// </para>
    /// </remarks>
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
    /// <remarks>
    /// The page counterpart of <see cref="HasModulePermissionAsync"/>, with the same rule.
    /// </remarks>
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
    /// <para>
    /// FOUR READS, WHATEVER THE PAGE COUNT, and each one is the set-based form of a read the
    /// single-page collector performs once per page: the pages themselves, the caller's roles
    /// within the owning tenant, the page-scope catalogue, and the grants.
    /// </para>
    /// <para>
    /// The verdict is composed PER PAGE and then disjoined, which is what makes this exactly
    /// equivalent to asking the single-page member once per page. Grouping the grants by page
    /// before judging them is the part that matters: pooling them instead would let an ALLOW on one
    /// page cancel a DENY on another, and deny precedence is decided WITHIN a page's grant set, so
    /// a pooled set would answer a question nobody asked.
    /// </para>
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
    /// Judges every named page against one permission key and returns the identifiers of those that grant it.
    /// </summary>
    /// <param name="tabIds">The pages to judge.</param>
    /// <param name="permissionKey">The key to test.</param>
    /// <param name="userId">Account identifier, or <see langword="null"/> for an anonymous caller.</param>
    /// <param name="roleNames">The role names the caller holds.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The granting page identifiers, in the order the pages were read.</returns>
    /// <remarks>
    /// <para>
    /// ONE BODY SERVES BOTH PUBLIC MEMBERS, and that is a correctness property rather than a tidiness one.
    /// The existential member and the plural member document themselves as asking the IDENTICAL question and
    /// differing only in what they report; implementing them separately would make that a claim maintained by
    /// hand, and a divergence between them would be a security defect - a page listing offering a target the
    /// authorisation check would then refuse, or refusing one it would have allowed.
    /// </para>
    /// <para>
    /// The early exit the existential member used to take is not lost, because there was never any I/O to
    /// save: the page rows, the permission catalogue and the whole grant set are all read BEFORE the loop, and
    /// the loop is in-memory matching. Visiting every page therefore costs the same reads and a negligible
    /// amount of additional matching.
    /// </para>
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

        Dictionary<int, PermissionKey> applicable = NarrowCatalogue(catalogue, permissionKey, IsTabScoped);

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
                if (!applicable.TryGetValue(grant.PermissionId, out PermissionKey key))
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
            // precedence stays a within-page decision. The verdict is COLLECTED rather than returned, which
            // is the single difference between the two members this body serves: visiting every page costs
            // no additional read, because every read above was issued for the whole set before the loop.
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
    /// The single key to narrow the catalogue to, or <see langword="null"/> to consider every key
    /// the module's catalogue declares.
    /// </param>
    /// <param name="userId">
    /// The caller's account identifier, or <see langword="null"/> when anonymous.
    /// </param>
    /// <param name="roleNames">The role names the caller declares.</param>
    /// <param name="cancellationToken">Token observed for cancellation.</param>
    /// <returns>The reached grants, allowing and denying alike, in no significant order.</returns>
    /// <remarks>
    /// <para>
    /// The reads happen in a fixed order, and the order is the point. The module is read first
    /// because it names the portal that role names must resolve within - which is what
    /// <see cref="BuildPrincipalAsync"/> then reads the roles of - and the definition that bounds
    /// its catalogue.
    /// </para>
    /// <para>
    /// An unknown module yields nothing. Reporting existence is the application service's job, and
    /// it does it before asking.
    /// </para>
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
            // Null and an empty list mean different things to the caller, and the difference is the reason
            // this method is nullable. Null is "no module carries that identifier"; an empty list is "the
            // module exists and the caller reached none of its grants".
            return null;
        }

        Principal principal = await BuildPrincipalAsync(module.PortalId, userId, roleNames, cancellationToken)
            .ConfigureAwait(false);

        IReadOnlyList<Permission> catalogue = await _permissions
            .GetByModuleIdAsync(moduleId, cancellationToken)
            .ConfigureAwait(false);

        Dictionary<int, PermissionKey> applicable = NarrowCatalogue(
            catalogue,
            permissionKey,
            entry => IsModuleScoped(entry, module.ModuleDefinitionId));

        if (applicable.Count == 0)
        {
            // Nothing in the catalogue can confer the key being asked about, so no grant on this module
            // could matter. Returning here spends no read at all, which is also what the per-entry loop this
            // replaced did in the same situation - it simply never entered its body.
            return matched;
        }

        // MIGRATION: ONE READ FOR EVERY GRANT ON THE MODULE, then an in-memory join against the catalogue
        // already in hand. This replaces a read per surviving catalogue entry, which made the round-trip
        // count a function of how many permissions the caller asked about - and on the unnarrowed path,
        // where permissionKey is null, that is every permission the module's definition declares.
        IReadOnlyList<ModulePermission> grants = await _permissions
            .GetModulePermissionsByModuleIdAsync(moduleId, AnyPermissionId, cancellationToken)
            .ConfigureAwait(false);

        foreach (ModulePermission grant in grants)
        {
            // The module check re-asserts the argument just passed to the reader, whose module position also
            // carries a documented wildcard, so a row answering a wider question than the one asked is
            // discarded rather than judged.
            if (grant.ModuleId != moduleId)
            {
                continue;
            }

            // The permission check is the in-memory half of the join, and it is exactly as strict as the
            // equality it replaces: a grant survives only when its own PermissionID is one of the entries
            // that passed the key filter and the scope test above.
            if (!applicable.TryGetValue(grant.PermissionId, out PermissionKey key))
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
    /// The single key to narrow the catalogue to, or <see langword="null"/> to consider every key
    /// the page catalogue declares.
    /// </param>
    /// <param name="userId">
    /// The caller's account identifier, or <see langword="null"/> when anonymous.
    /// </param>
    /// <param name="roleNames">The role names the caller declares.</param>
    /// <param name="cancellationToken">Token observed for cancellation.</param>
    /// <returns>The reached grants, allowing and denying alike, in no significant order.</returns>
    /// <remarks>
    /// The page counterpart of <see cref="CollectModuleGrantsAsync"/>, with two measured
    /// differences. And the page grant reader treats only its permission argument as a wildcard,
    /// never its page argument, so the page half of the equality check below is defensive symmetry
    /// rather than a requirement - it is kept so that the two collectors read identically and
    /// neither can be tightened without the other.
    /// </remarks>
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

        Dictionary<int, PermissionKey> applicable = NarrowCatalogue(catalogue, permissionKey, IsTabScoped);

        if (applicable.Count == 0)
        {
            return matched;
        }

        // One read for every grant on the page, then the same in-memory join the module collector performs.
        // GetTabPermissionsByTabIdAsync documents -1 in its permission position as "every permission",
        // measured from the terminal procedure's own guard; its page position, unlike the module reader's,
        // carries no wildcard at all, which is why the page equality below is defensive symmetry rather than
        // a requirement.
        IReadOnlyList<TabPermission> grants = await _permissions
            .GetTabPermissionsByTabIdAsync(tabId, AnyPermissionId, cancellationToken)
            .ConfigureAwait(false);

        foreach (TabPermission grant in grants)
        {
            if (grant.TabId != tabId)
            {
                continue;
            }

            if (!applicable.TryGetValue(grant.PermissionId, out PermissionKey key))
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
    /// Reduces a catalogue to the permission identifiers a grant may legitimately be judged
    /// against, and the key each one confers.
    /// </summary>
    /// <param name="catalogue">The entries the catalogue reader returned.</param>
    /// <param name="permissionKey">
    /// The single key to narrow to, or <see langword="null"/> to keep every key the catalogue
    /// declares.
    /// </param>
    /// <param name="inScope">
    /// The scope test for the collector calling this - definition-bound or page-wide.
    /// </param>
    /// <returns>The surviving keys, indexed by permission identifier.</returns>
    /// <remarks>
    /// <para>
    /// Extracted so the module and page collectors cannot drift apart on the three rules that
    /// decide which catalogue entries are eligible, since a divergence here is an authorisation
    /// defect rather than an inconsistency. The rules are, in order: the entry must carry the key
    /// being asked about, it must belong to the scope being evaluated, and its identifier must not
    /// be the wildcard.
    /// </para>
    /// <para>
    /// The wildcard identifier is excluded because a catalogue row bearing it is not a permission a
    /// grant can name - the value means "every permission" in a reader's argument, so admitting it
    /// as a join key would make any grant match any key.
    /// </para>
    /// </remarks>
    private static Dictionary<int, PermissionKey> NarrowCatalogue(
        IReadOnlyList<Permission> catalogue,
        PermissionKey? permissionKey,
        Func<Permission, bool> inScope)
    {
        Dictionary<int, PermissionKey> applicable = new(catalogue.Count);

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

    /// <summary>
    /// Reads the catalogue entry behind each distinct permission a set of grants names.
    /// </summary>
    /// <param name="reached">The grants whose permissions need resolving.</param>
    /// <param name="cancellationToken">Token observed for cancellation.</param>
    /// <returns>The entries that exist, indexed by permission identifier.</returns>
    /// <remarks>
    /// <para>ONE READ FOR THE WHOLE SET.</para>
    /// </remarks>
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
    /// The portal that owns the scope under evaluation, or <see langword="null"/> for a host-level
    /// scope that belongs to no portal.
    /// </param>
    /// <param name="userId">
    /// The caller's account identifier, or <see langword="null"/> when anonymous.
    /// </param>
    /// <param name="roleNames">The role names the caller declares.</param>
    /// <param name="cancellationToken">Token observed for cancellation.</param>
    /// <returns>
    /// The account identifier together with the role identifiers those names resolve to.
    /// </returns>
    /// <remarks>
    /// <para>
    /// MIGRATION: this reproduces <c>PortalSecurity.IsInRoles</c>
    /// (<c>Library/Components/Security/PortalSecurity.vb</c>) with the ambient state removed.
    /// </para>
    /// <para>
    /// Names are compared <strong>exactly</strong>. Nothing is trimmed, folded or localised,
    /// because the value stored in <c>Roles.RoleName</c> is the value a grant was made against, and
    /// normalising the comparison here would make this type disagree with the store on
    /// installations whose collation does not.
    /// </para>
    /// </remarks>
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
    /// <param name="roleId">
    /// The grant's role identifier, or <see langword="null"/> when it names none.
    /// </param>
    /// <param name="userId">
    /// The grant's account identifier, or <see langword="null"/> when it names none.
    /// </param>
    /// <param name="principal">
    /// The caller, as resolved by <see cref="BuildPrincipalAsync"/>.
    /// </param>
    /// <returns><see langword="true"/> when the grant applies to the caller.</returns>
    /// <remarks>
    /// <para>
    /// A grant names an account or a role, and the account takes precedence in the test because
    /// that is the order the legacy code tested in: <c>ModulePermissionController.vb</c> and
    /// <c>TabPermissionController.vb</c> both examined the account column first and consulted the
    /// role only when it held no account.
    /// </para>
    /// <para>
    /// MIGRATION: the legacy account test could not distinguish an account from an absence. The
    /// terminal column is nullable, so absence is now <see langword="null"/> and every stored
    /// number is a real account.
    /// </para>
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

    /// <summary>
    /// Reduces reached grants to the keys that survive allow-and-deny precedence.
    /// </summary>
    /// <param name="matched">
    /// Every grant the caller reached, in any order, possibly spanning several modules and pages.
    /// </param>
    /// <returns>The surviving keys, without duplicates.</returns>
    /// <remarks>
    /// <para>
    /// A key survives when some grant allows it on a scope where no grant denies it. Denials are
    /// collected in full before any allowance is judged, in a separate pass, so the outcome cannot
    /// depend on the order the store happened to return rows in - which matters because a grant and
    /// a denial of the same key on the same scope is a legitimate configuration, and "whichever
    /// came first wins" would make authorisation depend on a query plan.
    /// </para>
    /// <para>
    /// Suppression is correlated to the scope carrying the denial, never applied globally, for the
    /// reason given in the type remarks.
    /// </para>
    /// </remarks>
    private static HashSet<PermissionKey> Survivors(IReadOnlyCollection<MatchedGrant> matched)
    {
        HashSet<(PermissionScope Scope, int ScopeId, PermissionKey Key)> denied = new();

        foreach (MatchedGrant grant in matched)
        {
            if (!grant.AllowAccess)
            {
                denied.Add((grant.Scope, grant.ScopeId, grant.Key));
            }
        }

        HashSet<PermissionKey> held = new();

        foreach (MatchedGrant grant in matched)
        {
            if (grant.AllowAccess && !denied.Contains((grant.Scope, grant.ScopeId, grant.Key)))
            {
                held.Add(grant.Key);
            }
        }

        return held;
    }

    /// <summary>Builds the advisory reason for a module identifier that names no module.</summary>
    /// <param name="moduleId">The identifier the caller supplied.</param>
    /// <returns>The advisory reason accompanying the closed-default verdict.</returns>
    /// <remarks>Carried on a SUCCESSFUL outcome, never a failed one.</remarks>
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
    /// <remarks>The page counterpart of <see cref="UnknownModule" />, carried the same way.</remarks>
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
    /// <remarks>
    /// Deliberately a membership test over <see cref="Survivors"/> rather than a rule of its own.
    /// </remarks>
    private static bool Holds(IReadOnlyCollection<MatchedGrant> matched, PermissionKey permissionKey)
    {
        return Survivors(matched).Contains(permissionKey);
    }

    /// <summary>Renders surviving keys as the canonical strings the contract returns.</summary>
    /// <param name="keys">The surviving keys.</param>
    /// <returns>The key names, distinct, in a stable ordinal order.</returns>
    /// <remarks>
    /// MIGRATION: the name of a <see cref="PermissionKey"/> member <em>is</em> the stored spelling
    /// and the wire value, so rendering is <see cref="object.ToString"/> and nothing more - no
    /// upper-casing pass is required, because a member cannot be mis-cased. The ordering is not
    /// cosmetic: an access token minted twice from the same grants must carry an identical claim
    /// set both times, and a set's enumeration order is not a contract.
    /// </remarks>
    private static IReadOnlyList<string> Names(HashSet<PermissionKey> keys)
    {
        List<string> names = new(keys.Count);

        foreach (PermissionKey key in keys)
        {
            names.Add(key.ToString());
        }

        names.Sort(StringComparer.Ordinal);

        return names;
    }

    /// <summary>Decides whether a catalogue entry carries the key being asked about.</summary>
    /// <param name="entry">The catalogue entry.</param>
    /// <param name="permissionKey">
    /// The key to narrow to, or <see langword="null"/> to accept every key.
    /// </param>
    /// <returns><see langword="true"/> when the entry is in play.</returns>
    /// <remarks>
    /// MIGRATION: the comparison is between enumeration members, never between strings. A member is
    /// a closed value: it cannot be mis-cased, cannot be parsed from user input here, is never
    /// combined with another member, and is never reduced to its ordinal.
    /// </remarks>
    private static bool Applies(Permission entry, PermissionKey? permissionKey)
    {
        return permissionKey is not PermissionKey wanted || entry.PermissionKey == wanted;
    }

    /// <summary>
    /// Decides whether a catalogue entry belongs to the module scope being evaluated.
    /// </summary>
    /// <param name="entry">The catalogue entry.</param>
    /// <param name="moduleDefinitionId">
    /// The definition the module under evaluation was built from.
    /// </param>
    /// <returns><see langword="true"/> when the entry applies to that module.</returns>
    /// <remarks>
    /// <para>Two admissions, and both are needed.</para>
    /// <para>
    /// The check re-asserts, in this file, the predicate the catalogue read already applied, and it
    /// is worth its keep for what it forecloses: nothing scoped to anything other than this module
    /// can enter a module decision. In particular the file-system permission scope, which this
    /// solution models no entity for and which the migration plan excludes entirely, cannot reach a
    /// module verdict through a catalogue entry - and it cannot reach a page verdict either,
    /// because <see cref="IsTabScoped"/> admits one code and only one.
    /// </para>
    /// </remarks>
    private static bool IsModuleScoped(Permission entry, int moduleDefinitionId)
    {
        return string.Equals(entry.PermissionCode, ModuleDefinitionScopeCode, StringComparison.Ordinal)
            || entry.ModuleDefinitionId == moduleDefinitionId;
    }

    /// <summary>Decides whether a catalogue entry belongs to the page scope.</summary>
    /// <param name="entry">The catalogue entry.</param>
    /// <returns><see langword="true"/> when the entry applies to pages.</returns>
    /// <remarks>
    /// One code, exactly. Keeping the module and page scopes apart is what stops a grant recorded
    /// in one from being read as the other, which matters because both scope identifiers seed at
    /// <c>0</c> and an identifier alone does not say what it identifies.
    /// </remarks>
    private static bool IsTabScoped(Permission entry)
    {
        return string.Equals(entry.PermissionCode, TabScopeCode, StringComparison.Ordinal);
    }

    /// <summary>
    /// Distinguishes the two kinds of thing a permission grant can be recorded against.
    /// </summary>
    /// <remarks>
    /// It exists so that module grants and page grants can travel in one sequence without their
    /// identifiers colliding: <c>Modules.ModuleID</c> and <c>Tabs.TabID</c> both seed at <c>0</c>,
    /// so an identifier on its own does not say what it identifies, and a denial on page 4 must not
    /// suppress a key on module 4.
    /// </remarks>
    private enum PermissionScope
    {
        /// <summary>Recorded against a module instance.</summary>
        Module,

        /// <summary>Recorded against a page.</summary>
        Tab,
    }

    /// <summary>A caller, reduced to what a grant can be matched against.</summary>
    /// <param name="UserId">
    /// The account identifier, or <see langword="null"/> for an anonymous caller.
    /// </param>
    /// <param name="RoleIds">
    /// The identifiers the caller's declared role names resolved to within the owning portal,
    /// including <c>0</c> and any negative identifier a genuine role happens to hold.
    /// </param>
    private readonly record struct Principal(int? UserId, HashSet<int> RoleIds)
    {
        /// <summary>Gets a value indicating whether the caller has not signed in.</summary>
        /// <remarks>
        /// An absent account identifier is the anonymous caller, which is what the contract
        /// documents its nullable account parameter to mean.
        /// </remarks>
        internal bool IsAnonymous => UserId is null;
    }

    /// <summary>
    /// A grant that reached the caller, before its permission has been resolved to a key.
    /// </summary>
    /// <param name="Scope">Which kind of thing the grant was recorded against.</param>
    /// <param name="ScopeId">The module or page the grant was recorded against.</param>
    /// <param name="PermissionId">The catalogue entry the grant names.</param>
    /// <param name="AllowAccess">
    /// The stored allow-or-deny flag, carried through uninterpreted: interpreting it is
    /// <see cref="Survivors"/>'s job.
    /// </param>
    private readonly record struct ReachedGrant(
        PermissionScope Scope,
        int ScopeId,
        int PermissionId,
        bool AllowAccess);

    /// <summary>A grant that reached the caller, with its permission resolved to a key.</summary>
    /// <param name="Scope">Which kind of thing the grant was recorded against.</param>
    /// <param name="ScopeId">The module or page the grant was recorded against.</param>
    /// <param name="Key">The key the grant confers or denies.</param>
    /// <param name="AllowAccess"><see langword="true" /> allows, <see langword="false" /> denies.</param>
    private readonly record struct MatchedGrant(
        PermissionScope Scope,
        int ScopeId,
        PermissionKey Key,
        bool AllowAccess);
}
