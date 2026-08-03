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

namespace DnnMigration.UnitTests.Security;

/// <summary>
/// Covers the password-hashing contract: the policy it is configured with, the shape of the contract
/// itself, and the way the sign-in path is required to use it.
/// </summary>
/// <remarks>
/// <para>
/// The concrete hasher is an implementation detail of the infrastructure assembly and is deliberately
/// unreachable from this project. That is not an accident of layering, it is the reviewed and recorded
/// arrangement: the unit-test project references the application assembly only, and the project file says
/// in writing that an infrastructure reference must not be added. The concrete hasher is exercised for
/// real by the integration project, which drives an actual sign-in against an actual credential store.
/// </para>
/// <para>
/// What is left to assert here is more valuable than a hashing round trip would be, because it is what a
/// round trip cannot catch. A hasher can be perfectly correct and the system still insecure if the caller
/// compares credentials itself, stores what the user typed, checks the credential before checking the
/// lock, or reveals which half of a failed sign-in was wrong. Those are properties of the sign-in path
/// rather than of the hasher, they are all reachable from here through a substituted hasher, and each one
/// below fails loudly if a future change breaks it.
/// </para>
/// <para>
/// The suite also pins the configured policy. The legacy installation ran a deliberately loose policy, and
/// the migration preserves it verbatim rather than tightening it, because tightening a credential policy
/// during a migration locks out the existing membership. The policy is therefore asserted as intended
/// behaviour, together with the proof that a stricter policy is honoured when one is configured.
/// </para>
/// </remarks>
public class BcryptPasswordHasherTests
{
    private const int PortalId = -1;

    private const int UserId = 7;

    private const string AccountName = "measured_member";

    private const string RawPassword = "Integr8tion!Pass";

    private const string StoredHash = "$2a$12$storedhashvalue";

    private const string RehashedValue = "$2a$12$rehashedvalue";

    private const string RequestInvalidCode = "auth.request_invalid";

    private const string InvalidCredentialsCode = "auth.invalid_credentials";

    private const string LockedOutCode = "auth.locked_out";

    private const string InsecureAdminPasswordCode = "auth.insecure_admin_password";

    private const string InsecureHostPasswordCode = "auth.insecure_host_password";

    private const string GenericDenial = "The account name or credential is not correct.";

    private static readonly DateTime Now = new(2026, 8, 2, 12, 0, 0, DateTimeKind.Utc);

    /// <summary>
    /// The configured policy is the legacy one, preserved value for value.
    /// </summary>
    [Fact]
    public void PasswordPolicy_PreservesTheLegacyConfigurationVerbatim()
    {
        PasswordPolicyOptions policy = new();

        policy.MinRequiredPasswordLength.Should().Be(7, "the legacy provider was configured with seven");
        policy.MinRequiredNonAlphanumericCharacters.Should().Be(
            0,
            "the legacy provider required none, and demanding one now would refuse credentials that "
            + "existing members already hold");
        policy.RequiresQuestionAndAnswer.Should().BeFalse();
        policy.RequiresUniqueEmail.Should().BeFalse(
            "the legacy installation permitted two accounts to share an address");
        policy.PasswordResetEnabled.Should().BeTrue();
        policy.PasswordStrengthRegularExpression.Should().BeEmpty(
            "no strength pattern was configured, so none is imposed");
        policy.MaxInvalidPasswordAttempts.Should().Be(5);
        policy.PasswordAttemptWindowMinutes.Should().Be(10);
    }

    /// <summary>
    /// The policy is a plain settings object, so a deployment can tighten it without a code change.
    /// </summary>
    [Fact]
    public void PasswordPolicy_CanBeTightenedByConfigurationAlone()
    {
        PasswordPolicyOptions policy = new()
        {
            MinRequiredPasswordLength = 14,
            MinRequiredNonAlphanumericCharacters = 2,
            RequiresQuestionAndAnswer = true,
            RequiresUniqueEmail = true,
            PasswordStrengthRegularExpression = "^.{14,}$",
            MaxInvalidPasswordAttempts = 3,
            PasswordAttemptWindowMinutes = 30,
        };

        policy.MinRequiredPasswordLength.Should().Be(14);
        policy.MinRequiredNonAlphanumericCharacters.Should().Be(2);
        policy.RequiresQuestionAndAnswer.Should().BeTrue();
        policy.RequiresUniqueEmail.Should().BeTrue();
        policy.PasswordStrengthRegularExpression.Should().Be("^.{14,}$");
        policy.MaxInvalidPasswordAttempts.Should().Be(3);
        policy.PasswordAttemptWindowMinutes.Should().Be(30);
    }

