using DnnMigration.Domain.Abstractions.Services;
using DnnMigration.Domain.Enums;
using DnnMigration.Infrastructure.Services;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace DnnMigration.IntegrationTests.Services;

/// <summary>
/// Holds the security-diagnostics implementation to the guarantees the contract's shape alone cannot give
/// it.
/// </summary>
/// <remarks>
/// <strong>Why the logger is supplied by the test rather than observed in the pipeline.</strong> The
/// guarantees above are about what is HANDED to the logging pipeline, not about what a sink renders.
/// </remarks>
[Trait("Category", "Integration")]
[Collection(IntegrationTestCollection.Name)]
public sealed class SecurityDiagnosticsTests
{
    /// <summary>The message template, asserted verbatim.</summary>
    private const string ExpectedTemplate =
        "Security diagnostic {Occurrence} recorded for portal {PortalId} and account {UserId} "
        + "with reason {ReasonCode}.";

    /// <summary>The substitute the implementation writes in place of a value that is not code-shaped.</summary>
    private const string RejectedReasonCode = "unrecognised-code";

    private readonly ApiTestFixture _fixture;

    /// <summary>Initialises a new instance of the <see cref="SecurityDiagnosticsTests"/> class.</summary>
    /// <param name="fixture">The shared host, whose container supplies the registered recorder.</param>
    public SecurityDiagnosticsTests(ApiTestFixture fixture) => _fixture = fixture;

    /// <summary>The composed application resolves this implementation, once.</summary>
    /// <remarks>
    /// The registration matters more here than for most services: the Application layer's package surface
    /// excludes every logging assembly, so this registration is the only route by which a service in that
    /// layer can report a security anomaly at all. A host that resolved something else would silence every
    /// one of those reports without failing anything.
    /// </remarks>
    [Fact]
    public void TheComposedApplication_ResolvesOneSecurityDiagnostics()
    {
        ISecurityDiagnostics fromRoot = _fixture.Services.GetRequiredService<ISecurityDiagnostics>();

        fromRoot.Should().BeOfType<SecurityDiagnostics>();

        using IServiceScope scope = _fixture.Services.CreateScope();

        scope.ServiceProvider.GetRequiredService<ISecurityDiagnostics>().Should().BeSameAs(
            fromRoot,
            "the recorder holds only a logger, which is thread-safe and lifetime-agnostic, so one instance "
            + "serves the whole process");
    }

    /// <summary>A code-shaped reason is written unchanged.</summary>
    [Theory]
    [InlineData("auth.credentials-invalid")]
    [InlineData("user.membership-settings.storage-conflict")]
    [InlineData("SqlException")]
    [InlineData("error:515")]
    [InlineData("MIXED_case.9")]
    [InlineData("a")]
    [InlineData("0123456789012345678901234567890123456789012345678901234567890123")]
    public void Record_WritesACodeShapedReasonUnchanged(string reasonCode)
    {
        RecordingLogger logger = new();

        new SecurityDiagnostics(logger).Record(
            SecurityDiagnosticEvent.EffectivePermissionResolutionFailed,
            portalId: -1,
            userId: 7,
            reasonCode: reasonCode);

        logger.Entries.Should().Be(1);
        logger.Properties["ReasonCode"].Should().Be(
            reasonCode,
            "a real code must survive sanitisation, or the placeholder would replace the diagnostic value "
            + "the operator needs");
    }

    /// <summary>Anything that is not code-shaped is replaced by the placeholder.</summary>
    [Theory]
    [InlineData("Login failed for user 'sa'.")]
    [InlineData("code\nSecurity diagnostic Forged recorded for portal 1")]
    [InlineData("code\rmore")]
    [InlineData("code\tmore")]
    [InlineData("code more")]
    [InlineData("Server=localhost;Password=hunter2")]
    [InlineData("01234567890123456789012345678901234567890123456789012345678901234")]
    [InlineData("codé")]
    public void Record_ReplacesAnythingThatIsNotCodeShaped(string reasonCode)
    {
        RecordingLogger logger = new();

        new SecurityDiagnostics(logger).Record(
            SecurityDiagnosticEvent.MembershipRecordMissingDuringSignIn,
            reasonCode: reasonCode);

        logger.Properties["ReasonCode"].Should().Be(
            RejectedReasonCode,
            "a value that is prose, a forged log line or an exception message must be discarded rather than "
            + "written, which is a guarantee this type enforces rather than documents");

        logger.Rendered.Should().NotContain(
            reasonCode,
            "the rejected value must not reach the rendered message either, or the substitution would be "
            + "cosmetic");
    }

