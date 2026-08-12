using System.Net.Sockets;
using DnnMigration.Domain.Abstractions.Services;
using Microsoft.Data.SqlClient;

namespace DnnMigration.Infrastructure.Persistence;

/// <summary>
/// Recognises a failure that means the SQL Server backing this API was unreachable or unable to serve,
/// so the transport can answer it as a temporary condition rather than as a defect.
/// </summary>
/// <remarks>
/// <para>
/// MIGRATION: a database outage used to be answered <c>500 Internal Server Error</c> on every data
/// endpoint. The payload was a correct problem document that disclosed nothing, and the readiness view
/// already reported the outage as <c>503</c> to an orchestrator, so nothing was leaking and nothing was
/// unmonitored - but the status told a caller the server had a defect when the truth was that a
/// dependency was down, which is a different instruction: one says "report this", the other says "retry
/// shortly". Runtime testing measured that shape (database stopped, sign-in answered 500) and it is what
/// this type closes.
/// </para>
/// <para>
/// <strong>This assembly is the only one that may name <see cref="SqlException"/>, and this file and
/// <see cref="DuplicateKeyTranslator"/> are the only two that do.</strong> The API project references
/// neither the object-relational mapper nor the database client, deliberately, so it cannot inspect a
/// provider fault and must not begin to: the answer travels up as
/// <see cref="IStoreFailureClassifier"/>, a Domain contract with no provider type in its signature. This
/// is the same division the duplicate-key translation uses, for the same reason.
/// </para>
/// <para>
/// <strong>The classification rule is measured, not assumed.</strong> A stopped SQL Server was probed
/// through three paths - a raw connection open, an Entity Framework query with retrying enabled, and one
/// with it disabled - and all three produced the SAME thing: a single
/// <see cref="SqlException"/> with <c>Number = 0</c>, <c>Class = 20</c>,
/// <c>IsTransient</c> <see langword="false"/> and no inner exception, unwrapped by
/// the mapper. Two consequences follow. Keying on <c>IsTransient</c> alone would
/// have closed nothing, because the client does not consider an unreachable server transient. And
/// keying on error numbers alone would have closed nothing either, because the number is zero. The
/// load-bearing test is therefore SEVERITY: SQL Server reserves classes 20 and above for errors that
/// terminate the connection, and the client reuses that scale for its own connection-establishment
/// faults. The remaining tests are additions to it rather than the substance of it.
/// </para>
/// <para>
/// <strong>What is deliberately NOT matched.</strong> Every error the store raised while working
/// correctly: a constraint or unique-index violation (which is a caller conflict and has its own
/// translation), an invalid column or object name, a permission refusal, an arithmetic or conversion
/// fault, a deadlock victim. All of those are classes 11 to 19, they stay <c>500</c> or the status their
/// own translation chose, and that is correct - a caller told to retry a statement the store will refuse
/// every time is worse off than one told the server failed. The bias is deliberate and one-directional:
/// where a condition is ambiguous the answer is false, because mislabelling a defect as an outage hides
/// the defect and invites an infinite retry, while mislabelling an outage as a defect merely costs the
/// caller a retry hint they can live without.
/// </para>
/// <para>
/// <strong>No message text is read anywhere in this type.</strong> A provider message routinely quotes
/// the server name, the database name and the values bound to a statement; the duplicate-key translator
/// reads message text only to extract a constraint name it treats as optional. Here the decision rests
/// entirely on numeric and structural facts, so nothing this type touches can carry a credential, and it
/// cannot be defeated by a localised server whose wording differs.
/// </para>
/// </remarks>
internal sealed class SqlStoreFailureClassifier : IStoreFailureClassifier
{
    /// <summary>
    /// Lowest SQL Server severity class that terminates the connection, and the class the client also
    /// reports for a connection it could not establish at all.
    /// </summary>
    /// <remarks>
    /// Severity 20 and above is documented as a fatal error: the statement is abandoned and the
    /// connection is closed, so the same request may well succeed once the condition clears. Severity 19
    /// and below is either a caller-visible refusal or a defect, and neither becomes true or false with
    /// time. This one boundary is what the measured <c>Number = 0, Class = 20</c> connection failure is
    /// recognised by.
    /// </remarks>
    private const byte FatalSeverityClass = 20;

