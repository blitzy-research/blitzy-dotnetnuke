using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using DnnMigration.Application.Serialization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Serilog.Core;
using Serilog.Events;
using Xunit;

namespace DnnMigration.IntegrationTests;

/// <summary>
/// Hosts the real API request pipeline in process against a freshly provisioned relational database, and
/// seeds the reference data every suite needs.
/// </summary>
/// <remarks>
/// <para>
/// <strong>One host and one database for the whole suite.</strong> This type is a collection fixture, so
/// the container, the database, the schema and the seed are paid for once per test run rather than once per
/// class. The collection also serialises the classes that share it, which matters because they share
/// database state and a rate-limit partition.
/// </para>
/// <para>
/// <strong>Why configuration is supplied as environment variables.</strong> <c>Program.cs</c> reads
/// configuration <em>while composing services</em> - <c>AddInfrastructure(configuration)</c> resolves the
/// connection string and <c>AddJwtBearerAuthentication(configuration)</c> resolves the signing secret,
/// both before the host is built. Anything contributed through
/// <c>IWebHostBuilder.ConfigureAppConfiguration</c>
/// is applied at build time and therefore arrives after those reads, which is exactly how the first attempt
/// at this fixture failed: the host reported no connection string even though one had been supplied.
/// <see cref="IWebHostBuilder.UseSetting"/> is no better placed, because host configuration is chained
/// <em>underneath</em> <c>appsettings.json</c> and the shipped file declares both keys as empty strings so
/// that a deployment is forced to supply them.
/// </para>
/// <para>
/// Environment variables are added after the JSON files by the default builder, so they win, and they are
/// also precisely how the deployed topology supplies these values -
/// <c>docker/docker-compose.yml</c> sets <c>ConnectionStrings__Default</c> and <c>Jwt__Secret</c> on the API
/// service. Configuring the test host the same way keeps the suite faithful to the deployment rather than
/// exercising a path only tests use.
/// </para>
/// <para>
/// <strong>Environment.</strong> The host runs as <c>Testing</c>, not <c>Development</c>. There is no
/// <c>appsettings.Testing.json</c>, so nothing but <c>appsettings.json</c> and the overrides below applies -
/// in particular the development-only signing key in <c>appsettings.Development.json</c> is never loaded,
/// and the test host's key is stated here where a reader can see it.
/// </para>
/// <para>
/// <strong>The tenant seed is not optional.</strong> <c>PortalAliasResolutionMiddleware</c> resolves the
/// tenant from the request host on every <c>/api</c> request, so the seed registers the in-memory client's
/// host as a portal alias. Without it every request would run with an unresolved tenant.
/// </para>
/// </remarks>
public sealed class ApiTestFixture : WebApplicationFactory<Program>, IAsyncLifetime
{
    /// <summary>
    /// Host name the in-memory client sends, and therefore the alias the seed has to register.
    /// </summary>
    public const string TestHost = "localhost";

    /// <summary>
    /// Signing secret for the test host. Well over the thirty-two byte minimum the API enforces at
    /// start-up, and deliberately visible: it signs tokens for an in-process host that never listens on a
    /// socket, so there is nothing here for a reader to mistake for a deployment secret.
    /// </summary>
    public const string SigningSecret = "integration-test-signing-key-0123456789-not-a-secret";

    /// <summary>Issuer the test host both mints and validates.</summary>
    public const string Issuer = "DnnMigration.Tests";

    /// <summary>Audience the test host both mints and validates.</summary>
    public const string Audience = "DnnMigration.Tests";

    /// <summary>Origin permitted by the cross-origin policy under test.</summary>
    public const string AllowedOrigin = "http://localhost:4200";

    /// <summary>
    /// Password of every seeded account. It satisfies the legacy policy carried forward verbatim -
    /// minimum length seven, no non-alphanumeric requirement - with room to spare.
    /// </summary>
    public const string KnownPassword = "Integr8tion!Pass";

    /// <summary>Work factor the production hasher uses, mirrored so a seeded hash verifies.</summary>
    private const int PasswordWorkFactor = 12;

    /// <summary>Application name the credential store partitions accounts by.</summary>
    private const string MembershipApplicationName = "DotNetNuke";

