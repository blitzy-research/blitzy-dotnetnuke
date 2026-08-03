using DnnMigration.Application.Dtos.Common;
using DnnMigration.Application.Dtos.Module;
using DnnMigration.Application.Dtos.Portal;
using DnnMigration.Application.Dtos.Role;
using DnnMigration.Application.Dtos.Tab;
using DnnMigration.Application.Validation;
using DnnMigration.Domain.Common;
using FluentAssertions;
using FluentValidation.Results;
using Xunit;

namespace DnnMigration.UnitTests.Validation;

/// <summary>
/// Pins the storage-range rules added across the write surface, and the page-offset rule that stopped an
/// unrepresentable read position reaching a reader.
/// </summary>
/// <remarks>
/// <para>
/// A security review found that every validator treated the CLR type as a sufficient bound although the
/// stored domain is narrower. Each rule asserted here refuses a value the type accepts and the column
/// cannot hold, and each is asserted AT its boundary in both directions - the last storable value must
/// pass and the first unstorable one must fail - because a rule proven only far outside its limit does not
/// say where the limit is.
/// </para>
/// <para>
/// Every failure is also asserted to name the member a caller SUBMITTED. That is not incidental: an
/// earlier revision of these rules was written over the unwrapped optional value, which made the framework
/// report the field as <c>ServiceFee.Value</c>, so the key in the <c>errors</c> payload stopped matching
/// the key the caller had sent. Asserting the name is what keeps that regression from returning.
/// </para>
/// </remarks>
public class StorageRangeBoundTests
{
    private static readonly DateTime BeforeTheStoredCalendar =
        SqlServerRange.MinimumDateTime.AddDays(-1);

    private static readonly decimal PastTheCurrencyCeiling =
        SqlServerRange.MaximumMoney + 0.0001m;

