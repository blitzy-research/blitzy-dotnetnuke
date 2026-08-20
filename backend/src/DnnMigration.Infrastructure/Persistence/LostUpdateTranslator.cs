using Microsoft.Data.SqlClient;

namespace DnnMigration.Infrastructure.Persistence;

/// <summary>
/// Recognises a store refusal that means "the record you were editing moved underneath you", as opposed to
/// a refusal of the values themselves or a fault of the store.
/// </summary>
/// <remarks>
/// <para>
/// Separated from <see cref="UnitOfWork"/> for the same reason <see cref="DuplicateKeyTranslator"/> is: it
/// is the one piece of this translation that depends on provider error numbers, and a test should be able
/// to reach it without having to provoke a real race between two live transactions.
/// </para>
/// <para>
/// WHY THESE NUMBERS AND NOTHING ELSE. Every whole-record write path performs its read, its token
/// comparison and its flush inside one serialisable scope, precisely so that two callers cannot both pass
/// the comparison. Having made the section serialisable, the store reports the loser in one of exactly two
/// ways, and both mean the same thing to a caller: <b>1205</b>, chosen as a deadlock victim after two
/// callers each converted a shared lock in order to write, and <b>3960</b>, a snapshot-isolation update
/// conflict where the row changed after this transaction's snapshot. Each says the caller lost a
/// read-modify-write race, and each carries the same remedy - re-read and re-apply.
/// </para>
/// <para>
/// ⚠ TWO FURTHER NUMBERS ARE RECOGNISED AS A SECOND LINE OF DEFENCE, NOT AS A DISTINCT CONDITION, and they
/// are here because their absence was measured as a 500. <b>628</b> and <b>3903</b> both say the same thing:
/// a statement addressed a transaction the ENGINE has already discarded - a savepoint could not be created,
/// or a rollback found no transaction to roll back. Inside these write paths there is exactly one way for
/// that state to arise, and it is a lost update: the engine aborted this participant's transaction to break a
/// race and something afterwards spoke to the transaction anyway. The primary remedy for that is
/// <c>TransactionAwareExecutionStrategy</c>, which stops the retry that used to be the "something
/// afterwards"; this recognition is what makes the remaining exposure a 409 rather than a fault, because a
/// caller who lost a race must never be told the server broke. A genuine programming error that issued a
/// savepoint with no transaction of any kind would be reported the same way, and that trade is accepted
/// deliberately: such a defect fails on every single call and is found at once, whereas mistranslating a race
/// is silent and only ever visible under concurrency.
/// </para>
/// </remarks>
internal static class LostUpdateTranslator
{
    /// <summary>Error number the engine raises when it aborts a transaction to break a deadlock.</summary>
    private const int DeadlockVictim = 1205;

    /// <summary>Error number the engine raises for a snapshot-isolation update conflict.</summary>
    private const int SnapshotUpdateConflict = 3960;

    /// <summary>
    /// Error number the engine raises for a savepoint issued when it holds no active transaction, which in
    /// these write paths means the engine had already aborted this participant to break a race.
    /// </summary>
    private const int SavepointWithoutTransaction = 628;

    /// <summary>
    /// Error number the engine raises for a rollback that finds no matching transaction, the same condition
    /// as <see cref="SavepointWithoutTransaction"/> observed at the other end of the flush.
    /// </summary>
    private const int RollbackWithoutTransaction = 3903;

    /// <summary>Tests whether a store failure means the addressed record was changed by another caller.</summary>
    /// <param name="exception">The failure raised by the flush.</param>
    /// <returns><see langword="true"/> when the failure reports a lost update.</returns>
    /// <remarks>
    /// The WHOLE inner chain is walked rather than only the immediate inner exception, and every error in
    /// each provider exception's collection is examined rather than only the one it surfaces as its own
    /// number.
    /// </remarks>
    public static bool Describes(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        for (Exception? candidate = exception; candidate is not null; candidate = candidate.InnerException)
        {
            if (candidate is not SqlException sqlException)
            {
                continue;
            }

            if (DescribesLostUpdate(sqlException.Number))
            {
                return true;
            }

            foreach (SqlError error in sqlException.Errors)
            {
                if (DescribesLostUpdate(error.Number))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>Whether one engine error number reports that the caller lost a read-modify-write race.</summary>
    /// <param name="number">The engine's error number.</param>
    /// <returns><see langword="true"/> when the number describes a lost update.</returns>
    private static bool DescribesLostUpdate(int number) => number
        is DeadlockVictim
        or SnapshotUpdateConflict
        or SavepointWithoutTransaction
        or RollbackWithoutTransaction;
}
