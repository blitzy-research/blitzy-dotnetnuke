using System.Diagnostics;
using System.Text;
using DnnMigration.Api.ErrorHandling;

namespace DnnMigration.Api.Middleware;

/// <summary>
/// Records one structured log entry for every request that reaches the application, carrying what it was,
/// how it ended and how long it took - and nothing else.
/// </summary>
/// <remarks>
/// <para>
/// The legacy application had no request log. It wrote audit entries for business events through its own
/// event-log provider - a portal name, a user name, a user id and an event type - and nothing that
/// described a request or tied two entries to the same one.
/// </para>
/// <para>
/// <b>What is deliberately absent from the entry, and why.</b> No request body, no response body, no
/// header, no query-string value, no account name, no address - and <b>no exception object and no exception
/// message</b>. A request log is the highest-volume, longest-retained and most widely-readable log an
/// application produces, so anything personal placed in it is copied everywhere and kept the longest.
/// </para>
/// </remarks>
public sealed class RequestLoggingMiddleware
{
    /// <summary>
    /// Address of the health endpoint, recognised here only so that a successful probe can be logged
    /// quietly.
    /// </summary>
    /// <remarks>
    /// This stage runs before routing, so it cannot ask the routing system which address the probe was
    /// mapped to and has to compare against a literal. The authoritative declaration is
    /// <c>ApplicationBuilderExtensions.HealthEndpointPath</c>, and this constant tracks it.
    /// </remarks>
    private const string HealthPath = "/health";

    /// <summary>Value recorded for the failure property of a request that completed.</summary>
    private const string NoFailureDescription = "none";

    /// <summary>Separator placed between the type names of a failure and its inner failure.</summary>
    private const string ChainSeparator = " ---> ";

    /// <summary>Marker appended when a failure chain is longer than the walk allows.</summary>
    private const string ChainTruncationMarker = " ---> (chain truncated)";

    /// <summary>The number of failures described before the walk stops.</summary>
    private const int MaximumDescribedChainDepth = 5;

    private readonly RequestDelegate _next;
    private readonly ILogger<RequestLoggingMiddleware> _logger;

    /// <summary>Initialises a new instance of the <see cref="RequestLoggingMiddleware"/> class.</summary>
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
    /// Times the request, runs the remainder of the pipeline, and records the outcome as exactly one
    /// structured entry.
    /// </summary>
    /// <param name="context">The context of the request being processed.</param>
    /// <returns>A task that completes when the request has been handled.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="context"/> is <see langword="null"/>.</exception>
    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        long startedAt = Stopwatch.GetTimestamp();

        // Assigned by the catch below and read by the callback registered immediately after. The callback
        // closes over the VARIABLE rather than its value, and it runs once the response is complete - after
        // the catch has run - so the failure it reads is the one that escaped, not the null this starts as.
        Exception? failure = null;

        // Registered before the continuation is invoked, so it is in place however the request ends:
        // answered, refused, faulted, or abandoned by a caller who went away. The server fires completion
        // callbacks on every one of those paths.
        context.Response.OnCompleted(() =>
        {
            RecordCompletion(context, startedAt, failure);

            return Task.CompletedTask;
        });

