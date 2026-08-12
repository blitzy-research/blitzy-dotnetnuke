using System.Security.Cryptography;
using System.Text;
using DnnMigration.Application.Options;
using DnnMigration.Domain.Abstractions.Services;
using DnnMigration.Domain.Enums;
using Microsoft.Extensions.Options;

namespace DnnMigration.Infrastructure.Security;

/// <summary>
/// Process-local refresh-token store with atomic single-use rotation, family revocation and bounded
/// same-client concurrent-use grace.
/// </summary>
/// <remarks>
/// <para>
/// MIGRATION: legacy <c>FormsAuthentication</c> sign-out had no server-side session record at all - the
/// ticket was a self-contained cookie and revoking one was impossible. The target keeps refresh state in
/// this singleton for the lifetime of the process while access-token sign-out remains expiry plus
/// client-side discard. Only SHA-256 token digests and minimal identity/lifecycle fields are held; raw
/// refresh tokens are returned once and retained nowhere.
/// </para>
/// <para>
/// <strong>NO DATABASE OBJECT IS READ, WRITTEN OR REQUIRED BY THIS TYPE, AND THAT IS THE POINT.</strong>
/// An earlier revision persisted refresh families into a target-owned <c>[DnnMigration].[RefreshTokens]</c>
/// table, provisioned by a data-definition script an operator had to run first. That violated AAP rule T4 -
/// the existing DotNetNuke schema is immutable and no <c>CREATE</c>, <c>ALTER</c> or <c>DROP</c> may reach a
/// production database from this work - and it had a worse consequence than the rule breach: every
/// successful credential verification called <see cref="IssueAsync"/> before returning tokens, so against
/// the unaltered database the API was mandated to run on, login answered "token store unavailable" and
/// authentication could not complete at all. AAP section 0.4.3 registers the token service as a
/// <em>singleton</em>, which is only coherent for a store that holds its own state; this type is that store.
/// </para>
/// <para>
/// <strong>The operational consequence is stated rather than hidden.</strong> Refresh state does not survive
/// a process restart and is not shared between replicas, so a restart or a load-balanced second instance
/// forces callers to sign in again rather than refresh. That is a bounded availability cost - the access
/// token a caller already holds stays valid until its stamped expiry - and it is the cost of leaving the
/// existing schema untouched. It is recorded in <c>MIGRATION_NOTES.md</c> so a deployment that needs
/// cross-process refresh continuity knows it must supply a shared store of its own behind this same
/// contract rather than discovering the limitation in production.
/// </para>
/// <para>
/// <strong>The consequence is also DECLARED, ENFORCED AND OBSERVABLE, not only documented.</strong> Three
/// mechanisms exist so that a deployment cannot hold a mistaken belief about its own refresh state.
/// <c>RefreshTokenStore:Provider</c> makes the choice of store an explicit configuration value, so running
/// this process-local store is a recorded decision rather than a default nobody chose. The Infrastructure
/// composition root's <c>ValidateRefreshTokenStoreTopology</c> compares that declaration against the
/// <c>IRefreshTokenStore</c> the container actually resolves and refuses to start when the two disagree in
/// either direction - a deployment that declares an external store but registered none, and a deployment
/// that silently overrode this one while still declaring <c>InProcess</c>, are both start-up failures. And
/// <c>HealthChecks/RefreshTokenStoreHealth</c> reports which store is active, whether it is replica-safe, and
/// how full this one is, so saturation is visible before it starts signing callers out.
/// </para>
/// <para>
/// <strong>Substituting a shared store requires no change to this file or to this layer.</strong>
/// <c>IRefreshTokenStore</c> is a public Domain contract, both consumers - the token service and the
/// authentication service - depend on the contract rather than on this type, and the container resolves the
/// LAST registration of a service. A deployment therefore registers its own implementation after
/// <c>AddInfrastructure</c> and sets <c>RefreshTokenStore:Provider</c> to <c>External</c>; the topology check
/// then verifies the substitution took effect. That seam is covered by a test, so it is a verified property
/// rather than an assurance.
/// </para>
/// <para>
/// <strong>What this type deliberately does NOT do is become the shared store itself.</strong> The three
/// routes to cross-process refresh state are each closed by the plan this work implements: a target-owned
/// SQL table is forbidden by AAP rule T4, a distributed-cache client is absent from the frozen dependency
/// inventory in AAP section 0.6, and a third container to host one would break the two-service topology AAP
/// section 0.9.3 reproduces verbatim. The divergence from a shared-store resolution is recorded, with those
/// citations, in <c>MIGRATION_NOTES.md</c>.
/// </para>
/// <para>
/// <strong>Every state transition is serialised on one lock.</strong> The SQL implementation this replaces
/// held a <c>SERIALIZABLE</c> transaction with <c>UPDLOCK, HOLDLOCK</c> around each read-modify-write, so
/// two exchanges racing on one token produced exactly one successor. A single monitor over the whole
/// dictionary reproduces that guarantee exactly, and it is affordable because every operation is a handful
/// of dictionary lookups with no I/O inside the region. No <c>await</c> occurs while the lock is held, so
/// the lock can never be taken on one thread and released on another.
/// </para>
/// </remarks>
internal sealed class RefreshTokenStore : IRefreshTokenStore
{
    private const int TokenEntropyBytes = 32;

