using DnnMigration.Domain.Abstractions.Repositories;
using DnnMigration.Domain.Entities;
using DnnMigration.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace DnnMigration.Infrastructure.Repositories;

/// <summary>
/// Reads and writes <see cref="Module"/> content components, their page placements and both of their
/// settings collections.
/// </summary>
/// <remarks>
/// MIGRATION: replaces the sixty-three module stored procedures reached from the legacy core data
/// provider and the data-access half of <c>Library/Components/Modules/ModuleController.vb</c>. The
/// hand-rolled hydration in that file - which instantiated a <c>ModuleInfo</c> and then assigned each
/// column through <c>Convert.ToInt32(Null.SetNull(dr("Column"), currentValue))</c>, one statement per
/// column - is replaced entirely by the Entity Framework materialiser.
/// <para>
/// The legacy <c>ModuleInfo</c> was a single flattened class over a four-table join. Here a module,
/// its placement on a page, its module-scoped settings and its placement-scoped settings are four
/// distinct entities along the real table boundaries, which is why placements and settings are
/// separate members rather than properties of one result.
/// </para>
/// <para>
/// Every mutating member stages its change and returns without writing; the unit of work is the single
/// commit point. That is why no add member yields a generated key - see the write semantics documented
/// on <see cref="IModuleRepository"/>.
/// </para>
/// <para>
/// No read member applies <c>AsNoTracking</c>, for the reason given on <see cref="PortalRepository"/>.
/// </para>
/// </remarks>
internal sealed class ModuleRepository : IModuleRepository
{
    private readonly DnnDbContext _context;

