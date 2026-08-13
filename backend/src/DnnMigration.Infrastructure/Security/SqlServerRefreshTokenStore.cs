using System.Buffers;
using System.Data;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using DnnMigration.Application.Options;
using DnnMigration.Domain.Abstractions.Services;
using DnnMigration.Domain.Enums;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DnnMigration.Infrastructure.Security;

/// <summary>
/// Refresh-token store whose families live in a SQL Server table shared by every replica and outliving
/// every process, with the same single-use rotation, family revocation and concurrent-use grace semantics
/// as the process-local store it replaces.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why this type exists.</strong> A refresh-token family is the only server-side session record
/// this API keeps, so sign-out and replay defence are exactly as strong as the store's reach. Held in one
/// process, a revocation performed on replica A is invisible to replica B and a restart forgets every
/// family it ever issued - which means a token a caller still holds may be honoured by a replica that
/// never learned it was retired. This store removes that class of failure by putting the families
/// somewhere both replicas and both sides of a restart can see.
/// </para>
/// <para>
/// <strong>It does not touch the DotNetNuke schema, and cannot.</strong> The catalogue is named by
/// <see cref="RefreshTokenStoreOptions.ConnectionString"/>, which
/// <see cref="RefreshTokenStoreOptions.Validate"/> refuses to accept when it addresses the same database
/// as the application's own connection. AAP rule T4 freezes the existing schema; session state is
/// therefore held beside it rather than in it, in a catalogue the operator provisions for the purpose.
/// </para>
/// <para>
/// <strong>Every state transition is one serialisable transaction.</strong> The row is read with
/// <c>UPDLOCK, HOLDLOCK</c>, classified, and updated inside the same transaction, so two exchanges racing
/// on one token produce exactly one successor - the guarantee the single monitor in the process-local
/// store provides within one process, obtained here across all of them. Classification stays in managed
/// code so that the two stores share one definition of what an outcome means; only the storage moved.
/// </para>
/// <para>
/// <strong>Only digests are stored.</strong> The raw refresh token is returned to its caller once and
/// retained nowhere: the table holds a SHA-256 digest of the token and a digest of the client binding,
/// which is all the classification needs. A reader of the table therefore cannot mint a session.
/// </para>
/// </remarks>
internal sealed class SqlServerRefreshTokenStore : IRefreshTokenStore
{
    private const int TokenEntropyBytes = 32;

    private readonly IClock _clock;
    private readonly ILogger<SqlServerRefreshTokenStore> _logger;
    private readonly RefreshTokenStoreOptions _options;
    private readonly TimeSpan _slidingLifetime;
    private readonly TimeSpan _familyLifetime;

    /// <summary>
    /// The concurrent-use grace, taken from the VALIDATED settings rather than from a compiled constant.
    /// </summary>
    /// <remarks>
    /// MIGRATION: SEC-04. Both this and the capacity ceiling used to be <c>const</c> members of this type, so
    /// <c>RefreshTokenStore:ConcurrentUseGraceSeconds</c> and
    /// <c>RefreshTokenStore:MaximumTrackedTokens</c> governed the process-local store and were silently
    /// ignored here. A deployment that switched to the durable provider therefore had its configured session
    /// policy replaced by two numbers it could not see, which is the worst shape of a configuration defect: no
    /// error, no log line, and a security policy quietly different from the one on file.
    /// </remarks>
    private readonly TimeSpan _concurrentUseGrace;

    private readonly int _maximumTrackedTokens;

    /// <summary>
    /// How long a REVOKED row is retained before reclamation deletes it, from
    /// <c>RefreshTokenStore:RevokedRecordRetentionHours</c>.
    /// </summary>
    /// <remarks>
    /// PRIV-02. Reclamation here used to delete on expiry alone, so a revoked row - the account, the tenant and
    /// the token digest of a session that had already ended - stayed in the table until its family's absolute
    /// ceiling elapsed. The window is what the row is kept FOR: a revoked digest is the signal that makes a
    /// replay of that family recognisable rather than an unknown value. Past it, the signal is worthless and the
    /// row is only personal data.
    /// </remarks>
    private readonly TimeSpan _revokedRetention;
    private readonly SemaphoreSlim _readinessGate = new(1, 1);
    private bool _tableConfirmed;

    /// <summary>Initialises a new instance of the <see cref="SqlServerRefreshTokenStore"/> class.</summary>
    /// <param name="clock">The injected clock, so lifetimes are testable.</param>
    /// <param name="jwtOptions">Token lifetimes, validated on construction.</param>
    /// <param name="storeOptions">The store's own catalogue and table settings.</param>
    /// <param name="logger">Diagnostics route for store faults.</param>
    /// <exception cref="ArgumentNullException">Any argument is <see langword="null"/>.</exception>
    public SqlServerRefreshTokenStore(
        IClock clock,
        IOptions<JwtOptions> jwtOptions,
        IOptions<RefreshTokenStoreOptions> storeOptions,
        ILogger<SqlServerRefreshTokenStore> logger)
    {
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(jwtOptions);
        ArgumentNullException.ThrowIfNull(storeOptions);
        ArgumentNullException.ThrowIfNull(logger);

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
            // The settings this store now READS are validated before it is usable, exactly as the token
            // lifetimes above are. Constructing over unusable settings would defer the failure to the first
            // sign-in, which is the outcome start-up validation exists to prevent.
            throw new OptionsValidationException(
                RefreshTokenStoreOptions.SectionName,
                typeof(RefreshTokenStoreOptions),
                storeFailures);
        }

