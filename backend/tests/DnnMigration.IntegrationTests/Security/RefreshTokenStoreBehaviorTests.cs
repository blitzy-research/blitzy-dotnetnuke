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
    /// The family ceiling bounds the whole chain: a successor issued near the ceiling cannot be rotated
    /// past it, and its expiry never exceeds the ceiling.
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
    /// Two same-client exchanges inside the grace window yield one successor and one bounded refusal, and
    /// the same replay outside the window is treated as theft.
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
    /// A family whose ceiling has passed is discarded rather than retained, so the tracked set does not
    /// grow without bound.
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
    /// Every family here has a distinct ceiling instant, because the clock advances a day between issues,
    /// so the eviction order is deterministic: the oldest family goes first.
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
    /// THE FACT THAT CATCHES THE HARD CASE. A rotation writes twice - the consumed generation and its
    /// replacement - and ordinarily only the second is an addition, because the first replaces a key
    /// already present. At capacity that stops being true: eviction may reclaim the presented generation
    /// itself, and then both writes add.
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

    // PRIV-02 — ERASURE, AND RETENTION THAT DOES NOT DEPEND ON TRAFFIC
    // ⚠ EVERY FACT BELOW WAS UNREACHABLE BEFORE THE MEMBERS IT EXERCISES EXISTED, AND THAT IS THE FINDING.
    // The contract offered revocation and nothing else, so a record could be STAMPED and never REMOVED: an
    // account or a tenant deleted from the application went on being described here - the account, the
    // tenant and the token digest - until its family ceiling elapsed, and nothing on the contract could
    // remove the description.

    /// <summary>Erasing an account removes its records outright rather than stamping them.</summary>
    /// <remarks>
    /// The distinction between revocation and erasure, asserted where it is visible: after a REVOCATION the
    /// store still recognises the token - it answers that the family is revoked - whereas after an ERASURE
    /// it holds nothing and the same token is simply unknown.
    /// </remarks>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task ErasingAnAccountRemovesItsRecordsRatherThanStampingThem()
    {
        MutableClock clock = new(Origin);
        RefreshTokenStore store = Store(clock, slidingDays: 7, familyDays: 30);

        string token = await IssueAsync(store, userId: 41);

        (await store.RevokeAsync(token)).Should().Be(RefreshTokenOutcome.Succeeded);

        // Still HELD, and deliberately: a stamped record is what makes a replay of this family recognisable.
        RefreshTokenInspection stamped = await store.InspectAsync(token, ClientA);
        stamped.Outcome.Should().Be(
            RefreshTokenOutcome.Revoked,
            "revocation retains the record so the family stays recognisable");
        store.DescribeCapacity().TrackedGenerations.Should().Be(1);

        RefreshTokenPurgeResult purged = await store.PurgeSubjectAsync(RefreshTokenPurgeScope.ForAccount(41));

        purged.Outcome.Should().Be(RefreshTokenOutcome.Succeeded);
        purged.RemovedRecords.Should().Be(1);

        RefreshTokenInspection erased = await store.InspectAsync(token, ClientA);
        erased.Outcome.Should().Be(
            RefreshTokenOutcome.Unknown,
            "erasure removes the record, so there is nothing left to recognise");
        store.DescribeCapacity().TrackedGenerations.Should().Be(0);
    }

    /// <summary>An account-in-one-tenant erasure leaves that account's other tenants alone.</summary>
    /// <remarks>
    /// PRIV-02. THE SCOPE THAT MATTERS MOST. An account removed from one tenant may still be a member of
    /// another, and its sessions there are legitimate: erasing across every tenant because one membership
    /// was removed would sign it out of tenants it still belongs to.
    /// </remarks>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task ErasingAnAccountWithinOneTenantLeavesItsOtherTenantsAlone()
    {
        MutableClock clock = new(Origin);
        RefreshTokenStore store = Store(clock, slidingDays: 7, familyDays: 30);

        RefreshTokenIssueResult here = await store.IssueAsync(new RefreshTokenSubject(41, 7));
        RefreshTokenIssueResult elsewhere = await store.IssueAsync(new RefreshTokenSubject(41, 9));
        RefreshTokenIssueResult somebodyElse = await store.IssueAsync(new RefreshTokenSubject(42, 7));

        RefreshTokenPurgeResult purged = await store
            .PurgeSubjectAsync(RefreshTokenPurgeScope.ForAccountInPortal(41, 7));

        purged.RemovedRecords.Should().Be(1, "one account, one tenant, one record");

        (await store.InspectAsync(here.RefreshToken!, ClientA)).Outcome.Should().Be(RefreshTokenOutcome.Unknown);
        (await store.InspectAsync(elsewhere.RefreshToken!, ClientA)).Outcome.Should().Be(
            RefreshTokenOutcome.Succeeded,
            "the account still belongs to that tenant, and its session there was never in question");
        (await store.InspectAsync(somebodyElse.RefreshToken!, ClientA)).Outcome.Should().Be(
            RefreshTokenOutcome.Succeeded,
            "another subject's record is not this subject's to erase");
    }

    /// <summary>Erasing a tenant removes every account's records within it, and only within it.</summary>
    /// <remarks>
    /// PRIV-02. A tenant's records outlive its members: an account RETAINED because it belongs to another
    /// tenant still holds records scoped to the deleted one, and a per-account sweep over the accounts
    /// being deleted would leave exactly those behind.
    /// </remarks>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task ErasingATenantRemovesEveryAccountsRecordsWithinIt()
    {
        MutableClock clock = new(Origin);
        RefreshTokenStore store = Store(clock, slidingDays: 7, familyDays: 30);

        RefreshTokenIssueResult leaving = await store.IssueAsync(new RefreshTokenSubject(41, 7));
        RefreshTokenIssueResult retained = await store.IssueAsync(new RefreshTokenSubject(42, 7));
        RefreshTokenIssueResult otherTenant = await store.IssueAsync(new RefreshTokenSubject(42, 9));

        RefreshTokenPurgeResult purged = await store.PurgeSubjectAsync(RefreshTokenPurgeScope.ForPortal(7));

        purged.RemovedRecords.Should().Be(2);

        (await store.InspectAsync(leaving.RefreshToken!, ClientA)).Outcome.Should().Be(RefreshTokenOutcome.Unknown);
        (await store.InspectAsync(retained.RefreshToken!, ClientA)).Outcome.Should().Be(
            RefreshTokenOutcome.Unknown,
            "the account survives the tenant, but its session IN that tenant does not");
        (await store.InspectAsync(otherTenant.RefreshToken!, ClientA)).Outcome.Should().Be(
            RefreshTokenOutcome.Succeeded,
            "the same account's session in a surviving tenant is untouched");
    }

    /// <summary>Erasing a subject the store never held is a completed erasure, not a failure.</summary>
    /// <remarks>
    /// Idempotence, and it is what lets a deletion path call this unconditionally. Most members of a tenant
    /// have never signed in on any given instance, so "no such record" is the ordinary answer rather than
    /// an exceptional one - and a store that holds nothing about a subject genuinely holds no personal data
    /// about it, which is the whole claim the erasure makes.
    /// </remarks>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task ErasingASubjectTheStoreNeverHeldIsACompletedErasure()
    {
        MutableClock clock = new(Origin);
        RefreshTokenStore store = Store(clock, slidingDays: 7, familyDays: 30);

        RefreshTokenPurgeResult purged = await store.PurgeSubjectAsync(RefreshTokenPurgeScope.ForAccount(999));

        purged.Outcome.Should().Be(RefreshTokenOutcome.Unknown, "nothing matched");
        purged.RemovedRecords.Should().Be(0);
        purged.Answered.Should().BeTrue("the store answered, which is what a caller acts on");
    }

    /// <summary>A revoked record is reclaimed once its retention window has elapsed, and not before.</summary>
    /// <remarks>
    /// PRIV-02. THE DOCUMENTED MINIMUM PERIOD, MEASURED AT BOTH ENDS. Before this window existed the only
    /// thing that reclaimed a revoked record was its family's absolute ceiling, so an ordinary sign-out
    /// left the account, the tenant and the token digest in the store for the remainder of the refresh
    /// lifetime - thirty days, in this fact's configuration.
    /// </remarks>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task ARevokedRecordSurvivesItsRetentionWindowAndNoLonger()
    {
        MutableClock clock = new(Origin);
        RefreshTokenStore store = Store(clock, slidingDays: 7, familyDays: 30, revokedRetentionHours: 6);

        string token = await IssueAsync(store, userId: 41);
        (await store.RevokeAsync(token)).Should().Be(RefreshTokenOutcome.Succeeded);

        clock.Advance(TimeSpan.FromHours(6) - TimeSpan.FromSeconds(1));

        (await store.PurgeRetiredAsync()).RemovedRecords.Should().Be(
            0,
            "one second inside the window, the replay signal is still worth keeping");
        (await store.InspectAsync(token, ClientA)).Outcome.Should().Be(RefreshTokenOutcome.Revoked);

        clock.Advance(TimeSpan.FromSeconds(1));

        (await store.PurgeRetiredAsync()).RemovedRecords.Should().Be(
            1,
            "at the boundary the window has elapsed and the record is only personal data");
        (await store.InspectAsync(token, ClientA)).Outcome.Should().Be(
            RefreshTokenOutcome.Unknown,
            "and it is gone rather than merely unreadable");
    }

    /// <summary>Reclamation never removes a record that is still redeemable.</summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task ReclamationLeavesALiveSessionAlone()
    {
        MutableClock clock = new(Origin);
        RefreshTokenStore store = Store(clock, slidingDays: 7, familyDays: 30, revokedRetentionHours: 1);

        string live = await IssueAsync(store, userId: 41);

        clock.Advance(TimeSpan.FromDays(2));

        (await store.PurgeRetiredAsync()).RemovedRecords.Should().Be(0);
        (await store.InspectAsync(live, ClientA)).Outcome.Should().Be(
            RefreshTokenOutcome.Succeeded,
            "a redeemable record is not retired state, whatever the revoked-record window says");
    }

    /// <summary>Reclamation still removes a family past its absolute ceiling.</summary>
    /// <remarks>
    /// The pre-existing ground for removal, asserted through the new entry point so that adding the second
    /// ground cannot quietly have replaced the first. An expired family can never be redeemed and is no
    /// longer a theft signal either.
    /// </remarks>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task ReclamationRemovesAFamilyPastItsCeilingWithoutWaitingForTraffic()
    {
        MutableClock clock = new(Origin);
        RefreshTokenStore store = Store(clock, slidingDays: 7, familyDays: 30, revokedRetentionHours: 720);

        string token = await IssueAsync(store, userId: 41);

        clock.Advance(TimeSpan.FromDays(31));

        (await store.PurgeRetiredAsync()).RemovedRecords.Should().Be(1);
        store.DescribeCapacity().TrackedGenerations.Should().Be(
            0,
            "reclamation no longer waits for somebody to sign in");
    }

    /// <summary>Constructs a store with a controllable clock and an explicit shape.</summary>
    /// <param name="clock">The clock the test advances.</param>
    /// <param name="slidingDays">Per-generation sliding lifetime, in days.</param>
    /// <param name="familyDays">Family absolute ceiling, in days.</param>
    /// <param name="maximumTrackedTokens">Tracked-generation ceiling.</param>
    /// <param name="concurrentUseGraceSeconds">Same-client grace, in seconds.</param>
    /// <param name="revokedRetentionHours">
    /// How long a revoked record is retained before reclamation erases it, in hours.
    /// </param>
    /// <returns>The store.</returns>
    /// <remarks>
    /// The store-shape values below deliberately bypass the API layer's operational floor of one thousand
    /// tracked generations, which a capacity fact could not otherwise reach in a unit test: that floor is
    /// HOST POLICY enforced by <c>RefreshTokenStoreOptionsValidator</c>, whereas the store's own contract -
    /// stated by <see cref="RefreshTokenStoreOptions.Validate"/> - is only that the ceiling is at least
    /// one.
    /// </remarks>
    private static RefreshTokenStore Store(
        IClock clock,
        int slidingDays,
        int familyDays,
        int maximumTrackedTokens = 100_000,
        int concurrentUseGraceSeconds = 5,
        int revokedRetentionHours = 24) => new(
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
            RevokedRecordRetentionHours = revokedRetentionHours,
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
