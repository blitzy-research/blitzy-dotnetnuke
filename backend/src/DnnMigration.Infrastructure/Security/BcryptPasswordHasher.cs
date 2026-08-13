// MIGRATION: password read-back is removed outright rather than merely switched off. The legacy store
// advertised the capability at Website/release.config:L239 and exposed a member that handed the caller the
// stored password.

using System.Security.Cryptography;
using BCrypt.Net;
using DnnMigration.Application.Options;
using DnnMigration.Application.Validation;
using DnnMigration.Domain.Abstractions.Services;
using Microsoft.Extensions.Options;

namespace DnnMigration.Infrastructure.Security;

/// <summary>
/// The single implementation of <see cref="IPasswordHasher"/>, turning a plaintext password into a one-way
/// BCrypt digest and checking a candidate password against a stored digest.
/// </summary>
/// <remarks>
/// <para>
/// <b>Lifetime and state.</b> Registered as a singleton, which is safe because every field is readonly and
/// nothing is cached per caller.
/// </para>
/// <para>
/// <b>Where the work factor lives.</b> The cost is a single private constant on this type,
/// <c>WorkFactor</c>, deliberately not a property of the bound policy.
/// </para>
/// </remarks>
internal sealed class BcryptPasswordHasher : IPasswordHasher
{
    /// <summary>
    /// The BCrypt cost, as the base-2 logarithm of the number of rounds applied, so the work performed
    /// grows as 2^<c>WorkFactor</c>.
    /// </summary>
    /// <remarks>
    /// Twelve is one step above the 11 the hashing package uses when no cost is supplied, and it is stated
    /// explicitly rather than left to that default so a package upgrade cannot silently move it in either
    /// direction. It is the sole cost in this type: hashing and replacement detection share it, so the two
    /// can never disagree about what "current" means.
    /// </remarks>
    private const int WorkFactor = 12;

    /// <summary>The pre-hash algorithm used by the enhanced hashing pair, stated explicitly on every call.</summary>
    /// <remarks>
    /// SHA-384 is also the package's own default for both halves of the pair, so naming it changes no
    /// behaviour today. It is named anyway for one reason: an enhanced digest records nothing about which
    /// pre-hash produced it, so if a future package release moved that default the hashing and verification
    /// halves would begin disagreeing silently and every existing digest would stop verifying.
    /// </remarks>
    private const HashType PreHashAlgorithm = HashType.SHA384;

    /// <summary>The password policy in force, captured once at construction.</summary>
    private readonly PasswordPolicyOptions _passwordPolicy;

