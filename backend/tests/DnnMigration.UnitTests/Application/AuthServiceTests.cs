using System.Reflection;
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

namespace DnnMigration.UnitTests.Application;

/// <summary>
/// Pins the sign-in verdict: which of the seven legacy sign-in outcomes admits a caller, which refuses one,
/// and which distinct reason each refusal carries.
/// </summary>
/// <remarks>
/// <para>
/// <b>What this suite owns.</b> The legacy sign-in screen reported its verdict by mutating a status
/// variable the caller had declared, and every consumer of that variable then decided for itself what the
/// value meant. That decision is now the service's, taken once, and this suite is where it is held still.
/// </para>
/// <para>
/// <b>What it deliberately leaves to others.</b> Hashing and token algorithms are verified against their
/// real implementations by the two security suites, so here the hashing and token abstractions are
/// substituted and only the ORCHESTRATION is asserted - that the comparison is performed, that a pair is
/// minted exactly once when a credential is accepted and never when one is refused, and that a rotation
/// retires the value presented to it.
/// </para>
/// </remarks>
public class AuthServiceApplicationTests
{
    /// <summary>
    /// The tenant every test signs in against. Minus one is deliberate rather than arbitrary: the tenant
    /// table's key is declared as an identity seeded at minus one, so minus one is a REAL tenant and the
    /// legacy integer stand-in for "absent" was the same value.
    /// </summary>
    private const int PortalId = -1;

    /// <summary>The account identifier every resolved account carries.</summary>
    private const int UserId = 41;

    /// <summary>The role identifier a tenant designates as conferring its administration.</summary>
    /// <remarks>
    /// ZERO ON PURPOSE. <c>Roles.RoleID</c> is declared <c>IDENTITY (0, 1)</c> (01.00.00.SqlDataProvider
    /// L114), so zero is the FIRST REAL ROLE rather than an absent value. A suite that used a comfortable
    /// positive number would pass against a derivation that tested the designation for truthiness, or
    /// coalesced it, and so read the first role in the installation as no role at all.
    /// </remarks>
    private const int AdministratorRoleId = 0;

    /// <summary>The account name submitted by every test that does not care about the name.</summary>
    private const string AccountName = "measured_member";

    /// <summary>
    /// The submitted credential. Never compared to anything: the substituted hashing abstraction decides
    /// the verdict, so this value only has to be a non-empty string that is not one of the two the product
    /// was distributed with.
    /// </summary>
    private const string SubmittedCredential = "not-a-real-credential-9F2C";

    /// <summary>
    /// The stored representation the account is found holding. An obvious placeholder rather than a real
    /// digest, so that nothing resembling a credential is committed to this repository.
    /// </summary>
    private const string StoredRepresentation = "$2a$12$stored-representation-placeholder";

    /// <summary>
    /// The stand-in the hashing abstraction publishes for an account that holds no stored representation,
    /// so that the comparison is performed at full cost even when there is nothing to compare against.
    /// </summary>
    private const string DecoyRepresentation = "decoy-representation-placeholder";

    /// <summary>The one sentence every refused credential receives, whatever actually closed the gate.</summary>
    private const string UniformDenial = "The account name or credential is not correct.";

    private const string RequestInvalidCode = "auth.request_invalid";
    private const string InvalidCredentialsCode = "auth.invalid_credentials";
    private const string LockedOutCode = "auth.locked_out";
    private const string InvalidRefreshTokenCode = "auth.invalid_refresh_token";
    private const string UserNotFoundCode = "auth.user_not_found";
    private const string VerificationRequiredCode = "auth.verification_required";
    private const string VerificationCodeInvalidCode = "auth.verification_code_invalid";
    private const string AccountNotApprovedCode = "auth.account_not_approved";
    private const string ApprovalStoreUnavailableCode = "auth.approval_store_unavailable";
    private const string InsecureAdminPasswordCode = "auth.insecure_admin_password";
    private const string InsecureHostPasswordCode = "auth.insecure_host_password";
    private const string TokenStoreUnavailableCode = "TOKEN_STORE_UNAVAILABLE";

    /// <summary>The refusal every path reports when the blocking remediation state cannot be evaluated.</summary>
    private const string RemediationStoreUnavailableCode = "auth.remediation.store_unavailable";

    /// <summary>The account name the product was distributed with for the tenant administrator.</summary>
    private const string ShippedAdministratorName = "admin";

    /// <summary>The account name the product was distributed with for the installation owner.</summary>
    private const string ShippedHostName = "host";

    /// <summary>
    /// The instant the injected clock reports for the whole of every test. Fixed, and in coordinated
    /// universal time, because the legacy read the server's local wall clock and a suite that did the same
    /// would pass or fail according to where it ran.
    /// </summary>
    private static readonly DateTime Now = new(2026, 8, 2, 12, 0, 0, DateTimeKind.Utc);

    /// <summary>
    /// The seven legacy status ordinals paired with the verdict the legacy sign-in screen reached for each,
    /// transcribed from the two statements that produced it.
    /// </summary>
    /// <remarks>
    /// Two consequences follow, and neither is obvious from reading L187 alone. The not-approved member
    /// never reaches that inequality, so it is refused even though it is not the failure member.
    /// </remarks>
    private static readonly IReadOnlyDictionary<UserLoginStatus, bool> LegacyVerdict =
        new Dictionary<UserLoginStatus, bool>
        {
            [UserLoginStatus.Failure] = false,
            [UserLoginStatus.Success] = true,
            [UserLoginStatus.SuperUser] = true,
            [UserLoginStatus.UserLockedOut] = true,
            [UserLoginStatus.UserNotApproved] = false,
            [UserLoginStatus.InsecureAdminPassword] = true,
            [UserLoginStatus.InsecureHostPassword] = true,
        };

    // ---------------------------------------------------------------------------------------------
    // Region A -- the seven-member status ladder, and the one place this migration narrows it.
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// An accepted credential admits the caller, and the two accepting members stay distinguishable from
    /// one another.
    /// </summary>
    /// <param name="isSuperUser">Whether the resolved account is an installation-wide account.</param>
    /// <param name="expectedAuditName">
    /// The audit event name the outcome must be recorded under, which is the legacy status member's own
    /// name as a string.
    /// </param>
    /// <returns>A task representing the assertions.</returns>
    /// <remarks>
    /// The legacy carried two separate accepting members rather than one, and the distinction is not
    /// cosmetic: an installation-wide sign-in and a tenant sign-in are different events to whoever reads
    /// the trail. Both are admitted, and each keeps its own name.
    /// </remarks>
    [Theory]
    [InlineData(false, "LOGIN_SUCCESS")]
    [InlineData(true, "LOGIN_SUPERUSER")]
    public async Task LoginAsync_AnAcceptedCredential_AdmitsTheCallerUnderItsOwnOutcome(
        bool isSuperUser,
        string expectedAuditName)
    {
        SignInHarness harness = SignInHarness.Ready();
        harness.IsSuperUser = isSuperUser;

        Result<LoginResponse> outcome = await harness.LoginAsync();

        outcome.IsSuccess.Should().BeTrue("an accepted credential is the one case the legacy and the target agree on");
        outcome.Value.AccessToken.Should().NotBeNullOrEmpty();

        harness.AuditRecords.Should().ContainSingle()
            .Which.EventName.Should().Be(
                expectedAuditName,
                "the two accepting members are separate events, exactly as the legacy enumeration made them");
    }

    /// <summary>
    /// The lockout and weak-credential members are the three the legacy verdict admitted and the target
    /// does not admit unconditionally - this test states that narrowing and its justification.
    /// </summary>
    /// <returns>A task representing the assertions.</returns>
    /// <remarks>
    /// The test name is long on purpose. It is the shortest sentence that says both what the target does
    /// and what the legacy did, so that a reader who breaks this test learns immediately that they have
    /// walked into a deliberate divergence rather than a defect.
    /// </remarks>
    [Fact]
    public async Task LoginAsync_LockedOutAndInsecurePasswordStatuses_AreTreatedAsFailures_NarrowingLegacyPredicate()
    {
        LegacyVerdict[UserLoginStatus.UserLockedOut].Should().BeTrue(
            "Login.ascx.vb:L187 admitted the lockout member, which is the defect this test documents");
        LegacyVerdict[UserLoginStatus.InsecureAdminPassword].Should().BeTrue(
            "the weak-credential members were admitted too, and that part is preserved");
        LegacyVerdict[UserLoginStatus.InsecureHostPassword].Should().BeTrue();

        SignInHarness locked = SignInHarness.Ready();
        locked.IsLockedOut = true;

        Result<LoginResponse> lockedOutcome = await locked.LoginAsync();

        lockedOutcome.IsFailure.Should().BeTrue(
            "a locked account is refused here even though the legacy predicate admitted it");
        locked.Tokens.Verify(
            tokens => tokens.IssueTokensAsync(
                It.IsAny<int>(),
                It.IsAny<int>(),
                It.IsAny<CancellationToken>()),
            Times.Never);

        SignInHarness insecureAdministrator = SignInHarness.Ready();
        Result<LoginResponse> administratorOutcome =
            await insecureAdministrator.LoginAsync(username: ShippedAdministratorName, password: "dnnadmin");

        administratorOutcome.IsSuccess.Should().BeTrue(
            "the weak-credential members remain admitted, so an installation never loses access to its own administrator");
        administratorOutcome.Reason!.Code.Should().Be(
            InsecureAdminPasswordCode,
            "what changes is that the caller is now TOLD, where the legacy admitted it silently");
    }

    /// <summary>
    /// Every refusal carries its own reason code: the five refusing conditions do not collapse into one
    /// undifferentiated failure.
    /// </summary>
    /// <returns>A task representing the assertions.</returns>
    /// <remarks>
    /// The legacy had a single boolean verdict and a message key set alongside it, so a caller could not
    /// tell a wrong credential from a locked account from a pending registration except by reading the
    /// message.
    /// </remarks>
    [Fact]
    public async Task LoginAsync_TheFiveRefusingConditions_CarryFiveDistinctReasonCodes()
    {
        List<string> codes = [];

        SignInHarness rejected = SignInHarness.Ready();
        rejected.CredentialMatches = false;
        codes.Add((await rejected.LoginAsync()).Reason!.Code);

        // The lock is disclosed only to a caller entitled to that detail, so this arrangement signs an
        // installation-wide caller in first. Without it the answer would be the uniform denial, which is
        // correct behaviour but would hide the distinction this test exists to draw.
        SignInHarness locked = SignInHarness.Ready();
        locked.IsLockedOut = true;
        locked.CallerIsSignedIn = true;
        locked.SignedInCallerIsSuperUser = true;
        codes.Add((await locked.LoginAsync()).Reason!.Code);

        SignInHarness awaitingCode = SignInHarness.Ready();
        awaitingCode.IsApproved = false;
        awaitingCode.Portal.UserRegistration = UserRegistrationMode.VerifiedRegistration;
        codes.Add((await awaitingCode.LoginAsync()).Reason!.Code);

        SignInHarness wrongCode = SignInHarness.Ready();
        wrongCode.IsApproved = false;
        wrongCode.Portal.UserRegistration = UserRegistrationMode.VerifiedRegistration;
        codes.Add((await wrongCode.LoginAsync(verificationCode: "0-0")).Reason!.Code);

        SignInHarness notAdmitted = SignInHarness.Ready();
        notAdmitted.IsApproved = false;
        notAdmitted.Portal.UserRegistration = UserRegistrationMode.PublicRegistration;
        codes.Add((await notAdmitted.LoginAsync()).Reason!.Code);

        codes.Should().Equal(
            [
                InvalidCredentialsCode,
                LockedOutCode,
                VerificationRequiredCode,
                VerificationCodeInvalidCode,
                AccountNotApprovedCode,
            ],
            "each refusing condition keeps its own reason, which is what replaced the single by-reference status");

        codes.Should().OnlyHaveUniqueItems("no two refusing conditions may share a code");
    }

