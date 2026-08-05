using DnnMigration.Domain.Abstractions.Repositories;
using DnnMigration.Domain.Entities;
using DnnMigration.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace DnnMigration.Infrastructure.Repositories;

// =================================================================================================
// Migration record for the Module aggregate's persistence implementation.
//
// This type realises the twenty-eight members of IModuleRepository over four configured entities -
// Module, TabModule, ModuleSetting and TabModuleSetting - using nothing but LINQ against
// DnnDbContext. It declares no mapping, no table name, no column name and no SQL: the terminal
// legacy schema is bound by Persistence/Configurations/{Module,TabModule,ModuleSetting,
// TabModuleSetting}Configuration.cs, and the schema itself is immutable for this migration.
//
// The legacy counterpart is the data-access half of Library/Components/Modules/ModuleController.vb
// (1,456 lines), which reached twenty-six abstract members declared in the "' module" block of
// Library/Components/Providers/Data/DataProvider.vb (L126-L154). That abstract class carried 269
// MustOverride members in total and resolved its own implementation through a reflection-created
// static singleton (DataProvider.vb:L31-L50); here the implementation is supplied by constructor
// injection and only the in-scope module subset is realised.
//
// Every deliberate divergence from measured legacy behaviour is annotated inline below with a
// `// MIGRATION:` comment and recorded in MIGRATION_NOTES.md at the repository root. None is
// absorbed silently. This file does not edit that document.
// =================================================================================================
//
// MIGRATION: the legacy ModuleInfo (Library/Components/Modules/ModuleInfo.vb, 936 lines, 58
// properties) flattened dbo.Modules, dbo.TabModules, dbo.ModuleDefinitions, dbo.ModuleControls and a
// permission collection into a single object, so one read answered "what is this module" and "where
// does it sit" together. ModuleController.vb:L66-L72 shows the join being drained into that one
// object - PortalID, TabID, TabModuleID, ModuleID, ModuleDefID, ModuleOrder and PaneName assigned in
// consecutive statements. This implementation returns separate configured entities instead:
// GetByIdAsync yields the Module row alone and GetTabModuleAsync yields the (TabId, ModuleId)
// placement, and a caller needing the legacy joined row composes the two. No member reconstructs a
// flattened reader row.
//
// MIGRATION: the Entity Framework materialiser replaces every hand-coded hydration path. Gone are
// the `Convert.ToInt32(Null.SetNull(dr("Column"), currentValue))` blocks at
// ModuleController.vb:L54 and L66-L72 (one statement per column), the reflection-based row hydrator
// in Library/Components/Shared/CBO.vb, the thirty-five ArrayList and Hashtable results in
// ModuleController.vb, and the legacy permission-string fields AuthorizedEditRoles,
// AuthorizedViewRoles and AuthorizedRoles that ModuleInfo carried after the 03.00.01 script had
// already dropped their columns. No IDataReader, no Fill or FillObject method, no CBO pattern and no
// legacy collection wrapper appears here, and Library/Components/Shared/Null.vb is honoured as
// mapping knowledge only - its sentinels (NullInteger -1 at L43, NullDate Date.MinValue at L68,
// NullString "" at L73) are never written or matched by this file.
//
// MIGRATION: no caching reaches this type, even though ModuleController.vb is the densest cache site
// in the legacy codebase - twenty-one direct `DataCache.` calls, an `ignoreCache` parameter on
// GetModule at L885, and expiries computed as a per-entity timeout multiplied by
// Common.Globals.PerformanceSetting at L998, L1052, L1264 and L1355. Caching is IMemoryCache behind
// the coordinated ICacheService with the multiplier bound as Caching:PerformanceMultiplier, so no
// member here reads a cache, writes a cache, invalidates a cache or accepts an ignoreCache,
// clearCache or refreshCache flag. A repository that cached would make its own reads unobservable to
// the unit of work that shares its context.
//
// MIGRATION: `Framework.Reflection.CreateObject(objModule.BusinessControllerClass,
// objModule.BusinessControllerClass)` at ModuleController.vb:L231 and L431 is not performed here.
// Business-controller activation belongs to the coordinated IModuleBusinessControllerFactory, which
// resolves from a closed, DI-registered set rather than probing assemblies by name. This file
// activates nothing, loads no assembly and performs no reflection of any kind.
//
// MIGRATION: the search path is intentionally absent. `GetSearchModules(ByVal PortalId As Integer)`
// (DataProvider.vb:L131) is the one member of the legacy module block with no counterpart on this
// contract, because the search subsystem is out of scope in its entirety; the ISearchable contract
// was read for domain understanding only. Its omission is deliberate, not an oversight.
//
// MIGRATION: the two page-centric reads - "which modules sit on this page" - are NOT implemented
// here. `GetTabModules(TabId)` and `GetPortalTabModules(PortalId, TabId)` (DataProvider.vb:L122 and
// L121) resolve the inverse direction and belong to ITabRepository.GetTabModulesAsync and
// ITabRepository.GetPortalTabModulesAsync. Duplicating them would give one question two owners.
//
// MIGRATION: reads return TRACKED entities; no member applies AsNoTracking. This is a measured
// correctness decision, not an omission, and it matches all nine sibling repositories in this folder
// (see the same statement on PortalRepository). The coordinated Application layer obtains an entity
// from a read member, mutates it, and commits through IUnitOfWork.SaveChangesAsync WITHOUT calling
// any Update member - there is not one `_modules.Update*` call site in the solution. The mutation
// sites are ModuleService.cs:916 (`module.IsDeleted = true`, the recycle-bin soft delete),
// ModuleService.cs:778 and :786 (ModuleMappings.ApplyUpdate over the module and its placement, then
// the resolved ModuleOrder), ModuleService.cs:1175 (`stored.SettingValue = desired`) and
// ModuleService.cs:2194 (`existing.SettingValue = settingValue`). Detaching those results makes all
// four writes silent no-ops: the API keeps reporting success while nothing persists. That was
// verified by execution rather than argued - inserting AsNoTracking at the thirteen read-only query
// roots still compiled clean under `dotnet build -c Release --warnaserror` (0 warnings, 0 errors) and
// failed exactly four integration tests: ModuleApiTests.UpdateModule_ReturnsOkAndPersists,
// ModuleApiTests.DeleteModule_ReturnsNoContentAndHidesItFromTheCollection,
// ModuleApiTests.UpdateModule_WithNegativeCachePeriod_PersistsItVerbatim and
// ModuleApiTests.ModuleSettings_RoundTripBothScopes, the last reporting a module that a successful
// DELETE had left visible. Should the Application layer ever adopt explicit Update calls at those
// four sites, this decision can be revisited; until then, detaching here would trade a preserved
// behaviour for a change-tracker micro-optimisation.
//
// =================================================================================================

