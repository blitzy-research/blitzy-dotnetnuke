using System.Globalization;
using System.Reflection;
using Microsoft.Data.SqlClient;
using Testcontainers.MsSql;

namespace DnnMigration.IntegrationTests;

/// <summary>
/// Provisions the relational database that the integration suite runs against, and removes it again
/// when the run finishes.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why SQL Server and not an in-memory store.</strong> Three code paths under test cannot be
/// exercised by a non-relational provider, and one of them is required by the acceptance criteria:
/// </para>
/// <list type="bullet">
///   <item><description>
///     <c>PermissionRepository.DeleteUserPermissionsAsync</c> issues <c>ExecuteDeleteAsync</c>, which the
///     in-memory provider does not implement.
///   </description></item>
///   <item><description>
///     <c>MembershipStore.ApprovedUsers</c> hands back a <c>FromSqlInterpolated</c> query root so the
///     approval predicate reaches the database before paging.
///   </description></item>
///   <item><description>
///     <c>MembershipStore.IsAvailableAsync</c> returns <see langword="false"/> unless the provider is SQL
///     Server <em>and</em> the three external <c>aspnet_*</c> objects exist. Every credential-dependent
///     endpoint - account creation, sign-in, password change, unlock, approval - fails closed without them,
///     and account creation answering 201 is an explicit acceptance criterion.
///   </description></item>
/// </list>
/// <para>
/// <strong>Where the server comes from.</strong> If <see cref="ServerConnectionEnvironmentVariable"/> is
/// set, that connection string is used as the administrative connection and this type creates its own
/// database on that server. Otherwise a throwaway SQL Server container is started for the duration of the
/// run. The container generates its own password, so no credential is committed to source control - which
/// is the whole point of preferring it to a hard-coded local connection string.
/// </para>
/// <para>
/// <strong>Isolation.</strong> Each run gets a freshly named database, so parallel clones of this
/// repository sharing one host cannot collide with each other.
/// </para>
/// </remarks>
public sealed class TestDatabaseFactory : IAsyncDisposable
{
    /// <summary>
    /// Environment variable naming an existing SQL Server to use instead of starting a container. The
    /// connection string must have rights to create a database.
    /// </summary>
    public const string ServerConnectionEnvironmentVariable = "DNN_TEST_SQLSERVER";

    /// <summary>
    /// Container image used when no server is supplied. This tag is deliberately the same one the
    /// project's development compose file uses, so the image is already present locally and no registry
    /// pull is needed.
    /// </summary>
    private const string ContainerImage = "mcr.microsoft.com/mssql/server:2022-latest";

    /// <summary>Client-side batch delimiter used by both embedded schema scripts.</summary>
    private const string BatchSeparator = "GO";

    /// <summary>Embedded resource holding the mapped DotNetNuke tables.</summary>
    private const string SchemaResourceName = "DnnMigration.IntegrationTests.Schema.DnnSchema.sql";

    /// <summary>Embedded resource holding the external ASP.NET membership objects.</summary>
    private const string MembershipResourceName = "DnnMigration.IntegrationTests.Schema.MembershipSchema.sql";

    /// <summary>Embedded resource holding the unmapped installation-wide settings table.</summary>
    private const string HostSettingsResourceName = "DnnMigration.IntegrationTests.Schema.HostSettingsSchema.sql";

    private readonly MsSqlContainer? _container;
    private readonly string _administrativeConnectionString;
    private readonly string _databaseName;

    private bool _disposed;

    private TestDatabaseFactory(
        MsSqlContainer? container,
        string administrativeConnectionString,
        string databaseName,
        string connectionString)
    {
        _container = container;
        _administrativeConnectionString = administrativeConnectionString;
        _databaseName = databaseName;
        ConnectionString = connectionString;
    }

    /// <summary>The connection string of the provisioned database, ready for <c>ConnectionStrings:Default</c>.</summary>
    public string ConnectionString { get; }

    /// <summary>The name of the provisioned database.</summary>
    public string DatabaseName => _databaseName;

    /// <summary>
    /// Starts or locates a SQL Server, creates a uniquely named database on it, and applies both schema
    /// scripts.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The provisioned database.</returns>
    public static async Task<TestDatabaseFactory> CreateAsync(CancellationToken cancellationToken = default)
    {
        string databaseName = string.Concat(
            "DnnMigrationTests_",
            Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture).AsSpan(0, 12));

