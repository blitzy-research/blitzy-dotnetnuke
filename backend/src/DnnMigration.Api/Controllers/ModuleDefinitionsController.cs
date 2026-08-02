using Asp.Versioning;
using DnnMigration.Api.Authorization;
using DnnMigration.Api.ErrorHandling;
using DnnMigration.Api.Filters;
using DnnMigration.Application.Abstractions;
using DnnMigration.Application.Dtos.Common;
using DnnMigration.Application.Dtos.Module;
using DnnMigration.Domain.Common;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DnnMigration.Api.Controllers;

/// <summary>
/// The module definition lookup: which kinds of module this portal may place.
/// </summary>
/// <remarks>
/// <para>
/// MIGRATION: replaces the read paths of <c>Library/Components/Modules/DesktopModuleController.vb</c> and
/// <c>Definitions/ModuleDefinitionController.vb</c>.
/// </para>
/// <para>
/// <strong>Read-only, deliberately.</strong> Installing a desktop module means writing files into the
/// application directory and registering a definition - the legacy module-installer path, which this
/// migration places out of scope. Exposing a write endpoint here would be an invitation to reimplement it.
/// </para>
/// <para>
/// The list is portal-scoped rather than host-wide because premium definitions are granted to individual
/// portals: the same host can offer a different catalogue to each tenant, and a host-wide list would show a
/// caller definitions they cannot use.
/// </para>
/// </remarks>
[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/portals/{portalId:int}/module-definitions")]
[Produces("application/json")]
[Authorize]
public sealed class ModuleDefinitionsController : ControllerBase
{
    private readonly IModuleService _modules;

    /// <summary>Initialises a new instance of the <see cref="ModuleDefinitionsController"/> class.</summary>
    /// <param name="modules">The module service.</param>
    /// <exception cref="ArgumentNullException"><paramref name="modules"/> is <see langword="null"/>.</exception>
    public ModuleDefinitionsController(IModuleService modules)
    {
        _modules = modules ?? throw new ArgumentNullException(nameof(modules));
    }

    /// <summary>Lists the module definitions available to a portal.</summary>
    /// <param name="portalId">The portal identifier.</param>
    /// <param name="cancellationToken">Abandons the request when the caller disconnects.</param>
    /// <returns>The definitions the portal may place, in display order.</returns>
    [HttpGet]
    [ProducesResponseType(typeof(IReadOnlyList<ModuleDefinitionDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<IReadOnlyList<ModuleDefinitionDto>>> ListAsync(
        int portalId,
        CancellationToken cancellationToken)
    {
        Result<IReadOnlyList<ModuleDefinitionDto>> outcome = await _modules
            .ListModuleDefinitionsAsync(portalId, cancellationToken)
            .ConfigureAwait(false);

        return this.Complete(outcome);
    }
}
