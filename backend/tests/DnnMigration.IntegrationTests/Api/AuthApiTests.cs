using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using DnnMigration.Application.Dtos.Auth;
using DnnMigration.Application.Dtos.User;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace DnnMigration.IntegrationTests.Api;

/// <summary>
/// Covers the authentication vertical end to end: exchanging a credential for a token pair, rotating that
/// pair, revoking it, and reading the caller's own snapshot back.
/// </summary>
/// <remarks>
/// <para>
/// This suite is the only place the credential store is exercised over HTTP rather than bypassed. Every other
/// suite mints its bearer token directly from the fixture's signing key, which is fast and deliberate but
/// proves nothing about the sign-in path. The consequence is that everything between the submitted credential
/// and the issued token - tenant resolution, account resolution, the account-state gates, the hash comparison,
/// the failed-attempt bookkeeping and the claim resolution that reads roles and permissions out of the
/// database - is only ever asserted here.
/// </para>
/// <para>
/// The gates are asserted in the order the service applies them, because the order is the security property.
/// A caller who is not entitled to detail receives one indistinguishable denial for an unknown account, a
/// wrong credential, a missing credential row and a locked account; a caller who is entitled - the host or the
/// tenant's designated administrator - receives the specific reason. Both halves are pinned, since a refusal
/// that leaks which of the four applied is an account-enumeration oracle.
/// </para>
/// <para>
/// Refresh rotation is asserted as a rotation, not merely as a second token: presenting a consumed value is a
/// replay, and the response to a replay is to revoke everything the account holds rather than to refuse the
/// one value. That is wider than the legacy cookie sign-out and is asserted deliberately. Because it revokes
/// by account, every rotation test operates on an account created for that test alone, so no other suite's
/// tokens are collateral.
/// </para>
/// <para>
/// Rate limiting cannot be asserted against the shared host, which runs with a deliberately permissive limit
/// so that the other suites can sign in freely. It is asserted against a second host built inside an
/// environment override, which is the only seam that reaches the limiter's configuration - the host reads its
/// options while composing services, so nothing applied after construction can change them.
/// </para>
/// </remarks>
[Trait("Category", "Integration")]
[Collection(IntegrationTestCollection.Name)]
public sealed class AuthApiTests
{
    /// <summary>A tenant identifier no seeded or created portal can hold.</summary>
    private const int UnknownPortalId = 987654;

    /// <summary>
    /// The failed-attempt threshold the password policy defaults to, which is what the seeded configuration
    /// leaves in force.
    /// </summary>
    private const int MaxInvalidPasswordAttempts = 5;

    /// <summary>A BCrypt cost below the hasher's own, so a credential stored at it must be replaced.</summary>
    private const int SupersededWorkFactor = 10;

    /// <summary>The cost the hasher writes, which a replaced credential must therefore carry.</summary>
    private const int CurrentWorkFactor = 12;

    /// <summary>The permit count the dedicated rate-limited host runs with.</summary>
    private const int TightPermitLimit = 2;

    private readonly ApiTestFixture _fixture;

    /// <summary>Initialises a new instance of the <see cref="AuthApiTests"/> class.</summary>
    /// <param name="fixture">The shared host, database and seed.</param>
    public AuthApiTests(ApiTestFixture fixture) => _fixture = fixture;

    /// <summary>
    /// A seeded credential is exchanged for a usable token pair, and the access token it carries is accepted
    /// by a protected endpoint.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task Login_WithSeededAdministratorCredential_ReturnsOkAndAUsableTokenPair()
    {
        using HttpClient client = _fixture.CreateAnonymousClient();

        using HttpResponseMessage response = await client.PostAsJsonAsync(
            LoginRoute(_fixture.Seed.PortalId),
            new LoginRequest
            {
                Username = IntegrationSeed.AdminUserName,
                Password = ApiTestFixture.KnownPassword,
            },
            ApiTestFixture.Json);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        LoginResponse issued = await ReadLoginAsync(response);

        issued.AccessToken.Should().NotBeNullOrWhiteSpace();
        issued.TokenType.Should().Be("Bearer");
        issued.ExpiresIn.Should().BeGreaterThan(0);
        issued.ExpiresAtUtc.Should().BeAfter(DateTime.UtcNow);
        issued.RefreshToken.Should().NotBeNullOrWhiteSpace();
        issued.RefreshTokenExpiresAtUtc.Should().BeAfter(issued.ExpiresAtUtc);

        issued.User.UserId.Should().Be(_fixture.Seed.AdminUserId);
        issued.User.PortalId.Should().Be(_fixture.Seed.PortalId);
        issued.User.Username.Should().Be(IntegrationSeed.AdminUserName);
        issued.User.PortalName.Should().Be(IntegrationSeed.PortalName);
        issued.User.IsSuperUser.Should().BeFalse();
        issued.User.Roles.Should().Contain(IntegrationSeed.AdministratorsRoleName);

        // The token is only meaningful if the pipeline accepts it, so it is spent rather than inspected.
        using HttpClient bearer = AuthenticatedClientFactory.Authenticate(
            _fixture.CreateAnonymousClient(),
            issued.AccessToken);

        using HttpResponseMessage snapshot = await bearer.GetAsync(MeRoute);

        snapshot.StatusCode.Should().Be(HttpStatusCode.OK);

        CurrentUserDto? me = await snapshot.Content.ReadFromJsonAsync<CurrentUserDto>(ApiTestFixture.Json);

        me.Should().NotBeNull();
        me!.UserId.Should().Be(_fixture.Seed.AdminUserId);
        me.Roles.Should().Contain(IntegrationSeed.AdministratorsRoleName);
    }

