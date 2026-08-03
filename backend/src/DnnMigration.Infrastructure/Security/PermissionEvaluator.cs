using DnnMigration.Application.Abstractions;
using DnnMigration.Application.Options;
using DnnMigration.Domain.Abstractions.Repositories;
using DnnMigration.Domain.Entities;
using DnnMigration.Domain.Enums;
using Microsoft.Extensions.Options;

// MIGRATION: no interface is declared in this file, and that is a decision rather than an omission.
// The public abstraction this type implements already exists and is already consumed:
// Application/Abstractions/IPermissionEvaluator.cs declares it, Application/Services/PermissionService.cs
// depends on it, and Infrastructure/DependencyInjection.cs binds this implementation to it. Declaring a
// second interface of the same name in this namespace would not merely duplicate that contract - the
// registration file imports DnnMigration.Application.Abstractions and DnnMigration.Infrastructure.Security
// together, so the name would become ambiguous and the solution would stop compiling. More importantly,
// two contracts for one decision is the very failure this type exists to prevent: an evaluator that can
// disagree with another evaluator surfaces as an intermittent authorisation defect rather than as a
// failure. One contract, one implementation, one place where allow-and-deny precedence is settled.
//
// MIGRATION: this file supersedes logic the legacy codebase duplicated across three static controllers -
// PermissionController.vb, ModulePermissionController.vb and TabPermissionController.vb, carrying 9, 18
// and 15 public members - each of which re-derived the same reachability and precedence rules over its
// own grant table. AAP section 0.4.3 places that arithmetic here, once.
//
// MIGRATION: every read below goes through a Domain repository abstraction. Nothing in this file names a
// database session, a queryable, a table, a column or a stored procedure, which is what AAP Rule T3
// requires and what makes the whole type substitutable in a test with nothing but five fakes. The legacy
// alternative was the reflection-constructed static provider accessor at
// Library/Components/Providers/Data/DataProvider.vb:L31-L50, and it produces no code here.
//
// MIGRATION: the legacy reachability test lived in PortalSecurity.IsInRoles
// (Library/Components/Security/PortalSecurity.vb:L115-L136), which recovered its caller from ambient
// request state, split a semicolon-delimited role string and consulted the ambient web request for the
// authentication flag. The caller is named by argument here instead, so this type touches no request, no
// principal, no cookie and no session, and the same question always produces the same answer.
namespace DnnMigration.Infrastructure.Security;

