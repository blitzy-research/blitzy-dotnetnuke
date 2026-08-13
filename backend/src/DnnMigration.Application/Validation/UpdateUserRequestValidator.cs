using DnnMigration.Application.Dtos.User;
using DnnMigration.Domain.ValueObjects;
using FluentValidation;

namespace DnnMigration.Application.Validation;

/// <summary>
/// Declarative validator for <see cref="UpdateUserRequest"/>, the inbound contract of <c>PUT
/// /api/v1/users/{userId}</c>.
/// </summary>
/// <remarks>
/// <b>Identifiers carry no bound test.</b> The addressed user arrives on the route and the tenant arrives
/// from the resolved portal context, so no identifier appears on this request at all — and even if one did,
/// a bound test would be wrong. The identity seeds in this schema are deliberately low or negative, so a
/// zero or negative identifier is a legitimate row key rather than an absent value.
/// </remarks>
public class UpdateUserRequestValidator : AbstractValidator<UpdateUserRequest>
{
    /// <summary>Verbatim wording of <c>UserInfo_FirstName.Required</c>, <c>User.ascx.resx:L157</c>.</summary>
    private const string FirstNameRequiredMessage = "First name is required";

    /// <summary>Verbatim wording of <c>UserInfo_LastName.Required</c>, <c>User.ascx.resx:L160</c>.</summary>
    private const string LastNameRequiredMessage = "Last name is required";

    /// <summary>Verbatim wording of <c>UserInfo_DisplayName.Required</c>, <c>User.ascx.resx:L223</c>.</summary>
    private const string DisplayNameRequiredMessage = "Display Name is required";

    /// <summary>Verbatim wording of <c>UserInfo_Email.Required</c>, <c>User.ascx.resx:L163</c>.</summary>
    private const string EmailRequiredMessage = "Email is required";

    // The email format wording is the shared, application-wide message InvalidEmail.Text at
    // Website/App_GlobalResources/SharedResources.resx:L282, reproduced character for character including
    // its two spaces after the sentence period.
    private const string InvalidEmailMessage =
        "The email address specified is invalid.  Please specify a valid email address.";

    private const int EmailMaximumLength = 256;

    // MIGRATION: THIS FILE DECLARES NO EMAIL PATTERN OF ITS OWN, DELIBERATELY. The grammar lives once,
    // in Domain/ValueObjects/EmailAddress, and is reached below through EmailAddress.TryCreate. A local
    // copy of glbEmailRegEx from Library/Components/Shared/Globals.vb:L132 here, beside a second copy in
    // Validation/CreateUserRequestValidator.cs, would be two definitions of one rule with nothing keeping
    // them in step.

    /// <summary>
    /// Initialises a new instance of the <see cref="UpdateUserRequestValidator"/> class and declares its
    /// complete rule set.
    /// </summary>
    /// <remarks>
    /// The constructor is parameterless by design. This validator inspects nothing beyond the request it is
    /// handed, so it needs no configuration, no clock, no caller identity and no repository, and taking any
    /// of those would let a rule depend on state the caller cannot see.
    /// </remarks>
    public UpdateUserRequestValidator()
    {
        RuleFor(request => request.FirstName)
            .Cascade(CascadeMode.Stop)
            .NotEmpty().WithMessage(FirstNameRequiredMessage)
            .MaximumLength(50);

        RuleFor(request => request.LastName)
            .Cascade(CascadeMode.Stop)
            .NotEmpty().WithMessage(LastNameRequiredMessage)
            .MaximumLength(50);

        RuleFor(request => request.DisplayName)
            .Cascade(CascadeMode.Stop)
            .NotEmpty().WithMessage(DisplayNameRequiredMessage)
            .MaximumLength(128);

        // MIGRATION: requiredness here is an API-LEVEL rule and is NOT a restatement of the column's
        // nullability. The column permits null; the legacy screen did not, and the screen's rule is what an
        // update request must satisfy.
        RuleFor(request => request.Email)
            .Cascade(CascadeMode.Stop)
            .NotEmpty().WithMessage(EmailRequiredMessage)
            .MaximumLength(EmailMaximumLength).WithMessage(InvalidEmailMessage)
            .Must(candidate => EmailAddress.TryCreate(candidate, out _))
                .WithMessage(InvalidEmailMessage);
    }
}
