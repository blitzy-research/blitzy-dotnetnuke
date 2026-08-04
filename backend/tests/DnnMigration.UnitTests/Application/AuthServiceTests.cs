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
/// Pins the sign-in verdict: which of the seven legacy sign-in outcomes admits a caller, which refuses
/// one, and which distinct reason each refusal carries.
/// </summary>
/// <remarks>
/// <para>
/// <b>What this suite owns.</b> The legacy sign-in screen reported its verdict by mutating a status
/// variable the caller had declared, and every consumer of that variable then decided for itself what the
/// value meant. That decision is now the service's, taken once, and this suite is where it is held still.
/// Concretely: the seven-member status ladder and its ordinals, the single predicate that turned a status
/// into "admitted", the four-way ladder a not-yet-approved account walks, the two weak-credential
/// outcomes that accompany a success rather than refusing it, and the stable audit name each outcome is
/// recorded under.
/// </para>
/// <para>
/// <b>What it deliberately leaves to others.</b> Hashing and token algorithms are verified against their
/// real implementations by the two security suites, so here the hashing and token abstractions are
/// substituted and only the ORCHESTRATION is asserted - that the comparison is performed, that a pair is
/// minted exactly once when a credential is accepted and never when one is refused, and that a rotation
/// retires the value presented to it. Account creation, deletion and listing belong to the account
/// suite; request-shape rules belong to the sign-in request validator's suite; the response envelope, the
/// transport status mapping and request throttling belong to the integration suite, because all three are
/// properties of the hosted pipeline rather than of this service.
/// </para>
/// <para>
/// <b>Every collaborator is an abstraction declared by the domain or application layer.</b> Nothing here
/// names a persistence type, a hashing library, a token library or a web-framework type; there is no
/// database context to reach and no host to start. Two of the substituted abstractions have no
/// implementation in the application layer at all - the token contract is satisfied in the infrastructure
/// layer and the caller-identity contract at the api edge - which is precisely why they are substituted
/// here as interfaces and why no test in this file mentions a concrete token type.
/// </para>
/// <para>
/// <b>Time never comes from the ambient clock.</b> Every instant this suite asserts is derived from the
/// injected clock abstraction returning <see cref="Now"/>, so an expiry, a lock window or a recorded
/// sign-in instant is reproducible on any machine at any moment.
/// </para>
/// <para>
/// <b>No credential value is ever asserted.</b> The submitted credential and the stored representation
/// appear only as opaque inputs; no test reads one back, compares one to an expected literal, or asserts
/// one as a recorded property. The two credential-shaped constants below are deliberately unusable
/// placeholders.
/// </para>
/// </remarks>
public class AuthServiceTests
{
    /// <summary>
    /// The tenant every test signs in against. Minus one is deliberate rather than arbitrary: the tenant
    /// table's key is declared as an identity seeded at minus one, so minus one is a REAL tenant and the
    /// legacy integer stand-in for "absent" was the same value. A suite that used a comfortable positive
    /// number would never notice a service that had confused the two.
    /// </summary>
    private const int PortalId = -1;

    /// <summary>The account identifier every resolved account carries.</summary>
    private const int UserId = 41;

    /// <summary>The account name submitted by every test that does not care about the name.</summary>
    private const string AccountName = "measured_member";

    /// <summary>
    /// The submitted credential. Never compared to anything: the substituted hashing abstraction decides
    /// the verdict, so this value only has to be a non-empty string that is not one of the two the
    /// product was distributed with.
    /// </summary>
    private const string SubmittedCredential = "not-a-real-credential-9F2C";

    /// <summary>
    /// The stored representation the account is found holding. An obvious placeholder rather than a real
    /// digest, so that nothing resembling a credential is committed to this repository.
    /// </summary>
    private const string StoredRepresentation = "stored-representation-placeholder";

    /// <summary>
    /// The stand-in the hashing abstraction publishes for an account that holds no stored representation,
    /// so that the comparison is performed at full cost even when there is nothing to compare against.
    /// </summary>
    private const string DecoyRepresentation = "decoy-representation-placeholder";

    /// <summary>
    /// The one sentence every refused credential receives, whatever actually closed the gate.
    /// </summary>
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

    /// <summary>The account name the product was distributed with for the tenant administrator.</summary>
    private const string ShippedAdministratorName = "admin";

    /// <summary>The account name the product was distributed with for the installation owner.</summary>
    private const string ShippedHostName = "host";

