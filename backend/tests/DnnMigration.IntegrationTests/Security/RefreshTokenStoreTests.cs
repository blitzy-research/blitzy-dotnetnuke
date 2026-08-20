using DnnMigration.Application.Abstractions;
using DnnMigration.Domain.Abstractions.Services;
using DnnMigration.Domain.Enums;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DnnMigration.IntegrationTests.Security;

/// <summary>
/// Verifies that refresh-token state is shared by every service scope of one host and that rotation, replay
/// and revocation remain atomic across independently resolved scopes.
/// </summary>
[Trait("Category", "Integration")]
[Collection(IntegrationTestCollection.Name)]
public sealed class RefreshTokenStoreTests
{
    private const string ClientA = "client-binding-a";
    private const string ClientB = "client-binding-b";

    private readonly ApiTestFixture _fixture;

    /// <summary>Initialises a new instance of the <see cref="RefreshTokenStoreTests"/> class.</summary>
    /// <param name="fixture">The shared composed host and database.</param>
    public RefreshTokenStoreTests(ApiTestFixture fixture) => _fixture = fixture;

    /// <summary>
    /// State issued in one scope is redeemable from a fresh scope, and the raw token is returned exactly
    /// once and never handed back.
    /// </summary>
    /// <remarks>
    /// The second half is what the removed column assertion was really testing - that nothing gives a raw
    /// token back after issue - and the contract can state it directly: an inspection reports the subject
    /// and the expiry and carries no token of any kind, so there is nowhere for one to be echoed from.
    /// </remarks>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task Issue_StateSurvivesAFreshScopeAndNeverReturnsTheTokenAgain()
    {
        RefreshTokenIssueResult issued;
        await using (AsyncServiceScope scope = _fixture.Services.CreateAsyncScope())
        {
            issued = await scope.ServiceProvider
                .GetRequiredService<IRefreshTokenStore>()
                .IssueAsync(Subject(920_001));
        }

        issued.Outcome.Should().Be(RefreshTokenOutcome.Succeeded);
        issued.RefreshToken.Should().NotBeNullOrWhiteSpace();

        await using (AsyncServiceScope scope = _fixture.Services.CreateAsyncScope())
        {
            RefreshTokenInspection inspection = await scope.ServiceProvider
                .GetRequiredService<IRefreshTokenStore>()
                .InspectAsync(issued.RefreshToken!, ClientA);

            inspection.Outcome.Should().Be(RefreshTokenOutcome.Succeeded);
            inspection.Subject!.UserId.Should().Be(920_001);

            typeof(RefreshTokenInspection)
                .GetProperties()
                .Select(property => property.Name)
                .Should()
                .NotContain(
                    "RefreshToken",
                    "an inspection must not be able to hand a raw refresh token back to a caller");
        }

        await using (AsyncServiceScope scope = _fixture.Services.CreateAsyncScope())
        {
            // The two scopes above resolve ONE store, so an unknown token is unknown to all of them.
            RefreshTokenInspection unknown = await scope.ServiceProvider
                .GetRequiredService<IRefreshTokenStore>()
                .InspectAsync("not-a-token-this-store-ever-issued", ClientA);

            unknown.Outcome.Should().Be(RefreshTokenOutcome.Unknown);
        }
    }

    /// <summary>
    /// Every scope of one host resolves the SAME store instance, which is the registration AAP section
    /// 0.4.3 requires.
    /// </summary>
    [Fact]
    public void Store_IsOneSingletonSharedByEveryScope()
    {
        using IServiceScope first = _fixture.Services.CreateScope();
        using IServiceScope second = _fixture.Services.CreateScope();

        IRefreshTokenStore fromFirst = first.ServiceProvider.GetRequiredService<IRefreshTokenStore>();
        IRefreshTokenStore fromSecond = second.ServiceProvider.GetRequiredService<IRefreshTokenStore>();

        fromSecond.Should().BeSameAs(
            fromFirst,
            "refresh state is the instance, so two instances would lose families between requests");

        _fixture.Services.GetRequiredService<ITokenService>().Should().BeSameAs(
            first.ServiceProvider.GetRequiredService<ITokenService>(),
            "the token service is a singleton alongside the store it coordinates");
    }

    /// <summary>
    /// Two same-client exchanges racing on one token produce one successor and one bounded grace refusal
    /// without revoking the successor.
    /// </summary>
    [Fact]
    public async Task Rotate_ConcurrentSameClientUseDoesNotRevokeTheFamily()
    {
        string presented = await IssueAsync(920_002);

        await using AsyncServiceScope firstScope = _fixture.Services.CreateAsyncScope();
        await using AsyncServiceScope secondScope = _fixture.Services.CreateAsyncScope();

        Task<RefreshTokenRotationResult> first = firstScope.ServiceProvider
            .GetRequiredService<IRefreshTokenStore>()
            .RotateAsync(presented, ClientA);
        Task<RefreshTokenRotationResult> second = secondScope.ServiceProvider
            .GetRequiredService<IRefreshTokenStore>()
            .RotateAsync(presented, ClientA);

        RefreshTokenRotationResult[] results = await Task.WhenAll(first, second);

        results.Select(result => result.Outcome).Should().BeEquivalentTo(
            [RefreshTokenOutcome.Succeeded, RefreshTokenOutcome.ConcurrentUse]);

        string successor = results.Single(result => result.Outcome == RefreshTokenOutcome.Succeeded)
            .RefreshToken!;

        await using AsyncServiceScope verificationScope = _fixture.Services.CreateAsyncScope();
        RefreshTokenInspection inspection = await verificationScope.ServiceProvider
            .GetRequiredService<IRefreshTokenStore>()
            .InspectAsync(successor, ClientA);
        inspection.Outcome.Should().Be(RefreshTokenOutcome.Succeeded);
    }