/// <summary>
/// Persists the <b>Module</b> aggregate - the pluggable content component with a lifecycle -
/// together with its placements on pages and both of its settings collections.
/// </summary>
/// <remarks>
/// <para>
/// <b>Four entities, one consistency boundary.</b> A module, its placement on a page, its
/// module-scoped settings and its placement-scoped settings are four rows in four tables, and they
/// are only ever reached through a module or one of its placements. They are therefore served by one
/// repository rather than three, exactly as <see cref="IModuleRepository"/> specifies.
/// </para>
/// <para>
/// <b>Writes are staged, never committed.</b> Every mutating member records its intent with the
/// change tracker and returns; not one of them calls <c>SaveChanges</c>. The single commit point is
/// <see cref="IUnitOfWork.SaveChangesAsync"/>, which is what allows a module and its placements to be
/// written as one unit and what makes the five-table portal-creation sequence durable together
/// rather than statement by statement. It is also why no add member yields a generated key: the
/// legacy <c>AddModule</c> returned one only because the procedure ended with
/// <c>SCOPE_IDENTITY()</c>, and returning one here would force a flush.
/// </para>
/// <para>
/// <b>Bulk removals materialise before staging.</b> The two collection deletes read their rows and
/// hand them to <c>RemoveRange</c> rather than issuing <c>ExecuteDelete</c>. That is deliberate:
/// <c>ExecuteDelete</c> writes to the database immediately, outside the unit of work, so a caller
/// whose later step failed would find the settings already gone with no transaction to roll back.
/// </para>
/// <para>
/// <b>No identifier means absence.</b> <c>Modules.ModuleID</c> is <c>IDENTITY(0, 1)</c> and
/// <c>Portals.PortalID</c> is <c>IDENTITY(-1, 1)</c>, so zero and minus one are both real, persisted
/// keys. Absence is reported by a <see langword="null"/> return or an empty list, and no member
/// validates an identifier against a range or treats a particular value as "unspecified".
/// </para>
/// <para>
/// <b>Strings are matched literally.</b> The legacy sentinel for a missing string was the empty
/// string rather than <see langword="null"/>, so an empty pane name or setting name is a value the
/// store can genuinely hold. Lookup keys are compared as supplied - never trimmed, coalesced, or
/// widened into "match anything".
/// </para>
/// <para>
/// <b>Boundaries this type does not cross.</b> It takes exactly one dependency, the context, and
/// holds no cache, permission evaluator, business-controller factory, logger, clock, HTTP accessor or
/// service provider. It exposes no <see cref="IQueryable{T}"/>, <see cref="DbSet{TEntity}"/> or
/// <see cref="DbContext"/>, so no deferred query escapes; every list member materialises with
/// <c>ToListAsync</c> before returning. Permission rows and their evaluation belong to
/// <c>IPermissionRepository</c> and <c>PermissionEvaluator</c>; definition metadata belongs to
/// <c>IModuleDefinitionRepository</c>; and the multi-table orchestration that
/// <c>ModuleController.vb</c> performed - copy, move, synchronise, serialise and delete-all - belongs
/// to the Application layer.
/// </para>
/// </remarks>
internal sealed class ModuleRepository : IModuleRepository
{
    /// <summary>
    /// The unit-of-work scoped context. Held privately so that no caller can reach the model,
    /// a <see cref="DbSet{TEntity}"/> or a transaction through this repository.
    /// </summary>
    private readonly DnnDbContext _dbContext;

