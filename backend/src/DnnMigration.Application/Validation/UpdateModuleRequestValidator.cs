using DnnMigration.Application.Dtos.Module;
using DnnMigration.Domain.Common;
using FluentValidation;

namespace DnnMigration.Application.Validation;

// ======================================================================================================
// THE MEASURED EVIDENCE BEHIND EVERY DECISION IN THIS FILE - INCLUDING EVERY DECISION TO AUTHOR NO RULE
// ======================================================================================================
//
// MIGRATION: THE LEGACY SCREEN DECLARES ZERO REQUIRED-FIELD VALIDATORS - NOT ONE, AND NOT EVEN ON THE
//   TITLE. Measured case-insensitively against Website/admin/Modules/modulesettings.ascx, 225 lines:
//   RequiredFieldValidator 0, RegularExpressionValidator 0, ValidationSummary 0, RangeValidator 0,
//   ControlToCompare 0, CompareValidator 4. Those four are the COMPLETE declarative census, and every
//   one of them is a DataTypeCheck comparison rather than a presence, range or pattern test:
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
//   Nothing else on that screen carries a declarative rule: not the title box at L32, the header at L64,
//   the footer at L69, the colour at L132, the icon picker, the alignment list, the visibility group at
//   L144, the permission grid or the page picker. RESTRAINT IS THEREFORE THE REQUIREMENT HERE, NOT
//   THOROUGHNESS. The minimal-change discipline obliges validation rules to MATCH and messages to be
//   EQUIVALENT, so any rule the legacy screen never declared is a tightening that would refuse a module
//   configuration the legacy application accepted happily.
//
// MIGRATION: THE FOUR LEGACY MESSAGES ARE QUOTED ABOVE VERBATIM, INCLUDING THEIR LEADING BREAK TAG, AND
//   THAT TAG IS DELIBERATELY NOT CARRIED INTO ANY RUNTIME MESSAGE. It was inline layout for a validator
//   rendered beside its text box; emitted in a JSON payload a client would print it literally. The point
//   is narrower than it looks, because NONE of the four becomes a runtime message here: the two date
//   checks and the cache-period check are format tests that the request contract's own types now enforce
//   ahead of this validator, and the border check has no member to attach to. The wording is preserved
//   as EVIDENCE rather than as output, which is why it appears in this comment and not in a message.
//
// MIGRATION: A DataTypeCheck COMPARISON SUCCEEDS ON EMPTY INPUT, WHICH IS WHY NO PRESENCE RULE APPEARS
//   BELOW AND WHY EVERY RULE THAT DOES APPEAR IS EXPLICITLY CONDITIONAL. Such a validator asks only
//   "does this text parse as the named type"; given an empty box it passes. Leaving the start date, the
//   end date, the border or the cache period blank was consequently valid legacy behaviour, and the
//   code-behind corroborates it by guarding every read with a non-empty test before converting -
//   ModuleSettings.ascx.vb L349, L367 and L372. THIS MATTERS MORE ON AN UPDATE THAN ON A CREATE: the
//   legacy screen posted every text box on every save, blank ones included, and accepted them, so a
//   blank arriving here is unambiguously legitimate and a request that simply omits an optional member
//   must never be refused for the omission.
//
// MIGRATION: THE LEGACY STRING SENTINEL IS THE EMPTY STRING RATHER THAN A NULL, SO EVERY STRING GUARD
//   BELOW IS A NULL-OR-EMPTY TEST. Null.vb L71-L75 returns literally "" for an absent string, so an
//   absent value and an empty one are THE SAME STATE in legacy terms; a guard written as a bare null
//   test would treat them differently. The same file returns minus one for an absent integer, 255 for an
//   absent byte, the minimum date value for an absent date and the empty identifier for an absent one.
//   None of those is rejected anywhere in this file, because each is a legitimate stored value.
//
// MIGRATION: CROSS-FILE CONFLICT, REPORTED RATHER THAN PAPERED OVER - THERE IS NO BORDER RULE BECAUSE
//   THERE IS NO BORDER MEMBER. valBorder is the one validator on the screen whose wording promises a
//   range, "must be a number between 0 and 9", and the validator never enforced that range: its operator
//   is DataTypeCheck alone, and the single-digit effect came from txtBorder's MaxLength of 1. Reading
//   those two together as InclusiveBetween(0, 9) would be a reading of INTENT rather than a
//   transcription - and it is moot here, because UpdateModuleRequest carries no border member at all.
//   The pane-layout and rendering surface is excluded from the request contract, whose own exclusion
//   summary states that dropping the border is precisely why only three of the screen's four validators
//   survive. Naming a member that does not exist would not compile, and that contract is READ here and
//   never edited, so the rule is DROPPED and its absence recorded rather than faked. Two independent
//   findings show a numeric range would have been the WRONG reading even had the member survived: the
//   terminal column TabModules.Border is nvarchar(1) NULL - a single CHARACTER used as a renderer flag,
//   not a numeric width, confirmed at 01.00.00.SqlDataProvider:L232 and in the placement mapping - and
//   ModuleSettings.ascx.vb L347 assigns it as a bare string, with no integer parse anywhere on the
//   border path. Note for anyone comparing the two module validators: the create-path file asserts that
//   the update-path contract keeps this member and reproduces the rule as a single-digit test. That
//   assertion is STALE - measured, the update contract declares sixteen members and none of them is the
//   border - and it is corrected here rather than there, because a sibling validator is not this file's
//   to edit.
//
// MIGRATION: CROSS-FILE CONFLICT - CacheTime IS A NON-NULLABLE INTEGER, SO ITS RULE IS THE TYPE. The
//   member is declared as a plain integer rather than a nullable one, and the request contract justifies
//   that directly: a blank legacy box stored LITERALLY ZERO rather than a sentinel, so a nullable member
//   would invent an "unspecified" state the legacy contract never had. valCacheTime tested integrality
//   and nothing else, and integrality is now settled by the member's own type before this validator
//   runs. There is consequently nothing conditional to write and nothing left to check - see the
//   annotation in the constructor for why a floor at zero is refused as a narrowing.
//
// MIGRATION: CROSS-FILE CONFLICT - THE TWO DATES ARE ALREADY NULLABLE, SO THEIR FORMAT RULE ALSO LIVES
//   IN THE TYPE. valtxtStartDate and valtxtEndDate were format tests; a nullable date member enforces
//   exactly that at deserialisation, and an unparseable value yields an RFC 7807 validation problem at
//   the API edge before this validator is reached. The conditional guards written below are therefore
//   about the one rule that remains - representability - and they make explicit what the null-tolerant
//   helper already did, so an omitted date can never fail a rule.
//
// MIGRATION: THE LEGACY SCREENS COMPILED WITH OPTION STRICT OFF, SO EVERY IMPLICIT COERCION IS MADE
//   EXPLICIT HERE OR RELOCATED. Website/release.config:L125 declares strict as false for all thirty-nine
//   admin code-behinds, which permitted narrowing and late binding that C# refuses outright. Three
//   coercions on this screen are consequential. First, the cache period: L349-L353 parse the box when it
//   is non-empty and assign literally zero when it is not, so an empty string became 0 rather than
//   failing - reproduced by the member being a non-nullable integer whose default is already zero, so an
//   omitted property behaves exactly as the legacy blank did. Second, the two dates: L367-L376 convert
//   when non-empty and otherwise assign the legacy absent-date sentinel, so an empty string became the
//   minimum date value; here absence travels as a null instead, and the projection layer owns the
//   translation, which the data provider itself performed at SqlDataProvider.vb:L709 by converting that
//   sentinel to a database null before the datetime column. Third, the border: L347 assigns a text box
//   straight onto a property with no parse at all, which is why an integer reading of it was never
//   safe. In C# an empty string does not parse, so each of these is a change in failure MODE rather than
//   in which values are accepted, and none becomes a validation failure.
//
// MIGRATION: THIS FILE DELIBERATELY DUPLICATES ITS CREATE-PATH SIBLING RATHER THAN REUSING IT. The two
//   contracts are served by two independent validators: there is no inheritance, no rule-set inclusion,
//   no shared base type and no generic validator abstraction, because a shared base is forbidden and the
//   folder's inventory is fixed. The duplication is intentional, and it is not merely stylistic - the
//   two contracts genuinely differ. The create contract declares fourteen members and names the module
//   DEFINITION; this one declares sixteen, omits the definition because a module's definition is
//   IMMUTABLE after creation, and adds the soft-delete marker plus the two write-only intent flags. The
//   provider surface proves the immutability rather than implying it: the legacy create passed ten
//   arguments to the first provider call and the legacy update passed nine, the missing one being the
//   definition.
//
// MIGRATION: NO RULE HERE PERFORMS OR IMPLIES ANY DATA ACCESS, ANY LATE-BOUND ACTIVATION OR ANY
//   PERMISSION DECISION. Whether the module exists, whether the named page belongs to the portal and
//   whether the caller may write at all are STATEFUL questions this layer cannot answer; they belong to
//   the module service, the repositories behind it and the permission evaluator, and a module that does
//   not exist is a service-reported failure surfacing as a 404 rather than a validation failure
//   surfacing as a 400. The legacy business-controller class name is likewise absent from the contract
//   and from this file: the five late-bound activation sites it fed are replaced by an injected
//   IModuleBusinessControllerFactory resolving from a closed, registered set, so nothing here inspects a
//   type name, loads code or activates anything.

