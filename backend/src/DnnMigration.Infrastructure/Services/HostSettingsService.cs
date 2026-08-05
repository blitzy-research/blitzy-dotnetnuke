using System.Collections.ObjectModel;
using System.Data;
using System.Data.Common;
using System.Globalization;
using DnnMigration.Domain.Abstractions.Services;
using DnnMigration.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace DnnMigration.Infrastructure.Services;

// MIGRATION: 1 - SCOPE. Only the installation-settings TABLE portion of the six legacy members
// that migrated code actually reached is implemented here. The 2,704-line legacy globals module
// that 111 files referenced, and the three-file host-entities namespace that 53 files referenced,
// are NOT ported. The other five replacements each have their own named home elsewhere: the
// runtime performance multiplier is a bound options class in the Application layer, the
// application virtual path and the two physical-path members are host-environment and
// link-generation concerns in the API layer, and the unauthenticated-user role name is a domain
// constant. Not one of them acquires a member below.
//
// MIGRATION: 2 - NARROW RULE-T3 EXCEPTION. Rule T3 routes data access through a repository
// abstraction, and this is the one documented exception in the layer. The reason is structural
// rather than convenient: these rows are not one of the twenty-one mapped entities, so there is
// no entity type, no entity configuration, no mapped entity collection on the context and no
// repository interface for them - and none of those may be invented, because the entity inventory
// and the configuration count are both fixed. This service therefore takes the persistence
// context for exactly one purpose, to borrow the relational connection the context already owns,
// and the exception is contained rather than concealed: every relational type stays private, so
// no context, connection, command, reader or query object escapes through any public member.
//
// MIGRATION: 3 - REFLECTION SINGLETON AND PRE-GENERICS SHAPES REMOVED. The legacy path reached
// storage through a reflection-instantiated provider singleton
// (Library/Components/Providers/Data/DataProvider.vb L31-L50) whose four host members
// (L87-L90) answered with a forward-only reader, and the caller above them accumulated those rows
// into an untyped, mutable, pre-generics collection
// (Library/Components/Host/HostSettings.vb L28-L51). All three shapes are gone: the provider
// becomes constructor injection, the reader is consumed and closed inside this class, and the
// collection becomes a materialised, typed, genuinely read-only projection.
//
// MIGRATION: 4 - MISSING AND NULL SEMANTICS. A name with no row now yields null, so an empty
// string means a row exists and holds an empty value. The legacy reader could express neither
// distinction: it answered a missing key with the empty-string sentinel
// (Library/Components/Host/HostSettings.vb L20-L26) and mapped a database null to the empty
// string as well (L37-L41), leaving an absent row, a null column and a genuinely empty value
// indistinguishable. The target keeps the distinction at the read boundary: null means no row and
// an empty string means a row exists with an empty value.
//
// MIGRATION: 5 - READ CACHING INTENTIONALLY OMITTED. The legacy whole-table read cached its
// result under the key "GetHostSettings" with NO expiry argument at all
// (Library/Components/Host/HostSettings.vb L43). The target cache contract requires every cached
// read to state an explicit expiry span, and no measured host-settings expiry exists anywhere in the
// legacy source to supply. Inventing one - twenty minutes, sixty minutes, an infinite span or a
// private never-expiring path - would be a speculative behaviour change dressed as fidelity, and
// bypassing the abstraction to reach the underlying cache directly would be worse. So the read
// paths below deliberately do not cache, and this note is the record that AAP 0.7.5.2 requires
// for an omitted read path rather than allowing it to disappear silently.
//
// MIGRATION: 6 - WRITE-ON-READ SIDE EFFECT RETAINED. The terminal whole-table read is not a pure
// read. Its measured body
// (Website/Providers/DataProviders/SqlDataProvider/04.05.00.SqlDataProvider L898-L908) creates
// the installation's "GUID" row when it is absent and only then projects every row, so an
// installation acquires its identifier as a side effect of the first read. That side effect is
// reproduced rather than quietly dropped, which is why the whole-table read below writes.
//
// MIGRATION: 7 - SECURE FLAG NOT INTERPRETED. Nothing here filters on SettingIsSecure:
// withholding a sensitive value is an authorisation decision belonging to whoever publishes it,
// and the only legacy screens that made that decision are the excluded host-administration pages.
// The legacy secure-settings reader is deliberately not reproduced either - despite its name it
// returned only the NON-secure rows and additionally dropped any name containing "password", and
// under the minimal-change directive that naming defect is recorded rather than repaired.
//
// MIGRATION: 8 - DIVERGENCE, STORED PROCEDURES BECOME PARAMETERISED STATEMENTS. The legacy
// read members reached two stored procedures by concatenating a database owner and an object
// qualifier onto a procedure name and handing the result to a helper whose only artefact in the
// repository is a compiled assembly with no source
// (Library/Providers/DataProviders/SqlDataProvider/SqlDataProvider.vb, host region). AAP 0.1.2.1
// names the elimination of those concatenated procedure names as a goal of this migration and
// reserves raw procedure invocation for the few procedures whose logic cannot be expressed
// relationally; AAP 0.5.1.3 repeats that procedures are retained only where behaviourally
// necessary. These two are a keyed select and a whole-table select whose body also creates the
// installation GUID when absent, so each is expressed here as a parameterised statement against
// the table instead. The measured behaviour is preserved, side effect included - only the object
// the command names changes - and every caller value still crosses as a bound, sized parameter.

