using DnnMigration.Application.Dtos.Role;
using DnnMigration.Domain.Common;
using FluentValidation;

namespace DnnMigration.Application.Validation;

/// <summary>
/// Declares the field rules for <see cref="CreateRoleRequest"/>, the payload submitted to <c>POST
/// /api/v1/roles</c> to create a security role together with its paid-membership terms.
/// </summary>
/// <remarks>
/// Every rule below is a measured reproduction of a declarative validator on
/// <c>Website/admin/Security/editroles.ascx</c>, which declares exactly nine of them: one
/// <c>RequiredFieldValidator</c> on the name, and eight comparisons across the two fees and the two
/// periods.
/// </remarks>
public class CreateRoleRequestValidator : AbstractValidator<CreateRoleRequest>
{
    // MIGRATION: THERE IS NO UPPER BOUND ON EITHER PERIOD, and its removal was a correction rather than a
    // relaxation. A net-new ten-thousand-unit ceiling stood here and on the update validator, justified as
    // generous; generosity is not the test.

    /// <summary>
    /// Reported when a fee is a well-formed decimal that the terminal <c>money</c> column cannot hold.
    /// </summary>
    private static readonly string AmountUnrepresentableMessage = FormattableString.Invariant($"An amount must fall between {SqlServerRange.MinimumMoney} and {SqlServerRange.MaximumMoney}, ")
        + FormattableString.Invariant($"which is the range the stored column can hold.");

    /// <summary>
    /// Initialises a new instance of the <see cref="CreateRoleRequestValidator"/> class and declares its
    /// rules.
    /// </summary>
    /// <remarks>
    /// Rule-level cascade stops at the first failure for a member, so a caller reads one actionable message
    /// per field rather than a length complaint stacked on a presence complaint. Class-level cascade
    /// continues, so one malformed submission reports every bad field in a single response instead of
    /// forcing a caller to discover them one round trip at a time.
    /// </remarks>
    public CreateRoleRequestValidator()
    {
        RuleLevelCascadeMode = CascadeMode.Stop;
        ClassLevelCascadeMode = CascadeMode.Continue;

        // valRoleName: the screen's ONE presence check, and the only rule in this validator that is
        // unconditional. The width is the NOT NULL column's own.
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

        // SEC: THE STRENGTH RULE IS NET-NEW, AND IT APPLIES TO AUTHORING ONLY. An invitation code is a
        // shared secret that grants role membership - including a private, paid or permission-bearing role,
        // because a code IS the bypass for a service that is not published - and it was bounded only above,
        // so a one-character code was accepted and thereafter redeemable by anybody who submitted that
        // character.
        RuleFor(request => request.RsvpCode)
            .MaximumLength(RoleTermsRules.RsvpCodeMaximumLength)
            .Must(TextIntegrityRules.IsSingleLineSafe)
            .WithMessage(TextIntegrityRules.SingleLineMessage)
            .Must(RoleTermsRules.IsStrongAuthoredRsvpCode)
            .WithMessage(RoleTermsRules.RsvpCodeTooWeakMessage);

        // Column width, plus the net-new containment rule annotated above.
        RuleFor(request => request.IconFile)
            .MaximumLength(RoleTermsRules.IconFileMaximumLength)
            .Must(RoleTermsRules.IsContainedRelativePath)
            .WithMessage(RoleTermsRules.IconFileNotRelativeMessage);

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

        // valTrialFee2 (L128): GreaterThanEqual against 0 - zero admitted - despite the message
        // wording preserved above. A free trial is a real configuration.
        RuleFor(request => request.TrialFee)
            .GreaterThanOrEqualTo(0m)
            .WithMessage(RoleTermsRules.TrialFeeNegativeMessage)
            .Must(SqlServerRange.CanStore)
            .WithMessage(AmountUnrepresentableMessage)
            .When(request => request.TrialFee.HasValue);

        // valTrialPeriod2 (L146): GreaterThan against 0. Message and operator agree here, and the pair is
        // evaluated for the same reason as the billing period above.
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

        // No rule on the two flags: a boolean is its own constraint, and false is a legitimate state that
        // agrees with the columns' own zero defaults.
    }
}