    /// <summary>
    /// Serialises every read-modify-write, standing in for the SERIALIZABLE transaction the SQL
    /// implementation used.
    /// </summary>
    private readonly object _gate = new();

    /// <summary>
    /// Live and consumed generations, keyed by the SHA-256 digest of the token that addresses them.
    /// </summary>
    /// <remarks>
    /// A consumed generation is deliberately RETAINED rather than removed: its digest is the theft signal
    /// that lets a replay be recognised, and it stays until the family's absolute ceiling passes. The
    /// comparer is the fixed-length digest comparer below, so lookup is by value rather than by array
    /// reference.
    /// </remarks>
    private readonly Dictionary<byte[], StoredToken> _tokens = new(DigestComparer.Instance);

    private readonly IClock _clock;
    private readonly TimeSpan _slidingLifetime;
    private readonly TimeSpan _familyLifetime;

    /// <summary>
    /// Ceiling on how many generations this store tracks at once, from
    /// <c>RefreshTokenStore:MaximumTrackedTokens</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A bound is mandatory rather than defensive. The SQL implementation this replaces was bounded by disk
    /// and pruned in batches; an unbounded in-process dictionary is a memory-exhaustion vector, because a
    /// caller holding valid credentials adds one entry per sign-in and two per rotation and nothing expires
    /// for the whole family lifetime. The credential rate limiter bounds how fast that can happen; this
    /// bounds how far it can go.
    /// </para>
    /// <para>
    /// MIGRATION: this was a compiled constant of 100,000 until the QA remediation of the container
    /// checkpoint, which required the store's shape to be deployment configuration rather than a decision
    /// baked into the image. The default is unchanged, so a deployment that configures nothing is bounded
    /// exactly as it was; what changed is that an operator can now raise it, that the value is validated at
    /// start-up against the bounds declared on <see cref="RefreshTokenStoreOptions"/>, and that
    /// <c>RefreshTokenStoreHealth</c> reports how much of it is in use.
    /// </para>
    /// <para>
    /// When the ceiling is reached the families closest to their absolute ceiling are evicted first, so the
    /// cost of the bound is that the OLDEST refresh families stop refreshing and their holders sign in again
    /// - a bounded availability cost, never a failure to serve.
    /// </para>
    /// </remarks>
    private readonly int _maximumTrackedTokens;

    /// <summary>
    /// Window in which the same client may present an already-spent generation without it being treated as a
    /// replay, from <c>RefreshTokenStore:ConcurrentUseGraceSeconds</c>.
    /// </summary>
    /// <remarks>
    /// MIGRATION: also a compiled constant - five seconds - until the same remediation. The default is
    /// unchanged; zero is now expressible and disables the grace entirely, which is the strictest setting
    /// and treats every reuse as theft.
    /// </remarks>
    private readonly TimeSpan _concurrentUseGrace;

