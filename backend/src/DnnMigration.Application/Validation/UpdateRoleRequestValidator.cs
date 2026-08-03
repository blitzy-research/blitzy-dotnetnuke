using DnnMigration.Application.Dtos.Role;
using DnnMigration.Domain.Common;
using FluentValidation;

namespace DnnMigration.Application.Validation;

/// <summary>
/// Field rules for <see cref="UpdateRoleRequest"/>, the body of
/// <c>PUT /api/v1/portals/{portalId}/roles/{roleId}</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this type exists.</b> The update endpoint advertised a field-error response but no
/// <c>IValidator&lt;UpdateRoleRequest&gt;</c> resolved for its body, so semantically invalid input -
/// a negative fee, a billing period of zero, an over-long invitation code, a stored character that
/// is not a known frequency - travelled past the boundary untested. It then surfaced either as an
/// ordinary single-message problem document raised further in, or as a persistence failure, in both
/// cases carrying nothing a caller could use to correct a specific field. Every rule below already
/// applied to the sibling creation path; none of them is new to the application, and none of them is
/// invented here.
/// </para>
/// <para>
/// <b>The rules are literally the creation path's rules.</b> Both validators consume
/// <see cref="RoleTermsRules"/>, so the wording, the widths, the operators and the icon-path
/// predicate cannot differ between the two verbs. The most consequential consequence is the icon
/// path: the containment rule that rejects a rooted or upward-traversing value was declared on the
/// creation validator alone, which meant a value <c>POST</c> refused was stored verbatim by
/// <c>PUT</c> against the same <c>Roles.IconFile</c> column. Sharing the definition closes that and
/// prevents it reopening.
/// </para>
/// <para>
/// <b>Member coverage is exactly the contract's twelve members.</b> Ten of them carry a rule; the two
/// flags carry none, for the reason stated inline. Nothing here checks a member the contract does not
/// declare - in particular there is no name rule and no name length, because the update contract
/// deliberately has no <c>RoleName</c> member and adding one is forbidden by that contract's own
/// documentation.
/// </para>
/// <para>
/// <b>What this type deliberately does not do.</b> It reads no store, so it asserts neither that the
/// role exists nor that the portal does - both are expected failures owned by
/// <c>Application/Services/RoleService.cs</c>. It performs no cross-field gating between a fee and
/// its frequency: the legacy screen revealed the billing block only for a non-zero fee and the trial
/// block only for a non-none trial frequency, which is a rendering decision about which inputs were
/// reachable rather than a rule about which combinations were legal, and the terminal stored
/// procedure accepts every column independently with a null default. It computes no dates: the
/// expiry arithmetic driven by these terms belongs to the service. And it takes no constructor
/// parameter, because no rule below depends on configuration.
/// </para>
/// </remarks>
// MIGRATION: this validator is the update-path half of a rule set the legacy application expressed
// once, on one screen, for both operations. Website/admin/Security/editroles.ascx served creating and
// editing from a single postback - EditRoles.ascx.vb L131 switched on the identifier minus one to
// decide which - so every validator declared on that markup fired on both paths by construction. The
// migrated design splits the operations into two routed endpoints, and that split is what allowed the
// two paths to carry different rules. Restoring the legacy property, that both operations validate
// identically, is the whole purpose of this file.
//
// MIGRATION: no presence rule on any member, and the absence is measured rather than assumed. The
// screen declared exactly one required-field validator, valRoleName at editroles.ascx L31, and the
// edit path DISABLED it at EditRoles.ascx.vb L134 because the name was displayed read-only. Every
// other input on that screen was optional, and the terminal procedure at
// 04.00.04.SqlDataProvider L454 defaults each of its twelve value parameters to null. Adding a
// presence rule to any member here would refuse a submission the legacy screen accepted.
//
// MIGRATION: this is a full replacement, not a partial edit, and that is exactly why an omitted
// nullable member must NOT be treated as a validation failure. The request contract records the
// discipline: every member is applied as supplied, so omitting one clears the stored value. A
// validator that demanded a value would make the clearing operation unreachable.
//
// MIGRATION: the two frequency members are checked for membership of the shared domain enumeration
// and never against a literal list of characters. The reasoning is measured on the creation
// validator and is not restated here, but its cross-layer consequence bears repeating because this
// is the write path a stored oddity would travel back through: the shipped seed data carries codes
// the enumeration cannot represent - 01.00.00.SqlDataProvider L7192 seeds Administrators with '4'
// and L7194 seeds Registered Users with '0' - so a caller who reads such a role and resubmits it
// unchanged will be refused on this rule. That is the intended outcome. Tolerance for those rows
// lives on the READ path, in Domain/Enums/BillingFrequency.cs and Application/Mapping/RoleMappings.cs,
// and must never be retro-fitted here by weakening the rule, which would let new rows be created at
// an undocumented frequency rather than helping the two that already exist.
//
// MIGRATION: no upper bound on either fee. The baseline declared ServiceFee as decimal(5, 2) at
// 01.00.00.SqlDataProvider L119, but the destructive upgrade chain retypes it to money and settles it
// terminally at 03.01.01.SqlDataProvider L1173; TrialFee was money from birth at
// 01.00.08.SqlDataProvider L6830. Only the terminal state is meaningful, and no legacy validator
// bounded either fee above.
//
// MIGRATION: the MaxLength="50" attributes on the four fee and period text boxes (editroles.ascx
// L89, L104, L122, L136) are deliberately not carried across, because they bounded how many
// characters could be typed into a text box and the migrated members are a decimal and an integer, on
// which a character count is meaningless. The four DataTypeCheck comparisons beside them are absent
// for the same class of reason: a malformed number now fails deserialisation at the API edge, reported
// against the same member, so reproducing the check would assert what the type system already
// guarantees. The three genuine text members DO carry their column widths.
public class UpdateRoleRequestValidator : AbstractValidator<UpdateRoleRequest>
{
    /// <summary>
    /// Largest billing or trial period a role may declare, in units of its own frequency.
    /// </summary>
    /// <remarks>
    /// Net-new, and generous by four orders of magnitude against any real configuration: ten thousand units
    /// is ten thousand years at the yearly frequency and twenty-seven years at the daily one. The service's
    /// own date arithmetic clamps rather than overflowing, so this rule is not what keeps that safe - what it
    /// adds is a field-level answer naming the period, in place of a silently clamped expiry a caller never
    /// asked for. The create validator states the same figure and the two must agree.
    /// </remarks>
    private const int PeriodMaximum = 10_000;

