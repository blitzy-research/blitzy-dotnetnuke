using DnnMigration.Api.ErrorHandling;
using DnnMigration.Domain.Abstractions.Services;
using DnnMigration.Domain.Common;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace DnnMigration.IntegrationTests.Api;

/// <summary>
/// Proves that a duplicate-value refusal escaping to the pipeline is answered as a CONFLICT, explained in
/// wording authored here, and recorded as a client outcome rather than a server fault.
/// </summary>
/// <remarks>
/// <para>
/// AND THIS IS THE SAFETY NET RATHER THAN THE ROUTE. Every create path that writes through a unique
/// constraint catches the duplicate signal itself and answers with the same reason code its own sequential
/// pre-check emits, so a caller normally receives a 409 naming the field that collided - the role name, the
/// host name, the account name, the profile property - and never reaches the wording asserted here.
/// </para>
/// <para>
/// <strong>Measured against the handler's own surface, not over HTTP.</strong> Provoking the arm end to end
/// would require an endpoint that writes through a unique index WITHOUT catching the signal, which is
/// exactly the state the rest of this work removed - so an end-to-end test would either need a defect
/// deliberately left in place to keep it meaningful, or a test-only endpoint that proves nothing about the
/// real pipeline.
/// </para>
/// </remarks>
[Trait("Category", "Integration")]
public sealed class DuplicateKeyMappingTests
{
    /// <summary>The explanation a caller receives when a unique value it submitted is already held.</summary>
    private const string DuplicateRecordDetail =
        "The submitted values conflict with a record that already exists. "
        + "Reload the resource and submit different values.";

    /// <summary>
    /// A duplicate-value refusal is answered 409 with authored wording, discloses neither the constraint
    /// nor the provider's own text, and is not recorded as a server fault.
    /// </summary>
    /// <param name="constraintName">
    /// The constraint the store named, or <see langword="null"/> when it named none.
    /// </param>
    /// <returns>A task representing the test.</returns>
    [Theory]
    [Trait("Category", "Integration")]
    [InlineData("IX_RoleName")]
    [InlineData(null)]
    public async Task ADuplicateValueRefusal_IsAConflictAndNotAServerFault(string? constraintName)
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

        // The store-failure classifier is asked only after the duplicate arm has declined, so it is given
        // the answer that would be WRONG for this failure - "the store is unavailable" - deliberately.
        Mock<IStoreFailureClassifier> storeFailures = new(MockBehavior.Strict);
        storeFailures.Setup(classifier => classifier.IsStoreUnavailable(It.IsAny<Exception?>())).Returns(true);

        GlobalExceptionHandler handler = new(
            problemService.Object,
            problemFactory.Object,
            storeFailures.Object,
            factory.CreateLogger<GlobalExceptionHandler>());

        DefaultHttpContext httpContext = new();
        httpContext.Request.Method = HttpMethods.Post;

        // Shaped exactly as the persistence seam raises it: the Domain signal, carrying the constraint the
        // store disclosed and an authored message. No provider type appears, which is the whole reason the
        // transport can classify this outcome without referencing the mapper or the database client.
        DuplicateKeyException refusal = DuplicateKeyException.ForConstraint(
            constraintName,
            new InvalidOperationException(
                "Cannot insert duplicate key row in object 'dbo.Roles'. Server=db-prod-01;Password=hunter2"));

        bool handled = await handler.TryHandleAsync(httpContext, refusal, CancellationToken.None);

        handled.Should().BeTrue(
            "the handler wrote a payload, so it must claim the response rather than letting the framework "
            + "fall through and answer 500");

        httpContext.Response.StatusCode.Should().Be(
            StatusCodes.Status409Conflict,
            "the store refused a duplicated value: it worked correctly and kept exactly one record, so the "
            + "caller has a conflict to resolve rather than a server fault to report");

        written.Should().NotBeNull();
        written!.Status.Should().Be(StatusCodes.Status409Conflict);
        written.Detail.Should().Be(
            DuplicateRecordDetail,
            "the caller is told what to do about the collision in wording authored here");

        written.Detail.Should().NotContain(
            "IX_",
            "an index name is a schema fact of no use to a caller and obvious use to anyone mapping the store");
        written.Detail.Should().NotContain(
            "dbo.",
            "no object name may reach the caller");
        written.Detail.Should().NotContain(
            "Password",
            "a provider message routinely carries the connection string, so none of it may be republished");
        written.Detail.Should().NotContain(
            nameof(DuplicateKeyException),
            "no exception type may reach the caller");

        records.Entries.Should().NotBeEmpty("the refusal is still recorded, just not as a fault");
        records.Entries.Should().OnlyContain(
            entry => entry.Level < LogLevel.Error,
            "a collision the caller provoked is not a server fault, and recording one at Error lets a caller "
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
