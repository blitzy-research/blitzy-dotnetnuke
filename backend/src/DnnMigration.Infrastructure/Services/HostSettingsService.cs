using System.Collections.ObjectModel;
using System.Data;
using System.Data.Common;
using System.Globalization;
using DnnMigration.Domain.Abstractions.Services;
using DnnMigration.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace DnnMigration.Infrastructure.Services;

/// <summary>
/// Reads the installation-wide configuration rows the legacy platform persisted in the <c>HostSettings</c>
/// table. This is the single implementation of <see cref="IHostSettingsService"/>.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Terminal schema, honoured and never altered.</strong> The table is created at
/// <c>Website/Providers/DataProviders/SqlDataProvider/01.00.05.SqlDataProvider</c> with <c>SettingName
/// nvarchar(50) NOT NULL</c> and <c>SettingValue nvarchar(256) NOT NULL</c>; <c>02.00.01</c> makes the name
/// the clustered primary key, and <c>03.00.12</c> adds <c>SettingIsSecure bit NOT NULL</c> defaulting to
/// zero, which <c>03.01.01</c> re-asserts.
/// </para>
/// <para>
/// <strong>Cancellation.</strong> Every member is awaitable and every token reaches the operation that can
/// honour it - opening, executing and reading. Cancellation is never caught, wrapped or converted anywhere
/// below; it leaves as it arrived.
/// </para>
/// </remarks>
internal sealed class HostSettingsService : IHostSettingsService
{
    /// <summary>Longest name the <c>SettingName</c> column accepts.</summary>
    private const int SettingNameMaxLength = 50;

    /// <summary>Bound parameter carrying the setting name.</summary>
    private const string SettingNameParameter = "@SettingName";

    /// <summary>
    /// Name of the row the whole-table read creates when it is missing, preserved verbatim from the
    /// measured procedure body.
    /// </summary>
    private const string InstallationIdentifierSettingName = "GUID";

    /// <summary>Ordinal of the name column in the whole-table projection.</summary>
    private const int SettingNameOrdinal = 0;

    /// <summary>Ordinal of the value column in the whole-table projection.</summary>
    private const int SettingValueOrdinal = 1;

    /// <summary>
    /// Keyed read, reproducing the measured body of the legacy single-setting procedure
    /// (<c>02.00.00.SqlDataProvider</c>).
    /// </summary>
    private const string SelectSettingCommandText =
        "SELECT TOP (1) [SettingValue] FROM [dbo].[HostSettings] WHERE [SettingName] = @SettingName;";

    /// <summary>
    /// Whole-table projection. Only the name and the value are read: the secure flag is persistence
    /// metadata and has no place in the projection this service hands back.
    /// </summary>
    private const string SelectAllSettingsCommandText =
        "SELECT [SettingName], [SettingValue] FROM [dbo].[HostSettings];";

    /// <summary>
    /// The write-on-read half of the measured whole-table procedure (<c>04.05.00.SqlDataProvider</c>
    /// L898-L908), expressed as one atomic statement.
    /// </summary>
    private const string InsertInstallationIdentifierCommandText =
        "INSERT INTO [dbo].[HostSettings] ([SettingName], [SettingValue], [SettingIsSecure]) " +
        "SELECT @SettingName, CONVERT(nvarchar(256), NEWID()), 0 " +
        "WHERE NOT EXISTS (SELECT 1 FROM [dbo].[HostSettings] WITH (UPDLOCK, HOLDLOCK) " +
        "WHERE [SettingName] = @SettingName);";

    private readonly DnnDbContext _dbContext;

    /// <summary>Initialises a new instance of the <see cref="HostSettingsService"/> class.</summary>
    /// <param name="dbContext">
    /// The scoped persistence context, taken solely to borrow the relational connection it already owns.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="dbContext"/> is <see langword="null"/>.</exception>
    public HostSettingsService(DnnDbContext dbContext)
    {
        _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
    }

