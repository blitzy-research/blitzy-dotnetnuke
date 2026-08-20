using Asp.Versioning;
using DnnMigration.Api.Authorization;
using DnnMigration.Api.ErrorHandling;
using DnnMigration.Api.Extensions;
using DnnMigration.Api.Middleware;
using DnnMigration.Application.Abstractions;
using DnnMigration.Application.Dtos.Common;
using DnnMigration.Application.Dtos.Portal;
using DnnMigration.Domain.Common;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DnnMigration.Api.Controllers;

/// <summary>The portal resource - the multi-tenant site container - exposed at <c>/api/v1/portals</c>.</summary>
/// <remarks>
/// <para>
/// The class-level attribute is therefore authentication ONLY, and every action states its policy
/// explicitly.
/// </para>
/// <para>
/// No sentinel is manufactured or erased on this boundary. The transfer objects carry the stored values
/// through unchanged, so a legacy consumer still sees the retention period as -1 and an unset expiry as the
/// minimum date.
/// </para>
/// </remarks>
[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/portals")]
[Authorize]
[Produces("application/json")]
public sealed class PortalsController : ControllerBase
{
    /// <summary>The portal service this controller delegates to.</summary>
    private readonly IPortalService _portalService;

    /// <summary>Initialises a new instance of the <see cref="PortalsController"/> class.</summary>
    /// <param name="portalService">The application-layer contract for the portal aggregate.</param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="portalService"/> is <see langword="null"/>.
    /// </exception>
    /// <remarks>
    /// One dependency, and it is an application-layer contract.
    /// </remarks>
    public PortalsController(IPortalService portalService)
    {
        _portalService = portalService ?? throw new ArgumentNullException(nameof(portalService));
    }

    /// <summary>Lists the portals installed on this host as one page of a larger set.</summary>
    /// <param name="request">The paging, sorting and filtering arguments, bound from the query string.</param>
    /// <param name="name">An optional fragment of a portal's name.</param>
    /// <param name="cancellationToken">Abandons the read when the caller disconnects.</param>
    /// <returns>
    /// One page of portals, in the wire envelope: an <c>items</c> array of rows and a <c>meta</c> object
    /// carrying the total across every page, the page index and the page size.
    /// </returns>
    /// <remarks>
    /// The legacy filter was a starts-with match, assembled by appending a wildcard to the caller's text at
    /// that same line and interpreted as a pattern by the database, which had added none of its own. The
    /// modern filter is matched as literal text with pattern metacharacters escaped, so a caller can
    /// neither inject matching syntax nor force a scan by leading with a wildcard.
    /// </remarks>
    // HOST-SCOPED, NOT PORTAL-SCOPED. This action names no portal anywhere in its route, so the
    // portal-administrator policy would fall back to the tenant the caller arrived through and ask a
    // truthful but irrelevant question: an administrator of one tenant would satisfy it and then reach
    // every tenant.
    [HttpGet]
    [Authorize(Policy = PolicyNames.HostAdministrator)]
    // Installation-wide, and a bootstrap path: an installation with no portal has no alias for any host
    // name to resolve against, so requiring a resolved tenant here would make a fresh installation
    // permanently unlistable.
    [TenantOptional(
        "Enumerates every portal in the installation and must work on an installation that has none, where no "
        + "alias can resolve; restricted to host accounts.")]
    [ProducesResponseType(typeof(PagedResponse<PortalListItemDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<PagedResponse<PortalListItemDto>>> ListAsync(
        [FromQuery] PortalPagedRequest request,
        [FromQuery] string? name,
        CancellationToken cancellationToken)
    {
        Result<PagedResult<PortalListItemDto>> outcome = await _portalService
            .ListPortalsAsync(request, name, cancellationToken)
            .ConfigureAwait(false);

        // The shared translator tests the outcome before reading its value, so a failed outcome never has
        // its value touched - reading the value of a failed outcome throws by design. Projected onto the
        // wire envelope here rather than returned as the domain page.
        return this.CompletePage(outcome);
    }

    /// <summary>Lists the installation's portals, taking every filter from the request body.</summary>
    /// <param name="request">Paging, sorting and filtering arguments, bound from the body.</param>
    /// <param name="cancellationToken">Abandons the request when the caller disconnects.</param>
    /// <returns>A page of portals.</returns>
    /// <remarks>
    /// ⚠ THIS EXISTS FOR A PRIVACY REASON, NOT AN ERGONOMIC ONE, AND IS THE ADDRESS THE APPLICATION USES.
    /// The sibling <c>GET</c> bound TWO caller-chosen terms from the query string - the free-text term and
    /// the site-name filter - so both were written into the request line that the reverse proxy's access log
    /// and this application's own request log record. The account listing had already settled this the same
    /// way. The <c>GET</c> is retained for a term-free read and delegates to the identical service call, so
    /// the two transports cannot answer differently.
    /// </remarks>
    [HttpPost("search")]
    [Authorize(Policy = PolicyNames.HostAdministrator)]
    [TenantOptional(
        "Enumerates every portal in the installation and must work on an installation that has none, where no "
        + "alias can resolve; restricted to host accounts.")]
    [ProducesResponseType(typeof(PagedResponse<PortalListItemDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<PagedResponse<PortalListItemDto>>> SearchAsync(
        [FromBody] PortalSearchRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        Result<PagedResult<PortalListItemDto>> outcome = await _portalService
            .ListPortalsAsync(request, request.Name, cancellationToken)
            .ConfigureAwait(false);

        return this.CompletePage(outcome);
    }

    /// <summary>Reads one portal in full.</summary>
    /// <param name="portalId">The portal identifier.</param>
    /// <param name="cancellationToken">Abandons the read when the caller disconnects.</param>
    /// <returns>The portal.</returns>
    [HttpGet("{portalId:int}")]
    [Authorize(Policy = PolicyNames.PortalAdministrator)]
    [ProducesResponseType(typeof(ApiResponse<PortalDetailDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ApiResponse<PortalDetailDto?>>> GetAsync(
        int portalId,
        CancellationToken cancellationToken)
    {
        Result<PortalDetailDto?> outcome = await _portalService
            .GetPortalAsync(portalId, cancellationToken)
            .ConfigureAwait(false);

        return this.Complete(outcome);
    }

    /// <summary>Creates a portal together with the records a working tenant requires.</summary>
    /// <param name="request">The portal to create.</param>
    /// <param name="cancellationToken">Abandons the work when the caller disconnects.</param>
    /// <returns>The created portal, with the address of the new resource in the location header.</returns>
    /// <remarks>
    /// MIGRATION: replaces the fifteen-argument <c>CreatePortal</c> call at <c>Signup.ascx.vb:L274</c>,
    /// whose arguments were, in order, the title, first name, last name, user name, password, e-mail
    /// address, description, keywords, host map path, template selection, home directory, alias, server
    /// path, child path and child flag.
    /// </remarks>
    // HOST-SCOPED, NOT PORTAL-SCOPED, for the same reason as the listing above and one more: creating a
    // portal creates a NEW tenant together with its administrator account and its stock roles, which is not
    // an act within any existing tenant's authority.
    [HttpPost]
    [Authorize(Policy = PolicyNames.HostAdministrator)]
    // HASHES A CREDENTIAL: provisioning a tenant creates its administrator account and hashes the
    // credential supplied for it. "portals" is not on the path matcher's word list, so this was unbounded
    // too.
    [CredentialEndpoint]
    // The bootstrap path proper. The FIRST portal must be creatable before any alias exists, or the
    // installation can never be brought up; the request carries the alias it is claiming, so the tenant it
    // concerns is named in the body rather than in the host name.
    [TenantOptional(
        "Creates the first portal on an installation where no alias yet exists; the portal alias being "
        + "claimed is named in the request body. Restricted to host accounts.")]
    [ProducesResponseType(typeof(ApiResponse<PortalDetailDto>), StatusCodes.Status201Created)]
    // BASE ProblemDetails, not ValidationProblemDetails. Both shapes are reachable on this action: the
    // request validator names offending fields, and the service refuses a fully-valid request whose parent
    // alias resolves to no portal - a refusal with a failure code and no member to key an error map to.
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status429TooManyRequests)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status500InternalServerError)]
    public async Task<ActionResult<ApiResponse<PortalDetailDto>>> CreateAsync(
        [FromBody] CreatePortalRequest request,
        CancellationToken cancellationToken)
    {
        Result<PortalDetailDto> outcome = await _portalService
            .CreatePortalAsync(request, cancellationToken)
            .ConfigureAwait(false);

        // The identifier is read from the created representation rather than recomputed, and it is read
        // only on the success path: the translator checks the outcome first.
        return this.Created(outcome, created => created.PortalId);
    }

    /// <summary>Modifies an existing portal.</summary>
    /// <param name="portalId">The portal identifier, bound from the route.</param>
    /// <param name="request">The values to store.</param>
    /// <param name="cancellationToken">Abandons the work when the caller disconnects.</param>
    /// <returns>The stored portal.</returns>
    /// <remarks>
    /// The legacy code-behind compiled with Option Strict disabled, so its parsing was permissive in ways
    /// this boundary is not.
    /// </remarks>
    [HttpPut("{portalId:int}")]
    [Authorize(Policy = PolicyNames.PortalAdministrator)]
    [ProducesResponseType(typeof(ApiResponse<PortalDetailDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<ActionResult<ApiResponse<PortalDetailDto?>>> UpdateAsync(
        int portalId,
        [FromBody] UpdatePortalRequest request,
        CancellationToken cancellationToken)
    {
        Result<PortalDetailDto?> outcome = await _portalService
            .UpdatePortalAsync(portalId, request, cancellationToken)
            .ConfigureAwait(false);

        return this.Complete(outcome);
    }

    /// <summary>Removes a portal and the stored records that depend on it.</summary>
    /// <param name="portalId">The portal identifier.</param>
    /// <param name="cancellationToken">Abandons the work when the caller disconnects.</param>
    /// <returns>No content once the portal has been removed.</returns>
    /// <remarks>
    /// MIGRATION: the legacy screen wrote an audit entry keyed <c>PortalName</c> with event type
    /// <c>PORTAL_DELETED</c> on the success path.
    /// </remarks>
    [HttpDelete("{portalId:int}")]
    [Authorize(Policy = PolicyNames.HostAdministrator)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    // The unavailability branch is DECLARED, not new.
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult> DeleteAsync(int portalId, CancellationToken cancellationToken)
    {
        Result outcome = await _portalService
            .DeletePortalAsync(portalId, cancellationToken)
            .ConfigureAwait(false);

        return this.Complete(outcome);
    }

    /// <summary>Reads a portal's configuration, projected for display.</summary>
    /// <param name="portalId">The portal identifier.</param>
    /// <param name="cancellationToken">Abandons the read when the caller disconnects.</param>
    /// <returns>The configuration projection.</returns>
    /// <remarks>
    /// The resource is not a key/value settings table. Both actions operate on columns of the portal
    /// aggregate, and the legacy site-setting keys with no column counterpart remain deliberately excluded.
    /// </remarks>
    [HttpGet("{portalId:int}/settings")]
    [Authorize(Policy = PolicyNames.PortalAdministrator)]
    [ProducesResponseType(typeof(ApiResponse<PortalSettingsDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ApiResponse<PortalSettingsDto?>>> GetSettingsAsync(
        int portalId,
        CancellationToken cancellationToken)
    {
        Result<PortalSettingsDto?> outcome = await _portalService
            .GetPortalSettingsAsync(portalId, cancellationToken)
            .ConfigureAwait(false);

        return this.Complete(outcome);
    }

    /// <summary>Replaces a portal's editable configuration and returns the stored projection.</summary>
    /// <param name="portalId">The portal identifier, supplied only by the route.</param>
    /// <param name="request">The complete editable settings state.</param>
    /// <param name="cancellationToken">Abandons the update when the caller disconnects.</param>
    /// <returns>The updated configuration projection.</returns>
    /// <remarks>
    /// The service applies the same host-only comparison, administrator invariant, mapper, commit and cache
    /// invalidation as the general portal update, and - since both routes replace the same twenty-five
    /// columns through the same mapper - the same optimistic-concurrency check. The controller performs no
    /// rule itself.
    /// </remarks>
    [HttpPut("{portalId:int}/settings")]
    [Authorize(Policy = PolicyNames.PortalAdministrator)]
    [ProducesResponseType(typeof(ApiResponse<PortalSettingsDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<ActionResult<ApiResponse<PortalSettingsDto?>>> UpdateSettingsAsync(
        int portalId,
        [FromBody] UpdatePortalSettingsRequest request,
        CancellationToken cancellationToken)
    {
        Result<PortalSettingsDto?> outcome = await _portalService
            .UpdatePortalSettingsAsync(portalId, request, cancellationToken)
            .ConfigureAwait(false);

        return this.Complete(outcome);
    }

    /// <summary>Lists the accounts a portal may designate as its administrator.</summary>
    /// <param name="portalId">The portal identifier, supplied only by the route.</param>
    /// <param name="cancellationToken">Abandons the read when the caller disconnects.</param>
    /// <returns>The candidate accounts, which may be empty.</returns>
    /// <remarks>
    /// THE PORTAL COMES FROM THE ROUTE, and that is the whole reason this action exists. The role resources
    /// resolve their tenant from the caller's own context rather than from a path segment, so none of them
    /// can enumerate the administrators of a portal other than the caller's own - which left the settings
    /// screen able to display the stored administrator and unable to offer a replacement.
    /// </remarks>
    [HttpGet("{portalId:int}/administrators")]
    [Authorize(Policy = PolicyNames.PortalAdministrator)]
    [ProducesResponseType(typeof(ApiResponse<IReadOnlyList<PortalAdministratorDto>>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ApiResponse<IReadOnlyList<PortalAdministratorDto>?>>> ListAdministratorsAsync(
        int portalId,
        CancellationToken cancellationToken)
    {
        Result<IReadOnlyList<PortalAdministratorDto>?> outcome = await _portalService
            .ListAdministratorCandidatesAsync(portalId, cancellationToken)
            .ConfigureAwait(false);

        return this.Complete(outcome);
    }
}
