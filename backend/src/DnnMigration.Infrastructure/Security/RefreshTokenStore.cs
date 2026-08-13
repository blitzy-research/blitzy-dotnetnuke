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
/// Legacy <c>FormsAuthentication</c> sign-out had no server-side session record at all - the ticket was a
/// self-contained cookie and revoking one was impossible. The target keeps refresh state in this singleton
/// for the lifetime of the process while access-token sign-out remains expiry plus client-side discard.
/// </para>
/// <para>
/// <strong>The operational consequence is stated rather than hidden.</strong> Refresh state does not
/// survive a process restart and is not shared between replicas, so a restart or a load-balanced second
/// instance forces callers to sign in again rather than refresh.
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
    private readonly Dictionary<byte[], StoredToken> _tokens = new(DigestComparer.Instance);

    private readonly IClock _clock;
    private readonly TimeSpan _slidingLifetime;
    private readonly TimeSpan _familyLifetime;

    /// <summary>
    /// Ceiling on how many generations this store tracks at once, from
    /// <c>RefreshTokenStore:MaximumTrackedTokens</c>.
    /// </summary>
    /// <remarks>
    /// A bound is mandatory rather than defensive. The SQL implementation this replaces was bounded by disk
    /// and pruned in batches; an unbounded in-process dictionary is a memory-exhaustion vector, because a
    /// caller holding valid credentials adds one entry per sign-in and two per rotation and nothing expires
    /// for the whole family lifetime.
    /// </remarks>
    private readonly int _maximumTrackedTokens;

    /// <summary>
    /// Window in which the same client may present an already-spent generation without it being treated as
    /// a replay, from <c>RefreshTokenStore:ConcurrentUseGraceSeconds</c>.
    /// </summary>
    private readonly TimeSpan _concurrentUseGrace;

    /// <summary>
    /// How long a REVOKED generation is retained before reclamation erases it, from
    /// <c>RefreshTokenStore:RevokedRecordRetentionHours</c>.
    /// </summary>
    private readonly TimeSpan _revokedRetention;

    /// <summary>Initialises a new instance of the <see cref="RefreshTokenStore"/> class.</summary>
    /// <param name="clock">UTC clock used for every lifecycle decision.</param>
    /// <param name="jwtOptions">Validated access and refresh-token options.</param>
    /// <param name="storeOptions">Validated store shape: capacity and concurrent-use grace.</param>
    /// <exception cref="ArgumentNullException">Any argument is <see langword="null"/>.</exception>
    /// <exception cref="OptionsValidationException">
    /// The token lifetimes or the store settings are invalid.
    /// </exception>
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
        _revokedRetention = TimeSpan.FromHours(storeOptions.Value.RevokedRecordRetentionHours);
    }

    /// <summary>
    /// Reports how much of this store's tracked-generation capacity is in use, for the health probe.
    /// </summary>
    /// <returns>The tracked count and the ceiling it is measured against.</returns>
    internal CapacitySnapshot DescribeCapacity()
    {
        lock (_gate)
        {
            return new CapacitySnapshot(_tokens.Count, _maximumTrackedTokens);
        }
    }

    /// <inheritdoc />
    /// <inheritdoc />
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
            // what is already there.
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

                // Reclaimed and bounded BEFORE the two writes below, for the reason given on the issue
                // path: reserving the places first is what keeps the configured ceiling exact and keeps the
                // replacement out of the eviction candidates.
                PruneExpired(now);
                EnforceCapacity(headroom: 2);

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

    /// <inheritdoc />
    /// <remarks>
    /// PRIV-02. Removes the entries outright rather than stamping them, which is the distinction this
    /// member exists for: a sign-out revokes, a deletion erases.
    /// </remarks>
    public Task<RefreshTokenPurgeResult> PurgeSubjectAsync(
        RefreshTokenPurgeScope scope,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            List<byte[]>? matched = null;

            foreach (KeyValuePair<byte[], StoredToken> entry in _tokens)
            {
                if (scope.Includes(entry.Value.UserId, entry.Value.PortalId))
                {
                    (matched ??= []).Add(entry.Key);
                }
            }

            if (matched is null)
            {
                return Task.FromResult(RefreshTokenPurgeResult.NothingHeld());
            }

            foreach (byte[] key in matched)
            {
                _tokens.Remove(key);

                // The key is the digest of a refresh token. The dictionary no longer references it, so this is
                // the last moment at which its bytes can be cleared deliberately.
                CryptographicOperations.ZeroMemory(key);
            }

            return Task.FromResult(RefreshTokenPurgeResult.Removed(matched.Count));
        }
    }

    /// <inheritdoc />
    public Task<RefreshTokenPurgeResult> PurgeRetiredAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        DateTime now = Utc(_clock.UtcNow);

        lock (_gate)
        {
            return Task.FromResult(RefreshTokenPurgeResult.Removed(PruneExpired(now)));
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

            // A consumed fingerprint remains a replay signal until the family's absolute ceiling, even
            // after that generation's own sliding expiry. Checking the per-generation expiry first would
            // recreate the detection gap removed.
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
    private int RevokeFamilyCore(Guid familyId, DateTime now) =>
        RevokeWhere(stored => stored.FamilyId == familyId, now);

    /// <summary>Marks every unrevoked generation belonging to one account revoked.</summary>
    /// <param name="userId">The account whose families are revoked.</param>
    /// <param name="now">The revocation instant.</param>
    /// <returns>How many generations this call changed.</returns>
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

    /// <summary>
    /// Discards every generation whose family has passed its absolute ceiling, and every revoked generation
    /// held beyond the configured retention.
    /// </summary>
    /// <param name="now">The instant to prune against.</param>
    /// <returns>How many generations were discarded.</returns>
    /// <remarks>
    /// Bounded by the family lifetime rather than by a batch size: a family past its ceiling can never be
    /// redeemed and is no longer a theft signal, so keeping it would only grow the dictionary. Callers must
    /// already hold <see cref="_gate"/>.
    /// </remarks>
    private int PruneExpired(DateTime now)
    {
        // PRIV-02. Anything revoked at or before this instant has outlived the replay signal it was kept for.
        DateTime revokedBefore = now - _revokedRetention;
        List<byte[]>? expired = null;

        foreach (KeyValuePair<byte[], StoredToken> entry in _tokens)
        {
            bool familyOver = entry.Value.FamilyExpiresAtUtc <= now;

            // PRIV-02.
            bool retentionElapsed = entry.Value.RevokedAtUtc is { } revokedAt && revokedAt <= revokedBefore;

            if (familyOver || retentionElapsed)
            {
                (expired ??= []).Add(entry.Key);
            }
        }

        if (expired is null)
        {
            return 0;
        }

        foreach (byte[] key in expired)
        {
            _tokens.Remove(key);
        }

        return expired.Count;
    }

    /// <summary>
    /// Evicts the generations nearest their family ceiling until the tracked set fits the configured
    /// ceiling, with room left for the generations the caller is about to write.
    /// </summary>
    /// <param name="headroom">How many generations the caller will add immediately after this call.</param>
    /// <remarks>
    /// Runs only once expiry-based pruning has already reclaimed everything it can, so an ordinary
    /// deployment never reaches it. THE UNIT OF EVICTION IS THE FAMILY, not the generation, and that is a
    /// security property rather than a tidiness one.
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
    /// <param name="tracked">
    /// Every tracked generation, with the family it belongs to and that family's ceiling.
    /// </param>
    /// <param name="maximumTracked">The number of generations the set may hold.</param>
    /// <returns>The keys to discard, which may be empty.</returns>
    /// <remarks>
    /// Separated from <see cref="EnforceCapacity"/> and made pure so the policy can be exercised directly.
    /// The ceiling this store enforces is a hundred thousand generations, so a behavioural test of the
    /// eviction path would have to issue that many tokens through a store that rescans its whole dictionary
    /// on every issue - quadratic work for a policy that is three ordering decisions.
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

        // Grouped FIRST, so the ordering sorts families rather than generations. A family's ceiling is
        // shared by all of its generations, but it is taken as the minimum rather than assumed, which keeps
        // the ordering total even if a future revision ever shortens a ceiling for part of a family.
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
    /// <param name="TrackedGenerations">Live and spent generations currently held.</param>
    /// <param name="Ceiling">The configured maximum, above which the oldest families are retired early.</param>
    /// <remarks>
    /// A value type carrying two integers, returned by <see cref="DescribeCapacity"/> under the store's own
    /// monitor. It deliberately carries no token, no digest, no account and no tenant: the health probe
    /// that consumes it renders its description into a log, and a probe that could name an account would
    /// put identity into an operational diagnostic.
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
    /// The bounded client fingerprint that spent it, present exactly when <paramref name="ConsumedAtUtc"/>
    /// is.
    /// </param>
    /// <param name="RevokedAtUtc">When this generation was revoked, or <see langword="null"/>.</param>
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
