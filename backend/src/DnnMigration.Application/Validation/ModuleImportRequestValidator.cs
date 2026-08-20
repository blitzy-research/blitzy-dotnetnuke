using DnnMigration.Application.Dtos.Module;
using FluentValidation;

namespace DnnMigration.Application.Validation;

/// <summary>
/// Declares the field rules for <see cref="ModuleImportRequest"/>, the payload submitted to <c>POST
/// /api/v1/modules/import</c> to replace a module's content.
/// </summary>
/// <remarks>
/// THE MODULE RULE IS A NULL TEST AND MUST NEVER BECOME A NUMERIC ONE. <c>Modules.ModuleID</c> is
/// <c>IDENTITY (0, 1)</c>, so ZERO IS A REAL MODULE - the first one a portal ever creates - and <c>-1</c>
/// is simultaneously the legacy integer sentinel, the legacy field's "unresolved" initial value and a real
/// portal identifier elsewhere in this same schema.
/// </remarks>
public sealed class ModuleImportRequestValidator : AbstractValidator<ModuleImportRequest>
{
    /// <summary>Reported when the request names no module to import into.</summary>
    /// <remarks>
    /// Word for word the message <c>ModuleService.ImportModuleAsync</c> raises for the same condition, so
    /// the caller reads one sentence whichever enforcement point answers first.
    /// </remarks>
    private const string ModuleRequiredMessage = "The module to import into must be supplied.";

    /// <summary>Reported when the request carries no document.</summary>
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

        RuleFor(request => request.Content)
            .Must(content => !string.IsNullOrWhiteSpace(content)).WithMessage(ContentRequiredMessage);
    }
}
