// MIGRATION: FormsAuthentication.SignOut has no stateless counterpart, so logout now means
// MIGRATION: access-token expiry plus a client-side discard, backed by server-side revocation of
// MIGRATION: the presented refresh token's whole family — which is what this file provides.
//
// MIGRATION: The measured legacy site is Library/Components/Security/PortalSecurity.vb:L77-L95.
// MIGRATION: Its SignOut calls System.Web.Security.FormsAuthentication.SignOut() at L79, blanks
// MIGRATION: the "language" and "authentication" cookies at L82 and L85, and back-dates the
// MIGRATION: "portalaliasid" and "portalroles" cookies by thirty years at L88-L94. Read that list
// MIGRATION: again and note what is absent from it: every statement targets the response, so the
// MIGRATION: legacy sign-off invalidated NOTHING on the server. A ticket already copied from the
// MIGRATION: browser stayed valid for the whole of its remaining window, because the only thing
// MIGRATION: that sign-off could reach was the copy it asked the browser to drop. Family
// MIGRATION: revocation below is therefore net-new strengthening rather than a translation, and
// MIGRATION: nothing in the legacy tree was removed to make room for it.
//
// MIGRATION: The credential this replaces is the forms ticket established at
// MIGRATION: Library/Components/Users/UserController.vb:L1024-L1055 — SetAuthCookie at L1033,
// MIGRATION: with an optional long-lived variant at L1036-L1052 driven by the
// MIGRATION: PersistentCookieTimeout setting at Website/release.config:L51. That ticket was a
// MIGRATION: bearer credential with no rotation and no server-side record, so a single capture
// MIGRATION: yielded unlimited reuse until it elapsed. Single-use rotation with replay detection
// MIGRATION: is the replacement.
//
// MIGRATION: DIVERGENCE, reversible protection to a one-way digest. The legacy ticket was
// MIGRATION: protected by the machineKey element at Website/release.config:L89-L93 — Triple-DES,
// MIGRATION: whose decryption key is a literal committed to source control at L91 — so anything
// MIGRATION: holding both the stored material and that file could recover a usable ticket. This
// MIGRATION: store keeps only a one-way digest of each token, never the token, so a dump of its
// MIGRATION: state cannot be turned back into a credential. The same file's Encrypt and Decrypt
// MIGRATION: pair (PortalSecurity.vb:L138 and L175, CreateEncryptor at L161 and CreateDecryptor
// MIGRATION: at L209) is the reversible idiom being retired; no equivalent appears here, by
// MIGRATION: design.
//
// MIGRATION: The secure-generation lineage is genuine and worth naming: PortalSecurity.CreateKey
// MIGRATION: at L564-L571 already drew from a cryptographic generator and rendered the bytes as
// MIGRATION: hex through BytesToHexString at L585-L594. That intent is preserved. Only the
// MIGRATION: mechanism moves on — the obsolete generator type gives way to the modern static
// MIGRATION: factory, and hex gives way to unpadded URL-safe Base64, which carries the same
// MIGRATION: entropy in fewer characters and travels safely in a header, a body or a query
// MIGRATION: string.
//
// MIGRATION: There is no legacy refresh mechanism to port. DotNetNuke 4.9.0 renewed access by
// MIGRATION: sliding the forms ticket configured at Website/release.config:L146-L147, so the
// MIGRATION: browser never held a token, never learned when its own window closed and never
// MIGRATION: called a renewal endpoint. Every rotation, replay and revocation rule below is new
// MIGRATION: behaviour introduced by this migration, documented as such, and traceable to no
// MIGRATION: predecessor.
//
// MIGRATION: The ByRef status channel is gone. Library/Components/Users/UserController.vb:L1110
// MIGRATION: and L1132 reported their result by mutating a caller-supplied status argument. Every
// MIGRATION: operation here reports its result through an immutable return value instead, so no
// MIGRATION: member below takes a by-reference argument of any kind.

using System.Security.Cryptography;
using System.Text;

using DnnMigration.Application.Options;
using DnnMigration.Domain.Abstractions.Services;

using Microsoft.Extensions.Options;

namespace DnnMigration.Infrastructure.Security;

/// <summary>
/// Holds the server-side state that lets a refresh token be redeemed exactly once, hands back a
/// replacement on each redemption, detects the replay of a token that was already redeemed, and
/// revokes a whole token family on logout.
/// </summary>
/// <remarks>
/// <para>
/// Collaborator, not a service. This type is deliberately <see langword="internal"/>: it is the
/// private bookkeeping of <c>Infrastructure/Security/JwtTokenService.cs</c> and appears on no
/// application-layer contract. Nothing declared in this file — not the outcome vocabulary, not
/// the subject snapshot, not the result types — may be surfaced on <c>ITokenService</c>, written
/// into a wire DTO or minted into a token claim. Family identifiers, generation counters and
/// token digests in particular are implementation detail that a caller can neither read nor
/// supply.
/// </para>
/// <para>
/// Lifetime and injected dependencies. Registered as a singleton, because the state below must
/// outlive the request that created it: a token issued during one request is redeemed during a
/// later one. That lifetime is why the two constructor parameters are the only ones permitted —
/// both are themselves singletons. A per-request dependency captured here would be held past the
/// end of its own scope, so this type takes no persistence context, no repository, no cache, no
/// request-context accessor and no logging dependency, and it resolves nothing dynamically.
/// Because it holds no logging dependency it cannot log, which is the point: a store of
/// credentials is the last place a stray diagnostic line should be able to appear.
/// </para>
/// <para>
/// What is stored, and what is deliberately not. Each entry keeps a one-way digest of the token,
/// an immutable snapshot of the non-secret facts needed to re-mint an access token, an absolute
/// expiry instant, a family identifier, a generation number and two lifecycle flags. The token
/// itself is never stored, so it exists only as the return value handed to the caller once. No
/// access token, no credential and no signing material is read, derived or retained here — the
/// only setting this type reads from its options is the refresh lifetime.
/// </para>
/// <para>
/// Synchronisation. Every read and every mutation happens inside a single <c>lock</c> over one
/// private gate object, guarding two ordinary dictionaries. That is a deliberate choice over a
/// lock-free collection: family rotation has to re-check a token's state and then, only if that
/// check passed, consume it and insert its replacement, and a sequence of individually atomic
/// operations gives no guarantee across the sequence. With one gate the whole transition is
/// atomic by construction and can be verified by reading it, which matters more here than shaving
/// a lock acquisition. Critical sections contain no I/O and no callback, so they are bounded and
/// cannot deadlock.
/// </para>
/// <para>
/// Synchronous by design. Every operation is a bounded in-memory state transition that performs
/// no I/O, so the solution-wide rule that I/O-bound members must be awaitable does not apply, and
/// nothing here is dispatched to a thread pool. The awaitable public contract, together with
/// cancellation, belongs to <c>JwtTokenService</c>.
/// </para>
/// <para>
/// Durability and retention. State lives in the process, so restarting the API invalidates every
/// outstanding refresh token and every caller simply signs in again; access tokens already issued
/// remain valid until they elapse, exactly as they would anyway. Entries are never evicted, and
/// that is a security requirement rather than an omission: discarding a redeemed entry would make
/// a later replay of it indistinguishable from an unknown token, silently disabling the detection
/// that protects the rest of its family. Retention is therefore bounded by process lifetime, and
/// each entry is a few hundred bytes. A deployment that needs revocation to survive a restart, or
/// to be shared across replicas, replaces this type with a durable implementation of the same
/// four operations; that is a deliberate follow-on, and no cleanup timer or background worker is
/// introduced here to approximate it.
/// </para>
/// <para>
/// Value-neutral failures. Every rejection reports one of a small, fixed set of outcomes and
/// nothing else. No message names a user, a portal, a family or another token, and no failure
/// path reveals whether two pieces of presented material are related. Material that cannot be one
/// of this store's tokens is refused without being hashed at all.
/// </para>
/// </remarks>
internal sealed class RefreshTokenStore
{
    /// <summary>
    /// Number of random bytes behind every refresh token: 32 bytes, that is 256 bits of entropy.
    /// </summary>
    /// <remarks>
    /// Two hundred and fifty-six bits is the floor, not a target to be trimmed. It also matches
    /// the digest width used below, so neither step is the weaker of the two. Guessing a token is
    /// therefore not a viable attack, which is what allows the lookup below to be a plain
    /// dictionary probe.
    /// </remarks>
    private const int TokenByteLength = 32;

