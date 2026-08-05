using System.Globalization;
using DnnMigration.Application.Abstractions;
using DnnMigration.Domain.Abstractions.Services;
using DnnMigration.Domain.Enums;
using DnnMigration.Infrastructure.HealthChecks;
using DnnMigration.Infrastructure.Services;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace DnnMigration.UnitTests.Services;

/// <summary>
/// Holds the audit transport to the two guarantees it exists for: every fact it carries is attached as a
/// property that can be queried by name, and a record that cannot be written is never lost silently.
/// </summary>
/// <remarks>
/// <para>
/// The integration suite proves that the transport is registered and that its template and arguments agree
/// on a real host. What it cannot reach is the admission policy's edges - a value outside its bounds, a key
/// outside the vocabulary, a logger that faults - because no endpoint in the application produces any of
/// them. Those are asserted here, against the concrete type.
/// </para>
/// <para>
/// THE ADMISSION POLICY IS A CLOSED ALLOWLIST, and every assertion below is written against that rather than
/// against a key-shape test. A fact is carried only when its name appears on the transport's own list, in
/// which case it is attached as <c>AuditMetadata_&lt;Name&gt;</c>; anything else is withheld and counted. A
/// shape test would let a caller introduce a property this application has never reviewed, which is the
/// property the allowlist exists to deny - so "would this key be admitted" is deliberately not a question
/// about the key's characters.
/// </para>
/// <para>
/// The facts are read from the entry's own structured state, which is where they travel: the transport builds
/// one state object carrying the envelope, the admitted metadata, the two counts and the message template, so
/// asserting on that state is asserting on what a structured sink will record.
/// </para>
/// </remarks>
public class LoggingAuditSinkTests
{
    /// <summary>Every admitted fact becomes a property of its own, under the metadata prefix.</summary>
    /// <remarks>
    /// The three facts chosen are the three the review named as unqueryable while they were being flattened
    /// into one rendered member: the operation that was performed, the page a module was placed on, and the
    /// size of an imported payload. All three are on the transport's allowlist.
    /// </remarks>
    [Fact]
    public void Record_AttachesEveryAdmittedFactAsItsOwnProperty()
    {
        RecordingLogger logger = new();
        LoggingAuditSink sink = NewSink(logger);

        sink.Record(new AuditEvent("MODULE_UPDATED")
        {
            Properties = new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["Operation"] = "Import",
                ["TabId"] = "42",
                ["PayloadLength"] = "1024",
            },
        });

        logger.Properties.Should().Contain(
            new KeyValuePair<string, object?>("AuditMetadata_Operation", "Import"));
        logger.Properties.Should().Contain(
            new KeyValuePair<string, object?>("AuditMetadata_TabId", "42"));
        logger.Properties.Should().Contain(
            new KeyValuePair<string, object?>("AuditMetadata_PayloadLength", "1024"));