    /// <summary>
    /// Reported when a billing or trial period exceeds the largest count a role may declare.
    /// </summary>
    private static readonly string PeriodTooLargeMessage = FormattableString.Invariant(
        $"A billing or trial period must be no more than {PeriodMaximum} units of its own frequency.");

    /// <summary>
    /// Reported when a submitted amount exceeds what the terminal <c>money</c> column can hold.
    /// </summary>
    /// <remarks>
    /// REPRESENTABILITY, NOT A PRICE CEILING. The terminal column is <c>money</c>, not the baseline's
    /// <c>decimal(5, 2)</c>, and the rule states that column's own limits. The legacy screen declared no
    /// business maximum and inventing one would refuse a fee some installation legitimately charges; what is
    /// refused is an amount the column cannot hold at all, which unbounded reached the provider and surfaced
    /// as a server fault naming no field.
    /// </remarks>
    private static readonly string AmountUnrepresentableMessage = FormattableString.Invariant($"An amount must fall between {SqlServerRange.MinimumMoney} and {SqlServerRange.MaximumMoney}, ")
        + FormattableString.Invariant($"which is the range the stored column can hold.");

    /// <summary>
    /// Initialises a new instance of the <see cref="UpdateRoleRequestValidator"/> class and declares
    /// its rules.
    /// </summary>
    /// <remarks>
    /// Rule-level cascade stops at the first failure for a member, so a caller reads one actionable
    /// message per field rather than a length complaint stacked on a range complaint. Class-level
    /// cascade continues, so one malformed submission reports every bad field in a single response
    /// instead of forcing a caller to discover them one round trip at a time. Both settings match the
    /// creation validator exactly.
    /// </remarks>
    public UpdateRoleRequestValidator()
    {
        RuleLevelCascadeMode = CascadeMode.Stop;
        ClassLevelCascadeMode = CascadeMode.Continue;

        // No validator was declared on the description, so only the column width is asserted. The
        // rule is inert for an absent value: a length check passes a null.
        RuleFor(request => request.Description)
            .MaximumLength(RoleTermsRules.DescriptionMaximumLength);

        // No validator was declared on the invitation code either, and nothing in the schema makes
        // it unique, so a clash is not a conflict and only the width applies.
        RuleFor(request => request.RsvpCode)
            .MaximumLength(RoleTermsRules.RsvpCodeMaximumLength);

        // The column width plus the containment rule. THIS is the rule the update path was missing:
        // the creation validator declared it and this one did not, so the same column accepted a
        // rooted or traversing path through PUT that POST refused. Both now call one definition.
        RuleFor(request => request.IconFile)
            .MaximumLength(RoleTermsRules.IconFileMaximumLength)
            .Must(RoleTermsRules.IsContainedRelativePath)
            .WithMessage(RoleTermsRules.IconFileNotRelativeMessage);

        // valServiceFee2 (editroles.ascx L96): GreaterThanEqual against 0. Zero is a real price - a
        // free role - so the bound admits it and refuses only a negative amount.
        RuleFor(request => request.ServiceFee)
            .GreaterThanOrEqualTo(0m)
            .WithMessage(RoleTermsRules.ServiceFeeNegativeMessage)
            .Must(SqlServerRange.CanStore)
            .WithMessage(AmountUnrepresentableMessage)
            .When(request => request.ServiceFee.HasValue);

        // valBillingPeriod2 (L114): GreaterThan against 0 - strictly positive - despite the message
        // wording, which is preserved as the legacy screen wrote it. A cycle of zero units could
        // never advance an expiry date.
        RuleFor(request => request.BillingPeriod)
            .GreaterThan(0)
            .WithMessage(RoleTermsRules.BillingPeriodNotPositiveMessage)
            .LessThanOrEqualTo(PeriodMaximum)
            .WithMessage(PeriodTooLargeMessage)
            .When(request => request.BillingPeriod.HasValue);

        // valTrialFee2 (L128): GreaterThanEqual against 0 - zero admitted - despite the message
        // wording, likewise preserved. A free trial is a real configuration.
        RuleFor(request => request.TrialFee)
            .GreaterThanOrEqualTo(0m)
            .WithMessage(RoleTermsRules.TrialFeeNegativeMessage)
            .Must(SqlServerRange.CanStore)
            .WithMessage(AmountUnrepresentableMessage)
            .When(request => request.TrialFee.HasValue);

        // valTrialPeriod2 (L146): GreaterThan against 0. Message and operator agree here.
        RuleFor(request => request.TrialPeriod)
            .GreaterThan(0)
            .WithMessage(RoleTermsRules.TrialPeriodNotPositiveMessage)
            .LessThanOrEqualTo(PeriodMaximum)
            .WithMessage(PeriodTooLargeMessage)
            .When(request => request.TrialPeriod.HasValue);

        // Membership of the domain enumeration, which IS the character check because each member
        // carries the code point of its stored character. The presence guard is stated explicitly so
        // the rule reads as conditional rather than relying on the enumeration check to tolerate a
        // null.
        RuleFor(request => request.BillingFrequency)
            .IsInEnum()
            .WithMessage(RoleTermsRules.BillingFrequencyInvalidMessage)
            .When(request => request.BillingFrequency.HasValue);

        RuleFor(request => request.TrialFrequency)
            .IsInEnum()
            .WithMessage(RoleTermsRules.TrialFrequencyInvalidMessage)
            .When(request => request.TrialFrequency.HasValue);

        // No rule on IsPublic or AutoAssignment: a boolean is its own constraint, and false is a
        // legitimate state that agrees with the columns' own zero defaults.
        //
        // No rule on RoleGroupId: zero names a real group, because dbo.RoleGroups.RoleGroupID is
        // seeded IDENTITY(0,1), and the legacy negative value means "ungrouped", so any bound would
        // refuse one of the two.
    }
}
