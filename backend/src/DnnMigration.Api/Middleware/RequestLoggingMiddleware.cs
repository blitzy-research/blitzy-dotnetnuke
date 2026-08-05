using System.Diagnostics;
using System.Text;
using DnnMigration.Api.ErrorHandling;

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
/// response body, no header, no query-string value, no account name, no address - and
/// <b>no exception object and no exception message</b>. A request log is the
/// highest-volume, longest-retained and most widely-readable log an application
/// produces, so anything personal placed in it is copied everywhere and kept the
/// longest.
/// </para>
/// <para>
/// MIGRATION: THE ESCAPED FAILURE IS REPORTED BY TYPE NAME, NOT BY EXCEPTION OBJECT.
/// An earlier revision of this stage passed the caught <see cref="Exception"/> to the
/// logger, which had Serilog render its message, every inner message and its stack
/// into this entry. That defeated the redaction
/// <c>ErrorHandling/GlobalExceptionHandler.cs</c> performs on purpose - that type
/// describes a failure by type name and stack with messages stripped at every depth -
/// and it described the same failure twice, once talkatively here and once safely
/// there. An exception message is caller-derived text: a parser or validator quotes
/// the submitted value back, a data-provider fault names the server, the database and
/// sometimes the login, a loader names an internal path, and on the sign-in endpoint
/// the value in question came out of a credential body. This stage therefore records
/// only the bounded, message-free type chain produced by
/// <see cref="DescribeFailure(Exception?)"/>, and detailed failure diagnostics remain
/// the global handler's sole responsibility.
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
/// stage before that handler sees it, which is why the failure is re-thrown unchanged
/// and why the entry is written from a response-completion callback rather than as this
/// stage unwinds - at the moment of unwinding the outcome is not yet known.
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

    /// <summary>
    /// Value recorded for the failure property of a request that completed.
    /// </summary>
    /// <remarks>
    /// A fixed word rather than an absent or null property, so the entry has the same shape
    /// whatever the outcome and a log query can group on this property without having to
    /// treat a missing value as a third case.
    /// </remarks>
    private const string NoFailureDescription = "none";

    /// <summary>
    /// Separator placed between the type names of a failure and its inner failure.
    /// </summary>
    /// <remarks>
    /// The same arrow the framework itself uses when it renders a chain, so an operator reading
    /// this entry beside a framework-rendered one is reading the same notation.
    /// </remarks>
    private const string ChainSeparator = " ---> ";

    /// <summary>Marker appended when a failure chain is longer than the walk allows.</summary>
    private const string ChainTruncationMarker = " ---> (chain truncated)";

    /// <summary>
    /// The number of failures described before the walk stops.
    /// </summary>
    /// <remarks>
    /// Matches the bound <c>ErrorHandling/GlobalExceptionHandler.cs</c> applies to its own
    /// walk, so the two records of one failure agree on how much of the chain they name. The
    /// bound exists because a chain can be arbitrarily deep, and in the pathological case
    /// self-referential; without it one failure could produce an entry of unbounded size.
    /// </remarks>
    private const int MaximumDescribedChainDepth = 5;

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
    /// instance. This runs on every request, so its own cost is the one cost paid
    /// unconditionally. The elapsed time is deliberately not read from a clock: a wall clock
    /// can be adjusted mid-request and would then report a negative or wildly inflated
    /// duration.
    /// </para>
    /// <para>
    /// Exactly one entry is written, on the success path and on the failure path alike, and a
    /// failure is attached to that same entry rather than logged as a second one, so the count
    /// of these entries is the count of requests handled and can be read as such.
    /// </para>
    /// <para>
    /// <b>The entry is written when the response completes, not when this stage unwinds.</b>
    /// That is the whole reason this method registers a callback instead of using a
    /// <c>finally</c> block. This stage runs inside the global exception handler, so a failure
    /// raised downstream passes through here on its way out and the handler that translates it
    /// into a status code has not run yet: an entry written on the way out records whatever the
    /// response happened to carry beforehand, which for a fault raised inside a controller
    /// action is 200 - for a request the caller was answered 500. Recording a fault as a
    /// success is worse than not recording it, because it is believed. The callback costs one
    /// closure and one registration per request, and that is what the correct status code
    /// costs; the alternative is an entry that cannot be trusted.
    /// </para>
    /// <para>
    /// <b>The failure is reported as a bounded type name and never as an exception
    /// object.</b> See <see cref="DescribeFailure(Exception?)"/> for why, and for what the
    /// alternative would have disclosed.
    /// </para>
    /// </remarks>
    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        long startedAt = Stopwatch.GetTimestamp();

        // Assigned by the catch below and read by the callback registered immediately after.
        // The callback closes over the VARIABLE rather than its value, and it runs once the
        // response is complete - after the catch has run - so the failure it reads is the one
        // that escaped, not the null this starts as.
        Exception? failure = null;

        // Registered before the continuation is invoked, so it is in place however the request
        // ends: answered, refused, faulted, or abandoned by a caller who went away. The server
        // fires completion callbacks on every one of those paths.
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

            // Bare re-throw: preserves the original stack trace, and lets the failure
            // reach the global exception handler untouched. Swallowing it here would
            // suppress the error response the caller is owed.
            throw;
        }
    }

    /// <summary>
    /// Writes the single completion entry for a request whose response has finished.
    /// </summary>
    /// <param name="context">The context of the request being recorded.</param>
    /// <param name="startedAt">The timestamp taken as the request entered this stage.</param>
    /// <param name="failure">
    /// The failure that escaped the remainder of the pipeline, or <see langword="null"/> when
    /// the request completed without one.
    /// </param>
    /// <remarks>
    /// <para>
    /// Nothing here can throw on a value it was given: the status code is an integer, the
    /// duration is arithmetic on two timestamps, and every text value is either authored here or
    /// produced by a bounded describer. That matters more than it looks, because this runs from
    /// a server completion callback, where an exception is reported against the connection
    /// rather than the request and helps nobody.
    /// </para>
    /// <para>
    /// The level is tested before the entry is composed, not to change what gets recorded - a
    /// disabled level is discarded by the logger either way - but to avoid paying for it.
    /// Composing the entry allocates an argument array, boxes the status code, the duration and
    /// the flag, and walks the failure chain; a health probe arrives every few seconds for the
    /// lifetime of the deployment and its entry is below the configured floor, so this is the
    /// one request in the application whose entry would routinely be built only to be thrown
    /// away. The entry is still DEFINED for that path and appears the moment an operator lowers
    /// the floor to diagnose one, which is the difference between logging it quietly and
    /// excluding it from the pipeline as a special case.
    /// </para>
    /// </remarks>
    private void RecordCompletion(HttpContext context, long startedAt, Exception? failure)
    {
        // A cancellation raised because the caller went away is not a fault of this
        // application, and recording it as an error would bury the failures that are. The type
        // alone cannot establish that - an internal timeout raises the same type and IS a
        // genuine server-side failure - so the request's own abort signal is what distinguishes
        // them. This is the same test, on the same two facts, that the global exception handler
        // applies before it declines to answer a caller who is no longer listening.
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

    /// <summary>
    /// Reads the status code the caller was actually answered with.
    /// </summary>
    /// <param name="context">The context of the request being recorded.</param>
    /// <returns>
    /// The status code the global exception handler published for this request, or the status
    /// code on the response when it published none.
    /// </returns>
    /// <remarks>
    /// <para>
    /// The published value is preferred because it is a statement rather than an observation:
    /// it is the status the handler assigned after translating a failure, published under
    /// <see cref="GlobalExceptionHandler.AnsweredStatusItemKey"/> precisely because this stage
    /// had already unwound by the time that decision was made. An absent value is the normal
    /// case - no failure was translated - and the response's own status code is then the
    /// answer.
    /// </para>
    /// <para>
    /// Because the entry is written from a completion callback the two agree in practice, which
    /// is deliberate: neither source is relied upon alone, so a change to the order in which
    /// the pipeline is composed cannot silently turn a recorded fault back into a recorded
    /// success.
    /// </para>
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

    /// <summary>
    /// Chooses the severity at which the completion entry is written.
    /// </summary>
    /// <param name="statusCode">The status code on the response as this stage unwinds.</param>
    /// <param name="failure">
    /// The failure that escaped the remainder of the pipeline, or <see langword="null"/> when
    /// the request completed. Only its TYPE is read here, because severity is the only decision
    /// made from it; nothing derived from its message reaches the entry, which is what keeps a
    /// credential, a caller's input and a server's identity out of the log.
    /// </param>
    /// <param name="abandonedByCaller">
    /// Whether the failure was a cancellation raised because the caller stopped listening.
    /// </param>
    /// <param name="isHealthProbe">Whether the request addressed the health endpoint.</param>
    /// <returns>The severity for this request's entry.</returns>
    /// <remarks>
    /// <para>
    /// Five outcomes, in the order they are tested. A request the caller abandoned is tested
    /// FIRST and is informational: nobody is left to answer, no work of ours failed, and there
    /// is nothing for an operator to act on - a browser navigating away mid-request would
    /// otherwise raise an error alert, and enough of them would drown the alerts that matter.
    /// An escaped failure or a server-error status is this application's own fault and an
    /// operator has to see it, so it is an error. A client-error status is the caller's fault:
    /// worth noticing, not worth alarming anybody, so it is a warning - and it is deliberately
    /// not promoted to error, or the error stream would fill with ordinary validation failures
    /// until the genuine faults in it were invisible. A <em>successful</em> health probe is
    /// written at debug, below the configured floor: the container probes this endpoint every
    /// few seconds for the lifetime of the deployment, and at informational level those probes
    /// alone would outnumber every other entry in the log. Everything else is routine and
    /// informational.
    /// </para>
    /// <para>
    /// Abandonment is narrower than it sounds and cannot be used to hide a fault: the caller's
    /// own abort signal must be raised as well as the cancellation being observed, so an
    /// internal timeout - which raises the same exception type while the caller is still
    /// waiting - remains an error, and a failure of any other type remains an error whatever
    /// the connection is doing.
    /// </para>
    /// <para>
    /// The health downgrade is tested last on purpose, so it can only ever quieten a
    /// probe that succeeded. A probe answering 503 is the single most important entry this
    /// application can write - it is the reason a deployment is not coming up - and it
    /// stays an error.
    /// </para>
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
    /// Describes an escaped failure in a form that is bounded and carries no message.
    /// </summary>
    /// <param name="failure">
    /// The failure that escaped the remainder of the pipeline, or <see langword="null"/> when
    /// the request completed.
    /// </param>
    /// <returns>
    /// <see cref="NoFailureDescription"/> when the request completed; otherwise the type names
    /// of the failure and of its inner failures, joined in order and truncated at
    /// <see cref="MaximumDescribedChainDepth"/>.
    /// </returns>
    /// <remarks>
    /// <para>
    /// <b>An exception MESSAGE is caller-derived text and must never reach this log.</b> This
    /// entry is the highest-volume, longest-retained and most widely-readable record the
    /// application produces, and a message routinely carries the very things the rest of this
    /// stage is careful to exclude: a submitted value quoted back by a parser or a validator,
    /// a server name, a database name and sometimes a login from a data-provider fault, an
    /// internal file-system path from a loader, and - on the sign-in endpoint - a value taken
    /// from a credential body. Attaching the exception object would have Serilog render the
    /// message, every inner message and the stack, so it would defeat the redaction that
    /// <c>ErrorHandling/GlobalExceptionHandler.cs</c> performs deliberately: that type
    /// describes a failure by TYPE NAME and stack only, with messages stripped at every depth
    /// of the chain, and it is the only place a failure is described in detail.
    /// </para>
    /// <para>
    /// A type name is safe because it is authored by whoever wrote the type and can carry
    /// nothing the caller supplied. It is also the fact an operator actually needs from THIS
    /// entry: which request failed, how it ended, how long it took and what kind of fault
    /// escaped. The correlation identifier on the same entry joins it to the global handler's
    /// record, so nothing diagnostic is lost by declining to repeat it here - and the failure
    /// is now described exactly once per request rather than twice.
    /// </para>
    /// <para>
    /// The chain is walked iteratively and bounded, for the same reason the global handler
    /// bounds its own walk: a deeply nested or self-referential chain would otherwise let one
    /// failure produce an unbounded entry. An <see cref="AggregateException"/> is followed
    /// through <see cref="Exception.InnerException"/> like any other, which reports its first
    /// inner failure.
    /// </para>
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

    /// <summary>
    /// Reports whether the request addressed the health endpoint.
    /// </summary>
    /// <param name="context">The context of the request being processed.</param>
    /// <returns>
    /// <see langword="true"/> when the request path is <see cref="HealthPath"/> or a path
    /// segmented beneath it, compared without regard to case; otherwise
    /// <see langword="false"/>.
    /// </returns>
    /// <remarks>
    /// <para>
    /// The comparison is ordinal so that it cannot vary with the server's culture, and
    /// case-insensitive because a URL path is matched that way by the routing that runs
    /// after this stage.
    /// </para>
    /// <para>
    /// It is a SEGMENT-boundary prefix test rather than an equality test, because the health
    /// views are mapped as a family: the container probes the liveness path itself, and the
    /// readiness view sits one segment beneath it. Both are polled on a schedule, so both need
    /// the quieter level, and matching on the segment boundary is what admits the family
    /// without admitting an unrelated path that merely begins with the same characters - a
    /// request to <c>/healthcheck</c> is a different request and keeps the ordinary level.
    /// </para>
    /// </remarks>
    private static bool IsHealthProbe(HttpContext context)
    {
        return context.Request.Path.StartsWithSegments(HealthPath, StringComparison.OrdinalIgnoreCase);
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
