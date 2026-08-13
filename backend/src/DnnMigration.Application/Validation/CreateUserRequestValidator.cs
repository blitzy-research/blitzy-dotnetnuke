using System.Globalization;
using System.Text.RegularExpressions;
using DnnMigration.Application.Dtos.User;
using DnnMigration.Application.Options;
using DnnMigration.Domain.ValueObjects;
using FluentValidation;

namespace DnnMigration.Application.Validation;

/// <summary>
/// Validates <see cref="CreateUserRequest"/>, the inbound contract of <c>POST /api/v1/users</c>,
/// reproducing the legacy DotNetNuke user-creation rules exactly and adding nothing to them.
/// </summary>
/// <remarks>
/// <para>
/// Consequently this validator applies <b>no character-class requirement of any kind</b> to the password,
/// and no composition, repetition or dictionary rule. Each of those would be a tightening, and a tightening
/// during a migration denies access to users who are already registered.
/// </para>
/// <para>
/// <b>What this class deliberately does not do.</b> It performs no persistence lookup, so it enforces
/// neither login-name nor email distinctness; those are outcomes the service reports after attempting to
/// write, and they surface as an RFC 7807 document shaped by the transport layer.
/// </para>
/// </remarks>
public class CreateUserRequestValidator : AbstractValidator<CreateUserRequest>
{
    // Message wording is reproduced verbatim from the legacy resource files, and the inter-sentence spacing
    // in those files is INCONSISTENT. The global entries reproduced below use two spaces after a sentence
    // period.

    /// <summary>Legacy wording for an invalid login name, from <c>SharedResources.resx</c> L291.</summary>
    private const string InvalidUserNameMessage =
        "The username specified is invalid.  Please specify a valid username.";

    /// <summary>Legacy wording for an invalid email address, from <c>SharedResources.resx</c> L282.</summary>
    private const string InvalidEmailMessage =
        "The email address specified is invalid.  Please specify a valid email address.";

    /// <summary>
    /// Legacy wording for an invalid password, from <c>SharedResources.resx</c> L285. The two bracketed
    /// tokens are substituted from the bound policy at construction time.
    /// </summary>
    private const string InvalidPasswordMessageTemplate =
        "The password specified is invalid.  Please specify a valid password.  Passwords must be at least [PasswordLength] characters in length and contain at least [NoneAlphabet] non-alphanumeric characters.";

    /// <summary>
    /// Legacy wording for a password/confirmation mismatch, from <c>SharedResources.resx</c> L852. It
    /// intentionally carries no trailing period.
    /// </summary>
    private const string PasswordMismatchMessage =
        "The Password and Confirmation Passwords do not match";

    // The ceiling on a submitted credential is NOT declared in this file.

    /// <summary>Screen wording for a missing login name, from <c>User.ascx.resx</c> L154.</summary>
    private const string UsernameRequiredMessage = "User name is required";

    /// <summary>Screen wording for a missing given name, from <c>User.ascx.resx</c> L157.</summary>
    private const string FirstNameRequiredMessage = "First name is required";

    /// <summary>Screen wording for a missing family name, from <c>User.ascx.resx</c> L160.</summary>
    private const string LastNameRequiredMessage = "Last name is required";

    /// <summary>Screen wording for a missing email address, from <c>User.ascx.resx</c> L163.</summary>
    private const string EmailRequiredMessage = "Email is required";

    /// <summary>Screen wording for a malformed email address, from <c>User.ascx.resx</c> L220.</summary>
    private const string EmailFormatMessage = "You must enter a valid email address";

    // MIGRATION: the wording below is authored here. The legacy creation screen enforced
    // the display-name ceiling through a markup attribute on a property-editor field,
    // which silently truncated typing and produced no message at all, so there is no
    // legacy string to reproduce. The sentence below follows the measured pattern of the
    // global entries above so that it reads as part of the same family.

    /// <summary>Authored wording for an over-long display name; no legacy equivalent exists.</summary>
    private const string InvalidDisplayNameMessage =
        "The display name specified is invalid.  Please specify a valid display name.";

    // MIGRATION: net-new wording, and necessarily so - the legacy application had no ceiling of this kind
    // to report, because it stored credentials in a reversible format rather than hashing them.

    /// <summary>
    /// Net-new wording for a credential that cannot be hashed without loss; no legacy equivalent exists.
    /// </summary>
    private const string PasswordTooLongMessage =
        "The password specified is too long.  Please specify a password of no more than 72 bytes.";

