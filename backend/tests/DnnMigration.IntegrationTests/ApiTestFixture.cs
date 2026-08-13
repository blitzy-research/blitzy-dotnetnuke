using System.Globalization;
using System.Net.Http.Json;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using DnnMigration.Application.Serialization;
using DnnMigration.Domain.Abstractions.Repositories;
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
/// connection string and <c>AddJwtBearerAuthentication(configuration)</c> resolves the signing secret, both
/// before the host is built.
/// </para>
/// </remarks>
public sealed class ApiTestFixture : WebApplicationFactory<Program>, IAsyncLifetime
{
    /// <summary>Host name the in-memory client sends, and therefore the alias the seed has to register.</summary>
    public const string TestHost = "localhost";

    /// <summary>
    /// Signing secret for the test host. Well over the thirty-two byte minimum the API enforces at
    /// start-up, and deliberately visible: it signs tokens for an in-process host that never listens on a
    /// socket, so there is nothing here for a reader to mistake for a deployment secret.
    /// </summary>
    public const string SigningSecret = "integration-test-signing-key-0123456789-not-a-secret";

    /// <summary>
    /// Synthetic Triple-DES key used only to generate and verify integration-test legacy ciphertext.
    /// </summary>
    public const string LegacyCredentialDecryptionKey =
        "00112233445566778899AABBCCDDEEFF1021324354657687";

    /// <summary>Issuer the test host both mints and validates.</summary>
    public const string Issuer = "DnnMigration.Tests";

    /// <summary>Audience the test host both mints and validates.</summary>
    public const string Audience = "DnnMigration.Tests";

    /// <summary>Origin permitted by the cross-origin policy under test.</summary>
    public const string AllowedOrigin = "http://localhost:4200";

    /// <summary>Header carrying the correlation identifier, on the way in and on the way back out.</summary>
    /// <remarks>
    /// One spelling, declared once.
    /// </remarks>
    public const string CorrelationIdHeader = "X-Correlation-Id";

    /// <summary>
    /// Password of every seeded account. It satisfies the legacy policy carried forward verbatim - minimum
    /// length seven, no non-alphanumeric requirement - with room to spare.
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
    /// 429 depending on execution order.
    /// </summary>
    private const string PermissiveAuthenticationRateLimit = "10000";

    private TestDatabaseFactory? _database;
    private IntegrationSeed? _seed;

    /// <summary>Holds the process environment values this run overwrote, so disposal can put them back.</summary>
    private EnvironmentScope? _environment;

    /// <summary>
    /// Initialises a new instance of the <see cref="ApiTestFixture"/> class and fixes the client behaviour
    /// every suite depends on.
    /// </summary>
    /// <remarks>
    /// <strong>The base address is bound to the seeded alias.</strong> The alias-resolution middleware
    /// reads the tenant from the request's host name, and the test server takes that host name from the
    /// client's base address rather than from a socket. Deriving it from <see cref="TestHost"/> - the same
    /// constant the seed registers as a portal alias - makes the two impossible to drift apart.
    /// </remarks>
    public ApiTestFixture()
    {
        ClientOptions.AllowAutoRedirect = false;
        ClientOptions.BaseAddress = new Uri($"http://{TestHost}/", UriKind.Absolute);
    }

    /// <summary>
    /// Serialiser settings matching what the API is configured with: web naming, and enums as strings.
    /// </summary>
    public static JsonSerializerOptions Json { get; } = BuildJsonOptions();

