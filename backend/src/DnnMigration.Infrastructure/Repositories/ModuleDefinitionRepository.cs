using DnnMigration.Domain.Abstractions.Repositories;
using DnnMigration.Domain.Entities;
using DnnMigration.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace DnnMigration.Infrastructure.Repositories;

/// <summary>
/// Reads the installation-time module catalogue and writes its per-portal grants.
/// </summary>
/// <remarks>
/// MIGRATION: replaces the read surface of <c>Library/Components/Modules/DesktopModuleController.vb</c>
/// and <c>Library/Components/Modules/Definitions/ModuleDefinitionController.vb</c>, including the ten
/// reflection-hydrator call sites those two files used between them. The catalogue itself is
/// installation data, so the only mutable part of this contract is the per-portal grant that portal
/// administration toggles.
/// <para>
/// Every read member that returns a <see cref="ModuleDefinition"/> loads its
/// <see cref="ModuleDefinition.DesktopModule"/>. That navigation is declared non-nullable because
/// <c>ModuleDefinitions.DesktopModuleID</c> is a required column with a cascading foreign key, so
/// returning a definition without it would hand the caller a null through a reference the compiler
/// has been told cannot be null.
/// </para>
/// </remarks>
internal sealed class ModuleDefinitionRepository : IModuleDefinitionRepository
{
    private readonly DnnDbContext _context;

    /// <summary>Initialises a new instance of the <see cref="ModuleDefinitionRepository"/> class.</summary>
    /// <param name="context">The unit-of-work scoped database context.</param>
    /// <exception cref="ArgumentNullException"><paramref name="context"/> is <see langword="null"/>.</exception>
    public ModuleDefinitionRepository(DnnDbContext context)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
    }

    /// <inheritdoc />
    /// <remarks>
    /// A premium module is one an installation grants to individual portals; a non-premium module is
    /// available to every portal without a grant. The visibility test is therefore a disjunction
    /// evaluated by the store in one round trip, not a catalogue read followed by an in-memory
    /// filter. When no portal is named the whole catalogue is returned, which is what host-level
    /// administration needs in order to decide which modules to grant.
    /// </remarks>
    public async Task<IReadOnlyList<ModuleDefinition>> ListAsync(int? portalId, CancellationToken cancellationToken = default)
    {
        IQueryable<ModuleDefinition> query = _context.ModuleDefinitions
            .Include(d => d.DesktopModule);

        if (portalId.HasValue)
        {
            int owner = portalId.Value;

            query = query.Where(d =>
                !d.DesktopModule!.IsPremium
                || _context.PortalDesktopModules.Any(g =>
                    g.PortalId == owner && g.DesktopModuleId == d.DesktopModuleId));
        }

        return await query
            .OrderBy(d => d.FriendlyName)
            .ThenBy(d => d.ModuleDefinitionId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task<ModuleDefinition?> GetAsync(int moduleDefinitionId, CancellationToken cancellationToken = default)
    {
        return _context.ModuleDefinitions
            .Include(d => d.DesktopModule)
            .FirstOrDefaultAsync(d => d.ModuleDefinitionId == moduleDefinitionId, cancellationToken);
    }

    /// <inheritdoc />
    /// <remarks>
    /// <c>ModuleControls.ViewOrder</c> is nullable, so controls that carry no explicit position sort
    /// together and are then separated by their primary key. Ordering entirely in the store keeps the
    /// result deterministic without the caller having to re-sort.
    /// </remarks>
    public async Task<IReadOnlyList<ModuleControl>> ListControlsAsync(int moduleDefinitionId, CancellationToken cancellationToken = default)
    {
        return await _context.ModuleControls
            .Where(c => c.ModuleDefinitionId == moduleDefinitionId)
            .OrderBy(c => c.ViewOrder)
            .ThenBy(c => c.ModuleControlId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task<DesktopModule?> GetDesktopModuleAsync(int desktopModuleId, CancellationToken cancellationToken = default)
    {
        return _context.DesktopModules
            .FirstOrDefaultAsync(m => m.DesktopModuleId == desktopModuleId, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<PortalDesktopModule>> ListPortalGrantsAsync(int portalId, CancellationToken cancellationToken = default)
    {
        // The desktop module is loaded with the grant so that a caller can name what has been
        // granted without issuing one further read per row.
        return await _context.PortalDesktopModules
            .Include(g => g.DesktopModule)
            .Where(g => g.PortalId == portalId)
            .OrderBy(g => g.DesktopModuleId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public void AddPortalGrant(PortalDesktopModule grant)
    {
        ArgumentNullException.ThrowIfNull(grant);
        _context.PortalDesktopModules.Add(grant);
    }

    /// <inheritdoc />
    public void RemovePortalGrant(PortalDesktopModule grant)
    {
        ArgumentNullException.ThrowIfNull(grant);
        _context.PortalDesktopModules.Remove(grant);
    }
}
