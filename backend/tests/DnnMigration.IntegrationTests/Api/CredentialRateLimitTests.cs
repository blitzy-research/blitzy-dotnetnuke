using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Reflection;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DnnMigration.IntegrationTests.Api;

/// <summary>
/// Covers the credential rate limits on the endpoints that HASH a credential outside the sign-in flow.
/// </summary>
/// <remarks>
/// <para>
/// Sign-in's own window is proved by <c>AuthApiTests</c>. This suite exists for the three endpoints that were
/// left with no window and no concurrency bound at all, because the limits were applied by matching whole
/// segments of the request path against a word list and none of the three matched it: account creation on
/// <c>/users</c>, tenant provisioning on <c>/portals</c>, which hashes the administrator credential it
/// creates, and the administrative credential reset, whose <c>password-reset</c> segment is equal to neither
/// <c>password</c> nor <c>reset</c>. Hashing is deliberately expensive, so an unbounded hashing endpoint lets
/// the caller decide how much of this process's time and memory is spent.
/// </para>
/// <para>
/// The opt-in policy that was documented as the remedy for exactly this case could not have supplied one
/// either: its partitioner asked the same path matcher and returned the shared no-limit partition, so
/// annotating any of the three would have enforced nothing. That is fixed as well - a named policy now applies
/// its window unconditionally, because an author who declares it has already stated what the heuristic was
/// guessing at - and the three sign-in actions that declare it exercise that path in <c>AuthApiTests</c>.
/// </para>
/// <para>
/// <b>WHAT THESE FACTS ATTRIBUTE THE REFUSAL TO.</b> None of the three endpoints below declares a named rate
/// limiting policy: each carries only the credential mark. A refusal here can therefore have come from
/// nowhere but the GLOBAL limiter's classifier reading that mark - which is the whole point, because the
/// global limiter is the one that chains BOTH bounds. Had these actions also declared a named policy, a
/// <c>429</c> would have been ambiguous between the two mechanisms and would have proved only that a window
/// existed, not that the process-wide concurrency ceiling had been reached by the same classifier.
/// </para>
/// <para>
/// <b>Every fact builds its own host, and that is a correctness requirement rather than tidiness.</b> The
/// shared fixture deliberately runs with a permissive permit count so no ordinary test can exhaust it, and a
/// limiter reads its options while services are composed - so the tight override must be in force before the
/// host is constructed. A separate host also means a separate limiter with its own budget, which matters
/// because the test server exposes no remote address and every request therefore lands in one shared
/// unattributed partition. Two facts sharing a host would spend each other's budget.
/// </para>
/// <para>
/// <b>Every fact uses an ANONYMOUS client.</b> The rate-limiting middleware is ordered before authentication,
/// so a permitted attempt answers <c>401</c> and is still charged a permit. That is what makes these facts
/// cheap and side-effect free: nothing is authorised, no account is created, no credential is written and no
/// row is touched, yet the limiter is exercised exactly as it would be by a real attempt.
/// </para>
/// </remarks>
[Trait("Category", "Integration")]
[Collection(IntegrationTestCollection.Name)]
public sealed class CredentialRateLimitTests
{
    /// <summary>
    /// Permits per window while these facts run: two. Small enough that a fact costs three requests.
    /// </summary>
    private const int TightPermitLimit = 2;

    /// <summary>
    /// How many times the negative control is called: comfortably beyond the tight limit, so a limiter that
    /// had been applied to it would certainly have refused one of them.
    /// </summary>
    private const int NegativeControlAttempts = TightPermitLimit + 4;

    /// <summary>The name of the marker attribute, matched by name so no internals need exposing.</summary>
    private const string MarkerAttributeName = "CredentialEndpointAttribute";

    /// <summary>The address the proxied host presents as the connecting peer, and declares as trusted.</summary>
    /// <remarks>
    /// A documentation-range address, so it can never coincide with anything the build environment routes.
    /// </remarks>
    private const string ProxyAddress = "198.51.100.7";

    /// <summary>The forwarded-address header the proxy in the shipped topology sets.</summary>
    private const string ForwardedForHeaderName = "X-Forwarded-For";

    /// <summary>Permit count of the profile-write policy.</summary>
    private const int ProfileWritePermitLimit = 20;

