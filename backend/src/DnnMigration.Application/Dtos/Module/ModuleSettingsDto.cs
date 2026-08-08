namespace DnnMigration.Application.Dtos.Module;

/// <summary>
/// The two key-value setting stores of one module, carried as the response body of
/// <c>GET /api/v1/modules/{moduleId}/settings</c> and as the request body of
/// <c>PUT /api/v1/modules/{moduleId}/settings</c>. A boundary contract and nothing more: no
/// navigation property, no tracked state, no behaviour and no domain entity, in either direction.
/// </summary>
/// <remarks>
/// <para>
/// The two bags are disjoint scopes and must never be merged. Module-scoped rows hang off
/// <c>Modules.ModuleID</c> and placement-scoped rows off <c>TabModules.TabModuleID</c>, so a module
/// placed on every page has one module-scoped bag but many placement-scoped bags.
/// </para>
/// <para>
/// A write replaces each scope whole: a key the request omits is DELETED.
/// <c>ModuleService.UpdateModuleSettingsAsync</c> deletes every stored key the submitted map does not
/// contain, updates the changed ones, inserts the new ones and commits as one unit of work. So a
/// partial submission is destructive - read the bag, modify it, send it back whole - and an empty map
/// clears the scope. A key whose value is the empty string is NOT a deletion; it stores an empty
/// string.
/// </para>
/// <para>
/// MIGRATION: that whole-bag write is neither of the two legacy behaviours. The legacy per-key writers
/// documented that an empty value would remove a setting but never implemented it, so removal needed
/// one of four dedicated delete members. Here omission IS the deletion, which is why those four
/// members have no counterpart, and an empty value is still an empty value.
/// </para>
/// <para>
/// Key comparison is ordinal here and collation-defined in the store. Both maps are initialised with
/// an explicit ordinal comparer, matching the legacy in-memory collection, while no upgrade script
/// assigns a collation to either setting-name column - so store-side uniqueness follows the database
/// collation, which is case-insensitive on a default installation. The service therefore reconciles
/// submitted names case-insensitively, and no consumer may rely on two keys differing only in case
/// being two settings.
/// </para>
/// <para>
/// Dictionary keys are data, not identifiers. The API edge's property-naming policy must stay confined
/// to property names: applied to dictionary keys it would rewrite every setting name, and a rewritten
/// name is a different setting. The key vocabulary is open by design, having been written by whichever
/// module owned the setting.
/// </para>
/// <para>
/// Not carried here: no module or placement field (title, header, footer, dates, all-pages flag, cache
/// period, icon, visibility, alignment, colour, border), which belong to the module resource's own
/// contracts; and no permission entry, paging envelope, audit field or cache metadata.
/// </para>
/// </remarks>
public sealed class ModuleSettingsDto
{
    /// <summary>
    /// The module whose module-scoped settings are carried in <see cref="ModuleSettings"/>, mapped
    /// from <c>Modules.ModuleID</c>.
    /// </summary>
    /// <remarks>
    /// The column is an identity seeded at zero, so <c>0</c> is a legitimate module: neither a
    /// "less than or equal to zero" test nor a comparison against the legacy absent-integer
    /// sentinel of -1 is a valid emptiness check for this member.
    /// </remarks>
    public int ModuleId { get; set; }

    /// <summary>
    /// The placement whose placement-scoped settings are carried in
    /// <see cref="TabModuleSettings"/>, mapped from <c>TabModules.TabModuleID</c>, or
    /// <see langword="null"/> when no placement was addressed.
    /// </summary>
    /// <remarks>
    /// The only nullable member, and <see langword="null"/> is the only spelling of absence: the
    /// placement-scoped map is then empty while <see cref="ModuleSettings"/> remains meaningful on
    /// its own. A submitted -1 is an exact lookup value rather than absence, and since
    /// <c>TabModuleID</c> is seeded at one it normally matches no placement - answered as a
    /// placement that does not exist, not as one that was never named.
    /// </remarks>
    public int? TabModuleId { get; set; }

    /// <summary>
    /// The settings recorded against the module itself and therefore identical on every page the
    /// module appears on, from <c>dbo.ModuleSettings</c>. Keys are bounded at 50 characters and
    /// values at 2000.
    /// </summary>
    /// <remarks>
    /// Placement-scoped settings are never included; they are carried by
    /// <see cref="TabModuleSettings"/>. Values are never <see langword="null"/> - a stored SQL
    /// <c>NULL</c> surfaces as the empty string, so a key present with an empty value is a real
    /// setting and differs from an absent key. The bounds are documentation: declarative validation
    /// belongs with the request contracts and persistence limits with the entity configurations.
    /// </remarks>
    public IReadOnlyDictionary<string, string> ModuleSettings { get; set; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>
    /// The settings recorded against one placement alone, and therefore specific to a single
    /// occurrence of the module on a single page, from <c>dbo.TabModuleSettings</c>. Keys are
    /// bounded at 50 characters and values at 2000.
    /// </summary>
    /// <remarks>
    /// Module-scoped settings are never included; they are carried by <see cref="ModuleSettings"/>.
    /// Empty whenever <see cref="TabModuleId"/> is <see langword="null"/>, because there is then no
    /// placement to read. Values are never <see langword="null"/>, on the same reasoning as
    /// <see cref="ModuleSettings"/>.
    /// </remarks>
    public IReadOnlyDictionary<string, string> TabModuleSettings { get; set; } =
        new Dictionary<string, string>(StringComparer.Ordinal);
}
