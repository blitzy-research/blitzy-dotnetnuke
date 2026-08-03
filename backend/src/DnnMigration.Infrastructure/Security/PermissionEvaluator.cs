using DnnMigration.Application.Abstractions;
using DnnMigration.Domain.Entities;
using DnnMigration.Domain.Enums;
using DnnMigration.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace DnnMigration.Infrastructure.Security;

/// <summary>
/// One applicable permission grant, reduced to the three facts a decision depends on.
/// </summary>
/// <param name="ScopeKind">
/// Which kind of thing the grant was made against - see <see cref="PermissionScopeKind"/>. It exists so
/// that module grants and page grants can travel in one sequence without their identifiers colliding:
/// both <c>Modules.ModuleID</c> and <c>Tabs.TabID</c> seed at 0, so an identifier alone does not say
/// what it identifies.
/// </param>
/// <param name="ScopeId">
/// Identifier of the module or page the grant was made against. Carries no sentinel meaning: 0 is a real
/// identifier for both, and -1 is a real portal identifier, so nothing here may be read as "absent".
/// </param>
/// <param name="PermissionKey">
/// The permission key exactly as stored in <c>Permission.PermissionKey</c>. Compared
/// case-insensitively, because the legacy column is <c>varchar</c> under a case-insensitive collation
/// and the shipped rows are not consistently cased.
/// </param>
/// <param name="AllowAccess">
/// The value of the grant's <c>AllowAccess bit NOT NULL</c> column: <see langword="true"/> allows,
/// <see langword="false"/> denies.
/// </param>
internal readonly record struct PermissionGrant(
    PermissionScopeKind ScopeKind,
    int ScopeId,
    string PermissionKey,
    bool AllowAccess);

/// <summary>Distinguishes the two kinds of thing a permission grant can be made against.</summary>
internal enum PermissionScopeKind
{
    /// <summary>The grant was made against a module instance, in <c>ModulePermission</c>.</summary>
    Module = 0,

    /// <summary>The grant was made against a page, in <c>TabPermission</c>.</summary>
    Tab = 1,
}

