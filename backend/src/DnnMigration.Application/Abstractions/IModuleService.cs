// MIGRATION: this contract replaces the orchestration half of Library/Components/Modules/ModuleController.vb
// MIGRATION: - 1,456 measured lines carrying 38 public members, six of them Shared (static) - together with
// MIGRATION: the three legacy admin screens Website/admin/Modules/ModuleSettings.ascx.vb, Export.ascx.vb and
// MIGRATION: Import.ascx.vb, and the read-only halves of DesktopModuleController.vb (92 lines, 12 members)
// MIGRATION: and ModuleDefinitionController.vb (7 members). Static members become instance members on an
// MIGRATION: injected service so they can be substituted in tests, which the legacy shape made impossible:
// MIGRATION: an instance member was reached through a directly constructed object and a Shared member
// MIGRATION: through the type itself, so no consumer could stand either aside for a test double.
// MIGRATION:
// MIGRATION: Every line below carries the marker deliberately. Rule T5 requires each divergence to be
// MIGRATION: annotated inline rather than silently absorbed, and a per-line marker keeps the annotation
// MIGRATION: unambiguous if a paragraph is ever reflowed.
// MIGRATION:
// MIGRATION: 1. THE FLATTENED OBJECT IS SPLIT FOUR WAYS. ModuleInfo.vb is one 936-line class flattening the
// MIGRATION: Modules-to-TabModules-to-ModuleDefinitions-to-ModuleControls join. It becomes four entities
// MIGRATION: along the real table boundaries, so a module INSTANCE is identified by the pair
// MIGRATION: (ModuleID, TabID) and TabModuleID is the surrogate key of that placement. A module marked for
// MIGRATION: every page is one Modules row and many TabModules rows, which is exactly why removing "the
// MIGRATION: module" and removing "this placement" are different operations on this surface. Reported and
// MIGRATION: not corrected: the migration plan describes that class as 58 properties, whereas the measured
// MIGRATION: count of Public Property declarations is 54. The entity author's own measurement stands, since
// MIGRATION: this contract exposes DTOs rather than the entity and is not the place to settle the count.
// MIGRATION:
// MIGRATION: 2. TWO KEY-VALUE SETTINGS STORES, NEVER MERGED. The legacy pair returned untyped hashtables and
// MIGRATION: documented their own separation: the module-scoped reader at L1237 is remarked
// MIGRATION: "TabModuleSettings are not included" and the placement-scoped reader at L1336 is remarked
// MIGRATION: "ModuleSettings are not included". Both become one typed settings DTO that carries the two
// MIGRATION: stores as two distinct dictionaries - dbo.ModuleSettings keyed (ModuleID, SettingName) and
// MIGRATION: shared by every placement, dbo.TabModuleSettings keyed (TabModuleID, SettingName) and owned by
// MIGRATION: one placement. Collapsing them into a single map would destroy an every-page module's per-page
// MIGRATION: configuration, so the scope discriminator is explicit in both the read and the write member.
// MIGRATION:
// MIGRATION: 3. SIX PER-KEY MUTATORS BECOME REPLACE-THE-SET. The module-scoped triad at L1283, L1306 and
// MIGRATION: L1318 and the placement-scoped triad at L1373, L1395 and L1407 each set one key, deleted one
// MIGRATION: key or deleted every key. The caller now submits the whole desired set and the service computes
// MIGRATION: the difference and commits it as one unit of work. Keeping the triad would force the calling
// MIGRATION: controller to decide which rows to insert, update or delete, which Rule T2 forbids.
// MIGRATION:
// MIGRATION: 4. ORDERING AND PLACEMENT FOLD INTO THE UPDATE REQUEST. UpdateModuleOrder (L1160), the two
// MIGRATION: UpdateTabModuleOrder overloads (L1197, L1432) and MoveModule (L1078) were separate mutators
// MIGRATION: whose effects a caller had to sequence itself. Pane, order and placement are ordinary editable
// MIGRATION: state on the update request, and sibling renumbering within a pane happens inside the service,
// MIGRATION: so no caller computes an ordinal and no member exposes a renumbering step.
// MIGRATION:
// MIGRATION: 5. THREE DELETE MEMBERS COLLAPSE, KEEPING THE DISTINCTION THEY ENCODED. DeleteAllModules (L795)
// MIGRATION: carried an explicit deleteBaseModule flag alongside DeleteModule (L819) and DeleteTabModule
// MIGRATION: (L837), which is the proof that removing a placement and recycling the module are genuinely two
// MIGRATION: operations. One member expresses it through a nullable placement identifier rather than a bare
// MIGRATION: boolean: naming a placement removes that placement, and omitting one recycles the module and
// MIGRATION: every placement of it. The argument therefore says which row is addressed instead of asking the
// MIGRATION: controller to know what "true" means. The legacy untyped array-list of target pages becomes a
// MIGRATION: read-only generic list of page identifiers wherever a set of pages is genuinely needed.
// MIGRATION:
// MIGRATION: 6. NO CACHE CONCERN CROSSES THIS SURFACE. ModuleController reaches the legacy static cache at 21
// MIGRATION: measured sites - the largest single concentration in scope - computing each expiry as a
// MIGRATION: per-entity timeout multiplied by a global performance setting (L998, L1052, L1264, L1355) and
// MIGRATION: invalidating coarsely. The read flag at L885, the definition flag at L57 of
// MIGRATION: ModuleDefinitionController.vb, the cache-invalidation Sub at L614 and the file-based module
// MIGRATION: OUTPUT caching helpers at L478, L482 and L486 are all absent here. Caching belongs to the
// MIGRATION: domain cache abstraction the implementing service injects, with the legacy key names preserved
// MIGRATION: as constants and explicit invalidation after every successful write. The three output-caching
// MIGRATION: helpers returned a server path, and no filesystem path appears on this contract at all.
// MIGRATION:
// MIGRATION: 7. THE XML PORTAL-TEMPLATE MEMBERS ARE NOT PORTED. The deserialiser at L493 and the serialiser
// MIGRATION: at L539 exchanged raw XML node and document types and belonged to portal-template import, which
// MIGRATION: stays inside the portal service. No XML node, document or reader type crosses this boundary,
// MIGRATION: and the legacy template-merge enumeration is not among the target domain enumerations.
// MIGRATION:
// MIGRATION: 8. FIVE LATE-BOUND ACTIVATION SITES BECOME ONE INJECTED FACTORY. ModuleController.vb L231 and
// MIGRATION: L431 both hand a stored type name to a late-bound activator, and EventMessageProcessor.vb does
// MIGRATION: the same at L32, L52 and L77. These five are the only "COM-like" activation in scope and the
// MIGRATION: exclusion covering COM interop, VB6 and ActiveX is therefore VACUOUS against this codebase:
// MIGRATION: every one is ordinary .NET reflection, so no COM interop is removed here and no wording should
// MIGRATION: suggest otherwise. The export and import members resolve a module's portability behaviour from
// MIGRATION: the closed, dependency-injected set behind the sibling business-controller factory abstraction,
// MIGRATION: which the implementing service injects and this contract never names in a signature. A type
// MIGRATION: name consequently never crosses a boundary to be activated.
// MIGRATION:
// MIGRATION: 9. THE SILENT SWALLOW IS NOT REPRODUCED. Both activation sites sit inside a Try whose Catch
// MIGRATION: body is the comment "ignore errors", so a module that failed to export or import reported
// MIGRATION: success. Those outcomes are failure reasons here. Two consequences are documented on the
// MIGRATION: members themselves: a module whose behaviour is not registered fails with a reason rather than
// MIGRATION: throwing a reflection error, and an EMPTY export payload is a success rather than a failure,
// MIGRATION: preserving the legacy guard at L233 that wrote a content element only for non-empty content.
// MIGRATION:
// MIGRATION: 10. THE DEFERRED-IMPORT EVENT-QUEUE PATH IS OMITTED. L422 tests a module's feature set against
// MIGRATION: the legacy integer sentinel and, when the module was installed in the same request, parks the
// MIGRATION: payload on a queue for replay after an application restart. That queue subsystem is excluded,
// MIGRATION: and resolving behaviour from a closed registered set makes the capability discovery it existed
// MIGRATION: to defer unnecessary. Import either succeeds or reports a reason; nothing is parked.
// MIGRATION:
// MIGRATION: 11. THE SEARCH READER IS NOT PORTED. The reader at L1032 collected the modules a crawler should
// MIGRATION: visit; search is deferred entirely and the legacy searchable contract depended on the flattened
// MIGRATION: entity and an untyped result collection, both eliminated. No search member appears here. The
// MIGRATION: searchable CAPABILITY survives as ordinary data on the desktop-module record.
// MIGRATION:
// MIGRATION: 12. BOTH LEGACY ROW-HYDRATION PATHS ARE GONE RATHER THAN TRANSLATED. The private hydrator at
// MIGRATION: L53 assigns one property per line by wrapping each column read in the sentinel-substituting
// MIGRATION: helper - the block the plan cites at L54 and L66 to L72 - and its two wrappers build an untyped
// MIGRATION: sequence and an untyped dictionary over it. The framework's separate 729-line reflection-driven
// MIGRATION: hydrator, with 21 in-scope call sites, is likewise not carried forward. The object-relational
// MIGRATION: materialiser replaces both, so no hydration or fill member appears anywhere in the target and
// MIGRATION: no sentinel-substituting column read survives.
// MIGRATION:
// MIGRATION: 13. THE DEFINITION SURFACE IS READ-ONLY. DesktopModuleController.vb and
// MIGRATION: ModuleDefinitionController.vb between them expose add, update and delete for desktop modules,
// MIGRATION: portal grants and definitions (L32, L36, L40, L45, L70 and L32, L36, L53, L57). None is exposed
// MIGRATION: here: the definition catalogue is installation-time reference data, and installing one is a
// MIGRATION: module-loader concern that is excluded wholesale. The nine read members - L41, L45, L49 and
// MIGRATION: L50, L54, L58, L62, L66, L80, L85 - collapse into the single definition lookup below, which
// MIGRATION: applies the premium-module grant rule the legacy screens applied. Reported and not corrected:
// MIGRATION: the plan cites the definition controller under a Definitions subfolder, but no such subfolder
// MIGRATION: exists - the measured path is Library/Components/Modules/ModuleDefinitionController.vb.
// MIGRATION:
// MIGRATION: 14. UNTYPED COLLECTIONS BECOME READ-ONLY GENERIC ONES. Every legacy read returned the
// MIGRATION: pre-generic untyped array-list type - L871, L915, L928, L941, L1020, L1223, L1423 - and the
// MIGRATION: page reader at L1044 returned a map keyed by integer whose VALUES were entities. Reads here
// MIGRATION: return a read-only generic list of DTOs or a page of them, no entity crosses the boundary in
// MIGRATION: either direction, and that entity-valued map is not ported in any form. The eager-loading flag
// MIGRATION: at L928 is likewise not carried across: a boolean that changes the shape of the result would
// MIGRATION: make the caller decide something, and module permissions belong to the permission service.
// MIGRATION: The two definition-keyed lookups at L955 and L1020 are not separate members either - the paged
// MIGRATION: list below is the query surface, and its request contract carries no definition filter, so a
// MIGRATION: filter parameter here would be a promise the implementation could not keep.
// MIGRATION:
// MIGRATION: 15. NO NUMERIC SENTINEL EVER MEANS ABSENT. The legacy sentinel for a missing integer is -1 and
// MIGRATION: the legacy code passes it as a real argument to mean "any page". Both candidate sentinels are
// MIGRATION: genuine keys in this schema: Modules.ModuleID is IDENTITY (0, 1) and Tabs.TabID is
// MIGRATION: IDENTITY (0, 1), so 0 is a real module and a real page, while Portals.PortalID is
// MIGRATION: IDENTITY (-1, 1), so -1 is a real portal and is simultaneously the sentinel. TabModuleID and
// MIGRATION: ModuleDefID are both IDENTITY (1, 1). Every optional identifier below is therefore a nullable
// MIGRATION: integer and absence is null. No implementation may coalesce 0 or -1 into absence.
// MIGRATION:
// MIGRATION: 16. TWO FURTHER OMISSIONS, RECORDED SO THEY ARE NOT MISTAKEN FOR OVERSIGHTS. The two CopyModule
// MIGRATION: overloads (L700, L743) and CopyTabModuleSettings (L764) have no member here: no endpoint and no
// MIGRATION: administration screen in scope copies a module between pages, and adding one would be
// MIGRATION: speculation rather than parity. The module-actions collection, whose Add at L151 of
// MIGRATION: ModuleActionCollection.vb carries an optional-argument tail, is a Web Forms menu affordance
// MIGRATION: over a pre-generic collection base and is not ported; had it been, that tail would have become
// MIGRATION: an options object rather than a signature of defaults.
// MIGRATION:
// MIGRATION: 17. STATUS NO LONGER TRAVELS THROUGH AN ARGUMENT. The legacy tree reports outcomes by mutating
// MIGRATION: a ByRef argument alongside a return value. Every member below returns a result carrying both
// MIGRATION: the value and the reason, so no out-parameter and no by-reference parameter appears in this
// MIGRATION: contract, and Optional ByVal tails whose defaults were invisible at the call site are explicit.