    /// <summary>The host account signs in and its snapshot reports the host flag.</summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task Login_ForTheHostAccount_ReportsTheHostFlag()
    {
        using HttpClient client = _fixture.CreateAnonymousClient();

        using HttpResponseMessage response = await client.PostAsJsonAsync(
            LoginRoute(_fixture.Seed.PortalId),
            new LoginRequest
            {
                Username = IntegrationSeed.HostUserName,
                Password = ApiTestFixture.KnownPassword,
            },
            ApiTestFixture.Json);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        LoginResponse issued = await ReadLoginAsync(response);

        issued.User.UserId.Should().Be(_fixture.Seed.HostUserId);
        issued.User.IsSuperUser.Should().BeTrue();

        // The host holds no membership of the tenant it signs in to, and is admitted regardless. The
        // seed does place it in the administrators role, so that is asserted rather than an empty set.
        issued.User.PortalId.Should().Be(_fixture.Seed.PortalId);
    }

    /// <summary>
    /// The tenant the request host resolves to wins over a tenant named in the query string, so a query
    /// naming a tenant that does not exist does not defeat a sign-in.
    /// </summary>
    /// <remarks>
    /// The controller reads the resolved tenant first and falls back to the query parameter only when the
    /// request host matches no configured alias. The fixture serves every request as the seeded alias, so
    /// the parameter is decorative here - and asserting that keeps a future reordering from silently
    /// letting a caller choose its own tenant.
    /// </remarks>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task Login_PrefersTheResolvedTenantOverTheQueryString()
    {
        using HttpClient client = _fixture.CreateAnonymousClient();

        using HttpResponseMessage response = await client.PostAsJsonAsync(
            LoginRoute(UnknownPortalId),
            new LoginRequest
            {
                Username = IntegrationSeed.AdminUserName,
                Password = ApiTestFixture.KnownPassword,
            },
            ApiTestFixture.Json);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        LoginResponse issued = await ReadLoginAsync(response);

        issued.User.PortalId.Should().Be(_fixture.Seed.PortalId);
    }

    /// <summary>
    /// A wrong credential and an unknown account are refused identically, so neither reveals whether the
    /// account exists.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task Login_RefusesAWrongCredentialAndAnUnknownAccountIdentically()
    {
        using HttpClient client = _fixture.CreateAnonymousClient();

        using HttpResponseMessage wrongCredential = await client.PostAsJsonAsync(
            LoginRoute(_fixture.Seed.PortalId),
            new LoginRequest
            {
                Username = IntegrationSeed.AdminUserName,
                Password = "not-the-stored-credential",
            },
            ApiTestFixture.Json);

        using HttpResponseMessage unknownAccount = await client.PostAsJsonAsync(
            LoginRoute(_fixture.Seed.PortalId),
            new LoginRequest
            {
                Username = "no_such_account_" + Suffix(),
                Password = ApiTestFixture.KnownPassword,
            },
            ApiTestFixture.Json);

        wrongCredential.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        unknownAccount.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        // Every part of the document that describes the outcome must match. The trace identifier is
        // deliberately excluded because it is minted per request and carries no outcome information, so
        // comparing whole bodies would compare a value that is required to differ.
        string first = await ReadDenialAsync(wrongCredential);
        string second = await ReadDenialAsync(unknownAccount);

        first.Should().Contain("urn:dnnmigration:error:auth.invalid_credentials");
        second.Should().Be(first);
    }

    /// <summary>An unknown tenant is refused with the same generic denial, not with a tenant-shaped error.</summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task Login_ForAnUnknownTenant_ReturnsUnauthorized()
    {
        // The request must not resolve a tenant from its host, or the resolved tenant would win. A second
        // host name that matches no alias is the only way to reach the query-parameter branch.
        using HttpClient client = _fixture.CreateAnonymousClient();
        client.DefaultRequestHeaders.Host = "unconfigured-host.example";

        using HttpResponseMessage response = await client.PostAsJsonAsync(
            LoginRoute(UnknownPortalId),
            new LoginRequest
            {
                Username = IntegrationSeed.AdminUserName,
                Password = ApiTestFixture.KnownPassword,
            },
            ApiTestFixture.Json);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        string body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("urn:dnnmigration:error:auth.invalid_credentials");
    }

