using DnnMigration.Application.Dtos.Portal;
using FluentValidation;

namespace DnnMigration.Application.Validation;

/// <summary>
/// Declares the field rules for <see cref="UpdatePortalAliasRequest"/>, the payload submitted to
/// change an existing alias.
/// </summary>
/// <remarks>
/// <para>
/// The rules are the create contract's rules, and deliberately so: the value is bound for the same
/// column under the same unique constraint, so a shape accepted by one verb and refused by the other
/// would be a shape a caller could store by choosing the other verb. Both validators call
/// <see cref="PortalAliasRules"/> rather than restating anything.
/// </para>
/// <para>
/// <b>The legacy update path validated nothing at all.</b>
/// <c>Website/admin/Portal/editportalalias.ascx</c> declares no validator of any kind over its one
/// input, and <c>EditPortalAlias.ascx.vb</c> discovered a collision only by catching the exception
/// the unique constraint raised, at <c>L223-L228</c>. Shape is checked here, before the store is
/// reached; the collision remains the service's answer, reported as <c>portal.alias_duplicate</c>,
/// because only the store knows what is already bound.
/// </para>
/// <para>
/// Parameterless by design, for the same reason as its sibling: no rule below asks a question about
/// state.
/// </para>
/// </remarks>
public class UpdatePortalAliasRequestValidator : AbstractValidator<UpdatePortalAliasRequest>
{
    /// <summary>
    /// Initialises a new instance of the <see cref="UpdatePortalAliasRequestValidator"/> class and
    /// declares its rules.
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
