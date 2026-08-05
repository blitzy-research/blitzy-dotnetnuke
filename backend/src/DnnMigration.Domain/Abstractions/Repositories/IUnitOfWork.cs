namespace DnnMigration.Domain.Abstractions.Repositories;

/// <summary>
/// The single atomic commit boundary for the domain model: the one point at which work
/// staged through the sibling repository abstractions is made durable.
/// </summary>
/// <remarks>
/// <para>
/// Repositories stage inserts, updates and deletes; an implementer is obliged to make this
/// the only flush point, because a repository that persisted on its own behalf would split
/// a multi-table write into independently durable statements.
/// </para>
/// <para>
/// That split is the legacy defect this abstraction removes. Portal creation
/// (<c>Library/Components/Portal/PortalController.vb</c>, line 980) writes the
/// <c>Portals</c>, <c>PortalAlias</c>, <c>Roles</c>, <c>Tabs</c> and <c>Modules</c> tables
/// as five independent statement sequences and can raise after the portal row and its
/// administrator are already durable, leaving a portal with no alias, roles, pages or
/// modules and no means of recovery. The legacy data provider did declare transaction
/// members (<c>Library/Components/Providers/Data/DataProvider.vb</c>, lines 70 to 74),
/// but <c>PortalController</c> never invoked them.
/// </para>
/// <para>
/// No persistence session, transaction handle, query surface or change-tracking mechanic is
/// exposed, so a consumer cannot name - and therefore cannot depend upon - the technology
/// that stores the batch. An implementation is expected to be registered with a per-request
/// lifetime and disposed by the container: no release member is declared, and a consumer
/// must not attempt to manage its lifetime.
/// </para>
/// </remarks>
public interface IUnitOfWork
{
    /// <summary>
    /// Reports whether an explicit transaction opened through
    /// <see cref="BeginTransactionAsync(TransactionIsolation, CancellationToken)"/> is currently open on
    /// this unit of work.
    /// </summary>
    /// <remarks>
    /// <para>
    /// WHY A CALLEE NEEDS TO ASK. A service member that is atomic on its own AND is also one step of a
    /// larger atomic sequence has to know which of the two it is serving, because opening a second
    /// transaction is refused rather than nested (see
    /// <see cref="BeginTransactionAsync(TransactionIsolation, CancellationToken)"/>) and committing its own
    /// would make its half durable while the caller's remaining steps could still fail. Reading this
    /// property is how such a member stages its work and defers the commit to whoever owns the
    /// transaction; when nothing is open it opens and commits one of its own, so its standalone contract
    /// is unchanged.
    /// </para>
    /// <para>
    /// The value is a property of the unit of work rather than of any transaction handle, deliberately: the
    /// caller that owns the transaction holds the scope, and a callee must be able to answer the question
    /// without being handed - and therefore without being able to commit - the scope itself.
    /// </para>
    /// </remarks>
    bool HasActiveTransaction { get; }

