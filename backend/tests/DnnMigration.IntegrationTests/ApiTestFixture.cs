using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using DnnMigration.Application.Serialization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
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
    /// <returns>An authenticated client.</returns>
    public HttpClient CreateHostClient() => CreateClientFor(
        Seed.HostUserId,
        IntegrationSeed.HostUserName,
        Seed.PortalId,
        isSuperUser: true,
        roles: [IntegrationSeed.AdministratorsRoleName]);

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
        // HttpsRedirectionSectionName. An earlier revision set Security__EnableHttpsRedirection, which
        // nothing reads, so the override was inert and the suite was relying on the shipped default
        // instead. It is stated explicitly because a 307 to an https authority the test server does not
        // listen on would fail every request for a reason unrelated to what is being tested.
        ["Https__RedirectEnabled"] = "false",
        ["Serilog__MinimumLevel__Default"] = "Warning",
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
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.UseEnvironment("Testing");
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
/// The wire shape of a paged response, as the suites read it back.
/// </summary>
/// <remarks>
/// <para>
/// The API returns <c>DnnMigration.Domain.Common.PagedResult&lt;T&gt;</c>, which the serialiser can write
/// but cannot read: its four properties are get-only and its only constructor is private, so there is no
/// member the deserialiser could populate and no constructor it could invoke. Attempting to bind a response
/// straight back onto that type therefore fails at run time rather than at compile time.
/// </para>
/// <para>
/// Declaring the envelope here is also the more honest assertion. An integration suite is testing the
/// contract that leaves the process, so reading the payload into a type owned by the tests proves the four
/// documented members really are on the wire under the expected names. Binding back onto the producing type
/// would let a renamed or dropped member pass unnoticed, because both sides would move together.
/// </para>
/// <para>
/// The three derived members - the unpaged flag, the page count and the two navigation flags - are
/// deliberately absent. They are computed from these four, so a suite that needs one computes it from the
/// same inputs the server did, and a suite that wants to prove the server's arithmetic asserts on the raw
/// JSON instead.
/// </para>
/// </remarks>
/// <typeparam name="T">The element type of the page.</typeparam>
public sealed class PagedEnvelope<T>
{
    /// <summary>The elements on the requested page.</summary>
    public IReadOnlyList<T> Items { get; set; } = Array.Empty<T>();

    /// <summary>The total number of matching elements across every page.</summary>
    public int TotalCount { get; set; }

    /// <summary>The zero-based index of the page that was returned.</summary>
    public int PageIndex { get; set; }

    /// <summary>The requested page size, where zero means the response was not paged.</summary>
    public int PageSize { get; set; }
}
