namespace DnnMigration.Application.Validation;

/// <summary>
/// The field rules shared by the two profile-definition write contracts - the terminal column widths, the
/// legacy name pattern and the authored wording of each refusal - declared once and applied by both the
/// create and the update validator.
/// </summary>
/// <remarks>
/// <b>One definition, two callers.</b> A property name submitted on create and a property name submitted on
/// update are the same value bound for the same column, so a rule that held on one path and not the other
/// would be a rule a caller could bypass simply by choosing the other verb.
/// </remarks>
// Provenance of every width, measured across all 88 upgrade scripts case-insensitively and across the bare,
// owner-qualified, bracketed and templated naming forms.
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
    /// Security work-factor limit for tenant-authored expressions. The column remains mapped at its
    /// immutable terminal width of 2000; new writes are intentionally constrained more narrowly so a policy
    /// value cannot consume disproportionate parser and matching work.
    /// </summary>
    internal const int ValidationExpressionMaximumLength = 512;
}