    /// <summary>
    /// Atomically persists every change staged since the previous commit.
    /// </summary>
    /// <remarks>
    /// An implementer must apply the whole batch or none of it, so the multi-table sequence
    /// described on <see cref="IUnitOfWork"/> commits indivisibly. Server-generated keys
    /// become readable only after this call returns: an add operation stages a row and yields
    /// nothing because the store has not yet assigned an identifier, and the assigned key is
    /// then observed on the entity rather than through a return value, which is what lets
    /// several inserts across several tables share one commit.
    /// </remarks>
    /// <param name="cancellationToken">
    /// Abandons the operation. When cancellation is observed before the batch is committed, no
    /// change is applied.
    /// </param>
    /// <returns>
    /// A task whose result is the number of state entries written to the underlying store. Zero
    /// indicates that nothing was staged.
    /// </returns>
    // MIGRATION: the legacy provider returned a generated key directly from each of its 28 Add
    // members; key visibility is now deferred until this commit completes and is observed on
    // the entity.
    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Opens an explicit transaction spanning several commits, so that a sequence which cannot be
    /// expressed as one <see cref="SaveChangesAsync"/> is still all-or-nothing.
    /// </summary>
    /// <param name="isolation">
    /// The isolation the sequence requires. <see cref="TransactionIsolation.Default"/> leaves the
    /// store's own default in place; <see cref="TransactionIsolation.Serializable"/> is for a
    /// check-then-write whose correctness depends on no other caller changing the thing checked.
    /// </param>
    /// <param name="cancellationToken">Abandons the operation before the transaction is opened.</param>
    /// <returns>
    /// A scope that must be disposed. Disposing WITHOUT having committed rolls the transaction back,
    /// which is what makes a failure path safe by default rather than by remembering to write one.
    /// </returns>
    /// <remarks>
    /// <para>
    /// WHY THIS EXISTS, GIVEN THAT ONE SAVE IS ALREADY ATOMIC. Two sequences in this application
    /// genuinely cannot be one save, and both were previously unsafe for that reason.
    /// </para>
    /// <para>
    /// The first is tenant creation. Three columns on the tenant row hold identifiers the store only
    /// assigns while the first commit runs - the administrator's account key and two role keys - and
    /// the tenant's credential lives in an external membership store that no entity maps. So the
    /// sequence is necessarily commit, then write, then commit. Without an enclosing transaction the
    /// first commit is durable on its own, and an in-process compensation routine is the only thing
    /// standing between a failure and a half-built tenant - a routine that cannot run if the process
    /// is terminated, and that was written not to run on cancellation either.
    /// </para>
    /// <para>
    /// The second is the last-tenant guard on deletion. It counts the tenants, judges the count, and
    /// deletes in a later statement. Two callers deleting the two remaining tenants concurrently can
    /// both observe a count of two and both proceed, leaving an installation with no tenant at all -
    /// which is unreachable. A serializable transaction around the count and the delete is what makes
    /// the second caller wait and then observe a count of one.
    /// </para>
    /// <para>
    /// NO TECHNOLOGY IS NAMED. The scope exposes a commit and a disposal and nothing else: no
    /// connection, no savepoint, no isolation enumeration of the provider's own, and no query surface.
    /// A consumer therefore cannot reach the store through it, and Rule T3 still holds. An
    /// implementation is expected to refuse a second, nested scope rather than silently ignoring it,
    /// because a caller that believes it has opened a transaction and has not is worse off than one
    /// that fails.
    /// </para>
    /// </remarks>
    Task<ITransactionScope> BeginTransactionAsync(
        TransactionIsolation isolation = TransactionIsolation.Default,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Joins the transaction already open on this unit of work, or opens one when the caller is the outermost
    /// operation.
    /// </summary>
    /// <param name="isolation">
    /// Isolation to use only when a new transaction is required. A joined scope cannot change the isolation
    /// selected by its owner.
    /// </param>
    /// <param name="cancellationToken">Abandons opening a new transaction.</param>
    /// <returns>
    /// A scope whose commit and disposal are no-ops when it joined an existing transaction, and which owns a
    /// real transaction otherwise.
    /// </returns>
    /// <remarks>
    /// This is the composition-safe counterpart to <see cref="BeginTransactionAsync"/>. An application service
    /// may call another service that independently needs atomic multi-statement behavior; joining preserves
    /// one outer all-or-nothing boundary instead of either throwing on nesting or committing the inner work
    /// before the outer operation is complete.
    /// </remarks>
    Task<ITransactionScope> JoinOrBeginTransactionAsync(
        TransactionIsolation isolation = TransactionIsolation.Default,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// An open transaction spanning several commits, which is rolled back unless it is committed.
/// </summary>
/// <remarks>
/// There is deliberately no rollback member. Rollback is what disposal does when no commit has
/// happened, so the failure path is correct without anything being written for it - including the
/// paths a author does not anticipate, such as an exception thrown between two commits. A rollback
/// member would add a second way to say the same thing and a way to forget it.
/// </remarks>
public interface ITransactionScope : IAsyncDisposable
{
    /// <summary>
    /// Makes every commit taken inside this scope durable together.
    /// </summary>
    /// <param name="cancellationToken">
    /// Abandons the operation. A cancellation observed here leaves the transaction uncommitted, so
    /// disposal rolls it back.
    /// </param>
    /// <returns>A task that completes once the transaction has been committed.</returns>
    Task CommitAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// The isolation a multi-commit sequence requires, expressed without naming a provider's own type.
/// </summary>
/// <remarks>
/// Only the two levels this application actually needs are published. A full isolation enumeration
/// would invite a caller to pick a level for reasons it cannot justify, and every level beyond these
/// two would be dead - which is exactly the kind of surface that later reads as permission.
/// </remarks>
public enum TransactionIsolation
{
    /// <summary>
    /// The store's configured default. Correct for a sequence that is atomic-or-nothing but does not
    /// depend on anything it read staying unchanged.
    /// </summary>
    Default = 0,

    /// <summary>
    /// Fully serialisable. Required by a check-then-write whose decision would be wrong if another
    /// caller changed the thing checked between the two - the last-tenant guard being the case in
    /// this application.
    /// </summary>
    Serializable = 1,
}
