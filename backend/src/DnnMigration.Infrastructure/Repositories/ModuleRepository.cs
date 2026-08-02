using DnnMigration.Domain.Abstractions.Repositories;
using DnnMigration.Domain.Common;
using DnnMigration.Domain.Entities;
using DnnMigration.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace DnnMigration.Infrastructure.Repositories;

/// <summary>
/// Reads and writes <see cref="Module"/> content components, their page placements and their settings.
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

    /// <inheritdoc />
    /// <remarks>
    /// The definition is loaded with each module so that a listing can name the module type without
    /// one further read per row. Restricting to a page is expressed as an existence test over the
    /// placement table rather than as a join, so a module placed on the same page twice still appears
    /// once.
    /// </remarks>
    public async Task<PagedResult<Module>> ListAsync(
        int portalId,
        int? tabId,
        bool includeDeleted,
        int pageIndex,
        int pageSize,
        string? titleFilter,
        CancellationToken cancellationToken = default)
    {
        IQueryable<Module> query = _context.Modules
            .Include(m => m.ModuleDefinition)
            .Where(m => m.PortalId == portalId);

        if (!includeDeleted)
        {
            query = query.Where(m => !m.IsDeleted);
        }

        if (tabId.HasValue)
        {
            // TabID is IDENTITY(0, 1), so zero is a legitimate page key and the presence of a value
            // is what selects the filter rather than its magnitude.
            int page = tabId.Value;
            query = query.Where(m => _context.TabModules.Any(p => p.ModuleId == m.ModuleId && p.TabId == page));
        }

        if (!string.IsNullOrWhiteSpace(titleFilter))
        {
            // ModuleTitle is nullable, so the null test precedes the comparison; lower-casing both
            // sides keeps the match case-insensitive whatever collation the installation uses.
            string wanted = titleFilter.Trim().ToLowerInvariant();
            query = query.Where(m => m.ModuleTitle != null && m.ModuleTitle.ToLower().Contains(wanted));
        }

        query = query
            .OrderBy(m => m.ModuleTitle)
            .ThenBy(m => m.ModuleId);

        if (pageSize == 0)
        {
            List<Module> all = await query.ToListAsync(cancellationToken).ConfigureAwait(false);
            return PagedResult<Module>.Unpaged(all);
        }

        int totalCount = await query.CountAsync(cancellationToken).ConfigureAwait(false);

        List<Module> rows = await query
            .Skip(pageIndex * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return PagedResult<Module>.Create(rows, totalCount, pageIndex, pageSize);
    }

    /// <inheritdoc />
    /// <remarks>
    /// When placements are requested, <see cref="Module.TabModules"/> is populated as part of the
    /// same read. Callers that decide whether a change has a portal-wide effect, or that resolve an
    /// inherited view permission, need every placement of the module; leaving the collection empty
    /// would make an unplaced module and an unloaded one indistinguishable.
    /// </remarks>
    public Task<Module?> GetAsync(int moduleId, bool includePlacements = false, CancellationToken cancellationToken = default)
    {
        IQueryable<Module> query = _context.Modules.Include(m => m.ModuleDefinition);

        if (includePlacements)
        {
            query = query.Include(m => m.TabModules);
        }

        // ModuleID is IDENTITY(0, 1), so zero is a legitimate key. Absence is reported as null.
        return query.FirstOrDefaultAsync(m => m.ModuleId == moduleId, cancellationToken);
    }

    /// <inheritdoc />
    public Task<TabModule?> GetPlacementAsync(int tabModuleId, CancellationToken cancellationToken = default)
    {
        return _context.TabModules
            .FirstOrDefaultAsync(p => p.TabModuleId == tabModuleId, cancellationToken);
    }

    /// <inheritdoc />
    /// <remarks>
    /// <c>IX_TabModules</c> is unique over <c>(TabID, ModuleID)</c>, so a page and a module identify
    /// at most one placement and this overload can return a single row rather than a collection.
    /// </remarks>
    public Task<TabModule?> GetPlacementAsync(int tabId, int moduleId, CancellationToken cancellationToken = default)
    {
        return _context.TabModules
            .FirstOrDefaultAsync(p => p.TabId == tabId && p.ModuleId == moduleId, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<TabModule>> ListPlacementsAsync(int moduleId, CancellationToken cancellationToken = default)
    {
        return await _context.TabModules
            .Where(p => p.ModuleId == moduleId)
            .OrderBy(p => p.TabId)
            .ThenBy(p => p.TabModuleId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ModuleSetting>> ListSettingsAsync(int moduleId, CancellationToken cancellationToken = default)
    {
        return await _context.ModuleSettings
            .Where(s => s.ModuleId == moduleId)
            .OrderBy(s => s.SettingName)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<TabModuleSetting>> ListPlacementSettingsAsync(int tabModuleId, CancellationToken cancellationToken = default)
    {
        return await _context.TabModuleSettings
            .Where(s => s.TabModuleId == tabModuleId)
            .OrderBy(s => s.SettingName)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public void Add(Module module)
    {
        ArgumentNullException.ThrowIfNull(module);
        _context.Modules.Add(module);
    }

    /// <inheritdoc />
    public void AddPlacement(TabModule placement)
    {
        ArgumentNullException.ThrowIfNull(placement);
        _context.TabModules.Add(placement);
    }

    /// <inheritdoc />
    public void RemovePlacement(TabModule placement)
    {
        ArgumentNullException.ThrowIfNull(placement);
        _context.TabModules.Remove(placement);
    }

    /// <inheritdoc />
    public void AddSetting(ModuleSetting setting)
    {
        ArgumentNullException.ThrowIfNull(setting);
        _context.ModuleSettings.Add(setting);
    }

    /// <inheritdoc />
    public void RemoveSetting(ModuleSetting setting)
    {
        ArgumentNullException.ThrowIfNull(setting);
        _context.ModuleSettings.Remove(setting);
    }

    /// <inheritdoc />
    public void AddPlacementSetting(TabModuleSetting setting)
    {
        ArgumentNullException.ThrowIfNull(setting);
        _context.TabModuleSettings.Add(setting);
    }

    /// <inheritdoc />
    public void RemovePlacementSetting(TabModuleSetting setting)
    {
        ArgumentNullException.ThrowIfNull(setting);
        _context.TabModuleSettings.Remove(setting);
    }
}
