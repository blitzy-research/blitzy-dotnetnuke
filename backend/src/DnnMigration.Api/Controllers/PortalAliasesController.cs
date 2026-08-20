using Asp.Versioning;
using DnnMigration.Api.Authorization;
using DnnMigration.Api.ErrorHandling;
using DnnMigration.Api.Middleware;
using DnnMigration.Application.Abstractions;
using DnnMigration.Application.Dtos.Common;
using DnnMigration.Application.Dtos.Portal;
using DnnMigration.Domain.Common;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DnnMigration.Api.Controllers;

/// <summary>The portal alias resource: the host names by which a portal is reached.</summary>
/// <remarks>
/// <para>
/// <b>One canonical addressing family.</b> All five actions address one tenant's aliases beneath the portal
/// that owns them, at <c>portals/{portalId}/aliases</c>. No host-wide alias route is published:
/// installation-wide seam-repair addresses are outside the frozen API surface.
/// </para>
/// <para>
/// <b>Authority is declared per action.</b> The class attribute carries authentication only, and each of
/// the five actions names <see cref="PolicyNames.PortalAdministrator"/>, whose handler binds the tenant in
/// the route to the tenant the caller administers. A metadata test guards against a future action
/// inheriting authentication without naming its policy.
/// </para>
/// </remarks>
[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}")]
[Produces("application/json")]
[Authorize]
public sealed class PortalAliasesController : ControllerBase
{
    /// <summary>Reason the alias reads and writes are exempt from tenant resolution.</summary>
    /// <remarks>
    /// THE REPAIR PATH MUST NOT DEPEND ON WHAT IT REPAIRS. These actions are how an operator inspects and
    /// corrects the alias table, so requiring the host name to resolve against that table before they could
    /// be reached would make a mistyped alias unrecoverable: every route capable of fixing it would be
    /// refused for precisely the reason it needed fixing.
    /// </remarks>
    private const string AliasRepairJustification =
        "Alias administration: the path by which a wrong or missing alias is repaired, so it must not itself "
        + "require an alias to resolve. The portal is named by the route, and the authorisation policy still "
        + "binds a portal administrator to the tenant it arrived through.";
    /// <summary>The application-layer contract this controller delegates every decision to.</summary>
    private readonly IPortalService _portals;

    // NOTHING ELSE IS INJECTED EITHER. No repository, no unit of work, no database context, no cache, no
    // clock, and no ambient request-context accessor.
    /// <summary>Initialises a new instance of the <see cref="PortalAliasesController"/> class.</summary>
    /// <param name="portals">The application-layer contract for the portal aggregate and its aliases.</param>
    /// <exception cref="ArgumentNullException"><paramref name="portals"/> is <see langword="null"/>.</exception>
    public PortalAliasesController(IPortalService portals)
    {
        _portals = portals ?? throw new ArgumentNullException(nameof(portals));
    }