    /// <summary>Builds the serialiser options the test clients speak the API with.</summary>
    /// <returns>The options.</returns>
    /// <remarks>
    /// <strong>Nothing is ever omitted from a payload, and that is a rule rather than a default.</strong>
    /// The ignore condition is stated explicitly even though <see cref="JsonSerializerDefaults.Web"/>
    /// already leaves it at <see cref="JsonIgnoreCondition.Never"/>, because the two alternatives - the
    /// conditions that drop a null on write and that drop a default on write - are precisely the ones this
    /// suite exists to keep out. the legacy sentinel table represents "absent" as the empty string for text
    /// and as -1 for integers, and both are real values here: <c>Portals.PortalID</c> is <c>IDENTITY(-1,
    /// 1)</c> so -1 identifies the first portal, <c>Roles.RoleID</c> and <c>Tabs.TabID</c> are
    /// <c>IDENTITY(0, 1)</c> so zero identifies the first row, and <c>Portals.HostFee</c> holds a fee as
    /// text whose seeded value is the empty string.
    /// </remarks>
    private static JsonSerializerOptions BuildJsonOptions()
    {
        JsonSerializerOptions options = new(JsonSerializerDefaults.Web)
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        };

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
    /// Signs in as the seeded host account and returns a client presenting the token the API issued: a
    /// super user, and therefore a caller the permission evaluator short-circuits to "holds everything".
    /// </summary>
    /// <param name="alias">
    /// The host name the returned client ADDRESSES, or <see langword="null"/> to address the seeded alias.
    /// </param>
    /// <param name="cancellationToken">Abandons the sign-in when the test is cancelled.</param>
    /// <returns>An authenticated client.</returns>
    /// <exception cref="ArgumentException"><paramref name="alias"/> is supplied and blank.</exception>
    /// <exception cref="InvalidOperationException">The endpoint refused the seeded credential.</exception>
    /// <remarks>
    /// The credential is presented to <c>POST /api/v1/auth/login</c>, so the token this client carries is
    /// the one production issued for it: the sign-in controller ran, the credential was verified against
    /// the external membership store, and the claim set was composed by the production token service from
    /// the account's STORED superuser flag, role assignments and permission grants.
    /// </remarks>
    public async Task<HttpClient> CreateHostClientAsync(
        string? alias = null,
        CancellationToken cancellationToken = default)
    {
        RequireAddressableAlias(alias);

        HttpClient client = await AuthenticatedClientFactory
            .CreateHostClientAsync(this, cancellationToken)
            .ConfigureAwait(false);

        return AddressedAt(client, alias);
    }

    /// <summary>
    /// Signs in as the seeded portal administrator and returns a client presenting the token the API
    /// issued: not a super user, but the account the portal-administrator policy admits.
    /// </summary>
    /// <param name="alias">
    /// The host name the returned client ADDRESSES, or <see langword="null"/> to address the seeded alias.
    /// </param>
    /// <param name="cancellationToken">Abandons the sign-in when the test is cancelled.</param>
    /// <returns>An authenticated client.</returns>
    /// <exception cref="ArgumentException"><paramref name="alias"/> is supplied and blank.</exception>
    /// <exception cref="InvalidOperationException">The endpoint refused the seeded credential.</exception>
    /// <remarks>
    /// The policy resolves role membership from the database on every request rather than from the token,
    /// so the distinction between this persona and the host account is a property of the STORE and not of
    /// the claims presented - which is precisely why the token has to come from the sign-in endpoint for
    /// the distinction to be worth asserting.
    /// </remarks>
    public async Task<HttpClient> CreateAdministratorClientAsync(
        string? alias = null,
        CancellationToken cancellationToken = default)
    {
        RequireAddressableAlias(alias);

        HttpClient client = await AuthenticatedClientFactory
            .CreateAdministratorClientAsync(this, cancellationToken)
            .ConfigureAwait(false);

        return AddressedAt(client, alias);
    }

    /// <summary>
    /// Signs in as the seeded ordinary member and returns a client presenting the token the API issued: an
    /// authenticated caller holding no administrative entitlement.
    /// </summary>
    /// <param name="cancellationToken">Abandons the sign-in when the test is cancelled.</param>
    /// <returns>An authenticated but unprivileged client.</returns>
    /// <exception cref="InvalidOperationException">The endpoint refused the seeded credential.</exception>
    /// <remarks>
    /// The refusal such a caller receives is 403 and never 401: it is authenticated, so a 401 would report
    /// a broken token rather than an enforced policy, and a test that accepted either would pass for the
    /// wrong reason.
    /// </remarks>
    public Task<HttpClient> CreateUnprivilegedClientAsync(CancellationToken cancellationToken = default) =>
        AuthenticatedClientFactory.CreateUnprivilegedClientAsync(this, cancellationToken);

