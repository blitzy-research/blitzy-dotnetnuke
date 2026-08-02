// MIGRATION: this file is the declarative replacement for the eight
// asp:RequiredFieldValidator controls declared on the legacy portal signup
// screen, Website/admin/Portal/signup.ascx (115 lines), together with the
// character-set guard clause its code-behind ran on postback at
// Website/admin/Portal/Signup.ascx.vb:L191-L216. That screen posted to the
// fifteen-positional-parameter PortalController.CreatePortal declared at
// Library/Components/Portal/PortalController.vb:L980, whose parameter list is
// now the CreatePortalRequest contract validated here.
//
// MIGRATION: the legacy screen declares EIGHT validators and every one of them
// is a RequiredFieldValidator. A case-insensitive census of signup.ascx returns
// zero asp:RegularExpressionValidator and zero asp:CompareValidator matches, so
// no format pattern and no cross-field comparison is reproduced below, and none
// is invented to fill the gap.
//
// MIGRATION: each of those eight messages is ALSO declared in the screen's own
// resource file, and every resource value is the inline attribute text prefixed
// with a literal <br> element. Website/admin/Portal/App_LocalResources/
// Signup.ascx.resx:L144-L145 holds "<br>Portal Name Is Required." for
// valPortalName.ErrorMessage, and the markup carries a matching resourcekey
// attribute, so the resource value is what the browser rendered. The wording
// below reproduces those strings character for character but omits the <br>:
// they now travel as text in the errors member of an RFC 7807 problem document,
// and embedding presentation markup in a machine-readable contract would be a
// defect rather than fidelity.
//
// MIGRATION: the legacy per-character guard loop appended its message once for
// EVERY offending character (Signup.ascx.vb:L193 and L214 both use "&=" inside a
// For loop), so a three-character violation rendered the same sentence three
// times. One failure is reported per rule here. The rule that fires is
// identical; only the duplication, which was an artefact of concatenating into
// an HTML string, is not carried forward.

using System.Globalization;
using System.Text.RegularExpressions;
using DnnMigration.Application.Dtos.Portal;
using DnnMigration.Application.Options;
using DnnMigration.Domain.ValueObjects;
using FluentValidation;

namespace DnnMigration.Application.Validation;

/// <summary>
/// Validates the inbound <see cref="CreatePortalRequest"/> contract for
/// <c>POST /api/v1/portals</c>, reproducing the rule set measured on the legacy
/// portal signup screen exactly.
/// </summary>
/// <remarks>
/// <para>
/// <b>Provenance.</b> Every rule below traces to a control on
/// <c>Website/admin/Portal/signup.ascx</c> or to a guard clause in
/// <c>Website/admin/Portal/Signup.ascx.vb</c>, and every message is the wording
/// that screen displayed. Nothing here is inferred from the shape of the request
/// or from modern convention: where the legacy screen imposed no rule, this type
/// imposes none, and the omission is recorded next to the property it concerns.
/// </para>
/// <para>
/// <b>Name lengths follow the SCHEMA, not the markup.</b> The administrator's
/// given and family names are capped at 50 characters even though the legacy
/// text boxes allowed 100. The backing columns are <c>nvarchar(50)</c> in the
/// terminal schema — the <c>Tmp_Users</c> definition at
/// <c>Website/Providers/DataProviders/SqlDataProvider/01.00.06.SqlDataProvider</c>
/// L182-L197, with the two columns at L185 and L186 — and a case-insensitive
/// search for an <c>ALTER COLUMN</c> touching either name across all eighty-eight
/// upgrade scripts finds nothing, so 50 is terminal and was never widened. The
/// wider figure appears only on stored-procedure parameters. Accepting 100 would
/// admit a request the database then truncates or rejects, so the schema wins.
/// </para>
/// <para>
/// <b>Portal fee and quota values are deliberately not validated.</b> The legacy
/// code clamps a negative fee to zero rather than rejecting it, and the object it
/// clamps is a role rather than a portal:
/// <c>Library/Components/Portal/PortalController.vb:L395</c> and L398 assign
/// <c>ServiceFee</c> and <c>TrialFee</c> on a <c>RoleInfo</c> instance created at
/// L390 while a portal template installs its roles. Rejecting a negative value
/// here would convert a silent coercion into a failed request, which is a
/// behavioural change. The clamp therefore stays in the service layer, and no
/// lower-bound rule on any fee or quota appears below.
/// </para>
/// <para>
/// <b>What this type deliberately cannot do.</b> It performs no input or output
/// of any kind: no uniqueness probe, no template lookup on disk, no tenant
/// resolution. Those need a repository or the file system, both of which belong
/// to layers this one does not reference. It shapes no HTTP response either —
/// translating a failure into an RFC 7807 problem document is the Api layer's
/// filter, not this validator's business. And it never writes a diagnostic
/// record of any kind, because the request it inspects carries a plaintext
/// password.
/// </para>
/// <para>
/// <b>Dependencies.</b> Exactly one: the bound password policy, taken as an
/// already-validated snapshot. Every other rule is a shape or wording rule
/// measured on the legacy screen and needs no configuration, no clock, no
/// repository and no file-system access, and none of those is acquired. The
/// policy is required because this request carries the initial administrator's
/// credential, which reaches the same credential hasher the user-creation and
/// password-change contracts reach; a boundary that accepted a credential that
/// hasher rejects would turn a field-level problem into an unhandled fault. The
/// dependency shape deliberately matches
/// <see cref="CreateUserRequestValidator"/> and
/// <see cref="ChangePasswordRequestValidator"/>, so the composition root
/// satisfies one contract for one configuration section rather than three.
/// Instances are discovered by the Application layer's assembly scan, which
/// registers publicly visible validator types only — hence the accessibility of
/// this class — so no registration is declared here.
/// </para>
/// </remarks>
public class CreatePortalRequestValidator : AbstractValidator<CreatePortalRequest>
{
    /// <summary>
    /// Message of the <c>valPortalName</c> required-field validator
    /// (<c>signup.ascx:L40-L41</c>), reproduced verbatim including its trailing
    /// full stop.
    /// </summary>
    private const string PortalAliasRequiredMessage = "Portal Name Is Required.";

