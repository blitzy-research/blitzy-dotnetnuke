using DnnMigration.Domain.Entities;

namespace DnnMigration.Domain.Abstractions.Repositories;

// MIGRATION: This contract realises the module-definition slice of the legacy data surface - Library/Components/Providers/Data/DataProvider.vb lines 156 to 183, twenty-four members among the 269 MustOverride members declared on one 397-line abstract class. Only the module registration catalogue appears here. The god-interface is deliberately not reproduced: each aggregate owns a narrow contract of its own, so a consumer depends on the catalogue surface alone rather than on every table in the installation.
// MIGRATION: The legacy accessor was a reflection-resolved static singleton - a private provider-type constant, a shared constructor and a shadowed Instance function at DataProvider.vb lines 29 to 50 - which bound every caller to a configuration-driven lookup resolved at first use, and ModuleControlController.vb line 34 cached that singleton in a Private Shared field of its own. Implementations of this interface are supplied by constructor injection instead, so the dependency is declared, substitutable and verifiable at compile time.
// MIGRATION: GetDesktopModuleByFriendlyName (DataProvider.vb:L158) is omitted. Both legacy wrappers - DesktopModuleController.GetDesktopModuleByFriendlyName (L80) and GetDesktopModuleByName (L85) - carry <Obsolete("As the FriendlyName is not guaranteed to be the same as when the module is created, this method has been replaced by GetDesktopModuleByModuleName(moduleName)")>, and L85 delegates to the same provider member as L80, making it an exact duplicate. Only GetDesktopModuleByModuleName (L159) is surfaced, as the legacy source itself directs.
// MIGRATION: the legacy clearCache overloads (DesktopModuleController.vb:L74 Friend UpdateDesktopModule, ModuleDefinitionController.vb:L57, ModuleControlController.vb:L139) and the DataCache.ClearModuleCache/ClearPortalCache calls at DesktopModuleController.vb:L42, L47 and L76 are pure cache management; caching moves to ICacheService in Infrastructure and no cache parameter appears on this contract.
// MIGRATION: legacy AddDesktopModule (12 positional arguments, DataProvider.vb:L162), UpdateDesktopModule (13, L163), AddModuleControl (9, L181) and UpdateModuleControl (10, L182) are replaced by entity-oriented calls.
// MIGRATION: DesktopModuleController.vb hydrated rows through CBO.FillObject (L51, L55, L81, L86) and CBO.FillCollection (L59, L63, L67), returning ArrayList. CBO.vb is 729 lines of reflection-based forward-only-reader hydration and produces no target file; the object-relational mapper's materialiser replaces it and multi-row reads return a read-only list. ModuleControlController.vb carried a second, hand-written hydration path of its own - a collection filler and two overloads whose ten sentinel-translating assignments run from L89 to L98 - and it is deleted on the same grounds.
// MIGRATION: the legacy data layer passed several of these arguments through the sentinel-to-database-null converter at Null.vb:L155, so a sentinel argument reached the store as SQL NULL and the procedure then took an IS NULL branch. Measured wrappers: GetPortalDesktopModules and DeletePortalDesktopModules (SqlDataProvider.vb:L799, L805, both arguments), GetModuleControls (L832), GetModuleControlsByKey (L834, both arguments) and GetModuleControlByKeyAndSrc (L837, all three). The terminal procedures give that NULL two different meanings: GetPortalDesktopModules (02.02.02.SqlDataProvider:L3161-L3162) treats it as a match-all wildcard, whereas GetModuleControlsByKey (02.02.00.SqlDataProvider:L544-L545) treats it as a match-the-null-row test that locates the default control. Neither meaning is reproduced by a sentinel on this contract: identifiers and lookup keys are plain, required values here, absence is expressed by a null entity or an empty list, and a caller that needs the wildcard or the null-row reading asks for it explicitly through a member of its own. Recorded in MIGRATION_NOTES.md rather than silently absorbed.
// MIGRATION: GetModuleControlsByKey additionally carries a fixed exclusion in the store - 02.02.00.SqlDataProvider:L546 excludes one reserved negative control ordinal - and the terminal read at 04.05.00.SqlDataProvider:L1491 identifies a definition's default control by a null control key. Both are properties of the legacy query rather than of this contract, so they are documented for the implementer and are not expressed as parameters.
// MIGRATION: ModuleControlController.vb:L95 cast the persisted control ordinal to the access-level enumeration declared in Library/Components/Security/PortalSecurity.vb, a file this migration excludes. The ordinal travels untranslated as a plain integer on ModuleControl.ControlType, and DesktopModule.SupportedFeatures likewise stays a plain integer bit field. Neither is promoted to an enumeration, and no member of this contract interprets either one - that reading belongs to the Application layer.
// MIGRATION: aggregate ownership is split, and the split follows the legacy provider's own grouping. This contract owns the catalogue block at DataProvider.vb lines 156 to 183 - DesktopModule, PortalDesktopModule, ModuleDefinition and ModuleControl. The separate module block at lines 125 to 154 - Module, TabModule and their two settings tables - belongs to IModuleRepository, so no member here reads or writes a placed module instance or any settings row, and the settings tables are not split into repositories of their own.

