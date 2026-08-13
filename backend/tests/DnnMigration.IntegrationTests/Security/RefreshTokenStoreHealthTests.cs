using DnnMigration.Application.Options;
using DnnMigration.Domain.Abstractions.Services;
using DnnMigration.Domain.Enums;
using DnnMigration.Infrastructure.HealthChecks;
using DnnMigration.Infrastructure.Security;
using FluentAssertions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Xunit;

namespace DnnMigration.IntegrationTests.Security;

/// <summary>
/// Verifies that the refresh-token store's state model is reported by the running application rather than
/// documented only in prose.
/// </summary>
/// <remarks>
/// <para>
/// WHAT THIS PROBE IS FOR. The store this solution ships keeps refresh state in the API process, so it is
/// neither shared between replicas nor carried across a restart, and that has one dynamic consequence:
/// once the tracked-generation ceiling is reached, the oldest refresh families are retired early and their
/// holders must sign in again. Before this probe existed, the standing limitation appeared in three documents
/// and in no running deployment, and the dynamic consequence appeared nowhere at all.
/// </para>
/// <para>
/// The DESCRIPTION is asserted as well as the status, and deliberately so: the health response body is
/// contractually four members, and the API layer's health logging deliberately excludes each probe's data
/// dictionary, so the description is the ONLY channel by which this probe's numbers reach an operator. A
/// change that moved a number out of it would silently un-report the thing this probe exists to report.
/// </para>
/// </remarks>
[Trait("Category", "Integration")]
public class RefreshTokenStoreHealthTests
{
    private static readonly DateTime Origin = new(2026, 3, 1, 12, 0, 0, DateTimeKind.Utc);

    /// <summary>
    /// With capacity to spare the probe is healthy and states the locality and the usage.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task AnUnsaturatedInProcessStoreIsHealthyAndReportsItsLocality()
    {
        RefreshTokenStore store = Store(maximumTrackedTokens: 1_000);
        await store.IssueAsync(new RefreshTokenSubject(5_001, 0));

        HealthCheckResult result = await Check(store).CheckHealthAsync(Context());

        result.Status.Should().Be(HealthStatus.Healthy);
        result.Description.Should().Contain(
            "not shared between replicas",
            "the standing limitation is what an operator has to know before adding an instance");
        result.Description.Should().Contain(
            "does not survive a restart",
            "and a redeploy signing everyone out is the other half of it");
        result.Description.Should().Contain(
            "1 of 1000 generations",
            "the numbers reach an operator through the description, not through the data dictionary");

        result.Data["replicaSafe"].Should().Be(false);
        result.Data["survivesRestart"].Should().Be(false);
        result.Data["trackedGenerations"].Should().Be(1);
        result.Data["trackedGenerationCeiling"].Should().Be(1_000);
        result.Data["provider"].Should().Be(RefreshTokenStoreOptions.InProcessProvider);
        result.Data["storeIsThisSolutions"].Should().Be(true);
    }

    /// <summary>
    /// A store at its ceiling is degraded, and says which setting to change and what the alternative is.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// Degraded rather than unhealthy on purpose: a saturated store still serves every request and still
    /// issues tokens, so reporting it unhealthy would take a working instance out of service over a condition
    /// whose cost is a sign-in.
    /// </remarks>
    [Fact]
    public async Task AStoreAtItsCeilingIsDegradedAndNamesTheRemedy()
    {
        RefreshTokenStore store = Store(maximumTrackedTokens: 2);
        await store.IssueAsync(new RefreshTokenSubject(5_002, 0));
        await store.IssueAsync(new RefreshTokenSubject(5_003, 0));

        HealthCheckResult result = await Check(store, maximumTrackedTokens: 2)
            .CheckHealthAsync(Context());

        result.Status.Should().Be(HealthStatus.Degraded);
        result.Description.Should().Contain("retired early");
        result.Description.Should().Contain(
            $"{RefreshTokenStoreOptions.SectionName}:{nameof(RefreshTokenStoreOptions.MaximumTrackedTokens)}",
            "a degraded report has to name the setting an operator can change");
        result.Description.Should().Contain(
            RefreshTokenStoreOptions.ExternalProvider,
            "and the alternative to raising it, which is a shared store");
        result.Data["utilisationPercent"].Should().Be(100);
    }

    /// <summary>
    /// With a deployment-supplied store active the probe reports the substitution and claims nothing else.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// Capacity is a property of this solution's implementation rather than of the contract, so a replacement
    /// is not obliged to answer for it. Reporting it healthy with an explicit "no capacity or locality" is the
    /// honest answer; inventing one would be worse than saying nothing.
    /// </remarks>
    [Fact]
    public async Task ASubstitutedStoreIsReportedAsSubstitutedAndNothingIsClaimedForIt()
    {
        RefreshTokenStoreHealth check = new(
            new SubstituteRefreshTokenStore(),
            Options.Create(new RefreshTokenStoreOptions
            {
                Provider = RefreshTokenStoreOptions.ExternalProvider,
            }));

        HealthCheckResult result = await check.CheckHealthAsync(Context());

        result.Status.Should().Be(HealthStatus.Healthy);
        result.Description.Should().Contain("deployment-supplied store");
        result.Description.Should().Contain("no capacity or locality");
        result.Data["storeIsThisSolutions"].Should().Be(false);
        result.Data.Should().NotContainKey(
            "replicaSafe",
            "this probe cannot know whether a replacement is replica-safe, so it must not say");
        result.Data["provider"].Should().Be(RefreshTokenStoreOptions.ExternalProvider);
    }

