using DnnMigration.Application.Dtos.Role;
using FluentValidation;

namespace DnnMigration.Application.Validation;

/// <summary>
/// Field rules for <see cref="UpdateRoleGroupRequest"/>, the body of
/// <c>PUT /api/v1/portals/{portalId}/role-groups/{roleGroupId}</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>The rules are literally the creation path's rules.</b> Both validators consume
/// <see cref="RoleGroupTermsRules"/>, so the wording, the widths and the operators cannot differ between
/// the two verbs. That property matters more than it looks: the legacy editor served creating and
/// editing from one postback, so every validator it declared fired on both operations by construction,
/// and splitting the operations into two routed endpoints is exactly what would otherwise allow the two
/// paths to carry different rules.
/// </para>
/// <para>
/// <b>Member coverage is exactly the contract's two members.</b> Both carry a rule. Nothing here checks
/// a member the contract does not declare, and in particular neither the group's identifier nor its
/// portal is bounded, because the contract carries neither: both arrive in the route.
/// </para>
/// <para>
/// <b>What this type deliberately does not do.</b> It reads no store, so it asserts neither that the
/// group exists nor that the submitted name is free. Both are expected failures owned by
/// <c>Application/Services/RoleService.cs</c>, which excludes the group being edited from the uniqueness
/// comparison so that resubmitting a group's own name is a no-op rather than a self-collision.
/// </para>
/// </remarks>
// MIGRATION: no presence rule on the description and no rule distinguishing null from the empty string,
// for the reasons recorded on the creation validator. The presence rule on the NAME is present on both
// verbs because the column is NOT NULL: unlike the description, the name has no cleared state for an
// omitted value to mean, so the replacement discipline that makes an omitted nullable member a clearing
// instruction does not reach it.
public sealed class UpdateRoleGroupRequestValidator : AbstractValidator<UpdateRoleGroupRequest>
{
    /// <summary>
    /// Initialises a new instance of the <see cref="UpdateRoleGroupRequestValidator"/> class and
    /// declares its rules.
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
