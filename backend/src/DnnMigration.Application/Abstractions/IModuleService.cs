// MIGRATION: this contract replaces the orchestration half of Library/Components/Modules/ModuleController.vb
// - 1,456 measured lines, every member Shared (static) - together with the three legacy admin screens
// Website/admin/Modules/ModuleSettings.ascx.vb, Export.ascx.vb and Import.ascx.vb. Static members become
// instance members on an injected service so they can be substituted in tests, which the legacy shape
// made impossible.
//
// MIGRATION: both legacy row-hydration paths are gone, not translated. ModuleController.FillModuleInfo
// (L53) is a hand-rolled block spanning L66-L124 that assigns one property per line through
// Convert.ToXxx(Null.SetNull(dr("Column"), currentValue)), and its wrappers FillModuleInfoCollection
// (L164) and FillModuleInfoDictionary (L190) build an untyped sequence and an untyped dictionary over it.
// The framework's separate reflection-driven hydrator, 729 lines with 21 in-scope call sites, is likewise
// not carried forward. The object-relational materialiser replaces both, so no Fill member appears
// anywhere in the target and no hydration flag is honoured.
//
// MIGRATION: reflection-based activation is gone. ModuleController.vb:L231 and L431 both call
// Framework.Reflection.CreateObject(objModule.BusinessControllerClass) - a stored type name handed to a
// late-bound activator - and EventMessageProcessor.vb does the same at L32, L52 and L77. Those five sites
// are the only "COM-like" activation in scope and none is true COM interop. The export and import members
// below resolve a module's behaviour from the closed, dependency-injected set behind
// IModuleBusinessControllerFactory instead, so a type name never crosses a boundary to be activated and a
// module that is not registered fails with a reason rather than throwing a reflection error.
//
// MIGRATION: the flattened legacy object is split across four contracts, matching the real table
// boundaries. ModuleInfo.vb is one 936-line class exposing 58 properties over the
// Modules-to-TabModules-to-ModuleDefinitions-to-ModuleControls join; the read contracts here are the list
// row, the detail shape, the settings shape and the definition lookup, and each documents which table
// owns each member. The practical consequence for this contract is that a module with AllTabs set has
// many placements: it is one module row and several placement rows, so a delete of "the module" and a
// delete of "this placement" are different operations and are distinguished below.
//
// MIGRATION: caching is deliberate rather than incidental. ModuleController.vb reaches the legacy static
// cache at 21 measured sites, computing each expiry as a per-entity timeout multiplied by a global
// performance setting (L998, L1052, L1264, L1355) and invalidating coarsely. The implementing service
// applies caching through the domain layer's cache abstraction with the legacy key names preserved as
// constants and explicit invalidation after every successful write, and no cache concern appears on this
// surface.
//
// MIGRATION: Optional ByVal parameter tails become explicit arguments. The legacy members carried
// optional tails whose defaults were invisible at the call site; every member below states its arguments,
// and the paging pair travels as the shared paged-request contract rather than as a ByRef total.

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
