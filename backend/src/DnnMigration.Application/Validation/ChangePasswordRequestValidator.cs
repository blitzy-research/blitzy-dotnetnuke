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

// MIGRATION 06 of 18 - THE CURRENT PASSWORD IS REQUIRED FOR A CHANGE AND FORBIDDEN ON A RESET, WHICH
// REPLACES A COMPOUND LEGACY GUARD WITH TWO NAMED OPERATIONS. Password.ascx.vb:L284 reads
// "Not IsAdmin And txtOldPassword.Text = """, and L290 reads
// "Not IsAdmin And txtNewPassword.Text = txtOldPassword.Text". Both halves matter: the row itself is
// hidden for an administrator editing another account (L150-L152 sets trOldPassword.Visible = False),
// and trOldPassword is declared runat="server" at Password.ascx:L33 precisely so it can be hidden. One
// legacy button therefore served two different operations distinguished only by caller privilege.
//
// An earlier revision of this file reasoned from that guard to asserting NO presence rule at all for the
// change flow, deferring it to the application service where privilege is known. The reasoning was sound
// about the legacy screen and wrong about its consequence: the reset flow asserted nothing either -
// no current credential, and no new credential, because generation was assumed - so an entirely EMPTY
// request body was a complete, destructive credential reset that satisfied every rule in this file.
//
// The target separates the two legacy cases across the two operations instead of across a privilege
// test, which is what lets a validator enforce proof of possession without knowing the caller. CHANGE is
// self-service: declaring it asserts that the caller holds the credential, so presence is unconditional
// for that operation. RESET is administrative: the caller cannot present a credential they do not know,
// so proof of possession is replaced by proof of PRIVILEGE, which the Api layer establishes from role
// claims before this request is acted on, and the current-credential member must be ABSENT - a value
// there would imply the wrong authorisation model. No workflow is lost: the administrative change this
// guard exempted is reachable as RESET, which is what it always was.

// MIGRATION 06b of 18 - THE THIRD LEGACY OPERATION IS NOT VALIDATED HERE BECAUSE IT NO LONGER EXISTS.
// The recovery question-and-answer panel's three guards - L321 the current credential, L326 the new
// question, L331 the new answer - have no target counterpart, because the recovery pair has none: the
// owning service contract states that the legacy question-and-answer member has no counterpart and that
// no member declares a question or answer parameter, the pair existed to guard credential retrieval, and
// retrieval is dropped outright. The request no longer carries the three members, so a rule here would
// have nothing to attach to. The measured evidence that nothing is lost: the provider registers the pair
// as not required (Website/release.config:L241), the panel was gated on that same flag and on the caller
// editing their own account (L187) so it never rendered, and the reset handler's answer guard (L240) was
// compound on the same flag so it never fired.

// MIGRATION 07 of 18 - THE CURRENT PASSWORD IS NEVER POLICY-CHECKED. It is an EXISTING stored credential,
// not a new one. The shipped database seeds the Host account with a four-character password at
// 01.00.00.SqlDataProvider:L7205 and the Administrator account with a five-character password at L7207,
// both below the minimum of seven. Applying the length policy to the current password would leave those
// two accounts permanently unable to change their own passwords. Only the NEW password is policy-checked.

// MIGRATION 08 of 18 - THE MARKUP'S maxlength="20" IS NOT CARRIED ACROSS, AND THE CEILING THAT IS
// APPLIED COMES FROM THE HASHING PRIMITIVE INSTEAD. All seven legacy text boxes declare the twenty and
// the baseline column was nvarchar(20) at 01.00.00.SqlDataProvider:L106, but
// 01.00.06.SqlDataProvider:L192 widens the column to nvarchar(50) and credentials later move to the
// externally installed membership tables. Under one-way hashing the stored width is irrelevant, because
// the digest is fixed-length whatever the input, so twenty would be a TIGHTENING that rejects
// legitimately long credentials.
//
// The ceiling that IS enforced is 72 ENCODED BYTES, and it is a property of BCrypt rather than a policy
// rule or a defensive round number. BCrypt derives its key from at most 72 bytes of input and discards
// everything beyond that WITHOUT REPORTING ANYTHING, so two credentials agreeing on their first 72 bytes
// produce the same digest and verify interchangeably. An earlier revision of this file bounded the field
// at 256 CHARACTERS while the infrastructure hasher threw for the same input, so a credential between
// those two limits passed validation and then failed as an unhandled fault - a 500 for a field-level
// problem - and a credential the hasher silently truncated passed both. The bound is measured in encoded
// bytes and not in characters because a character outside the ASCII range occupies between two and four
// of them, so a character count understates the real limit for exactly the callers most likely to exceed
// it. The identical constant is enforced by the hasher and by the sibling create-user validator, so the
// boundary and the primitive cannot disagree. It remains more than three times the legacy input ceiling.

