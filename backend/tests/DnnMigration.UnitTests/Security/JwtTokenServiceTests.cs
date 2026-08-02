using System.Collections;
using System.Reflection;
using DnnMigration.Application;
using DnnMigration.Application.Abstractions;
using DnnMigration.Application.Dtos.Auth;
using DnnMigration.Application.Dtos.Common;
using DnnMigration.Application.Dtos.Module;
using DnnMigration.Application.Dtos.Portal;
using DnnMigration.Application.Dtos.Role;
using DnnMigration.Application.Dtos.User;
using DnnMigration.Application.Options;
using DnnMigration.Application.Services;
using DnnMigration.Domain.Abstractions.Repositories;
using DnnMigration.Domain.Abstractions.Services;
using DnnMigration.Domain.Common;
using DnnMigration.Domain.Entities;
using FluentAssertions;
using FluentValidation;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Xunit;

namespace DnnMigration.UnitTests.Security;

/// <summary>
/// Covers the bearer-token contract: the options that configure it, the claim names it writes, the shape of
/// the contract itself, and the way the authentication service is required to use it.
/// </summary>
/// <remarks>
/// <para>
/// The signing implementation lives in the infrastructure assembly and is deliberately unreachable from
/// this project, which references the application assembly and nothing else. Real issuance, real signature
/// validation and the real claim payload are proved by the integration project, which signs in over HTTP
/// and then calls a protected endpoint with the token it was given.
/// </para>
/// <para>
/// What remains here is the part an end-to-end sign-in cannot show, because a working sign-in looks the
/// same whether or not these properties hold: that no member of the contract can carry a credential or a
/// signing key; that the claim vocabulary is stable, because a renamed claim silently unauthorises every
/// existing token; that a rotation failure is reported as one indistinguishable reason, so the endpoint
/// cannot be used to tell an expired token from a revoked or replayed one; that a refresh re-reads the
/// caller's roles and permissions from the store instead of trusting the copy that travelled with the
/// token; and that an unavailable token store fails loudly rather than being mistaken for a bad token.
/// </para>
/// </remarks>
public class JwtTokenServiceTests
{
    private const int PortalId = -1;

    private const int UserId = 7;

    private const int HostUserId = 1;

    private const string AccountName = "measured_member";

    private const string RawPassword = "Integr8tion!Pass";

    private const string StoredHash = "$2a$12$storedhashvalue";

    private const string SuppliedRefreshToken = "supplied-refresh-token";

    private const string RotatedAccessToken = "rotated-access-token";

    private const string InvalidRefreshTokenCode = "auth.invalid_refresh_token";

    private const string UserNotFoundCode = "auth.user_not_found";

    private const string TokenStoreUnavailableCode = "TOKEN_STORE_UNAVAILABLE";

    private const string InvalidTokenMessage = "The refresh token is not valid.";

    private static readonly DateTime Now = new(2026, 8, 2, 12, 0, 0, DateTimeKind.Utc);

    /// <summary>
    /// The token options carry the documented defaults and name their configuration section.
    /// </summary>
    [Fact]
    public void TokenOptions_CarryTheDocumentedDefaults()
    {
        JwtOptions.SectionName.Should().Be("Jwt");

        JwtOptions options = new();

        options.Issuer.Should().Be("DnnMigration");
        options.Audience.Should().Be("DnnMigration");
        options.ExpirationMinutes.Should().Be(
            60,
            "an access token cannot be recalled once issued, so its life is deliberately short and is the "
            + "only thing that ends it");
        options.RefreshTokenExpirationDays.Should().Be(7);
    }

    /// <summary>
    /// There is no default signing key.
    /// </summary>
    /// <remarks>
    /// The legacy installation committed the key that decrypted every stored credential to source control.
    /// Shipping a default here would repeat that mistake in a form that works, which is worse: a deployment
    /// that forgot to supply a key would run happily on a key an attacker also has. An empty default is
    /// what lets the host refuse to start instead.
    /// </remarks>
    [Fact]
    public void TokenOptions_ShipNoSigningKey()
    {
        JwtOptions options = new();

        options.Secret.Should().BeEmpty();
    }

    /// <summary>
    /// Every token option is settable, so a deployment configures them without a code change.
    /// </summary>
    [Fact]
    public void TokenOptions_AreConfigurableEntirelyFromSettings()
    {
        JwtOptions options = new()
        {
            Secret = new string('k', 48),
            Issuer = "contoso-portal",
            Audience = "contoso-spa",
            ExpirationMinutes = 15,
            RefreshTokenExpirationDays = 1,
        };

        options.Secret.Should().HaveLength(48);
        options.Issuer.Should().Be("contoso-portal");
        options.Audience.Should().Be("contoso-spa");
        options.ExpirationMinutes.Should().Be(15);
        options.RefreshTokenExpirationDays.Should().Be(1);
    }

    /// <summary>
    /// The claim vocabulary is the measured set of wire names.
    /// </summary>
    /// <remarks>
    /// These strings are written into every issued token and read out of every presented one. Renaming any
    /// of them unauthorises every token already in circulation without a single test failing anywhere else,
    /// which is exactly why they are pinned here by value.
    /// </remarks>
    [Fact]
    public void ClaimNames_AreTheMeasuredWireNames()
    {
        DnnClaimTypes.Subject.Should().Be("sub");
        DnnClaimTypes.JwtId.Should().Be("jti");
        DnnClaimTypes.UniqueName.Should().Be("unique_name");
        DnnClaimTypes.PortalId.Should().Be("portal_id");
        DnnClaimTypes.SuperUser.Should().Be("is_superuser");
        DnnClaimTypes.Permission.Should().Be("permission");
    }

