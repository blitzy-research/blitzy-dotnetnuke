using DnnMigration.Domain.Entities;

namespace DnnMigration.Domain.Abstractions.Repositories;

// =================================================================================================
// Migration record for the Module aggregate's persistence contract.
//
// Source of truth is Library/Components/Providers/Data/DataProvider.vb lines 125-154: the twenty-six
// member "' module" block of a 397-line abstract class that declared 269 MustOverride members in
// total. That class resolved its own implementation through a reflection-created static singleton at
// lines 29-50, keyed by a provider kind of "data", a provider namespace of "DotNetNuke.Data", an empty
// assembly name and a Shared Shadows accessor. None of that survives: the implementation arrives by
// constructor injection, and only the in-scope subset of the 269 members is realised, decomposed by
// aggregate boundary so that no god interface is recreated.
//
// Every deliberate divergence from measured legacy behaviour is recorded below and in
// MIGRATION_NOTES.md at the repository root. None is absorbed silently.
// =================================================================================================
//
// MIGRATION: legacy GetModule (DataProvider.vb:L129) took both ModuleId and TabId because ModuleInfo.vb (L36-L936, 58 properties) was a flattened Modules-TabModules-ModuleDefinitions-ModuleControls join. The target splits that join into four entities, so the module row is read by GetByIdAsync(moduleId) and its placement on a page by GetTabModuleAsync(tabId, moduleId); together they reconstruct the legacy joined row.
//
// MIGRATION: GetSearchModules (DataProvider.vb:L131) is omitted - the search subsystem is out of scope in its entirety and the ISearchable contract is read for domain understanding only.
//
// MIGRATION: legacy AddTabModule (14 positional arguments, DataProvider.vb:L138) and UpdateTabModule (14, L139), plus AddModule (10, L132) and UpdateModule (9, L133), are replaced by entity-oriented calls.
//
// MIGRATION: ModuleController.vb hydrated rows with hand-rolled Convert.ToInt32(Null.SetNull(dr("Column"), currentValue)) blocks (L54, L66-L72), one line per column. The EF Core materializer replaces them; Null.vb is honoured as mapping knowledge only and produces no target file.
//
// MIGRATION: Framework.Reflection.CreateObject(objModule.BusinessControllerClass) at ModuleController.vb:L231 and L431 becomes an injected IModuleBusinessControllerFactory in Infrastructure resolving from a closed, DI-registered set. No activation, reflection or assembly probing appears on this contract.
//
// MIGRATION: the legacy Hashtable settings idiom becomes two genuine key/value entities, retained here rather than split into separate repositories - ModuleSettings is altered 13 times and TabModuleSettings 8 times across the 88-script DDL chain, proving both are real tables. Note the different keys: ModuleSetting is (ModuleId, SettingName); TabModuleSetting is (TabModuleId, SettingName).
//
// MIGRATION: the multi-table orchestration in ModuleController.vb belongs to the Application layer and is deliberately absent here - CopyModule (L700, L743), CopyTabModuleSettings (L764), DeleteAllModules (L795), MoveModule (L1078) and SynchronizeModule (L614) each span several tables, while DeserializeModule (L493) and SerializeModule (L539) take XmlNode and XmlDocument and belong to portal-template import. This contract stages single-aggregate reads and writes only.
//
// MIGRATION: no caching reaches this contract even though ModuleController.vb is the densest cache site in the codebase - 21 DataCache calls, an ignoreCache parameter at L885, and an expiry computed as a per-entity timeout multiplied by Common.Globals.PerformanceSetting at L998, L1052, L1264 and L1355. Caching becomes IMemoryCache behind ICacheService with the multiplier bound as the Caching:PerformanceMultiplier configuration key, so no member here accepts ignoreCache, clearCache or refreshCache.
//
// MIGRATION: permission concerns are absent by design - GetModulePermissions at ModuleController.vb:L239 was a string-join display helper, the ModulePermission entity belongs to IPermissionRepository, and evaluation belongs to Infrastructure/Security/PermissionEvaluator.cs. Likewise DesktopModule, PortalDesktopModule, ModuleDefinition and ModuleControl (DataProvider.vb:L156-L183) belong to IModuleDefinitionRepository; Module.ModuleDefinitionId is a mapped int column and nothing more.
//
// MIGRATION: two reads exist here that the provider block does not contain, both a direct consequence of splitting the flattened join described above. GetTabModuleByIdAsync addresses a placement by its own surrogate key, which the mandated TabModuleSetting block (DataProvider.vb:L149-L154) already treats as a first-class key. GetTabModulesByModuleIdAsync answers "every page this module is placed on", a question the legacy code answered implicitly from one joined row per page and which ITabRepository.GetTabModulesAsync cannot serve because it resolves the inverse direction.
//
// =================================================================================================