    /// <summary>
    /// Sentinel the membership schema stores for "this has never happened", measured from the legacy
    /// procedures as <c>CONVERT(datetime, '17540101', 112)</c>.
    /// </summary>
    private static readonly DateTime NeverRecorded = new(1754, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>
    /// Sign-in attempts the shared host permits before throttling. Deliberately far above anything the
    /// suite needs, because the limiter partitions by caller address and the in-memory transport gives
    /// every test the same address: a production-sized limit here would make an unrelated test fail with
    /// 429 depending on execution order. The throttle itself is asserted by <c>AuthApiTests</c> against a
    /// dedicated host configured with a small limit.
    /// </summary>
    private const string PermissiveAuthenticationRateLimit = "10000";

    private TestDatabaseFactory? _database;
    private IntegrationSeed? _seed;

    /// <summary>
    /// Serialiser settings matching what the API is configured with: web naming, and enums as strings.
    /// </summary>
    public static JsonSerializerOptions Json { get; } = BuildJsonOptions();

    /// <summary>
    /// Builds the serialiser options the test clients speak the API with.
    /// </summary>
    /// <returns>The options.</returns>
    /// <remarks>
    /// <para>
    /// This deliberately mirrors the policy the host registers, converter for converter, because a
    /// test client is a CONSUMER of the wire contract and a consumer that serialises by its own
    /// private rules proves nothing about the contract. Like the host, it registers the
    /// application's explicit per-type converters and NO blanket enumeration converter.
    /// </para>
    /// <para>
    /// MIGRATION: the specific converters exist because BillingFrequency carries the legacy char(1)
    /// codes that dbo.Roles.BillingFrequency and dbo.Roles.TrialFrequency store, so "M" and not
    /// "Month" is what travels. The enumerations without a legacy spelling - the portal
    /// registration and banner advertising modes and the module visibility - travel as the integer
    /// discriminators their columns store and their Angular models consume, so no converter claims
    /// them and none should.
    /// </para>
    /// </remarks>
    private static JsonSerializerOptions BuildJsonOptions()
    {
        JsonSerializerOptions options = new(JsonSerializerDefaults.Web);

        DnnJsonConverters.AddTo(options);

        return options;
    }

    /// <summary>The provisioned database.</summary>
    public TestDatabaseFactory Database =>
        _database ?? throw new InvalidOperationException("The fixture has not been initialised.");

    /// <summary>The seeded reference data.</summary>
    public IntegrationSeed Seed =>
        _seed ?? throw new InvalidOperationException("The fixture has not been initialised.");

    /// <summary>Creates a client with no credentials attached.</summary>
    /// <returns>An anonymous client.</returns>
    public HttpClient CreateAnonymousClient() => CreateClient();

    /// <summary>
    /// Creates a client authenticated as the seeded host account: a super user, and therefore a caller the
    /// permission evaluator short-circuits to "holds everything".
    /// </summary>
    /// <param name="alias">
    /// The host name to address, or <see langword="null"/> to address the seeded alias. Supplying one that is
    /// NOT configured is the point of the parameter: it reproduces the state an operator provisioning the
    /// first portal of an installation is in, where no tenant resolves at all, and no other member can put a
    /// host caller in that state.
    /// </param>
    /// <returns>An authenticated client.</returns>
    public HttpClient CreateHostClient(string? alias = null)
    {
        HttpClient client = CreateClientFor(
            Seed.HostUserId,
            IntegrationSeed.HostUserName,
            Seed.PortalId,
            isSuperUser: true,
            roles: [IntegrationSeed.AdministratorsRoleName]);

        if (alias is not null)
        {
            client.BaseAddress = new Uri($"http://{alias}", UriKind.Absolute);
        }

        return client;
    }

    /// <summary>
    /// Creates a client authenticated as the seeded portal administrator: not a super user, but a member of
    /// the Administrators role that the portal-administrator policy requires.
    /// </summary>
    /// <returns>An authenticated client.</returns>
    public HttpClient CreateAdministratorClient() => CreateClientFor(
        Seed.AdminUserId,
        IntegrationSeed.AdminUserName,
        Seed.PortalId,
        isSuperUser: false,
        roles: [IntegrationSeed.AdministratorsRoleName]);

    /// <summary>
    /// Creates a client whose requests RESOLVE TO a tenant other than the seeded one, authenticated as that
    /// tenant's own administrator.
    /// </summary>
    /// <param name="alias">The host name bound to the tenant, which is what resolves it.</param>
    /// <param name="portalId">The tenant identifier, carried on the token.</param>
    /// <param name="administratorUserId">The account identifier of that tenant's administrator.</param>
    /// <param name="administratorUserName">That administrator's account name.</param>
    /// <param name="isSuperUser">Whether the caller additionally carries installation-wide authority.</param>
    /// <returns>An authenticated client addressed at the named tenant.</returns>
    /// <remarks>
    /// <para>
    /// Necessary because the portal-administrator policy binds a route's tenant to the tenant the REQUEST
    /// RESOLVED TO, and resolution is by host name. A client created by
    /// <see cref="CreateAdministratorClient"/> addresses the seeded host, so it resolves to the seeded
    /// tenant and can only act on the seeded tenant's routes - which is the whole point of the binding. A
    /// test that needs to act on a tenant it has just created must therefore address that tenant, exactly
    /// as a real operator would.
    /// </para>
    /// <para>
    /// The base address is the only thing that changes: the test server binds no socket, so the host name in
    /// the request line is whatever the client's base address says, and the alias-resolution middleware reads
    /// it from there. The account identifier must be the created tenant's own administrator, because the
    /// policy handler reads role ASSIGNMENTS from the database rather than role names from the token.
    /// </para>
    /// </remarks>
    public HttpClient CreateTenantClient(
        string alias,
        int portalId,
        int administratorUserId,
        string administratorUserName,
        bool isSuperUser = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(alias);

        HttpClient client = CreateClientFor(
            administratorUserId,
            administratorUserName,
            portalId,
            isSuperUser,
            roles: [IntegrationSeed.AdministratorsRoleName]);

        client.BaseAddress = new Uri($"http://{alias}", UriKind.Absolute);

        return client;
    }

    /// <summary>Creates a client authenticated as an arbitrary caller.</summary>
    /// <param name="userId">The account identifier.</param>
    /// <param name="userName">The account name.</param>
    /// <param name="portalId">The tenant claim.</param>
    /// <param name="isSuperUser">Whether the caller is a host account.</param>
    /// <param name="roles">Role names to carry.</param>
    /// <param name="permissions">Permission keys to carry.</param>
    /// <returns>An authenticated client.</returns>
    public HttpClient CreateClientFor(
        int userId,
        string userName,
        int portalId,
        bool isSuperUser = false,
        IEnumerable<string>? roles = null,
        IEnumerable<string>? permissions = null)
    {
        string token = AuthenticatedClientFactory.CreateToken(
            SigningSecret,
            Issuer,
            Audience,
            userId,
            userName,
            portalId,
            isSuperUser,
            roles,
            permissions);

        return AuthenticatedClientFactory.Authenticate(CreateClient(), token);
    }

    /// <summary>
    /// Configuration the test host runs with, keyed by environment-variable name.
    /// </summary>
    /// <returns>The environment overrides the host reads.</returns>
    /// <remarks>
    /// The double underscore is the section separator the configuration provider understands, and the
    /// numeric tail on the origins entry is how an array element is addressed. Both spellings match
    /// <c>docker/docker-compose.yml</c>.
    /// </remarks>
    public IReadOnlyDictionary<string, string?> HostConfiguration() => new Dictionary<string, string?>
    {
        ["ConnectionStrings__Default"] = Database.ConnectionString,
        ["Jwt__Secret"] = SigningSecret,
        ["Jwt__Issuer"] = Issuer,
        ["Jwt__Audience"] = Audience,
        ["Jwt__ExpirationMinutes"] = "30",
        ["Jwt__RefreshTokenExpirationDays"] = "7",
        ["Cors__AllowedOrigins__0"] = AllowedOrigin,
        ["RateLimiting__Authentication__PermitLimit"] = PermissiveAuthenticationRateLimit,
        ["RateLimiting__Authentication__WindowSeconds"] = "60",
        // The key is Https:RedirectEnabled, read by ApplicationBuilderExtensions through the constant
        // HttpsRedirectionSectionName. Setting Security__EnableHttpsRedirection instead would be inert,
        // because nothing reads that key, and the suite would silently fall back to the shipped default.
        // It is stated explicitly because a 307 to an https authority the test server does not
        // listen on would fail every request for a reason unrelated to what is being tested.
        ["Https__RedirectEnabled"] = "false",
        ["Serilog__MinimumLevel__Default"] = "Warning",

        // The suite is quiet by default, above, and this is the one category it must not lose. The business
        // audit trail records an ACCEPTED sign-in and a completed tenant installation at the informational
        // level - correctly, because neither is a problem - so the default would discard exactly the two
        // events whose emission is under test, and the assertion would fail for a reason that has nothing to
        // do with the code it is asserting on. The override is scoped to that one source context rather than
        // raised globally, so nothing else becomes noisier.
        // The source context is the SURVIVING sink's own type. Naming a type that no longer exists would
        // leave the override silently inert, and the assertion would fail for the reason the override was
        // added to prevent.
        ["Serilog__MinimumLevel__Override__DnnMigration.Infrastructure.Services.LoggingAuditSink"] = "Information",
    };

    /// <summary>
    /// Applies environment overrides and restores the previous values when the returned scope is disposed.
    /// </summary>
    /// <param name="overrides">The variables to set, or to clear when the value is <see langword="null"/>.</param>
    /// <returns>A scope that restores the previous values.</returns>
    /// <remarks>
    /// A host reads configuration as it is composed, so an override only takes effect on a host built while
    /// the scope is open. A caller that wants a differently configured host must therefore force that host
    /// to build inside the scope, which touching its service provider does.
    /// </remarks>
    public static IDisposable OverrideEnvironment(IReadOnlyDictionary<string, string?> overrides)
    {
        ArgumentNullException.ThrowIfNull(overrides);

        return new EnvironmentScope(overrides);
    }

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// The recording sink is added so that the suite can assert what the application EMITS and not merely
    /// that it does not throw while emitting. The business audit events are why it exists: their whole value
    /// is in the properties they carry, and a message template whose placeholders had drifted apart from its
    /// arguments would render nonsense while still failing nothing. The sink is inert for every other suite -
    /// it records and does nothing else.
    /// </para>
    /// <para>
    /// It is registered as a SERILOG sink rather than as a framework logging provider, and that is forced
    /// rather than preferred: the host installs Serilog as its logger, which replaces the framework's
    /// provider list outright, so a registered provider would receive nothing at all. The composition root
    /// reads its logger configuration from the container as well as from configuration - explicitly, so that
    /// a sink can resolve a registered service - and this uses that same extensibility point. Nothing about
    /// production logging is altered to make the assertion possible.
    /// </para>
    /// </remarks>
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.UseEnvironment("Testing");

        builder.ConfigureServices(services =>
            services.AddSingleton<ILogEventSink>(RecordedLogs.Sink));
    }