    /// <summary>
    /// The claim vocabulary declares no role name of its own.
    /// </summary>
    /// <remarks>
    /// Roles are emitted under the framework's own role claim type so that the standard role requirements
    /// and the role-based policy used by the tenant-administration endpoints work without a custom handler.
    /// A private role claim here would look correct and silently authorise nobody.
    /// </remarks>
    [Fact]
    public void ClaimNames_DeclareNoRoleNameOfTheirOwn()
    {
        IReadOnlyList<string> declared = DeclaredClaimNames();

        declared.Should().NotContain(
            name => name.Contains("role", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// No two claim names collide.
    /// </summary>
    [Fact]
    public void ClaimNames_AreDistinct()
    {
        IReadOnlyList<string> values = DeclaredClaimValues();

        values.Should().OnlyHaveUniqueItems();
        values.Should().HaveCount(6);
        values.Should().NotContain(value => string.IsNullOrWhiteSpace(value));
    }

    /// <summary>
    /// The token contract offers exactly four operations.
    /// </summary>
    [Fact]
    public void TokenContract_OffersExactlyFourOperations()
    {
        typeof(ITokenService).GetMethods().Select(member => member.Name).Should().BeEquivalentTo(
            new[]
            {
                nameof(ITokenService.IssueTokensAsync),
                nameof(ITokenService.RefreshAsync),
                nameof(ITokenService.RevokeRefreshTokenAsync),
                nameof(ITokenService.RevokeAllRefreshTokensAsync),
            });
    }

    /// <summary>
    /// No operation on the token contract accepts or returns a credential or any signing material.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The distinction being drawn is between a <em>credential</em> or a <em>cryptographic key</em>, neither
    /// of which may cross this boundary, and a <em>permission key</em>, which is simply the name of an
    /// entitlement and is one of the things issuance exists to carry. An earlier draft of this suite treated
    /// the bare substring "key" as disqualifying and failed on the <c>permissionKeys</c> parameter; the
    /// contract was right and the assertion was wrong, so the rule is stated precisely here rather than
    /// loosened.
    /// </para>
    /// <para>
    /// The point of the check is that authentication and signing are separate responsibilities. The service
    /// that mints a token is handed an already-authenticated identity; if it could also be handed a
    /// credential, a future change could quietly move credential comparison into it, and the single
    /// verification path the sign-in flow depends on would no longer be single.
    /// </para>
    /// </remarks>
    [Fact]
    public void TokenContract_NeverAcceptsOrReturnsASecret()
    {
        string[] credentialTokens = ["password", "passphrase", "credential", "hash", "answer", "salt"];
        string[] signingMaterialTokens = ["secret", "signingkey", "securitykey", "privatekey", "apikey"];

        foreach (MethodInfo member in typeof(ITokenService).GetMethods())
        {
            foreach (ParameterInfo parameter in member.GetParameters())
            {
                string name = parameter.Name ?? string.Empty;

                credentialTokens.Should().NotContain(
                    token => name.Contains(token, StringComparison.OrdinalIgnoreCase),
                    "{0} must not take a credential - the token service signs, it does not authenticate",
                    member.Name);

                signingMaterialTokens.Should().NotContain(
                    token => name.Contains(token, StringComparison.OrdinalIgnoreCase),
                    "{0} must not be handed signing material - the key is configuration held by the "
                    + "implementation, not an argument its callers pass around",
                    member.Name);

                name.Should().NotBe(
                    "key",
                    "a parameter called exactly that is cryptographic material, whereas permissionKeys is "
                    + "a list of entitlement names");

                parameter.ParameterType.Should().Match(
                    type => type == typeof(int)
                        || type == typeof(bool)
                        || type == typeof(string)
                        || type == typeof(IReadOnlyList<string>)
                        || type == typeof(CancellationToken),
                    "{0} takes identity facts, entitlement names and a cancellation token only",
                    member.Name);
            }

            member.ReturnType.Should().Match(
                type => type == typeof(Task<Result>) || type == typeof(Task<Result<LoginResponse>>),
                "{0} must report through the result channel and hand back nothing else",
                member.Name);
        }
    }

    /// <summary>
    /// Issuance takes the identity facts and the caller's entitlements, in that order.
    /// </summary>
    [Fact]
    public void TokenContract_IssuanceTakesTheIdentityFactsInOrder()
    {
        MethodInfo issue = typeof(ITokenService).GetMethod(nameof(ITokenService.IssueTokensAsync))!;

        issue.GetParameters().Select(parameter => parameter.Name).Should().BeEquivalentTo(
            new[]
            {
                "userId",
                "portalId",
                "userName",
                "isSuperUser",
                "roles",
                "permissionKeys",
                "cancellationToken",
            },
            options => options.WithStrictOrdering());
    }

    /// <summary>
    /// The application layer registers its own services and deliberately does not register the token
    /// contract.
    /// </summary>
    /// <remarks>
    /// Signing is infrastructure. Registering the contract here would put a signing key inside the layer
    /// that is not allowed to know about one, and would make the application assembly unusable without a
    /// configured key even in a test that never signs anything.
    /// </remarks>
    [Fact]
    public void TokenContract_IsNotRegisteredByTheApplicationLayer()
    {
        RecordingServiceCollection services = [];

        services.AddApplication();

        services.Select(descriptor => descriptor.ServiceType).Should().NotContain(typeof(ITokenService));

        services.Should().Contain(
            descriptor => descriptor.ServiceType == typeof(IAuthService)
                && descriptor.ImplementationType == typeof(AuthService));
        services.Should().Contain(descriptor => descriptor.ServiceType == typeof(IPortalService));
        services.Should().Contain(descriptor => descriptor.ServiceType == typeof(IModuleService));
        services.Should().Contain(descriptor => descriptor.ServiceType == typeof(IUserService));
        services.Should().Contain(descriptor => descriptor.ServiceType == typeof(IRoleService));
        services.Should().Contain(descriptor => descriptor.ServiceType == typeof(IPermissionService));
        services.Should().Contain(descriptor => descriptor.ServiceType == typeof(ITabService));

        services.Should().OnlyContain(
            descriptor => descriptor.Lifetime == ServiceLifetime.Scoped,
            "every application service and validator is per request");

        // MIGRATION: this pins the SHAPE of the registered set rather than its size. An earlier
        // revision asserted a descriptor count, which measured the validator scan's registration
        // arithmetic instead of the property under test: AddValidatorsFromAssemblyContaining registers
        // every validator twice, once under IValidator<T> and once under its own concrete type, and
        // PagedRequestValidator has four sealed derivations the scan finds as well, so the number moves
        // whenever a validator is added or the scan's strategy changes while the property this test
        // exists to state - that this layer registers its own service contracts and its request
        // validators, and nothing else - does not.
        Type[] applicationServices =
        [
            typeof(IPortalService),
            typeof(IModuleService),
            typeof(IUserService),
            typeof(IRoleService),
            typeof(IPermissionService),
            typeof(ITabService),
            typeof(IAuthService),
        ];

        services.Should().OnlyContain(
            descriptor => applicationServices.Contains(descriptor.ServiceType)
                || typeof(IValidator).IsAssignableFrom(descriptor.ServiceType),
            "the application layer registers its own service contracts and its request validators, "
            + "and nothing else");

        // The validator inventory is stated by name, because which requests carry a validator is the
        // substance of the registration and a count would not say it. All four derivations of
        // PagedRequestValidator validate PagedRequest, so the set names that request once.
        services
            .Select(descriptor => descriptor.ServiceType)
            .Where(serviceType => serviceType.IsGenericType
                && serviceType.GetGenericTypeDefinition() == typeof(IValidator<>))
            .Select(serviceType => serviceType.GetGenericArguments()[0])
            .Distinct()
            .Should().BeEquivalentTo(
                new[]
                {
                    typeof(LoginRequest),
                    typeof(CreateUserRequest),
                    typeof(UpdateUserRequest),
                    typeof(ChangePasswordRequest),
                    typeof(CreateRoleRequest),
                    typeof(CreateModuleRequest),
                    typeof(UpdateModuleRequest),
                    typeof(CreatePortalRequest),
                    typeof(UpdatePortalRequest),
                    typeof(CreatePortalAliasRequest),
                    typeof(UpdatePortalAliasRequest),
                    typeof(PagedRequest),
                },
                "every request an endpoint binds has a registered validator");
    }

    /// <summary>
    /// A token response describes a bearer token before anything is put into it.
    /// </summary>
    [Fact]
    public void TokenResponse_DescribesABearerTokenByDefault()
    {
        LoginResponse response = new();

        response.TokenType.Should().Be(
            "Bearer",
            "the client sends the token back in an Authorization header whose scheme is this value");
        response.AccessToken.Should().BeEmpty();
        response.RefreshToken.Should().BeEmpty();
        response.User.Should().NotBeNull(
            "the snapshot is always present, so a caller never has to null-check the identity it just "
            + "authenticated");
        response.User.Roles.Should().BeEmpty();
        response.User.Permissions.Should().BeEmpty();
    }

    /// <summary>
    /// A sign-in asks for a token using the identity facts it resolved.
    /// </summary>
    [Fact]
    public async Task SignIn_AsksForATokenWithTheResolvedIdentityFacts()
    {
        Harness harness = Harness.SignedInSuccessfully();
        harness.Account.IsSuperUser = true;
        harness.Roles = ["Administrators", "Registered Users"];
        harness.Permissions = ["EDIT", "VIEW"];

        Result<LoginResponse> result = await harness.LoginAsync();

        result.IsSuccess.Should().BeTrue(result.Reason?.ToString());
        harness.Tokens.Verify(
            tokens => tokens.IssueTokensAsync(
                UserId,
                PortalId,
                AccountName,
                true,
                It.Is<IReadOnlyList<string>>(roles => roles.SequenceEqual(harness.Roles)),
                It.Is<IReadOnlyList<string>>(keys => keys.SequenceEqual(harness.Permissions)),
                It.IsAny<CancellationToken>()),
            Times.Once());
    }

    /// <summary>
    /// The response carries the snapshot the service resolved, not whatever the signer put there.
    /// </summary>
    [Fact]
    public async Task SignIn_AttachesTheResolvedSnapshotToTheIssuedResponse()
    {
        Harness harness = Harness.SignedInSuccessfully();
        harness.Roles = ["Registered Users"];
        harness.Permissions = ["VIEW"];
        harness.IssuedResponse.User = new CurrentUserDto { UserId = 999, Username = "stale" };

        Result<LoginResponse> result = await harness.LoginAsync();

        result.IsSuccess.Should().BeTrue(result.Reason?.ToString());
        result.Value.User.UserId.Should().Be(UserId);
        result.Value.User.PortalId.Should().Be(PortalId);
        result.Value.User.PortalName.Should().Be("Measured Portal");
        result.Value.User.Username.Should().Be(AccountName);
        result.Value.User.DisplayName.Should().Be("Ada Lovelace");
        result.Value.User.Email.Should().Be("ada@example.com");
        result.Value.User.Roles.Should().Equal(new[] { "Registered Users" });
        result.Value.User.Permissions.Should().Equal(new[] { "VIEW" });
    }

    /// <summary>
    /// A snapshot reports no entitlements when the evaluator cannot answer, rather than failing the sign-in.
    /// </summary>
    /// <remarks>
    /// The entitlement list is an optimisation for the client, not the enforcement point. Every protected
    /// endpoint re-evaluates on the server, so an unanswerable evaluation degrades the client's menus and
    /// nothing else.
    /// </remarks>
    [Fact]
    public async Task SignIn_ReportsNoEntitlementsWhenTheEvaluatorCannotAnswer()
    {
        Harness harness = Harness.SignedInSuccessfully();
        harness.Permissions = ["VIEW"];
        harness.PermissionService
            .Setup(permissions => permissions.GetEffectivePermissionKeysAsync(
                It.IsAny<int>(),
                It.IsAny<int?>(),
                It.IsAny<int?>(),
                It.IsAny<int?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<IReadOnlyList<string>>.Failure(
                "permission.portal_not_found",
                "No such portal."));

        Result<LoginResponse> result = await harness.LoginAsync();

        result.IsSuccess.Should().BeTrue(result.Reason?.ToString());
        result.Value.User.Permissions.Should().BeEmpty();
    }

    /// <summary>
    /// A refused issuance is a fault, not a denial.
    /// </summary>
    /// <remarks>
    /// Everything the sign-in path could legitimately refuse has already been checked by the time a token
    /// is requested, so a signer that declines is reporting a defect in the deployment. Turning that into a
    /// credential refusal would leave an unusable installation looking like a site full of people typing
    /// the wrong password.
    /// </remarks>
    [Fact]
    public async Task SignIn_FailsLoudlyWhenTheSignerDeclines()
    {
        Harness harness = Harness.SignedInSuccessfully();
        harness.Tokens
            .Setup(tokens => tokens.IssueTokensAsync(
                It.IsAny<int>(),
                It.IsAny<int>(),
                It.IsAny<string>(),
                It.IsAny<bool>(),
                It.IsAny<IReadOnlyList<string>>(),
                It.IsAny<IReadOnlyList<string>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<LoginResponse>.Failure("token.signing_key_missing", "No signing key."));

        InvalidOperationException failure = await Assert.ThrowsAsync<InvalidOperationException>(
            () => harness.LoginAsync());

        failure.Message.Should().Contain("declined to issue");
        failure.Message.Should().Contain("token.signing_key_missing");
    }

    /// <summary>
    /// An unavailable token store fails loudly on the issuing path.
    /// </summary>
    [Fact]
    public async Task SignIn_FailsLoudlyWhenTheTokenStoreIsUnavailable()
    {
        Harness harness = Harness.SignedInSuccessfully();
        harness.Tokens
            .Setup(tokens => tokens.IssueTokensAsync(
                It.IsAny<int>(),
                It.IsAny<int>(),
                It.IsAny<string>(),
                It.IsAny<bool>(),
                It.IsAny<IReadOnlyList<string>>(),
                It.IsAny<IReadOnlyList<string>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<LoginResponse>.Failure(TokenStoreUnavailableCode, "The store is down."));

        InvalidOperationException failure = await Assert.ThrowsAsync<InvalidOperationException>(
            () => harness.LoginAsync());

        failure.Message.Should().Contain("token store is unavailable");
    }

    /// <summary>
    /// A rotation request is required.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task Refresh_RequiresARequest()
    {
        Harness harness = Harness.SignedInSuccessfully();

        await Assert.ThrowsAsync<ArgumentNullException>(
            () => harness.Service.RefreshAsync(null!, CancellationToken.None));
    }

    /// <summary>
    /// A blank rotation request is refused without touching the store.
    /// </summary>
    /// <param name="supplied">The supplied token text.</param>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Refresh_RefusesABlankTokenWithoutTouchingTheStore(string supplied)
    {
        Harness harness = Harness.SignedInSuccessfully();

        Result<LoginResponse> result = await harness.RefreshAsync(supplied);

        result.IsFailure.Should().BeTrue();
        result.Reason!.Code.Should().Be(InvalidRefreshTokenCode);
        result.Reason!.Message.Should().Be(InvalidTokenMessage);
        harness.Tokens.Verify(
            tokens => tokens.RefreshAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never());
    }

    /// <summary>
    /// Every rotation failure is reported as the same single reason.
    /// </summary>
    /// <param name="reportedCode">The code the store reported.</param>
    /// <remarks>
    /// The store distinguishes an unknown token from an expired, revoked or already-used one, and that
    /// distinction is valuable in a log. Handing it to the caller would tell an attacker holding a stolen
    /// token whether it was ever valid and whether the theft has been noticed.
    /// </remarks>
    [Theory]
    [InlineData("refresh_token.unknown")]
    [InlineData("refresh_token.expired")]
    [InlineData("refresh_token.revoked")]
    [InlineData("refresh_token.already_used")]
    public async Task Refresh_ReportsEveryRotationFailureAsOneReason(string reportedCode)
    {
        Harness harness = Harness.SignedInSuccessfully();
        harness.Tokens
            .Setup(tokens => tokens.RefreshAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<LoginResponse>.Failure(reportedCode, "Some revealing detail."));

        Result<LoginResponse> result = await harness.RefreshAsync(SuppliedRefreshToken);

        result.IsFailure.Should().BeTrue();
        result.Reason!.Code.Should().Be(InvalidRefreshTokenCode);
        result.Reason!.Message.Should().Be(InvalidTokenMessage);
    }

    /// <summary>
    /// An unavailable token store fails loudly on the rotation path.
    /// </summary>
    [Fact]
    public async Task Refresh_FailsLoudlyWhenTheTokenStoreIsUnavailable()
    {
        Harness harness = Harness.SignedInSuccessfully();
        harness.Tokens
            .Setup(tokens => tokens.RefreshAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<LoginResponse>.Failure(TokenStoreUnavailableCode, "The store is down."));

        InvalidOperationException failure = await Assert.ThrowsAsync<InvalidOperationException>(
            () => harness.RefreshAsync(SuppliedRefreshToken));

        failure.Message.Should().Contain(
            "token store is unavailable",
            "an outage must not be reported as a bad token, or a whole estate of valid sessions looks "
            + "like a replay attack");
    }

    /// <summary>
    /// A rotation re-reads the caller's identity rather than trusting the copy that arrived with the token.
    /// </summary>
    /// <remarks>
    /// This is the whole reason a refresh has a short-lived counterpart. If the stored snapshot were copied
    /// forward, an account removed from a role, renamed, or stripped of a permission would keep its old
    /// entitlements for as long as it kept refreshing.
    /// </remarks>
    [Fact]
    public async Task Refresh_RereadsTheIdentityRatherThanTrustingTheStoredCopy()
    {
        Harness harness = Harness.SignedInSuccessfully();
        harness.Roles = ["Registered Users"];
        harness.Permissions = ["VIEW"];
        harness.RotatedResponse.User = new CurrentUserDto
        {
            UserId = UserId,
            PortalId = PortalId,
            PortalName = "Renamed Since",
            Username = "stale_name",
            DisplayName = "Stale Display",
            Email = "stale@example.com",
            IsSuperUser = true,
            Roles = ["Administrators"],
            Permissions = ["EDIT", "VIEW"],
        };

        Result<LoginResponse> result = await harness.RefreshAsync(SuppliedRefreshToken);

        result.IsSuccess.Should().BeTrue(result.Reason?.ToString());
        result.Value.AccessToken.Should().Be(
            RotatedAccessToken,
            "the rotated token itself is passed through untouched");
        result.Value.User.PortalName.Should().Be("Measured Portal");
        result.Value.User.Username.Should().Be(AccountName);
        result.Value.User.DisplayName.Should().Be("Ada Lovelace");
        result.Value.User.Email.Should().Be("ada@example.com");
        result.Value.User.IsSuperUser.Should().BeFalse();
        result.Value.User.Roles.Should().Equal(new[] { "Registered Users" });
        result.Value.User.Permissions.Should().Equal(new[] { "VIEW" });
        harness.Users.Verify(
            users => users.ListRoleNamesAsync(PortalId, UserId, Now, It.IsAny<CancellationToken>()),
            Times.Once());
    }

    /// <summary>
    /// A rotation is refused when the tenant named by the token no longer exists.
    /// </summary>
    [Fact]
    public async Task Refresh_IsRefusedWhenTheTenantHasGone()
    {
        Harness harness = Harness.SignedInSuccessfully();
        harness.Portals
            .Setup(portals => portals.GetAsync(
                It.IsAny<int>(),
                It.IsAny<bool>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((Portal?)null);

        Result<LoginResponse> result = await harness.RefreshAsync(SuppliedRefreshToken);

        result.IsFailure.Should().BeTrue();
        result.Reason!.Code.Should().Be(InvalidRefreshTokenCode);
    }

    /// <summary>
    /// A rotation is refused when the account named by the token no longer exists.
    /// </summary>
    [Fact]
    public async Task Refresh_IsRefusedWhenTheAccountHasGone()
    {
        Harness harness = Harness.SignedInSuccessfully();
        harness.Users
            .Setup(users => users.GetAsync(
                It.IsAny<int?>(),
                It.IsAny<int>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((User?)null);

        Result<LoginResponse> result = await harness.RefreshAsync(SuppliedRefreshToken);

        result.IsFailure.Should().BeTrue();
        result.Reason!.Code.Should().Be(InvalidRefreshTokenCode);
    }

    /// <summary>
    /// A host account is resolvable outside the tenant it is refreshing against.
    /// </summary>
    /// <remarks>
    /// A host account is not a member of any tenant, so a tenant-scoped read cannot find it. The
    /// installation-wide fallback exists for exactly that case and accepts nothing else.
    /// </remarks>
    [Fact]
    public async Task Refresh_ResolvesAHostAccountOutsideTheTenant()
    {
        Harness harness = Harness.SignedInSuccessfully();
        User host = new()
        {
            UserId = HostUserId,
            Username = "host",
            FirstName = "Host",
            LastName = "Account",
            DisplayName = "Host Account",
            Email = "host@example.com",
            IsSuperUser = true,
        };
        harness.Users
            .Setup(users => users.GetAsync(
                It.Is<int?>(portalId => portalId != null),
                It.IsAny<int>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((User?)null);
        harness.Users
            .Setup(users => users.GetAsync(
                It.Is<int?>(portalId => portalId == null),
                It.IsAny<int>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(host);

        Result<LoginResponse> result = await harness.RefreshAsync(SuppliedRefreshToken);

        result.IsSuccess.Should().BeTrue(result.Reason?.ToString());
        result.Value.User.Username.Should().Be("host");
        result.Value.User.IsSuperUser.Should().BeTrue();
    }

    /// <summary>
    /// An ordinary account found only outside the tenant is not accepted.
    /// </summary>
    [Fact]
    public async Task Refresh_RefusesAnOrdinaryAccountFoundOnlyOutsideTheTenant()
    {
        Harness harness = Harness.SignedInSuccessfully();
        User outsider = new()
        {
            UserId = 4242,
            Username = "someone_elses_member",
            FirstName = "Other",
            LastName = "Tenant",
            DisplayName = "Other Tenant",
            IsSuperUser = false,
        };
        harness.Users
            .Setup(users => users.GetAsync(
                It.Is<int?>(portalId => portalId != null),
                It.IsAny<int>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((User?)null);
        harness.Users
            .Setup(users => users.GetAsync(
                It.Is<int?>(portalId => portalId == null),
                It.IsAny<int>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(outsider);

        Result<LoginResponse> result = await harness.RefreshAsync(SuppliedRefreshToken);

        result.IsFailure.Should().BeTrue(
            "the installation-wide fallback exists for host accounts only, or a member of one tenant could "
            + "refresh into another");
        result.Reason!.Code.Should().Be(InvalidRefreshTokenCode);
    }

    /// <summary>
    /// A sign-out request is required.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task Logout_RequiresARequest()
    {
        Harness harness = Harness.SignedInSuccessfully();

        await Assert.ThrowsAsync<ArgumentNullException>(
            () => harness.Service.LogoutAsync(null!, CancellationToken.None));
    }

    /// <summary>
    /// Signing out without a token succeeds and touches nothing.
    /// </summary>
    /// <param name="supplied">The supplied token text.</param>
    /// <remarks>
    /// Signing out is idempotent by design. A client that has already discarded its token, or never held
    /// one, still gets a clean answer, because a failure here would leave the caller unable to complete a
    /// sign-out it has in every meaningful sense already completed.
    /// </remarks>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Logout_SucceedsWithoutTouchingTheStoreWhenNoTokenIsSupplied(string supplied)
    {
        Harness harness = Harness.SignedInSuccessfully();

        Result result = await harness.LogoutAsync(supplied);

        result.IsSuccess.Should().BeTrue();
        harness.Tokens.Verify(
            tokens => tokens.RevokeRefreshTokenAsync(
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()),
            Times.Never());
    }

    /// <summary>
    /// Signing out revokes the supplied token.
    /// </summary>
    [Fact]
    public async Task Logout_RevokesTheSuppliedToken()
    {
        Harness harness = Harness.SignedInSuccessfully();

        Result result = await harness.LogoutAsync(SuppliedRefreshToken);

        result.IsSuccess.Should().BeTrue();
        harness.Tokens.Verify(
            tokens => tokens.RevokeRefreshTokenAsync(
                SuppliedRefreshToken,
                It.IsAny<CancellationToken>()),
            Times.Once());
    }

    /// <summary>
    /// Signing out succeeds even when the token was already unusable.
    /// </summary>
    [Fact]
    public async Task Logout_SucceedsEvenWhenTheTokenWasAlreadyUnusable()
    {
        Harness harness = Harness.SignedInSuccessfully();
        harness.Tokens
            .Setup(tokens => tokens.RevokeRefreshTokenAsync(
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Failure("refresh_token.unknown", "No such token."));

        Result result = await harness.LogoutAsync(SuppliedRefreshToken);

        result.IsSuccess.Should().BeTrue(
            "an unknown or expired token means the session is already over, which is what the caller "
            + "asked for");
        result.Reason.Should().BeNull();
    }

    /// <summary>
    /// An unavailable token store fails loudly on the sign-out path.
    /// </summary>
    /// <remarks>
    /// This is the one revocation failure that must not be absorbed. Reporting success while the token
    /// remains usable would tell the caller their session had ended when it had not.
    /// </remarks>
    [Fact]
    public async Task Logout_FailsLoudlyWhenTheTokenStoreIsUnavailable()
    {
        Harness harness = Harness.SignedInSuccessfully();
        harness.Tokens
            .Setup(tokens => tokens.RevokeRefreshTokenAsync(
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Failure(TokenStoreUnavailableCode, "The store is down."));

        InvalidOperationException failure = await Assert.ThrowsAsync<InvalidOperationException>(
            () => harness.LogoutAsync(SuppliedRefreshToken));

        failure.Message.Should().Contain("token store is unavailable");
    }

    /// <summary>
    /// An unidentified caller has no identity to report, which is not a failure.
    /// </summary>
    [Fact]
    public async Task CurrentIdentity_IsAbsentRatherThanAFailureForAnUnidentifiedCaller()
    {
        Harness anonymous = Harness.SignedInSuccessfully();
        anonymous.CurrentUser.SetupGet(caller => caller.IsAuthenticated).Returns(false);

        Result<CurrentUserDto?> unauthenticated = await anonymous.Service
            .GetCurrentUserAsync(CancellationToken.None);

        unauthenticated.IsSuccess.Should().BeTrue();
        unauthenticated.Value.Should().BeNull();

        Harness noAccount = Harness.SignedInSuccessfully();
        noAccount.CurrentUser.SetupGet(caller => caller.IsAuthenticated).Returns(true);
        noAccount.CurrentUser.SetupGet(caller => caller.UserId).Returns((int?)null);
        noAccount.CurrentUser.SetupGet(caller => caller.PortalId).Returns(PortalId);

        Result<CurrentUserDto?> withoutAccount = await noAccount.Service
            .GetCurrentUserAsync(CancellationToken.None);

        withoutAccount.IsSuccess.Should().BeTrue();
        withoutAccount.Value.Should().BeNull();

        Harness noTenant = Harness.SignedInSuccessfully();
        noTenant.CurrentUser.SetupGet(caller => caller.IsAuthenticated).Returns(true);
        noTenant.CurrentUser.SetupGet(caller => caller.UserId).Returns(UserId);
        noTenant.CurrentUser.SetupGet(caller => caller.PortalId).Returns((int?)null);

        Result<CurrentUserDto?> withoutTenant = await noTenant.Service
            .GetCurrentUserAsync(CancellationToken.None);

        withoutTenant.IsSuccess.Should().BeTrue();
        withoutTenant.Value.Should().BeNull();
    }

    /// <summary>
    /// An identified caller gets a snapshot read from the store, not from the token.
    /// </summary>
    [Fact]
    public async Task CurrentIdentity_IsReadFromTheStore()
    {
        Harness harness = Harness.SignedInSuccessfully();
        harness.Roles = ["Registered Users", "Subscribers"];
        harness.Permissions = ["VIEW"];
        harness.CurrentUser.SetupGet(caller => caller.IsAuthenticated).Returns(true);
        harness.CurrentUser.SetupGet(caller => caller.UserId).Returns(UserId);
        harness.CurrentUser.SetupGet(caller => caller.PortalId).Returns(PortalId);
        harness.CurrentUser.SetupGet(caller => caller.Roles).Returns(new[] { "Administrators" });

        Result<CurrentUserDto?> result = await harness.Service.GetCurrentUserAsync(CancellationToken.None);

        result.IsSuccess.Should().BeTrue(result.Reason?.ToString());
        result.Value.Should().NotBeNull();
        result.Value!.UserId.Should().Be(UserId);
        result.Value.PortalId.Should().Be(PortalId);
        result.Value.PortalName.Should().Be("Measured Portal");
        result.Value.Roles.Should().Equal(
            new[] { "Registered Users", "Subscribers" },
            "the roles come from the store, so a role revoked since the token was minted disappears here "
            + "immediately");
        result.Value.Permissions.Should().Equal(new[] { "VIEW" });
    }

    /// <summary>
    /// An identified caller whose account has since gone is reported as missing, naming both identifiers.
    /// </summary>
    [Fact]
    public async Task CurrentIdentity_ReportsAnAccountThatHasSinceGone()
    {
        Harness harness = Harness.SignedInSuccessfully();
        harness.CurrentUser.SetupGet(caller => caller.IsAuthenticated).Returns(true);
        harness.CurrentUser.SetupGet(caller => caller.UserId).Returns(UserId);
        harness.CurrentUser.SetupGet(caller => caller.PortalId).Returns(PortalId);
        harness.Users
            .Setup(users => users.GetAsync(
                It.IsAny<int?>(),
                It.IsAny<int>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((User?)null);

        Result<CurrentUserDto?> result = await harness.Service.GetCurrentUserAsync(CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Reason!.Code.Should().Be(UserNotFoundCode);
        result.Reason!.Message.Should().Contain(UserId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        result.Reason!.Message.Should().Contain(PortalId.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// Reads the names of the claim constants the vocabulary declares.
    /// </summary>
    /// <returns>The declared names.</returns>
    private static IReadOnlyList<string> DeclaredClaimNames()
        => typeof(DnnClaimTypes)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => field.IsLiteral && field.FieldType == typeof(string))
            .Select(field => field.Name)
            .ToList();

    /// <summary>
    /// Reads the values of the claim constants the vocabulary declares.
    /// </summary>
    /// <returns>The declared values.</returns>
    private static IReadOnlyList<string> DeclaredClaimValues()
        => typeof(DnnClaimTypes)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => field.IsLiteral && field.FieldType == typeof(string))
            .Select(field => (string)field.GetRawConstantValue()!)
            .ToList();

    /// <summary>
    /// A registration list that records what an extension method asked for.
    /// </summary>
    /// <remarks>
    /// The concrete collection type lives in a package this project does not reference, and does not need
    /// to: the registration contract is a list of descriptors, so recording them is enough to assert what
    /// the application layer registers and what it deliberately leaves to the composition root.
    /// </remarks>
    private sealed class RecordingServiceCollection : IServiceCollection
    {
        private readonly List<ServiceDescriptor> _descriptors = [];

        public int Count => _descriptors.Count;

        public bool IsReadOnly => false;

        public ServiceDescriptor this[int index]
        {
            get => _descriptors[index];
            set => _descriptors[index] = value;
        }

        public void Add(ServiceDescriptor item) => _descriptors.Add(item);

        public void Clear() => _descriptors.Clear();

        public bool Contains(ServiceDescriptor item) => _descriptors.Contains(item);

        public void CopyTo(ServiceDescriptor[] array, int arrayIndex)
            => _descriptors.CopyTo(array, arrayIndex);

        public IEnumerator<ServiceDescriptor> GetEnumerator() => _descriptors.GetEnumerator();

        public int IndexOf(ServiceDescriptor item) => _descriptors.IndexOf(item);

        public void Insert(int index, ServiceDescriptor item) => _descriptors.Insert(index, item);

        public bool Remove(ServiceDescriptor item) => _descriptors.Remove(item);

        public void RemoveAt(int index) => _descriptors.RemoveAt(index);

        IEnumerator IEnumerable.GetEnumerator() => _descriptors.GetEnumerator();
    }

    /// <summary>
    /// Builds an authentication service over substituted collaborators.
    /// </summary>
    private sealed class Harness
    {
        private Harness()
        {
            Portal = new Portal
            {
                PortalId = PortalId,
                PortalName = "Measured Portal",
                AdministratorId = 2,
                HomeDirectory = "Portals/0",
                DefaultLanguage = "en-US",
            };

            Account = new User
            {
                UserId = UserId,
                Username = AccountName,
                FirstName = "Ada",
                LastName = "Lovelace",
                DisplayName = "Ada Lovelace",
                Email = "ada@example.com",
            };

            Roles = [];
            Permissions = [];

            IssuedResponse = new LoginResponse
            {
                AccessToken = "issued-access-token",
                RefreshToken = "issued-refresh-token",
                ExpiresIn = 3600,
                ExpiresAtUtc = Now.AddMinutes(60),
                RefreshTokenExpiresAtUtc = Now.AddDays(7),
            };

            RotatedResponse = new LoginResponse
            {
                AccessToken = RotatedAccessToken,
                RefreshToken = "rotated-refresh-token",
                ExpiresIn = 3600,
                ExpiresAtUtc = Now.AddMinutes(60),
                RefreshTokenExpiresAtUtc = Now.AddDays(7),
                User = new CurrentUserDto { UserId = UserId, PortalId = PortalId },
            };

            Policy = new PasswordPolicyOptions();
            Users = new Mock<IUserRepository>(MockBehavior.Loose);
            Portals = new Mock<IPortalRepository>(MockBehavior.Loose);
            PermissionService = new Mock<IPermissionService>(MockBehavior.Loose);
            Tokens = new Mock<ITokenService>(MockBehavior.Loose);
            PasswordHasher = new Mock<IPasswordHasher>(MockBehavior.Loose);
            Clock = new Mock<IClock>(MockBehavior.Loose);
            UnitOfWork = new Mock<IUnitOfWork>(MockBehavior.Loose);
            CurrentUser = new Mock<ICurrentUser>(MockBehavior.Loose);

            Service = new AuthService(
                Users.Object,
                Portals.Object,
                PermissionService.Object,
                Tokens.Object,
                PasswordHasher.Object,
                Clock.Object,
                UnitOfWork.Object,
                CurrentUser.Object,
                Policy);
        }

        public Portal Portal { get; }

        public User Account { get; }

        public IReadOnlyList<string> Roles { get; set; }

        public IReadOnlyList<string> Permissions { get; set; }

        public LoginResponse IssuedResponse { get; }

        public LoginResponse RotatedResponse { get; }

        public PasswordPolicyOptions Policy { get; }

        public Mock<IUserRepository> Users { get; }

        public Mock<IPortalRepository> Portals { get; }

        public Mock<IPermissionService> PermissionService { get; }

        public Mock<ITokenService> Tokens { get; }

        public Mock<IPasswordHasher> PasswordHasher { get; }

        public Mock<IClock> Clock { get; }

        public Mock<IUnitOfWork> UnitOfWork { get; }

        public Mock<ICurrentUser> CurrentUser { get; }

        public AuthService Service { get; }

        /// <summary>
        /// Builds a harness whose collaborators all agree that the operation should succeed.
        /// </summary>
        /// <returns>The harness.</returns>
        public static Harness SignedInSuccessfully()
        {
            Harness harness = new();

            harness.Clock.SetupGet(clock => clock.UtcNow).Returns(Now);

            harness.Portals
                .Setup(portals => portals.GetAsync(
                    It.IsAny<int>(),
                    It.IsAny<bool>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(harness.Portal);

            harness.Users
                .Setup(users => users.GetByUsernameAsync(
                    It.IsAny<int?>(),
                    It.IsAny<string>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(harness.Account);

            harness.Users
                .Setup(users => users.GetAsync(
                    It.IsAny<int?>(),
                    It.IsAny<int>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(harness.Account);

            harness.Users
                .Setup(users => users.GetCredentialStateAsync(
                    It.IsAny<int>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync((true, (string?)StoredHash, true, false));

            harness.Users
                .Setup(users => users.RecordSuccessfulLoginAsync(
                    It.IsAny<int>(),
                    It.IsAny<DateTime>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(true);

            harness.Users
                .Setup(users => users.ListRoleNamesAsync(
                    It.IsAny<int>(),
                    It.IsAny<int>(),
                    It.IsAny<DateTime>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => harness.Roles);

            harness.PermissionService
                .Setup(permissions => permissions.GetEffectivePermissionKeysAsync(
                    It.IsAny<int>(),
                    It.IsAny<int?>(),
                    It.IsAny<int?>(),
                    It.IsAny<int?>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => Result<IReadOnlyList<string>>.Success(harness.Permissions));

            harness.PasswordHasher
                .Setup(hasher => hasher.Verify(It.IsAny<string>(), It.IsAny<string>()))
                .Returns(true);

            harness.PasswordHasher
                .Setup(hasher => hasher.NeedsRehash(It.IsAny<string>()))
                .Returns(false);

            harness.Tokens
                .Setup(tokens => tokens.IssueTokensAsync(
                    It.IsAny<int>(),
                    It.IsAny<int>(),
                    It.IsAny<string>(),
                    It.IsAny<bool>(),
                    It.IsAny<IReadOnlyList<string>>(),
                    It.IsAny<IReadOnlyList<string>>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => Result<LoginResponse>.Success(harness.IssuedResponse));

            harness.Tokens
                .Setup(tokens => tokens.RefreshAsync(
                    It.IsAny<string>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => Result<LoginResponse>.Success(harness.RotatedResponse));

            harness.Tokens
                .Setup(tokens => tokens.RevokeRefreshTokenAsync(
                    It.IsAny<string>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(Result.Success());

            return harness;
        }

        /// <summary>
        /// Signs in with the default credential.
        /// </summary>
        /// <returns>The outcome.</returns>
        public Task<Result<LoginResponse>> LoginAsync()
            => Service.LoginAsync(
                PortalId,
                new LoginRequest { Username = AccountName, Password = RawPassword },
                ipAddress: null,
                CancellationToken.None);

        /// <summary>
        /// Rotates a supplied token.
        /// </summary>
        /// <param name="refreshToken">The token to rotate.</param>
        /// <returns>The outcome.</returns>
        public Task<Result<LoginResponse>> RefreshAsync(string refreshToken)
            => Service.RefreshAsync(
                new RefreshTokenRequest { RefreshToken = refreshToken },
                CancellationToken.None);

        /// <summary>
        /// Signs out a supplied token.
        /// </summary>
        /// <param name="refreshToken">The token to revoke.</param>
        /// <returns>The outcome.</returns>
        public Task<Result> LogoutAsync(string refreshToken)
            => Service.LogoutAsync(
                new RefreshTokenRequest { RefreshToken = refreshToken },
                CancellationToken.None);
    }
}
