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
/// failures. The correlation identifier IS present in the body, as the factory's
/// <c>correlationId</c> extension, and it is the same value this type writes to the response
/// header - a caller quoting the reference from either place quotes the identifier the log
/// entry below is tagged with. No serialiser options are declared here, so no legacy sentinel - the empty
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
    /// Explanation returned when the submitted body exceeded the configured request-size ceiling.
    /// </summary>
    /// <remarks>
    /// The ceiling itself is deliberately NOT quoted. A refusal that published the exact limit would
    /// hand an unauthenticated caller the one number needed to sit just beneath it, and the limit is a
    /// deployment setting rather than part of the contract. It says what to do instead, which is the
    /// only thing the caller can act on.
    /// </remarks>
    private const string PayloadTooLargeDetail =
        "The submitted request body is larger than this endpoint accepts. Submit a smaller body.";

    /// <summary>
    /// Explanation returned when the server could not read the request at the transport level.
    /// </summary>
    /// <remarks>
    /// Covers a malformed chunked body, a declared length that disagrees with what arrived, and the
    /// other framing faults the host rejects before any model binding happens. The host's own message
    /// is not published, for the reason set out on <see cref="UnexpectedFailureDetail"/>.
    /// </remarks>
    private const string MalformedRequestDetail =
        "The request could not be read. Check the request framing and headers, then submit it again.";

    /// <summary>
    /// Explanation returned when the store refused a write because the value it carried is already
    /// held by another record.
    /// </summary>
    /// <remarks>
    /// <para>
    /// MIGRATION: SEC-F6, AND A SAFETY NET RATHER THAN THE ANSWER. Every create path that writes
    /// through a unique constraint catches the duplicate-key signal itself and returns the SAME reason
    /// code its own sequential pre-check emits, so a caller receives a 409 naming the field that
    /// collided - the role name, the alias, the account name, the profile property - and never reaches
    /// this text. This arm exists because "every path" is a property of today's code: a path added later
    /// that forgets to catch would otherwise fall to the general case and be answered 500, telling the
    /// caller the server failed when the store had behaved correctly and kept exactly one record.
    /// Answering 409 with authored wording makes the status right even when the specific wording is not
    /// available.
    /// </para>
    /// <para>
    /// The constraint name is deliberately NOT published even though the signal carries one. An index
    /// name is a schema fact of no use to a caller and of obvious use to an attacker mapping the store,
    /// and it reaches the log through the diagnostic description like every other part of the chain.
    /// The wording names no field, because at this point none is known: a handler that guessed would be
    /// wrong for exactly the paths this net is here to cover.
    /// </para>
    /// </remarks>
    private const string DuplicateRecordDetail =
        "The submitted values conflict with a record that already exists. "
        + "Reload the resource and submit different values.";

    /// <summary>
    /// Explanation returned when the store refused a write because the record had already been changed
    /// by another caller.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A SAFETY NET, exactly like the duplicate arm above. Every whole-record write path compares the
    /// token the caller round-tripped and answers with its own reason code, naming the resource and
    /// telling the caller to reload it, so a caller normally never reaches this text. What that
    /// comparison cannot do on its own is close the window between the read and the flush - it is a
    /// pre-check, and two callers can both pass one - which is why those paths run the read, the
    /// comparison and the flush inside one serialisable scope and why the store's refusal of the losing
    /// half arrives here as a signal rather than as a status.
    /// </para>
    /// <para>
    /// The wording carries the one instruction that recovers from it and discloses nothing about WHICH
    /// value moved: a caller able to infer the changed column from a refusal would be reading another
    /// caller's edit through an error message. It names no resource either, because at this point none
    /// is known - a handler that guessed would be wrong for exactly the paths this net covers.
    /// </para>
    /// </remarks>
    private const string ConcurrentWriteDetail =
        "The record was changed by someone else after you read it, so nothing was written. "
        + "Reload the resource and apply your change again.";

    /// <summary>
    /// Explanation returned when a dependency this request needed could not be reached or could
    /// not serve.
    /// </summary>
    /// <remarks>
    /// <para>
    /// MIGRATION: a database outage used to be answered 500 on every data endpoint. The payload
    /// disclosed nothing and the readiness view already reported the outage as 503 to an
    /// orchestrator, so the defect was in the INSTRUCTION the status carried: 500 says "this
    /// server has a fault, report it", while the truth was "a dependency is down, retry
    /// shortly". Measured during runtime testing with the database stopped, where sign-in was
    /// answered 500.
    /// </para>
    /// <para>
    /// The wording says "a service this request depends on" rather than naming the store. Which
    /// dependency failed is an infrastructure fact of no use to a caller, and the alternative
    /// wording would tell an unauthenticated caller which component to probe. It says what can be
    /// acted on - retry, and quote the reference if it persists - and the reference is what joins
    /// the report to the log entry that does name the failure in full.
    /// </para>
    /// </remarks>
    private const string StoreUnavailableDetail =
        "A service this request depends on is temporarily unavailable. Retry after a short delay, "
        + "and quote the "
        + CorrelationIdMiddleware.HeaderName
        + " response header if the problem persists.";

    /// <summary>
    /// Value of the <c>Retry-After</c> header sent with a 503, in seconds.
    /// </summary>
    /// <remarks>
    /// Five seconds, and a delta-seconds form rather than a date, because a date requires the
    /// caller's clock to agree with this server's. The figure is a hint and nothing depends on
    /// it being right: it is short enough that a caller retrying on it recovers promptly from a
    /// brief failover, and long enough that a client honouring it does not become a load
    /// generator against a store that is already struggling. The same header and figure are
    /// emitted by the reverse proxy for the case it answers - the API being unreachable
    /// altogether - so a client sees one consistent hint whichever hop reports the condition.
    /// </remarks>
    private const string RetryAfterSeconds = "5";

    /// <summary>
    /// Text recorded in place of a route template when the failure happened before, or
    /// outside, endpoint selection.
    /// </summary>
    private const string UnmatchedRouteTemplate = "(no matched endpoint)";

    /// <summary>
    /// Key under which this type publishes the status code it answered a failed request with,
    /// so a stage that unwound before the outcome was known can still record it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This handler runs OUTSIDE <c>RequestLoggingMiddleware</c>, which means a failure has
    /// already passed through that stage by the time the status code is chosen here. The value
    /// published under this key is the outcome that stage could not have known, and it is
    /// authoritative for the request: it is the status this type assigned to the response, not
    /// an inference from it.
    /// </para>
    /// <para>
    /// It is published only where an outcome is actually chosen. Nothing is published when the
    /// caller has gone away, because no response was written and there is no outcome to report,
    /// and nothing is published when the response had already started, because the status line
    /// the caller received was decided elsewhere and is already on the response for any reader
    /// to see. A consumer must therefore treat an absent value as normal and fall back to the
    /// status code on the response.
    /// </para>
    /// <para>
    /// The prefix matches <see cref="CorrelationIdMiddleware.ItemKey"/> so that everything this
    /// application publishes on the request's item dictionary is recognisable as its own and
    /// cannot collide with a framework or library key.
    /// </para>
    /// </remarks>
    internal const string AnsweredStatusItemKey = "DnnMigration.AnsweredStatusCode";

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
    private readonly IStoreFailureClassifier _storeFailures;
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
    /// <param name="storeFailures">
    /// The classifier that reports whether a failure means the backing store was unreachable
    /// rather than that this application has a defect. It is injected rather than consulted
    /// statically because the knowledge it applies is database-provider knowledge, and this
    /// project references no database provider: the contract is declared in the Domain and
    /// implemented in the persistence assembly, which is the only assembly permitted to name a
    /// client type. See <see cref="Describe(Exception, IStoreFailureClassifier)"/> for what the
    /// answer changes.
    /// </param>
    /// <param name="logger">
    /// The logger that receives the diagnostic description of the failure. It is the only
    /// destination for diagnostic detail; none of it reaches the response. What is recorded is
    /// an allowlist rather than the exception object - see
    /// <see cref="DescribeForDiagnostics(Exception)"/> for what that admits and what it
    /// withholds.
    /// </param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="problemDetailsService"/>, <paramref name="problemDetailsFactory"/>,
    /// <paramref name="storeFailures"/> or <paramref name="logger"/> is <see langword="null"/>.
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

        (int statusCode, string detail) = Describe(exception, _storeFailures);

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

        // MIGRATION: a 503 carries a retry hint, because a status that says "come back later" and
        // then declines to say when leaves a client to invent an interval - and the interval it
        // invents is usually "immediately", which is how an already-struggling dependency acquires a
        // retry storm. Written through the typed accessor so the header name and its delta-seconds
        // form cannot drift, and only for 503: on a 500 there is nothing to come back for, and
        // advertising a retry would be an invitation to repeat a request that will fail identically.
        if (statusCode == StatusCodes.Status503ServiceUnavailable)
        {
            httpContext.Response.Headers.RetryAfter = RetryAfterSeconds;
        }

        // Published for the request-logging stage, which unwound before this decision was made
        // and would otherwise record the status the response carried BEFORE the failure - which
        // for a fault raised inside a controller action is 200, for a request answered 500.
        // The item dictionary is the only channel available: that stage is inside this handler,
        // so it cannot be told by a return value, and it must not re-derive the status by
        // repeating the translation performed here.
        httpContext.Items[AnsweredStatusItemKey] = statusCode;

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
    /// <param name="storeFailures">
    /// The classifier consulted after the table below has been exhausted, to tell a store
    /// outage apart from a defect.
    /// </param>
    /// <returns>The status code to report and the <c>detail</c> text to publish.</returns>
    /// <remarks>
    /// <para>
    /// Ordered from the most specific case to the least, and deliberately short. A longer
    /// table would mean this type had begun deciding outcomes that belong to the application
    /// layer, where an expected failure is already expressed as a failed <c>Result</c>.
    /// </para>
    /// <para>
    /// No persistence or data-access type is named, and none could be: this project
    /// references neither the object-relational mapper nor the database client. A failure from
    /// either therefore cannot be recognised BY TYPE here, which is why the one store-shaped
    /// question this type does ask is asked through an injected Domain contract whose
    /// implementation lives in the persistence assembly. The rule that the transport names no
    /// provider type holds unchanged.
    /// </para>
    /// <para>
    /// MIGRATION: SEC-F6 ADDED ONE STORE-SHAPED ARM, AND IT DOES NOT WEAKEN THE RULE ABOVE. A unique
    /// constraint refusing a write is not a server fault, so it must not be answered 500 - but it is
    /// recognised here through a DOMAIN signal raised at the persistence seam, not by inspecting a
    /// provider exception. The rule holds unchanged: the layer that owns the classification performs it,
    /// and this type still names no mapper or client type. Every other store failure continues to arrive
    /// as the general case.
    /// </para>
    /// </remarks>
    private static (int StatusCode, string Detail) Describe(
        Exception exception,
        IStoreFailureClassifier storeFailures) => exception switch
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

        // MIGRATION: SEC-F6. A UNIQUE CONSTRAINT REFUSING A WRITE IS THE CALLER'S CONFLICT, NOT A SERVER
        // FAULT. The store told us the value is already held; it did exactly its job and kept exactly one
        // record, so 409 is what the caller needs to hear and 500 - what this family was answered before -
        // was wrong on both counts, misreporting a correct store and raising a server-fault log entry for
        // an ordinary collision.
        //
        // This arm does NOT contradict the paragraph above about naming no persistence type. It names
        // none: the signal is a DOMAIN type, raised at the persistence seam where the provider fault is
        // recognised and translated, precisely so the transport can classify this outcome without
        // referencing the mapper or the database client. That translation is also why the arm is safe to
        // place here rather than being "the application layer's business" - the classification has already
        // been made by the layer that owns it.
        //
        // It is a net, not the route. Each create path catches the same signal and answers with the reason
        // code its own pre-check emits, which is both more useful and reached first; see the remarks on
        // DuplicateRecordDetail for why that text names no field and never publishes the constraint name.
        DuplicateKeyException => (StatusCodes.Status409Conflict, DuplicateRecordDetail),

        // A LOST UPDATE IS THE CALLER'S CONFLICT TOO, AND FOR THE SAME REASONS. The store refused a write
        // because the row had already moved: it did exactly its job and it preserved somebody's committed
        // edit, so 409 with an instruction to reload is what the caller needs to hear, and the 500 this
        // family was answered before was wrong on both counts - it reported a defect that does not exist and
        // it raised a server-fault log entry for an ordinary collision.
        //
        // Like the arm above it names no persistence type. The signal is a DOMAIN type raised at the
        // persistence seam, where an affected-row count of zero and a serialisation-failure error number are
        // both recognisable, precisely so the transport can classify the outcome without referencing the
        // mapper or the database client.
        //
        // It is a net, not the route: each write path catches the same signal and answers with the reason
        // code its own token comparison emits, which names the resource and is reached first.
        ConcurrencyConflictException => (StatusCodes.Status409Conflict, ConcurrentWriteDetail),

        // MIGRATION: A TRANSPORT REFUSAL CARRIES THE STATUS THE HOST ALREADY CHOSE. The host raises
        // this type when it declines to read a request itself - the body exceeding the configured size
        // ceiling, a malformed chunked body, a declared length that disagrees with what arrived - and it
        // records the status it settled on ON THE EXCEPTION. Without this arm the whole family fell to
        // the general case and was answered 500 and logged at Error, which was wrong twice over: it told
        // the caller the server had failed when the caller had submitted something the server correctly
        // refused, and it raised a server-fault log entry for an ordinary client mistake, so a volume of
        // oversized submissions read as an outage. Both are corrected by taking the status the host
        // already decided; the log level follows from the status family with no further change, because
        // the branch above keys off exactly that.
        //
        // The exception's own message is not published, in keeping with every other arm - the host's
        // wording for a size refusal quotes the configured limit, which is a deployment fact and not
        // part of any contract. The status is READ from the exception rather than assumed to be 413:
        // this type also carries 400 for the framing faults, and hard-coding either would mislabel the
        // other. Kestrel's own subclass derives from this type, so one arm covers both, and an
        // unexpected status on the exception is honoured as-is rather than being second-guessed here.
        BadHttpRequestException badRequest => (
            badRequest.StatusCode,
            badRequest.StatusCode == StatusCodes.Status413PayloadTooLarge
                ? PayloadTooLargeDetail
                : MalformedRequestDetail),

        // MIGRATION: A DEPENDENCY BEING DOWN IS NOT THIS SERVER HAVING A DEFECT, AND 503 IS WHAT SAYS SO.
        // This arm is reached only after every arm above has declined, so nothing already classified is
        // affected: a broken invariant is still 400, a refusal 403, a duplicate 409 and a transport fault
        // whatever the host decided. What changes is the residue - the failures that used to fall to the
        // general case below - and only the part of it that a classifier can positively identify as an
        // availability condition of the store or of the path to it.
        //
        // It is a GUARD rather than a type pattern for the reason set out on the constructor parameter:
        // this project references no database provider, so the exception cannot be recognised by type
        // here. The question is asked of a Domain contract whose implementation lives in the persistence
        // assembly, which is the only assembly permitted to know that a severity class of 20 or more means
        // the connection is gone. That keeps the rule intact - the layer that owns the classification
        // performs it - while letting the transport act on the answer, which is the only layer that can
        // choose a status code.
        //
        // The classification is deliberately conservative: anything it cannot positively identify stays
        // 500. Answering 503 for a defect would tell a caller to retry a request that can never succeed
        // and would mask a fault behind a retry loop, which is strictly worse than the 500 this replaces.
        // The log level follows the status family with no further change - both are 5xx - so an outage is
        // still recorded at Error and still raises whatever a deployment alerts on.
        // MIGRATION: A STALLED CACHE PRODUCTION IS THIS SERVER'S DEFECT, NOT A DEPENDENCY OUTAGE, AND THIS
        // ARM IS WHERE THE TWO ARE TOLD APART. It sits ABOVE the availability guard deliberately, and the
        // order is the whole of its effect: reaching the guard with this condition is what used to answer
        // 503 with a Retry-After. The cache raised a plain TimeoutException when a value factory outran its
        // budget, the classifier read any bare timeout in the chain as an unreachable database, and a caller
        // was consequently told to retry a request that would fail identically while the actual defect - a
        // factory ignoring the cancellation token it was handed - was hidden behind the retry hint.
        //
        // Two changes settle it and BOTH are needed. The cache now raises a cache-specific type, and the
        // classifier now requires structural data-access context before reading a generic timeout as an
        // outage. This arm is the third: it maps the condition EXPLICITLY, so the mapping is a stated
        // decision rather than a consequence of falling through to the general case, and a reader can see
        // that the omission of a retry hint is deliberate. 500 is the honest status - the request can be
        // reissued, but nothing about waiting makes it more likely to succeed - and the published wording is
        // the same fixed sentence every unexpected failure carries, so the cache key, the value shape and
        // the budget stay out of the response. The log entry above records the failure for the operator.
        CacheProductionTimeoutException => (
            StatusCodes.Status500InternalServerError,
            UnexpectedFailureDetail),

        Exception when storeFailures.IsStoreUnavailable(exception) => (
            StatusCodes.Status503ServiceUnavailable,
            StoreUnavailableDetail),

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
    /// <para>
    /// Visible to the assembly rather than private because <c>RequestLoggingMiddleware</c>
    /// describes the same request for the same reason, and two implementations of one
    /// disclosure policy drift: the moment they disagree, the request log admits the value the
    /// exception log refuses. One method, called from both, cannot.
    /// </para>
    /// </remarks>
    internal static string DescribeRouteTemplate(HttpContext httpContext)
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
    /// <para>
    /// Visible to the assembly rather than private because <c>RequestLoggingMiddleware</c> has
    /// the same failure to record and must record it under the same allowlist. Handing the
    /// exception object to a logger there instead - which is what a request log conventionally
    /// does - would publish through the provider's own renderer every message this method
    /// exists to drop, and would do so from the highest-volume, longest-retained log the
    /// application writes. The policy is therefore implemented once and shared, not restated.
    /// </para>
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
/// <strong>A failure that describes an internal fault is reported as one.</strong> Most failed
/// <c>Result</c> values describe something the caller can correct, and those map into the 4xx range. A
/// minority do not: a portal that could not be written, a role that could not be created, a module
/// business controller whose own code threw while exporting or importing. Nothing about the request
/// produced those, so answering 400 would tell a caller to edit a request that was already correct, and
/// would let a genuine server fault pass unnoticed through every monitor that watches the 5xx rate.
/// Those codes are enumerated in <see cref="ApiResults.InternalFailureTokens"/> and map to 500, alongside
/// the 503 reserved for a dependency that is genuinely unreachable. The 400 default is therefore the
/// answer for a request the caller can correct and for nothing else.
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
    /// <remarks>
    /// <para>
    /// <c>in_use</c> belongs here rather than with the request-correction default. A removal refused
    /// because the thing is still referenced - a role group that still classifies a role - is a
    /// perfectly well formed request that the STATE of the resource declines, which is the definition of
    /// a conflict, and releasing the references makes the identical request succeed. Classifying it as a
    /// bad request would tell the caller to edit a request that has nothing wrong with it.
    /// </para>
    /// <para>
    /// <c>last_remaining</c> is here for exactly that reason, and its absence was a defect rather than a
    /// choice. <c>portal.last_remaining</c> refuses the removal of the only portal an installation has
    /// left; it matched no table and therefore fell to the 400 default, while the endpoint's own published
    /// description declared the refusal as a 409 and argued the case in the same terms this table does.
    /// The declared 409 was consequently unreachable and the 400 the caller actually received was
    /// undeclared - one defect with two visible halves. Adding the token makes the published contract true
    /// rather than editing the description to match a classification that contradicted the reasoning above.
    /// </para>
    /// </remarks>
    private static readonly string[] ConflictTokens =
    {
        "duplicate", "already_exists", "already_registered", "already_required", "unchanged",
        "not_different", "conflict", "in_use", "last_remaining",
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

    /// <summary>
    /// Longest detail this edge will publish from a failed outcome's own message.
    /// </summary>
    /// <remarks>
    /// Generous by design. The longest explanation this solution authors is well inside it, so the bound
    /// exists only to refuse the kind of length that an object-relational-mapper, database-client or
    /// third-party message reaches - never to trim a legitimate sentence, which would leave a caller with
    /// half an explanation and no indication that anything was removed.
    /// </remarks>
    private const int MaximumPublishedDetailLength = 512;

    /// <summary>The whole codes that mean the caller has not proved who they are.</summary>
    /// <remarks>
    /// The three approval outcomes sit here alongside the lock-out outcome, and for the same reason: each
    /// describes an account state that refuses a sign-in the credential itself did not refuse, so the
    /// request failed to authenticate rather than being malformed. Classifying them by the default arm
    /// would render them <c>400</c>, which would tell a client that its request was at fault when the
    /// request was correct and the account was not yet admissible.
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
    /// <para>
    /// Every entry names an operation that had already passed validation and authorisation when it failed,
    /// so nothing the caller could change would make the identical request succeed. The measured set of
    /// producing codes is <c>portal.creation_failed</c> and <c>role.create_failed</c> in the application
    /// services, and <c>module.content.export_failed</c>, <c>module.content.import_failed</c>,
    /// <c>module.controller.capability_probe_failed</c> and <c>module.upgrade_failed</c> in the module
    /// business-controller boundary. The tokens are matched rather than the whole codes, for the same
    /// reason every other table here does: a new code that follows the naming convention classifies
    /// correctly without this file changing.
    /// </para>
    /// <para>
    /// <c>create_failed</c> and <c>creation_failed</c> are both listed because the services genuinely
    /// disagree about which they spell, and a table that guessed one would silently misclassify the other.
    /// Neither is a prefix of the other, so neither entry is redundant.
    /// </para>
    /// <para>
    /// This table is consulted AFTER the more specific classifications above it. That ordering matters:
    /// a code such as <c>module.content.import_failed</c> is an internal fault, whereas a hypothetical
    /// <c>..._failed_not_found</c> would name a missing resource, and the narrower reading must win.
    /// </para>
    /// </remarks>
    internal static readonly string[] InternalFailureTokens =
    {
        "creation_failed", "create_failed", "export_failed", "import_failed",
        "capability_probe_failed", "upgrade_failed", "internal_error",
    };

    /// <summary>
    /// The failure code reported when a read succeeded but the thing addressed does not exist.
    /// </summary>
    /// <remarks>
    /// Stated as a constant so the problem type a client branches on is identical whichever endpoint
    /// produced it, and so that the token it carries is classified by <see cref="NotFoundTokens"/> rather
    /// than by a status code written out at the call site.
    /// </remarks>
    private const string ResourceNotFoundCode = "resource.not_found";

    /// <summary>
    /// The detail reported with a 404 produced from a successful outcome that carried no value.
    /// </summary>
    /// <remarks>
    /// Deliberately says nothing about which identifier was addressed or which resource kind was asked
    /// for. Echoing either would reflect caller-supplied text into a response body and would let an
    /// unauthorised caller distinguish "this exists but is not yours" from "this does not exist", which is
    /// the enumeration oracle every other refusal in this API is written to avoid.
    /// </remarks>
    private const string ResourceNotFoundDetail = "The requested resource does not exist.";

    /// <summary>Translates an outcome that carries no value.</summary>
    /// <param name="controller">The controller producing the response.</param>
    /// <param name="result">The outcome to translate.</param>
    /// <returns>
    /// <c>204 No Content</c> on success, because the operation completed and there is nothing to return;
    /// otherwise a problem-details payload with the mapped status.
    /// </returns>
    /// <exception cref="ArgumentNullException">Either argument is <see langword="null"/>.</exception>
    // MIGRATION: this overload deliberately does NOT return the payload-free ApiResponse companion, and the
    // reason is a requirement rather than a preference. A payload-free success here is 204, which HTTP
    // forbids from carrying a body at all, so an envelope could only be attached by demoting these responses
    // to 200 - and the acceptance criteria pin DELETE to 204 explicitly for portals, modules and users.
    // Answering 200 with an empty envelope to satisfy a type's symmetry would break a stated criterion in
    // order to tidy an unused declaration, which is the wrong trade in both directions.
    //
    // Consequence, stated plainly so it is not mistaken for an oversight: ApiResponse - the non-generic,
    // payload-free arity - has no controller consumer in this API and cannot have one while 204 is the
    // correct answer for a command that returns nothing. It remains declared beside its generic form as the
    // documented shape for a payload-free 200, should an endpoint ever legitimately need one. Recorded in
    // the repository migration notes rather than resolved by forcing a wrong status code.
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
    /// <c>200 OK</c> carrying an <see cref="ApiResponse{T}"/> around the value on success;
    /// <c>404 Not Found</c> carrying a problem-details payload when the outcome succeeded but carries no
    /// value, because a nullable value on a successful outcome is how this solution expresses "asked, and
    /// it is not there"; otherwise a problem-details payload with the mapped status.
    /// </returns>
    /// <exception cref="ArgumentNullException">Either argument is <see langword="null"/>.</exception>
    /// <remarks>
    /// The not-found answer is built by the same factory as every other failure, so it carries the
    /// documented <c>type</c>, <c>title</c>, <c>detail</c> and <c>traceId</c> members rather than an empty
    /// body. A bodyless 404 was the previous behaviour and it contradicted the response type every action
    /// declares: a client written against the published envelope received a payload on every failure except
    /// this one, and no failure announced itself.
    /// </remarks>
    // MIGRATION: the envelope is applied HERE, at the one translation point every read and every update
    // already flowed through, rather than at each of the sixty-odd call sites. That is what makes the wire
    // contract uniform by construction instead of by convention: an action cannot opt out of the envelope
    // without abandoning this helper, and no call site had to change to adopt it. Before this change the
    // helper returned the payload bare, so ApiResponse<T> had no consumer anywhere in the solution while
    // its own documentation stated that every controller returns that shape on success - a contract the
    // code contradicted rather than implemented.
    //
    // MIGRATION: the envelope wraps the payload and adds nothing else. It carries no outcome flag, no
    // failure member and no transport-level status code, because a failure is already an RFC 7807 document
    // with a failing status line; a body claiming failure alongside a 200 is the exact ambiguity that
    // standard exists to remove. The metadata companion is left absent for a single value - it describes a
    // page, and there is no page here.
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

    /// <summary>
    /// Translates an outcome that carries a domain page into the shared paging envelope.
    /// </summary>
    /// <typeparam name="TItem">The element type of the page.</typeparam>
    /// <param name="controller">The controller producing the response.</param>
    /// <param name="result">The outcome to translate.</param>
    /// <returns>
    /// <c>200 OK</c> carrying a <see cref="PagedResponse{T}"/> on success; otherwise a problem-details
    /// payload with the mapped status.
    /// </returns>
    /// <exception cref="ArgumentNullException">Either argument is <see langword="null"/>.</exception>
    /// <remarks>
    /// A page is deliberately NOT wrapped in <see cref="ApiResponse{T}"/> as well. Two envelopes around one
    /// payload would make a client unwrap twice and would put the paging facts one level deeper than the
    /// records they describe, whereas <see cref="PagedResponse{T}"/> already pairs the records with an
    /// <see cref="ApiMeta"/> companion - it IS the success envelope for a collection, not a payload that
    /// needs one.
    /// </remarks>
    // MIGRATION: this overload exists to make the wrong shape UNEXPRESSIBLE, not merely discouraged. A
    // domain page is a Result<PagedResult<TItem>>, which the general helper above would happily accept with
    // TValue bound to PagedResult<TItem> - and that is precisely the layering breach this repair removes,
    // because it would serialise a Domain type, nested one level deeper than before. Overload resolution
    // prefers this member for that argument, since Result<PagedResult<TItem>> is more specific than
    // Result<TValue>, so every existing call site adopts the projection without being edited and a reader
    // cannot accidentally get the other one. Producing the domain page on the wire now requires writing the
    // type argument out by hand, which is visible in review.
    //
    // MIGRATION: the projection is PagedResponse<T>.From, which owns the one reshaping decision involved -
    // a domain page reports "unpaged" as a page size of zero, and the wire form reports the total instead,
    // so a client that divides to derive a page count is never handed a zero divisor for a response that
    // plainly holds records. That decision belongs to the Application layer's own projection and is
    // deliberately not restated here.
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
            // A successful listing must produce a page, even an empty one. Reaching here means a service
            // returned success with no envelope at all, which is a defect in that service rather than a
            // condition a caller can act on, so it is surfaced as an unexpected failure rather than
            // silently answered with an empty page a client would read as "nothing matched".
            throw new InvalidOperationException(
                "A listing reported success but produced no page to return.");
        }

        return controller.Ok(PagedResponse<TItem>.From(page));
    }

    /// <summary>
    /// Builds the problem-details payload reported when a successful outcome carried no value.
    /// </summary>
    /// <param name="controller">The controller producing the response.</param>
    /// <returns>A <c>404 Not Found</c> problem-details response.</returns>
    /// <remarks>
    /// Exposed so that an action which establishes absence for itself - rather than by reading a
    /// <c>Result</c> - answers with the identical payload instead of a bare <c>NotFound()</c>.
    /// </remarks>
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
    /// Builds the problem-details payload reported when an authenticated caller is not permitted to
    /// perform an operation.
    /// </summary>
    /// <param name="controller">The controller producing the response.</param>
    /// <param name="code">
    /// The failure code identifying the refusal, so a client can branch on it without parsing prose.
    /// </param>
    /// <returns>A <c>403 Forbidden</c> problem-details response.</returns>
    /// <remarks>
    /// The detail is fixed and names neither the missing grant nor the resource. A refusal that explained
    /// itself would tell an unauthorised caller which identifiers exist and which privilege to acquire,
    /// and it would differ from the refusal the authorisation middleware produces for the same cause -
    /// which is precisely the drift that made the two paths distinguishable before.
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
    /// <para>
    /// WHY THIS EXISTS SEPARATELY FROM <see cref="Complete{TValue}(ControllerBase, Result{TValue})"/>. The generic member returns the
    /// outcome's value AS IT STANDS, which for a paged outcome means serialising
    /// <see cref="PagedResult{T}"/> - a DOMAIN type - straight onto the wire. That breaks the DTO boundary
    /// this solution is built on in a way no compiler notices: the response happens to look reasonable, so
    /// nothing fails, and the domain's internal shape silently becomes a published contract that cannot then
    /// be changed without breaking every client. It also left the mandated wire envelope unreachable.
    /// </para>
    /// <para>
    /// Projecting HERE rather than in each action is deliberate. Five actions across four controllers return
    /// a page, and a projection repeated five times is a projection four of them can forget or spell
    /// differently. One member means the envelope is applied by construction, and a new paged endpoint that
    /// calls the wrong translator returns the wrong TYPE - which the declared
    /// <c>ProducesResponseType</c> and the compiler both object to - rather than the wrong shape.
    /// </para>
    /// <para>
    /// A successful outcome carrying no page is refused rather than answered with <c>404</c>, which is where
    /// this member deliberately differs from <see cref="Complete{TValue}(ControllerBase, Result{TValue})"/>. Absence is meaningful for a
    /// single resource - "asked, and it is not there" - but a listing that matched nothing is an EMPTY PAGE,
    /// not a missing one, and every service in this solution returns exactly that. A null page therefore
    /// means the service is defective, and answering <c>404</c> would present a defect as an ordinary
    /// outcome and teach clients to treat "no results" as "endpoint not found".
    /// </para>
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
    /// <para>
    /// The location is derived from the request path rather than from a named route, because every creation
    /// endpoint in this API posts to a collection whose member address is that path plus the identifier. A
    /// named-route lookup would add a second place the address is spelled, and a mismatch between them fails
    /// only at run time and only in the header.
    /// </para>
    /// <para>
    /// THE PATH BASE IS PART OF THAT ADDRESS AND OMITTING IT NAMES THE WRONG TENANT. A child portal is
    /// addressed by a path segment beneath a shared host name, and
    /// <see cref="Middleware.TenantPathBaseMiddleware"/> moves that segment out of the routable path and into
    /// the path base before routing runs - it has to, or the request matches no route at all. By the time an
    /// action returns, therefore, the address the caller actually posted to is the path base FOLLOWED BY the
    /// path, and the path alone is the parent's address rather than the child's. Composing from the path alone
    /// handed every child-tenant creation a location pointing at the shared host's root, which resolves to the
    /// PARENT tenant; following it reached a route naming a resource the parent does not own and was refused.
    /// The defect was invisible to the creating caller, who received a correct <c>201</c> and a correct body,
    /// and only surfaced for whoever followed the header.
    /// </para>
    /// <para>
    /// Both halves are taken in their URI form rather than their decoded one, because this value is written
    /// into a header: a path segment carrying a space or a reserved character must reach the caller encoded, and
    /// the decoded property would emit it raw. The identifier needs no such treatment - every creation endpoint
    /// projects an integer key - so it is formatted invariantly and appended as-is.
    /// </para>
    /// </remarks>
    // MIGRATION: a creation answers with the SAME envelope a read answers with, which is the whole point of
    // having one. A client that posts and then re-reads the resource unwraps one shape in both directions,
    // and the location header - not a differently shaped body - is what distinguishes the two responses.
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
            // A creation that reports success must produce a representation. Reaching here means a service
            // returned a successful outcome with no value, which is a defect in that service rather than a
            // condition the caller can act on, so it is surfaced as an unexpected failure.
            throw new InvalidOperationException(
                "A creation reported success but produced no representation to return.");
        }

        // MIGRATION: the collection address is the PATH BASE plus the PATH, not the path alone. A tenant
        // addressed by a path segment beneath a shared host has that segment in the path base by the time an
        // action runs, so the path alone spells the parent's address and a child's creation would be located
        // under the wrong tenant.
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
    /// Before this member existed, that action assembled a <see cref="ProblemDetails"/> by hand, which
    /// bypassed the shared factory and so omitted the trace identifier and the problem type that every
    /// other failure in this API carries. Exposing the translation is what removes the incentive to
    /// re-create it.
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
    /// <remarks>
    /// The failure code travels as the problem type so a client can branch on it without parsing prose, and
    /// the message travels as the detail. Neither carries a stack trace or an identifier the caller was not
    /// already entitled to see.
    /// </remarks>
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
    /// <para>
    /// <b>This is defence in depth and not a substitute for authoring safe messages.</b> Every message a
    /// service places on a failed outcome is meant to be caller-safe by construction, because this method
    /// publishes it verbatim as the RFC 7807 <c>detail</c>. A security review found one place where that
    /// intention had not held: the module-lifecycle factory placed a third-party module's own
    /// <c>Exception.Message</c> on its failed outcomes, on the reasonable-sounding premise that the
    /// underlying explanation should survive for the caller to log - and this method then published it to
    /// an HTTP client, complete with whatever connection string, path, statement or content the module had
    /// quoted. That source is fixed at source.
    /// </para>
    /// <para>
    /// The guard below exists because fixing the instance does not close the class. Any future author may
    /// reach for the same premise, and nothing about a <c>string</c> announces where it came from. So the
    /// two shapes that an exception's text has and an authored sentence does not are refused here: a
    /// multi-line value, which is what a stack trace or an aggregated failure looks like, and a value
    /// longer than any explanation this solution authors. Neither test can be satisfied by a legitimate
    /// message - every authored detail in this solution is one short single-line sentence - so the guard
    /// costs nothing and cannot mask a correct message.
    /// </para>
    /// <para>
    /// It is deliberately NOT a general redactor. It cannot tell whether a short single-line message
    /// quotes something it should not, and pretending otherwise would invite exactly the complacency that
    /// produced the finding. The rule remains that a failed outcome's message must be authored text; this
    /// is the backstop for the case where it is not.
    /// </para>
    /// </remarks>
    private static string SafeDetail(string? message)
    {
        // Unreachable through the public contract, and kept anyway so this method is total. A reason
        // refuses a blank message by construction and a failed outcome always carries a reason, so an
        // absent explanation cannot be built today - but this method's guarantee is about what it
        // publishes, and a guarantee that depends on a sibling type's invariant is weaker than one that
        // does not.
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

    /// <summary>
    /// Reports whether a message contains a word that names a CLR exception type.
    /// </summary>
    /// <param name="message">The message a failed outcome carried; never blank.</param>
    /// <returns>
    /// <see langword="true"/> when any whitespace-delimited word ends in <c>Exception</c>, ignoring trailing
    /// punctuation.
    /// </returns>
    /// <remarks>
    /// <para>
    /// THE THIRD REFUSAL, ADDED BECAUSE THE FIRST TWO DID NOT CATCH THE CASE THAT ACTUALLY OCCURRED. A
    /// service caught a credential-store failure and put <c>exception.GetType().Name</c> into the message on
    /// its failed outcome, reasoning that the underlying cause should survive for a caller to report. The
    /// value it produced was one short single line, so both existing tests passed it, and this edge then
    /// published the internal type of a component the caller has no relationship with as the RFC 7807
    /// <c>detail</c> - naming, in that instance, the database client. That source is fixed AT SOURCE, which
    /// is where the rule belongs; this test closes the CLASS, because the premise is a reasonable-sounding
    /// one that a future author will reach for again and nothing about a <see cref="string"/> announces
    /// where it came from.
    /// </para>
    /// <para>
    /// The test is a word test rather than a substring test, so a legitimate sentence containing the word
    /// "exception" in prose is unaffected: only a word ENDING in <c>Exception</c> matches, which is the .NET
    /// naming convention for the type family and a shape no authored explanation in this solution uses.
    /// Trailing punctuation is trimmed first because the offending value ended in a full stop, so a test
    /// that compared the raw word would have missed the very instance that prompted it.
    /// </para>
    /// <para>
    /// LIKE THE OTHER TWO, THIS IS DEFENCE IN DEPTH AND NOT A REDACTOR. It cannot tell whether a short
    /// single-line message quotes something it should not; what it can do is refuse the one shape that is
    /// never authored and is always internal. A message it refuses is replaced wholesale by
    /// <see cref="UnauthoredDetail"/> rather than edited, because a partially rewritten explanation is one
    /// nobody wrote.
    /// </para>
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
        // conditions a caller can act on, and one of those readings must win where both could match. What
        // reaches here having a failure token is an operation that had already been validated and
        // authorised when it failed, which is a server fault.
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
    /// <para>
    /// Visible to the rest of the API assembly so that a refusal decided outside a controller - by the
    /// authorisation middleware result handler - carries a problem type built by this same method, rather
    /// than a second spelling of the same convention.
    /// </para>
    /// </remarks>
    internal static string BuildProblemType(string code)
    {
        return FormattableString.Invariant($"urn:dnnmigration:error:{Normalise(code)}");
    }
}
