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
/// Composes the request pipeline. This is the one place where the order of the application's stages is
/// decided.
/// </summary>
/// <remarks>
/// <para>
/// Pipeline order is behaviour, not style.
/// </para>
/// <para>
/// <b>The fixed order.</b> Ten stages carry a mandated relative sequence and are not open to local
/// variation: <c>UseExceptionHandler()</c>, <c>CorrelationIdMiddleware</c>,
/// <c>RequestLoggingMiddleware</c>, <c>UseRouting()</c>, <c>UseCors()</c> with the named policy,
/// <c>UseAuthentication()</c>, <c>PortalAliasResolutionMiddleware</c>, <c>UseAuthorization()</c>,
/// <c>MapControllers()</c>, and the health endpoints. The three <c>MapHealthChecks</c> calls occupy one
/// position rather than three: they are sibling terminal endpoints on distinct paths, so the order
/// between them carries no meaning.
/// </para>
/// <para>
/// Every other stage this file adds is placed so that it does not disturb that relative order:
/// forwarded-header processing, transport security, the transport refusal and status-code writers, the
/// tenant path base, the credential cache-control and origin-vary markers, the rate limiter, the
/// restricted-session stage and the documentation console.
/// </para>
/// </remarks>
public static class ApplicationBuilderExtensions
{
    /// <summary>The deployed liveness endpoint: <c>/health</c>.</summary>
    public const string HealthEndpointPath = "/health";

    /// <summary>Explicit liveness endpoint, containing no dependency checks.</summary>
    public const string LivenessHealthEndpointPath = "/health/live";

    /// <summary>
    /// Endpoint name of the health probe, used only to give the route a stable identity in the endpoint
    /// metadata. It is deliberately not part of any URL.
    /// </summary>
    public const string HealthEndpointName = "HealthCheck";

    /// <summary>Endpoint name of the explicit liveness probe.</summary>
    public const string LivenessHealthEndpointName = "LivenessHealthCheck";

    /// <summary>Address of the readiness endpoint: <c>/health/ready</c>.</summary>
    public const string ReadinessEndpointPath = "/health/ready";

    /// <summary>Endpoint name of the readiness probe. Like the liveness name, metadata only.</summary>
    public const string ReadinessEndpointName = "ReadinessCheck";

    /// <summary>
    /// Tag a probe carries to declare itself a readiness signal rather than a liveness one: <c>ready</c>.
    /// </summary>
    private const string ReadinessTag = "ready";

    /// <summary>
    /// Configuration key a deployment sets to override whether this application redirects plain-HTTP
    /// requests to HTTPS itself: <c>Https:RedirectEnabled</c>.
    /// </summary>
    public const string HttpsRedirectionSectionName = "Https:RedirectEnabled";

    /// <summary>Configuration key naming the externally reachable TLS port.</summary>
    public const string HttpsRedirectPortSectionName = "Https:RedirectPort";

    /// <summary>Log category under which the health report's per-probe detail is recorded.</summary>
    /// <remarks>
    /// Named after this type so an operator can raise or lower the health log on its own without touching
    /// the rest of the pipeline's logging, which matters because a healthy probe arrives every few seconds
    /// for the lifetime of the deployment.
    /// </remarks>
    private const string HealthLogCategory = "DnnMigration.Api.HealthChecks";

    /// <summary>Name this service reports in its health document.</summary>
    /// <remarks>
    /// Read from the assembly rather than written as a literal, and that is the point: the same name is the
    /// file the container image starts, so deriving it here means the document cannot claim to be a service
    /// the image does not run. It resolves to <c>DnnMigration.Api</c>.
    /// </remarks>
    private static readonly string ServiceName =
        typeof(ApplicationBuilderExtensions).Assembly.GetName().Name ?? "DnnMigration.Api";

    /// <summary>Version this service reports in its health document.</summary>
    /// <remarks>
    /// The project sets no explicit version, so this is the SDK's default of <c>1.0.0.0</c> - which is
    /// exactly what the published operator documentation records. Resolved once, because the probe runs on
    /// a schedule for the lifetime of the process and reflection per probe would buy nothing.
    /// </remarks>
    private static readonly string ServiceVersion =
        typeof(ApplicationBuilderExtensions).Assembly.GetName().Version?.ToString() ?? "1.0.0.0";

