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
/// Pins the paid-membership term engine of <see cref="RoleService"/> - the single most intricate piece
/// of date arithmetic in the migration - together with the assignment and removal rules that surround
/// it, against recording doubles for every collaborator and a frozen clock.
/// </summary>
/// <remarks>
/// <para>
/// WHY THIS SUITE EXISTS ALONGSIDE THE BROADER ROLE SUITE. AAP 0.4.1.1 places a role-service suite in
/// the <c>Application</c> folder and AAP 0.5.1.5 states its charter precisely: mocked repositories,
/// asserting the outcome reasons that replaced the legacy by-reference status arguments. This file
/// discharges that charter by concentrating on ONE thing the legacy code did and did badly - deriving
/// when a role membership starts and stops - and it asserts each rule of that derivation separately, so
/// a failure names the broken rule rather than merely reporting that assignment is broken.
/// </para>
/// <para>
/// THE LEGACY SHAPE, AND WHY IT SPLIT IN TWO. <c>RoleController.vb</c> L489-L557 is one procedure with a
/// boolean switch: passing the cancel flag expired or deleted a membership, and omitting it derived and
/// wrote the dates. Those are two operations wearing one name, and the migrated contract separates them
/// into <see cref="RoleService.AssignUserToRoleAsync"/> (the L503-L555 arm) and
/// <see cref="RoleService.RemoveUserFromRoleAsync"/> (the L493-L501 arm). Both arms are covered here,
/// and each is measured against the legacy line it reproduces.
/// </para>
/// <para>
/// THE CLOCK IS INJECTED, ALWAYS. The legacy engine read the ambient machine clock FOUR separate times
/// within one derivation - the expiry seed at L505, the effective-date comparison at L530, and the
/// expiry comparison and reassignment at L533-L534 - so a request that crossed a tick between two of
/// them could clear one bound against one instant and seed the other from a different one. The migrated
/// engine reads <see cref="IClock.UtcNow"/> exactly once. Every test below freezes that reading at
/// <see cref="Now"/> and asserts against it, so nothing here can flake at a midnight, month or year
/// boundary. No test in this file reads a machine clock, and that absence is deliberate rather than
/// incidental: a date-arithmetic suite that consulted the real clock would be untrustworthy precisely
/// when it mattered.
/// </para>
/// <para>
/// SENTINELS ARE HONOURED AT THE BOUNDARY, NEVER IN THE MODEL (AAP Rule T7). Three legacy sentinels are
/// load-bearing here and each is asserted: the absent-integer marker of minus one, which is NOT the
/// migrated absence marker and is proved not to be; the absent-date marker of the minimum date value,
/// which the request boundary reads as absence; and the empty string that the legacy reader produced for
/// a null character column, whose migrated counterpart is a null enumeration value and whose handling is
/// a measured behavioural difference recorded below. Two identifier seeds are equally load-bearing: a
/// role identifier of zero is legitimate because the column is seeded from zero, and a portal identifier
/// of minus one is legitimate because that column is seeded from minus one - so neither may be mistaken
/// for an unset value.
/// </para>
/// <para>
/// SCOPE DISCIPLINE. Nothing here duplicates a sibling suite. The monetary floor lives with the portal
/// service, automatic enrolment on account creation lives with the account service, permission
/// evaluation lives with the security suites, rule-for-rule validator parity lives with the validation
/// suites, projection round-trips live with the mapping suite, and anything needing a database lives in
/// the other test project. The eight obsolete delegating members retained for binary compatibility at
/// <c>RoleController.vb</c> L845-L888 produce no migrated surface, so no test here targets one; the
/// canonical members they forwarded to are tested instead.
/// </para>
/// </remarks>
public class RoleServiceApplicationTests
{
    /// <summary>
    /// The tenant under test. Minus one is a REAL portal identifier, not an absence marker: the column is
    /// declared <c>IDENTITY (-1, 1)</c> in the baseline schema, so the first portal an installation ever
    /// creates bears it, and it collides exactly with the legacy absent-integer sentinel. Using it here
    /// means every tenant-scoped assertion below is made against the value most likely to be mishandled.
    /// </summary>
    private const int PortalId = -1;

    /// <summary>
    /// The role under test. Zero is a REAL role identifier: <c>Roles.RoleID</c> is declared
    /// <c>IDENTITY (0, 1)</c>, so the first role bears zero and it must never be read as unset merely
    /// because it equals the CLR default for its type.
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
    /// The thirty-first of January is deliberate: a one-month offset from it lands on the twenty-eighth
    /// of February, which is the calendar clamping the legacy month interval performed and which a naive
    /// thirty-day substitution would silently break. The time component is deliberately non-zero so that
    /// the removal path's truncation to a whole date is observable rather than assumed, and the kind is
    /// Coordinated Universal Time because the migrated clock speaks nothing else.
    /// </remarks>
    private static readonly DateTime Now = new(2026, 1, 31, 15, 9, 26, DateTimeKind.Utc);

