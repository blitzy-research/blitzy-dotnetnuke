using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;

namespace DnnMigration.Infrastructure.Persistence;

/// <summary>
/// Makes an aggregate read INSIDE an explicit transaction describe the store's current row, so that a caller
/// which opened a transaction in order to decide a write decides on the row that transaction has locked.
/// </summary>
/// <remarks>
/// <para>
/// ⚠ WHAT THIS EXISTS TO PREVENT, WHICH IS A SILENT DATA LOSS AND NOT AN INEFFICIENCY. Two of this
/// application's read shapes answer without taking their values from the store, and each is correct in
/// itself:
/// </para>
/// <list type="bullet">
///   <item>
///     <description>
///     A key read through <c>FindAsync</c> consults the change tracker FIRST and issues no statement at all
///     when the row is already held. Every authenticated request resolves its own tenant portal before
///     reaching a service, so a write path addressing that same portal is answered entirely from memory.
///     </description>
///   </item>
///   <item>
///     <description>
///     A query for a row that is already held DOES issue its statement, but identity resolution hands back
///     the instance already in hand and keeps ITS values: the statement's values are discarded. The lock is
///     taken; the values are stale.
///     </description>
///   </item>
/// </list>
/// <para>
/// Either shape defeats an optimistic-concurrency comparison made inside a transaction. In the first the
/// transaction locks nothing, because nothing is read inside it; in the second the transaction locks the row
/// but the comparison judges values from before the lock. Measured on the tenant-portal amendment: two
/// simultaneous callers holding one token both compared against the same pre-transaction values, both passed,
/// and both wrote - two <c>200</c> responses, one amendment lost, and no record anywhere that it had existed.
/// </para>
/// <para>
/// ⚠ THE RULE IS APPLIED IN THE READ RATHER THAN AT THE CALL SITES, AND THAT IS DELIBERATE. The alternative
/// considered first was a second, for-update read on each repository interface, chosen by each write path.
/// It was rejected: it widens three Domain abstractions, it obliges every present and future write path to
/// remember which of two nearly identical reads is the safe one, and forgetting is silent - the code compiles,
/// the tests pass, and the loss only appears under real concurrency. Holding an explicit transaction is
/// already an unambiguous statement of intent: nothing in this application opens one to read. So the
/// transaction itself is the signal, and it cannot be forgotten.
/// </para>
/// <para>
/// TWO CONDITIONS BOUND IT, and both are necessary rather than cautious:
/// </para>
/// <list type="number">
///   <item>
///     <description>
///     An explicit transaction must be open. Outside one, the tracker shortcut is a genuine saving on the
///     hot read path - a measured second read of the tenant portal within one request - and nothing is
///     deciding a write on the answer.
///     </description>
///   </item>
///   <item>
///     <description>
///     The entry must be <see cref="EntityState.Unchanged"/>. An <c>Added</c> entry has no row to read, and a
///     <c>Modified</c> or <c>Deleted</c> one carries values the caller staged on purpose - a composed
///     operation that reads, mutates, and reads again would otherwise have its own staged work silently
///     discarded, turning a concurrency fix into a different data loss.
///     </description>
///   </item>
/// </list>
/// <para>
/// COST, STATED PLAINLY: one extra round trip per aggregate read inside a transaction, which is to say on
/// write paths only, and only where the aggregate was already in hand. It buys the transaction the read it
/// was opened to protect.
/// </para>
/// </remarks>
internal static class TransactionalRead
{
    /// <summary>
    /// Refreshes one aggregate from the store when the caller is inside an explicit transaction, reporting
    /// whether the row still exists.
    /// </summary>
    /// <typeparam name="TEntity">The aggregate's type.</typeparam>
    /// <param name="dbContext">The context tracking the aggregate.</param>
    /// <param name="entity">The aggregate that was read, or <see langword="null"/>.</param>
    /// <param name="cancellationToken">Abandons the operation.</param>
    /// <returns>
    /// The aggregate, refreshed when the conditions above hold; or <see langword="null"/> when the store no
    /// longer holds its row, or when <paramref name="entity"/> was already <see langword="null"/>.
    /// </returns>
    /// <remarks>
    /// A reload of a row that no longer exists detaches the instance rather than throwing, so the entry's
    /// state AFTER the reload is the only reliable way to tell "refreshed" from "gone". Reporting a removed
    /// row as absent is what stops a caller amending values the store no longer holds - which is the same
    /// answer the read would have given had the row been removed before it ran.
    /// </remarks>
    internal static async Task<TEntity?> InTransactionAsync<TEntity>(
        DbContext dbContext,
        TEntity? entity,
        CancellationToken cancellationToken = default)
        where TEntity : class
    {
        if (entity is null || dbContext.Database.CurrentTransaction is null)
        {
            return entity;
        }

        EntityEntry<TEntity> entry = dbContext.Entry(entity);

        if (entry.State != EntityState.Unchanged)
        {
            return entity;
        }

        await entry.ReloadAsync(cancellationToken).ConfigureAwait(false);

        return entry.State == EntityState.Detached ? null : entity;
    }
}