    /// <summary>
    /// Message of the <c>valTemplate</c> required-field validator
    /// (<c>signup.ascx:L67-L68</c>), reproduced verbatim. It is the one message
    /// on the screen that carries no trailing full stop, and that is preserved.
    /// </summary>
    private const string TemplateRequiredMessage = "Please select a template file";

    /// <summary>
    /// Message of the <c>valFirstName</c> required-field validator
    /// (<c>signup.ascx:L79-L80</c>), reproduced verbatim.
    /// </summary>
    private const string FirstNameRequiredMessage = "First Name Is Required.";

    /// <summary>
    /// Message of the <c>valLastName</c> required-field validator
    /// (<c>signup.ascx:L84-L85</c>), reproduced verbatim.
    /// </summary>
    private const string LastNameRequiredMessage = "Last Name Is Required.";

    /// <summary>
    /// Message of the <c>valUsername</c> required-field validator
    /// (<c>signup.ascx:L89-L90</c>), reproduced verbatim.
    /// </summary>
    private const string UsernameRequiredMessage = "Username Is Required.";

    /// <summary>
    /// Message of the <c>valPassword</c> required-field validator
    /// (<c>signup.ascx:L95-L96</c>), reproduced verbatim.
    /// </summary>
    private const string PasswordRequiredMessage = "Password Is Required.";

    // MIGRATION: two DIFFERENT legacy resources share the key InvalidPassword.Text, and the one this
    // screen used is not the one that describes a policy failure. The screen's own local entry,
    // Website/admin/Portal/App_LocalResources/Signup.ascx.resx:L237-L238, reads "The Password Values
    // Entered Do Not Match." and is raised at Signup.ascx.vb:L221 for the confirmation mismatch and
    // for nothing else - the mismatch was the ONLY password check this screen performed. The global
    // entry at Website/App_GlobalResources/SharedResources.resx:L285-L286 is the tokenised policy
    // wording the membership path produced. The policy wording is the correct one to attach here,
    // because the condition being reported is the policy failure rather than a mismatch, and it is
    // deliberately the same text the create-user and change-password validators attach to the
    // identical condition: one condition, one wording, three boundaries. Each of the three cites the
    // same resource line, so a divergence between them is detectable by searching for the citation.
    //
    // MIGRATION: the confirmation field itself has no counterpart on this contract, so no mismatch
    // rule appears below and the local wording is deliberately not reproduced. The reasoning is
    // recorded on the request type, next to the member that would have carried it.

    /// <summary>
    /// Legacy wording for a credential that fails the bound policy, from
    /// <c>Website/App_GlobalResources/SharedResources.resx:L285-L286</c>. The two bracketed
    /// tokens are substituted from the bound policy at construction time, exactly as the
    /// legacy membership path substituted them at run time.
    /// </summary>
    private const string InvalidPasswordMessageTemplate =
        "The password specified is invalid.  Please specify a valid password.  Passwords must be at "
        + "least [PasswordLength] characters in length and contain at least [NoneAlphabet] "
        + "non-alphanumeric characters.";

    /// <summary>
    /// The token the legacy code replaced with the configured minimum password length.
    /// </summary>
    private const string PasswordLengthToken = "[PasswordLength]";

    /// <summary>
    /// The token the legacy code replaced with the configured minimum count of
    /// non-alphanumeric characters.
    /// </summary>
    private const string NoneAlphabetToken = "[NoneAlphabet]";


    /// <summary>
    /// Upper bound on the time any single strength-pattern evaluation may consume, so that a
    /// pathological input cannot hold a request thread. The value matches the sibling
    /// credential validators.
    /// </summary>
    private static readonly TimeSpan PatternMatchTimeout = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// Message of the <c>valEmail</c> required-field validator
    /// (<c>signup.ascx:L106-L107</c>), reproduced verbatim.
    /// </summary>
    private const string EmailRequiredMessage = "Email Is Required.";

    /// <summary>
    /// Wording for a malformed mail address, taken verbatim from the shared,
    /// application-wide <c>InvalidEmail.Text</c> entry at
    /// <c>Website/App_GlobalResources/SharedResources.resx:L282</c>, including its
    /// two spaces after the sentence period.
    /// </summary>
    /// <remarks>
    /// MIGRATION: the spacing in that resource file is inconsistent per key and is
    /// therefore never normalised in bulk. L276, L282, L291 and L294 use two spaces
    /// after a sentence period while L297 and L300 use one, and L300 additionally
    /// carries the long-standing spelling of "Futher". This key is reproduced exactly
    /// as measured. The screen itself carried no wording for a malformed address,
    /// because it enforced no format; the shared entry is used because it is the
    /// wording the rest of the application already emits for this condition.
    /// </remarks>
    private const string InvalidEmailMessage =
        "The email address specified is invalid.  Please specify a valid email address.";

    /// <summary>
    /// The <c>InvalidName</c> message the signup code-behind raised when the
    /// alias contained a character it did not permit, taken verbatim from
    /// <c>Website/admin/Portal/App_LocalResources/Signup.ascx.resx:L234-L235</c>.
    /// </summary>
    private const string InvalidAliasCharacterMessage =
        "The Portal Name Must Not Contain Spaces Or Punctuation.";

    /// <summary>
    /// Message for the bare-file-name rule on the template selection. The legacy
    /// screen had no equivalent wording because its drop-down list made a path
    /// impossible to submit; see the note above that rule.
    /// </summary>
    private const string TemplateFileNameOnlyMessage =
        "Template must be a file name and must not contain a directory path.";

