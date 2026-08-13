using System.Globalization;
using DnnMigration.Application.Dtos.Role;
using DnnMigration.Application.Validation;
using DnnMigration.Domain.Enums;
using FluentAssertions;
using FluentValidation.Results;
using Xunit;

namespace DnnMigration.UnitTests.Validation;

/// <summary>
/// Proves rule-for-rule parity between <see cref="CreateRoleRequestValidator"/> and the nine declarative
/// validators on the legacy role-edit screen <c>Website/admin/Security/editroles.ascx</c>.
/// </summary>
/// <remarks>
/// <para>
/// The load-bearing behaviour on this screen is what happens at exactly zero, because the two rule families
/// diverge there and nowhere else: a fee of zero is a legitimate price - it is how the legacy screen
/// expressed "this role is free", tested as such at <c>EditRoles.ascx.vb</c> L146 - whereas a period of
/// zero is a contradiction, since a billing cycle of no units could never advance an expiry date.
/// </para>
/// <para>
/// The second load-bearing behaviour is absence. Every one of the eight legacy comparisons was a
/// <c>CompareValidator</c>, which SUCCEEDS against an empty control because it compares nothing when there
/// is nothing to compare, and that is precisely why the name carries the screen's only presence check.
/// </para>
/// </remarks>
public class CreateRoleRequestValidatorTests
{
    /// <summary>
    /// Wording of <c>valRoleName</c>, the screen's only presence check, with its leading markup tag
    /// removed.
    /// </summary>
    private const string RoleNameRequired = "You Must Enter a Valid Name";

    /// <summary>
    /// Wording of <c>valServiceFee2</c>. Message and operator agree: the operator at L96 admits zero and
    /// the text says "or Equal to".
    /// </summary>
    private const string ServiceFeeNegative = "Service Fee Must Be Greater Than or Equal to Zero";

    /// <summary>
    /// Wording of <c>valBillingPeriod2</c>, carried across UNCHANGED even though it contradicts the
    /// operator it accompanies.
    /// </summary>
    private const string BillingPeriodNotPositive = "Billing Period Must Be Greater Than Zero";

    /// <summary>
    /// Wording of <c>valTrialFee2</c>, carried across UNCHANGED even though it contradicts the operator it
    /// accompanies.
    /// </summary>
    private const string TrialFeeNegative = "Trial Fee Must Be Greater Than or Equal to Zero";

    /// <summary>
    /// Wording of <c>valTrialPeriod2</c>. Message and operator agree: the operator at L146 is strictly
    /// greater than zero and the text says so.
    /// </summary>
    private const string TrialPeriodNotPositive = "Trial Period Must Be Greater Than Zero";

    /// <summary>Reported when a submitted billing frequency is outside the domain enumeration.</summary>
    private const string BillingFrequencyInvalid =
        "Billing Frequency must be one of None, One Time, Day, Week, Month or Year.";

    /// <summary>Reported when a submitted trial frequency is outside the domain enumeration.</summary>
    private const string TrialFrequencyInvalid =
        "Trial Frequency must be one of None, One Time, Day, Week, Month or Year.";

    /// <summary>Reported when a submitted icon path is rooted, volume-qualified or traverses upwards.</summary>
    private const string IconFileNotRelative =
        "Icon File must be a relative path within the portal's own folder.";

    /// <summary>
    /// Width of <c>Roles.RoleName nvarchar(50) NOT NULL</c> (<c>01.00.00.SqlDataProvider</c> L117, carried
    /// unchanged through the table rebuild at <c>01.00.05.SqlDataProvider</c> L2750).
    /// </summary>
    private const int RoleNameWidth = 50;

    /// <summary>
    /// Width of <c>Roles.Description nvarchar(1000) NULL</c> (<c>01.00.00.SqlDataProvider</c> L118).
    /// </summary>
    private const int DescriptionWidth = 1000;

    /// <summary>
    /// Width of <c>Roles.RSVPCode nvarchar(50) NULL</c>, added to the table alongside the icon column by
    /// <c>03.02.03.SqlDataProvider</c> L45 and never widened afterwards.
    /// </summary>
    private const int RsvpCodeWidth = 50;

    /// <summary>Width of <c>Roles.IconFile nvarchar(100) NULL</c> (<c>03.02.03.SqlDataProvider</c> L45).</summary>
    private const int IconFileWidth = 100;

    /// <summary>
    /// The subject. Constructed directly and once, because the validator takes no constructor argument,
    /// reads no configuration and touches no store - there is nothing here to mock.
    /// </summary>
    private readonly CreateRoleRequestValidator _validator = new();

    /// <summary>Builds the minimum request the legacy screen would have accepted: a name and nothing else.</summary>
    /// <returns>A request that must validate.</returns>
    private static CreateRoleRequest ValidRequest() => new()
    {
        RoleName = "Subscribers",
    };

