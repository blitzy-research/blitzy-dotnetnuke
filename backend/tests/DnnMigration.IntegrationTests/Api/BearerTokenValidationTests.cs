using System.Globalization;
using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using DnnMigration.Application.Abstractions;
using DnnMigration.Application.Dtos.Auth;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.IdentityModel.Tokens;
using Xunit;

namespace DnnMigration.IntegrationTests.Api;

/// <summary>
/// Covers the bearer material the API must REFUSE, and the session surface it must honour.
/// </summary>
/// <remarks>
/// <para>
/// Every other suite now obtains its callers by signing in, so every other suite presents a token this API
/// itself issued. That is the right default - it is what makes a protected-endpoint pass mean anything - but
/// it leaves an entire half of the authentication contract unexercised: the refusals. A token that has
/// expired, one whose validity has not begun, one signed with a key this installation does not hold, one
/// naming another issuer or audience, one carrying no signature at all, one signed with an algorithm the host
/// does not permit, one whose payload was edited after signing, and one that is not a token at all must each
/// be refused. None of those can be obtained by signing in, so this is the one suite that mints its own
/// bearer material - and the only reason the minting seam still exists.
/// </para>
/// <para>
/// <b>Why a control fact opens the suite.</b> Each refusal below asserts <c>401</c> from a protected
/// endpoint, and a broken endpoint would answer <c>401</c> to everything - so a suite of refusals alone can
/// pass while proving nothing. The first fact signs in for real and asserts the SAME endpoint answers
/// <c>200</c>, which is what makes every refusal after it attributable to the token that was presented.
/// </para>
/// <para>
/// <b>Why the probe is the current-account endpoint.</b> It is authenticated and carries no permission policy,
/// so a refusal there is the authentication stage's answer and cannot be an authorisation decision wearing the
/// same status code. One further fact deliberately probes a POLICY-protected route with an expired token, to
/// establish the ordering: authentication runs first, so the answer is <c>401</c> and never the <c>403</c> the
/// same caller would receive if the token had been accepted.
/// </para>
/// <para>
/// <b>The clock skew is a configured value and this suite respects it.</b> The host permits thirty seconds,
/// so an "expired" token is expired by minutes rather than by seconds, and a token expired well INSIDE the
/// window is asserted to be admitted - because that tolerance exists to stop a small clock difference between
/// two hosts from locking every caller out, and silently losing it would be a regression no refusal test could
/// see.
/// </para>
/// </remarks>
[Trait("Category", "Integration")]
[Collection(IntegrationTestCollection.Name)]
public sealed class BearerTokenValidationTests
{
    /// <summary>The authenticated endpoint that carries no permission policy.</summary>
    private static readonly Uri MeRoute = new("/api/v1/auth/me", UriKind.Relative);

    /// <summary>A policy-protected collection, used once to establish the pipeline ordering.</summary>
    private static readonly Uri PortalsRoute =
        new("/api/v1/portals?pageIndex=0&pageSize=1", UriKind.Relative);

    /// <summary>The problem type a request the pipeline could not authenticate carries.</summary>
    private const string UnauthenticatedType = "urn:dnnmigration:error:auth.unauthenticated";

    /// <summary>
    /// A signing key of the required length that this installation does not hold.
    /// </summary>
    /// <remarks>
    /// Long enough to satisfy the key-length rule the signing algorithm imposes, so that a refusal is
    /// attributable to the key being FOREIGN rather than to it being unusable. It names no placeholder
    /// fragment the host's own settings validator forbids, for the same reason: a rejected configuration
    /// would never reach the comparison this fact is about.
    /// </remarks>
    private const string ForeignSigningKey = "a-different-installations-signing-key-0123456789abcdef";

    private readonly ApiTestFixture _fixture;

    /// <summary>Initialises a new instance of the <see cref="BearerTokenValidationTests"/> class.</summary>
    /// <param name="fixture">The shared host and database.</param>
    public BearerTokenValidationTests(ApiTestFixture fixture) => _fixture = fixture;