    /// <summary>A value carrying a newline cannot produce a second log line.</summary>
    /// <remarks>
    /// Stated separately from the substitution above because it is the specific attack the sanitiser exists
    /// to stop: an attacker-influenced value containing a line break can otherwise append a fabricated
    /// entry to the log, and a fabricated SECURITY entry is worth more to them than any single leaked
    /// value.
    /// </remarks>
    [Fact]
    public void Record_CannotBeMadeToForgeASecondLogLine()
    {
        RecordingLogger logger = new();

        new SecurityDiagnostics(logger).Record(
            SecurityDiagnosticEvent.CredentialWorkFactorUpgradeFailed,
            reasonCode: "ok\r\nSecurity diagnostic AuditRecordNotWritten recorded for portal 1");

        logger.Entries.Should().Be(1, "one call must produce one entry");
        logger.Rendered.Should().NotContain("\n").And.NotContain("\r");
    }

    /// <summary>An absent reason is recorded as absent rather than as the placeholder.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Record_ReportsAnAbsentReasonAsAbsent(string? reasonCode)
    {
        RecordingLogger logger = new();

        new SecurityDiagnostics(logger).Record(
            SecurityDiagnosticEvent.AuditRecordNotWritten,
            reasonCode: reasonCode);

        logger.Properties.Should().ContainKey("ReasonCode");
        logger.Properties["ReasonCode"].Should().BeNull(
            "no code supplied is not the same fact as a code that was rejected, and the placeholder is how a "
            + "rejected one is found");
    }

    /// <summary>The template is a constant and every value is a structured property.</summary>
    /// <remarks>
    /// This is the assertion that makes the contract's shape guarantee complete.
    /// </remarks>
    [Fact]
    public void Record_UsesAConstantTemplateAndAttachesEveryValueAsAProperty()
    {
        RecordingLogger logger = new();

        new SecurityDiagnostics(logger).Record(
            SecurityDiagnosticEvent.LegacyCredentialMigrationFailed,
            portalId: -1,
            userId: 42,
            reasonCode: "auth.legacy-rehash-failed");

        logger.Template.Should().Be(
            ExpectedTemplate,
            "the template must be the same constant for every call, whatever the values are");

        logger.Properties.Keys.Should().BeEquivalentTo(
            new[] { "Occurrence", "PortalId", "UserId", "ReasonCode", "{OriginalFormat}" },
            "every fact is a named property, so an operator can query by name rather than by parsing prose");

        logger.Properties["Occurrence"].Should().Be(
            nameof(SecurityDiagnosticEvent.LegacyCredentialMigrationFailed),
            "the member NAME is written, not its ordinal: an ordinal is the one thing about an enumeration "
            + "that can change without a compiler complaining");
        logger.Properties["PortalId"].Should().Be(-1, "-1 is a real portal identity in this schema");
        logger.Properties["UserId"].Should().Be(42);
        logger.Properties["ReasonCode"].Should().Be("auth.legacy-rehash-failed");
    }

    /// <summary>Absent identifiers are attached as absent rather than as a substitute.</summary>
    /// <remarks>
    /// Zero and minus one are both real identities in this schema - <c>Portals.PortalID</c> seeds at minus
    /// one and <c>Roles.RoleID</c> at zero - so neither may stand in for "not applicable". An
    /// implementation that defaulted a null identifier to either would make a log entry claim an occurrence
    /// belonged to a tenant or an account it had nothing to do with.
    /// </remarks>
    [Fact]
    public void Record_AttachesAbsentIdentifiersAsAbsent()
    {
        RecordingLogger logger = new();

        new SecurityDiagnostics(logger).Record(SecurityDiagnosticEvent.AuditRecordNotWritten);

        logger.Properties["PortalId"].Should().BeNull();
        logger.Properties["UserId"].Should().BeNull(
            "zero and minus one are real identities here, so neither may stand in for an absent one");
    }