    /// <summary>The token the legacy code replaced with the configured minimum password length.</summary>
    private const string PasswordLengthToken = "[PasswordLength]";

    /// <summary>
    /// The token the legacy code replaced with the configured minimum count of non-alphanumeric characters.
    /// </summary>
    private const string NoneAlphabetToken = "[NoneAlphabet]";

    /// <summary>Terminal column width of the login name: <c>nvarchar(100)</c>.</summary>
    private const int UsernameMaximumLength = 100;

    /// <summary>
    /// Terminal column width of the given and family names: <c>nvarchar(50)</c>. This is the schema width,
    /// deliberately not the wider legacy markup allowance.
    /// </summary>
    private const int PersonNameMaximumLength = 50;

    /// <summary>Terminal column width of the display name: <c>nvarchar(128)</c>.</summary>
    private const int DisplayNameMaximumLength = 128;

    /// <summary>Terminal column width of the email address: <c>nvarchar(256)</c>.</summary>
    /// <remarks>
    /// The terminal width is not the baseline width. <c>dbo.Users.Email</c> is created at <c>nvarchar(100)
    /// NOT NULL</c> (<c>01.00.00.SqlDataProvider:L107</c>), DROPPED outright
    /// (<c>02.02.01.SqlDataProvider:L50-51</c>) when contact details moved into the ASP.NET membership
    /// tables, and re-added as <c>nvarchar(256) NULL</c> (<c>03.00.13.SqlDataProvider:L109-110</c>), after
    /// which nothing narrows it.
    /// </remarks>
    private const int EmailMaximumLength = 256;

    /// <summary>
    /// Upper bound on the time any single pattern evaluation may consume, so that a pathological input
    /// cannot hold a request thread.
    /// </summary>
    private static readonly TimeSpan PatternMatchTimeout = TimeSpan.FromMilliseconds(250);

    // Delegating also CORRECTS TWO DEFECTS that the local copy carried: - It matched a SUBSTRING.
    // FluentValidation's pattern rule tests whether the pattern occurs anywhere in the value, and the
    // legacy pattern is word-boundary delimited rather than anchored, so "a@b.co and some junk" satisfied
    // it.