    /// <summary>
    /// A token obtained by signing in is admitted, which is what makes every refusal below meaningful.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// The account facts are read as well as the status, because an endpoint that answered <c>200</c> with an
    /// empty body would satisfy a status-only assertion while proving that the token's claims were never read.
    /// </remarks>
    [Fact]
    public async Task IssuedToken_IsAdmittedByTheProtectedEndpoint()
    {
        using HttpClient client = await _fixture.CreateAdministratorClientAsync();

        using HttpResponseMessage response = await client.GetAsync(MeRoute);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        CurrentUserDto account = await ReadCurrentUserAsync(response);

        account.UserId.Should().Be(_fixture.Seed.AdminUserId);
        account.Username.Should().Be(IntegrationSeed.AdminUserName);
        account.PortalId.Should().Be(_fixture.Seed.PortalId);
    }

    /// <summary>An expired token is refused, and the refusal is an authentication refusal.</summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// Expired by five minutes, which is an order of magnitude beyond the thirty-second tolerance the host
    /// permits, so the outcome cannot turn on how long this test took to reach the request.
    /// </remarks>
    [Fact]
    public async Task ExpiredToken_IsRefused()
    {
        using HttpClient client = MintedFor(
            notBefore: DateTime.UtcNow.AddMinutes(-35),
            lifetime: TimeSpan.FromMinutes(30));

        using HttpResponseMessage response = await client.GetAsync(MeRoute);

        await AssertUnauthenticatedAsync(response);
    }

    /// <summary>
    /// A token that expired INSIDE the permitted skew is still admitted, which is the tolerance the previous
    /// fact's margin exists to stay clear of.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// Expired five seconds ago against a thirty-second tolerance. Asserted because the tolerance is a
    /// deliberate configured value: two hosts whose clocks differ by a second must not reject each other's
    /// tokens, and a change that dropped the tolerance to zero would pass every refusal fact in this suite
    /// while breaking every caller in a real deployment.
    /// </remarks>
    [Fact]
    public async Task TokenExpiredWithinThePermittedSkew_IsStillAdmitted()
    {
        using HttpClient client = MintedFor(
            notBefore: DateTime.UtcNow.AddMinutes(-30).AddSeconds(-5),
            lifetime: TimeSpan.FromMinutes(30));

        using HttpResponseMessage response = await client.GetAsync(MeRoute);

        response.StatusCode.Should().Be(
            HttpStatusCode.OK,
            "the host permits thirty seconds of clock difference and this token is five seconds past its "
            + "expiry");
    }

    /// <summary>A token whose validity has not yet begun is refused.</summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// The mirror image of expiry, and a distinct check: a validator that read only the expiry claim would
    /// accept a token minted for use tomorrow. That matters because a not-before far in the future is how a
    /// stolen signing key would be used to prepare credentials in advance.
    /// </remarks>
    [Fact]
    public async Task NotYetValidToken_IsRefused()
    {
        using HttpClient client = MintedFor(
            notBefore: DateTime.UtcNow.AddMinutes(10),
            lifetime: TimeSpan.FromMinutes(30));

        using HttpResponseMessage response = await client.GetAsync(MeRoute);

        await AssertUnauthenticatedAsync(response);
    }

    /// <summary>A token signed with a key this installation does not hold is refused.</summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// The single most important refusal in the suite. Every claim in the token is otherwise exactly what a
    /// genuine one carries - the same subject, the same tenant, the same issuer and audience, an unexpired
    /// lifetime - so the only thing that can refuse it is the signature check. An installation that skipped
    /// that check would let anybody who knows the claim shape mint an administrator.
    /// </remarks>
    [Fact]
    public async Task TokenSignedWithAForeignKey_IsRefused()
    {
        using HttpClient client = _fixture.CreateClientWithMintedBearer(
            _fixture.Seed.AdminUserId,
            IntegrationSeed.AdminUserName,
            _fixture.Seed.PortalId,
            signingSecret: ForeignSigningKey);

        using HttpResponseMessage response = await client.GetAsync(MeRoute);

        await AssertUnauthenticatedAsync(response);
    }

