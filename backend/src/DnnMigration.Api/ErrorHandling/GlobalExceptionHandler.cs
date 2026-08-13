using System.Text;
using DnnMigration.Api.Middleware;
using DnnMigration.Domain.Abstractions.Services;
using DnnMigration.Domain.Common;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.Extensions.Primitives;

namespace DnnMigration.Api.ErrorHandling;

/// <summary>
/// Translates every exception that escapes the request pipeline into an RFC 7807 problem-details response.
/// It is the only type in the solution that does so.
/// </summary>
/// <remarks>
/// <para>
/// <b>Scope.</b> Unhandled exceptions only. Validation and model-state failures belong to
/// <c>Api/Filters/ValidationProblemDetailsFactory.cs</c> and do not reach this type on the normal path,
/// because model binding produces a 400 before anything is thrown.
/// </para>
/// <para>
/// <b>Diagnostics.</b> The exception is described rather than handed to the logger whole: the type name of
/// every link in the chain and its stack trace are recorded in full, because those identify the defect and
/// are authored by us or by a library, while the messages are dropped because a message may quote the input
/// that failed - a value that would not parse, a key that was not found, a composed cache key embedding a
/// user name - and which messages do cannot be established here.
/// </para>
/// </remarks>
public sealed class GlobalExceptionHandler : IExceptionHandler
{
    // The repository-root MIGRATION_NOTES.md records this divergence at repository level; this annotation
    // is its counterpart in code.

    /// <summary>Explanation returned when an operation was refused for want of permission.</summary>
    private const string ForbiddenDetail = "You do not have permission to perform this operation.";

    /// <summary>Explanation returned when the domain refused the request as invalid.</summary>
    /// <remarks>
    /// Authored here, fixed for every occurrence and every environment, and deliberately unrelated to
    /// whatever the domain said. It names no field, no value and no rule, because a broken invariant is not
    /// a field-level validation report: the request reached a place where per-field checking had already
    /// passed, so there is no field to name and inventing one would mislead.
    /// </remarks>
    private const string InvalidRequestDetail =
        "The request was rejected because one or more of its values are not valid. Quote the "
        + CorrelationIdMiddleware.HeaderName
        + " response header when reporting this problem.";

    /// <summary>Explanation returned for any exception without a more specific mapping.</summary>
    private const string UnexpectedFailureDetail =
        "An unexpected error occurred while processing the request. Quote the "
        + CorrelationIdMiddleware.HeaderName
        + " response header when reporting this problem.";

    /// <summary>Explanation returned when the submitted body exceeded the configured request-size ceiling.</summary>
    private const string PayloadTooLargeDetail =
        "The submitted request body is larger than this endpoint accepts. Submit a smaller body.";

    /// <summary>Explanation returned when the server could not read the request at the transport level.</summary>
    /// <remarks>
    /// Covers a malformed chunked body, a declared length that disagrees with what arrived, and the other
    /// framing faults the host rejects before any model binding happens. The host's own message is not
    /// published, for the reason set out on <see cref="UnexpectedFailureDetail"/>.
    /// </remarks>
    private const string MalformedRequestDetail =
        "The request could not be read. Check the request framing and headers, then submit it again.";

    /// <summary>
    /// Explanation returned when the store refused a write because the value it carried is already held by
    /// another record.
    /// </summary>
    /// <remarks>
    /// AND A SAFETY NET RATHER THAN THE ANSWER. Every create path that writes through a unique constraint
    /// catches the duplicate-key signal itself and returns the SAME reason code its own sequential
    /// pre-check emits, so a caller receives a 409 naming the field that collided - the role name, the
    /// alias, the account name, the profile property - and never reaches this text.
    /// </remarks>
    private const string DuplicateRecordDetail =
        "The submitted values conflict with a record that already exists. "
        + "Reload the resource and submit different values.";

