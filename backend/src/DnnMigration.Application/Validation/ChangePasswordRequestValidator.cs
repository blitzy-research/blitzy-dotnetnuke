// 02 of 18 - THE MEASURED DEFECT AT UserController.vb:L1086, ANNOTATED RATHER THAN COPIED. The first two
// legacy checks ACCUMULATE ("isValid = False"), but the third performs a plain ASSIGNMENT ("isValid =
// rx.IsMatch(password)") that OVERWRITES whatever the first two concluded.

// The target separates the two legacy cases across the two operations instead of across a privilege test,
// which is what lets a validator enforce proof of possession without knowing the caller.

// MIGRATION 06b of 18 - THE THIRD LEGACY OPERATION IS NOT VALIDATED HERE BECAUSE IT NO LONGER EXISTS. The
// recovery question-and-answer panel's three guards - L321 the current credential, L326 the new question,
// L331 the new answer - have no target counterpart, because the recovery pair has none: the owning service
// contract states that the legacy question-and-answer member has no counterpart and that no member declares
// a question or answer parameter, the pair existed to guard credential retrieval, and retrieval is dropped
// outright.

// 07 of 18 - THE CURRENT PASSWORD IS NEVER POLICY-CHECKED. It is an EXISTING stored credential, not a new
// one.

// MIGRATION 08 of 18 - THE MARKUP'S maxlength="20" IS NOT CARRIED ACROSS, AND THE CEILING THAT IS APPLIED
// COMES FROM THE HASHING PRIMITIVE INSTEAD. All seven legacy text boxes declare the twenty and the baseline
// column was nvarchar(20) at 01.00.00.SqlDataProvider:L106, but 01.00.06.SqlDataProvider:L192 widens the
// column to nvarchar(50) and credentials later move to the externally installed membership tables.

// 10 of 18 - NO QUESTION-AND-ANSWER RULE AT ALL, AND NO MEMBER LEFT TO ATTACH ONE TO.
// requiresQuestionAndAnswer is false at Website/release.config:L241 and the reset flow's legacy guard at
// L240 is compound - "RequiresQuestionAndAnswer And Not IsAdmin" - so nothing was ever enforced in the
// observed installation.

// MIGRATION 11 of 18 - PASSWORD RETRIEVAL IS NOT CARRIED FORWARD, so no rule, field or message here asks
// for a stored password back.

// 12 of 18 - NO CURRENT-PASSWORD VERIFICATION HERE. Whether the supplied current password matches a BCrypt
// digest or an enabled legacy representation is an infrastructure concern orchestrated by the
// authentication service.

// 13 of 18 - NO IDENTIFIER BOUND TEST, AND NONE IS POSSIBLE HERE ANYWAY. The request carries no identifier
// at all: the target user is route-sourced and the portal is resolved from the request-scoped portal
// context.

// 14 of 18 - NO LOCKOUT OR ATTEMPT-THROTTLING RULE. passwordAttemptThreshold and passwordAttemptWindow
// appear only inside the reference comment at Website/release.config:L224-L225 and are NOT set on the
// provider element at L236-L247, so neither is measured policy.

// 15 of 18 - THE RESOURCE SPACING IS INCONSISTENT AND IS REPRODUCED PER KEY, NOT BLANKET-DOUBLED. Measured
// byte-precisely from Website/App_GlobalResources/SharedResources.resx, where each key is declared on one
// line and its wording sits on the next: InvalidPassword (key L285, wording L286), PasswordInvalid
// (L963/L964) and PasswordNotDifferent (L975/L976) use DOUBLE spaces after a sentence period, whereas
// PasswordMissing (L972/L973) and PasswordResetFailed (L978/L979) use SINGLE spaces.

// 16 of 18 - A LEGACY DOCUMENTATION INCONSISTENCY, NOTED AND NOT ACTED UPON. The attribute reference
// comment at Website/release.config:L222-L235 is headed "Configuration for DNNSQLMembershipProvider", while
// the provider actually registered at L236 is named AspNetSqlMembershipProvider.

