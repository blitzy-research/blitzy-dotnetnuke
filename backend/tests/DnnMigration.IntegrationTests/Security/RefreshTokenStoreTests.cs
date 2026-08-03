using DnnMigration.Domain.Abstractions.Services;
using DnnMigration.Domain.Enums;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DnnMigration.IntegrationTests.Security;

/// <summary>
/// Exercises the registered refresh-token store through the public domain contract, driving rotation
/// directly rather than through the sign-in endpoints.
/// </summary>
/// <remarks>
/// <para>
/// The implementation is internal to the infrastructure assembly and the unit-test project deliberately
/// references only the application assembly, so the store is reached the way the rest of the application
/// reaches it: resolved from the composed container behind <see cref="IRefreshTokenStore"/>. That also makes
/// this the correct level for these assertions, because what is under test is the store's own bookkeeping
/// rather than any endpoint's behaviour.
/// </para>
/// <para>
/// Rotation is driven in-process rather than over HTTP for two reasons: the refresh endpoint is rate limited
/// to a few dozen requests per minute, and the retention bound only becomes observable after more exchanges
/// than that window allows. Driving the contract directly costs no database work and no HTTP round trips, so
/// a test that needs a hundred exchanges runs in milliseconds.
/// </para>
/// <para>
/// Each test uses an account identifier of its own so that families never overlap, which matters because the
/// store is a singleton shared with every other test in the collection.
/// </para>
/// </remarks>
[Trait("Category", "Integration")]
[Collection(IntegrationTestCollection.Name)]
public sealed class RefreshTokenStoreTests
{
    /// <summary>
    /// The retention bound the store applies per family. Restated here as the test's own expectation rather
    /// than read from the implementation, so that changing the implementation's constant without considering
    /// the consequence fails this suite instead of silently redefining what it proves.
    /// </summary>
    private const int RetainedSpentGenerations = 64;

    private readonly ApiTestFixture _fixture;

    /// <summary>Initialises a new instance of the <see cref="RefreshTokenStoreTests"/> class.</summary>
    /// <param name="fixture">The shared composed host.</param>
    public RefreshTokenStoreTests(ApiTestFixture fixture) => _fixture = fixture;

    /// <summary>
    /// A family rotated far past the retention bound still rotates, and the generation it is holding is
    /// still redeemable.
    /// </summary>
    /// <remarks>
    /// The first half of the bound's contract: bounding retention must not cost a live session anything.
    /// Rotation never refuses on capacity, and trimming spent generations must not reach the one generation
    /// that has not been spent.
    /// </remarks>
    [Fact]
    public void Rotate_FarBeyondTheRetentionBound_KeepsTheSessionAlive()
    {
        IRefreshTokenStore store = Store();
        RefreshTokenSubject subject = Subject(userId: 910_001);

        RefreshTokenIssueResult issued = store.Issue(subject);

        issued.Outcome.Should().Be(RefreshTokenOutcome.Succeeded);

        string current = issued.RefreshToken!;

        for (int exchange = 0; exchange < (RetainedSpentGenerations * 2) + 5; exchange++)
        {
            RefreshTokenRotationResult rotated = store.Rotate(current, subject);

            rotated.Outcome.Should().Be(
                RefreshTokenOutcome.Succeeded,
                "rotation must never refuse a live session, whatever the family's history costs to retain");

            current = rotated.RefreshToken!;
        }

        store.Inspect(current).Outcome.Should().Be(RefreshTokenOutcome.Succeeded);
    }

    /// <summary>
    /// A generation spent recently is still recognised as a replay, and replaying it still ends every
    /// session the account holds.
    /// </summary>
    /// <remarks>
    /// The second half of the contract, and the property the retention bound is chosen to preserve. A copied
    /// token is realistically replayed while it is still recent, so recent history is what must stay
    /// detectable; the bound is sized in generations precisely so that this window is wide.
    /// </remarks>
    [Fact]
    public void Rotate_ReplayingARecentlySpentGeneration_StillEndsEverySession()
    {
        IRefreshTokenStore store = Store();
        RefreshTokenSubject subject = Subject(userId: 910_002);

        string first = store.Issue(subject).RefreshToken!;
        string second = store.Rotate(first, subject).RefreshToken!;
        string third = store.Rotate(second, subject).RefreshToken!;

        // The immediately preceding generation, presented again.
        RefreshTokenRotationResult replay = store.Rotate(second, subject);

        replay.Outcome.Should().Be(RefreshTokenOutcome.AlreadyUsed);
        store.Inspect(third).Outcome.Should().NotBe(
            RefreshTokenOutcome.Succeeded,
            "a replay says a copy exists, so the family the legitimate caller is holding ends too");
    }

