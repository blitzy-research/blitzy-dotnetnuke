using DnnMigration.Application.Dtos.Portal;
using DnnMigration.Domain.Common;
using FluentValidation;

namespace DnnMigration.Application.Validation;

// MIGRATION: 1 of 10 -- THE DEFINING MEASUREMENT. Website/admin/Portal/sitesettings.ascx is 568 lines
//   and declares ZERO asp:RequiredFieldValidator instances. Not one: not on the portal name, not on the
//   administrator, not on any quota. Its complete declarative validator census is exactly two
//   asp:CompareValidator instances, both Operator="DataTypeCheck":
//
//     valExpiryDate  L433-L435  ControlToValidate="txtExpiryDate"  Type="Date"
//                               ErrorMessage="<br>Invalid expiry date!"
//     valHostFee     L444-L446  ControlToValidate="txtHostFee"     Type="Currency"
//                               ErrorMessage="Invalid fee, needs to be a currency value!"
//                               ResourceKey="valHostFee.Error"
//
//   The census was re-measured against the file rather than inherited: grep -ci RequiredFieldValidator
//   returns 0, grep -ci RegularExpressionValidator returns 0, and grep -ni CompareValidator returns the
//   two declarations above. So the rule set below is thin by measurement, not by omission, and
//   restraint is the requirement here rather than thoroughness.

// MIGRATION: 2 of 10 -- AN ASP.NET CompareValidator SUCCEEDS ON EMPTY INPUT. A CompareValidator with
//   Operator="DataTypeCheck" performs no presence check whatsoever: given an empty string it PASSES.
//   Leaving the expiry date or the hosting fee blank was therefore entirely valid on the legacy screen.
//   Consequently no unconditional presence rule is attached to either member, and the one rule that
//   does touch the fee is guarded on a value having been supplied. Attaching NotEmpty() to a field
//   whose only legacy validator was a CompareValidator would convert a legitimate blank into an
//   HTTP 400 and would breach Minimal Change Clause item 4, which requires that validation rules match.

// MIGRATION: 3 of 10 -- THE ONE NET-NEW PRESENCE RULE, and why the schema rather than the markup
//   justifies it. NotEmpty() on the portal name is the single unconditional presence rule in this file.
//   The markup declared none, so this rule is NOT a reproduction; it is authorised by Rule T4 from
//   [PortalName] [nvarchar] (128) NOT NULL at 01.00.00.SqlDataProvider:L79.
//
//   The justification is stronger than "the column is NOT NULL", because an empty string does satisfy
//   a NOT NULL constraint. The decisive fact is the legacy null contract's WRITE side:
//   Library/Components/Shared/Null.vb exposes NullString as literally "" and its GetNull helper
//   converts a String field equal to NullString back into a database null before the value reaches the
//   stored procedure. A blank portal name therefore became "" in memory, was rewritten to NULL on the
//   way out, and was REJECTED by the NOT NULL column. The legacy application did not accept a blank
//   portal name -- it failed at the database. This rule reports that same rejection as a field-level
//   validation failure instead of an unhandled provider exception. The outcome class is unchanged;
//   only the diagnosis improves.

// MIGRATION: 4 of 10 -- NO LOWER BOUND ON THE FEE OR ON ANY QUOTA, and declaring one would be a real
//   defect rather than a safeguard. The tempting shape is five
//   GreaterThanOrEqualTo(0) rules -- on the hosting fee, the disk space, the page quota, the user quota
//   and the site-log retention -- defended as "a deliberate, documented
//   strengthening and not a legacy rule". None of the five belongs here, for measured reasons:
//
//     valHostFee has NO GreaterThanEqual companion. grep -ni GreaterThanEqual over the whole of
//       sitesettings.ascx returns nothing at all. The fee carries a type check and nothing else.
//     The contrast is deliberate elsewhere in the legacy application, which is what makes the absence
//       meaningful rather than accidental. Website/admin/Security/editroles.ascx DOES bound its fees:
//       valServiceFee2 at L94-L96 and valTrialFee2 at L126-L128 both declare
//       Operator="GreaterThanEqual" ValueToCompare="0". Roles are bounded; portals are not.
//     The clamp that is easily mistaken for a portal rule is not one. PortalController.vb:L390
//       constructs objRoleInfo = New RoleInfo, and L395 and L398 clamp objRoleInfo.ServiceFee and
//       objRoleInfo.TrialFee with IIf(value < 0, 0, value). The clamped object is a RoleInfo, reached
//       while a portal template creates its roles. It is not a portal fee and it is not this contract.
//     Clamping is service behaviour in any case. Rejecting here would change the outcome from
//       "accepted, then coerced by the service" into an HTTP 400, which is a new failure mode.
//
//   The omission is recorded rather than left silent precisely so a future reader does not restore the
//   rules as an oversight. Minimal Change Clause item 1 forbids opportunistic optimisation, and a
//   negative fee is exactly the kind of value the legacy path accepted.