    /// <summary>
    /// Character length of the unpadded URL-safe Base64 encoding of <see cref="TokenByteLength"/>
    /// bytes.
    /// </summary>
    /// <remarks>
    /// Derived arithmetic rather than a transcribed constant: Base64 carries six bits per
    /// character, so the unpadded length is the number of bits rounded up to the next whole
    /// character. For 32 bytes that evaluates to 43. Keeping it derived means changing
    /// <see cref="TokenByteLength"/> cannot leave a stale length behind.
    /// </remarks>
    private const int TokenCharacterLength = ((TokenByteLength * 8) + 5) / 6;

    /// <summary>
    /// Generation number carried by the first token of a family.
    /// </summary>
    private const int FirstGeneration = 0;

    /// <summary>
    /// Largest configured refresh lifetime, in days, that can be represented as an interval.
    /// </summary>
    /// <remarks>
    /// Taken from the interval type's own limit rather than chosen, so it states a
    /// representational boundary and not an invented policy. Its only purpose is to make the
    /// constructor refuse an unrepresentable setting immediately, instead of letting the
    /// conversion fault later at a point where the cause would be far less obvious. Deployments
    /// choose their own lifetime well inside this bound.
    /// </remarks>
    private static readonly int MaximumLifetimeDays = TimeSpan.MaxValue.Days;

    /// <summary>
    /// The single gate guarding <see cref="_recordsByDigest"/>, <see cref="_digestsByFamily"/>
    /// and <see cref="_lastFamilyId"/>, and guarding the lifecycle flags of every stored entry.
    /// </summary>
    /// <remarks>
    /// One gate for all mutable state, held for the whole of each operation. Two gates, or a
    /// lock-free collection, would permit a rotation to interleave with a revocation of the same
    /// family and leave the family in a state neither operation intended.
    /// </remarks>
    private readonly object _gate = new();

    /// <summary>
    /// Stored entries keyed by the one-way digest of their token.
    /// </summary>
    /// <remarks>
    /// The key is the digest, so the token itself appears nowhere in this dictionary — neither as
    /// a key nor on a value. Ordinal comparison is used because the key is fixed-case hexadecimal
    /// and its interpretation must not vary with the culture the process happens to be running
    /// under.
    /// </remarks>
    private readonly Dictionary<string, RefreshTokenRecord> _recordsByDigest =
        new(StringComparer.Ordinal);

    /// <summary>
    /// Digests of every generation ever issued in a family, keyed by family identifier.
    /// </summary>
    /// <remarks>
    /// This index is what makes revocation a family-wide operation rather than a single-token
    /// one. Without it, revoking a family would mean scanning every entry in the store; with it,
    /// the cost is proportional to that one family's length. Entries are appended and never
    /// removed, so a replay detected against an early generation still reaches the newest one.
    /// </remarks>
    private readonly Dictionary<long, List<string>> _digestsByFamily = new();

    /// <summary>
    /// Supplies the present instant. The only sanctioned source of time in this type.
    /// </summary>
    private readonly IClock _clock;

    /// <summary>
    /// How long a newly issued or newly rotated refresh token remains redeemable.
    /// </summary>
    /// <remarks>
    /// Copied and validated once at construction rather than read per operation. A singleton that
    /// re-read a reconfigured lifetime mid-flight would issue expiries that could not be compared
    /// with those already handed over, so the value is fixed for the life of the process.
    /// </remarks>
    private readonly TimeSpan _refreshTokenLifetime;

    /// <summary>
    /// Highest family identifier issued so far. Mutated only under <see cref="_gate"/>.
    /// </summary>
    /// <remarks>
    /// A monotonic counter, deliberately not a random or globally unique value. A family
    /// identifier is an internal correlation key that never leaves this type and is never
    /// presented to or accepted from a caller, so it needs no unpredictability — and using
    /// generated entropy for it would blur the line between an identifier and a credential, which
    /// is precisely the confusion this file exists to avoid. Numbering starts at 1, leaving 0 as
    /// a value no family ever has.
    /// </remarks>
    private long _lastFamilyId;

