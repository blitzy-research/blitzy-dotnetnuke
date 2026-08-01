using DnnMigration.Api.Middleware;
using DnnMigration.Domain.Common;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.Extensions.Primitives;

namespace DnnMigration.Api.ErrorHandling;

/// <summary>
/// Translates every exception that escapes the request pipeline into an RFC 7807
/// problem-details response. It is the only type in the solution that does so.
/// </summary>
/// <remarks>
/// <para>
/// <b>Scope.</b> Unhandled exceptions only. Validation and model-state failures belong to
/// <c>Api/Filters/ValidationProblemDetailsFactory.cs</c> and do not reach this type on the
/// normal path, because model binding produces a 400 before anything is thrown. Expected,
/// enumerated failures - a record that was not found, a duplicate user name, an incorrect
/// password - travel as a failed <c>Result</c> and are turned into status codes by the
/// controller that received them. This type is the safety net for what none of those
/// anticipated: it decides nothing about portals, users, roles, modules, tabs or
/// permissions, and it does no work beyond writing one response and one log entry.
/// </para>
/// <para>
/// <b>Mechanism.</b> This is the framework's own <see cref="IExceptionHandler"/> extension
/// point, registered with <c>AddExceptionHandler</c> and reached through
/// <c>UseExceptionHandler</c>, which sits first in the pipeline. It is deliberately not a
/// middleware wrapping the continuation in a <c>try</c>/<c>catch</c>: the framework already
/// owns that responsibility, including response-buffering and re-execution semantics that a
/// bespoke stage tends to get subtly wrong.
/// </para>
/// <para>
/// <b>Lifetime.</b> <c>AddExceptionHandler</c> registers the implementation as a singleton,
/// so every dependency held here is itself a singleton and nothing request-scoped is
/// captured in a field. Per-request state is read from the <see cref="HttpContext"/> passed
/// to <see cref="TryHandleAsync"/> and nowhere else; this type never resolves the request
/// from ambient state.
/// </para>
/// <para>
/// <b>Wire contract.</b> Payloads are built by the registered
/// <see cref="ProblemDetailsFactory"/> rather than constructed here, which makes the member
/// vocabulary - <c>type</c>, <c>title</c>, <c>status</c>, <c>detail</c> and the
/// framework-native <c>traceId</c> extension - identical to the vocabulary of every
/// validation response without restating any of the logic that produces it. The
/// <c>errors</c> object is absent because an unhandled exception carries no per-field
/// failures. The correlation identifier is likewise absent from the body and travels in its
/// own response header, matching the single source of truth that factory already
/// establishes. No serialiser options are declared here, so no legacy sentinel - the empty
/// string for absent text, minus one or zero for an absent or a genuine identifier - can be
/// rewritten or omitted on this path.
/// </para>
/// <para>
/// <b>Disclosure.</b> Nothing about the running process reaches the client: no stack trace,
/// no inner-exception chain, no file path, no assembly or product version, no host name and
/// no fragment of the request. Shape and text are identical in every environment, so no
/// deployment can be made accidentally talkative by configuration alone. The exception, with
/// its full stack, goes to the structured log, and the correlation identifier echoed on the
/// response is what joins a caller's report to that entry.
/// </para>
/// </remarks>
public sealed class GlobalExceptionHandler : IExceptionHandler
{
    // MIGRATION: centralised exception handling is net-new behaviour, not a translation. The
    // five in-scope DotNetNuke 4.9.0 trees contain no exception-to-response translator at
    // all, so nothing here preserves an existing behaviour. The two centralised analogues
    // that do exist in the checkout are both out of scope and neither is ported:
    //
    //   Library/HttpModules/Exception/ExceptionModule.vb - an IHttpModule hooked to
    //   HttpApplication.Error, part of the excluded Library/HttpModules tree and one of the
    //   eight modules the legacy application registered in configuration. There is no
    //   IHttpModule to port to, and three of its behaviours are deliberately inverted rather
    //   than reproduced: it filtered by file extension and skipped named pages, so most
    //   requests were never handled, whereas this type handles every unhandled exception on
    //   every route; it discarded failures in two nested empty catch blocks, whereas this
    //   type never swallows and instead returns false so the framework's default handler
    //   takes over; and it wrote no response body at all, whereas this type always writes a
    //   well-formed payload when it reports that it handled the exception.
    //
    //   Website/ErrorPage.aspx and its code-behind - a server-rendered page, excluded with
    //   the rest of the Web Forms surface. It disclosed the product version in its heading,
    //   which is exactly the disclosure this type refuses. Its one habit worth keeping is
    //   kept: it sanitised every value it echoed from the query string before rendering it,
    //   and nothing here reflects request content into a payload at all.
    //
    // The repository-root MIGRATION_NOTES.md records this divergence at repository level;
    // this annotation is its counterpart in code.