    /// <summary>
    /// Builds an invitation code of an exact length that satisfies every rule EXCEPT a width bound.
    /// </summary>
    /// <param name="length">The length the value must have.</param>
    /// <returns>A code of exactly that length, mixing letters with digits.</returns>
    private static string StrongCodeOfLength(int length)
        => string.Concat(Enumerable.Range(0, length).Select(index => index % 2 == 0 ? 'a' : '1'));
    /// <summary>
    /// Renders the framework's own length message for a property, so the expectation cannot drift from the
    /// validator's wording.
    /// </summary>
    /// <param name="displayName">
    /// The property's display name, which the framework derives by splitting its PascalCase identifier into
    /// words.
    /// </param>
    /// <param name="ceiling">The declared maximum length.</param>
    /// <param name="submitted">The length actually submitted, which the message quotes back.</param>
    /// <returns>The rendered message.</returns>
    /// <remarks>
    /// The validator does not override the message for any of its four length rules, so the framework
    /// default IS the contract and is reproduced here rather than approximated.
    /// </remarks>
    private static string LengthExceeded(string displayName, int ceiling, int submitted)
        => string.Format(
            CultureInfo.InvariantCulture,
            "The length of '{0}' must be {1} characters or fewer. You entered {2} characters.",
            displayName,
            ceiling,
            submitted);

    /// <summary>
    /// Asserts that a result reports the given message against the given property, character for character.
    /// </summary>
    /// <param name="result">The result to inspect.</param>
    /// <param name="property">The property the failure must name.</param>
    /// <param name="message">The message the failure must carry.</param>
    private static void ShouldReport(ValidationResult result, string property, string message)
    {
        result.IsValid.Should().BeFalse("a failure was expected for " + property);
        result.Errors.Should().Contain(
            failure => failure.PropertyName == property && failure.ErrorMessage == message,
            "the failure for {0} must carry exactly the legacy wording, but the result was {1}",
            property,
            Render(result));
    }

    /// <summary>Asserts that a result reports nothing at all against the given property.</summary>
    /// <param name="result">The result to inspect.</param>
    /// <param name="property">The property that must be unreported.</param>
    private static void ShouldNotReport(ValidationResult result, string property)
        => result.Errors.Should().NotContain(
            failure => failure.PropertyName == property,
            "no failure was expected for {0}, but the result was {1}",
            property,
            Render(result));

    /// <summary>Asserts that a result reports nothing whatsoever.</summary>
    /// <param name="result">The result to inspect.</param>
    private static void ShouldAccept(ValidationResult result)
    {
        result.IsValid.Should().BeTrue(Render(result));
        result.Errors.Should().BeEmpty(Render(result));
    }

    /// <summary>Renders a validation result for an assertion message.</summary>
    /// <param name="result">The result to render.</param>
    /// <returns>The rendered reason.</returns>
    private static string Render(ValidationResult result) => result.Errors.Count == 0
        ? "the result reported nothing"
        : "the result reported "
            + string.Join(" | ", result.Errors.Select(failure => failure.PropertyName + ": " + failure.ErrorMessage));

    // ACCEPTANCE BASELINES
    // These come first because they are the cleanest possible proof of the
    // CompareValidator-succeeds-on-empty semantics, and because every rule test below mutates one field of
    // the same minimal request.

    /// <summary>
    /// A request naming nothing but the role is accepted, because the name carries the screen's only
    /// presence check and every other rule is guarded on its value being present.
    /// </summary>
    [Fact]
    public async Task ARequestNamingOnlyTheRole_IsAccepted()
    {
        ValidationResult result = await _validator.ValidateAsync(ValidRequest());

        ShouldAccept(result);
    }

    /// <summary>
    /// The minimal request really does leave every optional term absent, so the acceptance above is
    /// evidence about the rules rather than about a factory that quietly supplied defaults.
    /// </summary>
    [Fact]
    public void TheMinimalRequest_LeavesEveryOptionalTermAbsent()
    {
        CreateRoleRequest request = ValidRequest();

        request.Description.Should().BeNull();
        request.ServiceFee.Should().BeNull();
        request.BillingPeriod.Should().BeNull();
        request.BillingFrequency.Should().BeNull();
        request.TrialFee.Should().BeNull();
        request.TrialPeriod.Should().BeNull();
        request.TrialFrequency.Should().BeNull();
        request.RoleGroupId.Should().BeNull();
        request.RsvpCode.Should().BeNull();
        request.IconFile.Should().BeNull();
    }

    /// <summary>A fully specified paid role with a free trial is accepted, exercising every member at once.</summary>
    [Fact]
    public async Task AFullySpecifiedPaidRole_IsAccepted()
    {
        CreateRoleRequest request = new()
        {
            RoleName = "Gold Members",
            Description = "Paid membership with a free trial",
            IsPublic = true,
            AutoAssignment = false,
            ServiceFee = 19.99m,
            BillingPeriod = 1,
            BillingFrequency = BillingFrequency.Month,
            TrialFee = 0m,
            TrialPeriod = 14,
            TrialFrequency = BillingFrequency.Day,
            RoleGroupId = 3,
            // Strong enough for the AUTHORING rule: twelve characters or more, mixing letters with digits.
            // A shorter code such as "GOLD-2026" is what the legacy screen accepted and is still
            // redeemable, but it can no longer be authored - see the strength facts below.
            RsvpCode = "GOLD-2026-ALPHA",
            IconFile = "icons/gold.gif",
        };

        ValidationResult result = await _validator.ValidateAsync(request);

        ShouldAccept(result);
    }

