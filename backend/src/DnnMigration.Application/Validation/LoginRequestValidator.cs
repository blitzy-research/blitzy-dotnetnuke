using DnnMigration.Application.Dtos.Auth;
using FluentValidation;

namespace DnnMigration.Application.Validation;

/// <summary>
/// Declares the field rules for <see cref="LoginRequest"/>, the credential
/// payload submitted to <c>POST /api/v1/auth/login</c>.
/// </summary>
/// <remarks>
/// <para>
/// This validator inspects the <b>shape</b> of a sign-in submission and nothing
/// beyond it. It performs no lookup, compares nothing against the one-way
/// password hash held in the Infrastructure layer, and reaches no store, so it
/// takes no dependency and its constructor is parameterless. Whether a
/// submission actually authenticates is decided by the sign-in service's
/// <c>LoginAsync</c> operation and reported in its return value as a domain
/// result. An authentication failure is a different condition from the
/// malformed-request failures declared here, and the two must not be conflated.
/// </para>
/// <para>
/// <b>The verification code is conditionally optional rather than merely
/// nullable.</b> The legacy screen collected it across TWO round trips:
/// <c>Website/DesktopModules/AuthenticationServices/DNN/Login.ascx</c> ships
/// both of its rows with <c>visible="false"</c> (L12 and L15), and
/// <c>Login.ascx.vb</c> reveals them only at L171-L174, on the branch entered at
/// L168 when a first attempt reported the account not yet approved and, at L170,
/// the tenant registers its users by verification. A first submission therefore
/// cannot carry a code, so a rule demanding one unconditionally would reject
/// sign-ins the legacy application accepted. The rule below is guarded so that
/// an absent code and an empty one behave identically.
/// </para>
/// <para>
/// Two legacy inputs are deliberately absent from the contract this validator
/// guards, and no rule here mentions either: the image-based human-verification
/// challenge that <c>Login.ascx</c> declared at L22 and that
/// <c>Login.ascx.vb</c> gated the entire sign-in on at L162, and the fixed
/// authentication-mechanism literal that <c>Login.ascx.vb</c> supplied at L164
/// and L191. Both omissions are annotated at the foot of the constructor.
/// </para>
/// <para>
/// Messages are structural only. A message may state that a field is required;
/// none may suggest whether an account exists, because resisting account
/// enumeration is the reason the sign-in service answers every rejected
/// credential the same way. Nothing from the request is written to any sink:
/// the payload carries a clear-text credential, and this type neither records
/// nor reformats it. Turning the failures declared here into an RFC 7807
/// response belongs to the Api layer, not to this type.
/// </para>
/// </remarks>
public class LoginRequestValidator : AbstractValidator<LoginRequest>
{
    /// <summary>
    /// Upper bound applied to <see cref="LoginRequest.Username"/>, taken from
    /// the terminal width of the stored sign-in-name column rather than from the
    /// first width the schema history mentions.
    /// </summary>
    private const int UsernameMaximumLength = 256;

