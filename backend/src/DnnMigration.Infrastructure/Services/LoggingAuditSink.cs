using System.Collections;
using DnnMigration.Application.Abstractions;
using DnnMigration.Domain.Abstractions.Services;
using DnnMigration.Domain.Enums;
using DnnMigration.Infrastructure.HealthChecks;
using Microsoft.Extensions.Logging;

namespace DnnMigration.Infrastructure.Services;

/// <summary>Writes audit records to the host's structured logging pipeline.</summary>
/// <remarks>
/// <para>
/// Registered as a singleton and holding no per-request state. The logger and diagnostics route are
/// singleton framework services, and the shared health collaborator contains only an atomic saturating
/// counter, so concurrent requests neither share mutable event data nor contend on a queue.
/// </para>
/// <para>
/// Completed operations are informational; denied and failed operations are warnings. Denials are raised
/// because a run of refused sign-ins or privilege checks is an operator-visible security signal, while a
/// completed administrative change is ordinary business activity.
/// </para>
/// </remarks>
internal sealed class LoggingAuditSink : IAuditSink
{
    /// <summary>The single, fixed message template every audit record is written with.</summary>
    private const string MessageTemplate =
        "Audit {AuditEvent} {AuditOutcome} portal={AuditPortalId} actor={AuditActorUserId} "
        + "subject={AuditSubjectUserId} resource={AuditResourceType}/{AuditResourceId} "
        + "failure={AuditFailureCode} details={AuditPropertyCount} "
        + "withheld={AuditPropertyWithheldCount}";

    /// <summary>The maximum number of allowlisted metadata values one record may add.</summary>
    private const int MaximumMetadataCount = 16;

    /// <summary>The maximum length of one metadata value.</summary>
    private const int MaximumMetadataValueLength = 128;

    /// <summary>The fixed substitute for a value that is not safe to write.</summary>
    private const string RejectedMetadataValue = "rejected";

    /// <summary>
    /// Maps the only producer keys the logging pipeline accepts to their structured property names.
    /// </summary>
    /// <remarks>
    /// THIS ALLOWLIST IS A CROSS-CUTTING COUPLING AND HAS TO BE EXTENDED WHENEVER A SERVICE RECORDS A NEW
    /// PROPERTY. A key that is absent here is WITHHELD - counted, not written - which is the correct
    /// default for caller-shaped text and a silent loss for a property a service deliberately added.
    /// </remarks>
    private static readonly IReadOnlyDictionary<string, string> AllowedMetadata = BuildAllowedMetadata();

    /// <summary>
    /// Stands in for a control character in a rendered property, so that no value can forge a line break,
    /// repaint a terminal, or otherwise alter the structure of the record that contains it.
    /// </summary>
    private const char ControlCharacterReplacement = '\uFFFD';

    /// <summary>The logger every record is written through.</summary>
    private readonly ILogger<LoggingAuditSink> _logger;

    /// <summary>The bounded secondary channel used when the primary logger faults.</summary>
    private readonly ISecurityDiagnostics _diagnostics;

    /// <summary>The process-local health signal recording every failed primary delivery.</summary>
    private readonly AuditPipelineHealth _health;

    /// <summary>Initialises a new instance of the <see cref="LoggingAuditSink"/> class.</summary>
    /// <param name="logger">The logger records are written through.</param>
    /// <param name="diagnostics">The bounded fallback signal used when the logger faults.</param>
    /// <param name="health">The process-local failed-delivery counter surfaced by the health endpoint.</param>
    /// <exception cref="ArgumentNullException">Any argument is <see langword="null"/>.</exception>
    public LoggingAuditSink(
        ILogger<LoggingAuditSink> logger,
        ISecurityDiagnostics diagnostics,
        AuditPipelineHealth health)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(diagnostics);
        ArgumentNullException.ThrowIfNull(health);

