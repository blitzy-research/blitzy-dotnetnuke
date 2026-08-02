namespace DnnMigration.Domain.Abstractions.Services;

// MIGRATION: reversible credential storage is deliberately not carried forward, and neither is
// password read-back: no member of this contract, and no service, endpoint or screen layered
// above it, can recover a plaintext password. Two distinct reversible mechanisms existed in
// DotNetNuke 4.9.0 and are easily conflated. The first is a general-purpose symmetric helper,
// Library/Components/Security/PortalSecurity.vb L138 and L175, which ran single DES over a
// caller-supplied key string; it was NOT the credential store. The second is the credential
// store itself: the ASP.NET SQL membership provider, registered with the reversible
// "Encrypted" password format and with retrieval enabled, whose ciphertext was recoverable
// with the deployment's Triple-DES machine key. That key was committed to source control, so
// any holder of the legacy sources could read every stored password - which is why neither
// mechanism is reproduced and why no key material, and no pointer to it, appears here.
//
// MIGRATION: a one-way digest cannot check a credential stored under the legacy reversible
// scheme, so existing rows migrate lazily: the stored value is re-hashed on the first
// successful legacy sign-in, and an administrative reset is the fallback for an account that
// never signs in again. NeedsRehash is the hook that makes that path possible, which is why it
// belongs on this contract rather than inside an implementation.
//
// MIGRATION: the measured legacy password policy is preserved exactly and is NOT tightened.
// It lives in the Application layer as bound options and declarative request validators, never
// on this contract, so a policy change never reaches the domain. Strengthening it as part of
// this migration would deny existing users access to their own accounts.

/// <summary>
/// The strictly one-way credential hashing contract: a plaintext password can be turned into a
/// stored digest and a candidate can be checked against one, but the original plaintext can
/// never be obtained through this contract.
/// </summary>
/// <remarks>
/// <para>
/// The impossibility of reading a password back is expressed in the shape of the contract
/// rather than left to an implementer's discipline. No member capable of producing plaintext
/// from stored data may be added: doing so would reinstate the exact weakness recorded in the
/// migration notes above.
/// </para>
/// <para>
/// Policy does not live here. Minimum length, character composition, question-and-answer
/// requirements and e-mail uniqueness are Application-layer concerns, so a policy change never
/// requires a change to this contract.
/// </para>
/// <para>
/// All three members are synchronous by design. Hashing and checking are pure CPU work that
/// touches no socket, file or database, so the solution-wide rule that I/O-bound members be
/// awaitable does not apply and these members must not be reshaped into an awaitable form. An
/// implementation is expected to hold no per-request state and to be registered as a singleton;
/// the algorithm and the package that provides it are referenced only by the implementing
/// project, so the choice cannot leak into a domain that declares no package reference at all.
/// </para>
/// </remarks>
public interface IPasswordHasher
{
    /// <summary>
    /// Turns a plaintext password into the one-way representation stored against the account.
    /// </summary>
    /// <param name="password">
    /// The plaintext password to hash. An implementation rejects a value that is empty or white
    /// space; this contract does not model an absent password as an empty value.
    /// </param>
    /// <returns>
    /// The stored representation, ready to persist. Hashing the same plaintext twice is expected
    /// to yield different values, so a stored representation must never be compared for equality;
    /// use <see cref="Verify(string, string)"/> instead.
    /// </returns>
    string Hash(string password);

    /// <summary>
    /// Determines whether a candidate plaintext password matches a stored representation.
    /// </summary>
    /// <param name="password">The candidate plaintext password to check.</param>
    /// <param name="passwordHash">
    /// The stored representation to check against, as produced by <see cref="Hash(string)"/>.
    /// </param>
    /// <returns>
    /// <see langword="true"/> on a match; otherwise <see langword="false"/>. A mismatch and a
    /// malformed stored representation are both reported as <see langword="false"/> rather than as
    /// a thrown exception, so a damaged stored value can never be coaxed into admitting an
    /// arbitrary password.
    /// </returns>
    bool Verify(string password, string passwordHash);

    /// <summary>
    /// Reports whether a stored representation should be regenerated the next time the owning
    /// credential is successfully presented.
    /// </summary>
    /// <param name="passwordHash">The stored representation to examine.</param>
    /// <returns>
    /// <see langword="true"/> when the value was produced by the legacy reversible scheme or when
    /// the current implementation would now generate a stronger one; otherwise
    /// <see langword="false"/>. Pair this with a successful <see cref="Verify(string, string)"/> to
    /// upgrade a credential in place, as described in the migration notes on this file.
    /// </returns>
    bool NeedsRehash(string passwordHash);
}