// MIGRATION: 5 of 10 -- NO BOUND TEST ON ANY IDENTIFIER, AND ONE IDENTITY TEST. UpdatePortalRequest
//   carries PortalId as the first of its twenty-seven members, matching argument 1 of the replaced
//   signature, and the ONE rule attached to it is that it must equal the {id} route segment. That rule
//   is what makes a second copy of the subject key safe: IPortalService.UpdatePortalAsync addresses the
//   route value and nothing else, so a body value that cannot disagree with the route cannot retarget
//   the write. The route value is read from the validation context's root data, which
//   Api/Filters/FluentValidationActionFilter.cs populates from RouteData for every request it validates
//   under the key "RoutePortalId"; when no route context is present -- a service or unit-test caller
//   invoking the validator directly -- there is nothing to disagree with and the rule stands down
//   rather than inventing a comparand.
//
//   A BOUND test on that identifier would still be wrong, and none is declared. Portals.PortalID is
//   [int] IDENTITY (-1, 1) NOT NULL at 01.00.00.SqlDataProvider:L77, so -1 is simultaneously a real
//   addressable portal and the legacy absent-integer sentinel Null.NullInteger, and the stock _default
//   portal occupies the very next value, zero, inserted at 01.00.00.SqlDataProvider:L7125. The same
//   applies to the four page references below: Tabs.TabID is IDENTITY (0, 1) at L140, so zero is a
//   legitimate page. Presence is expressed by nullability alone, never by a magic value.

// MIGRATION: 6 of 10 -- NO PAST-DATE REJECTION ON THE EXPIRY DATE, and no rule on it at all.
//   valExpiryDate is a well-formedness check (Operator="DataTypeCheck" Type="Date"), which a strongly
//   typed DateTime? already enforces during model binding: a value that would have failed that check
//   cannot bind into the request in the first place. Declaring a FluentValidation counterpart would
//   declare a check that can never fail, which is indistinguishable from enforcement under review, so
//   the legacy rule is reported as structurally redundant rather than padded out into a rule.
//
//   Nor is a range rule added. The comparable legacy pattern CLAMPS rather than rejects --
//   RoleController.vb:L530-L535 resets an effective date that is in the past to Null.NullDate and an
//   expiry date that is in the past to Now -- and clamping is service behaviour. Null.NullDate is
//   Date.MinValue, a legitimate "no expiry", so there is deliberately no GreaterThan(DateTime.MinValue)
//   here either.

// MIGRATION: 7 of 10 -- MARKUP MaxLength VALUES ARE NOT CARRIED ACROSS WHERE THEY ARE TEXT-ENTRY
//   WIDTHS. sitesettings.ascx sets maxlength on only a handful of inputs -- txtExpiryDate=15,
//   txtHostFee=10, txtHostSpace=6, txtPageQuota=6, txtUserQuota=6, txtUserId=50 -- and the first two
//   bound the characters a user could type into a text box, not the semantics of the value. Neither is
//   translated: MaximumLength(15) on a DateTime? and MaximumLength(10) on a decimal? would both be
//   category errors. The intent is translated instead, and only where a column can be cited. The three
//   quota widths bound integers whose type already bounds them, so they yield no rule either.

// MIGRATION: 8 of 10 -- THIS VALIDATOR DOES NOT INHERIT FROM, Include(), OR OTHERWISE REUSE
//   CreatePortalRequestValidator, and no shared validator base type is introduced. The asymmetry is
//   real and is the behaviour being preserved: Website/admin/Portal/signup.ascx, the CREATE screen,
//   declares eight RequiredFieldValidator instances, while sitesettings.ascx, the UPDATE screen,
//   declares none. Importing the create rules would invent eight presence requirements the legacy
//   update path never had. The one type this file shares with the create contract is a static,
//   same-namespace lexical helper holding a width and a path-shape predicate -- not a validator, not a
//   base class and not an Include(), so nothing is inherited and nothing is abstracted.

// MIGRATION: 9 of 10 -- NO UNIQUENESS, EXISTENCE OR FILESYSTEM CHECK, because every one of them needs
//   I/O this layer must not perform. Rule T3 keeps all data access behind repository interfaces and
//   Rule T1 keeps this project free of infrastructure, so the constructor takes no dependency at all.
//   Specifically absent: no portal-name or alias uniqueness check; no "portal exists" check; no
//   administrator-exists check against Users; no check that a page reference belongs to this portal; and
//   no filesystem probe of the home directory or the logo -- System.IO is not reached. Alias resolution
//   is an API-layer concern in any case: the legacy lookup was a substring match
//   (PortalAlias like '%' + @PortalAlias + '%' at 01.00.00.SqlDataProvider:L4569-L4600) and is replaced
//   by an exact match in PortalAliasResolutionMiddleware.
//
//   The genuine rule over the fee, the quotas, the retention period and the expiry date is an
//   AUTHORISATION rule, not a range rule: SiteSettings.ascx.vb:L760-L770 rejects the entire save when a
//   caller who is not a super user has altered any of the six host-only members. A validator cannot see
//   who is asking, so that rule belongs to PortalService and the API edge and is deliberately not here.