    /// <summary>
    /// Signs in as a named tenant's OWN administrator, addressing that tenant, and returns a client
    /// presenting the token the API issued.
    /// </summary>
    /// <param name="alias">The host name bound to the tenant, which is what resolves it.</param>
    /// <param name="portalId">The tenant identifier, named in the sign-in query string.</param>
    /// <param name="administratorUserName">That administrator's account name.</param>
    /// <param name="addressedAt">
    /// The host the returned client addresses, or <see langword="null"/> to keep addressing <paramref
    /// name="alias"/>.
    /// </param>
    /// <param name="cancellationToken">Abandons the sign-in when the test is cancelled.</param>
    /// <returns>An authenticated client addressed at the named tenant.</returns>
    /// <exception cref="ArgumentException"><paramref name="alias"/> or the account name is blank.</exception>
    /// <exception cref="InvalidOperationException">The endpoint refused the credential.</exception>
    /// <remarks>
    /// Necessary because the portal-administrator policy binds a route's tenant to the tenant the REQUEST
    /// RESOLVED TO, and resolution is by host name. A client created by <see
    /// cref="CreateAdministratorClientAsync"/> addresses the seeded host, so it resolves to the seeded
    /// tenant and can only act on the seeded tenant's routes - which is the whole point of the binding.
    /// </remarks>
    public async Task<HttpClient> CreateTenantClientAsync(
        string alias,
        int portalId,
        string administratorUserName,
        string? addressedAt = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(alias);
        ArgumentException.ThrowIfNullOrWhiteSpace(administratorUserName);
        RequireAddressableAlias(addressedAt);

        HttpClient client = await AuthenticatedClientFactory.CreateAuthenticatedClientAsync(
                this,
                administratorUserName,
                KnownPassword,
                portalId,
                alias,
                cancellationToken)
            .ConfigureAwait(false);

        return AddressedAt(client, addressedAt);
    }

    /// <summary>Signs in as an arbitrary account and returns a client presenting the token the API issued.</summary>
    /// <param name="userName">The account name to present.</param>
    /// <param name="password">The credential to present.</param>
    /// <param name="cancellationToken">Abandons the sign-in when the test is cancelled.</param>
    /// <returns>An authenticated client.</returns>
    /// <exception cref="ArgumentException">Either argument is blank.</exception>
    /// <exception cref="InvalidOperationException">The endpoint refused the credential.</exception>
    /// <remarks>
    /// For an account a test created for itself, which is the only way to obtain a caller whose
    /// entitlements are neither the seed's nor a tenant administrator's. The credential is the one the test
    /// supplied when it created the account, so a sign-in here also proves the create path stored a
    /// verifiable credential - something no minted token could establish.
    /// </remarks>
    public Task<HttpClient> CreateClientForAsync(
        string userName,
        string password,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userName);
        ArgumentException.ThrowIfNullOrWhiteSpace(password);