// MIGRATION 18 of 18 - THE OPERATION DISCRIMINATOR RULE IS NET-NEW. The legacy screen carried three
// independent submit buttons, each wired to its own handler, so the operation was never ambiguous.

using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using DnnMigration.Application.Dtos.User;
using DnnMigration.Application.Options;
using FluentValidation;

namespace DnnMigration.Application.Validation;

/// <summary>
/// Validates <see cref="ChangePasswordRequest"/>, the inbound contract for the password sub-resource of a
/// user. Reproduces the rules measured in the legacy DotNetNuke password administration screen, rule for
/// rule and message for message.
/// </summary>
/// <remarks>
/// <para>
/// <b>THE CURRENT PASSWORD IS DELIBERATELY NOT POLICY-CHECKED.</b> It is an existing stored credential
/// rather than a new one, and the shipped database seeds the Host and Administrator accounts with
/// four-character and five-character passwords respectively - both shorter than the minimum.
/// </para>
/// <para>
/// <b>WHAT THIS VALIDATOR DELIBERATELY DOES NOT DO.</b> It never compares the supplied current password
/// against the stored credential, because that requires the infrastructure hashing service reached through
/// the application service. It never decides whether the caller may change this user's password, because
/// that is authorisation.
/// </para>
/// </remarks>
public class ChangePasswordRequestValidator : AbstractValidator<ChangePasswordRequest>
{
    /// <summary>
    /// The legacy <c>InvalidPassword</c> wording, carrying the two legacy substitution tokens verbatim.
    /// Measured from <c>SharedResources.resx</c> (key L285, wording L286).
    /// </summary>
    private const string InvalidPasswordMessageTemplate =
        "The password specified is invalid.  Please specify a valid password.  Passwords must be at " +
        "least [PasswordLength] characters in length and contain at least [NoneAlphabet] " +
        "non-alphanumeric characters.";

    /// <summary>
    /// The legacy <c>PasswordMismatch</c> wording (key L852, wording L853). It carries no trailing period
    /// in the original.
    /// </summary>
    private const string PasswordMismatchMessage =
        "The Password and Confirmation Passwords do not match";

    /// <summary>
    /// The legacy <c>PasswordMissing</c> wording (key L972, wording L973). Single-spaced in the original.
    /// </summary>
    private const string PasswordMissingMessage =
        "You must provide your current password in order to change the password.";

    /// <summary>
    /// The legacy <c>PasswordNotDifferent</c> wording (key L975, wording L976). Double-spaced after the
    /// sentence period and carrying no trailing period, both as measured.
    /// </summary>
    private const string PasswordNotDifferentMessage =
        "The new password is the same as the old password.  Please enter a different password";

    // The InvalidPasswordQuestion wording and the InvalidPasswordAnswer wording (key L954, wording L955)
    // are deliberately NOT declared here, and neither may be added.

    /// <summary>
    /// Net-new wording: a reset must not carry the credential it is replacing, and no legacy screen ever
    /// had to say so, because its reset panel had no field for one.
    /// </summary>
    private const string CurrentPasswordNotAcceptedMessage =
        "The current password must not be supplied when resetting a password.";

    /// <summary>
    /// Net-new wording: the legacy screen's three submit buttons made the operation self-evident, so no
    /// legacy equivalent exists.
    /// </summary>
    private const string OperationRequiredMessage = "The password operation must be specified.";

    /// <summary>Net-new wording, for the same reason as <see cref="OperationRequiredMessage"/>.</summary>
    private const string OperationUnrecognisedMessage =
        "The password operation specified is not recognised.";

    // Everything the old constant's own documentation said still holds: the ceiling is net-new, it is not a
    // policy rule, it is emphatically NOT the legacy markup's twenty-character input limit, and it sits far
    // above any credential a person would plausibly choose, so it cannot reject a legitimate password.

