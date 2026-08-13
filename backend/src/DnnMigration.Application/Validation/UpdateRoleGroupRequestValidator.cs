using DnnMigration.Application.Dtos.Role;
using FluentValidation;

namespace DnnMigration.Application.Validation;

/// <summary>
/// Field rules for <see cref="UpdateRoleGroupRequest"/>, the body of <c>PUT
/// /api/v1/role-groups/{roleGroupId}</c>.
/// </summary>
/// <remarks>
/// <b>What this type deliberately does not do.</b> It reads no store, so it asserts neither that the group
/// exists nor that the submitted name is free. Both are expected failures owned by
/// <c>Application/Services/RoleService.cs</c>, which excludes the group being edited from the uniqueness
/// comparison so that resubmitting a group's own name is a no-op rather than a self-collision.
/// </remarks>
// No presence rule on the description and no rule distinguishing null from the empty string, for the
// reasons recorded on the creation validator.
public sealed class UpdateRoleGroupRequestValidator : AbstractValidator<UpdateRoleGroupRequest>
{
    /// <summary>
    /// Initialises a new instance of the <see cref="UpdateRoleGroupRequestValidator"/> class and declares
    /// its rules.
    /// </summary>
    /// <remarks>
    /// Rule-level cascade stops at the first failure for a member and class-level cascade continues,
    /// matching the creation validator exactly, so one malformed submission reports every bad field in a
    /// single response while each field reports one actionable message.
    /// </remarks>
    public UpdateRoleGroupRequestValidator()
    {
        RuleLevelCascadeMode = CascadeMode.Stop;
        ClassLevelCascadeMode = CascadeMode.Continue;

        // valRoleGroupName (EditGroups.ascx L12) plus the maxlength on the box it guarded (L11).
        RuleFor(request => request.RoleGroupName)
            .NotEmpty()
            .WithMessage(RoleGroupTermsRules.RoleGroupNameRequiredMessage)
            .MaximumLength(RoleGroupTermsRules.RoleGroupNameMaximumLength);

        // No validator was declared on the description, so only the column width is asserted.
        RuleFor(request => request.Description)
            .MaximumLength(RoleGroupTermsRules.DescriptionMaximumLength);
    }
}