/// <summary>
/// Validates the SHAPE of a module update submitted to
/// <c>PUT /api/v1/portals/{portalId}/modules/{moduleId}</c>, and nothing beyond shape.
/// </summary>
/// <remarks>
/// <para>
/// THE MODULE IDENTIFIER CARRIES NO BOUND TEST, AND THAT IS A CORRECTNESS REQUIREMENT RATHER THAN AN
/// OMISSION - it is not even a member of the request, because the route supplies it. Were it present, a
/// positive-only or non-zero test on it would be actively WRONG: <c>Modules.ModuleID</c> is declared
/// <c>IDENTITY (0, 1)</c> at <c>01.00.00.SqlDataProvider:L221</c>, so the first module created in every
/// installation has the identifier ZERO and such a rule would reject it. The same reasoning governs
/// every identifier this contract does carry or touch: <c>Tabs.TabID</c> is <c>IDENTITY (0, 1)</c> at
/// L140, and <c>Portals.PortalID</c> is <c>IDENTITY (-1, 1)</c> at L77 - where minus one is
/// simultaneously a real portal and the legacy integer sentinel for an absent value. Sentinels survive
/// at the boundary rather than in the domain, so minus one, zero, the empty string, 255, the minimum
/// date value and the empty identifier are legitimate values carrying meaning, never validation
/// failures.
/// </para>
/// <para>
/// THE LEGACY SCREEN DECLARED ZERO REQUIRED-FIELD VALIDATORS, so this validator declares no presence
/// rule of any kind - not even on the title - and each of the four rules it does declare is explicitly
/// conditional, because the screen's only validators were data-type comparisons and such a comparison
/// SUCCEEDS on empty input. The complete measured census, the four verbatim legacy messages and the
/// evidence for every decision to author no rule are recorded in the comments above this type.
/// </para>
/// <para>
/// THIS VALIDATOR DELIBERATELY DUPLICATES <c>CreateModuleRequestValidator</c> RATHER THAN REUSING IT.
/// It does not derive from it, does not include its rule set, and shares no base type or generic
/// validator abstraction with it: a shared base is forbidden and each file in this folder stands alone.
/// The two contracts differ genuinely as well as structurally - this one omits the module definition,
/// which is immutable after creation, and adds the soft-delete marker and the two write-only intent
/// flags.
/// </para>
/// <para>
/// Rule-level cascade stops at the first failure for a member; class-level cascade continues so every
/// member is reported in one response. Stateful checks - whether the module exists in the portal,
/// whether the named page belongs to it, and whether the every-page toggle and the two portal-wide
/// intent flags may be honoured - belong to <c>Application/Services/ModuleService.cs</c>. Shaping the
/// failure into an RFC 7807 response belongs to the API layer.
/// </para>
/// </remarks>
public class UpdateModuleRequestValidator : AbstractValidator<UpdateModuleRequest>
{
    /// <summary>
    /// Message reported when the submitted visibility is not one of the three defined members.
    /// </summary>
    /// <remarks>
    /// Net-new wording: the legacy radio-button list could not express an undefined value, so the screen
    /// declared no message for one. The three names are the enumeration's own, and the numbers behind
    /// them - 0, 1 and 2 - are load-bearing legacy data stored directly in the column.
    /// </remarks>
    private const string VisibilityInvalidMessage = "Visibility must be Maximized, Minimized or None.";

