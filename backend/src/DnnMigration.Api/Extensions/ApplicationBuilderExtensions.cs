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
/// The relative order of the exception handler, the correlation stage, request
/// logging, routing, cross-origin policy, authentication, authorisation, tenant
/// resolution, the controllers and the health endpoint is fixed by the migration
/// plan and is not open to local variation. The stages this file adds around them -
/// forwarded headers, transport security, the rate limiter and the documentation
/// console - are placed so that they do not disturb that relative order.
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
    /// Off by default, and the reason is concrete rather than cautious - see
    /// <see cref="UseApiPipeline(WebApplication)"/>.
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
    /// </remarks>
    public static WebApplication UseApiPipeline(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        // First, unconditionally. Every later stage that reads the caller's address or
        // the request's scheme - the rate limiter's partition, transport security,
        // request logging - must read the values as corrected here, not the proxy's.
        // Which hops are trusted to supply them is decided at registration; by default
        // nothing beyond loopback is, so on an untrusted hop these headers are ignored
        // rather than believed.
        app.UseForwardedHeaders();

        // Second, so that every stage after it is inside its scope. An exception
        // handler covers what follows it and nothing that precedes it; only the
        // forwarded-header stage above is left outside, and it does not throw on
        // malformed input - it ignores it.
        app.UseExceptionHandler();

        if (!app.Environment.IsDevelopment())
        {
            app.UseHsts();
        }

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
