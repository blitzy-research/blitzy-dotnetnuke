using System.Globalization;
using System.Text.Json;
using DnnMigration.Api.Middleware;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;

namespace DnnMigration.Api.Extensions;

/// <summary>
/// Composes the request pipeline. This is the one place where the order of the
/// application's stages is decided.
/// </summary>
/// <remarks>
/// <para>
/// Pipeline order is behaviour, not style. Each stage below is placed where it is for
/// a reason that is recorded next to it, because a stage moved one position can be
/// silently wrong: a limiter placed after authentication no longer bounds
/// unauthenticated floods, a correlation stage placed after logging cannot correlate
/// the log it was added for, and an error stage placed second catches everything
/// except the stage in front of it.
/// </para>
/// <para>
/// <b>The fixed order.</b> Ten stages carry a mandated relative sequence and are not open to local
/// variation. The additional stages this file adds around them - forwarded-header processing,
/// transport security, the tenant path base, the credential cache-control marker, the rate limiter
/// and the documentation console - are placed so that they do not disturb the relative order of the
/// ten:
/// </para>
/// <list type="number">
/// <item><description><c>UseExceptionHandler()</c>, parameterless.</description></item>
/// <item><description><c>UseMiddleware&lt;CorrelationIdMiddleware&gt;()</c>.</description></item>
/// <item><description><c>UseMiddleware&lt;RequestLoggingMiddleware&gt;()</c>.</description></item>
/// <item><description><c>UseRouting()</c>.</description></item>
/// <item><description><c>UseCors(CorsExtensions.PolicyName)</c>, the named policy.</description></item>
/// <item><description><c>UseAuthentication()</c>.</description></item>
/// <item><description><c>UseMiddleware&lt;PortalAliasResolutionMiddleware&gt;()</c>.</description></item>
/// <item><description><c>UseAuthorization()</c>.</description></item>
/// <item><description><c>MapControllers()</c>.</description></item>
/// <item><description>
/// <c>MapHealthChecks("/health")</c>, <c>MapHealthChecks("/health/ready")</c> and
/// <c>MapHealthChecks("/health/live")</c>, all anonymous. One step, three views: the first selects
/// every probe that is NOT readiness-tagged, the second selects exactly those that are, and the
/// third selects none. The order between them carries no meaning - they are sibling terminal
/// endpoints on distinct paths - which is why they occupy one position rather than three.
/// </description></item>
/// </list>
/// <para>
/// MIGRATION: THE TENANT STAGE PRECEDES AUTHORISATION, WHERE THE PLAN'S STAGE LIST PUT IT AFTER.
/// AAP 0.5.1.4 enumerates a single "portal-alias resolution" stage and places it behind
/// <c>UseAuthorization</c>. The implementation cannot have a single stage: rewriting the path base
/// for a child portal has to happen BEFORE routing, while deciding whether a particular endpoint
/// needs a tenant has to happen AFTER it, so resolution and refusal are two stages rather than one.
/// Once they are two, the refusal cannot stay behind authorisation - an authorisation policy that
/// reconciles the caller's portal against the arrival portal would be evaluated with no arrival
/// portal at all, which is the defect a code review raised against the earlier arrangement. The
/// refusal is therefore the stage that sits between authentication and authorisation, and the
/// pre-routing stage is reduced to the path-base prerequisite plus the diagnosis. No other named
/// stage moves, and the divergence from the plan's list is recorded in <c>MIGRATION_NOTES.md</c>
/// rather than left for a reader to discover from the code.
/// </para>
/// <para>
/// <b>Why each join is where it is.</b> A stage moved one position is silently wrong,
/// so the reason for every adjacency is recorded rather than left to be re-derived:
/// </para>
/// <list type="bullet">
/// <item><description>
/// <b>The exception handler is first</b> so that it wraps everything downstream,
/// including the correlation stage. An error stage placed second catches everything
/// except the stage in front of it, and a failure there would escape the problem
/// details contract entirely.
/// </description></item>
/// <item><description>
/// <b>Correlation precedes request logging</b> because the correlation identifier has
/// to exist before the entry that carries it is written. Reversed, every log entry
/// loses its correlation and the loop with the client's own correlation interceptor
/// is broken.
/// </description></item>
/// <item><description>
/// <b>Cross-origin policy follows routing</b> because it is endpoint-aware: the policy
/// is read from the endpoint that routing selected, and placed earlier it would
/// silently fall back to global behaviour.
/// </description></item>
/// <item><description>
/// <b>Cross-origin policy precedes authorisation</b> because a pre-flight request
/// carries no credentials. Authorisation first would answer every <c>OPTIONS</c> with
/// 401 before the cross-origin headers were attached, and the browser would report
/// that as an opaque cross-origin failure rather than as the refusal it is.
/// </description></item>
/// <item><description>
/// <b>Authentication precedes authorisation</b> because authentication establishes the
/// principal that authorisation evaluates.
/// </description></item>
/// <item><description>
/// <b>Tenant refusal sits between authentication and authorisation</b>, and that is the
/// whole reason it is a separate stage from the path-base one. It follows authentication
/// because it consults the caller's claims and must not disclose to an anonymous caller
/// which host names the installation serves; it precedes authorisation because a policy
/// that reconciles the caller's portal against the arrival portal must never be
/// evaluated with no arrival portal.
/// </description></item>
/// <item><description>
/// <b>The endpoints are last</b> because executing one terminates the pipeline.
/// </description></item>
/// <item><description>
/// <b>Forwarded headers are applied immediately inside the exception handler</b>, ahead
/// of every stage that observes the caller's address or scheme - correlation, request
/// logging, transport security, routing and the rate limiter. Placed later, each of those
/// would see the reverse proxy rather than the caller, and the credential limiter's
/// per-caller partition would collapse into one budget shared by everybody behind the
/// proxy.
/// </description></item>
/// <item><description>
/// <b>Correlation and request logging precede transport security</b>, which is a
/// correction rather than a preference. With redirection installed ahead of them, a
/// cleartext request that was redirected received no correlation identifier and was never
/// recorded - so the requests most worth seeing, the ones refused at the perimeter, were
/// the only ones the log did not carry. Redirection now happens INSIDE both stages, so a
/// 308 is correlated and recorded like any other answer.
/// </description></item>
/// </list>
/// <para>
/// <b>Forwarded-header processing runs before anything that reads an address or a
/// scheme.</b> TLS is terminated at the browser-facing edge and this application is
/// reached over plain HTTP on an internal network, so the caller's address and the
/// scheme the browser actually used arrive as headers rather than as properties of the
/// connection. Every stage that consumes either of them - transport security, request
/// logging, and the credential limiter's per-address partition - is placed after the
/// stage that corrects them. Reversed, the limiter partitions every caller behind the
/// proxy into a single shared budget, one caller can exhaust the credential allowance
/// for everybody, and the transport check cannot see that a request reached the edge in
/// clear text. Which hops may supply those headers is decided at registration, and the
/// forwarded HOST is deliberately not among the headers honoured: this application
/// resolves the tenant from the host name, so trusting a forwarded one would let a
/// caller behind the proxy choose which tenant serves it.
/// </para>
/// <para>
/// <b>Transport security is enforced, with exactly one exemption: the three health
/// paths.</b> Redirection to HTTPS is applied wherever a deployment configures it, and
/// the production overlay configures it. It is branched rather than blanket for one
/// reason only - the anonymous health endpoints answer a container probe over plain HTTP
/// before any credential exists, and the compose topology waits on that probe before
/// starting the front end, so a redirect there would leave the container permanently
/// unhealthy. EVERY other request must arrive over HTTPS or be redirected until it does.
/// </para>
/// <para>
/// SEC: THERE IS NO LOOPBACK EXEMPTION ANY MORE, AND ITS REMOVAL IS THE POINT. An earlier
/// revision also withheld the redirect from a "loopback-addressed" request, deciding that
/// from <c>Request.Host</c> - a value the CALLER sends. A remote client could therefore
/// send <c>Host: localhost</c> to a published listener, be exempted from the redirect and
/// be served over cleartext, which is precisely the enforcement this stage exists to
/// apply. The exemption is gone rather than re-derived from the connection: nothing that
/// ships needs it. The base compose topology disables redirection outright, the TLS
/// overlay forwards <c>X-Forwarded-Proto: https</c> from a named hop so the request is
/// already secure, and the container's own probe is a health path. See
/// <see cref="UseApiPipeline(WebApplication)"/> for the mechanism and the citations.
/// </para>
/// <para>
/// <b>All health views are anonymous, unthrottled and unredirected.</b> <c>/health</c> is
/// the LIVENESS view and is the one the container image's own probe and the compose
/// <c>service_healthy</c> condition read; it runs every probe that is not readiness-tagged,
/// which today means the process-local audit-delivery signal and no external dependency.
/// <c>/health/live</c> runs no probe at all. <c>/health/ready</c> runs the ready-tagged
/// database probes and is available for an orchestrator that wants to gate traffic on the
/// store; nothing in this repository's deployment artefacts reads it.
/// </para>
/// </remarks>
public static class ApplicationBuilderExtensions
{
    /// <summary>
    /// The deployed liveness endpoint: <c>/health</c>.
    /// </summary>
    /// <remarks>
    /// THIS IS THE PATH THE DEPLOYMENT ARTEFACTS PROBE, byte for byte:
    /// <c>docker/api.Dockerfile</c>'s <c>HEALTHCHECK</c>, <c>docker/docker-compose.yml</c>'s api
    /// health check and the end-to-end validation gate all read it, and the front-end service's
    /// <c>condition: service_healthy</c> is satisfied by it. It selects every probe that is not
    /// readiness-tagged, which is why it can answer while the external store is unreachable.
    /// <see cref="ReadinessEndpointPath"/> is the stricter view and is read by nothing in this
    /// repository; <see cref="LivenessHealthEndpointPath"/> executes no probe at all.
    /// </remarks>
    public const string HealthEndpointPath = "/health";

