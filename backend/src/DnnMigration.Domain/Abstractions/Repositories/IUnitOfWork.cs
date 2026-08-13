namespace DnnMigration.Domain.Abstractions.Repositories;

/// <summary>
/// The single atomic commit boundary for the domain model: the one point at which work staged through the
/// sibling repository abstractions is made durable.
/// </summary>
/// <remarks>
/// <para>
/// Repositories stage inserts, updates and deletes; an implementer is obliged to make this the only flush
/// point, because a repository that persisted on its own behalf would split a multi-table write into
/// independently durable statements.
/// </para>
/// <para>
/// No persistence session, transaction handle, query surface or change-tracking mechanic is exposed, so a
/// consumer cannot name - and therefore cannot depend upon - the technology that stores the batch. An
/// implementation is expected to be registered with a per-request lifetime and disposed by the container:
/// no release member is declared, and a consumer must not attempt to manage its lifetime.
/// </para>
/// </remarks>
public interface IUnitOfWork
{
    /// <summary>
    /// Reports whether an explicit transaction opened through <see
    /// cref="BeginTransactionAsync(TransactionIsolation, CancellationToken)"/> is currently open on this
    /// unit of work.
    /// </summary>
    /// <remarks>
    /// WHY A CALLEE NEEDS TO ASK. A service member that is atomic on its own AND is also one step of a
    /// larger atomic sequence has to know which of the two it is serving, because opening a second
    /// transaction is refused rather than nested (see <see
    /// cref="BeginTransactionAsync(TransactionIsolation, CancellationToken)"/>) and committing its own
    /// would make its half durable while the caller's remaining steps could still fail.
    /// </remarks>
    bool HasActiveTransaction { get; }

    /// <summary>Atomically persists every change staged since the previous commit.</summary>
    /// <remarks>
    /// An implementer must apply the whole batch or none of it, so the multi-table sequence described on
    /// <see cref="IUnitOfWork"/> commits indivisibly.
    /// </remarks>
    /// <param name="cancellationToken">Abandons the operation.</param>
    /// <returns>A task whose result is the number of state entries written to the underlying store.</returns>
    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Opens an explicit transaction spanning several commits, so that a sequence which cannot be expressed
    /// as one <see cref="SaveChangesAsync"/> is still all-or-nothing.
    /// </summary>
    /// <param name="isolation">
    /// The isolation the sequence requires. <see cref="TransactionIsolation.Default"/> leaves the store's
    /// own default in place; <see cref="TransactionIsolation.Serializable"/> is for a check-then-write
    /// whose correctness depends on no other caller changing the thing checked.
    /// </param>
    /// <param name="cancellationToken">Abandons the operation before the transaction is opened.</param>
    /// <returns>A scope that must be disposed.</returns>
    /// <remarks>
    /// The first is tenant creation. Three columns on the tenant row hold identifiers the store only
    /// assigns while the first commit runs - the administrator's account key and two role keys - and the
    /// tenant's credential lives in an external membership store that no entity maps.
    /// </remarks>
    Task<ITransactionScope> BeginTransactionAsync(
        TransactionIsolation isolation = TransactionIsolation.Default,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Joins the transaction already open on this unit of work, or opens one when the caller is the
    /// outermost operation.
    /// </summary>
    /// <param name="isolation">Isolation to use only when a new transaction is required.</param>
    /// <param name="cancellationToken">Abandons opening a new transaction.</param>
    /// <returns>
    /// A scope whose commit and disposal are no-ops when it joined an existing transaction, and which owns
    /// a real transaction otherwise.
    /// </returns>
    /// <remarks>
    /// This is the composition-safe counterpart to <see cref="BeginTransactionAsync"/>. An application
    /// service may call another service that independently needs atomic multi-statement behavior; joining
    /// preserves one outer all-or-nothing boundary instead of either throwing on nesting or committing the
    /// inner work before the outer operation is complete.
    /// </remarks>
    Task<ITransactionScope> JoinOrBeginTransactionAsync(
        TransactionIsolation isolation = TransactionIsolation.Default,
        CancellationToken cancellationToken = default);
}

/// <summary>An open transaction spanning several commits, which is rolled back unless it is committed.</summary>
public interface ITransactionScope : IAsyncDisposable
{
    /// <summary>Makes every commit taken inside this scope durable together.</summary>
    /// <param name="cancellationToken">Abandons the operation.</param>
    /// <returns>A task that completes once the transaction has been committed.</returns>
    Task CommitAsync(CancellationToken cancellationToken = default);
}

/// <summary>The isolation a multi-commit sequence requires, expressed without naming a provider's own type.</summary>
/// <remarks>
/// Only the two levels this application actually needs are published. A full isolation enumeration would
/// invite a caller to pick a level for reasons it cannot justify, and every level beyond these two would be
/// dead - which is exactly the kind of surface that later reads as permission.
/// </remarks>
public enum TransactionIsolation
{
    /// <summary>
    /// The store's configured default. Correct for a sequence that is atomic-or-nothing but does not depend
    /// on anything it read staying unchanged.
    /// </summary>
    Default = 0,

    /// <summary>
    /// Fully serialisable. Required by a check-then-write whose decision would be wrong if another caller
    /// changed the thing checked between the two - the last-tenant guard being the case in this
    /// application.
    /// </summary>
    Serializable = 1,
}
