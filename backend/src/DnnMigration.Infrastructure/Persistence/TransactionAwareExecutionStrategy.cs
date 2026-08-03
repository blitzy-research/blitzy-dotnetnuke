using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace DnnMigration.Infrastructure.Persistence;

/// <summary>
/// Retries a transient database failure exactly as the SQL Server provider's own strategy does, except while
/// the unit of work is holding a transaction it opened itself, when retrying is suspended.
/// </summary>
/// <remarks>
/// <para>
/// WHY THIS TYPE EXISTS. Two properties are both required and are in direct conflict as the provider ships
/// them. Transient-fault resilience is required because a managed SQL Server closes connections during a
/// failover or a throttling episode, and the request that happened to hold one fails for a reason unrelated
/// to anything the caller did. Atomicity across more than one commit is required because creating and
/// removing a tenant each span several writes - a tenant graph, an external credential record, and a second
/// commit that stamps identifiers the store only issues during the first - and a partial outcome leaves a
/// portal reachable at its host name whose administrator cannot sign in. The provider's retrying strategy
/// REFUSES to run inside a caller-opened transaction: <c>SaveChangesAsync</c> throws "the configured
/// execution strategy does not support user-initiated transactions".
/// </para>
/// <para>
/// WHY THE OBVIOUS ALTERNATIVES WERE REJECTED, each after being tried rather than reasoned about:
/// </para>
/// <para>
/// Wrapping the transactional unit in the strategy's own <c>ExecuteAsync</c> is what the exception message
/// suggests, and it is unsafe here. A retry re-invokes the delegate, but a rolled-back transaction does NOT
/// reset the change tracker: entities the first attempt saved remain tracked as unchanged, holding
/// store-assigned keys for rows that no longer exist. A replay would issue updates against missing rows or
/// insert duplicates - a data-integrity fault substituted for a transient one, which is strictly worse than
/// the failure it absorbs.
/// </para>
/// <para>
/// Choosing between two strategies in the registered factory, by asking whether a transaction is open at the
/// moment the strategy is CREATED, does not work either, and fails in two distinct ways that were both
/// observed. Asking the database facade from inside the factory HANGS: reading
/// <c>Database.CurrentTransaction</c> resolves the facade's dependency bundle, and the bundle contains the
/// execution strategy factory, so the factory asks a question whose answer needs the factory. Asking a plain
/// flag instead removes the hang but still gives the wrong answer, because the strategy is resolved ONCE PER
/// CONTEXT SCOPE - the state manager's dependencies hold a single instance - and it is created the first time
/// anything in the request touches the state manager, which is long before the transaction opens. The
/// decision has to be made when the strategy RUNS, not when it is built.
/// </para>
/// <para>
/// WHAT THIS DOES INSTEAD. It defers exactly one decision. The single cached instance keeps the provider's
/// own retry policy, retry count and back-off, and re-evaluates whether retrying applies on every execution.
/// Outside a transaction it behaves identically to the strategy it derives from. Inside one it reports that
/// it does not retry, which both silences the refusal and states the truth: the operation is not being
/// retried, and the resilience is supplied by the transaction reversing the whole unit so the caller can
/// retry the operation rather than the framework replaying half of it.
/// </para>
/// </remarks>
internal sealed class TransactionAwareExecutionStrategy : SqlServerRetryingExecutionStrategy
{
    /// <summary>
    /// Initialises a new instance of the <see cref="TransactionAwareExecutionStrategy"/> class.
    /// </summary>
    /// <param name="dependencies">The provider-supplied dependencies, passed through unchanged.</param>
    /// <remarks>
    /// No retry count or delay is supplied, so the provider's documented defaults apply. Inventing values
    /// here would silently override whatever the registration asked for.
    /// </remarks>
    public TransactionAwareExecutionStrategy(ExecutionStrategyDependencies dependencies)
        : base(dependencies)
    {
    }

    /// <summary>
    /// Gets a value indicating whether this execution might be retried after a failure.
    /// </summary>
    /// <remarks>
    /// Read on every execution, which is the whole point of the type. The base implementation's answer is
    /// preserved and merely narrowed - notably its own suspension, which is how the framework stops a nested
    /// strategy from retrying inside an outer one - so this never enables a retry the provider would have
    /// declined. Reading the flag resolves nothing and touches no facade, so it cannot re-enter the service
    /// provider the way an earlier attempt to consult <c>Database.CurrentTransaction</c> did.
    /// </remarks>
    public override bool RetriesOnFailure =>
        Dependencies.CurrentContext.Context is not DnnDbContext { ExplicitTransactionOpen: true }
        && base.RetriesOnFailure;
}
