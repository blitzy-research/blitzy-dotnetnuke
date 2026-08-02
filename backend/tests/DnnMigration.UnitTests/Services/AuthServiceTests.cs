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
            new[] { "portalId", "request", "ipAddress", "cancellationToken" },
            options => options.WithStrictOrdering());
        login.ReturnType.Should().Be(typeof(Task<Result<LoginResponse>>));
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
            .Setup(portals => portals.GetAsync(
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
    /// The network address is accepted and changes nothing.
    /// </summary>
    /// <param name="address">The address supplied by the caller.</param>
    /// <remarks>
    /// The address exists on the contract because the legacy call site supplied one. It is recorded by the
    /// request log rather than here, and it is never an authorisation input: an address that could grant or
    /// deny access would be a header a caller controls.
    /// </remarks>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("203.0.113.7")]
    [InlineData("not-an-address")]
    public async Task SignIn_AcceptsAnyNetworkAddressAndIsUnaffectedByIt(string? address)
    {
        Harness harness = Harness.Ready();

        Result<LoginResponse> result = await harness.Service.LoginAsync(
            PortalId,
            new LoginRequest { Username = AccountName, Password = RawPassword },
            address,
            CancellationToken.None);

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
            PortalId,
            new LoginRequest { Username = username, Password = password },
            ipAddress: null,
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
    /// Every collaborator is required.
    /// </summary>
    [Fact]
    public void Service_RequiresEveryCollaborator()
    {
        Mock<IUserRepository> users = new();
        Mock<IPortalRepository> portals = new();
        Mock<IPermissionService> permissions = new();
        Mock<ITokenService> tokens = new();
        Mock<IPasswordHasher> hasher = new();
        Mock<IClock> clock = new();
        Mock<IUnitOfWork> unitOfWork = new();
        Mock<ICurrentUser> currentUser = new();
        PasswordPolicyOptions policy = new();

        Assert.Throws<ArgumentNullException>(() =>
        {
            _ = new AuthService(
                null!,
                portals.Object,
                permissions.Object,
                tokens.Object,
                hasher.Object,
                clock.Object,
                unitOfWork.Object,
                currentUser.Object,
                policy);
        });
        Assert.Throws<ArgumentNullException>(() =>
        {
            _ = new AuthService(
                users.Object,
                null!,
                permissions.Object,
                tokens.Object,
                hasher.Object,
                clock.Object,
                unitOfWork.Object,
                currentUser.Object,
                policy);
        });
        Assert.Throws<ArgumentNullException>(() =>
        {
            _ = new AuthService(
                users.Object,
                portals.Object,
                null!,
                tokens.Object,
                hasher.Object,
                clock.Object,
                unitOfWork.Object,
                currentUser.Object,
                policy);
        });
        Assert.Throws<ArgumentNullException>(() =>
        {
            _ = new AuthService(
                users.Object,
                portals.Object,
                permissions.Object,
                null!,
                hasher.Object,
                clock.Object,
                unitOfWork.Object,
                currentUser.Object,
                policy);
        });
        Assert.Throws<ArgumentNullException>(() =>
        {
            _ = new AuthService(
                users.Object,
                portals.Object,
                permissions.Object,
                tokens.Object,
                null!,
                clock.Object,
                unitOfWork.Object,
                currentUser.Object,
                policy);
        });
        Assert.Throws<ArgumentNullException>(() =>
        {
            _ = new AuthService(
                users.Object,
                portals.Object,
                permissions.Object,
                tokens.Object,
                hasher.Object,
                null!,
                unitOfWork.Object,
                currentUser.Object,
                policy);
        });
        Assert.Throws<ArgumentNullException>(() =>
        {
            _ = new AuthService(
                users.Object,
                portals.Object,
                permissions.Object,
                tokens.Object,
                hasher.Object,
                clock.Object,
                null!,
                currentUser.Object,
                policy);
        });
        Assert.Throws<ArgumentNullException>(() =>
        {
            _ = new AuthService(
                users.Object,
                portals.Object,
                permissions.Object,
                tokens.Object,
                hasher.Object,
                clock.Object,
                unitOfWork.Object,
                null!,
                policy);
        });
        Assert.Throws<ArgumentNullException>(() =>
        {
            _ = new AuthService(
                users.Object,
                portals.Object,
                permissions.Object,
                tokens.Object,
                hasher.Object,
                clock.Object,
                unitOfWork.Object,
                currentUser.Object,
                null!);
        });
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
            UnitOfWork = new Mock<IUnitOfWork>(MockBehavior.Loose);
            CurrentUser = new Mock<ICurrentUser>(MockBehavior.Loose);

            Service = new AuthService(
                Users.Object,
                Portals.Object,
                Permissions.Object,
                Tokens.Object,
                PasswordHasher.Object,
                Clock.Object,
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
                .Setup(portals => portals.GetAsync(
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
                    ExpiresIn = 3600,
                    ExpiresAtUtc = Now.AddMinutes(60),
                    RefreshTokenExpiresAtUtc = Now.AddDays(7),
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
                portalId,
                new LoginRequest
                {
                    Username = username,
                    Password = password,
                    VerificationCode = verificationCode,
                },
                ipAddress: null,
                CancellationToken.None);
    }
}
