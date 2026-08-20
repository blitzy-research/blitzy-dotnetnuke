using System.Security.Cryptography;
using System.Text;
using DnnMigration.Api.ErrorHandling;
using DnnMigration.Domain.Abstractions.Services;
using DnnMigration.Domain.Common;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Infrastructure;

namespace DnnMigration.Api.Middleware;

/// <summary>
/// Resolves which portal an inbound request belongs to, from the host name the caller used, and refuses the
/// request when it cannot be resolved to exactly one.
/// </summary>
/// <remarks>
/// <para>
/// THIS IS THE ONE PLACE THE TENANT IS READ OUT OF THE TRANSPORT. Everything downstream consumes the
/// resolved snapshot through an abstraction and can no longer name the host name, the request or the
/// pipeline, which is what replaces the legacy arrangement where any code anywhere could reach the ambient
/// per-request item bag and read - or replace - the tenant.
/// </para>
/// <para>
/// THE PATH SEGMENT THAT IDENTIFIED THE TENANT IS REMOVED FROM THE ROUTABLE PATH BEFORE ROUTING RUNS, by
/// the sibling path-base stage. It has to be: this stage runs after routing, because deciding whether an
/// endpoint needs a tenant requires the matched endpoint's metadata, and a child's request path would
/// otherwise reach routing with the tenant's own segment still on the front and match no route at all.
/// </para>
/// </remarks>
public sealed class PortalAliasResolutionMiddleware
{
    // The legacy per-request composite is gone.

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
    /// Failure code carried as the problem type when a request that can only learn its tenant from the host
    /// name is refused because the host name identified none.
    /// </summary>
    /// <remarks>
    /// PUBLIC BECAUSE IT IS THE CANONICAL SPELLING, not because anything outside this type needs to
    /// construct one. Four controllers refuse the identical condition from inside an action, when the
    /// endpoint was reached with a tenant-naming route but the named portal still could not be resolved,
    /// and each currently repeats this literal privately.
    /// </remarks>
    public const string TenantUnresolvedCode = "portal.tenant_unresolved";

    /// <summary>Wording reported to the caller. Names no host and no resolution failure.</summary>
    private const string TenantUnresolvedDetail =
        "This request could not be associated with a portal.";

    /// <summary>The media type an RFC 7807 payload is served as.</summary>
    private const string ProblemContentType = "application/problem+json";

    private readonly RequestDelegate _next;

