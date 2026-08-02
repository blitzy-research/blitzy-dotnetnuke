using Asp.Versioning;
using DnnMigration.Api.Authorization;
using DnnMigration.Api.ErrorHandling;
using DnnMigration.Api.Filters;
using DnnMigration.Application.Abstractions;
using DnnMigration.Application.Dtos.Common;
using DnnMigration.Application.Dtos.Tab;
using DnnMigration.Domain.Common;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DnnMigration.Api.Controllers;

/// <summary>
/// The tab resource: the page abstraction that modules are placed on.
/// </summary>
/// <remarks>
/// <para>
/// MIGRATION: replaces the read and edit paths of <c>Website/admin/Tabs/Tabs.ascx.vb</c> and
/// <c>ManageTabs.ascx.vb</c>.
/// </para>
/// <para>
/// <strong>Deliberately narrow.</strong> Tabs are a supporting aggregate here, drawn in because module
/// placement and permissions are keyed by them - not a feature in their own right. So there is no creation
/// endpoint, no deletion endpoint and no recycle bin, and their absence is a scope decision rather than an
/// omission: adding them would pull the page-hierarchy feature into a migration whose five preserved domains
/// do not include it.
/// </para>
/// <para>
/// The collection is nested under its portal because the service reads it per portal; the individual tab is
/// addressed at its own path because the service identifies it by its own key alone. The read is guarded by
/// the tab view policy and the write by the tab edit policy, both of which read the <c>tabId</c> route value
/// - so the segment name is part of the contract with the authorisation handler and cannot be renamed here
/// alone.
/// </para>
/// </remarks>
[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}")]
[Produces("application/json")]
public sealed class TabsController : ControllerBase
{
    private readonly ITabService _tabs;

    /// <summary>Initialises a new instance of the <see cref="TabsController"/> class.</summary>
    /// <param name="tabs">The tab service.</param>
    /// <exception cref="ArgumentNullException"><paramref name="tabs"/> is <see langword="null"/>.</exception>
    public TabsController(ITabService tabs)
    {
        _tabs = tabs ?? throw new ArgumentNullException(nameof(tabs));
    }

    /// <summary>Lists a portal's tabs in navigation order.</summary>
    /// <param name="portalId">The portal identifier.</param>
    /// <param name="cancellationToken">Abandons the request when the caller disconnects.</param>
    /// <returns>The portal's tabs.</returns>
    /// <remarks>
    /// The order the service returns is the navigation order and is not re-sorted here. Re-sorting would
    /// look harmless and would break the hierarchy, because a child's position is meaningful only relative
    /// to the parent that precedes it.
    /// </remarks>
    [HttpGet("portals/{portalId:int}/tabs")]
    [Authorize]
    [ProducesResponseType(typeof(IReadOnlyList<TabListItemDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<IReadOnlyList<TabListItemDto>>> ListAsync(
        int portalId,
        CancellationToken cancellationToken)
    {
        Result<IReadOnlyList<TabListItemDto>> outcome = await _tabs
            .GetTabsAsync(portalId, cancellationToken)
            .ConfigureAwait(false);

        return this.Complete(outcome);
    }

    /// <summary>Retrieves one tab.</summary>
    /// <param name="tabId">The tab identifier.</param>
    /// <param name="cancellationToken">Abandons the request when the caller disconnects.</param>
    /// <returns>The tab, or <c>404 Not Found</c> when it does not exist.</returns>
    [HttpGet("tabs/{tabId:int}")]
    [Authorize(Policy = PolicyNames.TabView)]
    [ProducesResponseType(typeof(TabDetailDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<TabDetailDto?>> GetAsync(int tabId, CancellationToken cancellationToken)
    {
        Result<TabDetailDto?> outcome = await _tabs
            .GetTabAsync(tabId, cancellationToken)
            .ConfigureAwait(false);

        return this.Complete(outcome);
    }

    /// <summary>Updates a tab.</summary>
    /// <param name="tabId">The tab identifier.</param>
    /// <param name="request">The new state.</param>
    /// <param name="cancellationToken">Abandons the request when the caller disconnects.</param>
    /// <returns>The updated tab.</returns>
    [HttpPut("tabs/{tabId:int}")]
    [Authorize(Policy = PolicyNames.TabEdit)]
    [ProducesResponseType(typeof(TabDetailDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<TabDetailDto>> UpdateAsync(
        int tabId,
        [FromBody] UpdateTabRequest request,
        CancellationToken cancellationToken)
    {
        Result<TabDetailDto> outcome = await _tabs
            .UpdateTabAsync(tabId, request, cancellationToken)
            .ConfigureAwait(false);

        return this.Complete(outcome);
    }
}
