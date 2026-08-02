using Asp.Versioning;
using DnnMigration.Api.Authorization;
using DnnMigration.Api.ErrorHandling;
using DnnMigration.Api.Filters;
using DnnMigration.Application.Abstractions;
using DnnMigration.Application.Dtos.Common;
using DnnMigration.Application.Dtos.Portal;
using DnnMigration.Domain.Common;
using FluentValidation;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DnnMigration.Api.Controllers;

/// <summary>
/// The portal alias resource: the host names that resolve to a portal.
/// </summary>
/// <remarks>
/// <para>
/// MIGRATION: replaces <c>Website/admin/Portal/PortalAlias.ascx.vb</c> and
/// <c>EditPortalAlias.ascx.vb</c>.
/// </para>
/// <para>
/// The collection is nested under its portal, because an alias only exists in the context of one. The
/// individual alias is addressed at its own top-level path instead, and that asymmetry is deliberate rather
/// than sloppy: the service members that read, update and delete an alias identify it by its own key alone
/// and take no portal, so nesting them would put a segment in the URL that nothing validates and that a
/// caller could therefore set to any portal at all without being told they were wrong.
/// </para>
/// <para>
/// The write actions take dedicated request contracts carrying the host name alone, each with its own
/// declarative validator, and the read representation is never accepted as input. MIGRATION: accepting the
/// read DTO on a write let a caller supply an alias key that a create ignores and a portal key that an
/// update refuses to re-bind, so a request could name a tenant it was silently not given. Shape, length and
/// canonical form are settled declaratively here; uniqueness remains a database question and is answered by
/// the service, which reports it as a conflict.
/// </para>
/// </remarks>
[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}")]
[Produces("application/json")]
[Authorize(Policy = PolicyNames.PortalAdministrator)]
public sealed class PortalAliasesController : ControllerBase
{
    private readonly IPortalService _portals;
    private readonly IValidator<CreatePortalAliasRequest> _createValidator;
    private readonly IValidator<UpdatePortalAliasRequest> _updateValidator;

    /// <summary>Initialises a new instance of the <see cref="PortalAliasesController"/> class.</summary>
    /// <param name="portals">The portal service.</param>
    /// <param name="createValidator">Validates a submitted create request.</param>
    /// <param name="updateValidator">Validates a submitted update request.</param>
    /// <exception cref="ArgumentNullException">Any argument is <see langword="null"/>.</exception>
    public PortalAliasesController(
        IPortalService portals,
        IValidator<CreatePortalAliasRequest> createValidator,
        IValidator<UpdatePortalAliasRequest> updateValidator)
    {
        _portals = portals ?? throw new ArgumentNullException(nameof(portals));
        _createValidator = createValidator ?? throw new ArgumentNullException(nameof(createValidator));
        _updateValidator = updateValidator ?? throw new ArgumentNullException(nameof(updateValidator));
    }

