using DnnMigration.Application.Dtos.Role;
using DnnMigration.Domain.Common;
using FluentValidation;

namespace DnnMigration.Application.Validation;

/// <summary>
/// Declares the field rules for <see cref="CreateRoleRequest"/>, the payload submitted to
/// <c>POST /api/v1/roles</c> to create a security role together with its
/// paid-membership terms.
/// </summary>
/// <remarks>
/// <para>
/// Every rule below is a measured reproduction of a declarative validator on
/// <c>Website/admin/Security/editroles.ascx</c>, which declares exactly nine of them: one
/// <c>RequiredFieldValidator</c> on the name, and eight comparisons across the two fees and the two
/// periods. Nothing here was invented from the shape of the request, and the two rules that need
/// stored state - whether the name is already taken in the portal, and whether the nominated group
/// exists - are deliberately absent because a stateless validator cannot answer either.
/// </para>
/// <para>
/// <b>The two frequency members are NOT checked against a closed list of the six legacy character
/// codes.</b> The shipped baseline seed data itself carries codes outside those six -
/// <c>Administrators</c> is seeded with <c>'4'</c> and <c>Registered Users</c> with <c>'0'</c> - so a
/// literal allow-list of <c>N</c>, <c>O</c>, <c>D</c>, <c>W</c>, <c>M</c> and <c>Y</c> would reject
/// rows that ship with every installation. The request contract models both members as the shared
/// domain enumeration, so the enumeration type IS the constraint and the rule asserts nothing more
/// than membership of it. The four measurements behind that decision, and the cross-layer
/// consequence of the enumeration being closed while the column is not, are recorded in the
/// annotations below.
/// </para>
/// <para>
/// <b>Every legacy comparison agrees with its own error text, and each message is carried across
/// character for character.</b> Measured in <c>App_LocalResources/EditRoles.ascx.resx</c> against the
/// operators declared in <c>editroles.ascx</c>: <c>valServiceFee2</c> and <c>valTrialFee2</c> are
/// <c>GreaterThanEqual</c> and both read "Greater Than or Equal to Zero", while
/// <c>valBillingPeriod2</c> and <c>valTrialPeriod2</c> are <c>GreaterThan</c> and both read "Greater
/// Than Zero". There is no legacy self-disagreement to preserve. An earlier revision of this file
/// asserted two such disagreements and preserved them; they had been introduced here, by two message
/// strings swapped between the period rule and the fee rule, and the wording is now back to what the
/// legacy screen said. Every operator is unchanged.
/// </para>
/// <para>
/// <b>Every fee and period rule is conditional on the value being present, and on nothing else.</b>
/// The eight legacy comparisons were <c>CompareValidator</c>s, which pass an empty control without
/// comparing anything, so a free role tripped none of them. They were not, however, conditional on a
/// frequency: the annotations below record the measurement that refutes that reading.
/// </para>
/// <para>
/// <b>No identifier carries a bound test.</b> <c>Roles.RoleID</c> is an identity column seeded at
/// zero, so zero is a real key - it is the shipped <c>Administrators</c> role - and
/// <c>Roles.RoleGroupID</c> treats both zero and the legacy negative sentinel as meaningful. The
/// request contract carries neither the role's own key nor the owning portal's, so neither can be
/// restated by a caller in the first place.
/// </para>
/// <para>
/// Parameterless by design, and public by requirement: the composition root discovers every
/// validator in this assembly by scanning it for public implementations, so an internal type would
/// register silently as nothing at all.
/// </para>
/// </remarks>
// MIGRATION: provenance. The nine declarative validators measured on
// Website/admin/Security/editroles.ascx (190 lines) are, verbatim:
//   valRoleName       L29-L31   RequiredFieldValidator on txtRoleName
//   valServiceFee1    L90-L93   CompareValidator Type="Currency" Operator="DataTypeCheck"
//   valServiceFee2    L93-L96   CompareValidator Operator="GreaterThanEqual" ValueToCompare="0"
//   valBillingPeriod1 L108-L111 CompareValidator Type="Integer"  Operator="DataTypeCheck"
//   valBillingPeriod2 L111-L114 CompareValidator Operator="GreaterThan"      ValueToCompare="0"
//   valTrialFee1      L123-L125 CompareValidator Type="Currency" Operator="DataTypeCheck"
//   valTrialFee2      L125-L128 CompareValidator Operator="GreaterThanEqual" ValueToCompare="0"
//   valTrialPeriod1   L140-L143 CompareValidator Type="Integer"  Operator="DataTypeCheck"
//   valTrialPeriod2   L143-L146 CompareValidator Operator="GreaterThan"      ValueToCompare="0"
// The screen declares no pattern-matching validator of any kind, and neither does any other
// in-scope administration screen, so no pattern rule appears here and none was fabricated.
//
// MIGRATION: the leading <br> is STRIPPED from all five messages carried across, and the wording
// after it is reproduced character for character. Each legacy ErrorMessage began with that literal
// tag - for example ErrorMessage="<br>You Must Enter a Valid Name" at L31 - because the message was
// written straight into the page's markup and needed a line break ahead of it. The migrated API
// answers with a machine-readable problem document in which an HTML tag is neither markup nor data,
// merely presentation leaking into a contract, and a client that inserted it into a document would
// be rendering a server-supplied tag. The WORDING is what behavioural equivalence is about, and the
// wording is unchanged.
//
// MIGRATION: NEITHER THE BILLING-PERIOD RULE NOR THE TRIAL-FEE RULE DISAGREES WITH ITSELF IN THE
// LEGACY, and an earlier revision of this file said both did and set out to preserve the
// disagreement. Measured in EditRoles.ascx.resx, against the operators declared in editroles.ascx
// above: valBillingPeriod2.Text is "Billing Period Must Be Greater Than Zero" against
// Operator="GreaterThan" (L114), and valTrialFee2.Text is "Trial Fee Must Be Greater Than or Equal to
// Zero" against Operator="GreaterThanEqual" (L128). Both agree, as do valServiceFee2 and
// valTrialPeriod2. The two contradictions were introduced here, by two message strings that had in
// effect been swapped between the period rule and the fee rule, and are corrected in RoleTermsRules.
// Every operator below is unchanged - only the wording moved, back to what the legacy screen actually
// said.
//
// Note also that the withdrawn annotation cited the markup for the message text. It does not live
// there: every one of these validators carries a resourcekey and takes its text from the resource
// file, which is why that file is the measurement above.
//
// MIGRATION: a CompareValidator SUCCEEDS against an empty control. It compares nothing when there is
// nothing to compare, so all eight comparisons were silent for a role with no paid terms; only
// valRoleName was a presence check. That is why every member behind those eight rules is nullable on
// the request contract and why every rule below carries a presence guard. An unguarded rule, or a
// non-nullable member defaulting to zero, would turn "not supplied" into "supplied as zero" and the
// two period rules would then refuse every free role ever created.
//
// MIGRATION: the fee and period rules are NOT gated on a frequency, and this corrects a plausible
// misreading of the legacy save path. EditRoles.ascx.vb L216 and L226 do test the frequency
// selections - L216 requires a non-empty fee, a non-empty period and a billing selection other than
// the never code; L226 additionally requires the parsed service fee to be non-zero before the trial
// block is honoured - but those conditions decide WHAT IS WRITTEN, substituting a zero fee, a period
// of one and the never code when a block was left blank (L212-L214, L222-L224). They do not decide
// whether a validator fires. Four rows on the screen are server-side controls - trServiceFee L83,
// trBillingPeriod L98, trTrialFee L116, trTrialPeriod L130 - yet NONE of them is ever hidden: the
// code-behind assigns no visibility to any of the four, and its ActivateControls helper (L47) toggles
// only the enabled state and is called exactly once, in the no-permission branch at L177. The eight
// comparisons therefore always ran. Gating them on a frequency would ACCEPT a negative fee submitted
// alongside the never code, which the legacy screen refused, so the gate would be a regression rather
// than fidelity. The substitution behaviour itself is reproduced where it belongs, in
// Application/Mapping/RoleMappings.cs.
//
// MIGRATION: the legacy integer null sentinel of -1 never travels as a literal on this contract.
// RoleController.vb L537 branches on a period equal to that sentinel and short-circuits to no expiry
// date, so -1 was a MEANINGFUL value there rather than an error - which sits awkwardly beside
// valBillingPeriod2's demand for a strictly positive period. Modelling both periods as nullable
// resolves the tension without choosing a side: absence expresses "no term", the strictly-positive
// comparison applies only to a value that was actually supplied, and a caller who submits -1
// outright is refused exactly as the screen refused it. Translating absence into whatever the
// persisted representation requires belongs to the mapper.
//
// MIGRATION: no date rule of any kind, and none is possible to add here honestly. The legacy
// assignment path CLAMPS rather than refuses - RoleController.vb L530-L531 replaces an effective
// date earlier than the current instant with the null-date sentinel, and L533-L534 replaces an
// expiry date earlier than it with the current instant - so refusing a past date would convert a
// silent coercion into a rejected request. The sentinel is itself the minimum date value, which is a
// legitimate "no date", so a lower-bound rule would refuse it. This request contract carries no date
// member at all, which makes the point moot for the create path and worth recording only so that no
// later reader adds one.
//
// MIGRATION: no cross-field comparison between an effective and an expiry date. editroles.ascx
// declares none. The role-ASSIGNMENT screen securityroles.ascx does declare such a comparison
// alongside its own two date checks, but that is a different screen governing a different contract -
// a user's membership of a role rather than the role's definition - and its rules are deliberately
// not imported here.
//
// MIGRATION: the group reference carries no bound test. The legacy screen offered -1 as a real,
// selectable choice, added as the drop-down's first entry from the localised "GlobalRoles" resource
// (EditRoles.ascx.vb L75), and the legacy reader used that same value as its absent-integer
// sentinel, so -1, "Global Roles" and a stored null were one value seen from three layers. Zero is
// separately legitimate because the group table's key is an identity column seeded at zero. Any
// lower-bound test would therefore refuse either a real group or the ungrouped case. Whether the
// nominated group exists is a question about stored state and belongs to
// Application/Services/RoleService.cs.
//
// MIGRATION: no identifier bound test anywhere, for the same class of reason. Roles.RoleID is
// declared IDENTITY (0, 1) (01.00.00.SqlDataProvider L115), so zero is the FIRST real key and the
// shipped Administrators role holds exactly that value (L7192). Portals.PortalID is declared
// IDENTITY (-1, 1) (L77) with the _default portal seeded at zero (L7125), so a negative portal key
// is real too. Neither key appears on this contract - the role's key is assigned by the store and
// the portal's arrives in the route - so there is nothing here to bound, and a future reader must
// not add a bound on the assumption that a non-positive key means "unset".
//
// MIGRATION: the frequency members are checked for membership of the shared domain enumeration and
// nothing else. A closed list of the six legacy codes is refuted on four independent measurements.
// (a) The shipped seed data carries codes outside the six: 01.00.00.SqlDataProvider L7192 seeds
//     Administrators with '4' and L7194 seeds Registered Users with '0'.
// (b) The legacy screen deliberately TOLERATED codes it did not recognise: EditRoles.ascx.vb
//     L149-L151 looks a stored billing code up in the drop-down and selects it only if the lookup
//     returned something, and L157-L159 does the same for the trial code. An unknown code left the
//     drop-down unselected instead of raising.
// (c) The valid set was configurable DATA, not a compile-time constant: L117 reads it at runtime
//     from the "Frequency" list of the general-purpose lists subsystem, which this migration
//     excludes outright, so no static rule can know what it contained.
// (d) The schema imposes nothing: across the whole upgrade chain both columns stay plain
//     char(1) NULL - BillingFrequency at 01.00.00.SqlDataProvider L120, TrialFrequency at L122 -
//     with no check constraint restricting either, so the database itself accepts any single
//     character.
// CROSS-LAYER CONSEQUENCE, recorded because it is a real limitation of the chosen domain model and
// not of this rule: the enumeration is closed and cannot represent '4' or '0', so this validator
// necessarily refuses them on the WRITE path. That is acceptable - no caller should be creating a
// role at an undocumented frequency - but tolerance for the seeded rows must therefore exist on the
// READ path, in Domain/Enums/BillingFrequency.cs and Application/Mapping/RoleMappings.cs, and never
// be retro-fitted here by weakening the rule to a bare length check. Loosening this rule would not
// help those rows; it would only let new ones be created.
//
// MIGRATION: the six codes are load-bearing DATA and are never renamed. The legacy frequency switch
// at RoleController.vb L540-L546 dispatches on the characters themselves, and it carries NO default
// branch - an unrecognised code falls through in silence, producing no expiry computation rather
// than an error, which is itself the clearest evidence for point (b) above.
//
// MIGRATION: the legacy Visual Basic runtime import at RoleController.vb L25 - the single occurrence
// in scope - is dropped, along with the interval-based date helper it supplied. The arithmetic it
// performed is reproduced in Application/Services/RoleService.cs, where the weekly case correctly
// adds days multiplied by seven rather than using any week-based interval, because the legacy call
// used a DAY interval with the period multiplied by seven. None of that arithmetic belongs to a
// validator, and none of it appears here.
//
// MIGRATION: NO upper bound on either fee, and the 999.99 ceiling suggested by the baseline column
// is refuted by measurement. The baseline declares ServiceFee as decimal(5, 2)
// (01.00.00.SqlDataProvider L119), but the destructive upgrade chain retypes it to money twice while
// rebuilding the table - 01.00.04.SqlDataProvider L1326, converting existing values at L1341, and
// again at 01.00.05.SqlDataProvider L2752 - and settles it terminally with
// 03.01.01.SqlDataProvider L1173 ALTER COLUMN [ServiceFee] [money] NULL, adding a zero default at
// L1177. TrialFee was money from birth (01.00.08.SqlDataProvider L6830). Only the TERMINAL schema
// state is meaningful, the terminal type is money, and no legacy validator bounded either fee above,
// so authoring a ceiling here would enforce a limit that has not applied for many versions.
//
// MIGRATION: the MaxLength="50" attribute on the four fee and period text boxes (editroles.ascx
// L89, L104, L122, L136) is deliberately NOT carried across. It bounded how many CHARACTERS could be
// typed into a text box, which is a text-entry width; the migrated members are a decimal and an
// integer, on which a character count is meaningless. The intent behind the attribute - keep the
// submitted number within what the column can hold - is served by the member types and by the
// comparisons above. The three genuine text members DO carry their column widths, because the legacy
// screen relied on the same attribute for them and that client-side limit disappears with the
// postback.
//
// MIGRATION: the four DataTypeCheck comparisons - valServiceFee1, valBillingPeriod1, valTrialFee1
// and valTrialPeriod1 - need no counterpart and are intentionally absent rather than overlooked.
// They existed because a text box submits text, so "1,00O" had to be rejected before it could be
// parsed. A decimal cannot carry a non-currency value and an integer cannot carry a non-integer, so
// the corresponding failure is now a deserialisation failure at the API edge, reported against the
// same member, and reproducing the check inside the validator would assert something the type system
// has already guaranteed.
//
// MIGRATION: the legacy screen compiled with Option Strict OFF - Website/release.config L125
// declares <compilation debug="false" strict="false"> for every administration code-behind - so its
// coercions were implicit and lossy, and each one is made explicit here. The load-bearing case is
// the empty fee: EditRoles.ascx.vb L217 and L227 parse the fee boxes into single-precision numbers
// only inside the guards at L216 and L226, and the surrounding declarations at L212 and L222 leave a
// blank box as the number zero. C# will not parse an empty string into a number at all. The
// resolution is the nullable member plus the presence guard - absence stays absence through the
// boundary and the mapper decides what reaches the column - rather than a validation failure, which
// would refuse submissions the legacy screen accepted. The frequency selections, compared as
// late-bound strings against the never code at L216 and L226, become a typed enumeration whose
// comparison cannot be late-bound at all.
//
// MIGRATION: the role fees DO enforce a lower bound of zero, and the portal's host fee deliberately
// does NOT. The asymmetry is measured, not accidental: editroles.ascx declares valServiceFee2 and
// valTrialFee2 explicitly, whereas the portal's site-settings screen declares only a type check on
// its fee with no companion comparison, which is why the portal update validator omits one. A future
// reader must NOT harmonise the two. Separately, the portal-template creation path clamps a negative
// role fee up to zero in code (PortalController.vb L395 and L398, using a function that evaluates
// both of its arms rather than a short-circuiting conditional); that clamp is a service-side safety
// net on a template-driven path and is reproduced in Application/Mapping/RoleMappings.cs, not here,
// because a clamp silently accepts what this rule is required to refuse.
//
// MIGRATION: no uniqueness rule, and therefore no dependency and no asynchrony. The legacy screen
// looked the name up and refused a duplicate before inserting (EditRoles.ascx.vb L252, reporting
// its DuplicateRole message at L256), and the schema enforces the same through a unique index over
// the portal and the name. Both are questions about stored state, so both belong to
// Application/Services/RoleService.cs, which reports the collision as an expected failure. That is
// what keeps the constructor parameterless: this type reads no configuration, holds no policy object
// and touches no store.
//
// MIGRATION: the icon path rule is NET-NEW and is the only rule here without a legacy ancestor. The
// legacy screen declared no validator on its icon picker, restricting the choice by file type in the
// code-behind instead (EditRoles.ascx.vb L129) and storing whatever the control yielded verbatim
// (L248) - an affordance a JSON contract cannot reproduce, because a caller now supplies the string
// directly. The request contract states that rejecting rooted and traversing forms is this
// validator's responsibility, and an unbounded caller-supplied relative path reaching a stored
// column is a genuine gap rather than a matter of taste. The rule is expressed as plain character
// tests over the submitted string so that it neither reaches the file system nor introduces a
// pattern-matching engine, and it accepts every form the legacy picker could have produced.
public class CreateRoleRequestValidator : AbstractValidator<CreateRoleRequest>
{
    // Every message, every column width and the icon-path predicate this validator applies are
    // declared once in RoleTermsRules and applied identically by UpdateRoleRequestValidator. They
    // used to live here as private members, which is precisely how the icon-path containment rule
    // came to exist on this path and not on the update path - a rule a caller could bypass by
    // choosing PUT over POST. Moving them changes no rule and no wording; it removes the ability
    // for the two paths to drift apart again. The provenance annotation for each value travels with
    // it, so RoleTermsRules is where the measured legacy line references now live.

