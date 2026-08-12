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

    /// <summary>
    /// Synthetic Triple-DES key used only to generate and verify integration-test legacy ciphertext.
    /// </summary>
    /// <remarks>
    /// This value is unrelated to every legacy deployment key. It is deliberately obvious test
    /// material and exists only inside the in-process host and throwaway database.
    /// </remarks>
    public const string LegacyCredentialDecryptionKey =
        "00112233445566778899AABBCCDDEEFF1021324354657687";

    /// <summary>Issuer the test host both mints and validates.</summary>
    public const string Issuer = "DnnMigration.Tests";

    /// <summary>Audience the test host both mints and validates.</summary>
    public const string Audience = "DnnMigration.Tests";

    /// <summary>Origin permitted by the cross-origin policy under test.</summary>
    public const string AllowedOrigin = "http://localhost:4200";

    /// <summary>
    /// Header carrying the correlation identifier, on the way in and on the way back out.
    /// </summary>
    /// <remarks>
    /// One spelling, declared once. The correlation middleware accepts a caller-supplied value and echoes
    /// whatever it settled on, so the same header name is both the request and the response half of the
    /// round trip, and a suite that misspelled either half would assert nothing while appearing to pass -
    /// a request header nothing reads is simply ignored, and <c>TryGetValues</c> on a misspelled response
    /// header just answers false.
    /// </remarks>
    public const string CorrelationIdHeader = "X-Correlation-Id";

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
    /// Holds the process environment values this run overwrote, so disposal can put them back.
    /// </summary>
    /// <remarks>
    /// The same type the ad-hoc-host helper hands out, used here for the run-wide overrides so that both the
    /// scoped and the run-wide case restore identically. Cleared on disposal so a second disposal cannot try to
    /// restore values it has already restored.
    /// </remarks>
    private EnvironmentScope? _environment;

    /// <summary>
    /// Initialises a new instance of the <see cref="ApiTestFixture"/> class and fixes the client behaviour
    /// every suite depends on.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Redirects are never followed.</strong> The default client follows up to seven of them, which
    /// would make a suite assert against the response at the END of a redirect chain while believing it was
    /// asserting against the response the endpoint returned. That is not hypothetical here: an unguarded
    /// <c>UseHttpsRedirection</c> answers 307 to every plain-HTTP request, the test server listens on no
    /// socket and so cannot serve the https authority it points at, and the failure would name the wrong
    /// cause. Following is therefore disabled so that a 3xx is reported as a 3xx - and it costs nothing,
    /// because the redirect-free contract is what the suite is asserting and the <c>Location</c> header is
    /// only ever read off a 201, which no client auto-follows.
    /// </para>
    /// <para>
    /// <strong>The base address is bound to the seeded alias.</strong> The alias-resolution middleware reads
    /// the tenant from the request's host name, and the test server takes that host name from the client's
    /// base address rather than from a socket. Deriving it from <see cref="TestHost"/> - the same constant the
    /// seed registers as a portal alias - makes the two impossible to drift apart. It happens to equal the
    /// framework default, which is exactly why stating it matters: a silent default is not a guarantee.
    /// </para>
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
    /// <para>
    /// <strong>Nothing is ever omitted from a payload, and that is a rule rather than a default.</strong>
    /// The ignore condition is stated explicitly even though <see cref="JsonSerializerDefaults.Web"/>
    /// already leaves it at <see cref="JsonIgnoreCondition.Never"/>, because the two alternatives - the
    /// conditions that drop a null on write and that drop a default on write - are precisely the ones this
    /// suite exists to keep out. MIGRATION: the legacy sentinel table represents "absent" as the empty string for
    /// text and as -1 for integers, and both are real values here: <c>Portals.PortalID</c> is
    /// <c>IDENTITY(-1, 1)</c> so -1 identifies the first portal, <c>Roles.RoleID</c> and <c>Tabs.TabID</c>
    /// are <c>IDENTITY(0, 1)</c> so zero identifies the first row, and <c>Portals.HostFee</c> holds a fee as
    /// text whose seeded value is the empty string. Omitting defaults would erase all four from the wire and
    /// a test client that did so would silently stop asserting on them.
    /// </para>
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
    /// Signs in as the seeded host account and returns a client presenting the token the API issued: a super
    /// user, and therefore a caller the permission evaluator short-circuits to "holds everything".
    /// </summary>
    /// <param name="alias">
    /// The host name the returned client ADDRESSES, or <see langword="null"/> to address the seeded alias.
    /// Supplying one that is NOT configured is the point of the parameter: it reproduces the state an operator
    /// provisioning the first portal of an installation is in, where no tenant resolves at all, and no other
    /// member can put a host caller in that state.
    /// </param>
    /// <param name="cancellationToken">Abandons the sign-in when the test is cancelled.</param>
    /// <returns>An authenticated client. The caller owns it and must dispose it.</returns>
    /// <exception cref="ArgumentException"><paramref name="alias"/> is supplied and blank.</exception>
    /// <exception cref="InvalidOperationException">The endpoint refused the seeded credential.</exception>
    /// <remarks>
    /// <para>
    /// The credential is presented to <c>POST /api/v1/auth/login</c>, so the token this client carries is the
    /// one production issued for it: the sign-in controller ran, the credential was verified against the
    /// external membership store, and the claim set was composed by the production token service from the
    /// account's STORED superuser flag, role assignments and permission grants. A token minted by the test
    /// would assert none of those four stages and would keep passing after any of them broke.
    /// </para>
    /// <para>
    /// The credential is always presented AT THE SEEDED ALIAS, even when <paramref name="alias"/> names
    /// somewhere else. The parameter chooses the host a test's own requests address; it does not choose where
    /// the credential is presented, and keeping the two apart is what makes the token identical across every
    /// host the suite addresses - so a test measuring host resolution measures only that, rather than a token
    /// that also changed underneath it. Sign-ins are cached per persona, so the whole assembly pays for one.
    /// </para>
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
    /// Signs in as the seeded portal administrator and returns a client presenting the token the API issued:
    /// not a super user, but the account the portal-administrator policy admits.
    /// </summary>
    /// <param name="alias">
    /// The host name the returned client ADDRESSES, or <see langword="null"/> to address the seeded alias.
    /// Supplying one that differs from the seeded alias only in case, or that merely sits inside it, is how
    /// the tenant-resolution rules are measured without changing who the caller is.
    /// </param>
    /// <param name="cancellationToken">Abandons the sign-in when the test is cancelled.</param>
    /// <returns>An authenticated client. The caller owns it and must dispose it.</returns>
    /// <exception cref="ArgumentException"><paramref name="alias"/> is supplied and blank.</exception>
    /// <exception cref="InvalidOperationException">The endpoint refused the seeded credential.</exception>
    /// <remarks>
    /// The policy resolves role membership from the database on every request rather than from the token, so
    /// the distinction between this persona and the host account is a property of the STORE and not of the
    /// claims presented - which is precisely why the token has to come from the sign-in endpoint for the
    /// distinction to be worth asserting.
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
    /// <returns>An authenticated but unprivileged client. The caller owns it and must dispose it.</returns>
    /// <exception cref="InvalidOperationException">The endpoint refused the seeded credential.</exception>
    /// <remarks>
    /// The refusal such a caller receives is 403 and never 401: it is authenticated, so a 401 would report a
    /// broken token rather than an enforced policy, and a test that accepted either would pass for the wrong
    /// reason. The account holds the auto-assigned registered-users role and nothing else, which is what an
    /// ordinary signed-in visitor holds, so the same client also serves the handful of self-service routes a
    /// member may legitimately reach.
    /// </remarks>
    public Task<HttpClient> CreateUnprivilegedClientAsync(CancellationToken cancellationToken = default) =>
        AuthenticatedClientFactory.CreateUnprivilegedClientAsync(this, cancellationToken);

    /// <summary>
    /// Signs in as a named tenant's OWN administrator, addressing that tenant, and returns a client presenting
    /// the token the API issued.
    /// </summary>
    /// <param name="alias">
    /// The host name bound to the tenant, which is what resolves it. A child portal's alias carries a path
    /// segment after the authority, and the sign-in is composed beneath that segment so that the credential is
    /// presented to the tenant the segment names.
    /// </param>
    /// <param name="portalId">
    /// The tenant identifier, named in the sign-in query string. Read by the endpoint ONLY when the addressed
    /// host resolves no tenant - a resolved tenant always wins - so it makes a deliberately unresolvable host
    /// usable for sign-in without ever letting a credential be presented to a tenant the request is not
    /// addressing.
    /// </param>
    /// <param name="administratorUserName">That administrator's account name.</param>
    /// <param name="addressedAt">
    /// The host the returned client addresses, or <see langword="null"/> to keep addressing
    /// <paramref name="alias"/>. Supplied only where the two must genuinely differ: a route that names its own
    /// tenant is decided against the route and the token rather than against the addressed host, so a test
    /// measuring that binding needs a real foreign-tenant token presented at a DIFFERENT host - which is
    /// exactly the arrangement a cross-tenant escalation attempt has.
    /// </param>
    /// <param name="cancellationToken">Abandons the sign-in when the test is cancelled.</param>
    /// <returns>An authenticated client addressed at the named tenant. The caller owns it.</returns>
    /// <exception cref="ArgumentException"><paramref name="alias"/> or the account name is blank.</exception>
    /// <exception cref="InvalidOperationException">The endpoint refused the credential.</exception>
    /// <remarks>
    /// <para>
    /// Necessary because the portal-administrator policy binds a route's tenant to the tenant the REQUEST
    /// RESOLVED TO, and resolution is by host name. A client created by
    /// <see cref="CreateAdministratorClientAsync"/> addresses the seeded host, so it resolves to the seeded
    /// tenant and can only act on the seeded tenant's routes - which is the whole point of the binding. A
    /// test that needs to act on a tenant it has just created must therefore address that tenant, exactly as
    /// a real operator would.
    /// </para>
    /// <para>
    /// Unlike the seeded personas, the credential is presented AT THE TENANT'S OWN ALIAS rather than at the
    /// seeded one, because it has to be: the account belongs to that tenant, and the seeded alias resolves the
    /// seeded tenant, which the account is not a member of. Every tenant created by these suites is
    /// provisioned with <see cref="KnownPassword"/> as its administrator's credential, which is what makes a
    /// real sign-in possible here at all.
    /// </para>
    /// <para>
    /// The caller's account identifier and superuser flag are deliberately NOT parameters any more. Both are
    /// now facts the sign-in endpoint reads from the store while composing the token, so accepting them here
    /// would let a test state something the store contradicts.
    /// </para>
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

    /// <summary>
    /// Signs in as an arbitrary account and returns a client presenting the token the API issued.
    /// </summary>
    /// <param name="userName">The account name to present.</param>
    /// <param name="password">The credential to present.</param>
    /// <param name="cancellationToken">Abandons the sign-in when the test is cancelled.</param>
    /// <returns>An authenticated client. The caller owns it and must dispose it.</returns>
    /// <exception cref="ArgumentException">Either argument is blank.</exception>
    /// <exception cref="InvalidOperationException">The endpoint refused the credential.</exception>
    /// <remarks>
    /// For an account a test created for itself, which is the only way to obtain a caller whose entitlements
    /// are neither the seed's nor a tenant administrator's. The credential is the one the test supplied when
    /// it created the account, so a sign-in here also proves the create path stored a verifiable credential -
    /// something no minted token could establish.
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

    /// <summary>
    /// Mints a bearer token in the test and attaches it, WITHOUT signing in.
    /// </summary>
    /// <param name="userId">The account identifier.</param>
    /// <param name="userName">The account name.</param>
    /// <param name="portalId">The tenant claim.</param>
    /// <param name="isSuperUser">Whether the caller claims to be a host account.</param>
    /// <param name="roles">Role names to carry.</param>
    /// <param name="permissions">Permission keys to carry.</param>
    /// <param name="signingSecret">
    /// The key to sign with; defaults to the host's own. A different value produces a token the host cannot
    /// validate, which is the point of the parameter.
    /// </param>
    /// <param name="issuer">The issuer to state; defaults to the host's own.</param>
    /// <param name="audience">The audience to state; defaults to the host's own.</param>
    /// <param name="lifetime">
    /// How long the token is valid for; defaults to thirty minutes. A negative value, combined with a
    /// <paramref name="notBefore"/> in the past, produces an already-expired token.
    /// </param>
    /// <param name="notBefore">When the token becomes valid; defaults to one minute ago.</param>
    /// <returns>A client presenting the minted token. The caller owns it and must dispose it.</returns>
    /// <remarks>
    /// <para>
    /// <strong>Negative material only.</strong> This bypasses the sign-in controller, credential
    /// verification, production token issuance and production claim construction, so a test that uses it to
    /// obtain an ORDINARY caller asserts nothing about any of them and keeps passing after all four break.
    /// Every ordinary persona comes from <see cref="CreateHostClientAsync"/>,
    /// <see cref="CreateAdministratorClientAsync"/>, <see cref="CreateUnprivilegedClientAsync"/>,
    /// <see cref="CreateTenantClientAsync"/> or <see cref="CreateClientForAsync"/>, all of which present a
    /// credential to the real endpoint.
    /// </para>
    /// <para>
    /// What remains legitimate here is material the endpoint would never issue and whose REFUSAL is the
    /// behaviour under test: a token that has expired, one whose validity has not begun, one signed with a
    /// foreign key, one naming another issuer or audience, and one whose claim set is incomplete or
    /// malformed. Those cases live in <c>Api/BearerTokenValidationTests</c>, and this member exists for them.
    /// </para>
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
    /// says and the alias-resolution middleware reads it from there. Only the authority is significant: every
    /// request these suites send names an absolute path, so a path segment carried by a child portal's alias
    /// is stated by the request rather than inherited from here.
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

    /// <summary>
    /// Produces a correlation identifier in the CANONICAL shape the pipeline keeps.
    /// </summary>
    /// <returns>Thirty-two hexadecimal characters, unique per call.</returns>
    /// <remarks>
    /// <para>
    /// Every suite that asserts an identifier is echoed BACK has to send one the pipeline will keep, and
    /// the accepted shape is narrow by design: <c>Api/Middleware/CorrelationIdMiddleware.cs</c> accepts
    /// only 32 hexadecimal characters or the hyphenated 36-character UUID rendering, and replaces
    /// anything else with a generated value. That narrowness is a security control - it is what stops a
    /// caller from putting a password, a token or an e-mail address into this application's logs, headers
    /// and problem documents by way of a diagnostic header - so a suite must not widen it.
    /// </para>
    /// <para>
    /// Reaching for this helper rather than writing a readable label such as <c>"portal-suite-7"</c> is
    /// therefore not a style choice. A label of that shape is replaced by the pipeline, so a test sending
    /// one would assert the REPLACEMENT path while appearing to assert the echo path - passing for the
    /// wrong reason today and failing for an unrelated reason tomorrow.
    /// </para>
    /// </remarks>
    public static string NewCorrelationId() =>
        Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);

    /// <summary>
    /// Attaches a caller-supplied correlation identifier to a request and hands the request back.
    /// </summary>
    /// <param name="request">The request to stamp.</param>
    /// <param name="correlationId">The identifier to send.</param>
    /// <returns>The same request, so a call can be written inline at the send site.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="request"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="correlationId"/> is blank.</exception>
    /// <remarks>
    /// The header is added rather than set through the default headers of a client, because the round trip
    /// is a property of one request: a client-wide default would send the same identifier on every request a
    /// suite makes and an assertion that the response echoed it could then be satisfied by a value the test
    /// under examination never sent.
    /// </remarks>
    public static HttpRequestMessage WithCorrelationId(HttpRequestMessage request, string correlationId)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(correlationId);

        request.Headers.Remove(CorrelationIdHeader);
        request.Headers.Add(CorrelationIdHeader, correlationId);

        return request;
    }

    /// <summary>
    /// Reads the correlation identifier a response carries.
    /// </summary>
    /// <param name="response">The response to read.</param>
    /// <returns>
    /// The identifier, or <see langword="null"/> when the response carried none - which is itself a
    /// contract failure, because every response is required to carry one.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="response"/> is <see langword="null"/>.</exception>
    /// <remarks>
    /// Absence is returned rather than thrown so that a suite can assert on it directly and report "no
    /// correlation identifier" instead of failing with an exception from the helper, which would name the
    /// helper rather than the contract.
    /// </remarks>
    public static string? ReadCorrelationId(HttpResponseMessage response)
    {
        ArgumentNullException.ThrowIfNull(response);

        return response.Headers.TryGetValues(CorrelationIdHeader, out IEnumerable<string>? values)
            ? values.FirstOrDefault()
            : null;
    }

    /// <summary>
    /// Opens a dependency-injection scope on the host's container.
    /// </summary>
    /// <returns>A scope the caller owns and must dispose.</returns>
    /// <remarks>
    /// Every repository, the unit of work and the tenant context are registered SCOPED, mirroring their
    /// per-request lifetime, so a test that reaches for one has to establish a scope first. This is the same
    /// container the request pipeline resolves from - nothing is substituted - which is what makes a
    /// resolution here evidence about production composition. Prefer
    /// <see cref="CreateScopedServices"/> when the contracts wanted are the common four.
    /// </remarks>
    public IServiceScope CreateScope() => Services.CreateScope();

    /// <summary>
    /// Opens a dependency-injection scope and exposes the persistence contracts a suite reaches for.
    /// </summary>
    /// <returns>A scope handle the caller owns and must dispose.</returns>
    /// <remarks>
    /// A convenience over <see cref="CreateScope"/> that removes the resolve-by-hand step, and a guard
    /// rail with it: the accessors are typed, so a contract that was renamed or unregistered fails to
    /// compile or fails loudly on first touch rather than being resolved under a string.
    /// </remarks>
    public ScopedServices CreateScopedServices() => new(CreateScope());

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
        // MIGRATION: the legacy application read its connection string from the named entry
        // <add name="SiteSqlServer" connectionString="Data Source=.\SQLExpress;Integrated Security=True;
        // User Instance=True;AttachDBFilename=|DataDirectory|Database.mdf;" /> in Website/release.config
        // L21-L26 - a file-attached SQL Server Express user instance, resolved through
        // ConfigurationManager. That name is superseded by ConnectionStrings:Default, spelled
        // ConnectionStrings__Default as an environment variable, which is the form docker-compose supplies
        // and therefore the form the suite supplies too. The value here is the throwaway database this run
        // provisioned, so the production registration binds to a real server without being altered.
        ["ConnectionStrings__Default"] = Database.ConnectionString,
        ["Jwt__Secret"] = SigningSecret,
        ["Jwt__Issuer"] = Issuer,
        ["Jwt__Audience"] = Audience,
        ["Jwt__ExpirationMinutes"] = "30",
        ["Jwt__RefreshTokenExpirationDays"] = "7",
        ["LegacyCredentials__Enabled"] = "true",

        // The window's absolute deadline, which the enabled switch now requires. It is computed from the
        // run's own clock rather than written as a literal instant, because a literal would silently expire
        // and turn every legacy-credential fact into a refusal months after it was written - a failure whose
        // cause is invisible in the assertion that reports it. The value is round-trip formatted with a zero
        // offset, which is the only form the options validator accepts.
        //
        // MIGRATION: a second, parallel section named LegacyCredentialMigration was configured here by one
        // revision and is withdrawn. One window cannot have two switches, two keys and two deadlines; the
        // section that survives is the one the verifier and its start-up validation actually read.
        ["LegacyCredentials__EnabledUntilUtc"] =
            DateTimeOffset.UtcNow.AddDays(1).ToString("O", CultureInfo.InvariantCulture),
        ["LegacyCredentials__DecryptionKey"] = LegacyCredentialDecryptionKey,
        ["LegacyCredentials__DecryptionAlgorithm"] = "3DES",
        ["LegacyCredentials__ValidationAlgorithm"] = "SHA1",
        ["Cors__AllowedOrigins__0"] = AllowedOrigin,

        // SEC-006: STATED EXPLICITLY RATHER THAN INHERITED, THOUGH IT NOW MATCHES THE SHIPPED VALUE.
        // appsettings.json ships AllowedHosts as "*" because exact PortalAlias resolution is the authority
        // for which hosts identify a tenant - see the host-filtering entry in MIGRATION_NOTES.md for why two
        // independent allow-lists for one question is the defect and not the control. This suite needs that
        // value regardless of what the shipped file says, because it invents alias host names at run time -
        // "alias-<suffix>.local", a deliberately unconfigured "no-such-tenant.example", and a one-character
        // truncation of the seeded alias used to prove that a substring reaches no tenant - none of which can
        // be enumerated in a file written before the run.
        //
        // It is written out here rather than left to inheritance so the value the suite runs under is visible
        // in the suite. The MECHANISM is still proved to work: the host-filtering facts in
        // TenantResolutionTests build their own host carrying an explicit restricted list, which is how a
        // deployment-scoped boundary is configured (docker/docker-compose.tls.yml does exactly that), and
        // assert that an unconfigured name is refused under it.
        ["AllowedHosts"] = "*",
        ["RateLimiting__Authentication__PermitLimit"] = PermissiveAuthenticationRateLimit,
        ["RateLimiting__Authentication__WindowSeconds"] = "60",

        // MIGRATION: the credential policy is carried forward from the legacy membership provider
        // VERBATIM - Website/release.config L237-L247 registers AspNetSqlMembershipProvider with
        // minRequiredPasswordLength="7", minRequiredNonalphanumericCharacters="0",
        // requiresQuestionAndAnswer="false" and requiresUniqueEmail="false". It is pinned here rather than
        // inherited from appsettings.json so that the policy the suite proves is the legacy one whatever a
        // deployment overlay later says, and so that a reader can see the four values being asserted
        // against. Tightening any of them would be a migration that locked existing accounts out of an
        // installation, which is a decision for an operator and not a side effect of a port.
        // The names are the option PROPERTY names - MinRequiredPasswordLength, not MinimumLength - because
        // binding matches property names and a plausible-looking alternative spelling binds to nothing at
        // all, leaving the shipped default in force and the override silently inert.
        ["PasswordPolicy__MinRequiredPasswordLength"] = "7",
        ["PasswordPolicy__MinRequiredNonAlphanumericCharacters"] = "0",
        ["PasswordPolicy__RequiresQuestionAndAnswer"] = "false",
        ["PasswordPolicy__RequiresUniqueEmail"] = "false",
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

        // The second category the suite must not lose, and for the same reason. The request envelope -
        // one entry per request carrying the route template, the status the caller was answered with and
        // a message-redacted description of any failure - is written at the informational level for an
        // ordinary request, so the default above would discard the very entries
        // RequestLoggingContractTests asserts on. Scoped to that one source context, which is the
        // middleware's own type, so nothing else becomes noisier.
        ["Serilog__MinimumLevel__Override__DnnMigration.Api.Middleware.RequestLoggingMiddleware"] = "Information",

        // The third category, and the reason it is here is a contract that MOVED. The health endpoints
        // publish four members and deliberately name no probe, because both views are anonymous and an
        // enumeration of this application's dependencies is reconnaissance rather than diagnostics. The
        // per-probe detail an operator needs did not disappear with it - it moved to this category, written
        // at debug for a healthy report because a passing probe is not news. The default above would discard
        // it, so the fact asserting that every registered probe is still reported SOMEWHERE would have
        // nothing to read and would pass vacuously.
        ["Serilog__MinimumLevel__Override__DnnMigration.Api.HealthChecks"] = "Debug",
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

        // The container is validated rather than merely built, and this is the only place in the delivery
        // where that happens: the Development environment turns scope validation on by default, but this host
        // runs as Testing and production runs as Production, so neither would.
        //
        // ValidateOnBuild constructs a call site for every registration at build time, so a service whose
        // dependency was never registered - or a SINGLETON that captures a SCOPED one, which is a captive
        // dependency and outlives the scope it came from - fails here, naming the pair, instead of surfacing
        // later as a repository quietly shared between requests. ValidateScopes then forbids resolving a
        // scoped service straight from the root provider, which is the same fault committed by hand.
        //
        // Both are deliberately enabled on the shared host rather than on a dedicated one, so that every
        // suite in the run pays for the guarantee once and no composition change can slip past it.
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

        // Applied for the remainder of the run rather than per host, because every host this suite builds -
        // the shared one and any ad-hoc one - needs the same database and the same signing key.
        //
        // CAPTURED AND RESTORED rather than simply assigned. These are PROCESS-wide variables, and the test
        // host is not the only thing in the process that reads them: the connection string names a database
        // this fixture drops on the way out, so leaving it set would point anything that read it afterwards at
        // a database that no longer exists, and leaving the signing key set would leak a key into whatever ran
        // next. An earlier revision assigned them and never put anything back, which also meant a developer's
        // own exported value was silently destroyed for the remainder of the process.
        _environment = new EnvironmentScope(HostConfiguration());

        // Touching Services builds the host, which runs the options validation registered with
        // ValidateOnStart. A missing or short signing secret therefore fails the fixture with the
        // configuration message rather than failing every test with a connection error.
        _ = Services;

        // And then the composition itself is exercised once, inside a scope, because building the host
        // proves only that the registrations are well formed. Constructing the four contracts every suite
        // depends on proves they can actually be built - a repository whose constructor argument is
        // unregistered, or which cannot obtain the context, fails here with the container's own message
        // instead of failing an unrelated assertion halfway through the run. It is one scope and four
        // resolutions, so the cost is negligible against the diagnosis it buys.
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
    /// <para>
    /// EVERY STEP RUNS WHATEVER THE PREVIOUS ONE DID. An earlier revision awaited the host's disposal and then
    /// released the database, so a host that threw on the way down took the database release with it - and the
    /// database was created on a server that outlives the run, so the leak was permanent and silent. Each step
    /// is now independent, and the environment restoration in particular must happen even when both of the
    /// others fail, because it is the only one whose omission escapes this process.
    /// </para>
    /// <para>
    /// The ORDER is fixed and matters: the host is disposed first because it holds connections to the database,
    /// the database is released second, and the environment is restored last so that anything either disposal
    /// touches still reads the run's own configuration while it is shutting down.
    /// </para>
    /// <para>
    /// NO FAILURE IS DISCARDED. One failure is rethrown with its original type and stack, because a single
    /// fault deserves to be reported as itself rather than wrapped; several are reported together, because
    /// choosing one of them would hide the others and the second failure is frequently the consequence of the
    /// first.
    /// </para>
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
/// A dependency-injection scope on the host's container, with the persistence contracts a suite reaches for
/// exposed as typed members.
/// </summary>
/// <remarks>
/// <para>
/// The contracts below are registered SCOPED, because each of them ultimately reaches the same per-request
/// data context. A test therefore cannot resolve one from the root provider, and the handle exists so that
/// the scope it does need is opened, named and disposed in one place instead of being reconstructed at every
/// site.
/// </para>
/// <para>
/// Each accessor resolves on read rather than eagerly at construction, so opening a handle costs nothing for
/// the contracts a test does not touch. Because the registrations are scoped, two reads of the same accessor
/// inside one handle return the SAME instance - which is the behaviour a test wants when it writes through
/// the unit of work and then reads back through a repository, since both are working over one context.
/// Crossing that boundary requires a second handle, exactly as crossing a request boundary would.
/// </para>
/// <para>
/// <strong>The data context itself is deliberately not reachable from here.</strong> It is declared
/// <c>internal</c> to the Infrastructure assembly so that no layer above it can see a <c>DbContext</c> at
/// all, and this type names none of the members that would work around that. What a test gets is the same
/// abstraction the application gets, which is what makes an assertion made through it evidence about
/// production behaviour.
/// </para>
/// </remarks>
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
/// <para>
/// Membership of a single collection is what makes the suites run one after another instead of side by
/// side. That is required rather than merely tidy: they share one database, and the sign-in rate limiter
/// partitions by caller address, which the in-memory transport makes identical for all of them.
/// </para>
/// <para>
/// <b>SERIALISATION IS NOT ISOLATION, AND EVERY MUTATION OF SHARED STATE MUST BE FAILURE-SAFE.</b> Running one
/// fact at a time removes races; it does nothing whatever about state a fact leaves behind. Any fact that
/// mutates something the SEEDED reference data owns - the seeded tenant's pages, roles, accounts or
/// permission grants, or the installation's portal list - therefore has to restore it from a
/// <c>finally</c> block, so that the restoration happens on the failing path as well as the passing one.
/// </para>
/// <para>
/// The reason is that the alternative is not "one failure" but "one failure and then several misleading
/// ones". A fact that fails after marking a seeded page deleted, or after adding a role it meant to remove,
/// leaves every later fact in the run reading contaminated data - and those later failures point at code that
/// is working correctly, while the fact that actually broke is buried among them. Worse, the contamination
/// can make a later fact PASS: a listing assertion satisfied by a leftover row proves nothing.
/// </para>
/// <para>
/// Two rules follow, and both are applied throughout this assembly. Prefer creating your own row over mutating
/// a seeded one, because a row a fact created is a row no other fact reads by name. Where a seeded row must be
/// mutated, put the restoration in a <c>finally</c> block and keep the ASSERTIONS in the try body, so a
/// cleanup failure can never replace the failure that made cleanup necessary. Where the cleanup itself must be
/// dependable on a path where the component under test may be the broken thing, restore with a direct
/// statement rather than through that component.
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
