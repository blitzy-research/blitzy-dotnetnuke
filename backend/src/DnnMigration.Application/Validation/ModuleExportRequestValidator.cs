using DnnMigration.Application.Dtos.Module;
using FluentValidation;

namespace DnnMigration.Application.Validation;

// MIGRATION: this validator did not exist, and its absence was a published-contract defect rather than a
// missing nicety. POST .../modules/{moduleId}/export advertises ValidationProblemDetails for 400 - the
// document that carries a map of the request members refused - while the only 400 it could actually produce
// was a plain problem document raised by the service, because no validator was registered for this request
// type at all. A client written against the published description read `errors` and found nothing there. The
// rules below make the advertised shape reachable, so the description stops describing a response that could
// not occur.
//
// MIGRATION: the file-name rule is a PRESERVED legacy bound, not a new one. Website/admin/Modules/export.ascx
// declared its text box with MaxLength="200", and the click handler refused a blank name outright with
// `If cboFolders.SelectedIndex <> 0 And txtFile.Text <> ""`. ModuleExportRequest documents both facts and
// records that the length bound was "preserved as a documented parity bound rather than enforced by an
// attribute here" - which left the enforcement to nobody. It is enforced here now, which is where a
// field-level bound belongs.
//
// MIGRATION: the messages are IDENTICAL to the service's, deliberately. The service keeps its own guard
// because it is reachable from callers this validator does not sit in front of - the unit suites call it
// directly, and so would any future in-process consumer - so one condition has two enforcement points. Two
// points with two wordings would be two contracts; the same wording makes them one rule stated twice, and
// the duplication is annotated in both places so a change to either is visibly a change to a pair.

/// <summary>
/// Declares the field rules for <see cref="ModuleExportRequest"/>, the payload submitted to
/// <c>POST /api/v1/modules/{moduleId}/export</c> to obtain a module's content.
/// </summary>
/// <remarks>
/// <para>
/// ONE MEMBER CARRIES RULES AND THE OTHER DELIBERATELY CARRIES NONE. The file name is required and bounded,
/// because the legacy screen required and bounded it. The folder is accepted for vocabulary parity and
/// consumed by nothing - the file system left scope with the export's server-side write - so there is no
/// bound to preserve and no behaviour for a rule to protect. Its legacy control was a list whose first
/// entry was a non-selectable prompt and whose portal-root entry was valued with the EMPTY STRING, which is
/// exactly why the legacy guard tested the selected INDEX rather than the value: a test against the empty
/// string would have refused the portal root. There is no index here, and inventing a presence rule from
/// the half of the legacy guard that has no target would refuse requests the legacy admitted.
/// </para>
/// <para>
/// The module identifier is absent from this contract by design - the route carries it - so no rule here
/// touches it. That matters more than it looks: <c>Modules.ModuleID</c> is <c>IDENTITY(0, 1)</c>, so a
/// numeric floor anywhere near it would refuse the first module an installation ever created.
/// </para>
/// </remarks>
public sealed class ModuleExportRequestValidator : AbstractValidator<ModuleExportRequest>
{
    /// <summary>
    /// Largest file name the legacy export screen accepted.
    /// </summary>
    /// <remarks>
    /// The <c>MaxLength</c> of <c>txtFile</c> in <c>Website/admin/Modules/export.ascx</c>, and NOT a column
    /// width: this request writes no row, so the schema offers no bound to derive one from. It is the whole
    /// name's bound in the legacy and only a segment's bound here, since the server composed the real name
    /// from four parts - so enforcing it is if anything more permissive than the legacy was.
    /// </remarks>
    private const int FileNameMaximumLength = 200;

    /// <summary>
    /// Reported when no usable file name was supplied.
    /// </summary>
    /// <remarks>
    /// Word for word the message <c>ModuleService.ExportModuleAsync</c> raises for the same condition, so
    /// the caller reads one sentence whichever enforcement point answers first.
    /// </remarks>
    private const string FileNameRequiredMessage =
        "A file name is required so the returned document can be labelled.";

    /// <summary>
    /// Reported when the supplied file name is longer than the legacy screen accepted.
    /// </summary>
    /// <remarks>
    /// Composed from <see cref="FileNameMaximumLength"/> rather than spelling the number twice, so the bound
    /// and the sentence describing it cannot disagree.
    /// </remarks>
    private static readonly string FileNameTooLongMessage =
        FormattableString.Invariant($"The file name may not exceed {FileNameMaximumLength} characters.");

    /// <summary>
    /// Declares the rule set. Parameterless by design: a shape check needs no collaborator, and the
    /// assembly-wide scan that registers every validator in this folder constructs it with none.
    /// </summary>
    public ModuleExportRequestValidator()
    {
        RuleLevelCascadeMode = CascadeMode.Stop;
        ClassLevelCascadeMode = CascadeMode.Continue;

        // Blank, whitespace and absent are ONE state here, which is why the presence test is a predicate
        // rather than NotEmpty(). FluentValidation's NotEmpty treats a run of spaces as a supplied value,
        // while the legacy sentinel for an absent string was literally the empty string and the service
        // tests IsNullOrWhiteSpace - so NotEmpty alone would admit "   " here and have it refused one layer
        // deeper, by a plain problem document, which is the exact drift this validator exists to close.
        RuleFor(request => request.FileName)
            .Must(name => !string.IsNullOrWhiteSpace(name)).WithMessage(FileNameRequiredMessage)
            .MaximumLength(FileNameMaximumLength).WithMessage(FileNameTooLongMessage);
    }
}
