using DnnMigration.Application.Dtos.Module;
using FluentValidation;

namespace DnnMigration.Application.Validation;

// MIGRATION: this validator did not exist, and its absence was a published-contract defect rather than a
// missing nicety. POST .../modules/import advertises ValidationProblemDetails for 400 - the document that
// carries a map of the request members refused - while the only 400 it could actually produce was a plain
// problem document raised by the service, because no validator was registered for this request type at all.
// A client written against the published description read `errors` and found nothing there. The rules below
// make the advertised shape reachable, so the description stops describing a response that could not occur.
//
// MIGRATION: TWO RULES, BOTH PRESENCE, BECAUSE THE LEGACY SCREEN HAD EXACTLY ONE GUARD AND IT WAS PRESENCE.
// Import.ascx.vb's click handler tested `If Not cboFiles.SelectedItem Is Nothing` and otherwise showed the
// string literal "Please specify the file to import" - hardcoded and, uniquely on that screen, never
// localised, because its resource file holds no validation entry to resolve. In the target the document
// arrives in the body rather than being chosen from a list, so the one legacy guard becomes two: the document
// itself must be there, and the module it is destined for must be named, because this route names none.
// Nothing else is bounded. The DTO records that no length bound exists to preserve on either text member -
// the legacy control was a list rather than a text field, and this request writes no row - and the payload's
// well-formedness is a semantic question the service answers with the legacy's own wording.
//
// MIGRATION: the messages are IDENTICAL to the service's, deliberately. The service keeps its own guards
// because it is reachable from callers this validator does not sit in front of - the unit suites call it
// directly, and so would any future in-process consumer - so each condition has two enforcement points. Two
// points with two wordings would be two contracts; the same wording makes them one rule stated twice, and the
// duplication is annotated in both places so a change to either is visibly a change to a pair.

/// <summary>
/// Declares the field rules for <see cref="ModuleImportRequest"/>, the payload submitted to
/// <c>POST /api/v1/modules/import</c> to replace a module's content.
/// </summary>
/// <remarks>
/// <para>
/// THE MODULE RULE IS A NULL TEST AND MUST NEVER BECOME A NUMERIC ONE. <c>Modules.ModuleID</c> is
/// <c>IDENTITY (0, 1)</c>, so ZERO IS A REAL MODULE - the first one a portal ever creates - and <c>-1</c> is
/// simultaneously the legacy integer sentinel, the legacy field's "unresolved" initial value and a real
/// portal identifier elsewhere in this same schema. A caller sending either has made a LOOKUP, which must be
/// allowed to fail as a lookup in the service rather than be misreported here as a malformed request. That is
/// precisely why the member is nullable, and a rule of <c>GreaterThan(0)</c> or a comparison against
/// <c>-1</c> would undo the reason for its shape.
/// </para>
/// <para>
/// THE CONTENT RULE IS PRESENCE ONLY, AND STOPS SHORT OF WELL-FORMEDNESS ON PURPOSE. Whether the document
/// parses, whether its root is a <c>content</c> element and whether its <c>type</c> attribute names this
/// module are all questions the service answers, with the legacy screen's own three messages, and two of the
/// three need the module's own definition to answer at all. Restating any of them here would put one rule in
/// two places with two different answers available - which is the split this project has already collapsed
/// once on the paging contracts - and would make the field-level answer claim a precision it does not have.
/// </para>
/// <para>
/// NEITHER TEXT MEMBER IS BOUNDED AND NEITHER IS REQUIRED. The file name stopped being a correctness boundary
/// when the type check moved onto the payload's own attribute, so it is parity metadata that nothing branches
/// on; the folder is likewise accepted for vocabulary parity and consumed by nothing, the file system having
/// left scope. Inventing a bound for either would be a restriction on callers that no legacy submission ever
/// met.
/// </para>
/// </remarks>
public sealed class ModuleImportRequestValidator : AbstractValidator<ModuleImportRequest>
{
    /// <summary>
    /// Reported when the request names no module to import into.
    /// </summary>
    /// <remarks>
    /// Word for word the message <c>ModuleService.ImportModuleAsync</c> raises for the same condition, so the
    /// caller reads one sentence whichever enforcement point answers first.
    /// </remarks>
    private const string ModuleRequiredMessage = "The module to import into must be supplied.";

    /// <summary>
    /// Reported when the request carries no document.
    /// </summary>
    /// <remarks>
    /// Word for word the service's message for the same condition, for the same reason.
    /// </remarks>
    private const string ContentRequiredMessage = "The submitted document is empty.";

    /// <summary>
    /// Declares the rule set. Parameterless by design: a shape check needs no collaborator, and the
    /// assembly-wide scan that registers every validator in this folder constructs it with none.
    /// </summary>
    public ModuleImportRequestValidator()
    {
        RuleLevelCascadeMode = CascadeMode.Stop;
        ClassLevelCascadeMode = CascadeMode.Continue;

        // Absence alone, never sign - see the note on this class.
        RuleFor(request => request.ModuleId)
            .NotNull().WithMessage(ModuleRequiredMessage);

        // Blank, whitespace and absent are ONE state here, which is why the presence test is a predicate
        // rather than NotEmpty(). FluentValidation's NotEmpty treats a run of spaces as a supplied value,
        // while the service tests IsNullOrWhiteSpace - so NotEmpty alone would admit "   " here and have it
        // refused one layer deeper, by a plain problem document, which is the exact drift this validator
        // exists to close. Note that whitespace INSIDE a document is a different matter entirely and is
        // preserved: the service reads the payload with PreserveWhitespace precisely so a payload of spaces
        // reaches the module intact. What is refused here is a document that is nothing BUT whitespace.
        RuleFor(request => request.Content)
            .Must(content => !string.IsNullOrWhiteSpace(content)).WithMessage(ContentRequiredMessage);
    }
}
