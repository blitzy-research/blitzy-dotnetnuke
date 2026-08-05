using System.Globalization;
using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DnnMigration.Api.Controllers;
using DnnMigration.Application.Abstractions;
using DnnMigration.Application.Dtos.Auth;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.IdentityModel.Tokens;

namespace DnnMigration.IntegrationTests;

/// <summary>
/// Produces pre-authenticated <see cref="HttpClient"/> instances for the integration suite, and the raw
/// bearer material a suite needs to assert on a token rather than merely spend one.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Consumers must carry the integration trait.</strong> Every test class in this project declares
/// <c>[Trait("Category", "Integration")]</c> and <c>[Collection(IntegrationTestCollection.Name)]</c>. The
/// acceptance gate selects tests with <c>--filter "Category=Integration"</c>, so a class without the trait is
/// invisible to it - the test compiles, runs locally, and silently proves nothing at the gate. The collection
/// declaration is what shares one host, one database and one seed across the run. This type is a helper rather
/// than a test and carries no trait of its own; the obligation belongs to whoever calls it.
/// </para>
/// <para>
/// <strong>There are two ways to obtain a token here, and choosing between them matters.</strong>
/// </para>
/// <para>
/// The first is <see cref="CreateAuthenticatedClientAsync(ApiTestFixture, string, string, CancellationToken)"/>
/// and the persona helpers beside it. They perform a real <c>POST /api/v1/auth/login</c> against the hosted
/// pipeline and attach the token the API itself issued. Nothing is faked: the request passes through the
/// authentication scheme registration, the tenant-resolution middleware, the credential comparison against the
/// seeded one-way hash, the token service that mints the pair, and - on every subsequent call the returned
/// client makes - the authorisation handlers and the projection of the presented principal into the caller
/// abstraction. <b>No fake authentication handler is installed, no test authentication scheme is registered,
/// and the caller abstraction is never re-registered or substituted</b>; doing any of those would make every
/// authorisation assertion in the suite vacuous, because the thing being asserted would be the substitute.
/// This is the path a suite should reach for by default.
/// </para>
/// <para>
/// The second is <see cref="CreateToken"/>, which signs a token directly with the host's own key. It exists
/// for two reasons that the sign-in path cannot serve. Firstly, it is the only way to construct material the
/// sign-in endpoint would never issue - a token that has already expired, one signed with the wrong key, one
/// whose tenant claim disagrees with the route, one carrying a permission the seeded accounts do not hold -
/// and refusing such material is behaviour that has to be asserted. Secondly, the fixture's synchronous client
/// factories are built on it, and they are called several hundred times across this project from synchronous
/// expression-bodied members; converting that path to sign in would either force those members to block on an
/// HTTP round trip - which the analyser bans outright and which is a build error here - or force every one of
/// those call sites to change. Both members are therefore kept, source-compatible, and are documented as what
/// they are: a deliberate shortcut for suites whose subject is not authentication, alongside a genuine
/// sign-in path for suites whose subject is.
/// </para>
/// <para>
/// <strong>Tokens are cached per persona, and that is a correctness measure rather than an optimisation.</strong>
/// The authentication endpoints are rate limited - a window sized from <c>RateLimiting:Authentication</c> plus a
/// process-wide concurrency bound on credential-bearing requests - so a helper that signed in on every call
/// would spend the shared budget and turn unrelated tests into spurious 429 refusals whose cause depends on
/// execution order. The cache is keyed on the fixture instance, so it lives and dies with the host rather than
/// leaking a token from one host into another, and
/// <see cref="LoginWithoutCacheAsync"/> exists precisely so that a suite whose subject IS the throttle can
/// spend the budget on purpose. A second benefit follows for free: the seeded credentials are hashed at work
/// factor twelve, so each avoided sign-in is real time saved.
/// </para>
/// <para>
/// MIGRATION: the legacy screen decided authentication with
/// <c>authenticated = (loginStatus &lt;&gt; UserLoginStatus.LOGIN_FAILURE)</c> at
/// <c>Website/DesktopModules/AuthenticationServices/DNN/Login.ascx.vb:L187</c>, reached only in the
/// <c>Else</c> of a test for <c>LOGIN_USERNOTAPPROVED</c>. Read against the seven explicitly-valued members of
/// <c>Library/Components/Users/Membership/UserLoginStatus.vb:L23-L31</c>, that expression admits FIVE of them:
/// success and superuser as intended, the two insecure-credential advisories as success-with-warning, and -
/// because three is not zero and is not intercepted - <c>LOGIN_USERLOCKEDOUT</c>. A locked account
/// authenticated exactly as a valid one did. That is a measured legacy defect, recorded here rather than
/// reproduced: the target refuses a locked account, which is a deliberate behavioural change written up in
/// <c>MIGRATION_NOTES.md</c>. <b>No helper in this file decides that question.</b> Each helper reports what the
/// endpoint answered, so a suite asserts on the API's outcome instead of on a shortcut taken in a fixture.
/// </para>
/// <para>
/// MIGRATION: two arguments of the legacy call disappear. The literal authentication-type argument - always
/// the same provider name at the only call site that mattered - goes with the single bearer path, because
/// there is no provider to select between. And the CAPTCHA gate that gated the whole click handler goes with
/// the excluded control, which is a real reduction in defence against automated guessing; the compensating
/// control is the rate-limit window that is also the reason this file caches. The verification-code ladder
/// that produced three distinct messages survives as three distinct failure codes on the endpoint, so a suite
/// that wants them reads them from the response rather than from here.
/// </para>
/// <para>
/// MIGRATION: the legacy handler seeded its outcome from the sentinel module - false for the authenticated
/// flag and the EMPTY STRING, not null, for the message. Nothing here manufactures an empty string or a
/// negative integer to mean "absent", and nothing here configures serialisation to drop one: the shared
/// serialiser options are reused exactly as the fixture publishes them, with the ignore condition left at
/// never, because both the empty string and minus one are real values in this schema.
/// </para>
/// <para>
/// MIGRATION: the legacy installation stored credentials reversibly - <c>Website/release.config:L245</c>
/// declares an encrypted password format, <c>:L239</c> enables their retrieval, and <c>:L89-L93</c> commits
/// the deciphering key to source control in plain sight. The target stores a one-way hash, so retrieval is not
/// merely disallowed but uncomputable, and a credential stored at a superseded cost is replaced on the first
/// successful sign-in with administrative reset as the fallback. <b>No member of this file targets a
/// retrieval endpoint, and none may be added</b> - there is no such endpoint to target.
/// </para>
/// <para>
/// MIGRATION: the credential POLICY, unlike the credential store, is carried forward VERBATIM from the
/// provider registration at <c>Website/release.config:L237-L247</c> - a minimum length of SEVEN, ZERO required
/// non-alphanumeric characters, NO question-and-answer requirement, and email addresses NOT required to be
/// unique. The persona helpers below present the credential the fixture seeded, which satisfies that policy
/// with room to spare, and <b>nothing here hardens it</b>: tightening a policy during a migration would lock
/// existing accounts out of an installation that had accepted their credentials for years, so hardening is an
/// operator's separate decision rather than a side effect of a test helper.
/// </para>
/// <para>
/// One consequence of that policy is easy to mistake for a defect. Because email uniqueness is NOT enforced,
/// two accounts may legitimately hold the SAME address, so a suite must assert that a duplicate address is
/// ACCEPTED rather than refused. Establishing such a pair is the seed's job and not this factory's - this file
/// signs accounts in and creates none, which is why it names no table and reaches for no credential store.
/// </para>
/// <para>
/// MIGRATION: the lockout policy was never an explicit contract. <c>Website/release.config</c> documents
/// <c>passwordAttemptThreshold</c> and <c>passwordAttemptWindow</c> in the comment above the provider
/// registration but sets neither on the element, so the legacy installation ran on whatever the framework
/// defaulted to. Nothing here asserts a threshold, and a suite should not either.
/// </para>
/// <para>
/// MIGRATION: the legacy status enumerations use three contradictory numbering conventions - the account
/// creation statuses are explicitly valued with the first member zero and success thirteen, so the default
/// value of that type is NOT success; the credential-update statuses are unvalued, so their success IS zero;
/// and the registration statuses number their failures negatively. No numeric convention may be assumed when
/// a legacy status is mapped, and an assertion must name the member rather than its integer. Correspondingly,
/// the legacy by-reference status argument becomes the failure reason carried by the outcome envelope, and
/// <b>no member of this file takes an <c>out</c> or a <c>ref</c> parameter.</b>
/// </para>
/// </remarks>
public static class AuthenticatedClientFactory
{
    /// <summary>The sign-in address, relative to the client's base address.</summary>
    private const string LoginPath = "/api/v1/auth/login";

