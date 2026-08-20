namespace DnnMigration.Domain.Abstractions.Services;

// MIGRATION: reversible credential storage is deliberately not carried forward, and neither is password
// read-back: no member of this contract, and no service, endpoint or screen layered above it, can recover a
// plaintext password.

/// <summary>
/// The strictly one-way credential hashing contract: a plaintext password can be turned into a stored
/// digest and a candidate can be checked against one, but the original plaintext can never be obtained
/// through this contract.
/// </summary>
/// <remarks>
/// <para>
/// Policy does not live here. Minimum length, character composition, question-and-answer requirements and
/// e-mail uniqueness are Application-layer concerns, so a policy change never requires a change to this
/// contract.
/// </para>
/// <para>
/// All three members are synchronous by design. Hashing and checking are pure CPU work that touches no
/// socket, file or database, so the solution-wide rule that I/O-bound members be awaitable does not apply
/// and these members must not be reshaped into an awaitable form.
/// </para>
/// </remarks>
public interface IPasswordHasher
{
    /// <summary>Turns a plaintext password into the one-way representation stored against the account.</summary>
    /// <param name="password">The plaintext password to hash.</param>
    /// <returns>The stored representation, ready to persist.</returns>
    string Hash(string password);

    /// <summary>Determines whether a candidate plaintext password matches a stored representation.</summary>
    /// <param name="password">The candidate plaintext password to check.</param>
    /// <param name="passwordHash">
    /// The stored representation to check against, as produced by <see cref="Hash(string)"/>.
    /// </param>
    /// <returns><see langword="true"/> on a match; otherwise <see langword="false"/>.</returns>
    bool Verify(string password, string passwordHash);

    /// <summary>
    /// Reports whether a stored representation should be regenerated the next time the owning credential is
    /// successfully presented.
    /// </summary>
    /// <param name="passwordHash">The stored representation to examine.</param>
    /// <returns>
    /// <see langword="true"/> when the current implementation would now generate a stronger representation
    /// than the one supplied - in practice, when this scheme's cost has been raised since the value was
    /// written; otherwise <see langword="false"/>.
    /// </returns>
    bool NeedsRehash(string passwordHash);

    /// <summary>
    /// A stored-representation-shaped value that no credential will ever match, produced at the same cost
    /// as a real one, for equalising the work an authentication attempt performs when there is no stored
    /// representation to compare against.
    /// </summary>
    /// <value>A well-formed representation of a credential the implementation generated and discarded.</value>
    /// <remarks>
    /// WHY IT EXISTS AT ALL. An authentication path that returns early when it finds no account, no tenant
    /// or no credential record answers measurably faster than one that reaches a deliberately expensive
    /// comparison. Uniform wording on the refusal does not close that: the response TIME distinguishes the
    /// cases, which lets an unauthenticated caller enumerate accounts.
    /// </remarks>
    string UnmatchableHash { get; }
}