    /// <summary>
    /// Composes every stage of the request pipeline, in order, and maps the application's endpoints.
    /// </summary>
    /// <param name="app">The application being composed.</param>
    /// <returns>The same <paramref name="app"/> instance, so the caller can run it.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="app"/> is <see langword="null"/>.</exception>
    /// <remarks>
    /// <b>On the port the redirect names.</b> It is configured, not discovered.
    /// </remarks>
    public static WebApplication UseApiPipeline(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        // This method replaces the legacy request pipeline wholesale rather than porting it.

        // First, and unconditionally. An exception handler covers what follows it and nothing that precedes
        // it, so this is the one stage whose position admits no argument: every stage below is inside its
        // scope.
        app.UseExceptionHandler();

        // ⚠ THIS EARLY, BECAUSE COMPRESSION HAS TO WRAP THE STAGES THAT WRITE BODIES. Response compression is
        // outer to everything registered after it and inert for everything registered before it, so the only
        // position from which it can compress this API's responses at all is above the routing and endpoint
        // stages that produce them. It is inside the exception handler rather than outside it, so a fault the
        // handler answers is written by the handler itself and observed uncompressed while diagnosing.
        //
        // It changes no response a caller did not ask to have changed: a request that advertises no acceptable
        // encoding is served exactly the bytes it was served before. On the documented topology the reverse
        // proxy compresses and this stage never fires; on the direct-to-Kestrel path - the container network,
        // or any deployment terminating transport elsewhere - it is the only thing that does.
        app.UseResponseCompression();

        // SEC: FORWARDED HEADERS ARE PROCESSED HERE, AS EARLY AS POSSIBLE, AND ONLY WHEN A DEPLOYMENT HAS
        // NAMED THE PROXIES IT RUNS. Three later decisions read what this stage promotes and are wrong
        // without it: strict transport security and the redirect below both test the request's SCHEME,
        // which behind a TLS-terminating proxy arrives only in X-Forwarded-Proto; and the credential
        // limiter partitions on the caller's ADDRESS, which behind any proxy arrives only in
        // X-Forwarded-For.
        if (ServiceCollectionExtensions.HasTrustedProxies(app.Configuration))
        {
            app.UseForwardedHeaders();
        }

        // Before request logging, and that ordering is the whole point: this stage opens the logging scope
        // that carries the correlation identifier, so a log written by any later stage is correlated
        // without that stage having to know the identifier exists.
        app.UseMiddleware<CorrelationIdMiddleware>();

        app.UseMiddleware<RequestLoggingMiddleware>();

        if (!app.Environment.IsDevelopment())
        {
            app.UseHsts();
        }

        if (app.Configuration.GetValue<bool>(HttpsRedirectionSectionName))
        {
            app.UseWhen(
                TransportSecurityApplies,
                transportSecured => transportSecured.UseHttpsRedirection());
        }
        else if (!app.Environment.IsDevelopment())
        {
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
        // which would record it at Error as an unhandled fault whatever status it resolved to.
        app.UseMiddleware<TransportRefusalMiddleware>();

        app.UseStatusCodePages(WriteStatusOnlyProblemAsync);

        // Before routing, and only this stage can be.
        app.UseMiddleware<TenantPathBaseMiddleware>();

        // No static-file stage, and none is missing.

        // Routing must precede the four stages below. Cross-origin policy, the rate limiter and
        // authorisation each read metadata from the endpoint that routing selects; placed before routing
        // there is no endpoint yet and each of them would silently fall back to its global behaviour.
        app.UseRouting();

        // SEC: IMMEDIATELY AFTER ROUTING AND BEFORE EVERYTHING THAT CAN REFUSE A REQUEST. This stage marks
        // a credential endpoint's response as never cacheable, and it needs the endpoint routing selected
        // in order to read the credential-endpoint metadata - so it cannot run earlier.
        app.UseMiddleware<CredentialCacheControlMiddleware>();

        // Immediately before the cross-origin stage, so that every response the stage can influence
        // announces the request header its content depends on.
        app.UseMiddleware<OriginVaryMiddleware>();

        // Before the rate limiter, so that a refusal still carries the cross-origin headers. Without them a
        // browser cannot read the refusal at all: the caller would see an opaque network failure instead of
        // the status and the retry hint the limiter took care to send.
        app.UseCors(CorsExtensions.PolicyName);

        // Before authentication, deliberately.
        app.UseRateLimiter();

        app.UseAuthentication();

        app.UseMiddleware<RestrictedSessionMiddleware>();

        // Between authentication and authorisation, because the documentation console needs both: outside
        // development its gate reads the authenticated caller, and the console answers requests itself, so
        // an authorisation stage carrying a fallback policy would refuse it before it was reached.
        app.UseSwaggerDocumentation(app.Environment, app.Configuration);

        app.UseMiddleware<PortalAliasResolutionMiddleware>();

        app.UseAuthorization();

        app.MapControllers();

        // Anonymous, explicitly and necessarily. /health is the LIVENESS view and is the one the image's
        // HEALTHCHECK, the compose api health check and the end-to-end gate all read, so it is what decides
        // whether the api container reports healthy and therefore whether the frontend service starts.
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
        // document shape.
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
    /// <returns><see langword="true"/> for every probe that is not tagged as a readiness signal.</returns>
    private static bool IsLivenessProbe(HealthCheckRegistration registration)
    {
        ArgumentNullException.ThrowIfNull(registration);

        return !registration.Tags.Contains(ReadinessTag, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Selects the probes that belong to the readiness view.</summary>
    /// <param name="registration">The probe being considered.</param>
    /// <returns><see langword="true"/> for every probe tagged as a readiness signal.</returns>
    private static bool IsReadinessProbe(HealthCheckRegistration registration)
    {
        ArgumentNullException.ThrowIfNull(registration);

        return registration.Tags.Contains(ReadinessTag, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Reports whether transport security is enforced for one request.</summary>
    /// <param name="context">The request being classified.</param>
    /// <returns>
    /// <see langword="false"/> for the three published health paths, so none of them is ever answered with
    /// a redirect; otherwise <see langword="true"/>.
    /// </returns>
    /// <remarks>
    /// ONE EXCLUSION, AND IT IS LOAD-BEARING RATHER THAN LENIENT. The health paths must answer a container
    /// health check that runs over plain HTTP before any credential exists in the system, and the compose
    /// topology holds the front-end service back until that check succeeds; redirecting it would leave a
    /// correctly built deployment permanently unhealthy.
    /// </remarks>
    private static bool TransportSecurityApplies(HttpContext context) =>
        !IsPublishedHealthPath(context.Request.Path);

    /// <summary>Reports whether a path is one of the three health views mapped by this pipeline.</summary>
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
    private static async Task WriteHealthReportAsync(HttpContext context, HealthReport report)
    {
        context.Response.ContentType = "application/json; charset=utf-8";

        LogHealthReport(context, report);

        using MemoryStream buffer = new();

        await using (Utf8JsonWriter writer = new(buffer, new JsonWriterOptions { Indented = false }))
        {
            writer.WriteStartObject();

            writer.WriteString("status", report.Status.ToString());
            writer.WriteString("timestamp", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            writer.WriteString("version", ServiceVersion);
            writer.WriteString("serviceName", ServiceName);

            writer.WriteEndObject();
        }

        await context.Response.Body.WriteAsync(buffer.ToArray(), context.RequestAborted)
            .ConfigureAwait(false);
    }

    /// <summary>Records the per-probe detail of a health report as one structured log entry.</summary>
    /// <param name="context">The request being answered, used to resolve a logger.</param>
    /// <param name="report">The report produced by the registered checks.</param>
    /// <remarks>
    /// The logger is resolved from the request's own service container because this method is reached
    /// through a delegate the health-check options hold, which has no constructor to inject into.
    /// Resolution is tolerant by design: a host composed without a logger factory still answers the probe,
    /// because the body this endpoint returns must never depend on whether it could be logged.
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
    /// Every argument to the factory beyond the status code is left unspecified deliberately. The
    /// <c>instance</c> member in particular is NOT derived from the request URL: an unmatched path is
    /// caller-controlled text, so reflecting it into the payload would echo whatever an unauthenticated
    /// caller chose to send - including a credential put in the wrong place.
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
    /// <returns>The description in parentheses, or <see cref="string.Empty"/> when the probe supplied none.</returns>
    private static string DescribeProbe(HealthReportEntry entry)
        => string.IsNullOrWhiteSpace(entry.Description)
            ? string.Empty
            : FormattableString.Invariant($" ({entry.Description})");
}
