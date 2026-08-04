using System.Diagnostics;

namespace DnnMigration.Api.Middleware;

/// <summary>
/// Records one structured log entry for every request that reaches the application,
/// carrying what it was, how it ended and how long it took - and nothing else.
/// </summary>
/// <remarks>
/// <para>
/// MIGRATION: the legacy application had no request log. It wrote audit entries for
/// business events through its own event-log provider - a portal name, a user name, a
/// user id and an event type - and nothing that described a request or tied two
/// entries to the same one. This stage is therefore net-new, and the entry it writes
/// is NOT a substitute for those audit records: the legacy event-type audit entries
/// become Serilog structured events, under stable event names, emitted by the
/// Application-layer service that performs the business operation, at the point where
/// its outcome is known. This stage supplies the request envelope and the correlation
/// identifier that ties those events to the request that caused them; it is not the
/// audit sink, it names no portal and it names no business event.
/// </para>
/// <para>
/// <b>What is deliberately absent from the entry, and why.</b> No request body, no
/// response body, no header, no query-string value, no account name, no address. A
/// request log is the highest-volume, longest-retained and most widely-readable log an
/// application produces, so anything personal placed in it is copied everywhere and
/// kept the longest.
/// </para>
/// <para>
/// Two cases make that rule non-negotiable rather than merely prudent. The sign-in
/// endpoint carries credentials in its request body, so a middleware that captured
/// bodies in order to log them would write every credential this application ever
/// receives into its most widely-read log; nothing here reads either body, and no
/// buffering is enabled, so there is no window in which one could leak. The inbound
/// credential header carries a token that grants access for as long as it remains
/// valid; no header value is read at all. The query string is the subtler case: the
/// search endpoints of this API take a free-text filter, so an operator searching for a
/// user by email address would write that address into the log of every request that
/// carried it. The entry therefore records only <em>whether</em> a query string was
/// present, which is what an operator needs in order to tell a filtered request from an
/// unfiltered one, and is the one fact about it that cannot identify anybody.
/// </para>
/// <para>
/// <b>One instance serves the whole application.</b> Registration through
/// <c>UseMiddleware&lt;RequestLoggingMiddleware&gt;()</c> activates a single instance for
/// the application's lifetime, so this type holds nothing request-scoped: its only
/// dependencies are the pipeline continuation and a logger, both safe to hold for that
/// lifetime. Per-request state lives on the <see cref="HttpContext"/> passed to
/// <see cref="InvokeAsync(HttpContext)"/>, nothing is resolved from the request's own
/// service container, and no logger, sink or filter is configured here - that belongs
/// to the host.
/// </para>
/// <para>
/// <b>Pipeline position is fixed:</b> inside the global exception handler, immediately
/// inside the correlation-identifier stage, and outside everything else. Being inside
/// the correlation stage is what puts the identifier on this entry and on every entry
/// written while the request is handled. Being outside routing, authentication and the
/// controllers is what makes the single entry measure the whole of the work the request
/// caused. Being inside the exception handler means a failure escapes through this
/// stage before that handler sees it, which is why the entry is written from a block
/// that runs whatever the outcome and why the failure is then re-thrown unchanged.
/// </para>
/// </remarks>
public sealed class RequestLoggingMiddleware
{
    /// <summary>
    /// Address of the health endpoint, recognised here only so that a successful probe
    /// can be logged quietly.
    /// </summary>
    /// <remarks>
    /// This stage runs before routing, so it cannot ask the routing system which address
    /// the probe was mapped to and has to compare against a literal. The authoritative
    /// declaration is <c>ApplicationBuilderExtensions.HealthEndpointPath</c>, and this
    /// constant tracks it. Should the two ever diverge the only consequence is the volume
    /// of this log - a probe would be recorded at informational level again - never a
    /// change in what the endpoint returns or in who may reach it. That is deliberately
    /// the mildest failure mode available, because the container's own probe and the
    /// compose topology that waits on it must not depend on anything decided here.
    /// </remarks>
    private const string HealthPath = "/health";

    private readonly RequestDelegate _next;
    private readonly ILogger<RequestLoggingMiddleware> _logger;

