using System.Data;
using System.Data.Common;
using DnnMigration.Domain.Abstractions.Services;
using DnnMigration.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace DnnMigration.Infrastructure.Services;

/// <summary>
/// Reads and writes the installation-wide <c>dbo.HostSettings</c> rows.
/// </summary>
/// <remarks>
/// <para>
/// MIGRATION: this is the narrow replacement for the <c>HostSettings</c>-table portion of two excluded
/// legacy subsystems - the 2,704-line <c>Library/Components/Shared/Globals.vb</c> module that 111 files
/// referenced, and the three-file <c>DotNetNuke.Entities.Host</c> namespace that 53 files referenced.
/// Measured coupling from the code actually being migrated is six distinct members across fourteen call
/// sites, and only one of those six touches this table. Nothing else from either module is rebuilt here.
/// </para>
/// <para>
/// <strong>Why explicit statements rather than a mapped entity.</strong> <c>HostSettings</c> is not one
/// of the twenty-one entities the model owns, and adding a twenty-second would contradict the fixed
/// entity inventory. It does not belong in that inventory either: these rows are installation-wide
/// name-and-value configuration, not part of any aggregate, and nothing navigates to them from a mapped
/// entity. So this service reaches the table the same way <c>MembershipStore</c> reaches the external
/// credential store - through parameterised statements on the context's own connection, enlisting in the
/// context's transaction when one is open. Every value crosses as a bound parameter; nothing is
/// concatenated into SQL.
/// </para>
/// <para>
/// <strong>Terminal schema.</strong> The table is created at
/// <c>Website/Providers/DataProviders/SqlDataProvider/01.00.05.SqlDataProvider</c> line 2259 with
/// <c>SettingName nvarchar(50) NOT NULL</c> and <c>SettingValue nvarchar(256) NOT NULL</c>, uniquely
/// indexed on the name by <c>IX_HostSettings</c>. A later script adds
/// <c>SettingIsSecure bit NOT NULL</c> defaulting to 0. The unique index is what makes the upsert below
/// safe: it is the constraint that guarantees a name identifies at most one row.
/// </para>
/// <para>
/// <strong>Absent means null.</strong> A name with no row yields <see langword="null"/>, so an empty
/// string now means a row exists and holds an empty value. The legacy reader could not express that
/// distinction: it answered a missing key with the empty-string sentinel and mapped a database NULL to
/// the empty string as well, leaving an absent row, a NULL column and a genuinely empty value
/// indistinguishable to every caller.
/// </para>
/// <para>
/// <strong>The secure flag is stored, not enforced here.</strong> This service records it and does not
/// filter on it. Withholding sensitive values is an authorisation decision belonging to whichever
/// caller publishes them, and the only legacy screens that made that decision are the excluded
/// host-level administration pages. The legacy secure-settings reader is deliberately not reproduced:
/// despite its name it returned only the NON-secure rows and additionally dropped any name containing
/// "password". That naming defect is recorded rather than corrected, per the minimal-change directive.
/// </para>
/// </remarks>
internal sealed class HostSettingsService : IHostSettingsService
{
    private readonly DnnDbContext _context;
    private readonly ICacheService _cache;