    /// <summary>Lists the aliases bound to one portal.</summary>
    /// <param name="portalId">Identifier of the portal whose aliases are wanted.</param>
    /// <param name="cancellationToken">Abandons the read when the caller disconnects.</param>
    /// <returns>The portal's aliases, in the shared success envelope.</returns>
    [HttpGet("portals/{portalId:int}/aliases")]
    [Authorize(Policy = PolicyNames.PortalAdministrator)]
    [TenantOptional(AliasRepairJustification)]
    [ProducesResponseType(typeof(ApiResponse<IReadOnlyList<PortalAliasDto>>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ApiResponse<IReadOnlyList<PortalAliasDto>>>> ListForPortalAsync(
        int portalId,
        CancellationToken cancellationToken)
    {
        Result<IReadOnlyList<PortalAliasDto>> outcome = await _portals
            .ListPortalAliasesAsync(portalId, cancellationToken)
            .ConfigureAwait(false);

        return this.Complete(outcome);
    }

    /// <summary>Binds a new alias to a portal.</summary>
    /// <param name="portalId">Identifier of the portal to bind the host name to.</param>
    /// <param name="request">The host name to bind, and nothing else.</param>
    /// <param name="cancellationToken">Abandons the work when the caller disconnects.</param>
    /// <returns>The created alias, with the address of the new resource in the location header.</returns>
    [HttpPost("portals/{portalId:int}/aliases")]
    [Authorize(Policy = PolicyNames.PortalAdministrator)]
    [ProducesResponseType(typeof(ApiResponse<PortalAliasDto>), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<ActionResult<ApiResponse<PortalAliasDto>>> AddAsync(
        int portalId,
        [FromBody] CreatePortalAliasRequest request,
        CancellationToken cancellationToken)
    {
        Result<PortalAliasDto> outcome = await _portals
            .AddPortalAliasAsync(portalId, request, cancellationToken)
            .ConfigureAwait(false);

        return this.Created(outcome, created => created.PortalAliasId);
    }

    // THE CANONICAL PORTAL-NESTED RESOURCE. Each route names the owning tenant, so the portal-administrator
    // policy can bind the request to it and the service can compare the same tenant against the alias's
    // stored owner before reporting or writing anything.

    /// <summary>Reads one alias of one portal.</summary>
    /// <param name="portalId">Identifier of the portal that owns the alias.</param>
    /// <param name="portalAliasId">Identifier of the alias to read.</param>
    /// <param name="cancellationToken">Abandons the read when the caller disconnects.</param>
    /// <returns>The alias, in the shared success envelope.</returns>
    [HttpGet("portals/{portalId:int}/aliases/{portalAliasId:int}")]
    [Authorize(Policy = PolicyNames.PortalAdministrator)]
    [TenantOptional(AliasRepairJustification)]
    [ProducesResponseType(typeof(ApiResponse<PortalAliasDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ApiResponse<PortalAliasDto?>>> GetForPortalAsync(
        int portalId,
        int portalAliasId,
        CancellationToken cancellationToken)
    {
        Result<PortalAliasDto?> outcome = await _portals
            .GetPortalAliasAsync(portalId, portalAliasId, cancellationToken)
            .ConfigureAwait(false);

        return this.Complete(outcome);
    }

    /// <summary>Changes the host name of one alias of one portal.</summary>
    /// <param name="portalId">Identifier of the portal that owns the alias.</param>
    /// <param name="portalAliasId">Identifier of the alias to change.</param>
    /// <param name="request">The host name to store in place of the current one.</param>
    /// <param name="cancellationToken">Abandons the work when the caller disconnects.</param>
    /// <returns><c>200 OK</c> carrying the alias as it now stands.</returns>
    [HttpPut("portals/{portalId:int}/aliases/{portalAliasId:int}")]
    [Authorize(Policy = PolicyNames.PortalAdministrator)]
    [TenantOptional(AliasRepairJustification)]
    [ProducesResponseType(typeof(ApiResponse<PortalAliasDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<ActionResult<ApiResponse<PortalAliasDto>>> UpdateForPortalAsync(
        int portalId,
        int portalAliasId,
        [FromBody] UpdatePortalAliasRequest request,
        CancellationToken cancellationToken)
    {
        Result<PortalAliasDto> outcome = await _portals
            .UpdatePortalAliasAsync(portalId, portalAliasId, request, cancellationToken)
            .ConfigureAwait(false);

        return this.Complete(outcome);
    }

    /// <summary>Unbinds one alias of one portal.</summary>
    /// <param name="portalId">Identifier of the portal that owns the alias.</param>
    /// <param name="portalAliasId">Identifier of the alias to unbind.</param>
    /// <param name="cancellationToken">Abandons the work when the caller disconnects.</param>
    /// <returns><c>204 No Content</c> when the alias has been unbound.</returns>
    [HttpDelete("portals/{portalId:int}/aliases/{portalAliasId:int}")]
    [Authorize(Policy = PolicyNames.PortalAdministrator)]
    [TenantOptional(AliasRepairJustification)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<ActionResult> DeleteForPortalAsync(
        int portalId,
        int portalAliasId,
        CancellationToken cancellationToken)
    {
        Result outcome = await _portals
            .DeletePortalAliasAsync(portalId, portalAliasId, cancellationToken)
            .ConfigureAwait(false);

        return this.Complete(outcome);
    }
}
