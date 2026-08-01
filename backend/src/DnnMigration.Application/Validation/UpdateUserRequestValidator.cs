using System.Text.RegularExpressions;
using DnnMigration.Application.Dtos.User;
using FluentValidation;

namespace DnnMigration.Application.Validation;

/// <summary>
/// Declarative validator for <see cref="UpdateUserRequest"/>, the inbound contract of
/// <c>PUT /api/v1/users/{id}</c>.
/// </summary>
/// <remarks>
/// <para>
/// This validator carries <b>no password rule of any kind</b> and takes <b>no constructor
/// dependency</b>. Changing a credential is a separate resource with its own request contract,
/// <c>ChangePasswordRequest</c>, and its own validator; the users controller exposes it as a
/// sub-resource rather than folding it into a general profile update. Nothing here reads,
/// compares, hashes, measures or otherwise touches credential material, and
/// <see cref="UpdateUserRequest"/> exposes none to touch.
/// </para>
/// <para>
/// <b>Identifiers carry no bound test.</b> The addressed user arrives on the route and the tenant
/// arrives from the resolved portal context, so no identifier appears on this request at all — and
/// even if one did, a bound test would be wrong. The identity seeds in this schema are deliberately
/// low or negative, so a zero or negative identifier is a legitimate row key rather than an absent
/// value. Whether a user is present is a repository concern that the application service reports as
/// a failed outcome and the API surfaces as 404, never as a validation failure surfaced as 400.
/// </para>
/// <para>
/// <b>Field lengths follow the schema, not the markup.</b> Every limit below is the terminal
/// column width obtained by replaying the 88 upgrade scripts in order, because that chain is
/// destructive: a column can be created, dropped and re-created at a different width, so only its
/// last declaration describes the live database. Where the legacy screen, a stored procedure
/// parameter and the column disagree, the column governs.
/// </para>
/// <para>
/// Rules are declarative <c>RuleFor</c> chains only. Each chain stops at its first failure, which
/// reproduces the legacy screen's behaviour of reporting one message per field: a Web Forms
/// required-field check fires first, and every other Web Forms validator treats blank input as
/// valid and defers to it.
/// </para>
/// <para>
/// This type is resolved by assembly scanning in the layer's dependency-injection extension, with
/// internal types excluded from the scan, which is why it must be declared <c>public</c>. It
/// registers itself nowhere.
/// </para>
/// </remarks>
// The measured legacy authorities behind every decision below, so a reviewer can verify each one
// without re-deriving it:
//
//   * the field set and its constraints, from the attributes the runtime property editor reflected
//     over - Library/Components/Users/UserInfo.vb, where L301 is Username
//     <SortOrder(0), IsReadOnly(True), Required(True)>, L144 is FirstName
//     <SortOrder(1), MaxLength(50), Required(True)>, L178 is LastName
//     <SortOrder(2), MaxLength(50), Required(True)>, L104 is DisplayName
//     <SortOrder(3), Required(True), MaxLength(128)> and L121-L122 is Email
//     <SortOrder(4), MaxLength(256), Required(True), RegularExpressionValidator(glbEmailRegEx)>.
//     Every other property of that class carries <Browsable(False)> and rendered nowhere;
//   * the message wording, from Website/admin/Users/App_LocalResources/User.ascx.resx and
//     Website/App_GlobalResources/SharedResources.resx;
//   * the column widths, from Website/Providers/DataProviders/SqlDataProvider/*.SqlDataProvider.
//
// MIGRATION: No password rule of any kind is authored here, and no bound password-policy
// configuration is injected, so this validator has no constructor dependency whatsoever. Password
// change is a separate resource carrying its own request, ChangePasswordRequest, which the users
// controller exposes as a sub-resource. UpdateUserRequest exposes no credential member, so there
// is nothing on this contract for such a rule to bind to. The legacy policy that deliberately does
// not apply here is registered at Website/release.config:L239-L245 - the element itself spans
// L236-L247 - and UserController.ValidatePassword at
// Library/Components/Users/UserController.vb:L1067-L1091 shows it reduced in practice to a single
// active length check, because its non-alphanumeric test compares a count against a configured
// minimum of zero and can never fail, and its strength expression is guarded by a non-empty test
// while being absent from both release.config and development.config.
//
// MIGRATION: No password question or answer rule is authored. Website/release.config:L241 sets the
// membership provider's question-and-answer requirement to false, and the two input rows that
// would have collected them, trQuestion at Website/admin/Users/User.ascx:L50 and trAnswer at L54,
// are declared visible="false". UserController.ChangePasswordQuestionAndAnswer does exist at
// UserController.vb:L139, but a method existing is not evidence that a feature is enabled, and
// neither is the presence of the four matching shared-resource keys.
//
// MIGRATION: Password retrieval is not carried forward to any endpoint, screen or contract.
// UserController.vb:L433 declares GetPassword with a ByRef user argument; retrieval is dropped
// outright and that ByRef status-reporting idiom is retired, since no target public API exposes a
// by-reference parameter.
//
// MIGRATION: No bound test is authored on any identifier, and this contract carries no identifier
// member to test. The idiom is refused categorically rather than case by case, because the seeds
// in this schema make it actively wrong one field over: Portals.PortalID is declared
// IDENTITY (-1, 1) at 01.00.00.SqlDataProvider:L77 and the shipped _default portal is inserted
// with PortalID 0 at L7125, Roles.RoleID is IDENTITY (0, 1) at L115, and the legacy integer
// sentinel is itself minus one, so one integer means both "the first portal" and "nothing" at the
// same time. Library/Components/Shared/Null.vb likewise returns 255 for its byte sentinel and the
// framework minimum date for its date sentinel, so those too are values carrying meaning rather
// than validation failures.
//
// MIGRATION: FirstName and LastName are capped at 50, the column width, not at the 100 the legacy
// screen and the stored procedures allowed. Users.FirstName and Users.LastName are declared
// nvarchar(50) in the last rebuild of that table, at 01.00.06.SqlDataProvider:L185-L186, and a
// sweep of all 88 scripts finds no ALTER COLUMN touching either name, so 50 is terminal and was
// never widened. The property editor agreed at UserInfo.vb:L144 and L178.
//
// MIGRATION: LastName is treated as required even though UpdateUserRequest declares it a nullable
// string. Three authorities agree that it is required and only one superseded line disagrees. The
// last rebuild of the table declares LastName nvarchar(50) NOT NULL at 01.00.06:L186 - and at
// 01.00.05:L18 before it - whereas the NULL declaration is the original baseline at
// 01.00.00:L100, which that rebuild replaced; the property editor declared Required(True) at
// UserInfo.vb:L178; and the screen shipped a required-message for the field at
// User.ascx.resx:L159-L160. A nullable transport member and a required rule are consistent: the
// shape tolerates an omitted field so that the rule, rather than the deserialiser, is what rejects
// it.
//
// MIGRATION: Email is capped at 256, not at the 100 of the original baseline column. This is the
// destructive-chain trap in its sharpest form. Users.Email began as nvarchar(100) NOT NULL at
// 01.00.00.SqlDataProvider:L107, was dropped from the table altogether at 02.02.01:L51-L52 when
// credentials moved into the externally installed membership store, and was re-created at
// 03.00.13:L109-L110 as Email nvarchar(256) NULL, immediately populated from that store. No
// ALTER COLUMN in any of the 88 scripts touches it afterwards, so 256 is terminal. Two independent
// authorities agree: the property editor declared MaxLength(256) at UserInfo.vb:L121, and the
// terminal UpdateUser procedure declares @Email nvarchar(256) at 04.00.04:L1078. The re-created
// column permits NULL because it is a denormalised copy of the membership store's own column, but
// the screen required a value, so the required rule is retained and only the width is corrected.
//
// MIGRATION: DisplayName is required and capped at 128. The column was added to Users at
// 03.02.03.SqlDataProvider:L630, re-applied under a version guard at 04.00.04:L670, as
// nvarchar(128) NOT NULL with a default of the empty string, and the property editor declared
// Required(True), MaxLength(128) at UserInfo.vb:L104 with a required-message at
// User.ascx.resx:L222-L223. Two details are recorded rather than absorbed. The column's default
// makes an empty display name legitimate stored state for a row created before that upgrade and
// never edited since, yet the edit screen still demanded a value, so the required rule reproduces
// the screen. And the terminal UpdateUser procedure declares @DisplayName nvarchar(100) at
// 04.00.04:L1079 - narrower than its own column, and therefore silently truncating - but the
// target writes through the object-relational mapper instead of that procedure, so the column's
// 128 governs and the truncation disappears with the mechanism that caused it.
//
// MIGRATION: No email-uniqueness and no username-uniqueness check is authored, and this validator
// therefore takes no repository and declares no asynchronous rule. Website/release.config:L244
// registers the membership provider with its unique-email requirement set to false. The shipped
// seed data proves independently that format and duplication were never persistence preconditions:
// the Host account is inserted at 01.00.00.SqlDataProvider:L7205 with an email address of 'host'
// and the Administrator at L7207 with 'admin', neither of which satisfies even the legacy format
// pattern. Duplication is detected by the store and reported afterwards -
// UserController.GetUserCreateStatus maps a duplicate address at L600-L601 and a duplicate name at
// L619-L620 onto the two shared-resource messages about an account already being registered - so
// those are service-layer outcomes that become RFC 7807 problem-details responses, not validation
// failures. Those two messages are consequently attached to no rule in this file.
//
// MIGRATION: No rule is authored for a username. UpdateUserRequest exposes no such member, by
// measurement rather than by omission: UserInfo.vb:L301 declares Username IsReadOnly(True), the
// column carries the table's only uniqueness constraint and the membership store keys on it, and
// the terminal UpdateUser procedure at 04.00.04:L1072-L1091 never assigns it. Renaming a user is
// not an operation the legacy application offers. The shared-resource message about an invalid
// username, at SharedResources.resx:L291, is therefore deliberately attached to nothing here; it
// belongs to the creation contract, CreateUserRequest, which does carry the member.
//
// MIGRATION: No optional-field length rule with a presence guard appears here, because no optional
// field survives on this contract. Street, City, Region, PostalCode, Country, Unit and Telephone
// were removed from the Users table at 02.02.01.SqlDataProvider:L51-L52 and re-homed as profile
// values, which the 03.02.03 profile transfer confirms; they are edited through the profile
// sub-resource. Had one survived, its rule would have been guarded on emptiness rather than on
// null, because Null.vb:L71-L75 returns the empty string - not null - for its string sentinel, so
// legacy code cannot distinguish the two and a rule must not either.
//
// MIGRATION: No rule is authored for the super-user flag, the membership flags or role membership,
// none of which appear on this contract. Whether a caller may elevate another account is
// authorisation, not validation: it belongs to Api/Authorization/PermissionAuthorizationHandler.cs
// together with the caller-identity abstraction, and no such abstraction is injected here.
//
// MIGRATION: No profile-property rule is authored. Those properties are defined by
// ProfilePropertyDefinition rows and are rendered from the profile-definitions endpoint at
// run time, so a statically authored validator cannot know them; no reflective type discovery is
// performed to find out.
//
// MIGRATION: This validator deliberately duplicates the shape of its creation counterpart, the
// validator for CreateUserRequest, rather than sharing anything with it. There is no inheritance
// between the two, no rule-set inclusion and no shared base abstraction, because a common base for
// validators is expressly forbidden and the folder is fixed at ten independent files. Each stands
// alone, and the two are genuinely asymmetric in any case: the creation contract carries a
// username and credential material and this one carries neither.
//
// MIGRATION: Path correction for anyone tracing the email pattern. The action plan cites
// Library/Components/Common/Globals.vb, which does not exist in this repository. The constant is
// declared at Library/Components/Shared/Globals.vb:L132.
//
// MIGRATION: The legacy web pages compiled with Option Strict disabled -
// Website/release.config:L125 declares compilation debug="false" strict="false" - while the class
// library compiled with it enabled, so the admin code-behinds could legally narrow implicitly and
// bind late. This screen is the extreme case, since its field set was produced by a run-time
// reflective property editor rather than declared in markup: Website/admin/Users/User.ascx is 79
// lines and hosts a single propertyeditorcontrol at L6, and the sibling grids manageusers.ascx and
// users.ascx declare no validator at all. Every conversion in this file is consequently explicit
// and total. The limits are integer literals rather than parsed configuration, the pattern is a
// compile-time constant, the messages are constants, and nothing is coerced at run time - so there
// is no implicit narrowing left for which a behavioural difference could arise.
public class UpdateUserRequestValidator : AbstractValidator<UpdateUserRequest>
{
    /// <summary>
    /// The legacy email pattern, reproduced character for character from
    /// <c>glbEmailRegEx</c> at <c>Library/Components/Shared/Globals.vb:L132</c>.
    /// </summary>
    private const string LegacyEmailPattern = @"\b[a-zA-Z0-9._%\-+']+@[a-zA-Z0-9.\-]+\.[a-zA-Z]{2,4}\b";

