using DnnMigration.Domain.Entities;

namespace DnnMigration.Domain.Abstractions.Repositories;

/// <summary>
/// Reads the module-definition catalogue: desktop modules, their definitions, their controls and the
/// per-portal grants that make a desktop module available to a portal.
/// </summary>
/// <remarks>
/// MIGRATION: replaces the read surface of <c>DesktopModuleController.vb</c> and
/// <c>ModuleDefinitionController.vb</c>. The catalogue is installation-time data, so this contract is
/// read-only apart from the per-portal grant, which portal administration toggles.
/// </remarks>
public interface IModuleDefinitionRepository
{
    /// <summary>Returns the module definitions available to a portal.</summary>
    /// <param name="portalId">Portal identifier, or <see langword="null"/> for the whole catalogue.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <remarks>
    /// When <paramref name="portalId"/> is supplied, the result is restricted to definitions whose
    /// desktop module is either premium-granted to that portal through
    /// <see cref="PortalDesktopModule"/> or not premium at all.
    /// </remarks>
    Task<IReadOnlyList<ModuleDefinition>> ListAsync(int? portalId, CancellationToken cancellationToken = default);

    /// <summary>Returns one module definition by key, or <see langword="null"/>.</summary>
    /// <param name="moduleDefinitionId">Module definition identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<ModuleDefinition?> GetAsync(int moduleDefinitionId, CancellationToken cancellationToken = default);

    /// <summary>Returns the controls of one module definition, in view order.</summary>
    /// <param name="moduleDefinitionId">Module definition identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<IReadOnlyList<ModuleControl>> ListControlsAsync(int moduleDefinitionId, CancellationToken cancellationToken = default);

    /// <summary>Returns one desktop module by key, or <see langword="null"/>.</summary>
    /// <param name="desktopModuleId">Desktop module identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<DesktopModule?> GetDesktopModuleAsync(int desktopModuleId, CancellationToken cancellationToken = default);

    /// <summary>Returns the desktop modules granted to a portal.</summary>
    /// <param name="portalId">Portal identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<IReadOnlyList<PortalDesktopModule>> ListPortalGrantsAsync(int portalId, CancellationToken cancellationToken = default);

    /// <summary>Stages a new per-portal grant for insertion.</summary>
    /// <param name="grant">The grant to insert.</param>
    void AddPortalGrant(PortalDesktopModule grant);

    /// <summary>Stages a per-portal grant for deletion.</summary>
    /// <param name="grant">The grant to delete.</param>
    void RemovePortalGrant(PortalDesktopModule grant);
}