/// <summary>
/// The single place in this solution where permission grants are turned into an access decision.
/// </summary>
/// <remarks>
/// <para>
/// <strong>What it owns.</strong> Two things, and deliberately only two: <em>which grants a caller
/// reaches</em>, and <em>what those grants consequently confer</em>. Both listings and verdicts run
/// through <see cref="Survivors"/>, so a listing that offers an action and a verdict that refuses it
/// cannot disagree - <see cref="Holds"/> is defined as a membership test over the very set the listings
/// return rather than as a second rule that happens to agree today.
/// </para>
/// <para>
/// <strong>What it does not own.</strong> Host-account short-circuiting, existence checking, the
/// module-inherits-its-view-permission-from-its-page composition, and the shape of the answer handed to
/// an HTTP caller all belong to the application service above. That is why no member here accepts a
/// superuser flag: the legacy test returned true for a host account at
/// <c>PortalSecurity.vb:L123</c> before examining a single role, so the decision belongs to the caller
/// that knows the account, and asking this type would be asking a question already answered.
/// </para>
/// <para>
/// <strong>Deny beats allow, within a scope.</strong> A denying grant suppresses its key on the module or
/// page it was recorded against, even when another grant allows the same key there, and it suppresses it
/// nowhere else. Scoping the suppression matters: the portal-wide read spans every module and page of a
/// tenant, so applying one denial across that whole union would let a single forgotten page strip a key
/// the caller genuinely holds everywhere else. The legacy string builders at
/// <c>ModulePermissionController.vb:L243</c> and <c>TabPermissionController.vb:L218</c> filtered on the
/// allow flag and discarded denials outright, but they were assembling a value for a permission grid
/// rather than deciding access, which is why their behaviour is not the behaviour reproduced here.
/// </para>
/// <para>
/// <strong>Pseudo-principals are identifiers, not rows.</strong> A grant's role identifier may hold a
/// value that names no role row at all: <c>-1</c> is "All Users", <c>-2</c> is "Superuser", <c>-3</c> is
/// "Unauthenticated Users" and <c>-4</c> is "Nothing" - the measured constants at
/// <c>Library/Components/Shared/Globals.vb:L95-L98</c>. This is why neither grant table ever carried a
/// foreign key to the roles table and why the 04.05.00 upgrade script could rebuild <c>RoleID</c> as
/// nullable. <c>Roles.RoleID</c> is <c>IDENTITY(0, 1)</c>, so no genuine role can collide with a negative
/// value and <c>0</c> is a perfectly ordinary role identifier.
/// </para>
/// <para>
/// <strong>Tenant isolation.</strong> Role names are unique per portal rather than per installation, so
/// names are resolved to identifiers only within the portal that owns the module or page under
/// evaluation. Resolving them installation-wide would let a grant to one tenant's "Administrators" role
/// be honoured for another tenant's, which is a silent cross-tenant escalation rather than a visible
/// failure.
/// </para>
/// <para>
/// <strong>Absence denies.</strong> No catalogue entry, no grant, or no reachable grant all mean the
/// caller holds nothing. That is an answer, not an error, and it is the same answer an explicit denial
/// produces.
/// </para>
/// <para>
/// The precedence arithmetic itself is stateless, but every read is issued through repositories that share
/// the unit-of-work scoped context, so this type is registered per request rather than as a singleton.
/// </para>
/// </remarks>
internal sealed class PermissionEvaluator : IPermissionEvaluator
{
    /// <summary>
    /// The role identifier that admits every caller, authenticated or not.
    /// </summary>
    /// <remarks>
    /// MIGRATION: <c>glbRoleAllUsers = "-1"</c> at <c>Library/Components/Shared/Globals.vb:L95</c>,
    /// displayed as "All Users" (<c>L100</c>). <c>PortalSecurity.IsInRoles</c> admitted it
    /// unconditionally at <c>L125</c>, and that is reproduced exactly. It is emphatically not an absence
    /// marker: <c>Null.NullInteger</c> is also <c>-1</c>, and conflating the two would discard every
    /// public grant in the installation.
    /// </remarks>
    private const int AllUsersRoleId = -1;

    /// <summary>
    /// The role identifier reserved for host accounts, which matches nobody here.
    /// </summary>
    /// <remarks>
    /// MIGRATION: <c>glbRoleSuperUser = "-2"</c> at <c>Library/Components/Shared/Globals.vb:L96</c>. It is
    /// matched explicitly and refused, rather than left to fall through, because a reader who finds a
    /// <c>-2</c> in a grant row deserves to find the rule that governs it. No member of this type accepts
    /// a superuser flag - the contract has none, by design - so admitting <c>-2</c> would have to admit
    /// every caller, which is the one mistake this area cannot afford. A host account is answered by the
    /// application service before a grant is read, exactly as <c>PortalSecurity.vb:L123</c> answered it
    /// before examining a role.
    /// </remarks>
    private const int SuperUserRoleId = -2;

    /// <summary>
    /// The role identifier that admits only callers who have not signed in.
    /// </summary>
    /// <remarks>
    /// MIGRATION: <c>glbRoleUnauthUser = "-3"</c> at <c>Library/Components/Shared/Globals.vb:L97</c>,
    /// displayed as "Unauthenticated Users" (<c>L102</c>). <c>PortalSecurity.IsInRoles</c> admitted it
    /// only while <c>context.Request.IsAuthenticated</c> was false, at <c>L124</c>, so a grant to this
    /// identifier is genuinely narrower than a grant to every user and the two are not interchangeable.
    /// The contract carries no authentication flag of its own: an absent account identifier <em>is</em>
    /// the anonymous caller, which is what the contract documents its nullable account parameter to mean.
    /// </remarks>
    private const int UnauthenticatedRoleId = -3;

    /// <summary>
    /// The scope code shared by the catalogue entries every module definition inherits.
    /// </summary>
    /// <remarks>
    /// MIGRATION: the literal <c>SYSTEM_MODULE_DEFINITION</c> carried by the terminal
    /// <c>GetPermissionsByModuleID</c> and <c>GetModulePermissionsByModuleID</c> bodies. It is reference
    /// data seeded by the upgrade scripts rather than a configurable value, so it is a constant. Note
    /// that it is not the <em>only</em> code a module-scoped catalogue entry may carry: an installed
    /// module contributes entries under its own code, and those are legitimate.
    /// </remarks>
    private const string ModuleDefinitionScopeCode = "SYSTEM_MODULE_DEFINITION";

