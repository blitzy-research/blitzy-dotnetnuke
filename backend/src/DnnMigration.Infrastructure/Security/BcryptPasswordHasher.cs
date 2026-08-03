// MIGRATION: the legacy installation stored credentials in a form that could be turned back into
// plaintext. A reversible transform pair at Library/Components/Security/PortalSecurity.vb:L138 and
// L175 ran the same scheme in both directions, and the storage-format attribute at
// Website/release.config:L245 selected it for the credential store. All of that is replaced here by
// one-way BCrypt hashing, so no code path in this class - or above it - can turn a stored value back
// into a password. The shared secret that made the legacy scheme reversible was committed to source
// control at Website/release.config:L91-L92; it is cited by file and line only, and its value is
// reproduced nowhere in this solution.
//
// MIGRATION: password read-back is removed outright rather than merely switched off. The legacy
// store advertised the capability at Website/release.config:L239 and exposed a member that handed
// the caller the stored password. This class declares no counterpart, and no service, endpoint or
// screen above it returns or mails a stored password. Under a one-way digest that is impossible
// rather than forbidden, which is the entire point of the change.
//
// MIGRATION: CREDENTIALS ALREADY PRESENT IN THE DATABASE ARE MIGRATED BY ADMINISTRATIVE RESET, AND
// BY NOTHING ELSE. A one-way digest cannot be derived from a value held under the legacy reversible
// scheme, and this class verifies BCrypt digests only - it holds no legacy verifier, and the target
// maps no legacy credential column, so nothing anywhere in this solution can check a submitted
// password against a legacy stored value. Every pre-existing account therefore requires an
// administrative password reset before its owner can sign in, and that is a DELIBERATE FUNCTIONAL
// REDUCTION recorded in MIGRATION_NOTES.md rather than a gap.
//
// This class does NOT upgrade rows lazily, and no comment here or elsewhere may claim that it does -
// there is no re-hash of the plaintext an owner supplies on a first successful sign-in against a
// legacy value, because a first successful sign-in against a legacy value is impossible without a
// legacy verifier and none exists. Implementing
// one would require reading the legacy reversible material, decrypting it with the key committed at
// Website/release.config:L91-L92, and re-encrypting on success - a design that reintroduces exactly
// the reversibility this class exists to remove, and one the AAP's scope does not include.
//
// MIGRATION: NeedsRehash serves the remaining, genuinely supported upgrade: raising the cost of a
// digest this class produced itself. It reports whether a stored BCrypt digest was computed below
// the current work factor, so a caller that has ALREADY verified a password successfully can
// re-hash it at the current cost. It is not, and never was, a legacy-credential detector.
//
// MIGRATION: HASHING AND VERIFICATION BOTH USE BCRYPT.NET'S ENHANCED PAIR, AND THE TWO CAN NEVER
// DIVERGE BECAUSE NEITHER IS CALLED ANYWHERE ELSE. Plain BCrypt ignores every byte of its input past
// the first 72, so two distinct passwords sharing a 72-byte prefix authenticate interchangeably -
// verified empirically against BCrypt.Net-Next 4.0.3 rather than assumed. The enhanced pair digests
// the credential with SHA-384 before hashing, so the whole of the input contributes and that
// equivalence disappears. The pairing is not optional: an enhanced digest is an ordinary BCrypt
// digest of the pre-hashed value and carries no marker distinguishing it, so mixing an enhanced hash
// with a plain verification - or the reverse - fails every check silently. Both directions were
// confirmed to return false. This is a divergence from the legacy scheme in a class that already
// replaces it wholesale; it is recorded in MIGRATION_NOTES.md, and because the target maps no
// credential column the change has no stored data to be compatible with.
//
// MIGRATION: the legacy password policy is preserved exactly and is never tightened. A minimum
// length of 7 (Website/release.config:L242) and a minimum of zero characters outside 0-9, A-Z and
// a-z (L243) are the only two rules that reach this class, and both arrive as bound options rather
// than as literals written here. The remaining legacy switches - no security question or answer is
// demanded (L241), and an email address need not be unique (L244) - are registration concerns
// rather than hashing concerns, so they stay with the Application layer, which remains the primary
// policy boundary through its bound options and its declarative request validators. Strengthening
// any of these during the migration would deny access to accounts the legacy installation accepted.