        _clock = clock;
        _logger = logger;
        _options = storeOptions.Value;
        _slidingLifetime = TimeSpan.FromDays(jwtOptions.Value.RefreshTokenExpirationDays);
        _familyLifetime = TimeSpan.FromDays(jwtOptions.Value.RefreshTokenAbsoluteExpirationDays);
        _concurrentUseGrace = TimeSpan.FromSeconds(_options.ConcurrentUseGraceSeconds);
        _maximumTrackedTokens = _options.MaximumTrackedTokens;
        _revokedRetention = TimeSpan.FromHours(_options.RevokedRecordRetentionHours);
    }

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// <see langword="true"/>: every replica reads and writes this one table, so a family absent from it is
    /// absent from the whole deployment and a revocation that matches nothing has genuinely left nothing
    /// exchangeable.
    /// </para>
    /// <para>
    /// ⚠ WHY AN UNCONDITIONAL <see langword="true"/> IS SOUND, AND WHAT IT RESTS ON. Its one consumer treats
    /// <c>Unknown</c> as a successful revocation when this property holds - "there was nothing to retire" - so
    /// a store that answered <c>Unknown</c> because it could not REACH its table would turn an unperformed
    /// revocation into a reported success, which is the worst failure available to a session store. That cannot
    /// happen here, and the reason is structural rather than incidental: every operation converts an
    /// unreachable catalogue, an unprovisioned table and every provider fault into
    /// <see cref="RefreshTokenOutcome.StoreUnavailable"/>, never into <c>Unknown</c>, so absence is only ever
    /// reported when the table was read and held nothing. That is the invariant this property depends on, and it
    /// is the invariant the readiness confirmation and the uniform fault handling on all four operations exist
    /// to maintain.
    /// </para>
    /// <para>
    /// It answers a question about the store's SHAPE - is this state shared - and deliberately not about the
    /// catalogue's current availability. Availability changes between two calls and belongs to the health probe,
    /// which reports it as a named degradation; folding it into this property would make a caller's revocation
    /// decision depend on a value that was already stale when it read it.
    /// </para>
    /// </remarks>
    public bool IsAuthoritativeAcrossReplicas => true;

    /// <inheritdoc />
    public async Task<RefreshTokenIssueResult> IssueAsync(
        RefreshTokenSubject subject,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(subject);
        cancellationToken.ThrowIfCancellationRequested();

        DateTime now = Utc(_clock.UtcNow);
        DateTime familyExpiresAtUtc = now.Add(_familyLifetime);
        DateTime expiresAtUtc = Earlier(now.Add(_slidingLifetime), familyExpiresAtUtc);
        TokenMaterial token = CreateToken();

        try
        {
            await using SqlConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            await using SqlTransaction transaction = (SqlTransaction)await connection
                .BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken)
                .ConfigureAwait(false);

            // Expiry first, then capacity, then the write - the order the process-local store uses, and the
            // one that makes the configured ceiling exact. One place is reserved because one row follows.
            await PruneAsync(connection, transaction, now, cancellationToken).ConfigureAwait(false);

            if (!await EnforceCapacityAsync(connection, transaction, headroom: 1, cancellationToken)
                .ConfigureAwait(false))
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);

                return RefreshTokenIssueResult.Failed(RefreshTokenOutcome.CapacityExhausted);
            }

            await InsertAsync(
                connection,
                transaction,
                token.Digest,
                Guid.NewGuid(),
                generation: 0,
                subject.UserId,
                subject.PortalId,
                expiresAtUtc,
                familyExpiresAtUtc,
                cancellationToken).ConfigureAwait(false);

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

            return RefreshTokenIssueResult.Succeeded(token.RawToken, expiresAtUtc, subject);
        }
        catch (SqlException error)
        {
            Unavailable(error, nameof(IssueAsync));

            return RefreshTokenIssueResult.Failed(RefreshTokenOutcome.StoreUnavailable);
        }
        catch (InvalidOperationException error)
        {
            Unavailable(error, nameof(IssueAsync));

            return RefreshTokenIssueResult.Failed(RefreshTokenOutcome.StoreUnavailable);
        }
    }

    /// <inheritdoc />
    public async Task<RefreshTokenInspection> InspectAsync(
        string refreshToken,
        string clientBinding,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(refreshToken);
        ArgumentException.ThrowIfNullOrWhiteSpace(clientBinding);
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(refreshToken))
        {
            return RefreshTokenInspection.Failed(RefreshTokenOutcome.Unknown);
        }

        byte[] digest = Digest(refreshToken);
        byte[] clientDigest = Digest(clientBinding);
        DateTime now = Utc(_clock.UtcNow);

        try
        {
            await using SqlConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            await using SqlTransaction transaction = (SqlTransaction)await connection
                .BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken)
                .ConfigureAwait(false);

            StoredToken? stored = await ReadAsync(connection, transaction, digest, cancellationToken)
                .ConfigureAwait(false);

            if (stored is null)
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);

                return RefreshTokenInspection.Failed(RefreshTokenOutcome.Unknown);
            }

            RefreshTokenOutcome outcome = Classify(stored, clientDigest, now, _concurrentUseGrace);

            if (outcome == RefreshTokenOutcome.AlreadyUsed)
            {
                await RevokeForUserAsync(connection, transaction, stored.UserId, now, cancellationToken)
                    .ConfigureAwait(false);
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

            return outcome == RefreshTokenOutcome.Succeeded
                ? RefreshTokenInspection.Succeeded(
                    new RefreshTokenSubject(stored.UserId, stored.PortalId),
                    stored.ExpiresAtUtc)
                : RefreshTokenInspection.Failed(outcome, stored.UserId);
        }
        catch (SqlException error)
        {
            Unavailable(error, nameof(InspectAsync));

            return RefreshTokenInspection.Failed(RefreshTokenOutcome.StoreUnavailable);
        }
        catch (InvalidOperationException error)
        {
            Unavailable(error, nameof(InspectAsync));

            return RefreshTokenInspection.Failed(RefreshTokenOutcome.StoreUnavailable);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(digest);
            CryptographicOperations.ZeroMemory(clientDigest);
        }
    }

    /// <inheritdoc />
    public async Task<RefreshTokenRotationResult> RotateAsync(
        string refreshToken,
        string clientBinding,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(refreshToken);
        ArgumentException.ThrowIfNullOrWhiteSpace(clientBinding);
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(refreshToken))
        {
            return RefreshTokenRotationResult.Failed(RefreshTokenOutcome.Unknown);
        }

        byte[] digest = Digest(refreshToken);
        byte[] clientDigest = Digest(clientBinding);
        DateTime now = Utc(_clock.UtcNow);

        try
        {
            await using SqlConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            await using SqlTransaction transaction = (SqlTransaction)await connection
                .BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken)
                .ConfigureAwait(false);

            StoredToken? stored = await ReadAsync(connection, transaction, digest, cancellationToken)
                .ConfigureAwait(false);

            if (stored is null)
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);

                return RefreshTokenRotationResult.Failed(RefreshTokenOutcome.Unknown);
            }

            RefreshTokenOutcome outcome = Classify(stored, clientDigest, now, _concurrentUseGrace);

            if (outcome != RefreshTokenOutcome.Succeeded)
            {
                // A replayed token retires its whole family, exactly as the process-local store does: the
                // presentation proves the token escaped, and the family is the blast radius.
                if (outcome == RefreshTokenOutcome.AlreadyUsed)
                {
                    await RevokeForUserAsync(connection, transaction, stored.UserId, now, cancellationToken)
                        .ConfigureAwait(false);
                }

                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

                return RefreshTokenRotationResult.Failed(outcome);
            }

            if (stored.Generation == int.MaxValue)
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);

                // Thrown as its own type so the provider-fault handler below can let it through. It remains an
                // InvalidOperationException, so nothing that catches the general type changes behaviour.
                throw new GenerationCounterExhaustedException();
            }

            TokenMaterial replacement = CreateToken();
            DateTime replacementExpiresAtUtc = Earlier(
                now.Add(_slidingLifetime),
                stored.FamilyExpiresAtUtc);

            // ⚠ RECLAIMED AND BOUNDED BEFORE THE TWO WRITES, WHICH IS THE HALF THIS PATH USED TO OMIT
            // ENTIRELY. A rotation retains the spent generation - its digest, paired with the client that
            // spent it, is what distinguishes a same-client retry from a replay - so every refresh added a row
            // that nothing ever reclaimed until the family's absolute ceiling passed. One family refreshing on
            // a timer therefore grew this table without bound while the advertised ceiling was enforced only on
            // sign-in.
            //
            // TWO places, not one, for the reason the process-local store records: a rotation ordinarily adds
            // one net row, but eviction is free to reclaim the presented family itself when it is nearest its
            // ceiling, and then BOTH statements below are insertions. Reserving one place would leave the table
            // a single row over the ceiling in exactly that case. When the presented family IS evicted the
            // consume affects nothing and the successor is written as a fresh generation of the same family -
            // the caller keeps a usable session, which is the same outcome the process-local store produces.
            //
            // The return value is deliberately ignored here, and that is not the same decision as on the issue
            // path. Refusing a rotation for capacity would sign out a caller who already holds a live session,
            // to protect a bound the eviction above has already done everything it can to respect; the issue
            // path refuses because declining a NEW session is the lesser harm. Documented rather than silent.
            await PruneAsync(connection, transaction, now, cancellationToken).ConfigureAwait(false);
            await EnforceCapacityAsync(connection, transaction, headroom: 2, cancellationToken)
                .ConfigureAwait(false);

            await ConsumeAsync(connection, transaction, digest, now, clientDigest, cancellationToken)
                .ConfigureAwait(false);

            await InsertAsync(
                connection,
                transaction,
                replacement.Digest,
                stored.FamilyId,
                stored.Generation + 1,
                stored.UserId,
                stored.PortalId,
                replacementExpiresAtUtc,
                stored.FamilyExpiresAtUtc,
                cancellationToken).ConfigureAwait(false);

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

            return RefreshTokenRotationResult.Succeeded(
                replacement.RawToken,
                replacementExpiresAtUtc,
                new RefreshTokenSubject(stored.UserId, stored.PortalId));
        }
        catch (SqlException error)
        {
            Unavailable(error, nameof(RotateAsync));

            return RefreshTokenRotationResult.Failed(RefreshTokenOutcome.StoreUnavailable);
        }
        catch (InvalidOperationException error) when (error is not GenerationCounterExhaustedException)
        {
            // MIGRATION: SEC-11. Rotation caught strictly less than its three siblings, all of which also treat
            // InvalidOperationException as a store outage. The provider raises that type for conditions no less
            // real than a SqlException - a connection that was closed underneath the command, a transaction
            // already completed, a connection string the builder refuses - and rotation alone let them escape as
            // unhandled exceptions. The token service converts an outcome into a refusal; an escaping exception
            // becomes a 500 for a caller whose token was perfectly valid, and the fault text travels with it.
            //
            // The exhausted-counter assertion is deliberately excluded: it reports a state this store cannot
            // continue from, and reclassifying it as a transient outage would invite a retry that cannot help.
            Unavailable(error, nameof(RotateAsync));

            return RefreshTokenRotationResult.Failed(RefreshTokenOutcome.StoreUnavailable);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(digest);
            CryptographicOperations.ZeroMemory(clientDigest);
        }
    }

    /// <inheritdoc />
    public async Task<RefreshTokenOutcome> RevokeAsync(
        string refreshToken,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(refreshToken);
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(refreshToken))
        {
            return RefreshTokenOutcome.Unknown;
        }

        byte[] digest = Digest(refreshToken);
        DateTime now = Utc(_clock.UtcNow);

        try
        {
            await using SqlConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            await using SqlTransaction transaction = (SqlTransaction)await connection
                .BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken)
                .ConfigureAwait(false);

            StoredToken? stored = await ReadAsync(connection, transaction, digest, cancellationToken)
                .ConfigureAwait(false);

            if (stored is null)
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);

                return RefreshTokenOutcome.Unknown;
            }

            int changed = await ExecuteAsync(
                connection,
                transaction,
                FormattableString.Invariant($@"
UPDATE {_options.QualifiedTableName}
   SET [RevokedAtUtc] = @now
 WHERE [FamilyId] = @familyId AND [RevokedAtUtc] IS NULL;"),
                cancellationToken,
                new SqlParameter("@now", SqlDbType.DateTime2) { Value = now },
                new SqlParameter("@familyId", SqlDbType.UniqueIdentifier) { Value = stored.FamilyId })
                .ConfigureAwait(false);

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

            return changed == 0 ? RefreshTokenOutcome.AlreadyRevoked : RefreshTokenOutcome.Succeeded;
        }
        catch (SqlException error)
        {
            Unavailable(error, nameof(RevokeAsync));

            return RefreshTokenOutcome.StoreUnavailable;
        }
        catch (InvalidOperationException error)
        {
            Unavailable(error, nameof(RevokeAsync));

            return RefreshTokenOutcome.StoreUnavailable;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(digest);
        }
    }

    /// <inheritdoc />
    public async Task<RefreshTokenOutcome> RevokeAllForUserAsync(
        int userId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        DateTime now = Utc(_clock.UtcNow);

        try
        {
            await using SqlConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            await using SqlTransaction transaction = (SqlTransaction)await connection
                .BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken)
                .ConfigureAwait(false);

            object? known = await ScalarAsync(
                connection,
                transaction,
                FormattableString.Invariant($@"
SELECT TOP (1) 1
  FROM {_options.QualifiedTableName} WITH (UPDLOCK, HOLDLOCK)
 WHERE [UserId] = @userId;"),
                cancellationToken,
                new SqlParameter("@userId", SqlDbType.Int) { Value = userId })
                .ConfigureAwait(false);

            if (known is null or DBNull)
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);

                return RefreshTokenOutcome.Unknown;
            }

            int changed = await RevokeForUserAsync(connection, transaction, userId, now, cancellationToken)
                .ConfigureAwait(false);

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

            return changed == 0 ? RefreshTokenOutcome.AlreadyRevoked : RefreshTokenOutcome.Succeeded;
        }
        catch (SqlException error)
        {
            Unavailable(error, nameof(RevokeAllForUserAsync));

            return RefreshTokenOutcome.StoreUnavailable;
        }
        catch (InvalidOperationException error)
        {
            Unavailable(error, nameof(RevokeAllForUserAsync));

            return RefreshTokenOutcome.StoreUnavailable;
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// PRIV-02. DELETES rather than stamps, which is the distinction this member exists for: a sign-out revokes
    /// so the family stays recognisable, a deletion erases because the subject is gone. One statement, so the
    /// erasure is atomic without a transaction of its own - a partially purged subject is not a state this can
    /// reach.
    /// </para>
    /// <para>
    /// The predicate is composed from the scope's two optional identifiers, and both are always BOUND: the
    /// unused half is compared against itself through <c>@userId IS NULL</c> rather than being spliced out of
    /// the statement, so there is exactly one statement text for all three scopes and no path on which a
    /// missing identifier could widen the delete.
    /// </para>
    /// </remarks>
    public async Task<RefreshTokenPurgeResult> PurgeSubjectAsync(
        RefreshTokenPurgeScope scope,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            await using SqlConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);

            int removed = await ExecuteAsync(
                connection,
                null,
                FormattableString.Invariant($@"
DELETE FROM {_options.QualifiedTableName}
 WHERE (@userId IS NULL OR [UserId] = @userId)
   AND (@portalId IS NULL OR [PortalId] = @portalId);"),
                cancellationToken,
                new SqlParameter("@userId", SqlDbType.Int)
                {
                    Value = scope.UserId.HasValue ? scope.UserId.Value : DBNull.Value,
                },
                new SqlParameter("@portalId", SqlDbType.Int)
                {
                    Value = scope.PortalId.HasValue ? scope.PortalId.Value : DBNull.Value,
                })
                .ConfigureAwait(false);

            return RefreshTokenPurgeResult.Removed(removed);
        }
        catch (SqlException error)
        {
            Unavailable(error, nameof(PurgeSubjectAsync));

            return RefreshTokenPurgeResult.Unavailable();
        }
        catch (InvalidOperationException error)
        {
            Unavailable(error, nameof(PurgeSubjectAsync));

            return RefreshTokenPurgeResult.Unavailable();
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// PRIV-02. The operation-independent entry point into the same reclamation the issue and rotate paths
    /// perform inline. It exists because those were the ONLY callers: a deployment with no sign-in traffic
    /// reclaimed nothing, so every expired family and every revoked row it had ever written stayed in the table
    /// for good - and a durable store, unlike the process-local one, does not lose them on a restart either.
    /// Driven on a schedule by <c>RefreshTokenRetentionService</c>.
    /// </para>
    /// <para>
    /// It confirms the table first, exactly as every other operation does, so a sweep against an unprovisioned
    /// catalogue reports the store unavailable rather than raising an object-not-found fault out of a
    /// background timer.
    /// </para>
    /// </remarks>
    public async Task<RefreshTokenPurgeResult> PurgeRetiredAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        DateTime now = Utc(_clock.UtcNow);

        try
        {
            await using SqlConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);

            int removed = await PruneAsync(connection, null, now, cancellationToken).ConfigureAwait(false);

            return RefreshTokenPurgeResult.Removed(removed);
        }
        catch (SqlException error)
        {
            Unavailable(error, nameof(PurgeRetiredAsync));

            return RefreshTokenPurgeResult.Unavailable();
        }
        catch (InvalidOperationException error)
        {
            Unavailable(error, nameof(PurgeRetiredAsync));

            return RefreshTokenPurgeResult.Unavailable();
        }
    }

    /// <summary>Classifies a stored token, using the same rules as the process-local store.</summary>
    /// <param name="stored">The row that was read.</param>
    /// <param name="clientDigest">Digest of the presenting client's binding.</param>
    /// <param name="now">The current instant.</param>
    /// <param name="concurrentUseGrace">
    /// The configured window within which the same client re-presenting a just-spent generation is treated as a
    /// retry rather than as a replay. Passed in rather than read from a field so this decision stays pure and
    /// so the CONFIGURED value cannot be bypassed by a compiled default.
    /// </param>
    /// <returns>The outcome the presentation earns.</returns>
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
                && now >= stored.ConsumedAtUtc.Value
                && now - stored.ConsumedAtUtc.Value <= concurrentUseGrace
                && CryptographicOperations.FixedTimeEquals(stored.ConsumedClientDigest, clientDigest))
            {
                return RefreshTokenOutcome.ConcurrentUse;
            }

            return RefreshTokenOutcome.AlreadyUsed;
        }

        return stored.ExpiresAtUtc <= now
            ? RefreshTokenOutcome.Expired
            : RefreshTokenOutcome.Succeeded;
    }

    /// <summary>Opens a connection and confirms, once per instance, that the session table is present.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>An open connection to the session catalogue.</returns>
    private async Task<SqlConnection> OpenAsync(CancellationToken cancellationToken)
    {
        SqlConnection connection = new(_options.ConnectionString);

        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await ConfirmTableAsync(connection, cancellationToken).ConfigureAwait(false);

            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);

            throw;
        }
    }

    /// <summary>Confirms the session table exists, and refuses to operate when it does not.</summary>
    /// <param name="connection">An open connection to the session catalogue.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task that completes once the table is known to exist.</returns>
    /// <exception cref="InvalidOperationException">The table is absent.</exception>
    /// <remarks>
    /// <para>
    /// ⚠ THIS PROBES AND NEVER PROVISIONS, AND THE DIFFERENCE IS THE FINDING. Until this revision the store
    /// issued <c>CREATE TABLE</c> and three <c>CREATE INDEX</c> statements here on first use, under whatever
    /// identity the API runs as. Two things were wrong with that. AAP rule T4 makes schema authorship a
    /// deployment step rather than a runtime one, and the rule does not carve out "but only in its own
    /// catalogue" - a process that can create a table can create any table its login permits. And it forced
    /// the API's principal to hold DDL rights it needs for nothing else, which defeats the whole point of
    /// separating the identity that shapes a schema from the identity that reads and writes rows: an injection
    /// or a compromise of the API then inherits the ability to alter storage, not merely to read it.
    /// </para>
    /// <para>
    /// The table is provisioned from <c>docker/sql/refresh-token-store.sql</c>, which is the same script the
    /// integration suite applies, so the shape this store expects and the shape an operator creates cannot
    /// drift apart. What remains at runtime is one metadata read.
    /// </para>
    /// <para>
    /// THE PROBE ITSELF DISCLOSES NOTHING. <c>OBJECT_ID</c> answers whether a name resolves to a table in the
    /// catalogue already addressed by the open connection; it reads no row, no column value and no server
    /// configuration, and its argument is the identifier this store's own validated settings compose. The
    /// refusal it raises carries the table name and the script to run - operator-facing configuration, not
    /// credential material - and every caller converts it into <c>StoreUnavailable</c>, so no session
    /// operation is ever reported successful against a catalogue that cannot hold one.
    /// </para>
    /// <para>
    /// Confirmed at most once per instance, behind a gate, so the metadata read costs one statement per
    /// process rather than one per operation. The flag is only ever set to true, so a concurrent pair of
    /// callers cannot observe a torn value.
    /// </para>
    /// </remarks>
    private async Task ConfirmTableAsync(SqlConnection connection, CancellationToken cancellationToken)
    {
        if (_tableConfirmed)
        {
            return;
        }

        await _readinessGate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            if (_tableConfirmed)
            {
                return;
            }

            if (!await TablePresentAsync(connection, cancellationToken).ConfigureAwait(false))
            {
                throw new InvalidOperationException(
                    FormattableString.Invariant(
                        $"The refresh-token table {_options.QualifiedTableName} does not exist in the configured session catalogue.")
                    + " It is provisioned by a deployment step rather than by this application: run"
                    + " docker/sql/refresh-token-store.sql against that catalogue. The running API neither"
                    + " creates nor alters it.");
            }

            _tableConfirmed = true;
        }
        finally
        {
            _readinessGate.Release();
        }
    }

    /// <summary>Reports whether the configured session table resolves in the connected catalogue.</summary>
    /// <param name="connection">An open connection to the session catalogue.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns><see langword="true"/> when the table is present.</returns>
    private async Task<bool> TablePresentAsync(SqlConnection connection, CancellationToken cancellationToken)
    {
        object? present = await ScalarAsync(
            connection,
            transaction: null,
            FormattableString.Invariant(
                $"SELECT OBJECT_ID(N'{_options.QualifiedTableName}', N'U');"),
            cancellationToken).ConfigureAwait(false);

        return present is not null and not DBNull;
    }

    /// <summary>
    /// Reports whether the session catalogue is reachable and provisioned, and how much of the configured
    /// capacity is in use, for the health probe.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>What the probe was able to establish, as a closed set of bounded facts.</returns>
    /// <remarks>
    /// <para>
    /// MIGRATION: SEC-09. The health probe used to test the active store against the process-local
    /// implementation and, finding anything else, report "held by a deployment-supplied store" as HEALTHY with
    /// no check performed. For the durable store this solution itself ships that was three false statements at
    /// once - it IS this solution's store, it IS replica-safe, it DOES survive a restart - and, worse, a
    /// deployment whose session catalogue was unreachable or unprovisioned was reported healthy right up to
    /// the point where every sign-in failed.
    /// </para>
    /// <para>
    /// WHAT IT DELIBERATELY DOES NOT RETURN is any part of the fault. An outcome member says whether the
    /// catalogue was reachable and whether the table was there; the exception behind a failure is recorded
    /// through the store's own bounded diagnostic and never handed to the caller, because a provider exception
    /// carries the server, the catalogue, the login and sometimes the statement, and this value ends up in a
    /// health description an operator reads in a log.
    /// </para>
    /// <para>
    /// It never throws. A probe that threw would report the whole application unhealthy for a reason that has
    /// nothing to do with the application.
    /// </para>
    /// </remarks>
    internal async Task<SharedStoreReadiness> ProbeAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await using SqlConnection connection = new(_options.ConnectionString);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

            if (!await TablePresentAsync(connection, cancellationToken).ConfigureAwait(false))
            {
                return SharedStoreReadiness.TableMissing(_maximumTrackedTokens);
            }

            long tracked = await CountTrackedAsync(
                connection,
                transaction: null,
                cancellationToken).ConfigureAwait(false);

            return SharedStoreReadiness.Ready(tracked, _maximumTrackedTokens);
        }
        catch (SqlException error)
        {
            Unavailable(error, nameof(ProbeAsync));

            return SharedStoreReadiness.Unreachable(_maximumTrackedTokens);
        }
        catch (InvalidOperationException error)
        {
            Unavailable(error, nameof(ProbeAsync));

            return SharedStoreReadiness.Unreachable(_maximumTrackedTokens);
        }
    }

    /// <summary>Reads one token row under an update lock held for the transaction.</summary>
    /// <param name="connection">The open connection.</param>
    /// <param name="transaction">The enclosing serialisable transaction.</param>
    /// <param name="digest">Digest of the presented token.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The row, or <see langword="null"/> when the digest is unknown.</returns>
    private async Task<StoredToken?> ReadAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        byte[] digest,
        CancellationToken cancellationToken)
    {
        await using SqlCommand command = Command(
            connection,
            transaction,
            FormattableString.Invariant($@"
SELECT [FamilyId], [Generation], [UserId], [PortalId], [ExpiresAtUtc], [FamilyExpiresAtUtc],
       [ConsumedAtUtc], [ConsumedClientDigest], [RevokedAtUtc]
  FROM {_options.QualifiedTableName} WITH (UPDLOCK, HOLDLOCK)
 WHERE [TokenDigest] = @digest;"),
            new SqlParameter("@digest", SqlDbType.VarBinary, TokenEntropyBytes) { Value = digest });

        await using SqlDataReader reader = await command
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);

        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return new StoredToken(
            FamilyId: reader.GetGuid(0),
            Generation: reader.GetInt32(1),
            UserId: reader.GetInt32(2),
            PortalId: reader.GetInt32(3),
            ExpiresAtUtc: AsUtc(reader.GetDateTime(4)),
            FamilyExpiresAtUtc: AsUtc(reader.GetDateTime(5)),
            ConsumedAtUtc: reader.IsDBNull(6) ? null : AsUtc(reader.GetDateTime(6)),
            ConsumedClientDigest: reader.IsDBNull(7) ? null : (byte[])reader.GetValue(7),
            RevokedAtUtc: reader.IsDBNull(8) ? null : AsUtc(reader.GetDateTime(8)));
    }

    /// <summary>Inserts one token row.</summary>
    /// <param name="connection">The open connection.</param>
    /// <param name="transaction">The enclosing transaction.</param>
    /// <param name="digest">Digest of the token being stored.</param>
    /// <param name="familyId">Identifier of the family the token belongs to.</param>
    /// <param name="generation">Generation of the token within its family.</param>
    /// <param name="userId">The account the family belongs to.</param>
    /// <param name="portalId">The tenant the family was issued in.</param>
    /// <param name="expiresAtUtc">When this generation stops being usable.</param>
    /// <param name="familyExpiresAtUtc">When the whole family stops being usable.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task that completes when the row has been written.</returns>
    private Task InsertAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        byte[] digest,
        Guid familyId,
        int generation,
        int userId,
        int portalId,
        DateTime expiresAtUtc,
        DateTime familyExpiresAtUtc,
        CancellationToken cancellationToken) =>
        ExecuteAsync(
            connection,
            transaction,
            FormattableString.Invariant($@"
INSERT INTO {_options.QualifiedTableName}
    ([TokenDigest], [FamilyId], [Generation], [UserId], [PortalId], [ExpiresAtUtc], [FamilyExpiresAtUtc])
VALUES
    (@digest, @familyId, @generation, @userId, @portalId, @expiresAtUtc, @familyExpiresAtUtc);"),
            cancellationToken,
            new SqlParameter("@digest", SqlDbType.VarBinary, TokenEntropyBytes) { Value = digest },
            new SqlParameter("@familyId", SqlDbType.UniqueIdentifier) { Value = familyId },
            new SqlParameter("@generation", SqlDbType.Int) { Value = generation },
            new SqlParameter("@userId", SqlDbType.Int) { Value = userId },
            new SqlParameter("@portalId", SqlDbType.Int) { Value = portalId },
            new SqlParameter("@expiresAtUtc", SqlDbType.DateTime2) { Value = expiresAtUtc },
            new SqlParameter("@familyExpiresAtUtc", SqlDbType.DateTime2) { Value = familyExpiresAtUtc });

    /// <summary>Marks one token as consumed by a named client.</summary>
    /// <param name="connection">The open connection.</param>
    /// <param name="transaction">The enclosing transaction.</param>
    /// <param name="digest">Digest of the consumed token.</param>
    /// <param name="now">The consumption instant.</param>
    /// <param name="clientDigest">Digest of the consuming client's binding.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task that completes when the row has been updated.</returns>
    private Task ConsumeAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        byte[] digest,
        DateTime now,
        byte[] clientDigest,
        CancellationToken cancellationToken) =>
        ExecuteAsync(
            connection,
            transaction,
            FormattableString.Invariant($@"
UPDATE {_options.QualifiedTableName}
   SET [ConsumedAtUtc] = @now, [ConsumedClientDigest] = @clientDigest
 WHERE [TokenDigest] = @digest;"),
            cancellationToken,
            new SqlParameter("@now", SqlDbType.DateTime2) { Value = now },
            new SqlParameter("@clientDigest", SqlDbType.VarBinary, TokenEntropyBytes) { Value = clientDigest },
            new SqlParameter("@digest", SqlDbType.VarBinary, TokenEntropyBytes) { Value = digest });

    /// <summary>Revokes every unrevoked token of one account.</summary>
    /// <param name="connection">The open connection.</param>
    /// <param name="transaction">The enclosing transaction.</param>
    /// <param name="userId">The account whose families are retired.</param>
    /// <param name="now">The revocation instant.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>How many rows were retired by this call.</returns>
    private Task<int> RevokeForUserAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        int userId,
        DateTime now,
        CancellationToken cancellationToken) =>
        ExecuteAsync(
            connection,
            transaction,
            FormattableString.Invariant($@"
UPDATE {_options.QualifiedTableName}
   SET [RevokedAtUtc] = @now
 WHERE [UserId] = @userId AND [RevokedAtUtc] IS NULL;"),
            cancellationToken,
            new SqlParameter("@now", SqlDbType.DateTime2) { Value = now },
            new SqlParameter("@userId", SqlDbType.Int) { Value = userId });

    /// <summary>
    /// Removes rows whose family has expired outright, and revoked rows held beyond the configured retention.
    /// </summary>
    /// <param name="connection">The open connection.</param>
    /// <param name="transaction">The enclosing transaction, or <see langword="null"/> for an autocommitted sweep.</param>
    /// <param name="now">The current instant.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>How many rows were removed.</returns>
    /// <remarks>
    /// <para>
    /// PRIV-02. IT DELETES ON TWO GROUNDS NOW, AND THE SECOND IS THE RETENTION POLICY. Expiry alone left a
    /// revoked row - the account, the tenant and the token digest of a session that had already ended - in the
    /// table for the remainder of the refresh lifetime. A revoked row is kept because a presentation of that
    /// family afterwards is recognisable as a replay; past the configured window nobody is going to read that
    /// signal and what remains is personal data.
    /// </para>
    /// <para>
    /// The transaction became optional so the scheduled sweep can run without one. A sweep is a single
    /// set-based DELETE over rows nothing else can be redeeming - an expired family cannot be redeemed and a
    /// revoked one cannot either - so it needs no isolation beyond the statement's own, whereas the
    /// issue and rotate paths call it inside the transaction that will write their successor and must stay
    /// inside it.
    /// </para>
    /// </remarks>
    private Task<int> PruneAsync(
        SqlConnection connection,
        SqlTransaction? transaction,
        DateTime now,
        CancellationToken cancellationToken) =>
        ExecuteAsync(
            connection,
            transaction,
            FormattableString.Invariant($@"
DELETE FROM {_options.QualifiedTableName}
 WHERE [FamilyExpiresAtUtc] <= @now
    OR ([RevokedAtUtc] IS NOT NULL AND [RevokedAtUtc] <= @revokedBefore);"),
            cancellationToken,
            new SqlParameter("@now", SqlDbType.DateTime2) { Value = now },
            new SqlParameter("@revokedBefore", SqlDbType.DateTime2) { Value = now - _revokedRetention });

    /// <summary>Counts every tracked generation, which is what the capacity ceiling bounds.</summary>
    /// <param name="connection">The open connection.</param>
    /// <param name="transaction">The enclosing transaction, or <see langword="null"/>.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The number of rows in the session table.</returns>
    /// <remarks>
    /// MIGRATION: SEC-04. It used to count only rows that were neither revoked nor past their family ceiling,
    /// which made the ceiling bound something other than the table. Spent and revoked generations are RETAINED
    /// deliberately - a spent digest is the signal that makes a replay recognisable, and a revoked one is what
    /// makes a revocation observable - so they occupy the table while counting for nothing, and a table under a
    /// hundred-thousand-row ceiling could hold any number of them. The ceiling now bounds what it is documented
    /// to bound: the rows that exist. This is also the denominator the process-local store uses, whose
    /// semantics this store's own summary promises to match.
    /// </remarks>
    private async Task<long> CountTrackedAsync(
        SqlConnection connection,
        SqlTransaction? transaction,
        CancellationToken cancellationToken)
    {
        object? count = await ScalarAsync(
            connection,
            transaction,
            FormattableString.Invariant($@"
SELECT COUNT_BIG(1)
  FROM {_options.QualifiedTableName};"),
            cancellationToken).ConfigureAwait(false);

        return count is null or DBNull
            ? 0
            : Convert.ToInt64(count, CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Retires whole refresh families, nearest their absolute ceiling first, until the table has room for the
    /// generations the caller is about to write.
    /// </summary>
    /// <param name="connection">The open connection.</param>
    /// <param name="transaction">The enclosing serialisable transaction.</param>
    /// <param name="headroom">How many rows the caller will insert immediately after this call.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns><see langword="true"/> when the table has room for the caller's writes.</returns>
    /// <remarks>
    /// <para>
    /// MIGRATION: SEC-04. Two defects met here. The ceiling was a compiled constant rather than the configured
    /// one, and it was enforced on ISSUE ONLY: rotation consumed one generation and inserted its successor
    /// while retaining the spent row, so one family rotating on a timer grew the table by one row per refresh,
    /// for ever, past any advertised bound. Second, reaching the bound REFUSED every new sign-in - the store
    /// answered <c>CapacityExhausted</c>, which the token service reports as a store outage - so a table filled
    /// by ordinary use locked the whole deployment out instead of retiring its oldest sessions.
    /// </para>
    /// <para>
    /// THE UNIT OF EVICTION IS THE FAMILY, and that is a security property rather than a tidiness one. It is
    /// the same reasoning the process-local store records at length: reuse of a superseded generation is what
    /// identifies a stolen token, and this store answers such a reuse by revoking the whole family. A family
    /// left HALF-tracked cannot do that - the evicted older generations read as unknown tokens, which are
    /// refused but revoke nothing, so a thief's live generation survives the very presentation that should have
    /// killed it. Evicting families whole means the worst outcome under pressure is that a caller signs in
    /// again, which is what eviction is FOR.
    /// </para>
    /// <para>
    /// The ordering matches the process-local store's exactly - nearest family ceiling first, tie-broken on the
    /// family identifier - so the two implementations retire the same families in the same order, and the pure
    /// policy that store exposes for direct testing remains the definition of the behaviour. Expressed as one
    /// set-based statement rather than by reading the table into memory: the running total over families
    /// ordered by ceiling is exactly the loop that policy performs, and a family is retired when the total
    /// BEFORE it is still short of the excess.
    /// </para>
    /// <para>
    /// The headroom is reserved before the write for the reason that store also records: reclaiming to the bare
    /// ceiling and then inserting would exceed it by precisely the number of rows the caller adds.
    /// </para>
    /// </remarks>
    private async Task<bool> EnforceCapacityAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        int headroom,
        CancellationToken cancellationToken)
    {
        long effectiveCeiling = (long)_maximumTrackedTokens - headroom;
        long tracked = await CountTrackedAsync(connection, transaction, cancellationToken)
            .ConfigureAwait(false);
        long excess = tracked - effectiveCeiling;

        if (excess <= 0)
        {
            return true;
        }

        await ExecuteAsync(
            connection,
            transaction,
            FormattableString.Invariant($@"
WITH [families] AS
(
    SELECT [FamilyId],
           MIN([FamilyExpiresAtUtc]) AS [Ceiling],
           COUNT_BIG(1)              AS [Generations]
      FROM {_options.QualifiedTableName}
     GROUP BY [FamilyId]
),
[ordered] AS
(
    SELECT [FamilyId],
           [Generations],
           SUM([Generations]) OVER (ORDER BY [Ceiling], [FamilyId] ROWS UNBOUNDED PRECEDING) AS [Cumulative]
      FROM [families]
)
DELETE FROM {_options.QualifiedTableName}
 WHERE [FamilyId] IN
 (
     SELECT [FamilyId]
       FROM [ordered]
      WHERE [Cumulative] - [Generations] < @excess
 );"),
            cancellationToken,
            new SqlParameter("@excess", SqlDbType.BigInt) { Value = excess })
            .ConfigureAwait(false);

        // Re-counted rather than inferred from the affected-row count, because the answer this returns is
        // whether there is room NOW. Eviction frees space whenever any family exists, so the only way to
        // arrive here still full is a ceiling so small that no family fits under it - defensive rather than
        // reachable through validated settings, and reported as capacity rather than pretended past.
        long remaining = await CountTrackedAsync(connection, transaction, cancellationToken)
            .ConfigureAwait(false);

        return remaining <= effectiveCeiling;
    }

    /// <summary>Runs a non-query statement.</summary>
    /// <param name="connection">The open connection.</param>
    /// <param name="transaction">The enclosing transaction, or <see langword="null"/>.</param>
    /// <param name="sql">The statement text, whose only interpolations are validated identifiers.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <param name="parameters">Bound parameters.</param>
    /// <returns>The affected-row count.</returns>
    private async Task<int> ExecuteAsync(
        SqlConnection connection,
        SqlTransaction? transaction,
        string sql,
        CancellationToken cancellationToken,
        params SqlParameter[] parameters)
    {
        await using SqlCommand command = Command(connection, transaction, sql, parameters);

        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Runs a statement that projects one value.</summary>
    /// <param name="connection">The open connection.</param>
    /// <param name="transaction">The enclosing transaction, or <see langword="null"/>.</param>
    /// <param name="sql">The statement text.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <param name="parameters">Bound parameters.</param>
    /// <returns>The projected value.</returns>
    private async Task<object?> ScalarAsync(
        SqlConnection connection,
        SqlTransaction? transaction,
        string sql,
        CancellationToken cancellationToken,
        params SqlParameter[] parameters)
    {
        await using SqlCommand command = Command(connection, transaction, sql, parameters);

        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Builds a command carrying the configured timeout and the bound parameters.</summary>
    /// <param name="connection">The open connection.</param>
    /// <param name="transaction">The enclosing transaction, or <see langword="null"/>.</param>
    /// <param name="sql">The statement text.</param>
    /// <param name="parameters">Bound parameters.</param>
    /// <returns>The prepared command.</returns>
    private SqlCommand Command(
        SqlConnection connection,
        SqlTransaction? transaction,
        string sql,
        params SqlParameter[] parameters)
    {
        SqlCommand command = connection.CreateCommand();
        command.CommandText = sql;
        command.CommandType = CommandType.Text;
        command.CommandTimeout = _options.CommandTimeoutSeconds;

        if (transaction is not null)
        {
            command.Transaction = transaction;
        }

        foreach (SqlParameter parameter in parameters)
        {
            command.Parameters.Add(parameter);
        }

        return command;
    }

    /// <summary>Records a store fault as a bounded set of facts, never as the fault itself.</summary>
    /// <param name="error">The fault, read for its classification only.</param>
    /// <param name="operation">The member that failed.</param>
    /// <remarks>
    /// <para>
    /// ⚠ THE EXCEPTION IS NO LONGER PASSED TO THE LOGGER, AND THAT IS THE FIX. MIGRATION: SEC-10. A
    /// <see cref="SqlException"/> rendered into a log carries its message, and a provider message routinely
    /// names the server, the catalogue, the login that was refused and the statement that failed. The
    /// connection string reaching this store addresses the SESSION catalogue, and a durable-store deployment
    /// configures it with a principal of its own - so the previous call published the location and the
    /// principal of the session store into ordinary application logs on every transient fault, where a log
    /// aggregator retains and indexes it. Structured logs are read far more widely than the database they
    /// describe.
    /// </para>
    /// <para>
    /// WHAT IS KEPT IS WHAT AN OPERATOR ACTUALLY DIAGNOSES FROM: which operation failed, the exception TYPE,
    /// and - for a provider fault - the SQL error number and severity class. Those three answer the questions
    /// that matter here (is this a login failure, a timeout, a deadlock, a missing object, a transport drop)
    /// and every one of them is a bounded value chosen by the product rather than composed from configuration.
    /// The error number is precisely the value an operator looks up; the message that used to accompany it adds
    /// deployment detail and nothing diagnostic.
    /// </para>
    /// <para>
    /// The number is taken from the outermost provider error only. Walking the error collection would multiply
    /// the record without adding a distinct condition, and a batch's later errors are consequences of its
    /// first.
    /// </para>
    /// </remarks>
    private void Unavailable(Exception error, string operation)
    {
        if (error is SqlException provider)
        {
            _logger.LogError(
                "The shared refresh-token store could not complete {Operation}: {FaultType}, SQL error {SqlErrorNumber} (class {SqlErrorClass}). Session operations are reported as unavailable rather than as successful. The provider message is deliberately not recorded, because it names the session catalogue, its server and the principal used to reach them.",
                operation,
                nameof(SqlException),
                provider.Number,
                provider.Class);

            return;
        }

        _logger.LogError(
            "The shared refresh-token store could not complete {Operation}: {FaultType}. Session operations are reported as unavailable rather than as successful. The fault detail is deliberately not recorded, because it can carry the session catalogue's location and credentials.",
            operation,
            error.GetType().Name);
    }

    /// <summary>Produces one refresh token and its digest.</summary>
    /// <returns>The raw token and the digest stored for it.</returns>
    private static TokenMaterial CreateToken()
    {
        byte[] entropy = RandomNumberGenerator.GetBytes(TokenEntropyBytes);

        try
        {
            string raw = Convert.ToBase64String(entropy);

            return new TokenMaterial(raw, Digest(raw));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(entropy);
        }
    }

    /// <summary>Computes the SHA-256 digest of a value, leaving no copy of the value behind.</summary>
    /// <param name="value">The value to digest - a refresh token or a client binding.</param>
    /// <returns>The digest.</returns>
    /// <remarks>
    /// <para>
    /// MIGRATION: SEC-11. This was <c>SHA256.HashData(Encoding.UTF8.GetBytes(value))</c>, which allocates a
    /// mutable byte copy of the token, hands it to the hash, and abandons it to the garbage collector still
    /// holding the plaintext. That copy then survives in the heap for an unbounded period, is copied again by a
    /// compacting collection, and is captured verbatim by any process dump - which is exactly the exposure this
    /// store otherwise takes care to avoid, holding only digests in its table and zeroising every digest buffer
    /// it finishes with. Zeroising the digests while leaving the token's own bytes lying in the heap protected
    /// the less sensitive of the two.
    /// </para>
    /// <para>
    /// The token cannot be zeroised at its source - it is a <see cref="string"/>, because that is what the
    /// contract accepts and what the wire delivers - so the copy this method makes is the ONE part of the
    /// exposure that is under this method's control, and it is now cleared before the method returns. Rented
    /// rather than stack-allocated because a client binding has no length bound worth trusting to the stack;
    /// cleared through <see cref="CryptographicOperations.ZeroMemory"/>, which the runtime will not elide, and
    /// in a finally block so a hash failure cannot skip it.
    /// </para>
    /// </remarks>
    private static byte[] Digest(string value)
    {
        int maximum = Encoding.UTF8.GetMaxByteCount(value.Length);
        byte[] buffer = ArrayPool<byte>.Shared.Rent(maximum);

        try
        {
            int written = Encoding.UTF8.GetBytes(value, buffer);

            return SHA256.HashData(buffer.AsSpan(0, written));
        }
        finally
        {
            // Zeroised BEFORE the buffer returns to the pool, so no later renter can observe the plaintext.
            // The whole rented length is cleared rather than only the written span, because a pooled buffer may
            // be longer than this value and may still hold a previous renter's bytes.
            CryptographicOperations.ZeroMemory(buffer);
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>Returns the earlier of two instants.</summary>
    /// <param name="first">The first instant.</param>
    /// <param name="second">The second instant.</param>
    /// <returns>The earlier value.</returns>
    private static DateTime Earlier(DateTime first, DateTime second) =>
        first <= second ? first : second;

    /// <summary>Stamps an instant as UTC without shifting it.</summary>
    /// <param name="value">The instant.</param>
    /// <returns>The same instant, kinded UTC.</returns>
    private static DateTime Utc(DateTime value) => value.Kind == DateTimeKind.Utc
        ? value
        : DateTime.SpecifyKind(value, DateTimeKind.Utc);

    /// <summary>Stamps a read instant as UTC, since <c>datetime2</c> carries no offset.</summary>
    /// <param name="value">The value read from the store.</param>
    /// <returns>The same instant, kinded UTC.</returns>
    private static DateTime AsUtc(DateTime value) =>
        DateTime.SpecifyKind(value, DateTimeKind.Utc);

    /// <summary>One refresh token: the value handed out, and the digest kept.</summary>
    /// <param name="RawToken">The value returned to the caller exactly once.</param>
    /// <param name="Digest">The digest written to the store.</param>
    private sealed record TokenMaterial(string RawToken, byte[] Digest);

    /// <summary>Raised when one family has rotated as many times as its counter can express.</summary>
    /// <remarks>
    /// A distinct type so that the provider-fault handler on the rotation path can let it through rather than
    /// reclassifying an assertion about this store's own state as a transient outage. It derives from
    /// <see cref="InvalidOperationException"/> so any caller catching the general type is unaffected.
    /// </remarks>
    private sealed class GenerationCounterExhaustedException : InvalidOperationException
    {
        /// <summary>Initialises a new instance of the class.</summary>
        public GenerationCounterExhaustedException()
            : base("The refresh-token family exhausted its generation counter.")
        {
        }
    }

    /// <summary>What a readiness probe of the session catalogue was able to establish.</summary>
    /// <param name="Outcome">Whether the catalogue was reachable and provisioned.</param>
    /// <param name="TrackedGenerations">How many rows the session table holds, or zero when unknown.</param>
    /// <param name="Ceiling">The configured ceiling those rows are bounded by.</param>
    /// <remarks>
    /// <para>
    /// A closed set of bounded facts, and deliberately nothing else. No exception, no message, no connection
    /// detail: its consumer composes a health description that an operator reads in a log, and the fault behind
    /// a failure is recorded through this store's own bounded diagnostic instead.
    /// </para>
    /// <para>
    /// The ceiling travels even on a failure so the probe can report the configured bound whether or not it
    /// managed to count against it.
    /// </para>
    /// </remarks>
    internal readonly record struct SharedStoreReadiness(
        SharedStoreReadinessOutcome Outcome,
        long TrackedGenerations,
        int Ceiling)
    {
        /// <summary>Creates the result for a reachable, provisioned catalogue.</summary>
        /// <param name="tracked">How many rows the session table holds.</param>
        /// <param name="ceiling">The configured ceiling.</param>
        /// <returns>The result.</returns>
        public static SharedStoreReadiness Ready(long tracked, int ceiling) =>
            new(SharedStoreReadinessOutcome.Ready, tracked, ceiling);

        /// <summary>Creates the result for a catalogue that could not be reached at all.</summary>
        /// <param name="ceiling">The configured ceiling.</param>
        /// <returns>The result.</returns>
        public static SharedStoreReadiness Unreachable(int ceiling) =>
            new(SharedStoreReadinessOutcome.Unreachable, 0, ceiling);

        /// <summary>Creates the result for a reachable catalogue with no session table in it.</summary>
        /// <param name="ceiling">The configured ceiling.</param>
        /// <returns>The result.</returns>
        public static SharedStoreReadiness TableMissing(int ceiling) =>
            new(SharedStoreReadinessOutcome.TableMissing, 0, ceiling);
    }

    /// <summary>The persisted state of one refresh token.</summary>
    /// <param name="FamilyId">Identifier shared by every generation of one session.</param>
    /// <param name="Generation">How many rotations preceded this token.</param>
    /// <param name="UserId">The account the session belongs to.</param>
    /// <param name="PortalId">The tenant the session was established in.</param>
    /// <param name="ExpiresAtUtc">When this generation stops being usable.</param>
    /// <param name="FamilyExpiresAtUtc">When the whole family stops being usable.</param>
    /// <param name="ConsumedAtUtc">When this generation was exchanged, or <see langword="null"/>.</param>
    /// <param name="ConsumedClientDigest">Digest of the client that exchanged it.</param>
    /// <param name="RevokedAtUtc">When the token was retired, or <see langword="null"/>.</param>
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
}

/// <summary>What a readiness probe of the shared session catalogue established.</summary>
/// <remarks>
/// A closed set, declared beside the store rather than nested inside it because the health probe that consumes
/// it must be able to name each member. Every member describes the CATALOGUE, never the fault that revealed it:
/// telling "unreachable" from "reachable but not provisioned" is what an operator acts on, and the two demand
/// opposite remedies - fix connectivity or credentials, versus run the provisioning script.
/// </remarks>
internal enum SharedStoreReadinessOutcome
{
    /// <summary>The catalogue could not be reached, or the connection was refused.</summary>
    Unreachable = 0,

    /// <summary>The catalogue was reached and holds no session table.</summary>
    TableMissing = 1,

    /// <summary>The catalogue was reached and the session table is present.</summary>
    Ready = 2,
}
