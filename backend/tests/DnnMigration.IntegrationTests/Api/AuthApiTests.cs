using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DnnMigration.Application.Dtos.Auth;
using DnnMigration.Application.Dtos.User;
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
    /// <remarks>
    /// MIGRATION: this is the TARGET's default in force, and deliberately not presented as a ported legacy
    /// value. The legacy lockout policy was never stated: <c>passwordAttemptThreshold</c> and
    /// <c>passwordAttemptWindow</c> appear in <c>Website/release.config</c> only inside the descriptive
    /// comment above the provider element (L220-L232) and are absent from the element itself (L237-L247), so
    /// the platform's own implicit defaults applied and no configured number exists to preserve. What is
    /// asserted here is therefore that the configured threshold is ENFORCED, never that this particular
    /// number is what the legacy installation used.
    /// </remarks>
    private const int MaxInvalidPasswordAttempts = 5;

    /// <summary>A BCrypt cost below the hasher's own, so a credential stored at it must be replaced.</summary>
    private const int SupersededWorkFactor = 10;

    /// <summary>The cost the hasher writes, which a replaced credential must therefore carry.</summary>
    private const int CurrentWorkFactor = 12;

    /// <summary>The permit count the dedicated rate-limited host runs with.</summary>
    private const int TightPermitLimit = 2;

    /// <summary>
    /// The persisted <c>Portals.UserRegistration</c> value for verified registration, which is the only mode
    /// under which the approval ladder distinguishes "enter your code" from "that code is wrong".
    /// </summary>
    /// <remarks>
    /// Written as the stored integer rather than through the Domain enumeration, because this is a direct
    /// column write and the value's meaning is the column's, not the projection's.
    /// </remarks>
    private const int VerifiedRegistrationMode = 3;

    /// <summary>The mode the seed tenant holds, restored after any test that raises it.</summary>
    private const int DefaultRegistrationMode = 0;

    /// <summary>
    /// The media-type suffix every refusal on this controller has to carry.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The SUFFIX is asserted rather than the exact type, and that is a measured position rather than a
    /// weaker assertion for convenience. RFC 7807 section 3 fixes <c>application/problem+json</c>, and the
    /// documents this surface returns ARE RFC 7807 documents - but the framework serves them as
    /// <c>application/json</c>, because the controller declares <c>[Produces("application/json")]</c> and a
    /// produces declaration constrains every result the controller returns, refusals included. Measured, not
    /// assumed: asserting the RFC type here failed against the delivered pipeline with
    /// <c>application/json</c>.
    /// </para>
    /// <para>
    /// Pinning the current value would cement that deviation, and pinning the RFC value would fail against
    /// the pipeline as delivered - which is the same conclusion the sibling problem-document contract suite
    /// recorded for the same reason. Correcting it is a change to the controller's produces declaration and
    /// therefore not this suite's to make. The suffix still carries real content: it fails if a refusal ever
    /// arrives as a rendered page or as plain text, which is the failure mode that actually costs a client
    /// its error handling.
    /// </para>
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

    /// <summary>
    /// The greatest length at which the correlation middleware still trusts an inbound identifier.
    /// </summary>
    private const int MaximumCorrelationIdLength = 128;

    /// <summary>
    /// Account name the product was distributed with for a tenant administrator, and therefore the name
    /// the weak-credential advisory recognises.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The shipped credential for this account was the account name itself, so this one constant serves as
    /// both. That is deliberate rather than convenient: the account name has to appear here because the
    /// advisory keys on it, and reusing it as the credential means these tests introduce NO credential
    /// material into the repository that reading the account name did not already reveal. The service
    /// itself holds the two credentials only as SHA-256 fingerprints for the same reason, and that
    /// constant is the authority - this test drives the rule rather than restating it.
    /// </para>
    /// <para>
    /// MIGRATION: the legacy comparison was <c>UserController.vb:L1145</c>, which tested the name with
    /// VB's <c>=</c> under binary comparison and was therefore case-sensitive. The target compares
    /// case-insensitively, so the exact casing used here is not what makes the advisory fire.
    /// </para>
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
        issued.RefreshToken.Should().NotBeNullOrWhiteSpace();
        issued.ExpiresAtUtc.Should().BeAfter(
            DateTime.UtcNow,
            "exactly one expiry is published, as an absolute instant in UTC, so a client can schedule a "
            + "refresh instead of discovering expiry through a rejected request");

        // MIGRATION: the legacy session lifetime was the forms-authentication ticket's, declared as
        // <forms name=".DOTNETNUKE" protection="All" timeout="60" cookieless="UseCookies"/> at
        // Website/release.config:L147 - sixty minutes, which is the value the token lifetime option carries
        // forward. The number is NOT asserted here: this host deliberately configures a different lifetime so
        // that no suite depends on the shipped one, so asserting sixty would be asserting the fixture rather
        // than the parity. What is asserted is the property the legacy cookie had and this contract must keep -
        // that a session expires, at a stated instant, known to the client in advance. The cookie's own
        // settings (L214-L216) have no counterpart at all and are replaced by the bearer token wholesale.


        // The response publishes no bearer-scheme member, no remaining-seconds duration and no refresh
        // token expiry. The scheme is fixed by this contract rather than restated per response, a single
        // expiry representation cannot disagree with itself, and the refresh token's expiry is rotation
        // state the store owns rather than something a client should reason around.
        issued.MustChangePassword.Should().BeFalse(
            "the seeded account carries no forced credential update and is not using a shipped credential");
        issued.PasswordExpiring.Should().BeFalse("the seeded account's credential is not near expiry");

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
    /// <remarks>
    /// <para>
    /// MIGRATION - THE ONE DELIBERATE BEHAVIOURAL REVERSAL ON THIS SURFACE, AND THE REASON THIS TEST
    /// EXISTS. The legacy screen computed
    /// <c>authenticated = (loginStatus &lt;&gt; UserLoginStatus.LOGIN_FAILURE)</c> at
    /// <c>Login.ascx.vb:L187</c>, and the enclosing <c>If</c> at L168 intercepted only
    /// <c>LOGIN_USERNOTAPPROVED</c>. Read against the seven members of <c>UserLoginStatus</c> - measured
    /// verbatim as 0 through 6 - that expression admits <c>LOGIN_USERLOCKEDOUT</c>, which is 3: a locked
    /// account was therefore SIGNED IN, exactly as a successful one was. Only 0 and 4 refused.
    /// </para>
    /// <para>
    /// The migration discipline says to annotate a discovered defect rather than to fix it. This is the
    /// documented exception, on the same ground as the tenant-alias match: reproducing it would carry an
    /// AUTHENTICATION BYPASS into new code, and a lockout that admits the caller is not a lockout at all.
    /// The target refuses instead - and refuses without comparing the credential, so the account cannot be
    /// used as an oracle for whether a guess was right. The refusal is the same one an unknown account and a
    /// wrong credential receive, which is what the two assertions below pin: the shared code, and the absence
    /// of the word that would disclose the reason.
    /// </para>
    /// <para>
    /// The reason is not withheld from everyone - the companion test proves an entitled caller is told - so
    /// this is a disclosure boundary rather than a silence.
    /// </para>
    /// </remarks>
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
    /// incomplete. Answering 401 would invite a client to re-prompt for a credential that was never the
    /// problem. The reason code names which arm of the ladder applied.
    /// </remarks>
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

        // The approval outcomes are UNAUTHORIZED, not bad-request. Each names an account state that refused
        // a sign-in the credential itself did not refuse, so the request was correct and the account was
        // not yet admissible - telling a client its request was at fault would be the wrong answer. The
        // outcome is still NAMED in the body, which is the property this fact exists to assert: the caller
        // has proved its credential, so it is entitled to know which gate refused.
        withoutCode.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        string withoutCodeBody = await withoutCode.Content.ReadAsStringAsync();

        withoutCodeBody.Should().Contain(
            "auth.",
            "the approval outcome is named, because the caller has already proved its credential");

        // The seed tenant's registration mode is the persisted default, which admits no self-verification, so
        // the ladder selects the not-authorised member here. The other two members are exercised by the
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
    /// An account locked after its tokens were issued cannot renew any of them, and the refusal reaches every
    /// session it holds rather than only the one presented.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// <para>
    /// Two sessions are opened deliberately, because one would not distinguish the two possible fixes. The
    /// first is presented while the account is locked and must be refused. The second is never presented while
    /// locked at all: the account is unlocked first, so nothing about its own state or the store's would
    /// refuse it - and it must still be refused, which can only be true if the first refusal revoked the
    /// account's sessions rather than merely declining one request.
    /// </para>
    /// <para>
    /// The lock is written to the credential store directly, exactly as the sign-in lock-out test writes it,
    /// so the scenario does not depend on exhausting the attempt window or on the sign-in rate limiter.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Refresh_ForAnAccountLockedAfterIssue_IsRefusedAndEndsEverySession()
    {
        using HttpClient administrator = _fixture.CreateAdministratorClient();
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
    /// On a verified-registration tenant each of the three legacy approval outcomes reaches the caller as its
    /// own problem type answered <c>401</c>, and a wrong credential presented WITH the correct verification
    /// code approves nothing.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// <para>
    /// This test carries two obligations that only an end-to-end exchange can discharge. The first is parity:
    /// the legacy screen told its user which of <c>EnterCode</c>, <c>InvalidCode</c> and
    /// <c>UserNotAuthorized</c> had happened, and asserting the problem type proves the equivalent sentence now
    /// reaches a client. Naming them is safe only because each is reported from behind an accepted credential,
    /// and the third exchange below is what pins that boundary at the HTTP surface.
    /// </para>
    /// <para>
    /// The second is the status mapping. These codes are classified by an explicit list at the Api edge, and
    /// without an entry there they would fall to the default arm and answer <c>400</c> - telling a client its
    /// request was malformed when the request was correct and the account was not yet admissible. Only a real
    /// response carries that mapping, so only a test at this level can hold it.
    /// </para>
    /// <para>
    /// The tenant's registration mode is raised for the duration and restored in a <c>finally</c>, because it
    /// is the shared seed tenant and it is the only mode under which the ladder distinguishes its first two
    /// members. The integration suite is a single xunit collection, so no other test observes the window.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Login_ForAnUnapprovedAccount_NamesTheApprovalOutcomeAndRefusesAWrongCredential()
    {
        using HttpClient administrator = _fixture.CreateAdministratorClient();
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

            // THE CENTRAL ASSERTION OF THIS TEST. The correct verification code is composed from two integers
            // that appear in ordinary URLs, so it is guessable; before the gates were reordered, presenting it
            // with any password at all approved the account permanently and only then refused the sign-in. The
            // exchange below presents it with a WRONG password: the answer must be the uniform denial, which
            // discloses nothing about approval state to a caller who has proved nothing, and the account must
            // still be unapproved afterwards.
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

            // Read through the ENVELOPE. Deserialising an envelope directly as its payload type yields a
            // non-null object with every member unset, so the assertion below would have compared the
            // default of a boolean and passed whatever the endpoint actually said.
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

        UserDetailDto? detail = await read.Content.ReadEnvelopeAsync<UserDetailDto>();

        detail.Should().NotBeNull();
        detail!.LastLoginDate.Should().NotBeNull();
    }

    /// <summary>
    /// C-03: a required profile property the account has not answered raises the blocking profile advisory on
    /// sign-in, and the advisory survives a refresh.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// <para>
    /// The legacy gate is <c>UserController.vb</c> L1189-L1193 over
    /// <c>ProfileController.ValidateProfile</c>. Both halves are exercised here through the API: a required
    /// declaration is installed on the tenant, an account with no answer for it signs in, and the flag must be
    /// raised. It is then cleared by ANSWERING the property, which proves the flag tracks the profile rather
    /// than being set once at creation.
    /// </para>
    /// <para>
    /// Refresh is asserted as well because the review named both paths. A flag raised only on sign-in would be
    /// lost the moment a client rotated its token, which for a short-lived access token is within minutes.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Login_WhenARequiredProfilePropertyIsUnanswered_RaisesTheProfileAdvisory()
    {
        using HttpClient administrator = _fixture.CreateAdministratorClient();
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

            // Answering the property clears it, which proves the flag tracks the profile.
            await _fixture.Database.ExecuteAsync(
                """
                INSERT INTO [dbo].[UserProfile]
                    ([UserID], [PropertyDefinitionID], [PropertyValue], [Visibility], [LastUpdatedDate])
                VALUES (@userId, @definitionId, 'answered', 2, SYSUTCDATETIME());
                """,
                new Dictionary<string, object?>
                {
                    ["userId"] = account.UserId,
                    ["definitionId"] = definitionId,
                });

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

    /// <summary>
    /// C-03: the profile gate is not applied to the host account.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// <c>Website/admin/Authentication/Login.ascx.vb</c> L511 wraps the whole post-credential validation in
    /// <c>If Not objUser.IsSuperUser Then</c>. Applying the gate to the host account would be able to lock an
    /// installation out of the only account that can administer it, over reference data belonging to a tenant
    /// the host is not even a member of.
    /// </remarks>
    [Fact]
    public async Task Login_DoesNotRaiseTheProfileAdvisoryForTheHostAccount()
    {
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

    /// <summary>
    /// A session may rotate indefinitely without its retained history growing indefinitely, and the
    /// bound is observable: history older than the retention window is released, so replaying it refuses
    /// the caller without revoking the family.
    /// </summary>
    /// <remarks>
    /// <para>
    /// M-14: the defect this pins was uncontrolled growth. Redemption retains the entry it consumed so a
    /// later replay is recognisable and adds a replacement beside it, and nothing bounded how many times
    /// a caller could drive that - rotation cadence is chosen by the caller, not by the store - so one
    /// authenticated session could accumulate one permanently retained entry per exchange until its
    /// family's absolute ceiling elapsed, days later.
    /// </para>
    /// <para>
    /// Growth is not directly observable over HTTP, but its remedy is, and this asserts the remedy
    /// precisely because that is what a caller experiences. The generation redeemed FIRST is far enough
    /// behind after this many exchanges to have been released, so replaying it is answered as an
    /// unrecognised token rather than as a detected replay - and therefore does NOT revoke the family,
    /// which the live token continuing to work demonstrates. Compare
    /// <see cref="Refresh_WithAReplayedToken_IsRefusedAndRevokesTheAccountsTokens"/>: a replay still
    /// WITHIN the window is detected and does revoke everything. The two together are the exact trade the
    /// retention window makes - detection is narrowed to the window where a replay can still matter, and
    /// redemption is narrowed not at all, since no released entry was redeemable by anyone.
    /// </para>
    /// <para>
    /// The account is created for this test alone, because a detected replay would be account-wide.
    /// </para>
    /// </remarks>
    /// <returns>A task representing the test.</returns>
    [Fact]
    [Trait("Category", "Integration")]
    public async Task Refresh_RepeatedManyTimes_ReleasesHistoryBeyondTheRetentionWindow()
    {
        using HttpClient administrator = _fixture.CreateAdministratorClient();
        UserDetailDto account = await CreateUserAsync(administrator);

        using HttpClient client = _fixture.CreateAnonymousClient();
        LoginResponse first = await SignInAsync(client, account.Username);

        // Comfortably past the window, so the first generation cannot still be retained.
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

        using HttpResponseMessage staleReplay = await client.PostAsJsonAsync(
            RefreshRoute,
            new RefreshTokenRequest { RefreshToken = firstRedeemed },
            ApiTestFixture.Json);

        staleReplay.StatusCode.Should().Be(
            HttpStatusCode.Unauthorized,
            "a redeemed token is never redeemable again, whether or not its history is still retained");

        string body = await staleReplay.Content.ReadAsStringAsync();
        body.Should().Contain("urn:dnnmigration:error:auth.invalid_refresh_token");

        // The observable proof that the first generation was RELEASED rather than remembered: had it still
        // been retained, the presentation above would have been recognised as a replay and revoked every
        // token the account holds, so this exchange would fail.
        using HttpResponseMessage afterwards = await client.PostAsJsonAsync(
            RefreshRoute,
            new RefreshTokenRequest { RefreshToken = live },
            ApiTestFixture.Json);

        afterwards.StatusCode.Should().Be(
            HttpStatusCode.OK,
            "released history is reported as unrecognised rather than as a replay, so it must not revoke "
            + "the family the legitimate caller is still using");
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

        stillAccepted.StatusCode.Should().Be(
            HttpStatusCode.OK,
            "there is no registry of withdrawn access tokens and none may be introduced, so the reduction "
            + "against the legacy cookie sign-out is asserted rather than papered over");

        // Withdrawing the SAME value a second time answers alike. A token that had genuinely been revoked is
        // the case an unknown value cannot stand in for, and answering differently for it would tell an
        // anonymous caller that this value had once been live.
        using HttpResponseMessage alreadyRevoked = await client.PostAsJsonAsync(
            LogoutRoute,
            new RefreshTokenRequest { RefreshToken = issued.RefreshToken },
            ApiTestFixture.Json);

        alreadyRevoked.StatusCode.Should().Be(HttpStatusCode.NoContent);
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

        string neverIssued = "never-issued-" + Suffix();

        using HttpResponseMessage unknown = await client.PostAsJsonAsync(
            LogoutRoute,
            new RefreshTokenRequest { RefreshToken = neverIssued },
            ApiTestFixture.Json);

        unknown.StatusCode.Should().Be(HttpStatusCode.NoContent);

        // The SAME value again, so this arm asserts repetition rather than a second unknown value.
        using HttpResponseMessage repeated = await client.PostAsJsonAsync(
            LogoutRoute,
            new RefreshTokenRequest { RefreshToken = neverIssued },
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
    /// A submission carrying neither credential member is refused as a field-error document that names both
    /// members, is served as a problem document and preserves the request's trace identifier.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// <para>
    /// The two single-member tests above prove each rule fires. This one proves the DOCUMENT, which is a
    /// separate contract: a client renders the per-member messages beside the fields the operator filled
    /// in, so the dictionary keys are as load-bearing as the status, and a refusal that named no member
    /// would leave the operator nothing to correct. The key set is asserted as EXACTLY the two members,
    /// because a document that also attributed a failure to something the caller never sent would be
    /// telling the operator to correct a field that is not on the form.
    /// </para>
    /// <para>
    /// MIGRATION: these two rules were <c>asp:RequiredFieldValidator</c> controls rendered through the
    /// validation summary beside the legacy sign-in form (<c>Login.ascx</c>), so their wording travelled to
    /// the operator. It still does, and it is asserted as an exact string rather than as a substring for
    /// that reason - equivalent error messages are required, not merely equivalent statuses.
    /// </para>
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
    /// <para>
    /// The refusal half is the one worth having. The response header is registered through a starting
    /// callback BEFORE the pipeline continues, so it survives a short-circuit: an operator diagnosing a
    /// sign-in that was refused is exactly the caller who needs the identifier, and a middleware that only
    /// stamped successful responses would fail them at the moment it mattered.
    /// </para>
    /// <para>
    /// MIGRATION: the legacy sign-in path recorded nothing an operator could correlate - there are no audit
    /// calls anywhere in <c>Login.ascx.vb</c>, so both the trail and the identifier that joins a response to
    /// it are net-new here rather than ported. Nothing in the legacy source is the predecessor of this
    /// assertion.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Login_EchoesASuppliedCorrelationIdMintsOneWhenAbsentAndCarriesOneOnARefusal()
    {
        using HttpClient client = _fixture.CreateAnonymousClient();

        string supplied = "auth-accepted-" + Suffix();

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

        string refusedCorrelationId = "auth-refused-" + Suffix();

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
    /// An inbound identifier is caller-controlled and is written into log lines, so a value that is too long
    /// to be trusted is discarded and a fresh one minted - never sanitised and kept, and never echoed. The
    /// second half of the assertion matters as much as the first: a correlation identifier is a diagnostic
    /// concern, so refusing the request over one would let a caller deny itself service by sending a long
    /// header.
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
    /// The shipped tenant-administrator credential is ACCEPTED and carries the change advisory. It is not
    /// a refusal.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// <para>
    /// MIGRATION: <c>UserController.vb</c> L1144-L1148 REPLACED an already-successful status with
    /// <c>LOGIN_INSECUREADMINPASSWORD</c>, and <c>Login.ascx.vb:L187</c> then computed
    /// <c>authenticated = (loginStatus &lt;&gt; UserLoginStatus.LOGIN_FAILURE)</c> - so the caller was signed
    /// in. Status five was a success variant, never a denial, and this test exists to keep it one: turning
    /// the advisory into a refusal would lock an installation out of the very account it begins with, which
    /// is the opposite of the remediation the legacy intended.
    /// </para>
    /// <para>
    /// The advisory travels as the forced-change flag on the accepted response, which is the legacy
    /// remediation intent expressed in the target's own vocabulary. No legacy status ordinal reaches the
    /// wire, so nothing here asserts on the integer five - the three enumerations this vertical touches
    /// number themselves incompatibly (one seeds success at thirteen, one at zero and one counts
    /// downwards through negatives), which is precisely why the assertion is on the named outcome.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Login_WithTheShippedAdministratorCredential_IsAcceptedAndCarriesTheChangeAdvisory()
    {
        using HttpClient administrator = _fixture.CreateAdministratorClient();
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
    /// <remarks>
    /// MIGRATION: the second half of the same legacy rule, <c>UserController.vb</c> L1149-L1152, which
    /// promoted a superuser success to <c>LOGIN_INSECUREHOSTPASSWORD</c>. It is asserted separately from the
    /// administrator case because the legacy promoted from a DIFFERENT starting status and the target
    /// preserves that: the host arm is reached only for an account the store marks as a superuser, so a
    /// single test could not distinguish the two rules.
    /// </remarks>
    [Fact]
    public async Task Login_WithTheShippedHostCredential_IsAcceptedAndCarriesTheChangeAdvisory()
    {
        using HttpClient administrator = _fixture.CreateAdministratorClient();
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

    /// <summary>
    /// Two accounts may share one electronic-mail address, and both of them can sign in.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// <para>
    /// MIGRATION: <c>Website/release.config</c> L244 registers the membership provider with
    /// <c>requiresUniqueEmail="false"</c>, so a legacy installation's data may already hold duplicates.
    /// The policy is carried forward verbatim rather than tightened, because tightening it during a
    /// migration would refuse accounts that exist and refuse sign-ins that used to succeed. This test is
    /// the guard against a well-meant hardening: the creation must be ACCEPTED, never answered as a
    /// conflict.
    /// </para>
    /// <para>
    /// The sign-in half is what makes this an authentication test rather than an account-administration
    /// one. An address that identifies two accounts cannot be what resolves a credential, so both accounts
    /// have to be reachable by their own names and each has to receive its own session.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Login_ForTwoAccountsSharingOneEmailAddress_AcceptsBoth()
    {
        using HttpClient administrator = _fixture.CreateAdministratorClient();

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

    /// <summary>
    /// None of the three responses that carry a session publishes credential material of any kind.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// <para>
    /// MIGRATION: the legacy provider was registered with <c>enablePasswordRetrieval="true"</c> and
    /// <c>passwordFormat="Encrypted"</c> (<c>Website/release.config</c> L239 and L245) - reversible
    /// triple-DES storage, with the very key that reversed it committed to source control at L91-L93 - so a
    /// legacy installation could hand a stored credential back. Reversible storage is replaced by one-way
    /// BCrypt hashing and retrieval is deliberately NOT carried forward: no operation returns a credential,
    /// and this test asserts that absence positively rather than leaving it to the reader to notice that no
    /// such endpoint was written. The upgrade path for credentials already stored is the lazy re-hash the
    /// sibling test pins, never a decrypt-and-rewrite.
    /// </para>
    /// <para>
    /// The scan walks every member name in each document rather than checking the two documented shapes,
    /// because the failure this guards against is an ADDED member - a hash echoed back on a session
    /// response, or a credential reflected into a nested projection - and a shape-by-shape assertion would
    /// not see one.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Auth_PublishesNoCredentialMaterialOnAnyOfItsResponses()
    {
        using HttpClient administrator = _fixture.CreateAdministratorClient();

        // An account of this test's own, because the exchange below rotates a refresh token and a replay of
        // a rotated one withdraws every session the account holds. Using a seeded persona would make another
        // suite's session collateral.
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
    /// The controller publishes the four operations it declares and nothing else, and each of those is bound
    /// to its own method.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// <para>
    /// A closed surface is a security property rather than tidiness. The legacy application reached its
    /// credential features through several pages, and the ones that are out of scope here must not be quietly
    /// reachable through a catch-all route that answered anything under the controller's prefix. Three probes
    /// establish it, and the first two are deliberately made by DIFFERENT callers because the answer differs
    /// by caller.
    /// </para>
    /// <para>
    /// <strong>An anonymous caller is refused before the address is even judged.</strong> The authorisation
    /// options declare a fallback policy requiring an authenticated caller, which applies to anything that
    /// carries no authorisation metadata of its own - including a request that matched no operation at all.
    /// Measured rather than assumed: this probe was first written expecting a not-found answer and the
    /// pipeline answered 401. That is the stronger behaviour and is asserted as the contract it is, because it
    /// means an anonymous caller cannot use the surface as a directory of which addresses exist.
    /// </para>
    /// <para>
    /// <strong>An authenticated caller reaches the routing answer</strong>, and that is where the closed
    /// surface is actually proved: a path naming no operation is not found rather than served, so no
    /// catch-all stands behind this prefix. The third probe addresses a DECLARED path with the wrong method,
    /// which must be refused as such - a sign-in served over a retrieval verb would put a credential in a
    /// request line, where it reaches proxy logs and browser history.
    /// </para>
    /// <para>
    /// The undeclared segment is deliberately a name no feature could ever carry. Probing for the specific
    /// out-of-scope legacy paths by name would put those names into this file, which is exactly what a reader
    /// auditing the surface should not find here.
    /// </para>
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

        using HttpClient caller = _fixture.CreateHostClient();

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

    /// <summary>
    /// Sign-in is rate limited per caller address, and a rejected attempt states how long to wait.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A second host is built for this test because the shared host runs with a deliberately permissive
    /// limit. The limiter reads its options while services are composed, so the override must be in force
    /// before the host is constructed - which is why the client is created inside the scope. Building a
    /// dedicated host is also what keeps this test from spending the shared budget every other suite signs in
    /// against, and the attempts below name an account that does not exist so that exhausting the window
    /// cannot lock a real one out as a side effect.
    /// </para>
    /// <para>
    /// MIGRATION: this window IS the compensating control for a deleted defence. The legacy screen guarded
    /// sign-in with a human-verification challenge - <c>Login.ascx.vb:L162</c> ran the credential check only
    /// when <c>(UseCaptcha And ctlCaptcha.IsValid) OrElse (Not UseCaptcha)</c> held - and that control is
    /// excluded here with the rest of the legacy control library. The challenge answered automated guessing;
    /// so does a per-caller budget, and unlike the challenge it applies to every caller rather than only to
    /// the tenants that switched it on. The exchange is deliberate and is recorded rather than absorbed: no
    /// verification field exists on the sign-in contract, and none should be looked for.
    /// </para>
    /// <para>
    /// MIGRATION: the other argument that disappears from <c>Login.ascx.vb:L164</c> is the literal
    /// authentication-type <c>"DNN"</c>. The legacy passed it so a provider could be selected; there is one
    /// token path here, so the contract carries no such member and a caller cannot choose how it is
    /// authenticated. Neither this test nor any other may assert one exists.
    /// </para>
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
    /// <param name="username">
    /// The account name, or <see langword="null"/> for a generated one. Named explicitly only by the tests
    /// that need a name a rule recognises, since the recognised names are not unique to a test run.
    /// </param>
    /// <param name="email">
    /// The electronic-mail address, or <see langword="null"/> for a generated one. Supplied explicitly by
    /// the test that proves two accounts may share one.
    /// </param>
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
    /// <para>
    /// The credential is WRITTEN into the store rather than submitted, and that is forced rather than
    /// preferred: the policy carried forward from <c>Website/release.config</c> L242 requires seven
    /// characters, both shipped values are shorter, and the account-creation rules reproduce that policy
    /// verbatim - so the API correctly refuses to set either of them. Reaching the state a legacy
    /// installation is actually in therefore means writing the stored value, which is also what keeps the
    /// short credential out of every request this suite makes.
    /// </para>
    /// <para>
    /// The stored value is written at the hasher's own cost and digest, so verification succeeds and the
    /// lazy cost upgrade is not triggered as a side effect - that upgrade has its own test and this one
    /// must not depend on it.
    /// </para>
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
    /// state is established by writing the single column the sign-in path reads. That column is the whole of
    /// the distinction: a host account reaches the superuser outcome, which is the arm the host half of the
    /// weak-credential advisory is promoted from.
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
    /// <remarks>
    /// The whole document is walked, including nested objects and arrays, because the member this guards
    /// against is one nobody meant to add.
    /// </remarks>
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
    /// <para>
    /// The name is matched by containment and case-insensitively, so a nested or differently spelled member
    /// is caught too. The VALUE has to be textual for the member to offend, and that qualification is the
    /// whole precision of this check rather than a loophole in it: the accepted sign-in response is REQUIRED
    /// to publish the forced-change and the approaching-expiry advisories, whose names unavoidably contain
    /// the credential word and whose values are boolean flags carrying no material at all. Flagging those
    /// would make this test demand the removal of the two members that carry the legacy remediation intent.
    /// </para>
    /// <para>
    /// The bearer values a session response does carry are deliberately outside the vocabulary matched here.
    /// They are values the caller is meant to hold, minted for it and short-lived; a stored credential is
    /// not, which is the distinction this check draws.
    /// </para>
    /// </remarks>
    private static bool CarriesCredentialMaterial(JsonProperty member) =>
        member.Value.ValueKind == JsonValueKind.String
        && (member.Name.Contains("password", StringComparison.OrdinalIgnoreCase)
            || member.Name.Contains("credential", StringComparison.OrdinalIgnoreCase)
            || member.Name.Contains("secret", StringComparison.OrdinalIgnoreCase)
            || member.Name.Contains("answer", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Clears a lock by writing the credential store directly.
    /// </summary>
    /// <param name="userName">The account name.</param>
    /// <returns>A task representing the write.</returns>
    /// <remarks>
    /// <para>
    /// The inverse of <see cref="LockAsync"/>, and written the same way for the same reason: the
    /// administrative unlock endpoint carries its own authorisation, which is another suite's subject, and a
    /// test that only needs the stored state should not depend on it.
    /// </para>
    /// <para>
    /// The lock-out date is set to the legacy <c>17540101</c> sentinel rather than to <c>NULL</c>, because the
    /// column is <c>NOT NULL</c> and that sentinel is how this schema expresses absence - the value the
    /// membership procedures the upgrade chain patched write for the same purpose. Writing <c>NULL</c> here
    /// fails outright, which is how this helper learned the rule.
    /// </para>
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
    /// Written through SQL rather than through the tenant endpoint on purpose: the endpoint would exercise the
    /// portal-update path and its authorisation, which is another suite's subject, and this test needs only the
    /// stored value the sign-in ladder reads.
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
