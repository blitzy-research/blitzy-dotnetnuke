using Asp.Versioning;
using DnnMigration.Api.Authorization;
using DnnMigration.Api.ErrorHandling;
using DnnMigration.Api.Middleware;
using DnnMigration.Application.Abstractions;
using DnnMigration.Application.Dtos.Common;
using DnnMigration.Application.Dtos.Tab;
using DnnMigration.Domain.Common;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DnnMigration.Api.Controllers;

/// <summary>The tab resource: the page abstraction that modules are placed on.</summary>
/// <remarks>
/// The collection is nested under its portal because the service reads it per portal; the individual tab is
/// addressed at its own path because the service identifies it by its own key alone.
/// </remarks>
// The legacy authorisation gate was imperative and doubled.
[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}")]
// ⚠ THERE IS DELIBERATELY NO CONTROLLER-LEVEL [Authorize] HERE, AND ADDING ONE IS A REGRESSION. Every
// action below states its own policy, which is the whole authorisation story for this file.
[Produces("application/json")]
public sealed class TabsController : ControllerBase
{
    /// <summary>Why the identifier-only page actions may be served without a resolved tenant.</summary>
    /// <remarks>
    /// THE TENANT COMES FROM THE TOKEN, WHICH IS STRONGER THAN THE HOST NAME. A page named by identifier
    /// alone is authorised by the page view and page edit policies, which take the tenant from the caller's
    /// portal claim when the route names none.
    /// </remarks>
    private const string TokenScopedJustification =
        "The page is authorised against the portal claim on the caller's signed token, and the permission "
        + "service verifies the page belongs to that portal, so the host name is not the source of the tenant.";

    /// <summary>The route name of the by-identifier page read.</summary>
    /// <remarks>
    /// Naming the route makes the single-page address generatable rather than reconstructable: anything
    /// that has to point a caller at one page asks the link generator for this name instead of rebuilding
    /// the template by hand, so the version segment and the parameter spelling stay in one place.
    /// </remarks>
    private const string GetTabRouteName = "GetTab";

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
    /// <returns>The portal's tabs, complete and unpaged, in the shared success envelope.</returns>
    /// <remarks>
    /// The order the service returns is the navigation order and is not re-sorted here. Re-sorting would
    /// look harmless and would break the hierarchy, because a child's position is meaningful only relative
    /// to the parent that precedes it.
    /// </remarks>
    // TENANT-BOUND, NOT MERELY AUTHENTICATED. A bare authentication requirement let any bearer token name
    // any portal in the route and enumerate that tenant's whole page hierarchy - titles, parentage and
    // ordering - which is tenant data even though nothing is mutated.
    [HttpGet("portals/{portalId:int}/tabs")]
    [Authorize(Policy = PolicyNames.PortalContentEditor)]
    [ProducesResponseType(typeof(ApiResponse<IReadOnlyList<TabListItemDto>>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ApiResponse<IReadOnlyList<TabListItemDto>>>> ListAsync(
        int portalId,
        CancellationToken cancellationToken)
    {
        Result<IReadOnlyList<TabListItemDto>> outcome = await _tabs
            .GetTabsAsync(portalId, cancellationToken)
            .ConfigureAwait(false);

        return this.Complete(outcome);
    }

    /// <summary>Retrieves one tab.</summary>
    /// <param name="tabId">
    /// The tab identifier. <c>0</c> is a real page and must never be read as "unspecified" or "not yet
    /// saved": <c>Tabs.TabID</c> is declared <c>IDENTITY (0, 1)</c> (<c>01.00.00.SqlDataProvider:L140</c>),
    /// so the first page ever created in an installation carries it.
    /// </param>
    /// <param name="cancellationToken">Abandons the request when the caller disconnects.</param>
    /// <returns>The tab, or <c>404 Not Found</c> when it does not exist.</returns>
    /// <remarks>
    /// <strong>In practice that <c>404</c> is nearly unreachable, and deliberately so.</strong> This route
    /// addresses pages in every tenant, so the authorisation policy is evaluated before the action runs and
    /// refuses an identifier it cannot resolve a grant for.
    /// </remarks>
    [HttpGet("tabs/{tabId:int}", Name = GetTabRouteName)]
    [Authorize(Policy = PolicyNames.TabView)]
    [TenantOptional(TokenScopedJustification)]
    [ProducesResponseType(typeof(ApiResponse<TabDetailDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ApiResponse<TabDetailDto?>>> GetAsync(
        int tabId,
        CancellationToken cancellationToken)
    {
        Result<TabDetailDto?> outcome = await _tabs
            .GetTabAsync(tabId, cancellationToken)
            .ConfigureAwait(false);

        return this.Complete(outcome);
    }

    /// <summary>Updates a tab.</summary>
    /// <param name="tabId">The tab identifier.</param>
    /// <param name="request">The new state, including where the page sits in the tree.</param>
    /// <param name="cancellationToken">Abandons the request when the caller disconnects.</param>
    /// <returns>The updated tab.</returns>
    [HttpPut("tabs/{tabId:int}")]
    [Authorize(Policy = PolicyNames.TabEdit)]
    [TenantOptional(TokenScopedJustification)]
    [ProducesResponseType(typeof(ApiResponse<TabDetailDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ApiResponse<TabDetailDto>>> UpdateAsync(
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
