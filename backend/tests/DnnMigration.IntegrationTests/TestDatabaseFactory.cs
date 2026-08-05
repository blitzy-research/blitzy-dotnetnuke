using System.Globalization;
using System.Reflection;
using System.Runtime.ExceptionServices;
using Microsoft.Data.SqlClient;
using Testcontainers.MsSql;

namespace DnnMigration.IntegrationTests;

/// <summary>
/// Provisions the relational database that the integration suite runs against, and removes it again
/// when the run finishes.
/// </summary>
/// <remarks>
/// <para>
/// <strong>THE INTERNAL CONTEXT IS NEVER NAMED HERE, AND NOTHING MAY BE ADDED SO THAT IT COULD BE.</strong>
/// The Infrastructure context is declared <c>internal</c> to its own assembly deliberately, so that no
/// layer above it can see a <c>DbContext</c> at all. A test project may reference that assembly and still
/// be structurally unable to name the type, and this file does not try to: it changes only <em>where the
/// data lives</em>. It provisions a database, publishes <see cref="ConnectionString"/>, and lets the
/// fixture place that value in <c>ConnectionStrings:Default</c>, after which the production registration
/// binds to it unaltered. No service descriptor is removed, no provider is re-registered, no reflection
/// over the Infrastructure assembly is performed, and no <c>InternalsVisibleTo</c> is required.
/// <strong>Do not add one</strong>, do not add an assembly attribute to the project file, and do not ask
/// for the context to be made public: that accessibility is the design rather than an obstacle to it, and
/// the pipeline these tests exercise is the real one precisely because nothing was substituted to reach
/// it.
/// </para>
/// <para>
/// <strong>Why a real SQL Server rather than the in-memory or SQLite provider.</strong> Four paths under
/// test cannot be exercised by another provider, and the first is required by the acceptance criteria.
/// </para>
/// <list type="number">
///   <item><description>
///     <c>MembershipStore.IsAvailableAsync</c> answers <see langword="false"/> without probing at all when
///     the connection is not SQL Server, because every statement that type issues is Transact-SQL. Each
///     credential-dependent endpoint - account creation, sign-in, password change, unlock, approval - then
///     fails closed, and account creation answering 201 is an explicit acceptance criterion.
///   </description></item>
///   <item><description>
///     <c>MembershipStore.ApprovedUsers</c> hands back a <c>FromSqlInterpolated</c> query root so the
///     approval predicate reaches the database before paging, and <c>IsAvailableAsync</c> establishes its
///     tables with <c>OBJECT_ID</c>. The in-memory provider executes no raw SQL of any kind.
///   </description></item>
///   <item><description>
///     The three external <c>aspnet_*</c> objects are not mapped entity types, so no model-driven creation
///     could produce them however permissive the provider. They exist in a test database only because
///     <c>MembershipSchema.sql</c> creates them explicitly.
///   </description></item>
///   <item><description>
///     The persistence suite asserts the Fluent mapping against <c>INFORMATION_SCHEMA.COLUMNS</c> - the
///     declared data type and length of individual columns. That is a relational catalogue: the in-memory
///     provider has none, and SQLite's type affinity would let the assertion pass while proving nothing.
///   </description></item>
/// </list>
/// <para>
/// The reverse of that argument is worth stating too, because it is the reason this type has no provider
/// selector. Degrading to a permissive provider when a server is unavailable would not make the suite
/// portable - it would make it dishonest, reporting a pass for credential behaviour that had silently
/// stopped executing. An unreachable server is therefore surfaced as one actionable failure naming
/// <see cref="ServerConnectionEnvironmentVariable"/>, and never absorbed.
/// </para>
/// <para>
/// <strong>No schema is ever created from the model.</strong> MIGRATION: <c>EnsureCreated</c>,
/// <c>EnsureDeleted</c> and <c>Database.Migrate</c> are absent here and must stay absent. The DotNetNuke
/// schema is produced by the eighty-eight legacy upgrade scripts and depends on membership objects those
/// scripts only ever ALTER - searching every one of them case-insensitively, and across all four naming
/// conventions they use, finds exactly one CREATE of an <c>aspnet_</c> object, and it is a DotNetNuke
/// helper procedure rather than one of Microsoft's - so the model cannot reproduce the terminal schema even
/// in principle. Against a real installation the baseline migration is applied as a no-op that seeds a
/// single history row. Here the schema comes from explicit DDL instead, and every statement it contains
/// reaches only the throwaway database this type has just created and owns.
/// </para>
/// <para>
/// <strong>Where the server comes from, and why it is never guessed at.</strong> The route is SELECTED
/// EXPLICITLY, by <see cref="SelectProvider"/>, from exactly two environment variables, and a run that names
/// neither is refused with a single actionable diagnosis rather than quietly doing something expensive.
/// Setting <see cref="ServerConnectionEnvironmentVariable"/> uses that connection string as the
/// administrative connection and creates a database on that server; setting
/// <see cref="ContainerOptInEnvironmentVariable"/> starts a throwaway SQL Server container for the duration
/// of the run.
/// </para>
/// <para>
/// A container is NEVER started implicitly, and that is a requirement rather than a preference: the
/// migration plan lists the container package as explicitly not the default, and the configured-server route
/// is what makes the acceptance gates runnable on a host with no container runtime at all. An earlier
/// revision started a container whenever the server variable happened to be absent, which meant an
/// unconfigured run silently depended on a Docker daemon, pulled an image and took minutes - and reported
/// container plumbing as the cause when the real cause was a missing variable.
/// </para>
/// <para>
/// <strong>Why there is no permissive fallback.</strong> Neither an in-memory nor a SQLite provider is
/// substituted when nothing is configured. The credential paths this suite exercises answer
/// "store unavailable" on any provider that is not SQL Server, because the accounts live in external
/// membership tables, and every persona in the assembly now obtains its token by signing in for real. A
/// permissive provider would therefore report a pass while the behaviour under test had stopped executing,
/// which is strictly worse than refusing to run.
/// </para>
/// <para>
/// <strong>Fidelity of what the scripts provision.</strong> The legacy identity seeds are reproduced rather
/// than normalised, because the suite asserts on them. <c>Portals</c> starts at -1 and <c>Roles</c>,
/// <c>Tabs</c> and <c>Modules</c> start at 0, so a legal identifier collides both with the legacy
/// <c>NullInteger</c> sentinel at -1 and with a plausible reading of 0 as "no value", while <c>Users</c>
/// starts at 1 and neither is a valid account there. Column shapes are kept for the same reason:
/// <c>HostFee</c> is a fee held in <c>nvarchar(10)</c> whose seeded value is the empty string - which the
/// legacy sentinel table treats as its null - and <c>BillingFrequency</c> is a <c>char(1)</c> that accepts
/// codes outside the documented set. A test can only prove that an empty string never becomes null, or
/// that an unmapped billing code does not throw, if the store it runs against still permits both.
/// </para>
/// <para>
/// <strong>Isolation.</strong> Each run gets a freshly named database, so parallel clones of this
/// repository sharing one host cannot collide with each other - the name carries a fresh identifier
/// rather than a fixed one, which is what makes that true without any per-clone configuration. Within a
/// run the suite is a single serial collection over one fixture, so exactly one instance of this type
/// exists, the schema is applied once, and the seed is inserted once.
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
    /// Environment variable that OPTS IN to starting a throwaway SQL Server container. Absent or falsey, no
    /// container is ever started.
    /// </summary>
    /// <remarks>
    /// An opt-in rather than an opt-out, because starting a container is the expensive, daemon-dependent route
    /// and the one the migration plan names as not the default. Accepted values are the ordinary truthy
    /// spellings - see <see cref="IsOptedIn"/> - so a value of <c>0</c> or <c>false</c> reads as "no", which is
    /// what an operator turning the route off will write.
    /// </remarks>
    public const string ContainerOptInEnvironmentVariable = "DNN_TESTS_USE_MSSQL";

    /// <summary>
    /// Container image used when the container route is opted into.
    /// </summary>
    /// <remarks>
    /// <para>
    /// PINNED TO AN IMMUTABLE CUMULATIVE-UPDATE TAG rather than to a moving one. A moving tag makes the server
    /// this suite runs against change underneath it without a single line of the repository changing, so a
    /// suite that passed yesterday can fail today for a reason no diff explains - and the failures a server
    /// upgrade produces are exactly the subtle kind this suite exists to detect, because the schema assertions
    /// here are about identity seeds, column types and collation behaviour.
    /// </para>
    /// <para>
    /// VERIFIED rather than assumed: this tag resolves in the registry and its manifest digest is
    /// <c>sha256:ba4c8329f48fb8f02e1416be6a930ebfd71268caee78aa985f3af4315e457c89</c>, which is
    /// byte-identical to the image the development environment already holds - SQL Server 16.0.4265.3, the
    /// 2022 RTM-CU26 build, on Ubuntu 22.04. Pinning therefore pulls nothing new and changes nothing about what
    /// the suite runs against today; it only stops that from changing silently tomorrow.
    /// </para>
    /// </remarks>
    private const string ContainerImage = "mcr.microsoft.com/mssql/server:2022-CU26-ubuntu-22.04";

    /// <summary>Key under which a cleanup failure is attached to a primary provisioning failure.</summary>
    private const string CleanupFailureDataKey = "TestDatabaseCleanupFailure";

    /// <summary>How many times the drop is attempted before it is reported as a failure.</summary>
    /// <remarks>
    /// A drop can lose a legitimate race with a connection that has not yet been returned to the pool, and a
    /// second attempt after a short pause settles that. Retrying is what makes the eventual REPORT trustworthy:
    /// a failure that is reported after a single try invites being ignored as flaky, which is how an accumulating
    /// leak stays invisible.
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

    /// <summary>The connection string of the provisioned database, ready for <c>ConnectionStrings:Default</c>.</summary>
    public string ConnectionString { get; }

    /// <summary>
    /// Decides which route this run provisions its database through, from the environment alone.
    /// </summary>
    /// <returns>
    /// The selected route, or <see cref="TestDatabaseProvider.None"/> when the environment names neither -
    /// which <see cref="CreateAsync"/> turns into one actionable failure rather than a guess.
    /// </returns>
    /// <remarks>
    /// <para>
    /// Public and side-effect-free so that the decision can be read - by a test, or by a developer reasoning
    /// about a run - without provisioning anything. The whole selection lives in this one member, which is what
    /// makes "a container is never started implicitly" a property of a single readable function rather than of
    /// a condition buried in a provisioning path.
    /// </para>
    /// <para>
    /// <b>The explicit container opt-in wins when both are set,</b> because it is the more specific instruction:
    /// a configured server may well be ambient in a shell profile or a CI image, whereas nobody sets the
    /// container variable by accident. It is also the only way to exercise the container route at all on a host
    /// that has a server configured, and a route that cannot be exercised is a route that quietly rots - which
    /// is the defect class this whole remediation is about.
    /// </para>
    /// </remarks>
    public static TestDatabaseProvider SelectProvider()
    {
        if (IsOptedIn(Environment.GetEnvironmentVariable(ContainerOptInEnvironmentVariable)))
        {
            return TestDatabaseProvider.Container;
        }

        return string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(ServerConnectionEnvironmentVariable))
            ? TestDatabaseProvider.None
            : TestDatabaseProvider.ConfiguredServer;
    }

    /// <summary>Reads an environment value as an opt-in flag.</summary>
    /// <param name="value">The raw value, which may be absent.</param>
    /// <returns><see langword="true"/> when the value asks for the route.</returns>
    /// <remarks>
    /// The falsey spellings are recognised explicitly rather than treating any non-empty value as consent,
    /// because <c>DNN_TESTS_USE_MSSQL=0</c> is what an operator writes to turn the route OFF, and reading that
    /// as "on" would be the most confusing possible behaviour. Comparison is case-insensitive and trims, since
    /// these values arrive from shell profiles and CI definitions.
    /// </remarks>
    private static bool IsOptedIn(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        string trimmed = value.Trim();

        return !string.Equals(trimmed, "0", StringComparison.Ordinal)
            && !string.Equals(trimmed, "false", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(trimmed, "no", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(trimmed, "off", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Builds the diagnosis for a run that selected no route at all.</summary>
    /// <returns>The message, naming both supported routes and the reason nothing is substituted.</returns>
    /// <remarks>
    /// One message, raised once, at the moment the decision was needed. The alternative - letting each of a
    /// thousand facts fail on its own connection error - reports the symptom several hundred times and the
    /// remedy never.
    /// </remarks>
    private static string DescribeUnselectedProvider() => string.Join(
        Environment.NewLine,
        "The integration suite could not provision a test database: no provider was selected.",
        string.Empty,
        "Select exactly one of the two supported routes:",
        string.Empty,
        FormattableString.Invariant(
            $"  1. {ServerConnectionEnvironmentVariable}=<connection string with rights to create a"),
        "     database>. The suite creates a uniquely named database on that server, applies the",
        "     schema, and drops it again at the end of the run. This is the DEFAULT route and it needs",
        "     no container runtime, which is what makes the acceptance gates runnable on a host that",
        "     has none.",
        string.Empty,
        FormattableString.Invariant($"  2. {ContainerOptInEnvironmentVariable}=1, which starts a throwaway"),
        FormattableString.Invariant($"     '{ContainerImage}' container for the run. It needs a running"),
        "     container runtime and generates its own credential, so nothing is committed to source",
        "     control. It is opt-in on purpose and is never started implicitly.",
        string.Empty,
        "No in-memory or SQLite provider is substituted, and that is deliberate rather than an omission.",
        "The accounts these tests sign in as live in the external ASP.NET membership tables, and the",
        "credential store reports itself unavailable on any provider that is not SQL Server - so every",
        "persona in this assembly would fail to sign in, every protected-endpoint fact would be measuring",
        "a refusal instead of the behaviour it names, and the run would still be capable of reporting a",
        "pass. Refusing to start is the safer answer.");

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
    /// rejected. Both messages name what to do about it.
    /// </exception>
    /// <remarks>
    /// A failure after the database exists releases it before the exception propagates, and releases it the
    /// way it was acquired: disposing a container takes its database with it, while a database created on a
    /// caller-supplied server outlives this run and has to be dropped. Skipping the second case would leave
    /// an orphan behind on every failed run, and the GUID naming that gives each run its isolation is
    /// exactly what would make the accumulation invisible.
    /// </remarks>
    public static async Task<TestDatabaseFactory> CreateAsync(CancellationToken cancellationToken = default)
    {
        string databaseName = string.Concat(
            "DnnMigrationTests_",
            Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture).AsSpan(0, 12));

        MsSqlContainer? container = null;
        string administrativeConnectionString;

        switch (SelectProvider())
        {
            case TestDatabaseProvider.ConfiguredServer:
                administrativeConnectionString = Normalise(
                    Environment.GetEnvironmentVariable(ServerConnectionEnvironmentVariable)!,
                    "master");
                break;

            case TestDatabaseProvider.Container:
                container = await StartContainerAsync(cancellationToken).ConfigureAwait(false);
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

            // THREE SCRIPTS, AND NO FOURTH. A fourth once created [DnnMigration].[RefreshTokens] here, so the
            // suite silently provisioned a table the production application required and the unaltered
            // DotNetNuke schema does not contain - which made every refresh-token fact pass while login was
            // broken against the database this API is mandated to run on. The store is now process-local and
            // needs no schema, so the suite provisions only what the mapped model and the external membership
            // objects genuinely need.
        }
        catch (Exception primary)
        {
            Exception? cleanup = await ReleaseAsync(container, administrativeConnectionString, databaseName)
                .ConfigureAwait(false);

            if (cleanup is not null)
            {
                // Attached and written out, never thrown. The primary failure is the actionable one - it says
                // why provisioning failed - and a cleanup error raised in its place would replace a diagnosis
                // with a consequence. Both are recorded so neither is lost: the console line is what a reader
                // of the run output sees, and the data entry travels with the exception itself.
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
    /// <param name="parameters">
    /// Parameters to bind, keyed by name. The leading <c>@</c> is optional: the provider normalises a key
    /// given without it, and both spellings are in use across the suite, so neither form needs correcting.
    /// A <see langword="null"/> value is bound as <c>DBNull</c> rather than omitted.
    /// </param>
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
    /// <typeparam name="T">
    /// The expected value type. It must be convertible from what the column yields, so a value whose CLR
    /// type is not itself convertible - a <c>uniqueidentifier</c>, for instance - has to be cast in the
    /// statement rather than here.
    /// </typeparam>
    /// <param name="sql">The statement text.</param>
    /// <param name="parameters">
    /// Parameters to bind, keyed by name. The leading <c>@</c> is optional: the provider normalises a key
    /// given without it, and both spellings are in use across the suite, so neither form needs correcting.
    /// A <see langword="null"/> value is bound as <c>DBNull</c> rather than omitted.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The converted scalar value.</returns>
    /// <exception cref="InvalidOperationException">
    /// The statement returned no row, or returned <c>NULL</c>. An absent value is raised rather than
    /// returned as a default, because a silent zero or empty string would satisfy an assertion that the
    /// statement had in fact failed to answer - use <c>COALESCE</c> or <c>COUNT</c> when absence is the
    /// legitimate result being measured.
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
            // displace: the run has finished and this is the only thing that went wrong. An undropped database
            // on a shared server accumulates on every run and eventually breaks provisioning for everybody, so
            // it is a real defect rather than housekeeping - and one that nothing else in the solution would
            // ever notice, because the GUID naming that gives each run its isolation also hides the pile-up.
            ExceptionDispatchInfo.Capture(cleanup).Throw();
        }
    }

    /// <summary>
    /// Releases a provisioned database the way it was acquired, and never reports a housekeeping problem as
    /// a failure.
    /// </summary>
    /// <param name="container">The container started for this run, or <see langword="null"/> when a server was supplied.</param>
    /// <param name="administrativeConnectionString">The administrative connection used to drop the database.</param>
    /// <param name="databaseName">The database to release.</param>
    /// <returns>A task that completes once the resources are released.</returns>
    /// <remarks>
    /// <para>
    /// Shared by disposal and by the provisioning failure path, so that a run which fails halfway leaves
    /// exactly as little behind as a run which succeeds. Duplicating it was how the failure path came to
    /// release only the container and to leak a database created on a caller-supplied server.
    /// </para>
    /// <para>
    /// Existing sessions are rolled back before the drop, because a database with an open connection cannot
    /// be dropped, and pooled connections are cleared first for the same reason - a pool holds connections
    /// open after the last reader has finished with them. Clearing the pool is process-wide, which is safe
    /// here because the suite runs as a single serial collection with one fixture, so no other database is
    /// in use when this runs.
    /// </para>
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
    /// Starts the throwaway server this run opted into, translating an unusable container runtime into a
    /// single actionable failure.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The started container.</returns>
    /// <remarks>
    /// <para>
    /// Every failure on this path reaches the caller as the same symptom - no database - and the underlying
    /// exceptions describe container plumbing rather than the one thing a reader can act on. Reporting the
    /// remedy once, where the decision to start a container was actually taken, is the difference between a
    /// diagnosable run and several hundred tests failing over with a socket error. The original exception is
    /// preserved as the inner exception, because the remedy is a hypothesis and the cause is the evidence
    /// for it.
    /// </para>
    /// <para>
    /// The container is disposed before the translated failure is raised. A partially started container
    /// still holds a Docker resource, and this method owns it until it hands a started one back, so
    /// releasing it here is what keeps a failed run from leaking one.
    /// </para>
    /// </remarks>
    private static async Task<MsSqlContainer> StartContainerAsync(CancellationToken cancellationToken)
    {
        MsSqlContainer? container = null;

        try
        {
            container = new MsSqlBuilder(ContainerImage).Build();

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

            string diagnosis = string.Join(
                Environment.NewLine,
                "The integration suite could not provision a test database.",
                string.Empty,
                FormattableString.Invariant(
                    $"{ContainerOptInEnvironmentVariable} opted this run into a throwaway"),
                FormattableString.Invariant($"'{ContainerImage}' container, and starting it failed:"),
                failure.Message,
                string.Empty,
                "REMEDY: either make a container runtime available, or take the other supported route -",
                FormattableString.Invariant(
                    $"clear {ContainerOptInEnvironmentVariable} and set {ServerConnectionEnvironmentVariable}"),
                "to a connection string with rights to create a database. The suite then creates and drops",
                "its own database on that server and starts no container, which is the supported route on a",
                "host with no container runtime.",
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
    /// MIGRATION: this is the only place in the solution that issues data-definition statements, and the
    /// only database it can reach is the GUID-named one <see cref="CreateAsync"/> has just created and will
    /// drop again. Nothing here runs against an installation: rule T4 holds that the DotNetNuke schema is
    /// externally owned, so the shipped baseline migration is empty and is applied to a real database purely
    /// to seed a history row. Provisioning a test database from explicit DDL rather than from the model is
    /// what makes that possible - it also provisions the external membership objects, which are not mapped
    /// entity types and which no model-driven creation could produce.
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

/// <summary>
/// The routes by which the integration suite can obtain the SQL Server it provisions its database on.
/// </summary>
/// <remarks>
/// <para>
/// An enumeration rather than a pair of booleans read at the point of use, so that the decision has ONE name,
/// ONE place it is made - <see cref="TestDatabaseFactory.SelectProvider"/> - and can be asserted on without
/// provisioning anything. The defect this replaces was structural rather than a wrong condition: the choice
/// was implicit in an if/else inside the provisioning path, so "what will this run do?" could only be answered
/// by starting it.
/// </para>
/// <para>
/// There is deliberately no member for an in-memory or SQLite provider. Both are named in the migration plan as
/// available, and both are unusable here for a reason that is a property of this application rather than of the
/// test harness: the accounts live in external ASP.NET membership tables and the credential store reports
/// itself unavailable on any provider that is not SQL Server. Adding a member for a route that cannot execute
/// the behaviour under test would be adding exactly the kind of unexercised infrastructure this remediation
/// exists to remove.
/// </para>
/// </remarks>
public enum TestDatabaseProvider
{
    /// <summary>
    /// Nothing was selected. Provisioning refuses with a diagnosis naming both routes rather than guessing.
    /// </summary>
    None = 0,

    /// <summary>
    /// An already-running server, named by <see cref="TestDatabaseFactory.ServerConnectionEnvironmentVariable"/>.
    /// The suite creates and drops its own uniquely named database on it. This is the default route and it
    /// requires no container runtime.
    /// </summary>
    ConfiguredServer = 1,

    /// <summary>
    /// A throwaway container, started only when
    /// <see cref="TestDatabaseFactory.ContainerOptInEnvironmentVariable"/> asks for it. Never selected
    /// implicitly.
    /// </summary>
    Container = 2,
}
