using DnnMigration.Application.Dtos.Module;
using DnnMigration.Domain.Common;
using FluentValidation;

namespace DnnMigration.Application.Validation;

// MIGRATION: the legacy screen Website/admin/Modules/modulesettings.ascx served both the add and the
// edit path, so the declarative rule set is identical to the create path's and the measured evidence is
// recorded once, on CreateModuleRequestValidator. In summary: four validators, all DataTypeCheck
// comparisons, and zero required-field validators anywhere on the screen.
//
// MIGRATION: the two contracts nevertheless have separate validators - unlike the role pair, where the
// planned inventory declares only a create validator - because the planned validation inventory names
// both a create and an update module validator, and because their identifier rules genuinely differ: a
// create names both the definition and the page, while an update names only the page. The DEFINITION is
// absent from the update contract because it is immutable after creation, which the provider surface
// proves rather than implies - the legacy create passed TEN arguments to the first provider call and the
// legacy update passed NINE, the missing one being the definition. The PAGE is present, because it is
// argument one of the legacy second provider call and changing it is how a move is expressed.
//
// MIGRATION: NO RULE IS PLACED ON TabId, AND THAT IS A CORRECTNESS REQUIREMENT RATHER THAN AN OMISSION.
// The page is mandatory structurally, but its check is "this page exists and belongs to the portal",
// which is STATEFUL and therefore the service's to make. A NotEmpty rule here would be actively WRONG:
// Tabs.TabID is IDENTITY(0,1), so ZERO IS A LEGITIMATE PAGE and NotEmpty - which rejects default(int) -
// would make the first page of every portal unreachable. Equally, -1 must never be rejected as an
// absence marker, since the legacy query surface used it as an "any page" wildcard.
//
// MIGRATION: no rule is placed on the two instruction flags, and that is deliberate rather than an
// omission. Naming the module as the portal default and propagating appearance across every
// non-administrative page are both far-reaching, but neither has a shape a stateless validator can
// judge - their legitimacy depends on the portal's current state, so the service evaluates and audits
// them. The legacy screen likewise placed no validator on chkDefault or chkAllModules. The same applies
// to IsDeleted: a soft delete is a state, not a shape.
//
// MIGRATION: NO ORDERING RULE BETWEEN THE TWO DATES EXISTS AND NONE IS DECLARED. A start-before-end
// comparison must not be added here: it would be an invented rule. Measured:
// the legacy screen's two date validators are independent DataTypeCheck comparisons and NEITHER declares
// a ControlToCompare - the attribute appears nowhere in the markup - so an end date preceding a start
// date was storable, producing a module that could never be visible. That is a legacy defect, recorded
// on the contract and deliberately NOT corrected, because the minimal-change discipline requires
// validation rules to MATCH rather than to improve on the original.
//
// MIGRATION: the non-negative cache-time rule that an earlier revision declared here is REMOVED rather
// than kept as a documented divergence. valCacheTime checked integrality only and the code-behind stored
// whatever parsed, so a negative period was an accepted legacy submission; a divergence that REFUSES input
// the original accepted is a narrowing of the contract, and the minimal-change discipline permits a
// documented divergence only where exact equivalence is impossible, which it is not here. Integrality
// survives on the CLR type. A blank legacy field wrote literally zero rather than a sentinel, and an
// omitted JSON property arrives as zero, so that submission is unchanged.

/// <summary>
/// Validates the shape of a module update submitted to <c>PUT /api/v1/portals/{portalId}/modules/{moduleId}</c>.
/// </summary>
/// <remarks>
/// Rule-level cascade stops at the first failure for a member; class-level cascade continues so every
/// field is reported in one response. Stateful rules - whether the module exists in the portal, and
/// whether the every-page toggle can be applied - belong to
/// <c>Application/Services/ModuleService.cs</c>.
/// </remarks>
public class UpdateModuleRequestValidator : AbstractValidator<UpdateModuleRequest>
{
    private const string VisibilityInvalidMessage = "Visibility must be Maximized, Minimized or None.";

    /// <summary>
    /// Message reported when a submitted date falls outside the range the terminal <c>datetime</c>
    /// column can hold. The wording is net-new: the legacy screen declared only a format check and had
    /// no such rule, and the failure it reports was previously a server fault naming no field.
    /// </summary>
    private static readonly string DateUnrepresentableMessage = FormattableString.Invariant($"A date must fall between {SqlServerRange.MinimumDateTime:yyyy-MM-dd} and ")
        + FormattableString.Invariant($"{SqlServerRange.MaximumDateTime:yyyy-MM-dd}, which is the range the stored column can hold.");

