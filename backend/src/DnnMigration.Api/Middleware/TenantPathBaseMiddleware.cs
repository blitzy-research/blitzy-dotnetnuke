using DnnMigration.Domain.Abstractions.Services;
using DnnMigration.Domain.Common;
using Microsoft.AspNetCore.Http;

namespace DnnMigration.Api.Middleware;

/// <summary>
/// Resolves the tenant a request belongs to before routing runs, and when that tenant is addressed by a
/// path segment beneath a shared host, moves the segment out of the routable path and into the path base.
/// </summary>
/// <remarks>
/// <para>
/// WHY IT DOES NOT DUPLICATE THE LATER RESOLUTION. Resolution is memoised for the lifetime of the request,
/// so this stage performs the work once and the later stages - the named resolution middleware and the
/// portal-administration authorisation handler - observe the identical outcome without resolving again.
/// </para>
/// <para>
/// WHAT THIS STAGE OWNS AND WHAT IT DOES NOT. It owns POPULATION and DIAGNOSIS; the named stage owns the
/// REFUSAL. Two facts keep the diagnosis here, and neither depends on where the named stage is ordered.
/// </para>
/// </remarks>
internal sealed class TenantPathBaseMiddleware
{
    /// <summary>
    /// The closed reason code recorded when the resolved alias's path is not a prefix of the request path.
    /// </summary>
    private const string PathPrefixMismatchReasonCode = "TENANT_PATH_PREFIX_MISMATCH";

    private readonly RequestDelegate _next;
    private readonly ILogger<TenantPathBaseMiddleware> _logger;

    /// <summary>Initialises the middleware.</summary>
    /// <param name="next">The next component in the pipeline.</param>
    /// <param name="logger">Records a rebased request, for the operator diagnosing a child portal.</param>
    /// <exception cref="ArgumentNullException">Thrown when either argument is <see langword="null"/>.</exception>
    public TenantPathBaseMiddleware(RequestDelegate next, ILogger<TenantPathBaseMiddleware> logger)
    {
        ArgumentNullException.ThrowIfNull(next);
        ArgumentNullException.ThrowIfNull(logger);

        _next = next;
        _logger = logger;
    }

    /// <summary>
    /// Resolves the tenant and rebases the request when the tenant is addressed beneath a path segment.
    /// </summary>
    /// <param name="context">The current request.</param>
    /// <param name="portalContext">Resolves and holds the tenant for this request.</param>
    /// <returns>A task that completes when the request has been handled.</returns>
    /// <remarks>
    /// The tenant holder is taken per invocation rather than through the constructor because this
    /// middleware is one long-lived instance while the holder is scoped to a single request; capturing a
    /// scoped service in a longer-lived one would serve the first request's tenant to every request after
    /// it.
    /// </remarks>
    public async Task InvokeAsync(HttpContext context, IPortalContextHolder portalContext)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(portalContext);

        if (PortalAliasResolutionMiddleware.IsExemptPath(context.Request.Path))
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        string address = TenantAddress.Of(context);

        // THE AUTHORITATIVE TENANT IS POPULATED HERE, AND SO IS ITS DIAGNOSIS. This stage runs before
        // routing and therefore before authorisation, so by the time any authorisation handler asks which
        // tenant a request belongs to, the answer is already established - and a failure to establish one
        // has already been recorded.
        Result resolution = await portalContext
            .EnsureResolvedAsync(address, context.RequestAborted)
            .ConfigureAwait(false);

        if (!portalContext.IsResolved)
        {
            // One wording, declared once, on the component that owns the vocabulary.
            PortalAliasResolutionMiddleware.LogUnresolved(_logger, address, resolution);

            await _next(context).ConfigureAwait(false);
            return;
        }

        PathString tenantPath = TenantAddress.PathPortionOf(portalContext.Current.PortalAlias);

        // A bare-host alias is the ordinary case and needs nothing done: the routable path is already the
        // one the routes were written against.
        if (!tenantPath.HasValue)
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        if (!context.Request.Path.StartsWithSegments(tenantPath, StringComparison.OrdinalIgnoreCase, out PathString remainder))
        {
            // The tenant resolved from an address that included this path, so the path must begin with the
            // alias's own segments.
            _logger.LogError(
                "Portal {PortalId} resolved from an alias {TenantPathSegmentCount} segment(s) deep, which " +
                "the request path does not begin with. Reason code {ReasonCode}. The request was left unrebased.",
                portalContext.Current.PortalId,
                SegmentCountOf(tenantPath),
                PathPrefixMismatchReasonCode);

            await _next(context).ConfigureAwait(false);
            return;
        }

        PathString originalPathBase = context.Request.PathBase;
        PathString originalPath = context.Request.Path;

        context.Request.PathBase = originalPathBase.Add(tenantPath);
        context.Request.Path = remainder;

        // SEC-B4: NEITHER THE REBASED PATH NOR THE ALIAS PATH IS RECORDED. The remainder is the caller's
        // own text and would put an arbitrary path into the log on every child request; the request
        // envelope records the matched ROUTE TEMPLATE for exactly that reason, and duplicating the raw path
        // here would defeat it.
        _logger.LogDebug(
            "Request rebased for portal {PortalId} beneath an alias {TenantPathSegmentCount} segment(s) deep.",
            portalContext.Current.PortalId,
            SegmentCountOf(tenantPath));

        try
        {
            await _next(context).ConfigureAwait(false);
        }
        finally
        {
            // Restored on the way out, including when the pipeline threw.
            context.Request.PathBase = originalPathBase;
            context.Request.Path = originalPath;
        }
    }

    /// <summary>
    /// Counts the segments in an alias's path portion, for a log entry that must not carry the path itself.
    /// </summary>
    /// <param name="tenantPath">The alias's path portion, which may be empty.</param>
    /// <returns>
    /// The number of non-empty segments: <c>0</c> for a bare-host alias, <c>1</c> for <c>/child</c>, and so
    /// on.
    /// </returns>
    private static int SegmentCountOf(PathString tenantPath)
    {
        string? value = tenantPath.Value;

        return string.IsNullOrEmpty(value)
            ? 0
            : value.Split('/', StringSplitOptions.RemoveEmptyEntries).Length;
    }
}
