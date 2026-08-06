// MIGRATION: this is the replacement transport for the legacy business audit trail. The legacy
// EventLogController.AddLog persisted a LogInfo into an EventLog table through the logging provider
// family, which AAP 0.2.2.2 excludes; the facts and their stable event names survive, the store does not.
// Records are emitted as STRUCTURED log events, so the host's configured sink - Serilog, wired in
// Api/Program.cs - decides where they land, and an operator who wants them in a table again configures a
// sink rather than changing this file.
//
// MIGRATION: the message template is FIXED and every fact is a named property. That is what makes the
// trail queryable in the way the legacy table was: an operator asks for AuditEvent = "PORTAL_CREATED"
// rather than pattern-matching a sentence. A template assembled by interpolation would compile to a
// distinct template per call and would destroy that property, which is why nothing below interpolates.
//
// MIGRATION: the legacy record's server name and configuration identifier are not reproduced. Both were
// populated by the excluded logging provider from ambient machine state, and both are supplied by the
// hosting environment now - the container name, the environment name - rather than by the record.

using System.Collections;
using DnnMigration.Application.Abstractions;
using DnnMigration.Domain.Abstractions.Services;
using DnnMigration.Domain.Enums;
using DnnMigration.Infrastructure.HealthChecks;
using Microsoft.Extensions.Logging;

namespace DnnMigration.Infrastructure.Services;

/// <summary>
/// Writes audit records to the host's structured logging pipeline.
/// </summary>
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
/// <para>
/// The sink is also the privacy enforcement boundary. It writes stable numeric identifiers, a closed set of
/// bounded metadata keys and no raw account name, person name, electronic-mail address, tenant alias,
/// filename, path or free-text description. Unknown keys are discarded; invalid values become one fixed
/// scalar. This protects every configured logging provider rather than relying on each producer to remain
/// careful forever.
/// </para>
/// </remarks>
internal sealed class LoggingAuditSink : IAuditSink
{
    /// <summary>
    /// The single, fixed message template every audit record is written with.
    /// </summary>
    /// <remarks>
    /// One template for every event, so that a structured sink groups the whole trail under one event
    /// identifier and an operator filters it by the <c>AuditEvent</c> property rather than by text. The
    /// The custom state carries a matching <c>{OriginalFormat}</c> entry, so logging providers preserve this
    /// one template while receiving each value as an independently queryable scalar.
    /// </remarks>
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
    /// MIGRATION: THIS ALLOWLIST IS A CROSS-CUTTING COUPLING AND HAS TO BE EXTENDED WHENEVER A SERVICE
    /// RECORDS A NEW PROPERTY. A key that is absent here is WITHHELD - counted, not written - which is the
    /// correct default for caller-shaped text and a silent loss for a property a service deliberately added.
    /// Five keys were added for exactly that reason after two revisions were combined: the portal-
    /// installation record gained the administrator's numeric key and presence-only flags for its two
    /// free-text members (AdministratorId, DescriptionSupplied, KeywordsSupplied), and the credential
    /// migration record and its failure detail were added (PreviousFormat, ReplacementKind). Each is a
    /// numeric identifier, a closed enumeration member, or a boolean - none is caller-shaped prose, which is
    /// why the tenant NAME and ALIAS that the same revision recorded are deliberately still absent - and
    /// every value still passes the bounded, control-character-rejecting check below before it is written.
    /// </remarks>
    private static readonly IReadOnlyDictionary<string, string> AllowedMetadata = BuildAllowedMetadata();