    /// <summary>Lists the aliases of one portal.</summary>
    /// <param name="portalId">The portal identifier.</param>
    /// <param name="cancellationToken">Abandons the request when the caller disconnects.</param>
    /// <returns>The portal's aliases.</returns>
    [HttpGet("portals/{portalId:int}/aliases")]
    [ProducesResponseType(typeof(IReadOnlyList<PortalAliasDto>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<PortalAliasDto>>> ListForPortalAsync(
        int portalId,
        CancellationToken cancellationToken)
    {
        Result<IReadOnlyList<PortalAliasDto>> outcome = await _portals
            .ListPortalAliasesAsync(portalId, cancellationToken)
            .ConfigureAwait(false);

        return this.Complete(outcome);
    }

    /// <summary>Lists every alias on this host.</summary>
    /// <param name="cancellationToken">Abandons the request when the caller disconnects.</param>
    /// <returns>Every alias, across all portals.</returns>
    /// <remarks>
    /// The host-wide view exists because alias collisions are a host-level problem: an operator diagnosing
    /// why one portal is answering for another needs to see every alias at once, which no per-portal view
    /// can show.
    /// </remarks>
    [HttpGet("portal-aliases")]
    [ProducesResponseType(typeof(IReadOnlyList<PortalAliasDto>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<PortalAliasDto>>> ListAllAsync(
        CancellationToken cancellationToken)
    {
        Result<IReadOnlyList<PortalAliasDto>> outcome = await _portals
            .ListPortalAliasesAsync(null, cancellationToken)
            .ConfigureAwait(false);

        return this.Complete(outcome);
    }

    /// <summary>Retrieves one alias.</summary>
    /// <param name="portalAliasId">The alias identifier.</param>
    /// <param name="cancellationToken">Abandons the request when the caller disconnects.</param>
    /// <returns>The alias, or <c>404 Not Found</c> when it does not exist.</returns>
    [HttpGet("portal-aliases/{portalAliasId:int}")]
    [ProducesResponseType(typeof(PortalAliasDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<PortalAliasDto?>> GetAsync(
        int portalAliasId,
        CancellationToken cancellationToken)
    {
        Result<PortalAliasDto?> outcome = await _portals
            .GetPortalAliasAsync(portalAliasId, cancellationToken)
            .ConfigureAwait(false);

        return this.Complete(outcome);
    }

    /// <summary>Adds an alias to a portal.</summary>
    /// <param name="portalId">The portal identifier.</param>
    /// <param name="request">The host name to bind.</param>
    /// <param name="cancellationToken">Abandons the request when the caller disconnects.</param>
    /// <returns>The created alias, with its address in the location header.</returns>
    [HttpPost("portals/{portalId:int}/aliases")]
    [ProducesResponseType(typeof(PortalAliasDto), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<PortalAliasDto>> AddAsync(
        int portalId,
        [FromBody] CreatePortalAliasRequest request,
        CancellationToken cancellationToken)
    {
        ActionResult? invalid = await this
            .ValidateRequestAsync(_createValidator, request, cancellationToken)
            .ConfigureAwait(false);

        if (invalid is not null)
        {
            return invalid;
        }

        Result<PortalAliasDto> outcome = await _portals
            .AddPortalAliasAsync(portalId, request, cancellationToken)
            .ConfigureAwait(false);

        return this.Created(outcome, created => created.PortalAliasId);
    }

    /// <summary>Updates an alias.</summary>
    /// <param name="portalAliasId">The alias identifier.</param>
    /// <param name="request">The host name to bind in place of the current one.</param>
    /// <param name="cancellationToken">Abandons the request when the caller disconnects.</param>
    /// <returns><c>204 No Content</c> when the alias has been updated.</returns>
    [HttpPut("portal-aliases/{portalAliasId:int}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult> UpdateAsync(
        int portalAliasId,
        [FromBody] UpdatePortalAliasRequest request,
        CancellationToken cancellationToken)
    {
        ActionResult? invalid = await this
            .ValidateRequestAsync(_updateValidator, request, cancellationToken)
            .ConfigureAwait(false);

        if (invalid is not null)
        {
            return invalid;
        }

        Result outcome = await _portals
            .UpdatePortalAliasAsync(portalAliasId, request, cancellationToken)
            .ConfigureAwait(false);

        return this.Complete(outcome);
    }

    /// <summary>Deletes an alias.</summary>
    /// <param name="portalAliasId">The alias identifier.</param>
    /// <param name="cancellationToken">Abandons the request when the caller disconnects.</param>
    /// <returns><c>204 No Content</c> when the alias has been removed.</returns>
    [HttpDelete("portal-aliases/{portalAliasId:int}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult> DeleteAsync(
        int portalAliasId,
        CancellationToken cancellationToken)
    {
        Result outcome = await _portals
            .DeletePortalAliasAsync(portalAliasId, cancellationToken)
            .ConfigureAwait(false);

        return this.Complete(outcome);
    }
}
