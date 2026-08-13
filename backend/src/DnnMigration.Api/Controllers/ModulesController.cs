using Asp.Versioning;
using DnnMigration.Api.Authorization;
using DnnMigration.Api.ErrorHandling;
using DnnMigration.Api.Extensions;
using DnnMigration.Application.Abstractions;
using DnnMigration.Application.Dtos.Common;
using DnnMigration.Application.Dtos.Module;
using DnnMigration.Domain.Abstractions.Services;
using DnnMigration.Domain.Common;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DnnMigration.Api.Controllers;

/// <summary>The module resource: the pluggable content component and its placements.</summary>
/// <remarks>
/// <strong>The canonical module resource is flat and tenant-bound.</strong> Every route begins at
/// <c>/api/v1/modules</c>; the portal is the tenant resolved from the request host and authenticated
/// context, not a second resource identity embedded in the path.
/// </remarks>
[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/modules")]
[Produces("application/json")]
public sealed class ModulesController : ControllerBase
{
    private const string TenantUnresolvedCode = "portal.tenant_unresolved";

    /// <summary>The media type an exported module payload is returned as.</summary>
    private const string ExportContentType = "application/xml";

    private readonly IModuleService _modules;
    private readonly IPortalContextHolder _portalContext;

    /// <summary>Initialises a new instance of the <see cref="ModulesController"/> class.</summary>
    /// <param name="modules">The module service.</param>
    /// <param name="portalContext">The tenant resolved from the request host.</param>
    /// <exception cref="ArgumentNullException">Either dependency is <see langword="null"/>.</exception>
    public ModulesController(IModuleService modules, IPortalContextHolder portalContext)
    {
        _modules = modules ?? throw new ArgumentNullException(nameof(modules));
        _portalContext = portalContext ?? throw new ArgumentNullException(nameof(portalContext));
    }

    /// <summary>Returns the tenant resolved for the current request, or <see langword="null"/>.</summary>
    private int? ResolvePortalId() =>
        _portalContext.IsResolved ? _portalContext.Current.PortalId : null;

    /// <summary>Lists a portal's modules.</summary>
    /// <param name="request">Paging and sorting arguments, bound from the query string.</param>
    /// <param name="tabId">Restricts the result to modules placed on one tab.</param>
    /// <param name="includeDeleted">Includes modules that are in the recycle bin.</param>
    /// <param name="cancellationToken">Abandons the request when the caller disconnects.</param>
    /// <returns>A page of modules.</returns>
    // TENANT-BOUND, NOT MERELY AUTHENTICATED. This action inherited only the class-level authentication
    // requirement, so any bearer token could name any portal in the route and enumerate that tenant's
    // modules.
    [HttpGet]
    [Authorize(Policy = PolicyNames.PortalAdministrator)]
    [ProducesResponseType(typeof(PagedResponse<ModuleListItemDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<PagedResponse<ModuleListItemDto>>> ListAsync(
        [FromQuery] ModulePagedRequest request,
        [FromQuery] int? tabId,
        [FromQuery] bool includeDeleted,
        CancellationToken cancellationToken)
    {
        if (ResolvePortalId() is not { } portalId)
        {
            return this.ForbiddenProblem(TenantUnresolvedCode);
        }

        Result<PagedResult<ModuleListItemDto>> outcome = await _modules
            .ListModulesAsync(portalId, request, tabId, includeDeleted, cancellationToken)
            .ConfigureAwait(false);

        return this.CompletePage(outcome);
    }

    /// <summary>Retrieves one module.</summary>
    /// <param name="moduleId">The module identifier.</param>
    /// <param name="tabModuleId">Selects one placement when the module appears on several tabs.</param>
    /// <param name="cancellationToken">Abandons the request when the caller disconnects.</param>
    /// <returns>The module, or <c>404 Not Found</c> when it does not exist in this portal.</returns>
    // THREE MEASURED CONSEQUENCES OF THE VIEW GATE THIS REPLACES, all reproduced against a live
    // installation before the change: 1.
    [HttpGet("{moduleId:int}")]
    [Authorize(Policy = PolicyNames.ModuleEdit)]
    [ProducesResponseType(typeof(ApiResponse<ModuleDetailDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ApiResponse<ModuleDetailDto?>>> GetAsync(
        int moduleId,
        [FromQuery] int? tabModuleId,
        CancellationToken cancellationToken)
    {
        if (ResolvePortalId() is not { } portalId)
        {
            return this.ForbiddenProblem(TenantUnresolvedCode);
        }

        Result<ModuleDetailDto?> outcome = await _modules
            .GetModuleAsync(portalId, moduleId, tabModuleId, cancellationToken)
            .ConfigureAwait(false);

        return this.Complete(outcome);
    }

    /// <summary>Creates a module and places it.</summary>
    /// <param name="request">The module to create.</param>
    /// <param name="cancellationToken">Abandons the request when the caller disconnects.</param>
    /// <returns>The created module, with its address in the location header.</returns>
    // Everything above explains why this action carries no named POLICY, and none of it changes: a policy
    // would narrow the legacy entitlement, and the page the grant is claimed against arrives in the body
    // where no route-reading policy can see it.
    [HttpPost]
    [Authorize]
    [ProducesResponseType(typeof(ApiResponse<ModuleDetailDto>), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ApiResponse<ModuleDetailDto>>> CreateAsync(
        [FromBody] CreateModuleRequest request,
        CancellationToken cancellationToken)
    {
        if (ResolvePortalId() is not { } portalId)
        {
            return this.ForbiddenProblem(TenantUnresolvedCode);
        }

        Result<ModuleDetailDto> outcome = await _modules
            .CreateModuleAsync(portalId, request, cancellationToken)
            .ConfigureAwait(false);

        return this.Created(outcome, created => created.ModuleId);
    }

    /// <summary>Updates a module.</summary>
    /// <param name="moduleId">The module identifier.</param>
    /// <param name="request">The new state.</param>
    /// <param name="cancellationToken">Abandons the request when the caller disconnects.</param>
    /// <returns>
    /// The updated module, or <c>404 Not Found</c> when it or the selected page does not exist in this
    /// portal.
    /// </returns>
    // MIGRATION: request.TabId SELECTS THE PLACEMENT THIS UPDATE ADDRESSES; IT DOES NOT MOVE IT.
    // ModuleSettings.ascx.vb L398-L408 compared the selected page with the current one and called
    // ModuleController.MoveModule, whose implementation copied the placement and its scoped settings before
    // deleting the source.
    [HttpPut("{moduleId:int}")]
    [Authorize(Policy = PolicyNames.ModuleEdit)]
    [ProducesResponseType(typeof(ApiResponse<ModuleDetailDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ApiResponse<ModuleDetailDto?>>> UpdateAsync(
        int moduleId,
        [FromBody] UpdateModuleRequest request,
        CancellationToken cancellationToken)
    {
        if (ResolvePortalId() is not { } portalId)
        {
            return this.ForbiddenProblem(TenantUnresolvedCode);
        }

        Result<ModuleDetailDto?> outcome = await _modules
            .UpdateModuleAsync(portalId, moduleId, request, cancellationToken)
            .ConfigureAwait(false);

        return this.Complete(outcome);
    }

    /// <summary>Deletes a module, or removes one of its placements.</summary>
    /// <param name="moduleId">The module identifier.</param>
    /// <param name="tabModuleId">Removes only this placement, leaving the module on its other tabs.</param>
    /// <param name="cancellationToken">Abandons the request when the caller disconnects.</param>
    /// <returns><c>204 No Content</c> when the deletion has been applied.</returns>
    [HttpDelete("{moduleId:int}")]
    [Authorize(Policy = PolicyNames.ModuleEdit)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<ActionResult> DeleteAsync(
        int moduleId,
        [FromQuery] int? tabModuleId,
        CancellationToken cancellationToken)
    {
        if (ResolvePortalId() is not { } portalId)
        {
            return this.ForbiddenProblem(TenantUnresolvedCode);
        }

        Result outcome = await _modules
            .DeleteModuleAsync(portalId, moduleId, tabModuleId, cancellationToken)
            .ConfigureAwait(false);

        return this.Complete(outcome);
    }

    /// <summary>Retrieves a module's settings.</summary>
    /// <param name="moduleId">The module identifier.</param>
    /// <param name="tabModuleId">Selects one placement when the module appears on several tabs.</param>
    /// <param name="cancellationToken">Abandons the request when the caller disconnects.</param>
    /// <returns>The settings, or <c>404 Not Found</c> when the module does not exist in this portal.</returns>
    [HttpGet("{moduleId:int}/settings")]
    [Authorize(Policy = PolicyNames.ModuleEdit)]
    [ProducesResponseType(typeof(ApiResponse<ModuleSettingsDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ApiResponse<ModuleSettingsDto?>>> GetSettingsAsync(
        int moduleId,
        [FromQuery] int? tabModuleId,
        CancellationToken cancellationToken)
    {
        if (ResolvePortalId() is not { } portalId)
        {
            return this.ForbiddenProblem(TenantUnresolvedCode);
        }

        Result<ModuleSettingsDto?> outcome = await _modules
            .GetModuleSettingsAsync(portalId, moduleId, tabModuleId, cancellationToken)
            .ConfigureAwait(false);

        return this.Complete(outcome);
    }

    /// <summary>Replaces a module's settings.</summary>
    /// <param name="moduleId">The module identifier.</param>
    /// <param name="settings">The settings to store.</param>
    /// <param name="tabModuleId">Selects one placement when the module appears on several tabs.</param>
    /// <param name="cancellationToken">Abandons the request when the caller disconnects.</param>
    /// <returns><c>204 No Content</c> when the settings have been stored.</returns>
    [HttpPut("{moduleId:int}/settings")]
    [Authorize(Policy = PolicyNames.ModuleEdit)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<ActionResult> UpdateSettingsAsync(
        int moduleId,
        [FromBody] ModuleSettingsDto settings,
        [FromQuery] int? tabModuleId,
        CancellationToken cancellationToken)
    {
        if (ResolvePortalId() is not { } portalId)
        {
            return this.ForbiddenProblem(TenantUnresolvedCode);
        }

        if (settings is null)
        {
            ModelState.AddModelError(string.Empty, "A request body is required and was not supplied.");

            return ValidationProblem(ModelState);
        }

        Result outcome = await _modules
            .UpdateModuleSettingsAsync(
                portalId,
                moduleId,
                tabModuleId,
                settings.ModuleSettings,
                settings.TabModuleSettings,
                cancellationToken)
            .ConfigureAwait(false);

        return this.Complete(outcome);
    }

    /// <summary>Exports a module's content.</summary>
    /// <param name="moduleId">The module identifier.</param>
    /// <param name="request">Names the file the caller intends to save the payload as.</param>
    /// <param name="cancellationToken">Abandons the request when the caller disconnects.</param>
    /// <returns>The exported payload as a downloadable document.</returns>
    // MIGRATION: H7b.
    [HttpPost("{moduleId:int}/export")]
    [Authorize(Policy = PolicyNames.ModuleEdit)]
    [RequestSizeLimit(ServiceCollectionExtensions.MaximumRequestBodyBytes)]
    [ProducesResponseType(typeof(string), StatusCodes.Status200OK, ExportContentType)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status500InternalServerError)]
    public async Task<ActionResult> ExportAsync(
        int moduleId,
        [FromBody] ModuleExportRequest request,
        CancellationToken cancellationToken)
    {
        if (ResolvePortalId() is not { } portalId)
        {
            return this.ForbiddenProblem(TenantUnresolvedCode);
        }

        if (request is null)
        {
            ModelState.AddModelError(string.Empty, "A request body is required and was not supplied.");

            return ValidationProblem(ModelState);
        }

        Result<string> outcome = await _modules
            .ExportModuleAsync(portalId, moduleId, request, cancellationToken)
            .ConfigureAwait(false);

        if (outcome.IsFailure)
        {
            // Routed through the shared translator rather than assembled here.
            return this.Failed(outcome);
        }

        return Content(outcome.Value, ExportContentType);
    }

    /// <summary>Imports content into a module.</summary>
    /// <param name="request">The module to import into, and the content to import.</param>
    /// <param name="cancellationToken">Abandons the request when the caller disconnects.</param>
    /// <returns><c>204 No Content</c> when the content has been imported.</returns>
    // MIGRATION: H8. THE DOCUMENT IS PARSED BY THE SERVICE, NEVER HERE. Import.ascx.vb read the file at L184 and
    //            then, at L188-L192, constructed an XmlDocument and called LoadXml inside a Try whose Catch
    //            produced the NotValidXml message; on success it read the type attribute at L196, compared it
    //            with the module's own name, read the version attribute at L198 and passed the document's
    //            InnerXml to ImportModule at L200. Every part of that is application behaviour: no XML type is
    //            constructed in this file, no attribute is read, and no parse is wrapped in a try/catch to shape
    //            an error. The service reports a malformed document as a failure reason and the shared status
    //            table answers it with 400, which is the caller-correctable classification the code earns by
    //            naming no request member. The mismatched-type and unsupported-module refusals arrive the same
    //            way. One argument disappears entirely rather than moving: the legacy call passed
    //            UserInfo.UserID as the acting account, which the service now takes from the authenticated
    //            caller, because an identifier a request could choose for itself would let one account attribute
    //            an import to another.
    //
    // MIGRATION: H9. THE TARGET ARRIVES IN THE BODY, WHICH IS WHAT DECIDES THIS ACTION'S POLICY. The route
    //            carries no module identifier, so the module view and module edit policies CANNOT be used here:
    //            their handler resolves the module from route data, finds nothing, and fails closed - it would
    //            refuse every import, at request time, with nothing at compile time to catch it. The
    //            portal-administrator policy is the correct grain because it evaluates against the portal the
    //            route does name, and the per-resource check on the module named in the body is the service's.
    //            The legacy page had no in-code role check of its own at all, taking its protection from the
    //            permissions of the administration page hosting it, so an explicit policy replaces an ambient
    //            one here rather than a stated one.
    //
    // MIGRATION: H10. THE BODY LIMIT IS THE IMPORT'S OWN, NOT THE GLOBAL ONE. Declaring the global
    //            one-mebibyte ceiling here contradicts the import contract, under which the service accepts
    //            a document of 1 048 576 CHARACTERS: a document at the accepted ceiling cannot fit through a
    //            mebibyte limit in front of it once JSON member names, quotes and escaping are counted, so
    //            the two numbers describe different contracts and the larger is unreachable. The limit
    //            declared here is computed from the
    //            document ceiling the import contract publishes, so the two cannot disagree, and it is
    //            declared PER ACTION rather than raised globally because every other endpoint - including
    //            the unauthenticated ones - is correctly bounded at a mebibyte. Reaching this allowance
    //            requires the tenant-administrator policy above.
    [HttpPost("import")]
    [Authorize(Policy = PolicyNames.PortalAdministrator)]
    [RequestSizeLimit(ServiceCollectionExtensions.MaximumImportRequestBodyBytes)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status500InternalServerError)]
    public async Task<ActionResult> ImportAsync(
        [FromBody] ModuleImportRequest request,
        CancellationToken cancellationToken)
    {
        if (ResolvePortalId() is not { } portalId)
        {
            return this.ForbiddenProblem(TenantUnresolvedCode);
        }

        if (request is null)
        {
            ModelState.AddModelError(string.Empty, "A request body is required and was not supplied.");

            return ValidationProblem(ModelState);
        }

        Result outcome = await _modules
            .ImportModuleAsync(portalId, request, cancellationToken)
            .ConfigureAwait(false);

        return this.Complete(outcome);
    }
}