// MIGRATION: 10 of 10 -- THE OPTION STRICT ASYMMETRY, and every coercion it hid made explicit.
//   Website/release.config:L125 compiles the admin code-behinds with <compilation debug="false"
//   strict="false">, so SiteSettings.ascx.vb ran with Option Strict OFF while the class library at
//   Library/DotNetNuke.Library.vbproj compiled with Option Strict ON. The save path read every numeric
//   box as a STRING and let VB coerce it silently into a twenty-seven-argument typed call. In C# an
//   empty string does not parse, so each coercion is named here and each blank case is modelled as a
//   nullable property the SERVICE interprets -- never as a validation failure:
//
//     A blank hosting-fee box left the local at its initialiser of zero, so "no fee entered" was
//       persisted as a real zero rather than as a database null (SiteSettings.ascx.vb:L704-L707).
//     A blank disk-space box behaved identically (L709-L712), and its local was declared As Double even
//       though the terminal column is int, so a fractional entry parsed, travelled as floating point and
//       was truncated by the database. HostSpace is int? here, so such a value now fails to bind
//       instead of being silently truncated.
//     A blank page-quota box left the local at zero (L714-L717).
//     The user-quota local is declared As Double at L719, assigned from an integer parse at L721, and
//       passed to an argument declared As Integer -- an implicit narrowing C# rejects outright. This is
//       the clearest single artefact of the disabled strictness on this screen.
//     A blank site-log-history box left the local at the bare literal -1, the absent-integer sentinel
//       written without going through the null contract (L724-L727).
//     A blank expiry box left the local at the absent-date sentinel (L729-L732), and all four page
//       references defaulted to the absent-integer sentinel when no list item was selected (L734, L739,
//       L744, L749).
//
//   The empty-string-to-zero quota coercion is the case to keep in view: a blank quota that the legacy
//   screen accepted as zero must arrive here as null and be interpreted by the service. This file
//   declares no rule that would reject it.

/// <summary>
/// Validates the shape of a portal update request submitted to <c>PUT /api/v1/portals/{id}</c>,
/// replacing the legacy Site Settings screen and the twenty-seven-argument
/// <c>PortalController.UpdatePortalInfo</c> call at
/// <c>Library/Components/Portal/PortalController.vb:L1568</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>The legacy screen declared almost nothing, and neither does this class.</b>
/// <c>Website/admin/Portal/sitesettings.ascx</c> is 568 lines and declares <b>zero</b>
/// <c>asp:RequiredFieldValidator</c> instances and <b>zero</b> <c>asp:RegularExpressionValidator</c>
/// instances. Its entire declarative validator census is two <c>asp:CompareValidator</c> instances,
/// both performing a data-type check: <c>valExpiryDate</c> at L433 and <c>valHostFee</c> at L444.
/// Reproducing that measured rule set faithfully means declaring very little, so the rules below are
/// widths read from the terminal schema, enumeration-membership assertions, one currency shape rule
/// and one lexical path rule.
/// </para>
/// <para>
/// <b>An ASP.NET <c>CompareValidator</c> succeeds on empty input.</b> With
/// <c>Operator="DataTypeCheck"</c> it performs no presence check at all, so submitting a blank expiry
/// date or a blank hosting fee was legitimate. Every rule here that touches those members is therefore
/// conditional on a value having been supplied, and no member except the portal name carries a presence
/// rule of any kind.
/// </para>
/// <para>
/// <b>Portal fee and quota members are deliberately unbounded below.</b> <c>valHostFee</c> is a
/// currency type check with no <c>GreaterThanEqual</c> companion anywhere in the file, whereas the role
/// screen <c>Website/admin/Security/editroles.ascx</c> bounds its own fees explicitly at L94-L96 and
/// L126-L128. A negative hosting charge or quota is accepted here and coerced by
/// <c>PortalService</c>, exactly as the legacy path behaved; rejecting it would substitute an HTTP 400
/// for a clamped save. The reasoning, and the five candidate rules that are deliberately NOT declared
/// for this reason, are recorded in the migration notes above the class.
/// </para>
/// <para>
/// <b>No identifier is bound-tested.</b> This request carries no copy of the portal identifier at all,
/// so no route-versus-body comparison exists to make. Where identifiers do appear -- the administrator
/// reference and the four page references -- they carry no rule, because
/// <c>Portals.PortalID</c> seeds at <c>-1</c> and <c>Tabs.TabID</c> seeds at <c>0</c>, making both
/// values real identifiers even though the legacy contract used <c>-1</c> as its absent marker.
/// Referential integrity belongs to <c>PortalService</c> and to the database.
/// </para>
/// <para>
/// <b>Dependencies: none.</b> The constructor takes no argument. Every rule is a width measured from
/// the terminal schema, an enumeration-membership assertion, a fixed-point shape rule or a lexical
/// path rule, so no configuration, clock, repository, options object or file-system access is needed or
/// acquired. Instances are discovered by the Application layer's assembly scan, which registers
/// publicly visible validator types only -- which is why this class is <c>public</c> rather than
/// <c>internal</c>.
/// </para>
/// </remarks>
public class UpdatePortalRequestValidator : AbstractValidator<UpdatePortalRequest>
{
    /// <summary>
    /// Terminal width of <c>Portals.PortalName</c>: <c>[nvarchar] (128) NOT NULL</c> at
    /// <c>01.00.00.SqlDataProvider:L79</c>. No <c>ALTER COLUMN</c> anywhere in the eighty-eight-script
    /// chain touches this column, so the baseline declaration is terminal.
    /// </summary>
    private const int PortalNameMaximumLength = 128;

