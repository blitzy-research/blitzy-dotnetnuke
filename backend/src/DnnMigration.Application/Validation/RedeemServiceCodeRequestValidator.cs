using DnnMigration.Application.Dtos.User;
using FluentValidation;

namespace DnnMigration.Application.Validation;

/// <summary>
/// Declares the field rules for <see cref="RedeemServiceCodeRequest"/>, the invitation code submitted to
/// <c>POST /api/v1/users/{userId}/services/redemptions</c>.
/// </summary>
/// <remarks>
/// <para>
/// FluentValidation's emptiness rule treats a whitespace-only string as empty, which is stricter than the
/// legacy inequality and deliberately so: whitespace could never match a real code, and the alternative -
/// reading every role in the tenant in order to answer "no match" - spends a query to reach a foregone
/// conclusion.
/// </para>
/// <para>
/// The length bound is the stored column's own width, <c>Roles.RSVPCode nvarchar(50) NULL</c>, taken from
/// the shared role-terms constants rather than spelled again here. A longer value cannot match any stored
/// code, so naming the offending field is strictly more useful than a successful "no match".
/// </para>
/// </remarks>
public class RedeemServiceCodeRequestValidator : AbstractValidator<RedeemServiceCodeRequest>
{
    /// <summary>Reported when the submission carries no code.</summary>
    internal const string CodeRequiredMessage = "An RSVP Code is required.";

    /// <summary>
    /// Initialises a new instance of the <see cref="RedeemServiceCodeRequestValidator"/> class and declares
    /// its rules.
    /// </summary>
    public RedeemServiceCodeRequestValidator()
    {
        RuleFor(request => request.Code)
            .NotEmpty()
            .WithMessage(CodeRequiredMessage)
            .MaximumLength(RoleTermsRules.RsvpCodeMaximumLength);
    }
}
