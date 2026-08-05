using DnnMigration.Api.Middleware;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.Extensions.Logging;
using Xunit;

namespace DnnMigration.IntegrationTests.Api;

/// <summary>
/// Covers what the per-request completion entry is allowed to contain (SEC-024).
/// </summary>
/// <remarks>
/// <para>
/// THE ENTRY THIS SUITE INSPECTS IS THE ONLY ONE WRITTEN FOR EVERY REQUEST, which is what makes its content a
/// security property rather than a matter of taste. It previously carried two things it must not. The RAW
/// REQUEST PATH, whose route values are account, portal and module identifiers - and, for a mistyped address,
/// arbitrary caller text. And the EXCEPTION OBJECT, which is the sharper of the two: a logging sink renders an
/// exception argument as its message and full stack trace, so passing it reinstated, at information level and
/// in a log whose retention and access the application does not control, precisely the detail the global
/// exception handler takes care to redact before it reaches a caller.
/// </para>
/// <para>
/// THE STAGE IS EXERCISED DIRECTLY RATHER THAN THROUGH A HOST, and deliberately. The property under test is
/// what arguments the stage hands its logger, and the application configures a structured logging pipeline
/// that owns its own providers - so a host-based fact would be asserting the sink's rendering as much as the
/// stage's arguments, and could not observe the exception argument at all. Driving the stage with a fake
/// logger observes exactly the two things that matter: the rendered message, and whether an exception object
/// was passed.
/// </para>
/// <para>
/// No database, no server and no fixture: this suite is deliberately independent of both.
/// </para>
/// </remarks>
[Trait("Category", "Integration")]
public sealed class RequestLoggingRedactionTests
{
    /// <summary>The route template of the endpoint these facts pretend routing selected.</summary>
    private const string RouteTemplate = "api/v1/portals/{portalId}/users/{userId}";

    /// <summary>The concrete path the caller addressed, carrying two identifiers.</summary>
    private const string ConcretePath = "/api/v1/portals/3/users/57";

    /// <summary>The value recorded in the route position when no endpoint was selected.</summary>
    /// <remarks>
    /// Stated as the same literal RequestLoggingContractTests states, because it is one observable value:
    /// an operator filtering the log on unmatched requests filters on this exact text.
    /// </remarks>
    private const string UnmatchedRouteTemplate = "(no matched endpoint)";

    /// <summary>
    /// A completed request is described by its ROUTE TEMPLATE, and the identifiers it addressed do not reach
    /// the entry.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task CompletionEntry_NamesTheRouteTemplate_NotTheAddressedIdentifiers()
    {
        var logger = new CapturingLogger();
        var middleware = new RequestLoggingMiddleware(static _ => Task.CompletedTask, logger);
        (DefaultHttpContext context, CompletionCapture completion) = BuildContext();

        await middleware.InvokeAsync(context);
        await completion.RunCallbacksAsync();

        CapturedEntry entry = logger.Entries.Should().ContainSingle().Subject;

        entry.Message.Should().Contain(
            RouteTemplate,
            "the template names the operation, which is what an operator needs");
        entry.Message.Should().NotContain(
            ConcretePath,
            "the concrete path deposits the addressed portal and account identifiers into the log");
        entry.Message.Should().Contain(
            "Failure: none",
            "the classification is present on every entry so a query can filter on one value");
        entry.Exception.Should().BeNull("nothing failed");
    }

    /// <summary>
    /// A failed request records the failure's TYPE and nothing else about it: no message, no stack trace, and
    /// no exception object for a sink to expand.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task CompletionEntry_RecordsOnlyTheFailureType_AndNeverTheExceptionObject()
    {
        const string SecretDetail = "Server=db-01;User Id=sa;Password=hunter2";

        var logger = new CapturingLogger();
        var middleware = new RequestLoggingMiddleware(
            _ => throw new UnauthorizedAccessException(SecretDetail),
            logger);

        (DefaultHttpContext context, CompletionCapture completion) = BuildContext();

        Func<Task> act = async () => await middleware.InvokeAsync(context);

        await act.Should().ThrowAsync<UnauthorizedAccessException>(
            "the stage records the outcome and re-throws, so the caller still receives the error response");

        // The entry is written when the RESPONSE completes, not when the stage returns, which is how a status
        // written by a later stage - the exception handler's, here - is the one recorded. In the composed
        // pipeline the server raises this; a directly constructed context has to be completed by the test.
        await completion.RunCallbacksAsync();

        CapturedEntry entry = logger.Entries.Should().ContainSingle().Subject;

        entry.Message.Should().Contain(
            nameof(UnauthorizedAccessException),
            "the type classifies the fault and is authored text of this application");
        entry.Message.Should().NotContain(
            SecretDetail,
            "an exception message is composed at throw time from whatever the failing operation held");
        entry.Exception.Should().BeNull(
            "passing the exception object makes a sink render its message and full stack trace, undoing the "
            + "redaction the caller-facing handler performs");
    }