    /// <summary>The role name is required, and a name of nothing but spaces counts as no name.</summary>
    /// <param name="roleName">The submitted name.</param>
    /// <remarks>
    /// A whitespace-only name is treated as absent, which is a documented narrowing rather than a
    /// reproduction: the legacy <c>RequiredFieldValidator</c> trimmed before testing, so it too refused a
    /// name of spaces, but it would have accepted one padded with spaces and stored the padding. The
    /// migrated rule refuses the empty case identically and leaves padding to the caller.
    /// </remarks>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t")]
    public async Task RoleName_IsRequired(string? roleName)
    {
        CreateRoleRequest request = ValidRequest();
        request.RoleName = roleName!;

        ValidationResult result = await _validator.ValidateAsync(request);

        ShouldReport(result, nameof(CreateRoleRequest.RoleName), RoleNameRequired);

        // One omission, one message. The rule chains a presence check and a width check, and the rule-level
        // cascade stops at the first failure, so a missing name must not also be reported as an over-long
        // one - a caller reading two messages about one blank field cannot tell which describes the fault.
        result.Errors
            .Where(failure => failure.PropertyName == nameof(CreateRoleRequest.RoleName))
            .Should().ContainSingle(Render(result));
    }

    /// <summary>The role name is bounded at the width of the column that stores it.</summary>
    /// <param name="length">The submitted length.</param>
    /// <param name="accepted">Whether that length is within the ceiling.</param>
    /// <remarks>
    /// Two sources agree on fifty here, which is why it is asserted as a hard boundary: the terminal column
    /// is <c>RoleName nvarchar(50) NOT NULL</c> and the legacy text box declared <c>MaxLength="50"</c>. The
    /// markup limit vanished with the postback, so the column width is now the only thing enforcing it and
    /// the rule has to.
    /// </remarks>
    [Theory]
    [InlineData(49, true)]
    [InlineData(50, true)]
    [InlineData(51, false)]
    public async Task RoleName_IsBoundedByItsColumnWidth(int length, bool accepted)
    {
        CreateRoleRequest request = ValidRequest();
        request.RoleName = new string('a', length);

        ValidationResult result = await _validator.ValidateAsync(request);

        if (accepted)
        {
            ShouldAccept(result);
        }
        else
        {
            ShouldReport(
                result,
                nameof(CreateRoleRequest.RoleName),
                LengthExceeded("Role Name", RoleNameWidth, length));
        }
    }

    /// <summary>
    /// An over-long name reports the length and nothing else, so a caller reads one actionable message per
    /// field rather than a presence complaint stacked underneath a length complaint.
    /// </summary>
    [Fact]
    public async Task RoleName_WhenTooLong_ReportsOnlyTheLengthFailure()
    {
        CreateRoleRequest request = ValidRequest();
        request.RoleName = new string('a', RoleNameWidth + 1);

        ValidationResult result = await _validator.ValidateAsync(request);

        result.Errors.Should().ContainSingle(Render(result));
        result.Errors.Should().NotContain(
            failure => failure.ErrorMessage == RoleNameRequired,
            "a name that is too long is still a name, so the presence message must not also fire");
    }

    // Description - NO legacy validator of any kind
    // The screen declared none, so the only bound is the column's own width and there is deliberately no
    // presence rule.

    /// <summary>The description is bounded at the width of the column that stores it.</summary>
    /// <param name="length">The submitted length.</param>
    /// <param name="accepted">Whether that length is within the ceiling.</param>
    /// <remarks>
    /// As with the name, both the terminal column - <c>Description nvarchar(1000) NULL</c> - and the legacy
    /// text box's <c>MaxLength="1000"</c> agree on the figure.
    /// </remarks>
    [Theory]
    [InlineData(999, true)]
    [InlineData(1000, true)]
    [InlineData(1001, false)]
    public async Task Description_IsBoundedByItsColumnWidth(int length, bool accepted)
    {
        CreateRoleRequest request = ValidRequest();
        request.Description = new string('b', length);

        ValidationResult result = await _validator.ValidateAsync(request);

        if (accepted)
        {
            ShouldAccept(result);
        }
        else
        {
            ShouldReport(
                result,
                nameof(CreateRoleRequest.Description),
                LengthExceeded("Description", DescriptionWidth, length));
        }
    }

    /// <summary>
    /// A description that is absent, or present but empty, reports nothing - there is no presence rule on
    /// it, because the legacy screen declared none.
    /// </summary>
    /// <param name="description">The submitted description.</param>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task Description_WhenAbsentOrEmpty_ReportsNothing(string? description)
    {
        CreateRoleRequest request = ValidRequest();
        request.Description = description;

        ValidationResult result = await _validator.ValidateAsync(request);

        ShouldAccept(result);
        ShouldNotReport(result, nameof(CreateRoleRequest.Description));
    }

    // THE FOUR ZERO BOUNDARIES

