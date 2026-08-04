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
/// <para>
/// <strong>Exactly which legacy operations were dropped, so that none is mistaken for an oversight.</strong>
/// Five write paths existed on the two legacy screens and none is reproduced here. Creation
/// (<c>ManageTabs.ascx.vb:L314</c>, <c>AddTab</c>) and deletion (<c>Tabs.ascx.vb:L73</c>,
/// <c>DeleteTab</c> behind a special-page guard) have no endpoint, which is why the page transfer folder
/// declares three types and no create request. Reordering - four commands, <c>left</c> and <c>right</c> at
/// <c>Tabs.ascx.vb:L188-L191</c> and <c>up</c> and <c>down</c> at <c>:L222-L225</c>, every one of them a call
/// to the seven-argument tab-order routine at <c>TabController.vb:L550</c> - has no endpoint either: ordering
/// is stated as named properties on the update request, so a caller moves a page by describing where it
/// belongs rather than by nudging it one step at a time. Design propagation to child pages and the restoring
/// and emptying of deleted pages are absent with the features they belong to, and the page export and import
/// screens produce no controller at all.
/// </para>
/// </remarks>
// MIGRATION: page skin and container values are NOT interpreted here. They survive on the update request as
// opaque tokens, for the single reason that an edit which did not mention them must not blank an
// administrator's stored choice - the legacy screen posted both, so dropping them would silently destroy data
// on every save. Nothing in the target resolves a skin, locates a container or renders with either, because
// skinning and containers are excluded from this migration wholesale. This controller therefore reads neither
// value, validates neither, and names neither.
//
// MIGRATION: the legacy authorisation gate was imperative and doubled. ManageTabs.ascx.vb:L586 read
// `PortalSecurity.IsInRoles(PortalSettings.AdministratorRoleName) = False And
// PortalSecurity.IsInRoles(PortalSettings.ActiveTab.AdministratorRoles.ToString) = False`, and on failure ran
// `Response.Redirect(NavigateURL("Access Denied"), True)`. Both halves of that condition - administer the
// portal, or administer this particular page - are what the page view and page edit policies now express
// declaratively, decided by one evaluator outside this file. Two consequences are deliberate: the redirect
// becomes a 403 problem-details response, because an API tells a caller it was refused instead of sending it
// somewhere else; and the `.ToString` on a role collection in that condition, which compiled only because the
// legacy web pages were built with Option Strict OFF (`Website/release.config:L125`,
// `<compilation debug="false" strict="false">`), has no counterpart at all - there is no late-bound conversion
// anywhere on this surface.
//
// MIGRATION: no permission decision is re-implemented here, and that restraint is the point. The legacy
// permission reader and the legacy permission test disagreed with each other - TabPermissionController.vb's
// `GetTabPermissions` (L218) required `AllowAccess` while `HasTabPermission` (L38-L54) did not - and the
// single evaluator in the infrastructure layer settles that disagreement with deny precedence. A second
// evaluator living in a controller would answer differently for the same caller, which is worse than either
// original answer.
[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}")]
// ⚠ THERE IS DELIBERATELY NO CONTROLLER-LEVEL [Authorize] HERE, AND ADDING ONE IS A REGRESSION. Every action
// below states its own policy, which is the whole authorisation story for this file. Two separate mechanisms
// make the seemingly safer controller-level attribute wrong, and both were established by running the suite
// rather than by reading the framework:
//
//   1. A page may legitimately be PUBLIC. A view grant to the all-users pseudo-role (-1) or the
//      unauthenticated-users pseudo-role (-3) is how DotNetNuke publishes a page to visitors who have no
//      account at all, so the page view policy is expected to succeed for an anonymous caller. Requiring
//      authentication for the controller turns that 200 into a 401 and un-publishes every public page - a
//      behavioural change, not a hardening. Verified: adding a bare [Authorize] here failed exactly
//      GetTab_AsAnAnonymousCallerWithAnUnauthenticatedGrant_ReturnsOk and
//      GetTab_AsAnAnonymousCallerWithAnAllUsersGrant_ReturnsOk, each with 401 where 200 was expected.
//   2. These attributes COMBINE; they do not override. A controller-level POLICY is demanded IN ADDITION to
//      each action's own, so naming a portal-scoped one would also impose a tenant requirement on the two
//      identifier-only actions below - which are marked tenant-optional precisely because they take their
//      tenant from the caller's token - and refuse all of them.
//
// [AllowAnonymous] is not the escape either: it suppresses authorisation for an endpoint ENTIRELY, so pairing
// it with a controller-level [Authorize] would discard the page view policy and open every page to everyone.
// The sibling controller that shares this shape (modules, with the same permission-key policies) carries no
// controller-level attribute for the same reason; the controllers that do carry one gate on MEMBERSHIP, which
// no anonymous caller can ever satisfy, so the question does not arise there.
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

    /// <summary>The route name of the by-identifier page read.</summary>
    /// <remarks>
    /// Naming the route makes the single-page address generatable rather than reconstructable: anything that
    /// has to point a caller at one page asks the link generator for this name instead of rebuilding the
    /// template by hand, so the version segment and the parameter spelling stay in one place. It is a
    /// <c>const</c> because an attribute argument accepts nothing else, and it is declared here rather than
    /// written inline so the name is greppable by one token and cannot drift between declaration and use.
    /// Route names are application-wide, and this is the only one the API declares.
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
    /// <param name="portalId">
    /// The portal identifier. Both <c>0</c> and <c>-1</c> are real portal identifiers here and neither means
    /// "unspecified": <c>Portals.PortalID</c> is declared <c>IDENTITY (-1, 1)</c>
    /// (<c>01.00.00.SqlDataProvider:L77</c>), so <c>-1</c> is the seed and the shipped default row was inserted
    /// with <c>0</c>. The route constraint is a plain integer for exactly that reason.
    /// </param>
    /// <param name="cancellationToken">Abandons the request when the caller disconnects.</param>
    /// <returns>The portal's tabs, complete and unpaged, in the shared success envelope.</returns>
    /// <response code="200">
    /// Every page of the portal, in navigation order. A portal with no pages answers here with an empty
    /// collection - that is a legitimate answer, never a failure. The answer is complete rather than paged
    /// because each row carries its own parent, level and order, and those are only coherent over the whole
    /// set: a tree split across pages severs parents from their children.
    /// </response>
    /// <response code="401">No credential was presented, or the one presented is not valid.</response>
    /// <response code="403">
    /// The caller is authenticated but does not administer the portal this request addresses.
    /// </response>
    /// <response code="404">No portal bears that identifier. An empty portal is deliberately not this answer.</response>
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
    /// <param name="tabId">
    /// The tab identifier. <c>0</c> is a real page and must never be read as "unspecified" or "not yet saved":
    /// <c>Tabs.TabID</c> is declared <c>IDENTITY (0, 1)</c> (<c>01.00.00.SqlDataProvider:L140</c>), so the
    /// first page ever created in an installation carries it. The route constraint is therefore a plain
    /// integer, admitting the whole range, and the authorisation handler that reads this same route value
    /// parses it the same way.
    /// </param>
    /// <param name="cancellationToken">Abandons the request when the caller disconnects.</param>
    /// <returns>The tab, or <c>404 Not Found</c> when it does not exist.</returns>
    /// <response code="200">
    /// The page, including its permission grants. Reachable by a caller with no account at all when the page
    /// carries a view grant to the all-users or unauthenticated-users pseudo-role, which is how a page is
    /// published to visitors.
    /// </response>
    /// <response code="401">
    /// No credential was presented and the page is not published anonymously, or the credential presented is
    /// not valid.
    /// </response>
    /// <response code="403">
    /// The caller holds no view grant on this page. This is also the answer for an identifier that exists in
    /// another tenant <em>and</em> for one that exists nowhere - see the note below on why those two are
    /// deliberately indistinguishable.
    /// </response>
    /// <response code="404">
    /// The page disappeared between the authorisation decision and the read. An unknown identifier does
    /// <em>not</em> answer here; it answers <c>403</c>.
    /// </response>
    /// <remarks>
    /// <para>
    /// The service reports absence as a successful outcome carrying no value, and the shared translator turns
    /// that into a <c>404</c>. Absence is not a failure inside the service because nothing went wrong; it
    /// becomes one only at the HTTP boundary, where "there is no such page" is what a status code exists to
    /// say.
    /// </para>
    /// <para>
    /// <strong>In practice that <c>404</c> is nearly unreachable, and deliberately so.</strong> This route
    /// addresses pages in every tenant, so the authorisation policy is evaluated before the action runs and
    /// refuses an identifier it cannot resolve a grant for. Answering <c>404</c> for an identifier that exists
    /// nowhere while answering <c>403</c> for one that exists in another tenant would let a caller tell the two
    /// apart and walk the identifier space, so both answer the refusal. The <c>404</c> remains declared because
    /// the translator can still produce it if the page is removed between the decision and the read.
    /// </para>
    /// </remarks>
    // MIGRATION: the permission grants on the returned page are a typed collection, not the legacy
    // semicolon-delimited role string. The legacy screens passed such strings around verbatim -
    // ManageTabs.ascx.vb:L492 and :L509 hand `AuthorizedRoles` and `AuthorizedEditRoles` straight to a role
    // test - and TabPermissionController.vb:L214-L227 built them by appending a semicolon after each role name
    // and after each user identifier wrapped in square brackets, then prefixing one more semicolon, yielding a
    // value shaped like  ;Administrators;[42];  . Two defects were structural rather than incidental. A role
    // whose name contained the separator could not survive a round trip, and a user grant was smuggled through
    // a role-shaped field by a bracketing convention that only the matching parser understood. Both disappear:
    // a user grant is a first-class nullable user identifier on its own row, and nothing here separates or
    // recombines a delimited string. There is also no negative or deny form to carry - this DotNetNuke
    // generation stores permission as a single allow flag, so no prefix convention is invented to express one.
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
    /// <param name="tabId">
    /// The tab identifier. As with the read above, <c>0</c> addresses a real page.
    /// </param>
    /// <param name="request">
    /// The new state, including where the page sits in the tree. A parent of <see langword="null"/> makes the
    /// page a root-level page; neither <c>0</c> nor <c>-1</c> may be used to say that, because each is a real
    /// identifier in this schema.
    /// </param>
    /// <param name="cancellationToken">Abandons the request when the caller disconnects.</param>
    /// <returns>The updated tab.</returns>
    /// <response code="200">The page as it stands after the update.</response>
    /// <response code="400">
    /// The body was absent or malformed, or the request was refused. A shape-level violation - a missing or
    /// overlong page name, an end date preceding a start date - arrives as an RFC 7807 validation document
    /// naming each offending field. The three state-level refusals reported the same way are a page name that
    /// is a reserved device name, a requested parent in another portal, and a requested parent that is the page
    /// itself or one of its own descendants.
    /// </response>
    /// <response code="401">No credential was presented, or the one presented is not valid.</response>
    /// <response code="403">
    /// The caller holds no edit grant on this page, or no such page exists - the two are indistinguishable
    /// here for the same anti-enumeration reason given on the read above.
    /// </response>
    /// <response code="404">
    /// The requested parent does not exist. A page named in the body follows the same convention as one named
    /// in the route: a resource that cannot be found answers <c>404</c>, whereas a request that describes
    /// something impossible - a parent forming a cycle, or one in another tenant - answers <c>400</c>.
    /// </response>
    /// <remarks>
    /// <para>
    /// MIGRATION: replaces the edit branch of <c>ManageTabs.ascx.vb:L257-L308</c>, which set visibility, the
    /// link-disabled flag and the permission grants and then called <c>UpdateTab</c>. Its reserved-name check
    /// (<c>:L272-L274</c>, matching <c>CON</c>, <c>NUL</c>, <c>AUX</c>, <c>COM1</c>-<c>COM9</c> and
    /// <c>LPT1</c>-<c>LPT9</c>) is preserved as a stable reason code; its silent abandonment of the save on a
    /// circular parent reference is not - that rejection now announces itself instead of rendering nothing,
    /// which a user could read as success.
    /// </para>
    /// <para>
    /// MIGRATION: this action declares no <c>409 Conflict</c>, and its absence is a measured finding rather
    /// than an omission. The legacy duplicate-path refusal - the <c>TabExists</c> message - sat behind
    /// <c>If String.IsNullOrEmpty(strAction)</c> at <c>ManageTabs.ascx.vb:L279</c>, while the edit branch was
    /// entered at <c>:L304</c> under <c>If strAction = "edit"</c>. The guard therefore never ran on an update:
    /// it belonged to the create path, which this API deliberately does not expose. The service contract agrees
    /// - it declares no conflict reason code - so publishing a <c>409</c> here would advertise a status no
    /// request can elicit, which misleads a client more than saying nothing would.
    /// </para>
    /// </remarks>
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