    /// <summary>A token naming another issuer is refused.</summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// Signed with the host's OWN key, so the signature verifies and the refusal can only come from the issuer
    /// comparison. This is the check that stops a token minted for a sibling deployment that shares a key -
    /// through a copied configuration, or a key rotated into two places - from being spent here.
    /// </remarks>
    [Fact]
    public async Task TokenNamingAnotherIssuer_IsRefused()
    {
        using HttpClient client = _fixture.CreateClientWithMintedBearer(
            _fixture.Seed.AdminUserId,
            IntegrationSeed.AdminUserName,
            _fixture.Seed.PortalId,
            issuer: "https://another-installation.example");

        using HttpResponseMessage response = await client.GetAsync(MeRoute);

        await AssertUnauthenticatedAsync(response);
    }

    /// <summary>A token naming another audience is refused.</summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// Also signed with the host's own key and naming the host's own issuer, so this isolates the audience
    /// comparison. It is what stops a token this very installation issued for a DIFFERENT service - a
    /// companion API sharing the signing key - from being replayed against this one.
    /// </remarks>
    [Fact]
    public async Task TokenNamingAnotherAudience_IsRefused()
    {
        using HttpClient client = _fixture.CreateClientWithMintedBearer(
            _fixture.Seed.AdminUserId,
            IntegrationSeed.AdminUserName,
            _fixture.Seed.PortalId,
            audience: "another-service");

        using HttpResponseMessage response = await client.GetAsync(MeRoute);

        await AssertUnauthenticatedAsync(response);
    }

    /// <summary>A token carrying no signature at all is refused.</summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// The classic downgrade: a caller re-writes the header to declare that the token is unsigned and drops
    /// the signature segment, betting that the validator will believe the header. The host requires signed
    /// tokens and permits exactly one algorithm, so the bet loses - and this fact is what keeps both of those
    /// settings from being relaxed unnoticed.
    /// </remarks>
    [Fact]
    public async Task UnsignedToken_IsRefused()
    {
        var unsigned = new JwtSecurityToken(
            issuer: ApiTestFixture.Issuer,
            audience: ApiTestFixture.Audience,
            claims: GenuineClaims(),
            notBefore: DateTime.UtcNow.AddMinutes(-1),
            expires: DateTime.UtcNow.AddMinutes(29));

        using HttpClient client = WithBearer(new JwtSecurityTokenHandler().WriteToken(unsigned));

        using HttpResponseMessage response = await client.GetAsync(MeRoute);

        await AssertUnauthenticatedAsync(response);
    }

    /// <summary>A token signed with an algorithm the host does not permit is refused.</summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// Signed with the host's own key, correctly, using a STRONGER algorithm than the one configured. The
    /// refusal is therefore not about strength: it is about the validator accepting only the algorithm it was
    /// told to accept, which is what closes the family of attacks that work by choosing the algorithm for the
    /// verifier. A validator that trusted the header here would also trust it in the unsigned case above.
    /// </remarks>
    [Fact]
    public async Task TokenSignedWithAnUnpermittedAlgorithm_IsRefused()
    {
        var credentials = new SigningCredentials(
            new SymmetricSecurityKey(Encoding.UTF8.GetBytes(ApiTestFixture.SigningSecret + ApiTestFixture.SigningSecret)),
            SecurityAlgorithms.HmacSha512);

        var token = new JwtSecurityToken(
            issuer: ApiTestFixture.Issuer,
            audience: ApiTestFixture.Audience,
            claims: GenuineClaims(),
            notBefore: DateTime.UtcNow.AddMinutes(-1),
            expires: DateTime.UtcNow.AddMinutes(29),
            signingCredentials: credentials);

        using HttpClient client = WithBearer(new JwtSecurityTokenHandler().WriteToken(token));

        using HttpResponseMessage response = await client.GetAsync(MeRoute);

        await AssertUnauthenticatedAsync(response);
    }