using DnnMigration.Application.Dtos.Common;
using DnnMigration.Application.Dtos.Module;
using DnnMigration.Domain.Common;

namespace DnnMigration.Application.Abstractions;

/// <summary>
/// Orchestrates module instances within a portal: listing them, reading and writing one instance and its
/// settings, listing the definitions that can be instantiated, and moving content in and out.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Every member is portal-scoped, and that is a tenancy boundary rather than a convenience.</strong>
/// The portal identifier is the first argument of every member and the implementation must confirm that
/// the addressed module belongs to it before reading or writing. <c>Modules.PortalID</c> is genuinely
/// nullable - the 03.00.01 upgrade script adds it as <c>int NULL</c> - so a host-level module belongs to
/// no portal and is reachable only where a member documents it.
/// </para>
/// <para>
/// <strong>A module and its placement are distinct.</strong> <c>dbo.Modules</c> holds one row per module;
/// <c>dbo.TabModules</c> holds one row per placement of that module on a page. Creating a module creates
/// one of each; a module marked for every page has one module row and many placement rows. Read contracts
/// key row identity on the placement identifier, and the delete member below distinguishes removing one
/// placement from recycling the module itself.
/// </para>
/// <para>
/// <strong>Two settings stores, kept separate.</strong> <c>dbo.ModuleSettings</c> is keyed
/// <c>(ModuleID, SettingName)</c> with a 256-character value and is shared by every placement;
/// <c>dbo.TabModuleSettings</c> is keyed <c>(TabModuleID, SettingName)</c> with a 2000-character value and
/// belongs to one placement. Merging them would collapse an every-page module's per-page configuration,
/// so the settings members exchange them as two dictionaries.
/// </para>
/// <para>
/// <strong>Result semantics.</strong> Every member returns a <see cref="Result"/> or a
/// <see cref="Result{T}"/>, so an expected outcome - an absent module, a definition the portal may not
/// use, a module whose behaviour is not registered - is a failure reason rather than an exception.
/// Failure codes are lower-case, dotted and stable, in the form <c>module.condition</c>. On a
/// single-item lookup a <see cref="Result{T}"/> that succeeded and carries <see langword="null"/> means
/// the row is absent, which is not a failure: callers test <see cref="Result.IsSuccess"/> first and then
/// the value.
/// </para>
/// <para>
/// <strong>No sentinel signals absence.</strong> <c>Modules.ModuleID</c> and <c>Tabs.TabID</c> are both
/// <c>IDENTITY (0, 1)</c> and <c>TabModules.TabModuleID</c> and <c>ModuleDefinitions.ModuleDefID</c> are
/// both <c>IDENTITY (1, 1)</c>, while -1 is a genuine portal identifier and a genuine module-order
/// instruction. No implementation may coalesce 0 or -1 into absence, and no optional identifier is
/// expressed as anything but a nullable integer.
/// </para>
/// <para>
/// <strong>Registration and lifetime.</strong> One of the seven services registered by
/// <c>AddApplication()</c>, as <c>AddScoped&lt;IModuleService, ModuleService&gt;()</c>, implemented by
/// <c>Application/Services/ModuleService.cs</c>. Scoped is required: it depends on the scoped module,
/// definition and tab repositories and on the unit of work.
/// </para>
/// <para>
/// <strong>Implementer's checklist.</strong> Reach the database only through repository abstractions.
/// Commit every multi-table write - a create, an every-page toggle, a settings replacement, an import -
/// as one unit of work. Resolve module behaviour only through
/// <see cref="IModuleBusinessControllerFactory"/>, never by activating a stored type name. Invalidate the
/// cache explicitly after every successful write. Perform every operation asynchronously and honour the
/// cancellation token.
/// </para>
/// </remarks>
public interface IModuleService
{
    /// <summary>
    /// Lists one page of the modules in a portal.
    /// </summary>
    /// <param name="portalId">The portal to list within.</param>
    /// <param name="request">Paging, sorting and free-text query, applied to the module title.</param>
    /// <param name="tabId">Restrict to the modules placed on one page, or <see langword="null"/> for the whole portal. 0 is a genuine page.</param>
    /// <param name="includeDeleted">Whether to include modules in the recycle bin.</param>
    /// <param name="cancellationToken">Token that cancels the read.</param>
    /// <returns>
    /// A task producing a successful <see cref="Result{T}"/> carrying the page and its total count. An
    /// empty page is a legitimate answer. Fails with <c>module.portal_not_found</c> when the portal does
    /// not exist, or <c>module.request_invalid</c> when the paging request is malformed.
    /// </returns>
    /// <remarks>
    /// A module placed on every page contributes one row per placement, each with its own placement
    /// identifier, so the total count is a count of placements rather than of modules.
    /// </remarks>
    Task<Result<PagedResult<ModuleListItemDto>>> ListModulesAsync(
        int portalId,
        PagedRequest request,
        int? tabId = null,
        bool includeDeleted = false,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads one module together with one of its placements.
    /// </summary>
    /// <param name="portalId">The portal the module must belong to.</param>
    /// <param name="moduleId">The module. 0 is genuine.</param>
    /// <param name="tabModuleId">
    /// The placement to describe, or <see langword="null"/> to take the module's first placement in page
    /// order - which is what the legacy settings screen bound.
    /// </param>
    /// <param name="cancellationToken">Token that cancels the read.</param>
    /// <returns>
    /// A task producing a successful <see cref="Result{T}"/> whose value describes the module, or whose
    /// value is <see langword="null"/> when no such module exists in the portal. Fails with
    /// <c>module.placement_not_found</c> when a placement is named that does not belong to the module.
    /// </returns>
    Task<Result<ModuleDetailDto?>> GetModuleAsync(
        int portalId,
        int moduleId,
        int? tabModuleId = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates a module and places it on a page.
    /// </summary>
    /// <param name="portalId">The portal to create the module in.</param>
    /// <param name="request">The module and placement state to create.</param>
    /// <param name="cancellationToken">Token that cancels the write.</param>
    /// <returns>
    /// A task producing a successful <see cref="Result{T}"/> describing the created module and its
    /// placement. Fails with <c>module.definition_not_found</c> when the definition does not exist or is
    /// not available to the portal, <c>module.tab_not_found</c> when the page does not exist in the
    /// portal, or <c>module.request_invalid</c> when the request is malformed.
    /// </returns>
    /// <remarks>
    /// Writes to <c>dbo.Modules</c> and <c>dbo.TabModules</c> as one unit of work, so a module can never
    /// exist without the placement it was created with. When the request asks for every page, one
    /// placement row is written per page of the portal in the same unit of work.
    /// </remarks>
    Task<Result<ModuleDetailDto>> CreateModuleAsync(
        int portalId,
        CreateModuleRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Replaces the editable state of one module placement.
    /// </summary>
    /// <param name="portalId">The portal the module must belong to.</param>
    /// <param name="moduleId">The module to update. 0 is genuine.</param>
    /// <param name="request">The complete editable state to apply.</param>
    /// <param name="cancellationToken">Token that cancels the write.</param>
    /// <returns>
    /// A task producing a successful <see cref="Result{T}"/> describing the stored result, or whose value
    /// is <see langword="null"/> when no such module exists in the portal. Fails with
    /// <c>module.request_invalid</c> when the request is malformed.
    /// </returns>
    /// <remarks>
    /// The two instruction flags on the request are honoured here and are the reason this is a
    /// multi-table write: naming the module as the portal default writes portal-level keys, and
    /// propagating appearance touches every module on every non-administrative page. Both are applied in
    /// the same unit of work as the module's own change, and both are recorded on the audit log because
    /// their blast radius exceeds the addressed module.
    /// </remarks>
    Task<Result<ModuleDetailDto?>> UpdateModuleAsync(
        int portalId,
        int moduleId,
        UpdateModuleRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes a module placement, or recycles the module itself.
    /// </summary>
    /// <param name="portalId">The portal the module must belong to.</param>
    /// <param name="moduleId">The module. 0 is genuine.</param>
    /// <param name="tabModuleId">
    /// The single placement to remove, or <see langword="null"/> to recycle the module and every
    /// placement of it.
    /// </param>
    /// <param name="cancellationToken">Token that cancels the write.</param>
    /// <returns>
    /// A task producing a successful <see cref="Result"/>. Fails with <c>module.not_found</c> when no
    /// such module exists in the portal, or <c>module.placement_not_found</c> when a placement is named
    /// that does not belong to it.
    /// </returns>
    /// <remarks>
    /// Recycling is a soft delete through the module's recycle-bin flag - <c>Modules.IsDeleted</c>, a
    /// <c>bit NOT NULL</c> column added in the 02.00.00 upgrade script with a default of 0 - which is
    /// what the legacy administration screens performed, so content survives and the module can be
    /// restored. Removing a single placement deletes that placement row and its placement-scoped
    /// settings, leaving the module and its other placements intact.
    /// </remarks>
    Task<Result> DeleteModuleAsync(
        int portalId,
        int moduleId,
        int? tabModuleId = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads one module placement's configuration, including both key-value settings stores.
    /// </summary>
    /// <param name="portalId">The portal the module must belong to.</param>
    /// <param name="moduleId">The module. 0 is genuine.</param>
    /// <param name="tabModuleId">The placement whose placement-scoped settings are wanted, or <see langword="null"/> for the module's first placement.</param>
    /// <param name="cancellationToken">Token that cancels the read.</param>
    /// <returns>
    /// A task producing a successful <see cref="Result{T}"/> carrying the configuration, or whose value is
    /// <see langword="null"/> when no such module exists in the portal.
    /// </returns>
    Task<Result<ModuleSettingsDto?>> GetModuleSettingsAsync(
        int portalId,
        int moduleId,
        int? tabModuleId = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Replaces the key-value settings recorded against a module and against one of its placements.
    /// </summary>
    /// <param name="portalId">The portal the module must belong to.</param>
    /// <param name="moduleId">The module whose module-scoped settings are replaced.</param>
    /// <param name="tabModuleId">The placement whose placement-scoped settings are replaced, or <see langword="null"/> to leave placement settings untouched.</param>
    /// <param name="moduleSettings">
    /// The complete desired module-scoped set. Passing an empty dictionary clears the store, which is the
    /// supported way to remove every module setting.
    /// </param>
    /// <param name="tabModuleSettings">The complete desired placement-scoped set, applied only when a placement is named.</param>
    /// <param name="cancellationToken">Token that cancels the write.</param>
    /// <returns>
    /// A task producing a successful <see cref="Result"/>. Fails with <c>module.not_found</c> when the
    /// module does not exist in the portal, or <c>module.setting_invalid</c> when a key is blank or a key
    /// or value exceeds its column length.
    /// </returns>
    /// <remarks>
    /// Replace-the-set rather than a granular add, update and delete triad: the caller submits the whole
    /// desired state and the service computes the difference and commits it as one unit of work.
    /// Exposing the triad would force the calling controller to decide which rows to insert, update or
    /// delete, which is a business decision no controller may take.
    /// </remarks>
    Task<Result> UpdateModuleSettingsAsync(
        int portalId,
        int moduleId,
        int? tabModuleId,
        IReadOnlyDictionary<string, string> moduleSettings,
        IReadOnlyDictionary<string, string> tabModuleSettings,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists the module definitions a portal may instantiate.
    /// </summary>
    /// <param name="portalId">The portal whose available definitions are wanted.</param>
    /// <param name="cancellationToken">Token that cancels the read.</param>
    /// <returns>
    /// A task producing a successful <see cref="Result{T}"/> carrying the definitions in a stable order.
    /// An empty sequence is a legitimate answer for a portal that has been granted none.
    /// </returns>
    /// <remarks>
    /// Restricted to definitions whose desktop module is either not premium or has been granted to the
    /// portal through <c>dbo.PortalDesktopModules</c>, which is the premium-module rule the legacy
    /// screens applied. The catalogue is installation-time reference data, so no member here creates or
    /// modifies a definition.
    /// </remarks>
    Task<Result<IReadOnlyList<ModuleDefinitionDto>>> ListModuleDefinitionsAsync(
        int portalId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Exports one module's content by asking the module's own portability behaviour for it.
    /// </summary>
    /// <param name="portalId">The portal the module must belong to.</param>
    /// <param name="moduleId">The module to export.</param>
    /// <param name="request">The naming hints for the produced document.</param>
    /// <param name="cancellationToken">Token that cancels the operation.</param>
    /// <returns>
    /// A task producing a successful <see cref="Result{T}"/> whose value is the exported document. Fails
    /// with <c>module.not_found</c> when the module does not exist in the portal,
    /// <c>module.not_portable</c> when the module's behaviour is not registered with the factory or does
    /// not support export, or <c>module.request_invalid</c> when the naming hints are malformed.
    /// </returns>
    /// <remarks>
    /// The document is returned to the caller rather than written to a server path, which is the one
    /// substantive difference from the legacy screen and is recorded as such: the legacy destination was
    /// a folder beneath the portal home directory under the web root, which does not exist in the target
    /// container topology.
    /// </remarks>
    Task<Result<string>> ExportModuleAsync(
        int portalId,
        int moduleId,
        ModuleExportRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Imports previously exported content into a module by handing it to the module's own portability
    /// behaviour.
    /// </summary>
    /// <param name="portalId">The portal the target module must belong to.</param>
    /// <param name="request">The target module, the document, and the provenance recorded on the audit log.</param>
    /// <param name="cancellationToken">Token that cancels the operation.</param>
    /// <returns>
    /// A task producing a successful <see cref="Result"/>. Fails with <c>module.not_found</c> when the
    /// target module does not exist in the portal, <c>module.not_portable</c> when its behaviour is not
    /// registered with the factory or does not support import, or <c>module.content_invalid</c> when the
    /// document cannot be interpreted.
    /// </returns>
    /// <remarks>
    /// The whole import is one unit of work, so a document that fails part-way leaves the module exactly
    /// as it was rather than half-loaded. The target module is named in the body because the endpoint
    /// carries no identifier in its route; the portal argument is what prevents that body-supplied
    /// identifier from reaching another tenant's module.
    /// </remarks>
    Task<Result> ImportModuleAsync(
        int portalId,
        ModuleImportRequest request,
        CancellationToken cancellationToken = default);
}