    /// <summary>
    /// The decoy representation served by <see cref="UnmatchableHash"/>, computed at most once for the
    /// lifetime of this instance and only if something asks for it.
    /// </summary>
    /// <remarks>
    /// LAZY, AND THREAD-SAFE BY THE MODE THAT IS CHOSEN. Computing it costs one full hashing operation -
    /// the same deliberately expensive operation the cost factor governs - so it must be paid once and
    /// never per attempt. <see cref="LazyThreadSafetyMode.ExecutionAndPublication"/> is named explicitly
    /// rather than left to the default because this type is registered as a singleton and is therefore
    /// reached concurrently: the default for this constructor overload is already the safe mode, and
    /// stating it keeps that guarantee from resting on a default nobody can see at the call site.
    /// </remarks>
    private readonly Lazy<string> _unmatchableHash = new(
        CreateUnmatchableHash,
        LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>Initialises a new instance of the <see cref="BcryptPasswordHasher"/> class.</summary>
    /// <param name="passwordPolicyOptions">The bound password policy.</param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="passwordPolicyOptions"/> is <see langword="null"/>.
    /// </exception>
    public BcryptPasswordHasher(IOptions<PasswordPolicyOptions> passwordPolicyOptions)
    {
        ArgumentNullException.ThrowIfNull(passwordPolicyOptions);

        _passwordPolicy = passwordPolicyOptions.Value;
    }

    /// <summary>Turns a plaintext password into the one-way representation stored against the account.</summary>
    /// <param name="password">The plaintext password to hash.</param>
    /// <returns>The stored representation of <paramref name="password"/>, ready to be persisted.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="password"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="password"/> is empty, consists only of white space, or fails the bound policy.
    /// </exception>
    public string Hash(string password)
    {
        // MIGRATION: one narrow, deliberate divergence, recorded rather than absorbed.
        ArgumentException.ThrowIfNullOrWhiteSpace(password);

        // These two guards mirror the legacy check at
        // Library/Components/Users/UserController.vb:L1067-L1091 and nothing more.
        int minimumLength = _passwordPolicy.MinRequiredPasswordLength;
        if (password.Length < minimumLength)
        {
            throw new ArgumentException(
                $"A password must be at least {minimumLength} characters long.",
                nameof(password));
        }

        int minimumNonAlphanumericCharacters = _passwordPolicy.MinRequiredNonAlphanumericCharacters;

        if (minimumNonAlphanumericCharacters > 0
            && CountNonAlphanumericCharacters(password) < minimumNonAlphanumericCharacters)
        {
            throw new ArgumentException(
                $"A password must contain at least {minimumNonAlphanumericCharacters} character(s) "
                + "outside the ranges 0-9, A-Z and a-z.",
                nameof(password));
        }

        // The shared upper bound, applied explicitly.
        if (!CredentialBounds.IsWithinMaximumByteLength(password))
        {
            throw new ArgumentException(
                "A password must be no longer than "
                + $"{CredentialBounds.MaximumByteLength} bytes when encoded as UTF-8.",
                nameof(password));
        }

        // The enhanced half of the pair. It digests the credential with SHA-384 before hashing, so every
        // byte of the input contributes and the algorithm's 72-byte significance limit cannot make two
        // distinct passwords equivalent.
        return BCrypt.Net.BCrypt.EnhancedHashPassword(password, WorkFactor, PreHashAlgorithm);
    }

    /// <summary>Determines whether a candidate plaintext password matches a stored one-way representation.</summary>
    /// <param name="password">
    /// The candidate plaintext password to check, used exactly as supplied for the reason given on <see
    /// cref="Hash(string)"/>.
    /// </param>
    /// <param name="passwordHash">
    /// The stored representation to check against, as produced by <see cref="Hash(string)"/>.
    /// </param>
    /// <returns>
    /// <see langword="true"/> when <paramref name="password"/> matches <paramref name="passwordHash"/>;
    /// otherwise <see langword="false"/>.
    /// </returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="password"/> or <paramref name="passwordHash"/> is <see langword="null"/>.
    /// </exception>
    public bool Verify(string password, string passwordHash)
    {
        ArgumentNullException.ThrowIfNull(password);
        ArgumentNullException.ThrowIfNull(passwordHash);

        // The shared upper bound again, and the ONLY bound applied on this path. It is not policy: no
        // stored credential can exceed it, because Hash refuses to mint one that does, so refusing an
        // over-long candidate cannot lock anyone out of an existing account.
        if (!CredentialBounds.IsWithinMaximumByteLength(password))
        {
            return false;
        }

        // A stored value arrives from a database row that may predate this implementation entirely, so
        // every way the hashing package can reject one is answered with "does not match" rather than
        // allowed to surface as a fault.
        try
        {
            return BCrypt.Net.BCrypt.EnhancedVerify(password, passwordHash, PreHashAlgorithm);
        }
        catch (BCrypt.Net.SaltParseException)
        {
            return false;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (FormatException)
        {
            return false;
        }
        catch (IndexOutOfRangeException)
        {
            return false;
        }
    }

    /// <summary>
    /// Reports whether a stored one-way representation should be regenerated the next time the owning
    /// credential is successfully presented.
    /// </summary>
    /// <param name="passwordHash">The stored representation to examine.</param>
    /// <returns>
    /// <see langword="true"/> only when <paramref name="passwordHash"/> is a digest this implementation
    /// produced and carries a lower cost than <c>WorkFactor</c>; otherwise <see langword="false"/>,
    /// including for any value it cannot parse.
    /// </returns>
    /// <remarks>
    /// This member exists to support ONE upgrade: raising the cost of a digest <see cref="Hash(string)"/>
    /// produced. It is not a legacy-credential detector, and a <see langword="true"/> answer must never be
    /// read as an invitation to accept a credential that <see cref="Verify(string, string)"/> rejected.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="passwordHash"/> is <see langword="null"/>.</exception>
    public bool NeedsRehash(string passwordHash)
    {
        ArgumentNullException.ThrowIfNull(passwordHash);

        try
        {
            return BCrypt.Net.BCrypt.PasswordNeedsRehash(passwordHash, WorkFactor);
        }
        catch (BCrypt.Net.SaltParseException)
        {
            return false;
        }
        catch (BCrypt.Net.HashInformationException)
        {
            return false;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (FormatException)
        {
            return false;
        }
        catch (IndexOutOfRangeException)
        {
            return false;
        }
    }

    /// <inheritdoc />
    public string UnmatchableHash => _unmatchableHash.Value;

    /// <summary>
    /// Produces the decoy representation, at the current work factor, from cryptographically random input
    /// that is not retained.
    /// </summary>
    /// <returns>A well-formed stored representation that no credential matches.</returns>
    /// <remarks>
    /// The input is Base64 of thirty-two random bytes, which lands comfortably inside the credential length
    /// bound and inside the range the enhanced pre-hash accepts.
    /// </remarks>
    private static string CreateUnmatchableHash()
    {
        string unmatchable = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

        return BCrypt.Net.BCrypt.EnhancedHashPassword(unmatchable, WorkFactor, PreHashAlgorithm);
    }

    /// <summary>
    /// Counts the characters in <paramref name="password"/> that fall outside the ASCII alphanumeric set.
    /// </summary>
    /// <param name="password">The password to scan.</param>
    /// <returns>The number of characters that are not an ASCII digit or an ASCII letter.</returns>
    private static int CountNonAlphanumericCharacters(string password)
    {
        int count = 0;

        foreach (char character in password)
        {
            if (!char.IsAsciiLetterOrDigit(character))
            {
                count++;
            }
        }

        return count;
    }
}