    /// <summary>A genuine token whose payload was edited after signing is refused.</summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// The token comes from a REAL sign-in and only its payload segment is disturbed, so this is the closest
    /// this suite comes to the actual attack: a caller who holds a valid token of their own and edits the
    /// claims in it. The edit is a single character, which is enough for the signature to disagree, and no
    /// assertion is made about which claim changed - the point is that no edit survives at all.
    /// </remarks>
    [Fact]
    public async Task TokenWithATamperedPayload_IsRefused()
    {
        string issued = await AuthenticatedClientFactory.GetAccessTokenAsync(
            _fixture,
            IntegrationSeed.AdminUserName,
            ApiTestFixture.KnownPassword);

        string[] segments = issued.Split('.');
        segments.Should().HaveCount(3, "a compact serialised token is header, payload and signature");

        using HttpClient client = WithBearer(
            segments[0] + '.' + Disturb(segments[1]) + '.' + segments[2]);

        using HttpResponseMessage response = await client.GetAsync(MeRoute);

        await AssertUnauthenticatedAsync(response);
    }

    /// <summary>A genuine token with its signature removed is refused.</summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// Distinct from the tampered payload above and from the unsigned token before it: here the header still
    /// declares the algorithm the host permits, and the material that should prove it is simply absent. A
    /// validator that treated an empty signature as "nothing to disagree with" would accept every token any
    /// caller cared to write.
    /// </remarks>
    [Fact]
    public async Task TokenWithItsSignatureRemoved_IsRefused()
    {
        string issued = await AuthenticatedClientFactory.GetAccessTokenAsync(
            _fixture,
            IntegrationSeed.AdminUserName,
            ApiTestFixture.KnownPassword);

        using HttpClient client = WithBearer(issued[..(issued.LastIndexOf('.') + 1)]);

        using HttpResponseMessage response = await client.GetAsync(MeRoute);

        await AssertUnauthenticatedAsync(response);
    }

    /// <summary>Material that is not a token at all is refused rather than faulting the process.</summary>
    /// <param name="presented">The value presented as a bearer token.</param>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// A parser reached by an unauthenticated caller is a denial-of-service surface, so the requirement is
    /// twofold: refuse, and refuse with the authentication vocabulary rather than with a server fault. The
    /// cases are the shapes such material actually arrives in - a bare word, a truncated token, a token with
    /// too many segments, and a segment that is not valid base64url at all.
    /// </remarks>
    [Theory]
    [InlineData("not-a-token")]
    [InlineData("eyJhbGciOiJIUzI1NiJ9")]
    [InlineData("one.two")]
    [InlineData("one.two.three.four")]
    [InlineData("!!!.???.***")]
    public async Task MalformedBearerMaterial_IsRefused(string presented)
    {
        using HttpClient client = WithBearer(presented);

        using HttpResponseMessage response = await client.GetAsync(MeRoute);

        await AssertUnauthenticatedAsync(response);
    }

    /// <summary>A genuine token presented under another authentication scheme is not accepted.</summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// The token is real and unexpired; only the scheme is wrong. The API registers one scheme, so a caller
    /// who guesses at another must be treated as having presented no credential - not as having presented this
    /// one under a different name.
    /// </remarks>
    [Fact]
    public async Task IssuedTokenPresentedUnderAnotherScheme_IsNotAccepted()
    {
        string issued = await AuthenticatedClientFactory.GetAccessTokenAsync(
            _fixture,
            IntegrationSeed.AdminUserName,
            ApiTestFixture.KnownPassword);

        using HttpClient client = _fixture.CreateAnonymousClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", issued);

        using HttpResponseMessage response = await client.GetAsync(MeRoute);

        await AssertUnauthenticatedAsync(response);
    }

