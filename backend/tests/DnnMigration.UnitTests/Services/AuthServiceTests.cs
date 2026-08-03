using System.Reflection;
using DnnMigration.Application.Abstractions;
using DnnMigration.Application.Dtos.Auth;
using DnnMigration.Application.Options;
using DnnMigration.Application.Services;
using DnnMigration.Domain.Abstractions.Repositories;
using DnnMigration.Domain.Abstractions.Services;
using DnnMigration.Domain.Common;
using DnnMigration.Domain.Entities;
using FluentAssertions;
using Moq;
using Xunit;

namespace DnnMigration.UnitTests.Services;

/// <summary>
/// Covers the sign-in workflow: the order in which the account gates run, how an account is resolved by
/// name, the verified-registration gate, and where the transaction boundary is opened.
/// </summary>
/// <remarks>
/// <para>
/// The credential and token halves of this service are covered by the two security suites, which assert the
/// hashing contract as the sign-in path consumes it and the token contract as the sign-in path consumes it.
/// This suite deliberately does not repeat either. What it owns is the workflow around them: which gate runs
/// before which, what makes an account resolvable at all, the one branch that changes stored state during a
/// sign-in, and the fact that nothing is committed on any path that ends in a refusal.
/// </para>
/// <para>
/// Gate order is the substance of the suite rather than an incidental detail. Each gate leaks something if
/// it runs in the wrong place: examining the credential before the lock lets an attacker confirm a guess on
/// an already-locked account; examining it before the registration gate lets an unapproved registration be
/// used as a credential oracle; and reporting a lock to an unauthorised caller confirms that the account
/// exists at all. The assertions below therefore check not only the answer but which collaborators were
/// never reached.
/// </para>
/// </remarks>
public class AuthServiceTests
{
    private const int PortalId = -1;

    private const int OtherPortalId = 3;

    private const int UserId = 7;

    private const string AccountName = "measured_member";

    private const string RawPassword = "Integr8tion!Pass";

    private const string StoredHash = "$2a$12$storedhashvalue";

    private const string GenericDenial = "The account name or credential is not correct.";

    private const string RequestInvalidCode = "auth.request_invalid";

    private const string InvalidCredentialsCode = "auth.invalid_credentials";

    private const string LockedOutCode = "auth.locked_out";

    private const string InsecureAdminPasswordCode = "auth.insecure_admin_password";

    private static readonly DateTime Now = new(2026, 8, 2, 12, 0, 0, DateTimeKind.Utc);

    /// <summary>
    /// The authentication contract offers exactly four operations with the measured shapes.
    /// </summary>
    [Fact]
    public void AuthenticationContract_OffersExactlyFourOperations()
    {
        typeof(IAuthService).GetMethods().Select(member => member.Name).Should().BeEquivalentTo(
            new[]
            {
                nameof(IAuthService.LoginAsync),
                nameof(IAuthService.RefreshAsync),
                nameof(IAuthService.LogoutAsync),
                nameof(IAuthService.GetCurrentUserAsync),
            });

        MethodInfo login = typeof(IAuthService).GetMethod(nameof(IAuthService.LoginAsync))!;

        login.GetParameters().Select(parameter => parameter.Name).Should().BeEquivalentTo(
            new[] { "request", "cancellationToken" },
            options => options.WithStrictOrdering());
        login.ReturnType.Should().Be(typeof(Task<Result<LoginResponse>>));
    }

    /// <summary>
    /// The sign-in operation accepts the submitted request and a cancellation token, and nothing else.
    /// </summary>
    /// <remarks>
    /// Asserted deliberately rather than left to the shape test above. The tenant travels on the request
    /// and the caller's network address does not travel at all, so a future parameter added for either
    /// would be a regression against the fixed signature this migration prescribes: everything the
    /// operation needs about the submission is on the submission.
    /// </remarks>
    [Fact]
    public void AuthenticationContract_TakesNoTenantOrNetworkAddressArgument()
    {
        MethodInfo login = typeof(IAuthService).GetMethod(nameof(IAuthService.LoginAsync))!;

        login.GetParameters().Should().HaveCount(2);
        login.GetParameters()[0].ParameterType.Should().Be(typeof(LoginRequest));
        login.GetParameters()[1].ParameterType.Should().Be(typeof(CancellationToken));
        login.GetParameters()[1].HasDefaultValue.Should().BeTrue();
        login.GetParameters().Should().NotContain(parameter => parameter.ParameterType == typeof(string));
        login.GetParameters().Should().OnlyContain(parameter => !parameter.ParameterType.IsByRef);
    }

