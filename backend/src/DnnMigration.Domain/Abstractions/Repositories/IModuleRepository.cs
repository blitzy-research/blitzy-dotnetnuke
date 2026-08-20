using DnnMigration.Domain.Common;
using DnnMigration.Domain.Entities;

namespace DnnMigration.Domain.Abstractions.Repositories;

/// <summary>
/// Reads and writes the <b>Module</b> aggregate - the pluggable content component with a lifecycle -
/// together with its placements on pages and both of its settings collections.
/// </summary>
/// <remarks>
/// <para>
/// <b>Identifier semantics.</b> No identifier value means "absent" on this contract. <c>Modules</c> and
/// <c>Tabs</c> both seed their identity column at zero (<c>IDENTITY (0, 1)</c>, baseline DDL lines 221 and
/// 140), so zero is a real, persisted row; <c>Portals</c> seeds at minus one, so minus one is a real portal
/// as well as the value the legacy sentinel helper used for "no integer".
/// </para>
/// <para>
/// <b>String semantics.</b> The legacy sentinel for a missing string was the <em>empty string</em>, not
/// <see langword="null"/>, so a SQL <c>NULL</c> and <c>""</c> were indistinguishable once read through the
/// legacy path and an empty setting name was legally representable.
/// </para>
/// </remarks>
public interface IModuleRepository
{
    /// <summary>Returns every module in the installation, across all tenants.</summary>
    /// <param name="cancellationToken">Propagates notification that the operation should be cancelled.</param>
    /// <returns>
    /// Every <see cref="Module"/> row, including tenant-less host modules and modules already flagged
    /// deleted.
    /// </returns>
    Task<IReadOnlyList<Module>> GetAllAsync(CancellationToken cancellationToken = default);

    /// <summary>Returns every module belonging to one tenant.</summary>
    /// <param name="portalId">The owning portal's identifier.</param>
    /// <param name="cancellationToken">Propagates notification that the operation should be cancelled.</param>
    /// <returns>
    /// The tenant's <see cref="Module"/> rows, or an empty list when the tenant owns none or does not
    /// exist.
    /// </returns>
    Task<IReadOnlyList<Module>> GetByPortalIdAsync(int portalId, CancellationToken cancellationToken = default);

    /// <summary>Returns a tenant's modules restricted by their all-pages flag.</summary>
    /// <param name="portalId">The owning portal's identifier.</param>
    /// <param name="allTabs">
    /// When <see langword="true"/>, restricts the result to modules whose <see cref="Module.AllTabs"/> flag
    /// is set - those that appear on every page of the tenant.
    /// </param>
    /// <param name="cancellationToken">Propagates notification that the operation should be cancelled.</param>
    /// <returns>The matching <see cref="Module"/> rows, or an empty list when none match.</returns>
    Task<IReadOnlyList<Module>> GetAllTabsModulesAsync(int portalId, bool allTabs, CancellationToken cancellationToken = default);