    /// <summary>
    /// Explanation returned when an operation was refused for want of permission.
    /// </summary>
    /// <remarks>
    /// Deliberately says nothing about which permission was missing, which resource was
    /// addressed or whether it exists, because a caller who is not entitled to the operation
    /// is not entitled to learn that either.
    /// </remarks>
    private const string ForbiddenDetail = "You do not have permission to perform this operation.";

    /// <summary>
    /// Explanation returned for any exception without a more specific mapping.
    /// </summary>
    /// <remarks>
    /// Authored text, fixed for every occurrence and every environment. The header name is
    /// composed from the constant that defines it rather than repeated as a literal, so this
    /// sentence cannot come to name a header the application does not send.
    /// </remarks>
    private const string UnexpectedFailureDetail =
        "An unexpected error occurred while processing the request. Quote the "
        + CorrelationIdMiddleware.HeaderName
        + " response header when reporting this problem.";

    private readonly IProblemDetailsService _problemDetailsService;
    private readonly ProblemDetailsFactory _problemDetailsFactory;
    private readonly ILogger<GlobalExceptionHandler> _logger;

    /// <summary>
    /// Initialises a new instance of the <see cref="GlobalExceptionHandler"/> class.
    /// </summary>
    /// <param name="problemDetailsService">
    /// The framework service, registered by <c>AddProblemDetails</c>, that negotiates the
    /// media type and writes the payload. Using it rather than serialising by hand is what
    /// guarantees the <c>application/problem+json</c> content type and the serialiser
    /// settings configured for the application.
    /// </param>
    /// <param name="problemDetailsFactory">
    /// The factory that populates a payload's shared members - the problem-type link, the
    /// status-code title and the trace identifier - so this type's output cannot drift from
    /// every other problem response the application produces.
    /// </param>
    /// <param name="logger">
    /// The logger that receives the exception in full. It is the only destination for
    /// diagnostic detail; none of it reaches the response.
    /// </param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="problemDetailsService"/>, <paramref name="problemDetailsFactory"/> or
    /// <paramref name="logger"/> is <see langword="null"/>.
    /// </exception>
    public GlobalExceptionHandler(
        IProblemDetailsService problemDetailsService,
        ProblemDetailsFactory problemDetailsFactory,
        ILogger<GlobalExceptionHandler> logger)
    {
        ArgumentNullException.ThrowIfNull(problemDetailsService);
        ArgumentNullException.ThrowIfNull(problemDetailsFactory);
        ArgumentNullException.ThrowIfNull(logger);

        _problemDetailsService = problemDetailsService;
        _problemDetailsFactory = problemDetailsFactory;
        _logger = logger;
    }

    /// <summary>
    /// Records <paramref name="exception"/> and, where a response can still be produced,
    /// answers the request with an RFC 7807 problem-details payload.
    /// </summary>
    /// <param name="httpContext">The context of the request that failed.</param>
    /// <param name="exception">The exception that escaped the pipeline.</param>
    /// <param name="cancellationToken">
    /// The request's own abort signal, supplied by the framework, observed when deciding
    /// whether the caller is still there to receive a response. It is not threaded further
    /// because the service that writes the payload exposes no overload accepting a token, so
    /// there is no awaited call here to hand it to.
    /// </param>
    /// <returns>
    /// <see langword="true"/> when this handler has fully written the response, which tells
    /// the framework to stop; <see langword="false"/> when it has not, which asks the
    /// framework to fall through to its own default handling. A <see langword="false"/>
    /// result is a deliberate hand-off and never a swallowed failure: the exception has
    /// always been logged by the time it is returned.
    /// </returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="httpContext"/> or <paramref name="exception"/> is
    /// <see langword="null"/>.
    /// </exception>
    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        ArgumentNullException.ThrowIfNull(exception);