    /// <summary>
    /// A request whose host matches no alias and which names no tenant is a bad request, because the tenant
    /// being signed in to is then unknowable.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task Login_WithNeitherAResolvedNorANamedTenant_ReturnsBadRequest()
    {
        using HttpClient client = _fixture.CreateAnonymousClient();
        client.DefaultRequestHeaders.Host = "unconfigured-host.example";

        using HttpResponseMessage response = await client.PostAsJsonAsync(
            new Uri("/api/v1/auth/login", UriKind.Relative),
            new LoginRequest
            {
                Username = IntegrationSeed.AdminUserName,
                Password = ApiTestFixture.KnownPassword,
            },
            ApiTestFixture.Json);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        string body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("does not correspond to a configured portal alias");
    }

    /// <summary>A blank account name is a request fault reported by the validator, not a denial.</summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task Login_WithoutAnAccountName_ReturnsBadRequest()
    {
        using HttpClient client = _fixture.CreateAnonymousClient();

        using HttpResponseMessage response = await client.PostAsJsonAsync(
            LoginRoute(_fixture.Seed.PortalId),
            new LoginRequest { Username = string.Empty, Password = ApiTestFixture.KnownPassword },
            ApiTestFixture.Json);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        string body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("A username is required.");
    }

    /// <summary>A blank credential is likewise a request fault.</summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task Login_WithoutACredential_ReturnsBadRequest()
    {
        using HttpClient client = _fixture.CreateAnonymousClient();

        using HttpResponseMessage response = await client.PostAsJsonAsync(
            LoginRoute(_fixture.Seed.PortalId),
            new LoginRequest { Username = IntegrationSeed.AdminUserName, Password = string.Empty },
            ApiTestFixture.Json);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        string body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("A password is required.");
    }

    /// <summary>A locked account is refused generically when the caller is not entitled to the reason.</summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task Login_ForALockedAccount_IsGenericToAnAnonymousCaller()
    {
        using HttpClient administrator = _fixture.CreateAdministratorClient();
        UserDetailDto account = await CreateUserAsync(administrator);
        await LockAsync(account.Username);

        using HttpClient client = _fixture.CreateAnonymousClient();

        using HttpResponseMessage response = await client.PostAsJsonAsync(
            LoginRoute(_fixture.Seed.PortalId),
            new LoginRequest { Username = account.Username, Password = ApiTestFixture.KnownPassword },
            ApiTestFixture.Json);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        string body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("urn:dnnmigration:error:auth.invalid_credentials");
        body.Should().NotContain("locked");
    }

    /// <summary>
    /// The same locked account reports the specific reason when the caller signing it in is the host, which
    /// is the case that makes an administrative diagnosis possible without creating an oracle.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task Login_ForALockedAccount_IsExplicitToAHostCaller()
    {
        using HttpClient administrator = _fixture.CreateAdministratorClient();
        UserDetailDto account = await CreateUserAsync(administrator);
        await LockAsync(account.Username);

        // Sign-in is anonymous, but it does not refuse a caller that presents a token, and the entitlement
        // test reads that caller. A host-authenticated sign-in attempt is therefore told why.
        using HttpClient host = _fixture.CreateHostClient();

        using HttpResponseMessage response = await host.PostAsJsonAsync(
            LoginRoute(_fixture.Seed.PortalId),
            new LoginRequest { Username = account.Username, Password = ApiTestFixture.KnownPassword },
            ApiTestFixture.Json);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        string body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("urn:dnnmigration:error:auth.locked_out");
        body.Should().Contain("is locked and must be unlocked");
    }

    /// <summary>
    /// Repeated wrong credentials lock the account once the policy's threshold is reached, which is the
    /// bookkeeping the legacy membership procedures performed inside the store.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task Login_WithRepeatedWrongCredentials_LocksTheAccount()
    {
        using HttpClient administrator = _fixture.CreateAdministratorClient();
        UserDetailDto account = await CreateUserAsync(administrator);

        using HttpClient client = _fixture.CreateAnonymousClient();

        for (int attempt = 0; attempt < MaxInvalidPasswordAttempts; attempt++)
        {
            using HttpResponseMessage refused = await client.PostAsJsonAsync(
                LoginRoute(_fixture.Seed.PortalId),
                new LoginRequest { Username = account.Username, Password = "wrong-" + attempt },
                ApiTestFixture.Json);

            refused.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        }

        using HttpClient host = _fixture.CreateHostClient();

        using HttpResponseMessage locked = await host.PostAsJsonAsync(
            LoginRoute(_fixture.Seed.PortalId),
            new LoginRequest { Username = account.Username, Password = ApiTestFixture.KnownPassword },
            ApiTestFixture.Json);

        locked.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        string body = await locked.Content.ReadAsStringAsync();
        body.Should().Contain("urn:dnnmigration:error:auth.locked_out");

        // The lock is observable through the administrative projection as well as through the denial.
        using HttpResponseMessage read = await administrator.GetAsync(
            UserRoute(_fixture.Seed.PortalId, account.UserId));

        read.StatusCode.Should().Be(HttpStatusCode.OK);

        UserDetailDto? detail = await read.Content.ReadFromJsonAsync<UserDetailDto>(ApiTestFixture.Json);

        detail.Should().NotBeNull();
        detail!.IsLockedOut.Should().BeTrue();
    }

