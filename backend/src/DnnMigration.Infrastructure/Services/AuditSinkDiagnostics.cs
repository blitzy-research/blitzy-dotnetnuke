using System.Diagnostics.Tracing;

namespace DnnMigration.Infrastructure.Services;

/// <summary>
/// Reports audit records that could not be written, on a channel that does not depend on the pipeline they
/// failed to reach.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a second channel exists.</b> <see cref="LoggingAuditSink"/> must never throw: by the time it
/// runs, the operation it describes has already been committed, so a fault while taking the note cannot be
/// allowed to destroy the request.
/// </para>
/// <para>
/// <b>Why an event source and a counter.</b> Both are in the base class library, so this layer takes no new
/// dependency to report a loss - which matters, because this project deliberately references no logging
/// library at all: it owns persistence, security and health, and the logging library belongs to the host.
/// </para>
/// </remarks>
[EventSource(Name = "DnnMigration-Audit")]
internal sealed class AuditSinkDiagnostics : EventSource
{
    /// <summary>Identifier of the lost-record event.</summary>
    private const int AuditRecordLostEventId = 1;

    /// <summary>Reported in place of a failure whose type cannot be named.</summary>
    private const string UnknownFailureType = "(unknown)";

    private long _lostRecords;

    /// <summary>Initialises a new instance of the <see cref="AuditSinkDiagnostics"/> class.</summary>
    private AuditSinkDiagnostics()
    {
    }

    /// <summary>Gets the single instance for this process.</summary>
    /// <remarks>
    /// A static instance rather than a registered service, deliberately. This is reached from the one place
    /// in the solution where the container's own services cannot be relied upon - a handler that runs
    /// because something in the logging pipeline has already failed - so it must not itself require
    /// resolution to work.
    /// </remarks>
    internal static AuditSinkDiagnostics Instance { get; } = new();

    /// <summary>Gets the number of audit records this process has failed to write.</summary>
    internal long LostRecordCount => Interlocked.Read(ref _lostRecords);

    /// <summary>
    /// Records that one audit record could not be written, and why - without naming any value it carried.
    /// </summary>
    /// <param name="eventName">The event name of the record that was lost.</param>
    /// <param name="failure">The failure that prevented it from being written.</param>
    /// <remarks>
    /// Cannot throw, which is a requirement rather than an observation: it is called from the one handler
    /// in the solution that has promised its caller never to fail. Incrementing the counter is atomic and
    /// cannot fail, and an event source contains its own write failures rather than surfacing them.
    /// </remarks>
    internal void ReportLoss(string? eventName, Exception? failure)
    {
        Interlocked.Increment(ref _lostRecords);

        AuditRecordLost(
            eventName ?? UnknownFailureType,
            failure?.GetType().FullName ?? UnknownFailureType);
    }

    /// <summary>Writes the lost-record event.</summary>
    /// <param name="eventName">The event name of the record that was lost.</param>
    /// <param name="failureType">The type name of the failure that prevented the write.</param>
    /// <remarks>
    /// Public because the event-source infrastructure requires event methods to be public, non-virtual and
    /// void-returning in order to generate a manifest for them; the declaring type is internal, so the
    /// effective reach is unchanged.
    /// </remarks>
    [Event(
        AuditRecordLostEventId,
        Level = EventLevel.Error,
        Message = "An audit record was lost. Event name: {0}. Failure type: {1}.")]
    public void AuditRecordLost(string eventName, string failureType)
    {
        WriteEvent(AuditRecordLostEventId, eventName, failureType);
    }
}