/// <summary>
/// The persistence contract for the module registration catalogue: what kinds of module an
/// installation has, which portals may use them, what each publishes, and how each is reached.
/// </summary>
/// <remarks>
/// <para>
/// Four related aggregates are covered, in the order the legacy provider grouped them:
/// <see cref="DesktopModule"/> is the once-per-installation registration of a module package,
/// <see cref="PortalDesktopModule"/> is the grant entitling one portal to use one package,
/// <see cref="ModuleDefinition"/> is a unit a package publishes, and <see cref="ModuleControl"/>
/// is a user-interface entry point a definition publishes.
/// </para>
/// <para>
/// <b>This is the catalogue, not the placements.</b> A module instance sitting on a page, and the
/// settings recorded against that instance, belong to <see cref="IModuleRepository"/>. The division
/// is not a matter of taste: it reproduces the grouping the legacy provider itself used, and it is
/// what keeps either contract from becoming a second god-interface. No member here reads or writes
/// a placed instance.
/// </para>
/// <para>
/// <b>This is the only route from the application layer to the persisted catalogue.</b> It exposes
/// no persistence session, no transaction handle, no deferred query surface and no provider type,
/// so a consumer cannot name - and therefore cannot depend upon - the technology that stores a
/// module package. Every read returns a materialised result: a nullable entity, or a read-only
/// list. Nothing deferred crosses this boundary, so no query can be enumerated after the session
/// that produced it has gone.
/// </para>
/// <para>
/// <b>Writes stage; they do not commit.</b> Each write member records an intention against the
/// current unit of work and returns once it is recorded. Nothing is durable until
/// <see cref="IUnitOfWork.SaveChangesAsync"/> is called. That is what allows a package, its
/// definitions, its controls and its per-portal grants to be written as one indivisible batch,
/// and it is why no write member returns a generated key: under an object-relational mapper the
/// identity is assigned by the store during the commit, so a member that returned one would have
/// to commit on the caller's behalf and would destroy the batch. Read the entity's own identity
/// property after the commit instead. The legacy code had already stopped depending on those
/// return values in places - DesktopModuleController.vb:L36 wraps a provider function that yields
/// an identity in a Sub that discards it.
/// </para>
/// <para>
/// <b>No identifier value is reserved to mean absent.</b> The Portals table declares its key as
/// IDENTITY (-1, 1) and the shipped default portal is portal zero, while Modules, Tabs, Roles and
/// RoleGroups seed at zero; only ModuleDefinitions and the three other tables covered here seed at
/// one. The legacy null helper nevertheless set its integer sentinel to minus one and reported that
/// value as absent, so it could not tell a real row from a missing one. No member of this contract
/// treats any numeric value as absent, and none applies a range or sign constraint to an
/// identifier. Absence is expressed only by a null entity or an empty list.
/// </para>
/// <para>
/// <b>Text keys are required, and an empty key is a real key.</b> The legacy string sentinel was
/// the empty string rather than null, so an empty value was legally representable in the store and
/// remains distinguishable from a missing one. An implementer must not silently treat an empty
/// lookup key as though no key had been supplied.
/// </para>
/// <para>
/// Identifiers cross this boundary as plain framework integers and strings rather than as
/// identifier value objects. The value objects exist for application-facing boundaries; the
/// entities declare plain scalars for their own identity, and the conversion between the two
/// belongs to the persistence configuration in the outer layer.
/// </para>
/// <para>
/// <b>Registration is modelled; activation is not.</b> <see cref="DesktopModule.BusinessControllerClass"/>
/// and <see cref="ModuleControl.ControlSrc"/> are inert mapped text. Nothing on this contract
/// resolves, loads, instantiates or probes for anything they name, and no member parses an
/// installation manifest or interprets <see cref="DesktopModule.SupportedFeatures"/>. Module
/// lifecycle orchestration and the resolution of a package's own behaviour belong to services in
/// the Application and Infrastructure layers, which draw from a closed, injected set.
/// </para>
/// </remarks>
public interface IModuleDefinitionRepository
{
    // ---------------------------------------------------------------------------------------
    // DesktopModule - the installed module package. DataProvider.vb lines 157 to 164.
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// Returns the installed module package with the given identity, or <see langword="null"/>
    /// when the installation has no such package.
    /// </summary>
    /// <remarks>
    /// Traces to <c>GetDesktopModule</c> (DataProvider.vb:L157), whose forward-only result the
    /// legacy wrapper at DesktopModuleController.vb:L50 turned into a single object through the
    /// reflection hydrator. A missing package is reported by the null return rather than by an
    /// empty object, which is what the legacy hydrator produced and callers could not distinguish
    /// from a package whose every column happened to hold a sentinel.
    /// </remarks>
    /// <param name="desktopModuleId">The identity of the package to return.</param>
    /// <param name="cancellationToken">Propagates notification that the operation should be cancelled.</param>
    /// <returns>The matching package, or <see langword="null"/> when none matches.</returns>
    Task<DesktopModule?> GetDesktopModuleByIdAsync(int desktopModuleId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the installed module package with the given stable module name, or
    /// <see langword="null"/> when no package carries it.
    /// </summary>
    /// <remarks>
    /// Traces to <c>GetDesktopModuleByModuleName</c> (DataProvider.vb:L159), reached through
    /// DesktopModuleController.vb:L54. This is the only name-based package lookup on the contract,
    /// and it is deliberately the one the legacy source nominated: the friendly-name lookup beside
    /// it was marked obsolete in favour of this member, because a friendly name is not guaranteed
    /// to be the value a package was created with. Match <see cref="DesktopModule.ModuleName"/>
    /// exactly; the legacy procedure performed no widening of the argument.
    /// </remarks>
    /// <param name="moduleName">
    /// The stable module name to match, as held by <see cref="DesktopModule.ModuleName"/>. An
    /// empty value is a legitimate key and must be matched as given.
    /// </param>
    /// <param name="cancellationToken">Propagates notification that the operation should be cancelled.</param>
    /// <returns>The matching package, or <see langword="null"/> when none matches.</returns>
    Task<DesktopModule?> GetDesktopModuleByModuleNameAsync(string moduleName, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns every installed module package, which is the catalogue a host administrator works
    /// from when deciding what to grant.
    /// </summary>
    /// <remarks>
    /// Traces to <c>GetDesktopModules</c> (DataProvider.vb:L160), the one member of this block
    /// that took no argument, and supersedes the untyped non-generic list returned by
    /// DesktopModuleController.vb:L58. An installation with no packages yields an empty list, never
    /// <see langword="null"/>.
    /// </remarks>
    /// <param name="cancellationToken">Propagates notification that the operation should be cancelled.</param>
    /// <returns>Every installed package, or an empty list when the installation has none.</returns>
    Task<IReadOnlyList<DesktopModule>> GetDesktopModulesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the installed module packages that the given portal may use.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Traces to <c>GetDesktopModulesByPortal</c> (DataProvider.vb:L161), reached through
    /// DesktopModuleController.vb:L62, and supersedes the untyped non-generic list it returned.
    /// </para>
    /// <para>
    /// Availability is a disjunction, not a lookup: a package that is not premium is available to
    /// every portal without any grant at all, while a premium package is available only where a
    /// <see cref="PortalDesktopModule"/> row exists for the pair. That is the rule the legacy
    /// procedure expressed, and an implementer must evaluate it in the store rather than by
    /// reading the whole catalogue and filtering in memory.
    /// </para>
    /// <para>
    /// The terminal procedure (04.05.00.SqlDataProvider:L1041-L1064) also excludes every package
    /// marked as belonging to the administration experience, before the premium test is applied,
    /// and orders by friendly name. Both are part of the ported rule rather than presentation
    /// preferences: this member answers what a portal may place as content, so a package flagged
    /// administrative is not a candidate however it is granted.
    /// </para>
    /// </remarks>
    /// <param name="portalId">
    /// The identity of the portal whose available packages are wanted. Every integer is a
    /// legitimate portal identity here, including minus one and zero.
    /// </param>
    /// <param name="cancellationToken">Propagates notification that the operation should be cancelled.</param>
    /// <returns>The packages available to the portal, or an empty list when none is.</returns>
    Task<IReadOnlyList<DesktopModule>> GetDesktopModulesByPortalIdAsync(int portalId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Stages the insertion of a new installed module package.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Traces to <c>AddDesktopModule</c> (DataProvider.vb:L162), which took twelve positional
    /// arguments and returned the generated identity. The whole argument list collapses onto the
    /// entity, so a column added to the package in future changes the entity rather than this
    /// signature, and no caller has to remember an order.
    /// </para>
    /// <para>
    /// The insertion is staged, not performed. The identity is assigned by the store, so
    /// <see cref="DesktopModule.DesktopModuleId"/> holds its final value only once
    /// <see cref="IUnitOfWork.SaveChangesAsync"/> has completed; read it from the entity then.
    /// </para>
    /// </remarks>
    /// <param name="desktopModule">The package to insert.</param>
    /// <param name="cancellationToken">Propagates notification that the operation should be cancelled.</param>
    /// <returns>A task that completes once the insertion has been staged.</returns>
    Task AddDesktopModuleAsync(DesktopModule desktopModule, CancellationToken cancellationToken = default);

    /// <summary>
    /// Stages an update to an existing installed module package.
    /// </summary>
    /// <remarks>
    /// Traces to <c>UpdateDesktopModule</c> (DataProvider.vb:L163), which took thirteen positional
    /// arguments - the twelve of the insertion plus the identity - and which the legacy code
    /// reached through a pair of overloads whose only difference was a cache flag. Both the
    /// argument list and the flag are gone: the entity carries the new state and its own identity,
    /// and cache invalidation is a separate concern behind its own abstraction.
    /// </remarks>
    /// <param name="desktopModule">
    /// The package to update, identified by <see cref="DesktopModule.DesktopModuleId"/>.
    /// </param>
    /// <param name="cancellationToken">Propagates notification that the operation should be cancelled.</param>
    /// <returns>A task that completes once the update has been staged.</returns>
    Task UpdateDesktopModuleAsync(DesktopModule desktopModule, CancellationToken cancellationToken = default);

    /// <summary>
    /// Stages the removal of an installed module package.
    /// </summary>
    /// <remarks>
    /// Traces to <c>DeleteDesktopModule</c> (DataProvider.vb:L164), reached through
    /// DesktopModuleController.vb:L40. The package's definitions and its per-portal grants are
    /// removed with it by the cascading foreign keys the schema already declares, so this member
    /// neither enumerates nor deletes them itself. Removing a package that does not exist is not
    /// an error the caller has to guard against.
    /// </remarks>
    /// <param name="desktopModuleId">The identity of the package to remove.</param>
    /// <param name="cancellationToken">Propagates notification that the operation should be cancelled.</param>
    /// <returns>A task that completes once the removal has been staged.</returns>
    Task DeleteDesktopModuleAsync(int desktopModuleId, CancellationToken cancellationToken = default);

    // ---------------------------------------------------------------------------------------
    // PortalDesktopModule - the per-portal grant. DataProvider.vb lines 166 to 168.
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// Returns the grants entitling one portal to use one installed module package.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Traces to <c>GetPortalDesktopModules</c> (DataProvider.vb:L166), reached through
    /// DesktopModuleController.vb:L66, and supersedes the untyped non-generic list it returned.
    /// Both arguments narrow the result, and the pair is unique in the schema, so a match yields
    /// at most one grant; the member nevertheless returns a list because that is the shape the
    /// legacy read produced and because the uniqueness is the database's guarantee to make rather
    /// than this signature's to assert.
    /// </para>
    /// <para>
    /// The legacy procedure additionally projected the portal name and the package's friendly name
    /// alongside the row. Those are join projections rather than columns of the grant, so they are
    /// absent from <see cref="PortalDesktopModule"/>; an implementer that wants to spare a caller
    /// a further read should load the grant's own references instead.
    /// </para>
    /// </remarks>
    /// <param name="portalId">
    /// The identity of the portal whose grants are wanted. Every integer is a legitimate portal
    /// identity here, including minus one and zero.
    /// </param>
    /// <param name="desktopModuleId">The identity of the package the grant must cover.</param>
    /// <param name="cancellationToken">Propagates notification that the operation should be cancelled.</param>
    /// <returns>The matching grants, or an empty list when the portal holds no such grant.</returns>
    Task<IReadOnlyList<PortalDesktopModule>> GetPortalDesktopModulesAsync(int portalId, int desktopModuleId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Stages the insertion of a grant entitling a portal to use an installed module package.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Traces to <c>AddPortalDesktopModule</c> (DataProvider.vb:L167). The grant carries no state
    /// beyond the pair it joins, so its existence is the fact being recorded and
    /// <see cref="PortalDesktopModule.PortalId"/> together with
    /// <see cref="PortalDesktopModule.DesktopModuleId"/> is all the entity needs to carry.
    /// </para>
    /// <para>
    /// The insertion is staged, not performed, and nothing is returned. The provider function this
    /// replaces did yield the generated identity, but the legacy wrapper immediately discarded it -
    /// DesktopModuleController.vb:L36 is a Sub - so the key was already surplus to the operation.
    /// <see cref="PortalDesktopModule.PortalDesktopModuleId"/> holds its final value once
    /// <see cref="IUnitOfWork.SaveChangesAsync"/> has completed.
    /// </para>
    /// </remarks>
    /// <param name="portalDesktopModule">The grant to insert.</param>
    /// <param name="cancellationToken">Propagates notification that the operation should be cancelled.</param>
    /// <returns>A task that completes once the insertion has been staged.</returns>
    Task AddPortalDesktopModuleAsync(PortalDesktopModule portalDesktopModule, CancellationToken cancellationToken = default);

    /// <summary>
    /// Stages the withdrawal of the grants entitling one portal to use one installed module
    /// package.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Traces to <c>DeletePortalDesktopModules</c> (DataProvider.vb:L168), reached through
    /// DesktopModuleController.vb:L45. Withdrawing an entitlement means removing the row; there is
    /// no field on the grant to blank instead.
    /// </para>
    /// <para>
    /// The member is named for a set because the legacy procedure was, and it returns no count.
    /// The number of rows a staged change ultimately affects is what
    /// <see cref="IUnitOfWork.SaveChangesAsync"/> reports, so reporting it here as well would
    /// invite a caller to read it before the change had been made.
    /// </para>
    /// </remarks>
    /// <param name="portalId">The identity of the portal losing the entitlement.</param>
    /// <param name="desktopModuleId">The identity of the package the entitlement covered.</param>
    /// <param name="cancellationToken">Propagates notification that the operation should be cancelled.</param>
    /// <returns>A task that completes once the withdrawal has been staged.</returns>
    Task DeletePortalDesktopModulesAsync(int portalId, int desktopModuleId, CancellationToken cancellationToken = default);

    // ---------------------------------------------------------------------------------------
    // ModuleDefinition - what a package publishes. DataProvider.vb lines 170 to 175.
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// Returns the definitions published by one installed module package.
    /// </summary>
    /// <remarks>
    /// Traces to <c>GetModuleDefinitions</c> (DataProvider.vb:L170), reached through
    /// ModuleDefinitionController.vb:L49, and supersedes the untyped non-generic list it returned.
    /// A package that publishes nothing yields an empty list, which is a legitimate state rather
    /// than an error: the owning package exists independently of the definitions it may later gain.
    /// </remarks>
    /// <param name="desktopModuleId">
    /// The identity of the owning package, as held by <see cref="ModuleDefinition.DesktopModuleId"/>.
    /// </param>
    /// <param name="cancellationToken">Propagates notification that the operation should be cancelled.</param>
    /// <returns>The package's definitions, or an empty list when it publishes none.</returns>
    Task<IReadOnlyList<ModuleDefinition>> GetModuleDefinitionsByDesktopModuleIdAsync(int desktopModuleId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the definitions a portal may place, or the whole catalogue of definitions when no
    /// portal is named.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This member has no single counterpart in the legacy provider block, and it is the one
    /// deliberate addition to this contract. The legacy screens assembled the same result as an
    /// N+1: <c>GetDesktopModulesByPortal</c> (DataProvider.vb:L161) to find the packages a portal
    /// could use, then <c>GetModuleDefinitions</c> (L170) once per package to find what each
    /// published. Expressing it as one member lets an implementer satisfy it in a single round
    /// trip, which is a consequence of the target architecture rather than an optimisation of any
    /// ported rule - the rule itself is unchanged.
    /// </para>
    /// <para>
    /// Availability carries the same disjunction as
    /// <see cref="GetDesktopModulesByPortalIdAsync"/>: a definition is placeable when its package
    /// is not premium, or when a <see cref="PortalDesktopModule"/> grant exists for the pair.
    /// </para>
    /// <para>
    /// A null portal is not a sentinel and not a wildcard over rows; it states that the question
    /// being asked is host-level rather than portal-level, which is how a host administrator sees
    /// every definition in order to decide what to grant. Because the parameter is genuinely
    /// optional it is modelled as a nullable value, so no numeric value has to be reserved to
    /// stand for its absence.
    /// </para>
    /// </remarks>
    /// <param name="portalId">
    /// The identity of the portal whose placeable definitions are wanted, or
    /// <see langword="null"/> for the whole catalogue. Every integer is a legitimate portal
    /// identity here, including minus one and zero.
    /// </param>
    /// <param name="cancellationToken">Propagates notification that the operation should be cancelled.</param>
    /// <returns>The matching definitions, or an empty list when none matches.</returns>
    Task<IReadOnlyList<ModuleDefinition>> GetModuleDefinitionsByPortalIdAsync(int? portalId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the definition with the given identity, or <see langword="null"/> when no definition
    /// carries it.
    /// </summary>
    /// <remarks>
    /// Traces to <c>GetModuleDefinition</c> (DataProvider.vb:L171), reached through
    /// ModuleDefinitionController.vb:L41.
    /// </remarks>
    /// <param name="moduleDefinitionId">
    /// The identity of the definition to return, as held by
    /// <see cref="ModuleDefinition.ModuleDefinitionId"/>.
    /// </param>
    /// <param name="cancellationToken">Propagates notification that the operation should be cancelled.</param>
    /// <returns>The matching definition, or <see langword="null"/> when none matches.</returns>
    Task<ModuleDefinition?> GetModuleDefinitionByIdAsync(int moduleDefinitionId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the definition published by one package under one friendly name, or
    /// <see langword="null"/> when the package publishes no such definition.
    /// </summary>
    /// <remarks>
    /// Traces to <c>GetModuleDefinitionByName</c> (DataProvider.vb:L172), reached through
    /// ModuleDefinitionController.vb:L45. Unlike the package friendly name - whose uniqueness the
    /// upgrade chain dropped, and whose lookup the legacy source consequently marked obsolete - a
    /// definition's friendly name is still unique in the terminal schema, so this pairing is a
    /// sound key and is the one the legacy installer used to decide between inserting a definition
    /// and updating one.
    /// </remarks>
    /// <param name="desktopModuleId">The identity of the owning package.</param>
    /// <param name="friendlyName">
    /// The friendly name to match, as held by <see cref="ModuleDefinition.FriendlyName"/>. An empty
    /// value is a legitimate key and must be matched as given.
    /// </param>
    /// <param name="cancellationToken">Propagates notification that the operation should be cancelled.</param>
    /// <returns>The matching definition, or <see langword="null"/> when none matches.</returns>
    Task<ModuleDefinition?> GetModuleDefinitionByNameAsync(int desktopModuleId, string friendlyName, CancellationToken cancellationToken = default);

    /// <summary>
    /// Stages the insertion of a new definition.
    /// </summary>
    /// <remarks>
    /// Traces to <c>AddModuleDefinition</c> (DataProvider.vb:L173), reached through
    /// ModuleDefinitionController.vb:L32, which returned the generated identity. The insertion is
    /// staged, not performed, so <see cref="ModuleDefinition.ModuleDefinitionId"/> holds its final
    /// value only once <see cref="IUnitOfWork.SaveChangesAsync"/> has completed. The owning package
    /// is fixed at insertion: the legacy update procedure did not accept it, and neither does the
    /// update member below.
    /// </remarks>
    /// <param name="moduleDefinition">The definition to insert.</param>
    /// <param name="cancellationToken">Propagates notification that the operation should be cancelled.</param>
    /// <returns>A task that completes once the insertion has been staged.</returns>
    Task AddModuleDefinitionAsync(ModuleDefinition moduleDefinition, CancellationToken cancellationToken = default);

    /// <summary>
    /// Stages an update to an existing definition.
    /// </summary>
    /// <remarks>
    /// Traces to <c>UpdateModuleDefinition</c> (DataProvider.vb:L175), which the legacy code
    /// reached through a pair of overloads at ModuleDefinitionController.vb:L53 and L57 whose only
    /// difference was a cache flag; the flag is not surfaced. The legacy procedure rewrote only the
    /// friendly name and <see cref="ModuleDefinition.DefaultCacheTime"/>, so an implementer must
    /// not take this member as licence to move a definition between packages.
    /// </remarks>
    /// <param name="moduleDefinition">
    /// The definition to update, identified by <see cref="ModuleDefinition.ModuleDefinitionId"/>.
    /// </param>
    /// <param name="cancellationToken">Propagates notification that the operation should be cancelled.</param>
    /// <returns>A task that completes once the update has been staged.</returns>
    Task UpdateModuleDefinitionAsync(ModuleDefinition moduleDefinition, CancellationToken cancellationToken = default);

    /// <summary>
    /// Stages the removal of a definition.
    /// </summary>
    /// <remarks>
    /// Traces to <c>DeleteModuleDefinition</c> (DataProvider.vb:L174), reached through
    /// ModuleDefinitionController.vb:L36. The definition's controls are removed with it by the
    /// cascading foreign key the schema already declares, so this member neither enumerates nor
    /// deletes them itself.
    /// </remarks>
    /// <param name="moduleDefinitionId">The identity of the definition to remove.</param>
    /// <param name="cancellationToken">Propagates notification that the operation should be cancelled.</param>
    /// <returns>A task that completes once the removal has been staged.</returns>
    Task DeleteModuleDefinitionAsync(int moduleDefinitionId, CancellationToken cancellationToken = default);

    // ---------------------------------------------------------------------------------------
    // ModuleControl - how a definition is reached. DataProvider.vb lines 177 to 183.
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// Returns the control row with the given identity, or <see langword="null"/> when no control
    /// carries it.
    /// </summary>
    /// <remarks>
    /// Traces to <c>GetModuleControl</c> (DataProvider.vb:L177), reached through
    /// ModuleControlController.vb:L119, whose hand-written hydration path this replaces.
    /// </remarks>
    /// <param name="moduleControlId">
    /// The identity of the control to return, as held by
    /// <see cref="ModuleControl.ModuleControlId"/>.
    /// </param>
    /// <param name="cancellationToken">Propagates notification that the operation should be cancelled.</param>
    /// <returns>The matching control, or <see langword="null"/> when none matches.</returns>
    Task<ModuleControl?> GetModuleControlByIdAsync(int moduleControlId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the control rows belonging to one definition.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Traces to <c>GetModuleControls</c> (DataProvider.vb:L178), reached through
    /// ModuleControlController.vb:L127, and supersedes the untyped non-generic list it returned.
    /// </para>
    /// <para>
    /// <see cref="ModuleControl.ModuleDefinitionId"/> is nullable in the schema, because an
    /// installation also holds host-level controls that belong to no definition. Those rows are
    /// therefore never returned by this member, whatever identity is supplied - a definition's
    /// controls and the definition-less controls are disjoint sets.
    /// </para>
    /// <para>
    /// The legacy procedure ordered by <see cref="ModuleControl.ViewOrder"/>, which is itself
    /// nullable, so an implementer must break the resulting ties deterministically rather than
    /// leaving the row order to the store.
    /// </para>
    /// </remarks>
    /// <param name="moduleDefinitionId">The identity of the owning definition.</param>
    /// <param name="cancellationToken">Propagates notification that the operation should be cancelled.</param>
    /// <returns>The definition's controls, or an empty list when it has none.</returns>
    Task<IReadOnlyList<ModuleControl>> GetModuleControlsByDefinitionIdAsync(int moduleDefinitionId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the control rows of one definition that carry a given control key.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Traces to <c>GetModuleControlsByKey</c> (DataProvider.vb:L179), reached through
    /// ModuleControlController.vb:L131. The argument order mirrors the legacy member, which named
    /// the key first.
    /// </para>
    /// <para>
    /// A control key is not unique by itself, which is why this member returns a list where the
    /// key-and-source lookup below returns at most one row: a definition may publish several
    /// controls under one key, distinguished by their source. The terminal procedure also excluded
    /// one reserved control ordinal from its result, and that exclusion is a property of the legacy
    /// query rather than of this contract - an implementer reproducing it should do so explicitly
    /// and record it, since it is not visible in this signature.
    /// </para>
    /// </remarks>
    /// <param name="controlKey">
    /// The control key to match, as held by <see cref="ModuleControl.ControlKey"/>. An empty value
    /// is a legitimate key and must be matched as given, never treated as though no key had been
    /// supplied.
    /// </param>
    /// <param name="moduleDefinitionId">The identity of the owning definition.</param>
    /// <param name="cancellationToken">Propagates notification that the operation should be cancelled.</param>
    /// <returns>The matching controls, or an empty list when none matches.</returns>
    Task<IReadOnlyList<ModuleControl>> GetModuleControlsByKeyAsync(string controlKey, int moduleDefinitionId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the single control row identified by its definition, its control key and its source,
    /// or <see langword="null"/> when no control matches all three.
    /// </summary>
    /// <remarks>
    /// Traces to <c>GetModuleControlByKeyAndSrc</c> (DataProvider.vb:L180), reached through
    /// ModuleControlController.vb:L123. The three values together are the natural key the schema
    /// enforces with a unique index, which is why this member returns at most one row while the
    /// key-only lookup above returns a list. The argument order mirrors the legacy member, which
    /// named the definition first.
    /// </remarks>
    /// <param name="moduleDefinitionId">The identity of the owning definition.</param>
    /// <param name="controlKey">
    /// The control key to match, as held by <see cref="ModuleControl.ControlKey"/>. An empty value
    /// is a legitimate key and must be matched as given.
    /// </param>
    /// <param name="controlSrc">
    /// The control source to match, as held by <see cref="ModuleControl.ControlSrc"/>. It is inert
    /// text used only to complete the key; nothing resolves or loads what it names. An empty value
    /// is a legitimate key and must be matched as given.
    /// </param>
    /// <param name="cancellationToken">Propagates notification that the operation should be cancelled.</param>
    /// <returns>The matching control, or <see langword="null"/> when none matches.</returns>
    Task<ModuleControl?> GetModuleControlByKeyAndSrcAsync(int moduleDefinitionId, string controlKey, string controlSrc, CancellationToken cancellationToken = default);

    /// <summary>
    /// Stages the insertion of a new control row.
    /// </summary>
    /// <remarks>
    /// Traces to <c>AddModuleControl</c> (DataProvider.vb:L181), which took nine positional
    /// arguments and returned the generated identity, and which the legacy code reached through
    /// ModuleControlController.vb:L110. The argument list collapses onto the entity. The insertion
    /// is staged, not performed, so <see cref="ModuleControl.ModuleControlId"/> holds its final
    /// value only once <see cref="IUnitOfWork.SaveChangesAsync"/> has completed.
    /// <see cref="ModuleControl.ControlType"/> is carried as the plain persisted ordinal, which
    /// legitimately includes negative values, so nothing here validates or reinterprets it.
    /// </remarks>
    /// <param name="moduleControl">The control to insert.</param>
    /// <param name="cancellationToken">Propagates notification that the operation should be cancelled.</param>
    /// <returns>A task that completes once the insertion has been staged.</returns>
    Task AddModuleControlAsync(ModuleControl moduleControl, CancellationToken cancellationToken = default);

    /// <summary>
    /// Stages an update to an existing control row.
    /// </summary>
    /// <remarks>
    /// Traces to <c>UpdateModuleControl</c> (DataProvider.vb:L182), which took ten positional
    /// arguments - the nine of the insertion plus the identity - and which the legacy code reached
    /// through a pair of overloads at ModuleControlController.vb:L135 and L139 whose only difference
    /// was a cache flag; the flag is not surfaced. Both the argument list and the flag are gone: the
    /// entity carries the new state and its own identity.
    /// </remarks>
    /// <param name="moduleControl">
    /// The control to update, identified by <see cref="ModuleControl.ModuleControlId"/>.
    /// </param>
    /// <param name="cancellationToken">Propagates notification that the operation should be cancelled.</param>
    /// <returns>A task that completes once the update has been staged.</returns>
    Task UpdateModuleControlAsync(ModuleControl moduleControl, CancellationToken cancellationToken = default);

    /// <summary>
    /// Stages the removal of a control row.
    /// </summary>
    /// <remarks>
    /// Traces to <c>DeleteModuleControl</c> (DataProvider.vb:L183), reached through
    /// ModuleControlController.vb:L114. Removing a control that does not exist is not an error the
    /// caller has to guard against.
    /// </remarks>
    /// <param name="moduleControlId">The identity of the control to remove.</param>
    /// <param name="cancellationToken">Propagates notification that the operation should be cancelled.</param>
    /// <returns>A task that completes once the removal has been staged.</returns>
    Task DeleteModuleControlAsync(int moduleControlId, CancellationToken cancellationToken = default);
}
