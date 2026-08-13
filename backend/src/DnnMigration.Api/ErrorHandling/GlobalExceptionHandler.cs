using System.Text;
using DnnMigration.Api.Middleware;
using DnnMigration.Application.Dtos.Common;
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

/// <summary>Translates an application-layer outcome into an HTTP response.</summary>
/// <remarks>
/// <strong>The mapping keys on the reason token, not on the whole code.</strong> Failure codes in this
/// solution are dotted paths whose final segment names the reason - and the segment separator inside that
/// last token is not consistent, because different services were written to use underscores and hyphens.
/// </remarks>
public static class ApiResults
{
    /// <summary>The reason tokens that mean the addressed thing does not exist.</summary>
    private static readonly string[] NotFoundTokens =
    {
        "not_found", "notfound", "source_missing", "unknown_profile_property", "unknown_property",
    };

    /// <summary>The reason tokens that mean the request conflicts with the current state.</summary>
    /// <remarks>
    /// <c>last_remaining</c> is here for exactly that reason, and its absence was a defect rather than a
    /// choice. <c>portal.last_remaining</c> refuses the removal of the only portal an installation has
    /// left; it matched no table and therefore fell to the 400 default, while the endpoint's own published
    /// description declared the refusal as a 409 and argued the case in the same terms this table does.
    /// </remarks>
    private static readonly string[] ConflictTokens =
    {
        "duplicate", "already_exists", "already_registered", "already_required", "unchanged",
        "not_different", "conflict", "in_use", "last_remaining", "superseded",
    };

    /// <summary>The reason tokens that mean the caller is not permitted to do this.</summary>
    private static readonly string[] ForbiddenTokens =
    {
        "forbidden", "protected",
    };

    /// <summary>The reason tokens that mean a dependency could not be reached.</summary>
    private static readonly string[] UnavailableTokens =
    {
        "provider_error", "store_unavailable",
    };

    /// <summary>
    /// Published in place of a failed outcome's own message when that message is absent, or when it does
    /// not have the shape of an authored explanation.
    /// </summary>
    /// <remarks>
    /// Deliberately identical in spirit to the unhandled-exception text: it says that the request did not
    /// complete and points at the one diagnostic handle a caller holds. It names no cause, because in the
    /// case that produces it the cause is precisely what must not be published.
    /// </remarks>
    private const string UnauthoredDetail =
        "The request could not be completed. Quote the "
        + CorrelationIdMiddleware.HeaderName
        + " response header when reporting this problem.";

    /// <summary>Longest detail this edge will publish from a failed outcome's own message.</summary>
    private const int MaximumPublishedDetailLength = 512;

    /// <summary>The whole codes that mean the caller has not proved who they are.</summary>
    /// <remarks>
    /// The three approval outcomes sit here alongside the lock-out outcome, and for the same reason: each
    /// describes an account state that refuses a sign-in the credential itself did not refuse, so the
    /// request failed to authenticate rather than being malformed.
    /// </remarks>
    private static readonly string[] UnauthorizedCodes =
    {
        "auth.invalid_credentials", "auth.locked_out", "auth.invalid_refresh_token",
        "auth.insecure_admin_password", "auth.insecure_host_password",
        "auth.verification_required", "auth.verification_code_invalid",
        "auth.account_not_approved",
    };

    /// <summary>The reason tokens that mean this application, not the caller, is at fault.</summary>
    /// <remarks>
    /// Every entry names an operation that had already passed validation and authorisation when it failed,
    /// so nothing the caller could change would make the identical request succeed.
    /// </remarks>
    internal static readonly string[] InternalFailureTokens =
    {
        "creation_failed", "create_failed", "export_failed", "import_failed",
        "capability_probe_failed", "upgrade_failed", "internal_error",
    };

    /// <summary>The failure code reported when a read succeeded but the thing addressed does not exist.</summary>
    /// <remarks>
    /// Stated as a constant so the problem type a client branches on is identical whichever endpoint
    /// produced it, and so that the token it carries is classified by <see cref="NotFoundTokens"/> rather
    /// than by a status code written out at the call site.
    /// </remarks>
    private const string ResourceNotFoundCode = "resource.not_found";