    /// <summary>
    /// The perpetual-term date. The legacy engine assigned this literal for the one-off frequency
    /// (<c>RoleController.vb</c> L542) and it is externally observable in every existing row, so it is
    /// carried through unchanged rather than converted into an absent expiry.
    /// </summary>
    private static readonly DateTime PerpetualExpiry = new(9999, 12, 31, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>
    /// Each of the SIX legacy frequency codes is the character the column actually stores, and the
    /// migrated enumeration's underlying value is that character rather than an arbitrary ordinal.
    /// </summary>
    /// <param name="code">The single-character code exactly as the column holds it.</param>
    /// <param name="expected">The migrated member the code must resolve to.</param>
    /// <remarks>
    /// MIGRATION: the codes are DATA, never identifiers, and this test is what stops a future rename from
    /// passing review. <c>Roles.BillingFrequency</c> and <c>Roles.TrialFrequency</c> are both declared
    /// <c>char(1) NULL</c> in the baseline schema, the terminal chain leaves them with no foreign key and
    /// no check constraint, and the six characters below are the literal bytes already sitting in every
    /// existing installation. Renaming a member would not fail a build - it would silently mis-read live
    /// rows - so the round trip is asserted here in both directions. The plan text names only the day,
    /// week, month and year codes; the never and one-off codes are equally load-bearing, the first
    /// because it doubles as the no-trial guard and the second because it encodes a perpetual term, and
    /// both are covered.
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

    /// <summary>
    /// The day code offsets the expiry by the period counted in days.
    /// </summary>
    /// <remarks>
    /// MIGRATION: replaces the day-interval call the Visual Basic runtime supplied at
    /// <c>RoleController.vb</c> L543, whose import at L25 is the only occurrence in the whole in-scope
    /// legacy surface and is removed entirely. The migrated arithmetic is the framework's own day offset.
    /// </remarks>
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

    /// <summary>
    /// The week code offsets the expiry by SEVEN DAYS PER PERIOD, not by a week interval.
    /// </summary>
    /// <remarks>
    /// MIGRATION: replaces <c>RoleController.vb</c> L544, which deliberately used a DAY interval
    /// multiplied by seven rather than any week interval. The distinction is not cosmetic - the migrated
    /// arithmetic must multiply and add days to stay equivalent - so this test asserts the multiplication
    /// explicitly as well as the resulting literal.
    /// </remarks>
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
    /// <remarks>
    /// MIGRATION: replaces <c>RoleController.vb</c> L545. The frozen instant is the thirty-first of
    /// January precisely so this test proves the clamping: a one-month term lands on the twenty-eighth of
    /// February, which is what the legacy month interval produced and what a thirty-day substitution
    /// would silently get wrong.
    /// </remarks>
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
    /// <remarks>
    /// MIGRATION: replaces <c>RoleController.vb</c> L546. The migrated engine expresses a year as twelve
    /// months so that one clamping helper serves both codes; this test pins that choice by asserting the
    /// result against the framework's year offset as well as against a literal, so the two can never
    /// drift apart unnoticed.
    /// </remarks>
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
    /// <remarks>
    /// <para>
    /// The leap case is the one where a year offset and a naive three-hundred-and-sixty-five-day offset
    /// disagree, so it is asserted rather than assumed. A one-year term from the twenty-ninth of February
    /// clamps onto the twenty-eighth, exactly as the legacy year interval did.
    /// </para>
    /// <para>
    /// MIGRATION: SEC-F5. The leap day is placed on the CLOCK rather than submitted as an expiry. It used
    /// to be submitted, because a submitted bound was the base the term was offset from; a submitted bound
    /// is now stored as given, so the only base a derivation has is the current instant - and moving the
    /// clock is how this suite states one. The property under examination is unchanged: it is the offset
    /// helper's calendar arithmetic, not where the base came from.
    /// </para>
    /// </remarks>
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

    /// <summary>
    /// The never code stores NO expiry at all, so the membership does not lapse.
    /// </summary>
    /// <remarks>
    /// MIGRATION: <c>RoleController.vb</c> L541 assigned the absent-date sentinel here - the minimum date
    /// value, published as the legacy null-date constant. Absence is carried as a null date in the
    /// migrated model rather than as that sentinel, because the sentinel is absence and not a real
    /// instant (AAP Rule T7). The sentinel survives only where a wire contract is externally observable,
    /// which an unbounded expiry is not.
    /// </remarks>
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

    /// <summary>
    /// The one-off code stores the perpetual far-future date, and does NOT consult the period.
    /// </summary>
    /// <remarks>
    /// MIGRATION: <c>RoleController.vb</c> L542 assigned the literal thirty-first of December 9999. That
    /// value is a REAL, externally observable date sitting in existing rows, so it is carried through
    /// unchanged and is never quietly converted into an absent expiry - the two mean different things to
    /// a legacy reader and are deliberately not conflated.
    /// </remarks>
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
    /// <param name="expiresEventually">
    /// Whether the code yields a bounded term. Only the never code does not.
    /// </param>
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
    /// An ABSENT period short-circuits the whole frequency table and yields no expiry, whatever
    /// frequency the role declares.
    /// </summary>
    /// <param name="code">The frequency code the role declares alongside its absent period.</param>
    /// <remarks>
    /// MIGRATION: <c>RoleController.vb</c> L537 tested the period against the legacy absent-integer
    /// sentinel and assigned the absent-date sentinel BEFORE the L540 selection was reached, so the
    /// frequency was never consulted. That ordering is reproduced exactly. The one-off code is included
    /// among the cases specifically to prove the short-circuit wins over the perpetual far-future date -
    /// which is the case a reordering of the two rules would break while every other case still passed.
    /// </remarks>
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
    /// <remarks>
    /// MIGRATION: MEASURED BEHAVIOURAL DIFFERENCE, DELIBERATE AND RECORDED. The legacy engine could only
    /// say "no period" by storing minus one, because <c>RoleInfo.vb</c> L203 and L218 declared both
    /// periods as non-nullable integers; the terminal columns are <c>[TrialPeriod] [int] NULL</c> and
    /// <c>[BillingPeriod] [int] NULL</c>, so the migrated model says it with a null and the schema wins
    /// (AAP Rule T4). Consequently a literal minus one is now an ORDINARY NEGATIVE PERIOD rather than an
    /// absence marker, and it reaches the frequency table instead of short-circuiting it. This test pins
    /// that, and pairing minus one with the one-off code makes the difference unmistakable: the migrated
    /// engine answers the perpetual date, where the legacy engine would have answered no expiry at all.
    /// The distinction matters because minus one is simultaneously a legitimate portal identifier, so
    /// treating it as "absent" wherever it appears is precisely the conflation Rule T7 forbids.
    /// </remarks>
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
    /// MIGRATION: a validated request cannot submit a period at or below zero, but a row written before
    /// those rules existed can hold one, and the engine is reached with stored values. The legacy day
    /// interval would have thrown for an extreme value; the migrated helper clamps in both directions,
    /// which is hardening rather than a change of business rule and is asserted here so it cannot regress.
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
    /// MIGRATION: the week case is the dangerous one and is the reason the migrated engine widens before
    /// it multiplies. Multiplying the largest storable period by seven in thirty-two-bit arithmetic wraps
    /// to a negative day count, which would have moved an expiry silently INTO THE PAST - a membership
    /// cancelling itself on creation. The day, month and year cases raised a range fault instead, which
    /// surfaced as a server error naming no field. Both faults are replaced by a clamp, and the clamp
    /// resolves upward to the perpetual date because that value is already this domain's encoding of an
    /// unbounded term.
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
    /// When the trial has NOT been consumed and the trial frequency is not the never code, the TRIAL
    /// terms govern the expiry and the billing terms are ignored.
    /// </summary>
    /// <remarks>
    /// MIGRATION: reproduces the selection at <c>RoleController.vb</c> L521-L523. The fixture declares a
    /// fourteen-day trial ahead of a one-month billing term, and the two produce visibly different dates,
    /// so this test cannot pass by accident if the branches were transposed.
    /// </remarks>
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
    /// <remarks>
    /// MIGRATION: reproduces <c>RoleController.vb</c> L515 priming the flag from the stored membership and
    /// L524-L526 selecting the billing terms. Nothing on the request contract can reset the flag, which is
    /// what stops a cancelled subscriber restarting a trial.
    /// </remarks>
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
    /// <remarks>
    /// MIGRATION: this is the second, independent responsibility the never code carries at
    /// <c>RoleController.vb</c> L521, where the guard reads "the trial frequency is not the never code".
    /// The terminal projection applies the same test when it gates the trial fee, period and frequency
    /// behind a comparison against that code, so dropping the member would break trial selection in two
    /// layers at once. It is therefore asserted separately from the consumed-trial case above.
    /// </remarks>
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
    /// An ABSENT trial frequency lets the billing terms govern - the resolution of a legacy expression
    /// that did not short-circuit, and a measured behavioural difference.
    /// </summary>
    /// <remarks>
    /// <para>
    /// MIGRATION: MEASURED BEHAVIOURAL DIFFERENCE, DELIBERATE AND RECORDED IN <c>MIGRATION_NOTES.md</c>.
    /// <c>RoleController.vb</c> L521 reads
    /// <c>If IsTrialUsed = False And role.TrialFrequency.ToString &lt;&gt; "N" Then</c>, and the operator
    /// is the NON-SHORT-CIRCUITING one, so the right-hand side was evaluated even when the left-hand side
    /// was already false. That never faulted in the legacy world for two reasons that both stopped
    /// holding at the migration boundary: <c>RoleInfo.vb</c> L188 declared the property as a string, and
    /// the legacy reader coerced a null column to the legacy null-string constant, which is the EMPTY
    /// STRING and not a null. The consequence was that a role whose trial frequency column was null
    /// compared empty against the never code, the comparison succeeded, and the TRIAL terms governed a
    /// role that declared no trial at all.
    /// </para>
    /// <para>
    /// The column is genuinely <c>char(1) NULL</c>, so the migrated property is a nullable enumeration and
    /// an absent value is a null rather than an empty string. The migrated guard therefore requires a
    /// present frequency before the trial can govern, and an absent one falls to the BILLING terms. That
    /// is a different answer from the legacy one, it is the answer the column's own nullability implies,
    /// and it is asserted here rather than being left to be discovered. The explicit conversion the legacy
    /// line performed on an already-string property - a coercion the administration screens' relaxed
    /// compilation mode permitted - has no migrated counterpart at all.
    /// </para>
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
    /// <remarks>
    /// MIGRATION: the companion case to the test above. Because <c>RoleController.vb</c> L521 used the
    /// non-short-circuiting operator, its right-hand side ran even on the already-consumed branch, so a
    /// naive translation to a short-circuiting operator would change WHEN the property is touched as well
    /// as what the comparison yields. The migrated guard reaches the same answer by pattern-matching the
    /// nullable value rather than by dereferencing it, so neither ordering can fault; this test proves the
    /// combination that would have thrown had the migrated property been dereferenced instead.
    /// </remarks>
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
    /// <para>
    /// MIGRATION: SEC-F5 REPLACED A FACT ASSERTING THE OPPOSITE, and the correction is a correction of
    /// ATTRIBUTION. The discarded-start-gate rule is real, but it lives at <c>RoleController.vb</c>
    /// L530-L532 inside <c>UpdateUserRole</c> - a member that declares no date parameters and reads every
    /// bound it works from out of the STORED assignment at L513-L515. The member the legacy screen called
    /// with a caller's own dates is <c>AddUserRole</c> at L295-L315, which assigns both bounds to the row
    /// verbatim on the insert branch and on the update branch alike. Applying the stored-bound rule to a
    /// submitted bound discarded the caller's instruction while answering that it had been accepted.
    /// </para>
    /// <para>
    /// The membership is still ACTIVE, which is the substantive property the withdrawn fact was reaching
    /// for: a start date in the past opens the membership rather than gating it, whether it is recorded or
    /// cleared. What changes is that the store now says WHEN it opened.
    /// </para>
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
    /// <remarks>
    /// The negative half of the clearing rule at <c>RoleController.vb</c> L530: only a PAST value is
    /// cleared, and asserting the future case separately is what proves the comparison is a comparison
    /// rather than an unconditional clear.
    /// </remarks>
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
    /// <param name="offsetDays">
    /// How far the submitted bound sits from the frozen instant. Both signs are asserted, because the two
    /// previously took different wrong turns: a past bound was advanced to the present and then extended,
    /// while a future bound was used as the base the term was added to.
    /// </param>
    /// <remarks>
    /// <para>
    /// MIGRATION: SEC-F5 REPLACED TWO FACTS WITH THIS ONE, for the attribution reason recorded on the
    /// effective-date fact above: <c>RoleController.vb</c> L533-L534 clamps the STORED bound inside
    /// <c>UpdateUserRole</c>, which accepts no submitted dates, whereas the member the screen called
    /// (<c>AddUserRole</c>, L295-L315) stored what it was given. Running the derivation over a caller's
    /// own bound meant a stated end date came back a period later on a 2xx response - the most costly
    /// shape of this defect, because the stored value was plausible and the caller had no reason to
    /// re-read it.
    /// </para>
    /// <para>
    /// The derivation itself is not withdrawn and is asserted by the frequency facts above, all of which
    /// submit no bound. The two cases are separate because they are reached through different members in
    /// the legacy source, and this fact pins which one a caller's date reaches.
    /// </para>
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
    /// The legacy ABSENT-DATE MARKER submitted for either bound is read as absence at the request
    /// boundary, and is never stored as a real first-of-January-0001 instant.
    /// </summary>
    /// <remarks>
    /// MIGRATION: the marker is the legacy null-date constant, which is the MINIMUM DATE VALUE and not a
    /// null - the legacy properties were non-nullable dates, so a caller had no other way to say
    /// "unbounded". A caller built against that contract still submits it, and the boundary translates it
    /// (AAP Rule T7) so that no layer below carries sentinel knowledge. This case is measured in the legacy
    /// tree: the private automatic-enrolment helper at <c>RoleController.vb</c> L76 passes the marker for
    /// BOTH bounds, and the account-creation path does the same, so an installation's rows genuinely
    /// contain it. The migrated contract must therefore accept the minimum date value for both bounds
    /// without either rejecting it or preserving it as a real date.
    /// </remarks>
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
    /// <remarks>
    /// MIGRATION: this is the assertion that makes the boundary translation safe to reason about. The
    /// marker is always in the past, so the clamping would already have cleared a marker effective bound
    /// and advanced a marker expiry bound to the current instant - which is exactly the offset base an
    /// ABSENT expiry produces. Reading the marker as absence therefore states a reason that was previously
    /// only implied, and this test proves the equivalence rather than asserting it in prose: were the two
    /// requests ever to diverge, a caller written against the legacy non-nullable date contract would
    /// silently start receiving different terms from one written against the migrated nullable one. The
    /// automatic-enrolment helper at <c>RoleController.vb</c> L76 and the account-creation path both submit
    /// the marker for both bounds, so this is the shape existing callers actually use.
    /// </remarks>
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
    /// The absent-date marker is recognised even when a time component has been attached to it, because
    /// the comparison is made on the date part alone.
    /// </summary>
    /// <remarks>
    /// MIGRATION: the legacy emptiness tests compared the date part against the marker's date part and
    /// carried the source comment that this avoids subtle time differences. A caller that copied the
    /// marker through a legacy object may present it with a time attached, so an exact-equality test would
    /// let such a value through and store the year one as a real bound. The date-part comparison is
    /// preserved and asserted.
    /// </remarks>
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
    /// <para>
    /// MEASURED LEGACY SHAPE, ANNOTATED AND DELIBERATELY NOT FIXED (Minimal Change Clause item 1). The
    /// selection at <c>RoleController.vb</c> L540-L547 has NO default arm - L547 closes it immediately
    /// after the year case - so a character outside the six simply fell through and the bound kept the
    /// value the normalisation above had given it. The migrated switch reproduces that with a discard arm
    /// yielding the same local, so an unrecognised character is still not an error.
    /// </para>
    /// <para>
    /// MIGRATION: the terminal columns carry no check constraint and no foreign key, so the store accepts
    /// any single character and an unrecognised one is genuinely reachable from live data. Preserving the
    /// non-throwing shape means such a row is still readable and still assignable, where a throw would
    /// have turned one bad character into a failed operation. The DIFFERENCE from the legacy answer is the
    /// value that survives: the legacy engine seeded its local from the ambient instant at L505, so an
    /// unrecognised character stored an expiry equal to NOW - a membership that lapsed the moment it was
    /// created - whereas the migrated engine seeds from the request and stores NO expiry when none was
    /// submitted. That is the sane reading of the same code path and is recorded rather than absorbed.
    /// </para>
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
    /// <remarks>
    /// The discard arm yields the normalised local, so a bound the caller supplied survives untouched
    /// rather than being replaced by a derived one. Asserting both halves is what pins the arm as a
    /// pass-through rather than as a silent clear.
    /// </remarks>
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

    /// <summary>
    /// A member who does NOT yet hold the role has an assignment STAGED, and nothing is revised.
    /// </summary>
    /// <remarks>
    /// MIGRATION: the insert half of the branch at <c>RoleController.vb</c> L550-L555, which tested a local
    /// identifier against minus one to decide between revising the row it had read and calling the
    /// assignment member. The migrated engine tests the read row for null instead, which removes the
    /// sentinel from the decision entirely while reaching the same two arms.
    /// </remarks>
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
    /// <remarks>
    /// MIGRATION: the revise half of the branch at <c>RoleController.vb</c> L550-L552, and the upsert shape
    /// of the assignment member at L295-L315. Exactly two members are writable on a renewal: the identity
    /// columns are untouched because a renewal is not a move, and the consumed-trial fact is untouched
    /// because nothing a caller submits may reset it.
    /// </remarks>
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
    /// <remarks>
    /// MIGRATION: the legacy engine read the ambient machine clock four times inside one derivation -
    /// <c>RoleController.vb</c> L505, L530, L533 and L534 - and a request that crossed a tick between two
    /// of them could clear one bound against one instant and seed the other from a different one. The
    /// migrated engine reads once. This test proves the reading is taken from the injected abstraction, and
    /// the count assertion is what stops a future edit reintroducing a second, independent reading.
    /// </remarks>
    [Fact]
    public async Task AssignUserToRole_ReadsTheInjectedClockExactlyOnce()
    {
        // MIGRATION: SEC-F5. BOTH SHAPES ARE ASSERTED, because there are now two of them. A submitted
        // bound leaves the derivation early, and an edit that moved the reading down beside the offset
        // would then read the clock ZERO times on that path - which no single-shape assertion would
        // notice, and which would quietly reintroduce a second reading site the day a caller-independent
        // value was needed above it. The shape that runs the whole derivation is asserted alongside it.
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
    /// <remarks>
    /// The legacy engine had no time abstraction whatsoever, so no assertion of this kind could be written
    /// against it. Re-deriving the same request against a second frozen instant proves the engine consults
    /// the abstraction rather than a machine clock: a machine-clock read would answer identically for both.
    /// </remarks>
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
    /// MIGRATION: the legacy procedures returned nothing at all and reported status by mutating a
    /// by-reference argument, so a caller could not distinguish an applied change from a discarded one.
    /// The migrated members return an outcome carrying a stable machine-readable code, and NO migrated
    /// member exposes a by-reference or output parameter of any kind. The three cases are asserted in the
    /// order the service resolves them, because that order is itself a contract: the tenant is proved
    /// before the role, and the role before the account.
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
    /// <remarks>
    /// The terminal role read filtered on the role identifier AND the portal identifier together, so a
    /// foreign row simply did not come back. The migrated read keeps the portal as a CONDITION rather than
    /// as a hint, and this test proves the refusal survives.
    /// </remarks>
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
    /// MIGRATION: the legacy screens reached the event log through its controller with the key
    /// <c>USER_ROLE_CREATED</c>, and every record carried the acting account. The log store itself is
    /// outside the migration scope, so the record is emitted through the audit abstraction instead and the
    /// stable event NAME is what carries the audit intent forward. The acting account still comes from the
    /// credential rather than from a request body. The cache invalidation replaces the legacy coarse
    /// portal-wide and host-wide clears with one EXPLICIT, member-keyed eviction.
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
    /// A renewal is recorded under the SAME event name as a first assignment, distinguished by a property
    /// rather than by an event name the legacy vocabulary never contained.
    /// </summary>
    /// <remarks>
    /// MIGRATION: the legacy member was an upsert and raised one key for both arms, so inventing a second
    /// name would be a fabrication rather than fidelity. The distinguishing property is added instead.
    /// </remarks>
    [Fact]
    public async Task AssignUserToRole_Renewal_IsRecordedUnderTheSameEventNameAndFlagged()
    {
        Harness harness = Harness.Ready();
        harness.LookupRole = TermRole(Frequency.Month, period: 1);
        harness.ExistingAssignment = Membership();

        Result outcome = await harness.Service
            .AssignUserToRoleAsync(PortalId, RoleId, Assignment(), CancellationToken.None);

        outcome.IsSuccess.Should().BeTrue();
        AuditEvent record = Assert.Single(harness.AuditRecords);
        record.EventName.Should().Be(AuditEventNames.UserRoleCreated);
        record.Properties.Should().ContainKey("Renewed")
            .WhoseValue.Should().Be(true.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// Cancelling a PAID membership whose trial has been consumed EXPIRES it by back-dating the bound one
    /// day from TODAY, and does NOT delete the row - so the consumed-trial fact survives.
    /// </summary>
    /// <remarks>
    /// <para>
    /// MIGRATION: reproduces <c>RoleController.vb</c> L494-L497 verbatim in effect. The legacy line offset
    /// by minus one day from <c>Date.Today()</c> - a DATE, not a timestamp - and the truncation is
    /// preserved so the row reads as already lapsed for the WHOLE of the current day rather than only after
    /// the current hour. The frozen instant deliberately carries a non-zero time component so this test can
    /// prove the truncation rather than assume it.
    /// </para>
    /// <para>
    /// MIGRATION: two substitutions are recorded rather than absorbed. The Visual Basic runtime's
    /// date-offset intrinsic becomes the framework's own day offset, which is the only reason the legacy
    /// runtime import at L25 could be removed at all; and the legacy reading was server-LOCAL whereas the
    /// injected clock is Coordinated Universal Time only, so the back-dated bound can name a different
    /// calendar day from the one a legacy installation would have produced for the same real instant. The
    /// second difference is accepted deliberately - a local-zone stamp is not comparable between hosts -
    /// and it is precisely why the clock is injected here.
    /// </para>
    /// <para>
    /// MIGRATION: the fee is read from the ROLE where the legacy line appeared to read it from the
    /// membership. The two are the SAME value and this is a shape change rather than a behavioural one:
    /// <c>UserRoleInfo</c> declares <c>Inherits RoleInfo</c> (<c>UserRoleInfo.vb</c> L42-L43), so the fee
    /// the legacy line reached was <c>RoleInfo.ServiceFee</c> (L164) inherited onto the membership class,
    /// and the membership table itself carries no fee column at all. Splitting that single legacy class
    /// into a role entity and a membership entity along the real table boundaries is what moves the read
    /// to its owning record, and the value it yields is unchanged.
    /// </para>
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
    /// <remarks>
    /// MIGRATION: the legacy procedure took the expiring arm inside the same removal member and reported
    /// ONE outcome to its caller, so the two effects were indistinguishable. The advisory reason on a
    /// SUCCESS - not a failure - is how the distinction is surfaced without changing the operation's
    /// result.
    /// </remarks>
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
    /// <remarks>
    /// MIGRATION: the guard at <c>RoleController.vb</c> L494 is a conjunction of THREE conditions - the
    /// membership exists, the fee is strictly greater than zero, and the trial has been consumed - and the
    /// else arm at L500 deletes. Each falsifying combination is asserted separately because a single
    /// combined case would not say which condition had been mis-transcribed. A fee of exactly zero is
    /// included deliberately: zero is not greater than zero, and a free role is legitimate. An unrecorded
    /// consumed-trial value is included because the migrated column is nullable and a null must read as
    /// "not consumed" rather than faulting.
    /// </remarks>
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
    /// <para>
    /// MIGRATION: SEC-F5. <c>SecurityRoles.ascx.vb</c> L522-L526 cleared both date boxes for exactly this
    /// pairing before reading them, and L528-L539 then substituted the absent-date marker for each empty
    /// box, so the legacy assignment member received absence for both however the operator had filled the
    /// form in. The comparison is typed here; the legacy one compared an <c>Integer</c> against a
    /// <c>String</c> and relied on Option Strict being off.
    /// </para>
    /// <para>
    /// Enforcing it became NECESSARY, not merely faithful, once a submitted bound was honoured. While the
    /// derivation silently rewrote every submitted bound, this pairing was protected by accident - a
    /// portal's administrators role carries no term, so a submitted expiry was discarded on its way
    /// through. Honouring it would put an expiry on the tenant's only administrative membership, and when
    /// that lapsed the tenant would have no administrator at all: the same self-inflicted lockout the
    /// protected-role guard exists to prevent, reached by a different route.
    /// </para>
    /// <para>
    /// The bounds are discarded rather than the request refused, because discarding is what the screen did.
    /// A refusal would be a new behaviour, and it would break the enrolment of an administrator by a caller
    /// that submits the two dates on every assignment it makes.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task AssignUserToRole_PortalAdministratorToTheAdministratorRole_DiscardsSubmittedBounds()
    {
        Harness harness = Harness.Ready();

        // THE ROLE IS SHAPED THE WAY PORTAL PROVISIONING ACTUALLY SHAPES IT, which is what makes this fact
        // able to fail. Provisioning writes a tenant's three system roles with a MONTH frequency and a
        // period of ZERO - measured against a provisioned tenant, not assumed - so the derivation would
        // read a period that is present and zero, add zero months to the current instant, and produce an
        // expiry of NOW. An earlier revision of this rule passed absence THROUGH the derivation rather
        // than around it, and against this shape that yielded an administrators-role membership already
        // expired when it was written; the tenant's own administrator was then refused by the
        // authorisation handler on its very next request, because assignment validity windows are what the
        // handler reads. A role carrying no terms at all could not distinguish the two implementations.
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
    /// <remarks>
    /// The legacy condition is a conjunction - the administrator AND the administrators role - so asserting
    /// each half separately is what proves it is a conjunction rather than either half on its own. Without
    /// this, a guard keyed on the account alone, or on the role alone, would pass the fact above while
    /// silently discarding bounds a caller legitimately submitted.
    /// </remarks>
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
    /// <remarks>
    /// MIGRATION: the first of exactly two cases the protected-assignment rule refuses, measured at
    /// <c>RoleController.vb</c> L741 and duplicated in its twin at L764. It is enforced INSIDE the
    /// operation rather than exposed as a question a caller may ask and then ignore (AAP Rule T2); the
    /// legacy screen used the same rule only to decide whether to reveal a button, which is a presentation
    /// concern and never the authoritative enforcement.
    /// </remarks>
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
    /// MIGRATION: the second of the two protected cases. It is unconditional in the member's identity,
    /// which is why it is asserted with an ordinary member rather than with the administrator - a
    /// transcription that accidentally conjoined the two rules would still pass the administrator case and
    /// fail here.
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
    /// <remarks>
    /// MIGRATION: the legacy path cleared caches coarsely, portal-wide and host-wide, which discarded far
    /// more than the change warranted. The migrated eviction names the one member whose role set changed,
    /// and this test asserts the coarse clears are NOT reached - the assertion that makes the refinement
    /// verifiable rather than merely claimed.
    /// </remarks>
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
    /// MIGRATION: the two seeds are the sharpest sentinel collision in the whole migration.
    /// <c>Portals.PortalID</c> is seeded from MINUS ONE, which is exactly the legacy absent-integer
    /// sentinel, and <c>Roles.RoleID</c> is seeded from ZERO, which is the CLR default for its type. A
    /// migrated layer that read either as "absent" would refuse the very first portal and the very first
    /// role an installation ever created. Every other test in this file already uses both seeds; this one
    /// states the requirement outright so the reason cannot be lost if the constants are ever changed.
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
    /// <remarks>
    /// MIGRATION: nine legacy read members returned an untyped collection and two returned a bare string
    /// array, so a caller learned nothing about the shape it received and paging was reported through a
    /// by-reference total. The migrated reads answer a typed envelope carrying its items and its total
    /// together. Preserving the paid-membership columns through the projection is a functional-parity
    /// requirement rather than an optional extra, so they are asserted here.
    /// </remarks>
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
    /// <remarks>
    /// MIGRATION: this is the structural replacement for the legacy by-reference status argument. A legacy
    /// caller that ignored the status still received a collection and could not tell a genuine empty result
    /// from a failed one. Reading the value of a failed outcome is now a programming error and is reported
    /// as one.
    /// </remarks>
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
    /// <remarks>
    /// MIGRATION: two legacy members answered a bare array of role NAMES, which forced every caller to
    /// re-read the role to learn anything else about it. The migrated read answers the same projection the
    /// listing uses, so a caller has the paid-membership terms in hand.
    /// </remarks>
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

    /// <summary>
    /// Builds a member of the tenant under test.
    /// </summary>
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

    /// <summary>
    /// Builds one stored membership.
    /// </summary>
    /// <param name="userId">The account the membership belongs to.</param>
    /// <param name="roleId">The role the membership names.</param>
    /// <param name="trialUsed">
    /// Whether the trial has been consumed. Null models the column's own nullability, which must read as
    /// "not consumed" rather than faulting.
    /// </param>
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

    /// <summary>
    /// Builds an assignment request for the member under test.
    /// </summary>
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
    /// <remarks>
    /// Only Domain and Application abstractions are doubled. Nothing here reaches a database context, an
    /// object-relational mapper, a web host or an ambient request, and the assembled subject is the real
    /// service rather than a stand-in - so every assertion in this file is made against the production
    /// derivation itself.
    /// </remarks>
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
            // MIGRATION: SEC-F3/SEC-F5. The READ is gated by the same existence flag as the PROBE, so the
            // harness describes ONE world. Several members now read the tenant row rather than probing for
            // it - they need its designations - and a harness that answered "no such tenant" to the probe
            // while handing out a row to the read would let a test assert a refusal that production could
            // not produce, or miss one it does. A test may still clear the row alone, which is how it says
            // "the tenant is there but carries no designation".
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

            // The clock is FROZEN. No test in this file may observe a moving instant, because a
            // date-arithmetic suite that could straddle a midnight, month or year boundary would fail
            // unpredictably and for a reason unrelated to the rule it asserts.
            harness.Clock.SetupGet(clock => clock.UtcNow).Returns(Now);

            return harness;
        }
    }
}
