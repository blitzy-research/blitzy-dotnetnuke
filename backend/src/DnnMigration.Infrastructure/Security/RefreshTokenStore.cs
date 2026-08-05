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
    private const int ConcurrentUseGraceSeconds = 5;

    /// <summary>
    /// Hard ceiling on how many generations this store tracks at once.
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
    /// 100,000 entries is roughly ten megabytes of tracked state, and it is far above any legitimate working
    /// set: one caller refreshing every half hour for the full thirty-day family lifetime accounts for about
    /// 1,400 entries. When the ceiling is reached the families closest to their absolute ceiling are evicted
    /// first, so the cost of the bound is that the OLDEST refresh families stop refreshing and their holders
    /// sign in again - a bounded availability cost, never a failure to serve.
    /// </para>
    /// </remarks>
    private const int MaximumTrackedTokens = 100_000;

    private static readonly TimeSpan ConcurrentUseGrace =
        TimeSpan.FromSeconds(ConcurrentUseGraceSeconds);

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

    /// <summary>Initialises a new instance of the <see cref="RefreshTokenStore"/> class.</summary>
    /// <param name="clock">UTC clock used for every lifecycle decision.</param>
    /// <param name="jwtOptions">Validated access and refresh-token options.</param>
    /// <exception cref="ArgumentNullException">Any argument is <see langword="null"/>.</exception>
    /// <exception cref="OptionsValidationException">The token lifetimes are invalid.</exception>
    /// <remarks>
    /// Both dependencies are themselves singletons, so this type may be one: it captures no request-scoped
    /// service and holds no database context. The database context the SQL implementation took - solely to
    /// read a connection string off it - is gone with the SQL, which is what removes the captive-dependency
    /// problem that forced the scoped registrations AAP section 0.4.3 forbids.
    /// </remarks>
    public RefreshTokenStore(
        IClock clock,
        IOptions<JwtOptions> jwtOptions)
    {
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(jwtOptions);

        IReadOnlyList<string> failures = jwtOptions.Value.Validate();
        if (failures.Count != 0)
        {
            throw new OptionsValidationException(
                JwtOptions.SectionName,
                typeof(JwtOptions),
                failures);
        }

        _clock = clock;
        _slidingLifetime = TimeSpan.FromDays(jwtOptions.Value.RefreshTokenExpirationDays);
        _familyLifetime = TimeSpan.FromDays(jwtOptions.Value.RefreshTokenAbsoluteExpirationDays);
    }

    /// <inheritdoc />
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

                RefreshTokenOutcome outcome = Classify(stored, clientDigest, now);
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

                RefreshTokenOutcome outcome = Classify(stored, clientDigest, now);
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

                PruneExpired(now);

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

    private static RefreshTokenOutcome Classify(
        StoredToken stored,
        ReadOnlySpan<byte> clientDigest,
        DateTime now)
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
                && now >= stored.ConsumedAtUtc.Value
                && now - stored.ConsumedAtUtc.Value <= ConcurrentUseGrace
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
    /// Bounded by the family lifetime rather than by a batch size: a family past its ceiling can never be
    /// redeemed and is no longer a theft signal, so keeping it would only grow the dictionary. Callers must
    /// already hold <see cref="_gate"/>.
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

        EnforceCapacity();
    }

    /// <summary>
    /// Evicts the generations nearest their family ceiling until the tracked set fits
    /// <see cref="MaximumTrackedTokens"/>.
    /// </summary>
    /// <remarks>
    /// Runs only once expiry-based pruning has already reclaimed everything it can, so an ordinary
    /// deployment never reaches it. Ordering by the family ceiling and then by generation makes the eviction
    /// deterministic and keeps a family's generations together, so a family is retired as a unit rather than
    /// left half-tracked. Callers must already hold <see cref="_gate"/>.
    /// </remarks>
    private void EnforceCapacity()
    {
        int excess = _tokens.Count - MaximumTrackedTokens;
        if (excess <= 0)
        {
            return;
        }

        byte[][] evictions = _tokens
            .OrderBy(entry => entry.Value.FamilyExpiresAtUtc)
            .ThenBy(entry => entry.Value.Generation)
            .Take(excess)
            .Select(entry => entry.Key)
            .ToArray();

        foreach (byte[] key in evictions)
        {
            _tokens.Remove(key);
        }
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
