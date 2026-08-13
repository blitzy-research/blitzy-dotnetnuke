using DnnMigration.Application.Dtos.Auth;
using FluentValidation;

namespace DnnMigration.Application.Validation;

/// <summary>
/// Declares the field rules for <see cref="LoginRequest"/>, the credential payload submitted to <c>POST
/// /api/v1/auth/login</c>.
/// </summary>
/// <remarks>
/// <para>
/// Two legacy inputs are deliberately absent from the contract this validator guards, and no rule here
/// mentions either: the image-based human-verification challenge that <c>Login.ascx</c> declared at L22 and
/// that <c>Login.ascx.vb</c> gated the entire sign-in on at L162, and the fixed authentication-mechanism
/// literal that <c>Login.ascx.vb</c> supplied at L164 and L191.
/// </para>
/// <para>
/// Messages are structural only. A message may state that a field is required; none may suggest whether an
/// account exists, because resisting account enumeration is the reason the sign-in service answers every
/// rejected credential the same way.
/// </para>
/// </remarks>
public class LoginRequestValidator : AbstractValidator<LoginRequest>
{
    /// <summary>
    /// Upper bound applied to <see cref="LoginRequest.Username"/>, taken from the terminal width of
    /// <c>dbo.Users.Username</c>, which is <c>nvarchar(100) NOT NULL</c>.
    /// </summary>
    /// <remarks>
    /// The figure is the width of the DotNetNuke user table specifically, not of the externally installed
    /// ASP.NET membership table, which is wider. The two are separate stores and only the narrower one
    /// bounds what this application can hold.
    /// </remarks>
    private const int UsernameMaximumLength = 100;

    /// <summary>
    /// Initialises a new instance of the <see cref="LoginRequestValidator"/> class and declares its rules.
    /// </summary>
    public LoginRequestValidator()
    {
        // MIGRATION: The ceiling is NET-NEW -- the markup sets no length limit on the sign-in box at L10 --
        // and its value is the TERMINAL width of dbo.Users.Username, which is nvarchar(100) NOT NULL.
        RuleFor(request => request.Username)
            .NotEmpty()
            .WithMessage("A username is required.")
            .MaximumLength(UsernameMaximumLength)
            .WithMessage("A username cannot be longer than {MaxLength} characters.");

        // MIGRATION: There IS an upper bound on the password, it is NET-NEW, and it is a deliberate
        // tightening.
        RuleFor(request => request.Password)
            .Cascade(CascadeMode.Stop)
            .NotEmpty()
            .WithMessage("A password is required.")
            .Must(CredentialBounds.IsWithinMaximumByteLength)
            .WithMessage(CredentialBounds.MaximumByteLengthMessage);

        RuleFor(request => request.VerificationCode)
            .NotEmpty()
            .WithMessage("A verification code, when supplied, must contain at least one non-whitespace character.")
            .When(request => !string.IsNullOrEmpty(request.VerificationCode));

        // Deliberate divergences that carry no rule. Recorded here under Rule T5 so that each absence reads
        // as a documented decision rather than as an omission, and cited by file and line wherever naming
        // the thing would defeat the point of having left it behind.

        // MIGRATION: The fixed authentication-mechanism literal is dropped, so neither a rule nor a member
        // carries it.

        // MIGRATION: The message selection at Login.ascx.vb:L175-L184 "EnterCode", "InvalidCode" and
        // "UserNotAuthorized" -- is NOT reproduced here.
    }
}
