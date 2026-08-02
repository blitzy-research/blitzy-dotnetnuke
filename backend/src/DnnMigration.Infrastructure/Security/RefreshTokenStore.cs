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
// MIGRATION: revocation below is therefore added strengthening rather than a translation, and
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
// MIGRATION: protected by a reversible cipher whose key was held in source control, so anything
// MIGRATION: holding both the stored material and that key could recover a usable ticket. This
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
using DnnMigration.Domain.Enums;

using Microsoft.Extensions.Options;

namespace DnnMigration.Infrastructure.Security;

/// <summary>
/// Holds the server-side state that lets a refresh token be redeemed exactly once, hands back a
/// replacement on each redemption, detects the replay of a token that was already redeemed, and
/// revokes a whole token family on logout.
/// </summary>
/// <remarks>
/// <para>
/// Implementation, not a contract. This type is deliberately <see langword="internal"/> and is
/// reached only as <see cref="IRefreshTokenStore"/>, resolved from the container by
/// <c>AddInfrastructure</c>. Keeping it invisible is what stops a caller depending on the storage
/// medium, the locking strategy or the retention policy of whichever implementation is registered,
/// and it is why the contract types it exchanges live in the Domain layer while the bookkeeping
/// below does not.
/// </para>
/// <para>
/// Visible on the contract is only what a caller legitimately needs: the subject snapshot, the three
/// result types and the outcome vocabulary. None of those may be surfaced further — written into a
/// wire DTO or minted into a token claim — for the reason recorded on the outcome enumeration itself.
/// Family identifiers, generation counters and token digests never appear on the contract at all;
/// they are bookkeeping a caller can neither read nor supply.
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
/// Durability. State lives in the process, so restarting the API invalidates every outstanding
/// refresh token and every caller simply signs in again; access tokens already issued remain valid
/// until they elapse, exactly as they would anyway. A deployment that needs revocation to survive a
/// restart, or to be shared across replicas, replaces this type with a durable implementation of the
/// same operations.
/// </para>
/// <para>
/// Retention, and why it can be bounded without weakening replay detection. Two things are retained
/// for different reasons and for different lengths of time. A <em>live</em> entry keeps the snapshot
/// needed to re-mint an access token. A <em>spent</em> entry — redeemed, revoked or expired — keeps
/// only its family, its generation and its two lifecycle flags, because all that is still wanted
/// from it is the ability to recognise a later presentation as a replay rather than as an unknown
/// token. The snapshot is therefore discarded the moment an entry stops being redeemable, which is
/// what stops a sign-in name, a role list and a permission list being held for the life of the
/// process after they have ceased to be useful. Whole families are then dropped once their absolute
/// ceiling has passed, and at that point nothing is lost: every generation in such a family is
/// already refused on expiry, so the distinction between "already used" and "unknown" no longer
/// changes any outcome. That is only true because the ceiling exists — without it a family in
/// continuous use never became prunable, which is why bounded retention and the absolute ceiling are
/// one design rather than two.
/// </para>
/// <para>
/// Capacity. Total entries are capped, and issuing refuses rather than growing without limit once
/// the cap is reached and pruning has failed to recover room. An unbounded in-memory store reachable
/// from an unauthenticated endpoint is a denial-of-service lever; refusing to issue degrades sign-in
/// while the process stays healthy, whereas exhausting memory takes the whole API down. No cleanup
/// timer and no background worker is introduced: pruning happens opportunistically inside operations
/// that already hold the gate, so it adds no thread and no unsynchronised access.
/// </para>
/// <para>
/// Two ceilings, not one. Each token carries its own sliding expiry, and each <em>family</em>
/// carries an absolute ceiling fixed when the family was created. Rotation issues a replacement with
/// a fresh sliding expiry but copies the ceiling forward untouched, so a session cannot be extended
/// indefinitely by continuous use — which is what
/// <c>Application/Abstractions/ITokenService.cs</c> requires of any implementation. Reaching the
/// ceiling is not a revocation and not an error: it means the caller must authenticate again, which
/// is the only point at which a credential, an approval state and a lockout state are re-examined.
/// </para>
/// <para>
/// Indexed by user, as well as by digest and family. Revoking every token a user holds is a required
/// operation — the response to a detected replay and to an administrative credential reset — so the
/// families belonging to a user are indexed rather than discovered by scanning. Without that index
/// the operation would either be unavailable or cost a walk of the entire store.
/// </para>
/// <para>
/// Value-neutral failures. Every rejection reports one of a small, fixed set of outcomes and
/// nothing else. No message names a user, a portal, a family or another token, and no failure
/// path reveals whether two pieces of presented material are related. Material that cannot be one
/// of this store's tokens is refused without being hashed at all.
/// </para>
/// </remarks>
internal sealed class RefreshTokenStore : IRefreshTokenStore
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
    /// Largest number of entries — live and spent together — that the store will hold.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A cap rather than a target. It exists so that an in-memory store reachable from an
    /// unauthenticated endpoint cannot be grown without limit, and it is deliberately generous: at
    /// one entry per sign-in and per rotation, a hundred thousand entries is far beyond what any
    /// single process serves within one absolute ceiling, so a deployment behaving normally never
    /// approaches it. Reaching it therefore indicates either an attack or a ceiling configured so
    /// long that pruning cannot keep up, and in both cases refusing to issue is the safer outcome
    /// than consuming the host's memory.
    /// </para>
    /// <para>
    /// Spent entries count towards it. They are small, but they are exactly what an attacker
    /// accumulates by rotating in a loop, so excluding them would leave the cap trivially evadable.
    /// </para>
    /// </remarks>
    private const int MaximumStoredEntries = 100_000;

    /// <summary>
    /// Largest configured refresh lifetime, in days, that this store will accept for either the
    /// sliding lifetime or the absolute family ceiling.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A POLICY bound, and deliberately not a representational one. An earlier revision took the
    /// interval type's own limit, on the reasoning that the constructor's only job was to refuse a
    /// setting it could not convert. That limit is over ten million days, so in practice it refused
    /// nothing: a deployment could configure an absolute ceiling of a century and the store would
    /// accept it, which turns the one setting that is supposed to end a session into a setting that
    /// never does. Two guarantees are derived from the ceiling - how long a family stays redeemable,
    /// and how long a family that has passed its deadline is retained so a late presentation is still
    /// recognised as a replay - so an unbounded value removes both while appearing to configure them.
    /// </para>
    /// <para>
    /// One year is chosen because it is the point past which a renewable session is a permanent
    /// credential in all but name, and because it is far above any lifetime a deployment has cause to
    /// set: the shipped configuration is thirty days. It bounds BOTH settings the constructor reads,
    /// which matters most for the absolute ceiling, since that is the setting with no bound of its own
    /// in <see cref="JwtOptions"/>. The sliding lifetime additionally has the stricter
    /// <see cref="JwtOptions.MaximumRefreshTokenExpirationDays"/> applied to it at start-up, so this
    /// figure is the backstop for a store constructed directly rather than the operative limit on that
    /// setting.
    /// </para>
    /// </remarks>
    private const int MaximumLifetimeDays = 365;

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
    /// <remarks>
    /// Holds both redeemable and spent entries, and the difference is visible on the entry rather
    /// than in which dictionary it sits: a spent one has discarded its snapshot and kept its family,
    /// generation and flags. Keeping one dictionary is what lets a replay be recognised by a single
    /// probe, with no possibility of the two containers disagreeing about a digest.
    /// </remarks>
    private readonly Dictionary<string, RefreshTokenRecord> _recordsByDigest =
        new(StringComparer.Ordinal);

    /// <summary>
    /// Digests of every generation ever issued in a family, keyed by family identifier.
    /// </summary>
    /// <remarks>
    /// This index is what makes revocation a family-wide operation rather than a single-token
    /// one. Without it, revoking a family would mean scanning every entry in the store; with it,
    /// the cost is proportional to that one family's length. Within a family's life, entries are
    /// appended and never removed, so a replay detected against an early generation still reaches
    /// the newest one; a family is removed only as a whole, and only once its absolute ceiling has
    /// passed and every generation in it is refused on expiry regardless.
    /// </remarks>
    private readonly Dictionary<long, List<string>> _digestsByFamily = new();

    /// <summary>
    /// Identifiers of every family belonging to a user, keyed by that user's numeric key.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This index is what makes user-wide revocation possible at all. It backs two required
    /// behaviours: the response to a detected replay, which must end every session the user holds
    /// rather than only the family that was replayed, and the response to an administrative
    /// credential reset. Without it, either operation would have to walk every entry in the store.
    /// </para>
    /// <para>
    /// A set rather than a list, because a family is registered once per sign-in and a set makes
    /// re-registration harmless. Note what is <em>not</em> stored: the key is the user's numeric
    /// identifier only. No sign-in name, no role and no permission appears in this index, so it
    /// carries no personal data even while a family is live.
    /// </para>
    /// </remarks>
    private readonly Dictionary<int, HashSet<long>> _familiesByUser = new();

    /// <summary>
    /// Family identifiers in the order their families were created, oldest first.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is what makes pruning cheap. Every family's ceiling is its creation instant plus one
    /// constant interval, so families reach their ceilings in exactly the order they were created —
    /// which means the oldest family is always the next to become prunable, and pruning never has to
    /// search for candidates or scan the store. Entries leave the front, so the cost is proportional
    /// to what is actually removed rather than to what is held.
    /// </para>
    /// <para>
    /// The ordering property depends on the ceiling interval being fixed for the process lifetime,
    /// which is why that interval is copied once at construction rather than re-read per operation.
    /// A ceiling that could change mid-flight would break the order and silently make pruning miss
    /// families.
    /// </para>
    /// </remarks>
    private readonly Queue<long> _familiesInCreationOrder = new();

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
    /// How long a refresh-token family remains redeemable in total, measured from the sign-in that
    /// created it and never extended by rotation.
    /// </summary>
    /// <remarks>
    /// Copied and validated once at construction, for the same reason as the sliding lifetime: a
    /// singleton that re-read a reconfigured ceiling mid-flight would stamp ceilings that could not
    /// be compared with those already recorded. This is the bound that makes rotation unable to
    /// extend a session indefinitely.
    /// </remarks>
    private readonly TimeSpan _refreshTokenAbsoluteLifetime;

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
    /// <see cref="JwtOptions.RefreshTokenExpirationDays"/> and
    /// <see cref="JwtOptions.RefreshTokenAbsoluteExpirationDays"/> are read; every other member is
    /// ignored here, so no credential material is touched by this type.
    /// </param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="clock"/> or <paramref name="jwtOptions"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Either configured lifetime is not a positive number of days or is too large to be represented
    /// as an interval, or the absolute ceiling is shorter than the per-token lifetime.
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

        var ceilingDays = jwtOptions.Value.RefreshTokenAbsoluteExpirationDays;
        var ceilingSettingName =
            $"{JwtOptions.SectionName}:{nameof(JwtOptions.RefreshTokenAbsoluteExpirationDays)}";

        if (ceilingDays <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(jwtOptions),
                ceilingDays,
                $"{ceilingSettingName} must be a positive number of days.");
        }

        if (ceilingDays > MaximumLifetimeDays)
        {
            throw new ArgumentOutOfRangeException(
                nameof(jwtOptions),
                ceilingDays,
                $"{ceilingSettingName} must not exceed {MaximumLifetimeDays} days.");
        }

        // A ceiling below the sliding lifetime would clamp every token to the ceiling and make the
        // sliding setting unreachable, so the pair is refused together rather than silently
        // reinterpreted. The application layer's options validator reports the same disagreement at
        // start-up; this check is the backstop for a store constructed directly.
        if (ceilingDays < lifetimeDays)
        {
            throw new ArgumentOutOfRangeException(
                nameof(jwtOptions),
                ceilingDays,
                $"{ceilingSettingName} must not be less than {settingName}, which is "
                + $"{lifetimeDays} day(s).");
        }

        _clock = clock;
        _refreshTokenLifetime = TimeSpan.FromDays(lifetimeDays);
        _refreshTokenAbsoluteLifetime = TimeSpan.FromDays(ceilingDays);
    }

    /// <summary>
    /// Issues the first refresh token of a new family, for a caller who has just authenticated.
    /// </summary>
    /// <param name="subject">
    /// The non-secret facts to be re-minted into an access token when this token, or any later
    /// generation of its family, is redeemed.
    /// </param>
    /// <returns>
    /// On success, the raw token — handed over exactly once and never stored — together with its
    /// absolute expiry and the snapshot recorded against it. On refusal,
    /// <see cref="RefreshTokenOutcome.CapacityExhausted"/> and no token, which is the one way this
    /// operation can decline: the store is full and pruning recovered no room.
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

        // A new family's ceiling is fixed here, once, and every generation of the family will carry
        // this same instant forward unchanged. The token's own expiry is the earlier of its sliding
        // window and that ceiling, so the very first token of a family whose ceiling is shorter than
        // the sliding lifetime is already bounded correctly.
        var familyExpiresAtUtc = AddAbsoluteLifetime(issuedAtUtc);
        var expiresAtUtc = Earlier(AddLifetime(issuedAtUtc), familyExpiresAtUtc);

        lock (_gate)
        {
            // Prune before testing capacity, so a store full of families that have passed their
            // ceiling admits a new sign-in instead of refusing one.
            Prune(issuedAtUtc);

            if (_recordsByDigest.Count >= MaximumStoredEntries)
            {
                return RefreshTokenIssueResult.Failed(RefreshTokenOutcome.CapacityExhausted);
            }

            // Numbering under the gate is what makes the identifier unique; a counter incremented
            // anywhere else could hand the same family to two concurrent sign-ins.
            var familyId = ++_lastFamilyId;
            var rawToken = CreateAndStore(
                familyId,
                FirstGeneration,
                expiresAtUtc,
                familyExpiresAtUtc,
                subject);

            // Registering the family against its owner is what makes user-wide revocation reachable
            // later. It happens inside the same gated step that created the family, so a family can
            // never exist unindexed.
            if (!_familiesByUser.TryGetValue(subject.UserId, out var userFamilies))
            {
                userFamilies = [];
                _familiesByUser[subject.UserId] = userFamilies;
            }

            userFamilies.Add(familyId);
            _familiesInCreationOrder.Enqueue(familyId);

            return RefreshTokenIssueResult.Succeeded(rawToken, expiresAtUtc, subject);
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

            // The property pattern, rather than a null-forgiving operator, is what makes the
            // released-snapshot case safe by construction: a redeemable entry always still holds its
            // snapshot, so this test can never fail for a genuinely redeemable entry, and if it ever
            // did the reading would report a refusal instead of dereferencing nothing.
            // A refusal reports the owner when the entry was found, so the caller can answer a replay
            // account-wide. The owner key outlives the snapshot on the entry precisely for this.
            return stored is { Subject: not null } && outcome == RefreshTokenOutcome.Succeeded
                ? RefreshTokenInspection.Succeeded(stored.Subject, stored.ExpiresAtUtc)
                : RefreshTokenInspection.Failed(outcome, stored?.OwnerUserId);
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
    /// The replacement receives a fresh sliding expiry measured from this redemption rather than
    /// the remaining window of the token it replaces, so the per-token bound limits how long a token
    /// may sit unused. It does <em>not</em> receive a fresh ceiling: the family's absolute expiry is
    /// copied forward untouched, so continuous use cannot extend a session past it. Both bounds are
    /// therefore in force at once, and the replacement expires at whichever comes first.
    /// </para>
    /// <para>
    /// Re-presenting a token that was already redeemed revokes every family the owning user holds,
    /// not just the family presented. A replay establishes that a token has been copied but not
    /// which of the user's sessions the copy came from, so ending one family would leave a
    /// second copied family working.
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
            // Rotation prunes as well as issuing, because rotation is the operation that actually
            // grows a long-lived session: the redeemed entry is retained for replay detection and a
            // replacement is added beside it, so a session left rotating adds an entry each time
            // while creating no new family. Pruning here keeps that growth bounded by the ceiling
            // rather than by however long the process happens to run.
            //
            // Unlike issuing, rotation never refuses on capacity. Refusing to issue declines a new
            // session, which is recoverable by signing in again; refusing to rotate would destroy a
            // live one. Rotation also cannot create a family, so it cannot be used to escape the
            // family bound, and it cannot be driven at all without a valid unredeemed token — which
            // means growth along this path is bounded by the ceiling divided by the rotation cadence,
            // per authenticated session, and is not reachable by an unauthenticated party.
            Prune(asOfUtc);

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
                    // Every family the user holds, not merely the family that was replayed. A
                    // replay says a token has been copied; it says nothing about which of that
                    // user's sessions the copy came from, so ending only this one would leave an
                    // attacker holding whichever other family it also copied. This is the behaviour
                    // ITokenService.RefreshAsync requires of an implementation, and the owner key
                    // survives on the entry precisely so it is still available here after the
                    // snapshot has been discarded.
                    RevokeAllForUserCore(stored.OwnerUserId);
                }

                // An entry that has passed its own expiry can never be redeemed again, so the
                // snapshot it still holds is dead weight. Releasing it here rather than waiting for
                // the family to be pruned shortens how long an abandoned session's sign-in name and
                // role list are retained.
                if (stored is not null && outcome == RefreshTokenOutcome.Expired)
                {
                    stored.DiscardSubject();
                }

                return RefreshTokenRotationResult.Failed(outcome);
            }

            // MIGRATION: the presented token and the supplied subject must describe the SAME user.
            // The caller re-reads the user's roles and permission keys before every rotation, so a
            // privilege change takes effect on the next rotation instead of persisting for the life
            // of the family; but that re-read is authority the caller holds for ITS OWN principal, so
            // pairing it with a token issued to somebody else would mint a replacement carrying one
            // user's identity and another's privileges. Presenting a mismatched pair is a defect in
            // the calling code rather than a bad submission - the caller must read the user recorded
            // against the token first - so it is thrown rather than reported as a failed outcome,
            // and it is checked before the record is consumed so a rejected attempt leaves the
            // family untouched.
            if (subject.UserId != stored.OwnerUserId)
            {
                throw new InvalidOperationException(
                    "The supplied subject does not belong to the user the presented refresh token "
                    + "was issued to. Re-read the caller's roles and permission keys for the user "
                    + "recorded against the token before redeeming it.");
            }

            stored.MarkConsumed();

            // The consumed entry keeps its family, generation and flags — enough to recognise a
            // later replay — and drops the snapshot it no longer needs. See the retention paragraph
            // at the head of this file.
            stored.DiscardSubject();

            // The replacement inherits the family's ceiling verbatim. This single line is what stops
            // rotation extending a session without limit: the sliding window restarts, the ceiling
            // does not move.
            var familyExpiresAtUtc = stored.FamilyExpiresAtUtc;
            var expiresAtUtc = Earlier(AddLifetime(asOfUtc), familyExpiresAtUtc);
            var replacement = CreateAndStore(
                stored.FamilyId,
                stored.Generation + 1,
                expiresAtUtc,
                familyExpiresAtUtc,
                subject);

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

    /// <inheritdoc />
    public RefreshTokenOutcome RevokeAllForUser(int userId)
    {
        lock (_gate)
        {
            // Whether the user is known to this store is decided before anything is revoked, so that
            // "holds no family" and "held families, all already revoked" stay distinguishable. Doing
            // it afterwards could not tell them apart, because both leave nothing changed.
            if (!_familiesByUser.ContainsKey(userId))
            {
                return RefreshTokenOutcome.Unknown;
            }

            return RevokeAllForUserCore(userId)
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
    /// The already-redeemed arm deliberately precedes the expired one. Every rotation stamps a
    /// fresh expiry — bounded by the family's ceiling, but fresh — so a family's earliest generation
    /// may be long past its own expiry while its newest is still live, and a replay of that earliest
    /// generation is therefore a real signal about a live credential. Testing expiry first would
    /// classify it as merely expired and forfeit the user-wide revocation that the signal calls for.
    /// Expired material still never yields a replacement, so nothing is weakened by the ordering —
    /// only the replay detection is strengthened.
    /// </para>
    /// <para>
    /// Once a family's ceiling has passed, this reading reports expiry for every one of its
    /// generations, because each generation's own expiry is capped at that ceiling. That is what makes
    /// the family safe to discard at the ceiling: from then on the redeemed-versus-unknown distinction
    /// cannot change any outcome, since both are refusals and neither yields a replacement.
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

            // A revoked entry can never be redeemed, so its snapshot is released at the same moment
            // it becomes unusable. Logout and administrative reset both reach this line.
            member.DiscardSubject();
            revokedAny = true;
        }

        return revokedAny;
    }

    /// <summary>
    /// Revokes every family belonging to one user, ending every session that user holds.
    /// </summary>
    /// <param name="userId">Numeric key of the user whose families are to be revoked.</param>
    /// <returns>
    /// <see langword="true"/> when this call changed at least one generation from unrevoked to
    /// revoked; <see langword="false"/> when the user holds no family, or every family was already
    /// fully revoked.
    /// </returns>
    /// <remarks>
    /// <para>
    /// Two callers require this, and neither is a convenience. A detected replay must end every
    /// session the user holds, because a replay proves a token was copied without revealing which
    /// session the copy came from. An administrative credential reset must do the same, or the reset
    /// would leave an existing session renewable by whoever prompted the reset.
    /// </para>
    /// <para>
    /// Idempotent by construction: it reports whether anything changed rather than whether anything
    /// was found, so calling it for a user with no families succeeds and reports no change. The
    /// families are read through the user index rather than by scanning, and the index is not
    /// modified here — a revoked family stays indexed until its ceiling passes, so a replay against
    /// one of its generations is still recognised as a replay rather than as an unknown token.
    /// </para>
    /// <para>
    /// The numeric key is used exactly as supplied. Both zero and negative one are legitimate account
    /// keys in this schema, so neither is treated as meaning "no user"; a key that belongs to nobody
    /// simply matches no family.
    /// </para>
    /// <para>
    /// Must be called while holding <see cref="_gate"/>. The public
    /// <see cref="RevokeAllForUser(int)"/> takes the gate and delegates here.
    /// </para>
    /// </remarks>
    private bool RevokeAllForUserCore(int userId)
    {
        var userFamilies = _familiesByUser.GetValueOrDefault(userId);
        if (userFamilies is null)
        {
            return false;
        }

        var revokedAny = false;

        foreach (var familyId in userFamilies)
        {
            // Deliberately not short-circuited on the first success: every family must be revoked,
            // so the accumulated flag is combined rather than used to stop the walk.
            revokedAny |= RevokeFamily(familyId);
        }

        return revokedAny;
    }

    /// <summary>
    /// Discards every family whose absolute ceiling has passed, releasing its entries, its family
    /// index and its place in the user index.
    /// </summary>
    /// <param name="asOfUtc">The instant the surrounding operation captured.</param>
    /// <remarks>
    /// <para>
    /// Pruning at the ceiling — and only at the ceiling — is what makes bounded retention safe.
    /// Every generation of such a family is already refused on expiry, so forgetting it cannot turn
    /// a rejection into an acceptance; the single observable consequence is that a much later
    /// presentation of one of its tokens is reported as unknown rather than as already used, and
    /// both are refusals. Removing an entry any earlier than that WOULD be a security regression,
    /// because it would make a replay of a redeemed token indistinguishable from an unknown one
    /// while the rest of its family was still live.
    /// </para>
    /// <para>
    /// The walk stops at the first family that has not reached its ceiling, which is correct because
    /// the queue is in creation order and every ceiling is one fixed interval after its creation. No
    /// full scan of the store happens here, and the work done is proportional to the number of
    /// families actually removed.
    /// </para>
    /// <para>
    /// A family whose entries have all somehow gone is still dequeued and still cleaned out of both
    /// indexes, so a missing record cannot leave the queue stuck at its front and stall pruning
    /// permanently.
    /// </para>
    /// <para>
    /// Must be called while holding <see cref="_gate"/>: it mutates every container.
    /// </para>
    /// </remarks>
    private void Prune(DateTime asOfUtc)
    {
        while (_familiesInCreationOrder.TryPeek(out var familyId))
        {
            var familyDigests = _digestsByFamily.GetValueOrDefault(familyId);

            // The ceiling is read from any surviving generation, because every generation of a
            // family carries the same one. A family with no surviving generation is treated as
            // prunable so the queue can always make progress.
            var ceilingUtc = FindFamilyCeiling(familyDigests);
            if (ceilingUtc > asOfUtc)
            {
                break;
            }

            _familiesInCreationOrder.Dequeue();

            if (familyDigests is not null)
            {
                foreach (var familyDigest in familyDigests)
                {
                    _recordsByDigest.Remove(familyDigest);
                }

                _digestsByFamily.Remove(familyId);
            }

            RemoveFamilyFromUserIndex(familyId);
        }
    }

    /// <summary>
    /// Reports the absolute ceiling shared by a family's generations, or the earliest representable
    /// instant when the family has no surviving generation.
    /// </summary>
    /// <param name="familyDigests">Digests of the family's generations, or <see langword="null"/>.</param>
    /// <returns>The family's ceiling, or <see cref="DateTime.MinValue"/> when it has none left.</returns>
    /// <remarks>
    /// Returning the earliest representable instant for an empty family is what makes such a family
    /// unconditionally prunable, which is the behaviour <see cref="Prune"/> depends on to keep making
    /// progress. Must be called while holding <see cref="_gate"/>.
    /// </remarks>
    private DateTime FindFamilyCeiling(List<string>? familyDigests)
    {
        if (familyDigests is null)
        {
            return DateTime.MinValue;
        }

        foreach (var familyDigest in familyDigests)
        {
            var member = _recordsByDigest.GetValueOrDefault(familyDigest);
            if (member is not null)
            {
                return member.FamilyExpiresAtUtc;
            }
        }

        return DateTime.MinValue;
    }

    /// <summary>
    /// Removes one family from the per-user index, and removes the user's entry entirely once it
    /// holds no families.
    /// </summary>
    /// <param name="familyId">The family to remove.</param>
    /// <remarks>
    /// Dropping the user's entry when its last family goes is what keeps the index from accumulating
    /// one permanent entry per account that ever signed in. The search is over users rather than
    /// direct, because a family identifier does not carry its owner; the index is small — one entry
    /// per user with a live or recently expired family — and this runs only while a family is
    /// actually being discarded. Must be called while holding <see cref="_gate"/>.
    /// </remarks>
    private void RemoveFamilyFromUserIndex(long familyId)
    {
        foreach (var (userId, userFamilies) in _familiesByUser)
        {
            if (!userFamilies.Remove(familyId))
            {
                continue;
            }

            if (userFamilies.Count == 0)
            {
                _familiesByUser.Remove(userId);
            }

            return;
        }
    }

    /// <summary>
    /// Creates one fresh token, records its digest against a new entry, and returns the raw
    /// token.
    /// </summary>
    /// <param name="familyId">The family the new entry belongs to.</param>
    /// <param name="generation">The new entry's generation number within that family.</param>
    /// <param name="expiresAtUtc">The absolute instant after which the new token is
    /// expired.</param>
    /// <param name="familyExpiresAtUtc">The ceiling shared by every generation of the family, carried
    /// forward unchanged rather than recalculated.</param>
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
    /// The family ceiling is supplied rather than computed here, and that is the whole mechanism by
    /// which rotation cannot extend a session without end: a rotation passes forward the ceiling it
    /// read from the entry it is replacing, so every generation of a family records the same one and
    /// no generation can move it. Recording it per entry rather than in a separate family table also
    /// means the two can never disagree, and that pruning can recover a family's ceiling from any
    /// surviving generation.
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
        DateTime familyExpiresAtUtc,
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
            new RefreshTokenRecord(
                familyId,
                generation,
                expiresAtUtc,
                familyExpiresAtUtc,
                subject));

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
    /// Adds the configured absolute family lifetime to an instant, saturating rather than
    /// overflowing.
    /// </summary>
    /// <param name="instant">The instant a family was created, in Coordinated Universal Time.</param>
    /// <returns>The family's ceiling instant, in Coordinated Universal Time.</returns>
    /// <remarks>
    /// Separate from the sliding calculation because the two intervals answer different questions and
    /// must be able to differ: one bounds how long a single token stays redeemable, the other bounds
    /// how long the session it belongs to may be renewed for. Saturation and the stamped kind follow
    /// the same reasoning set down on the sliding calculation, so a family created at the very end of
    /// representable time yields a ceiling rather than a fault.
    /// </remarks>
    private DateTime AddAbsoluteLifetime(DateTime instant)
    {
        var headroom = DateTime.MaxValue - instant;

        return _refreshTokenAbsoluteLifetime < headroom
            ? instant + _refreshTokenAbsoluteLifetime
            : DateTime.SpecifyKind(DateTime.MaxValue, DateTimeKind.Utc);
    }

    /// <summary>
    /// Reports the earlier of two instants.
    /// </summary>
    /// <param name="first">The first instant to compare.</param>
    /// <param name="second">The second instant to compare.</param>
    /// <returns>Whichever of the two comes first.</returns>
    /// <remarks>
    /// Every expiry this store records is the earlier of the sliding expiry and the family ceiling,
    /// so that the two bounds compose instead of competing: a token stops being redeemable when
    /// either has passed, and the ceiling therefore cannot be overrun by a rotation that happens to
    /// arrive shortly before it. Reading it as a named comparison rather than as a conditional at
    /// each call site is what keeps the rule visibly identical at issue and at rotation.
    /// </remarks>
    private static DateTime Earlier(DateTime first, DateTime second) =>
        first <= second ? first : second;

    /// <summary>
    /// One stored refresh token: its family, its generation, its expiry, the snapshot recorded
    /// against it, and its lifecycle flags.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Nested and private, so an instance cannot be handed to a caller even accidentally: callers
    /// receive the immutable result types declared alongside
    /// <see cref="IRefreshTokenStore"/> instead. Notice what the type does not declare — there is
    /// no token field and no digest field. The digest lives only as the key of the dictionary this
    /// entry sits in and as an entry in the family index, and the token itself is never written
    /// down anywhere.
    /// </para>
    /// <para>
    /// Everything identifying is immutable; only the two lifecycle flags and the snapshot change,
    /// all three change in one direction only, and all three change exclusively under the enclosing
    /// store's gate. A flag that could be cleared would let revoked material become usable again and
    /// a snapshot that could be restored would defeat the point of releasing it, so no transition
    /// here has an inverse.
    /// </para>
    /// </remarks>
    private sealed class RefreshTokenRecord
    {
        public RefreshTokenRecord(
            long familyId,
            long generation,
            DateTime expiresAtUtc,
            DateTime familyExpiresAtUtc,
            RefreshTokenSubject subject)
        {
            FamilyId = familyId;
            Generation = generation;
            ExpiresAtUtc = expiresAtUtc;
            FamilyExpiresAtUtc = familyExpiresAtUtc;

            // Copied out of the snapshot rather than read through it, because the snapshot is
            // released once this entry can no longer be redeemed while the owner must outlive it:
            // user-wide revocation and the per-user index both need to know whose entry this is long
            // after there is any reason to remember who they are.
            OwnerUserId = subject.UserId;
            Subject = subject;
        }

        /// <summary>Gets the family this entry belongs to. Revocation applies to all of
        /// it.</summary>
        public long FamilyId { get; }

        /// <summary>Gets this entry's position in its family, counted from zero.</summary>
        public long Generation { get; }

        /// <summary>Gets the absolute instant at and after which this entry is expired.</summary>
        public DateTime ExpiresAtUtc { get; }

        /// <summary>
        /// Gets the instant at and after which every generation of this entry's family is expired,
        /// whatever their own expiries say.
        /// </summary>
        /// <remarks>
        /// Identical across every generation of one family and never recalculated, so a rotation
        /// carries it forward instead of renewing it. This is the bound that stops a continuously
        /// rotated session from lasting for ever, and it is also the instant at which the family
        /// becomes safe to forget.
        /// </remarks>
        public DateTime FamilyExpiresAtUtc { get; }

        /// <summary>
        /// Gets the numeric key of the account this entry belongs to.
        /// </summary>
        /// <remarks>
        /// Held separately from the snapshot precisely so that it survives the snapshot's release. A
        /// numeric key is not personal information on its own, whereas the sign-in name and role list
        /// in the snapshot are, which is why one is kept for the entry's whole life and the other is
        /// discarded as soon as it can no longer be needed.
        /// </remarks>
        public int OwnerUserId { get; }

        /// <summary>
        /// Gets the immutable non-secret snapshot recorded against this entry, or
        /// <see langword="null"/> once the entry can no longer be redeemed and the snapshot has been
        /// released.
        /// </summary>
        /// <remarks>
        /// A redeemable entry always holds its snapshot, so a reading that finds nothing here is
        /// reporting an entry that is spent, revoked or expired — never one that should have been
        /// honoured.
        /// </remarks>
        public RefreshTokenSubject? Subject { get; private set; }

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

        /// <summary>
        /// Releases the snapshot recorded against this entry. Call only while holding the store's
        /// gate, and only once the entry can no longer be redeemed.
        /// </summary>
        /// <remarks>
        /// Retaining a sign-in name and a role list for an entry that can never be honoured again
        /// serves nothing and lengthens how long personal information sits in memory, so redemption,
        /// revocation and expiry all release it at the moment they make the entry unusable. What the
        /// entry keeps afterwards — family, generation, owner key, the two flags and the two expiries
        /// — is exactly what replay detection, revocation and pruning still need, and no more. The
        /// operation is deliberately idempotent so that a second revocation of an already-released
        /// entry is not an error.
        /// </remarks>
        public void DiscardSubject() => Subject = null;
    }
}
