using DnnMigration.Api.ErrorHandling;
using DnnMigration.Domain.Abstractions.Services;
using DnnMigration.Domain.Common;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace DnnMigration.IntegrationTests.Api;

/// <summary>
/// Proves that a failure to reach the store is answered <c>503</c> with a retry hint rather than
/// <c>500</c>, that everything else keeps the status it had, and that a genuinely unreachable SQL Server
/// is recognised as such.
/// </summary>
/// <remarks>
/// <para>
/// MIGRATION: measured on the delivered container topology with the database container stopped. Every data
/// endpoint, sign-in included, answered <c>500 Internal Server Error</c> with a well-formed problem document
/// that disclosed nothing. Nothing was leaking and the readiness view already reported the outage as
/// <c>503</c> to an orchestrator, so the defect was in the instruction the status carried: <c>500</c> tells a
/// caller this server is broken and asks them to report it, while the truth was that a dependency was down
/// and the same request would succeed shortly.
/// </para>
/// <para>
/// <strong>Asserted on the handler's own surface, not over HTTP, and for a concrete reason.</strong>
/// Provoking the condition end to end would mean taking the test database away from a live host mid-suite,
/// which would fail every other fact in the run for reasons unrelated to what is under test and would leave
/// the outcome dependent on the order tests happened to execute in. The mapping is made in exactly one place
/// - the handler - so that is where it is measured, with the classifier substituted so both of its answers
/// can be exercised. What the substitution cannot prove is that a real outage produces an exception the real
/// classifier recognises, so the last fact here dials an address nothing listens on and asks the
/// CONTAINER-RESOLVED classifier about the exception the client actually raises. Between them the mapping and
/// the recognition are both covered, and neither stands in for the other.
/// </para>
/// <para>
/// The 500 case is asserted alongside the 503 case deliberately. The risk this change carries is not that an
/// outage stays 500 - that was already the behaviour - but that a DEFECT starts being answered 503, telling a
/// caller to retry something that can never succeed and burying a fault behind a retry loop.
/// </para>
/// </remarks>
[Trait("Category", "Integration")]
[Collection(IntegrationTestCollection.Name)]
public sealed class StoreUnavailabilityMappingTests
{
    private readonly ApiTestFixture _fixture;

    /// <summary>Initialises a new instance of the <see cref="StoreUnavailabilityMappingTests"/> class.</summary>
    /// <param name="fixture">
    /// The shared composed host, used by the last fact to resolve the classifier the application actually
    /// registered rather than one this test constructed.
    /// </param>
    public StoreUnavailabilityMappingTests(ApiTestFixture fixture) => _fixture = fixture;

    /// <summary>Explanation the caller must receive when a dependency could not serve.</summary>
    /// <remarks>
    /// Restated here as a literal rather than referenced from the handler, so a change to the published
    /// wording has to be made deliberately in two places. It names no component: which dependency failed is
    /// an infrastructure fact of no use to a caller and of obvious use to someone deciding what to probe.
    /// </remarks>
    private const string StoreUnavailableDetail =
        "A service this request depends on is temporarily unavailable. Retry after a short delay, "
        + "and quote the X-Correlation-Id response header if the problem persists.";

    /// <summary>Explanation published for a failure that is a defect rather than an outage.</summary>
    private const string UnexpectedFailureDetail =
        "An unexpected error occurred while processing the request. Quote the "
        + "X-Correlation-Id response header when reporting this problem.";