    /// <summary>
    /// Message for the contained-relative-directory rule on the home directory. This is the
    /// legacy screen's own wording, reproduced verbatim from
    /// <c>Website/admin/Portal/App_LocalResources/Signup.ascx.resx:L288-L289</c>, so a caller
    /// submitting an unusable folder sees the text the legacy application showed. What changes is
    /// when the refusal happens, not what it says: the legacy screen reported it after resolving
    /// the folder against the file system (<c>Signup.ascx.vb:L251-L256</c>), whereas this rule
    /// reaches the same verdict from the shape of the value alone.
    /// </summary>
    private const string InvalidHomeFolderMessage =
        "The Home Folder you specified is not valid.";

    /// <summary>
    /// Maximum length of the portal title, which the markup and the schema agree
    /// on: the <c>txtTitle</c> text box declares <c>maxlength</c> 128
    /// (<c>signup.ascx:L52</c>) and the column is <c>nvarchar(128)</c>
    /// (<c>01.00.00.SqlDataProvider:L79</c>, preserved by the table rebuild at
    /// <c>01.00.05.SqlDataProvider:L1364</c>).
    /// </summary>
    private const int PortalNameMaximumLength = 128;

    /// <summary>
    /// Maximum length of the portal alias, taken from the <c>maxlength</c> 128 on
    /// the <c>txtPortalName</c> text box (<c>signup.ascx:L40</c>). The column is
    /// wider at <c>nvarchar(200)</c> (<c>01.00.00.SqlDataProvider:L78</c>), so the
    /// measured markup limit is the binding one.
    /// </summary>
    private const int PortalAliasMaximumLength = 128;

    /// <summary>
    /// Maximum length shared by the description and keywords fields:
    /// <c>maxlength</c> 500 on both text boxes (<c>signup.ascx:L56</c> and L61)
    /// and <c>nvarchar(500)</c> on both columns
    /// (<c>01.00.02.SqlDataProvider:L884-L885</c>).
    /// </summary>
    private const int MetadataMaximumLength = 500;

    // MIGRATION: the home-directory constants and the traversal predicate that used to sit here now
    // live in Validation/PortalHomeDirectoryRules.cs, because the portal UPDATE contract carries the
    // same field and needs the identical defence. Two copies of a path-traversal rule can be tightened
    // in one place and left alone in the other, and the weaker copy then defines the application's
    // actual behaviour, so there is one definition and two callers.


    /// <summary>
    /// Maximum length of the administrator's given and family names. This is the
    /// schema width of 50 rather than the markup's 100; the reasoning is on the
    /// type.
    /// </summary>
    private const int PersonNameMaximumLength = 50;

    /// <summary>
    /// Maximum length of the administrator's sign-in name:
    /// <c>Username nvarchar(100) NOT NULL</c>
    /// (<c>01.00.06.SqlDataProvider:L197</c>), matching <c>maxlength</c> 100 on the
    /// text box at <c>signup.ascx:L89</c>.
    /// </summary>
    /// <remarks>
    /// This width is genuinely terminal. The column is introduced by the second
    /// <c>Tmp_Users</c> rebuild and the only later statement touching it adds the
    /// uniqueness constraint <c>IX_{objectQualifier}Users</c>
    /// (<c>03.00.09.SqlDataProvider:L422</c>); no <c>ALTER COLUMN</c> ever widens it.
    /// </remarks>
    private const int UsernameMaximumLength = 100;

    /// <summary>
    /// Maximum length of the administrator's electronic mail address: the terminal
    /// <c>Email nvarchar(256) NULL</c> column of <c>dbo.Users</c>.
    /// </summary>
    /// <remarks>
    /// This is deliberately NOT the same figure as
    /// <see cref="UsernameMaximumLength"/>, and the two were previously conflated into
    /// one shared constant. They are not the same width in the terminal schema: the
    /// email column is created at <c>nvarchar(100) NOT NULL</c>
    /// (<c>01.00.00.SqlDataProvider:L107</c>), DROPPED at
    /// <c>02.02.01.SqlDataProvider:L50-51</c>, and re-added as
    /// <c>nvarchar(256) NULL</c> at <c>03.00.13.SqlDataProvider:L109-110</c>, whereas the
    /// username column is never widened. The <c>maxlength</c> 100 on the legacy text box
    /// at <c>signup.ascx:L106</c> is a presentation limit on one screen, not a storage
    /// constraint, and the terminal <c>AddUser</c> procedure accepts
    /// <c>@Email nvarchar(256)</c> (<c>04.00.04.SqlDataProvider:L704</c>). The terminal
    /// <c>AddPortalInfo</c> (<c>04.03.05.SqlDataProvider:L51</c>) has no email parameter
    /// at all, because the address belongs to the administrator user rather than to the
    /// portal.
    /// </remarks>
    private const int EmailMaximumLength = 256;


    /// <summary>
    /// The characters a child portal's alias may contain, copied verbatim from
    /// <c>Signup.ascx.vb:L192</c> and L207. Lower case only, which is what makes
    /// the legacy lower-casing at L183 lossless.
    /// </summary>
    private const string ChildPortalAliasCharacters = "abcdefghijklmnopqrstuvwxyz0123456789-";

    /// <summary>
    /// The characters a parent portal's alias may contain. The legacy code builds
    /// this set by widening the child set with three punctuation characters when
    /// the portal is not a child (<c>Signup.ascx.vb:L208-L210</c>), which lets a
    /// parent alias carry a host name, a port and a path segment.
    /// </summary>
    private const string ParentPortalAliasCharacters = ChildPortalAliasCharacters + "./:";

    /// <summary>
    /// The scheme prefix the legacy screen stripped from the alias before
    /// inspecting its characters (<c>Signup.ascx.vb:L184</c>).
    /// </summary>
    private const string LegacySchemePrefix = "http://";

    /// <summary>
    /// The characters whose presence turns a template name into a path. Forward
    /// and backward slash cover a relative or absolute path and a network share;
    /// the colon covers a drive qualifier and an alternate data stream.
    /// </summary>
    private const string PathQualifyingCharacters = "/\\:";