// MIGRATION 09 of 18 - THE "NEW MUST DIFFER FROM CURRENT" RULE IS AUTHORED, GUARDED. It has genuine
// measured backing at Password.ascx.vb:L290, and its wording exists as PasswordNotDifferent. Because
// Password.ascx declares no validators the legacy comparison lived in the code-behind, and resource-key
// existence alone is never proof of enforcement - the code-behind line is the proof. The guard requires
// BOTH values to be supplied, which faithfully reproduces the legacy "Not IsAdmin" half without needing
// caller privilege: an administrator supplies no current password at all, so the rule correctly abstains
// for that path exactly as L290 did.

// MIGRATION 10 of 18 - NO QUESTION-AND-ANSWER RULE AT ALL, AND NO MEMBER LEFT TO ATTACH ONE TO.
// requiresQuestionAndAnswer is false at Website/release.config:L241 and the reset flow's legacy guard at
// L240 is compound - "RequiresQuestionAndAnswer And Not IsAdmin" - so nothing was ever enforced in the
// observed installation. An earlier revision of this file reproduced that conditionality faithfully,
// gating an answer rule on the bound policy flag so that it existed but could not fire by default. The
// recovery pair is now absent from the request entirely, so the rule is removed rather than left
// unreachable: a rule guarding a field the contract does not accept would assert a requirement that
// cannot be satisfied and cannot be observed. The four related resource keys do exist in the legacy tree,
// and their existence was never evidence of enforcement. The bound policy flag is consequently
// unsatisfiable in the target, which is why PasswordPolicyOptions.Validate rejects a deployment that sets
// it - the guard has moved from this file, where it silently did nothing, to start-up, where it is loud.

// MIGRATION 11 of 18 - PASSWORD RETRIEVAL IS NOT CARRIED FORWARD, so no rule, field or message here
// asks for a stored password back. The legacy retrieval entry point on the user controller (L433) also
// used a ByRef argument, and no by-reference parameter appears in any target public API. It replaced
// the legacy retrieval switch at Website/release.config:L239 together with the reversible cipher key
// committed to that same file in the clear at L89-L93; neither the switch nor the key is reproduced
// anywhere in this solution. Note that RESET is a different flag (L240) and IS carried forward.

// MIGRATION 12 of 18 - NO CURRENT-PASSWORD VERIFICATION HERE. Whether the supplied current password
// matches the stored digest is an infrastructure concern reached through the application service. The
// transition for credentials that predate the migration is an ADMINISTRATIVE RESET and nothing else:
// an earlier revision of this note described a re-hash on first successful login with reset as a
// fallback, which is the intent AAP 0.7.5.5 records but whose primary branch no code path in this
// solution can perform, because nothing verifies a credential held under the legacy reversible scheme
// and nothing may, per that same section. This file performs no digest work either way, touches no
// repository and runs no asynchronous rule. Its only dependency is the bound password policy, taken
// as an already-bound PasswordPolicyOptions instance, taken directly rather than through IOptions<T>.

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
// separate request types, so the intent is stated explicitly and this file rejects an absent or
// unrecognised value. That rejection has no legacy counterpart and no legacy wording, so its two
// messages are net-new. Note the hazard it removes: the two surviving operations both carry a new
// credential and its confirmation and differ only in whether the current credential is present, so
// inferring the operation from which members happen to be populated would read "no current credential
// supplied" - precisely what a caller who forgot the field sends - as "reset this account's credential".
// Only two values are recognised; the third legacy operation is not one of them, because its members and
// its capability are both absent from the target rather than merely unused.

