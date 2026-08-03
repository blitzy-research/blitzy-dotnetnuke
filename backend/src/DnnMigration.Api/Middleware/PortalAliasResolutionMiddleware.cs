using DnnMigration.Api.ErrorHandling;
using DnnMigration.Domain.Abstractions.Services;
using DnnMigration.Domain.Common;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Infrastructure;

namespace DnnMigration.Api.Middleware;

/// <summary>
/// Resolves which portal an inbound request belongs to, from the host name the caller used, and refuses
/// the request when it cannot be resolved to exactly one.
/// </summary>
/// <remarks>
/// <para>
/// THIS IS THE ONE PLACE THE TENANT IS READ OUT OF THE TRANSPORT. Everything downstream consumes the
/// resolved snapshot through an abstraction and can no longer name the host name, the request or the
/// pipeline, which is what replaces the legacy arrangement where any code anywhere could reach the
/// ambient per-request item bag and read - or replace - the tenant.
/// </para>
/// <para>
/// RESOLUTION IS ON THE HOST NAME AND THE REQUEST PATH TOGETHER, the host exactly as sent with its port.
/// That is the shape the alias column stores - a bare host for a parent portal, host-and-path for a child -
/// and comparing anything else against it would not match. No scheme is prepended, no trailing slash is
/// added and no case is imposed: case is the column collation's business, and under the default
/// case-insensitive collation host names compare case-insensitively as host names should. The address is
/// built by the shared helper alongside this file, so every stage that resolves a tenant asks the same
/// question of the same value.
/// </para>
/// <para>
/// VIRTUAL-PATH ALIASES ARE RESOLVED, which the legacy product required and an earlier revision of this
/// component did not do. A child portal was addressed by a path segment beneath a shared host - the signup
/// screen composed and stored exactly "domain/segment" at Signup.ascx.vb:L232-L236 - so the alias column may
/// carry path segments and the host alone does not distinguish a parent from its children. The holder builds
/// a candidate chain from the address, most-specific-first, and prefers the LONGEST candidate that matches,
/// which is the same preference the legacy request-side walk expressed by stopping at the first recognised
/// directory. It costs one round trip for the whole chain rather than one per segment. A parent and its child
/// are both expected to match, so more than one match across DIFFERENT candidates is the ordinary case and
/// not an ambiguity; two rows for the SAME address still is one, and is still refused.
/// </para>
/// <para>
/// THE PATH SEGMENT THAT IDENTIFIED THE TENANT IS REMOVED FROM THE ROUTABLE PATH BEFORE ROUTING RUNS, by the
/// sibling path-base stage. It has to be: this stage runs after routing, by mandated order, and a child's
/// request path would otherwise reach routing with the tenant's own segment still on the front and match no
/// route at all. Resolving the tenant correctly and then answering 404 for every request beneath it would be
/// no better than not resolving it. The two stages share both the address helper and the exemption list, so
/// neither can resolve a tenant the other would have exempted.
/// </para>
/// <para>
/// FAILING TO RESOLVE NEVER INVENTS A TENANT, AND NOW REFUSES THE ENDPOINTS THAT DEPEND ON ONE. There is no
/// default portal and no anonymous tenant to fall back to: an unknown host, an ambiguous one, or a portal
/// missing facts a snapshot needs all leave the request with no tenant at all, and the reason is logged for
/// the operator who must correct it. Until this component was corrected it then simply CONTINUED, on every
/// path, on the stated grounds that portal-scoped authorisation would deny a tenant-less request anyway. That
/// premise no longer holds and should never have been relied upon: the portal-administrator policy is now
/// anchored to the portal named in the ROUTE - which is what closed a cross-tenant defect - so it no longer
/// requires a resolved tenant, and an endpoint whose only possible source of a tenant is the host name would
/// have run with none.
/// </para>
/// <para>
/// A BLANKET REFUSAL IS STILL WRONG, so the refusal is scoped by how the endpoint names its tenant. A route
/// carrying a <c>portalId</c> segment names it in the route and is served: refusing those would lock a host
/// account out of administering any portal from a management host name that is deliberately not an alias, and
/// would add nothing, because the route portal is proved against authoritative administrator membership rather
/// than against the host name. An endpoint marked <see cref="TenantOptionalAttribute"/> is served for the
/// reason its mark states - installation-wide reference data, a tenant named by the caller's token or its own
/// request, or a bootstrap and repair path that must stay reachable precisely BECAUSE the aliases are wrong.
/// Everything else is refused, so an endpoint added later that forgets the question fails closed.
/// </para>
/// <para>
/// THE REFUSAL DISCLOSES NOTHING AND LOCKS NOBODY OUT OF SIGNING IN. It is written only where a request has
/// already passed authorisation - this component is ordered after the authorisation middleware, which
/// short-circuits an unauthenticated or unauthorised caller before reaching here - so an anonymous caller
/// cannot use the status code to enumerate which host names the installation serves; it receives its 401
/// either way. The sign-in endpoint carries the tenant-optional mark and stays reachable from a host that has
/// not been configured yet, which is what lets an operator obtain a session and repair the configuration. The
/// body names neither the host nor which of the three resolution failures occurred.
/// </para>
/// <para>
/// TWO CLASSES OF PATH ARE EXEMPT, and the exemptions are as load-bearing as the resolution. The container
/// health probe must answer before any portal exists in the database at all: the compose file makes the
/// front-end service's start-up conditional on the API reporting healthy, so a health endpoint that
/// required a configured alias would leave a freshly deployed installation permanently unhealthy and the
/// front end permanently unstarted. The interactive API description is exempt for the same practical
/// reason - it describes the API rather than serving a tenant's data. Neither exemption reaches anything
/// that reads tenant data, and no portal-scoped endpoint is exempt.
/// </para>
/// <para>
/// RESOLUTION IS PERFORMED THROUGH AN IDEMPOTENT ENTRY POINT, so this middleware and the portal
/// administrator authorisation handler can both insist on a resolved tenant without either depending on
/// having run first. That matters because the mandated pipeline order places authorisation ahead of this
/// middleware while the security property required is that no protected request is authorised without a
/// resolved tenant; whichever of the two runs first performs the work, and the other observes the
/// identical outcome rather than resolving again.
/// </para>
/// </remarks>
internal sealed class PortalAliasResolutionMiddleware
{
    /// <summary>
    /// Path prefixes that are served without a resolved tenant. See the type remarks for why each is here;
    /// nothing that reads tenant data may be added.
    /// </summary>
    private static readonly string[] ExemptPathPrefixes =
    [
        "/health",
        "/swagger",
        "/openapi",
    ];