    /// <summary>
    /// An unapproved account is admitted only on presentation of its verification code, and the approval it
    /// earns is persisted rather than applying to that one exchange.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task Login_ForAnUnapprovedAccount_RequiresTheVerificationCode()
    {
        using HttpClient administrator = _fixture.CreateAdministratorClient();
        UserDetailDto account = await CreateUserAsync(administrator, authorize: false);

        account.IsApproved.Should().BeFalse();

        using HttpClient client = _fixture.CreateAnonymousClient();

        using HttpResponseMessage withoutCode = await client.PostAsJsonAsync(
            LoginRoute(_fixture.Seed.PortalId),
            new LoginRequest { Username = account.Username, Password = ApiTestFixture.KnownPassword },
            ApiTestFixture.Json);

        withoutCode.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        using HttpResponseMessage withWrongCode = await client.PostAsJsonAsync(
            LoginRoute(_fixture.Seed.PortalId),
            new LoginRequest
            {
                Username = account.Username,
                Password = ApiTestFixture.KnownPassword,
                VerificationCode = "0-0",
            },
            ApiTestFixture.Json);

        withWrongCode.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        string expectedCode = string.Create(
            CultureInfo.InvariantCulture,
            $"{_fixture.Seed.PortalId}-{account.UserId}");

        using HttpResponseMessage verified = await client.PostAsJsonAsync(
            LoginRoute(_fixture.Seed.PortalId),
            new LoginRequest
            {
                Username = account.Username,
                Password = ApiTestFixture.KnownPassword,
                VerificationCode = expectedCode,
            },
            ApiTestFixture.Json);

        verified.StatusCode.Should().Be(HttpStatusCode.OK);

        // The approval must survive the exchange: a subsequent sign-in carries no code and is admitted.
        using HttpResponseMessage afterwards = await client.PostAsJsonAsync(
            LoginRoute(_fixture.Seed.PortalId),
            new LoginRequest { Username = account.Username, Password = ApiTestFixture.KnownPassword },
            ApiTestFixture.Json);

        afterwards.StatusCode.Should().Be(HttpStatusCode.OK);

        using HttpResponseMessage read = await administrator.GetAsync(
            UserRoute(_fixture.Seed.PortalId, account.UserId));

        UserDetailDto? detail = await read.Content.ReadFromJsonAsync<UserDetailDto>(ApiTestFixture.Json);

        detail.Should().NotBeNull();
        detail!.IsApproved.Should().BeTrue();
    }

