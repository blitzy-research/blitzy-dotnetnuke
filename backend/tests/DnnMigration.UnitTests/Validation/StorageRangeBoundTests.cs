using DnnMigration.Application.Dtos.Common;
using DnnMigration.Application.Dtos.Module;
using DnnMigration.Application.Dtos.Portal;
using DnnMigration.Application.Dtos.Role;
using DnnMigration.Application.Dtos.Tab;
using DnnMigration.Application.Validation;
using DnnMigration.Domain.Common;
using DnnMigration.Domain.Enums;
using FluentAssertions;
using FluentValidation.Results;
using Xunit;

namespace DnnMigration.UnitTests.Validation;

/// <summary>
/// Pins the storage-range rules added across the write surface, and the page-offset rule that stopped an
/// unrepresentable read position reaching a reader.
/// </summary>
/// <remarks>
/// A security review found that every validator treated the CLR type as a sufficient bound although the
/// stored domain is narrower.
/// </remarks>
public class StorageRangeBoundTests
{
    private static readonly DateTime BeforeTheStoredCalendar =
        SqlServerRange.MinimumDateTime.AddDays(-1);

    private static readonly decimal PastTheCurrencyCeiling =
        SqlServerRange.MaximumMoney + 0.0001m;

    /// <summary>The perpetual expiry a role's one-off term stores, which every date rule must admit.</summary>
    private static readonly DateTime PerpetualExpiry = new(9999, 12, 31, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>
    /// A page request whose index and size multiply past the addressable range is refused, naming the page
    /// index.
    /// </summary>
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

    /// <summary>An ordinary page request is untouched by the offset rule.</summary>
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
            RoleName = "Subscribers",
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
    /// A role period that is not strictly positive is refused on both paths where a recurring cycle is
    /// declared, with the legacy wording.
    /// </summary>
    [Theory]
    [InlineData(0, BillingFrequency.Month)]
    [InlineData(-1, BillingFrequency.Month)]
    [InlineData(-1, null)]
    public void RolePeriods_ThatAreNotPositive_AreRefusedOnBothPaths(int period, BillingFrequency? frequency)
    {
        new CreateRoleRequestValidator().Validate(new CreateRoleRequest
        {
            RoleName = "Subscribers",
            BillingPeriod = period,
            BillingFrequency = frequency,
            TrialPeriod = period,
            TrialFrequency = frequency,
        }).IsValid.Should().BeFalse();

        new UpdateRoleRequestValidator().Validate(new UpdateRoleRequest
        {
            RoleName = "Subscribers",
            BillingPeriod = period,
            BillingFrequency = frequency,
            TrialPeriod = period,
            TrialFrequency = frequency,
        }).IsValid.Should().BeFalse();
    }

    /// <summary>
    /// A role period of zero is accepted on both paths when no recurring cycle is declared beside it, so a
    /// role the portal template created can be read and written back unchanged.
    /// </summary>
    [Theory]
    [InlineData(BillingFrequency.None)]
    [InlineData(null)]
    public void RolePeriodOfZero_WithNoCycleDeclared_IsAcceptedOnBothPaths(BillingFrequency? frequency)
    {
        new CreateRoleRequestValidator().Validate(new CreateRoleRequest
        {
            RoleName = "Subscribers",
            BillingPeriod = 0,
            BillingFrequency = frequency,
            TrialPeriod = 0,
            TrialFrequency = frequency,
        }).IsValid.Should().BeTrue();

        new UpdateRoleRequestValidator().Validate(new UpdateRoleRequest
        {
            RoleName = "Subscribers",
            BillingPeriod = 0,
            BillingFrequency = frequency,
            TrialPeriod = 0,
            TrialFrequency = frequency,
        }).IsValid.Should().BeTrue();
    }

    /// <summary>
    /// EVERY positive role period is accepted on both paths, including the largest an <c>int</c> column can
    /// hold.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(12)]
    [InlineData(10_000)]
    [InlineData(10_001)]
    [InlineData(int.MaxValue)]
    public void RolePeriods_AnyPositiveCount_IsAcceptedOnBothPaths(int period)
    {
        new CreateRoleRequestValidator().Validate(new CreateRoleRequest
        {
            RoleName = "Subscribers",
            BillingPeriod = period,
            TrialPeriod = period,
        }).IsValid.Should().BeTrue();

        new UpdateRoleRequestValidator()
            .Validate(new UpdateRoleRequest
            {
                RoleName = "Subscribers",
                BillingPeriod = period,
                TrialPeriod = period,
            })
            .IsValid.Should().BeTrue();
    }

    /// <summary>A membership window date outside the stored calendar is refused, naming the date submitted.</summary>
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
    [Fact]
    public void RoleAssignmentDates_AreComparedToOneAnotherAsTheLegacyScreenComparedThem()
        => new RoleAssignmentRequestValidator().Validate(new RoleAssignmentRequest
        {
            UserId = 5,
            EffectiveDate = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            ExpiryDate = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        }).IsValid.Should().BeFalse();

    /// <summary>A portal fee past the currency ceiling is refused even though it satisfies the digit count.</summary>
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

    /// <summary>A portal fee at the currency ceiling is accepted.</summary>
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
    /// Processor credential updates admit the explicit keep and clear states and require a managed-secret
    /// reference for replacements.
    /// </summary>
    /// <param name="reference">The submitted update value.</param>
    /// <param name="accepted">Whether the reference satisfies the contract.</param>
    [Theory]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData("secret://processor/current", true)]
    [InlineData("plaintext-password", false)]
    [InlineData("secret://processor/contains space", false)]
    [InlineData("secret://processor/abcdefghijklmnopqrstuvwxyz0123456789-extra", false)]
    public void PortalProcessorCredentialReference_RequiresManagedSecretSyntax(
        string? reference,
        bool accepted)
    {
        UpdatePortalRequest request = ValidPortalUpdate();
        request.ProcessorCredentialReference = reference;

        new UpdatePortalRequestValidator().Validate(request).IsValid.Should().Be(
            accepted,
            "null keeps, empty clears, and replacements must be bounded secret references");
    }

