using DnnMigration.Domain.Abstractions.Repositories;
using DnnMigration.Domain.Entities;
using DnnMigration.Domain.Enums;
using DnnMigration.Infrastructure.Persistence;
using DnnMigration.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;

namespace DnnMigration.Infrastructure.Repositories;

/// <summary>
/// Reads the permission catalogue and evaluates the <see cref="ModulePermission"/> and
/// <see cref="TabPermission"/> grants that decide what a caller may do.
/// </summary>
/// <remarks>
/// MIGRATION: replaces the thirty-three permission procedures reached from the legacy core data
/// provider and the data-access halves of <c>PermissionController.vb</c>,
/// <c>ModulePermissionController.vb</c> and <c>TabPermissionController.vb</c> - nine, eighteen and
/// fifteen public members respectively, plus six reflection-hydrator call sites - behind one contract.
/// <para>
/// <strong>Pseudo-role sentinels.</strong> Neither grant table has a foreign key to <c>Roles</c>, and
/// that is deliberate in the legacy schema rather than an omission: a grant's <c>RoleID</c> may hold a
/// sentinel that has no <c>Roles</c> row at all. <c>Library/Components/Shared/Globals.vb</c> lines 95
/// to 102 declare them - <c>-1</c> is "All Users", <c>-2</c> is "Superuser", <c>-3</c> is
/// "Unauthenticated Users" and <c>-4</c> is a deliberate non-match - and
/// <c>Library/Components/Tabs/TabController.vb</c> lines 901 to 904 together with
/// <c>Library/Components/Modules/ModuleController.vb</c> lines 354 to 357 show the write path mapping
/// those names onto those numbers. Resolving the caller's role names against <c>Roles</c> alone would
/// therefore silently discard every public and every anonymous grant, denying access the legacy
/// application granted. The sentinels are honoured here from the caller's authentication state, which
/// reproduces <c>PortalSecurity.IsInRoles</c> at
/// <c>Library/Components/Security/PortalSecurity.vb</c> lines 115 to 136 exactly: "All Users" admits
/// every caller unconditionally, and "Unauthenticated Users" admits a caller only while it is
/// unauthenticated.
/// </para>
/// <para>
/// <strong>Superuser.</strong> The legacy check short-circuits to allowed for a host account before it
/// examines a single role. That decision belongs to the caller and is taken there, which is why no
/// member of this repository accepts a superuser flag. A grant held against the <c>-2</c> sentinel is
/// consequently never matched here, and it does not need to be: the only caller it could apply to has
/// already been answered. <c>Roles.RoleID</c> is <c>IDENTITY(0, 1)</c>, so no genuine role can ever
/// collide with a negative sentinel.
/// </para>
/// <para>
/// <strong>Tenant isolation.</strong> Role names are resolved only within the portal that owns the
/// module or page being evaluated. Names are unique per portal, not per installation, so resolving
/// them installation-wide would let a grant to one tenant's "Administrators" role be honoured for
/// another tenant's. A host-level module or page carries a null portal and therefore resolves no
/// named role at all, which fails closed.
/// </para>
/// <para>
/// <strong>Precedence.</strong> A denying entry suppresses the key it names even when another entry
/// allows it, which is the legacy precedence. Suppression is scoped: a denial recorded against one
/// module suppresses that key on that module and nowhere else.
/// </para>
/// </remarks>
internal sealed class PermissionRepository : IPermissionRepository
{
    /// <summary>
    /// The sentinel role identifier that grants to every caller, taken from the one place it is defined.
    /// </summary>
    /// <remarks>
    /// Aliased rather than restated so that the query which selects reachable grants and the rule which
    /// interprets them can never drift apart. The measured origin is documented on
    /// <see cref="PermissionEvaluator.AllUsersRoleId"/>.
    /// </remarks>
    private const int AllUsersRoleId = PermissionEvaluator.AllUsersRoleId;

    /// <summary>The sentinel role identifier that grants only to an unauthenticated caller.</summary>
    /// <remarks>See <see cref="PermissionEvaluator.UnauthenticatedRoleId"/> for the measured origin.</remarks>
    private const int UnauthenticatedRoleId = PermissionEvaluator.UnauthenticatedRoleId;

