using DnnMigration.Application.Dtos.Role;
using DnnMigration.Application.Validation;
using DnnMigration.Domain.Enums;
using FluentAssertions;
using FluentValidation.Results;
using Xunit;

namespace DnnMigration.UnitTests.Validation;

/// <summary>
/// Covers the role-creation validator, including the paid-membership terms the legacy screen validated
/// and the two frequency fields whose stored representation is a single character.
/// </summary>
/// <remarks>
/// <para>
/// The interesting rules here are the conditional ones. Every paid-membership term is optional, and a
/// term that is absent is not the same as a term that is zero: an absent billing period means the role
/// is free, whereas a submitted zero is a contradiction the legacy screen refused. The tests below
/// assert both halves of that, because a rule written without the <c>When</c> guard would reject every
/// free role ever created.
/// </para>
/// <para>
/// The frequency fields are checked against the enumeration, and that check is load-bearing in a way the
/// other enumeration checks in this solution are not: <see cref="BillingFrequency"/> carries stored
/// characters as its member values, so the CLR default of zero is <em>not</em> a member. A cast of an
/// arbitrary integer therefore has to be refused explicitly rather than trusted.
/// </para>
/// </remarks>
public class CreateRoleRequestValidatorTests
{
    private const string RoleNameRequired = "Role Name Is Required.";

    private const string ServiceFeeNegative = "Service Fee Must Be Greater Than or Equal to Zero";

    private const string BillingPeriodNotPositive = "Billing Period Must Be Greater Than Zero";

    private const string TrialFeeNegative = "Trial Fee Must Be Greater Than or Equal to Zero";

    private const string TrialPeriodNotPositive = "Trial Period Must Be Greater Than Zero";

    private const string BillingFrequencyInvalid =
        "Billing Frequency must be one of None, One Time, Day, Week, Month or Year.";

    private const string TrialFrequencyInvalid =
        "Trial Frequency must be one of None, One Time, Day, Week, Month or Year.";

    private readonly CreateRoleRequestValidator _validator = new();

    /// <summary>
    /// A request naming nothing but the role is accepted, because every other field is optional.
    /// </summary>
    [Fact]
    public void ARequestNamingOnlyTheRole_IsAccepted()
    {
        ValidationResult result = _validator.Validate(new CreateRoleRequest { RoleName = "Subscribers" });

        result.IsValid.Should().BeTrue(Describe(result));
    }

    /// <summary>
    /// A fully specified paid role is accepted.
    /// </summary>
    [Fact]
    public void AFullySpecifiedPaidRole_IsAccepted()
    {
        CreateRoleRequest request = new()
        {
            RoleName = "Gold Members",
            Description = "Paid membership with a trial",
            IsPublic = true,
            AutoAssignment = false,
            ServiceFee = 19.99m,
            BillingPeriod = 1,
            BillingFrequency = BillingFrequency.Month,
            TrialFee = 0m,
            TrialPeriod = 14,
            TrialFrequency = BillingFrequency.Day,
            RsvpCode = "GOLD2026",
            IconFile = "gold.gif",
        };

        ValidationResult result = _validator.Validate(request);

        result.IsValid.Should().BeTrue(Describe(result));
    }

    /// <summary>
    /// The role name is required.
    /// </summary>
    /// <param name="roleName">The submitted name.</param>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void RoleName_IsRequired(string roleName)
    {
        Messages(new CreateRoleRequest { RoleName = roleName }).Should().Contain(RoleNameRequired);
    }

    /// <summary>
    /// The text fields are bounded by the columns that store them.
    /// </summary>
    [Fact]
    public void TextFields_AreBoundedByTheirColumns()
    {
        CreateRoleRequest atTheLimit = new()
        {
            RoleName = new string('a', 50),
            Description = new string('b', 1000),
            RsvpCode = new string('c', 50),
            IconFile = new string('d', 100),
        };

        ValidationResult accepted = _validator.Validate(atTheLimit);

        accepted.IsValid.Should().BeTrue(Describe(accepted));

        CreateRoleRequest overTheLimit = new()
        {
            RoleName = new string('a', 51),
            Description = new string('b', 1001),
            RsvpCode = new string('c', 51),
            IconFile = new string('d', 101),
        };

        Failures(overTheLimit, nameof(CreateRoleRequest.RoleName)).Should().ContainSingle();
        Failures(overTheLimit, nameof(CreateRoleRequest.Description)).Should().ContainSingle();
        Failures(overTheLimit, nameof(CreateRoleRequest.RsvpCode)).Should().ContainSingle();
        Failures(overTheLimit, nameof(CreateRoleRequest.IconFile)).Should().ContainSingle();
    }