    /// <summary>Initialises a new instance of the <see cref="HostSettingsService"/> class.</summary>
    /// <param name="context">The unit-of-work scoped database context, whose connection is borrowed.</param>
    /// <param name="cache">
    /// The cache, so that a write discards the host-wide stored copy. The legacy upsert finished with
    /// exactly that step, at <c>Library/Components/Host/HostSettingsController.vb</c> line 57, and it is
    /// performed here because it is a storage concern that the contract deliberately does not express.
    /// </param>
    /// <exception cref="ArgumentNullException">Either argument is <see langword="null"/>.</exception>
    public HostSettingsService(DnnDbContext context, ICacheService cache)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
    }

    /// <inheritdoc />
    public async Task<string?> GetSettingAsync(string settingName, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settingName);

        const string sql = "SELECT TOP (1) [SettingValue] FROM [dbo].[HostSettings] WHERE [SettingName] = @name;";

        return await ExecuteAsync(
                sql,
                new[] { new KeyValuePair<string, object?>("@name", settingName) },
                static async (command, token) =>
                {
                    object? value = await command.ExecuteScalarAsync(token).ConfigureAwait(false);

                    // DBNull and a missing row are different facts about the store, but the contract
                    // makes them one answer: there is no value to hand back. The column is declared NOT
                    // NULL, so only the missing-row case arises in practice; guarding both costs nothing
                    // and means a hand-edited row cannot produce a cast failure.
                    return value is null or DBNull ? null : Convert.ToString(value, Culture);
                },
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<string, string>> GetSettingsAsync(CancellationToken cancellationToken = default)
    {
        const string sql = "SELECT [SettingName], [SettingValue] FROM [dbo].[HostSettings];";

        return await ExecuteAsync(
                sql,
                Array.Empty<KeyValuePair<string, object?>>(),
                static async (command, token) =>
                {
                    // Ordinal-ignore-case, because the unique index is on the name under a
                    // case-insensitive collation: two rows differing only in casing cannot exist, so a
                    // case-sensitive projection would let a caller miss a row it asked for by name.
                    Dictionary<string, string> settings = new(StringComparer.OrdinalIgnoreCase);

                    using DbDataReader reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);

                    while (await reader.ReadAsync(token).ConfigureAwait(false))
                    {
                        string name = reader.GetString(0);
                        settings[name] = reader.IsDBNull(1) ? string.Empty : reader.GetString(1);
                    }

                    return (IReadOnlyDictionary<string, string>)settings;
                },
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    /// <remarks>
    /// One statement, not a read followed by a branch. The legacy controller read the row first and then
    /// chose between two distinct write primitives, which leaves a window in which a concurrent writer
    /// inserts the same name between the read and the insert - and <c>IX_HostSettings</c> then turns that
    /// race into a unique-constraint violation. Updating first and inserting only when nothing was
    /// updated closes the window to a single round trip, and the unique index remains the guarantee that
    /// makes the insert branch unambiguous.
    /// <para>
    /// An empty value is stored as an empty value; it is never read as a request to remove the row. The
    /// host-wide cached copy is discarded afterwards, so a subsequent read observes what was just
    /// written rather than the value it replaced.
    /// </para>
    /// </remarks>
    public async Task UpsertSettingAsync(
        string settingName,
        string settingValue,
        bool isSecure = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settingName);
        ArgumentNullException.ThrowIfNull(settingValue);

        const string sql = @"
UPDATE [dbo].[HostSettings]
SET [SettingValue] = @value, [SettingIsSecure] = @secure
WHERE [SettingName] = @name;

IF @@ROWCOUNT = 0
    INSERT INTO [dbo].[HostSettings] ([SettingName], [SettingValue], [SettingIsSecure])
    VALUES (@name, @value, @secure);";

        KeyValuePair<string, object?>[] parameters =
        [
            new KeyValuePair<string, object?>("@name", settingName),
            new KeyValuePair<string, object?>("@value", settingValue),
            new KeyValuePair<string, object?>("@secure", isSecure),
        ];

        await ExecuteAsync(
                sql,
                parameters,
                static async (command, token) =>
                {
                    await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                    return true;
                },
                cancellationToken)
            .ConfigureAwait(false);

        _cache.InvalidateHost();
    }

    /// <summary>Runs one statement on the context's connection and projects its result.</summary>
    /// <typeparam name="TResult">What the projection produces.</typeparam>
    /// <param name="sql">The statement, carrying only parameter placeholders and no interpolated values.</param>
    /// <param name="parameters">The values to bind.</param>
    /// <param name="action">The projection to run against the prepared command.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Whatever the projection produced.</returns>
    /// <remarks>
    /// The connection is borrowed rather than created, and any transaction the context already has open
    /// is enlisted, so a write here commits or rolls back with the surrounding unit of work instead of
    /// escaping it. The connection is closed only when this call is what opened it: closing a connection
    /// somebody else opened would break the operation that owns it.
    /// </remarks>
    private async Task<TResult> ExecuteAsync<TResult>(
        string sql,
        IReadOnlyCollection<KeyValuePair<string, object?>> parameters,
        Func<DbCommand, CancellationToken, Task<TResult>> action,
        CancellationToken cancellationToken)
    {
        DbConnection connection = _context.Database.GetDbConnection();
        bool openedHere = connection.State != ConnectionState.Open;

        if (openedHere)
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        }

        try
        {
            using DbCommand command = connection.CreateCommand();

            command.CommandText = sql;
            command.CommandType = CommandType.Text;

            IDbContextTransaction? transaction = _context.Database.CurrentTransaction;

            if (transaction is not null)
            {
                command.Transaction = transaction.GetDbTransaction();
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
                await connection.CloseAsync().ConfigureAwait(false);
            }
        }
    }

    /// <summary>The culture used for the one value conversion this service performs.</summary>
    /// <remarks>
    /// Invariant, because a setting value is data moving between a store and a caller and must not
    /// acquire the formatting of whatever culture the current request happens to carry.
    /// </remarks>
    private static System.Globalization.CultureInfo Culture => System.Globalization.CultureInfo.InvariantCulture;
}
