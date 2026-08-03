using DnnMigration.Application.Dtos.Module;
using DnnMigration.Domain.Common;
using FluentValidation;

namespace DnnMigration.Application.Validation;

// ======================================================================================================
// THE MEASURED EVIDENCE BEHIND EVERY DECISION IN THIS FILE, INCLUDING THE DECISIONS TO AUTHOR NO RULE
// ======================================================================================================
//
// MIGRATION: THE LEGACY SCREEN DECLARES ZERO REQUIRED-FIELD VALIDATORS - NOT ONE, AND NOT EVEN ON THE
//   TITLE. Measured against Website/admin/Modules/modulesettings.ascx, 225 lines:
//   RequiredFieldValidator 0, RegularExpressionValidator 0, ValidationSummary 0, ControlToCompare 0,
//   CompareValidator 4. Those four are the complete declarative census, and every one of them is a
//   DataTypeCheck comparison rather than a presence or range test:
//
//     L78   valtxtStartDate  txtStartDate   Type="Date"      maxlength=11
//                            ErrorMessage="<br>Invalid Start Date"
//     L88   valtxtEndDate    txtEndDate     Type="Date"      maxlength=11
//                            ErrorMessage="<br>Invalid End Date"
//     L138  valBorder        txtBorder      Type="Integer"   MaxLength=1
//                            ErrorMessage="<br>Invalid Border (must be a number between 0 and 9)"
//     L172  valCacheTime     txtCacheTime   Type="Integer"   maxlength=6
//                            ErrorMessage="<br>Invalid Cache Time"
//
//   Nothing else on that screen carries a declarative rule: not the title, the header, the footer, the
//   colour, the icon, the alignment, the visibility group, the permission grid or the page picker.
//   RESTRAINT IS THEREFORE THE REQUIREMENT HERE, NOT THOROUGHNESS. The minimal-change discipline obliges
//   validation rules to MATCH and messages to be EQUIVALENT, so a rule the legacy screen never declared
//   is a tightening that would refuse a module configuration the legacy application accepted happily.
//
// MIGRATION: A DataTypeCheck COMPARISON SUCCEEDS ON EMPTY INPUT, WHICH IS WHY NO PRESENCE RULE APPEARS
//   BELOW. Such a validator asks only "does this text parse as the named type"; given an empty box it
//   passes. Leaving the start date, the end date, the border or the cache period blank was consequently
//   valid legacy behaviour, and the code-behind corroborates it by guarding every read with a non-empty
//   test before converting - ModuleSettings.ascx.vb L349, L367 and L372. This file therefore declares no
//   unconditional presence rule of any kind, and its two string rules are explicitly conditional so a
//   blank can never fail one.
//
// MIGRATION: THE LEGACY BLANK MUST NOT BECOME AN ERROR, AND THE LEGACY STRING SENTINEL IS THE EMPTY
//   STRING RATHER THAN A NULL. Null.vb returns literally "" for an absent string, so an absent value and
//   an empty one are THE SAME STATE in legacy terms. Every conditional guard below is written as a
//   null-or-empty test rather than a null test, so a caller sending "" is treated exactly as a caller
//   omitting the member. The same file returns minus one for an absent integer and the minimum date value
//   for an absent date; neither is rejected anywhere here, because both are legitimate stored values.
//
// MIGRATION: CROSS-FILE CONFLICT, REPORTED RATHER THAN PAPERED OVER - THERE IS NO BORDER RULE BECAUSE
//   THERE IS NO BORDER MEMBER. valBorder is the one validator on the screen whose wording promises a
//   range, "must be a number between 0 and 9", and the range was never enforced by the validator at all:
//   its operator is DataTypeCheck only, and the single-digit effect came from txtBorder's MaxLength of 1.
//   The rule cannot be reproduced on this contract regardless, because CreateModuleRequest carries no
//   border member - the creation contract deliberately excludes the pane-layout and rendering surface, as
//   its own annotations record. Naming a member that does not exist would not compile, and the contract is
//   read here and never edited, so the rule is DROPPED and its absence recorded rather than faked. Two
//   independent findings show a numeric range would have been the wrong reading even had the member
//   survived: the terminal column TabModules.Border is nvarchar(1) NULL - a single character used as a
//   renderer flag, not a width - and ModuleSettings.ascx.vb L347 assigns it as a bare string with no
//   integer parse anywhere on the border path. The update-path contract keeps the member and reproduces
//   the rule as a single-digit test; this contract has nothing to attach it to.
//
// MIGRATION: NO RULE COMPARES THE TWO DATES TO EACH OTHER, AND THE MEASURED CONTRAST PROVES THE ABSENCE
//   IS DELIBERATE. modulesettings.ascx contains zero controltocompare attributes, so both date validators
//   are independent format checks. Website/admin/Security/securityroles.ascx L45-L47 shows what the
//   opposite looks like on a screen from the same generation: two independent DataTypeCheck validators
//   PLUS a third, valDates, whose operator is a comparison and which names a controltocompare attribute.
//   The module screen had the same option and did not take it. An end date preceding a start date was
//   therefore storable, producing a module that could never be visible - a legacy defect that the
//   discipline here requires be annotated rather than fixed. Note that the outcome is unchanged in
//   practice: ModuleService evaluates the schedule on the write path, which is where a judgement about
//   the relationship between two submitted values belongs, and a declarative shape validator has no
//   business duplicating it.
//
// MIGRATION: NO IDENTIFIER CARRIES A NUMERIC BOUND, AND BOTH OBVIOUS BOUNDS WOULD BE WRONG. The measured
//   identity declarations in 01.00.00.SqlDataProvider are Modules.ModuleID IDENTITY (0, 1) at L221,
//   Tabs.TabID IDENTITY (0, 1) at L140, Portals.PortalID IDENTITY (-1, 1) at L77 - whose shipped default
//   row is inserted with an explicit PortalID of 0 at L7125, so -1 and 0 are both real portal keys -
//   Roles.RoleID IDENTITY (0, 1) at L115, and ModuleDefinitions.ModuleDefID IDENTITY (1, 1) at L66. Zero is
//   therefore a REAL page and a REAL module, and the portal seed collides exactly with the legacy
//   absent-integer sentinel, so no single numeric rule can serve identifiers that start at three different
//   values and any such rule risks refusing a row that genuinely exists. A positive-only rule on the
//   definition would assert existence, which is a stateful question a shape validator cannot answer; a
//   zero-or-greater rule on the page would assert nothing the type has not already guaranteed while
//   risking the wildcard the legacy query surface used. Neither is declared. Presence is expressed by the
//   members being non-nullable integers, and EXISTENCE - whether the definition is available to the portal,
//   whether the page belongs to it - is answered by the module service against the store, which the
//   integration suite exercises through its own not-found cases.
//
// MIGRATION: NEITHER DATE CARRIES A LOWER BOUND. Null.vb returns the minimum date value for an absent
//   date, and the legacy screen stored exactly that for a blank box while rendering it back as an empty
//   box, so the earliest representable date is a legitimate "no date" rather than a corrupt one. A lower
//   bound would convert the legacy blank into a validation failure.
//
// MIGRATION: VISIBILITY IS CONSTRAINED BY ITS ENUMERATION, NEVER BY A NUMERIC RANGE. The legacy control
//   was a radio group whose three items carried 0, 1 and 2, and the screen used the stored number
//   directly as the list index. Those numbers are the persisted values, so the ModuleVisibility
//   enumeration reproduces them exactly and membership of it is the whole constraint. Writing the range
//   as literals would reintroduce precisely the magic integers this migration replaces with named
//   members, and would silently drift the moment a member is added.
//
// MIGRATION: THE MARKUP MaxLength VALUES ARE TEXT-ENTRY WIDTHS AND ARE NOT CARRIED ACROSS. maxlength 11
//   on the two date boxes and 6 on the cache box bounded how many characters an operator could type into
//   a rendered text box; neither is a property of the underlying column, and the wire format here is
//   typed JSON with no text box to cap. Translating 11 into a length rule on a date, or 6 into one on an
//   integer, would invent a constraint the store never had. The intent is translated instead of the
//   character count, and the only length rules below are the two whose bound is a real measured column
//   width.
//
// MIGRATION: THE LEADING <br> IS STRIPPED FROM EVERY MESSAGE AND THE WORDING IS PRESERVED CHARACTER FOR
//   CHARACTER. Each legacy ErrorMessage begins with that tag because the validator rendered inline into
//   the page, next to the field, and needed a line break ahead of itself. A JSON validation payload has no
//   such layout duty and its consumer is not a browser, so emitting markup inside a message string would
//   push presentation into the contract. The wording an existing operator recognises is what the
//   discipline requires be equivalent, and it is kept intact - the full legacy strings are recorded
//   verbatim in the census above so nothing is lost from the record.
//
// MIGRATION: THE CACHE PERIOD CARRIES NO RULE, BECAUSE THE LEGACY SCREEN IMPOSED NONE BEYOND THE ONE THE
//   TYPE SYSTEM NOW ENFORCES FOR FREE. valCacheTime (modulesettings.ascx L172) declared
//   Operator="DataTypeCheck" Type="Integer" and nothing further - no CompareValidator against a floor, no
//   RangeValidator - and the code-behind stored whatever parsed, Int32.Parse(txtCacheTime.Text) at L349-L350
//   with no comparison of any kind. A negative period was therefore an accepted submission. An earlier
//   revision of this file added a non-negative floor and labelled it net-new; that floor is REMOVED. It was
//   not authorised by the legacy screen, by the request contract or by the AAP, and the migration discipline
//   requires identical inputs to produce identical outcomes rather than a tighter rule imposed because a
//   tighter rule looks defensible. Integrality itself needs no rule here: the member is typed int, so a
//   non-integer never binds and the payload is refused by the binder before any validator runs - the same
//   check the legacy validator performed, relocated to the type rather than dropped.
//
// MIGRATION: A BLANK LEGACY BOX IS STILL A ZERO, AND THE MEMBER IS STILL NOT NULLABLE. L349-L350 parses the
//   box only when it is non-empty and otherwise assigns zero, so "blank" and "zero" were the same
//   submission and zero means "do not cache". An omitted JSON property leaves the member at zero, which
//   reproduces that exactly. Nullability is deliberately not introduced: it would invent an unspecified
//   state the legacy contract never had. Note also that minus one is meaningful rather than nonsensical in
//   this domain - ModuleSettings.ascx.vb L138 tests a definition's DefaultCacheTime against the legacy
//   integer sentinel of minus one - which is a further reason a floor at zero is not this layer's to assert.
//
// MIGRATION: THE LEGACY SCREENS COMPILED WITH OPTION STRICT OFF, AND EVERY IMPLICIT COERCION IS NOW
//   EXPLICIT. Website/release.config L125 sets strict="false" for the administration screens while the
//   class library compiled with it on, so those screens legally performed narrowing conversions and late
//   binding that C# refuses. Three measured cases matter here, and all three concern the empty string.
//   The border was assigned straight from the text box as a string with no parse at all, so a blank
//   stored the empty string - the legacy absent-string sentinel - rather than a zero. The cache period
//   was parsed only inside a non-empty test, so a blank left the value at zero: THE EMPTY STRING BECAME
//   ZERO, and it is that coercion which makes a non-nullable integer defaulting to zero the faithful
//   translation instead of a validation failure. Both dates were converted with no culture argument and
//   likewise only inside a non-empty test, so a blank left the absent-date sentinel in place. In C# an
//   empty string parses to nothing at all, so the coercion moves to deserialisation: an unparseable value
//   now yields an RFC 7807 validation problem at the API edge instead of a client-side format message.
//   That is a documented change in failure MODE, not in which values are accepted.
//
// MIGRATION: NOTHING HERE RESOLVES A TYPE, TOUCHES THE STORE OR SHAPES A RESPONSE. The legacy module
//   pipeline activated a business controller named by a string, late-bound at five call sites in
//   ModuleController.vb and EventMessageProcessor.vb; that responsibility belongs to the injected
//   IModuleBusinessControllerFactory over a closed, container-registered set, and the creation contract
//   does not accept a type name at all. Stateful questions belong to ModuleService, permission questions
//   to the permission evaluator, and the HTTP shape of a failure to the validation problem factory at the
//   API edge. This file is a declarative shape check and nothing else.

