using DnnMigration.Application.Abstractions;
using DnnMigration.Domain.Abstractions.Services;
using DnnMigration.Domain.Enums;
using DnnMigration.Infrastructure.HealthChecks;
using DnnMigration.Infrastructure.Services;
using FluentAssertions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace DnnMigration.IntegrationTests.Security;

/// <summary>
/// Holds the concrete audit transport to its privacy, structure and failure-observability guarantees.
/// </summary>
[Trait("Category", "Integration")]
public sealed class LoggingAuditSinkTests
{
    /// <summary>
    /// Only the closed metadata vocabulary reaches the logger, and hostile values become one fixed scalar.
    /// </summary>
    [Fact]
    public void Record_EmitsOnlyAllowlistedBoundStructuredMetadata()
    {
        var logger = new CapturingLogger<LoggingAuditSink>();
        var diagnostics = new Mock<ISecurityDiagnostics>(MockBehavior.Strict);
        var health = new AuditPipelineHealth();
        var sink = new LoggingAuditSink(logger, diagnostics.Object, health);

        sink.Record(new AuditEvent(AuditEventNames.PortalCreated)
        {
            PortalId = -1,
            ActorUserId = 7,
            SubjectUserId = 8,
            ResourceType = "Portal",
            ResourceId = "-1",
            Properties = new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["Operation"] = "Import",
                ["PayloadLength"] = "42",
                ["DiagnosticType"] = "XmlException;Forged=True\r\nsecond-line",
                ["Advisory"] = new string('a', 129),
                ["PortalName"] = "A directly identifying tenant name",
                ["SourceFileName"] = "caller-controlled.xml",
            },
        });

        IReadOnlyDictionary<string, object?> properties = logger.Properties;

        properties["AuditEvent"].Should().Be(AuditEventNames.PortalCreated);
        properties["AuditActorUserId"].Should().Be(7);
        properties.Should().NotContainKey("AuditActorUserName");
        properties["AuditMetadata_Operation"].Should().Be("Import");
        properties["AuditMetadata_PayloadLength"].Should().Be("42");
        properties["AuditMetadata_DiagnosticType"].Should().Be("rejected");
        properties["AuditMetadata_Advisory"].Should().Be("rejected");
        properties.Should().NotContainKeys(
            "PortalName",
            "SourceFileName",
            "AuditMetadata_PortalName",
            "AuditMetadata_SourceFileName");