    /// <summary>
    /// A module term date outside the stored calendar is refused on both the create and the update path.
    /// </summary>
    /// <remarks>
    /// The count is asserted, not merely the presence, and that is the load-bearing half. The create
    /// validator had registered the identical storage-range rule for each date property THREE TIMES, so one
    /// unstorable value produced three indistinguishable failures naming the same property - a validation
    /// response repeating itself for no reason a client could interpret.
    /// </remarks>
    [Fact]
    public void ModuleTermDates_OutsideTheStoredCalendar_AreRefusedExactlyOnce()
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

        created.Errors.Should().ContainSingle(
            failure => failure.PropertyName == nameof(CreateModuleRequest.StartDate),
            "one wrong value is one failure; a rule declared more than once reports the same sentence "
            + "several times under the same property name");
        updated.Errors.Should().ContainSingle(
            failure => failure.PropertyName == nameof(UpdateModuleRequest.EndDate));

        // The bound is declared for BOTH members on BOTH paths, so the member that was not set here must
        // report nothing. Pinned so that collapsing the duplicates cannot be mistaken for deleting a rule.
        created.Errors.Should().NotContain(
            failure => failure.PropertyName == nameof(CreateModuleRequest.EndDate));
        updated.Errors.Should().NotContain(
            failure => failure.PropertyName == nameof(UpdateModuleRequest.StartDate));

        ValidationResult createdEndDate = new CreateModuleRequestValidator().Validate(new CreateModuleRequest
        {
            ModuleDefId = 1,
            TabId = 1,
            ModuleTitle = "Announcements",
            EndDate = BeforeTheStoredCalendar,
        });

        createdEndDate.Errors.Should().ContainSingle(
            failure => failure.PropertyName == nameof(CreateModuleRequest.EndDate),
            "the create path bounds the closing term too, and bounds it once");
    }

    /// <summary>One unstorable module term date produces exactly ONE failure, on each write path.</summary>
    [Fact]
    public void OneUnstorableModuleTermDate_ProducesExactlyOneFailure()
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
            TabId = 1,
            ModuleTitle = "Announcements",
            StartDate = BeforeTheStoredCalendar,
        });

        created.Errors
            .Count(failure => failure.PropertyName == nameof(CreateModuleRequest.StartDate))
            .Should().Be(1, "a rule declared twice cannot make a request less valid, only the answer longer");

        updated.Errors
            .Count(failure => failure.PropertyName == nameof(UpdateModuleRequest.StartDate))
            .Should().Be(1, "the update path must not acquire the duplication the create path carried");
    }

    /// <summary>
    /// Both module term properties carry the storage bound on both paths, and each carries it once.
    /// </summary>
    [Fact]
    public void ModuleTermDates_CarryTheStorageBoundOncePerProperty()
    {
        ValidationResult created = new CreateModuleRequestValidator().Validate(new CreateModuleRequest
        {
            ModuleDefId = 1,
            TabId = 1,
            ModuleTitle = "Announcements",
            StartDate = BeforeTheStoredCalendar,
            EndDate = BeforeTheStoredCalendar,
        });

        ValidationResult updated = new UpdateModuleRequestValidator().Validate(new UpdateModuleRequest
        {
            TabId = 1,
            ModuleTitle = "Announcements",
            StartDate = BeforeTheStoredCalendar,
            EndDate = BeforeTheStoredCalendar,
        });

        created.Errors.Should().HaveCount(2);
        created.Errors.Select(failure => failure.PropertyName).Should().BeEquivalentTo(
            new[] { nameof(CreateModuleRequest.StartDate), nameof(CreateModuleRequest.EndDate) });

        updated.Errors.Should().HaveCount(2);
        updated.Errors.Select(failure => failure.PropertyName).Should().BeEquivalentTo(
            new[] { nameof(UpdateModuleRequest.StartDate), nameof(UpdateModuleRequest.EndDate) });
    }

    /// <summary>Module term dates are deliberately still not compared to one another.</summary>
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

    /// <summary>A page carrying one term date and not the other is accepted.</summary>
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

    /// <summary>Builds a portal update that satisfies every rule except the one under test.</summary>
    /// <param name="fee">The hosting fee to submit.</param>
    /// <returns>The request.</returns>
    private static UpdatePortalRequest ValidPortalUpdate(decimal? fee = null) => new()
    {
        PortalName = "Measured Portal",
        HostFee = fee,
    };
}