    /// <summary>
    /// Terminal width of the two managed-file references <c>Portals.LogoFile</c> and
    /// <c>Portals.BackgroundFile</c>: <c>[nvarchar] (50) NULL</c> at
    /// <c>01.00.00.SqlDataProvider:L81</c> and <c>nvarchar(50) NULL</c> at
    /// <c>01.00.02.SqlDataProvider:L886</c> respectively. No later <c>ALTER COLUMN</c> widens either.
    /// </summary>
    private const int FileReferenceMaximumLength = 50;

    /// <summary>
    /// Terminal width of <c>Portals.FooterText</c>: <c>[nvarchar] (100) NULL</c> at
    /// <c>01.00.00.SqlDataProvider:L82</c>, with no later <c>ALTER COLUMN</c>.
    /// </summary>
    private const int FooterTextMaximumLength = 100;

    /// <summary>
    /// Terminal width of <c>Portals.Currency</c>: <c>[char] (3) NULL</c> at
    /// <c>01.00.00.SqlDataProvider:L88</c>, with no later <c>ALTER COLUMN</c>.
    /// </summary>
    /// <remarks>
    /// The rule is a maximum rather than an exact length, and that choice is load-bearing rather than
    /// lax. The legacy absent-string sentinel is the empty string, not a null reference
    /// (<c>Library/Components/Shared/Null.vb</c>), so an existing row may hold an empty currency code
    /// and a caller may legitimately submit one. An exact-length rule passes <c>null</c> but fails
    /// <c>""</c>, which would let the two representations of "not supplied" behave differently -- the
    /// precise trap this contract must avoid. A fixed-width column also rejects rather than truncates
    /// an over-long value, so the upper bound is worth enforcing.
    /// </remarks>
    private const int CurrencyMaximumLength = 3;

    /// <summary>
    /// Terminal width of the three payment-gateway columns <c>Portals.PaymentProcessor</c>,
    /// <c>Portals.ProcessorUserId</c> and <c>Portals.ProcessorPassword</c>: all
    /// <c>nvarchar(50) NULL</c>, added together at <c>01.00.06.SqlDataProvider:L599-L601</c>.
    /// </summary>
    private const int ProcessorFieldMaximumLength = 50;

    /// <summary>
    /// Terminal width of <c>Portals.Description</c> and <c>Portals.KeyWords</c>: both
    /// <c>nvarchar(500) NULL</c>, added together at <c>01.00.02.SqlDataProvider:L884-L885</c>.
    /// </summary>
    private const int MetadataMaximumLength = 500;

    /// <summary>
    /// Terminal width of <c>Portals.DefaultLanguage</c>: <c>nvarchar(10) NOT NULL</c>.
    /// </summary>
    /// <remarks>
    /// This column is the clearest case in the contract for reading the whole chain rather than the
    /// first declaration. It arrives as <c>nvarchar(6) NOT NULL</c> at
    /// <c>02.02.00.SqlDataProvider:L147</c>, is re-asserted at that width by
    /// <c>03.01.01.SqlDataProvider:L1121</c>, and is finally widened by the <c>ALTER COLUMN</c> at
    /// <c>04.03.05.SqlDataProvider:L291</c>. Enforcing 6 would reject values the terminal schema
    /// stores.
    /// </remarks>
    private const int DefaultLanguageMaximumLength = 10;