/// <summary>
/// The single place in this solution where allow-and-deny precedence over permission grants is decided.
/// </summary>
/// <remarks>
/// <para>
/// MIGRATION: this supersedes logic that the legacy codebase duplicated across three controllers -
/// <c>PermissionController.vb</c>, <c>ModulePermissionController.vb</c> and
/// <c>TabPermissionController.vb</c>, carrying 9, 18 and 15 public members respectively - each of which
/// re-derived the same rules over its own grant table. Centralising them here is the whole point:
/// <em>two evaluators that can disagree is the worst available result in this area</em>, because the
/// disagreement shows up as an intermittent authorisation defect rather than as a failure.
/// </para>
/// <para>
/// <strong>Where the boundary lies.</strong> This type owns the whole of evaluation: it establishes
/// <em>which grants a caller reaches</em> - a retrieval question that the database must answer because
/// it needs the role table - and then decides <em>what the caller consequently holds</em>.
/// <c>PermissionRepository</c> deliberately does neither. That contract mirrors the legacy provider
/// blocks at core <c>DataProvider.vb</c> L279-L308 and is pure persistence: it reads and writes rows
/// and returns no verdict, so an access question can only be asked here. Both effective-key reads and
/// both single-key verdicts run through <see cref="Reduce"/>, so a verdict and a listing are incapable
/// of contradicting one another: <see cref="Holds"/> is defined as a membership test against the very
/// set <see cref="Reduce"/> returns rather than as a second rule that happens to agree today.
/// </para>
/// <para>
/// <strong>Deny beats allow, within a scope.</strong> A denying grant suppresses its key on the module or
/// page it was made against, even when another grant allows the same key there. It does not suppress that
/// key elsewhere. The legacy string builders at <c>ModulePermissionController.vb:L243</c> and
/// <c>TabPermissionController.vb:L218</c> filtered on the allow flag being set and so discarded deny rows
/// altogether, but they were assembling a value for a permission grid rather than deciding access, which
/// is why their behaviour is not the behaviour reproduced here.
/// </para>
/// <para>
/// <strong>Why suppression is scoped rather than global.</strong> The portal-wide read exists to answer
/// "which sections may this caller be offered", and it spans every module and page of a tenant. Applying
/// a denial across that whole union would let one obscure denial on one forgotten page remove a key the
/// caller genuinely holds everywhere else - strictly harsher than any legacy behaviour and wrong for the
/// question being asked. Suppression is therefore correlated to the scope that carries the denial, which
/// is also exactly what the two scoped reads do, so the three reads share one rule rather than two.
/// </para>
/// <para>
/// <strong>Pseudo-roles are sentinel identifiers, not rows.</strong> The constants published here are the
/// measured legacy values from <c>Library/Components/Shared/Globals.vb</c> lines 95 to 98, and the write
/// path that produces them is <c>TabController.vb:L901-L904</c> and
/// <c>ModuleController.vb:L354-L357</c>, both of which map a display name onto a negative identifier
/// before storing it. This is why neither grant table has ever carried a foreign key to <c>Roles</c>, and
/// why the 04.05.00 script could make <c>RoleID</c> nullable: a sentinel identifies no role row. They are
/// published here, rather than restated in the repository, so that the query which selects reachable
/// grants and the rule which interprets them cannot drift apart. <c>Roles.RoleID</c> is
/// <c>IDENTITY(0, 1)</c>, so no genuine role can ever collide with a negative sentinel.
/// </para>
/// <para>
/// The precedence arithmetic below is stateless, but the type resolves grants through the unit-of-work
/// scoped database context and is therefore registered per request rather than as a singleton.
/// </para>
/// </remarks>
internal sealed class PermissionEvaluator : IPermissionEvaluator
{
    /// <summary>
    /// The sentinel role identifier that admits every caller, authenticated or not.
    /// </summary>
    /// <remarks>
    /// MIGRATION: <c>glbRoleAllUsers = "-1"</c> at <c>Library/Components/Shared/Globals.vb:L95</c>,
    /// displayed as "All Users". <c>PortalSecurity.IsInRoles</c> admitted it unconditionally at line 125.
    /// </remarks>
    public const int AllUsersRoleId = -1;

    /// <summary>
    /// The sentinel role identifier reserved for host accounts, deliberately never matched here.
    /// </summary>
    /// <remarks>
    /// MIGRATION: <c>glbRoleSuperUser = "-2"</c> at <c>Library/Components/Shared/Globals.vb:L96</c>. It is
    /// published for completeness and for the reader who finds a -2 in a grant row, but no rule below
    /// matches it. A superuser is admitted before any grant is examined - which is what
    /// <c>PortalSecurity.IsInRoles</c> did at line 123, testing <c>IsSuperUser</c> first and returning
    /// true without looking at a single role - so the only caller this sentinel could serve has already
    /// been answered. Matching it here would be dead code that looked like a security rule.
    /// </remarks>
    public const int SuperUserRoleId = -2;

    /// <summary>
    /// The sentinel role identifier that admits only callers who have not signed in.
    /// </summary>
    /// <remarks>
    /// MIGRATION: <c>glbRoleUnauthUser = "-3"</c> at <c>Library/Components/Shared/Globals.vb:L97</c>,
    /// displayed as "Unauthenticated Users". <c>PortalSecurity.IsInRoles</c> admitted it only when
    /// <c>context.Request.IsAuthenticated</c> was false, at line 124 - so a grant to this sentinel is
    /// genuinely narrower than a grant to every user, and the two are not interchangeable.
    /// </remarks>
    public const int UnauthenticatedRoleId = -3;

    /// <summary>
    /// The sentinel role identifier that admits nobody.
    /// </summary>
    /// <remarks>
    /// MIGRATION: <c>glbRoleNothing = "-4"</c> at <c>Library/Components/Shared/Globals.vb:L98</c>. No rule
    /// matches it, which is the correct treatment rather than an omission: a grant carrying it reaches no
    /// caller, so leaving it unmatched is precisely what it asks for.
    /// </remarks>
    public const int NoRoleId = -4;

    private readonly DnnDbContext _context;