/// <summary>
/// Validates the shape of a module creation submitted to <c>POST /api/v1/portals/{portalId}/modules</c>.
/// </summary>
/// <remarks>
/// <para>
/// THIS VALIDATOR IS DELIBERATELY SPARSE, AND THE SPARSENESS IS THE PORTED BEHAVIOUR. The legacy
/// module settings screen, <c>Website/admin/Modules/modulesettings.ascx</c>, declared ZERO
/// required-field validators and zero regular-expression validators. Its entire declarative rule set
/// was four <c>CompareValidator</c> declarations, each with <c>Operator="DataTypeCheck"</c>: a date
/// format check on the start date, the same on the end date, an integer check on the border and an
/// integer check on the cache period. Because such a comparison SUCCEEDS ON EMPTY INPUT, a blank
/// value was valid legacy behaviour for every one of the four, so no rule here may demand presence
/// and the conditional rules below exist to guarantee that a blank cannot fail.
/// </para>
/// <para>
/// Two of the four have no counterpart at all. The border check has no member to attach to, the
/// creation contract having excluded the rendering surface; its message promised a range of nought to
/// nine that the validator never actually enforced, the single-digit effect having come from the text
/// box's <c>MaxLength</c> of one rather than from any rule. The two date checks are satisfied by the
/// member types themselves, a nullable <see cref="System.DateTime"/> being unable to carry an
/// unparseable date once the wire format is JSON, so restating them would add rules that can never
/// fire.
/// </para>
/// <para>
/// NO RULE COMPARES THE START DATE TO THE END DATE. Measured, the screen declares no
/// <c>ControlToCompare</c> attribute anywhere, whereas a sibling screen of the same generation,
/// <c>Website/admin/Security/securityroles.ascx</c>, does declare exactly such a cross-field
/// comparison - so the absence here is a deliberate legacy choice rather than an oversight, and
/// reproducing it is fidelity. That an end date could precede a start date is recorded as a legacy
/// defect, and the write path in <c>Application/Services/ModuleService.cs</c> is where the schedule is
/// judged.
/// </para>
/// <para>
/// NO IDENTIFIER CARRIES A NUMERIC BOUND. The page and module identities both seed at zero and the
/// portal identity seeds at minus one, which is simultaneously the legacy sentinel for an absent
/// integer, so a numeric floor would refuse rows that genuinely exist. Presence is carried by the
/// members being non-nullable; existence is a question for the module service against the store.
/// </para>
/// <para>
/// Rule-level cascade stops at the first failure for a member so a single field reports one reason;
/// class-level cascade continues so every offending field is reported in one response. Stateful rules -
/// whether the definition exists and is available to the portal, whether the page belongs to the
/// portal, and whether the requested schedule is coherent - belong to
/// <c>Application/Services/ModuleService.cs</c>.
/// </para>
/// </remarks>
public class CreateModuleRequestValidator : AbstractValidator<CreateModuleRequest>
{
    /// <summary>
    /// Message reported when a submitted date falls outside the range the terminal <c>datetime</c>
    /// column can hold. The wording is net-new: the legacy screen declared only a format check and had
    /// no such rule, and the failure it reports was previously a server fault naming no field.
    /// </summary>
    private static readonly string DateUnrepresentableMessage = FormattableString.Invariant($"A date must fall between {SqlServerRange.MinimumDateTime:yyyy-MM-dd} and ")
        + FormattableString.Invariant($"{SqlServerRange.MaximumDateTime:yyyy-MM-dd}, which is the range the stored column can hold.");

