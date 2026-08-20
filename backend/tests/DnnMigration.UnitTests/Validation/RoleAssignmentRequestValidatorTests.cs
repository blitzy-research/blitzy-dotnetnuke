// The legacy declarations, measured rather than assumed.
using DnnMigration.Application.Dtos.Role;
using DnnMigration.Application.Validation;
using FluentAssertions;
using FluentValidation.Results;
using Xunit;

namespace DnnMigration.UnitTests.Validation;

/// <summary>
/// Proves rule-for-rule parity between <see cref="RoleAssignmentRequestValidator"/> and the three
/// declarative validators on the legacy role-assignment screen
/// <c>Website/admin/Security/securityroles.ascx</c>.
/// </summary>
public class RoleAssignmentRequestValidatorTests
{
    /// <summary>Wording of <c>valDates</c>, with its leading markup tag removed.</summary>
    private const string ExpiryNotAfterEffective = "Expiry Date must be Greater than Effective Date";

    /// <summary>
    /// The wording the boundary reports for a non-positive account key. The legacy screen had no
    /// counterpart message, because its selector could not post one.
    /// </summary>
    private const string UserIdNotPositive = "A user must be selected.";

    /// <summary>An arbitrary instant used as the effective date wherever a fixed one is needed.</summary>
    private static readonly DateTime Effective = new(2024, 6, 1, 9, 0, 0, DateTimeKind.Utc);

    /// <summary>
    /// The subject. Constructed directly and once: the validator takes no constructor argument, reads no
    /// configuration and touches no store, so there is nothing here to mock.
    /// </summary>
    private readonly RoleAssignmentRequestValidator _validator = new();

    /// <summary>
    /// Builds the minimum request the legacy screen would have accepted: an account and no dates at all.
    /// </summary>
    /// <returns>A request that must validate.</returns>
    private static RoleAssignmentRequest ValidRequest() => new()
    {
        UserId = 3,
    };

    /// <summary>Asserts that a result reports no failure at all.</summary>
    /// <param name="result">The result to inspect.</param>
    private static void ShouldAccept(ValidationResult result)
    {
        result.IsValid.Should().BeTrue(Render(result));
        result.Errors.Should().BeEmpty(Render(result));
    }

    /// <summary>Asserts that a result reports one specific message against one specific member.</summary>
    /// <param name="result">The result to inspect.</param>
    /// <param name="property">The member expected to carry the failure.</param>
    /// <param name="message">The exact wording expected.</param>
    private static void ShouldReport(ValidationResult result, string property, string message)
    {
        result.IsValid.Should().BeFalse("a failure was expected for " + property);
        result.Errors.Should().Contain(
            failure => failure.PropertyName == property && failure.ErrorMessage == message,
            "the failure for {0} must carry exactly the legacy wording, but the result was {1}",
            property,
            Render(result));
    }

    /// <summary>Renders a result for a failure message.</summary>
    /// <param name="result">The result to render.</param>
    /// <returns>A readable description of every failure it carries.</returns>
    private static string Render(ValidationResult result) => result.Errors.Count == 0
        ? "the result reported nothing"
        : "the result reported "
            + string.Join(" | ", result.Errors.Select(failure => failure.PropertyName + ": " + failure.ErrorMessage));

    // ------------------------------------------------------------------------
    // ACCEPTANCE BASELINES - the CompareValidator-succeeds-on-empty semantics
    // ------------------------------------------------------------------------

    /// <summary>
    /// A request naming only the account is accepted, which is the parity proof for the most easily lost
    /// legacy behaviour on this screen.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task ARequestNamingOnlyTheAccount_IsAccepted()
    {
        ValidationResult result = await _validator.ValidateAsync(ValidRequest());

        ShouldAccept(result);
    }

