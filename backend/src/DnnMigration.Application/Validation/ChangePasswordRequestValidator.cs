// MIGRATION 01 of 18 - THE ONLY ACTIVE LEGACY PASSWORD RULE IS A MINIMUM LENGTH, AND IT IS THE ONLY
// ONE REPRODUCED HERE. Library/Components/Users/UserController.vb:L1067-L1091 wrote three checks, and
// with the shipped configuration only the first can ever fire:
//   L1073  password.Length < MinPasswordLength                     -> ACTIVE. minRequiredPasswordLength
//          is seven at Website/release.config:L242.
//   L1079  Regex("[^0-9a-zA-Z]").Matches(password).Count < Min      -> VACUOUS.
//          minRequiredNonalphanumericCharacters is zero at Website/release.config:L243, and a match
//          count is never below zero, so the comparison can never fail.
//   L1084  strength pattern, when configured                        -> NEVER FIRES. Searching BOTH
//          Website/release.config AND Website/development.config for the setting exits non-zero with
//          no match, so it is PROVEN ABSENT and the legacy emptiness test at L1084 was always false.
// Consequently NO letter-case requirement, NO digit requirement, NO punctuation requirement, NO
// character-variety requirement, NO repeated-character limit, NO dictionary or breach-list check and NO
// comparison against previously set credentials is added. Every one of those would TIGHTEN the policy,
// which the Minimal Change Clause forbids and which the plan defers verbatim: "Tightening the policy
// during a migration would deny existing users access, so any hardening is left as a separate, explicit
// decision."

// MIGRATION 02 of 18 - THE MEASURED DEFECT AT UserController.vb:L1086, ANNOTATED RATHER THAN COPIED.
// The first two legacy checks ACCUMULATE ("isValid = False"), but the third performs a plain ASSIGNMENT
// ("isValid = rx.IsMatch(password)") that OVERWRITES whatever the first two concluded. A password that
// had already failed the length check could therefore be declared valid by a strength-pattern match.
// The defect is DORMANT, because the guard at L1084 never opens in this installation. The rules below
// contribute INDEPENDENTLY - the validation framework collects each failure separately - so the defect
// is NOT reproduced. That correction is itself a behavioural divergence and is recorded as one here
// rather than absorbed silently. The legacy file is read-only and was not edited.

// MIGRATION 03 of 18 - THE POLICY IS BOUND, NEVER HARD-CODED. The message template below keeps the
// legacy [PasswordLength] and [NoneAlphabet] tokens and substitutes them from the bound policy values,
// reproducing UserController.vb:L607-L609 exactly. Neither the minimum length nor the non-alphanumeric
// minimum appears as a literal anywhere in this file, so the wording can never drift away from the rule
// it describes when the configuration changes.

// MIGRATION 04 of 18 - AN UNCONFIGURED STRENGTH PATTERN SKIPS THE RULE ENTIRELY. An empty pattern
// matches every input, so applying one would be a silent no-op wearing the appearance of enforcement.
// The rule is therefore not merely guarded but CONDITIONALLY CONSTRUCTED: when the pattern is empty the
// matcher is never built at all, so an empty pattern is never compiled.

// MIGRATION 05 of 18 - Website/admin/Users/Password.ascx DECLARES ZERO VALIDATORS OF ANY KIND. A census
// of that 100-line file returns zero for every declarative validator category, and there is no regular
// expression validator in it or in any sibling admin screen. Every rule below is therefore a documented
// DERIVATION from the measured configuration, the measured resource wording and the measured code-behind
// flow - never a transcription of declarative markup, and never a fabricated pattern.

// MIGRATION 06 of 18 - THE CURRENT PASSWORD IS NOT UNCONDITIONALLY REQUIRED, BECAUSE THE LEGACY GUARD
// WAS COMPOUND. Password.ascx.vb:L284 reads "Not IsAdmin And txtOldPassword.Text = """, and L290 reads
// "Not IsAdmin And txtNewPassword.Text = txtOldPassword.Text". Both halves matter: the row itself is
// hidden for an administrator editing another account (L150-L152 sets trOldPassword.Visible = False),
// and trOldPassword is declared runat="server" at Password.ascx:L33 precisely so it can be hidden. The
// caller-privilege half is resolved from the current-user abstraction and the caller's role claims,
// which are deliberately absent from the request and unavailable to a validator, so this file does NOT
// assert an unconditional presence rule for the change flow. Doing so would delete the administrative
// password-change path and breach the parity requirement that "every workflow reachable from the legacy
// admin pages must be supported". Presence for that flow is enforced where privilege is known: the
// application service. The change-question-and-answer flow is different - its legacy guard at L321 has
// no privilege half - so presence IS asserted there.