    /// <summary>
    /// A service fee of exactly zero is accepted, because zero is how the legacy screen expressed "this
    /// role is free" rather than how it expressed "this value is missing".
    /// </summary>
    [Fact]
    public async Task ServiceFee_AtExactlyZero_IsAccepted()
    {
        CreateRoleRequest request = ValidRequest();
        request.ServiceFee = 0m;

        ValidationResult result = await _validator.ValidateAsync(request);

        ShouldAccept(result);
        ShouldNotReport(result, nameof(CreateRoleRequest.ServiceFee));
    }

    /// <summary>
    /// A trial fee of exactly zero is accepted - a free trial is a real configuration - even though the
    /// message the caller would see for a negative value says otherwise.
    /// </summary>
    [Fact]
    public async Task TrialFee_AtExactlyZero_IsAccepted()
    {
        CreateRoleRequest request = ValidRequest();
        request.TrialFee = 0m;

        ValidationResult result = await _validator.ValidateAsync(request);

        ShouldAccept(result);
        ShouldNotReport(result, nameof(CreateRoleRequest.TrialFee));
    }

    /// <summary>A billing period of exactly zero is refused when a recurring cycle is declared beside it.</summary>
    [Fact]
    public async Task BillingPeriod_AtExactlyZero_IsRefused()
    {
        CreateRoleRequest request = ValidRequest();
        request.BillingPeriod = 0;
        request.BillingFrequency = BillingFrequency.Month;

        ValidationResult result = await _validator.ValidateAsync(request);

        ShouldReport(result, nameof(CreateRoleRequest.BillingPeriod), BillingPeriodNotPositive);
    }

    /// <summary>A billing period of exactly zero is ACCEPTED when no recurring cycle is declared beside it.</summary>
    [Fact]
    public async Task BillingPeriod_AtExactlyZero_WithNoCycle_IsAccepted()
    {
        CreateRoleRequest request = ValidRequest();
        request.BillingPeriod = 0;
        request.BillingFrequency = BillingFrequency.None;

        ValidationResult result = await _validator.ValidateAsync(request);

        ShouldAccept(result);
        ShouldNotReport(result, nameof(CreateRoleRequest.BillingPeriod));
    }

    /// <summary>A trial period of exactly zero is refused, and here message and operator agree.</summary>
    [Fact]
    public async Task TrialPeriod_AtExactlyZero_IsRefused()
    {
        CreateRoleRequest request = ValidRequest();
        request.TrialPeriod = 0;
        request.TrialFrequency = BillingFrequency.Month;

        ValidationResult result = await _validator.ValidateAsync(request);

        ShouldReport(result, nameof(CreateRoleRequest.TrialPeriod), TrialPeriodNotPositive);
    }

    /// <summary>
    /// A trial period of exactly zero is ACCEPTED when no trial cycle is declared beside it, matching the
    /// billing pair and matching what the portal template's own roles carry.
    /// </summary>
    [Fact]
    public async Task TrialPeriod_AtExactlyZero_WithNoCycle_IsAccepted()
    {
        CreateRoleRequest request = ValidRequest();
        request.TrialPeriod = 0;
        request.TrialFrequency = BillingFrequency.None;

        ValidationResult result = await _validator.ValidateAsync(request);

        ShouldAccept(result);
        ShouldNotReport(result, nameof(CreateRoleRequest.TrialPeriod));
    }

    // THE FOUR COMPARISONS AWAY FROM ZERO
    // Negative values are refused by all four rules; one unit is accepted by all four. Every submitted
    // amount below is written as an invariant-culture literal - see the note on the decimal test data.

    /// <summary>A negative service fee is refused.</summary>
    /// <param name="serviceFee">The submitted amount, written in the invariant culture.</param>
    [Theory]
    [InlineData("-0.01")]
    [InlineData("-1")]
    [InlineData("-19.99")]
    public async Task ServiceFee_WhenNegative_IsRefused(string serviceFee)
    {
        CreateRoleRequest request = ValidRequest();
        request.ServiceFee = decimal.Parse(serviceFee, CultureInfo.InvariantCulture);

        ValidationResult result = await _validator.ValidateAsync(request);

        ShouldReport(result, nameof(CreateRoleRequest.ServiceFee), ServiceFeeNegative);
    }

    /// <summary>
    /// A negative trial fee is refused, and the message it reports is the contradictory legacy wording.
    /// </summary>
    /// <param name="trialFee">The submitted amount, written in the invariant culture.</param>
    [Theory]
    [InlineData("-0.01")]
    [InlineData("-1")]
    public async Task TrialFee_WhenNegative_IsRefusedWithItsContradictoryLegacyWording(string trialFee)
    {
        CreateRoleRequest request = ValidRequest();
        request.TrialFee = decimal.Parse(trialFee, CultureInfo.InvariantCulture);

        ValidationResult result = await _validator.ValidateAsync(request);

        ShouldReport(result, nameof(CreateRoleRequest.TrialFee), TrialFeeNegative);
    }