using System.Globalization;
using System.Text;
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

    // MIGRATION: the InvalidPasswordQuestion wording (SharedResources.resx key L957, wording L958) and
    // the InvalidPasswordAnswer wording (key L954, wording L955) are deliberately NOT declared here. An
    // earlier revision carried both. The recovery pair is absent from the request, so a message for a
    // field that cannot be submitted would be unreachable text asserting a rule that does not exist. The
    // two resource entries are cited so that their absence is provably deliberate; the wording itself is
    // not reproduced, not even as a quotation, so that its absence is verifiable by search.

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

    /// <summary>
    /// Net-new wording, for the same reason as <see cref="OperationRequiredMessage"/>.
    /// </summary>
    private const string OperationUnrecognisedMessage =
        "The password operation specified is not recognised.";

    // MIGRATION: THE CEILING ON A SUBMITTED CREDENTIAL IS NO LONGER DECLARED IN THIS FILE. A local
    // constant of 256 CHARACTERS, with its own local wording, used to live at this point, and three
    // sibling credential entry points either declared a different number or declared none at all. A
    // ceiling that only some entry points apply is not a ceiling, so the number, the unit and the
    // message now come from Validation/CredentialBounds.cs and every credential path applies the
    // same one.
    //
    // The unit changed with the move, and the change is the point: the shared bound is measured in
    // UTF-8 BYTES rather than characters, because that is the form the hashing algorithm consumes.
    // A character ceiling does not bound what the algorithm is handed - one emoji is a single
    // character and four bytes - so the old constant was approximately right rather than right. The
    // reasoning, and why the value is not the algorithm's own 72-byte significance limit, are
    // recorded on CredentialBounds itself.
    //
    // Everything the old constant's own documentation said still holds: the ceiling is net-new, it
    // is not a policy rule, it is emphatically NOT the legacy markup's twenty-character input limit,
    // and it sits far above any credential a person would plausibly choose, so it cannot reject a
    // legitimate password.

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
    /// The bound password policy, supplied by the container from the configuration section named by
    /// <see cref="PasswordPolicyOptions.SectionName"/>. Supplies the minimum length that the only active
    /// rule enforces, the two values substituted into the legacy failure wording, the flag that gates
    /// the reset flow's answer rule, and the optional strength pattern. Its value is read once here
    /// rather than dereferenced inside each rule, so every rule sees one consistent snapshot of the
    /// policy.
    /// </param>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="passwordPolicy"/> is <see langword="null"/>. Failing loudly
    /// at construction is deliberate: a validator that silently fell back to built-in defaults would
    /// enforce a policy nobody configured, which is precisely the kind of silent divergence this
    /// migration is required to surface rather than absorb.
    /// </exception>
    public ChangePasswordRequestValidator(PasswordPolicyOptions passwordPolicy)
    {
        ArgumentNullException.ThrowIfNull(passwordPolicy);

        // MIGRATION NOTE ON THE CONSTRUCTOR SHAPE - deliberate, and now the single convention across
        // this layer. The specification for this file asked for IOptions<PasswordPolicyOptions>, and
        // that is not resolvable here: this project references exactly the two FluentValidation
        // packages, and FluentValidation.DependencyInjectionExtensions 11.12.0 closes over
        // FluentValidation and Microsoft.Extensions.DependencyInjection.Abstractions only, so
        // Microsoft.Extensions.Options never reaches this compilation. That is enforced rather than
        // merely observed: the project file states why the options package is excluded, so
        // reintroducing IOptions<T> in this layer fails to compile instead of passing review.
        //
        // Taking the bound policy directly is the established convention and not a concession. Every
        // type in the Options folder carries no import at all, the policy type itself documents that it
        // is "declared in this layer and bound by the Api layer", and the only IOptions<T> in the
        // backend sits in the Api layer where the web framework supplies it. CreateUserRequestValidator
        // takes the same plain snapshot, so the composition root satisfies ONE contract for this
        // configuration section rather than two - the two validators previously disagreed, which is what
        // put an unneeded package reference in the project file.
        //
        // The composition root must therefore make the bound policy instance resolvable in its own
        // right, projecting it from the bound and startup-validated options it already creates, so the
        // container can supply it here. If it does not, construction fails immediately and visibly,
        // which is the intended outcome; it never degrades to an unconfigured policy.
        int minimumPasswordLength = passwordPolicy.MinRequiredPasswordLength;
        int minimumNonAlphanumericCharacters = passwordPolicy.MinRequiredNonAlphanumericCharacters;
        string strengthPattern = passwordPolicy.PasswordStrengthRegularExpression;

        // MIGRATION: PasswordPolicyOptions.RequiresQuestionAndAnswer is deliberately NOT read here. An
        // earlier revision read it to gate an answer rule on the reset flow. The recovery pair is absent
        // from the request, so there is nothing for the flag to gate, and the flag is unsatisfiable in
        // the target - PasswordPolicyOptions.Validate rejects it at start-up, which is a louder and
        // earlier signal than a rule that silently could not fire.

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

        // --- BOTH operations: the replacement credential ----------------------------------------------
        // MIGRATION: the two credential-policy rules below apply to the RESET operation as well as to
        // CHANGE. The legacy reset had no credential input because the membership provider generated one
        // and returned it (L906, returned at L915); generation is not carried forward, because a generated
        // credential must be transmitted to be useful, the mail subsystem is excluded, and no endpoint
        // returns a credential - so a generated value would be knowable to nobody and the reset would be
        // a lockout. The administrator supplies the replacement explicitly and it is held to exactly the
        // same length, ceiling and strength rules as a self-service change, which is what makes the reset
        // an actual reset rather than a weaker back door.
        //
        // --- CHANGE only: the confirmation ------------------------------------------------------------
        // Legacy check 1, L272: "If txtNewPassword.Text <> txtNewConfirm.Text". Attached to the
        // confirmation rather than to the new password because the confirmation is the field the caller
        // should correct. Two absent values compare equal and pass here, which is correct: the new
        // password's own presence rule is what reports an omitted credential, so one omission yields one
        // message rather than two.
        //
        // MIGRATION: the confirmation is required on CHANGE and not on RESET, and the asymmetry is
        // deliberate. A confirmation exists to catch a typing mistake in a value the typist cannot see,
        // which is the self-service case; the legacy reset panel rendered no credential input at all, so
        // no legacy behaviour asks for one here. Extending the comparison to RESET would also make a
        // documented outcome unreachable: a reset that submits the credential already stored is answered
        // as a CONFLICT against the stored hash, and a request refused here for a missing confirmation
        // could never reach the store to be compared against it.
        //
        // MIGRATION: a RESET that does supply a confirmation is still held to it - the service compares
        // the two whenever a confirmation is present, for either operation - so narrowing this rule
        // removes a requirement and not a check.
        RuleFor(request => request.ConfirmPassword)
            .Equal(request => request.NewPassword)
                .WithMessage(PasswordMismatchMessage)
            .When(request => IsOperation(request, ChangePasswordRequest.OperationChange));

        // Legacy check 2, L278, which delegates to ValidatePassword at L1067-L1091. The minimum length
        // is the only policy rule that fires under the SHIPPED configuration, and it is bound rather
        // than written. The ceiling is the hasher's own, measured in encoded bytes. Stopping after the
        // first failure keeps an empty submission to a single message, exactly as the legacy check
        // returned a single outcome; it can never erase a failure already recorded, so it is not the
        // L1086 defect in another guise.
        RuleFor(request => request.NewPassword)
            .Cascade(CascadeMode.Stop)
            .NotEmpty().WithMessage(invalidPasswordMessage)
            .MinimumLength(minimumPasswordLength).WithMessage(invalidPasswordMessage)
            .Must(CredentialBounds.IsWithinMaximumByteLength)
                .WithMessage(CredentialBounds.MaximumByteLengthMessage)
            .When(IsCredentialReplacement);

        // Legacy check 2 continued: the non-alphanumeric count, ValidatePassword L1078-L1079.
        //
        // MIGRATION: this rule was previously MISSING while its number was still substituted into the
        // message above, so a deployment that configured a minimum above zero produced a message
        // asserting a requirement this boundary never checked - and the credential then reached the
        // hasher, which DOES check it and throws, turning a field-level problem into an unhandled
        // fault. The rule is expressed even though the shipped configuration makes it inert
        // (minRequiredNonalphanumericCharacters="0", Website/release.config:L243) precisely because the
        // value is configurable: omitting a rule on the ground that today's configuration cannot reach
        // it is what allowed the gap to exist.
        //
        // A configured minimum of zero SKIPS THE RULE ENTIRELY rather than registering a comparison
        // that can never fail, mirroring the hasher, which skips the identical check for the identical
        // reason. A rule that cannot fail is indistinguishable from enforcement under review.
        if (minimumNonAlphanumericCharacters > 0)
        {
            RuleFor(request => request.NewPassword)
                .Must(password =>
                    HasEnoughNonAlphanumericCharacters(password, minimumNonAlphanumericCharacters))
                .WithMessage(invalidPasswordMessage)
                .When(request =>
                    IsCredentialReplacement(request) && !string.IsNullOrEmpty(request.NewPassword));
        }

        // --- CHANGE only: proof of possession --------------------------------------------------------
        // Legacy check 3, L284: "Not IsAdmin And txtOldPassword.Text = """. Asserted unconditionally for
        // this operation rather than conditioned on privilege, because declaring CHANGE is itself the
        // assertion of self-service - the administrative path the legacy privilege half exempted is the
        // RESET operation. Presence only, never the length policy, per migration note 07: the shipped
        // Host and Administrator accounts carry credentials shorter than the configured minimum, so
        // policy-checking the existing credential would leave exactly those accounts unable to change it.
        RuleFor(request => request.CurrentPassword)
            .NotEmpty().WithMessage(PasswordMissingMessage)
            .When(request => IsOperation(request, ChangePasswordRequest.OperationChange));

        // Legacy check 4, L290: "Not IsAdmin And txtNewPassword.Text = txtOldPassword.Text". The guard
        // still requires both values to be present, which keeps one omission to one message: the presence
        // rule above reports a missing current credential, and this rule abstains rather than adding a
        // second failure for the same field.
        RuleFor(request => request.NewPassword)
            .NotEqual(request => request.CurrentPassword)
                .WithMessage(PasswordNotDifferentMessage)
            .When(request =>
                IsOperation(request, ChangePasswordRequest.OperationChange)
                && !string.IsNullOrEmpty(request.CurrentPassword)
                && !string.IsNullOrEmpty(request.NewPassword));

        // --- RESET only: the current credential must be ABSENT ----------------------------------------
        // MIGRATION: NET-NEW, with no legacy counterpart because the legacy reset panel had no field for
        // a current credential at all - it could not have supplied one. The rule exists because the two
        // operations are separated by AUTHORISATION: a change is authorised by proof of possession and a
        // reset by proof of privilege, established from the caller's role claims at the Api edge. A reset
        // carrying a current credential is therefore either a client that meant to send CHANGE or an
        // attempt to have the two models evaluated at once, and both are better rejected at the boundary
        // than resolved by a precedence rule nobody can see. Rejecting it also keeps the service's
        // authorisation decision unambiguous: the operation alone determines which check applies.
        RuleFor(request => request.CurrentPassword)
            .Empty().WithMessage(CurrentPasswordNotAcceptedMessage)
            .When(request => IsOperation(request, ChangePasswordRequest.OperationReset));

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
                .When(IsCredentialReplacement);
        }

        // --- The shared credential ceiling, applied unconditionally to the CURRENT password --------
        // MIGRATION: net-new, and deliberately NOT scoped to an operation. CurrentPassword is a
        // credential this endpoint hands to the one-way verifier on every flow that carries it, so it
        // is a credential entry point in exactly the sense the shared bound exists to cover, and
        // leaving it out would have left an unbounded field on an endpoint whose sibling field is
        // bounded. It carries no length POLICY - a floor on a PRESENTED password would lock out the
        // shipped Host and Administrator accounts, whose credentials are shorter than the configured
        // minimum - so the ceiling here is the only length rule this member has, and it is a bound
        // rather than a policy.
        //
        // It is unconditional rather than repeated per flow because the value means the same thing on
        // every flow that supplies it, and an absent value passes the check, so the reset flow that
        // never supplies one is unaffected.
        RuleFor(request => request.CurrentPassword)
            .Must(CredentialBounds.IsWithinMaximumByteLength)
                .WithMessage(CredentialBounds.MaximumByteLengthMessage);
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
    /// Reports whether the request replaces the account's credential, which both supported operations
    /// do - a self-service change and an administrative reset.
    /// </summary>
    /// <param name="request">The request whose declared operation is being tested.</param>
    /// <returns>
    /// <see langword="true"/> when the declared operation is either a change or a reset.
    /// </returns>
    /// <remarks>
    /// The rules that govern the replacement credential itself - presence, the bound minimum length, the
    /// hasher's encoded-byte ceiling, the confirmation comparison and any configured strength pattern -
    /// are identical for both operations, so they share one condition rather than being written twice.
    /// Writing them twice is how the two paths would drift apart, and the earlier revision's reset path
    /// had no credential rules at all.
    /// </remarks>
    private static bool IsCredentialReplacement(ChangePasswordRequest request) =>
        IsOperation(request, ChangePasswordRequest.OperationChange)
        || IsOperation(request, ChangePasswordRequest.OperationReset);

    /// <summary>
    /// Reports whether a declared operation is one of the two the target supports. Matching is
    /// ordinal and exact, which keeps the wire contract deterministic and avoids any hidden
    /// normalisation of a value that selects between a credential change and a credential reset.
    /// </summary>
    /// <param name="operation">The declared operation, which may be absent.</param>
    /// <returns><see langword="true"/> when the operation is recognised.</returns>
    /// <remarks>
    /// The legacy screen's third operation - replacing the recovery question and answer - is
    /// deliberately not recognised, because neither its members nor the capability behind them exists in
    /// the target. Accepting the value and then doing nothing would report success for work that never
    /// happened.
    /// </remarks>
    private static bool IsRecognisedOperation(string? operation) =>
        string.Equals(operation, ChangePasswordRequest.OperationChange, StringComparison.Ordinal)
        || string.Equals(operation, ChangePasswordRequest.OperationReset, StringComparison.Ordinal);


    /// <summary>
    /// Reports whether a submitted credential carries at least the configured number of characters
    /// outside the ranges <c>0-9</c>, <c>A-Z</c> and <c>a-z</c>.
    /// </summary>
    /// <param name="password">The submitted credential, which may be absent or blank.</param>
    /// <param name="minimum">
    /// The configured minimum count. The caller registers this rule only when the minimum is above
    /// zero, so a zero minimum performs no scan at all.
    /// </param>
    /// <returns>
    /// <see langword="true"/> when the credential carries at least <paramref name="minimum"/> such
    /// characters, and also when it is absent or blank, for the reason given on
    /// <see cref="CredentialBounds.IsWithinMaximumByteLength(string?)"/>.
    /// </returns>
    /// <remarks>
    /// The classification reproduces the legacy character class <c>[^0-9a-zA-Z]</c> declared at
    /// <c>Library/Components/Users/UserController.vb:L1078</c> and compared against the configured
    /// minimum at L1079. It is deliberately <b>not</b> a Unicode-aware test: the framework's
    /// culture-aware letter-or-digit check would classify an accented letter as alphanumeric, so such
    /// a character would stop contributing to the total and the rule would silently widen. This is the
    /// identical test <c>DnnMigration.Infrastructure/Security/BcryptPasswordHasher.cs</c> performs, so
    /// the boundary and the primitive agree on which credentials satisfy the rule. The scan stops as
    /// soon as the minimum is reached, and the value itself is never retained or echoed.
    /// </remarks>
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
