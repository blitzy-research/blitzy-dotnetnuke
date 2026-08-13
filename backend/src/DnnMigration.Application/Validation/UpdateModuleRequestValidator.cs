using DnnMigration.Application.Dtos.Module;
using DnnMigration.Domain.Common;
using FluentValidation;

namespace DnnMigration.Application.Validation;

/// <summary>
/// Validates the SHAPE of a module update submitted to <c>PUT /api/v1/modules/{moduleId}</c>, and nothing
/// beyond shape.
/// </summary>
/// <remarks>
/// <para>
/// THE MODULE IDENTIFIER CARRIES NO BOUND TEST, AND THAT IS A CORRECTNESS REQUIREMENT RATHER THAN AN
/// OMISSION - it is not even a member of the request, because the route supplies it.
/// </para>
/// <para>
/// THIS VALIDATOR DELIBERATELY DUPLICATES <c>CreateModuleRequestValidator</c> RATHER THAN REUSING IT. It
/// does not derive from it, does not include its rule set, and shares no base type or generic validator
/// abstraction with it: a shared base is forbidden and each file in this folder stands alone.
/// </para>
/// </remarks>
public class UpdateModuleRequestValidator : AbstractValidator<UpdateModuleRequest>
{
    /// <summary>Message reported when the submitted visibility is not one of the three defined members.</summary>
    /// <remarks>
    /// Net-new wording: the legacy radio-button list could not express an undefined value, so the screen
    /// declared no message for one. The three names are the enumeration's own, and the numbers behind them
    /// - 0, 1 and 2 - are load-bearing legacy data stored directly in the column.
    /// </remarks>
    private const string VisibilityInvalidMessage = "Visibility must be Maximized, Minimized or None.";

    /// <summary>
    /// Message reported when a submitted date falls outside the range the terminal <c>datetime</c> column
    /// can hold.
    /// </summary>
    private static readonly string DateUnrepresentableMessage = FormattableString.Invariant($"A date must fall between {SqlServerRange.MinimumDateTime:yyyy-MM-dd} and ")
        + FormattableString.Invariant($"{SqlServerRange.MaximumDateTime:yyyy-MM-dd}, which is the range the stored column can hold.");

    /// <summary>
    /// Maximum stored length of the module title: <c>Modules.ModuleTitle nvarchar (256) NULL</c>, declared
    /// at <c>01.00.00.SqlDataProvider:L226</c> and unchanged by the upgrade chain.
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
    /// Declares the rule set. Parameterless by design: this validator needs no policy, no clock, no options
    /// object and no repository, and the assembly-wide scan that registers every validator in this folder
    /// constructs it with none.
    /// </summary>
    public UpdateModuleRequestValidator()
    {
        RuleLevelCascadeMode = CascadeMode.Stop;
        ClassLevelCascadeMode = CascadeMode.Continue;

        // THE TITLE IS NOT SUBJECT TO A PRESENCE RULE. Measured: modulesettings.ascx declares no
        // required-field validator and no validation summary anywhere in its 225 lines, and its title box
        // carries a rendered width but no maximum length - so the 256-character column bound is the only
        // rule there is to impose.
        RuleFor(request => request.ModuleTitle)
            .MaximumLength(ModuleTitleMaximumLength)
            .When(request => !string.IsNullOrEmpty(request.ModuleTitle));

        // The icon is a STORED NAME OR PATH and nothing more - no upload, no location resolution and no
        // reachability promise, file-system handling being outside this migration.
        RuleFor(request => request.IconFile)
            .MaximumLength(IconFileMaximumLength)
            .When(request => !string.IsNullOrEmpty(request.IconFile));

        RuleFor(request => request.IconFile)
            .Must(IconReferenceRules.IsContained)
            .WithMessage(IconReferenceRules.NotContainedMessage);

        RuleFor(request => request.Visibility)
            .IsInEnum().WithMessage(VisibilityInvalidMessage);

        // NO ORDERING COMPARISON BETWEEN THE TWO DATES EXISTS, AND NONE IS DECLARED. Measured: both legacy
        // validators are independent DataTypeCheck comparisons and NEITHER names a control to compare
        // against - the attribute appears nowhere in the 225 lines, whereas the security roles screen does
        // declare exactly such a cross-field comparison beside its two date checks.
        RuleFor(request => request.StartDate)
            .Must(SqlServerRange.CanStore).WithMessage(DateUnrepresentableMessage)
            .When(request => request.StartDate.HasValue);

        RuleFor(request => request.EndDate)
            .Must(SqlServerRange.CanStore).WithMessage(DateUnrepresentableMessage)
            .When(request => request.EndDate.HasValue);
    }
}
