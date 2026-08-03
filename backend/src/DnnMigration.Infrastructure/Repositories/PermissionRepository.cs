using DnnMigration.Domain.Abstractions.Repositories;
using DnnMigration.Domain.Entities;
using DnnMigration.Domain.Enums;
using DnnMigration.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace DnnMigration.Infrastructure.Repositories;

/// <summary>
/// Reads and writes the permission catalogue and the <see cref="ModulePermission"/> and
/// <see cref="TabPermission"/> grants recorded against it.
/// </summary>
/// <remarks>
/// <para>
/// MIGRATION: replaces the thirty-three permission procedures reached from the legacy core data
/// provider and the data-access halves of <c>PermissionController.vb</c>,
/// <c>ModulePermissionController.vb</c> and <c>TabPermissionController.vb</c> - nine, eighteen and
/// fifteen public members respectively, plus six reflection-hydrator call sites - behind one contract.
/// </para>
/// <para>
/// <strong>This type answers no access question.</strong> Every member reads or writes rows. Deciding
/// what a caller consequently holds - allow-and-deny precedence, pseudo-role sentinels, the role
/// lookup that keeps one tenant's grants away from another's - belongs to
/// <see cref="Security.PermissionEvaluator"/>, which is the single authority on it. Splitting the two
/// concerns is the point: the legacy controllers mixed retrieval with precedence and consequently
/// carried three copies of the same rule.
/// </para>
/// <para>
/// <strong>Pseudo-role sentinels are legitimate stored values.</strong> Neither grant table has a
/// foreign key to <c>Roles</c>, and that is deliberate in the legacy schema rather than an omission: a
/// grant's <c>RoleID</c> may hold a sentinel that has no <c>Roles</c> row at all - <c>-1</c> is "All
/// Users", <c>-2</c> is "Superuser", <c>-3</c> is "Unauthenticated Users" and <c>-4</c> is a
/// deliberate non-match. Nothing here treats a negative role identifier as absent, and nothing here
/// filters one out; the rows are returned as stored and interpreted elsewhere.
/// </para>
/// <para>
/// <strong>Writes are staged, not committed.</strong> The three insert members stage and return no
/// generated key, so a batch of grants - a page's permissions copied onto its children, or the
/// portal-creation sequence that writes portals, aliases, roles, pages and modules together - commits
/// atomically through the unit of work rather than one row at a time. Each entity's identity property
/// holds its key once that commit completes.
/// </para>
/// </remarks>
internal sealed class PermissionRepository : IPermissionRepository
{
    /// <summary>
    /// The value the legacy two-argument grant readers accept in place of a real identifier to mean
    /// "every one of them".
    /// </summary>
    /// <remarks>
    /// MIGRATION: measured from the terminal procedure bodies, not assumed.
    /// <c>GetModulePermissionsByModuleID</c> (04.04.00) guards both of its arguments with
    /// <c>(@ModuleID = -1 OR ModuleID = @ModuleID)</c> and
    /// <c>(PermissionID = @PermissionID OR @PermissionID = -1)</c>, and
    /// <c>GetTabPermissionsByTabID</c> (04.05.00) carries the same guard on its permission argument
    /// only. That wildcard is part of the procedure contract the Domain interface inherits, which is
    /// why it is honoured here rather than quietly dropped: a caller asking for every grant on one
    /// module has no other way to say so. It is a wildcard and nothing else - it is emphatically not
    /// an absence marker, and a stored <c>RoleID</c> of the same value is a real principal.
    /// </remarks>
    private const int AnyPermissionId = -1;

    /// <summary>
    /// The scope code carried by the catalogue entries every module definition shares.
    /// </summary>
    /// <remarks>
    /// MIGRATION: the literal <c>'SYSTEM_MODULE_DEFINITION'</c> appears in the terminal bodies of
    /// <c>GetPermissionsByModuleID</c> (04.05.03) and <c>GetModulePermissionsByModuleID</c> (04.04.00).
    /// It is reference data seeded by the upgrade scripts, so it is a stored value rather than a
    /// configurable one and is named here instead of being repeated as a literal at each use.
    /// </remarks>
    private const string ModuleDefinitionScopeCode = "SYSTEM_MODULE_DEFINITION";

    /// <summary>The scope code carried by the catalogue entries every page shares.</summary>
    /// <remarks>
    /// MIGRATION: the literal <c>'SYSTEM_TAB'</c> in the terminal bodies of
    /// <c>GetPermissionsByTabID</c> (04.05.03) and <c>GetTabPermissionsByTabID</c> (04.05.00).
    /// </remarks>
    private const string TabScopeCode = "SYSTEM_TAB";

    private readonly DnnDbContext _context;