    /// <summary>Permit count of the invitation-code redemption policy.</summary>
    /// <remarks>
    /// Stated here as the number the production policy declares, so a change to that number fails this fact
    /// rather than passing quietly with a looser bound than the one that was reviewed.
    /// </remarks>
    private const int RedemptionPermitLimit = 5;

    private readonly ApiTestFixture _fixture;

    /// <summary>Initialises a new instance of the <see cref="CredentialRateLimitTests"/> class.</summary>
    /// <param name="fixture">The shared fixture, used for its configuration and seed identifiers only.</param>
    public CredentialRateLimitTests(ApiTestFixture fixture) => _fixture = fixture;

    /// <summary>
    /// Account creation is bounded. Before the classifier consulted endpoint metadata this endpoint hashed a
    /// credential on every call with no window and no concurrency bound whatsoever.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task CreateUser_IsRateLimited() =>
        await AssertBoundedAsync(new Uri(
            "/api/v1/users",
            UriKind.Relative));

    /// <summary>
    /// The administrative credential reset is bounded. This is the subtler of the three misses: the address
    /// reads as though the path matcher would cover it, and whole-segment comparison meant it did not.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task ResetPassword_IsRateLimited() =>
        await AssertBoundedAsync(new Uri(
            $"/api/v1/users/{ApiTestFixture.Route(_fixture.Seed.MemberUserId)}/password-reset",
            UriKind.Relative));

    /// <summary>
    /// Tenant provisioning is bounded, because it hashes the credential of the administrator account it
    /// creates.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task CreatePortal_IsRateLimited() =>
        await AssertBoundedAsync(new Uri("/api/v1/portals", UriKind.Relative));

    /// <summary>
    /// The self-service credential change is bounded. It was already covered by the path matcher, so this
    /// fact guards against the marker work having narrowed what was previously bounded.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task ChangePassword_IsStillRateLimited() =>
        await AssertBoundedAsync(new Uri(
            $"/api/v1/users/{ApiTestFixture.Route(_fixture.Seed.MemberUserId)}/password",
            UriKind.Relative));

    /// <summary>
    /// THE NEGATIVE CONTROL. An ordinary write that handles no credential is NOT bounded, however many times
    /// it is called. Without this, every fact above would pass equally well against a limiter that had been
    /// widened to bound the whole API - which would spend one shared credential budget on ordinary
    /// administration traffic and take the console down under its own load.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task OrdinaryWrite_IsNotRateLimited()
    {
        using (ApiTestFixture.OverrideEnvironment(TightConfiguration()))
        {
            await using var host = new TightlyLimitedHost();
            using HttpClient client = host.CreateClient();

            var route = new Uri(
                $"/api/v1/portals/{ApiTestFixture.Route(_fixture.Seed.PortalId)}",
                UriKind.Relative);

            for (int attempt = 0; attempt < NegativeControlAttempts; attempt++)
            {
                using HttpResponseMessage response = await client.PutAsJsonAsync(
                    route,
                    new { portalName = "not applied" },
                    ApiTestFixture.Json);

                response.StatusCode.Should().Be(
                    HttpStatusCode.Unauthorized,
                    "a write that handles no credential must not consume the credential budget");
            }
        }
    }

    /// <summary>
    /// Profile replacements have their own bounded budget because an accepted request may evaluate a set of
    /// tenant-authored regular expressions.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task ProfileUpdate_IsRateLimitedOnItsOwnBudget()
    {
        using (ApiTestFixture.OverrideEnvironment(_fixture.HostConfiguration()))
        {
            await using var host = new TightlyLimitedHost();
            using HttpClient client = host.CreateClient();
            // FLAT: the account routes are mounted at api/v1/users and name no portal segment, so the tenant
            // comes from the arrival host. Addressed with the portal in the path this reached NO endpoint,
            // and an unmatched request carries no rate-limiting metadata - so the limiter never charged and
            // every attempt was answered by authentication instead of by the budget under test.
            var route = new Uri("/api/v1/users/1/profile", UriKind.Relative);

            for (int permitted = 0; permitted < ProfileWritePermitLimit; permitted++)
            {
                using HttpResponseMessage allowed = await client.PutAsJsonAsync(
                    route,
                    new { properties = Array.Empty<object>() },
                    ApiTestFixture.Json);

                allowed.StatusCode.Should().Be(
                    HttpStatusCode.Unauthorized,
                    "the profile limiter runs before authentication and charges each attempted write");
            }

            using HttpResponseMessage rejected = await client.PutAsJsonAsync(
                route,
                new { properties = Array.Empty<object>() },
                ApiTestFixture.Json);

            rejected.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
            rejected.Headers.RetryAfter.Should().NotBeNull();
        }
    }