    /// <summary>Utilisation is a whole percentage of the configured ceiling.</summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task UtilisationIsExpressedAsAWholePercentageOfTheCeiling()
    {
        RefreshTokenStore store = Store(maximumTrackedTokens: 8);
        for (int userId = 5_010; userId < 5_012; userId++)
        {
            await store.IssueAsync(new RefreshTokenSubject(userId, 0));
        }

        HealthCheckResult result = await Check(store, maximumTrackedTokens: 8)
            .CheckHealthAsync(Context());

        result.Data["utilisationPercent"].Should().Be(25);
        result.Description.Should().Contain("25 per cent");
    }

    /// <summary>Builds the probe over a store, with settings that agree with it.</summary>
    /// <param name="store">The store the probe reports on.</param>
    /// <param name="maximumTrackedTokens">The ceiling the settings declare.</param>
    /// <returns>The probe.</returns>
    private static RefreshTokenStoreHealth Check(
        RefreshTokenStore store,
        int maximumTrackedTokens = 1_000) => new(
        store,
        Options.Create(new RefreshTokenStoreOptions
        {
            Provider = RefreshTokenStoreOptions.InProcessProvider,
            MaximumTrackedTokens = maximumTrackedTokens,
        }));

    /// <summary>Builds a store with a fixed clock and an explicit ceiling.</summary>
    /// <param name="maximumTrackedTokens">The tracked-generation ceiling.</param>
    /// <returns>The store.</returns>
    /// <remarks>
    /// The ceilings used here are far below the deployment floor of one thousand that the Api layer's
    /// validator enforces, which is what makes a saturation fact reachable at all: the store's own contract
    /// requires only that the ceiling be at least one, and this suite exercises the store against that
    /// contract rather than against host policy.
    /// </remarks>
    private static RefreshTokenStore Store(int maximumTrackedTokens) => new(
        new FixedClock(Origin),
        Options.Create(new JwtOptions
        {
            Secret = "unit-test-signing-secret-with-enough-entropy-0123456789",
            Issuer = "DnnMigration",
            Audience = "DnnMigration",
            ExpirationMinutes = 30,
            RefreshTokenExpirationDays = 7,
            RefreshTokenAbsoluteExpirationDays = 30,
        }),
        Options.Create(new RefreshTokenStoreOptions
        {
            Provider = RefreshTokenStoreOptions.InProcessProvider,
            MaximumTrackedTokens = maximumTrackedTokens,
        }));

    /// <summary>The minimum context the probe's signature requires; it reads nothing from it.</summary>
    /// <returns>The context.</returns>
    private static HealthCheckContext Context() => new()
    {
        Registration = new HealthCheckRegistration(
            "refresh-token-store",
            _ => new NeverInvokedCheck(),
            HealthStatus.Degraded,
            tags: null),
    };

    /// <summary>A clock that does not move, because no fact here depends on time passing.</summary>
    /// <param name="utcNow">The instant it reports.</param>
    private sealed class FixedClock(DateTime utcNow) : IClock
    {
        /// <inheritdoc />
        public DateTime UtcNow => utcNow;
    }

    /// <summary>Satisfies the registration's factory; the probe under test is invoked directly.</summary>
    private sealed class NeverInvokedCheck : IHealthCheck
    {
        /// <inheritdoc />
        public Task<HealthCheckResult> CheckHealthAsync(
            HealthCheckContext context,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("Registered to satisfy the context, never invoked.");
    }

    /// <summary>A stand-in for a deployment-supplied store; only its TYPE matters here.</summary>
    private sealed class SubstituteRefreshTokenStore : IRefreshTokenStore
    {
        /// <inheritdoc />
        /// <remarks>
        /// <see langword="true"/>: this stand-in represents a deployment-supplied store, and the contract
        /// admits <see langword="true"/> only for state every replica shares and a restart survives, which
        /// is exactly the claim the probe under test has to report differently from the shipped store.
        /// </remarks>
        public bool IsAuthoritativeAcrossReplicas => true;

        /// <inheritdoc />
        public Task<RefreshTokenIssueResult> IssueAsync(
            RefreshTokenSubject subject,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("The substitute store is registered, not exercised.");

        /// <inheritdoc />
        public Task<RefreshTokenInspection> InspectAsync(
            string refreshToken,
            string clientBinding,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("The substitute store is registered, not exercised.");

        /// <inheritdoc />
        public Task<RefreshTokenRotationResult> RotateAsync(
            string refreshToken,
            string clientBinding,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("The substitute store is registered, not exercised.");

        /// <inheritdoc />
        public Task<RefreshTokenOutcome> RevokeAsync(
            string refreshToken,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("The substitute store is registered, not exercised.");

        /// <inheritdoc />
        public Task<RefreshTokenOutcome> RevokeAllForUserAsync(
            int userId,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("The substitute store is registered, not exercised.");

        /// <inheritdoc />
        public Task<RefreshTokenPurgeResult> PurgeSubjectAsync(
            RefreshTokenPurgeScope scope,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("The substitute store is registered, not exercised.");

        /// <inheritdoc />
        /// <remarks>
        /// PRIV-02. Answers with an empty reclamation rather than refusing, and it is the ONE member that does.
        /// The reclamation sweep is a hosted service that runs on a schedule in every host these facts build,
        /// so a substitute that threw here would raise out of a background timer during an unrelated
        /// assertion - a failure attributed to whichever fact happened to be running. Reporting "nothing to
        /// reclaim" is also true of a store that holds nothing.
        /// </remarks>
        public Task<RefreshTokenPurgeResult> PurgeRetiredAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(RefreshTokenPurgeResult.NothingHeld());
    }
}