    /// <summary>The detail reported with a 404 produced from a successful outcome that carried no value.</summary>
    private const string ResourceNotFoundDetail = "The requested resource does not exist.";

    /// <summary>Translates an outcome that carries no value.</summary>
    /// <param name="controller">The controller producing the response.</param>
    /// <param name="result">The outcome to translate.</param>
    /// <returns>
    /// <c>204 No Content</c> on success, because the operation completed and there is nothing to return;
    /// otherwise a problem-details payload with the mapped status.
    /// </returns>
    /// <exception cref="ArgumentNullException">Either argument is <see langword="null"/>.</exception>
    public static ActionResult Complete(this ControllerBase controller, Result result)
    {
        ArgumentNullException.ThrowIfNull(controller);
        ArgumentNullException.ThrowIfNull(result);

        return result.IsSuccess ? controller.NoContent() : controller.Problem(result);
    }

    /// <summary>Translates an outcome that carries a value into the shared success envelope.</summary>
    /// <typeparam name="TValue">The value type.</typeparam>
    /// <param name="controller">The controller producing the response.</param>
    /// <param name="result">The outcome to translate.</param>
    /// <returns>
    /// <c>200 OK</c> carrying an <see cref="ApiResponse{T}"/> around the value on success; <c>404 Not
    /// Found</c> carrying a problem-details payload when the outcome succeeded but carries no value,
    /// because a nullable value on a successful outcome is how this solution expresses "asked, and it is
    /// not there"; otherwise a problem-details payload with the mapped status.
    /// </returns>
    /// <exception cref="ArgumentNullException">Either argument is <see langword="null"/>.</exception>
    public static ActionResult<ApiResponse<TValue>> Complete<TValue>(
        this ControllerBase controller,
        Result<TValue> result)
    {
        ArgumentNullException.ThrowIfNull(controller);
        ArgumentNullException.ThrowIfNull(result);

        if (result.IsFailure)
        {
            return controller.Problem(result);
        }

        TValue value = result.Value;

        return value is null
            ? controller.NotFoundProblem()
            : controller.Ok(ApiResponse<TValue>.Success(value));
    }

    /// <summary>Translates an outcome that carries a domain page into the shared paging envelope.</summary>
    /// <typeparam name="TItem">The element type of the page.</typeparam>
    /// <param name="controller">The controller producing the response.</param>
    /// <param name="result">The outcome to translate.</param>
    /// <returns>
    /// <c>200 OK</c> carrying a <see cref="PagedResponse{T}"/> on success; otherwise a problem-details
    /// payload with the mapped status.
    /// </returns>
    /// <exception cref="ArgumentNullException">Either argument is <see langword="null"/>.</exception>
    public static ActionResult<PagedResponse<TItem>> Complete<TItem>(
        this ControllerBase controller,
        Result<PagedResult<TItem>> result)
    {
        ArgumentNullException.ThrowIfNull(controller);
        ArgumentNullException.ThrowIfNull(result);

        if (result.IsFailure)
        {
            return controller.Problem(result);
        }

        PagedResult<TItem> page = result.Value;

        if (page is null)
        {
            throw new InvalidOperationException(
                "A listing reported success but produced no page to return.");
        }

        return controller.Ok(PagedResponse<TItem>.From(page));
    }

    /// <summary>Builds the problem-details payload reported when a successful outcome carried no value.</summary>
    /// <param name="controller">The controller producing the response.</param>
    /// <returns>A <c>404 Not Found</c> problem-details response.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="controller"/> is <see langword="null"/>.</exception>
    public static ObjectResult NotFoundProblem(this ControllerBase controller)
    {
        ArgumentNullException.ThrowIfNull(controller);

        return controller.Problem(
            detail: ResourceNotFoundDetail,
            statusCode: StatusCodes.Status404NotFound,
            type: BuildProblemType(ResourceNotFoundCode));
    }

