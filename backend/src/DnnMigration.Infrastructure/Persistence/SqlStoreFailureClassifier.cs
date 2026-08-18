using System.Data.Common;
using System.Net.Sockets;
using DnnMigration.Domain.Abstractions.Services;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace DnnMigration.Infrastructure.Persistence;

/// <summary>
/// Recognises a failure that means the SQL Server backing this API was unreachable or unable to serve, so
/// the transport can answer it as a temporary condition rather than as a defect.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The classification rule is measured, not assumed.</strong> A stopped SQL Server was probed
/// through three paths - a raw connection open, an Entity Framework query with retrying enabled, and one
/// with it disabled - and all three produced the SAME thing: a single <see cref="SqlException"/> with
/// <c>Number = 0</c>, <c>Class = 20</c>, <c>IsTransient</c> <see langword="false"/> and no inner exception,
/// unwrapped by the mapper.
/// </para>
/// <para>
/// <strong>What is deliberately NOT matched.</strong> Every error the store raised while working correctly:
/// a constraint or unique-index violation (which is a caller conflict and has its own translation), an
/// invalid column or object name, a permission refusal, an arithmetic or conversion fault, a deadlock
/// victim.
/// </para>
/// </remarks>
internal sealed class SqlStoreFailureClassifier : IStoreFailureClassifier
{
    /// <summary>
    /// Lowest SQL Server severity class that terminates the connection, and the class the client also
    /// reports for a connection it could not establish at all.
    /// </summary>
    private const byte FatalSeverityClass = 20;

    /// <summary>Error number the client reports when a command exceeded its timeout.</summary>
    /// <remarks>
    /// Carried with a class below the fatal boundary, so it needs naming explicitly. A timeout is the one
    /// availability condition that does not close the connection: the store was reachable and did not
    /// answer in time, which is a load or a lock-contention condition and is exactly the case a retry hint
    /// is for.
    /// </remarks>
    private const int CommandTimeoutErrorNumber = -2;

    /// <summary>
    /// Error number the client reports when a command was CANCELLED rather than allowed to finish.
    /// </summary>
    /// <remarks>
    /// Zero is the client's own number for a condition it raised itself rather than one the server sent, so
    /// it is deliberately not sufficient on its own - it is paired below with the requirement that no server
    /// error accompany it. A cancelled command carries no <see cref="SqlError"/> from the server, because the
    /// server never got to report anything.
    /// </remarks>
    private const int ClientRaisedErrorNumber = 0;

    /// <summary>
    /// Error number the server reports when the batch it was running was aborted.
    /// </summary>
    /// <remarks>
    /// Raised when a statement is torn down mid-flight - which is what cancelling an in-progress command
    /// does. It is named explicitly because the number carries a severity BELOW the fatal boundary, so the
    /// availability test above does not see it and it would otherwise fall through to the general arm and be
    /// reported as a defect in this application.
    /// </remarks>
    private const int BatchAbortedErrorNumber = 3980;

    /// <inheritdoc />
    /// <remarks>
    /// The whole chain is walked, because the fault that matters is routinely not the outermost one: the
    /// mapper wraps a provider failure during a flush, an exhausted retry strategy wraps the last attempt's
    /// failure, and an application-level failure may carry either as its cause. A test that looked only at
    /// the surface would classify the same outage correctly on a read and incorrectly on a write.
    /// </remarks>
    public bool IsStoreUnavailable(Exception? exception) => Walk(exception, inStoreContext: false, depth: 0);

    /// <inheritdoc />
    /// <remarks>
    /// The whole chain is walked for the same reason the availability test walks it: the cancellation that
    /// matters is routinely not the outermost link. The mapper wraps whatever the client handed it, and the
    /// client may hand it either a cancellation or its own exception type describing one.
    /// </remarks>
    public bool IsCallerCancellation(Exception? exception) =>
        WalkForCancellation(exception, depth: 0);

    /// <summary>Walks one exception chain looking for evidence that the work was abandoned.</summary>
    /// <param name="candidate">The link to examine, or <see langword="null"/> at the end of a chain.</param>
    /// <param name="depth">How many links have been followed, bounded exactly as the sibling walk is.</param>
    /// <returns><see langword="true"/> when the chain establishes a cancellation.</returns>
    private static bool WalkForCancellation(Exception? candidate, int depth)
    {
        const int maximumDepth = 32;

        while (candidate is not null && depth < maximumDepth)
        {
            if (candidate is AggregateException aggregate)
            {
                foreach (Exception inner in aggregate.Flatten().InnerExceptions)
                {
                    if (WalkForCancellation(inner, depth + 1))
                    {
                        return true;
                    }
                }

                return false;
            }

            // Covers TaskCanceledException too, which derives from it. This is the ordinary shape and is
            // tested first.
            if (candidate is OperationCanceledException)
            {
                return true;
            }

            if (DescribesCancellation(candidate))
            {
                return true;
            }

            candidate = candidate.InnerException;
            depth++;
        }

        return false;
    }