    /// <summary>
    /// Initialises a new instance of the <see cref="RequestLoggingMiddleware"/> class.
    /// </summary>
    /// <param name="next">The remainder of the request pipeline.</param>
    /// <param name="logger">Receives the completion entry.</param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="next"/> or <paramref name="logger"/> is <see langword="null"/>.
    /// </exception>
    public RequestLoggingMiddleware(RequestDelegate next, ILogger<RequestLoggingMiddleware> logger)
    {
        ArgumentNullException.ThrowIfNull(next);
        ArgumentNullException.ThrowIfNull(logger);

        _next = next;
        _logger = logger;
    }

    /// <summary>
    /// Times the request, runs the remainder of the pipeline, and records the outcome as
    /// exactly one structured entry.
    /// </summary>
    /// <param name="context">The context of the request being processed.</param>
    /// <returns>A task that completes when the request has been handled.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="context"/> is <see langword="null"/>.
    /// </exception>
    /// <remarks>
    /// <para>
    /// Timing is taken from the high-resolution timestamp rather than from a stopwatch
    /// instance, so measuring a request allocates nothing. This runs on every request, so
    /// its own cost is the one cost paid unconditionally. The elapsed time is deliberately
    /// not read from a clock: a wall clock can be adjusted mid-request and would then
    /// report a negative or wildly inflated duration.
    /// </para>
    /// <para>
    /// Exactly one entry is written, on the success path and on the failure path alike,
    /// from a block that runs whatever the outcome. A failure is attached to that same
    /// entry rather than logged as a second one, so the count of these entries is the
    /// count of requests handled and can be read as such.
    /// </para>
    /// </remarks>
    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        long startedAt = Stopwatch.GetTimestamp();
        Exception? failure = null;