    /// <summary>Initialises a new instance of the <see cref="RefreshTokenStore"/> class.</summary>
    /// <param name="clock">UTC clock used for every lifecycle decision.</param>
    /// <param name="jwtOptions">Validated access and refresh-token options.</param>
    /// <param name="storeOptions">Validated store shape: capacity and concurrent-use grace.</param>
    /// <exception cref="ArgumentNullException">Any argument is <see langword="null"/>.</exception>
    /// <exception cref="OptionsValidationException">
    /// The token lifetimes or the store settings are invalid.
    /// </exception>
    /// <remarks>
    /// <para>
    /// Every dependency is itself a singleton, so this type may be one: it captures no request-scoped
    /// service and holds no database context. The database context the SQL implementation took - solely to
    /// read a connection string off it - is gone with the SQL, which is what removes the captive-dependency
    /// problem that forced the scoped registrations AAP section 0.4.3 forbids.
    /// </para>
    /// <para>
    /// BOTH options objects are validated HERE as well as by the Api layer's start-up validators, and the
    /// duplication is deliberate for the reason the options classes themselves record: a host that composed
    /// this layer without those validators - a test, a console utility, a future host - would otherwise
    /// construct a store on values nothing had judged. Validating at the point of use is what makes the rule
    /// true for every composition rather than for one.
    /// </para>
    /// <para>
    /// The provider NAME is deliberately not inspected here. Whether the declared provider matches the store
    /// the container resolves is a question about the container, not about this instance, so it is settled by
    /// <c>DependencyInjection.ValidateRefreshTokenStoreTopology</c> once the graph is built. This type would
    /// have to refuse to be constructed at all under an external declaration, which is wrong: a deployment
    /// may legitimately leave it registered and unused.
    /// </para>
    /// </remarks>
    public RefreshTokenStore(
        IClock clock,
        IOptions<JwtOptions> jwtOptions,
        IOptions<RefreshTokenStoreOptions> storeOptions)
    {
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(jwtOptions);
        ArgumentNullException.ThrowIfNull(storeOptions);

        IReadOnlyList<string> failures = jwtOptions.Value.Validate();
        if (failures.Count != 0)
        {
            throw new OptionsValidationException(
                JwtOptions.SectionName,
                typeof(JwtOptions),
                failures);
        }

        IReadOnlyList<string> storeFailures = storeOptions.Value.Validate();
        if (storeFailures.Count != 0)
        {
            throw new OptionsValidationException(
                RefreshTokenStoreOptions.SectionName,
                typeof(RefreshTokenStoreOptions),
                storeFailures);
        }

        _clock = clock;
        _slidingLifetime = TimeSpan.FromDays(jwtOptions.Value.RefreshTokenExpirationDays);
        _familyLifetime = TimeSpan.FromDays(jwtOptions.Value.RefreshTokenAbsoluteExpirationDays);
        _maximumTrackedTokens = storeOptions.Value.MaximumTrackedTokens;
        _concurrentUseGrace = TimeSpan.FromSeconds(storeOptions.Value.ConcurrentUseGraceSeconds);
    }

    /// <summary>
    /// Reports how much of this store's tracked-generation capacity is in use, for the health probe.
    /// </summary>
    /// <returns>The tracked count and the ceiling it is measured against.</returns>
    /// <remarks>
    /// <para>
    /// Internal rather than part of <see cref="IRefreshTokenStore"/>, and deliberately so: capacity is a
    /// property of THIS implementation, not of the contract. Adding it to the contract would oblige every
    /// deployment-supplied store to answer a question that may be meaningless for it - a shared store's
    /// capacity is its own operational concern - and would make the substitution seam harder to satisfy for
    /// no benefit. The health probe reaches it by pattern-matching the active store against this type, which
    /// is also how it reports honestly that it can say nothing about a replacement.
    /// </para>
    /// <para>
    /// Taken under the same monitor as every other read, so the count cannot be observed midway through a
    /// rotation's two writes.
    /// </para>
    /// </remarks>
    internal CapacitySnapshot DescribeCapacity()
    {
        lock (_gate)
        {
            return new CapacitySnapshot(_tokens.Count, _maximumTrackedTokens);
        }
    }

    /// <inheritdoc />
    /// <inheritdoc />
    /// <remarks>
    /// <see langword="false"/>, and stated rather than implied: this store's families live in one process,
    /// so a family it does not hold may still be held - and honoured - by another instance. Callers
    /// therefore report an unmatched revocation as unconfirmed instead of as a completed sign-out.
    /// </remarks>
    public bool IsAuthoritativeAcrossReplicas => false;

    public Task<RefreshTokenIssueResult> IssueAsync(
        RefreshTokenSubject subject,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(subject);
        cancellationToken.ThrowIfCancellationRequested();

        DateTime now = Utc(_clock.UtcNow);
        DateTime familyExpiresAtUtc = now.Add(_familyLifetime);
        DateTime expiresAtUtc = Earlier(now.Add(_slidingLifetime), familyExpiresAtUtc);
        TokenMaterial token = CreateToken();

        lock (_gate)
        {
            PruneExpired(now);

            // Room is made for the generation about to be inserted, rather than the bound being applied to
            // what is already there. Two things follow, and both matter now that the ceiling is a
            // deployment-configurable number rather than a compiled constant: the tracked set never exceeds
            // the ceiling an operator configured, and the generation being issued can never be the one
            // evicted - which, at capacity and with several families sharing one ceiling instant, would
            // otherwise return a refresh token that was already unknown to the store that issued it.
            EnforceCapacity(headroom: 1);

            _tokens[token.Digest] = new StoredToken(
                FamilyId: Guid.NewGuid(),
                Generation: 0,
                UserId: subject.UserId,
                PortalId: subject.PortalId,
                ExpiresAtUtc: expiresAtUtc,
                FamilyExpiresAtUtc: familyExpiresAtUtc,
                ConsumedAtUtc: null,
                ConsumedClientDigest: null,
                RevokedAtUtc: null);
        }

        return Task.FromResult(RefreshTokenIssueResult.Succeeded(
            token.RawToken,
            expiresAtUtc,
            subject));
    }