    /// <summary>
    /// Initialises a new instance of the <see cref="LoginRequestValidator"/>
    /// class and declares its rules.
    /// </summary>
    /// <remarks>
    /// Parameterless by design. A rule that needed a collaborator would be
    /// asking a question about state, and questions about state belong to the
    /// sign-in service rather than to request validation.
    /// </remarks>
    public LoginRequestValidator()
    {
        // MIGRATION: Requiring a sign-in name is NET-NEW, and it is a
        // TIGHTENING. The legacy screen validated nothing at all: a census of
        // Website/DesktopModules/AuthenticationServices/DNN/Login.ascx -- 37
        // lines in total -- finds zero declarative validators of any kind, and
        // the only emptiness test in the 204-line code-behind sits at L126,
        // inside Page_Load, where it merely chooses which box receives focus.
        // L164 then called the legacy credential check with whatever the boxes
        // happened to hold. An empty sign-in name therefore used to travel to
        // the store and return as an authentication failure; here it is refused
        // as a malformed request, so the observable status for that input
        // changes from 401 to 400. The change is deliberate rather than
        // incidental: an empty name can never match a stored one, refusing it
        // here removes a pointless round trip, and it narrows the surface
        // available for credential guessing.
        //
        // MIGRATION: The ceiling is NET-NEW as well -- the markup sets no length
        // limit on the sign-in box at L10 -- and its value is taken from the
        // TERMINAL schema, not from the first script that mentions the column.
        // 01.00.06.SqlDataProvider:L197 introduces the column at 100
        // characters, but 03.02.03.SqlDataProvider:L2185 and L2384 and then
        // 04.00.04.SqlDataProvider:L2253 and L2451 widen it to 256, and the
        // externally installed membership table agrees at InstallCommon.sql:L169
        // with the same 256. Bounding at 100 would invent a restriction 156
        // characters tighter than the schema permits and would refuse names the
        // store is able to hold, so the terminal 256 is the only defensible
        // figure. A longer name cannot exist in either store, so rejecting it
        // here can never turn a successful sign-in into a failing one.
        RuleFor(request => request.Username)
            .NotEmpty()
            .WithMessage("A username is required.")
            .MaximumLength(UsernameMaximumLength)
            .WithMessage("A username cannot be longer than {MaxLength} characters.");

        // MIGRATION: Requiring a password is NET-NEW and a TIGHTENING for the
        // same reason, and with the same consequence, as the sign-in name
        // above: L164 applied no guard, so an empty password used to yield an
        // authentication failure and now yields a malformed-request failure,
        // 401 becoming 400.
        //
        // MIGRATION: There is deliberately NO lower bound on the password here,
        // and introducing one later would be a defect rather than hardening.
        // The shipped database seeds two accounts whose passwords are shorter
        // than the length the membership configuration asks for:
        // 01.00.00.SqlDataProvider:L7205 seeds the Host account with a
        // four-character password and L7207 seeds the Administrator account
        // with a five-character one, while Website/release.config:L242 asks for
        // seven. A length floor on the sign-in path would therefore stop the two
        // accounts that ship with every installation from ever signing in. A
        // length floor is a rule about a password being SET, not about one being
        // PRESENTED, so it belongs to the create-user and change-password
        // validators and to the password-policy option type under
        // Application/Options.
        //
        // MIGRATION: There is deliberately no upper bound on the password
        // either. The legacy column grew from 20 characters at
        // 01.00.00.SqlDataProvider:L104 to 50 at 01.00.06.SqlDataProvider:L192
        // and then stopped holding the credential at all once it moved to the
        // externally installed membership tables, so no measured limit on a
        // SUBMITTED password exists anywhere in the sources. LoginRequest
        // already records that same decision on its Password member, for that
        // same reason, and the two must not disagree. Bounding the size of a
        // request body is a transport concern the Api host already covers.
        RuleFor(request => request.Password)
            .NotEmpty()
            .WithMessage("A password is required.");

        // MIGRATION: The verification code stays optional, reproducing the
        // two-round-trip reveal at Login.ascx.vb:L168-L185, and an absent code
        // is treated as identical to an empty one. The legacy branch at L177
        // tested the box against the empty string -- an empty-STRING test, not a
        // null test -- because the legacy null-string sentinel at
        // Library/Components/Shared/Null.vb:L71-L75 returns the empty string
        // rather than a null reference, so the original contract could not
        // distinguish "absent" from "empty" at all. The guard below preserves
        // that exactly: when the member is null or empty the rule does not run,
        // so neither form can fail a check that the other passes. What the guard
        // does admit is the whitespace-only residue, which the built-in check
        // then refuses, because FluentValidation counts a whitespace-only string
        // as empty. Legacy would have sent such a value to the store and
        // answered "InvalidCode"; here it is a malformed request, the same
        // 401-to-400 shift as above.
        //
        // MIGRATION: No format, length or pattern rule is applied to the code,
        // because there is none to reproduce. The legacy path passed it
        // untouched through Library/Components/Users/UserController.vb:L1136
        // into the membership provider, and no column for it appears anywhere in
        // the 88-script schema chain, so any pattern asserted here would be
        // invented rather than migrated.
        RuleFor(request => request.VerificationCode)
            .NotEmpty()
            .WithMessage("A verification code, when supplied, must contain at least one non-whitespace character.")
            .When(request => !string.IsNullOrEmpty(request.VerificationCode));

        // --------------------------------------------------------------------
        // Deliberate divergences that carry no rule. Recorded here under Rule T5
        // so that each absence reads as a documented decision rather than as an
        // omission, and cited by file and line wherever naming the thing would
        // defeat the point of having left it behind.
        // --------------------------------------------------------------------

        // MIGRATION: The image-based human-verification challenge is dropped, so
        // no rule guards it and the contract carries no member for it.
        // Login.ascx declared that control at L22, inside the two rows opened at
        // L18 and L21; Login.ascx.vb toggled those rows from a per-tenant switch
        // at L137-L138 and, at L162, gated the ENTIRE sign-in on the control
        // reporting itself valid. The control belongs to Library/Controls, a tree
        // this migration excludes wholesale -- 102 files across ten control
        // sub-libraries -- so there is nothing for a rule to bind to. This is a
        // DELIBERATE FUNCTIONAL REDUCTION, not an oversight. The named
        // compensating control is request rate limiting on the sign-in
        // endpoint, configured in the Api layer and applied to its
        // authentication controller.

        // MIGRATION: The fixed authentication-mechanism literal is dropped, so
        // neither a rule nor a member carries it. Login.ascx.vb supplied it
        // twice -- as the fourth argument of the legacy credential check at
        // L164, and again at L191 when building the authenticated-event
        // arguments -- because the legacy platform multiplexed several pluggable
        // sign-in mechanisms behind a single screen. The target has exactly one
        // path, JWT bearer tokens, so such a discriminator could only ever hold
        // one value; it is neither accepted from the caller nor inferred.

        // MIGRATION: The message selection at Login.ascx.vb:L175-L184 --
        // "EnterCode", "InvalidCode" and "UserNotAuthorized" -- is NOT
        // reproduced here. Every one of those outcomes depends on the legacy
        // sign-in status returned at L164 and on the tenant registration mode
        // read at L170, and a validator may see neither: Rule T2 keeps workflow
        // above this layer, and Rule T3 keeps persistence away from it. They
        // become failure reasons on the domain result that the sign-in service
        // returns, and the legacy resource wording stays the authority for their
        // text.

        // MIGRATION: The status output parameter that terminated the legacy
        // signature at Login.ascx.vb:L164, and the same idiom on
        // Library/Components/Users/UserController.vb:L433, are retired rather
        // than translated. The sign-in service reports its outcome in its return
        // value instead, because no by-reference or output parameter appears in
        // any public API of the target.

        // MIGRATION: The legacy code-behind compiled with strict type checking
        // disabled -- Website/release.config:L125 sets strict to false, whereas
        // the class library at Library/DotNetNuke.Library.vbproj:L24 enables it
        // -- so it was free to narrow types implicitly and to bind members late.
        // None of that is carried across. Every rule above reads a
        // strongly-typed member of LoginRequest and performs no conversion, no
        // coercion, no trimming and no reformatting, so there is no implicit
        // narrowing here whose result could differ from the original.
    }
}
