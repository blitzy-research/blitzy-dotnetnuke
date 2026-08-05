using DnnMigration.Application.Dtos.Module;
using FluentValidation;

namespace DnnMigration.Application.Validation;

// MIGRATION: this validator did not exist, and two distinct defects followed from that.
//
// First, an explicitly null map. Both dictionary members are non-nullable reference types carrying an
// initialiser, which makes a null look impossible to a reader - but an initialiser only runs when the
// deserialiser does not assign, and a body carrying "moduleSettings": null assigns null over it. The
// service guarded that with an argument-null throw, so a caller could reach a server fault with a
// syntactically valid body. A null map is now a field-level answer here, and the service's guard is
// restated as a refusal rather than a throw.
//
// Second, cardinality. The service already bounded every setting NAME at fifty characters and every
// VALUE at two thousand, so no single entry was unbounded - but nothing bounded the NUMBER of entries,
// and the two limits multiply. A body well inside the request size limit can carry tens of thousands of
// short settings, each of which becomes a tracked entity and a row in one transaction. The bounds below
// are the missing dimension.
//
// MIGRATION: the key and value bounds are DUPLICATED from the service on purpose, and the duplication is
// the point rather than an oversight. The service keeps its own copies because it is reachable from
// callers this validator does not sit in front of, and this validator states them because a caller
// deserves the field-level answer that names the offending setting. The two must agree; they are
// annotated in both places so a change to either is visibly a change to a pair.

/// <summary>
/// Declares the field rules for <see cref="ModuleSettingsDto"/>, the payload submitted to
/// <c>PUT /api/v1/modules/{moduleId}/settings</c> to replace a module's settings.
/// </summary>
/// <remarks>
/// <para>
/// The rules bound three things the request previously left open: that each map is present at all, how
/// many entries it may carry, and how many entries the two may carry between them. The last is what
/// bounds the size of the single transaction the service opens, which is why it is stated separately
/// rather than left implied by the per-map bounds.
/// </para>
/// <para>
/// An EMPTY map is valid and is not the same as an absent one. Replacing a module's settings with none is
/// how every setting is cleared, so a rule demanding entries would remove the only way to do that.
/// </para>
/// <para>
/// No rule is placed on the two identifiers. The module identifier is taken from the route, so a body
/// echoing it, contradicting it, or omitting it changes nothing the service reads, and refusing a
/// mismatch here would invent a rule the endpoint does not have. The placement identifier is likewise
/// taken from the query string, and its stored form is an identity seeded at one while the module's is
/// seeded at zero, so no numeric bound is shared between them in any case.
/// </para>
/// </remarks>
public sealed class ModuleSettingsDtoValidator : AbstractValidator<ModuleSettingsDto>
{
    /// <summary>
    /// Largest number of settings either scope may carry.
    /// </summary>
    /// <remarks>
    /// A net-new bound with no legacy counterpart, and generous by design: seventy-two distinct setting
    /// names exist across EVERY bundled module in the whole legacy application, so this admits more than
    /// three times the vocabulary the entire product ever used, for a single module. It is stated to
    /// bound row growth and transaction size, not to constrain any real configuration.
    /// </remarks>
    private const int SettingsPerScopeMaximum = 250;

    /// <summary>
    /// Largest number of settings the two scopes may carry between them in one submission.
    /// </summary>
    /// <remarks>
    /// Lower than twice the per-scope bound, so that it genuinely binds when both maps are large rather
    /// than being implied by them. This is the figure that bounds one transaction's row count, which is
    /// the resource the per-scope bounds alone do not protect.
    /// </remarks>
    private const int SettingsAggregateMaximum = 400;

    /// <summary>
    /// Width of <c>ModuleSettings.SettingName</c> and <c>TabModuleSettings.SettingName</c>. Must agree
    /// with the service's own copy of this bound.
    /// </summary>
    private const int SettingNameMaximumLength = 50;

    /// <summary>
    /// Terminal width of <c>ModuleSettings.SettingValue</c> and <c>TabModuleSettings.SettingValue</c>,
    /// established when the upgrade chain rebuilt the module settings table and superseded the 256 of the
    /// baseline column. Must agree with the service's own copy of this bound.
    /// </summary>
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
    /// <remarks>
    /// Rule-level cascade stops so that a null map yields one message rather than a null complaint
    /// followed by a count complaint against the same absent map. Class-level cascade continues so both
    /// maps are reported together.
    /// </remarks>
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

        // The aggregate rule is stated on the request rather than on either map, because it is a property
        // of the submission as a whole and naming one map for it would misdirect the caller. It runs only
        // when both maps are present, so a null map is reported as a null map and not as a count.
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

    /// <summary>
    /// Determines whether every setting name in a map fits the stored column.
    /// </summary>
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

    /// <summary>
    /// Determines whether every setting value in a map fits the stored column.
    /// </summary>
    /// <param name="map">The submitted map, which this rule only sees when it is not null.</param>
    /// <returns><see langword="true"/> when every value fits; otherwise <see langword="false"/>.</returns>
    /// <remarks>
    /// A null value is not a failure and is not tested here. The contract states that a stored SQL null
    /// surfaces as the empty string, and the service coerces an absent value to the empty string on the
    /// way in, so a null value is a real setting with no content rather than a malformed entry.
    /// </remarks>
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