    /// <summary>
    /// Initialises the store, validating and copying the configured refresh lifetime.
    /// </summary>
    /// <param name="clock">Supplies the present instant, in Coordinated Universal Time.</param>
    /// <param name="jwtOptions">
    /// Carries the bound <see cref="JwtOptions"/>. Only
    /// <see cref="JwtOptions.RefreshTokenExpirationDays"/> is read; every other member is ignored
    /// here, so no credential material is touched by this type.
    /// </param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="clock"/> or <paramref name="jwtOptions"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// The configured refresh lifetime is not a positive number of days, or is too large to be
    /// represented as an interval.
    /// </exception>
    /// <remarks>
    /// Failing here fails at startup, while the host can still refuse to serve traffic. A
    /// non-positive lifetime would make every issued token expire at or before the instant it was
    /// created, so a refresh could never succeed and the fault would surface as a puzzling
    /// authentication failure much later. Both messages name the offending setting and its
    /// supplied value, and nothing else: neither reproduces a token or any credential material.
    /// </remarks>
    public RefreshTokenStore(IClock clock, IOptions<JwtOptions> jwtOptions)
    {
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(jwtOptions);

        var lifetimeDays = jwtOptions.Value.RefreshTokenExpirationDays;
        var settingName =
            $"{JwtOptions.SectionName}:{nameof(JwtOptions.RefreshTokenExpirationDays)}";

        if (lifetimeDays <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(jwtOptions),
                lifetimeDays,
                $"{settingName} must be a positive number of days.");
        }

        if (lifetimeDays > MaximumLifetimeDays)
        {
            throw new ArgumentOutOfRangeException(
                nameof(jwtOptions),
                lifetimeDays,
                $"{settingName} must not exceed {MaximumLifetimeDays} days.");
        }