    /// <summary>
    /// A failure the classifier identifies as a store outage is answered 503 with a retry hint, explained
    /// without naming the store, and still recorded as a fault.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    [Trait("Category", "Integration")]
    public async Task AStoreOutage_IsAnswered503WithARetryHint()
    {
        using Harness harness = Harness.Create(storeIsUnavailable: true);

        // A perfectly ordinary exception object. The point of the arrangement is that the DECISION comes from
        // the classifier rather than from the exception's type, because the transport references no database
        // provider and so cannot recognise a provider fault by type at all.
        Exception failure = new InvalidOperationException("the store could not be reached");

        bool handled = await harness.Handler.TryHandleAsync(harness.Context, failure, CancellationToken.None);

        handled.Should().BeTrue("the handler wrote a payload, so it owns the response");

        harness.Context.Response.StatusCode.Should().Be(
            StatusCodes.Status503ServiceUnavailable,
            "a dependency being unavailable is not this server having a defect, and the status is the only "
            + "part of the answer a client and an orchestrator both act on");

        harness.Context.Response.Headers.RetryAfter.ToString().Should().Be(
            "5",
            "a status that says come back later and declines to say when leaves a client to invent an "
            + "interval, and the interval it invents is usually 'immediately'");

        harness.Written.Should().NotBeNull();
        harness.Written!.Status.Should().Be(StatusCodes.Status503ServiceUnavailable);
        harness.Written.Detail.Should().Be(StoreUnavailableDetail);
        harness.Written.Detail.Should().NotContain(
            "the store could not be reached",
            "no exception message may be republished, because a provider message routinely carries the "
            + "server name, the database name and the values bound to a statement");

        harness.Records.Entries.Should().Contain(
            entry => entry.Level == LogLevel.Error,
            "an outage is still a 5xx and must still raise whatever a deployment alerts on; only the status "
            + "the caller receives changes");
    }

    /// <summary>A failure the classifier does not recognise keeps its 500.</summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    [Trait("Category", "Integration")]
    public async Task AFailureThatIsNotAnOutage_IsStillAnswered500AndCarriesNoRetryHint()
    {
        using Harness harness = Harness.Create(storeIsUnavailable: false);

        bool handled = await harness.Handler.TryHandleAsync(
            harness.Context,
            new InvalidOperationException("a defect in this application"),
            CancellationToken.None);

        handled.Should().BeTrue();

        harness.Context.Response.StatusCode.Should().Be(
            StatusCodes.Status500InternalServerError,
            "a defect answered 503 tells the caller to retry a request that can never succeed, which is "
            + "strictly worse than reporting the fault");

        harness.Context.Response.Headers.RetryAfter.Should().BeEmpty(
            "there is nothing to come back for, and advertising a retry would invite a request that will "
            + "fail identically");

        harness.Written.Should().NotBeNull();
        harness.Written!.Detail.Should().Be(UnexpectedFailureDetail);
    }

    /// <summary>
    /// A cache production timeout is answered 500 with no retry hint, even when the store classifier would
    /// call everything an outage.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// <para>
    /// THE THIRD PART OF A THREE-PART FIX, AND THE PART THAT PROVES THE OTHER TWO ARE WIRED UP. The
    /// in-process cache used to signal an expired single-flight budget with a plain
    /// <see cref="TimeoutException"/>, and the store classifier used to read that bare type as the database
    /// being unreachable. A cache defect - a producer that hangs, a budget set too low, a lock held too long -
    /// therefore reached the caller as <c>503</c> with a retry hint pointing at a database that was in fact
    /// perfectly healthy, and every retry hit the same hanging producer. The cache now raises a type of its
    /// own and the classifier now requires structural provider or mapper context, so neither half can produce
    /// that answer any more.
    /// </para>
    /// <para>
    /// The arrangement is deliberately hostile: the classifier is substituted to answer YES for every
    /// failure. If the cache arm did not sit ABOVE the outage guard in the handler's description, this test
    /// would see 503 - which makes the assertion a test of the ARM ORDER, not merely of the type. That order
    /// is load-bearing and invisible in a diff, so it is pinned here rather than left to a comment.
    /// </para>
    /// </remarks>
    [Fact]
    [Trait("Category", "Integration")]
    public async Task ACacheProductionTimeout_IsAnswered500EvenWhenTheClassifierWouldSayOutage()
    {
        using Harness harness = Harness.Create(storeIsUnavailable: true);

        bool handled = await harness.Handler.TryHandleAsync(
            harness.Context,
            new CacheProductionTimeoutException(
                "A cached value took longer than the shared production budget to produce."),
            CancellationToken.None);

        handled.Should().BeTrue();

        harness.Context.Response.StatusCode.Should().Be(
            StatusCodes.Status500InternalServerError,
            "a cache producer that did not finish in its budget is this application's defect, and answering "
            + "it as a dependency outage tells the caller to retry into the same hanging producer while "
            + "pointing the investigation at a database that never failed");

        harness.Context.Response.Headers.RetryAfter.Should().BeEmpty(
            "there is no interval after which a hung producer becomes healthy, so there is nothing to "
            + "advertise");

        harness.Written.Should().NotBeNull();
        harness.Written!.Status.Should().Be(StatusCodes.Status500InternalServerError);
        harness.Written.Detail.Should().Be(UnexpectedFailureDetail);
        harness.Written.Detail.Should().NotContain(
            "production budget",
            "the cache's own wording is an implementation detail of this process");

        harness.Records.Entries.Should().Contain(
            entry => entry.Level == LogLevel.Error,
            "the condition is still a fault this deployment must see; only the status and the retry advice "
            + "change");
    }