    /// <summary>
    /// A token that validates cryptographically but carries no subject claim is authenticated and then reaches
    /// no account, and is refused outright by a policy.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// <para>
    /// This one is NOT an authentication refusal, and the distinction is the point: the signature, issuer,
    /// audience and lifetime are all sound, so the pipeline authenticates the request and the claim gap is
    /// discovered afterwards. Both consequences are asserted, because either one alone could be satisfied by
    /// the wrong mechanism. The account endpoint answers <c>404</c> - authenticated, but naming no account
    /// that exists - and the tenant-scoped route answers <c>403</c>, because a policy that cannot read a
    /// caller's account key cannot verify a role assignment for it and must fail closed.
    /// </para>
    /// <para>
    /// The absence of <c>200</c> is the load-bearing half. A pipeline that defaulted an unreadable subject to
    /// zero would reach the FIRST row of a table seeded from zero, and the response would be indistinguishable
    /// from a legitimate one.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task TokenWithNoSubjectClaim_IsAuthenticatedAndThenReachesNoAccount()
    {
        string token = SignedWithout(DnnClaimTypes.Subject);

        using HttpClient client = WithBearer(token);

        using HttpResponseMessage account = await client.GetAsync(MeRoute);

        account.StatusCode.Should().Be(
            HttpStatusCode.NotFound,
            "the token authenticates, so the refusal is about the account it fails to name");

        ProblemDetails? problem = await account.Content
            .ReadFromJsonAsync<ProblemDetails>(ApiTestFixture.Json);

        problem.Should().NotBeNull("every refusal carries the shared problem document");
        problem!.Status.Should().Be((int)HttpStatusCode.NotFound);

        using HttpClient policyProbe = WithBearer(token);

        using HttpResponseMessage policy = await policyProbe.GetAsync(TenantRoute());

        policy.StatusCode.Should().Be(
            HttpStatusCode.Forbidden,
            "a policy that cannot read the caller's account key cannot verify an assignment and must deny");
    }

    /// <summary>A token whose subject claim is not an account key reaches no account.</summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// The garbled counterpart of the missing claim, and the one a defect is likelier to produce than an
    /// attacker: a subject written as a name, or carried over from another identity system, must not be
    /// coerced into a key. The answer is the same <c>404</c> the missing claim produces, which is itself worth
    /// pinning - an unreadable subject and an absent one are the same situation and must not be reported
    /// differently. A parse that fell back to zero would instead reach the FIRST account in a table seeded
    /// from zero and answer <c>200</c> with somebody else's record.
    /// </remarks>
    [Fact]
    public async Task TokenWithANonNumericSubject_ReachesNoAccount()
    {
        using HttpClient client = WithBearer(
            SignedWith(claim => claim.Type == DnnClaimTypes.Subject
                ? new Claim(DnnClaimTypes.Subject, "not-an-account-key")
                : claim));

        using HttpResponseMessage response = await client.GetAsync(MeRoute);

        response.StatusCode.Should().Be(
            HttpStatusCode.NotFound,
            "an unreadable subject names no account, exactly as an absent one does");
    }

    /// <summary>
    /// A token carrying no tenant claim is refused by a tenant-scoped policy rather than admitted to the
    /// tenant the request happens to have arrived at.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// The seeded administrator's own token, minus the tenant claim, presented to the seeded tenant's own
    /// route - which the same caller reaches successfully with a complete token. Administering a portal
    /// requires the tenant the token was issued for to be the tenant the route is about, so a token that
    /// cannot say which tenant it was issued for must be refused rather than given the benefit of the host
    /// name. Without this the claim would be optional in practice, and a token minted in one tenant would
    /// administer another simply by being addressed there.
    /// </remarks>
    [Fact]
    public async Task TokenWithNoTenantClaim_IsRefusedByATenantScopedPolicy()
    {
        Uri tenantRoute = TenantRoute();

        using HttpClient complete = await _fixture.CreateAdministratorClientAsync();

        using HttpResponseMessage admitted = await complete.GetAsync(tenantRoute);
        admitted.StatusCode.Should().Be(
            HttpStatusCode.OK,
            "the caller administers this tenant, so the route is reachable with a complete token");

        using HttpClient without = WithBearer(SignedWithout(DnnClaimTypes.PortalId));

        using HttpResponseMessage refused = await without.GetAsync(tenantRoute);

        refused.StatusCode.Should().Be(
            HttpStatusCode.Forbidden,
            "the request is authenticated, so the refusal is an authorisation decision and not a 401");
    }

