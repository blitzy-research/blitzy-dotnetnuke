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

using DnnMigration.Application.Dtos.Portal;
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
/// <b>Dependencies.</b> None. The constructor takes no argument, the type holds
/// no field, and the only namespace it imports beyond the validation library is
/// the one declaring the request it validates. Instances are discovered by the
/// Application layer's assembly scan, which registers publicly visible validator
/// types only — hence the accessibility of this class — so no registration is
/// declared here.
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

    /// <summary>
    /// Message of the <c>valEmail</c> required-field validator
    /// (<c>signup.ascx:L106-L107</c>), reproduced verbatim.
    /// </summary>
    private const string EmailRequiredMessage = "Email Is Required.";

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

    /// <summary>
    /// Maximum length of the portal-relative home directory: <c>maxlength</c> 100
    /// on the text box (<c>signup.ascx:L47</c>) and <c>varchar(100)</c> on the
    /// column.
    /// </summary>
    private const int HomeDirectoryMaximumLength = 100;

    /// <summary>
    /// Maximum length of the administrator's given and family names. This is the
    /// schema width of 50 rather than the markup's 100; the reasoning is on the
    /// type.
    /// </summary>
    private const int PersonNameMaximumLength = 50;

    /// <summary>
    /// Maximum length shared by the administrator's sign-in name and electronic
    /// mail address: <c>maxlength</c> 100 on both text boxes
    /// (<c>signup.ascx:L89</c> and L106), <c>Username nvarchar(100) NOT NULL</c>
    /// (<c>01.00.06.SqlDataProvider:L197</c>) and
    /// <c>Email nvarchar(100) NOT NULL</c>
    /// (<c>01.00.00.SqlDataProvider:L107</c>).
    /// </summary>
    private const int CredentialMaximumLength = 100;

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
    /// Declares the rule set. The constructor takes no argument by design: every
    /// rule below is a shape or wording rule measured on the legacy screen, and
    /// none of them needs configuration, a clock, a repository or the file
    /// system.
    /// </summary>
    public CreatePortalRequestValidator()
    {
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
        // in the service. The screen's own InvalidHomeFolder check (L251-L257)
        // resolved a physical path and is likewise excluded from this layer.
        RuleFor(request => request.HomeDirectory)
            .MaximumLength(HomeDirectoryMaximumLength);

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
            .MaximumLength(CredentialMaximumLength);

        // MIGRATION: requiredness and nothing else. signup.ascx declares exactly
        // one validator on txtPassword -- valPassword, a required-field validator
        // at L95-L96 -- so the screen itself imposed no policy. The legacy policy
        // lived in the membership path instead, at
        // Library/Components/Users/UserController.vb:L1067-L1091, and with the
        // shipped configuration only one of its three rules could ever fire: the
        // minimum length of seven (Website/release.config:L242). The
        // non-alphanumeric rule is vacuous because the configured minimum is zero
        // (L243) and a match count is never below zero, and the strength pattern
        // is unreachable because the setting appears in neither
        // Website/release.config nor Website/development.config -- a
        // case-insensitive search of both files finds no occurrence at all. That
        // one active rule belongs to the user-creation and password-change
        // contracts and to the service that orchestrates them, so this validator
        // binds no policy type, declares no length floor and injects nothing.
        //
        // MIGRATION: the markup also declares maxlength 20 on txtPassword
        // (signup.ascx:L94), matching the plaintext Password nvarchar(20) column
        // of the baseline schema (01.00.00.SqlDataProvider:L106). That ceiling is
        // deliberately NOT reproduced. The column no longer stores the submitted
        // value -- the Infrastructure layer replaces it with a fixed-width
        // one-way hash -- so the storage limit that justified the ceiling has
        // gone, and enforcing it now would cap password strength at twenty
        // characters for no remaining reason.
        RuleFor(request => request.AdministratorPassword)
            .NotEmpty().WithMessage(PasswordRequiredMessage);

        // MIGRATION: requiredness and length only -- no format rule. The screen
        // declared no regular-expression validator on txtEmail, and the legacy
        // pattern that a modern reader would reach for,
        // Library/Components/Shared/Globals.vb:L132
        //   \b[a-zA-Z0-9._%\-+']+@[a-zA-Z0-9.\-]+\.[a-zA-Z]{2,4}\b
        // is unfit as an inbound rule on two measured grounds. Its top-level
        // domain is capped at four letters, so a perfectly valid address under a
        // longer modern top-level domain fails it; and it is word-boundary
        // anchored rather than anchored to the whole value, so it also passes a
        // valid address buried in surrounding text. Anchoring it would be a
        // tightening, relaxing the cap would be a loosening, and applying it as
        // written would reject data the legacy system itself shipped: the address
        // seeded onto the Host account is a single bare word carrying neither an
        // at-sign nor a dot (01.00.00.SqlDataProvider:L7205), and the one seeded
        // onto the Administrator account at L7207 is shaped the same way, so
        // neither can satisfy the pattern. It is recorded here as measured and
        // deliberately unenforced.
        //
        // MIGRATION: no uniqueness rule either. The membership provider was
        // registered with requiresUniqueEmail="false"
        // (Website/release.config:L244), so two users could already share an
        // address and existing rows may do so. A duplicate is reported by the
        // service after the persistence attempt, as a failed outcome rather than
        // a rejected request, which is also the only place a repository is
        // available to detect it.
        RuleFor(request => request.AdministratorEmail)
            .NotEmpty().WithMessage(EmailRequiredMessage)
            .MaximumLength(CredentialMaximumLength);

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
}
