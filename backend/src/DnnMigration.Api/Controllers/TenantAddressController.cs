using Asp.Versioning;
using DnnMigration.Api.Middleware;
using DnnMigration.Application.Dtos.Common;
using DnnMigration.Application.Dtos.Portal;
using DnnMigration.Domain.Abstractions.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace DnnMigration.Api.Controllers;

/// <summary>
/// Reports how this deployment addresses the tenant that the calling request resolved to, so that a browser
/// can tell a real child-portal path segment from a mistyped one.
/// </summary>
/// <remarks>
/// <para>
/// <strong>THE QUESTION THIS ANSWERS, AND WHY ONLY THE SERVER CAN.</strong> A portal may be addressed
/// beneath one path segment of a shared host (<c>host/acme</c>), so the single-page application derives that
/// segment from the address its document was served at and puts it back onto every link and every API call.
/// The segment's SHAPE is all a client can judge, and a mistyped console route has exactly the same shape as
/// a child portal's segment. Judging on shape alone made an unrecognised segment behave like a tenant: the
/// address resolved to no portal, so every request beneath it was refused, while the console itself showed
/// its ordinary sign-in screen and blamed the credential. The alias rows that separate the two cases live
/// here, so the decision is made here and the client asks before it commits.
/// </para>
/// <para>
/// <strong>ANONYMOUS, AND IT HAS TO BE.</strong> Access tokens in this application are held in memory only,
/// so a cold load - the very moment the base address must be decided - is always unauthenticated. The
/// operation is therefore the ONE anonymous read in this API, and it is bounded to the narrowest answer that
/// settles the question: the path portion of the alias the request itself resolved by, and nothing else. An
/// unauthenticated caller learns whether the address it already knows reaches a tenant, which the sign-in
/// endpoint has always distinguished for an unconfigured address, and learns nothing about any other alias.
/// There is deliberately no listing operation, no lookup by value and no portal identifier in the response.
/// </para>
/// <para>
/// <strong>REACHING IT IS ITSELF PART OF THE ANSWER.</strong> <see cref="TenantPathBaseMiddleware"/> moves a
/// tenant's segment out of the routable path only when the address resolves to an alias carrying that
/// segment, and <c>PortalContextHolder</c> fails closed with no bare-host fallback. So a request beneath an
/// unrecognised segment keeps the segment in its path, matches no route at all, and is answered by routing
/// and the authorisation fallback rather than by this controller - which is why a client may read a refusal
/// as "that segment names no tenant" and a success as "it does".
/// </para>
/// <para>
/// <strong>NO RATE-LIMIT POLICY OF ITS OWN, DELIBERATELY.</strong> The action performs no I/O: tenant
/// resolution has already happened before routing, once per request, for EVERY request under <c>/api/</c>
/// including the ones the authorisation fallback then refuses. This operation therefore costs a caller
/// strictly less than the anonymous requests it could already make, adds no amplification, takes no body, no
/// credential and no parameter, and is charged by the pipeline's chained global concurrency limiter like
/// every other request. Attaching a credential window here would either invent a budget nothing needs or
/// share one with the session read, whose budget it would then spend.
/// </para>
/// </remarks>
[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/tenant-address")]
[AllowAnonymous]
// The endpoint's whole purpose is to report the ABSENCE of a resolved tenant as readily as its presence, so
// it must be served when the address resolves to none. It reads no tenant-owned resource: the only value it
// returns is the path portion of the alias the caller's own request resolved by, which is empty in exactly
// the case this mark covers.
[TenantOptional(
    "The operation reports whether the caller's own address resolves to a tenant and beneath which path "
    + "segment, so an unresolved address is a legitimate answer rather than a refusal; no tenant-owned "
    + "resource is read.")]
[Produces("application/json")]
public sealed class TenantAddressController : ControllerBase
{
    /// <summary>Holds the tenant the pre-routing stage resolved from this request's address, if any.</summary>
    private readonly IPortalContextHolder _portalContext;

    /// <summary>Initialises a new instance of the <see cref="TenantAddressController"/> class.</summary>
    /// <param name="portalContext">
    /// The per-request tenant holder. Injected rather than resolved on demand, and it is the only
    /// dependency: there is no service and no repository behind this operation, because the fact it reports
    /// was established by the request pipeline before routing ran.
    /// </param>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="portalContext"/> is <see langword="null"/>.
    /// </exception>
    public TenantAddressController(IPortalContextHolder portalContext)
    {
        _portalContext = portalContext ?? throw new ArgumentNullException(nameof(portalContext));
    }

    /// <summary>Describes the tenant addressing of the calling request.</summary>
    /// <returns>
    /// The path prefix this deployment addresses the resolved tenant beneath, or the empty string when the
    /// request resolved to a bare-host alias or to no portal.
    /// </returns>
    /// <remarks>
    /// <para>
    /// Synchronous, and that is not an oversight of the async-throughout rule: the rule governs I/O-bound
    /// members, and this method performs none. Resolution is memoised per request and was attempted by
    /// <see cref="TenantPathBaseMiddleware"/> before routing, so the answer is already established and
    /// awaiting it again would only add a state machine around two property reads.
    /// </para>
    /// <para>
    /// One success and no failure of its own. Every refusal a caller can receive here is produced by the
    /// transport - a method the route does not accept, or the routing and authorisation answer to an address
    /// whose unrecognised segment was never rebased - and each is declared once, for every operation in this
    /// API, by the response filter that owns transport refusals.
    /// </para>
    /// </remarks>
    [HttpGet]
    [ProducesResponseType(typeof(ApiResponse<TenantAddressDto>), StatusCodes.Status200OK)]
    public ActionResult<ApiResponse<TenantAddressDto>> Get()
    {
        // A NULL-STATE TEST, NEVER A VALUE TEST. `IsResolved` is false both before an attempt and after a
        // failed one, and reading `Current` in either state throws by contract - so the guard is what keeps
        // the unresolved case an ordinary answer instead of a fault.
        string pathPrefix = _portalContext.IsResolved
            ? TenantAddress.PathPortionOf(_portalContext.Current.PortalAlias).Value ?? string.Empty
            : string.Empty;

        // Ok(...) rather than the shared outcome translator: there is no application-layer outcome to
        // translate here, because the value being reported is a property of the transport rather than of a
        // stored record, and no branch of this method can fail.
        return Ok(ApiResponse<TenantAddressDto>.Success(new TenantAddressDto { PathPrefix = pathPrefix }));
    }
}
