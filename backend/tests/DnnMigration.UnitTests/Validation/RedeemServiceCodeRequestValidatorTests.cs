using DnnMigration.Application.Dtos.Role;
using DnnMigration.Application.Dtos.User;
using DnnMigration.Application.Validation;
using FluentAssertions;
using FluentValidation.Results;
using Xunit;

namespace DnnMigration.UnitTests.Validation;

/// <summary>
/// Proves that <see cref="RedeemServiceCodeRequestValidator"/> preserves the legacy invitation-code guard
/// and bounds the submission at the stored column's width, and declares nothing else.
/// </summary>
public class RedeemServiceCodeRequestValidatorTests
{
    /// <summary>The member every failure below is attributed to.</summary>
    private const string CodeMember = nameof(RedeemServiceCodeRequest.Code);

    /// <summary>Wording reported when no code is submitted.</summary>
    private const string CodeRequired = "An RSVP Code is required.";

    /// <summary>
    /// Width of <c>Roles.RSVPCode nvarchar(50) NULL</c> (<c>03.02.03.SqlDataProvider</c> L45), restated
    /// independently for the same reason.
    /// </summary>
    private const int RsvpCodeWidth = 50;

    /// <summary>The validator under test.</summary>
    private readonly RedeemServiceCodeRequestValidator _validator = new();

    /// <summary>An absent, empty or whitespace-only code is refused, and the failure names the code.</summary>
    /// <remarks>
    /// Whitespace is treated as empty, which is stricter than the legacy inequality and deliberately so:
    /// whitespace could never match a real code, and the alternative - reading every role in the tenant to
    /// answer "no match" - spends a query to reach a foregone conclusion.
    /// </remarks>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("\t\r\n")]
    public void Code_IsRequired(string? submitted)
    {
        ValidationResult result = _validator.Validate(new RedeemServiceCodeRequest { Code = submitted });

        result.IsValid.Should().BeFalse();
        result.Errors.Should().ContainSingle()
            .Which.Should().Match<ValidationFailure>(failure =>
                failure.PropertyName == CodeMember
                && failure.ErrorMessage == CodeRequired);
    }

    /// <summary>A code of exactly the stored width is accepted and one character longer is refused.</summary>
    [Fact]
    public void Code_IsBoundedByTheStoredColumnWidth()
    {
        string atTheBound = new('c', RsvpCodeWidth);
        string overTheBound = new('c', RsvpCodeWidth + 1);

        _validator.Validate(new RedeemServiceCodeRequest { Code = atTheBound })
            .IsValid.Should().BeTrue();

        ValidationResult refused = _validator.Validate(new RedeemServiceCodeRequest { Code = overTheBound });

        refused.IsValid.Should().BeFalse();
        refused.Errors.Should().ContainSingle().Which.PropertyName.Should().Be(CodeMember);
    }

    /// <summary>
    /// A code carrying surrounding whitespace, mixed case or punctuation is ACCEPTED, because the validator
    /// judges shape alone and the match itself is ordinal and untrimmed.
    /// </summary>
    [Theory]
    [InlineData(" Founders-2026")]
    [InlineData("Founders-2026 ")]
    [InlineData("founders-2026")]
    [InlineData("FOUNDERS 2026!")]
    public void Code_IsJudgedForShapeAloneAndNeverNormalised(string submitted)
    {
        var request = new RedeemServiceCodeRequest { Code = submitted };

        _validator.Validate(request).IsValid.Should().BeTrue();
        request.Code.Should().Be(submitted, "validation must not rewrite the value it judges");
    }

    /// <summary>The validator declares exactly one rule, against exactly one member.</summary>
    [Fact]
    public void Validator_DeclaresRulesForTheCodeAlone()
    {
        _validator.CreateDescriptor().GetMembersWithValidators()
            .Select(member => member.Key)
            .Should().Equal(CodeMember);
    }

    /// <summary>
    /// A code that the role editor would now REFUSE to author afresh is still accepted for redemption, and
    /// still refused when a caller tries to author it.
    /// </summary>
    /// <param name="legacyCode">A code of the shape installations already hold.</param>
    /// <remarks>
    /// Three cases must be told apart, and only the middle one is "authoring":
    /// <list type="bullet">
    /// <item>REDEEMING a stored weak code - always admitted, or every member holding a legitimately issued
    /// code is locked out.</item>
    /// <item>AUTHORING a weak code, meaning storing a value that is not already there - always refused,
    /// which is the net-new security rule. Creation is authoring by definition, so the creation validator
    /// owns it and asserts it here.</item>
    /// <item>RE-SUBMITTING the stored code unchanged while editing something else on the role - admitted,
    /// because nothing is authored. This is why the update validator no longer declares the strength rule:
    /// a validator cannot see the stored value, so it cannot tell this case from the one above. The
    /// decision moved to <c>RoleService.UpdateRoleAsync</c>, which compares the two, and both of its arms
    /// are asserted in <c>RoleServiceTests</c>.</item>
    /// </list>
    /// Refusing the third case would have made a role carrying a legacy code un-editable except by rotating
    /// that code - which invalidates it for every member holding it, defeating the very protection the first
    /// case exists to give. AAP 0.7.5.5 forbids exactly this class of migration-time tightening.
    /// </remarks>
    [Theory]
    [InlineData("JOIN")]
    [InlineData("JOIN2008")]
    [InlineData("abcdefghijk")]
    [InlineData("Founders")]
    public void Code_StillRedeemsWhenItIsWeakerThanTheEditorWouldNowAuthor(string legacyCode)
    {
        _validator.Validate(new RedeemServiceCodeRequest { Code = legacyCode }).IsValid
            .Should().BeTrue("a code already issued must stay redeemable by the members who hold it");

        ValidationResult authored = new CreateRoleRequestValidator().Validate(
            new CreateRoleRequest { RoleName = "Subscribers", RsvpCode = legacyCode });

        authored.IsValid.Should().BeFalse("the same value may not be authored into a new role");
        authored.Errors.Should().ContainSingle()
            .Which.PropertyName.Should().Be(nameof(CreateRoleRequest.RsvpCode));

        // The shape rules still apply on update - only the provenance-dependent strength rule moved - so an
        // unchanged legacy code passes the contract and reaches the service that can actually judge it.
        new UpdateRoleRequestValidator().Validate(
            new UpdateRoleRequest { RoleName = "Subscribers", RsvpCode = legacyCode }).IsValid
            .Should().BeTrue("the contract cannot judge provenance, so it defers to the service");
    }
}
