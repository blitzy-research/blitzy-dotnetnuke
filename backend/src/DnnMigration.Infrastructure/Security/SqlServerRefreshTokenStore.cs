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
/// this API keeps, so sign-out and replay defence are exactly as strong as the store's reach.
/// </para>
/// <para>
/// <strong>Every state transition is one serialisable transaction.</strong> The row is read with
/// <c>UPDLOCK, HOLDLOCK</c>, classified, and updated inside the same transaction, so two exchanges racing
/// on one token produce exactly one successor - the guarantee the single monitor in the process-local store
/// provides within one process, obtained here across all of them.
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
    private readonly TimeSpan _concurrentUseGrace;

    private readonly int _maximumTrackedTokens;

    /// <summary>
    /// How long a REVOKED row is retained before reclamation deletes it, from
    /// <c>RefreshTokenStore:RevokedRecordRetentionHours</c>.
    /// </summary>
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
    /// <see langword="true"/>: every replica reads and writes this one table, so a family absent from it is
    /// absent from the whole deployment and a revocation that matches nothing has genuinely left nothing
    /// exchangeable.
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

            // TWO places, not one, for the reason the process-local store records: a rotation ordinarily
            // adds one net row, but eviction is free to reclaim the presented family itself when it is
            // nearest its ceiling, and then BOTH statements below are insertions.
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
            // Rotation caught strictly less than its three siblings, all of which also treat
            // InvalidOperationException as a store outage.
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
    /// PRIV-02. DELETES rather than stamps, which is the distinction this member exists for: a sign-out
    /// revokes so the family stays recognisable, a deletion erases because the subject is gone.
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
    /// PRIV-02. The operation-independent entry point into the same reclamation the issue and rotate paths
    /// perform inline.
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
    /// The configured window within which the same client re-presenting a just-spent generation is treated
    /// as a retry rather than as a replay.
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
    /// The table is provisioned from <c>docker/sql/refresh-token-store.sql</c>, which is the same script
    /// the integration suite applies, so the shape this store expects and the shape an operator creates
    /// cannot drift apart. What remains at runtime is one metadata read.
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
    /// WHAT IT DELIBERATELY DOES NOT RETURN is any part of the fault.
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
    /// Removes rows whose family has expired outright, and revoked rows held beyond the configured
    /// retention.
    /// </summary>
    /// <param name="connection">The open connection.</param>
    /// <param name="transaction">
    /// The enclosing transaction, or <see langword="null"/> for an autocommitted sweep.
    /// </param>
    /// <param name="now">The current instant.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>How many rows were removed.</returns>
    /// <remarks>
    /// PRIV-02. IT DELETES ON TWO GROUNDS NOW, AND THE SECOND IS THE RETENTION POLICY. Expiry alone left a
    /// revoked row - the account, the tenant and the token digest of a session that had already ended - in
    /// the table for the remainder of the refresh lifetime.
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
    /// Retires whole refresh families, nearest their absolute ceiling first, until the table has room for
    /// the generations the caller is about to write.
    /// </summary>
    /// <param name="connection">The open connection.</param>
    /// <param name="transaction">The enclosing serialisable transaction.</param>
    /// <param name="headroom">How many rows the caller will insert immediately after this call.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns><see langword="true"/> when the table has room for the caller's writes.</returns>
    /// <remarks>
    /// Two defects met here. The ceiling was a compiled constant rather than the configured one, and it was
    /// enforced on ISSUE ONLY: rotation consumed one generation and inserted its successor while retaining
    /// the spent row, so one family rotating on a timer grew the table by one row per refresh, for ever,
    /// past any advertised bound.
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
    /// ⚠ THE EXCEPTION IS DELIBERATELY NOT PASSED TO THE LOGGER. MIGRATION: A <see
    /// cref="SqlException"/> rendered into a log carries its message, and a provider message routinely
    /// names the server, the catalogue, the login that was refused and the statement that failed.
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
    /// This was <c>SHA256.HashData(Encoding.UTF8.GetBytes(value))</c>, which allocates a mutable byte copy
    /// of the token, hands it to the hash, and abandons it to the garbage collector still holding the
    /// plaintext.
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
            // The whole rented length is cleared rather than only the written span, because a pooled buffer
            // may be longer than this value and may still hold a previous renter's bytes.
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
internal enum SharedStoreReadinessOutcome
{
    /// <summary>The catalogue could not be reached, or the connection was refused.</summary>
    Unreachable = 0,

    /// <summary>The catalogue was reached and holds no session table.</summary>
    TableMissing = 1,

    /// <summary>The catalogue was reached and the session table is present.</summary>
    Ready = 2,
}