    /// <summary>Error number the client reports when a command exceeded its timeout.</summary>
    /// <remarks>
    /// Carried with a class below the fatal boundary, so it needs naming explicitly. A timeout is the one
    /// availability condition that does not close the connection: the store was reachable and did not
    /// answer in time, which is a load or a lock-contention condition and is exactly the case a retry
    /// hint is for.
    /// </remarks>
    private const int CommandTimeoutErrorNumber = -2;

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// The whole chain is walked, because the fault that matters is routinely not the outermost one: the
    /// mapper wraps a provider failure during a flush, an exhausted retry strategy wraps the last
    /// attempt's failure, and an application-level failure may carry either as its cause. A test that
    /// looked only at the surface would classify the same outage correctly on a read and incorrectly on a
    /// write.
    /// </para>
    /// <para>
    /// The walk is depth-bounded and aggregate-aware. A depth bound is not defensive dressing: an
    /// exception chain is built from arbitrary <c>InnerException</c> references, a cyclic or pathologically
    /// deep chain would spin here, and this method runs while a failure response is being composed - the
    /// one place a hang costs the caller the response as well as the request. An
    /// <see cref="AggregateException"/> is flattened rather than followed by its first inner reference
    /// alone, because a parallel operation reports every branch's failure and the store fault may be in
    /// any of them.
    /// </para>
    /// </remarks>
    public bool IsStoreUnavailable(Exception? exception)
    {
        const int maximumDepth = 32;

        if (exception is null)
        {
            return false;
        }

        Exception? candidate = exception;

        for (int depth = 0; candidate is not null && depth < maximumDepth; depth++)
        {
            if (candidate is AggregateException aggregate)
            {
                foreach (Exception inner in aggregate.Flatten().InnerExceptions)
                {
                    if (IsStoreUnavailable(inner))
                    {
                        return true;
                    }
                }

                return false;
            }

            if (DescribesUnavailability(candidate))
            {
                return true;
            }

            candidate = candidate.InnerException;
        }

        return false;
    }

    /// <summary>Tests one link of an exception chain, without following it further.</summary>
    /// <param name="candidate">The link to test.</param>
    /// <returns><see langword="true"/> when this link alone establishes an availability failure.</returns>
    /// <remarks>
    /// <para>
    /// The arms are ordered from the most specific to the most general, and each is here for a measured or
    /// documented reason rather than for symmetry:
    /// </para>
    /// <para>
    /// <see cref="SqlException"/> is examined by severity, by the client's own transient flag and by the
    /// command-timeout number, and EVERY error in its collection is examined rather than only the one the
    /// exception surfaces as its own: a batch can report several, and the connection-level one need not be
    /// first. The transient flag is consulted even though the measured outage did not set it, because it
    /// is precisely what a managed instance DOES set during a failover or a throttling episode - the two
    /// conditions a deployment is most likely to meet in production and least likely to meet in a test.
    /// </para>
    /// <para>
    /// <see cref="SocketException"/> covers the path rather than the store: name resolution failing, a
    /// refused connection, a reset or an unreachable host. It appears as the cause of a client failure on
    /// some platforms and standalone when a connection is torn down mid-stream.
    /// </para>
    /// <para>
    /// <see cref="TimeoutException"/> is the framework-level timeout the client raises when it gives up
    /// obtaining a connection from the pool, which is a saturation condition and not a defect.
    /// </para>
    /// <para>
    /// Nothing else qualifies. In particular no arm matches on a mapper type: a failed flush is not an
    /// availability condition by virtue of having failed, and if a store fault caused it, the fault is in
    /// the chain and is found there.
    /// </para>
    /// </remarks>
    private static bool DescribesUnavailability(Exception candidate)
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
                return true;

            default:
                return false;
        }
    }
}
