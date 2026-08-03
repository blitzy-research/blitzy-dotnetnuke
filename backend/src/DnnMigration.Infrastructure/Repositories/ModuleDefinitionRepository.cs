using DnnMigration.Domain.Abstractions.Repositories;
using DnnMigration.Domain.Entities;
using DnnMigration.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace DnnMigration.Infrastructure.Repositories;

/// <summary>
/// Reads and writes the installation-time module registration catalogue.
/// </summary>
/// <remarks>
/// <para>
/// MIGRATION: replaces the whole of the legacy module-definition provider block
/// (Library/Components/Providers/Data/DataProvider.vb lines 156 to 183) together with the three
/// controllers that wrapped it - DesktopModuleController.vb, ModuleDefinitionController.vb and
/// ModuleControlController.vb - including the reflection-hydrator call sites and the hand-written
/// reader loop those files used between them.
/// </para>
/// <para>
/// MIGRATION: every legacy insert returned the generated key because its stored procedure ended
/// with a scope-identity read. Here each write only stages the change, so the key is assigned when
/// <see cref="IUnitOfWork.SaveChangesAsync"/> commits. That keeps a multi-table write - a package,
/// its definitions, its controls and its grants - inside one transaction instead of committing once
/// per row.
/// </para>
/// <para>
/// MIGRATION: the cache invalidation the legacy controllers performed inline
/// (DesktopModuleController.vb L42, L47 and L76; ModuleDefinitionController.vb L38 and L59;
/// ModuleControlController.vb L116 and L141), and the clearCache overloads that guarded it, are not
/// reproduced. Caching is a separate concern behind its own abstraction.
/// </para>
/// <para>
/// Every read that returns a <see cref="ModuleDefinition"/> loads its
/// <see cref="ModuleDefinition.DesktopModule"/>, so a caller can name the owning package without a
/// further round trip. All ordering is applied in the store and every order is total, so successive
/// reads return rows in the same sequence.
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

    // ---------------------------------------------------------------------------------------
    // DesktopModule
    // ---------------------------------------------------------------------------------------

    /// <inheritdoc />
    public Task<DesktopModule?> GetDesktopModuleByIdAsync(int desktopModuleId, CancellationToken cancellationToken = default)
    {
        return _context.DesktopModules
            .FirstOrDefaultAsync(m => m.DesktopModuleId == desktopModuleId, cancellationToken);
    }

    /// <inheritdoc />
    /// <remarks>
    /// The module name is matched exactly, as the legacy procedure did. An empty name is treated as
    /// the key it is and is never folded into "no name supplied".
    /// </remarks>
    public Task<DesktopModule?> GetDesktopModuleByModuleNameAsync(string moduleName, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(moduleName);

        return _context.DesktopModules
            .FirstOrDefaultAsync(m => m.ModuleName == moduleName, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<DesktopModule>> GetDesktopModulesAsync(CancellationToken cancellationToken = default)
    {
        return await _context.DesktopModules
            .OrderBy(m => m.FriendlyName)
            .ThenBy(m => m.DesktopModuleId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Reproduces the terminal procedure (04.05.00.SqlDataProvider lines 1041 to 1064): packages
    /// belonging to the administration experience are excluded outright, and of the remainder a
    /// package qualifies when it is not premium or when a grant exists for this portal. The premium
    /// test is a disjunction evaluated by the store in one round trip, never a catalogue read
    /// followed by an in-memory filter.
    /// </remarks>
    public async Task<IReadOnlyList<DesktopModule>> GetDesktopModulesByPortalIdAsync(int portalId, CancellationToken cancellationToken = default)
    {
        return await _context.DesktopModules
            .Where(m =>
                !m.IsAdmin
                && (!m.IsPremium
                    || _context.PortalDesktopModules.Any(g =>
                        g.PortalId == portalId && g.DesktopModuleId == m.DesktopModuleId)))
            .OrderBy(m => m.FriendlyName)
            .ThenBy(m => m.DesktopModuleId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task AddDesktopModuleAsync(DesktopModule desktopModule, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(desktopModule);
        _context.DesktopModules.Add(desktopModule);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task UpdateDesktopModuleAsync(DesktopModule desktopModule, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(desktopModule);
        _context.DesktopModules.Update(desktopModule);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    /// <remarks>
    /// The row is loaded before it is staged for removal so that a package which is already absent
    /// is a no-op rather than a failure, which is how the legacy procedure behaved. The definitions
    /// and grants that reference it are removed by the cascading foreign keys the schema declares.
    /// </remarks>
    public async Task DeleteDesktopModuleAsync(int desktopModuleId, CancellationToken cancellationToken = default)
    {
        DesktopModule? existing = await _context.DesktopModules
            .FirstOrDefaultAsync(m => m.DesktopModuleId == desktopModuleId, cancellationToken)
            .ConfigureAwait(false);

        if (existing is not null)
        {
            _context.DesktopModules.Remove(existing);
        }
    }

    // ---------------------------------------------------------------------------------------
    // PortalDesktopModule
    // ---------------------------------------------------------------------------------------

    /// <inheritdoc />
    /// <remarks>
    /// The package is loaded with the grant so that a caller can name what has been granted without
    /// issuing one further read per row. The pair is unique in the schema, so at most one row is
    /// returned; the list shape is the contract's, not a suggestion that duplicates are possible.
    /// </remarks>
    public async Task<IReadOnlyList<PortalDesktopModule>> GetPortalDesktopModulesAsync(int portalId, int desktopModuleId, CancellationToken cancellationToken = default)
    {
        return await _context.PortalDesktopModules
            .Include(g => g.DesktopModule)
            .Where(g => g.PortalId == portalId && g.DesktopModuleId == desktopModuleId)
            .OrderBy(g => g.PortalDesktopModuleId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task AddPortalDesktopModuleAsync(PortalDesktopModule portalDesktopModule, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(portalDesktopModule);
        _context.PortalDesktopModules.Add(portalDesktopModule);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    /// <remarks>
    /// Named for a set because the legacy procedure was, and staged as a set: every grant matching
    /// the pair is removed. Withdrawing an entitlement that was never held is a no-op.
    /// </remarks>
    public async Task DeletePortalDesktopModulesAsync(int portalId, int desktopModuleId, CancellationToken cancellationToken = default)
    {
        List<PortalDesktopModule> existing = await _context.PortalDesktopModules
            .Where(g => g.PortalId == portalId && g.DesktopModuleId == desktopModuleId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        if (existing.Count > 0)
        {
            _context.PortalDesktopModules.RemoveRange(existing);
        }
    }

    // ---------------------------------------------------------------------------------------
    // ModuleDefinition
    // ---------------------------------------------------------------------------------------

    /// <inheritdoc />
    public async Task<IReadOnlyList<ModuleDefinition>> GetModuleDefinitionsByDesktopModuleIdAsync(int desktopModuleId, CancellationToken cancellationToken = default)
    {
        return await _context.ModuleDefinitions
            .Include(d => d.DesktopModule)
            .Where(d => d.DesktopModuleId == desktopModuleId)
            .OrderBy(d => d.FriendlyName)
            .ThenBy(d => d.ModuleDefinitionId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    /// <remarks>
    /// A premium package is one an installation grants to individual portals; a non-premium package
    /// is available to every portal without a grant. The visibility test is therefore a disjunction
    /// evaluated by the store in one round trip, not a catalogue read followed by an in-memory
    /// filter. When no portal is named the whole catalogue is returned, which is what host-level
    /// administration needs in order to decide which packages to grant.
    /// </remarks>
    public async Task<IReadOnlyList<ModuleDefinition>> GetModuleDefinitionsByPortalIdAsync(int? portalId, CancellationToken cancellationToken = default)
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
    public Task<ModuleDefinition?> GetModuleDefinitionByIdAsync(int moduleDefinitionId, CancellationToken cancellationToken = default)
    {
        return _context.ModuleDefinitions
            .Include(d => d.DesktopModule)
            .FirstOrDefaultAsync(d => d.ModuleDefinitionId == moduleDefinitionId, cancellationToken);
    }

    /// <inheritdoc />
    /// <remarks>
    /// The friendly name is matched exactly, as the legacy procedure did. An empty name is treated
    /// as the key it is.
    /// </remarks>
    public Task<ModuleDefinition?> GetModuleDefinitionByNameAsync(int desktopModuleId, string friendlyName, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(friendlyName);

        return _context.ModuleDefinitions
            .Include(d => d.DesktopModule)
            .FirstOrDefaultAsync(
                d => d.DesktopModuleId == desktopModuleId && d.FriendlyName == friendlyName,
                cancellationToken);
    }

    /// <inheritdoc />
    public Task AddModuleDefinitionAsync(ModuleDefinition moduleDefinition, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(moduleDefinition);
        _context.ModuleDefinitions.Add(moduleDefinition);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task UpdateModuleDefinitionAsync(ModuleDefinition moduleDefinition, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(moduleDefinition);
        _context.ModuleDefinitions.Update(moduleDefinition);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    /// <remarks>
    /// The row is loaded before it is staged for removal so that a definition which is already
    /// absent is a no-op. Its controls are removed by the cascading foreign key the schema declares.
    /// </remarks>
    public async Task DeleteModuleDefinitionAsync(int moduleDefinitionId, CancellationToken cancellationToken = default)
    {
        ModuleDefinition? existing = await _context.ModuleDefinitions
            .FirstOrDefaultAsync(d => d.ModuleDefinitionId == moduleDefinitionId, cancellationToken)
            .ConfigureAwait(false);

        if (existing is not null)
        {
            _context.ModuleDefinitions.Remove(existing);
        }
    }

    // ---------------------------------------------------------------------------------------
    // ModuleControl
    // ---------------------------------------------------------------------------------------

    /// <inheritdoc />
    public Task<ModuleControl?> GetModuleControlByIdAsync(int moduleControlId, CancellationToken cancellationToken = default)
    {
        return _context.ModuleControls
            .FirstOrDefaultAsync(c => c.ModuleControlId == moduleControlId, cancellationToken);
    }

    /// <inheritdoc />
    /// <remarks>
    /// The owning-definition column is nullable, so the host-level controls that belong to no
    /// definition never satisfy this filter whatever identity is supplied. The view order is
    /// nullable too, so controls carrying no explicit position sort together and are then separated
    /// by their primary key, which keeps the result deterministic without the caller re-sorting.
    /// </remarks>
    public async Task<IReadOnlyList<ModuleControl>> GetModuleControlsByDefinitionIdAsync(int moduleDefinitionId, CancellationToken cancellationToken = default)
    {
        return await _context.ModuleControls
            .Where(c => c.ModuleDefinitionId == moduleDefinitionId)
            .OrderBy(c => c.ViewOrder)
            .ThenBy(c => c.ModuleControlId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    /// <remarks>
    /// A control key is not unique by itself, so several controls of one definition may share it and
    /// be distinguished by their source. The control key is matched exactly; an empty key is treated
    /// as the key it is.
    /// </remarks>
    public async Task<IReadOnlyList<ModuleControl>> GetModuleControlsByKeyAsync(string controlKey, int moduleDefinitionId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(controlKey);

        return await _context.ModuleControls
            .Where(c => c.ModuleDefinitionId == moduleDefinitionId && c.ControlKey == controlKey)
            .OrderBy(c => c.ViewOrder)
            .ThenBy(c => c.ModuleControlId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    /// <remarks>
    /// The three values together are the natural key the schema enforces with a unique index, so at
    /// most one row can match. Both text values are matched exactly.
    /// </remarks>
    public Task<ModuleControl?> GetModuleControlByKeyAndSrcAsync(int moduleDefinitionId, string controlKey, string controlSrc, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(controlKey);
        ArgumentNullException.ThrowIfNull(controlSrc);

        return _context.ModuleControls
            .FirstOrDefaultAsync(
                c => c.ModuleDefinitionId == moduleDefinitionId
                    && c.ControlKey == controlKey
                    && c.ControlSrc == controlSrc,
                cancellationToken);
    }

    /// <inheritdoc />
    public Task AddModuleControlAsync(ModuleControl moduleControl, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(moduleControl);
        _context.ModuleControls.Add(moduleControl);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task UpdateModuleControlAsync(ModuleControl moduleControl, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(moduleControl);
        _context.ModuleControls.Update(moduleControl);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    /// <remarks>
    /// The row is loaded before it is staged for removal so that a control which is already absent
    /// is a no-op.
    /// </remarks>
    public async Task DeleteModuleControlAsync(int moduleControlId, CancellationToken cancellationToken = default)
    {
        ModuleControl? existing = await _context.ModuleControls
            .FirstOrDefaultAsync(c => c.ModuleControlId == moduleControlId, cancellationToken)
            .ConfigureAwait(false);

        if (existing is not null)
        {
            _context.ModuleControls.Remove(existing);
        }
    }
}