    /// <summary>
    /// The hashing contract offers exactly three operations and no way to recover a credential.
    /// </summary>
    /// <remarks>
    /// The legacy installation stored credentials reversibly and offered retrieval, with the key that
    /// decrypts every stored credential committed to source control. Retrieval is deliberately not carried
    /// forward, and the cheapest durable guarantee of that is that the contract has no member capable of
    /// producing a credential from stored state.
    /// </remarks>
    [Fact]
    public void HashingContract_OffersThreeOperationsAndNoRecoveryPath()
    {
        MethodInfo[] members = typeof(IPasswordHasher).GetMethods();

        members.Select(member => member.Name).Should().BeEquivalentTo(
            new[] { "Hash", "Verify", "NeedsRehash" });

        MethodInfo hash = typeof(IPasswordHasher).GetMethod(nameof(IPasswordHasher.Hash))!;
        MethodInfo verify = typeof(IPasswordHasher).GetMethod(nameof(IPasswordHasher.Verify))!;
        MethodInfo needsRehash = typeof(IPasswordHasher).GetMethod(nameof(IPasswordHasher.NeedsRehash))!;

        hash.ReturnType.Should().Be(typeof(string), "hashing yields a hash");
        hash.GetParameters().Should().HaveCount(1);

        verify.ReturnType.Should().Be(
            typeof(bool),
            "verification answers whether a credential matches and never hands back the credential");
        verify.GetParameters().Should().HaveCount(2);
        verify.GetParameters()[0].Name.Should().Be("password");
        verify.GetParameters()[1].Name.Should().Be("passwordHash");

        needsRehash.ReturnType.Should().Be(typeof(bool));
        needsRehash.GetParameters().Should().ContainSingle(
            "deciding whether a stored hash is out of date needs the hash alone - a member that also took "
            + "the credential would invite callers to keep it around");
        needsRehash.GetParameters()[0].Name.Should().Be("passwordHash");

        members.Select(member => member.Name).Should().NotContain(
            name => name.Contains("Retrieve", StringComparison.OrdinalIgnoreCase)
                || name.Contains("Decrypt", StringComparison.OrdinalIgnoreCase)
                || name.Contains("Unprotect", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// A sign-in compares the submitted credential through the hasher, passing the stored hash as given.
    /// </summary>
    [Fact]
    public async Task SignIn_ComparesTheCredentialThroughTheHasher()
    {
        Harness harness = Harness.SignedInSuccessfully();

        Result<LoginResponse> result = await harness.LoginAsync(RawPassword);

        result.IsSuccess.Should().BeTrue(result.Reason?.ToString());
        harness.PasswordHasher.Verify(
            hasher => hasher.Verify(RawPassword, StoredHash),
            Times.Once());
    }

    /// <summary>
    /// A rejected credential produces the generic denial rather than saying what was wrong.
    /// </summary>
    [Fact]
    public async Task SignIn_IsRefusedGenericallyWhenTheCredentialDoesNotMatch()
    {
        Harness harness = Harness.SignedInSuccessfully();
        harness.PasswordHasher.Setup(hasher => hasher.Verify(It.IsAny<string>(), It.IsAny<string>()))
            .Returns(false);

        Result<LoginResponse> result = await harness.LoginAsync(RawPassword);

        result.IsFailure.Should().BeTrue();
        result.Reason!.Code.Should().Be(InvalidCredentialsCode);
        result.Reason!.Message.Should().Be(
            GenericDenial,
            "a wrong credential and an unknown account must be indistinguishable, or the endpoint becomes "
            + "an account-name oracle");
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
    /// An unknown account is refused with exactly the same reason as a wrong credential.
    /// </summary>
    [Fact]
    public async Task SignIn_RefusesAnUnknownAccountWithTheSameReasonAsAWrongCredential()
    {
        Harness harness = Harness.SignedInSuccessfully();
        harness.Users
            .Setup(users => users.GetByUsernameAsync(
                It.IsAny<int?>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((User?)null);

        Result<LoginResponse> unknownAccount = await harness.LoginAsync(RawPassword);

        Harness wrongCredential = Harness.SignedInSuccessfully();
        wrongCredential.PasswordHasher
            .Setup(hasher => hasher.Verify(It.IsAny<string>(), It.IsAny<string>()))
            .Returns(false);

        Result<LoginResponse> refused = await wrongCredential.LoginAsync(RawPassword);

        unknownAccount.Reason!.Code.Should().Be(refused.Reason!.Code);
        unknownAccount.Reason!.Message.Should().Be(refused.Reason!.Message);
    }

    /// <summary>
    /// A failed sign-in is recorded against the configured threshold and window.
    /// </summary>
    [Fact]
    public async Task SignIn_RecordsAFailureAgainstTheConfiguredThresholdAndWindow()
    {
        Harness harness = Harness.SignedInSuccessfully();
        harness.PasswordHasher.Setup(hasher => hasher.Verify(It.IsAny<string>(), It.IsAny<string>()))
            .Returns(false);

        await harness.LoginAsync(RawPassword);

        harness.Users.Verify(
            users => users.RecordFailedLoginAsync(
                UserId,
                5,
                TimeSpan.FromMinutes(10),
                Now,
                It.IsAny<CancellationToken>()),
            Times.Once());
    }

    /// <summary>
    /// A tightened policy changes the recorded threshold and window without any other change.
    /// </summary>
    [Fact]
    public async Task SignIn_HonoursATightenedLockoutPolicy()
    {
        Harness harness = Harness.SignedInSuccessfully();
        harness.Policy.MaxInvalidPasswordAttempts = 3;
        harness.Policy.PasswordAttemptWindowMinutes = 30;
        harness.PasswordHasher.Setup(hasher => hasher.Verify(It.IsAny<string>(), It.IsAny<string>()))
            .Returns(false);

        await harness.LoginAsync(RawPassword);

        harness.Users.Verify(
            users => users.RecordFailedLoginAsync(
                UserId,
                3,
                TimeSpan.FromMinutes(30),
                Now,
                It.IsAny<CancellationToken>()),
            Times.Once());
    }

    /// <summary>
    /// A successful sign-in re-hashes when the stored hash is out of date, and stores the hasher's output.
    /// </summary>
    /// <remarks>
    /// This is the migration path for credentials that predate the hashing scheme. The legacy store held
    /// credentials reversibly, so they cannot be verified by a one-way hash; an account is upgraded the
    /// first time it signs in successfully, with an administrative reset as the fallback.
    /// </remarks>
    [Fact]
    public async Task SignIn_UpgradesAnOutOfDateStoredHash()
    {
        Harness harness = Harness.SignedInSuccessfully();
        harness.PasswordHasher.Setup(hasher => hasher.NeedsRehash(StoredHash)).Returns(true);
        harness.PasswordHasher.Setup(hasher => hasher.Hash(RawPassword)).Returns(RehashedValue);

        Result<LoginResponse> result = await harness.LoginAsync(RawPassword);

        result.IsSuccess.Should().BeTrue(result.Reason?.ToString());
        harness.PasswordHasher.Verify(hasher => hasher.Hash(RawPassword), Times.Once());
        harness.Users.Verify(
            users => users.SetPasswordHashAsync(UserId, RehashedValue, Now, It.IsAny<CancellationToken>()),
            Times.Once());
    }

    /// <summary>
    /// A current stored hash is left exactly as it is.
    /// </summary>
    [Fact]
    public async Task SignIn_LeavesACurrentStoredHashAlone()
    {
        Harness harness = Harness.SignedInSuccessfully();

        await harness.LoginAsync(RawPassword);

        harness.PasswordHasher.Verify(hasher => hasher.Hash(It.IsAny<string>()), Times.Never());
        harness.Users.Verify(
            users => users.SetPasswordHashAsync(
                It.IsAny<int>(),
                It.IsAny<string>(),
                It.IsAny<DateTime>(),
                It.IsAny<CancellationToken>()),
            Times.Never());
    }

    /// <summary>
    /// The credential the caller typed is never written to the store, on either sign-in path.
    /// </summary>
    [Fact]
    public async Task SignIn_NeverWritesTheSubmittedCredential()
    {
        Harness upgraded = Harness.SignedInSuccessfully();
        upgraded.PasswordHasher.Setup(hasher => hasher.NeedsRehash(It.IsAny<string>())).Returns(true);
        upgraded.PasswordHasher.Setup(hasher => hasher.Hash(It.IsAny<string>())).Returns(RehashedValue);

        await upgraded.LoginAsync(RawPassword);

        upgraded.Users.Verify(
            users => users.SetPasswordHashAsync(
                It.IsAny<int>(),
                RawPassword,
                It.IsAny<DateTime>(),
                It.IsAny<CancellationToken>()),
            Times.Never());

        Harness unchanged = Harness.SignedInSuccessfully();

        await unchanged.LoginAsync(RawPassword);

        unchanged.Users.Verify(
            users => users.SetPasswordHashAsync(
                It.IsAny<int>(),
                RawPassword,
                It.IsAny<DateTime>(),
                It.IsAny<CancellationToken>()),
            Times.Never());
    }

    /// <summary>
    /// An account with no stored credential is refused without the hasher being consulted.
    /// </summary>
    [Fact]
    public async Task SignIn_IsRefusedWithoutConsultingTheHasherWhenNoCredentialIsStored()
    {
        Harness missing = Harness.SignedInSuccessfully();
        missing.Users
            .Setup(users => users.GetCredentialStateAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((false, (string?)null, false, false));

        Result<LoginResponse> absent = await missing.LoginAsync(RawPassword);

        absent.IsFailure.Should().BeTrue();
        absent.Reason!.Code.Should().Be(InvalidCredentialsCode);
        missing.PasswordHasher.Verify(
            hasher => hasher.Verify(It.IsAny<string>(), It.IsAny<string>()),
            Times.Never());

        Harness noHash = Harness.SignedInSuccessfully();
        noHash.Users
            .Setup(users => users.GetCredentialStateAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((true, (string?)null, true, false));

        Result<LoginResponse> blank = await noHash.LoginAsync(RawPassword);

        blank.IsFailure.Should().BeTrue();
        blank.Reason!.Code.Should().Be(InvalidCredentialsCode);
        noHash.PasswordHasher.Verify(
            hasher => hasher.Verify(It.IsAny<string>(), It.IsAny<string>()),
            Times.Never());
    }

    /// <summary>
    /// A locked account is refused before the credential is examined at all.
    /// </summary>
    /// <remarks>
    /// The order matters. Checking the credential first and the lock afterwards would let an attacker use
    /// the response to confirm a guessed credential on an account that is already locked, which is exactly
    /// what the lock exists to prevent.
    /// </remarks>
    [Fact]
    public async Task SignIn_ChecksTheLockBeforeTheCredential()
    {
        Harness harness = Harness.SignedInSuccessfully();
        harness.Users
            .Setup(users => users.GetCredentialStateAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((true, (string?)StoredHash, true, true));

        Result<LoginResponse> result = await harness.LoginAsync(RawPassword);

        result.IsFailure.Should().BeTrue();
        harness.PasswordHasher.Verify(
            hasher => hasher.Verify(It.IsAny<string>(), It.IsAny<string>()),
            Times.Never());
        harness.Users.Verify(
            users => users.RecordFailedLoginAsync(
                It.IsAny<int>(),
                It.IsAny<int>(),
                It.IsAny<TimeSpan>(),
                It.IsAny<DateTime>(),
                It.IsAny<CancellationToken>()),
            Times.Never(),
            "a locked account gains nothing from another recorded failure, and recording one would push "
            + "the lock instant forward on every attempt");
    }

    /// <summary>
    /// The lock is disclosed only to a caller already entitled to know, and never to the person signing in.
    /// </summary>
    [Fact]
    public async Task SignIn_DisclosesTheLockOnlyToAnEntitledCaller()
    {
        Harness anonymous = Harness.SignedInSuccessfully();
        anonymous.Users
            .Setup(users => users.GetCredentialStateAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((true, (string?)StoredHash, true, true));

        Result<LoginResponse> hidden = await anonymous.LoginAsync(RawPassword);

        hidden.Reason!.Code.Should().Be(
            InvalidCredentialsCode,
            "telling an unauthenticated caller that an account is locked confirms the account exists");
        hidden.Reason!.Message.Should().Be(GenericDenial);

        Harness host = Harness.SignedInSuccessfully();
        host.Users
            .Setup(users => users.GetCredentialStateAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((true, (string?)StoredHash, true, true));
        host.CurrentUser.SetupGet(caller => caller.IsAuthenticated).Returns(true);
        host.CurrentUser.SetupGet(caller => caller.IsSuperUser).Returns(true);

        Result<LoginResponse> disclosed = await host.LoginAsync(RawPassword);

        disclosed.Reason!.Code.Should().Be(LockedOutCode);
        disclosed.Reason!.Message.Should().Contain("locked");
    }

    /// <summary>
    /// The tenant administrator is entitled to the lock detail for their own tenant.
    /// </summary>
    [Fact]
    public async Task SignIn_DisclosesTheLockToTheTenantAdministrator()
    {
        Harness harness = Harness.SignedInSuccessfully();
        harness.Portal.AdministratorId = 42;
        harness.Users
            .Setup(users => users.GetCredentialStateAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((true, (string?)StoredHash, true, true));
        harness.CurrentUser.SetupGet(caller => caller.IsAuthenticated).Returns(true);
        harness.CurrentUser.SetupGet(caller => caller.IsSuperUser).Returns(false);
        harness.CurrentUser.SetupGet(caller => caller.UserId).Returns(42);

        Result<LoginResponse> result = await harness.LoginAsync(RawPassword);

        result.Reason!.Code.Should().Be(LockedOutCode);
    }

    /// <summary>
    /// An administrator of another tenant is not entitled to the lock detail.
    /// </summary>
    [Fact]
    public async Task SignIn_WithholdsTheLockFromAnAdministratorOfAnotherTenant()
    {
        Harness harness = Harness.SignedInSuccessfully();
        harness.Portal.AdministratorId = 42;
        harness.Users
            .Setup(users => users.GetCredentialStateAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((true, (string?)StoredHash, true, true));
        harness.CurrentUser.SetupGet(caller => caller.IsAuthenticated).Returns(true);
        harness.CurrentUser.SetupGet(caller => caller.IsSuperUser).Returns(false);
        harness.CurrentUser.SetupGet(caller => caller.UserId).Returns(99);

        Result<LoginResponse> result = await harness.LoginAsync(RawPassword);

        result.Reason!.Code.Should().Be(InvalidCredentialsCode);
    }

    /// <summary>
    /// Signing in with a credential the product shipped succeeds and carries an advisory.
    /// </summary>
    /// <param name="submitted">The shipped credential submitted.</param>
    [Theory]
    [InlineData("admin")]
    [InlineData("dnnadmin")]
    public async Task SignIn_AdvisesWhenTheAdministratorStillUsesAShippedCredential(string submitted)
    {
        Harness harness = Harness.SignedInSuccessfully();
        harness.Account.IsSuperUser = false;

        Result<LoginResponse> result = await harness.LoginAsync(submitted, username: "admin");

        result.IsSuccess.Should().BeTrue(
            "the advisory rides on a successful sign-in - refusing the credential would lock the "
            + "administrator out of the site they need in order to change it");
        result.Reason.Should().NotBeNull();
        result.Reason!.Code.Should().Be(InsecureAdminPasswordCode);
        result.Reason!.Message.Should().Contain("shipped credential");
    }

    /// <summary>
    /// The host account receives its own advisory.
    /// </summary>
    /// <param name="submitted">The shipped credential submitted.</param>
    [Theory]
    [InlineData("host")]
    [InlineData("dnnhost")]
    public async Task SignIn_AdvisesWhenTheHostStillUsesAShippedCredential(string submitted)
    {
        Harness harness = Harness.SignedInSuccessfully();
        harness.Account.IsSuperUser = true;

        Result<LoginResponse> result = await harness.LoginAsync(submitted, username: "host");

        result.IsSuccess.Should().BeTrue();
        result.Reason!.Code.Should().Be(InsecureHostPasswordCode);
    }

    /// <summary>
    /// A sign-in that uses neither shipped credential carries no advisory.
    /// </summary>
    [Fact]
    public async Task SignIn_CarriesNoAdvisoryForAnOrdinaryCredential()
    {
        Harness harness = Harness.SignedInSuccessfully();
        harness.Account.IsSuperUser = false;

        Result<LoginResponse> result = await harness.LoginAsync(RawPassword, username: "admin");

        result.IsSuccess.Should().BeTrue();
        result.Reason.Should().BeNull(
            "the advisory reports a shipped credential, not merely a privileged account name");
    }

    /// <summary>
    /// The shipped-credential advisory is matched on the account name without regard to case and on the
    /// credential exactly.
    /// </summary>
    [Fact]
    public async Task SignIn_MatchesTheShippedAccountNameLooselyAndTheCredentialExactly()
    {
        Harness mixedCaseName = Harness.SignedInSuccessfully();
        mixedCaseName.Account.IsSuperUser = false;

        Result<LoginResponse> named = await mixedCaseName.LoginAsync("admin", username: "ADMIN");

        named.Reason!.Code.Should().Be(
            InsecureAdminPasswordCode,
            "account names are compared without regard to case everywhere else, so the advisory must be "
            + "too, or it is trivially evaded");

        Harness mixedCasePassword = Harness.SignedInSuccessfully();
        mixedCasePassword.Account.IsSuperUser = false;

        Result<LoginResponse> credentialed = await mixedCasePassword.LoginAsync("Admin", username: "admin");

        credentialed.Reason.Should().BeNull(
            "credentials are case sensitive, so a differently-cased credential is a different credential "
            + "and is not the one the product shipped");
    }

    /// <summary>
    /// A sign-in missing either half of the credential is refused before anything is read or hashed.
    /// </summary>
    /// <param name="username">The submitted account name.</param>
    /// <param name="password">The submitted credential.</param>
    [Theory]
    [InlineData("", "Integr8tion!Pass")]
    [InlineData("   ", "Integr8tion!Pass")]
    [InlineData("measured_member", "")]
    public async Task SignIn_RequiresBothHalvesBeforeReadingAnything(string username, string password)
    {
        Harness harness = Harness.SignedInSuccessfully();

        Result<LoginResponse> result = await harness.LoginAsync(password, username);

        result.IsFailure.Should().BeTrue();
        result.Reason!.Code.Should().Be(RequestInvalidCode);
        harness.PasswordHasher.Verify(
            hasher => hasher.Verify(It.IsAny<string>(), It.IsAny<string>()),
            Times.Never());
        harness.Users.Verify(
            users => users.GetCredentialStateAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.Never());
        harness.Portals.Verify(
            portals => portals.GetByIdAsync(It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()),
            Times.Never(),
            "a request that cannot possibly succeed is refused without touching the store at all");
    }

    /// <summary>
    /// A sign-in request is required.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task SignIn_RequiresARequest()
    {
        Harness harness = Harness.SignedInSuccessfully();

        await Assert.ThrowsAsync<ArgumentNullException>(
            () => harness.Service.LoginAsync(null!, CancellationToken.None));
    }

    /// <summary>
    /// A whitespace-only credential is treated as supplied, because whitespace is a legal credential.
    /// </summary>
    [Fact]
    public async Task SignIn_TreatsAWhitespaceCredentialAsSupplied()
    {
        Harness harness = Harness.SignedInSuccessfully();

        Result<LoginResponse> result = await harness.LoginAsync("   ");

        result.IsSuccess.Should().BeTrue(
            "the account name is rejected when it is only whitespace but the credential is not, because "
            + "whitespace is a permissible credential and trimming it would silently change what the "
            + "caller typed");
        harness.PasswordHasher.Verify(hasher => hasher.Verify("   ", StoredHash), Times.Once());
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

        public User Account { get; }

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
        public static Harness SignedInSuccessfully()
        {
            Harness harness = new();

            harness.Clock.SetupGet(clock => clock.UtcNow).Returns(Now);

            harness.Portals
                .Setup(portals => portals.GetByIdAsync(
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
                    ExpiresAtUtc = Now.AddMinutes(30),
                }));

            return harness;
        }

        /// <summary>
        /// Signs in with a given credential and optional account name.
        /// </summary>
        /// <param name="password">The credential to submit.</param>
        /// <param name="username">The account name to submit.</param>
        /// <returns>The outcome.</returns>
        public Task<Result<LoginResponse>> LoginAsync(string password, string username = AccountName)
            => Service.LoginAsync(
                new LoginRequest { PortalId = PortalId, Username = username, Password = password },
                CancellationToken.None);
    }
}