// MIGRATION 07 of 18 - THE CURRENT PASSWORD IS NEVER POLICY-CHECKED. It is an EXISTING stored credential,
// not a new one. The shipped database seeds the Host account with a four-character password at
// 01.00.00.SqlDataProvider:L7205 and the Administrator account with a five-character password at L7207,
// both below the minimum of seven. Applying the length policy to the current password would leave those
// two accounts permanently unable to change their own passwords. Only the NEW password is policy-checked.

// MIGRATION 08 of 18 - THE MARKUP'S maxlength="20" IS NOT CARRIED ACROSS. All seven legacy text boxes
// declare it and the baseline column was nvarchar(20) at 01.00.00.SqlDataProvider:L106, but
// 01.00.06.SqlDataProvider:L192 widens the column to nvarchar(50) and credentials later move to the
// externally installed membership tables. Under one-way hashing the stored width is irrelevant, because
// the digest is fixed-length whatever the input. Twenty would therefore be a TIGHTENING that rejects
// legitimately long passwords. The generous ceiling below is NET-NEW, exists only to bound the size of
// an inbound field, and is deliberately not derived from any legacy value.

// MIGRATION 09 of 18 - THE "NEW MUST DIFFER FROM CURRENT" RULE IS AUTHORED, GUARDED. It has genuine
// measured backing at Password.ascx.vb:L290, and its wording exists as PasswordNotDifferent. Because
// Password.ascx declares no validators the legacy comparison lived in the code-behind, and resource-key
// existence alone is never proof of enforcement - the code-behind line is the proof. The guard requires
// BOTH values to be supplied, which faithfully reproduces the legacy "Not IsAdmin" half without needing
// caller privilege: an administrator supplies no current password at all, so the rule correctly abstains
// for that path exactly as L290 did.

// MIGRATION 10 of 18 - NO QUESTION-AND-ANSWER RULE ON THE DEFAULT PATH. requiresQuestionAndAnswer is
// false at Website/release.config:L241. The reset flow's legacy guard at L240 is compound -
// "RequiresQuestionAndAnswer And Not IsAdmin" - so the answer rule below is gated on the bound policy
// flag and never fires under the shipped configuration. The four related resource keys do exist, but
// their existence is NOT evidence of enforcement. No such rule is ever unconditional. The
// change-question-and-answer flow's own rules are reached only when that operation is explicitly
// requested; the legacy panel was itself configuration-gated at L187.

// MIGRATION 11 of 18 - PASSWORD RETRIEVAL IS NOT CARRIED FORWARD, so no rule, field or message here
// asks for a stored password back. The legacy retrieval entry point on the user controller (L433) also
// used a ByRef argument, and no by-reference parameter appears in any target public API. It replaced
// the legacy retrieval switch at Website/release.config:L239 together with the reversible cipher key
// committed to that same file in the clear at L89-L93; neither the switch nor the key is reproduced
// anywhere in this solution. Note that RESET is a different flag (L240) and IS carried forward.

// MIGRATION 12 of 18 - NO CURRENT-PASSWORD VERIFICATION HERE. Whether the supplied current password
// matches the stored digest is an infrastructure concern reached through the application service, and
// the documented transition - re-hash on first successful login, with administrative reset as the
// fallback - is service behaviour. This file therefore performs no digest work, touches no repository
// and runs no asynchronous rule. Its only dependency is the bound password policy.

// MIGRATION 13 of 18 - NO IDENTIFIER BOUND TEST, AND NONE IS POSSIBLE HERE ANYWAY. The request carries
// no identifier at all: the target user is route-sourced and the portal is resolved from the
// request-scoped portal context. The prohibition still stands and is recorded because the legacy seeds
// make it load-bearing - Portals.PortalID is IDENTITY (-1, 1) at 01.00.00.SqlDataProvider:L77 with the
// shipped _default portal at PortalID zero (L7125), and Roles.RoleID is IDENTITY (0, 1) at L115 - so
// neither zero nor minus one may ever be treated as "absent". Absence is expressed by a nullable type.
// Existence is a repository concern producing a not-found response, never a validation failure.