    /// <summary>
    /// The instant the injected clock reports for the whole of every test. Fixed, and in coordinated
    /// universal time, because the legacy read the server's local wall clock and a suite that did the
    /// same would pass or fail according to where it ran.
    /// </summary>
    private static readonly DateTime Now = new(2026, 8, 2, 12, 0, 0, DateTimeKind.Utc);

    /// <summary>
    /// The seven legacy status ordinals paired with the verdict the legacy sign-in screen reached for
    /// each, transcribed from the two statements that produced it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Read literally from <c>Login.ascx.vb</c>. L163 seeds the status at the failure member; L164 hands
    /// it to the account validation to be mutated; L168 tests it against the not-approved member alone;
    /// and L187 - sitting in the ELSE arm of that test - is the whole of the verdict:
    /// <c>authenticated = (loginStatus &lt;&gt; UserLoginStatus.LOGIN_FAILURE)</c>.
    /// </para>
    /// <para>
    /// Two consequences follow, and neither is obvious from reading L187 alone. The not-approved member
    /// never reaches that inequality, so it is refused even though it is not the failure member. And
    /// EVERY OTHER member does reach it, so three members the reader would expect to be refused are
    /// admitted: the lockout member and the two weak-credential members.
    /// </para>
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
    /// The lockout and weak-credential members are the three the legacy verdict admitted and the
    /// target does not admit unconditionally - this test states that narrowing and its justification.
    /// </summary>
    /// <returns>A task representing the assertions.</returns>
    /// <remarks>
    /// <para>
    /// The test name is long on purpose. It is the shortest sentence that says both what the target does
    /// and what the legacy did, so that a reader who breaks this test learns immediately that they have
    /// walked into a deliberate divergence rather than a defect.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task LoginAsync_LockedOutAndInsecurePasswordStatuses_AreTreatedAsFailures_NarrowingLegacyPredicate()
    {
        // MIGRATION: the legacy verdict was `authenticated = (loginStatus <> LOGIN_FAILURE)` at
        // Login.ascx.vb:L187, which admitted every member except the failure member and the not-approved
        // member -- the latter only because L168 diverted it before L187 could be reached. Three members
        // therefore reached L187 and were admitted that a reader would expect to be refused: the
        // lockout member (3) and the two weak-credential members (5 and 6).
        //
        // MIGRATION: the target narrows that predicate, and it narrows it in TWO DIFFERENT WAYS, which is
        // why one test states both. The lockout member becomes a genuine REFUSAL, because a lock that
        // does not lock is the failure of the only control standing between an attacker and unlimited
        // guessing; Minimal Change Clause item 1 preserves a discovered defect unless it blocks delivery,
        // and an authentication bypass does. The two weak-credential members stay ADMITTED -- refusing
        // them would deny an installation the two accounts every installation begins with -- but
        // they are narrowed from "silently authenticated" to "authenticated WITH an advisory the caller
        // is told about". Both divergences are recorded in MIGRATION_NOTES.md.
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
                It.IsAny<string>(),
                It.IsAny<bool>(),
                It.IsAny<IReadOnlyList<string>>(),
                It.IsAny<IReadOnlyList<string>>(),
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
    /// <para>
    /// The legacy had a single boolean verdict and a message key set alongside it, so a caller could not
    /// tell a wrong credential from a locked account from a pending registration except by reading the
    /// message. Collapsing all of them onto one code here would reproduce that, and the plan's
    /// requirement is the opposite: the status members that replaced the by-reference argument must map
    /// onto DISTINCT reasons.
    /// </para>
    /// <para>
    /// Note which distinctions are drawn and which are deliberately withheld. A wrong credential, an
    /// unknown account and an unknown tenant all answer identically, because telling them apart would
    /// turn this member into an oracle for account names. The distinctions asserted here are between
    /// conditions a caller has ALREADY proved a credential for, plus the lock, which is disclosed only to
    /// a caller entitled to it.
    /// </para>
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
    /// <para>
    /// This is the fact that explains why the legacy verdict admitted members 5 and 6, and it is a
    /// measured fact rather than an inference. At <c>UserController.vb</c> L1144-L1148 an
    /// already-successful status is REPLACED by the administrator advisory when the account name is the
    /// distributed one and the credential is one of two the product shipped with; L1149-L1153 does the
    /// same for the installation owner, replacing the installation-wide success. Neither branch can be
    /// entered from any other status, so member 5 means "tenant sign-in accepted, credential is one we
    /// published" and member 6 means the same for an installation-wide sign-in.
    /// </para>
    /// <para>
    /// The promotion is therefore not a refusal in disguise. A caller who reaches it has presented the
    /// correct credential; what the outcome adds is that the credential is publicly known.
    /// </para>
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
                It.IsAny<string>(),
                It.IsAny<bool>(),
                It.IsAny<IReadOnlyList<string>>(),
                It.IsAny<IReadOnlyList<string>>(),
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
    /// <remarks>
    /// Both halves matter. A tenant member who happens to choose one of the distributed credentials is not
    /// the distributed administrator, and the distributed administrator holding a credential of its own
    /// choosing is not weakly credentialed. Asserting the absence of the advisory in both directions is
    /// what stops the promotion from widening into a general credential-quality opinion, which this
    /// service does not hold.
    /// </remarks>
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
    /// <para>
    /// This is the assertion that makes the promotion semantics observable in the trail rather than only
    /// in the returned reason, and it is the reason the promotion has to be understood rather than merely
    /// reproduced. Because the status variable is REPLACED by the promotion, anything downstream that maps
    /// the status onto an audit name sees the promoted member and not the member it was promoted from -
    /// and the accepting members are exactly what such a mapping tends to enumerate. An implementation
    /// that enumerated only members 1 and 2 as acceptances would therefore record a promoted sign-in as a
    /// refusal, name it with the failure event, and emit a second record besides.
    /// </para>
    /// <para>
    /// The required behaviour is stated positively here: one record, describing an acceptance, named for
    /// the pre-promotion outcome, so that an installation still signing in with a published credential is
    /// visible in the trail as the tenant or installation-wide sign-in it actually is.
    /// </para>
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

    /// <summary>
    /// All seven legacy ordinals survive the rename, contiguously and in their original order.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The ordinals are load-bearing rather than incidental. The legacy enumeration was declared with
    /// explicit values 0 through 6, the status travelled by value through a by-reference argument, and
    /// the value reached comparisons in code the migration does not own. A rename that renumbered them -
    /// by dropping the member this migration refuses to admit, say, or by reordering for readability -
    /// would silently change the meaning of every persisted or transmitted integer.
    /// </para>
    /// <para>
    /// Asserted by ordinal AND by count, because either check alone is satisfiable by a wrong
    /// enumeration: seven members could be misnumbered, and the right numbers could be joined by an
    /// eighth member the legacy never had.
    /// </para>
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
    /// <remarks>
    /// The mapping is asserted rather than assumed because the audit name depends on it. See
    /// <see cref="AuditEventNames_PreserveTheLegacyLogTypeKeyStrings"/> for why that dependency is the
    /// consequential one.
    /// </remarks>
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
    /// <para>
    /// This is the one place where the rename could have broken something invisible. The legacy wrote its
    /// audit key as <c>loginStatus.ToString</c> at <c>UserController.vb</c> L80, so the key WAS the member
    /// name: a trail accumulated before the migration holds the strings asserted below, and a query, an
    /// alert or a report written against that trail matches on them. Renaming the members to PascalCase
    /// and letting the audit name follow would have silently orphaned every one of those.
    /// </para>
    /// <para>
    /// The migration keeps the two apart deliberately: the members are renamed for the target language
    /// and the audit names are pinned to the legacy strings, so the audit intent survives the change of
    /// mechanism. No divergence is introduced here, so this needs no entry beyond the mapping itself.
    /// </para>
    /// <para>
    /// The session names are asserted alongside them for a different reason: they have no legacy
    /// counterpart at all, because the legacy had no rotation to record. Pinning them here stops them
    /// drifting into the login family's shape and being mistaken for ported names.
    /// </para>
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
    /// <remarks>
    /// <para>
    /// The legacy audited exactly two of its seven members - the failure member and the lockout member,
    /// grouped by one condition at <c>UserController.vb</c> L1138 - and audited nothing on a success. The
    /// grouping is worth naming, because it is the internal contradiction that justifies the narrowing
    /// asserted in region A: the same code that recorded a lockout attempt as a failure handed the
    /// caller a status the sign-in screen then treated as authenticated.
    /// </para>
    /// <para>
    /// MIGRATION: the account identifier is recorded. The legacy passed its integer stand-in for "absent"
    /// - minus one, not a null - at <c>UserController.vb</c> L79, so its trail recorded that a sign-in had
    /// failed without recording whose, which makes a run against one account indistinguishable from
    /// scattered mistyping across many. Recorded in MIGRATION_NOTES.md as a deliberate improvement.
    /// </para>
    /// </remarks>
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

    /// <summary>
    /// No audit record carries a credential, a stored representation or a token value.
    /// </summary>
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
                .Concat([record.EventName, record.FailureCode, record.ActorUserName, record.ResourceId]));

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
    /// <para>
    /// Transcribed from <c>Login.ascx.vb</c> L168-L185, which is the only branch of the legacy verdict that
    /// did not run through the L187 inequality. Its four leaves were: L175 "EnterCode" when the code fields
    /// had not yet been shown; L178 "InvalidCode" when they had and a non-empty code had been submitted;
    /// L180 "EnterCode" again when they had and the field was left empty; and L184 "UserNotAuthorized" when
    /// the tenant was not on verified registration at all.
    /// </para>
    /// <para>
    /// The first and third leaves are one case here rather than two, and that is a faithful mapping rather
    /// than a lost distinction. What separated them in the legacy was <em>whether the screen had already
    /// rendered the code fields</em> - a property of the page's own view state, not of the request. A
    /// stateless endpoint has no such memory and needs none: both leaves selected the SAME message key, so
    /// both map onto the same reason. The distinction that survives is the one that was ever about the
    /// submission - a code was supplied and was wrong, versus no code was supplied.
    /// </para>
    /// <para>
    /// MIGRATION: these three reasons are RETURNED to the caller, where the legacy set a message key on an
    /// event that was raised. That is safe here only because of the ordering asserted in
    /// <see cref="LoginAsync_TheApprovalLadder_IsReachedOnlyAfterTheCredentialIsProved"/>: every caller who
    /// receives one of them has already presented the account's correct credential, so none of the three
    /// tells an unauthenticated caller that an account exists.
    /// </para>
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
    /// <remarks>
    /// The legacy could not reach this condition: the call it made to persist the approval returned nothing
    /// and reported no failure, so a store that declined was indistinguishable from one that complied.
    /// Continuing would admit an account the store still holds as unapproved, so the outcome is a refusal -
    /// but a refusal with its own reason, because the one caller who did everything right should not be
    /// told its code was wrong.
    /// </remarks>
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
    /// <para>
    /// MIGRATION: the gate order is deliberately not the legacy order, and this is the test that fixes it
    /// in place. The legacy provider evaluated the lock, then approval, then the credential, which meant
    /// the approval gate PERSISTED AN APPROVAL before any credential had been compared. The code it
    /// compared is not a secret - it is the tenant identifier, a hyphen and the account identifier, both of
    /// which appear in ordinary links - so a caller who knew only an account name could submit that code
    /// with a deliberately wrong credential, have the account approved and committed, and then be refused.
    /// The refusal made it look harmless; the state change was permanent, and it turned a pending
    /// registration into a live account awaiting only a credential guess.
    /// </para>
    /// <para>
    /// Minimal Change Clause item 1 preserves a discovered defect unless it blocks delivery. This one does,
    /// so the credential is proved first and MIGRATION_NOTES.md records both the defect and the reordering.
    /// Every observable outcome for a CORRECT credential is preserved - a verified code still approves and
    /// signs in, an absent or wrong code is still refused - and the only outcome that changes is the one
    /// that should never have existed.
    /// </para>
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