    /// <summary>The scope code carried by the catalogue entries every page shares.</summary>
    /// <remarks>
    /// MIGRATION: the literal <c>SYSTEM_TAB</c> carried by the terminal <c>GetPermissionsByTabID</c> and
    /// <c>GetTabPermissionsByTabID</c> bodies. Unlike the module code above, this one <em>is</em> the
    /// complete page vocabulary: the page catalogue read filters on it and on nothing else.
    /// </remarks>
    private const string TabScopeCode = "SYSTEM_TAB";

    /// <summary>
    /// The value both grant readers treat as "every permission" in their permission argument.
    /// </summary>
    /// <remarks>
    /// MIGRATION: the terminal grant procedures accept <c>-1</c> in the permission position as a wildcard,
    /// which the repository contract documents. <c>Permission.PermissionID</c> is <c>IDENTITY(1, 1)</c>,
    /// so no catalogue entry can ever carry it and the guard below can never reject a real entry. The
    /// guard exists because the consequence of losing it is silent and severe: passing the wildcard would
    /// return the grants of <em>every</em> permission and this type would then judge them as though they
    /// all carried the key that was asked about.
    /// </remarks>
    private const int AnyPermissionId = -1;

    private readonly IPermissionRepository _permissions;

    private readonly IRoleRepository _roles;

    private readonly IModuleRepository _modules;

    private readonly ITabRepository _tabs;

    private readonly string _allUsersRoleName;

    private readonly string _unauthenticatedRoleName;