    /// <summary>
    /// Route value naming the tenant. Spelled identically to the value the authorisation handlers read, so
    /// one route segment name governs both the policy and this refusal.
    /// </summary>
    private const string PortalRouteValueKey = "portalId";

    /// <summary>
    /// Failure code carried as the problem type. Shared verbatim with the module-definitions endpoint, which
    /// refuses the same condition, so the two are one behaviour rather than two spellings of it.
    /// </summary>
    private const string TenantUnresolvedCode = "portal.tenant_unresolved";

    /// <summary>Wording reported to the caller. Names no host and no resolution failure.</summary>
    private const string TenantUnresolvedDetail =
        "This request could not be associated with a portal.";

    /// <summary>The media type an RFC 7807 payload is served as.</summary>
    private const string ProblemContentType = "application/problem+json";

    private readonly RequestDelegate _next;
    private readonly ILogger<PortalAliasResolutionMiddleware> _logger;

    /// <summary>
    /// Initialises the middleware.
    /// </summary>
    /// <param name="next">The next component in the pipeline.</param>
    /// <param name="logger">Records why a request was refused, for the operator who must fix it.</param>
    /// <exception cref="ArgumentNullException">
    /// Thrown when either argument is <see langword="null"/>.
    /// </exception>
    public PortalAliasResolutionMiddleware(
        RequestDelegate next,
        ILogger<PortalAliasResolutionMiddleware> logger)
    {
        ArgumentNullException.ThrowIfNull(next);
        ArgumentNullException.ThrowIfNull(logger);

        _next = next;
        _logger = logger;
    }