        logger.Message.Should().NotContain("A directly identifying tenant name");
        logger.Message.Should().NotContain("caller-controlled.xml");
        diagnostics.VerifyNoOtherCalls();
        health.FailureCount.Should().Be(0);
    }

    /// <summary>
    /// Even allowlisted metadata cannot expand one record beyond the fixed per-event ceiling.
    /// </summary>
    [Fact]
    public void Record_CapsTheNumberOfStructuredMetadataProperties()
    {
        var logger = new CapturingLogger<LoggingAuditSink>();
        var diagnostics = new Mock<ISecurityDiagnostics>(MockBehavior.Strict);
        var sink = new LoggingAuditSink(logger, diagnostics.Object, new AuditPipelineHealth());
        string[] allowedKeys =
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
            "MustUpdateProfile",
        ];

        sink.Record(new AuditEvent(AuditEventNames.PortalCreated)
        {
            Properties = allowedKeys.ToDictionary(key => key, _ => (string?)"1", StringComparer.Ordinal),
        });

        logger.Properties.Keys.Count(key => key.StartsWith("AuditMetadata_", StringComparison.Ordinal))
            .Should().Be(16);
        diagnostics.VerifyNoOtherCalls();
    }

    /// <summary>
    /// A primary logging failure leaves the caller successful, emits the closed fallback occurrence and
    /// degrades the process health signal.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task Record_WhenThePrimaryLoggerFails_ReportsTheLossAndDegradesHealth()
    {
        var logger = new CapturingLogger<LoggingAuditSink>
        {
            Failure = new InvalidOperationException("sink detail that must not be forwarded"),
        };
        var diagnostics = new Mock<ISecurityDiagnostics>(MockBehavior.Strict);
        var health = new AuditPipelineHealth();
        var sink = new LoggingAuditSink(logger, diagnostics.Object, health);
        var auditEvent = new AuditEvent(AuditEventNames.UserDeleted)
        {
            PortalId = 4,
            ActorUserId = 5,
            SubjectUserId = 6,
        };

        Action record = () => sink.Record(auditEvent);

        record.Should().NotThrow("a committed operation must not become a false failure when its trail faults");
        diagnostics.Verify(
            diagnostic => diagnostic.Record(
                SecurityDiagnosticEvent.AuditRecordNotWritten,
                4,
                5,
                nameof(InvalidOperationException)),
            Times.Once);

        HealthCheckResult status = await health.CheckHealthAsync(new HealthCheckContext());

        status.Status.Should().Be(HealthStatus.Degraded);
        status.Data["failureCount"].Should().Be(1L);
    }

    /// <summary>
    /// A non-conforming fallback recorder still cannot escape the audit contract, and the in-memory health
    /// counter remains the independent observable signal.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task Record_WhenBothLoggingChannelsFail_StillPreservesTheOperationAndCountsTheLoss()
    {
        var logger = new CapturingLogger<LoggingAuditSink>
        {
            Failure = new InvalidOperationException("primary failed"),
        };
        var diagnostics = new Mock<ISecurityDiagnostics>(MockBehavior.Strict);
        diagnostics
            .Setup(diagnostic => diagnostic.Record(
                It.IsAny<SecurityDiagnosticEvent>(),
                It.IsAny<int?>(),
                It.IsAny<int?>(),
                It.IsAny<string?>()))
            .Throws(new InvalidOperationException("fallback failed"));

        var health = new AuditPipelineHealth();
        var sink = new LoggingAuditSink(logger, diagnostics.Object, health);

        Action record = () => sink.Record(new AuditEvent(AuditEventNames.RoleDeleted));

        record.Should().NotThrow();

        HealthCheckResult status = await health.CheckHealthAsync(new HealthCheckContext());
        status.Status.Should().Be(HealthStatus.Degraded);
        status.Data["failureCount"].Should().Be(1L);
    }

    /// <summary>
    /// A logging filter that disables the required audit level is treated as a delivery failure rather than
    /// as an intentional no-op.
    /// </summary>
    [Fact]
    public void Record_WhenTheAuditLevelIsDisabled_DegradesHealthAndEmitsTheFallbackCode()
    {
        var logger = new CapturingLogger<LoggingAuditSink> { Enabled = false };
        var diagnostics = new Mock<ISecurityDiagnostics>(MockBehavior.Strict);
        var health = new AuditPipelineHealth();
        var sink = new LoggingAuditSink(logger, diagnostics.Object, health);

        sink.Record(new AuditEvent(AuditEventNames.PortalCreated) { PortalId = -1, ActorUserId = 4 });

        diagnostics.Verify(
            diagnostic => diagnostic.Record(
                SecurityDiagnosticEvent.AuditRecordNotWritten,
                -1,
                4,
                "LogLevelDisabled"),
            Times.Once);
        health.FailureCount.Should().Be(1);
    }

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        /// <summary>Gets or sets whether the logger accepts the requested level.</summary>
        public bool Enabled { get; init; } = true;

        /// <summary>Gets or sets the error raised instead of accepting a log entry.</summary>
        public Exception? Failure { get; init; }

        /// <summary>Gets the last rendered message.</summary>
        public string Message { get; private set; } = string.Empty;

        /// <summary>Gets the last structured state.</summary>
        public IReadOnlyDictionary<string, object?> Properties { get; private set; } =
            new Dictionary<string, object?>(StringComparer.Ordinal);

        /// <inheritdoc />
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        /// <inheritdoc />
        public bool IsEnabled(LogLevel logLevel) => Enabled;

        /// <inheritdoc />
        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (Failure is not null)
            {
                throw Failure;
            }

            Message = formatter(state, exception);
            Properties = state is IEnumerable<KeyValuePair<string, object?>> structured
                ? structured.ToDictionary(property => property.Key, property => property.Value, StringComparer.Ordinal)
                : new Dictionary<string, object?>(StringComparer.Ordinal);
        }
    }
}
