using System.Data;
using System.Data.Common;
using DnnMigration.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace DnnMigration.Infrastructure.Persistence;

/// <summary>
/// The approval, lock-out and activity facts an account carries in the external ASP.NET membership store.
/// Deliberately carries no credential material.
/// </summary>
/// <param name="IsApproved">Whether the account is approved for use.</param>
/// <param name="IsLockedOut">Whether the account is locked out.</param>
/// <param name="CreatedDate">
/// When the credential record was created, or <see langword="null"/> when never recorded.
/// </param>
/// <param name="LastLoginDate">The last successful sign-in, or <see langword="null"/> when never recorded.</param>
/// <param name="LastActivityDate">The last recorded activity, or <see langword="null"/> when never recorded.</param>
/// <param name="LastLockoutDate">The last lock-out, or <see langword="null"/> when never recorded.</param>
/// <param name="LastPasswordChangeDate">
/// The last password change, or <see langword="null"/> when never recorded.
/// </param>
/// <remarks>
/// This type exists separately from <see cref="MembershipCredentialSnapshot"/> so that a password hash
/// cannot reach a listing by accident: the members that populate a <see cref="Domain.Entities.User"/> can
/// only obtain this shape, and it has no field capable of carrying one.
/// </remarks>
internal sealed record MembershipAccountSnapshot(
    bool IsApproved,
    bool IsLockedOut,
    DateTime? CreatedDate,
    DateTime? LastLoginDate,
    DateTime? LastActivityDate,
    DateTime? LastLockoutDate,
    DateTime? LastPasswordChangeDate);

/// <summary>The credential state authentication needs in order to decide a sign-in.</summary>
/// <param name="PasswordValue">
/// The stored credential representation, or <see langword="null"/> when none is stored.
/// </param>
/// <param name="Format">The persisted legacy format discriminator, or <see langword="null"/> when invalid.</param>
/// <param name="PasswordSalt">The base-64 membership salt, or <see langword="null"/> when none is stored.</param>
/// <param name="IsApproved">Whether the account is approved for use.</param>
/// <param name="IsLockedOut">Whether the account is locked out.</param>
/// <remarks>
/// Obtained only by <see cref="MembershipStore.GetCredentialStateAsync"/>. None of its credential material
/// may be placed in a result returned across the API boundary, in a log entry or in a message.
/// </remarks>
internal sealed record MembershipCredentialSnapshot(
    string? PasswordValue,
    PasswordFormat? Format,
    string? PasswordSalt,
    bool IsApproved,
    bool IsLockedOut);

/// <summary>Addresses the external ASP.NET membership store that holds DotNetNuke credentials.</summary>
/// <remarks>
/// <strong>Availability.</strong> Because the objects are provisioned outside this repository's schema
/// chain, they are absent from a greenfield database and from every non-SQL-Server provider. <see
/// cref="IsAvailableAsync"/> establishes their presence once per instance and every member fails closed
/// when they are missing, reporting the truthful "no credential record" answer rather than fabricating one
/// or throwing an object-name error.
/// </remarks>
internal sealed class MembershipStore
{
    /// <summary>The ASP.NET membership application that owns DotNetNuke's accounts.</summary>
    private const string MembershipApplicationName = "DotNetNuke";

    /// <summary>The stored <c>PasswordFormat</c> discriminator for a one-way hash.</summary>
    /// <remarks>
    /// Matches <see cref="Domain.Enums.PasswordFormat.Hashed"/>. The legacy store wrote <c>2</c>
    /// (<c>Encrypted</c>), so this value additionally marks a row whose credential has been migrated to a
    /// one-way hash and distinguishes it from one that has not.
    /// </remarks>
    private const int HashedPasswordFormat = 1;

    /// <summary>The number of user names bound into a single batched read.</summary>
    private const int UserNameBatchSize = 500;

    /// <summary>The instant the ASP.NET membership schema uses to mean "never".</summary>
    /// <remarks>
    /// The legacy procedures write <c>CONVERT(datetime, '17540101', 112)</c> - see the correct-password
    /// branch of <c>aspnet_Membership_UpdateUserInfo</c> in the 04.00.00 script. The columns are <c>NOT
    /// NULL</c>, so the sentinel is how the schema expresses absence.
    /// </remarks>
    private static readonly DateTime NeverRecorded = new(1754, 1, 1, 0, 0, 0, DateTimeKind.Unspecified);

    private readonly DnnDbContext _context;
    private bool? _available;