    /// <summary>
    /// A generation spent long enough ago to have been forgotten is refused, and the refusal is the
    /// unrecognised one rather than the replay one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This test exists to state the retention bound's cost in executable form rather than only in prose,
    /// because it is a deliberate reduction and a reader is entitled to see exactly how far it goes. Once a
    /// spent generation has been forgotten, presenting it again is indistinguishable from presenting a value
    /// that never existed: it is still <b>refused</b>, so nothing is gained by presenting it, but it no
    /// longer escalates to family-wide revocation, so the leak signal is lost for that value.
    /// </para>
    /// <para>
    /// The accepted trade is recorded on the store's retention constant and in the migration notes: the
    /// alternative was a store that rotation could grow without limit until it refused every new sign-in in
    /// the installation.
    /// </para>
    /// </remarks>
    [Fact]
    public void Rotate_ReplayingAForgottenGeneration_IsRefusedWithoutEndingTheSession()
    {
        IRefreshTokenStore store = Store();
        RefreshTokenSubject subject = Subject(userId: 910_003);

        string first = store.Issue(subject).RefreshToken!;
        string current = first;

        // Comfortably past the bound, so the very first generation is certain to have been trimmed.
        for (int exchange = 0; exchange < RetainedSpentGenerations + 10; exchange++)
        {
            current = store.Rotate(current, subject).RefreshToken!;
        }

        store.Inspect(first).Outcome.Should().Be(
            RefreshTokenOutcome.Unknown,
            "the oldest spent generations are forgotten first, so this one is no longer held at all");

        RefreshTokenRotationResult replay = store.Rotate(first, subject);

        replay.Outcome.Should().Be(RefreshTokenOutcome.Unknown);

        // THE POINT OF THE TEST. The forgotten value buys its presenter nothing - it was refused - and the
        // legitimate session continues, because an unrecognised value is not evidence of a copy.
        store.Inspect(current).Outcome.Should().Be(RefreshTokenOutcome.Succeeded);
    }

    /// <summary>
    /// Rotating one family does not disturb another family belonging to the same account.
    /// </summary>
    /// <remarks>
    /// Trimming walks one family's generations and removes entries from the store's own index, so this pins
    /// that the walk cannot reach beyond the family it was asked about. A defect here would silently end a
    /// user's other sessions whenever one of them rotated enough times.
    /// </remarks>
    [Fact]
    public void Rotate_TrimmingOneFamily_LeavesTheAccountsOtherFamilyIntact()
    {
        IRefreshTokenStore store = Store();
        RefreshTokenSubject subject = Subject(userId: 910_004);

        string untouched = store.Issue(subject).RefreshToken!;
        string busy = store.Issue(subject).RefreshToken!;

        for (int exchange = 0; exchange < RetainedSpentGenerations + 10; exchange++)
        {
            busy = store.Rotate(busy, subject).RefreshToken!;
        }

        store.Inspect(untouched).Outcome.Should().Be(
            RefreshTokenOutcome.Succeeded,
            "a second session opened from another device is not affected by how often the first rotates");
        store.Inspect(busy).Outcome.Should().Be(RefreshTokenOutcome.Succeeded);
    }

    /// <summary>
    /// Account-wide revocation still reaches a family that has been trimmed.
    /// </summary>
    /// <remarks>
    /// Trimming removes generations from a family without removing the family, so the per-account index the
    /// revocation walks must still name it. Were trimming to leave that index inconsistent, an administrative
    /// revocation would silently miss the longest-lived session on the account - which is the one it most
    /// needs to reach.
    /// </remarks>
    [Fact]
    public void RevokeAllForUser_AfterTrimming_StillEndsTheTrimmedFamily()
    {
        IRefreshTokenStore store = Store();
        RefreshTokenSubject subject = Subject(userId: 910_005);

        string current = store.Issue(subject).RefreshToken!;

        for (int exchange = 0; exchange < RetainedSpentGenerations + 10; exchange++)
        {
            current = store.Rotate(current, subject).RefreshToken!;
        }

        store.RevokeAllForUser(subject.UserId).Should().Be(RefreshTokenOutcome.Succeeded);
        store.Inspect(current).Outcome.Should().NotBe(RefreshTokenOutcome.Succeeded);
        store.Rotate(current, subject).Outcome.Should().NotBe(RefreshTokenOutcome.Succeeded);
    }

    /// <summary>Resolves the registered store from the composed container.</summary>
    /// <returns>The application's own refresh-token store.</returns>
    private IRefreshTokenStore Store() => _fixture.Services.GetRequiredService<IRefreshTokenStore>();

    /// <summary>Builds a snapshot for an account identifier this suite owns.</summary>
    /// <param name="userId">The account identifier, unique to one test.</param>
    /// <returns>The snapshot to record against the family.</returns>
    private static RefreshTokenSubject Subject(int userId) => new(
        userId,
        portalId: 0,
        userName: "store_" + userId.ToString(System.Globalization.CultureInfo.InvariantCulture),
        isSuperUser: false,
        roles: new[] { "Registered Users" },
        permissionKeys: new[] { "VIEW" });
}