        string correlationId = ResolveCorrelationId(httpContext);

        // A cancellation raised because the caller went away is not a fault of this
        // application, and recording it as an error would bury the failures that are. The
        // type alone cannot establish that - an internal timeout raises the same type and is
        // a genuine server-side failure - so the request's abort signal is what distinguishes
        // them, and the token the framework supplied is tested alongside the one on the
        // context rather than in place of it so the conclusion does not depend on those two
        // being the same signal. There is nobody left to answer, so no payload is written and
        // the hand-off is reported honestly rather than claimed as handled.
        bool callerHasGoneAway =
            cancellationToken.IsCancellationRequested || httpContext.RequestAborted.IsCancellationRequested;

        if (exception is OperationCanceledException && callerHasGoneAway)
        {
            _logger.LogInformation(
                "Request {RequestMethod} {RequestPath} was abandoned because the client disconnected; no response was written. Correlation identifier {CorrelationId}.",
                httpContext.Request.Method,
                httpContext.Request.Path,
                correlationId);

            return false;
        }

        // Tested before anything is assigned. Once the status line and headers are flushed
        // the status code cannot be changed, and a payload appended to what was already sent
        // would corrupt it, so the only correct action is to record the failure and decline.
        // This is the commonest defect in an exception handler, which is why the check comes
        // first rather than immediately before the write.
        if (httpContext.Response.HasStarted)
        {
            _logger.LogError(
                exception,
                "Unhandled exception on {RequestMethod} {RequestPath} after the response had already started; the status code and body cannot be replaced, so no problem-details payload was written. Correlation identifier {CorrelationId}.",
                httpContext.Request.Method,
                httpContext.Request.Path,
                correlationId);

            return false;
        }

        (int statusCode, string detail) = Describe(exception);

        // Level follows the status family: a caller who provoked a 4xx has not broken the
        // server and must not raise an error alert, while anything resolving to 5xx has. The
        // exception is passed as the first argument in both cases, which is what puts its
        // message, inner-exception chain and stack trace in the log - the only place any of
        // them appear. Method and path are recorded because they identify the failing
        // operation; the query string, the request body, the Authorization header and cookies
        // are all deliberately absent, because any of them can carry a credential.
        if (statusCode >= StatusCodes.Status500InternalServerError)
        {
            _logger.LogError(
                exception,
                "Unhandled exception on {RequestMethod} {RequestPath}; responding {StatusCode} with a problem-details payload. Correlation identifier {CorrelationId}.",
                httpContext.Request.Method,
                httpContext.Request.Path,
                statusCode,
                correlationId);
        }
        else
        {
            _logger.LogWarning(
                exception,
                "Request {RequestMethod} {RequestPath} was refused; responding {StatusCode} with a problem-details payload. Correlation identifier {CorrelationId}.",
                httpContext.Request.Method,
                httpContext.Request.Path,
                statusCode,
                correlationId);
        }

        // Assigned before the payload is written: the status carried inside the body is a
        // copy for the reader's benefit and does not set the status line.
        httpContext.Response.StatusCode = statusCode;

        // The dedicated pipeline stage normally supplies this header from a response-starting
        // callback, and assigning the same value through the indexer is idempotent. It is
        // assigned here anyway so that a failure raised before that callback was registered
        // still carries the identifier the entry logged above was tagged with; without it,
        // such a response would be the one a caller could never report.
        httpContext.Response.Headers[CorrelationIdMiddleware.HeaderName] = correlationId;

        // Title and problem type are left unspecified so the factory fills them from the
        // status-code vocabulary shared with every other problem response, and the instance
        // member is left unspecified because deriving it from the request URL would reflect
        // caller-controlled content into the payload.
        ProblemDetails problemDetails = _problemDetailsFactory.CreateProblemDetails(
            httpContext,
            statusCode,
            title: null,
            type: null,
            detail: detail,
            instance: null);

