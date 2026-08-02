using System.Text;
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
/// <para>
/// <b>No variable text.</b> This type publishes and records only values it authors itself. The
/// response detail is fixed per status code, so no exception message and no caller-supplied
/// fragment can reach a payload. The log entry carries the request method, the endpoint's own
/// route template and a bounded description of the exception chain, so a crafted request path
/// can neither close a log line and forge the next one nor grow an entry without limit. Because
/// nothing variable is admitted in the first place, no sanitising pass is needed to make it
/// safe.
/// </para>
/// <para>
/// <b>Diagnostics.</b> The exception is described rather than handed to the logger whole: the
/// type name of every link in the chain and its stack trace are recorded in full, because those
/// identify the defect and are authored by us or by a library, while the messages are dropped
/// because a message may quote the input that failed - a value that would not parse, a key that
/// was not found, a composed cache key embedding a user name - and which messages do cannot be
/// established here. The correlation identifier echoed on the response is what joins a caller's
/// report to that entry.
/// </para>
/// </remarks>
public sealed class GlobalExceptionHandler : IExceptionHandler
{
    // MIGRATION: centralised exception handling has no legacy behaviour to translate. The
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
    /// Explanation returned when the domain refused the request as invalid.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Authored here, fixed for every occurrence and every environment, and deliberately
    /// unrelated to whatever the domain said. It names no field, no value and no rule, because a
    /// broken invariant is not a field-level validation report: the request reached a place
    /// where per-field checking had already passed, so there is no field to name and inventing
    /// one would mislead.
    /// </para>
    /// <para>
    /// Callers that need per-field detail get it, and by a different route. A request that fails
    /// declarative validation never reaches this type at all: the validation filter turns it
    /// into a 400 carrying the <c>errors</c> object, whose messages are authored, bounded
    /// validator text rather than arbitrary exception text. That path is unaffected by this
    /// constant and must stay that way.
    /// </para>
    /// </remarks>
    private const string InvalidRequestDetail =
        "The request was rejected because one or more of its values are not valid. Quote the "
        + CorrelationIdMiddleware.HeaderName
        + " response header when reporting this problem.";

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

    /// <summary>
    /// Text recorded in place of a route template when the failure happened before, or
    /// outside, endpoint selection.
    /// </summary>
    private const string UnmatchedRouteTemplate = "(no matched endpoint)";

    /// <summary>
    /// Greatest number of links of an inner-exception chain that are described.
    /// </summary>
    /// <remarks>
    /// A chain deeper than this carries no further diagnostic value, and a bound removes any
    /// possibility of an unbounded log entry - or a non-terminating walk - being produced from
    /// an exception graph this type did not construct and cannot vouch for.
    /// </remarks>
    private const int MaximumDescribedChainDepth = 8;

    /// <summary>
    /// Identifies the entry recorded when a caller abandoned the request.
    /// </summary>
    /// <remarks>
    /// The four identifiers on this type are fixed, distinct and never reused, so an alert or
    /// a dashboard can select one class of event without matching on message text - which is
    /// prose, and may be reworded.
    /// </remarks>
    private static readonly EventId ClientDisconnectedEvent = new(1001, "ClientDisconnected");

    /// <summary>
    /// Identifies the entry recorded when the response had already started.
    /// </summary>
    private static readonly EventId ResponseAlreadyStartedEvent = new(1002, "ResponseAlreadyStarted");

    /// <summary>
    /// Identifies the entry recorded for a failure answered with a 5xx status code.
    /// </summary>
    private static readonly EventId UnhandledServerFaultEvent = new(1003, "UnhandledServerFault");

    /// <summary>
    /// Identifies the entry recorded for a failure answered with a 4xx status code.
    /// </summary>
    private static readonly EventId RequestRefusedEvent = new(1004, "RequestRefused");

    private readonly IProblemDetailsService _problemDetailsService;
    private readonly ProblemDetailsFactory _problemDetailsFactory;
    private readonly ILogger<GlobalExceptionHandler> _logger;