    /// <summary>
    /// How long a configured strength pattern may run against a single candidate before it is abandoned.
    /// The pattern is operator-supplied configuration applied to user-supplied input, so an unbounded match
    /// would be a denial-of-service vector; the bound is generous enough that no reasonable pattern reaches
    /// it.
    /// </summary>
    private const int StrengthPatternTimeoutMilliseconds = 250;

    /// <summary>Initialises the validator against the password policy in force.</summary>
    /// <param name="passwordPolicy">
    /// The bound password policy, supplied by the container from the configuration section named by <see
    /// cref="PasswordPolicyOptions.SectionName"/>.
    /// </param>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="passwordPolicy"/> is <see langword="null"/>.
    /// </exception>
    public ChangePasswordRequestValidator(PasswordPolicyOptions passwordPolicy)
    {
        ArgumentNullException.ThrowIfNull(passwordPolicy);

        int minimumPasswordLength = passwordPolicy.MinRequiredPasswordLength;
        int minimumNonAlphanumericCharacters = passwordPolicy.MinRequiredNonAlphanumericCharacters;
        string strengthPattern = passwordPolicy.PasswordStrengthRegularExpression;

        // PasswordPolicyOptions.RequiresQuestionAndAnswer is deliberately NOT read here, and must not be
        // read to gate an answer rule on the reset flow.

        string invalidPasswordMessage = InvalidPasswordMessageTemplate
            .Replace(
                "[PasswordLength]",
                minimumPasswordLength.ToString(CultureInfo.InvariantCulture),
                StringComparison.Ordinal)
            .Replace(
                "[NoneAlphabet]",
                minimumNonAlphanumericCharacters.ToString(CultureInfo.InvariantCulture),
                StringComparison.Ordinal);

        // The operation discriminator
        // Net-new, and required before any other rule can be scoped. Stopping after the first failure
        // avoids reporting "not specified" and "not recognised" together for one absent value.
        RuleFor(request => request.Operation)
            .Cascade(CascadeMode.Stop)
            .NotEmpty().WithMessage(OperationRequiredMessage)
            .Must(operation => IsRecognisedOperation(operation))
                .WithMessage(OperationUnrecognisedMessage);

        // BOTH operations: the replacement credential
        // MIGRATION: the two credential-policy rules below apply to the RESET operation as well as to
        // CHANGE. The legacy reset had no credential input because the membership provider generated one
        // and returned it (L906, returned at L915); generation is not carried forward, because a generated
        // credential must be transmitted to be useful, the mail subsystem is excluded, and no endpoint
        // returns a credential - so a generated value would be knowable to nobody and the reset would be a
        // lockout.
        RuleFor(request => request.ConfirmPassword)
            .Equal(request => request.NewPassword)
                .WithMessage(PasswordMismatchMessage)
            .When(request => IsOperation(request, ChangePasswordRequest.OperationChange));

        // Legacy check 2, L278, which delegates to ValidatePassword at L1067-L1091. The minimum length is
        // the only policy rule that fires under the SHIPPED configuration, and it is bound rather than
        // written.
        RuleFor(request => request.NewPassword)
            .Cascade(CascadeMode.Stop)
            .NotEmpty().WithMessage(invalidPasswordMessage)
            .MinimumLength(minimumPasswordLength).WithMessage(invalidPasswordMessage)
            .Must(CredentialBounds.IsWithinMaximumByteLength)
                .WithMessage(CredentialBounds.MaximumByteLengthMessage)
            .When(IsCredentialReplacement);

        if (minimumNonAlphanumericCharacters > 0)
        {
            RuleFor(request => request.NewPassword)
                .Must(password =>
                    HasEnoughNonAlphanumericCharacters(password, minimumNonAlphanumericCharacters))
                .WithMessage(invalidPasswordMessage)
                .When(request =>
                    IsCredentialReplacement(request) && !string.IsNullOrEmpty(request.NewPassword));
        }

        // CHANGE only: proof of possession
        // Legacy check 3, L284: "Not IsAdmin And txtOldPassword.Text = """.
        RuleFor(request => request.CurrentPassword)
            .NotEmpty().WithMessage(PasswordMissingMessage)
            .When(request => IsOperation(request, ChangePasswordRequest.OperationChange));

        // Legacy check 4, L290: "Not IsAdmin And txtNewPassword.Text = txtOldPassword.Text".
        RuleFor(request => request.NewPassword)
            .NotEqual(request => request.CurrentPassword)
                .WithMessage(PasswordNotDifferentMessage)
            .When(request =>
                IsOperation(request, ChangePasswordRequest.OperationChange)
                && !string.IsNullOrEmpty(request.CurrentPassword)
                && !string.IsNullOrEmpty(request.NewPassword));

        // RESET only: the current credential must be ABSENT
        // MIGRATION: NET-NEW, with no legacy counterpart because the legacy reset panel had no field for a
        // current credential at all - it could not have supplied one.
        RuleFor(request => request.CurrentPassword)
            .Empty().WithMessage(CurrentPasswordNotAcceptedMessage)
            .When(request => IsOperation(request, ChangePasswordRequest.OperationReset));

        // The optional strength pattern: ValidatePassword L1084-L1086
        if (!string.IsNullOrEmpty(strengthPattern))
        {
            Regex strengthMatcher = new Regex(
                strengthPattern,
                RegexOptions.CultureInvariant,
                TimeSpan.FromMilliseconds(StrengthPatternTimeoutMilliseconds));

            RuleFor(request => request.NewPassword)
                .Matches(strengthMatcher).WithMessage(invalidPasswordMessage)
                .When(IsCredentialReplacement);
        }

        // The shared credential ceiling, applied unconditionally to the CURRENT password
        // MIGRATION: net-new, and deliberately NOT scoped to an operation.
        RuleFor(request => request.CurrentPassword)
            .Must(CredentialBounds.IsWithinMaximumByteLength)
                .WithMessage(CredentialBounds.MaximumByteLengthMessage);
    }