    /// <summary>Initialises the middleware.</summary>
    /// <param name="next">The next component in the pipeline.</param>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="next"/> is <see langword="null"/>.
    /// </exception>
    /// <remarks>
    /// This stage takes no logger of its own any more. It reports nothing: diagnosing an unresolvable host
    /// is owned by the pre-routing stage, which sees every request whether or not authorisation later
    /// refuses it, and this stage's remaining job is the refusal alone.
    /// </remarks>
    public PortalAliasResolutionMiddleware(RequestDelegate next)
    {
        ArgumentNullException.ThrowIfNull(next);

        _next = next;
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
    /// Builds the refusal payload, so the body is this API's own vocabulary with its trace identifier
    /// rather than a second spelling declared here.
    /// </param>
    /// <returns>A task that completes when the request has been handled.</returns>
    public async Task InvokeAsync(HttpContext context,
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

        string address = TenantAddress.Of(context);

        // MIGRATION: the three fuzzy fallback stages are deliberately not reproduced.
        Result outcome = await portalContext
            .EnsureResolvedAsync(address, context.RequestAborted)
            .ConfigureAwait(false);

        if (!outcome.IsSuccess)
        {
            // THE DIAGNOSIS IS NOT WRITTEN HERE. TenantPathBaseMiddleware, which runs before routing and
            // resolves the tenant for every request, writes it instead, so the entry exists whatever this
            // stage or authorisation goes on to do with the request.
            if (RequiresResolvedTenant(context) && !AuthenticationRefusesFirst(context))
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
    /// THE ONE EXEMPTION IS THE DECLARED ONE. <see cref="TenantOptionalAttribute"/> marks the endpoints
    /// that genuinely read no tenant - sign-in, which must be reachable from an unconfigured host so an
    /// operator can obtain a session; the alias endpoints that repair the very configuration that failed;
    /// and installation-wide reference data.
    /// </remarks>
    private static bool RequiresResolvedTenant(HttpContext context)
    {
        Endpoint? endpoint = context.GetEndpoint();
        if (endpoint is null)
        {
            return false;
        }

        return endpoint.Metadata.GetMetadata<TenantOptionalAttribute>() is null;
    }

    /// <summary>
    /// Determines whether authorisation will refuse this request anyway, for want of an authenticated
    /// caller, so that the tenant refusal must stay silent and let it.
    /// </summary>
    /// <param name="context">The current request.</param>
    /// <returns>
    /// <see langword="true"/> when the caller is unauthenticated and the matched endpoint is not marked to
    /// allow anonymous access.
    /// </returns>
    /// <remarks>
    /// WHY THE TEST IS SOUND RATHER THAN OPTIMISTIC. The application registers a FALLBACK authorisation
    /// policy of <c>RequireAuthenticatedUser</c> (<c>AuthenticationExtensions.AddApiAuthorization</c>), so
    /// every endpoint that does not carry <see cref="IAllowAnonymous"/> is guaranteed to be refused for an
    /// unauthenticated caller whether or not it declares a policy of its own.
    /// </remarks>
    private static bool AuthenticationRefusesFirst(HttpContext context)
    {
        if (context.User.Identity?.IsAuthenticated == true)
        {
            return false;
        }

        Endpoint? endpoint = context.GetEndpoint();

        return endpoint?.Metadata.GetMetadata<IAllowAnonymous>() is null;
    }

    /// <summary>Refuses a tenant-dependent request with the shared RFC 7807 vocabulary.</summary>
    /// <param name="context">The current request.</param>
    /// <param name="problemDetailsFactory">Builds the payload.</param>
    /// <returns>A task that completes when the refusal has been written.</returns>
    /// <remarks>
    /// THE STATUS IS 403, NOT 400 AND NOT 404. The request is well formed, so it is not a bad request; the
    /// address exists, so it is not absent.
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

    /// <summary>Determines whether a path is served without a resolved tenant.</summary>
    /// <remarks>
    /// Matched as a path segment prefix rather than a substring, so a tenant-scoped path that merely
    /// contains an exempt word cannot exempt itself. Case-insensitive, because a URL path's case is the
    /// caller's choice and an exemption that could be evaded by changing case would not be one.
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

    /// <summary>Records why no tenant was resolved, for the operator who must correct it.</summary>
    /// <param name="logger">
    /// The logger of the stage that observed the failure. the observing stage supplies its own logger
    /// rather than this type holding one, because the stage that DIAGNOSES an unresolved tenant - the
    /// pre-routing stage that populates it, which runs before authorisation - is not the stage that REFUSES
    /// one.
    /// </param>
    /// <param name="address">The address that failed to resolve.</param>
    /// <param name="outcome">The failed resolution outcome.</param>
    internal static void LogUnresolved(ILogger logger, string address, Result outcome)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(outcome);

        // AND NEITHER IS THE HOST NAME, WHICH IS THE REMAINING HALF OF THE SAME FIX. A bounded, sanitised
        // HOST CANDIDATE was kept here on the argument that an operator needs it in order to add an alias
        // row. Bounding a value stops it forging a log line; it does not stop it disclosing one.
        string fingerprint = TenantAddress.FingerprintOf(address);

        string reasonCode = outcome.Reason?.Code ?? "PORTAL_ALIAS_UNRESOLVED";

        bool unknownHost = string.Equals(
            reasonCode,
            IPortalContextHolder.NotFoundReasonCode,
            StringComparison.Ordinal);

        // Structured, so each fact is a queryable property rather than text spliced into a message.
        if (unknownHost)
        {
            logger.LogWarning(
                "The address behind fingerprint {AddressFingerprint} does not identify a configured " +
                "portal, so this request continues with no tenant. Reason code {ReasonCode}.",
                fingerprint,
                reasonCode);

            return;
        }

        logger.LogError(
            "The tenant for the address behind fingerprint {AddressFingerprint} could not be resolved, so " +
            "this request continues with no tenant. Reason code {ReasonCode}. Detail: {ReasonMessage}. " +
            "This is an installation configuration defect and every request to that address will be unable " +
            "to reach tenant-scoped endpoints until it is corrected.",
            fingerprint,
            reasonCode,
            outcome.Reason?.Message ?? "none reported");
    }
}
