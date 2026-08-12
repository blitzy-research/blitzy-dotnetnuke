using DnnMigration.Application.Options;
using DnnMigration.Domain.Abstractions.Services;
using DnnMigration.Domain.Enums;
using DnnMigration.Infrastructure.Security;
using FluentAssertions;
using Microsoft.Extensions.Options;
using Xunit;

namespace DnnMigration.IntegrationTests.Security;

/// <summary>
/// Verifies the refresh-token store's clock-dependent lifecycle: sliding expiry, the family ceiling, the
/// bounded same-client grace, and replay detection that survives a spent generation's own expiry.
/// </summary>
/// <remarks>
/// <para>
/// These facts live in the unit suite because they need the CLOCK moved, and moving it is the only honest
/// way to reach the states they assert. The store no longer persists anything, so its state cannot be
/// edited from outside; an earlier revision of the integration suite reached the same states with an
/// <c>UPDATE</c> against a target-owned <c>[DnnMigration].[RefreshTokens]</c> table, and that table has been
/// removed because AAP rule T4 forbids adding an object to the existing DotNetNuke schema.
/// </para>
/// <para>
/// The store is constructed directly rather than resolved, which is what makes the clock controllable.
/// <c>InternalsVisibleTo</c> on the Infrastructure project grants this assembly the visibility to do so, and
/// nothing is widened to public for the sake of a test.
/// </para>
/// </remarks>
[Trait("Category", "Integration")]
public class RefreshTokenStoreBehaviorTests
{
    private const string ClientA = "client-binding-a";
    private const string ClientB = "client-binding-b";

    private static readonly DateTime Origin = new(2026, 3, 1, 12, 0, 0, DateTimeKind.Utc);

    /// <summary>
    /// A spent generation is still recognised as a replay after its own sliding expiry has passed, and the
    /// replay revokes the live successor.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// The sliding lifetime is one day and the family ceiling thirty, so advancing two days puts the spent
    /// generation past its own expiry while its family is still live. Classifying it as expired rather than
    /// as a replay would be the detection gap this fact exists to prevent.
    /// </remarks>
    [Fact]
    public async Task ASpentGenerationPastItsSlidingExpiryIsStillAReplayAndRevokesTheFamily()
    {
        MutableClock clock = new(Origin);
        RefreshTokenStore store = Store(clock, slidingDays: 1, familyDays: 30);

        string first = await IssueAsync(store, userId: 4_101);

        RefreshTokenRotationResult rotated = await store.RotateAsync(first, ClientA);
        rotated.Outcome.Should().Be(RefreshTokenOutcome.Succeeded);
        string successor = rotated.RefreshToken!;

        clock.Advance(TimeSpan.FromDays(2));

        RefreshTokenRotationResult replay = await store.RotateAsync(first, ClientB);
        replay.Outcome.Should().Be(
            RefreshTokenOutcome.AlreadyUsed,
            "a consumed generation stays a theft signal until its family's absolute ceiling");

        RefreshTokenInspection inspection = await store.InspectAsync(successor, ClientA);
        inspection.Outcome.Should().Be(
            RefreshTokenOutcome.Revoked,
            "detecting the replay must retire the whole family, successor included");
    }

    /// <summary>A live generation past its own sliding expiry is refused as expired.</summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task ALiveGenerationPastItsSlidingExpiryIsExpired()
    {
        MutableClock clock = new(Origin);
        RefreshTokenStore store = Store(clock, slidingDays: 1, familyDays: 30);

        string token = await IssueAsync(store, userId: 4_102);

        clock.Advance(TimeSpan.FromDays(1) + TimeSpan.FromSeconds(1));

        (await store.InspectAsync(token, ClientA)).Outcome
            .Should().Be(RefreshTokenOutcome.Expired);
        (await store.RotateAsync(token, ClientA)).Outcome
            .Should().Be(RefreshTokenOutcome.Expired);
    }

    /// <summary>
    /// The family ceiling bounds the whole chain: a successor issued near the ceiling cannot be rotated past
    /// it, and its expiry never exceeds the ceiling.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task TheFamilyCeilingBoundsEveryGenerationAndEndsTheChain()
    {
        MutableClock clock = new(Origin);
        RefreshTokenStore store = Store(clock, slidingDays: 7, familyDays: 8);

        string token = await IssueAsync(store, userId: 4_103);

        clock.Advance(TimeSpan.FromDays(2));

        RefreshTokenRotationResult rotated = await store.RotateAsync(token, ClientA);
        rotated.Outcome.Should().Be(RefreshTokenOutcome.Succeeded);
        rotated.ExpiresAtUtc.Should().Be(
            Origin.AddDays(8),
            "a replacement's sliding expiry is clamped to the family ceiling it inherits");

        clock.Advance(TimeSpan.FromDays(7));

        (await store.RotateAsync(rotated.RefreshToken!, ClientA)).Outcome
            .Should().Be(RefreshTokenOutcome.Expired);
    }

