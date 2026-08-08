// MIGRATION: this contract replaces the orchestration half of the legacy ModuleController together with the
// three legacy module admin screens and the read-only halves of the desktop-module and module-definition
// controllers. Static members become instance members on an injected service so they can be substituted in
// tests, which the legacy shape made impossible: a Shared member was reached through the type itself, so no
// consumer could stand it aside for a test double.
//
// MIGRATION: the flattened legacy object is split four ways - module, placement, definition and control -
// along the real table boundaries, and the two key-value settings stores (module-scoped and
// placement-scoped) are never merged. ModuleSettingsDto documents both rules in full.
using DnnMigration.Application.Dtos.Common;
using DnnMigration.Application.Dtos.Module;
using DnnMigration.Domain.Common;

namespace DnnMigration.Application.Abstractions;

/// <summary>
/// Orchestrates module instances within a portal: listing them, reading and writing one instance
/// and its settings, listing the definitions that can be instantiated, and moving content in and
/// out.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Every member is portal-scoped, and that is a tenancy boundary rather than a convenience.</strong>
/// The portal identifier is the first argument of every member and the implementation must confirm
/// that the addressed module belongs to it before reading or writing. <c>Modules.PortalID</c> is
/// genuinely nullable - the 03.00.01 upgrade script adds it as <c>int NULL</c> - so a host-level
/// module belongs to no portal and is reachable only where a member documents it.
/// </para>
/// <para>
/// <strong>No sentinel signals absence.</strong> <c>Modules.ModuleID</c> and <c>Tabs.TabID</c> are
/// both <c>IDENTITY (0, 1)</c> and <c>TabModules.TabModuleID</c> and
/// <c>ModuleDefinitions.ModuleDefID</c> are both <c>IDENTITY (1, 1)</c>, while -1 is a genuine
/// portal identifier and a genuine module-order instruction. No implementation may coalesce 0 or -1
/// into absence, and no optional identifier is expressed as anything but a nullable integer.
/// </para>
/// <para>
/// <strong>Registration and lifetime.</strong> One of the seven services registered by
/// <c>AddApplication</c>, as <c>AddScoped&lt;IModuleService, ModuleService&gt;</c>, implemented by
/// <c>Application/Services/ModuleService.cs</c>. Scoped is required: it depends on the scoped
/// module, definition and tab repositories and on the unit of work.
/// </para>
/// </remarks>
public interface IModuleService
{
    /// <summary>Lists one page of the modules in a portal.</summary>
    /// <param name="portalId">The portal to list within.</param>
    /// <param name="request">Page coordinates and the free-text query, applied to the module title.</param>
    /// <param name="tabId">
    /// Restrict to the modules placed on one page, or <see langword="null"/> for the whole portal. 0 is
    /// a genuine page.
    /// </param>
    /// <param name="includeDeleted">Whether to include modules in the recycle bin.</param>
    /// <param name="cancellationToken">Token that cancels the read.</param>
    /// <returns>
    /// A task producing a successful <see cref="Result{T}"/> carrying the page and its total count.
    /// </returns>
    /// <remarks>
    /// <para>
    /// A module placed on every page contributes one row per placement, each with its own placement
    /// identifier.
    /// </para>
    /// <para>
    /// The window and the total are in ROW units, which is the unit the response carries: a request for
    /// a page of twenty receives at most twenty rows, the total counts the rows the whole collection
    /// holds, and the next page index continues where the previous one stopped. Neither figure is
    /// widened to describe a projection, because the projection is what the window is taken over.
    /// </para>
    /// <para>
    /// The ordering is fixed and is not caller-selectable: modules by title then key, and within each
    /// module its placements by page, then position, then placement identifier. A request that names a
    /// sort field is refused with <c>module.request_invalid</c> rather than answered in the default
    /// order, because a listing that accepted the field and ignored it would return a page the caller
    /// believes was ordered and has no way to discover was not.
    /// </para>
    /// </remarks>
    Task<Result<PagedResult<ModuleListItemDto>>> ListModulesAsync(
        int portalId,
        PagedRequest request,
        int? tabId = null,
        bool includeDeleted = false,
        CancellationToken cancellationToken = default);

    /// <summary>Reads one module together with one of its placements.</summary>
    /// <param name="portalId">The portal the module must belong to.</param>
    /// <param name="moduleId">The module. 0 is genuine.</param>
    /// <param name="tabModuleId">
    /// The placement to describe, or <see langword="null"/> to take the module's first placement in
    /// page order - which is what the legacy settings screen bound.
    /// </param>
    /// <param name="cancellationToken">Token that cancels the read.</param>
    /// <returns>
    /// A task producing a successful <see cref="Result{T}"/> whose value describes the module, or
    /// whose value is <see langword="null"/> when no such module exists in the portal.
    /// </returns>
    Task<Result<ModuleDetailDto?>> GetModuleAsync(
        int portalId,
        int moduleId,
        int? tabModuleId = null,
        CancellationToken cancellationToken = default);

