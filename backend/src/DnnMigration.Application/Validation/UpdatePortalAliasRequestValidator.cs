using DnnMigration.Application.Dtos.Portal;
using FluentValidation;

namespace DnnMigration.Application.Validation;

/// <summary>
/// Declares the field rules for <see cref="UpdatePortalAliasRequest"/>, the payload submitted to change an
/// existing alias.
/// </summary>
/// <remarks>
/// The rules are the create contract's rules, and deliberately so: the value is bound for the same column
/// under the same unique constraint, so a shape accepted by one verb and refused by the other would be a
/// shape a caller could store by choosing the other verb. Both validators call <see
/// cref="PortalAliasRules"/> rather than restating anything.
/// </remarks>
public class UpdatePortalAliasRequestValidator : AbstractValidator<UpdatePortalAliasRequest>
{
    /// <summary>
    /// Initialises a new instance of the <see cref="UpdatePortalAliasRequestValidator"/> class and declares
    /// its rules.
    /// </summary>
    public UpdatePortalAliasRequestValidator()
    {
        RuleFor(request => request.HttpAlias)
            .NotEmpty()
            .WithMessage(PortalAliasRules.RequiredMessage)
            .MaximumLength(PortalAliasRules.MaximumLength)
            .WithMessage(PortalAliasRules.TooLongMessage)
            .Must(PortalAliasRules.IsAcceptable)
            .WithMessage(PortalAliasRules.InvalidMessage)
            .Must(PortalAliasRules.IsWithinSupportedTopology)
            .WithMessage(PortalAliasRules.UnsupportedPathMessage);
    }
}
