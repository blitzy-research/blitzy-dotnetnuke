using DnnMigration.Domain.Abstractions.Repositories;
using DnnMigration.Domain.Common;
using DnnMigration.Domain.Entities;
using DnnMigration.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace DnnMigration.Infrastructure.Repositories;

/// <summary>
/// Persists the <b>Module</b> aggregate - the pluggable content component with a lifecycle - together with
/// its placements on pages and both of its settings collections.
/// </summary>
/// <remarks>
/// <para>
/// <b>Writes are staged, never committed.</b> Every mutating member records its intent with the change
/// tracker and returns; not one of them calls <c>SaveChanges</c>.
/// </para>
/// <para>
/// <b>Bulk removals materialise before staging.</b> The two collection deletes read their rows and hand
/// them to <c>RemoveRange</c> rather than issuing <c>ExecuteDelete</c>. That is deliberate:
/// <c>ExecuteDelete</c> writes to the database immediately, outside the unit of work, so a caller whose
/// later step failed would find the settings already gone with no transaction to roll back.
/// </para>
/// </remarks>
internal sealed class ModuleRepository : IModuleRepository
{
    /// <summary>
    /// The unit-of-work scoped context. Held privately so that no caller can reach the model, a <see
    /// cref="DbSet{TEntity}"/> or a transaction through this repository.
    /// </summary>
    private readonly DnnDbContext _dbContext;

    /// <summary>The module property a placement listing orders by when the caller names none.</summary>
    private const string DefaultPlacementSortProperty = "ModuleTitle";

    /// <summary>Initialises a new instance of the <see cref="ModuleRepository"/> class.</summary>
    /// <param name="dbContext">The scoped database context shared with the unit of work.</param>
    /// <exception cref="ArgumentNullException"><paramref name="dbContext"/> is <see langword="null"/>.</exception>
    public ModuleRepository(DnnDbContext dbContext)
    {
        _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
    }