    /// <summary>Initialises a new instance of the <see cref="MembershipStore"/> class.</summary>
    /// <param name="context">The unit-of-work scoped database context whose connection is borrowed.</param>
    /// <exception cref="ArgumentNullException"><paramref name="context"/> is <see langword="null"/>.</exception>
    public MembershipStore(DnnDbContext context)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
    }

    /// <summary>Determines whether the external membership store can be reached.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns><see langword="true"/> when both membership tables are present on a SQL Server connection.</returns>
    public async Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default)
    {
        if (_available.HasValue)
        {
            return _available.Value;
        }

        if (!_context.Database.IsSqlServer())
        {
            _available = false;
            return false;
        }

        const string Sql = @"
SELECT CASE
           WHEN OBJECT_ID(N'[dbo].[aspnet_Users]') IS NOT NULL
            AND OBJECT_ID(N'[dbo].[aspnet_Membership]') IS NOT NULL
            AND OBJECT_ID(N'[dbo].[aspnet_Applications]') IS NOT NULL
           THEN 1 ELSE 0
       END;";

        object? probe = await ExecuteAsync(
                Sql,
                Array.Empty<KeyValuePair<string, object?>>(),
                static (command, token) => command.ExecuteScalarAsync(token),
                cancellationToken)
            .ConfigureAwait(false);

        _available = probe is not null && Convert.ToInt32(probe, System.Globalization.CultureInfo.InvariantCulture) == 1;
        return _available.Value;
    }

    /// <summary>
    /// Returns the <c>dbo.Users</c> query root restricted to accounts whose credential record carries the
    /// requested approval state.
    /// </summary>
    /// <param name="isApproved">The approval state to select.</param>
    /// <returns>A composable query root over <see cref="Domain.Entities.User"/>.</returns>
    /// <remarks>
    /// The approval state and the application name cross as bound parameters, not as text. Callers must
    /// establish <see cref="IsAvailableAsync"/> first: the statement names objects that a greenfield
    /// database and every non-SQL-Server provider lack, and issuing it against either would raise an
    /// object-name error rather than return an empty set.
    /// </remarks>
    public IQueryable<Domain.Entities.User> ApprovedUsers(bool isApproved)
    {
        string application = MembershipApplicationName.ToLowerInvariant();

        return _context.Users.FromSqlInterpolated($@"
SELECT u.*
FROM [dbo].[Users] u
WHERE EXISTS (
    SELECT 1
    FROM [dbo].[aspnet_Users] au
    INNER JOIN [dbo].[aspnet_Applications] aa ON aa.[ApplicationId] = au.[ApplicationId]
    INNER JOIN [dbo].[aspnet_Membership] am ON am.[UserId] = au.[UserId]
    WHERE au.[LoweredUserName] = LOWER(u.[Username])
      AND aa.[LoweredApplicationName] = {application}
      AND am.[IsApproved] = {isApproved})");
    }

    /// <summary>Reads the account facts of one user name.</summary>
    /// <param name="userName">The DotNetNuke user name.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The snapshot, or <see langword="null"/> when the store holds no record for that name.</returns>
    public async Task<MembershipAccountSnapshot?> GetAccountSnapshotAsync(string userName, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(userName);

        IReadOnlyDictionary<string, MembershipAccountSnapshot> snapshots =
            await GetAccountSnapshotsAsync(new[] { userName }, cancellationToken).ConfigureAwait(false);

        return snapshots.TryGetValue(userName, out MembershipAccountSnapshot? snapshot) ? snapshot : null;
    }

    /// <summary>Reads the account facts of many user names in batches.</summary>
    /// <param name="userNames">The DotNetNuke user names to read.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// A case-insensitive map from user name to snapshot, containing only the names the store holds a
    /// record for.
    /// </returns>
    public async Task<IReadOnlyDictionary<string, MembershipAccountSnapshot>> GetAccountSnapshotsAsync(
        IReadOnlyCollection<string> userNames,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(userNames);

        Dictionary<string, MembershipAccountSnapshot> snapshots = new(StringComparer.OrdinalIgnoreCase);

        if (userNames.Count == 0 || !await IsAvailableAsync(cancellationToken).ConfigureAwait(false))
        {
            return snapshots;
        }

        List<string> distinct = Distinct(userNames);

        for (int offset = 0; offset < distinct.Count; offset += UserNameBatchSize)
        {
            List<string> batch = distinct.GetRange(offset, Math.Min(UserNameBatchSize, distinct.Count - offset));
            await ReadAccountBatchAsync(batch, snapshots, cancellationToken).ConfigureAwait(false);
        }

        return snapshots;
    }

    /// <summary>Reads the credential state of one user name.</summary>
    /// <param name="userName">The DotNetNuke user name.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The credential snapshot, or <see langword="null"/> when no credential record exists.</returns>
    public async Task<MembershipCredentialSnapshot?> GetCredentialStateAsync(string userName, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(userName);

        if (!await IsAvailableAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        const string Sql = @"
SELECT am.[Password], am.[PasswordFormat], am.[PasswordSalt], am.[IsApproved], am.[IsLockedOut]
FROM [dbo].[aspnet_Users] au
INNER JOIN [dbo].[aspnet_Applications] aa ON aa.[ApplicationId] = au.[ApplicationId]
INNER JOIN [dbo].[aspnet_Membership] am ON am.[UserId] = au.[UserId]
WHERE aa.[LoweredApplicationName] = @app AND au.[LoweredUserName] = @user;";

        return await ExecuteAsync(
                Sql,
                Parameters(userName),
                static async (command, token) =>
                {
                    using DbDataReader reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);

                    if (!await reader.ReadAsync(token).ConfigureAwait(false))
                    {
                        return null;
                    }

                    int storedFormat = reader.GetInt32(1);
                    PasswordFormat? format = Enum.IsDefined(typeof(PasswordFormat), storedFormat)
                        ? (PasswordFormat)storedFormat
                        : null;

                    return new MembershipCredentialSnapshot(
                        reader.IsDBNull(0) ? null : reader.GetString(0),
                        format,
                        reader.IsDBNull(2) ? null : reader.GetString(2),
                        reader.GetBoolean(3),
                        reader.GetBoolean(4));
                },
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Creates the credential record of a newly registered account.</summary>
    /// <param name="userName">The DotNetNuke user name.</param>
    /// <param name="passwordHash">The one-way hash to store.</param>
    /// <param name="isApproved">Whether the account is usable immediately.</param>
    /// <param name="utcNow">The creation instant.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns><see langword="true"/> when a credential record was created.</returns>
    /// <remarks>
    /// <c>PasswordSalt</c> is written empty because BCrypt embeds its salt inside the hash, and the column
    /// is <c>NOT NULL</c> so it cannot simply be omitted. The membership <c>Email</c> columns are left
    /// null: this member takes no address, and <c>dbo.Users.Email</c> is authoritative in the target.
    /// </remarks>
    public async Task<bool> CreateAsync(
        string userName,
        string passwordHash,
        bool isApproved,
        DateTime utcNow,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(userName);
        ArgumentNullException.ThrowIfNull(passwordHash);

        if (!await IsAvailableAsync(cancellationToken).ConfigureAwait(false))
        {
            return false;
        }

        const string Sql = @"
DECLARE @applicationId uniqueidentifier;
DECLARE @membershipUserId uniqueidentifier;
DECLARE @created int = 0;

SELECT @applicationId = [ApplicationId]
FROM [dbo].[aspnet_Applications]
WHERE [LoweredApplicationName] = @app;

IF @applicationId IS NULL
BEGIN
    SET @applicationId = NEWID();
    INSERT INTO [dbo].[aspnet_Applications] ([ApplicationName], [LoweredApplicationName], [ApplicationId], [Description])
    VALUES (@applicationName, @app, @applicationId, NULL);
END

SELECT @membershipUserId = [UserId]
FROM [dbo].[aspnet_Users]
WHERE [ApplicationId] = @applicationId AND [LoweredUserName] = @user;

IF @membershipUserId IS NULL
BEGIN
    SET @membershipUserId = NEWID();
    INSERT INTO [dbo].[aspnet_Users]
        ([ApplicationId], [UserId], [UserName], [LoweredUserName], [MobileAlias], [IsAnonymous], [LastActivityDate])
    VALUES (@applicationId, @membershipUserId, @userName, @user, NULL, 0, @now);
END

IF NOT EXISTS (SELECT 1 FROM [dbo].[aspnet_Membership] WHERE [UserId] = @membershipUserId)
BEGIN
    INSERT INTO [dbo].[aspnet_Membership]
        ([ApplicationId], [UserId], [Password], [PasswordFormat], [PasswordSalt], [MobilePIN],
         [Email], [LoweredEmail], [PasswordQuestion], [PasswordAnswer], [IsApproved], [IsLockedOut],
         [CreateDate], [LastLoginDate], [LastPasswordChangedDate], [LastLockoutDate],
         [FailedPasswordAttemptCount], [FailedPasswordAttemptWindowStart],
         [FailedPasswordAnswerAttemptCount], [FailedPasswordAnswerAttemptWindowStart], [Comment])
    VALUES
        (@applicationId, @membershipUserId, @hash, @format, N'', NULL,
         NULL, NULL, NULL, NULL, @approved, 0,
         @now, @never, @now, @never,
         0, @never,
         0, @never, NULL);

    SET @created = 1;
END

SELECT @created;";

        List<KeyValuePair<string, object?>> parameters = Parameters(userName);
        parameters.Add(new KeyValuePair<string, object?>("@applicationName", MembershipApplicationName));
        parameters.Add(new KeyValuePair<string, object?>("@userName", userName));
        parameters.Add(new KeyValuePair<string, object?>("@hash", passwordHash));
        parameters.Add(new KeyValuePair<string, object?>("@format", HashedPasswordFormat));
        parameters.Add(new KeyValuePair<string, object?>("@approved", isApproved));
        parameters.Add(new KeyValuePair<string, object?>("@now", utcNow));
        parameters.Add(new KeyValuePair<string, object?>("@never", NeverRecorded));

        object? created = await ExecuteAsync(
                Sql,
                parameters,
                static (command, token) => command.ExecuteScalarAsync(token),
                cancellationToken)
            .ConfigureAwait(false);

        return created is not null
            && Convert.ToInt32(created, System.Globalization.CultureInfo.InvariantCulture) == 1;
    }

    /// <summary>
    /// Replaces the stored password hash of an account, only while it is still the one the caller read.
    /// </summary>
    /// <param name="userName">The DotNetNuke user name.</param>
    /// <param name="passwordHash">The new one-way hash.</param>
    /// <param name="expectedPasswordValue">
    /// The stored representation the caller read and decided against, or <see langword="null"/> when it
    /// read no stored value at all.
    /// </param>
    /// <param name="utcNow">The change instant.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// <see cref="CredentialWriteOutcome.Replaced"/> when the credential was replaced, <see
    /// cref="CredentialWriteOutcome.Superseded"/> when it had already changed, <see
    /// cref="CredentialWriteOutcome.NoRecord"/> when the account holds no credential record, and <see
    /// cref="CredentialWriteOutcome.StoreUnavailable"/> when the store could not be reached.
    /// </returns>
    /// <remarks>
    /// ⚠ THE EXPECTATION IS PART OF THE STATEMENT, WHICH IS WHAT MAKES THIS SAFE ACROSS REPLICAS. The
    /// predicate is evaluated by the database in the same statement that performs the update, so two
    /// callers that read the same representation cannot both write: the second finds the row no longer
    /// matching and affects nothing.
    /// </remarks>
    public async Task<CredentialWriteOutcome> SetPasswordHashAsync(
        string userName,
        string passwordHash,
        string? expectedPasswordValue,
        DateTime utcNow,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(userName);
        ArgumentNullException.ThrowIfNull(passwordHash);

        if (!await IsAvailableAsync(cancellationToken).ConfigureAwait(false))
        {
            return CredentialWriteOutcome.StoreUnavailable;
        }

        const string Sql = @"
UPDATE am
SET am.[Password] = @hash,
    am.[PasswordFormat] = @format,
    am.[PasswordSalt] = N'',
    am.[LastPasswordChangedDate] = @now
FROM [dbo].[aspnet_Membership] am
INNER JOIN [dbo].[aspnet_Users] au ON au.[UserId] = am.[UserId]
INNER JOIN [dbo].[aspnet_Applications] aa ON aa.[ApplicationId] = au.[ApplicationId]
WHERE aa.[LoweredApplicationName] = @app
  AND au.[LoweredUserName] = @user
  AND (
        (@expected IS NOT NULL AND am.[Password] COLLATE Latin1_General_BIN2 = @expected)
     OR (@expected IS NULL AND am.[Password] IS NULL)
      );";

        List<KeyValuePair<string, object?>> parameters = Parameters(userName);
        parameters.Add(new KeyValuePair<string, object?>("@hash", passwordHash));
        parameters.Add(new KeyValuePair<string, object?>("@format", HashedPasswordFormat));
        parameters.Add(new KeyValuePair<string, object?>("@now", utcNow));
        parameters.Add(new KeyValuePair<string, object?>("@expected", expectedPasswordValue));

        if (await AffectedAsync(Sql, parameters, cancellationToken).ConfigureAwait(false))
        {
            return CredentialWriteOutcome.Replaced;
        }

        return await CredentialRecordExistsAsync(userName, cancellationToken).ConfigureAwait(false)
            ? CredentialWriteOutcome.Superseded
            : CredentialWriteOutcome.NoRecord;
    }

    /// <summary>Reports whether an account holds a credential record at all.</summary>
    /// <param name="userName">The DotNetNuke user name.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns><see langword="true"/> when a membership credential row exists for the account.</returns>
    /// <remarks>
    /// Reads NOTHING about the credential - not the representation, not the format, not the salt - because
    /// the only question it answers is whether a row is there. Its one caller is the replacement above,
    /// which needs to tell a refused expectation from an absent record without widening the surface through
    /// which credential material can be read.
    /// </remarks>
    private async Task<bool> CredentialRecordExistsAsync(
        string userName,
        CancellationToken cancellationToken)
    {
        const string Sql = @"
SELECT CAST(1 AS int)
FROM [dbo].[aspnet_Users] au
INNER JOIN [dbo].[aspnet_Applications] aa ON aa.[ApplicationId] = au.[ApplicationId]
INNER JOIN [dbo].[aspnet_Membership] am ON am.[UserId] = au.[UserId]
WHERE aa.[LoweredApplicationName] = @app AND au.[LoweredUserName] = @user;";

        object? present = await ExecuteAsync(
                Sql,
                Parameters(userName),
                static (command, token) => command.ExecuteScalarAsync(token),
                cancellationToken)
            .ConfigureAwait(false);

        return present is not null and not DBNull;
    }

    /// <summary>Clears the failure counters and stamps a successful sign-in.</summary>
    /// <param name="userName">The DotNetNuke user name.</param>
    /// <param name="utcNow">The sign-in instant.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// <see cref="MembershipWriteOutcome.Recorded"/> when a credential record was updated, <see
    /// cref="MembershipWriteOutcome.NoRecord"/> when no record matched, and <see
    /// cref="MembershipWriteOutcome.StoreUnavailable"/> when the store is not installed or not reachable.
    /// </returns>
    /// <remarks>
    /// The three outcomes are reported separately because an absent store and an absent record are not the
    /// same fact and must not be answered with one value: the first means the counter reset never ran, the
    /// second means there was nothing to reset. A boolean forced them together, and a caller could then not
    /// tell a working control from a broken one.
    /// </remarks>
    public async Task<MembershipWriteOutcome> RecordSuccessfulLoginAsync(
        string userName,
        DateTime utcNow,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(userName);

        if (!await IsAvailableAsync(cancellationToken).ConfigureAwait(false))
        {
            return MembershipWriteOutcome.StoreUnavailable;
        }

        const string Sql = @"
UPDATE am
SET am.[FailedPasswordAttemptCount] = 0,
    am.[FailedPasswordAnswerAttemptCount] = 0,
    am.[FailedPasswordAttemptWindowStart] =
        CASE WHEN am.[FailedPasswordAttemptCount] > 0 OR am.[FailedPasswordAnswerAttemptCount] > 0
             THEN @never ELSE am.[FailedPasswordAttemptWindowStart] END,
    am.[FailedPasswordAnswerAttemptWindowStart] =
        CASE WHEN am.[FailedPasswordAttemptCount] > 0 OR am.[FailedPasswordAnswerAttemptCount] > 0
             THEN @never ELSE am.[FailedPasswordAnswerAttemptWindowStart] END,
    am.[LastLockoutDate] =
        CASE WHEN am.[FailedPasswordAttemptCount] > 0 OR am.[FailedPasswordAnswerAttemptCount] > 0
             THEN @never ELSE am.[LastLockoutDate] END,
    am.[LastLoginDate] = @now
FROM [dbo].[aspnet_Membership] am
INNER JOIN [dbo].[aspnet_Users] au ON au.[UserId] = am.[UserId]
INNER JOIN [dbo].[aspnet_Applications] aa ON aa.[ApplicationId] = au.[ApplicationId]
WHERE aa.[LoweredApplicationName] = @app AND au.[LoweredUserName] = @user;

UPDATE au
SET au.[LastActivityDate] = @now
FROM [dbo].[aspnet_Users] au
INNER JOIN [dbo].[aspnet_Applications] aa ON aa.[ApplicationId] = au.[ApplicationId]
WHERE aa.[LoweredApplicationName] = @app AND au.[LoweredUserName] = @user;";

        List<KeyValuePair<string, object?>> parameters = Parameters(userName);
        parameters.Add(new KeyValuePair<string, object?>("@now", utcNow));
        parameters.Add(new KeyValuePair<string, object?>("@never", NeverRecorded));

        return await AffectedAsync(Sql, parameters, cancellationToken).ConfigureAwait(false)
            ? MembershipWriteOutcome.Recorded
            : MembershipWriteOutcome.NoRecord;
    }

    /// <summary>Records a failed sign-in and locks the account once the threshold is reached.</summary>
    /// <param name="userName">The DotNetNuke user name.</param>
    /// <param name="lockoutThreshold">The number of consecutive failures that locks the account.</param>
    /// <param name="attemptWindow">The window within which consecutive failures accumulate.</param>
    /// <param name="utcNow">The failure instant.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// <see cref="MembershipWriteOutcome.RecordedAndLocked"/> when the account is locked out after this
    /// failure, <see cref="MembershipWriteOutcome.Recorded"/> when the failure was counted and the account
    /// is not locked, <see cref="MembershipWriteOutcome.NoRecord"/> when the account holds no credential
    /// record, and <see cref="MembershipWriteOutcome.StoreUnavailable"/> when the store is not installed or
    /// not reachable - in which case THE FAILURE WAS NOT COUNTED AT ALL.
    /// </returns>
    /// <remarks>
    /// The new lock-out state is read back through the update's <c>OUTPUT</c> clause so that the decision
    /// and the report are one atomic statement; a separate read afterwards could observe a concurrent
    /// change. The follow-up read runs only when nothing was updated, which distinguishes an account that
    /// was already locked - and is therefore still locked out - from one that does not exist.
    /// </remarks>
    public async Task<MembershipWriteOutcome> RecordFailedLoginAsync(
        string userName,
        int lockoutThreshold,
        TimeSpan attemptWindow,
        DateTime utcNow,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(userName);

        // The one outcome that is NOT about the account: the counter that produces a lock-out could not be
        // incremented, so this attempt is unrecorded and the control did not run.
        if (!await IsAvailableAsync(cancellationToken).ConfigureAwait(false))
        {
            return MembershipWriteOutcome.StoreUnavailable;
        }

        const string UpdateSql = @"
UPDATE am
SET am.[FailedPasswordAttemptCount] =
        CASE WHEN @now > DATEADD(minute, @window, am.[FailedPasswordAttemptWindowStart])
             THEN 1 ELSE am.[FailedPasswordAttemptCount] + 1 END,
    am.[FailedPasswordAttemptWindowStart] = @now,
    am.[IsLockedOut] =
        CASE WHEN (CASE WHEN @now > DATEADD(minute, @window, am.[FailedPasswordAttemptWindowStart])
                        THEN 1 ELSE am.[FailedPasswordAttemptCount] + 1 END) >= @threshold
             THEN 1 ELSE am.[IsLockedOut] END,
    am.[LastLockoutDate] =
        CASE WHEN (CASE WHEN @now > DATEADD(minute, @window, am.[FailedPasswordAttemptWindowStart])
                        THEN 1 ELSE am.[FailedPasswordAttemptCount] + 1 END) >= @threshold
             THEN @now ELSE am.[LastLockoutDate] END
OUTPUT CASE WHEN inserted.[IsLockedOut] = 1 THEN 1 ELSE 0 END
FROM [dbo].[aspnet_Membership] am
INNER JOIN [dbo].[aspnet_Users] au ON au.[UserId] = am.[UserId]
INNER JOIN [dbo].[aspnet_Applications] aa ON aa.[ApplicationId] = au.[ApplicationId]
WHERE aa.[LoweredApplicationName] = @app AND au.[LoweredUserName] = @user AND am.[IsLockedOut] = 0;";

        List<KeyValuePair<string, object?>> parameters = Parameters(userName);
        parameters.Add(new KeyValuePair<string, object?>("@now", utcNow));
        parameters.Add(new KeyValuePair<string, object?>("@window", (int)Math.Max(attemptWindow.TotalMinutes, 0d)));
        parameters.Add(new KeyValuePair<string, object?>("@threshold", lockoutThreshold));

        object? outcome = await ExecuteAsync(
                UpdateSql,
                parameters,
                static (command, token) => command.ExecuteScalarAsync(token),
                cancellationToken)
            .ConfigureAwait(false);

        if (outcome is not null)
        {
            return Convert.ToInt32(outcome, System.Globalization.CultureInfo.InvariantCulture) == 1
                ? MembershipWriteOutcome.RecordedAndLocked
                : MembershipWriteOutcome.Recorded;
        }

        // Nothing was updated, which means either the account does not exist or it was already locked.
        const string StateSql = @"
SELECT CASE WHEN am.[IsLockedOut] = 1 THEN 1 ELSE 0 END
FROM [dbo].[aspnet_Membership] am
INNER JOIN [dbo].[aspnet_Users] au ON au.[UserId] = am.[UserId]
INNER JOIN [dbo].[aspnet_Applications] aa ON aa.[ApplicationId] = au.[ApplicationId]
WHERE aa.[LoweredApplicationName] = @app AND au.[LoweredUserName] = @user;";

        object? existing = await ExecuteAsync(
                StateSql,
                Parameters(userName),
                static (command, token) => command.ExecuteScalarAsync(token),
                cancellationToken)
            .ConfigureAwait(false);

        if (existing is null)
        {
            return MembershipWriteOutcome.NoRecord;
        }

        return Convert.ToInt32(existing, System.Globalization.CultureInfo.InvariantCulture) == 1
            ? MembershipWriteOutcome.RecordedAndLocked
            : MembershipWriteOutcome.Recorded;
    }

    /// <summary>Sets whether an account is approved for use.</summary>
    /// <param name="userName">The DotNetNuke user name.</param>
    /// <param name="isApproved">The approval state to store.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns><see langword="true"/> when a credential record was updated.</returns>
    public async Task<bool> SetApprovalAsync(string userName, bool isApproved, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(userName);

        if (!await IsAvailableAsync(cancellationToken).ConfigureAwait(false))
        {
            return false;
        }

        const string Sql = @"
UPDATE am
SET am.[IsApproved] = @approved
FROM [dbo].[aspnet_Membership] am
INNER JOIN [dbo].[aspnet_Users] au ON au.[UserId] = am.[UserId]
INNER JOIN [dbo].[aspnet_Applications] aa ON aa.[ApplicationId] = au.[ApplicationId]
WHERE aa.[LoweredApplicationName] = @app AND au.[LoweredUserName] = @user;";

        List<KeyValuePair<string, object?>> parameters = Parameters(userName);
        parameters.Add(new KeyValuePair<string, object?>("@approved", isApproved));

        return await AffectedAsync(Sql, parameters, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Clears a lock-out and resets the failure counters.</summary>
    /// <param name="userName">The DotNetNuke user name.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns><see langword="true"/> when a credential record was updated.</returns>
    /// <remarks>
    /// The counters and their windows are reset with the lock-out, because leaving an exhausted counter
    /// behind would re-lock the account on its very next failure and make the unlock look ineffective.
    /// </remarks>
    public async Task<bool> UnlockAsync(string userName, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(userName);

        if (!await IsAvailableAsync(cancellationToken).ConfigureAwait(false))
        {
            return false;
        }

        const string Sql = @"
UPDATE am
SET am.[IsLockedOut] = 0,
    am.[LastLockoutDate] = @never,
    am.[FailedPasswordAttemptCount] = 0,
    am.[FailedPasswordAttemptWindowStart] = @never,
    am.[FailedPasswordAnswerAttemptCount] = 0,
    am.[FailedPasswordAnswerAttemptWindowStart] = @never
FROM [dbo].[aspnet_Membership] am
INNER JOIN [dbo].[aspnet_Users] au ON au.[UserId] = am.[UserId]
INNER JOIN [dbo].[aspnet_Applications] aa ON aa.[ApplicationId] = au.[ApplicationId]
WHERE aa.[LoweredApplicationName] = @app AND au.[LoweredUserName] = @user;";

        List<KeyValuePair<string, object?>> parameters = Parameters(userName);
        parameters.Add(new KeyValuePair<string, object?>("@never", NeverRecorded));

        return await AffectedAsync(Sql, parameters, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Deletes the credential record of an account.</summary>
    /// <param name="userName">The DotNetNuke user name.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns><see langword="true"/> when a credential record was deleted.</returns>
    /// <remarks>
    /// REPRODUCES THE STOCK DELETION ORDER, AND THE ORDER IS THE WHOLE POINT. The membership user row is
    /// referenced by four other tables through NON-CASCADING foreign keys, so removing it first - or
    /// removing only it and the credential row - is refused by the store for any account that has ever held
    /// a membership role, stored a profile or personalised a page. <c>aspnet_Users_DeleteUser</c>
    /// (<c>Website/Providers/DataProviders/SqlDataProvider/InstallCommon.sql</c> lines 421-541, ALTERed at
    /// <c>04.00.00.SqlDataProvider</c> lines 475-595) clears the dependants first and in a fixed sequence:
    /// the credential row, then the role memberships, then the profile, then the personalisation, and only
    /// then the user row.
    /// </remarks>
    public async Task<bool> DeleteAsync(string userName, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(userName);

        if (!await IsAvailableAsync(cancellationToken).ConfigureAwait(false))
        {
            return false;
        }

        const string Sql = @"
DECLARE @membershipUserId uniqueidentifier;
DECLARE @deleted int = 0;
DECLARE @opened bit = 0;

SELECT @membershipUserId = au.[UserId]
FROM [dbo].[aspnet_Users] au
INNER JOIN [dbo].[aspnet_Applications] aa ON aa.[ApplicationId] = au.[ApplicationId]
WHERE aa.[LoweredApplicationName] = @app AND au.[LoweredUserName] = @user;

IF @membershipUserId IS NOT NULL
BEGIN
    IF @@TRANCOUNT = 0
    BEGIN
        BEGIN TRANSACTION;
        SET @opened = 1;
    END

    BEGIN TRY
        DELETE FROM [dbo].[aspnet_Membership] WHERE [UserId] = @membershipUserId;
        SET @deleted = @@ROWCOUNT;

        IF OBJECT_ID(N'[dbo].[aspnet_UsersInRoles]', N'U') IS NOT NULL
            DELETE FROM [dbo].[aspnet_UsersInRoles] WHERE [UserId] = @membershipUserId;

        IF OBJECT_ID(N'[dbo].[aspnet_Profile]', N'U') IS NOT NULL
            DELETE FROM [dbo].[aspnet_Profile] WHERE [UserId] = @membershipUserId;

        IF OBJECT_ID(N'[dbo].[aspnet_PersonalizationPerUser]', N'U') IS NOT NULL
            DELETE FROM [dbo].[aspnet_PersonalizationPerUser] WHERE [UserId] = @membershipUserId;

        DELETE FROM [dbo].[aspnet_Users] WHERE [UserId] = @membershipUserId;

        IF @opened = 1
            COMMIT TRANSACTION;
    END TRY
    BEGIN CATCH
        IF @opened = 1 AND XACT_STATE() <> 0
            ROLLBACK TRANSACTION;

        THROW;
    END CATCH
END

SELECT @deleted;";

        object? deleted = await ExecuteAsync(
                Sql,
                Parameters(userName),
                static (command, token) => command.ExecuteScalarAsync(token),
                cancellationToken)
            .ConfigureAwait(false);

        return deleted is not null
            && Convert.ToInt32(deleted, System.Globalization.CultureInfo.InvariantCulture) > 0;
    }

    /// <summary>Reads one batch of account snapshots into the accumulating map.</summary>
    /// <param name="batch">The user names in this batch.</param>
    /// <param name="snapshots">The map to populate.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task that completes when the batch has been read.</returns>
    private async Task ReadAccountBatchAsync(
        List<string> batch,
        Dictionary<string, MembershipAccountSnapshot> snapshots,
        CancellationToken cancellationToken)
    {
        List<KeyValuePair<string, object?>> parameters = new(batch.Count + 1)
        {
            new KeyValuePair<string, object?>("@app", MembershipApplicationName.ToLowerInvariant()),
        };

        string[] placeholders = new string[batch.Count];

        for (int index = 0; index < batch.Count; index++)
        {
            string name = "@user" + index.ToString(System.Globalization.CultureInfo.InvariantCulture);
            placeholders[index] = name;
            parameters.Add(new KeyValuePair<string, object?>(name, batch[index].Trim().ToLowerInvariant()));
        }

        string sql = @"
SELECT au.[LoweredUserName], am.[IsApproved], am.[IsLockedOut], am.[CreateDate], am.[LastLoginDate],
       au.[LastActivityDate], am.[LastLockoutDate], am.[LastPasswordChangedDate]
FROM [dbo].[aspnet_Users] au
INNER JOIN [dbo].[aspnet_Applications] aa ON aa.[ApplicationId] = au.[ApplicationId]
INNER JOIN [dbo].[aspnet_Membership] am ON am.[UserId] = au.[UserId]
WHERE aa.[LoweredApplicationName] = @app AND au.[LoweredUserName] IN ("
            + string.Join(", ", placeholders)
            + ");";

        Dictionary<string, MembershipAccountSnapshot> read = await ExecuteAsync(
                sql,
                parameters,
                static async (command, token) =>
                {
                    Dictionary<string, MembershipAccountSnapshot> rows = new(StringComparer.OrdinalIgnoreCase);

                    using DbDataReader reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);

                    while (await reader.ReadAsync(token).ConfigureAwait(false))
                    {
                        rows[reader.GetString(0)] = new MembershipAccountSnapshot(
                            reader.GetBoolean(1),
                            reader.GetBoolean(2),
                            Moment(reader, 3),
                            Moment(reader, 4),
                            Moment(reader, 5),
                            Moment(reader, 6),
                            Moment(reader, 7));
                    }

                    return rows;
                },
                cancellationToken)
            .ConfigureAwait(false);

        foreach (KeyValuePair<string, MembershipAccountSnapshot> row in read)
        {
            snapshots[row.Key] = row.Value;
        }
    }

    /// <summary>Executes a statement and reports whether it changed anything.</summary>
    /// <param name="sql">The statement text.</param>
    /// <param name="parameters">The bound parameters.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns><see langword="true"/> when at least one row was affected.</returns>
    private async Task<bool> AffectedAsync(
        string sql,
        IReadOnlyList<KeyValuePair<string, object?>> parameters,
        CancellationToken cancellationToken)
    {
        int affected = await ExecuteAsync(
                sql,
                parameters,
                static (command, token) => command.ExecuteNonQueryAsync(token),
                cancellationToken)
            .ConfigureAwait(false);

        return affected > 0;
    }

    /// <summary>Runs one command against the context's own connection.</summary>
    /// <typeparam name="TResult">The result the command produces.</typeparam>
    /// <param name="sql">The statement text.</param>
    /// <param name="parameters">The bound parameters.</param>
    /// <param name="action">The operation to perform against the prepared command.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The operation's result.</returns>
    /// <remarks>
    /// The context's connection is borrowed rather than a new one opened, so these statements observe the
    /// same session - and, critically, enlist in the same transaction - as everything the unit of work has
    /// staged. The connection is closed again only when this call is what opened it, so a caller that had
    /// deliberately kept it open is left undisturbed.
    /// </remarks>
    private async Task<TResult> ExecuteAsync<TResult>(
        string sql,
        IReadOnlyList<KeyValuePair<string, object?>> parameters,
        Func<DbCommand, CancellationToken, Task<TResult>> action,
        CancellationToken cancellationToken)
    {
        DbConnection connection = _context.Database.GetDbConnection();
        bool openedHere = connection.State != ConnectionState.Open;

        if (openedHere)
        {
            await _context.Database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        }

        try
        {
            using DbCommand command = connection.CreateCommand();
            command.CommandText = sql;

            DbTransaction? ambient = _context.Database.CurrentTransaction?.GetDbTransaction();

            if (ambient is not null)
            {
                command.Transaction = ambient;
            }

            foreach (KeyValuePair<string, object?> parameter in parameters)
            {
                DbParameter bound = command.CreateParameter();
                bound.ParameterName = parameter.Key;
                bound.Value = parameter.Value ?? DBNull.Value;
                command.Parameters.Add(bound);
            }

            return await action(command, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (openedHere)
            {
                await _context.Database.CloseConnectionAsync().ConfigureAwait(false);
            }
        }
    }

    /// <summary>Builds the application and user-name parameters every statement needs.</summary>
    /// <param name="userName">The DotNetNuke user name.</param>
    /// <returns>A mutable parameter list seeded with the two common values.</returns>
    private static List<KeyValuePair<string, object?>> Parameters(string userName)
    {
        return new List<KeyValuePair<string, object?>>(8)
        {
            new("@app", MembershipApplicationName.ToLowerInvariant()),
            new("@user", userName.Trim().ToLowerInvariant()),
        };
    }

    /// <summary>Reduces the caller's user names to a distinct, case-insensitive list.</summary>
    /// <param name="userNames">The names to reduce.</param>
    /// <returns>The distinct names, with blanks discarded.</returns>
    private static List<string> Distinct(IReadOnlyCollection<string> userNames)
    {
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
        List<string> distinct = new(userNames.Count);

        foreach (string name in userNames)
        {
            if (!string.IsNullOrWhiteSpace(name) && seen.Add(name.Trim()))
            {
                distinct.Add(name.Trim());
            }
        }

        return distinct;
    }

    /// <summary>Translates a stored instant, honouring the schema's "never" sentinel.</summary>
    /// <param name="reader">The reader positioned on the current row.</param>
    /// <param name="ordinal">The column to read.</param>
    /// <returns>The instant, or <see langword="null"/> when it is null or the sentinel.</returns>
    private static DateTime? Moment(DbDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal))
        {
            return null;
        }

        DateTime value = reader.GetDateTime(ordinal);
        return value <= NeverRecorded ? null : value;
    }
}
