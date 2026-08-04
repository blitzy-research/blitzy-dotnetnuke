using System.Text.Json;
using DnnMigration.Api.Middleware;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Http;
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
/// <b>The fixed order.</b> These ten stages, in this sequence, are fixed by the
/// migration plan and are not open to local variation. The additional stages this
/// file adds around them - transport security, the tenant path base, the rate limiter
/// and the documentation console - are placed so that they do not disturb the
/// relative order of the ten:
/// </para>
/// <list type="number">
/// <item><description><c>UseExceptionHandler()</c>, parameterless.</description></item>
/// <item><description><c>UseMiddleware&lt;CorrelationIdMiddleware&gt;()</c>.</description></item>
/// <item><description><c>UseMiddleware&lt;RequestLoggingMiddleware&gt;()</c>.</description></item>
/// <item><description><c>UseRouting()</c>.</description></item>
/// <item><description><c>UseCors(CorsExtensions.PolicyName)</c>, the named policy.</description></item>
/// <item><description><c>UseAuthentication()</c>.</description></item>
/// <item><description><c>UseAuthorization()</c>.</description></item>
/// <item><description><c>UseMiddleware&lt;PortalAliasResolutionMiddleware&gt;()</c>.</description></item>
/// <item><description><c>MapControllers()</c>.</description></item>
/// <item><description><c>MapHealthChecks("/health")</c>, anonymous.</description></item>
/// </list>
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
/// <b>Tenant resolution follows authentication</b> because it consults the caller's
/// claims; it cannot run before the claims exist.
/// </description></item>
/// <item><description>
/// <b>The endpoints are last</b> because executing one terminates the pipeline.
/// </description></item>
/// </list>
/// <para>
/// <b>Transport security is guarded, deliberately.</b> Redirection to HTTPS is off
/// unless a deployment switches it on, and that is a correctness requirement rather
/// than a preference: in the shipped topology TLS is terminated by the reverse proxy
/// in front of this application, the container listens on plain HTTP, and the image's
/// own health probe reaches this process over plain HTTP on that port. An
/// unconditional redirect would answer the probe with a redirect instead of a
/// response, the container would never report healthy, and the front end that waits on
/// it would never start. See <see cref="UseApiPipeline(WebApplication)"/> for the
/// mechanism and the citation.
/// </para>
/// <para>
/// <b>The health endpoint is anonymous and unthrottled.</b> Both properties are
/// load-bearing rather than incidental: the compose topology holds the front-end
/// container back until this endpoint reports healthy, so anything that authenticates,
/// throttles or redirects it stops the whole deployment from starting.
/// </para>
/// </remarks>
public static class ApplicationBuilderExtensions
{
    /// <summary>
    /// Address of the health endpoint: <c>/health</c>.
    /// </summary>
    /// <remarks>
    /// Fixed by two things outside this codebase that must agree with it exactly: the
    /// container image's own health probe, and the compose topology in which the front
    /// end will not start until this address reports healthy. Changing it here without
    /// changing both of those produces a deployment where the front end never starts.
    /// </remarks>
    public const string HealthEndpointPath = "/health";

    /// <summary>
    /// Endpoint name of the health probe, used only to give the route a stable identity in
    /// the endpoint metadata. It is deliberately not part of any URL.
    /// </summary>
    public const string HealthEndpointName = "HealthCheck";

    /// <summary>
    /// Configuration key a deployment sets to <see langword="true"/> to make this
    /// application redirect plain-HTTP requests to HTTPS itself:
    /// <c>Https:RedirectEnabled</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Off by default, and the reason is concrete rather than cautious - see
    /// <see cref="UseApiPipeline(WebApplication)"/>.
    /// </para>
    /// <para>
    /// The key is deliberately published as a constant rather than spelled inline. It is
    /// read in exactly one place, but it is depended upon from outside this file: a
    /// deployment sets it to switch redirection on, and the integration host sets it to
    /// keep redirection off so that no request is answered with a redirect to an
    /// authority the test server does not listen on. A key that is renamed without both
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
    /// development and needs no switch: the stage that applies it ignores requests
    /// that did not arrive over HTTPS and ignores loopback hosts, so it cannot affect
    /// the container's own plain-HTTP health probe. Redirection is the opposite case
    /// and is off unless a deployment asks for it, because in the shipped topology TLS
    /// is terminated by the reverse proxy in front of this application and the health
    /// probe reaches this process over plain HTTP on the same port. Redirecting
    /// unconditionally would answer that probe with a redirect to a port nothing is
    /// listening on, the probe would never report healthy, and the front-end container
    /// would never start - a total outage produced by a hardening measure. A
    /// deployment that terminates TLS in this process, rather than in front of it,
    /// switches redirection on.
    /// </para>
    /// <para>
    /// <b>On the tenant stage's position.</b> It is placed after authorisation, where
    /// the plan puts it, even though the authorisation handler that decides portal
    /// administration needs a resolved tenant and therefore runs first. That is not a
    /// contradiction: resolution is idempotent and is triggered by whichever of the
    /// two reaches it first, so the handler resolves the tenant when it asks and this
    /// stage then finds the work already done. Its remaining job is the one the
    /// handler cannot do - guaranteeing that an endpoint reached without any
    /// authorisation requirement still cannot execute against an unresolved tenant.
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

