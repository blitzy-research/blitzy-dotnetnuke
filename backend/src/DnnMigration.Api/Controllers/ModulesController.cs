using Asp.Versioning;
using DnnMigration.Api.Authorization;
using DnnMigration.Api.ErrorHandling;
using DnnMigration.Api.Filters;
using DnnMigration.Application.Abstractions;
using DnnMigration.Application.Dtos.Common;
using DnnMigration.Application.Dtos.Module;
using DnnMigration.Domain.Common;
using FluentValidation;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DnnMigration.Api.Controllers;

/// <summary>
/// The module resource: the pluggable content component and its placements.
/// </summary>
/// <remarks>
/// <para>
/// MIGRATION: replaces <c>Website/admin/Modules/ModuleSettings.ascx.vb</c>, <c>Export.ascx.vb</c> and
/// <c>Import.ascx.vb</c>. The two file operations were postback pages that wrote to and read from the portal
/// home directory; they are now explicit calls that carry their content in the request and response.
/// </para>
/// <para>
/// <strong>Every path carries its portal.</strong> The tenant is a route segment rather than something
/// inferred from the caller's token or from the request host, and that is the tenant-isolation requirement
/// expressed in the URL: a module identifier alone does not say which portal it belongs to, so a service
/// that took only the identifier would be one query away from returning another tenant's module. The
/// authorisation handler reads this same <c>portalId</c> segment and prefers it over the value in the token,
/// so a caller who tampers with it is checked against the portal they asked for rather than the one they
/// signed into.
/// </para>
/// <para>
/// The single-module endpoints carry the module view and module edit policies, which is what those policies
/// exist for; they read the <c>moduleId</c> route value, so that segment name is part of the contract with
/// the handler. The listing carries the portal-administrator policy, because a portal-wide read has no module
/// identifier for a per-module policy to evaluate.
/// </para>
/// <para>
/// Creation and import are the two actions whose target arrives in the request BODY - the page a module is
/// placed on, and the module content is imported into - so no route-reading PERMISSION policy can reach
/// either, and the per-resource permission check is performed by the service after binding. That statement
/// used to appear here while being untrue: the service verified only that the target belonged to the tenant
/// and never evaluated a permission, so any authenticated caller could place a module on any tenant's page or
/// overwrite any tenant's module content. The service now performs the check it is credited with.
/// </para>
/// <para>
/// These two actions nonetheless carry the TENANT-BOUND policy, and that is not the permission check
/// duplicated in the wrong place. The route names a tenant while the caller's grants belong to whichever
/// tenant issued its token, so without it a caller holding a grant in its own tenant would have that grant
/// judged against the NAMED tenant's resources - the cross-tenant reach that binding the route to the caller's
/// tenant exists to close. The policy answers "may this caller act in this tenant at all"; the service answers
/// "may it act on this resource". Neither subsumes the other, and administering a tenant does not imply a
/// grant on a module within it: only a host account is answered affirmatively without one.
/// </para>
/// </remarks>
[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/portals/{portalId:int}/modules")]
[Produces("application/json")]
public sealed class ModulesController : ControllerBase
{
    /// <summary>The media type an exported module payload is returned as.</summary>
    /// <remarks>
    /// The legacy export wrote an XML document to a file in the portal home directory, and the content a
    /// module produces is unchanged by this migration, so the payload is still XML. Returning it as XML
    /// rather than as a JSON string means a caller can save the response body directly, exactly as the
    /// legacy page produced a saveable file.
    /// </remarks>
    private const string ExportContentType = "application/xml";

    private readonly IModuleService _modules;

    // NO VALIDATOR IS INJECTED, AND THAT IS THE POINT. Every request contract this controller binds is
    // validated by FluentValidationActionFilter, which is registered once for the whole API, runs before
    // the action and resolves a validator from each argument's declared type. This controller used to
    // take validators of its own and invoke them by hand as well, which was a second invocation path for
    // one rule set and the reason the paging contract was judged against the wrong sortable vocabulary.
    // Adding a validator argument back here would recreate that split.
    /// <summary>Initialises a new instance of the <see cref="ModulesController"/> class.</summary>
    /// <param name="modules">The module service.</param>
    /// <exception cref="ArgumentNullException"><paramref name="modules"/> is <see langword="null"/>.</exception>
    public ModulesController(IModuleService modules)
    {
        _modules = modules ?? throw new ArgumentNullException(nameof(modules));
    }

    /// <summary>Lists a portal's modules.</summary>
    /// <param name="portalId">The portal identifier.</param>
    /// <param name="request">Paging and sorting arguments, bound from the query string.</param>
    /// <param name="tabId">Restricts the result to modules placed on one tab.</param>
    /// <param name="includeDeleted">Includes modules that are in the recycle bin.</param>
    /// <param name="cancellationToken">Abandons the request when the caller disconnects.</param>
    /// <returns>A page of modules.</returns>
    // TENANT-BOUND, NOT MERELY AUTHENTICATED. This action inherited only the class-level authentication
    // requirement, so any bearer token could name any portal in the route and enumerate that tenant's
    // modules. The per-module policies below cannot serve a listing, because they evaluate a permission
    // against a module identifier and a listing has none; the portal-administrator policy is the right
    // grain for a portal-wide read and is anchored to the portal this route names.
    [HttpGet]
    [Authorize(Policy = PolicyNames.PortalAdministrator)]
    [ProducesResponseType(typeof(PagedResponse<ModuleListItemDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<PagedResponse<ModuleListItemDto>>> ListAsync(
        int portalId,
        [FromQuery] ModulePagedRequest request,
        [FromQuery] int? tabId,
        [FromQuery] bool includeDeleted,
        CancellationToken cancellationToken)
    {
        // The paging contract is validated by the globally registered validation filter, which now
        // resolves ModulePagedRequestValidator from this parameter's type and applies the module
        // collection's own sortable set. This action used to invoke IValidator<PagedRequest> by hand,
        // which was a second invocation path AND the wrong rules: that contract resolves the
        // unspecialised validator, whose sortable set is the union of every collection's, so a portal
        // or account field name was accepted here and then discarded by the listing.
        Result<PagedResult<ModuleListItemDto>> outcome = await _modules
            .ListModulesAsync(portalId, request, tabId, includeDeleted, cancellationToken)
            .ConfigureAwait(false);

        // Projected onto the wire envelope here rather than returned as the domain page. CompletePage
        // applies PagedResponse<T>.From, so the response carries `items` plus `meta` and the domain
        // paging type never crosses the boundary.
        return this.CompletePage(outcome);
    }

    /// <summary>Retrieves one module.</summary>
    /// <param name="portalId">The portal identifier.</param>
    /// <param name="moduleId">The module identifier.</param>
    /// <param name="tabModuleId">Selects one placement when the module appears on several tabs.</param>
    /// <param name="cancellationToken">Abandons the request when the caller disconnects.</param>
    /// <returns>The module, or <c>404 Not Found</c> when it does not exist in this portal.</returns>
    [HttpGet("{moduleId:int}")]
    [Authorize(Policy = PolicyNames.ModuleView)]
    [ProducesResponseType(typeof(ApiResponse<ModuleDetailDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ApiResponse<ModuleDetailDto?>>> GetAsync(
        int portalId,
        int moduleId,
        [FromQuery] int? tabModuleId,
        CancellationToken cancellationToken)
    {
        Result<ModuleDetailDto?> outcome = await _modules
            .GetModuleAsync(portalId, moduleId, tabModuleId, cancellationToken)
            .ConfigureAwait(false);

        return this.Complete(outcome);
    }

    /// <summary>Creates a module and places it.</summary>
    /// <param name="portalId">The portal identifier.</param>
    /// <param name="request">The module to create.</param>
    /// <param name="cancellationToken">Abandons the request when the caller disconnects.</param>
    /// <returns>The created module, with its address in the location header.</returns>
    [HttpPost]
    [ProducesResponseType(typeof(ApiResponse<ModuleDetailDto>), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ApiResponse<ModuleDetailDto>>> CreateAsync(
        int portalId,
        [FromBody] CreateModuleRequest request,
        CancellationToken cancellationToken)
    {
        // VALIDATED BY THE GLOBALLY REGISTERED FILTER, NOT HERE. FluentValidationActionFilter runs
        // before every action, resolves a validator from each bound argument's declared type and
        // short-circuits with the same RFC 7807 validation document this call used to build - so the
        // block that used to stand here could never fire. It was a second invocation path for one
        // rule set, which is exactly what the review asked to be collapsed: two paths are two places
        // for the rules, the context and the failure shape to diverge, and the one written by hand
        // reached the wrong validator on the paging contract.
        Result<ModuleDetailDto> outcome = await _modules
            .CreateModuleAsync(portalId, request, cancellationToken)
            .ConfigureAwait(false);

        return this.Created(outcome, created => created.ModuleId);
    }

    /// <summary>Updates a module.</summary>
    /// <param name="portalId">The portal identifier.</param>
    /// <param name="moduleId">The module identifier.</param>
    /// <param name="request">The new state.</param>
    /// <param name="cancellationToken">Abandons the request when the caller disconnects.</param>
    /// <returns>The updated module, or <c>404 Not Found</c> when it does not exist in this portal.</returns>
    [HttpPut("{moduleId:int}")]
    [Authorize(Policy = PolicyNames.ModuleEdit)]
    [ProducesResponseType(typeof(ApiResponse<ModuleDetailDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ApiResponse<ModuleDetailDto?>>> UpdateAsync(
        int portalId,
        int moduleId,
        [FromBody] UpdateModuleRequest request,
        CancellationToken cancellationToken)
    {
        // VALIDATED BY THE GLOBALLY REGISTERED FILTER, NOT HERE. FluentValidationActionFilter runs
        // before every action, resolves a validator from each bound argument's declared type and
        // short-circuits with the same RFC 7807 validation document this call used to build - so the
        // block that used to stand here could never fire. It was a second invocation path for one
        // rule set, which is exactly what the review asked to be collapsed: two paths are two places
        // for the rules, the context and the failure shape to diverge, and the one written by hand
        // reached the wrong validator on the paging contract.
        Result<ModuleDetailDto?> outcome = await _modules
            .UpdateModuleAsync(portalId, moduleId, request, cancellationToken)
            .ConfigureAwait(false);

        return this.Complete(outcome);
    }

    /// <summary>Deletes a module, or removes one of its placements.</summary>
    /// <param name="portalId">The portal identifier.</param>
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
        int portalId,
        int moduleId,
        [FromQuery] int? tabModuleId,
        CancellationToken cancellationToken)
    {
        Result outcome = await _modules
            .DeleteModuleAsync(portalId, moduleId, tabModuleId, cancellationToken)
            .ConfigureAwait(false);

        return this.Complete(outcome);
    }

    /// <summary>Retrieves a module's settings.</summary>
    /// <param name="portalId">The portal identifier.</param>
    /// <param name="moduleId">The module identifier.</param>
    /// <param name="tabModuleId">Selects one placement when the module appears on several tabs.</param>
    /// <param name="cancellationToken">Abandons the request when the caller disconnects.</param>
    /// <returns>The settings, or <c>404 Not Found</c> when the module does not exist in this portal.</returns>
    [HttpGet("{moduleId:int}/settings")]
    [Authorize(Policy = PolicyNames.ModuleView)]
    [ProducesResponseType(typeof(ApiResponse<ModuleSettingsDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ApiResponse<ModuleSettingsDto?>>> GetSettingsAsync(
        int portalId,
        int moduleId,
        [FromQuery] int? tabModuleId,
        CancellationToken cancellationToken)
    {
        Result<ModuleSettingsDto?> outcome = await _modules
            .GetModuleSettingsAsync(portalId, moduleId, tabModuleId, cancellationToken)
            .ConfigureAwait(false);

        return this.Complete(outcome);
    }

    /// <summary>Replaces a module's settings.</summary>
    /// <param name="portalId">The portal identifier.</param>
    /// <param name="moduleId">The module identifier.</param>
    /// <param name="settings">The settings to store.</param>
    /// <param name="tabModuleId">Selects one placement when the module appears on several tabs.</param>
    /// <param name="cancellationToken">Abandons the request when the caller disconnects.</param>
    /// <returns><c>204 No Content</c> when the settings have been stored.</returns>
    /// <remarks>
    /// The two dictionaries are lifted out of the request object and passed as the service's own two
    /// arguments. That is unwrapping the transport shape, not a decision: the service takes them separately
    /// because they land in two different tables - one keyed by the module, one by the placement - and
    /// merging them here would lose which is which.
    /// </remarks>
    [HttpPut("{moduleId:int}/settings")]
    [Authorize(Policy = PolicyNames.ModuleEdit)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<ActionResult> UpdateSettingsAsync(
        int portalId,
        int moduleId,
        [FromBody] ModuleSettingsDto settings,
        [FromQuery] int? tabModuleId,
        CancellationToken cancellationToken)
    {
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
    /// <param name="portalId">The portal identifier.</param>
    /// <param name="moduleId">The module identifier.</param>
    /// <param name="request">Names the file the caller intends to save the payload as.</param>
    /// <param name="cancellationToken">Abandons the request when the caller disconnects.</param>
    /// <returns>The exported payload as a downloadable document.</returns>
    /// <remarks>
    /// A module that declares no content-portability contract exports successfully with an empty payload,
    /// which is the legacy behaviour: the export page produced a file whether or not the module had anything
    /// to put in it. An empty body and a failure are therefore different answers here, and the distinction is
    /// preserved rather than collapsed into a 404.
    /// </remarks>
    // The success media type is declared PER RESPONSE rather than with a Produces attribute on the action,
    // and the difference is load-bearing rather than stylistic. Produces rewrites the permitted media types
    // of any ObjectResult the action returns, so declaring application/xml here forced the FAILURE response
    // - a problem document, which is an ObjectResult - to be negotiated as XML. No XML formatter is
    // registered, so every refusal from this one action answered 406 Not Acceptable with no body instead of
    // the problem document it advertises. Stating the media type on the 200 response alone documents the
    // XML payload accurately in the published description while leaving the class-level JSON negotiation to
    // carry the failures, which is what makes this action's errors identical to every other action's. The
    // success path returns a content result, which no negotiation touches.
    [HttpPost("{moduleId:int}/export")]
    [Authorize(Policy = PolicyNames.ModuleEdit)]
    [ProducesResponseType(typeof(string), StatusCodes.Status200OK, ExportContentType)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status500InternalServerError)]
    public async Task<ActionResult> ExportAsync(
        int portalId,
        int moduleId,
        [FromBody] ModuleExportRequest request,
        CancellationToken cancellationToken)
    {
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
            // Routed through the shared translator rather than assembled here. A hand-built payload
            // omitted the problem type and the trace identifier that every other failure in this API
            // carries, and it fixed a title of its own that disagreed with the vocabulary the status code
            // is registered under - so a client parsing this API's errors had to special-case one action.
            // The status code comes from the same table as everywhere else, which is also what promotes a
            // module-execution fault out of the caller-correctable range.
            return this.Failed(outcome);
        }

        return Content(outcome.Value, ExportContentType);
    }

    /// <summary>Imports content into a module.</summary>
    /// <param name="portalId">The portal identifier.</param>
    /// <param name="request">The module to import into, and the content to import.</param>
    /// <param name="cancellationToken">Abandons the request when the caller disconnects.</param>
    /// <returns><c>204 No Content</c> when the content has been imported.</returns>
    /// <remarks>
    /// The target module travels in the body rather than in the path because the legacy import page chose its
    /// target from a list on the form, and because an import addressed at a module in the URL would read as
    /// idempotent when it is not.
    /// </remarks>
    [HttpPost("import")]
    [Authorize(Policy = PolicyNames.PortalAdministrator)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status500InternalServerError)]
    public async Task<ActionResult> ImportAsync(
        int portalId,
        [FromBody] ModuleImportRequest request,
        CancellationToken cancellationToken)
    {
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