    /// <summary>Explicit liveness endpoint, containing no dependency checks.</summary>
    public const string LivenessHealthEndpointPath = "/health/live";


    /// <summary>
    /// Endpoint name of the health probe, used only to give the route a stable identity in
    /// the endpoint metadata. It is deliberately not part of any URL.
    /// </summary>
    public const string HealthEndpointName = "HealthCheck";

    /// <summary>Endpoint name of the explicit liveness probe.</summary>
    public const string LivenessHealthEndpointName = "LivenessHealthCheck";


    /// <summary>
    /// Address of the readiness endpoint: <c>/health/ready</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The companion to <see cref="HealthEndpointPath"/>, and the two answer different
    /// questions: that one reports whether the process can answer at all, this one reports
    /// whether it can serve a request end to end - which for this application means whether
    /// the external store it persists through is reachable.
    /// </para>
    /// <para>
    /// Nothing outside this codebase depends on this address yet, which is exactly why the
    /// readiness checks live HERE and not on the liveness path: the image's own health probe
    /// and the compose condition are pinned to that one, and making them depend on a store
    /// the compose topology does not even declare would hold the front end back for a reason
    /// unrelated to the api's ability to serve it. An orchestrator that wants the stricter
    /// signal points its readiness probe at this address.
    /// </para>
    /// </remarks>
    public const string ReadinessEndpointPath = "/health/ready";

    /// <summary>
    /// Endpoint name of the readiness probe. Like the liveness name, metadata only.
    /// </summary>
    public const string ReadinessEndpointName = "ReadinessCheck";

    /// <summary>
    /// Tag a probe carries to declare itself a readiness signal rather than a liveness one:
    /// <c>ready</c>.
    /// </summary>
    /// <remarks>
    /// A contract with the persistence layer, which applies this exact tag to both of its
    /// database probes. It is compared case-insensitively so a differently-cased registration
    /// still lands in the view its author intended rather than silently joining the other one.
    /// </remarks>
    private const string ReadinessTag = "ready";

    /// <summary>
    /// Configuration key a deployment sets to override whether this application redirects
    /// plain-HTTP requests to HTTPS itself: <c>Https:RedirectEnabled</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Off in the base settings and <b>on in the production overlay</b>, so a deployment
    /// serving real traffic enforces the transport by default while a developer running the
    /// host directly, and the integration host, are unaffected. The enforcement itself is
    /// narrowed by <see cref="TransportSecurityApplies(HttpContext)"/> rather than by this
    /// flag - see <see cref="UseApiPipeline(WebApplication)"/> - and it is inseparable from
    /// <see cref="HttpsRedirectPortSectionName"/>, without which the
    /// stage this flag installs has no target authority to redirect to and does nothing at
    /// all.
    /// </para>
    /// <para>
    /// The key is deliberately published as a constant rather than spelled inline. It is
    /// read in exactly one place, but it is depended upon from outside this file: a
    /// deployment and integration hosts override it for their topology. A key that is renamed without both
    /// of those following becomes inert rather than failing, and an inert flag is
    /// indistinguishable from a flag that was set - which is precisely the failure this
    /// constant exists to make impossible.
    /// </para>
    /// <para>
    /// It is intentionally not backed by an options class. The four bound options types
    /// are a closed set, and a single boolean that is read once while the pipeline is
    /// being composed - before any request exists, and therefore before any options
    /// snapshot would be resolved - gains nothing from being one.
    /// </para>
    /// </remarks>
    public const string HttpsRedirectionSectionName = "Https:RedirectEnabled";