    /// <summary>
    /// Message reported when a submitted date falls outside the range the terminal <c>datetime</c>
    /// column can hold.
    /// </summary>
    /// <remarks>
    /// The wording is net-new: the legacy screen declared only a format check, and the failure this
    /// reports was previously a server fault naming no field at all. The bound is composed from the
    /// storage limits rather than restated, so the message and the rule can never drift apart.
    /// </remarks>
    private static readonly string DateUnrepresentableMessage = FormattableString.Invariant($"A date must fall between {SqlServerRange.MinimumDateTime:yyyy-MM-dd} and ")
        + FormattableString.Invariant($"{SqlServerRange.MaximumDateTime:yyyy-MM-dd}, which is the range the stored column can hold.");

    // Measured terminal column widths, and the ONLY two the update contract can carry. The pane,
    // alignment, colour and border bounds went with their excluded members, and with the border went one
    // of the legacy screen's four validators. Neither bound below is a presence check, and neither comes
    // from the markup: txtTitle at modulesettings.ascx:L32 declares a rendered width and NO maximum
    // length, so the column is the sole authority for the title, and the icon picker declares no
    // validator either.
    //
    // MIGRATION: THE MARKUP'S OWN LENGTH NUMBERS ARE DELIBERATELY NOT CARRIED ACROSS, BECAUSE THEY BOUND
    //   RENDERED TEXT RATHER THAN STORED VALUES. maxlength=11 on each date box sized a short typed date
    //   and says nothing about a date member; maxlength=6 on the cache-period box sized six digits and
    //   says nothing about an integer member; MaxLength=1 on the border box produced the single-digit
    //   effect its message described. Translating any of the three into a length rule would be
    //   transcribing a character count instead of the intent behind it.