    /// <summary>Every occurrence is recorded at the warning level, by its own name.</summary>
    [Theory]
    [MemberData(nameof(EveryOccurrence))]
    public void Record_WritesEveryOccurrenceAsAWarningUnderItsOwnName(SecurityDiagnosticEvent occurrence)
    {
        RecordingLogger logger = new();

        new SecurityDiagnostics(logger).Record(occurrence);

        logger.Level.Should().Be(
            LogLevel.Warning,
            "these are exactly the class of event an operator wants surfaced without being paged");
        logger.Properties["Occurrence"].Should().Be(occurrence.ToString());
    }

    /// <summary>A logging provider that throws does not fail the caller.</summary>
    /// <remarks>
    /// Every call site has already decided the occurrence does not warrant failing the request.
    /// </remarks>
    [Fact]
    public void Record_ContainsAFailureInTheLoggingPipeline()
    {
        FaultingLogger logger = new();
        SecurityDiagnostics diagnostics = new(logger);

        Action record = () => diagnostics.Record(
            SecurityDiagnosticEvent.AuditRecordNotWritten,
            portalId: -1,
            userId: 1,
            reasonCode: "audit.sink-unavailable");

        record.Should().NotThrow(
            "a recorder that threw would turn a condition judged not to warrant failing the request into "
            + "the failure it was judged not to be");

        logger.Attempts.Should().Be(1, "the write was genuinely attempted, not skipped");
    }

    /// <summary>The recorder refuses to be constructed without a logger.</summary>
    [Fact]
    public void Construction_RefusesANullLogger()
    {
        Action construct = () => _ = new SecurityDiagnostics(null!);

        construct.Should().Throw<ArgumentNullException>()
            .WithParameterName("logger");
    }

    /// <summary>Every declared occurrence, for the level-and-name theory.</summary>
    public static TheoryData<SecurityDiagnosticEvent> EveryOccurrence
    {
        get
        {
            TheoryData<SecurityDiagnosticEvent> data = new();

            foreach (SecurityDiagnosticEvent occurrence in Enum.GetValues<SecurityDiagnosticEvent>())
            {
                data.Add(occurrence);
            }

            return data;
        }
    }

    /// <summary>Captures the level, template, structured state and rendered text of each entry.</summary>
    private sealed class RecordingLogger : ILogger<SecurityDiagnostics>
    {
        /// <summary>Gets the structured properties of the last entry, by name.</summary>
        public Dictionary<string, object?> Properties { get; } = new(StringComparer.Ordinal);

        /// <summary>Gets the number of entries written.</summary>
        public int Entries { get; private set; }

        /// <summary>Gets the level of the last entry.</summary>
        public LogLevel Level { get; private set; } = LogLevel.None;

        /// <summary>Gets the message template of the last entry, as the pipeline received it.</summary>
        public string? Template { get; private set; }

        /// <summary>Gets the rendered text of the last entry.</summary>
        public string Rendered { get; private set; } = string.Empty;

        /// <inheritdoc />
        public IDisposable BeginScope<TState>(TState state)
            where TState : notnull => new NullScope();

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

            Entries++;
            Level = logLevel;
            Rendered = formatter(state, exception);

            if (state is IEnumerable<KeyValuePair<string, object?>> values)
            {
                foreach (KeyValuePair<string, object?> value in values)
                {
                    Properties[value.Key] = value.Value;

                    if (string.Equals(value.Key, "{OriginalFormat}", StringComparison.Ordinal))
                    {
                        Template = value.Value as string;
                    }
                }
            }
        }

        /// <summary>A scope that records nothing on disposal.</summary>
        private sealed class NullScope : IDisposable
        {
            /// <inheritdoc />
            public void Dispose()
            {
            }
        }
    }

    /// <summary>Fails every write, as an unhealthy logging pipeline would.</summary>
    private sealed class FaultingLogger : ILogger<SecurityDiagnostics>
    {
        /// <summary>Gets the number of writes attempted against this logger.</summary>
        public int Attempts { get; private set; }

        /// <inheritdoc />
        public IDisposable BeginScope<TState>(TState state)
            where TState : notnull
            => throw new InvalidOperationException("The logging pipeline is unavailable.");

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
            Attempts++;

            throw new InvalidOperationException("The logging pipeline is unavailable.");
        }
    }
}