/// <summary>
/// Reads the installation-wide configuration rows the legacy platform persisted in the
/// <c>HostSettings</c> table. This is the single implementation of
/// <see cref="IHostSettingsService"/>.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Terminal schema, honoured and never altered.</strong> The table is created at
/// <c>Website/Providers/DataProviders/SqlDataProvider/01.00.05.SqlDataProvider</c> with
/// <c>SettingName nvarchar(50) NOT NULL</c> and <c>SettingValue nvarchar(256) NOT NULL</c>;
/// <c>02.00.01</c> makes the name the clustered primary key, and <c>03.00.12</c> adds
/// <c>SettingIsSecure bit NOT NULL</c> defaulting to zero, which <c>03.01.01</c> re-asserts. Those
/// three facts are the whole contract: the name width bounds the keyed read parameter, and the
/// primary key makes a name identify at most one row. Rule T4 applies without exception - nothing
/// here creates, alters, drops or migrates any database object.
/// </para>
/// <para>
/// <strong>Lifetime.</strong> Registered scoped, alongside the scoped context whose connection it
/// borrows. It holds no mutable state of its own, adds no accessor, factory or disposal member,
/// and never opens a connection it did not need: the container owns the context, and the context
/// owns the connection.
/// </para>
/// <para>
/// <strong>Cancellation.</strong> Every member is awaitable and every token reaches the operation
/// that can honour it - opening, executing and reading. Cancellation is never caught, wrapped or
/// converted anywhere below; it leaves as it arrived.
/// </para>
/// </remarks>
internal sealed class HostSettingsService : IHostSettingsService
{
    /// <summary>Longest name the <c>SettingName</c> column accepts.</summary>
    private const int SettingNameMaxLength = 50;

    /// <summary>Bound parameter carrying the setting name.</summary>
    private const string SettingNameParameter = "@SettingName";

    /// <summary>
    /// Name of the row the whole-table read creates when it is missing, preserved verbatim from
    /// the measured procedure body.
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
    /// Whole-table projection. Only the name and the value are read: the secure flag is
    /// persistence metadata and has no place in the projection this service hands back.
    /// </summary>
    private const string SelectAllSettingsCommandText =
        "SELECT [SettingName], [SettingValue] FROM [dbo].[HostSettings];";

    /// <summary>
    /// The write-on-read half of the measured whole-table procedure
    /// (<c>04.05.00.SqlDataProvider</c> L898-L908), expressed as one atomic statement. The legacy
    /// body tested for the row and inserted it as two separate statements, which races; folding
    /// the test into the insert and taking an update lock over the key range means a concurrent
    /// caller cannot slip an identical row in between, so the primary key cannot be violated.
    /// </summary>
    private const string InsertInstallationIdentifierCommandText =
        "INSERT INTO [dbo].[HostSettings] ([SettingName], [SettingValue], [SettingIsSecure]) " +
        "SELECT @SettingName, CONVERT(nvarchar(256), NEWID()), 0 " +
        "WHERE NOT EXISTS (SELECT 1 FROM [dbo].[HostSettings] WITH (UPDLOCK, HOLDLOCK) " +
        "WHERE [SettingName] = @SettingName);";

    private readonly DnnDbContext _dbContext;

