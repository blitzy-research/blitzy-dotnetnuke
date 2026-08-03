using Asp.Versioning;
using DnnMigration.Api.Authorization;
using DnnMigration.Api.ErrorHandling;
using DnnMigration.Api.Filters;
using DnnMigration.Api.Middleware;
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
/// <para>
/// <strong>The collection listing is tenant-bound administration, not bare authentication.</strong> It returns
/// a tenant's entire page hierarchy, which is its navigation structure - including pages in the recycle bin and
/// pages an ordinary visitor is not permitted to see. An earlier revision required only that the caller be
/// authenticated, so any member of any tenant could enumerate any other tenant's site map by changing the
/// <c>portalId</c> segment. It cannot use the tab view policy, because that policy is evaluated against one
/// page and this route names none; the tenant the route DOES name is what binds it.
/// </para>
/// </remarks>
[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}")]
[Produces("application/json")]
public sealed class TabsController : ControllerBase
{
    /// <summary>
    /// Why the identifier-only page actions may be served without a resolved tenant.
    /// </summary>
    /// <remarks>
    /// THE TENANT COMES FROM THE TOKEN, WHICH IS STRONGER THAN THE HOST NAME. A page named by identifier alone
    /// is authorised by the page view and page edit policies, which take the tenant from the caller's portal
    /// claim when the route names none. That claim was fixed when the token was issued and is signed, whereas a
    /// Host header is unauthenticated caller-supplied text; and the permission service verifies that the page
    /// actually belongs to that portal before answering, so a token for one tenant cannot reach another's page.
    /// </remarks>
    private const string TokenScopedJustification =
        "The page is authorised against the portal claim on the caller's signed token, and the permission "
        + "service verifies the page belongs to that portal, so the host name is not the source of the tenant.";

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
    // TENANT-BOUND, NOT MERELY AUTHENTICATED. A bare authentication requirement let any bearer token name
    // any portal in the route and enumerate that tenant's whole page hierarchy - titles, parentage and
    // ordering - which is tenant data even though nothing is mutated. The portal-administrator policy is
    // anchored to the portal this route names, so an administrator of another tenant is refused. The
    // single-page endpoints below stay on the page-scoped permission policies, which were already
    // route-anchored and are the finer-grained answer where a page identifier exists to evaluate.
    [HttpGet("portals/{portalId:int}/tabs")]
    [Authorize(Policy = PolicyNames.PortalAdministrator)]
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
    /// <param name="tabId">The tab identifier.</param>
    /// <param name="cancellationToken">Abandons the request when the caller disconnects.</param>
    /// <returns>The tab, or <c>404 Not Found</c> when it does not exist.</returns>
    [HttpGet("tabs/{tabId:int}")]
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
    /// <param name="request">The new state.</param>
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