    /// <summary>
    /// The minimal request really does leave both dates absent, so the acceptance above is evidence about
    /// the rule rather than about a factory that quietly supplied dates.
    /// </summary>
    [Fact]
    public void TheMinimalRequest_LeavesBothDatesAbsent()
    {
        RoleAssignmentRequest request = ValidRequest();

        request.EffectiveDate.Should().BeNull();
        request.ExpiryDate.Should().BeNull();
        request.NotifyUser.Should().BeFalse();
    }

    /// <summary>
    /// Supplying only ONE of the two dates is accepted, in either direction, because a comparison needs
    /// both sides.
    /// </summary>
    /// <param name="withEffective">Whether the effective date is the one supplied.</param>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// This is the half of the empty-control semantics that a <c>When</c> clause guarding only one member
    /// would get wrong: a validator conditioned solely on the expiry being present would compare a supplied
    /// expiry against a null effective date, and a null compares as less than every date, so an expiry
    /// supplied alone would be accepted only by accident of operator ordering.
    /// </remarks>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SupplyingOnlyOneDate_IsAccepted(bool withEffective)
    {
        RoleAssignmentRequest request = ValidRequest();

        if (withEffective)
        {
            request.EffectiveDate = Effective;
        }
        else
        {
            request.ExpiryDate = Effective;
        }

        ValidationResult result = await _validator.ValidateAsync(request);

        ShouldAccept(result);
    }

    /// <summary>A well-ordered pair of dates is accepted.</summary>
    /// <param name="daysAfter">How far the expiry follows the effective date.</param>
    /// <returns>A task representing the test.</returns>
    [Theory]
    [InlineData(1)]
    [InlineData(30)]
    [InlineData(3650)]
    public async Task AnExpiryThatFollowsTheEffectiveDate_IsAccepted(int daysAfter)
    {
        RoleAssignmentRequest request = ValidRequest();
        request.EffectiveDate = Effective;
        request.ExpiryDate = Effective.AddDays(daysAfter);

        ValidationResult result = await _validator.ValidateAsync(request);

        ShouldAccept(result);
    }

    /// <summary>An expiry one tick after the effective date is accepted.</summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task AnExpiryOneTickAfterTheEffectiveDate_IsAccepted()
    {
        RoleAssignmentRequest request = ValidRequest();
        request.EffectiveDate = Effective;
        request.ExpiryDate = Effective.AddTicks(1);

        ValidationResult result = await _validator.ValidateAsync(request);

        ShouldAccept(result);
    }

    // ------------------------------------------------------------------------
    // valDates - the one rule, and its exact boundary
    // ------------------------------------------------------------------------

    /// <summary>
    /// An expiry EQUAL to the effective date is refused, because the legacy operator was <c>GreaterThan</c>
    /// rather than <c>GreaterThanOrEqual</c>.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// This is the load-bearing boundary of the whole suite: it is the single assertion that distinguishes
    /// a faithful reproduction from the more forgiving rule an implementer reaches for by default, and a
    /// membership expiring at its own effective instant confers nothing.
    /// </remarks>
    [Fact]
    public async Task AnExpiryEqualToTheEffectiveDate_IsRefused()
    {
        RoleAssignmentRequest request = ValidRequest();
        request.EffectiveDate = Effective;
        request.ExpiryDate = Effective;

        ValidationResult result = await _validator.ValidateAsync(request);

        ShouldReport(result, nameof(RoleAssignmentRequest.ExpiryDate), ExpiryNotAfterEffective);
    }

    /// <summary>An expiry preceding the effective date is refused.</summary>
    /// <param name="daysBefore">How far the expiry precedes the effective date.</param>
    /// <returns>A task representing the test.</returns>
    [Theory]
    [InlineData(1)]
    [InlineData(365)]
    public async Task AnExpiryBeforeTheEffectiveDate_IsRefused(int daysBefore)
    {
        RoleAssignmentRequest request = ValidRequest();
        request.EffectiveDate = Effective;
        request.ExpiryDate = Effective.AddDays(-daysBefore);

        ValidationResult result = await _validator.ValidateAsync(request);

        ShouldReport(result, nameof(RoleAssignmentRequest.ExpiryDate), ExpiryNotAfterEffective);
    }

