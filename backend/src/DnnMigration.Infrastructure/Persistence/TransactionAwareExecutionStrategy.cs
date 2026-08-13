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
    public override bool RetriesOnFailure =>
        Dependencies.CurrentContext.Context is not DnnDbContext { ExplicitTransactionOpen: true }
        && base.RetriesOnFailure;
}
