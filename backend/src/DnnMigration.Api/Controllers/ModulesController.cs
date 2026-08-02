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
/// the handler. Creation and import carry no module identifier yet, so there is nothing for those policies
/// to evaluate and the service performs the check - which is also why this file must not attempt one.
/// </para>
/// </remarks>
[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/portals/{portalId:int}/modules")]
[Produces("application/json")]
[Authorize]
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
    private readonly IValidator<PagedRequest> _pageValidator;
    private readonly IValidator<CreateModuleRequest> _createValidator;
    private readonly IValidator<UpdateModuleRequest> _updateValidator;

    /// <summary>Initialises a new instance of the <see cref="ModulesController"/> class.</summary>
    /// <param name="modules">The module service.</param>
    /// <param name="pageValidator">Validates paging arguments.</param>
    /// <param name="createValidator">Validates a creation request.</param>
    /// <param name="updateValidator">Validates an update request.</param>
    /// <exception cref="ArgumentNullException">Any argument is <see langword="null"/>.</exception>
    public ModulesController(
        IModuleService modules,
        IValidator<PagedRequest> pageValidator,
        IValidator<CreateModuleRequest> createValidator,
        IValidator<UpdateModuleRequest> updateValidator)
    {
        _modules = modules ?? throw new ArgumentNullException(nameof(modules));
        _pageValidator = pageValidator ?? throw new ArgumentNullException(nameof(pageValidator));
        _createValidator = createValidator ?? throw new ArgumentNullException(nameof(createValidator));
        _updateValidator = updateValidator ?? throw new ArgumentNullException(nameof(updateValidator));
    }

    /// <summary>Lists a portal's modules.</summary>
    /// <param name="portalId">The portal identifier.</param>
    /// <param name="request">Paging and sorting arguments, bound from the query string.</param>
    /// <param name="tabId">Restricts the result to modules placed on one tab.</param>
    /// <param name="includeDeleted">Includes modules that are in the recycle bin.</param>
    /// <param name="cancellationToken">Abandons the request when the caller disconnects.</param>
    /// <returns>A page of modules.</returns>
    [HttpGet]
    [ProducesResponseType(typeof(PagedResult<ModuleListItemDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<PagedResult<ModuleListItemDto>>> ListAsync(
        int portalId,
        [FromQuery] PagedRequest request,
        [FromQuery] int? tabId,
        [FromQuery] bool includeDeleted,
        CancellationToken cancellationToken)
    {
        ActionResult? invalid = await this
            .ValidateRequestAsync(_pageValidator, request, cancellationToken)
            .ConfigureAwait(false);

        if (invalid is not null)
        {
            return invalid;
        }

        Result<PagedResult<ModuleListItemDto>> outcome = await _modules
            .ListModulesAsync(portalId, request, tabId, includeDeleted, cancellationToken)
            .ConfigureAwait(false);

        return this.Complete(outcome);
    }

    /// <summary>Retrieves one module.</summary>
    /// <param name="portalId">The portal identifier.</param>
    /// <param name="moduleId">The module identifier.</param>
    /// <param name="tabModuleId">Selects one placement when the module appears on several tabs.</param>
    /// <param name="cancellationToken">Abandons the request when the caller disconnects.</param>
    /// <returns>The module, or <c>404 Not Found</c> when it does not exist in this portal.</returns>
    [HttpGet("{moduleId:int}")]
    [Authorize(Policy = PolicyNames.ModuleView)]
    [ProducesResponseType(typeof(ModuleDetailDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ModuleDetailDto?>> GetAsync(
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
    [ProducesResponseType(typeof(ModuleDetailDto), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<ModuleDetailDto>> CreateAsync(
        int portalId,
        [FromBody] CreateModuleRequest request,
        CancellationToken cancellationToken)
    {
        ActionResult? invalid = await this
            .ValidateRequestAsync(_createValidator, request, cancellationToken)
            .ConfigureAwait(false);

        if (invalid is not null)
        {
            return invalid;
        }

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
    [ProducesResponseType(typeof(ModuleDetailDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ModuleDetailDto?>> UpdateAsync(
        int portalId,
        int moduleId,
        [FromBody] UpdateModuleRequest request,
        CancellationToken cancellationToken)
    {
        ActionResult? invalid = await this
            .ValidateRequestAsync(_updateValidator, request, cancellationToken)
            .ConfigureAwait(false);

        if (invalid is not null)
        {
            return invalid;
        }

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
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
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
    [ProducesResponseType(typeof(ModuleSettingsDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ModuleSettingsDto?>> GetSettingsAsync(
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
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
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
    [HttpPost("{moduleId:int}/export")]
    [Authorize(Policy = PolicyNames.ModuleEdit)]
    [Produces(ExportContentType)]
    [ProducesResponseType(typeof(string), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
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
            return StatusCode(
                ApiResults.MapStatusCode(outcome.Error?.Code),
                new ProblemDetails
                {
                    Status = ApiResults.MapStatusCode(outcome.Error?.Code),
                    Detail = outcome.Error?.Message,
                    Title = "The module could not be exported.",
                });
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
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
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