    /// <summary>
    /// A request that matched no endpoint is reported as unmatched rather than by its path, because that is
    /// the one case in which the path is arbitrary caller-supplied text.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task CompletionEntry_ReportsAnUnmatchedRequest_WithoutEchoingItsPath()
    {
        const string HostilePath = "/api/v1/<script>alert(1)</script>";

        var logger = new CapturingLogger();
        var middleware = new RequestLoggingMiddleware(static _ => Task.CompletedTask, logger);

        var context = new DefaultHttpContext();
        var completion = new CompletionCapture();
        context.Features.Set<IHttpResponseFeature>(completion);
        context.Request.Method = HttpMethods.Get;
        context.Request.Path = HostilePath;
        context.Response.StatusCode = StatusCodes.Status404NotFound;

        await middleware.InvokeAsync(context);
        await completion.RunCallbacksAsync();

        CapturedEntry entry = logger.Entries.Should().ContainSingle().Subject;

        entry.Message.Should().NotContain("script", "an unmatched path is unvalidated caller input");
        entry.Message.Should().Contain(
            UnmatchedRouteTemplate,
            "the marker is the shared one the envelope contract asserts, so both suites describe one value");
    }

    /// <summary>Builds a request that routing has already matched to a two-identifier endpoint.</summary>
    /// <returns>The context to hand the stage.</returns>
    private static (DefaultHttpContext Context, CompletionCapture Completion) BuildContext()
    {
        var context = new DefaultHttpContext();
        var completion = new CompletionCapture();
        context.Features.Set<IHttpResponseFeature>(completion);
        context.Request.Method = HttpMethods.Get;
        context.Request.Path = ConcretePath;
        context.Request.QueryString = new QueryString("?query=someone%40example.com");
        context.Response.StatusCode = StatusCodes.Status200OK;

        // What routing publishes on the context once it has selected an endpoint. The stage reads it after the
        // remainder of the pipeline has run, which is why assigning it up front models the real sequence.
        context.SetEndpoint(new RouteEndpoint(
            static _ => Task.CompletedTask,
            RoutePatternFactory.Parse(RouteTemplate),
            order: 0,
            new EndpointMetadataCollection(),
            displayName: "Portal users"));

        return (context, completion);
    }

    /// <summary>A response feature that remembers its completion callbacks so a test can raise them.</summary>
    /// <remarks>
    /// The framework's default in-memory response feature accepts completion callbacks and never invokes them,
    /// because nothing completes a response that no server is writing. The stage under test records its entry
    /// from that callback - deliberately, so the status it reports is the one actually answered - so a fact
    /// built on a bare context observed no entry at all and read as though the stage had stopped logging.
    /// </remarks>
    private sealed class CompletionCapture : IHttpResponseFeature
    {
        private readonly List<Func<Task>> _callbacks = [];

        public Stream Body { get; set; } = Stream.Null;

        public bool HasStarted { get; private set; }

        public IHeaderDictionary Headers { get; set; } = new HeaderDictionary();

        public string? ReasonPhrase { get; set; }

        public int StatusCode { get; set; } = StatusCodes.Status200OK;

        public void OnCompleted(Func<object, Task> callback, object state)
        {
            ArgumentNullException.ThrowIfNull(callback);

            _callbacks.Add(() => callback(state));
        }

        public void OnStarting(Func<object, Task> callback, object state)
        {
            ArgumentNullException.ThrowIfNull(callback);
        }

        /// <summary>Raises every registered completion callback, in registration order.</summary>
        /// <returns>A task that completes once every callback has run.</returns>
        public async Task RunCallbacksAsync()
        {
            HasStarted = true;

            foreach (Func<Task> callback in _callbacks)
            {
                await callback().ConfigureAwait(false);
            }
        }
    }

    /// <summary>One captured entry, reduced to the two parts these facts assert on.</summary>
    /// <param name="Message">The rendered message.</param>
    /// <param name="Exception">The exception the producer passed, if any.</param>
    private sealed record CapturedEntry(string Message, Exception? Exception);

    /// <summary>A logger that records what it is handed and renders nothing anywhere.</summary>
    private sealed class CapturingLogger : ILogger<RequestLoggingMiddleware>
    {
        /// <summary>Gets the entries written so far, in order.</summary>
        public List<CapturedEntry> Entries { get; } = [];

        /// <inheritdoc />
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        /// <inheritdoc />
        public bool IsEnabled(LogLevel logLevel) => true;

        /// <inheritdoc />
        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            ArgumentNullException.ThrowIfNull(formatter);

            Entries.Add(new CapturedEntry(formatter(state, exception), exception));
        }
    }
}
