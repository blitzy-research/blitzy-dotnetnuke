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
/// sibling path-base stage. It has to be: this stage runs after routing, because deciding whether an endpoint
/// needs a tenant requires the matched endpoint's metadata, and a child's request path would otherwise reach
/// routing with the tenant's own segment still on the front and match no route at all. Resolving the tenant
/// correctly and then answering 404 for every request beneath it would be no better than not resolving it.
/// The two stages share both the address helper and the exemption list, so neither can resolve a tenant the
/// other would have exempted.
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
/// SEC-B4: THIS STAGE RUNS BETWEEN AUTHENTICATION AND AUTHORISATION, NOT AFTER AUTHORISATION. It was
/// registered after <c>UseAuthorization</c>, which meant every tenant-scoped policy - each of which
/// reconciles the caller's portal, the route's portal and the ARRIVAL portal - was evaluated on requests
/// that had no arrival portal at all, silently degrading a three-sided check to a two-sided one exactly
/// where a host name resolved to nothing. Enforcing before authorisation removes that state from every
/// policy's view. The anti-enumeration property the old position provided is preserved deliberately rather
/// than lost: see <c>AuthenticationRefusesFirst</c>.
/// </para>
/// <para>
/// A BLANKET REFUSAL IS STILL WRONG, so the refusal is scoped by how the endpoint names its tenant. A route
/// carrying a <c>portalId</c> segment names it in the route and is served: refusing those would prevent a host
/// account from administering any portal via a management host name that is deliberately not an alias, and
/// would add nothing, because the route portal is proved against authoritative administrator membership rather
/// than against the host name. An endpoint marked <see cref="TenantOptionalAttribute"/> is served for the
/// reason its mark states - installation-wide reference data, a tenant named by the caller's token or its own
/// request, or a bootstrap and repair path that must stay reachable precisely BECAUSE the aliases are wrong.
/// Everything else is refused, so an endpoint added later that forgets the question fails closed.
/// </para>
/// <para>
/// THE REFUSAL DISCLOSES NOTHING AND LOCKS NOBODY OUT OF SIGNING IN. It is withheld from a caller that
/// authorisation is certain to turn away for want of authentication - the application registers a fallback
/// policy demanding an authenticated user, so an anonymous caller on any endpoint that does not allow
/// anonymous access receives its 401 from every host name alike and cannot use the status code to enumerate
/// which host names the installation serves. The sign-in endpoint carries the tenant-optional mark and stays
/// reachable from a host that has not been configured yet, which is what lets an operator obtain a session
/// and repair the configuration. The body names neither the host nor which of the three resolution failures
/// occurred.
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
/// RESOLUTION IS PERFORMED THROUGH AN IDEMPOTENT ENTRY POINT, so this middleware, the pre-routing path-base
/// stage and the portal administrator authorisation handler can all insist on a resolved tenant without any
/// of them depending on having run first. In the composed pipeline the path-base stage resolves first, so
/// the call here ordinarily observes that outcome rather than reading the store again; the idempotence is
/// what makes the ordering a matter of correctness rather than of luck.
/// </para>
/// </remarks>
public sealed class PortalAliasResolutionMiddleware
{
    // MIGRATION: the legacy per-request composite is gone. Tenant facts used to live on a mutable object
    // parked in the ambient request-item bag - written by the page pipeline and read anywhere via
    // PortalController.vb:1210 and Default.aspx.vb:99 - whose tab property carried a public setter
    // (portal-settings source line 398, its setter at 402) and whose constructor performed the tenant read
    // while constructing (line 548). Any code could therefore replace the tenant mid-request. What replaces
    // it is an immutable, request-scoped snapshot reached only through a domain abstraction: 1397 lines and 49
    // properties reduce to the 8 facts the interface exposes, and the tab identity is no longer tenant state
    // at all - it arrives as a route parameter on the endpoints that need it.

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
    /// PUBLIC BECAUSE IT IS THE CANONICAL SPELLING, not because anything outside this type needs to construct
    /// one. Four controllers refuse the identical condition from inside an action, when the endpoint was
    /// reached with a tenant-naming route but the named portal still could not be resolved, and each currently
    /// repeats this literal privately. A client is entitled to see one failure code for one condition, so the
    /// declaration that the middleware answers with is the one they should all read. Exposing it here is the
    /// half of that consolidation this file owns; retargeting those four declarations belongs to their own
    /// files and is recorded as a hand-off rather than reached across a boundary from here.
    /// </remarks>
    public const string TenantUnresolvedCode = "portal.tenant_unresolved";