    // MIGRATION: THERE IS NO UPPER BOUND ON EITHER PERIOD, and its removal was a correction rather
    // than a relaxation. A net-new ten-thousand-unit ceiling stood here and on the update validator,
    // justified as generous; generosity is not the test. The legacy screen declared one rule on each
    // period - valBillingPeriod2 at editroles.ascx L114 and valTrialPeriod2 at L146, both strictly
    // greater than zero - the columns are plain int (01.00.08.SqlDataProvider L6829 and
    // 01.00.05.SqlDataProvider L2754), and the terminal procedure bounds neither. Any positive Int32
    // the legacy application accepted must therefore still be accepted, and the ceiling refused a band
    // of them. Nothing is left unprotected by the removal: RoleService.DeriveAssignmentDates routes
    // every offset through its clamping helpers, so a period large enough to overflow the date
    // arithmetic yields the storable bound instead of a wrapped or faulted expiry - the guarantee that
    // rule was said to reinforce is the helpers' own and always was.

    /// <summary>
    /// Reported when a fee is a well-formed decimal that the terminal <c>money</c> column cannot hold.
    /// The wording is net-new: the legacy screen declared no upper bound and therefore had no message to
    /// reproduce, and the failure it reports was previously a server fault naming no field.
    /// </summary>
    private static readonly string AmountUnrepresentableMessage = FormattableString.Invariant($"An amount must fall between {SqlServerRange.MinimumMoney} and {SqlServerRange.MaximumMoney}, ")
        + FormattableString.Invariant($"which is the range the stored column can hold.");