    /// <summary>
    /// A failure already classified by the layer that owns it keeps that classification, whatever the store
    /// classifier says.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    [Trait("Category", "Integration")]
    public async Task AnAlreadyClassifiedFailure_IsNotReclassifiedAsAnOutage()
    {
        // The classifier is told to answer yes for everything, so any arm that ran after it would answer 503.
        using Harness harness = Harness.Create(storeIsUnavailable: true);

        bool handled = await harness.Handler.TryHandleAsync(
            harness.Context,
            new DomainException("an invariant was broken"),
            CancellationToken.None);

        handled.Should().BeTrue();

        harness.Context.Response.StatusCode.Should().Be(
            StatusCodes.Status400BadRequest,
            "the outage arm sits BELOW every specific arm, so a request that is itself at fault is still "
            + "reported as the caller's problem rather than as an infrastructure condition");

        harness.Context.Response.Headers.RetryAfter.Should().BeEmpty(
            "a request that will be rejected on the same grounds every time must not be advertised as "
            + "retryable");
    }

    /// <summary>
    /// The classifier the application actually runs recognises a genuinely unreachable SQL Server.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// This is the fact that keeps the substituted classifier in the tests above honest, and the one the
    /// fabricated-severity unit facts cannot establish: it asks the client to reach an address nothing listens
    /// on and hands the REAL exception to the REAL implementation, resolved from the composition root rather
    /// than constructed here. A loopback port with no listener is refused immediately, so no timeout is waited
    /// out and nothing outside this host is contacted.
    /// <para>
    /// ⚠ THE PORT IS OBTAINED FROM THE OPERATING SYSTEM, NOT SPELLED HERE. This fact used to name a fixed high
    /// port and assert, in effect, that nothing on the machine had bound it - an assertion this suite is in no
    /// position to make, because several clones of this repository build and test in parallel on one host. A
    /// collision would not fail loudly either: a process that ACCEPTED the connection would turn "refused
    /// immediately" into a handshake with something unrelated, and this fact would then report whatever that
    /// something did. <see cref="RefusedEndpoint"/> asks the kernel for a free ephemeral port and releases it,
    /// so the refusal is a property the kernel established rather than one this file hoped for.
    /// </para>
    /// </remarks>
    [Fact]
    [Trait("Category", "Integration")]
    public async Task TheRealClassifier_RecognisesAnUnreachableServer()
    {
        Exception? raised = null;

        try
        {
            await using SqlConnection connection = new(
                RefusedEndpoint.ConnectionString("DnnMigrationProbe"));

            await connection.OpenAsync();
        }
        catch (Exception exception)
        {
            raised = exception;
        }

        raised.Should().NotBeNull(
            "nothing listens on the probe port, so the client must have failed to open a connection");

        // Resolved from the application's own container, so this measures the registration as well as the
        // rule: a classifier that was never registered, or registered against a different contract, fails
        // here rather than silently leaving every outage answered 500 in production.
        using ScopedServices services = _fixture.CreateScopedServices();
        IStoreFailureClassifier classifier = services.Resolve<IStoreFailureClassifier>();

        classifier.IsStoreUnavailable(raised).Should().BeTrue(
            "an unreachable server is the commonest outage of all, and it is reported with error number 0, "
            + "so a rule keyed on recognisable error numbers would have closed nothing");
    }

