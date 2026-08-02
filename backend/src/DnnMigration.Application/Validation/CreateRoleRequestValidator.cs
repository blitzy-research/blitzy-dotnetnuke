using DnnMigration.Application.Dtos.Role;
using FluentValidation;

namespace DnnMigration.Application.Validation;

// MIGRATION: every rule below is measured from Website/admin/Security/editroles.ascx and reproduced
// rather than reinterpreted. The screen declares exactly nine validators:
//
//   valRoleName        RequiredFieldValidator on txtRoleName
//   valServiceFee1     CompareValidator, Type="Currency", Operator="DataTypeCheck"
//   valServiceFee2     CompareValidator, Operator="GreaterThanEqual", ValueToCompare="0"
//   valBillingPeriod1  CompareValidator, Type="Integer",  Operator="DataTypeCheck"
//   valBillingPeriod2  CompareValidator, Operator="GreaterThan",      ValueToCompare="0"
//   valTrialFee1       CompareValidator, Type="Currency", Operator="DataTypeCheck"
//   valTrialFee2       CompareValidator, Operator="GreaterThanEqual", ValueToCompare="0"
//   valTrialPeriod1    CompareValidator, Type="Integer",  Operator="DataTypeCheck"
//   valTrialPeriod2    CompareValidator, Operator="GreaterThan",      ValueToCompare="0"
//
// MIGRATION: the fee-versus-period asymmetry is genuine and is preserved exactly. A fee may be zero -
// GreaterThanEqual against 0 - while a period may not - GreaterThan against 0. A free role with a
// zero service fee is therefore valid, and a billing period of zero is not, because a cycle of zero
// units would never advance an expiry date. Normalising the two rules to one would silently change
// behaviour in both directions, so they are kept distinct even though the error wording differs only
// in one word. Note also that the legacy trial-fee error message reads "Trial Fee Must Be Greater Than
// Zero" while its operator is GreaterThanEqual: the message and the rule disagree in the legacy source
// itself. The rule is authoritative and is preserved; the wording is corrected here so the message
// cannot mislead a caller into thinking a zero trial fee is rejected.
//
// MIGRATION: an ASP.NET CompareValidator does not fire against an empty control, so every one of those
// eight comparisons was conditional on the field having been filled in. That is why each numeric member
// on the request contract is nullable and why each rule below carries a When clause: defaulting to zero
// would turn "not supplied" into "supplied as zero", which the two period rules would then reject for a
// role that has no billing at all.
//
// MIGRATION: the DataTypeCheck comparisons need no counterpart. A decimal? cannot carry a
// non-currency value and an int? cannot carry a non-integer, so those four rules are structurally
// impossible to violate once the members are typed and the wire format is JSON.
//
// MIGRATION: the maximum lengths are asserted here because the legacy screen relied on the textbox
// MaxLength attribute, a client-side-only limit that disappears with the postback. The widths are the
// measured terminal columns on dbo.Roles: RoleName nvarchar(50) NOT NULL, Description nvarchar(1000),
// RSVPCode nvarchar(50), IconFile nvarchar(100).
//
// MIGRATION: no uniqueness rule appears here. Roles.RoleName is unique per portal through the
// IX_Roles unique index on (PortalID, RoleName), which a stateless validator cannot check;
// Application/Services/RoleService.cs performs the repository read and reports the collision.
//
// MIGRATION: the frequency members are validated for enumeration membership rather than against the
// literal character codes. Roles.BillingFrequency and Roles.TrialFrequency are char(1) columns whose
// D, W, M and Y values RoleController.vb:L543-L546 switched on, and the domain enumeration carries
// those exact persisted values - so membership of the enumeration IS the character check, and no
// separate string rule is needed.

/// <summary>
/// Validates the shape of a role creation submitted to
/// <c>POST /api/v1/portals/{portalId}/roles</c>.
/// </summary>
/// <remarks>
/// Rule-level cascade stops at the first failure for a member so a caller sees one actionable message
/// per field; class-level cascade continues so every field is reported in one response. Stateful rules -
/// name uniqueness within the portal, whether a named role group exists - belong to
/// <c>Application/Services/RoleService.cs</c>.
/// </remarks>
public class CreateRoleRequestValidator : AbstractValidator<CreateRoleRequest>
{
    private const string RoleNameRequiredMessage = "Role Name Is Required.";

    private const string ServiceFeeNegativeMessage =
        "Service Fee Must Be Greater Than or Equal to Zero";

    private const string BillingPeriodNotPositiveMessage =
        "Billing Period Must Be Greater Than Zero";

    private const string TrialFeeNegativeMessage = "Trial Fee Must Be Greater Than or Equal to Zero";

    private const string TrialPeriodNotPositiveMessage = "Trial Period Must Be Greater Than Zero";

    private const string BillingFrequencyInvalidMessage =
        "Billing Frequency must be one of None, One Time, Day, Week, Month or Year.";

    private const string TrialFrequencyInvalidMessage =
        "Trial Frequency must be one of None, One Time, Day, Week, Month or Year.";

    // Measured terminal column widths on dbo.Roles.
    private const int RoleNameMaximumLength = 50;
    private const int DescriptionMaximumLength = 1000;
    private const int RsvpCodeMaximumLength = 50;
    private const int IconFileMaximumLength = 100;

    /// <summary>
    /// Declares the rule set.
    /// </summary>
    public CreateRoleRequestValidator()
    {
        RuleLevelCascadeMode = CascadeMode.Stop;
        ClassLevelCascadeMode = CascadeMode.Continue;

        RuleFor(request => request.RoleName)
            .NotEmpty().WithMessage(RoleNameRequiredMessage)
            .MaximumLength(RoleNameMaximumLength);

        RuleFor(request => request.Description)
            .MaximumLength(DescriptionMaximumLength);

        RuleFor(request => request.RsvpCode)
            .MaximumLength(RsvpCodeMaximumLength);

        RuleFor(request => request.IconFile)
            .MaximumLength(IconFileMaximumLength);

        // valServiceFee2: GreaterThanEqual against 0. A free role is legitimate.
        RuleFor(request => request.ServiceFee)
            .GreaterThanOrEqualTo(0m).WithMessage(ServiceFeeNegativeMessage)
            .When(request => request.ServiceFee.HasValue);

        // valBillingPeriod2: GreaterThan against 0. A cycle of zero units cannot advance an expiry.
        RuleFor(request => request.BillingPeriod)
            .GreaterThan(0).WithMessage(BillingPeriodNotPositiveMessage)
            .When(request => request.BillingPeriod.HasValue);

        // valTrialFee2: GreaterThanEqual against 0, despite the legacy message wording.
        RuleFor(request => request.TrialFee)
            .GreaterThanOrEqualTo(0m).WithMessage(TrialFeeNegativeMessage)
            .When(request => request.TrialFee.HasValue);

        // valTrialPeriod2: GreaterThan against 0.
        RuleFor(request => request.TrialPeriod)
            .GreaterThan(0).WithMessage(TrialPeriodNotPositiveMessage)
            .When(request => request.TrialPeriod.HasValue);

        RuleFor(request => request.BillingFrequency)
            .IsInEnum().WithMessage(BillingFrequencyInvalidMessage)
            .When(request => request.BillingFrequency.HasValue);

        RuleFor(request => request.TrialFrequency)
            .IsInEnum().WithMessage(TrialFrequencyInvalidMessage)
            .When(request => request.TrialFrequency.HasValue);
    }
}
