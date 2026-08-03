// MIGRATION: this suite is the parity proof for
// Application/Validation/ProfilePropertyDefinitionDtoValidator.cs. Its subject is not "does the validator
// reject bad input" but "does the validator enforce exactly the rule set the legacy profile-definition
// editor enforced, and no more". Every assertion names the legacy declaration or the schema statement it
// reproduces, and the boundary lengths are asserted on both sides rather than sampled, because an
// off-by-one in a width rule is the defect a length test exists to catch.
//
// MIGRATION: the legacy declarations, measured rather than assumed. Website/admin/Users/
// EditProfileDefinition.ascx declares ZERO validators of any kind: its whole field set is one
// <dnn:propertyeditorcontrol id="Properties"> at L23, a reflective editor from the excluded control library
// that renders its validators from ATTRIBUTES on the object being edited. The authoritative declarations are
// therefore on Library/Components/Users/Profile/ProfilePropertyDefinition.vb, and there are exactly three:
//   PropertyCategory L193  <Required(True), SortOrder(2)>
//   PropertyName     L228  <Required(True), IsReadOnly(True), SortOrder(0),
//                           RegularExpressionValidator("^[a-zA-Z0-9._%\-+']+$")>
//   ViewOrder        L300  <Required(True), SortOrder(8)>
// Every other member - DataType L91, DefaultValue L109, Length L141, Required L264, ValidationExpression
// L282, Visible L318, Visibility L336 - carries a sort order and nothing else. The tests below account for
// all three declarations and, in the ABSENCES section, for every member that must remain unconstrained.
//
// MIGRATION: the two widened columns are the load-bearing part of this file. ValidationExpression was
// created nvarchar(100) (03.02.03:L1074) and widened to nvarchar(2000) by 04.03.05:L17; DefaultValue was
// created nvarchar(50) (03.02.03:L1069) and widened to ntext by 04.05.00:L1593. A rule taking the CREATING
// width would refuse values that any database upgraded past those scripts already stores, so this suite
// asserts 2000 as a hard boundary and asserts that the default value is bounded by nothing at all. Those
// two tests are the reason the suite exists as a parity proof rather than as a smoke test.
using DnnMigration.Application.Dtos.User;
using DnnMigration.Application.Validation;
using FluentAssertions;
using FluentValidation.Results;
using Xunit;

namespace DnnMigration.UnitTests.Validation;

/// <summary>
/// Proves rule-for-rule parity between <see cref="ProfilePropertyDefinitionDtoValidator"/> and the three
/// declarative validators the legacy property editor rendered from
/// <c>Library/Components/Users/Profile/ProfilePropertyDefinition.vb</c>, together with the terminal widths of
/// the columns that store the payload.
/// </summary>
/// <remarks>
/// <para>
/// Both halves of every failure are asserted, never just the fact of failure. The property name becomes the
/// key and the message the value in the <c>errors</c> dictionary of the RFC 7807 payload a client consumes,
/// so a test that checked only <see cref="ValidationResult.IsValid"/> would prove a rule fires without
/// proving it reports what a caller can act on.
/// </para>
/// <para>
/// One projection type serves as both request and response for this resource, so a caller legitimately reads
/// a definition, edits one field and sends the same shape back. Several tests below exist to keep that round
/// trip possible: they assert that members carrying values the API itself emits - a view order of -1, a
/// visibility taken from a module setting, a data-type key from the excluded lookup - are not refused.
/// </para>
/// </remarks>
public class ProfilePropertyDefinitionDtoValidatorTests
{
    /// <summary>Wording reported when the property name is missing.</summary>
    private const string PropertyNameRequired = "You Must Enter a Property Name";

    /// <summary>Wording reported when the property name breaks the legacy pattern.</summary>
    private const string PropertyNameInvalid =
        "Property Name may contain only letters, numbers and the characters . _ % - + '";

    /// <summary>Wording reported when the property name exceeds its column width.</summary>
    private const string PropertyNameTooLong = "Property Name must be 50 characters or fewer";

    /// <summary>Wording reported when the property category is missing.</summary>
    private const string PropertyCategoryRequired = "You Must Enter a Property Category";