    /// <summary>A negative billing period is refused.</summary>
    /// <param name="billingPeriod">The submitted period.</param>
    [Theory]
    [InlineData(-1)]
    [InlineData(-12)]
    public async Task BillingPeriod_WhenNegative_IsRefused(int billingPeriod)
    {
        CreateRoleRequest request = ValidRequest();
        request.BillingPeriod = billingPeriod;

        ValidationResult result = await _validator.ValidateAsync(request);

        ShouldReport(result, nameof(CreateRoleRequest.BillingPeriod), BillingPeriodNotPositive);
    }

    /// <summary>A negative trial period is refused.</summary>
    /// <param name="trialPeriod">The submitted period.</param>
    [Theory]
    [InlineData(-1)]
    [InlineData(-12)]
    public async Task TrialPeriod_WhenNegative_IsRefused(int trialPeriod)
    {
        CreateRoleRequest request = ValidRequest();
        request.TrialPeriod = trialPeriod;

        ValidationResult result = await _validator.ValidateAsync(request);

        ShouldReport(result, nameof(CreateRoleRequest.TrialPeriod), TrialPeriodNotPositive);
    }

    /// <summary>
    /// One unit satisfies both period rules, which is the value the legacy code-behind itself defaulted to.
    /// </summary>
    [Fact]
    public async Task BothPeriods_AtOneUnit_AreAccepted()
    {
        CreateRoleRequest request = ValidRequest();
        request.BillingPeriod = 1;
        request.TrialPeriod = 1;

        ValidationResult result = await _validator.ValidateAsync(request);

        ShouldAccept(result);
    }

    /// <summary>
    /// A five-figure fee is accepted, because the terminal column is a money type rather than the narrow
    /// scaled decimal the baseline script declared.
    /// </summary>
    /// <param name="fee">The submitted amount, written in the invariant culture.</param>
    /// <remarks>
    /// The general lesson is worth recording: this upgrade chain evolves tables by creating a temporary
    /// twin, copying rows, dropping the original and renaming, so "there is no ALTER COLUMN for this
    /// column, therefore the baseline declaration stands" is an unsound inference.
    /// </remarks>
    [Theory]
    [InlineData("1000.00")]
    [InlineData("12345.67")]
    [InlineData("1234567.8912")]
    public async Task BothFees_FarAboveTheBaselineCeiling_AreAccepted(string fee)
    {
        decimal amount = decimal.Parse(fee, CultureInfo.InvariantCulture);
        CreateRoleRequest request = ValidRequest();
        request.ServiceFee = amount;
        request.TrialFee = amount;

        ValidationResult result = await _validator.ValidateAsync(request);

        ShouldAccept(result);
    }

    /// <summary>
    /// Every fee and period rule is silent when its value is absent, which is the whole reason the legacy
    /// screen needed only one presence check.
    /// </summary>
    [Fact]
    public async Task EveryPaidMembershipRule_IsSilentWhenItsValueIsAbsent()
    {
        CreateRoleRequest request = ValidRequest();
        request.ServiceFee = null;
        request.BillingPeriod = null;
        request.TrialFee = null;
        request.TrialPeriod = null;

        ValidationResult result = await _validator.ValidateAsync(request);

        ShouldAccept(result);
        ShouldNotReport(result, nameof(CreateRoleRequest.ServiceFee));
        ShouldNotReport(result, nameof(CreateRoleRequest.BillingPeriod));
        ShouldNotReport(result, nameof(CreateRoleRequest.TrialFee));
        ShouldNotReport(result, nameof(CreateRoleRequest.TrialPeriod));
    }

    // THE TWO FREQUENCY MEMBERS

    /// <summary>Every declared member is accepted for the billing term.</summary>
    /// <param name="frequency">The frequency under test.</param>
    [Theory]
    [InlineData(BillingFrequency.None)]
    [InlineData(BillingFrequency.OneTime)]
    [InlineData(BillingFrequency.Day)]
    [InlineData(BillingFrequency.Week)]
    [InlineData(BillingFrequency.Month)]
    [InlineData(BillingFrequency.Year)]
    public async Task BillingFrequency_AcceptsEveryDeclaredMember(BillingFrequency frequency)
    {
        CreateRoleRequest request = ValidRequest();
        request.BillingFrequency = frequency;

        ValidationResult result = await _validator.ValidateAsync(request);

        ShouldAccept(result);
    }

    /// <summary>
    /// Every declared member is accepted for the trial term as well, because one member list serves both
    /// columns.
    /// </summary>
    /// <param name="frequency">The frequency under test.</param>
    /// <remarks>
    /// The schema joins the same frequency list twice from a single role row, once per column, so a
    /// separate trial-specific type would have no source. Asserting the full set against both members is
    /// what proves the two rules were authored symmetrically.
    /// </remarks>
    [Theory]
    [InlineData(BillingFrequency.None)]
    [InlineData(BillingFrequency.OneTime)]
    [InlineData(BillingFrequency.Day)]
    [InlineData(BillingFrequency.Week)]
    [InlineData(BillingFrequency.Month)]
    [InlineData(BillingFrequency.Year)]
    public async Task TrialFrequency_AcceptsEveryDeclaredMember(BillingFrequency frequency)
    {
        CreateRoleRequest request = ValidRequest();
        request.TrialFrequency = frequency;

        ValidationResult result = await _validator.ValidateAsync(request);

        ShouldAccept(result);
    }