    /// <summary>Initialises a new instance of the <see cref="PermissionEvaluator"/> class.</summary>
    /// <param name="context">The unit-of-work scoped database context grants are resolved through.</param>
    /// <exception cref="ArgumentNullException"><paramref name="context"/> is <see langword="null"/>.</exception>
    public PermissionEvaluator(DnnDbContext context)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
    }

    /// <inheritdoc />
    /// <remarks>
    /// The union spans every module and page of the tenant, and the deleted ones are excluded because a
    /// grant on something a caller can no longer reach confers nothing. Role names are resolved against
    /// the portal directly here, rather than through the owning module or page as the two scoped reads
    /// do, because the portal <em>is</em> the scope in this case.
    /// </remarks>
    public async Task<IReadOnlyList<string>> ListEffectivePortalPermissionKeysAsync(
        int portalId,
        int? userId,
        IReadOnlyCollection<string> roleNames,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(roleNames);

        IReadOnlyList<string> wantedRoles = NormaliseRoleNames(roleNames);
        int? caller = userId;

        IQueryable<int> portalRoleIds = _context.Roles
            .Where(r => r.PortalId == portalId && wantedRoles.Contains(r.RoleName.ToLower()))
            .Select(r => r.RoleId);

        IQueryable<ModulePermission> moduleGrants = _context.ModulePermissions
            .Where(p => p.Module!.PortalId == portalId && !p.Module.IsDeleted)
            .Where(p =>
                (caller != null && p.UserId != null && p.UserId == caller)
                || p.RoleId == AllUsersRoleId
                || (caller == null && p.RoleId == UnauthenticatedRoleId)
                || (p.RoleId != null && portalRoleIds.Contains(p.RoleId.Value)));

        IQueryable<TabPermission> tabGrants = _context.TabPermissions
            .Where(p => p.Tab!.PortalId == portalId && !p.Tab.IsDeleted)
            .Where(p =>
                (caller != null && p.UserId != null && p.UserId == caller)
                || p.RoleId == AllUsersRoleId
                || (caller == null && p.RoleId == UnauthenticatedRoleId)
                || (p.RoleId != null && portalRoleIds.Contains(p.RoleId.Value)));

        // Both projections carry a scope discriminator, so the two sets travel in a single union without
        // their identifiers colliding - both Modules.ModuleID and Tabs.TabID seed at 0, so an identifier
        // alone does not say what it identifies. One statement therefore serves a whole tenant, which is
        // the entire reason this member exists rather than a loop over the two scoped reads.
        //
        // The union is deliberately taken over rows of plain columns rather than over the PermissionGrant
        // projection the two scoped reads use. Constructing a domain-shaped value is a client projection,
        // and a relational set operation cannot be translated once one has been applied, so unioning the
        // projected sequences fails at execution rather than at compile time. Taking the union first and
        // shaping afterwards keeps the whole read in one statement; shaping first and unioning afterwards
        // does not, and issuing two statements would abandon the single-round-trip property outright.
        var rows = await moduleGrants
            .Select(p => new
            {
                ScopeKind = (int)PermissionScopeKind.Module,
                ScopeId = p.ModuleId,
                PermissionKey = p.Permission!.PermissionKey,
                p.AllowAccess,
            })
            .Union(tabGrants.Select(p => new
            {
                ScopeKind = (int)PermissionScopeKind.Tab,
                ScopeId = p.TabId,
                PermissionKey = p.Permission!.PermissionKey,
                p.AllowAccess,
            }))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        // MIGRATION: Permission.PermissionKey is the closed PermissionKey enumeration and the column
        // stores its member name, so the union carries the enumeration and the name is taken here,
        // client side, once the rows have landed. PermissionGrant deliberately keeps a string: it
        // models what the row said, and the precedence rule below is what decides what an
        // unrecognised key means.
        List<PermissionGrant> grants = rows.ConvertAll(row => new PermissionGrant(
            (PermissionScopeKind)row.ScopeKind,
            row.ScopeId,
            row.PermissionKey.ToString(),
            row.AllowAccess));

        return Reduce(grants);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<string>> ListEffectiveModulePermissionKeysAsync(
        int moduleId,
        int? userId,
        IReadOnlyCollection<string> roleNames,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(roleNames);

        // One round trip fetches the reachable grants, projected to the three facts a decision depends
        // on; the precedence rule is then applied below, which is the only place it exists.
        List<PermissionGrant> grants = await ProjectModuleGrants(ApplicableModuleGrants(moduleId, userId, roleNames))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return Reduce(grants);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<string>> ListEffectiveTabPermissionKeysAsync(
        int tabId,
        int? userId,
        IReadOnlyCollection<string> roleNames,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(roleNames);

        List<PermissionGrant> grants = await ProjectTabGrants(ApplicableTabGrants(tabId, userId, roleNames))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return Reduce(grants);
    }

    /// <inheritdoc />
    /// <remarks>
    /// A caller with no reachable grant on the module holds nothing, so the answer is false rather than
    /// an error: an absent grant denies, which is the closed default and the same answer an explicit
    /// denial produces.
    /// </remarks>
    public async Task<bool> HasModulePermissionAsync(
        int moduleId,
        PermissionKey permissionKey,
        int? userId,
        IReadOnlyCollection<string> roleNames,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(roleNames);

        // MIGRATION: the key is compared as the enumeration itself rather than as lower-cased text.
        // The property is a closed enumeration mapped to the varchar column by a string conversion, so
        // the provider compares the canonical member name against the column and the comparison stays
        // case-insensitive by virtue of the database collation - which is exactly what the previous
        // explicit lower-casing was reproducing, without it a query could no longer index-seek.
        List<PermissionGrant> grants = await ProjectModuleGrants(
                ApplicableModuleGrants(moduleId, userId, roleNames)
                    .Where(p => p.Permission!.PermissionKey == permissionKey))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return Holds(grants, permissionKey);
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

        // MIGRATION: the same enumeration comparison as HasModulePermissionAsync, for the same reason.
        List<PermissionGrant> grants = await ProjectTabGrants(
                ApplicableTabGrants(tabId, userId, roleNames)
                    .Where(p => p.Permission!.PermissionKey == permissionKey))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return Holds(grants, permissionKey);
    }

    /// <summary>
    /// Reduces a caller's reachable grants to the distinct permission keys the caller consequently holds.
    /// </summary>
    /// <param name="grants">
    /// Every grant the caller reaches, in any order, possibly spanning several modules and pages. An
    /// empty sequence is legitimate and yields an empty result.
    /// </param>
    /// <returns>
    /// The surviving keys, upper-cased, without duplicates, in a stable ordinal order. Upper-casing
    /// matches the closed <see cref="PermissionKey"/> enumeration and the strings the Angular
    /// <c>hasPermission</c> directive matches on; the ordering exists so that a token minted twice from
    /// the same grants carries an identical claim set both times.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="grants"/> is <see langword="null"/>.</exception>
    /// <remarks>
    /// A key survives when some grant allows it on a scope where no grant denies it. Suppression is
    /// evaluated per scope, for the reason given in the type remarks, so a denial on one page cannot
    /// remove a key held on another. An empty result is a positive answer - the caller holds nothing here
    /// - and never signals an error.
    /// </remarks>
    public IReadOnlyList<string> Reduce(IEnumerable<PermissionGrant> grants)
    {
        ArgumentNullException.ThrowIfNull(grants);

        // Collect the denials first, because a denial has to be known before any allowance on the same
        // scope can be judged, and the sequence may present them in any order.
        HashSet<(PermissionScopeKind Kind, int Id, string Key)> denied = new();
        List<PermissionGrant> allowances = new();

        foreach (PermissionGrant grant in grants)
        {
            string key = NormaliseKey(grant.PermissionKey);

            if (key.Length == 0)
            {
                continue;
            }

            if (grant.AllowAccess)
            {
                allowances.Add(grant);
            }
            else
            {
                denied.Add((grant.ScopeKind, grant.ScopeId, key));
            }
        }

        HashSet<string> held = new(StringComparer.Ordinal);

        foreach (PermissionGrant allowance in allowances)
        {
            string key = NormaliseKey(allowance.PermissionKey);

            if (!denied.Contains((allowance.ScopeKind, allowance.ScopeId, key)))
            {
                held.Add(key);
            }
        }

        List<string> ordered = held.ToList();
        ordered.Sort(StringComparer.Ordinal);

        return ordered;
    }

    /// <summary>
    /// Decides whether a caller's reachable grants confer one particular permission.
    /// </summary>
    /// <param name="grants">Every grant the caller reaches for the scope being decided.</param>
    /// <param name="permissionKey">The permission being asked about.</param>
    /// <returns>
    /// <see langword="true"/> when the caller holds it; otherwise <see langword="false"/>. A caller with
    /// no reachable grant holds nothing, so an absent grant denies - the closed default, and the same
    /// answer an explicit denial produces.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="grants"/> is <see langword="null"/>.</exception>
    /// <remarks>
    /// Deliberately defined as a membership test over <see cref="Reduce"/> rather than as a rule of its
    /// own. Expressing the verdict twice - once for listings and once for decisions - is how the two
    /// come to disagree, and a listing that offers an action the decision then refuses is a defect a user
    /// experiences and a test rarely catches.
    /// </remarks>
    public bool Holds(IEnumerable<PermissionGrant> grants, PermissionKey permissionKey)
    {
        ArgumentNullException.ThrowIfNull(grants);

        string wanted = NormaliseKey(permissionKey.ToString());

        return Reduce(grants).Contains(wanted, StringComparer.Ordinal);
    }

    /// <summary>
    /// Reduces a caller's role names to the distinct, lower-cased set a grant query may be matched on.
    /// </summary>
    /// <param name="roleNames">The caller's role names, in any order and possibly containing blanks.</param>
    /// <returns>The distinct lower-cased names, with blanks discarded.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="roleNames"/> is <see langword="null"/>.</exception>
    /// <remarks>
    /// Blank names are discarded, which reproduces the <c>role &lt;&gt; ""</c> guard
    /// <c>PortalSecurity.IsInRoles</c> applied at line 123 - it had to, because it split a
    /// semicolon-delimited string whose legacy form carried a leading delimiter and therefore always
    /// produced an empty first element. Lower-casing happens here rather than being left to the database
    /// collation so that the comparison behaves identically on a case-sensitive installation.
    /// </remarks>
    public IReadOnlyList<string> NormaliseRoleNames(IReadOnlyCollection<string> roleNames)
    {
        ArgumentNullException.ThrowIfNull(roleNames);

        HashSet<string> distinct = new(StringComparer.Ordinal);

        foreach (string name in roleNames)
        {
            if (!string.IsNullOrWhiteSpace(name))
            {
                distinct.Add(name.Trim().ToLowerInvariant());
            }
        }

        return distinct.ToList();
    }

    /// <summary>Brings a permission key into the one casing every comparison here uses.</summary>
    /// <param name="permissionKey">The key as stored, or as named by the caller.</param>
    /// <returns>The trimmed, upper-cased key, or an empty string when nothing was supplied.</returns>
    private static string NormaliseKey(string? permissionKey)
    {
        return string.IsNullOrWhiteSpace(permissionKey)
            ? string.Empty
            : permissionKey.Trim().ToUpperInvariant();
    }

    /// <summary>Projects module grants to the three facts a permission decision depends on.</summary>
    /// <param name="grants">The reachable module grants.</param>
    /// <returns>A projection carrying the module scope discriminator.</returns>
    /// <remarks>
    /// Only three columns cross, and the stored allow-or-deny flag crosses unaltered: the projection
    /// narrows the row without interpreting it, because interpreting it is the precedence rule's job.
    /// The grant's own module identifier travels with it so that suppression stays correlated to the
    /// scope that carries the denial even when several modules are in the same result.
    /// </remarks>
    private static IQueryable<PermissionGrant> ProjectModuleGrants(IQueryable<ModulePermission> grants)
    {
        // MIGRATION: the key crosses as the enumeration member's name. The column already holds that
        // name - the mapping applies a string conversion rather than an ordinal one - and constructing
        // PermissionGrant is a client projection over the materialised rows, so the name is taken after
        // the column has been read and the emitted SQL still selects the same three columns.
        return grants.Select(p => new PermissionGrant(
            PermissionScopeKind.Module,
            p.ModuleId,
            p.Permission!.PermissionKey.ToString(),
            p.AllowAccess));
    }

    /// <summary>Projects page grants to the three facts a permission decision depends on.</summary>
    /// <param name="grants">The reachable page grants.</param>
    /// <returns>A projection carrying the page scope discriminator.</returns>
    private static IQueryable<PermissionGrant> ProjectTabGrants(IQueryable<TabPermission> grants)
    {
        // MIGRATION: as ProjectModuleGrants, the key crosses as the enumeration member's name.
        return grants.Select(p => new PermissionGrant(
            PermissionScopeKind.Tab,
            p.TabId,
            p.Permission!.PermissionKey.ToString(),
            p.AllowAccess));
    }

    /// <summary>Selects the grants on one module that the given caller reaches.</summary>
    /// <param name="moduleId">The module being evaluated.</param>
    /// <param name="userId">The caller's account identifier, or <see langword="null"/> when anonymous.</param>
    /// <param name="roleNames">The caller's role names.</param>
    /// <returns>The reachable grants, allowing and denying alike.</returns>
    /// <remarks>
    /// Four ways a grant reaches a caller, and they are alternatives rather than a hierarchy: it names the
    /// account directly, it names the "All Users" sentinel, it names the "Unauthenticated Users" sentinel
    /// while the caller is anonymous, or it names a role the caller holds within the portal that owns the
    /// module. Denying grants are selected too - they have to be, because a key that is never seen cannot
    /// be suppressed.
    /// <para>
    /// The role lookup is correlated to the owning portal, which is what keeps one tenant's
    /// "Administrators" grants from reaching another tenant's answer. A host-level module carries a null
    /// portal, and relational equality never matches null, so it resolves no named role and fails closed.
    /// </para>
    /// </remarks>
    private IQueryable<ModulePermission> ApplicableModuleGrants(int moduleId, int? userId, IReadOnlyCollection<string> roleNames)
    {
        IReadOnlyList<string> wantedRoles = NormaliseRoleNames(roleNames);
        int? caller = userId;

        IQueryable<int> scopedRoleIds = _context.Roles
            .Where(r => wantedRoles.Contains(r.RoleName.ToLower())
                && _context.Modules.Any(m => m.ModuleId == moduleId && m.PortalId == r.PortalId))
            .Select(r => r.RoleId);

        return _context.ModulePermissions
            .Where(p => p.ModuleId == moduleId)
            .Where(p =>
                (caller != null && p.UserId != null && p.UserId == caller)
                || p.RoleId == AllUsersRoleId
                || (caller == null && p.RoleId == UnauthenticatedRoleId)
                || (p.RoleId != null && scopedRoleIds.Contains(p.RoleId.Value)));
    }

    /// <summary>Selects the grants on one page that the given caller reaches.</summary>
    /// <param name="tabId">The page being evaluated.</param>
    /// <param name="userId">The caller's account identifier, or <see langword="null"/> when anonymous.</param>
    /// <param name="roleNames">The caller's role names.</param>
    /// <returns>The reachable grants, allowing and denying alike.</returns>
    /// <remarks>The page counterpart of <see cref="ApplicableModuleGrants"/>, with the same four rules.</remarks>
    private IQueryable<TabPermission> ApplicableTabGrants(int tabId, int? userId, IReadOnlyCollection<string> roleNames)
    {
        IReadOnlyList<string> wantedRoles = NormaliseRoleNames(roleNames);
        int? caller = userId;

        IQueryable<int> scopedRoleIds = _context.Roles
            .Where(r => wantedRoles.Contains(r.RoleName.ToLower())
                && _context.Tabs.Any(t => t.TabId == tabId && t.PortalId == r.PortalId))
            .Select(r => r.RoleId);

        return _context.TabPermissions
            .Where(p => p.TabId == tabId)
            .Where(p =>
                (caller != null && p.UserId != null && p.UserId == caller)
                || p.RoleId == AllUsersRoleId
                || (caller == null && p.RoleId == UnauthenticatedRoleId)
                || (p.RoleId != null && scopedRoleIds.Contains(p.RoleId.Value)));
    }
}
