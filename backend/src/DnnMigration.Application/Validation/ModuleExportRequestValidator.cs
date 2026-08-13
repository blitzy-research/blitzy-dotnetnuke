using DnnMigration.Application.Dtos.Module;
using FluentValidation;

namespace DnnMigration.Application.Validation;

/// <summary>
/// Declares the field rules for <see cref="ModuleExportRequest"/>, the payload submitted to <c>POST
/// /api/v1/modules/{moduleId}/export</c> to obtain a module's content.
/// </summary>
/// <remarks>
/// The module identifier is absent from this contract by design - the route carries it - so no rule here
/// touches it. That matters more than it looks: <c>Modules.ModuleID</c> is <c>IDENTITY(0, 1)</c>, so a
/// numeric floor anywhere near it would refuse the first module an installation ever created.
/// </remarks>
public sealed class ModuleExportRequestValidator : AbstractValidator<ModuleExportRequest>
{
    /// <summary>Largest file name the legacy export screen accepted.</summary>
    /// <remarks>
    /// The <c>MaxLength</c> of <c>txtFile</c> in <c>Website/admin/Modules/export.ascx</c>, and NOT a column
    /// width: this request writes no row, so the schema offers no bound to derive one from. It is the whole
    /// name's bound in the legacy and only a segment's bound here, since the server composed the real name
    /// from four parts - so enforcing it is if anything more permissive than the legacy was.
    /// </remarks>
    private const int FileNameMaximumLength = 200;

    /// <summary>Reported when no usable file name was supplied.</summary>
    /// <remarks>
    /// Word for word the message <c>ModuleService.ExportModuleAsync</c> raises for the same condition, so
    /// the caller reads one sentence whichever enforcement point answers first.
    /// </remarks>
    private const string FileNameRequiredMessage =
        "A file name is required so the returned document can be labelled.";

    /// <summary>Reported when the supplied file name is longer than the legacy screen accepted.</summary>
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
        // rather than NotEmpty().
        RuleFor(request => request.FileName)
            .Must(name => !string.IsNullOrWhiteSpace(name)).WithMessage(FileNameRequiredMessage)
            .MaximumLength(FileNameMaximumLength).WithMessage(FileNameTooLongMessage);
    }
}
