// MIGRATION: this abstraction is what makes the legacy business audit trail survive the loss of the
// mechanism that carried it. The legacy trail was written by
// DotNetNuke.Services.Log.EventLog.EventLogController.AddLog, which persisted a LogInfo through the
// LOGGING PROVIDER FAMILY that AAP 0.2.2.2 places out of scope - so the store is gone, but the nineteen
// in-scope audit sites and their stable event names are not, and dropping them would delete evidence an
// operator relies on. Generic request logging cannot stand in for it: a line recording method, path,
// status and elapsed time cannot say WHICH portal was created, WHICH account was refused or WHY, and it
// cannot carry the legacy event name at all.
//
// MIGRATION: PACKAGE-NEUTRAL BY CONSTRUCTION, and that is the whole reason this contract exists rather
// than a logger being injected directly. This project declares exactly two package references,
// FluentValidation and its dependency-injection extensions, plus one project reference to the domain
// layer. Microsoft.Extensions.Logging.Abstractions is neither among them nor present in the reference
// pack a class library targets, so naming ILogger<T> in an application service does not compile - a fact
// already recorded at Services/AuthService.cs:L77-L79 and Services/PortalService.cs:L662-L663. Adding the
// package would put a logging-framework dependency in the layer that owns the business rules and would
// couple every service to one telemetry vendor. The application layer therefore states WHAT is worth
// recording and the infrastructure layer decides HOW, which is the same inversion already used for the
// clock, the cache and the host settings.
//
// MIGRATION: there is no event bus, no domain-event dispatcher and no outbox behind this. The legacy
// in-scope event model was seven Web Forms control events on ONE file
// (Library/Components/Users/UserUserControlBase.vb:L59-L65), which become Angular component outputs;
// inventing server-side event infrastructure to carry an audit record would be scope creep dressed as
// fidelity. A sink writes the record and returns.

namespace DnnMigration.Application.Abstractions;

/// <summary>
/// Receives the durable account of business and security facts that the application layer decides are
/// worth keeping.
/// </summary>
/// <remarks>
/// <para>
/// <b>What an implementation must guarantee.</b> Two things, and they are absolute.
/// </para>
/// <para>
/// First, <see cref="Record"/> MUST NOT THROW, for any input, ever. An audit record is a by-product of an
/// operation that has already succeeded; a sink that threw would turn a completed sign-in or a committed
/// portal creation into a server fault, losing the operation as well as the record. An implementation
/// therefore contains its own failures and, where it can, reports them through its own channel.
/// </para>
/// <para>
/// Second, it must not perform work whose failure or latency the caller has to reason about - no
/// database write, no network call, no file handle opened per event. The shipped implementation writes to
/// the host's structured logging pipeline, which buffers.
/// </para>
/// <para>
/// <b>Deliberately synchronous, and this is not an oversight against Rule T6.</b> Rule T6 requires every
/// I/O-BOUND method to return a task; recording an event is not I/O-bound, because the guarantee above
/// forbids an implementation from doing I/O the caller must await. Returning a task would oblige every
/// call site to await something that never blocks, and - worse - would make it possible to forget the
/// await and silently lose the record, or to have a faulted task surface later inside an unrelated
/// operation. Every other collaborator in this layer that performs no I/O is likewise synchronous.
/// </para>
/// <para>
/// <b>Called after the fact, never as part of a decision.</b> A service records an event once the
/// operation it describes has been committed or conclusively refused. Nothing branches on the result of
/// recording, because there is no result: an audit sink observes, it never adjudicates. This is also why
/// a call to it is never inside a transaction that might still roll back - an event describing a write
/// that was subsequently abandoned would be worse than no event at all.
/// </para>
/// <para>
/// <b>Registration.</b> The infrastructure layer registers the implementation. A singleton lifetime is
/// correct: a sink holds no per-request state, and the event carries every fact it needs.
/// </para>
/// </remarks>
public interface IAuditSink
{
    /// <summary>Records one audited fact.</summary>
    /// <param name="auditEvent">The fact to record.</param>
    /// <remarks>
    /// Never throws, including when <paramref name="auditEvent"/> is <see langword="null"/>: a
    /// null-guard that threw would defeat the whole purpose of the guarantee above, so a null is
    /// discarded silently rather than escalated. Callers construct the event inline, so a null cannot
    /// arise from the shipped call sites.
    /// </remarks>
    void Record(AuditEvent auditEvent);
}
