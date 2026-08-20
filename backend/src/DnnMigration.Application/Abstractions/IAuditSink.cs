namespace DnnMigration.Application.Abstractions;

/// <summary>
/// Receives the durable account of business and security facts that the application layer decides are worth
/// keeping.
/// </summary>
/// <remarks>
/// <para>
/// First, <see cref="Record"/> MUST NOT THROW, for any input, ever. An audit record is a by-product of an
/// operation that has already succeeded; a sink that threw would turn a completed sign-in or a committed
/// portal creation into a server fault, losing the operation as well as the record.
/// </para>
/// <para>
/// Second, it must not perform work whose failure or latency the caller has to reason about - no database
/// write, no network call, no file handle opened per event. The shipped implementation writes to the host's
/// structured logging pipeline, which buffers.
/// </para>
/// </remarks>
public interface IAuditSink
{
    /// <summary>Records one audited fact.</summary>
    /// <param name="auditEvent">The fact to record.</param>
    void Record(AuditEvent auditEvent);
}