        try
        {
            await _next(context).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            // Recorded, not handled.
            failure = exception;

            throw;
        }
    }

    /// <summary>Writes the single completion entry for a request whose response has finished.</summary>
    /// <param name="context">The context of the request being recorded.</param>
    /// <param name="startedAt">The timestamp taken as the request entered this stage.</param>
    /// <param name="failure">
    /// The failure that escaped the remainder of the pipeline, or <see langword="null"/> when the request
    /// completed without one.
    /// </param>
    /// <remarks>
    /// The level is tested before the entry is composed, not to change what gets recorded - a disabled
    /// level is discarded by the logger either way - but to avoid paying for it.
    /// </remarks>
    private void RecordCompletion(HttpContext context, long startedAt, Exception? failure)
    {
        // A cancellation raised because the caller went away is not a fault of this application, and
        // recording it as an error would bury the failures that are.
        bool abandonedByCaller = failure is OperationCanceledException
            && context.RequestAborted.IsCancellationRequested;

        int statusCode = ResolveAnsweredStatusCode(context);
        LogLevel level = SelectLevel(statusCode, failure, abandonedByCaller, IsHealthProbe(context));

        if (!_logger.IsEnabled(level))
        {
            return;
        }

        _logger.Log(
            level,
            "{RequestMethod} {RouteTemplate} completed with {StatusCode} in {ElapsedMilliseconds:F1} ms. Query string present: {HasQueryString}. Failure: {Failure}. Correlation id: {CorrelationId}.",
            context.Request.Method,
            GlobalExceptionHandler.DescribeRouteTemplate(context),
            statusCode,
            Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds,
            context.Request.QueryString.HasValue,
            DescribeFailure(failure),
            ResolveCorrelationId(context));
    }

    /// <summary>Reads the status code the caller was actually answered with.</summary>
    /// <param name="context">The context of the request being recorded.</param>
    /// <returns>
    /// The status code the global exception handler published for this request, or the status code on the
    /// response when it published none.
    /// </returns>
    /// <remarks>
    /// The published value is preferred because it is a statement rather than an observation: it is the
    /// status the handler assigned after translating a failure, published under <see
    /// cref="GlobalExceptionHandler.AnsweredStatusItemKey"/> precisely because this stage had already
    /// unwound by the time that decision was made.
    /// </remarks>
    private static int ResolveAnsweredStatusCode(HttpContext context)
    {
        // ContainsKey before the indexer, for the reason given on ResolveCorrelationId.
        if (context.Items.ContainsKey(GlobalExceptionHandler.AnsweredStatusItemKey)
            && context.Items[GlobalExceptionHandler.AnsweredStatusItemKey] is int answered
            && answered >= StatusCodes.Status100Continue)
        {
            return answered;
        }

        return context.Response.StatusCode;
    }

    /// <summary>
    /// Names the failure that ended a request, using type names only.
    /// </summary>
    /// <param name="failure">
    /// The failure that escaped the remainder of the pipeline, or <see langword="null"/> when
    /// the request completed.
    /// </param>
    /// <returns>
    /// <see cref="NoFailureDescription"/> when the request completed; otherwise the full type name of
    /// each exception in the chain, outermost first, joined by an arrow and bounded by
    /// <see cref="MaximumDescribedChainDepth"/>.
    /// </returns>
    /// <remarks>
    /// <para>
    /// <b>No message, from any exception in the chain, ever.</b> That is the property this
    /// method exists to guarantee, and it is a property of what it READS rather than of what
    /// it filters: it touches only <see cref="System.Type.FullName"/>, so there is no path by
    /// which caller input, a credential or another tenant's data can reach the returned
    /// string. A method that read messages and then tried to redact them would be an
    /// allow-list problem that has to stay correct as exceptions nobody has seen reach it.
    /// </para>
    /// <para>
    /// <b>No stack trace either</b>, which is the one difference from the description the
    /// global exception handler logs. That handler runs for the same failure and records the
    /// trace once; repeating it here would double the size of every failed request's log
    /// footprint and add no fact. The type chain is kept because it is what tells an operator
    /// reading the request log WHICH failure a 500 was, without having to join to another
    /// entry.
    /// </para>
    /// <para>
    /// The chain is walked iteratively and bounded, so a deeply nested or self-referential
    /// chain cannot produce an unbounded entry. An aggregate failure is followed through its
    /// first inner exception like any other, which is the one that identifies the defect in
    /// practice.
    /// </para>
    /// </remarks>
    private static string DescribeFailureType(Exception? failure)
    {
        if (failure is null)
        {
            return NoFailureDescription;
        }

        StringBuilder description = new();
        Exception? current = failure;
        int depth = 0;

        while (current is not null && depth < MaximumDescribedChainDepth)
        {
            if (depth > 0)
            {
                description.Append(" ---> ");
            }

            // FullName is null only for a generic parameter type, which an exception cannot
            // be; the fallback keeps the description free of empty positions regardless.
            description.Append(current.GetType().FullName ?? current.GetType().Name);

            current = current.InnerException;
            depth++;
        }

        if (current is not null)
        {
            description.Append(" ---> (chain truncated)");
        }

        return description.ToString();
    }

    /// <summary>Chooses the severity at which the completion entry is written.</summary>
    /// <param name="statusCode">The status code on the response as this stage unwinds.</param>
    /// <param name="failure">
    /// The failure that escaped the remainder of the pipeline, or <see langword="null"/> when the request
    /// completed.
    /// </param>
    /// <param name="abandonedByCaller">
    /// Whether the failure was a cancellation raised because the caller stopped listening.
    /// </param>
    /// <param name="isHealthProbe">Whether the request addressed the health endpoint.</param>
    /// <returns>The severity for this request's entry.</returns>
    /// <remarks>
    /// Five outcomes, in the order they are tested. A request the caller abandoned is tested FIRST and is
    /// informational: nobody is left to answer, no work of ours failed, and there is nothing for an
    /// operator to act on - a browser navigating away mid-request would otherwise raise an error alert, and
    /// enough of them would drown the alerts that matter.
    /// </remarks>
    private static LogLevel SelectLevel(
        int statusCode,
        Exception? failure,
        bool abandonedByCaller,
        bool isHealthProbe)
    {
        if (abandonedByCaller)
        {
            return LogLevel.Information;
        }

        // Tested before the escaped-failure branch, so a client error that arrived as an exception is
        // recorded as the caller's mistake rather than as this application's fault. The upper bound is
        // explicit: a 5xx must not fall into this branch even though it is also >= 400.
        if (statusCode >= StatusCodes.Status400BadRequest
            && statusCode < StatusCodes.Status500InternalServerError)
        {
            return LogLevel.Warning;
        }

        if (failure is not null || statusCode >= StatusCodes.Status500InternalServerError)
        {
            return LogLevel.Error;
        }

        return isHealthProbe ? LogLevel.Debug : LogLevel.Information;
    }

    /// <summary>Describes an escaped failure in a form that is bounded and carries no message.</summary>
    /// <param name="failure">
    /// The failure that escaped the remainder of the pipeline, or <see langword="null"/> when the request
    /// completed.
    /// </param>
    /// <returns>
    /// <see cref="NoFailureDescription"/> when the request completed; otherwise the type names of the
    /// failure and of its inner failures, joined in order and truncated at <see
    /// cref="MaximumDescribedChainDepth"/>.
    /// </returns>
    /// <remarks>
    /// <b>An exception MESSAGE is caller-derived text and must never reach this log.</b> This entry is the
    /// highest-volume, longest-retained and most widely-readable record the application produces, and a
    /// message routinely carries the very things the rest of this stage is careful to exclude: a submitted
    /// value quoted back by a parser or a validator, a server name, a database name and sometimes a login
    /// from a data-provider fault, an internal file-system path from a loader, and - on the sign-in
    /// endpoint - a value taken from a credential body.
    /// </remarks>
    private static string DescribeFailure(Exception? failure)
    {
        if (failure is null)
        {
            return NoFailureDescription;
        }

        StringBuilder description = new();
        Exception? current = failure;
        int depth = 0;

        while (current is not null && depth < MaximumDescribedChainDepth)
        {
            if (depth > 0)
            {
                description.Append(ChainSeparator);
            }

            // FullName is null only for a generic parameter type, which an exception cannot be;
            // the fallback keeps the description from ever containing an empty position.
            description.Append(current.GetType().FullName ?? current.GetType().Name);

            current = current.InnerException;
            depth++;
        }

        if (current is not null)
        {
            description.Append(ChainTruncationMarker);
        }

        return description.ToString();
    }

    /// <summary>Reports whether the request addressed the health endpoint.</summary>
    /// <param name="context">The context of the request being processed.</param>
    /// <returns>
    /// <see langword="true"/> when the request path is <see cref="HealthPath"/> or a path segmented beneath
    /// it, compared without regard to case; otherwise <see langword="false"/>.
    /// </returns>
    /// <remarks>
    /// The comparison is ordinal so that it cannot vary with the server's culture, and case-insensitive
    /// because a URL path is matched that way by the routing that runs after this stage.
    /// </remarks>
    private static bool IsHealthProbe(HttpContext context)
    {
        return context.Request.Path.StartsWithSegments(HealthPath, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Reads the correlation identifier that <see cref="CorrelationIdMiddleware"/> published for this
    /// request.
    /// </summary>
    /// <param name="context">The context of the request being processed.</param>
    /// <returns>
    /// The published identifier, or <see cref="HttpContext.TraceIdentifier"/> when none was published.
    /// </returns>
    /// <remarks>
    /// The fallback costs nothing and removes a way for this entry to be useless: that stage also assigns
    /// the identifier to <see cref="HttpContext.TraceIdentifier"/>, so in the composed pipeline both
    /// sources hold the same value, and if this stage is ever exercised without it - in a test, or after a
    /// re-ordering - the entry still carries the framework's own request identifier instead of nothing at
    /// all.
    /// </remarks>
    private static string ResolveCorrelationId(HttpContext context)
    {
        // ContainsKey before the indexer: this dictionary is keyed by object and the indexer is only
        // guaranteed to tolerate an absent key on the framework's own implementation. Probing first is
        // correct against any implementation, and needs no output argument in the bargain.
        if (context.Items.ContainsKey(CorrelationIdMiddleware.ItemKey)
            && context.Items[CorrelationIdMiddleware.ItemKey] is string published
            && published.Length > 0)
        {
            return published;
        }

        return context.TraceIdentifier;
    }
}