    /// <summary>
    /// An unusable submission is reported as a malformed request rather than as a rejected credential, and
    /// an absent tenant is never defaulted.
    /// </summary>
    /// <param name="username">The account name submitted.</param>
    /// <param name="password">The credential submitted.</param>
    /// <param name="portalId">The tenant the submission was addressed to, where one was resolved.</param>
    /// <returns>A task representing the assertions.</returns>
    /// <remarks>
    /// The tenant case is the one worth stating: minus one and zero are both real tenants, so there is no
    /// integer this member could default an absent tenant to. Reporting the shape of the request also
    /// avoids the alternative mistake of answering "credential rejected" to a caller whose credential was
    /// never examined.
    /// </remarks>
    [Theory]
    [InlineData("", SubmittedCredential, PortalId)]
    [InlineData("   ", SubmittedCredential, PortalId)]
    [InlineData(AccountName, "", PortalId)]
    [InlineData(AccountName, SubmittedCredential, null)]
    public async Task LoginAsync_AnUnusableSubmission_ReportsTheRequestRatherThanTheCredential(
        string username,
        string password,
        int? portalId)
    {
        SignInHarness harness = SignInHarness.Ready();

        Result<LoginResponse> outcome = await harness.Service.LoginAsync(
            new LoginRequest
            {
                PortalId = portalId,
                Username = username,
                Password = password,
            },
            CancellationToken.None);

        outcome.IsFailure.Should().BeTrue();
        outcome.Reason!.Code.Should().Be(RequestInvalidCode);

        harness.PasswordHasher.Verify(
            hasher => hasher.Verify(It.IsAny<string>(), It.IsAny<string>()),
            Times.Never,
            "an unusable submission is rejected before any comparison is attempted");
    }

    // ---------------------------------------------------------------------------------------------
    // Region B -- the two weak-credential members are PROMOTIONS from the two accepting members.
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// The weak-credential members are promotions from the accepting members, so each is reachable only
    /// from the one outcome it is promoted from.
    /// </summary>
    /// <param name="isSuperUser">Whether the resolved account is an installation-wide account.</param>
    /// <param name="username">The account name submitted.</param>
    /// <param name="password">One of the two credentials the product was distributed with.</param>
    /// <param name="expectedAdvisory">The advisory the promotion must attach.</param>
    /// <returns>A task representing the assertions.</returns>
    /// <remarks>
    /// The promotion is therefore not a refusal in disguise. A caller who reaches it has presented the
    /// correct credential; what the outcome adds is that the credential is publicly known.
    /// </remarks>
    [Theory]
    [InlineData(false, ShippedAdministratorName, "admin", InsecureAdminPasswordCode)]
    [InlineData(false, ShippedAdministratorName, "dnnadmin", InsecureAdminPasswordCode)]
    [InlineData(true, ShippedHostName, "host", InsecureHostPasswordCode)]
    [InlineData(true, ShippedHostName, "dnnhost", InsecureHostPasswordCode)]
    public async Task LoginAsync_AShippedCredential_PromotesTheAcceptingOutcomeWithoutRefusingIt(
        bool isSuperUser,
        string username,
        string password,
        string expectedAdvisory)
    {
        SignInHarness harness = SignInHarness.Ready();
        harness.IsSuperUser = isSuperUser;
        harness.Account.Username = username;

        Result<LoginResponse> outcome = await harness.LoginAsync(username: username, password: password);

        outcome.IsSuccess.Should().BeTrue("a promoted outcome is still an accepted one");
        outcome.Reason.Should().NotBeNull("the advisory travels as an informational reason on a success");
        outcome.Reason!.Code.Should().Be(expectedAdvisory);
        outcome.Value.MustChangePassword.Should().BeTrue(
            "forcing a credential change is the remediation the legacy intended for both members");

        harness.Tokens.Verify(
            tokens => tokens.IssueTokensAsync(
                It.IsAny<int>(),
                It.IsAny<int>(),
                It.IsAny<CancellationToken>()),
            Times.Once,
            "the caller is signed in, so exactly one pair is minted");
    }

    /// <summary>
    /// The promotion is gated on the pairing of a distributed account name with a distributed credential:
    /// neither half alone promotes anything.
    /// </summary>
    /// <param name="username">The account name submitted.</param>
    /// <param name="password">The credential submitted.</param>
    /// <returns>A task representing the assertions.</returns>
    [Theory]
    [InlineData(AccountName, "admin")]
    [InlineData(AccountName, "dnnhost")]
    [InlineData(ShippedAdministratorName, SubmittedCredential)]
    [InlineData(ShippedHostName, SubmittedCredential)]
    public async Task LoginAsync_WithOnlyHalfOfADistributedPairing_PromotesNothing(
        string username,
        string password)
    {
        SignInHarness harness = SignInHarness.Ready();
        harness.Account.Username = username;

        Result<LoginResponse> outcome = await harness.LoginAsync(username: username, password: password);

        outcome.IsSuccess.Should().BeTrue();
        outcome.Reason.Should().BeNull("no advisory is attached unless BOTH halves are the distributed ones");
        outcome.Value.MustChangePassword.Should().BeFalse();
    }

    /// <summary>
    /// A promoted sign-in leaves exactly one audit record, and that record describes an acceptance under
    /// the name of the outcome the promotion came from.
    /// </summary>
    /// <param name="isSuperUser">Whether the resolved account is an installation-wide account.</param>
    /// <param name="username">The distributed account name submitted.</param>
    /// <param name="password">The distributed credential submitted.</param>
    /// <param name="expectedAuditName">The audit event name the promotion must be recorded under.</param>
    /// <returns>A task representing the assertions.</returns>
    /// <remarks>
    /// The required behaviour is stated positively here: one record, describing an acceptance, named for
    /// the pre-promotion outcome, so that an installation still signing in with a published credential is
    /// visible in the trail as the tenant or installation-wide sign-in it actually is.
    /// </remarks>
    [Theory]
    [InlineData(false, ShippedAdministratorName, "dnnadmin", "LOGIN_SUCCESS")]
    [InlineData(true, ShippedHostName, "dnnhost", "LOGIN_SUPERUSER")]
    public async Task LoginAsync_APromotedSignIn_IsAuditedOnceAsAnAcceptance(
        bool isSuperUser,
        string username,
        string password,
        string expectedAuditName)
    {
        SignInHarness harness = SignInHarness.Ready();
        harness.IsSuperUser = isSuperUser;
        harness.Account.Username = username;

        Result<LoginResponse> outcome = await harness.LoginAsync(username: username, password: password);

        outcome.IsSuccess.Should().BeTrue();

        AuditEvent record = harness.AuditRecords.Should().ContainSingle(
            "a single sign-in is a single event, whether or not its credential was a published one").Subject;

        record.Outcome.Should().Be(
            AuditOutcome.Succeeded,
            "the caller WAS admitted, so no record of this sign-in may describe a denial");
        record.EventName.Should().Be(
            expectedAuditName,
            "the promoted member is recorded under the accepting member it was promoted from");
        record.FailureCode.Should().BeNull("an acceptance carries no failure code");
        record.ActorUserId.Should().Be(UserId);
    }

    // ---------------------------------------------------------------------------------------------
    // Region C -- ordinal fidelity, and the audit name that depends on the member name.
    // ---------------------------------------------------------------------------------------------

    /// <summary>All seven legacy ordinals survive the rename, contiguously and in their original order.</summary>
    /// <remarks>
    /// The ordinals are load-bearing rather than incidental. The legacy enumeration was declared with
    /// explicit values 0 through 6, the status travelled by value through a by-reference argument, and the
    /// value reached comparisons in code the migration does not own.
    /// </remarks>
    [Fact]
    public void UserLoginStatus_PreservesAllSevenLegacyOrdinals()
    {
        ((int)UserLoginStatus.Failure).Should().Be(0);
        ((int)UserLoginStatus.Success).Should().Be(1);
        ((int)UserLoginStatus.SuperUser).Should().Be(2);
        ((int)UserLoginStatus.UserLockedOut).Should().Be(3);
        ((int)UserLoginStatus.UserNotApproved).Should().Be(4);
        ((int)UserLoginStatus.InsecureAdminPassword).Should().Be(5);
        ((int)UserLoginStatus.InsecureHostPassword).Should().Be(6);

        Enum.GetValues<UserLoginStatus>().Should().HaveCount(
            7,
            "the legacy enumeration had exactly seven members and the migration neither drops nor adds one");

        Enum.GetValues<UserLoginStatus>().Select(status => (int)status).Should().Equal(
            [0, 1, 2, 3, 4, 5, 6],
            "the members stay contiguous, so a value read as an integer still means what it meant");
    }

    /// <summary>
    /// The rename is a pure case change: every member maps back to the legacy member name it replaced.
    /// </summary>
    [Fact]
    public void UserLoginStatus_MembersAreThePascalCaseRenameOfTheLegacyMemberNames()
    {
        Dictionary<UserLoginStatus, string> legacyNames = new()
        {
            [UserLoginStatus.Failure] = "LOGIN_FAILURE",
            [UserLoginStatus.Success] = "LOGIN_SUCCESS",
            [UserLoginStatus.SuperUser] = "LOGIN_SUPERUSER",
            [UserLoginStatus.UserLockedOut] = "LOGIN_USERLOCKEDOUT",
            [UserLoginStatus.UserNotApproved] = "LOGIN_USERNOTAPPROVED",
            [UserLoginStatus.InsecureAdminPassword] = "LOGIN_INSECUREADMINPASSWORD",
            [UserLoginStatus.InsecureHostPassword] = "LOGIN_INSECUREHOSTPASSWORD",
        };

        legacyNames.Should().HaveCount(
            Enum.GetValues<UserLoginStatus>().Length,
            "every member of the target enumeration is accounted for in the mapping");

        foreach ((UserLoginStatus status, string legacyName) in legacyNames)
        {
            string flattened = legacyName
                .Replace("_", string.Empty, StringComparison.Ordinal)
                .Replace("LOGIN", string.Empty, StringComparison.Ordinal);

            status.ToString().ToUpperInvariant().Should().Be(
                flattened,
                "the rename changes only casing and the member prefix, never the identity of the member");
        }
    }

    /// <summary>
    /// The audit event names are the legacy member names verbatim, which is what keeps the trail readable
    /// across the rename.
    /// </summary>
    /// <remarks>
    /// The migration keeps the two apart deliberately: the members are renamed for the target language and
    /// the audit names are pinned to the legacy strings, so the audit intent survives the change of
    /// mechanism. No divergence is introduced here, so this needs no entry beyond the mapping itself.
    /// </remarks>
    [Fact]
    public void AuditEventNames_PreserveTheLegacyLogTypeKeyStrings()
    {
        AuditEventNames.LoginSuccess.Should().Be("LOGIN_SUCCESS");
        AuditEventNames.LoginSuperUser.Should().Be("LOGIN_SUPERUSER");
        AuditEventNames.LoginFailure.Should().Be("LOGIN_FAILURE");
        AuditEventNames.LoginUserLockedOut.Should().Be("LOGIN_USERLOCKEDOUT");
        AuditEventNames.LoginUserNotApproved.Should().Be("LOGIN_USERNOTAPPROVED");

        AuditEventNames.SessionRenewed.Should().Be("SESSION_RENEWED");
        AuditEventNames.SessionRefused.Should().Be("SESSION_REFUSED");
        AuditEventNames.SessionEnded.Should().Be("SESSION_ENDED");
    }

    /// <summary>
    /// The two outcomes the legacy recorded are recorded here too, under their legacy names and naming the
    /// account the legacy could not.
    /// </summary>
    /// <returns>A task representing the assertions.</returns>
    [Fact]
    public async Task LoginAsync_TheTwoOutcomesTheLegacyAudited_AreRecordedUnderTheirLegacyNames()
    {
        SignInHarness rejected = SignInHarness.Ready();
        rejected.CredentialMatches = false;

        await rejected.LoginAsync();

        AuditEvent rejectedRecord = rejected.AuditRecords.Should().ContainSingle().Subject;
        rejectedRecord.EventName.Should().Be(AuditEventNames.LoginFailure);
        rejectedRecord.Outcome.Should().Be(AuditOutcome.Denied);
        rejectedRecord.ActorUserId.Should().Be(UserId, "and not the legacy minus-one stand-in");

        SignInHarness locked = SignInHarness.Ready();
        locked.IsLockedOut = true;

        await locked.LoginAsync();

        AuditEvent lockedRecord = locked.AuditRecords.Should().ContainSingle().Subject;
        lockedRecord.EventName.Should().Be(AuditEventNames.LoginUserLockedOut);
        lockedRecord.Outcome.Should().Be(AuditOutcome.Denied);
        lockedRecord.ActorUserId.Should().Be(UserId);
    }