using System.Security.Cryptography;
using BCrypt.Net;
using DnnMigration.Application.Options;
using DnnMigration.Application.Validation;
using DnnMigration.Domain.Abstractions.Services;
using Microsoft.Extensions.Options;

namespace DnnMigration.Infrastructure.Security;

/// <summary>
/// The single implementation of <see cref="IPasswordHasher"/>, turning a plaintext password into a
/// one-way BCrypt digest and checking a candidate password against a stored digest.
/// </summary>
/// <remarks>
/// <para>
/// <b>One-way only.</b> The type exposes exactly the three operations the contract declares, and
/// none of them can produce a plaintext password from stored data. No member may be added that
/// does: the whole reason this type exists is that the scheme it replaces could, as recorded in the
/// migration notes at the head of this file.
/// </para>
/// <para>
/// <b>Lifetime and state.</b> Registered as a singleton, which is safe because every field is
/// readonly and nothing is cached per caller. The bound policy is captured once from
/// <c>IOptions&lt;PasswordPolicyOptions&gt;</c> in the constructor - the non-monitoring options
/// interface already resolves its value a single time for the life of the container, so holding the
/// value rather than the wrapper changes no behaviour and makes the immutability visible. No scoped
/// service is injected, and none may be: doing so would capture a per-request dependency inside a
/// process-lifetime object.
/// </para>
/// <para>
/// <b>Where the work factor lives.</b> The cost is a single private constant on this type,
/// <c>WorkFactor</c>, deliberately not a property of the bound policy. A password policy states
/// what a user must supply; the cost of the digest is an implementation parameter of the algorithm
/// this class happens to use, and surfacing it as configuration would invite it to be lowered from
/// outside the code. <see cref="Hash(string)"/> and <see cref="NeedsRehash(string)"/> read the same
/// constant, which is what makes raising it a complete upgrade rather than a half one: every digest
/// produced below the new value immediately begins reporting that it needs replacing.
/// </para>
/// <para>
/// <b>Salting.</b> BCrypt generates a fresh salt for every call and embeds it in the returned value,
/// so hashing one password twice yields two different results. Nothing is added around that - no
/// separate salt column, no additional secret, no wrapper and no re-encoding - because every such
/// addition would be a second thing to keep, protect and get right.
/// </para>
/// <para>
/// <b>Synchronous by design.</b> All three operations are pure computation and touch no socket, file
/// or database, so there is nothing to await and no cancellation to observe. The project-wide rule
/// that I/O-bound work be awaited does not reach computation, and these members must not be
/// reshaped into an awaitable form or dispatched to a background thread from here; a caller that
/// needs to keep a thread free owns that decision.
/// </para>
/// <para>
/// <b>Nothing is logged.</b> No logger is injected. A password must never be written to a log, and a
/// stored digest is a credential-equivalent that offers an offline attacker a target, so neither is
/// recorded. Exception messages raised below quote policy numbers only and never the value supplied.
/// </para>
/// <para>
/// <b>Input length is bounded, and the bound is shared rather than restated.</b> Both
/// <see cref="Hash(string)"/> and <see cref="Verify(string, string)"/> refuse a credential longer
/// than <see cref="CredentialBounds.MaximumByteLength"/> UTF-8 bytes. That constant is the same one
/// every request validator applies, so a credential that reaches this class has already been bounded
/// once; the check here is defence in depth for any future caller that reaches the hasher without
/// passing through a validator. Enforcing it explicitly matters because neither hashing entry point
/// of the underlying package refuses an over-long input of its own accord - a four-hundred-character
/// password was confirmed to hash without complaint - so silence is not a bound.
/// </para>
/// </remarks>
internal sealed class BcryptPasswordHasher : IPasswordHasher
{
    /// <summary>
    /// The BCrypt cost, as the base-2 logarithm of the number of rounds applied, so the work
    /// performed grows as 2^<c>WorkFactor</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Twelve is one step above the 11 the hashing package uses when no cost is supplied, and it is
    /// stated explicitly rather than left to that default so a package upgrade cannot silently move
    /// it in either direction. It is the sole cost in this type: hashing and replacement detection
    /// share it, so the two can never disagree about what "current" means.
    /// </para>
    /// <para>
    /// It is bounded by being a <see langword="const"/>. The algorithm accepts a cost between 4 and
    /// 31, and a value at the bottom of that range would make an offline attack cheap while a value
    /// at the top would make a single sign-in take minutes; twelve sits deliberately in the middle
    /// and cannot be moved by configuration, an environment variable or a request. This is the same
    /// reasoning that keeps the shared credential ceiling a constant on
    /// <see cref="CredentialBounds"/>: a security parameter a deployment can weaken is not a
    /// parameter, it is a suggestion.
    /// </para>
    /// </remarks>
    private const int WorkFactor = 12;