        _logger = logger;
        _diagnostics = diagnostics;
        _health = health;
    }

    /// <inheritdoc />
    /// <remarks>
    /// Honours the contract's absolute guarantee that recording never throws. Three things could plausibly
    /// throw here and all three are contained: a null event, which is discarded; a logger or sink that
    /// faults; and projection of a caller-supplied property dictionary, which is performed inside the same
    /// guarded region rather than before it.
    /// </remarks>
    public void Record(AuditEvent auditEvent)
    {
        if (auditEvent is null)
        {
            return;
        }

        try
        {
            LogLevel level = auditEvent.Outcome == AuditOutcome.Succeeded
                ? LogLevel.Information
                : LogLevel.Warning;

            if (!_logger.IsEnabled(level))
            {
                ReportFailure(auditEvent, "LogLevelDisabled", failure: null);
                return;
            }

            AuditLogState state = BuildState(auditEvent);

            _logger.Log(
                level,
                EventIdFor(state.EventName),
                state,
                exception: null,
                static (logState, _) => logState.ToString());
        }
        catch (Exception exception)
        {
            ReportFailure(auditEvent, exception.GetType().Name, exception);
        }
    }

    /// <summary>
    /// Selects the stable numeric identifier the record is written under, from the family the event belongs
    /// to.
    /// </summary>
    /// <param name="eventName">The event name the record carries.</param>
    /// <returns>The identifier and its symbolic name.</returns>
    private static EventId EventIdFor(string eventName) => eventName switch
    {
        AuditEventNames.LoginSuccess
            or AuditEventNames.LoginSuperUser
            or AuditEventNames.LoginFailure
            or AuditEventNames.LoginUserLockedOut
            or AuditEventNames.LoginUserNotApproved => new EventId(1001, "SignInOutcome"),

        AuditEventNames.PortalCreated
            or AuditEventNames.HostAlert => new EventId(1002, "PortalInstalled"),

        AuditEventNames.SessionRenewed
            or AuditEventNames.SessionRefused
            or AuditEventNames.SessionEnded => new EventId(1003, "SessionLifecycle"),

        _ => new EventId(1000, "Audit"),
    };

    /// <summary>Projects an application event onto the fixed structured logging contract.</summary>
    /// <param name="auditEvent">The event to project.</param>
    /// <returns>A bounded state object carrying only allowlisted properties.</returns>
    private static AuditLogState BuildState(AuditEvent auditEvent)
    {
        // An event with no name is unusable to a reader, so both failure modes collapse to the same loud
        // stand-in: there is no legitimate absent case here.
        string eventName = SanitiseCode(
            auditEvent.EventName,
            absentValue: "UNRECOGNISED_AUDIT_EVENT",
            unsafeValue: "UNRECOGNISED_AUDIT_EVENT")!;

        // A resource attribution is genuinely optional - a session event names no resource - so absence is
        // recorded as absence, while a supplied value that cannot be written still leaves its mark.
        string? resourceType = SanitiseCode(
            auditEvent.ResourceType,
            absentValue: null,
            unsafeValue: RejectedMetadataValue);
        string? resourceId = SanitiseCode(
            auditEvent.ResourceId,
            absentValue: null,
            unsafeValue: RejectedMetadataValue);

        // ⚠ ABSENT MEANS SUCCEEDED, AND MUST NOT READ AS A REFUSAL. Every producer sets this to null when the
        // outcome was accepted, so the absent case has to stay null for the field to mean anything: with the
        // not-safe-to-write substitute here instead, all 218 successful outcomes in a QA run carried
        // failure="rejected".
        string? failureCode = SanitiseCode(
            auditEvent.FailureCode,
            absentValue: null,
            unsafeValue: RejectedMetadataValue);

        List<KeyValuePair<string, object?>> properties =
        [
            new("AuditEvent", eventName),
            new("AuditOutcome", auditEvent.Outcome),
            new("AuditPortalId", auditEvent.PortalId),
            new("AuditActorUserId", auditEvent.ActorUserId),
            new("AuditSubjectUserId", auditEvent.SubjectUserId),
            new("AuditResourceType", resourceType),
            new("AuditResourceId", resourceId),
            new("AuditFailureCode", failureCode),
        ];

        int acceptedMetadata = 0;
        foreach (KeyValuePair<string, string?> property in auditEvent.Properties)
        {
            if (acceptedMetadata >= MaximumMetadataCount)
            {
                break;
            }

            if (!AllowedMetadata.TryGetValue(property.Key, out string? outputName))
            {
                continue;
            }

            properties.Add(new KeyValuePair<string, object?>(
                outputName,
                SanitiseMetadataValue(property.Value)));
            acceptedMetadata++;
        }

        int withheldMetadata = auditEvent.Properties.Count - acceptedMetadata;

        properties.Add(new KeyValuePair<string, object?>("AuditPropertyCount", acceptedMetadata));
        properties.Add(new KeyValuePair<string, object?>("AuditPropertyWithheldCount", withheldMetadata));
        properties.Add(new KeyValuePair<string, object?>("{OriginalFormat}", MessageTemplate));

        string message = FormattableString.Invariant(
            $"Audit {eventName} {auditEvent.Outcome} portal={auditEvent.PortalId} actor={auditEvent.ActorUserId} subject={auditEvent.SubjectUserId} resource={resourceType}/{resourceId} failure={failureCode} details={acceptedMetadata} withheld={withheldMetadata}");

        return new AuditLogState(eventName, message, properties);
    }

    /// <summary>Builds the closed metadata-key vocabulary accepted from producers.</summary>
    private static IReadOnlyDictionary<string, string> BuildAllowedMetadata()
    {
        string[] keys =
        [
            "AccountRemoved",
            "AdministratorId",
            "Advisory",
            "AffectedTabCount",
            "AliasesReleased",
            "ApplyToAllModules",
            "Approved",
            "AutoAssignment",
            "BusinessControllerRegistered",
            "DescriptionSupplied",
            "DiagnosticType",
            "EffectiveDate",
            "EffectCount",
            "Expired",
            "ExpiryDate",
            "IsChildPortal",
            "IsDeleted",
            "IsVisible",
            "KeywordsSupplied",
            "LineNumber",
            "LinePosition",

            // Portal creation PROCEEDS when the installation's page-permission catalogue does not define a
            // key its home page would have been granted - the legacy template parser iterated an empty
            // catalogue answer and created the portal regardless - and these three facts are what make the
            // resulting record actionable: which scope code was consulted, and which of the two keys was
            // absent.
            "MissingEditDefinition",
            "MissingViewDefinition",
            "MustChangePassword",
            "MustUpdateProfile",
            "Operation",
            "ParentId",
            "PayloadLength",
            "PermissionCode",
            "PlacementCount",
            "PreviousFormat",
            "PreviousParentId",
            "Renewed",
            "ReplacementKind",
            "SetAsDefaultSettings",
            "TabId",
            "TabModuleId",
            "Version",
        ];

        return keys.ToDictionary(
            key => key,
            key => "AuditMetadata_" + key,
            StringComparer.Ordinal);
    }

    /// <summary>Reduces a metadata value to a bounded, single-field scalar.</summary>
    private static string? SanitiseMetadataValue(string? value)
    {
        if (value is null)
        {
            return null;
        }

        if (value.Length > MaximumMetadataValueLength)
        {
            return RejectedMetadataValue;
        }

        foreach (char character in value)
        {
            if (char.IsControl(character)
                || character is ';' or '=' or '\u2028' or '\u2029')
            {
                return RejectedMetadataValue;
            }
        }

        return value;
    }

    /// <summary>Reduces an envelope value to a short machine-readable code.</summary>
    /// <param name="value">The value to reduce, which may legitimately be absent.</param>
    /// <param name="absentValue">What to record when the producer supplied nothing at all.</param>
    /// <param name="unsafeValue">What to record when a value WAS supplied but cannot be written.</param>
    /// <returns>The value, or one of the two substitutes.</returns>
    /// <remarks>
    /// <para>
    /// <strong>ABSENT AND UNSAFE ARE DIFFERENT ANSWERS AND THE CALLER CHOOSES BOTH.</strong> These were one
    /// parameter, and collapsing them was a reporting defect rather than a stylistic one: a successful outcome
    /// carries no failure code by construction - every producer writes <c>Outcome == Succeeded ? null : code</c>
    /// - so feeding the not-safe-to-write substitute in for an absent value stamped
    /// <see cref="RejectedMetadataValue"/> onto the audit record of every success. A reader filtering the log
    /// for refusals then matched all of them, which is the exact opposite of what the field is for.
    /// </para>
    /// <para>
    /// Keeping the unsafe substitute separate is what stops the fix from trading one silent failure for
    /// another. A value that arrives malformed must still leave a mark, because "the producer named a resource
    /// and it could not be written" is evidence; folding that into absence would delete it.
    /// </para>
    /// </remarks>
    private static string? SanitiseCode(string? value, string? absentValue, string? unsafeValue)
    {
        // The producer supplied nothing. This is a legitimate state for a failure code on a success and for a
        // resource attribution on an event that names no resource, so it is answered separately from the
        // malformed cases below.
        if (string.IsNullOrEmpty(value))
        {
            return absentValue;
        }

        if (value.Length > 64)
        {
            return unsafeValue;
        }

        foreach (char character in value)
        {
            bool permitted = character is >= 'a' and <= 'z'
                or >= 'A' and <= 'Z'
                or >= '0' and <= '9'
                or '.' or '_' or '-' or ':';

            if (!permitted)
            {
                return unsafeValue;
            }
        }

        return value;
    }

    /// <summary>Records a failed primary delivery without letting either fallback escape.</summary>
    private void ReportFailure(AuditEvent auditEvent, string reasonCode, Exception? failure)
    {
        _health.RecordFailure();

        // Two channels, deliberately, because they answer different questions and fail independently.
        try
        {
            AuditSinkDiagnostics.Instance.ReportLoss(auditEvent.EventName, failure);
        }
        catch (Exception)
        {
            // A diagnostic channel must never turn a recorded operation into a failed one.
        }

        try
        {
            _diagnostics.Record(
                SecurityDiagnosticEvent.AuditRecordNotWritten,
                auditEvent.PortalId,
                auditEvent.ActorUserId,
                reasonCode);
        }
        catch (Exception)
        {
            // The diagnostics contract already requires containment. This second guard defends the audit
            // contract even from a non-conforming replacement supplied by a host or a test.
        }
    }

    /// <summary>A logging state whose key/value enumeration becomes structured sink properties.</summary>
    private sealed class AuditLogState : IReadOnlyList<KeyValuePair<string, object?>>
    {
        private readonly string _message;
        private readonly IReadOnlyList<KeyValuePair<string, object?>> _properties;

        /// <summary>Initialises one immutable logging state.</summary>
        public AuditLogState(
            string eventName,
            string message,
            IReadOnlyList<KeyValuePair<string, object?>> properties)
        {
            EventName = eventName;
            _message = message;
            _properties = properties;
        }

        /// <summary>Gets the sanitised event name used for family selection.</summary>
        public string EventName { get; }

        /// <inheritdoc />
        public int Count => _properties.Count;

        /// <inheritdoc />
        public KeyValuePair<string, object?> this[int index] => _properties[index];

        /// <inheritdoc />
        public IEnumerator<KeyValuePair<string, object?>> GetEnumerator() => _properties.GetEnumerator();

        /// <inheritdoc />
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        /// <inheritdoc />
        public override string ToString() => _message;
    }
}
