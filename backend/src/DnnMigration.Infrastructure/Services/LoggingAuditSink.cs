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

using DnnMigration.Application.Abstractions;
using Microsoft.Extensions.Logging;

namespace DnnMigration.Infrastructure.Services;

/// <summary>
/// Writes audit records to the host's structured logging pipeline.
/// </summary>
/// <remarks>
/// <para>
/// Stateless and therefore registered as a singleton. It holds a logger and nothing else: no buffer, no
/// queue and no per-request field, so there is nothing for two concurrent requests to contend over.
/// </para>
/// <para>
/// Every audit record is written at information level, including the refusals. A refusal is a normal,
/// expected outcome of an authentication endpoint - it is not a warning about the server's health - and
/// promoting it would make a brute-force attempt indistinguishable from a misconfiguration in an
/// operator's alerting. The one exception is <see cref="AuditOutcome.Failed"/>, which by definition
/// describes something that should have worked and is the reason the level is chosen per record rather
/// than fixed.
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
    /// placeholder order matches the argument order of the logging call below exactly; Microsoft's
    /// logging abstraction binds these POSITIONALLY, not by name, so reordering one without the other
    /// would silently mislabel every property.
    /// </remarks>
    private const string MessageTemplate =
        "Audit {AuditEvent} {AuditOutcome} portal={AuditPortalId} actor={AuditActorUserId} "
        + "actorName={AuditActorUserName} subject={AuditSubjectUserId} "
        + "resource={AuditResourceType}/{AuditResourceId} failure={AuditFailureCode} "
        + "detail={AuditProperties}";

    /// <summary>The logger every record is written through.</summary>
    private readonly ILogger<LoggingAuditSink> _logger;

    /// <summary>Initialises a new instance of the <see cref="LoggingAuditSink"/> class.</summary>
    /// <param name="logger">The logger records are written through.</param>
    /// <exception cref="ArgumentNullException"><paramref name="logger"/> is <see langword="null"/>.</exception>
    public LoggingAuditSink(ILogger<LoggingAuditSink> logger)
    {
        ArgumentNullException.ThrowIfNull(logger);

        _logger = logger;
    }

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// Honours the contract's absolute guarantee that recording never throws. Three things could
    /// plausibly throw here and all three are contained: a null event, which is discarded; a logger or
    /// sink that faults, which is swallowed; and the property rendering, which is performed inside the
    /// same guarded region rather than before it.
    /// </para>
    /// <para>
    /// Cancellation is deliberately NOT re-thrown from the catch, unlike every other guarded region in
    /// this solution. Nothing here is cancellable - no token is accepted and no awaitable is created -
    /// so an <see cref="OperationCanceledException"/> reaching this handler could only come from a
    /// misbehaving sink, and letting it escape would fault an operation that had already completed.
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
                return;
            }

            _logger.Log(
                level,
                EventIdFor(auditEvent.EventName),
                MessageTemplate,
                auditEvent.EventName,
                auditEvent.Outcome,
                auditEvent.PortalId,
                auditEvent.ActorUserId,
                auditEvent.ActorUserName,
                auditEvent.SubjectUserId,
                auditEvent.ResourceType,
                auditEvent.ResourceId,
                auditEvent.FailureCode,
                RenderProperties(auditEvent));
        }
        catch (Exception)
        {
            // Contained deliberately, and this handler is empty by design rather than by omission. The
            // contract states that recording must never throw for any reason: the caller has already
            // committed the operation being described, so escalating a logging fault would destroy a
            // successful request in order to complain about the note taken of it. There is nowhere to
            // report the loss either - the only channel available is the one that just failed.
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

        AuditEventNames.PortalCreated => new EventId(1002, "PortalInstalled"),

        AuditEventNames.SessionRenewed
            or AuditEventNames.SessionRefused
            or AuditEventNames.SessionEnded => new EventId(1003, "SessionLifecycle"),

        _ => new EventId(1000, "Audit"),
    };

    /// <summary>
    /// Renders an event's descriptive properties as one short, stable string.
    /// </summary>
    /// <param name="auditEvent">The event whose properties are being rendered.</param>
    /// <returns>
    /// A <c>key=value</c> list joined by <c>"; "</c>, or <see langword="null"/> when the event carries no
    /// properties, so an absent detail reads as absent rather than as an empty string.
    /// </returns>
    /// <remarks>
    /// <para>
    /// Rendered rather than passed through as a dictionary because the destructuring behaviour of a
    /// collection property differs between logging sinks: one writes a structured map, another calls
    /// <c>ToString</c> and records the type name. A deterministic string reads the same through every
    /// sink, which matters more for an audit trail than nesting does.
    /// </para>
    /// <para>
    /// Keys are emitted in the order the caller supplied them, so a record written twice for the same
    /// operation renders identically and can be compared textually. A null value is rendered as the empty
    /// string after its separator, which distinguishes "the key was recorded with no value" from "the key
    /// was not recorded".
    /// </para>
    /// </remarks>
    private static string? RenderProperties(AuditEvent auditEvent)
    {
        if (auditEvent.Properties.Count == 0)
        {
            return null;
        }

        return string.Join(
            "; ",
            auditEvent.Properties.Select(property => $"{property.Key}={property.Value}"));
    }
}
