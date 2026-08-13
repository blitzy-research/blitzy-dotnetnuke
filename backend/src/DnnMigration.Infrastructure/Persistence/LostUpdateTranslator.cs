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
/// WHY THESE TWO NUMBERS AND NOTHING ELSE. Every whole-record write path performs its read, its token
/// comparison and its flush inside one serialisable scope, precisely so that two callers cannot both pass
/// the comparison. Having made the section serialisable, the store reports the loser in one of exactly two
/// ways, and both mean the same thing to a caller: <b>1205</b>, chosen as a deadlock victim after two
/// callers each converted a shared lock in order to write, and <b>3960</b>, a snapshot-isolation update
/// conflict where the row changed after this transaction's snapshot. Each says the caller lost a
/// read-modify-write race, and each carries the same remedy - re-read and re-apply.
/// </para>
/// </remarks>
internal static class LostUpdateTranslator
{
    /// <summary>Error number the engine raises when it aborts a transaction to break a deadlock.</summary>
    private const int DeadlockVictim = 1205;

    /// <summary>Error number the engine raises for a snapshot-isolation update conflict.</summary>
    private const int SnapshotUpdateConflict = 3960;

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

            if (sqlException.Number is DeadlockVictim or SnapshotUpdateConflict)
            {
                return true;
            }

            foreach (SqlError error in sqlException.Errors)
            {
                if (error.Number is DeadlockVictim or SnapshotUpdateConflict)
                {
                    return true;
                }
            }
        }

        return false;
    }
}
