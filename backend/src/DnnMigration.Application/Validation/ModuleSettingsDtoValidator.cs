using DnnMigration.Application.Dtos.Module;
using FluentValidation;

namespace DnnMigration.Application.Validation;

/// <summary>
/// Declares the field rules for <see cref="ModuleSettingsDto"/>, the payload submitted to <c>PUT
/// /api/v1/modules/{moduleId}/settings</c> to replace a module's settings.
/// </summary>
/// <remarks>
/// No rule is placed on the two identifiers. The module identifier is taken from the route, so a body
/// echoing it, contradicting it, or omitting it changes nothing the service reads, and refusing a mismatch
/// here would invent a rule the endpoint does not have.
/// </remarks>
public sealed class ModuleSettingsDtoValidator : AbstractValidator<ModuleSettingsDto>
{
    /// <summary>Largest number of settings either scope may carry.</summary>
    /// <remarks>
    /// A net-new bound with no legacy counterpart, and generous by design: seventy-two distinct setting
    /// names exist across EVERY bundled module in the whole legacy application, so this admits more than
    /// three times the vocabulary the entire product ever used, for a single module. It is stated to bound
    /// row growth and transaction size, not to constrain any real configuration.
    /// </remarks>
    private const int SettingsPerScopeMaximum = 250;

    /// <summary>Largest number of settings the two scopes may carry between them in one submission.</summary>
    /// <remarks>
    /// Lower than twice the per-scope bound, so that it genuinely binds when both maps are large rather
    /// than being implied by them. This is the figure that bounds one transaction's row count, which is the
    /// resource the per-scope bounds alone do not protect.
    /// </remarks>
    private const int SettingsAggregateMaximum = 400;

    /// <summary>
    /// Width of <c>ModuleSettings.SettingName</c> and <c>TabModuleSettings.SettingName</c>. Must agree with
    /// the service's own copy of this bound.
    /// </summary>
    private const int SettingNameMaximumLength = 50;

    /// <summary>Must agree with the service's own copy of this bound.</summary>
    private const int SettingValueMaximumLength = 2000;

    private const string ModuleSettingsMissingMessage =
        "The module settings map is required. Send an empty object to clear every module setting.";

    private const string TabModuleSettingsMissingMessage =
        "The placement settings map is required. Send an empty object to clear every placement setting.";

    private static readonly string PerScopeTooManyMessage = FormattableString.Invariant(
        $"A settings map must carry no more than {SettingsPerScopeMaximum} entries.");

    private static readonly string AggregateTooManyMessage = FormattableString.Invariant(
        $"The two settings maps must carry no more than {SettingsAggregateMaximum} entries between them.");

    private static readonly string NameBlankMessage = "A setting name must not be blank.";

    private static readonly string NameTooLongMessage = FormattableString.Invariant(
        $"A setting name must be no longer than {SettingNameMaximumLength} characters.");

    private static readonly string ValueTooLongMessage = FormattableString.Invariant(
        $"A setting value must be no longer than {SettingValueMaximumLength} characters.");

    /// <summary>
    /// Initialises a new instance of the <see cref="ModuleSettingsDtoValidator"/> class and declares its
    /// rules.
    /// </summary>
    public ModuleSettingsDtoValidator()
    {
        RuleLevelCascadeMode = CascadeMode.Stop;
        ClassLevelCascadeMode = CascadeMode.Continue;

        RuleFor(request => request.ModuleSettings)
            .NotNull()
            .WithMessage(ModuleSettingsMissingMessage)
            .Must(map => map.Count <= SettingsPerScopeMaximum)
            .WithMessage(PerScopeTooManyMessage)
            .Must(HaveWellFormedNames)
            .WithMessage(NameBlankMessage)
            .Must(HaveNamesWithinLength)
            .WithMessage(NameTooLongMessage)
            .Must(HaveValuesWithinLength)
            .WithMessage(ValueTooLongMessage);

        RuleFor(request => request.TabModuleSettings)
            .NotNull()
            .WithMessage(TabModuleSettingsMissingMessage)
            .Must(map => map.Count <= SettingsPerScopeMaximum)
            .WithMessage(PerScopeTooManyMessage)
            .Must(HaveWellFormedNames)
            .WithMessage(NameBlankMessage)
            .Must(HaveNamesWithinLength)
            .WithMessage(NameTooLongMessage)
            .Must(HaveValuesWithinLength)
            .WithMessage(ValueTooLongMessage);

        RuleFor(request => request)
            .Must(request =>
                request.ModuleSettings.Count + request.TabModuleSettings.Count <= SettingsAggregateMaximum)
            .When(request => request.ModuleSettings is not null && request.TabModuleSettings is not null)
            .WithName(nameof(ModuleSettingsDto.ModuleSettings))
            .WithMessage(AggregateTooManyMessage);
    }

    /// <summary>
    /// Determines whether every setting name in a map carries at least one non-white-space character.
    /// </summary>
    /// <param name="map">The submitted map, which this rule only sees when it is not null.</param>
    /// <returns><see langword="true"/> when no name is blank; otherwise <see langword="false"/>.</returns>
    /// <remarks>
    /// A blank name cannot address a setting: the stored column is part of the table's primary key, so two
    /// blank names are the same key and the second write silently replaces the first.
    /// </remarks>
    private static bool HaveWellFormedNames(IReadOnlyDictionary<string, string> map)
    {
        foreach (string name in map.Keys)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Determines whether every setting name in a map fits the stored column.</summary>
    /// <param name="map">The submitted map, which this rule only sees when it is not null.</param>
    /// <returns><see langword="true"/> when every name fits; otherwise <see langword="false"/>.</returns>
    private static bool HaveNamesWithinLength(IReadOnlyDictionary<string, string> map)
    {
        foreach (string name in map.Keys)
        {
            if (name.Length > SettingNameMaximumLength)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Determines whether every setting value in a map fits the stored column.</summary>
    /// <param name="map">The submitted map, which this rule only sees when it is not null.</param>
    /// <returns><see langword="true"/> when every value fits; otherwise <see langword="false"/>.</returns>
    private static bool HaveValuesWithinLength(IReadOnlyDictionary<string, string> map)
    {
        foreach (string value in map.Values)
        {
            if (value is not null && value.Length > SettingValueMaximumLength)
            {
                return false;
            }
        }

        return true;
    }
}