    /// <summary>Creates a module and places it on a page.</summary>
    /// <param name="portalId">The portal to create the module in.</param>
    /// <param name="request">The module and placement state to create.</param>
    /// <param name="cancellationToken">Token that cancels the write.</param>
    /// <returns>
    /// A task producing a successful <see cref="Result{T}"/> describing the created module and its
    /// placement.
    /// </returns>
    /// <remarks>
    /// Writes to <c>dbo.Modules</c> and <c>dbo.TabModules</c> as one unit of work, so a module can
    /// never exist without the placement it was created with.
    /// </remarks>
    Task<Result<ModuleDetailDto>> CreateModuleAsync(
        int portalId,
        CreateModuleRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>Replaces the editable state of one module placement.</summary>
    /// <param name="portalId">The portal the module must belong to.</param>
    /// <param name="moduleId">The module to update. 0 is genuine.</param>
    /// <param name="request">The complete editable state to apply.</param>
    /// <param name="cancellationToken">Token that cancels the write.</param>
    /// <returns>
    /// A task producing a successful <see cref="Result{T}"/> describing the stored result, or whose
    /// value is <see langword="null"/> when no such module exists in the portal.
    /// </returns>
    /// <remarks>
    /// The selected page and the two instruction flags are honoured here and are the reason this is
    /// a multi-table write. Changing the page copies the placement and its scoped settings before
    /// removing the source; naming the module as the portal default writes portal-level keys; and
    /// propagating appearance touches every module on every non-administrative page.
    /// </remarks>
    Task<Result<ModuleDetailDto?>> UpdateModuleAsync(
        int portalId,
        int moduleId,
        UpdateModuleRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>Removes a module placement, or recycles the module itself.</summary>
    /// <param name="portalId">The portal the module must belong to.</param>
    /// <param name="moduleId">The module. 0 is genuine.</param>
    /// <param name="tabModuleId">
    /// The single placement to remove, or <see langword="null"/> to recycle the module and every
    /// placement of it.
    /// </param>
    /// <param name="cancellationToken">Token that cancels the write.</param>
    /// <returns>A task producing a successful <see cref="Result"/>.</returns>
    /// <remarks>
    /// Recycling is a soft delete through the module's recycle-bin flag - <c>Modules.IsDeleted</c>,
    /// a <c>bit NOT NULL</c> column added in the 02.00.00 upgrade script with a default of 0 -
    /// which is what the legacy administration screens performed, so content survives and the
    /// module can be restored.
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
    /// <param name="tabModuleId">
    /// The placement whose placement-scoped settings are wanted, or <see langword="null"/> for the
    /// module's first placement.
    /// </param>
    /// <param name="cancellationToken">Token that cancels the read.</param>
    /// <returns>
    /// A task producing a successful <see cref="Result{T}"/> carrying the configuration, or whose
    /// value is <see langword="null"/> when no such module exists in the portal. Administrative
    /// modules fail with <c>module.settings_protected</c>, because their settings are exposed only
    /// through typed privileged contracts.
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
    /// <param name="tabModuleId">
    /// The placement whose placement-scoped settings are replaced, or <see langword="null"/> to
    /// leave placement settings untouched.
    /// </param>
    /// <param name="moduleSettings">The complete desired module-scoped set.</param>
    /// <param name="tabModuleSettings">
    /// The complete desired placement-scoped set, applied only when a placement is named.
    /// </param>
    /// <param name="cancellationToken">Token that cancels the write.</param>
    /// <returns>A task producing a successful <see cref="Result"/>.</returns>
    /// <remarks>
    /// Replace-the-set rather than a granular add, update and delete triad: the caller submits the
    /// whole desired state and the service computes the difference and commits it as one unit of
    /// work.
    /// </remarks>
    Task<Result> UpdateModuleSettingsAsync(
        int portalId,
        int moduleId,
        int? tabModuleId,
        IReadOnlyDictionary<string, string> moduleSettings,
        IReadOnlyDictionary<string, string> tabModuleSettings,
        CancellationToken cancellationToken = default);

    /// <summary>Lists the module definitions a portal may instantiate.</summary>
    /// <param name="portalId">The portal whose available definitions are wanted.</param>
    /// <param name="cancellationToken">Token that cancels the read.</param>
    /// <returns>
    /// A task producing a successful <see cref="Result{T}"/> carrying the definitions in a stable
    /// order.
    /// </returns>
    /// <remarks>
    /// Restricted to definitions whose desktop module is either not premium or has been granted to
    /// the portal through <c>dbo.PortalDesktopModules</c>, which is the premium-module rule the
    /// legacy screens applied.
    /// </remarks>
    Task<Result<IReadOnlyList<ModuleDefinitionDto>>> ListModuleDefinitionsAsync(
        int portalId,
        CancellationToken cancellationToken = default);

    /// <summary>Reads one module definition, as it is available to a portal.</summary>
    /// <param name="portalId">The portal the definition must be available to.</param>
    /// <param name="moduleDefinitionId">The definition wanted.</param>
    /// <param name="cancellationToken">Token that cancels the read.</param>
    /// <returns>
    /// A task producing a successful <see cref="Result{T}"/> carrying the definition, or whose
    /// value is <see langword="null"/> when the portal has no such definition available - which
    /// lets the API layer answer <c>404</c> without this member raising a failure for an ordinary
    /// "not there".
    /// </returns>
    /// <remarks>
    /// <para>
    /// Replaces the legacy single-definition read
    /// <c>ModuleDefinitionController.GetModuleDefinition</c>, which took the identifier alone and
    /// answered from the whole installation.
    /// </para>
    /// <para>
    /// The value is nullable rather than the absence being a failure code, which is the convention
    /// every single-record read on this contract follows.
    /// </para>
    /// </remarks>
    Task<Result<ModuleDefinitionDto?>> GetModuleDefinitionAsync(
        int portalId,
        int moduleDefinitionId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists the definitions one desktop module declares, as they are available to a portal.
    /// </summary>
    /// <param name="portalId">The portal the definitions must be available to.</param>
    /// <param name="desktopModuleId">The installed package whose definitions are wanted.</param>
    /// <param name="cancellationToken">Token that cancels the read.</param>
    /// <returns>
    /// A task producing a successful <see cref="Result{T}"/> carrying the definitions in the same
    /// stable order the catalogue uses.
    /// </returns>
    /// <remarks>
    /// <para>
    /// Replaces <c>ModuleDefinitionController.GetModuleDefinitions(desktopModuleId)</c>.
    /// </para>
    /// <para>
    /// Narrowed by the same premium-module rule as the catalogue, for the reason given on the
    /// single-definition read above: a package the portal has not been granted must not become
    /// readable by naming it.
    /// </para>
    /// </remarks>
    Task<Result<IReadOnlyList<ModuleDefinitionDto>>> ListDesktopModuleDefinitionsAsync(
        int portalId,
        int desktopModuleId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Exports one module's content by asking the module's own portability behaviour for it.
    /// </summary>
    /// <param name="portalId">The portal the module must belong to.</param>
    /// <param name="moduleId">The module to export.</param>
    /// <param name="request">The naming hints for the produced document.</param>
    /// <param name="cancellationToken">Token that cancels the operation.</param>
    /// <returns>
    /// A task producing a successful <see cref="Result{T}"/> whose value is the exported document.
    /// </returns>
    /// <remarks>
    /// <para>
    /// The document is returned to the caller rather than written to a server path, which is the
    /// one substantive difference from the legacy screen and is recorded as such: the legacy
    /// destination was a folder beneath the portal home directory under the web root, which does
    /// not exist in the target container topology.
    /// </para>
    /// <para>
    /// THE WIRE FORMAT IS THE LEGACY STANDALONE ADMIN SCREEN'S, and an implementer must not
    /// substitute the portal-template one.
    /// </para>
    /// <para>
    /// <b>The content an implementation will carry is bounded.</b> A payload arrives from module
    /// code rather than from the caller's request, so no request-body limit applies to it, and the
    /// document assembly and the well-formedness check that follow are both proportional to its
    /// length. An implementation therefore states a ceiling and refuses beyond it with
    /// <c>module.export_failed</c>, which is a server-side classification because the caller
    /// supplied nothing it could correct.
    /// </para>
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
    /// A task producing a successful <see cref="Result"/>, which is a positive statement that the
    /// module's content was replaced.
    /// </returns>
    /// <remarks>
    /// <para>
    /// The whole import is one unit of work, so a document that fails part-way leaves the module exactly
    /// as it was rather than half-loaded. The target module is named in the body because the endpoint
    /// carries no identifier in its route; the portal argument is what prevents that body-supplied
    /// identifier from reaching another tenant's module. The document's declared type is enforced, and
    /// an implementer must not skip that check.
    /// </para>
    /// <para>
    /// The provenance recorded on the audit trail is bounded and normalised, not verbatim. The submitted
    /// folder name, document name and declared version are caller-controlled and this request carries no
    /// length bound of its own, so an implementer caps each one and replaces its control characters
    /// before constructing the event, preferring server-derived package facts wherever they answer the
    /// same question.
    /// </para>
    /// </remarks>
    Task<Result> ImportModuleAsync(
        int portalId,
        ModuleImportRequest request,
        CancellationToken cancellationToken = default);
}