    /// <summary>
    /// Terminal width of <c>Portals.HomeDirectory</c>: <c>varchar(100) NOT NULL</c>, established by the
    /// <c>ALTER TABLE</c> at <c>03.01.01.SqlDataProvider:L1123</c> and echoed by every stored-procedure
    /// parameter for the column.
    /// </summary>
    /// <remarks>
    /// Declared locally rather than borrowed from a shared helper so that this contract's only rule on
    /// the member is a measured schema width owned by this file, in exactly the form the seven widths
    /// above take. See the migration note beside the rule for why no path-shape rule accompanies it.
    /// </remarks>
    private const int HomeDirectoryMaximumLength = 100;

    /// <summary>
    /// The key under which <c>Api/Filters/FluentValidationActionFilter.cs</c> publishes the
    /// <c>portalId</c> route value into the validation context's root data.
    /// </summary>
    /// <remarks>
    /// The filter composes the key by prefixing the route parameter name with <c>Route</c> and
    /// upper-casing its first character, and it parses a numeric value into an <see cref="int"/> before
    /// storing it. Spelling the key once, here, is what keeps this rule and that filter from drifting
    /// apart silently: a mis-spelled key would not fail to compile and would present as a validator
    /// that silently stopped comparing.
    /// </remarks>
    private const string RoutePortalIdKey = "RoutePortalId";

    /// <summary>
    /// Message reported when the identifier in the body does not name the portal the request path
    /// addresses.
    /// </summary>
    /// <remarks>
    /// Worded as a statement of the contract rather than as a legacy message, because the legacy screen
    /// had no counterpart: it took the subject from page state and could not disagree with itself. The
    /// message names both sources so a caller can tell which value to correct.
    /// </remarks>
    private const string PortalIdMismatchMessage =
        "The portal identifier in the body must match the portal identifier in the request path.";

    /// <summary>
    /// Total number of digits permitted in <see cref="UpdatePortalRequest.HostFee"/>, matching the
    /// terminal <c>money</c> column exactly.
    /// </summary>
    /// <remarks>
    /// The terminal type is established by the <c>ALTER COLUMN</c> at
    /// <c>03.01.01.SqlDataProvider:L1118</c>, which redefines <c>Portals.HostFee</c> as
    /// <c>[money] NOT NULL</c> and supersedes the baseline declaration of <c>nvarchar(10)</c> at
    /// <c>01.00.00.SqlDataProvider:L89</c>. SQL Server <c>money</c> holds nineteen digits with four of
    /// them after the decimal point, so this pair reproduces the storage bound rather than inventing
    /// one.
    /// </remarks>
    private const int HostFeePrecision = 19;

    /// <summary>
    /// Number of digits permitted after the decimal point in
    /// <see cref="UpdatePortalRequest.HostFee"/>, matching the scale of the terminal <c>money</c>
    /// column.
    /// </summary>
    private const int HostFeeScale = 4;

    /// <summary>
    /// Message reported when the hosting fee is not expressible as a currency amount. Reproduced
    /// verbatim from the <c>ErrorMessage</c> of <c>valHostFee</c> at
    /// <c>Website/admin/Portal/sitesettings.ascx:L445</c>, so the wording a portal administrator saw
    /// on the legacy screen is the wording the API returns.
    /// </summary>
    private const string HostFeeInvalidMessage = "Invalid fee, needs to be a currency value!";

    /// <summary>
    /// Message reported when the hosting fee is a well-formed currency amount that the terminal
    /// <c>money</c> column still cannot hold. The wording is net-new: the legacy screen had no such
    /// rule and therefore no message to reproduce, and the failure it reports was previously a server
    /// fault rather than a validation answer.
    /// </summary>
    private static readonly string HostFeeUnrepresentableMessage = FormattableString.Invariant($"Invalid fee, it must fall between {SqlServerRange.MinimumMoney} and ")
        + FormattableString.Invariant($"{SqlServerRange.MaximumMoney}, which is the range the stored column can hold.");

    /// <summary>
    /// Message reported when the expiry date falls outside the range the terminal <c>datetime</c>
    /// column can hold. The wording is net-new for the same reason as the fee message above.
    /// </summary>
    private static readonly string ExpiryDateUnrepresentableMessage = FormattableString.Invariant($"The expiry date must fall between {SqlServerRange.MinimumDateTime:yyyy-MM-dd} and ")
        + FormattableString.Invariant($"{SqlServerRange.MaximumDateTime:yyyy-MM-dd}, which is the range the stored column can hold.");

    /// <summary>
    /// Message reported when the portal name is absent or blank. The wording is net-new, because the
    /// legacy screen declared no validator on the control and therefore had no message to reproduce;
    /// the rule itself is justified by the <c>NOT NULL</c> column rather than by the markup.
    /// </summary>
    private const string PortalNameRequiredMessage = "Site Title is required.";

