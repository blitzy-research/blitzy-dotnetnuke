using DnnMigration.Application.Dtos.User;
using FluentValidation;

namespace DnnMigration.Application.Validation;

/// <summary>
/// Declares the field rules for <see cref="UpdateProfilePropertyDefinitionRequest"/>, the payload submitted
/// to <c>PUT /api/v1/profile-definitions/{propertyDefinitionId}</c> to amend one profile property the
/// resolved portal already collects.
/// </summary>
// An ASP.NET RegularExpressionValidator SUCCEEDS against an empty control by design - it validates format,
// not presence, and defers presence to the RequiredFieldValidator beside it.
public sealed class UpdateProfilePropertyDefinitionRequestValidator
    : AbstractValidator<UpdateProfilePropertyDefinitionRequest>
{
    /// <summary>
    /// Initialises a new instance of the <see cref="UpdateProfilePropertyDefinitionRequestValidator"/>
    /// class and declares its rules.
    /// </summary>
    /// <remarks>
    /// Rule-level cascade stops at the first failure for a member, which is what reproduces the legacy
    /// pairing of a presence check with a format check: an omitted name reports one actionable message
    /// rather than two. Class-level cascade continues, so one malformed submission names every bad field in
    /// a single response instead of forcing a caller to discover them one round trip at a time.
    /// </remarks>
    public UpdateProfilePropertyDefinitionRequestValidator()
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
                .WithMessage(ProfileDefinitionTermsRules.PropertyCategoryTooLongMessage)
            .Must(TextIntegrityRules.IsSingleLineSafe)
            .WithMessage(TextIntegrityRules.SingleLineMessage);

        // No legacy validator, but a NULLABLE column with a terminal width: an over-long expression would
        // be truncated or refused by the store rather than reported to the caller.
        RuleFor(request => request.ValidationExpression)
            .MaximumLength(ProfileDefinitionTermsRules.ValidationExpressionMaximumLength)
            .WithMessage(ProfileDefinitionTermsRules.ValidationExpressionTooLongMessage);
    }
}
