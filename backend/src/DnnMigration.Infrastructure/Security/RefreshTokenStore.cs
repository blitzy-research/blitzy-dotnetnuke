using System.Data;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using DnnMigration.Application.Options;
using DnnMigration.Domain.Abstractions.Services;
using DnnMigration.Domain.Enums;
using DnnMigration.Infrastructure.Persistence;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace DnnMigration.Infrastructure.Security;

/// <summary>
/// SQL-backed refresh-token store with atomic single-use rotation, family revocation and bounded
/// same-client concurrent-use grace.
/// </summary>
/// <remarks>
/// <para>
/// MIGRATION: legacy FormsAuthentication sign-out had no server-side session record. The target
/// keeps refresh state durably in the target-owned <c>DnnMigration.RefreshTokens</c> table while
/// access-token sign-out remains expiry plus client-side discard. Only SHA-256 token digests and
/// minimal identity/lifecycle fields are stored; raw refresh tokens are returned once and retained
/// nowhere.
/// </para>
/// <para>
/// The table is provisioned explicitly by the operator-run script under
/// <c>Persistence/Scripts</c>. Application startup never creates, alters or migrates a production
/// schema, and no legacy DotNetNuke object is modified.
/// </para>
/// </remarks>
internal sealed class RefreshTokenStore : IRefreshTokenStore
{
    private const int TokenEntropyBytes = 32;
    private const int DigestLength = 32;
    private const int ExpiredPruneBatchSize = 500;
    private const int ConcurrentUseGraceSeconds = 5;
    private const string TableName = "[DnnMigration].[RefreshTokens]";

    private static readonly TimeSpan ConcurrentUseGrace =
        TimeSpan.FromSeconds(ConcurrentUseGraceSeconds);

    private readonly string _connectionString;
    private readonly IClock _clock;
    private readonly TimeSpan _slidingLifetime;
    private readonly TimeSpan _familyLifetime;

    /// <summary>Initialises a new instance of the <see cref="RefreshTokenStore"/> class.</summary>
    /// <param name="context">Supplies the configured SQL Server connection string.</param>
    /// <param name="clock">UTC clock used for every lifecycle decision.</param>
    /// <param name="jwtOptions">Validated access and refresh-token options.</param>
    /// <exception cref="ArgumentNullException">Any argument is <see langword="null"/>.</exception>
    /// <exception cref="OptionsValidationException">The token lifetimes are invalid.</exception>
    /// <exception cref="InvalidOperationException">No database connection string is configured.</exception>
    public RefreshTokenStore(
        DnnDbContext context,
        IClock clock,
        IOptions<JwtOptions> jwtOptions)
    {
        ArgumentNullException.ThrowIfNull(context);
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

        _connectionString = context.Database.GetConnectionString()
            ?? throw new InvalidOperationException(
                "The durable refresh-token store requires ConnectionStrings:Default.");
        _clock = clock;
        _slidingLifetime = TimeSpan.FromDays(jwtOptions.Value.RefreshTokenExpirationDays);
        _familyLifetime = TimeSpan.FromDays(jwtOptions.Value.RefreshTokenAbsoluteExpirationDays);
    }

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

            await PruneExpiredAsync(connection, transaction, now, cancellationToken).ConfigureAwait(false);

            await using SqlCommand command = Command(
                connection,
                transaction,
                $"""
                INSERT INTO {TableName}
                    ([TokenDigest], [FamilyId], [Generation], [UserId], [PortalId],
                     [CreatedAtUtc], [ExpiresAtUtc], [FamilyExpiresAtUtc],
                     [ConsumedAtUtc], [ConsumedClientDigest], [RevokedAtUtc])
                VALUES
                    (@digest, @familyId, 0, @userId, @portalId,
                     @now, @expires, @familyExpires, NULL, NULL, NULL);
                """);
            Binary(command, "@digest", token.Digest);
            command.Parameters.Add(new SqlParameter("@familyId", SqlDbType.UniqueIdentifier)
            {
                Value = Guid.NewGuid(),
            });
            command.Parameters.Add(new SqlParameter("@userId", SqlDbType.Int)
            {
                Value = subject.UserId,
            });
            command.Parameters.Add(new SqlParameter("@portalId", SqlDbType.Int)
            {
                Value = subject.PortalId,
            });
            DateTimeParameter(command, "@now", now);
            DateTimeParameter(command, "@expires", expiresAtUtc);
            DateTimeParameter(command, "@familyExpires", familyExpiresAtUtc);

            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