// MIGRATION 14 of 18 - NO LOCKOUT OR ATTEMPT-THROTTLING RULE. passwordAttemptThreshold and
// passwordAttemptWindow appear only inside the reference comment at Website/release.config:L224-L225
// and are NOT set on the provider element at L236-L247, so neither is measured policy. Request-rate
// limiting is a transport-edge concern owned by the Api layer.

// MIGRATION 15 of 18 - THE RESOURCE SPACING IS INCONSISTENT AND IS REPRODUCED PER KEY, NOT
// BLANKET-DOUBLED. Measured byte-precisely from Website/App_GlobalResources/SharedResources.resx, where
// each key is declared on one line and its wording sits on the next: InvalidPassword (key L285, wording
// L286), PasswordInvalid (L963/L964) and PasswordNotDifferent (L975/L976) use DOUBLE spaces after a
// sentence period, whereas PasswordMissing (L972/L973) and PasswordResetFailed (L978/L979) use SINGLE
// spaces. PasswordMismatch and PasswordNotDifferent carry NO trailing period. PasswordMismatch is also
// DUPLICATED - the ".Text" entry at L852/L853 is the wording used below, while a second ".Text1" entry
// at L969/L970 carries a differently-cased variant of the same sentiment that ends in a period. The
// legacy code passes the BARE key (UserController.vb:L612), which resolves to the ".Text" form, so the
// L852/L853 form is the one reproduced here and the L969/L970 variant is deliberately NOT reproduced
// anywhere in this file - not even as a quotation, so that its absence is provable by search. The
// duplication itself is recorded and deliberately left unresolved. PasswordResetFailed is a
// service-layer persistence outcome and is deliberately NOT attached to any rule below.

// MIGRATION 16 of 18 - A LEGACY DOCUMENTATION INCONSISTENCY, NOTED AND NOT ACTED UPON. The attribute
// reference comment at Website/release.config:L222-L235 is headed "Configuration for
// DNNSQLMembershipProvider", while the provider actually registered at L236 is named
// AspNetSqlMembershipProvider. The measured element span is L236-L247 with its policy attributes at
// L239-L245; the plan cites the span as L236-L246. The values were read from the element, never from
// the comment.

// MIGRATION 17 of 18 - EVERY COMPARISON IS EXPLICIT, BECAUSE THE LEGACY SCREENS COMPILED WITH OPTION
// STRICT OFF. Website/release.config:L125 declares compilation strict="false" for all admin
// code-behinds, so Password.ascx.vb could rely on implicit narrowing and late binding that C# rejects.
// Two coercions are made explicit here. First, every legacy guard tests against the EMPTY STRING rather
// than against a null - "txtOldPassword.Text = """ at L284, "txtAnswer.Text = """ at L241,
// "txtQAPassword.Text = """ at L321 - because a Web Forms text box never yields a null and because the
// legacy sentinel module returns literally "" from NullString (Null.vb:L71-L75). The request models
// both states, so every optional guard tests null AND empty together rather than null alone. Second,
// each integer substituted into a message is formatted against the invariant culture and each string
// comparison names its ordinal semantics, so neither depends on ambient culture.

// MIGRATION 18 of 18 - THE OPERATION DISCRIMINATOR RULE IS NET-NEW. The legacy screen carried three
// independent submit buttons, each wired to its own handler, so the operation was never ambiguous. A
// single endpoint has no equivalent signal, and the request folder holds one union type rather than
// three request types, so the intent is stated explicitly and this file rejects an absent or
// unrecognised value. That rejection has no legacy counterpart and no legacy wording, so its two
// messages are net-new. Note the hazard it removes: because the shipped configuration does not require
// a question and answer, a reset carries no field at all, so inferring the operation from which members
// happen to be populated would read an empty body as "reset this user's credential".

using System.Globalization;
using System.Text.RegularExpressions;
using DnnMigration.Application.Dtos.User;
using DnnMigration.Application.Options;
using FluentValidation;

namespace DnnMigration.Application.Validation;

