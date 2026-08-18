using DnnMigration.Domain.Abstractions.Repositories;
using DnnMigration.Domain.Entities;
using DnnMigration.Domain.Enums;
using DnnMigration.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;

namespace DnnMigration.Infrastructure.Repositories;

// This type carries rows and interprets none of them. Each of the three legacy static controllers it
// replaces mixed retrieval with interpretation, and consequently each carried its own copy of the same
// allow-and-deny precedence rule.

/// <summary>
/// Reads and writes the permission catalogue together with the grants recorded against a module instance
/// and against a page.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Negative role identifiers are real stored data.</strong> Neither grant table declares a foreign
/// key on its role column, and in this schema that is deliberate rather than an omission: a grant's role
/// identifier may name a pseudo-principal that has no row in the roles table at all, and the legacy grant
/// views folded exactly three of them into a display name - <c>-1</c> for all users, <c>-2</c> for the
/// superuser and <c>-3</c> for unauthenticated users.
/// </para>
/// <para>
/// <strong>The wildcard in the two-argument readers is a different thing entirely.</strong> It happens to
/// be <c>-1</c> as well, which is precisely why the two are separated by name below rather than left to a
/// shared constant. A <c>-1</c> arriving as a filter argument means "every one of them"; a <c>-1</c> stored
/// in a role column means a specific principal.
/// </para>
/// </remarks>
internal sealed class PermissionRepository : IPermissionRepository
{
    /// <summary>
    /// The value the module position of the module-grant reader accepts in place of a real module
    /// identifier to mean "every module".
    /// </summary>
    private const int AnyModuleId = -1;

    /// <summary>
    /// The value the permission position of both two-argument grant readers accepts in place of a real
    /// permission identifier to mean "every permission".
    /// </summary>
    private const int AnyPermissionId = -1;

    /// <summary>The scope code carried by the catalogue entries every module definition shares.</summary>
    /// <remarks>
    /// Reference data seeded by the upgrade chain, appearing in the terminal bodies of the module-scoped
    /// catalogue reader (<c>04.05.03.SqlDataProvider</c>) and the module-grant reader
    /// (<c>04.04.00.SqlDataProvider</c>). It is a stored value rather than a configurable one, which is why
    /// it is named here once instead of repeated as a literal at each use.
    /// </remarks>
    private const string ModuleDefinitionScopeCode = "SYSTEM_MODULE_DEFINITION";

    /// <summary>The scope code carried by the catalogue entries every page shares.</summary>
    private const string TabScopeCode = "SYSTEM_TAB";

    private readonly DnnDbContext _dbContext;

    /// <summary>Initialises a new instance of the <see cref="PermissionRepository"/> class.</summary>
    /// <param name="dbContext">The context scoped to the current unit of work.</param>
    /// <exception cref="ArgumentNullException"><paramref name="dbContext"/> is <see langword="null"/>.</exception>
    /// <remarks>
    /// The only dependency, by design. The legacy controllers additionally reached a static cache module, a
    /// reflection-created provider singleton and the ambient request context; none of the three has a
    /// counterpart here, because caching is coordinated above this layer, the provider indirection is
    /// replaced by injection, and nothing about reading a row depends on who asked.
    /// </remarks>
    public PermissionRepository(DnnDbContext dbContext)
    {
        _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
    }

    // =================================================================================================
    // Permission - the catalogue. A row declares that an action exists; it grants nothing.
    // =================================================================================================