    /// <summary>Wording reported when the property category exceeds its column width.</summary>
    private const string PropertyCategoryTooLong = "Property Category must be 50 characters or fewer";

    /// <summary>Wording reported when the validation expression exceeds its column width.</summary>
    private const string ValidationExpressionTooLong =
        "Validation Expression must be 2000 characters or fewer";

    /// <summary>
    /// Terminal width of <c>PropertyName nvarchar(50) NOT NULL</c> (<c>03.02.03.SqlDataProvider</c> L1071,
    /// never altered).
    /// </summary>
    private const int PropertyNameWidth = 50;

    /// <summary>
    /// Terminal width of <c>PropertyCategory nvarchar(50) NOT NULL</c> (<c>03.02.03.SqlDataProvider</c>
    /// L1070, never altered).
    /// </summary>
    private const int PropertyCategoryWidth = 50;

    /// <summary>
    /// Terminal width of <c>ValidationExpression</c>, created <c>nvarchar(100)</c> and WIDENED to
    /// <c>nvarchar(2000)</c> by <c>04.03.05.SqlDataProvider</c> L17.
    /// </summary>
    private const int ValidationExpressionWidth = 2000;

    /// <summary>
    /// The subject. Constructed directly and once: the validator takes no constructor argument, reads no
    /// configuration and touches no store, so there is nothing here to mock.
    /// </summary>
    private readonly ProfilePropertyDefinitionDtoValidator _validator = new();