    /// <summary>
    /// Resolves the tenant, when the request host identifies one, and continues the pipeline either way.
    /// </summary>
    /// <remarks>
    /// The tenant holder and the problem-details writer are taken per invocation rather than through the
    /// constructor because this middleware is a single long-lived instance while the holder is scoped to
    /// one request; capturing a scoped service in a longer-lived one would serve the first request's tenant
    /// to every request after it, which is the most severe form of the defect this component exists to
    /// prevent.
    /// </remarks>
    /// <param name="context">The current request.</param>
    /// <param name="portalContext">Resolves and holds the tenant for this request.</param>
    /// <param name="problemDetailsFactory">
    /// Builds the refusal payload, so the body is this API's own vocabulary with its trace identifier rather
    /// than a second spelling declared here.
    /// </param>
    /// <returns>A task that completes when the request has been handled.</returns>
    public async Task InvokeAsync(
        HttpContext context,
        IPortalContextHolder portalContext,
        ProblemDetailsFactory problemDetailsFactory)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(portalContext);
        ArgumentNullException.ThrowIfNull(problemDetailsFactory);

        if (IsExemptPath(context.Request.Path))
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        // The address comes from the shared helper, which reads the host together with the request's full
        // path so that a child portal addressed beneath a path segment resolves. Resolution is memoised, so
        // on a request the path-base stage has already resolved this call observes that outcome rather than
        // reading the store again - and because both stages ask through the same helper, neither can be
        // handed an address the other would not have produced.
        string address = TenantAddress.Of(context);

        Result outcome = await portalContext
            .EnsureResolvedAsync(address, context.RequestAborted)
            .ConfigureAwait(false);

        if (!outcome.IsSuccess)
        {
            LogUnresolved(address, outcome);

            if (RequiresResolvedTenant(context))
            {
                await WriteTenantUnresolvedAsync(context, problemDetailsFactory).ConfigureAwait(false);
                return;
            }
        }

