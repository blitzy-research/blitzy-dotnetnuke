using DnnMigration.Application.Dtos.Role;
using DnnMigration.Domain.Common;
using FluentValidation;

namespace DnnMigration.Application.Validation;

/// <summary>Field rules for <see cref="UpdateRoleRequest"/>, the body of <c>PUT /api/v1/roles/{roleId}</c>.</summary>
/// <remarks>
/// <b>The rules are literally the creation path's rules.</b> Both validators consume <see
/// cref="RoleTermsRules"/>, so the wording, the widths, the operators and the icon-path predicate cannot
/// differ between the two verbs.
/// </remarks>
// Exactly ONE presence rule, on the name, and the count is measured rather than assumed. The screen
// declared exactly one required-field validator, valRoleName at editroles.ascx L31.
public class UpdateRoleRequestValidator : AbstractValidator<UpdateRoleRequest>
{
    // MIGRATION: THERE IS NO UPPER BOUND ON EITHER PERIOD, and its removal was a correction rather than a
    // relaxation. A net-new ten-thousand-unit ceiling stood here and on the creation validator, justified
    // as generous; generosity is not the test.

    /// <summary>Reported when a submitted amount exceeds what the terminal <c>money</c> column can hold.</summary>
    private static readonly string AmountUnrepresentableMessage = FormattableString.Invariant($"An amount must fall between {SqlServerRange.MinimumMoney} and {SqlServerRange.MaximumMoney}, ")
        + FormattableString.Invariant($"which is the range the stored column can hold.");

    /// <summary>
    /// Initialises a new instance of the <see cref="UpdateRoleRequestValidator"/> class and declares its
    /// rules.
    /// </summary>
    /// <remarks>
    /// Rule-level cascade stops at the first failure for a member, so a caller reads one actionable message
    /// per field rather than a length complaint stacked on a range complaint. Class-level cascade
    /// continues, so one malformed submission reports every bad field in a single response instead of
    /// forcing a caller to discover them one round trip at a time.
    /// </remarks>
    public UpdateRoleRequestValidator()
    {
        RuleLevelCascadeMode = CascadeMode.Stop;
        ClassLevelCascadeMode = CascadeMode.Continue;

        // valRoleName: the screen's ONE presence check, and the only unconditional rule here. The width is
        // the NOT NULL column's own.
        RuleFor(request => request.RoleName)
            .NotEmpty()
            .WithMessage(RoleTermsRules.RoleNameRequiredMessage)
            .MaximumLength(RoleTermsRules.RoleNameMaximumLength)
            .Must(TextIntegrityRules.IsSingleLineSafe)
            .WithMessage(TextIntegrityRules.SingleLineMessage);

        // No validator was declared on the description, so only the column width is asserted. The
        // rule is inert for an absent value: a length check passes a null.
        RuleFor(request => request.Description)
            .MaximumLength(RoleTermsRules.DescriptionMaximumLength)
            .Must(TextIntegrityRules.IsMultiLineSafe)
            .WithMessage(TextIntegrityRules.MultiLineMessage);

        // No validator was declared on the invitation code either, and nothing in the schema makes it
        // unique, so a clash is not a conflict and the column width is the only ported bound.
        RuleFor(request => request.RsvpCode)
            .MaximumLength(RoleTermsRules.RsvpCodeMaximumLength)
            .Must(TextIntegrityRules.IsSingleLineSafe)
            .WithMessage(TextIntegrityRules.SingleLineMessage)
            .Must(RoleTermsRules.IsStrongAuthoredRsvpCode)
            .WithMessage(RoleTermsRules.RsvpCodeTooWeakMessage);

        // The column width plus the containment rule. THIS is the rule the update path was missing: the
        // creation validator declared it and this one did not, so the same column accepted a rooted or
        // traversing path through PUT that POST refused.
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

        RuleFor(request => request.BillingPeriod)
            .Must((request, period) =>
                RoleTermsRules.IsPeriodAdmissibleForFrequency(period, request.BillingFrequency))
            .WithMessage(RoleTermsRules.BillingPeriodNotPositiveMessage)
            .When(request => request.BillingPeriod.HasValue);

        // valTrialFee2 (L128): GreaterThanEqual against 0 - zero admitted, and the legacy message says
        // "or Equal to Zero", so the two agree here too. A free trial is a real configuration.
        RuleFor(request => request.TrialFee)
            .GreaterThanOrEqualTo(0m)
            .WithMessage(RoleTermsRules.TrialFeeNegativeMessage)
            .Must(SqlServerRange.CanStore)
            .WithMessage(AmountUnrepresentableMessage)
            .When(request => request.TrialFee.HasValue);

        RuleFor(request => request.TrialPeriod)
            .Must((request, period) =>
                RoleTermsRules.IsPeriodAdmissibleForFrequency(period, request.TrialFrequency))
            .WithMessage(RoleTermsRules.TrialPeriodNotPositiveMessage)
            .When(request => request.TrialPeriod.HasValue);

        RuleFor(request => request.BillingFrequency)
            .IsInEnum()
            .WithMessage(RoleTermsRules.BillingFrequencyInvalidMessage)
            .When(request => request.BillingFrequency.HasValue);

        RuleFor(request => request.TrialFrequency)
            .IsInEnum()
            .WithMessage(RoleTermsRules.TrialFrequencyInvalidMessage)
            .When(request => request.TrialFrequency.HasValue);

        // No rule on IsPublic or AutoAssignment: a boolean is its own constraint, and false is a legitimate
        // state that agrees with the columns' own zero defaults.
    }
}
