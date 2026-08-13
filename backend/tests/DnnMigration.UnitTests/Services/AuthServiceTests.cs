using System.Globalization;
using System.Reflection;
using System.Text;
using DnnMigration.Application.Abstractions;
using DnnMigration.Application.Dtos.Auth;
using DnnMigration.Application.Options;
using DnnMigration.Application.Services;
using DnnMigration.Domain.Abstractions.Repositories;
using DnnMigration.Domain.Abstractions.Services;
using DnnMigration.Domain.Common;
using DnnMigration.Domain.Entities;
using DnnMigration.Domain.Enums;
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
/// This suite deliberately does not repeat either.
/// </para>
/// <para>
/// Gate order is the substance of the suite rather than an incidental detail.
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

    /// <summary>
    /// The decoy stored form the hashing abstraction publishes, compared against when there is no real
    /// stored form. Deliberately distinguishable from <see cref="StoredHash"/> so a test can assert WHICH
    /// value the one comparison was made against.
    /// </summary>
    private const string DecoyHash = "$2a$12$decoyhashvalue";

    private const string GenericDenial = "The account name or credential is not correct.";

    private const string RequestInvalidCode = "auth.request_invalid";

    private const string InvalidCredentialsCode = "auth.invalid_credentials";

    private const string LockedOutCode = "auth.locked_out";

    private const string AccountNotApprovedCode = "auth.account_not_approved";

    private const string VerificationRequiredCode = "auth.verification_required";

    private const string VerificationCodeInvalidCode = "auth.verification_code_invalid";

    private const string InsecureAdminPasswordCode = "auth.insecure_admin_password";

    private const string InsecureHostPasswordCode = "auth.insecure_host_password";
    /// <summary>
    /// The reason the token service reports when a token record could not be persisted. It describes
    /// neither the caller nor the presented token, so it is escalated rather than folded into a denial.
    /// </summary>
    private const string TokenStoreUnavailableCode = "TOKEN_STORE_UNAVAILABLE";

    /// <summary>
    /// The single reason every refusal of a presented refresh token reports. The four causes the token
    /// service distinguishes are collapsed into it deliberately.
    /// </summary>
    private const string InvalidRefreshTokenCode = "auth.invalid_refresh_token";

    /// <summary>
    /// Reported when a correct code met a correct credential and the approval could not be written.
    /// Distinct from the three above because nothing the caller submitted was wrong.
    /// </summary>
    private const string ApprovalStoreUnavailableCode = "auth.approval_store_unavailable";

    /// <summary>
    /// Reported when a correct credential was still in its legacy reversible representation and the
    /// replacement that would have retired it could not be written, so the sign-in is refused.
    /// </summary>
    /// <remarks>
    /// Shaped like <see cref="ApprovalStoreUnavailableCode"/> rather than like a credential refusal, and
    /// for the same reason: nothing the caller submitted was wrong. Its reason token ends in
    /// <c>store_unavailable</c>, which the Api edge answers <c>503</c>.
    /// </remarks>
    private const string CredentialMigrationStoreUnavailableCode =
        "auth.credential_migration_store_unavailable";

    private static readonly DateTime Now = new(2026, 8, 2, 12, 0, 0, DateTimeKind.Utc);

    /// <summary>
    /// The authentication contract offers the four public authentication operations plus the authoritative
    /// remediation-state read used by the API authorization gate.
    /// </summary>
    [Fact]
    public void AuthenticationContract_OffersExactlyFiveOperations()
    {
        typeof(IAuthService).GetMethods().Select(member => member.Name).Should().BeEquivalentTo(
            new[]
            {
                nameof(IAuthService.LoginAsync),
                nameof(IAuthService.RefreshAsync),
                nameof(IAuthService.LogoutAsync),
                nameof(IAuthService.EvaluateRemediationAsync),
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
    /// The tenant is read from the request rather than from any ambient source, and its absence is refused
    /// as a malformed request rather than as a rejected credential.
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

    /// <summary>An account found within the tenant is used and the installation-wide read is not attempted.</summary>
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

    /// <summary>A host account signs in to a tenant it is not a member of.</summary>
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

    /// <summary>An ordinary account belonging to another tenant cannot sign in here.</summary>
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
    /// An unapproved registration cannot sign in without its verification code, even with a correct
    /// credential - and the approval outcome is reported, because the credential was proved first.
    /// </summary>
    [Fact]
    public async Task SignIn_RefusesAnUnapprovedRegistrationWithoutItsVerificationCode()
    {
        Harness harness = Harness.Ready();
        harness.IsApproved = false;

        Result<LoginResponse> result = await harness.LoginAsync();

        result.IsFailure.Should().BeTrue();
        result.Reason!.Code.Should().Be(AccountNotApprovedCode);
        harness.PasswordHasher.Verify(
            hasher => hasher.Verify(RawPassword, StoredHash),
            Times.Once());
        harness.Users.Verify(
            users => users.SetApprovalAsync(
                It.IsAny<int>(),
                It.IsAny<bool>(),
                It.IsAny<CancellationToken>()),
            Times.Never());
    }

    /// <summary>
    /// Under verified registration the two verification arms are reported separately, and only to a caller
    /// whose credential has been accepted.
    /// </summary>
    /// <param name="suppliedCode">The code the submission carried.</param>
    /// <param name="expectedCode">The reason code the ladder selects for it.</param>
    [Theory]
    [InlineData(null, VerificationRequiredCode)]
    [InlineData("", VerificationRequiredCode)]
    [InlineData("   ", VerificationCodeInvalidCode)]
    [InlineData("-1-8", VerificationCodeInvalidCode)]
    public async Task SignIn_UnderVerifiedRegistration_ReportsTheApprovalLadder(
        string? suppliedCode,
        string expectedCode)
    {
        Harness harness = Harness.Ready();
        harness.IsApproved = false;
        harness.Portal.UserRegistration = UserRegistrationMode.VerifiedRegistration;

        Result<LoginResponse> result = await harness.LoginAsync(verificationCode: suppliedCode);

        result.IsFailure.Should().BeTrue();
        result.Reason!.Code.Should().Be(expectedCode);
        result.Reason!.Message.Should().NotBeNullOrWhiteSpace(
            "an approval outcome carries actionable wording, unlike the deliberately opaque denial");
        harness.Users.Verify(
            users => users.SetApprovalAsync(
                It.IsAny<int>(),
                It.IsAny<bool>(),
                It.IsAny<CancellationToken>()),
            Times.Never());
    }

    /// <summary>The verification code must match exactly.</summary>
    /// <param name="supplied">The code supplied.</param>
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
        result.Reason!.Code.Should().Be(
            AccountNotApprovedCode,
            "the harness portal registers no sign-up mode, so every mismatch reaches the not-authorised arm");
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
    /// A pending registration whose stored address violates the portal's configured expression is not
    /// approved, even when the predictable legacy verification code is supplied correctly.
    /// </summary>
    [Fact]
    public async Task SignIn_DoesNotApproveAnAddressRejectedByThePortalRule()
    {
        Harness harness = Harness.Ready();
        harness.IsApproved = false;
        harness.Accounts
            .Setup(accounts => accounts.IsEmailValidAsync(
                PortalId,
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<bool>.Success(false));

        Result<LoginResponse> result = await harness.LoginAsync(verificationCode: "-1-7");

        result.IsFailure.Should().BeTrue();
        harness.Users.Verify(
            users => users.SetApprovalAsync(
                It.IsAny<int>(),
                It.IsAny<bool>(),
                It.IsAny<CancellationToken>()),
            Times.Never());
    }

    /// <summary>The verification code is built from the tenant being signed in to and the account resolved.</summary>
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

    /// <summary>A registration whose approval cannot be recorded is refused and nothing is committed.</summary>
    /// <remarks>
    /// The credential is compared BEFORE the approval is attempted, so it is verified exactly once here.
    /// The refusal reports the approval outcome rather than a credential denial, because the credential was
    /// in fact correct and telling this caller otherwise would send it to reset a working credential.
    /// </remarks>
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

        // NOT one of the three approval outcomes, and not the uniform denial either. The caller presented a
        // correct code and a correct credential; what failed was the write.
        result.Reason!.Code.Should().Be(ApprovalStoreUnavailableCode);
        harness.UnitOfWork.Verify(
            unitOfWork => unitOfWork.SaveChangesAsync(It.IsAny<CancellationToken>()),
            Times.Never());

        // The credential is compared once, as it is on every structurally valid attempt; see the note on the
        // verification-code test above for why the previous expectation of no comparison was itself the defect.
        harness.PasswordHasher.Verify(
            hasher => hasher.Verify(It.IsAny<string>(), It.IsAny<string>()),
            Times.Once());
    }

    /// <summary>A host account is not subject to the registration gate.</summary>
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

    /// <summary>The lock is examined before the registration gate.</summary>
    [Fact]
    public async Task SignIn_ExaminesTheLockBeforeTheRegistrationGate()
    {
        Harness harness = Harness.Ready();
        harness.IsApproved = false;
        harness.IsLockedOut = true;
        harness.CurrentUser.SetupGet(caller => caller.IsAuthenticated).Returns(true);
        harness.CurrentUser.SetupGet(caller => caller.UserId).Returns(UserId);
        harness.InstallationWideAccount = HostAccount();

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
        harness.PasswordHasher.Verify(
            hasher => hasher.Verify(It.IsAny<string>(), It.IsAny<string>()),
            Times.Once(),
            "the comparison is unconditional, so a refusal reached here costs what an acceptance costs");
    }

    /// <summary>
    /// A lock that has aged past the installation's automatic-unlock window is cleared and the sign-in
    /// continues.
    /// </summary>
    [Fact]
    public async Task SignIn_ClearsALockThatHasAgedPastTheAutomaticUnlockWindow()
    {
        Harness harness = Harness.Ready();
        harness.IsLockedOut = true;
        harness.ScopedAccount!.IsLockedOut = true;
        harness.ScopedAccount!.LastLockoutDate = Now.AddMinutes(-11);
        harness.Users
            .Setup(users => users.UnlockAsync(UserId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        Result<LoginResponse> result = await harness.LoginAsync();

        result.IsSuccess.Should().BeTrue(result.Reason?.ToString());
        harness.Users.Verify(
            users => users.UnlockAsync(UserId, It.IsAny<CancellationToken>()),
            Times.Once());
        harness.ScopedAccount!.IsLockedOut.Should().BeFalse(
            "the loaded row is kept in step, so nothing later in the request calls the account locked");
    }

    /// <summary>A lock still inside the window is not cleared.</summary>
    /// <remarks>
    /// MIGRATION: as above, the credential comparison is no longer skipped for a locked account, so what is
    /// asserted here is that it costs the same rather than that it is absent. What the fact is about is
    /// unchanged: a lock whose window has not elapsed is left in place and the attempt is refused.
    /// </remarks>
    [Fact]
    public async Task SignIn_LeavesALockThatIsStillInsideTheWindow()
    {
        Harness harness = Harness.Ready();
        harness.IsLockedOut = true;
        harness.ScopedAccount!.LastLockoutDate = Now.AddMinutes(-9);

        Result<LoginResponse> result = await harness.LoginAsync();

        result.IsFailure.Should().BeTrue();
        harness.Users.Verify(
            users => users.UnlockAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.Never());
        harness.PasswordHasher.Verify(
            hasher => hasher.Verify(It.IsAny<string>(), It.IsAny<string>()),
            Times.Once(),
            "the comparison is unconditional, so a lock cannot be detected from how long the refusal took");
    }

    /// <summary>
    /// An explicit zero duration switches automatic unlocking off altogether, and a negative one is treated
    /// the same way rather than unlocking instantly.
    /// </summary>
    /// <param name="configured">The configured duration.</param>
    [Theory]
    [InlineData("0")]
    [InlineData("-30")]
    public async Task SignIn_DoesNotUnlockWhenTheWindowIsZeroOrNegative(string configured)
    {
        Harness harness = Harness.Ready();
        harness.IsLockedOut = true;
        harness.ScopedAccount!.LastLockoutDate = Now.AddYears(-1);
        harness.HostSettings
            .Setup(settings => settings.GetSettingAsync(
                "AutoAccountUnlockDuration",
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(configured);

        Result<LoginResponse> result = await harness.LoginAsync();

        result.IsFailure.Should().BeTrue();
        harness.Users.Verify(
            users => users.UnlockAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.Never());
    }

    /// <summary>An account whose lock instant is unknown is not unlocked.</summary>
    /// <remarks>
    /// With no instant there is no window to measure. Unlocking on an unknown would turn a missing fact
    /// into an open door, so the lock stands and an administrator clears it.
    /// </remarks>
    [Fact]
    public async Task SignIn_DoesNotUnlockWhenTheLockInstantIsUnknown()
    {
        Harness harness = Harness.Ready();
        harness.IsLockedOut = true;
        harness.ScopedAccount!.LastLockoutDate = null;

        Result<LoginResponse> result = await harness.LoginAsync();

        result.IsFailure.Should().BeTrue();
        harness.Users.Verify(
            users => users.UnlockAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.Never());
    }

    /// <summary>A refused sign-in writes nothing beyond the failure it is required to record.</summary>
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
                It.IsAny<string?>(),
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
                It.IsAny<CancellationToken>()),
            Times.Never());
    }

    /// <summary>A successful sign-in records the login once, at the instant the clock reports.</summary>
    [Fact]
    public async Task SignIn_RecordsTheLoginOnceAtTheClockInstant()
    {
        Harness harness = Harness.Ready();

        await harness.LoginAsync();

        harness.Users.Verify(
            users => users.RecordSuccessfulLoginAsync(UserId, Now, It.IsAny<CancellationToken>()),
            Times.Once());
    }

    /// <summary>The login timestamp and any re-hash share one instant.</summary>
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
    /// A legacy credential accepted during the compatibility window is replaced with BCrypt before the
    /// session is issued and the migration is audited without credential material.
    /// </summary>
    [Fact]
    public async Task SignIn_MigratesAnAcceptedLegacyCredentialOnItsFirstLogin()
    {
        const string legacyValue = "legacy-encrypted-value";
        const string legacySalt = "legacy-salt";
        const string replacement = "$2a$12$replacement-value";
        Harness harness = Harness.Ready();
        harness.StoredCredential = legacyValue;
        harness.CredentialFormat = PasswordFormat.Encrypted;
        harness.CredentialSalt = legacySalt;
        harness.CredentialMatches = false;
        harness.LegacyCredentialMatches = true;
        harness.PasswordHasher.Setup(hasher => hasher.Hash(RawPassword)).Returns(replacement);

        Result<LoginResponse> result = await harness.LoginAsync();

        result.IsSuccess.Should().BeTrue();
        harness.PasswordHasher.Verify(
            hasher => hasher.Verify(RawPassword, DecoyHash),
            Times.Once(),
            "a legacy row still pays the same current-cost comparison as every other attempt");
        harness.LegacyCredentials.Verify(
            verifier => verifier.Verify(
                RawPassword,
                legacyValue,
                PasswordFormat.Encrypted,
                legacySalt),
            Times.Once());
        harness.PasswordHasher.Verify(
            hasher => hasher.NeedsRehash(It.IsAny<string>()),
            Times.Never(),
            "legacy classification belongs to the compatibility verifier, not the BCrypt cost check");
        harness.Users.Verify(
            users => users.SetPasswordHashAsync(
                UserId,
                replacement,
                It.IsAny<string?>(),
                Now,
                It.IsAny<CancellationToken>()),
            Times.Once());

        AuditEvent migration = harness.AuditRecords
            .Should().ContainSingle(record => record.EventName == AuditEventNames.LegacyCredentialMigrated)
            .Subject;

        migration.PortalId.Should().Be(PortalId);
        migration.ActorUserId.Should().Be(UserId);
        migration.SubjectUserId.Should().Be(UserId);
        migration.Properties.Should().ContainSingle()
            .Which.Should().Be(
                new KeyValuePair<string, string?>("PreviousFormat", nameof(PasswordFormat.Encrypted)));

        IEnumerable<string?> recordedValues = migration.Properties.Values;
        recordedValues.Should().NotContain(value =>
            value != null
            && (value.Contains(RawPassword, StringComparison.Ordinal)
                || value.Contains(legacyValue, StringComparison.Ordinal)
                || value.Contains(legacySalt, StringComparison.Ordinal)
                || value.Contains(replacement, StringComparison.Ordinal)));
    }

    /// <summary>A legacy representation that the bounded verifier refuses is neither replaced nor audited.</summary>
    [Fact]
    public async Task SignIn_WhenLegacyVerificationRefuses_WritesNothing()
    {
        Harness harness = Harness.Ready();
        harness.StoredCredential = "legacy-encrypted-value";
        harness.CredentialFormat = PasswordFormat.Encrypted;
        harness.CredentialSalt = "legacy-salt";
        harness.CredentialMatches = false;
        harness.LegacyCredentialMatches = false;

        Result<LoginResponse> result = await harness.LoginAsync();

        result.IsFailure.Should().BeTrue();
        harness.Users.Verify(
            users => users.SetPasswordHashAsync(
                It.IsAny<int>(),
                It.IsAny<string>(),
                It.IsAny<string?>(),
                It.IsAny<DateTime>(),
                It.IsAny<CancellationToken>()),
            Times.Never());
        harness.AuditRecords.Should().NotContain(
            record => record.EventName == AuditEventNames.LegacyCredentialMigrated);
    }

    /// <summary>
    /// A proven legacy credential whose immediate replacement the store refuses is refused a session, and
    /// the distinct deadline-sensitive anomaly is recorded.
    /// </summary>
    /// <remarks>
    /// So the refusal is asserted here instead, and it is asserted to be a NAMED DEPENDENCY FAILURE rather
    /// than the uniform credential denial: nothing the caller submitted was wrong, and reporting a
    /// credential refusal would send an account holder hunting for a mistake it did not make.
    /// </remarks>
    [Fact]
    public async Task SignIn_WhenLegacyReplacementFails_RefusesTheSignIn()
    {
        Harness harness = Harness.Ready();
        harness.StoredCredential = "legacy-encrypted-value";
        harness.CredentialFormat = PasswordFormat.Encrypted;
        harness.CredentialSalt = "legacy-salt";
        harness.CredentialMatches = false;
        harness.LegacyCredentialMatches = true;
        harness.CredentialUpgradeAccepted = CredentialWriteOutcome.NoRecord;
        harness.PasswordHasher.Setup(hasher => hasher.Hash(RawPassword)).Returns("$2a$12$replacement-value");

        Result<LoginResponse> result = await harness.LoginAsync();

        result.IsFailure.Should().BeTrue(
            "a credential that is still in its legacy reversible form must not mint a session when the "
            + "replacement that would have retired it could not be written");
        result.Reason!.Code.Should().Be(CredentialMigrationStoreUnavailableCode);
        result.Reason!.Message.Should().NotBe(
            GenericDenial,
            "nothing the caller submitted was wrong, so this must not be reported as a credential refusal");

        harness.Diagnostics.Verify(
            diagnostics => diagnostics.Record(
                SecurityDiagnosticEvent.LegacyCredentialMigrationFailed,
                PortalId,
                UserId,
                It.IsAny<string?>()),
            Times.Once());
        harness.AuditRecords.Should().NotContain(
            record => record.EventName == AuditEventNames.LegacyCredentialMigrated);

        // No session is minted, which is the whole point of the refusal: the earlier behaviour issued one.
        harness.Tokens.Verify(
            tokens => tokens.IssueTokensAsync(
                It.IsAny<int>(),
                It.IsAny<int>(),
                It.IsAny<CancellationToken>()),
            Times.Never());
    }

    /// <summary>
    /// A credential replaced by somebody else after this sign-in verified it, and before a session is
    /// minted from it, is refused with the uniform denial.
    /// </summary>
    /// <param name="credentialEmptiedOutright">
    /// Whether the later read reports no credential at all rather than a different one.
    /// </param>
    /// <returns>A task representing the assertion.</returns>
    /// <remarks>
    /// THIS IS THE RACE THE SIGN-IN PATH EXISTS TO LOSE SAFELY, and it is not hypothetical. Everything a
    /// sign-in decides comes from ONE credential read taken before the comparison, and that comparison is
    /// the one deliberately expensive step on the path - so the interval between reading the credential and
    /// issuing a session is wide enough to matter.
    /// </remarks>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SignIn_RefusesASessionWhenTheCredentialChangedAfterItWasVerified(
        bool credentialEmptiedOutright)
    {
        Harness harness = Harness.Ready();
        harness.CredentialChangesAfterVerification = true;
        harness.CredentialValueAfterVerification = credentialEmptiedOutright
            ? null
            : "$2a$12$reset-by-an-administrator";

        Result<LoginResponse> result = await harness.LoginAsync();

        result.IsFailure.Should().BeTrue(
            "the representation this sign-in verified is not the account's credential any more");
        result.Reason!.Code.Should().Be(InvalidCredentialsCode);
        result.Reason!.Message.Should().Be(
            GenericDenial,
            "the condition is attacker-influenceable, so it must be indistinguishable from a wrong credential");

        harness.CredentialStateReads.Should().BeGreaterThanOrEqualTo(
            2,
            "the refusal must come from a second look at the credential, not from the read that preceded the "
            + "comparison");

        harness.Diagnostics.Verify(
            diagnostics => diagnostics.Record(
                SecurityDiagnosticEvent.CredentialChangedDuringSignIn,
                PortalId,
                UserId,
                It.IsAny<string?>()),
            Times.Once());

        harness.Tokens.Verify(
            tokens => tokens.IssueTokensAsync(
                It.IsAny<int>(),
                It.IsAny<int>(),
                It.IsAny<CancellationToken>()),
            Times.Never());
    }

    /// <summary>
    /// A credential replaced after the session was minted has that session revoked and the sign-in refused.
    /// </summary>
    /// <param name="revocationStoreRefuses">Whether the token store can record the revocation.</param>
    /// <returns>A task representing the assertion.</returns>
    /// <remarks>
    /// REVOKING IS THE OPERATIVE HALF, not the refusal. The family exists in the store by the time this
    /// runs; returning a failure alone would leave the caller holding exchangeable material, which is
    /// precisely what an administrator's reset is performed to end.
    /// </remarks>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SignIn_RefusesAndRevokesWhenTheCredentialChangedAfterTheSessionWasMinted(
        bool revocationStoreRefuses)
    {
        Harness harness = Harness.Ready();
        harness.CredentialChangesAfterVerification = true;
        harness.CredentialChangesFromRead = 3;
        harness.SessionsRevoked = !revocationStoreRefuses;

        Result<LoginResponse> result = await harness.LoginAsync();

        result.IsFailure.Should().BeTrue(
            "a session minted from a credential that has since been replaced must not be handed to its caller");

        harness.CredentialStateReads.Should().Be(
            3,
            "the sign-in reads the credential once before the comparison, once before issuance and once after "
            + "it, and this refusal must come from the third");

        // The family WAS minted - that is the situation this closes - and it is ended rather than orphaned.
        harness.Tokens.Verify(
            tokens => tokens.IssueTokensAsync(UserId, PortalId, It.IsAny<CancellationToken>()),
            Times.Once());
        harness.RevokedSessionUserIds.Should().Equal(new[] { UserId });

        harness.Diagnostics.Verify(
            diagnostics => diagnostics.Record(
                SecurityDiagnosticEvent.CredentialChangedDuringSignIn,
                PortalId,
                UserId,
                It.IsAny<string?>()),
            Times.Once());

        if (revocationStoreRefuses)
        {
            result.Reason!.Code.Should().Be(
                TokenStoreUnavailableCode,
                "a revocation that could not be persisted is a dependency failure, and reporting it as a "
                + "credential refusal would leave the family exchangeable while blaming the caller");
        }
        else
        {
            result.Reason!.Code.Should().Be(InvalidCredentialsCode);
            result.Reason!.Message.Should().Be(GenericDenial);
        }
    }

    /// <summary>
    /// The second look accepts the representation this sign-in itself wrote, so a work-factor upgrade does
    /// not refuse its own session.
    /// </summary>
    /// <remarks>
    /// The negative half of the test above, and it is what makes that one evidence of anything rather than
    /// a check that refuses whenever the credential moved. A sign-in that upgrades the stored cost
    /// legitimately changes the credential mid-request, so the second look admits two values: the
    /// representation it verified, and the representation it wrote.
    /// </remarks>
    [Fact]
    public async Task SignIn_AcceptsTheCredentialThisRequestItselfWrote()
    {
        const string Replacement = "$2a$12$replacement-value";

        Harness harness = Harness.Ready();
        harness.PasswordHasher.Setup(hasher => hasher.NeedsRehash(It.IsAny<string>())).Returns(true);
        harness.PasswordHasher.Setup(hasher => hasher.Hash(RawPassword)).Returns(Replacement);
        harness.CredentialChangesAfterVerification = true;
        harness.CredentialValueAfterVerification = Replacement;

        Result<LoginResponse> result = await harness.LoginAsync();

        result.IsSuccess.Should().BeTrue(result.Reason?.ToString());
        harness.RehashExpectation.Should().Be(
            StoredHash,
            "the replacement must be conditional on the representation this sign-in verified, or it would "
            + "overwrite a reset performed while this request was in flight");

        harness.Diagnostics.Verify(
            diagnostics => diagnostics.Record(
                SecurityDiagnosticEvent.CredentialChangedDuringSignIn,
                It.IsAny<int?>(),
                It.IsAny<int?>(),
                It.IsAny<string?>()),
            Times.Never());
    }

    /// <summary>
    /// A replacement the store refuses because the credential changed under it denies the sign-in rather
    /// than treating the refusal as a transient store fault.
    /// </summary>
    /// <param name="legacyCredential">
    /// Whether the replacement is a legacy migration rather than a cost upgrade.
    /// </param>
    /// <returns>A task representing the assertion.</returns>
    /// <remarks>
    /// THE STORE REFUSED THIS WRITE ON PURPOSE, and telling that apart from a store that could not accept
    /// it is the whole reason the write reports a closed outcome rather than a boolean.
    /// </remarks>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SignIn_RefusesASessionWhenTheStoreRefusesAReplacementBecauseTheCredentialChanged(
        bool legacyCredential)
    {
        Harness harness = Harness.Ready();
        harness.CredentialUpgradeAccepted = CredentialWriteOutcome.Superseded;
        harness.PasswordHasher.Setup(hasher => hasher.Hash(RawPassword)).Returns("$2a$12$replacement-value");

        if (legacyCredential)
        {
            harness.StoredCredential = "legacy-encrypted-value";
            harness.CredentialFormat = PasswordFormat.Encrypted;
            harness.CredentialSalt = "legacy-salt";
            harness.CredentialMatches = false;
            harness.LegacyCredentialMatches = true;
        }
        else
        {
            harness.PasswordHasher.Setup(hasher => hasher.NeedsRehash(It.IsAny<string>())).Returns(true);
        }

        Result<LoginResponse> result = await harness.LoginAsync();

        result.IsFailure.Should().BeTrue(
            "the store refused the replacement because the credential is no longer the one that was verified");
        result.Reason!.Code.Should().Be(InvalidCredentialsCode);
        result.Reason!.Message.Should().Be(GenericDenial);

        harness.Diagnostics.Verify(
            diagnostics => diagnostics.Record(
                SecurityDiagnosticEvent.CredentialChangedDuringSignIn,
                PortalId,
                UserId,
                It.IsAny<string?>()),
            Times.Once());

        // NOT reported as a store fault, which is the distinction the closed outcome buys.
        harness.Diagnostics.Verify(
            diagnostics => diagnostics.Record(
                SecurityDiagnosticEvent.CredentialWorkFactorUpgradeFailed,
                It.IsAny<int?>(),
                It.IsAny<int?>(),
                It.IsAny<string?>()),
            Times.Never());
        harness.Diagnostics.Verify(
            diagnostics => diagnostics.Record(
                SecurityDiagnosticEvent.LegacyCredentialMigrationFailed,
                It.IsAny<int?>(),
                It.IsAny<int?>(),
                It.IsAny<string?>()),
            Times.Never());

        harness.Tokens.Verify(
            tokens => tokens.IssueTokensAsync(
                It.IsAny<int>(),
                It.IsAny<int>(),
                It.IsAny<CancellationToken>()),
            Times.Never());
        harness.AuditRecords.Should().NotContain(
            record => record.EventName == AuditEventNames.LegacyCredentialMigrated);
    }

    /// <summary>
    /// A legacy migration carries the legacy representation it verified as the write's expectation.
    /// </summary>
    [Fact]
    public async Task SignIn_MigratingALegacyCredential_CarriesTheVerifiedRepresentationAsTheExpectation()
    {
        const string LegacyValue = "legacy-encrypted-value";

        Harness harness = Harness.Ready();
        harness.StoredCredential = LegacyValue;
        harness.CredentialFormat = PasswordFormat.Encrypted;
        harness.CredentialSalt = "legacy-salt";
        harness.CredentialMatches = false;
        harness.LegacyCredentialMatches = true;
        harness.PasswordHasher.Setup(hasher => hasher.Hash(RawPassword)).Returns("$2a$12$replacement-value");
        harness.CredentialChangesAfterVerification = true;
        harness.CredentialValueAfterVerification = "$2a$12$replacement-value";

        Result<LoginResponse> result = await harness.LoginAsync();

        result.IsSuccess.Should().BeTrue(result.Reason?.ToString());
        harness.RehashExpectation.Should().Be(LegacyValue);
        harness.AuditRecords.Should().Contain(
            record => record.EventName == AuditEventNames.LegacyCredentialMigrated);
    }

    /// <summary>
    /// An out-of-date credential cost is regenerated from the submitted credential and stored once.
    /// </summary>
    /// <remarks>
    /// Two facts are asserted together because either alone would permit a serious defect. The value
    /// written must be regenerated from the SUBMITTED plaintext, since that is the only place the correct
    /// credential exists at this point -- regenerating from the stored value would produce a hash of a
    /// hash, and the account would become unopenable at the next sign-in.
    /// </remarks>
    [Fact]
    public async Task SignIn_UpgradesAnOutOfDateCredentialCostOnceFromTheSubmittedCredential()
    {
        const string regenerated = "$2a$12$regeneratedvalue";
        Harness harness = Harness.Ready();
        harness.PasswordHasher.Setup(hasher => hasher.NeedsRehash(StoredHash)).Returns(true);
        harness.PasswordHasher.Setup(hasher => hasher.Hash(It.IsAny<string>())).Returns(regenerated);

        Result<LoginResponse> result = await harness.LoginAsync();

        result.IsSuccess.Should().BeTrue();
        harness.PasswordHasher.Verify(
            hasher => hasher.Hash(RawPassword),
            Times.Once());
        harness.PasswordHasher.Verify(
            hasher => hasher.Hash(StoredHash),
            Times.Never());
        harness.Users.Verify(
            users => users.SetPasswordHashAsync(
                UserId,
                regenerated,
                It.IsAny<string?>(),
                It.IsAny<DateTime>(),
                It.IsAny<CancellationToken>()),
            Times.Once());
    }

    /// <summary>A credential already at the current cost is neither regenerated nor rewritten.</summary>
    [Fact]
    public async Task SignIn_LeavesACredentialAlreadyAtTheCurrentCostUntouched()
    {
        Harness harness = Harness.Ready();

        Result<LoginResponse> result = await harness.LoginAsync();

        result.IsSuccess.Should().BeTrue();
        harness.PasswordHasher.Verify(
            hasher => hasher.Hash(It.IsAny<string>()),
            Times.Never());
        harness.Users.Verify(
            users => users.SetPasswordHashAsync(
                It.IsAny<int>(),
                It.IsAny<string>(),
                It.IsAny<string?>(),
                It.IsAny<DateTime>(),
                It.IsAny<CancellationToken>()),
            Times.Never());
    }

    /// <summary>A refused credential's stored value is not even examined for staleness.</summary>
    /// <remarks>
    /// The order is what makes the upgrade path safe, and this is the assertion that pins it. Staleness is
    /// consulted only after current-scheme verification has succeeded; legacy classification belongs to the
    /// separate bounded verifier and likewise cannot reach replacement until it has accepted the
    /// credential.
    /// </remarks>
    [Fact]
    public async Task SignIn_DoesNotExamineTheStoredValueForStalenessWhenTheCredentialWasRefused()
    {
        Harness harness = Harness.Ready();
        harness.PasswordHasher
            .Setup(hasher => hasher.Verify(It.IsAny<string>(), It.IsAny<string>()))
            .Returns(false);

        Result<LoginResponse> result = await harness.LoginAsync();

        result.IsFailure.Should().BeTrue();
        harness.PasswordHasher.Verify(
            hasher => hasher.NeedsRehash(It.IsAny<string>()),
            Times.Never());
        harness.PasswordHasher.Verify(
            hasher => hasher.Hash(It.IsAny<string>()),
            Times.Never());
    }

    /// <summary>
    /// A credential-cost upgrade that cannot be persisted still signs the caller in, and now leaves a
    /// security event behind instead of nothing at all.
    /// </summary>
    [Fact]
    public async Task SignIn_RecordsAFailedCredentialCostUpgradeWithoutFailingTheSignIn()
    {
        Harness harness = Harness.Ready();
        harness.PasswordHasher.Setup(hasher => hasher.NeedsRehash(It.IsAny<string>())).Returns(true);
        harness.PasswordHasher.Setup(hasher => hasher.Hash(It.IsAny<string>())).Returns("rehashed");
        harness.Users
            .Setup(users => users.SetPasswordHashAsync(
                It.IsAny<int>(),
                It.IsAny<string>(),
                It.IsAny<string?>(),
                It.IsAny<DateTime>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new TimeoutException("the store did not answer"));

        Result<LoginResponse> result = await harness.LoginAsync();

        result.IsSuccess.Should().BeTrue("a correct credential signs in whether or not its cost upgrades");

        AuditEvent record = harness.AuditRecords
            .Should().ContainSingle(entry => entry.EventName == "PASSWORD_REHASH_FAILURE").Subject;

        record.Outcome.Should().Be(AuditOutcome.Failed, "which the sink raises to warning level");
        record.SubjectUserId.Should().Be(UserId);
        record.FailureCode.Should().Be(
            "credential_replacement_store_failure",
            "the audit column holds a stable code naming the condition, not the type the library happened to throw");
        record.Properties.Should().ContainSingle()
            .Which.Should().Be(
                new KeyValuePair<string, string?>("ReplacementKind", "WorkFactorUpgrade"));

        harness.Diagnostics.Verify(
            diagnostics => diagnostics.Record(
                SecurityDiagnosticEvent.CredentialWorkFactorUpgradeFailed,
                PortalId,
                UserId,
                nameof(TimeoutException)),
            Times.Once());
    }

    /// <summary>A cancelled credential-cost upgrade propagates rather than being recorded as a failure.</summary>
    /// <remarks>
    /// A cancelled request is not a failed write, and recording it as one would fill the trail with events
    /// describing callers who simply navigated away. The containment's exception filter excludes
    /// cancellation for that reason and this test pins it.
    /// </remarks>
    [Fact]
    public async Task SignIn_DoesNotRecordACancelledCredentialCostUpgrade()
    {
        Harness harness = Harness.Ready();
        harness.PasswordHasher.Setup(hasher => hasher.NeedsRehash(It.IsAny<string>())).Returns(true);
        harness.PasswordHasher.Setup(hasher => hasher.Hash(It.IsAny<string>())).Returns("rehashed");
        harness.Users
            .Setup(users => users.SetPasswordHashAsync(
                It.IsAny<int>(),
                It.IsAny<string>(),
                It.IsAny<string?>(),
                It.IsAny<DateTime>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => harness.LoginAsync());

        harness.AuditRecords.Should().NotContain(entry => entry.EventName == "PASSWORD_REHASH_FAILURE");
    }

    /// <summary>An accepted sign-in is recorded under the legacy success event name, with the real account.</summary>
    [Fact]
    public async Task SignIn_RecordsTheLegacySuccessEvent()
    {
        Harness harness = Harness.Ready();

        Result<LoginResponse> result = await harness.LoginAsync();

        result.IsSuccess.Should().BeTrue();

        AuditEvent record = harness.AuditRecords.Should().ContainSingle().Subject;

        record.EventName.Should().Be("LOGIN_SUCCESS");
        record.Outcome.Should().Be(AuditOutcome.Succeeded);
        record.PortalId.Should().Be(PortalId);
        record.ActorUserId.Should().Be(UserId);
        record.SubjectUserId.Should().Be(UserId);
        record.FailureCode.Should().BeNull();
        record.Properties.Should().NotContainKey("Username", "stable account identifiers replace retained names");
        record.Properties.Should().NotContainKey("Password");
    }

    /// <summary>
    /// A host account's sign-in is recorded under its own legacy event name, so the two accepted outcomes
    /// stay distinguishable in the trail without reading a property.
    /// </summary>
    [Fact]
    public async Task SignIn_RecordsTheLegacySuperUserEventForAHostAccount()
    {
        Harness harness = Harness.Ready();
        harness.ScopedAccount = HostAccount();

        Result<LoginResponse> result = await harness.LoginAsync();

        result.IsSuccess.Should().BeTrue();
        harness.AuditRecords.Should().ContainSingle().Which.EventName.Should().Be("LOGIN_SUPERUSER");
    }

    /// <summary>
    /// A rejected credential is recorded under the legacy failure event name, NAMING THE ACCOUNT - which
    /// the legacy trail could not do.
    /// </summary>
    [Fact]
    public async Task SignIn_RecordsTheLegacyFailureEventNamingTheAccount()
    {
        Harness harness = Harness.Ready();
        harness.PasswordHasher
            .Setup(hasher => hasher.Verify(It.IsAny<string>(), It.IsAny<string>()))
            .Returns(false);

        Result<LoginResponse> result = await harness.LoginAsync();

        result.IsFailure.Should().BeTrue();

        AuditEvent record = harness.AuditRecords.Should().ContainSingle().Subject;

        record.EventName.Should().Be("LOGIN_FAILURE");
        record.Outcome.Should().Be(AuditOutcome.Denied);
        record.ActorUserId.Should().Be(UserId, "and not the legacy minus-one sentinel");
        record.FailureCode.Should().Be("credential_rejected");
    }

    /// <summary>A locked account's refusal is recorded under the legacy locked-out event name.</summary>
    [Fact]
    public async Task SignIn_RecordsTheLegacyLockedOutEvent()
    {
        Harness harness = Harness.Ready();
        harness.IsLockedOut = true;

        Result<LoginResponse> result = await harness.LoginAsync();

        result.IsFailure.Should().BeTrue();
        harness.AuditRecords.Should().ContainSingle().Which.EventName.Should().Be("LOGIN_USERLOCKEDOUT");
    }

    /// <summary>An unapproved account's refusal is recorded under the legacy not-approved event name.</summary>
    [Fact]
    public async Task SignIn_RecordsTheLegacyNotApprovedEvent()
    {
        Harness harness = Harness.Ready();
        harness.IsApproved = false;

        Result<LoginResponse> result = await harness.LoginAsync();

        result.IsFailure.Should().BeTrue();
        result.Reason!.Code.Should().Be(
            AccountNotApprovedCode,
            "the ladder sits behind an accepted credential, so it names the gate that refused");

        AuditEvent record = harness.AuditRecords.Should().ContainSingle().Subject;

        record.EventName.Should().Be("LOGIN_USERNOTAPPROVED");
        record.FailureCode.Should().Be("not_approved");
    }

    /// <summary>Signing out is recorded under the net-new session-ended event name.</summary>
    [Fact]
    public async Task SignOut_RecordsTheSessionEnding()
    {
        Harness harness = Harness.Ready();
        harness.CurrentUser.SetupGet(caller => caller.IsAuthenticated).Returns(true);
        harness.CurrentUser.SetupGet(caller => caller.UserId).Returns(UserId);
        harness.CurrentUser.SetupGet(caller => caller.UserName).Returns(AccountName);
        harness.CurrentUser.SetupGet(caller => caller.PortalId).Returns(PortalId);
        harness.Tokens
            .Setup(tokens => tokens.RevokeRefreshTokenAsync(
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success());

        Result result = await harness.Service.LogoutAsync(
            new RefreshTokenRequest { RefreshToken = "a-token" },
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();

        AuditEvent record = harness.AuditRecords.Should().ContainSingle().Subject;

        record.EventName.Should().Be("SESSION_ENDED");
        record.ActorUserId.Should().Be(UserId);
        record.PortalId.Should().Be(PortalId);
    }

    /// <summary>
    /// A sign-out the token store could not complete records nothing, because the session did not end.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task SignOut_RecordsNothingWhenTheTokenStoreCouldNotRevoke()
    {
        Harness harness = Harness.Ready();
        harness.CurrentUser.SetupGet(caller => caller.IsAuthenticated).Returns(true);
        harness.CurrentUser.SetupGet(caller => caller.UserId).Returns(UserId);
        harness.CurrentUser.SetupGet(caller => caller.PortalId).Returns(PortalId);
        harness.Tokens
            .Setup(tokens => tokens.RevokeRefreshTokenAsync(
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Failure(TokenStoreUnavailableCode, "The token store could not be written."));

        Result result = await harness.Service.LogoutAsync(
            new RefreshTokenRequest { RefreshToken = "a-token" },
            CancellationToken.None);

        result.IsFailure.Should().BeTrue("the presented token can still mint successors");
        result.Reason!.Code.Should().Be(TokenStoreUnavailableCode);

        harness.AuditRecords.Should().BeEmpty(
            "the refresh token was not revoked, so no record may assert that the session ended");
    }

    /// <summary>
    /// A retirement the store could not prove is refused and is recorded nowhere, and the refusal discloses
    /// nothing about whether the presented value existed.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    /// <remarks>
    /// ⚠ THIS FACT ASSERTED THE OPPOSITE AND THE EXPECTATION IS WITHDRAWN, because the reading it rested on
    /// was shown to be unsound. It required an unrecognised token to be reported as a completed sign-out,
    /// on the reading that a value the store cannot find can no longer mint a successor.
    /// </remarks>
    [Fact]
    public async Task SignOut_RefusesAndRecordsNothingWhenTheRetirementCouldNotBeProven()
    {
        Harness harness = Harness.Ready();
        harness.CurrentUser.SetupGet(caller => caller.IsAuthenticated).Returns(true);
        harness.CurrentUser.SetupGet(caller => caller.UserId).Returns(UserId);
        harness.CurrentUser.SetupGet(caller => caller.PortalId).Returns(PortalId);
        harness.Tokens
            .Setup(tokens => tokens.RevokeRefreshTokenAsync(
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Failure(
                "REFRESH_TOKEN_NOTFOUND",
                "This instance holds no such refresh-token family, so no session can be confirmed as "
                + "retired. Retain the credential and retry."));

        Result result = await harness.Service.LogoutAsync(
            new RefreshTokenRequest { RefreshToken = "a-token" },
            CancellationToken.None);

        result.IsFailure.Should().BeTrue(
            "a retirement this instance cannot prove may still leave a live session behind, and the client "
            + "must keep the credential rather than discard it");

        result.Reason!.Code.Should().Be(
            "SESSION_REVOCATION_STORE_UNAVAILABLE",
            "the transport maps this one code to the answer that tells a client to retain its credential");

        result.Reason!.Message.Should().NotContain(
            "a-token",
            "a refusal may not quote the value presented to it");
        result.Reason!.Message.Should().NotContainEquivalentOf(
            "not found",
            "the wording must not distinguish a value that never existed from a session this instance cannot "
            + "reach, or the refusal becomes an oracle for whether a guessed token exists");
        result.Reason!.Message.Should().NotContainEquivalentOf(
            "unknown",
            "for the same reason: the two conditions must be indistinguishable from outside");

        harness.AuditRecords.Should().BeEmpty(
            "the retirement was not confirmed, so no record may assert that the session ended");
    }

    /// <summary>
    /// The caller's network address cannot influence the outcome, because it never reaches this layer.
    /// </summary>
    /// <remarks>
    /// The legacy call site supplied an address as its seventh argument and the legacy service did nothing
    /// with it but record it. It is now recorded by the Api layer's structured request log instead, and is
    /// absent from this contract altogether - which is a stronger guarantee than accepting and ignoring it,
    /// because an address that could grant or deny access would be an input a caller controls.
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

    /// <summary>An approved registration that also used a shipped credential carries the advisory.</summary>
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
    }

    /// <summary>
    /// A forced credential update recorded on the account row reaches the response as the advisory boolean.
    /// </summary>
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
    /// An ordinary sign-in raises no remediation requirement or expiry advisory, and says so explicitly
    /// rather than by omission.
    /// </summary>
    [Fact]
    public async Task SignIn_WithNothingOutstanding_RaisesNoAdvisory()
    {
        Harness harness = Harness.Ready();

        Result<LoginResponse> result = await harness.LoginAsync();

        result.IsSuccess.Should().BeTrue();
        result.Value.MustChangePassword.Should().BeFalse();
        result.Value.PasswordExpiring.Should().BeFalse();
    }

    /// <summary>Every sign-in outcome is audited, and the outcome name is the legacy log type key.</summary>
    /// <returns>A task representing the assertion.</returns>
    /// <remarks>
    /// The tenant and resolved account identifier are asserted with it. Submitted names are deliberately
    /// excluded: unresolved probes remain anonymous rather than acquiring a second retention lifecycle in
    /// the audit store.
    /// </remarks>
    [Fact]
    public async Task SignIn_AuditsEveryOutcomeUnderItsLegacyName()
    {
        Harness accepted = Harness.Ready();
        await accepted.LoginAsync();

        accepted.AuditRecords.Should().ContainSingle().Which.Should().Match<AuditEvent>(entry =>
            entry.EventName == AuditEventNames.LoginSuccess
            && entry.PortalId == PortalId
            && entry.ActorUserId == UserId);

        Harness locked = Harness.Ready();
        locked.IsLockedOut = true;
        await locked.LoginAsync();

        locked.AuditRecords.Should().ContainSingle().Which.Should().Match<AuditEvent>(entry =>
            entry.EventName == AuditEventNames.LoginUserLockedOut && entry.ActorUserId == UserId);

        // The refusal is arranged by making the hasher disagree rather than by submitting different text,
        // because the harness's hasher accepts any pair by default - so a different password alone would
        // still verify and this case would silently assert the accepted outcome again.
        Harness wrongCredential = Harness.Ready();
        wrongCredential.PasswordHasher
            .Setup(hasher => hasher.Verify(It.IsAny<string>(), It.IsAny<string>()))
            .Returns(false);

        await wrongCredential.LoginAsync();

        wrongCredential.AuditRecords.Should().ContainSingle().Which.Should().Match<AuditEvent>(entry =>
            entry.EventName == AuditEventNames.LoginFailure && entry.ActorUserId == UserId);
    }

    /// <summary>
    /// An attempt against an account name that matches nothing is audited, with no account identifier.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    /// <remarks>
    /// The legacy audited this case and could not describe it: it always passed its integer absence
    /// sentinel as the account identifier, so a run of failures against one account and a run against many
    /// names looked identical in the trail.
    /// </remarks>
    [Fact]
    public async Task SignIn_AuditsAnUnknownAccountNameWithoutAnIdentifier()
    {
        Harness harness = Harness.Ready();
        harness.ScopedAccount = null;
        harness.InstallationWideAccount = null;

        await harness.LoginAsync(username: "no-such-account");

        harness.AuditRecords.Should().ContainSingle().Which.Should().Match<AuditEvent>(entry =>
            entry.EventName == AuditEventNames.LoginFailure
            && !entry.Properties.ContainsKey("Username")
            && entry.ActorUserId == null);
    }

    /// <summary>A submission whose tenant was never assigned is not audited at all.</summary>
    /// <returns>A task representing the assertion.</returns>
    /// <remarks>
    /// Asserted because the absence is a decision. A missing tenant is a malformed request rather than a
    /// sign-in attempt, and the audit contract has no value that could stand for "unknown tenant" - the
    /// tenant column is seeded <c>IDENTITY(-1, 1)</c>, so both zero and minus one are real tenants.
    /// </remarks>
    [Fact]
    public async Task SignIn_WithNoTenantAssigned_IsNotAudited()
    {
        Harness harness = Harness.Ready();

        await harness.Service.LoginAsync(
            new LoginRequest { Username = AccountName, Password = RawPassword },
            CancellationToken.None);

        harness.AuditRecords.Should().BeEmpty();
    }

    /// <summary>No audited sign-in entry can carry the submitted credential.</summary>
    /// <returns>A task representing the assertion.</returns>
    /// <remarks>
    /// Proven over the CONTRACT rather than over one call, because a per-call assertion would only show
    /// that today's call site is careful. No member of the audited payload is capable of carrying a
    /// credential, so no call site can leak one - which is what makes the closed method set a guarantee
    /// instead of a convention.
    /// </remarks>
    [Fact]
    public async Task SignIn_AuditsNothingThatCouldCarryTheCredential()
    {
        Harness harness = Harness.Ready();

        await harness.LoginAsync();

        AuditEvent recorded = harness.AuditRecords.Should().ContainSingle().Subject;

        IEnumerable<string> text = typeof(AuditEvent)
            .GetProperties()
            .Where(property => property.PropertyType == typeof(string))
            .Select(property => (string?)property.GetValue(recorded))
            .Where(value => value is not null)
            .Select(value => value!)
            .Concat(recorded.Properties.Values.Where(value => value is not null).Select(value => value!));

        text.Should().NotContain(
            RawPassword,
            "a credential must be unexpressible on the audit contract, not merely omitted by a caller");

        typeof(AuditEvent).GetProperties().Select(property => property.Name).Should().BeEquivalentTo(
            new[]
            {
                "EventName", "Outcome", "PortalId", "ActorUserId", "SubjectUserId", "ResourceType",
                "ResourceId", "FailureCode", "Properties",
            },
            "the payload is closed, so a member added to it is a decision that has to be made here too");
    }

    /// <summary>A contained credential cost upgrade failure is recorded rather than lost.</summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task SignIn_RecordsAContainedCredentialCostUpgradeFailure()
    {
        Harness harness = Harness.Ready();
        InvalidOperationException failure = new("The credential store refused the write.");

        harness.PasswordHasher.Setup(hasher => hasher.NeedsRehash(StoredHash)).Returns(true);
        harness.PasswordHasher.Setup(hasher => hasher.Hash(RawPassword)).Returns("$2a$14$rehashed");
        harness.Users
            .Setup(users => users.SetPasswordHashAsync(
                It.IsAny<int>(),
                It.IsAny<string>(),
                It.IsAny<string?>(),
                It.IsAny<DateTime>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(failure);

        Result<LoginResponse> result = await harness.LoginAsync();

        result.IsSuccess.Should().BeTrue(
            "a failed cost upgrade leaves a still-valid credential in place and must not fail the sign-in");

        harness.AuditRecords.Should().ContainSingle(entry =>
            entry.EventName == AuditEventNames.PasswordRehashFailure
            && entry.SubjectUserId == UserId
            && entry.FailureCode == "credential_replacement_store_failure");

        harness.AuditRecords.Should().NotContain(
            entry => entry.FailureCode == nameof(InvalidOperationException),
            "an exception type name is not a stable code and does not belong in that column");

        harness.Diagnostics.Verify(
            diagnostics => diagnostics.Record(
                SecurityDiagnosticEvent.CredentialWorkFactorUpgradeFailed,
                PortalId,
                UserId,
                nameof(InvalidOperationException)),
            Times.Once());
    }

    /// <summary>
    /// C-03: an account whose profile is incomplete is admitted only to the remediation-limited session,
    /// with the blocking profile requirement raised.
    /// </summary>
    [Fact]
    public async Task SignIn_WhenTheProfileIsIncomplete_RaisesTheProfileAdvisory()
    {
        Harness harness = Harness.Ready();
        harness.ProfileIncomplete = true;

        Result<LoginResponse> result = await harness.LoginAsync();

        result.IsSuccess.Should().BeTrue("the credential was correct; the profile is a separate matter");
        result.Value.MustUpdateProfile.Should().BeTrue();
        harness.Accounts.Verify(
            accounts => accounts.RequiresProfileCompletionAsync(
                PortalId,
                UserId,
                It.IsAny<CancellationToken>()),
            Times.Once());
    }

    /// <summary>
    /// C-03: the profile requirement is raised alongside a credential requirement rather than being
    /// suppressed by it.
    /// </summary>
    /// <remarks>
    /// The legacy status enumeration was single-valued and tested the profile arm LAST, only while no other
    /// advisory had been raised, so it could never report both. This contract can, and the widening is
    /// deliberate: hiding a blocking profile requirement behind a non-blocking credential reminder would
    /// lose the more consequential of the two.
    /// </remarks>
    [Fact]
    public async Task SignIn_ReportsTheProfileAdvisoryTogetherWithACredentialAdvisory()
    {
        Harness harness = Harness.Ready();
        harness.ProfileIncomplete = true;
        harness.ScopedAccount!.UpdatePassword = true;

        Result<LoginResponse> result = await harness.LoginAsync();

        result.IsSuccess.Should().BeTrue();
        result.Value.MustChangePassword.Should().BeTrue();
        result.Value.MustUpdateProfile.Should().BeTrue();
    }

    /// <summary>C-03: the profile gate is not applied to an installation-wide account.</summary>
    [Fact]
    public async Task SignIn_DoesNotApplyTheProfileGateToAHostAccount()
    {
        Harness harness = Harness.Ready();
        harness.ProfileIncomplete = true;
        harness.ScopedAccount = null;
        harness.InstallationWideAccount = HostAccount();

        Result<LoginResponse> result = await harness.LoginAsync(username: "host");

        result.IsSuccess.Should().BeTrue();
        result.Value.MustUpdateProfile.Should().BeFalse();
        harness.Accounts.Verify(
            accounts => accounts.RequiresProfileCompletionAsync(
                It.IsAny<int>(),
                It.IsAny<int>(),
                It.IsAny<CancellationToken>()),
            Times.Never());
    }

    /// <summary>
    /// A profile requirement that cannot be evaluated refuses the sign-in rather than issuing unrestricted
    /// credentials on missing evidence.
    /// </summary>
    /// <remarks>
    /// This deliberately tightens the earlier advisory-only implementation. The profile state is now a
    /// blocking authorization input, so treating a store failure as "profile complete" would let an account
    /// bypass the gate precisely when its required state cannot be established.
    /// </remarks>
    [Fact]
    public async Task SignIn_WhenProfileRemediationCannotBeEvaluated_FailsClosed()
    {
        Harness harness = Harness.Ready();
        harness.Accounts
            .Setup(accounts => accounts.RequiresProfileCompletionAsync(
                It.IsAny<int>(),
                It.IsAny<int>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<bool>.Failure("user.profile.unavailable", "unavailable"));

        Result<LoginResponse> result = await harness.LoginAsync();

        result.IsFailure.Should().BeTrue();
        result.Reason!.Code.Should().Be("auth.remediation.store_unavailable");
        harness.Tokens.Verify(
            tokens => tokens.IssueTokensAsync(
                It.IsAny<int>(),
                It.IsAny<int>(),
                It.IsAny<CancellationToken>()),
            Times.Never());
    }

    /// <summary>
    /// The authorization-facing remediation read combines the durable credential flag with the current
    /// profile requirement instead of trusting claims from the presented token.
    /// </summary>
    [Fact]
    public async Task EvaluateRemediationAsync_ReReadsBothBlockingRequirements()
    {
        Harness harness = Harness.Ready();
        harness.ScopedAccount!.UpdatePassword = true;
        harness.ProfileIncomplete = true;

        Result<AuthenticationRemediationState> result = await harness.Service.EvaluateRemediationAsync(
            PortalId,
            UserId,
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.MustChangePassword.Should().BeTrue();
        result.Value.MustUpdateProfile.Should().BeTrue();
        result.Value.IsRequired.Should().BeTrue();
        harness.Accounts.Verify(
            accounts => accounts.RequiresProfileCompletionAsync(
                PortalId,
                UserId,
                It.IsAny<CancellationToken>()),
            Times.Once());
    }

    /// <summary>
    /// The authorization-facing remediation read fails closed when required profile state is unavailable.
    /// </summary>
    [Fact]
    public async Task EvaluateRemediationAsync_WhenProfileStateIsUnavailable_FailsClosed()
    {
        Harness harness = Harness.Ready();
        harness.Accounts
            .Setup(accounts => accounts.RequiresProfileCompletionAsync(
                It.IsAny<int>(),
                It.IsAny<int>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<bool>.Failure("user.profile.unavailable", "unavailable"));

        Result<AuthenticationRemediationState> result = await harness.Service.EvaluateRemediationAsync(
            PortalId,
            UserId,
            CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Reason!.Code.Should().Be("auth.remediation.store_unavailable");
    }

    /// <summary>
    /// C-04: the shipped-credential advisory still recognises exactly the four distributed credentials, and
    /// no others, now that it compares fingerprints instead of plaintext.
    /// </summary>
    /// <remarks>
    /// This is the behaviour-preservation test for the fingerprint change. It pins BOTH directions: each of
    /// the two administrator credentials is recognised, and a credential that merely resembles one - a
    /// different case, or a longer string with the same prefix - is not.
    /// </remarks>
    /// <param name="password">The submitted credential.</param>
    /// <param name="expectedAdvisory">Whether the advisory is expected.</param>
    [Theory]
    [InlineData("admin", true)]
    [InlineData("dnnadmin", true)]
    [InlineData("Admin", false)]
    [InlineData("DNNADMIN", false)]
    [InlineData("admin1", false)]
    [InlineData("adm", false)]

    // A BLANK credential is deliberately not among these cases. It never reaches the advisory at all: the
    // request-validity gate refuses it before any account is resolved, which
    // SignIn_ReportsAMalformedRequestSeparately already pins.
    public async Task SignIn_RecognisesOnlyTheDistributedAdministratorCredentials(
        string password,
        bool expectedAdvisory)
    {
        Harness harness = Harness.Ready();
        harness.ScopedAccount!.Username = "admin";

        Result<LoginResponse> result = await harness.LoginAsync(username: "admin", password: password);

        result.IsSuccess.Should().BeTrue();
        (result.Reason?.Code == InsecureAdminPasswordCode).Should().Be(expectedAdvisory);
        result.Value.MustChangePassword.Should().Be(expectedAdvisory);
    }

    /// <summary>
    /// C-04: the host advisory likewise recognises exactly the two credentials the host account shipped
    /// with.
    /// </summary>
    /// <param name="password">The submitted credential.</param>
    /// <param name="expectedAdvisory">Whether the advisory is expected.</param>
    [Theory]
    [InlineData("host", true)]
    [InlineData("dnnhost", true)]
    [InlineData("Host", false)]
    [InlineData("hosting", false)]
    public async Task SignIn_RecognisesOnlyTheDistributedHostCredentials(string password, bool expectedAdvisory)
    {
        Harness harness = Harness.Ready();
        harness.ScopedAccount = null;
        harness.InstallationWideAccount = HostAccount();

        Result<LoginResponse> result = await harness.LoginAsync(username: "host", password: password);

        result.IsSuccess.Should().BeTrue();
        (result.Reason?.Code == InsecureHostPasswordCode).Should().Be(expectedAdvisory);
    }

    /// <summary>C-04: no plaintext credential appears anywhere in the compiled service.</summary>
    /// <remarks>
    /// The finding was that the four distributed credentials were embedded as string literals, which is
    /// CWE-798. Asserting the BEHAVIOUR of the fingerprint comparison, as the theories above do, cannot
    /// catch a reintroduction - a future edit could add the literals back beside the digests and every
    /// behavioural test would still pass.
    /// </remarks>
    [Fact]
    public void Service_EmbedsNoDistributedCredentialAsPlaintext()
    {
        byte[] image = File.ReadAllBytes(typeof(AuthService).Assembly.Location);

        foreach (string distributed in new[] { "dnnadmin", "dnnhost" })
        {
            ImageContains(image, distributed).Should().BeFalse(
                $"\"{distributed}\" is a working credential and must exist only as a fingerprint");
        }

        foreach (string fingerprint in new[]
        {
            "8c6976e5b5410415bde908bd4dee15dfb167a9c873fc4bb8a81f6f2ab448a918",
            "4740ae6347b0172c01254ff55bae5aff5199f4446e7f6d643d40185b3f475145",
        })
        {
            ImageContains(image, fingerprint).Should().BeTrue(
                "the advisory must compare a digest, not a credential");
        }
    }

    /// <summary>
    /// Reports whether a compiled image holds the given text as a literal, in either encoding and at any
    /// alignment.
    /// </summary>
    /// <param name="image">The bytes of the compiled assembly.</param>
    /// <param name="needle">The text to look for.</param>
    /// <returns><see langword="true"/> when the text appears anywhere in the image.</returns>
    private static bool ImageContains(byte[] image, string needle) =>
        ContainsSequence(image, Encoding.Unicode.GetBytes(needle))
        || ContainsSequence(image, Encoding.UTF8.GetBytes(needle));

    /// <summary>Reports whether one byte sequence occurs within another.</summary>
    /// <param name="haystack">The bytes to search.</param>
    /// <param name="needle">The bytes to find.</param>
    /// <returns><see langword="true"/> when the sequence occurs.</returns>
    private static bool ContainsSequence(byte[] haystack, byte[] needle)
    {
        if (needle.Length == 0 || needle.Length > haystack.Length)
        {
            return false;
        }

        for (int start = 0; start <= haystack.Length - needle.Length; start++)
        {
            if (haystack.AsSpan(start, needle.Length).SequenceEqual(needle))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// M-07: an accepted sign-in is recorded under the legacy event name, with the advisory flags and no
    /// credential.
    /// </summary>
    [Fact]
    public async Task SignIn_RecordsTheAcceptedOutcomeUnderTheLegacyEventName()
    {
        Harness harness = Harness.Ready();
        harness.ProfileIncomplete = true;

        Result<LoginResponse> result = await harness.LoginAsync();

        result.IsSuccess.Should().BeTrue();
        (string EventName, IReadOnlyDictionary<string, string?> Properties) recorded =
            harness.AuditEvents.Should().ContainSingle().Subject;

        recorded.EventName.Should().Be("LOGIN_SUCCESS");
        recorded.Properties["PortalId"].Should().Be(PortalId.ToString(CultureInfo.InvariantCulture));
        recorded.Properties["UserId"].Should().Be(UserId.ToString(CultureInfo.InvariantCulture));
        recorded.Properties.Should().NotContainKey("Username");
        recorded.Properties["MustUpdateProfile"].Should().Be("true");
        recorded.Properties.Values.Should().NotContain(
            RawPassword,
            "the submitted credential is never recorded");
    }

    /// <summary>
    /// M-07: a host sign-in is recorded under the superuser event name, which is a different legacy event.
    /// </summary>
    [Fact]
    public async Task SignIn_RecordsAHostOutcomeUnderTheSuperuserEventName()
    {
        Harness harness = Harness.Ready();
        harness.ScopedAccount = null;
        harness.InstallationWideAccount = HostAccount();

        await harness.LoginAsync(username: "host");

        harness.AuditEvents.Should().ContainSingle()
            .Which.EventName.Should().Be("LOGIN_SUPERUSER");
    }

    /// <summary>
    /// M-07: a refused credential is recorded under the legacy failure name, carrying the account
    /// identifier the legacy trail could not.
    /// </summary>
    /// <remarks>
    /// <c>UserController.vb</c> L1140 always passed <c>Null.NullInteger</c> as the identifier, so the
    /// legacy trail never recorded WHICH account a refusal concerned even when the code had just resolved
    /// one. That is a documented divergence, and this test is what pins it.
    /// </remarks>
    [Fact]
    public async Task SignIn_RecordsARefusalWithTheAccountItResolved()
    {
        Harness harness = Harness.Ready();
        harness.PasswordHasher
            .Setup(hasher => hasher.Verify(It.IsAny<string>(), It.IsAny<string>()))
            .Returns(false);

        Result<LoginResponse> result = await harness.LoginAsync();

        result.IsFailure.Should().BeTrue();
        (string EventName, IReadOnlyDictionary<string, string?> Properties) recorded =
            harness.AuditEvents.Should().ContainSingle().Subject;

        recorded.EventName.Should().Be("LOGIN_FAILURE");
        recorded.Properties["UserId"].Should().Be(UserId.ToString(CultureInfo.InvariantCulture));
        recorded.Properties.Should().NotContainKey("Username");
    }

    /// <summary>M-07: a sign-in for an account that does not exist is recorded with no account identifier.</summary>
    [Fact]
    public async Task SignIn_RecordsARefusalForAnUnknownAccountWithoutAnIdentifier()
    {
        Harness harness = Harness.Ready();
        harness.ScopedAccount = null;
        harness.InstallationWideAccount = null;

        Result<LoginResponse> result = await harness.LoginAsync(username: "nobody");

        result.IsFailure.Should().BeTrue();
        (string EventName, IReadOnlyDictionary<string, string?> Properties) recorded =
            harness.AuditEvents.Should().ContainSingle().Subject;

        recorded.EventName.Should().Be("LOGIN_FAILURE");
        recorded.Properties["UserId"].Should().BeNull("no account resolved, so none is recorded");
        recorded.Properties.Should().NotContainKey(
            "Username",
            "an unresolved submitted name remains anonymous in the independently retained trail");
    }

    /// <summary>M-07: a locked account is recorded under the legacy lock-out event name.</summary>
    [Fact]
    public async Task SignIn_RecordsALockedAccountUnderTheLegacyLockOutName()
    {
        Harness harness = Harness.Ready();
        harness.IsLockedOut = true;

        Result<LoginResponse> result = await harness.LoginAsync();

        result.IsFailure.Should().BeTrue();
        harness.AuditEvents.Should().ContainSingle()
            .Which.EventName.Should().Be("LOGIN_USERLOCKEDOUT");
    }

    /// <summary>Every collaborator is required.</summary>
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
            new Mock<IRoleRepository>().Object,
            new Mock<IPermissionService>().Object,
            new Mock<IUserService>().Object,
            new Mock<ITokenService>().Object,
            new Mock<IRefreshTokenStore>().Object,
            new Mock<IPasswordHasher>().Object,
            new Mock<ILegacyCredentialVerifier>().Object,
            new Mock<IClock>().Object,
            new Mock<IHostSettingsService>().Object,
            new Mock<IUnitOfWork>().Object,
            new Mock<ICurrentUser>().Object,
            new Mock<IAuditSink>().Object,
            new PasswordPolicyOptions(),
            new Mock<ISecurityDiagnostics>().Object,
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

    /// <summary>A correct verification code presented with an INCORRECT credential approves nothing.</summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task SignIn_WithACorrectVerificationCodeButAWrongCredential_ApprovesNothing()
    {
        Harness harness = Harness.Ready();
        harness.IsApproved = false;
        harness.CredentialMatches = false;

        Result<LoginResponse> result = await harness.LoginAsync(
            verificationCode: FormattableString.Invariant($"{PortalId}-{UserId}"));

        result.IsFailure.Should().BeTrue();
        result.Reason!.Code.Should().Be(InvalidCredentialsCode);
        result.Reason!.Message.Should().Be(GenericDenial);

        harness.Users.Verify(
            users => users.SetApprovalAsync(
                It.IsAny<int>(),
                It.IsAny<bool>(),
                It.IsAny<CancellationToken>()),
            Times.Never(),
            "an unproven credential must not approve an account, whatever code accompanied it");

        harness.UnitOfWork.Verify(
            unitOfWork => unitOfWork.SaveChangesAsync(It.IsAny<CancellationToken>()),
            Times.Never());

        // The entity's own flag is nullable and starts unset, so the assertion is that the service did not set
        // it - which is the fact under test - rather than that it holds any particular value.
        harness.ScopedAccount!.IsApproved.Should().NotBeTrue();

        harness.Tokens.Verify(
            tokens => tokens.IssueTokensAsync(
                It.IsAny<int>(),
                It.IsAny<int>(),
                It.IsAny<CancellationToken>()),
            Times.Never());
    }

    /// <summary>
    /// A wrong credential against an UNAPPROVED account is counted towards the lock-out, as it is for any
    /// other account.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    /// <remarks>
    /// A consequence of proving the credential before the approval gate, and an improvement rather than a
    /// side effect.
    /// </remarks>
    [Fact]
    public async Task SignIn_CountsAWrongCredentialAgainstAnUnapprovedAccount()
    {
        Harness harness = Harness.Ready();
        harness.IsApproved = false;
        harness.CredentialMatches = false;

        Result<LoginResponse> result = await harness.LoginAsync();

        result.IsFailure.Should().BeTrue();

        // AND IT LEARNS NOTHING ABOUT THE ACCOUNT'S APPROVAL STATE. This is the boundary that makes naming
        // the three approval outcomes safe: they are reported only from behind an accepted credential, so a
        // caller who fails the credential against an unapproved account is answered exactly as one who
        // fails it against an approved account, an absent account or an absent tenant.
        result.Reason!.Code.Should().Be(InvalidCredentialsCode);
        result.Reason!.Message.Should().Be(GenericDenial);
        harness.Users.Verify(
            users => users.RecordFailedLoginAsync(
                UserId,
                It.IsAny<int>(),
                It.IsAny<TimeSpan>(),
                Now,
                It.IsAny<CancellationToken>()),
            Times.Once());
    }

    /// <summary>A refresh presented for an admissible account succeeds and ends no session.</summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task Refresh_ForAnAdmissibleAccount_SucceedsAndEndsNoSession()
    {
        Harness harness = Harness.Ready();

        Result<LoginResponse> result = await harness.RefreshAsync();

        result.IsSuccess.Should().BeTrue(result.Reason?.ToString());
        harness.RevokedSessionUserIds.Should().BeEmpty();
    }

    /// <summary>
    /// A refresh is refused, and the account's sessions ended, once the membership store reports a state
    /// that would refuse a sign-in.
    /// </summary>
    /// <param name="isLockedOut">Whether the account has been locked since its token was issued.</param>
    /// <param name="isApproved">Whether the account is still approved.</param>
    /// <param name="credentialExists">Whether the account still holds a credential record.</param>
    /// <returns>A task representing the assertion.</returns>
    /// <remarks>
    /// This is the substance of the finding. Approval and lock-out live in the external membership store
    /// rather than on the account row, so re-reading the account and its roles - which the service already
    /// did - observed neither.
    /// </remarks>
    [Theory]
    [InlineData(true, true, true)]
    [InlineData(false, false, true)]
    [InlineData(false, true, false)]
    public async Task Refresh_WhenTheMembershipStoreRefusesTheAccount_IsRefusedAndEndsEverySession(
        bool isLockedOut,
        bool isApproved,
        bool credentialExists)
    {
        Harness harness = Harness.Ready();
        harness.IsLockedOut = isLockedOut;
        harness.IsApproved = isApproved;
        harness.CredentialOnFile = credentialExists;

        Result<LoginResponse> result = await harness.RefreshAsync();

        result.IsFailure.Should().BeTrue();
        result.Reason!.Code.Should().Be(InvalidRefreshTokenCode);
        harness.RevokedSessionUserIds.Should().Equal(new[] { UserId });
    }

    /// <summary>
    /// A host account is admitted on refresh without being approved into the tenant, which is the same
    /// exemption the sign-in path grants it.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    /// <remarks>
    /// A host account is created by the installer, so it is approved into no tenant and has no verification
    /// code to present. Applying the approval gate to one would lock the installation out of the account
    /// that governs it, which is exactly why the exemption exists on sign-in and why it is carried across
    /// here rather than reinvented.
    /// </remarks>
    [Fact]
    public async Task Refresh_ForAnUnapprovedHostAccount_IsPermitted()
    {
        Harness harness = Harness.Ready();
        harness.IsApproved = false;
        harness.ScopedAccount!.IsSuperUser = true;

        Result<LoginResponse> result = await harness.RefreshAsync();

        result.IsSuccess.Should().BeTrue(result.Reason?.ToString());
        harness.RevokedSessionUserIds.Should().BeEmpty();
    }

    /// <summary>
    /// A refusal whose revocation cannot be written is escalated rather than answered as an ordinary
    /// refusal.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    /// <remarks>
    /// The same rule that governs the rest of this service: a store that cannot be written describes
    /// neither the caller nor the presented token, so reporting it as "your token is not valid" would state
    /// something the service does not know.
    /// </remarks>
    [Fact]
    public async Task Refresh_WhenTheRefusalsRevocationCannotBeWritten_Escalates()
    {
        Harness harness = Harness.Ready();
        harness.IsLockedOut = true;
        harness.SessionsRevoked = false;

        Result<LoginResponse> result = await harness.RefreshAsync();

        result.IsFailure.Should().BeTrue();
        result.Reason!.Code.Should().Be(
            TokenStoreUnavailableCode,
            "the outage is reported as an outage, so the caller is not told its token was rejected");
        result.Reason!.Code.Should().NotBe(InvalidRefreshTokenCode);
        harness.RevokedSessionUserIds.Should().Equal(
            new[] { UserId },
            "the revocation was attempted before it was found to have failed");
    }

    /// <summary>
    /// On a verified-registration tenant the three legacy approval outcomes are selected exactly as the
    /// legacy screen selected them, and each is reported to the caller.
    /// </summary>
    /// <param name="supplied">The verification code the submission carried, if any.</param>
    /// <param name="expectedCode">The outcome the ladder must select.</param>
    /// <returns>A task representing the assertion.</returns>
    [Theory]
    [InlineData(null, VerificationRequiredCode)]
    [InlineData("", VerificationRequiredCode)]
    [InlineData("   ", VerificationCodeInvalidCode)]
    [InlineData("-1-8", VerificationCodeInvalidCode)]
    [InlineData("not-a-code", VerificationCodeInvalidCode)]
    public async Task SignIn_OnAVerifiedRegistrationTenant_SelectsTheLegacyApprovalOutcome(
        string? supplied,
        string expectedCode)
    {
        Harness harness = Harness.Ready();
        harness.IsApproved = false;
        harness.Portal.UserRegistration = UserRegistrationMode.VerifiedRegistration;

        Result<LoginResponse> result = await harness.LoginAsync(verificationCode: supplied);

        result.IsFailure.Should().BeTrue();
        result.Reason!.Code.Should().Be(expectedCode);
        result.Reason!.Message.Should().NotBeNullOrWhiteSpace();

        // No submitted value is echoed back. A rejected input repeated inside an error message is how that
        // message becomes a reflection vector, so the wording is authored and fixed per outcome.
        if (!string.IsNullOrWhiteSpace(supplied))
        {
            result.Reason!.Message.Should().NotContain(supplied);
        }

        harness.Users.Verify(
            users => users.SetApprovalAsync(
                It.IsAny<int>(),
                It.IsAny<bool>(),
                It.IsAny<CancellationToken>()),
            Times.Never());
    }

    /// <summary>
    /// A correct verification code presented WITH the correct credential approves the account and signs it
    /// in.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    /// <remarks>
    /// The other half of the reordering, and the one that proves nothing was lost by it: every observable
    /// outcome for a caller who does present the right credential is unchanged - the approval is recorded,
    /// the tracked row is committed, and tokens are issued.
    /// </remarks>
    [Fact]
    public async Task SignIn_WithACorrectVerificationCodeAndCredential_ApprovesAndSignsIn()
    {
        Harness harness = Harness.Ready();
        harness.IsApproved = false;

        Result<LoginResponse> result = await harness.LoginAsync(
            verificationCode: FormattableString.Invariant($"{PortalId}-{UserId}"));

        result.IsSuccess.Should().BeTrue(result.Reason?.ToString());
        harness.Users.Verify(
            users => users.SetApprovalAsync(UserId, true, It.IsAny<CancellationToken>()),
            Times.Once());
        harness.UnitOfWork.Verify(
            unitOfWork => unitOfWork.SaveChangesAsync(It.IsAny<CancellationToken>()),
            Times.Once(),
            "approving a verified registration is the one branch of a sign-in that changes a tracked row");
        harness.ScopedAccount!.IsApproved.Should().BeTrue();
    }

    /// <summary>
    /// Exactly one credential comparison is performed on every structurally valid attempt, whatever the
    /// lookups found - and when there is no stored form, the comparison is made against the decoy.
    /// </summary>
    /// <param name="scenario">Which world the attempt arrives in.</param>
    /// <returns>A task representing the assertion.</returns>
    /// <remarks>
    /// The refusal wording is uniform across all of these, and uniform wording is not enough: an attempt
    /// that returns before reaching a deliberately expensive comparison answers measurably sooner than one
    /// that does not, so the response TIME distinguishes an unknown account from a real one and an attacker
    /// can enumerate accounts by measuring.
    /// </remarks>
    [Theory]
    [InlineData("unknown-tenant")]
    [InlineData("unknown-account")]
    [InlineData("no-credential-record")]
    [InlineData("wrong-credential")]
    [InlineData("correct-credential")]
    public async Task SignIn_PerformsExactlyOneCredentialComparisonWhateverWasFound(string scenario)
    {
        Harness harness = Harness.Ready();
        string expectedComparand = StoredHash;

        switch (scenario)
        {
            case "unknown-tenant":
                harness.Portals
                    .Setup(portals => portals.GetByIdAsync(
                        It.IsAny<int>(),
                        It.IsAny<bool>(),
                        It.IsAny<CancellationToken>()))
                    .ReturnsAsync((Portal?)null);
                expectedComparand = DecoyHash;
                break;

            case "unknown-account":
                harness.ScopedAccount = null;
                harness.InstallationWideAccount = null;
                expectedComparand = DecoyHash;
                break;

            case "no-credential-record":
                harness.Users
                    .Setup(users => users.GetCredentialStateAsync(
                        It.IsAny<int>(),
                        It.IsAny<CancellationToken>()))
                    .ReturnsAsync((
                        false,
                        (string?)null,
                        (PasswordFormat?)null,
                        (string?)null,
                        false,
                        false));
                expectedComparand = DecoyHash;
                break;

            case "wrong-credential":
                harness.CredentialMatches = false;
                break;

            default:
                break;
        }

        Result<LoginResponse> result = await harness.LoginAsync();

        result.IsSuccess.Should().Be(scenario == "correct-credential");

        harness.PasswordHasher.Verify(
            hasher => hasher.Verify(RawPassword, expectedComparand),
            Times.Once(),
            "the one comparison is made against the stored form when there is one and the decoy when there "
            + "is not");

        harness.PasswordHasher.Verify(
            hasher => hasher.Verify(It.IsAny<string>(), It.IsAny<string>()),
            Times.Once(),
            "a second comparison would be as measurable as none");
    }

    /// <summary>A malformed request is refused without any comparison, because there is nothing to compare.</summary>
    /// <param name="username">The account name to submit.</param>
    /// <param name="password">The credential to submit.</param>
    /// <returns>A task representing the assertion.</returns>
    /// <remarks>
    /// The boundary of the previous guarantee, and it is a real boundary rather than an exception to the
    /// rule. These refusals are decided from the shape of the submission alone: no store is consulted, no
    /// account is named to an attacker, and the answer carries its own distinct code - so the timing of an
    /// attempt that omitted a credential entirely reveals nothing about any account.
    /// </remarks>
    [Theory]
    [InlineData("", RawPassword)]
    [InlineData(AccountName, "")]
    public async Task SignIn_WithAMalformedRequest_PerformsNoComparison(string username, string password)
    {
        Harness harness = Harness.Ready();

        Result<LoginResponse> result = await harness.LoginAsync(username, password);

        result.IsFailure.Should().BeTrue();
        result.Reason!.Code.Should().Be(RequestInvalidCode);
        harness.PasswordHasher.Verify(
            hasher => hasher.Verify(It.IsAny<string>(), It.IsAny<string>()),
            Times.Never());
    }

    /// <summary>
    /// A failed attempt that the membership store could not count raises rather than answering with a
    /// denial.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task SignIn_WhenAFailedAttemptCannotBeCounted_RaisesRatherThanDenying()
    {
        Harness harness = Harness.Ready();
        harness.CredentialMatches = false;
        harness.FailedLoginOutcome = MembershipWriteOutcome.StoreUnavailable;

        Func<Task> attempt = () => harness.LoginAsync();

        await attempt.Should().ThrowAsync<InvalidOperationException>()
            .Where(thrown => thrown.Message.Contains("membership store", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// A successful sign-in whose counter reset could not be written raises rather than issuing tokens.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task SignIn_WhenTheCounterResetCannotBeWritten_RaisesRatherThanIssuingTokens()
    {
        Harness harness = Harness.Ready();
        harness.SuccessfulLoginOutcome = MembershipWriteOutcome.StoreUnavailable;

        Func<Task> attempt = () => harness.LoginAsync();

        await attempt.Should().ThrowAsync<InvalidOperationException>();

        harness.Tokens.Verify(
            tokens => tokens.IssueTokensAsync(
                It.IsAny<int>(),
                It.IsAny<int>(),
                It.IsAny<CancellationToken>()),
            Times.Never(),
            "no token may be minted while the lock-out control is known to be down");
    }

    /// <summary>
    /// A bookkeeping write that finds no credential record is recorded and does not fail the sign-in.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    /// <remarks>
    /// Distinct from an unreachable store, and treated differently on purpose: the control RAN and found
    /// nothing, which on this path means the record was removed between the read that found it and the
    /// write that followed. That is a race, and the only sensible handling of a race is to proceed.
    /// </remarks>
    [Fact]
    public async Task SignIn_WhenTheBookkeepingRecordHasVanished_RecordsItAndProceeds()
    {
        Harness harness = Harness.Ready();
        harness.SuccessfulLoginOutcome = MembershipWriteOutcome.NoRecord;

        Result<LoginResponse> result = await harness.LoginAsync();

        result.IsSuccess.Should().BeTrue(result.Reason?.ToString());
        harness.Diagnostics.Verify(
            diagnostics => diagnostics.Record(
                SecurityDiagnosticEvent.MembershipRecordMissingDuringSignIn,
                It.IsAny<int?>(),
                UserId,
                It.IsAny<string?>()),
            Times.Once());
    }

    /// <summary>A work-factor upgrade that the store refuses is recorded and does not fail the sign-in.</summary>
    /// <param name="storeThrows">Whether the store raises rather than answering that it wrote nothing.</param>
    /// <returns>A task representing the assertion.</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SignIn_WhenTheWorkFactorUpgradeFails_RecordsItAndStillSucceeds(bool storeThrows)
    {
        Harness harness = Harness.Ready();
        harness.PasswordHasher
            .Setup(hasher => hasher.NeedsRehash(It.IsAny<string>()))
            .Returns(true);

        if (storeThrows)
        {
            harness.Users
                .Setup(users => users.SetPasswordHashAsync(
                    It.IsAny<int>(),
                    It.IsAny<string>(),
                    It.IsAny<string?>(),
                    It.IsAny<DateTime>(),
                    It.IsAny<CancellationToken>()))
                .ThrowsAsync(new InvalidOperationException("the provider refused the write"));
        }
        else
        {
            harness.CredentialUpgradeAccepted = CredentialWriteOutcome.NoRecord;
        }

        Result<LoginResponse> result = await harness.LoginAsync();

        result.IsSuccess.Should().BeTrue(
            "a cost upgrade that could not be stored must never fail a sign-in whose credential was correct");

        harness.Diagnostics.Verify(
            diagnostics => diagnostics.Record(
                SecurityDiagnosticEvent.CredentialWorkFactorUpgradeFailed,
                PortalId,
                UserId,
                It.IsAny<string?>()),
            Times.Once());
    }

    /// <summary>A work-factor upgrade that succeeds, and one that was never due, record nothing.</summary>
    /// <param name="upgradeDue">Whether the stored form is reported as superseded.</param>
    /// <returns>A task representing the assertion.</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SignIn_WhenTheWorkFactorUpgradeIsNotNeededOrSucceeds_RecordsNothing(bool upgradeDue)
    {
        Harness harness = Harness.Ready();
        harness.PasswordHasher
            .Setup(hasher => hasher.NeedsRehash(It.IsAny<string>()))
            .Returns(upgradeDue);

        Result<LoginResponse> result = await harness.LoginAsync();

        result.IsSuccess.Should().BeTrue(result.Reason?.ToString());
        harness.Diagnostics.Verify(
            diagnostics => diagnostics.Record(
                It.IsAny<SecurityDiagnosticEvent>(),
                It.IsAny<int?>(),
                It.IsAny<int?>(),
                It.IsAny<string?>()),
            Times.Never());
    }

    /// <summary>
    /// Sign-in does not resolve mutable authority for either the access token or its response projection.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    /// <remarks>
    /// Roles and permission keys change independently of a token's lifetime. They are loaded through
    /// <c>/auth/me</c> and re-evaluated by server-side authorization, so the login path must not read or
    /// copy them into long-lived bearer material.
    /// </remarks>
    [Fact]
    public async Task SignIn_DoesNotResolveMutableAuthorityForTheTokenResponse()
    {
        Harness harness = Harness.Ready();

        Result<LoginResponse> result = await harness.LoginAsync();

        result.IsSuccess.Should().BeTrue(result.Reason?.ToString());
        result.Value.User.Roles.Should().BeEmpty();
        result.Value.User.Permissions.Should().BeEmpty();

        harness.Permissions.Verify(
            permissions => permissions.GetEffectivePermissionKeysAsync(
                It.IsAny<int>(),
                It.IsAny<int?>(),
                It.IsAny<int?>(),
                It.IsAny<int?>(),
                It.IsAny<CancellationToken>()),
            Times.Never());
        harness.Users.Verify(
            users => users.ListRoleNamesAsync(
                It.IsAny<int>(),
                It.IsAny<int>(),
                It.IsAny<DateTime>(),
                It.IsAny<CancellationToken>()),
            Times.Never());

        harness.Tokens.Verify(
            tokens => tokens.IssueTokensAsync(
                It.IsAny<int>(),
                It.IsAny<int>(),
                It.IsAny<CancellationToken>()),
            Times.Once());
    }

    /// <summary>Builds the installation-wide host account.</summary>
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

    /// <summary>Builds an authentication service over substituted collaborators.</summary>
    private sealed class Harness
    {
        private const string ClientBinding = "client-binding-placeholder";

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
            Roles = new Mock<IRoleRepository>(MockBehavior.Loose);
            Permissions = new Mock<IPermissionService>(MockBehavior.Loose);
            Accounts = new Mock<IUserService>(MockBehavior.Loose);
            Tokens = new Mock<ITokenService>(MockBehavior.Loose);
            RefreshTokens = new Mock<IRefreshTokenStore>(MockBehavior.Loose);
            PasswordHasher = new Mock<IPasswordHasher>(MockBehavior.Loose);
            LegacyCredentials = new Mock<ILegacyCredentialVerifier>(MockBehavior.Loose);
            Clock = new Mock<IClock>(MockBehavior.Loose);
            HostSettings = new Mock<IHostSettingsService>(MockBehavior.Loose);
            UnitOfWork = new Mock<IUnitOfWork>(MockBehavior.Loose);
            CurrentUser = new Mock<ICurrentUser>(MockBehavior.Loose);
            Audit = new Mock<IAuditSink>(MockBehavior.Loose);

            AuditRecords = [];
            Audit
                .Setup(sink => sink.Record(It.IsAny<AuditEvent>()))
                .Callback<AuditEvent>(AuditRecords.Add);

            // The profile gate defaults to "complete", so a test that says nothing about profiles sees the
            // behaviour it saw before the gate existed; a test about the gate overrides this one stub.
            Accounts
                .Setup(a => a.RequiresProfileCompletionAsync(
                    It.IsAny<int>(),
                    It.IsAny<int>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => Result<bool>.Success(ProfileIncomplete));
            Accounts
                .Setup(a => a.IsEmailValidAsync(
                    It.IsAny<int>(),
                    It.IsAny<string>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(Result<bool>.Success(true));

            Diagnostics = new Mock<ISecurityDiagnostics>(MockBehavior.Loose);

            Service = new AuthService(
                Users.Object,
                Portals.Object,
                Roles.Object,
                Permissions.Object,
                Accounts.Object,
                Tokens.Object,
                RefreshTokens.Object,
                PasswordHasher.Object,
                LegacyCredentials.Object,
                Clock.Object,
                HostSettings.Object,
                UnitOfWork.Object,
                CurrentUser.Object,
                Audit.Object,
                Policy,
                Diagnostics.Object);
        }

        public Portal Portal { get; }

        public Mock<IUserService> Accounts { get; }

        public Mock<IAuditSink> Audit { get; }

        /// <summary>
        /// The recorded events as name-and-property pairs, which is the shape the audit assertions on this
        /// suite were written against.
        /// </summary>
        /// <remarks>
        /// A projection over <see cref="AuditRecords"/> rather than a second capture, so there is exactly
        /// one audit stream and no test can pass against a record the service did not emit.
        /// </remarks>
        public IReadOnlyList<(string EventName, IReadOnlyDictionary<string, string?> Properties)> AuditEvents =>
            AuditRecords
                .Select(record =>
                {
                    Dictionary<string, string?> properties =
                        new(record.Properties, StringComparer.Ordinal)
                        {
                            ["PortalId"] = record.PortalId?.ToString(CultureInfo.InvariantCulture),
                            ["UserId"] = record.ActorUserId?.ToString(CultureInfo.InvariantCulture),
                        };

                    return (record.EventName, (IReadOnlyDictionary<string, string?>)properties);
                })
                .ToList();

        /// <summary>
        /// Whether the profile gate reports the account incomplete. Default <see langword="false"/>.
        /// </summary>
        public bool ProfileIncomplete { get; set; }

        public User? ScopedAccount { get; set; }

        public User? InstallationWideAccount { get; set; }

        public bool IsApproved { get; set; }

        public bool IsLockedOut { get; set; }

        /// <summary>
        /// Whether the membership store still holds a credential record for the account. Defaults to true.
        /// </summary>
        /// <remarks>
        /// Separate from the stored hash on purpose, because the two answer different questions: the
        /// record's existence is what the refresh gate consults, while the hash is what a comparison is
        /// made against.
        /// </remarks>
        public bool CredentialOnFile { get; set; } = true;

        /// <summary>The stored representation returned by the membership store.</summary>
        public string? StoredCredential { get; set; } = StoredHash;

        /// <summary>The persisted format accompanying <see cref="StoredCredential"/>.</summary>
        public PasswordFormat? CredentialFormat { get; set; } = PasswordFormat.Hashed;

        /// <summary>The persisted legacy salt, empty for a current BCrypt representation.</summary>
        public string? CredentialSalt { get; set; } = string.Empty;

        /// <summary>Whether the bounded compatibility verifier accepts the submitted credential.</summary>
        public bool LegacyCredentialMatches { get; set; }

        /// <summary>
        /// What the store reports when a failed attempt is recorded. Defaults to the ordinary outcome -
        /// counted, and the account is still usable.
        /// </summary>
        public MembershipWriteOutcome FailedLoginOutcome { get; set; } = MembershipWriteOutcome.Recorded;

        /// <summary>
        /// What the store reports when the counters are cleared after a successful sign-in. Defaults to the
        /// ordinary outcome.
        /// </summary>
        public MembershipWriteOutcome SuccessfulLoginOutcome { get; set; } = MembershipWriteOutcome.Recorded;

        /// <summary>
        /// Whether the store accepts a replacement credential representation during the work-factor
        /// upgrade.
        /// </summary>
        public CredentialWriteOutcome CredentialUpgradeAccepted { get; set; } = CredentialWriteOutcome.Replaced;

        /// <summary>
        /// What the hashing abstraction reports for the one comparison the sign-in path performs. Defaults
        /// to a match, because most tests here are about the workflow around a correct credential.
        /// </summary>
        public bool CredentialMatches { get; set; } = true;

        /// <summary>
        /// Whether the stored credential is replaced by somebody else once this sign-in has read it,
        /// modelling an administrative reset that lands while the deliberately expensive comparison is
        /// running.
        /// </summary>
        /// <remarks>
        /// When set, the first credential read reports <see cref="StoredCredential"/> - so the comparison
        /// this sign-in performs is against the representation it legitimately found - and every later read
        /// reports <see cref="CredentialValueAfterVerification"/>.
        /// </remarks>
        public bool CredentialChangesAfterVerification { get; set; }

        /// <summary>
        /// The credential-state read from which the staged change becomes visible. Defaults to the second,
        /// which is the read the sign-in path takes immediately before it mints a session.
        /// </summary>
        /// <remarks>
        /// A sign-in reads the credential three times - once before the comparison, once before issuance
        /// and once after it - and the three reads defend against different things, so a test has to be
        /// able to name which one first observes the change.
        /// </remarks>
        public int CredentialChangesFromRead { get; set; } = 2;

        /// <summary>
        /// The representation later reads report when <see cref="CredentialChangesAfterVerification"/> is
        /// set. A null value models the credential record being emptied outright, which is refused for the
        /// same reason a different value is.
        /// </summary>
        public string? CredentialValueAfterVerification { get; set; } = "$2a$12$reset-by-an-administrator";

        /// <summary>
        /// How many times the credential state has been read, so a test can pin that the sign-in path takes
        /// the second look at all rather than passing for the wrong reason.
        /// </summary>
        public int CredentialStateReads { get; private set; }

        /// <summary>Accounts whose sessions the service asked to have ended, in the order it asked.</summary>
        public List<int> RevokedSessionUserIds { get; } = [];

        /// <summary>Whether the token store can end an account's sessions. Defaults to true.</summary>
        public bool SessionsRevoked { get; set; } = true;

        /// <summary>
        /// What rotation answers, or <see langword="null"/> to answer with the default successful pair.
        /// </summary>
        public Result<LoginResponse>? RotationOutcome { get; set; }

        public DateTime? RecordedLoginInstant { get; private set; }

        public DateTime? RehashInstant { get; private set; }

        /// <summary>The expectation the credential replacement carried, when one was attempted.</summary>
        public string? RehashExpectation { get; private set; }

        public PasswordPolicyOptions Policy { get; }

        public Mock<IUserRepository> Users { get; }

        public Mock<IPortalRepository> Portals { get; }

        /// <summary>
        /// The role store, asked only whether the caller holds the role the tenant designates as its
        /// administrator. Loose by default, so it answers with an empty assignment list and the advisory
        /// administration fact is reported false unless a test arranges otherwise.
        /// </summary>
        public Mock<IRoleRepository> Roles { get; }

        public Mock<IPermissionService> Permissions { get; }

        public Mock<ITokenService> Tokens { get; }

        public Mock<IRefreshTokenStore> RefreshTokens { get; }

        public Mock<IPasswordHasher> PasswordHasher { get; }

        public Mock<ILegacyCredentialVerifier> LegacyCredentials { get; }

        public Mock<IClock> Clock { get; }

        public Mock<IHostSettingsService> HostSettings { get; }

        public Mock<IUnitOfWork> UnitOfWork { get; }

        public Mock<ICurrentUser> CurrentUser { get; }

        /// <summary>Every audit event the service emitted, in the order it emitted them.</summary>
        public List<AuditEvent> AuditRecords { get; }
        /// <summary>
        /// Receives the anomalies the service absorbs rather than reports. Asserted on directly, because an
        /// absorbed anomaly that is not recorded is indistinguishable from one that never happened - which
        /// is the defect these assertions exist to keep closed.
        /// </summary>
        public Mock<ISecurityDiagnostics> Diagnostics { get; }

        public AuthService Service { get; }

        /// <summary>Builds a harness whose collaborators all agree that the sign-in should succeed.</summary>
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
                .ReturnsAsync(() => harness.ReadCredentialState());

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
                .ReturnsAsync(() => harness.SuccessfulLoginOutcome);

            harness.Users
                .Setup(users => users.RecordFailedLoginAsync(
                    It.IsAny<int>(),
                    It.IsAny<int>(),
                    It.IsAny<TimeSpan>(),
                    It.IsAny<DateTime>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => harness.FailedLoginOutcome);

            harness.Users
                .Setup(users => users.SetPasswordHashAsync(
                    It.IsAny<int>(),
                    It.IsAny<string>(),
                    It.IsAny<string?>(),
                    It.IsAny<DateTime>(),
                    It.IsAny<CancellationToken>()))
                .Callback<int, string, string?, DateTime, CancellationToken>(
                    (_, _, expected, instant, _) =>
                    {
                        harness.RehashInstant = instant;
                        harness.RehashExpectation = expected;
                    })
                .ReturnsAsync(() => harness.CredentialUpgradeAccepted);

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

            harness.HostSettings
                .Setup(settings => settings.GetSettingAsync(
                    It.IsAny<string>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync((string?)null);

            harness.PasswordHasher
                .Setup(hasher => hasher.Verify(It.IsAny<string>(), It.IsAny<string>()))
                .Returns(() => harness.CredentialMatches);

            harness.PasswordHasher
                .SetupGet(hasher => hasher.UnmatchableHash)
                .Returns(DecoyHash);

            harness.PasswordHasher
                .Setup(hasher => hasher.NeedsRehash(It.IsAny<string>()))
                .Returns(false);

            // MIGRATION: THE STUB ANSWERS FROM THE HARNESS RATHER THAN WITH A CONSTANT, WHICH IS WHAT MAKES
            // THE LEGACY FACTS OPERATIVE. Two revisions each introduced a compatibility verifier, one
            // answering a boolean and one answering a two-part outcome that separates "this row is legacy"
            // from "this credential matched".
            harness.LegacyCredentials
                .Setup(verifier => verifier.Verify(
                    It.IsAny<string>(),
                    It.IsAny<string>(),
                    It.IsAny<PasswordFormat>(),
                    It.IsAny<string?>()))
                .Returns(() => harness.CredentialFormat == PasswordFormat.Hashed
                    && harness.StoredCredential?.StartsWith("$2", StringComparison.Ordinal) != false
                        ? LegacyCredentialVerification.Current
                        : LegacyCredentialVerification.Legacy(harness.LegacyCredentialMatches));

            // Resolution by identifier, which the refresh path uses where the sign-in path resolves by
            // name.
            harness.Users
                .Setup(users => users.GetAsync(
                    It.Is<int?>(portalId => portalId != null),
                    It.IsAny<int>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => harness.ScopedAccount);

            harness.Users
                .Setup(users => users.GetAsync(
                    It.Is<int?>(portalId => portalId == null),
                    It.IsAny<int>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => harness.InstallationWideAccount);

            // Rotation succeeds by default and answers with the identity recorded against the presented
            // value, which is where the refresh path reads the tenant and account from. Nothing the caller
            // supplies reaches it.
            harness.RefreshTokens
                .Setup(store => store.InspectAsync(
                    It.IsAny<string>(),
                    It.IsAny<string>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => RefreshTokenInspection.Succeeded(
                    new RefreshTokenSubject(UserId, PortalId),
                    Now.AddDays(1)));

            harness.Tokens
                .Setup(tokens => tokens.RefreshAsync(
                    It.IsAny<string>(),
                    It.IsAny<string>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => harness.RotationOutcome ?? Result<LoginResponse>.Success(new LoginResponse
                {
                    AccessToken = "rotated-access-token",
                    RefreshToken = "rotated-refresh-token",
                    ExpiresAtUtc = Now.AddMinutes(60),
                    User = new CurrentUserDto { UserId = UserId, PortalId = PortalId, Username = AccountName },
                }));

            harness.Tokens
                .Setup(tokens => tokens.RevokeAllRefreshTokensAsync(
                    It.IsAny<int>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync((int userId, CancellationToken _) =>
                {
                    harness.RevokedSessionUserIds.Add(userId);
                    return harness.SessionsRevoked
                        ? Result.Success()
                        : Result.Failure(TokenStoreUnavailableCode, "The token store could not be written.");
                });

            harness.Tokens
                .Setup(tokens => tokens.IssueTokensAsync(
                    It.IsAny<int>(),
                    It.IsAny<int>(),
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
        /// Answers a credential-state read, counting it and honouring a credential change staged to land
        /// after the first read.
        /// </summary>
        /// <returns>The state the store reports for this read.</returns>
        /// <remarks>
        /// The read is answered from a method rather than an inline tuple so that the ORDER of reads is
        /// expressible. Nothing about the sign-in path is knowable from a stub that cannot tell its first
        /// read from its second, and the race this exists to pin is defined by that difference.
        /// </remarks>
        internal (bool Exists, string? Value, PasswordFormat? Format, string? Salt, bool IsApproved, bool IsLockedOut) ReadCredentialState()
        {
            CredentialStateReads++;

            string? value = CredentialChangesAfterVerification
                && CredentialStateReads >= CredentialChangesFromRead
                ? CredentialValueAfterVerification
                : StoredCredential;

            return (
                CredentialOnFile,
                CredentialOnFile ? value : null,
                CredentialOnFile ? CredentialFormat : null,
                CredentialOnFile ? CredentialSalt : null,
                IsApproved,
                IsLockedOut);
        }

        /// <summary>Signs in with the supplied particulars.</summary>
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

        /// <summary>Exchanges a refresh token.</summary>
        /// <param name="refreshToken">The value to present.</param>
        /// <returns>The outcome.</returns>
        public Task<Result<LoginResponse>> RefreshAsync(string refreshToken = "presented-refresh-token")
            => Service.RefreshAsync(
                new RefreshTokenRequest
                {
                    RefreshToken = refreshToken,
                    ClientBinding = ClientBinding,
                },
                CancellationToken.None);
    }
}