        return AuthenticatedClientFactory.CreateAuthenticatedClientAsync(
            this,
            userName,
            password,
            cancellationToken);
    }

    /// <summary>Mints a bearer token in the test and attaches it, WITHOUT signing in.</summary>
    /// <param name="userId">The account identifier.</param>
    /// <param name="userName">The account name.</param>
    /// <param name="portalId">The tenant claim.</param>
    /// <param name="isSuperUser">Whether the caller claims to be a host account.</param>
    /// <param name="roles">Role names to carry.</param>
    /// <param name="permissions">Permission keys to carry.</param>
    /// <param name="signingSecret">The key to sign with; defaults to the host's own.</param>
    /// <param name="issuer">The issuer to state; defaults to the host's own.</param>
    /// <param name="audience">The audience to state; defaults to the host's own.</param>
    /// <param name="lifetime">How long the token is valid for; defaults to thirty minutes.</param>
    /// <param name="notBefore">When the token becomes valid; defaults to one minute ago.</param>
    /// <returns>A client presenting the minted token.</returns>
    /// <remarks>
    /// <strong>Negative material only.</strong> This bypasses the sign-in controller, credential
    /// verification, production token issuance and production claim construction, so a test that uses it to
    /// obtain an ORDINARY caller asserts nothing about any of them and keeps passing after all four break.
    /// </remarks>
    public HttpClient CreateClientWithMintedBearer(
        int userId,
        string userName,
        int portalId,
        bool isSuperUser = false,
        IEnumerable<string>? roles = null,
        IEnumerable<string>? permissions = null,
        string? signingSecret = null,
        string? issuer = null,
        string? audience = null,
        TimeSpan? lifetime = null,
        DateTime? notBefore = null)
    {
        string token = AuthenticatedClientFactory.CreateToken(
            signingSecret ?? SigningSecret,
            issuer ?? Issuer,
            audience ?? Audience,
            userId,
            userName,
            portalId,
            isSuperUser,
            roles,
            permissions,
            lifetime,
            notBefore);

        return AuthenticatedClientFactory.Authenticate(CreateClient(), token);
    }

    /// <summary>Rejects a blank alias before a sign-in is attempted for it.</summary>
    /// <param name="alias">The alias to check, which may be absent.</param>
    /// <exception cref="ArgumentException"><paramref name="alias"/> is supplied and blank.</exception>
    /// <remarks>
    /// Checked BEFORE the sign-in rather than at the point of use, so that a mistyped alias cannot leave an
    /// authenticated client undisposed while the argument failure propagates.
    /// </remarks>
    private static void RequireAddressableAlias(string? alias)
    {
        if (alias is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(alias);
        }
    }

    /// <summary>Points an authenticated client at a host name, when one was named.</summary>
    /// <param name="client">The client to address.</param>
    /// <param name="alias">The host name, or <see langword="null"/> to leave the seeded address in place.</param>
    /// <returns>The same client, so a call can be written inline.</returns>
    /// <remarks>
    /// The test server binds no socket, so the host name in the request line is whatever the base address
    /// says and the alias-resolution middleware reads it from there. Only the authority is significant:
    /// every request these suites send names an absolute path, so a path segment carried by a child
    /// portal's alias is stated by the request rather than inherited from here.
    /// </remarks>
    private static HttpClient AddressedAt(HttpClient client, string? alias)
    {
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
    /// Necessary because the portal-administrator policy binds a route's tenant to the tenant the REQUEST
    /// RESOLVED TO, and resolution is by host name. A client created by <see
    /// cref="CreateAdministratorClient"/> addresses the seeded host, so it resolves to the seeded tenant
    /// and can only act on the seeded tenant's routes - which is the whole point of the binding.
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
    /// <param name="userName">Compatibility input ignored by the minimized token factory.</param>
    /// <param name="portalId">The tenant claim.</param>
    /// <param name="isSuperUser">Compatibility input ignored by the minimized token factory.</param>
    /// <param name="roles">Compatibility input ignored by the minimized token factory.</param>
    /// <param name="permissions">Compatibility input ignored by the minimized token factory.</param>
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

    /// <summary>Produces a correlation identifier in the CANONICAL shape the pipeline keeps.</summary>
    /// <returns>Thirty-two hexadecimal characters, unique per call.</returns>
    /// <remarks>
    /// Every suite that asserts an identifier is echoed BACK has to send one the pipeline will keep, and
    /// the accepted shape is narrow by design: <c>Api/Middleware/CorrelationIdMiddleware.cs</c> accepts
    /// only 32 hexadecimal characters or the hyphenated 36-character UUID rendering, and replaces anything
    /// else with a generated value.
    /// </remarks>
    public static string NewCorrelationId() =>
        Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);

    /// <summary>Attaches a caller-supplied correlation identifier to a request and hands the request back.</summary>
    /// <param name="request">The request to stamp.</param>
    /// <param name="correlationId">The identifier to send.</param>
    /// <returns>The same request, so a call can be written inline at the send site.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="request"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="correlationId"/> is blank.</exception>
    public static HttpRequestMessage WithCorrelationId(HttpRequestMessage request, string correlationId)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(correlationId);

        request.Headers.Remove(CorrelationIdHeader);
        request.Headers.Add(CorrelationIdHeader, correlationId);

        return request;
    }

    /// <summary>Reads the correlation identifier a response carries.</summary>
    /// <param name="response">The response to read.</param>
    /// <returns>
    /// The identifier, or <see langword="null"/> when the response carried none - which is itself a
    /// contract failure, because every response is required to carry one.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="response"/> is <see langword="null"/>.</exception>
    public static string? ReadCorrelationId(HttpResponseMessage response)
    {
        ArgumentNullException.ThrowIfNull(response);

        return response.Headers.TryGetValues(CorrelationIdHeader, out IEnumerable<string>? values)
            ? values.FirstOrDefault()
            : null;
    }

    /// <summary>Opens a dependency-injection scope on the host's container.</summary>
    /// <returns>A scope the caller owns and must dispose.</returns>
    /// <remarks>
    /// Every repository, the unit of work and the tenant context are registered SCOPED, mirroring their
    /// per-request lifetime, so a test that reaches for one has to establish a scope first. This is the
    /// same container the request pipeline resolves from - nothing is substituted - which is what makes a
    /// resolution here evidence about production composition.
    /// </remarks>
    public IServiceScope CreateScope() => Services.CreateScope();

    /// <summary>
    /// Opens a dependency-injection scope and exposes the persistence contracts a suite reaches for.
    /// </summary>
    /// <returns>A scope handle the caller owns and must dispose.</returns>
    /// <remarks>
    /// A convenience over <see cref="CreateScope"/> that removes the resolve-by-hand step, and a guard rail
    /// with it: the accessors are typed, so a contract that was renamed or unregistered fails to compile or
    /// fails loudly on first touch rather than being resolved under a string.
    /// </remarks>
    public ScopedServices CreateScopedServices() => new(CreateScope());

    /// <summary>Configuration the test host runs with, keyed by environment-variable name.</summary>
    /// <returns>The environment overrides the host reads.</returns>
    public IReadOnlyDictionary<string, string?> HostConfiguration() => new Dictionary<string, string?>
    {
        ["ConnectionStrings__Default"] = Database.ConnectionString,
        ["Jwt__Secret"] = SigningSecret,
        ["Jwt__Issuer"] = Issuer,
        ["Jwt__Audience"] = Audience,
        ["Jwt__ExpirationMinutes"] = "30",
        ["Jwt__RefreshTokenExpirationDays"] = "7",

        // THE SINGLE-INSTANCE ACKNOWLEDGEMENT, AND IT IS TRUE OF THIS HOST. In Production the API refuses
        // to start on a refresh-token store that is neither shared between replicas nor carried across a
        // restart unless the deployment states that it runs one instance - because scaling out on the
        // process-local store needed no code change, no configuration change and produced no warning, while
        // a sign-out against one replica left the session exchangeable on every other.
        ["RefreshTokenStore__AcknowledgeSingleInstance"] = "true",
        ["LegacyCredentials__Enabled"] = "true",

        // The window's absolute deadline, which the enabled switch now requires.
        ["LegacyCredentials__EnabledUntilUtc"] =
            DateTimeOffset.UtcNow.AddDays(1).ToString("O", CultureInfo.InvariantCulture),
        ["LegacyCredentials__DecryptionKey"] = LegacyCredentialDecryptionKey,
        ["LegacyCredentials__DecryptionAlgorithm"] = "3DES",
        ["LegacyCredentials__ValidationAlgorithm"] = "SHA1",
        ["Cors__AllowedOrigins__0"] = AllowedOrigin,

        ["AllowedHosts"] = "*",
        ["RateLimiting__Authentication__PermitLimit"] = PermissiveAuthenticationRateLimit,
        ["RateLimiting__Authentication__WindowSeconds"] = "60",

        // The credential policy is carried forward from the legacy membership provider VERBATIM -
        // Website/release.config L237-L247 registers AspNetSqlMembershipProvider with
        // minRequiredPasswordLength="7", minRequiredNonalphanumericCharacters="0",
        // requiresQuestionAndAnswer="false" and requiresUniqueEmail="false".
        ["PasswordPolicy__MinRequiredPasswordLength"] = "7",
        ["PasswordPolicy__MinRequiredNonAlphanumericCharacters"] = "0",
        ["PasswordPolicy__RequiresQuestionAndAnswer"] = "false",
        ["PasswordPolicy__RequiresUniqueEmail"] = "false",
        ["Https__RedirectEnabled"] = "false",
        ["Serilog__MinimumLevel__Default"] = "Warning",

        // The suite is quiet by default, above, and this is the one category it must not lose.
        ["Serilog__MinimumLevel__Override__DnnMigration.Infrastructure.Services.LoggingAuditSink"] = "Information",

        // The second category the suite must not lose, and for the same reason.
        ["Serilog__MinimumLevel__Override__DnnMigration.Api.Middleware.RequestLoggingMiddleware"] = "Information",

        // The third category, and the reason it is here is a contract that MOVED. The health endpoints
        // publish four members and deliberately name no probe, because both views are anonymous and an
        // enumeration of this application's dependencies is reconnaissance rather than diagnostics.
        ["Serilog__MinimumLevel__Override__DnnMigration.Api.HealthChecks"] = "Debug",
    };

    /// <summary>
    /// Applies environment overrides and restores the previous values when the returned scope is disposed.
    /// </summary>
    /// <param name="overrides">The variables to set, or to clear when the value is <see langword="null"/>.</param>
    /// <returns>A scope that restores the previous values.</returns>
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

        builder.UseDefaultServiceProvider((_, options) =>
        {
            options.ValidateScopes = true;
            options.ValidateOnBuild = true;
        });

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

        _environment = new EnvironmentScope(HostConfiguration());

        // Touching Services builds the host, which runs the options validation registered with
        // ValidateOnStart. A missing or short signing secret therefore fails the fixture with the
        // configuration message rather than failing every test with a connection error.
        _ = Services;

        using (ScopedServices probe = CreateScopedServices())
        {
            _ = probe.Portals;
            _ = probe.Users;
            _ = probe.Roles;
            _ = probe.UnitOfWork;
        }
    }

    /// <summary>Shuts the host down, removes the database, and puts the process environment back.</summary>
    /// <returns>A task that completes when all three are released.</returns>
    /// <remarks>
    /// The ORDER is fixed and matters: the host is disposed first because it holds connections to the
    /// database, the database is released second, and the environment is restored last so that anything
    /// either disposal touches still reads the run's own configuration while it is shutting down.
    /// </remarks>
    async Task IAsyncLifetime.DisposeAsync()
    {
        List<Exception> failures = [];

        try
        {
            await base.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception failure)
        {
            failures.Add(failure);
        }

        if (_database is not null)
        {
            try
            {
                await _database.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception failure)
            {
                failures.Add(failure);
            }
        }

        try
        {
            _environment?.Dispose();
        }
        catch (Exception failure)
        {
            failures.Add(failure);
        }
        finally
        {
            _environment = null;
        }

        if (failures.Count == 1)
        {
            ExceptionDispatchInfo.Capture(failures[0]).Throw();
        }

        if (failures.Count > 1)
        {
            throw new AggregateException(
                "The integration fixture failed to release more than one resource. Each failure is preserved "
                + "below, because the later ones are often consequences of the first and discarding them would "
                + "hide the sequence.",
                failures);
        }
    }

    /// <summary>
    /// Inserts the reference data the suites build on: one portal with an alias matching the test host, the
    /// three stock roles, a host account and a portal administrator with working credentials, a module
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
        // the two role names to resolve, so they are stamped once the roles and the administrator exist
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

        // ENHANCED, not the plain form. The production hasher pre-hashes the credential with SHA-384 before
        // BCrypt so that a password longer than BCrypt's own 72-byte input window cannot be silently
        // truncated into an alias of a shorter one.
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
/// A dependency-injection scope on the host's container, with the persistence contracts a suite reaches for
/// exposed as typed members.
/// </summary>
public sealed class ScopedServices : IDisposable
{
    private readonly IServiceScope _scope;

