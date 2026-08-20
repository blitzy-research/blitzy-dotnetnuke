using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using DnnMigration.Application.Abstractions;
using DnnMigration.Application.Dtos.Auth;
using DnnMigration.Application.Options;
using DnnMigration.Domain.Abstractions.Services;
using DnnMigration.Domain.Common;
using DnnMigration.Domain.Enums;
using DnnMigration.Infrastructure.Security;
using FluentAssertions;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Moq;
using Xunit;

namespace DnnMigration.IntegrationTests.Security;

/// <summary>Exercises the concrete minimal JWT implementation over a substituted durable refresh store.</summary>
[Trait("Category", "Integration")]
public sealed class JwtTokenServiceBehaviorTests
{
    private const int UserId = 7;
    private const int PortalId = -1;
    private const string RefreshToken = "refresh-token-placeholder";
    private const string ReplacementRefreshToken = "replacement-refresh-token-placeholder";
    private const string ClientBinding = "client-binding-placeholder";
    private const string Issuer = "DnnMigration.Tests";
    private const string Audience = "DnnMigration.Tests.Client";
    private const string SigningSecret =
        "unit-test-signing-secret-with-more-than-thirty-two-distinct-safe-bytes";

    private static readonly DateTime Now = new(2026, 8, 4, 12, 0, 0, DateTimeKind.Utc);

    /// <summary>The durable store collaborator is required.</summary>
    [Fact]
    public void Constructor_RequiresRefreshStore()
    {
        Action construct = () => new JwtTokenService(
            null!,
            new Mock<IClock>().Object,
            Options.Create(ValidOptions()));

        construct.Should().Throw<ArgumentNullException>();
    }

    /// <summary>The clock collaborator is required.</summary>
    [Fact]
    public void Constructor_RequiresClock()
    {
        Action construct = () => new JwtTokenService(
            new Mock<IRefreshTokenStore>().Object,
            null!,
            Options.Create(ValidOptions()));

        construct.Should().Throw<ArgumentNullException>();
    }

    /// <summary>The options wrapper is required.</summary>
    [Fact]
    public void Constructor_RequiresOptions()
    {
        Action construct = () => new JwtTokenService(
            new Mock<IRefreshTokenStore>().Object,
            new Mock<IClock>().Object,
            null!);

        construct.Should().Throw<ArgumentNullException>();
    }

    /// <summary>Invalid token settings fail construction rather than first use.</summary>
    [Fact]
    public void Constructor_RejectsInvalidOptions()
    {
        JwtOptions options = ValidOptions();
        options.Secret = "too-short";

        Action construct = () => new JwtTokenService(
            new Mock<IRefreshTokenStore>().Object,
            new Mock<IClock>().Object,
            Options.Create(options));

        construct.Should().Throw<OptionsValidationException>();
    }