    /// <inheritdoc />
    /// <exception cref="ArgumentNullException"><paramref name="settingName"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="settingName"/> is blank, or longer than the column accepts.
    /// </exception>
    /// <remarks>
    /// Absence and emptiness are separated here for the first time. An empty result set means no row
    /// carries the name, which is reported as <see langword="null"/>; a row whose value is empty is
    /// reported as the empty string.
    /// </remarks>
    public async Task<string?> GetSettingAsync(string settingName, CancellationToken cancellationToken = default)
    {
        string name = ValidateSettingName(settingName);

        return await WithBorrowedConnectionAsync(
                async (connection, token) =>
                {
                    await using DbCommand command = CreateCommand(connection, SelectSettingCommandText);
                    AddTextParameter(command, SettingNameParameter, name, SettingNameMaxLength);

                    object? scalar = await command.ExecuteScalarAsync(token).ConfigureAwait(false);

                    return scalar switch
                    {
                        null => null,
                        DBNull => string.Empty,
                        string text => text,
                        _ => Convert.ToString(scalar, CultureInfo.InvariantCulture) ?? string.Empty,
                    };
                },
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Two statements, in the order the measured procedure body used them: create the
    /// installation-identifier row if it is missing, then project every row.
    /// </remarks>
    public async Task<IReadOnlyDictionary<string, string>> GetSettingsAsync(CancellationToken cancellationToken = default)
    {
        return await WithBorrowedConnectionAsync(
                async (connection, token) =>
                {
                    await using (DbCommand seed = CreateCommand(connection, InsertInstallationIdentifierCommandText))
                    {
                        AddTextParameter(
                            seed,
                            SettingNameParameter,
                            InstallationIdentifierSettingName,
                            SettingNameMaxLength);

                        await seed.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                    }

                    Dictionary<string, string> settings = new(StringComparer.Ordinal);

                    await using (DbCommand command = CreateCommand(connection, SelectAllSettingsCommandText))
                    {
                        // Scoped so that the reader is disposed - and the connection therefore
                        // freed for the next command - before the projection is handed back.
                        await using DbDataReader reader = await command
                            .ExecuteReaderAsync(token)
                            .ConfigureAwait(false);

                        while (await reader.ReadAsync(token).ConfigureAwait(false))
                        {
                            string name = await reader
                                .GetFieldValueAsync<string>(SettingNameOrdinal, token)
                                .ConfigureAwait(false);

                            // The column is NOT NULL, so this test is boundary defence rather than ordinary
                            // data handling. It answers with the empty string, which is precisely what the
                            // legacy accumulation did for a null column.
                            bool valueIsNull = await reader
                                .IsDBNullAsync(SettingValueOrdinal, token)
                                .ConfigureAwait(false);

                            string value = valueIsNull
                                ? string.Empty
                                : await reader
                                    .GetFieldValueAsync<string>(SettingValueOrdinal, token)
                                    .ConfigureAwait(false);

                            // Assigned rather than added: the name is the primary key, so a duplicate
                            // cannot occur, and assignment means a database that somehow held one could not
                            // turn a read into an exception.
                            settings[name] = value;
                        }
                    }

                    // Genuinely read-only, not a mutable map widened to a read-only interface: a
                    // caller cannot reach the underlying store by casting the result back.
                    return (IReadOnlyDictionary<string, string>)new ReadOnlyDictionary<string, string>(settings);
                },
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Lends the context's own connection to <paramref name="operation"/>, opening it only if it was not
    /// already open and closing it again only if this call is what opened it.
    /// </summary>
    /// <typeparam name="TResult">What the operation produces.</typeparam>
    /// <param name="operation">The work to run against the connection.</param>
    /// <param name="cancellationToken">Observed while opening and passed on to the operation.</param>
    /// <returns>Whatever the operation produced.</returns>
    private async Task<TResult> WithBorrowedConnectionAsync<TResult>(
        Func<DbConnection, CancellationToken, Task<TResult>> operation,
        CancellationToken cancellationToken)
    {
        DbConnection connection = _dbContext.Database.GetDbConnection();
        bool openedHere = connection.State != ConnectionState.Open;

        if (openedHere)
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        }

        try
        {
            return await operation(connection, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (openedHere)
            {
                await connection.CloseAsync().ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Prepares a text command on the borrowed connection, enlisted in whatever transaction the context
    /// currently has open.
    /// </summary>
    /// <param name="connection">The borrowed connection.</param>
    /// <param name="commandText">One of this class's constant statements.</param>
    /// <returns>A command ready for its parameters.</returns>
    private DbCommand CreateCommand(DbConnection connection, string commandText)
    {
        DbCommand command = connection.CreateCommand();

        command.CommandText = commandText;
        command.CommandType = CommandType.Text;

        // Enlisting keeps the write-on-read GUID seed inside any surrounding unit of work instead of
        // escaping it, and the transaction is read afresh for every command so one begun after this service
        // was constructed is still picked up.
        DbTransaction? active = _dbContext.Database.CurrentTransaction?.GetDbTransaction();

        if (active is not null)
        {
            command.Transaction = active;
        }

        return command;
    }

    /// <summary>Binds a value as a sized text parameter matching the column it targets.</summary>
    /// <param name="command">The command being prepared.</param>
    /// <param name="parameterName">The parameter name declared in the statement.</param>
    /// <param name="value">The value to bind.</param>
    /// <param name="size">The column's declared length, so that one query plan serves every call.</param>
    private static void AddTextParameter(DbCommand command, string parameterName, string value, int size)
    {
        DbParameter parameter = command.CreateParameter();

        parameter.ParameterName = parameterName;
        parameter.DbType = DbType.String;
        parameter.Size = size;
        parameter.Value = value;

        command.Parameters.Add(parameter);
    }

    /// <summary>Checks a setting name against the column that has to hold it.</summary>
    /// <param name="settingName">The name as supplied.</param>
    /// <returns>The same name, once it is known to fit.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="settingName"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">The name is blank or too long.</exception>
    /// <remarks>
    /// A blank name is refused rather than looked up, because the column is the primary key and a blank
    /// primary key identifies nothing a caller could have meant. An over-long name is refused for the same
    /// reason: no row can carry it, so accepting one would either truncate the caller's intent or surface
    /// as a provider error further down.
    /// </remarks>
    private static string ValidateSettingName(string settingName)
    {
        ArgumentNullException.ThrowIfNull(settingName);

        if (string.IsNullOrWhiteSpace(settingName))
        {
            throw new ArgumentException("A host setting name is required.", nameof(settingName));
        }

        if (settingName.Length > SettingNameMaxLength)
        {
            throw new ArgumentException(
                FormattableString.Invariant(
                    $"A host setting name may be at most {SettingNameMaxLength} characters; this one is {settingName.Length}."),
                nameof(settingName));
        }

        return settingName;
    }
}