    /// <summary>
    /// The tenant is read from the request rather than from any ambient source, and its absence is
    /// refused as a malformed request rather than as a rejected credential.
    /// </summary>
    /// <remarks>
    /// Zero and minus one are both real tenants, so the absent tenant cannot be defaulted to either.
    /// Refusing it with the request-shape reason keeps it distinguishable from a wrong credential, which
    /// matters because the two have different causes and different fixes.
    /// </remarks>
    [Fact]
    public async Task SignIn_RefusesASubmissionWhoseTenantWasNeverAssigned()
    {
        Harness harness = Harness.Ready();

        Result<LoginResponse> result = await harness.Service.LoginAsync(
            new LoginRequest { Username = AccountName, Password = RawPassword },
            CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Reason!.Code.Should().Be(RequestInvalidCode);
        harness.Users.Verify(
            users => users.GetByUsernameAsync(
                It.IsAny<int?>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()),
            Times.Never());
    }

    /// <summary>
    /// Signing in against a tenant that does not exist is refused without an account being looked for.
    /// </summary>
    /// <remarks>
    /// The refusal is the ordinary one rather than a not-found, because the request arrives on a public
    /// endpoint: reporting that a tenant does not exist would let an unauthenticated caller enumerate the
    /// installation's tenants.
    /// </remarks>
    [Fact]
    public async Task SignIn_RefusesAnUnknownTenantWithoutLookingForAnAccount()
    {
        Harness harness = Harness.Ready();
        harness.Portals
            .Setup(portals => portals.GetByIdAsync(
                It.IsAny<int>(),
                It.IsAny<bool>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((Portal?)null);

        Result<LoginResponse> result = await harness.LoginAsync();

        result.IsFailure.Should().BeTrue();
        result.Reason!.Code.Should().Be(InvalidCredentialsCode);
        result.Reason!.Message.Should().Be(GenericDenial);
        harness.Users.Verify(
            users => users.GetByUsernameAsync(
                It.IsAny<int?>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()),
            Times.Never());
    }

    /// <summary>
    /// An account found within the tenant is used and the installation-wide read is not attempted.
    /// </summary>
    [Fact]
    public async Task SignIn_ResolvesTheAccountWithinTheTenantFirst()
    {
        Harness harness = Harness.Ready();

        Result<LoginResponse> result = await harness.LoginAsync();

        result.IsSuccess.Should().BeTrue(result.Reason?.ToString());
        harness.Users.Verify(
            users => users.GetByUsernameAsync(PortalId, AccountName, It.IsAny<CancellationToken>()),
            Times.Once());
        harness.Users.Verify(
            users => users.GetByUsernameAsync(null, It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never(),
            "the installation-wide read exists for host accounts, which are not members of any tenant, so "
            + "it is only worth a round trip when the tenant-scoped read finds nothing");
    }

    /// <summary>
    /// A host account signs in to a tenant it is not a member of.
    /// </summary>
    [Fact]
    public async Task SignIn_ResolvesAHostAccountOutsideTheTenant()
    {
        Harness harness = Harness.Ready();
        harness.ScopedAccount = null;
        harness.InstallationWideAccount = HostAccount();

        Result<LoginResponse> result = await harness.LoginAsync();

        result.IsSuccess.Should().BeTrue(result.Reason?.ToString());
        result.Value.User.Username.Should().Be("host");
        result.Value.User.IsSuperUser.Should().BeTrue();
    }

    /// <summary>
    /// An ordinary account belonging to another tenant cannot sign in here.
    /// </summary>
    [Fact]
    public async Task SignIn_RefusesAnOrdinaryAccountFromAnotherTenant()
    {
        Harness harness = Harness.Ready();
        harness.ScopedAccount = null;
        harness.InstallationWideAccount = new User
        {
            UserId = 4242,
            Username = AccountName,
            FirstName = "Other",
            LastName = "Tenant",
            DisplayName = "Other Tenant",
            IsSuperUser = false,
        };

        Result<LoginResponse> result = await harness.LoginAsync();

        result.IsFailure.Should().BeTrue(
            "otherwise one tenant's member could sign in to every tenant in the installation");
        result.Reason!.Code.Should().Be(InvalidCredentialsCode);
        harness.Users.Verify(
            users => users.GetCredentialStateAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.Never());
    }

    /// <summary>
    /// An unapproved registration cannot sign in without its verification code.
    /// </summary>
    /// <remarks>
    /// The credential is deliberately not examined. An unapproved registration that reported credential
    /// failure separately from approval failure would be usable as a credential oracle by anyone who could
    /// register.
    /// </remarks>
    [Fact]
    public async Task SignIn_RefusesAnUnapprovedRegistrationWithoutItsVerificationCode()
    {
        Harness harness = Harness.Ready();
        harness.IsApproved = false;

        Result<LoginResponse> result = await harness.LoginAsync();

        result.IsFailure.Should().BeTrue();
        result.Reason!.Code.Should().Be(InvalidCredentialsCode);
        result.Reason!.Message.Should().Be(GenericDenial);
        harness.PasswordHasher.Verify(
            hasher => hasher.Verify(It.IsAny<string>(), It.IsAny<string>()),
            Times.Never());
        harness.Users.Verify(
            users => users.SetApprovalAsync(
                It.IsAny<int>(),
                It.IsAny<bool>(),
                It.IsAny<CancellationToken>()),
            Times.Never());
    }

    /// <summary>
    /// The verification code must match exactly.
    /// </summary>
    /// <param name="supplied">The code supplied.</param>
    /// <remarks>
    /// The comparison is ordinal and untrimmed on purpose. The code is machine generated and echoed back
    /// from a link, so any difference at all means the caller did not follow the link that was sent.
    /// </remarks>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("-1-8")]
    [InlineData("-2-7")]
    [InlineData(" -1-7")]
    [InlineData("-1-7 ")]
    [InlineData("-1--7")]
    [InlineData("1-7")]
    public async Task SignIn_RefusesAMismatchedVerificationCode(string supplied)
    {
        Harness harness = Harness.Ready();
        harness.IsApproved = false;

        Result<LoginResponse> result = await harness.LoginAsync(verificationCode: supplied);

        result.IsFailure.Should().BeTrue();
        result.Reason!.Code.Should().Be(InvalidCredentialsCode);
        harness.Users.Verify(
            users => users.SetApprovalAsync(
                It.IsAny<int>(),
                It.IsAny<bool>(),
                It.IsAny<CancellationToken>()),
            Times.Never());
    }

    /// <summary>
    /// The matching verification code approves the registration, commits it, and signs the account in.
    /// </summary>
    [Fact]
    public async Task SignIn_ApprovesARegistrationThatPresentsItsVerificationCode()
    {
        Harness harness = Harness.Ready();
        harness.IsApproved = false;

        Result<LoginResponse> result = await harness.LoginAsync(verificationCode: "-1-7");

        result.IsSuccess.Should().BeTrue(result.Reason?.ToString());
        harness.Users.Verify(
            users => users.SetApprovalAsync(UserId, true, It.IsAny<CancellationToken>()),
            Times.Once());
        harness.ScopedAccount!.IsApproved.Should().BeTrue(
            "the loaded row is updated too, so the snapshot handed back describes an approved account");
        harness.UnitOfWork.Verify(
            unitOfWork => unitOfWork.SaveChangesAsync(It.IsAny<CancellationToken>()),
            Times.Once());
    }

    /// <summary>
    /// The verification code is built from the tenant being signed in to and the account resolved.
    /// </summary>
    [Fact]
    public async Task SignIn_BuildsTheVerificationCodeFromTheTenantAndTheAccount()
    {
        Harness harness = Harness.Ready();
        harness.IsApproved = false;

        Result<LoginResponse> wrongTenant = await harness.LoginAsync(
            portalId: OtherPortalId,
            verificationCode: "-1-7");

        wrongTenant.IsFailure.Should().BeTrue(
            "the code names the tenant, so a code issued for one tenant does not approve a registration in "
            + "another");

        Harness matching = Harness.Ready();
        matching.IsApproved = false;

        Result<LoginResponse> rightTenant = await matching.LoginAsync(
            portalId: OtherPortalId,
            verificationCode: "3-7");

        rightTenant.IsSuccess.Should().BeTrue(rightTenant.Reason?.ToString());
    }

    /// <summary>
    /// A registration whose approval cannot be recorded is refused and nothing is committed.
    /// </summary>
    [Fact]
    public async Task SignIn_RefusesARegistrationWhoseApprovalCannotBeRecorded()
    {
        Harness harness = Harness.Ready();
        harness.IsApproved = false;
        harness.Users
            .Setup(users => users.SetApprovalAsync(
                It.IsAny<int>(),
                It.IsAny<bool>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        Result<LoginResponse> result = await harness.LoginAsync(verificationCode: "-1-7");

        result.IsFailure.Should().BeTrue();
        result.Reason!.Code.Should().Be(InvalidCredentialsCode);
        harness.UnitOfWork.Verify(
            unitOfWork => unitOfWork.SaveChangesAsync(It.IsAny<CancellationToken>()),
            Times.Never());
        harness.PasswordHasher.Verify(
            hasher => hasher.Verify(It.IsAny<string>(), It.IsAny<string>()),
            Times.Never());
    }

    /// <summary>
    /// A host account is not subject to the registration gate.
    /// </summary>
    /// <remarks>
    /// A host account is created by the installer rather than by registration, so it has no verification
    /// code to present and no tenant to be approved into.
    /// </remarks>
    [Fact]
    public async Task SignIn_ExemptsAHostAccountFromTheRegistrationGate()
    {
        Harness harness = Harness.Ready();
        harness.IsApproved = false;
        harness.ScopedAccount = HostAccount();

        Result<LoginResponse> result = await harness.LoginAsync();

        result.IsSuccess.Should().BeTrue(result.Reason?.ToString());
        harness.Users.Verify(
            users => users.SetApprovalAsync(
                It.IsAny<int>(),
                It.IsAny<bool>(),
                It.IsAny<CancellationToken>()),
            Times.Never());
        harness.UnitOfWork.Verify(
            unitOfWork => unitOfWork.SaveChangesAsync(It.IsAny<CancellationToken>()),
            Times.Never());
    }

    /// <summary>
    /// An ordinary successful sign-in commits nothing, because it changed nothing that needs committing.
    /// </summary>
    /// <remarks>
    /// The login timestamp and the re-hashed credential are both written by explicit statements against the
    /// external membership store rather than through the tracked model, so the transaction boundary is
    /// opened only by the one branch that mutates a tracked row.
    /// </remarks>
    [Fact]
    public async Task SignIn_CommitsNothingWhenNothingTrackedChanged()
    {
        Harness harness = Harness.Ready();

        Result<LoginResponse> result = await harness.LoginAsync();

        result.IsSuccess.Should().BeTrue(result.Reason?.ToString());
        harness.UnitOfWork.Verify(
            unitOfWork => unitOfWork.SaveChangesAsync(It.IsAny<CancellationToken>()),
            Times.Never());
    }

    /// <summary>
    /// The lock is examined before the registration gate.
    /// </summary>
    [Fact]
    public async Task SignIn_ExaminesTheLockBeforeTheRegistrationGate()
    {
        Harness harness = Harness.Ready();
        harness.IsApproved = false;
        harness.IsLockedOut = true;
        harness.CurrentUser.SetupGet(caller => caller.IsAuthenticated).Returns(true);
        harness.CurrentUser.SetupGet(caller => caller.IsSuperUser).Returns(true);

        Result<LoginResponse> result = await harness.LoginAsync(verificationCode: "-1-7");

        result.Reason!.Code.Should().Be(
            LockedOutCode,
            "a locked account is not approved by presenting a verification code");
        harness.Users.Verify(
            users => users.SetApprovalAsync(
                It.IsAny<int>(),
                It.IsAny<bool>(),
                It.IsAny<CancellationToken>()),
            Times.Never());
    }

    /// <summary>
    /// A refused sign-in writes nothing beyond the failure it is required to record.
    /// </summary>
    [Fact]
    public async Task SignIn_WritesNothingOnARefusal()
    {
        Harness harness = Harness.Ready();
        harness.PasswordHasher
            .Setup(hasher => hasher.Verify(It.IsAny<string>(), It.IsAny<string>()))
            .Returns(false);

        Result<LoginResponse> result = await harness.LoginAsync();

        result.IsFailure.Should().BeTrue();
        harness.Users.Verify(
            users => users.RecordSuccessfulLoginAsync(
                It.IsAny<int>(),
                It.IsAny<DateTime>(),
                It.IsAny<CancellationToken>()),
            Times.Never());
        harness.Users.Verify(
            users => users.SetPasswordHashAsync(
                It.IsAny<int>(),
                It.IsAny<string>(),
                It.IsAny<DateTime>(),
                It.IsAny<CancellationToken>()),
            Times.Never());
        harness.UnitOfWork.Verify(
            unitOfWork => unitOfWork.SaveChangesAsync(It.IsAny<CancellationToken>()),
            Times.Never());
        harness.Tokens.Verify(
            tokens => tokens.IssueTokensAsync(
                It.IsAny<int>(),
                It.IsAny<int>(),
                It.IsAny<string>(),
                It.IsAny<bool>(),
                It.IsAny<IReadOnlyList<string>>(),
                It.IsAny<IReadOnlyList<string>>(),
                It.IsAny<CancellationToken>()),
            Times.Never());
    }

    /// <summary>
    /// A successful sign-in records the login once, at the instant the clock reports.
    /// </summary>
    [Fact]
    public async Task SignIn_RecordsTheLoginOnceAtTheClockInstant()
    {
        Harness harness = Harness.Ready();

        await harness.LoginAsync();

        harness.Users.Verify(
            users => users.RecordSuccessfulLoginAsync(UserId, Now, It.IsAny<CancellationToken>()),
            Times.Once());
    }

    /// <summary>
    /// The login timestamp and any re-hash share one instant.
    /// </summary>
    /// <remarks>
    /// The clock is read once and reused. Reading it twice would put two different instants on two rows
    /// written for the same event, which is exactly the kind of skew that makes an audit trail hard to read.
    /// </remarks>
    [Fact]
    public async Task SignIn_UsesOneInstantForEveryThingItWrites()
    {
        Harness harness = Harness.Ready();
        int reads = 0;
        harness.Clock.SetupGet(clock => clock.UtcNow).Returns(() =>
        {
            reads++;
            return Now.AddSeconds(reads);
        });
        harness.PasswordHasher.Setup(hasher => hasher.NeedsRehash(It.IsAny<string>())).Returns(true);
        harness.PasswordHasher.Setup(hasher => hasher.Hash(It.IsAny<string>())).Returns("rehashed");

        await harness.LoginAsync();

        DateTime recorded = harness.RecordedLoginInstant!.Value;

        harness.RehashInstant.Should().Be(recorded);
    }

    /// <summary>
    /// The caller's network address cannot influence the outcome, because it never reaches this layer.
    /// </summary>
    /// <remarks>
    /// The legacy call site supplied an address as its seventh argument and the legacy service did nothing
    /// with it but record it. It is now recorded by the Api layer's structured request log instead, and is
    /// absent from this contract altogether - which is a stronger guarantee than accepting and ignoring
    /// it, because an address that could grant or deny access would be an input a caller controls. This
    /// test asserts the absence, since that is now the whole of the behaviour.
    /// </remarks>
    [Fact]
    public async Task SignIn_CannotBeInfluencedByTheCallersNetworkAddress()
    {
        typeof(IAuthService)
            .GetMethod(nameof(IAuthService.LoginAsync))!
            .GetParameters()
            .Should()
            .NotContain(parameter => parameter.ParameterType == typeof(string));

        typeof(LoginRequest)
            .GetProperties()
            .Should()
            .NotContain(property => property.Name.Contains("Address", StringComparison.Ordinal)
                || property.Name.Contains("Ip", StringComparison.Ordinal));

        Harness harness = Harness.Ready();

        Result<LoginResponse> result = await harness.LoginAsync();

        result.IsSuccess.Should().BeTrue(result.Reason?.ToString());
        harness.Tokens.Verify(
            tokens => tokens.IssueTokensAsync(
                UserId,
                PortalId,
                AccountName,
                false,
                It.IsAny<IReadOnlyList<string>>(),
                It.IsAny<IReadOnlyList<string>>(),
                It.IsAny<CancellationToken>()),
            Times.Once());
    }

    /// <summary>
    /// A malformed sign-in request is refused with its own reason rather than as a credential refusal.
    /// </summary>
    /// <param name="username">The submitted account name.</param>
    /// <param name="password">The submitted credential.</param>
    /// <remarks>
    /// The distinction is deliberate and safe. Reporting that the request itself was incomplete reveals
    /// nothing about whether any account exists, and it is the only way a client can tell a validation
    /// mistake from a wrong credential.
    /// </remarks>
    [Theory]
    [InlineData("", "Integr8tion!Pass")]
    [InlineData("   ", "Integr8tion!Pass")]
    [InlineData("measured_member", "")]
    public async Task SignIn_ReportsAMalformedRequestSeparately(string username, string password)
    {
        Harness harness = Harness.Ready();

        Result<LoginResponse> result = await harness.Service.LoginAsync(
            new LoginRequest { PortalId = PortalId, Username = username, Password = password },
            CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Reason!.Code.Should().Be(RequestInvalidCode);
        result.Reason!.Message.Should().Be("An account name and a credential are both required.");
    }

    /// <summary>
    /// An approved registration that also used a shipped credential carries the advisory.
    /// </summary>
    [Fact]
    public async Task SignIn_CanApproveARegistrationAndStillAdviseOnAShippedCredential()
    {
        Harness harness = Harness.Ready();
        harness.IsApproved = false;
        harness.ScopedAccount!.Username = "admin";

        Result<LoginResponse> result = await harness.LoginAsync(
            username: "admin",
            password: "dnnadmin",
            verificationCode: "-1-7");

        result.IsSuccess.Should().BeTrue();
        result.Reason!.Code.Should().Be(InsecureAdminPasswordCode);
        harness.UnitOfWork.Verify(
            unitOfWork => unitOfWork.SaveChangesAsync(It.IsAny<CancellationToken>()),
            Times.Once());
    }

    /// <summary>
    /// A shipped credential raises the must-change advisory on the response body, never as a status field.
    /// </summary>
    /// <remarks>
    /// MIGRATION: the legacy sign-in status enumeration carried two success-with-caveat values, promoted at
    /// UserController.vb:L1144-L1152 when a known default credential was presented. Neither reaches the wire
    /// as a status; both fold onto the response's must-change advisory, because forcing a credential change
    /// was the legacy remediation for both. The accompanying reason is what keeps the two cases apart for the
    /// API edge.
    /// </remarks>
    [Fact]
    public async Task SignIn_WithAShippedCredential_RaisesTheMustChangeAdvisoryOnTheBody()
    {
        Harness harness = Harness.Ready();
        harness.ScopedAccount!.Username = "admin";

        Result<LoginResponse> result = await harness.LoginAsync(username: "admin", password: "dnnadmin");

        result.IsSuccess.Should().BeTrue("a shipped credential is still a valid credential");
        result.Reason!.Code.Should().Be(InsecureAdminPasswordCode);
        result.Value.MustChangePassword.Should().BeTrue(
            "forcing a credential change is the legacy remediation for a shipped credential");
        result.Value.PasswordExpiring.Should().BeFalse("only the must-change advisory applies here");
        result.Value.MustUpdateProfile.Should().BeFalse("only the must-change advisory applies here");
    }

    /// <summary>
    /// A forced credential update recorded on the account row reaches the response as the advisory boolean.
    /// </summary>
    /// <remarks>
    /// MIGRATION: this was the highest-precedence value of the legacy post-credential check, read from the
    /// account row's own update flag (UserController.vb:L1175-L1177, over the membership property at
    /// UserMembership.vb:L323) and blocking in the legacy screen.
    /// </remarks>
    [Fact]
    public async Task SignIn_WithAForcedCredentialUpdate_RaisesTheMustChangeAdvisory()
    {
        Harness harness = Harness.Ready();
        harness.ScopedAccount!.UpdatePassword = true;

        Result<LoginResponse> result = await harness.LoginAsync();

        result.IsSuccess.Should().BeTrue();
        result.Value.MustChangePassword.Should().BeTrue();
    }

    /// <summary>
    /// An ordinary sign-in raises no advisory, and says so explicitly rather than by omission.
    /// </summary>
    /// <remarks>
    /// All three advisories are plain booleans, so "no advisory" is a written <see langword="false"/> rather
    /// than an absent field. The legacy null test treated a false boolean as absent, which is precisely the
    /// ambiguity a nullable form would have reintroduced here.
    /// </remarks>
    [Fact]
    public async Task SignIn_WithNothingOutstanding_RaisesNoAdvisory()
    {
        Harness harness = Harness.Ready();

        Result<LoginResponse> result = await harness.LoginAsync();

        result.IsSuccess.Should().BeTrue();
        result.Value.MustChangePassword.Should().BeFalse();
        result.Value.PasswordExpiring.Should().BeFalse();
        result.Value.MustUpdateProfile.Should().BeFalse();
    }

    /// <summary>
    /// Every collaborator is required.
    /// </summary>
    /// <remarks>
    /// Driven from the constructor's own parameter list rather than from a hand-written call per position,
    /// so that a collaborator added later is covered without this test being edited and cannot be added
    /// without a guard. The parameter count is asserted first, because a silently shortened argument list
    /// would otherwise make every omission below pass for the wrong reason.
    /// </remarks>
    [Fact]
    public void Service_RequiresEveryCollaborator()
    {
        object[] collaborators =
        [
            new Mock<IUserRepository>().Object,
            new Mock<IPortalRepository>().Object,
            new Mock<IPermissionService>().Object,
            new Mock<ITokenService>().Object,
            new Mock<IPasswordHasher>().Object,
            new Mock<IClock>().Object,
            new Mock<IHostSettingsService>().Object,
            new Mock<IUnitOfWork>().Object,
            new Mock<ICurrentUser>().Object,
            new PasswordPolicyOptions(),
        ];

        ConstructorInfo constructor = typeof(AuthService).GetConstructors().Single();

        constructor.GetParameters().Should().HaveCount(
            collaborators.Length,
            "every constructor parameter must be represented below, or an omission would go untested");

        _ = constructor.Invoke(collaborators);

        for (int omitted = 0; omitted < collaborators.Length; omitted++)
        {
            object?[] withOneMissing = new object?[collaborators.Length];
            Array.Copy(collaborators, withOneMissing, collaborators.Length);
            withOneMissing[omitted] = null;

            TargetInvocationException thrown = Assert.Throws<TargetInvocationException>(
                () => constructor.Invoke(withOneMissing));

            thrown.InnerException.Should().BeOfType<ArgumentNullException>(
                $"the collaborator at position {omitted} is required");
        }
    }

    /// <summary>
    /// Builds the installation-wide host account.
    /// </summary>
    /// <returns>The host account.</returns>
    private static User HostAccount() => new()
    {
        UserId = UserId,
        Username = "host",
        FirstName = "Host",
        LastName = "Account",
        DisplayName = "Host Account",
        Email = "host@example.com",
        IsSuperUser = true,
    };

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

            ScopedAccount = new User
            {
                UserId = UserId,
                Username = AccountName,
                FirstName = "Ada",
                LastName = "Lovelace",
                DisplayName = "Ada Lovelace",
                Email = "ada@example.com",
            };

            IsApproved = true;
            Policy = new PasswordPolicyOptions();
            Users = new Mock<IUserRepository>(MockBehavior.Loose);
            Portals = new Mock<IPortalRepository>(MockBehavior.Loose);
            Permissions = new Mock<IPermissionService>(MockBehavior.Loose);
            Tokens = new Mock<ITokenService>(MockBehavior.Loose);
            PasswordHasher = new Mock<IPasswordHasher>(MockBehavior.Loose);
            Clock = new Mock<IClock>(MockBehavior.Loose);
            HostSettings = new Mock<IHostSettingsService>(MockBehavior.Loose);
            UnitOfWork = new Mock<IUnitOfWork>(MockBehavior.Loose);
            CurrentUser = new Mock<ICurrentUser>(MockBehavior.Loose);

            Service = new AuthService(
                Users.Object,
                Portals.Object,
                Permissions.Object,
                Tokens.Object,
                PasswordHasher.Object,
                Clock.Object,
                HostSettings.Object,
                UnitOfWork.Object,
                CurrentUser.Object,
                Policy);
        }

        public Portal Portal { get; }

        public User? ScopedAccount { get; set; }

        public User? InstallationWideAccount { get; set; }

        public bool IsApproved { get; set; }

        public bool IsLockedOut { get; set; }

        public DateTime? RecordedLoginInstant { get; private set; }

        public DateTime? RehashInstant { get; private set; }

        public PasswordPolicyOptions Policy { get; }

        public Mock<IUserRepository> Users { get; }

        public Mock<IPortalRepository> Portals { get; }

        public Mock<IPermissionService> Permissions { get; }

        public Mock<ITokenService> Tokens { get; }

        public Mock<IPasswordHasher> PasswordHasher { get; }

        public Mock<IClock> Clock { get; }

        public Mock<IHostSettingsService> HostSettings { get; }

        public Mock<IUnitOfWork> UnitOfWork { get; }

        public Mock<ICurrentUser> CurrentUser { get; }

        public AuthService Service { get; }

        /// <summary>
        /// Builds a harness whose collaborators all agree that the sign-in should succeed.
        /// </summary>
        /// <returns>The harness.</returns>
        public static Harness Ready()
        {
            Harness harness = new();

            harness.Clock.SetupGet(clock => clock.UtcNow).Returns(Now);

            harness.Portals
                .Setup(portals => portals.GetByIdAsync(
                    It.IsAny<int>(),
                    It.IsAny<bool>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => harness.Portal);

            harness.Users
                .Setup(users => users.GetByUsernameAsync(
                    It.Is<int?>(portalId => portalId != null),
                    It.IsAny<string>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => harness.ScopedAccount);

            harness.Users
                .Setup(users => users.GetByUsernameAsync(
                    It.Is<int?>(portalId => portalId == null),
                    It.IsAny<string>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => harness.InstallationWideAccount);

            harness.Users
                .Setup(users => users.GetCredentialStateAsync(
                    It.IsAny<int>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => (true, (string?)StoredHash, harness.IsApproved, harness.IsLockedOut));

            harness.Users
                .Setup(users => users.SetApprovalAsync(
                    It.IsAny<int>(),
                    It.IsAny<bool>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(true);

            harness.Users
                .Setup(users => users.RecordSuccessfulLoginAsync(
                    It.IsAny<int>(),
                    It.IsAny<DateTime>(),
                    It.IsAny<CancellationToken>()))
                .Callback<int, DateTime, CancellationToken>(
                    (_, instant, _) => harness.RecordedLoginInstant = instant)
                .ReturnsAsync(true);

            harness.Users
                .Setup(users => users.RecordFailedLoginAsync(
                    It.IsAny<int>(),
                    It.IsAny<int>(),
                    It.IsAny<TimeSpan>(),
                    It.IsAny<DateTime>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(false);

            harness.Users
                .Setup(users => users.SetPasswordHashAsync(
                    It.IsAny<int>(),
                    It.IsAny<string>(),
                    It.IsAny<DateTime>(),
                    It.IsAny<CancellationToken>()))
                .Callback<int, string, DateTime, CancellationToken>(
                    (_, _, instant, _) => harness.RehashInstant = instant)
                .ReturnsAsync(true);

            harness.Users
                .Setup(users => users.ListRoleNamesAsync(
                    It.IsAny<int>(),
                    It.IsAny<int>(),
                    It.IsAny<DateTime>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(Array.Empty<string>());

            harness.Permissions
                .Setup(permissions => permissions.GetEffectivePermissionKeysAsync(
                    It.IsAny<int>(),
                    It.IsAny<int?>(),
                    It.IsAny<int?>(),
                    It.IsAny<int?>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(Result<IReadOnlyList<string>>.Success(Array.Empty<string>()));

            // No credential-expiry window is configured, which is the shipped state: the legacy
            // property defaulted the window to zero when the installation-wide setting was absent,
            // and zero disables the check.
            harness.HostSettings
                .Setup(settings => settings.GetSettingAsync(
                    It.IsAny<string>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync((string?)null);

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
                .ReturnsAsync(() => Result<LoginResponse>.Success(new LoginResponse
                {
                    AccessToken = "access-token",
                    RefreshToken = "refresh-token",
                    ExpiresAtUtc = Now.AddMinutes(60),
                }));

            return harness;
        }

        /// <summary>
        /// Signs in with the supplied particulars.
        /// </summary>
        /// <param name="username">The account name to submit.</param>
        /// <param name="password">The credential to submit.</param>
        /// <param name="verificationCode">The verification code to submit.</param>
        /// <param name="portalId">The tenant to sign in to.</param>
        /// <returns>The outcome.</returns>
        public Task<Result<LoginResponse>> LoginAsync(
            string username = AccountName,
            string password = RawPassword,
            string? verificationCode = null,
            int portalId = PortalId)
            => Service.LoginAsync(
                new LoginRequest
                {
                    PortalId = portalId,
                    Username = username,
                    Password = password,
                    VerificationCode = verificationCode,
                },
                CancellationToken.None);
    }
}