    internal ScopedServices(IServiceScope scope) => _scope = scope;

    /// <summary>The scoped provider, for a contract this type exposes no member for.</summary>
    public IServiceProvider ServiceProvider => _scope.ServiceProvider;

    /// <summary>The portal repository.</summary>
    public IPortalRepository Portals => Resolve<IPortalRepository>();

    /// <summary>The user repository.</summary>
    public IUserRepository Users => Resolve<IUserRepository>();

    /// <summary>The role repository.</summary>
    public IRoleRepository Roles => Resolve<IRoleRepository>();

    /// <summary>The unit of work, which is the only way a write reaches the database.</summary>
    public IUnitOfWork UnitOfWork => Resolve<IUnitOfWork>();

    /// <summary>Resolves any registered contract from this scope.</summary>
    /// <typeparam name="T">The contract to resolve.</typeparam>
    /// <returns>The resolved service.</returns>
    /// <exception cref="InvalidOperationException">The contract is not registered.</exception>
    public T Resolve<T>()
        where T : notnull =>
        _scope.ServiceProvider.GetRequiredService<T>();

    /// <summary>Disposes the scope, and with it everything resolved from it.</summary>
    public void Dispose() => _scope.Dispose();
}

/// <summary>Identifiers and names of the rows <see cref="ApiTestFixture"/> seeds.</summary>
/// <remarks>
/// Identity values are read back from the database rather than assumed, because two of the seeded tables
/// have unusual identity seeds that make guessing wrong in opposite directions: <c>Portals.PortalID</c> is
/// <c>IDENTITY(-1, 1)</c>, so the first portal is -1 - the very value the legacy sentinel table used for
/// "absent" - and <c>Roles.RoleID</c> and <c>Tabs.TabID</c> are <c>IDENTITY(0, 1)</c>, so zero is a real
/// identifier.
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