/// <summary>
/// Validates <see cref="ChangePasswordRequest"/>, the inbound contract for the password sub-resource of
/// a user. Reproduces the rules measured in the legacy DotNetNuke password administration screen, rule
/// for rule and message for message.
/// </summary>
/// <remarks>
/// <para>
/// <b>THE ONLY POLICY RULE IS A MINIMUM LENGTH, AND IT IS BOUND FROM CONFIGURATION.</b> The new password
/// must be present and must be at least <see cref="PasswordPolicyOptions.MinRequiredPasswordLength"/>
/// characters long, and that is the whole of the policy. There is no letter-case, digit, punctuation or
/// character-variety requirement, and no comparison against previously set credentials, because the
/// legacy installation enforced none: its non-alphanumeric minimum is zero, which makes that check
/// impossible to fail, and its strength pattern is configured in neither legacy configuration file.
/// Adding any of them would tighten the policy and deny users access as they are migrated. The
/// minimum itself is never written as a literal here; it is read from the bound policy, and the same
/// bound values are substituted into the failure message so wording and rule cannot drift apart.
/// </para>
/// <para>
/// <b>THE CURRENT PASSWORD IS DELIBERATELY NOT POLICY-CHECKED.</b> It is an existing stored credential
/// rather than a new one, and the shipped database seeds the Host and Administrator accounts with
/// four-character and five-character passwords respectively - both shorter than the minimum. Measuring
/// the current password against the policy would leave precisely those two accounts unable to change
/// their own passwords. It also carries no maximum, for the same reason: no inbound bound may be allowed
/// to reject a credential the legacy application already accepted.
/// </para>
/// <para>
/// <b>WHETHER THE CURRENT PASSWORD IS REQUIRED FOLLOWS THE REQUEST SHAPE AND THE OPERATION.</b> Every
/// member of the request is nullable, and the legacy presence guard for the change flow is compound -
/// it required the current password only when the caller was not an administrator. The legacy markup
/// declares that table row as a server control specifically so it could be hidden, and the code-behind
/// hides it for an administrator editing another account, so an administrative change genuinely carried
/// no current password. Caller privilege is not part of this request and is not available to a
/// validator, so no unconditional presence rule is asserted for the change flow; presence there is
/// enforced by the application service, which knows the caller. The change-question-and-answer flow is
/// the exception: its legacy guard has no privilege half, so presence is asserted for that operation.
/// </para>
/// <para>
/// <b>RULES ARE SCOPED TO THE DECLARED OPERATION.</b> The request is a union of the legacy screen's three
/// independent panels - change, reset, and change question and answer - so a rule that belongs to one
/// panel must not fire for another. Every rule below is therefore conditioned on the declared operation,
/// which reproduces the legacy arrangement in which each submit button reached only its own handler and
/// read only its own panel. Members belonging to another operation are simply not validated, exactly as
/// the legacy handlers ignored the other panels, rather than being rejected as unexpected.
/// </para>
/// <para>
/// <b>WHAT THIS VALIDATOR DELIBERATELY DOES NOT DO.</b> It never compares the supplied current password
/// against the stored credential, because that requires the infrastructure hashing service reached
/// through the application service. It never decides whether the caller may change this user's password,
/// because that is authorisation. It never reads a repository, so it never reports existence - a missing
/// user is a not-found response, never a validation failure. It never asks for a stored password back,
/// because retrieval is not carried forward. It performs no input or output and therefore declares no
/// asynchronous rule. And it never records or echoes any field it validates: an instance of this request
/// can hold three plaintext credentials at once, making it the most sensitive payload in the
/// application, so no message below interpolates a submitted value.
/// </para>
/// <para>
/// <b>Registration.</b> This type is discovered by the assembly-scanning validator registration in the
/// Application layer's composition extension at a scoped lifetime, which is what allows it to take a
/// constructor dependency at all, and it is public because that scan does not consider internal types.
/// It must not register itself, and it must not bind its own configuration section; the Api layer binds
/// <see cref="PasswordPolicyOptions"/> from the section named by
/// <see cref="PasswordPolicyOptions.SectionName"/>.
/// </para>
/// </remarks>
public class ChangePasswordRequestValidator : AbstractValidator<ChangePasswordRequest>
{
    /// <summary>
    /// The legacy <c>InvalidPassword</c> wording, carrying the two legacy substitution tokens verbatim.
    /// Measured from <c>SharedResources.resx</c> (key L285, wording L286). The double spaces after the
    /// two sentence periods are part of the measured original and are intentional.
    /// </summary>
    private const string InvalidPasswordMessageTemplate =
        "The password specified is invalid.  Please specify a valid password.  Passwords must be at " +
        "least [PasswordLength] characters in length and contain at least [NoneAlphabet] " +
        "non-alphanumeric characters.";