    /// <summary>
    /// The perpetual expiry a role's one-off term stores, which every date rule must admit.
    /// </summary>
    private static readonly DateTime PerpetualExpiry = new(9999, 12, 31, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>
    /// A page request whose index and size multiply past the addressable range is refused, naming the page
    /// index.
    /// </summary>
    /// <remarks>
    /// Neither member is individually out of range - the size is within the permitted maximum and the index
    /// is a positive integer - so this is reachable only as a cross-field rule, which is why it is stated
    /// over the request rather than over either member.
    /// </remarks>
    [Fact]
    public void PagedRequest_WhoseOffsetIsUnrepresentable_IsRefused()
    {
        var request = new PagedRequest { PageIndex = 300_000_000, PageSize = 100 };

        ValidationResult result = new PagedRequestValidator().Validate(request);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(
            failure => failure.PropertyName == nameof(PagedRequest.PageIndex),
            "the offset is reported against the index, which is the member a caller can lower");
    }

    /// <summary>
    /// An ordinary page request is untouched by the offset rule.
    /// </summary>
    /// <param name="pageIndex">The requested page.</param>
    /// <param name="pageSize">The requested page size.</param>
    [Theory]
    [InlineData(0, 25)]
    [InlineData(1, 100)]
    [InlineData(1_000, 100)]
    [InlineData(21_474_836, 100)]
    public void PagedRequest_WithARepresentableOffset_IsAccepted(int pageIndex, int pageSize)
        => new PagedRequestValidator()
            .Validate(new PagedRequest { PageIndex = pageIndex, PageSize = pageSize })
            .IsValid.Should().BeTrue();

    /// <summary>
    /// A role fee past the currency ceiling is refused on both the create and the update path, and the
    /// failure names the fee itself.
    /// </summary>
    /// <remarks>
    /// Both paths are asserted together because they were unequal before this work: the create path had a
    /// lower bound and no upper one, and the update path had no validator at all, so the same amount was
    /// refused, stored or faulted depending only on which verb a caller used.
    /// </remarks>
    [Fact]
    public void RoleFees_PastTheCurrencyCeiling_AreRefusedOnBothPaths()
    {
        ValidationResult created = new CreateRoleRequestValidator().Validate(new CreateRoleRequest
        {
            RoleName = "Subscribers",
            ServiceFee = PastTheCurrencyCeiling,
            TrialFee = PastTheCurrencyCeiling,
        });

        ValidationResult updated = new UpdateRoleRequestValidator().Validate(new UpdateRoleRequest
        {
            ServiceFee = PastTheCurrencyCeiling,
            TrialFee = PastTheCurrencyCeiling,
        });

        foreach (ValidationResult result in new[] { created, updated })
        {
            result.IsValid.Should().BeFalse();
            result.Errors.Select(failure => failure.PropertyName).Should().Contain(
                [nameof(CreateRoleRequest.ServiceFee), nameof(CreateRoleRequest.TrialFee)],
                "a failure names the member the caller submitted, not its unwrapped value");
        }
    }

    /// <summary>
    /// A role fee at the currency ceiling is accepted, so the bound refuses only what the column cannot
    /// hold.
    /// </summary>
    [Fact]
    public void RoleFees_AtTheCurrencyCeiling_AreAccepted()
    {
        new CreateRoleRequestValidator().Validate(new CreateRoleRequest
        {
            RoleName = "Subscribers",
            ServiceFee = SqlServerRange.MaximumMoney,
            TrialFee = SqlServerRange.MaximumMoney,
        }).IsValid.Should().BeTrue();

        new UpdateRoleRequestValidator().Validate(new UpdateRoleRequest
        {
            ServiceFee = SqlServerRange.MaximumMoney,
            TrialFee = SqlServerRange.MaximumMoney,
        }).IsValid.Should().BeTrue();
    }

    /// <summary>
    /// A negative role fee is still refused on the update path with the legacy wording, so adding a ceiling
    /// did not displace the measured floor.
    /// </summary>
    [Fact]
    public void RoleServiceFee_WhenNegative_IsRefusedWithTheLegacyWording()
    {
        ValidationResult result = new UpdateRoleRequestValidator()
            .Validate(new UpdateRoleRequest { ServiceFee = -0.01m });

        result.Errors.Should().Contain(
            failure => failure.PropertyName == nameof(UpdateRoleRequest.ServiceFee)
                && failure.ErrorMessage == "Service Fee Must Be Greater Than or Equal to Zero",
            "the update path must carry the same measured wording as the create path");
    }

    /// <summary>
    /// A role period past the permitted maximum is refused on both paths.
    /// </summary>
    /// <remarks>
    /// The period drives the offset arithmetic that derives a membership expiry. That arithmetic clamps
    /// rather than overflowing, so this rule is not what keeps it safe - it is what gives a caller a
    /// field-level answer instead of a silently clamped expiry it never asked for.
    /// </remarks>
    [Fact]
    public void RolePeriods_PastThePermittedMaximum_AreRefused()
    {
        const int pastTheMaximum = 10_001;

        new CreateRoleRequestValidator().Validate(new CreateRoleRequest
        {
            RoleName = "Subscribers",
            BillingPeriod = pastTheMaximum,
            TrialPeriod = pastTheMaximum,
        }).IsValid.Should().BeFalse();

        new UpdateRoleRequestValidator().Validate(new UpdateRoleRequest
        {
            BillingPeriod = pastTheMaximum,
            TrialPeriod = pastTheMaximum,
        }).IsValid.Should().BeFalse();
    }

    /// <summary>
    /// An ordinary role period is accepted, and so is one at the maximum.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(12)]
    [InlineData(10_000)]
    public void RolePeriods_WithinThePermittedMaximum_AreAccepted(int period)
        => new UpdateRoleRequestValidator()
            .Validate(new UpdateRoleRequest { BillingPeriod = period, TrialPeriod = period })
            .IsValid.Should().BeTrue();

    /// <summary>
    /// A membership window date outside the stored calendar is refused, naming the date submitted.
    /// </summary>
    [Fact]
    public void RoleAssignmentDates_OutsideTheStoredCalendar_AreRefused()
    {
        ValidationResult result = new RoleAssignmentRequestValidator().Validate(new RoleAssignmentRequest
        {
            UserId = 5,
            EffectiveDate = BeforeTheStoredCalendar,
            ExpiryDate = BeforeTheStoredCalendar,
        });

        result.IsValid.Should().BeFalse();
        result.Errors.Select(failure => failure.PropertyName).Should().Contain(
            [nameof(RoleAssignmentRequest.EffectiveDate), nameof(RoleAssignmentRequest.ExpiryDate)]);
    }

    /// <summary>
    /// A membership assignment carrying the preserved perpetual expiry is accepted, and so is one carrying
    /// neither date.
    /// </summary>
    /// <remarks>
    /// The perpetual value is an ordinary stored instant that a legacy reader expects verbatim, so a rule
    /// refusing it would break the contract it was added to defend. Absence is accepted because an absent
    /// effective date means "already in force" and an absent expiry means "derive one from the role's own
    /// terms" - both real states.
    /// </remarks>
    [Fact]
    public void RoleAssignmentDates_AdmitThePerpetualValueAndAbsence()
    {
        new RoleAssignmentRequestValidator().Validate(new RoleAssignmentRequest
        {
            UserId = 5,
            ExpiryDate = PerpetualExpiry,
        }).IsValid.Should().BeTrue("the perpetual expiry must round-trip");

        new RoleAssignmentRequestValidator()
            .Validate(new RoleAssignmentRequest { UserId = 5 })
            .IsValid.Should().BeTrue("neither date is required");
    }

    /// <summary>
    /// A membership window whose start follows its end IS refused, because the legacy screen refused it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// AN EARLIER REVISION OF THIS FACT ASSERTED THE OPPOSITE, on the stated ground that "the legacy screen
    /// declared no ordering validator". That ground is false, and the reason it was believed is worth
    /// recording so it is not believed again: <c>Website/admin/Security/securityroles.ascx</c> writes its
    /// tags in LOWER CASE, so a case-sensitive search for <c>CompareValidator</c> finds nothing while
    /// <c>comparevalidator</c> finds three. L47 declares <c>valDates</c> with
    /// <c>operator="GreaterThan"</c>, <c>controltovalidate="txtExpiryDate"</c> and
    /// <c>controltocompare="txtEffectiveDate"</c>, and its message reads "Expiry Date must be Greater than
    /// Effective Date". The ordering rule is therefore measured legacy behaviour, and reproducing it is
    /// required by the migration discipline rather than a change of policy.
    /// </para>
    /// <para>
    /// Strictly greater, matching the operator: a window that opens and closes at the same instant is a
    /// membership never in force, and the legacy screen refused it too. The rule fires only when both dates
    /// are present, which is the ASP.NET comparison validator's own behaviour - it treats an empty control as
    /// valid - so an open-ended window in either direction stays legal, as the two facts above assert.
    /// </para>
    /// </remarks>
    [Fact]
    public void RoleAssignmentDates_AreComparedToOneAnotherAsTheLegacyScreenComparedThem()
        => new RoleAssignmentRequestValidator().Validate(new RoleAssignmentRequest
        {
            UserId = 5,
            EffectiveDate = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            ExpiryDate = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        }).IsValid.Should().BeFalse();

    /// <summary>
    /// A portal fee past the currency ceiling is refused even though it satisfies the digit count.
    /// </summary>
    /// <remarks>
    /// This is the narrow band the precision rule alone left open. Nineteen digits with four decimals admit
    /// fifteen leading digits of any magnitude, so an amount above the currency ceiling and below a
    /// sixteen-digit integer part passed the digit count and still overflowed the column.
    /// </remarks>
    [Fact]
    public void PortalHostFee_InTheBandThePrecisionRuleLeftOpen_IsRefused()
    {
        const decimal insideTheDigitCountAndPastTheCeiling = 999_999_999_999_999.9999m;

        ValidationResult result = new UpdatePortalRequestValidator()
            .Validate(ValidPortalUpdate(fee: insideTheDigitCountAndPastTheCeiling));

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(
            failure => failure.PropertyName == nameof(UpdatePortalRequest.HostFee));
    }

    /// <summary>
    /// A portal fee at the currency ceiling is accepted.
    /// </summary>
    [Fact]
    public void PortalHostFee_AtTheCurrencyCeiling_IsAccepted()
        => new UpdatePortalRequestValidator()
            .Validate(ValidPortalUpdate(fee: SqlServerRange.MaximumMoney))
            .IsValid.Should().BeTrue();

    /// <summary>
    /// A portal expiry outside the stored calendar is refused, and the perpetual value is admitted.
    /// </summary>
    [Fact]
    public void PortalExpiry_IsBoundedByTheStoredCalendar()
    {
        UpdatePortalRequest tooEarly = ValidPortalUpdate();
        tooEarly.ExpiryDate = BeforeTheStoredCalendar;

        UpdatePortalRequest perpetual = ValidPortalUpdate();
        perpetual.ExpiryDate = PerpetualExpiry;

        new UpdatePortalRequestValidator().Validate(tooEarly).IsValid.Should().BeFalse();
        new UpdatePortalRequestValidator().Validate(perpetual).IsValid.Should().BeTrue();
    }

    /// <summary>
    /// A module term date outside the stored calendar is refused on both the create and the update path.
    /// </summary>
    [Fact]
    public void ModuleTermDates_OutsideTheStoredCalendar_AreRefused()
    {
        ValidationResult created = new CreateModuleRequestValidator().Validate(new CreateModuleRequest
        {
            ModuleDefId = 1,
            TabId = 1,
            ModuleTitle = "Announcements",
            StartDate = BeforeTheStoredCalendar,
        });

        ValidationResult updated = new UpdateModuleRequestValidator().Validate(new UpdateModuleRequest
        {
            ModuleTitle = "Announcements",
            EndDate = BeforeTheStoredCalendar,
        });

        created.Errors.Should().Contain(
            failure => failure.PropertyName == nameof(CreateModuleRequest.StartDate));
        updated.Errors.Should().Contain(
            failure => failure.PropertyName == nameof(UpdateModuleRequest.EndDate));
    }

    /// <summary>
    /// Module term dates are deliberately still not compared to one another.
    /// </summary>
    /// <remarks>
    /// The legacy validators on both boxes were format checks alone, so an ordering rule would be a new
    /// restriction on callers. Asserted so that adding one is visibly a decision.
    /// </remarks>
    [Fact]
    public void ModuleTermDates_AreNotComparedToOneAnother()
        => new UpdateModuleRequestValidator().Validate(new UpdateModuleRequest
        {
            ModuleTitle = "Announcements",
            StartDate = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            EndDate = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        }).IsValid.Should().BeTrue();

    /// <summary>
    /// A page term date outside the stored calendar is refused, and the pair is NOT compared - the same
    /// treatment the module's term dates receive immediately above.
    /// </summary>
    /// <remarks>
    /// <para>
    /// AN EARLIER REVISION OF THIS FACT ALSO REQUIRED THE PAIR TO BE ORDERED, and the ordering half has been
    /// withdrawn rather than the whole fact deleted. The representability bound stands, and it is the half
    /// that matters here: it moves a refusal the provider already performed - as a server fault naming no
    /// field - to the edge, where it names the member. It changes no outcome, only the shape of one.
    /// </para>
    /// <para>
    /// The ordering rule did change an outcome, and measurement does not support it. The page-administration
    /// screen declares the two date boxes with a rendered width and no <c>ControlToCompare</c> anywhere, so a
    /// reversed pair was a submission the legacy screen accepted and stored. That is a legacy defect, and the
    /// migration discipline requires it to be recorded rather than corrected: identical inputs must produce
    /// identical outcomes. The asymmetry with the module rule above therefore disappears, which is the more
    /// defensible position of the two - the same kind of value is judged the same way on both surfaces.
    /// Recorded in MIGRATION_NOTES.md.
    /// </para>
    /// </remarks>
    [Fact]
    public void PageTermDates_AreBoundedAndNotCompared()
    {
        ValidationResult unstorable = new UpdateTabRequestValidator().Validate(new UpdateTabRequest
        {
            TabName = "Home",
            StartDate = BeforeTheStoredCalendar,
        });

        ValidationResult outOfOrder = new UpdateTabRequestValidator().Validate(new UpdateTabRequest
        {
            TabName = "Home",
            StartDate = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            EndDate = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        });

        unstorable.Errors.Should().Contain(
            failure => failure.PropertyName == nameof(UpdateTabRequest.StartDate));
        outOfOrder.IsValid.Should().BeTrue(
            "the legacy screen declared no comparison between the two, so a reversed pair was storable");
    }

    /// <summary>
    /// A page carrying one term date and not the other is accepted.
    /// </summary>
    /// <remarks>
    /// The ordering rule must not become a rule demanding the pair: a page with a start and no end runs
    /// indefinitely, and one with an end and no start has always been running.
    /// </remarks>
    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public void PageTermDates_MayBeSuppliedSingly(bool withStart, bool withEnd)
    {
        var request = new UpdateTabRequest { TabName = "Home" };
        if (withStart)
        {
            request.StartDate = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        }

        if (withEnd)
        {
            request.EndDate = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        }

        new UpdateTabRequestValidator().Validate(request).IsValid.Should().BeTrue();
    }

    /// <summary>
    /// Builds a portal update that satisfies every rule except the one under test.
    /// </summary>
    /// <param name="fee">The hosting fee to submit.</param>
    /// <returns>The request.</returns>
    private static UpdatePortalRequest ValidPortalUpdate(decimal? fee = null) => new()
    {
        PortalName = "Measured Portal",
        HostFee = fee,
    };
}