    /// <summary>
    /// Both frequency members may be absent, because both columns are nullable and the legacy screen's
    /// default selection was merely a default rather than a requirement.
    /// </summary>
    [Fact]
    public async Task BothFrequencies_MayBeAbsent()
    {
        CreateRoleRequest request = ValidRequest();
        request.BillingFrequency = null;
        request.TrialFrequency = null;

        ValidationResult result = await _validator.ValidateAsync(request);

        ShouldAccept(result);
        ShouldNotReport(result, nameof(CreateRoleRequest.BillingFrequency));
        ShouldNotReport(result, nameof(CreateRoleRequest.TrialFrequency));
    }

    /// <summary>
    /// A value cast into the frequency type that is not one of its members is refused, for both members.
    /// </summary>
    /// <param name="rawValue">The raw numeric value cast into the enumeration.</param>
    /// <remarks>
    /// This is the rule that makes the enumeration itself the constraint, and it is load-bearing here in a
    /// way that an enumeration check usually is not: the members carry the code points of the legacy stored
    /// characters rather than a zero-based sequence, so an arbitrary cast is not merely unlikely to be a
    /// member - the ordinary default is not one either.
    /// </remarks>
    [Theory]
    [InlineData(1)]
    [InlineData(65)]
    [InlineData(122)]
    [InlineData(9999)]
    public async Task BothFrequencies_RefuseAValueOutsideTheEnumeration(int rawValue)
    {
        BillingFrequency outsider = (BillingFrequency)rawValue;
        Enum.IsDefined(outsider).Should().BeFalse("the test data must actually be outside the enumeration");

        CreateRoleRequest request = ValidRequest();
        request.BillingFrequency = outsider;
        request.TrialFrequency = outsider;

        ValidationResult result = await _validator.ValidateAsync(request);

        ShouldReport(result, nameof(CreateRoleRequest.BillingFrequency), BillingFrequencyInvalid);
        ShouldReport(result, nameof(CreateRoleRequest.TrialFrequency), TrialFrequencyInvalid);
    }

    /// <summary>
    /// The ordinary default of the frequency type is refused, which is the boundary a caller is most likely
    /// to reach by accident.
    /// </summary>
    /// <remarks>
    /// Because every member carries a stored character's code point, no member has the value zero, so the
    /// type's default is not a member. A caller that sends it has sent something the column cannot hold,
    /// and a deserialiser that filled the member in rather than leaving it absent would be caught here.
    /// </remarks>
    [Fact]
    public async Task BothFrequencies_RefuseTheDefaultOfTheirType()
    {
        Enum.IsDefined(default(BillingFrequency)).Should().BeFalse(
            "no member may carry the value zero, because every member carries a stored character");

        CreateRoleRequest request = ValidRequest();
        request.BillingFrequency = default(BillingFrequency);
        request.TrialFrequency = default(BillingFrequency);

        ValidationResult result = await _validator.ValidateAsync(request);

        ShouldReport(result, nameof(CreateRoleRequest.BillingFrequency), BillingFrequencyInvalid);
        ShouldReport(result, nameof(CreateRoleRequest.TrialFrequency), TrialFrequencyInvalid);
    }

    /// <summary>
    /// The two frequency codes that DotNetNuke itself seeds are refused on the CREATE path, which pins the
    /// one genuine limitation of modelling the column as a closed enumeration.
    /// </summary>
    /// <param name="seededCode">The seeded character, exactly as it sits in the shipped baseline data.</param>
    /// <remarks>
    /// The consequence must not be misread as licence to weaken this rule. Tolerance for the two SEEDED
    /// ROWS belongs on the READ path, in the domain enumeration and the role mapper; loosening the rule
    /// here would not rescue a single existing row, it would only permit new rows to be created at an
    /// uninterpretable code.
    /// </remarks>
    // The shipped seed data carries billing codes OUTSIDE the six documented members
    // 01.00.00.SqlDataProvider L7192 seeds the Administrators role with the digit four and L7194 seeds the
    // Registered Users role with the digit zero - and no CHECK constraint anywhere in the 88-script chain
    // restricts either column, so the database accepts them.
    [Theory]
    [InlineData('4')]
    [InlineData('0')]
    public async Task BothFrequencies_RefuseTheShippedSeedCodesOnTheCreatePath(char seededCode)
    {
        BillingFrequency seeded = (BillingFrequency)seededCode;
        Enum.IsDefined(seeded).Should().BeFalse(
            "the shipped seed codes lie outside the six documented members, which is the entire point");

        CreateRoleRequest request = ValidRequest();
        request.BillingFrequency = seeded;
        request.TrialFrequency = seeded;

        ValidationResult result = await _validator.ValidateAsync(request);

        ShouldReport(result, nameof(CreateRoleRequest.BillingFrequency), BillingFrequencyInvalid);
        ShouldReport(result, nameof(CreateRoleRequest.TrialFrequency), TrialFrequencyInvalid);
    }