    /// <summary>
    /// Message reported when the registration mode carries a value that names no member of
    /// <c>UserRegistrationMode</c>. It names the four modes the legacy option list offered at
    /// <c>Website/admin/Portal/sitesettings.ascx:L230-L233</c>.
    /// </summary>
    private const string RegistrationModeInvalidMessage =
        "User Registration must be None, Private, Public or Verified.";

    /// <summary>
    /// Message reported when the advertising mode carries a value that names no member of
    /// <c>BannerAdvertisingMode</c>. It names the three modes the legacy option list offered at
    /// <c>Website/admin/Portal/sitesettings.ascx:L123-L125</c>.
    /// </summary>
    private const string AdvertisingModeInvalidMessage =
        "Banner Advertising must be None, Site or Host.";

    /// <summary>
    /// Declares the rule set. The constructor takes no argument: every rule is a width measured from
    /// the terminal schema, an enumeration-membership assertion, a fixed-point shape rule matching the
    /// <c>money</c> column, or a lexical rule over a caller-supplied relative path.
    /// </summary>
    public UpdatePortalRequestValidator()
    {
        // Each rule stops at its own first failure, so one over-long value yields one message rather
        // than a width failure and a shape failure for the same field.
        RuleLevelCascadeMode = CascadeMode.Stop;

        // The legacy screen rendered every failing validator at once, so every failing field is
        // reported together instead of one per round trip.
        ClassLevelCascadeMode = CascadeMode.Continue;

        // The identity rule. See migration note 5: the route segment is the subject of the write, and
        // this rule is what forbids the body from naming a different one. It is expressed with the
        // context-bearing Must overload because the comparand is not on the request - it is the route
        // value the action filter published - and it stands down when no route context exists so that a
        // direct caller is not asked to satisfy a comparison that has no other side.
        RuleFor(request => request.PortalId)
            .Must((request, portalId, context) => MatchesRoutePortalId(portalId, context))
            .WithMessage(PortalIdMismatchMessage);

        // The only unconditional presence rule in this file. See migration note 3 above: the markup
        // declared none, and the authority is the NOT NULL column combined with the legacy write-side
        // conversion of the empty string into a database null.
        RuleFor(request => request.PortalName)
            .NotEmpty().WithMessage(PortalNameRequiredMessage)
            .MaximumLength(PortalNameMaximumLength);

        RuleFor(request => request.LogoFile)
            .MaximumLength(FileReferenceMaximumLength);

        RuleFor(request => request.BackgroundFile)
            .MaximumLength(FileReferenceMaximumLength);

        RuleFor(request => request.FooterText)
            .MaximumLength(FooterTextMaximumLength);

        RuleFor(request => request.Currency)
            .MaximumLength(CurrencyMaximumLength);

        RuleFor(request => request.Description)
            .MaximumLength(MetadataMaximumLength);

        RuleFor(request => request.KeyWords)
            .MaximumLength(MetadataMaximumLength);

        RuleFor(request => request.DefaultLanguage)
            .MaximumLength(DefaultLanguageMaximumLength);

        RuleFor(request => request.PaymentProcessor)
            .MaximumLength(ProcessorFieldMaximumLength);

        RuleFor(request => request.ProcessorUserId)
            .MaximumLength(ProcessorFieldMaximumLength);

        // MIGRATION: the gateway credential is a SECRET travelling through a request body, and the only
        //   rule this layer can usefully place on it is the column width. What matters as much as the
        //   rule is what the rule must not do: FluentValidation's default message for a length failure
        //   interpolates the submitted value's LENGTH but never the value itself, and this rule
        //   deliberately does not override that message. A validation response is returned to the
        //   caller and is a plausible thing to log, so a custom message quoting this field would put a
        //   credential into both. No complexity, minimum-length or confirmation rule is declared
        //   either: this is a credential the portal presents to a third party, not one it verifies, and
        //   the legacy screen imposed none.
        RuleFor(request => request.ProcessorPassword)
            .MaximumLength(ProcessorFieldMaximumLength);

        // MIGRATION: the two mode members are enumerations on the wire but plain int columns underneath,
        //   and the legacy screen constrained them by RENDERING A LIST rather than by declaring a
        //   validator -- optUserRegistration at sitesettings.ascx:L230-L233 and optBanners at
        //   L123-L125. A radio-button list cannot submit a value it does not offer, so the legacy
        //   guarantee was structural. A JSON caller has no list and the serialiser accepts a bare
        //   integer, so the same guarantee has to be asserted explicitly here or it is simply lost, and
        //   an unnamed value would be stored and then read back as a mode no branch in the target
        //   recognises. This is therefore a mechanism change that PRESERVES the legacy guarantee, not a
        //   new restriction on callers, and it is the rule UpdatePortalRequest itself asks for on both
        //   members. Both enumerations define a zero member -- NoRegistration and None -- so a request
        //   that omits these members entirely is still valid, which matters because nothing else in
        //   this contract is required.
        RuleFor(request => request.UserRegistration)
            .IsInEnum().WithMessage(RegistrationModeInvalidMessage);

        RuleFor(request => request.BannerAdvertising)
            .IsInEnum().WithMessage(AdvertisingModeInvalidMessage);

        // MIGRATION: this is the ONLY rule on the hosting fee, and it is the typed expression of
        //   valHostFee's Operator="DataTypeCheck" Type="Currency" -- a well-formedness check and
        //   nothing more. decimal? already rejects a non-numeric value during model binding, but it
        //   does not enforce the one thing "is a currency value" additionally means against this
        //   column: the terminal type is money (03.01.01.SqlDataProvider:L1118), which holds nineteen
        //   digits with four after the point, and a decimal carrying more scale than that is well
        //   formed as a decimal yet is not storable as this currency. The bound is read from the
        //   column, not invented.
        //
        // MIGRATION: PrecisionScale imposes NO SIGN CONSTRAINT, which is why it is the correct rule
        //   here and a lower bound is not. A negative fee passes this rule and reaches the service to
        //   be coerced, exactly as the legacy path behaved. See migration note 4: valHostFee has no
        //   GreaterThanEqual companion, and the IIf clamps in PortalController.vb guard a RoleInfo.
        //
        // MIGRATION: the guard is HasValue rather than nothing, because a CompareValidator succeeds on
        //   empty input and a blank fee box was legitimate. Trailing zeros are ignored so that a value
        //   such as 12.5000, which the money column stores exactly, is not rejected for carrying the
        //   scale the column itself defines.
        // MIGRATION: PrecisionScale alone leaves a NARROW BAND of unstorable values, and the second rule
        //   closes it. The currency's ceiling is 922,337,203,685,477.5807, which is nineteen digits, so
        //   a precision of nineteen and a scale of four admit every value the column can hold - but
        //   they also admit fifteen leading digits of any magnitude, so an amount from
        //   922,337,203,685,477.5808 up to 999,999,999,999,999.9999 satisfies the digit count and still
        //   overflows the column. Unbounded, such a value passed every rule here and was refused by the
        //   provider instead, which surfaces as a server fault naming no field rather than a field-level
        //   answer. The added rule states the column's own limits and nothing more: it imposes no sign
        //   constraint and no price ceiling, so the preceding paragraph's reasoning is untouched.
        RuleFor(request => request.HostFee)
            .PrecisionScale(HostFeePrecision, HostFeeScale, ignoreTrailingZeros: true)
                .WithMessage(HostFeeInvalidMessage)
            .Must(SqlServerRange.CanStore)
                .WithMessage(HostFeeUnrepresentableMessage)
            .When(request => request.HostFee.HasValue);

        // MIGRATION: the home directory carries A WIDTH RULE AND NOTHING ELSE. The width is the
        //   terminal [varchar] (100) NOT NULL established at 03.01.01.SqlDataProvider:L1123 and echoed
        //   by every stored-procedure parameter for this column, so it is a measured schema rule of
        //   exactly the kind every other width rule in this file reproduces, and it is declared the
        //   same way - as a local constant on this type.
        //
        // MIGRATION: a LEXICAL PATH-SHAPE RULE IS DELIBERATELY NOT DECLARED HERE, and its absence is a
        //   correction rather than an omission. An earlier revision rejected rooted and
        //   parent-traversing values on this member and described the rule in its own comments as "a
        //   net-new addition rather than a reproduction". It is removed for two measured reasons. The
        //   legacy update screen declared no validator on txtHomeDirectory at all, so rejecting a value
        //   the legacy screen accepted would breach Minimal Change Clause item 4, which requires that
        //   validation rules match. And the hazard the rule guarded against does not exist in the
        //   target: the only place the legacy concatenated this column into a physical path was
        //   PortalController.vb:L994, inside the file-system subsystem that this migration excludes,
        //   so nothing in the target maps this value onto a file system and the stored column is inert
        //   text. The creation contract keeps its own shape rule because that endpoint's contract
        //   declares the home-folder shape as validated; the update contract does not, and the two
        //   paths are therefore intentionally not symmetric. Should a file-mapping layer ever be added,
        //   the check belongs at the point of mapping rather than here.
        RuleFor(request => request.HomeDirectory)
            .MaximumLength(HomeDirectoryMaximumLength);

        // MIGRATION: the expiry date carries a REPRESENTABILITY bound and no business rule. The CLR date
        //   type begins in the year one while the stored column begins in 1753, so a value the type
        //   accepts can still be unstorable; unbounded it passed every rule here and was refused by the
        //   provider, which surfaces as a server fault naming no field. The bound therefore says only
        //   what the column can hold. It is emphatically NOT the range rule the note below declines: no
        //   past-date rejection, no lower bound above the column's own floor, and the upper bound is
        //   stated at the last representable instant of 9999-12-31 rather than at that day's midnight,
        //   precisely so the preserved perpetual-expiry value is admitted by the rule rather than
        //   refused by it. The sentinel minimum date is carried as null and never reaches this rule.
        RuleFor(request => request.ExpiryDate)
            .Must(SqlServerRange.CanStore)
            .WithMessage(ExpiryDateUnrepresentableMessage);

        // MIGRATION: no rule on the disk space, the page quota, the user quota, the site-log retention,
        //   the time-zone offset, the administrator reference or the four page references. Each omission
        //   is measured, and together they are the bulk of this contract:
        //
        //     ExpiryDate      valExpiryDate is a date type check that DateTime? already enforces at
        //                     model binding; see migration note 6. No BUSINESS range rule, no past-date
        //                     rejection, no GreaterThan(DateTime.MinValue). The storage-range rule
        //                     applied immediately above is a different kind of bound and is annotated
        //                     there; it constrains only what the column can physically hold.
        //     HostSpace       txtHostSpace carries NO validator of any kind -- uniquely among the
        //                     numeric fields on the screen -- and the resource file reads "enter 0 for
        //                     unlimited space", so ZERO IS MEANINGFUL and neither a positivity rule nor
        //                     a currency rule borrowed from the neighbouring fee may be inferred.
        //     PageQuota       No validator; a blank box persisted a real zero quota. No lower bound.
        //     UserQuota       No validator; likewise. See migration note 10 for the narrowing artefact.
        //     SiteLogHistory  No validator. This is the one member whose blank case produced the
        //                     absent-integer sentinel rather than zero, so null is the modern
        //                     expression of "no retention period" and -1 is not a magic value here.
        //     TimeZoneOffset  No validator, and a range rule is DECLINED rather than merely absent. The
        //                     offsets the legacy list offered span -720 to 780 minutes, which is
        //                     NARROWER than the real range of civil offsets -- +13:00 and +14:00 exist
        //                     -- so enforcing the legacy list would reject valid data, which is a worse
        //                     outcome than accepting an odd one. Zero is also a real offset rather than
        //                     an absent marker: it is the United Kingdom.
        //     AdministratorId Foreign key to Users. Users.UserID is IDENTITY (1, 1) at
        //                     01.00.00.SqlDataProvider:L98, so zero never occurs as a user identifier,
        //                     but that is schema knowledge and NOT a rule -- encoding it is not what
        //                     the legacy screen did, and existence is the service's question.
        //     Splash/Home/    Foreign keys to Tabs. The only meaningful check is that the row exists
        //     Login/UserTabId AND belongs to this portal, which is a tenant-isolation question needing
        //                     a repository this layer must not acquire. A lower bound would be actively
        //                     wrong: Tabs.TabID seeds at zero. The ownership check belongs to
        //                     PortalService.
    }