        _clock = clock;
        _refreshTokenLifetime = TimeSpan.FromDays(lifetimeDays);
    }

    /// <summary>
    /// Issues the first refresh token of a new family, for a caller who has just authenticated.
    /// </summary>
    /// <param name="subject">
    /// The non-secret facts to be re-minted into an access token when this token, or any later
    /// generation of its family, is redeemed.
    /// </param>
    /// <returns>
    /// The raw token — handed over exactly once and never stored — together with its absolute
    /// expiry and the snapshot that was recorded against it.
    /// </returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="subject"/> is <see langword="null"/>.
    /// </exception>
    /// <remarks>
    /// A rejected sign-in must never reach this method; issuing a token is the consequence of a
    /// verified credential, and verifying one is not this type's concern. The present instant is
    /// read once and used for the whole operation, so the expiry recorded here is measured from a
    /// single point in time rather than from two readings that could straddle a boundary.
    /// </remarks>
    public RefreshTokenIssueResult Issue(RefreshTokenSubject subject)
    {
        ArgumentNullException.ThrowIfNull(subject);

        var issuedAtUtc = _clock.UtcNow;
        var expiresAtUtc = AddLifetime(issuedAtUtc);

        lock (_gate)
        {
            // Numbering under the gate is what makes the identifier unique; a counter incremented
            // anywhere else could hand the same family to two concurrent sign-ins.
            var familyId = ++_lastFamilyId;
            var rawToken = CreateAndStore(familyId, FirstGeneration, expiresAtUtc, subject);

            return new RefreshTokenIssueResult(rawToken, expiresAtUtc, subject);
        }
    }

    /// <summary>
    /// Reports the current state of presented material and, when it is redeemable, the immutable
    /// snapshot recorded against it. Changes nothing.
    /// </summary>
    /// <param name="refreshToken">The refresh token a caller has presented.</param>
    /// <returns>
    /// An outcome distinguishing unknown, revoked, already-redeemed and expired material from
    /// material that is currently redeemable, carrying the snapshot and expiry only in that last
    /// case.
    /// </returns>
    /// <remarks>
    /// <para>
    /// This exists so a caller can read the recorded subject <em>before</em> doing any awaitable
    /// work of its own — re-checking that the user still holds the roles the snapshot claims, for
    /// instance — and then supply an updated snapshot to <see cref="Rotate"/>.
    /// </para>
    /// <para>
    /// A successful reading authorises nothing. It is a point-in-time observation, and by the
    /// moment the caller acts on it the token may already have been redeemed or revoked by a
    /// concurrent request. <see cref="Rotate"/> therefore repeats every one of these checks
    /// inside the same lock that performs the rotation, and it is that re-check — never this one
    /// — that decides whether a replacement is issued.
    /// </para>
    /// </remarks>
    public RefreshTokenInspection Inspect(string refreshToken)
    {
        var digest = ComputeLookupKey(refreshToken);
        if (digest is null)
        {
            return RefreshTokenInspection.Failed(RefreshTokenOutcome.Unknown);
        }

        var asOfUtc = _clock.UtcNow;

        lock (_gate)
        {
            var stored = _recordsByDigest.GetValueOrDefault(digest);
            var outcome = Classify(stored, asOfUtc);

            return stored is not null && outcome == RefreshTokenOutcome.Succeeded
                ? RefreshTokenInspection.Succeeded(stored.Subject, stored.ExpiresAtUtc)
                : RefreshTokenInspection.Failed(outcome);
        }
    }

    /// <summary>
    /// Redeems presented material exactly once, consuming it and issuing a single replacement in
    /// the same family; or, if it was already redeemed, revokes the entire family as a replay.
    /// </summary>
    /// <param name="refreshToken">The refresh token a caller has presented.</param>
    /// <param name="subject">
    /// The snapshot to record against the replacement. Supplying it here — rather than copying
    /// the snapshot forward — is what lets a caller refresh the roles and permission keys it will
    /// mint into the next access token.
    /// </param>
    /// <returns>
    /// On success, the replacement token with its own absolute expiry and the supplied snapshot;
    /// otherwise the outcome that refused the redemption, and no token.
    /// </returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="subject"/> is <see langword="null"/>.
    /// </exception>
    /// <remarks>
    /// <para>
    /// One lock covers the whole transition: the re-check, the consumption of the presented token
    /// and the insertion of its replacement. Two callers racing to redeem the same token
    /// therefore produce exactly one success — the loser finds the token already consumed and is
    /// treated as a replay — so a family can never acquire two live children from one redemption.
    /// </para>
    /// <para>
    /// The replacement receives a fresh absolute expiry measured from this redemption, not the
    /// remaining window of the token it replaces. A family is consequently able to outlive the
    /// configured lifetime while it stays continuously in use, which is the intended behaviour of
    /// rotation: the bound limits how long a token may sit unused, and revocation — not the bound
    /// — is what ends an active session.
    /// </para>
    /// <para>
    /// Because each generation carries its own expiry, an early generation can be past its expiry
    /// while a later one is still live. Replaying such a token is still a genuine replay against
    /// a live child, which is exactly why the classification below settles the already-redeemed
    /// case before the expired one.
    /// </para>
    /// </remarks>
    public RefreshTokenRotationResult Rotate(string refreshToken, RefreshTokenSubject subject)
    {
        ArgumentNullException.ThrowIfNull(subject);

        var digest = ComputeLookupKey(refreshToken);
        if (digest is null)
        {
            return RefreshTokenRotationResult.Failed(RefreshTokenOutcome.Unknown);
        }

        var asOfUtc = _clock.UtcNow;

        lock (_gate)
        {
            var stored = _recordsByDigest.GetValueOrDefault(digest);
            var outcome = Classify(stored, asOfUtc);

            if (stored is null || outcome != RefreshTokenOutcome.Succeeded)
            {
                // Replay. Material that was already redeemed can only have been presented
                // again by something holding a copy of it, so the family is treated as
                // compromised: every generation is revoked, including the live replacement the
                // legitimate caller is holding. Ending both sessions is the correct trade — the
                // alternative is leaving an attacker with a working credential.
                if (stored is not null && outcome == RefreshTokenOutcome.AlreadyUsed)
                {
                    RevokeFamily(stored.FamilyId);
                }

                return RefreshTokenRotationResult.Failed(outcome);
            }

            stored.MarkConsumed();

            var expiresAtUtc = AddLifetime(asOfUtc);
            var replacement =
                CreateAndStore(stored.FamilyId, stored.Generation + 1, expiresAtUtc, subject);

            // The replacement's entry is already in place by this point, so the token is never
            // returned to a caller before it can be redeemed.
            return RefreshTokenRotationResult.Succeeded(replacement, expiresAtUtc, subject);
        }
    }

    /// <summary>
    /// Revokes every generation in the family of the presented material, ending the session it
    /// belongs to.
    /// </summary>
    /// <param name="refreshToken">The refresh token a caller has presented.</param>
    /// <returns>
    /// <see cref="RefreshTokenOutcome.Succeeded"/> when at least one generation was revoked by
    /// this call, <see cref="RefreshTokenOutcome.AlreadyRevoked"/> when the family was already
    /// fully revoked, and <see cref="RefreshTokenOutcome.Unknown"/> when the material is not
    /// recognised.
    /// </returns>
    /// <remarks>
    /// <para>
    /// The whole family is revoked, never a single generation. Revoking only the presented token
    /// would leave the replacement handed back at the previous redemption fully usable, so logout
    /// would not end the session — it would merely skip a generation.
    /// </para>
    /// <para>
    /// The presented token's own state is deliberately not a precondition. An expired or
    /// already-consumed token still identifies its family, and that family may well contain a
    /// live child, so a caller signing off with stale material in hand still gets the session
    /// ended. Repeating the call is harmless: the second attempt finds nothing left to change and
    /// reports that distinctly rather than pretending to have acted. For the same reason this is
    /// the one operation that reads no clock — expiry has no bearing on whether revocation should
    /// proceed.
    /// </para>
    /// <para>
    /// Only refresh state is revoked. There is no register of blocked access tokens here and none
    /// should be added: an access token is validated by its signature and its own expiry, so
    /// consulting server-side state on every request would give up the statelessness that is the
    /// reason for issuing one. That is the trade recorded at the head of this file — a signed-off
    /// caller's access token stays valid until it elapses, and the client discards it.
    /// </para>
    /// </remarks>
    public RefreshTokenOutcome Revoke(string refreshToken)
    {
        var digest = ComputeLookupKey(refreshToken);
        if (digest is null)
        {
            return RefreshTokenOutcome.Unknown;
        }

        lock (_gate)
        {
            var stored = _recordsByDigest.GetValueOrDefault(digest);
            if (stored is null)
            {
                return RefreshTokenOutcome.Unknown;
            }

            return RevokeFamily(stored.FamilyId)
                ? RefreshTokenOutcome.Succeeded
                : RefreshTokenOutcome.AlreadyRevoked;
        }
    }

    /// <summary>
    /// Classifies a stored entry, or its absence, against a captured instant.
    /// </summary>
    /// <param name="stored">The entry found for a digest, or <see langword="null"/>.</param>
    /// <param name="asOfUtc">The instant the surrounding operation captured.</param>
    /// <returns>The single outcome that describes the entry's state.</returns>
    /// <remarks>
    /// <para>
    /// The arms are ordered, and the order carries meaning. An explicit revocation is reported
    /// first, because it is the most specific statement that can be made about an entry and
    /// because reporting it first makes repeated revocation idempotent instead of re-revoking a
    /// family on every subsequent attempt.
    /// </para>
    /// <para>
    /// The already-redeemed arm deliberately precedes the expired one. Since every rotation
    /// stamps a fresh expiry, a family's earliest generation may be long past its expiry while
    /// its newest is still live, so a replay of that earliest generation is a real signal about a
    /// live credential. Testing expiry first would classify it as merely expired and forfeit the
    /// family revocation that the signal calls for. Expired material still never yields a
    /// replacement, so nothing is weakened by the ordering — only the replay detection is
    /// strengthened.
    /// </para>
    /// <para>
    /// Expiry is exclusive of its own instant: an entry whose expiry equals the captured instant
    /// is treated as expired, so the boundary fails closed.
    /// </para>
    /// <para>
    /// Must be called while holding <see cref="_gate"/>: it reads lifecycle flags that a
    /// concurrent operation may be mutating.
    /// </para>
    /// </remarks>
    private static RefreshTokenOutcome Classify(RefreshTokenRecord? stored, DateTime asOfUtc) =>
        stored switch
        {
            null => RefreshTokenOutcome.Unknown,
            { IsRevoked: true } => RefreshTokenOutcome.Revoked,
            { IsConsumed: true } => RefreshTokenOutcome.AlreadyUsed,
            _ when stored.ExpiresAtUtc <= asOfUtc => RefreshTokenOutcome.Expired,
            _ => RefreshTokenOutcome.Succeeded,
        };

    /// <summary>
    /// Revokes every generation of one family.
    /// </summary>
    /// <param name="familyId">The family to revoke.</param>
    /// <returns>
    /// <see langword="true"/> when this call changed at least one generation from unrevoked to
    /// revoked; <see langword="false"/> when the family is unknown or was already fully revoked.
    /// </returns>
    /// <remarks>
    /// Reporting whether anything actually changed is what lets <see cref="Revoke"/> distinguish
    /// a revocation it performed from one that had already happened, without any caller having to
    /// inspect internal state to determine it. Expiry is not consulted: revoking an entry that
    /// has already expired costs nothing and keeps the family's state uniform.
    /// <para>
    /// Must be called while holding <see cref="_gate"/>.
    /// </para>
    /// </remarks>
    private bool RevokeFamily(long familyId)
    {
        var familyDigests = _digestsByFamily.GetValueOrDefault(familyId);
        if (familyDigests is null)
        {
            return false;
        }

        var revokedAny = false;

        foreach (var familyDigest in familyDigests)
        {
            var member = _recordsByDigest.GetValueOrDefault(familyDigest);
            if (member is null || member.IsRevoked)
            {
                continue;
            }

            member.MarkRevoked();
            revokedAny = true;
        }

        return revokedAny;
    }

    /// <summary>
    /// Creates one fresh token, records its digest against a new entry, and returns the raw
    /// token.
    /// </summary>
    /// <param name="familyId">The family the new entry belongs to.</param>
    /// <param name="generation">The new entry's generation number within that family.</param>
    /// <param name="expiresAtUtc">The absolute instant after which the new token is
    /// expired.</param>
    /// <param name="subject">The snapshot recorded against the new entry.</param>
    /// <returns>
    /// The raw token, which the caller returns onward exactly once. It is not retained here.
    /// </returns>
    /// <remarks>
    /// <para>
    /// The loop regenerates on a digest that is already present rather than overwriting the entry
    /// that holds it. Overwriting would silently invalidate an unrelated caller's token, and —
    /// worse — would erase a redeemed entry that replay detection depends on. At 256 bits the
    /// loop is not expected to iterate in the lifetime of any deployment; it exists so that the
    /// impossible case is handled correctly rather than assumed away.
    /// </para>
    /// <para>
    /// Generation is a 64-bit counter, so incrementing it cannot overflow within any physically
    /// realisable number of rotations, and no saturating guard is needed on the caller's
    /// increment.
    /// </para>
    /// <para>
    /// Must be called while holding <see cref="_gate"/>: it both probes and mutates both
    /// dictionaries, and generating the token inside the gate is what makes the probe and the
    /// insertion one indivisible step. Generation and digesting are pure in-memory work measured
    /// in microseconds, so holding the gate across them costs nothing worth reclaiming.
    /// </para>
    /// </remarks>
    private string CreateAndStore(
        long familyId,
        long generation,
        DateTime expiresAtUtc,
        RefreshTokenSubject subject)
    {
        string rawToken;
        string digest;

        do
        {
            rawToken = CreateRawToken();
            digest = ComputeDigest(rawToken);
        }
        while (_recordsByDigest.ContainsKey(digest));

        _recordsByDigest.Add(
            digest,
            new RefreshTokenRecord(familyId, generation, expiresAtUtc, subject));

        var familyDigests = _digestsByFamily.GetValueOrDefault(familyId);
        if (familyDigests is null)
        {
            familyDigests = new List<string>();
            _digestsByFamily[familyId] = familyDigests;
        }

        familyDigests.Add(digest);

        return rawToken;
    }

    /// <summary>
    /// Draws <see cref="TokenByteLength"/> bytes from the platform's cryptographic generator and
    /// renders them as an unpadded, URL-safe Base64 string.
    /// </summary>
    /// <returns>An opaque, transport-safe token of <see cref="TokenCharacterLength"/>
    /// characters.</returns>
    /// <remarks>
    /// <para>
    /// The generator is the platform's cryptographic one. A general-purpose pseudo-random source
    /// would be seeded predictably enough to make tokens guessable, and is never acceptable for
    /// credential material; nor is a globally unique identifier, which encodes version and
    /// variant bits and so carries well below its nominal width in unpredictability.
    /// </para>
    /// <para>
    /// The encoding substitutes the two Base64 characters that require escaping in a URL and
    /// drops the padding, leaving an alphabet of letters, digits, hyphen and underscore. Such a
    /// value survives a header, a JSON body and a query string without further encoding, and it
    /// is opaque: it carries no identifier, no timestamp and no structure a holder could parse or
    /// forge. The transformation uses only base-class-library members, and the entropy buffer is
    /// cleared as soon as it has been encoded so that one copy of the material leaves memory
    /// immediately instead of waiting for collection.
    /// </para>
    /// </remarks>
    private static string CreateRawToken()
    {
        var entropy = RandomNumberGenerator.GetBytes(TokenByteLength);

        try
        {
            return Convert.ToBase64String(entropy)
                .Replace('+', '-')
                .Replace('/', '_')
                .TrimEnd('=');
        }
        finally
        {
            CryptographicOperations.ZeroMemory(entropy);
        }
    }

    /// <summary>
    /// Derives the deterministic one-way digest that a token is stored under.
    /// </summary>
    /// <param name="token">The raw token to digest.</param>
    /// <returns>The digest, rendered as fixed-case hexadecimal.</returns>
    /// <remarks>
    /// SHA-256 over the token's textual bytes, which makes the digest of a presented token
    /// identical to the digest computed when it was issued. Deliberately unsalted and uniterated:
    /// those defences exist to slow down guessing a low-entropy secret, and this input is 256
    /// uniformly random bits, so there is nothing to guess and a per-entry salt would only make
    /// the value unusable as a lookup key. Being one-way is the property that matters — the
    /// digest cannot be turned back into the token, so the stored state is not a credential.
    /// </remarks>
    private static string ComputeDigest(string token) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    /// <summary>
    /// Converts presented material into the key it would be stored under, or reports that it
    /// cannot be one of this store's tokens.
    /// </summary>
    /// <param name="presentedToken">The material a caller presented.</param>
    /// <returns>
    /// The lookup key, or <see langword="null"/> when the material is absent, empty or not the
    /// length this store issues.
    /// </returns>
    /// <remarks>
    /// Absent and empty material are treated identically and refused, matching the contract
    /// stated on the refresh request DTO. The length test is not validation for its own sake:
    /// every token this store issues has exactly one length, so anything else cannot match an
    /// entry, and rejecting it before digesting means an anonymous caller cannot make the process
    /// hash arbitrarily large input. The rejection is value-neutral — every non-matching shape
    /// produces the same unknown outcome, so nothing is revealed about what a real token looks
    /// like beyond its length, which a legitimate holder already knows.
    /// </remarks>
    private static string? ComputeLookupKey(string presentedToken) =>
        string.IsNullOrEmpty(presentedToken) || presentedToken.Length != TokenCharacterLength
            ? null
            : ComputeDigest(presentedToken);

    /// <summary>
    /// Adds the configured refresh lifetime to an instant, saturating rather than overflowing.
    /// </summary>
    /// <param name="instant">The instant to measure from, in Coordinated Universal Time.</param>
    /// <returns>The absolute expiry instant, in Coordinated Universal Time.</returns>
    /// <remarks>
    /// The constructor already refuses a lifetime too large to represent as an interval, so the
    /// only remaining edge is an instant so late that adding the lifetime would leave the
    /// representable range. Saturating at the maximum instant keeps that case from faulting a
    /// request that has done nothing wrong, and a token expiring at the end of representable time
    /// is indistinguishable in practice from one expiring a few days from now. The saturated
    /// value is stamped as Coordinated Universal Time so that every expiry this type records has
    /// the same kind, regardless of which branch produced it.
    /// </remarks>
    private DateTime AddLifetime(DateTime instant)
    {
        var headroom = DateTime.MaxValue - instant;

        return _refreshTokenLifetime < headroom
            ? instant + _refreshTokenLifetime
            : DateTime.SpecifyKind(DateTime.MaxValue, DateTimeKind.Utc);
    }

    /// <summary>
    /// One stored refresh token: its family, its generation, its expiry, the snapshot recorded
    /// against it, and its lifecycle flags.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Nested and private, so an instance cannot be handed to a caller even accidentally: callers
    /// receive the immutable result types declared below this class instead. Notice what the type
    /// does not declare — there is no token field and no digest field. The digest lives only as
    /// the key of the dictionary this entry sits in and as an entry in the family index, and the
    /// token itself is never written down anywhere.
    /// </para>
    /// <para>
    /// Everything identifying is immutable; only the two lifecycle flags change, they change in
    /// one direction only, and they change exclusively under the enclosing store's gate. A flag
    /// that could be cleared would let revoked material become usable again, so neither
    /// transition has an inverse.
    /// </para>
    /// </remarks>
    private sealed class RefreshTokenRecord
    {
        public RefreshTokenRecord(
            long familyId,
            long generation,
            DateTime expiresAtUtc,
            RefreshTokenSubject subject)
        {
            FamilyId = familyId;
            Generation = generation;
            ExpiresAtUtc = expiresAtUtc;
            Subject = subject;
        }

        /// <summary>Gets the family this entry belongs to. Revocation applies to all of
        /// it.</summary>
        public long FamilyId { get; }

        /// <summary>Gets this entry's position in its family, counted from zero.</summary>
        public long Generation { get; }

        /// <summary>Gets the absolute instant at and after which this entry is expired.</summary>
        public DateTime ExpiresAtUtc { get; }

        /// <summary>Gets the immutable non-secret snapshot recorded against this entry.</summary>
        public RefreshTokenSubject Subject { get; }

        /// <summary>
        /// Gets a value indicating whether this entry has already been redeemed. A second
        /// presentation of redeemed material is a replay.
        /// </summary>
        public bool IsConsumed { get; private set; }

        /// <summary>
        /// Gets a value indicating whether this entry has been revoked, whether by a sign-off or
        /// by the family-wide revocation that follows a detected replay.
        /// </summary>
        public bool IsRevoked { get; private set; }

        /// <summary>Marks this entry redeemed. Call only while holding the store's
        /// gate.</summary>
        public void MarkConsumed() => IsConsumed = true;

        /// <summary>Marks this entry revoked. Call only while holding the store's gate.</summary>
        public void MarkRevoked() => IsRevoked = true;
    }
}

