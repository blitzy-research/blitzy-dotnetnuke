using System.Globalization;
using DnnMigration.Application.Abstractions;
using DnnMigration.Application.Dtos.Common;
using DnnMigration.Application.Dtos.Role;
using DnnMigration.Application.Services;
using DnnMigration.Domain.Abstractions.Repositories;
using DnnMigration.Domain.Abstractions.Services;
using DnnMigration.Domain.Common;
using DnnMigration.Domain.Entities;
using DnnMigration.Domain.Enums;
using FluentAssertions;
using Moq;
using Xunit;
using Frequency = DnnMigration.Domain.Enums.BillingFrequency;

namespace DnnMigration.UnitTests.Application;

/// <summary>
/// Pins the paid-membership term engine of <see cref="RoleService"/> - the single most intricate piece of
/// date arithmetic in the migration - together with the assignment and removal rules that surround it,
/// against recording doubles for every collaborator and a frozen clock.
/// </summary>
/// <remarks>
/// THE CLOCK IS INJECTED, ALWAYS. The legacy engine read the ambient machine clock FOUR separate times
/// within one derivation - the expiry seed at L505, the effective-date comparison at L530, and the expiry
/// comparison and reassignment at L533-L534 - so a request that crossed a tick between two of them could
/// clear one bound against one instant and seed the other from a different one.
/// </remarks>
public class RoleServiceApplicationTests
{
    /// <summary>
    /// The tenant under test. Minus one is a REAL portal identifier, not an absence marker: the column is
    /// declared <c>IDENTITY (-1, 1)</c> in the baseline schema, so the first portal an installation ever
    /// creates bears it, and it collides exactly with the legacy absent-integer sentinel.
    /// </summary>
    private const int PortalId = -1;

    /// <summary>
    /// The role under test. Zero is a REAL role identifier: <c>Roles.RoleID</c> is declared <c>IDENTITY (0,
    /// 1)</c>, so the first role bears zero and it must never be read as unset merely because it equals the
    /// CLR default for its type.
    /// </summary>
    private const int RoleId = 0;

    /// <summary>The portal's administrator role, distinct from the role under test.</summary>
    private const int AdministratorRoleId = 41;

    /// <summary>The portal's registered-members role, distinct from the role under test.</summary>
    private const int RegisteredRoleId = 42;

    /// <summary>The member being assigned or removed.</summary>
    private const int UserId = 7;

    /// <summary>The portal's designated administrator account.</summary>
    private const int AdministratorUserId = 2;

    /// <summary>The role name every fixture carries.</summary>
    private const string RoleName = "Subscribers";

    /// <summary>The member's login name, which is the key the cache invalidation is keyed by.</summary>
    private const string MemberName = "measured_member";

    /// <summary>The operator each call is made on behalf of, so audit attribution can be asserted.</summary>
    private const int OperatorUserId = 2;

    /// <summary>The operator's account name, as an audit record carries it.</summary>
    private const string OperatorUserName = "measured_operator";

    private const string PortalNotFoundCode = "portal.not_found";

    private const string RoleNotFoundCode = "role.not_found";

    private const string UserNotFoundCode = "user.not_found";

    private const string AssignmentNotFoundCode = "role_assignment.not_found";

    private const string AssignmentProtectedCode = "role_assignment.protected";

    private const string AssignmentExpiredNotRemovedCode = "role_assignment.expired_not_removed";

    /// <summary>
    /// The frozen instant every derivation is measured against, chosen rather than picked at random.
    /// </summary>
    /// <remarks>
    /// The thirty-first of January is deliberate: a one-month offset from it lands on the twenty-eighth of
    /// February, which is the calendar clamping the legacy month interval performed and which a naive
    /// thirty-day substitution would silently break.
    /// </remarks>
    private static readonly DateTime Now = new(2026, 1, 31, 15, 9, 26, DateTimeKind.Utc);

