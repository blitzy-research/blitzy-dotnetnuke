using DnnMigration.Domain.Abstractions.Repositories;
using DnnMigration.Domain.Entities;
using DnnMigration.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace DnnMigration.Infrastructure.Repositories;

// MIGRATION: this type replaces the legacy module-definition provider block
// (Library/Components/Providers/Data/DataProvider.vb), the three static controllers that wrapped it
// (DesktopModuleController.vb, ModuleDefinitionController.vb, ModuleControlController.vb) and the
// stored-procedure layer beneath them, every call of which reached the source-less binary
// Microsoft.ApplicationBlocks.Data.dll through SqlHelper with the procedure name built by string
// concatenation. Table and column binding belongs to the four configurations under
// Persistence/Configurations, so this file names no table, no column and no procedure, and issues no SQL of
// its own.
//
// MIGRATION: long positional argument lists and SCOPE_IDENTITY returns collapse to entity staging. The four
// legacy write procedures took nine to thirteen positional arguments and each returned the generated key.

/// <summary>
/// Reads and writes the module registration catalogue - installed packages, the per-portal grants
/// over them, the definitions each package publishes and the controls each definition publishes.
/// </summary>
/// <remarks>
/// <para>
/// Implements <see cref="IModuleDefinitionRepository"/> over <see cref="DnnDbContext"/> and nothing
/// else. The type is <see langword="internal"/> and <see langword="sealed"/> so that the
/// persistence session cannot escape this assembly and the contract stays the only route to the
/// catalogue: the container resolves it by its interface, and no consumer can name a query surface,
/// a transaction handle or a provider type through it.
/// </para>
/// <para>
/// <b>Reads never track.</b> Every query member states <c>AsNoTracking</c> explicitly, because
/// <see cref="DnnDbContext"/> declares no global tracking behaviour, and because a repository whose
/// reads tracked would let a caller's incidental mutation reach the database on the next commit
/// without any write member having been called.
/// </para>
/// </remarks>
internal sealed class ModuleDefinitionRepository : IModuleDefinitionRepository
{
    /// <summary>The lowest control ordinal the terminal control-key query admits.</summary>
    /// <remarks>
    /// Named rather than inlined because it is a reserved-value boundary carried over from the
    /// store, not an arithmetic constant: <c>04.05.00.SqlDataProvider</c> ends the terminal
    /// <c>GetModuleControlsByKey</c> predicate with <c>AND ControlType &gt;= -1</c>.
    /// </remarks>
    private const int LowestSelectableControlType = -1;

    private readonly DnnDbContext _dbContext;

    /// <summary>
    /// Initialises a new instance of the <see cref="ModuleDefinitionRepository"/> class.
    /// </summary>
    /// <param name="dbContext">The request-scoped persistence session.</param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="dbContext"/> is <see langword="null"/>.
    /// </exception>
    /// <remarks>
    /// MIGRATION: the only dependency, and the only one permitted. Nothing else is injected: no
    /// cache, no logger, no clock, no business-controller factory, no HTTP accessor and nothing
    /// from the security layer.
    /// </remarks>
    public ModuleDefinitionRepository(DnnDbContext dbContext)
    {
        ArgumentNullException.ThrowIfNull(dbContext);

        _dbContext = dbContext;
    }

    // DesktopModule - the installed module package. DataProvider.vb.

    /// <inheritdoc/>
    /// <remarks>
    /// Reproduces <c>GetDesktopModule</c> (<c>02.00.00.SqlDataProvider</c>), a plain equality read
    /// on the primary key. The key is unique by definition, so the single-row operator is exact
    /// rather than optimistic; a package that does not exist is reported by the null return, which
    /// is the distinction the legacy reflection hydrator could not make - it produced an object
    /// whose every property held a sentinel, indistinguishable from a package whose columns
    /// happened to hold those values.
    /// </remarks>
    public Task<DesktopModule?> GetDesktopModuleByIdAsync(int desktopModuleId, CancellationToken cancellationToken = default) =>
        _dbContext.DesktopModules
            .AsNoTracking()
            .SingleOrDefaultAsync(package => package.DesktopModuleId == desktopModuleId, cancellationToken);