/// <summary>
/// The complete set of results a refresh-token operation can report.
/// </summary>
/// <remarks>
/// <para>
/// Internal to this assembly and confined to this file. These members are the store's private
/// vocabulary: they must not appear on an application-layer contract, in a wire DTO or as a token
/// claim. A caller translates them into whatever its own contract requires — typically a single
/// problem-details response that does not distinguish between them, because telling an
/// unauthorised holder <em>why</em> its material was refused tells it something worth knowing.
/// </para>
/// <para>
/// <see cref="Unknown"/> is deliberately the zero member, so that a value left at its default
/// reads as a refusal rather than as an approval. An enumeration whose default meant success
/// would turn any missed assignment into an authorisation.
/// </para>
/// </remarks>
internal enum RefreshTokenOutcome
{
    /// <summary>
    /// The presented material does not correspond to any entry, or could not be one of this
    /// store's tokens at all. Nothing was changed.
    /// </summary>
    Unknown = 0,

    /// <summary>
    /// The operation completed: material was found redeemable, was redeemed, or was revoked by
    /// this call.
    /// </summary>
    Succeeded = 1,

    /// <summary>
    /// The entry's expiry has passed. It can never be redeemed again and no replacement is
    /// issued.
    /// </summary>
    Expired = 2,

    /// <summary>
    /// The entry was revoked, whether by a sign-off or by the family-wide revocation that follows
    /// a detected replay. No replacement is issued.
    /// </summary>
    Revoked = 3,