    /// <summary>
    /// Invitation-code redemption is bounded, on a window OF ITS OWN, so guessing cannot spend the budget
    /// sign-in needs and cannot buy itself a fresh budget by exhausting somebody else's.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// <para>
    /// SEC: THE REGRESSION TEST FOR AN UNBOUNDED ROLE-GRANT ORACLE. The action takes a secret, compares it
    /// against every role of the tenant - published or not, free or not - grants membership of every role that
    /// bears it, and answers a match and a miss differently. Nothing bounded it: it declared no policy, and
    /// the fall-back path classifier matched a closed word list naming nothing in this route, so an
    /// authenticated account could guess without limit.
    /// </para>
    /// <para>
    /// BOTH HALVES ARE ASSERTED, and one alone proves neither. That the window refuses once spent is what
    /// shows the endpoint is bounded at all; that it takes FIVE attempts rather than the two this host allows
    /// a credential request is what shows the budget is its OWN rather than a share of the credential window -
    /// which is the property that stops guessing traffic suppressing sign-in.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Redemption_IsRateLimitedOnItsOwnBudget()
    {
        using (ApiTestFixture.OverrideEnvironment(_fixture.HostConfiguration()))
        {
            await using var host = new TightlyLimitedHost();
            using HttpClient client = host.CreateClient();
            var route = new Uri("/api/v1/users/1/services/redemptions", UriKind.Relative);

            for (int permitted = 0; permitted < RedemptionPermitLimit; permitted++)
            {
                using HttpResponseMessage allowed = await client.PostAsJsonAsync(
                    route,
                    new { code = "guess-attempt" },
                    ApiTestFixture.Json);

                allowed.StatusCode.Should().Be(
                    HttpStatusCode.Unauthorized,
                    "the redemption limiter runs before authentication and charges each attempted submission, "
                    + "and five are permitted where a credential request would have been refused after two");
            }

            using HttpResponseMessage rejected = await client.PostAsJsonAsync(
                route,
                new { code = "guess-attempt" },
                ApiTestFixture.Json);

            rejected.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
            rejected.Headers.RetryAfter.Should().NotBeNull();
        }
    }

    /// <summary>
    /// The redemption window keys on the ACCOUNT as well as on the client address, so one account exhausting
    /// its budget does not refuse another account reaching the API from the same address.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// <para>
    /// SEC: THE PARTITION IS THE OTHER HALF OF THE BOUND. An address-only key would let one account spread its
    /// guessing over as many addresses as it can reach; an account-only key would let a pool of accounts behind
    /// one address share the work out. This fact proves the account half is present, and the fact above proves
    /// the window is enforced - together they say a fresh budget costs a fresh account AND a fresh address. The
    /// client address is identical for both callers here, because the test server presents no peer address at
    /// all, so the account half is the only thing that can separate them.
    /// </para>
    /// <para>
    /// The account half is read from the ROUTE, because the limiter runs before authentication and there is no
    /// authenticated principal to read when the partition is chosen. Each caller here therefore names its own
    /// account twice - in the route it posts to and in the credential it presents - which is the only
    /// combination the action's <c>AccountOwner</c> policy admits, and is why a caller cannot usefully invent
    /// route values to mint itself extra budgets: every request in such a partition is refused before a code is
    /// compared.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task RedemptionWindow_PartitionsOnTheAccount()
    {
        using (ApiTestFixture.OverrideEnvironment(_fixture.HostConfiguration()))
        {
            await using var host = new TightlyLimitedHost();
            using HttpClient first = host.CreateClient();
            using HttpClient second = host.CreateClient();

            first.DefaultRequestHeaders.Authorization = BearerFor(_fixture.Seed.MemberUserId);
            second.DefaultRequestHeaders.Authorization = BearerFor(_fixture.Seed.AdminUserId);

            var firstRoute = new Uri(
                $"/api/v1/users/{ApiTestFixture.Route(_fixture.Seed.MemberUserId)}/services/redemptions",
                UriKind.Relative);
            var secondRoute = new Uri(
                $"/api/v1/users/{ApiTestFixture.Route(_fixture.Seed.AdminUserId)}/services/redemptions",
                UriKind.Relative);

            for (int spent = 0; spent <= RedemptionPermitLimit; spent++)
            {
                using HttpResponseMessage _ = await first.PostAsJsonAsync(
                    firstRoute,
                    new { code = "guess-attempt" },
                    ApiTestFixture.Json);
            }

            using HttpResponseMessage exhausted = await first.PostAsJsonAsync(
                firstRoute,
                new { code = "guess-attempt" },
                ApiTestFixture.Json);

            exhausted.StatusCode.Should().Be(
                HttpStatusCode.TooManyRequests,
                "the first account has spent its own window");

            using HttpResponseMessage other = await second.PostAsJsonAsync(
                secondRoute,
                new { code = "guess-attempt" },
                ApiTestFixture.Json);

            other.StatusCode.Should().NotBe(
                HttpStatusCode.TooManyRequests,
                "a second account from the same address holds a budget of its own");
        }
    }

