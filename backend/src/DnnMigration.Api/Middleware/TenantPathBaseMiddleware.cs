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
/// controllers, health checks. This stage is a new, un-named component, and it MUST precede routing because a
/// path base cannot be established after the path has already been matched. Placing it there is therefore
/// additive: it displaces none of the named elements.
/// </para>
/// <para>
/// WHERE THE NAMED STAGE ACTUALLY SITS, because this file used to state it wrongly. An earlier revision of
/// these remarks asserted that the named portal-alias-resolution stage "still runs after authorisation exactly
/// where it did". It does not, and had not for some time:
/// <see cref="PortalAliasResolutionMiddleware"/> is registered AFTER <c>UseAuthentication</c> and BEFORE
/// <c>UseAuthorization</c> - see the SEC-B4 note in <c>ApplicationBuilderExtensions.UseApiPipeline</c>, which
/// moved it there deliberately so that no tenant-scoped authorisation policy is ever evaluated on a request
/// that resolved to no arrival tenant. That is a departure from the mandated ordering, argued and recorded at
/// the registration site; what is corrected here is only this file's description of it. The two facts this
/// stage's own design depends on are unaffected either way: it runs before routing, and it is the first place
/// the tenant is resolved.
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
/// REFUSAL. Two facts keep the diagnosis here, and neither depends on where the named stage is ordered.
/// Resolution is memoised for the request, so the attempt that can FAIL is the one this stage makes - by the
/// time any later stage asks, it is reading a settled answer, and recording there would either duplicate the
/// entry or describe an attempt that stage never made. And the named stage refuses CONDITIONALLY: it writes
/// nothing unless the matched endpoint requires a tenant and authentication would not refuse the request
/// first, so an unresolvable host name reaching a tenant-optional endpoint - or reaching any endpoint
/// anonymously - would leave no entry at all. Diagnosing unconditionally at the point of first resolution is
/// what makes the entry exist whatever the rest of the pipeline decides. The refusal cannot move here in
/// return: deciding whether an endpoint needs a tenant reads the matched endpoint's metadata, and routing has
/// not run yet. The wording of the diagnosis is still declared on the named stage, so one component owns it
/// and the two cannot drift.
/// </para>
/// <para>
/// WHY THE PATH BASE AND NOT A REWRITE. Assigning the path base PRESERVES the segment that identified the
/// tenant, so anything that has to hand the caller an address back can still reach it; rewriting the path and
/// discarding the prefix would route correctly and then hand out links that reach the parent, which is a worse
/// defect than the one being fixed because it is invisible until a caller follows one.
/// </para>
/// <para>
/// PRESERVING IT IS NECESSARY BUT NOT SUFFICIENT, which this file also used to state wrongly. An earlier
/// revision claimed that "URL generation prepends the path base, so a <c>Location</c> header produced
/// downstream still names the child's address rather than the parent's". The first half is true of the
/// framework's link generation and the second half did not follow, because this API composes a created
/// resource's address itself rather than through link generation - and the helper that does so read the PATH
/// alone, which under a rebased request is precisely the parent's address. Every child-tenant creation
/// therefore answered <c>201</c> with a location pointing at the shared host's root. The helper now composes
/// the path base and the path together, so the claim holds - see <c>ApiResults.Created</c>. The two halves are
/// a pair: this stage keeping the segment is what makes the correct address AVAILABLE, and that helper using
/// it is what makes it PUBLISHED.
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
    /// <summary>
    /// The closed reason code recorded when the resolved alias's path is not a prefix of the request path.
    /// </summary>
    /// <remarks>
    /// A CODE rather than a sentence, and a constant rather than a literal at the call site, because it is
    /// what an operator alerts and searches on. It is the only reason this stage declines to rebase, so the
    /// vocabulary is closed at one member; a second condition would add a second constant here rather than
    /// widening this one into prose.
    /// </remarks>
    private const string PathPrefixMismatchReasonCode = "TENANT_PATH_PREFIX_MISMATCH";

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
        // already been recorded. The diagnosis is UNCONDITIONAL here, which is why it is here: the named stage
        // records nothing unless the matched endpoint requires a tenant and authentication would not refuse the
        // request first, so an unresolvable host reaching a tenant-optional or anonymous endpoint left no entry
        // at all. The refusal itself stays in the named stage, because deciding whether an endpoint needs a
        // tenant requires the matched endpoint's metadata, which does not exist yet here.
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
            //
            // SEC-B4: THE ALIAS PATH IS NOT RECORDED, AND THAT IS THE FIX. This entry used to name the
            // tenant path verbatim. Its provenance made that look safe - it comes from the stored alias row
            // rather than from the request - but the audit design deliberately excludes alias TEXT from
            // ordinary log records whatever its provenance, because an alias identifies a tenant to anybody
            // who reads the log: a child-portal path such as /acme-legal names a customer as surely as a
            // host name does. What is recorded instead answers the same diagnostic question - which tenant,
            // how deep its alias is, and which comparison disagreed - from facts that name nobody. The
            // portal identifier is the key an operator uses to read the alias out of the database, which is
            // where it belongs.
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

        // SEC-B4: NEITHER THE REBASED PATH NOR THE ALIAS PATH IS RECORDED. The remainder is the caller's own
        // text and would put an arbitrary path into the log on every child request; the request envelope
        // records the matched ROUTE TEMPLATE for exactly that reason, and duplicating the raw path here
        // would defeat it. The ALIAS path was recorded until a review found it, and its removal is the
        // second half of that fix: it is server-authored, which is why it looked acceptable, but it is still
        // tenant-identifying text - a child-portal path names a customer - and the audit design excludes
        // alias text from ordinary records regardless of where it came from. The portal identifier and the
        // alias DEPTH describe the rebase without naming the tenant, and the identifier is what an operator
        // reads the alias itself out of the database with.
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
            // Restored on the way out, including when the pipeline threw. The request object outlives this
            // stage - the logging and correlation stages wrapped around it still read the path after control
            // returns - and leaving it rebased would make every entry they write describe a path the caller
            // never sent.
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
    /// <remarks>
    /// The DEPTH is what distinguishes the two diagnoses this stage produces - a one-segment alias whose
    /// segment did not match, from a deeper alias where a middle segment did not - without naming any
    /// segment. Empty entries are discarded so a leading separator, and a trailing one a stored alias may
    /// carry, do not inflate the count; the value has to mean the same thing for every alias or it means
    /// nothing.
    /// </remarks>
    private static int SegmentCountOf(PathString tenantPath)
    {
        string? value = tenantPath.Value;

        return string.IsNullOrEmpty(value)
            ? 0
            : value.Split('/', StringSplitOptions.RemoveEmptyEntries).Length;
    }
}