    /// <summary>
    /// Provisions the database, seeds it, and then builds the host so that a configuration fault surfaces
    /// here rather than inside the first test.
    /// </summary>
    /// <returns>A task that completes when the fixture is ready.</returns>
    async Task IAsyncLifetime.InitializeAsync()
    {
        _database = await TestDatabaseFactory.CreateAsync().ConfigureAwait(false);
        _seed = await SeedAsync(_database).ConfigureAwait(false);

        // Applied for the remainder of the run rather than scoped, because every host this suite builds -
        // the shared one and any ad-hoc one - needs the same database and the same signing key.
        foreach (KeyValuePair<string, string?> setting in HostConfiguration())
        {
            Environment.SetEnvironmentVariable(setting.Key, setting.Value);
        }

        // Touching Services builds the host, which runs the options validation registered with
        // ValidateOnStart. A missing or short signing secret therefore fails the fixture with the
        // configuration message rather than failing every test with a connection error.
        _ = Services;
    }

    /// <summary>Shuts the host down and removes the database.</summary>
    /// <returns>A task that completes when both are released.</returns>
    async Task IAsyncLifetime.DisposeAsync()
    {
        await base.DisposeAsync().ConfigureAwait(false);

        if (_database is not null)
        {
            await _database.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Inserts the reference data the suites build on: one portal with an alias matching the test host,
    /// the three stock roles, a host account and a portal administrator with working credentials, a module
    /// definition to place instances of, two pages, and the permission catalogue rows the authorisation
    /// policies resolve against.
    /// </summary>
    /// <param name="database">The provisioned database.</param>
    /// <returns>The identifiers assigned by the database.</returns>
    private static async Task<IntegrationSeed> SeedAsync(TestDatabaseFactory database)
    {
        int portalId = await database.ScalarAsync<int>(
            """
            INSERT INTO [dbo].[Portals]
                ([PortalName], [FooterText], [UserRegistration], [BannerAdvertising], [Currency],
                 [HostFee], [HostSpace], [Description], [KeyWords], [GUID], [DefaultLanguage],
                 [TimezoneOffset], [HomeDirectory], [PageQuota], [UserQuota])
            VALUES
                (@name, N'Integration footer', 2, 0, 'USD',
                 N'0', 0, N'Portal seeded by the integration suite', N'integration', NEWID(), N'en-US',
                 -8, '', 0, 0);
            SELECT CAST(SCOPE_IDENTITY() AS int);
            """,
            new Dictionary<string, object?> { ["@name"] = IntegrationSeed.PortalName }).ConfigureAwait(false);

        int portalAliasId = await database.ScalarAsync<int>(
            """
            INSERT INTO [dbo].[PortalAlias] ([PortalID], [HTTPAlias]) VALUES (@portalId, @alias);
            SELECT CAST(SCOPE_IDENTITY() AS int);
            """,
            new Dictionary<string, object?> { ["@portalId"] = portalId, ["@alias"] = TestHost })
            .ConfigureAwait(false);

        int administratorRoleId = await InsertRoleAsync(
            database, portalId, IntegrationSeed.AdministratorsRoleName, "Portal Administrators", isPublic: false, autoAssignment: false)
            .ConfigureAwait(false);
        int registeredRoleId = await InsertRoleAsync(
            database, portalId, IntegrationSeed.RegisteredUsersRoleName, "Registered Users", isPublic: false, autoAssignment: true)
            .ConfigureAwait(false);
        int subscribersRoleId = await InsertRoleAsync(
            database, portalId, IntegrationSeed.SubscribersRoleName, "A public role for portal subscriptions", isPublic: true, autoAssignment: true)
            .ConfigureAwait(false);

        int hostUserId = await InsertUserAsync(
            database, IntegrationSeed.HostUserName, "Integration", "Host", "Integration Host", "host@integration.test", isSuperUser: true)
            .ConfigureAwait(false);
        int adminUserId = await InsertUserAsync(
            database, IntegrationSeed.AdminUserName, "Integration", "Administrator", "Integration Administrator", "admin@integration.test", isSuperUser: false)
            .ConfigureAwait(false);
        int memberUserId = await InsertUserAsync(
            database, IntegrationSeed.MemberUserName, "Integration", "Member", "Integration Member", "member@integration.test", isSuperUser: false)
            .ConfigureAwait(false);

        foreach (int userId in new[] { hostUserId, adminUserId, memberUserId })
        {
            await database.ExecuteAsync(
                """
                INSERT INTO [dbo].[UserPortals] ([UserId], [PortalId], [CreatedDate], [Authorised])
                VALUES (@userId, @portalId, SYSUTCDATETIME(), 1);
                """,
                new Dictionary<string, object?> { ["@userId"] = userId, ["@portalId"] = portalId })
                .ConfigureAwait(false);
        }

        await AssignRoleAsync(database, hostUserId, administratorRoleId).ConfigureAwait(false);
        await AssignRoleAsync(database, adminUserId, administratorRoleId).ConfigureAwait(false);
        await AssignRoleAsync(database, memberUserId, registeredRoleId).ConfigureAwait(false);

        // The tenant snapshot the alias middleware publishes requires all three of these to be present and
        // the two role names to resolve, so they are stamped once the roles and the administrator exist -
        // exactly the insert-then-update sequence the legacy portal creation performed.
        await database.ExecuteAsync(
            """
            UPDATE [dbo].[Portals]
               SET [AdministratorId] = @adminUserId,
                   [AdministratorRoleId] = @administratorRoleId,
                   [RegisteredRoleId] = @registeredRoleId
             WHERE [PortalID] = @portalId;
            """,
            new Dictionary<string, object?>
            {
                ["@adminUserId"] = adminUserId,
                ["@administratorRoleId"] = administratorRoleId,
                ["@registeredRoleId"] = registeredRoleId,
                ["@portalId"] = portalId,
            }).ConfigureAwait(false);

        // ENHANCED, not the plain form. The production hasher pre-hashes the credential
        // with SHA-384 before BCrypt so that a password longer than BCrypt's own 72-byte
        // input window cannot be silently truncated into an alias of a shorter one. A
        // plain hash seeded here would therefore never verify, and every login in this
        // suite would return 401 for a reason that has nothing to do with the code under
        // test. The pre-hash algorithm must stay in step with the hasher's.
        string passwordHash = BCrypt.Net.BCrypt.EnhancedHashPassword(
            KnownPassword,
            PasswordWorkFactor,
            BCrypt.Net.HashType.SHA384);
        foreach (string userName in new[]
                 {
                     IntegrationSeed.HostUserName,
                     IntegrationSeed.AdminUserName,
                     IntegrationSeed.MemberUserName,
                 })
        {
            await InsertCredentialAsync(database, userName, passwordHash).ConfigureAwait(false);
        }

        int desktopModuleId = await database.ScalarAsync<int>(
            """
            INSERT INTO [dbo].[DesktopModules]
                ([FriendlyName], [Description], [Version], [IsPremium], [IsAdmin],
                 [BusinessControllerClass], [FolderName], [ModuleName], [SupportedFeatures])
            VALUES
                (@friendlyName, N'Seeded desktop module', N'01.00.00', 0, 0,
                 NULL, N'Integration', @moduleName, 0);
            SELECT CAST(SCOPE_IDENTITY() AS int);
            """,
            new Dictionary<string, object?>
            {
                ["@friendlyName"] = IntegrationSeed.DesktopModuleFriendlyName,
                ["@moduleName"] = IntegrationSeed.DesktopModuleName,
            }).ConfigureAwait(false);

        int moduleDefinitionId = await database.ScalarAsync<int>(
            """
            INSERT INTO [dbo].[ModuleDefinitions] ([FriendlyName], [DesktopModuleID], [DefaultCacheTime])
            VALUES (@friendlyName, @desktopModuleId, 0);
            SELECT CAST(SCOPE_IDENTITY() AS int);
            """,
            new Dictionary<string, object?>
            {
                ["@friendlyName"] = IntegrationSeed.ModuleDefinitionFriendlyName,
                ["@desktopModuleId"] = desktopModuleId,
            }).ConfigureAwait(false);

        int moduleControlId = await database.ScalarAsync<int>(
            """
            INSERT INTO [dbo].[ModuleControls]
                ([ModuleDefID], [ControlKey], [ControlTitle], [ControlSrc], [ControlType], [ViewOrder],
                 [SupportsPartialRendering])
            VALUES (@moduleDefinitionId, NULL, N'View', N'Integration/View.ascx', 0, 0, 0);
            SELECT CAST(SCOPE_IDENTITY() AS int);
            """,
            new Dictionary<string, object?> { ["@moduleDefinitionId"] = moduleDefinitionId })
            .ConfigureAwait(false);

        int rootTabId = await InsertTabAsync(database, portalId, IntegrationSeed.RootTabName, parentId: null, level: 0, tabOrder: 2, tabPath: "//Home")
            .ConfigureAwait(false);
        int childTabId = await InsertTabAsync(database, portalId, IntegrationSeed.ChildTabName, parentId: rootTabId, level: 1, tabOrder: 4, tabPath: "//Home//Reports")
            .ConfigureAwait(false);

        int moduleViewPermissionId = await InsertPermissionAsync(
            database, moduleDefinitionId, IntegrationSeed.ModulePermissionCode, "VIEW", "View Module").ConfigureAwait(false);
        int moduleEditPermissionId = await InsertPermissionAsync(
            database, moduleDefinitionId, IntegrationSeed.ModulePermissionCode, "EDIT", "Edit Module").ConfigureAwait(false);
        int tabViewPermissionId = await InsertPermissionAsync(
            database, moduleDefinitionId, IntegrationSeed.TabPermissionCode, "VIEW", "View Page").ConfigureAwait(false);
        int tabEditPermissionId = await InsertPermissionAsync(
            database, moduleDefinitionId, IntegrationSeed.TabPermissionCode, "EDIT", "Edit Page").ConfigureAwait(false);

        return new IntegrationSeed
        {
            PortalId = portalId,
            PortalAliasId = portalAliasId,
            AdministratorRoleId = administratorRoleId,
            RegisteredRoleId = registeredRoleId,
            SubscribersRoleId = subscribersRoleId,
            HostUserId = hostUserId,
            AdminUserId = adminUserId,
            MemberUserId = memberUserId,
            DesktopModuleId = desktopModuleId,
            ModuleDefinitionId = moduleDefinitionId,
            ModuleControlId = moduleControlId,
            RootTabId = rootTabId,
            ChildTabId = childTabId,
            ModuleViewPermissionId = moduleViewPermissionId,
            ModuleEditPermissionId = moduleEditPermissionId,
            TabViewPermissionId = tabViewPermissionId,
            TabEditPermissionId = tabEditPermissionId,
        };
    }

    private static Task<int> InsertRoleAsync(
        TestDatabaseFactory database,
        int portalId,
        string roleName,
        string description,
        bool isPublic,
        bool autoAssignment) =>
        database.ScalarAsync<int>(
            """
            INSERT INTO [dbo].[Roles]
                ([PortalID], [RoleName], [Description], [ServiceFee], [BillingPeriod], [BillingFrequency],
                 [TrialFee], [TrialPeriod], [TrialFrequency], [IsPublic], [AutoAssignment])
            VALUES (@portalId, @roleName, @description, 0, 0, 'N', 0, 0, 'N', @isPublic, @autoAssignment);
            SELECT CAST(SCOPE_IDENTITY() AS int);
            """,
            new Dictionary<string, object?>
            {
                ["@portalId"] = portalId,
                ["@roleName"] = roleName,
                ["@description"] = description,
                ["@isPublic"] = isPublic,
                ["@autoAssignment"] = autoAssignment,
            });

    private static Task<int> InsertUserAsync(
        TestDatabaseFactory database,
        string userName,
        string firstName,
        string lastName,
        string displayName,
        string email,
        bool isSuperUser) =>
        database.ScalarAsync<int>(
            """
            INSERT INTO [dbo].[Users]
                ([Username], [FirstName], [LastName], [DisplayName], [Email], [IsSuperUser], [UpdatePassword])
            VALUES (@userName, @firstName, @lastName, @displayName, @email, @isSuperUser, 0);
            SELECT CAST(SCOPE_IDENTITY() AS int);
            """,
            new Dictionary<string, object?>
            {
                ["@userName"] = userName,
                ["@firstName"] = firstName,
                ["@lastName"] = lastName,
                ["@displayName"] = displayName,
                ["@email"] = email,
                ["@isSuperUser"] = isSuperUser,
            });

    private static Task AssignRoleAsync(TestDatabaseFactory database, int userId, int roleId) =>
        database.ExecuteAsync(
            """
            INSERT INTO [dbo].[UserRoles] ([UserID], [RoleID], [EffectiveDate], [ExpiryDate], [IsTrialUsed])
            VALUES (@userId, @roleId, NULL, NULL, 0);
            """,
            new Dictionary<string, object?> { ["@userId"] = userId, ["@roleId"] = roleId });

    private static Task<int> InsertTabAsync(
        TestDatabaseFactory database,
        int portalId,
        string tabName,
        int? parentId,
        int level,
        int tabOrder,
        string tabPath) =>
        database.ScalarAsync<int>(
            """
            INSERT INTO [dbo].[Tabs]
                ([TabOrder], [PortalID], [TabName], [IsVisible], [ParentId], [Level], [DisableLink],
                 [Title], [IsDeleted], [TabPath], [IsSecure])
            VALUES (@tabOrder, @portalId, @tabName, 1, @parentId, @level, 0, @tabName, 0, @tabPath, 0);
            SELECT CAST(SCOPE_IDENTITY() AS int);
            """,
            new Dictionary<string, object?>
            {
                ["@tabOrder"] = tabOrder,
                ["@portalId"] = portalId,
                ["@tabName"] = tabName,
                ["@parentId"] = parentId,
                ["@level"] = level,
                ["@tabPath"] = tabPath,
            });

    private static Task<int> InsertPermissionAsync(
        TestDatabaseFactory database,
        int moduleDefinitionId,
        string permissionCode,
        string permissionKey,
        string permissionName) =>
        database.ScalarAsync<int>(
            """
            INSERT INTO [dbo].[Permission] ([PermissionCode], [ModuleDefID], [PermissionKey], [PermissionName])
            VALUES (@permissionCode, @moduleDefinitionId, @permissionKey, @permissionName);
            SELECT CAST(SCOPE_IDENTITY() AS int);
            """,
            new Dictionary<string, object?>
            {
                ["@permissionCode"] = permissionCode,
                ["@moduleDefinitionId"] = moduleDefinitionId,
                ["@permissionKey"] = permissionKey,
                ["@permissionName"] = permissionName,
            });

    /// <summary>
    /// Writes an account into the external membership store with a hash the production hasher verifies.
    /// </summary>
    /// <param name="database">The provisioned database.</param>
    /// <param name="userName">The DotNetNuke account name.</param>
    /// <param name="passwordHash">A BCrypt hash of <see cref="KnownPassword"/>.</param>
    /// <returns>A task that completes when the account exists.</returns>
    /// <remarks>
    /// The format discriminator is written as one, the value the store uses for a one-way hash, and the
    /// salt column is written empty because BCrypt carries its salt inside the hash. Both match what
    /// <c>MembershipStore.CreateAsync</c> writes, so a seeded account is indistinguishable from a created
    /// one.
    /// </remarks>
    private static Task InsertCredentialAsync(
        TestDatabaseFactory database,
        string userName,
        string passwordHash) =>
        database.ExecuteAsync(
            """
            DECLARE @applicationId uniqueidentifier;
            DECLARE @membershipUserId uniqueidentifier = NEWID();

            SELECT @applicationId = [ApplicationId]
            FROM [dbo].[aspnet_Applications]
            WHERE [LoweredApplicationName] = @loweredApplication;

            IF @applicationId IS NULL
            BEGIN
                SET @applicationId = NEWID();
                INSERT INTO [dbo].[aspnet_Applications]
                    ([ApplicationName], [LoweredApplicationName], [ApplicationId], [Description])
                VALUES (@application, @loweredApplication, @applicationId, NULL);
            END

            INSERT INTO [dbo].[aspnet_Users]
                ([ApplicationId], [UserId], [UserName], [LoweredUserName], [MobileAlias], [IsAnonymous],
                 [LastActivityDate])
            VALUES (@applicationId, @membershipUserId, @userName, @loweredUserName, NULL, 0, @never);

            INSERT INTO [dbo].[aspnet_Membership]
                ([ApplicationId], [UserId], [Password], [PasswordFormat], [PasswordSalt], [MobilePIN],
                 [Email], [LoweredEmail], [PasswordQuestion], [PasswordAnswer], [IsApproved], [IsLockedOut],
                 [CreateDate], [LastLoginDate], [LastPasswordChangedDate], [LastLockoutDate],
                 [FailedPasswordAttemptCount], [FailedPasswordAttemptWindowStart],
                 [FailedPasswordAnswerAttemptCount], [FailedPasswordAnswerAttemptWindowStart], [Comment])
            VALUES (@applicationId, @membershipUserId, @passwordHash, 1, N'', NULL,
                    NULL, NULL, NULL, NULL, 1, 0,
                    SYSUTCDATETIME(), @never, SYSUTCDATETIME(), @never,
                    0, @never,
                    0, @never, NULL);
            """,
            new Dictionary<string, object?>
            {
                ["@application"] = MembershipApplicationName,
                ["@loweredApplication"] = MembershipApplicationName.ToLowerInvariant(),
                ["@userName"] = userName,
                ["@loweredUserName"] = userName.ToLowerInvariant(),
                ["@passwordHash"] = passwordHash,
                ["@never"] = NeverRecorded,
            });

    /// <summary>Formats an integer for a route segment or query value.</summary>
    /// <param name="value">The value to format.</param>
    /// <returns>The invariant decimal representation.</returns>
    public static string Route(int value) => value.ToString(CultureInfo.InvariantCulture);

    /// <summary>Restores environment variables to their previous values on disposal.</summary>
    private sealed class EnvironmentScope : IDisposable
    {
        private readonly Dictionary<string, string?> _previous = [];

        internal EnvironmentScope(IReadOnlyDictionary<string, string?> overrides)
        {
            foreach (KeyValuePair<string, string?> setting in overrides)
            {
                _previous[setting.Key] = Environment.GetEnvironmentVariable(setting.Key);
                Environment.SetEnvironmentVariable(setting.Key, setting.Value);
            }
        }

        public void Dispose()
        {
            foreach (KeyValuePair<string, string?> setting in _previous)
            {
                Environment.SetEnvironmentVariable(setting.Key, setting.Value);
            }

            _previous.Clear();
        }
    }
}

/// <summary>
/// Identifiers and names of the rows <see cref="ApiTestFixture"/> seeds.
/// </summary>
/// <remarks>
/// Identity values are read back from the database rather than assumed, because two of the seeded tables
/// have unusual identity seeds that make guessing wrong in opposite directions: <c>Portals.PortalID</c> is
/// <c>IDENTITY(-1, 1)</c>, so the first portal is -1 - the very value the legacy sentinel table used for
/// "absent" - and <c>Roles.RoleID</c> and <c>Tabs.TabID</c> are <c>IDENTITY(0, 1)</c>, so zero is a real
/// identifier. Nothing here may treat -1 or 0 as missing.
/// </remarks>
public sealed record IntegrationSeed
{
    /// <summary>Name of the seeded portal.</summary>
    public const string PortalName = "Integration Portal";

    /// <summary>Stock role whose membership the portal-administrator policy requires.</summary>
    public const string AdministratorsRoleName = "Administrators";

    /// <summary>Stock role assigned to every registered member.</summary>
    public const string RegisteredUsersRoleName = "Registered Users";

    /// <summary>Stock public role.</summary>
    public const string SubscribersRoleName = "Subscribers";

    /// <summary>Account name of the seeded super user.</summary>
    public const string HostUserName = "integration_host";

    /// <summary>Account name of the seeded portal administrator.</summary>
    public const string AdminUserName = "integration_admin";

    /// <summary>Account name of the seeded ordinary member.</summary>
    public const string MemberUserName = "integration_member";

    /// <summary>Friendly name of the seeded desktop module.</summary>
    public const string DesktopModuleFriendlyName = "Integration Desktop Module";

    /// <summary>Unique module name of the seeded desktop module.</summary>
    public const string DesktopModuleName = "IntegrationDesktopModule";

    /// <summary>Friendly name of the seeded module definition.</summary>
    public const string ModuleDefinitionFriendlyName = "Integration Module Definition";

    /// <summary>Name of the seeded root page.</summary>
    public const string RootTabName = "Home";

    /// <summary>Name of the seeded child page.</summary>
    public const string ChildTabName = "Reports";

    /// <summary>Permission code the module permission catalogue rows carry.</summary>
    public const string ModulePermissionCode = "SYSTEM_MODULE_DEFINITION";

    /// <summary>Permission code the page permission catalogue rows carry.</summary>
    public const string TabPermissionCode = "SYSTEM_TAB";

    /// <summary>The seeded portal.</summary>
    public required int PortalId { get; init; }

    /// <summary>The alias row that maps the test host to the seeded portal.</summary>
    public required int PortalAliasId { get; init; }

    /// <summary>The seeded Administrators role.</summary>
    public required int AdministratorRoleId { get; init; }

    /// <summary>The seeded Registered Users role.</summary>
    public required int RegisteredRoleId { get; init; }

    /// <summary>The seeded Subscribers role.</summary>
    public required int SubscribersRoleId { get; init; }

    /// <summary>The seeded super user.</summary>
    public required int HostUserId { get; init; }

    /// <summary>The seeded portal administrator.</summary>
    public required int AdminUserId { get; init; }

    /// <summary>The seeded ordinary member.</summary>
    public required int MemberUserId { get; init; }

    /// <summary>The seeded desktop module.</summary>
    public required int DesktopModuleId { get; init; }

    /// <summary>The seeded module definition, which module instances are created from.</summary>
    public required int ModuleDefinitionId { get; init; }

    /// <summary>The seeded module control.</summary>
    public required int ModuleControlId { get; init; }

    /// <summary>The seeded root page.</summary>
    public required int RootTabId { get; init; }

    /// <summary>The seeded child page.</summary>
    public required int ChildTabId { get; init; }

    /// <summary>Catalogue row for the module view permission.</summary>
    public required int ModuleViewPermissionId { get; init; }

    /// <summary>Catalogue row for the module edit permission.</summary>
    public required int ModuleEditPermissionId { get; init; }

    /// <summary>Catalogue row for the page view permission.</summary>
    public required int TabViewPermissionId { get; init; }

    /// <summary>Catalogue row for the page edit permission.</summary>
    public required int TabEditPermissionId { get; init; }
}

/// <summary>
/// Binds every integration suite to one shared <see cref="ApiTestFixture"/>.
/// </summary>
/// <remarks>
/// Membership of a single collection is what makes the suites run one after another instead of side by
/// side. That is required rather than merely tidy: they share one database, and the sign-in rate limiter
/// partitions by caller address, which the in-memory transport makes identical for all of them.
/// </remarks>
[CollectionDefinition(Name)]
public sealed class IntegrationTestCollection : ICollectionFixture<ApiTestFixture>
{
    /// <summary>The collection name every integration suite declares.</summary>
    public const string Name = "DnnMigration integration";
}

/// <summary>
/// The wire shape of a paged response, as the suites read it back: an <c>items</c> array beside a
/// <c>meta</c> object.
/// </summary>
/// <remarks>
/// <para>
/// The API returns <c>DnnMigration.Application.Dtos.Common.PagedResponse&lt;T&gt;</c>, whose members are
/// initialise-only, so a response cannot be bound straight back onto the producing type. Declaring the
/// envelope here is also the more honest assertion: an integration suite is testing the contract that
/// leaves the process, so reading the payload into a type owned by the tests proves the documented members
/// really are on the wire under the expected names. Binding back onto the producing type would let a
/// renamed or dropped member pass unnoticed, because both sides would move together.
/// </para>
/// <para>
/// <b>The paging facts live one level down, under <c>meta</c>.</b> Before the success envelope was adopted
/// this type mirrored the domain page directly, with the total and the coordinates as siblings of the
/// records. The API now returns the Application-layer projection, which pairs the records with an
/// <c>ApiMeta</c> companion, so the shape asserted here has changed with it - and it had to, because
/// serialising the domain page was the layering breach that made the change necessary.
/// </para>
/// <para>
/// The three flat members are retained as read-only pass-throughs to that companion. They are not a second
/// source of truth: each simply forwards, so a suite reads <c>page.TotalCount</c> exactly as before while
/// the wire shape underneath it is the new one. Keeping them is what confined a wire-contract change to one
/// type instead of to every paging assertion in the suite.
/// </para>
/// <para>
/// The derived members of the producing type - the unpaged flag, the page count and the two navigation
/// flags - are deliberately absent. They are computed from these facts, so a suite that needs one computes
/// it from the same inputs the server did, and a suite that wants to prove the server's arithmetic asserts
/// on the raw JSON instead.
/// </para>
/// </remarks>
/// <typeparam name="T">The element type of the page.</typeparam>
public sealed class PagedEnvelope<T>
{
    /// <summary>The elements on the requested page.</summary>
    public IReadOnlyList<T> Items { get; set; } = Array.Empty<T>();

    /// <summary>The paging facts that accompany the elements.</summary>
    public ApiMetaEnvelope Meta { get; set; } = new();

    /// <summary>The total number of matching elements across every page.</summary>
    public int TotalCount => Meta.TotalCount;

    /// <summary>The zero-based index of the page that was returned.</summary>
    public int PageIndex => Meta.PageIndex;

    /// <summary>
    /// The size of the page that was returned. Note that the producing projection reports an unpaged
    /// response as a page size equal to the total rather than as zero, so a client that divides to derive a
    /// page count is never handed a zero divisor for a response that plainly holds records.
    /// </summary>
    public int PageSize => Meta.PageSize;
}

/// <summary>
/// The wire shape of the metadata companion that accompanies a paged response.
/// </summary>
/// <remarks>
/// Declared separately rather than inlined, because it is a distinct member of the published contract and a
/// suite that wants to assert on the companion itself - that it is present at all, for instance - needs a
/// type to read it into.
/// </remarks>
public sealed class ApiMetaEnvelope
{
    /// <summary>The total number of matching elements across every page.</summary>
    public int TotalCount { get; set; }

    /// <summary>The zero-based index of the page that was returned.</summary>
    public int PageIndex { get; set; }

    /// <summary>The size of the page that was returned.</summary>
    public int PageSize { get; set; }

    /// <summary>The number of pages the total divides into at this page size, or zero when there is nothing
    /// to page.</summary>
    public int TotalPages { get; set; }
}

/// <summary>
/// The wire shape of a single-payload success response, as the suites read it back.
/// </summary>
/// <remarks>
/// <para>
/// Every action that answers with a payload now wraps it in one envelope, so a suite reads two member names
/// rather than one shape per resource. Reading through this type is what proves the wrapping actually
/// happens: a payload published bare would fail to bind here, whereas asserting on the payload type alone
/// would pass either way.
/// </para>
/// <para>
/// The metadata companion is nullable because it describes a page. A single-payload response leaves it
/// absent, and a suite asserting that it is absent is asserting a real property of the contract.
/// </para>
/// </remarks>
/// <typeparam name="T">The payload type.</typeparam>
public sealed class ApiEnvelope<T>
{
    /// <summary>The transported payload.</summary>
    public T Data { get; set; } = default!;

    /// <summary>The metadata companion, absent for a response that carries no page.</summary>
    public ApiMetaEnvelope? Meta { get; set; }
}

/// <summary>
/// Reads a payload out of the shared success envelope.
/// </summary>
/// <remarks>
/// Exists so that a suite states its intent - "read the payload" - in one call rather than deserialising an
/// envelope and reaching into it at every site. The failure mode it removes is the quiet one: a suite that
/// forgot to unwrap would bind an all-default payload and then assert against it, and several assertions
/// would pass by coincidence.
/// </remarks>
public static class ApiEnvelopeReader
{
    /// <summary>Reads and unwraps the payload of a success response.</summary>
    /// <typeparam name="T">The payload type.</typeparam>
    /// <param name="content">The response content to read.</param>
    /// <returns>The payload, or <see langword="null"/> when the body carried none.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="content"/> is <see langword="null"/>.</exception>
    public static async Task<T?> ReadEnvelopeAsync<T>(this HttpContent content)
    {
        ArgumentNullException.ThrowIfNull(content);

        ApiEnvelope<T>? envelope = await content
            .ReadFromJsonAsync<ApiEnvelope<T>>(ApiTestFixture.Json)
            .ConfigureAwait(false);

        return envelope is null ? default : envelope.Data;
    }
}

/// <summary>
/// One log event, reduced to the facts a contract assertion can be written against.
/// </summary>
/// <param name="Level">The severity the event was written at.</param>
/// <param name="EventId">The identifier the event was written under, or zero when it carried none.</param>
/// <param name="EventName">The name the identifier carried, or an empty string when it carried none.</param>
/// <param name="Message">The rendered message, with every property substituted into its placeholder.</param>
/// <param name="Properties">The structured properties, by name, with scalars unwrapped.</param>
/// <param name="Exception">The attached exception, when the event carried one.</param>
public sealed record LogRecord(
    LogEventLevel Level,
    int EventId,
    string EventName,
    string Message,
    IReadOnlyDictionary<string, object?> Properties,
    Exception? Exception);

/// <summary>
/// Captures everything the host logs, so that a suite can assert what was emitted rather than only that
/// emitting did not fail.
/// </summary>
/// <remarks>
/// <para>
/// Static because the logger is built once for the host and the host is a collection fixture shared by the
/// whole assembly - there is one logging pipeline, so one sink is the honest shape. Events accumulate for
/// the life of the run, which is why a suite asserting on them locates its own event rather than assuming
/// it is the only one present.
/// </para>
/// <para>
/// The rendered message is kept alongside the properties deliberately. The properties prove the values were
/// attached; the rendered message proves the template's placeholders and its arguments still agree, which is
/// the failure the property bag alone would hide - a drifted template still carries every property and
/// simply renders them in the wrong places, or not at all.
/// </para>
/// </remarks>
public static class RecordedLogs
{
    private static readonly List<LogRecord> Records = [];

    /// <summary>Gets the sink the host's logger writes through.</summary>
    public static ILogEventSink Sink { get; } = new RecordingSink();

    /// <summary>Returns every event captured so far.</summary>
    /// <returns>A snapshot, safe to enumerate while the host keeps logging.</returns>
    public static IReadOnlyList<LogRecord> Snapshot()
    {
        lock (Records)
        {
            return Records.ToList();
        }
    }

    /// <summary>Adds one event to the sink.</summary>
    /// <param name="record">The event to add.</param>
    internal static void Add(LogRecord record)
    {
        lock (Records)
        {
            Records.Add(record);
        }
    }

    private sealed class RecordingSink : ILogEventSink
    {
        public void Emit(LogEvent logEvent)
        {
            ArgumentNullException.ThrowIfNull(logEvent);

            Dictionary<string, object?> properties = new(StringComparer.Ordinal);
            int eventId = 0;
            string eventName = string.Empty;

            foreach (KeyValuePair<string, LogEventPropertyValue> property in logEvent.Properties)
            {
                // The framework's event identifier arrives as a structure holding an identifier and a name,
                // because the logging adapter projects it that way rather than as two scalars. It is lifted
                // out here so an assertion can address the identifier directly, which is the value an
                // operator's alert rule is written against.
                if (property.Key == "EventId" && property.Value is StructureValue structure)
                {
                    foreach (LogEventProperty member in structure.Properties)
                    {
                        if (member.Name == "Id" && member.Value is ScalarValue { Value: int id })
                        {
                            eventId = id;
                        }
                        else if (member.Name == "Name" && member.Value is ScalarValue { Value: string name })
                        {
                            eventName = name;
                        }
                    }

                    continue;
                }

                properties[property.Key] = property.Value is ScalarValue scalar
                    ? scalar.Value
                    : property.Value.ToString();
            }

            Add(new LogRecord(
                logEvent.Level,
                eventId,
                eventName,
                logEvent.RenderMessage(CultureInfo.InvariantCulture),
                properties,
                logEvent.Exception));
        }
    }
}

/// <summary>
/// The standard success envelope for a collection response, as the tests read it off the wire.
/// </summary>
/// <remarks>
/// The unpaged counterpart of <see cref="PagedEnvelope{T}"/>. The reads that apply no window - bounded
/// reference catalogues and the per-parent collections - publish the Application layer's
/// <c>ApiResponse&lt;IReadOnlyList&lt;T&gt;&gt;</c>, which carries the records under <c>data</c> and no
/// metadata, because a response that was never paged has no page to describe.
/// </remarks>
/// <typeparam name="T">The element type of the collection.</typeparam>
public sealed class CollectionEnvelope<T>
{
    /// <summary>The records the endpoint produced, never null and empty when nothing matched.</summary>
    public IReadOnlyList<T> Data { get; set; } = Array.Empty<T>();
}