    /// <summary>
    /// Builds the minimum definition the legacy editor would have accepted: the two required strings and
    /// nothing else supplied.
    /// </summary>
    /// <returns>A definition that must validate.</returns>
    /// <remarks>
    /// Every unconstrained member is left at its default on purpose, so each test below mutates exactly one
    /// field and a failure identifies exactly one rule.
    /// </remarks>
    private static ProfilePropertyDefinitionDto ValidDefinition() => new()
    {
        PropertyName = "City",
        PropertyCategory = "Address",
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
            "the failure for {0} must carry exactly the expected wording, but the result was {1}",
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
    // ACCEPTANCE BASELINES
    // ------------------------------------------------------------------------

    /// <summary>
    /// A definition carrying nothing but the two required strings is accepted, because those are the only
    /// two members the legacy editor rendered a presence rule for.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task ADefinitionCarryingOnlyTheTwoRequiredStrings_IsAccepted()
    {
        ValidationResult result = await _validator.ValidateAsync(ValidDefinition());

        ShouldAccept(result);
    }

    /// <summary>
    /// The minimal definition really does leave every optional member unsupplied, so the acceptance above is
    /// evidence about the rules rather than about a factory that quietly populated fields.
    /// </summary>
    [Fact]
    public void TheMinimalDefinition_LeavesEveryUnconstrainedMemberAtItsDefault()
    {
        ProfilePropertyDefinitionDto definition = ValidDefinition();

        definition.PropertyDefinitionId.Should().Be(0);
        definition.ModuleDefId.Should().BeNull();
        definition.DataType.Should().Be(0);
        definition.DefaultValue.Should().BeNull();
        definition.Length.Should().Be(0);
        definition.Required.Should().BeFalse();
        definition.ValidationExpression.Should().BeNull();
        definition.ViewOrder.Should().Be(0);
        definition.Visible.Should().BeFalse();
        definition.Visibility.Should().Be(0);
    }

    /// <summary>A fully populated definition is accepted, exercising every member at once.</summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task AFullyPopulatedDefinition_IsAccepted()
    {
        ProfilePropertyDefinitionDto definition = new()
        {
            PropertyDefinitionId = 42,
            PortalId = 7,
            ModuleDefId = 3,
            DataType = 349,
            DefaultValue = "London",
            PropertyCategory = "Address",
            PropertyName = "City",
            Length = 50,
            Required = true,
            ValidationExpression = @"^[A-Za-z ]+$",
            ViewOrder = 11,
            Visible = true,
            Visibility = 2,
        };

        ValidationResult result = await _validator.ValidateAsync(definition);

        ShouldAccept(result);
    }

    // ------------------------------------------------------------------------
    // PropertyName - Required(True) + RegularExpressionValidator + column width
    // ------------------------------------------------------------------------

    /// <summary>The property name is required, reproducing <c>Required(True)</c> at L228.</summary>
    /// <param name="propertyName">The submitted name.</param>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// A whitespace-only name is treated as absent. That is a documented narrowing rather than a
    /// reproduction: the legacy required rule trimmed before testing, so it too refused a name of spaces,
    /// but the name is also the key a profile value is addressed by and the column is uniquely indexed, so
    /// admitting whitespace would create a key nobody can type.
    /// </remarks>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t")]
    public async Task PropertyName_IsRequired(string propertyName)
    {
        ProfilePropertyDefinitionDto definition = ValidDefinition();
        definition.PropertyName = propertyName;

        ValidationResult result = await _validator.ValidateAsync(definition);

        ShouldReport(result, nameof(ProfilePropertyDefinitionDto.PropertyName), PropertyNameRequired);
    }

    /// <summary>
    /// An omitted name reports the presence failure ALONE, which is what the legacy pairing did: an ASP.NET
    /// regular-expression validator succeeds against an empty control by design, deferring presence to the
    /// required validator beside it.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task PropertyName_WhenAbsent_ReportsOnlyThePresenceFailure()
    {
        ProfilePropertyDefinitionDto definition = ValidDefinition();
        definition.PropertyName = string.Empty;

        ValidationResult result = await _validator.ValidateAsync(definition);

        result.Errors
            .Where(failure => failure.PropertyName == nameof(ProfilePropertyDefinitionDto.PropertyName))
            .Should().HaveCount(1, Render(result));
    }

    /// <summary>The property name is bounded at the width of the column that stores it.</summary>
    /// <param name="length">The submitted length.</param>
    /// <param name="accepted">Whether that length is within the ceiling.</param>
    /// <returns>A task representing the test.</returns>
    [Theory]
    [InlineData(49, true)]
    [InlineData(50, true)]
    [InlineData(51, false)]
    public async Task PropertyName_IsBoundedByItsColumnWidth(int length, bool accepted)
    {
        ProfilePropertyDefinitionDto definition = ValidDefinition();
        definition.PropertyName = new string('a', length);

        ValidationResult result = await _validator.ValidateAsync(definition);

        if (accepted)
        {
            ShouldAccept(result);
        }
        else
        {
            ShouldReport(result, nameof(ProfilePropertyDefinitionDto.PropertyName), PropertyNameTooLong);
        }
    }

    /// <summary>
    /// Every character class the legacy pattern admits is accepted, so the pattern was carried across rather
    /// than approximated.
    /// </summary>
    /// <param name="propertyName">A name built only from admitted characters.</param>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// The pattern is <c>^[a-zA-Z0-9._%\-+']+$</c>: letters, digits, dot, underscore, percent, hyphen, plus
    /// and apostrophe. The hyphen and the apostrophe matter in practice - real installations carry names such
    /// as <c>Address-2</c> - and the apostrophe is the one that a naive tightening would drop first.
    /// </remarks>
    [Theory]
    [InlineData("City")]
    [InlineData("Address2")]
    [InlineData("Address-2")]
    [InlineData("Address_2")]
    [InlineData("Address.Line")]
    [InlineData("Percent%Complete")]
    [InlineData("A+B")]
    [InlineData("O'Brien")]
    [InlineData("a")]
    public async Task PropertyName_AcceptsEveryCharacterTheLegacyPatternAdmits(string propertyName)
    {
        ProfilePropertyDefinitionDto definition = ValidDefinition();
        definition.PropertyName = propertyName;

        ValidationResult result = await _validator.ValidateAsync(definition);

        ShouldAccept(result);
    }

    /// <summary>
    /// A character outside the legacy pattern is refused with the pattern message, and a space is the case
    /// that matters most because it is the one a caller is likeliest to try.
    /// </summary>
    /// <param name="propertyName">A name carrying a refused character.</param>
    /// <returns>A task representing the test.</returns>
    [Theory]
    [InlineData("Home City")]
    [InlineData("City!")]
    [InlineData("City?")]
    [InlineData("City/Town")]
    [InlineData("City\\Town")]
    [InlineData("City#1")]
    [InlineData("<script>")]
    [InlineData("City;Town")]
    public async Task PropertyName_RefusesEveryCharacterTheLegacyPatternExcluded(string propertyName)
    {
        ProfilePropertyDefinitionDto definition = ValidDefinition();
        definition.PropertyName = propertyName;

        ValidationResult result = await _validator.ValidateAsync(definition);

        ShouldReport(result, nameof(ProfilePropertyDefinitionDto.PropertyName), PropertyNameInvalid);
    }

    /// <summary>
    /// An over-long name reports the length and nothing else, so a caller reads one actionable message per
    /// field rather than a pattern complaint stacked underneath a length complaint.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task PropertyName_WhenTooLong_ReportsOnlyTheLengthFailure()
    {
        ProfilePropertyDefinitionDto definition = ValidDefinition();
        definition.PropertyName = new string('a', PropertyNameWidth + 1);

        ValidationResult result = await _validator.ValidateAsync(definition);

        result.Errors
            .Where(failure => failure.PropertyName == nameof(ProfilePropertyDefinitionDto.PropertyName))
            .Should().HaveCount(1, Render(result));
    }

    // ------------------------------------------------------------------------
    // PropertyCategory - Required(True) + column width
    // ------------------------------------------------------------------------

    /// <summary>The property category is required, reproducing <c>Required(True)</c> at L193.</summary>
    /// <param name="propertyCategory">The submitted category.</param>
    /// <returns>A task representing the test.</returns>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t")]
    public async Task PropertyCategory_IsRequired(string propertyCategory)
    {
        ProfilePropertyDefinitionDto definition = ValidDefinition();
        definition.PropertyCategory = propertyCategory;

        ValidationResult result = await _validator.ValidateAsync(definition);

        ShouldReport(
            result,
            nameof(ProfilePropertyDefinitionDto.PropertyCategory),
            PropertyCategoryRequired);
    }

    /// <summary>The property category is bounded at the width of the column that stores it.</summary>
    /// <param name="length">The submitted length.</param>
    /// <param name="accepted">Whether that length is within the ceiling.</param>
    /// <returns>A task representing the test.</returns>
    [Theory]
    [InlineData(49, true)]
    [InlineData(50, true)]
    [InlineData(51, false)]
    public async Task PropertyCategory_IsBoundedByItsColumnWidth(int length, bool accepted)
    {
        ProfilePropertyDefinitionDto definition = ValidDefinition();
        definition.PropertyCategory = new string('a', length);

        ValidationResult result = await _validator.ValidateAsync(definition);

        if (accepted)
        {
            ShouldAccept(result);
        }
        else
        {
            ShouldReport(
                result,
                nameof(ProfilePropertyDefinitionDto.PropertyCategory),
                PropertyCategoryTooLong);
        }
    }

    /// <summary>
    /// The category is NOT restricted to the four headings the shipped defaults use, because the legacy
    /// editor was a free-text box and installations invent their own.
    /// </summary>
    /// <param name="propertyCategory">A heading outside the shipped four.</param>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// The shipped definitions use Name, Address, Contact Info and Preferences. A closed list would refuse
    /// every heading an administrator added, and no legacy declaration constrained the value - note in
    /// particular that a space is admitted here, unlike in the property name, because only the name carried
    /// a pattern.
    /// </remarks>
    [Theory]
    [InlineData("Contact Info")]
    [InlineData("Employment History")]
    [InlineData("Custom")]
    public async Task PropertyCategory_AcceptsAnyHeading(string propertyCategory)
    {
        ProfilePropertyDefinitionDto definition = ValidDefinition();
        definition.PropertyCategory = propertyCategory;

        ValidationResult result = await _validator.ValidateAsync(definition);

        ShouldAccept(result);
    }

    // ------------------------------------------------------------------------
    // ValidationExpression - the WIDENED column
    // ------------------------------------------------------------------------

    /// <summary>
    /// The validation expression is bounded at the TERMINAL width of 2000, not at the 100 it was created
    /// with.
    /// </summary>
    /// <param name="length">The submitted length.</param>
    /// <param name="accepted">Whether that length is within the ceiling.</param>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// The 101-character case is the one that proves the point: it is refused by the creating width and
    /// accepted by the terminal one, so a validator taking the wrong number fails here and only here.
    /// </remarks>
    [Theory]
    [InlineData(100, true)]
    [InlineData(101, true)]
    [InlineData(1999, true)]
    [InlineData(2000, true)]
    [InlineData(2001, false)]
    public async Task ValidationExpression_IsBoundedByItsTerminalColumnWidth(int length, bool accepted)
    {
        ProfilePropertyDefinitionDto definition = ValidDefinition();
        definition.ValidationExpression = new string('a', length);

        ValidationResult result = await _validator.ValidateAsync(definition);

        if (accepted)
        {
            ShouldAccept(result);
        }
        else
        {
            ShouldReport(
                result,
                nameof(ProfilePropertyDefinitionDto.ValidationExpression),
                ValidationExpressionTooLong);
        }
    }

    /// <summary>
    /// An absent or empty expression reports nothing, because the column is nullable and an unconstrained
    /// property is the ordinary case.
    /// </summary>
    /// <param name="validationExpression">The submitted expression.</param>
    /// <returns>A task representing the test.</returns>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task ValidationExpression_WhenAbsentOrEmpty_ReportsNothing(string? validationExpression)
    {
        ProfilePropertyDefinitionDto definition = ValidDefinition();
        definition.ValidationExpression = validationExpression;

        ValidationResult result = await _validator.ValidateAsync(definition);

        ShouldAccept(result);
    }

    /// <summary>
    /// The expression is not itself parsed as a regular expression, and a syntactically invalid one is
    /// accepted.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// The stored expression is a rule applied to PROFILE INPUT later, not a rule about this field, and no
    /// legacy declaration compiled it. Compiling it here would also hand an unauthenticated caller a way to
    /// spend server time on a pathological pattern.
    /// </remarks>
    [Fact]
    public async Task ValidationExpression_IsNotItselfCompiled()
    {
        ProfilePropertyDefinitionDto definition = ValidDefinition();
        definition.ValidationExpression = "([unclosed";

        ValidationResult result = await _validator.ValidateAsync(definition);

        ShouldAccept(result);
    }

    // ------------------------------------------------------------------------
    // ABSENCES - members that must remain unconstrained, each for a measured reason
    // ------------------------------------------------------------------------

    /// <summary>
    /// The default value carries NO length rule, because its column was widened from
    /// <c>nvarchar(50)</c> to <c>ntext</c>.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// A rule of 50 would refuse values that any database upgraded past <c>04.05.00.SqlDataProvider</c>
    /// already stores, so a value comfortably past both the old width and the expression's ceiling is
    /// asserted as acceptable.
    /// </remarks>
    [Fact]
    public async Task DefaultValue_IsBoundedByNothing()
    {
        ProfilePropertyDefinitionDto definition = ValidDefinition();
        definition.DefaultValue = new string('a', ValidationExpressionWidth + 1000);

        ValidationResult result = await _validator.ValidateAsync(definition);

        ShouldAccept(result);
    }

    /// <summary>
    /// A view order of -1 is accepted, because on this contract -1 is an INSTRUCTION rather than an absence
    /// marker.
    /// </summary>
    /// <param name="viewOrder">The submitted order.</param>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// The terminal upsert procedure branches on <c>IF @vieworder = -1</c> and substitutes the current
    /// maximum order plus one, so -1 is the only way a caller can say "append to the end". A lower-bound
    /// rule would remove that, which is why this test names the value explicitly.
    /// </remarks>
    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(39)]
    [InlineData(int.MaxValue)]
    public async Task ViewOrder_AcceptsEveryValueIncludingTheAppendInstruction(int viewOrder)
    {
        ProfilePropertyDefinitionDto definition = ValidDefinition();
        definition.ViewOrder = viewOrder;

        ValidationResult result = await _validator.ValidateAsync(definition);

        ShouldAccept(result);
    }