        MsSqlContainer? container = null;
        string administrativeConnectionString;

        string? configured = Environment.GetEnvironmentVariable(ServerConnectionEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(configured))
        {
            administrativeConnectionString = Normalise(configured, "master");
        }
        else
        {
            container = new MsSqlBuilder()
                .WithImage(ContainerImage)
                .Build();

            await container.StartAsync(cancellationToken).ConfigureAwait(false);
            administrativeConnectionString = Normalise(container.GetConnectionString(), "master");
        }

        string connectionString = Normalise(administrativeConnectionString, databaseName);

        try
        {
            await CreateDatabaseAsync(administrativeConnectionString, databaseName, cancellationToken)
                .ConfigureAwait(false);

            await ApplyScriptAsync(connectionString, ReadResource(SchemaResourceName), cancellationToken)
                .ConfigureAwait(false);

            await ApplyScriptAsync(connectionString, ReadResource(MembershipResourceName), cancellationToken)
                .ConfigureAwait(false);

            await ApplyScriptAsync(connectionString, ReadResource(HostSettingsResourceName), cancellationToken)
                .ConfigureAwait(false);
        }
        catch
        {
            if (container is not null)
            {
                await container.DisposeAsync().ConfigureAwait(false);
            }

            throw;
        }

        return new TestDatabaseFactory(container, administrativeConnectionString, databaseName, connectionString);
    }

    /// <summary>Opens a connection to the provisioned database.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>An open connection the caller owns and must dispose.</returns>
    public async Task<SqlConnection> OpenAsync(CancellationToken cancellationToken = default)
    {
        var connection = new SqlConnection(ConnectionString);

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

    /// <summary>Executes a non-query statement against the provisioned database.</summary>
    /// <param name="sql">The statement text.</param>
    /// <param name="parameters">Parameters to bind, keyed by name including the leading <c>@</c>.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The number of rows affected.</returns>
    public async Task<int> ExecuteAsync(
        string sql,
        IReadOnlyDictionary<string, object?>? parameters = null,
        CancellationToken cancellationToken = default)
    {
        await using SqlConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqlCommand command = connection.CreateCommand();

        command.CommandText = sql;
        Bind(command, parameters);

        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Executes a statement against the provisioned database and returns its first value.</summary>
    /// <typeparam name="T">The expected value type.</typeparam>
    /// <param name="sql">The statement text.</param>
    /// <param name="parameters">Parameters to bind, keyed by name including the leading <c>@</c>.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The converted scalar value.</returns>
    /// <exception cref="InvalidOperationException">The statement returned no value.</exception>
    public async Task<T> ScalarAsync<T>(
        string sql,
        IReadOnlyDictionary<string, object?>? parameters = null,
        CancellationToken cancellationToken = default)
    {
        await using SqlConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqlCommand command = connection.CreateCommand();

        command.CommandText = sql;
        Bind(command, parameters);

        object? value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        if (value is null || value is DBNull)
        {
            throw new InvalidOperationException(
                FormattableString.Invariant($"The statement produced no value: {sql}"));
        }

        return (T)Convert.ChangeType(value, typeof(T), CultureInfo.InvariantCulture);
    }

    /// <summary>Removes the provisioned database, and the container hosting it when this run started one.</summary>
    /// <returns>A task that completes once the resources are released.</returns>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (_container is not null)
        {
            // Disposing the container removes the database with it, so there is nothing to drop first.
            await _container.DisposeAsync().ConfigureAwait(false);
            return;
        }

        // A caller-supplied server outlives this run, so the database has to be dropped explicitly.
        // Existing sessions are rolled back first, because a database with an open connection cannot be
        // dropped and a leaked test database would break the next run's isolation.
        try
        {
            SqlConnection.ClearAllPools();

            await using var connection = new SqlConnection(_administrativeConnectionString);
            await connection.OpenAsync().ConfigureAwait(false);

            await using SqlCommand command = connection.CreateCommand();
            command.CommandText = string.Concat(
                "IF DB_ID(N'", _databaseName, "') IS NOT NULL BEGIN ",
                "ALTER DATABASE [", _databaseName, "] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; ",
                "DROP DATABASE [", _databaseName, "]; END");

            await command.ExecuteNonQueryAsync().ConfigureAwait(false);
        }
        catch (SqlException)
        {
            // A database that cannot be dropped is a housekeeping problem on a developer machine, never a
            // test result. Reporting it as a failure would turn a clean run red for the wrong reason.
        }
    }

    private static void Bind(SqlCommand command, IReadOnlyDictionary<string, object?>? parameters)
    {
        if (parameters is null)
        {
            return;
        }

        foreach (KeyValuePair<string, object?> parameter in parameters)
        {
            command.Parameters.AddWithValue(parameter.Key, parameter.Value ?? DBNull.Value);
        }
    }

    private static async Task CreateDatabaseAsync(
        string administrativeConnectionString,
        string databaseName,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(administrativeConnectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        await using SqlCommand command = connection.CreateCommand();

        // The name is generated by this type from a GUID, never supplied by a caller, so there is no
        // untrusted input to parameterise here - and a database name cannot be a bound parameter anyway.
        command.CommandText = string.Concat("CREATE DATABASE [", databaseName, "]");
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task ApplyScriptAsync(
        string connectionString,
        string script,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        int ordinal = 0;
        foreach (string batch in SplitBatches(script))
        {
            ordinal++;

            await using SqlCommand command = connection.CreateCommand();
            command.CommandText = batch;

            try
            {
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (SqlException failure)
            {
                // A schema script that fails halfway leaves a database no test can interpret, and the
                // provider's message alone does not say which statement was rejected. Naming the batch and
                // quoting it turns a five-word error into a diagnosis.
                throw new InvalidOperationException(
                    FormattableString.Invariant(
                        $"Schema batch {ordinal} failed: {failure.Message}{Environment.NewLine}{Truncate(batch)}"),
                    failure);
            }
        }
    }

    private static string Truncate(string text) =>
        text.Length <= 600 ? text : string.Concat(text.AsSpan(0, 600), "...");

    /// <summary>
    /// Splits a script on its client-side batch delimiter.
    /// </summary>
    /// <param name="script">The script text.</param>
    /// <returns>The non-empty batches, in order.</returns>
    /// <remarks>
    /// <c>GO</c> is understood by command-line tools and editors, not by the SQL Server protocol, so a
    /// script containing it cannot be sent as a single command. Only a line that consists solely of the
    /// delimiter is treated as one, so the word appearing inside an identifier or a comment is left alone.
    /// </remarks>
    private static IEnumerable<string> SplitBatches(string script)
    {
        var batch = new List<string>();

        foreach (string line in script.Split('\n'))
        {
            string trimmed = line.TrimEnd('\r');

            if (string.Equals(trimmed.Trim(), BatchSeparator, StringComparison.OrdinalIgnoreCase))
            {
                string text = string.Join('\n', batch).Trim();
                if (text.Length > 0)
                {
                    yield return text;
                }

                batch.Clear();
                continue;
            }

            batch.Add(trimmed);
        }

        string tail = string.Join('\n', batch).Trim();
        if (tail.Length > 0)
        {
            yield return tail;
        }
    }

    private static string ReadResource(string resourceName)
    {
        Assembly assembly = typeof(TestDatabaseFactory).Assembly;

        using Stream? stream = assembly.GetManifestResourceStream(resourceName);
        if (stream is null)
        {
            throw new InvalidOperationException(
                FormattableString.Invariant(
                    $"Embedded resource '{resourceName}' is missing. Available: {string.Join(", ", assembly.GetManifestResourceNames())}"));
        }

        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    /// <summary>
    /// Rewrites a connection string to target a specific database and to tolerate the self-signed
    /// certificate a throwaway local server presents.
    /// </summary>
    /// <param name="connectionString">The connection string to rewrite.</param>
    /// <param name="databaseName">The database to target.</param>
    /// <returns>The rewritten connection string.</returns>
    /// <remarks>
    /// Certificate validation is relaxed deliberately and only here: the server is a container created for
    /// this test run, so there is no identity to verify and no traffic that leaves the host. Nothing in the
    /// deployed configuration relaxes it - <c>docker/.env.example</c> documents a connection string that
    /// the operator supplies.
    /// </remarks>
    private static string Normalise(string connectionString, string databaseName)
    {
        var builder = new SqlConnectionStringBuilder(connectionString)
        {
            InitialCatalog = databaseName,
            TrustServerCertificate = true,
        };

        if (builder.ConnectTimeout < 30)
        {
            builder.ConnectTimeout = 30;
        }

        return builder.ConnectionString;
    }
}