    /// <summary>
    /// Two same-client exchanges inside the grace window yield one successor and one bounded refusal, and the
    /// same replay outside the window is treated as theft.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task TheSameClientGraceWindowIsBoundedInTime()
    {
        MutableClock clock = new(Origin);
        RefreshTokenStore store = Store(clock, slidingDays: 7, familyDays: 30);

        string inside = await IssueAsync(store, userId: 4_104);
        (await store.RotateAsync(inside, ClientA)).Outcome.Should().Be(RefreshTokenOutcome.Succeeded);

        clock.Advance(TimeSpan.FromSeconds(4));
        (await store.RotateAsync(inside, ClientA)).Outcome.Should().Be(
            RefreshTokenOutcome.ConcurrentUse,
            "a same-client retry within the grace window is a retry, not a replay");

        string outside = await IssueAsync(store, userId: 4_105);
        (await store.RotateAsync(outside, ClientA)).Outcome.Should().Be(RefreshTokenOutcome.Succeeded);

        clock.Advance(TimeSpan.FromSeconds(6));
        (await store.RotateAsync(outside, ClientA)).Outcome.Should().Be(
            RefreshTokenOutcome.AlreadyUsed,
            "past the window even the original client's presentation is indistinguishable from theft");
    }

    /// <summary>
    /// A family whose ceiling has passed is discarded rather than retained, so the tracked set does not grow
    /// without bound.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// Observable through the contract alone: a pruned generation reports <c>Unknown</c> rather than
    /// <c>Expired</c>, because it is no longer tracked at all. Pruning runs on issue, so a later issue is
    /// what triggers it.
    /// </remarks>
    [Fact]
    public async Task AFamilyPastItsCeilingIsPrunedRatherThanRetained()
    {
        MutableClock clock = new(Origin);
        RefreshTokenStore store = Store(clock, slidingDays: 1, familyDays: 1);

        string token = await IssueAsync(store, userId: 4_106);

        clock.Advance(TimeSpan.FromDays(1) + TimeSpan.FromSeconds(1));

        (await store.InspectAsync(token, ClientA)).Outcome.Should().Be(
            RefreshTokenOutcome.Expired,
            "before pruning runs the generation is still tracked and reports its own state");

        await IssueAsync(store, userId: 4_107);

        (await store.InspectAsync(token, ClientA)).Outcome.Should().Be(
            RefreshTokenOutcome.Unknown,
            "a family past its ceiling is reclaimed, so nothing remains to classify");
    }

    /// <summary>
    /// Revoking every family of one account is idempotent and reports the difference between the first call
    /// and a repeat.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task RevokingEveryFamilyOfAnAccountIsIdempotent()
    {
        MutableClock clock = new(Origin);
        RefreshTokenStore store = Store(clock, slidingDays: 7, familyDays: 30);

        string firstFamily = await IssueAsync(store, userId: 4_108);
        string secondFamily = await IssueAsync(store, userId: 4_108);
        string otherAccount = await IssueAsync(store, userId: 4_109);

        (await store.RevokeAllForUserAsync(4_108)).Should().Be(RefreshTokenOutcome.Succeeded);
        (await store.RevokeAllForUserAsync(4_108)).Should().Be(RefreshTokenOutcome.AlreadyRevoked);
        (await store.RevokeAllForUserAsync(4_110)).Should().Be(
            RefreshTokenOutcome.Unknown,
            "an account the store has never seen is unknown rather than already revoked");

        (await store.InspectAsync(firstFamily, ClientA)).Outcome.Should().Be(RefreshTokenOutcome.Revoked);
        (await store.InspectAsync(secondFamily, ClientA)).Outcome.Should().Be(RefreshTokenOutcome.Revoked);
        (await store.InspectAsync(otherAccount, ClientA)).Outcome.Should().Be(
            RefreshTokenOutcome.Succeeded,
            "another account's families are unaffected");
    }