    /// <summary>Wording reported to the caller. Names no host and no resolution failure.</summary>
    private const string TenantUnresolvedDetail =
        "This request could not be associated with a portal.";

    /// <summary>The media type an RFC 7807 payload is served as.</summary>
    private const string ProblemContentType = "application/problem+json";

    private readonly RequestDelegate _next;

    /// <summary>
    /// Initialises the middleware.
    /// </summary>
    /// <param name="next">The next component in the pipeline.</param>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="next"/> is <see langword="null"/>.
    /// </exception>
    /// <remarks>
    /// This stage takes no logger of its own any more. It reports nothing: diagnosing an unresolvable host is
    /// owned by the pre-routing stage, which sees every request whether or not authorisation later refuses it,
    /// and this stage's remaining job is the refusal alone. Its rendering of that diagnosis is still declared
    /// here, as <see cref="LogUnresolved"/>, so one component owns the wording.
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
    /// Builds the refusal payload, so the body is this API's own vocabulary with its trace identifier rather
    /// than a second spelling declared here.
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

        // The address comes from the shared helper, which reads the host together with the request's full
        // path so that a child portal addressed beneath a path segment resolves. Resolution is memoised, so
        // on a request the path-base stage has already resolved this call observes that outcome rather than
        // reading the store again - and because both stages ask through the same helper, neither can be
        // handed an address the other would not have produced.
        //
        // MIGRATION: the host is taken from the request's own host value, ports included, and never from a
        // forwarded-host header. The proxy in the shipped topology is configured to forward only the client
        // address and the scheme, so a forwarded host would be unvalidated caller input; the aliases this
        // installation is configured with carry ports, which is why the port is part of the key rather than
        // trimmed off it.
        string address = TenantAddress.Of(context);

        // MIGRATION: one exact lookup replaces a wildcard containment predicate. The legacy procedure matched
        // the stored alias column against a pattern padded on both sides and then took the lowest-numbered
        // portal of whatever matched (01.00.00.SqlDataProvider:4582) - so an alias that merely sat inside
        // another portal's alias resolved to the WRONG tenant, and ties were broken by identifier order rather
        // than by correctness. The product abandoned the procedure entirely: it was dropped at
        // 02.02.00.SqlDataProvider:267 and no later script in the 88-script chain recreates it. This asks
        // instead for equality against a bounded, most-specific-first candidate chain in a single round trip,
        // and treats two rows claiming the SAME address as an ambiguity to refuse rather than a tie to break.
        //
        // MIGRATION: matching stays case-insensitive by design, not by accident. Stored aliases were
        // lower-cased on every write and on every read (PortalAliasController.vb:31, :52, :76 and :97), so a
        // case-sensitive comparison would fail to match a mixed-case host that the legacy product resolved.
        // The comparison is therefore culture-independent; no culture-sensitive lower-casing is performed
        // anywhere on this path, because the mapping of dotted-capital I differs by culture and would make
        // tenant resolution depend on the server's locale.
        //
        // MIGRATION: the three fuzzy fallback stages are deliberately not reproduced. After its exact attempt
        // the legacy resolver retried with and without a "www." label (portal-settings source 1089-1098), then
        // stripped the leading label and retried against a wildcard-domain entry, the bare domain and a
        // "www."-prefixed domain (lines 1109-1118); a second resolver additionally accepted any stored alias of
        // which the requested one was a leading prefix (lines 1171 and 1184, explained at 1165 as a workaround
        // for child portals reached through the parent's domain). Each of those widens one address into
        // several tenants. Child portals are served here by resolving the path segment that identifies them,
        // which is the accurate mechanism the prefix match was approximating.
        //
        // MIGRATION: resolution never writes. On a fresh installation the legacy resolver detected an
        // unconfigured alias table and repaired it in place during a read (lines 1126-1135), then evicted its
        // cache and retried. That made a plain retrieval mutate the database. The schema is immutable to this
        // application and the installer lies outside this scope, so an unconfigured alias is reported and
        // refused, and an operator repairs it through the alias endpoints, kept reachable for that purpose.
        //
        // MIGRATION: nothing is silently substituted. When the legacy procedure could not confirm that the
        // requested tab belonged to the resolved portal it quietly swapped in that portal's lowest-numbered
        // tab (01.00.00.SqlDataProvider:4603), so a request for another tenant's page was answered with a
        // page the caller never asked for. There is no substitution here and no default tenant: an address
        // that resolves to nothing yields no tenant, and the endpoints that depend on one are refused.
        //
        // MIGRATION: the alias-collection cache is intentionally omitted rather than ported. The legacy
        // lookup table was held for the process lifetime with an absolute expiry set to the largest
        // representable instant and a priority that forbade eviction (portal-settings source 1232), so an alias
        // corrected in the database stayed wrong in memory until the application restarted. No measured
        // lifetime exists to replace it with, and inventing one would be a guess, so resolution is memoised
        // for the duration of a single request only - which removes the repeated round trip within a request
        // without letting a stale tenant outlive it.
        //
        // MIGRATION: no table-name-prefix setting is introduced. The legacy provider templated a prefix into
        // object and constraint names and the two shipped configurations disagreed about it - empty in
        // release.config:354, non-empty in development.config:352. Table and column binding here is declared
        // in the persistence layer's entity configurations instead, so this component names no database
        // object; adding a configuration key that nothing consumed would be worse than having none.
        Result outcome = await portalContext
            .EnsureResolvedAsync(address, context.RequestAborted)
            .ConfigureAwait(false);

