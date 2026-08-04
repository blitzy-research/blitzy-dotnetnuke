using DnnMigration.Application.Abstractions;
using Microsoft.Extensions.Logging;

namespace DnnMigration.Infrastructure.Services;

/// <summary>
/// Writes audit facts as structured log events.
/// </summary>
/// <remarks>
/// <para>
/// This is the class the logging package is needed for, which is why it lives here and the contract it
/// implements lives in the Application layer. AAP 0.7.5.3 sets the target mechanism explicitly: the legacy
/// audit sites "become Serilog structured events at the Application service layer, with legacy event-type
/// members mapped to stable log event names so audit intent survives the change of mechanism". The mapping is
/// the event name itself, carried through unchanged.
/// </para>
/// <para>
/// Structured rather than formatted. The event name and every property are attached as named values, so a log
/// sink can be queried by them - "every PORTAL_DELETED in the last month" is a filter over a field, not a
/// substring search over a rendered sentence. That is what replaces the legacy <c>EventLog</c> table's
/// queryable columns; a message assembled by string concatenation would have thrown that away.
/// </para>
/// <para>
/// Recorded at information level, not warning. An audit fact is a record of something that happened, not a
/// report that something is wrong: a tenant being deleted is an ordinary administrative act and should not
/// raise an operational alarm. The legacy code made the opposite choice at one site - the installation entry
/// was typed <c>HOST_ALERT</c> even though the enumeration offered <c>PORTAL_CREATED</c>, and it set
/// <c>BypassBuffering</c> so the entry was written through immediately. The alarm-level typing is not
/// reproduced, because the name now carries the meaning; the write-through intent is preserved differently and
/// more simply, in that nothing here batches.
/// </para>
/// </remarks>
internal sealed class AuditLog : IAuditLog
{
    /// <summary>The event identifier every audit record carries, so a sink can select audit records alone.</summary>
    /// <remarks>
    /// A single identifier for the whole category rather than one per event name: the name is already a
    /// property, and minting an integer per name would create a second vocabulary to keep in step with the
    /// first.
    /// </remarks>
    private static readonly EventId AuditEventId = new(1000, "Audit");

    private readonly ILogger<AuditLog> _logger;

    /// <summary>Initialises a new instance of the <see cref="AuditLog"/> class.</summary>
    /// <param name="logger">The logger the records are written to.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="logger"/> is null.</exception>
    public AuditLog(ILogger<AuditLog> logger) =>
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    /// <inheritdoc />
    public Task RecordAsync(
        string eventName,
        IReadOnlyDictionary<string, string?> properties,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(eventName);
        ArgumentNullException.ThrowIfNull(properties);

        // An abandoned record is a LOST record, never a failure of the operation being audited. The operation
        // has already completed by the time it is audited, so throwing here would report a completed act as
        // failed - the one outcome an audit trail must never be able to cause.
        if (cancellationToken.IsCancellationRequested)
        {
            return Task.CompletedTask;
        }

        // The properties are attached as a scope so that every one of them is an addressable field on the
        // event, whatever the sink. Composing them into the message template instead would make the event name
        // the only queryable part and turn the rest into prose.
        using (_logger.BeginScope(properties.ToDictionary(entry => entry.Key, entry => (object?)entry.Value)))
        {
            _logger.LogInformation(AuditEventId, "Audit {AuditEvent}", eventName);
        }

        return Task.CompletedTask;
    }
}