    /// <summary>
    /// The entry had already been redeemed, so presenting it again is a replay. Every generation
    /// in its family has been revoked, and no replacement survives.
    /// </summary>
    AlreadyUsed = 4,

    /// <summary>
    /// A revocation found every generation of the family already revoked, so it had nothing left
    /// to change. Reported distinctly from <see cref="Succeeded"/> so a repeated sign-off is not
    /// mistaken for a fresh one.
    /// </summary>
    AlreadyRevoked = 5,
}

/// <summary>
/// The immutable, non-secret facts recorded against a refresh token, so that redeeming it can
/// re-mint an access token describing the same caller.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately a sealed class and not a record. A record synthesises a string representation
/// that prints every member, which would put a caller's identity — and, on the sibling result
/// types, a live token — into any string ever built from one of these objects. A plain class
/// inherits the representation that prints only its type name, so the safe behaviour is the
/// default one and no override is needed to obtain it. That property is worth more here than
/// value equality, which nothing in this file needs.
/// </para>
/// <para>
/// Constructing an instance copies the two collections, so a caller that keeps and later mutates
/// the list it passed cannot alter a snapshot already recorded against an issued token. The
/// copies are exposed only through a read-only view, so the arrays behind them cannot be reached
/// and mutated either. Immutability is what makes one instance safe to share between the several
/// generations of a family and safe to read on any thread.
/// </para>
/// <para>
/// Values are recorded exactly as supplied. This type validates nothing and normalises nothing:
/// the caller owns what a claim should contain, and a store that silently rewrote its input would
/// make the token it protects disagree with the state it was minted from. A null collection is
/// the single accommodation, read as "none supplied" and recorded as empty.
/// </para>
/// <para>
/// No identifier is range-checked, and none may be. In this schema both -1 and 0 are legitimate
/// identifiers — the portal table seeds its identity at -1 and the role table at 0 — so a guard
/// rejecting them would refuse real callers, and treating either as "absent" would misread real
/// data. Absence is carried by nullability alone.
/// </para>
/// <para>
/// Nothing secret belongs on this type. There is no token, no access token, no credential and no
/// signing material among the members below, and none may be added: an instance is retained for
/// the whole life of a token family, so anything sensitive placed here would be retained with it.
/// </para>
/// </remarks>
internal sealed class RefreshTokenSubject
{
    /// <summary>
    /// Records the facts to be re-minted when this token, or a later generation of its family, is
    /// redeemed.
    /// </summary>
    /// <param name="userId">Identifier of the caller the token was issued to.</param>
    /// <param name="portalId">
    /// Identifier of the portal scoping the caller, or <see langword="null"/> when no portal
    /// scope applies.
    /// </param>
    /// <param name="userName">
    /// The caller's login name, or <see langword="null"/> when it is not known. An empty string
    /// is recorded as given rather than converted, so the two remain distinguishable.
    /// </param>
    /// <param name="isSuperUser">
    /// Whether the caller carries the host-level flag. Recorded so the replacement token
    /// describes the caller consistently; it is not, and must never become, an authorisation
    /// shortcut.
    /// </param>
    /// <param name="roles">
    /// The caller's role names. Copied on entry. <see langword="null"/> is recorded as empty.
    /// </param>
    /// <param name="permissionKeys">
    /// The caller's permission keys. Copied on entry. <see langword="null"/> is recorded as
    /// empty.
    /// </param>
    public RefreshTokenSubject(
        int userId,
        int? portalId,
        string? userName,
        bool isSuperUser,
        IEnumerable<string>? roles,
        IEnumerable<string>? permissionKeys)
    {
        UserId = userId;
        PortalId = portalId;
        UserName = userName;
        IsSuperUser = isSuperUser;

        // Materialise first, then wrap. Enumerating into a private array is what severs the
        // link to the caller's collection; the read-only wrapper is what stops the array being
        // reached through the property and mutated afterwards. Either step alone would leave a
        // way in.
        Roles = Array.AsReadOnly(roles?.ToArray() ?? []);
        PermissionKeys = Array.AsReadOnly(permissionKeys?.ToArray() ?? []);
    }