    /// <summary>Initialises a new instance of the <see cref="ModuleRepository"/> class.</summary>
    /// <param name="context">The unit-of-work scoped database context.</param>
    /// <exception cref="ArgumentNullException"><paramref name="context"/> is <see langword="null"/>.</exception>
    public ModuleRepository(DnnDbContext context)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
    }

    // ---------------------------------------------------------------------------------------------
    // Module
    // ---------------------------------------------------------------------------------------------

    /// <inheritdoc />
    /// <remarks>
    /// The definition is loaded with each module so that a caller can name the module kind without one
    /// further read per row. Ordering is stable so that a caller composing a page of results at the
    /// Application layer sees a deterministic sequence.
    /// </remarks>
    public async Task<IReadOnlyList<Module>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        return await _context.Modules
            .Include(m => m.ModuleDefinition)
            .OrderBy(m => m.ModuleTitle)
            .ThenBy(m => m.ModuleId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Modules flagged deleted are included, matching the legacy procedure, which applied no such
    /// predicate. Filtering the recycle bin is the caller's decision.
    /// </remarks>
    public async Task<IReadOnlyList<Module>> GetByPortalIdAsync(int portalId, CancellationToken cancellationToken = default)
    {
        return await _context.Modules
            .Include(m => m.ModuleDefinition)
            .Where(m => m.PortalId == portalId)
            .OrderBy(m => m.ModuleTitle)
            .ThenBy(m => m.ModuleId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<Module>> GetAllTabsModulesAsync(
        int portalId,
        bool allTabs,
        CancellationToken cancellationToken = default)
    {
        return await _context.Modules
            .Include(m => m.ModuleDefinition)
            .Where(m => m.PortalId == portalId && m.AllTabs == allTabs)
            .OrderBy(m => m.ModuleTitle)
            .ThenBy(m => m.ModuleId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    /// <remarks>
    /// The placements are loaded alongside the module. Callers that decide whether a change has a
    /// portal-wide effect, or that resolve an inherited view permission, need them, and leaving the
    /// collection empty would make an unplaced module and an unloaded one indistinguishable.
    /// </remarks>
    public Task<Module?> GetByIdAsync(int moduleId, CancellationToken cancellationToken = default)
    {
        // ModuleID is IDENTITY(0, 1), so zero is a legitimate key. Absence is reported as null.
        return _context.Modules
            .Include(m => m.ModuleDefinition)
            .Include(m => m.TabModules)
            .FirstOrDefaultAsync(m => m.ModuleId == moduleId, cancellationToken);
    }

    /// <inheritdoc />
    /// <remarks>
    /// <c>IX_TabModules</c> is unique over <c>(TabID, ModuleID)</c>, so a page and a module identify at
    /// most one placement and this member can return a single row rather than a collection.
    /// </remarks>
    public Task<TabModule?> GetTabModuleAsync(int tabId, int moduleId, CancellationToken cancellationToken = default)
    {
        return _context.TabModules
            .FirstOrDefaultAsync(p => p.TabId == tabId && p.ModuleId == moduleId, cancellationToken);
    }

    /// <inheritdoc />
    /// <remarks>
    /// The friendly name identifies the definition, so the predicate reaches through the loaded
    /// definition rather than duplicating the name onto the module. The comparison is case-insensitive
    /// whatever collation the installation uses, and the empty name is matched literally because the
    /// legacy sentinel for a missing string was the empty string.
    /// </remarks>
    public Task<Module?> GetByDefinitionAsync(
        int portalId,
        string friendlyName,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(friendlyName);

        string wanted = friendlyName.ToLowerInvariant();

        return _context.Modules
            .Include(m => m.ModuleDefinition)
            .Where(m => m.PortalId == portalId && m.ModuleDefinition.FriendlyName.ToLower() == wanted)
            .OrderBy(m => m.ModuleId)
            .FirstOrDefaultAsync(cancellationToken);
    }

    /// <inheritdoc />
    public Task AddAsync(Module module, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(module);
        cancellationToken.ThrowIfCancellationRequested();

        // Staged, not written. The key is assigned when the unit of work commits, which is why this
        // member yields no identifier: the legacy procedure returned one only because it ended with
        // select SCOPE_IDENTITY(), and returning one here would force a flush that split the
        // multi-table portal creation - of which this table is one of five - into independently
        // durable statements.
        _context.Modules.Add(module);

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    /// <remarks>
    /// A module read through this repository is already tracked, so its modifications are staged by the
    /// tracker and this call is the caller's explicit statement of intent. An untracked instance is
    /// attached and marked modified so the same call works for it too.
    /// </remarks>
    public Task UpdateAsync(Module module, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(module);
        cancellationToken.ThrowIfCancellationRequested();

        if (_context.Entry(module).State is EntityState.Detached)
        {
            _context.Modules.Update(module);
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    /// <remarks>
    /// A hard delete, matching the legacy procedure. Sending a module to the recycle bin is a change to
    /// <see cref="Module.IsDeleted"/> staged through <see cref="UpdateAsync"/>, and this member is not a
    /// substitute for it.
    /// </remarks>
    public async Task DeleteAsync(int moduleId, CancellationToken cancellationToken = default)
    {
        // Resolved before removal because the contract identifies its target by identifier, as the
        // legacy procedure did. When the caller already holds the entity this resolves from the change
        // tracker without a round trip.
        Module? module = await _context.Modules
            .FirstOrDefaultAsync(candidate => candidate.ModuleId == moduleId, cancellationToken)
            .ConfigureAwait(false);

        if (module is null)
        {
            // Nothing to stage. Absence is not an error: a caller that has already established absence
            // need not distinguish the two cases.
            return;
        }

        _context.Modules.Remove(module);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Scoped to one pane of one page and ordered as the pane renders, so a renumbering pass sees
    /// exactly the rows whose order it is about to rewrite. The pane name is compared literally; the
    /// legacy store held a missing pane name as the empty string rather than as null.
    /// </remarks>
    public async Task<IReadOnlyList<TabModule>> GetTabModuleOrderAsync(
        int tabId,
        string paneName,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(paneName);

        return await _context.TabModules
            .Where(p => p.TabId == tabId && p.PaneName == paneName)
            .OrderBy(p => p.ModuleOrder)
            .ThenBy(p => p.TabModuleId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    /// <remarks>
    /// The legacy procedure wrote only the ordering columns, and that narrowness is preserved by marking
    /// just <see cref="TabModule.ModuleOrder"/> and <see cref="TabModule.PaneName"/> modified when the
    /// placement is not already tracked. A renumbering pass therefore cannot carry a placement's
    /// unrelated edits along with it.
    /// </remarks>
    public Task UpdateTabModuleOrderAsync(TabModule tabModule, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tabModule);
        cancellationToken.ThrowIfCancellationRequested();

        if (_context.Entry(tabModule).State is EntityState.Detached)
        {
            _context.TabModules.Attach(tabModule);

            _context.Entry(tabModule).Property(p => p.ModuleOrder).IsModified = true;
            _context.Entry(tabModule).Property(p => p.PaneName).IsModified = true;
        }

        return Task.CompletedTask;
    }

    // ---------------------------------------------------------------------------------------------
    // TabModule
    // ---------------------------------------------------------------------------------------------

    /// <inheritdoc />
    public Task<TabModule?> GetTabModuleByIdAsync(int tabModuleId, CancellationToken cancellationToken = default)
    {
        return _context.TabModules
            .FirstOrDefaultAsync(p => p.TabModuleId == tabModuleId, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<TabModule>> GetTabModulesByModuleIdAsync(
        int moduleId,
        CancellationToken cancellationToken = default)
    {
        return await _context.TabModules
            .Where(p => p.ModuleId == moduleId)
            .OrderBy(p => p.TabId)
            .ThenBy(p => p.TabModuleId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task AddTabModuleAsync(TabModule tabModule, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tabModule);
        cancellationToken.ThrowIfCancellationRequested();

        // The legacy procedure was already declared Sub and never returned a key, so nothing is lost by
        // staging the insertion here and letting the unit of work assign TabModuleId.
        _context.TabModules.Add(tabModule);

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task UpdateTabModuleAsync(TabModule tabModule, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tabModule);
        cancellationToken.ThrowIfCancellationRequested();

        if (_context.Entry(tabModule).State is EntityState.Detached)
        {
            _context.TabModules.Update(tabModule);
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    /// <remarks>
    /// Removing the last placement of a module does not remove the module: the two lifecycles are
    /// separate in the schema and remain separate here.
    /// </remarks>
    public async Task DeleteTabModuleAsync(int tabId, int moduleId, CancellationToken cancellationToken = default)
    {
        TabModule? placement = await _context.TabModules
            .FirstOrDefaultAsync(p => p.TabId == tabId && p.ModuleId == moduleId, cancellationToken)
            .ConfigureAwait(false);

        if (placement is null)
        {
            return;
        }

        _context.TabModules.Remove(placement);
    }

    // ---------------------------------------------------------------------------------------------
    // ModuleSetting - composite key (ModuleId, SettingName)
    // ---------------------------------------------------------------------------------------------

    /// <inheritdoc />
    public async Task<IReadOnlyList<ModuleSetting>> GetModuleSettingsAsync(
        int moduleId,
        CancellationToken cancellationToken = default)
    {
        return await _context.ModuleSettings
            .Where(s => s.ModuleId == moduleId)
            .OrderBy(s => s.SettingName)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    /// <remarks>
    /// The name is compared literally rather than trimmed or widened, so an empty name addresses the row
    /// the legacy store could genuinely hold under an empty key.
    /// </remarks>
    public Task<ModuleSetting?> GetModuleSettingAsync(
        int moduleId,
        string settingName,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settingName);

        return _context.ModuleSettings
            .FirstOrDefaultAsync(s => s.ModuleId == moduleId && s.SettingName == settingName, cancellationToken);
    }

    /// <inheritdoc />
    public Task AddModuleSettingAsync(ModuleSetting moduleSetting, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(moduleSetting);
        cancellationToken.ThrowIfCancellationRequested();

        _context.ModuleSettings.Add(moduleSetting);

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task UpdateModuleSettingAsync(ModuleSetting moduleSetting, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(moduleSetting);
        cancellationToken.ThrowIfCancellationRequested();

        if (_context.Entry(moduleSetting).State is EntityState.Detached)
        {
            _context.ModuleSettings.Update(moduleSetting);
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task DeleteModuleSettingAsync(
        int moduleId,
        string settingName,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settingName);

        ModuleSetting? setting = await _context.ModuleSettings
            .FirstOrDefaultAsync(s => s.ModuleId == moduleId && s.SettingName == settingName, cancellationToken)
            .ConfigureAwait(false);

        if (setting is null)
        {
            return;
        }

        _context.ModuleSettings.Remove(setting);
    }

    /// <inheritdoc />
    /// <remarks>
    /// A module with no settings is staged as no removals at all, which is not an error. The
    /// affected-row count is what the unit of work returns on commit.
    /// </remarks>
    public async Task DeleteModuleSettingsAsync(int moduleId, CancellationToken cancellationToken = default)
    {
        List<ModuleSetting> settings = await _context.ModuleSettings
            .Where(s => s.ModuleId == moduleId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        if (settings.Count is 0)
        {
            return;
        }

        _context.ModuleSettings.RemoveRange(settings);
    }

    // ---------------------------------------------------------------------------------------------
    // TabModuleSetting - composite key (TabModuleId, SettingName)
    // ---------------------------------------------------------------------------------------------

    /// <inheritdoc />
    public async Task<IReadOnlyList<TabModuleSetting>> GetTabModuleSettingsAsync(
        int tabModuleId,
        CancellationToken cancellationToken = default)
    {
        return await _context.TabModuleSettings
            .Where(s => s.TabModuleId == tabModuleId)
            .OrderBy(s => s.SettingName)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Keyed by the placement identifier, not the module identifier. Passing a module key here would
    /// silently read another placement's settings, so the two key spaces are kept strictly apart.
    /// </remarks>
    public Task<TabModuleSetting?> GetTabModuleSettingAsync(
        int tabModuleId,
        string settingName,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settingName);

        return _context.TabModuleSettings
            .FirstOrDefaultAsync(
                s => s.TabModuleId == tabModuleId && s.SettingName == settingName,
                cancellationToken);
    }

    /// <inheritdoc />
    public Task AddTabModuleSettingAsync(
        TabModuleSetting tabModuleSetting,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tabModuleSetting);
        cancellationToken.ThrowIfCancellationRequested();

        _context.TabModuleSettings.Add(tabModuleSetting);

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task UpdateTabModuleSettingAsync(
        TabModuleSetting tabModuleSetting,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tabModuleSetting);
        cancellationToken.ThrowIfCancellationRequested();

        if (_context.Entry(tabModuleSetting).State is EntityState.Detached)
        {
            _context.TabModuleSettings.Update(tabModuleSetting);
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

        TabModuleSetting? setting = await _context.TabModuleSettings
            .FirstOrDefaultAsync(
                s => s.TabModuleId == tabModuleId && s.SettingName == settingName,
                cancellationToken)
            .ConfigureAwait(false);

        if (setting is null)
        {
            return;
        }

        _context.TabModuleSettings.Remove(setting);
    }

    /// <inheritdoc />
    public async Task DeleteTabModuleSettingsAsync(int tabModuleId, CancellationToken cancellationToken = default)
    {
        List<TabModuleSetting> settings = await _context.TabModuleSettings
            .Where(s => s.TabModuleId == tabModuleId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        if (settings.Count is 0)
        {
            return;
        }

        _context.TabModuleSettings.RemoveRange(settings);
    }
}
