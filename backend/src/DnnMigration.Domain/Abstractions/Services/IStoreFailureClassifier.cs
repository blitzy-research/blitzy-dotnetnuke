namespace DnnMigration.Domain.Abstractions.Services;

// MIGRATION: the legacy application had no equivalent and could not have had one. Its data layer
// called SqlHelper (Library/Components/DataAccessBlock/bin/Microsoft.ApplicationBlocks.Data.dll, a
// binary with no source in the repository) from Library/Providers/DataProviders/SqlDataProvider/
// SqlDataProvider.vb, and a provider fault surfaced wherever it happened to be raised - the in-scope
// trees contain no exception-to-response translator at all, so a database outage reached the browser
// as whatever the ASP.NET 2.0 error page made of it. This contract exists because the target has one
// error surface, and that surface has to be able to tell "this store cannot serve right now" apart
// from "this application has a defect" WITHOUT the transport layer acquiring a database dependency.

/// <summary>
/// Decides whether a failure means the backing store could not be reached or could not serve, as
/// opposed to a defect in this application.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this is a contract rather than a method somewhere.</b> The two facts it reconciles pull in
/// opposite directions. Only the persistence assembly may name the database client type - that is what
/// keeps the API project free of a provider dependency, and the project reference graph is what makes
/// the rule structural rather than aspirational. But the decision the classification feeds is a
/// TRANSPORT decision: a store that is unreachable is a 503 with a retry hint, while a defect is a
/// 500, and the two must not be conflated because a caller and an orchestrator act differently on
/// each. Declaring the question here and answering it in the assembly that owns provider knowledge
/// satisfies both: the classification is performed by the layer that can perform it correctly, and the
/// layer that needs the answer asks for it by name.
/// </para>
/// <para>
/// <b>What it must answer true for.</b> A condition in which the store, or the path to it, is at
/// fault and the same request could plausibly succeed later: the server cannot be resolved or
/// refuses the connection, the connection is dropped mid-statement, the instance is failing over or
/// throttling, the database is unavailable or at its connection ceiling, or a command exceeds its
/// timeout.
/// </para>
/// <para>
/// <b>What it must answer false for, and this matters more.</b> Anything the store answered
/// correctly. A rejected write, an absent column, a permission refusal, an arithmetic overflow, a
/// constraint violation and every failure of this application's own logic are defects or
/// caller-visible conflicts, and reporting them as a temporary outage would tell a caller to retry a
/// request that can never succeed while hiding a fault that needs fixing. Where a condition is
/// ambiguous the answer is false: over-reporting availability failures is the more damaging error,
/// because it converts a permanent defect into an invitation to retry forever.
/// </para>
/// <para>
/// <b>Chain, not surface.</b> An implementation examines the whole exception chain, because the
/// original provider fault is routinely wrapped - by the object-relational mapper on a write, by a
/// retry strategy that exhausted its attempts, or by an application-level failure that carries it as
/// its cause - and the wrapper's own type says nothing about whether the store was reachable.
/// </para>
/// <para>
/// <b>Purity.</b> An implementation must be free of side effects, must not touch the network or the
/// clock, must never throw for any input including <see langword="null"/>, and must be safe to call
/// from any thread. It is consulted while a response to a failure is being composed, which is the
/// least forgiving moment in a request's life: a classifier that threw there would replace a
/// well-formed error response with no response at all.
/// </para>
/// </remarks>
public interface IStoreFailureClassifier
{
    /// <summary>
    /// Reports whether <paramref name="exception"/>, or any exception in its chain, means the backing
    /// store was unreachable or unable to serve.
    /// </summary>
    /// <param name="exception">
    /// The failure to classify. <see langword="null"/> is permitted and answers
    /// <see langword="false"/>, because the caller is an error path and must not be given a second
    /// failure to handle while handling the first.
    /// </param>
    /// <returns>
    /// <see langword="true"/> when the failure is an availability condition of the store or of the
    /// path to it; <see langword="false"/> for every other failure, including one that cannot be
    /// classified.
    /// </returns>
    bool IsStoreUnavailable(Exception? exception);
}