    /// <summary>
    /// An installation-wide account is exempt from the approval ladder.
    /// </summary>
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
    /// A locked account's lock is disclosed only to a caller already entitled to that detail.
    /// </summary>
    /// <param name="callerIsSuperUser">Whether the signed-in caller is an installation-wide account.</param>
    /// <param name="callerIsTenantAdministrator">Whether the signed-in caller administers this tenant.</param>
    /// <param name="expectedCode">The reason code such a caller receives.</param>
    /// <returns>A task representing the assertions.</returns>
    /// <remarks>
    /// "This account exists and is locked" is itself a disclosure, so an unauthenticated caller receives
    /// the uniform denial and an administrator receives the detail it needs in order to act.
    /// </remarks>
    [Theory]
    [InlineData(false, false, InvalidCredentialsCode)]
    [InlineData(true, false, LockedOutCode)]
    [InlineData(false, true, LockedOutCode)]
    public async Task LoginAsync_ALockedAccount_DisclosesTheLockOnlyToAnEntitledCaller(
        bool callerIsSuperUser,
        bool callerIsTenantAdministrator,
        string expectedCode)
    {
        SignInHarness harness = SignInHarness.Ready();
        harness.IsLockedOut = true;
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

    // ---------------------------------------------------------------------------------------------
    // Region E -- token orchestration. The algorithm belongs to the token suite; the sequencing is here.
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// An accepted credential mints exactly one pair, and the pair is minted with the facts the caller
    /// actually holds.
    /// </summary>
    /// <returns>A task representing the assertions.</returns>
    /// <remarks>
    /// Every fact the token asserts is passed to the token contract rather than fetched by it, so this is
    /// where the correctness of those arguments is established. The roles and permission keys are read
    /// through their own abstractions and handed on unaltered - neither invented, filtered nor reordered
    /// here.
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
                AccountName,
                false,
                It.Is<IReadOnlyList<string>>(roles => roles.SequenceEqual(harness.RoleNames)),
                It.Is<IReadOnlyList<string>>(keys => keys.SequenceEqual(harness.PermissionKeys)),
                It.IsAny<CancellationToken>()),
            Times.Once);

        outcome.Value.User.Roles.Should().Equal(harness.RoleNames);
        outcome.Value.User.Permissions.Should().Equal(harness.PermissionKeys);
        outcome.Value.User.PortalId.Should().Be(PortalId, "and minus one survives as a real tenant identifier");
    }

    /// <summary>
    /// No refusal mints a token, whichever gate closed.
    /// </summary>
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
                It.IsAny<string>(),
                It.IsAny<bool>(),
                It.IsAny<IReadOnlyList<string>>(),
                It.IsAny<IReadOnlyList<string>>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
    }

    /// <summary>
    /// The expiry the caller receives is the one the token contract minted, computed from the injected
    /// clock and the configured lifetime and never recomputed here.
    /// </summary>
    /// <returns>A task representing the assertions.</returns>
    /// <remarks>
    /// <para>
    /// The configured lifetime lives on the bound token settings and is read by the token implementation,
    /// not by this service - which is exactly why the assertion is that the value is PROPAGATED unchanged.
    /// A service that recomputed the expiry from its own clock reading would produce a value that drifted
    /// from the one the token itself asserts, and the two would disagree by however long the sign-in took.
    /// </para>
    /// <para>
    /// The instant is derived from the injected clock rather than the ambient one, which is what makes the
    /// assertion exact rather than approximate.
    /// </para>
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
    /// <para>
    /// Rotation is single-use by contract: exchanging a value mints a successor and marks the presented
    /// value used in the same atomic unit, so the retirement is asserted as a delegation to that contract
    /// rather than re-implemented here.
    /// </para>
    /// <para>
    /// The identity on the returned pair is re-read from stored state rather than copied from the retired
    /// token, so a role or permission change takes effect at the next exchange. Nothing the caller supplies
    /// alongside the token can influence whose successor is minted - the request carries the token and
    /// nothing else.
    /// </para>
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
        outcome.Value.User.Roles.Should().Equal(
            harness.RoleNames,
            "the caller's authority is re-read rather than copied from the retired token");

        harness.Tokens.Verify(
            tokens => tokens.RefreshAsync(SignInHarness.PresentedRefreshToken, It.IsAny<CancellationToken>()),
            Times.Once);
        harness.Tokens.Verify(
            tokens => tokens.IssueTokensAsync(
                It.IsAny<int>(),
                It.IsAny<int>(),
                It.IsAny<string>(),
                It.IsAny<bool>(),
                It.IsAny<IReadOnlyList<string>>(),
                It.IsAny<IReadOnlyList<string>>(),
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
    /// <remarks>
    /// The four conditions are deliberately indistinguishable, so a guessed value cannot be confirmed to
    /// have once existed. Collapsing them is a narrowing of information rather than an inability to tell
    /// them apart.
    /// </remarks>
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
                It.IsAny<string>(),
                It.IsAny<bool>(),
                It.IsAny<IReadOnlyList<string>>(),
                It.IsAny<IReadOnlyList<string>>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
    }

    /// <summary>
    /// A blank refresh token is refused without troubling the token store.
    /// </summary>
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
            tokens => tokens.RefreshAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()),
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
    /// A logout revokes the refresh token and does nothing else: there is no server-side retraction of
    /// an access token to assert.
    /// </summary>
    /// <returns>A task representing the assertions.</returns>
    /// <remarks>
    /// <para>
    /// MIGRATION: the legacy logout mechanism has no counterpart, and this is the most consequential absence on the
    /// contract. It destroyed the ticket cookie and four further cookies by name, back-dating two of them
    /// so the browser dropped them at once, and that ended the session instantly because the session lived
    /// in the cookie. A bearer access token is self-contained and asserts its own validity, so once one has
    /// been handed to a caller no server action retracts it. A logout therefore has exactly three parts:
    /// the refresh token is revoked so no successor can be minted, the short access-token lifetime elapses,
    /// and the client discards its own copy. No list of rejected access tokens is introduced and no
    /// per-request revocation lookup is performed - either would turn stateless bearer authentication back
    /// into the server-held session this migration exists to leave behind. Recorded in MIGRATION_NOTES.md.
    /// </para>
    /// <para>
    /// MIGRATION: the persistence flag that accompanied the legacy ticket has no counterpart either.
    /// Surviving beyond the browser session is exactly what that cookie was for, and a rotating refresh
    /// token does the same job while being single-use and revocable, which the cookie was neither. Recorded
    /// in MIGRATION_NOTES.md.
    /// </para>
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
    /// A logout with nothing to revoke succeeds, because the caller has already achieved the only
    /// outcome it asked for.
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

    // ---------------------------------------------------------------------------------------------
    // Region F -- replacing a superseded stored representation on the first accepted sign-in.
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// A stored representation the hashing abstraction considers superseded is replaced once, on the first
    /// accepted sign-in, and the sign-in still succeeds.
    /// </summary>
    /// <returns>A task representing the assertions.</returns>
    /// <remarks>
    /// <para>
    /// MIGRATION: the credential store changes from reversible to one-way, and that is a security fix rather
    /// than a refactor. The original schema held the value in clear text, and the membership provider was
    /// later configured to store it reversibly with retrieval enabled, decryptable with a key committed to
    /// source control. A one-way store cannot verify a value held that way, so the migration path is to
    /// replace the stored representation the first time a credential is successfully presented, with an
    /// administrative reset as the fallback for an account that never signs in again. Recorded in
    /// MIGRATION_NOTES.md.
    /// </para>
    /// <para>
    /// MIGRATION: credential retrieval is not carried forward at all, and neither is the reminder message
    /// the recovery screen sent. The legacy member returned the caller's own credential in clear text
    /// whenever retrieval was enabled, and the recovery screen decrypted it and mailed it. A one-way store
    /// makes recovery impossible by construction, which is the point of adopting one, so no member returns,
    /// echoes or reconstructs a credential and no recovery flow exists to be tested. Recorded in
    /// MIGRATION_NOTES.md, and asserted structurally by
    /// <see cref="AuthService_ExposesNoMemberThatCouldReturnACredential"/>.
    /// </para>
    /// <para>
    /// The replacement is written through the account store's own explicit statement rather than through
    /// the tracked graph, so an ordinary sign-in still commits nothing - see
    /// <see cref="LoginAsync_AnOrdinarySignIn_CommitsNothing"/>, which states that as a property in its own
    /// right.
    /// </para>
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
                Now,
                It.IsAny<CancellationToken>()),
            Times.Once,
            "and written exactly once, stamped with the instant the injected clock reported");
    }

    /// <summary>
    /// A stored representation the hashing abstraction is content with is left alone and nothing is written.
    /// </summary>
    /// <returns>A task representing the assertions.</returns>
    /// <remarks>
    /// The replacement must be a one-time migration step rather than something every sign-in performs.
    /// Rewriting a current representation on every sign-in would turn a read-mostly path into a write path
    /// for no benefit, and would make every sign-in depend on the store accepting a write.
    /// </remarks>
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
                It.IsAny<DateTime>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
    }

    /// <summary>
    /// A refused credential neither replaces nor writes anything, however superseded the stored
    /// representation is.
    /// </summary>
    /// <returns>A task representing the assertions.</returns>
    /// <remarks>
    /// The condition worth guarding is the pairing: an account whose representation is superseded AND whose
    /// credential was wrong. Replacing on that path would let a caller who cannot sign in still cause a
    /// write, and would replace the stored representation with one derived from a credential that was
    /// rejected.
    /// </remarks>
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
    /// credentials must not report on itself by logging. The closed diagnostic vocabulary is the route.
    /// </remarks>
    [Fact]
    public async Task LoginAsync_AReplacementTheStoreDeclines_StillSignsInAndReportsTheAnomaly()
    {
        SignInHarness harness = SignInHarness.Ready();
        harness.StoredRepresentationIsSuperseded = true;
        harness.ReplacementAccepted = false;

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

    /// <summary>
    /// An ordinary accepted sign-in commits nothing through the unit of work.
    /// </summary>
    /// <returns>A task representing the assertions.</returns>
    /// <remarks>
    /// <para>
    /// Worth stating explicitly, because the natural expectation is the opposite. A sign-in updates
    /// bookkeeping - the last accepted instant, the cleared attempt counters, and on the first accepted
    /// sign-in the replaced stored representation - so it looks like a write path. Every one of those
    /// writes goes to the external membership store through an explicit statement rather than through the
    /// tracked graph, so there is nothing for a commit to flush.
    /// </para>
    /// <para>
    /// The single exception is the verified-registration approval, which changes a tracked row and is the
    /// only branch that opens the transaction boundary; that case is asserted in
    /// <see cref="LoginAsync_TheCorrectVerificationCode_ApprovesTheAccountAndCommitsExactlyOnce"/>.
    /// Asserting the absence here is what stops a future revision from committing on every sign-in and
    /// making the transaction boundary meaningless.
    /// </para>
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
    /// <para>
    /// The shipped policy is preserved verbatim rather than tightened: a minimum length of seven, no
    /// required non-alphanumeric characters, no question-and-answer pair, and email uniqueness not
    /// enforced. Tightening any of them during a migration would bar existing members from accounts they
    /// hold today, so hardening is left as a separate, explicit decision.
    /// </para>
    /// <para>
    /// Rule-for-rule coverage of those four settings belongs to the sign-in request validator's suite, which
    /// owns the shape of a submission. What is asserted here is narrower and is this service's own
    /// responsibility: that it HONOURS the policy it is handed rather than substituting a constant of its
    /// own.
    /// </para>
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
    /// <remarks>
    /// Read from the legacy provider registration: retrieval and reset enabled, no question-and-answer
    /// requirement, a minimum length of seven, no required non-alphanumeric characters, and email
    /// uniqueness not enforced. Retrieval is the one setting deliberately NOT carried across, because the
    /// one-way store makes it impossible.
    /// </remarks>
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

    /// <summary>
    /// Four different reasons for rejecting a credential produce one indistinguishable answer.
    /// </summary>
    /// <returns>A task representing the assertions.</returns>
    /// <remarks>
    /// <para>
    /// An unknown tenant, an unknown account, an account holding no stored representation and a wrong
    /// credential are all answered identically. Any difference between them - a different code, a different
    /// sentence, even a different set of properties on the returned reason - turns this member into an
    /// oracle for account names or for the installation's tenants.
    /// </para>
    /// <para>
    /// The comparison is also performed on every one of the four, which is what stops the RESPONSE TIME
    /// from drawing the distinction that the wording refuses to draw. Identical wording over a
    /// short-circuited path is not uniformity.
    /// </para>
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
    /// The asserted identity is read from the injected caller-identity abstraction rather than from anything
    /// the request carried, and the snapshot crossing this boundary is a data-transfer type: no account
    /// entity leaves the service.
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
    /// <para>
    /// Asserted structurally rather than by inspection, because both properties are the kind that a
    /// well-meaning addition breaks. The by-reference argument is the shape this whole contract exists to
    /// retire: three legacy members reported a sign-in verdict by mutating a status variable their caller
    /// had declared, and all three collapse into one member returning a result. An added output argument
    /// would quietly reintroduce the pattern.
    /// </para>
    /// <para>
    /// The credential half is the security property. No member may return a credential, a stored
    /// representation, a verification answer or a signing key, and there is no recovery member for one to
    /// hide behind - which is what makes the one-way store's guarantee real rather than aspirational.
    /// </para>
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

    /// <summary>
    /// The refusing conditions a sign-in can end in, named so that a theory can walk all of them.
    /// </summary>
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

    /// <summary>
    /// Assembles the service over substituted abstractions, with every default set to the state a shipped
    /// installation is actually in.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Deliberately a private nested type rather than a shared file. Each test mutates only the one or two
    /// properties its condition depends on, so a reader sees the whole arrangement in the test body and
    /// nothing is inherited invisibly from somewhere else in the project.
    /// </para>
    /// <para>
    /// Every substituted collaborator is an abstraction declared by the domain or application layer. Two of
    /// them - the token contract and the caller-identity contract - have no implementation in the
    /// application layer at all, which is exactly why they are substituted as interfaces here and why no
    /// concrete token type is named anywhere in this file.
    /// </para>
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

        /// <summary>The account identifier the tenant records as its administrator.</summary>
        public const int AdministratorId = 2;

        /// <summary>The replacement a superseded stored representation is upgraded to.</summary>
        private const string ReplacementRepresentation = "replacement-representation-placeholder";

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
            PermissionKeys = [];

            Users = new Mock<IUserRepository>(MockBehavior.Loose);
            Portals = new Mock<IPortalRepository>(MockBehavior.Loose);
            Permissions = new Mock<IPermissionService>(MockBehavior.Loose);
            Accounts = new Mock<IUserService>(MockBehavior.Loose);
            Tokens = new Mock<ITokenService>(MockBehavior.Loose);
            PasswordHasher = new Mock<IPasswordHasher>(MockBehavior.Loose);
            Clock = new Mock<IClock>(MockBehavior.Loose);
            HostSettings = new Mock<IHostSettingsService>(MockBehavior.Loose);
            UnitOfWork = new Mock<IUnitOfWork>(MockBehavior.Loose);
            CurrentUser = new Mock<ICurrentUser>(MockBehavior.Loose);
            Audit = new Mock<IAuditSink>(MockBehavior.Loose);
            Diagnostics = new Mock<ISecurityDiagnostics>(MockBehavior.Loose);

            Service = new AuthService(
                Users.Object,
                Portals.Object,
                Permissions.Object,
                Accounts.Object,
                Tokens.Object,
                PasswordHasher.Object,
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
        /// to mint, which is how the expiry assertion stays tied to the configured lifetime rather than to a
        /// magic number.
        /// </summary>
        public JwtOptions TokenSettings { get; }

        /// <summary>Every audit record the service emitted, in the order it emitted them.</summary>
        public List<AuditEvent> AuditRecords { get; }

        /// <summary>The role names the account holds in this tenant.</summary>
        public IReadOnlyList<string> RoleNames { get; set; }

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

        /// <summary>Whether the hashing abstraction considers the stored representation superseded.</summary>
        public bool StoredRepresentationIsSuperseded { get; set; }

        /// <summary>Whether the store accepts a replacement representation.</summary>
        public bool ReplacementAccepted { get; set; } = true;

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

        /// <summary>Whether that caller is an installation-wide account.</summary>
        public bool SignedInCallerIsSuperUser { get; set; }

        /// <summary>The identifier of that caller.</summary>
        public int SignedInCallerUserId { get; set; } = UserId;

        /// <summary>The account store.</summary>
        public Mock<IUserRepository> Users { get; }

        /// <summary>The tenant store.</summary>
        public Mock<IPortalRepository> Portals { get; }

        /// <summary>Effective-permission resolution.</summary>
        public Mock<IPermissionService> Permissions { get; }

        /// <summary>The account-administration vertical, asked only whether a profile is outstanding.</summary>
        public Mock<IUserService> Accounts { get; }

        /// <summary>Token minting, rotation and revocation.</summary>
        public Mock<ITokenService> Tokens { get; }

        /// <summary>One-way credential comparison and replacement detection.</summary>
        public Mock<IPasswordHasher> PasswordHasher { get; }

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
                .Setup(users => users.GetCredentialStateAsync(
                    It.IsAny<int>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => (
                    harness.StoredRepresentationOnFile,
                    harness.StoredRepresentationOnFile ? StoredRepresentation : null,
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
                .ReturnsAsync(Result<bool>.Success(false));

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
                    It.IsAny<string>(),
                    It.IsAny<bool>(),
                    It.IsAny<IReadOnlyList<string>>(),
                    It.IsAny<IReadOnlyList<string>>(),
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

            harness.Tokens
                .Setup(tokens => tokens.RefreshAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
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

        /// <summary>
        /// Builds a harness arranged to end in one named refusing condition.
        /// </summary>
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
                new RefreshTokenRequest { RefreshToken = refreshToken },
                CancellationToken.None);
    }
}