    /// <summary>
    /// Builds the problem-details payload reported when an authenticated caller is not permitted to perform
    /// an operation.
    /// </summary>
    /// <param name="controller">The controller producing the response.</param>
    /// <param name="code">
    /// The failure code identifying the refusal, so a client can branch on it without parsing prose.
    /// </param>
    /// <returns>A <c>403 Forbidden</c> problem-details response.</returns>
    /// <remarks>
    /// The detail is fixed and names neither the missing grant nor the resource. A refusal that explained
    /// itself would tell an unauthorised caller which identifiers exist and which privilege to acquire, and
    /// it would differ from the refusal the authorisation middleware produces for the same cause - which is
    /// precisely the drift that made the two paths distinguishable before.
    /// </remarks>
    /// <exception cref="ArgumentNullException">Either argument is <see langword="null"/>.</exception>
    public static ObjectResult ForbiddenProblem(this ControllerBase controller, string code)
    {
        ArgumentNullException.ThrowIfNull(controller);
        ArgumentException.ThrowIfNullOrWhiteSpace(code);

        return controller.Problem(
            detail: AuthorizationProblemDetails.ForbiddenDetail,
            statusCode: StatusCodes.Status403Forbidden,
            type: BuildProblemType(code));
    }

    /// <summary>Translates a paged outcome, projecting the domain page onto the wire envelope.</summary>
    /// <typeparam name="TRow">The row contract the page carries.</typeparam>
    /// <param name="controller">The controller producing the response.</param>
    /// <param name="result">The outcome to translate.</param>
    /// <returns>
    /// <c>200 OK</c> carrying a <see cref="PagedResponse{T}"/> on success; otherwise a problem-details
    /// payload with the mapped status.
    /// </returns>
    /// <exception cref="ArgumentNullException">Either argument is <see langword="null"/>.</exception>
    /// <remarks>
    /// A successful outcome carrying no page is refused rather than answered with <c>404</c>, which is
    /// where this member deliberately differs from <see cref="Complete{TValue}(ControllerBase,
    /// Result{TValue})"/>.
    /// </remarks>
    public static ActionResult<PagedResponse<TRow>> CompletePage<TRow>(
        this ControllerBase controller,
        Result<PagedResult<TRow>> result)
    {
        ArgumentNullException.ThrowIfNull(controller);
        ArgumentNullException.ThrowIfNull(result);

        if (result.IsFailure)
        {
            return controller.Problem(result);
        }

        PagedResult<TRow> page = result.Value
            ?? throw new InvalidOperationException(
                "A listing reported success but produced no page to return.");

        return controller.Ok(PagedResponse<TRow>.From(page));
    }
    /// <summary>Translates the outcome of a creation into a <c>201 Created</c> response.</summary>
    /// <typeparam name="TValue">The created representation's type.</typeparam>
    /// <param name="controller">The controller producing the response.</param>
    /// <param name="result">The outcome to translate.</param>
    /// <param name="identify">Extracts the new resource's identifier for the location header.</param>
    /// <returns>
    /// <c>201 Created</c> carrying the representation and a location header, or a problem-details payload
    /// with the mapped status.
    /// </returns>
    /// <exception cref="ArgumentNullException">Any argument is <see langword="null"/>.</exception>
    /// <remarks>
    /// THE PATH BASE IS PART OF THAT ADDRESS AND OMITTING IT NAMES THE WRONG TENANT. A child portal is
    /// addressed by a path segment beneath a shared host name, and <see
    /// cref="Middleware.TenantPathBaseMiddleware"/> moves that segment out of the routable path and into
    /// the path base before routing runs - it has to, or the request matches no route at all.
    /// </remarks>
    public static ActionResult<ApiResponse<TValue>> Created<TValue>(
        this ControllerBase controller,
        Result<TValue> result,
        Func<TValue, object> identify)
    {
        ArgumentNullException.ThrowIfNull(controller);
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(identify);

        if (result.IsFailure)
        {
            return controller.Problem(result);
        }

        TValue value = result.Value;

        if (value is null)
        {
            throw new InvalidOperationException(
                "A creation reported success but produced no representation to return.");
        }

        // The collection address is the PATH BASE plus the PATH, not the path alone.
        string collectionPath = (controller.Request.PathBase.ToUriComponent()
                + controller.Request.Path.ToUriComponent())
            .TrimEnd('/');

        string location = FormattableString.Invariant($"{collectionPath}/{identify(value)}");

        return controller.Created(location, ApiResponse<TValue>.Success(value));
    }

