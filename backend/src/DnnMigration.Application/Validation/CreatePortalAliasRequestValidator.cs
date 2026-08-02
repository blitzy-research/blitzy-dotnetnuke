using DnnMigration.Application.Dtos.Portal;
using FluentValidation;

namespace DnnMigration.Application.Validation;

/// <summary>
/// Declares the field rules for <see cref="CreatePortalAliasRequest"/>, the payload submitted to
/// bind a new alias to a portal.
/// </summary>
/// <remarks>
/// <para>
/// Every rule delegates to <see cref="PortalAliasRules"/>, which the update validator also calls, so
/// the two write paths cannot enforce different shapes for the same uniquely indexed column. The
/// bound, the message wording and the acceptance test are each declared exactly once, there.
/// </para>
/// <para>
/// <b>Requiring the alias is net-new, and it is a tightening.</b> The legacy screen refused to act
/// on an empty box - <c>Website/admin/Portal/EditPortalAlias.ascx.vb:L209</c> wraps the entire write
/// in <c>If strAlias &lt;&gt; ""</c> - but it also said nothing, redirected nowhere and reported no
/// error, so an operator who submitted an empty field saw the form again with no explanation. A
/// contract that must answer cannot reproduce silence, so an absent alias becomes a field-level
/// failure. Nothing that previously succeeded now fails.
/// </para>
/// <para>
/// Parameterless by design. Every rule is a question about the shape of the submission; whether the
/// host name is already bound is a question about stored state and belongs to the service, which
/// reports it as <c>portal.alias_duplicate</c>.
/// </para>
/// </remarks>
public class CreatePortalAliasRequestValidator : AbstractValidator<CreatePortalAliasRequest>
{
    /// <summary>
    /// Initialises a new instance of the <see cref="CreatePortalAliasRequestValidator"/> class and
    /// declares its rules.
    /// </summary>
    public CreatePortalAliasRequestValidator()
    {
        RuleFor(request => request.HttpAlias)
            .NotEmpty()
            .WithMessage(PortalAliasRules.RequiredMessage)
            .MaximumLength(PortalAliasRules.MaximumLength)
            .WithMessage(PortalAliasRules.TooLongMessage)
            .Must(PortalAliasRules.IsAcceptable)
            .WithMessage(PortalAliasRules.InvalidMessage);
    }
}