        // The exception travels on the context so a configured customisation can see it; it
        // is not read back here and no part of it is copied into the payload. The service
        // reports whether a writer accepted the payload and that answer is returned
        // unchanged, because claiming success when nothing was written would leave the caller
        // with an empty response and no fall-back.
        return await _problemDetailsService.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            Exception = exception,
            ProblemDetails = problemDetails,
        });
    }

    /// <summary>
    /// Maps an exception to the status code that describes it and the explanation returned to
    /// the caller.
    /// </summary>
    /// <param name="exception">The exception being translated.</param>
    /// <returns>The status code to report and the <c>detail</c> text to publish.</returns>
    /// <remarks>
    /// <para>
    /// Ordered from the most specific case to the least, and deliberately short. A longer
    /// table would mean this type had begun deciding outcomes that belong to the application
    /// layer, where an expected failure is already expressed as a failed <c>Result</c>.
    /// </para>
    /// <para>
    /// No persistence or data-access type is named, and none could be: this project
    /// references neither the object-relational mapper nor the database client, so a failure
    /// from either arrives as the general case. That is the correct outcome as well as the
    /// only available one, since such a failure is a server-side fault and telling its
    /// varieties apart is the application layer's business rather than the transport's.
    /// </para>
    /// </remarks>
    private static (int StatusCode, string Detail) Describe(Exception exception) => exception switch
    {
        // A broken invariant means the request asked the model to enter a state it must never
        // occupy, so the request is what is at fault and 400 is what the caller needs to
        // hear. This message is the one that may be published: it is written by our own
        // domain code, for a reader, and is passed through exactly as authored - including an
        // empty one, which is preserved rather than replaced.
        DomainException domainException => (StatusCodes.Status400BadRequest, domainException.Message),

        // Authentication is settled long before this point, because an unauthenticated caller
        // is challenged by the authentication handler and never arrives here. This is a
        // caller who is known and still not entitled, so 403 rather than 401.
        UnauthorizedAccessException => (StatusCodes.Status403Forbidden, ForbiddenDetail),

        // MIGRATION: the general case publishes fixed text and never the exception's own
        // message, in every environment. That is not conservatism about detail. An
        // object-relational-mapper or database-client message routinely carries the
        // connection string, the server and database names and the values bound to a
        // statement, and an argument or key-lookup message routinely carries the value that
        // failed; any of those can be a credential or personal data, and none can be
        // recognised from the base type, so the only safe rule is to publish none of them.
        // Enriching this text outside production was considered and rejected: it would give
        // one deployment a payload another does not have, and the legacy application set the
        // precedent for one error surface everywhere by declaring the same customErrors mode
        // in both its release and its development configuration. The message and stack are
        // already in the log, joined to this response by the correlation identifier, which is
        // the one diagnostic handle a caller receives.
        _ => (StatusCodes.Status500InternalServerError, UnexpectedFailureDetail),
    };

    /// <summary>
    /// Resolves the correlation identifier for the failed request.
    /// </summary>
    /// <param name="httpContext">The context of the request that failed.</param>
    /// <returns>
    /// The identifier to log and to echo on the response; never <see langword="null"/> and
    /// never empty.
    /// </returns>
    /// <remarks>
    /// <para>
    /// The dedicated pipeline stage publishes the identifier to
    /// <see cref="HttpContext.Items"/> before the rest of the pipeline runs, and that stage
    /// runs inside this handler, so the item is the authoritative source for any request that
    /// reached it. The response header is consulted next, because a value there can only have
    /// been placed by this application; the stage itself writes that header from a
    /// response-starting callback that has not yet run at this point, so the item and not the
    /// header is the primary source. <see cref="HttpContext.TraceIdentifier"/> is the last
    /// resort, reached only when the failure preceded the stage that would have supplied a
    /// value.
    /// </para>
    /// <para>
    /// The inbound request header is deliberately not consulted. It is caller-controlled and
    /// unvalidated; the pipeline stage validates it and substitutes a generated identifier
    /// when it cannot be trusted, and reading the raw value here would reintroduce exactly
    /// the response-splitting and log-forging that substitution prevents. Re-validating it
    /// independently would duplicate that stage's rule and let the two drift, so the
    /// validated result is used, or a framework-supplied value is, and never the raw header.
    /// </para>
    /// </remarks>
    private static string ResolveCorrelationId(HttpContext httpContext)
    {
        // Read with a lookup that reports a miss rather than through the indexer. The
        // dictionary behind Items is replaceable, and a plain Dictionary indexer throws for an
        // absent key - a throw from inside an exception handler being the one failure with
        // nowhere left to go. The by-reference argument here is the base-class-library lookup
        // contract being consumed correctly; no member of this type's own surface takes one.
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
}