    /// <summary>Initialises a new instance of the <see cref="PermissionEvaluator"/> class.</summary>
    /// <param name="permissions">Reads the permission catalogue and the recorded grants.</param>
    /// <param name="roles">Resolves role names to identifiers within one portal.</param>
    /// <param name="modules">Establishes which portal and definition own a module under evaluation.</param>
    /// <param name="tabs">Establishes which portal owns a page under evaluation.</param>
    /// <param name="portalOptions">Supplies the two built-in role display names.</param>
    /// <exception cref="ArgumentNullException">Any argument is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// Either built-in role name is blank. Both are load-bearing: they are the names by which a caller
    /// declares that it stands for every user or for an anonymous one, so a blank value would silently
    /// stop matching rather than fail.
    /// </exception>
    /// <remarks>
    /// <para>
    /// MIGRATION: five collaborators, and each earns its place. <see cref="IPermissionRepository"/> and
    /// <see cref="IRoleRepository"/> supply the grants and the roles. <see cref="IModuleRepository"/> and
    /// <see cref="ITabRepository"/> supply the one fact the scoped members are not given and cannot do
    /// without - the portal that owns the module or page - because the contract's scoped members name only
    /// the module or the page, and resolving role names outside the owning portal is the cross-tenant
    /// escalation the contract explicitly forbids. The module lookup additionally yields the definition
    /// that bounds its catalogue.
    /// </para>
    /// <para>
    /// MIGRATION: <see cref="PortalOptions"/> replaces the excluded static role-name constants
    /// <c>glbRoleAllUsersName</c> and <c>glbRoleUnauthUserName</c>. They are configurable rather than
    /// compiled in because the legacy comparison matched a persisted display name as a string, so an
    /// installation that renamed either role would silently stop matching a compiled-in literal. Both
    /// values are copied verbatim - never trimmed, folded or localised - because the name in the roles
    /// table is the value that must match.
    /// </para>
    /// <para>
    /// Nothing else is injected. No database context, no cache, no clock, no HTTP or request accessor, no
    /// ambient tenant snapshot and no service provider: an access decision that depended on any of those
    /// could differ between two callers asking the same question.
    /// </para>
    /// </remarks>
    public PermissionEvaluator(
        IPermissionRepository permissions,
        IRoleRepository roles,
        IModuleRepository modules,
        ITabRepository tabs,
        IOptions<PortalOptions> portalOptions)
    {
        ArgumentNullException.ThrowIfNull(portalOptions);

        _permissions = permissions ?? throw new ArgumentNullException(nameof(permissions));
        _roles = roles ?? throw new ArgumentNullException(nameof(roles));
        _modules = modules ?? throw new ArgumentNullException(nameof(modules));
        _tabs = tabs ?? throw new ArgumentNullException(nameof(tabs));

        PortalOptions options = portalOptions.Value
            ?? throw new ArgumentNullException(nameof(portalOptions));

        if (string.IsNullOrWhiteSpace(options.AllUsersRoleName))
        {
            throw new ArgumentException(
                "PortalOptions.AllUsersRoleName is blank. It is the name by which a caller declares that "
                + "it stands for every user, so a blank value would quietly match nothing.",
                nameof(portalOptions));
        }

        if (string.IsNullOrWhiteSpace(options.UnauthenticatedRoleName))
        {
            throw new ArgumentException(
                "PortalOptions.UnauthenticatedRoleName is blank. It is the name by which an anonymous "
                + "caller is recognised, so a blank value would quietly match nothing.",
                nameof(portalOptions));
        }

        _allUsersRoleName = options.AllUsersRoleName;
        _unauthenticatedRoleName = options.UnauthenticatedRoleName;
    }

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// The union spans every module and page of the tenant. Soft-deleted ones are excluded, because a
    /// grant on something the caller can no longer reach confers nothing.
    /// </para>
    /// <para>
    /// MIGRATION: this member exists instead of a loop over the two scoped reads, and the reason is the
    /// round-trip count: a portal carrying hundreds of pages and modules would cost hundreds of reads to
    /// answer one question. It is answered with a fixed five reads - the portal's roles, its modules, its
    /// pages, its module grants and its page grants - plus one catalogue lookup for each <em>distinct
    /// permission the caller actually reaches</em>. That last number is bounded by the catalogue, which
    /// the schema bounds, rather than by how much content the tenant holds, so the cost does not grow
    /// with the portal.
    /// </para>
    /// <para>
    /// The reduction is the same one the scoped members use, applied per scope, so a key this member
    /// reports for a module is exactly the key the module-scoped member reports for it.
    /// </para>
    /// </remarks>
    public async Task<IReadOnlyList<string>> ListEffectivePortalPermissionKeysAsync(
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

        // Live modules, indexed to their definitions: the definition bounds which catalogue entries apply
        // to a module, and it is needed again below when each reached grant is checked against its scope.
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
        // than from a navigation property. Whether the repository loaded that reference is the
        // repository's business, and a decision that silently returned "holds nothing" because a
        // reference happened to be unloaded would be an authorisation defect that no test of this type
        // could see.
        Dictionary<int, Permission> catalogue = await ReadCatalogueAsync(reached, cancellationToken)
            .ConfigureAwait(false);

        List<MatchedGrant> matched = new(reached.Count);
        foreach (ReachedGrant grant in reached)
        {
            Permission? entry = catalogue.GetValueOrDefault(grant.PermissionId);
            if (entry is null)
            {
                // The catalogue entry the grant names does not exist, so there is no key to confer. That
                // is a broken row rather than a denial, and failing closed is the only safe reading of it.
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

        return Names(Survivors(matched));
    }

    /// <inheritdoc />
    /// <remarks>
    /// Answered from the module's own grants alone. A module that defers its view permission to the pages
    /// it sits on is resolved by the application service, which composes this answer with the page
    /// answers; that composition is not performed here, because a module's grant rows are all this member
    /// is asked about.
    /// </remarks>
    public async Task<IReadOnlyList<string>> ListEffectiveModulePermissionKeysAsync(
        int moduleId,
        int? userId,
        IReadOnlyCollection<string> roleNames,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(roleNames);
        cancellationToken.ThrowIfCancellationRequested();

        List<MatchedGrant> matched = await CollectModuleGrantsAsync(
                moduleId,
                permissionKey: null,
                userId,
                roleNames,
                cancellationToken)
            .ConfigureAwait(false);

        return Names(Survivors(matched));
    }

    /// <inheritdoc />
    /// <remarks>The page counterpart of <see cref="ListEffectiveModulePermissionKeysAsync"/>.</remarks>
    public async Task<IReadOnlyList<string>> ListEffectiveTabPermissionKeysAsync(
        int tabId,
        int? userId,
        IReadOnlyCollection<string> roleNames,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(roleNames);
        cancellationToken.ThrowIfCancellationRequested();

        List<MatchedGrant> matched = await CollectTabGrantsAsync(
                tabId,
                permissionKey: null,
                userId,
                roleNames,
                cancellationToken)
            .ConfigureAwait(false);

        return Names(Survivors(matched));
    }

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// A caller with no reachable grant on the module holds nothing, so the answer is
    /// <see langword="false"/> rather than an error: an absent grant denies, which is the closed default
    /// and the same answer an explicit denial produces. A module that does not exist confers nothing for
    /// the same reason.
    /// </para>
    /// <para>
    /// Only the catalogue entries carrying the requested key are read, so the grants examined are exactly
    /// the grants that could confer it. The verdict is then the same reduction the listing above performs,
    /// asked whether the key survived.
    /// </para>
    /// </remarks>
    public async Task<bool> HasModulePermissionAsync(
        int moduleId,
        PermissionKey permissionKey,
        int? userId,
        IReadOnlyCollection<string> roleNames,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(roleNames);
        cancellationToken.ThrowIfCancellationRequested();

        List<MatchedGrant> matched = await CollectModuleGrantsAsync(
                moduleId,
                permissionKey,
                userId,
                roleNames,
                cancellationToken)
            .ConfigureAwait(false);

        return Holds(matched, permissionKey);
    }

    /// <inheritdoc />
    /// <remarks>The page counterpart of <see cref="HasModulePermissionAsync"/>, with the same rule.</remarks>
    public async Task<bool> HasTabPermissionAsync(
        int tabId,
        PermissionKey permissionKey,
        int? userId,
        IReadOnlyCollection<string> roleNames,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(roleNames);
        cancellationToken.ThrowIfCancellationRequested();

        List<MatchedGrant> matched = await CollectTabGrantsAsync(
                tabId,
                permissionKey,
                userId,
                roleNames,
                cancellationToken)
            .ConfigureAwait(false);

        return Holds(matched, permissionKey);
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
    /// <para>
    /// The reads happen in a fixed order, and the order is the point. The module is read first because it
    /// names the portal that role names must resolve within - which is what
    /// <see cref="BuildPrincipalAsync"/> then reads the roles of - and the definition that bounds its
    /// catalogue. The catalogue is read next and narrowed to the keys being asked about. The grants are
    /// read last, once per surviving catalogue entry, because that is the shape the repository contract
    /// offers: a grant reader taking a module and one permission.
    /// </para>
    /// <para>
    /// An unknown module yields nothing. That is the closed default rather than an error - this member is
    /// asked what a caller holds, and the answer for something that does not exist is "nothing". Reporting
    /// existence is the application service's job, and it does it before asking.
    /// </para>
    /// </remarks>
    private async Task<List<MatchedGrant>> CollectModuleGrantsAsync(
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
            return matched;
        }

        Principal principal = await BuildPrincipalAsync(module.PortalId, userId, roleNames, cancellationToken)
            .ConfigureAwait(false);

        IReadOnlyList<Permission> catalogue = await _permissions
            .GetByModuleIdAsync(moduleId, cancellationToken)
            .ConfigureAwait(false);

        HashSet<int> visited = new();

        foreach (Permission entry in catalogue)
        {
            if (!Applies(entry, permissionKey) || !IsModuleScoped(entry, module.ModuleDefinitionId))
            {
                continue;
            }

            if (entry.PermissionId == AnyPermissionId || !visited.Add(entry.PermissionId))
            {
                continue;
            }

            IReadOnlyList<ModulePermission> grants = await _permissions
                .GetModulePermissionsByModuleIdAsync(moduleId, entry.PermissionId, cancellationToken)
                .ConfigureAwait(false);

            foreach (ModulePermission grant in grants)
            {
                // The two equality checks re-assert the arguments just passed to the reader. Both of that
                // reader's arguments carry a documented wildcard, so a row that answers a wider question
                // than the one asked must not be judged as though it carried the requested key.
                if (grant.ModuleId != moduleId || grant.PermissionId != entry.PermissionId)
                {
                    continue;
                }

                if (Matches(grant.RoleId, grant.UserId, principal))
                {
                    matched.Add(new MatchedGrant(
                        PermissionScope.Module,
                        grant.ModuleId,
                        entry.PermissionKey,
                        grant.AllowAccess));
                }
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
    /// <remarks>
    /// The page counterpart of <see cref="CollectModuleGrantsAsync"/>, with two measured differences. The
    /// page catalogue is product-wide rather than definition-bound, so every page shares it and no
    /// definition narrows it. And the page grant reader treats only its permission argument as a wildcard,
    /// never its page argument, so the page half of the equality check below is defensive symmetry rather
    /// than a requirement - it is kept so that the two collectors read identically and neither can be
    /// tightened without the other.
    /// </remarks>
    private async Task<List<MatchedGrant>> CollectTabGrantsAsync(
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
            return matched;
        }

        Principal principal = await BuildPrincipalAsync(tab.PortalId, userId, roleNames, cancellationToken)
            .ConfigureAwait(false);

        IReadOnlyList<Permission> catalogue = await _permissions
            .GetByTabIdAsync(tabId, cancellationToken)
            .ConfigureAwait(false);

        HashSet<int> visited = new();

        foreach (Permission entry in catalogue)
        {
            if (!Applies(entry, permissionKey) || !IsTabScoped(entry))
            {
                continue;
            }

            if (entry.PermissionId == AnyPermissionId || !visited.Add(entry.PermissionId))
            {
                continue;
            }

            IReadOnlyList<TabPermission> grants = await _permissions
                .GetTabPermissionsByTabIdAsync(tabId, entry.PermissionId, cancellationToken)
                .ConfigureAwait(false);

            foreach (TabPermission grant in grants)
            {
                if (grant.TabId != tabId || grant.PermissionId != entry.PermissionId)
                {
                    continue;
                }

                if (Matches(grant.RoleId, grant.UserId, principal))
                {
                    matched.Add(new MatchedGrant(
                        PermissionScope.Tab,
                        grant.TabId,
                        entry.PermissionKey,
                        grant.AllowAccess));
                }
            }
        }

        return matched;
    }

    /// <summary>Reads the catalogue entry behind each distinct permission a set of grants names.</summary>
    /// <param name="reached">The grants whose permissions need resolving.</param>
    /// <param name="cancellationToken">Token observed for cancellation.</param>
    /// <returns>The entries that exist, indexed by permission identifier.</returns>
    /// <remarks>
    /// One read per <em>distinct</em> permission, never one per grant: a tenant records the same handful of
    /// permissions across all of its content, so resolving them per row would multiply the reads by the
    /// size of the portal for no additional information. An identifier naming no entry is simply absent
    /// from the result, and the caller treats that as conferring nothing.
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

        Dictionary<int, Permission> catalogue = new(permissionIds.Count);
        foreach (int permissionId in permissionIds)
        {
            Permission? entry = await _permissions
                .GetByIdAsync(permissionId, cancellationToken)
                .ConfigureAwait(false);

            if (entry is not null)
            {
                catalogue[permissionId] = entry;
            }
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
    /// <remarks>
    /// <para>
    /// MIGRATION: this reproduces <c>PortalSecurity.IsInRoles</c>
    /// (<c>Library/Components/Security/PortalSecurity.vb:L115-L136</c>) with the ambient state removed.
    /// The "All Users" name participates for every caller, matching the unconditional test at
    /// <c>L125</c>; the "Unauthenticated Users" name participates only while the caller is anonymous,
    /// matching the guarded test at <c>L124</c>. Blank names are discarded, which is the
    /// <c>role &lt;&gt; ""</c> guard at <c>L123</c> - the legacy string it split carried a leading
    /// delimiter and so always produced an empty first element.
    /// </para>
    /// <para>
    /// Names are compared <strong>exactly</strong>. Nothing is trimmed, folded or localised, because the
    /// value stored in <c>Roles.RoleName</c> is the value a grant was made against, and normalising the
    /// comparison here would make this type disagree with the store on installations whose collation does
    /// not.
    /// </para>
    /// <para>
    /// Every identifier a name resolves to is kept, <c>0</c> and negatives included. <c>Roles.RoleID</c>
    /// is <c>IDENTITY(0, 1)</c>, so <c>0</c> is the first role any installation creates, and discarding
    /// non-positive identifiers - a natural-looking guard - would silently revoke it.
    /// </para>
    /// <para>
    /// A host-level scope carries no portal, and resolves no named role as a result. That is deliberate
    /// and closed: with no portal there is no set of role names that can be resolved without reaching
    /// installation-wide, and reaching installation-wide is the cross-tenant escalation the contract
    /// forbids. Such a scope is still reachable through the "All Users", anonymous and account-scoped
    /// grants, none of which needs a role identifier at all.
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

        declared.Add(_allUsersRoleName);

        if (userId is null)
        {
            declared.Add(_unauthenticatedRoleName);
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
    /// <para>
    /// A grant names an account or a role, and the account takes precedence in the test because that is
    /// the order the legacy code tested in: <c>ModulePermissionController.vb:L37-L45</c> and
    /// <c>TabPermissionController.vb:L42-L50</c> both examined the account column first and consulted the
    /// role only when it held no account.
    /// </para>
    /// <para>
    /// MIGRATION: the legacy account test could not distinguish an account from an absence. It asked
    /// <c>Null.IsNull(UserID)</c>, and <c>Null.NullInteger</c> is <c>-1</c>, so a stored <c>-1</c> read as
    /// "no account". The terminal column is nullable, so absence is now <see langword="null"/> and every
    /// stored number is a real account. The legacy code then wrapped the account in brackets and matched
    /// it as though it were a role name - <c>IsInRoles("[" &amp; UserID &amp; "]")</c> - which is a
    /// workaround for having only a role-name comparison available, and no such synthetic name is
    /// reconstructed here.
    /// </para>
    /// <para>
    /// A role identifier of <c>-1</c> admits everyone, <c>-3</c> admits only an anonymous caller, and
    /// <c>-2</c> admits nobody for the reason given on <see cref="SuperUserRoleId"/>. Anything else must
    /// name a role the caller holds within the owning portal. <c>-4</c> - the legacy "Nothing" role -
    /// needs no special case: <c>Roles.RoleID</c> is <c>IDENTITY(0, 1)</c>, so it can never resolve from a
    /// role name and therefore reaches nobody, which is precisely what it asks for.
    /// </para>
    /// <para>
    /// A grant naming neither an account nor a role reaches nobody. Both columns became nullable in the
    /// same upgrade, so the combination is representable, and the closed reading is the only safe one.
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

    /// <summary>Reduces reached grants to the keys that survive allow-and-deny precedence.</summary>
    /// <param name="matched">
    /// Every grant the caller reached, in any order, possibly spanning several modules and pages. An empty
    /// sequence is legitimate and yields an empty result.
    /// </param>
    /// <returns>The surviving keys, without duplicates.</returns>
    /// <remarks>
    /// <para>
    /// A key survives when some grant allows it on a scope where no grant denies it. Denials are collected
    /// in full before any allowance is judged, in a separate pass, so the outcome cannot depend on the
    /// order the store happened to return rows in - which matters because a grant and a denial of the same
    /// key on the same scope is a legitimate configuration, and "whichever came first wins" would make
    /// authorisation depend on a query plan.
    /// </para>
    /// <para>
    /// Suppression is correlated to the scope carrying the denial, never applied globally, for the reason
    /// given in the type remarks.
    /// </para>
    /// <para>
    /// This is the only implementation of the rule in the solution. Both listing members return its
    /// output and both verdict members test membership in it, so a listing that offers an action the
    /// verdict then refuses is not merely unlikely, it is unrepresentable.
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

    /// <summary>Decides whether reached grants confer one particular key.</summary>
    /// <param name="matched">Every grant the caller reached for the scope being decided.</param>
    /// <param name="permissionKey">The key being asked about.</param>
    /// <returns><see langword="true"/> when the caller holds it.</returns>
    /// <remarks>
    /// Deliberately a membership test over <see cref="Survivors"/> rather than a rule of its own.
    /// Expressing the verdict a second time is how a verdict and a listing come to disagree, and a user
    /// offered an action that is then refused is a defect they experience and a test rarely catches.
    /// </remarks>
    private static bool Holds(IReadOnlyCollection<MatchedGrant> matched, PermissionKey permissionKey)
    {
        return Survivors(matched).Contains(permissionKey);
    }

    /// <summary>Renders surviving keys as the canonical strings the contract returns.</summary>
    /// <param name="keys">The surviving keys.</param>
    /// <returns>The key names, distinct, in a stable ordinal order.</returns>
    /// <remarks>
    /// MIGRATION: the name of a <see cref="PermissionKey"/> member <em>is</em> the stored spelling and the
    /// wire value, so rendering is <see cref="object.ToString"/> and nothing more - no upper-casing pass
    /// is required, because a member cannot be mis-cased. The ordering is not cosmetic: an access token
    /// minted twice from the same grants must carry an identical claim set both times, and a set's
    /// enumeration order is not a contract.
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
    /// <param name="permissionKey">The key to narrow to, or <see langword="null"/> to accept every key.</param>
    /// <returns><see langword="true"/> when the entry is in play.</returns>
    /// <remarks>
    /// MIGRATION: the comparison is between enumeration members, never between strings. The provider typed
    /// this column as text and the legacy controllers compared it with string equality
    /// (<c>ModulePermissionController.vb:L36</c>), which is what let mis-cased and untrimmed values become
    /// a hazard. A member is a closed value: it cannot be mis-cased, cannot be parsed from user input
    /// here, is never combined with another member, and is never reduced to its ordinal.
    /// </remarks>
    private static bool Applies(Permission entry, PermissionKey? permissionKey)
    {
        return permissionKey is not PermissionKey wanted || entry.PermissionKey == wanted;
    }

    /// <summary>Decides whether a catalogue entry belongs to the module scope being evaluated.</summary>
    /// <param name="entry">The catalogue entry.</param>
    /// <param name="moduleDefinitionId">The definition the module under evaluation was built from.</param>
    /// <returns><see langword="true"/> when the entry applies to that module.</returns>
    /// <remarks>
    /// <para>
    /// Two admissions, and both are needed. An entry carrying the product-wide module scope code applies
    /// to every module, and an entry declared by the module's own definition applies to that module. The
    /// second admission is why this is not a fixed list of known codes: every installed module contributes
    /// catalogue entries under a code of its own choosing, and a check that only recognised the shipped
    /// code would revoke every permission those modules define.
    /// </para>
    /// <para>
    /// The check re-asserts, in this file, the predicate the catalogue read already applied, and it is
    /// worth its keep for what it forecloses: nothing scoped to anything other than this module can enter
    /// a module decision. In particular the file-system permission scope, which this solution models no
    /// entity for and which the migration plan excludes entirely, cannot reach a module verdict through a
    /// catalogue entry - and it cannot reach a page verdict either, because <see cref="IsTabScoped"/>
    /// admits one code and only one.
    /// </para>
    /// <para>
    /// The code comparison is ordinal. Every occurrence of the two scope codes across the upgrade scripts
    /// is upper-case, so an exact comparison matches the shipped data, and where it would not, refusing is
    /// the closed direction to fail in.
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
    /// One code, exactly. The terminal page catalogue procedure filters on the product-wide page scope code
    /// and on nothing else - it does not even reference the page it is passed - so every page shares one
    /// vocabulary and no definition widens it. Keeping the module and page scopes apart is what stops a
    /// grant recorded in one from being read as the other, which matters because both scope identifiers
    /// seed at <c>0</c> and an identifier alone does not say what it identifies.
    /// </remarks>
    private static bool IsTabScoped(Permission entry)
    {
        return string.Equals(entry.PermissionCode, TabScopeCode, StringComparison.Ordinal);
    }

    /// <summary>Distinguishes the two kinds of thing a permission grant can be recorded against.</summary>
    /// <remarks>
    /// It exists so that module grants and page grants can travel in one sequence without their
    /// identifiers colliding: <c>Modules.ModuleID</c> and <c>Tabs.TabID</c> both seed at <c>0</c>, so an
    /// identifier on its own does not say what it identifies, and a denial on page 4 must not suppress a
    /// key on module 4.
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
    /// The account identifier, or <see langword="null"/> for an anonymous caller. It carries no sentinel
    /// meaning: every number here is a real account.
    /// </param>
    /// <param name="RoleIds">
    /// The identifiers the caller's declared role names resolved to within the owning portal, including
    /// <c>0</c> and any negative identifier a genuine role happens to hold.
    /// </param>
    private readonly record struct Principal(int? UserId, HashSet<int> RoleIds)
    {
        /// <summary>Gets a value indicating whether the caller has not signed in.</summary>
        /// <remarks>
        /// An absent account identifier is the anonymous caller, which is what the contract documents its
        /// nullable account parameter to mean. This is the flag the legacy code read from the ambient web
        /// request's authentication property, derived from the argument instead.
        /// </remarks>
        internal bool IsAnonymous => UserId is null;
    }

    /// <summary>A grant that reached the caller, before its permission has been resolved to a key.</summary>
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
    /// <param name="ScopeId">
    /// The module or page the grant was recorded against. It travels with the grant so that suppression
    /// stays correlated to the scope carrying the denial even when several scopes are in one sequence.
    /// </param>
    /// <param name="Key">The key the grant confers or denies.</param>
    /// <param name="AllowAccess"><see langword="true"/> allows, <see langword="false"/> denies.</param>
    private readonly record struct MatchedGrant(
        PermissionScope Scope,
        int ScopeId,
        PermissionKey Key,
        bool AllowAccess);
}