    /// <summary>
    /// Initialises a new instance of the <see cref="HostSettingsService"/> class.
    /// </summary>
    /// <param name="dbContext">
    /// The scoped persistence context, taken solely to borrow the relational connection it already
    /// owns. This is the narrow Rule-T3 exception recorded above; the context is never exposed.
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
    /// Absence and emptiness are separated here for the first time. An empty result set means no
    /// row carries the name, which is reported as <see langword="null"/>; a row whose value is
    /// empty is reported as the empty string. No caching happens on this path, for the reason
    /// recorded in migration note 5.
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

                    // A null reference here means the projection returned no ROW, which is the one
                    // case the contract reports as null. DBNull means a row exists whose column is
                    // null - impossible against the NOT NULL column, so this arm is boundary
                    // defence against a hand-edited database, and it answers with the empty string
                    // because a row does exist. The final arm is unreachable for an nvarchar
                    // column and exists only because the compiler requires the switch to be
                    // exhaustive; it converts invariantly so that no ambient culture can reshape a
                    // stored value in transit.
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
    /// <para>
    /// Two statements, in the order the measured procedure body used them: create the
    /// installation-identifier row if it is missing, then project every row. The first is the
    /// write-on-read side effect recorded in migration note 6, retained rather than dropped; it is
    /// idempotent and race-safe, so calling this member concurrently is safe even on a database
    /// that has never held the row.
    /// </para>
    /// <para>
    /// The projection is fully materialised and the reader is closed before anything is returned,
    /// so no caller can find itself holding an open reader or a live query. Keys compare
    /// ordinally, which is what the legacy untyped collection did - it used the default
    /// case-sensitive comparison - so a caller asking by the exact stored name behaves as it always
    /// did. No caching happens on this path either, for the reason recorded in migration note 5.
    /// </para>
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

                            // The column is NOT NULL, so this test is boundary defence rather than
                            // ordinary data handling. It answers with the empty string, which is
                            // precisely what the legacy accumulation did for a null column.
                            bool valueIsNull = await reader
                                .IsDBNullAsync(SettingValueOrdinal, token)
                                .ConfigureAwait(false);

                            string value = valueIsNull
                                ? string.Empty
                                : await reader
                                    .GetFieldValueAsync<string>(SettingValueOrdinal, token)
                                    .ConfigureAwait(false);

                            // Assigned rather than added: the name is the primary key, so a
                            // duplicate cannot occur, and assignment means a database that somehow
                            // held one could not turn a read into an exception.
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
    /// Lends the context's own connection to <paramref name="operation"/>, opening it only if it
    /// was not already open and closing it again only if this call is what opened it.
    /// </summary>
    /// <typeparam name="TResult">What the operation produces.</typeparam>
    /// <param name="operation">The work to run against the connection.</param>
    /// <param name="cancellationToken">Observed while opening and passed on to the operation.</param>
    /// <returns>Whatever the operation produced.</returns>
    /// <remarks>
    /// The connection is borrowed, never created and never disposed: it belongs to the scoped
    /// context, and disposing it would break every later operation in the same scope. Closing it
    /// when somebody else opened it would be the same mistake in a smaller form, which is why the
    /// close is conditional. This method is the whole of the Rule-T3 exception's contact with the
    /// provider, and it is private, so nothing relational reaches a caller.
    /// </remarks>
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
    /// Prepares a text command on the borrowed connection, enlisted in whatever transaction the
    /// context currently has open.
    /// </summary>
    /// <param name="connection">The borrowed connection.</param>
    /// <param name="commandText">
    /// One of this class's constant statements. Caller data never reaches this argument, and no
    /// statement below is composed, concatenated or interpolated from a value.
    /// </param>
    /// <returns>A command ready for its parameters.</returns>
    private DbCommand CreateCommand(DbConnection connection, string commandText)
    {
        DbCommand command = connection.CreateCommand();

        command.CommandText = commandText;
        command.CommandType = CommandType.Text;

        // Enlisting keeps the write-on-read GUID seed inside any surrounding unit of work instead of
        // escaping it, and the transaction is read afresh for every command so one begun after this
        // service was constructed is still picked up.
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

    /// <summary>
    /// Checks a setting name against the column that has to hold it.
    /// </summary>
    /// <param name="settingName">The name as supplied.</param>
    /// <returns>The same name, once it is known to fit.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="settingName"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">The name is blank or too long.</exception>
    /// <remarks>
    /// A blank name is refused rather than looked up, because the column is the primary key and a
    /// blank primary key identifies nothing a caller could have meant. An over-long name is
    /// refused for the same reason: no row can carry it, so accepting one would either truncate the
    /// caller's intent or surface as a provider error further down.
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
