using DnnMigration.Domain.Common;
using DnnMigration.Domain.Entities;

namespace DnnMigration.Domain.Abstractions.Repositories;

/// <summary>
/// Reads and writes the <see cref="Module"/> aggregate, its page placements and its settings.
/// </summary>
/// <remarks>
/// MIGRATION: replaces the module slice of the legacy data provider (63 stored procedures) and the
/// data-access half of <c>ModuleController.vb</c>. The legacy <c>ModuleInfo</c> class was a flattened
/// join over <c>Modules</c>, <c>TabModules</c>, <c>ModuleDefinitions</c> and <c>ModuleControls</c>;
/// this contract keeps the module and its placement separate, matching the real table boundaries.
/// </remarks>
public interface IModuleRepository
{
    /// <summary>Returns one page of modules within a portal.</summary>
    /// <param name="portalId">Portal identifier.</param>
    /// <param name="tabId">Restrict to the modules placed on one page, or <see langword="null"/> for the whole portal.</param>
    /// <param name="includeDeleted">Whether to include modules in the recycle bin.</param>
    /// <param name="pageIndex">Zero-based page index.</param>
    /// <param name="pageSize">Page size; 0 requests every match unpaged.</param>
    /// <param name="titleFilter">Case-insensitive substring of the module title, or <see langword="null"/> for all.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<PagedResult<Module>> ListAsync(
        int portalId,
        int? tabId,
        bool includeDeleted,
        int pageIndex,
        int pageSize,
        string? titleFilter,
        CancellationToken cancellationToken = default);

    /// <summary>Returns one module by key, or <see langword="null"/>.</summary>
    /// <param name="moduleId">Module identifier. 0 is legitimate: <c>Modules.ModuleID</c> is <c>IDENTITY(0, 1)</c>.</param>
    /// <param name="includePlacements">Whether to load the module's page placements with it.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<Module?> GetAsync(int moduleId, bool includePlacements = false, CancellationToken cancellationToken = default);

    /// <summary>Returns one page placement by key, or <see langword="null"/>.</summary>
    /// <param name="tabModuleId">Placement identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<TabModule?> GetPlacementAsync(int tabModuleId, CancellationToken cancellationToken = default);

    /// <summary>Returns the placement of one module on one page, or <see langword="null"/>.</summary>
    /// <param name="tabId">Page identifier.</param>
    /// <param name="moduleId">Module identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<TabModule?> GetPlacementAsync(int tabId, int moduleId, CancellationToken cancellationToken = default);

    /// <summary>Returns every placement of one module.</summary>
    /// <param name="moduleId">Module identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<IReadOnlyList<TabModule>> ListPlacementsAsync(int moduleId, CancellationToken cancellationToken = default);

    /// <summary>Returns the module-scoped settings of one module.</summary>
    /// <param name="moduleId">Module identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<IReadOnlyList<ModuleSetting>> ListSettingsAsync(int moduleId, CancellationToken cancellationToken = default);

    /// <summary>Returns the placement-scoped settings of one placement.</summary>
    /// <param name="tabModuleId">Placement identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<IReadOnlyList<TabModuleSetting>> ListPlacementSettingsAsync(int tabModuleId, CancellationToken cancellationToken = default);

    /// <summary>Stages a new module for insertion.</summary>
    /// <param name="module">The module to insert.</param>
    void Add(Module module);

    /// <summary>Stages a new page placement for insertion.</summary>
    /// <param name="placement">The placement to insert.</param>
    void AddPlacement(TabModule placement);

    /// <summary>Stages a page placement for deletion.</summary>
    /// <param name="placement">The placement to delete.</param>
    void RemovePlacement(TabModule placement);

    /// <summary>Stages a new module-scoped setting for insertion.</summary>
    /// <param name="setting">The setting to insert.</param>
    void AddSetting(ModuleSetting setting);

    /// <summary>Stages a module-scoped setting for deletion.</summary>
    /// <param name="setting">The setting to delete.</param>
    void RemoveSetting(ModuleSetting setting);

    /// <summary>Stages a new placement-scoped setting for insertion.</summary>
    /// <param name="setting">The setting to insert.</param>
    void AddPlacementSetting(TabModuleSetting setting);

    /// <summary>Stages a placement-scoped setting for deletion.</summary>
    /// <param name="setting">The setting to delete.</param>
    void RemovePlacementSetting(TabModuleSetting setting);
}
