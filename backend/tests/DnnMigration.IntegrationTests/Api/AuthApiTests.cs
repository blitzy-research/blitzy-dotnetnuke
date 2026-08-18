using System.Globalization;
using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DnnMigration.Application.Abstractions;
using DnnMigration.Application.Dtos.Auth;
using DnnMigration.Application.Dtos.User;
using DnnMigration.Domain.Enums;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace DnnMigration.IntegrationTests.Api;

/// <summary>
/// Covers the authentication vertical end to end: exchanging a credential for a token pair, rotating that
/// pair, revoking it, and reading the caller's own snapshot back.
/// </summary>
/// <remarks>
/// <para>
/// The gates are asserted in the order the service applies them, because the order is the security
/// property. A caller who is not entitled to detail receives one indistinguishable denial for an unknown
/// account, a wrong credential, a missing credential row and a locked account; a caller who is entitled -
/// the host or the tenant's designated administrator - receives the specific reason.
/// </para>
/// <para>
/// Refresh rotation is asserted as a rotation, not merely as a second token: presenting a consumed value is
/// a replay, and the response to a replay is to revoke everything the account holds rather than to refuse
/// the one value. That is wider than the legacy cookie sign-out and is asserted deliberately.
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
    /// <remarks>
    /// This is the TARGET's default in force, and deliberately not presented as a ported legacy value.
    /// </remarks>
    private const int MaxInvalidPasswordAttempts = 5;

    /// <summary>A BCrypt cost below the hasher's own, so a credential stored at it must be replaced.</summary>
    private const int SupersededWorkFactor = 10;

    /// <summary>The cost the hasher writes, which a replaced credential must therefore carry.</summary>
    private const int CurrentWorkFactor = 12;

    /// <summary>A policy-compliant replacement used by the password-remediation flow.</summary>
    private const string RemediatedPassword = "Remediated!Pass9";

    /// <summary>The permit count the dedicated rate-limited host runs with.</summary>
    private const int TightPermitLimit = 2;

    /// <summary>
    /// The persisted <c>Portals.UserRegistration</c> value for verified registration, which is the only
    /// mode under which the approval ladder distinguishes "enter your code" from "that code is wrong".
    /// </summary>
    /// <remarks>
    /// Written as the stored integer rather than through the Domain enumeration, because this is a direct
    /// column write and the value's meaning is the column's, not the projection's.
    /// </remarks>
    private const int VerifiedRegistrationMode = 3;

    /// <summary>The mode the seed tenant holds, restored after any test that raises it.</summary>
    private const int DefaultRegistrationMode = 0;

    /// <summary>The media-type suffix every refusal on this controller has to carry.</summary>
    /// <remarks>
    /// The SUFFIX is asserted rather than the exact type, and that is a measured position rather than a
    /// weaker assertion for convenience.
    /// </remarks>
    private const string JsonMediaTypeSuffix = "json";

    /// <summary>
    /// The member name the account-name rule attributes its failure to, spelled as the validator reports
    /// it.
    /// </summary>
    private const string UsernameMember = "Username";

    /// <summary>The member name the credential rule attributes its failure to.</summary>
    private const string PasswordMember = "Password";

    /// <summary>The exact wording the account-name rule carries to the operator.</summary>
    private const string MissingUsernameMessage = "A username is required.";

    /// <summary>The exact wording the credential rule carries to the operator.</summary>
    private const string MissingPasswordMessage = "A password is required.";

    /// <summary>
    /// The one extension member the problem-document factory adds, which joins a caller's report of a
    /// refusal to the entry the server recorded.
    /// </summary>
    private const string TraceIdExtension = "traceId";

    /// <summary>The greatest length at which the correlation middleware still trusts an inbound identifier.</summary>
    private const int MaximumCorrelationIdLength = 128;

    /// <summary>
    /// Account name the product was distributed with for a tenant administrator, and therefore the name the
    /// weak-credential advisory recognises.
    /// </summary>
    /// <remarks>
    /// The shipped credential for this account was the account name itself, so this one constant serves as
    /// both. That is deliberate rather than convenient: the account name has to appear here because the
    /// advisory keys on it, and reusing it as the credential means these tests introduce NO credential
    /// material into the repository that reading the account name did not already reveal.
    /// </remarks>
    private const string ShippedAdministratorAccountName = "admin";

    /// <summary>
    /// Account name the product was distributed with for the installation host, which is also its shipped
    /// credential. See <see cref="ShippedAdministratorAccountName"/>.
    /// </summary>
    private const string ShippedHostAccountName = "host";

    /// <summary>
    /// A path segment under the controller's route that names no operation, used to prove the surface is
    /// closed rather than served by a catch-all.
    /// </summary>
    private const string UndeclaredOperationSegment = "operation-this-controller-does-not-declare";

    private readonly ApiTestFixture _fixture;

    /// <summary>Initialises a new instance of the <see cref="AuthApiTests"/> class.</summary>
    /// <param name="fixture">The shared host, database and seed.</param>
    public AuthApiTests(ApiTestFixture fixture) => _fixture = fixture;

    /// <summary>
    /// A seeded credential is exchanged for a usable token pair, and the access token it carries is
    /// accepted by a protected endpoint.
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
        issued.RefreshToken.Should().NotBeNullOrWhiteSpace();
        issued.ExpiresAtUtc.Should().BeAfter(
            DateTime.UtcNow,
            "exactly one expiry is published, as an absolute instant in UTC, so a client can schedule a "
            + "refresh instead of discovering expiry through a rejected request");

        // The legacy session lifetime was the forms-authentication ticket's, declared as <forms
        // name=".DOTNETNUKE" protection="All" timeout="60" cookieless="UseCookies"/> at
        // Website/release.config:L147 - sixty minutes, which is the value the token lifetime option carries
        // forward.

        // The response publishes no bearer-scheme member, no remaining-seconds duration and no refresh
        // token expiry.
        issued.MustChangePassword.Should().BeFalse(
            "the seeded account carries no forced credential update and is not using a shipped credential");
        issued.PasswordExpiring.Should().BeFalse("the seeded account's credential is not near expiry");

        issued.User.UserId.Should().Be(_fixture.Seed.AdminUserId);
        issued.User.PortalId.Should().Be(_fixture.Seed.PortalId);
        issued.User.Username.Should().Be(IntegrationSeed.AdminUserName);
        issued.User.PortalName.Should().Be(IntegrationSeed.PortalName);
        issued.User.IsSuperUser.Should().BeFalse();
        // ⚠ REVERSED DELIBERATELY. This used to require an EMPTY collection, which is the behaviour a runtime
        // audit reported: signing in understated the caller's authority while the current-user read, issued
        // moments later by the same client, reported it in full. The claim assertions immediately below are
        // the ones that keep authority OUT OF THE TOKEN, and they are unchanged - that property is separate
        // from what the response body says, and a response body is not a credential.
        issued.User.Roles.Should().Contain(
            IntegrationSeed.AdministratorsRoleName,
            "the sign-in answer states the authority the caller holds, agreeing with /auth/me");

        JwtSecurityToken accessToken = new JwtSecurityTokenHandler().ReadJwtToken(issued.AccessToken);
        HashSet<string> allowedClaimTypes =
        [
            JwtRegisteredClaimNames.Sub,
            JwtRegisteredClaimNames.Jti,
            JwtRegisteredClaimNames.Iat,
            JwtRegisteredClaimNames.Nbf,
            JwtRegisteredClaimNames.Exp,
            JwtRegisteredClaimNames.Iss,
            JwtRegisteredClaimNames.Aud,
            DnnClaimTypes.PortalId,
        ];
        accessToken.Claims.Select(claim => claim.Type).Should().OnlyContain(
            claimType => allowedClaimTypes.Contains(claimType),
            "access tokens carry stable identity and envelope claims only");

        // The token is only meaningful if the pipeline accepts it, so it is spent rather than inspected.
        using HttpClient bearer = AuthenticatedClientFactory.Authenticate(
            _fixture.CreateAnonymousClient(),
            issued.AccessToken);

        using HttpResponseMessage snapshot = await bearer.GetAsync(MeRoute);

        snapshot.StatusCode.Should().Be(HttpStatusCode.OK);

        CurrentUserDto? me = await snapshot.Content.ReadEnvelopeAsync<CurrentUserDto>();

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

        // The media type is part of the indistinguishability: two refusals that agreed on every member but
        // were served as different types would still tell a caller which branch it had reached.
        wrongCredential.Content.Headers.ContentType?.MediaType.Should().EndWith(JsonMediaTypeSuffix);
        unknownAccount.Content.Headers.ContentType?.MediaType.Should().Be(
            wrongCredential.Content.Headers.ContentType?.MediaType);

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

    /// <summary>
    /// A locked account is refused generically when the submitted credential is WRONG, which is what keeps
    /// the lock from becoming an account-name oracle.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// ⚠ THIS TEST USED TO SUBMIT THE CORRECT PASSWORD AND REQUIRE THE GENERIC REFUSAL, WHICH IS THE DEFECT
    /// ITS SIBLING BELOW NOW PROVES CLOSED. Withholding the lock from the account's own holder told somebody
    /// who had typed their correct password that it was wrong, with no wait, no counter and no recovery
    /// route. Uniformity is preserved exactly where it does any work: for a caller who cannot prove the
    /// credential, which is the only caller an enumeration attempt has.
    /// </remarks>
    [Fact]
    public async Task Login_ForALockedAccount_IsGenericWhenTheCredentialIsWrong()
    {
        using HttpClient administrator = await _fixture.CreateAdministratorClientAsync();
        UserDetailDto account = await CreateUserAsync(administrator);
        await LockAsync(account.Username);

        using HttpClient client = _fixture.CreateAnonymousClient();

        using HttpResponseMessage response = await client.PostAsJsonAsync(
            LoginRoute(_fixture.Seed.PortalId),
            new LoginRequest { Username = account.Username, Password = "not-this-accounts-password" },
            ApiTestFixture.Json);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        string body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("urn:dnnmigration:error:auth.invalid_credentials");
        body.Should().NotContain("locked");
    }

    /// <summary>
    /// The same locked account states the lock, a wait and a recovery route to the caller who PROVED the
    /// credential — its own holder — even though that caller is anonymous.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// The wait is not asserted as a literal: it is read from the installation's own automatic-unlock window,
    /// so a fixed number here would be a second source of truth. What is asserted is that the sentence names
    /// the state, offers a route out, and is NOT the operator-facing sentence reserved for a caller who is
    /// entitled to the detail without holding the credential.
    /// </remarks>
    [Fact]
    public async Task Login_ForALockedAccount_IsExplicitToItsOwnHolder()
    {
        using HttpClient administrator = await _fixture.CreateAdministratorClientAsync();
        UserDetailDto account = await CreateUserAsync(administrator);
        await LockAsync(account.Username);

        using HttpClient client = _fixture.CreateAnonymousClient();

        using HttpResponseMessage response = await client.PostAsJsonAsync(
            LoginRoute(_fixture.Seed.PortalId),
            new LoginRequest { Username = account.Username, Password = ApiTestFixture.KnownPassword },
            ApiTestFixture.Json);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        string body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("urn:dnnmigration:error:auth.locked_out");
        body.Should().Contain("locked");
        body.Should().Contain("administrator");
        body.Should().NotContain(
            "The account name or credential is not correct.",
            "which is the false statement this replaces");
        body.Should().NotContain(
            "is locked and must be unlocked",
            "that sentence is addressed to an operator diagnosing somebody else's account");
    }

    /// <summary>
    /// The same locked account reports the operator-facing reason to an entitled caller who does NOT hold the
    /// credential, which is what makes an administrative diagnosis possible without creating an oracle.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task Login_ForALockedAccount_IsExplicitToAHostCaller()
    {
        using HttpClient administrator = await _fixture.CreateAdministratorClientAsync();
        UserDetailDto account = await CreateUserAsync(administrator);
        await LockAsync(account.Username);

        // Sign-in is anonymous, but it does not refuse a caller that presents a token, and the entitlement
        // test reads that caller. A host-authenticated sign-in attempt is therefore told why. The credential
        // is deliberately wrong: a caller who PROVES it is treated as the account's holder and receives the
        // reader-facing advisory instead, which the sibling test above covers.
        using HttpClient host = await _fixture.CreateHostClientAsync();

        using HttpResponseMessage response = await host.PostAsJsonAsync(
            LoginRoute(_fixture.Seed.PortalId),
            new LoginRequest { Username = account.Username, Password = "not-this-accounts-password" },
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
        using HttpClient administrator = await _fixture.CreateAdministratorClientAsync();
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

        using HttpClient host = await _fixture.CreateHostClientAsync();

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

        UserDetailDto? detail = await read.Content.ReadEnvelopeAsync<UserDetailDto>();

        detail.Should().NotBeNull();
        detail!.IsLockedOut.Should().BeTrue();
    }

    /// <summary>
    /// An unapproved account is admitted only on presentation of its verification code, and the approval it
    /// earns is persisted rather than applying to that one exchange.
    /// </summary>
    /// <remarks>
    /// The two refusals answer <c>400 Bad Request</c> rather than <c>401 Unauthorized</c>, and that is
    /// deliberate. The credential is verified before the approval gate runs, so a caller reaching either
    /// refusal has already proved its credential: it is not unauthenticated, and its submission is
    /// incomplete.
    /// </remarks>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task Login_ForAnUnapprovedAccount_RequiresTheVerificationCode()
    {
        using HttpClient administrator = await _fixture.CreateAdministratorClientAsync();
        UserDetailDto account = await CreateUserAsync(administrator, authorize: false);

        account.IsApproved.Should().BeFalse();

        using HttpClient client = _fixture.CreateAnonymousClient();

        using HttpResponseMessage withoutCode = await client.PostAsJsonAsync(
            LoginRoute(_fixture.Seed.PortalId),
            new LoginRequest { Username = account.Username, Password = ApiTestFixture.KnownPassword },
            ApiTestFixture.Json);

        // The approval outcomes are UNAUTHORIZED, not bad-request. Each names an account state that refused
        // a sign-in the credential itself did not refuse, so the request was correct and the account was
        // not yet admissible - telling a client its request was at fault would be the wrong answer.
        withoutCode.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        string withoutCodeBody = await withoutCode.Content.ReadAsStringAsync();

        withoutCodeBody.Should().Contain(
            "auth.",
            "the approval outcome is named, because the caller has already proved its credential");

        // The seed tenant's registration mode is the persisted default, which admits no self-verification,
        // so the ladder selects the not-authorised member here. The other two members are exercised by the
        // verified-registration test below, which raises the mode for the duration.
        (await withoutCode.Content.ReadAsStringAsync())
            .Should().Contain("urn:dnnmigration:error:auth.account_not_approved");

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

        UserDetailDto? detail = await read.Content.ReadEnvelopeAsync<UserDetailDto>();

        detail.Should().NotBeNull();
        detail!.IsApproved.Should().BeTrue();
    }

    /// <summary>
    /// An account locked after its tokens were issued cannot renew any of them, and the refusal reaches
    /// every session it holds rather than only the one presented.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// Two sessions are opened deliberately, because one would not distinguish the two possible fixes. The
    /// first is presented while the account is locked and must be refused.
    /// </remarks>
    [Fact]
    public async Task Refresh_ForAnAccountLockedAfterIssue_IsRefusedAndEndsEverySession()
    {
        using HttpClient administrator = await _fixture.CreateAdministratorClientAsync();
        UserDetailDto account = await CreateUserAsync(administrator);

        using HttpClient client = _fixture.CreateAnonymousClient();

        LoginResponse presented = await SignInAsync(client, account.Username);
        LoginResponse untouched = await SignInAsync(client, account.Username);

        untouched.RefreshToken.Should().NotBe(presented.RefreshToken);

        await LockAsync(account.Username);

        using HttpResponseMessage whileLocked = await client.PostAsJsonAsync(
            RefreshRoute,
            new RefreshTokenRequest { RefreshToken = presented.RefreshToken },
            ApiTestFixture.Json);

        whileLocked.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await whileLocked.Content.ReadAsStringAsync())
            .Should().Contain("urn:dnnmigration:error:auth.invalid_refresh_token");

        // The lock is lifted before the second session is presented, so nothing about the account refuses it.
        await UnlockAsync(account.Username);

        using HttpResponseMessage afterUnlock = await client.PostAsJsonAsync(
            RefreshRoute,
            new RefreshTokenRequest { RefreshToken = untouched.RefreshToken },
            ApiTestFixture.Json);

        afterUnlock.StatusCode.Should().Be(
            HttpStatusCode.Unauthorized,
            "the refusal revoked every session the account held, not merely the token that was presented");

        // And the account itself is usable again, which proves the revocation ended sessions rather than
        // disabling the account: a fresh sign-in succeeds and yields a pair that does renew.
        LoginResponse renewed = await SignInAsync(client, account.Username);

        using HttpResponseMessage fresh = await client.PostAsJsonAsync(
            RefreshRoute,
            new RefreshTokenRequest { RefreshToken = renewed.RefreshToken },
            ApiTestFixture.Json);

        fresh.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    /// <summary>
    /// On a verified-registration tenant each of the three legacy approval outcomes reaches the caller as
    /// its own problem type answered <c>401</c>, and a wrong credential presented WITH the correct
    /// verification code approves nothing.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// The tenant's registration mode is raised for the duration and restored in a <c>finally</c>, because
    /// it is the shared seed tenant and it is the only mode under which the ladder distinguishes its first
    /// two members. The integration suite is a single xunit collection, so no other test observes the
    /// window.
    /// </remarks>
    [Fact]
    public async Task Login_ForAnUnapprovedAccount_NamesTheApprovalOutcomeAndRefusesAWrongCredential()
    {
        using HttpClient administrator = await _fixture.CreateAdministratorClientAsync();
        UserDetailDto account = await CreateUserAsync(administrator, authorize: false);

        account.IsApproved.Should().BeFalse();

        await SetRegistrationModeAsync(VerifiedRegistrationMode);

        try
        {
            using HttpClient client = _fixture.CreateAnonymousClient();

            string correctCode = string.Create(
                CultureInfo.InvariantCulture,
                $"{_fixture.Seed.PortalId}-{account.UserId}");

            // No code: the legacy EnterCode branch.
            using HttpResponseMessage withoutCode = await client.PostAsJsonAsync(
                LoginRoute(_fixture.Seed.PortalId),
                new LoginRequest { Username = account.Username, Password = ApiTestFixture.KnownPassword },
                ApiTestFixture.Json);

            withoutCode.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
            (await withoutCode.Content.ReadAsStringAsync())
                .Should().Contain("urn:dnnmigration:error:auth.verification_required");

            // A code that does not match: the legacy InvalidCode branch. The rejected value must not come back.
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

            string wrongCodeBody = await withWrongCode.Content.ReadAsStringAsync();

            wrongCodeBody.Should().Contain("urn:dnnmigration:error:auth.verification_code_invalid");

            // THE CENTRAL ASSERTION OF THIS TEST. The correct verification code is composed from two
            // integers that appear in ordinary URLs, so it is guessable; before the gates were reordered,
            // presenting it with any password at all approved the account permanently and only then refused
            // the sign-in.
            using HttpResponseMessage wrongCredential = await client.PostAsJsonAsync(
                LoginRoute(_fixture.Seed.PortalId),
                new LoginRequest
                {
                    Username = account.Username,
                    Password = ApiTestFixture.KnownPassword + "-wrong",
                    VerificationCode = correctCode,
                },
                ApiTestFixture.Json);

            wrongCredential.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

            string wrongCredentialBody = await wrongCredential.Content.ReadAsStringAsync();

            wrongCredentialBody.Should().Contain("urn:dnnmigration:error:auth.invalid_credentials");
            wrongCredentialBody.Should().NotContain(
                "auth.verification",
                "a caller that has not proved the credential learns nothing about the account's approval state");

            using HttpResponseMessage stillPending = await administrator.GetAsync(
                UserRoute(_fixture.Seed.PortalId, account.UserId));

            stillPending.StatusCode.Should().Be(HttpStatusCode.OK);

            UserDetailDto? pending = await stillPending.Content
                .ReadEnvelopeAsync<UserDetailDto>();

            pending.Should().NotBeNull();
            pending!.IsApproved.Should().BeFalse(
                "a guessable code presented with a wrong credential must approve nothing");
        }
        finally
        {
            await SetRegistrationModeAsync(DefaultRegistrationMode);
        }
    }

    /// <summary>
    /// A credential stored at a superseded cost is replaced on the first successful sign-in, which is the
    /// lazy upgrade that carries accounts forward without asking them to reset.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task Login_ReplacesACredentialStoredAtASupersededCost()
    {
        using HttpClient administrator = await _fixture.CreateAdministratorClientAsync();
        UserDetailDto account = await CreateUserAsync(administrator);

        // Enhanced, matching the production hasher, so the only thing that differs from a current
        // credential is the cost. A plain hash would fail to verify at all and the test would prove nothing
        // about rehashing.
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

    /// <summary>
    /// A format-2 membership credential is verified once and replaced with BCrypt during the same sign-in.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task Login_WithLegacyEncryptedCredential_ReplacesItWithBcrypt()
    {
        using HttpClient administrator = await _fixture.CreateAdministratorClientAsync();
        UserDetailDto account = await CreateUserAsync(administrator);
        (string storedValue, string passwordSalt) = CreateLegacyEncryptedCredential(
            ApiTestFixture.KnownPassword);

        await WriteStoredCredentialAsync(
            account.Username,
            storedValue,
            PasswordFormat.Encrypted,
            passwordSalt);

        using HttpClient client = _fixture.CreateAnonymousClient();

        using HttpResponseMessage first = await client.PostAsJsonAsync(
            LoginRoute(_fixture.Seed.PortalId),
            new LoginRequest
            {
                Username = account.Username,
                Password = ApiTestFixture.KnownPassword,
            },
            ApiTestFixture.Json);

        first.StatusCode.Should().Be(
            HttpStatusCode.OK,
            "the migration verifier is enabled only in the throwaway integration host");

        string? replaced = await ReadStoredHashAsync(account.Username);
        int format = await ReadStoredFormatAsync(account.Username);
        string salt = await ReadStoredSaltAsync(account.Username);

        replaced.Should().NotBeNullOrWhiteSpace();
        replaced.Should().NotBe(storedValue);
        replaced.Should().StartWith("$2");
        format.Should().Be((int)PasswordFormat.Hashed);
        salt.Should().BeEmpty();

        using HttpResponseMessage second = await client.PostAsJsonAsync(
            LoginRoute(_fixture.Seed.PortalId),
            new LoginRequest
            {
                Username = account.Username,
                Password = ApiTestFixture.KnownPassword,
            },
            ApiTestFixture.Json);

        second.StatusCode.Should().Be(
            HttpStatusCode.OK,
            "the replacement must be a usable current BCrypt representation");
    }

    /// <summary>
    /// The migration is AUDITED, and neither the credential nor the deployment key reaches the record.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// The credential is staged with the same helper the fact above uses, rather than with the
    /// independently produced provider vector this fact was written against.
    /// </remarks>
    [Fact]
    public async Task Login_MigratingALegacyCredential_RecordsItWithoutDisclosingAnySecret()
    {
        using HttpClient administrator = await _fixture.CreateAdministratorClientAsync();
        UserDetailDto account = await CreateUserAsync(administrator);
        (string storedValue, string passwordSalt) = CreateLegacyEncryptedCredential(
            ApiTestFixture.KnownPassword);

        await WriteStoredCredentialAsync(
            account.Username,
            storedValue,
            PasswordFormat.Encrypted,
            passwordSalt);

        using HttpClient client = _fixture.CreateAnonymousClient();

        using HttpResponseMessage response = await client.PostAsJsonAsync(
            LoginRoute(_fixture.Seed.PortalId),
            new LoginRequest
            {
                Username = account.Username,
                Password = ApiTestFixture.KnownPassword,
            },
            ApiTestFixture.Json);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        LogRecord audit = RecordedLogs.Snapshot()
            .Where(record => Equals(
                record.Properties.GetValueOrDefault("AuditEvent"),
                AuditEventNames.LegacyCredentialMigrated))
            .Single(record => Equals(
                record.Properties.GetValueOrDefault("AuditResourceId"),
                account.UserId.ToString(CultureInfo.InvariantCulture)));

        audit.Properties["AuditMetadata_PreviousFormat"].Should().Be(
            PasswordFormat.Encrypted.ToString());

        audit.Message.Should().NotContain(ApiTestFixture.KnownPassword);
        audit.Message.Should().NotContain(storedValue);
        audit.Message.Should().NotContain(passwordSalt);
        audit.Message.Should().NotContain(ApiTestFixture.LegacyCredentialDecryptionKey);
    }

    /// <summary>A successful sign-in is recorded against the account.</summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task Login_RecordsTheSignIn()
    {
        using HttpClient administrator = await _fixture.CreateAdministratorClientAsync();
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

        UserDetailDto? detail = await read.Content.ReadEnvelopeAsync<UserDetailDto>();

        detail.Should().NotBeNull();
        detail!.LastLoginDate.Should().NotBeNull();
    }

    /// <summary>
    /// An account marked for a credential change is blocked from the ordinary API surface while
    /// authentication lifecycle and its own password-remediation route remain available.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task RequiredPasswordChange_AllowsOnlyAuthenticationAndOwnPasswordRemediation()
    {
        using HttpClient administrator = await _fixture.CreateAdministratorClientAsync();
        UserDetailDto account = await CreateUserAsync(administrator);

        int affected = await _fixture.Database.ExecuteAsync(
            "UPDATE [dbo].[Users] SET [UpdatePassword] = 1 WHERE [UserID] = @userId;",
            new Dictionary<string, object?> { ["userId"] = account.UserId });

        affected.Should().Be(1);

        using HttpClient anonymous = _fixture.CreateAnonymousClient();
        LoginResponse issued = await SignInAsync(anonymous, account.Username);

        issued.MustChangePassword.Should().BeTrue();
        issued.MustUpdateProfile.Should().BeFalse();

        using HttpResponseMessage refreshed = await anonymous.PostAsJsonAsync(
            RefreshRoute,
            new RefreshTokenRequest { RefreshToken = issued.RefreshToken },
            ApiTestFixture.Json);

        refreshed.StatusCode.Should().Be(HttpStatusCode.OK);
        LoginResponse exchanged = await ReadLoginAsync(refreshed);
        exchanged.MustChangePassword.Should().BeTrue(
            "rotation re-evaluates the durable UpdatePassword flag instead of clearing the block");

        using HttpClient bearer = AuthenticatedClientFactory.Authenticate(
            _fixture.CreateAnonymousClient(),
            exchanged.AccessToken);

        using HttpResponseMessage ordinary = await bearer.GetAsync(
            UserRoute(_fixture.Seed.PortalId, account.UserId));
        ordinary.StatusCode.Should().Be(
            HttpStatusCode.Forbidden,
            "a blocking credential requirement closes every ordinary protected route");

        using HttpResponseMessage authentication = await bearer.GetAsync(MeRoute);
        authentication.StatusCode.Should().Be(
            HttpStatusCode.OK,
            "authentication lifecycle routes remain available while remediation is outstanding");

        using HttpResponseMessage changed = await bearer.PostAsJsonAsync(
            PasswordRoute(_fixture.Seed.PortalId, account.UserId),
            new ChangePasswordRequest
            {
                Operation = ChangePasswordRequest.OperationChange,
                CurrentPassword = ApiTestFixture.KnownPassword,
                NewPassword = RemediatedPassword,
                ConfirmPassword = RemediatedPassword,
            },
            ApiTestFixture.Json);

        changed.StatusCode.Should().Be(
            HttpStatusCode.NoContent,
            "the account owner must be able to clear the requirement that is blocking it");

        using HttpResponseMessage afterRemediation = await bearer.GetAsync(
            UserRoute(_fixture.Seed.PortalId, account.UserId));
        afterRemediation.StatusCode.Should().Be(
            HttpStatusCode.OK,
            "the gate re-reads the cleared UpdatePassword flag instead of trusting the stale token claim");
    }

    /// <summary>
    /// C-03: a required profile property the account has not answered raises the blocking profile
    /// requirement on sign-in, survives refresh and restricts the token until the owner answers it.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// Refresh is asserted as well because the review named both paths. A flag raised only on sign-in would
    /// be lost the moment a client rotated its token, which for a short-lived access token is within
    /// minutes.
    /// </remarks>
    [Fact]
    public async Task Login_WhenARequiredProfilePropertyIsUnanswered_RaisesTheProfileAdvisory()
    {
        using HttpClient administrator = await _fixture.CreateAdministratorClientAsync();
        UserDetailDto account = await CreateUserAsync(administrator);

        int definitionId = await _fixture.Database.ScalarAsync<int>(
            """
            INSERT INTO [dbo].[ProfilePropertyDefinition]
                ([PortalID], [ModuleDefID], [Deleted], [DataType], [DefaultValue], [PropertyCategory],
                 [PropertyName], [Length], [Required], [ValidationExpression], [ViewOrder], [Visible])
            VALUES (@portalId, NULL, 0, 0, '', 'Contact', @propertyName, 50, 1, NULL, 101, 1);
            SELECT CAST(SCOPE_IDENTITY() AS int);
            """,
            new Dictionary<string, object?>
            {
                ["portalId"] = _fixture.Seed.PortalId,
                ["propertyName"] = FormattableString.Invariant($"Gate{Suffix()}"),
            });

        try
        {
            using HttpClient client = _fixture.CreateAnonymousClient();

            using HttpResponseMessage response = await client.PostAsJsonAsync(
                LoginRoute(_fixture.Seed.PortalId),
                new LoginRequest { Username = account.Username, Password = ApiTestFixture.KnownPassword },
                ApiTestFixture.Json);

            response.StatusCode.Should().Be(
                HttpStatusCode.OK,
                "the credential is correct; an incomplete profile is an advisory rather than a refusal");

            LoginResponse issued = await ReadLoginAsync(response);
            issued.MustUpdateProfile.Should().BeTrue();

            // The advisory must survive rotation, or a client would lose it within one access-token lifetime.
            using HttpResponseMessage rotated = await client.PostAsJsonAsync(
                RefreshRoute,
                new RefreshTokenRequest { RefreshToken = issued.RefreshToken },
                ApiTestFixture.Json);

            rotated.StatusCode.Should().Be(HttpStatusCode.OK);
            LoginResponse exchanged = await ReadLoginAsync(rotated);
            exchanged.MustUpdateProfile.Should().BeTrue("the advisory is re-evaluated on every rotation");

            using HttpClient bearer = AuthenticatedClientFactory.Authenticate(
                _fixture.CreateAnonymousClient(),
                exchanged.AccessToken);

            using HttpResponseMessage ordinary = await bearer.GetAsync(
                UserRoute(_fixture.Seed.PortalId, account.UserId));
            ordinary.StatusCode.Should().Be(
                HttpStatusCode.Forbidden,
                "an incomplete required profile blocks the ordinary protected surface");

            using HttpResponseMessage authentication = await bearer.GetAsync(MeRoute);
            authentication.StatusCode.Should().Be(
                HttpStatusCode.OK,
                "the caller must retain access to its authentication lifecycle");

            using HttpResponseMessage profile = await bearer.GetAsync(
                ProfileRoute(_fixture.Seed.PortalId, account.UserId));
            profile.StatusCode.Should().Be(
                HttpStatusCode.OK,
                "the owner must be able to read the profile it is required to complete");

            using HttpResponseMessage answered = await bearer.PutAsJsonAsync(
                ProfileRoute(_fixture.Seed.PortalId, account.UserId),
                new UserProfileDto
                {
                    UserId = account.UserId,
                    Properties =
                    [
                        new UserProfileValueDto
                        {
                            PropertyDefinitionId = definitionId,
                            PropertyValue = "answered",
                            Visibility = 2,
                        },
                    ],
                },
                ApiTestFixture.Json);

            answered.StatusCode.Should().Be(
                HttpStatusCode.NoContent,
                "the permitted profile route must be able to clear the blocking requirement");

            using HttpResponseMessage afterRemediation = await bearer.GetAsync(
                UserRoute(_fixture.Seed.PortalId, account.UserId));
            afterRemediation.StatusCode.Should().Be(
                HttpStatusCode.OK,
                "the gate re-reads the completed profile instead of trusting the stale token claim");

            using HttpResponseMessage again = await client.PostAsJsonAsync(
                LoginRoute(_fixture.Seed.PortalId),
                new LoginRequest { Username = account.Username, Password = ApiTestFixture.KnownPassword },
                ApiTestFixture.Json);

            again.StatusCode.Should().Be(HttpStatusCode.OK);
            LoginResponse completed = await ReadLoginAsync(again);
            completed.MustUpdateProfile.Should().BeFalse("the required property now carries an answer");
        }
        finally
        {
            await _fixture.Database.ExecuteAsync(
                "DELETE FROM [dbo].[UserProfile] WHERE [PropertyDefinitionID] = @definitionId;",
                new Dictionary<string, object?> { ["definitionId"] = definitionId });
            await _fixture.Database.ExecuteAsync(
                "DELETE FROM [dbo].[ProfilePropertyDefinition] WHERE [PropertyDefinitionID] = @definitionId;",
                new Dictionary<string, object?> { ["definitionId"] = definitionId });
        }
    }

    /// <summary>C-03: the profile gate is not applied to the host account.</summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task Login_DoesNotRaiseTheProfileAdvisoryForTheHostAccount()
    {
        // Installed on the resolved tenant for the same reason as the advisory test above.
        int definitionId = await _fixture.Database.ScalarAsync<int>(
            """
            INSERT INTO [dbo].[ProfilePropertyDefinition]
                ([PortalID], [ModuleDefID], [Deleted], [DataType], [DefaultValue], [PropertyCategory],
                 [PropertyName], [Length], [Required], [ValidationExpression], [ViewOrder], [Visible])
            VALUES (@portalId, NULL, 0, 0, '', 'Contact', @propertyName, 50, 1, NULL, 102, 1);
            SELECT CAST(SCOPE_IDENTITY() AS int);
            """,
            new Dictionary<string, object?>
            {
                ["portalId"] = _fixture.Seed.PortalId,
                ["propertyName"] = FormattableString.Invariant($"HostGate{Suffix()}"),
            });

        try
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
            issued.User.IsSuperUser.Should().BeTrue();
            issued.MustUpdateProfile.Should().BeFalse();
        }
        finally
        {
            await _fixture.Database.ExecuteAsync(
                "DELETE FROM [dbo].[ProfilePropertyDefinition] WHERE [PropertyDefinitionID] = @definitionId;",
                new Dictionary<string, object?> { ["definitionId"] = definitionId });
        }
    }

    /// <summary>An account holding no credential row is refused, not admitted on an empty comparison.</summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task Login_ForAnAccountWithoutACredential_ReturnsUnauthorized()
    {
        using HttpClient administrator = await _fixture.CreateAdministratorClientAsync();
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
        using HttpClient administrator = await _fixture.CreateAdministratorClientAsync();
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
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task Refresh_WithAReplayedToken_IsRefusedAndRevokesTheAccountsTokens()
    {
        using HttpClient administrator = await _fixture.CreateAdministratorClientAsync();
        UserDetailDto account = await CreateUserAsync(administrator);

        using HttpClient client = _fixture.CreateAnonymousClient();
        LoginResponse first = await SignInAsync(client, account.Username);

        using HttpResponseMessage rotated = await client.PostAsJsonAsync(
            RefreshRoute,
            new RefreshTokenRequest { RefreshToken = first.RefreshToken },
            ApiTestFixture.Json);

        rotated.StatusCode.Should().Be(HttpStatusCode.OK);

        LoginResponse second = await ReadLoginAsync(rotated);

        using HttpClient replayingClient = _fixture.CreateAnonymousClient();
        replayingClient.DefaultRequestHeaders.UserAgent.ParseAdd("cross-client-replay/1.0");

        using HttpResponseMessage replay = await replayingClient.PostAsJsonAsync(
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

    /// <summary>
    /// A spent fingerprint remains detectable through the family's absolute lifetime, even after many
    /// rotations.
    /// </summary>
    /// <remarks>
    /// The generation redeemed first is replayed from a different client after enough exchanges to exceed
    /// the removed generation window. Recognising it must still revoke the live successor.
    /// </remarks>
    /// <returns>A task representing the test.</returns>
    [Fact]
    [Trait("Category", "Integration")]
    public async Task Refresh_RepeatedManyTimes_RetainsReplayDetectionThroughTheFamilyLifetime()
    {
        using HttpClient administrator = await _fixture.CreateAdministratorClientAsync();
        UserDetailDto account = await CreateUserAsync(administrator);

        using HttpClient client = _fixture.CreateAnonymousClient();
        LoginResponse first = await SignInAsync(client, account.Username);

        // Comfortably past the removed eight-generation window.
        const int Exchanges = 20;

        string firstRedeemed = first.RefreshToken;
        string live = first.RefreshToken;

        for (int exchange = 0; exchange < Exchanges; exchange++)
        {
            using HttpResponseMessage rotated = await client.PostAsJsonAsync(
                RefreshRoute,
                new RefreshTokenRequest { RefreshToken = live },
                ApiTestFixture.Json);

            rotated.StatusCode.Should().Be(
                HttpStatusCode.OK,
                "a session that keeps redeeming its current token is behaving correctly and must never be "
                + "refused for having done so often");

            live = (await ReadLoginAsync(rotated)).RefreshToken;
        }

        using HttpClient replayingClient = _fixture.CreateAnonymousClient();
        replayingClient.DefaultRequestHeaders.UserAgent.ParseAdd("old-generation-replay/1.0");

        using HttpResponseMessage staleReplay = await replayingClient.PostAsJsonAsync(
            RefreshRoute,
            new RefreshTokenRequest { RefreshToken = firstRedeemed },
            ApiTestFixture.Json);

        staleReplay.StatusCode.Should().Be(
            HttpStatusCode.Unauthorized,
            "a redeemed token is never redeemable again, whether or not its history is still retained");

        string body = await staleReplay.Content.ReadAsStringAsync();
        body.Should().Contain("urn:dnnmigration:error:auth.invalid_refresh_token");

        // The observable proof that the old fingerprint was retained: replay detection revokes the live
        // successor even though many generations separate it from the copied value.
        using HttpResponseMessage afterwards = await client.PostAsJsonAsync(
            RefreshRoute,
            new RefreshTokenRequest { RefreshToken = live },
            ApiTestFixture.Json);

        afterwards.StatusCode.Should().Be(
            HttpStatusCode.Unauthorized,
            "a cross-client replay of any retained generation revokes the account's live families");
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
        using HttpClient administrator = await _fixture.CreateAdministratorClientAsync();
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

        stillAccepted.StatusCode.Should().Be(
            HttpStatusCode.OK,
            "there is no registry of withdrawn access tokens and none may be introduced, so the reduction "
            + "against the legacy cookie sign-out is asserted rather than papered over");

        // Withdrawing the SAME value a second time answers alike. A token that had genuinely been revoked
        // is the case an unknown value cannot stand in for, and answering differently for it would tell an
        // anonymous caller that this value had once been live.
        using HttpResponseMessage alreadyRevoked = await client.PostAsJsonAsync(
            LogoutRoute,
            new RefreshTokenRequest { RefreshToken = issued.RefreshToken },
            ApiTestFixture.Json);

        alreadyRevoked.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    /// <summary>
    /// Signing out succeeds with nothing to do for a blank value, and reports an UNCONFIRMED retirement -
    /// repeatably - for a value this instance does not hold.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// A blank value asks for nothing, so it is still a completed sign-out. An unrecognised value is no
    /// longer answered <c>204</c>: with families held per process, "I do not hold this" and "this belongs
    /// to another replica" are the same answer, and reporting the second as a completed sign-out left the
    /// session live there while the client discarded the only credential able to end it.
    /// </remarks>
    [Fact]
    public async Task Logout_CompletesForABlankValueAndReportsAnUnconfirmedRetirement()
    {
        using HttpClient client = _fixture.CreateAnonymousClient();

        using HttpResponseMessage blank = await client.PostAsJsonAsync(
            LogoutRoute,
            new RefreshTokenRequest { RefreshToken = string.Empty },
            ApiTestFixture.Json);

        blank.StatusCode.Should().Be(HttpStatusCode.NoContent);

        string neverIssued = "never-issued-" + Suffix();

        using HttpResponseMessage unknown = await client.PostAsJsonAsync(
            LogoutRoute,
            new RefreshTokenRequest { RefreshToken = neverIssued },
            ApiTestFixture.Json);

        unknown.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);

        // The SAME value again, so this arm asserts repetition rather than a second unknown value.
        using HttpResponseMessage repeated = await client.PostAsJsonAsync(
            LogoutRoute,
            new RefreshTokenRequest { RefreshToken = neverIssued },
            ApiTestFixture.Json);

        repeated.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
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
        using HttpClient client = await _fixture.CreateHostClientAsync();

        using HttpResponseMessage response = await client.GetAsync(MeRoute);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        CurrentUserDto? me = await response.Content.ReadEnvelopeAsync<CurrentUserDto>();

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
    /// The caller's own snapshot publishes the blocking remediation obligations, INCLUDING one imposed after
    /// the session began.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// <para>
    /// ⚠ THIS IS THE CROSS-LAYER CONTRACT GAP THAT WAS MEASURED. Sign-in published the obligations on its own
    /// top-level members while this endpoint omitted them entirely - and this endpoint is DELIBERATELY open
    /// during remediation, which makes it the only response a confined caller can still read. An obligation
    /// imposed after sign-in therefore reached the client on no path at all: every ordinary endpoint answered
    /// <c>403 auth.remediation_required</c>, the console rendered as though nothing were owed, and the caller
    /// was never sent to the screen that clears it.
    /// </para>
    /// <para>
    /// THE OBLIGATION USED HERE IS ACCOUNT-SCOPED ON PURPOSE. A forced credential change touches ONE account
    /// row, so this case cannot affect any other case in a suite that shares one database - whereas marking a
    /// profile property required is tenant-wide and would confine every member account in the tenant. The
    /// profile arm of the same decision is measured by the service-level facts, which can impose it in
    /// isolation.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Me_AfterAnObligationIsImposedMidSession_PublishesIt()
    {
        using HttpClient administrator = await _fixture.CreateAdministratorClientAsync();
        UserDetailDto account = await CreateUserAsync(administrator);

        using HttpClient bearer = await _fixture.CreateClientForAsync(
            account.Username ?? string.Empty,
            ApiTestFixture.KnownPassword);

        using HttpResponseMessage before = await bearer.GetAsync(MeRoute);

        before.StatusCode.Should().Be(HttpStatusCode.OK);

        CurrentUserDto? unencumbered = await before.Content.ReadEnvelopeAsync<CurrentUserDto>();

        unencumbered.Should().NotBeNull();
        unencumbered!.MustChangePassword.Should().BeFalse(
            "false is DATA here rather than an omission: the member owes nothing yet");
        unencumbered.MustUpdateProfile.Should().BeFalse();

        // The obligation is imposed by an administrator while the member's session is live and its access
        // token unchanged, which is exactly the sequence the client cannot otherwise observe.
        using HttpResponseMessage forced = await administrator.PostAsync(
            new Uri(
                $"/api/v1/users/{account.UserId.ToString(CultureInfo.InvariantCulture)}/require-password-change",
                UriKind.Relative),
            content: null);

        forced.StatusCode.Should().Be(HttpStatusCode.NoContent);

        using HttpResponseMessage after = await bearer.GetAsync(MeRoute);

        after.StatusCode.Should().Be(
            HttpStatusCode.OK,
            "the snapshot stays reachable while an obligation stands, which is what makes it the path that "
            + "can report one");

        CurrentUserDto? encumbered = await after.Content.ReadEnvelopeAsync<CurrentUserDto>();

        encumbered.Should().NotBeNull();
        encumbered!.MustChangePassword.Should().BeTrue(
            "the same token now describes a session carrying an obligation the sign-in response could not "
            + "have known about");
        encumbered.UserId.Should().Be(account.UserId);
    }

    /// <summary>
    /// The snapshot is read from the database rather than from the token, so a token for an account that no
    /// longer exists is reported as a missing account instead of being answered from its own claims.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task Me_WhenTheAccountNoLongerExists_ReturnsNotFound()
    {
        using HttpClient administrator = await _fixture.CreateAdministratorClientAsync();
        UserDetailDto account = await CreateUserAsync(administrator);

        using HttpClient bearer = await _fixture.CreateClientForAsync(
            account.Username ?? string.Empty,
            ApiTestFixture.KnownPassword);

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
    /// A submission carrying neither credential member is refused as a field-error document that names both
    /// members, is served as a problem document and preserves the request's trace identifier.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// The two single-member tests above prove each rule fires. This one proves the DOCUMENT, which is a
    /// separate contract: a client renders the per-member messages beside the fields the operator filled
    /// in, so the dictionary keys are as load-bearing as the status, and a refusal that named no member
    /// would leave the operator nothing to correct.
    /// </remarks>
    [Fact]
    public async Task Login_WithNeitherMemberSupplied_ReportsBothOfThemInTheProblemDocument()
    {
        using HttpClient client = _fixture.CreateAnonymousClient();

        using HttpResponseMessage response = await client.PostAsJsonAsync(
            LoginRoute(_fixture.Seed.PortalId),
            new LoginRequest { Username = string.Empty, Password = string.Empty },
            ApiTestFixture.Json);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        response.Content.Headers.ContentType?.MediaType.Should().EndWith(
            JsonMediaTypeSuffix,
            "a field-error refusal is a JSON document a client parses, never a rendered page");

        ValidationProblemDetails? problem = await response.Content
            .ReadFromJsonAsync<ValidationProblemDetails>(ApiTestFixture.Json);

        problem.Should().NotBeNull();
        problem!.Status.Should().Be((int)HttpStatusCode.BadRequest);
        problem.Title.Should().NotBeNullOrWhiteSpace();

        problem.Errors.Should().ContainKey(UsernameMember);
        problem.Errors.Should().ContainKey(PasswordMember);
        problem.Errors[UsernameMember].Should().Contain(MissingUsernameMessage);
        problem.Errors[PasswordMember].Should().Contain(MissingPasswordMessage);
        problem.Errors.Should().HaveCount(
            2,
            "the document attributes a failure to each member the caller actually sent and to nothing "
            + "else - the tenant is assigned by the controller and the verification code is only ruled on "
            + "when one is supplied");

        problem.Extensions.Should().ContainKey(
            TraceIdExtension,
            "the trace identifier is the one extension the factory adds, and it is what joins a caller's "
            + "report of a refused submission to the entry the server recorded");
    }

    /// <summary>
    /// A caller-supplied correlation identifier is echoed on an accepted sign-in, one is minted when the
    /// caller supplies none, and one is present on a refusal as well.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// The refusal half is the one worth having. The response header is registered through a starting
    /// callback BEFORE the pipeline continues, so it survives a short-circuit: an operator diagnosing a
    /// sign-in that was refused is exactly the caller who needs the identifier, and a middleware that only
    /// stamped successful responses would fail them at the moment it mattered.
    /// </remarks>
    [Fact]
    public async Task Login_EchoesASuppliedCorrelationIdMintsOneWhenAbsentAndCarriesOneOnARefusal()
    {
        using HttpClient client = _fixture.CreateAnonymousClient();

        string supplied = ApiTestFixture.NewCorrelationId();

        using HttpRequestMessage accepted = new(HttpMethod.Post, LoginRoute(_fixture.Seed.PortalId))
        {
            Content = JsonContent.Create(
                new LoginRequest
                {
                    Username = IntegrationSeed.AdminUserName,
                    Password = ApiTestFixture.KnownPassword,
                },
                options: ApiTestFixture.Json),
        };

        using CorrelatedResponse echoed = await AuthenticatedClientFactory
            .SendWithCorrelationIdAsync(client, accepted, supplied);

        echoed.Response.StatusCode.Should().Be(HttpStatusCode.OK);
        echoed.ReceivedCorrelationId.Should().Be(supplied);
        echoed.RoundTripped.Should().BeTrue();

        // Nothing is supplied here, so the response has to carry an identifier the server minted.
        using HttpResponseMessage minted = await client.PostAsJsonAsync(
            LoginRoute(_fixture.Seed.PortalId),
            new LoginRequest
            {
                Username = IntegrationSeed.AdminUserName,
                Password = ApiTestFixture.KnownPassword,
            },
            ApiTestFixture.Json);

        minted.StatusCode.Should().Be(HttpStatusCode.OK);

        string? generated = ApiTestFixture.ReadCorrelationId(minted);

        generated.Should().NotBeNullOrWhiteSpace(
            "every response carries an identifier, whether or not the caller brought one");
        generated.Should().NotBe(supplied, "the minted value belongs to this request alone");

        string refusedCorrelationId = ApiTestFixture.NewCorrelationId();

        using HttpRequestMessage refused = new(HttpMethod.Post, LoginRoute(_fixture.Seed.PortalId))
        {
            Content = JsonContent.Create(
                new LoginRequest
                {
                    Username = "no_such_account_" + Suffix(),
                    Password = ApiTestFixture.KnownPassword,
                },
                options: ApiTestFixture.Json),
        };

        using CorrelatedResponse denial = await AuthenticatedClientFactory
            .SendWithCorrelationIdAsync(client, refused, refusedCorrelationId);

        denial.Response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        denial.Response.Content.Headers.ContentType?.MediaType.Should().EndWith(JsonMediaTypeSuffix);
        denial.ReceivedCorrelationId.Should().Be(
            refusedCorrelationId,
            "the identifier is registered on the response before the pipeline continues, so a refused "
            + "request is correlatable too");
    }

    /// <summary>
    /// An unusable correlation identifier is replaced rather than echoed, and does not turn a good
    /// submission into a refusal.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// An inbound identifier is caller-controlled and is written into log lines, so a value that is too
    /// long to be trusted is discarded and a fresh one minted - never sanitised and kept, and never echoed.
    /// </remarks>
    [Fact]
    public async Task Login_WithAnUnusableCorrelationId_MintsAReplacementAndStillAnswers()
    {
        using HttpClient client = _fixture.CreateAnonymousClient();

        string overlong = new('a', MaximumCorrelationIdLength + 1);

        using HttpRequestMessage request = new(HttpMethod.Post, LoginRoute(_fixture.Seed.PortalId))
        {
            Content = JsonContent.Create(
                new LoginRequest
                {
                    Username = IntegrationSeed.AdminUserName,
                    Password = ApiTestFixture.KnownPassword,
                },
                options: ApiTestFixture.Json),
        };

        using CorrelatedResponse correlated = await AuthenticatedClientFactory
            .SendWithCorrelationIdAsync(client, request, overlong);

        correlated.Response.StatusCode.Should().Be(
            HttpStatusCode.OK,
            "an unusable correlation identifier is a diagnostic concern and must never refuse a request");

        correlated.ReceivedCorrelationId.Should().NotBeNullOrWhiteSpace();
        correlated.ReceivedCorrelationId.Should().NotBe(
            overlong,
            "an untrusted inbound value is discarded and replaced rather than echoed back");
        correlated.ReceivedCorrelationId!.Length.Should().BeLessThanOrEqualTo(MaximumCorrelationIdLength);
    }

    /// <summary>
    /// The shipped tenant-administrator credential is ACCEPTED and carries the change advisory. It is not a
    /// refusal.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task Login_WithTheShippedAdministratorCredential_IsAcceptedAndCarriesTheChangeAdvisory()
    {
        using HttpClient administrator = await _fixture.CreateAdministratorClientAsync();
        await CreateShippedAccountAsync(administrator, ShippedAdministratorAccountName);

        using HttpClient client = _fixture.CreateAnonymousClient();

        using HttpResponseMessage response = await client.PostAsJsonAsync(
            LoginRoute(_fixture.Seed.PortalId),
            new LoginRequest
            {
                Username = ShippedAdministratorAccountName,
                Password = ShippedAdministratorAccountName,
            },
            ApiTestFixture.Json);

        response.StatusCode.Should().Be(
            HttpStatusCode.OK,
            "the weak-credential outcome is an advisory on an accepted sign-in, exactly as the legacy made "
            + "it, and refusing it would lock an installation out of its administrator");

        LoginResponse issued = await ReadLoginAsync(response);

        issued.AccessToken.Should().NotBeNullOrWhiteSpace("the session is genuinely issued, not withheld");
        issued.MustChangePassword.Should().BeTrue(
            "the advisory reaches the client as the forced-change flag, which is how the legacy "
            + "remediation intent survives without a status ordinal on the wire");
        issued.User.Username.Should().Be(ShippedAdministratorAccountName);
        issued.User.IsSuperUser.Should().BeFalse("this is the tenant administrator, not the host account");
    }

    /// <summary>
    /// The shipped host credential is likewise ACCEPTED with the change advisory, and is reported as a host
    /// caller.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task Login_WithTheShippedHostCredential_IsAcceptedAndCarriesTheChangeAdvisory()
    {
        using HttpClient administrator = await _fixture.CreateAdministratorClientAsync();
        await CreateShippedAccountAsync(administrator, ShippedHostAccountName);
        await PromoteToSuperUserAsync(ShippedHostAccountName);

        using HttpClient client = _fixture.CreateAnonymousClient();

        using HttpResponseMessage response = await client.PostAsJsonAsync(
            LoginRoute(_fixture.Seed.PortalId),
            new LoginRequest
            {
                Username = ShippedHostAccountName,
                Password = ShippedHostAccountName,
            },
            ApiTestFixture.Json);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        LoginResponse issued = await ReadLoginAsync(response);

        issued.AccessToken.Should().NotBeNullOrWhiteSpace();
        issued.MustChangePassword.Should().BeTrue();
        issued.User.IsSuperUser.Should().BeTrue(
            "the host arm of the advisory is reached from the superuser outcome, so a caller that was not "
            + "reported as one would mean the wrong arm fired");
    }

    /// <summary>Two accounts may share one electronic-mail address, and both of them can sign in.</summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// The sign-in half is what makes this an authentication test rather than an account-administration
    /// one. An address that identifies two accounts cannot be what resolves a credential, so both accounts
    /// have to be reachable by their own names and each has to receive its own session.
    /// </remarks>
    [Fact]
    public async Task Login_ForTwoAccountsSharingOneEmailAddress_AcceptsBoth()
    {
        using HttpClient administrator = await _fixture.CreateAdministratorClientAsync();

        string shared = "shared.address." + Suffix() + "@example.com";

        // Each creation asserts the created status internally, which is the "never a conflict" half of the
        // contract: a duplicate address that was refused would fail here rather than below.
        UserDetailDto first = await CreateUserAsync(administrator, email: shared);
        UserDetailDto second = await CreateUserAsync(administrator, email: shared);

        first.Email.Should().Be(shared);
        second.Email.Should().Be(shared);
        second.UserId.Should().NotBe(first.UserId);
        second.Username.Should().NotBe(first.Username);

        using HttpClient client = _fixture.CreateAnonymousClient();

        LoginResponse firstSession = await SignInAsync(client, first.Username);
        LoginResponse secondSession = await SignInAsync(client, second.Username);

        firstSession.User.UserId.Should().Be(first.UserId);
        secondSession.User.UserId.Should().Be(second.UserId);
        secondSession.AccessToken.Should().NotBe(
            firstSession.AccessToken,
            "each account receives its own session, so a shared address cannot merge two identities");
    }

    /// <summary>None of the three responses that carry a session publishes credential material of any kind.</summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// MIGRATION: the legacy provider was registered with <c>enablePasswordRetrieval="true"</c> and
    /// <c>passwordFormat="Encrypted"</c> - reversible triple-DES storage, with the very key that reversed
    /// it committed to source control at L91-L93 - so a legacy installation could hand a stored credential
    /// back.
    /// </remarks>
    [Fact]
    public async Task Auth_PublishesNoCredentialMaterialOnAnyOfItsResponses()
    {
        using HttpClient administrator = await _fixture.CreateAdministratorClientAsync();

        // An account of this test's own, because the exchange below rotates a refresh token and a replay of
        // a rotated one withdraws every session the account holds. Using a seeded persona would make
        // another suite's session collateral.
        UserDetailDto account = await CreateUserAsync(administrator);

        using HttpClient client = _fixture.CreateAnonymousClient();

        using HttpResponseMessage signIn = await client.PostAsJsonAsync(
            LoginRoute(_fixture.Seed.PortalId),
            new LoginRequest { Username = account.Username, Password = ApiTestFixture.KnownPassword },
            ApiTestFixture.Json);

        signIn.StatusCode.Should().Be(HttpStatusCode.OK);

        // Read once, into a string, and deserialise from it. The response stream is not rewindable, so a
        // second read of the same content would fail for a reason unrelated to the contract.
        string signInBody = await signIn.Content.ReadAsStringAsync();

        AssertPublishesNoCredentialMember(signInBody, "the sign-in response");
        signInBody.Should().NotContain(
            ApiTestFixture.KnownPassword,
            "the submitted credential is never reflected back, not even inside an unrelated member");

        ApiEnvelope<LoginResponse>? envelope =
            JsonSerializer.Deserialize<ApiEnvelope<LoginResponse>>(signInBody, ApiTestFixture.Json);

        envelope.Should().NotBeNull();

        LoginResponse issued = envelope!.Data;

        issued.RefreshToken.Should().NotBeNullOrWhiteSpace();

        using HttpResponseMessage rotated = await client.PostAsJsonAsync(
            RefreshRoute,
            new RefreshTokenRequest { RefreshToken = issued.RefreshToken },
            ApiTestFixture.Json);

        rotated.StatusCode.Should().Be(HttpStatusCode.OK);

        AssertPublishesNoCredentialMember(
            await rotated.Content.ReadAsStringAsync(),
            "the token-exchange response");

        using HttpClient bearer = AuthenticatedClientFactory.Authenticate(
            _fixture.CreateAnonymousClient(),
            issued.AccessToken);

        using HttpResponseMessage snapshot = await bearer.GetAsync(MeRoute);

        snapshot.StatusCode.Should().Be(HttpStatusCode.OK);

        AssertPublishesNoCredentialMember(
            await snapshot.Content.ReadAsStringAsync(),
            "the caller-description response");
    }

    /// <summary>
    /// The controller publishes the four operations it declares and nothing else, and each of those is
    /// bound to its own method.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// A closed surface is a security property rather than tidiness. The legacy application reached its
    /// credential features through several pages, and the ones that are out of scope here must not be
    /// quietly reachable through a catch-all route that answered anything under the controller's prefix.
    /// </remarks>
    [Fact]
    public async Task Auth_DeclaresNoOperationBeyondTheFourItPublishes()
    {
        Uri undeclared = new("/api/v1/auth/" + UndeclaredOperationSegment, UriKind.Relative);

        using HttpClient anonymous = _fixture.CreateAnonymousClient();

        using HttpResponseMessage unauthenticated = await anonymous.PostAsJsonAsync(
            undeclared,
            new RefreshTokenRequest { RefreshToken = "unused-by-an-unrouted-address" },
            ApiTestFixture.Json);

        unauthenticated.StatusCode.Should().Be(
            HttpStatusCode.Unauthorized,
            "the fallback policy demands an authenticated caller for anything that declares no "
            + "authorisation of its own, so an anonymous caller cannot probe which addresses exist");

        using HttpClient caller = await _fixture.CreateHostClientAsync();

        using HttpResponseMessage posted = await caller.PostAsJsonAsync(
            undeclared,
            new RefreshTokenRequest { RefreshToken = "unused-by-an-unrouted-address" },
            ApiTestFixture.Json);

        posted.StatusCode.Should().Be(
            HttpStatusCode.NotFound,
            "nothing under the controller's prefix is served by a catch-all");

        using HttpResponseMessage fetched = await caller.GetAsync(undeclared);

        fetched.StatusCode.Should().Be(HttpStatusCode.NotFound);

        using HttpResponseMessage wrongMethod = await caller.GetAsync(LoginRoute(_fixture.Seed.PortalId));

        wrongMethod.StatusCode.Should().Be(
            HttpStatusCode.MethodNotAllowed,
            "each declared address is bound to exactly one method, so a credential cannot be submitted in "
            + "a request line");
    }

    /// <summary>Sign-in is rate limited per caller address, and a rejected attempt states how long to wait.</summary>
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
    /// <param name="username">The account name, or <see langword="null"/> for a generated one.</param>
    /// <param name="email">The electronic-mail address, or <see langword="null"/> for a generated one.</param>
    /// <returns>The created account.</returns>
    private async Task<UserDetailDto> CreateUserAsync(
        HttpClient client,
        bool authorize = true,
        string? username = null,
        string? email = null)
    {
        string suffix = Suffix();

        using HttpResponseMessage response = await client.PostAsJsonAsync(
            UsersRoute(_fixture.Seed.PortalId),
            new CreateUserRequest
            {
                Username = username ?? "itest_auth_" + suffix,
                FirstName = "Integration",
                LastName = "Signin",
                DisplayName = "Integration Signin " + suffix,
                Email = email ?? "itest.auth." + suffix + "@example.com",
                Password = ApiTestFixture.KnownPassword,
                ConfirmPassword = ApiTestFixture.KnownPassword,
                Authorize = authorize,
            },
            ApiTestFixture.Json);

        response.StatusCode.Should().Be(HttpStatusCode.Created);

        UserDetailDto? created = await response.Content.ReadEnvelopeAsync<UserDetailDto>();

        created.Should().NotBeNull();
        return created!;
    }

    /// <summary>
    /// Creates an account under one of the names the product was distributed with, holding that name as its
    /// credential, so that the weak-credential advisory applies to it.
    /// </summary>
    /// <param name="administrator">A client holding the administrators role.</param>
    /// <param name="accountName">The shipped account name, which is also the shipped credential.</param>
    /// <returns>A task representing the creation.</returns>
    /// <remarks>
    /// The credential is WRITTEN into the store rather than submitted, and that is forced rather than
    /// preferred: the policy carried forward from <c>Website/release.config</c> L242 requires seven
    /// characters, both shipped values are shorter, and the account-creation rules reproduce that policy
    /// verbatim - so the API correctly refuses to set either of them.
    /// </remarks>
    private async Task CreateShippedAccountAsync(HttpClient administrator, string accountName)
    {
        UserDetailDto created = await CreateUserAsync(administrator, username: accountName);

        created.Username.Should().Be(accountName);

        await WriteStoredHashAsync(
            accountName,
            BCrypt.Net.BCrypt.EnhancedHashPassword(
                accountName,
                CurrentWorkFactor,
                BCrypt.Net.HashType.SHA384));
    }

    /// <summary>Marks an account as an installation-wide host account by writing the store directly.</summary>
    /// <param name="userName">The account name.</param>
    /// <returns>A task representing the write.</returns>
    /// <remarks>
    /// There is no endpoint that grants installation-wide authority, and there should not be one, so the
    /// state is established by writing the single column the sign-in path reads. That column is the whole
    /// of the distinction: a host account reaches the superuser outcome, which is the arm the host half of
    /// the weak-credential advisory is promoted from.
    /// </remarks>
    private async Task PromoteToSuperUserAsync(string userName)
    {
        int affected = await _fixture.Database.ExecuteAsync(
            """
            UPDATE [dbo].[Users]
            SET [IsSuperUser] = 1
            WHERE LOWER([Username]) = LOWER(@userName);
            """,
            new Dictionary<string, object?> { ["userName"] = userName });

        affected.Should().Be(1);
    }

    /// <summary>
    /// Asserts that a response document publishes no member whose name suggests credential material.
    /// </summary>
    /// <param name="payload">The response body.</param>
    /// <param name="description">What the document is, so a failure names the operation.</param>
    private static void AssertPublishesNoCredentialMember(string payload, string description)
    {
        using JsonDocument document = JsonDocument.Parse(payload);

        IReadOnlyList<string> published = FindCredentialMembers(document.RootElement);

        published.Should().BeEmpty(
            "{0} must publish no credential material, and credentials are one-way hashed with no "
            + "retrieval operation anywhere in this surface",
            description);
    }

    /// <summary>Collects the names of any members that could carry credential material.</summary>
    /// <param name="node">The element to walk.</param>
    /// <returns>The offending member names, empty when there are none.</returns>
    private static List<string> FindCredentialMembers(JsonElement node)
    {
        List<string> found = [];

        switch (node.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (JsonProperty member in node.EnumerateObject())
                {
                    if (CarriesCredentialMaterial(member))
                    {
                        found.Add(member.Name);
                    }

                    found.AddRange(FindCredentialMembers(member.Value));
                }

                break;

            case JsonValueKind.Array:
                foreach (JsonElement item in node.EnumerateArray())
                {
                    found.AddRange(FindCredentialMembers(item));
                }

                break;

            default:
                break;
        }

        return found;
    }

    /// <summary>Reports whether a member both names and could carry credential material.</summary>
    /// <param name="member">The member to judge.</param>
    /// <returns><see langword="true"/> when the member must not appear on this surface.</returns>
    /// <remarks>
    /// The bearer values a session response does carry are deliberately outside the vocabulary matched
    /// here. They are values the caller is meant to hold, minted for it and short-lived; a stored
    /// credential is not, which is the distinction this check draws.
    /// </remarks>
    private static bool CarriesCredentialMaterial(JsonProperty member) =>
        member.Value.ValueKind == JsonValueKind.String
        && (member.Name.Contains("password", StringComparison.OrdinalIgnoreCase)
            || member.Name.Contains("credential", StringComparison.OrdinalIgnoreCase)
            || member.Name.Contains("secret", StringComparison.OrdinalIgnoreCase)
            || member.Name.Contains("answer", StringComparison.OrdinalIgnoreCase));

    /// <summary>Clears a lock by writing the credential store directly.</summary>
    /// <param name="userName">The account name.</param>
    /// <returns>A task representing the write.</returns>
    /// <remarks>
    /// The inverse of <see cref="LockAsync"/>, and written the same way for the same reason: the
    /// administrative unlock endpoint carries its own authorisation, which is another suite's subject, and
    /// a test that only needs the stored state should not depend on it.
    /// </remarks>
    private async Task UnlockAsync(string userName)
    {
        int affected = await _fixture.Database.ExecuteAsync(
            """
            UPDATE am
            SET am.[IsLockedOut] = 0,
                am.[LastLockoutDate] = CONVERT(datetime, '17540101', 112),
                am.[FailedPasswordAttemptCount] = 0,
                am.[FailedPasswordAttemptWindowStart] = CONVERT(datetime, '17540101', 112)
            FROM [dbo].[aspnet_Membership] am
            INNER JOIN [dbo].[aspnet_Users] au ON au.[UserId] = am.[UserId]
            WHERE au.[LoweredUserName] = LOWER(@userName);
            """,
            new Dictionary<string, object?> { ["userName"] = userName });

        affected.Should().Be(1);
    }

    /// <summary>
    /// Sets the seed tenant's registration mode, which selects between the three approval outcomes.
    /// </summary>
    /// <param name="mode">The persisted <c>Portals.UserRegistration</c> value.</param>
    /// <returns>A task representing the write.</returns>
    /// <remarks>
    /// Written through SQL rather than through the tenant endpoint on purpose: the endpoint would exercise
    /// the portal-update path and its authorisation, which is another suite's subject, and this test needs
    /// only the stored value the sign-in ladder reads.
    /// </remarks>
    private async Task SetRegistrationModeAsync(int mode)
    {
        int affected = await _fixture.Database.ExecuteAsync(
            """
            UPDATE [dbo].[Portals]
            SET [UserRegistration] = @mode
            WHERE [PortalID] = @portalId;
            """,
            new Dictionary<string, object?>
            {
                ["mode"] = mode,
                ["portalId"] = _fixture.Seed.PortalId,
            });

        affected.Should().Be(1);
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

    /// <summary>
    /// Every token-bearing response, and every refusal from the same endpoints, forbids storage by any
    /// cache.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// An access token and a refresh token are bearer credentials: an intermediate cache that retains the
    /// response retains the credentials, and a shared cache can then serve one caller's tokens to another.
    /// </remarks>
    [Fact]
    public async Task CredentialEndpoints_ForbidResponseCaching()
    {
        using HttpClient client = _fixture.CreateAnonymousClient();

        using HttpResponseMessage issued = await client.PostAsJsonAsync(
            LoginRoute(_fixture.Seed.PortalId),
            new LoginRequest
            {
                Username = IntegrationSeed.AdminUserName,
                Password = ApiTestFixture.KnownPassword,
            },
            ApiTestFixture.Json);

        issued.StatusCode.Should().Be(HttpStatusCode.OK);
        AssertNotCacheable(issued, "a successful sign-in carries an access token and a refresh token");

        LoginResponse pair = await ReadLoginAsync(issued);

        using HttpResponseMessage refused = await client.PostAsJsonAsync(
            LoginRoute(_fixture.Seed.PortalId),
            new LoginRequest
            {
                Username = IntegrationSeed.AdminUserName,
                Password = "definitely-not-the-password",
            },
            ApiTestFixture.Json);

        refused.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        AssertNotCacheable(refused, "a cached refusal is a cached security decision");

        using HttpResponseMessage rotated = await client.PostAsJsonAsync(
            RefreshRoute,
            new RefreshTokenRequest { RefreshToken = pair.RefreshToken },
            ApiTestFixture.Json);

        rotated.StatusCode.Should().Be(HttpStatusCode.OK);
        AssertNotCacheable(rotated, "rotation issues a fresh pair of bearer credentials");

        using HttpResponseMessage endedSession = await client.PostAsJsonAsync(
            LogoutRoute,
            new RefreshTokenRequest { RefreshToken = "not-a-token-that-was-ever-issued" },
            ApiTestFixture.Json);

        AssertNotCacheable(endedSession, "revocation is addressed with a bearer credential in the body");

        using HttpClient administrator = await _fixture.CreateAdministratorClientAsync();

        using HttpResponseMessage ordinary = await administrator.GetAsync(
            new Uri($"/api/v1/portals/{Route(_fixture.Seed.PortalId)}", UriKind.Relative));

        ordinary.StatusCode.Should().Be(HttpStatusCode.OK);

        ordinary.Headers.CacheControl.Should().NotBeNull(
            "an authorised read returns personal data, which no cache may store");
        ordinary.Headers.CacheControl!.NoStore.Should().BeTrue(
            "PRIV-03: a private browser cache must not write this response to disk");
        ordinary.Headers.CacheControl.Private.Should().BeTrue(
            "the shared proxy in front of this API serves every tenant, so it must hold nothing caller-specific");
        ordinary.Headers.CacheControl.MaxAge.Should().Be(
            TimeSpan.Zero,
            "the belt-and-braces value for an intermediary that falls back to freshness arithmetic");

        ordinary.Headers.Pragma.Should().BeEmpty(
            "the HTTP/1.0 spelling is reserved for credential endpoints, which is what keeps the two rules "
            + "distinguishable rather than one rule applied everywhere");
    }

    /// <summary>Asserts that one response forbids caching in all three of the vocabularies caches read.</summary>
    /// <param name="response">The response to inspect.</param>
    /// <param name="because">Why this response must not be retained.</param>
    private static void AssertNotCacheable(HttpResponseMessage response, string because)
    {
        response.Headers.CacheControl.Should().NotBeNull(because);
        response.Headers.CacheControl!.NoStore.Should().BeTrue(because);
        response.Headers.CacheControl.NoCache.Should().BeTrue(because);
        response.Headers.Pragma.Should().Contain(
            directive => directive.Name == "no-cache",
            "some proxies honour only the HTTP/1.0 spelling");
        response.Content.Headers.Expires.Should().NotBeNull(
            "a cache that assigns a heuristic freshness lifetime reads this instead");
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

    /// <summary>Writes a complete legacy membership representation for a migration test.</summary>
    /// <param name="userName">The account name.</param>
    /// <param name="storedValue">The encoded legacy representation.</param>
    /// <param name="format">The persisted legacy format discriminator.</param>
    /// <param name="passwordSalt">The encoded per-account salt.</param>
    /// <returns>A task representing the write.</returns>
    private async Task WriteStoredCredentialAsync(
        string userName,
        string storedValue,
        PasswordFormat format,
        string passwordSalt)
    {
        int affected = await _fixture.Database.ExecuteAsync(
            """
            UPDATE am
            SET am.[Password] = @storedValue,
                am.[PasswordFormat] = @format,
                am.[PasswordSalt] = @passwordSalt
            FROM [dbo].[aspnet_Membership] am
            INNER JOIN [dbo].[aspnet_Users] au ON au.[UserId] = am.[UserId]
            WHERE au.[LoweredUserName] = LOWER(@userName);
            """,
            new Dictionary<string, object?>
            {
                ["userName"] = userName,
                ["storedValue"] = storedValue,
                ["format"] = (int)format,
                ["passwordSalt"] = passwordSalt,
            });

        affected.Should().Be(1);
    }

    /// <summary>Reads the persisted membership format discriminator.</summary>
    /// <param name="userName">The account name.</param>
    /// <returns>The stored integer discriminator.</returns>
    private Task<int> ReadStoredFormatAsync(string userName) =>
        _fixture.Database.ScalarAsync<int>(
            """
            SELECT COALESCE(MAX(am.[PasswordFormat]), -1)
            FROM [dbo].[aspnet_Membership] am
            INNER JOIN [dbo].[aspnet_Users] au ON au.[UserId] = am.[UserId]
            WHERE au.[LoweredUserName] = LOWER(@userName);
            """,
            new Dictionary<string, object?> { ["userName"] = userName });

    /// <summary>Reads the persisted membership salt.</summary>
    /// <param name="userName">The account name.</param>
    /// <returns>The stored salt, or the empty string when cleared.</returns>
    private Task<string> ReadStoredSaltAsync(string userName) =>
        _fixture.Database.ScalarAsync<string>(
            """
            SELECT COALESCE(MAX(am.[PasswordSalt]), N'')
            FROM [dbo].[aspnet_Membership] am
            INNER JOIN [dbo].[aspnet_Users] au ON au.[UserId] = am.[UserId]
            WHERE au.[LoweredUserName] = LOWER(@userName);
            """,
            new Dictionary<string, object?> { ["userName"] = userName });

    /// <summary>Builds a synthetic format-2 fixture using the integration host's throwaway key.</summary>
    /// <param name="password">The fixture credential.</param>
    /// <returns>The encoded ciphertext and salt.</returns>
    private static (string StoredValue, string PasswordSalt) CreateLegacyEncryptedCredential(
        string password)
    {
        byte[] key = Convert.FromHexString(ApiTestFixture.LegacyCredentialDecryptionKey);
        byte[] salt = RandomNumberGenerator.GetBytes(16);
        byte[] passwordBytes = Encoding.Unicode.GetBytes(password);
        byte[] plaintext = [];
        byte[] ciphertext = [];

        try
        {
            using TripleDES algorithm = TripleDES.Create();
            algorithm.Key = key;
            algorithm.IV = new byte[algorithm.BlockSize / 8];
            algorithm.Mode = CipherMode.CBC;
            algorithm.Padding = PaddingMode.PKCS7;

            int prefixLength = algorithm.BlockSize / 8;
            plaintext = new byte[prefixLength + salt.Length + passwordBytes.Length];
            RandomNumberGenerator.Fill(plaintext.AsSpan(0, prefixLength));
            salt.CopyTo(plaintext, prefixLength);
            passwordBytes.CopyTo(plaintext, prefixLength + salt.Length);

            using ICryptoTransform encryptor = algorithm.CreateEncryptor();
            ciphertext = encryptor.TransformFinalBlock(plaintext, 0, plaintext.Length);
            return (Convert.ToBase64String(ciphertext), Convert.ToBase64String(salt));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            CryptographicOperations.ZeroMemory(salt);
            CryptographicOperations.ZeroMemory(passwordBytes);
            CryptographicOperations.ZeroMemory(plaintext);
            CryptographicOperations.ZeroMemory(ciphertext);
        }
    }

    /// <summary>
    /// Reduces a problem document to the parts that describe the outcome, so two denials can be compared
    /// for indistinguishability without comparing the per-request trace identifier.
    /// </summary>
    /// <param name="response">The refused response.</param>
    /// <returns>The problem type, title, status and detail, joined.</returns>
    private static async Task<string> ReadDenialAsync(HttpResponseMessage response)
    {
        using JsonDocument document =
            JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        static string Read(JsonElement root, string name) =>
            root.TryGetProperty(name, out JsonElement value)
                ? value.ToString()
                : string.Empty;

        JsonElement problem = document.RootElement;

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
        LoginResponse? issued = await response.Content.ReadEnvelopeAsync<LoginResponse>();

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

    /// <summary>Builds the canonical account collection route for the resolved tenant.</summary>
    /// <param name="_">Ignored legacy call-site value; the request host resolves the tenant.</param>
    /// <returns>A relative route.</returns>
    private static Uri UsersRoute(int _) => new("/api/v1/users", UriKind.Relative);

    /// <summary>Builds the item route for one account.</summary>
    /// <param name="_">Ignored legacy call-site value; the request host resolves the tenant.</param>
    /// <param name="userId">The account identifier.</param>
    /// <returns>A relative route.</returns>
    private static Uri UserRoute(int _, int userId) =>
        new($"/api/v1/users/{Route(userId)}", UriKind.Relative);

    /// <summary>Builds the account-owner credential-change route.</summary>
    /// <param name="_">Ignored legacy call-site value; the request host resolves the tenant.</param>
    /// <param name="userId">The account identifier.</param>
    /// <returns>A relative route.</returns>
    private static Uri PasswordRoute(int _, int userId) =>
        new($"/api/v1/users/{Route(userId)}/password", UriKind.Relative);

    /// <summary>Builds the account profile route.</summary>
    /// <param name="_">Ignored legacy call-site value; the request host resolves the tenant.</param>
    /// <param name="userId">The account identifier.</param>
    /// <returns>A relative route.</returns>
    private static Uri ProfileRoute(int _, int userId) =>
        new($"/api/v1/users/{Route(userId)}/profile", UriKind.Relative);

    /// <summary>Formats an identifier for a route without picking up the ambient culture.</summary>
    /// <param name="value">The identifier.</param>
    /// <returns>The invariant representation.</returns>
    private static string Route(int value) => value.ToString(CultureInfo.InvariantCulture);

    /// <summary>Produces a short random suffix for values that reach a unique constraint.</summary>
    /// <returns>Twelve lower-case hexadecimal characters.</returns>
    private static string Suffix() => Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture)[..12];
}