        // MIGRATION: forwarded-header processing is deliberately absent. nginx does
        // forward X-Real-IP, X-Forwarded-For and X-Forwarded-Proto, so without this
        // stage the address every later stage observes is the proxy's rather than the
        // caller's, and the credential limiter's per-address partition therefore
        // collapses into one shared budget for all callers behind the proxy. That is
        // recorded rather than fixed because the degradation is strictly more
        // restrictive: callers share a smaller allowance than they otherwise would,
        // which can refuse a legitimate request but can never admit one the per-address
        // partition would have refused. A deployment that needs true per-caller
        // partitioning terminates that decision at the proxy.

        // First, and unconditionally. An exception handler covers what follows it and
        // nothing that precedes it, so this is the one stage whose position admits no
        // argument: every stage below is inside its scope.
        app.UseExceptionHandler();

        // Applied outside development, and it needs no switch of its own because it
        // cannot reach the health probe. This stage only sets a response header - it
        // never redirects and never changes a status code - and it sets it only on a
        // request that arrived over HTTPS to a host that is not excluded. The
        // container's probe is plain HTTP to a loopback address, so it satisfies
        // neither condition and the header is never even emitted.
        if (!app.Environment.IsDevelopment())
        {
            app.UseHsts();
        }

        // MIGRATION: redirection to HTTPS is enforced only when a deployment asks for
        // it, and the default is off. docs/project-guide.md:L282 (task H3, SSL/TLS
        // Certificate Setup) assigns "Configure HTTPS redirection" and "Update nginx
        // for HTTPS" to deferred production work performed AT THE PROXY, and
        // docker/api.Dockerfile:L69 sets ASPNETCORE_URLS=http://+:8080 so this process
        // speaks plain HTTP on the internal network by design. Redirecting
        // unconditionally would answer docker/api.Dockerfile:L76-L77's
        // "wget --spider http://127.0.0.1:8080/health" with a redirect rather than a
        // response; the probe would never report healthy; docker-compose.yml's
        // "condition: service_healthy" would never be satisfied; the front-end
        // container would never start; and the end-to-end gate would fail. No
        // compiler, analyser or unit test catches that, which is exactly why the guard
        // is here and why it is documented at this length. A host that terminates TLS
        // in this process rather than in front of it switches the flag on.
        if (app.Configuration.GetValue<bool>(HttpsRedirectionSectionName))
        {
            app.UseHttpsRedirection();
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

        // Before routing, and only this stage can be. A child portal is addressed by a
        // path segment beneath a shared host, so the segment that identifies the tenant
        // sits in front of the path the routes were written against; it has to move
        // into the path base before routing matches, or every request to a child
        // portal answers 404 no matter how correctly its tenant resolved. Routing
        // cannot be un-done afterwards, which is why the named portal-alias stage
        // below - fixed after authorisation by the mandated order - cannot do this.
        // Nothing named in that order moves: this is an additional, un-named stage.
        app.UseMiddleware<TenantPathBaseMiddleware>();

        // MIGRATION: no static-file stage, and none is missing. The legacy application
        // served its own markup, stylesheets and skins from this process; this one
        // returns JSON and nothing else. docker/nginx.conf:L91 serves the single-page
        // application with "try_files $uri $uri/ /index.html" and proxies only /api/
        // and /health through to this process (L61 and L74), so a static-file stage here
        // would have no content to serve and would add a filesystem probe to every
        // request that misses a route. Server-side rendering is out of scope: there is
        // no Razor, no view engine and no skinning in this application.

        // Routing must precede the four stages below. Cross-origin policy, the rate
        // limiter and authorisation each read metadata from the endpoint that routing
        // selects; placed before routing there is no endpoint yet and each of them
        // would silently fall back to its global behaviour.
        app.UseRouting();

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

        // Between authentication and authorisation, because the documentation console
        // needs both: outside development its gate reads the authenticated caller, and
        // the console answers requests itself, so an authorisation stage carrying a
        // fallback policy would refuse it before it was reached. Adds nothing at all
        // unless the environment or an explicit opt-in says so.
        app.UseSwaggerDocumentation(app.Environment, app.Configuration);

        app.UseAuthorization();

        // After authorisation, before the endpoints. See the remarks above for why
        // this position is correct even though the portal-administration handler needs
        // a resolved tenant and runs earlier.
        app.UseMiddleware<PortalAliasResolutionMiddleware>();

        app.MapControllers();

        // Anonymous, explicitly and necessarily. A fallback policy requires an
        // authenticated caller for every endpoint that does not say otherwise, and
        // this endpoint must answer a container probe that runs before any credential
        // exists in the system at all. Without this call the probe receives 401, is
        // never healthy, and nothing that waits on it ever starts.
        //
        // It is also unthrottled, and that is a property of the limiter's classifier
        // rather than of anything declared here: every partition resolves to the shared
        // no-limit partition unless a request is credential-bearing, and this endpoint
        // is neither annotated as credential-bearing nor reached by a mutating method.
        // It therefore consumes no budget and cannot be refused for exceeding one.
        // Both properties are depended upon from outside this codebase -
        // docker/api.Dockerfile probes it and docker-compose.yml holds the front-end
        // container back until it answers - so neither may be narrowed here.
        //
        // Response headers for the browser-facing origin are the proxy's concern, not
        // this pipeline's: docs/project-guide.md:L295 (task M5) assigns the content
        // security policy, frame options and strict transport security to deferred work
        // configured at nginx, so no header stage is registered here.
        //
        // The report is written as JSON rather than left to the framework's default
        // writer. The default emits the status word alone, which tells an operator that
        // something is wrong but not WHICH of the two database probes said so - and with
        // two probes registered against the same database, that distinction is the whole
        // diagnostic value of the endpoint.
        app.MapHealthChecks(
                HealthEndpointPath,
                new HealthCheckOptions
                {
                    AllowCachingResponses = false,
                    ResponseWriter = WriteHealthReportAsync,
                })
            .AllowAnonymous()
            .WithName(HealthEndpointName)
            .ExcludeFromDescription();

        return app;
    }