    /// <summary>
    /// Maximum stored length of the module title: <c>Modules.ModuleTitle nvarchar (256) NULL</c>,
    /// declared at <c>01.00.00.SqlDataProvider:L226</c> and unchanged by the upgrade chain.
    /// </summary>
    private const int ModuleTitleMaximumLength = 256;

    /// <summary>
    /// Maximum stored length of the placement icon: <c>TabModules.IconFile nvarchar (100) NULL</c>. The
    /// width originates on the module table at <c>01.00.00.SqlDataProvider:L234</c> and the upgrade chain
    /// carries it, unchanged, onto the placement table at <c>03.00.01.SqlDataProvider:L30</c>, which is
    /// where the terminal schema and the placement mapping both hold it.
    /// </summary>
    private const int IconFileMaximumLength = 100;

    /// <summary>
    /// Declares the rule set. Parameterless by design: this validator needs no policy, no clock, no
    /// options object and no repository, and the assembly-wide scan that registers every validator in
    /// this folder constructs it with none. That scan skips non-public types, which is why this class is
    /// public and why it registers itself nowhere.
    /// </summary>
    public UpdateModuleRequestValidator()
    {
        RuleLevelCascadeMode = CascadeMode.Stop;
        ClassLevelCascadeMode = CascadeMode.Continue;

        // MIGRATION: THE TITLE IS NOT SUBJECT TO A PRESENCE RULE. Measured: modulesettings.ascx declares
        //   no required-field validator and no validation summary anywhere in its 225 lines, and its
        //   title box carries a rendered width but no maximum length - so the 256-character column bound
        //   is the only rule there is to impose. The code-behind confirms it by assigning the box
        //   straight onto the property at L344 with no guard. A non-empty rule would be a tightening the
        //   minimal-change discipline forbids. The guard is a null-or-empty test rather than a null test
        //   because the legacy absent string was literally "", so a caller sending an empty title is
        //   treated exactly as a caller omitting it: neither can fail this rule.
        RuleFor(request => request.ModuleTitle)
            .MaximumLength(ModuleTitleMaximumLength)
            .When(request => !string.IsNullOrEmpty(request.ModuleTitle));

        // MIGRATION: the icon is a STORED NAME OR PATH and nothing more - no upload, no location
        //   resolution and no reachability promise, file-system handling being outside this migration.
        //   The legacy picker declared no validator, so the column width is again the only bound, and the
        //   same null-or-empty guard applies for the same reason.
        RuleFor(request => request.IconFile)
            .MaximumLength(IconFileMaximumLength)
            .When(request => !string.IsNullOrEmpty(request.IconFile));

        // MIGRATION: VISIBILITY IS CHECKED AS AN ENUMERATION, NEVER AS A NUMERIC RANGE. The legacy
        //   control was a radio-button list whose three items carried the values 0, 1 and 2, and the
        //   legacy reader mapped both 0 AND the legacy integer sentinel - and, through the sentinel
        //   substitution applied first, a stored null as well - to Maximized, mapped 1 to Minimized and 2
        //   to None, and declared NO fallback branch, so a stored value outside that set silently became
        //   Maximized. The same missing-default pattern recurs in the save at L359-L363, where such a
        //   value left the field unchanged instead. Both are legacy defects, annotated and deliberately
        //   NOT fixed. Here the type carries the constraint and exactly the three defined members are
        //   accepted: a range over the raw numbers would reintroduce magic integers the migration
        //   replaced with named members. None is a REAL display state meaning "not rendered", not an
        //   absence marker.
        RuleFor(request => request.Visibility)
            .IsInEnum().WithMessage(VisibilityInvalidMessage);

        // MIGRATION: THE TWO DATE MEMBERS CARRY A REPRESENTABILITY BOUND AND NOTHING ELSE, AND IT IS NOT
        //   A SENTINEL TEST. The stored columns are datetime, whose calendar begins in 1753, while the
        //   CLR date type begins in the year one - so a value the type accepts can still be unstorable.
        //   Unbounded, such a value passed every rule here and was refused by the provider instead, which
        //   surfaces as a server fault naming no field rather than as a field-level answer. The bound
        //   states only what the column can hold, so it moves an existing refusal to the edge rather than
        //   inventing one. Crucially it does not reject the legacy absent-date sentinel as an absence
        //   marker: absence travels on this contract as a null, which the guard below skips and the
        //   helper admits, and the legacy provider itself converted that sentinel to a database null at
        //   SqlDataProvider.vb:L709 before the column ever saw it. The upper limit is stated at the last
        //   representable instant of 9999-12-31 rather than at that day's midnight, so a preserved
        //   perpetual value is admitted by the rule rather than refused by it.
        //
        // MIGRATION: NO ORDERING COMPARISON BETWEEN THE TWO DATES EXISTS, AND NONE IS DECLARED. Measured:
        //   both legacy validators are independent DataTypeCheck comparisons and NEITHER names a control
        //   to compare against - the attribute appears nowhere in the 225 lines, whereas the security
        //   roles screen does declare exactly such a cross-field comparison beside its two date checks.
        //   An end date preceding a start date was therefore storable, producing a module that could
        //   never be visible. That is a legacy defect, recorded on the contract and deliberately NOT
        //   corrected, because the minimal-change discipline requires validation rules to MATCH rather
        //   than to improve on the original, and any hardening is a separate, explicit decision.
        RuleFor(request => request.StartDate)
            .Must(SqlServerRange.CanStore).WithMessage(DateUnrepresentableMessage)
            .When(request => request.StartDate.HasValue);

        RuleFor(request => request.EndDate)
            .Must(SqlServerRange.CanStore).WithMessage(DateUnrepresentableMessage)
            .When(request => request.EndDate.HasValue);

        // ==================================================================================
        // THE ELEVEN MEMBERS THAT CARRY NO RULE, AND WHY EACH ONE CARRIES NONE
        // ==================================================================================
        //
        // MIGRATION: NO RULE ON TabId, AND THAT IS A CORRECTNESS REQUIREMENT. The page is mandatory
        //   structurally - it is argument one of the legacy second provider call and its column is
        //   non-nullable - but its check is "this page exists and belongs to the portal", which is
        //   STATEFUL and therefore the service's to make. A non-empty rule here would be actively WRONG:
        //   Tabs.TabID is IDENTITY (0, 1) at 01.00.00.SqlDataProvider:L140, so ZERO IS A LEGITIMATE PAGE
        //   and a rule rejecting the integer default would make the first page of every portal
        //   unreachable. Minus one must equally never be refused as an absence marker, the legacy query
        //   surface having used it as an "any page" wildcard.
        //
        // MIGRATION: NO RULE ON CacheTime, and the non-negative floor an earlier revision declared here
        //   is REMOVED rather than kept as a documented divergence. valCacheTime at L172 checked
        //   integrality alone, and the code-behind stored whatever parsed - a bare parse at L349-L350
        //   with no comparison - so a negative period was an accepted legacy submission, and a
        //   divergence that REFUSES input the original accepted is a narrowing of the contract. The
        //   discipline permits a documented divergence only where exact equivalence is impossible, which
        //   it is not here. Integrality still holds, on the type: the member is an integer, so a
        //   non-integer is refused by the binder before this validator runs. A blank legacy field wrote
        //   literally zero and an omitted property arrives as zero, so that submission is unchanged, and
        //   zero remains the meaningful value "do not cache". Minus one is meaningful in this
        //   neighbourhood too - a definition's default cache period of minus one meant "caching not
        //   applicable" and hid the field entirely - which is a further reason a floor is not this
        //   layer's to assert.
        //
        // MIGRATION: NO RULE ON ModuleOrder, and none may be written. Its initialiser of minus one is a
        //   LOAD-BEARING COMMAND meaning "append at the bottom of the pane", proven by the legacy
        //   create's dedicated branch under the comment "position module at bottom of pane", by the
        //   ordering routine's own parameter documentation - "-1 if to be added at the end" - by that
        //   routine implementing the instruction as "read the highest existing order and step past it",
        //   and by the move routine passing the same literal as a destination. Because the legacy
        //   integer sentinel for an absent value is the same number, any rule rejecting a negative here
        //   would destroy the instruction. The legacy screen placed no rule on this field either.
        //
        // MIGRATION: NO RULE ON Header OR Footer. Their columns are unbounded national text, and their
        //   legacy controls at L64 and L69 were six-row multi-line boxes with neither a maximum length
        //   nor a validator, so there is no length rule to port and inventing one would refuse content
        //   the legacy application stored. Their absent legacy value was the empty string rather than a
        //   null, which the projection layer reconciles.
        //
        // MIGRATION: NO RULE ON THE FOUR FLAGS AllTabs, InheritViewPermissions, DisplayTitle AND
        //   IsDeleted. A boolean is total: its type admits exactly the two values the column stores, so a
        //   rule could only restate the type. The legacy screen declared no validator on any of their
        //   check boxes. Two carry consequences a validator still cannot judge - the every-page toggle
        //   copies the module across pages or withdraws it, and the inherit flag causes view entries to
        //   be discarded rather than written - and both consequences depend on stored state, so the
        //   service owns them. The soft-delete marker is a STATE rather than a shape; note that the
        //   legacy save assigned it false unconditionally at L364, so every save silently un-deleted the
        //   module, and that defect is annotated on the contract and deliberately not reproduced.
        //
        // MIGRATION: NO RULE ON SetAsDefaultSettings OR ApplyToAllModules. Both are write-only intent
        //   rather than stored module state - neither is a column on either table, and no read contract
        //   echoes them back - and both are far-reaching: the first writes PORTAL-level configuration
        //   naming the default module and page, and the second rewrites placement rows belonging to every
        //   other module on every non-administrative page of the portal. Neither has a shape a stateless
        //   validator can judge, because their legitimacy depends on the portal's current state and on
        //   the caller's authority: the legacy screen disabled both check boxes, along with the
        //   every-page toggle and the page picker, for anyone outside the administrator role - exactly
        //   four controls, at L333-L338 - and that gate is now policy-based authorisation on the write
        //   path rather than a rule here, because a permission is not a property of the submitted state.
        //   The legacy screen declared no validator on either box.
        //
        // MIGRATION: NO OPTIMISTIC-CONCURRENCY RULE, BECAUSE THERE IS NOTHING TO PORT. Measured across
        //   the upgrade chain, neither table carries a row-version or time-stamp column, so this endpoint
        //   replaces rather than patches and two concurrent callers can overwrite one another exactly as
        //   two concurrent legacy postbacks could. Inventing a marker would be a schema change this
        //   migration forbids, and handling one would be persistence work rather than validation.
    }
}