    /// <summary>
    /// A credential stored at a superseded cost is replaced on the first successful sign-in, which is the
    /// lazy upgrade that carries accounts forward without asking them to reset.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task Login_ReplacesACredentialStoredAtASupersededCost()
    {
        using HttpClient administrator = _fixture.CreateAdministratorClient();
        UserDetailDto account = await CreateUserAsync(administrator);

        // Enhanced, matching the production hasher, so the only thing that differs from a
        // current credential is the cost. A plain hash would fail to verify at all and the
        // test would prove nothing about rehashing.
        string superseded = BCrypt.Net.BCrypt.EnhancedHashPassword(
            ApiTestFixture.KnownPassword,
            SupersededWorkFactor,
            BCrypt.Net.HashType.SHA384);

        await WriteStoredHashAsync(account.Username, superseded);
        (await ReadStoredHashAsync(account.Username)).Should().Be(superseded);

        using HttpClient client = _fixture.CreateAnonymousClient();

        using HttpResponseMessage response = await client.PostAsJsonAsync(
            LoginRoute(_fixture.Seed.PortalId),
            new LoginRequest { Username = account.Username, Password = ApiTestFixture.KnownPassword },
            ApiTestFixture.Json);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        string? replaced = await ReadStoredHashAsync(account.Username);

        replaced.Should().NotBeNull();
        replaced.Should().NotBe(superseded);
        replaced!.Should().Contain(
            string.Create(CultureInfo.InvariantCulture, $"${CurrentWorkFactor}$"));

        // The replacement must still verify the same credential, or the upgrade would lock the account out.
        using HttpResponseMessage again = await client.PostAsJsonAsync(
            LoginRoute(_fixture.Seed.PortalId),
            new LoginRequest { Username = account.Username, Password = ApiTestFixture.KnownPassword },
            ApiTestFixture.Json);

        again.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    /// <summary>A successful sign-in is recorded against the account.</summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task Login_RecordsTheSignIn()
    {
        using HttpClient administrator = _fixture.CreateAdministratorClient();
        UserDetailDto account = await CreateUserAsync(administrator);

        // A newly created account has never signed in, and the store's "never" sentinel is projected as
        // absent rather than as the sentinel date itself.
        account.LastLoginDate.Should().BeNull();

        using HttpClient client = _fixture.CreateAnonymousClient();

        using HttpResponseMessage response = await client.PostAsJsonAsync(
            LoginRoute(_fixture.Seed.PortalId),
            new LoginRequest { Username = account.Username, Password = ApiTestFixture.KnownPassword },
            ApiTestFixture.Json);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using HttpResponseMessage read = await administrator.GetAsync(
            UserRoute(_fixture.Seed.PortalId, account.UserId));

        UserDetailDto? detail = await read.Content.ReadFromJsonAsync<UserDetailDto>(ApiTestFixture.Json);

        detail.Should().NotBeNull();
        detail!.LastLoginDate.Should().NotBeNull();
    }

    /// <summary>An account holding no credential row is refused, not admitted on an empty comparison.</summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task Login_ForAnAccountWithoutACredential_ReturnsUnauthorized()
    {
        using HttpClient administrator = _fixture.CreateAdministratorClient();
        UserDetailDto account = await CreateUserAsync(administrator);

        await _fixture.Database.ExecuteAsync(
            """
            DELETE am
            FROM [dbo].[aspnet_Membership] am
            INNER JOIN [dbo].[aspnet_Users] au ON au.[UserId] = am.[UserId]
            WHERE au.[LoweredUserName] = LOWER(@userName);
            """,
            new Dictionary<string, object?> { ["userName"] = account.Username });

        using HttpClient client = _fixture.CreateAnonymousClient();

        using HttpResponseMessage response = await client.PostAsJsonAsync(
            LoginRoute(_fixture.Seed.PortalId),
            new LoginRequest { Username = account.Username, Password = ApiTestFixture.KnownPassword },
            ApiTestFixture.Json);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        string body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("urn:dnnmigration:error:auth.invalid_credentials");
    }

    /// <summary>An exchange yields a new pair, and the access token it carries is accepted.</summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task Refresh_RotatesThePairAndIssuesAUsableAccessToken()
    {
        using HttpClient administrator = _fixture.CreateAdministratorClient();
        UserDetailDto account = await CreateUserAsync(administrator);

        using HttpClient client = _fixture.CreateAnonymousClient();
        LoginResponse first = await SignInAsync(client, account.Username);

        using HttpResponseMessage response = await client.PostAsJsonAsync(
            RefreshRoute,
            new RefreshTokenRequest { RefreshToken = first.RefreshToken },
            ApiTestFixture.Json);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        LoginResponse second = await ReadLoginAsync(response);

        second.RefreshToken.Should().NotBeNullOrWhiteSpace();
        second.RefreshToken.Should().NotBe(first.RefreshToken);
        second.AccessToken.Should().NotBeNullOrWhiteSpace();
        second.User.UserId.Should().Be(account.UserId);
        second.User.PortalId.Should().Be(_fixture.Seed.PortalId);
        second.User.Username.Should().Be(account.Username);

        using HttpClient bearer = AuthenticatedClientFactory.Authenticate(
            _fixture.CreateAnonymousClient(),
            second.AccessToken);

        using HttpResponseMessage snapshot = await bearer.GetAsync(MeRoute);

        snapshot.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    /// <summary>
    /// Presenting a consumed refresh token is a replay, and the answer is to revoke every token the account
    /// holds rather than to refuse the one value.
    /// </summary>
    /// <remarks>
    /// The account is created for this test alone precisely because the response is account-wide. Using a
    /// seeded account would revoke tokens other tests may hold.
    /// </remarks>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task Refresh_WithAReplayedToken_IsRefusedAndRevokesTheAccountsTokens()
    {
        using HttpClient administrator = _fixture.CreateAdministratorClient();
        UserDetailDto account = await CreateUserAsync(administrator);

        using HttpClient client = _fixture.CreateAnonymousClient();
        LoginResponse first = await SignInAsync(client, account.Username);

        using HttpResponseMessage rotated = await client.PostAsJsonAsync(
            RefreshRoute,
            new RefreshTokenRequest { RefreshToken = first.RefreshToken },
            ApiTestFixture.Json);

        rotated.StatusCode.Should().Be(HttpStatusCode.OK);

        LoginResponse second = await ReadLoginAsync(rotated);

        using HttpResponseMessage replay = await client.PostAsJsonAsync(
            RefreshRoute,
            new RefreshTokenRequest { RefreshToken = first.RefreshToken },
            ApiTestFixture.Json);

        replay.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        string body = await replay.Content.ReadAsStringAsync();
        body.Should().Contain("urn:dnnmigration:error:auth.invalid_refresh_token");

        // The replacement was valid a moment ago and is now worthless, which is the whole point.
        using HttpResponseMessage afterwards = await client.PostAsJsonAsync(
            RefreshRoute,
            new RefreshTokenRequest { RefreshToken = second.RefreshToken },
            ApiTestFixture.Json);

        afterwards.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    /// <summary>A blank refresh token is refused as a token fault, not accepted as an empty exchange.</summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task Refresh_WithoutAToken_ReturnsUnauthorized()
    {
        using HttpClient client = _fixture.CreateAnonymousClient();

        using HttpResponseMessage response = await client.PostAsJsonAsync(
            RefreshRoute,
            new RefreshTokenRequest { RefreshToken = string.Empty },
            ApiTestFixture.Json);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        string body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("urn:dnnmigration:error:auth.invalid_refresh_token");
    }

    /// <summary>An unrecognised refresh token is refused with the same reason as a consumed one.</summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task Refresh_WithAnUnknownToken_ReturnsUnauthorized()
    {
        using HttpClient client = _fixture.CreateAnonymousClient();

        using HttpResponseMessage response = await client.PostAsJsonAsync(
            RefreshRoute,
            new RefreshTokenRequest { RefreshToken = "not-a-token-" + Suffix() },
            ApiTestFixture.Json);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        string body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("urn:dnnmigration:error:auth.invalid_refresh_token");
    }

    /// <summary>Signing out revokes the presented refresh token, so it can no longer be exchanged.</summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task Logout_RevokesTheRefreshToken()
    {
        using HttpClient administrator = _fixture.CreateAdministratorClient();
        UserDetailDto account = await CreateUserAsync(administrator);

        using HttpClient client = _fixture.CreateAnonymousClient();
        LoginResponse issued = await SignInAsync(client, account.Username);

        using HttpResponseMessage response = await client.PostAsJsonAsync(
            LogoutRoute,
            new RefreshTokenRequest { RefreshToken = issued.RefreshToken },
            ApiTestFixture.Json);

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);

        using HttpResponseMessage afterwards = await client.PostAsJsonAsync(
            RefreshRoute,
            new RefreshTokenRequest { RefreshToken = issued.RefreshToken },
            ApiTestFixture.Json);

        afterwards.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        // MIGRATION: the access token already issued is not recalled. A bearer token cannot be withdrawn,
        // which is why its lifetime is short; the legacy cookie sign-out took effect at once.
        using HttpClient bearer = AuthenticatedClientFactory.Authenticate(
            _fixture.CreateAnonymousClient(),
            issued.AccessToken);

        using HttpResponseMessage stillAccepted = await bearer.GetAsync(MeRoute);

        stillAccepted.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    /// <summary>
    /// Signing out is idempotent and silent about whether anything was revoked, so a blank value, an
    /// unrecognised value and a repeat all succeed.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task Logout_IsIdempotentAndSilent()
    {
        using HttpClient client = _fixture.CreateAnonymousClient();

        using HttpResponseMessage blank = await client.PostAsJsonAsync(
            LogoutRoute,
            new RefreshTokenRequest { RefreshToken = string.Empty },
            ApiTestFixture.Json);

        blank.StatusCode.Should().Be(HttpStatusCode.NoContent);

        using HttpResponseMessage unknown = await client.PostAsJsonAsync(
            LogoutRoute,
            new RefreshTokenRequest { RefreshToken = "never-issued-" + Suffix() },
            ApiTestFixture.Json);

        unknown.StatusCode.Should().Be(HttpStatusCode.NoContent);

        using HttpResponseMessage repeated = await client.PostAsJsonAsync(
            LogoutRoute,
            new RefreshTokenRequest { RefreshToken = "never-issued-" + Suffix() },
            ApiTestFixture.Json);

        repeated.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    /// <summary>The caller's own snapshot requires credentials.</summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task Me_WithoutCredentials_ReturnsUnauthorized()
    {
        using HttpClient client = _fixture.CreateAnonymousClient();

        using HttpResponseMessage response = await client.GetAsync(MeRoute);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    /// <summary>The host's snapshot reports the host flag, its roles and its resolved permissions.</summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task Me_ForTheHostAccount_ReturnsOkWithTheHostFlag()
    {
        using HttpClient client = _fixture.CreateHostClient();

        using HttpResponseMessage response = await client.GetAsync(MeRoute);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        CurrentUserDto? me = await response.Content.ReadFromJsonAsync<CurrentUserDto>(ApiTestFixture.Json);

        me.Should().NotBeNull();
        me!.UserId.Should().Be(_fixture.Seed.HostUserId);
        me.PortalId.Should().Be(_fixture.Seed.PortalId);
        me.PortalName.Should().Be(IntegrationSeed.PortalName);
        me.Username.Should().Be(IntegrationSeed.HostUserName);
        me.IsSuperUser.Should().BeTrue();
        me.Roles.Should().Contain(IntegrationSeed.AdministratorsRoleName);
        me.Permissions.Should().NotBeNull();
    }

    /// <summary>
    /// The snapshot is read from the database rather than from the token, so a token for an account that no
    /// longer exists is reported as a missing account instead of being answered from its own claims.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task Me_WhenTheAccountNoLongerExists_ReturnsNotFound()
    {
        using HttpClient administrator = _fixture.CreateAdministratorClient();
        UserDetailDto account = await CreateUserAsync(administrator);

        using HttpClient bearer = _fixture.CreateClientFor(
            account.UserId,
            account.Username,
            _fixture.Seed.PortalId);

        using HttpResponseMessage before = await bearer.GetAsync(MeRoute);
        before.StatusCode.Should().Be(HttpStatusCode.OK);

        using HttpResponseMessage removed = await administrator.DeleteAsync(
            UserRoute(_fixture.Seed.PortalId, account.UserId));

        removed.StatusCode.Should().Be(HttpStatusCode.NoContent);

        using HttpResponseMessage after = await bearer.GetAsync(MeRoute);

        after.StatusCode.Should().Be(HttpStatusCode.NotFound);

        string body = await after.Content.ReadAsStringAsync();
        body.Should().Contain("urn:dnnmigration:error:auth.user_not_found");
    }

    /// <summary>
    /// Sign-in is rate limited per caller address, and a rejected attempt states how long to wait.
    /// </summary>
    /// <remarks>
    /// A second host is built for this test because the shared host runs with a deliberately permissive
    /// limit. The limiter reads its options while services are composed, so the override must be in force
    /// before the host is constructed - which is why the client is created inside the scope.
    /// </remarks>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task Login_IsRateLimitedPerCaller()
    {
        Dictionary<string, string?> tight = new(_fixture.HostConfiguration(), StringComparer.Ordinal)
        {
            ["RateLimiting__Authentication__PermitLimit"] =
                TightPermitLimit.ToString(CultureInfo.InvariantCulture),
            ["RateLimiting__Authentication__WindowSeconds"] = "60",
        };

        using (ApiTestFixture.OverrideEnvironment(tight))
        {
            await using var host = new RateLimitedHost();
            using HttpClient client = host.CreateClient();

            // The account is deliberately unknown, so the permitted attempts cost no bookkeeping on any
            // real account and cannot lock one out as a side effect of exhausting the window.
            var attempt = new LoginRequest
            {
                Username = "rate_limited_" + Suffix(),
                Password = ApiTestFixture.KnownPassword,
            };

            for (int permitted = 0; permitted < TightPermitLimit; permitted++)
            {
                using HttpResponseMessage allowed = await client.PostAsJsonAsync(
                    LoginRoute(_fixture.Seed.PortalId),
                    attempt,
                    ApiTestFixture.Json);

                allowed.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
            }

            using HttpResponseMessage rejected = await client.PostAsJsonAsync(
                LoginRoute(_fixture.Seed.PortalId),
                attempt,
                ApiTestFixture.Json);

            rejected.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
            rejected.Headers.RetryAfter.Should().NotBeNull();
        }
    }

    /// <summary>
    /// A host built with the same environment the fixture publishes, so that the only difference from the
    /// shared host is whatever the surrounding override changes.
    /// </summary>
    private sealed class RateLimitedHost : WebApplicationFactory<Program>
    {
        /// <inheritdoc />
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            ArgumentNullException.ThrowIfNull(builder);

            builder.UseEnvironment("Testing");
        }
    }

    /// <summary>Signs an account in with the shared known credential and returns the issued pair.</summary>
    /// <param name="client">An anonymous client.</param>
    /// <param name="userName">The account name.</param>
    /// <returns>The issued token pair.</returns>
    private async Task<LoginResponse> SignInAsync(HttpClient client, string userName)
    {
        using HttpResponseMessage response = await client.PostAsJsonAsync(
            LoginRoute(_fixture.Seed.PortalId),
            new LoginRequest { Username = userName, Password = ApiTestFixture.KnownPassword },
            ApiTestFixture.Json);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        return await ReadLoginAsync(response);
    }

    /// <summary>Creates an account through the API and returns its representation.</summary>
    /// <param name="client">A client holding the administrators role.</param>
    /// <param name="authorize">Whether the account is approved on creation.</param>
    /// <returns>The created account.</returns>
    private async Task<UserDetailDto> CreateUserAsync(HttpClient client, bool authorize = true)
    {
        string suffix = Suffix();

        using HttpResponseMessage response = await client.PostAsJsonAsync(
            UsersRoute(_fixture.Seed.PortalId),
            new CreateUserRequest
            {
                Username = "itest_auth_" + suffix,
                FirstName = "Integration",
                LastName = "Signin",
                DisplayName = "Integration Signin " + suffix,
                Email = "itest.auth." + suffix + "@example.com",
                Password = ApiTestFixture.KnownPassword,
                ConfirmPassword = ApiTestFixture.KnownPassword,
                Authorize = authorize,
            },
            ApiTestFixture.Json);

        response.StatusCode.Should().Be(HttpStatusCode.Created);

        UserDetailDto? created = await response.Content.ReadFromJsonAsync<UserDetailDto>(ApiTestFixture.Json);

        created.Should().NotBeNull();
        return created!;
    }

    /// <summary>Applies a lock to an account by writing the credential store directly.</summary>
    /// <param name="userName">The account name.</param>
    /// <returns>A task representing the write.</returns>
    /// <remarks>
    /// The lock is applied by writing rather than by exhausting the attempt window, so the test does not
    /// depend on the policy's threshold or on the sign-in rate limiter.
    /// </remarks>
    private async Task LockAsync(string userName)
    {
        int affected = await _fixture.Database.ExecuteAsync(
            """
            UPDATE am
            SET am.[IsLockedOut] = 1,
                am.[LastLockoutDate] = SYSUTCDATETIME(),
                am.[FailedPasswordAttemptCount] = 5
            FROM [dbo].[aspnet_Membership] am
            INNER JOIN [dbo].[aspnet_Users] au ON au.[UserId] = am.[UserId]
            WHERE au.[LoweredUserName] = LOWER(@userName);
            """,
            new Dictionary<string, object?> { ["userName"] = userName });

        affected.Should().Be(1);
    }

    /// <summary>Reads the stored credential hash for an account.</summary>
    /// <param name="userName">The account name.</param>
    /// <returns>The stored value, or <see langword="null"/> when no credential row exists.</returns>
    private async Task<string?> ReadStoredHashAsync(string userName)
    {
        string stored = await _fixture.Database.ScalarAsync<string>(
            """
            SELECT COALESCE(MAX(am.[Password]), N'')
            FROM [dbo].[aspnet_Membership] am
            INNER JOIN [dbo].[aspnet_Users] au ON au.[UserId] = am.[UserId]
            WHERE au.[LoweredUserName] = LOWER(@userName);
            """,
            new Dictionary<string, object?> { ["userName"] = userName });

        return stored.Length == 0 ? null : stored;
    }

    /// <summary>Overwrites the stored credential hash for an account.</summary>
    /// <param name="userName">The account name.</param>
    /// <param name="passwordHash">The value to store.</param>
    /// <returns>A task representing the write.</returns>
    private async Task WriteStoredHashAsync(string userName, string passwordHash)
    {
        int affected = await _fixture.Database.ExecuteAsync(
            """
            UPDATE am
            SET am.[Password] = @passwordHash
            FROM [dbo].[aspnet_Membership] am
            INNER JOIN [dbo].[aspnet_Users] au ON au.[UserId] = am.[UserId]
            WHERE au.[LoweredUserName] = LOWER(@userName);
            """,
            new Dictionary<string, object?>
            {
                ["userName"] = userName,
                ["passwordHash"] = passwordHash,
            });

        affected.Should().Be(1);
    }

    /// <summary>
    /// Reduces a problem document to the parts that describe the outcome, so two denials can be compared
    /// for indistinguishability without comparing the per-request trace identifier.
    /// </summary>
    /// <param name="response">The refused response.</param>
    /// <returns>The problem type, title, status and detail, joined.</returns>
    private static async Task<string> ReadDenialAsync(HttpResponseMessage response)
    {
        using System.Text.Json.JsonDocument document =
            System.Text.Json.JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        static string Read(System.Text.Json.JsonElement root, string name) =>
            root.TryGetProperty(name, out System.Text.Json.JsonElement value)
                ? value.ToString()
                : string.Empty;

        System.Text.Json.JsonElement problem = document.RootElement;

        return string.Join(
            '|',
            Read(problem, "type"),
            Read(problem, "title"),
            Read(problem, "status"),
            Read(problem, "detail"));
    }

    /// <summary>Reads a login representation from a response.</summary>
    /// <param name="response">The response.</param>
    /// <returns>The representation.</returns>
    private static async Task<LoginResponse> ReadLoginAsync(HttpResponseMessage response)
    {
        LoginResponse? issued = await response.Content.ReadFromJsonAsync<LoginResponse>(ApiTestFixture.Json);

        issued.Should().NotBeNull();
        return issued!;
    }

    /// <summary>The caller's own snapshot route.</summary>
    private static Uri MeRoute => new("/api/v1/auth/me", UriKind.Relative);

    /// <summary>The token exchange route.</summary>
    private static Uri RefreshRoute => new("/api/v1/auth/refresh", UriKind.Relative);

    /// <summary>The sign-out route.</summary>
    private static Uri LogoutRoute => new("/api/v1/auth/logout", UriKind.Relative);

    /// <summary>Builds the sign-in route naming a tenant explicitly.</summary>
    /// <param name="portalId">The tenant identifier.</param>
    /// <returns>A relative route.</returns>
    private static Uri LoginRoute(int portalId) =>
        new($"/api/v1/auth/login?portalId={Route(portalId)}", UriKind.Relative);

    /// <summary>Builds the collection route for a tenant's accounts.</summary>
    /// <param name="portalId">The tenant identifier.</param>
    /// <returns>A relative route.</returns>
    private static Uri UsersRoute(int portalId) =>
        new($"/api/v1/portals/{Route(portalId)}/users", UriKind.Relative);

    /// <summary>Builds the item route for one account.</summary>
    /// <param name="portalId">The tenant identifier.</param>
    /// <param name="userId">The account identifier.</param>
    /// <returns>A relative route.</returns>
    private static Uri UserRoute(int portalId, int userId) =>
        new($"/api/v1/portals/{Route(portalId)}/users/{Route(userId)}", UriKind.Relative);

    /// <summary>Formats an identifier for a route without picking up the ambient culture.</summary>
    /// <param name="value">The identifier.</param>
    /// <returns>The invariant representation.</returns>
    private static string Route(int value) => value.ToString(CultureInfo.InvariantCulture);

    /// <summary>Produces a short random suffix for values that reach a unique constraint.</summary>
    /// <returns>Twelve lower-case hexadecimal characters.</returns>
    private static string Suffix() => Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture)[..12];
}