    /// <summary>Writes the health report as JSON.</summary>
    /// <param name="context">The request being answered.</param>
    /// <param name="report">The report produced by the registered checks.</param>
    /// <returns>A task that completes when the response body has been written.</returns>
    /// <remarks>
    /// <para>
    /// Each entry is named alongside its own status, so a partial failure identifies the
    /// probe that failed instead of collapsing into one word.
    /// </para>
    /// <para>
    /// <strong>The exception and the data dictionary are deliberately omitted.</strong>
    /// This endpoint is anonymous, so everything written here is public. An exception
    /// message from a failed database probe routinely carries the server name, the
    /// database name and sometimes the login, and a probe's data dictionary carries the
    /// server and database by design. The description is included because it is authored
    /// text rather than harvested detail.
    /// </para>
    /// <para>
    /// The document is composed into a buffer and written in one call, so a serialisation
    /// failure cannot leave a half-written body on the wire with a 200 already committed.
    /// </para>
    /// </remarks>
    private static async Task WriteHealthReportAsync(HttpContext context, HealthReport report)
    {
        context.Response.ContentType = "application/json; charset=utf-8";

        using MemoryStream buffer = new();

        await using (Utf8JsonWriter writer = new(buffer, new JsonWriterOptions { Indented = false }))
        {
            writer.WriteStartObject();
            writer.WriteString("status", report.Status.ToString());
            writer.WriteNumber("totalDurationMs", report.TotalDuration.TotalMilliseconds);
            writer.WriteStartArray("checks");

            foreach (KeyValuePair<string, HealthReportEntry> entry in report.Entries)
            {
                writer.WriteStartObject();
                writer.WriteString("name", entry.Key);
                writer.WriteString("status", entry.Value.Status.ToString());
                writer.WriteNumber("durationMs", entry.Value.Duration.TotalMilliseconds);

                if (!string.IsNullOrWhiteSpace(entry.Value.Description))
                {
                    writer.WriteString("description", entry.Value.Description);
                }

                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        await context.Response.Body.WriteAsync(buffer.ToArray(), context.RequestAborted)
            .ConfigureAwait(false);
    }
}
