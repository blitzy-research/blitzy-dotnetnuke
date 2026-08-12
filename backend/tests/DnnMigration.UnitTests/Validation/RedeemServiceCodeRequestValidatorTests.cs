// MIGRATION: this suite is the parity proof for
// Application/Validation/RedeemServiceCodeRequestValidator.cs. Its subject is not "does the validator reject
// an empty string" but "does it preserve the one guard the legacy invitation-code handler actually had, and
// nothing beyond it".
//
// MIGRATION: the legacy declarations, measured rather than assumed. Website/admin/Users/MemberServices.ascx
// declares the code box at L14 and its command at L15 and NO validator of any kind - no
// RequiredFieldValidator, no RegularExpressionValidator, no CompareValidator. The only guard is imperative,
// at MemberServices.ascx.vb L403: `If code <> "" Then`, wrapping the entire handler body. So an empty box
// produced no subscription, no message and no visible effect at all.
//
// MIGRATION: why that guard could not simply be dropped, which is the substantive fact this suite pins. An
// absent Roles.RSVPCode reached the comparison at L411 as Null.NullString, and Null.vb L66-L75 defines that
// sentinel as the EMPTY STRING rather than as nothing. Without the guard, an empty submission would therefore
// have compared equal to every role in the tenant that carries no invitation code, and the loop - which has
// no early exit - would have enrolled the account in all of them. The rule is a security bound, not a
// nicety, and the service repeats it so it holds for a caller that reached it without this pipeline.
//
// MIGRATION: the length bound is the stored column's own width, Roles.RSVPCode nvarchar(50) NULL
// (03.02.03.SqlDataProvider L45), taken from the shared role-terms constants - the same CEILING the role
// editor applies when the value is WRITTEN, which is what keeps a redeemable code and a storable one the same
// set at the top end.
//
// SEC: the two paths are deliberately NOT symmetrical at the bottom end. The role editor now also refuses to
// AUTHOR a code that is short or single-class, because the size of the space is half of what makes guessing
// expensive; redemption keeps accepting such a code, because installations already hold them and refusing the
// submission would lock out the members they were issued to rather than protecting anything. See
// Code_StillRedeemsWhenItIsWeakerThanTheEditorWouldNowAuthor, which pins both directions of that path.
//
// MIGRATION: what is deliberately NOT declared. No pattern rule, because the legacy screen declared none and
// an invitation code is tenant-authored free text. No trimming and no case folding, here or in the service:
// the legacy comparison was an in-memory Visual Basic string equality with no Option Compare Text in the
// file, so it was ordinal and untrimmed, and widening it would let a code match a role its issuer did not
// intend. Whether any role bears the code is a question about state and is answered by the service, so this
// validator takes no collaborator and its message reveals nothing about which codes exist.
using DnnMigration.Application.Dtos.Role;
using DnnMigration.Application.Dtos.User;
using DnnMigration.Application.Validation;
using FluentAssertions;
using FluentValidation.Results;
using Xunit;

namespace DnnMigration.UnitTests.Validation;

/// <summary>
/// Proves that <see cref="RedeemServiceCodeRequestValidator"/> preserves the legacy invitation-code guard and
/// bounds the submission at the stored column's width, and declares nothing else.
/// </summary>
/// <remarks>
/// Both halves of every failure are asserted rather than only the fact of failure: the property name becomes
/// the key and the message the value in the <c>errors</c> dictionary of the RFC 7807 payload a client
/// consumes, so a test checking only <see cref="ValidationResult.IsValid"/> would prove a rule fires without
/// proving it names the member the caller would change.
/// </remarks>
public class RedeemServiceCodeRequestValidatorTests
{
    /// <summary>The member every failure below is attributed to.</summary>
    private const string CodeMember = nameof(RedeemServiceCodeRequest.Code);

    /// <summary>
    /// Wording reported when no code is submitted.
    /// </summary>
    /// <remarks>
    /// Restated here as a literal rather than read from the validator, matching the sibling suites in this
    /// folder: a test that quoted the implementation's own constant would pass whatever that constant said,
    /// which is not a parity proof. The legacy screen had no message of its own to recover - its guard was
    /// silent - so this wording is authored, and stating it twice is what makes a change to it visible.
    /// </remarks>
    private const string CodeRequired = "An RSVP Code is required.";

    /// <summary>
    /// Width of <c>Roles.RSVPCode nvarchar(50) NULL</c> (<c>03.02.03.SqlDataProvider</c> L45), restated
    /// independently for the same reason.
    /// </summary>
    private const int RsvpCodeWidth = 50;

    /// <summary>The validator under test.</summary>
    private readonly RedeemServiceCodeRequestValidator _validator = new();

    /// <summary>
    /// An absent, empty or whitespace-only code is refused, and the failure names the code.
    /// </summary>
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

    /// <summary>
    /// A code of exactly the stored width is accepted and one character longer is refused.
    /// </summary>
    /// <remarks>
    /// Both sides of the boundary are asserted, because an off-by-one bound is invisible to a test that only
    /// exercises a value far past it.
    /// </remarks>
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
    /// <remarks>
    /// This is the inverse assertion to the emptiness rule and is the one that would fail if somebody
    /// "helpfully" added trimming or case folding: the value has to reach the service exactly as submitted,
    /// or a code could match a role its issuer did not intend.
    /// </remarks>
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
    /// <remarks>
    /// Asserted so that a rule added here without a legacy counterpart is visible. The legacy screen declared
    /// no validator at all, so every rule on this contract has to be justified from the imperative guard or
    /// from the stored column, and there are only those two.
    /// </remarks>
    [Fact]
    public void Validator_DeclaresRulesForTheCodeAlone()
    {
        _validator.CreateDescriptor().GetMembersWithValidators()
            .Select(member => member.Key)
            .Should().Equal(CodeMember);
    }

    /// <summary>
    /// A code that the role editor would now REFUSE to author is still accepted for redemption, and that
    /// asymmetry is deliberate.
    /// </summary>
    /// <param name="legacyCode">A code of the shape installations already hold.</param>
    /// <remarks>
    /// <para>
    /// SEC: THE STRENGTH RULE BELONGS ON THE WRITE PATH ONLY. Guessing an invitation code is bounded by how
    /// fast an attacker may try and by how large the space is, so the write path now refuses a short or
    /// single-class code and the endpoint bounds the rate. Applying the same strength rule HERE would bound
    /// nothing further - an attacker guessing a short code is guessing a code that already exists in the
    /// store, and refusing the submission would simply make that role unreachable to the members it was
    /// issued to.
    /// </para>
    /// <para>
    /// The upgrade path is therefore: existing codes keep working, and the next time an issuer saves the role
    /// the editor requires a stronger one. The two assertions here are what pin that path in place - drop the
    /// first and legacy members are locked out, drop the second and weak codes can be authored again.
    /// </para>
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

        ValidationResult authored = new UpdateRoleRequestValidator().Validate(
            new UpdateRoleRequest { RoleName = "Subscribers", RsvpCode = legacyCode });

        authored.IsValid.Should().BeFalse("the same value may no longer be authored or rotated in");
        authored.Errors.Should().ContainSingle()
            .Which.PropertyName.Should().Be(nameof(UpdateRoleRequest.RsvpCode));
    }
}
