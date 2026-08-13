using DnnMigration.Application.Dtos.User;
using FluentValidation;

namespace DnnMigration.Application.Validation;

/// <summary>
/// Declares the field rules for <see cref="CreateProfilePropertyDefinitionRequest"/>, the payload submitted
/// to <c>POST /api/v1/profile-definitions</c> to declare one new profile property for the resolved portal.
/// </summary>
/// <remarks>
/// <b>Why a separate validator from the update path.</b> The two verbs no longer share a request type,
/// because the terminal procedures do not honour the same member set: <c>AddPropertyDefinition</c>
/// (<c>04.06.00:L1101</c>) takes a module-definition key that <c>UpdatePropertyDefinition</c>
/// (<c>04.05.00:L1685</c>) does not.
/// </remarks>
// An ASP.NET RegularExpressionValidator SUCCEEDS against an empty control by design - it validates format,
// not presence, and defers presence to the RequiredFieldValidator beside it.
public sealed class CreateProfilePropertyDefinitionRequestValidator
    : AbstractValidator<CreateProfilePropertyDefinitionRequest>
{
    /// <summary>
    /// Initialises a new instance of the <see cref="CreateProfilePropertyDefinitionRequestValidator"/>
    /// class and declares its rules.
    /// </summary>
    /// <remarks>
    /// Rule-level cascade stops at the first failure for a member, which is what reproduces the legacy
    /// pairing of a presence check with a format check: an omitted name reports one actionable message
    /// rather than two. Class-level cascade continues, so one malformed submission names every bad field in
    /// a single response instead of forcing a caller to discover them one round trip at a time.
    /// </remarks>
    public CreateProfilePropertyDefinitionRequestValidator()
    {
        RuleLevelCascadeMode = CascadeMode.Stop;
        ClassLevelCascadeMode = CascadeMode.Continue;

        RuleFor(request => request.PropertyName)
            .NotEmpty().WithMessage(ProfileDefinitionTermsRules.PropertyNameRequiredMessage)
            .MaximumLength(ProfileDefinitionTermsRules.PropertyNameMaximumLength)
                .WithMessage(ProfileDefinitionTermsRules.PropertyNameTooLongMessage)
            .Matches(ProfileDefinitionTermsRules.PropertyNamePattern)
                .WithMessage(ProfileDefinitionTermsRules.PropertyNameInvalidMessage);

        RuleFor(request => request.PropertyCategory)
            .NotEmpty().WithMessage(ProfileDefinitionTermsRules.PropertyCategoryRequiredMessage)
            .MaximumLength(ProfileDefinitionTermsRules.PropertyCategoryMaximumLength)
                .WithMessage(ProfileDefinitionTermsRules.PropertyCategoryTooLongMessage);

        // No legacy validator, but a NULLABLE column with a terminal width: an over-long expression would
        // be truncated or refused by the store rather than reported to the caller.
        RuleFor(request => request.ValidationExpression)
            .MaximumLength(ProfileDefinitionTermsRules.ValidationExpressionMaximumLength)
            .WithMessage(ProfileDefinitionTermsRules.ValidationExpressionTooLongMessage);
    }
}
