using System.Reflection;
using DnnMigration.Application.Dtos.User;
using DnnMigration.Application.Validation;
using FluentAssertions;
using FluentValidation.Results;
using Xunit;

namespace DnnMigration.UnitTests.Validation;

/// <summary>
/// Proves rule-for-rule parity between the two profile-definition write validators and the three
/// declarative validators the legacy property editor rendered from
/// <c>Library/Components/Users/Profile/ProfilePropertyDefinition.vb</c>, together with the terminal widths
/// of the columns that store the payload - and proves that the two validators agree with each other.
/// </summary>
/// <remarks>
/// MIGRATION: the two verbs no longer share one projection type, so the round-trip concern that shaped the
/// previous suite has changed rather than disappeared. A caller still reads a definition, edits one field
/// and sends it back, but it now sends back a narrower shape whose surplus members the deserialiser
/// ignores.
/// </remarks>
public class ProfileDefinitionWriteContractValidatorTests
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

    /// <summary>Wording reported when the validation expression exceeds its write work-factor limit.</summary>
    private const string ValidationExpressionTooLong =
        "Validation Expression must be 512 characters or fewer";

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
    /// The create-path subject. Constructed directly: the validator takes no constructor argument, reads no
    /// configuration and touches no store, so there is nothing here to mock.
    /// </summary>
    private readonly CreateProfilePropertyDefinitionRequestValidator _create = new();

    /// <summary>The update-path subject, constructed for the same reasons.</summary>
    private readonly UpdateProfilePropertyDefinitionRequestValidator _update = new();

    /// <summary>
    /// Builds the minimum create request the legacy editor would have accepted: the two required strings
    /// and nothing else supplied.
    /// </summary>
    /// <param name="mutate">Applied to the baseline so a test changes exactly one field.</param>
    /// <returns>A create request.</returns>
    private static CreateProfilePropertyDefinitionRequest NewCreate(
        Action<CreateProfilePropertyDefinitionRequest>? mutate = null)
    {
        CreateProfilePropertyDefinitionRequest request = new()
        {
            PropertyName = "City",
            PropertyCategory = "Address",
        };

        mutate?.Invoke(request);
        return request;
    }

    /// <summary>Builds the minimum update request, matching the create baseline field for field.</summary>
    /// <param name="mutate">Applied to the baseline so a test changes exactly one field.</param>
    /// <returns>An update request.</returns>
    private static UpdateProfilePropertyDefinitionRequest NewUpdate(
        Action<UpdateProfilePropertyDefinitionRequest>? mutate = null)
    {
        UpdateProfilePropertyDefinitionRequest request = new()
        {
            PropertyName = "City",
            PropertyCategory = "Address",
        };

        mutate?.Invoke(request);
        return request;
    }

    /// <summary>Asserts that a result reports no failure at all.</summary>
    /// <param name="result">The result to inspect.</param>
    /// <param name="verb">Names which validator produced the result, for the failure message.</param>
    private static void ShouldAccept(ValidationResult result, string verb)
    {
        result.IsValid.Should().BeTrue(verb + " " + Render(result));
        result.Errors.Should().BeEmpty(verb + " " + Render(result));
    }

    /// <summary>Asserts that a result reports one specific message against one specific member.</summary>
    /// <param name="result">The result to inspect.</param>
    /// <param name="property">The member expected to carry the failure.</param>
    /// <param name="message">The exact wording expected.</param>
    /// <param name="verb">Names which validator produced the result, for the failure message.</param>
    private static void ShouldReport(
        ValidationResult result,
        string property,
        string message,
        string verb)
    {
        result.IsValid.Should().BeFalse("a failure was expected for " + property + " on " + verb);
        result.Errors.Should().Contain(
            failure => failure.PropertyName == property && failure.ErrorMessage == message,
            "the failure for {0} on {1} must carry exactly the expected wording, but the result was {2}",
            property,
            verb,
            Render(result));
    }

    /// <summary>Renders a result for a failure message.</summary>
    /// <param name="result">The result to render.</param>
    /// <returns>A readable description of every failure it carries.</returns>
    private static string Render(ValidationResult result) => result.Errors.Count == 0
        ? "the result reported nothing"
        : "the result reported "
            + string.Join(" | ", result.Errors.Select(failure => failure.PropertyName + ": " + failure.ErrorMessage));

    /// <summary>Asserts that both validators accept the shape each lambda produces.</summary>
    /// <param name="onCreate">Mutation applied to the create baseline.</param>
    /// <param name="onUpdate">The same mutation applied to the update baseline.</param>
    private void ShouldAcceptBoth(
        Action<CreateProfilePropertyDefinitionRequest> onCreate,
        Action<UpdateProfilePropertyDefinitionRequest> onUpdate)
    {
        ShouldAccept(_create.Validate(NewCreate(onCreate)), "create");
        ShouldAccept(_update.Validate(NewUpdate(onUpdate)), "update");
    }

    /// <summary>Asserts that both validators report the same message against the same member.</summary>
    /// <param name="onCreate">Mutation applied to the create baseline.</param>
    /// <param name="onUpdate">The same mutation applied to the update baseline.</param>
    /// <param name="property">The member expected to carry the failure.</param>
    /// <param name="message">The exact wording expected from both.</param>
    private void ShouldReportBoth(
        Action<CreateProfilePropertyDefinitionRequest> onCreate,
        Action<UpdateProfilePropertyDefinitionRequest> onUpdate,
        string property,
        string message)
    {
        ShouldReport(_create.Validate(NewCreate(onCreate)), property, message, "create");
        ShouldReport(_update.Validate(NewUpdate(onUpdate)), property, message, "update");
    }

    // ------------------------------------------------------------------------
    // ACCEPTANCE BASELINES
    // ------------------------------------------------------------------------

    /// <summary>
    /// A request carrying nothing but the two required strings is accepted on both verbs, because those are
    /// the only two members the legacy editor rendered a presence rule for.
    /// </summary>
    [Fact]
    public void ARequestCarryingOnlyTheTwoRequiredStrings_IsAcceptedOnBothVerbs()
        => ShouldAcceptBoth(_ => { }, _ => { });

    /// <summary>
    /// The minimal requests really do leave every optional member unsupplied, so the acceptance above is
    /// evidence about the rules rather than about a factory that quietly populated fields.
    /// </summary>
    [Fact]
    public void TheMinimalRequests_LeaveEveryUnconstrainedMemberAtItsDefault()
    {
        CreateProfilePropertyDefinitionRequest create = NewCreate();

        create.ModuleDefId.Should().BeNull();
        create.DataType.Should().Be(0);
        create.DefaultValue.Should().BeNull();
        create.Length.Should().Be(0);
        create.Required.Should().BeFalse();
        create.ValidationExpression.Should().BeNull();
        create.ViewOrder.Should().Be(0);
        create.Visible.Should().BeFalse();

        UpdateProfilePropertyDefinitionRequest update = NewUpdate();

        update.DataType.Should().Be(0);
        update.DefaultValue.Should().BeNull();
        update.Length.Should().Be(0);
        update.Required.Should().BeFalse();
        update.ValidationExpression.Should().BeNull();
        update.ViewOrder.Should().Be(0);
        update.Visible.Should().BeFalse();
    }

    /// <summary>A fully populated request is accepted on both verbs, exercising every member at once.</summary>
    [Fact]
    public void AFullyPopulatedRequest_IsAcceptedOnBothVerbs()
        => ShouldAcceptBoth(
            create =>
            {
                create.ModuleDefId = 3;
                create.DataType = 349;
                create.DefaultValue = "London";
                create.PropertyCategory = "Address";
                create.PropertyName = "City";
                create.Length = 50;
                create.Required = true;
                create.ValidationExpression = @"^[A-Za-z ]+$";
                create.ViewOrder = 11;
                create.Visible = true;
            },
            update =>
            {
                update.DataType = 349;
                update.DefaultValue = "London";
                update.PropertyCategory = "Address";
                update.PropertyName = "City";
                update.Length = 50;
                update.Required = true;
                update.ValidationExpression = @"^[A-Za-z ]+$";
                update.ViewOrder = 11;
                update.Visible = true;
            });

    // ------------------------------------------------------------------------
    // PropertyName - Required(True) + RegularExpressionValidator + column width
    // ------------------------------------------------------------------------

    /// <summary>The property name is required on BOTH verbs, reproducing <c>Required(True)</c> at L228.</summary>
    /// <param name="propertyName">The submitted name.</param>
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
    public void PropertyName_IsRequiredOnBothVerbs(string propertyName)
        => ShouldReportBoth(
            create => create.PropertyName = propertyName,
            update => update.PropertyName = propertyName,
            nameof(CreateProfilePropertyDefinitionRequest.PropertyName),
            PropertyNameRequired);

    /// <summary>
    /// An omitted name reports the presence failure ALONE, which is what the legacy pairing did: an ASP.NET
    /// regular-expression validator succeeds against an empty control by design, deferring presence to the
    /// required validator beside it.
    /// </summary>
    [Fact]
    public void PropertyName_WhenAbsent_ReportsOnlyThePresenceFailureOnBothVerbs()
    {
        _create.Validate(NewCreate(create => create.PropertyName = string.Empty)).Errors
            .Where(failure => failure.PropertyName == nameof(CreateProfilePropertyDefinitionRequest.PropertyName))
            .Should().HaveCount(1);

        _update.Validate(NewUpdate(update => update.PropertyName = string.Empty)).Errors
            .Where(failure => failure.PropertyName == nameof(UpdateProfilePropertyDefinitionRequest.PropertyName))
            .Should().HaveCount(1);
    }

    /// <summary>The property name is bounded at the width of the column that stores it, on both verbs.</summary>
    /// <param name="length">The submitted length.</param>
    /// <param name="accepted">Whether that length is within the ceiling.</param>
    [Theory]
    [InlineData(49, true)]
    [InlineData(50, true)]
    [InlineData(51, false)]
    public void PropertyName_IsBoundedByItsColumnWidthOnBothVerbs(int length, bool accepted)
    {
        string name = new('a', length);

        if (accepted)
        {
            ShouldAcceptBoth(
                create => create.PropertyName = name,
                update => update.PropertyName = name);
        }
        else
        {
            ShouldReportBoth(
                create => create.PropertyName = name,
                update => update.PropertyName = name,
                nameof(CreateProfilePropertyDefinitionRequest.PropertyName),
                PropertyNameTooLong);
        }
    }

    /// <summary>
    /// Every character class the legacy pattern admits is accepted on both verbs, so the pattern was
    /// carried across rather than approximated.
    /// </summary>
    /// <param name="propertyName">A name built only from admitted characters.</param>
    /// <remarks>
    /// The pattern is <c>^[a-zA-Z0-9._%\-+']+$</c>: letters, digits, dot, underscore, percent, hyphen, plus
    /// and apostrophe. The hyphen and the apostrophe matter in practice - real installations carry names
    /// such as <c>Address-2</c> - and the apostrophe is the one that a naive tightening would drop first.
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
    public void PropertyName_AcceptsEveryCharacterTheLegacyPatternAdmits(string propertyName)
        => ShouldAcceptBoth(
            create => create.PropertyName = propertyName,
            update => update.PropertyName = propertyName);

    /// <summary>
    /// A character outside the legacy pattern is refused with the pattern message on both verbs, and a
    /// space is the case that matters most because it is the one a caller is likeliest to try.
    /// </summary>
    /// <param name="propertyName">A name carrying a refused character.</param>
    [Theory]
    [InlineData("Home City")]
    [InlineData("City!")]
    [InlineData("City?")]
    [InlineData("City/Town")]
    [InlineData("City\\Town")]
    [InlineData("City#1")]
    [InlineData("<script>")]
    [InlineData("City;Town")]
    public void PropertyName_RefusesEveryCharacterTheLegacyPatternExcluded(string propertyName)
        => ShouldReportBoth(
            create => create.PropertyName = propertyName,
            update => update.PropertyName = propertyName,
            nameof(CreateProfilePropertyDefinitionRequest.PropertyName),
            PropertyNameInvalid);

    /// <summary>
    /// An over-long name reports the length and nothing else, so a caller reads one actionable message per
    /// field rather than a pattern complaint stacked underneath a length complaint.
    /// </summary>
    [Fact]
    public void PropertyName_WhenTooLong_ReportsOnlyTheLengthFailureOnBothVerbs()
    {
        string tooLong = new('a', PropertyNameWidth + 1);

        _create.Validate(NewCreate(create => create.PropertyName = tooLong)).Errors
            .Where(failure => failure.PropertyName == nameof(CreateProfilePropertyDefinitionRequest.PropertyName))
            .Should().HaveCount(1);

        _update.Validate(NewUpdate(update => update.PropertyName = tooLong)).Errors
            .Where(failure => failure.PropertyName == nameof(UpdateProfilePropertyDefinitionRequest.PropertyName))
            .Should().HaveCount(1);
    }

    // ------------------------------------------------------------------------
    // PropertyCategory - Required(True) + column width
    // ------------------------------------------------------------------------

    /// <summary>The property category is required on both verbs, reproducing <c>Required(True)</c> at L193.</summary>
    /// <param name="propertyCategory">The submitted category.</param>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t")]
    public void PropertyCategory_IsRequiredOnBothVerbs(string propertyCategory)
        => ShouldReportBoth(
            create => create.PropertyCategory = propertyCategory,
            update => update.PropertyCategory = propertyCategory,
            nameof(CreateProfilePropertyDefinitionRequest.PropertyCategory),
            PropertyCategoryRequired);

    /// <summary>The property category is bounded at the width of its column, on both verbs.</summary>
    /// <param name="length">The submitted length.</param>
    /// <param name="accepted">Whether that length is within the ceiling.</param>
    [Theory]
    [InlineData(49, true)]
    [InlineData(50, true)]
    [InlineData(51, false)]
    public void PropertyCategory_IsBoundedByItsColumnWidthOnBothVerbs(int length, bool accepted)
    {
        string category = new('a', length);

        if (accepted)
        {
            ShouldAcceptBoth(
                create => create.PropertyCategory = category,
                update => update.PropertyCategory = category);
        }
        else
        {
            ShouldReportBoth(
                create => create.PropertyCategory = category,
                update => update.PropertyCategory = category,
                nameof(CreateProfilePropertyDefinitionRequest.PropertyCategory),
                PropertyCategoryTooLong);
        }
    }

    /// <summary>
    /// The category is NOT restricted to the four headings the shipped defaults use, because the legacy
    /// editor was a free-text box and installations invent their own.
    /// </summary>
    /// <param name="propertyCategory">A heading outside the shipped four.</param>
    [Theory]
    [InlineData("Contact Info")]
    [InlineData("Employment History")]
    [InlineData("Custom")]
    public void PropertyCategory_AcceptsAnyHeading(string propertyCategory)
        => ShouldAcceptBoth(
            create => create.PropertyCategory = propertyCategory,
            update => update.PropertyCategory = propertyCategory);

    // ------------------------------------------------------------------------
    // ValidationExpression - bounded tenant-authored regular expression
    // ------------------------------------------------------------------------

    /// <summary>
    /// The validation expression is bounded at the 512-character write work-factor limit on both verbs.
    /// </summary>
    /// <param name="length">The submitted length.</param>
    /// <param name="accepted">Whether that length is within the ceiling.</param>
    /// <remarks>
    /// The terminal column remains <c>nvarchar(2000)</c> for immutable-schema compatibility. New writes are
    /// intentionally narrower because tenant-authored expressions are compiled and evaluated against
    /// profile values; 512 characters, the bounded collection size and the matching timeout together limit
    /// that work.
    /// </remarks>
    [Theory]
    [InlineData(100, true)]
    [InlineData(101, true)]
    [InlineData(511, true)]
    [InlineData(512, true)]
    [InlineData(513, false)]
    public void ValidationExpression_IsBoundedByItsWriteWorkFactorLimitOnBothVerbs(int length, bool accepted)
    {
        string expression = new('a', length);

        if (accepted)
        {
            ShouldAcceptBoth(
                create => create.ValidationExpression = expression,
                update => update.ValidationExpression = expression);
        }
        else
        {
            ShouldReportBoth(
                create => create.ValidationExpression = expression,
                update => update.ValidationExpression = expression,
                nameof(CreateProfilePropertyDefinitionRequest.ValidationExpression),
                ValidationExpressionTooLong);
        }
    }

    /// <summary>
    /// An absent or empty expression reports nothing, because the column is nullable and an unconstrained
    /// property is the ordinary case.
    /// </summary>
    /// <param name="validationExpression">The submitted expression.</param>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void ValidationExpression_WhenAbsentOrEmpty_ReportsNothing(string? validationExpression)
        => ShouldAcceptBoth(
            create => create.ValidationExpression = validationExpression,
            update => update.ValidationExpression = validationExpression);

    /// <summary>
    /// The expression is not itself parsed as a regular expression, and a syntactically invalid one is
    /// accepted on both verbs.
    /// </summary>
    [Fact]
    public void ValidationExpression_IsNotItselfCompiled()
        => ShouldAcceptBoth(
            create => create.ValidationExpression = "([unclosed",
            update => update.ValidationExpression = "([unclosed");

    // ------------------------------------------------------------------------
    // ABSENCES - members that must remain unconstrained, each for a measured reason
    // ------------------------------------------------------------------------

    /// <summary>
    /// The default value carries NO length rule on either verb, because its column was widened from
    /// <c>nvarchar(50)</c> to <c>ntext</c>.
    /// </summary>
    [Fact]
    public void DefaultValue_IsBoundedByNothingOnBothVerbs()
    {
        string enormous = new('a', ValidationExpressionWidth + 1000);

        ShouldAcceptBoth(
            create => create.DefaultValue = enormous,
            update => update.DefaultValue = enormous);
    }

    /// <summary>
    /// A view order of -1 is accepted on both verbs, because on the create contract -1 is an INSTRUCTION
    /// rather than an absence marker and no rule may refuse it.
    /// </summary>
    /// <param name="viewOrder">The submitted order.</param>
    /// <remarks>
    /// The terminal create procedure branches on <c>IF @vieworder = -1</c> (<c>04.06.00:L1112</c>) and
    /// substitutes the current maximum order plus one, so -1 is the only way a caller can say "append to
    /// the end". A lower-bound rule would remove that, which is why this test names the value explicitly.
    /// </remarks>
    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(39)]
    [InlineData(int.MaxValue)]
    public void ViewOrder_AcceptsEveryValueIncludingTheAppendInstruction(int viewOrder)
        => ShouldAcceptBoth(
            create => create.ViewOrder = viewOrder,
            update => update.ViewOrder = viewOrder);

    /// <summary>
    /// The data-type key carries NO rule on either verb, because it references the excluded lookup
    /// subsystem and no set exists here to test membership of.
    /// </summary>
    /// <param name="dataType">The submitted key.</param>
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(349)]
    public void DataType_CarriesNoRule(int dataType)
        => ShouldAcceptBoth(
            create => create.DataType = dataType,
            update => update.DataType = dataType);

    /// <summary>
    /// The length carries no rule on either verb, and zero in particular is accepted because it
    /// legitimately means "unbounded" for a text property and is the column's own default.
    /// </summary>
    /// <param name="length">The submitted length.</param>
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(50)]
    public void Length_CarriesNoRule(int length)
        => ShouldAcceptBoth(
            create => create.Length = length,
            update => update.Length = length);

    /// <summary>
    /// The create contract's module-definition key carries no bound test, so the values this schema really
    /// uses are accepted.
    /// </summary>
    /// <param name="moduleDefId">The submitted module-definition key.</param>
    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(int.MaxValue)]
    public void ModuleDefId_CarriesNoBoundTest(int? moduleDefId)
        => ShouldAccept(_create.Validate(NewCreate(create => create.ModuleDefId = moduleDefId)), "create");

    /// <summary>
    /// A submission that breaks both string rules reports BOTH members on both verbs, because class-level
    /// cascade continues.
    /// </summary>
    [Fact]
    public void ASubmissionBreakingBothStringRules_ReportsBoth()
    {
        ShouldReportBoth(
            create =>
            {
                create.PropertyName = string.Empty;
                create.PropertyCategory = string.Empty;
            },
            update =>
            {
                update.PropertyName = string.Empty;
                update.PropertyCategory = string.Empty;
            },
            nameof(CreateProfilePropertyDefinitionRequest.PropertyName),
            PropertyNameRequired);

        ShouldReportBoth(
            create =>
            {
                create.PropertyName = string.Empty;
                create.PropertyCategory = string.Empty;
            },
            update =>
            {
                update.PropertyName = string.Empty;
                update.PropertyCategory = string.Empty;
            },
            nameof(CreateProfilePropertyDefinitionRequest.PropertyCategory),
            PropertyCategoryRequired);
    }

    // ------------------------------------------------------------------------
    // MEMBER CENSUS - the contracts advertise only what the procedures honour
    // ------------------------------------------------------------------------

    /// <summary>
    /// The create contract carries EXACTLY the ten body members the terminal insert procedure declares, and
    /// nothing else.
    /// </summary>
    /// <remarks>
    /// <c>AddPropertyDefinition</c> (<c>04.06.00:L1101</c>) declares eleven parameters: <c>@PortalId</c>,
    /// which arrives from the route or from the resolved tenant rather than from the body, plus the ten
    /// asserted here.
    /// </remarks>
    [Fact]
    public void TheCreateContract_CarriesExactlyTheMembersTheInsertProcedureWrites()
        => MemberNamesOf<CreateProfilePropertyDefinitionRequest>().Should().BeEquivalentTo(
            new[]
            {
                "ModuleDefId",
                "DataType",
                "DefaultValue",
                "PropertyCategory",
                "PropertyName",
                "Length",
                "Required",
                "ValidationExpression",
                "ViewOrder",
                "Visible",
            });

    /// <summary>
    /// The update contract carries EXACTLY the nine body members the terminal update procedure declares,
    /// and in particular carries NO module-definition key.
    /// </summary>
    [Fact]
    public void TheUpdateContract_CarriesExactlyTheMembersTheUpdateProcedureWrites()
        => MemberNamesOf<UpdateProfilePropertyDefinitionRequest>().Should().BeEquivalentTo(
            new[]
            {
                "DataType",
                "DefaultValue",
                "PropertyCategory",
                "PropertyName",
                "Length",
                "Required",
                "ValidationExpression",
                "ViewOrder",
                "Visible",
            });

    /// <summary>
    /// Neither write contract advertises a member that arrives from the route, is assigned by the store, or
    /// is not a column on the table at all.
    /// </summary>
    [Fact]
    public void NeitherWriteContract_AdvertisesAMemberItCannotHonour()
    {
        string[] forbidden = ["PropertyDefinitionId", "PortalId", "Visibility", "IsDeleted", "Deleted"];

        MemberNamesOf<CreateProfilePropertyDefinitionRequest>().Should().NotIntersectWith(forbidden);
        MemberNamesOf<UpdateProfilePropertyDefinitionRequest>().Should().NotIntersectWith(forbidden);
    }

    /// <summary>Reads the public instance member names a contract publishes.</summary>
    /// <typeparam name="TContract">The contract to inspect.</typeparam>
    /// <returns>Every public readable and writable member name.</returns>
    private static IReadOnlyList<string> MemberNamesOf<TContract>()
        => typeof(TContract)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(property => property.Name)
            .ToList();
}
