using DnnMigration.Api.ErrorHandling;
using DnnMigration.Domain.Abstractions.Services;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace DnnMigration.IntegrationTests.Api;

/// <summary>
/// Proves that a refusal decided by the HOST is answered with the status the host chose and recorded as a
/// client mistake, by exercising the exception handler directly.
/// </summary>
/// <remarks>
/// <para>
/// This is asserted here rather than over HTTP for a reason that took a failing test to establish. The
/// in-memory test host used by every other suite in this project does not enforce the request-body ceiling
/// at all: a body of any size is handed to the application, so the refusal under test is never raised and
/// the submission is instead answered by whatever application-level rule the oversized member happens to
/// break. An end-to-end test therefore cannot distinguish the mapping being correct from the mapping being
/// absent - it passed against the defect and against the fix alike, which makes it worse than no test.
/// </para>
/// <para>
/// The mapping is consequently measured where it is actually made, on the handler's own public surface, with
/// the exception the host raises constructed exactly as the host constructs it. What the end-to-end suites
/// keep is the half they can measure honestly: the OpenAPI declaration of these statuses is asserted in
/// <see cref="ResponseDeclarationContractTests"/>, and the pipeline's refusal of an oversized body was
/// confirmed against a real host before this test was written - a live host answered 413 with this API's own
/// problem taxonomy for bodies of 1.2, 1.5 and 2 MB while logging them below Error.
/// </para>
/// <para>
/// Both directions are asserted. The status is taken FROM the exception rather than assumed, so a handler
/// that hard-coded 413 would pass the oversize case and fail the framing case; and the level is asserted to
/// be below Error, because the defect being guarded against was not only the wrong status but a server-fault
/// log entry for an ordinary client mistake, which lets a caller submitting oversized bodies bury the error
/// rate that real faults are alerted on.
/// </para>
/// </remarks>
[Trait("Category", "Integration")]
public sealed class TransportRefusalMappingTests
{
    /// <summary>Media-type-free explanation published for a body past the ceiling.</summary>
    private const string PayloadTooLargeDetail =
        "The submitted request body is larger than this endpoint accepts. Submit a smaller body.";

    /// <summary>Explanation published when the request could not be read at the transport level.</summary>
    private const string MalformedRequestDetail =
        "The request could not be read. Check the request framing and headers, then submit it again.";

    /// <summary>
    /// A host refusal is answered with the status the host settled on, explained without quoting the
    /// host's own message, and recorded below Error.
    /// </summary>
    /// <param name="hostStatus">The status the host put on its refusal.</param>
    /// <param name="expectedDetail">The explanation the caller must receive.</param>
    /// <returns>A task representing the test.</returns>
    [Theory]
    [Trait("Category", "Integration")]
    [InlineData(StatusCodes.Status413PayloadTooLarge, PayloadTooLargeDetail)]
    [InlineData(StatusCodes.Status400BadRequest, MalformedRequestDetail)]
    public async Task AHostDecidedRefusal_CarriesTheHostsStatusAndIsNotRecordedAsAFault(
        int hostStatus,
        string expectedDetail)
    {
        RecordingLoggerProvider records = new();

        using ILoggerFactory factory = LoggerFactory.Create(builder =>
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

        // Answered "the store is unavailable" on purpose, which is the wrong answer for a transport refusal.
        // The arm that classifies a store outage sits BELOW this one, so the host's status must survive the
        // classifier saying yes; a handler that consulted the classifier first would answer 503 for an
        // oversized body and fail here.
        Mock<IStoreFailureClassifier> storeFailures = new(MockBehavior.Strict);
        storeFailures.Setup(classifier => classifier.IsStoreUnavailable(It.IsAny<Exception?>())).Returns(true);

        GlobalExceptionHandler handler = new(
            problemService.Object,
            problemFactory.Object,
            storeFailures.Object,
            factory.CreateLogger<GlobalExceptionHandler>());

        DefaultHttpContext httpContext = new();
        httpContext.Request.Method = HttpMethods.Post;

        // Constructed exactly as the host constructs it: the status the host settled on travels ON the
        // exception, which is the whole point of the mapping under test.
        BadHttpRequestException refusal = new("host refusal", hostStatus);

        bool handled = await handler.TryHandleAsync(httpContext, refusal, CancellationToken.None);

        handled.Should().BeTrue(
            "the handler wrote a payload, so it must report the response as its own rather than asking the "
            + "framework to fall through and answer 500");

        httpContext.Response.StatusCode.Should().Be(
            hostStatus,
            "the status line must carry the status the host chose, not a status this handler invented");

        written.Should().NotBeNull();
        written!.Status.Should().Be(hostStatus, "the payload restates the status the caller was sent");
        written.Detail.Should().Be(
            expectedDetail,
            "the caller is told what to do about the refusal in wording authored here");

        written.Detail.Should().NotContain(
            "host refusal",
            "the host's own message is caller-facing text nobody reviewed and must not be republished");
        written.Detail.Should().NotContain(
            nameof(BadHttpRequestException),
            "no exception type may reach the caller");

        records.Entries.Should().NotBeEmpty("the refusal is still recorded, just not as a fault");
        records.Entries.Should().OnlyContain(
            entry => entry.Level < LogLevel.Error,
            "a refusal the caller provoked is not a server fault, and recording one at Error lets a caller "
            + "flood the error stream and defeat error-rate alerting for the faults that matter");
    }

    /// <summary>Captures every entry written through a logger factory, with its level.</summary>
    private sealed class RecordingLoggerProvider : ILoggerProvider
    {
        /// <summary>Gets the entries recorded so far.</summary>
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        /// <inheritdoc />
        public ILogger CreateLogger(string categoryName) => new RecordingLogger(this);

        /// <inheritdoc />
        public void Dispose()
        {
        }

        /// <summary>Writes every entry into the owning provider's list.</summary>
        /// <param name="owner">The provider collecting the entries.</param>
        private sealed class RecordingLogger(RecordingLoggerProvider owner) : ILogger
        {
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

                owner.Entries.Add((logLevel, formatter(state, exception)));
            }
        }
    }
}