    /// <inheritdoc/>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="moduleName"/> is <see langword="null"/>.
    /// </exception>
    /// <remarks>
    /// <para>
    /// Reproduces <c>GetDesktopModuleByModuleName</c> (<c>03.01.00.SqlDataProvider</c>), whose
    /// predicate is <c>ModuleName = @ModuleName</c> and nothing more.
    /// </para>
    /// <para>
    /// The single-row operator is exact because this is the one uniqueness the terminal schema
    /// imposes on the table - <c>IX_DesktopModules_ModuleName</c> at
    /// <c>03.01.00.SqlDataProvider</c>, declared unique by <c>DesktopModuleConfiguration</c> and
    /// dropped by no later script.
    /// </para>
    /// </remarks>
    public Task<DesktopModule?> GetDesktopModuleByModuleNameAsync(string moduleName, CancellationToken cancellationToken = default)
    {
        // MIGRATION: guarded rather than trusted. A null argument would translate to "ModuleName IS NULL"
        // and quietly answer with a row the caller did not ask for, which is the null-row reading the
        // contract excludes; an empty string, by contrast, is a legitimate key and is matched as given.
        ArgumentNullException.ThrowIfNull(moduleName);

        return _dbContext.DesktopModules
            .AsNoTracking()
            .SingleOrDefaultAsync(package => package.ModuleName == moduleName, cancellationToken);
    }