    /// <summary>Initialises a new instance of the <see cref="ModuleRepository"/> class.</summary>
    /// <param name="dbContext">
    /// The scoped database context shared with the unit of work. Sharing one context instance is
    /// what lets a read here and a commit there participate in the same change tracker.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="dbContext"/> is <see langword="null"/>.</exception>
    public ModuleRepository(DnnDbContext dbContext)
    {
        _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
    }

    // ---------------------------------------------------------------------------------------------
    // Module - DataProvider.vb L126-L136
    // ---------------------------------------------------------------------------------------------

    /// <inheritdoc />
    /// <remarks>
    /// Installation-wide and deliberately unfiltered: neither the tenant nor the soft-delete flag is
    /// predicated, matching <c>GetAllModules()</c>, which applied no <c>where</c> clause at all.
    /// The definition and the package behind it are loaded with each row because the mapping layer
    /// reports both - the definition's display name and the package's name, description and version -
    /// and ordering by the primary key gives every caller the same sequence for the same data.
    /// </remarks>
    // MIGRATION: the PACKAGE is loaded as well as the definition, on this and every other read below.
    // Loading only the definition made the module projections report a null package name, description
    // and version on every response, because those three values live one table further out on
    // dbo.DesktopModules and the navigation that reaches them was never populated. Two joins onto
    // already-joined tables is the price of a projection that cannot silently report an absence it
    // caused itself.
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
    /// The tenant predicate is exact equality against the nullable <c>PortalID</c> column. It is not
    /// <c>&gt; 0</c>, not a default-identifier test and not a minus-one wildcard: <c>Portals.PortalID</c>
    /// is <c>IDENTITY(-1, 1)</c>, so minus one and zero are both real tenants, and a module owned by the
    /// installation rather than by a tenant is represented by a null column - a state the entity models
    /// directly and no sentinel is needed to express. An unknown tenant and an empty one both yield an
    /// empty list; telling them apart is the portal contract's job, not this one's.
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
    /// <remarks>
    /// Both values of the flag are meaningful queries rather than a mode switch, so the flag is
    /// compared rather than branched on: <c>AllTabs == allTabs</c> selects the modules that appear on
    /// every page of the tenant when true, and those placed on specific pages when false. The filter is
    /// applied to the <c>Module</c> aggregate, which is where the <c>AllTabs</c> column lives; the
    /// legacy procedure reached the same rows through the flattened join, and reconstructing that join
    /// here would contradict the split this migration makes.
    /// </remarks>
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
    /// <remarks>
    /// Returns the module row and its definition, and nothing further.
    /// </remarks>
    // MIGRATION: this is the first half of the split of `GetModule(ModuleId, TabId)`
    // (DataProvider.vb:L129). The legacy member needed a page key as well because it returned one row
    // of a four-table join, and the page key chose which placement that row carried. Here the module
    // is its own entity, so its own key suffices and NO placement is loaded: pulling TabModules in
    // would re-flatten precisely the join this migration decomposed. The placement is read by
    // GetTabModuleAsync, every placement of the module by GetTabModulesByModuleIdAsync, and the two
    // consumers that want them already ask explicitly - ModuleService.cs:1765-1767 and
    // PermissionService.cs:983-987 both fall back to GetTabModulesByModuleIdAsync when the collection
    // is empty. ModuleID is IDENTITY(0, 1), so zero is a legitimate key and absence is reported as
    // null rather than by any numeric convention.
    public Task<Module?> GetByIdAsync(int moduleId, CancellationToken cancellationToken = default)
    {
        return _dbContext.Modules
            .Include(module => module.ModuleDefinition)
                .ThenInclude(definition => definition.DesktopModule)
            .FirstOrDefaultAsync(module => module.ModuleId == moduleId, cancellationToken);
    }