    /// <summary>
    /// Each declared member still converts to the single character the column stores, which is what makes a
    /// member rename a data corruption rather than a refactor.
    /// </summary>
    /// <param name="frequency">The member under test.</param>
    /// <param name="storedCharacter">The character the column holds for it.</param>
    [Theory]
    [InlineData(BillingFrequency.None, 'N')]
    [InlineData(BillingFrequency.OneTime, 'O')]
    [InlineData(BillingFrequency.Day, 'D')]
    [InlineData(BillingFrequency.Week, 'W')]
    [InlineData(BillingFrequency.Month, 'M')]
    [InlineData(BillingFrequency.Year, 'Y')]
    public void EachDeclaredMember_StillCarriesItsStoredCharacter(
        BillingFrequency frequency,
        char storedCharacter)
    {
        ((char)frequency).Should().Be(
            storedCharacter,
            "the member values are the persisted contract, so changing one would silently mis-read live rows");
    }

    // THE INVITATION CODE AND THE ICON PATH
    // Neither carried a legacy validator. The invitation code therefore has its column width and nothing
    // else; the icon path has its width plus one NET-NEW containment rule, annotated as such because it is
    // the only rule in the validator without a legacy ancestor.

    /// <summary>
    /// The invitation code is bounded at the width of the column that stores it, and nothing more is
    /// asserted about it.
    /// </summary>
    /// <param name="length">The submitted length.</param>
    /// <param name="accepted">Whether that length is within the ceiling.</param>
    /// <remarks>
    /// No legacy validator was declared on the invitation-code box and no index makes the value unique, so
    /// a clash between two roles is not a conflict and there is deliberately no uniqueness rule to
    /// reproduce.
    /// </remarks>
    [Theory]
    [InlineData(49, true)]
    [InlineData(50, true)]
    [InlineData(51, false)]
    public async Task RsvpCode_IsBoundedByItsColumnWidth(int length, bool accepted)
    {
        CreateRoleRequest request = ValidRequest();
        request.RsvpCode = StrongCodeOfLength(length);

        ValidationResult result = await _validator.ValidateAsync(request);

        if (accepted)
        {
            ShouldAccept(result);
        }
        else
        {
            ShouldReport(
                result,
                nameof(CreateRoleRequest.RsvpCode),
                LengthExceeded("Rsvp Code", RsvpCodeWidth, length));
        }
    }