    /// <summary>
    /// Builds the problem-details payload for a failed outcome whose successful counterpart is not a JSON
    /// representation.
    /// </summary>
    /// <param name="controller">The controller producing the response.</param>
    /// <param name="result">The failed outcome.</param>
    /// <returns>The problem-details response.</returns>
    /// <remarks>
    /// Almost every action reaches the failure path through <c>Complete</c>, which needs no separate entry
    /// point. The exception is an action whose success is not a JSON envelope at all - the module export,
    /// which returns an XML document - and which therefore cannot use <c>Complete</c> for either outcome.
    /// </remarks>
    /// <exception cref="ArgumentNullException">Either argument is <see langword="null"/>.</exception>
    internal static ObjectResult Failed(this ControllerBase controller, Result result)
    {
        ArgumentNullException.ThrowIfNull(controller);
        ArgumentNullException.ThrowIfNull(result);

        return controller.Problem(result);
    }

    /// <summary>Builds the problem-details payload for a failed outcome.</summary>
    /// <param name="controller">The controller producing the response.</param>
    /// <param name="result">The failed outcome.</param>
    /// <returns>The problem-details response.</returns>
    private static ObjectResult Problem(this ControllerBase controller, Result result)
    {
        ResultReason? error = result.Error;
        string code = error?.Code ?? "request.failed";
        string detail = SafeDetail(error?.Message);

        return controller.Problem(
            detail: detail,
            statusCode: MapStatusCode(code),
            type: BuildProblemType(code));
    }

    /// <summary>Publishes an expected failure's explanation, or a stand-in when it does not look authored.</summary>
    /// <param name="message">The message carried by the failed outcome, if any.</param>
    /// <returns>The message to publish; never <see langword="null"/> and never empty.</returns>
    /// <remarks>
    /// <b>This is defence in depth and not a substitute for authoring safe messages.</b> Every message a
    /// service places on a failed outcome is meant to be caller-safe by construction, because this method
    /// publishes it verbatim as the RFC 7807 <c>detail</c>.
    /// </remarks>
    private static string SafeDetail(string? message)
    {
        // Unreachable through the public contract, and kept anyway so this method is total.
        if (string.IsNullOrWhiteSpace(message))
        {
            return UnauthoredDetail;
        }

        bool looksAuthored = message.Length <= MaximumPublishedDetailLength
            && message.IndexOf('\n', StringComparison.Ordinal) < 0
            && message.IndexOf('\r', StringComparison.Ordinal) < 0
            && !NamesAnExceptionType(message);

        return looksAuthored ? message : UnauthoredDetail;
    }

    /// <summary>Reports whether a message contains a word that names a CLR exception type.</summary>
    /// <param name="message">The message a failed outcome carried; never blank.</param>
    /// <returns>
    /// <see langword="true"/> when any whitespace-delimited word ends in <c>Exception</c>, ignoring
    /// trailing punctuation.
    /// </returns>
    /// <remarks>
    /// THE THIRD REFUSAL, ADDED BECAUSE THE FIRST TWO DID NOT CATCH THE CASE THAT ACTUALLY OCCURRED. A
    /// service caught a credential-store failure and put <c>exception.GetType().Name</c> into the message
    /// on its failed outcome, reasoning that the underlying cause should survive for a caller to report.
    /// </remarks>
    private static bool NamesAnExceptionType(string message)
    {
        const string suffix = "Exception";

        ReadOnlySpan<char> remaining = message.AsSpan();

        while (!remaining.IsEmpty)
        {
            int separator = remaining.IndexOfAny(' ', '\t');
            ReadOnlySpan<char> word = separator < 0 ? remaining : remaining[..separator];

            // Trailing punctuation is trimmed because the value that prompted this test ended in a full
            // stop, so comparing the raw word would have missed the instance it exists for.
            if (word.TrimEnd(".,;:)]}\"'").EndsWith(suffix, StringComparison.Ordinal))
            {
                return true;
            }

            remaining = separator < 0 ? ReadOnlySpan<char>.Empty : remaining[(separator + 1)..];
        }

        return false;
    }