    /// <summary>
    /// The parent-directory token, rejected in a template name in its own right
    /// because appending it to a directory path that already ends in a separator
    /// escapes that directory without needing a separator of its own.
    /// </summary>
    private const string ParentDirectoryToken = "..";

    /// <summary>
    /// Declares the rule set, binding the administrator credential's thresholds and the
    /// numbers substituted into its message from the supplied policy rather than restating
    /// any of them.
    /// </summary>
    /// <param name="policy">
    /// The password policy in force, preserved verbatim from the legacy membership provider
    /// registration and bound by the Api layer from the configuration section named by
    /// <see cref="PasswordPolicyOptions.SectionName"/>. It is read once here, at construction
    /// time; no rule dereferences it again while validating. It is this validator's only
    /// dependency: every other rule is a shape or wording rule measured on the legacy screen,
    /// and none of them needs configuration, a clock, a repository or the file system.
    /// </param>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="policy"/> is <see langword="null"/>. Failing loudly at
    /// construction is deliberate: a validator that silently fell back to built-in defaults
    /// would enforce a policy nobody configured.
    /// </exception>
    // MIGRATION: an earlier revision of this type took no argument and applied requiredness to the
    // administrator credential and nothing else, on the measured ground that signup.ascx declared
    // exactly one validator on txtPassword. That ground was sound about the SCREEN and wrong about the
    // FLOW. The screen's submit handler called CreatePortal (Signup.ascx.vb:L274), which assigned the
    // credential to a membership record (PortalController.vb:L1005), so the policy the screen did not
    // check was applied further down - and in the target that same credential reaches
    // Infrastructure/Security/BcryptPasswordHasher.cs, which THROWS for a credential below the
    // configured minimum length or short of the configured non-alphanumeric count. An unchecked
    // boundary therefore did not mean "no policy"; it meant the policy failure surfaced as an
    // unhandled fault - a 500 for a field-level problem - instead of a validation message naming the
    // field. The request type already documented these rules as living here (see the note above its
    // AdministratorPassword member); this constructor is what makes that documentation true.
    //
    // MIGRATION: the policy arrives as an already-bound instance, NOT as IOptions<T>, matching both
    // sibling credential validators. This layer's package inventory contains FluentValidation and
    // nothing else, and IOptions<T> is a composition-root concern rather than a contract this project
    // should express.
    public CreatePortalRequestValidator(PasswordPolicyOptions policy)
    {
        ArgumentNullException.ThrowIfNull(policy);

        // Each of the legacy validators ran in the browser and blocked the
        // postback, so a field that failed its required check never reached the
        // server-side checks that came after it. Stopping at the first failure
        // per property reproduces that sequencing and stops one empty field from
        // reporting both a requiredness failure and a shape failure.
        RuleLevelCascadeMode = CascadeMode.Stop;

        // Stated explicitly rather than left to a default: the legacy screen
        // rendered every failing validator at once, so every failing field must
        // be reported together instead of one per round trip.
        ClassLevelCascadeMode = CascadeMode.Continue;

        // Read once, here, so no rule dereferences the policy while validating and so the message and
        // the rules that produce it can never disagree about the numbers.
        int minimumPasswordLength = policy.MinRequiredPasswordLength;
        int minimumNonAlphanumericCharacters = policy.MinRequiredNonAlphanumericCharacters;

        // MIGRATION: both bracketed tokens are substituted from the BOUND POLICY VALUES, reproducing
        // UserController.GetUserCreateStatus L607-L609, which read the same two numbers from the
        // provider facade and rewrote them into the message at run time. Neither number is restated as
        // a literal anywhere in this file: doing so would desynchronise the wording from the rule the
        // moment the configuration changed, leaving the text asserting a threshold nobody enforces.
        //
        // MIGRATION: the legacy conversions were implicit. This screen's code-behind compiled with
        // Option Strict disabled - Website/release.config:L125 declares strict="false", whereas the
        // class library at Library/DotNetNuke.Library.vbproj:L24 declares it enabled - so the legacy
        // formatting picked up the ambient culture silently. Both conversions are explicit and
        // culture-invariant here, which is the treatment every implicit coercion receives on crossing
        // into this solution.
        string invalidPasswordMessage = InvalidPasswordMessageTemplate
            .Replace(
                PasswordLengthToken,
                minimumPasswordLength.ToString(CultureInfo.InvariantCulture),
                StringComparison.Ordinal)
            .Replace(
                NoneAlphabetToken,
                minimumNonAlphanumericCharacters.ToString(CultureInfo.InvariantCulture),
                StringComparison.Ordinal);

        // MIGRATION: the portal TITLE carries no requiredness rule, and that is
        // measured rather than overlooked. signup.ascx labels the txtTitle box
        // "Title:" at L51 and declares no validator whatsoever on it at L52,
        // while the call site passes txtTitle.Text as the first argument of
        // CreatePortal (Signup.ascx.vb:L274). The similarly named valPortalName
        // validator, whose message reads "Portal Name Is Required.", is bound to
        // txtPortalName -- the box that L39 labels "Portal Alias:" -- so its
        // message names a field it does not guard. Deriving a requiredness rule
        // for the title from that wording would invent a rule the legacy screen
        // never enforced, so only the length limit applies here. The column is
        // NOT NULL, but an empty string satisfies NOT NULL and the legacy screen
        // could submit one.
        RuleFor(request => request.PortalName)
            .MaximumLength(PortalNameMaximumLength);

        // MIGRATION: the message on the rule below reads "Portal Name Is
        // Required." yet guards the ALIAS, because that is exactly what the
        // legacy screen did -- valPortalName declares
        // controltovalidate="txtPortalName" at signup.ascx:L41 and L39 labels
        // that same box "Portal Alias:". The wording is preserved verbatim
        // rather than corrected to match the field it guards, because Minimal
        // Change Clause item 4 requires equivalent error messages and this is
        // the message a user of the legacy screen actually saw.
        //
        // MIGRATION: the character-set rule is lifted from a code-behind guard
        // clause rather than from markup, which is why no regular-expression
        // validator backs it: the screen looped over the alias one character at
        // a time (Signup.ascx.vb:L191-L195 for a child, L212-L216 otherwise)
        // testing membership of a literal character set. Alias UNIQUENESS, the
        // third check that loop was paired with (L260-L265, message
        // DuplicatePortalAlias), is absent here on purpose: it needs a
        // repository, so the service layer owns it and reports it as a failed
        // outcome.
        RuleFor(request => request.PortalAlias)
            .NotEmpty().WithMessage(PortalAliasRequiredMessage)
            .MaximumLength(PortalAliasMaximumLength)
            .Must((request, alias) => HasOnlyPermittedAliasCharacters(alias, request.IsChildPortal))
                .WithMessage(InvalidAliasCharacterMessage);

        // The description and keywords fields carried no validator; only the
        // markup and column width constrain them, and the two agree.
        RuleFor(request => request.Description)
            .MaximumLength(MetadataMaximumLength);

        RuleFor(request => request.KeyWords)
            .MaximumLength(MetadataMaximumLength);

        // MIGRATION: an absent home directory is a request for the server-side
        // default, not a validation failure. The legacy screen pre-filled the box
        // with a placeholder and sent an empty string when the user left it
        // untouched (Signup.ascx.vb:L245-L249), and the controller then derived
        // the default from the new portal's identifier
        // (PortalController.vb:L991-L992) -- which cannot be computed before the
        // row exists. So no requiredness rule applies, and the defaulting stays
        // in the service.
        //
        // MIGRATION: what the legacy screen did NOT do, and what is added here. The screen's own
        // InvalidHomeFolder check (Signup.ascx.vb:L251-L257) resolved the submitted value to a
        // physical path and reported failure only if that resolution came back empty; it applied no
        // shape rule whatsoever. So a caller could submit a rooted, drive-qualified or
        // parent-traversing value and the legacy code would happily map it, because the value was
        // concatenated straight into a path
        // (ApplicationPath + "/" + HomeDir + "/", L254, and again at PortalController.vb:L994). That
        // is a path-traversal defect, and preserving it is not an option the migration's
        // behaviour-preservation rule can be read to require: the request type already states that no
        // mapped or absolute path is accepted from a caller, so this rule makes an existing promise
        // true rather than inventing a new constraint. The divergence is recorded in
        // MIGRATION_NOTES.md.
        //
        // The rule is purely LEXICAL and touches no file system, which is what allows it to live in
        // this layer at all. It rejects every form that could escape a relative root and then asserts
        // containment against a synthetic root, so a value that passes cannot denote anything outside
        // the directory it is combined with. The physical check - that the resolved directory exists
        // under the hosting environment's content root and is writable - remains the service's
        // responsibility at the point it maps the value, exactly where the legacy code performed it
        // and the only place the content root is available.
        RuleFor(request => request.HomeDirectory)
            .MaximumLength(PortalHomeDirectoryRules.MaximumLength)
            .Must(PortalHomeDirectoryRules.IsSafeRelativeDirectory)
                .WithMessage(PortalHomeDirectoryRules.InvalidMessage);

        // MIGRATION: valTemplate is declared with InitialValue="-1"
        // (signup.ascx:L67-L68), which made the drop-down's unselected state fail
        // its required check. That "-1" is a placeholder for an unselected list
        // item, NOT an identifier, and the target field is a template file name,
        // so presence is expressed as a non-empty string and no numeric or
        // literal placeholder comparison is introduced. This distinction matters
        // because the legacy null contract uses the same negative value as its
        // absent-integer sentinel (Library/Components/Shared/Null.vb:L41-L45)
        // while that value is simultaneously the seed of the Portals primary key
        // (01.00.00.SqlDataProvider:L77) and therefore a real identifier;
        // treating it as an absence marker anywhere in this file would be a
        // defect.
        //
        // MIGRATION: the bare-file-name rule has no counterpart in the markup
        // because the legacy screen could not express a path: the value came from
        // a server-populated drop-down list of template names. Now that the same
        // value arrives as a free-form string over HTTP, that structural
        // guarantee has to be stated as a rule, because the service concatenates
        // it onto a directory path and opens the result
        // (PortalController.vb:L1064 and L1075). The rule preserves the legacy
        // guarantee rather than adding a new restriction, and it needs no
        // input or output to enforce -- whether the named template EXISTS is
        // still checked by the service, never here.
        RuleFor(request => request.TemplateFile)
            .NotEmpty().WithMessage(TemplateRequiredMessage)
            .Must(IsBareFileName).WithMessage(TemplateFileNameOnlyMessage);

        // MIGRATION: 50, not the markup's 100. The terminal column is
        // nvarchar(50) (01.00.06.SqlDataProvider:L182-L197, the two names at L185
        // and L186) and no ALTER COLUMN in any of the eighty-eight upgrade
        // scripts widens either one, so the markup's 100 would admit a value the
        // database cannot store. The reasoning is set down once on the type.
        RuleFor(request => request.AdministratorFirstName)
            .NotEmpty().WithMessage(FirstNameRequiredMessage)
            .MaximumLength(PersonNameMaximumLength);

        // MIGRATION: 50 here for the same reason, and requiredness is doubly
        // grounded: the family-name column is nullable in the baseline
        // schema (01.00.00.SqlDataProvider:L100) and becomes NOT NULL in the
        // rebuild at 01.00.06.SqlDataProvider:L186, which matches the valLastName
        // validator the screen already declared.
        RuleFor(request => request.AdministratorLastName)
            .NotEmpty().WithMessage(LastNameRequiredMessage)
            .MaximumLength(PersonNameMaximumLength);

        RuleFor(request => request.AdministratorUsername)
            .NotEmpty().WithMessage(UsernameRequiredMessage)
            .MaximumLength(UsernameMaximumLength);

        // MIGRATION: the screen's own rule and the membership path's rules, applied together at one
        // boundary. signup.ascx declares exactly one validator on txtPassword -- valPassword, a
        // required-field validator at L95-L96 -- so requiredness is the screen's contribution and it
        // keeps the screen's wording. The policy lived in the membership path at
        // Library/Components/Users/UserController.vb:L1067-L1091, and under the shipped configuration
        // only one of its three rules could ever fire: the minimum length of seven
        // (Website/release.config:L242). The non-alphanumeric rule is inert because the configured
        // minimum is zero (L243) and a count is never below zero, and the strength pattern is
        // unreachable because the setting appears in neither Website/release.config nor
        // Website/development.config -- a case-insensitive search of both finds no occurrence at all.
        //
        // All three are nonetheless expressed below rather than only the one that currently fires,
        // because each is CONFIGURABLE and the hasher enforces each unconditionally. Omitting a rule
        // on the ground that today's configuration makes it inert would mean that raising the
        // configured minimum, or demanding a non-alphanumeric character, silently converted a
        // field-level rejection into an unhandled fault. The inert cases cost nothing: the
        // non-alphanumeric rule is skipped entirely when the configured minimum is zero, and the
        // strength rule is not registered at all when no pattern is configured.
        //
        // MIGRATION: the markup also declares maxlength 20 on txtPassword (signup.ascx:L94), matching
        // the plaintext Password nvarchar(20) column of the baseline schema
        // (01.00.00.SqlDataProvider:L106). That ceiling is deliberately NOT reproduced. The column no
        // longer stores the submitted value -- the Infrastructure layer replaces it with a
        // fixed-width one-way hash -- so the storage limit that justified it has gone, and enforcing
        // it now would cap credential strength at twenty characters for no remaining reason.
        //
        // MIGRATION: the ceiling that IS enforced is NET-NEW and is not a policy rule. This member is a
        // credential that reaches the one-way hash, so it is a credential entry point, and an unbounded
        // field feeding a deliberately expensive function lets the caller choose how much work the
        // server performs. The number, the unit and the wording live once, on
        // Validation/CredentialBounds.cs, and sign-in, registration, the password sub-resource and this
        // validator all read them from there, so the four boundaries cannot drift apart. The unit is
        // encoded BYTES rather than characters, because that is what the algorithm consumes and a
        // character outside the ASCII range occupies two to four of them. It is deliberately not the
        // algorithm's own 72-byte significance limit: the hasher digests the credential with SHA-384
        // before hashing (Infrastructure/Security/BcryptPasswordHasher.cs), so every byte contributes
        // and truncation cannot make two distinct credentials interchangeable. At more than twelve
        // times the legacy input ceiling it cannot reject a credential the legacy screen accepted.
        RuleFor(request => request.AdministratorPassword)
            .NotEmpty().WithMessage(PasswordRequiredMessage)
            .MinimumLength(minimumPasswordLength).WithMessage(invalidPasswordMessage)
            .Must(CredentialBounds.IsWithinMaximumByteLength)
                .WithMessage(CredentialBounds.MaximumByteLengthMessage);

        // MIGRATION: a configured minimum of zero SKIPS THE RULE ENTIRELY rather than registering a
        // comparison that can never fail, for the same reason the strength rule below is skipped when
        // no pattern is configured: a rule that cannot fail is indistinguishable from enforcement
        // under review, and evaluating it would scan every character of every submitted credential to
        // reach a foregone conclusion. This mirrors the hasher, which skips the identical check for
        // the identical reason.
        if (minimumNonAlphanumericCharacters > 0)
        {
            RuleFor(request => request.AdministratorPassword)
                .Must(password =>
                    HasEnoughNonAlphanumericCharacters(password, minimumNonAlphanumericCharacters))
                .WithMessage(invalidPasswordMessage)
                .When(request => !string.IsNullOrEmpty(request.AdministratorPassword));
        }

        // MIGRATION: an unconfigured strength pattern SKIPS THE RULE ENTIRELY rather than compiling an
        // empty one. An empty pattern matches every input, so applying it would be a silent no-op
        // wearing the appearance of enforcement - strictly worse than no rule, because it would
        // survive review. The pattern is compiled once here, never per evaluation, and carries a
        // timeout because it is operator-supplied configuration applied to caller-supplied input. An
        // unparseable configured value throws at construction, which is what the legacy code did when
        // it built the pattern, merely reached sooner and reported against the configuration rather
        // than against a caller's request; the bound policy's own start-up validation reports it
        // earlier still.
        if (!string.IsNullOrEmpty(policy.PasswordStrengthRegularExpression))
        {
            Regex configuredStrengthPattern = new(
                policy.PasswordStrengthRegularExpression,
                RegexOptions.CultureInvariant,
                PatternMatchTimeout);

            RuleFor(request => request.AdministratorPassword)
                .Matches(configuredStrengthPattern).WithMessage(invalidPasswordMessage)
                .When(request => !string.IsNullOrEmpty(request.AdministratorPassword));
        }

        // MIGRATION: requiredness, length AND format. The shape rule is the legacy
        // constant at Library/Components/Shared/Globals.vb:L132, and it is applied
        // by delegating to the Domain value object that owns it rather than by
        // transcribing the pattern a fourth time. An earlier reading of this file
        // left the format unenforced; the three grounds it gave are corrected here,
        // because each one argued for a different remedy than omission.
        //
        // MIGRATION: the four-letter cap on the top-level domain is a PRESERVED
        // LEGACY DEFECT, not a reason to skip validation. A longer modern top-level
        // domain did fail the legacy screen, and it still fails, deliberately:
        // widening the rule would accept addresses the legacy application refused,
        // which is the loosening this migration must not make silently. The
        // apostrophe stays admitted for the mirror-image reason, so accounts that
        // can sign in today keep working.
        //
        // MIGRATION: whole-value matching is a documented TIGHTENING relative to
        // the raw constant, and it is the faithful reading of the screen. The
        // constant is delimited by word-boundary assertions, so applied as a search
        // it passes any value merely CONTAINING something address-shaped, including
        // one carrying an embedded line break. Since this address is written to the
        // administrator account and quoted in audit records, that is a property to
        // remove rather than preserve; the value object refuses such characters by
        // construction, because neither of its character classes admits one.
        //
        // MIGRATION: the seeded accounts are NOT evidence against an inbound rule,
        // and reading them that way was a category error. The address on the Host
        // account is a single bare word with no at-sign (01.00.00.SqlDataProvider:
        // L7205) and the Administrator's at L7207 is shaped the same way, so
        // neither satisfies the legacy pattern -- but both are EXISTING ROWS, and a
        // validator binds inbound requests only. It is never a precondition for
        // reading or writing a row that already exists, which is precisely why the
        // value object also exposes a non-throwing factory for materialising such
        // rows. Declining to check new input because old data would fail the check
        // would leave the format unenforced everywhere for the sake of records this
        // rule never sees.
        //
        // MIGRATION: no uniqueness rule either. The membership provider was
        // registered with requiresUniqueEmail="false"
        // (Website/release.config:L244), so two users could already share an
        // address and existing rows may do so. A duplicate is reported by the
        // service after the persistence attempt, as a failed outcome rather than
        // a rejected request, which is also the only place a repository is
        // available to detect it.
        // MIGRATION: THE SHAPE CHECK IS NET-NEW ON THIS PATH, AND ITS ABSENCE WAS A GAP RATHER
        // THAN A FAITHFUL OMISSION. The signup screen declared a required-field validator over
        // this box and no expression validator, so a malformed address passed the screen and
        // went on to become the new administrator's membership e-mail. The address is checked
        // here for the same reason the user-creation and user-update paths check it: the value
        // is stored as a real account's contact address, and the two paths must not disagree
        // about what an address is. The rule delegates to the single authority,
        // Domain/ValueObjects/EmailAddress.cs, rather than restating a pattern - a second
        // transcription is exactly how the create and update user paths once came to accept
        // different addresses. The divergence is recorded in MIGRATION_NOTES.md, and the
        // wording is the shared InvalidEmailMessage entry rather than an invented one, because
        // the screen carried no wording of its own for a condition it never reported.
        //
        // Stopping at the first failure keeps an omitted address reporting only that it is
        // required: the shape helper additionally treats a blank value as satisfying the rule,
        // so the two guards agree even if the cascade is ever removed.
        RuleFor(request => request.AdministratorEmail)
            .Cascade(CascadeMode.Stop)
            .NotEmpty().WithMessage(EmailRequiredMessage)
            .MaximumLength(EmailMaximumLength).WithMessage(InvalidEmailMessage)
            .Must(BeAWellFormedEmailAddress).WithMessage(InvalidEmailMessage);

        // MIGRATION: the child-portal flag carries no rule of its own. Its radio
        // button list always posted one of two values and declared no validator
        // (signup.ascx:L33-L36), and the flag is a non-nullable Boolean whose
        // absent value deserialises to the legacy default of a parent portal. It
        // appears above only as the condition that selects which character set
        // the alias is measured against.
        //
        // MIGRATION: no rule anywhere in this file reproduces valConfirm, the
        // eighth validator on the screen (signup.ascx:L101-L102), whose message
        // reads, verbatim and recorded here so the measured wording survives:
        //   "Password Confirmation Is Required."
        // Its text box was never one of the fifteen arguments -- the call site at
        // Signup.ascx.vb:L274 passes the password once -- was never persisted,
        // and is absent from the request contract by design, so there is no
        // property to attach a rule to. The equality check the code-behind ran at
        // Signup.ascx.vb:L220-L222 moves to the browser form, where both values
        // exist; sending a password twice would widen its exposure without
        // adding any safety. That check was server-side to begin with: the screen
        // declares no asp:CompareValidator, so this is a change of mechanism and
        // of location, not a change of rule.
        //
        // MIGRATION: the wording that comparison would have used is itself
        // ambiguous in the legacy sources, so all THREE measured strings are
        // recorded verbatim, one per line, rather than one being silently chosen.
        // The screen used its own local resource, InvalidPassword.Text, at
        // Website/admin/Portal/App_LocalResources/Signup.ascx.resx:L237-L238:
        //   "The Password Values Entered Do Not Match."
        // The shared resource file additionally carries a DUPLICATE pair under one
        // logical name -- PasswordMismatch.Text at
        // Website/App_GlobalResources/SharedResources.resx:L852-L853:
        //   "The Password and Confirmation Passwords do not match"
        // and PasswordMismatch.Text1 at the same file's L969-L970:
        //   "Password Values Entered Do Not Match."
        // Note that the first of those three has no trailing full stop while the
        // other two do. The bare-key convention that
        // UserController.GetUserCreateStatus uses resolves to the .Text form, so
        // that is the entry a future cross-field rule should adopt, and the
        // screen's own local entry is the one that actually rendered here. The
        // duplicate is noted rather than resolved: deleting a legacy resource
        // entry is not this refactor's business.
    }

