namespace DnnMigration.Application.Dtos.Module;

/// <summary>
/// The two key-value setting stores of one module, carried as the response body of
/// <c>GET /api/v1/modules/{moduleId}/settings</c> and as the request body of
/// <c>PUT /api/v1/modules/{moduleId}/settings</c>. A boundary contract and nothing more: no navigation
/// property, no tracked state, no behaviour and no domain entity, in either direction.
/// </summary>
/// <remarks>
/// <para>
/// THE TWO BAGS ARE DISJOINT SCOPES, AND THE LEGACY PRODUCT SAID SO TWICE. This is the single most
/// important fact about this contract, because merging the two maps would look like a simplification and
/// would in fact destroy information. The legacy reader for the module-scoped store carried the remark
/// "TabModuleSettings are not included"; the reader for the placement-scoped store carried the exact
/// converse, "ModuleSettings are not included". Neither store has ever contained the other's rows.
/// </para>
/// <para>
/// THE LEGACY USER INTERFACE DREW THE SAME LINE IN USER-FACING PROSE. The settings screen was split into
/// two captioned sections whose help text survives verbatim in
/// <c>Website/admin/Modules/App_LocalResources/ModuleSettings.ascx.resx</c>. "Module Settings" reads: "In
/// this section, you can define the settings that relate to the Module content and permissions (ie. those
/// settings that will be the same on all pages that the Module appears )." - that is
/// <see cref="ModuleSettings"/>. "Page Settings" reads: "In this section, you can define settings
/// specific to this particular occurrence of the Module for this Page." - that is
/// <see cref="TabModuleSettings"/>. The split is faithful to the original design rather than an invention
/// of this migration.
/// </para>
/// <para>
/// The two maps are also keyed differently, which is the structural reason they cannot merge: module-scoped
/// rows hang off <c>Modules.ModuleID</c> and placement-scoped rows off <c>TabModules.TabModuleID</c>. A
/// module placed on every page has one placement row per page, so a single module identifier addresses one
/// module-scoped bag but many distinct placement-scoped bags.
/// </para>
/// <para>
/// WRITES REPLACE THE WHOLE BAG: A KEY THE REQUEST OMITS IS DELETED. This is the naive reading of
/// <c>PUT</c> and it is the implemented one. <c>ModuleService.UpdateModuleSettingsAsync</c> reads the
/// stored rows for each scope, deletes every stored key the submitted map does not contain, updates the
/// ones whose value differs, inserts the ones that are new, and commits the lot as one unit of work.
/// Two consequences a caller must plan for. A partial submission is destructive: to change one setting,
/// read the current bag, modify it and send it back whole. And an empty map is not a no-op - it clears
/// that scope. Submitting a key whose value is the empty string is NOT a deletion; it stores an empty
/// string, which is a real stored setting. This is a DIVERGENCE from the legacy layer, where writing was
/// per key and removal needed one of four dedicated delete members, and it is recorded as one rather than
/// absorbed: the legacy screen always posted every field it displayed, so a whole-bag write reproduces
/// what that screen actually did while giving a programmatic caller one round trip instead of a
/// difference calculation of its own.
/// </para>
/// <para>
/// KEY ABSENCE IS NOT THE SAME AS AN EMPTY VALUE. Because a stored SQL <c>NULL</c> surfaces as the empty
/// string rather than as <see langword="null"/>, a key that is present with an empty value is a real
/// stored setting and is distinguishable from a key that is absent. Neither map ever contains a
/// <see langword="null"/> value, and empty-valued keys must not be dropped.
/// </para>
/// <para>
/// KEY COMPARISON: THIS TYPE'S OWN DEFAULT IS ORDINAL AND CASE-SENSITIVE, WHICH IS WHAT THE LEGACY
/// IN-MEMORY COLLECTION DID. Both maps below are initialised with an explicit ordinal comparer, so an
/// instance this type constructs for itself draws exactly the distinctions the legacy collection drew.
/// The comparer of a POPULATED instance belongs to whichever layer populated it, and the service
/// reconciles submitted names case-INsensitively. That is the conservative choice rather than a claim
/// about the schema: NO upgrade script assigns a collation to either setting-name column, so comparison
/// and primary-key uniqueness follow the database's own collation, which on a default SQL Server
/// installation is case-insensitive but is a deployment property rather than a guarantee of this model.
/// Reconciling case-insensitively is correct under either collation, whereas relying on case-distinct
/// rows would be wrong under one of them. Consumers must therefore not rely on two keys that differ only
/// in case being two settings, nor on a lookup that differs from a stored name only in case.
/// </para>
/// <para>
/// KEYS ARE DATA, NOT IDENTIFIERS, SO NO SERIALISATION NAMING POLICY MAY TRANSFORM THEM. The wire casing
/// of the four members below is decided by one central serialisation policy at the API edge. That policy
/// must apply to property names only. A naming policy applied to dictionary keys would silently rewrite
/// every setting name in both maps, and a rewritten name is a different setting.
/// </para>
/// <para>
/// THIS CONTRACT IS A GENUINE KEY-VALUE BAG, UNLIKE PORTAL CONFIGURATION, AND MUST NOT BE "CORRECTED" INTO
/// A TYPED PROJECTION. Both maps are backed by real tables - <c>dbo.ModuleSettings</c> and
/// <c>dbo.TabModuleSettings</c> - which the upgrade chain alters repeatedly. Portal configuration has no
/// such table: a case-insensitive search of all eighty-eight upgrade scripts for a <c>PortalSettings</c>
/// table returns nothing, the legacy abstract data surface declares no member for one, the concrete
/// provider invokes no procedure for one, and the legacy type of that name was a per-request ambient
/// composite rather than a persisted aggregate. Portal configuration is therefore projected from columns,
/// whereas the two maps here are the real thing. The key vocabulary is open by design: it was written by
/// whichever module owned the setting, so no stronger typing is available or honest.
/// </para>
/// <para>
/// THESE MAPS REPLACE A DYNAMICALLY LOADED SETTINGS CONTROL. The legacy screen reserved a panel captioned
/// "Module Specific Settings" and filled it at page initialisation by resolving a per-definition settings
/// control and loading it, then called back into that control to save. That control-loading mechanism is
/// not ported, and the per-definition settings it edited are exactly what these two maps now carry. That
/// is why a generic shape is correct here and a typed one would be wrong.
/// </para>
/// <para>
/// NOT CARRIED HERE: no module or placement field of any kind - the module's title, header, footer,
/// activity dates and all-pages flag, and the placement's cache period, icon, visibility, title flag,
/// alignment, colour and border - none appears on this contract. Those belong to the detail and request
/// contracts for the module resource, and duplicating them would reintroduce exactly the ambiguity about
/// which scope a value came from that separating these two maps exists to remove. No permission entry, no
/// paging envelope, no audit field, no cache metadata, no serialisation attribute and no validation
/// attribute appears either: the two settings tables carry no audit columns, settings are not a paged
/// collection, caching is an injected service concern rather than a wire member, and declarative
/// validation lives with the request contracts.
/// </para>
/// </remarks>
// MIGRATION: THE DISJOINT SCOPES ARE PRESERVED AS TWO EXPLICITLY NAMED MAPS. The legacy object model
//   flattened the module-to-placement join into one class of fifty-eight properties, so a consumer holding
//   a value could not tell whether it belonged to the module or to one of its placements. Two separately
//   named maps make that recoverable. Both legacy readers documented the exclusion of the other store, and
//   the legacy screen's two captions said the same thing to end users, so this is fidelity rather than
//   redesign. The untyped hash tables those readers returned are retired with them: both maps are
//   read-only string-to-string, so no call site casts.
//
// MIGRATION: THE WRITE PATH REPLACES EACH SCOPE WHOLE, WHICH IS NEITHER OF THE TWO LEGACY BEHAVIOURS.
//   The legacy documentation on both per-key writers promised that an empty value would remove the
//   setting; neither legacy BODY did it - each updated the row if present and inserted it if absent, so
//   removal was reachable only through four dedicated delete members. The target does a third thing:
//   ModuleService.UpdateModuleSettingsAsync deletes every stored key the submitted map omits, updates the
//   changed ones and inserts the new ones. Omission IS the deletion, which is why the four delete members
//   have no counterpart on this contract; an empty value is still an empty value, so the
//   documented-but-unimplemented legacy behaviour is not resurrected either.
//
// MIGRATION: KEY COMPARISON IS ORDINAL HERE AND COLLATION-DEFINED IN THE STORE, AND THE ASYMMETRY IS
//   DELIBERATE. Both legacy readers built a plain hash table with no comparer, which compares string keys
//   ordinally, so both initialisers below name StringComparer.Ordinal explicitly rather than inheriting a
//   default. No upgrade script assigns a collation to either setting-name column, so store-side equality
//   and primary-key uniqueness follow the database collation - case-insensitive on a default SQL Server
//   installation. The service reconciles submitted names case-insensitively, which is correct under either
//   collation, so a populated instance can be more permissive on lookup than this type's own default. That
//   is documented rather than resolved by silently changing one side.
//
// MIGRATION: A STORED SQL NULL SURFACES AS THE EMPTY STRING, NEVER AS NULL. Both legacy readers
//   substituted an empty string for a null value column, which is this codebase's absent-string sentinel
//   made explicit at the read site. Both value types below are therefore non-nullable string, and the
//   mapping layer must not normalise an empty value to null nor drop the key that carries it.
//
// MIGRATION: THE PLACEMENT IDENTIFIER IS THE ONLY NULLABLE MEMBER, AND -1 IS NEVER NULL. Null means no
//   placement was addressed, so the placement-scoped map is empty while the module-scoped map remains
//   meaningful on its own. The legacy absent-integer sentinel is -1 and the legacy predicate reported -1 as
//   absent; here -1 is applied as an exact lookup value instead, and because TabModules.TabModuleID is
//   seeded at 1 it normally matches nothing, so the answer is "no such placement" rather than "no
//   placement named". Identity seeds in this schema are deliberately low and sometimes negative - the
//   module key seeds at 0 and the placement key at 1 - so no single "first value" test is valid for both,
//   and only null spells absence.
//
// MIGRATION: BOTH VALUE COLUMNS ARE 2000 AT THE TERMINAL SCHEMA. The module-scoped column was created at
//   256 by the baseline script, but 01.00.08.SqlDataProvider rebuilt the table through Tmp_ModuleSettings
//   at nvarchar(2000) and renamed it over the original; no later script narrows it, and every subsequent
//   nvarchar(256) in the chain is a stored-procedure parameter rather than a column. The placement-scoped
//   table is created at 2000 outright. Reading only the baseline CREATE would invent an eightfold
//   asymmetry the terminal schema does not have and would contradict ModuleSettingConfiguration. Keys are
//   bounded at fifty in both stores. The bounds are documentation only: declarative validation belongs
//   with the request contracts and persistence limits with the entity configurations.
//
// MIGRATION: CACHING IS NOT PART OF THIS CONTRACT. Both legacy readers cached their hash table against a
//   global performance multiplier and invalidated by removing one coarse key on every write. Caching moves
//   behind an injected cache service with the legacy key names kept as constants and invalidation made
//   explicit, so no expiry, timestamp or cache-key member appears here: when a value was cached is a
//   service concern and never a property of the value.
//
// MIGRATION: THE GENERIC SHAPE IS THE EXCEPTION RATHER THAN THE RULE, recorded so that the
//   untyped-bag-to-typed-projection transformation applied elsewhere is not applied here by analogy. It
//   does not apply because these two stores are genuine key-value tables with open key vocabularies. A
//   sibling contract also projects from the module-scoped store, but as a typed projection of a small
//   KNOWN key vocabulary belonging to one module instance; the two must not be merged.
//
// MIGRATION: DICTIONARY KEYS ARE DATA AND MUST SURVIVE SERIALISATION UNCHANGED. The legacy reader used
//   the stored name as its hash-table key verbatim. The API edge's central property-naming policy must
//   stay confined to property names: applied to dictionary keys it would rewrite every setting name, and
//   because the key vocabulary is open there is no fixed list against which such a rewrite could be
//   detected or reversed.
public sealed class ModuleSettingsDto
{
    /// <summary>
    /// The module whose module-scoped settings are carried in <see cref="ModuleSettings"/>, mapped from
    /// <c>Modules.ModuleID</c>.
    /// </summary>
    /// <remarks>
    /// The column is an identity seeded at zero, so <c>0</c> is a legitimate module and must never be read
    /// as an absent or unsaved one. Neither a "less than or equal to zero" test nor a comparison against
    /// the legacy absent-integer sentinel of -1 is a valid emptiness check for this member - and -1 is
    /// itself a real key elsewhere in this schema, being the seed of <c>Portals.PortalID</c>, whose shipped
    /// default row additionally carries an explicit 0.
    /// </remarks>
    public int ModuleId { get; set; }

