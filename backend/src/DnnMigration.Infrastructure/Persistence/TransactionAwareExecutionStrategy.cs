using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace DnnMigration.Infrastructure.Persistence;

/// <summary>
/// Retries a transient database failure exactly as the SQL Server provider's own strategy does, except
/// while the unit of work is holding a transaction it opened itself, when retrying is suspended.
/// </summary>
/// <remarks>
/// <para>
/// WHY THIS TYPE EXISTS. Two properties are both required and are in direct conflict as the provider ships
/// them. Transient-fault resilience is required because a managed SQL Server closes connections during a
/// failover or a throttling episode, and the request that happened to hold one fails for a reason unrelated
/// to anything the caller did.
/// </para>
/// <para>
/// Wrapping the transactional unit in the strategy's own <c>ExecuteAsync</c> is what the exception message
/// suggests, and it is unsafe here. A retry re-invokes the delegate, but a rolled-back transaction does NOT
/// reset the change tracker: entities the first attempt saved remain tracked as unchanged, holding
/// store-assigned keys for rows that no longer exist.
/// </para>
/// <para>
/// ⚠ SUSPENSION TAKES BOTH MEMBERS BELOW, AND OVERRIDING ONLY THE FIRST LEFT THE DEFECT IN PLACE. The two
/// are read by different parts of the base class and only one of them governs the retry loop:
/// </para>
/// <list type="bullet">
///   <item>
///     <description>
///     <see cref="RetriesOnFailure"/> is consulted once, by the base class's first-execution check, purely to
///     decide whether to REFUSE an operation that already holds a user transaction. Answering
///     <see langword="false"/> there is what lets the unit of work own its transaction at all.
///     </description>
///   </item>
///   <item>
///     <description>
///     <see cref="ShouldRetryOn"/> is what the retry LOOP asks after each failure. It is reached whether or
///     not <see cref="RetriesOnFailure"/> answered <see langword="false"/>, so leaving it to the base class
///     meant a failure inside a unit-of-work transaction was still retried.
///     </description>
///   </item>
/// </list>
/// <para>
/// MEASURED CONSEQUENCE OF THE GAP, which is the reason the second override exists. Two callers each read a
/// record and wrote it inside their own serialisable scope. The engine broke the resulting lock conversion by
/// choosing one as a deadlock victim - error 1205 - and, as it always does, rolled that participant's WHOLE
/// transaction back server-side. The base class classifies 1205 as transient, so the loop re-invoked
/// <c>SaveChanges</c>; the provider's transaction OBJECT was still in hand, so the flush opened by issuing a
/// savepoint, and a savepoint against a transaction the server had already discarded fails with error 628,
/// "Cannot issue SAVE TRANSACTION when there is no active transaction". 628 is not transient and describes
/// nothing the caller did, so it escaped every translation the unit of work performs and the loser of an
/// ordinary write race was answered 500 instead of 409.
/// </para>
/// <para>
/// Suspending the loop while the flag is set makes the engine's own report - 1205, or a snapshot conflict -
/// the exception the caller sees, which <c>LostUpdateTranslator</c> recognises and the unit of work turns
/// into <c>ConcurrencyConflictException</c>. Nothing is lost by not retrying: the transaction is already gone,
/// so there is nothing left to retry INTO, and the operation the caller must repeat is the whole read-modify
/// -write, which only the caller can re-issue.
/// </para>
/// </remarks>
internal sealed class TransactionAwareExecutionStrategy : SqlServerRetryingExecutionStrategy
{
    /// <summary>Initialises a new instance of the <see cref="TransactionAwareExecutionStrategy"/> class.</summary>
    /// <param name="dependencies">The provider-supplied dependencies, passed through unchanged.</param>
    public TransactionAwareExecutionStrategy(ExecutionStrategyDependencies dependencies)
        : base(dependencies)
    {
    }

    /// <summary>Gets a value indicating whether this execution might be retried after a failure.</summary>
    public override bool RetriesOnFailure => !HoldsUnitOfWorkTransaction && base.RetriesOnFailure;

    /// <summary>
    /// Whether the context this strategy is running for has a transaction the unit of work opened, and is
    /// therefore mid-way through an operation whose atomicity this type must not break.
    /// </summary>
    /// <remarks>
    /// Read on each access rather than captured in the constructor. A strategy instance outlives the
    /// individual flushes it serves, and a scope may be opened after it was created, so a captured answer
    /// could describe a state that no longer holds.
    /// </remarks>
    private bool HoldsUnitOfWorkTransaction =>
        Dependencies.CurrentContext.Context is DnnDbContext { ExplicitTransactionOpen: true };

    /// <summary>Decides whether a failure is one this strategy will retry.</summary>
    /// <param name="exception">The failure the operation raised.</param>
    /// <returns>
    /// <see langword="false"/> whenever the unit of work is holding its own transaction, so the failure
    /// reaches the caller unaltered; otherwise the provider's own transient-fault classification.
    /// </returns>
    protected override bool ShouldRetryOn(Exception exception) =>
        !HoldsUnitOfWorkTransaction && base.ShouldRetryOn(exception);
}
