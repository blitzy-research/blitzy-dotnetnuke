using DnnMigration.Application.Dtos.Role;
using FluentValidation;

namespace DnnMigration.Application.Validation;

/// <summary>
/// Field rules for <see cref="RoleGroupDto"/>, the body of both
/// <c>POST /api/v1/portals/{portalId}/role-groups</c> and
/// <c>PUT /api/v1/portals/{portalId}/role-groups/{roleGroupId}</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this type exists.</b> Both role-group write actions advertised a field-error response but
/// no <see cref="IValidator{T}"/> resolved for their body, so an absent name reached the store and
/// failed against a <c>NOT NULL</c> column, and an over-long name failed against the column width -
/// each producing a persistence fault rather than the declared per-field response. Both rules the
/// legacy screen declared are reproduced below, so the failure is now reported at the boundary, on
/// the offending member, before anything is attempted.
/// </para>
/// <para>
/// <b>One shape, two verbs, one validator.</b> The contract deliberately serves reads and both writes
/// with a single type, because the legacy editor posted the same two editable fields whether it was
/// inserting or updating (<c>EditGroups.ascx.vb</c> L107-L111). A single validator therefore governs
/// both verbs by construction, which removes any possibility of the two paths diverging - the defect
/// its sibling role contracts had to be repaired for.
/// </para>
/// <para>
/// <b>What this type deliberately does not do.</b> It bounds neither identifier, for reasons recorded
/// on the contract itself and restated inline below, and it asserts nothing about stored state. The
/// composite uniqueness constraint over <c>(PortalID, RoleGroupName)</c> is a question about what is
/// already stored, so a clash is an expected failure raised by
/// <c>Application/Services/RoleService.cs</c> and reported as a conflict, mirroring the
/// duplicate-group message the legacy editor emitted at <c>EditGroups.ascx.vb</c> L117. Keeping it
/// there is what allows this validator to be parameterless and synchronous.
/// </para>
/// </remarks>
// MIGRATION: both rules are measured from Website/admin/Security/EditGroups.ascx, and the file must be
// read case-insensitively to find them - the markup writes its tags in lower case, so a search for
// "RequiredFieldValidator" returns nothing while "requiredfieldvalidator" returns the rule. L11
// declares the name text box with maxlength="50" and L12 declares valRoleGroupName beside it, a
// requiredfieldvalidator whose errormessage is "<br>You Must Enter a Valid Name". L17 declares the
// description text box with maxlength="1000" and NO validator of any kind. That is the entire field
// rule set for this screen, and this validator reproduces it exactly: presence plus width on the
// name, width alone on the description.
//
// MIGRATION: the name message is the same sentence the sibling role editor used, so it is taken from
// the shared RoleTermsRules rather than restated. The two screens genuinely declared identical
// wording - editroles.ascx L31 and EditGroups.ascx L12 both read "<br>You Must Enter a Valid Name" -
// so sharing the constant reproduces the legacy text rather than harmonising two different texts into
// one. The two widths happen to coincide with the sibling role table's as well, but they are declared
// here from this table's own columns because they are a different table's constraints and must remain
// free to differ.
//
// MIGRATION: the presence rule is NotEmpty rather than NotNull, and the difference is observable.
// RoleGroups.RoleGroupName is nvarchar(50) NOT NULL (03.02.03.SqlDataProvider L20, recreated
// identically at 04.00.04.SqlDataProvider L53), and NOT NULL is satisfied by the empty string - so
// the legacy code-behind, which assigned the text box verbatim at EditGroups.ascx.vb L110, could in
// principle have stored one. It could not in practice, because the client-side requiredfieldvalidator
// refused an empty box before the postback ever ran and the server re-checked it through Page.IsValid
// at L106. NotEmpty is therefore the faithful translation of the rule as the operator experienced it;
// NotNull alone would admit a value that no legacy submission could produce.
//
// MIGRATION: neither identifier is bounded, and this is a deliberate omission rather than an
// oversight. RoleGroups.RoleGroupID is seeded IDENTITY(0,1) at 03.02.03.SqlDataProvider L18, so zero
// identifies the first group ever created; and the portal is route-authoritative, resolved once per
// request, with dbo.Portals.PortalID itself seeded IDENTITY(-1,1). Any "greater than zero" bound on
// either member would refuse a legitimate value, and any "non-positive means absent" test would
// silently exclude a real row. The contract records that the service ignores whatever arrives in
// either member, so there is nothing here for a rule to protect.
//
// MIGRATION: the legacy sentinel -1, which the editor used in the identifier to choose between
// inserting and updating (EditGroups.ascx.vb L42, L68 and L113), is deliberately not recognised. The
// operation is known from the route, so no rule needs to admit, refuse or interpret that value.
//
// MIGRATION: no rule on the description beyond its width, and in particular no presence rule and no
// rule distinguishing null from the empty string. The legacy read path funnelled every string through
// Null.SetNull, whose string sentinel is the empty string (Null.vb L70), so a stored NULL and a stored
// empty description were indistinguishable once loaded and the editor round-tripped both as an empty
// box. Which of the two a null submission becomes on the way to the column is settled in
// Application/Mapping/RoleMappings.cs, the one place that translates between this contract and the
// persisted model, and asserting anything about it here would move that decision into two places.
public class RoleGroupDtoValidator : AbstractValidator<RoleGroupDto>
{
    /// <summary>
    /// Width of <c>RoleGroups.RoleGroupName nvarchar(50) NOT NULL</c>
    /// (<c>03.02.03.SqlDataProvider</c> L20, recreated identically at
    /// <c>04.00.04.SqlDataProvider</c> L53), which the legacy screen mirrored as
    /// <c>maxlength="50"</c> on its name text box at <c>EditGroups.ascx</c> L11.
    /// </summary>
    private const int RoleGroupNameMaximumLength = 50;

    /// <summary>
    /// Width of <c>RoleGroups.Description nvarchar(1000) NULL</c>
    /// (<c>03.02.03.SqlDataProvider</c> L21), which the legacy screen mirrored as
    /// <c>maxlength="1000"</c> on its description text box at <c>EditGroups.ascx</c> L17.
    /// </summary>
    private const int DescriptionMaximumLength = 1000;

    /// <summary>
    /// Initialises a new instance of the <see cref="RoleGroupDtoValidator"/> class and declares its
    /// rules.
    /// </summary>
    /// <remarks>
    /// Rule-level cascade stops at the first failure for a member, so a caller reads one actionable
    /// message per field rather than a length complaint stacked on a presence complaint. Class-level
    /// cascade continues, so a submission that is wrong in both members reports both in a single
    /// response.
    /// </remarks>
    public RoleGroupDtoValidator()
    {
        RuleLevelCascadeMode = CascadeMode.Stop;
        ClassLevelCascadeMode = CascadeMode.Continue;

        // valRoleGroupName (EditGroups.ascx L12) plus the maxlength on the box it guarded (L11).
        // This is the screen's only field validator and the only unconditional rule here.
        RuleFor(group => group.RoleGroupName)
            .NotEmpty()
            .WithMessage(RoleTermsRules.RoleNameRequiredMessage)
            .MaximumLength(RoleGroupNameMaximumLength);

        // No validator was declared on the description, so only the column width is asserted. The
        // rule is inert for an absent value: a length check passes a null.
        RuleFor(group => group.Description)
            .MaximumLength(DescriptionMaximumLength);
    }
}
