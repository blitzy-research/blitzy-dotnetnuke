using DnnMigration.Application.Dtos.Module;
using FluentValidation;

namespace DnnMigration.Application.Validation;

// MIGRATION: the legacy module settings screen Website/admin/Modules/modulesettings.ascx declares FOUR
// validators and, measured, ZERO required-field validators - not one, and not even on the title:
//
//   valtxtStartDate  CompareValidator, Type="Date",    Operator="DataTypeCheck"
//   valtxtEndDate    CompareValidator, Type="Date",    Operator="DataTypeCheck"
//   valCacheTime     CompareValidator, Type="Integer", Operator="DataTypeCheck"
//   valBorder        CompareValidator, Type="Integer", Operator="DataTypeCheck"
//
// A blank module title is therefore legitimate legacy behaviour, and the definition's friendly name is
// used as the heading - which is what the legacy code-behind did. No NotEmpty rule is placed on the
// title here for that reason. The border rule has no counterpart at all because the border member is
// excluded from every module contract along with the rest of the server-side rendering surface.
//
// MIGRATION: the three surviving DataTypeCheck comparisons are satisfied by the member types
// themselves. DateTime? cannot carry an unparseable date and int? cannot carry a non-integer once the
// wire format is JSON, so restating them would add rules that can never fire.
//
// MIGRATION: what this validator asserts instead is what the legacy relied on the postback pipeline to
// guarantee and a JSON caller can now violate. The identifier floors come from measured identity seeds:
// ModuleDefinitions.ModuleDefID is IDENTITY (1, 1) so a definition identifier must be positive, while
// Tabs.TabID is IDENTITY (0, 1) so 0 is a genuine page and only a negative value is rejected. Getting
// this backwards - rejecting TabId of 0 - would make the very first page of a portal unusable.
//
// MIGRATION: PaneName is required here even though it appears on no legacy validator, because
// TabModules.PaneName is nvarchar(50) NOT NULL and the legacy screen supplied it implicitly from the
// skin's pane picker rather than from a text field a user could blank. Its absence is now expressible,
// so it is asserted.
//
// MIGRATION: the non-negative cache-time rule is a deliberate, documented divergence. valCacheTime
// checked integrality only, so the legacy screen accepted a negative cache lifetime; nothing honours
// one, and ModuleDefinitions.DefaultCacheTime is int NOT NULL with a default of 0. Rejecting it at the
// boundary is recorded as an intentional strengthening.
//
// MIGRATION: the start-before-end rule is likewise new. The legacy screen validated each date's format
// independently and never compared them, so an end date before a start date was storable and produced a
// module that could never be visible. The comparison is added deliberately and is recorded as such.

/// <summary>
/// Validates the shape of a module creation submitted to <c>POST /api/v1/modules</c>.
/// </summary>
/// <remarks>
/// Rule-level cascade stops at the first failure for a member; class-level cascade continues so every
/// field is reported in one response. Stateful rules - whether the definition exists and is available to
/// the portal, whether the page exists in the portal - belong to
/// <c>Application/Services/ModuleService.cs</c>.
/// </remarks>
public class CreateModuleRequestValidator : AbstractValidator<CreateModuleRequest>
{
    private const string ModuleDefinitionRequiredMessage = "A module definition must be selected.";
    private const string TabRequiredMessage = "A page must be selected.";
    private const string PaneRequiredMessage = "A pane must be selected.";
    private const string VisibilityInvalidMessage = "Visibility must be Maximized, Minimized or None.";
    private const string CacheTimeNegativeMessage = "Invalid Cache Time";
    private const string EndBeforeStartMessage = "The end date must not precede the start date.";

    // Measured terminal column widths.
    private const int ModuleTitleMaximumLength = 256;
    private const int PaneNameMaximumLength = 50;
    private const int IconFileMaximumLength = 100;

    /// <summary>
    /// Declares the rule set.
    /// </summary>
    public CreateModuleRequestValidator()
    {
        RuleLevelCascadeMode = CascadeMode.Stop;
        ClassLevelCascadeMode = CascadeMode.Continue;

        // ModuleDefinitions.ModuleDefID is IDENTITY (1, 1): zero cannot name a real definition.
        RuleFor(request => request.ModuleDefId)
            .GreaterThan(0).WithMessage(ModuleDefinitionRequiredMessage);

        // Tabs.TabID is IDENTITY (0, 1): zero IS a real page, so only negatives are rejected.
        RuleFor(request => request.TabId)
            .GreaterThanOrEqualTo(0).WithMessage(TabRequiredMessage);

        RuleFor(request => request.ModuleTitle)
            .MaximumLength(ModuleTitleMaximumLength);

        RuleFor(request => request.PaneName)
            .NotEmpty().WithMessage(PaneRequiredMessage)
            .MaximumLength(PaneNameMaximumLength);

        RuleFor(request => request.IconFile)
            .MaximumLength(IconFileMaximumLength);

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