/// <summary>
/// Reads and writes the <b>Module</b> aggregate - the pluggable content component with a lifecycle -
/// together with its placements on pages and both of its settings collections.
/// </summary>
/// <remarks>
/// <para>
/// This single contract deliberately covers four entities: <see cref="Module"/> itself,
/// <see cref="TabModule"/> (a module's placement on one page), <see cref="ModuleSetting"/> and
/// <see cref="TabModuleSetting"/>. The settings collections are <em>not</em> split into separate
/// repositories. They are key/value rows of the same aggregate, they are only ever reached through a
/// module or one of its placements, and separating them would fragment a single consistency boundary
/// across three contracts for no gain.
/// </para>
/// <para>
/// <b>Ownership boundary.</b> All placement mutation lives here, but the two page-centric reads -
/// "which modules are placed on this page" - belong to <c>ITabRepository</c> and are not duplicated
/// here. The definition metadata behind a module (<c>ModuleDefinition</c> and friends) belongs to
/// <c>IModuleDefinitionRepository</c>. A caller that needs a module's definition name joins the two
/// contracts at the Application layer rather than expecting one repository to serve both.
/// </para>
/// <para>
/// <b>Write semantics.</b> Every mutating member <em>stages</em> its change; nothing here writes to
/// the database. The caller commits through <see cref="IUnitOfWork.SaveChangesAsync"/>, and only
/// after that commit do generated keys such as <see cref="Module.ModuleId"/> and
/// <see cref="TabModule.TabModuleId"/> hold their database values. This is why no add member yields
/// an identifier: the legacy procedures returned one only because each ended with
/// <c>SCOPE_IDENTITY()</c>, and yielding one here would force this repository to flush - splitting
/// the five-table portal-creation sequence over Portals, PortalAlias, Roles, Tabs and Modules
/// (<c>PortalController.vb:L980</c>) into independently durable statements. The legacy provider
/// corroborates the shape: <c>AddTabModule</c>, <c>AddModuleSetting</c> and <c>AddTabModuleSetting</c>
/// were already declared <c>Sub</c> and never returned a key at all.
/// </para>
/// <para>
/// <b>Identifier semantics.</b> No identifier value means "absent" on this contract. <c>Modules</c>
/// and <c>Tabs</c> both seed their identity column at zero (<c>IDENTITY (0, 1)</c>, baseline DDL
/// lines 221 and 140), so zero is a real, persisted row; <c>Portals</c> seeds at minus one, so minus
/// one is a real portal as well as the value the legacy sentinel helper used for "no integer".
/// Absence is therefore reported by a <see langword="null"/> return or an empty list, never by a
/// magic number, and no member validates an identifier against a range.
/// </para>
/// <para>
/// <b>String semantics.</b> The legacy sentinel for a missing string was the <em>empty string</em>,
/// not <see langword="null"/>, so a SQL <c>NULL</c> and <c>""</c> were indistinguishable once read
/// through the legacy path and an empty setting name was legally representable. Lookup-key
/// parameters here are non-nullable, and an implementation must match an empty key literally rather
/// than quietly treating it as <see langword="null"/> or as "match anything".
/// </para>
/// <para>
/// <b>Boundary purity.</b> Members return materialised entities and read-only collections. No
/// deferred query, reader, connection, transaction, command or provider-specific value crosses this
/// boundary, and no member exposes an out-parameter or a by-reference parameter: expected outcomes are
/// carried by the return value. Paging is absent because the legacy block has none; a caller needing one
/// composes one at the Application layer, where the paging request contract lives.
/// </para>
/// </remarks>
public interface IModuleRepository
{
    // ---------------------------------------------------------------------------------------------
    // Module - DataProvider.vb L126-L136
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Returns every module in the installation, across all tenants.
    /// </summary>
    /// <param name="cancellationToken">Propagates notification that the operation should be cancelled.</param>
    /// <returns>
    /// Every <see cref="Module"/> row, including tenant-less host modules and modules already flagged
    /// deleted. The list is empty when the table is empty; it is never <see langword="null"/>.
    /// </returns>
    /// <remarks>
    /// Replaces <c>GetAllModules() As IDataReader</c> (DataProvider.vb:L126). This is an
    /// installation-wide read with no tenant predicate, so callers serving a single tenant should
    /// prefer <see cref="GetByPortalIdAsync"/>; this member exists for upgrade and maintenance paths
    /// that legitimately span tenants.
    /// </remarks>
    Task<IReadOnlyList<Module>> GetAllAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns every module belonging to one tenant.
    /// </summary>
    /// <param name="portalId">
    /// The owning portal's identifier. Minus one and zero are both legitimate values: the portal
    /// identity column seeds at minus one and the shipped default portal is zero.
    /// </param>
    /// <param name="cancellationToken">Propagates notification that the operation should be cancelled.</param>
    /// <returns>
    /// The tenant's <see cref="Module"/> rows, or an empty list when the tenant owns none or does not
    /// exist. Modules flagged deleted are included; filtering them is the caller's decision, matching
    /// the legacy procedure, which applied no such predicate.
    /// </returns>
    /// <remarks>
    /// Replaces <c>GetModules(ByVal PortalId As Integer) As IDataReader</c> (DataProvider.vb:L127).
    /// Existence of the tenant is not asserted here - an unknown tenant is indistinguishable from an
    /// empty one, and reporting the difference is an Application-layer concern that owns the portal
    /// contract.
    /// </remarks>
    Task<IReadOnlyList<Module>> GetByPortalIdAsync(int portalId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns a tenant's modules restricted by their all-pages flag.
    /// </summary>
    /// <param name="portalId">The owning portal's identifier.</param>
    /// <param name="allTabs">
    /// When <see langword="true"/>, restricts the result to modules whose
    /// <see cref="Module.AllTabs"/> flag is set - those that appear on every page of the tenant.
    /// When <see langword="false"/>, restricts it to modules placed on specific pages only.
    /// </param>
    /// <param name="cancellationToken">Propagates notification that the operation should be cancelled.</param>
    /// <returns>The matching <see cref="Module"/> rows, or an empty list when none match.</returns>
    /// <remarks>
    /// Replaces <c>GetAllTabsModules(ByVal PortalId As Integer, ByVal AllTabs As Boolean) As IDataReader</c>
    /// (DataProvider.vb:L128). The flag is a filter, not a mode switch: both values are meaningful
    /// queries, which is why it is a plain parameter rather than two members.
    /// </remarks>
    Task<IReadOnlyList<Module>> GetAllTabsModulesAsync(int portalId, bool allTabs, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns one module by its identifier, or <see langword="null"/> when no such module exists.
    /// </summary>
    /// <param name="moduleId">
    /// The module's identifier. Zero is a legitimate key - the <c>Modules</c> identity column seeds at
    /// zero - so it must not be treated as "unspecified".
    /// </param>
    /// <param name="cancellationToken">Propagates notification that the operation should be cancelled.</param>
    /// <returns>
    /// The matching <see cref="Module"/>, or <see langword="null"/> when the identifier resolves to no
    /// row. Absence is a normal result and not an error.
    /// </returns>
    /// <remarks>
    /// This is the first half of the split of <c>GetModule(ByVal ModuleId As Integer, ByVal TabId As Integer)</c>
    /// (DataProvider.vb:L129). The legacy member needed both keys because it returned one row of a
    /// four-table join and the page key selected which placement that row carried. Here the module is
    /// a single entity, so its own key is sufficient; the placement is read by
    /// <see cref="GetTabModuleAsync"/>.
    /// </remarks>
    Task<Module?> GetByIdAsync(int moduleId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the placement of one module on one page, or <see langword="null"/> when the module is
    /// not placed on that page.
    /// </summary>
    /// <param name="tabId">
    /// The page's identifier. Zero is a legitimate key - the <c>Tabs</c> identity column also seeds at
    /// zero.
    /// </param>
    /// <param name="moduleId">The module's identifier.</param>
    /// <param name="cancellationToken">Propagates notification that the operation should be cancelled.</param>
    /// <returns>
    /// The single matching <see cref="TabModule"/>, or <see langword="null"/> when the pairing does not
    /// exist. A page and a module identify at most one placement, so a collection is not required.
    /// </returns>
    /// <remarks>
    /// This is the second half of the split of <c>GetModule(ModuleId, TabId)</c> (DataProvider.vb:L129).
    /// Read together with <see cref="GetByIdAsync"/> it reconstructs exactly the joined row the legacy
    /// member returned, while keeping the two facts - what the module is, and where it sits - in the
    /// entities that actually own them.
    /// </remarks>
    Task<TabModule?> GetTabModuleAsync(int tabId, int moduleId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the first module in a tenant whose definition carries the supplied friendly name, or
    /// <see langword="null"/> when the tenant has no instance of that definition.
    /// </summary>
    /// <param name="portalId">The owning portal's identifier.</param>
    /// <param name="friendlyName">
    /// The definition's friendly name. Non-nullable: an empty name is matched literally, because the
    /// legacy sentinel for a missing string was the empty string and an implementation must not widen
    /// it into "match anything".
    /// </param>
    /// <param name="cancellationToken">Propagates notification that the operation should be cancelled.</param>
    /// <returns>
    /// A matching <see cref="Module"/>, or <see langword="null"/> when none matches.
    /// </returns>
    /// <remarks>
    /// Replaces <c>GetModuleByDefinition(ByVal PortalId As Integer, ByVal FriendlyName As String) As IDataReader</c>
    /// (DataProvider.vb:L130). This is the lookup by which the administrative screens locate a
    /// well-known module instance - the site-settings host, for example - without hard-coding a key.
    /// The name identifies the definition, which is owned by <c>IModuleDefinitionRepository</c>; this
    /// member resolves through it to the module instance rather than exposing definition metadata.
    /// </remarks>
    Task<Module?> GetByDefinitionAsync(int portalId, string friendlyName, CancellationToken cancellationToken = default);

    /// <summary>
    /// Stages a new module for insertion.
    /// </summary>
    /// <param name="module">
    /// The module to insert. Its <see cref="Module.ModuleId"/> is assigned by the database and is only
    /// meaningful after the unit of work commits.
    /// </param>
    /// <param name="cancellationToken">Propagates notification that the operation should be cancelled.</param>
    /// <returns>
    /// A task that completes once the insertion is staged. It yields no identifier by design - see the
    /// write semantics on <see cref="IModuleRepository"/>.
    /// </returns>
    /// <remarks>
    /// Replaces the ten-argument <c>AddModule(PortalID, ModuleDefID, ModuleTitle, AllTabs, Header,
    /// Footer, StartDate, EndDate, InheritViewPermissions, IsDeleted) As Integer</c>
    /// (DataProvider.vb:L132). Every one of those arguments is a property of <see cref="Module"/>, so
    /// the positional list collapses into the entity and can no longer be transposed at a call site.
    /// The legacy <c>Integer</c> return is deliberately dropped rather than reproduced.
    /// </remarks>
    Task AddAsync(Module module, CancellationToken cancellationToken = default);

    /// <summary>
    /// Stages an existing module's modifications.
    /// </summary>
    /// <param name="module">The module whose current state should be persisted.</param>
    /// <param name="cancellationToken">Propagates notification that the operation should be cancelled.</param>
    /// <returns>A task that completes once the modification is staged.</returns>
    /// <remarks>
    /// Replaces the nine-argument <c>UpdateModule(ModuleId, ModuleTitle, AllTabs, Header, Footer,
    /// StartDate, EndDate, InheritViewPermissions, IsDeleted)</c> (DataProvider.vb:L133). Sending a
    /// module to the recycle bin is a change to <see cref="Module.IsDeleted"/> staged through this
    /// member, not a call to <see cref="DeleteAsync"/>: the legacy screens preserved the row so it
    /// could be restored, and that behaviour is preserved.
    /// </remarks>
    Task UpdateAsync(Module module, CancellationToken cancellationToken = default);

    /// <summary>
    /// Stages the permanent removal of one module.
    /// </summary>
    /// <param name="moduleId">The identifier of the module to remove.</param>
    /// <param name="cancellationToken">Propagates notification that the operation should be cancelled.</param>
    /// <returns>
    /// A task that completes once the removal is staged. An identifier that resolves to no row is not
    /// an error: a caller that has already established absence need not distinguish the two cases.
    /// </returns>
    /// <remarks>
    /// Replaces <c>DeleteModule(ByVal ModuleId As Integer)</c> (DataProvider.vb:L134). This is the
    /// hard delete that empties the recycle bin, distinct from the soft delete described on
    /// <see cref="UpdateAsync"/>. Cascading to placements and settings is the schema's responsibility
    /// through the existing foreign keys, which this refactor does not alter.
    /// </remarks>
    Task DeleteAsync(int moduleId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the placements within one pane of one page, in the order they are rendered.
    /// </summary>
    /// <param name="tabId">The page's identifier.</param>
    /// <param name="paneName">
    /// The pane's name as the page's skin declares it. Non-nullable: the legacy store represented a
    /// missing pane name as the empty string, so an empty value is matched literally.
    /// </param>
    /// <param name="cancellationToken">Propagates notification that the operation should be cancelled.</param>
    /// <returns>
    /// The pane's <see cref="TabModule"/> rows ordered by <see cref="TabModule.ModuleOrder"/>, or an
    /// empty list when the pane holds nothing.
    /// </returns>
    /// <remarks>
    /// Replaces <c>GetTabModuleOrder(ByVal TabId As Integer, ByVal PaneName As String) As IDataReader</c>
    /// (DataProvider.vb:L135). Note that this is a <em>pane-scoped ordering</em> read and is a
    /// different member from the page-scoped reads owned by <c>ITabRepository</c>; it exists so a
    /// renumbering pass can see exactly the rows whose order it is about to rewrite.
    /// </remarks>
    Task<IReadOnlyList<TabModule>> GetTabModuleOrderAsync(int tabId, string paneName, CancellationToken cancellationToken = default);

    /// <summary>
    /// Stages a change to one placement's position within a pane.
    /// </summary>
    /// <param name="tabModule">
    /// The placement carrying the intended <see cref="TabModule.ModuleOrder"/> and
    /// <see cref="TabModule.PaneName"/>.
    /// </param>
    /// <param name="cancellationToken">Propagates notification that the operation should be cancelled.</param>
    /// <returns>A task that completes once the change is staged.</returns>
    /// <remarks>
    /// Replaces <c>UpdateModuleOrder(ByVal TabId As Integer, ByVal ModuleId As Integer, ByVal ModuleOrder As Integer, ByVal PaneName As String)</c>
    /// (DataProvider.vb:L136). All four legacy arguments are properties of <see cref="TabModule"/>, so
    /// the entity carries them and the positional list disappears. The member is kept separate from
    /// <see cref="UpdateTabModuleAsync"/> because the legacy procedure wrote only the ordering
    /// columns; preserving that narrowness stops a renumbering pass from carrying a placement's
    /// unrelated edits along with it.
    /// </remarks>
    Task UpdateTabModuleOrderAsync(TabModule tabModule, CancellationToken cancellationToken = default);

    // ---------------------------------------------------------------------------------------------
    // TabModule - DataProvider.vb L138-L140, plus the two reads the join split makes necessary
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Returns one placement by its own identifier, or <see langword="null"/> when no such placement
    /// exists.
    /// </summary>
    /// <param name="tabModuleId">
    /// The placement's surrogate identifier. This is the same key the placement-scoped settings hang
    /// off, so it is a first-class addressable value rather than an internal detail.
    /// </param>
    /// <param name="cancellationToken">Propagates notification that the operation should be cancelled.</param>
    /// <returns>
    /// The matching <see cref="TabModule"/>, or <see langword="null"/> when the identifier resolves to
    /// no row.
    /// </returns>
    /// <remarks>
    /// No provider member corresponds to this read, because the legacy flattened join always arrived at
    /// a placement through its module and page rather than through its own key. Splitting that join
    /// makes the surrogate key addressable, and the mandated placement-settings block
    /// (DataProvider.vb:L149-L154) is keyed by it - so a caller holding only a settings row must be
    /// able to resolve the placement that owns it. This is the canonical by-identifier read shape and
    /// carries no behavioural divergence of its own.
    /// </remarks>
    Task<TabModule?> GetTabModuleByIdAsync(int tabModuleId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns every placement of one module - that is, every page the module appears on.
    /// </summary>
    /// <param name="moduleId">The module's identifier.</param>
    /// <param name="cancellationToken">Propagates notification that the operation should be cancelled.</param>
    /// <returns>
    /// The module's <see cref="TabModule"/> rows in a stable order, or an empty list when the module is
    /// not placed anywhere. An unplaced module and an unknown one both yield an empty list; a caller
    /// that must tell them apart reads the module first through <see cref="GetByIdAsync"/>.
    /// </returns>
    /// <remarks>
    /// No provider member corresponds to this read either, and its absence from the legacy provider is
    /// itself evidence of the flattened join: legacy callers obtained one joined row per page and
    /// therefore never needed to ask the question separately. Once the join is decomposed the question
    /// becomes explicit, and it is genuinely this contract's to answer - the page-centric reads owned
    /// by <c>ITabRepository</c> resolve the inverse direction ("which modules sit on this page") and
    /// cannot substitute. It is required wherever a change must be judged page-wide or portal-wide, and
    /// wherever an inherited view permission has to be resolved from a module's placements.
    /// </remarks>
    Task<IReadOnlyList<TabModule>> GetTabModulesByModuleIdAsync(int moduleId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns every placement of each of many modules in one read.
    /// </summary>
    /// <param name="moduleIds">
    /// The modules whose placements are wanted. Every value is a real identity - <c>ModuleID</c> is
    /// <c>IDENTITY(0, 1)</c>, so 0 addresses a row and no value is treated as "unspecified" - and an
    /// empty request asks for nothing rather than for everything.
    /// </param>
    /// <param name="cancellationToken">Propagates notification that the operation should be cancelled.</param>
    /// <returns>
    /// The placements of every named module in a stable order, flat rather than grouped, so a caller
    /// groups by <see cref="TabModule.ModuleId"/> itself. A module that is placed nowhere, and one that
    /// does not exist, both contribute no row; an empty request yields an empty list without a round
    /// trip.
    /// </returns>
    /// <remarks>
    /// <para>
    /// The set-based form of <see cref="GetTabModulesByModuleIdAsync"/>, and it exists for one reason:
    /// a listing that projects one row per placement needs the placements of every module it is about
    /// to consider, and asking for them one module at a time makes the read cost proportional to the
    /// number of modules. This member answers the same question for many modules in a single statement.
    /// It does not replace the single-module read, which remains the right shape for the decision paths
    /// that hold exactly one module.
    /// </para>
    /// <para>
    /// No paging member is implied or invented by this: the legacy module block of the data provider
    /// carries none, and the page window over a projected collection belongs to the layer that owns the
    /// paging request. This is a set-shaped READ, nothing more.
    /// </para>
    /// </remarks>
    Task<IReadOnlyList<TabModule>> GetTabModulesByModuleIdsAsync(
        IReadOnlyCollection<int> moduleIds,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Stages a new placement of a module on a page.
    /// </summary>
    /// <param name="tabModule">
    /// The placement to insert. Its <see cref="TabModule.TabModuleId"/> is assigned by the database and
    /// is only meaningful after the unit of work commits.
    /// </param>
    /// <param name="cancellationToken">Propagates notification that the operation should be cancelled.</param>
    /// <returns>A task that completes once the insertion is staged, yielding no identifier by design.</returns>
    /// <remarks>
    /// Replaces the fourteen-argument <c>AddTabModule(TabId, ModuleId, ModuleOrder, PaneName, CacheTime,
    /// Alignment, Color, Border, IconFile, Visibility, ContainerSrc, DisplayTitle, DisplayPrint,
    /// DisplaySyndicate)</c> (DataProvider.vb:L138). Fourteen positional arguments, four of them
    /// consecutive strings, is precisely the transposition hazard the entity removes. Note that the
    /// legacy <c>Visibility</c> argument was a bare <c>Integer</c>; the entity models it as the
    /// <see cref="TabModule.Visibility"/> enumeration, and no integer overload is offered here to
    /// mirror the provider.
    /// </remarks>
    Task AddTabModuleAsync(TabModule tabModule, CancellationToken cancellationToken = default);

    /// <summary>
    /// Stages an existing placement's modifications.
    /// </summary>
    /// <param name="tabModule">The placement whose current state should be persisted.</param>
    /// <param name="cancellationToken">Propagates notification that the operation should be cancelled.</param>
    /// <returns>A task that completes once the modification is staged.</returns>
    /// <remarks>
    /// Replaces the fourteen-argument <c>UpdateTabModule(...)</c> (DataProvider.vb:L139), whose
    /// argument list was identical to <c>AddTabModule</c>'s. Where a caller intends only to reposition
    /// the placement it should use <see cref="UpdateTabModuleOrderAsync"/>, which preserves the
    /// narrower write the legacy ordering procedure performed.
    /// </remarks>
    Task UpdateTabModuleAsync(TabModule tabModule, CancellationToken cancellationToken = default);

    /// <summary>
    /// Stages the removal of one module's placement from one page.
    /// </summary>
    /// <param name="tabId">The page's identifier.</param>
    /// <param name="moduleId">The module's identifier.</param>
    /// <param name="cancellationToken">Propagates notification that the operation should be cancelled.</param>
    /// <returns>
    /// A task that completes once the removal is staged. A pairing that resolves to no row is not an
    /// error.
    /// </returns>
    /// <remarks>
    /// Replaces <c>DeleteTabModule(ByVal TabId As Integer, ByVal ModuleId As Integer)</c>
    /// (DataProvider.vb:L140). Removing the last placement of a module does not remove the module: the
    /// two lifecycles are separate in the schema and remain separate here, so a caller that intends to
    /// retire the module as well follows this with <see cref="UpdateAsync"/> or
    /// <see cref="DeleteAsync"/>.
    /// </remarks>
    Task DeleteTabModuleAsync(int tabId, int moduleId, CancellationToken cancellationToken = default);

    // ---------------------------------------------------------------------------------------------
    // ModuleSetting - DataProvider.vb L142-L147 - composite key (ModuleId, SettingName)
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Returns every module-scoped setting of one module.
    /// </summary>
    /// <param name="moduleId">The owning module's identifier.</param>
    /// <param name="cancellationToken">Propagates notification that the operation should be cancelled.</param>
    /// <returns>
    /// The module's <see cref="ModuleSetting"/> rows in a stable order, or an empty list when it has
    /// none. Never <see langword="null"/>.
    /// </returns>
    /// <remarks>
    /// Replaces <c>GetModuleSettings(ByVal ModuleId As Integer) As IDataReader</c>
    /// (DataProvider.vb:L142), which the legacy code drained into an untyped name/value map. Entities
    /// are returned rather than a string-to-string dictionary because the rows are a real table and
    /// each carries its own key, so a caller can stage a change to one without rebuilding the whole
    /// collection.
    /// </remarks>
    Task<IReadOnlyList<ModuleSetting>> GetModuleSettingsAsync(int moduleId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns one module-scoped setting by its composite key, or <see langword="null"/> when the
    /// module has no setting under that name.
    /// </summary>
    /// <param name="moduleId">The owning module's identifier.</param>
    /// <param name="settingName">
    /// The setting's name, the second half of the composite key. Non-nullable, and an empty name is
    /// matched literally: because the legacy sentinel for a missing string was the empty string rather
    /// than <see langword="null"/>, an empty name is a value the legacy store could genuinely hold, and
    /// an implementation must neither reject it nor coerce it.
    /// </param>
    /// <param name="cancellationToken">Propagates notification that the operation should be cancelled.</param>
    /// <returns>The matching <see cref="ModuleSetting"/>, or <see langword="null"/> when absent.</returns>
    /// <remarks>
    /// Replaces <c>GetModuleSetting(ByVal ModuleId As Integer, ByVal SettingName As String) As IDataReader</c>
    /// (DataProvider.vb:L143). The key here is the <em>module</em> identifier - contrast
    /// <see cref="GetTabModuleSettingAsync"/>, which is keyed by the <em>placement</em> identifier. The
    /// two collections are not interchangeable and must never be conflated.
    /// </remarks>
    Task<ModuleSetting?> GetModuleSettingAsync(int moduleId, string settingName, CancellationToken cancellationToken = default);

    /// <summary>
    /// Stages a new module-scoped setting for insertion.
    /// </summary>
    /// <param name="moduleSetting">
    /// The setting to insert. Both halves of its composite key -
    /// <see cref="ModuleSetting.ModuleId"/> and <see cref="ModuleSetting.SettingName"/> - must already
    /// be populated, because neither is database-generated.
    /// </param>
    /// <param name="cancellationToken">Propagates notification that the operation should be cancelled.</param>
    /// <returns>A task that completes once the insertion is staged.</returns>
    /// <remarks>
    /// Replaces <c>AddModuleSetting(ByVal ModuleId As Integer, ByVal SettingName As String, ByVal SettingValue As String)</c>
    /// (DataProvider.vb:L144), which was already declared <c>Sub</c> and returned nothing - the clearest
    /// corroboration that no add member on this contract owes the caller an identifier.
    /// </remarks>
    Task AddModuleSettingAsync(ModuleSetting moduleSetting, CancellationToken cancellationToken = default);

    /// <summary>
    /// Stages a change to an existing module-scoped setting's value.
    /// </summary>
    /// <param name="moduleSetting">
    /// The setting whose <see cref="ModuleSetting.SettingValue"/> should be persisted. Its composite key
    /// identifies the row and is not itself updatable - renaming a setting is a removal followed by an
    /// insertion.
    /// </param>
    /// <param name="cancellationToken">Propagates notification that the operation should be cancelled.</param>
    /// <returns>A task that completes once the modification is staged.</returns>
    /// <remarks>
    /// Replaces <c>UpdateModuleSetting(ByVal ModuleId As Integer, ByVal SettingName As String, ByVal SettingValue As String)</c>
    /// (DataProvider.vb:L145). The legacy provider kept insertion and update as separate members even
    /// though both wrote the same three columns, and that separation is preserved so a caller states
    /// which of the two it intends rather than relying on an implicit upsert.
    /// </remarks>
    Task UpdateModuleSettingAsync(ModuleSetting moduleSetting, CancellationToken cancellationToken = default);

    /// <summary>
    /// Stages the removal of one module-scoped setting.
    /// </summary>
    /// <param name="moduleId">The owning module's identifier.</param>
    /// <param name="settingName">The setting's name. An empty name is matched literally.</param>
    /// <param name="cancellationToken">Propagates notification that the operation should be cancelled.</param>
    /// <returns>
    /// A task that completes once the removal is staged. A key that resolves to no row is not an error.
    /// </returns>
    /// <remarks>
    /// Replaces <c>DeleteModuleSetting(ByVal ModuleId As Integer, ByVal SettingName As String)</c>
    /// (DataProvider.vb:L146). The target is addressed by key rather than by entity so that a caller
    /// which knows only the name it wishes to clear need not read the row first.
    /// </remarks>
    Task DeleteModuleSettingAsync(int moduleId, string settingName, CancellationToken cancellationToken = default);

    /// <summary>
    /// Stages the removal of every module-scoped setting of one module.
    /// </summary>
    /// <param name="moduleId">The owning module's identifier.</param>
    /// <param name="cancellationToken">Propagates notification that the operation should be cancelled.</param>
    /// <returns>
    /// A task that completes once the removals are staged. It yields no affected-row count: that number
    /// is what <see cref="IUnitOfWork.SaveChangesAsync"/> returns when the unit of work commits.
    /// </returns>
    /// <remarks>
    /// Replaces <c>DeleteModuleSettings(ByVal ModuleId As Integer)</c> (DataProvider.vb:L147). A module
    /// with no settings is staged as no removals at all, which is not an error.
    /// </remarks>
    Task DeleteModuleSettingsAsync(int moduleId, CancellationToken cancellationToken = default);

    // ---------------------------------------------------------------------------------------------
    // TabModuleSetting - DataProvider.vb L149-L154 - composite key (TabModuleId, SettingName)
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Returns every placement-scoped setting of one placement.
    /// </summary>
    /// <param name="tabModuleId">
    /// The owning <see cref="TabModule"/>'s identifier. Note that this is the <em>placement</em> key,
    /// not the module key.
    /// </param>
    /// <param name="cancellationToken">Propagates notification that the operation should be cancelled.</param>
    /// <returns>
    /// The placement's <see cref="TabModuleSetting"/> rows in a stable order, or an empty list when it
    /// has none.
    /// </returns>
    /// <remarks>
    /// Replaces <c>GetTabModuleSettings(ByVal TabModuleId As Integer) As IDataReader</c>
    /// (DataProvider.vb:L149). These settings are per-placement, so the same module placed on two pages
    /// carries two independent collections - which is exactly why they are a separate table from the
    /// module-scoped settings and are modelled as a separate entity here.
    /// </remarks>
    Task<IReadOnlyList<TabModuleSetting>> GetTabModuleSettingsAsync(int tabModuleId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns one placement-scoped setting by its composite key, or <see langword="null"/> when the
    /// placement has no setting under that name.
    /// </summary>
    /// <param name="tabModuleId">The owning placement's identifier.</param>
    /// <param name="settingName">
    /// The setting's name, the second half of the composite key. Non-nullable, and an empty name is
    /// matched literally for the same reason given on <see cref="GetModuleSettingAsync"/>.
    /// </param>
    /// <param name="cancellationToken">Propagates notification that the operation should be cancelled.</param>
    /// <returns>The matching <see cref="TabModuleSetting"/>, or <see langword="null"/> when absent.</returns>
    /// <remarks>
    /// Replaces <c>GetTabModuleSetting(ByVal TabModuleId As Integer, ByVal SettingName As String) As IDataReader</c>
    /// (DataProvider.vb:L150). The composite key differs from the module-scoped one in its first
    /// component: this is <c>(TabModuleId, SettingName)</c> where that is <c>(ModuleId, SettingName)</c>.
    /// Passing a module identifier here would silently read another placement's settings, so the two
    /// key spaces must be kept strictly apart.
    /// </remarks>
    Task<TabModuleSetting?> GetTabModuleSettingAsync(int tabModuleId, string settingName, CancellationToken cancellationToken = default);

    /// <summary>
    /// Stages a new placement-scoped setting for insertion.
    /// </summary>
    /// <param name="tabModuleSetting">
    /// The setting to insert. Both halves of its composite key -
    /// <see cref="TabModuleSetting.TabModuleId"/> and <see cref="TabModuleSetting.SettingName"/> - must
    /// already be populated. When the owning placement is itself newly staged, its generated identifier
    /// is not available until the unit of work commits, so the two must be staged in an order that lets
    /// the change tracker resolve the relationship.
    /// </param>
    /// <param name="cancellationToken">Propagates notification that the operation should be cancelled.</param>
    /// <returns>A task that completes once the insertion is staged.</returns>
    /// <remarks>
    /// Replaces <c>AddTabModuleSetting(ByVal TabModuleId As Integer, ByVal SettingName As String, ByVal SettingValue As String)</c>
    /// (DataProvider.vb:L151), which was likewise already a <c>Sub</c>.
    /// </remarks>
    Task AddTabModuleSettingAsync(TabModuleSetting tabModuleSetting, CancellationToken cancellationToken = default);

    /// <summary>
    /// Stages a change to an existing placement-scoped setting's value.
    /// </summary>
    /// <param name="tabModuleSetting">
    /// The setting whose <see cref="TabModuleSetting.SettingValue"/> should be persisted.
    /// </param>
    /// <param name="cancellationToken">Propagates notification that the operation should be cancelled.</param>
    /// <returns>A task that completes once the modification is staged.</returns>
    /// <remarks>
    /// Replaces <c>UpdateTabModuleSetting(ByVal TabModuleId As Integer, ByVal SettingName As String, ByVal SettingValue As String)</c>
    /// (DataProvider.vb:L152).
    /// </remarks>
    Task UpdateTabModuleSettingAsync(TabModuleSetting tabModuleSetting, CancellationToken cancellationToken = default);

    /// <summary>
    /// Stages the removal of one placement-scoped setting.
    /// </summary>
    /// <param name="tabModuleId">The owning placement's identifier.</param>
    /// <param name="settingName">The setting's name. An empty name is matched literally.</param>
    /// <param name="cancellationToken">Propagates notification that the operation should be cancelled.</param>
    /// <returns>
    /// A task that completes once the removal is staged. A key that resolves to no row is not an error.
    /// </returns>
    /// <remarks>
    /// Replaces <c>DeleteTabModuleSetting(ByVal TabModuleId As Integer, ByVal SettingName As String)</c>
    /// (DataProvider.vb:L153).
    /// </remarks>
    Task DeleteTabModuleSettingAsync(int tabModuleId, string settingName, CancellationToken cancellationToken = default);

    /// <summary>
    /// Stages the removal of every placement-scoped setting of one placement.
    /// </summary>
    /// <param name="tabModuleId">The owning placement's identifier.</param>
    /// <param name="cancellationToken">Propagates notification that the operation should be cancelled.</param>
    /// <returns>
    /// A task that completes once the removals are staged. As with
    /// <see cref="DeleteModuleSettingsAsync"/> it yields no affected-row count, which
    /// <see cref="IUnitOfWork.SaveChangesAsync"/> returns instead.
    /// </returns>
    /// <remarks>
    /// Replaces <c>DeleteTabModuleSettings(ByVal TabModuleId As Integer)</c> (DataProvider.vb:L154).
    /// This is the member a caller uses when retiring a placement, since the settings are keyed by the
    /// placement rather than by the module and would otherwise be orphaned.
    /// </remarks>
    Task DeleteTabModuleSettingsAsync(int tabModuleId, CancellationToken cancellationToken = default);
}
