namespace DnnMigration.Application.Dtos.Module;

/// <summary>
/// The two key-value setting stores of one module, carried as the response body of <c>GET
/// /api/v1/modules/{moduleId}/settings</c> and as the request body of <c>PUT
/// /api/v1/modules/{moduleId}/settings</c>. A boundary contract and nothing more: no navigation property,
/// no tracked state, no behaviour and no domain entity, in either direction.
/// </summary>
/// <remarks>
/// <para>
/// That whole-bag write is neither of the two legacy behaviours. The legacy per-key writers documented that
/// an empty value would remove a setting but never implemented it, so removal needed one of four dedicated
/// delete members.
/// </para>
/// <para>
/// Key comparison is ordinal here and collation-defined in the store. Both maps are initialised with an
/// explicit ordinal comparer, matching the legacy in-memory collection, while no upgrade script assigns a
/// collation to either setting-name column - so store-side uniqueness follows the database collation, which
/// is case-insensitive on a default installation.
/// </para>
/// </remarks>
public sealed class ModuleSettingsDto
{
    /// <summary>
    /// The module whose module-scoped settings are carried in <see cref="ModuleSettings"/>, mapped from
    /// <c>Modules.ModuleID</c>.
    /// </summary>
    /// <remarks>
    /// The column is an identity seeded at zero, so <c>0</c> is a legitimate module: neither a "less than
    /// or equal to zero" test nor a comparison against the legacy absent-integer sentinel of -1 is a valid
    /// emptiness check for this member.
    /// </remarks>
    public int ModuleId { get; set; }

    /// <summary>
    /// The placement whose placement-scoped settings are carried in <see cref="TabModuleSettings"/>, mapped
    /// from <c>TabModules.TabModuleID</c>, or <see langword="null"/> when no placement was addressed.
    /// </summary>
    public int? TabModuleId { get; set; }

    /// <summary>
    /// The settings recorded against the module itself and therefore identical on every page the module
    /// appears on, from <c>dbo.ModuleSettings</c>. Keys are bounded at 50 characters and values at 2000.
    /// </summary>
    /// <remarks>
    /// Placement-scoped settings are never included; they are carried by <see cref="TabModuleSettings"/>.
    /// Values are never <see langword="null"/> - a stored SQL <c>NULL</c> surfaces as the empty string, so
    /// a key present with an empty value is a real setting and differs from an absent key.
    /// </remarks>
    public IReadOnlyDictionary<string, string> ModuleSettings { get; set; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>
    /// The settings recorded against one placement alone, and therefore specific to a single occurrence of
    /// the module on a single page, from <c>dbo.TabModuleSettings</c>. Keys are bounded at 50 characters
    /// and values at 2000.
    /// </summary>
    public IReadOnlyDictionary<string, string> TabModuleSettings { get; set; } =
        new Dictionary<string, string>(StringComparer.Ordinal);
}