    /// <summary>
    /// An expired token is refused by a POLICY-protected route with <c>401</c>, not <c>403</c>.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// Establishes the pipeline ordering, which no other fact in this suite can: the same caller presenting a
    /// VALID token reaches this route successfully, and an ordinary member presenting a valid token is refused
    /// with <c>403</c>. A <c>403</c> here would therefore mean the expired token had been accepted and the
    /// refusal had come from the policy instead - the pass would look identical and mean the opposite.
    /// </remarks>
    [Fact]
    public async Task ExpiredTokenOnAPolicyProtectedRoute_IsRefusedAsUnauthenticated()
    {
        using HttpClient host = await _fixture.CreateHostClientAsync();

        using HttpResponseMessage admitted = await host.GetAsync(PortalsRoute);
        admitted.StatusCode.Should().Be(
            HttpStatusCode.OK,
            "a host account reaches this collection, so a refusal below is about the token");

        using HttpClient expired = _fixture.CreateClientWithMintedBearer(
            _fixture.Seed.HostUserId,
            IntegrationSeed.HostUserName,
            _fixture.Seed.PortalId,
            isSuperUser: true,
            notBefore: DateTime.UtcNow.AddMinutes(-35),
            lifetime: TimeSpan.FromMinutes(30));

        using HttpResponseMessage response = await expired.GetAsync(PortalsRoute);

        await AssertUnauthenticatedAsync(response);
    }

    /// <summary>
    /// A sign-in performed without the suite's session cache issues a usable session, and that session can be
    /// read, rotated and ended through the endpoints the API publishes for it.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// <para>
    /// Deliberately uncached. Every other fact in this assembly reuses one sign-in per persona, which is the
    /// right trade for a suite that needs a caller rather than a session - but it means the SECOND sign-in of
    /// a persona is never performed, and rotating or ending a shared session would disturb every later fact.
    /// An independent session avoids both problems and is the only honest way to spend a refresh token.
    /// </para>
    /// <para>
    /// The four steps are asserted in sequence because they only mean anything together: an issued pair that
    /// cannot be spent, a rotation that returns a token the API will not accept, or a sign-out that leaves the
    /// refresh token usable would each pass a narrower test.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task AnUncachedSession_CanBeReadRotatedAndEnded()
    {
        LoginResponse issued = await AuthenticatedClientFactory.LoginWithoutCacheAsync(
            _fixture,
            IntegrationSeed.MemberUserName,
            ApiTestFixture.KnownPassword);

        issued.AccessToken.Should().NotBeNullOrWhiteSpace();
        issued.RefreshToken.Should().NotBeNullOrWhiteSpace();

        using HttpClient signedIn = WithBearer(issued.AccessToken);

        CurrentUserDto account = await AuthenticatedClientFactory.GetCurrentUserAsync(signedIn);

        account.UserId.Should().Be(_fixture.Seed.MemberUserId);
        account.Username.Should().Be(IntegrationSeed.MemberUserName);

        LoginResponse rotated = await AuthenticatedClientFactory.RefreshAsync(_fixture, issued.RefreshToken);

        rotated.RefreshToken.Should().NotBe(
            issued.RefreshToken,
            "a rotation replaces the refresh token rather than re-issuing it");

        using HttpClient afterRotationClient = WithBearer(rotated.AccessToken);

        CurrentUserDto afterRotation = await AuthenticatedClientFactory
            .GetCurrentUserAsync(afterRotationClient);

        afterRotation.UserId.Should().Be(_fixture.Seed.MemberUserId);

        await AuthenticatedClientFactory.LogoutAsync(_fixture, rotated.RefreshToken);

        Func<Task> spendingTheEndedSession = () =>
            AuthenticatedClientFactory.RefreshAsync(_fixture, rotated.RefreshToken);

        await spendingTheEndedSession.Should().ThrowAsync<InvalidOperationException>(
            "the refresh token was revoked by the sign-out, so it can no longer be spent");
    }