    /// <summary>Chooses the status code for one failure code.</summary>
    /// <param name="code">The failure code.</param>
    /// <returns>The HTTP status code.</returns>
    public static int MapStatusCode(string? code)
    {
        string normalised = Normalise(code);

        if (normalised.Length == 0)
        {
            return StatusCodes.Status400BadRequest;
        }

        if (UnauthorizedCodes.Contains(normalised, StringComparer.Ordinal)
            || normalised.StartsWith("refresh_token.", StringComparison.Ordinal))
        {
            return StatusCodes.Status401Unauthorized;
        }

        string reason = ReasonToken(normalised);

        if (Matches(reason, UnavailableTokens))
        {
            return StatusCodes.Status503ServiceUnavailable;
        }

        if (Matches(reason, ForbiddenTokens))
        {
            return StatusCodes.Status403Forbidden;
        }

        if (Matches(reason, NotFoundTokens))
        {
            return StatusCodes.Status404NotFound;
        }

        if (Matches(reason, ConflictTokens))
        {
            return StatusCodes.Status409Conflict;
        }

        // Last before the default, and deliberately so: the narrower classifications above describe
        // conditions a caller can act on, and one of those readings must win where both could match.
        if (Matches(reason, InternalFailureTokens))
        {
            return StatusCodes.Status500InternalServerError;
        }

        // Everything remaining describes a request the caller can correct.
        return StatusCodes.Status400BadRequest;
    }

    /// <summary>Reduces a failure code to a stable comparison form.</summary>
    /// <param name="code">The failure code.</param>
    /// <returns>The lower-cased code with hyphens folded onto underscores.</returns>
    /// <remarks>
    /// Hyphens and underscores are folded together because the services disagree about which they use
    /// inside a reason token, and that disagreement is not worth a hundred duplicate table entries.
    /// Upper-cased codes are folded too, so a code spelled in capitals classifies the same as its
    /// lower-cased twin.
    /// </remarks>
    private static string Normalise(string? code)
    {
        return string.IsNullOrWhiteSpace(code)
            ? string.Empty
            : code.Trim().ToLowerInvariant().Replace('-', '_');
    }

    /// <summary>Extracts the segment of a code that names the reason.</summary>
    /// <param name="normalised">The normalised code.</param>
    /// <returns>The final dotted segment, or the whole code when it has no separator.</returns>
    private static string ReasonToken(string normalised)
    {
        int lastSeparator = normalised.LastIndexOf('.');

        return lastSeparator >= 0 && lastSeparator < normalised.Length - 1
            ? normalised[(lastSeparator + 1)..]
            : normalised;
    }

    /// <summary>Tests whether a reason token contains any of a set of markers.</summary>
    /// <param name="reason">The reason token.</param>
    /// <param name="tokens">The markers to look for.</param>
    /// <returns><see langword="true"/> when any marker appears.</returns>
    private static bool Matches(string reason, string[] tokens)
    {
        foreach (string token in tokens)
        {
            if (reason.Contains(token, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Builds the problem type URI-shaped identifier for a failure code.</summary>
    /// <param name="code">The failure code.</param>
    /// <returns>The problem type.</returns>
    /// <remarks>
    /// Visible to the rest of the API assembly so that a refusal decided outside a controller - by the
    /// authorisation middleware result handler - carries a problem type built by this same method, rather
    /// than a second spelling of the same convention.
    /// </remarks>
    internal static string BuildProblemType(string code)
    {
        return FormattableString.Invariant($"urn:dnnmigration:error:{Normalise(code)}");
    }
}
