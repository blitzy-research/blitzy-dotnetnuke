using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;
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
            $"/api/v1/portals/{ApiTestFixture.Route(_fixture.Seed.PortalId)}/users",
            UriKind.Relative));

    /// <summary>
    /// The administrative credential reset is bounded. This is the subtler of the three misses: the address
    /// reads as though the path matcher would cover it, and whole-segment comparison meant it did not.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task ResetPassword_IsRateLimited() =>
        await AssertBoundedAsync(new Uri(
            $"/api/v1/portals/{ApiTestFixture.Route(_fixture.Seed.PortalId)}"
                + $"/users/{ApiTestFixture.Route(_fixture.Seed.MemberUserId)}/password-reset",
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
            $"/api/v1/portals/{ApiTestFixture.Route(_fixture.Seed.PortalId)}"
                + $"/users/{ApiTestFixture.Route(_fixture.Seed.MemberUserId)}/password",
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
}
