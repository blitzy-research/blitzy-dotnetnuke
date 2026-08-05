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
/// <strong>Where the server comes from.</strong> <see cref="SelectProvider"/> owns the whole decision and is
/// the only place it is made. It reads two environment variables and, when neither directs it, ONE property
/// of the host:
/// </para>
/// <list type="number">
///   <item><description>
///     <see cref="ContainerOptInEnvironmentVariable"/> set to a truthy value - start a throwaway SQL Server
///     container. An explicit instruction wins outright, including over a configured server, because it is
///     the only way to exercise the container route on a host that has one.
///   </description></item>
///   <item><description>
///     <see cref="ServerConnectionEnvironmentVariable"/> set - use that connection string as the
///     administrative connection and create a database on that server. This is the PREFERRED route: it is
///     the fastest, it needs no container runtime, and it is what a developer or a CI image with a database
///     already available should configure.
///   </description></item>
///   <item><description>
///     <see cref="ContainerOptInEnvironmentVariable"/> set to a falsey value with no server configured -
///     refuse. A written <c>0</c> is a veto, so an operator who turned the container route OFF does not get
///     one started by discovery.
///   </description></item>
///   <item><description>
///     Nothing configured either way, and a container runtime is reachable - start a throwaway container.
///     The route is DISCOVERED rather than assumed: <see cref="IsContainerRuntimeReachable"/> looks for a
///     socket or an endpoint variable and finds nothing on a host that has no runtime.
///   </description></item>
///   <item><description>
///     Nothing configured and no runtime reachable - refuse, with one diagnosis that reports what was
///     probed and how to supply a server.
///   </description></item>
/// </list>
/// <para>
/// <strong>Why discovery exists at all, given that a container is expensive.</strong> Because the acceptance
/// gates are executed verbatim - <c>dotnet test --configuration Release --filter "Category=Integration"</c>,
/// with no preparation - and an earlier revision refused that command outright unless one of the two
/// variables had already been exported. The requirement the harness exists to satisfy is that the suite runs
/// "with no live DotNetNuke database available", so a gate that fails closed on a clean runner does not meet
/// it however well the failure reads: anyone following the published instructions saw the whole suite red and
/// had no way to tell an unconfigured host from a broken migration. Discovery closes that without weakening
/// anything else, because it is reached only when nothing has been configured, it cannot fire on a host with
/// no runtime, and an explicit falsey opt-out still refuses.
/// </para>
/// <para>
/// A container is still never started by GUESSWORK - the distinction that matters, and the one the earlier
/// revision got wrong. That revision started a container whenever the server variable happened to be absent,
/// so a run on a host with no daemon spent minutes failing inside container plumbing and reported that
/// plumbing as the cause when the real cause was a missing variable. Here the runtime is confirmed reachable
/// first, the selected route is carried in <see cref="TestDatabaseProvider"/> so the failure text can say
/// whether it was asked for or discovered, and a host with neither route still gets the single actionable
/// message rather than a socket error repeated once per test.
/// </para>
/// <para>
/// <strong>Why there is no permissive fallback, and what that means for two approved packages.</strong>
/// Neither an in-memory nor a SQLite provider is substituted, at any point in the order above. The credential
/// paths this suite exercises answer "store unavailable" on any provider that is not SQL Server, because the
/// accounts live in external membership tables, and every persona in the assembly obtains its token by
/// signing in for real. A permissive provider would therefore report a pass while the behaviour under test
/// had stopped executing, which is strictly worse than refusing to run - and the four SQL-Server-only paths
/// listed above are not incidental to a handful of facts, they are what the personas, the raw-SQL query root
/// and the catalogue assertions are all built on.
/// </para>
/// <para>
/// MIGRATION: the plan approves <c>Microsoft.EntityFrameworkCore.InMemory</c> and
/// <c>Microsoft.EntityFrameworkCore.Sqlite</c> for this project and describes them as the route for a host
/// with no container runtime. Both references are therefore RETAINED in the project file - the approved
/// dependency set is not narrowed here - but no fixture selects either, and this file is the reason. What
/// closes the gap that capability was meant to close is discovery: the acceptance gates now run unprepared
/// wherever a database can be provisioned honestly, rather than depending on a provider that cannot execute
/// the credential behaviour the gates assert. Adopting either provider later means authoring a SQLite-dialect
/// schema, a substitute for the external membership store, and provider-aware expectations for the
/// catalogue-shape facts - a deliberate piece of work, not a switch. The disposition is recorded in
/// MIGRATION_NOTES.md.
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
    /// Environment variable that directs the container route explicitly: a truthy value asks for a throwaway
    /// SQL Server container and outranks a configured server, a falsey value refuses one and also vetoes
    /// discovering a runtime.
    /// </summary>
    /// <remarks>
    /// Read as THREE states rather than as a boolean - asked for, refused, or not mentioned - by
    /// <see cref="ReadContainerRouteRequest"/>. The distinction is load-bearing because an unmentioned value
    /// permits <see cref="SelectProvider"/> to discover a runtime, so a value of <c>0</c>, <c>false</c>,
    /// <c>no</c> or <c>off</c> has to mean "not even then". Anything else reads as consent, matching the
    /// ordinary truthy spellings an operator or a CI definition writes.
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
    /// Decides which route this run provisions its database through, from the environment and, only when the
    /// environment directs nothing, from whether a container runtime is reachable.
    /// </summary>
    /// <returns>
    /// The selected route, or <see cref="TestDatabaseProvider.None"/> when no route is available - which
    /// <see cref="CreateAsync"/> turns into one actionable failure rather than a guess.
    /// </returns>
    /// <remarks>
    /// <para>
    /// Public and side-effect-free so that the decision can be read - by a test, or by a developer reasoning
    /// about a run - without provisioning anything. The whole selection lives in this one member, which is what
    /// makes every claim about it checkable by reading one function rather than by starting a run.
    /// </para>
    /// <para>
    /// <b>The explicit container opt-in wins over everything,</b> because it is the most specific instruction:
    /// a configured server may well be ambient in a shell profile or a CI image, whereas nobody sets the
    /// container variable by accident. It is also the only way to exercise the container route at all on a host
    /// that has a server configured, and a route that cannot be exercised is a route that quietly rots.
    /// </para>
    /// <para>
    /// <b>A configured server beats discovery,</b> because it is the cheaper and more predictable route and
    /// because someone who exported that variable said where the database should live.
    /// </para>
    /// <para>
    /// <b>An explicit falsey opt-out is a veto, not merely an absence of consent.</b> Discovery is skipped
    /// entirely when the variable is present and reads as "no", so <c>DNN_TESTS_USE_MSSQL=0</c> genuinely turns
    /// the container route off instead of turning it into an implicit one. This is the whole reason the opt-in
    /// is read as three states rather than as a boolean.
    /// </para>
    /// <para>
    /// <b>Discovery is last, and it is a measurement rather than an assumption.</b> It fires only when nothing
    /// has been configured and only when a runtime is actually reachable, so an unconfigured run on a host with
    /// no runtime still refuses with the diagnosis instead of spending minutes failing inside container
    /// plumbing.
    /// </para>
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
    /// <remarks>
    /// Three states rather than a boolean, because "absent" and "explicitly off" must behave differently once
    /// an absent value permits discovery: collapsing them is precisely how a written <c>0</c> would stop
    /// meaning anything.
    /// </remarks>
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
    /// <remarks>
    /// The falsey spellings are recognised explicitly rather than treating any non-empty value as consent,
    /// because <c>DNN_TESTS_USE_MSSQL=0</c> is what an operator writes to turn the route OFF, and reading that
    /// as "on" would be the most confusing possible behaviour. Comparison is case-insensitive and trims, since
    /// these values arrive from shell profiles and CI definitions.
    /// </remarks>
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
    /// <para>
    /// Deliberately a CHEAP, SIDE-EFFECT-FREE probe rather than a handshake. <see cref="SelectProvider"/> is
    /// documented as answerable without provisioning anything, and it is called before any container work
    /// begins; opening a connection here would make reading the decision cost a round trip and would give the
    /// probe two ways to fail. Being approximate is acceptable because it is not the last word: when the
    /// runtime turns out to be unusable after all, <see cref="StartContainerAsync"/> still translates that into
    /// one actionable failure naming both supported routes.
    /// </para>
    /// <para>
    /// The endpoint variables are checked first and are the ones the container library itself honours, so a
    /// remote or rootless daemon reached over TCP is recognised even though no socket exists on this
    /// filesystem. The socket paths then cover the ordinary local cases: the Docker daemon's own socket, and
    /// the rootful and rootless Podman sockets, since Podman's Docker-compatible endpoint serves this library
    /// unchanged. <c>File.Exists</c> answers <see langword="true"/> for a unix domain socket on this runtime -
    /// verified by execution rather than assumed, because a probe that silently answered
    /// <see langword="false"/> for every socket would reintroduce exactly the defect this closes.
    /// </para>
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
    /// <remarks>
    /// One message, raised once, at the moment the decision was needed. The alternative - letting each of a
    /// thousand facts fail on its own connection error - reports the symptom several hundred times and the
    /// remedy never.
    /// </remarks>
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
    /// <remarks>
    /// Composed from the opt-in state rather than written as one fixed paragraph, because the two ways to
    /// reach <see cref="TestDatabaseProvider.None"/> have DIFFERENT remedies and different evidence behind
    /// them. When the variable refused the route, no runtime probe ran at all, so saying "no runtime could be
    /// found" would assert a measurement that was never taken - and would send the reader off to install a
    /// container runtime when clearing one variable is what they need. Diagnoses that overstate what was
    /// checked are how a clear message becomes a wrong one.
    /// </remarks>
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
    /// Starts the throwaway server this run selected, translating an unusable container runtime into a
    /// single actionable failure.
    /// </summary>
    /// <param name="provider">
    /// The route that led here, so the diagnosis can say whether the container was ASKED for or DISCOVERED.
    /// Reporting the wrong one sends the reader to the wrong remedy: an opted-in run needs its runtime fixed,
    /// whereas a discovered run can simply be pointed at a server instead.
    /// </param>
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
    private static async Task<MsSqlContainer> StartContainerAsync(
        TestDatabaseProvider provider,
        CancellationToken cancellationToken)
    {
        MsSqlContainer? container = null;

        try
        {
            // WithImage rather than a builder constructor taking the image: the pinned
            // Testcontainers.MsSql 3.10.0 exposes only the parameterless builder, and WithImage is the
            // supported way to replace the module's own default tag on that line. Overriding it is the
            // point - the module default is a moving tag, and ContainerImage explains at length why this
            // suite refuses one.
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
/// The two container members are distinguished by HOW the route was chosen rather than by what it does, because
/// they provision identically and differ only in what a failure should tell the reader to do about it. Folding
/// them into one member would cost nothing at provisioning time and would make every diagnosis on that path
/// either guess or stay silent about which remedy applies.
/// </para>
/// <para>
/// There is deliberately no member for an in-memory or SQLite provider. Both are named in the migration plan as
/// available, and both are unusable here for a reason that is a property of this application rather than of the
/// test harness: the accounts live in external ASP.NET membership tables and the credential store reports
/// itself unavailable on any provider that is not SQL Server. Adding a member for a route that cannot execute
/// the behaviour under test would be adding exactly the kind of unexercised infrastructure this suite is meant
/// to avoid. MIGRATION: the two package references remain, and TestDatabaseFactory records why.
/// </para>
/// </remarks>
public enum TestDatabaseProvider
{
    /// <summary>
    /// No route was available: nothing was configured and no container runtime could be found, or the container
    /// route was explicitly refused. Provisioning fails with one diagnosis naming both routes.
    /// </summary>
    None = 0,

    /// <summary>
    /// An already-running server, named by <see cref="TestDatabaseFactory.ServerConnectionEnvironmentVariable"/>.
    /// The suite creates and drops its own uniquely named database on it. This is the preferred route and it
    /// requires no container runtime.
    /// </summary>
    ConfiguredServer = 1,

    /// <summary>
    /// A throwaway container, asked for explicitly by
    /// <see cref="TestDatabaseFactory.ContainerOptInEnvironmentVariable"/>. This request outranks a configured
    /// server.
    /// </summary>
    Container = 2,

    /// <summary>
    /// A throwaway container selected because nothing was configured and a container runtime was found to be
    /// reachable. This is what lets the acceptance gates run on an unprepared host; it is never chosen when a
    /// server is configured, and never when the container route has been explicitly refused.
    /// </summary>
    DiscoveredContainer = 3,
}
