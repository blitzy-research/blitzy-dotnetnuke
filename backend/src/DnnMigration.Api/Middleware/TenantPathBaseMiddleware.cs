using DnnMigration.Domain.Abstractions.Services;
using DnnMigration.Domain.Common;
using Microsoft.AspNetCore.Http;

namespace DnnMigration.Api.Middleware;

/// <summary>
/// Resolves the tenant a request belongs to before routing runs, and when that tenant is addressed by a path
/// segment beneath a shared host, moves the segment out of the routable path and into the path base.
/// </summary>
/// <remarks>
/// <para>
/// WHY THIS STAGE EXISTS. The legacy product let a child portal be addressed by a path segment beneath a
/// shared host name: the signup screen composed and stored exactly <c>domain/segment</c>
/// (<c>Website/admin/Portal/Signup.ascx.vb:L232-L236</c>), and every request beneath that segment belonged to
/// the child. Reaching the same tenant here needs two things that no single later stage can supply. The
/// tenant must be resolved from the host AND the path, because the host alone does not distinguish a parent
/// from its children. And the segment that identified the tenant must then be taken OUT of the path that
/// routing matches, because <c>/child/api/v1/portals</c> matches no route while <c>/api/v1/portals</c>
/// matches the intended one. Without the second half the first is useless: the tenant would resolve
/// correctly and every request beneath it would still answer 404.
/// </para>
/// <para>
/// WHY BEFORE ROUTING, AND WHY THAT IS NOT A REORDERING. The mandated pipeline order names ten elements and
/// fixes their order RELATIVE TO ONE ANOTHER (AAP 0.5.1.4): exception handler, correlation identifier,
/// request logging, routing, cross-origin policy, authentication, authorisation, portal-alias resolution,
/// controllers, health checks. Every one of those keeps its position: nothing is moved, and in particular the
/// named portal-alias-resolution stage still runs after authorisation exactly where it did. This stage is a
/// new, un-named component, and it MUST precede routing because a path base cannot be established after the
/// path has already been matched. Placing it here is therefore additive rather than a re-ordering of the
/// mandated sequence.
/// </para>
/// <para>
/// WHY IT DOES NOT DUPLICATE THE LATER RESOLUTION. Resolution is memoised for the lifetime of the request, so
/// this stage performs the work once and the later stages - the named resolution middleware and the
/// portal-administration authorisation handler - observe the identical outcome without resolving again. All
/// three ask for it through the same address helper, so no two of them can disagree about which tenant the
/// request belongs to.
/// </para>
/// <para>
/// WHAT THIS STAGE OWNS AND WHAT IT DOES NOT. It owns POPULATION and DIAGNOSIS; the named stage owns the
/// REFUSAL. Diagnosis moved here because the named stage is ordered after authorisation, so a request
/// authorisation refused never reached it - and an unresolvable host name is one of the likeliest causes of
/// that refusal, which left an operator with a 403 and no entry explaining it. The refusal cannot move here in
/// return: deciding whether an endpoint needs a tenant reads the matched endpoint's metadata, and routing has
/// not run yet. The wording of the diagnosis is still declared on the named stage, so one component owns it
/// and the two cannot drift.
/// </para>
/// <para>
/// WHY THE PATH BASE AND NOT A REWRITE. Assigning the path base preserves the original address for anything
/// that needs to generate a link back to the caller: URL generation prepends the path base, so a
/// <c>Location</c> header produced downstream still names the child's address rather than the parent's.
/// Rewriting the path and discarding the prefix would route correctly and then hand out links that reach the
/// wrong tenant, which is a worse defect than the one being fixed because it is invisible until a caller
/// follows one.
/// </para>
/// <para>
/// THE EXEMPTIONS MATCH THE LATER STAGE'S EXACTLY, and that is a requirement rather than a convenience. The
/// container health probe answers before any portal exists in the database, and the interactive API
/// description describes the API rather than serving a tenant's data. If this stage resolved a tenant for a
/// path the later stage exempts, the memoised outcome would make the exemption there meaningless - the work
/// would already have been done - so the two lists are kept identical and read from one place.
/// </para>
/// </remarks>
internal sealed class TenantPathBaseMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<TenantPathBaseMiddleware> _logger;

    /// <summary>
    /// Initialises the middleware.
    /// </summary>
    /// <param name="next">The next component in the pipeline.</param>
    /// <param name="logger">Records a rebased request, for the operator diagnosing a child portal.</param>
    /// <exception cref="ArgumentNullException">
    /// Thrown when either argument is <see langword="null"/>.
    /// </exception>
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
    /// The tenant holder is taken per invocation rather than through the constructor because this middleware
    /// is one long-lived instance while the holder is scoped to a single request; capturing a scoped service
    /// in a longer-lived one would serve the first request's tenant to every request after it.
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

        // SEC-030: THE AUTHORITATIVE TENANT IS POPULATED HERE, AND SO IS ITS DIAGNOSIS. This stage runs before
        // routing and therefore before authorisation, so by the time any authorisation handler asks which
        // tenant a request belongs to, the answer is already established - and a failure to establish one has
        // already been recorded. Both properties matter and neither was true before: the diagnosis lived in the
        // named stage that runs AFTER authorisation, so a request authorisation refused - which an unresolvable
        // host makes far more likely - produced a 403 with no entry saying why. The refusal itself stays in the
        // named stage, because deciding whether an endpoint needs a tenant requires the matched endpoint's
        // metadata, which does not exist yet here.
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

        // Matched as whole segments rather than as a string prefix, so an alias of "host/child" cannot
        // rebase a request for "/childish/api/v1/..." - a substring match here would strip four characters
        // out of the middle of an unrelated path and route the remainder somewhere nobody asked for. This
        // is the same class of defect as the legacy substring alias resolution, and it is refused for the
        // same reason.
        if (!context.Request.Path.StartsWithSegments(tenantPath, StringComparison.OrdinalIgnoreCase, out PathString remainder))
        {
            // The tenant resolved from an address that included this path, so the path must begin with the
            // alias's own segments. Reaching here means the two comparisons disagree, which is a defect
            // rather than a routine outcome, so it is recorded and the request continues UNCHANGED - a
            // guessed rebase is how a request ends up served by the wrong tenant.
            _logger.LogError(
                "The resolved portal alias carries the path {TenantPath}, which the request path does not begin with. The request was left unrebased.",
                tenantPath.Value);

            await _next(context).ConfigureAwait(false);
            return;
        }

        PathString originalPathBase = context.Request.PathBase;
        PathString originalPath = context.Request.Path;

        context.Request.PathBase = originalPathBase.Add(tenantPath);
        context.Request.Path = remainder;

        // SEC-B4: THE REBASED PATH IS NOT RECORDED. Both facts here are server-authored - the portal
        // identifier comes from the resolved snapshot and the tenant path from the stored alias row - whereas
        // the remainder is the caller's own text and would put an arbitrary path into the log on every child
        // request. The request envelope records the matched ROUTE TEMPLATE for exactly that reason, and it is
        // the value an operator should read; duplicating the raw path here would defeat it.
        _logger.LogDebug(
            "Request rebased for portal {PortalId} beneath {TenantPath}.",
            portalContext.Current.PortalId,
            tenantPath.Value);

        try
        {
            await _next(context).ConfigureAwait(false);
        }
        finally
        {
            // Restored on the way out, including when the pipeline threw. The request object outlives this
            // stage - the logging and correlation stages wrapped around it still read the path after control
            // returns - and leaving it rebased would make every entry they write describe a path the caller
            // never sent.
            context.Request.PathBase = originalPathBase;
            context.Request.Path = originalPath;
        }
    }
}