    // Measured terminal column widths. Only the two the update contract can actually carry remain: the
    // pane, alignment, colour and border bounds went with their excluded members, and with the border went
    // one of the legacy screen's four validators - an integer check whose message read "Invalid Border
    // (must be a number between 0 and 9)". Of the four, the two date checks and the cache-time check were
    // all DataTypeCheck format comparisons that the CLR types now enforce ahead of this validator, so what
    // survives here is the pair of column-width bounds and the enumeration check, and none is a presence
    // check.
    private const int ModuleTitleMaximumLength = 256;
    private const int IconFileMaximumLength = 100;

    /// <summary>
    /// Declares the rule set.
    /// </summary>
    public UpdateModuleRequestValidator()
    {
        RuleLevelCascadeMode = CascadeMode.Stop;
        ClassLevelCascadeMode = CascadeMode.Continue;

        // MIGRATION: the title is NOT subject to a presence rule. Measured: modulesettings.ascx declares
        // ZERO RequiredFieldValidator and ZERO ValidationSummary elements, and the title's text box carries
        // a rendered width but no maximum length - so the 256-character column bound is the only rule there
        // is to impose. Adding a non-empty rule would be a tightening the minimal-change discipline forbids.
        RuleFor(request => request.ModuleTitle)
            .MaximumLength(ModuleTitleMaximumLength);

        RuleFor(request => request.IconFile)
            .MaximumLength(IconFileMaximumLength);

        // MIGRATION: 5.3 - the legacy reader mapped 0 and the integer sentinel -1 alike to Maximized, 1 to
        // Minimized and 2 to None, and declared NO fallback branch, so an out-of-range stored value silently
        // became Maximized. That defect is not reproduced: exactly the three defined members are accepted
        // and anything else is refused. None is a REAL display state meaning "not rendered", not an absence.
        RuleFor(request => request.Visibility)
            .IsInEnum().WithMessage(VisibilityInvalidMessage);

        // MIGRATION: 5.5 - NO RULE IS PLACED ON CacheTime, and the non-negative floor an earlier revision
        // declared here is REMOVED. valCacheTime (modulesettings.ascx L172) checked integrality and nothing
        // else, and the code-behind stored whatever parsed - Int32.Parse(txtCacheTime.Text) at L349-L350,
        // with no comparison - so a negative period was an accepted submission and refusing one narrowed the
        // legacy contract without authority from the screen, the request contract or the AAP. Integrality is
        // still enforced, by the type: the member is int, so a non-integer is refused by the binder before
        // this validator runs. A blank legacy field stored literally zero and an omitted JSON property
        // arrives as zero, so that submission still behaves identically, and zero remains the meaningful
        // value "do not cache". Minus one is meaningful here too - L138 tests a definition's
        // DefaultCacheTime against the legacy integer sentinel of minus one - which is a further reason a
        // floor at zero is not this layer's to assert. The same removal is annotated on the create validator.
        //
        // MIGRATION: the two date members carry a REPRESENTABILITY bound and nothing else. The CLR date
        // type begins in the year one while the stored column begins in 1753, so a value the type accepts
        // can still be unstorable; unbounded, such a value passed every rule here and was refused by the
        // provider instead, which surfaces as a server fault naming no field rather than a field-level
        // answer. The bound states only what the column can hold. The two are still NOT compared to one
        // another - see the annotation on that omission - and the upper bound is stated at the last
        // representable instant of 9999-12-31 rather than at that day's midnight, so a preserved
        // perpetual value is admitted by the rule rather than refused by it.
        RuleFor(request => request.StartDate)
            .Must(SqlServerRange.CanStore).WithMessage(DateUnrepresentableMessage);

        RuleFor(request => request.EndDate)
            .Must(SqlServerRange.CanStore).WithMessage(DateUnrepresentableMessage);

        // MIGRATION: 5.1 - NO RULE IS PLACED ON ModuleOrder, and none may be. Its -1 is a LOAD-BEARING
        // COMMAND meaning "append at the bottom of the pane", proven by the legacy create's dedicated branch
        // under the comment "position module at bottom of pane" and by the ordering routine's own parameter
        // documentation, "-1 if to be added at the end". Because the legacy integer sentinel for an absent
        // value is ALSO -1, any rule rejecting a negative here would destroy the instruction. The legacy
        // screen placed no rule on this field either.
        //
        // MIGRATION: the two date members carry NO BUSINESS rule. Their legacy validators were DataTypeCheck
        // format comparisons, and format is now enforced by JSON deserialisation into a nullable date - an
        // unparseable value yields an RFC 7807 validation problem at the API edge before this validator runs.
        // Nothing further is checked, and in particular the two are NOT compared to one another; see the
        // annotation on the removed ordering rule above.
    }
}