    /// <summary>
    /// Builds the rule set, binding every password threshold and every substituted message token from the
    /// supplied policy rather than restating any of them.
    /// </summary>
    /// <param name="passwordPolicy">
    /// The password policy in force, preserved verbatim from the legacy membership provider registration
    /// and bound by the Api layer from the configuration section named by <see
    /// cref="PasswordPolicyOptions.SectionName"/>.
    /// </param>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="passwordPolicy"/> is <see langword="null"/>.
    /// </exception>
    public CreateUserRequestValidator(PasswordPolicyOptions passwordPolicy)
    {
        ArgumentNullException.ThrowIfNull(passwordPolicy);

        PasswordPolicyOptions policy = passwordPolicy;

        // MIGRATION: the two bracketed tokens are substituted from the BOUND POLICY VALUES, reproducing
        // UserController.GetUserCreateStatus L607-L609, which read the same two numbers from the provider
        // facade and rewrote them into the message at run time.
        string invalidPasswordMessage = InvalidPasswordMessageTemplate
            .Replace(
                PasswordLengthToken,
                policy.MinRequiredPasswordLength.ToString(CultureInfo.InvariantCulture),
                StringComparison.Ordinal)
            .Replace(
                NoneAlphabetToken,
                policy.MinRequiredNonAlphanumericCharacters.ToString(CultureInfo.InvariantCulture),
                StringComparison.Ordinal);

        // No distinctness rule on the login name. A duplicate is a service-layer outcome, not a request
        // defect: the legacy path attempted the write and mapped the resulting provider status through
        // GetUserCreateStatus L619-L620 onto a dedicated message.
        RuleFor(request => request.Username)
            .NotEmpty().WithMessage(UsernameRequiredMessage)
            .MaximumLength(UsernameMaximumLength).WithMessage(InvalidUserNameMessage);

        // The family name is REQUIRED. The baseline script declared the column nullable at 01.00.00:L100,
        // but the 01.00.05 rebuild re-declared it NOT NULL at 01.00.05:L18 and the 01.00.06 rebuild
        // preserved that at 01.00.06:L186, so accepting a missing family name would defer a constraint
        // violation to write time.
        RuleFor(request => request.FirstName)
            .NotEmpty().WithMessage(FirstNameRequiredMessage)
            .MaximumLength(PersonNameMaximumLength).WithMessage(InvalidUserNameMessage);

        RuleFor(request => request.LastName)
            .NotEmpty().WithMessage(LastNameRequiredMessage)
            .MaximumLength(PersonNameMaximumLength).WithMessage(InvalidUserNameMessage);

        RuleFor(request => request.DisplayName)
            .MaximumLength(DisplayNameMaximumLength).WithMessage(InvalidDisplayNameMessage)
            .When(request => !string.IsNullOrEmpty(request.DisplayName));

        RuleFor(request => request.Email)
            .NotEmpty().WithMessage(EmailRequiredMessage)
            .MaximumLength(EmailMaximumLength).WithMessage(InvalidEmailMessage)
            .Must(candidate => EmailAddress.TryCreate(candidate, out _))
                .WithMessage(EmailFormatMessage);

        RuleFor(request => request.Password)
            .NotEmpty().WithMessage(invalidPasswordMessage)
            .MinimumLength(policy.MinRequiredPasswordLength).WithMessage(invalidPasswordMessage)
            .Must(CredentialBounds.IsWithinMaximumByteLength)
                .WithMessage(CredentialBounds.MaximumByteLengthMessage);

        if (policy.MinRequiredNonAlphanumericCharacters > 0)
        {
            int minimumNonAlphanumericCharacters = policy.MinRequiredNonAlphanumericCharacters;

            RuleFor(request => request.Password)
                .Must(password =>
                    HasEnoughNonAlphanumericCharacters(password, minimumNonAlphanumericCharacters))
                .WithMessage(invalidPasswordMessage)
                .When(request => !string.IsNullOrEmpty(request.Password));
        }

        RuleFor(request => request.ConfirmPassword)
            .Equal(request => request.Password).WithMessage(PasswordMismatchMessage);

        // MIGRATION: NO RECOVERY QUESTION OR ANSWER RULE, AND NO REQUEST MEMBER TO ATTACH ONE TO. The
        // provider's question-and-answer attribute at Website/release.config L241 is false and the legacy
        // screen hid both inputs and skipped their checks unless the provider demanded them, so nothing was
        // enforced in the observed installation.

        if (!string.IsNullOrEmpty(policy.PasswordStrengthRegularExpression))
        {
            Regex configuredStrengthPattern = new(
                policy.PasswordStrengthRegularExpression,
                RegexOptions.CultureInvariant,
                PatternMatchTimeout);

            RuleFor(request => request.Password)
                .Matches(configuredStrengthPattern).WithMessage(invalidPasswordMessage)
                .When(request => !string.IsNullOrEmpty(request.Password));
        }

        // No lower-bound test on any identifier, and none is even reachable this contract carries no key of
        // any kind, because a caller-supplied tenant key would breach tenant isolation and a
        // caller-supplied user key would be ignored rather than honoured.
    }

    /// <summary>
    /// Reports whether a submitted credential carries at least the configured number of characters outside
    /// the ranges <c>0-9</c>, <c>A-Z</c> and <c>a-z</c>.
    /// </summary>
    /// <param name="password">The submitted credential, which may be absent or blank.</param>
    /// <param name="minimum">The configured minimum count.</param>
    /// <returns>
    /// <see langword="true"/> when the credential carries at least <paramref name="minimum"/> such
    /// characters, and also when it is absent or blank, for the reason given on <see
    /// cref="CredentialBounds.IsWithinMaximumByteLength(string?)"/>.
    /// </returns>
    private static bool HasEnoughNonAlphanumericCharacters(string? password, int minimum)
    {
        if (string.IsNullOrEmpty(password))
        {
            return true;
        }

        int found = 0;

        foreach (char character in password)
        {
            if (!char.IsAsciiLetterOrDigit(character) && ++found >= minimum)
            {
                return true;
            }
        }

        return found >= minimum;
    }

    /// <summary>
    /// Reports whether a supplied address satisfies the single legacy shape rule, by asking the Domain
    /// value object that owns that rule rather than by restating it.
    /// </summary>
    /// <param name="email">The address as supplied by the caller.</param>
    /// <returns>
    /// <see langword="true"/> when the address is well formed, or when no address was supplied at all; <see
    /// langword="false"/> only when a supplied address is malformed.
    /// </returns>
    private static bool BeAWellFormedEmailAddress(string? email)
    {
        if (string.IsNullOrWhiteSpace(email))
        {
            return true;
        }

        return EmailAddress.TryCreate(email, out _);
    }
}