    private readonly DnnDbContext _context;
    private readonly PermissionEvaluator _evaluator;

    /// <summary>Initialises a new instance of the <see cref="PermissionRepository"/> class.</summary>
    /// <param name="context">The unit-of-work scoped database context.</param>
    /// <param name="evaluator">
    /// The single authority on allow-and-deny precedence. This repository establishes <em>which grants a
    /// caller reaches</em> - a retrieval question, answered by the database because it needs the role
    /// table - and then asks the evaluator <em>what the caller consequently holds</em>. The precedence
    /// rule is deliberately not restated here: two evaluators that can disagree is the worst available
    /// result in this area, because the disagreement presents as an intermittent authorisation defect
    /// rather than as a failure.
    /// </param>
    /// <exception cref="ArgumentNullException">Either argument is <see langword="null"/>.</exception>
    public PermissionRepository(DnnDbContext context, PermissionEvaluator evaluator)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _evaluator = evaluator ?? throw new ArgumentNullException(nameof(evaluator));
    }

    /// <inheritdoc />
    /// <remarks>
    /// The two filters compose conjunctively, and both are optional, so an unfiltered call returns the
    /// whole catalogue. The scope code is compared case-insensitively because
    /// <c>Permission.PermissionCode</c> is a free-text <c>varchar</c> column rather than a constrained
    /// one - the shipped rows are not consistently cased, and a case-sensitive comparison would silently
    /// return nothing.
    /// </remarks>
    public async Task<IReadOnlyList<Permission>> ListAsync(
        string? permissionCode,
        int? moduleDefinitionId,
        CancellationToken cancellationToken = default)
    {
        IQueryable<Permission> query = _context.Permissions;

        if (!string.IsNullOrWhiteSpace(permissionCode))
        {
            string wanted = permissionCode.Trim().ToLowerInvariant();
            query = query.Where(p => p.PermissionCode.ToLower() == wanted);
        }

        if (moduleDefinitionId.HasValue)
        {
            int definition = moduleDefinitionId.Value;
            query = query.Where(p => p.ModuleDefinitionId == definition);
        }

        return await query
            .OrderBy(p => p.PermissionCode)
            .ThenBy(p => p.PermissionKey)
            .ThenBy(p => p.PermissionId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task<Permission?> GetAsync(int permissionId, CancellationToken cancellationToken = default)
    {
        return _context.Permissions
            .FirstOrDefaultAsync(p => p.PermissionId == permissionId, cancellationToken);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Every grant on the module is returned, allowing and denying alike, with the permission it names
    /// and the role or account it was made to loaded alongside. This is the grid-shaped read: a caller
    /// presenting a permission matrix needs the rows themselves, not the decision they add up to. The
    /// role reference is null for a grant held against a pseudo-role sentinel, which is a legitimate row
    /// and not a broken one.
    /// </remarks>
    public async Task<IReadOnlyList<ModulePermission>> ListModulePermissionsAsync(int moduleId, CancellationToken cancellationToken = default)
    {
        return await _context.ModulePermissions
            .Include(p => p.Permission)
            .Include(p => p.Role)
            .Include(p => p.User)
            .Where(p => p.ModuleId == moduleId)
            .OrderBy(p => p.PermissionId)
            .ThenBy(p => p.RoleId)
            .ThenBy(p => p.UserId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    /// <remarks>The page counterpart of <see cref="ListModulePermissionsAsync"/>, with the same shape.</remarks>
    public async Task<IReadOnlyList<TabPermission>> ListTabPermissionsAsync(int tabId, CancellationToken cancellationToken = default)
    {
        return await _context.TabPermissions
            .Include(p => p.Permission)
            .Include(p => p.Role)
            .Include(p => p.User)
            .Where(p => p.TabId == tabId)
            .OrderBy(p => p.PermissionId)
            .ThenBy(p => p.RoleId)
            .ThenBy(p => p.UserId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
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
        // on; the precedence rule is then applied by the evaluator, which is the only place it exists.
        List<PermissionGrant> grants = await ProjectModuleGrants(ApplicableModuleGrants(moduleId, userId, roleNames))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return _evaluator.Reduce(grants);
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

        return _evaluator.Reduce(grants);
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

        IReadOnlyList<string> wantedRoles = _evaluator.NormaliseRoleNames(roleNames);
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
        // models what the row said, and the evaluator is the component that decides what an
        // unrecognised key means.
        List<PermissionGrant> grants = rows.ConvertAll(row => new PermissionGrant(
            (PermissionScopeKind)row.ScopeKind,
            row.ScopeId,
            row.PermissionKey.ToString(),
            row.AllowAccess));

        return _evaluator.Reduce(grants);
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

        return _evaluator.Holds(grants, permissionKey);
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

        return _evaluator.Holds(grants, permissionKey);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Two set-based statements, not a load-and-stage: the member is contracted to take effect
    /// immediately and alone, whereas staging would make the removal depend on a later commit that would
    /// also flush every other pending change in the unit of work. Only grants held against the account
    /// itself are removed - grants the account receives through a role are untouched, because they belong
    /// to the role and not to the person.
    /// <para>
    /// Removing nothing is a legitimate outcome and returns zero, which is why the count is returned
    /// rather than a flag.
    /// </para>
    /// </remarks>
    public async Task<int> DeleteUserPermissionsAsync(int portalId, int userId, CancellationToken cancellationToken = default)
    {
        int modules = await _context.ModulePermissions
            .Where(p => p.UserId == userId && p.Module!.PortalId == portalId)
            .ExecuteDeleteAsync(cancellationToken)
            .ConfigureAwait(false);

        int tabs = await _context.TabPermissions
            .Where(p => p.UserId == userId && p.Tab!.PortalId == portalId)
            .ExecuteDeleteAsync(cancellationToken)
            .ConfigureAwait(false);

        return modules + tabs;
    }

    /// <inheritdoc />
    public void AddModulePermission(ModulePermission permission)
    {
        ArgumentNullException.ThrowIfNull(permission);
        _context.ModulePermissions.Add(permission);
    }

    /// <inheritdoc />
    public void RemoveModulePermission(ModulePermission permission)
    {
        ArgumentNullException.ThrowIfNull(permission);
        _context.ModulePermissions.Remove(permission);
    }

    /// <inheritdoc />
    public void AddTabPermission(TabPermission permission)
    {
        ArgumentNullException.ThrowIfNull(permission);
        _context.TabPermissions.Add(permission);
    }

    /// <inheritdoc />
    public void RemoveTabPermission(TabPermission permission)
    {
        ArgumentNullException.ThrowIfNull(permission);
        _context.TabPermissions.Remove(permission);
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
    /// module. Denying grants are selected too - they have to be, because the evaluator cannot suppress a
    /// key it never sees.
    /// <para>
    /// The role lookup is correlated to the owning portal, which is what keeps one tenant's
    /// "Administrators" grants out of another tenant's answer. A host-level module carries a null portal,
    /// and relational equality never matches null, so it resolves no named role and fails closed.
    /// </para>
    /// </remarks>
    private IQueryable<ModulePermission> ApplicableModuleGrants(int moduleId, int? userId, IReadOnlyCollection<string> roleNames)
    {
        IReadOnlyList<string> wantedRoles = _evaluator.NormaliseRoleNames(roleNames);
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
        IReadOnlyList<string> wantedRoles = _evaluator.NormaliseRoleNames(roleNames);
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

    /// <summary>Projects module grants to the three facts a permission decision depends on.</summary>
    /// <param name="grants">The reachable module grants.</param>
    /// <returns>A projection carrying the module scope discriminator.</returns>
    /// <remarks>
    /// Only three columns cross, and the stored allow-or-deny flag crosses unaltered: the projection
    /// narrows the row without interpreting it, because interpreting it is the evaluator's job. The
    /// grant's own module identifier travels with it so that suppression stays correlated to the scope
    /// that carries the denial even when several modules are in the same result.
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
}