    /// <summary>Verbatim wording of <c>UserInfo_FirstName.Required</c>, <c>User.ascx.resx:L157</c>.</summary>
    private const string FirstNameRequiredMessage = "First name is required";

    /// <summary>Verbatim wording of <c>UserInfo_LastName.Required</c>, <c>User.ascx.resx:L160</c>.</summary>
    private const string LastNameRequiredMessage = "Last name is required";

    /// <summary>Verbatim wording of <c>UserInfo_DisplayName.Required</c>, <c>User.ascx.resx:L223</c>.</summary>
    private const string DisplayNameRequiredMessage = "Display Name is required";

    /// <summary>Verbatim wording of <c>UserInfo_Email.Required</c>, <c>User.ascx.resx:L163</c>.</summary>
    private const string EmailRequiredMessage = "Email is required";

    // MIGRATION: The email format wording is the shared, application-wide message
    // InvalidEmail.Text at Website/App_GlobalResources/SharedResources.resx:L282, reproduced
    // character for character including its two spaces after the sentence period. The spacing in
    // that file is inconsistent per key and is therefore never normalised in bulk: L276, L282,
    // L291 and L294 use two spaces after a sentence period while L297 and L300 use one, and L300
    // additionally carries the long-standing spelling of "Futher". The screen-local alternative,
    // UserInfo_Email.Validation at User.ascx.resx:L219-L220, reads "You must enter a valid email
    // address"; the shared message is preferred because it is the wording the service layer also
    // emits for the same condition, through GetUserCreateStatus at UserController.vb:L604-L605,
    // and because the localisation mechanism is not carried forward, so one canonical string is
    // used for one condition.
    private const string InvalidEmailMessage =
        "The email address specified is invalid.  Please specify a valid email address.";