    /// <summary>
    /// Names the three permitted display states rather than their persisted numbers, so the message
    /// stays readable if the enumeration is ever extended.
    /// </summary>
    private const string VisibilityInvalidMessage = "Visibility must be Maximized, Minimized or None.";

    /// <summary>
    /// The measured bound on <c>Modules.ModuleTitle</c> (<c>nvarchar(256) NULL</c>). The legacy text box
    /// declared a rendered width but no maximum length, so the column is the sole authority.
    /// </summary>
    private const int ModuleTitleMaximumLength = 256;

    /// <summary>
    /// The measured bound on <c>TabModules.IconFile</c> (<c>nvarchar(100) NULL</c>). A stored name or
    /// path only: the legacy control declared no validator, and nothing here resolves a location.
    /// </summary>
    private const int IconFileMaximumLength = 100;

    /// <summary>
    /// Declares the rule set. Parameterless by design: a shape check needs no collaborator, and the
    /// assembly-wide scan that registers every validator in this folder constructs it with none.
    /// </summary>
    public CreateModuleRequestValidator()
    {
        RuleLevelCascadeMode = CascadeMode.Stop;
        ClassLevelCascadeMode = CascadeMode.Continue;

        // The column width is the only bound the legacy screen implies, and the guard makes the legacy
        // equivalence explicit: an omitted member and an empty string are the same state, because the
        // legacy absent-string sentinel was itself the empty string. Neither may ever fail.
        RuleFor(request => request.ModuleTitle)
            .MaximumLength(ModuleTitleMaximumLength)
            .When(request => !string.IsNullOrEmpty(request.ModuleTitle));

        // Same treatment, same reasoning, against the measured icon column width.
        RuleFor(request => request.IconFile)
            .MaximumLength(IconFileMaximumLength)
            .When(request => !string.IsNullOrEmpty(request.IconFile));

        // Membership of the enumeration is the whole constraint. The persisted numbers are 0, 1 and 2,
        // and they live on the named members rather than in a range written here.
        RuleFor(request => request.Visibility)
            .IsInEnum().WithMessage(VisibilityInvalidMessage);

        // MIGRATION: the two date members carry a REPRESENTABILITY bound and nothing else, and the
        // non-negative floor on the cache period that an earlier revision declared beside them is NOT
        // reinstated here - the census below records why. The CLR date type begins in the year one while
        // the stored column begins in 1753, so a value the type accepts can still be unstorable;
        // unbounded, such a value passed every rule here and was refused by the provider instead, which
        // surfaces as a server fault naming no field rather than a field-level answer. The bound states
        // only what the column can hold. The two are still NOT compared to one another - see the
        // annotation on that omission - and the upper bound is stated at the last representable instant of
        // 9999-12-31 rather than at that day's midnight, so a preserved perpetual value is admitted by the
        // rule rather than refused by it.
        RuleFor(request => request.StartDate)
            .Must(SqlServerRange.CanStore).WithMessage(DateUnrepresentableMessage);

        RuleFor(request => request.EndDate)
            .Must(SqlServerRange.CanStore).WithMessage(DateUnrepresentableMessage);

        // Deliberately unvalidated, each for a measured reason, so that a later reader does not mistake
        // an absence for an omission:
        //   CacheTime                   - the legacy validator checked INTEGRALITY AND NOTHING ELSE
        //                                 (modulesettings.ascx L172, Operator="DataTypeCheck"
        //                                 Type="Integer"), and the code-behind stored whatever parsed
        //                                 (L349-L350, Int32.Parse with no comparison). Integrality is
        //                                 already total here, because the member is typed int and a
        //                                 non-integer never binds. See the migration note above
        //   ModuleDefId, TabId          - identifiers; the seeds collide with the legacy sentinel, so no
        //                                 numeric bound is admissible and existence is the store's answer
        //   StartDate, EndDate          - the two are never compared to each other, and the nullable date
        //                                 type enforces the only legacy check; the storage-range bound
        //                                 applied above is a different kind of rule, annotated there
        //   ModuleOrder                 - minus one is a load-bearing instruction meaning "append at the
        //                                 bottom of the pane", never an absent value, so it must pass
        //   Header, Footer              - unbounded national text columns; the legacy controls declared
        //                                 neither a maximum length nor a validator
        //   AllTabs, InheritViewPermissions, DisplayTitle
        //                               - booleans with no legacy rule; the every-page flag was gated by
        //                                 role rather than by validation, which authorisation now owns
    }
}