    /// <summary>
    /// Explanation returned when the store refused a write because the record had already been changed by
    /// another caller.
    /// </summary>
    /// <remarks>
    /// A SAFETY NET, exactly like the duplicate arm above. Every whole-record write path compares the token
    /// the caller round-tripped and answers with its own reason code, naming the resource and telling the
    /// caller to reload it, so a caller normally never reaches this text.
    /// </remarks>
    private const string ConcurrentWriteDetail =
        "The record was changed by someone else after you read it, so nothing was written. "
        + "Reload the resource and apply your change again.";

    /// <summary>
    /// Explanation returned when a dependency this request needed could not be reached or could not serve.
    /// </summary>
    private const string StoreUnavailableDetail =
        "A service this request depends on is temporarily unavailable. Retry after a short delay, "
        + "and quote the "
        + CorrelationIdMiddleware.HeaderName
        + " response header if the problem persists.";

    /// <summary>Value of the <c>Retry-After</c> header sent with a 503, in seconds.</summary>
    private const string RetryAfterSeconds = "5";

    /// <summary>
    /// Text recorded in place of a route template when the failure happened before, or outside, endpoint
    /// selection.
    /// </summary>
    private const string UnmatchedRouteTemplate = "(no matched endpoint)";

    /// <summary>
    /// Key under which this type publishes the status code it answered a failed request with, so a stage
    /// that unwound before the outcome was known can still record it.
    /// </summary>
    /// <remarks>
    /// The prefix matches <see cref="CorrelationIdMiddleware.ItemKey"/> so that everything this application
    /// publishes on the request's item dictionary is recognisable as its own and cannot collide with a
    /// framework or library key.
    /// </remarks>
    internal const string AnsweredStatusItemKey = "DnnMigration.AnsweredStatusCode";

    /// <summary>Greatest number of links of an inner-exception chain that are described.</summary>
    private const int MaximumDescribedChainDepth = 8;

    /// <summary>Identifies the entry recorded when a caller abandoned the request.</summary>
    private static readonly EventId ClientDisconnectedEvent = new(1001, "ClientDisconnected");

    /// <summary>Identifies the entry recorded when the response had already started.</summary>
    private static readonly EventId ResponseAlreadyStartedEvent = new(1002, "ResponseAlreadyStarted");

    /// <summary>Identifies the entry recorded for a failure answered with a 5xx status code.</summary>
    private static readonly EventId UnhandledServerFaultEvent = new(1003, "UnhandledServerFault");

    /// <summary>Identifies the entry recorded for a failure answered with a 4xx status code.</summary>
    private static readonly EventId RequestRefusedEvent = new(1004, "RequestRefused");

    private readonly IProblemDetailsService _problemDetailsService;
    private readonly ProblemDetailsFactory _problemDetailsFactory;
    private readonly IStoreFailureClassifier _storeFailures;
    private readonly ILogger<GlobalExceptionHandler> _logger;

    /// <summary>Initialises a new instance of the <see cref="GlobalExceptionHandler"/> class.</summary>
    /// <param name="problemDetailsService">
    /// The framework service, registered by <c>AddProblemDetails</c>, that writes the payload.
    /// </param>
    /// <param name="problemDetailsFactory">
    /// The factory that populates a payload's shared members - the problem-type link, the status-code title
    /// and the trace identifier - so this type's output cannot drift from every other problem response the
    /// application produces.
    /// </param>
    /// <param name="storeFailures">
    /// The classifier that reports whether a failure means the backing store was unreachable rather than
    /// that this application has a defect.
    /// </param>
    /// <param name="logger">The logger that receives the diagnostic description of the failure.</param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="problemDetailsService"/>, <paramref name="problemDetailsFactory"/>, <paramref
    /// name="storeFailures"/> or <paramref name="logger"/> is <see langword="null"/>.
    /// </exception>
    public GlobalExceptionHandler(
        IProblemDetailsService problemDetailsService,
        ProblemDetailsFactory problemDetailsFactory,
        IStoreFailureClassifier storeFailures,
        ILogger<GlobalExceptionHandler> logger)
    {
        ArgumentNullException.ThrowIfNull(problemDetailsService);
        ArgumentNullException.ThrowIfNull(problemDetailsFactory);
        ArgumentNullException.ThrowIfNull(storeFailures);
        ArgumentNullException.ThrowIfNull(logger);

        _problemDetailsService = problemDetailsService;
        _problemDetailsFactory = problemDetailsFactory;
        _storeFailures = storeFailures;
        _logger = logger;
    }