    /// <summary>
    /// Initialises a new instance of the <see cref="GlobalExceptionHandler"/> class.
    /// </summary>
    /// <param name="problemDetailsService">
    /// The framework service, registered by <c>AddProblemDetails</c>, that writes the
    /// payload. Using it rather than serialising by hand is what keeps this handler's
    /// output on the serialiser settings configured for the application, so an exception
    /// response cannot drift from every other problem response in member casing or null
    /// handling. It is deliberately <em>not</em> claimed here to guarantee the RFC 7807
    /// section 3 <c>application/problem+json</c> content type: every controller-produced
    /// problem document this host emits was measured at runtime as
    /// <c>application/json; charset=utf-8</c>, the bodies being correct RFC 7807 documents
    /// either way. The deviation is documented in <c>MIGRATION_NOTES.md</c> rather than
    /// corrected, because the mandated envelope is a body contract that is satisfied and
    /// nothing in this solution or in the Angular client reads the media type.
    /// </param>
    /// <param name="problemDetailsFactory">
    /// The factory that populates a payload's shared members - the problem-type link, the
    /// status-code title and the trace identifier - so this type's output cannot drift from
    /// every other problem response the application produces.
    /// </param>
    /// <param name="logger">
    /// The logger that receives the diagnostic description of the failure. It is the only
    /// destination for diagnostic detail; none of it reaches the response. What is recorded is
    /// an allowlist rather than the exception object - see
    /// <see cref="DescribeForDiagnostics(Exception)"/> for what that admits and what it
    /// withholds.
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