    /// <summary>Issue delegates the exact account and tenant identifiers to durable state.</summary>
    [Fact]
    public async Task Issue_PersistsTheExactSubject()
    {
        Harness harness = Harness.Ready();
        harness.Store
            .Setup(store => store.IssueAsync(
                It.Is<RefreshTokenSubject>(subject =>
                    subject.UserId == UserId && subject.PortalId == PortalId),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(Issued(UserId, PortalId));

        Result<LoginResponse> result = await harness.Service.IssueTokensAsync(UserId, PortalId);

        result.IsSuccess.Should().BeTrue();
        harness.Store.VerifyAll();
    }

    /// <summary>Schema-valid zero and negative identifiers remain real identities.</summary>
    [Theory]
    [InlineData(0, -1)]
    [InlineData(-1, 0)]
    public async Task Issue_PreservesSchemaIdentifiers(int userId, int portalId)
    {
        Harness harness = Harness.Ready();
        harness.Store
            .Setup(store => store.IssueAsync(
                It.Is<RefreshTokenSubject>(subject =>
                    subject.UserId == userId && subject.PortalId == portalId),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(Issued(userId, portalId));

        Result<LoginResponse> result = await harness.Service.IssueTokensAsync(userId, portalId);

        result.IsSuccess.Should().BeTrue();
        result.Value.User.UserId.Should().Be(userId);
        result.Value.User.PortalId.Should().Be(portalId);
    }

    /// <summary>A store outage yields no token pair and retains the stable dependency code.</summary>
    [Fact]
    public async Task Issue_WhenStoreIsUnavailable_ReturnsNoPair()
    {
        Harness harness = Harness.Ready();
        harness.Store
            .Setup(store => store.IssueAsync(It.IsAny<RefreshTokenSubject>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(RefreshTokenIssueResult.Failed(RefreshTokenOutcome.StoreUnavailable));

        Result<LoginResponse> result = await harness.Service.IssueTokensAsync(UserId, PortalId);

        result.IsFailure.Should().BeTrue();
        result.Reason!.Code.Should().Be("TOKEN_STORE_UNAVAILABLE");
    }

    /// <summary>A capacity refusal is reported as store unavailability rather than a credential fault.</summary>
    [Fact]
    public async Task Issue_WhenStoreCapacityIsExhausted_ReturnsStoreUnavailable()
    {
        Harness harness = Harness.Ready();
        harness.Store
            .Setup(store => store.IssueAsync(It.IsAny<RefreshTokenSubject>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(RefreshTokenIssueResult.Failed(RefreshTokenOutcome.CapacityExhausted));

        Result<LoginResponse> result = await harness.Service.IssueTokensAsync(UserId, PortalId);

        result.IsFailure.Should().BeTrue();
        result.Reason!.Code.Should().Be("TOKEN_STORE_UNAVAILABLE");
    }

    /// <summary>Cancellation before issue reaches neither the store nor token construction.</summary>
    [Fact]
    public async Task Issue_HonoursPreCancelledRequest()
    {
        Harness harness = Harness.Ready();
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();

        Func<Task> issue = () => harness.Service.IssueTokensAsync(
            UserId,
            PortalId,
            cancellation.Token);

        await issue.Should().ThrowAsync<OperationCanceledException>();
        harness.Store.Verify(
            store => store.IssueAsync(
                It.IsAny<RefreshTokenSubject>(),
                It.IsAny<CancellationToken>()),
            Times.Never());
    }

    /// <summary>The issued access token validates under the configured key, issuer and audience.</summary>
    [Fact]
    public async Task Issue_ProducesAValidSignedAccessToken()
    {
        Harness harness = Harness.WithSuccessfulIssue();

        Result<LoginResponse> result = await harness.Service.IssueTokensAsync(UserId, PortalId);

        JwtSecurityTokenHandler handler = new() { MapInboundClaims = false };
        ClaimsPrincipal principal = handler.ValidateToken(
            result.Value.AccessToken,
            ValidationParameters(),
            out SecurityToken validated);

        validated.Should().BeOfType<JwtSecurityToken>();
        principal.FindFirst(DnnClaimTypes.Subject)!.Value.Should().Be(UserId.ToString());
        principal.FindFirst(DnnClaimTypes.PortalId)!.Value.Should().Be(PortalId.ToString());
    }

    /// <summary>The custom claim set is exactly subject, tenant and token identifier.</summary>
    [Fact]
    public async Task Issue_EmitsOnlyMinimalCustomClaims()
    {
        Harness harness = Harness.WithSuccessfulIssue();

        Result<LoginResponse> result = await harness.Service.IssueTokensAsync(UserId, PortalId);

        JwtSecurityToken token = Read(result.Value.AccessToken);
        HashSet<string> envelope =
        [
            JwtRegisteredClaimNames.Iss,
            JwtRegisteredClaimNames.Aud,
            JwtRegisteredClaimNames.Exp,
            JwtRegisteredClaimNames.Nbf,
            JwtRegisteredClaimNames.Iat,
        ];

        token.Claims
            .Where(claim => !envelope.Contains(claim.Type))
            .Select(claim => claim.Type)
            .Should().BeEquivalentTo(
                [DnnClaimTypes.Subject, DnnClaimTypes.PortalId, DnnClaimTypes.JwtId]);
    }

    /// <summary>Names, host status, roles and permissions cannot re-enter through emitted claims.</summary>
    [Fact]
    public async Task Issue_EmitsNoMutableAuthorityOrProfileClaim()
    {
        Harness harness = Harness.WithSuccessfulIssue();

        Result<LoginResponse> result = await harness.Service.IssueTokensAsync(UserId, PortalId);

        string[] claimTypes = Read(result.Value.AccessToken)
            .Claims
            .Select(claim => claim.Type)
            .ToArray();
        claimTypes.Should().NotContain(type =>
            type.Contains("name", StringComparison.OrdinalIgnoreCase)
            || type.Contains("role", StringComparison.OrdinalIgnoreCase)
            || type.Contains("permission", StringComparison.OrdinalIgnoreCase)
            || type.Contains("super", StringComparison.OrdinalIgnoreCase)
            || type.Contains("email", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The response projection is authority-minimised as well as the access token.</summary>
    [Fact]
    public async Task Issue_ResponseContainsOnlyStableIdentityDefaults()
    {
        Harness harness = Harness.WithSuccessfulIssue();

        Result<LoginResponse> result = await harness.Service.IssueTokensAsync(UserId, PortalId);

        result.Value.User.UserId.Should().Be(UserId);
        result.Value.User.PortalId.Should().Be(PortalId);
        result.Value.User.Username.Should().BeEmpty();
        result.Value.User.IsSuperUser.Should().BeFalse();
        result.Value.User.Roles.Should().BeEmpty();
        result.Value.User.Permissions.Should().BeEmpty();
    }

    /// <summary>The access-token expiry is derived from the configured lifetime.</summary>
    [Fact]
    public async Task Issue_UsesConfiguredAccessLifetime()
    {
        Harness harness = Harness.WithSuccessfulIssue(expirationMinutes: 17);

        Result<LoginResponse> result = await harness.Service.IssueTokensAsync(UserId, PortalId);

        result.Value.ExpiresAtUtc.Should().Be(Now.AddMinutes(17));
        Read(result.Value.AccessToken).ValidTo.Should().Be(Now.AddMinutes(17));
    }

    /// <summary>All issue timestamps derive from one clock reading.</summary>
    [Fact]
    public async Task Issue_ReadsTheClockOnce()
    {
        Harness harness = Harness.WithSuccessfulIssue();

        await harness.Service.IssueTokensAsync(UserId, PortalId);

        harness.Clock.VerifyGet(clock => clock.UtcNow, Times.Once());
    }

    /// <summary>Two issues at one instant still receive distinct token identifiers.</summary>
    [Fact]
    public async Task Issue_ProducesDistinctAccessTokensAtTheSameInstant()
    {
        Harness harness = Harness.Ready();
        harness.Store
            .SetupSequence(store => store.IssueAsync(
                It.IsAny<RefreshTokenSubject>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(RefreshTokenIssueResult.Succeeded(
                "refresh-one",
                Now.AddDays(1),
                Subject()))
            .ReturnsAsync(RefreshTokenIssueResult.Succeeded(
                "refresh-two",
                Now.AddDays(1),
                Subject()));

        LoginResponse first = (await harness.Service.IssueTokensAsync(UserId, PortalId)).Value;
        LoginResponse second = (await harness.Service.IssueTokensAsync(UserId, PortalId)).Value;

        first.AccessToken.Should().NotBe(second.AccessToken);
        Read(first.AccessToken).Id.Should().NotBe(Read(second.AccessToken).Id);
    }

    /// <summary>The configured issuer and audience are stamped unchanged.</summary>
    [Fact]
    public async Task Issue_UsesConfiguredIssuerAndAudience()
    {
        Harness harness = Harness.WithSuccessfulIssue();

        JwtSecurityToken token = Read(
            (await harness.Service.IssueTokensAsync(UserId, PortalId)).Value.AccessToken);

        token.Issuer.Should().Be(Issuer);
        token.Audiences.Should().ContainSingle().Which.Should().Be(Audience);
    }

    /// <summary>Issued-at and not-before are anchored to the injected UTC instant.</summary>
    [Fact]
    public async Task Issue_UsesInjectedClockForEnvelopeTimes()
    {
        Harness harness = Harness.WithSuccessfulIssue();

        JwtSecurityToken token = Read(
            (await harness.Service.IssueTokensAsync(UserId, PortalId)).Value.AccessToken);

        token.ValidFrom.Should().Be(Now);
        token.Payload.IssuedAt.Should().Be(Now);
    }

    /// <summary>Rotation delegates the opaque token and trusted binding unchanged.</summary>
    [Fact]
    public async Task Refresh_PassesPresentedMaterialAndClientBindingToStore()
    {
        Harness harness = Harness.Ready();
        harness.Store
            .Setup(store => store.RotateAsync(
                RefreshToken,
                ClientBinding,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(Rotated(UserId, PortalId));

        Result<LoginResponse> result = await harness.Service.RefreshAsync(
            RefreshToken,
            ClientBinding);

        result.IsSuccess.Should().BeTrue();
        harness.Store.VerifyAll();
    }

    /// <summary>Rotation takes identity only from the store's subject.</summary>
    [Fact]
    public async Task Refresh_UsesTheDurableStoreSubject()
    {
        Harness harness = Harness.Ready();
        harness.Store
            .Setup(store => store.RotateAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(Rotated(userId: 91, portalId: 0));

        Result<LoginResponse> result = await harness.Service.RefreshAsync(
            RefreshToken,
            ClientBinding);

        result.Value.User.UserId.Should().Be(91);
        result.Value.User.PortalId.Should().Be(0);
        Read(result.Value.AccessToken).Subject.Should().Be("91");
    }

    /// <summary>A successful rotation returns the store's one-time replacement token.</summary>
    [Fact]
    public async Task Refresh_ReturnsTheReplacementRefreshToken()
    {
        Harness harness = Harness.WithSuccessfulRotation();

        Result<LoginResponse> result = await harness.Service.RefreshAsync(
            RefreshToken,
            ClientBinding);

        result.Value.RefreshToken.Should().Be(ReplacementRefreshToken);
    }

    /// <summary>A rotated access token receives a fresh token identifier.</summary>
    [Fact]
    public async Task Refresh_ProducesAFreshAccessTokenIdentifier()
    {
        Harness harness = Harness.Ready();
        harness.Store
            .Setup(store => store.IssueAsync(It.IsAny<RefreshTokenSubject>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Issued(UserId, PortalId));
        harness.Store
            .Setup(store => store.RotateAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(Rotated(UserId, PortalId));

        string issued = (await harness.Service.IssueTokensAsync(UserId, PortalId)).Value.AccessToken;
        string rotated = (await harness.Service.RefreshAsync(RefreshToken, ClientBinding)).Value.AccessToken;

        Read(rotated).Id.Should().NotBe(Read(issued).Id);
    }

    /// <summary>Blank refresh material is refused without a store read.</summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Refresh_BlankTokenNeverReachesStore(string refreshToken)
    {
        Harness harness = Harness.Ready();

        Result<LoginResponse> result = await harness.Service.RefreshAsync(
            refreshToken,
            ClientBinding);

        result.IsFailure.Should().BeTrue();
        result.Reason!.Code.Should().Be("REFRESH_TOKEN_NOTFOUND");
        harness.Store.Verify(
            store => store.RotateAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()),
            Times.Never());
    }

    /// <summary>A null refresh value is a caller defect rather than an ordinary refusal.</summary>
    [Fact]
    public async Task Refresh_RejectsNullToken()
    {
        Harness harness = Harness.Ready();

        Func<Task> refresh = () => harness.Service.RefreshAsync(
            null!,
            ClientBinding);

        await refresh.Should().ThrowAsync<ArgumentNullException>();
    }

    /// <summary>A missing trusted client binding is rejected before store access.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Refresh_RejectsBlankClientBinding(string? clientBinding)
    {
        Harness harness = Harness.Ready();

        Func<Task> refresh = () => harness.Service.RefreshAsync(
            RefreshToken,
            clientBinding!);

        await refresh.Should().ThrowAsync<ArgumentException>();
        harness.Store.Verify(
            store => store.RotateAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()),
            Times.Never());
    }

    /// <summary>Cancellation before rotation leaves the store untouched.</summary>
    [Fact]
    public async Task Refresh_HonoursPreCancelledRequest()
    {
        Harness harness = Harness.Ready();
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();

        Func<Task> refresh = () => harness.Service.RefreshAsync(
            RefreshToken,
            ClientBinding,
            cancellation.Token);

        await refresh.Should().ThrowAsync<OperationCanceledException>();
        harness.Store.Verify(
            store => store.RotateAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()),
            Times.Never());
    }

    /// <summary>Every store refusal maps to its stable token-service reason.</summary>
    [Theory]
    [InlineData(RefreshTokenOutcome.Unknown, "REFRESH_TOKEN_NOTFOUND")]
    [InlineData(RefreshTokenOutcome.Revoked, "REFRESH_TOKEN_REVOKED")]
    [InlineData(RefreshTokenOutcome.AlreadyUsed, "REFRESH_TOKEN_ALREADYUSED")]
    [InlineData(RefreshTokenOutcome.ConcurrentUse, "REFRESH_TOKEN_ALREADYUSED")]
    [InlineData(RefreshTokenOutcome.Expired, "REFRESH_TOKEN_EXPIRED")]
    [InlineData(RefreshTokenOutcome.StoreUnavailable, "TOKEN_STORE_UNAVAILABLE")]
    [InlineData(RefreshTokenOutcome.CapacityExhausted, "TOKEN_STORE_UNAVAILABLE")]
    public async Task Refresh_MapsStoreOutcomes(
        RefreshTokenOutcome outcome,
        string expectedCode)
    {
        Harness harness = Harness.Ready();
        harness.Store
            .Setup(store => store.RotateAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(RefreshTokenRotationResult.Failed(outcome));

        Result<LoginResponse> result = await harness.Service.RefreshAsync(
            RefreshToken,
            ClientBinding);

        result.IsFailure.Should().BeTrue();
        result.Reason!.Code.Should().Be(expectedCode);
    }

    /// <summary>No refresh refusal leaks the opaque material in its public detail.</summary>
    [Theory]
    [InlineData(RefreshTokenOutcome.Unknown)]
    [InlineData(RefreshTokenOutcome.Revoked)]
    [InlineData(RefreshTokenOutcome.AlreadyUsed)]
    [InlineData(RefreshTokenOutcome.Expired)]
    public async Task Refresh_FailureNeverEchoesPresentedMaterial(RefreshTokenOutcome outcome)
    {
        Harness harness = Harness.Ready();
        harness.Store
            .Setup(store => store.RotateAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(RefreshTokenRotationResult.Failed(outcome));

        Result<LoginResponse> result = await harness.Service.RefreshAsync(
            RefreshToken,
            ClientBinding);

        result.Reason!.Message.Should().NotContain(RefreshToken);
        result.Reason.Message.Should().NotContain(ClientBinding);
    }

    /// <summary>Blank revocation is idempotent and performs no store I/O.</summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Revoke_BlankTokenSucceedsWithoutStoreAccess(string refreshToken)
    {
        Harness harness = Harness.Ready();

        Result result = await harness.Service.RevokeRefreshTokenAsync(refreshToken);

        result.IsSuccess.Should().BeTrue();
        harness.Store.Verify(
            store => store.RevokeAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never());
    }

    /// <summary>Only a PROVEN retirement is reported as a completed revocation.</summary>
    /// <param name="outcome">The outcome the store reported.</param>
    [Theory]
    [InlineData(RefreshTokenOutcome.Succeeded)]
    [InlineData(RefreshTokenOutcome.AlreadyRevoked)]
    public async Task Revoke_ProvenRetirementIsIdempotentSuccess(RefreshTokenOutcome outcome)
    {
        Harness harness = Harness.Ready();
        harness.Store
            .SetupGet(store => store.IsAuthoritativeAcrossReplicas)
            .Returns(false);
        harness.Store
            .Setup(store => store.RevokeAsync(
                RefreshToken,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(outcome);

        Result result = await harness.Service.RevokeRefreshTokenAsync(RefreshToken);

        result.IsSuccess.Should().BeTrue();
    }

    /// <summary>An unproven retirement is reported as a failure, not as a completed sign-out.</summary>
    /// <param name="outcome">The outcome the store reported.</param>
    [Theory]
    [InlineData(RefreshTokenOutcome.Unknown)]
    [InlineData(RefreshTokenOutcome.Expired)]
    [InlineData(RefreshTokenOutcome.Revoked)]
    [InlineData(RefreshTokenOutcome.AlreadyUsed)]
    [InlineData(RefreshTokenOutcome.ConcurrentUse)]
    public async Task Revoke_UnprovenRetirementIsReported(RefreshTokenOutcome outcome)
    {
        Harness harness = Harness.Ready();
        harness.Store
            .SetupGet(store => store.IsAuthoritativeAcrossReplicas)
            .Returns(false);
        harness.Store
            .Setup(store => store.RevokeAsync(
                RefreshToken,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(outcome);

        Result result = await harness.Service.RevokeRefreshTokenAsync(RefreshToken);

        result.IsFailure.Should().BeTrue();
    }

    /// <summary>An AUTHORITATIVE store that holds nothing has proved there is nothing left to retire.</summary>
    [Fact]
    public async Task Revoke_UnknownFromAuthoritativeStoreIsSuccess()
    {
        Harness harness = Harness.Ready();
        harness.Store
            .SetupGet(store => store.IsAuthoritativeAcrossReplicas)
            .Returns(true);
        harness.Store
            .Setup(store => store.RevokeAsync(
                RefreshToken,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(RefreshTokenOutcome.Unknown);

        Result result = await harness.Service.RevokeRefreshTokenAsync(RefreshToken);

        result.IsSuccess.Should().BeTrue();
    }

    /// <summary>Store faults prevent revocation from being reported as complete.</summary>
    [Theory]
    [InlineData(RefreshTokenOutcome.StoreUnavailable)]
    [InlineData(RefreshTokenOutcome.CapacityExhausted)]
    public async Task Revoke_StoreFaultIsReported(RefreshTokenOutcome outcome)
    {
        Harness harness = Harness.Ready();
        harness.Store
            .Setup(store => store.RevokeAsync(
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(outcome);

        Result result = await harness.Service.RevokeRefreshTokenAsync(RefreshToken);

        result.IsFailure.Should().BeTrue();
        result.Reason!.Code.Should().Be("TOKEN_STORE_UNAVAILABLE");
    }

    /// <summary>Single-family revocation observes cancellation before store access.</summary>
    [Fact]
    public async Task Revoke_HonoursPreCancelledRequest()
    {
        Harness harness = Harness.Ready();
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();

        Func<Task> revoke = () => harness.Service.RevokeRefreshTokenAsync(
            RefreshToken,
            cancellation.Token);

        await revoke.Should().ThrowAsync<OperationCanceledException>();
        harness.Store.Verify(
            store => store.RevokeAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never());
    }

    /// <summary>Account-wide revocation delegates the exact account identifier.</summary>
    [Fact]
    public async Task RevokeAll_PassesTheExactAccountIdentifier()
    {
        Harness harness = Harness.Ready();
        harness.Store
            .Setup(store => store.RevokeAllForUserAsync(
                UserId,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(RefreshTokenOutcome.Succeeded);

        Result result = await harness.Service.RevokeAllRefreshTokensAsync(UserId);

        result.IsSuccess.Should().BeTrue();
        harness.Store.VerifyAll();
    }

    /// <summary>Only a PROVEN account-wide retirement is reported as complete.</summary>
    /// <param name="outcome">The outcome the store reported.</param>
    [Theory]
    [InlineData(RefreshTokenOutcome.Succeeded)]
    [InlineData(RefreshTokenOutcome.AlreadyRevoked)]
    public async Task RevokeAll_ProvenRetirementIsIdempotentSuccess(RefreshTokenOutcome outcome)
    {
        Harness harness = Harness.Ready();
        harness.Store
            .SetupGet(store => store.IsAuthoritativeAcrossReplicas)
            .Returns(false);
        harness.Store
            .Setup(store => store.RevokeAllForUserAsync(
                It.IsAny<int>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(outcome);

        Result result = await harness.Service.RevokeAllRefreshTokensAsync(UserId);

        result.IsSuccess.Should().BeTrue();
    }

    /// <summary>An unproven account-wide retirement is reported rather than absorbed.</summary>
    /// <param name="outcome">The outcome the store reported.</param>
    /// <remarks>
    /// The callers that cascade an account or tenant removal distinguish this from an unreachable store
    /// themselves - "nothing to end here" does not block a removal, an unanswerable store does - and they
    /// can only do that because this contract stops collapsing the two into one success.
    /// </remarks>
    [Theory]
    [InlineData(RefreshTokenOutcome.Unknown)]
    [InlineData(RefreshTokenOutcome.Expired)]
    [InlineData(RefreshTokenOutcome.Revoked)]
    [InlineData(RefreshTokenOutcome.AlreadyUsed)]
    [InlineData(RefreshTokenOutcome.ConcurrentUse)]
    public async Task RevokeAll_UnprovenRetirementIsReported(RefreshTokenOutcome outcome)
    {
        Harness harness = Harness.Ready();
        harness.Store
            .SetupGet(store => store.IsAuthoritativeAcrossReplicas)
            .Returns(false);
        harness.Store
            .Setup(store => store.RevokeAllForUserAsync(
                It.IsAny<int>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(outcome);

        Result result = await harness.Service.RevokeAllRefreshTokensAsync(UserId);

        result.IsFailure.Should().BeTrue();
    }

    /// <summary>Account-wide store faults are surfaced.</summary>
    [Theory]
    [InlineData(RefreshTokenOutcome.StoreUnavailable)]
    [InlineData(RefreshTokenOutcome.CapacityExhausted)]
    public async Task RevokeAll_StoreFaultIsReported(RefreshTokenOutcome outcome)
    {
        Harness harness = Harness.Ready();
        harness.Store
            .Setup(store => store.RevokeAllForUserAsync(
                It.IsAny<int>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(outcome);

        Result result = await harness.Service.RevokeAllRefreshTokensAsync(UserId);

        result.IsFailure.Should().BeTrue();
        result.Reason!.Code.Should().Be("TOKEN_STORE_UNAVAILABLE");
    }

    /// <summary>Account-wide revocation observes cancellation before store access.</summary>
    [Fact]
    public async Task RevokeAll_HonoursPreCancelledRequest()
    {
        Harness harness = Harness.Ready();
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();

        Func<Task> revoke = () => harness.Service.RevokeAllRefreshTokensAsync(
            UserId,
            cancellation.Token);

        await revoke.Should().ThrowAsync<OperationCanceledException>();
        harness.Store.Verify(
            store => store.RevokeAllForUserAsync(
                It.IsAny<int>(),
                It.IsAny<CancellationToken>()),
            Times.Never());
    }

    private static JwtOptions ValidOptions(int expirationMinutes = 30) => new()
    {
        Secret = SigningSecret,
        Issuer = Issuer,
        Audience = Audience,
        ExpirationMinutes = expirationMinutes,
        RefreshTokenExpirationDays = 7,
        RefreshTokenAbsoluteExpirationDays = 30,
    };

    private static RefreshTokenSubject Subject(int userId = UserId, int portalId = PortalId) =>
        new(userId, portalId);

    private static RefreshTokenIssueResult Issued(int userId, int portalId) =>
        RefreshTokenIssueResult.Succeeded(
            RefreshToken,
            Now.AddDays(7),
            Subject(userId, portalId));

    private static RefreshTokenRotationResult Rotated(int userId, int portalId) =>
        RefreshTokenRotationResult.Succeeded(
            ReplacementRefreshToken,
            Now.AddDays(7),
            Subject(userId, portalId));

    private static JwtSecurityToken Read(string accessToken) =>
        new JwtSecurityTokenHandler().ReadJwtToken(accessToken);

    private static TokenValidationParameters ValidationParameters() => new()
    {
        ValidateIssuerSigningKey = true,
        IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(SigningSecret)),
        ValidateIssuer = true,
        ValidIssuer = Issuer,
        ValidateAudience = true,
        ValidAudience = Audience,
        ValidateLifetime = false,
        ClockSkew = TimeSpan.Zero,
        NameClaimType = DnnClaimTypes.Subject,
    };

    private sealed class Harness
    {
        private Harness(int expirationMinutes)
        {
            Store = new Mock<IRefreshTokenStore>(MockBehavior.Strict);
            Clock = new Mock<IClock>(MockBehavior.Strict);
            Clock.SetupGet(clock => clock.UtcNow).Returns(Now);
            Service = new JwtTokenService(
                Store.Object,
                Clock.Object,
                Options.Create(ValidOptions(expirationMinutes)));
        }

        public Mock<IRefreshTokenStore> Store { get; }

        public Mock<IClock> Clock { get; }

        public JwtTokenService Service { get; }

        public static Harness Ready(int expirationMinutes = 30) => new(expirationMinutes);

        public static Harness WithSuccessfulIssue(int expirationMinutes = 30)
        {
            Harness harness = Ready(expirationMinutes);
            harness.Store
                .Setup(store => store.IssueAsync(
                    It.IsAny<RefreshTokenSubject>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(Issued(UserId, PortalId));
            return harness;
        }

        public static Harness WithSuccessfulRotation(int expirationMinutes = 30)
        {
            Harness harness = Ready(expirationMinutes);
            harness.Store
                .Setup(store => store.RotateAsync(
                    It.IsAny<string>(),
                    It.IsAny<string>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(Rotated(UserId, PortalId));
            return harness;
        }
    }
}