        await _next(context).ConfigureAwait(false);
    }

    /// <summary>
    /// Determines whether the matched endpoint can only obtain its tenant from the host name, and therefore
    /// must not be served when the host name resolved to none.
    /// </summary>
    /// <param name="context">The current request.</param>
    /// <returns><see langword="true"/> when the request must be refused.</returns>
    /// <remarks>
    /// <para>
    /// NO MATCHED ENDPOINT MEANS NO REFUSAL. A request that matched no route is a 404 and that answer belongs
    /// to routing; refusing it here would replace a truthful "no such address" with a misleading one, and
    /// would tell a caller probing for addresses that the host is unconfigured.
    /// </para>
    /// <para>
    /// A <c>portalId</c> ROUTE VALUE IS INTRINSIC PROOF. The route value is set by route matching alone - it
    /// cannot be supplied through the query string or a header - so its presence means the tenant arrives by a
    /// means other than the host name. It is not trusted as an authorisation decision: the portal-administrator
    /// policy verifies administration of that exact portal against stored role membership, and every service
    /// verifies that the record it acts on belongs to it.
    /// </para>
    /// </remarks>
    private static bool RequiresResolvedTenant(HttpContext context)
    {
        Endpoint? endpoint = context.GetEndpoint();
        if (endpoint is null)
        {
            return false;
        }

        if (context.Request.RouteValues.ContainsKey(PortalRouteValueKey))
        {
            return false;
        }

        return endpoint.Metadata.GetMetadata<TenantOptionalAttribute>() is null;
    }

    /// <summary>
    /// Refuses a tenant-dependent request with the shared RFC 7807 vocabulary.
    /// </summary>
    /// <param name="context">The current request.</param>
    /// <param name="problemDetailsFactory">Builds the payload.</param>
    /// <returns>A task that completes when the refusal has been written.</returns>
    /// <remarks>
    /// <para>
    /// THE STATUS IS 403, NOT 400 AND NOT 404. The request is well formed, so it is not a bad request; the
    /// address exists, so it is not absent. What is missing is the caller's entitlement to a tenant on this
    /// host, which is a refusal to serve - and it is the same status, code and wording the module-definitions
    /// endpoint already answered with for exactly this condition, so a client sees one behaviour rather than
    /// two.
    /// </para>
    /// <para>
    /// NEITHER THE HOST NAME NOR THE REASON REACHES THE CALLER. The host name is attacker-supplied text and
    /// echoing it is a reflection vector; distinguishing an unknown host from an ambiguous one or from a
    /// misconfigured portal would let a caller probe the installation's alias table. The operator who needs
    /// all three reads them from the log entry written immediately before this.
    /// </para>
    /// <para>
    /// The response is guarded on <c>HasStarted</c> because a component ordered earlier may already have begun
    /// writing; overwriting a started response throws, which would turn a refusal into a server fault.
    /// </para>
    /// </remarks>
    private static async Task WriteTenantUnresolvedAsync(
        HttpContext context,
        ProblemDetailsFactory problemDetailsFactory)
    {
        if (context.Response.HasStarted)
        {
            return;
        }

        context.Response.StatusCode = StatusCodes.Status403Forbidden;

        ProblemDetails problem = problemDetailsFactory.CreateProblemDetails(
            context,
            statusCode: StatusCodes.Status403Forbidden,
            detail: TenantUnresolvedDetail,
            type: ApiResults.BuildProblemType(TenantUnresolvedCode));

        context.Response.ContentType = ProblemContentType;

        await context.Response
            .WriteAsJsonAsync(problem, problem.GetType(), options: null, contentType: ProblemContentType)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Determines whether a path is served without a resolved tenant.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Matched as a path segment prefix rather than a substring, so a tenant-scoped path that merely
    /// contains an exempt word cannot exempt itself. Case-insensitive, because a URL path's case is the
    /// caller's choice and an exemption that could be evaded by changing case would not be one.
    /// </para>
    /// <para>
    /// Visible to the sibling path-base stage, which MUST apply the identical list. Resolution is memoised
    /// per request, so a stage that resolved a tenant for a path this list exempts would make the exemption
    /// here meaningless - the work would already have been done. One list, read from one place, removes that
    /// possibility rather than relying on two copies staying equal.
    /// </para>
    /// </remarks>
    /// <param name="path">The request path.</param>
    /// <returns><see langword="true"/> when the path is exempt.</returns>
    internal static bool IsExemptPath(PathString path)
    {
        foreach (string prefix in ExemptPathPrefixes)
        {
            if (path.StartsWithSegments(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Records why no tenant was resolved, for the operator who must correct it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// THE LEVEL DISTINGUISHES WHOSE PROBLEM IT IS. A host name that matches no alias is a request addressed
    /// to something this installation does not serve, which is routine and is recorded as a warning. A host
    /// name that matches more than one alias, or one whose portal is missing facts it needs, is an
    /// installation an operator must repair - the request was well formed and would have resolved against a
    /// correctly configured installation - so it is recorded as an error.
    /// </para>
    /// <para>
    /// THE REASON GOES TO THE LOG AND NOWHERE ELSE. Nothing about which of the three refusals occurred, and
    /// no host name, reaches the caller: the host name is attacker-supplied text and echoing it back is a
    /// reflection vector, and distinguishing "unknown host" from "ambiguous host" in a response would let an
    /// unauthenticated caller enumerate which host names this installation is configured for. The operator
    /// who needs the distinction reads it here.
    /// </para>
    /// </remarks>
    /// <param name="address">
    /// The host name and path that failed to resolve, for the log only. The path is included because a child
    /// portal is identified by it, so an operator diagnosing an unreachable child needs to see the whole
    /// address that was probed rather than only its host.
    /// </param>
    /// <param name="outcome">The failed resolution outcome.</param>
    private void LogUnresolved(string address, Result outcome)
    {
        string reasonCode = outcome.Reason?.Code ?? "PORTAL_ALIAS_UNRESOLVED";

        bool unknownHost = string.Equals(
            reasonCode,
            IPortalContextHolder.NotFoundReasonCode,
            StringComparison.Ordinal);

        // Structured, so the host name is a queryable property rather than text spliced into a message.
        // A Host header is routing information rather than personal data, and it is the one fact an
        // operator needs in order to correct the alias configuration.
        if (unknownHost)
        {
            _logger.LogWarning(
                "The host name {RequestHost} does not identify a configured portal, so this request " +
                "continues with no tenant. Reason code {ReasonCode}.",
                address,
                reasonCode);

            return;
        }

        _logger.LogError(
            "The tenant for host name {RequestHost} could not be resolved, so this request continues with " +
            "no tenant. Reason code {ReasonCode}. Detail: {ReasonMessage}. This is an installation " +
            "configuration defect and every request to this host will be unable to reach tenant-scoped " +
            "endpoints until it is corrected.",
            address,
            reasonCode,
            outcome.Reason?.Message ?? "none reported");
    }
}
