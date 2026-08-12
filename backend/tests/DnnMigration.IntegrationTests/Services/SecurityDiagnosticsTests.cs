using DnnMigration.Domain.Abstractions.Services;
using DnnMigration.Domain.Enums;
using DnnMigration.Infrastructure.Services;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace DnnMigration.IntegrationTests.Services;

/// <summary>
/// Holds the security-diagnostics implementation to the guarantees the contract's shape alone cannot give it.
/// </summary>
/// <remarks>
/// <para>
/// <strong>What the contract test could not reach.</strong> The unit suite pins the SHAPE of
/// <see cref="ISecurityDiagnostics"/> - one operation, a closed occurrence, two optional identifiers and a
/// code, and no parameter through which a message, an exception or an arbitrary object could travel. That is
/// the right guarantee at the right layer, and it is genuinely structural. It is also blind to everything the
/// implementation does with what it is given: a reason code carrying a newline forging a second log line, an
/// exception message passed as a "code" and written verbatim, the wrong level leaving the event invisible under
/// a production filter, a message template composed by interpolation so that a supplied value becomes part of
/// the template itself, or a logging provider whose failure escapes and turns a deliberately non-failing
/// condition into a failed request. Each of those is a real defect that would leave the contract test green.
/// </para>
/// <para>
/// <strong>Why the logger is supplied by the test rather than observed in the pipeline.</strong> The
/// guarantees above are about what is HANDED to the logging pipeline, not about what a sink renders. A
/// recording logger sees the level, the template and the structured arguments exactly as the implementation
/// produced them, before any sink, filter or formatter has had a chance to alter them - which is the only
/// place several of these assertions can be made at all. The registration itself is asserted separately
/// against the composed container, so both halves are covered: the right implementation is resolved, and the
/// implementation behaves.
/// </para>
/// </remarks>
[Trait("Category", "Integration")]
[Collection(IntegrationTestCollection.Name)]
public sealed class SecurityDiagnosticsTests
{
    /// <summary>
    /// The message template, asserted verbatim.
    /// </summary>
    /// <remarks>
    /// Duplicated from the implementation deliberately. The template being a CONSTANT is the guarantee that
    /// no supplied value can become part of it, and the only way to assert a constant is to state it. A
    /// template composed by interpolation would still carry the same placeholders and would render almost
    /// identically, so nothing weaker than an exact comparison distinguishes the two.
    /// </remarks>
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
    /// <remarks>
    /// The permitted set is ASCII letters, digits and the four separators the solution's own codes use. Each
    /// case below is a shape that appears in the delivered failure codes or in a framework type name, plus
    /// the boundary length, so a narrowing of the permitted set would fail here rather than silently start
    /// substituting the placeholder for real codes.
    /// </remarks>
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
    /// <remarks>
    /// <para>
    /// These are the values the bound exists for. Prose and an exception message are what a mistaken call
    /// site passes; a newline or a carriage return is what forges a second log line; the over-long value is
    /// the shape a whole exception message has even when it happens to contain no space.
    /// </para>
    /// <para>
    /// The substitute is asserted rather than an omission, because the occurrence is still worth recording
    /// and the distinctive placeholder is what makes the mistaken call site findable by searching the logs.
    /// </para>
    /// </remarks>
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
    /// to stop: an attacker-influenced value containing a line break can otherwise append a fabricated entry
    /// to the log, and a fabricated SECURITY entry is worth more to them than any single leaked value.
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
    /// <remarks>
    /// The distinction is worth keeping: no code supplied is an ordinary call, whereas the placeholder means
    /// a call site supplied something it should not have. Collapsing the two would make the placeholder
    /// useless for finding that call site.
    /// </remarks>
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
    /// This is the assertion that makes the contract's shape guarantee complete. The shape stops a message
    /// from being PASSED; only this stops one from being COMPOSED. A template built by interpolation would
    /// carry the caller's values inside the template itself, which defeats structured logging, makes the
    /// entries unqueryable by property, and reintroduces exactly the injection the sanitiser above prevents.
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
    /// Zero and minus one are both real identities in this schema - <c>Portals.PortalID</c> seeds at minus one
    /// and <c>Roles.RoleID</c> at zero - so neither may stand in for "not applicable". An implementation that
    /// defaulted a null identifier to either would make a log entry claim an occurrence belonged to a tenant
    /// or an account it had nothing to do with.
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
    /// <remarks>
    /// The level is a deliberate choice for the whole enumeration: none of these fails the caller's request,
    /// so error would over-report and page somebody; none is routine, so information would leave them
    /// invisible under an ordinary production filter. The theory covers every member so a member added later
    /// cannot arrive at a different level, and it asserts the name so an added member is provably reported as
    /// itself.
    /// </remarks>
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
    /// Every call site has already decided the occurrence does not warrant failing the request. A recorder
    /// that let a provider's failure escape would reverse that decision at the worst possible moment - during
    /// a sign-in, a permission resolution or a credential upgrade - and it would do so only when the logging
    /// pipeline was already unhealthy, which is when the application can least afford a second failure.
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
    /// <remarks>
    /// A null logger would make every call a silent no-operation, which is the one failure mode a recorder
    /// whose whole job is to never throw could not otherwise reveal.
    /// </remarks>
    [Fact]
    public void Construction_RefusesANullLogger()
    {
        Action construct = () => _ = new SecurityDiagnostics(null!);

        construct.Should().Throw<ArgumentNullException>()
            .WithParameterName("logger");
    }

    /// <summary>Every declared occurrence, for the level-and-name theory.</summary>
    /// <remarks>
    /// Enumerated from the type rather than listed, so a member added to the enumeration is covered without
    /// this file being edited - which is the whole reason the level assertion is a theory rather than a fact.
    /// </remarks>
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