    /// <summary>
    /// The legacy <c>PasswordMismatch</c> wording (key L852, wording L853). It carries no trailing
    /// period in the original. The duplicate <c>.Text1</c> variant at L969/L970 is deliberately unused,
    /// because the legacy code resolves the bare key to this form.
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

    /// <summary>
    /// The legacy <c>InvalidPasswordQuestion</c> wording (key L957, wording L958).
    /// </summary>
    private const string InvalidPasswordQuestionMessage = "Password Question must be provided";

    /// <summary>
    /// The legacy <c>InvalidPasswordAnswer</c> wording (key L954, wording L955).
    /// </summary>
    private const string InvalidPasswordAnswerMessage = "Password Answer must be provided";

    /// <summary>
    /// Net-new wording: the legacy screen's three submit buttons made the operation self-evident, so no
    /// legacy equivalent exists.
    /// </summary>
    private const string OperationRequiredMessage = "The password operation must be specified.";

    /// <summary>
    /// Net-new wording, for the same reason as <see cref="OperationRequiredMessage"/>.
    /// </summary>
    private const string OperationUnrecognisedMessage =
        "The password operation specified is not recognised.";

    /// <summary>
    /// Net-new wording for the net-new inbound ceiling described by
    /// <see cref="PasswordMaximumLength"/>.
    /// </summary>
    private const string PasswordTooLongMessage =
        "The password specified is longer than this endpoint accepts.";

    /// <summary>
    /// A deliberately generous ceiling on the length of a submitted new password. This is NET-NEW and
    /// exists only to bound the size of an inbound field; it is not a policy rule and it is emphatically
    /// not the legacy markup's twenty-character input limit, which is not carried across. It sits far
    /// above any credential a user would plausibly choose, so it cannot reject a legitimate password.
    /// </summary>
    private const int PasswordMaximumLength = 256;

    /// <summary>
    /// How long a configured strength pattern may run against a single candidate before it is abandoned.
    /// The pattern is operator-supplied configuration applied to user-supplied input, so an unbounded
    /// match would be a denial-of-service vector; the bound is generous enough that no reasonable
    /// pattern reaches it.
    /// </summary>
    private const int StrengthPatternTimeoutMilliseconds = 250;

