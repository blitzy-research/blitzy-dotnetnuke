using DnnMigration.Application.Dtos.Role;
using FluentValidation;

namespace DnnMigration.Application.Validation;

/// <summary>
/// Field rules for <see cref="CreateRoleGroupRequest"/>, the body of <c>POST /api/v1/role-groups</c>.
/// </summary>
/// <remarks>
/// <b>Why this type exists.</b> Both role-group write actions advertise a field-error response, and both
/// need a validator resolved for their body or an absent name reaches the store and fails against a <c>NOT
/// NULL</c> column while an over-long one fails against the column width - each producing a persistence
/// fault rather than the declared per-field response.
/// </remarks>
public sealed class CreateRoleGroupRequestValidator : AbstractValidator<CreateRoleGroupRequest>
{
    /// <summary>
    /// Initialises a new instance of the <see cref="CreateRoleGroupRequestValidator"/> class and declares
    /// its rules.
    /// </summary>
    /// <remarks>
    /// Rule-level cascade stops at the first failure for a member, so a caller reads one actionable message
    /// per field rather than a length complaint stacked on a presence complaint. Class-level cascade
    /// continues, so a submission that is wrong in both members reports both in a single response.
    /// </remarks>
    public CreateRoleGroupRequestValidator()
    {
        RuleLevelCascadeMode = CascadeMode.Stop;
        ClassLevelCascadeMode = CascadeMode.Continue;

        // valRoleGroupName (EditGroups.ascx L12) plus the maxlength on the box it guarded (L11).
        // This is the screen's only field validator and the only unconditional rule here.
        RuleFor(request => request.RoleGroupName)
            .NotEmpty()
            .WithMessage(RoleGroupTermsRules.RoleGroupNameRequiredMessage)
            .MaximumLength(RoleGroupTermsRules.RoleGroupNameMaximumLength);

        // No validator was declared on the description, so only the column width is asserted. The
        // rule is inert for an absent value: a length check passes a null.
        RuleFor(request => request.Description)
            .MaximumLength(RoleGroupTermsRules.DescriptionMaximumLength);
    }
}
