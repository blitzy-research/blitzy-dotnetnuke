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
/// The portal resource: the multi-tenant site container.
/// </summary>
/// <remarks>
/// <para>
/// MIGRATION: replaces <c>Website/admin/Portal/Portals.ascx.vb</c> (the list), <c>Signup.ascx.vb</c>
/// (creation) and <c>SiteSettings.ascx.vb</c> (edit and settings). Those pages held their business rules in
/// event handlers - a fifteen-argument creation call and a twenty-seven-argument update call, both invoked
/// from a button click. Here the rules live in <see cref="IPortalService"/> and this type does three things
/// only: it validates the shape of what arrived, it delegates, and it turns the outcome into a status code.
/// </para>
/// <para>
/// <strong>There is no business logic in this file and there must never be any.</strong> Not a null check
/// that decides an outcome, not a permission re-interpreted, not a default filled in. Every one of those
/// belongs to the service, because the service is what the unit tests exercise and what a future second
/// caller - a background job, a console tool - would reach. A rule implemented here would apply to HTTP
/// callers and silently not apply to anything else.
/// </para>
/// </remarks>
[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}")]
[Produces("application/json")]
[Authorize(Policy = PolicyNames.PortalAdministrator)]
public sealed class PortalsController : ControllerBase
{
    private readonly IPortalService _portals;
    private readonly IValidator<PagedRequest> _pageValidator;
    private readonly IValidator<CreatePortalRequest> _createValidator;
    private readonly IValidator<UpdatePortalRequest> _updateValidator;

    /// <summary>Initialises a new instance of the <see cref="PortalsController"/> class.</summary>
    /// <param name="portals">The portal service.</param>
    /// <param name="pageValidator">Validates paging arguments.</param>
    /// <param name="createValidator">Validates a creation request.</param>
    /// <param name="updateValidator">Validates an update request.</param>
    /// <exception cref="ArgumentNullException">Any argument is <see langword="null"/>.</exception>
    public PortalsController(
        IPortalService portals,
        IValidator<PagedRequest> pageValidator,
        IValidator<CreatePortalRequest> createValidator,
        IValidator<UpdatePortalRequest> updateValidator)
    {
        _portals = portals ?? throw new ArgumentNullException(nameof(portals));
        _pageValidator = pageValidator ?? throw new ArgumentNullException(nameof(pageValidator));
        _createValidator = createValidator ?? throw new ArgumentNullException(nameof(createValidator));
        _updateValidator = updateValidator ?? throw new ArgumentNullException(nameof(updateValidator));
    }