    /// <summary>Everything one handler exercise needs, assembled once.</summary>
    private sealed class Harness : IDisposable
    {
        private Harness(
            GlobalExceptionHandler handler,
            DefaultHttpContext context,
            RecordingLoggerProvider records,
            Func<ProblemDetails?> written,
            ILoggerFactory factory)
        {
            Handler = handler;
            Context = context;
            Records = records;
            WrittenAccessor = written;
            Factory = factory;
        }

        /// <summary>Gets the handler under test.</summary>
        public GlobalExceptionHandler Handler { get; }

        /// <summary>Gets the request context the handler answers.</summary>
        public DefaultHttpContext Context { get; }

        /// <summary>Gets the recorded log entries.</summary>
        public RecordingLoggerProvider Records { get; }

        /// <summary>Gets the payload the handler wrote, if any.</summary>
        public ProblemDetails? Written => WrittenAccessor();

        private Func<ProblemDetails?> WrittenAccessor { get; }

        private ILoggerFactory Factory { get; }

        /// <inheritdoc />
        public void Dispose() => Factory.Dispose();

        /// <summary>Assembles a harness whose classifier answers as instructed.</summary>
        /// <param name="storeIsUnavailable">The answer the substituted classifier gives.</param>
        /// <returns>The harness.</returns>
        public static Harness Create(bool storeIsUnavailable)
        {
            RecordingLoggerProvider records = new();

            ILoggerFactory factory = LoggerFactory.Create(builder =>
            {
                builder.SetMinimumLevel(LogLevel.Trace);
                builder.AddProvider(records);
            });

            Mock<ProblemDetailsFactory> problemFactory = new(MockBehavior.Strict);
            Mock<IProblemDetailsService> problemService = new(MockBehavior.Strict);

            ProblemDetails? written = null;

            problemFactory
                .Setup(created => created.CreateProblemDetails(
                    It.IsAny<HttpContext>(),
                    It.IsAny<int?>(),
                    It.IsAny<string?>(),
                    It.IsAny<string?>(),
                    It.IsAny<string?>(),
                    It.IsAny<string?>()))
                .Returns((HttpContext _, int? status, string? title, string? type, string? detail, string? instance) =>
                    new ProblemDetails
                    {
                        Status = status,
                        Title = title,
                        Type = type,
                        Detail = detail,
                        Instance = instance,
                    });

            problemService
                .Setup(service => service.TryWriteAsync(It.IsAny<ProblemDetailsContext>()))
                .Returns((ProblemDetailsContext context) =>
                {
                    written = context.ProblemDetails;
                    return ValueTask.FromResult(true);
                });

            Mock<IStoreFailureClassifier> storeFailures = new(MockBehavior.Strict);
            storeFailures
                .Setup(classifier => classifier.IsStoreUnavailable(It.IsAny<Exception?>()))
                .Returns(storeIsUnavailable);

            GlobalExceptionHandler handler = new(
                problemService.Object,
                problemFactory.Object,
                storeFailures.Object,
                factory.CreateLogger<GlobalExceptionHandler>());

            DefaultHttpContext context = new();
            context.Request.Method = HttpMethods.Get;

            return new Harness(handler, context, records, () => written, factory);
        }
    }

    /// <summary>Captures every entry written through a logger factory, with its level.</summary>
    private sealed class RecordingLoggerProvider : ILoggerProvider
    {
        private readonly List<(LogLevel Level, string Message)> _entries = [];

        /// <summary>Gets the entries recorded so far.</summary>
        public IReadOnlyList<(LogLevel Level, string Message)> Entries => _entries;

        /// <inheritdoc />
        public ILogger CreateLogger(string categoryName) => new Recorder(_entries);

        /// <inheritdoc />
        public void Dispose()
        {
        }

        private sealed class Recorder : ILogger
        {
            private readonly List<(LogLevel Level, string Message)> _entries;

            public Recorder(List<(LogLevel Level, string Message)> entries) => _entries = entries;

            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                ArgumentNullException.ThrowIfNull(formatter);

                lock (_entries)
                {
                    _entries.Add((logLevel, formatter(state, exception)));
                }
            }
        }
    }
}