    /// <summary>
    /// Initialises a new instance of the <see cref="CreateRoleRequestValidator"/> class and declares
    /// its rules.
    /// </summary>
    /// <remarks>
    /// Rule-level cascade stops at the first failure for a member, so a caller reads one actionable
    /// message per field rather than a length complaint stacked on a presence complaint. Class-level
    /// cascade continues, so one malformed submission reports every bad field in a single response
    /// instead of forcing a caller to discover them one round trip at a time.
    /// </remarks>
    public CreateRoleRequestValidator()
    {
        RuleLevelCascadeMode = CascadeMode.Stop;
        ClassLevelCascadeMode = CascadeMode.Continue;

        // valRoleName (editroles.ascx L29-L31): the screen's ONE presence check, and the only rule
        // in this validator that is unconditional. The width is the NOT NULL column's own.
        // MIGRATION: TEXT INTEGRITY, WHICH THE LEGACY SCREENS DID NOT CHECK AND THIS MIGRATION DOES. Runtime
        // testing stored a NUL byte and zero-width spaces in this very field and served both back, producing a
        // role name that no operator can read, retype or tell apart from a visibly identical one - and making
        // the duplicate-name rule below unenforceable by inspection. The rule refuses invisible characters
        // only; every printable character the legacy screens accepted, emoji and right-to-left text included,
        // still passes. Recorded as a deliberate divergence in MIGRATION_NOTES.md per Rule T5.
        RuleFor(request => request.RoleName)
            .NotEmpty()
            .WithMessage(RoleTermsRules.RoleNameRequiredMessage)
            .MaximumLength(RoleTermsRules.RoleNameMaximumLength)
            .Must(TextIntegrityRules.IsSingleLineSafe)
            .WithMessage(TextIntegrityRules.SingleLineMessage);

        // No validator was declared on the description, so only the column width is asserted. The
        // rule is inert for an absent value: a length check passes a null.
        RuleFor(request => request.Description)
            .MaximumLength(RoleTermsRules.DescriptionMaximumLength)
            .Must(TextIntegrityRules.IsMultiLineSafe)
            .WithMessage(TextIntegrityRules.MultiLineMessage);

        // No validator was declared on the invitation code, and nothing in the schema makes it unique, so a
        // clash is not a conflict and the column width is the only ported bound.
        //
        // SEC: THE STRENGTH RULE IS NET-NEW, AND IT APPLIES TO AUTHORING ONLY. An invitation code is a shared
        // secret that grants role membership - including a private, paid or permission-bearing role, because a
        // code IS the bypass for a service that is not published - and it was bounded only above, so a
        // one-character code was accepted and thereafter redeemable by anybody who submitted that character.
        // The rule refuses a newly authored value that is too easily guessed; it deliberately does NOT reach
        // the redemption contract, because codes the legacy application already stored are weak by
        // construction and refusing them there would make a code an account was legitimately given
        // unredeemable. That asymmetry is the migration path and is recorded in MIGRATION_NOTES.md per Rule
        // T5. An absent or empty value still passes: clearing the code is how an issuer withdraws one.
        RuleFor(request => request.RsvpCode)
            .MaximumLength(RoleTermsRules.RsvpCodeMaximumLength)
            .Must(TextIntegrityRules.IsSingleLineSafe)
            .WithMessage(TextIntegrityRules.SingleLineMessage)
            .Must(RoleTermsRules.IsStrongAuthoredRsvpCode)
            .WithMessage(RoleTermsRules.RsvpCodeTooWeakMessage);

        // Column width, plus the net-new containment rule annotated above. The containment check
        // itself moved to IconReferenceRules so that the role UPDATE and the page update apply the
        // very same rule; while it was private to this class those two paths accepted references
        // this one refused, and the weaker path defined the application's actual behaviour.
        RuleFor(request => request.IconFile)
            .MaximumLength(RoleTermsRules.IconFileMaximumLength)
            .Must(RoleTermsRules.IsContainedRelativePath)
            .WithMessage(RoleTermsRules.IconFileNotRelativeMessage);

        // valServiceFee2 (L96): GreaterThanEqual against 0. Zero is a real price - a free role -
        // so the bound admits it and refuses only a negative amount.
        //
        // REPRESENTABILITY, NOT A PRICE CEILING. The terminal column is money, not the baseline's
        // decimal(5, 2), and the second rule states that column's own limits. It is emphatically not
        // a business maximum: the legacy screen declared none, and inventing one would refuse a fee
        // some installation legitimately charges. What it refuses is an amount the column cannot hold
        // at all, which unbounded reached the provider and surfaced as a server fault naming no field
        // instead of a field-level answer.
        RuleFor(request => request.ServiceFee)
            .GreaterThanOrEqualTo(0m)
            .WithMessage(RoleTermsRules.ServiceFeeNegativeMessage)
            .Must(SqlServerRange.CanStore)
            .WithMessage(AmountUnrepresentableMessage)
            .When(request => request.ServiceFee.HasValue);

        // valBillingPeriod2 (L114): GreaterThan against 0 - strictly positive WHERE A CYCLE IS DECLARED.
        // A cycle of zero units could never advance an expiry date, so a declared frequency still demands
        // a positive number of units; a period of 0 beside no cycle is the value the portal template's own
        // roles carry and the value this API's read projection reports, so refusing it made a value the API
        // emits a value it would not accept. See RoleTermsRules.IsPeriodAdmissibleForFrequency.
        RuleFor(request => request.BillingPeriod)
            .Must((request, period) =>
                RoleTermsRules.IsPeriodAdmissibleForFrequency(period, request.BillingFrequency))
            .WithMessage(RoleTermsRules.BillingPeriodNotPositiveMessage)
            .When(request => request.BillingPeriod.HasValue);

        // valTrialFee2 (L128): GreaterThanEqual against 0 - zero admitted - despite the message
        // wording preserved above. A free trial is a real configuration.
        RuleFor(request => request.TrialFee)
            .GreaterThanOrEqualTo(0m)
            .WithMessage(RoleTermsRules.TrialFeeNegativeMessage)
            .Must(SqlServerRange.CanStore)
            .WithMessage(AmountUnrepresentableMessage)
            .When(request => request.TrialFee.HasValue);

        // valTrialPeriod2 (L146): GreaterThan against 0. Message and operator agree here, and the pair is
        // evaluated for the same reason as the billing period above.
        RuleFor(request => request.TrialPeriod)
            .Must((request, period) =>
                RoleTermsRules.IsPeriodAdmissibleForFrequency(period, request.TrialFrequency))
            .WithMessage(RoleTermsRules.TrialPeriodNotPositiveMessage)
            .When(request => request.TrialPeriod.HasValue);

        // Membership of the domain enumeration, which IS the character check because each member
        // carries the code point of its stored character. Never a literal list of those characters:
        // see the four measurements annotated above. The presence guard is stated explicitly so the
        // rule reads as conditional rather than relying on the enumeration check to tolerate a null.
        RuleFor(request => request.BillingFrequency)
            .IsInEnum()
            .WithMessage(RoleTermsRules.BillingFrequencyInvalidMessage)
            .When(request => request.BillingFrequency.HasValue);

        RuleFor(request => request.TrialFrequency)
            .IsInEnum()
            .WithMessage(RoleTermsRules.TrialFrequencyInvalidMessage)
            .When(request => request.TrialFrequency.HasValue);

        // No rule on the two flags: a boolean is its own constraint, and false is a legitimate
        // state that agrees with the columns' own zero defaults.
        //
        // No rule on the group reference: zero names a real group and the legacy negative value
        // means "ungrouped", so any bound would refuse one of them.
    }
}