    /// <summary>The token-exchange address, relative to the client's base address.</summary>
    private const string RefreshPath = "/api/v1/auth/refresh";

    /// <summary>The revocation address, relative to the client's base address.</summary>
    private const string LogoutPath = "/api/v1/auth/logout";

    /// <summary>The caller-description address, relative to the client's base address.</summary>
    private const string CurrentUserPath = "/api/v1/auth/me";

    /// <summary>The scheme every bearer token is presented under.</summary>
    private const string BearerScheme = "Bearer";

    /// <summary>Default lifetime of a minted token: long enough for a suite, short enough to be a token.</summary>
    private static readonly TimeSpan DefaultLifetime = TimeSpan.FromMinutes(30);

    /// <summary>
    /// How much of a cached token's remaining life is treated as already spent.
    /// </summary>
    /// <remarks>
    /// A token that expires while a request is in flight fails that request for a reason unrelated to what it
    /// was testing, so a cached session is re-established once it comes within this margin of expiry rather
    /// than at expiry. The margin is generous relative to the work a single test does and small relative to
    /// the configured lifetime, so it costs at most one extra sign-in per persona per run.
    /// </remarks>
    private static readonly TimeSpan ExpiryMargin = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Cached sign-in results, keyed on the host they were issued by.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Keyed on the fixture INSTANCE rather than held in a plain static field, so the cache is scoped to one
    /// host: a token minted by one host's signing key is meaningless to another, and a token issued against
    /// one provisioned database names accounts that another database does not have. The entry becomes
    /// unreachable when its fixture does, which is the whole reason this collection type is used - a
    /// dictionary keyed the same way would keep every host alive for the life of the process.
    /// </para>
    /// <para>
    /// Nothing here is disposed on collection, and nothing needs to be: the only disposable a cache entry owns
    /// is a semaphore that was never given a wait handle, so it holds no unmanaged resource.
    /// </para>
    /// </remarks>
    private static readonly ConditionalWeakTable<ApiTestFixture, SessionCache> Sessions = new();

    /// <summary>
    /// Mints a signed bearer token carrying only the stable identity vocabulary the API reads.
    /// </summary>
    /// <param name="secret">The signing secret; must match the host's <c>Jwt:Secret</c>.</param>
    /// <param name="issuer">The issuer; must match the host's <c>Jwt:Issuer</c>.</param>
    /// <param name="audience">The audience; must match the host's <c>Jwt:Audience</c>.</param>
    /// <param name="userId">The account identifier written to the subject claim.</param>
    /// <param name="userName">Compatibility input validated but deliberately not written as a claim.</param>
    /// <param name="portalId">The tenant written to the portal claim.</param>
    /// <param name="isSuperUser">Compatibility input deliberately not written as a claim.</param>
    /// <param name="roles">Compatibility input deliberately not written as claims.</param>
    /// <param name="permissions">Compatibility input deliberately not written as claims.</param>
    /// <param name="lifetime">How long the token stays valid; defaults to thirty minutes.</param>
    /// <param name="notBefore">
    /// When the token becomes valid; defaults to one minute ago so that a token cannot be rejected by the
    /// clock skew of a host that started a moment later.
    /// </param>
    /// <returns>The compact serialised token.</returns>
    /// <remarks>
    /// <para>
    /// <strong>This does not exercise the sign-in path and is not a substitute for it.</strong> Prefer
    /// <see cref="CreateAuthenticatedClientAsync(ApiTestFixture, string, string, CancellationToken)"/> unless
    /// the test needs material the endpoint would never issue - an expired token, a token signed with a
    /// foreign key, or a tenant claim that disagrees with the route.
    /// Those cases are why this member exists, and refusing such material is behaviour worth asserting.
    /// </para>
    /// <para>
    /// <strong>Why it constructs the token the same way production does.</strong> This uses the
    /// <see cref="JwtSecurityToken"/> constructor followed by
    /// <see cref="JwtSecurityTokenHandler.WriteToken(SecurityToken)"/>, which is precisely what
    /// <c>Infrastructure/Security/JwtTokenService</c> does. Mirroring that construction keeps the
    /// subject, tenant and token-identifier claim names representative of a real token while ensuring
    /// mutable authority cannot be injected through this test helper.
    /// </para>
    /// </remarks>
    public static string CreateToken(
        string secret,
        string issuer,
        string audience,
        int userId,
        string userName,
        int portalId,
        bool isSuperUser = false,
        IEnumerable<string>? roles = null,
        IEnumerable<string>? permissions = null,
        TimeSpan? lifetime = null,
        DateTime? notBefore = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(secret);
        ArgumentException.ThrowIfNullOrWhiteSpace(issuer);
        ArgumentException.ThrowIfNullOrWhiteSpace(audience);
        ArgumentNullException.ThrowIfNull(userName);

        DateTime issuedAt = notBefore ?? DateTime.UtcNow.AddMinutes(-1);
        DateTime expires = issuedAt.Add(lifetime ?? DefaultLifetime);

        var claims = new List<Claim>
        {
            new(DnnClaimTypes.Subject, userId.ToString(CultureInfo.InvariantCulture)),
            new(DnnClaimTypes.JwtId, Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture)),
            new(DnnClaimTypes.PortalId, portalId.ToString(CultureInfo.InvariantCulture)),
        };

        var credentials = new SigningCredentials(
            new SymmetricSecurityKey(Encoding.UTF8.GetBytes(secret)),
            SecurityAlgorithms.HmacSha256);