    /// <summary>
    /// Initialises the validator against the password policy in force.
    /// </summary>
    /// <param name="passwordPolicy">
    /// The password policy, bound by the Api layer from the configuration section named by
    /// <see cref="PasswordPolicyOptions.SectionName"/>. Supplies the minimum length that the only active
    /// rule enforces, the two values substituted into the legacy failure wording, the flag that gates
    /// the reset flow's answer rule, and the optional strength pattern. Read once here rather than
    /// dereferenced inside each rule, so every rule sees one consistent snapshot of the policy.
    /// </param>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="passwordPolicy"/> is <see langword="null"/>. Failing loudly at
    /// construction is deliberate: a validator that silently fell back to built-in defaults would
    /// enforce a policy nobody configured, which is precisely the kind of silent divergence this
    /// migration is required to surface rather than absorb.
    /// </exception>
    public ChangePasswordRequestValidator(PasswordPolicyOptions passwordPolicy)
    {
        ArgumentNullException.ThrowIfNull(passwordPolicy);

        // MIGRATION NOTE ON THE CONSTRUCTOR SHAPE - measured, and deliberate. The specification for this
        // file asked for IOptions<PasswordPolicyOptions>, and that is NOT resolvable in this project:
        // FluentValidation.DependencyInjectionExtensions 11.12.0 depends only on FluentValidation and on
        // Microsoft.Extensions.DependencyInjection.Abstractions, so Microsoft.Extensions.Options never
        // reaches this compilation. The restored graph for this project is exactly four libraries and
        // contains no options package, and a probe using IOptions<PasswordPolicyOptions> failed to
        // compile. No package reference was added and no project file was edited in response, because
        // the specification requires that this be reported rather than patched, and because the plan
        // fixes this project's package set at the two validation packages above.
        //
        // Taking the bound policy directly is also the convention already established in this solution
        // rather than a concession: every type in the Options folder carries no import at all, the
        // policy type itself documents that it is "declared in this layer and bound by the Api layer",
        // and the only IOptions<T> anywhere in the backend sits in the Api layer where the web framework
        // supplies it. The composition root must therefore make the bound policy instance resolvable in
        // its own right - projecting it from the bound options it already creates - so the container can
        // supply it here. If it does not, construction fails immediately and visibly, which is the
        // intended outcome; it never degrades to an unconfigured policy.
        int minimumPasswordLength = passwordPolicy.MinRequiredPasswordLength;
        int minimumNonAlphanumericCharacters = passwordPolicy.MinRequiredNonAlphanumericCharacters;
        bool requiresQuestionAndAnswer = passwordPolicy.RequiresQuestionAndAnswer;
        string strengthPattern = passwordPolicy.PasswordStrengthRegularExpression;

        // Reproduces UserController.vb:L607-L609: take the legacy wording and replace each token with
        // the configured number. Invariant culture and ordinal comparison are stated explicitly so the
        // result never depends on the ambient culture of the request thread.
        string invalidPasswordMessage = InvalidPasswordMessageTemplate
            .Replace(
                "[PasswordLength]",
                minimumPasswordLength.ToString(CultureInfo.InvariantCulture),
                StringComparison.Ordinal)
            .Replace(
                "[NoneAlphabet]",
                minimumNonAlphanumericCharacters.ToString(CultureInfo.InvariantCulture),
                StringComparison.Ordinal);

        // --- The operation discriminator ------------------------------------------------------------
        // Net-new, and required before any other rule can be scoped. Stopping after the first failure
        // avoids reporting "not specified" and "not recognised" together for one absent value.
        RuleFor(request => request.Operation)
            .Cascade(CascadeMode.Stop)
            .NotEmpty().WithMessage(OperationRequiredMessage)
            .Must(operation => IsRecognisedOperation(operation))
                .WithMessage(OperationUnrecognisedMessage);

        // --- CHANGE flow: Password.ascx.vb cmdUpdate_Click, L269-L307 --------------------------------

        // Legacy check 1, L272: "If txtNewPassword.Text <> txtNewConfirm.Text". Attached to the
        // confirmation rather than to the new password because the confirmation is the field the caller
        // should correct. Two absent values compare equal and pass here, which is correct: the new
        // password's own presence rule is what reports an omitted credential, so one omission yields one
        // message rather than two.
        RuleFor(request => request.ConfirmPassword)
            .Equal(request => request.NewPassword)
                .WithMessage(PasswordMismatchMessage)
            .When(request => IsOperation(request, ChangePasswordRequest.OperationChange));

        // Legacy check 2, L278, which delegates to ValidatePassword at L1067-L1091. The minimum length
        // is the ONLY policy rule, and it is bound rather than written. The ceiling is net-new and
        // generous. Stopping after the first failure keeps an empty submission to a single message,
        // exactly as the legacy check returned a single outcome; it can never erase a failure already
        // recorded, so it is not the L1086 defect in another guise.
        RuleFor(request => request.NewPassword)
            .Cascade(CascadeMode.Stop)
            .NotEmpty().WithMessage(invalidPasswordMessage)
            .MinimumLength(minimumPasswordLength).WithMessage(invalidPasswordMessage)
            .MaximumLength(PasswordMaximumLength).WithMessage(PasswordTooLongMessage)
            .When(request => IsOperation(request, ChangePasswordRequest.OperationChange));

        // Legacy check 4, L290: "Not IsAdmin And txtNewPassword.Text = txtOldPassword.Text". The guard
        // requires both values, which reproduces the privilege half faithfully without needing to know
        // the caller: an administrator supplied no current password, so the comparison correctly
        // abstains for that path.
        RuleFor(request => request.NewPassword)
            .NotEqual(request => request.CurrentPassword)
                .WithMessage(PasswordNotDifferentMessage)
            .When(request =>
                IsOperation(request, ChangePasswordRequest.OperationChange)
                && !string.IsNullOrEmpty(request.CurrentPassword)
                && !string.IsNullOrEmpty(request.NewPassword));

        // Legacy check 3, L284, is INTENTIONALLY ABSENT: see the note above the constructor body and
        // migration note 06. Its privilege half cannot be evaluated here, and asserting the presence half
        // alone would delete the administrative change path.

        // --- The optional strength pattern: ValidatePassword L1084-L1086 -----------------------------
        // Conditionally CONSTRUCTED rather than merely guarded, so that an unconfigured pattern is never
        // compiled. An empty pattern matches everything, so building one would look like enforcement
        // while enforcing nothing. Under the measured configuration this branch is never taken, because
        // the setting is absent from both legacy configuration files.
        if (!string.IsNullOrEmpty(strengthPattern))
        {
            Regex strengthMatcher = new Regex(
                strengthPattern,
                RegexOptions.CultureInvariant,
                TimeSpan.FromMilliseconds(StrengthPatternTimeoutMilliseconds));

            RuleFor(request => request.NewPassword)
                .Matches(strengthMatcher).WithMessage(invalidPasswordMessage)
                .When(request => IsOperation(request, ChangePasswordRequest.OperationChange));
        }

        // --- RESET flow: Password.ascx.vb cmdReset_Click, L237-L257 ----------------------------------
        // Legacy L239 seeds the answer with the empty string; L240 requires it only under the compound
        // guard "RequiresQuestionAndAnswer And Not IsAdmin"; L241 rejects a blank one. Gated on the
        // bound policy flag, which is false in the shipped configuration, so this rule never fires
        // there and an otherwise empty reset request is legal - exactly as the legacy handler behaved.
        RuleFor(request => request.PasswordAnswer)
            .NotEmpty().WithMessage(InvalidPasswordAnswerMessage)
            .When(request =>
                IsOperation(request, ChangePasswordRequest.OperationReset)
                && requiresQuestionAndAnswer);

        // --- CHANGE QUESTION AND ANSWER flow: cmdUpdateQA_Click, L319-L345 ---------------------------
        // All three legacy guards are unconditional WITHIN this flow - none has a privilege half - so
        // all three are asserted, scoped to the operation. The flow itself was configuration-gated in
        // the legacy screen at L187, so it is latent under the shipped configuration; it is carried
        // forward because it remains reachable under a different membership configuration and dropping a
        // reachable operation would breach functional parity.

        // Legacy L321: "If txtQAPassword.Text = """. The legacy outcome maps onto the generic
        // invalid-password status, whose wording is about password requirements rather than about an
        // omission; the measured PasswordMissing wording states the actual failure and is the
        // equivalent message the parity rule requires. Presence only - never the length policy, per
        // migration note 07.
        RuleFor(request => request.CurrentPassword)
            .NotEmpty().WithMessage(PasswordMissingMessage)
            .When(request =>
                IsOperation(request, ChangePasswordRequest.OperationChangeQuestionAndAnswer));

        // Legacy L326: "If txtEditQuestion.Text = """.
        RuleFor(request => request.NewPasswordQuestion)
            .NotEmpty().WithMessage(InvalidPasswordQuestionMessage)
            .When(request =>
                IsOperation(request, ChangePasswordRequest.OperationChangeQuestionAndAnswer));

        // Legacy L331: "If txtEditAnswer.Text = """.
        RuleFor(request => request.NewPasswordAnswer)
            .NotEmpty().WithMessage(InvalidPasswordAnswerMessage)
            .When(request =>
                IsOperation(request, ChangePasswordRequest.OperationChangeQuestionAndAnswer));
    }