    /// <summary>Tests one link for a provider-reported cancellation, without following it further.</summary>
    /// <param name="candidate">The link to test.</param>
    /// <returns><see langword="true"/> when this link describes abandoned rather than failed work.</returns>
    /// <remarks>
    /// ⚠ A TIMEOUT MUST NOT MATCH HERE, and the ordering below is what keeps it out. The client reports a
    /// command timeout with its own number as well, so the timeout number is excluded explicitly before the
    /// client-raised arm is considered: the caller of a timed-out command is still waiting and is owed the
    /// availability answer, not silence.
    /// </remarks>
    private static bool DescribesCancellation(Exception candidate)
    {
        if (candidate is not SqlException sqlException)
        {
            return false;
        }

        if (sqlException.Number == CommandTimeoutErrorNumber)
        {
            return false;
        }

        foreach (SqlError error in sqlException.Errors)
        {
            if (error.Number == CommandTimeoutErrorNumber)
            {
                return false;
            }

            if (error.Number == BatchAbortedErrorNumber)
            {
                return true;
            }
        }

        if (sqlException.Number == BatchAbortedErrorNumber)
        {
            return true;
        }

        // THE CLIENT RAISED THIS ITSELF AND NO SERVER ERROR ACCOMPANIES IT, which is the shape a cancelled
        // command leaves behind: the statement was torn down before the server could answer, so the only error
        // present is the client's own.
        //
        // ⚠ "NO SERVER ERROR" MEANS EVERY ERROR IS CLIENT-RAISED, NOT THAT THE COLLECTION IS EMPTY. This
        // condition was first written as Errors.Count == 0, which never matches: the client always attaches at
        // least one error, and for a cancelled command it attaches exactly one carrying number 0. An empty
        // collection is not the real shape, so testing for it made this arm unreachable.
        //
        // A transient flag or a fatal severity class would mean a connection condition instead, so both are
        // excluded - that is the availability test's territory and it must keep it.
        if (sqlException.Number != ClientRaisedErrorNumber
            || sqlException.IsTransient
            || sqlException.Class >= FatalSeverityClass)
        {
            return false;
        }

        foreach (SqlError error in sqlException.Errors)
        {
            if (error.Number != ClientRaisedErrorNumber)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Walks one exception chain, carrying whether a data-access frame has been seen above the current
    /// link.
    /// </summary>
    /// <param name="candidate">The link to examine, or <see langword="null"/> at the end of a chain.</param>
    /// <param name="inStoreContext">
    /// Whether a frame identifying this chain as a data-access failure has already been seen.
    /// </param>
    /// <param name="depth">How many links have been followed, for the bound described on the interface.</param>
    /// <returns><see langword="true"/> when the chain establishes an availability failure of the store.</returns>
    private static bool Walk(Exception? candidate, bool inStoreContext, int depth)
    {
        const int maximumDepth = 32;

        while (candidate is not null && depth < maximumDepth)
        {
            if (candidate is AggregateException aggregate)
            {
                foreach (Exception inner in aggregate.Flatten().InnerExceptions)
                {
                    if (Walk(inner, inStoreContext, depth + 1))
                    {
                        return true;
                    }
                }

                return false;
            }

            if (DescribesUnavailability(candidate, inStoreContext))
            {
                return true;
            }

            inStoreContext = inStoreContext || IdentifiesADataAccessFailure(candidate);

            candidate = candidate.InnerException;
            depth++;
        }

        return false;
    }

    /// <summary>
    /// Reports whether one link identifies its chain as a data-access failure, without itself being an
    /// availability condition.
    /// </summary>
    /// <param name="candidate">The link to test.</param>
    /// <returns><see langword="true"/> when the link comes from the database client or the mapper.</returns>
    /// <remarks>
    /// NONE OF THESE IS TREATED AS AN OUTAGE BY ITSELF, and that distinction is the whole point of a
    /// separate method. A failed flush is not an availability condition by virtue of having failed - it is
    /// just as likely to be a constraint violation, which has its own translation and its own status - so
    /// these types only ADMIT the generic arms below for the chain they identify.
    /// </remarks>
    private static bool IdentifiesADataAccessFailure(Exception candidate) =>
        candidate is DbException or DbUpdateException or RetryLimitExceededException;

    /// <summary>Tests one link of an exception chain, without following it further.</summary>
    /// <param name="candidate">The link to test.</param>
    /// <param name="inStoreContext">Whether a data-access frame has been seen at or above this link.</param>
    /// <returns><see langword="true"/> when this link establishes an availability failure.</returns>
    /// <remarks>
    /// <see cref="SqlException"/> is examined by severity, by the client's own transient flag and by the
    /// command-timeout number, and EVERY error in its collection is examined rather than only the one the
    /// exception surfaces as its own: a batch can report several, and the connection-level one need not be
    /// first.
    /// </remarks>
    private static bool DescribesUnavailability(Exception candidate, bool inStoreContext)
    {
        switch (candidate)
        {
            case SqlException sqlException:
                if (sqlException.IsTransient
                    || sqlException.Class >= FatalSeverityClass
                    || sqlException.Number == CommandTimeoutErrorNumber)
                {
                    return true;
                }

                foreach (SqlError error in sqlException.Errors)
                {
                    if (error.Class >= FatalSeverityClass || error.Number == CommandTimeoutErrorNumber)
                    {
                        return true;
                    }
                }

                return false;

            case SocketException:
            case TimeoutException:
                return inStoreContext;

            default:
                return false;
        }
    }
}