/// <summary>Binds every integration suite to one shared <see cref="ApiTestFixture"/>.</summary>
/// <remarks>
/// <para>
/// Membership of a single collection is what makes the suites run one after another instead of side by
/// side. That is required rather than merely tidy: they share one database, and the sign-in rate limiter
/// partitions by caller address, which the in-memory transport makes identical for all of them.
/// </para>
/// <para>
/// <b>SERIALISATION IS NOT ISOLATION, AND EVERY MUTATION OF SHARED STATE MUST BE FAILURE-SAFE.</b> Running
/// one fact at a time removes races; it does nothing whatever about state a fact leaves behind.
/// </para>
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
/// <b>The paging facts live one level down, under <c>meta</c>.</b> Before the success envelope was adopted
/// this type mirrored the domain page directly, with the total and the coordinates as siblings of the
/// records.
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

/// <summary>The wire shape of the metadata companion that accompanies a paged response.</summary>
public sealed class ApiMetaEnvelope
{
    /// <summary>The total number of matching elements across every page.</summary>
    public int TotalCount { get; set; }

    /// <summary>The zero-based index of the page that was returned.</summary>
    public int PageIndex { get; set; }

    /// <summary>The size of the page that was returned.</summary>
    public int PageSize { get; set; }

    /// <summary>
    /// The number of pages the total divides into at this page size, or zero when there is nothing to page.
    /// </summary>
    public int TotalPages { get; set; }
}

/// <summary>The wire shape of a single-payload success response, as the suites read it back.</summary>
/// <typeparam name="T">The payload type.</typeparam>
public sealed class ApiEnvelope<T>
{
    /// <summary>The transported payload.</summary>
    public T Data { get; set; } = default!;

    /// <summary>The metadata companion, absent for a response that carries no page.</summary>
    public ApiMetaEnvelope? Meta { get; set; }
}

/// <summary>Reads a payload out of the shared success envelope.</summary>
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

/// <summary>One log event, reduced to the facts a contract assertion can be written against.</summary>
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
/// Static because the logger is built once for the host and the host is a collection fixture shared by the
/// whole assembly - there is one logging pipeline, so one sink is the honest shape. Events accumulate for
/// the life of the run, which is why a suite asserting on them locates its own event rather than assuming
/// it is the only one present.
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

/// <summary>The standard success envelope for a collection response, as the tests read it off the wire.</summary>
/// <typeparam name="T">The element type of the collection.</typeparam>
public sealed class CollectionEnvelope<T>
{
    /// <summary>The records the endpoint produced, never null and empty when nothing matched.</summary>
    public IReadOnlyList<T> Data { get; set; } = Array.Empty<T>();
}