    /// <summary>Configuration key naming the externally reachable TLS port.</summary>
    public const string HttpsRedirectPortSectionName = "Https:RedirectPort";

    /// <summary>
    /// Log category under which the health report's per-probe detail is recorded.
    /// </summary>
    /// <remarks>
    /// Named after this type so an operator can raise or lower the health log on its own
    /// without touching the rest of the pipeline's logging, which matters because a healthy
    /// probe arrives every few seconds for the lifetime of the deployment.
    /// </remarks>
    private const string HealthLogCategory = "DnnMigration.Api.HealthChecks";

    /// <summary>
    /// Name this service reports in its health document.
    /// </summary>
    /// <remarks>
    /// Read from the assembly rather than written as a literal, and that is the point: the
    /// same name is the file the container image starts, so deriving it here means the
    /// document cannot claim to be a service the image does not run. It resolves to
    /// <c>DnnMigration.Api</c>.
    /// </remarks>
    private static readonly string ServiceName =
        typeof(ApplicationBuilderExtensions).Assembly.GetName().Name ?? "DnnMigration.Api";

    /// <summary>
    /// Version this service reports in its health document.
    /// </summary>
    /// <remarks>
    /// The project sets no explicit version, so this is the SDK's default of
    /// <c>1.0.0.0</c> - which is exactly what the published operator documentation
    /// records. Resolved once, because the probe runs on a schedule for the lifetime of
    /// the process and reflection per probe would buy nothing. Nothing more detailed is
    /// exposed: an informational version would carry the commit, and this endpoint is
    /// anonymous.
    /// </remarks>
    private static readonly string ServiceVersion =
        typeof(ApplicationBuilderExtensions).Assembly.GetName().Version?.ToString() ?? "1.0.0.0";