    /// <summary>
    /// Dropping the suite's cached sessions changes nothing a test can observe, because the cache holds only
    /// what a sign-in would produce again.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// The cache is what keeps several hundred real sign-ins from being performed, so it is load-bearing for
    /// the whole assembly - and a cache that could mask a broken sign-in would undo the very change that made
    /// these suites authenticate for real. Forgetting the sessions and immediately signing in again is what
    /// proves it cannot: the caller that comes back is the same caller, obtained the same way, from the same
    /// endpoint.
    /// </remarks>
    [Fact]
    public async Task ForgettingTheCachedSessions_LeavesEveryPersonaObtainable()
    {
        using HttpClient before = await _fixture.CreateAdministratorClientAsync();

        using HttpResponseMessage first = await before.GetAsync(MeRoute);
        first.StatusCode.Should().Be(HttpStatusCode.OK);

        AuthenticatedClientFactory.ForgetCachedSessions(_fixture);

        using HttpClient after = await _fixture.CreateAdministratorClientAsync();

        using HttpResponseMessage second = await after.GetAsync(MeRoute);
        second.StatusCode.Should().Be(
            HttpStatusCode.OK,
            "the persona is obtained by signing in, so a forgotten cache costs a sign-in and nothing else");

        (await ReadCurrentUserAsync(second)).UserId.Should().Be(_fixture.Seed.AdminUserId);
    }

    /// <summary>Asserts that a response is the pipeline's authentication refusal.</summary>
    /// <param name="response">The response to examine.</param>
    /// <returns>A task representing the assertion.</returns>
    /// <remarks>
    /// The bearer challenge header is asserted alongside the status and the problem document because all three
    /// belong to the same contract: a client that cannot see a challenge cannot know to refresh, and a body
    /// that omits the reason token leaves an operator to guess which of the refusals applied.
    /// </remarks>
    private static async Task AssertUnauthenticatedAsync(HttpResponseMessage response)
    {
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        response.Headers.WwwAuthenticate.Should().NotBeEmpty(
            "a 401 must tell the caller which scheme to present");

        ProblemDetails? problem = await response.Content
            .ReadFromJsonAsync<ProblemDetails>(ApiTestFixture.Json);

        problem.Should().NotBeNull();
        problem!.Type.Should().Be(UnauthenticatedType);
        problem.Status.Should().Be((int)HttpStatusCode.Unauthorized);
    }

    /// <summary>Reads the current-account payload out of the shared success envelope.</summary>
    /// <param name="response">The response to read.</param>
    /// <returns>The account the API reported.</returns>
    private static async Task<CurrentUserDto> ReadCurrentUserAsync(HttpResponseMessage response)
    {
        ApiEnvelope<CurrentUserDto>? envelope = await response.Content
            .ReadFromJsonAsync<ApiEnvelope<CurrentUserDto>>(ApiTestFixture.Json);

        envelope.Should().NotBeNull();
        envelope!.Data.Should().NotBeNull();

        return envelope.Data!;
    }

    /// <summary>Mints a token for the seeded administrator with a chosen validity window.</summary>
    /// <param name="notBefore">When the token becomes valid.</param>
    /// <param name="lifetime">How long it stays valid from that moment.</param>
    /// <returns>A client presenting the token.</returns>
    /// <remarks>
    /// The persona is the seeded administrator with every other claim exactly as a genuine token carries it,
    /// so that a temporal fact varies the validity window and nothing else. No role is handed over: the
    /// minter deliberately writes none, and the administrator's authority is read from the seeded row on the
    /// request that needs it.
    /// </remarks>
    private HttpClient MintedFor(DateTime notBefore, TimeSpan lifetime) =>
        _fixture.CreateClientWithMintedBearer(
            _fixture.Seed.AdminUserId,
            IntegrationSeed.AdminUserName,
            _fixture.Seed.PortalId,
            lifetime: lifetime,
            notBefore: notBefore);

    /// <summary>The seeded tenant's own resource, which its administrator reaches and nobody else does.</summary>
    /// <returns>A relative address naming the seeded tenant.</returns>
    /// <remarks>
    /// Named on the ROUTE rather than left to the host name, because that is what brings the tenant-scoped
    /// policy into play: the policy requires the tenant the route is about, the tenant the token was issued
    /// for and the tenant the request arrived at to agree, and a route that names none of them would be
    /// decided by a different rule.
    /// </remarks>
    private Uri TenantRoute() => new(
        "/api/v1/portals/" + _fixture.Seed.PortalId.ToString(CultureInfo.InvariantCulture),
        UriKind.Relative);