        try
        {
            await _next(context).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            // Recorded, not handled. The entry below has to be able to say that this
            // request failed, because the status code on the response is still whatever
            // it was before the failure - the handler that turns this into an RFC 7807
            // response sits outside this stage and has not run yet.
            failure = exception;

            // Bare re-throw: preserves the original stack trace, and lets the failure
            // reach the global exception handler untouched. Swallowing it here would
            // suppress the error response the caller is owed.
            throw;
        }
        finally
        {
            int statusCode = context.Response.StatusCode;
            LogLevel level = SelectLevel(statusCode, failure, IsHealthProbe(context));

            // The level is tested before the entry is composed, not to change what gets
            // recorded - a disabled level is discarded by the logger either way - but to
            // avoid paying for it. Composing the entry allocates an argument array and
            // boxes the status code, the duration and the flag; a health probe arrives
            // every few seconds for the lifetime of the deployment and its entry is
            // below the configured floor, so this is the one request in the application
            // whose entry is routinely built only to be thrown away. The entry is still
            // DEFINED for that path and appears the moment an operator lowers the floor
            // to diagnose one, which is the difference between logging it quietly and
            // excluding it from the pipeline as a special case.
            if (_logger.IsEnabled(level))
            {
                _logger.Log(
                    level,
                    failure,
                    "{RequestMethod} {RequestPath} completed with {StatusCode} in {ElapsedMilliseconds:F1} ms. Query string present: {HasQueryString}. Correlation id: {CorrelationId}.",
                    context.Request.Method,
                    context.Request.Path.Value,
                    statusCode,
                    Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds,
                    context.Request.QueryString.HasValue,
                    ResolveCorrelationId(context));
            }
        }
    }

    /// <summary>
    /// Chooses the severity at which the completion entry is written.
    /// </summary>
    /// <param name="statusCode">The status code on the response as this stage unwinds.</param>
    /// <param name="failure">
    /// The failure that escaped the remainder of the pipeline, or <see langword="null"/>
    /// when the request completed.
    /// </param>
    /// <param name="isHealthProbe">Whether the request addressed the health endpoint.</param>
    /// <returns>The severity for this request's entry.</returns>
    /// <remarks>
    /// <para>
    /// Four outcomes, in the order they are tested. An escaped failure or a server-error
    /// status is this application's own fault and an operator has to see it, so it is an
    /// error. A client-error status is the caller's fault: worth noticing, not worth
    /// alarming anybody, so it is a warning - and it is deliberately not promoted to
    /// error, or the error stream would fill with ordinary validation failures until the
    /// genuine faults in it were invisible. A <em>successful</em> health probe is written
    /// at debug, below the configured floor: the container probes this endpoint every few
    /// seconds for the lifetime of the deployment, and at informational level those
    /// probes alone would outnumber every other entry in the log. Everything else is
    /// routine and informational.
    /// </para>
    /// <para>
    /// The health downgrade is tested last on purpose, so it can only ever quieten a
    /// probe that succeeded. A probe answering 503 is the single most important entry this
    /// application can write - it is the reason a deployment is not coming up - and it
    /// stays an error.
    /// </para>
    /// </remarks>
    private static LogLevel SelectLevel(int statusCode, Exception? failure, bool isHealthProbe)
    {
        if (failure is not null || statusCode >= StatusCodes.Status500InternalServerError)
        {
            return LogLevel.Error;
        }

        if (statusCode >= StatusCodes.Status400BadRequest)
        {
            return LogLevel.Warning;
        }

        return isHealthProbe ? LogLevel.Debug : LogLevel.Information;
    }

    /// <summary>
    /// Reports whether the request addressed the health endpoint.
    /// </summary>
    /// <param name="context">The context of the request being processed.</param>
    /// <returns>
    /// <see langword="true"/> when the request path is exactly <see cref="HealthPath"/>,
    /// compared without regard to case; otherwise <see langword="false"/>.
    /// </returns>
    /// <remarks>
    /// The comparison is ordinal so that it cannot vary with the server's culture, and
    /// case-insensitive because a URL path is matched that way by the routing that runs
    /// after this stage. It is an equality test rather than a prefix test: a request to a
    /// longer path that merely begins with the same characters is a different request and
    /// must not inherit the quieter level.
    /// </remarks>
    private static bool IsHealthProbe(HttpContext context)
    {
        return string.Equals(context.Request.Path.Value, HealthPath, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Reads the correlation identifier that <see cref="CorrelationIdMiddleware"/> published
    /// for this request.
    /// </summary>
    /// <param name="context">The context of the request being processed.</param>
    /// <returns>
    /// The published identifier, or <see cref="HttpContext.TraceIdentifier"/> when none was
    /// published. Never <see langword="null"/>.
    /// </returns>
    /// <remarks>
    /// <para>
    /// <see cref="CorrelationIdMiddleware.ItemKey"/> is read rather than the inbound header,
    /// because that key holds the value that stage validated, whereas the header holds
    /// whatever the caller sent. The constant is referenced rather than its text repeated,
    /// so the two stages cannot drift apart.
    /// </para>
    /// <para>
    /// The fallback costs nothing and removes a way for this entry to be useless: that
    /// stage also assigns the identifier to <see cref="HttpContext.TraceIdentifier"/>, so
    /// in the composed pipeline both sources hold the same value, and if this stage is ever
    /// exercised without it - in a test, or after a re-ordering - the entry still carries
    /// the framework's own request identifier instead of nothing at all.
    /// </para>
    /// <para>
    /// The identifier is named on the entry even though the surrounding logging scope
    /// already carries it under the same property name. The values are identical by
    /// construction, so naming it is not a conflict; it makes this entry legible on its
    /// own, and keeps one property name answering the question for every entry the
    /// application writes, whether or not a scope reached it.
    /// </para>
    /// </remarks>
    private static string ResolveCorrelationId(HttpContext context)
    {
        // ContainsKey before the indexer: this dictionary is keyed by object and the
        // indexer is only guaranteed to tolerate an absent key on the framework's own
        // implementation. Probing first is correct against any implementation, and needs
        // no output argument in the bargain.
        if (context.Items.ContainsKey(CorrelationIdMiddleware.ItemKey)
            && context.Items[CorrelationIdMiddleware.ItemKey] is string published
            && published.Length > 0)
        {
            return published;
        }

        return context.TraceIdentifier;
    }
}