        var token = new JwtSecurityToken(
            issuer: issuer,
            audience: audience,
            claims: claims,
            notBefore: issuedAt,
            expires: expires,
            signingCredentials: credentials);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    /// <summary>Attaches a bearer token to a client and returns the same client.</summary>
    /// <param name="client">The client to authenticate.</param>
    /// <param name="token">The compact serialised token.</param>
    /// <returns>The client, so calls can be chained.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="client"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="token"/> is blank.</exception>
    public static HttpClient Authenticate(HttpClient client, string token)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentException.ThrowIfNullOrWhiteSpace(token);

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(BearerScheme, token);
        return client;
    }

    /// <summary>
    /// Signs in with the supplied credential and returns a client presenting the token the API issued.
    /// </summary>
    /// <param name="fixture">The hosted API, its provisioned database and its seed.</param>
    /// <param name="userName">The account name to present.</param>
    /// <param name="password">The credential to present.</param>
    /// <param name="cancellationToken">Abandons the sign-in when the test is cancelled.</param>
    /// <returns>
    /// A client carrying <c>Authorization: Bearer</c>. The caller owns it and must dispose it.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="fixture"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="userName"/> or <paramref name="password"/> is blank.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// The endpoint refused the credential. The message carries the refusal's problem document - its type,
    /// title, status, detail and per-field errors - because a bare status code does not tell the test author
    /// which of the account-state gates applied.
    /// </exception>
    /// <remarks>
    /// The tenant is taken from the request host, which the fixture binds to the seeded alias, so the seeded
    /// accounts sign in without naming a portal. Use
    /// <see cref="CreateAuthenticatedClientAsync(ApiTestFixture, string, string, int?, string?, CancellationToken)"/>
    /// when the tenant must be named explicitly or a different alias must be addressed.
    /// </remarks>
    public static Task<HttpClient> CreateAuthenticatedClientAsync(
        ApiTestFixture fixture,
        string userName,
        string password,
        CancellationToken cancellationToken = default) =>
        CreateAuthenticatedClientAsync(fixture, userName, password, portalId: null, host: null, cancellationToken);

    /// <summary>
    /// Signs in with the supplied credential against a named tenant or host, and returns a client presenting
    /// the token the API issued.
    /// </summary>
    /// <param name="fixture">The hosted API, its provisioned database and its seed.</param>
    /// <param name="userName">The account name to present.</param>
    /// <param name="password">The credential to present.</param>
    /// <param name="portalId">
    /// The tenant to sign in to, stated in the query string. Read by the endpoint ONLY when the request host
    /// matches no configured alias: a resolved tenant always wins, so this cannot be used to sign in to a
    /// tenant the request is not addressing.
    /// </param>
    /// <param name="host">
    /// The host name to address, or <see langword="null"/> to address the seeded alias. Supplying one that is
    /// deliberately unconfigured reproduces the state an operator provisioning the first portal is in, where
    /// no tenant resolves and <paramref name="portalId"/> is the only thing naming one.
    /// </param>
    /// <param name="cancellationToken">Abandons the sign-in when the test is cancelled.</param>
    /// <returns>
    /// A client carrying <c>Authorization: Bearer</c> and addressed at <paramref name="host"/>. The caller
    /// owns it and must dispose it.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="fixture"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="userName"/> or <paramref name="password"/> is blank, or <paramref name="host"/> is
    /// supplied and blank.
    /// </exception>
    /// <exception cref="InvalidOperationException">The endpoint refused the credential.</exception>
    public static async Task<HttpClient> CreateAuthenticatedClientAsync(
        ApiTestFixture fixture,
        string userName,
        string password,
        int? portalId,
        string? host,
        CancellationToken cancellationToken = default)
    {
        LoginResponse issued = await LoginAsync(fixture, userName, password, portalId, host, cancellationToken)
            .ConfigureAwait(false);

        return Authenticate(CreateClient(fixture, host), issued.AccessToken);
    }

    /// <summary>
    /// Signs in as the seeded host account and returns a client presenting the token the API issued.
    /// </summary>
    /// <param name="fixture">The hosted API, its provisioned database and its seed.</param>
    /// <param name="cancellationToken">Abandons the sign-in when the test is cancelled.</param>
    /// <returns>An authenticated client. The caller owns it and must dispose it.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="fixture"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">The endpoint refused the seeded credential.</exception>
    /// <remarks>
    /// The host account is an installation-wide superuser, which the permission evaluator short-circuits to
    /// "holds everything". It is therefore the wrong persona for asserting that a permission is enforced and
    /// the right one for asserting that an operation works at all.
    /// </remarks>
    public static Task<HttpClient> CreateHostClientAsync(
        ApiTestFixture fixture,
        CancellationToken cancellationToken = default) =>
        CreateAuthenticatedClientAsync(
            fixture,
            IntegrationSeed.HostUserName,
            ApiTestFixture.KnownPassword,
            cancellationToken);

    /// <summary>
    /// Signs in as the seeded portal administrator and returns a client presenting the token the API issued.
    /// </summary>
    /// <param name="fixture">The hosted API, its provisioned database and its seed.</param>
    /// <param name="cancellationToken">Abandons the sign-in when the test is cancelled.</param>
    /// <returns>An authenticated client. The caller owns it and must dispose it.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="fixture"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">The endpoint refused the seeded credential.</exception>
    /// <remarks>
    /// Not a superuser, and a member of the seeded administrators role - which the seed also records as the
    /// tenant's designated administrator, so this is the persona the portal-administrator policy admits. The
    /// policy resolves role membership from the database on every request rather than from the token, so the
    /// distinction from the host account is real rather than a property of the claims presented.
    /// </remarks>
    public static Task<HttpClient> CreateAdministratorClientAsync(
        ApiTestFixture fixture,
        CancellationToken cancellationToken = default) =>
        CreateAuthenticatedClientAsync(
            fixture,
            IntegrationSeed.AdminUserName,
            ApiTestFixture.KnownPassword,
            cancellationToken);

    /// <summary>
    /// Signs in as the seeded ordinary member and returns a client presenting the token the API issued.
    /// </summary>
    /// <param name="fixture">The hosted API, its provisioned database and its seed.</param>
    /// <param name="cancellationToken">Abandons the sign-in when the test is cancelled.</param>
    /// <returns>An authenticated client. The caller owns it and must dispose it.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="fixture"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">The endpoint refused the seeded credential.</exception>
    /// <remarks>
    /// The account holds the auto-assigned registered-users role and nothing else, which is what an ordinary
    /// signed-in visitor holds.
    /// </remarks>
    public static Task<HttpClient> CreateRegisteredUserClientAsync(
        ApiTestFixture fixture,
        CancellationToken cancellationToken = default) =>
        CreateAuthenticatedClientAsync(
            fixture,
            IntegrationSeed.MemberUserName,
            ApiTestFixture.KnownPassword,
            cancellationToken);

    /// <summary>
    /// Signs in as a caller that is authenticated and holds no administrative entitlement, for asserting that
    /// an operation refuses rather than that it works.
    /// </summary>
    /// <param name="fixture">The hosted API, its provisioned database and its seed.</param>
    /// <param name="cancellationToken">Abandons the sign-in when the test is cancelled.</param>
    /// <returns>An authenticated but unprivileged client. The caller owns it and must dispose it.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="fixture"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">The endpoint refused the seeded credential.</exception>
    /// <remarks>
    /// <para>
    /// This is the seeded member account, and the duplication with
    /// <see cref="CreateRegisteredUserClientAsync"/> is deliberate rather than accidental: the member is the
    /// only seeded account outside the administrators role, so it is simultaneously the ordinary registered
    /// visitor and the unprivileged caller. Naming both intentions separately means a permission-denied test
    /// states WHY it chose this persona, and it means that if the seed ever gains a truly role-less account
    /// this member can be repointed at it without touching the tests that assert refusal.
    /// </para>
    /// <para>
    /// The refusal such a caller receives is 403 and not 401. It is authenticated, so a 401 here would report
    /// a broken token rather than an enforced permission, and a test that accepted either would pass for the
    /// wrong reason.
    /// </para>
    /// </remarks>
    public static Task<HttpClient> CreateUnprivilegedClientAsync(
        ApiTestFixture fixture,
        CancellationToken cancellationToken = default) =>
        CreateRegisteredUserClientAsync(fixture, cancellationToken);

    /// <summary>
    /// Signs in and returns the raw access token, without a client.
    /// </summary>
    /// <param name="fixture">The hosted API, its provisioned database and its seed.</param>
    /// <param name="userName">The account name to present.</param>
    /// <param name="password">The credential to present.</param>
    /// <param name="cancellationToken">Abandons the sign-in when the test is cancelled.</param>
    /// <returns>The compact serialised access token the API issued.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="fixture"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="userName"/> or <paramref name="password"/> is blank.
    /// </exception>
    /// <exception cref="InvalidOperationException">The endpoint refused the credential.</exception>
    /// <remarks>
    /// For a test that reads the token rather than spends it: asserting on the claims a real token carries, or
    /// deliberately damaging one - truncating it, re-signing it, presenting it under the wrong scheme - to
    /// assert that the pipeline refuses it. Spending it needs no raw token, because
    /// <see cref="CreateAuthenticatedClientAsync(ApiTestFixture, string, string, CancellationToken)"/>
    /// attaches it already.
    /// </remarks>
    public static async Task<string> GetAccessTokenAsync(
        ApiTestFixture fixture,
        string userName,
        string password,
        CancellationToken cancellationToken = default)
    {
        LoginResponse issued = await LoginAsync(
                fixture,
                userName,
                password,
                portalId: null,
                host: null,
                cancellationToken)
            .ConfigureAwait(false);

        return issued.AccessToken;
    }

    /// <summary>
    /// Signs in and returns the whole issued pair, reusing a cached sign-in for the same persona.
    /// </summary>
    /// <param name="fixture">The hosted API, its provisioned database and its seed.</param>
    /// <param name="userName">The account name to present.</param>
    /// <param name="password">The credential to present.</param>
    /// <param name="portalId">
    /// The tenant to name in the query string, or <see langword="null"/> to rely on the host.
    /// </param>
    /// <param name="host">The host name to address, or <see langword="null"/> to address the seeded alias.</param>
    /// <param name="cancellationToken">Abandons the sign-in when the test is cancelled.</param>
    /// <returns>
    /// A freshly deserialised representation of the issued pair. Every call returns its own instance, so a
    /// test may read or even mutate it without affecting another test that asked for the same persona.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="fixture"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="userName"/> or <paramref name="password"/> is blank, or <paramref name="host"/> is
    /// supplied and blank.
    /// </exception>
    /// <exception cref="InvalidOperationException">The endpoint refused the credential.</exception>
    /// <remarks>
    /// <para>
    /// The cached entry keeps the response BODY rather than the deserialised object, which is what makes the
    /// per-call instance possible: two tests handed the same mutable object would be coupled through it, and
    /// the coupling would present as one test failing because of what another did to a shared field.
    /// </para>
    /// <para>
    /// <strong>The refresh token in a cached result is shared, and rotating it has consequences.</strong> The
    /// store treats a refresh token as single use and answers a replay by revoking every refresh token the
    /// account holds - correctly, because a replay is evidence of theft. A test that rotates a cached
    /// persona's refresh token would therefore leave the next test's copy of that same value already spent.
    /// <see cref="RefreshAsync"/> accordingly takes the token to exchange as an argument and never reads this
    /// cache, and a rotation test should obtain its pair from <see cref="LoginWithoutCacheAsync"/> so the pair
    /// it rotates is its own. Access tokens are unaffected either way: they are not revocable, which is the
    /// documented reduction that makes their lifetime short.
    /// </para>
    /// </remarks>
    public static async Task<LoginResponse> LoginAsync(
        ApiTestFixture fixture,
        string userName,
        string password,
        int? portalId = null,
        string? host = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(fixture);
        ArgumentException.ThrowIfNullOrWhiteSpace(userName);
        ArgumentException.ThrowIfNullOrWhiteSpace(password);

        SessionCache cache = Sessions.GetValue(fixture, static _ => new SessionCache());
        var key = new PersonaKey(userName, password, portalId, host);
        string? cached = cache.Read(key);

        if (cached is not null)
        {
            return ParseIssuedPair(cached);
        }

        await cache.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            // Re-read inside the gate: a persona requested concurrently would otherwise sign in twice, which
            // is exactly the spend on the shared credential budget the cache exists to avoid.
            cached = cache.Read(key);

            if (cached is not null)
            {
                return ParseIssuedPair(cached);
            }

            string body = await PostSignInAsync(fixture, userName, password, portalId, host, cancellationToken)
                .ConfigureAwait(false);

            LoginResponse issued = ParseIssuedPair(body);

            // Written only after a successful parse, so a refusal or an unreadable body is never cached and
            // the next attempt reaches the endpoint rather than replaying a failure.
            cache.Write(key, body, issued.ExpiresAtUtc);

            return issued;
        }
        finally
        {
            cache.Gate.Release();
        }
    }

    /// <summary>
    /// Signs in without consulting or populating the cache, so the request genuinely reaches the endpoint.
    /// </summary>
    /// <param name="fixture">The hosted API, its provisioned database and its seed.</param>
    /// <param name="userName">The account name to present.</param>
    /// <param name="password">The credential to present.</param>
    /// <param name="portalId">
    /// The tenant to name in the query string, or <see langword="null"/> to rely on the host.
    /// </param>
    /// <param name="host">The host name to address, or <see langword="null"/> to address the seeded alias.</param>
    /// <param name="cancellationToken">Abandons the sign-in when the test is cancelled.</param>
    /// <returns>The issued pair.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="fixture"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="userName"/> or <paramref name="password"/> is blank, or <paramref name="host"/> is
    /// supplied and blank.
    /// </exception>
    /// <exception cref="InvalidOperationException">The endpoint refused the credential.</exception>
    /// <remarks>
    /// <para>
    /// Deliberately uncached, for the two cases where a cache hit would defeat the test. The first is the
    /// rate-limit boundary: a suite proving that the window refuses with 429 has to spend the window, and it
    /// must do so against a host configured with a small limit rather than the shared one, whose limit is
    /// deliberately far above anything a suite needs. The second is rotation: the pair being rotated must
    /// belong to the test rotating it, for the reason set out on <see cref="LoginAsync"/>.
    /// </para>
    /// <para>
    /// Every call pays a credential comparison at the production work factor, so a loop over this member is
    /// slow by design. That is the correct trade for a test whose subject is the sign-in path, and the wrong
    /// one for a test that merely needs a caller.
    /// </para>
    /// </remarks>
    public static async Task<LoginResponse> LoginWithoutCacheAsync(
        ApiTestFixture fixture,
        string userName,
        string password,
        int? portalId = null,
        string? host = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(fixture);
        ArgumentException.ThrowIfNullOrWhiteSpace(userName);
        ArgumentException.ThrowIfNullOrWhiteSpace(password);

        string body = await PostSignInAsync(fixture, userName, password, portalId, host, cancellationToken)
            .ConfigureAwait(false);

        return ParseIssuedPair(body);
    }

    /// <summary>
    /// Exchanges a refresh token for a replacement pair.
    /// </summary>
    /// <param name="fixture">The hosted API.</param>
    /// <param name="refreshToken">The refresh token to present. Single use.</param>
    /// <param name="cancellationToken">Abandons the exchange when the test is cancelled.</param>
    /// <returns>The replacement pair.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="fixture"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="refreshToken"/> is blank.</exception>
    /// <exception cref="InvalidOperationException">
    /// The exchange was refused - the token is unknown, lapsed, already spent, or belongs to an account that
    /// may no longer sign in. Every cause answers alike, so the problem document is surfaced in the message
    /// rather than inferred from the status.
    /// </exception>
    /// <remarks>
    /// <para>
    /// The token is an argument rather than something read from the cache, deliberately: the exchange consumes
    /// it, and presenting a consumed value again is a replay that revokes every refresh token the account
    /// holds. Obtain the pair from <see cref="LoginWithoutCacheAsync"/> so the value being spent belongs to
    /// the test spending it.
    /// </para>
    /// <para>
    /// This throws on a refusal, so it suits a test that needs a rotated pair in order to get on with
    /// something else. A test whose subject IS the refusal should post to the endpoint directly and assert on
    /// the status and the problem document.
    /// </para>
    /// </remarks>
    public static async Task<LoginResponse> RefreshAsync(
        ApiTestFixture fixture,
        string refreshToken,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(fixture);
        ArgumentException.ThrowIfNullOrWhiteSpace(refreshToken);

        using HttpClient client = CreateClient(fixture, host: null);

        using HttpResponseMessage response = await client
            .PostAsJsonAsync(
                new Uri(RefreshPath, UriKind.Relative),
                new RefreshTokenRequest { RefreshToken = refreshToken },
                ApiTestFixture.Json,
                cancellationToken)
            .ConfigureAwait(false);

        string body = await ReadSuccessBodyAsync(response, "exchange a refresh token", cancellationToken)
            .ConfigureAwait(false);

        return ParseIssuedPair(body);
    }

    /// <summary>
    /// Withdraws a refresh token, ending the session it belongs to.
    /// </summary>
    /// <param name="fixture">The hosted API.</param>
    /// <param name="refreshToken">The refresh token to withdraw.</param>
    /// <param name="cancellationToken">Abandons the request when the test is cancelled.</param>
    /// <returns>A task that completes when the endpoint has answered.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="fixture"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="refreshToken"/> is blank.</exception>
    /// <exception cref="InvalidOperationException">
    /// The endpoint answered anything other than an empty success.
    /// </exception>
    /// <remarks>
    /// <para>
    /// The endpoint answers an empty success unconditionally, whether or not a matching token was found, so
    /// that an anonymous caller cannot use it to discover which sessions are live. A test therefore learns
    /// nothing from this call about whether the token existed, and must assert the effect - that the
    /// withdrawn token can no longer be exchanged - rather than the answer.
    /// </para>
    /// <para>
    /// MIGRATION: the legacy sign-out cleared five cookies and took effect at once. A bearer token cannot be
    /// recalled, so this withdraws the refresh token and an ALREADY-ISSUED ACCESS TOKEN STAYS VALID UNTIL IT
    /// EXPIRES. <b>Do not assert that a client authenticated before this call begins to be refused after
    /// it</b> - it will not be, there is no registry of withdrawn access tokens, and none may be introduced.
    /// That window is a documented reduction and the reason the access-token lifetime is short.
    /// </para>
    /// </remarks>
    public static async Task LogoutAsync(
        ApiTestFixture fixture,
        string refreshToken,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(fixture);
        ArgumentException.ThrowIfNullOrWhiteSpace(refreshToken);

        using HttpClient client = CreateClient(fixture, host: null);

        using HttpResponseMessage response = await client
            .PostAsJsonAsync(
                new Uri(LogoutPath, UriKind.Relative),
                new RefreshTokenRequest { RefreshToken = refreshToken },
                ApiTestFixture.Json,
                cancellationToken)
            .ConfigureAwait(false);

        if (response.StatusCode != HttpStatusCode.NoContent)
        {
            throw await DescribeFailureAsync(response, "withdraw a refresh token", cancellationToken)
                .ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Reads the caller's own description back from the API, as the presented token resolves it.
    /// </summary>
    /// <param name="client">A client already carrying a bearer token.</param>
    /// <param name="cancellationToken">Abandons the request when the test is cancelled.</param>
    /// <returns>The caller's identity, roles and permission codes.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="client"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">
    /// The endpoint refused the token or answered without a description.
    /// </exception>
    /// <remarks>
    /// This is the end-to-end proof that a client from this factory works: the answer is composed from the
    /// authenticated principal the pipeline built out of the presented token, so a matching account name and
    /// role set demonstrates that signing, validation, claim mapping and the caller projection all agree.
    /// A client whose token was minted rather than issued is answered identically, which is what makes the
    /// minted path a legitimate shortcut rather than a fiction.
    /// </remarks>
    public static async Task<CurrentUserDto> GetCurrentUserAsync(
        HttpClient client,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);

        using HttpResponseMessage response = await client
            .GetAsync(new Uri(CurrentUserPath, UriKind.Relative), cancellationToken)
            .ConfigureAwait(false);

        string body = await ReadSuccessBodyAsync(response, "read the caller's description", cancellationToken)
            .ConfigureAwait(false);

        return ReadPayload<CurrentUserDto>(body, CurrentUserPath);
    }

    /// <summary>
    /// Sends a request carrying a correlation identifier and reports the identifier that came back.
    /// </summary>
    /// <param name="client">The client to send with.</param>
    /// <param name="request">
    /// The request to stamp and send. It is consumed by the send, so it cannot be sent twice.
    /// </param>
    /// <param name="correlationId">
    /// The identifier to send, or <see langword="null"/> to generate one. Generating one is the common case:
    /// the assertion is that the value round-trips, and a value the test invented is the only value that can
    /// prove the response echoed rather than originated it.
    /// </param>
    /// <param name="cancellationToken">Abandons the request when the test is cancelled.</param>
    /// <returns>
    /// The response together with the identifier sent and the identifier received. The caller owns the result
    /// and must dispose it, which disposes the response.
    /// </returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="client"/> or <paramref name="request"/> is <see langword="null"/>.
    /// </exception>
    /// <remarks>
    /// The header is stamped on the REQUEST rather than on the client's default headers, because the round
    /// trip is a property of one request: a client-wide default would send the same identifier on every
    /// request a suite made, and an assertion that the response echoed it could then be satisfied by a value
    /// the test under examination never sent.
    /// </remarks>
    public static async Task<CorrelatedResponse> SendWithCorrelationIdAsync(
        HttpClient client,
        HttpRequestMessage request,
        string? correlationId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(request);

        string sent = string.IsNullOrWhiteSpace(correlationId)
            ? Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture)
            : correlationId;

        ApiTestFixture.WithCorrelationId(request, sent);

        HttpResponseMessage response = await client
            .SendAsync(request, cancellationToken)
            .ConfigureAwait(false);

        return new CorrelatedResponse(response, sent, ApiTestFixture.ReadCorrelationId(response));
    }

    /// <summary>
    /// Discards every cached sign-in belonging to one host, so the next request signs in again.
    /// </summary>
    /// <param name="fixture">The host whose cached sign-ins are to be discarded.</param>
    /// <exception cref="ArgumentNullException"><paramref name="fixture"/> is <see langword="null"/>.</exception>
    /// <remarks>
    /// Needed rarely and for one reason: a test that changes what an account IS - disabling it, deleting it,
    /// forcing a credential change - and then wants a token minted after that change. Most permission changes
    /// need no eviction, because entitlement is resolved from the database on every request rather than read
    /// out of the token, so a stale token still yields current authorisation. Calling this unnecessarily only
    /// costs sign-ins.
    /// </remarks>
    public static void ForgetCachedSessions(ApiTestFixture fixture)
    {
        ArgumentNullException.ThrowIfNull(fixture);

        // An entry is created when the host had none, which is deliberate: an empty cache and no cache are the
        // same state to every reader, and creating one keeps this a single expression with no lookup pattern
        // that reports absence through an argument.
        Sessions.GetValue(fixture, static _ => new SessionCache()).Clear();
    }

    /// <summary>
    /// Creates an unauthenticated client for one exchange, with the redirect policy stated rather than
    /// inherited.
    /// </summary>
    /// <param name="fixture">The hosted API.</param>
    /// <param name="host">The host name to address, or <see langword="null"/> for the fixture's own.</param>
    /// <returns>A client the caller owns.</returns>
    /// <remarks>
    /// <para>
    /// Redirects are NOT followed. A test asserts the status the API returned, and a followed redirect would
    /// replace a 307 with whatever answered at the target - so an unguarded redirection would present as the
    /// wrong status code on an unrelated assertion. The fixture already configures this on its own client
    /// options; it is restated here because a silent default is not a guarantee.
    /// </para>
    /// <para>
    /// The base address is derived from the fixture's own so that the tenant a request resolves to cannot
    /// drift from the alias the seed registered. The test server binds no socket, so the host name in the
    /// request line is whatever this address says, which is what makes addressing a deliberately
    /// unconfigured host possible.
    /// </para>
    /// </remarks>
    private static HttpClient CreateClient(ApiTestFixture fixture, string? host)
    {
        ArgumentNullException.ThrowIfNull(fixture);

        WebApplicationFactoryClientOptions options = new()
        {
            AllowAutoRedirect = false,
            HandleCookies = fixture.ClientOptions.HandleCookies,
            BaseAddress = fixture.ClientOptions.BaseAddress,
        };

        if (host is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(host);
            options.BaseAddress = new Uri($"http://{host}/", UriKind.Absolute);
        }

        return fixture.CreateClient(options);
    }

    /// <summary>Posts a credential to the sign-in endpoint and returns the successful response body.</summary>
    /// <param name="fixture">The hosted API.</param>
    /// <param name="userName">The account name to present.</param>
    /// <param name="password">The credential to present.</param>
    /// <param name="portalId">The tenant to name in the query string, or <see langword="null"/>.</param>
    /// <param name="host">The host name to address, or <see langword="null"/>.</param>
    /// <param name="cancellationToken">Abandons the request when the test is cancelled.</param>
    /// <returns>The response body, unparsed.</returns>
    /// <remarks>
    /// The tenant travels in the QUERY STRING and not in the body. The request contract excludes its tenant
    /// member from serialisation precisely so that a caller cannot present a credential to a portal it is not
    /// addressing: the value the service sees is assigned by the endpoint from what the transport resolved.
    /// Setting it on the object here would be silently inert.
    /// </remarks>
    private static async Task<string> PostSignInAsync(
        ApiTestFixture fixture,
        string userName,
        string password,
        int? portalId,
        string? host,
        CancellationToken cancellationToken)
    {
        using HttpClient client = CreateClient(fixture, host);

        using HttpResponseMessage response = await client
            .PostAsJsonAsync(
                SignInRoute(portalId, host),
                new LoginRequest { Username = userName, Password = password },
                ApiTestFixture.Json,
                cancellationToken)
            .ConfigureAwait(false);

        return await ReadSuccessBodyAsync(
                response,
                $"sign in as '{userName}'",
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Builds the sign-in address, naming a tenant when one was supplied.</summary>
    /// <param name="portalId">The tenant identifier, or <see langword="null"/> to omit it.</param>
    /// <param name="host">The host being addressed, or <see langword="null"/> for the seeded alias.</param>
    /// <returns>A relative address.</returns>
    /// <remarks>
    /// The address is composed BENEATH the path segment a child portal's alias carries. A child portal is
    /// addressed as authority plus segment - the resolver stores the composed form and the path-base stage
    /// strips the segment before routing - so a sign-in posted to the bare path would be resolved by the
    /// authority alone and would present the credential to the PARENT tenant, which the child's administrator
    /// is not a member of. Stating the segment is therefore what makes a child tenant signable-in at all, and
    /// it is inert for every alias that carries no segment.
    /// </remarks>
    private static Uri SignInRoute(int? portalId, string? host)
    {
        string path = PathBaseOf(host) + LoginPath;

        return portalId is null
            ? new Uri(path, UriKind.Relative)
            : new Uri(
                $"{path}?{AuthController.PortalQueryParameterName}="
                + portalId.Value.ToString(CultureInfo.InvariantCulture),
                UriKind.Relative);
    }

    /// <summary>Extracts the path segment a composed child-portal alias carries.</summary>
    /// <param name="host">The host being addressed, or <see langword="null"/>.</param>
    /// <returns>A leading-slash segment, or an empty string when the alias names an authority alone.</returns>
    private static string PathBaseOf(string? host)
    {
        if (host is null)
        {
            return string.Empty;
        }

        int separator = host.IndexOf('/', StringComparison.Ordinal);

        return separator < 0 ? string.Empty : host[separator..].TrimEnd('/');
    }

    /// <summary>Returns the body of a successful response, or throws describing the refusal.</summary>
    /// <param name="response">The response to read.</param>
    /// <param name="attempted">What the caller was trying to do, for the failure message.</param>
    /// <param name="cancellationToken">Abandons the read when the test is cancelled.</param>
    /// <returns>The response body.</returns>
    private static async Task<string> ReadSuccessBodyAsync(
        HttpResponseMessage response,
        string attempted,
        CancellationToken cancellationToken)
    {
        if (!response.IsSuccessStatusCode)
        {
            throw await DescribeFailureAsync(response, attempted, cancellationToken).ConfigureAwait(false);
        }

        return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Builds an exception describing a refusal, reading its problem document rather than discarding it.
    /// </summary>
    /// <param name="response">The refused response.</param>
    /// <param name="attempted">What the caller was trying to do.</param>
    /// <param name="cancellationToken">Abandons the read when the test is cancelled.</param>
    /// <returns>The exception to throw.</returns>
    /// <remarks>
    /// <para>
    /// Every refusal from this API is an RFC 7807 problem document, and a bare success assertion would throw
    /// exactly the diagnostic a test author needs away: the account-state gates all answer 401 by design, so
    /// the status alone cannot say whether the account was unknown, the credential wrong, the account locked
    /// or the account awaiting approval. The document's stable failure code can, and so it is surfaced here.
    /// </para>
    /// <para>
    /// <b>Neither the submitted credential nor any response value is included</b> - only the account name,
    /// which the test supplied and already knows, and the problem document's own published members. When the
    /// body does not parse as a problem document it is described STRUCTURALLY by
    /// <see cref="DescribeBody(string)"/> rather than quoted, because a body arriving in an unexpected shape
    /// is exactly the case that could be carrying issued tokens.
    /// </para>
    /// </remarks>
    private static async Task<InvalidOperationException> DescribeFailureAsync(
        HttpResponseMessage response,
        string attempted,
        CancellationToken cancellationToken)
    {
        string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        var message = new StringBuilder()
            .Append("Failed to ")
            .Append(attempted)
            .Append(": the API answered ")
            .Append(((int)response.StatusCode).ToString(CultureInfo.InvariantCulture))
            .Append(' ')
            .Append(response.StatusCode.ToString())
            .Append('.');

        ValidationProblemDetails? problem = TryReadProblem(body);

        if (problem is null)
        {
            message.Append(" The body carried no problem document: ").Append(DescribeBody(body));

            return new InvalidOperationException(message.ToString());
        }

        message.Append(" type='").Append(problem.Type ?? "<none>").Append('\'')
            .Append(" title='").Append(problem.Title ?? "<none>").Append('\'')
            .Append(" status=")
            .Append(problem.Status?.ToString(CultureInfo.InvariantCulture) ?? "<none>")
            .Append(" detail='").Append(problem.Detail ?? "<none>").Append('\'');

        foreach (KeyValuePair<string, string[]> error in problem.Errors)
        {
            message.Append(" errors['").Append(error.Key).Append("']=[")
                .Append(string.Join("; ", error.Value))
                .Append(']');
        }

        return new InvalidOperationException(message.ToString());
    }

    /// <summary>Parses a problem document, tolerating a body that is not one.</summary>
    /// <param name="body">The response body.</param>
    /// <returns>The document, or <see langword="null"/> when the body is not one.</returns>
    /// <remarks>
    /// The validation-flavoured type is read whether or not the refusal was a validation failure: it derives
    /// from the plain document and leaves its per-field collection empty when the payload carries none, so one
    /// read covers both shapes. A body that is not JSON at all yields nothing rather than throwing, because
    /// this runs while building a failure message and an exception here would replace the real diagnostic.
    /// </remarks>
    private static ValidationProblemDetails? TryReadProblem(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<ValidationProblemDetails>(body, ApiTestFixture.Json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Reads an issued token pair out of the shared success envelope.</summary>
    /// <param name="body">The response body.</param>
    /// <returns>The issued pair.</returns>
    /// <remarks>
    /// Deserialising the ENVELOPE rather than the payload is deliberate: it is what proves the wrapping
    /// actually happens. A payload published bare would fail to bind here, whereas binding the payload type
    /// directly would succeed either way and leave the contract unasserted.
    /// </remarks>
    private static LoginResponse ParseIssuedPair(string body)
    {
        LoginResponse issued = ReadPayload<LoginResponse>(body, LoginPath);

        if (string.IsNullOrWhiteSpace(issued.AccessToken))
        {
            throw new InvalidOperationException(
                "The API answered a success carrying no access token, so no client could be authenticated. "
                + "Body: " + DescribeBody(body));
        }

        return issued;
    }

    /// <summary>Reads a payload out of the shared success envelope.</summary>
    /// <typeparam name="T">The payload type.</typeparam>
    /// <param name="body">The response body.</param>
    /// <param name="address">The address that produced the body, for the failure message.</param>
    /// <returns>The payload.</returns>
    /// <remarks>
    /// The body is described rather than quoted. This runs on a SUCCESSFUL response, so a body that fails to
    /// bind here is a successful sign-in whose envelope drifted - the one case where the material being
    /// diagnosed is a live access and refresh token pair. Quoting it would write both to the build log of
    /// every run that hit the drift.
    /// </remarks>
    private static T ReadPayload<T>(string body, string address)
    {
        ApiEnvelope<T>? envelope;

        try
        {
            envelope = JsonSerializer.Deserialize<ApiEnvelope<T>>(body, ApiTestFixture.Json);
        }
        catch (JsonException failure)
        {
            throw new InvalidOperationException(
                $"The body '{address}' answered is not the shared success envelope. "
                + $"Body: {DescribeBody(body)}",
                failure);
        }

        if (envelope is null || envelope.Data is null)
        {
            throw new InvalidOperationException(
                $"The body '{address}' answered carried no payload inside the success envelope. "
                + $"Body: {DescribeBody(body)}");
        }

        return envelope.Data;
    }

    /// <summary>
    /// Describes a response body by its SHAPE - its length, a short digest and the member names it carries -
    /// without reproducing a single value from it.
    /// </summary>
    /// <param name="body">The body to describe.</param>
    /// <returns>A diagnostic string that cannot carry a credential.</returns>
    /// <remarks>
    /// <para>
    /// Every caller of this runs while building a failure message for a body that arrived in an unexpected
    /// shape, and the bodies this factory reads are sign-in, refresh and current-account responses. An
    /// unexpected shape from any of the first two is a live access token and a live refresh token, so quoting
    /// the body verbatim would publish working credentials to a build log - retained, searchable, and valid
    /// for as long as the token's lifetime.
    /// </para>
    /// <para>
    /// Member NAMES are safe and are what a drifted envelope is diagnosed from: they say whether the payload
    /// was published bare rather than wrapped, or whether a member was renamed. VALUES are never emitted, not
    /// even for members whose names look harmless, because the shape being diagnosed is by definition not the
    /// shape whose members are known. The length and digest between them distinguish two different unexpected
    /// bodies from each other and let one run be compared with another, which is all a diagnostic needs.
    /// </para>
    /// <para>
    /// The digest is truncated deliberately: it exists to correlate observations of the same body, not to
    /// withstand an attempt to recover the body from it, and a full digest of a short body would be closer to
    /// an encoding of it than a fingerprint of it.
    /// </para>
    /// </remarks>
    private static string DescribeBody(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return "<empty>";
        }

        var description = new StringBuilder("<")
            .Append("length=")
            .Append(body.Length.ToString(CultureInfo.InvariantCulture))
            .Append(" sha256=")
            .Append(ShortDigest(body));

        try
        {
            using JsonDocument document = JsonDocument.Parse(body);

            description.Append(" json=").Append(document.RootElement.ValueKind.ToString());

            if (document.RootElement.ValueKind == JsonValueKind.Object)
            {
                description.Append(" members=[").Append(string.Join(", ", MemberNames(document.RootElement)))
                    .Append(']');
            }
        }
        catch (JsonException)
        {
            description.Append(" json=none");
        }

        return description.Append('>').ToString();
    }

    /// <summary>Lists the member names an object carries, one level into each nested object.</summary>
    /// <param name="element">The object to read.</param>
    /// <returns>Dotted member paths, in document order.</returns>
    /// <remarks>
    /// One level of nesting is enough to tell a wrapped payload from a bare one, which is the question these
    /// diagnostics answer. Recursing without bound would be a way of reconstructing an arbitrarily deep body
    /// from its own member names, and nothing here needs that.
    /// </remarks>
    private static IEnumerable<string> MemberNames(JsonElement element)
    {
        foreach (JsonProperty member in element.EnumerateObject())
        {
            if (member.Value.ValueKind == JsonValueKind.Object)
            {
                foreach (JsonProperty nested in member.Value.EnumerateObject())
                {
                    yield return member.Name + "." + nested.Name;
                }
            }
            else
            {
                yield return member.Name;
            }
        }
    }

    /// <summary>Computes a truncated hexadecimal digest of a string.</summary>
    /// <param name="value">The value to digest.</param>
    /// <returns>The first four bytes of the SHA-256 digest, in lower-case hexadecimal.</returns>
    private static string ShortDigest(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..8].ToLowerInvariant();

    /// <summary>Identifies one persona's sign-in within one host.</summary>
    /// <param name="UserName">The account name presented.</param>
    /// <param name="Password">The credential presented.</param>
    /// <param name="PortalId">The tenant named in the query string, if any.</param>
    /// <param name="Host">The host addressed, if any.</param>
    /// <remarks>
    /// All four members participate, because all four can change the token that comes back: the host and the
    /// tenant identifier between them decide which portal the token is issued for, and two accounts may share
    /// neither name nor credential. Held in memory only; nothing writes any part of this to a log.
    /// </remarks>
    private readonly record struct PersonaKey(string UserName, string Password, int? PortalId, string? Host);

    /// <summary>Sign-in results cached for the lifetime of one host.</summary>
    private sealed class SessionCache
    {
        private readonly Dictionary<PersonaKey, CachedSession> _entries = new();

        /// <summary>Serialises sign-ins so a persona requested concurrently is fetched once.</summary>
        internal SemaphoreSlim Gate { get; } = new(1, 1);

        /// <summary>Reads a cached body when one is present and not near expiry.</summary>
        /// <param name="key">The persona.</param>
        /// <returns>The cached response body, or <see langword="null"/> when no usable entry exists.</returns>
        /// <remarks>
        /// Absence is reported by the return value rather than through an argument, so no member of this file -
        /// public or private - carries an <c>out</c> parameter. A cached body is never null when present, so
        /// the two states cannot be confused.
        /// </remarks>
        internal string? Read(PersonaKey key)
        {
            lock (_entries)
            {
                return _entries.TryGetValue(key, out CachedSession entry)
                    && entry.ExpiresAtUtc - DateTime.UtcNow > ExpiryMargin
                    ? entry.Body
                    : null;
            }
        }

        /// <summary>Records a sign-in.</summary>
        /// <param name="key">The persona.</param>
        /// <param name="body">The response body to replay.</param>
        /// <param name="expiresAtUtc">When the access token it carries lapses.</param>
        internal void Write(PersonaKey key, string body, DateTime expiresAtUtc)
        {
            lock (_entries)
            {
                _entries[key] = new CachedSession(body, expiresAtUtc);
            }
        }

        /// <summary>Discards every entry.</summary>
        internal void Clear()
        {
            lock (_entries)
            {
                _entries.Clear();
            }
        }

        /// <summary>One cached sign-in.</summary>
        /// <param name="Body">The response body, replayed rather than a shared object handed out.</param>
        /// <param name="ExpiresAtUtc">When the access token it carries lapses.</param>
        private readonly record struct CachedSession(string Body, DateTime ExpiresAtUtc);
    }
}

/// <summary>
/// A response together with the correlation identifier that was sent and the one that came back.
/// </summary>
/// <remarks>
/// Exists so that the round-trip assertion is a single expression rather than three statements, and so that
/// the sent value cannot be lost between stamping the request and reading the response - which is the mistake
/// that makes such an assertion pass vacuously.
/// </remarks>
public sealed class CorrelatedResponse : IDisposable
{
    /// <summary>Initialises a new instance of the <see cref="CorrelatedResponse"/> class.</summary>
    /// <param name="response">The response, which this instance takes ownership of.</param>
    /// <param name="sentCorrelationId">The identifier the request carried.</param>
    /// <param name="receivedCorrelationId">The identifier the response carried, if any.</param>
    /// <exception cref="ArgumentNullException"><paramref name="response"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="sentCorrelationId"/> is blank.</exception>
    internal CorrelatedResponse(
        HttpResponseMessage response,
        string sentCorrelationId,
        string? receivedCorrelationId)
    {
        ArgumentNullException.ThrowIfNull(response);
        ArgumentException.ThrowIfNullOrWhiteSpace(sentCorrelationId);

        Response = response;
        SentCorrelationId = sentCorrelationId;
        ReceivedCorrelationId = receivedCorrelationId;
    }

    /// <summary>The response. Disposed with this instance.</summary>
    public HttpResponseMessage Response { get; }

    /// <summary>The identifier the request carried.</summary>
    public string SentCorrelationId { get; }

    /// <summary>
    /// The identifier the response carried, or <see langword="null"/> when it carried none - which is itself a
    /// contract failure, because every response is required to carry one.
    /// </summary>
    public string? ReceivedCorrelationId { get; }

    /// <summary>Whether the identifier sent is the identifier that came back.</summary>
    /// <remarks>
    /// Compared exactly. The middleware echoes a caller-supplied identifier unchanged and only invents one
    /// when the caller supplied none, so a case-insensitive or trimming comparison would accept a value the
    /// contract does not permit.
    /// </remarks>
    public bool RoundTripped =>
        string.Equals(SentCorrelationId, ReceivedCorrelationId, StringComparison.Ordinal);

    /// <inheritdoc />
    public void Dispose() => Response.Dispose();
}
