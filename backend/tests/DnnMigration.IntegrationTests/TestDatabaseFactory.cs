using System.Globalization;
using System.Reflection;
using System.Runtime.ExceptionServices;
using Microsoft.Data.SqlClient;
using Testcontainers.MsSql;

namespace DnnMigration.IntegrationTests;

/// <summary>
/// Provisions the relational database that the integration suite runs against, and removes it again when
/// the run finishes.
/// </summary>
/// <remarks>
/// <para>
/// <strong>THE INTERNAL CONTEXT IS NEVER NAMED HERE, AND NOTHING MAY BE ADDED SO THAT IT COULD BE.</strong>
/// The Infrastructure context is declared <c>internal</c> to its own assembly deliberately, so that no
/// layer above it can see a <c>DbContext</c> at all.
/// </para>
/// <para>
/// <strong>Why a real SQL Server rather than the in-memory or SQLite provider.</strong> Four paths under
/// test cannot be exercised by another provider, and the first is required by the acceptance criteria.
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
    /// Environment variable that directs the container route explicitly: a truthy value asks for a
    /// throwaway SQL Server container and outranks a configured server, a falsey value refuses one and also
    /// vetoes discovering a runtime.
    /// </summary>
    /// <remarks>
    /// Read as THREE states rather than as a boolean - asked for, refused, or not mentioned - by <see
    /// cref="ReadContainerRouteRequest"/>. The distinction is load-bearing because an unmentioned value
    /// permits <see cref="SelectProvider"/> to discover a runtime, so a value of <c>0</c>, <c>false</c>,
    /// <c>no</c> or <c>off</c> has to mean "not even then".
    /// </remarks>
    public const string ContainerOptInEnvironmentVariable = "DNN_TESTS_USE_MSSQL";

    /// <summary>Container image used when the container route is opted into.</summary>
    /// <remarks>
    /// PINNED TO AN IMMUTABLE CUMULATIVE-UPDATE TAG rather than to a moving one.
    /// </remarks>
    internal const string ContainerImage =
        "mcr.microsoft.com/mssql/server@sha256:ba4c8329f48fb8f02e1416be6a930ebfd71268caee78aa985f3af4315e457c89";

    /// <summary>The human-readable version the digest in <see cref="ContainerImage"/> resolves to.</summary>
    internal const string ContainerImageTag = "2022-CU26-ubuntu-22.04";

    /// <summary>Key under which a cleanup failure is attached to a primary provisioning failure.</summary>
    private const string CleanupFailureDataKey = "TestDatabaseCleanupFailure";

    /// <summary>How many times the drop is attempted before it is reported as a failure.</summary>
    /// <remarks>
    /// A drop can lose a legitimate race with a connection that has not yet been returned to the pool, and
    /// a second attempt after a short pause settles that. Retrying is what makes the eventual REPORT
    /// trustworthy: a failure that is reported after a single try invites being ignored as flaky, which is
    /// how an accumulating leak stays invisible.
    /// </remarks>
    private const int DropAttempts = 3;

    /// <summary>How long to wait between drop attempts.</summary>
    private static readonly TimeSpan DropRetryDelay = TimeSpan.FromMilliseconds(250);

    /// <summary>Client-side batch delimiter used by all three embedded schema scripts.</summary>
    private const string BatchSeparator = "GO";

    /// <summary>
    /// Embedded resource holding the mapped terminal DotNetNuke tables, indexes and physical constraints.
    /// </summary>
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

    /// <summary>
    /// The connection string of the provisioned database, ready for <c>ConnectionStrings:Default</c>.
    /// </summary>
    public string ConnectionString { get; }

    /// <summary>
    /// Decides which route this run provisions its database through, from the environment and, only when
    /// the environment directs nothing, from whether a container runtime is reachable.
    /// </summary>
    /// <returns>
    /// The selected route, or <see cref="TestDatabaseProvider.None"/> when no route is available - which
    /// <see cref="CreateAsync"/> turns into one actionable failure rather than a guess.
    /// </returns>
    /// <remarks>
    /// Public and side-effect-free so that the decision can be read - by a test, or by a developer
    /// reasoning about a run - without provisioning anything. The whole selection lives in this one member,
    /// which is what makes every claim about it checkable by reading one function rather than by starting a
    /// run.
    /// </remarks>
    public static TestDatabaseProvider SelectProvider()
    {
        ContainerRouteRequest request = ReadContainerRouteRequest(
            Environment.GetEnvironmentVariable(ContainerOptInEnvironmentVariable));

        if (request == ContainerRouteRequest.Requested)
        {
            return TestDatabaseProvider.Container;
        }

        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(ServerConnectionEnvironmentVariable)))
        {
            return TestDatabaseProvider.ConfiguredServer;
        }

        if (request == ContainerRouteRequest.Refused)
        {
            return TestDatabaseProvider.None;
        }

        return IsContainerRuntimeReachable()
            ? TestDatabaseProvider.DiscoveredContainer
            : TestDatabaseProvider.None;
    }

    /// <summary>What the container opt-in variable asks for.</summary>
    private enum ContainerRouteRequest
    {
        /// <summary>The variable is absent or blank, so discovery may proceed.</summary>
        Unspecified = 0,

        /// <summary>The variable asks for the container route.</summary>
        Requested = 1,

        /// <summary>The variable refuses the container route, which also vetoes discovery.</summary>
        Refused = 2,
    }

    /// <summary>Reads the container opt-in variable as a three-state request.</summary>
    /// <param name="value">The raw value, which may be absent.</param>
    /// <returns>What the value asks for.</returns>
    private static ContainerRouteRequest ReadContainerRouteRequest(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return ContainerRouteRequest.Unspecified;
        }

        string trimmed = value.Trim();

        bool refused = string.Equals(trimmed, "0", StringComparison.Ordinal)
            || string.Equals(trimmed, "false", StringComparison.OrdinalIgnoreCase)
            || string.Equals(trimmed, "no", StringComparison.OrdinalIgnoreCase)
            || string.Equals(trimmed, "off", StringComparison.OrdinalIgnoreCase);

        return refused ? ContainerRouteRequest.Refused : ContainerRouteRequest.Requested;
    }

    /// <summary>
    /// Reports whether a container runtime looks reachable from this process, without contacting it.
    /// </summary>
    /// <returns><see langword="true"/> when an endpoint variable or a well-known socket is present.</returns>
    /// <remarks>
    /// Deliberately a CHEAP, SIDE-EFFECT-FREE probe rather than a handshake. <see cref="SelectProvider"/>
    /// is documented as answerable without provisioning anything, and it is called before any container
    /// work begins; opening a connection here would make reading the decision cost a round trip and would
    /// give the probe two ways to fail.
    /// </remarks>
    private static bool IsContainerRuntimeReachable()
    {
        string[] endpointVariables =
        [
            "DOCKER_HOST",
            "TESTCONTAINERS_DOCKER_SOCKET_OVERRIDE",
        ];

        foreach (string variable in endpointVariables)
        {
            if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(variable)))
            {
                return true;
            }
        }

        string? runtimeDirectory = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        string?[] socketPaths =
        [
            "/var/run/docker.sock",
            "/run/docker.sock",
            "/run/podman/podman.sock",
            string.IsNullOrWhiteSpace(runtimeDirectory)
                ? null
                : Path.Combine(runtimeDirectory, "podman", "podman.sock"),
            string.IsNullOrWhiteSpace(home) ? null : Path.Combine(home, ".docker", "run", "docker.sock"),
        ];

        foreach (string? path in socketPaths)
        {
            if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Builds the diagnosis for a run that selected no route at all.</summary>
    /// <returns>The message, naming both supported routes and the reason nothing is substituted.</returns>
    private static string DescribeUnselectedProvider() => string.Join(
        Environment.NewLine,
        "The integration suite could not provision a test database: no SQL Server was available.",
        string.Empty,
        DescribeWhyNoRouteWasAvailable(),
        string.Empty,
        "Take either supported route:",
        string.Empty,
        FormattableString.Invariant(
            $"  1. {ServerConnectionEnvironmentVariable}=<connection string with rights to create a"),
        "     database>. The suite creates a uniquely named database on that server, applies the",
        "     schema, and drops it again at the end of the run. This is the PREFERRED route: it is the",
        "     fastest and it needs no container runtime. Any SQL Server 2019 or later will do, including",
        "     one you start yourself, so this is the route on a host with no container runtime at all.",
        string.Empty,
        FormattableString.Invariant($"  2. {ContainerOptInEnvironmentVariable}=1, which starts a throwaway"),
        FormattableString.Invariant($"     '{ContainerImage}' container for the run. It needs a reachable"),
        "     container runtime and generates its own credential, so nothing is committed to source",
        "     control. This route is also selected AUTOMATICALLY when neither variable is set and a",
        "     runtime is reachable, which is what lets the acceptance gates run unprepared - so seeing",
        "     this message means no runtime was found, and naming the variable will not conjure one.",
        string.Empty,
        "No in-memory or SQLite provider is substituted, and that is deliberate rather than an omission.",
        "The accounts these tests sign in as live in the external ASP.NET membership tables, and the",
        "credential store reports itself unavailable on any provider that is not SQL Server - so every",
        "persona in this assembly would fail to sign in, every protected-endpoint fact would be measuring",
        "a refusal instead of the behaviour it names, and the run would still be capable of reporting a",
        "pass. Refusing to start is the safer answer.");

    /// <summary>States why this run had no route, using only what was actually established.</summary>
    /// <returns>The explanation, in one or two sentences.</returns>
    private static string DescribeWhyNoRouteWasAvailable() =>
        ReadContainerRouteRequest(Environment.GetEnvironmentVariable(ContainerOptInEnvironmentVariable))
            == ContainerRouteRequest.Refused
            ? string.Join(
                Environment.NewLine,
                FormattableString.Invariant(
                    $"No server was configured, and {ContainerOptInEnvironmentVariable} is set to a value that"),
                "refuses the container route - which also vetoes discovering one, deliberately, so that",
                "turning the route off cannot be undone by discovery. Nothing was probed on this host.")
            : string.Join(
                Environment.NewLine,
                "This run reached the last resort: no server was configured, no container runtime could be",
                FormattableString.Invariant(
                    $"found on this host, and {ContainerOptInEnvironmentVariable} said nothing either way - so"),
                "a reachable runtime would have been discovered and used automatically.");

    /// <summary>The name of the provisioned database.</summary>
    public string DatabaseName => _databaseName;

    /// <summary>
    /// Starts or locates a SQL Server, creates a uniquely named database on it, and applies the three
    /// schema scripts: the mapped DotNetNuke tables, the external membership objects, and the
    /// installation-wide settings table.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The provisioned database.</returns>
    /// <exception cref="InvalidOperationException">
    /// No server was configured and no throwaway container could be started, or a schema batch was
    /// rejected.
    /// </exception>
    /// <remarks>
    /// A failure after the database exists releases it before the exception propagates, and releases it the
    /// way it was acquired: disposing a container takes its database with it, while a database created on a
    /// caller-supplied server outlives this run and has to be dropped.
    /// </remarks>
    public static async Task<TestDatabaseFactory> CreateAsync(CancellationToken cancellationToken = default)
    {
        string databaseName = string.Concat(
            "DnnMigrationTests_",
            Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture).AsSpan(0, 12));

        MsSqlContainer? container = null;
        string administrativeConnectionString;
        TestDatabaseProvider provider = SelectProvider();

        switch (provider)
        {
            case TestDatabaseProvider.ConfiguredServer:
                administrativeConnectionString = Normalise(
                    Environment.GetEnvironmentVariable(ServerConnectionEnvironmentVariable)!,
                    "master");
                break;

            case TestDatabaseProvider.Container:
            case TestDatabaseProvider.DiscoveredContainer:
                container = await StartContainerAsync(provider, cancellationToken).ConfigureAwait(false);
                administrativeConnectionString = Normalise(container.GetConnectionString(), "master");
                break;

            default:
                throw new InvalidOperationException(DescribeUnselectedProvider());
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

            // THREE SCRIPTS, AND NO FOURTH. A fourth once created [DnnMigration].[RefreshTokens] here, so
            // the suite silently provisioned a table the production application required and the unaltered
            // DotNetNuke schema does not contain - which made every refresh-token fact pass while login was
            // broken against the database this API is mandated to run on.
        }
        catch (Exception primary)
        {
            Exception? cleanup = await ReleaseAsync(container, administrativeConnectionString, databaseName)
                .ConfigureAwait(false);

            if (cleanup is not null)
            {
                primary.Data[CleanupFailureDataKey] = cleanup.ToString();

                await Console.Error
                    .WriteLineAsync("Test database cleanup also failed: " + cleanup.Message)
                    .ConfigureAwait(false);
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
    /// <param name="parameters">Parameters to bind, keyed by name.</param>
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
    /// <param name="parameters">Parameters to bind, keyed by name.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The converted scalar value.</returns>
    /// <exception cref="InvalidOperationException">
    /// The statement returned no row, or returned <c>NULL</c>.
    /// </exception>
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

        Exception? cleanup = await ReleaseAsync(_container, _administrativeConnectionString, _databaseName)
            .ConfigureAwait(false);

        if (cleanup is not null)
        {
            // Raised here, unlike on the provisioning path, because there is no primary failure for it to
            // displace: the run has finished and this is the only thing that went wrong.
            ExceptionDispatchInfo.Capture(cleanup).Throw();
        }
    }

    /// <summary>
    /// Releases a provisioned database the way it was acquired, and never reports a housekeeping problem as
    /// a failure.
    /// </summary>
    /// <param name="container">
    /// The container started for this run, or <see langword="null"/> when a server was supplied.
    /// </param>
    /// <param name="administrativeConnectionString">The administrative connection used to drop the database.</param>
    /// <param name="databaseName">The database to release.</param>
    /// <returns>A task that completes once the resources are released.</returns>
    /// <remarks>
    /// Existing sessions are rolled back before the drop, because a database with an open connection cannot
    /// be dropped, and pooled connections are cleared first for the same reason - a pool holds connections
    /// open after the last reader has finished with them.
    /// </remarks>
    private static async Task<Exception?> ReleaseAsync(
        MsSqlContainer? container,
        string administrativeConnectionString,
        string databaseName)
    {
        if (container is not null)
        {
            // Disposing the container removes the database with it, so there is nothing to drop first.
            try
            {
                await container.DisposeAsync().ConfigureAwait(false);
                return null;
            }
            catch (Exception failure)
            {
                return failure;
            }
        }

        // A caller-supplied server outlives this run, so the database has to be dropped explicitly.
        Exception? lastFailure = null;

        for (int attempt = 1; attempt <= DropAttempts; attempt++)
        {
            try
            {
                SqlConnection.ClearAllPools();

                await using var connection = new SqlConnection(administrativeConnectionString);
                await connection.OpenAsync().ConfigureAwait(false);

                await using SqlCommand command = connection.CreateCommand();

                // The name was generated by this type from a GUID rather than supplied by a caller, and a
                // database name cannot be a bound parameter in any case.
                command.CommandText = string.Concat(
                    "IF DB_ID(N'", databaseName, "') IS NOT NULL BEGIN ",
                    "ALTER DATABASE [", databaseName, "] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; ",
                    "DROP DATABASE [", databaseName, "]; END");

                await command.ExecuteNonQueryAsync().ConfigureAwait(false);

                return null;
            }
            catch (Exception failure)
            {
                lastFailure = failure;

                if (attempt < DropAttempts)
                {
                    await Task.Delay(DropRetryDelay).ConfigureAwait(false);
                }
            }
        }

        return new InvalidOperationException(
            string.Join(
                Environment.NewLine,
                FormattableString.Invariant(
                    $"The test database '{databaseName}' could not be dropped after {DropAttempts} attempts."),
                "It was created on the server named by "
                + ServerConnectionEnvironmentVariable
                + ", which outlives this run, so it is still there.",
                "REMEDY: drop it by hand. Every run creates a uniquely named database, so an undropped one",
                "accumulates silently until the server runs out of room - which is why this is reported",
                "rather than absorbed.",
                string.Empty,
                "Last failure: " + lastFailure!.Message),
            lastFailure);
    }

    /// <summary>
    /// Starts the throwaway server this run selected, translating an unusable container runtime into a
    /// single actionable failure.
    /// </summary>
    /// <param name="provider">
    /// The route that led here, so the diagnosis can say whether the container was ASKED for or DISCOVERED.
    /// Reporting the wrong one sends the reader to the wrong remedy: an opted-in run needs its runtime
    /// fixed, whereas a discovered run can simply be pointed at a server instead.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The started container.</returns>
    /// <remarks>
    /// The container is disposed before the translated failure is raised. A partially started container
    /// still holds a Docker resource, and this method owns it until it hands a started one back, so
    /// releasing it here is what keeps a failed run from leaking one.
    /// </remarks>
    private static async Task<MsSqlContainer> StartContainerAsync(
        TestDatabaseProvider provider,
        CancellationToken cancellationToken)
    {
        MsSqlContainer? container = null;

        try
        {
            container = new MsSqlBuilder().WithImage(ContainerImage).Build();

            await container.StartAsync(cancellationToken).ConfigureAwait(false);
            return container;
        }
        catch (OperationCanceledException)
        {
            // Cancellation is the caller's own decision, so it carries no diagnosis worth adding and must
            // keep its type: rewrapping it would make a deliberate stop look like a provisioning fault.
            await DisposeContainerAsync(container).ConfigureAwait(false);
            throw;
        }
        catch (Exception failure)
        {
            await DisposeContainerAsync(container).ConfigureAwait(false);

            string selection = provider == TestDatabaseProvider.DiscoveredContainer
                ? "Nothing named a route, a container runtime looked reachable, so this run started"
                : FormattableString.Invariant(
                    $"{ContainerOptInEnvironmentVariable} opted this run into");

            string remedy = provider == TestDatabaseProvider.DiscoveredContainer
                ? FormattableString.Invariant(
                    $"REMEDY: set {ServerConnectionEnvironmentVariable} to a connection string with rights to create a database.")
                : FormattableString.Invariant(
                    $"REMEDY: either make the container runtime usable, or clear {ContainerOptInEnvironmentVariable} and set {ServerConnectionEnvironmentVariable}.");

            string diagnosis = string.Join(
                Environment.NewLine,
                "The integration suite could not provision a test database.",
                string.Empty,
                selection,
                FormattableString.Invariant(
                    $"a throwaway '{ContainerImage}' container, and starting it failed:"),
                failure.Message,
                string.Empty,
                remedy,
                "The suite then creates and drops its own database on that server and starts no container,",
                "which is the supported route on a host whose container runtime is missing or unusable.",
                string.Empty,
                "A permissive in-memory or SQLite provider is deliberately NOT used as a fallback. The",
                "credential paths under test report themselves unavailable on any provider that is not SQL",
                "Server, so such a run would report a pass while the behaviour under test had silently",
                "stopped executing - a worse outcome than this failure.");

            throw new InvalidOperationException(diagnosis, failure);
        }

        static async Task DisposeContainerAsync(MsSqlContainer? started)
        {
            if (started is not null)
            {
                await started.DisposeAsync().ConfigureAwait(false);
            }
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

    /// <summary>Applies one schema script, batch by batch, to the database this type owns.</summary>
    /// <param name="connectionString">The connection string of the provisioned database.</param>
    /// <param name="script">The script text.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task that completes once every batch has been applied.</returns>
    /// <remarks>
    /// This is the only place in the solution that issues data-definition statements, and the only database
    /// it can reach is the GUID-named one <see cref="CreateAsync"/> has just created and will drop again.
    /// </remarks>
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

    /// <summary>Splits a script on its client-side batch delimiter.</summary>
    /// <param name="script">The script text.</param>
    /// <returns>The non-empty batches, in order.</returns>
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

/// <summary>
/// The routes by which the integration suite can obtain the SQL Server it provisions its database on.
/// </summary>
public enum TestDatabaseProvider
{
    /// <summary>
    /// No route was available: nothing was configured and no container runtime could be found, or the
    /// container route was explicitly refused. Provisioning fails with one diagnosis naming both routes.
    /// </summary>
    None = 0,

    /// <summary>
    /// An already-running server, named by <see
    /// cref="TestDatabaseFactory.ServerConnectionEnvironmentVariable"/>. The suite creates and drops its
    /// own uniquely named database on it.
    /// </summary>
    ConfiguredServer = 1,

    /// <summary>
    /// A throwaway container, asked for explicitly by <see
    /// cref="TestDatabaseFactory.ContainerOptInEnvironmentVariable"/>. This request outranks a configured
    /// server.
    /// </summary>
    Container = 2,

    /// <summary>
    /// A throwaway container selected because nothing was configured and a container runtime was found to
    /// be reachable. This is what lets the acceptance gates run on an unprepared host; it is never chosen
    /// when a server is configured, and never when the container route has been explicitly refused.
    /// </summary>
    DiscoveredContainer = 3,
}