    /// <summary>
    /// Records <paramref name="exception"/> and, where a response can still be produced, answers the
    /// request with an RFC 7807 problem-details payload.
    /// </summary>
    /// <param name="httpContext">The context of the request that failed.</param>
    /// <param name="exception">The exception that escaped the pipeline.</param>
    /// <param name="cancellationToken">
    /// The request's own abort signal, supplied by the framework, observed when deciding whether the caller
    /// is still there to receive a response.
    /// </param>
    /// <returns>
    /// <see langword="true"/> when this handler has fully written the response, which tells the framework
    /// to stop; <see langword="false"/> when it has not, which asks the framework to fall through to its
    /// own default handling.
    /// </returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="httpContext"/> or <paramref name="exception"/> is <see langword="null"/>.
    /// </exception>
    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        ArgumentNullException.ThrowIfNull(exception);

        string correlationId = ResolveCorrelationId(httpContext);

        string routeTemplate = DescribeRouteTemplate(httpContext);
        string failure = DescribeForDiagnostics(exception);

        // A cancellation raised because the caller went away is not a fault of this application, and
        // recording it as an error would bury the failures that are.
        bool callerHasGoneAway =
            cancellationToken.IsCancellationRequested || httpContext.RequestAborted.IsCancellationRequested;

        if (exception is OperationCanceledException && callerHasGoneAway)
        {
            _logger.LogInformation(
                ClientDisconnectedEvent,
                "Request {RequestMethod} {RouteTemplate} was abandoned because the client disconnected; no response was written. Failure {Failure}. Correlation identifier {CorrelationId}.",
                httpContext.Request.Method,
                routeTemplate,
                failure,
                correlationId);

            return false;
        }

        // Tested before anything is assigned. Once the status line and headers are flushed the status code
        // cannot be changed, and a payload appended to what was already sent would corrupt it, so the only
        // correct action is to record the failure and decline.
        if (httpContext.Response.HasStarted)
        {
            _logger.LogError(
                ResponseAlreadyStartedEvent,
                "Unhandled exception on {RequestMethod} {RouteTemplate} after the response had already started; the status code and body cannot be replaced, so no problem-details payload was written. Failure {Failure}. Correlation identifier {CorrelationId}.",
                httpContext.Request.Method,
                routeTemplate,
                failure,
                correlationId);

            return false;
        }

        (int statusCode, string detail) = Describe(exception, _storeFailures);

        // Every entry this type writes carries the same allowlisted shape - event identifier, request
        // method, route template, the redacted failure description, the status code where one was chosen,
        // and the correlation identifier. The list is closed: a value not named here is not recorded.
        if (statusCode >= StatusCodes.Status500InternalServerError)
        {
            _logger.LogError(
                UnhandledServerFaultEvent,
                "Unhandled exception on {RequestMethod} {RouteTemplate}; responding {StatusCode} with a problem-details payload. Failure {Failure}. Correlation identifier {CorrelationId}.",
                httpContext.Request.Method,
                routeTemplate,
                statusCode,
                failure,
                correlationId);
        }
        else
        {
            _logger.LogWarning(
                RequestRefusedEvent,
                "Request {RequestMethod} {RouteTemplate} was refused; responding {StatusCode} with a problem-details payload. Failure {Failure}. Correlation identifier {CorrelationId}.",
                httpContext.Request.Method,
                routeTemplate,
                statusCode,
                failure,
                correlationId);
        }

        // Assigned before the payload is written: the status carried inside the body is a
        // copy for the reader's benefit and does not set the status line.
        httpContext.Response.StatusCode = statusCode;