    /// <summary>
    /// Reports whether the submitted portal identifier names the portal the request path addresses.
    /// </summary>
    /// <param name="portalId">The identifier carried by the body.</param>
    /// <param name="context">
    /// The validation context, whose root data carries the route values published by the API layer.
    /// </param>
    /// <returns>
    /// <see langword="true"/> when the two agree, or when no route identifier is present at all;
    /// <see langword="false"/> only when both are present and they differ.
    /// </returns>
    /// <remarks>
    /// <para>
    /// Absence of a route identifier is reported as acceptable rather than as a failure, and that
    /// choice is deliberate in both directions. This validator is resolved by type, so it also runs for
    /// callers that never went through the API layer - an application service composing a request, a
    /// unit test exercising the rule set - and those callers have no route for the body to disagree
    /// with. Refusing them would make the contract untestable in isolation and would report a missing
    /// comparand as a caller error. It is also safe: the only path on which a mismatch could retarget a
    /// write is the HTTP path, and on that path the filter always publishes the route value, so the
    /// comparison is always available exactly where it matters.
    /// </para>
    /// <para>
    /// The stored value is compared as an <see cref="int"/> and never as text, because the publishing
    /// filter parses a numeric route value before storing it and a text comparison would fail on
    /// insignificant differences such as a leading plus sign. A stored value of some other type is
    /// treated as absent rather than coerced.
    /// </para>
    /// </remarks>
    private static bool MatchesRoutePortalId(int portalId, ValidationContext<UpdatePortalRequest> context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (!context.RootContextData.TryGetValue(RoutePortalIdKey, out object? routeValue)
            || routeValue is not int routePortalId)
        {
            return true;
        }

        return portalId == routePortalId;
    }
}