    /// <summary>
    /// The placement whose placement-scoped settings are carried in <see cref="TabModuleSettings"/>, mapped
    /// from <c>TabModules.TabModuleID</c>, or <see langword="null"/> when no placement was addressed.
    /// </summary>
    /// <remarks>
    /// The only nullable member on this contract. <see langword="null"/> means no placement context was
    /// supplied, in which case <see cref="TabModuleSettings"/> is empty and
    /// <see cref="ModuleSettings"/> is still meaningful on its own. <see langword="null"/> is the ONLY
    /// spelling of absence. A submitted -1 is not absence either: it is an exact lookup value, and since
    /// <c>TabModules.TabModuleID</c> is an identity seeded at one it normally matches no placement at
    /// all, so the request is answered as a placement that does not exist rather than as one that was
    /// never named. That seed also differs from <see cref="ModuleId"/>, whose identity is seeded at
    /// zero, so the two identifiers do not share a lowest legitimate value.
    /// </remarks>
    public int? TabModuleId { get; set; }

    /// <summary>
    /// The settings recorded against the module itself and therefore identical on every page the module
    /// appears on, from <c>dbo.ModuleSettings</c>. Keys are bounded at 50 characters and values at 2000 -
    /// the terminal width established when <c>01.00.08.SqlDataProvider</c> line 6256 rebuilt the table
    /// through <c>Tmp_ModuleSettings</c> and renamed it over the original, superseding the 256 of the
    /// baseline column at <c>01.00.00.SqlDataProvider</c> line 353.
    /// </summary>
    /// <remarks>
    /// The legacy screen described this scope as: "In this section, you can define the settings that relate
    /// to the Module content and permissions (ie. those settings that will be the same on all pages that
    /// the Module appears )." Despite that wording no permission entry is carried here - the reference to
    /// permissions described how the legacy screen grouped its controls, not the contents of this store.
    /// Placement-scoped settings are never included; they are carried by
    /// <see cref="TabModuleSettings"/>. Values are never <see langword="null"/>: a stored SQL <c>NULL</c>
    /// surfaces as the empty string, so a key present with an empty value is a real setting and differs
    /// from a key that is absent. This map is initialised with an ordinal, case-sensitive comparer, matching
    /// the legacy in-memory collection; because no upgrade script assigns a collation to
    /// <c>SettingName</c>, whether two stored names differing only in case can coexist follows the
    /// database collation - case-insensitively on a default installation - so a lookup must never rely on
    /// such a difference in either direction. The value bound of 2000 here matches the 2000 permitted for
    /// <see cref="TabModuleSettings"/>; the 256 of the baseline column was superseded when the upgrade
    /// chain rebuilt this table, so the two stores are symmetric at the terminal schema.
    /// </remarks>
    public IReadOnlyDictionary<string, string> ModuleSettings { get; set; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>
    /// The settings recorded against one placement alone, and therefore specific to a single occurrence of
    /// the module on a single page, from <c>dbo.TabModuleSettings</c>. Keys are bounded at 50 characters and
    /// values at 2000.
    /// </summary>
    /// <remarks>
    /// The legacy screen described this scope as: "In this section, you can define settings specific to this
    /// particular occurrence of the Module for this Page." Module-scoped settings are never included; they
    /// are carried by <see cref="ModuleSettings"/>. Empty whenever <see cref="TabModuleId"/> is
    /// <see langword="null"/>, because there is then no placement to read. Values are never
    /// <see langword="null"/>: a stored SQL <c>NULL</c> surfaces as the empty string, so a key present with
    /// an empty value is a real setting and differs from a key that is absent. This map is initialised with
    /// an ordinal, case-sensitive comparer, matching the legacy in-memory collection; because no upgrade
    /// script assigns a collation to <c>SettingName</c>, whether two stored names differing only in case
    /// can coexist follows the database collation, so a lookup must never rely on such a difference in
    /// either direction. Note that the value
    /// bound of 2000 here is the same as the 2000 permitted for <see cref="ModuleSettings"/> once the
    /// upgrade chain has rebuilt that table; the two stores are symmetric at the terminal schema.
    /// </remarks>
    public IReadOnlyDictionary<string, string> TabModuleSettings { get; set; } =
        new Dictionary<string, string>(StringComparer.Ordinal);
}