    /// <summary>The store requires no database object of any kind to issue, rotate or revoke.</summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// The whole lifecycle is exercised against a store constructed with a clock and options and nothing
    /// else. There is no connection string, no context and no script to run first, which is precisely the
    /// property AAP rule T4 requires: nothing this type does can reach a schema it is forbidden to alter.
    /// </remarks>
    [Fact]
    public async Task TheWholeLifecycleRunsWithNoDatabaseDependency()
    {
        MutableClock clock = new(Origin);
        RefreshTokenStore store = Store(clock, slidingDays: 7, familyDays: 30);

        string issued = await IssueAsync(store, userId: 4_111);
        RefreshTokenRotationResult rotated = await store.RotateAsync(issued, ClientA);
        rotated.Outcome.Should().Be(RefreshTokenOutcome.Succeeded);

        (await store.RevokeAsync(rotated.RefreshToken!)).Should().Be(RefreshTokenOutcome.Succeeded);
        (await store.RevokeAsync(rotated.RefreshToken!)).Should().Be(RefreshTokenOutcome.AlreadyRevoked);
        (await store.RevokeAsync("never-issued")).Should().Be(RefreshTokenOutcome.Unknown);

        typeof(RefreshTokenStore)
            .GetConstructors()
            .SelectMany(constructor => constructor.GetParameters())
            .Select(parameter => parameter.ParameterType.Name)
            .Should()
            .BeEquivalentTo(
                [nameof(IClock), "IOptions`1", "IOptions`1"],
                "a database dependency here is what previously made login impossible on an unaltered schema");
    }

    /// <summary>
    /// The tracked-generation ceiling comes from configuration, and reaching it retires the oldest families
    /// rather than refusing to issue.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// <para>
    /// The ceiling used to be a compiled constant of one hundred thousand, which no test could reach without
    /// issuing one hundred thousand tokens - so the eviction path, and the fact that its cost is an early
    /// sign-out rather than a failed sign-in, were asserted nowhere. A configurable ceiling makes both
    /// reachable.
    /// </para>
    /// <para>
    /// Every family here has a distinct ceiling instant, because the clock advances a day between issues, so
    /// the eviction order is deterministic: the oldest family goes first.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task TheConfiguredCeilingRetiresTheOldestFamiliesRatherThanRefusingToIssue()
    {
        MutableClock clock = new(Origin);
        RefreshTokenStore store = Store(
            clock,
            slidingDays: 30,
            familyDays: 30,
            maximumTrackedTokens: 3);

        string first = await IssueAsync(store, userId: 4_120);
        clock.Advance(TimeSpan.FromDays(1));
        string second = await IssueAsync(store, userId: 4_121);
        clock.Advance(TimeSpan.FromDays(1));
        string third = await IssueAsync(store, userId: 4_122);

        store.DescribeCapacity().Should().Be(
            new RefreshTokenStore.CapacitySnapshot(3, 3),
            "three live generations exactly fill a ceiling of three");

        (await store.InspectAsync(first, ClientA)).Outcome.Should().Be(RefreshTokenOutcome.Succeeded);

        clock.Advance(TimeSpan.FromDays(1));
        string fourth = await IssueAsync(store, userId: 4_123);

        store.DescribeCapacity().Should().Be(
            new RefreshTokenStore.CapacitySnapshot(3, 3),
            "the ceiling is enforced on issue, so the tracked set never exceeds it");

        (await store.InspectAsync(fourth, ClientA)).Outcome.Should().Be(
            RefreshTokenOutcome.Succeeded,
            "issuing still succeeds at the ceiling - the cost of the bound is never a refused sign-in");
        (await store.InspectAsync(first, ClientA)).Outcome.Should().Be(
            RefreshTokenOutcome.Unknown,
            "the family nearest its own ceiling is the one retired, so its holder signs in again");
        (await store.InspectAsync(second, ClientA)).Outcome.Should().Be(
            RefreshTokenOutcome.Succeeded,
            "no family beyond the one needed to make room is disturbed");
        (await store.InspectAsync(third, ClientA)).Outcome.Should().Be(RefreshTokenOutcome.Succeeded);
    }

