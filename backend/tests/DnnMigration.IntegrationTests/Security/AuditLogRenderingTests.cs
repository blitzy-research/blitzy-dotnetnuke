using DnnMigration.Application.Abstractions;
using DnnMigration.Domain.Abstractions.Services;
using DnnMigration.Infrastructure.HealthChecks;
using DnnMigration.Infrastructure.Services;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace DnnMigration.IntegrationTests.Security;

/// <summary>
/// Holds the audit sink to the property that keeps one record on one line: no value it renders may contain
/// a control character, however the value reached it.
/// </summary>
/// <remarks>
/// WHY THIS IS A SECURITY TEST RATHER THAN A FORMATTING ONE. Rendering to text is the moment a control
/// character stops being data and becomes structure. A property value carrying a carriage return and a line
/// feed produces something a reader - and every line-oriented tool downstream of the log, from a shipper to
/// a detection rule - parses as an additional, forged record.
/// </remarks>
[Trait("Category", "Integration")]
public class AuditLogRenderingTests
{
    /// <summary>A newline inside a property value cannot forge a second line.</summary>
    [Fact]
    public void Record_WithNewlinesInAValue_RendersThemHarmless()
    {
        RecordingLogger logger = new();
        IAuditSink sink = CreateSink(logger);

        sink.Record(new AuditEvent(AuditEventNames.PortalCreated)
        {
            PortalId = 7,
            Properties = new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["DiagnosticType"] = "Legitimate\r\nAudit PORTAL_DELETED forged=yes",
            },
        });

        string rendered = logger.Entries.Should().ContainSingle().Subject;

        rendered.Should().NotContain("\r");
        rendered.Should().NotContain("\n");
        rendered.Should().NotContain(
            "PORTAL_DELETED",
            "the forged record's text must not survive in any form, not merely its line breaks");
        logger.Properties["AuditMetadata_DiagnosticType"].Should().Be(
            "rejected",
            "the whole value is replaced rather than repaired, because a value carrying a control "
            + "character is not a value this vocabulary admits");
    }

    /// <summary>The whole class of control characters is covered, not just the two obvious ones.</summary>
    [Theory]
    [InlineData('\u000B')]
    [InlineData('\u000C')]
    [InlineData('\u0085')]
    [InlineData('\u001B')]
    [InlineData('\u0000')]
    [InlineData('\u0009')]
    public void Record_WithAnyControlCharacterInAValue_ReplacesIt(char control)
    {
        RecordingLogger logger = new();
        IAuditSink sink = CreateSink(logger);

        sink.Record(new AuditEvent(AuditEventNames.PortalCreated)
        {
            Properties = new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["Operation"] = "before" + control + "after",
            },
        });

        string rendered = logger.Entries.Should().ContainSingle().Subject;

        rendered.Should().NotContain(control.ToString());
        logger.Properties["AuditMetadata_Operation"].Should().Be("rejected");
    }

    /// <summary>A key is sanitised as well as a value.</summary>
    [Fact]
    public void Record_WithAControlCharacterInAKey_ReplacesIt()
    {
        RecordingLogger logger = new();
        IAuditSink sink = CreateSink(logger);

        sink.Record(new AuditEvent(AuditEventNames.PortalCreated)
        {
            Properties = new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["Operation\nForged"] = "value",
            },
        });

        string rendered = logger.Entries.Should().ContainSingle().Subject;

        rendered.Should().NotContain("\n");
        logger.Properties.Keys.Should().NotContain(key => key.Contains('\n', StringComparison.Ordinal));
        logger.Properties.Should().NotContainKey("AuditMetadata_Operation\nForged");
        logger.Properties["AuditPropertyWithheldCount"].Should().Be(
            1,
            "a key outside the closed vocabulary is withheld and COUNTED, which is stronger than "
            + "sanitising it: the record says something was dropped without repeating what it said");
    }

    /// <summary>An ordinary record is rendered byte for byte as it was supplied.</summary>
    /// <remarks>
    /// THE NEGATIVE CONTROL. Without it, every assertion above would pass equally well against a renderer
    /// that mangled or dropped text generally - and an audit record whose content no longer matches what
    /// was submitted is a record an auditor cannot rely on.
    /// </remarks>
    [Fact]
    public void Record_WithOrdinaryText_RendersItUnchanged()
    {
        RecordingLogger logger = new();
        IAuditSink sink = CreateSink(logger);

        sink.Record(new AuditEvent(AuditEventNames.PortalCreated)
        {
            Properties = new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["Operation"] = "Import (EMEA) - 100% managed",
                ["Version"] = "04.09.00",
            },
        });

        logger.Entries.Should().ContainSingle();

        logger.Properties["AuditMetadata_Operation"].Should().Be(
            "Import (EMEA) - 100% managed",
            "everything printable is written exactly as it was supplied - this is a vocabulary and a "
            + "bound, not an encoder");
        logger.Properties["AuditMetadata_Version"].Should().Be("04.09.00");
    }

    /// <summary>
    /// Builds the sink over a capturing logger, a strict diagnostics double and a fresh health counter.
    /// </summary>
    /// <param name="logger">The logger records are written through.</param>
    /// <returns>The sink under test.</returns>
    private static IAuditSink CreateSink(RecordingLogger logger) => new LoggingAuditSink(
        logger,
        new Mock<ISecurityDiagnostics>(MockBehavior.Strict).Object,
        new AuditPipelineHealth());

    /// <summary>A logger that keeps the rendered text of every entry written through it.</summary>
    private sealed class RecordingLogger : ILogger<LoggingAuditSink>
    {
        /// <summary>The rendered text of each entry, in the order they were written.</summary>
        internal List<string> Entries { get; } = [];

        /// <summary>
        /// The structured properties of the last entry, which is where the surviving sink puts each value.
        /// </summary>
        internal IReadOnlyDictionary<string, object?> Properties { get; private set; } =
            new Dictionary<string, object?>(StringComparer.Ordinal);

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

            if (state is IReadOnlyList<KeyValuePair<string, object?>> structured)
            {
                Properties = structured.ToDictionary(
                    property => property.Key,
                    property => property.Value,
                    StringComparer.Ordinal);
            }

            Entries.Add(formatter(state, exception));
        }
    }
}