    /// <summary>
    /// An over-long name reports only the length, because the first failure on a property stops the rest.
    /// </summary>
    [Fact]
    public void RoleName_ReportsOnlyItsFirstFailure()
    {
        IReadOnlyList<ValidationFailure> failures =
            Failures(new CreateRoleRequest { RoleName = new string('a', 51) }, nameof(CreateRoleRequest.RoleName));

        failures.Should().ContainSingle();
        failures[0].ErrorMessage.Should().NotBe(RoleNameRequired);
    }

    /// <summary>
    /// A fee of zero is a legitimate charge, and a negative one is not.
    /// </summary>
    [Fact]
    public void Fees_AdmitZeroAndRefuseNegatives()
    {
        CreateRoleRequest free = new() { RoleName = "Free", ServiceFee = 0m, TrialFee = 0m };

        _validator.Validate(free).IsValid.Should().BeTrue(
            "a role priced at nothing is a real configuration, not a missing value");

        CreateRoleRequest negative = new() { RoleName = "Refund", ServiceFee = -0.01m, TrialFee = -1m };

        IReadOnlyList<string> messages = Messages(negative);

        messages.Should().Contain(ServiceFeeNegative);
        messages.Should().Contain(TrialFeeNegative);
    }

    /// <summary>
    /// A billing period must be positive, so a submitted zero is refused.
    /// </summary>
    [Fact]
    public void Periods_MustBePositiveWhenSupplied()
    {
        CreateRoleRequest zeroed = new() { RoleName = "Nonsense", BillingPeriod = 0, TrialPeriod = 0 };

        IReadOnlyList<string> messages = Messages(zeroed);

        messages.Should().Contain(BillingPeriodNotPositive);
        messages.Should().Contain(TrialPeriodNotPositive);

        CreateRoleRequest negative = new() { RoleName = "Nonsense", BillingPeriod = -1, TrialPeriod = -1 };

        messages = Messages(negative);

        messages.Should().Contain(BillingPeriodNotPositive);
        messages.Should().Contain(TrialPeriodNotPositive);

        CreateRoleRequest positive = new() { RoleName = "Sensible", BillingPeriod = 1, TrialPeriod = 1 };

        _validator.Validate(positive).IsValid.Should().BeTrue();
    }

    /// <summary>
    /// An absent term is not a zero term: a free role submits nothing and passes.
    /// </summary>
    [Fact]
    public void AbsentTerms_AreNotZeroTerms()
    {
        CreateRoleRequest free = new() { RoleName = "Registered Users" };

        free.ServiceFee.Should().BeNull();
        free.BillingPeriod.Should().BeNull();
        free.BillingFrequency.Should().BeNull();
        free.TrialFee.Should().BeNull();
        free.TrialPeriod.Should().BeNull();
        free.TrialFrequency.Should().BeNull();

        _validator.Validate(free).IsValid.Should().BeTrue(
            "every paid-membership rule is guarded on the value being present, so a free role reaches no "
            + "rule at all - and a rule written without that guard would refuse every free role");
    }

    /// <summary>
    /// Each defined frequency is accepted for both the billing term and the trial term.
    /// </summary>
    /// <param name="frequency">The frequency under test.</param>
    [Theory]
    [InlineData(BillingFrequency.None)]
    [InlineData(BillingFrequency.OneTime)]
    [InlineData(BillingFrequency.Day)]
    [InlineData(BillingFrequency.Week)]
    [InlineData(BillingFrequency.Month)]
    [InlineData(BillingFrequency.Year)]
    public void Frequencies_AcceptEveryDefinedMember(BillingFrequency frequency)
    {
        CreateRoleRequest request = new()
        {
            RoleName = "Members",
            BillingFrequency = frequency,
            TrialFrequency = frequency,
        };

        ValidationResult result = _validator.Validate(request);

        result.IsValid.Should().BeTrue(Describe(result));
    }