    /// <summary>
    /// Reports whether every character of a submitted portal alias belongs to the
    /// set the legacy signup screen permitted.
    /// </summary>
    /// <param name="submittedAlias">The alias exactly as submitted.</param>
    /// <param name="isChildPortal">
    /// Selects the permitted character set and, for a child portal, the portion of
    /// the alias the legacy code measured.
    /// </param>
    /// <returns>
    /// <see langword="true"/> when the alias is absent — so that requiredness is
    /// reported once, by the rule that owns it — or when every measured character
    /// is permitted; otherwise <see langword="false"/>.
    /// </returns>
    /// <remarks>
    /// <para>
    /// Two normalisations are applied before the characters are measured, in the
    /// order and for the reason the legacy code applied them: the alias is lowered
    /// in case (<c>Signup.ascx.vb:L183</c>) and the scheme prefix is removed
    /// (L184), both before either guard loop runs. Reproducing that order matters,
    /// because the permitted sets hold lower-case letters only and the legacy
    /// membership test was case-sensitive; measuring the raw value instead would
    /// reject a mixed-case alias the legacy screen accepted, and would reject a
    /// child alias carrying a scheme prefix that the legacy screen stripped. Both
    /// normalisations are evaluated here and discarded; the request is never
    /// mutated, and persisting the lowered form remains service behaviour.
    /// </para>
    /// <para>
    /// The case conversion is culture-invariant, whereas the legacy call was
    /// culture-sensitive. Every permitted character is a printable ASCII
    /// character, so the two agree on all of them; they can differ only on
    /// non-ASCII input, which both reject. Pinning the culture keeps validation
    /// from depending on the server's regional settings.
    /// </para>
    /// <para>
    /// For a child portal the legacy code measured only the segment after the
    /// final forward slash (<c>Signup.ascx.vb:L202</c>), because a child is
    /// addressed beneath its parent's alias and only the trailing segment is the
    /// child's own name. That derivation is reproduced, including its edge cases:
    /// an alias with no slash is measured whole, exactly as the legacy
    /// one-based string function did when its search returned zero, and an alias
    /// ending in a slash yields an empty segment that no character can fail. The
    /// stricter reading, in which the whole value is measured for a child
    /// (<c>Signup.ascx.vb:L191-L195</c>), belongs to the branch the legacy screen
    /// took when the request did not originate from a host-level page — a
    /// decision that rested on ambient per-request state and now belongs to the
    /// service and its authorisation policy. The more permissive branch is
    /// reproduced deliberately: a validator must not reject a request the legacy
    /// system accepted.
    /// </para>
    /// </remarks>
    private static bool HasOnlyPermittedAliasCharacters(string? submittedAlias, bool isChildPortal)
    {
        if (string.IsNullOrEmpty(submittedAlias))
        {
            return true;
        }

        string normalisedAlias = submittedAlias
            .ToLowerInvariant()
            .Replace(LegacySchemePrefix, string.Empty, StringComparison.Ordinal);

        string measuredAlias = isChildPortal
            ? normalisedAlias[(normalisedAlias.LastIndexOf('/') + 1)..]
            : normalisedAlias;

        string permittedCharacters = isChildPortal
            ? ChildPortalAliasCharacters
            : ParentPortalAliasCharacters;

        foreach (char character in measuredAlias)
        {
            if (!permittedCharacters.Contains(character, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Reports whether a submitted template selection names a file without
    /// qualifying it with a path.
    /// </summary>
    /// <param name="submittedTemplateName">
    /// The template name exactly as submitted, without the extension the service
    /// appends.
    /// </param>
    /// <returns>
    /// <see langword="true"/> when the value is absent — so that requiredness is
    /// reported once, by the rule that owns it — or when it names a file only;
    /// otherwise <see langword="false"/>.
    /// </returns>
    /// <remarks>
    /// The check is textual and touches nothing: it opens no handle, enumerates no
    /// folder and asks the file system nothing at all. Whether the named template
    /// exists, and whether it validates against the portal template schema, are
    /// both established by the service, which is where the legacy screen
    /// established them too (<c>Signup.ascx.vb:L169-L180</c>).
    /// </remarks>
    private static bool IsBareFileName(string? submittedTemplateName)
    {
        if (string.IsNullOrEmpty(submittedTemplateName))
        {
            return true;
        }

        foreach (char character in submittedTemplateName)
        {
            if (PathQualifyingCharacters.Contains(character, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return !submittedTemplateName.Contains(ParentDirectoryToken, StringComparison.Ordinal);
    }


    /// <summary>
    /// Reports whether the submitted administrator credential carries at least the configured
    /// number of characters outside the ranges <c>0-9</c>, <c>A-Z</c> and <c>a-z</c>.
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
    /// <para>
    /// The classification reproduces the legacy character class <c>[^0-9a-zA-Z]</c> declared at
    /// <c>Library/Components/Users/UserController.vb:L1078</c> and compared against the configured
    /// minimum at L1079. It is deliberately <b>not</b> a Unicode-aware test: the framework's
    /// culture-aware letter-or-digit check would classify an accented letter as alphanumeric, so
    /// such a character would stop contributing to the total and the rule would silently widen.
    /// The ASCII-restricted test used here negates precisely the set the legacy pattern negated,
    /// and it is the identical test
    /// <c>DnnMigration.Infrastructure/Security/BcryptPasswordHasher.cs</c> performs, so the
    /// boundary and the primitive agree on which credentials satisfy the rule.
    /// </para>
    /// <para>
    /// The scan stops as soon as the minimum is reached, and the value itself is never retained,
    /// echoed or interpolated into the failure message.
    /// </para>
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

    /// <summary>
    /// Reports whether the administrator's mail address satisfies the single legacy shape rule, by
    /// asking the Domain value object that owns that rule rather than by restating it here.
    /// </summary>
    /// <param name="submittedEmail">The address as supplied by the caller, which may be absent.</param>
    /// <returns>
    /// <see langword="true"/> when the address is well formed, and also when none was supplied;
    /// <see langword="false"/> only when a supplied address is malformed.
    /// </returns>
    /// <remarks>
    /// An absent or blank value returns <see langword="true"/> so that the emptiness rule alone
    /// reports absence, which is how the legacy screen behaved - its framework validators treated
    /// blank input as satisfying every check but the required one. The check is applied to the WHOLE
    /// value, which is what the framework expression validator on the user screens did; the reasoning,
    /// and every deliberate departure from the legacy pattern, are recorded on
    /// <see cref="EmailAddress"/> itself.
    /// </remarks>
    private static bool BeAWellFormedEmailAddress(string? submittedEmail)
    {
        if (string.IsNullOrWhiteSpace(submittedEmail))
        {
            return true;
        }

        return EmailAddress.TryCreate(submittedEmail, out _);
    }
}