    /// <summary>
    /// Mints a bearer credential for one seeded account, so each caller acts as the account its route names.
    /// </summary>
    /// <param name="userId">The account the credential names.</param>
    /// <returns>The authorisation header to present.</returns>
    /// <remarks>
    /// Minted rather than obtained by signing in, because signing in is itself a credential-bearing request
    /// and would spend the very budget these facts measure - the sign-in window is set to two permits while
    /// they run, so two sign-ins would exhaust it before the fact under test began. The token names the
    /// seeded tenant so the request resolves an arrival tenant and reaches the action's own authorisation,
    /// which is what makes each caller a genuine owner of the account it posts to rather than a stranger
    /// bouncing off authorisation.
    /// </remarks>
    private AuthenticationHeaderValue BearerFor(int userId)
        => new(
            "Bearer",
            AuthenticatedClientFactory.CreateToken(
                ApiTestFixture.SigningSecret,
                ApiTestFixture.Issuer,
                ApiTestFixture.Audience,
                userId,
                userName: "rate-limit-partition-" + userId.ToString(CultureInfo.InvariantCulture),
                _fixture.Seed.PortalId));

    /// <summary>
    /// The set of actions declaring themselves credential endpoints is exactly the reviewed inventory.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Both directions of drift matter. A hashing action added without the mark is silently unbounded, which
    /// is the defect this work corrected; and a mark added to an ordinary action quietly spends a shared
    /// budget on traffic that does no cryptographic work. Pinning the inventory turns either into a failing
    /// test rather than a property nobody is looking at.
    /// </para>
    /// <para>
    /// Matched by attribute NAME rather than by type, because the attribute is internal to the API assembly
    /// and exposing internals to a test project purely to name it here would weaken the production
    /// assembly's surface for the convenience of one assertion.
    /// </para>
    /// </remarks>
    [Fact]
    public void CredentialEndpointMarks_AreExactlyTheReviewedInventory()
    {
        var expected = new[]
        {
            "AuthController.LoginAsync",
            "AuthController.LogoutAsync",
            "AuthController.RefreshAsync",
            "PortalsController.CreateAsync",
            "UsersController.ChangePasswordAsync",
            "UsersController.CreateAsync",
            // SEC: REDEMPTION IS IN THE INVENTORY, AND ITS ABSENCE WAS THE DEFECT. The action does not hash a
            // credential, which is why it was not here, but it SUBMITS a secret - an invitation code that
            // grants role membership when it matches - and its two answers are distinguishable, so it is an
            // online guessing oracle in substance. The mark brings it under the process-wide concurrency bound
            // and the body limit; the window it draws on is its own, declared on the action, because guessing
            // must not be able to spend the budget sign-in needs.
            "UsersController.RedeemServiceCodeAsync",
            "UsersController.ResetPasswordAsync",
        };

        var found = new List<string>();

        foreach (Type controller in typeof(Program).Assembly.GetTypes()
            .Where(type => typeof(ControllerBase).IsAssignableFrom(type) && !type.IsAbstract))
        {
            controller.GetCustomAttributes(inherit: false)
                .Select(attribute => attribute.GetType().Name)
                .Should().NotContain(
                    MarkerAttributeName,
                    "a class-level mark would spend the credential budget on the controller's ordinary reads");

            foreach (MethodInfo action in controller.GetMethods(
                BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                bool marked = action.GetCustomAttributes(inherit: false)
                    .Any(attribute => string.Equals(
                        attribute.GetType().Name,
                        MarkerAttributeName,
                        StringComparison.Ordinal));

                if (marked)
                {
                    found.Add($"{controller.Name}.{action.Name}");
                }
            }
        }

        found.Order(StringComparer.Ordinal).Should().Equal(
            expected.Order(StringComparer.Ordinal),
            "an action that hashes a credential must be marked, and an action that does not must not be");
    }

    /// <summary>
    /// SEC-022. The caller-description read is bounded by a window OF ITS OWN, so polling it cannot spend the
    /// budget every other caller needs in order to sign in.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// Both halves matter and a single assertion proves neither. Spending the read's whole window and then
    /// finding sign-in still permitted is what shows the budgets are separate; finding the read itself refused
    /// once its own window is spent is what shows it is still bounded, so the fix did not simply exempt it.
    /// One controller-wide declaration produced the coupling this fact rules out.
    /// </remarks>
    [Fact]
    public async Task SessionRead_DoesNotSpendTheCredentialBudget()
    {
        using (ApiTestFixture.OverrideEnvironment(TightConfiguration()))
        {
            await using var host = new TightlyLimitedHost();
            using HttpClient client = host.CreateClient();

            var callerDescription = new Uri("/api/v1/auth/me", UriKind.Relative);

            for (int permitted = 0; permitted < TightPermitLimit; permitted++)
            {
                using HttpResponseMessage allowed = await client.GetAsync(callerDescription);

                allowed.StatusCode.Should().Be(
                    HttpStatusCode.Unauthorized,
                    "the limiter runs before authentication, so an anonymous read is charged and turned away");
            }

            using HttpResponseMessage readRefused = await client.GetAsync(callerDescription);

            readRefused.StatusCode.Should().Be(
                HttpStatusCode.TooManyRequests,
                "the caller-description read must remain bounded: a stolen token would otherwise be free to "
                + "probe it without limit");

            using HttpResponseMessage signIn = await client.PostAsJsonAsync(
                new Uri("/api/v1/auth/login", UriKind.Relative),
                new { username = "nobody", password = "not-a-password" },
                ApiTestFixture.Json);

            signIn.StatusCode.Should().NotBe(
                HttpStatusCode.TooManyRequests,
                "sign-in draws on the credential window, which the caller-description read must not spend");
        }
    }

    /// <summary>
    /// SEC-022. With the deployment's proxy declared, the credential window partitions on the FORWARDED
    /// client address, so one caller behind the proxy cannot spend every other caller's budget.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// <para>
    /// This is the fact the previous suite could not express. Addressed directly, every request already
    /// arrives from a distinct socket, so a per-address partition looks correct however the forwarded headers
    /// are treated. Behind a proxy every request arrives from the PROXY's address, and without the forwarded
    /// headers stage all callers collapse into one partition and therefore one budget - which is a denial of
    /// service against authentication for everyone, reachable by any one caller.
    /// </para>
    /// <para>
    /// The test server exposes no remote address of its own, so the fixture host below assigns one before the
    /// application's own pipeline runs and declares that same address as the trusted proxy. That is exactly
    /// the shipped topology in miniature: one proxy, named explicitly, forwarding the caller's address.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task CredentialWindow_PartitionsOnTheForwardedAddress_WhenTheProxyIsTrusted()
    {
        Dictionary<string, string?> configuration = TightConfiguration();
        configuration["Proxy__KnownProxies__0"] = ProxyAddress;

        using (ApiTestFixture.OverrideEnvironment(configuration))
        {
            await using var host = new ProxiedHost();
            using HttpClient client = host.CreateClient();

            var signIn = new Uri("/api/v1/auth/login", UriKind.Relative);

            for (int permitted = 0; permitted < TightPermitLimit; permitted++)
            {
                using HttpResponseMessage allowed = await SendAsync(client, signIn, "203.0.113.10");
                allowed.StatusCode.Should().NotBe(HttpStatusCode.TooManyRequests);
            }

            using HttpResponseMessage exhausted = await SendAsync(client, signIn, "203.0.113.10");
            exhausted.StatusCode.Should().Be(
                HttpStatusCode.TooManyRequests,
                "the first caller has spent its own budget");

            using HttpResponseMessage other = await SendAsync(client, signIn, "203.0.113.20");
            other.StatusCode.Should().NotBe(
                HttpStatusCode.TooManyRequests,
                "a second caller behind the same proxy holds a budget of its own; without the forwarded "
                + "headers stage both would share one");
        }
    }

    /// <summary>
    /// SEC-022, the safe default. With NO proxy declared, a forwarded address is ignored, so a caller cannot
    /// name its own partition and mint itself an unlimited budget.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// The forwarded headers stage is not even registered unless the deployment names what it trusts, which is
    /// what this fact pins. A header honoured from an undeclared hop is worse than one ignored: the value is
    /// supplied by whoever made the request, so trusting it lets a caller choose a fresh partition per
    /// request and defeat the window entirely.
    /// </remarks>
    [Fact]
    public async Task CredentialWindow_IgnoresTheForwardedAddress_WhenNoProxyIsTrusted()
    {
        using (ApiTestFixture.OverrideEnvironment(TightConfiguration()))
        {
            await using var host = new ProxiedHost();
            using HttpClient client = host.CreateClient();

            var signIn = new Uri("/api/v1/auth/login", UriKind.Relative);

            for (int permitted = 0; permitted < TightPermitLimit; permitted++)
            {
                using HttpResponseMessage allowed = await SendAsync(client, signIn, "203.0.113.30");
                allowed.StatusCode.Should().NotBe(HttpStatusCode.TooManyRequests);
            }

            using HttpResponseMessage claimingAnotherAddress = await SendAsync(client, signIn, "203.0.113.40");

            claimingAnotherAddress.StatusCode.Should().Be(
                HttpStatusCode.TooManyRequests,
                "an undeclared proxy's forwarded address must not be honoured, or a caller could choose its "
                + "own partition per request");
        }
    }

    /// <summary>
    /// SEC-016. A refusal from the credential limiter is marked non-cacheable, exactly like the token-bearing
    /// success it stands in for.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// This is the response an action filter cannot reach: the limiter short-circuits the pipeline before any
    /// action or filter runs, which is why the directive is applied by a pipeline stage placed immediately
    /// after routing.
    /// </remarks>
    [Fact]
    public async Task CredentialRefusal_IsNotCacheable()
    {
        using (ApiTestFixture.OverrideEnvironment(TightConfiguration()))
        {
            await using var host = new TightlyLimitedHost();
            using HttpClient client = host.CreateClient();

            var signIn = new Uri("/api/v1/auth/login", UriKind.Relative);

            for (int permitted = 0; permitted <= TightPermitLimit; permitted++)
            {
                using HttpResponseMessage _ = await client.PostAsJsonAsync(
                    signIn,
                    new { username = "nobody", password = "not-a-password" },
                    ApiTestFixture.Json);
            }

            using HttpResponseMessage refused = await client.PostAsJsonAsync(
                signIn,
                new { username = "nobody", password = "not-a-password" },
                ApiTestFixture.Json);

            refused.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
            refused.Headers.CacheControl.Should().NotBeNull();
            refused.Headers.CacheControl!.NoStore.Should().BeTrue();
            refused.Headers.Pragma.Should().Contain(directive => directive.Name == "no-cache");
        }
    }

    /// <summary>Sends one request that claims to have been forwarded from <paramref name="clientAddress"/>.</summary>
    /// <param name="client">The client addressing the proxied host.</param>
    /// <param name="route">The route to address.</param>
    /// <param name="clientAddress">The address the proxy claims the caller has.</param>
    /// <returns>The response, for the caller to assert on and dispose.</returns>
    private static async Task<HttpResponseMessage> SendAsync(
        HttpClient client,
        Uri route,
        string clientAddress)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, route)
        {
            Content = JsonContent.Create(
                new { username = "nobody", password = "not-a-password" },
                options: ApiTestFixture.Json),
        };