    /// <summary>
    /// The visibility hint carries NO range rule, so a value outside the three documented meanings survives
    /// a round trip.
    /// </summary>
    /// <param name="visibility">The submitted hint.</param>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// The legacy member was typed as a three-member enumeration, which looks closed but is not at run time:
    /// an enumeration is an integer, and the legacy loader assigned this member by converting the
    /// <c>Profile_DefaultVisibility</c> module setting, so whatever that setting held travelled through
    /// unchecked. The member is additionally not persisted on this table at all. A 0-to-2 rule would refuse a
    /// caller that read such a value and sent it back unchanged.
    /// </remarks>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(7)]
    [InlineData(-1)]
    public async Task Visibility_CarriesNoRangeRule(int visibility)
    {
        ProfilePropertyDefinitionDto definition = ValidDefinition();
        definition.Visibility = visibility;

        ValidationResult result = await _validator.ValidateAsync(definition);

        ShouldAccept(result);
    }

    /// <summary>
    /// The data-type key carries NO rule, because it references the excluded lookup subsystem and no set
    /// exists here to test membership of.
    /// </summary>
    /// <param name="dataType">The submitted key.</param>
    /// <returns>A task representing the test.</returns>
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(349)]
    public async Task DataType_CarriesNoRule(int dataType)
    {
        ProfilePropertyDefinitionDto definition = ValidDefinition();
        definition.DataType = dataType;

        ValidationResult result = await _validator.ValidateAsync(definition);

        ShouldAccept(result);
    }

    /// <summary>
    /// The length carries no rule, and zero in particular is accepted because it legitimately means
    /// "unbounded" for a text property and is the column's own default.
    /// </summary>
    /// <param name="length">The submitted length.</param>
    /// <returns>A task representing the test.</returns>
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(50)]
    public async Task Length_CarriesNoRule(int length)
    {
        ProfilePropertyDefinitionDto definition = ValidDefinition();
        definition.Length = length;

        ValidationResult result = await _validator.ValidateAsync(definition);

        ShouldAccept(result);
    }

    /// <summary>
    /// No identifier carries a bound test, so the values this schema really uses - a negative portal key and
    /// a zero one - are accepted.
    /// </summary>
    /// <param name="portalId">The submitted tenant key.</param>
    /// <param name="propertyDefinitionId">The submitted definition key.</param>
    /// <param name="moduleDefId">The submitted module-definition key.</param>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// The portal table is <c>IDENTITY (-1, 1)</c> and the terminal schema makes
    /// <c>ProfilePropertyDefinition.PortalID</c> nullable, so neither a negative nor a zero portal key means
    /// "unset". The definition key is assigned by the store on a create and taken from the route on an
    /// update, so a caller cannot make it authoritative in either case.
    /// </remarks>
    [Theory]
    [InlineData(-1, 0, null)]
    [InlineData(0, -1, 0)]
    [InlineData(int.MaxValue, int.MaxValue, int.MaxValue)]
    public async Task NoIdentifierCarriesABoundTest(
        int portalId,
        int propertyDefinitionId,
        int? moduleDefId)
    {
        ProfilePropertyDefinitionDto definition = ValidDefinition();
        definition.PortalId = portalId;
        definition.PropertyDefinitionId = propertyDefinitionId;
        definition.ModuleDefId = moduleDefId;

        ValidationResult result = await _validator.ValidateAsync(definition);

        ShouldAccept(result);
    }

    /// <summary>
    /// A submission that breaks both string rules reports BOTH members, because class-level cascade
    /// continues.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// One round trip has to surface every bad field. A caller forced to discover them one at a time would
    /// make several requests to learn what one response could have told it.
    /// </remarks>
    [Fact]
    public async Task ASubmissionBreakingBothStringRules_ReportsBoth()
    {
        ProfilePropertyDefinitionDto definition = new()
        {
            PropertyName = string.Empty,
            PropertyCategory = string.Empty,
        };

        ValidationResult result = await _validator.ValidateAsync(definition);

        ShouldReport(result, nameof(ProfilePropertyDefinitionDto.PropertyName), PropertyNameRequired);
        ShouldReport(
            result,
            nameof(ProfilePropertyDefinitionDto.PropertyCategory),
            PropertyCategoryRequired);
    }
}