    /// <summary>Lists the portals installed on this host.</summary>
    /// <param name="request">Paging and sorting arguments, bound from the query string.</param>
    /// <param name="name">Restricts the result to portals whose name matches.</param>
    /// <param name="cancellationToken">Abandons the request when the caller disconnects.</param>
    /// <returns>A page of portals.</returns>
    [HttpGet("portals")]
    [ProducesResponseType(typeof(PagedResult<PortalListItemDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<PagedResult<PortalListItemDto>>> ListAsync(
        [FromQuery] PagedRequest request,
        [FromQuery] string? name,
        CancellationToken cancellationToken)
    {
        ActionResult? invalid = await this
            .ValidateRequestAsync(_pageValidator, request, cancellationToken)
            .ConfigureAwait(false);

        if (invalid is not null)
        {
            return invalid;
        }

        Result<PagedResult<PortalListItemDto>> outcome = await _portals
            .ListPortalsAsync(request, name, cancellationToken)
            .ConfigureAwait(false);

        return this.Complete(outcome);
    }

    /// <summary>Retrieves one portal.</summary>
    /// <param name="portalId">The portal identifier.</param>
    /// <param name="cancellationToken">Abandons the request when the caller disconnects.</param>
    /// <returns>The portal, or <c>404 Not Found</c> when it does not exist.</returns>
    [HttpGet("portals/{portalId:int}")]
    [ProducesResponseType(typeof(PortalDetailDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<PortalDetailDto?>> GetAsync(
        int portalId,
        CancellationToken cancellationToken)
    {
        Result<PortalDetailDto?> outcome = await _portals
            .GetPortalAsync(portalId, cancellationToken)
            .ConfigureAwait(false);

        return this.Complete(outcome);
    }

    /// <summary>Creates a portal.</summary>
    /// <param name="request">The portal to create.</param>
    /// <param name="cancellationToken">Abandons the request when the caller disconnects.</param>
    /// <returns>The created portal, with its address in the location header.</returns>
    [HttpPost("portals")]
    [ProducesResponseType(typeof(PortalDetailDto), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<PortalDetailDto>> CreateAsync(
        [FromBody] CreatePortalRequest request,
        CancellationToken cancellationToken)
    {
        ActionResult? invalid = await this
            .ValidateRequestAsync(_createValidator, request, cancellationToken)
            .ConfigureAwait(false);

        if (invalid is not null)
        {
            return invalid;
        }

        Result<PortalDetailDto> outcome = await _portals
            .CreatePortalAsync(request, cancellationToken)
            .ConfigureAwait(false);

        return this.Created(outcome, created => created.PortalId);
    }

    /// <summary>Updates a portal.</summary>
    /// <param name="portalId">The portal identifier.</param>
    /// <param name="request">The new state.</param>
    /// <param name="cancellationToken">Abandons the request when the caller disconnects.</param>
    /// <returns>The updated portal, or <c>404 Not Found</c> when it does not exist.</returns>
    [HttpPut("portals/{portalId:int}")]
    [ProducesResponseType(typeof(PortalDetailDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<PortalDetailDto?>> UpdateAsync(
        int portalId,
        [FromBody] UpdatePortalRequest request,
        CancellationToken cancellationToken)
    {
        ActionResult? invalid = await this
            .ValidateRequestAsync(_updateValidator, request, cancellationToken)
            .ConfigureAwait(false);

        if (invalid is not null)
        {
            return invalid;
        }

        Result<PortalDetailDto?> outcome = await _portals
            .UpdatePortalAsync(portalId, request, cancellationToken)
            .ConfigureAwait(false);

        return this.Complete(outcome);
    }

    /// <summary>Deletes a portal.</summary>
    /// <param name="portalId">The portal identifier.</param>
    /// <param name="cancellationToken">Abandons the request when the caller disconnects.</param>
    /// <returns><c>204 No Content</c> when the portal has been removed.</returns>
    [HttpDelete("portals/{portalId:int}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult> DeleteAsync(int portalId, CancellationToken cancellationToken)
    {
        Result outcome = await _portals
            .DeletePortalAsync(portalId, cancellationToken)
            .ConfigureAwait(false);

        return this.Complete(outcome);
    }

    /// <summary>Retrieves a portal's settings projection.</summary>
    /// <param name="portalId">The portal identifier.</param>
    /// <param name="cancellationToken">Abandons the request when the caller disconnects.</param>
    /// <returns>The settings, or <c>404 Not Found</c> when the portal does not exist.</returns>
    /// <remarks>
    /// MIGRATION: the legacy <c>PortalSettings</c> object was assembled per request into the ambient items
    /// dictionary and was mutable. There is no settings table behind this: the values are columns on the
    /// portal row, which is why this reads as a projection rather than as its own resource.
    /// </remarks>
    [HttpGet("portals/{portalId:int}/settings")]
    [ProducesResponseType(typeof(PortalSettingsDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<PortalSettingsDto?>> GetSettingsAsync(
        int portalId,
        CancellationToken cancellationToken)
    {
        Result<PortalSettingsDto?> outcome = await _portals
            .GetPortalSettingsAsync(portalId, cancellationToken)
            .ConfigureAwait(false);

        return this.Complete(outcome);
    }
}