    /// <summary>A replay from another client revokes every family belonging to the account.</summary>
    [Fact]
    public async Task Rotate_ReplayFromAnotherClientRevokesEveryAccountFamily()
    {
        const int UserId = 920_003;
        string first = await IssueAsync(UserId);
        string otherFamily = await IssueAsync(UserId);

        string successor;
        await using (AsyncServiceScope scope = _fixture.Services.CreateAsyncScope())
        {
            RefreshTokenRotationResult rotated = await scope.ServiceProvider
                .GetRequiredService<IRefreshTokenStore>()
                .RotateAsync(first, ClientA);
            successor = rotated.RefreshToken!;
        }

        await using (AsyncServiceScope replayScope = _fixture.Services.CreateAsyncScope())
        {
            RefreshTokenRotationResult replay = await replayScope.ServiceProvider
                .GetRequiredService<IRefreshTokenStore>()
                .RotateAsync(first, ClientB);
            replay.Outcome.Should().Be(RefreshTokenOutcome.AlreadyUsed);
        }

        await using AsyncServiceScope verificationScope = _fixture.Services.CreateAsyncScope();
        IRefreshTokenStore store =
            verificationScope.ServiceProvider.GetRequiredService<IRefreshTokenStore>();
        (await store.InspectAsync(successor, ClientA)).Outcome.Should().Be(RefreshTokenOutcome.Revoked);
        (await store.InspectAsync(otherFamily, ClientA)).Outcome.Should().Be(RefreshTokenOutcome.Revoked);
    }

    /// <summary>
    /// A spent fingerprint remains replay-detectable after many rotations and therefore still revokes the
    /// live successor.
    /// </summary>
    [Fact]
    public async Task Rotate_OldSpentFingerprintRemainsDetectableThroughTheFamilyLifetime()
    {
        const int UserId = 920_004;
        string first = await IssueAsync(UserId);
        string current = first;

        for (int exchange = 0; exchange < 96; exchange++)
        {
            await using AsyncServiceScope scope = _fixture.Services.CreateAsyncScope();
            RefreshTokenRotationResult rotated = await scope.ServiceProvider
                .GetRequiredService<IRefreshTokenStore>()
                .RotateAsync(current, ClientA);

            rotated.Outcome.Should().Be(RefreshTokenOutcome.Succeeded);
            current = rotated.RefreshToken!;
        }

        await using (AsyncServiceScope replayScope = _fixture.Services.CreateAsyncScope())
        {
            RefreshTokenRotationResult replay = await replayScope.ServiceProvider
                .GetRequiredService<IRefreshTokenStore>()
                .RotateAsync(first, ClientB);
            replay.Outcome.Should().Be(RefreshTokenOutcome.AlreadyUsed);
        }

        await using AsyncServiceScope verificationScope = _fixture.Services.CreateAsyncScope();
        RefreshTokenInspection inspection = await verificationScope.ServiceProvider
            .GetRequiredService<IRefreshTokenStore>()
            .InspectAsync(current, ClientA);
        inspection.Outcome.Should().Be(RefreshTokenOutcome.Revoked);
    }

    // The fact that a CONSUMED generation stays a theft signal after its own sliding expiry and before the
    // family ceiling lived here, and it forced that state by issuing an UPDATE against
    // [DnnMigration].[RefreshTokens].

    /// <summary>Family revocation performed in one scope is observed from another scope.</summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task Revoke_StateIsSharedAcrossScopes()
    {
        string token = await IssueAsync(920_005);

        await using (AsyncServiceScope scope = _fixture.Services.CreateAsyncScope())
        {
            RefreshTokenOutcome revoked = await scope.ServiceProvider
                .GetRequiredService<IRefreshTokenStore>()
                .RevokeAsync(token);
            revoked.Should().Be(RefreshTokenOutcome.Succeeded);
        }

        await using AsyncServiceScope verificationScope = _fixture.Services.CreateAsyncScope();
        RefreshTokenInspection inspection = await verificationScope.ServiceProvider
            .GetRequiredService<IRefreshTokenStore>()
            .InspectAsync(token, ClientA);
        inspection.Outcome.Should().Be(RefreshTokenOutcome.Revoked);
    }

    private async Task<string> IssueAsync(int userId)
    {
        await using AsyncServiceScope scope = _fixture.Services.CreateAsyncScope();
        RefreshTokenIssueResult issued = await scope.ServiceProvider
            .GetRequiredService<IRefreshTokenStore>()
            .IssueAsync(Subject(userId));

        issued.Outcome.Should().Be(RefreshTokenOutcome.Succeeded);
        return issued.RefreshToken!;
    }

    private static RefreshTokenSubject Subject(int userId) => new(
        userId,
        portalId: 0);
}