        logger.Properties["AuditPropertyCount"].Should().Be(3);
        logger.Properties["AuditPropertyWithheldCount"].Should().Be(0);
    }

    /// <summary>A null value is preserved as null rather than becoming an empty string.</summary>
    /// <remarks>
    /// The distinction is the one the legacy null encoding makes load-bearing: an empty string is a legacy
    /// absent-value sentinel in its own right, so collapsing null onto it would record a different fact from
    /// the one the caller supplied.
    /// </remarks>
    [Fact]
    public void Record_PreservesANullValueAsNull()
    {
        RecordingLogger logger = new();
        LoggingAuditSink sink = NewSink(logger);

        sink.Record(new AuditEvent("ROLE_UPDATED")
        {
            Properties = new Dictionary<string, string?>(StringComparer.Ordinal) { ["Advisory"] = null },
        });

        logger.Properties.Should().ContainKey("AuditMetadata_Advisory");
        logger.Properties["AuditMetadata_Advisory"].Should().BeNull();
        logger.Properties["AuditPropertyCount"].Should().Be(1);
    }

    /// <summary>A key outside the allowlist is withheld, and the withholding is counted.</summary>
    /// <remarks>
    /// Withheld rather than reshaped, deliberately: every key in the solution is authored by an application
    /// service and is already on the list, so a key that fails is an unintended change rather than a value to
    /// rescue. The count is what stops the withholding from being silent. The two keys used here are the two
    /// shapes a caller-derived key takes - prose and a leading digit - and neither is admissible for the
    /// stronger reason that neither is on the list at all.
    /// </remarks>
    [Fact]
    public void Record_WithholdsAFactWhoseKeyIsNotOnTheAllowlist()
    {
        RecordingLogger logger = new();
        LoggingAuditSink sink = NewSink(logger);

        sink.Record(new AuditEvent("MODULE_UPDATED")
        {
            Properties = new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["Operation"] = "Export",
                ["not an identifier"] = "withheld",
                ["1LeadingDigit"] = "withheld",
            },
        });

        logger.Properties.Should().ContainKey("AuditMetadata_Operation");
        logger.Properties.Keys.Should().ContainSingle(name => name.StartsWith(
            "AuditMetadata_",
            StringComparison.Ordinal));
        logger.Properties["AuditPropertyCount"].Should().Be(1);
        logger.Properties["AuditPropertyWithheldCount"].Should().Be(2);
    }

    /// <summary>A fact cannot take, or overwrite, a name the envelope already uses.</summary>
    /// <remarks>
    /// This is the collision a logging pipeline resolves by keeping whichever value arrived first, so without
    /// a guard the fact would vanish while the record still claimed to carry it - or, worse, would displace an
    /// envelope member an operator reads as authoritative. Two mechanisms make it impossible here: an admitted
    /// fact is namespaced under the metadata prefix, so it cannot land on an envelope name at all, and a key
    /// naming an envelope member is not on the allowlist in the first place. The count makes the refusal
    /// visible instead of silent.
    /// </remarks>
    [Fact]
    public void Record_WithholdsAFactThatWouldCollideWithTheEnvelope()
    {
        RecordingLogger logger = new();
        LoggingAuditSink sink = NewSink(logger);

        sink.Record(new AuditEvent("USER_CREATED")
        {
            PortalId = 7,
            Properties = new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["Event"] = "collides with AuditEvent",
                ["PortalId"] = "collides with AuditPortalId",
            },
        });

        logger.Properties.Keys.Should().NotContain(name => name.StartsWith(
            "AuditMetadata_",
            StringComparison.Ordinal));
        logger.Properties["AuditEvent"].Should().Be("USER_CREATED");
        logger.Properties["AuditPortalId"].Should().Be(7);
        logger.Properties["AuditPropertyCount"].Should().Be(0);
        logger.Properties["AuditPropertyWithheldCount"].Should().Be(2);
    }

    /// <summary>An over-long value is replaced by a fixed marker rather than shortened.</summary>
    /// <remarks>
    /// Replaced rather than truncated, and that is the stronger of the two behaviours: a shortened value reads
    /// as the whole value and would be quoted as evidence of something the record does not actually say, while
    /// a prefix of an over-long value is still caller-controlled content of unbounded shape. The marker records
    /// that a value was present and was refused, which is all an operator can safely act on.
    /// </remarks>
    [Fact]
    public void Record_RefusesAnOverlongValueAndSaysSo()
    {
        RecordingLogger logger = new();
        LoggingAuditSink sink = NewSink(logger);

        sink.Record(new AuditEvent("MODULE_UPDATED")
        {
            Properties = new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["Version"] = new string('v', 400),
            },
        });

        logger.Properties["AuditMetadata_Version"].Should().Be("rejected");
        logger.Properties["AuditPropertyCount"].Should().Be(1);
    }

    /// <summary>A value carrying a control character is replaced rather than recorded.</summary>
    /// <remarks>
    /// A newline inside a descriptive value is how a log line is closed and a second, forged one is written
    /// after it. The substitution records that a value was present and was refused. The delimiter characters a
    /// rendered key=value list would be parsed on are refused for the same reason.
    /// </remarks>
    [Fact]
    public void Record_ReplacesAValueCarryingAControlCharacter()
    {
        RecordingLogger logger = new();
        LoggingAuditSink sink = NewSink(logger);

        sink.Record(new AuditEvent("MODULE_UPDATED")
        {
            Properties = new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["Version"] = "1.0\nAudit FORGED_EVENT Succeeded",
            },
        });

        logger.Properties["AuditMetadata_Version"].Should().Be("rejected");
    }

    /// <summary>The number of facts one record can carry is bounded.</summary>
    /// <remarks>
    /// The widest record in the solution carries a handful of facts, so the ceiling is not a limit anybody is
    /// expected to reach - it is there because the map is populated from request and document input and an
    /// audit entry must not be allowed to grow without bound. Every key offered here is on the allowlist, so
    /// the ceiling is the only thing that can withhold one, which is what this fact isolates.
    /// </remarks>
    [Fact]
    public void Record_BoundsTheNumberOfFactsItAttaches()
    {
        RecordingLogger logger = new();
        LoggingAuditSink sink = NewSink(logger);
        Dictionary<string, string?> facts = new(StringComparer.Ordinal);

        foreach (string name in AllowlistedMetadataNames)
        {
            facts[name] = "value";
        }

        facts.Count.Should().BeGreaterThan(
            16,
            "the ceiling can only be demonstrated by offering more admissible facts than it permits");

        sink.Record(new AuditEvent("MODULE_UPDATED") { Properties = facts });

        logger.Properties.Keys
            .Count(name => name.StartsWith("AuditMetadata_", StringComparison.Ordinal))
            .Should()
            .Be(16);
        logger.Properties["AuditPropertyCount"].Should().Be(16);
        logger.Properties["AuditPropertyWithheldCount"].Should().Be(facts.Count - 16);
    }

    /// <summary>
    /// A logger that faults neither fails the caller nor loses the record without trace.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Both halves matter and they pull against each other. The transport has promised never to throw,
    /// because the operation it describes has already been committed and failing the request to complain
    /// about the note taken of it would be worse than the note being missing. But a gap in an audit trail
    /// reads as "the operation did not happen", so the loss has to be reported - and it cannot be reported
    /// through the channel that just failed.
    /// </para>
    /// <para>
    /// THREE INDEPENDENT CHANNELS carry that report, and all three are asserted because each answers a
    /// different reader: the process-local counter an operator queries out of band, the health signal an
    /// orchestrator polls, and the security diagnostic that names the anomaly. The counter is process-wide and
    /// monotonic, so the assertion is on the INCREASE rather than on an absolute value: other facts in this
    /// assembly may run alongside this one, and a total would make the test depend on them.
    /// </para>
    /// </remarks>
    [Fact]
    public void Record_WhenTheLoggerFaults_ReportsTheLossOnIndependentChannels()
    {
        long before = AuditSinkDiagnostics.Instance.LostRecordCount;
        Mock<ISecurityDiagnostics> diagnostics = new();
        AuditPipelineHealth health = new();
        LoggingAuditSink sink = new(new FaultingLogger(), diagnostics.Object, health);

        Action record = () => sink.Record(new AuditEvent("PORTAL_CREATED"));

        record.Should().NotThrow(
            "the caller has already committed the operation being described");
        AuditSinkDiagnostics.Instance.LostRecordCount.Should().BeGreaterThan(
            before,
            "a lost audit record must be observable somewhere other than the pipeline that lost it");
        health.FailureCount.Should().Be(
            1,
            "an incomplete accountability trail is a condition an orchestrator can be told about");
        diagnostics.Verify(
            channel => channel.Record(
                It.IsAny<SecurityDiagnosticEvent>(),
                It.IsAny<int?>(),
                It.IsAny<int?>(),
                It.IsAny<string>()),
            Times.Once);
    }

    /// <summary>A null event is discarded without touching the logger.</summary>
    [Fact]
    public void Record_WithNoEvent_DoesNothing()
    {
        RecordingLogger logger = new();
        LoggingAuditSink sink = NewSink(logger);

        sink.Record(null!);

        logger.Entries.Should().Be(0);
        logger.Properties.Should().BeEmpty();
    }

    /// <summary>
    /// The metadata names the transport admits, restated here as this suite's own expectation.
    /// </summary>
    /// <remarks>
    /// Restated rather than read from the transport, deliberately: a fact that asked the subject for its own
    /// answer could not fail. Only the count matters to the assertions above, so a name added to the
    /// transport's list without being added here still leaves every fact valid.
    /// </remarks>
    private static readonly string[] AllowlistedMetadataNames =
    [
        "AccountRemoved",
        "Advisory",
        "AffectedTabCount",
        "AliasesReleased",
        "ApplyToAllModules",
        "Approved",
        "AutoAssignment",
        "BusinessControllerRegistered",
        "DiagnosticType",
        "EffectiveDate",
        "EffectCount",
        "Expired",
        "ExpiryDate",
        "IsChildPortal",
        "IsDeleted",
        "IsVisible",
        "LineNumber",
        "LinePosition",
        "MustChangePassword",
        "Operation",
        "ParentId",
        "PayloadLength",
        "PlacementCount",
        "TabId",
        "TabModuleId",
        "Version",
    ];

    /// <summary>Builds the transport over a recording logger and inert failure channels.</summary>
    /// <param name="logger">The logger whose entries the calling fact inspects.</param>
    /// <returns>The transport under test.</returns>
    private static LoggingAuditSink NewSink(RecordingLogger logger) =>
        new(logger, Mock.Of<ISecurityDiagnostics>(), new AuditPipelineHealth());

    /// <summary>Captures the structured state of every entry written to it.</summary>
    private sealed class RecordingLogger : ILogger<LoggingAuditSink>
    {
        /// <summary>Gets the structured properties of the last entry, by name.</summary>
        public Dictionary<string, object?> Properties { get; } = new(StringComparer.Ordinal);

        /// <summary>Gets the number of entries written.</summary>
        public int Entries { get; private set; }

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
            Entries++;

            if (state is IEnumerable<KeyValuePair<string, object?>> values)
            {
                foreach (KeyValuePair<string, object?> value in values)
                {
                    Properties[value.Key] = value.Value;
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

    /// <summary>Fails every write, as a broken sink would.</summary>
    private sealed class FaultingLogger : ILogger<LoggingAuditSink>
    {
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
            => throw new InvalidOperationException("The logging pipeline is unavailable.");
    }
}