    /// <summary>Attaches arbitrary bearer material to an anonymous client.</summary>
    /// <param name="token">The value to present, which need not be a token.</param>
    /// <returns>A client presenting it.</returns>
    /// <remarks>
    /// The header is assigned directly rather than through the factory's own helper, because that helper
    /// refuses blank material and this suite needs to present values a helper would rightly reject.
    /// </remarks>
    private HttpClient WithBearer(string token)
    {
        HttpClient client = _fixture.CreateAnonymousClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        return client;
    }

    /// <summary>The claim set a genuine token for the seeded administrator carries.</summary>
    /// <returns>The claims, in the vocabulary the API reads.</returns>
    /// <remarks>
    /// <strong>MIGRATION:</strong> three claims, because that is what the reconciled contract mints - see
    /// <c>Application/Abstractions/ITokenService</c>, whose vocabulary declares only the subject, the tenant
    /// and the token identifier. A name, a host flag and a role entry were all considered and withdrawn: each
    /// is mutable state that a token would freeze for its whole lifetime, so every server-side guard re-reads
    /// them from authoritative storage per request instead. Restating them here would make this suite's
    /// "genuine" token richer than a real one, and a fact built on it would then be evidence about a token
    /// the host never issues.
    /// </remarks>
    private IEnumerable<Claim> GenuineClaims() =>
    [
        new(DnnClaimTypes.Subject, _fixture.Seed.AdminUserId.ToString(CultureInfo.InvariantCulture)),
        new(DnnClaimTypes.JwtId, Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture)),
        new(DnnClaimTypes.PortalId, _fixture.Seed.PortalId.ToString(CultureInfo.InvariantCulture)),
    ];

    /// <summary>Signs a genuine claim set with one claim type removed.</summary>
    /// <param name="claimType">The claim to leave out.</param>
    /// <returns>The compact serialised token.</returns>
    private string SignedWithout(string claimType) =>
        Sign(GenuineClaims().Where(claim => claim.Type != claimType));

    /// <summary>Signs a genuine claim set with each claim passed through a projection.</summary>
    /// <param name="project">Rewrites the claims it is given.</param>
    /// <returns>The compact serialised token.</returns>
    private string SignedWith(Func<Claim, Claim> project) => Sign(GenuineClaims().Select(project));

    /// <summary>
    /// Signs a claim set with the host's own key, issuer, audience and a valid window.
    /// </summary>
    /// <param name="claims">The claims to carry.</param>
    /// <returns>The compact serialised token.</returns>
    /// <remarks>
    /// Everything except the claim set is exactly what a genuine token carries, which is what makes a fact
    /// built on this attributable to the claims alone. Constructed the way production constructs it - the
    /// token type then the handler's writer - because the alternative applies the handler's outbound claim map
    /// and would rename the role claim on the way out, changing a second thing.
    /// </remarks>
    private static string Sign(IEnumerable<Claim> claims)
    {
        var credentials = new SigningCredentials(
            new SymmetricSecurityKey(Encoding.UTF8.GetBytes(ApiTestFixture.SigningSecret)),
            SecurityAlgorithms.HmacSha256);

        var token = new JwtSecurityToken(
            issuer: ApiTestFixture.Issuer,
            audience: ApiTestFixture.Audience,
            claims: claims,
            notBefore: DateTime.UtcNow.AddMinutes(-1),
            expires: DateTime.UtcNow.AddMinutes(29),
            signingCredentials: credentials);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    /// <summary>Changes one character of a token segment without changing its length.</summary>
    /// <param name="segment">The segment to disturb.</param>
    /// <returns>The disturbed segment.</returns>
    /// <remarks>
    /// The final character is replaced with a different one drawn from the same alphabet, so the result stays
    /// a well formed base64url segment of the same length. That is deliberate: a malformed segment would be
    /// refused by the parser, and this fact is about the SIGNATURE disagreeing rather than about the shape.
    /// </remarks>
    private static string Disturb(string segment)
    {
        char last = segment[^1];
        char replacement = last == 'A' ? 'B' : 'A';

        return segment[..^1] + replacement;
    }
}