    /// <summary>
    /// Reports whether the request declares the given operation, comparing ordinally against the
    /// constants published by <see cref="ChangePasswordRequest"/> so that the operation names are
    /// defined in exactly one place.
    /// </summary>
    /// <param name="request">The request whose declared operation is being tested.</param>
    /// <param name="operation">The operation constant to test against.</param>
    /// <returns><see langword="true"/> when the request declares that operation.</returns>
    private static bool IsOperation(ChangePasswordRequest request, string operation) =>
        string.Equals(request.Operation, operation, StringComparison.Ordinal);

    /// <summary>
    /// Reports whether a declared operation is one of the three the legacy screen supported. Matching is
    /// ordinal and exact, which keeps the wire contract deterministic and avoids any hidden
    /// normalisation of a value that selects between a credential change and a credential reset.
    /// </summary>
    /// <param name="operation">The declared operation, which may be absent.</param>
    /// <returns><see langword="true"/> when the operation is recognised.</returns>
    private static bool IsRecognisedOperation(string? operation) =>
        string.Equals(operation, ChangePasswordRequest.OperationChange, StringComparison.Ordinal)
        || string.Equals(operation, ChangePasswordRequest.OperationReset, StringComparison.Ordinal)
        || string.Equals(
            operation,
            ChangePasswordRequest.OperationChangeQuestionAndAnswer,
            StringComparison.Ordinal);
}