    /// <summary>
    /// Upper bound on how long the pattern may spend on one candidate address, so that a
    /// pathological input cannot turn matching into a denial-of-service vector.
    /// </summary>
    private static readonly TimeSpan EmailPatternMatchTimeout = TimeSpan.FromMilliseconds(250);

    // MIGRATION: The pattern is preserved exactly as measured, with two properties that a
    // modernising hand would quietly change. Its top-level-domain segment is bounded at two to
    // four letters, so addresses ending in a longer domain are rejected exactly as they were
    // before; widening it would accept accounts the legacy application refused. And it is bounded
    // by word boundaries rather than anchored to the whole subject, so it is applied as an
    // unanchored search. That reproduces Website/Install/WizardUser.ascx.vb:L153, which tests the
    // same constant with an unanchored match, and it is also the looser of the two legacy
    // behaviours - a Web Forms expression validator would have demanded a whole-subject match -
    // so it cannot reject an address that is accepted today. Anchoring to the whole subject would
    // be a tightening and is not applied.
    //
    // MIGRATION: The pattern is applied as the fixed shipped constant. The legacy screen replaced
    // it at run time from the portal's Security_EmailValidation setting whenever that setting was
    // present, at Website/admin/Users/User.ascx.vb:L408-L411. Reading a portal setting is data
    // access, which a validator does not perform, so the divergence is bounded and named here
    // instead. It is narrower than it first appears: UserModuleBase.vb:L168 seeds that very
    // setting from this same constant, so behaviour differs only for an installation whose
    // administrator replaced the default.
    private static readonly Regex LegacyEmailExpression = new(
        LegacyEmailPattern,
        RegexOptions.CultureInvariant,
        EmailPatternMatchTimeout);