    /// <summary>No audit record carries a credential, a stored representation or a token value.</summary>
    /// <returns>A task representing the assertions.</returns>
    /// <remarks>
    /// Asserted over the records produced by an accepted sign-in and by a refused one, since the refusal
    /// path is the one holding a credential it has just judged wrong and therefore the likelier place for
    /// one to be recorded "for diagnosis".
    /// </remarks>
    [Fact]
    public async Task LoginAsync_RecordsNoCredentialTokenOrStoredRepresentation()
    {
        SignInHarness accepted = SignInHarness.Ready();
        await accepted.LoginAsync();

        SignInHarness refused = SignInHarness.Ready();
        refused.CredentialMatches = false;
        await refused.LoginAsync();

        IEnumerable<string?> everyRecordedValue = accepted.AuditRecords
            .Concat(refused.AuditRecords)
            .SelectMany(record => record.Properties.Values
                .Concat([record.EventName, record.FailureCode, record.ResourceId]));

        foreach (string? value in everyRecordedValue)
        {
            if (value is null)
            {
                continue;
            }

            value.Should().NotContain(SubmittedCredential, "a submitted credential is never recorded");
            value.Should().NotContain(StoredRepresentation, "a stored representation is never recorded");
            value.Should().NotContain(DecoyRepresentation);
            value.Should().NotContain(SignInHarness.MintedAccessToken, "a token value is never recorded");
            value.Should().NotContain(SignInHarness.MintedRefreshToken);
        }
    }

    // ---------------------------------------------------------------------------------------------
    // Region D -- the four-way ladder a not-yet-approved account walks, and when it is walked.
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// The not-approved member resolves to the same four outcomes the legacy screen resolved, in the same
    /// four circumstances.
    /// </summary>
    /// <param name="registration">The tenant's registration mode.</param>
    /// <param name="verificationCode">The verification code submitted, where one was.</param>
    /// <param name="expectedCode">The reason code the target reports.</param>
    /// <param name="legacyMessageKey">The message key the legacy screen selected in the same circumstance.</param>
    /// <returns>A task representing the assertions.</returns>
    /// <remarks>
    /// The first and third leaves are one case here rather than two, and that is a faithful mapping rather
    /// than a lost distinction. What separated them in the legacy was <em>whether the screen had already
    /// rendered the code fields</em> - a property of the page's own view state, not of the request.
    /// </remarks>
    [Theory]
    [InlineData(UserRegistrationMode.VerifiedRegistration, null, VerificationRequiredCode, "EnterCode")]
    [InlineData(UserRegistrationMode.VerifiedRegistration, "", VerificationRequiredCode, "EnterCode")]
    [InlineData(UserRegistrationMode.VerifiedRegistration, "99-99", VerificationCodeInvalidCode, "InvalidCode")]
    [InlineData(UserRegistrationMode.NoRegistration, null, AccountNotApprovedCode, "UserNotAuthorized")]
    [InlineData(UserRegistrationMode.PrivateRegistration, null, AccountNotApprovedCode, "UserNotAuthorized")]
    [InlineData(UserRegistrationMode.PublicRegistration, "99-99", AccountNotApprovedCode, "UserNotAuthorized")]
    public async Task LoginAsync_ANotYetApprovedAccount_WalksTheLegacyLadder(
        UserRegistrationMode registration,
        string? verificationCode,
        string expectedCode,
        string legacyMessageKey)
    {
        SignInHarness harness = SignInHarness.Ready();
        harness.IsApproved = false;
        harness.Portal.UserRegistration = registration;

        Result<LoginResponse> outcome = await harness.LoginAsync(verificationCode: verificationCode);

        outcome.IsFailure.Should().BeTrue(
            "the legacy diverted this member before its verdict, so it was never admitted either");
        outcome.Reason!.Code.Should().Be(
            expectedCode,
            "this reason stands in for the legacy message key {0}",
            legacyMessageKey);
        outcome.Reason.Message.Should().NotBe(
            UniformDenial,
            "the ladder is explained rather than hidden behind the uniform denial, because the credential was proved");

        harness.AuditRecords.Should().ContainSingle()
            .Which.EventName.Should().Be(AuditEventNames.LoginUserNotApproved);

        harness.UnitOfWork.Verify(
            work => work.SaveChangesAsync(It.IsAny<CancellationToken>()),
            Times.Never,
            "a refused sign-in commits nothing");
    }

    /// <summary>
    /// The correct verification code admits the account, records the approval, and commits exactly once.
    /// </summary>
    /// <returns>A task representing the assertions.</returns>
    /// <remarks>
    /// The code is not stored anywhere - no column for it appears in the schema chain - so it is recomputed
    /// from the tenant and account identifiers joined by a hyphen, which is how the legacy provider
    /// composed it. An account left awaiting verification at cut-over therefore holds a code this
    /// comparison still accepts, which is the property that makes the migration non-disruptive.
    /// </remarks>
    [Fact]
    public async Task LoginAsync_TheCorrectVerificationCode_ApprovesTheAccountAndCommitsExactlyOnce()
    {
        SignInHarness harness = SignInHarness.Ready();
        harness.IsApproved = false;
        harness.Portal.UserRegistration = UserRegistrationMode.VerifiedRegistration;

        string composedCode = FormattableString.Invariant($"{PortalId}-{UserId}");

        Result<LoginResponse> outcome = await harness.LoginAsync(verificationCode: composedCode);

        outcome.IsSuccess.Should().BeTrue("a verified registration signs in on the same request that verifies it");

        harness.Users.Verify(
            users => users.SetApprovalAsync(UserId, true, It.IsAny<CancellationToken>()),
            Times.Once);

        harness.UnitOfWork.Verify(
            work => work.SaveChangesAsync(It.IsAny<CancellationToken>()),
            Times.Once,
            "approving a registration is the one branch of a sign-in that changes a tracked row");

        harness.Account.IsApproved.Should().BeTrue();
    }

    /// <summary>
    /// A correct code that could not be recorded keeps the gate closed and distinguishes itself from a
    /// wrong code.
    /// </summary>
    /// <returns>A task representing the assertions.</returns>
    [Fact]
    public async Task LoginAsync_AnApprovalThatCannotBeRecorded_RefusesWithItsOwnReasonAndCommitsNothing()
    {
        SignInHarness harness = SignInHarness.Ready();
        harness.IsApproved = false;
        harness.ApprovalAccepted = false;
        harness.Portal.UserRegistration = UserRegistrationMode.VerifiedRegistration;

        Result<LoginResponse> outcome =
            await harness.LoginAsync(verificationCode: FormattableString.Invariant($"{PortalId}-{UserId}"));

        outcome.IsFailure.Should().BeTrue();
        outcome.Reason!.Code.Should().Be(ApprovalStoreUnavailableCode);
        outcome.Reason.Code.Should().NotBe(VerificationCodeInvalidCode, "the code WAS correct");

        harness.UnitOfWork.Verify(
            work => work.SaveChangesAsync(It.IsAny<CancellationToken>()),
            Times.Never);
        harness.Account.IsApproved.Should().BeFalse();
    }