        // MIGRATION: DIVERGENCE. Both of these replace a value that used to be recorded
        // directly. The route TEMPLATE replaces the request path, because a path is caller
        // input: identifiers in it are frequently personal - an email address used as a route
        // key, an account number - and a path is where a mistyped credential lands when a
        // caller puts one in the wrong place. The template is the pattern the endpoint
        // declared, is authored by us, and groups every request to one operation under one
        // value, which a path cannot do. The exception DESCRIPTION replaces the exception
        // object, for the reason set out on DescribeForDiagnostics.
        string routeTemplate = DescribeRouteTemplate(httpContext);
        string failure = DescribeForDiagnostics(exception);

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
                ClientDisconnectedEvent,
                "Request {RequestMethod} {RouteTemplate} was abandoned because the client disconnected; no response was written. Failure {Failure}. Correlation identifier {CorrelationId}.",
                httpContext.Request.Method,
                routeTemplate,
                failure,
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
                ResponseAlreadyStartedEvent,
                "Unhandled exception on {RequestMethod} {RouteTemplate} after the response had already started; the status code and body cannot be replaced, so no problem-details payload was written. Failure {Failure}. Correlation identifier {CorrelationId}.",
                httpContext.Request.Method,
                routeTemplate,
                failure,
                correlationId);

            return false;
        }

        (int statusCode, string detail) = Describe(exception);

        // Level follows the status family: a caller who provoked a 4xx has not broken the
        // server and must not raise an error alert, while anything resolving to 5xx has.
        //
        // Every entry this type writes carries the same allowlisted shape - event identifier,
        // request method, route template, the redacted failure description, the status code
        // where one was chosen, and the correlation identifier. The list is closed: a value not
        // named here is not recorded. The query string, the request body, the route VALUES, the
        // Authorization header, cookies and the raw request path are all deliberately absent,
        // because each of them can carry a credential or personal data, and the exception
        // object is absent for the same reason - see DescribeForDiagnostics.
        //
        // Correlating an entry with the request that produced it is the correlation
        // identifier's job, and it is on every entry and on the response, so nothing is lost
        // by declining to repeat caller input here.
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
        // occupy, so the request is what is at fault and 400 is what the caller needs to hear.
        //
        // The exception's OWN message is deliberately not published, and the discarded argument
        // for publishing it is worth recording because it is superficially convincing: the text
        // is written by our own domain code, for a reader, so it looks safe by provenance. It is
        // not. Being safe would require every author of every DomainException message, now and
        // in future, to omit every value the caller supplied - an unenforceable property of
        // human discipline rather than a property of the code, and one this solution had already
        // failed to hold. PortalGuid.Parse interpolated the rejected caller text straight into
        // its message, so an arbitrary string chosen by an unauthenticated caller was echoed
        // back inside a 400 body. That site is fixed, but a rule that depends on nobody ever
        // reintroducing it is not a rule. Publishing fixed text instead makes the guarantee
        // structural: a future interpolated message becomes a log entry and nothing more.
        //
        // The diagnostics that matter are still recorded. The 4xx branch describes the failure for
        // the log exactly as the 5xx branch does, through DescribeForDiagnostics: the type name of
        // every link in the exception chain and its stack trace are written in full, because those
        // identify the defect and are authored by us or by a library, while the messages are
        // dropped because a message may quote the input that failed. The exception object itself is
        // deliberately not handed to the logger, for the reason set out on that member. The
        // correlation identifier echoed on this response is what joins a caller's report to that
        // entry. This is the same treatment the general case below receives, for the same reason,
        // which leaves UnauthorizedAccessException the only arm whose text is specific, and that
        // text is authored here rather than taken from the exception.
        // The one exception to "never publish an exception's own text" is opt-in and explicit:
        // DomainException.PublicDetail exists so a thrower can supply caller-safe wording
        // deliberately. Absent or blank, the authored constant above is used, so a thrower cannot
        // disclose by accident and a caller never receives an empty explanation.
        DomainException domainException => (
            StatusCodes.Status400BadRequest,
            string.IsNullOrWhiteSpace(domainException.PublicDetail)
                ? InvalidRequestDetail
                : domainException.PublicDetail),

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

    /// <summary>
    /// Describes the endpoint a request reached by its declared route template.
    /// </summary>
    /// <param name="httpContext">The context of the request being described.</param>
    /// <returns>
    /// The raw route pattern of the selected endpoint, or
    /// <see cref="UnmatchedRouteTemplate"/> when no endpoint with a pattern was selected.
    /// </returns>
    /// <remarks>
    /// <para>
    /// The template is authored text - the pattern declared on the action - so it is safe to
    /// record, and it is stable across every request to the same operation, which is what makes
    /// it aggregatable. A request path is neither: it contains the caller's own values.
    /// </para>
    /// <para>
    /// Only <see cref="RouteEndpoint"/> carries a pattern. An endpoint of any other kind, or
    /// none at all - the case when the failure was raised by a stage that runs before endpoint
    /// selection, or when nothing matched - yields the fixed substitute rather than falling
    /// back to the path, because falling back to the path would reintroduce exactly the value
    /// this method exists to keep out of the log.
    /// </para>
    /// </remarks>
    private static string DescribeRouteTemplate(HttpContext httpContext)
    {
        string? template = (httpContext.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText;

        return string.IsNullOrWhiteSpace(template) ? UnmatchedRouteTemplate : template;
    }

    /// <summary>
    /// Describes a failure for the log without admitting any exception message.
    /// </summary>
    /// <param name="exception">The failure to describe.</param>
    /// <returns>
    /// The type name of each exception in the chain, each followed by its stack trace where one
    /// is present, outermost first.
    /// </returns>
    /// <remarks>
    /// <para>
    /// MIGRATION: DIVERGENCE. The exception object used to be handed to the logger, which
    /// records its message, the message of every inner exception and the stack trace. The type
    /// names and the stack trace are the parts that identify a defect, and they are kept in
    /// full. The MESSAGES are the part that cannot be vouched for, and they are dropped.
    /// </para>
    /// <para>
    /// A message is not always authored text. Framework and library messages routinely quote
    /// the input that failed - a value that would not parse, a key that was not found, a
    /// connection string a provider rejected - and application messages do too, as
    /// <c>Domain/ValueObjects/PortalGuid.cs:L233</c> does. So a message may contain a
    /// credential, a personal identifier, or a fragment of another tenant's data, and which
    /// messages do cannot be established by inspecting this type. An allowlist that admits
    /// only what is known to be safe is therefore the only shape that stays correct as
    /// exceptions this handler has never seen reach it.
    /// </para>
    /// <para>
    /// A stack trace is method, type and, where symbols are published, file and line: all of it
    /// authored by us or by a library, none of it caller input. It is what makes an entry
    /// actionable, which is why the redaction is of messages specifically and not of diagnostic
    /// detail generally.
    /// </para>
    /// <para>
    /// The chain is walked iteratively and bounded by
    /// <see cref="MaximumDescribedChainDepth"/>. An <see cref="AggregateException"/> is
    /// followed through <see cref="Exception.InnerException"/> like any other, which reports
    /// its first inner failure; enumerating every branch would let one exception produce an
    /// unbounded entry, and the first branch is what identifies the defect in practice.
    /// </para>
    /// </remarks>
    private static string DescribeForDiagnostics(Exception exception)
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

/// <summary>
/// Translates an application-layer outcome into an HTTP response.
/// </summary>
/// <remarks>
/// <para>
/// This lives beside <see cref="GlobalExceptionHandler"/> on purpose. That type maps <em>exceptions</em> to
/// status codes and this one maps <em>expected failures</em> to status codes, and the two tables have to
/// agree about what a given condition means. Keeping them in one file means a reader can compare them at a
/// glance instead of discovering months later that a rule is expressed as a 400 down one path and a 409 down
/// the other.
/// </para>
/// <para>
/// <strong>No failure translated here becomes a 500.</strong> A failed <c>Result</c> is by definition an
/// expected outcome that the caller can be told about, so it maps to a status in the 4xx range, or to 503
/// where a dependency is genuinely unavailable. A 500 means something happened that this application did not
/// anticipate, which is exactly the set of conditions that arrive as exceptions and are handled by the
/// sibling type.
/// </para>
/// <para>
/// <strong>The mapping keys on the reason token, not on the whole code.</strong> Failure codes in this
/// solution are dotted paths whose final segment names the reason - and the segment separator inside that
/// last token is not consistent, because different services were written to use underscores and hyphens.
/// Normalising both away and matching on the reason means the table does not have to enumerate a hundred
/// codes, and a new code that follows the naming convention is classified correctly without this file
/// changing.
/// </para>
/// </remarks>
public static class ApiResults
{
    /// <summary>The reason tokens that mean the addressed thing does not exist.</summary>
    private static readonly string[] NotFoundTokens =
    {
        "not_found", "notfound", "source_missing", "unknown_profile_property", "unknown_property",
    };

    /// <summary>The reason tokens that mean the request conflicts with the current state.</summary>
    private static readonly string[] ConflictTokens =
    {
        "duplicate", "already_exists", "already_registered", "already_required", "unchanged",
        "not_different", "conflict",
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

    /// <summary>The whole codes that mean the caller has not proved who they are.</summary>
    private static readonly string[] UnauthorizedCodes =
    {
        "auth.invalid_credentials", "auth.locked_out", "auth.invalid_refresh_token",
        "auth.insecure_admin_password", "auth.insecure_host_password",
    };

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

    /// <summary>Translates an outcome that carries a value.</summary>
    /// <typeparam name="TValue">The value type.</typeparam>
    /// <param name="controller">The controller producing the response.</param>
    /// <param name="result">The outcome to translate.</param>
    /// <returns>
    /// <c>200 OK</c> with the value on success; <c>404 Not Found</c> when the outcome succeeded but carries
    /// no value, because a nullable value on a successful outcome is how this solution expresses "asked, and
    /// it is not there"; otherwise a problem-details payload with the mapped status.
    /// </returns>
    /// <exception cref="ArgumentNullException">Either argument is <see langword="null"/>.</exception>
    public static ActionResult<TValue> Complete<TValue>(
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

        return value is null ? controller.NotFound() : controller.Ok(value);
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
    /// The location is derived from the request path rather than from a named route, because every creation
    /// endpoint in this API posts to a collection whose member address is that path plus the identifier. A
    /// named-route lookup would add a second place the address is spelled, and a mismatch between them fails
    /// only at run time and only in the header.
    /// </remarks>
    public static ActionResult<TValue> Created<TValue>(
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
            // A creation that reports success must produce a representation. Reaching here means a service
            // returned a successful outcome with no value, which is a defect in that service rather than a
            // condition the caller can act on, so it is surfaced as an unexpected failure.
            throw new InvalidOperationException(
                "A creation reported success but produced no representation to return.");
        }

        string collectionPath = controller.Request.Path.Value?.TrimEnd('/') ?? string.Empty;
        string location = FormattableString.Invariant($"{collectionPath}/{identify(value)}");

        return controller.Created(location, value);
    }

    /// <summary>Builds the problem-details payload for a failed outcome.</summary>
    /// <param name="controller">The controller producing the response.</param>
    /// <param name="result">The failed outcome.</param>
    /// <returns>The problem-details response.</returns>
    /// <remarks>
    /// The failure code travels as the problem type so a client can branch on it without parsing prose, and
    /// the message travels as the detail. Neither carries a stack trace or an identifier the caller was not
    /// already entitled to see.
    /// </remarks>
    private static ObjectResult Problem(this ControllerBase controller, Result result)
    {
        ResultReason? error = result.Error;
        string code = error?.Code ?? "request.failed";
        string detail = error?.Message ?? "The request could not be completed.";

        return controller.Problem(
            detail: detail,
            statusCode: MapStatusCode(code),
            type: BuildProblemType(code));
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

        // Everything remaining describes a request the caller can correct.
        return StatusCodes.Status400BadRequest;
    }

    /// <summary>Reduces a failure code to a stable comparison form.</summary>
    /// <param name="code">The failure code.</param>
    /// <returns>The lower-cased code with hyphens folded onto underscores.</returns>
    /// <remarks>
    /// Hyphens and underscores are folded together because the services disagree about which they use inside
    /// a reason token, and that disagreement is not worth a hundred duplicate table entries. Upper-cased
    /// codes are folded too, so a code spelled in capitals classifies the same as its lower-cased twin.
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
    /// A relative, non-dereferenceable identifier. RFC 7807 permits one, and inventing an absolute URL for a
    /// documentation page that does not exist would be worse than not having one: a client would follow it
    /// and get a 404 from an unrelated host.
    /// </remarks>
    private static string BuildProblemType(string code)
    {
        return FormattableString.Invariant($"urn:dnnmigration:error:{Normalise(code)}");
    }
}