    /// <summary>A newly authored invitation code must be long enough and varied enough not to be guessable.</summary>
    /// <param name="submitted">The code the issuer typed.</param>
    /// <param name="accepted">Whether the rule admits it.</param>
    /// <remarks>
    /// SEC: THE OTHER HALF OF CLOSING A GUESSING ORACLE. Redemption compares a submitted code against every
    /// role of the tenant and grants membership of every role that bears it, so the cost of guessing is set
    /// by two things together - how fast an attacker may try, which the endpoint's own window now bounds,
    /// and how large the space it is trying against is, which is this rule.
    /// </remarks>
    [Theory]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData("JOIN", false)]
    [InlineData("JOIN2008", false)]
    [InlineData("abcdefghijk1", true)]
    [InlineData("abcdefghijk", false)]
    [InlineData("abcdefghijklmnop", false)]
    [InlineData("1234567890123456", false)]
    [InlineData("GOLD-2026-ALPHA", true)]
    [InlineData("Founders-Circle", true)]
    public async Task RsvpCode_MustBeHardEnoughToGuessWhenItIsAuthored(string? submitted, bool accepted)
    {
        CreateRoleRequest request = ValidRequest();
        request.RsvpCode = submitted;

        ValidationResult result = await _validator.ValidateAsync(request);

        if (accepted)
        {
            ShouldAccept(result);
        }
        else
        {
            ShouldReport(
                result,
                nameof(CreateRoleRequest.RsvpCode),
                "An RSVP Code must be at least 12 characters long and must mix letters with digits or punctuation.");
        }
    }

    /// <summary>The icon path is bounded at the width of the column that stores it.</summary>
    /// <param name="length">The submitted length.</param>
    /// <param name="accepted">Whether that length is within the ceiling.</param>
    [Theory]
    [InlineData(99, true)]
    [InlineData(100, true)]
    [InlineData(101, false)]
    public async Task IconFile_IsBoundedByItsColumnWidth(int length, bool accepted)
    {
        CreateRoleRequest request = ValidRequest();
        request.IconFile = new string('d', length);

        ValidationResult result = await _validator.ValidateAsync(request);

        if (accepted)
        {
            ShouldAccept(result);
        }
        else
        {
            ShouldReport(
                result,
                nameof(CreateRoleRequest.IconFile),
                LengthExceeded("Icon File", IconFileWidth, length));
        }
    }

    /// <summary>A relative icon path is accepted, in every form the legacy picker could have produced.</summary>
    /// <param name="iconFile">The submitted path.</param>
    [Theory]
    [InlineData("gold.gif")]
    [InlineData("icons/gold.gif")]
    [InlineData("icons\\gold.gif")]
    [InlineData("Images/Roles/gold-members.png")]
    public async Task IconFile_WhenRelative_IsAccepted(string iconFile)
    {
        CreateRoleRequest request = ValidRequest();
        request.IconFile = iconFile;

        ValidationResult result = await _validator.ValidateAsync(request);

        ShouldAccept(result);
    }

    /// <summary>A rooted, volume-qualified or upward-traversing icon path is refused.</summary>
    /// <param name="iconFile">The submitted path.</param>
    /// <remarks>
    /// This rule is the validator's only NET-NEW rule and is asserted here as such rather than as parity.
    /// </remarks>
    // MIGRATION: NET-NEW rule with no legacy ancestor. editroles.ascx declares no validator on its icon
    // picker; the code-behind restricted the choice by file type instead and stored the control's value
    // verbatim.
    [Theory]
    [InlineData("/etc/passwd")]
    [InlineData("\\\\server\\share\\icon.gif")]
    [InlineData("C:\\Windows\\icon.gif")]
    [InlineData("../../secrets.txt")]
    [InlineData("icons/../../secrets.txt")]
    [InlineData("http://example.test/icon.gif")]
    public async Task IconFile_WhenRootedOrTraversing_IsRefused(string iconFile)
    {
        CreateRoleRequest request = ValidRequest();
        request.IconFile = iconFile;

        ValidationResult result = await _validator.ValidateAsync(request);

        ShouldReport(result, nameof(CreateRoleRequest.IconFile), IconFileNotRelative);
    }

    /// <summary>An absent or empty icon path reports nothing, because no icon is the normal case.</summary>
    /// <param name="iconFile">The submitted path.</param>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task IconFile_WhenAbsentOrEmpty_ReportsNothing(string? iconFile)
    {
        CreateRoleRequest request = ValidRequest();
        request.IconFile = iconFile;

        ValidationResult result = await _validator.ValidateAsync(request);

        ShouldAccept(result);
        ShouldNotReport(result, nameof(CreateRoleRequest.IconFile));
    }

    // THE UNCONSTRAINED MEMBERS

    /// <summary>
    /// The optional group reference is unconstrained, including at both values a reader would be tempted to
    /// reject.
    /// </summary>
    /// <param name="roleGroupId">The submitted group key.</param>
    [Theory]
    [InlineData(null)]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(7)]
    public async Task RoleGroupId_IsUnconstrained(int? roleGroupId)
    {
        CreateRoleRequest request = ValidRequest();
        request.RoleGroupId = roleGroupId;

        ValidationResult result = await _validator.ValidateAsync(request);

        ShouldAccept(result);
        ShouldNotReport(result, nameof(CreateRoleRequest.RoleGroupId));
    }

    /// <summary>Both flags are unconstrained in every combination, because a boolean is its own constraint.</summary>
    /// <param name="isPublic">Whether the role is public.</param>
    /// <param name="autoAssignment">Whether the role is assigned automatically.</param>
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task BothFlags_AreUnconstrained(bool isPublic, bool autoAssignment)
    {
        CreateRoleRequest request = ValidRequest();
        request.IsPublic = isPublic;
        request.AutoAssignment = autoAssignment;

        ValidationResult result = await _validator.ValidateAsync(request);

        ShouldAccept(result);
    }

    // ------------------------------------------------------------------------
    // THE WHOLE RESPONSE
    // ------------------------------------------------------------------------

    /// <summary>
    /// One malformed submission reports every bad field rather than only the first, so a caller learns
    /// everything wrong with a request in a single round trip.
    /// </summary>
    [Fact]
    public async Task SeveralBadFields_AreAllReportedTogether()
    {
        CreateRoleRequest request = new()
        {
            RoleName = string.Empty,
            ServiceFee = -1m,
            BillingPeriod = -1,
            TrialFee = -1m,
            TrialPeriod = -1,
            BillingFrequency = default(BillingFrequency),
        };

        ValidationResult result = await _validator.ValidateAsync(request);

        ShouldReport(result, nameof(CreateRoleRequest.RoleName), RoleNameRequired);
        ShouldReport(result, nameof(CreateRoleRequest.ServiceFee), ServiceFeeNegative);
        ShouldReport(result, nameof(CreateRoleRequest.BillingPeriod), BillingPeriodNotPositive);
        ShouldReport(result, nameof(CreateRoleRequest.TrialFee), TrialFeeNegative);
        ShouldReport(result, nameof(CreateRoleRequest.TrialPeriod), TrialPeriodNotPositive);
        ShouldReport(result, nameof(CreateRoleRequest.BillingFrequency), BillingFrequencyInvalid);
        result.Errors.Should().HaveCount(6, Render(result));
    }

    /// <summary>
    /// A role priced at nothing with no terms at all is accepted, which is the single most common role in a
    /// real installation and the submission a mis-authored rule would break first.
    /// </summary>
    [Fact]
    public async Task AFreeRoleWithNoTermsAtAll_IsAccepted()
    {
        CreateRoleRequest request = new()
        {
            RoleName = "Registered Users",
            Description = "Registered Users",
        };

        ValidationResult result = await _validator.ValidateAsync(request);

        ShouldAccept(result);
    }
}