    /// <summary>
    /// Composes every stage of the request pipeline, in order, and maps the
    /// application's endpoints.
    /// </summary>
    /// <param name="app">The application being composed.</param>
    /// <returns>
    /// The same <paramref name="app"/> instance, so the caller can run it.
    /// </returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="app"/> is <see langword="null"/>.
    /// </exception>
    /// <remarks>
    /// <para>
    /// <b>On transport security.</b> Strict transport security is applied outside
    /// development and needs no switch: the stage that applies it only ever ADDS a
    /// response header, and only on a request that already arrived over HTTPS, so it
    /// cannot affect the container's own plain-HTTP health probe. Redirection is
    /// configured rather than unconditional, and the production overlay configures it on,
    /// because the model this deployment implements is TLS terminated at the
    /// browser-facing edge with plain HTTP on the internal network only. What makes
    /// enforcement safe there is <see cref="TransportSecurityApplies(HttpContext)"/>,
    /// which withholds the redirect from the three health paths and from NOTHING else.
    /// Redirecting those would answer the container's own probe with a redirect to a port
    /// nothing is listening on, the probe would never report healthy, and the front-end
    /// container would never start - a total outage produced by a hardening measure.
    /// Redirecting everything else is the point: a caller that reaches this process in
    /// clear text is answered with a redirect rather than served, so a directly published
    /// API port cannot be used to send a credential or a bearer token unencrypted.
    /// </para>
    /// <para>
    /// <b>On the port the redirect names.</b> It is configured, not discovered. The
    /// redirection stage looks for an HTTPS port in its options, in the host's
    /// configuration and then among the addresses the server is listening on; this process
    /// listens on plain HTTP only, so the last of those can never supply one, and when none
    /// of them does the stage logs once and forwards the request unchanged instead of
    /// failing. The port is therefore supplied at registration - see
    /// <see cref="HttpsRedirectPortSectionName"/> - so that switching
    /// redirection on cannot silently do nothing.
    /// </para>
    /// <para>
    /// <b>On the tenant stage's position.</b> It is placed after authentication and
    /// before authorisation, which is the position the security property requires: no
    /// authorisation policy may be evaluated for an endpoint that needs a tenant while
    /// no tenant is resolved, and a stage behind authorisation cannot promise that.
    /// Resolution itself is idempotent and has usually already happened in the
    /// pre-routing path-base stage, so this stage ordinarily observes that outcome
    /// rather than repeating the work; what it adds is the endpoint-specific REFUSAL,
    /// which needs the matched endpoint's metadata and therefore cannot be done before
    /// routing. Its refusal is withheld from an unauthenticated caller on a protected
    /// endpoint, because the fallback policy answers such a caller with 401 from every
    /// host alike and refusing here first would let it enumerate configured host names.
    /// </para>
    /// <para>
    /// <b>On the exception handler's overload.</b> The parameterless overload is the
    /// only correct one here. It dispatches to the registered
    /// <see cref="Microsoft.AspNetCore.Diagnostics.IExceptionHandler"/> implementation
    /// and, when that implementation declines an exception by returning
    /// <see langword="false"/>, falls through to the framework's own problem-details
    /// response. Passing an inline handler or an error-path string instead would bypass
    /// the registered handler altogether and destroy that fall-through, and no compiler
    /// diagnostic reports the mistake.
    /// </para>
    /// <para>
    /// <b>On the cross-origin policy's name.</b> The named policy is applied explicitly.
    /// The parameterless form would require a default policy, which is deliberately not
    /// registered, and an inline policy built here would bypass the origin-restricted
    /// policy that is registered - unauditably, because the allowed origins would then
    /// be decided in two places.
    /// </para>
    /// <para>
    /// <b>On the rate limiter and the health endpoint.</b> The limiter is placed after
    /// routing because its classifier reads endpoint metadata, which does not exist
    /// before routing has selected an endpoint; placed earlier it would silently lose
    /// that test and fall back to inspecting the method and path alone. The health
    /// endpoint is nonetheless outside every partition the limiter applies: each
    /// partition resolves to the shared no-limit partition unless the request is
    /// credential-bearing, and a request is credential-bearing only when it carries the
    /// credential-endpoint metadata or uses one of the mutating methods against a
    /// credential path. The probe carries neither - it is a read - so it is never
    /// counted, and it therefore cannot be refused for exceeding a budget it never
    /// consumes.
    /// </para>
    /// </remarks>
    public static WebApplication UseApiPipeline(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        // MIGRATION: this method replaces the legacy request pipeline wholesale rather
        // than porting it. Website/release.config declared eight managed modules at
        // L67-L74 (ScriptModule, Compression, RequestFilter, UrlRewrite, Exception,
        // UsersOnline, DNNMembership, Personalization) and seven handlers at L77-L83
        // (ScriptResource.axd, *_AppService.axd, *.asmx, Logoff.aspx, RSS.aspx,
        // LinkClick.aspx, *.captcha.aspx), and Website/Default.aspx.vb ran a 700-line
        // Web Forms page lifecycle on top of them. None of that is translated: the
        // module chain becomes the explicit stages below, and the page lifecycle has no
        // counterpart at all because the browser is served by a single-page application
        // instead. Two of its members are named here so their absence is not mistaken
        // for an omission - LoadSkin at Website/Default.aspx.vb:L217 and
        // ManageStyleSheets at :L355 - because skinning is out of scope and the
        // application shell replaces both.

        // First, and unconditionally. An exception handler covers what follows it and
        // nothing that precedes it, so this is the one stage whose position admits no
        // argument: every stage below is inside its scope.
        app.UseExceptionHandler();

        // SEC: FORWARDED HEADERS ARE PROCESSED HERE, AS EARLY AS POSSIBLE, AND ONLY
        // WHEN A DEPLOYMENT HAS NAMED THE PROXIES IT RUNS. Three later decisions read
        // what this stage promotes and are wrong without it: strict transport security
        // and the redirect below both test the request's SCHEME, which behind a
        // TLS-terminating proxy arrives only in X-Forwarded-Proto; and the credential
        // limiter partitions on the caller's ADDRESS, which behind any proxy arrives
        // only in X-Forwarded-For. Without this stage every caller behind the proxy
        // shares one address and therefore one credential budget - a denial of service
        // against authentication for everyone, reachable by one caller.
        //
        // The guard is the security property, not caution. A forwarded header is
        // supplied by whoever made the request, so honouring one from a hop the
        // deployment does not control lets a caller name its own address and choose its
        // own rate-limit partition. HasTrustedProxies reports whether
        // Proxy:KnownProxies or Proxy:KnownNetworks names anything; with neither
        // configured this stage is not registered at all, which is strictly safer than
        // registering it against the framework's default trust list - the loopback
        // address, which inside a container is this process itself.
        //
        // docker/docker-compose.tls.yml is the topology that switches this on, and it
        // scopes the trust to the compose network rather than to the world.
        // MIGRATION: the legacy application had no equivalent. IIS terminated TLS in the
        // same process that ran the pages, so Request.IsSecureConnection and
        // Request.UserHostAddress were already the caller's own and nothing had to be
        // reconstructed. Splitting the proxy from the application is what creates the
        // question, and this stage is the answer.
        if (ServiceCollectionExtensions.HasTrustedProxies(app.Configuration))
        {
            app.UseForwardedHeaders();
        }

        // Before request logging, and that ordering is the whole point: this stage
        // opens the logging scope that carries the correlation identifier, so a log
        // written by any later stage is correlated without that stage having to know
        // the identifier exists.
        app.UseMiddleware<CorrelationIdMiddleware>();

        // Immediately inside the correlation scope, and outside everything else, so the
        // one entry it writes measures the whole of the work the request caused and
        // reports the status code the caller actually received.
        app.UseMiddleware<RequestLoggingMiddleware>();

        // SEC-B4: TRANSPORT ENFORCEMENT RUNS INSIDE CORRELATION AND REQUEST LOGGING, NOT
        // AHEAD OF THEM. Both stages above used to be registered after this one, and the
        // consequence was silent: a cleartext request that this stage redirected
        // short-circuited before a correlation identifier existed and before the request
        // envelope was written, so the perimeter refusals - the requests an operator most
        // needs to see, and the only evidence that enforcement is working at all - were
        // the one class of request that produced no log entry and no correlation loop with
        // the client's interceptor. Moving the redirect inside them costs nothing: neither
        // stage reads the scheme, and both restore what they changed on the way out.
        //
        // Applied outside development, and strict transport security needs no switch of its
        // own because it cannot reach the health probe. That stage only sets a response
        // header - it never redirects and never changes a status code - and it sets it only
        // on a request that arrived over HTTPS. The container's probe is plain HTTP, so it
        // fails that condition and the header is never even emitted.
        if (!app.Environment.IsDevelopment())
        {
            app.UseHsts();
        }

        // Redirection to HTTPS is enforced wherever a deployment turns it on, and the
        // production overlay turns it on. What makes that safe is the branch predicate,
        // not a blanket switch: TransportSecurityApplies excludes the one class of request
        // for which a redirect would be destructive rather than protective.
        //
        // That class is the health probe. docker/api.Dockerfile probes
        // "wget --spider http://localhost:8080/health" over plain HTTP, and
        // docker-compose.yml holds the front-end container back on
        // "condition: service_healthy"; answering that probe with a redirect would leave
        // the container permanently unhealthy, the front end permanently unstarted and the
        // end-to-end gate failed. Nothing - no compiler, no analyser, no unit test -
        // reports that mistake, which is why the exclusion is explicit and documented
        // here.
        //
        // SEC-B4: THE SECOND EXCLUSION IS GONE. A "loopback-addressed" request was also
        // exempted, and the classification read context.Request.Host - the authority the
        // CALLER asked for. A remote client that sent "Host: localhost" to a published
        // listener was therefore exempted from enforcement and served over cleartext, and
        // nginx repeated the same mistake with the same header. Deriving it from the
        // connection instead would have closed the spoof, but nothing that ships needs the
        // exemption at all: docker-compose.yml sets Https__RedirectEnabled=false because
        // that topology is cleartext behind a proxy end to end, docker-compose.tls.yml
        // turns enforcement on and its proxy forwards X-Forwarded-Proto=https from a named
        // hop so the request is already secure, and the container's own probe is a health
        // path. An operator diagnosing this process over plain HTTP on a TLS-mode
        // deployment now receives the redirect, which is the honest answer.
        //
        // Everything other than a health path must therefore reach this application over
        // HTTPS or be redirected until it does. That is what closes the plain-HTTP path to
        // a directly published API port: a caller that bypasses the proxy and speaks plain
        // HTTP to this process is answered with a redirect it cannot satisfy without TLS,
        // so no credential and no bearer token crosses a network in clear text.
        if (app.Configuration.GetValue<bool>(HttpsRedirectionSectionName))
        {
            app.UseWhen(
                TransportSecurityApplies,
                transportSecured => transportSecured.UseHttpsRedirection());
        }
        else if (!app.Environment.IsDevelopment())
        {
            // SEC: SAID OUT LOUD, ONCE, AT STARTUP. A deployment serving production traffic
            // with neither transport enforcement here nor a TLS-terminating proxy in front
            // is carrying credentials and bearer tokens in clear text, and the failure is
            // completely silent otherwise: every probe answers 200 and every gate passes.
            // This is a warning rather than a refusal because the shipped topology is
            // legitimately plain HTTP behind a proxy that terminates TLS - refusing to
            // start would make the correct deployment impossible - but an operator who has
            // NOT arranged that must be told, in the log, on every start.
            app.Logger.LogWarning(
                "Transport security is not enforced by this process: '{Setting}' is not set outside "
                + "development. This is correct only when a reverse proxy terminates TLS in front of it "
                + "(see docker/nginx.tls.conf.template and docker/docker-compose.tls.yml, which also set "
                + "'{ProxySetting}' so that the forwarded scheme is honoured). Without one, credentials and "
                + "bearer tokens traverse the network in clear text.",
                HttpsRedirectionSectionName,
                ServiceCollectionExtensions.KnownNetworksSectionName);
        }

        // Inside the correlation and request-logging scopes opened above, so a refusal the HOST decided is
        // answered by this application's own handler instead of reaching the framework's exception stage,
        // which would record it at Error as an unhandled fault whatever status it resolved to. See the
        // middleware for why an oversized or malformed body is a client error rather than a fault, and why
        // the payload itself is still produced by the one registered handler. Neither stage between here
        // and the request-logging registration reads the request body, so nothing this intercepts can
        // escape upward past it.
        app.UseMiddleware<TransportRefusalMiddleware>();

        // MIGRATION: A STATUS CODE DECIDED BEFORE AN ENDPOINT WAS REACHED NOW CARRIES A BODY. Routing
        // itself answers two statuses - 404 for a path matching no route and 405 for a path matching one
        // whose method it does not accept - and it answers them with the status line alone. A caller
        // therefore received an EMPTY body from those two while every other refusal in this API carried a
        // problem document, so a client with one parser for error responses had two cases it could not
        // parse and no way to tell them apart beyond the status code. This stage supplies the same
        // document, through the same factory and writer as every other producer, so the taxonomy, the
        // media type, the trace identifier and the correlation identifier are identical.
        //
        // POSITION. Inside the correlation scope, so the identifier this payload carries is the one the
        // response header carries and the log entry was tagged with; inside the request-logging scope, so
        // the entry still reports the status the caller received - which this stage does not change, only
        // describes; and OUTSIDE routing, because a request that matches no endpoint never reaches any
        // stage placed after it.
        //
        // WHAT IT DELIBERATELY DOES NOT TOUCH. The framework's own guard is that a response which already
        // declares a content type or a length is left alone, which is exactly the boundary wanted here:
        // every refusal that writes its own document - the authentication challenge, the authorisation
        // refusal, the remediation refusal, the rate-limit refusal, every controller problem and the
        // exception handler - sets one, so none of them is re-written or double-written. Successful
        // responses are outside its range entirely, so the three anonymous health endpoints keep
        // answering exactly as they did, including their own 503 payload, which carries a content type.
        app.UseStatusCodePages(WriteStatusOnlyProblemAsync);

        // Before routing, and only this stage can be. A child portal is addressed by a
        // path segment beneath a shared host, so the segment that identifies the tenant
        // sits in front of the path the routes were written against; it has to move
        // into the path base before routing matches, or every request to a child
        // portal answers 404 no matter how correctly its tenant resolved. Routing
        // cannot be un-done afterwards, which is why the named portal-alias stage
        // below cannot do this. This stage is deliberately reduced to that prerequisite
        // and the diagnosis of a failure to resolve: the REFUSAL belongs to the named
        // stage, which runs after authentication where it can tell an anonymous caller
        // apart from a known one and where the matched endpoint's metadata exists.
        app.UseMiddleware<TenantPathBaseMiddleware>();

        // MIGRATION: no static-file stage, and none is missing. The legacy application
        // served its own markup, stylesheets and skins from this process; this one
        // returns JSON and nothing else. docker/nginx.conf serves the single-page
        // application from its own root with "try_files $uri $uri/ /index.html" and
        // forwards exactly one prefix - its "location /api/" block - through to this
        // process, so a static-file stage here would have no content to serve and would
        // add a filesystem probe to every request that misses a route. The health
        // endpoint is deliberately NOT among the forwarded prefixes: it is probed
        // directly on this process's own port, which is why it must stay reachable
        // without the proxy. Server-side rendering is out of scope: there is no Razor, no
        // view engine and no skinning in this application.

        // Routing must precede the four stages below. Cross-origin policy, the rate
        // limiter and authorisation each read metadata from the endpoint that routing
        // selects; placed before routing there is no endpoint yet and each of them
        // would silently fall back to its global behaviour.
        app.UseRouting();

        // SEC: IMMEDIATELY AFTER ROUTING AND BEFORE EVERYTHING THAT CAN REFUSE A
        // REQUEST. This stage marks a credential endpoint's response as never
        // cacheable, and it needs the endpoint routing selected in order to read the
        // credential-endpoint metadata - so it cannot run earlier. It must run before
        // the rate limiter, authentication and authorisation, because each of those can
        // SHORT-CIRCUIT the pipeline: a 429 from the limiter, a 401 from
        // authentication and a 403 from authorisation never reach an action filter, and
        // a token-bearing endpoint's refusals are exactly the responses an intermediate
        // cache must not keep either. The header is attached through a response callback
        // rather than written here, so it lands whoever writes the response.
        app.UseMiddleware<CredentialCacheControlMiddleware>();

        // Immediately before the cross-origin stage, so that every response the stage can influence
        // announces the request header its content depends on. The framework's own stage omits that
        // declaration for a single-origin policy, which is this deployment's configuration - see the
        // middleware's own remarks for why the omission is unsafe for a shared cache and why the
        // declaration has to be unconditional.
        app.UseMiddleware<OriginVaryMiddleware>();

        // Before the rate limiter, so that a refusal still carries the cross-origin
        // headers. Without them a browser cannot read the refusal at all: the caller
        // would see an opaque network failure instead of the status and the retry hint
        // the limiter took care to send.
        app.UseCors(CorsExtensions.PolicyName);

        // Before authentication, deliberately. The floods this bounds - credential
        // stuffing, and the processor cost of verifying a password hash - are made up
        // entirely of requests that will fail to authenticate, so a limiter placed
        // after authentication would bound only the traffic that was never the problem.
        app.UseRateLimiter();

        app.UseAuthentication();

        // Store-backed mandatory credential/profile remediation is evaluated after the bearer token has
        // established the caller and before any ordinary endpoint can be authorised. Endpoints explicitly
        // marked for remediation still pass through their normal policies; the marker grants nothing.
        app.UseMiddleware<RestrictedSessionMiddleware>();

        // Between authentication and authorisation, because the documentation console
        // needs both: outside development its gate reads the authenticated caller, and
        // the console answers requests itself, so an authorisation stage carrying a
        // fallback policy would refuse it before it was reached. Adds nothing at all
        // unless the environment or an explicit opt-in says so.
        app.UseSwaggerDocumentation(app.Environment, app.Configuration);

        // SEC-B4: AFTER AUTHENTICATION AND BEFORE AUTHORISATION, WHICH IS A CORRECTION.
        // This stage used to be registered after UseAuthorization, on the reasoning that
        // resolution is idempotent so an authorisation handler could resolve the tenant
        // itself when it asked. That reasoning holds for RESOLUTION and fails for
        // ENFORCEMENT: every tenant-scoped authorisation decision reconciles the caller's
        // portal, the route's portal and the ARRIVAL portal, and with the refusal behind
        // authorisation those policies were evaluated on requests that had no arrival
        // portal at all - a three-sided check silently degrading to a two-sided one
        // exactly where a host name resolved to nothing. Enforcing here means no policy
        // ever sees that state.
        //
        // It stays after the documentation console because that console answers requests
        // itself and describes the API rather than serving a tenant's data; the stage
        // exempts its paths in any case, so the adjacency is belt and braces.
        app.UseMiddleware<PortalAliasResolutionMiddleware>();

        app.UseAuthorization();

        app.MapControllers();

        // Anonymous, explicitly and necessarily. /health is the LIVENESS view and is the
        // one the image's HEALTHCHECK, the compose api health check and the end-to-end
        // gate all read, so it is what decides whether the api container reports healthy
        // and therefore whether the frontend service starts. It selects every probe that
        // is NOT readiness-tagged - today the process-local audit-delivery signal, and no
        // external dependency - so the process can answer it while SQL Server is
        // unreachable. /health/live runs no probe at all. /health/ready selects only the
        // checks tagged "ready" and is offered for an orchestrator that wants to gate
        // TRAFFIC on the store; nothing in this repository's deployment artefacts reads
        // it, and pointing the container probe at it would hold the frontend back behind a
        // service the compose topology does not even declare.
        //
        // It is also unthrottled, and that is a property of the limiter's classifier
        // rather than of anything declared here: every partition resolves to the shared
        // no-limit partition unless a request is credential-bearing, and this endpoint
        // is neither annotated as credential-bearing nor reached by a mutating method.
        // It therefore consumes no budget and cannot be refused for exceeding one.
        // Both properties are depended upon from outside this codebase, so none of the
        // three mappings may be authenticated, throttled or redirected.
        //
        // Response headers for the browser-facing origin are the proxy's concern, not
        // this pipeline's: docs/project-guide.md:L295 (task M5) assigns the content
        // security policy, frame options and referrer policy to the layer that serves the
        // browser, and docker/nginx.conf configures them there. This process answers a
        // JSON API and serves no document, so a header stage here would attach a document
        // policy to responses no browser renders. Strict transport security is the one
        // exception and belongs to both layers, because both can be addressed over HTTPS;
        // this pipeline emits it above.
        //
        // The report is written as JSON rather than left to the framework's default
        // writer. The default emits the status word alone, which distinguishes nothing:
        // it cannot say WHICH registered probe reported the fault, nor which of a probe's
        // outcomes it was. The database probe alone answers "not configured" and
        // "unavailable" differently, and those two send an operator to different places -
        // the container's environment in the first case, the instance or the network in
        // the second - and the process-local audit-delivery signal is a different probe
        // again. Naming each check and carrying its authored description is therefore the
        // whole diagnostic value of the endpoint, and the status word alone discards it.
        //
        // THREE VIEWS, each with an EXPLICIT predicate, because the questions a probe can ask are not the
        // same question. Liveness asks "is this process able to answer at all", and the container's own
        // probe asks exactly that: it must answer 200 while the process is up, including during the window
        // in which an EXTERNAL database is not yet reachable - the compose topology declares no database
        // service, so the store this api persists through may legitimately be unreachable when the process
        // starts. Readiness asks "can this process serve a request end to end", which does depend on the
        // store.
        //
        // Before the predicates existed, one endpoint ran every registered check and the readiness tag on
        // those checks had no effect whatever: the container's liveness probe depended on a database, so an
        // unreachable one held the api container permanently unhealthy, held the frontend container back
        // behind its service_healthy condition, and did so while every probe was correctly reporting the
        // truth. The predicate is what makes the tag mean something.
        app.MapHealthChecks(
                HealthEndpointPath,
                new HealthCheckOptions
                {
                    AllowCachingResponses = false,
                    ResponseWriter = WriteHealthReportAsync,
                    Predicate = IsLivenessProbe,
                })
            .AllowAnonymous()
            .WithName(HealthEndpointName)
            .ExcludeFromDescription();

        // The readiness view. Anonymous for the same reason as the liveness view - an orchestrator's probe
        // carries no credential - and written by the same writer, so an operator reading either sees one
        // document shape. It is a separate PATH rather than a query parameter on the first, because the
        // liveness path is depended upon from outside this codebase, byte for byte, by the image's own
        // health check and by the compose condition, and a probe that had to pass a parameter to get the
        // liveness answer would be one edit away from getting the readiness answer instead. Nothing in
        // this repository probes THIS path; it exists for an orchestrator that gates traffic rather than
        // start-up on the store.
        app.MapHealthChecks(
                ReadinessEndpointPath,
                new HealthCheckOptions
                {
                    AllowCachingResponses = false,
                    ResponseWriter = WriteHealthReportAsync,
                    Predicate = IsReadinessProbe,
                })
            .AllowAnonymous()
            .WithName(ReadinessEndpointName)
            .ExcludeFromDescription();

        // The explicit liveness view. It runs NO probe at all - not even the ones the default view
        // includes - so it answers the narrowest question there is: can this process accept a request and
        // compose a response. /health keeps the wider liveness set, because a monitor pinned to it should
        // still learn that the audit transport has failed; an orchestrator that wants restart-or-not and
        // nothing else points its liveness probe here instead.
        app.MapHealthChecks(
                LivenessHealthEndpointPath,
                new HealthCheckOptions
                {
                    AllowCachingResponses = false,
                    Predicate = _ => false,
                    ResponseWriter = WriteHealthReportAsync,
                })
            .AllowAnonymous()
            .WithName(LivenessHealthEndpointName)
            .ExcludeFromDescription();

        return app;
    }

