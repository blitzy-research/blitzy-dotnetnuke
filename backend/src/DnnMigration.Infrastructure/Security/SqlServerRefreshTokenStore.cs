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
    private const int ConcurrentUseGraceSeconds = 5;

    /// <summary>
    /// Hard ceiling on tracked, still-live tokens, mirroring the process-local store's own bound.
    /// </summary>
    /// <remarks>
    /// The bound exists so that a caller looping over sign-in cannot grow the table without limit. Expired
    /// families are pruned on every issue, so the ceiling is only reached by live sessions.
    /// </remarks>
    private const int MaximumTrackedTokens = 100_000;

    private static readonly TimeSpan ConcurrentUseGrace =
        TimeSpan.FromSeconds(ConcurrentUseGraceSeconds);

    private readonly IClock _clock;
    private readonly ILogger<SqlServerRefreshTokenStore> _logger;
    private readonly RefreshTokenStoreOptions _options;
    private readonly TimeSpan _slidingLifetime;
    private readonly TimeSpan _familyLifetime;
    private readonly SemaphoreSlim _bootstrapGate = new(1, 1);
    private bool _bootstrapped;

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

        _clock = clock;
        _logger = logger;
        _options = storeOptions.Value;
        _slidingLifetime = TimeSpan.FromDays(jwtOptions.Value.RefreshTokenExpirationDays);
        _familyLifetime = TimeSpan.FromDays(jwtOptions.Value.RefreshTokenAbsoluteExpirationDays);
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

            await PruneAsync(connection, transaction, now, cancellationToken).ConfigureAwait(false);

            long live = await CountLiveAsync(connection, transaction, now, cancellationToken)
                .ConfigureAwait(false);

            if (live >= MaximumTrackedTokens)
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

            RefreshTokenOutcome outcome = Classify(stored, clientDigest, now);

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

            RefreshTokenOutcome outcome = Classify(stored, clientDigest, now);

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

                throw new InvalidOperationException(
                    "The refresh-token family exhausted its generation counter.");
            }

            TokenMaterial replacement = CreateToken();
            DateTime replacementExpiresAtUtc = Earlier(
                now.Add(_slidingLifetime),
                stored.FamilyExpiresAtUtc);

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

    /// <summary>Classifies a stored token, using the same rules as the process-local store.</summary>
    /// <param name="stored">The row that was read.</param>
    /// <param name="clientDigest">Digest of the presenting client's binding.</param>
    /// <param name="now">The current instant.</param>
    /// <returns>The outcome the presentation earns.</returns>
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

    /// <summary>Opens a connection, provisioning the table on first use when permitted.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>An open connection to the session catalogue.</returns>
    private async Task<SqlConnection> OpenAsync(CancellationToken cancellationToken)
    {
        SqlConnection connection = new(_options.ConnectionString);

        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await EnsureTableAsync(connection, cancellationToken).ConfigureAwait(false);

            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);

            throw;
        }
    }

    /// <summary>Creates the session table when it is absent and creation is permitted.</summary>
    /// <param name="connection">An open connection to the session catalogue.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task that completes once the table is known to exist.</returns>
    /// <remarks>
    /// The statement is guarded and idempotent, so several replicas racing to start produce one table. It
    /// runs at most once per instance, and it reaches only the catalogue this store is configured with.
    /// </remarks>
    private async Task EnsureTableAsync(SqlConnection connection, CancellationToken cancellationToken)
    {
        if (_bootstrapped)
        {
            return;
        }

        await _bootstrapGate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            if (_bootstrapped)
            {
                return;
            }

            if (!_options.CreateTableIfMissing)
            {
                object? present = await ScalarAsync(
                    connection,
                    transaction: null,
                    FormattableString.Invariant(
                        $"SELECT OBJECT_ID(N'{_options.QualifiedTableName}', N'U');"),
                    cancellationToken).ConfigureAwait(false);

                if (present is null or DBNull)
                {
                    throw new InvalidOperationException(
                        FormattableString.Invariant(
                            $"The refresh-token table {_options.QualifiedTableName} does not exist and ")
                        + FormattableString.Invariant(
                            $"{RefreshTokenStoreOptions.SectionName}:{nameof(RefreshTokenStoreOptions.CreateTableIfMissing)} is disabled."));
                }

                _bootstrapped = true;

                return;
            }

            string sql = FormattableString.Invariant($@"
IF OBJECT_ID(N'{_options.QualifiedTableName}', N'U') IS NULL
BEGIN
    CREATE TABLE {_options.QualifiedTableName}
    (
        [TokenDigest]           varbinary(32)    NOT NULL,
        [FamilyId]              uniqueidentifier NOT NULL,
        [Generation]            int              NOT NULL,
        [UserId]                int              NOT NULL,
        [PortalId]              int              NOT NULL,
        [ExpiresAtUtc]          datetime2(3)     NOT NULL,
        [FamilyExpiresAtUtc]    datetime2(3)     NOT NULL,
        [ConsumedAtUtc]         datetime2(3)     NULL,
        [ConsumedClientDigest]  varbinary(32)    NULL,
        [RevokedAtUtc]          datetime2(3)     NULL,
        CONSTRAINT [PK_DnnMigrationRefreshTokens] PRIMARY KEY CLUSTERED ([TokenDigest])
    );

    CREATE INDEX [IX_DnnMigrationRefreshTokens_UserId]
        ON {_options.QualifiedTableName} ([UserId]);

    CREATE INDEX [IX_DnnMigrationRefreshTokens_FamilyId]
        ON {_options.QualifiedTableName} ([FamilyId]);

    CREATE INDEX [IX_DnnMigrationRefreshTokens_FamilyExpiresAtUtc]
        ON {_options.QualifiedTableName} ([FamilyExpiresAtUtc]);
END");

            await ExecuteAsync(connection, transaction: null, sql, cancellationToken).ConfigureAwait(false);

            _bootstrapped = true;
        }
        finally
        {
            _bootstrapGate.Release();
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

    /// <summary>Removes rows whose family has expired outright.</summary>
    /// <param name="connection">The open connection.</param>
    /// <param name="transaction">The enclosing transaction.</param>
    /// <param name="now">The current instant.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task that completes when expired rows have been removed.</returns>
    private Task PruneAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        DateTime now,
        CancellationToken cancellationToken) =>
        ExecuteAsync(
            connection,
            transaction,
            FormattableString.Invariant($@"
DELETE FROM {_options.QualifiedTableName}
 WHERE [FamilyExpiresAtUtc] <= @now;"),
            cancellationToken,
            new SqlParameter("@now", SqlDbType.DateTime2) { Value = now });

    /// <summary>Counts still-usable rows, which is what the capacity ceiling bounds.</summary>
    /// <param name="connection">The open connection.</param>
    /// <param name="transaction">The enclosing transaction.</param>
    /// <param name="now">The current instant.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The number of live rows.</returns>
    private async Task<long> CountLiveAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        DateTime now,
        CancellationToken cancellationToken)
    {
        object? count = await ScalarAsync(
            connection,
            transaction,
            FormattableString.Invariant($@"
SELECT COUNT_BIG(1)
  FROM {_options.QualifiedTableName}
 WHERE [RevokedAtUtc] IS NULL AND [FamilyExpiresAtUtc] > @now;"),
            cancellationToken,
            new SqlParameter("@now", SqlDbType.DateTime2) { Value = now }).ConfigureAwait(false);

        return count is null or DBNull
            ? 0
            : Convert.ToInt64(count, CultureInfo.InvariantCulture);
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

    /// <summary>Records a store fault without leaking a token or a connection secret.</summary>
    /// <param name="error">The fault.</param>
    /// <param name="operation">The member that failed.</param>
    private void Unavailable(Exception error, string operation) =>
        _logger.LogError(
            error,
            "The shared refresh-token store could not complete {Operation}. Session operations are reported as unavailable rather than as successful.",
            operation);

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

    /// <summary>Computes the SHA-256 digest of a value.</summary>
    /// <param name="value">The value to digest.</param>
    /// <returns>The digest.</returns>
    private static byte[] Digest(string value) =>
        SHA256.HashData(Encoding.UTF8.GetBytes(value));

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