    /// <summary>
    /// The pre-hash algorithm used by the enhanced hashing pair, stated explicitly on every call.
    /// </summary>
    /// <remarks>
    /// SHA-384 is also the package's own default for both halves of the pair, so naming it changes
    /// no behaviour today. It is named anyway for one reason: an enhanced digest records nothing
    /// about which pre-hash produced it, so if a future package release moved that default the
    /// hashing and verification halves would begin disagreeing silently and every existing digest
    /// would stop verifying. Stating the algorithm on both sides makes that failure impossible.
    /// </remarks>
    private const HashType PreHashAlgorithm = HashType.SHA384;

    /// <summary>
    /// The password policy in force, captured once at construction.
    /// </summary>
    private readonly PasswordPolicyOptions _passwordPolicy;

    /// <summary>
    /// The decoy representation served by <see cref="UnmatchableHash"/>, computed at most once for the
    /// lifetime of this instance and only if something asks for it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// LAZY, AND THREAD-SAFE BY THE MODE THAT IS CHOSEN. Computing it costs one full hashing operation -
    /// the same deliberately expensive operation the cost factor governs - so it must be paid once and
    /// never per attempt. <see cref="LazyThreadSafetyMode.ExecutionAndPublication"/> is named explicitly
    /// rather than left to the default because this type is registered as a singleton and is therefore
    /// reached concurrently: the default for this constructor overload is already the safe mode, and stating
    /// it keeps that guarantee from resting on a default nobody can see at the call site.
    /// </para>
    /// <para>
    /// THE INPUT IS RANDOM AND IS NOT RETAINED. Thirty-two cryptographically random bytes are hashed and the
    /// bytes are then dropped, so no value exists anywhere - in this process, in configuration, or in the
    /// source - that verifies against the result. A hard-coded decoy would be the opposite: a constant in a
    /// public repository, computed at whatever cost factor applied on the day it was written.
    /// </para>
    /// </remarks>
    private readonly Lazy<string> _unmatchableHash = new(
        CreateUnmatchableHash,
        LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>
    /// Initialises a new instance of the <see cref="BcryptPasswordHasher"/> class.
    /// </summary>
    /// <param name="passwordPolicyOptions">
    /// The bound password policy. Supplied by the container as configuration; this type never reads
    /// a configuration key, an environment variable or any other ambient source directly.
    /// </param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="passwordPolicyOptions"/> is <see langword="null"/>.
    /// </exception>
    public BcryptPasswordHasher(IOptions<PasswordPolicyOptions> passwordPolicyOptions)
    {
        ArgumentNullException.ThrowIfNull(passwordPolicyOptions);

        _passwordPolicy = passwordPolicyOptions.Value;
    }

    /// <summary>
    /// Turns a plaintext password into the one-way representation stored against the account.
    /// </summary>
    /// <param name="password">
    /// The plaintext password to hash. It is hashed exactly as supplied: nothing is trimmed,
    /// case-folded, Unicode-normalised or otherwise re-encoded, because any such change would alter
    /// the bytes fed to the algorithm and make an already-stored digest unverifiable.
    /// </param>
    /// <returns>
    /// The stored representation of <paramref name="password"/>, ready to be persisted. Two calls
    /// with the same input return different values, so a stored representation must never be
    /// compared for equality; use <see cref="Verify(string, string)"/> instead.
    /// </returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="password"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="password"/> is empty, consists only of white space, or fails the bound
    /// policy. The message names the policy requirement and never quotes the value supplied.
    /// </exception>
    public string Hash(string password)
    {
        // MIGRATION: one narrow, deliberate divergence, recorded rather than absorbed. The legacy
        // check measured length alone, so a password consisting entirely of white space was
        // acceptable to it once long enough; here such a value is rejected as absent, because the
        // contract this type implements states that it is and because a wholly blank credential is
        // indistinguishable from a missing one at every boundary above. The reach is bounded to an
        // account whose password is nothing but white space, and it cannot lock anyone out of an
        // existing account: Verify applies no policy at all, so such a credential still verifies,
        // and this guard only refuses to mint a new stored value from it. This is a guard on the
        // shape of the argument, not a policy rule - it inspects and never alters, so white space
        // WITHIN a password remains significant and is carried through untouched.
        ArgumentException.ThrowIfNullOrWhiteSpace(password);

        // MIGRATION: these two guards mirror the legacy check at
        // Library/Components/Users/UserController.vb:L1067-L1091 and nothing more. They are a
        // backstop for a caller that reached this type without passing through the Application
        // layer's validators, which remain the primary policy boundary and own the user-facing
        // wording. The legacy third rule, a strength pattern, is deliberately absent here: it was
        // configured in neither Website/release.config nor Website/development.config, so it never
        // fired, and applying it now would tighten the policy mid-migration.
        int minimumLength = _passwordPolicy.MinRequiredPasswordLength;
        if (password.Length < minimumLength)
        {
            throw new ArgumentException(
                $"A password must be at least {minimumLength} characters long.",
                nameof(password));
        }

        int minimumNonAlphanumericCharacters = _passwordPolicy.MinRequiredNonAlphanumericCharacters;

        // A minimum of zero is not a rule: a count can never fall below zero, so the comparison
        // could only ever succeed. It is skipped rather than evaluated, so the shipped
        // configuration performs no pointless scan of every character.
        if (minimumNonAlphanumericCharacters > 0
            && CountNonAlphanumericCharacters(password) < minimumNonAlphanumericCharacters)
        {
            throw new ArgumentException(
                $"A password must contain at least {minimumNonAlphanumericCharacters} character(s) "
                + "outside the ranges 0-9, A-Z and a-z.",
                nameof(password));
        }

        // The shared upper bound, applied explicitly. The value and the unit both come from
        // CredentialBounds so that this guard and every request validator enforce one number, and it
        // is checked here rather than assumed because the hashing call below accepts an input of any
        // length without complaint. Throwing is correct on this path: minting a stored credential is
        // a deliberate act by trusted code, and a value that reached it unbounded is a defect in the
        // caller rather than a bad submission. The message quotes the bound and never the value.
        if (!CredentialBounds.IsWithinMaximumByteLength(password))
        {
            throw new ArgumentException(
                "A password must be no longer than "
                + $"{CredentialBounds.MaximumByteLength} bytes when encoded as UTF-8.",
                nameof(password));
        }

        // The enhanced half of the pair. It digests the credential with SHA-384 before hashing, so
        // every byte of the input contributes and the algorithm's 72-byte significance limit cannot
        // make two distinct passwords equivalent. Verify below MUST use the matching enhanced half:
        // the returned value carries no marker recording which half produced it, so a mismatched
        // pair fails silently rather than loudly.
        return BCrypt.Net.BCrypt.EnhancedHashPassword(password, WorkFactor, PreHashAlgorithm);
    }

    /// <summary>
    /// Determines whether a candidate plaintext password matches a stored one-way representation.
    /// </summary>
    /// <param name="password">
    /// The candidate plaintext password to check, used exactly as supplied for the reason given on
    /// <see cref="Hash(string)"/>.
    /// </param>
    /// <param name="passwordHash">
    /// The stored representation to check against, as produced by <see cref="Hash(string)"/>.
    /// </param>
    /// <returns>
    /// <see langword="true"/> when <paramref name="password"/> matches
    /// <paramref name="passwordHash"/>; otherwise <see langword="false"/>. A mismatch, a candidate
    /// exceeding <see cref="CredentialBounds.MaximumByteLength"/>, and a stored representation this
    /// implementation cannot parse are all reported as <see langword="false"/>.
    /// </returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="password"/> or <paramref name="passwordHash"/> is <see langword="null"/>.
    /// </exception>
    public bool Verify(string password, string passwordHash)
    {
        // Null is a caller defect and is surfaced as one. Beyond that the arguments are taken as
        // they are: the bound policy is NOT applied here, so a credential accepted under an earlier
        // policy stays verifiable after the policy changes. Re-checking policy on the way in would
        // lock out exactly the accounts this migration exists to carry forward.
        ArgumentNullException.ThrowIfNull(password);
        ArgumentNullException.ThrowIfNull(passwordHash);

        // The shared upper bound again, and the ONLY bound applied on this path. It is not policy: no
        // stored credential can exceed it, because Hash refuses to mint one that does, so refusing an
        // over-long candidate cannot lock anyone out of an existing account. What it does prevent is
        // an unauthenticated caller handing an arbitrarily large value to a deliberately expensive
        // function on every request.
        //
        // A refusal is reported as "does not match" rather than thrown, deliberately. This member
        // answers a sign-in attempt, so an over-long candidate is a failed attempt, not a fault; a
        // thrown exception here would turn a submission into a server error and would also let a
        // caller distinguish "too long" from "wrong", which is a difference an attacker has no
        // business being able to observe.
        if (!CredentialBounds.IsWithinMaximumByteLength(password))
        {
            return false;
        }

        // A stored value arrives from a database row that may predate this implementation entirely,
        // so every way the hashing package can reject one is answered with "does not match" rather
        // than allowed to surface as a fault. That neither admits an arbitrary password nor turns a
        // damaged row into a server error. The set below is exhaustive for the pinned package
        // version and was established by exercising it against truncated, mutated and randomised
        // stored values, not by reading its documentation alone - the documentation names only the
        // first two:
        //   SaltParseException        the version or revision marker is not one this scheme knows;
        //   ArgumentException         the value is absent-shaped, plus its out-of-range subtype for
        //                             a value too short to carry a complete salt;
        //   FormatException           a cost is present in the value but is not a number;
        //   IndexOutOfRangeException  the value is shorter than the fixed offsets the parser reads.
        // Nothing wider is caught. This method indexes nothing, parses nothing and allocates
        // nothing, so each of these can only have arisen from parsing the supplied stored value; any
        // other fault is genuine and still propagates to the caller.
        //
        // The enhanced half of the pair, matching Hash. This pairing is load-bearing rather than
        // stylistic: an enhanced digest is an ordinary BCrypt digest of the SHA-384 pre-hash and
        // records nothing that identifies it as one, so verifying an enhanced digest with the plain
        // entry point - or a plain digest with this one - returns false for every credential,
        // confirmed in both directions against the pinned package. Neither entry point may be changed
        // without changing the other in the same edit.
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
    /// Reports whether a stored one-way representation should be regenerated the next time the
    /// owning credential is successfully presented.
    /// </summary>
    /// <param name="passwordHash">The stored representation to examine.</param>
    /// <returns>
    /// <see langword="true"/> only when <paramref name="passwordHash"/> is a digest this
    /// implementation produced and carries a lower cost than <c>WorkFactor</c>; otherwise
    /// <see langword="false"/>, including for any value it cannot parse.
    /// </returns>
    /// <remarks>
    /// <para>
    /// This member exists to support ONE upgrade: raising the cost of a digest
    /// <see cref="Hash(string)"/> produced. It is not a legacy-credential detector, and a
    /// <see langword="true"/> answer must never be read as an invitation to accept a credential that
    /// <see cref="Verify(string, string)"/> rejected. Legacy credentials are migrated by
    /// administrative reset only, for the reasons set out at the head of this file.
    /// </para>
    /// <para>
    /// An unparseable value therefore answers <see langword="false"/>, which is a CHANGE from an
    /// earlier revision that answered <see langword="true"/> for one. See the annotation on the
    /// handlers below for why the earlier answer was the wrong one to give.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="passwordHash"/> is <see langword="null"/>.
    /// </exception>
    public bool NeedsRehash(string passwordHash)
    {
        ArgumentNullException.ThrowIfNull(passwordHash);

        // MIGRATION: AN UNPARSEABLE STORED VALUE ANSWERS "NO", AND THAT IS A DELIBERATE CHANGE. An
        // earlier revision answered "yes, replace it" and defended the answer on reachability: a caller
        // arrives here only after Verify has succeeded, Verify rejects exactly the values that land in
        // these handlers, so no legacy row could be accompanied by an accepted credential and the
        // answer was unreachable in practice. The reachability argument is correct and is not the
        // problem. The ANSWER was, for two reasons.
        //
        // First, it made this member say something untrue about a legacy row. "Replace it" promises an
        // upgrade that cannot be performed: replacing a stored value requires the plaintext, the
        // plaintext arrives only with a successful verification, and a value held under the legacy
        // reversible scheme can never be verified here. Depending on nobody ever consulting a public
        // member outside its one intended pairing is not a property of the code - it is a property of
        // future authors' discipline - and this contract is declared on a domain abstraction that any
        // caller may reach.
        //
        // Second, and this is what a security review of this file found, that untrue answer was the
        // same conflation that had spread through eight contracts and comments in prose: that a legacy
        // credential is upgraded lazily rather than reset administratively. Correcting the prose while
        // leaving the code asserting the same thing would have left the misleading claim in the one
        // place a reader trusts most.
        //
        // Answering "no" costs nothing operationally. An account whose stored value cannot be parsed
        // cannot sign in either way - Verify rejects it - so it requires an administrative reset
        // whatever this member says, and saying "nothing to upgrade" is simply the truthful
        // description of a value this scheme did not produce.
        //
        // The handled set deliberately mirrors Verify's AND now agrees with it on the answer, so the
        // two members can never disagree about which stored values this implementation understands:
        // a value this scheme did not produce is neither verifiable nor upgradable. The pinned package
        // funnels every malformed value here into the first of them, and the remaining four are the
        // types it documents or was observed to raise on the closely related parse path; catching them
        // keeps the answer deterministic if an edge case ever reaches a different branch. Nothing wider
        // is caught, for the reason given in Verify.
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
    /// <remarks>
    /// Served from the lazily computed field above, so the cost is paid once for the lifetime of this
    /// singleton and every subsequent read is a field access. <see cref="Verify(string, string)"/> treats the
    /// value exactly as it treats a value read from the database - there is no branch for it and no way to
    /// ask whether a supplied representation is this one - which is what makes an attempt against it
    /// indistinguishable, in both cost and code path, from an attempt against a real account.
    /// </remarks>
    public string UnmatchableHash => _unmatchableHash.Value;

    /// <summary>
    /// Produces the decoy representation, at the current work factor, from cryptographically random input
    /// that is not retained.
    /// </summary>
    /// <returns>A well-formed stored representation that no credential matches.</returns>
    /// <remarks>
    /// <para>
    /// The input is Base64 of thirty-two random bytes, which lands comfortably inside the credential length
    /// bound and inside the range the enhanced pre-hash accepts. It is produced by the cryptographic
    /// generator rather than a pseudo-random one - not because a decoy needs unpredictability against an
    /// attacker who never sees it, but because a seeded generator could in principle repeat a value across
    /// processes, and a decoy that is reproducible is a decoy that could be matched.
    /// </para>
    /// <para>
    /// It goes through the same enhanced entry point and the same work factor as
    /// <see cref="Hash(string)"/>, deliberately reusing them rather than re-stating them: were the factor or
    /// the pre-hash algorithm ever changed, the decoy would move with them in the same edit, and a decoy
    /// whose cost has drifted from a real comparison's cost has stopped serving its only purpose.
    /// </para>
    /// </remarks>
    private static string CreateUnmatchableHash()
    {
        string unmatchable = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

        return BCrypt.Net.BCrypt.EnhancedHashPassword(unmatchable, WorkFactor, PreHashAlgorithm);
    }

    /// <summary>
    /// Counts the characters in <paramref name="password"/> that fall outside the ASCII
    /// alphanumeric set.
    /// </summary>
    /// <param name="password">The password to scan.</param>
    /// <returns>The number of characters that are not an ASCII digit or an ASCII letter.</returns>
    private static int CountNonAlphanumericCharacters(string password)
    {
        // MIGRATION: this reproduces the legacy character class [^0-9a-zA-Z] from
        // Library/Components/Users/UserController.vb:L1078 exactly. The framework's culture-aware
        // letter-or-digit test is deliberately not used: it is Unicode-aware, so an accented letter
        // would count as alphanumeric and stop contributing to this total, silently widening a rule
        // the migration is required to preserve. The ASCII-restricted test below covers precisely
        // the set the legacy pattern negated, and the scan runs over UTF-16 units just as the legacy
        // length check did, so both measures agree with their originals.
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