        if (statusCode == StatusCodes.Status503ServiceUnavailable)
        {
            httpContext.Response.Headers.RetryAfter = RetryAfterSeconds;
        }

        // Published for the request-logging stage, which unwound before this decision was made and would
        // otherwise record the status the response carried BEFORE the failure - which for a fault raised
        // inside a controller action is 200, for a request answered 500.
        httpContext.Items[AnsweredStatusItemKey] = statusCode;

        // The dedicated pipeline stage normally supplies this header from a response-starting callback, and
        // assigning the same value through the indexer is idempotent.
        httpContext.Response.Headers[CorrelationIdMiddleware.HeaderName] = correlationId;

        ProblemDetails problemDetails = _problemDetailsFactory.CreateProblemDetails(
            httpContext,
            statusCode,
            title: null,
            type: null,
            detail: detail,
            instance: null);

        return await _problemDetailsService.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            Exception = exception,
            ProblemDetails = problemDetails,
        });
    }

    /// <summary>
    /// Maps an exception to the status code that describes it and the explanation returned to the caller.
    /// </summary>
    /// <param name="exception">The exception being translated.</param>
    /// <param name="storeFailures">
    /// The classifier consulted after the table below has been exhausted, to tell a store outage apart from
    /// a defect.
    /// </param>
    /// <returns>The status code to report and the <c>detail</c> text to publish.</returns>
    /// <remarks>
    /// Ordered from the most specific case to the least, and deliberately short. A longer table would mean
    /// this type had begun deciding outcomes that belong to the application layer, where an expected
    /// failure is already expressed as a failed <c>Result</c>.
    /// </remarks>
    private static (int StatusCode, string Detail) Describe(
        Exception exception,
        IStoreFailureClassifier storeFailures) => exception switch
        {
            // A broken invariant means the request asked the model to enter a state it must never occupy,
            // so the request is what is at fault and 400 is what the caller needs to hear.
            DomainException domainException => (
                StatusCodes.Status400BadRequest,
                string.IsNullOrWhiteSpace(domainException.PublicDetail)
                    ? InvalidRequestDetail
                    : domainException.PublicDetail),

            // Authentication is settled long before this point, because an unauthenticated caller is
            // challenged by the authentication handler and never arrives here. This is a caller who is
            // known and still not entitled, so 403 rather than 401.
            UnauthorizedAccessException => (StatusCodes.Status403Forbidden, ForbiddenDetail),

            // A UNIQUE CONSTRAINT REFUSING A WRITE IS THE CALLER'S CONFLICT, NOT A SERVER FAULT. The store
            // told us the value is already held; it did exactly its job and kept exactly one record, so 409
            // is what the caller needs to hear and 500 - what this family was answered before was wrong on
            // both counts, misreporting a correct store and raising a server-fault log entry for an
            // ordinary collision.
            DuplicateKeyException => (StatusCodes.Status409Conflict, DuplicateRecordDetail),

            // A LOST UPDATE IS THE CALLER'S CONFLICT TOO, AND FOR THE SAME REASONS. The store refused a
            // write because the row had already moved: it did exactly its job and it preserved somebody's
            // committed edit, so 409 with an instruction to reload is what the caller needs to hear, and
            // the 500 this family was answered before was wrong on both counts - it reported a defect that
            // does not exist and it raised a server-fault log entry for an ordinary collision.
            ConcurrencyConflictException => (StatusCodes.Status409Conflict, ConcurrentWriteDetail),

            BadHttpRequestException badRequest => (
                badRequest.StatusCode,
                badRequest.StatusCode == StatusCodes.Status413PayloadTooLarge
                    ? PayloadTooLargeDetail
                    : MalformedRequestDetail),

            CacheProductionTimeoutException => (
                StatusCodes.Status500InternalServerError,
                UnexpectedFailureDetail),

            Exception when storeFailures.IsStoreUnavailable(exception) => (
                StatusCodes.Status503ServiceUnavailable,
                StoreUnavailableDetail),

            // The general case publishes fixed text and never the exception's own message, in every
            // environment. That is not conservatism about detail.
            _ => (StatusCodes.Status500InternalServerError, UnexpectedFailureDetail),
        };

    /// <summary>Resolves the correlation identifier for the failed request.</summary>
    /// <param name="httpContext">The context of the request that failed.</param>
    /// <returns>
    /// The identifier to log and to echo on the response; never <see langword="null"/> and never empty.
    /// </returns>
    /// <remarks>
    /// The dedicated pipeline stage publishes the identifier to <see cref="HttpContext.Items"/> before the
    /// rest of the pipeline runs, and that stage runs inside this handler, so the item is the authoritative
    /// source for any request that reached it.
    /// </remarks>
    private static string ResolveCorrelationId(HttpContext httpContext)
    {
        if (httpContext.Items.TryGetValue(CorrelationIdMiddleware.ItemKey, out object? published)
            && published is string publishedId
            && !string.IsNullOrWhiteSpace(publishedId))
        {
            return publishedId;
        }

        StringValues echoed = httpContext.Response.Headers[CorrelationIdMiddleware.HeaderName];

        // One header line is the only shape accepted, matching the pipeline stage's own rule,
        // so a value that somehow arrived repeated is not joined and republished.
        string? alreadyOnResponse = echoed.Count == 1 ? echoed[0] : null;

        if (!string.IsNullOrWhiteSpace(alreadyOnResponse))
        {
            return alreadyOnResponse;
        }

        return httpContext.TraceIdentifier;
    }

    /// <summary>Describes the endpoint a request reached by its declared route template.</summary>
    /// <param name="httpContext">The context of the request being described.</param>
    /// <returns>
    /// The raw route pattern of the selected endpoint, or <see cref="UnmatchedRouteTemplate"/> when no
    /// endpoint with a pattern was selected.
    /// </returns>
    /// <remarks>
    /// Only <see cref="RouteEndpoint"/> carries a pattern. An endpoint of any other kind, or none at all -
    /// the case when the failure was raised by a stage that runs before endpoint selection, or when nothing
    /// matched - yields the fixed substitute rather than falling back to the path, because falling back to
    /// the path would reintroduce exactly the value this method exists to keep out of the log.
    /// </remarks>
    internal static string DescribeRouteTemplate(HttpContext httpContext)
    {
        string? template = (httpContext.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText;

        return string.IsNullOrWhiteSpace(template) ? UnmatchedRouteTemplate : template;
    }

    /// <summary>Describes a failure for the log without admitting any exception message.</summary>
    /// <param name="exception">The failure to describe.</param>
    /// <returns>
    /// The type name of each exception in the chain, each followed by its stack trace where one is present,
    /// outermost first.
    /// </returns>
    /// <remarks>
    /// A message is not always authored text. Framework and library messages routinely quote the input that
    /// failed - a value that would not parse, a key that was not found, a connection string a provider
    /// rejected - and application messages do too, as <c>Domain/ValueObjects/PortalGuid.cs:L233</c> does.
    /// </remarks>
    internal static string DescribeForDiagnostics(Exception exception)
    {
        StringBuilder description = new();
        Exception? current = exception;
        int depth = 0;

        while (current is not null && depth < MaximumDescribedChainDepth)
        {
            if (depth > 0)
            {
                description.Append(" ---> ");
            }

            // FullName is null only for a generic parameter type, which an exception cannot be;
            // the fallback is there so the description never contains an empty position.
            description.Append(current.GetType().FullName ?? current.GetType().Name);

            string? stack = current.StackTrace;

            if (!string.IsNullOrWhiteSpace(stack))
            {
                description.Append(' ').Append(stack);
            }

            current = current.InnerException;
            depth++;
        }

        if (current is not null)
        {
            description.Append(" ---> (chain truncated)");
        }

        return description.ToString();
    }
}
