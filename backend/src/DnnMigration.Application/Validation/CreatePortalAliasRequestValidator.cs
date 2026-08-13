using DnnMigration.Application.Dtos.Portal;
using FluentValidation;

namespace DnnMigration.Application.Validation;

/// <summary>
/// Declares the field rules for <see cref="CreatePortalAliasRequest"/>, the payload submitted to bind a new
/// alias to a portal.
/// </summary>
/// <remarks>
/// Every rule delegates to <see cref="PortalAliasRules"/>, which the update validator also calls, so the
/// two write paths cannot enforce different shapes for the same uniquely indexed column. The bound, the
/// message wording and the acceptance test are each declared exactly once, there.
/// </remarks>
public class CreatePortalAliasRequestValidator : AbstractValidator<CreatePortalAliasRequest>
{
    /// <summary>
    /// Initialises a new instance of the <see cref="CreatePortalAliasRequestValidator"/> class and declares
    /// its rules.
    /// </summary>
    public CreatePortalAliasRequestValidator()
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