    /// <summary>
    /// Rotating at capacity keeps the tracked set within the configured ceiling and still returns a usable
    /// successor.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// <para>
    /// THE FACT THAT CATCHES THE HARD CASE. A rotation writes twice - the consumed generation and its
    /// replacement - and ordinarily only the second is an addition, because the first replaces a key already
    /// present. At capacity that stops being true: eviction may reclaim the presented generation itself, and
    /// then both writes add. Reserving one place would leave the tracked set one entry above the ceiling in
    /// exactly that case, which is a bound that is not a bound.
    /// </para>
    /// <para>
    /// Every family here is issued at a distinct instant, so the eviction order is deterministic and the
    /// presented family is the oldest - which is precisely the case that reclaims it.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task RotatingAtCapacityStaysWithinTheCeilingAndStillReturnsAUsableSuccessor()
    {
        MutableClock clock = new(Origin);
        RefreshTokenStore store = Store(
            clock,
            slidingDays: 30,
            familyDays: 30,
            maximumTrackedTokens: 3);

        string oldest = await IssueAsync(store, userId: 4_130);
        clock.Advance(TimeSpan.FromDays(1));
        await IssueAsync(store, userId: 4_131);
        clock.Advance(TimeSpan.FromDays(1));
        await IssueAsync(store, userId: 4_132);

        store.DescribeCapacity().TrackedGenerations.Should().Be(3, "the store starts this fact full");

        RefreshTokenRotationResult rotated = await store.RotateAsync(oldest, ClientA);
        rotated.Outcome.Should().Be(
            RefreshTokenOutcome.Succeeded,
            "capacity pressure must never turn a valid rotation into a refusal");

        store.DescribeCapacity().TrackedGenerations.Should().BeLessThanOrEqualTo(
            3,
            "the configured ceiling is a bound on the tracked set, including across a rotation's two writes");

        (await store.InspectAsync(rotated.RefreshToken!, ClientA)).Outcome.Should().Be(
            RefreshTokenOutcome.Succeeded,
            "the successor a rotation returned must be redeemable, so it can never be what was evicted");
    }

    /// <summary>A configured grace of zero makes any reuse of a spent generation a replay.</summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// The strictest setting, and the one a deployment that will not tolerate a forgiven reuse selects. The
    /// clock does not advance between the two exchanges, so the retry arrives at the same instant the first
    /// was spent - the single case a naive "elapsed is within the window" comparison would still forgive.
    /// </remarks>
    [Fact]
    public async Task AZeroGraceTreatsEvenAnImmediateSameClientRetryAsAReplay()
    {
        MutableClock clock = new(Origin);
        RefreshTokenStore store = Store(
            clock,
            slidingDays: 7,
            familyDays: 30,
            concurrentUseGraceSeconds: 0);

        string token = await IssueAsync(store, userId: 4_124);
        RefreshTokenRotationResult rotated = await store.RotateAsync(token, ClientA);
        rotated.Outcome.Should().Be(RefreshTokenOutcome.Succeeded);

        (await store.RotateAsync(token, ClientA)).Outcome.Should().Be(
            RefreshTokenOutcome.AlreadyUsed,
            "with the grace disabled there is no window in which a second presentation is a retry");

        (await store.InspectAsync(rotated.RefreshToken!, ClientA)).Outcome.Should().Be(
            RefreshTokenOutcome.Revoked,
            "detecting the replay retires the family, successor included");
    }

    /// <summary>A grace longer than the default is honoured for its whole configured length.</summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// The complement of the zero-grace fact: this one fails if the store still read a compiled five-second
    /// constant, because a retry twenty seconds after the exchange would then be theft.
    /// </remarks>
    [Fact]
    public async Task AGraceLongerThanTheDefaultIsHonouredForItsConfiguredLength()
    {
        MutableClock clock = new(Origin);
        RefreshTokenStore store = Store(
            clock,
            slidingDays: 7,
            familyDays: 30,
            concurrentUseGraceSeconds: 30);

        string token = await IssueAsync(store, userId: 4_125);
        (await store.RotateAsync(token, ClientA)).Outcome.Should().Be(RefreshTokenOutcome.Succeeded);

        clock.Advance(TimeSpan.FromSeconds(20));

        (await store.RotateAsync(token, ClientA)).Outcome.Should().Be(
            RefreshTokenOutcome.ConcurrentUse,
            "twenty seconds is inside a thirty-second window and outside the previous five-second constant");

        clock.Advance(TimeSpan.FromSeconds(11));

        (await store.RotateAsync(token, ClientA)).Outcome.Should().Be(
            RefreshTokenOutcome.AlreadyUsed,
            "past the configured window the same presentation is indistinguishable from theft");
    }

    /// <summary>
    /// The store refuses to be constructed on store settings nothing could work with, naming the section.
    /// </summary>
    /// <remarks>
    /// The store validates its own settings as well as the Api layer validating them on start, so a host
    /// composed without those validators still cannot build a store on an unusable capacity. Asserted through
    /// the constructor because that is the only point at which the values are read.
    /// </remarks>
    [Fact]
    public void UnusableStoreSettingsAreRefusedByTheConstructor()
    {
        MutableClock clock = new(Origin);

        Action construct = () => Store(
            clock,
            slidingDays: 7,
            familyDays: 30,
            maximumTrackedTokens: 0);

        construct.Should().Throw<OptionsValidationException>()
            .Which.Failures.Should().ContainMatch(
                $"*{RefreshTokenStoreOptions.SectionName}:*",
                "a refusal has to name the configuration path an operator must change");
    }