    /// <summary>
    /// The approval ladder is reached only once the credential has been proved, so a wrong credential
    /// approves nothing.
    /// </summary>
    /// <returns>A task representing the assertions.</returns>
    /// <remarks>
    /// The gate order is deliberately not the legacy order, and this is the test that fixes it in place.
    /// The legacy provider evaluated the lock, then approval, then the credential, which meant the approval
    /// gate PERSISTED AN APPROVAL before any credential had been compared.
    /// </remarks>
    [Fact]
    public async Task LoginAsync_TheApprovalLadder_IsReachedOnlyAfterTheCredentialIsProved()
    {
        SignInHarness harness = SignInHarness.Ready();
        harness.IsApproved = false;
        harness.CredentialMatches = false;
        harness.Portal.UserRegistration = UserRegistrationMode.VerifiedRegistration;

        Result<LoginResponse> outcome =
            await harness.LoginAsync(verificationCode: FormattableString.Invariant($"{PortalId}-{UserId}"));

        outcome.IsFailure.Should().BeTrue();
        outcome.Reason!.Code.Should().Be(
            InvalidCredentialsCode,
            "a caller who has proved nothing receives the uniform denial and learns nothing about the account");

        harness.Users.Verify(
            users => users.SetApprovalAsync(It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "a wrong credential approves nothing, which is the whole point of the reordering");
        harness.UnitOfWork.Verify(
            work => work.SaveChangesAsync(It.IsAny<CancellationToken>()),
            Times.Never);

        harness.Users.Verify(
            users => users.RecordFailedLoginAsync(
                UserId,
                It.IsAny<int>(),
                It.IsAny<TimeSpan>(),
                It.IsAny<DateTime>(),
                It.IsAny<CancellationToken>()),
            Times.Once,
            "and the attempt counts, where under the legacy order a pending account could be guessed at without limit");
    }

    /// <summary>An installation-wide account is exempt from the approval ladder.</summary>
    /// <returns>A task representing the assertions.</returns>
    /// <remarks>
    /// The exemption is the legacy provider's own and it follows from how such an account comes into
    /// existence: it is created by the installer, so it has no verification code to present and no tenant
    /// to be approved into.
    /// </remarks>
    [Fact]
    public async Task LoginAsync_AnInstallationWideAccount_IsExemptFromTheApprovalLadder()
    {
        SignInHarness harness = SignInHarness.Ready();
        harness.IsSuperUser = true;
        harness.IsApproved = false;
        harness.Portal.UserRegistration = UserRegistrationMode.VerifiedRegistration;

        Result<LoginResponse> outcome = await harness.LoginAsync();

        outcome.IsSuccess.Should().BeTrue();
        harness.AuditRecords.Should().ContainSingle()
            .Which.EventName.Should().Be(AuditEventNames.LoginSuperUser);
    }

    /// <summary>
    /// A locked account's lock is disclosed to whoever PROVED the credential, and to an entitled caller,
    /// and to nobody else.
    /// </summary>
    /// <param name="credentialMatches">Whether the submitted credential is the account's own.</param>
    /// <param name="callerIsSuperUser">Whether the signed-in caller is an installation-wide account.</param>
    /// <param name="callerIsTenantAdministrator">Whether the signed-in caller administers this tenant.</param>
    /// <param name="expectedCode">The reason code such a caller receives.</param>
    /// <returns>A task representing the assertions.</returns>
    /// <remarks>
    /// <para>
    /// ⚠ THE FIRST ROW USED TO EXPECT THE UNIFORM DENIAL, AND THAT IS THE DEFECT THIS NOW PROVES CLOSED.
    /// Runtime testing measured the cost of withholding the lock from the account's own holder: somebody who
    /// typed their CORRECT password was told "The account name or credential is not correct." - a false
    /// statement - with no wait time, no counter, no warning on the attempt that triggered the lock and no
    /// recovery route. Across five failed attempts and the sixth, correct one the sentence never changed;
    /// only the correlation identifier did.
    /// </para>
    /// <para>
    /// It is still not an enumeration oracle, and the second row is what establishes that. Disclosure is
    /// gated on the credential having been ACCEPTED, so somebody probing account names never reaches the
    /// disclosing branch and receives the same uniform refusal as before, byte for byte. This is also the
    /// standard the approval ladder already met - a not-approved account IS reported distinctly behind a
    /// correct credential - so lockout alone being generic protected nothing and misdirected the one person
    /// entitled to know.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData(true, false, false, LockedOutCode)]
    [InlineData(false, false, false, InvalidCredentialsCode)]
    [InlineData(true, true, false, LockedOutCode)]
    [InlineData(true, false, true, LockedOutCode)]
    [InlineData(false, true, false, LockedOutCode)]
    public async Task LoginAsync_ALockedAccount_DisclosesTheLockOnlyToAnEntitledCaller(
        bool credentialMatches,
        bool callerIsSuperUser,
        bool callerIsTenantAdministrator,
        string expectedCode)
    {
        SignInHarness harness = SignInHarness.Ready();
        harness.IsLockedOut = true;
        harness.CredentialMatches = credentialMatches;
        harness.SignedInCallerIsSuperUser = callerIsSuperUser;

        if (callerIsSuperUser || callerIsTenantAdministrator)
        {
            harness.SignedInCallerUserId = callerIsTenantAdministrator ? SignInHarness.AdministratorId : 900;
            harness.CallerIsSignedIn = true;
        }

        Result<LoginResponse> outcome = await harness.LoginAsync();

        outcome.IsFailure.Should().BeTrue();
        outcome.Reason!.Code.Should().Be(expectedCode);
    }

    /// <summary>
    /// The advisory a locked-out account's own holder receives names a wait and a recovery route rather than
    /// stating an internal fact.
    /// </summary>
    /// <returns>A task representing the assertions.</returns>
    /// <remarks>
    /// The wait is deliberately not asserted as a literal: it is read from the installation's own automatic
    /// unlock window, so a fixed number here would be a second source of truth that could disagree with the
    /// mechanism. What IS asserted is that the sentence is addressed to a person, carries a recovery route,
    /// and is not the operator-facing sentence an entitled caller receives.
    /// </remarks>
    [Fact]
    public async Task LoginAsync_ALockedAccountsOwnHolder_ReceivesAnActionableAdvisory()
    {
        SignInHarness harness = SignInHarness.Ready();
        harness.IsLockedOut = true;

        Result<LoginResponse> outcome = await harness.LoginAsync();

        outcome.Reason!.Code.Should().Be(LockedOutCode);

        string advisory = outcome.Reason!.Message;

        advisory.Should().Contain(
            "locked",
            "the state is named, which is the whole point: the previous sentence denied it");
        advisory.Should().Contain(
            "administrator",
            "and a recovery route is offered, because the legacy pointed at a password reminder this port has no endpoint or screen for");
        advisory.Should().NotContain(
            "The account name or credential is not correct.",
            "the false statement this replaces");
        advisory.Should().NotContain(
            FormattableString.Invariant($"Account {UserId}"),
            "the operator-facing sentence names the identifier; the reader-facing one must not");
    }

    // ---------------------------------------------------------------------------------------------
    // Region E -- token orchestration. The algorithm belongs to the token suite; the sequencing is here.
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// An accepted credential mints exactly one pair using only the stable identity required by the
    /// authority-minimised token contract.
    /// </summary>
    /// <returns>A task representing the assertions.</returns>
    /// <remarks>
    /// Mutable authority is deliberately absent from the token contract. The response carries an empty
    /// authority projection and clients load current roles and permissions through the explicit
    /// current-user endpoint.
    /// </remarks>
    [Fact]
    public async Task LoginAsync_AnAcceptedCredential_MintsExactlyOnePairFromTheCallersOwnFacts()
    {
        SignInHarness harness = SignInHarness.Ready();
        harness.RoleNames = ["Administrators", "Registered Users"];
        harness.PermissionKeys = ["VIEW", "EDIT"];

        Result<LoginResponse> outcome = await harness.LoginAsync();

        outcome.IsSuccess.Should().BeTrue();

        harness.Tokens.Verify(
            tokens => tokens.IssueTokensAsync(
                UserId,
                PortalId,
                It.IsAny<CancellationToken>()),
            Times.Once);

        // ⚠ REVERSED DELIBERATELY. These two assertions used to require EMPTY collections, which is the
        // behaviour a runtime audit reported as a defect: a caller who signed in and read the current-user
        // endpoint a moment later received two disagreeing descriptions of one session. An empty collection
        // is an assertion that the account holds nothing, not an omission, and for an administrator it is
        // false. Withholding them bought no confidentiality either, because the current-user read publishes
        // exactly these members to exactly this caller a moment later. The ACCESS TOKEN still carries no
        // authority at all, which is a separate property and is asserted separately.
        outcome.Value.User.Roles.Should().NotBeEmpty(
            "the sign-in answer states the authority the caller actually holds");
        outcome.Value.User.Permissions.Should().NotBeEmpty(
            "and states it from the same resolution the current-user read uses, so the two agree");
        outcome.Value.User.PortalId.Should().Be(PortalId, "and minus one survives as a real tenant identifier");
    }

    /// <summary>No refusal mints a token, whichever gate closed.</summary>
    /// <param name="arrangement">The refusing condition to arrange.</param>
    /// <returns>A task representing the assertions.</returns>
    /// <remarks>
    /// Stated once across every refusing condition rather than once per condition, because "no token on
    /// failure" is a property of the whole method and a new refusing branch should inherit it.
    /// </remarks>
    [Theory]
    [InlineData(RefusalArrangement.WrongCredential)]
    [InlineData(RefusalArrangement.UnknownAccount)]
    [InlineData(RefusalArrangement.UnknownTenant)]
    [InlineData(RefusalArrangement.NoStoredRepresentation)]
    [InlineData(RefusalArrangement.LockedAccount)]
    [InlineData(RefusalArrangement.AwaitingApproval)]
    public async Task LoginAsync_ARefusedCredential_MintsNothing(RefusalArrangement arrangement)
    {
        SignInHarness harness = SignInHarness.Refusing(arrangement);

        Result<LoginResponse> outcome = await harness.LoginAsync();

        outcome.IsFailure.Should().BeTrue();

        harness.Tokens.Verify(
            tokens => tokens.IssueTokensAsync(
                It.IsAny<int>(),
                It.IsAny<int>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
    }

    /// <summary>
    /// The expiry the caller receives is the one the token contract minted, computed from the injected
    /// clock and the configured lifetime and never recomputed here.
    /// </summary>
    /// <returns>A task representing the assertions.</returns>
    /// <remarks>
    /// The configured lifetime lives on the bound token settings and is read by the token implementation,
    /// not by this service - which is exactly why the assertion is that the value is PROPAGATED unchanged.
    /// A service that recomputed the expiry from its own clock reading would produce a value that drifted
    /// from the one the token itself asserts, and the two would disagree by however long the sign-in took.
    /// </remarks>
    [Fact]
    public async Task LoginAsync_PropagatesTheMintedExpiryUnchanged()
    {
        SignInHarness harness = SignInHarness.Ready();
        harness.TokenSettings.ExpirationMinutes = 15;

        Result<LoginResponse> outcome = await harness.LoginAsync();

        outcome.IsSuccess.Should().BeTrue();
        outcome.Value.ExpiresAtUtc.Should().Be(
            Now.AddMinutes(harness.TokenSettings.ExpirationMinutes),
            "the expiry is the minted one, derived from the injected clock plus the configured lifetime");
        outcome.Value.ExpiresAtUtc.Kind.Should().Be(
            DateTimeKind.Utc,
            "coordinated universal time throughout, where the legacy read the server's local wall clock");
        outcome.Value.AccessToken.Should().Be(SignInHarness.MintedAccessToken);
        outcome.Value.RefreshToken.Should().Be(SignInHarness.MintedRefreshToken);
    }

    /// <summary>
    /// A credential that was accepted but whose session could not be recorded reports the store's own
    /// reason rather than a rejected credential.
    /// </summary>
    /// <returns>A task representing the assertions.</returns>
    /// <remarks>
    /// The distinction matters to the caller: one of these is worth retrying and the other is not. The
    /// reason travels unchanged, keeping the token contract's own code form, so the two are never confused.
    /// </remarks>
    [Fact]
    public async Task LoginAsync_WhenTheSessionCannotBeRecorded_ReportsTheStoreRatherThanTheCredential()
    {
        SignInHarness harness = SignInHarness.Ready();
        harness.TokenStoreAvailable = false;

        Result<LoginResponse> outcome = await harness.LoginAsync();

        outcome.IsFailure.Should().BeTrue();
        outcome.Reason!.Code.Should().Be(TokenStoreUnavailableCode);
        outcome.Reason.Code.Should().NotBe(
            InvalidCredentialsCode,
            "the credential was correct and telling the caller otherwise would send it to reset a working credential");
    }

    /// <summary>
    /// A rotation issues a new pair through the token contract, which is where the presented value is
    /// retired.
    /// </summary>
    /// <returns>A task representing the assertions.</returns>
    /// <remarks>
    /// Rotation is single-use by contract: exchanging a value mints a successor and marks the presented
    /// value used in the same atomic unit, so the retirement is asserted as a delegation to that contract
    /// rather than re-implemented here.
    /// </remarks>
    [Fact]
    public async Task RefreshAsync_AValidToken_RotatesThroughTheTokenContractAndReReadsTheCaller()
    {
        SignInHarness harness = SignInHarness.Ready();
        harness.RoleNames = ["Subscribers"];

        Result<LoginResponse> outcome = await harness.RefreshAsync();

        outcome.IsSuccess.Should().BeTrue();
        outcome.Value.AccessToken.Should().Be(SignInHarness.RotatedAccessToken);
        outcome.Value.RefreshToken.Should().Be(
            SignInHarness.RotatedRefreshToken,
            "a successor value is issued, which is what retires the one presented");
        outcome.Value.User.Roles.Should().NotBeEmpty(
            "a rotated pair describes the session as the current-user read would, so the two never disagree");

        harness.Tokens.Verify(
            tokens => tokens.RefreshAsync(
                SignInHarness.PresentedRefreshToken,
                SignInHarness.ClientBinding,
                It.IsAny<CancellationToken>()),
            Times.Once);
        harness.Tokens.Verify(
            tokens => tokens.IssueTokensAsync(
                It.IsAny<int>(),
                It.IsAny<int>(),
                It.IsAny<CancellationToken>()),
            Times.Never,
            "rotation is not a second sign-in, so the issuing member is not called again");

        harness.AuditRecords.Should().ContainSingle()
            .Which.EventName.Should().Be(AuditEventNames.SessionRenewed);
    }

    /// <summary>
    /// A refresh token that is unknown, expired, already redeemed or revoked receives one answer and mints
    /// nothing.
    /// </summary>
    /// <returns>A task representing the assertions.</returns>
    [Fact]
    public async Task RefreshAsync_AnUnknownOrExpiredToken_ReportsOneReasonAndMintsNothing()
    {
        SignInHarness harness = SignInHarness.Ready();
        harness.RotationOutcome = Result<LoginResponse>.Failure(
            "token.expired",
            "The refresh token has passed its absolute expiry.");

        Result<LoginResponse> outcome = await harness.RefreshAsync();

        outcome.IsFailure.Should().BeTrue();
        outcome.Reason!.Code.Should().Be(InvalidRefreshTokenCode);
        outcome.Reason.Code.Should().NotBe("token.expired", "the store's own distinctions do not reach the caller");

        harness.Tokens.Verify(
            tokens => tokens.IssueTokensAsync(
                It.IsAny<int>(),
                It.IsAny<int>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
    }

    /// <summary>A blank refresh token is refused without troubling the token store.</summary>
    /// <param name="presented">The blank value submitted.</param>
    /// <returns>A task representing the assertions.</returns>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task RefreshAsync_ABlankToken_IsRefusedWithoutTouchingTheStore(string presented)
    {
        SignInHarness harness = SignInHarness.Ready();

        Result<LoginResponse> outcome = await harness.RefreshAsync(presented);

        outcome.IsFailure.Should().BeTrue();
        outcome.Reason!.Code.Should().Be(InvalidRefreshTokenCode);

        harness.Tokens.Verify(
            tokens => tokens.RefreshAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
    }

    /// <summary>
    /// An account locked or unapproved since its token was issued is refused, and every session it holds is
    /// revoked rather than merely this exchange being denied.
    /// </summary>
    /// <param name="lockedSince">Whether the account has been locked since its token was issued.</param>
    /// <param name="approvalWithdrawn">Whether the account's approval has been withdrawn since.</param>
    /// <returns>A task representing the assertions.</returns>
    /// <remarks>
    /// The membership gates live in the external store rather than on the account row, so re-reading the
    /// account alone would not observe them. The refusal is made durable because an account that may not
    /// sign in must not keep the means to keep trying.
    /// </remarks>
    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task RefreshAsync_AnAccountBarredSinceIssue_IsRefusedAndItsSessionsRevoked(
        bool lockedSince,
        bool approvalWithdrawn)
    {
        SignInHarness harness = SignInHarness.Ready();
        harness.IsLockedOut = lockedSince;
        harness.IsApproved = !approvalWithdrawn;

        Result<LoginResponse> outcome = await harness.RefreshAsync();

        outcome.IsFailure.Should().BeTrue();
        outcome.Reason!.Code.Should().Be(InvalidRefreshTokenCode);

        harness.Tokens.Verify(
            tokens => tokens.RevokeAllRefreshTokensAsync(UserId, It.IsAny<CancellationToken>()),
            Times.Once);

        harness.AuditRecords.Should().ContainSingle()
            .Which.EventName.Should().Be(AuditEventNames.SessionRefused);
    }

    /// <summary>
    /// A logout revokes the refresh token and does nothing else: there is no server-side retraction of an
    /// access token to assert.
    /// </summary>
    /// <returns>A task representing the assertions.</returns>
    /// <remarks>
    /// MIGRATION: the legacy logout mechanism has no counterpart, and this is the most consequential
    /// absence on the contract. It destroyed the ticket cookie and four further cookies by name,
    /// back-dating two of them so the browser dropped them at once, and that ended the session instantly
    /// because the session lived in the cookie.
    /// </remarks>
    [Fact]
    public async Task LogoutAsync_RevokesTheRefreshTokenAndNothingElse()
    {
        SignInHarness harness = SignInHarness.Ready();
        harness.CallerIsSignedIn = true;

        Result outcome = await harness.Service.LogoutAsync(
            new RefreshTokenRequest { RefreshToken = SignInHarness.PresentedRefreshToken },
            CancellationToken.None);

        outcome.IsSuccess.Should().BeTrue();

        harness.Tokens.Verify(
            tokens => tokens.RevokeRefreshTokenAsync(
                SignInHarness.PresentedRefreshToken,
                It.IsAny<CancellationToken>()),
            Times.Once,
            "revoking the one presented value is the only server-side effect a logout may have");
        harness.Tokens.Verify(
            tokens => tokens.RevokeAllRefreshTokensAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "ending one session does not end the caller's other sessions");

        harness.AuditRecords.Should().ContainSingle()
            .Which.EventName.Should().Be(AuditEventNames.SessionEnded);
    }

    /// <summary>
    /// A logout with nothing to revoke succeeds, because the caller has already achieved the only outcome
    /// it asked for.
    /// </summary>
    /// <returns>A task representing the assertions.</returns>
    [Fact]
    public async Task LogoutAsync_WithNoTokenToRevoke_SucceedsWithoutTouchingTheStore()
    {
        SignInHarness harness = SignInHarness.Ready();

        Result outcome = await harness.Service.LogoutAsync(
            new RefreshTokenRequest { RefreshToken = string.Empty },
            CancellationToken.None);

        outcome.IsSuccess.Should().BeTrue();

        harness.Tokens.Verify(
            tokens => tokens.RevokeRefreshTokenAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    /// <summary>
    /// A stored representation the hashing abstraction considers superseded is replaced once, on the first
    /// accepted sign-in, and the sign-in still succeeds.
    /// </summary>
    /// <returns>A task representing the assertions.</returns>
    /// <remarks>
    /// The credential store changes from reversible to one-way, and that is a security fix rather than a
    /// refactor. The original schema held the value in clear text, and the membership provider was later
    /// configured to store it reversibly with retrieval enabled, decryptable with a key committed to source
    /// control.
    /// </remarks>
    [Fact]
    public async Task LoginAsync_ASupersededStoredRepresentation_IsReplacedOnceOnTheFirstAcceptedSignIn()
    {
        SignInHarness harness = SignInHarness.Ready();
        harness.StoredRepresentationIsSuperseded = true;

        Result<LoginResponse> outcome = await harness.LoginAsync();

        outcome.IsSuccess.Should().BeTrue("replacing the representation is a migration step, not a gate");

        harness.PasswordHasher.Verify(
            hasher => hasher.Hash(It.IsAny<string>()),
            Times.Once,
            "the replacement is produced exactly once");
        harness.Users.Verify(
            users => users.SetPasswordHashAsync(
                UserId,
                It.IsAny<string>(),
                It.IsAny<string?>(),
                Now,
                It.IsAny<CancellationToken>()),
            Times.Once,
            "and written exactly once, stamped with the instant the injected clock reported");
    }

    /// <summary>
    /// A successfully verified legacy representation is replaced with BCrypt during that same accepted
    /// sign-in.
    /// </summary>
    /// <returns>A task representing the assertions.</returns>
    [Fact]
    public async Task LoginAsync_AcceptedLegacyCredential_IsImmediatelyReplacedWithBcrypt()
    {
        const string SyntheticSalt = "synthetic-salt-placeholder";

        SignInHarness harness = SignInHarness.Ready();
        harness.StoredFormat = PasswordFormat.Encrypted;
        harness.StoredSalt = SyntheticSalt;
        harness.CredentialMatches = false;
        harness.LegacyCredentials
            .Setup(verifier => verifier.Verify(
                SubmittedCredential,
                StoredRepresentation,
                PasswordFormat.Encrypted,
                SyntheticSalt))
            .Returns(LegacyCredentialVerification.Legacy(isMatch: true));

        Result<LoginResponse> outcome = await harness.LoginAsync();

        outcome.IsSuccess.Should().BeTrue(
            "the bounded legacy verifier accepted the credential and the replacement is not a second gate");
        harness.PasswordHasher.Verify(
            hasher => hasher.Verify(SubmittedCredential, DecoyRepresentation),
            Times.Once,
            "a current-cost comparison still runs so a legacy row does not create a timing shortcut");
        harness.PasswordHasher.Verify(
            hasher => hasher.Hash(SubmittedCredential),
            Times.Once);
        harness.Users.Verify(
            users => users.SetPasswordHashAsync(
                UserId,
                SignInHarness.ReplacementRepresentation,
                It.IsAny<string?>(),
                Now,
                It.IsAny<CancellationToken>()),
            Times.Once,
            "the accepted legacy representation is replaced before the token pair is issued");
        harness.PasswordHasher.Verify(
            hasher => hasher.NeedsRehash(It.IsAny<string>()),
            Times.Never,
            "legacy acceptance itself requires the replacement; the BCrypt cost predicate is irrelevant");
    }

    /// <summary>
    /// A stored representation the hashing abstraction is content with is left alone and nothing is
    /// written.
    /// </summary>
    /// <returns>A task representing the assertions.</returns>
    [Fact]
    public async Task LoginAsync_ACurrentStoredRepresentation_IsNeitherReplacedNorWritten()
    {
        SignInHarness harness = SignInHarness.Ready();
        harness.StoredRepresentationIsSuperseded = false;

        Result<LoginResponse> outcome = await harness.LoginAsync();

        outcome.IsSuccess.Should().BeTrue();

        harness.PasswordHasher.Verify(hasher => hasher.Hash(It.IsAny<string>()), Times.Never);
        harness.Users.Verify(
            users => users.SetPasswordHashAsync(
                It.IsAny<int>(),
                It.IsAny<string>(),
                It.IsAny<string?>(),
                It.IsAny<DateTime>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
    }

    /// <summary>
    /// A refused credential neither replaces nor writes anything, however superseded the stored
    /// representation is.
    /// </summary>
    /// <returns>A task representing the assertions.</returns>
    [Fact]
    public async Task LoginAsync_ARefusedCredential_NeitherReplacesNorWrites()
    {
        SignInHarness harness = SignInHarness.Ready();
        harness.StoredRepresentationIsSuperseded = true;
        harness.CredentialMatches = false;

        Result<LoginResponse> outcome = await harness.LoginAsync();

        outcome.IsFailure.Should().BeTrue();
        outcome.Reason!.Code.Should().Be(InvalidCredentialsCode);

        harness.PasswordHasher.Verify(hasher => hasher.Hash(It.IsAny<string>()), Times.Never);
        harness.Users.Verify(
            users => users.SetPasswordHashAsync(
                It.IsAny<int>(),
                It.IsAny<string>(),
                It.IsAny<string?>(),
                It.IsAny<DateTime>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
        harness.UnitOfWork.Verify(
            work => work.SaveChangesAsync(It.IsAny<CancellationToken>()),
            Times.Never);
    }

    /// <summary>
    /// A replacement the store declines does not fail the sign-in, but it is reported rather than absorbed.
    /// </summary>
    /// <returns>A task representing the assertions.</returns>
    /// <remarks>
    /// The credential was correct, so refusing the caller would deny a legitimate sign-in over a
    /// housekeeping step. Saying nothing is the other mistake: an installation whose stored representations
    /// are stuck below the cost it believes it enforces has no other way of finding out, and a store of
    /// credentials must not report on itself by logging.
    /// </remarks>
    [Fact]
    public async Task LoginAsync_AReplacementTheStoreDeclines_StillSignsInAndReportsTheAnomaly()
    {
        SignInHarness harness = SignInHarness.Ready();
        harness.StoredRepresentationIsSuperseded = true;
        harness.ReplacementAccepted = CredentialWriteOutcome.NoRecord;

        Result<LoginResponse> outcome = await harness.LoginAsync();

        outcome.IsSuccess.Should().BeTrue("a correct credential is not refused over a housekeeping step");

        harness.Diagnostics.Verify(
            diagnostics => diagnostics.Record(
                SecurityDiagnosticEvent.CredentialWorkFactorUpgradeFailed,
                PortalId,
                UserId,
                It.IsAny<string>()),
            Times.Once);
    }

    /// <summary>An ordinary accepted sign-in commits nothing through the unit of work.</summary>
    /// <returns>A task representing the assertions.</returns>
    /// <remarks>
    /// Worth stating explicitly, because the natural expectation is the opposite. A sign-in updates
    /// bookkeeping - the last accepted instant, the cleared attempt counters, and on the first accepted
    /// sign-in the replaced stored representation - so it looks like a write path.
    /// </remarks>
    [Fact]
    public async Task LoginAsync_AnOrdinarySignIn_CommitsNothing()
    {
        SignInHarness harness = SignInHarness.Ready();
        harness.StoredRepresentationIsSuperseded = true;

        Result<LoginResponse> outcome = await harness.LoginAsync();

        outcome.IsSuccess.Should().BeTrue();

        harness.Users.Verify(
            users => users.RecordSuccessfulLoginAsync(UserId, Now, It.IsAny<CancellationToken>()),
            Times.Once,
            "the bookkeeping does happen -- through the membership store's own statement");
        harness.UnitOfWork.Verify(
            work => work.SaveChangesAsync(It.IsAny<CancellationToken>()),
            Times.Never,
            "but no tracked row changed, so nothing is committed");
    }

    // ---------------------------------------------------------------------------------------------
    // Region G -- the policy is honoured as given, denials stay uniform, and no credential can leave.
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// The lockout threshold and attempt window are taken from the bound policy and passed to the store
    /// unaltered.
    /// </summary>
    /// <returns>A task representing the assertions.</returns>
    /// <remarks>
    /// The shipped policy is preserved verbatim rather than tightened: a minimum length of seven, no
    /// required non-alphanumeric characters, no question-and-answer pair, and email uniqueness not
    /// enforced. Tightening any of them during a migration would bar existing members from accounts they
    /// hold today, so hardening is left as a separate, explicit decision.
    /// </remarks>
    [Fact]
    public async Task LoginAsync_HonoursTheBoundLockOutPolicyRatherThanAConstantOfItsOwn()
    {
        SignInHarness harness = SignInHarness.Ready();
        harness.Policy.MaxInvalidPasswordAttempts = 3;
        harness.Policy.PasswordAttemptWindowMinutes = 45;
        harness.CredentialMatches = false;

        await harness.LoginAsync();

        harness.Users.Verify(
            users => users.RecordFailedLoginAsync(
                UserId,
                3,
                TimeSpan.FromMinutes(45),
                Now,
                It.IsAny<CancellationToken>()),
            Times.Once,
            "the threshold and window are the bound ones, and the instant is the injected clock's");
    }

    /// <summary>
    /// The shipped policy values are carried across unchanged, which is what keeps existing members able to
    /// sign in.
    /// </summary>
    [Fact]
    public void PasswordPolicyOptions_DefaultsToTheShippedLegacyPolicy()
    {
        PasswordPolicyOptions policy = new();

        policy.MinRequiredPasswordLength.Should().Be(7);
        policy.MinRequiredNonAlphanumericCharacters.Should().Be(0);
        policy.RequiresQuestionAndAnswer.Should().BeFalse();
        policy.RequiresUniqueEmail.Should().BeFalse();
        policy.PasswordResetEnabled.Should().BeTrue(
            "reset is the supported recovery path now that retrieval is not carried forward");
    }

    /// <summary>Four different reasons for rejecting a credential produce one indistinguishable answer.</summary>
    /// <returns>A task representing the assertions.</returns>
    /// <remarks>
    /// An unknown tenant, an unknown account, an account holding no stored representation and a wrong
    /// credential are all answered identically. Any difference between them - a different code, a different
    /// sentence, even a different set of properties on the returned reason - turns this member into an
    /// oracle for account names or for the installation's tenants.
    /// </remarks>
    [Fact]
    public async Task LoginAsync_NoRejectedCredential_RevealsWhichGateClosed()
    {
        List<(string Code, string Message)> answers = [];

        foreach (RefusalArrangement arrangement in new[]
        {
            RefusalArrangement.UnknownTenant,
            RefusalArrangement.UnknownAccount,
            RefusalArrangement.NoStoredRepresentation,
            RefusalArrangement.WrongCredential,
        })
        {
            SignInHarness harness = SignInHarness.Refusing(arrangement);

            Result<LoginResponse> outcome = await harness.LoginAsync();

            outcome.IsFailure.Should().BeTrue();
            answers.Add((outcome.Reason!.Code, outcome.Reason.Message));

            harness.PasswordHasher.Verify(
                hasher => hasher.Verify(It.IsAny<string>(), It.IsAny<string>()),
                Times.Once,
                "the comparison is performed whatever was found, so the timing draws no distinction either");
        }

        answers.Should().OnlyContain(answer => answer.Code == InvalidCredentialsCode);
        answers.Select(answer => answer.Message).Distinct(StringComparer.Ordinal).Should().ContainSingle()
            .Which.Should().Be(UniformDenial);
    }

    /// <summary>
    /// The authenticated caller describes itself through the caller snapshot, and an unauthenticated one
    /// receives no description rather than an error.
    /// </summary>
    /// <returns>A task representing the assertions.</returns>
    /// <remarks>
    /// The asserted identity is read from the injected caller-identity abstraction rather than from
    /// anything the request carried, and the snapshot crossing this boundary is a data-transfer type: no
    /// account entity leaves the service.
    /// </remarks>
    [Fact]
    public async Task GetCurrentUserAsync_DescribesASignedInCallerAndNoOtherKind()
    {
        SignInHarness anonymous = SignInHarness.Ready();

        Result<CurrentUserDto?> anonymousOutcome =
            await anonymous.Service.GetCurrentUserAsync(CancellationToken.None);

        anonymousOutcome.IsSuccess.Should().BeTrue("no caller is a valid answer rather than a fault");
        anonymousOutcome.Value.Should().BeNull();

        SignInHarness signedIn = SignInHarness.Ready();
        signedIn.CallerIsSignedIn = true;
        signedIn.RoleNames = ["Administrators"];

        Result<CurrentUserDto?> outcome = await signedIn.Service.GetCurrentUserAsync(CancellationToken.None);

        outcome.IsSuccess.Should().BeTrue();
        outcome.Value.Should().NotBeNull();
        outcome.Value!.UserId.Should().Be(UserId);
        outcome.Value.PortalId.Should().Be(PortalId);
        outcome.Value.Username.Should().Be(AccountName);
        outcome.Value.Roles.Should().Equal(signedIn.RoleNames);
    }

    /// <summary>
    /// The caller snapshot answers portal administration from the tenant's own designation, and never from
    /// a role name.
    /// </summary>
    /// <returns>A task representing the assertions.</returns>
    /// <remarks>
    /// The name is operator-editable, the designated role need not be named anything in particular, and a
    /// role of the same name may belong to another tenant entirely; so the name is evidence of nothing and
    /// the column is evidence of everything. The published names are asserted to be UNCHANGED in each case,
    /// which is what shows the new fact is derived independently of them rather than summarising them.
    /// </remarks>
    [Fact]
    public async Task GetCurrentUserAsync_DecidesPortalAdministrationFromTheDesignationAndNotFromARoleName()
    {
        // The designated role is the one the caller holds, and its assignment window is open.
        SignInHarness designated = SignInHarness.Ready();
        designated.CallerIsSignedIn = true;
        designated.Portal.AdministratorRoleId = AdministratorRoleId;
        designated.RoleNames = ["Administrators"];
        designated.RoleAssignments = [ActiveAssignment(AdministratorRoleId)];

        Result<CurrentUserDto?> admitted =
            await designated.Service.GetCurrentUserAsync(CancellationToken.None);

        admitted.IsSuccess.Should().BeTrue();
        admitted.Value!.IsPortalAdministrator.Should().BeTrue();
        admitted.Value.Roles.Should().Equal(designated.RoleNames);

        // The SAME published role name, and the same held role - but the tenant designates a DIFFERENT role.
        // A name match would admit this caller; the designation refuses them.
        SignInHarness misnamed = SignInHarness.Ready();
        misnamed.CallerIsSignedIn = true;
        misnamed.Portal.AdministratorRoleId = AdministratorRoleId + 1;
        misnamed.RoleNames = ["Administrators"];
        misnamed.RoleAssignments = [ActiveAssignment(AdministratorRoleId)];

        Result<CurrentUserDto?> refused = await misnamed.Service.GetCurrentUserAsync(CancellationToken.None);

        refused.IsSuccess.Should().BeTrue();
        refused.Value!.IsPortalAdministrator.Should().BeFalse(
            "the designation names a role this caller does not hold, whatever the role they do hold is called");
        refused.Value.Roles.Should().Equal(
            misnamed.RoleNames,
            "the published names are unchanged, so the new fact is derived rather than summarised");

        // An unset designation is a configuration gap. A gap must not grant, and it is never widened to any
        // other role - not even to one named for the purpose.
        SignInHarness undesignated = SignInHarness.Ready();
        undesignated.CallerIsSignedIn = true;
        undesignated.Portal.AdministratorRoleId = null;
        undesignated.RoleNames = ["Administrators"];
        undesignated.RoleAssignments = [ActiveAssignment(AdministratorRoleId)];

        Result<CurrentUserDto?> ungranted =
            await undesignated.Service.GetCurrentUserAsync(CancellationToken.None);

        ungranted.Value!.IsPortalAdministrator.Should().BeFalse();

        // Holding the designated role is not enough on its own: an assignment whose window has already closed
        // counts for nothing, which is what stops a lapsed administrator from being reported as a current one.
        SignInHarness lapsed = SignInHarness.Ready();
        lapsed.CallerIsSignedIn = true;
        lapsed.Portal.AdministratorRoleId = AdministratorRoleId;
        lapsed.RoleAssignments = [ExpiredAssignment(AdministratorRoleId)];

        Result<CurrentUserDto?> stale = await lapsed.Service.GetCurrentUserAsync(CancellationToken.None);

        stale.Value!.IsPortalAdministrator.Should().BeFalse();

        // A host account administers every tenant, which is the answer the enforcing policy gives too. It
        // holds no assignment at all here, so nothing but the host flag can be producing the answer.
        SignInHarness host = SignInHarness.Ready();
        host.CallerIsSignedIn = true;
        host.Account.IsSuperUser = true;
        host.Portal.AdministratorRoleId = null;
        host.RoleAssignments = [];

        Result<CurrentUserDto?> hostOutcome = await host.Service.GetCurrentUserAsync(CancellationToken.None);

        hostOutcome.Value!.IsPortalAdministrator.Should().BeTrue();
    }

    /// <summary>
    /// The caller snapshot publishes both blocking remediation obligations, including one imposed AFTER the
    /// session began.
    /// </summary>
    /// <returns>A task representing the assertions.</returns>
    /// <remarks>
    /// ⚠ THIS IS THE CASE WHOSE ABSENCE WAS MEASURED AS A DEFECT. A tenant administrator marking a profile
    /// property required while an account is signed in imposes an obligation the sign-in response could not
    /// possibly have carried, and this endpoint is DELIBERATELY open during remediation - it is therefore
    /// the only response the confined caller can still read, and the only place the client can learn what
    /// it now owes. With the members absent, the client rendered the ordinary console while every ordinary
    /// endpoint refused it, and the caller was never sent to the screen that clears the obligation.
    /// </remarks>
    [Fact]
    public async Task GetCurrentUserAsync_PublishesBothBlockingRemediationObligations()
    {
        // Neither obligation stands: the members are DATA rather than omissions, so false must travel.
        SignInHarness unencumbered = SignInHarness.Ready();
        unencumbered.CallerIsSignedIn = true;

        Result<CurrentUserDto?> clear =
            await unencumbered.Service.GetCurrentUserAsync(CancellationToken.None);

        clear.IsSuccess.Should().BeTrue();
        clear.Value!.MustChangePassword.Should().BeFalse();
        clear.Value.MustUpdateProfile.Should().BeFalse();

        // The profile obligation alone, as an administrator marking a property required produces it.
        SignInHarness profileOwed = SignInHarness.Ready();
        profileOwed.CallerIsSignedIn = true;
        profileOwed.ProfileCompletionOutcome = Result<bool>.Success(true);

        Result<CurrentUserDto?> owed = await profileOwed.Service.GetCurrentUserAsync(CancellationToken.None);

        owed.IsSuccess.Should().BeTrue();
        owed.Value!.MustUpdateProfile.Should().BeTrue();
        owed.Value.MustChangePassword.Should().BeFalse();

        // The credential obligation alone, from the account row's own forced-change flag.
        SignInHarness credentialOwed = SignInHarness.Ready();
        credentialOwed.CallerIsSignedIn = true;
        credentialOwed.Account.UpdatePassword = true;

        Result<CurrentUserDto?> forced =
            await credentialOwed.Service.GetCurrentUserAsync(CancellationToken.None);

        forced.IsSuccess.Should().BeTrue();
        forced.Value!.MustChangePassword.Should().BeTrue();
        forced.Value.MustUpdateProfile.Should().BeFalse();

        // BOTH AT ONCE, which the legacy single-valued status enumeration could not report and this
        // contract must: a caller who has just changed their credential still owes the profile.
        SignInHarness both = SignInHarness.Ready();
        both.CallerIsSignedIn = true;
        both.Account.UpdatePassword = true;
        both.ProfileCompletionOutcome = Result<bool>.Success(true);

        Result<CurrentUserDto?> encumbered = await both.Service.GetCurrentUserAsync(CancellationToken.None);

        encumbered.IsSuccess.Should().BeTrue();
        encumbered.Value!.MustChangePassword.Should().BeTrue();
        encumbered.Value.MustUpdateProfile.Should().BeTrue();
    }

    /// <summary>
    /// A remediation state that cannot be read refuses the description rather than reporting that no
    /// obligation stands.
    /// </summary>
    /// <returns>A task representing the assertions.</returns>
    /// <remarks>
    /// FAILS CLOSED, exactly as the rotation and the restricted-session pipeline stage do. Answering with a
    /// snapshot reporting no obligation would put the client in the one state it cannot recover from -
    /// rendering the ordinary console while every ordinary endpoint refuses it - and the refusal code is
    /// the store-unavailable one, so the edge answers 503 rather than describing the caller wrongly.
    /// </remarks>
    [Fact]
    public async Task GetCurrentUserAsync_WhenTheRemediationStateCannotBeRead_RefusesRatherThanReportingNone()
    {
        SignInHarness harness = SignInHarness.Ready();
        harness.CallerIsSignedIn = true;
        harness.ProfileCompletionOutcome = Result<bool>.Failure(
            "user.profile.store_unavailable",
            "The profile store could not be read.");

        Result<CurrentUserDto?> outcome = await harness.Service.GetCurrentUserAsync(CancellationToken.None);

        outcome.IsFailure.Should().BeTrue();
        outcome.Reason!.Code.Should().Be(RemediationStoreUnavailableCode);
    }

    /// <summary>
    /// The sign-in response's top-level obligations and the identity it embeds carry the SAME values, so no
    /// single response can contradict itself.
    /// </summary>
    /// <returns>A task representing the assertions.</returns>
    /// <remarks>
    /// The client reads the obligations from one place or the other depending on which endpoint answered;
    /// two members computed independently would eventually disagree, and the disagreement would present as
    /// an intermittent routing fault rather than as a contract defect.
    /// </remarks>
    [Fact]
    public async Task SignIn_EmbedsTheSameObligationsItPublishesAtTheTopLevel()
    {
        SignInHarness harness = SignInHarness.Ready();
        harness.Account.UpdatePassword = true;
        harness.ProfileCompletionOutcome = Result<bool>.Success(true);

        Result<LoginResponse> outcome = await harness.LoginAsync();

        outcome.IsSuccess.Should().BeTrue();
        outcome.Value.MustChangePassword.Should().BeTrue();
        outcome.Value.MustUpdateProfile.Should().BeTrue();
        outcome.Value.User.MustChangePassword.Should().Be(outcome.Value.MustChangePassword);
        outcome.Value.User.MustUpdateProfile.Should().Be(outcome.Value.MustUpdateProfile);
    }

    /// <summary>
    /// A caller whose account has since been removed is told so, rather than being described as an account
    /// that no longer exists.
    /// </summary>
    /// <returns>A task representing the assertions.</returns>
    [Fact]
    public async Task GetCurrentUserAsync_WhenTheAccountHasSinceBeenRemoved_ReportsItsAbsence()
    {
        SignInHarness harness = SignInHarness.Ready();
        harness.CallerIsSignedIn = true;
        harness.ScopedAccountExists = false;

        Result<CurrentUserDto?> outcome = await harness.Service.GetCurrentUserAsync(CancellationToken.None);

        outcome.IsFailure.Should().BeTrue();
        outcome.Reason!.Code.Should().Be(UserNotFoundCode);
    }

    /// <summary>
    /// The public surface has no member through which a credential could leave, and no member reports its
    /// verdict by mutating an argument.
    /// </summary>
    /// <remarks>
    /// The credential half is the security property. No member may return a credential, a stored
    /// representation, a verification answer or a signing key, and there is no recovery member for one to
    /// hide behind - which is what makes the one-way store's guarantee real rather than aspirational.
    /// </remarks>
    [Fact]
    public void AuthService_ExposesNoMemberThatCouldReturnACredential()
    {
        MethodInfo[] surface = typeof(IAuthService)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Concat(typeof(AuthService).GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .Where(method => method.DeclaringType == typeof(AuthService)))
            .ToArray();

        surface.Should().NotBeEmpty("the surface must actually have been discovered for this to mean anything");

        foreach (MethodInfo method in surface)
        {
            method.GetParameters().Should().OnlyContain(
                parameter => !parameter.ParameterType.IsByRef,
                "no member reports its verdict by mutating an argument, which is the legacy shape being retired");

            method.Name.ToUpperInvariant().Should().NotContain(
                "PASSWORD",
                "there is no credential-shaped member: recovery is not carried forward");
            method.Name.ToUpperInvariant().Should().NotContain("CREDENTIAL");
            method.Name.ToUpperInvariant().Should().NotContain("SECRET");
        }

        string[] forbiddenOnTheWire = ["PASSWORD", "PASSWORDHASH", "STOREDHASH", "SECRET", "SIGNINGKEY"];

        typeof(LoginResponse).GetProperties()
            .Select(property => property.Name.ToUpperInvariant())
            .Should().OnlyContain(
                name => !forbiddenOnTheWire.Contains(name, StringComparer.Ordinal),
                "and nothing credential-shaped travels on the response either");
    }

    /// <summary>The refusing conditions a sign-in can end in, named so that a theory can walk all of them.</summary>
    /// <remarks>
    /// Public because a theory's parameter type may not be less accessible than the theory itself. The
    /// vocabulary is closed deliberately: a new refusing branch in the service should appear here and
    /// inherit the properties every refusal is required to have.
    /// </remarks>
    public enum RefusalArrangement
    {
        /// <summary>The account was found and the credential did not match.</summary>
        WrongCredential = 0,

        /// <summary>No account matches the submitted name in this tenant.</summary>
        UnknownAccount = 1,

        /// <summary>The tenant the credential was addressed to does not exist.</summary>
        UnknownTenant = 2,

        /// <summary>The account exists and holds no stored representation to compare against.</summary>
        NoStoredRepresentation = 3,

        /// <summary>The account is locked and its automatic-unlock window has not elapsed.</summary>
        LockedAccount = 4,

        /// <summary>The account has not yet been admitted to the tenant.</summary>
        AwaitingApproval = 5,
    }
    /// <summary>An assignment of one role whose validity window is open at the instant the tests judge.</summary>
    /// <param name="roleId">The role held.</param>
    /// <returns>The assignment.</returns>
    private static UserRole ActiveAssignment(int roleId) => new()
    {
        UserId = UserId,
        RoleId = roleId,
        EffectiveDate = Now.AddDays(-1),
        ExpiryDate = Now.AddDays(1),
    };

    /// <summary>An assignment of one role whose validity window has already closed.</summary>
    /// <param name="roleId">The role formerly held.</param>
    /// <returns>The assignment.</returns>
    private static UserRole ExpiredAssignment(int roleId) => new()
    {
        UserId = UserId,
        RoleId = roleId,
        EffectiveDate = Now.AddDays(-30),
        ExpiryDate = Now.AddDays(-1),
    };

    /// <summary>
    /// Assembles the service over substituted abstractions, with every default set to the state a shipped
    /// installation is actually in.
    /// </summary>
    /// <remarks>
    /// Every substituted collaborator is an abstraction declared by the domain or application layer. Two of
    /// them - the token contract and the caller-identity contract - have no implementation in the
    /// application layer at all, which is exactly why they are substituted as interfaces here and why no
    /// concrete token type is named anywhere in this file.
    /// </remarks>
    private sealed class SignInHarness
    {
        /// <summary>The access token the token contract mints on a sign-in.</summary>
        public const string MintedAccessToken = "minted-access-token";

        /// <summary>The refresh token the token contract mints on a sign-in.</summary>
        public const string MintedRefreshToken = "minted-refresh-token";

        /// <summary>The access token a rotation produces.</summary>
        public const string RotatedAccessToken = "rotated-access-token";

        /// <summary>The successor refresh token a rotation produces.</summary>
        public const string RotatedRefreshToken = "rotated-refresh-token";

        /// <summary>The refresh token a caller presents for exchange.</summary>
        public const string PresentedRefreshToken = "presented-refresh-token";

        /// <summary>The server-observed client binding carried through refresh orchestration.</summary>
        public const string ClientBinding = "client-binding-placeholder";

        /// <summary>The account identifier the tenant records as its administrator.</summary>
        public const int AdministratorId = 2;

        /// <summary>The replacement a superseded stored representation is upgraded to.</summary>
        public const string ReplacementRepresentation = "replacement-representation-placeholder";

        private SignInHarness()
        {
            Portal = new Portal
            {
                PortalId = PortalId,
                PortalName = "Measured Portal",
                AdministratorId = AdministratorId,
                UserRegistration = UserRegistrationMode.PublicRegistration,
            };

            Account = new User
            {
                UserId = UserId,
                Username = AccountName,
                FirstName = "Ada",
                LastName = "Lovelace",
                DisplayName = "Ada Lovelace",
                Email = "ada@example.invalid",
                IsApproved = true,
            };

            Policy = new PasswordPolicyOptions();
            TokenSettings = new JwtOptions();
            AuditRecords = [];
            RoleNames = [];
            RoleAssignments = [];
            PermissionKeys = [];

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
            Diagnostics = new Mock<ISecurityDiagnostics>(MockBehavior.Loose);

            Accounts
                .Setup(accounts => accounts.IsEmailValidAsync(
                    It.IsAny<int>(),
                    It.IsAny<string>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(Result<bool>.Success(true));

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

        /// <summary>The tenant the credential is presented to.</summary>
        public Portal Portal { get; }

        /// <summary>The account the submitted name resolves to.</summary>
        public User Account { get; }

        /// <summary>The bound credential policy the service is handed.</summary>
        public PasswordPolicyOptions Policy { get; }

        /// <summary>
        /// The bound token settings. Read by this suite to derive the expiry the token contract is arranged
        /// to mint, which is how the expiry assertion stays tied to the configured lifetime rather than to
        /// a magic number.
        /// </summary>
        public JwtOptions TokenSettings { get; }

        /// <summary>Every audit record the service emitted, in the order it emitted them.</summary>
        public List<AuditEvent> AuditRecords { get; }

        /// <summary>The role names the account holds in this tenant.</summary>
        public IReadOnlyList<string> RoleNames { get; set; }

        /// <summary>The role assignments the role store answers with for the signed-in caller.</summary>
        /// <remarks>
        /// Held apart from <see cref="RoleNames"/> deliberately. The names are what the snapshot PUBLISHES;
        /// these carry the role KEYS and the validity windows the advisory administration fact is decided
        /// from, and the two are not interchangeable - the designation is a column naming a role
        /// identifier, so a test that arranged only a name could not exercise the decision at all.
        /// </remarks>
        public IReadOnlyList<UserRole> RoleAssignments { get; set; }

        /// <summary>The permission keys effective for the account in this tenant.</summary>
        public IReadOnlyList<string> PermissionKeys { get; set; }

        /// <summary>Whether the resolved account is an installation-wide account.</summary>
        public bool IsSuperUser
        {
            get => Account.IsSuperUser;
            set => Account.IsSuperUser = value;
        }

        /// <summary>
        /// Whether the tenant has admitted the account. Proxies the entity's own property so the loaded
        /// account and the membership store the credential state is read from cannot disagree, which is the
        /// state a real request observes.
        /// </summary>
        /// <remarks>
        /// The entity's property is NULLABLE and this one is not, which is a faithful narrowing rather than
        /// a convenience: approval is held in the external membership store, so the entity has to be able
        /// to say "no membership record was found", whereas the credential state the store reports is a
        /// plain answer. An absent record reads here as not admitted, which is the safe direction.
        /// </remarks>
        public bool IsApproved
        {
            get => Account.IsApproved ?? false;
            set => Account.IsApproved = value;
        }

        /// <summary>Whether the account is currently locked.</summary>
        public bool IsLockedOut { get; set; }

        /// <summary>Whether the submitted credential matches the stored representation.</summary>
        public bool CredentialMatches { get; set; } = true;

        /// <summary>Whether a stored representation exists to compare against.</summary>
        public bool StoredRepresentationOnFile { get; set; } = true;

        /// <summary>The persisted membership format discriminator for the stored representation.</summary>
        public PasswordFormat StoredFormat { get; set; } = PasswordFormat.Hashed;

        /// <summary>The optional legacy membership salt stored beside the representation.</summary>
        public string? StoredSalt { get; set; }

        /// <summary>Whether the hashing abstraction considers the stored representation superseded.</summary>
        public bool StoredRepresentationIsSuperseded { get; set; }

        /// <summary>Whether the store accepts a replacement representation.</summary>
        public CredentialWriteOutcome ReplacementAccepted { get; set; } = CredentialWriteOutcome.Replaced;

        /// <summary>Whether the store accepts an approval.</summary>
        public bool ApprovalAccepted { get; set; } = true;

        /// <summary>Whether the tenant exists.</summary>
        public bool TenantExists { get; set; } = true;

        /// <summary>Whether the submitted name resolves to an account.</summary>
        public bool ScopedAccountExists { get; set; } = true;

        /// <summary>Whether the token store can record a session.</summary>
        public bool TokenStoreAvailable { get; set; } = true;

        /// <summary>An outcome to force from a rotation, where a test needs one.</summary>
        public Result<LoginResponse>? RotationOutcome { get; set; }

        /// <summary>Whether the request already carries an authenticated caller.</summary>
        public bool CallerIsSignedIn { get; set; }

        /// <summary>
        /// What the account-administration vertical answers when asked whether the account leaves a
        /// required profile property empty. A FAILED outcome models a store that cannot be read, which the
        /// remediation decision treats as blocking rather than as "no obligation".
        /// </summary>
        public Result<bool> ProfileCompletionOutcome { get; set; } = Result<bool>.Success(false);

        /// <summary>Whether that caller is an installation-wide account.</summary>
        public bool SignedInCallerIsSuperUser { get; set; }

        /// <summary>The identifier of that caller.</summary>
        public int SignedInCallerUserId { get; set; } = UserId;

        /// <summary>The account store.</summary>
        public Mock<IUserRepository> Users { get; }

        /// <summary>The tenant store.</summary>
        public Mock<IPortalRepository> Portals { get; }

        /// <summary>
        /// The role store, asked only whether the caller holds the role the tenant designates as its
        /// administrator. Loose by default, so it answers with an empty assignment list and the advisory
        /// administration fact is reported false unless a test arranges otherwise.
        /// </summary>
        public Mock<IRoleRepository> Roles { get; }

        /// <summary>Effective-permission resolution.</summary>
        public Mock<IPermissionService> Permissions { get; }

        /// <summary>The account-administration vertical, asked only whether a profile is outstanding.</summary>
        public Mock<IUserService> Accounts { get; }

        /// <summary>Token minting, rotation and revocation.</summary>
        public Mock<ITokenService> Tokens { get; }

        /// <summary>Non-consuming refresh-token inspection.</summary>
        public Mock<IRefreshTokenStore> RefreshTokens { get; }

        /// <summary>One-way credential comparison and replacement detection.</summary>
        public Mock<IPasswordHasher> PasswordHasher { get; }

        /// <summary>Bounded verification of representations still held in a legacy format.</summary>
        public Mock<ILegacyCredentialVerifier> LegacyCredentials { get; }

        /// <summary>The only source of the current instant.</summary>
        public Mock<IClock> Clock { get; }

        /// <summary>Installation-wide settings.</summary>
        public Mock<IHostSettingsService> HostSettings { get; }

        /// <summary>The commit point for a tracked change.</summary>
        public Mock<IUnitOfWork> UnitOfWork { get; }

        /// <summary>The already-authenticated caller, where there is one.</summary>
        public Mock<ICurrentUser> CurrentUser { get; }

        /// <summary>The audit trail.</summary>
        public Mock<IAuditSink> Audit { get; }

        /// <summary>The closed security-anomaly vocabulary.</summary>
        public Mock<ISecurityDiagnostics> Diagnostics { get; }

        /// <summary>The service under test.</summary>
        public AuthService Service { get; }

        /// <summary>
        /// Builds a harness whose defaults describe a shipped installation: one tenant on public
        /// registration, one admitted account holding a current stored representation, an available token
        /// store, and no installation-wide settings configured.
        /// </summary>
        /// <returns>The arranged harness.</returns>
        public static SignInHarness Ready()
        {
            SignInHarness harness = new();

            harness.Clock.SetupGet(clock => clock.UtcNow).Returns(Now);

            harness.Portals
                .Setup(portals => portals.GetByIdAsync(
                    It.IsAny<int>(),
                    It.IsAny<bool>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => harness.TenantExists ? harness.Portal : null);

            harness.Users
                .Setup(users => users.GetByUsernameAsync(
                    It.IsAny<int?>(),
                    It.IsAny<string>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => harness.ScopedAccountExists ? harness.Account : null);

            harness.Users
                .Setup(users => users.GetAsync(
                    It.IsAny<int?>(),
                    It.IsAny<int>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => harness.ScopedAccountExists ? harness.Account : null);

            harness.Users
                .Setup(users => users.GetAsync(
                    It.Is<int?>(portalId => portalId == null),
                    It.Is<int>(userId => userId == harness.SignedInCallerUserId),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => harness.CallerIsSignedIn && harness.SignedInCallerIsSuperUser
                    ? new User
                    {
                        UserId = harness.SignedInCallerUserId,
                        Username = "authoritative_host",
                        IsSuperUser = true,
                    }
                    : null);

            harness.Users
                .Setup(users => users.GetCredentialStateAsync(
                    It.IsAny<int>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => (
                    harness.StoredRepresentationOnFile,
                    harness.StoredRepresentationOnFile ? StoredRepresentation : null,
                    harness.StoredFormat,
                    harness.StoredSalt,
                    harness.IsApproved,
                    harness.IsLockedOut));

            harness.Users
                .Setup(users => users.SetApprovalAsync(
                    It.IsAny<int>(),
                    It.IsAny<bool>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => harness.ApprovalAccepted);

            harness.Users
                .Setup(users => users.UnlockAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(false);

            harness.Users
                .Setup(users => users.RecordSuccessfulLoginAsync(
                    It.IsAny<int>(),
                    It.IsAny<DateTime>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(MembershipWriteOutcome.Recorded);

            harness.Users
                .Setup(users => users.RecordFailedLoginAsync(
                    It.IsAny<int>(),
                    It.IsAny<int>(),
                    It.IsAny<TimeSpan>(),
                    It.IsAny<DateTime>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(MembershipWriteOutcome.Recorded);

            harness.Users
                .Setup(users => users.SetPasswordHashAsync(
                    It.IsAny<int>(),
                    It.IsAny<string>(),
                    It.IsAny<string?>(),
                    It.IsAny<DateTime>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => harness.ReplacementAccepted);

            harness.Users
                .Setup(users => users.ListRoleNamesAsync(
                    It.IsAny<int>(),
                    It.IsAny<int>(),
                    It.IsAny<DateTime>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => harness.RoleNames);

            harness.Roles
                .Setup(roles => roles.GetUserRolesAsync(
                    It.IsAny<int>(),
                    It.IsAny<int>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => harness.RoleAssignments);

            harness.Permissions
                .Setup(permissions => permissions.GetEffectivePermissionKeysAsync(
                    It.IsAny<int>(),
                    It.IsAny<int?>(),
                    It.IsAny<int?>(),
                    It.IsAny<int?>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => Result<IReadOnlyList<string>>.Success(harness.PermissionKeys));

            harness.Accounts
                .Setup(accounts => accounts.RequiresProfileCompletionAsync(
                    It.IsAny<int>(),
                    It.IsAny<int>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => harness.ProfileCompletionOutcome);

            // Nothing is configured installation-wide, which is the shipped state: the credential-expiry
            // window then defaults to zero and disables itself, and the automatic-unlock window falls back
            // to its documented default.
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
                .Returns(DecoyRepresentation);

            harness.PasswordHasher
                .Setup(hasher => hasher.NeedsRehash(It.IsAny<string>()))
                .Returns(() => harness.StoredRepresentationIsSuperseded);

            harness.PasswordHasher
                .Setup(hasher => hasher.Hash(It.IsAny<string>()))
                .Returns(ReplacementRepresentation);

            harness.LegacyCredentials
                .Setup(verifier => verifier.Verify(
                    It.IsAny<string>(),
                    It.IsAny<string>(),
                    It.IsAny<PasswordFormat>(),
                    It.IsAny<string?>()))
                .Returns(LegacyCredentialVerification.Current);

            harness.UnitOfWork
                .Setup(work => work.SaveChangesAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(1);

            harness.CurrentUser.SetupGet(caller => caller.IsAuthenticated).Returns(() => harness.CallerIsSignedIn);
            harness.CurrentUser.SetupGet(caller => caller.IsSuperUser).Returns(() => harness.SignedInCallerIsSuperUser);
            harness.CurrentUser.SetupGet(caller => caller.UserName).Returns(AccountName);
            harness.CurrentUser
                .SetupGet(caller => caller.UserId)
                .Returns(() => harness.CallerIsSignedIn ? harness.SignedInCallerUserId : null);
            harness.CurrentUser
                .SetupGet(caller => caller.PortalId)
                .Returns(() => harness.CallerIsSignedIn ? PortalId : null);

            harness.Audit
                .Setup(sink => sink.Record(It.IsAny<AuditEvent>()))
                .Callback<AuditEvent>(harness.AuditRecords.Add);

            harness.Tokens
                .Setup(tokens => tokens.IssueTokensAsync(
                    It.IsAny<int>(),
                    It.IsAny<int>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => harness.TokenStoreAvailable
                    ? Result<LoginResponse>.Success(new LoginResponse
                    {
                        AccessToken = MintedAccessToken,
                        RefreshToken = MintedRefreshToken,
                        ExpiresAtUtc = Now.AddMinutes(harness.TokenSettings.ExpirationMinutes),
                    })
                    : Result<LoginResponse>.Failure(
                        TokenStoreUnavailableCode,
                        "The session could not be recorded."));

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
                    AccessToken = RotatedAccessToken,
                    RefreshToken = RotatedRefreshToken,
                    ExpiresAtUtc = Now.AddMinutes(harness.TokenSettings.ExpirationMinutes),
                    User = new CurrentUserDto
                    {
                        UserId = UserId,
                        PortalId = PortalId,
                        Username = AccountName,
                    },
                }));

            harness.Tokens
                .Setup(tokens => tokens.RevokeRefreshTokenAsync(
                    It.IsAny<string>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(Result.Success());

            harness.Tokens
                .Setup(tokens => tokens.RevokeAllRefreshTokensAsync(
                    It.IsAny<int>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(Result.Success());

            return harness;
        }

        /// <summary>Builds a harness arranged to end in one named refusing condition.</summary>
        /// <param name="arrangement">The condition to arrange.</param>
        /// <returns>The arranged harness.</returns>
        /// <exception cref="ArgumentOutOfRangeException">The condition is not a member of the vocabulary.</exception>
        public static SignInHarness Refusing(RefusalArrangement arrangement)
        {
            SignInHarness harness = Ready();

            switch (arrangement)
            {
                case RefusalArrangement.WrongCredential:
                    harness.CredentialMatches = false;
                    break;

                case RefusalArrangement.UnknownAccount:
                    harness.ScopedAccountExists = false;
                    break;

                case RefusalArrangement.UnknownTenant:
                    harness.TenantExists = false;
                    break;

                case RefusalArrangement.NoStoredRepresentation:
                    harness.StoredRepresentationOnFile = false;
                    break;

                case RefusalArrangement.LockedAccount:
                    harness.IsLockedOut = true;
                    break;

                case RefusalArrangement.AwaitingApproval:
                    harness.IsApproved = false;
                    harness.Portal.UserRegistration = UserRegistrationMode.VerifiedRegistration;
                    break;

                default:
                    throw new ArgumentOutOfRangeException(
                        nameof(arrangement),
                        arrangement,
                        "The refusing condition is not a member of the vocabulary.");
            }

            return harness;
        }

        /// <summary>
        /// Submits a credential, defaulting every argument to the ordinary case so that a test states only
        /// what it varies.
        /// </summary>
        /// <param name="username">The account name submitted.</param>
        /// <param name="password">The credential submitted.</param>
        /// <param name="verificationCode">The account-verification code submitted, where one is.</param>
        /// <returns>The service's outcome.</returns>
        public Task<Result<LoginResponse>> LoginAsync(
            string username = AccountName,
            string password = SubmittedCredential,
            string? verificationCode = null)
            => Service.LoginAsync(
                new LoginRequest
                {
                    PortalId = PortalId,
                    Username = username,
                    Password = password,
                    VerificationCode = verificationCode,
                },
                CancellationToken.None);

        /// <summary>Presents a refresh token for exchange.</summary>
        /// <param name="refreshToken">The value presented.</param>
        /// <returns>The service's outcome.</returns>
        public Task<Result<LoginResponse>> RefreshAsync(string refreshToken = PresentedRefreshToken)
            => Service.RefreshAsync(
                new RefreshTokenRequest
                {
                    RefreshToken = refreshToken,
                    ClientBinding = ClientBinding,
                },
                CancellationToken.None);
    }
}
