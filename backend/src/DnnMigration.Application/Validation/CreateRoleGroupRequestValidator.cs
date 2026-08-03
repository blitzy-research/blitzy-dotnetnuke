using DnnMigration.Application.Dtos.Role;
using FluentValidation;

namespace DnnMigration.Application.Validation;

/// <summary>
/// Field rules for <see cref="CreateRoleGroupRequest"/>, the body of
/// <c>POST /api/v1/portals/{portalId}/role-groups</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this type exists.</b> Both role-group write actions advertise a field-error response, and
/// both need a validator resolved for their body or an absent name reaches the store and fails against
/// a <c>NOT NULL</c> column while an over-long one fails against the column width - each producing a
/// persistence fault rather than the declared per-field response. The predecessor of this type bound
/// the RESPONSE projection, which meant one validator governed both verbs by accident of the shape they
/// shared; the two verbs now have their own contracts, so each has its own validator and the rules they
/// share are shared explicitly through <see cref="RoleGroupTermsRules"/>.
/// </para>
/// <para>
/// <b>Member coverage is exactly the contract's two members.</b> Both carry a rule: presence and width
/// on the name, width alone on the description. Nothing here checks a member the contract does not
/// declare - in particular neither identifier is bounded, because the contract carries neither.
/// </para>
/// <para>
/// <b>What this type deliberately does not do.</b> It reads no store, so it asserts neither that the
/// portal exists nor that the name is free. The composite uniqueness constraint over
/// <c>(PortalID, RoleGroupName)</c> is a question about stored state, so a clash is an expected failure
/// raised by <c>Application/Services/RoleService.cs</c> and reported as a conflict, mirroring the
/// duplicate-group message the legacy editor emitted at <c>EditGroups.ascx.vb</c> L117. Keeping it there
/// is what allows this validator to be parameterless and synchronous.
/// </para>
/// </remarks>
// MIGRATION: the presence rule is NotEmpty rather than NotNull, and the difference is observable.
// RoleGroups.RoleGroupName is nvarchar(50) NOT NULL (03.02.03.SqlDataProvider L20, recreated identically
// at 04.00.04.SqlDataProvider L53), and NOT NULL is satisfied by the empty string - so the legacy
// code-behind, which assigned the text box verbatim at EditGroups.ascx.vb L110, could in principle have
// stored one. It could not in practice, because the client-side requiredfieldvalidator refused an empty
// box before the postback ever ran and the server re-checked it through Page.IsValid at L106. NotEmpty is
// therefore the faithful translation of the rule as the operator experienced it; NotNull alone would admit
// a value no legacy submission could produce.
//
// MIGRATION: no rule on the description beyond its width, and in particular no presence rule and no rule
// distinguishing null from the empty string. The legacy screen declared no validator on that box at all
// (EditGroups.ascx L17), and the legacy read path funnelled every string through Null.SetNull, whose
// string sentinel is the empty string (Null.vb L70), so a stored NULL and a stored empty description were
// indistinguishable once loaded. Which of the two a null submission becomes on the way to the column is
// settled in Application/Mapping/RoleMappings.cs, and asserting anything about it here would move that
// decision into two places.
public sealed class CreateRoleGroupRequestValidator : AbstractValidator<CreateRoleGroupRequest>
{
    /// <summary>
    /// Initialises a new instance of the <see cref="CreateRoleGroupRequestValidator"/> class and
    /// declares its rules.
    /// </summary>
    /// <remarks>
    /// Rule-level cascade stops at the first failure for a member, so a caller reads one actionable
    /// message per field rather than a length complaint stacked on a presence complaint. Class-level
    /// cascade continues, so a submission that is wrong in both members reports both in a single
    /// response. Both settings match the sibling update validator exactly.
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
