using Microsoft.Data.SqlClient;

namespace DnnMigration.Infrastructure.Persistence;

/// <summary>
/// Recognises a store refusal that means "the record you were editing moved underneath you", as opposed to a
/// refusal of the values themselves or a fault of the store.
/// </summary>
/// <remarks>
/// <para>
/// Separated from <see cref="UnitOfWork"/> for the same reason <see cref="DuplicateKeyTranslator"/> is: it is
/// the one piece of this translation that depends on provider error numbers, and a test should be able to
/// reach it without having to provoke a real race between two live transactions.
/// </para>
/// <para>
/// WHY THESE TWO NUMBERS AND NOTHING ELSE. Every whole-record write path performs its read, its
/// token comparison and its flush inside one serialisable scope, precisely so that two callers cannot both
/// pass the comparison. Having made the section serialisable, the store reports the loser in one of exactly
/// two ways, and both mean the same thing to a caller:
/// </para>
/// <list type="bullet">
/// <item>
/// <description>
/// <b>1205</b> - the transaction was chosen as a deadlock victim. Two callers that both read the row under
/// serialisable isolation each hold a shared lock on it, and each then asks to convert that lock in order to
/// write; the engine resolves the impasse by aborting one. The aborted caller has lost a read-modify-write
/// race, which is a lost update however the engine phrased it, and its remedy is to re-read and re-apply.
/// </description>
/// </item>
/// <item>
/// <description>
/// <b>3960</b> - a snapshot-isolation update conflict, raised where a deployment has enabled
/// read-committed-snapshot or snapshot isolation and the row changed after this transaction's snapshot was
/// taken. It is the same condition reported by a different concurrency-control mechanism, so it must not be
/// classified differently just because an installation configured its isolation differently.
/// </description>
/// </item>
/// </list>
/// <para>
/// NOTHING ELSE QUALIFIES, and the omissions are deliberate. A lock-request timeout (1222) is a saturation
/// condition rather than a lost update - the row may not have changed at all - and telling a caller its edit
/// was overwritten when it was merely made to wait would be a false report. A unique-value refusal has its
/// own translator, its own signal and its own remedy. Anything the store raises that is neither continues to
/// travel exactly as it did.
/// </para>
/// <para>
/// Only the persistence assembly may name <see cref="SqlException"/>, and this file is one of the two places
/// that does. Everything above sees
/// <see cref="DnnMigration.Domain.Common.ConcurrencyConflictException"/>, which the Domain declares and which
/// names no provider type.
/// </para>
/// </remarks>
internal static class LostUpdateTranslator
{
    /// <summary>Error number the engine raises when it aborts a transaction to break a deadlock.</summary>
    private const int DeadlockVictim = 1205;

    /// <summary>Error number the engine raises for a snapshot-isolation update conflict.</summary>
    private const int SnapshotUpdateConflict = 3960;

    /// <summary>
    /// Tests whether a store failure means the addressed record was changed by another caller.
    /// </summary>
    /// <param name="exception">The failure raised by the flush.</param>
    /// <returns><see langword="true"/> when the failure reports a lost update.</returns>
    /// <remarks>
    /// The WHOLE inner chain is walked rather than only the immediate inner exception, and every error in
    /// each provider exception's collection is examined rather than only the one it surfaces as its own
    /// number. The provider wraps its fault at more than one depth depending on whether an execution
    /// strategy or a transaction scope was in play, and a batch can report several errors of which the
    /// concurrency one need not be first - so a translation that looked one level down, or at one number,
    /// would classify the same race correctly on one code path and report it as a server fault on another.
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