    /// <inheritdoc />
    public Task<RefreshTokenInspection> InspectAsync(
        string refreshToken,
        string clientBinding,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(refreshToken);
        ArgumentException.ThrowIfNullOrWhiteSpace(clientBinding);
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(refreshToken))
        {
            return Task.FromResult(RefreshTokenInspection.Failed(RefreshTokenOutcome.Unknown));
        }

        byte[] digest = Digest(refreshToken);
        byte[] clientDigest = Digest(clientBinding);
        DateTime now = Utc(_clock.UtcNow);

        try
        {
            lock (_gate)
            {
                if (!_tokens.TryGetValue(digest, out StoredToken? stored))
                {
                    return Task.FromResult(RefreshTokenInspection.Failed(RefreshTokenOutcome.Unknown));
                }

                RefreshTokenOutcome outcome = Classify(stored, clientDigest, now, _concurrentUseGrace);
                if (outcome == RefreshTokenOutcome.AlreadyUsed)
                {
                    RevokeAllForUserCore(stored.UserId, now);
                }

                return Task.FromResult(outcome == RefreshTokenOutcome.Succeeded
                    ? RefreshTokenInspection.Succeeded(
                        new RefreshTokenSubject(stored.UserId, stored.PortalId),
                        stored.ExpiresAtUtc)
                    : RefreshTokenInspection.Failed(outcome, stored.UserId));
            }
        }
        finally
        {
            // The lookup digest is a locally computed copy: the dictionary retains the array it was first
            // keyed with, so clearing this one cannot disturb a stored key.
            CryptographicOperations.ZeroMemory(digest);
            CryptographicOperations.ZeroMemory(clientDigest);
        }
    }

    /// <inheritdoc />
    public Task<RefreshTokenRotationResult> RotateAsync(
        string refreshToken,
        string clientBinding,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(refreshToken);
        ArgumentException.ThrowIfNullOrWhiteSpace(clientBinding);
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(refreshToken))
        {
            return Task.FromResult(RefreshTokenRotationResult.Failed(RefreshTokenOutcome.Unknown));
        }

        byte[] digest = Digest(refreshToken);
        byte[] clientDigest = Digest(clientBinding);
        DateTime now = Utc(_clock.UtcNow);

        try
        {
            lock (_gate)
            {
                if (!_tokens.TryGetValue(digest, out StoredToken? stored))
                {
                    return Task.FromResult(
                        RefreshTokenRotationResult.Failed(RefreshTokenOutcome.Unknown));
                }

                RefreshTokenOutcome outcome = Classify(stored, clientDigest, now, _concurrentUseGrace);
                if (outcome != RefreshTokenOutcome.Succeeded)
                {
                    if (outcome == RefreshTokenOutcome.AlreadyUsed)
                    {
                        RevokeAllForUserCore(stored.UserId, now);
                    }

                    return Task.FromResult(RefreshTokenRotationResult.Failed(outcome));
                }

                if (stored.Generation == int.MaxValue)
                {
                    throw new InvalidOperationException(
                        "The refresh-token family exhausted its generation counter.");
                }

                TokenMaterial replacement = CreateToken();
                DateTime replacementExpiresAtUtc = Earlier(
                    now.Add(_slidingLifetime),
                    stored.FamilyExpiresAtUtc);

                // Reclaimed and bounded BEFORE the two writes below, for the reason given on the issue path:
                // reserving the places first is what keeps the configured ceiling exact and keeps the
                // replacement out of the eviction candidates. The family being rotated cannot be PRUNED here,
                // because classification has already established that its absolute ceiling is still ahead.
                //
                // TWO places, not one, and the second is not slack. A rotation ordinarily adds one net
                // generation - the consumed entry replaces an existing key and the replacement is new - but
                // eviction is free to reclaim the presented generation itself when it is among the oldest, and
                // then BOTH writes below are additions rather than one. Reserving one place would leave the
                // tracked set a single entry above the ceiling in exactly that case. The cost of reserving the
                // second is that one further old family is retired during a rotation performed at capacity,
                // which is a path an ordinary deployment never reaches at all.
                PruneExpired(now);
                EnforceCapacity(headroom: 2);

                // The presented generation is marked consumed and KEPT. Its digest, paired with the client
                // fingerprint that spent it, is what distinguishes a near-simultaneous same-client retry
                // from a replay arriving from somewhere else.
                _tokens[digest] = stored with
                {
                    ConsumedAtUtc = now,
                    ConsumedClientDigest = (byte[])clientDigest.Clone(),
                };

                _tokens[replacement.Digest] = stored with
                {
                    Generation = checked(stored.Generation + 1),
                    ExpiresAtUtc = replacementExpiresAtUtc,
                    ConsumedAtUtc = null,
                    ConsumedClientDigest = null,
                    RevokedAtUtc = null,
                };

                return Task.FromResult(RefreshTokenRotationResult.Succeeded(
                    replacement.RawToken,
                    replacementExpiresAtUtc,
                    new RefreshTokenSubject(stored.UserId, stored.PortalId)));
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(digest);
            CryptographicOperations.ZeroMemory(clientDigest);
        }
    }

    /// <inheritdoc />
    public Task<RefreshTokenOutcome> RevokeAsync(
        string refreshToken,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(refreshToken);
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(refreshToken))
        {
            return Task.FromResult(RefreshTokenOutcome.Unknown);
        }

        byte[] digest = Digest(refreshToken);
        DateTime now = Utc(_clock.UtcNow);

        try
        {
            lock (_gate)
            {
                if (!_tokens.TryGetValue(digest, out StoredToken? stored))
                {
                    return Task.FromResult(RefreshTokenOutcome.Unknown);
                }

                int changed = RevokeFamilyCore(stored.FamilyId, now);
                return Task.FromResult(changed == 0
                    ? RefreshTokenOutcome.AlreadyRevoked
                    : RefreshTokenOutcome.Succeeded);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(digest);
        }
    }

    /// <inheritdoc />
    public Task<RefreshTokenOutcome> RevokeAllForUserAsync(
        int userId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        DateTime now = Utc(_clock.UtcNow);

        lock (_gate)
        {
            bool known = false;
            foreach (StoredToken candidate in _tokens.Values)
            {
                if (candidate.UserId == userId)
                {
                    known = true;
                    break;
                }
            }

            if (!known)
            {
                return Task.FromResult(RefreshTokenOutcome.Unknown);
            }

            int changed = RevokeAllForUserCore(userId, now);
            return Task.FromResult(changed == 0
                ? RefreshTokenOutcome.AlreadyRevoked
                : RefreshTokenOutcome.Succeeded);
        }
    }

    /// <summary>Decides what a presented generation is, without changing anything.</summary>
    /// <param name="stored">The generation the presented token addresses.</param>
    /// <param name="clientDigest">Fingerprint of the client presenting it.</param>
    /// <param name="now">The instant to judge against.</param>
    /// <param name="concurrentUseGrace">
    /// Window in which the same client may re-present a spent generation without it counting as a replay.
    /// </param>
    /// <returns>The outcome the caller must act on.</returns>
    /// <remarks>
    /// Static, and the grace arrives as a parameter rather than being read from the instance, so this decision
    /// is a pure function of the four values named above. That is what lets it run outside the dictionary
    /// write while remaining reproducible - and it stayed static when the grace became configuration, because
    /// a rule that reads mutable instance state is a rule two callers can disagree about.
    /// </remarks>
    private static RefreshTokenOutcome Classify(
        StoredToken stored,
        ReadOnlySpan<byte> clientDigest,
        DateTime now,
        TimeSpan concurrentUseGrace)
    {
        if (stored.RevokedAtUtc is not null)
        {
            return RefreshTokenOutcome.Revoked;
        }

        if (stored.FamilyExpiresAtUtc <= now)
        {
            return RefreshTokenOutcome.Expired;
        }

        if (stored.ConsumedAtUtc is not null)
        {
            if (stored.ConsumedClientDigest is not null
                && concurrentUseGrace > TimeSpan.Zero
                && now >= stored.ConsumedAtUtc.Value
                && now - stored.ConsumedAtUtc.Value <= concurrentUseGrace
                && CryptographicOperations.FixedTimeEquals(
                    stored.ConsumedClientDigest,
                    clientDigest))
            {
                return RefreshTokenOutcome.ConcurrentUse;
            }

            // A consumed fingerprint remains a replay signal until the family's absolute ceiling,
            // even after that generation's own sliding expiry. Checking the per-generation expiry
            // first would recreate the detection gap SEC-039 removed.
            return RefreshTokenOutcome.AlreadyUsed;
        }

        return stored.ExpiresAtUtc <= now
            ? RefreshTokenOutcome.Expired
            : RefreshTokenOutcome.Succeeded;
    }

    /// <summary>Marks every unrevoked generation of one family revoked.</summary>
    /// <param name="familyId">The family to revoke.</param>
    /// <param name="now">The revocation instant.</param>
    /// <returns>How many generations this call changed.</returns>
    /// <remarks>Callers must already hold <see cref="_gate"/>.</remarks>
    private int RevokeFamilyCore(Guid familyId, DateTime now) =>
        RevokeWhere(stored => stored.FamilyId == familyId, now);

    /// <summary>Marks every unrevoked generation belonging to one account revoked.</summary>
    /// <param name="userId">The account whose families are revoked.</param>
    /// <param name="now">The revocation instant.</param>
    /// <returns>How many generations this call changed.</returns>
    /// <remarks>Callers must already hold <see cref="_gate"/>.</remarks>
    private int RevokeAllForUserCore(int userId, DateTime now) =>
        RevokeWhere(stored => stored.UserId == userId, now);

    /// <summary>Marks every unrevoked generation matching a predicate revoked.</summary>
    /// <param name="match">Selects the generations to revoke.</param>
    /// <param name="now">The revocation instant.</param>
    /// <returns>How many generations this call changed.</returns>
    /// <remarks>
    /// The keys are collected before any entry is replaced, because a dictionary cannot be written to while
    /// it is being enumerated. Callers must already hold <see cref="_gate"/>.
    /// </remarks>
    private int RevokeWhere(Func<StoredToken, bool> match, DateTime now)
    {
        List<byte[]>? affected = null;

        foreach (KeyValuePair<byte[], StoredToken> entry in _tokens)
        {
            if (entry.Value.RevokedAtUtc is null && match(entry.Value))
            {
                (affected ??= []).Add(entry.Key);
            }
        }

        if (affected is null)
        {
            return 0;
        }

        foreach (byte[] key in affected)
        {
            _tokens[key] = _tokens[key] with { RevokedAtUtc = now };
        }

        return affected.Count;
    }

    /// <summary>Discards every generation whose family has passed its absolute ceiling.</summary>
    /// <param name="now">The instant to prune against.</param>
    /// <remarks>
    /// <para>
    /// Bounded by the family lifetime rather than by a batch size: a family past its ceiling can never be
    /// redeemed and is no longer a theft signal, so keeping it would only grow the dictionary. Callers must
    /// already hold <see cref="_gate"/>.
    /// </para>
    /// <para>
    /// Expiry ONLY. This method used to enforce the capacity ceiling as well, and the two were separated
    /// because the order they need differs: reclamation must run before a mutation, so that dead state is
    /// counted out first, whereas the ceiling must be applied with a place reserved for what is about to be
    /// written. Each caller now performs both explicitly and in that order.
    /// </para>
    /// </remarks>
    private void PruneExpired(DateTime now)
    {
        List<byte[]>? expired = null;

        foreach (KeyValuePair<byte[], StoredToken> entry in _tokens)
        {
            if (entry.Value.FamilyExpiresAtUtc <= now)
            {
                (expired ??= []).Add(entry.Key);
            }
        }

        if (expired is not null)
        {
            foreach (byte[] key in expired)
            {
                _tokens.Remove(key);
            }
        }
    }

    /// <summary>
    /// Evicts the generations nearest their family ceiling until the tracked set fits the configured ceiling,
    /// with room left for the generations the caller is about to write.
    /// </summary>
    /// <param name="headroom">
    /// How many generations the caller will add immediately after this call. Reserving them here is what makes
    /// the configured ceiling exact and keeps a generation being written out of the eviction candidates.
    /// </param>
    /// <remarks>
    /// <para>
    /// Runs only once expiry-based pruning has already reclaimed everything it can, so an ordinary deployment
    /// never reaches it. THE UNIT OF EVICTION IS THE FAMILY, not the generation, and that is a security
    /// property rather than a tidiness one.
    /// </para>
    /// <para>
    /// MIGRATION: an earlier revision ordered the individual generations by family ceiling and then by
    /// generation, took exactly the excess, and claimed in this very remark that doing so "keeps a family's
    /// generations together". It does not: the cut lands wherever the excess count happens to fall, so
    /// whenever that boundary fell inside a family's run of generations, the family was left HALF-TRACKED.
    /// That is precisely the state in which theft detection stops working while the family goes on being
    /// redeemable. Reuse of a superseded generation is what identifies a stolen token, and this store answers
    /// it by revoking the whole family; a generation that has been evicted is indistinguishable from one that
    /// never existed, so presenting a stolen older generation reads as an unknown token - refused, but with no
    /// family revocation - and the thief's live generation survives. Evicting whole families instead means the
    /// worst outcome under memory pressure is that a family must sign in again, which is the outcome eviction
    /// is FOR.
    /// </para>
    /// <para>
    /// Families are retired nearest-ceiling first, tie-broken on the family identifier so the choice is
    /// deterministic rather than dependent on dictionary ordering. Eviction continues until the tracked set
    /// fits, which can leave the set slightly further under the ceiling than the excess required - removing a
    /// family entire is the point, and stopping mid-family to hit an exact count would reintroduce the fault.
    /// Callers must already hold <see cref="_gate"/>.
    /// </para>
    /// </remarks>
    private void EnforceCapacity(int headroom = 0)
    {
        // THE CEILING IS THE CONFIGURED ONE, AND THE HEADROOM IS RESERVED BEFORE THE WRITE. Reclaiming to
        // the bare ceiling and then writing would exceed it by exactly the number of generations the caller
        // is about to add, which is the defect that surfaced when the ceiling became a setting: the issue
        // path enforced before insertion and the rotation path after its writes.
        int effectiveCeiling = _maximumTrackedTokens - headroom;

        if (_tokens.Count - effectiveCeiling <= 0)
        {
            return;
        }

        IReadOnlyList<byte[]> evictions = SelectCapacityEvictions(
            _tokens.Select(entry => (entry.Key, entry.Value.FamilyId, entry.Value.FamilyExpiresAtUtc)),
            effectiveCeiling);

        foreach (byte[] key in evictions)
        {
            _tokens.Remove(key);
        }
    }

    /// <summary>
    /// Chooses which tracked entries to discard so that the tracked set fits a ceiling, retiring whole
    /// rotation families rather than individual generations.
    /// </summary>
    /// <typeparam name="TKey">The type identifying one tracked generation.</typeparam>
    /// <param name="tracked">Every tracked generation, with the family it belongs to and that family's ceiling.</param>
    /// <param name="maximumTracked">The number of generations the set may hold.</param>
    /// <returns>
    /// The keys to discard, which may be empty. Never a partial family: the result contains either every
    /// generation of a family or none of them.
    /// </returns>
    /// <remarks>
    /// <para>
    /// Separated from <see cref="EnforceCapacity"/> and made pure so the policy can be exercised directly. The
    /// ceiling this store enforces is a hundred thousand generations, so a behavioural test of the eviction
    /// path would have to issue that many tokens through a store that rescans its whole dictionary on every
    /// issue - quadratic work for a policy that is three ordering decisions. Extracting the decision is what
    /// lets those three decisions be asserted with a handful of synthetic families instead, in the same way
    /// <c>DuplicateKeyTranslator</c> and <c>LostUpdateTranslator</c> are asserted.
    /// </para>
    /// <para>
    /// The parameter is a projection rather than the stored records, so this method needs nothing of the
    /// private token shape and cannot come to depend on it.
    /// </para>
    /// </remarks>
    internal static IReadOnlyList<TKey> SelectCapacityEvictions<TKey>(
        IEnumerable<(TKey Key, Guid FamilyId, DateTime FamilyExpiresAtUtc)> tracked,
        int maximumTracked)
    {
        ArgumentNullException.ThrowIfNull(tracked);

        // Materialised once: the count is needed before the grouping is walked, and the source is a lazy
        // projection over a dictionary the caller is about to modify.
        (TKey Key, Guid FamilyId, DateTime FamilyExpiresAtUtc)[] entries = [.. tracked];

        int remaining = entries.Length;
        if (remaining - maximumTracked <= 0)
        {
            return [];
        }

        // Grouped FIRST, so the ordering sorts families rather than generations. A family's ceiling is shared
        // by all of its generations, but it is taken as the minimum rather than assumed, which keeps the
        // ordering total even if a future revision ever shortens a ceiling for part of a family. The family
        // identifier is the tie-break, so the choice is deterministic rather than dependent on the order a
        // dictionary happened to enumerate in.
        var families = entries
            .GroupBy(entry => entry.FamilyId)
            .Select(family => new
            {
                FamilyId = family.Key,
                Ceiling = family.Min(entry => entry.FamilyExpiresAtUtc),
                Keys = family.Select(entry => entry.Key).ToArray(),
            })
            .OrderBy(family => family.Ceiling)
            .ThenBy(family => family.FamilyId);

        List<TKey> evictions = [];

        foreach (var family in families)
        {
            if (remaining - maximumTracked <= 0)
            {
                break;
            }

            evictions.AddRange(family.Keys);
            remaining -= family.Keys.Length;
        }

        return evictions;
    }

    private static TokenMaterial CreateToken()
    {
        byte[] random = RandomNumberGenerator.GetBytes(TokenEntropyBytes);
        try
        {
            string raw = Convert.ToBase64String(random)
                .TrimEnd('=')
                .Replace('+', '-')
                .Replace('/', '_');
            return new TokenMaterial(raw, Digest(raw));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(random);
        }
    }

    private static byte[] Digest(string value)
    {
        byte[] source = Encoding.UTF8.GetBytes(value);
        try
        {
            return SHA256.HashData(source);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(source);
        }
    }

    private static DateTime Earlier(DateTime left, DateTime right) => left <= right ? left : right;

    private static DateTime Utc(DateTime value) => value.Kind == DateTimeKind.Utc
        ? value
        : DateTime.SpecifyKind(value, DateTimeKind.Utc);

    private readonly record struct TokenMaterial(string RawToken, byte[] Digest);

    /// <summary>How much of this store's tracked-generation capacity is in use.</summary>
    /// <param name="TrackedGenerations">
    /// Live and spent generations currently held. A spent generation counts, because it is retained as the
    /// signal that makes a replay recognisable and it occupies capacity until its family's ceiling passes.
    /// </param>
    /// <param name="Ceiling">The configured maximum, above which the oldest families are retired early.</param>
    /// <remarks>
    /// A value type carrying two integers, returned by <see cref="DescribeCapacity"/> under the store's own
    /// monitor. It deliberately carries no token, no digest, no account and no tenant: the health probe that
    /// consumes it renders its description into a log, and a probe that could name an account would put
    /// identity into an operational diagnostic.
    /// </remarks>
    internal readonly record struct CapacitySnapshot(int TrackedGenerations, int Ceiling);

    /// <summary>One stored generation of one refresh family.</summary>
    /// <param name="FamilyId">Identifies the rotation family every generation belongs to.</param>
    /// <param name="Generation">Zero-based position of this token within its family.</param>
    /// <param name="UserId">The account the family represents.</param>
    /// <param name="PortalId">The tenant the family represents.</param>
    /// <param name="ExpiresAtUtc">This generation's own sliding expiry.</param>
    /// <param name="FamilyExpiresAtUtc">The family's absolute ceiling, shared by every generation.</param>
    /// <param name="ConsumedAtUtc">When this generation was spent, or <see langword="null"/> while live.</param>
    /// <param name="ConsumedClientDigest">
    /// The bounded client fingerprint that spent it, present exactly when
    /// <paramref name="ConsumedAtUtc"/> is.
    /// </param>
    /// <param name="RevokedAtUtc">When this generation was revoked, or <see langword="null"/>.</param>
    /// <remarks>
    /// A record so that a transition is expressed as a replacement rather than as a mutation: an entry a
    /// caller is holding cannot be changed underneath it, which is what lets classification run outside the
    /// dictionary write.
    /// </remarks>
    private sealed record StoredToken(
        Guid FamilyId,
        int Generation,
        int UserId,
        int PortalId,
        DateTime ExpiresAtUtc,
        DateTime FamilyExpiresAtUtc,
        DateTime? ConsumedAtUtc,
        byte[]? ConsumedClientDigest,
        DateTime? RevokedAtUtc);

    /// <summary>Compares fixed-length SHA-256 digests by value.</summary>
    /// <remarks>
    /// Required because the dictionary is keyed by <see cref="byte"/> arrays, whose default comparer is
    /// reference identity - which would make every lookup miss. Equality is constant-time over the whole
    /// digest, and the hash code is taken from the leading four bytes of a value that is already a
    /// uniformly distributed hash.
    /// </remarks>
    private sealed class DigestComparer : IEqualityComparer<byte[]>
    {
        /// <summary>The single shared instance.</summary>
        public static readonly DigestComparer Instance = new();

        private DigestComparer()
        {
        }

        /// <inheritdoc />
        public bool Equals(byte[]? x, byte[]? y)
        {
            if (ReferenceEquals(x, y))
            {
                return true;
            }

            return x is not null
                && y is not null
                && CryptographicOperations.FixedTimeEquals(x, y);
        }

        /// <inheritdoc />
        public int GetHashCode(byte[] obj)
        {
            ArgumentNullException.ThrowIfNull(obj);

            return obj.Length >= sizeof(int)
                ? BitConverter.ToInt32(obj, 0)
                : obj.Length;
        }
    }
}