    /// <summary>An expiry one tick before the effective date is refused.</summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task AnExpiryOneTickBeforeTheEffectiveDate_IsRefused()
    {
        RoleAssignmentRequest request = ValidRequest();
        request.EffectiveDate = Effective;
        request.ExpiryDate = Effective.AddTicks(-1);

        ValidationResult result = await _validator.ValidateAsync(request);

        ShouldReport(result, nameof(RoleAssignmentRequest.ExpiryDate), ExpiryNotAfterEffective);
    }

    /// <summary>
    /// The failure is attributed to the expiry date and to nothing else, because that is the control the
    /// legacy validator validated.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task AnIllOrderedPair_AttributesTheFailureToTheExpiryDateAlone()
    {
        RoleAssignmentRequest request = ValidRequest();
        request.EffectiveDate = Effective;
        request.ExpiryDate = Effective.AddDays(-1);

        ValidationResult result = await _validator.ValidateAsync(request);

        result.Errors.Should().HaveCount(1, Render(result));
        result.Errors[0].PropertyName
            .Should().Be(nameof(RoleAssignmentRequest.ExpiryDate), Render(result));
    }

    // ------------------------------------------------------------------------
    // ABSENCES - members that must remain unconstrained, each for a measured reason
    // ------------------------------------------------------------------------

    /// <summary>
    /// The account key carries exactly ONE rule - that it is positive - and nothing about whether the
    /// account exists.
    /// </summary>
    /// <param name="userId">The submitted account key.</param>
    /// <param name="accepted">Whether the submission is admitted.</param>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// Whether a positive key names an account, and one that belongs to this tenant, is a question about
    /// stored state that this layer cannot reach and must not acquire. The service answers it, and a caller
    /// relies on the not-found outcome it produces rather than on a boundary refusal.
    /// </remarks>
    [Theory]
    [InlineData(0, false)]
    [InlineData(-1, false)]
    [InlineData(1, true)]
    [InlineData(int.MaxValue, true)]
    public async Task UserId_CarriesOnlyAPositivityRule(int userId, bool accepted)
    {
        RoleAssignmentRequest request = ValidRequest();
        request.UserId = userId;

        ValidationResult result = await _validator.ValidateAsync(request);

        if (accepted)
        {
            ShouldAccept(result);
        }
        else
        {
            ShouldReport(result, nameof(RoleAssignmentRequest.UserId), UserIdNotPositive);
        }
    }

    /// <summary>
    /// A date in the past is accepted, on either member, because the legacy assignment path CLAMPED such a
    /// date rather than refusing it.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task DatesInThePast_AreAccepted()
    {
        RoleAssignmentRequest request = ValidRequest();
        request.EffectiveDate = new DateTime(2001, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        request.ExpiryDate = new DateTime(2001, 2, 1, 0, 0, 0, DateTimeKind.Utc);

        ValidationResult result = await _validator.ValidateAsync(request);

        ShouldAccept(result);
    }

    /// <summary>
    /// The minimum date value is accepted as an effective date, because it IS the legacy sentinel for "no
    /// date" and any lower-bound rule would refuse it.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task TheLegacyNullDateSentinel_IsAcceptedAsAnEffectiveDate()
    {
        RoleAssignmentRequest request = ValidRequest();
        request.EffectiveDate = DateTime.MinValue;
        request.ExpiryDate = Effective;

        ValidationResult result = await _validator.ValidateAsync(request);

        ShouldAccept(result);
    }

    /// <summary>The notification flag carries no rule, in either state.</summary>
    /// <param name="notifyUser">The submitted flag.</param>
    /// <returns>A task representing the test.</returns>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task NotifyUser_CarriesNoRule(bool notifyUser)
    {
        RoleAssignmentRequest request = ValidRequest();
        request.NotifyUser = notifyUser;

        ValidationResult result = await _validator.ValidateAsync(request);

        ShouldAccept(result);
    }
}