    /// <summary>Gets the identifier of the caller the token was issued to.</summary>
    public int UserId { get; }

    /// <summary>
    /// Gets the identifier of the portal scoping the caller, or <see langword="null"/> when no
    /// portal scope applies.
    /// </summary>
    public int? PortalId { get; }

    /// <summary>
    /// Gets the caller's login name, or <see langword="null"/> when it is not known.
    /// </summary>
    public string? UserName { get; }

    /// <summary>Gets a value indicating whether the caller carries the host-level flag.</summary>
    public bool IsSuperUser { get; }

    /// <summary>Gets the caller's role names. Never <see langword="null"/>.</summary>
    public IReadOnlyList<string> Roles { get; }

    /// <summary>Gets the caller's permission keys. Never <see langword="null"/>.</summary>
    public IReadOnlyList<string> PermissionKeys { get; }
}

/// <summary>
/// What issuing a refresh token produced: the token itself, its absolute expiry, and the snapshot
/// recorded against it.
/// </summary>
/// <remarks>
/// A sealed class rather than a record, for the reason set down on the subject type: the member
/// below holds a live credential, and a synthesised string representation would print it. Issuing
/// cannot fail for any reason short of a rejected argument, so this type carries no outcome —
/// receiving one means a token was created.
/// </remarks>
internal sealed class RefreshTokenIssueResult
{
    /// <summary>
    /// Records the token that was issued, its expiry, and the snapshot stored against it.
    /// </summary>
    /// <param name="refreshToken">The raw token, to be handed onward exactly once.</param>
    /// <param name="expiresAtUtc">The absolute expiry instant, in Coordinated Universal
    /// Time.</param>
    /// <param name="subject">The snapshot recorded against the token.</param>
    public RefreshTokenIssueResult(
        string refreshToken,
        DateTime expiresAtUtc,
        RefreshTokenSubject subject)
    {
        RefreshToken = refreshToken;
        ExpiresAtUtc = expiresAtUtc;
        Subject = subject;
    }

    /// <summary>
    /// Gets the raw refresh token. This is the only time it is available: the store kept a
    /// one-way digest of it and cannot reproduce it. Return it to the caller who authenticated
    /// and retain it nowhere else.
    /// </summary>
    public string RefreshToken { get; }

    /// <summary>
    /// Gets the absolute instant at and after which the token is expired, in Coordinated
    /// Universal Time.
    /// </summary>
    public DateTime ExpiresAtUtc { get; }

    /// <summary>
    /// Gets the snapshot the store recorded, echoed back so that what is minted into a token and
    /// what will be re-minted on redemption are known to be the same facts.
    /// </summary>
    public RefreshTokenSubject Subject { get; }
}

/// <summary>
/// What reading the state of presented material found, without changing anything.
/// </summary>
/// <remarks>
/// A successful reading is an observation, not a permission. The material may be redeemed or
/// revoked by another request between this reading and any action taken on it, which is why
/// redemption repeats every check under its own lock and decides for itself.
/// </remarks>
internal sealed class RefreshTokenInspection
{
    private RefreshTokenInspection(
        RefreshTokenOutcome outcome,
        RefreshTokenSubject? subject,
        DateTime? expiresAtUtc)
    {
        Outcome = outcome;
        Subject = subject;
        ExpiresAtUtc = expiresAtUtc;
    }