    /// <inheritdoc />
    /// <remarks>
    /// No predicate, and the ordering is the same one every scoped catalogue reader here applies, so the
    /// unscoped answer and a scoped answer present their rows in the same sequence. The read is untracked
    /// like its siblings: the catalogue is reference data this repository never writes.
    /// </remarks>
    public async Task<IReadOnlyList<Permission>> GetCatalogueAsync(CancellationToken cancellationToken = default)
    {
        return await _dbContext.Permissions
            .AsNoTracking()
            .OrderBy(entry => entry.PermissionId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    /// <remarks>
    /// The terminal single-row reader selects the five catalogue columns for one primary key, so at most
    /// one row can match and the answer is a nullable entity rather than a list. An identifier naming no
    /// row yields <see langword="null"/>, which is an answer rather than a fault.
    /// </remarks>
    public Task<Permission?> GetByIdAsync(int permissionId, CancellationToken cancellationToken = default)
    {
        return _dbContext.Permissions
            .AsNoTracking()
            .FirstOrDefaultAsync(entry => entry.PermissionId == permissionId, cancellationToken);
    }

    /// <inheritdoc />
    /// <remarks>
    /// The set is snapshotted into an array before it reaches the predicate, so the translated membership
    /// test is built once from a stable sequence rather than from a collection the caller could still be
    /// mutating. Duplicates collapse first: a repeated identifier would lengthen the parameter list without
    /// widening the answer.
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

        int[] wanted = permissionIds.Distinct().ToArray();

        return await _dbContext.Permissions
            .AsNoTracking()
            .Where(entry => wanted.Contains(entry.PermissionId))
            .OrderBy(entry => entry.PermissionId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    /// <remarks>
    /// The terminal definition-scoped reader (<c>04.05.03.SqlDataProvider</c>) filters on the definition
    /// column alone and orders by permission identifier. Both halves are reproduced exactly.
    /// </remarks>
    public async Task<IReadOnlyList<Permission>> GetByModuleDefinitionIdAsync(int moduleDefinitionId, CancellationToken cancellationToken = default)
    {
        return await _dbContext.Permissions
            .AsNoTracking()
            .Where(entry => entry.ModuleDefinitionId == moduleDefinitionId)
            .OrderBy(entry => entry.PermissionId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    /// <remarks>
    /// A module identifier naming no row contributes nothing from the first half and still yields the
    /// second, which is what the legacy scalar subquery did - comparing a column to a null subquery result
    /// matched nothing rather than failing.
    /// </remarks>
    public async Task<IReadOnlyList<Permission>> GetByModuleIdAsync(int moduleId, CancellationToken cancellationToken = default)
    {
        // Composed as a subquery rather than resolved in a first round trip, so the union below stays a
        // single statement exactly as the legacy procedure was. Nothing is materialised from it: it appears
        // only inside a predicate, so no module entity is tracked or returned.
        IQueryable<int> owningDefinitionIds = _dbContext.Modules
            .Where(module => module.ModuleId == moduleId)
            .Select(module => module.ModuleDefinitionId);

        return await _dbContext.Permissions
            .AsNoTracking()
            .Where(entry => owningDefinitionIds.Contains(entry.ModuleDefinitionId)
                || entry.PermissionCode == ModuleDefinitionScopeCode)
            .OrderBy(entry => entry.PermissionId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    /// <remarks>
    /// <strong>the singular legacy name is a misnomer and the plural return is deliberate.</strong> The
    /// terminal procedure (<c>04.06.00.SqlDataProvider</c>) compares each column to its argument and can
    /// match many rows - the uniqueness rule on this table spans the scope code, the definition and the
    /// key, so one code-and-key pair may legitimately exist once per definition.
    /// </remarks>
    public async Task<IReadOnlyList<Permission>> GetByCodeAndKeyAsync(
        string permissionCode,
        PermissionKey permissionKey,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(permissionCode);

        // The key travels as an enumeration member because the caller is asking about one of the four keys
        // THIS SOLUTION names, but the column it is compared against is free text (see
        // PermissionConfiguration), so the member's own spelling is resolved once here and the predicate
        // becomes a plain string equality the provider translates directly. Case is left to the column's
        // collation, which is how the legacy procedure this replaces compared it.
        string wantedKey = permissionKey.ToString();

        return await _dbContext.Permissions
            .AsNoTracking()
            .Where(entry => entry.PermissionCode == permissionCode && entry.PermissionKey == wantedKey)
            .OrderBy(entry => entry.PermissionId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<Permission>> GetByTabIdAsync(int tabId, CancellationToken cancellationToken = default)
    {
        IQueryable<int> matchingTabIds = _dbContext.Tabs
            .Where(tab => tab.TabId == tabId)
            .Select(tab => tab.TabId);

        return await _dbContext.Permissions
            .AsNoTracking()
            .Where(entry => matchingTabIds.Any() && entry.PermissionCode == TabScopeCode)
            .OrderBy(entry => entry.PermissionId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task DeleteAsync(int permissionId, CancellationToken cancellationToken = default)
    {
        Permission? entry = await _dbContext.Permissions
            .FirstOrDefaultAsync(candidate => candidate.PermissionId == permissionId, cancellationToken)
            .ConfigureAwait(false);

        if (entry is null)
        {
            return;
        }

        _dbContext.Permissions.Remove(entry);
    }

    /// <inheritdoc />
    /// <remarks>
    /// The legacy insert took four positional arguments and returned the generated key from
    /// <c>SCOPE_IDENTITY()</c>. The four values travel as properties on one entity and no key is returned,
    /// because returning one would force this member to commit on its own and destroy the unit-of-work
    /// boundary.
    /// </remarks>
    public Task AddAsync(Permission permission, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(permission);
        cancellationToken.ThrowIfCancellationRequested();

        _dbContext.Permissions.Add(permission);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    /// <remarks>
    /// The staging ASSIGNS THE STATE and does not call <c>DbSet.Update</c>. <c>Update</c> decides between
    /// <c>Added</c> and <c>Modified</c> by asking whether the key "is set" - reading an <see cref="int"/>
    /// key of 0 as unset - and then walks the navigation graph applying the same test to everything it
    /// reaches.
    /// </remarks>
    public Task UpdateAsync(Permission permission, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(permission);
        cancellationToken.ThrowIfCancellationRequested();

        EntityEntry<Permission> entry = _dbContext.Entry(permission);

        if (entry.State is EntityState.Detached)
        {
            entry.State = EntityState.Modified;
        }

        return Task.CompletedTask;
    }

    // No read below loads the catalogue entry, the role or the account alongside the grant, and the legacy
    // view is the reason the question arises rather than the reason to do it.

    /// <inheritdoc />
    /// <remarks>
    /// MIGRATION: the terminal single-row grant reader (<c>04.04.00.SqlDataProvider</c>) selects one row by
    /// primary key. This member has no counterpart in the page-grant family, and that absence is measured
    /// rather than accidental - see the note above the page section.
    /// </remarks>
    public Task<ModulePermission?> GetModulePermissionByIdAsync(int modulePermissionId, CancellationToken cancellationToken = default)
    {
        return _dbContext.ModulePermissions
            .AsNoTracking()
            .FirstOrDefaultAsync(grant => grant.ModulePermissionId == modulePermissionId, cancellationToken);
    }

    /// <inheritdoc />
    /// <remarks>
    /// <strong>both arguments carry the legacy wildcard, and each is translated by composing the predicate
    /// rather than by pushing the comparison into it.</strong> The terminal body guards its scope argument
    /// with <c>(@ModuleID = -1 OR ModuleID = @ModuleID …)</c> and its permission argument with
    /// <c>(PermissionID = @PermissionID OR @PermissionID = -1)</c>.
    /// </remarks>
    public async Task<IReadOnlyList<ModulePermission>> GetModulePermissionsByModuleIdAsync(
        int moduleId,
        int permissionId,
        CancellationToken cancellationToken = default)
    {
        IQueryable<ModulePermission> query = _dbContext.ModulePermissions.AsNoTracking();

        if (moduleId != AnyModuleId)
        {
            query = query.Where(grant => grant.ModuleId == moduleId);
        }

        if (permissionId != AnyPermissionId)
        {
            query = query.Where(grant => grant.PermissionId == permissionId);
        }

        // Finishing on the primary key makes the sequence total. The three columns before it group the rows
        // the way a permission matrix reads, but they cannot order it on their own: under either wildcard
        // two rows can agree on all three.
        return await query
            .OrderBy(grant => grant.PermissionId)
            .ThenBy(grant => grant.RoleId)
            .ThenBy(grant => grant.UserId)
            .ThenBy(grant => grant.ModulePermissionId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    /// <remarks>
    /// The terminal tenant-scoped grant reader (<c>04.04.00.SqlDataProvider</c>) joins the grant to the
    /// modules table and filters on its tenant column, which is what keeps one tenant's grants away from
    /// another tenant's answer. The join is expressed through the configured module relationship rather
    /// than restated as a manual join.
    /// </remarks>
    public async Task<IReadOnlyList<ModulePermission>> GetModulePermissionsByPortalIdAsync(int portalId, CancellationToken cancellationToken = default)
    {
        return await _dbContext.ModulePermissions
            .AsNoTracking()
            .Where(grant => grant.Module.PortalId == portalId)
            .OrderBy(grant => grant.ModuleId)
            .ThenBy(grant => grant.PermissionId)
            .ThenBy(grant => grant.RoleId)
            .ThenBy(grant => grant.UserId)
            .ThenBy(grant => grant.ModulePermissionId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    /// <remarks>
    /// The terminal page-scoped grant reader (<c>04.04.00.SqlDataProvider</c>) joins the grant to the
    /// placement table on the module and filters on the page. It returns MODULE grants selected by page,
    /// never page grants, and the page-grant family has no mirror image of it - a genuine cross-scope query
    /// that exists on one side only.
    /// </remarks>
    public async Task<IReadOnlyList<ModulePermission>> GetModulePermissionsByTabIdAsync(int tabId, CancellationToken cancellationToken = default)
    {
        IQueryable<int> placedModuleIds = _dbContext.TabModules
            .Where(placement => placement.TabId == tabId)
            .Select(placement => placement.ModuleId);

        return await _dbContext.ModulePermissions
            .AsNoTracking()
            .Where(grant => placedModuleIds.Contains(grant.ModuleId))
            .OrderBy(grant => grant.ModuleId)
            .ThenBy(grant => grant.PermissionId)
            .ThenBy(grant => grant.RoleId)
            .ThenBy(grant => grant.UserId)
            .ThenBy(grant => grant.ModulePermissionId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    /// <remarks>
    /// The terminal bulk removal filters on the module column alone and removes the matching rows in one
    /// statement. This does the same: ONE set-based delete carrying the same predicate, and no load.
    /// </remarks>
    public Task DeleteModulePermissionsByModuleIdAsync(int moduleId, CancellationToken cancellationToken = default)
    {
        return _dbContext.ModulePermissions
            .Where(grant => grant.ModuleId == moduleId)
            .ExecuteDeleteAsync(cancellationToken);
    }

    /// <inheritdoc />
    /// <remarks>
    /// MIGRATION: only grants naming the account itself are removed. A grant the account receives through a
    /// role belongs to the role, and removing it would revoke access from every other holder of that role.
    /// </remarks>
    public Task DeleteModulePermissionsByUserIdAsync(int portalId, int userId, CancellationToken cancellationToken = default)
    {
        return _dbContext.ModulePermissions
            .Where(grant => grant.UserId == userId && grant.Module.PortalId == portalId)
            .ExecuteDeleteAsync(cancellationToken);
    }

    /// <inheritdoc />
    /// <remarks>
    /// The terminal <c>DeleteRole</c> procedure (<c>03.00.10.SqlDataProvider</c>) removed this family by
    /// role identifier alone, and so does this. There is deliberately NO tenant predicate: a role belongs
    /// to exactly one tenant, so its identifier already bounds the removal, whereas the account-scoped
    /// sibling above needs one because an account belongs to many.
    /// </remarks>
    public Task DeleteModulePermissionsByRoleIdAsync(int roleId, CancellationToken cancellationToken = default)
    {
        return _dbContext.ModulePermissions
            .Where(grant => grant.RoleId == roleId)
            .ExecuteDeleteAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task DeleteModulePermissionAsync(int modulePermissionId, CancellationToken cancellationToken = default)
    {
        ModulePermission? grant = await _dbContext.ModulePermissions
            .FirstOrDefaultAsync(candidate => candidate.ModulePermissionId == modulePermissionId, cancellationToken)
            .ConfigureAwait(false);

        if (grant is null)
        {
            return;
        }

        _dbContext.ModulePermissions.Remove(grant);
    }

    /// <inheritdoc />
    public Task AddModulePermissionAsync(ModulePermission modulePermission, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(modulePermission);
        cancellationToken.ThrowIfCancellationRequested();

        _dbContext.ModulePermissions.Add(modulePermission);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    /// <remarks>
    /// The staging ASSIGNS THE STATE rather than calling <c>DbSet.Update</c>, which walks the navigation
    /// graph and reads an <see cref="int"/> key of 0 as unset.
    /// </remarks>
    public Task UpdateModulePermissionAsync(ModulePermission modulePermission, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(modulePermission);
        cancellationToken.ThrowIfCancellationRequested();

        EntityEntry<ModulePermission> entry = _dbContext.Entry(modulePermission);

        if (entry.State is EntityState.Detached)
        {
            entry.State = EntityState.Modified;
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    /// <remarks>
    /// The terminal tenant-scoped page-grant reader (<c>04.04.00.SqlDataProvider</c>) filters on the tenant
    /// column its view sources from the pages table, so the tenant path is expressed here through the
    /// configured page relationship.
    /// </remarks>
    public async Task<IReadOnlyList<TabPermission>> GetTabPermissionsByPortalIdAsync(int portalId, CancellationToken cancellationToken = default)
    {
        return await _dbContext.TabPermissions
            .AsNoTracking()
            .Where(grant => grant.Tab.PortalId == portalId)
            .OrderBy(grant => grant.TabId)
            .ThenBy(grant => grant.PermissionId)
            .ThenBy(grant => grant.RoleId)
            .ThenBy(grant => grant.UserId)
            .ThenBy(grant => grant.TabPermissionId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    /// <remarks>
    /// The terminal body additionally unions in grants whose page column is null carrying the product-wide
    /// page scope code. That branch cannot arise against this model, whose grant entity declares a
    /// non-nullable page identifier behind an enforced foreign key, so no member reproduces it.
    /// </remarks>
    public async Task<IReadOnlyList<TabPermission>> GetTabPermissionsByTabIdAsync(
        int tabId,
        int permissionId,
        CancellationToken cancellationToken = default)
    {
        IQueryable<TabPermission> query = _dbContext.TabPermissions
            .AsNoTracking()
            .Where(grant => grant.TabId == tabId);

        if (permissionId != AnyPermissionId)
        {
            query = query.Where(grant => grant.PermissionId == permissionId);
        }

        return await query
            .OrderBy(grant => grant.PermissionId)
            .ThenBy(grant => grant.RoleId)
            .ThenBy(grant => grant.UserId)
            .ThenBy(grant => grant.TabPermissionId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    /// <remarks>
    /// The same predicate and the same wildcard convention as the single-page member, widened to a set,
    /// with the page leading the ordering so each page's slice reads identically to what that member would
    /// return.
    /// </remarks>
    public async Task<IReadOnlyList<TabPermission>> GetTabPermissionsByTabIdsAsync(
        IReadOnlyCollection<int> tabIds,
        int permissionId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tabIds);

        if (tabIds.Count == 0)
        {
            return Array.Empty<TabPermission>();
        }

        int[] wanted = tabIds.Distinct().ToArray();

        IQueryable<TabPermission> query = _dbContext.TabPermissions
            .AsNoTracking()
            .Where(grant => wanted.Contains(grant.TabId));

        if (permissionId != AnyPermissionId)
        {
            query = query.Where(grant => grant.PermissionId == permissionId);
        }

        return await query
            .OrderBy(grant => grant.TabId)
            .ThenBy(grant => grant.PermissionId)
            .ThenBy(grant => grant.RoleId)
            .ThenBy(grant => grant.UserId)
            .ThenBy(grant => grant.TabPermissionId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    /// <remarks>
    /// The terminal bulk removal filters on the page column alone and removes the matching rows in one
    /// statement, which is what this issues - ONE set-based delete carrying that predicate, with no load.
    /// Removing nothing is a legitimate outcome and yields no count, as the legacy procedure yielded none.
    /// </remarks>
    public Task DeleteTabPermissionsByTabIdAsync(int tabId, CancellationToken cancellationToken = default)
    {
        return _dbContext.TabPermissions
            .Where(grant => grant.TabId == tabId)
            .ExecuteDeleteAsync(cancellationToken);
    }

    /// <inheritdoc />
    /// <remarks>
    /// MIGRATION: only grants naming the account itself are removed; a grant reaching the account through a
    /// role belongs to the role. ONE set-based delete with no load, for the same reason as its module
    /// counterpart: the row count is unbounded and no value on those rows is needed to remove them.
    /// </remarks>
    public Task DeleteTabPermissionsByUserIdAsync(int portalId, int userId, CancellationToken cancellationToken = default)
    {
        return _dbContext.TabPermissions
            .Where(grant => grant.UserId == userId && grant.Tab.PortalId == portalId)
            .ExecuteDeleteAsync(cancellationToken);
    }

    /// <inheritdoc />
    /// <remarks>
    /// The page-scoped half of the role cleanup the terminal <c>DeleteRole</c> procedure performed
    /// (<c>03.00.10.SqlDataProvider</c>), by role identifier alone.
    /// </remarks>
    public Task DeleteTabPermissionsByRoleIdAsync(int roleId, CancellationToken cancellationToken = default)
    {
        return _dbContext.TabPermissions
            .Where(grant => grant.RoleId == roleId)
            .ExecuteDeleteAsync(cancellationToken);
    }

    /// <inheritdoc />
    /// <remarks>
    /// The folder-scoped cleanup the terminal <c>DeleteRole</c> procedure performed, and the first
    /// statement in its body (<c>03.00.10.SqlDataProvider</c>).
    /// </remarks>
    public async Task DeleteFolderPermissionsByRoleIdAsync(int roleId, CancellationToken cancellationToken = default)
    {
        if (!_dbContext.Database.IsSqlServer())
        {
            return;
        }

        _ = await _dbContext.Database
            .ExecuteSqlInterpolatedAsync(
                $@"
IF OBJECT_ID(N'[dbo].[FolderPermission]', N'U') IS NOT NULL
    DELETE FROM [dbo].[FolderPermission] WHERE [RoleID] = {roleId};",
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task DeleteTabPermissionAsync(int tabPermissionId, CancellationToken cancellationToken = default)
    {
        TabPermission? grant = await _dbContext.TabPermissions
            .FirstOrDefaultAsync(candidate => candidate.TabPermissionId == tabPermissionId, cancellationToken)
            .ConfigureAwait(false);

        if (grant is null)
        {
            return;
        }

        _dbContext.TabPermissions.Remove(grant);
    }

    /// <inheritdoc />
    public Task AddTabPermissionAsync(TabPermission tabPermission, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tabPermission);
        cancellationToken.ThrowIfCancellationRequested();

        _dbContext.TabPermissions.Add(tabPermission);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    /// <remarks>
    /// The staging ASSIGNS THE STATE rather than calling <c>DbSet.Update</c>, which walks the navigation
    /// graph and reads an <see cref="int"/> key of 0 as unset.
    /// </remarks>
    public Task UpdateTabPermissionAsync(TabPermission tabPermission, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tabPermission);
        cancellationToken.ThrowIfCancellationRequested();

        EntityEntry<TabPermission> entry = _dbContext.Entry(tabPermission);

        if (entry.State is EntityState.Detached)
        {
            entry.State = EntityState.Modified;
        }

        return Task.CompletedTask;
    }
}
