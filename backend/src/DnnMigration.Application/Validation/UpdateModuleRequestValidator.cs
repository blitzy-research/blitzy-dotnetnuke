using DnnMigration.Application.Dtos.Module;
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
// create names the definition and the page it is adding to, while an update names neither, since
// re-pointing an existing instance at a different definition would orphan its content and moving it to a
// different page is a create-and-delete rather than an edit.
//
// MIGRATION: no rule is placed on the two instruction flags, and that is deliberate rather than an
// omission. Naming the module as the portal default and propagating appearance across every
// non-administrative page are both far-reaching, but neither has a shape a stateless validator can
// judge - their legitimacy depends on the portal's current state, so the service evaluates and audits
// them. The legacy screen likewise placed no validator on chkDefault or chkAllModules.
//
// MIGRATION: as on the create path, the non-negative cache-time rule and the start-before-end
// comparison are documented divergences rather than ports: valCacheTime checked integrality only, and
// the legacy screen never compared the two dates to each other.

/// <summary>
/// Validates the shape of a module update submitted to <c>PUT /api/v1/modules/{moduleId}</c>.
/// </summary>
/// <remarks>
/// Rule-level cascade stops at the first failure for a member; class-level cascade continues so every
/// field is reported in one response. Stateful rules - whether the module exists in the portal, and
/// whether the every-page toggle can be applied - belong to
/// <c>Application/Services/ModuleService.cs</c>.
/// </remarks>
public class UpdateModuleRequestValidator : AbstractValidator<UpdateModuleRequest>
{
    private const string PaneRequiredMessage = "A pane must be selected.";
    private const string VisibilityInvalidMessage = "Visibility must be Maximized, Minimized or None.";
    private const string CacheTimeNegativeMessage = "Invalid Cache Time";

    /// <summary>
    /// Reproduced character for character from <c>ModuleSettings.ascx.resx</c> valBorder.ErrorMessage. The
    /// leading <c>&lt;br&gt;</c> the legacy resource carried was markup for the inline validator summary and
    /// is dropped; the wording an existing operator recognises is preserved.
    /// </summary>
    private const string BorderInvalidMessage = "Invalid Border (must be a number between 0 and 9)";
    private const string EndBeforeStartMessage = "The end date must not precede the start date.";

    // Measured terminal column widths.
    private const int ModuleTitleMaximumLength = 256;
    private const int PaneNameMaximumLength = 50;
    private const int IconFileMaximumLength = 100;

    /// <summary>The bound on <c>TabModules.Alignment</c> (<c>nvarchar(10) NULL</c>).</summary>
    private const int AlignmentMaximumLength = 10;

    /// <summary>The bound on <c>TabModules.Color</c> (<c>nvarchar(20) NULL</c>).</summary>
    private const int ColorMaximumLength = 20;

    /// <summary>
    /// The bound on <c>TabModules.Border</c> (<c>nvarchar(1) NULL</c>) — a single character used as a flag
    /// by the legacy renderer, not a width in pixels.
    /// </summary>
    private const int BorderMaximumLength = 1;

    /// <summary>
    /// Declares the rule set.
    /// </summary>
    public UpdateModuleRequestValidator()
    {
        RuleLevelCascadeMode = CascadeMode.Stop;
        ClassLevelCascadeMode = CascadeMode.Continue;

        RuleFor(request => request.ModuleTitle)
            .MaximumLength(ModuleTitleMaximumLength);

        RuleFor(request => request.PaneName)
            .NotEmpty().WithMessage(PaneRequiredMessage)
            .MaximumLength(PaneNameMaximumLength);

        RuleFor(request => request.IconFile)
            .MaximumLength(IconFileMaximumLength);

        // MIGRATION: the legacy appearance fields carried no validator of their own on
        // Website/admin/Modules/modulesettings.ascx, so the only rule imposed here is the column bound the
        // store would otherwise refuse. Their contents stay opaque: nothing pattern-checks an alignment
        // keyword or a colour, because the legacy renderer accepted whatever the operator typed and a
        // stricter rule would refuse a value the old screen stored happily.
        RuleFor(request => request.Alignment)
            .MaximumLength(AlignmentMaximumLength);

        RuleFor(request => request.Color)
            .MaximumLength(ColorMaximumLength);

        // MIGRATION: the legacy screen DID validate this one. modulesettings.ascx declares a
        // CompareValidator with Operator="DataTypeCheck" Type="Integer" against a MaxLength="1" text box,
        // whose message is ModuleSettings.ascx.resx valBorder.ErrorMessage. A single character that must
        // parse as an integer is exactly one digit, so the rule is reproduced as a digit test and the
        // legacy message text is preserved (its leading <br> was markup and is dropped).
        RuleFor(request => request.Border)
            .MaximumLength(BorderMaximumLength)
            .Must(border => border is null || (border.Length == 1 && char.IsAsciiDigit(border[0])))
                .WithMessage(BorderInvalidMessage);

        RuleFor(request => request.Visibility)
            .IsInEnum().WithMessage(VisibilityInvalidMessage);

        RuleFor(request => request.CacheTime)
            .GreaterThanOrEqualTo(0).WithMessage(CacheTimeNegativeMessage)
            .When(request => request.CacheTime.HasValue);

        RuleFor(request => request.EndDate)
            .GreaterThanOrEqualTo(request => request.StartDate!.Value)
                .WithMessage(EndBeforeStartMessage)
            .When(request => request.StartDate.HasValue && request.EndDate.HasValue);
    }
}