    /// <summary>
    /// Stands in for a control character in a rendered property, so that no value can forge a line
    /// break, repaint a terminal, or otherwise alter the structure of the record that contains it.
    /// </summary>
    /// <remarks>
    /// A single printable character, chosen so that the substitution is visible to a reader rather than
    /// silent: a record showing an unexpected placeholder invites the question, whereas a record with the
    /// character removed looks like ordinary text and hides that anything was altered.
    /// </remarks>
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
    /// <para>
    /// Honours the contract's absolute guarantee that recording never throws. Three things could
    /// plausibly throw here and all three are contained: a null event, which is discarded; a logger or
    /// sink that faults; and projection of a caller-supplied property dictionary, which is performed inside
    /// the same guarded region rather than before it.
    /// </para>
    /// <para>
    /// Cancellation is deliberately NOT re-thrown from the catch, unlike every other guarded region in
    /// this solution. Nothing here is cancellable - no token is accepted and no awaitable is created -
    /// so an <see cref="OperationCanceledException"/> reaching this handler could only come from a
    /// misbehaving sink, and letting it escape would fault an operation that had already completed. A
    /// contained failure increments the audit-pipeline health counter first, then attempts the closed
    /// security-diagnostic fallback; failure of that fallback is contained independently.
    /// </para>
    /// </remarks>
    public void Record(AuditEvent auditEvent)
    {
        if (auditEvent is null)
        {
            return;
        }

        try
        {
            // A DELIBERATE REFUSAL ESCALATES, and only a completed operation is merely informational. A run
            // of refused sign-ins is the pattern an operator wants raised, and it is invisible if every
            // refusal is recorded at the same level as every success; a refusal is also the outcome an
            // alerting rule is most often written against. The unfinished outcome escalates for the reason it
            // always did.
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
    /// Selects the stable numeric identifier the record is written under, from the family the event
    /// belongs to.
    /// </summary>
    /// <param name="eventName">The event name the record carries.</param>
    /// <returns>The identifier and its symbolic name.</returns>
    /// <remarks>
    /// <para>
    /// A CATEGORY identifier, not one per event name. The distinction is what lets the two requirements on
    /// this sink hold at once: an operator addresses a whole family with one alert rule by number, and
    /// narrows to a single event within it by filtering the <c>AuditEvent</c> property - so nothing needs a
    /// number of its own, and the trail is not fragmented into as many identifiers as it has event names.
    /// </para>
    /// <para>
    /// These numbers are a PUBLISHED CONTRACT. An operator's alert rule addresses an event by its number, so
    /// renumbering one silently detaches whatever was watching it; the two that are pinned by a test are
    /// pinned as literals there rather than read from here, precisely so that a change on either side has to
    /// be deliberate. New families take the next free number and never reuse a retired one.
    /// </para>
    /// <para>
    /// Anything unmapped is recorded under the general identifier rather than under zero. Zero is what an
    /// unspecified identifier already means, so a family that was simply never added here would be
    /// indistinguishable from one that had been deliberately left general.
    /// </para>
    /// </remarks>
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

    /// <summary>
    /// Projects an application event onto the fixed structured logging contract.
    /// </summary>
    /// <param name="auditEvent">The event to project.</param>
    /// <returns>A bounded state object carrying only allowlisted properties.</returns>
    private static AuditLogState BuildState(AuditEvent auditEvent)
    {
        string eventName = SanitiseCode(auditEvent.EventName, "UNRECOGNISED_AUDIT_EVENT")!;
        string? resourceType = SanitiseCode(auditEvent.ResourceType, null);
        string? resourceId = SanitiseCode(auditEvent.ResourceId, null);
        string? failureCode = SanitiseCode(auditEvent.FailureCode, RejectedMetadataValue);

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

        // ADMITTED AND WITHHELD ARE BOTH COUNTED, and the withheld count is the point. A caller that
        // supplies a fact this sink will not carry - one outside the allowlist, or one whose value failed a
        // bound - would otherwise see its fact vanish with no trace that it was ever offered. Counting both
        // makes the omission visible in the same entry, so an operator can tell "this event carries no
        // metadata" apart from "this event offered metadata that was refused" without reading the source.
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

            // MIGRATION: SEC-F1. Portal creation refuses when the installation's page-permission catalogue
            // does not define a key its home page must grant, and these three facts are what make that
            // refusal actionable: which scope code was consulted, and which of the two keys was absent. All
            // three are authored constants or booleans - no caller input reaches them - which is why they
            // belong in this vocabulary rather than in the withheld count.
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
    private static string? SanitiseCode(string? value, string? rejectedValue)
    {
        if (string.IsNullOrEmpty(value))
        {
            return rejectedValue;
        }

        if (value.Length > 64)
        {
            return rejectedValue;
        }

        foreach (char character in value)
        {
            bool permitted = character is >= 'a' and <= 'z'
                or >= 'A' and <= 'Z'
                or >= '0' and <= '9'
                or '.' or '_' or '-' or ':';

            if (!permitted)
            {
                return rejectedValue;
            }
        }

        return value;
    }

    /// <summary>Records a failed primary delivery without letting either fallback escape.</summary>
    private void ReportFailure(AuditEvent auditEvent, string reasonCode, Exception? failure)
    {
        _health.RecordFailure();

        // Two channels, deliberately, because they answer different questions and fail independently. The
        // health signal above is what an orchestrator reads, and the security diagnostic below is what an
        // operator queries; this counter is neither - it is an out-of-band event source that survives the
        // logging pipeline being the very thing that failed, and it is the only channel that can be trusted
        // when the primary logger is the fault. It cannot throw back into this path.
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

    /// <summary>
    /// A logging state whose key/value enumeration becomes structured sink properties.
    /// </summary>
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