            return RefreshTokenIssueResult.Succeeded(
                token.RawToken,
                expiresAtUtc,
                subject);
        }
        catch (Exception exception) when (exception is SqlException or InvalidOperationException)
        {
            return RefreshTokenIssueResult.Failed(RefreshTokenOutcome.StoreUnavailable);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(token.Digest);
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

            StoredToken? stored = await ReadForUpdateAsync(
                    connection,
                    transaction,
                    digest,
                    cancellationToken)
                .ConfigureAwait(false);

            if (stored is null)
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return RefreshTokenInspection.Failed(RefreshTokenOutcome.Unknown);
            }

            RefreshTokenOutcome outcome = Classify(stored, clientDigest, now);
            if (outcome == RefreshTokenOutcome.AlreadyUsed)
            {
                await RevokeAllForUserCoreAsync(
                        connection,
                        transaction,
                        stored.UserId,
                        now,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

            return outcome == RefreshTokenOutcome.Succeeded
                ? RefreshTokenInspection.Succeeded(
                    new RefreshTokenSubject(stored.UserId, stored.PortalId),
                    stored.ExpiresAtUtc)
                : RefreshTokenInspection.Failed(outcome, stored.UserId);
        }
        catch (Exception exception) when (exception is SqlException or InvalidOperationException)
        {
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

            StoredToken? stored = await ReadForUpdateAsync(
                    connection,
                    transaction,
                    digest,
                    cancellationToken)
                .ConfigureAwait(false);

            if (stored is null)
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return RefreshTokenRotationResult.Failed(RefreshTokenOutcome.Unknown);
            }

            RefreshTokenOutcome outcome = Classify(stored, clientDigest, now);
            if (outcome != RefreshTokenOutcome.Succeeded)
            {
                if (outcome == RefreshTokenOutcome.AlreadyUsed)
                {
                    await RevokeAllForUserCoreAsync(
                            connection,
                            transaction,
                            stored.UserId,
                            now,
                            cancellationToken)
                        .ConfigureAwait(false);
                }

                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return RefreshTokenRotationResult.Failed(outcome);
            }

            TokenMaterial replacement = CreateToken();
            try
            {
                await MarkConsumedAsync(
                        connection,
                        transaction,
                        digest,
                        clientDigest,
                        now,
                        cancellationToken)
                    .ConfigureAwait(false);

                DateTime replacementExpiresAtUtc = Earlier(
                    now.Add(_slidingLifetime),
                    stored.FamilyExpiresAtUtc);

                await InsertReplacementAsync(
                        connection,
                        transaction,
                        replacement.Digest,
                        stored,
                        now,
                        replacementExpiresAtUtc,
                        cancellationToken)
                    .ConfigureAwait(false);

                await PruneExpiredAsync(connection, transaction, now, cancellationToken)
                    .ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

                RefreshTokenSubject subject = new(stored.UserId, stored.PortalId);
                return RefreshTokenRotationResult.Succeeded(
                    replacement.RawToken,
                    replacementExpiresAtUtc,
                    subject);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(replacement.Digest);
            }
        }
        catch (Exception exception) when (exception is SqlException or InvalidOperationException)
        {
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

            StoredToken? stored = await ReadForUpdateAsync(
                    connection,
                    transaction,
                    digest,
                    cancellationToken)
                .ConfigureAwait(false);

            if (stored is null)
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return RefreshTokenOutcome.Unknown;
            }

            int changed = await RevokeFamilyCoreAsync(
                    connection,
                    transaction,
                    stored.FamilyId,
                    now,
                    cancellationToken)
                .ConfigureAwait(false);

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return changed == 0
                ? RefreshTokenOutcome.AlreadyRevoked
                : RefreshTokenOutcome.Succeeded;
        }
        catch (Exception exception) when (exception is SqlException or InvalidOperationException)
        {
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

            int known = await CountForUserAsync(
                    connection,
                    transaction,
                    userId,
                    cancellationToken)
                .ConfigureAwait(false);
            if (known == 0)
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return RefreshTokenOutcome.Unknown;
            }

            int changed = await RevokeAllForUserCoreAsync(
                    connection,
                    transaction,
                    userId,
                    now,
                    cancellationToken)
                .ConfigureAwait(false);

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return changed == 0
                ? RefreshTokenOutcome.AlreadyRevoked
                : RefreshTokenOutcome.Succeeded;
        }
        catch (Exception exception) when (exception is SqlException or InvalidOperationException)
        {
            return RefreshTokenOutcome.StoreUnavailable;
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

    private async Task<SqlConnection> OpenAsync(CancellationToken cancellationToken)
    {
        SqlConnection connection = new(_connectionString);

        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static async Task<StoredToken?> ReadForUpdateAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        byte[] digest,
        CancellationToken cancellationToken)
    {
        await using SqlCommand command = Command(
            connection,
            transaction,
            $"""
            SELECT [FamilyId], [Generation], [UserId], [PortalId],
                   [ExpiresAtUtc], [FamilyExpiresAtUtc], [ConsumedAtUtc],
                   [ConsumedClientDigest], [RevokedAtUtc]
            FROM {TableName} WITH (UPDLOCK, HOLDLOCK)
            WHERE [TokenDigest] = @digest;
            """);
        Binary(command, "@digest", digest);

        await using SqlDataReader reader = await command
            .ExecuteReaderAsync(CommandBehavior.SingleRow, cancellationToken)
            .ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return new StoredToken(
            reader.GetGuid(0),
            reader.GetInt32(1),
            reader.GetInt32(2),
            reader.GetInt32(3),
            Utc(reader.GetDateTime(4)),
            Utc(reader.GetDateTime(5)),
            reader.IsDBNull(6) ? null : Utc(reader.GetDateTime(6)),
            reader.IsDBNull(7) ? null : reader.GetFieldValue<byte[]>(7),
            reader.IsDBNull(8) ? null : Utc(reader.GetDateTime(8)));
    }

    private static async Task MarkConsumedAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        byte[] digest,
        byte[] clientDigest,
        DateTime now,
        CancellationToken cancellationToken)
    {
        await using SqlCommand command = Command(
            connection,
            transaction,
            $"""
            UPDATE {TableName}
            SET [ConsumedAtUtc] = @now,
                [ConsumedClientDigest] = @clientDigest
            WHERE [TokenDigest] = @digest
              AND [ConsumedAtUtc] IS NULL
              AND [RevokedAtUtc] IS NULL;
            """);
        Binary(command, "@digest", digest);
        Binary(command, "@clientDigest", clientDigest);
        DateTimeParameter(command, "@now", now);

        int changed = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        if (changed != 1)
        {
            throw new InvalidOperationException(
                "The refresh-token row changed after it was locked for rotation.");
        }
    }

    private static async Task InsertReplacementAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        byte[] replacementDigest,
        StoredToken stored,
        DateTime now,
        DateTime expiresAtUtc,
        CancellationToken cancellationToken)
    {
        if (stored.Generation == int.MaxValue)
        {
            throw new InvalidOperationException(
                "The refresh-token family exhausted its generation counter.");
        }

        await using SqlCommand command = Command(
            connection,
            transaction,
            $"""
            INSERT INTO {TableName}
                ([TokenDigest], [FamilyId], [Generation], [UserId], [PortalId],
                 [CreatedAtUtc], [ExpiresAtUtc], [FamilyExpiresAtUtc],
                 [ConsumedAtUtc], [ConsumedClientDigest], [RevokedAtUtc])
            VALUES
                (@digest, @familyId, @generation, @userId, @portalId,
                 @now, @expires, @familyExpires, NULL, NULL, NULL);
            """);
        Binary(command, "@digest", replacementDigest);
        command.Parameters.Add(new SqlParameter("@familyId", SqlDbType.UniqueIdentifier)
        {
            Value = stored.FamilyId,
        });
        command.Parameters.Add(new SqlParameter("@generation", SqlDbType.Int)
        {
            Value = checked(stored.Generation + 1),
        });
        command.Parameters.Add(new SqlParameter("@userId", SqlDbType.Int)
        {
            Value = stored.UserId,
        });
        command.Parameters.Add(new SqlParameter("@portalId", SqlDbType.Int)
        {
            Value = stored.PortalId,
        });
        DateTimeParameter(command, "@now", now);
        DateTimeParameter(command, "@expires", expiresAtUtc);
        DateTimeParameter(command, "@familyExpires", stored.FamilyExpiresAtUtc);

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<int> RevokeFamilyCoreAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        Guid familyId,
        DateTime now,
        CancellationToken cancellationToken)
    {
        await using SqlCommand command = Command(
            connection,
            transaction,
            $"""
            UPDATE {TableName}
            SET [RevokedAtUtc] = @now
            WHERE [FamilyId] = @familyId
              AND [RevokedAtUtc] IS NULL;
            """);
        command.Parameters.Add(new SqlParameter("@familyId", SqlDbType.UniqueIdentifier)
        {
            Value = familyId,
        });
        DateTimeParameter(command, "@now", now);
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<int> RevokeAllForUserCoreAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        int userId,
        DateTime now,
        CancellationToken cancellationToken)
    {
        await using SqlCommand command = Command(
            connection,
            transaction,
            $"""
            UPDATE {TableName}
            SET [RevokedAtUtc] = @now
            WHERE [UserId] = @userId
              AND [RevokedAtUtc] IS NULL;
            """);
        command.Parameters.Add(new SqlParameter("@userId", SqlDbType.Int)
        {
            Value = userId,
        });
        DateTimeParameter(command, "@now", now);
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<int> CountForUserAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        int userId,
        CancellationToken cancellationToken)
    {
        await using SqlCommand command = Command(
            connection,
            transaction,
            $"""
            SELECT COUNT_BIG(*)
            FROM {TableName} WITH (UPDLOCK, HOLDLOCK)
            WHERE [UserId] = @userId;
            """);
        command.Parameters.Add(new SqlParameter("@userId", SqlDbType.Int)
        {
            Value = userId,
        });

        object? count = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        long value = count is null or DBNull ? 0 : Convert.ToInt64(count, CultureInfo.InvariantCulture);
        return value > int.MaxValue ? int.MaxValue : (int)value;
    }

    private static async Task PruneExpiredAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        DateTime now,
        CancellationToken cancellationToken)
    {
        await using SqlCommand command = Command(
            connection,
            transaction,
            $"""
            DELETE TOP ({ExpiredPruneBatchSize})
            FROM {TableName}
            WHERE [FamilyExpiresAtUtc] <= @now;
            """);
        DateTimeParameter(command, "@now", now);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static SqlCommand Command(
        SqlConnection connection,
        SqlTransaction transaction,
        string commandText)
    {
        SqlCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = commandText;
        return command;
    }

    private static void Binary(SqlCommand command, string name, byte[] value)
    {
        command.Parameters.Add(new SqlParameter(name, SqlDbType.Binary, DigestLength)
        {
            Value = value,
        });
    }

    private static void DateTimeParameter(SqlCommand command, string name, DateTime value)
    {
        command.Parameters.Add(new SqlParameter(name, SqlDbType.DateTime2)
        {
            Value = value,
        });
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

    private sealed class StoredToken
    {
        public StoredToken(
            Guid familyId,
            int generation,
            int userId,
            int portalId,
            DateTime expiresAtUtc,
            DateTime familyExpiresAtUtc,
            DateTime? consumedAtUtc,
            byte[]? consumedClientDigest,
            DateTime? revokedAtUtc)
        {
            FamilyId = familyId;
            Generation = generation;
            UserId = userId;
            PortalId = portalId;
            ExpiresAtUtc = expiresAtUtc;
            FamilyExpiresAtUtc = familyExpiresAtUtc;
            ConsumedAtUtc = consumedAtUtc;
            ConsumedClientDigest = consumedClientDigest;
            RevokedAtUtc = revokedAtUtc;
        }

        public Guid FamilyId { get; }

        public int Generation { get; }

        public int UserId { get; }

        public int PortalId { get; }

        public DateTime ExpiresAtUtc { get; }

        public DateTime FamilyExpiresAtUtc { get; }

        public DateTime? ConsumedAtUtc { get; }

        public byte[]? ConsumedClientDigest { get; }

        public DateTime? RevokedAtUtc { get; }
    }
}
