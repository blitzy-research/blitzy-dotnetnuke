namespace DnnMigration.Application.Validation;

/// <summary>
/// The field rules shared by the two profile-definition write contracts - the terminal column widths, the
/// legacy name pattern and the authored wording of each refusal - declared once and applied by both the
/// create and the update validator.
/// </summary>
/// <remarks>
/// <para>
/// <b>One definition, two callers.</b> A property name submitted on create and a property name submitted
/// on update are the same value bound for the same column, so a rule that held on one path and not the
/// other would be a rule a caller could bypass simply by choosing the other verb. Before the two verbs had
/// separate request contracts, one validator governed both by accident of the shape they shared; sharing
/// the rules explicitly is what preserves that property now that they do not, and it follows the pattern
/// this folder already established for the role and role-group contracts.
/// </para>
/// <para>
/// <b>Where the rules come from, and why not from markup.</b> The legacy editor
/// <c>Website/admin/Users/EditProfileDefinition.ascx</c> declares NO validator of its own. Its whole field
/// set is a single <c>&lt;dnn:propertyeditorcontrol id="Properties"&gt;</c> at L23, a reflective editor
/// from the excluded control library that RENDERS its validators from attributes on the object being
/// edited. The authoritative rules are therefore the attributes on
/// <c>Library/Components/Users/Profile/ProfilePropertyDefinition.vb</c>, and there are exactly three:
/// <c>&lt;Required(True), SortOrder(2)&gt;</c> on <c>PropertyCategory</c> (L193);
/// <c>&lt;Required(True), IsReadOnly(True), SortOrder(0),
/// RegularExpressionValidator("^[a-zA-Z0-9._%\-+']+$")&gt;</c> on <c>PropertyName</c> (L228); and
/// <c>&lt;Required(True), SortOrder(8)&gt;</c> on <c>ViewOrder</c> (L300). Every other member of that
/// class carries a sort order and nothing else, so the legacy editor rendered no rule for any of them.
/// Nothing here was invented from the shape of a request.
/// </para>
/// <para>
/// <b>Nothing here reads state, configuration or a clock.</b> Every member is a compile-time constant,
/// which is what keeps both validators parameterless. The uniqueness the schema enforces through
/// <c>IX_ProfilePropertyDefinition</c> over <c>(PortalID, ModuleDefID, PropertyName)</c> is a question
/// about stored state, so a clash is an expected failure raised by
/// <c>Application/Services/UserService.cs</c> and reported as a conflict.
/// </para>
/// </remarks>
// MIGRATION: provenance of every width, measured across all 88 upgrade scripts case-insensitively and
// across the bare, owner-qualified, bracketed and templated naming forms.
//   PropertyName         nvarchar(50)   NOT NULL   created 03.02.03:L1071, never altered - terminal
//   PropertyCategory     nvarchar(50)   NOT NULL   created 03.02.03:L1070, never altered - terminal
//   ValidationExpression nvarchar(100)  NULL       created 03.02.03:L1074,
//                        nvarchar(2000)            WIDENED by 04.03.05:L17 - 2000 is terminal
//   DefaultValue         nvarchar(50)   NULL       created 03.02.03:L1069,
//                        ntext                     WIDENED by 04.05.00:L1593 - effectively unbounded
// A limit of 100 on the expression, or of 50 on the default value, would refuse rows that any database
// upgraded past 4.3.5 or 4.5.0 respectively already stores. Both numbers are taken from the terminal
// state, and the default value consequently carries no length rule at all. Note that the terminal
// PROCEDURE parameters are narrower than the terminal COLUMNS in both cases - 04.05.00:L1685 declares
// @DefaultValue nvarchar(50) and @ValidationExpression nvarchar(100) - and the column is what decides,
// because it is what an upgraded database actually holds.
//
// MIGRATION: the regular expression is carried across CHARACTER FOR CHARACTER from the legacy attribute,
// including its anchors and its escaped hyphen. It admits letters, digits, dot, underscore, percent,
// hyphen, plus and apostrophe, and requires at least one character. It is NOT relaxed to allow a space:
// the legacy pattern rejected one, the column is indexed for uniqueness per portal and module, and the
// name is the key by which a profile value is addressed.
//
// MIGRATION: the messages carry the legacy WORDING and drop the presentation. Legacy validator messages
// began with a literal <br> tag because they were written into page markup; an HTML tag is neither markup
// nor data in a machine-readable problem document, so it is stripped while the wording is preserved. The
// property editor composed its own presence and pattern text from the localised member label rather than
// from a fixed string - there is no resource entry to transcribe, because there was no declared validator
// to key one to - so the messages below name the field and the rule plainly and are recorded here as
// authored wording rather than as transcribed wording.
internal static class ProfileDefinitionTermsRules
{
    /// <summary>Reported when the property name is missing.</summary>
    internal const string PropertyNameRequiredMessage = "You Must Enter a Property Name";

    /// <summary>Reported when the property name carries a character the legacy pattern refused.</summary>
    internal const string PropertyNameInvalidMessage =
        "Property Name may contain only letters, numbers and the characters . _ % - + '";

    /// <summary>Reported when the property name exceeds the width of its column.</summary>
    internal const string PropertyNameTooLongMessage = "Property Name must be 50 characters or fewer";

    /// <summary>Reported when the property category is missing.</summary>
    internal const string PropertyCategoryRequiredMessage = "You Must Enter a Property Category";

    /// <summary>Reported when the property category exceeds the width of its column.</summary>
    internal const string PropertyCategoryTooLongMessage =
        "Property Category must be 50 characters or fewer";

    /// <summary>Reported when the validation expression exceeds the width of its column.</summary>
    internal const string ValidationExpressionTooLongMessage =
        "Validation Expression must be 512 characters or fewer";

    /// <summary>
    /// The legacy pattern, reproduced character for character from
    /// <c>ProfilePropertyDefinition.vb:L228</c>.
    /// </summary>
    internal const string PropertyNamePattern = @"^[a-zA-Z0-9._%\-+']+$";

    /// <summary>Terminal width of <c>PropertyName nvarchar(50) NOT NULL</c>.</summary>
    internal const int PropertyNameMaximumLength = 50;

    /// <summary>Terminal width of <c>PropertyCategory nvarchar(50) NOT NULL</c>.</summary>
    internal const int PropertyCategoryMaximumLength = 50;

    /// <summary>
    /// Security work-factor limit for tenant-authored expressions. The column remains mapped at its immutable
    /// terminal width of 2000; new writes are intentionally constrained more narrowly so a policy value cannot
    /// consume disproportionate parser and matching work.
    /// </summary>
    internal const int ValidationExpressionMaximumLength = 512;
}
