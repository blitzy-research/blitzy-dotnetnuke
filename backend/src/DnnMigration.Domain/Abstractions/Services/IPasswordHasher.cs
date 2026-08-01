namespace DnnMigration.Domain.Abstractions.Services;

// MIGRATION: Reversible credential storage is deliberately not carried forward.
// DotNetNuke 4.9.0 held passwords in a form that could be turned back into plaintext:
// the reversible cipher pair at Library/Components/Security/PortalSecurity.vb L138 and
// L175 ran a single-DES transform in both directions, and the ASP.NET membership
// provider was registered with a reversible storage format at Website/release.config
// L245. Both are replaced by one-way hashing performed in DnnMigration.Infrastructure.
// The symmetric key that unlocked every stored credential was committed to source
// control at Website/release.config L91, with the algorithm named at L92; it is cited
// here by file and line only, and its value is never reproduced in this codebase.
//
// MIGRATION: Password read-back is deliberately removed. The legacy membership provider
// switched it on at Website/release.config L239. No member of this contract offers it,
// and neither does any service, endpoint or screen layered above this contract. The
// removal is intentional and permanent, and it is the reason this contract is shaped
// the way it is.
//
// MIGRATION: Upgrade path for credentials already present in the database. A one-way
// digest cannot check a credential that was stored under the legacy reversible scheme,
// so existing rows are migrated lazily: the stored value is re-hashed on the first
// successful legacy sign-in, and an administrative reset is the fallback for any
// account that never signs in again. NeedsRehash is the hook that makes that path
// possible, which is why it belongs on this contract rather than being hidden inside
// the implementation.
//
// MIGRATION: Password policy is preserved exactly as measured and is NOT tightened.
// Minimum length 7 (Website/release.config L242), zero required non-alphanumeric
// characters (L243), no question-and-answer requirement (L241) and email uniqueness
// not enforced (L244) are reproduced in the Application layer as bound options and
// declarative request validators. None of those values belongs on this contract, and
// strengthening any of them as part of this migration would deny existing users access
// to their own accounts.

/// <summary>
/// Defines the strictly one-way credential hashing contract for the application. A
/// plaintext password can be turned into a stored digest, and a candidate password can
/// be checked against a stored digest, but the original plaintext can never be obtained
/// through this contract, by design.
/// </summary>
/// <remarks>
/// <para>
/// The impossibility of reading a password back is the defining property of this type,
/// and it is expressed in the shape of the contract rather than left to the discipline
/// of an implementer. There is deliberately no member capable of producing a plaintext
/// password from stored data, and none may be added: doing so would reinstate the exact
/// weakness this migration exists to eliminate. The MIGRATION notes above record the
/// legacy behaviour being replaced and the upgrade path applied to credentials that
/// already exist in the database.
/// </para>
/// <para>
/// Password policy does not live here. Minimum length, character composition,
/// question-and-answer requirements and email uniqueness are Application-layer
/// concerns, expressed as bound options and declarative request validators. This
/// contract is concerned only with turning an already-accepted password into a stored
/// digest, and with checking a candidate against one, so a policy change never requires
/// a change here. Per the MIGRATION note above, the legacy policy is reproduced rather
/// than strengthened.
/// </para>
/// <para>
/// Implementation and lifetime. DnnMigration.Infrastructure supplies the single
/// implementation, Infrastructure/Security/BcryptPasswordHasher.cs, and registers it
/// with a singleton lifetime because it holds no per-request state. The hashing
/// algorithm and the third-party package that provides it are named and referenced only
/// by that project. This project declares no package reference of any kind, so the
/// choice of algorithm cannot leak inward and can be revised without touching the
/// domain.
/// </para>
/// <para>
/// Synchronous by design. All three members are synchronous. Hashing and checking are
/// pure CPU work and touch no socket, file or database, so there is nothing to await and
/// no cancellation to observe. This is a deliberate decision rather than an oversight:
/// the project-wide rule that I/O-bound work be awaited does not apply to computation,
/// and these members must not be reshaped into an awaitable form.
/// </para>
/// </remarks>
public interface IPasswordHasher
{
    /// <summary>
    /// Turns a plaintext password into the one-way representation that is stored against
    /// the account.
    /// </summary>
    /// <param name="password">
    /// The plaintext password to hash. The implementation rejects a value that is empty
    /// or consists only of white space; this contract does not model an absent password
    /// with an empty value.
    /// </param>
    /// <returns>
    /// The stored representation of <paramref name="password"/>, ready to be persisted.
    /// Hashing the same plaintext twice is expected to produce two different values, so
    /// a stored representation must never be compared for equality; use
    /// <see cref="Verify(string, string)"/> instead.
    /// </returns>
    string Hash(string password);

    /// <summary>
    /// Determines whether a candidate plaintext password matches a stored one-way
    /// representation.
    /// </summary>
    /// <param name="password">The candidate plaintext password to check.</param>
    /// <param name="passwordHash">
    /// The stored representation to check against, as produced by
    /// <see cref="Hash(string)"/>.
    /// </param>
    /// <returns>
    /// <see langword="true"/> when <paramref name="password"/> matches
    /// <paramref name="passwordHash"/>; otherwise <see langword="false"/>. A mismatch and
    /// a malformed stored representation are both reported as <see langword="false"/>
    /// rather than as a thrown exception, so a damaged stored value can never be coaxed
    /// into admitting an arbitrary password.
    /// </returns>
    bool Verify(string password, string passwordHash);

    /// <summary>
    /// Reports whether a stored one-way representation should be regenerated the next
    /// time the owning credential is successfully presented.
    /// </summary>
    /// <param name="passwordHash">The stored representation to examine.</param>
    /// <returns>
    /// <see langword="true"/> when <paramref name="passwordHash"/> should be replaced,
    /// either because it was produced by the legacy reversible scheme or because the
    /// current implementation would now generate a stronger one; otherwise
    /// <see langword="false"/>. Callers pair this with a successful
    /// <see cref="Verify(string, string)"/> to upgrade credentials in place, as described
    /// in the MIGRATION notes on this file.
    /// </returns>
    bool NeedsRehash(string passwordHash);
}
