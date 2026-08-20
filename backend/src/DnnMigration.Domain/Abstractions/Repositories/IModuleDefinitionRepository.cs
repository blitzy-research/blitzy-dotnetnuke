using DnnMigration.Domain.Entities;

namespace DnnMigration.Domain.Abstractions.Repositories;

/// <summary>
/// The persistence contract for the module registration catalogue: what kinds of module an installation
/// has, which portals may use them, what each publishes, and how each is reached.
/// </summary>
/// <remarks>
/// <para>
/// Four related aggregates are covered, in the order the legacy provider grouped them: <see
/// cref="DesktopModule"/> is the once-per-installation registration of a module package, <see
/// cref="PortalDesktopModule"/> is the grant entitling one portal to use one package, <see
/// cref="ModuleDefinition"/> is a unit a package publishes, and <see cref="ModuleControl"/> is a
/// user-interface entry point a definition publishes.
/// </para>
/// <para>
/// <b>No identifier value is reserved to mean absent.</b> The Portals table declares its key as IDENTITY
/// (-1, 1) and the shipped default portal is portal zero, while Modules, Tabs, Roles and RoleGroups seed at
/// zero; only ModuleDefinitions and the three other tables covered here seed at one.
/// </para>
/// </remarks>
public interface IModuleDefinitionRepository
{
    /// <summary>
    /// Returns the installed module package with the given identity, or <see langword="null"/> when the
    /// installation has no such package.
    /// </summary>
    /// <param name="desktopModuleId">The identity of the package to return.</param>
    /// <param name="cancellationToken">Propagates notification that the operation should be cancelled.</param>
    /// <returns>The matching package, or <see langword="null"/> when none matches.</returns>
    Task<DesktopModule?> GetDesktopModuleByIdAsync(int desktopModuleId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the installed module package with the given stable module name, or <see langword="null"/>
    /// when no package carries it.
    /// </summary>
    /// <param name="moduleName">
    /// The stable module name to match, as held by <see cref="DesktopModule.ModuleName"/>.
    /// </param>
    /// <param name="cancellationToken">Propagates notification that the operation should be cancelled.</param>
    /// <returns>The matching package, or <see langword="null"/> when none matches.</returns>
    Task<DesktopModule?> GetDesktopModuleByModuleNameAsync(string moduleName, CancellationToken cancellationToken = default);

    /// <summary>Returns the installed module packages that the given portal may use.</summary>
    /// <remarks>
    /// The terminal procedure (04.05.00.SqlDataProvider:L1041-L1064) also excludes every package marked as
    /// belonging to the administration experience, before the premium test is applied, and orders by
    /// friendly name.
    /// </remarks>
    /// <param name="portalId">The identity of the portal whose available packages are wanted.</param>
    /// <param name="cancellationToken">Propagates notification that the operation should be cancelled.</param>
    /// <returns>The packages available to the portal, or an empty list when none is.</returns>
    Task<IReadOnlyList<DesktopModule>> GetDesktopModulesByPortalIdAsync(int portalId, CancellationToken cancellationToken = default);

    /// <summary>Stages the insertion of a new installed module package.</summary>
    /// <remarks>
    /// The insertion is staged, not performed. The identity is assigned by the store, so <see
    /// cref="DesktopModule.DesktopModuleId"/> holds its final value only once <see
    /// cref="IUnitOfWork.SaveChangesAsync"/> has completed; read it from the entity then.
    /// </remarks>
    /// <param name="desktopModule">The package to insert.</param>
    /// <param name="cancellationToken">Propagates notification that the operation should be cancelled.</param>
    /// <returns>A task that completes once the insertion has been staged.</returns>
    Task AddDesktopModuleAsync(DesktopModule desktopModule, CancellationToken cancellationToken = default);

    /// <summary>Stages an update to an existing installed module package.</summary>
    /// <param name="desktopModule">
    /// The package to update, identified by <see cref="DesktopModule.DesktopModuleId"/>.
    /// </param>
    /// <param name="cancellationToken">Propagates notification that the operation should be cancelled.</param>
    /// <returns>A task that completes once the update has been staged.</returns>
    Task UpdateDesktopModuleAsync(DesktopModule desktopModule, CancellationToken cancellationToken = default);

    /// <summary>Stages the removal of an installed module package.</summary>
    /// <remarks>
    /// Traces to <c>DeleteDesktopModule</c>, reached through DesktopModuleController.vb:L40. The package's
    /// definitions and its per-portal grants are removed with it by the cascading foreign keys the schema
    /// already declares, so this member neither enumerates nor deletes them itself.
    /// </remarks>
    /// <param name="desktopModuleId">The identity of the package to remove.</param>
    /// <param name="cancellationToken">Propagates notification that the operation should be cancelled.</param>
    /// <returns>A task that completes once the removal has been staged.</returns>
    Task DeleteDesktopModuleAsync(int desktopModuleId, CancellationToken cancellationToken = default);

    /// <summary>Returns the grants entitling one portal to use one installed module package.</summary>
    /// <param name="portalId">The identity of the portal whose grants are wanted.</param>
    /// <param name="desktopModuleId">The identity of the package the grant must cover.</param>
    /// <param name="cancellationToken">Propagates notification that the operation should be cancelled.</param>
    /// <returns>The matching grants, or an empty list when the portal holds no such grant.</returns>
    Task<IReadOnlyList<PortalDesktopModule>> GetPortalDesktopModulesAsync(int portalId, int desktopModuleId, CancellationToken cancellationToken = default);

    /// <summary>Stages the insertion of a grant entitling a portal to use an installed module package.</summary>
    /// <param name="portalDesktopModule">The grant to insert.</param>
    /// <param name="cancellationToken">Propagates notification that the operation should be cancelled.</param>
    /// <returns>A task that completes once the insertion has been staged.</returns>
    Task AddPortalDesktopModuleAsync(PortalDesktopModule portalDesktopModule, CancellationToken cancellationToken = default);

    /// <summary>
    /// Stages the withdrawal of the grants entitling one portal to use one installed module package.
    /// </summary>
    /// <remarks>
    /// The member is named for a set because the legacy procedure was, and it returns no count. The number
    /// of rows a staged change ultimately affects is what <see cref="IUnitOfWork.SaveChangesAsync"/>
    /// reports, so reporting it here as well would invite a caller to read it before the change had been
    /// made.
    /// </remarks>
    /// <param name="portalId">The identity of the portal losing the entitlement.</param>
    /// <param name="desktopModuleId">The identity of the package the entitlement covered.</param>
    /// <param name="cancellationToken">Propagates notification that the operation should be cancelled.</param>
    /// <returns>A task that completes once the withdrawal has been staged.</returns>
    Task DeletePortalDesktopModulesAsync(int portalId, int desktopModuleId, CancellationToken cancellationToken = default);

    /// <summary>Returns the definitions published by one installed module package.</summary>
    /// <param name="desktopModuleId">
    /// The identity of the owning package, as held by <see cref="ModuleDefinition.DesktopModuleId"/>.
    /// </param>
    /// <param name="cancellationToken">Propagates notification that the operation should be cancelled.</param>
    /// <returns>The package's definitions, or an empty list when it publishes none.</returns>
    Task<IReadOnlyList<ModuleDefinition>> GetModuleDefinitionsByDesktopModuleIdAsync(int desktopModuleId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the definitions a portal may place, or the whole catalogue of definitions when no portal is
    /// named.
    /// </summary>
    /// <remarks>
    /// A null portal is not a sentinel and not a wildcard over rows; it states that the question being
    /// asked is host-level rather than portal-level, which is how a host administrator sees every
    /// definition in order to decide what to grant. Because the parameter is genuinely optional it is
    /// modelled as a nullable value, so no numeric value has to be reserved to stand for its absence.
    /// </remarks>
    /// <param name="portalId">
    /// The identity of the portal whose placeable definitions are wanted, or <see langword="null"/> for the
    /// whole catalogue.
    /// </param>
    /// <param name="cancellationToken">Propagates notification that the operation should be cancelled.</param>
    /// <returns>The matching definitions, or an empty list when none matches.</returns>
    Task<IReadOnlyList<ModuleDefinition>> GetModuleDefinitionsByPortalIdAsync(int? portalId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns one administrative definition that is instantiated in a portal, matched by friendly name.
    /// </summary>
    /// <param name="portalId">The portal whose administrative module instance must own the definition.</param>
    /// <param name="friendlyName">The administrative definition's friendly name, matched exactly.</param>
    /// <param name="cancellationToken">Propagates notification that the operation should be cancelled.</param>
    /// <returns>
    /// The matching definition, or <see langword="null"/> when the portal has no live instance of an
    /// administrative package publishing that name.
    /// </returns>
    Task<ModuleDefinition?> GetAdministrativeDefinitionByFriendlyNameAsync(
        int portalId,
        string friendlyName,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the definition with the given identity, or <see langword="null"/> when no definition carries
    /// it.
    /// </summary>
    /// <param name="moduleDefinitionId">
    /// The identity of the definition to return, as held by <see
    /// cref="ModuleDefinition.ModuleDefinitionId"/>.
    /// </param>
    /// <param name="cancellationToken">Propagates notification that the operation should be cancelled.</param>
    /// <returns>The matching definition, or <see langword="null"/> when none matches.</returns>
    Task<ModuleDefinition?> GetModuleDefinitionByIdAsync(int moduleDefinitionId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the definition published by one package under one friendly name, or <see langword="null"/>
    /// when the package publishes no such definition.
    /// </summary>
    /// <param name="desktopModuleId">The identity of the owning package.</param>
    /// <param name="friendlyName">
    /// The friendly name to match, as held by <see cref="ModuleDefinition.FriendlyName"/>.
    /// </param>
    /// <param name="cancellationToken">Propagates notification that the operation should be cancelled.</param>
    /// <returns>The matching definition, or <see langword="null"/> when none matches.</returns>
    Task<ModuleDefinition?> GetModuleDefinitionByNameAsync(int desktopModuleId, string friendlyName, CancellationToken cancellationToken = default);

    /// <summary>Stages the insertion of a new definition.</summary>
    /// <param name="moduleDefinition">The definition to insert.</param>
    /// <param name="cancellationToken">Propagates notification that the operation should be cancelled.</param>
    /// <returns>A task that completes once the insertion has been staged.</returns>
    Task AddModuleDefinitionAsync(ModuleDefinition moduleDefinition, CancellationToken cancellationToken = default);

    /// <summary>Stages an update to an existing definition.</summary>
    /// <param name="moduleDefinition">
    /// The definition to update, identified by <see cref="ModuleDefinition.ModuleDefinitionId"/>.
    /// </param>
    /// <param name="cancellationToken">Propagates notification that the operation should be cancelled.</param>
    /// <returns>A task that completes once the update has been staged.</returns>
    Task UpdateModuleDefinitionAsync(ModuleDefinition moduleDefinition, CancellationToken cancellationToken = default);

    /// <summary>Stages the removal of a definition.</summary>
    /// <remarks>
    /// Traces to <c>DeleteModuleDefinition</c>, reached through ModuleDefinitionController.vb:L36. The
    /// definition's controls are removed with it by the cascading foreign key the schema already declares,
    /// so this member neither enumerates nor deletes them itself.
    /// </remarks>
    /// <param name="moduleDefinitionId">The identity of the definition to remove.</param>
    /// <param name="cancellationToken">Propagates notification that the operation should be cancelled.</param>
    /// <returns>A task that completes once the removal has been staged.</returns>
    Task DeleteModuleDefinitionAsync(int moduleDefinitionId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the control row with the given identity, or <see langword="null"/> when no control carries
    /// it.
    /// </summary>
    /// <param name="moduleControlId">
    /// The identity of the control to return, as held by <see cref="ModuleControl.ModuleControlId"/>.
    /// </param>
    /// <param name="cancellationToken">Propagates notification that the operation should be cancelled.</param>
    /// <returns>The matching control, or <see langword="null"/> when none matches.</returns>
    Task<ModuleControl?> GetModuleControlByIdAsync(int moduleControlId, CancellationToken cancellationToken = default);

    /// <summary>Returns the control rows belonging to one definition.</summary>
    /// <remarks>
    /// <see cref="ModuleControl.ModuleDefinitionId"/> is nullable in the schema, because an installation
    /// also holds host-level controls that belong to no definition. Those rows are therefore never returned
    /// by this member, whatever identity is supplied - a definition's controls and the definition-less
    /// controls are disjoint sets.
    /// </remarks>
    /// <param name="moduleDefinitionId">The identity of the owning definition.</param>
    /// <param name="cancellationToken">Propagates notification that the operation should be cancelled.</param>
    /// <returns>The definition's controls, or an empty list when it has none.</returns>
    Task<IReadOnlyList<ModuleControl>> GetModuleControlsByDefinitionIdAsync(int moduleDefinitionId, CancellationToken cancellationToken = default);

    /// <summary>Returns the control rows of one definition that carry a given control key.</summary>
    /// <remarks>
    /// A control key is not unique by itself, which is why this member returns a list where the
    /// key-and-source lookup below returns at most one row: a definition may publish several controls under
    /// one key, distinguished by their source.
    /// </remarks>
    /// <param name="controlKey">The control key to match, as held by <see cref="ModuleControl.ControlKey"/>.</param>
    /// <param name="moduleDefinitionId">The identity of the owning definition.</param>
    /// <param name="cancellationToken">Propagates notification that the operation should be cancelled.</param>
    /// <returns>The matching controls, or an empty list when none matches.</returns>
    Task<IReadOnlyList<ModuleControl>> GetModuleControlsByKeyAsync(string controlKey, int moduleDefinitionId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the single control row identified by its definition, its control key and its source, or <see
    /// langword="null"/> when no control matches all three.
    /// </summary>
    /// <param name="moduleDefinitionId">The identity of the owning definition.</param>
    /// <param name="controlKey">The control key to match, as held by <see cref="ModuleControl.ControlKey"/>.</param>
    /// <param name="controlSrc">
    /// The control source to match, as held by <see cref="ModuleControl.ControlSrc"/>.
    /// </param>
    /// <param name="cancellationToken">Propagates notification that the operation should be cancelled.</param>
    /// <returns>The matching control, or <see langword="null"/> when none matches.</returns>
    Task<ModuleControl?> GetModuleControlByKeyAndSrcAsync(int moduleDefinitionId, string controlKey, string controlSrc, CancellationToken cancellationToken = default);

    /// <summary>Stages the insertion of a new control row.</summary>
    /// <param name="moduleControl">The control to insert.</param>
    /// <param name="cancellationToken">Propagates notification that the operation should be cancelled.</param>
    /// <returns>A task that completes once the insertion has been staged.</returns>
    Task AddModuleControlAsync(ModuleControl moduleControl, CancellationToken cancellationToken = default);

    /// <summary>Stages an update to an existing control row.</summary>
    /// <param name="moduleControl">
    /// The control to update, identified by <see cref="ModuleControl.ModuleControlId"/>.
    /// </param>
    /// <param name="cancellationToken">Propagates notification that the operation should be cancelled.</param>
    /// <returns>A task that completes once the update has been staged.</returns>
    Task UpdateModuleControlAsync(ModuleControl moduleControl, CancellationToken cancellationToken = default);

    /// <summary>Stages the removal of a control row.</summary>
    /// <param name="moduleControlId">The identity of the control to remove.</param>
    /// <param name="cancellationToken">Propagates notification that the operation should be cancelled.</param>
    /// <returns>A task that completes once the removal has been staged.</returns>
    Task DeleteModuleControlAsync(int moduleControlId, CancellationToken cancellationToken = default);
}