    /// <summary>Selects the probes that belong to the liveness view.</summary>
    /// <param name="registration">The probe being considered.</param>
    /// <returns>
    /// <see langword="true"/> for every probe that is not tagged as a readiness signal.
    /// </returns>
    /// <remarks>
    /// Expressed as an exclusion rather than as an allowlist on purpose. A probe added later without a tag
    /// is a probe whose author has not said which view it belongs to, and the safe reading of silence for a
    /// LIVENESS view is "include it" only if it cannot depend on anything external - which is precisely
    /// what an untagged probe asserts by not carrying the readiness tag. The consequence is deliberate and
    /// worth stating: tagging is how a dependency-touching probe stays out of the container's start-up
    /// path, so an untagged probe that opens a socket would break it.
    /// </remarks>
    private static bool IsLivenessProbe(HealthCheckRegistration registration)
    {
        ArgumentNullException.ThrowIfNull(registration);

        return !registration.Tags.Contains(ReadinessTag, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Selects the probes that belong to the readiness view.</summary>
    /// <param name="registration">The probe being considered.</param>
    /// <returns><see langword="true"/> for every probe tagged as a readiness signal.</returns>
    /// <remarks>
    /// The exact complement of <see cref="IsLivenessProbe"/>, so every registered probe appears in exactly
    /// one of the two views and none can be registered into neither.
    /// </remarks>
    private static bool IsReadinessProbe(HealthCheckRegistration registration)
    {
        ArgumentNullException.ThrowIfNull(registration);

        return registration.Tags.Contains(ReadinessTag, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Reports whether transport security is enforced for one request.
    /// </summary>
    /// <param name="context">The request being classified.</param>
    /// <returns>
    /// <see langword="false"/> for the three published health paths, so none of them is ever
    /// answered with a redirect; otherwise <see langword="true"/>.
    /// </returns>
    /// <remarks>
    /// <para>
    /// ONE EXCLUSION, AND IT IS LOAD-BEARING RATHER THAN LENIENT. The health paths must answer
    /// a container health check that runs over plain HTTP before any credential exists in the
    /// system, and the compose topology holds the front-end service back until that check
    /// succeeds; redirecting it would leave a correctly built deployment permanently unhealthy.
    /// </para>
    /// <para>
    /// SEC: THE PREDICATE READS NOTHING THE CALLER SUPPLIES. An earlier revision also exempted
    /// a request whose <c>Host</c> named <c>localhost</c> or a loopback literal. The host is
    /// the authority the CALLER asked for, so a remote client could disable transport
    /// enforcement for itself with one header - the exemption was, in effect, an opt-out from
    /// HTTPS published to the internet. It is removed rather than re-derived from the
    /// connection endpoint, because nothing that ships needs it: the cleartext compose
    /// topology disables redirection outright, the TLS topology's proxy forwards the secure
    /// scheme from a named hop, and the container's own probe is a health path. The path is the
    /// only input, and a path is matched by this process rather than trusted from it.
    /// </para>
    /// <para>
    /// The comparison is against the three EXACT published paths rather than a prefix, so a
    /// longer path beneath the health namespace - or one that merely begins with the same
    /// characters, such as a hypothetical <c>/healthz</c> - is a different endpoint and does
    /// not inherit the exemption. A single trailing separator is tolerated because a probe may
    /// spell the address either way and both reach the same endpoint.
    /// </para>
    /// </remarks>
    private static bool TransportSecurityApplies(HttpContext context) =>
        !IsPublishedHealthPath(context.Request.Path);

    /// <summary>
    /// Reports whether a path is one of the three health views mapped by this pipeline.
    /// </summary>
    /// <param name="path">The request path.</param>
    /// <returns><see langword="true"/> when the path addresses a health view exactly.</returns>
    private static bool IsPublishedHealthPath(PathString path)
    {
        if (!path.HasValue)
        {
            return false;
        }

        string candidate = path.Value!;

        // A single trailing separator addresses the same endpoint, and routing treats the two
        // spellings alike, so the exemption has to as well.
        if (candidate.Length > 1 && candidate.EndsWith('/'))
        {
            candidate = candidate[..^1];
        }

        return string.Equals(candidate, HealthEndpointPath, StringComparison.OrdinalIgnoreCase)
            || string.Equals(candidate, ReadinessEndpointPath, StringComparison.OrdinalIgnoreCase)
            || string.Equals(candidate, LivenessHealthEndpointPath, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Writes the health report as JSON.</summary>
    /// <param name="context">The request being answered.</param>
    /// <param name="report">The report produced by the registered checks.</param>
    /// <returns>A task that completes when the response body has been written.</returns>
    /// <remarks>
    /// <para>
    /// <strong>The body is exactly four members - <c>status</c>, <c>timestamp</c>,
    /// <c>version</c> and <c>serviceName</c> - and nothing else.</strong> That is the
    /// published contract for this endpoint, and a body wider than its own contract is a
    /// body no consumer was told to expect.
    /// </para>
    /// <para>
    /// MIGRATION: PER-PROBE DETAIL MOVED FROM THE BODY TO THE LOG. An earlier revision
    /// appended <c>totalDurationMs</c> and a <c>checks</c> array naming each registered
    /// probe, its status, its duration and its description, on the argument that extra
    /// members take nothing away from a consumer reading only the four. Two things make
    /// that wrong. The endpoint is <b>anonymous</b>, so the probe names and their
    /// authored descriptions are published to any unauthenticated caller and enumerate
    /// the application's internal dependencies for them. And a documented four-member
    /// contract that actually returns six is a contract a consumer cannot rely on. The
    /// diagnostic value the earlier note defended is real, so it is not discarded: the
    /// per-probe detail is written to the structured log below, at a severity that
    /// follows the aggregate status, where it reaches an operator and nobody else.
    /// </para>
    /// <para>
    /// <strong>The exception and the data dictionary are omitted from the log too.</strong>
    /// An exception message from a failed database probe routinely carries the server
    /// name, the database name and sometimes the login, and a probe's data dictionary
    /// carries the server and database by design; the description is authored text rather
    /// than harvested detail, which is why it is the one narrative value recorded.
    /// </para>
    /// <para>
    /// The document is composed into a buffer and written in one call, so a serialisation
    /// failure cannot leave a half-written body on the wire with a 200 already committed.
    /// </para>
    /// </remarks>
    private static async Task WriteHealthReportAsync(HttpContext context, HealthReport report)
    {
        context.Response.ContentType = "application/json; charset=utf-8";

        LogHealthReport(context, report);

        using MemoryStream buffer = new();

        await using (Utf8JsonWriter writer = new(buffer, new JsonWriterOptions { Indented = false }))
        {
            writer.WriteStartObject();

            // These four members are the whole of the published contract -
            // docs/project-guide.md records this endpoint answering with status,
            // timestamp, version and serviceName - so all four are written and no fifth
            // is. An operator or monitor reading the documentation is served exactly what
            // it describes.
            writer.WriteString("status", report.Status.ToString());
            writer.WriteString("timestamp", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            writer.WriteString("version", ServiceVersion);
            writer.WriteString("serviceName", ServiceName);

            writer.WriteEndObject();
        }

        await context.Response.Body.WriteAsync(buffer.ToArray(), context.RequestAborted)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Records the per-probe detail of a health report as one structured log entry.
    /// </summary>
    /// <param name="context">The request being answered, used to resolve a logger.</param>
    /// <param name="report">The report produced by the registered checks.</param>
    /// <remarks>
    /// <para>
    /// This is where "which probe said so" lives, now that the response body carries only
    /// its four contracted members. With a dependency probe and a process-local
    /// audit-delivery probe registered, and with each carrying an authored description the
    /// aggregate status word cannot express, that distinction is the whole diagnostic value
    /// of the endpoint - and losing it would be a real regression, so it is relocated rather
    /// than dropped.
    /// </para>
    /// <para>
    /// Severity follows the aggregate status. A healthy report is written at debug,
    /// because the container probes this endpoint every few seconds for the lifetime of
    /// the deployment and at informational level those entries alone would outnumber
    /// every other entry in the log. Anything other than healthy is a warning at least:
    /// a degraded or unhealthy probe is the reason a deployment is not coming up.
    /// </para>
    /// <para>
    /// The logger is resolved from the request's own service container because this
    /// method is reached through a delegate the health-check options hold, which has no
    /// constructor to inject into. Resolution is tolerant by design: a host composed
    /// without a logger factory still answers the probe, because the body this endpoint
    /// returns must never depend on whether it could be logged.
    /// </para>
    /// </remarks>
    private static void LogHealthReport(HttpContext context, HealthReport report)
    {
        ILogger? logger = context.RequestServices
            .GetService<ILoggerFactory>()
            ?.CreateLogger(HealthLogCategory);

        if (logger is null)
        {
            return;
        }

        LogLevel level = report.Status == HealthStatus.Healthy ? LogLevel.Debug : LogLevel.Warning;

        if (!logger.IsEnabled(level))
        {
            return;
        }

        // Composed as one string rather than as a structured collection: a logger renders
        // a collection by calling ToString on each element, which for a report entry
        // would emit the exception and the data dictionary this method is careful to
        // exclude. Naming the three safe values explicitly is what keeps that exclusion
        // true whatever sink is configured.
        string probes = string.Join(
            ", ",
            report.Entries.Select(entry => FormattableString.Invariant(
                $"{entry.Key}={entry.Value.Status} in {entry.Value.Duration.TotalMilliseconds:F1} ms{DescribeProbe(entry.Value)}")));

        logger.Log(
            level,
            "Health report {HealthStatus} in {TotalDurationMilliseconds:F1} ms. Probes: {HealthProbes}.",
            report.Status.ToString(),
            report.TotalDuration.TotalMilliseconds,
            probes);
    }

    /// <summary>
    /// Writes the standard problem document for a status code that was decided before, or without, an
    /// endpoint - which in this API means routing's own 404 and 405.
    /// </summary>
    /// <param name="context">The status-code context the framework supplies.</param>
    /// <returns>A task that completes once a payload has been written, or declined.</returns>
    /// <remarks>
    /// <para>
    /// Nothing is authored here. The document is built by the registered
    /// <see cref="ProblemDetailsFactory"/> and written by the registered
    /// <see cref="IProblemDetailsService"/> - the same two collaborators every other producer uses - so the
    /// problem type, the title, the detail, the media type and the <c>traceId</c> and <c>correlationId</c>
    /// extensions are decided in exactly one place. Supplying wording here instead would be a second
    /// vocabulary for the two statuses this stage covers, which is the defect it exists to remove.
    /// </para>
    /// <para>
    /// Every argument to the factory beyond the status code is left unspecified deliberately. The
    /// <c>instance</c> member in particular is NOT derived from the request URL: an unmatched path is
    /// caller-controlled text, so reflecting it into the payload would echo whatever an unauthenticated
    /// caller chose to send - including a credential put in the wrong place.
    /// </para>
    /// <para>
    /// The writer's answer is not overridden. When no writer accepts the payload the response keeps the
    /// status line it already had, which is the behaviour before this stage existed and is a correct
    /// answer rather than a failure.
    /// </para>
    /// </remarks>
    private static async Task WriteStatusOnlyProblemAsync(StatusCodeContext context)
    {
        HttpContext httpContext = context.HttpContext;
        int statusCode = httpContext.Response.StatusCode;

        ProblemDetailsFactory factory = httpContext.RequestServices
            .GetRequiredService<ProblemDetailsFactory>();
        IProblemDetailsService problemDetailsService = httpContext.RequestServices
            .GetRequiredService<IProblemDetailsService>();

        ProblemDetails problemDetails = factory.CreateProblemDetails(
            httpContext,
            statusCode,
            title: null,
            type: null,
            detail: null,
            instance: null);

        await problemDetailsService.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            ProblemDetails = problemDetails,
        }).ConfigureAwait(false);
    }

    /// <summary>Renders a probe's authored description, or nothing when it has none.</summary>
    /// <param name="entry">The probe's report entry.</param>
    /// <returns>
    /// The description in parentheses, or <see cref="string.Empty"/> when the probe supplied
    /// none.
    /// </returns>
    /// <remarks>
    /// Only the description is read. The entry also carries the exception the probe raised
    /// and a data dictionary that names the server and the database, and neither is touched
    /// here - the description is authored text, the other two are harvested detail.
    /// </remarks>
    private static string DescribeProbe(HealthReportEntry entry)
        => string.IsNullOrWhiteSpace(entry.Description)
            ? string.Empty
            : FormattableString.Invariant($" ({entry.Description})");
}
