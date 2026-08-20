namespace DnnMigration.Domain.Abstractions.Services;

/// <summary>
/// Decides whether a failure means the backing store could not be reached or could not serve, as opposed to
/// a defect in this application.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this is a contract rather than a method somewhere.</b> The two facts it reconciles pull in
/// opposite directions. Only the persistence assembly may name the database client type - that is what
/// keeps the API project free of a provider dependency, and the project reference graph is what makes the
/// rule structural rather than aspirational.
/// </para>
/// <para>
/// <b>What it must answer true for.</b> A condition in which the store, or the path to it, is at fault and
/// the same request could plausibly succeed later: the server cannot be resolved or refuses the connection,
/// the connection is dropped mid-statement, the instance is failing over or throttling, the database is
/// unavailable or at its connection ceiling, or a command exceeds its timeout.
/// </para>
/// </remarks>
public interface IStoreFailureClassifier
{
    /// <summary>
    /// Reports whether <paramref name="exception"/>, or any exception in its chain, means the backing store
    /// was unreachable or unable to serve.
    /// </summary>
    /// <param name="exception">
    /// The failure to classify. <see langword="null"/> is permitted and answers <see langword="false"/>,
    /// because the caller is an error path and must not be given a second failure to handle while handling
    /// the first.
    /// </param>
    /// <returns>
    /// <see langword="true"/> when the failure is an availability condition of the store or of the path to
    /// it; <see langword="false"/> for every other failure, including one that cannot be classified.
    /// </returns>
    bool IsStoreUnavailable(Exception? exception);

    /// <summary>
    /// Reports whether <paramref name="exception"/>, or any exception in its chain, means the work was
    /// ABANDONED - the caller went away, or the token governing the work was cancelled - rather than failed.
    /// </summary>
    /// <param name="exception">
    /// The failure to classify. <see langword="null"/> is permitted and answers <see langword="false"/>, for
    /// the reason given on the member above.
    /// </param>
    /// <returns>
    /// <see langword="true"/> when the chain establishes that the work was cancelled; <see langword="false"/>
    /// for every other failure, including one that cannot be classified.
    /// </returns>
    /// <remarks>
    /// <para>
    /// <b>Why this is not simply a test for <see cref="OperationCanceledException"/> at the call site.</b>
    /// Cancelling work that is already inside the database client does not always surface as that type. The
    /// client can report the abandonment in its OWN exception type instead, and the mapper then wraps
    /// whatever it was handed - so a caller who navigated away could be recorded as an unhandled server
    /// fault, with a status nobody was left to receive. Only the persistence assembly may name the client's
    /// type, which is why the question is asked here rather than answered with a type test in the API
    /// project.
    /// </para>
    /// <para>
    /// <b>What it must NOT answer true for.</b> A command TIMEOUT is not a cancellation. The store was
    /// reachable and did not answer in time, the caller is still waiting, and the honest answer to them is
    /// the availability condition the member above already reports - not silence.
    /// </para>
    /// </remarks>
    bool IsCallerCancellation(Exception? exception);
}