        if (!outcome.IsSuccess)
        {
            // SEC-030: THE DIAGNOSIS IS NOT WRITTEN HERE. TenantPathBaseMiddleware, which runs before routing
            // and resolves the tenant for every request, writes it instead, so the entry exists whatever this
            // stage or authorisation goes on to do with the request. Only the endpoint-specific REFUSAL lives
            // here, because it needs the matched endpoint's metadata to know whether this endpoint requires a
            // tenant at all.
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
    /// <para>
    /// NO MATCHED ENDPOINT MEANS NO REFUSAL. A request that matched no route is a 404 and that answer belongs
    /// to routing; refusing it here would replace a truthful "no such address" with a misleading one, and
    /// would tell a caller probing for addresses that the host is unconfigured.
    /// </para>
    /// <para>
    /// SEC: A <c>portalId</c> ROUTE VALUE IS NOT AN EXEMPTION, and it used to be one. The reasoning was that a
    /// route naming its own tenant needs no host name to identify one - which is true of ROUTING and false of
    /// SECURITY. A route value is chosen by the caller, so exempting every route that carries one meant every
    /// tenant-scoped route in the API could be addressed from a host name that named a different tenant, or no
    /// tenant at all, and the arrival tenant was then never compared with anything. The tenant a caller
    /// arrived through is one of the three identities a tenant-scoped decision has to reconcile
    /// (<c>Authorization/PortalAdministrationEvaluator.IsTenantBoundAsync</c> reconciles all three), and an
    /// unresolvable one cannot be reconciled with anything - so a request that needs a tenant and has no
    /// resolvable one is refused here regardless of what its route says.
    /// </para>
    /// <para>
    /// THE ONE EXEMPTION IS THE DECLARED ONE. <see cref="TenantOptionalAttribute"/> marks the endpoints that
    /// genuinely read no tenant - sign-in, which must be reachable from an unconfigured host so an operator can
    /// obtain a session; the alias endpoints that repair the very configuration that failed; and
    /// installation-wide reference data. Each mark carries a written justification and the inventory of marks
    /// is pinned by a test, so an exemption cannot be added without review. That is what an exemption should
    /// look like: named, justified and enumerated - not implied by the shape of a route.
    /// </para>
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
    /// <para>
    /// SEC-B4: THIS IS WHAT KEEPS THE ANTI-ENUMERATION PROPERTY WHILE THE STAGE MOVES AHEAD OF
    /// AUTHORISATION. The stage used to run AFTER the authorisation middleware, and one security property
    /// depended on exactly that: an anonymous caller was turned away with 401 before it could reach a
    /// tenant refusal, so it could not tell a configured host name from an unconfigured one by reading the
    /// status code. Moving the stage in front of authorisation - which the tenant-isolation property
    /// requires, because a policy must never reconcile against an arrival portal that does not exist -
    /// would have handed that anonymous caller a 403 from the unconfigured host and a 401 from the
    /// configured one, which is an enumeration oracle for the installation's alias table.
    /// </para>
    /// <para>
    /// WHY THE TEST IS SOUND RATHER THAN OPTIMISTIC. The application registers a FALLBACK authorisation
    /// policy of <c>RequireAuthenticatedUser</c> (<c>AuthenticationExtensions.AddApiAuthorization</c>), so
    /// every endpoint that does not carry <see cref="IAllowAnonymous"/> is guaranteed to be refused for an
    /// unauthenticated caller whether or not it declares a policy of its own. Deferring to authorisation in
    /// exactly that case therefore changes nothing about WHETHER the request is refused - only about which
    /// stage refuses it and with which status - and it is the status that carried the disclosure.
    /// </para>
    /// <para>
    /// Nothing else is deferred. An AUTHENTICATED caller is refused here, before any policy runs, which is
    /// the case the move was made for; and an endpoint that genuinely allows anonymous access is refused
    /// here too, because authorisation would not stop it and it would otherwise execute with no tenant.
    /// </para>
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
    /// <para>
    /// AND NO PART OF THE ADDRESS REACHES THE LOG. The entry records a DIGEST of the full address and a
    /// closed reason code, and nothing else derived from the request. The reason message is authored text
    /// from the resolution layer and carries no caller input at all.
    /// </para>
    /// <para>
    /// THE HOST NAME ITSELF USED TO BE RECORDED, AND ITS REMOVAL IS THE FIX FOR A REPORTED DEFECT. A
    /// bounded, control-character-stripped host candidate was logged here at warning or error level, on the
    /// reasoning that an operator needs to know WHICH host to add an alias row for. Bounding it prevented
    /// log forging but not disclosure, and this stage runs before authentication for every request - so an
    /// unauthenticated caller could put text of its choosing into retained production logs simply by
    /// choosing a Host header, and a genuine deployment's own host names are themselves customer
    /// identifiers: <c>acme-legal.example.com</c> names a client, and a mistyped or hostile value can be
    /// e-mail-shaped or secret-shaped. Neither is data this application's logs should retain, and the
    /// retention and access policy of those logs sits outside this application's control.
    /// </para>
    /// <para>
    /// WHAT REPLACES IT, AND WHY THAT IS SUFFICIENT. The fingerprint is stable for a given address, so
    /// repeated failures against the same address remain recognisable as ONE problem and can be counted,
    /// alerted on and correlated - which is what an operator acts on. Recovering the address itself is a
    /// question for the request under a support reference, where the correlation identifier joins the
    /// caller's report to this entry, rather than for a log line every unauthenticated caller can write to.
    /// The reason code still distinguishes an unknown host from an ambiguous one and from a misconfigured
    /// portal, which is the distinction that decides what an operator does next.
    /// </para>
    /// </remarks>
    /// <param name="logger">
    /// The logger of the stage that observed the failure. SEC-030: the observing stage supplies its own logger
    /// rather than this type holding one, because the stage that DIAGNOSES an unresolved tenant - the
    /// pre-routing stage that populates it, which runs before authorisation - is not the stage that REFUSES
    /// one. Declaring the wording here and passing the logger in keeps a single vocabulary for the condition
    /// while letting the log entry carry the category of whichever stage actually saw it.
    /// </param>
    /// <param name="address">
    /// The address that failed to resolve. It is NOT logged, in whole or in part: only a fingerprint of it
    /// is recorded. See the remarks for why.
    /// </param>
    /// <param name="outcome">The failed resolution outcome.</param>
    internal static void LogUnresolved(ILogger logger, string address, Result outcome)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(outcome);

        // SEC-B4: THE RAW ADDRESS IS NOT RECORDED. This entry used to log the whole address - the host name
        // followed by the request's FULL path - at warning or error level, on a path that runs before
        // routing and therefore for every request. An unknown host name is caller-controlled, so a caller
        // could pick any host it liked and then force arbitrary path text into the production log with it:
        // a mistyped credential, a bearer token pasted into a URL, an e-mail address or any other personal
        // data that happens to sit in a path segment. That bypassed the request envelope's own protection,
        // which records the ROUTE TEMPLATE rather than the path for exactly this reason, and it
        // contradicted the requirement that structured logging exclude sensitive data.
        //
        // AND NEITHER IS THE HOST NAME, WHICH IS THE REMAINING HALF OF THE SAME FIX. A bounded, sanitised
        // HOST CANDIDATE was kept here on the argument that an operator needs it in order to add an alias
        // row. Bounding a value stops it forging a log line; it does not stop it disclosing one. The value
        // is still text an unauthenticated caller chooses, on a path that runs for every request, and a
        // real deployment's host names identify its customers - so both the hostile case and the ordinary
        // case put data into retained logs that this application should not keep. The remarks on this
        // method carry the full reasoning and what replaces the capability.
        //
        // What remains is the FINGERPRINT: a short digest of the whole address, stable for a given address,
        // so repeated failures against one address are still recognisable and countable as one problem
        // without the address, the host or the path being reproduced anywhere.
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