    /// <summary>Initialises a new instance of the <see cref="PermissionRepository"/> class.</summary>
    /// <param name="context">The unit-of-work scoped database context.</param>
    /// <exception cref="ArgumentNullException"><paramref name="context"/> is <see langword="null"/>.</exception>
    public PermissionRepository(DnnDbContext context)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
    }

    // =================================================================================================
    // Permission - the catalogue.
    // =================================================================================================

    /// <inheritdoc />
    public Task<Permission?> GetByIdAsync(int permissionId, CancellationToken cancellationToken = default)
    {
        return _context.Permissions
            .FirstOrDefaultAsync(p => p.PermissionId == permissionId, cancellationToken);
    }

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// The set is materialised into an array before it is used in the predicate, so the translated
    /// <c>IN</c> list is built once from a stable snapshot rather than from a collection the caller could
    /// still be mutating. Duplicates are collapsed first, because a repeated identifier would lengthen
    /// the parameter list without widening the answer.
    /// </para>
    /// <para>
    /// An empty request short-circuits with no round trip. That is not merely an optimisation: EF Core
    /// translates an empty <c>Contains</c> into a constant-false predicate, and issuing a query whose
    /// answer is known to be empty is a round trip spent to learn nothing.
    /// </para>
    /// <para>
    /// Ordering by identifier matches the definition-scoped and page-scoped readers on this type, so
    /// every catalogue read in this repository returns a stable sequence.
    /// </para>
    /// </remarks>
    public async Task<IReadOnlyList<Permission>> GetByIdsAsync(
        IReadOnlyCollection<int> permissionIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(permissionIds);

        if (permissionIds.Count == 0)
        {
            return Array.Empty<Permission>();
        }

        int[] distinct = permissionIds.Distinct().ToArray();

        return await _context.Permissions
            .Where(p => distinct.Contains(p.PermissionId))
            .OrderBy(p => p.PermissionId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    /// <remarks>
    /// MIGRATION: the terminal <c>GetPermissionsByModuleDefID</c> (04.05.03) filters on the definition
    /// column alone and orders by permission identifier, which is reproduced exactly.
    /// </remarks>
    public async Task<IReadOnlyList<Permission>> GetByModuleDefinitionIdAsync(int moduleDefinitionId, CancellationToken cancellationToken = default)
    {
        return await _context.Permissions
            .Where(p => p.ModuleDefinitionId == moduleDefinitionId)
            .OrderBy(p => p.PermissionId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    /// <remarks>
    /// MIGRATION: the terminal <c>GetPermissionsByModuleID</c> (04.05.03) resolves the module's own
    /// definition through a scalar subquery and then takes the union of that definition's entries with
    /// every entry carrying the product-wide module-definition scope code. Both halves are reproduced,
    /// because dropping the second would silently narrow the result for every module in the
    /// installation. A module identifier that names no row contributes nothing from the first half and
    /// still yields the second, which is what the legacy statement did.
    /// </remarks>
    public async Task<IReadOnlyList<Permission>> GetByModuleIdAsync(int moduleId, CancellationToken cancellationToken = default)
    {
        IQueryable<int> owningDefinitionIds = _context.Modules
            .Where(m => m.ModuleId == moduleId)
            .Select(m => m.ModuleDefinitionId);

        return await _context.Permissions
            .Where(p => owningDefinitionIds.Contains(p.ModuleDefinitionId)
                || p.PermissionCode == ModuleDefinitionScopeCode)
            .OrderBy(p => p.PermissionId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    /// <remarks>
    /// MIGRATION: the terminal <c>GetPermissionByCodeAndKey</c> (04.06.00) compares each column to its
    /// argument and can match many rows, which is why the contract is plural despite the singular
    /// legacy name. The scope code is compared case-insensitively because
    /// <c>Permission.PermissionCode</c> is a free-text <c>varchar</c> column whose shipped rows are not
    /// consistently cased; the key is compared as the enumeration, which the mapping converts to the
    /// same column text.
    /// </remarks>
    public async Task<IReadOnlyList<Permission>> GetByCodeAndKeyAsync(
        string permissionCode,
        PermissionKey permissionKey,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(permissionCode);

        string wanted = permissionCode.Trim().ToLowerInvariant();

        return await _context.Permissions
            .Where(p => p.PermissionCode.ToLower() == wanted && p.PermissionKey == permissionKey)
            .OrderBy(p => p.PermissionId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    /// <remarks>
    /// MIGRATION: the terminal <c>GetPermissionsByTabID</c> (04.05.03) filters on the product-wide page
    /// scope code and never references its page argument at all, so every page receives the same
    /// catalogue. That is reproduced rather than corrected - inventing a page filter the legacy
    /// statement did not apply would narrow results the legacy application returned.
    /// </remarks>
    public async Task<IReadOnlyList<Permission>> GetByTabIdAsync(int tabId, CancellationToken cancellationToken = default)
    {
        return await _context.Permissions
            .Where(p => p.PermissionCode == TabScopeCode)
            .OrderBy(p => p.PermissionId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    /// <remarks>
    /// A set-based delete, so an identifier naming no row removes nothing and is not an error, which is
    /// what the legacy procedure did.
    /// </remarks>
    public Task DeleteAsync(int permissionId, CancellationToken cancellationToken = default)
    {
        return _context.Permissions
            .Where(p => p.PermissionId == permissionId)
            .ExecuteDeleteAsync(cancellationToken);
    }

    /// <inheritdoc />
    public Task AddAsync(Permission permission, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(permission);

        _context.Permissions.Add(permission);

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task UpdateAsync(Permission permission, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(permission);

        _context.Permissions.Update(permission);

        return Task.CompletedTask;
    }

    // =================================================================================================
    // ModulePermission - grants recorded against a module instance.
    // =================================================================================================

    /// <inheritdoc />
    public Task<ModulePermission?> GetModulePermissionByIdAsync(int modulePermissionId, CancellationToken cancellationToken = default)
    {
        return _context.ModulePermissions
            .Include(p => p.Permission)
            .Include(p => p.Role)
            .Include(p => p.User)
            .FirstOrDefaultAsync(p => p.ModulePermissionId == modulePermissionId, cancellationToken);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Both arguments honour the legacy wildcard, so this one member serves "every grant on this
    /// module", "this permission on this module" and "this permission everywhere". Allowing and denying
    /// grants both come back, and the permission, role and account references are loaded alongside:
    /// this is the grid-shaped read, and a caller presenting a permission matrix needs the rows
    /// themselves rather than the decision they add up to. The role reference is null for a grant held
    /// against a pseudo-role sentinel, which is a legitimate row and not a broken one.
    /// </remarks>
    public async Task<IReadOnlyList<ModulePermission>> GetModulePermissionsByModuleIdAsync(
        int moduleId,
        int permissionId,
        CancellationToken cancellationToken = default)
    {
        return await _context.ModulePermissions
            .Include(p => p.Permission)
            .Include(p => p.Role)
            .Include(p => p.User)
            .Where(p => moduleId == AnyPermissionId || p.ModuleId == moduleId)
            .Where(p => permissionId == AnyPermissionId || p.PermissionId == permissionId)
            .OrderBy(p => p.PermissionId)
            .ThenBy(p => p.RoleId)
            .ThenBy(p => p.UserId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    /// <remarks>
    /// MIGRATION: the terminal <c>GetModulePermissionsByPortal</c> (04.04.00) joins the grant view to
    /// the modules table and filters on the portal column, which is what confines the answer to one
    /// tenant.
    /// </remarks>
    public async Task<IReadOnlyList<ModulePermission>> GetModulePermissionsByPortalIdAsync(int portalId, CancellationToken cancellationToken = default)
    {
        return await _context.ModulePermissions
            .Include(p => p.Permission)
            .Include(p => p.Role)
            .Include(p => p.User)
            .Where(p => p.Module!.PortalId == portalId)
            .OrderBy(p => p.ModuleId)
            .ThenBy(p => p.PermissionId)
            .ThenBy(p => p.RoleId)
            .ThenBy(p => p.UserId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    /// <remarks>
    /// MIGRATION: the terminal <c>GetModulePermissionsByTabID</c> (04.04.00) joins the grant view to the
    /// module placement table, so this returns module grants selected by page rather than page grants.
    /// </remarks>
    public async Task<IReadOnlyList<ModulePermission>> GetModulePermissionsByTabIdAsync(int tabId, CancellationToken cancellationToken = default)
    {
        IQueryable<int> placedModuleIds = _context.TabModules
            .Where(tm => tm.TabId == tabId)
            .Select(tm => tm.ModuleId);

        return await _context.ModulePermissions
            .Include(p => p.Permission)
            .Include(p => p.Role)
            .Include(p => p.User)
            .Where(p => placedModuleIds.Contains(p.ModuleId))
            .OrderBy(p => p.ModuleId)
            .ThenBy(p => p.PermissionId)
            .ThenBy(p => p.RoleId)
            .ThenBy(p => p.UserId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task DeleteModulePermissionsByModuleIdAsync(int moduleId, CancellationToken cancellationToken = default)
    {
        return _context.ModulePermissions
            .Where(p => p.ModuleId == moduleId)
            .ExecuteDeleteAsync(cancellationToken);
    }

    /// <inheritdoc />
    /// <remarks>
    /// MIGRATION: the terminal <c>DeleteModulePermissionsByUserID</c> (04.08.00) joins the grant table
    /// to the modules table so only the named tenant's grants are removed, which is reproduced by the
    /// portal predicate here. Only grants naming the account itself go: a grant the account receives
    /// through a role belongs to the role, and removing it would strip every other holder of that role.
    /// </remarks>
    public Task DeleteModulePermissionsByUserIdAsync(int portalId, int userId, CancellationToken cancellationToken = default)
    {
        return _context.ModulePermissions
            .Where(p => p.UserId == userId && p.Module!.PortalId == portalId)
            .ExecuteDeleteAsync(cancellationToken);
    }

    /// <inheritdoc />
    public Task DeleteModulePermissionAsync(int modulePermissionId, CancellationToken cancellationToken = default)
    {
        return _context.ModulePermissions
            .Where(p => p.ModulePermissionId == modulePermissionId)
            .ExecuteDeleteAsync(cancellationToken);
    }

    /// <inheritdoc />
    public Task AddModulePermissionAsync(ModulePermission modulePermission, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(modulePermission);

        _context.ModulePermissions.Add(modulePermission);

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task UpdateModulePermissionAsync(ModulePermission modulePermission, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(modulePermission);

        _context.ModulePermissions.Update(modulePermission);

        return Task.CompletedTask;
    }

    // =================================================================================================
    // TabPermission - grants recorded against a page.
    // =================================================================================================

    /// <inheritdoc />
    /// <remarks>
    /// MIGRATION: the terminal <c>GetTabPermissionsByPortal</c> (04.04.00) also matches the host-level
    /// rows whose portal column is null, but only when its argument is itself null. The contract takes a
    /// plain value, so that branch is unreachable and host-level grants are not returned here.
    /// </remarks>
    public async Task<IReadOnlyList<TabPermission>> GetTabPermissionsByPortalIdAsync(int portalId, CancellationToken cancellationToken = default)
    {
        return await _context.TabPermissions
            .Include(p => p.Permission)
            .Include(p => p.Role)
            .Include(p => p.User)
            .Where(p => p.Tab!.PortalId == portalId)
            .OrderBy(p => p.TabId)
            .ThenBy(p => p.PermissionId)
            .ThenBy(p => p.RoleId)
            .ThenBy(p => p.UserId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    /// <remarks>
    /// The permission argument honours the legacy wildcard, so this member serves both "every grant on
    /// this page" and "this permission on this page". The page argument does not: the terminal
    /// <c>GetTabPermissionsByTabID</c> (04.05.00) guards only its permission argument, and that measured
    /// asymmetry with the module reader is preserved rather than smoothed over.
    /// </remarks>
    public async Task<IReadOnlyList<TabPermission>> GetTabPermissionsByTabIdAsync(
        int tabId,
        int permissionId,
        CancellationToken cancellationToken = default)
    {
        return await _context.TabPermissions
            .Include(p => p.Permission)
            .Include(p => p.Role)
            .Include(p => p.User)
            .Where(p => p.TabId == tabId)
            .Where(p => permissionId == AnyPermissionId || p.PermissionId == permissionId)
            .OrderBy(p => p.PermissionId)
            .ThenBy(p => p.RoleId)
            .ThenBy(p => p.UserId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task DeleteTabPermissionsByTabIdAsync(int tabId, CancellationToken cancellationToken = default)
    {
        return _context.TabPermissions
            .Where(p => p.TabId == tabId)
            .ExecuteDeleteAsync(cancellationToken);
    }

    /// <inheritdoc />
    /// <remarks>
    /// MIGRATION: the terminal <c>DeleteTabPermissionsByUserID</c> (04.08.00) joins the grant table to
    /// the pages table so only the named tenant's grants are removed. As with its module counterpart,
    /// only grants naming the account itself are removed.
    /// </remarks>
    public Task DeleteTabPermissionsByUserIdAsync(int portalId, int userId, CancellationToken cancellationToken = default)
    {
        return _context.TabPermissions
            .Where(p => p.UserId == userId && p.Tab!.PortalId == portalId)
            .ExecuteDeleteAsync(cancellationToken);
    }

    /// <inheritdoc />
    public Task DeleteTabPermissionAsync(int tabPermissionId, CancellationToken cancellationToken = default)
    {
        return _context.TabPermissions
            .Where(p => p.TabPermissionId == tabPermissionId)
            .ExecuteDeleteAsync(cancellationToken);
    }

    /// <inheritdoc />
    public Task AddTabPermissionAsync(TabPermission tabPermission, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tabPermission);

        _context.TabPermissions.Add(tabPermission);

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task UpdateTabPermissionAsync(TabPermission tabPermission, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tabPermission);

        _context.TabPermissions.Update(tabPermission);

        return Task.CompletedTask;
    }
}