    /// <summary>Returns one module by its identifier, or <see langword="null"/> when no such module exists.</summary>
    /// <param name="moduleId">The module's identifier.</param>
    /// <param name="cancellationToken">Propagates notification that the operation should be cancelled.</param>
    /// <returns>
    /// The matching <see cref="Module"/>, or <see langword="null"/> when the identifier resolves to no row.
    /// </returns>
    Task<Module?> GetByIdAsync(int moduleId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the placement of one module on one page, or <see langword="null"/> when the module is not
    /// placed on that page.
    /// </summary>
    /// <param name="tabId">The page's identifier.</param>
    /// <param name="moduleId">The module's identifier.</param>
    /// <param name="cancellationToken">Propagates notification that the operation should be cancelled.</param>
    /// <returns>
    /// The single matching <see cref="TabModule"/>, or <see langword="null"/> when the pairing does not
    /// exist.
    /// </returns>
    Task<TabModule?> GetTabModuleAsync(int tabId, int moduleId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the first module in a tenant whose definition carries the supplied friendly name, or <see
    /// langword="null"/> when the tenant has no instance of that definition.
    /// </summary>
    /// <param name="portalId">The owning portal's identifier.</param>
    /// <param name="friendlyName">The definition's friendly name.</param>
    /// <param name="cancellationToken">Propagates notification that the operation should be cancelled.</param>
    /// <returns>A matching <see cref="Module"/>, or <see langword="null"/> when none matches.</returns>
    Task<Module?> GetByDefinitionAsync(int portalId, string friendlyName, CancellationToken cancellationToken = default);

    /// <summary>Stages a new module for insertion.</summary>
    /// <param name="module">The module to insert.</param>
    /// <param name="cancellationToken">Propagates notification that the operation should be cancelled.</param>
    /// <returns>A task that completes once the insertion is staged.</returns>
    Task AddAsync(Module module, CancellationToken cancellationToken = default);

    /// <summary>Stages an existing module's modifications.</summary>
    /// <param name="module">The module whose current state should be persisted.</param>
    /// <param name="cancellationToken">Propagates notification that the operation should be cancelled.</param>
    /// <returns>A task that completes once the modification is staged.</returns>
    Task UpdateAsync(Module module, CancellationToken cancellationToken = default);

    /// <summary>Stages the permanent removal of one module.</summary>
    /// <param name="moduleId">The identifier of the module to remove.</param>
    /// <param name="cancellationToken">Propagates notification that the operation should be cancelled.</param>
    /// <returns>A task that completes once the removal is staged.</returns>
    Task DeleteAsync(int moduleId, CancellationToken cancellationToken = default);

    /// <summary>Stages the permanent removal of every module in a set the caller already holds.</summary>
    /// <param name="modules">The modules to remove, as they were read.</param>
    /// <param name="cancellationToken">Propagates notification that the operation should be cancelled.</param>
    /// <returns>A task that completes once every removal is staged.</returns>
    /// <remarks>
    /// MIGRATION: net-new, and it exists to remove a round trip per row rather than to add a capability.
    /// Tenant deletion has to remove the tenant's modules explicitly, because <c>FK_Modules_Portals</c> is
    /// the one foreign key into <c>dbo.Portals</c> that the 03.00.09 upgrade script re-adds WITHOUT a
    /// cascade clause.
    /// </remarks>
    Task DeleteRangeAsync(IReadOnlyCollection<Module> modules, CancellationToken cancellationToken = default);

    /// <summary>Returns the placements within one pane of one page, in the order they are rendered.</summary>
    /// <param name="tabId">The page's identifier.</param>
    /// <param name="paneName">The pane's name as the page's skin declares it.</param>
    /// <param name="cancellationToken">Propagates notification that the operation should be cancelled.</param>
    /// <returns>
    /// The pane's <see cref="TabModule"/> rows ordered by <see cref="TabModule.ModuleOrder"/>, or an empty
    /// list when the pane holds nothing.
    /// </returns>
    Task<IReadOnlyList<TabModule>> GetTabModuleOrderAsync(int tabId, string paneName, CancellationToken cancellationToken = default);

    /// <summary>Stages a change to one placement's position within a pane.</summary>
    /// <param name="tabModule">
    /// The placement carrying the intended <see cref="TabModule.ModuleOrder"/> and <see
    /// cref="TabModule.PaneName"/>.
    /// </param>
    /// <param name="cancellationToken">Propagates notification that the operation should be cancelled.</param>
    /// <returns>A task that completes once the change is staged.</returns>
    Task UpdateTabModuleOrderAsync(TabModule tabModule, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns one placement by its own identifier, or <see langword="null"/> when no such placement
    /// exists.
    /// </summary>
    /// <param name="tabModuleId">The placement's surrogate identifier.</param>
    /// <param name="cancellationToken">Propagates notification that the operation should be cancelled.</param>
    /// <returns>
    /// The matching <see cref="TabModule"/>, or <see langword="null"/> when the identifier resolves to no
    /// row.
    /// </returns>
    /// <remarks>
    /// No provider member corresponds to this read, because the legacy flattened join always arrived at a
    /// placement through its module and page rather than through its own key. Splitting that join makes the
    /// surrogate key addressable, and the mandated placement-settings block is keyed by it - so a caller
    /// holding only a settings row must be able to resolve the placement that owns it.
    /// </remarks>
    Task<TabModule?> GetTabModuleByIdAsync(int tabModuleId, CancellationToken cancellationToken = default);

    /// <summary>Returns every placement of one module - that is, every page the module appears on.</summary>
    /// <param name="moduleId">The module's identifier.</param>
    /// <param name="cancellationToken">Propagates notification that the operation should be cancelled.</param>
    /// <returns>
    /// The module's <see cref="TabModule"/> rows in a stable order, or an empty list when the module is not
    /// placed anywhere.
    /// </returns>
    Task<IReadOnlyList<TabModule>> GetTabModulesByModuleIdAsync(int moduleId, CancellationToken cancellationToken = default);

    /// <summary>Returns every placement of each of many modules in one read.</summary>
    /// <param name="moduleIds">The modules whose placements are wanted.</param>
    /// <param name="cancellationToken">Propagates notification that the operation should be cancelled.</param>
    /// <returns>
    /// The placements of every named module in a stable order, flat rather than grouped, so a caller groups
    /// by <see cref="TabModule.ModuleId"/> itself.
    /// </returns>
    Task<IReadOnlyList<TabModule>> GetTabModulesByModuleIdsAsync(
        IReadOnlyCollection<int> moduleIds,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns one page of a tenant's module PLACEMENTS, filtered, ordered, counted and windowed by the
    /// store.
    /// </summary>
    /// <param name="portalId">The owning tenant, matched exactly.</param>
    /// <param name="tabId">
    /// Restrict to the placements on one page, or <see langword="null"/> for every page of the tenant.
    /// <c>Tabs.TabID</c> is <c>IDENTITY(0, 1)</c>, so the PRESENCE of a value selects the filter and its
    /// magnitude never does.
    /// </param>
    /// <param name="includeDeleted">
    /// Whether placements of modules already flagged deleted - the recycle bin - are included.
    /// </param>
    /// <param name="titleQuery">
    /// A fragment the module title must contain, case-insensitively, or <see langword="null"/> for no title
    /// restriction.
    /// </param>
    /// <param name="sortBy">
    /// The MODULE property the rows are ordered by, or <see langword="null"/> for the default.
    /// </param>
    /// <param name="descending">Whether the module ordering runs downwards.</param>
    /// <param name="pageIndex">The page to return, counted from zero.</param>
    /// <param name="pageSize">The page width, or zero for every matching row.</param>
    /// <param name="cancellationToken">Propagates notification that the operation should be cancelled.</param>
    /// <returns>
    /// The requested window of placements together with the total number of placements the whole filtered
    /// collection holds.
    /// </returns>
    /// <remarks>
    /// MIGRATION: net-new, and it replaces an Application-layer composition rather than a legacy procedure
    /// - the legacy module block of the data provider carries no paging member of any kind.
    /// </remarks>
    Task<PagedResult<TabModule>> ListPlacementsAsync(
        int portalId,
        int? tabId,
        bool includeDeleted,
        string? titleQuery,
        string? sortBy,
        bool descending,
        int pageIndex,
        int pageSize,
        CancellationToken cancellationToken = default);

    /// <summary>Stages a new placement of a module on a page.</summary>
    /// <param name="tabModule">The placement to insert.</param>
    /// <param name="cancellationToken">Propagates notification that the operation should be cancelled.</param>
    /// <returns>A task that completes once the insertion is staged, yielding no identifier by design.</returns>
    Task AddTabModuleAsync(TabModule tabModule, CancellationToken cancellationToken = default);

    /// <summary>Stages an existing placement's modifications.</summary>
    /// <param name="tabModule">The placement whose current state should be persisted.</param>
    /// <param name="cancellationToken">Propagates notification that the operation should be cancelled.</param>
    /// <returns>A task that completes once the modification is staged.</returns>
    Task UpdateTabModuleAsync(TabModule tabModule, CancellationToken cancellationToken = default);

    /// <summary>Stages the removal of one module's placement from one page.</summary>
    /// <param name="tabId">The page's identifier.</param>
    /// <param name="moduleId">The module's identifier.</param>
    /// <param name="cancellationToken">Propagates notification that the operation should be cancelled.</param>
    /// <returns>A task that completes once the removal is staged.</returns>
    Task DeleteTabModuleAsync(int tabId, int moduleId, CancellationToken cancellationToken = default);

    /// <summary>Returns every module-scoped setting of one module.</summary>
    /// <param name="moduleId">The owning module's identifier.</param>
    /// <param name="cancellationToken">Propagates notification that the operation should be cancelled.</param>
    /// <returns>
    /// The module's <see cref="ModuleSetting"/> rows in a stable order, or an empty list when it has none.
    /// </returns>
    Task<IReadOnlyList<ModuleSetting>> GetModuleSettingsAsync(int moduleId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns one module-scoped setting by its composite key, or <see langword="null"/> when the module
    /// has no setting under that name.
    /// </summary>
    /// <param name="moduleId">The owning module's identifier.</param>
    /// <param name="settingName">The setting's name, the second half of the composite key.</param>
    /// <param name="cancellationToken">Propagates notification that the operation should be cancelled.</param>
    /// <returns>The matching <see cref="ModuleSetting"/>, or <see langword="null"/> when absent.</returns>
    Task<ModuleSetting?> GetModuleSettingAsync(int moduleId, string settingName, CancellationToken cancellationToken = default);

    /// <summary>Stages a new module-scoped setting for insertion.</summary>
    /// <param name="moduleSetting">The setting to insert.</param>
    /// <param name="cancellationToken">Propagates notification that the operation should be cancelled.</param>
    /// <returns>A task that completes once the insertion is staged.</returns>
    Task AddModuleSettingAsync(ModuleSetting moduleSetting, CancellationToken cancellationToken = default);

    /// <summary>Stages a change to an existing module-scoped setting's value.</summary>
    /// <param name="moduleSetting">
    /// The setting whose <see cref="ModuleSetting.SettingValue"/> should be persisted.
    /// </param>
    /// <param name="cancellationToken">Propagates notification that the operation should be cancelled.</param>
    /// <returns>A task that completes once the modification is staged.</returns>
    Task UpdateModuleSettingAsync(ModuleSetting moduleSetting, CancellationToken cancellationToken = default);

    /// <summary>Stages the removal of one module-scoped setting.</summary>
    /// <param name="moduleId">The owning module's identifier.</param>
    /// <param name="settingName">The setting's name.</param>
    /// <param name="cancellationToken">Propagates notification that the operation should be cancelled.</param>
    /// <returns>A task that completes once the removal is staged.</returns>
    Task DeleteModuleSettingAsync(int moduleId, string settingName, CancellationToken cancellationToken = default);

    /// <summary>Stages the removal of every module-scoped setting of one module.</summary>
    /// <param name="moduleId">The owning module's identifier.</param>
    /// <param name="cancellationToken">Propagates notification that the operation should be cancelled.</param>
    /// <returns>A task that completes once the removals are staged.</returns>
    Task DeleteModuleSettingsAsync(int moduleId, CancellationToken cancellationToken = default);

    /// <summary>Returns every placement-scoped setting of one placement.</summary>
    /// <param name="tabModuleId">The owning <see cref="TabModule"/>'s identifier.</param>
    /// <param name="cancellationToken">Propagates notification that the operation should be cancelled.</param>
    /// <returns>
    /// The placement's <see cref="TabModuleSetting"/> rows in a stable order, or an empty list when it has
    /// none.
    /// </returns>
    Task<IReadOnlyList<TabModuleSetting>> GetTabModuleSettingsAsync(int tabModuleId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns one placement-scoped setting by its composite key, or <see langword="null"/> when the
    /// placement has no setting under that name.
    /// </summary>
    /// <param name="tabModuleId">The owning placement's identifier.</param>
    /// <param name="settingName">The setting's name, the second half of the composite key.</param>
    /// <param name="cancellationToken">Propagates notification that the operation should be cancelled.</param>
    /// <returns>The matching <see cref="TabModuleSetting"/>, or <see langword="null"/> when absent.</returns>
    /// <remarks>
    /// Replaces <c>GetTabModuleSetting(ByVal TabModuleId As Integer, ByVal SettingName As String) As
    /// IDataReader</c>. The composite key differs from the module-scoped one in its first component: this
    /// is <c>(TabModuleId, SettingName)</c> where that is <c>(ModuleId, SettingName)</c>.
    /// </remarks>
    Task<TabModuleSetting?> GetTabModuleSettingAsync(int tabModuleId, string settingName, CancellationToken cancellationToken = default);

    /// <summary>Stages a new placement-scoped setting for insertion.</summary>
    /// <param name="tabModuleSetting">The setting to insert.</param>
    /// <param name="cancellationToken">Propagates notification that the operation should be cancelled.</param>
    /// <returns>A task that completes once the insertion is staged.</returns>
    Task AddTabModuleSettingAsync(TabModuleSetting tabModuleSetting, CancellationToken cancellationToken = default);

    /// <summary>Stages a change to an existing placement-scoped setting's value.</summary>
    /// <param name="tabModuleSetting">
    /// The setting whose <see cref="TabModuleSetting.SettingValue"/> should be persisted.
    /// </param>
    /// <param name="cancellationToken">Propagates notification that the operation should be cancelled.</param>
    /// <returns>A task that completes once the modification is staged.</returns>
    Task UpdateTabModuleSettingAsync(TabModuleSetting tabModuleSetting, CancellationToken cancellationToken = default);

    /// <summary>Stages the removal of one placement-scoped setting.</summary>
    /// <param name="tabModuleId">The owning placement's identifier.</param>
    /// <param name="settingName">The setting's name.</param>
    /// <param name="cancellationToken">Propagates notification that the operation should be cancelled.</param>
    /// <returns>A task that completes once the removal is staged.</returns>
    Task DeleteTabModuleSettingAsync(int tabModuleId, string settingName, CancellationToken cancellationToken = default);

    /// <summary>Stages the removal of every placement-scoped setting of one placement.</summary>
    /// <param name="tabModuleId">The owning placement's identifier.</param>
    /// <param name="cancellationToken">Propagates notification that the operation should be cancelled.</param>
    /// <returns>A task that completes once the removals are staged.</returns>
    Task DeleteTabModuleSettingsAsync(int tabModuleId, CancellationToken cancellationToken = default);
}