    /// <summary>
    /// Reports whether the request declares the given operation, comparing ordinally against the constants
    /// published by <see cref="ChangePasswordRequest"/> so that the operation names are defined in exactly
    /// one place.
    /// </summary>
    /// <param name="request">The request whose declared operation is being tested.</param>
    /// <param name="operation">The operation constant to test against.</param>
    /// <returns><see langword="true"/> when the request declares that operation.</returns>
    private static bool IsOperation(ChangePasswordRequest request, string operation) =>
        string.Equals(request.Operation, operation, StringComparison.Ordinal);

    /// <summary>
    /// Reports whether the request replaces the account's credential, which both supported operations do -
    /// a self-service change and an administrative reset.
    /// </summary>
    /// <param name="request">The request whose declared operation is being tested.</param>
    /// <returns><see langword="true"/> when the declared operation is either a change or a reset.</returns>
    /// <remarks>
    /// The rules that govern the replacement credential itself - presence, the bound minimum length, the
    /// hasher's encoded-byte ceiling, the confirmation comparison and any configured strength pattern - are
    /// identical for both operations, so they share one condition rather than being written twice.
    /// </remarks>
    private static bool IsCredentialReplacement(ChangePasswordRequest request) =>
        IsOperation(request, ChangePasswordRequest.OperationChange)
        || IsOperation(request, ChangePasswordRequest.OperationReset);

    /// <summary>
    /// Reports whether a declared operation is one of the two the target supports. Matching is ordinal and
    /// exact, which keeps the wire contract deterministic and avoids any hidden normalisation of a value
    /// that selects between a credential change and a credential reset.
    /// </summary>
    /// <param name="operation">The declared operation, which may be absent.</param>
    /// <returns><see langword="true"/> when the operation is recognised.</returns>
    private static bool IsRecognisedOperation(string? operation) =>
        string.Equals(operation, ChangePasswordRequest.OperationChange, StringComparison.Ordinal)
        || string.Equals(operation, ChangePasswordRequest.OperationReset, StringComparison.Ordinal);

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
}
