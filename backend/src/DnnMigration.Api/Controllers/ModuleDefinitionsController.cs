using Asp.Versioning;
using DnnMigration.Api.Authorization;
using DnnMigration.Api.ErrorHandling;
using DnnMigration.Application.Abstractions;
using DnnMigration.Application.Dtos.Common;
using DnnMigration.Application.Dtos.Module;
using DnnMigration.Domain.Abstractions.Services;
using DnnMigration.Domain.Common;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DnnMigration.Api.Controllers;

/// <summary>The module definition catalogue: which kinds of module this portal may place on a page.</summary>
/// <remarks>
/// MIGRATION: the legacy gate answered a refusal with <c>Response.Redirect(NavigateURL("Access Denied"),
/// True)</c>, which rendered a localised message page. A JSON API has no page to redirect to, so the target
/// answers a plain <c>403 Forbidden</c> from the authorisation middleware and the redirect is not
/// reproduced.
/// </remarks>
[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/module-definitions")]
// Declaring tenant administration here therefore refused a caller the legacy admitted, and refused them in
// the way hardest to see: the create ACTION carries no policy at all, deliberately, so a page administrator
// reached the placement form and was then handed an empty type selector by a supporting read they were not
// permitted to make.
[Authorize(Policy = PolicyNames.PortalContentEditor)]
[Produces("application/json")]
public sealed class ModuleDefinitionsController : ControllerBase
{
    /// <summary>
    /// Failure code carried as the problem type when the request reached this action without a tenant.
    /// </summary>
    /// <remarks>
    /// A distinct code from the middleware's generic refusal, so an operator reading a support log can tell
    /// "this host resolves to no portal" apart from "this caller lacks the grant" - while the caller reads
    /// the same fixed wording either way and learns nothing from the difference.
    /// </remarks>
    private const string TenantUnresolvedCode = "portal.tenant_unresolved";

    /// <summary>The catalogue reader this controller delegates to.</summary>
    private readonly IModuleService _modules;

    /// <summary>The tenant this request addresses, resolved from the request host.</summary>
    private readonly IPortalContextHolder _portalContext;

    /// <summary>Initialises a new instance of the <see cref="ModuleDefinitionsController"/> class.</summary>
    /// <param name="modules">The module service, which owns the definition catalogue.</param>
    /// <param name="portalContext">
    /// Holds the tenant that the alias-resolution middleware resolved from the request host.
    /// </param>
    /// <exception cref="ArgumentNullException">Either argument is <see langword="null"/>.</exception>
    /// <remarks>
    /// Two dependencies, both contracts rather than implementations, and neither is a persistence type: the
    /// repositories and the persistence context are unreachable from this layer by design. There is no
    /// separate definition or desktop-module service to inject - the definition catalogue is part of the
    /// module service's surface, because a definition is only ever read in order to place a module.
    /// </remarks>
    public ModuleDefinitionsController(IModuleService modules, IPortalContextHolder portalContext)
    {
        _modules = modules ?? throw new ArgumentNullException(nameof(modules));
        _portalContext = portalContext ?? throw new ArgumentNullException(nameof(portalContext));
    }

    /// <summary>Lists the module definitions the addressed portal may instantiate.</summary>
    /// <param name="cancellationToken">Abandons the read when the caller disconnects.</param>
    /// <returns>
    /// The definitions available to the tenant this request addresses, in a stable order, each joined to
    /// the identity and capability facts of its owning desktop module.
    /// </returns>
    /// <remarks>
    /// Paging is deliberately absent. The catalogue is small, bounded reference data written only by module
    /// installation, so the whole sequence is returned rather than a page of it - which is also what the
    /// legacy reads did, each returning an entire collection.
    /// </remarks>
    [HttpGet]
    [ProducesResponseType(typeof(ApiResponse<IReadOnlyList<ModuleDefinitionDto>>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<ApiResponse<IReadOnlyList<ModuleDefinitionDto>>>> ListAsync(
        CancellationToken cancellationToken)
    {
        if (!_portalContext.IsResolved)
        {
            return this.ForbiddenProblem(TenantUnresolvedCode);
        }

        // The portal identifier is forwarded exactly as resolved. Clamping it, or treating any particular
        // value as "absent", would be a second and quieter copy of a rule that belongs to the tenant
        // boundary - and -1 is a genuine portal here, so such a copy would be wrong.
        Result<IReadOnlyList<ModuleDefinitionDto>> outcome = await _modules
            .ListModuleDefinitionsAsync(_portalContext.Current.PortalId, cancellationToken)
            .ConfigureAwait(false);

        // Complete tests the outcome before reading its value, so a failed outcome never has its value
        // touched, and maps any failure code to a status through the one table this API uses.
        return this.Complete(outcome);
    }

    /// <summary>Reads one module definition available to the addressed portal.</summary>
    /// <param name="moduleDefinitionId">Identifier of the definition wanted.</param>
    /// <param name="cancellationToken">Abandons the read when the caller disconnects.</param>
    /// <returns>The definition.</returns>
    [HttpGet("{moduleDefinitionId:int}")]
    [ProducesResponseType(typeof(ApiResponse<ModuleDefinitionDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ApiResponse<ModuleDefinitionDto?>>> GetAsync(
        int moduleDefinitionId,
        CancellationToken cancellationToken)
    {
        // The same precondition the catalogue read states: the holder throws rather than returning a
        // placeholder tenant, and under the class-level policy an unresolved host has already been refused.
        if (!_portalContext.IsResolved)
        {
            return this.ForbiddenProblem(TenantUnresolvedCode);
        }

        Result<ModuleDefinitionDto?> outcome = await _modules
            .GetModuleDefinitionAsync(_portalContext.Current.PortalId, moduleDefinitionId, cancellationToken)
            .ConfigureAwait(false);

        // A successful outcome carrying no value means the definition is not available here, which the
        // shared translator answers as 404 - the convention every single-record read in this API follows.
        return this.Complete(outcome);
    }

    /// <summary>Lists the definitions one installed package declares for the addressed portal.</summary>
    /// <param name="desktopModuleId">Identifier of the installed package whose definitions are wanted.</param>
    /// <param name="cancellationToken">Abandons the read when the caller disconnects.</param>
    /// <returns>The definitions that package declares, in the catalogue's own order.</returns>
    // Every advertised refusal declares a body, and the shape is the general problem document rather than
    // the validation one: this action's request carries no content and no query, so there are no request
    // members a validation document could name - a path segment constrained to an integer refuses a value
    // of the wrong shape before the action is reached at all.
    [HttpGet("desktop-modules/{desktopModuleId:int}")]
    [ProducesResponseType(typeof(ApiResponse<IReadOnlyList<ModuleDefinitionDto>>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<ApiResponse<IReadOnlyList<ModuleDefinitionDto>>>> ListForDesktopModuleAsync(
        int desktopModuleId,
        CancellationToken cancellationToken)
    {
        if (!_portalContext.IsResolved)
        {
            return this.ForbiddenProblem(TenantUnresolvedCode);
        }

        Result<IReadOnlyList<ModuleDefinitionDto>> outcome = await _modules
            .ListDesktopModuleDefinitionsAsync(
                _portalContext.Current.PortalId,
                desktopModuleId,
                cancellationToken)
            .ConfigureAwait(false);

        return this.Complete(outcome);
    }
}