    /// <inheritdoc />
    /// <remarks>
    /// <c>IX_TabModules</c> is unique over <c>(TabID, ModuleID)</c> - TabID first, then ModuleID, as
    /// the 03.00.01 script declares it - so a page and a module identify at most one placement and a
    /// single row is the correct shape rather than a collection. <c>Tabs.TabID</c> also seeds at zero,
    /// so neither key argument has a reserved value.
    /// </remarks>
    // MIGRATION: this is the second half of the split of `GetModule(ModuleId, TabId)`
    // (DataProvider.vb:L129). Read together with GetByIdAsync it reconstructs exactly the joined row
    // the legacy member returned, while keeping "what the module is" and "where it sits" in the two
    // entities that own them.
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
    /// through the required <c>ModuleDefID</c> relationship - which Entity Framework renders as an
    /// inner join onto <c>dbo.ModuleDefinitions</c> - rather than duplicating the name onto the module
    /// row. Matching is exact: the name is compared as supplied, which reproduces the legacy
    /// <c>where FriendlyName = @FriendlyName</c> and leaves case sensitivity to the installation's
    /// collation exactly as the procedure did, instead of forcing a case fold that would also make the
    /// column's index unusable. An empty name is therefore matched literally rather than widened.
    /// Ordering by the primary key makes "the first matching module" a deterministic answer when a
    /// tenant holds several instances of one definition.
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
    /// Staging only. Placements and settings added to the entity's collections before this call are
    /// staged with it by the change tracker, which is how a module and its first placement become one
    /// insert pair rather than two independently durable writes.
    /// </remarks>
    // MIGRATION: replaces the ten-argument `AddModule(PortalID, ModuleDefID, ModuleTitle, AllTabs,
    // Header, Footer, StartDate, EndDate, InheritViewPermissions, IsDeleted) As Integer`
    // (DataProvider.vb:L132). Every argument is a property of Module, so the positional list collapses
    // into the entity and can no longer be transposed at a call site. The legacy Integer return came
    // from a trailing `select SCOPE_IDENTITY()` and is deliberately dropped: yielding a key here would
    // force a flush and split the five-table portal-creation sequence - Portals, PortalAlias, Roles,
    // Tabs and Modules - into separately durable statements. ModuleId is assigned when the unit of work
    // commits. Nothing is coerced on the way in: null StartDate, EndDate and InheritViewPermissions are
    // written as SQL nulls rather than as the legacy Date.MinValue and False sentinels, and no
    // configured default - DisplaySyndicate's deliberate CLR-versus-database divergence included - is
    // overridden by this repository.
    public Task AddAsync(Module module, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(module);
        cancellationToken.ThrowIfCancellationRequested();

        _dbContext.Modules.Add(module);

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    /// <remarks>
    /// An entity read through this repository is already tracked, so its modifications are staged by
    /// the change tracker and this call is the caller's explicit statement of intent. A detached
    /// instance - one built by a caller rather than read - is attached and marked modified so that the
    /// same call serves both origins. Sending a module to the recycle bin is a change to
    /// <see cref="Module.IsDeleted"/> staged through here, never a call to <see cref="DeleteAsync"/>:
    /// the legacy screens kept the row so it could be restored.
    /// </remarks>
    // MIGRATION: replaces the nine-argument `UpdateModule(ModuleId, ModuleTitle, AllTabs, Header,
    // Footer, StartDate, EndDate, InheritViewPermissions, IsDeleted)` (DataProvider.vb:L133).
    //
    // MIGRATION: the detached branch assigns the state directly instead of calling DbSet.Update, and
    // that is a correctness requirement of this schema rather than a stylistic preference.
    // DbSet.Update decides between Added and Modified by asking whether the key "is set", and it
    // reads an int key of 0 as unset. dbo.Modules.ModuleID is declared IDENTITY(0, 1)
    // (01.00.00.SqlDataProvider:L221), so 0 is the real first module of an installation - which means
    // Update(module) on a detached module 0 stages an INSERT and duplicates the row instead of
    // updating it. Assigning EntityState.Modified attaches the instance and marks every property
    // modified without consulting the key at all. This is the same -1/0 sentinel collision the
    // migration analysis records: 0 and -1 are values here, never absences.
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
    /// The hard delete that empties the recycle bin, distinct from the soft delete described on
    /// <see cref="UpdateAsync"/>. The row is located first because the contract addresses its target by
    /// identifier, as the legacy procedure did; when the caller already holds the entity this resolves
    /// from the change tracker without a round trip. Cascading to placements and settings is the
    /// schema's existing foreign-key behaviour, which this refactor does not alter.
    /// </remarks>
    // MIGRATION: replaces `DeleteModule(ByVal ModuleId As Integer)` (DataProvider.vb:L134). The removal
    // is staged rather than executed, so it commits with whatever else the unit of work carries.
    public async Task DeleteAsync(int moduleId, CancellationToken cancellationToken = default)
    {
        Module? module = await _dbContext.Modules
            .FirstOrDefaultAsync(candidate => candidate.ModuleId == moduleId, cancellationToken)
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
    /// <remarks>
    /// A <em>pane-scoped</em> ordering read, and a different question from the page-scoped reads owned
    /// by <c>ITabRepository</c>: it exists so that a renumbering pass sees exactly the rows whose order
    /// it is about to rewrite. Ordering is <see cref="TabModule.ModuleOrder"/> then
    /// <see cref="TabModule.TabModuleId"/>, so two placements that share an order - which the schema
    /// permits, since nothing makes <c>ModuleOrder</c> unique - still come back in a stable sequence
    /// instead of whatever order the engine happens to produce. The pane name is compared literally;
    /// the legacy store held a missing pane name as the empty string rather than as null, so an empty
    /// value addresses the rows recorded under it.
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
    /// the two ordering columns, and that narrowness is preserved for a detached placement by marking
    /// just <see cref="TabModule.ModuleOrder"/> and <see cref="TabModule.PaneName"/> modified - so a
    /// renumbering pass cannot carry a placement's unrelated edits along with it. A placement that is
    /// already tracked is left to the change tracker, which is the caller's own read.
    /// </remarks>
    // MIGRATION: replaces `UpdateModuleOrder(ByVal TabId As Integer, ByVal ModuleId As Integer, ByVal
    // ModuleOrder As Integer, ByVal PaneName As String)` (DataProvider.vb:L136). All four arguments are
    // properties of TabModule, so the entity carries them and the positional list disappears.
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

    // ---------------------------------------------------------------------------------------------
    // TabModule - DataProvider.vb L138-L140, plus the two reads the join split makes necessary
    // ---------------------------------------------------------------------------------------------

    /// <inheritdoc />
    /// <remarks>
    /// Addresses a placement by its own surrogate key, which is a first-class value rather than an
    /// internal detail: the placement-scoped settings hang off it, so a caller holding a settings row
    /// must be able to resolve the placement that owns it.
    /// </remarks>
    public Task<TabModule?> GetTabModuleByIdAsync(int tabModuleId, CancellationToken cancellationToken = default)
    {
        return _dbContext.TabModules
            .FirstOrDefaultAsync(placement => placement.TabModuleId == tabModuleId, cancellationToken);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Answers "every page this module appears on". Ordered by page and then by the placement key so
    /// the sequence is stable. An unplaced module and an unknown one both yield an empty list; a caller
    /// that must distinguish them reads the module first through <see cref="GetByIdAsync"/>.
    /// </remarks>
    // MIGRATION: no legacy provider member corresponds to this read, and that absence is itself
    // evidence of the flattened join - legacy callers received one joined row per page and so never
    // needed to ask the question separately. It is this contract's to answer rather than
    // ITabRepository's, because ITabRepository.GetTabModulesAsync resolves the inverse direction
    // ("which modules sit on this page") and cannot substitute.
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
    /// <para>
    /// The set-based twin of <see cref="GetTabModulesByModuleIdAsync"/>. One statement answers "every
    /// page each of these modules appears on", which is what keeps a listing that emits one row per
    /// placement from issuing one read per module. The ordering leads with the module so that the flat
    /// result can be grouped in one pass, and finishes on the placement key so the sequence is total:
    /// <c>ModuleOrder</c> is not unique within a page, so it cannot terminate an ordering on its own.
    /// </para>
    /// <para>
    /// The identifiers are snapshotted into an array before they reach the predicate, so the translated
    /// membership test is built from a stable sequence rather than from a collection the caller could
    /// still be mutating, and duplicates collapse first because a repeated identifier would lengthen the
    /// statement without widening the answer. An empty request short-circuits with no round trip, which
    /// is correctness as much as economy: the answer is knowably empty.
    /// </para>
    /// </remarks>
    // MIGRATION: no legacy provider member corresponds to this read, for the same reason its
    // single-module twin has none - legacy callers received one flattened join row per page and so never
    // asked the question separately. It is a READ ONLY: no page window, no total and no ordering
    // parameter reaches this contract, because the legacy module block declares no paging member and the
    // page window over a projection belongs to the layer that owns the paging request.
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
    /// Staging only; <see cref="TabModule.TabModuleId"/> is assigned when the unit of work commits.
    /// </remarks>
    // MIGRATION: replaces the fourteen-argument `AddTabModule(TabId, ModuleId, ModuleOrder, PaneName,
    // CacheTime, Alignment, Color, Border, IconFile, Visibility, ContainerSrc, DisplayTitle,
    // DisplayPrint, DisplaySyndicate)` (DataProvider.vb:L138) - four of those arguments consecutive
    // strings, which is exactly the transposition hazard the entity removes. The legacy Visibility
    // argument was a bare Integer; the entity models it as the ModuleVisibility enumeration and no
    // integer overload is offered. This member was already a Sub and returned no key, so nothing is
    // lost by staging. The appearance columns and the three display flags are written exactly as the
    // entity carries them: DisplaySyndicate in particular is left alone, because its CLR default and
    // its database default deliberately differ and the configuration pins the column
    // ValueGeneratedNever so that both survive.
    public Task AddTabModuleAsync(TabModule tabModule, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tabModule);
        cancellationToken.ThrowIfCancellationRequested();

        _dbContext.TabModules.Add(tabModule);

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    /// <remarks>
    /// Stages the whole placement. A caller intending only to reposition it should use
    /// <see cref="UpdateTabModuleOrderAsync"/>, which preserves the narrower write the legacy ordering
    /// procedure performed.
    /// </remarks>
    // MIGRATION: replaces the fourteen-argument `UpdateTabModule(...)` (DataProvider.vb:L139), whose
    // argument list was identical to AddTabModule's.
    public Task UpdateTabModuleAsync(TabModule tabModule, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tabModule);
        cancellationToken.ThrowIfCancellationRequested();

        if (_dbContext.Entry(tabModule).State is EntityState.Detached)
        {
            // Assigned rather than Update(...) for the reason given on UpdateAsync: the composite
            // setting keys and the module key can legitimately consist entirely of default values,
            // which Update would misread as "unsaved" and stage as an insert.
            _dbContext.Entry(tabModule).State = EntityState.Modified;
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    /// <remarks>
    /// Removing the last placement of a module does not remove the module: the two lifecycles are
    /// separate in the schema and remain separate here, so a caller that also intends to retire the
    /// module follows this with <see cref="UpdateAsync"/> or <see cref="DeleteAsync"/>. A pairing that
    /// resolves to no row is not an error.
    /// </remarks>
    // MIGRATION: replaces `DeleteTabModule(ByVal TabId As Integer, ByVal ModuleId As Integer)`
    // (DataProvider.vb:L140).
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

    // ---------------------------------------------------------------------------------------------
    // ModuleSetting - DataProvider.vb L142-L147 - composite key (ModuleId, SettingName)
    //
    // MIGRATION: the legacy settings idiom was an untyped map, not a row set. ModuleController.vb's
    // GetModuleSettings returned a Hashtable, filled it positionally from the reader
    // (`objSettings(dr.GetString(0)) = dr.GetString(1)`) and translated a SQL NULL value into the empty
    // string - the NullString sentinel from Null.vb:L73 - so a stored null and a stored zero-length
    // value became indistinguishable. Here the rows are what they always were in the database: entities
    // with the real composite primary key PK_ModuleSettings (ModuleID, SettingName). No Hashtable, no
    // dictionary projection and no sentinel translation is recreated, and because each row carries its
    // own key a caller can stage a change to one setting without rebuilding the collection. Note the
    // key's first component - the MODULE identifier - which is a different key space from
    // TabModuleSetting's below; conflating them would silently read or write another scope's settings.
    // ---------------------------------------------------------------------------------------------

    /// <inheritdoc />
    /// <remarks>
    /// Ordered by <see cref="ModuleSetting.SettingName"/>, the second half of the composite key, which
    /// is unique per module and therefore a total order.
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
    /// <remarks>
    /// The predicate names both halves of the composite key. The name is compared exactly as supplied -
    /// never trimmed, case-folded or coalesced - so an empty name addresses the row the legacy store
    /// could genuinely hold under an empty key rather than being rejected or widened into "any".
    /// </remarks>
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
    /// <remarks>
    /// Both halves of the composite key must already be populated by the caller, because neither is
    /// database-generated: <c>ModuleID</c> is configured <c>ValueGeneratedNever</c> here, since in this
    /// table it is a foreign key rather than an identity.
    /// </remarks>
    // MIGRATION: replaces `AddModuleSetting(ModuleId, SettingName, SettingValue)`
    // (DataProvider.vb:L144), which was already declared Sub and returned nothing - the clearest
    // corroboration that no add member on this contract owes the caller an identifier.
    public Task AddModuleSettingAsync(ModuleSetting moduleSetting, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(moduleSetting);
        cancellationToken.ThrowIfCancellationRequested();

        _dbContext.ModuleSettings.Add(moduleSetting);

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    /// <remarks>
    /// Stages the value only; the composite key identifies the row and is not itself updatable, so
    /// renaming a setting is a removal followed by an insertion. Insertion and update are kept as
    /// separate members, as the legacy provider kept them, so a caller states which it intends rather
    /// than relying on an implicit upsert.
    /// </remarks>
    // MIGRATION: replaces `UpdateModuleSetting(ModuleId, SettingName, SettingValue)`
    // (DataProvider.vb:L145).
    public Task UpdateModuleSettingAsync(ModuleSetting moduleSetting, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(moduleSetting);
        cancellationToken.ThrowIfCancellationRequested();

        if (_dbContext.Entry(moduleSetting).State is EntityState.Detached)
        {
            // Assigned rather than Update(...) for the reason given on UpdateAsync: the composite
            // setting keys and the module key can legitimately consist entirely of default values,
            // which Update would misread as "unsaved" and stage as an insert.
            _dbContext.Entry(moduleSetting).State = EntityState.Modified;
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    /// <remarks>
    /// Addressed by key rather than by entity so that a caller which knows only the name it wishes to
    /// clear need not read the row first. A key that resolves to no row is not an error.
    /// </remarks>
    // MIGRATION: replaces `DeleteModuleSetting(ModuleId, SettingName)` (DataProvider.vb:L146).
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
    /// <remarks>
    /// A module with no settings stages no removals at all, which is not an error, and no affected-row
    /// count is yielded - that number is what <see cref="IUnitOfWork.SaveChangesAsync"/> returns on
    /// commit.
    /// </remarks>
    // MIGRATION: replaces `DeleteModuleSettings(ByVal ModuleId As Integer)` (DataProvider.vb:L147). The
    // rows are materialised and handed to RemoveRange rather than removed with ExecuteDeleteAsync:
    // ExecuteDelete issues its DELETE immediately and outside the change tracker, so it would escape
    // the coordinated commit boundary entirely - a caller whose next step failed would find the
    // settings already gone with no transaction left to roll back, and the tracker would still hold
    // stale copies of rows that no longer exist.
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

    // ---------------------------------------------------------------------------------------------
    // TabModuleSetting - DataProvider.vb L149-L154 - composite key (TabModuleId, SettingName)
    //
    // MIGRATION: a separate table and therefore a separate entity, keyed PK_TabModuleSettings
    // (TabModuleID, SettingName). These settings are per-placement, so the same module placed on two
    // pages carries two independent collections - which is exactly why the schema keeps them apart from
    // the module-scoped rows above. The composite key differs from ModuleSetting's in its FIRST
    // component: this is (TabModuleId, SettingName) where that is (ModuleId, SettingName). Passing a
    // module identifier to a member here would silently address another scope's rows, so the two key
    // spaces are kept strictly apart and no member coerces between them.
    // ---------------------------------------------------------------------------------------------

    /// <inheritdoc />
    /// <remarks>
    /// Keyed by the <em>placement</em> identifier, not the module identifier. Ordered by
    /// <see cref="TabModuleSetting.SettingName"/>, which is unique per placement.
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
    /// <remarks>
    /// Both halves of the composite key are named in the predicate, and the name is matched literally
    /// for the same reason given on <see cref="GetModuleSettingAsync"/>.
    /// </remarks>
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
    /// <remarks>
    /// When the owning placement is itself newly staged its generated identifier does not exist yet, so
    /// the caller either stages the setting through the placement's own collection or orders the two
    /// calls so that the change tracker can resolve the relationship.
    /// </remarks>
    // MIGRATION: replaces `AddTabModuleSetting(TabModuleId, SettingName, SettingValue)`
    // (DataProvider.vb:L151), likewise already a Sub.
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
    // MIGRATION: replaces `UpdateTabModuleSetting(TabModuleId, SettingName, SettingValue)`
    // (DataProvider.vb:L152).
    public Task UpdateTabModuleSettingAsync(
        TabModuleSetting tabModuleSetting,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tabModuleSetting);
        cancellationToken.ThrowIfCancellationRequested();

        if (_dbContext.Entry(tabModuleSetting).State is EntityState.Detached)
        {
            // Assigned rather than Update(...) for the reason given on UpdateAsync: the composite
            // setting keys and the module key can legitimately consist entirely of default values,
            // which Update would misread as "unsaved" and stage as an insert.
            _dbContext.Entry(tabModuleSetting).State = EntityState.Modified;
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    /// <remarks>A key that resolves to no row is not an error.</remarks>
    // MIGRATION: replaces `DeleteTabModuleSetting(TabModuleId, SettingName)` (DataProvider.vb:L153).
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
    /// <remarks>
    /// The member a caller uses when retiring a placement, since these rows are keyed by the placement
    /// rather than by the module and would otherwise be orphaned.
    /// </remarks>
    // MIGRATION: replaces `DeleteTabModuleSettings(ByVal TabModuleId As Integer)`
    // (DataProvider.vb:L154). Materialised and staged with RemoveRange for the same reason as
    // DeleteModuleSettingsAsync: an immediate ExecuteDelete would write outside the coordinated unit of
    // work.
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