    /// <summary>Gets what the reading found.</summary>
    public RefreshTokenOutcome Outcome { get; }

    /// <summary>
    /// Gets the snapshot recorded against the material, or <see langword="null"/> unless
    /// <see cref="Outcome"/> is <see cref="RefreshTokenOutcome.Succeeded"/>.
    /// </summary>
    public RefreshTokenSubject? Subject { get; }

    /// <summary>
    /// Gets the material's absolute expiry, or <see langword="null"/> unless
    /// <see cref="Outcome"/> is <see cref="RefreshTokenOutcome.Succeeded"/>.
    /// </summary>
    public DateTime? ExpiresAtUtc { get; }

    /// <summary>
    /// Reports material that is currently redeemable, carrying its snapshot and expiry.
    /// </summary>
    /// <param name="subject">The snapshot recorded against the material.</param>
    /// <param name="expiresAtUtc">The material's absolute expiry.</param>
    /// <returns>A successful reading.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="subject"/> is <see langword="null"/>.
    /// </exception>
    public static RefreshTokenInspection Succeeded(
        RefreshTokenSubject subject,
        DateTime expiresAtUtc)
    {
        ArgumentNullException.ThrowIfNull(subject);

        return new RefreshTokenInspection(RefreshTokenOutcome.Succeeded, subject, expiresAtUtc);
    }

    /// <summary>
    /// Reports material that is not redeemable, carrying the reason and nothing else.
    /// </summary>
    /// <param name="outcome">Why the material is not redeemable.</param>
    /// <returns>An unsuccessful reading.</returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="outcome"/> is <see cref="RefreshTokenOutcome.Succeeded"/>.
    /// </exception>
    /// <remarks>
    /// The guard is what keeps the invariant honest rather than merely documented: a successful
    /// outcome can only be produced by the factory that also demands a snapshot, so no reading
    /// can claim success while carrying nothing to act on.
    /// </remarks>
    public static RefreshTokenInspection Failed(RefreshTokenOutcome outcome)
    {
        if (outcome == RefreshTokenOutcome.Succeeded)
        {
            throw new ArgumentOutOfRangeException(
                nameof(outcome),
                outcome,
                "An unsuccessful reading cannot carry a successful outcome.");
        }

        return new RefreshTokenInspection(outcome, subject: null, expiresAtUtc: null);
    }
}

/// <summary>
/// What redeeming presented material produced: on success the single replacement token, otherwise
/// the reason the redemption was refused.
/// </summary>
/// <remarks>
/// A sealed class rather than a record, for the reason set down on the subject type: on success
/// one member holds a live credential. On failure that member is <see langword="null"/> and no
/// token was created — a refused redemption never leaves a usable token behind, and a replay
/// revokes the whole family before reporting.
/// </remarks>
internal sealed class RefreshTokenRotationResult
{
    private RefreshTokenRotationResult(
        RefreshTokenOutcome outcome,
        string? refreshToken,
        DateTime? expiresAtUtc,
        RefreshTokenSubject? subject)
    {
        Outcome = outcome;
        RefreshToken = refreshToken;
        ExpiresAtUtc = expiresAtUtc;
        Subject = subject;
    }

    /// <summary>Gets the result of the redemption.</summary>
    public RefreshTokenOutcome Outcome { get; }

    /// <summary>
    /// Gets the replacement token, or <see langword="null"/> unless <see cref="Outcome"/> is
    /// <see cref="RefreshTokenOutcome.Succeeded"/>. As with issuing, this is the only time the
    /// value is available.
    /// </summary>
    public string? RefreshToken { get; }

    /// <summary>
    /// Gets the replacement's absolute expiry, or <see langword="null"/> unless
    /// <see cref="Outcome"/> is <see cref="RefreshTokenOutcome.Succeeded"/>.
    /// </summary>
    public DateTime? ExpiresAtUtc { get; }

    /// <summary>
    /// Gets the snapshot recorded against the replacement, or <see langword="null"/> unless
    /// <see cref="Outcome"/> is <see cref="RefreshTokenOutcome.Succeeded"/>.
    /// </summary>
    public RefreshTokenSubject? Subject { get; }

    /// <summary>
    /// Reports a completed redemption, carrying the single replacement it produced.
    /// </summary>
    /// <param name="refreshToken">The replacement token, to be handed onward exactly
    /// once.</param>
    /// <param name="expiresAtUtc">The replacement's absolute expiry.</param>
    /// <param name="subject">The snapshot recorded against the replacement.</param>
    /// <returns>A successful redemption.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="refreshToken"/> or <paramref name="subject"/> is <see langword="null"/>.
    /// </exception>
    public static RefreshTokenRotationResult Succeeded(
        string refreshToken,
        DateTime expiresAtUtc,
        RefreshTokenSubject subject)
    {
        ArgumentNullException.ThrowIfNull(refreshToken);
        ArgumentNullException.ThrowIfNull(subject);

        return new RefreshTokenRotationResult(
            RefreshTokenOutcome.Succeeded,
            refreshToken,
            expiresAtUtc,
            subject);
    }

    /// <summary>
    /// Reports a refused redemption, carrying the reason and no token.
    /// </summary>
    /// <param name="outcome">Why the redemption was refused.</param>
    /// <returns>An unsuccessful redemption.</returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="outcome"/> is <see cref="RefreshTokenOutcome.Succeeded"/>.
    /// </exception>
    /// <remarks>
    /// The guard makes it impossible to construct a result that claims success while carrying no
    /// replacement, so a caller reading <see cref="RefreshToken"/> after testing
    /// <see cref="Outcome"/> can rely on finding one.
    /// </remarks>
    public static RefreshTokenRotationResult Failed(RefreshTokenOutcome outcome)
    {
        if (outcome == RefreshTokenOutcome.Succeeded)
        {
            throw new ArgumentOutOfRangeException(
                nameof(outcome),
                outcome,
                "A refused redemption cannot carry a successful outcome.");
        }

        return new RefreshTokenRotationResult(
            outcome,
            refreshToken: null,
            expiresAtUtc: null,
            subject: null);
    }
}
