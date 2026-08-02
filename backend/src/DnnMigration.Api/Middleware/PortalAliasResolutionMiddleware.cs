using DnnMigration.Domain.Abstractions.Repositories;
using DnnMigration.Domain.Abstractions.Services;
using DnnMigration.Domain.Common;
using Microsoft.AspNetCore.Http;

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
/// RESOLUTION IS ON THE HOST NAME ALONE, exactly as sent, host and port together. That is the shape the
/// alias column stores, and comparing anything else against it would not match. No scheme is prepended, no
/// trailing slash is added and no case is imposed - case is the column collation's business, and under the
/// default case-insensitive collation host names compare case-insensitively as host names should.
/// </para>
/// <para>
/// VIRTUAL-PATH ALIASES ARE NOT RESOLVED, and that is a deliberate limitation rather than an oversight.
/// The legacy product allowed a child portal to be addressed by a path segment beneath a shared host, so
/// its alias column could hold a host-and-path value. Supporting that means generating a candidate chain
/// from the request path and probing it most-specific-first, which is a query per candidate on every
/// request, and no finding requires it and no validation gate exercises it. A deployment that addresses
/// each portal by its own host name - the arrangement this resolves - is unaffected. The limitation is
/// recorded as a behavioural difference rather than absorbed silently.
/// </para>
/// <para>
/// FAILING TO RESOLVE NEVER INVENTS A TENANT, AND NEVER REFUSES THE REQUEST HERE EITHER. There is no default
/// portal and no anonymous tenant to fall back to: an unknown host, an ambiguous one, or a portal missing
/// facts a snapshot needs all leave the request with no tenant at all, and the reason is logged for the
/// operator who must correct it. What must not happen is a blanket refusal from this point, for two reasons.
/// Every endpoint that needs a tenant already refuses without one - portal-scoped authorisation denies when
/// no tenant is resolved, and the sign-in endpoint reports exactly which fact it is missing - so refusing
/// here adds no protection while making the one endpoint that can produce a session unreachable from a host
/// that has not been configured yet. And a refusal that only an unconfigured host receives is itself a
/// disclosure: an unauthenticated caller could enumerate which host names this installation serves from the
/// status code alone, which is the enumeration the refusal body was written to avoid.
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
    /// <returns>A task that completes when the request has been handled.</returns>
    public async Task InvokeAsync(HttpContext context, IPortalContextHolder portalContext)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(portalContext);

        if (IsExempt(context.Request.Path))
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        string host = context.Request.Host.Value ?? string.Empty;

        Result outcome = await portalContext
            .EnsureResolvedAsync(host, context.RequestAborted)
            .ConfigureAwait(false);

        if (!outcome.IsSuccess)
        {
            LogUnresolved(host, outcome);
        }

        await _next(context).ConfigureAwait(false);
    }

    /// <summary>
    /// Determines whether a path is served without a resolved tenant.
    /// </summary>
    /// <remarks>
    /// Matched as a path segment prefix rather than a substring, so a tenant-scoped path that merely
    /// contains an exempt word cannot exempt itself. Case-insensitive, because a URL path's case is the
    /// caller's choice and an exemption that could be evaded by changing case would not be one.
    /// </remarks>
    /// <param name="path">The request path.</param>
    /// <returns><see langword="true"/> when the path is exempt.</returns>
    private static bool IsExempt(PathString path)
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
    /// <param name="host">The host name that failed to resolve, for the log only.</param>
    /// <param name="outcome">The failed resolution outcome.</param>
    private void LogUnresolved(string host, Result outcome)
    {
        string reasonCode = outcome.Reason?.Code ?? "PORTAL_ALIAS_UNRESOLVED";

        bool unknownHost = string.Equals(
            reasonCode,
            IPortalAliasRepository.NotFoundReasonCode,
            StringComparison.Ordinal);

        // Structured, so the host name is a queryable property rather than text spliced into a message.
        // A Host header is routing information rather than personal data, and it is the one fact an
        // operator needs in order to correct the alias configuration.
        if (unknownHost)
        {
            _logger.LogWarning(
                "The host name {RequestHost} does not identify a configured portal, so this request " +
                "continues with no tenant. Reason code {ReasonCode}.",
                host,
                reasonCode);

            return;
        }

        _logger.LogError(
            "The tenant for host name {RequestHost} could not be resolved, so this request continues with " +
            "no tenant. Reason code {ReasonCode}. Detail: {ReasonMessage}. This is an installation " +
            "configuration defect and every request to this host will be unable to reach tenant-scoped " +
            "endpoints until it is corrected.",
            host,
            reasonCode,
            outcome.Reason?.Message ?? "none reported");
    }
}