    /// <inheritdoc />
    /// <remarks>
    /// Installation-wide and deliberately unfiltered: neither the tenant nor the soft-delete flag is
    /// predicated, matching <c>GetAllModules()</c>, which applied no <c>where</c> clause at all.
    /// </remarks>
    // The PACKAGE is loaded as well as the definition, on this and every other read below.
    public async Task<IReadOnlyList<Module>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        return await _dbContext.Modules
            .Include(module => module.ModuleDefinition)
                .ThenInclude(definition => definition.DesktopModule)
            .OrderBy(module => module.ModuleId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    /// <remarks>
    /// The tenant predicate is exact equality against the nullable <c>PortalID</c> column.
    /// </remarks>
    public async Task<IReadOnlyList<Module>> GetByPortalIdAsync(int portalId, CancellationToken cancellationToken = default)
    {
        return await _dbContext.Modules
            .Include(module => module.ModuleDefinition)
                .ThenInclude(definition => definition.DesktopModule)
            .Where(module => module.PortalId == portalId)
            .OrderBy(module => module.ModuleId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<Module>> GetAllTabsModulesAsync(
        int portalId,
        bool allTabs,
        CancellationToken cancellationToken = default)
    {
        return await _dbContext.Modules
            .Include(module => module.ModuleDefinition)
                .ThenInclude(definition => definition.DesktopModule)
            .Where(module => module.PortalId == portalId && module.AllTabs == allTabs)
            .OrderBy(module => module.ModuleId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task<Module?> GetByIdAsync(int moduleId, CancellationToken cancellationToken = default)
    {
        return _dbContext.Modules
            .Include(module => module.ModuleDefinition)
                .ThenInclude(definition => definition.DesktopModule)
            .FirstOrDefaultAsync(module => module.ModuleId == moduleId, cancellationToken);
    }

    /// <inheritdoc />
    /// <remarks>
    /// <c>IX_TabModules</c> is unique over <c>(TabID, ModuleID)</c> - TabID first, then ModuleID, as the
    /// 03.00.01 script declares it - so a page and a module identify at most one placement and a single row
    /// is the correct shape rather than a collection. <c>Tabs.TabID</c> also seeds at zero, so neither key
    /// argument has a reserved value.
    /// </remarks>
    public Task<TabModule?> GetTabModuleAsync(int tabId, int moduleId, CancellationToken cancellationToken = default)
    {
        return _dbContext.TabModules
            .FirstOrDefaultAsync(
                placement => placement.TabId == tabId && placement.ModuleId == moduleId,
                cancellationToken);
    }

    /// <inheritdoc />
    /// <remarks>
    /// The friendly name identifies the <em>definition</em>, not the module, so the predicate reaches
    /// through the required <c>ModuleDefID</c> relationship - which Entity Framework renders as an inner
    /// join onto <c>dbo.ModuleDefinitions</c> - rather than duplicating the name onto the module row.
    /// </remarks>
    public Task<Module?> GetByDefinitionAsync(
        int portalId,
        string friendlyName,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(friendlyName);

        return _dbContext.Modules
            .Include(module => module.ModuleDefinition)
                .ThenInclude(definition => definition.DesktopModule)
            .Where(module => module.PortalId == portalId
                && module.ModuleDefinition.FriendlyName == friendlyName)
            .OrderBy(module => module.ModuleId)
            .FirstOrDefaultAsync(cancellationToken);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Staging only. Placements and settings added to the entity's collections before this call are staged
    /// with it by the change tracker, which is how a module and its first placement become one insert pair
    /// rather than two independently durable writes.
    /// </remarks>
    // MIGRATION: replaces the ten-argument `AddModule(PortalID, ModuleDefID, ModuleTitle, AllTabs, Header,
    // Footer, StartDate, EndDate, InheritViewPermissions, IsDeleted) As Integer`.
    public Task AddAsync(Module module, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(module);
        cancellationToken.ThrowIfCancellationRequested();

        _dbContext.Modules.Add(module);

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    // The detached branch assigns the state directly instead of calling DbSet.Update, and that is a
    // correctness requirement of this schema rather than a stylistic preference.
    public Task UpdateAsync(Module module, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(module);
        cancellationToken.ThrowIfCancellationRequested();

        if (_dbContext.Entry(module).State is EntityState.Detached)
        {
            _dbContext.Entry(module).State = EntityState.Modified;
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    /// <remarks>
    /// The hard delete that empties the recycle bin, distinct from the soft delete described on <see
    /// cref="UpdateAsync"/>. The row is located first because the contract addresses its target by
    /// identifier, as the legacy procedure did.
    /// </remarks>
    public async Task DeleteAsync(int moduleId, CancellationToken cancellationToken = default)
    {
        Module? module = await _dbContext.Modules
            .FindAsync(new object?[] { moduleId }, cancellationToken)
            .ConfigureAwait(false);

        if (module is null)
        {
            // Absence is not an error: a caller that has already established it need not distinguish
            // "removed" from "was never there", which is what makes the delete idempotent.
            return;
        }

        _dbContext.Modules.Remove(module);
    }

    /// <inheritdoc />
    // MIGRATION: net-new. It exists because tenant deletion must remove the tenant's modules explicitly
    // FK_Modules_Portals is the one foreign key into dbo.Portals that carries no cascade clause in the
    // terminal schema - and it already holds every module entity when it does so.
    public Task DeleteRangeAsync(
        IReadOnlyCollection<Module> modules,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(modules);
        cancellationToken.ThrowIfCancellationRequested();

        if (modules.Count == 0)
        {
            return Task.CompletedTask;
        }

        _dbContext.Modules.RemoveRange(modules);

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    /// <remarks>
    /// A <em>pane-scoped</em> ordering read, and a different question from the page-scoped reads owned by
    /// <c>ITabRepository</c>: it exists so that a renumbering pass sees exactly the rows whose order it is
    /// about to rewrite.
    /// </remarks>
    public async Task<IReadOnlyList<TabModule>> GetTabModuleOrderAsync(
        int tabId,
        string paneName,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(paneName);

        return await _dbContext.TabModules
            .Where(placement => placement.TabId == tabId && placement.PaneName == paneName)
            .OrderBy(placement => placement.ModuleOrder)
            .ThenBy(placement => placement.TabModuleId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Kept separate from <see cref="UpdateTabModuleAsync"/> on purpose. The legacy procedure wrote only
    /// the two ordering columns, and that narrowness is preserved for a detached placement by marking just
    /// <see cref="TabModule.ModuleOrder"/> and <see cref="TabModule.PaneName"/> modified - so a renumbering
    /// pass cannot carry a placement's unrelated edits along with it.
    /// </remarks>
    public Task UpdateTabModuleOrderAsync(TabModule tabModule, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tabModule);
        cancellationToken.ThrowIfCancellationRequested();

        if (_dbContext.Entry(tabModule).State is EntityState.Detached)
        {
            // Unchanged first, then the two columns: assigning the state attaches the instance without
            // Attach's key-is-set heuristic, and marking properties afterwards is what keeps the write
            // narrow. See the note on UpdateAsync for why that heuristic cannot be relied on here.
            _dbContext.Entry(tabModule).State = EntityState.Unchanged;

            _dbContext.Entry(tabModule).Property(placement => placement.ModuleOrder).IsModified = true;
            _dbContext.Entry(tabModule).Property(placement => placement.PaneName).IsModified = true;
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    /// <remarks>
    /// Addresses a placement by its own surrogate key, which is a first-class value rather than an internal
    /// detail: the placement-scoped settings hang off it, so a caller holding a settings row must be able
    /// to resolve the placement that owns it.
    /// </remarks>
    public Task<TabModule?> GetTabModuleByIdAsync(int tabModuleId, CancellationToken cancellationToken = default)
    {
        return _dbContext.TabModules
            .FirstOrDefaultAsync(placement => placement.TabModuleId == tabModuleId, cancellationToken);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Answers "every page this module appears on". Ordered by page and then by the placement key so the
    /// sequence is stable.
    /// </remarks>
    public async Task<IReadOnlyList<TabModule>> GetTabModulesByModuleIdAsync(
        int moduleId,
        CancellationToken cancellationToken = default)
    {
        return await _dbContext.TabModules
            .Where(placement => placement.ModuleId == moduleId)
            .OrderBy(placement => placement.TabId)
            .ThenBy(placement => placement.TabModuleId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    /// <remarks>
    /// The set-based twin of <see cref="GetTabModulesByModuleIdAsync"/>. One statement answers "every page
    /// each of these modules appears on", which is what keeps a listing that emits one row per placement
    /// from issuing one read per module.
    /// </remarks>
    // No legacy provider member corresponds to this read, for the same reason its single-module twin has
    // none - legacy callers received one flattened join row per page and so never asked the question
    // separately.
    public async Task<IReadOnlyList<TabModule>> GetTabModulesByModuleIdsAsync(
        IReadOnlyCollection<int> moduleIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(moduleIds);

        if (moduleIds.Count == 0)
        {
            return Array.Empty<TabModule>();
        }

        int[] wanted = moduleIds.Distinct().ToArray();

        return await _dbContext.TabModules
            .Where(placement => wanted.Contains(placement.ModuleId))
            .OrderBy(placement => placement.ModuleId)
            .ThenBy(placement => placement.TabId)
            .ThenBy(placement => placement.TabModuleId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    /// <remarks>
    /// EVERY NARROWING IS RELATIONAL AND HAPPENS BEFORE THE WINDOW. The tenant predicate, the recycle-bin
    /// predicate and the title fragment all reach through the required <c>ModuleID</c> relationship, which
    /// Entity Framework renders as an inner join onto <c>dbo.Modules</c> - the same join the legacy
    /// procedure performed - so the store decides which placements qualify.
    /// </remarks>
    public async Task<PagedResult<TabModule>> ListPlacementsAsync(
        int portalId,
        int? tabId,
        bool includeDeleted,
        string? titleQuery,
        string? sortBy,
        bool descending,
        int pageIndex,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        IQueryable<TabModule> query = _dbContext.TabModules
            .AsNoTracking()
            .Where(placement => placement.Module.PortalId == portalId);

        if (!includeDeleted)
        {
            query = query.Where(placement => !placement.Module.IsDeleted);
        }

        if (tabId is int addressedTab)
        {
            // The presence of a value selects the filter, never its magnitude: TabID is IDENTITY(0, 1),
            // so a page identifier of zero addresses a real page and must not read as "unspecified".
            query = query.Where(placement => placement.TabId == addressedTab);
        }

        if (!string.IsNullOrWhiteSpace(titleQuery))
        {
            // ModuleTitle permits null, so the null test precedes the comparison.
            string trimmed = titleQuery.Trim();
            string wanted = trimmed.ToLowerInvariant();

            // ⚠ FAIL CLOSED WHEN THE FILTER CANNOT FILTER. A filter made only of supplementary characters
            // carries no weight in this schema's collation, so the comparison degrades to one that matches
            // every row - and the screen goes on announcing an active filter over the complete record set.
            // See CollationSafeFilter for the measurement and for the two remedies that were rejected.
            //
            // A CONTAINS comparison degrades exactly as a prefix one does, and worse: the measured module
            // search for a single rocket returned all eight visible placements.
            query = CollationSafeFilter.CannotDiscriminate(trimmed)
                ? query.Where(_ => false)
                : query.Where(placement =>
                    placement.Module.ModuleTitle != null
                    && placement.Module.ModuleTitle.ToLower().Contains(wanted));
        }

        query = ApplyPlacementOrder(query, sortBy, descending);

        // The module, its definition and the package behind it are loaded WITH THE WINDOW, so the graph is
        // hydrated for the rows being returned and for no others.
        IQueryable<TabModule> projection = query
            .Include(placement => placement.Module)
                .ThenInclude(module => module.ModuleDefinition)
                    .ThenInclude(definition => definition.DesktopModule);

        if (pageSize == 0)
        {
            List<TabModule> all = await projection.ToListAsync(cancellationToken).ConfigureAwait(false);

            return PagedResult<TabModule>.Unpaged(all);
        }

        int totalCount = await query.CountAsync(cancellationToken).ConfigureAwait(false);

        List<TabModule> rows = await projection
            .Skip(Paging.SkipCount(pageIndex, pageSize))
            .Take(pageSize)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return PagedResult<TabModule>.Create(rows, totalCount, pageIndex, pageSize);
    }

    /// <summary>
    /// Applies the caller's chosen module ordering to a placement query, then the fixed placement
    /// tie-break.
    /// </summary>
    /// <param name="query">The filtered placement query.</param>
    /// <param name="sortBy">The module property to order by, or <see langword="null"/> for the default.</param>
    /// <param name="descending">Whether the module ordering runs downwards.</param>
    /// <returns>The ordered query.</returns>
    /// <remarks>
    /// An ordering is applied unconditionally, and every arm terminates on the module's own key, so the
    /// order is TOTAL: without that, two modules sharing a title have no defined relative position and the
    /// same page coordinates can return different rows on two calls, which makes a pager unable to
    /// enumerate the collection.
    /// </remarks>
    private static IQueryable<TabModule> ApplyPlacementOrder(
        IQueryable<TabModule> query,
        string? sortBy,
        bool descending)
    {
        string property = string.IsNullOrWhiteSpace(sortBy)
            ? DefaultPlacementSortProperty
            : sortBy.Trim();

        IOrderedQueryable<TabModule> ordered = property.ToUpperInvariant() switch
        {
            "MODULEID" => descending
                ? query.OrderByDescending(placement => placement.Module.ModuleId)
                : query.OrderBy(placement => placement.Module.ModuleId),
            "ISDELETED" => descending
                ? query.OrderByDescending(placement => placement.Module.IsDeleted)
                    .ThenByDescending(placement => placement.Module.ModuleId)
                : query.OrderBy(placement => placement.Module.IsDeleted)
                    .ThenBy(placement => placement.Module.ModuleId),
            "STARTDATE" => descending
                ? query.OrderByDescending(placement => placement.Module.StartDate)
                    .ThenByDescending(placement => placement.Module.ModuleId)
                : query.OrderBy(placement => placement.Module.StartDate)
                    .ThenBy(placement => placement.Module.ModuleId),
            "ENDDATE" => descending
                ? query.OrderByDescending(placement => placement.Module.EndDate)
                    .ThenByDescending(placement => placement.Module.ModuleId)
                : query.OrderBy(placement => placement.Module.EndDate)
                    .ThenBy(placement => placement.Module.ModuleId),
            _ => descending
                ? query.OrderByDescending(placement => placement.Module.ModuleTitle)
                    .ThenByDescending(placement => placement.Module.ModuleId)
                : query.OrderBy(placement => placement.Module.ModuleTitle)
                    .ThenBy(placement => placement.Module.ModuleId),
        };

        return ordered
            .ThenBy(placement => placement.TabId)
            .ThenBy(placement => placement.ModuleOrder)
            .ThenBy(placement => placement.TabModuleId);
    }

    /// <inheritdoc />
    public Task AddTabModuleAsync(TabModule tabModule, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tabModule);
        cancellationToken.ThrowIfCancellationRequested();

        _dbContext.TabModules.Add(tabModule);

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    /// <remarks>
    /// Stages the whole placement. A caller intending only to reposition it should use <see
    /// cref="UpdateTabModuleOrderAsync"/>, which preserves the narrower write the legacy ordering procedure
    /// performed.
    /// </remarks>
    public Task UpdateTabModuleAsync(TabModule tabModule, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tabModule);
        cancellationToken.ThrowIfCancellationRequested();

        if (_dbContext.Entry(tabModule).State is EntityState.Detached)
        {
            _dbContext.Entry(tabModule).State = EntityState.Modified;
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    /// <remarks>
    /// Removing the last placement of a module does not remove the module: the two lifecycles are separate
    /// in the schema and remain separate here, so a caller that also intends to retire the module follows
    /// this with <see cref="UpdateAsync"/> or <see cref="DeleteAsync"/>. A pairing that resolves to no row
    /// is not an error.
    /// </remarks>
    public async Task DeleteTabModuleAsync(int tabId, int moduleId, CancellationToken cancellationToken = default)
    {
        TabModule? placement = await _dbContext.TabModules
            .FirstOrDefaultAsync(
                candidate => candidate.TabId == tabId && candidate.ModuleId == moduleId,
                cancellationToken)
            .ConfigureAwait(false);

        if (placement is null)
        {
            return;
        }

        _dbContext.TabModules.Remove(placement);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Ordered by <see cref="ModuleSetting.SettingName"/>, the second half of the composite key, which is
    /// unique per module and therefore a total order.
    /// </remarks>
    public async Task<IReadOnlyList<ModuleSetting>> GetModuleSettingsAsync(
        int moduleId,
        CancellationToken cancellationToken = default)
    {
        return await _dbContext.ModuleSettings
            .Where(setting => setting.ModuleId == moduleId)
            .OrderBy(setting => setting.SettingName)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task<ModuleSetting?> GetModuleSettingAsync(
        int moduleId,
        string settingName,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settingName);

        return _dbContext.ModuleSettings
            .FirstOrDefaultAsync(
                setting => setting.ModuleId == moduleId && setting.SettingName == settingName,
                cancellationToken);
    }

    /// <inheritdoc />
    public Task AddModuleSettingAsync(ModuleSetting moduleSetting, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(moduleSetting);
        cancellationToken.ThrowIfCancellationRequested();

        _dbContext.ModuleSettings.Add(moduleSetting);

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task UpdateModuleSettingAsync(ModuleSetting moduleSetting, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(moduleSetting);
        cancellationToken.ThrowIfCancellationRequested();

        if (_dbContext.Entry(moduleSetting).State is EntityState.Detached)
        {
            _dbContext.Entry(moduleSetting).State = EntityState.Modified;
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    /// <remarks>
    /// Addressed by key rather than by entity so that a caller which knows only the name it wishes to clear
    /// need not read the row first. A key that resolves to no row is not an error.
    /// </remarks>
    public async Task DeleteModuleSettingAsync(
        int moduleId,
        string settingName,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settingName);

        ModuleSetting? setting = await _dbContext.ModuleSettings
            .FirstOrDefaultAsync(
                candidate => candidate.ModuleId == moduleId && candidate.SettingName == settingName,
                cancellationToken)
            .ConfigureAwait(false);

        if (setting is null)
        {
            return;
        }

        _dbContext.ModuleSettings.Remove(setting);
    }

    /// <inheritdoc />
    // MIGRATION: replaces `DeleteModuleSettings(ByVal ModuleId As Integer)`.
    public async Task DeleteModuleSettingsAsync(int moduleId, CancellationToken cancellationToken = default)
    {
        List<ModuleSetting> settings = await _dbContext.ModuleSettings
            .Where(setting => setting.ModuleId == moduleId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        if (settings.Count is 0)
        {
            return;
        }

        _dbContext.ModuleSettings.RemoveRange(settings);
    }

    // A separate table and therefore a separate entity, keyed PK_TabModuleSettings (TabModuleID,
    // SettingName).

    /// <inheritdoc />
    /// <remarks>
    /// Keyed by the <em>placement</em> identifier, not the module identifier. Ordered by <see
    /// cref="TabModuleSetting.SettingName"/>, which is unique per placement.
    /// </remarks>
    public async Task<IReadOnlyList<TabModuleSetting>> GetTabModuleSettingsAsync(
        int tabModuleId,
        CancellationToken cancellationToken = default)
    {
        return await _dbContext.TabModuleSettings
            .Where(setting => setting.TabModuleId == tabModuleId)
            .OrderBy(setting => setting.SettingName)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task<TabModuleSetting?> GetTabModuleSettingAsync(
        int tabModuleId,
        string settingName,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settingName);

        return _dbContext.TabModuleSettings
            .FirstOrDefaultAsync(
                setting => setting.TabModuleId == tabModuleId && setting.SettingName == settingName,
                cancellationToken);
    }

    /// <inheritdoc />
    public Task AddTabModuleSettingAsync(
        TabModuleSetting tabModuleSetting,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tabModuleSetting);
        cancellationToken.ThrowIfCancellationRequested();

        _dbContext.TabModuleSettings.Add(tabModuleSetting);

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task UpdateTabModuleSettingAsync(
        TabModuleSetting tabModuleSetting,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tabModuleSetting);
        cancellationToken.ThrowIfCancellationRequested();

        if (_dbContext.Entry(tabModuleSetting).State is EntityState.Detached)
        {
            _dbContext.Entry(tabModuleSetting).State = EntityState.Modified;
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task DeleteTabModuleSettingAsync(
        int tabModuleId,
        string settingName,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settingName);

        TabModuleSetting? setting = await _dbContext.TabModuleSettings
            .FirstOrDefaultAsync(
                candidate => candidate.TabModuleId == tabModuleId && candidate.SettingName == settingName,
                cancellationToken)
            .ConfigureAwait(false);

        if (setting is null)
        {
            return;
        }

        _dbContext.TabModuleSettings.Remove(setting);
    }

    /// <inheritdoc />
    public async Task DeleteTabModuleSettingsAsync(int tabModuleId, CancellationToken cancellationToken = default)
    {
        List<TabModuleSetting> settings = await _dbContext.TabModuleSettings
            .Where(setting => setting.TabModuleId == tabModuleId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        if (settings.Count is 0)
        {
            return;
        }

        _dbContext.TabModuleSettings.RemoveRange(settings);
    }
}