    /// <summary>
    /// A value cast into the frequency type that is not a member is refused.
    /// </summary>
    /// <param name="rawValue">The raw value cast into the enumeration.</param>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(65)]
    [InlineData(122)]
    public void Frequencies_RefuseAValueThatIsNotAMember(int rawValue)
    {
        BillingFrequency notAMember = (BillingFrequency)rawValue;

        Enum.IsDefined(notAMember).Should().BeFalse("the test data must actually be outside the enumeration");

        CreateRoleRequest request = new()
        {
            RoleName = "Members",
            BillingFrequency = notAMember,
            TrialFrequency = notAMember,
        };

        IReadOnlyList<string> messages = Messages(request);

        messages.Should().Contain(BillingFrequencyInvalid);
        messages.Should().Contain(TrialFrequencyInvalid);
    }

    /// <summary>
    /// Zero is refused specifically, because it is the CLR default of the frequency type.
    /// </summary>
    [Fact]
    public void Frequencies_RefuseTheClrDefault()
    {
        CreateRoleRequest request = new()
        {
            RoleName = "Members",
            BillingFrequency = default(BillingFrequency),
        };

        Messages(request).Should().Contain(
            BillingFrequencyInvalid,
            "the frequency members carry stored characters rather than ordinals, so zero is not a member "
            + "and a caller that sent it has sent something the column cannot hold");
    }

    /// <summary>
    /// One bad request reports every bad field rather than only the first.
    /// </summary>
    [Fact]
    public void SeveralBadFields_AreAllReported()
    {
        CreateRoleRequest request = new()
        {
            RoleName = string.Empty,
            ServiceFee = -1m,
            BillingPeriod = 0,
            TrialFee = -1m,
            TrialPeriod = 0,
        };

        IReadOnlyList<string> messages = Messages(request);

        messages.Should().HaveCount(5);
        messages.Should().Contain(RoleNameRequired);
        messages.Should().Contain(ServiceFeeNegative);
        messages.Should().Contain(BillingPeriodNotPositive);
        messages.Should().Contain(TrialFeeNegative);
        messages.Should().Contain(TrialPeriodNotPositive);
    }

    /// <summary>
    /// The optional group reference is unconstrained, because zero is a real group key.
    /// </summary>
    /// <param name="roleGroupId">The submitted group key.</param>
    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    [InlineData(7)]
    public void RoleGroup_IsUnconstrained(int? roleGroupId)
    {
        CreateRoleRequest request = new() { RoleName = "Members", RoleGroupId = roleGroupId };

        ValidationResult result = _validator.Validate(request);

        result.IsValid.Should().BeTrue(
            Describe(result)
            + " - RoleGroupID is IDENTITY(0, 1), so a submitted zero names the first group of the "
            + "installation and cannot be rejected as though it were unset. Whether the group exists is "
            + "a question for the service, not for the validator");
    }

    /// <summary>
    /// Validates a request and returns the messages it reported.
    /// </summary>
    /// <param name="request">The request to validate.</param>
    /// <returns>The reported messages.</returns>
    private IReadOnlyList<string> Messages(CreateRoleRequest request)
        => _validator.Validate(request).Errors.Select(failure => failure.ErrorMessage).ToList();

    /// <summary>
    /// Validates a request and returns the failures reported against one property.
    /// </summary>
    /// <param name="request">The request to validate.</param>
    /// <param name="propertyName">The property to filter on.</param>
    /// <returns>The reported failures.</returns>
    private IReadOnlyList<ValidationFailure> Failures(CreateRoleRequest request, string propertyName)
        => _validator.Validate(request).Errors
            .Where(failure => string.Equals(failure.PropertyName, propertyName, StringComparison.Ordinal))
            .ToList();

    /// <summary>
    /// Renders a validation result for an assertion message.
    /// </summary>
    /// <param name="result">The result to render.</param>
    /// <returns>The rendered reason.</returns>
    private static string Describe(ValidationResult result)
        => "the request should have been accepted but reported: "
            + string.Join(" | ", result.Errors.Select(failure => failure.PropertyName + ": " + failure.ErrorMessage));
}
