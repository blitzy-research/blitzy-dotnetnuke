using DnnMigration.Domain.Abstractions.Repositories;
using DnnMigration.Domain.Entities;
using DnnMigration.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace DnnMigration.Infrastructure.Repositories;

/// <summary>
/// Reads and writes the module registration catalogue - installed packages, the per-portal grants over
/// them, the definitions each package publishes and the controls each definition publishes.
/// </summary>
/// <remarks>
/// Implements <see cref="IModuleDefinitionRepository"/> over <see cref="DnnDbContext"/> and nothing else.
/// </remarks>
internal sealed class ModuleDefinitionRepository : IModuleDefinitionRepository
{
    /// <summary>The lowest control ordinal the terminal control-key query admits.</summary>
    private const int LowestSelectableControlType = -1;

    private readonly DnnDbContext _dbContext;

    /// <summary>Initialises a new instance of the <see cref="ModuleDefinitionRepository"/> class.</summary>
    /// <param name="dbContext">The request-scoped persistence session.</param>
    /// <exception cref="ArgumentNullException"><paramref name="dbContext"/> is <see langword="null"/>.</exception>
    /// <remarks>
    /// The only dependency, and the only one permitted. Nothing else is injected: no cache, no logger, no
    /// clock, no business-controller factory, no HTTP accessor and nothing from the security layer.
    /// </remarks>
    public ModuleDefinitionRepository(DnnDbContext dbContext)
    {
        ArgumentNullException.ThrowIfNull(dbContext);

        _dbContext = dbContext;
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Reproduces <c>GetDesktopModule</c> (<c>02.00.00.SqlDataProvider</c>), a plain equality read on the
    /// primary key.
    /// </remarks>
    public Task<DesktopModule?> GetDesktopModuleByIdAsync(int desktopModuleId, CancellationToken cancellationToken = default) =>
        _dbContext.DesktopModules
            .AsNoTracking()
            .SingleOrDefaultAsync(package => package.DesktopModuleId == desktopModuleId, cancellationToken);

    /// <inheritdoc/>
    /// <exception cref="ArgumentNullException"><paramref name="moduleName"/> is <see langword="null"/>.</exception>
    /// <remarks>
    /// The single-row operator is exact because this is the one uniqueness the terminal schema imposes on
    /// the table - <c>IX_DesktopModules_ModuleName</c> at <c>03.01.00.SqlDataProvider</c>, declared unique
    /// by <c>DesktopModuleConfiguration</c> and dropped by no later script.
    /// </remarks>
    public Task<DesktopModule?> GetDesktopModuleByModuleNameAsync(string moduleName, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(moduleName);

        return _dbContext.DesktopModules
            .AsNoTracking()
            .SingleOrDefaultAsync(package => package.ModuleName == moduleName, cancellationToken);
    }

    /// <inheritdoc/>
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
    /// The supplied entity alone is staged, by transitioning its entry rather than by asking the set to
    /// update it.
    /// </remarks>
    public Task UpdateDesktopModuleAsync(DesktopModule desktopModule, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(desktopModule);
        cancellationToken.ThrowIfCancellationRequested();

        _dbContext.Entry(desktopModule).State = EntityState.Modified;

        return Task.CompletedTask;
    }

    /// <inheritdoc/>
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

    /// <inheritdoc/>
    /// <remarks>
    /// The pair is unique in the terminal schema - <c>IX_PortalDesktopModules</c>, declared unique by
    /// <c>PortalDesktopModuleConfiguration</c> - so at most one grant can match. The list shape is the
    /// contract's, because the uniqueness is the database's guarantee to make rather than this signature's
    /// to assert.
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
    public async Task AddPortalDesktopModuleAsync(PortalDesktopModule portalDesktopModule, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(portalDesktopModule);

        await _dbContext.PortalDesktopModules.AddAsync(portalDesktopModule, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Replaces <c>DeletePortalDesktopModules</c>. Both identifiers are applied exactly - the legacy
    /// wrapper passed both through the sentinel converter, so -1 once withdrew every portal's grants at
    /// once, and it no longer does.
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

    /// <inheritdoc/>
    /// <remarks>
    /// MIGRATION: the identifier is applied exactly, and the legacy wildcard is not reproduced. The
    /// terminal predicate reads <c>where DesktopModuleId = @DesktopModuleId or @DesktopModuleId = -1</c> -
    /// the sentinel spelled out as a literal rather than hidden behind a NULL conversion - so -1 returned
    /// every definition in the installation.
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
    /// Availability carries the same rule as <see cref="GetDesktopModulesByPortalIdAsync"/>: an
    /// administrative package is excluded outright, then a remaining definition is placeable when its
    /// owning package is not premium or when a grant exists for the pair (<c>04.05.00.SqlDataProvider</c>).
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
    /// Administrative definitions are intentionally reachable only through this named lookup. A deleted
    /// instance does not make its security settings source available.
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
    public Task<ModuleDefinition?> GetModuleDefinitionByIdAsync(int moduleDefinitionId, CancellationToken cancellationToken = default) =>
        _dbContext.ModuleDefinitions
            .AsNoTracking()
            .Include(definition => definition.DesktopModule)
            .SingleOrDefaultAsync(definition => definition.ModuleDefinitionId == moduleDefinitionId, cancellationToken);

    /// <inheritdoc/>
    /// <exception cref="ArgumentNullException"><paramref name="friendlyName"/> is <see langword="null"/>.</exception>
    /// <remarks>
    /// The single-row operator is exact because a definition's friendly name is unique in the terminal
    /// schema on its own - <c>01.00.08.SqlDataProvider</c> adds <c>CONSTRAINT IX_ModuleDefinitions UNIQUE
    /// NONCLUSTERED (FriendlyName)</c> and no later script drops it - so narrowing by the owning package
    /// can only reduce the match further.
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
    public async Task AddModuleDefinitionAsync(ModuleDefinition moduleDefinition, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(moduleDefinition);

        await _dbContext.ModuleDefinitions.AddAsync(moduleDefinition, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="moduleDefinition"/> is <see langword="null"/>.
    /// </exception>
    public Task UpdateModuleDefinitionAsync(ModuleDefinition moduleDefinition, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(moduleDefinition);
        cancellationToken.ThrowIfCancellationRequested();

        _dbContext.Entry(moduleDefinition).State = EntityState.Modified;

        return Task.CompletedTask;
    }

    /// <inheritdoc/>
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

    /// <inheritdoc/>
    public Task<ModuleControl?> GetModuleControlByIdAsync(int moduleControlId, CancellationToken cancellationToken = default) =>
        _dbContext.ModuleControls
            .AsNoTracking()
            .SingleOrDefaultAsync(control => control.ModuleControlId == moduleControlId, cancellationToken);

    /// <inheritdoc/>
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
    /// <exception cref="ArgumentNullException"><paramref name="controlKey"/> is <see langword="null"/>.</exception>
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
    /// MIGRATION: the control source completes the key and nothing more. It holds a Web Forms control path
    /// in the legacy data and is carried through untouched; nothing here resolves, loads or probes for what
    /// it names, because the presentation layer is no longer server-rendered.
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
    /// The supplied entity alone is staged, by transitioning its entry rather than by asking the set to
    /// update it, so a control that arrives carrying its owning definition does not drag a write to
    /// <c>dbo.ModuleDefinitions</c> along with it.
    /// </remarks>
    public Task UpdateModuleControlAsync(ModuleControl moduleControl, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(moduleControl);
        cancellationToken.ThrowIfCancellationRequested();

        _dbContext.Entry(moduleControl).State = EntityState.Modified;

        return Task.CompletedTask;
    }

    /// <inheritdoc/>
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