    /// <inheritdoc/>
    /// <remarks>
    /// <para>
    /// Reproduces the terminal <c>GetDesktopModulesByPortal</c> (<c>04.05.00.SqlDataProvider</c>)
    /// clause for clause.
    /// </para>
    /// <para>
    /// MIGRATION: the portal identifier is applied exactly. It is not a wildcard, and -1 in
    /// particular selects the portal whose key is -1 rather than every portal, even though the
    /// legacy integer sentinel was that same value (<c>Null.vb</c>) and <c>dbo.Portals.PortalID</c>
    /// is declared <c>IDENTITY(-1, 1)</c> - which is precisely why the sentinel could not tell a
    /// real tenant from a missing one.
    /// </para>
    /// </remarks>
    public async Task<IReadOnlyList<DesktopModule>> GetDesktopModulesByPortalIdAsync(int portalId, CancellationToken cancellationToken = default) =>
        await _dbContext.DesktopModules
            .AsNoTracking()
            .Where(package =>
                !package.IsAdmin
                && (!package.IsPremium
                    || package.PortalDesktopModules.Any(grant => grant.PortalId == portalId)))
            .OrderBy(package => package.FriendlyName)
            .ThenBy(package => package.DesktopModuleId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

    /// <inheritdoc/>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="desktopModule"/> is <see langword="null"/>.
    /// </exception>
    /// <remarks>
    /// Replaces <c>AddDesktopModule</c> (<c>DataProvider.vb</c>), twelve positional arguments
    /// assembled at <c>DesktopModuleController.vb</c> and executed as a scalar read of
    /// <c>SCOPE_IDENTITY</c> at <c>SqlDataProvider.vb</c>.
    /// </remarks>
    public async Task AddDesktopModuleAsync(DesktopModule desktopModule, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(desktopModule);

        await _dbContext.DesktopModules.AddAsync(desktopModule, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="desktopModule"/> is <see langword="null"/>.
    /// </exception>
    /// <remarks>
    /// <para>
    /// Replaces <c>UpdateDesktopModule</c> (<c>DataProvider.vb</c>), thirteen positional arguments
    /// assembled at <c>DesktopModuleController.vb</c> and reached through the overload pair whose
    /// only difference was a cache flag.
    /// </para>
    /// <para>
    /// MIGRATION: the supplied entity alone is staged, by transitioning its entry rather than by
    /// asking the set to update it. The distinction is load-bearing here rather than stylistic,
    /// because the set-level call also walks the object graph and stages what it reaches - and a
    /// package obtained from <see cref="ModuleDefinition.DesktopModule"/> has its own definitions
    /// hanging off it, so updating one package would have written rows in
    /// <c>dbo.ModuleDefinitions</c> that no caller asked to change.
    /// </para>
    /// </remarks>
    public Task UpdateDesktopModuleAsync(DesktopModule desktopModule, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(desktopModule);
        cancellationToken.ThrowIfCancellationRequested();

        _dbContext.Entry(desktopModule).State = EntityState.Modified;

        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Replaces <c>DeleteDesktopModule</c> (<c>DataProvider.vb</c>, wrapped at
    /// <c>DesktopModuleController.vb</c>). The row is loaded before it is staged for removal, so
    /// removing a package that is already absent is an idempotent no-op rather than a failure -
    /// which is how the legacy statement behaved, since a delete matching nothing is not an error.
    /// </remarks>
    public async Task DeleteDesktopModuleAsync(int desktopModuleId, CancellationToken cancellationToken = default)
    {
        DesktopModule? package = await _dbContext.DesktopModules
            .SingleOrDefaultAsync(candidate => candidate.DesktopModuleId == desktopModuleId, cancellationToken)
            .ConfigureAwait(false);

        if (package is not null)
        {
            _dbContext.DesktopModules.Remove(package);
        }
    }

    // PortalDesktopModule - the per-portal grant. DataProvider.vb.

    /// <inheritdoc/>
    /// <remarks>
    /// <para>
    /// Supersedes <c>GetPortalDesktopModules</c> (<c>02.02.02.SqlDataProvider</c>), reached through
    /// the ArrayList-returning wrapper at <c>DesktopModuleController.vb</c>. Both identifiers are
    /// applied exactly and the legacy <c>order by PortalId, DesktopModuleId</c> is reproduced, then
    /// extended with the key so the order is total by construction rather than by relying on the
    /// constraint.
    /// </para>
    /// <para>
    /// The pair is unique in the terminal schema - <c>IX_PortalDesktopModules</c>, declared unique
    /// by <c>PortalDesktopModuleConfiguration</c> - so at most one grant can match. The list shape
    /// is the contract's, because the uniqueness is the database's guarantee to make rather than
    /// this signature's to assert.
    /// </para>
    /// </remarks>
    public async Task<IReadOnlyList<PortalDesktopModule>> GetPortalDesktopModulesAsync(int portalId, int desktopModuleId, CancellationToken cancellationToken = default) =>
        await _dbContext.PortalDesktopModules
            .AsNoTracking()
            .Include(grant => grant.Portal)
            .Include(grant => grant.DesktopModule)
            .Where(grant => grant.PortalId == portalId && grant.DesktopModuleId == desktopModuleId)
            .OrderBy(grant => grant.PortalId)
            .ThenBy(grant => grant.DesktopModuleId)
            .ThenBy(grant => grant.PortalDesktopModuleId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

    /// <inheritdoc />
    /// <exception cref="ArgumentNullException">
    /// <paramref name="portalDesktopModule" /> is <see langword="null" />.
    /// </exception>
    /// <remarks>
    /// Replaces <c>AddPortalDesktopModule</c> (<c>DataProvider.vb</c>, wrapped at
    /// <c>DesktopModuleController.vb</c>).
    /// </remarks>
    public async Task AddPortalDesktopModuleAsync(PortalDesktopModule portalDesktopModule, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(portalDesktopModule);

        await _dbContext.PortalDesktopModules.AddAsync(portalDesktopModule, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    /// <remarks>
    /// <para>
    /// Replaces <c>DeletePortalDesktopModules</c> (<c>DataProvider.vb</c>, wrapped at
    /// <c>DesktopModuleController.vb</c>). Both identifiers are applied exactly - the legacy
    /// wrapper passed both through the sentinel converter (<c>SqlDataProvider.vb</c>), so -1 once
    /// withdrew every portal's grants at once, and it no longer does.
    /// </para>
    /// <para>
    /// MIGRATION: the matching rows are materialised and then staged for removal, rather than
    /// deleted by a set-based statement. A set-based delete would execute at once, ahead of
    /// everything else the caller has staged and outside the change tracker that then still holds
    /// the removed rows - and, absent an explicit transaction, it would be durable immediately.
    /// </para>
    /// </remarks>
    public async Task DeletePortalDesktopModulesAsync(int portalId, int desktopModuleId, CancellationToken cancellationToken = default)
    {
        List<PortalDesktopModule> grants = await _dbContext.PortalDesktopModules
            .Where(grant => grant.PortalId == portalId && grant.DesktopModuleId == desktopModuleId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        if (grants.Count > 0)
        {
            _dbContext.PortalDesktopModules.RemoveRange(grants);
        }
    }

    // ModuleDefinition - what a package publishes. DataProvider.vb.

    /// <inheritdoc/>
    /// <remarks>
    /// <para>
    /// Supersedes <c>GetModuleDefinitions</c> (<c>02.02.00.SqlDataProvider</c>), reached through
    /// the ArrayList-returning wrapper at <c>ModuleDefinitionController.vb</c>.
    /// </para>
    /// <para>
    /// MIGRATION: the identifier is applied exactly, and the legacy wildcard is not reproduced. The
    /// terminal predicate reads
    /// <c>where DesktopModuleId = @DesktopModuleId or @DesktopModuleId = -1</c> - the sentinel
    /// spelled out as a literal rather than hidden behind a NULL conversion - so -1 returned every
    /// definition in the installation.
    /// </para>
    /// </remarks>
    public async Task<IReadOnlyList<ModuleDefinition>> GetModuleDefinitionsByDesktopModuleIdAsync(int desktopModuleId, CancellationToken cancellationToken = default) =>
        await _dbContext.ModuleDefinitions
            .AsNoTracking()
            .Include(definition => definition.DesktopModule)
            .Where(definition => definition.DesktopModuleId == desktopModuleId)
            .OrderBy(definition => definition.FriendlyName)
            .ThenBy(definition => definition.ModuleDefinitionId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

    /// <inheritdoc/>
    /// <remarks>
    /// <para>
    /// MIGRATION: this member has no single legacy counterpart, and it collapses an N+1 the legacy
    /// screens performed by hand: <c>GetDesktopModulesByPortal</c> (<c>DataProvider.vb</c>) to find
    /// the packages a portal could use, then <c>GetModuleDefinitions</c> once per package to find
    /// what each published.
    /// </para>
    /// <para>
    /// Availability carries the same rule as <see cref="GetDesktopModulesByPortalIdAsync"/>: an
    /// administrative package is excluded outright, then a remaining definition is placeable when
    /// its owning package is not premium or when a grant exists for the pair
    /// (<c>04.05.00.SqlDataProvider</c>). The administrative exclusion is required here because
    /// this member is the single-query replacement for the legacy two-step package-then-definition
    /// read; omitting the package predicate from the collapsed query reintroduced rows the first
    /// legacy step had removed.
    /// </para>
    /// </remarks>
    public async Task<IReadOnlyList<ModuleDefinition>> GetModuleDefinitionsByPortalIdAsync(int? portalId, CancellationToken cancellationToken = default)
    {
        IQueryable<ModuleDefinition> definitions = _dbContext.ModuleDefinitions
            .AsNoTracking()
            .Include(definition => definition.DesktopModule);

        if (portalId.HasValue)
        {
            int addressedPortalId = portalId.Value;

            definitions = definitions.Where(definition =>
                !definition.DesktopModule!.IsAdmin
                && (!definition.DesktopModule!.IsPremium
                    || definition.DesktopModule!.PortalDesktopModules.Any(
                        grant => grant.PortalId == addressedPortalId)));
        }

        return await definitions
            .OrderBy(definition => definition.FriendlyName)
            .ThenBy(definition => definition.ModuleDefinitionId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Administrative definitions are intentionally reachable only through this named lookup. A
    /// deleted instance does not make its security settings source available.
    /// </remarks>
    public Task<ModuleDefinition?> GetAdministrativeDefinitionByFriendlyNameAsync(
        int portalId,
        string friendlyName,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(friendlyName);

        return _dbContext.ModuleDefinitions
            .AsNoTracking()
            .Include(definition => definition.DesktopModule)
            .Where(definition =>
                definition.DesktopModule!.IsAdmin
                && definition.FriendlyName == friendlyName
                && _dbContext.Modules.Any(module =>
                    module.PortalId == portalId
                    && module.ModuleDefinitionId == definition.ModuleDefinitionId
                    && !module.IsDeleted))
            .OrderBy(definition => definition.ModuleDefinitionId)
            .FirstOrDefaultAsync(cancellationToken);
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Reproduces <c>GetModuleDefinition</c> (<c>02.00.00.SqlDataProvider</c>), a plain equality
    /// read on the primary key, reached through <c>ModuleDefinitionController.vb</c>.
    /// </remarks>
    public Task<ModuleDefinition?> GetModuleDefinitionByIdAsync(int moduleDefinitionId, CancellationToken cancellationToken = default) =>
        _dbContext.ModuleDefinitions
            .AsNoTracking()
            .Include(definition => definition.DesktopModule)
            .SingleOrDefaultAsync(definition => definition.ModuleDefinitionId == moduleDefinitionId, cancellationToken);

    /// <inheritdoc/>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="friendlyName"/> is <see langword="null"/>.
    /// </exception>
    /// <remarks>
    /// <para>
    /// Reproduces <c>GetModuleDefinitionByName</c> (<c>02.00.00.SqlDataProvider</c>), whose
    /// predicate is <c>DesktopModuleId = @DesktopModuleId and FriendlyName = @FriendlyName</c>,
    /// reached through <c>ModuleDefinitionController.vb</c>.
    /// </para>
    /// <para>
    /// The single-row operator is exact because a definition's friendly name is unique in the
    /// terminal schema on its own - <c>01.00.08.SqlDataProvider</c> adds
    /// <c>CONSTRAINT IX_ModuleDefinitions UNIQUE NONCLUSTERED (FriendlyName)</c> and no later
    /// script drops it - so narrowing by the owning package can only reduce the match further.
    /// </para>
    /// </remarks>
    public Task<ModuleDefinition?> GetModuleDefinitionByNameAsync(int desktopModuleId, string friendlyName, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(friendlyName);

        return _dbContext.ModuleDefinitions
            .AsNoTracking()
            .Include(definition => definition.DesktopModule)
            .SingleOrDefaultAsync(
                definition => definition.DesktopModuleId == desktopModuleId
                    && definition.FriendlyName == friendlyName,
                cancellationToken);
    }

    /// <inheritdoc/>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="moduleDefinition"/> is <see langword="null"/>.
    /// </exception>
    /// <remarks>
    /// Replaces <c>AddModuleDefinition</c> (<c>DataProvider.vb</c>), assembled at
    /// <c>ModuleDefinitionController.vb</c> from the owning package, the friendly name and the
    /// default cache time, and executed as a scalar read of <c>SCOPE_IDENTITY</c> at
    /// <c>SqlDataProvider.vb</c>. The owning package is fixed at insertion, because the legacy
    /// update procedure did not accept it and neither does the update member below.
    /// </remarks>
    public async Task AddModuleDefinitionAsync(ModuleDefinition moduleDefinition, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(moduleDefinition);

        await _dbContext.ModuleDefinitions.AddAsync(moduleDefinition, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="moduleDefinition"/> is <see langword="null"/>.
    /// </exception>
    /// <remarks>
    /// <para>
    /// Replaces <c>UpdateModuleDefinition</c> (<c>DataProvider.vb</c>), reached through the
    /// overload pair at <c>ModuleDefinitionController.vb</c> and whose only difference was a cache
    /// flag.
    /// </para>
    /// <para>
    /// MIGRATION: staging the supplied entity by transitioning its entry, rather than asking the
    /// set to update it, is what keeps this member from doing more than the legacy one did.
    /// </para>
    /// </remarks>
    public Task UpdateModuleDefinitionAsync(ModuleDefinition moduleDefinition, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(moduleDefinition);
        cancellationToken.ThrowIfCancellationRequested();

        _dbContext.Entry(moduleDefinition).State = EntityState.Modified;

        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Replaces <c>DeleteModuleDefinition</c> (<c>DataProvider.vb</c>, wrapped at
    /// <c>ModuleDefinitionController.vb</c>). The row is loaded - deliberately tracked, because
    /// removal transitions a tracked row - before it is staged for removal, so removing a
    /// definition that is already absent is an idempotent no-op.
    /// </remarks>
    public async Task DeleteModuleDefinitionAsync(int moduleDefinitionId, CancellationToken cancellationToken = default)
    {
        ModuleDefinition? definition = await _dbContext.ModuleDefinitions
            .SingleOrDefaultAsync(candidate => candidate.ModuleDefinitionId == moduleDefinitionId, cancellationToken)
            .ConfigureAwait(false);

        if (definition is not null)
        {
            _dbContext.ModuleDefinitions.Remove(definition);
        }
    }

    // ModuleControl - how a definition is reached. DataProvider.vb.

    /// <inheritdoc/>
    /// <remarks>
    /// Reproduces <c>GetModuleControl</c> (<c>02.00.00.SqlDataProvider</c>), a plain equality read
    /// on the primary key, reached through <c>ModuleControlController.vb</c> and the hand-written
    /// hydration path this replaces. The owning definition is not loaded: a control is asked for by
    /// its own key here, and the definition is nullable on this table, so eager loading it would
    /// add a join that is optional in the schema and unnecessary to the question.
    /// </remarks>
    public Task<ModuleControl?> GetModuleControlByIdAsync(int moduleControlId, CancellationToken cancellationToken = default) =>
        _dbContext.ModuleControls
            .AsNoTracking()
            .SingleOrDefaultAsync(control => control.ModuleControlId == moduleControlId, cancellationToken);

    /// <inheritdoc/>
    /// <remarks>
    /// <para>
    /// Supersedes <c>GetModuleControls</c> (<c>04.05.00.SqlDataProvider</c>), reached through the
    /// ArrayList-returning wrapper at <c>ModuleControlController.vb</c>. The terminal
    /// <c>ORDER BY ControlKey, ViewOrder</c> is reproduced in that order - control key first, which
    /// is the sequence the legacy screens listed - and extended with the key.
    /// </para>
    /// <para>
    /// MIGRATION: the definition identifier is applied exactly. The legacy wrapper passed it
    /// through the sentinel converter (<c>SqlDataProvider.vb</c>) and the procedure paired the
    /// resulting NULL with its own null column -
    /// <c>((ModuleDefId is null and @ModuleDefId is null) or (ModuleDefId = @ModuleDefId))</c> - so
    /// -1 returned the host-level controls that belong to no definition at all.
    /// </para>
    /// </remarks>
    public async Task<IReadOnlyList<ModuleControl>> GetModuleControlsByDefinitionIdAsync(int moduleDefinitionId, CancellationToken cancellationToken = default) =>
        await _dbContext.ModuleControls
            .AsNoTracking()
            .Where(control => control.ModuleDefinitionId == moduleDefinitionId)
            .OrderBy(control => control.ControlKey)
            .ThenBy(control => control.ViewOrder)
            .ThenBy(control => control.ModuleControlId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

    /// <inheritdoc/>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="controlKey"/> is <see langword="null"/>.
    /// </exception>
    /// <remarks>
    /// <para>
    /// Supersedes <c>GetModuleControlsByKey</c> (<c>04.05.00.SqlDataProvider</c>), reached through
    /// the ArrayList-returning wrapper at <c>ModuleControlController.vb</c>. A control key is not
    /// unique by itself, which is why this member returns a list where the key-and-source lookup
    /// below returns at most one row: a definition may publish several controls under one key,
    /// distinguished by their source.
    /// </para>
    /// <para>
    /// MIGRATION: the reserved-ordinal exclusion the store carried is reproduced explicitly,
    /// because it is a property of the legacy query rather than of the contract and therefore
    /// invisible in the signature. This is the only place in this file where the ordinal is
    /// examined at all, and it is a comparison against a boundary the store defined - see
    /// <see cref="LowestSelectableControlType"/> - never an interpretation of what the ordinal
    /// means.
    /// </para>
    /// </remarks>
    public async Task<IReadOnlyList<ModuleControl>> GetModuleControlsByKeyAsync(string controlKey, int moduleDefinitionId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(controlKey);

        return await _dbContext.ModuleControls
            .AsNoTracking()
            .Where(control =>
                control.ModuleDefinitionId == moduleDefinitionId
                && control.ControlKey == controlKey
                && control.ControlType >= LowestSelectableControlType)
            .OrderBy(control => control.ViewOrder)
            .ThenBy(control => control.ModuleControlId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc/>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="controlKey"/> or <paramref name="controlSrc"/> is <see langword="null"/>.
    /// </exception>
    /// <remarks>
    /// <para>
    /// Reproduces <c>GetModuleControlByKeyAndSrc</c> (<c>04.05.00.SqlDataProvider</c>), reached
    /// through <c>ModuleControlController.vb</c>. The three values together are the natural key the
    /// schema enforces with a unique index - <c>IX_ModuleControls</c> over
    /// <c>(ModuleDefID, ControlKey, ControlSrc)</c>, added at <c>02.00.00.SqlDataProvider</c>,
    /// dropped by no later script and declared unique and unfiltered by
    /// <c>ModuleControlConfiguration</c> - which is why the single-row operator is exact here while
    /// the key-only lookup above returns a list.
    /// </para>
    /// <para>
    /// MIGRATION: the control source completes the key and nothing more. It holds a Web Forms
    /// control path in the legacy data and is carried through untouched; nothing here resolves,
    /// loads or probes for what it names, because the presentation layer is no longer
    /// server-rendered.
    /// </para>
    /// </remarks>
    public Task<ModuleControl?> GetModuleControlByKeyAndSrcAsync(int moduleDefinitionId, string controlKey, string controlSrc, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(controlKey);
        ArgumentNullException.ThrowIfNull(controlSrc);

        return _dbContext.ModuleControls
            .AsNoTracking()
            .SingleOrDefaultAsync(
                control => control.ModuleDefinitionId == moduleDefinitionId
                    && control.ControlKey == controlKey
                    && control.ControlSrc == controlSrc,
                cancellationToken);
    }

    /// <inheritdoc/>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="moduleControl"/> is <see langword="null"/>.
    /// </exception>
    /// <remarks>
    /// Replaces <c>AddModuleControl</c> (<c>DataProvider.vb</c>), nine positional arguments
    /// assembled at <c>ModuleControlController.vb</c> - one of them a cast of the access-level
    /// enumeration back to the integer it had always been - and executed as a scalar read of
    /// <c>SCOPE_IDENTITY</c> at <c>SqlDataProvider.vb</c>.
    /// </remarks>
    public async Task AddModuleControlAsync(ModuleControl moduleControl, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(moduleControl);

        await _dbContext.ModuleControls.AddAsync(moduleControl, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="moduleControl"/> is <see langword="null"/>.
    /// </exception>
    /// <remarks>
    /// <para>
    /// Replaces <c>UpdateModuleControl</c> (<c>DataProvider.vb</c>), ten positional arguments - the
    /// nine of the insertion plus the identity - assembled at <c>ModuleControlController.vb</c> and
    /// reached through the overload pair whose only difference was a cache flag.
    /// </para>
    /// <para>
    /// MIGRATION: the supplied entity alone is staged, by transitioning its entry rather than by
    /// asking the set to update it, so a control that arrives carrying its owning definition does
    /// not drag a write to <c>dbo.ModuleDefinitions</c> along with it. The nullable columns are
    /// written as the entity holds them: a null control key, control title, control source, icon
    /// file, view order or help address reaches the store as SQL NULL rather than as the empty
    /// string or -1 the legacy sentinel system produced, which is what keeps a definition's default
    /// control - identified by a null control key at <c>04.05.00.SqlDataProvider</c> - findable at
    /// all.
    /// </para>
    /// </remarks>
    public Task UpdateModuleControlAsync(ModuleControl moduleControl, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(moduleControl);
        cancellationToken.ThrowIfCancellationRequested();

        _dbContext.Entry(moduleControl).State = EntityState.Modified;

        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Replaces <c>DeleteModuleControl</c> (<c>DataProvider.vb</c>, wrapped at
    /// <c>ModuleControlController.vb</c>). The row is loaded - deliberately tracked, because
    /// removal transitions a tracked row - before it is staged for removal, so removing a control
    /// that is already absent is an idempotent no-op rather than something the caller has to guard
    /// against.
    /// </remarks>
    public async Task DeleteModuleControlAsync(int moduleControlId, CancellationToken cancellationToken = default)
    {
        ModuleControl? control = await _dbContext.ModuleControls
            .SingleOrDefaultAsync(candidate => candidate.ModuleControlId == moduleControlId, cancellationToken)
            .ConfigureAwait(false);

        if (control is not null)
        {
            _dbContext.ModuleControls.Remove(control);
        }
    }
}
