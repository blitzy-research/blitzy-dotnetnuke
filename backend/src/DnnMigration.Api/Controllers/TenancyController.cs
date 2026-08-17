using Asp.Versioning;

using DnnMigration.Api.ErrorHandling;
using DnnMigration.Api.Extensions;
using DnnMigration.Api.Middleware;
using DnnMigration.Application.Abstractions;
using DnnMigration.Application.Dtos.Common;
using DnnMigration.Application.Dtos.Portal;
using DnnMigration.Domain.Common;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace DnnMigration.Api.Controllers;

/// <summary>
/// Answers the one addressing question a browser cannot answer for itself: whether the first segment of its
/// own address names a configured tenant.
/// </summary>
/// <remarks>
/// <para>
/// <strong>WHY THIS ENDPOINT EXISTS AT ALL.</strong> A portal may be addressed as a path beneath a shared
/// host, so a single-page application has to decide - before it creates its router - whether to hold its
/// first path segment aside as a tenant prefix or hand it to the router as part of a route. The shape rules
/// in <see cref="PortalAliasTopology"/> narrow that question and cannot settle it, because a typo and a real
/// child alias have the same shape. The client used to guess, and the guess was measured to fail badly: a
/// mistyped one-segment address was claimed as a tenant, the router never saw the address it had been
/// given, the not-found view became unreachable, and every request thereafter was issued beneath a prefix no
/// portal owned - silently, with no way out but editing the address bar.
/// </para>
/// <para>
/// <strong>WHY IT IS ANONYMOUS.</strong> The address most in need of resolving is frequently the sign-in
/// address itself, so requiring a credential would make the answer unobtainable exactly when it is needed.
/// </para>
/// <para>
/// <strong>WHY THAT IS NOT AN ENUMERATION SURFACE.</strong> Whether a portal answers at a given address is
/// already observable by visiting that address - it is what an address MEANS. Nothing here reports a
/// portal's identity, name, settings or existence as a record; the response carries the submitted segment
/// and one boolean. It is bounded by its own rate-limiting budget so it cannot be used as a cheap probe
/// loop, and a segment the topology could never have stored is answered without reading the store at all.
/// </para>
/// </remarks>
[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/tenancy")]
// ⚠ WITHOUT THIS MARK THIS ENDPOINT WOULD BE REFUSED EXACTLY WHEN IT IS NEEDED.
// `PortalAliasResolutionMiddleware` refuses any endpoint that reaches it with no resolved tenant unless the
// endpoint opts out - and an unresolved tenant is the precise circumstance this endpoint exists to describe.
// A host that resolves to no portal, or a path prefix that names none, is the question, not an error.
[TenantOptional(
    "This endpoint's whole purpose is to report whether an address names a tenant, so it must be reachable "
    + "from an address that names none; it reads no tenant-scoped data and returns the submitted segment and "
    + "one boolean.")]
// Declared for the same reason every other controller in this API declares it: the response is JSON, and an
// operation that omits this advertises `text/plain` in the published document, which is a parsing path a
// client would have to discover rather than read.
[Produces("application/json")]
public sealed class TenancyController : ControllerBase
{
    /// <summary>The portal service, which owns the alias reader this question is answered from.</summary>
    private readonly IPortalService _portals;

    /// <summary>Initialises a new instance of the <see cref="TenancyController"/> class.</summary>
    /// <param name="portals">The portal service.</param>
    /// <exception cref="ArgumentNullException">When <paramref name="portals"/> is <see langword="null"/>.</exception>
    public TenancyController(IPortalService portals)
    {
        ArgumentNullException.ThrowIfNull(portals);

        _portals = portals;
    }

    /// <summary>Reports whether one leading path segment names a configured tenant beneath this host.</summary>
    /// <param name="segment">The first path segment of the address being resolved, without separators.</param>
    /// <param name="cancellationToken">Abandons the request when the caller disconnects.</param>
    /// <returns>The segment, echoed back, and whether it may be held aside as a tenant path prefix.</returns>
    /// <remarks>
    /// <para>
    /// <strong>ALWAYS 200, NEVER A REFUSAL FOR AN UNKNOWN SEGMENT.</strong> "This is not a tenant path" is
    /// the ANSWER for a typo, not an error: the caller acts on it by handing the segment to its router, which
    /// is precisely what produces the not-found view the defect made unreachable. A refusal would give the
    /// caller neither answer and leave it where it started. A missing or blank segment is likewise answered
    /// rather than refused, so no client can be blocked by getting the question slightly wrong.
    /// </para>
    /// <para>
    /// The HOST is taken from the request rather than accepted as a parameter, deliberately: a caller that
    /// could name the host could ask about a tenancy that has nothing to do with the address it was served
    /// from.
    /// </para>
    /// </remarks>
    [HttpGet("path-prefix")]
    [AllowAnonymous]
    // Bounded on the session-read budget rather than the credential budget. This is an unauthenticated read
    // that a legitimate client issues at most once per document load, so it needs a ceiling - but sharing the
    // credential window would let address resolution consume a sign-in allowance and lock a user out of a
    // screen they had not yet reached.
    [EnableRateLimiting(RateLimitingExtensions.SessionReadPolicyName)]
    [ProducesResponseType(typeof(ApiResponse<TenantPathPrefixDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status429TooManyRequests)]
    public async Task<ActionResult<ApiResponse<TenantPathPrefixDto>>> GetPathPrefixAsync(
        [FromQuery] string? segment,
        CancellationToken cancellationToken)
    {
        // `Host.Value` carries the port when one is present, which is required rather than incidental: an
        // alias is stored with its port, so `localhost:4202` and `localhost` are different addresses.
        string hostAuthority = Request.Host.Value ?? string.Empty;

        Result<TenantPathPrefixDto> outcome = await _portals
            .ResolveTenantPathPrefixAsync(hostAuthority, segment ?? string.Empty, cancellationToken)
            .ConfigureAwait(false);

        return this.Complete(outcome);
    }
}