    /// <summary>An unrecognised provider name is refused by the constructor.</summary>
    /// <remarks>
    /// The store cannot judge whether a DECLARED external store was actually registered - that is a question
    /// about the container, settled by <c>ValidateRefreshTokenStoreTopology</c> - but it can and does refuse a
    /// name this build does not recognise, so a typo never reaches a running process.
    /// </remarks>
    [Fact]
    public void AnUnrecognisedProviderNameIsRefusedByTheConstructor()
    {
        Action construct = () => new RefreshTokenStore(
            new MutableClock(Origin),
            Options.Create(new JwtOptions
            {
                Secret = "unit-test-signing-secret-with-enough-entropy-0123456789",
                Issuer = "DnnMigration",
                Audience = "DnnMigration",
                ExpirationMinutes = 30,
                RefreshTokenExpirationDays = 7,
                RefreshTokenAbsoluteExpirationDays = 30,
            }),
            Options.Create(new RefreshTokenStoreOptions { Provider = "Redis" }));

        construct.Should().Throw<OptionsValidationException>()
            .Which.Failures.Should().ContainMatch("*does not recognise*");
    }

    /// <summary>Constructs a store with a controllable clock and an explicit shape.</summary>
    /// <param name="clock">The clock the test advances.</param>
    /// <param name="slidingDays">Per-generation sliding lifetime, in days.</param>
    /// <param name="familyDays">Family absolute ceiling, in days.</param>
    /// <param name="maximumTrackedTokens">
    /// Tracked-generation ceiling. Defaults to the shipped default so that every fact written before this
    /// setting existed still exercises the shipped behaviour.
    /// </param>
    /// <param name="concurrentUseGraceSeconds">
    /// Same-client grace, in seconds. Defaults to the shipped default for the same reason.
    /// </param>
    /// <returns>The store.</returns>
    /// <remarks>
    /// The store-shape values below deliberately bypass the API layer's operational floor of one thousand
    /// tracked generations, which a capacity fact could not otherwise reach in a unit test: that floor is HOST
    /// POLICY enforced by <c>RefreshTokenStoreOptionsValidator</c>, whereas the store's own contract - stated
    /// by <see cref="RefreshTokenStoreOptions.Validate"/> - is only that the ceiling is at least one. Testing
    /// the store against its own contract is correct; a deployment cannot configure these values, and the fact
    /// that it cannot is asserted in the options tests rather than here.
    /// </remarks>
    private static RefreshTokenStore Store(
        IClock clock,
        int slidingDays,
        int familyDays,
        int maximumTrackedTokens = 100_000,
        int concurrentUseGraceSeconds = 5) => new(
        clock,
        Options.Create(new JwtOptions
        {
            Secret = "unit-test-signing-secret-with-enough-entropy-0123456789",
            Issuer = "DnnMigration",
            Audience = "DnnMigration",
            ExpirationMinutes = 30,
            RefreshTokenExpirationDays = slidingDays,
            RefreshTokenAbsoluteExpirationDays = familyDays,
        }),
        Options.Create(new RefreshTokenStoreOptions
        {
            Provider = RefreshTokenStoreOptions.InProcessProvider,
            MaximumTrackedTokens = maximumTrackedTokens,
            ConcurrentUseGraceSeconds = concurrentUseGraceSeconds,
        }));

    private static async Task<string> IssueAsync(RefreshTokenStore store, int userId)
    {
        RefreshTokenIssueResult issued = await store.IssueAsync(new RefreshTokenSubject(userId, 0));
        issued.Outcome.Should().Be(RefreshTokenOutcome.Succeeded);
        return issued.RefreshToken!;
    }

    /// <summary>A clock the test advances explicitly, so no fact depends on wall-clock time passing.</summary>
    private sealed class MutableClock : IClock
    {
        private DateTime _utcNow;

        /// <summary>Initialises a new instance of the <see cref="MutableClock"/> class.</summary>
        /// <param name="utcNow">The instant the clock starts at.</param>
        public MutableClock(DateTime utcNow) => _utcNow = utcNow;

        /// <inheritdoc />
        public DateTime UtcNow => _utcNow;

        /// <summary>Moves the clock forward.</summary>
        /// <param name="by">How far forward to move it.</param>
        public void Advance(TimeSpan by) => _utcNow = _utcNow.Add(by);
    }
}