        request.Headers.TryAddWithoutValidation(ForwardedForHeaderName, clientAddress);

        return await client.SendAsync(request);
    }

    /// <summary>
    /// Spends the whole window on one address and asserts the next request is refused with a wait hint.
    /// </summary>
    /// <param name="route">The credential endpoint to exercise.</param>
    /// <returns>A task representing the assertion.</returns>
    /// <remarks>
    /// The permitted attempts are expected to answer <c>401</c> rather than to succeed: the limiter runs
    /// before authentication, so an anonymous request is charged a permit and then turned away without
    /// reaching the action. Asserting that status is also what proves the requests really arrived - a routing
    /// mistake would answer <c>404</c> and the fact would otherwise pass on the strength of the final
    /// refusal alone.
    /// </remarks>
    private async Task AssertBoundedAsync(Uri route)
    {
        using (ApiTestFixture.OverrideEnvironment(TightConfiguration()))
        {
            await using var host = new TightlyLimitedHost();
            using HttpClient client = host.CreateClient();

            for (int permitted = 0; permitted < TightPermitLimit; permitted++)
            {
                using HttpResponseMessage allowed = await client.PostAsJsonAsync(
                    route,
                    new { },
                    ApiTestFixture.Json);

                allowed.StatusCode.Should().Be(
                    HttpStatusCode.Unauthorized,
                    "the limiter is ordered before authentication, so a permitted attempt is charged and "
                    + "then turned away");
            }

            using HttpResponseMessage rejected = await client.PostAsJsonAsync(
                route,
                new { },
                ApiTestFixture.Json);

            rejected.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
            rejected.Headers.RetryAfter.Should().NotBeNull(
                "a refusal must tell the caller how long to wait rather than leaving it to guess");
        }
    }

    /// <summary>
    /// Revocation draws on a window OF ITS OWN, so sign-in traffic cannot starve it.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// <para>
    /// Both halves matter and a single assertion proves neither. Spending the whole credential window and
    /// then finding revocation still permitted is what shows the budgets are separate; finding revocation
    /// itself refused once ITS window is spent is what shows it is still bounded, so the fix did not simply
    /// exempt it. An exempt revocation endpoint would be worse than the coupling: it is
    /// <c>AllowAnonymous</c> by design - so that a caller whose access token has already expired can still
    /// withdraw its refresh token - and therefore an unbounded one is an unauthenticated endpoint anybody
    /// may hammer.
    /// </para>
    /// <para>
    /// The starvation this rules out did not require the same person. The window partitions on the caller's
    /// address, so any peer sharing one - everyone behind a single NAT or corporate egress - could spend it
    /// by guessing credentials, and thereby suppress a revocation somebody else was trying to perform.
    /// </para>
    /// <para>
    /// Every request here is refused on its merits rather than succeeding: the credentials are deliberately
    /// wrong and the token deliberately unknown, so nothing is authorised and no row is touched. What is
    /// asserted is only WHICH refusal arrives - a limiter refusal is <c>429</c>, and any other status proves
    /// the limiter permitted the request and the application then judged it.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Revocation_IsNotStarvedByTheCredentialBudget()
    {
        using (ApiTestFixture.OverrideEnvironment(TightConfiguration()))
        {
            await using var host = new TightlyLimitedHost();
            using HttpClient client = host.CreateClient();

            var signIn = new Uri("/api/v1/auth/login", UriKind.Relative);
            var revoke = new Uri("/api/v1/auth/logout", UriKind.Relative);

            // Spend the credential window exactly as a burst of failed sign-in attempts would.
            for (int permitted = 0; permitted < TightPermitLimit; permitted++)
            {
                using HttpResponseMessage attempt = await client.PostAsJsonAsync(
                    signIn,
                    new { username = "nobody", password = "not-a-password" },
                    ApiTestFixture.Json);

                attempt.StatusCode.Should().NotBe(
                    HttpStatusCode.TooManyRequests,
                    "the first attempts are within the window and must be judged on their merits");
            }

            using HttpResponseMessage signInRefused = await client.PostAsJsonAsync(
                signIn,
                new { username = "nobody", password = "not-a-password" },
                ApiTestFixture.Json);

            signInRefused.StatusCode.Should().Be(
                HttpStatusCode.TooManyRequests,
                "the credential window is now spent, which is the precondition this fact needs");

            // THE FINDING, STATED AS BEHAVIOUR: revocation must still be reachable.
            using HttpResponseMessage revocation = await client.PostAsJsonAsync(
                revoke,
                new { refreshToken = "not-a-token", clientBinding = string.Empty },
                ApiTestFixture.Json);

            revocation.StatusCode.Should().NotBe(
                HttpStatusCode.TooManyRequests,
                "withdrawing a refresh token must not draw on the window sign-in attempts spend, or "
                + "credential guessing would suppress credential withdrawal");

            // AND IT IS STILL BOUNDED. Spend revocation's own window and it refuses in its turn.
            for (int permitted = 1; permitted < TightPermitLimit; permitted++)
            {
                using HttpResponseMessage allowed = await client.PostAsJsonAsync(
                    revoke,
                    new { refreshToken = "not-a-token", clientBinding = string.Empty },
                    ApiTestFixture.Json);

                allowed.StatusCode.Should().NotBe(
                    HttpStatusCode.TooManyRequests,
                    "the remainder of revocation's own window must still be honoured");
            }

            using HttpResponseMessage revocationRefused = await client.PostAsJsonAsync(
                revoke,
                new { refreshToken = "not-a-token", clientBinding = string.Empty },
                ApiTestFixture.Json);

            revocationRefused.StatusCode.Should().Be(
                HttpStatusCode.TooManyRequests,
                "revocation must remain bounded: it is anonymous by design, so an unbounded endpoint "
                + "would be one anybody could hammer without limit");
        }
    }

    /// <summary>Builds the fixture's configuration with the window tightened.</summary>
    /// <returns>The environment the tightly limited host reads.</returns>
    private Dictionary<string, string?> TightConfiguration() =>
        new(_fixture.HostConfiguration(), StringComparer.Ordinal)
        {
            ["RateLimiting__Authentication__PermitLimit"] =
                TightPermitLimit.ToString(CultureInfo.InvariantCulture),
            ["RateLimiting__Authentication__WindowSeconds"] = "60",
        };

    /// <summary>
    /// A host built with the same environment the fixture publishes, so the only difference from the shared
    /// host is the tightened window the surrounding override applies.
    /// </summary>
    private sealed class TightlyLimitedHost : WebApplicationFactory<Program>
    {
        /// <inheritdoc />
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            ArgumentNullException.ThrowIfNull(builder);

            builder.UseEnvironment("Testing");
        }
    }

    /// <summary>
    /// The same host, with a connecting peer address assigned before the application's own pipeline runs.
    /// </summary>
    /// <remarks>
    /// The test server exposes no remote address, and the forwarded-headers stage refuses to promote anything
    /// unless the peer it can see is one the deployment named - so without this the proxied facts above could
    /// not distinguish "the header was ignored because the proxy is untrusted" from "there was no peer to
    /// compare". A startup filter is what makes the assignment possible: filters wrap the application's
    /// pipeline from the outside, so this runs ahead of every stage the application registers, including the
    /// forwarded-headers stage that must see it.
    /// </remarks>
    private sealed class ProxiedHost : WebApplicationFactory<Program>
    {
        /// <inheritdoc />
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            ArgumentNullException.ThrowIfNull(builder);

            builder.UseEnvironment("Testing");
            builder.ConfigureServices(services =>
                services.AddSingleton<IStartupFilter>(new PeerAddressStartupFilter(ProxyAddress)));
        }
    }

    /// <summary>Assigns the connecting peer address of every request before the application runs.</summary>
    /// <param name="peerAddress">The address to present as the connecting peer.</param>
    private sealed class PeerAddressStartupFilter(string peerAddress) : IStartupFilter
    {
        /// <inheritdoc />
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
        {
            ArgumentNullException.ThrowIfNull(next);

            IPAddress address = IPAddress.Parse(peerAddress);

            return app =>
            {
                app.Use(async (context, continuation) =>
                {
                    context.Connection.RemoteIpAddress = address;
                    await continuation().ConfigureAwait(false);
                });

                next(app);
            };
        }
    }
}