    /// <summary>
    /// Initialises a new instance of the <see cref="UpdateUserRequestValidator"/> class and
    /// declares its complete rule set.
    /// </summary>
    /// <remarks>
    /// The constructor is parameterless by design. This validator inspects nothing beyond the
    /// request it is handed, so it needs no configuration, no clock, no caller identity and no
    /// repository, and taking any of those would let a rule depend on state the caller cannot see.
    /// </remarks>
    public UpdateUserRequestValidator()
    {
        // Users.FirstName - nvarchar(50) NOT NULL, terminal at 01.00.06.SqlDataProvider:L185;
        // MaxLength(50), Required(True) at UserInfo.vb:L144.
        RuleFor(request => request.FirstName)
            .Cascade(CascadeMode.Stop)
            .NotEmpty().WithMessage(FirstNameRequiredMessage)
            .MaximumLength(50);

        // Users.LastName - nvarchar(50) NOT NULL, terminal at 01.00.06.SqlDataProvider:L186;
        // MaxLength(50), Required(True) at UserInfo.vb:L178; required-message at
        // User.ascx.resx:L159-L160.
        RuleFor(request => request.LastName)
            .Cascade(CascadeMode.Stop)
            .NotEmpty().WithMessage(LastNameRequiredMessage)
            .MaximumLength(50);

        // Users.DisplayName - nvarchar(128) NOT NULL, added at 03.02.03.SqlDataProvider:L630;
        // Required(True), MaxLength(128) at UserInfo.vb:L104; required-message at
        // User.ascx.resx:L222-L223.
        RuleFor(request => request.DisplayName)
            .Cascade(CascadeMode.Stop)
            .NotEmpty().WithMessage(DisplayNameRequiredMessage)
            .MaximumLength(128);

        // Users.Email - nvarchar(256), terminal at 03.00.13.SqlDataProvider:L110; MaxLength(256),
        // Required(True) and the pattern attribute at UserInfo.vb:L121-L122; required-message at
        // User.ascx.resx:L163. Stopping at the first failure keeps an omitted address reporting
        // only that it is required, which is how the legacy screen behaved: its expression
        // validator treated blank input as valid and deferred to the required check.
        RuleFor(request => request.Email)
            .Cascade(CascadeMode.Stop)
            .NotEmpty().WithMessage(EmailRequiredMessage)
            .MaximumLength(256)
            .Matches(LegacyEmailExpression).WithMessage(InvalidEmailMessage);
    }
}