    /// <summary>
    /// The perpetual-term date. The legacy engine assigned this literal for the one-off frequency and it is
    /// externally observable in every existing row, so it is carried through unchanged rather than
    /// converted into an absent expiry.
    /// </summary>
    private static readonly DateTime PerpetualExpiry = new(9999, 12, 31, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>
    /// Each of the SIX legacy frequency codes is the character the column actually stores, and the migrated
    /// enumeration's underlying value is that character rather than an arbitrary ordinal.
    /// </summary>
    /// <param name="code">The single-character code exactly as the column holds it.</param>
    /// <param name="expected">The migrated member the code must resolve to.</param>
    /// <remarks>
    /// The codes are DATA, never identifiers, and this test is what stops a future rename from passing
    /// review. <c>Roles.BillingFrequency</c> and <c>Roles.TrialFrequency</c> are both declared <c>char(1)
    /// NULL</c> in the baseline schema, the terminal chain leaves them with no foreign key and no check
    /// constraint, and the six characters below are the literal bytes already sitting in every existing
    /// installation.
    /// </remarks>
    [Theory]
    [InlineData("N", Frequency.None)]
    [InlineData("O", Frequency.OneTime)]
    [InlineData("D", Frequency.Day)]
    [InlineData("W", Frequency.Week)]
    [InlineData("M", Frequency.Month)]
    [InlineData("Y", Frequency.Year)]
    public void FrequencyCode_IsTheStoredCharacterAndRoundTripsThroughTheEnumeration(
        string code,
        Frequency expected)
    {
        code.Should().HaveLength(1, "the column is a single character wide");

        ((Frequency)code[0]).Should().Be(expected, "the code is the enumeration's underlying value");
        ((char)expected).Should().Be(code[0], "and the conversion is lossless in both directions");
    }

    /// <summary>
    /// The service refuses construction without any one of its nine collaborators, so a registration
    /// mistake surfaces at composition rather than as a null dereference inside a derivation.
    /// </summary>
    [Fact]
    public void Service_RefusesConstructionWithoutEveryCollaborator()
    {
        var roles = new Mock<IRoleRepository>().Object;
        var portals = new Mock<IPortalRepository>().Object;
        var users = new Mock<IUserRepository>().Object;
        var permissions = new Mock<IPermissionService>().Object;
        var unitOfWork = new Mock<IUnitOfWork>().Object;
        var clock = new Mock<IClock>().Object;
        var cache = new Mock<ICacheService>().Object;
        var currentUser = new Mock<ICurrentUser>().Object;
        var audit = new Mock<IAuditSink>().Object;

        Assert.Throws<ArgumentNullException>(
            "roles",
            () => _ = new RoleService(null!, portals, users, permissions, unitOfWork, clock, cache, currentUser, audit));
        Assert.Throws<ArgumentNullException>(
            "portals",
            () => _ = new RoleService(roles, null!, users, permissions, unitOfWork, clock, cache, currentUser, audit));
        Assert.Throws<ArgumentNullException>(
            "users",
            () => _ = new RoleService(roles, portals, null!, permissions, unitOfWork, clock, cache, currentUser, audit));
        Assert.Throws<ArgumentNullException>(
            "permissions",
            () => _ = new RoleService(roles, portals, users, null!, unitOfWork, clock, cache, currentUser, audit));
        Assert.Throws<ArgumentNullException>(
            "unitOfWork",
            () => _ = new RoleService(roles, portals, users, permissions, null!, clock, cache, currentUser, audit));
        Assert.Throws<ArgumentNullException>(
            "clock",
            () => _ = new RoleService(roles, portals, users, permissions, unitOfWork, null!, cache, currentUser, audit));
        Assert.Throws<ArgumentNullException>(
            "cache",
            () => _ = new RoleService(roles, portals, users, permissions, unitOfWork, clock, null!, currentUser, audit));
        Assert.Throws<ArgumentNullException>(
            "currentUser",
            () => _ = new RoleService(roles, portals, users, permissions, unitOfWork, clock, cache, null!, audit));
        Assert.Throws<ArgumentNullException>(
            "audit",
            () => _ = new RoleService(roles, portals, users, permissions, unitOfWork, clock, cache, currentUser, null!));
    }

    /// <summary>The day code offsets the expiry by the period counted in days.</summary>
    [Fact]
    public async Task AssignUserToRole_DayFrequency_OffsetsTheExpiryByThePeriodInDays()
    {
        Harness harness = Harness.Ready();
        harness.LookupRole = TermRole(Frequency.Day, period: 30);

        Result outcome = await harness.Service
            .AssignUserToRoleAsync(PortalId, RoleId, Assignment(), CancellationToken.None);

        outcome.IsSuccess.Should().BeTrue();
        UserRole stored = Assert.Single(harness.AddedAssignments);
        stored.ExpiryDate.Should().Be(Now.AddDays(30));
        stored.ExpiryDate.Should().Be(new DateTime(2026, 3, 2, 15, 9, 26, DateTimeKind.Utc));
        harness.UnitOfWork.Verify(unit => unit.SaveChangesAsync(CancellationToken.None), Times.Once);
    }

    /// <summary>The week code offsets the expiry by SEVEN DAYS PER PERIOD, not by a week interval.</summary>
    [Fact]
    public async Task AssignUserToRole_WeekFrequency_OffsetsTheExpiryBySevenDaysPerPeriod()
    {
        Harness harness = Harness.Ready();
        harness.LookupRole = TermRole(Frequency.Week, period: 2);

        Result outcome = await harness.Service
            .AssignUserToRoleAsync(PortalId, RoleId, Assignment(), CancellationToken.None);

        outcome.IsSuccess.Should().BeTrue();
        UserRole stored = Assert.Single(harness.AddedAssignments);
        stored.ExpiryDate.Should().Be(Now.AddDays(2 * 7));
        stored.ExpiryDate.Should().Be(new DateTime(2026, 2, 14, 15, 9, 26, DateTimeKind.Utc));
    }

    /// <summary>
    /// The month code offsets the expiry by the period counted in months, preserving the legacy calendar
    /// clamping when the target month is shorter than the source month.
    /// </summary>
    [Fact]
    public async Task AssignUserToRole_MonthFrequency_OffsetsTheExpiryByThePeriodInMonthsAndClampsTheDay()
    {
        Harness harness = Harness.Ready();
        harness.LookupRole = TermRole(Frequency.Month, period: 1);

        Result outcome = await harness.Service
            .AssignUserToRoleAsync(PortalId, RoleId, Assignment(), CancellationToken.None);

        outcome.IsSuccess.Should().BeTrue();
        UserRole stored = Assert.Single(harness.AddedAssignments);
        stored.ExpiryDate.Should().Be(Now.AddMonths(1));
        stored.ExpiryDate.Should().Be(
            new DateTime(2026, 2, 28, 15, 9, 26, DateTimeKind.Utc),
            "a one-month term from the thirty-first of January clamps onto the last day of February");
    }

    /// <summary>
    /// The year code offsets the expiry by the period counted in years, and the migrated implementation's
    /// twelve-months-per-year form is proved equivalent to the framework's own year offset.
    /// </summary>
    [Fact]
    public async Task AssignUserToRole_YearFrequency_OffsetsTheExpiryByThePeriodInYears()
    {
        Harness harness = Harness.Ready();
        harness.LookupRole = TermRole(Frequency.Year, period: 1);

        Result outcome = await harness.Service
            .AssignUserToRoleAsync(PortalId, RoleId, Assignment(), CancellationToken.None);

        outcome.IsSuccess.Should().BeTrue();
        UserRole stored = Assert.Single(harness.AddedAssignments);
        stored.ExpiryDate.Should().Be(Now.AddYears(1));
        stored.ExpiryDate.Should().Be(new DateTime(2027, 1, 31, 15, 9, 26, DateTimeKind.Utc));
    }

    /// <summary>
    /// A twelve-month period and a one-year period reach the same instant, including across a leap day.
    /// </summary>
    [Fact]
    public async Task AssignUserToRole_YearFrequency_ClampsALeapDayOntoTheTwentyEighth()
    {
        Harness harness = Harness.Ready();
        harness.LookupRole = TermRole(Frequency.Year, period: 1);
        var leapDay = new DateTime(2028, 2, 29, 15, 9, 26, DateTimeKind.Utc);
        harness.Clock.SetupGet(clock => clock.UtcNow).Returns(leapDay);

        Result outcome = await harness.Service.AssignUserToRoleAsync(
            PortalId,
            RoleId,
            Assignment(),
            CancellationToken.None);

        outcome.IsSuccess.Should().BeTrue();
        UserRole stored = Assert.Single(harness.AddedAssignments);
        stored.ExpiryDate.Should().Be(leapDay.AddYears(1));
        stored.ExpiryDate.Should().Be(new DateTime(2029, 2, 28, 15, 9, 26, DateTimeKind.Utc));
    }

    /// <summary>The never code stores NO expiry at all, so the membership does not lapse.</summary>
    [Fact]
    public async Task AssignUserToRole_NoneFrequency_StoresNoExpiry()
    {
        Harness harness = Harness.Ready();
        harness.LookupRole = TermRole(Frequency.None, period: 1);

        Result outcome = await harness.Service
            .AssignUserToRoleAsync(PortalId, RoleId, Assignment(), CancellationToken.None);

        outcome.IsSuccess.Should().BeTrue();
        UserRole stored = Assert.Single(harness.AddedAssignments);
        stored.ExpiryDate.Should().BeNull("the never code means the membership does not lapse");
        stored.GetStatus(Now).Should().Be(RoleStatus.Active);
    }

    /// <summary>The one-off code stores the perpetual far-future date, and does NOT consult the period.</summary>
    [Fact]
    public async Task AssignUserToRole_OneTimeFrequency_StoresThePerpetualFarFutureDate()
    {
        Harness harness = Harness.Ready();
        harness.LookupRole = TermRole(Frequency.OneTime, period: 1);

        Result outcome = await harness.Service
            .AssignUserToRoleAsync(PortalId, RoleId, Assignment(), CancellationToken.None);

        outcome.IsSuccess.Should().BeTrue();
        UserRole stored = Assert.Single(harness.AddedAssignments);
        stored.ExpiryDate.Should().Be(PerpetualExpiry);
        stored.ExpiryDate.Should().Be(new DateTime(9999, 12, 31, 0, 0, 0, DateTimeKind.Utc));
        stored.ExpiryDate!.Value.Year.Should().Be(9999);
    }

    /// <summary>
    /// Every one of the six codes derives a bound without throwing, whichever character the column holds.
    /// </summary>
    /// <param name="code">The single-character code exactly as the column holds it.</param>
    /// <param name="expiresEventually">Whether the code yields a bounded term.</param>
    /// <remarks>
    /// The individual arithmetic is pinned by the six preceding tests; this one drives the whole table
    /// through the service from the stored CHARACTER rather than from a member name, which is the form a
    /// row actually presents, and proves the conversion is wired end to end.
    /// </remarks>
    [Theory]
    [InlineData("N", false)]
    [InlineData("O", true)]
    [InlineData("D", true)]
    [InlineData("W", true)]
    [InlineData("M", true)]
    [InlineData("Y", true)]
    public async Task AssignUserToRole_AcceptsEveryStoredFrequencyCharacter(string code, bool expiresEventually)
    {
        Harness harness = Harness.Ready();
        harness.LookupRole = TermRole((Frequency)code[0], period: 1);

        Result outcome = await harness.Service
            .AssignUserToRoleAsync(PortalId, RoleId, Assignment(), CancellationToken.None);

        outcome.IsSuccess.Should().BeTrue();
        UserRole stored = Assert.Single(harness.AddedAssignments);
        stored.ExpiryDate.HasValue.Should().Be(expiresEventually);
    }

    /// <summary>
    /// An ABSENT period short-circuits the whole frequency table and yields no expiry, whatever frequency
    /// the role declares.
    /// </summary>
    /// <param name="code">The frequency code the role declares alongside its absent period.</param>
    [Theory]
    [InlineData("N")]
    [InlineData("O")]
    [InlineData("D")]
    [InlineData("W")]
    [InlineData("M")]
    [InlineData("Y")]
    public async Task AssignUserToRole_AbsentPeriod_ShortCircuitsBeforeTheFrequencyIsExamined(string code)
    {
        Harness harness = Harness.Ready();
        harness.LookupRole = new Role
        {
            RoleId = RoleId,
            PortalId = PortalId,
            RoleName = RoleName,
            ServiceFee = 9.99m,
            BillingFrequency = (Frequency)code[0],
            BillingPeriod = null,
        };

        Result outcome = await harness.Service
            .AssignUserToRoleAsync(PortalId, RoleId, Assignment(), CancellationToken.None);

        outcome.IsSuccess.Should().BeTrue();
        UserRole stored = Assert.Single(harness.AddedAssignments);
        stored.ExpiryDate.Should().BeNull(
            "an absent period is answered before the frequency table is reached");
    }

    /// <summary>
    /// The legacy absent-integer sentinel of MINUS ONE is NOT the migrated absence marker, and is proved
    /// not to be by pairing it with the one-off code.
    /// </summary>
    [Fact]
    public async Task AssignUserToRole_MinusOnePeriod_IsARealPeriodAndNotTheAbsenceMarker()
    {
        Harness harness = Harness.Ready();
        harness.LookupRole = TermRole(Frequency.OneTime, period: -1);

        Result outcome = await harness.Service
            .AssignUserToRoleAsync(PortalId, RoleId, Assignment(), CancellationToken.None);

        outcome.IsSuccess.Should().BeTrue();
        UserRole stored = Assert.Single(harness.AddedAssignments);
        stored.ExpiryDate.Should().Be(
            PerpetualExpiry,
            "minus one is a period the migrated engine carries, not an instruction to skip the table");
    }

    /// <summary>
    /// A negative stored period runs the bound BACKWARDS and is clamped at the earliest storable instant
    /// rather than overflowing.
    /// </summary>
    /// <remarks>
    /// A validated request cannot submit a period at or below zero, but a row written before those rules
    /// existed can hold one, and the engine is reached with stored values. The legacy day interval would
    /// have thrown for an extreme value; the migrated helper clamps in both directions, which is hardening
    /// rather than a change of business rule and is asserted here so it cannot regress.
    /// </remarks>
    [Fact]
    public async Task AssignUserToRole_ExtremeNegativePeriod_ClampsAtTheEarliestStorableInstant()
    {
        Harness harness = Harness.Ready();
        harness.LookupRole = TermRole(Frequency.Day, period: int.MinValue);

        Result outcome = await harness.Service
            .AssignUserToRoleAsync(PortalId, RoleId, Assignment(), CancellationToken.None);

        outcome.IsSuccess.Should().BeTrue();
        UserRole stored = Assert.Single(harness.AddedAssignments);
        stored.ExpiryDate.Should().Be(SqlServerRange.MinimumDateTime);
        stored.ExpiryDate.Should().Be(new DateTime(1753, 1, 1, 0, 0, 0, DateTimeKind.Unspecified));
    }

    /// <summary>
    /// An extreme positive period is clamped to the perpetual date rather than overflowing, and the week
    /// code's multiplication by seven cannot wrap into a NEGATIVE day count.
    /// </summary>
    /// <param name="code">The offsetting frequency code under test.</param>
    /// <remarks>
    /// The week case is the dangerous one and is the reason the migrated engine widens before it
    /// multiplies. Multiplying the largest storable period by seven in thirty-two-bit arithmetic wraps to a
    /// negative day count, which would have moved an expiry silently INTO THE PAST - a membership
    /// cancelling itself on creation.
    /// </remarks>
    [Theory]
    [InlineData("D")]
    [InlineData("W")]
    [InlineData("M")]
    [InlineData("Y")]
    public async Task AssignUserToRole_ExtremePositivePeriod_ClampsToThePerpetualDateWithoutWrapping(
        string code)
    {
        Harness harness = Harness.Ready();
        harness.LookupRole = TermRole((Frequency)code[0], period: int.MaxValue);

        Result outcome = await harness.Service
            .AssignUserToRoleAsync(PortalId, RoleId, Assignment(), CancellationToken.None);

        outcome.IsSuccess.Should().BeTrue();
        UserRole stored = Assert.Single(harness.AddedAssignments);
        stored.ExpiryDate.Should().Be(PerpetualExpiry);
        stored.ExpiryDate.Should().BeAfter(Now, "a wrapped multiplication would land in the past");
    }

    /// <summary>
    /// When the trial has NOT been consumed and the trial frequency is not the never code, the TRIAL terms
    /// govern the expiry and the billing terms are ignored.
    /// </summary>
    [Fact]
    public async Task AssignUserToRole_TrialNotYetUsed_LetsTheTrialTermsGovern()
    {
        Harness harness = Harness.Ready();
        harness.LookupRole = TrialThenMonthlyRole();
        harness.ExistingAssignment = null;

        Result outcome = await harness.Service
            .AssignUserToRoleAsync(PortalId, RoleId, Assignment(), CancellationToken.None);

        outcome.IsSuccess.Should().BeTrue();
        UserRole stored = Assert.Single(harness.AddedAssignments);
        stored.ExpiryDate.Should().Be(Now.AddDays(14), "the fourteen-day trial governs");
        stored.ExpiryDate.Should().NotBe(Now.AddMonths(1), "the billing term must not have been used");
        stored.IsTrialUsed.Should().BeFalse("a newly staged assignment has consumed no trial");
    }

    /// <summary>
    /// When the trial HAS already been consumed, the BILLING terms govern - and the consumed fact is read
    /// from the stored row, never from anything the caller submits.
    /// </summary>
    [Fact]
    public async Task AssignUserToRole_TrialAlreadyUsed_LetsTheBillingTermsGovern()
    {
        Harness harness = Harness.Ready();
        harness.LookupRole = TrialThenMonthlyRole();
        harness.ExistingAssignment = Membership(trialUsed: true);

        Result outcome = await harness.Service
            .AssignUserToRoleAsync(PortalId, RoleId, Assignment(), CancellationToken.None);

        outcome.IsSuccess.Should().BeTrue();
        harness.AddedAssignments.Should().BeEmpty("the member already holds the role");
        harness.ExistingAssignment!.ExpiryDate.Should().Be(Now.AddMonths(1), "the billing term governs");
        harness.ExistingAssignment.ExpiryDate.Should().NotBe(Now.AddDays(14));
        harness.ExistingAssignment.IsTrialUsed.Should().BeTrue("a renewal never resets the trial fact");
    }

    /// <summary>
    /// A trial frequency of the NEVER code means "no trial", so the billing terms govern even though the
    /// trial has not been consumed.
    /// </summary>
    [Fact]
    public async Task AssignUserToRole_TrialFrequencyIsTheNeverCode_LetsTheBillingTermsGovern()
    {
        Harness harness = Harness.Ready();
        Role role = TrialThenMonthlyRole();
        role.TrialFrequency = Frequency.None;
        harness.LookupRole = role;

        Result outcome = await harness.Service
            .AssignUserToRoleAsync(PortalId, RoleId, Assignment(), CancellationToken.None);

        outcome.IsSuccess.Should().BeTrue();
        UserRole stored = Assert.Single(harness.AddedAssignments);
        stored.ExpiryDate.Should().Be(Now.AddMonths(1), "a never trial frequency means no trial at all");
        stored.ExpiryDate.Should().NotBe(Now.AddDays(14));
    }

    /// <summary>
    /// An ABSENT trial frequency lets the billing terms govern - the resolution of a legacy expression that
    /// did not short-circuit, and a measured behavioural difference.
    /// </summary>
    /// <remarks>
    /// MIGRATION: MEASURED BEHAVIOURAL DIFFERENCE, DELIBERATE AND RECORDED IN <c>MIGRATION_NOTES.md</c>.
    /// <c>RoleController.vb</c> L521 reads <c>If IsTrialUsed = False And role.TrialFrequency.ToString
    /// &lt;&gt; "N" Then</c>, and the operator is the NON-SHORT-CIRCUITING one, so the right-hand side was
    /// evaluated even when the left-hand side was already false.
    /// </remarks>
    [Fact]
    public async Task AssignUserToRole_AbsentTrialFrequency_LetsTheBillingTermsGovern()
    {
        Harness harness = Harness.Ready();
        Role role = TrialThenMonthlyRole();
        role.TrialFrequency = null;
        harness.LookupRole = role;

        Result outcome = await harness.Service
            .AssignUserToRoleAsync(PortalId, RoleId, Assignment(), CancellationToken.None);

        outcome.IsSuccess.Should().BeTrue();
        UserRole stored = Assert.Single(harness.AddedAssignments);
        stored.ExpiryDate.Should().Be(
            Now.AddMonths(1),
            "an absent trial frequency is absence, not the empty string the legacy reader produced");
        stored.ExpiryDate.Should().NotBe(Now.AddDays(14));
    }

    /// <summary>
    /// An absent trial frequency is evaluated without faulting even when the trial has already been
    /// consumed, which is the branch the legacy non-short-circuiting operator made reachable.
    /// </summary>
    [Fact]
    public async Task AssignUserToRole_AbsentTrialFrequencyAndConsumedTrial_DoesNotFault()
    {
        Harness harness = Harness.Ready();
        Role role = TrialThenMonthlyRole();
        role.TrialFrequency = null;
        role.TrialPeriod = null;
        harness.LookupRole = role;
        harness.ExistingAssignment = Membership(trialUsed: true);

        Result outcome = await harness.Service
            .AssignUserToRoleAsync(PortalId, RoleId, Assignment(), CancellationToken.None);

        outcome.IsSuccess.Should().BeTrue();
        harness.ExistingAssignment!.ExpiryDate.Should().Be(Now.AddMonths(1));
    }

    /// <summary>
    /// An effective date already in the PAST is stored EXACTLY AS SUBMITTED, so a backdated grant is
    /// recorded as the caller stated it.
    /// </summary>
    /// <remarks>
    /// The membership is still ACTIVE, which is the substantive property the withdrawn fact was reaching
    /// for: a start date in the past opens the membership rather than gating it, whether it is recorded or
    /// cleared. What changes is that the store now says WHEN it opened.
    /// </remarks>
    [Fact]
    public async Task AssignUserToRole_PastEffectiveDate_IsStoredExactlyAsSubmitted()
    {
        Harness harness = Harness.Ready();
        harness.LookupRole = TermRole(Frequency.Day, period: 7);
        DateTime backdated = Now.AddDays(-1);

        Result outcome = await harness.Service.AssignUserToRoleAsync(
            PortalId,
            RoleId,
            Assignment(effectiveDate: backdated),
            CancellationToken.None);

        outcome.IsSuccess.Should().BeTrue();
        UserRole stored = Assert.Single(harness.AddedAssignments);
        stored.EffectiveDate.Should().Be(backdated, "a stated start date is the caller's instruction");
        stored.GetStatus(Now).Should().Be(RoleStatus.Active, "a start already behind us opens the membership");
    }

    /// <summary>
    /// An effective date in the FUTURE is preserved exactly, so a membership can be granted ahead of time.
    /// </summary>
    [Fact]
    public async Task AssignUserToRole_FutureEffectiveDate_IsPreservedExactly()
    {
        Harness harness = Harness.Ready();
        harness.LookupRole = TermRole(Frequency.Day, period: 7);
        DateTime starts = Now.AddDays(10);

        Result outcome = await harness.Service.AssignUserToRoleAsync(
            PortalId,
            RoleId,
            Assignment(effectiveDate: starts),
            CancellationToken.None);

        outcome.IsSuccess.Should().BeTrue();
        UserRole stored = Assert.Single(harness.AddedAssignments);
        stored.EffectiveDate.Should().Be(starts);
        stored.GetStatus(Now).Should().Be(RoleStatus.Pending, "the membership is granted but not in force");
    }

    /// <summary>
    /// A SUBMITTED expiry date is stored exactly as submitted, whether it is already in the past or still
    /// in the future, and the role's term is not added to it.
    /// </summary>
    /// <param name="offsetDays">How far the submitted bound sits from the frozen instant.</param>
    /// <remarks>
    /// The derivation itself is not withdrawn and is asserted by the frequency facts above, all of which
    /// submit no bound. The two cases are separate because they are reached through different members in
    /// the legacy source, and this fact pins which one a caller's date reaches.
    /// </remarks>
    [Theory]
    [InlineData(-30)]
    [InlineData(10)]
    public async Task AssignUserToRole_SubmittedExpiryDate_IsStoredWithoutTheTermBeingAdded(int offsetDays)
    {
        Harness harness = Harness.Ready();
        harness.LookupRole = TermRole(Frequency.Day, period: 7);
        DateTime submitted = Now.AddDays(offsetDays);

        Result outcome = await harness.Service.AssignUserToRoleAsync(
            PortalId,
            RoleId,
            Assignment(expiryDate: submitted),
            CancellationToken.None);

        outcome.IsSuccess.Should().BeTrue();
        UserRole stored = Assert.Single(harness.AddedAssignments);
        stored.ExpiryDate.Should().Be(submitted, "a stated end date is the caller's instruction");
        stored.ExpiryDate.Should().NotBe(submitted.AddDays(7), "the term is not added to a stated bound");
        stored.ExpiryDate.Should().NotBe(Now.AddDays(7), "nor is the bound replaced by a derived one");
    }

    /// <summary>
    /// The legacy ABSENT-DATE MARKER submitted for either bound is read as absence at the request boundary,
    /// and is never stored as a real first-of-January-0001 instant.
    /// </summary>
    [Fact]
    public async Task AssignUserToRole_LegacyAbsentDateMarker_IsReadAsAbsenceForBothBounds()
    {
        Harness harness = Harness.Ready();
        harness.LookupRole = TermRole(Frequency.Day, period: 7);

        Result outcome = await harness.Service.AssignUserToRoleAsync(
            PortalId,
            RoleId,
            Assignment(effectiveDate: DateTime.MinValue, expiryDate: DateTime.MinValue),
            CancellationToken.None);

        outcome.IsSuccess.Should().BeTrue();
        UserRole stored = Assert.Single(harness.AddedAssignments);
        stored.EffectiveDate.Should().BeNull("the marker means unbounded, not the year one");
        stored.ExpiryDate.Should().Be(
            Now.AddDays(7),
            "an absent bound offsets from the current instant, exactly as an elapsed one does");
    }

    /// <summary>
    /// Submitting the absent-date marker for both bounds reaches THE SAME stored outcome as submitting
    /// neither, so translating the marker changes no answer.
    /// </summary>
    [Fact]
    public async Task AssignUserToRole_AbsentDateMarker_MatchesSubmittingNoBoundsAtAll()
    {
        Harness withMarker = Harness.Ready();
        withMarker.LookupRole = TermRole(Frequency.Month, period: 1);

        Result markerOutcome = await withMarker.Service.AssignUserToRoleAsync(
            PortalId,
            RoleId,
            Assignment(effectiveDate: DateTime.MinValue, expiryDate: DateTime.MinValue),
            CancellationToken.None);

        Harness withNothing = Harness.Ready();
        withNothing.LookupRole = TermRole(Frequency.Month, period: 1);

        Result absentOutcome = await withNothing.Service
            .AssignUserToRoleAsync(PortalId, RoleId, Assignment(), CancellationToken.None);

        markerOutcome.IsSuccess.Should().BeTrue("the marker is accepted, never refused");
        absentOutcome.IsSuccess.Should().BeTrue();

        UserRole fromMarker = Assert.Single(withMarker.AddedAssignments);
        UserRole fromAbsent = Assert.Single(withNothing.AddedAssignments);

        fromMarker.EffectiveDate.Should().Be(fromAbsent.EffectiveDate);
        fromMarker.ExpiryDate.Should().Be(fromAbsent.ExpiryDate);
        fromMarker.ExpiryDate.Should().Be(Now.AddMonths(1));
    }

    /// <summary>
    /// The absent-date marker is recognised even when a time component has been attached to it, because the
    /// comparison is made on the date part alone.
    /// </summary>
    [Fact]
    public async Task AssignUserToRole_AbsentDateMarkerCarryingATime_IsStillReadAsAbsence()
    {
        Harness harness = Harness.Ready();
        harness.LookupRole = TermRole(Frequency.Day, period: 7);

        Result outcome = await harness.Service.AssignUserToRoleAsync(
            PortalId,
            RoleId,
            Assignment(effectiveDate: DateTime.MinValue.AddHours(3)),
            CancellationToken.None);

        outcome.IsSuccess.Should().BeTrue();
        UserRole stored = Assert.Single(harness.AddedAssignments);
        stored.EffectiveDate.Should().BeNull();
    }

    /// <summary>
    /// An UNRECOGNISED frequency character leaves the bound exactly as the normalisation left it and does
    /// NOT throw - a legacy shape preserved deliberately rather than corrected.
    /// </summary>
    /// <remarks>
    /// The terminal columns carry no check constraint and no foreign key, so the store accepts any single
    /// character and an unrecognised one is genuinely reachable from live data. Preserving the non-throwing
    /// shape means such a row is still readable and still assignable, where a throw would have turned one
    /// bad character into a failed operation.
    /// </remarks>
    [Fact]
    public async Task AssignUserToRole_UnrecognisedFrequencyCharacter_DoesNotThrowAndLeavesTheBoundAlone()
    {
        Harness harness = Harness.Ready();
        harness.LookupRole = TermRole((Frequency)'Q', period: 3);

        Result outcome = await harness.Service
            .AssignUserToRoleAsync(PortalId, RoleId, Assignment(), CancellationToken.None);

        outcome.IsSuccess.Should().BeTrue("an unrecognised character is not an error");
        UserRole stored = Assert.Single(harness.AddedAssignments);
        stored.ExpiryDate.Should().BeNull("no bound was submitted, so none is derived");
        harness.UnitOfWork.Verify(unit => unit.SaveChangesAsync(CancellationToken.None), Times.Once);
    }

    /// <summary>
    /// An unrecognised frequency character preserves a submitted FUTURE bound unchanged, which is the other
    /// half of the fall-through shape.
    /// </summary>
    [Fact]
    public async Task AssignUserToRole_UnrecognisedFrequencyCharacter_PreservesASubmittedFutureBound()
    {
        Harness harness = Harness.Ready();
        harness.LookupRole = TermRole((Frequency)'Q', period: 3);
        DateTime submitted = Now.AddDays(45);

        Result outcome = await harness.Service.AssignUserToRoleAsync(
            PortalId,
            RoleId,
            Assignment(expiryDate: submitted),
            CancellationToken.None);

        outcome.IsSuccess.Should().BeTrue();
        UserRole stored = Assert.Single(harness.AddedAssignments);
        stored.ExpiryDate.Should().Be(submitted);
    }

    /// <summary>A member who does NOT yet hold the role has an assignment STAGED, and nothing is revised.</summary>
    [Fact]
    public async Task AssignUserToRole_MemberDoesNotHoldTheRole_StagesANewAssignment()
    {
        Harness harness = Harness.Ready();
        harness.LookupRole = TermRole(Frequency.Month, period: 1);
        harness.ExistingAssignment = null;

        Result outcome = await harness.Service.AssignUserToRoleAsync(
            PortalId,
            RoleId,
            Assignment(effectiveDate: Now.AddDays(5)),
            CancellationToken.None);

        outcome.IsSuccess.Should().BeTrue();
        UserRole stored = Assert.Single(harness.AddedAssignments);
        stored.UserId.Should().Be(UserId);
        stored.RoleId.Should().Be(RoleId, "a role identifier of zero is a real identifier");
        stored.EffectiveDate.Should().Be(Now.AddDays(5));
        stored.ExpiryDate.Should().Be(Now.AddMonths(1));
        stored.IsTrialUsed.Should().BeFalse();

        harness.Roles.Verify(
            repository => repository.AddUserRoleAsync(It.IsAny<UserRole>(), CancellationToken.None),
            Times.Once);
        harness.Roles.Verify(
            repository => repository.UpdateUserRoleAsync(It.IsAny<UserRole>(), It.IsAny<CancellationToken>()),
            Times.Never);
        harness.UnitOfWork.Verify(unit => unit.SaveChangesAsync(CancellationToken.None), Times.Once);
    }

    /// <summary>
    /// A member who ALREADY holds the role has the two bounds revised in place, and no second assignment is
    /// staged - so the operation is idempotent in exactly the way the legacy upsert was.
    /// </summary>
    [Fact]
    public async Task AssignUserToRole_MemberAlreadyHoldsTheRole_RevisesTheBoundsInPlace()
    {
        Harness harness = Harness.Ready();
        harness.LookupRole = TermRole(Frequency.Month, period: 1);
        UserRole held = Membership(trialUsed: true, expiryDate: Now.AddDays(-90));
        int identity = held.UserRoleId;
        harness.ExistingAssignment = held;

        Result outcome = await harness.Service
            .AssignUserToRoleAsync(PortalId, RoleId, Assignment(), CancellationToken.None);

        outcome.IsSuccess.Should().BeTrue();
        harness.AddedAssignments.Should().BeEmpty("a renewal stages no second row");
        held.UserRoleId.Should().Be(identity, "a renewal is not a move");
        held.UserId.Should().Be(UserId);
        held.RoleId.Should().Be(RoleId);
        held.IsTrialUsed.Should().BeTrue("the consumed-trial fact survives a renewal");
        held.ExpiryDate.Should().Be(Now.AddMonths(1));

        harness.Roles.Verify(
            repository => repository.AddUserRoleAsync(It.IsAny<UserRole>(), It.IsAny<CancellationToken>()),
            Times.Never);
        harness.UnitOfWork.Verify(unit => unit.SaveChangesAsync(CancellationToken.None), Times.Once);
    }

    /// <summary>
    /// The whole derivation is driven by ONE reading of the injected clock, so no two rules within a single
    /// assignment can disagree about what "now" means.
    /// </summary>
    [Fact]
    public async Task AssignUserToRole_ReadsTheInjectedClockExactlyOnce()
    {
        Harness derived = Harness.Ready();
        derived.LookupRole = TermRole(Frequency.Day, period: 7);

        Result derivedOutcome = await derived.Service.AssignUserToRoleAsync(
            PortalId,
            RoleId,
            Assignment(),
            CancellationToken.None);

        derivedOutcome.IsSuccess.Should().BeTrue();
        derived.Clock.VerifyGet(clock => clock.UtcNow, Times.Once);
        Assert.Single(derived.AddedAssignments).ExpiryDate.Should().Be(Now.AddDays(7));

        Harness submitted = Harness.Ready();
        submitted.LookupRole = TermRole(Frequency.Day, period: 7);
        DateTime ends = Now.AddDays(-1);

        Result submittedOutcome = await submitted.Service.AssignUserToRoleAsync(
            PortalId,
            RoleId,
            Assignment(effectiveDate: ends, expiryDate: ends),
            CancellationToken.None);

        submittedOutcome.IsSuccess.Should().BeTrue();
        submitted.Clock.VerifyGet(clock => clock.UtcNow, Times.Once);

        UserRole stored = Assert.Single(submitted.AddedAssignments);
        stored.EffectiveDate.Should().Be(ends);
        stored.ExpiryDate.Should().Be(ends);
    }

    /// <summary>
    /// Moving the injected clock moves every derived bound with it, which is the property that makes this
    /// engine testable at all.
    /// </summary>
    [Fact]
    public async Task AssignUserToRole_DerivedBoundsFollowTheInjectedClock()
    {
        Harness harness = Harness.Ready();
        harness.LookupRole = TermRole(Frequency.Day, period: 7);
        var later = new DateTime(2030, 6, 15, 8, 30, 0, DateTimeKind.Utc);
        harness.Clock.SetupGet(clock => clock.UtcNow).Returns(later);

        Result outcome = await harness.Service
            .AssignUserToRoleAsync(PortalId, RoleId, Assignment(), CancellationToken.None);

        outcome.IsSuccess.Should().BeTrue();
        UserRole stored = Assert.Single(harness.AddedAssignments);
        stored.ExpiryDate.Should().Be(later.AddDays(7));
        stored.ExpiryDate.Should().NotBe(Now.AddDays(7));
    }

    /// <summary>
    /// Each expected assignment failure is reported as a NAMED REASON on the outcome, and nothing is
    /// committed - which is what replaced the legacy by-reference status arguments.
    /// </summary>
    /// <param name="portalExists">Whether the tenant resolves.</param>
    /// <param name="roleExists">Whether the role resolves within that tenant.</param>
    /// <param name="memberExists">Whether the account resolves within that tenant.</param>
    /// <param name="expectedCode">The reason code the outcome must carry.</param>
    /// <remarks>
    /// The legacy procedures returned nothing at all and reported status by mutating a by-reference
    /// argument, so a caller could not distinguish an applied change from a discarded one. The migrated
    /// members return an outcome carrying a stable machine-readable code, and NO migrated member exposes a
    /// by-reference or output parameter of any kind.
    /// </remarks>
    [Theory]
    [InlineData(false, true, true, PortalNotFoundCode)]
    [InlineData(true, false, true, RoleNotFoundCode)]
    [InlineData(true, true, false, UserNotFoundCode)]
    public async Task AssignUserToRole_ExpectedFailure_IsReportedAsAReasonAndCommitsNothing(
        bool portalExists,
        bool roleExists,
        bool memberExists,
        string expectedCode)
    {
        Harness harness = Harness.Ready();
        harness.PortalExists = portalExists;
        harness.LookupRole = roleExists ? TermRole(Frequency.Month, period: 1) : null;
        harness.Member = memberExists ? Member() : null;

        Result outcome = await harness.Service
            .AssignUserToRoleAsync(PortalId, RoleId, Assignment(), CancellationToken.None);

        outcome.IsFailure.Should().BeTrue();
        outcome.Error.Should().NotBeNull();
        outcome.Error!.Code.Should().Be(expectedCode);
        outcome.Error.Message.Should().NotBeNullOrWhiteSpace("every failure is explicable");

        harness.AddedAssignments.Should().BeEmpty();
        harness.UnitOfWork.Verify(
            unit => unit.SaveChangesAsync(It.IsAny<CancellationToken>()),
            Times.Never);
        harness.Cache.Verify(
            cache => cache.InvalidateUser(It.IsAny<int>(), It.IsAny<string>()),
            Times.Never);
        harness.AuditRecords.Should().BeEmpty("nothing was committed, so nothing is recorded");
    }

    /// <summary>
    /// A role belonging to a DIFFERENT tenant is reported as missing rather than assigned, so tenant
    /// isolation cannot be crossed by supplying a foreign identifier.
    /// </summary>
    [Fact]
    public async Task AssignUserToRole_RoleBelongsToAnotherTenant_IsReportedAsMissing()
    {
        Harness harness = Harness.Ready();
        Role foreign = TermRole(Frequency.Month, period: 1);
        foreign.PortalId = 3;
        harness.LookupRole = foreign;

        Result outcome = await harness.Service
            .AssignUserToRoleAsync(PortalId, RoleId, Assignment(), CancellationToken.None);

        outcome.IsFailure.Should().BeTrue();
        outcome.Error!.Code.Should().Be(RoleNotFoundCode);
        harness.AddedAssignments.Should().BeEmpty();
    }

    /// <summary>
    /// A null assignment request is refused with an argument fault rather than being treated as an empty
    /// one, because a missing body is a caller defect and not a business outcome.
    /// </summary>
    [Fact]
    public async Task AssignUserToRole_NullRequest_IsRefusedAsAnArgumentFault()
    {
        Harness harness = Harness.Ready();

        await Assert.ThrowsAsync<ArgumentNullException>(
            () => harness.Service.AssignUserToRoleAsync(PortalId, RoleId, null!, CancellationToken.None));
    }

    /// <summary>
    /// A committed assignment invalidates the member's cache entry and records the migrated audit event, so
    /// the legacy trail survives the change of storage mechanism.
    /// </summary>
    /// <remarks>
    /// The legacy screens reached the event log through its controller with the key
    /// <c>USER_ROLE_CREATED</c>, and every record carried the acting account. The log store itself is
    /// outside the migration scope, so the record is emitted through the audit abstraction instead and the
    /// stable event NAME is what carries the audit intent forward.
    /// </remarks>
    [Fact]
    public async Task AssignUserToRole_OnCommit_InvalidatesTheMemberAndRecordsTheAuditEvent()
    {
        Harness harness = Harness.Ready();
        harness.LookupRole = TermRole(Frequency.Month, period: 1);

        Result outcome = await harness.Service
            .AssignUserToRoleAsync(PortalId, RoleId, Assignment(), CancellationToken.None);

        outcome.IsSuccess.Should().BeTrue();
        harness.Cache.Verify(cache => cache.InvalidateUser(PortalId, MemberName), Times.Once);
        harness.Cache.Verify(cache => cache.InvalidatePortal(It.IsAny<int>()), Times.Never);
        harness.Cache.Verify(cache => cache.InvalidateHost(), Times.Never);

        AuditEvent record = Assert.Single(harness.AuditRecords);
        record.EventName.Should().Be(AuditEventNames.UserRoleCreated);
        record.EventName.Should().Be("USER_ROLE_CREATED", "the legacy key is the stable event name");
        record.PortalId.Should().Be(PortalId);
        record.ActorUserId.Should().Be(OperatorUserId);
        record.SubjectUserId.Should().Be(UserId);
        record.ResourceId.Should().Be(RoleId.ToString(CultureInfo.InvariantCulture));
        record.Properties.Should().NotContainKey("RoleName", "stable identifiers replace retained role names");
        record.Properties.Should().ContainKey("Renewed").WhoseValue.Should().Be(false.ToString(CultureInfo.InvariantCulture));
        record.Properties.Should().ContainKey("ExpiryDate")
            .WhoseValue.Should().Be(Now.AddMonths(1).ToString("O", CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// A renewal is recorded under its OWN event name, because the member already held the role and nothing
    /// was granted.
    /// </summary>
    [Fact]
    public async Task AssignUserToRole_Renewal_IsRecordedUnderItsOwnEventNameAndFlagged()
    {
        Harness harness = Harness.Ready();
        harness.LookupRole = TermRole(Frequency.Month, period: 1);
        harness.ExistingAssignment = Membership();

        Result outcome = await harness.Service
            .AssignUserToRoleAsync(PortalId, RoleId, Assignment(), CancellationToken.None);

        outcome.IsSuccess.Should().BeTrue();
        AuditEvent record = Assert.Single(harness.AuditRecords);
        record.EventName.Should().Be(
            AuditEventNames.UserRoleUpdated,
            "the membership already existed, so recording a creation would over-count grants and hide when "
            + "access was first given");
        record.EventName.Should().NotBe(AuditEventNames.UserRoleCreated);
        record.Properties.Should().ContainKey("Renewed")
            .WhoseValue.Should().Be(true.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// Cancelling a PAID membership whose trial has been consumed EXPIRES it by back-dating the bound one
    /// day from TODAY, and does NOT delete the row - so the consumed-trial fact survives.
    /// </summary>
    /// <remarks>
    /// Two substitutions are recorded rather than absorbed.
    /// </remarks>
    [Fact]
    public async Task RemoveUserFromRole_PaidAndTrialConsumed_ExpiresOneDayBeforeTodayInsteadOfDeleting()
    {
        Harness harness = Harness.Ready();
        harness.LookupRole = PaidRole(serviceFee: 9.99m);
        UserRole held = Membership(trialUsed: true, expiryDate: Now.AddDays(60));
        harness.ExistingAssignment = held;

        Result outcome = await harness.Service
            .RemoveUserFromRoleAsync(PortalId, RoleId, UserId, CancellationToken.None);

        outcome.IsSuccess.Should().BeTrue();
        held.ExpiryDate.Should().Be(Now.Date.AddDays(-1));
        held.ExpiryDate.Should().Be(
            new DateTime(2026, 1, 30, 0, 0, 0, DateTimeKind.Utc),
            "the bound is a whole date one day behind today, carrying no time component");
        held.ExpiryDate!.Value.TimeOfDay.Should().Be(TimeSpan.Zero);
        held.IsTrialUsed.Should().BeTrue("the consumed-trial fact is exactly what expiry preserves");
        held.GetStatus(Now).Should().Be(RoleStatus.Expired);

        harness.Roles.Verify(
            repository => repository.DeleteUserRoleAsync(
                It.IsAny<int>(),
                It.IsAny<int>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
        harness.UnitOfWork.Verify(unit => unit.SaveChangesAsync(CancellationToken.None), Times.Once);
    }

    /// <summary>
    /// The expiring removal reports an ADVISORY reason on its successful outcome, which is the only way a
    /// caller learns that the row was expired rather than withdrawn.
    /// </summary>
    [Fact]
    public async Task RemoveUserFromRole_ExpiringArm_CarriesAnAdvisoryReasonOnSuccess()
    {
        Harness harness = Harness.Ready();
        harness.LookupRole = PaidRole(serviceFee: 9.99m);
        harness.ExistingAssignment = Membership(trialUsed: true);

        Result outcome = await harness.Service
            .RemoveUserFromRoleAsync(PortalId, RoleId, UserId, CancellationToken.None);

        outcome.IsSuccess.Should().BeTrue();
        outcome.Error.Should().BeNull("an advisory reason on a success is not a failure");
        outcome.Reason.Should().NotBeNull();
        outcome.Reason!.Code.Should().Be(AssignmentExpiredNotRemovedCode);

        AuditEvent record = Assert.Single(harness.AuditRecords);
        record.EventName.Should().Be(AuditEventNames.UserRoleDeleted);
        record.EventName.Should().Be("USER_ROLE_DELETED");
        record.Properties.Should().ContainKey("Expired")
            .WhoseValue.Should().Be(true.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// Every combination of the fee and consumed-trial conditions OTHER than both together deletes the
    /// membership outright, and reports no advisory reason.
    /// </summary>
    /// <param name="serviceFee">The role's fee, or null when the role declares none.</param>
    /// <param name="trialUsed">Whether the membership records a consumed trial, or null when unrecorded.</param>
    [Theory]
    [InlineData(9.99, false)]
    [InlineData(null, true)]
    [InlineData(null, false)]
    [InlineData(0.0, true)]
    [InlineData(0.0, false)]
    [InlineData(9.99, null)]
    public async Task RemoveUserFromRole_AnyOtherFeeAndTrialCombination_DeletesTheMembership(
        double? serviceFee,
        bool? trialUsed)
    {
        Harness harness = Harness.Ready();
        harness.LookupRole = PaidRole(serviceFee is double fee ? (decimal)fee : null);
        UserRole held = Membership(trialUsed: trialUsed, expiryDate: Now.AddDays(60));
        harness.ExistingAssignment = held;

        Result outcome = await harness.Service
            .RemoveUserFromRoleAsync(PortalId, RoleId, UserId, CancellationToken.None);

        outcome.IsSuccess.Should().BeTrue();
        outcome.Reason.Should().BeNull("a withdrawn membership needs no advisory reason");
        held.ExpiryDate.Should().Be(Now.AddDays(60), "the deleting arm does not touch the bound");

        (int RemovedUserId, int RemovedRoleId) key = Assert.Single(harness.RemovedAssignmentKeys);
        key.RemovedUserId.Should().Be(UserId);
        key.RemovedRoleId.Should().Be(RoleId);
        harness.UnitOfWork.Verify(unit => unit.SaveChangesAsync(CancellationToken.None), Times.Once);

        AuditEvent record = Assert.Single(harness.AuditRecords);
        record.Properties.Should().ContainKey("Expired")
            .WhoseValue.Should().Be(false.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// A membership the member does not hold is reported as missing, and neither arm of the removal runs.
    /// </summary>
    /// <remarks>
    /// MIGRATION: <c>RoleController.vb</c> L494 tested the read membership for null as the FIRST of its
    /// three conditions and fell to the deleting arm when it was absent, which asked the store to remove a
    /// row that was not there and reported nothing either way. The migrated member answers a named reason
    /// instead, so an absent membership is distinguishable from a withdrawn one.
    /// </remarks>
    [Fact]
    public async Task RemoveUserFromRole_MembershipAbsent_IsReportedAsAReasonAndCommitsNothing()
    {
        Harness harness = Harness.Ready();
        harness.LookupRole = PaidRole(serviceFee: 9.99m);
        harness.ExistingAssignment = null;

        Result outcome = await harness.Service
            .RemoveUserFromRoleAsync(PortalId, RoleId, UserId, CancellationToken.None);

        outcome.IsFailure.Should().BeTrue();
        outcome.Error!.Code.Should().Be(AssignmentNotFoundCode);
        harness.RemovedAssignmentKeys.Should().BeEmpty();
        harness.UnitOfWork.Verify(
            unit => unit.SaveChangesAsync(It.IsAny<CancellationToken>()),
            Times.Never);
        harness.AuditRecords.Should().BeEmpty();
    }

    /// <summary>
    /// Submitted bounds are DISCARDED, not honoured, for the one pairing the legacy screen protected: the
    /// portal's designated administrator holding the portal's designated administrators role.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    /// <remarks>
    /// Enforcing it became NECESSARY, not merely faithful, once a submitted bound was honoured. While the
    /// derivation silently rewrote every submitted bound, this pairing was protected by accident - a
    /// portal's administrators role carries no term, so a submitted expiry was discarded on its way
    /// through.
    /// </remarks>
    [Fact]
    public async Task AssignUserToRole_PortalAdministratorToTheAdministratorRole_DiscardsSubmittedBounds()
    {
        Harness harness = Harness.Ready();

        var administratorRole = new Role
        {
            RoleId = AdministratorRoleId,
            PortalId = PortalId,
            RoleName = "Administrators",
            ServiceFee = 0m,
            BillingFrequency = Frequency.Month,
            BillingPeriod = 0,
            TrialFrequency = Frequency.None,
            TrialPeriod = 0,
        };

        harness.LookupRole = administratorRole;
        harness.Member = Member(AdministratorUserId);

        Result outcome = await harness.Service.AssignUserToRoleAsync(
            PortalId,
            AdministratorRoleId,
            new RoleAssignmentRequest
            {
                UserId = AdministratorUserId,
                EffectiveDate = Now.AddDays(3),
                ExpiryDate = Now.AddDays(30),
            },
            CancellationToken.None);

        outcome.IsSuccess.Should().BeTrue(outcome.Error?.ToString());
        UserRole stored = Assert.Single(harness.AddedAssignments);
        stored.EffectiveDate.Should().BeNull("the administrator's own membership carries no start gate");
        stored.ExpiryDate.Should().BeNull("and no expiry, or the tenant would lose its administrator");
    }

    /// <summary>
    /// The discard is confined to that ONE pairing: the same account submitting the same bounds for a
    /// DIFFERENT role has them stored verbatim, and so does a different account in the administrators role.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task AssignUserToRole_ProtectedBoundsApplyOnlyToThatOnePairing()
    {
        DateTime ends = Now.AddDays(30);

        Harness sameAccountOtherRole = Harness.Ready();
        sameAccountOtherRole.LookupRole = TermRole(Frequency.Month, period: 1);
        sameAccountOtherRole.Member = Member(AdministratorUserId);

        Result otherRole = await sameAccountOtherRole.Service.AssignUserToRoleAsync(
            PortalId,
            RoleId,
            new RoleAssignmentRequest { UserId = AdministratorUserId, ExpiryDate = ends },
            CancellationToken.None);

        otherRole.IsSuccess.Should().BeTrue(otherRole.Error?.ToString());
        Assert.Single(sameAccountOtherRole.AddedAssignments).ExpiryDate.Should().Be(
            ends,
            "the administrator's membership of an ordinary role is bounded like anyone else's");

        Harness otherAccountAdminRole = Harness.Ready();
        Role administratorRole = PaidRole(serviceFee: null);
        administratorRole.RoleId = AdministratorRoleId;
        otherAccountAdminRole.LookupRole = administratorRole;

        Result otherAccount = await otherAccountAdminRole.Service.AssignUserToRoleAsync(
            PortalId,
            AdministratorRoleId,
            new RoleAssignmentRequest { UserId = UserId, ExpiryDate = ends },
            CancellationToken.None);

        otherAccount.IsSuccess.Should().BeTrue(otherAccount.Error?.ToString());
        Assert.Single(otherAccountAdminRole.AddedAssignments).ExpiryDate.Should().Be(
            ends,
            "a co-administrator's membership may legitimately be time-limited");
    }

    /// <summary>
    /// Removing the portal's DESIGNATED ADMINISTRATOR from that portal's ADMINISTRATOR ROLE is refused.
    /// </summary>
    [Fact]
    public async Task RemoveUserFromRole_PortalAdministratorFromTheAdministratorRole_IsRefused()
    {
        Harness harness = Harness.Ready();
        Role administratorRole = PaidRole(serviceFee: null);
        administratorRole.RoleId = AdministratorRoleId;
        harness.LookupRole = administratorRole;
        harness.Member = Member(AdministratorUserId);
        harness.ExistingAssignment = Membership(userId: AdministratorUserId, roleId: AdministratorRoleId);

        Result outcome = await harness.Service.RemoveUserFromRoleAsync(
            PortalId,
            AdministratorRoleId,
            AdministratorUserId,
            CancellationToken.None);

        outcome.IsFailure.Should().BeTrue();
        outcome.Error!.Code.Should().Be(AssignmentProtectedCode);
        harness.RemovedAssignmentKeys.Should().BeEmpty();
        harness.UnitOfWork.Verify(
            unit => unit.SaveChangesAsync(It.IsAny<CancellationToken>()),
            Times.Never);
    }

    /// <summary>
    /// Removing ANY member at all from the portal's REGISTERED-MEMBERS role is refused, whoever the member
    /// is.
    /// </summary>
    /// <remarks>
    /// The second of the two protected cases. It is unconditional in the member's identity, which is why it
    /// is asserted with an ordinary member rather than with the administrator - a transcription that
    /// accidentally conjoined the two rules would still pass the administrator case and fail here.
    /// </remarks>
    [Fact]
    public async Task RemoveUserFromRole_AnyMemberFromTheRegisteredRole_IsRefused()
    {
        Harness harness = Harness.Ready();
        Role registeredRole = PaidRole(serviceFee: null);
        registeredRole.RoleId = RegisteredRoleId;
        harness.LookupRole = registeredRole;
        harness.ExistingAssignment = Membership(roleId: RegisteredRoleId);

        Result outcome = await harness.Service
            .RemoveUserFromRoleAsync(PortalId, RegisteredRoleId, UserId, CancellationToken.None);

        outcome.IsFailure.Should().BeTrue();
        outcome.Error!.Code.Should().Be(AssignmentProtectedCode);
        harness.RemovedAssignmentKeys.Should().BeEmpty();
    }

    /// <summary>
    /// The protected rule refuses only the exact conjunction it names: the designated administrator may be
    /// removed from an ORDINARY role, and an ordinary member may be removed from the ADMINISTRATOR role.
    /// </summary>
    /// <remarks>
    /// The negative half of the protected rule. The refusal is a conjunction of the account AND the role
    /// for the administrator case, so both near-misses must succeed or the rule has been widened beyond
    /// what the legacy code refused - which would lock administrators of a portal into roles they should be
    /// able to leave.
    /// </remarks>
    [Fact]
    public async Task RemoveUserFromRole_NeitherProtectedConjunction_IsAllowed()
    {
        Harness administratorLeavingOrdinaryRole = Harness.Ready();
        administratorLeavingOrdinaryRole.LookupRole = PaidRole(serviceFee: null);
        administratorLeavingOrdinaryRole.Member = Member(AdministratorUserId);
        administratorLeavingOrdinaryRole.ExistingAssignment = Membership(userId: AdministratorUserId);

        Result administratorOutcome = await administratorLeavingOrdinaryRole.Service
            .RemoveUserFromRoleAsync(PortalId, RoleId, AdministratorUserId, CancellationToken.None);

        administratorOutcome.IsSuccess.Should().BeTrue("an administrator may leave an ordinary role");

        Harness memberLeavingAdministratorRole = Harness.Ready();
        Role administratorRole = PaidRole(serviceFee: null);
        administratorRole.RoleId = AdministratorRoleId;
        memberLeavingAdministratorRole.LookupRole = administratorRole;
        memberLeavingAdministratorRole.ExistingAssignment = Membership(roleId: AdministratorRoleId);

        Result memberOutcome = await memberLeavingAdministratorRole.Service
            .RemoveUserFromRoleAsync(PortalId, AdministratorRoleId, UserId, CancellationToken.None);

        memberOutcome.IsSuccess.Should().BeTrue(
            "an ordinary member may be removed from the administrator role");
    }

    /// <summary>
    /// Each expected removal failure is reported as a named reason, in the order the service resolves the
    /// facts, and nothing is committed.
    /// </summary>
    /// <param name="portalResolves">Whether the tenant row resolves.</param>
    /// <param name="roleResolves">Whether the role resolves within that tenant.</param>
    /// <param name="memberResolves">Whether the account resolves within that tenant.</param>
    /// <param name="expectedCode">The reason code the outcome must carry.</param>
    /// <remarks>
    /// The removal path READS the tenant row rather than merely probing for it, because it needs the two
    /// protected identifiers to decide whether the removal is permitted at all. That asymmetry with the
    /// assignment path is deliberate and is pinned by the portal case below.
    /// </remarks>
    [Theory]
    [InlineData(false, true, true, PortalNotFoundCode)]
    [InlineData(true, false, true, RoleNotFoundCode)]
    [InlineData(true, true, false, UserNotFoundCode)]
    public async Task RemoveUserFromRole_ExpectedFailure_IsReportedAsAReasonAndCommitsNothing(
        bool portalResolves,
        bool roleResolves,
        bool memberResolves,
        string expectedCode)
    {
        Harness harness = Harness.Ready();
        if (!portalResolves)
        {
            harness.PortalRow = null;
        }

        harness.LookupRole = roleResolves ? PaidRole(serviceFee: 9.99m) : null;
        harness.Member = memberResolves ? Member() : null;
        harness.ExistingAssignment = Membership(trialUsed: true);

        Result outcome = await harness.Service
            .RemoveUserFromRoleAsync(PortalId, RoleId, UserId, CancellationToken.None);

        outcome.IsFailure.Should().BeTrue();
        outcome.Error!.Code.Should().Be(expectedCode);
        outcome.Error.Message.Should().NotBeNullOrWhiteSpace();
        harness.RemovedAssignmentKeys.Should().BeEmpty();
        harness.UnitOfWork.Verify(
            unit => unit.SaveChangesAsync(It.IsAny<CancellationToken>()),
            Times.Never);
        harness.AuditRecords.Should().BeEmpty();
    }

    /// <summary>
    /// A committed removal invalidates the member's cache entry explicitly, on BOTH the expiring and the
    /// deleting arm.
    /// </summary>
    [Fact]
    public async Task RemoveUserFromRole_OnCommit_InvalidatesOnlyTheAffectedMember()
    {
        Harness expiring = Harness.Ready();
        expiring.LookupRole = PaidRole(serviceFee: 9.99m);
        expiring.ExistingAssignment = Membership(trialUsed: true);

        Result expiringOutcome = await expiring.Service
            .RemoveUserFromRoleAsync(PortalId, RoleId, UserId, CancellationToken.None);

        expiringOutcome.IsSuccess.Should().BeTrue();
        expiring.Cache.Verify(cache => cache.InvalidateUser(PortalId, MemberName), Times.Once);
        expiring.Cache.Verify(cache => cache.InvalidatePortal(It.IsAny<int>()), Times.Never);
        expiring.Cache.Verify(cache => cache.InvalidateHost(), Times.Never);

        Harness deleting = Harness.Ready();
        deleting.LookupRole = PaidRole(serviceFee: null);
        deleting.ExistingAssignment = Membership();

        Result deletingOutcome = await deleting.Service
            .RemoveUserFromRoleAsync(PortalId, RoleId, UserId, CancellationToken.None);

        deletingOutcome.IsSuccess.Should().BeTrue();
        deleting.Cache.Verify(cache => cache.InvalidateUser(PortalId, MemberName), Times.Once);
        deleting.Cache.Verify(cache => cache.InvalidateHost(), Times.Never);
    }

    /// <summary>
    /// Both write paths accept the legitimate identifier SEEDS - a portal identifier of minus one and a
    /// role identifier of zero - without mistaking either for an unset value.
    /// </summary>
    /// <remarks>
    /// The two seeds are the sharpest sentinel collision in the whole migration. <c>Portals.PortalID</c> is
    /// seeded from MINUS ONE, which is exactly the legacy absent-integer sentinel, and <c>Roles.RoleID</c>
    /// is seeded from ZERO, which is the CLR default for its type. A migrated layer that read either as
    /// "absent" would refuse the very first portal and the very first role an installation ever created.
    /// </remarks>
    [Fact]
    public async Task BothWritePaths_AcceptTheLegitimateIdentifierSeeds()
    {
        PortalId.Should().Be(-1, "the portal column is seeded from minus one");
        RoleId.Should().Be(0, "the role column is seeded from zero");

        Harness assigning = Harness.Ready();
        assigning.LookupRole = TermRole(Frequency.Month, period: 1);

        Result assigned = await assigning.Service
            .AssignUserToRoleAsync(PortalId, RoleId, Assignment(), CancellationToken.None);

        assigned.IsSuccess.Should().BeTrue();
        Assert.Single(assigning.AddedAssignments).RoleId.Should().Be(RoleId);

        Harness removing = Harness.Ready();
        removing.LookupRole = PaidRole(serviceFee: null);
        removing.ExistingAssignment = Membership();

        Result removed = await removing.Service
            .RemoveUserFromRoleAsync(PortalId, RoleId, UserId, CancellationToken.None);

        removed.IsSuccess.Should().BeTrue();
        Assert.Single(removing.RemovedAssignmentKeys).RemovedRoleId.Should().Be(RoleId);
    }

    /// <summary>
    /// The role listing answers a TYPED, PAGED envelope rather than the legacy untyped collection, and the
    /// paid-membership columns survive the projection.
    /// </summary>
    [Fact]
    public async Task ListRoles_AnswersATypedPagedEnvelopeCarryingThePaidMembershipColumns()
    {
        Harness harness = Harness.Ready();
        harness.PortalRoles = [TrialThenMonthlyRole()];

        Result<PagedResult<RoleListItemDto>> outcome = await harness.Service.ListRolesAsync(
            PortalId,
            new PagedRequest { PageIndex = 0, PageSize = 10, SortBy = "RoleName" },
            roleGroupId: null,
            RoleGroupScope.All,
            CancellationToken.None);

        outcome.IsSuccess.Should().BeTrue();
        PagedResult<RoleListItemDto> page = outcome.Value;
        page.Should().BeOfType<PagedResult<RoleListItemDto>>();
        page.Items.Should().BeAssignableTo<IReadOnlyList<RoleListItemDto>>();
        page.TotalCount.Should().Be(1);
        page.PageIndex.Should().Be(0);
        page.PageSize.Should().Be(10);
        page.HasPreviousPage.Should().BeFalse();
        page.HasNextPage.Should().BeFalse();

        RoleListItemDto row = Assert.Single(page.Items);
        row.RoleId.Should().Be(RoleId);
        row.RoleName.Should().Be(RoleName);
        row.ServiceFee.Should().Be(9.99m);
        row.BillingPeriod.Should().Be(1);
        row.BillingFrequency.Should().Be(Frequency.Month);
        row.TrialPeriod.Should().Be(14);
        row.TrialFrequency.Should().Be(Frequency.Day);
    }

    /// <summary>
    /// A failed listing exposes no value at all, so a caller that skipped its own success check is stopped
    /// rather than handed a default envelope.
    /// </summary>
    [Fact]
    public async Task ListRoles_FailedOutcome_ExposesNoValue()
    {
        Harness harness = Harness.Ready();
        harness.PortalExists = false;

        Result<PagedResult<RoleListItemDto>> outcome = await harness.Service.ListRolesAsync(
            PortalId,
            new PagedRequest { PageIndex = 0, PageSize = 10, SortBy = "RoleName" },
            roleGroupId: null,
            RoleGroupScope.All,
            CancellationToken.None);

        outcome.IsFailure.Should().BeTrue();
        outcome.Error!.Code.Should().Be(PortalNotFoundCode);
        Assert.Throws<InvalidOperationException>(() => _ = outcome.Value);
    }

    /// <summary>
    /// A member's role set is answered as a typed read-only list rather than as the legacy string array.
    /// </summary>
    [Fact]
    public async Task ListUserRoles_AnswersATypedReadOnlyList()
    {
        Harness harness = Harness.Ready();
        harness.PortalRoles = [TrialThenMonthlyRole()];
        harness.UserAssignments = [Membership()];

        Result<IReadOnlyList<RoleListItemDto>> outcome = await harness.Service
            .ListUserRolesAsync(PortalId, UserId, CancellationToken.None);

        outcome.IsSuccess.Should().BeTrue();
        outcome.Value.Should().BeAssignableTo<IReadOnlyList<RoleListItemDto>>();
        RoleListItemDto row = Assert.Single(outcome.Value);
        row.RoleId.Should().Be(RoleId);
        row.TrialFrequency.Should().Be(Frequency.Day);
    }

    /// <summary>
    /// A member holding no roles is answered with an EMPTY typed list and a success, because absent is not
    /// the same answer as failed.
    /// </summary>
    [Fact]
    public async Task ListUserRoles_MemberHoldsNoRoles_AnswersAnEmptyListAndSucceeds()
    {
        Harness harness = Harness.Ready();
        harness.UserAssignments = [];

        Result<IReadOnlyList<RoleListItemDto>> outcome = await harness.Service
            .ListUserRolesAsync(PortalId, UserId, CancellationToken.None);

        outcome.IsSuccess.Should().BeTrue();
        Assert.Empty(outcome.Value);
    }

    /// <summary>
    /// Builds a paid role whose BILLING term is the supplied frequency and period and which declares no
    /// trial, so a derivation over it exercises exactly one branch of the trial-versus-billing selection.
    /// </summary>
    /// <param name="frequency">The billing frequency the role declares.</param>
    /// <param name="period">The billing period the role declares.</param>
    /// <returns>A role belonging to the tenant under test.</returns>
    private static Role TermRole(Frequency frequency, int period) => new()
    {
        RoleId = RoleId,
        PortalId = PortalId,
        RoleName = RoleName,
        ServiceFee = 9.99m,
        BillingFrequency = frequency,
        BillingPeriod = period,
        TrialFrequency = null,
        TrialPeriod = null,
        IsPublic = true,
    };

    /// <summary>
    /// Builds a role offering a fourteen-day trial ahead of a one-month billing term, deliberately chosen
    /// so the two terms resolve to visibly different dates.
    /// </summary>
    /// <returns>A role carrying both a trial term and a billing term.</returns>
    private static Role TrialThenMonthlyRole() => new()
    {
        RoleId = RoleId,
        PortalId = PortalId,
        RoleName = RoleName,
        ServiceFee = 9.99m,
        BillingFrequency = Frequency.Month,
        BillingPeriod = 1,
        TrialFee = 0m,
        TrialFrequency = Frequency.Day,
        TrialPeriod = 14,
        IsPublic = true,
    };

    /// <summary>
    /// Builds a role carrying the supplied fee and NO membership term, for the removal path - which
    /// consults the fee and never the term.
    /// </summary>
    /// <param name="serviceFee">The fee the role charges, or null when it declares none.</param>
    /// <returns>A role belonging to the tenant under test.</returns>
    private static Role PaidRole(decimal? serviceFee) => new()
    {
        RoleId = RoleId,
        PortalId = PortalId,
        RoleName = RoleName,
        ServiceFee = serviceFee,
        BillingFrequency = null,
        BillingPeriod = null,
        TrialFrequency = null,
        TrialPeriod = null,
    };

    /// <summary>Builds a member of the tenant under test.</summary>
    /// <param name="userId">The account identifier to carry.</param>
    /// <returns>An account satisfying the columns the store declares as required.</returns>
    private static User Member(int userId = UserId) => new()
    {
        UserId = userId,
        Username = MemberName,
        FirstName = "Ada",
        LastName = "Lovelace",
        DisplayName = "Ada Lovelace",
        Email = "ada@example.com",
        IsApproved = true,
    };

    /// <summary>Builds one stored membership.</summary>
    /// <param name="userId">The account the membership belongs to.</param>
    /// <param name="roleId">The role the membership names.</param>
    /// <param name="trialUsed">Whether the trial has been consumed.</param>
    /// <param name="effectiveDate">When the membership takes effect, or null for no start bound.</param>
    /// <param name="expiryDate">When it lapses, or null for an unbounded membership.</param>
    /// <returns>A stored membership row.</returns>
    private static UserRole Membership(
        int userId = UserId,
        int roleId = RoleId,
        bool? trialUsed = null,
        DateTime? effectiveDate = null,
        DateTime? expiryDate = null) => new()
        {
            // The surrogate key is deliberately non-zero and unrelated to either identifier, so a test that
            // asserts the key survived a renewal cannot pass by coincidence.
            UserRoleId = 1000 + userId,
            UserId = userId,
            RoleId = roleId,
            IsTrialUsed = trialUsed,
            EffectiveDate = effectiveDate,
            ExpiryDate = expiryDate,
            User = Member(userId),
        };

    /// <summary>Builds an assignment request for the member under test.</summary>
    /// <param name="effectiveDate">The submitted start bound, or null to submit none.</param>
    /// <param name="expiryDate">The submitted end bound, or null to let the role's terms derive one.</param>
    /// <returns>A well-formed assignment request.</returns>
    /// <remarks>
    /// Both bounds are nullable on the request contract, which is what replaced the legacy non-nullable
    /// date parameters that could express absence only through a sentinel. The notification switch the
    /// legacy shared member carried is not reproduced and is therefore left at its default.
    /// </remarks>
    private static RoleAssignmentRequest Assignment(
        DateTime? effectiveDate = null,
        DateTime? expiryDate = null) => new()
        {
            UserId = UserId,
            EffectiveDate = effectiveDate,
            ExpiryDate = expiryDate,
        };

    /// <summary>
    /// Assembles the service over eight recording doubles and a FROZEN clock, exposing every answer the
    /// service can receive as mutable state so a test describes its world declaratively.
    /// </summary>
    private sealed class Harness
    {
        private Harness()
        {
            PortalRow = new Portal
            {
                PortalId = PortalId,
                PortalName = "Measured Portal",
                DefaultLanguage = "en-US",
                HomeDirectory = "Portals/0",
                AdministratorId = AdministratorUserId,
                AdministratorRoleId = AdministratorRoleId,
                RegisteredRoleId = RegisteredRoleId,
            };

            PortalExists = true;
            Member = RoleServiceApplicationTests.Member();
            LookupRole = TermRole(Frequency.Month, period: 1);
            ExistingAssignment = null;
            PortalRoles = [];
            UserAssignments = [];
            AddedAssignments = [];
            UpdatedAssignments = [];
            RemovedAssignmentKeys = [];
            AuditRecords = [];

            Roles = new Mock<IRoleRepository>(MockBehavior.Loose);
            Portals = new Mock<IPortalRepository>(MockBehavior.Loose);
            Users = new Mock<IUserRepository>(MockBehavior.Loose);
            Permissions = new Mock<IPermissionService>(MockBehavior.Loose);
            UnitOfWork = new Mock<IUnitOfWork>(MockBehavior.Loose);
            Clock = new Mock<IClock>(MockBehavior.Loose);
            Cache = new Mock<ICacheService>(MockBehavior.Loose);
            CurrentUser = new Mock<ICurrentUser>(MockBehavior.Loose);
            Audit = new Mock<IAuditSink>(MockBehavior.Loose);

            // The operator is fixed so an audit assertion can name the actor it expects rather than
            // accepting whatever a loose double happens to answer.
            CurrentUser.SetupGet(caller => caller.UserId).Returns(OperatorUserId);
            CurrentUser.SetupGet(caller => caller.UserName).Returns(OperatorUserName);

            Service = new RoleService(
                Roles.Object,
                Portals.Object,
                Users.Object,
                Permissions.Object,
                UnitOfWork.Object,
                Clock.Object,
                Cache.Object,
                CurrentUser.Object,
                Audit.Object);
        }

        /// <summary>Gets the real service under test.</summary>
        public RoleService Service { get; }

        /// <summary>Gets the role, group and assignment repository double.</summary>
        public Mock<IRoleRepository> Roles { get; }

        /// <summary>Gets the portal repository double.</summary>
        public Mock<IPortalRepository> Portals { get; }

        /// <summary>Gets the account repository double.</summary>
        public Mock<IUserRepository> Users { get; }

        /// <summary>Gets the permission contract double, which owns the removal of a role's grants.</summary>
        public Mock<IPermissionService> Permissions { get; }

        /// <summary>Gets the unit-of-work double, so commits can be counted.</summary>
        public Mock<IUnitOfWork> UnitOfWork { get; }

        /// <summary>Gets the frozen clock double.</summary>
        public Mock<IClock> Clock { get; }

        /// <summary>Gets the cache double, so evictions can be counted.</summary>
        public Mock<ICacheService> Cache { get; }

        /// <summary>Gets the acting-operator double.</summary>
        public Mock<ICurrentUser> CurrentUser { get; }

        /// <summary>Gets the audit double.</summary>
        public Mock<IAuditSink> Audit { get; }

        /// <summary>Gets every audit record the service emitted, in emission order.</summary>
        public List<AuditEvent> AuditRecords { get; }

        /// <summary>Gets or sets the tenant row the removal path reads, or null when it does not resolve.</summary>
        public Portal? PortalRow { get; set; }

        /// <summary>Gets or sets whether the tenant probe the assignment path uses answers true.</summary>
        public bool PortalExists { get; set; }

        /// <summary>Gets or sets the role the store answers, or null when none resolves.</summary>
        public Role? LookupRole { get; set; }

        /// <summary>Gets or sets the stored membership, or null when the member holds no such role.</summary>
        public UserRole? ExistingAssignment { get; set; }

        /// <summary>Gets or sets the account the store answers, or null when none resolves.</summary>
        public User? Member { get; set; }

        /// <summary>Gets or sets every role the tenant owns, as the listing reads them.</summary>
        public IReadOnlyList<Role> PortalRoles { get; set; }

        /// <summary>Gets or sets the memberships one account holds.</summary>
        public IReadOnlyList<UserRole> UserAssignments { get; set; }

        /// <summary>Gets the memberships staged for insertion.</summary>
        public List<UserRole> AddedAssignments { get; }

        /// <summary>Gets the memberships staged through the explicit update member.</summary>
        public List<UserRole> UpdatedAssignments { get; }

        /// <summary>Gets the account-and-role keys whose memberships were staged for deletion.</summary>
        public List<(int RemovedUserId, int RemovedRoleId)> RemovedAssignmentKeys { get; }

        /// <summary>
        /// Builds a harness whose world is internally consistent: the tenant exists, the role exists and
        /// belongs to it, the account exists, the member holds no membership yet, and the clock is frozen.
        /// </summary>
        /// <returns>A wired harness.</returns>
        public static Harness Ready()
        {
            var harness = new Harness();

            harness.Portals
                .Setup(repository => repository.ExistsAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => harness.PortalExists);
            // /The READ is gated by the same existence flag as the PROBE, so the harness describes ONE
            // world.
            harness.Portals
                .Setup(repository => repository.GetByIdAsync(
                    It.IsAny<int>(),
                    It.IsAny<bool>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => harness.PortalExists ? harness.PortalRow : null);

            // The portal is a CONDITION of the role read rather than a hint, exactly as the terminal
            // procedure's own predicate made it, so a role planted against another tenant is withheld and a
            // tenant-isolation test exercises a genuine refusal.
            harness.Roles
                .Setup(repository => repository.GetByIdAsync(
                    It.IsAny<int>(),
                    It.IsAny<int>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync((int roleId, int portalId, CancellationToken _) =>
                    harness.LookupRole is { } candidate && candidate.PortalId == portalId ? candidate : null);

            harness.Roles
                .Setup(repository => repository.GetUserRoleAsync(
                    It.IsAny<int>(),
                    It.IsAny<int>(),
                    It.IsAny<int>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => harness.ExistingAssignment);

            harness.Roles
                .Setup(repository => repository.GetByPortalIdAsync(
                    It.IsAny<int>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => harness.PortalRoles);

            // The role listing's page, served by the store. Faked over the same role world the unpaged stub
            // above serves, including the STRICT tenant predicate that is the difference between the two
            // reads, so the listing facts still describe the listing rather than a canned answer.
            harness.Roles
                .Setup(repository => repository.ListAsync(
                    It.IsAny<int>(),
                    It.IsAny<int?>(),
                    It.IsAny<bool>(),
                    It.IsAny<string?>(),
                    It.IsAny<string?>(),
                    It.IsAny<bool>(),
                    It.IsAny<int>(),
                    It.IsAny<int>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync((
                    int portalId,
                    int? roleGroupId,
                    bool ungroupedOnly,
                    string? nameQuery,
                    string? sortBy,
                    bool descending,
                    int pageIndex,
                    int pageSize,
                    CancellationToken _) =>
                {
                    IEnumerable<Role> matching = harness.PortalRoles.Where(role => role.PortalId == portalId);

                    if (roleGroupId is int wantedGroup)
                    {
                        matching = matching.Where(role => role.RoleGroupId == wantedGroup);
                    }
                    else if (ungroupedOnly)
                    {
                        matching = matching.Where(role => role.RoleGroupId is null);
                    }

                    if (!string.IsNullOrWhiteSpace(nameQuery))
                    {
                        string wanted = nameQuery.Trim();
                        matching = matching.Where(role =>
                            role.RoleName.Contains(wanted, StringComparison.OrdinalIgnoreCase));
                    }

                    List<Role> rows = (descending
                            ? matching.OrderByDescending(role => role.RoleName, StringComparer.OrdinalIgnoreCase)
                                .ThenByDescending(role => role.RoleId)
                            : matching.OrderBy(role => role.RoleName, StringComparer.OrdinalIgnoreCase)
                                .ThenBy(role => role.RoleId))
                        .ToList();

                    if (pageSize == 0)
                    {
                        return PagedResult<Role>.Unpaged(rows);
                    }

                    return PagedResult<Role>.Create(
                        rows.Skip(Paging.SkipCount(pageIndex, pageSize)).Take(pageSize).ToList(),
                        rows.Count,
                        pageIndex,
                        pageSize);
                });

            harness.Roles
                .Setup(repository => repository.GetUserRolesAsync(
                    It.IsAny<int>(),
                    It.IsAny<int>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => harness.UserAssignments);

            harness.Roles
                .Setup(repository => repository.AddUserRoleAsync(
                    It.IsAny<UserRole>(),
                    It.IsAny<CancellationToken>()))
                .Callback<UserRole, CancellationToken>((staged, _) => harness.AddedAssignments.Add(staged))
                .Returns(Task.CompletedTask);

            harness.Roles
                .Setup(repository => repository.UpdateUserRoleAsync(
                    It.IsAny<UserRole>(),
                    It.IsAny<CancellationToken>()))
                .Callback<UserRole, CancellationToken>((staged, _) => harness.UpdatedAssignments.Add(staged))
                .Returns(Task.CompletedTask);

            harness.Roles
                .Setup(repository => repository.DeleteUserRoleAsync(
                    It.IsAny<int>(),
                    It.IsAny<int>(),
                    It.IsAny<CancellationToken>()))
                .Callback<int, int, CancellationToken>(
                    (userId, roleId, _) => harness.RemovedAssignmentKeys.Add((userId, roleId)))
                .Returns(Task.CompletedTask);

            harness.Users
                .Setup(repository => repository.GetAsync(
                    It.IsAny<int?>(),
                    It.IsAny<int>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => harness.Member);

            harness.UnitOfWork
                .Setup(unit => unit.SaveChangesAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(1);

            harness.Audit
                .Setup(sink => sink.Record(It.IsAny<AuditEvent>()))
                .Callback<AuditEvent>(harness.AuditRecords.Add);

            harness.Clock.SetupGet(clock => clock.UtcNow).Returns(Now);

            return harness;
        }
    }
}
