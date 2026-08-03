namespace DnnMigration.Application.Dtos.Module;

/// <summary>
/// The two key-value setting stores of one module, carried as the response body of
/// <c>GET /api/v1/modules/{id}/settings</c> and as the request body of
/// <c>PUT /api/v1/modules/{id}/settings</c>. A boundary contract and nothing more: no navigation
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
/// WRITES ARE AN UPSERT PER KEY, NOT A WHOLE-BAG REPLACE, AND THIS CONTRADICTS THE NAIVE READING OF
/// <c>PUT</c>. Submitting this contract adds or updates each key it carries. Omitting a key does NOT delete
/// that key, and submitting a key whose value is the empty string does NOT delete it either - it stores an
/// empty string. Callers that assume replace semantics will silently accumulate settings they believe they
/// removed. Removal is a separate operation, exactly as it was in the legacy layer, where four dedicated
/// members deleted a single module setting, all settings of a module, a single placement setting and all
/// settings of a placement.
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
/// The comparer of a POPULATED instance belongs to whichever layer populated it, and the projection layer
/// deliberately matches names case-insensitively instead - not as a relaxation, but because it is
/// faithful to the STORE: both setting-name columns are declared under a case-insensitive collation and
/// participate in their table's primary key, so two names differing only in case cannot coexist as rows
/// and a case-sensitive projection would draw a distinction the database cannot express. The two
/// positions are therefore both correct for what they describe, and the divergence between the legacy
/// in-memory collection and the store is recorded here rather than absorbed silently. Consumers must not
/// rely on a lookup that differs from a stored name only in case.
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
// MIGRATION: 5.1 - THE DISJOINT SCOPES ARE PRESERVED AS TWO EXPLICITLY NAMED MAPS. The legacy object model
//   flattened the module-to-placement join into one class of fifty-eight properties, so a consumer holding
//   a value could not tell whether it belonged to the module or to one of its placements. Two separately
//   named maps make that recoverable, and the four-entity split of the same join expresses the same fact
//   structurally. Both legacy readers documented the exclusion of the other store, and the legacy screen's
//   own two captions said the same thing to end users, so this is fidelity rather than redesign.
//
// MIGRATION: 5.2 - THE UNTYPED HASH TABLE IS RETIRED. Both legacy readers returned a non-generic hash
//   table, which boxed every key and every value as object and pushed a cast onto each call site. Both are
//   now read-only maps of string to string: the key and value types are stated once, no cast is required,
//   and the reflection-driven row hydrator that filled the legacy collections is gone with it.
//
// MIGRATION: 5.3 - THE LEGACY WRITE MEMBERS DOCUMENTED A REMOVAL BEHAVIOUR THEY DO NOT IMPLEMENT, AND THIS
//   IS THE HIGHEST-CONSEQUENCE FINDING ON THIS CONTRACT. The documentation on the module-setting writer
//   promised "empty SettingValue will remove the setting, if not preserveIfEmpty is true", and the
//   placement-setting writer promised the same (spelling "remove" as "relove"). Neither body does it:
//   each reads the existing row and then updates it if present or inserts it if absent, with no removal
//   branch and no parameter of that name anywhere in either signature. Removal was only ever reachable
//   through the four dedicated delete members. This contract therefore documents the behaviour that was
//   MEASURED, not the behaviour that was DOCUMENTED: an upsert per key. The discrepancy is a legacy defect,
//   annotated here and deliberately NOT fixed, and no removal, replace or merge flag has been invented to
//   paper over it - inventing one would add a capability the legacy contract never had.
//
// MIGRATION: 5.4 - THE LEGACY IN-MEMORY COLLECTION WAS CASE-SENSITIVE, AND THIS TYPE'S OWN DEFAULT SAYS SO
//   EXPLICITLY. Both legacy readers constructed a plain hash table with NO comparer argument, which for
//   string keys compares ordinally and case-sensitively; neither used a case-insensitive variant, and this
//   was verified by execution rather than assumed. Both initialisers below therefore name
//   StringComparer.Ordinal explicitly, so the guarantee is stated rather than inherited from whatever a
//   dictionary's default happens to be. The wider picture is deliberately recorded because the two layers
//   answer to different authorities: the STORE bounds what can exist, and its setting-name columns are
//   case-insensitively collated primary-key members, so two names differing only in case cannot be two
//   rows. The projection layer matches names case-insensitively for that reason, which means a populated
//   instance can be more permissive on lookup than this type's own default. That is a divergence between
//   the legacy in-memory semantics and the storage semantics, not a defect in either layer, and it is
//   documented here instead of being resolved by silently changing one of them.
//
// MIGRATION: 5.5 - A STORED SQL NULL SURFACES AS THE EMPTY STRING, NEVER AS NULL. Both legacy readers
//   tested the value column for database null and substituted an empty string when it was, which is the
//   absent-string sentinel of this codebase made explicit at the read site - that sentinel is the empty
//   string, not null. Both value types below are therefore non-nullable string. Emitting null for such a
//   value would be an observable change to the contract, so the mapping layer must not normalise an empty
//   value to null nor drop the key that carries it.
//
// MIGRATION: 5.6 - THE PLACEMENT IDENTIFIER IS THE ONLY NULLABLE MEMBER, AND -1 IS NEVER NULL. Null means
//   no placement was addressed, so the placement-scoped map is empty while the module-scoped map remains
//   meaningful on its own. It does NOT mean the sentinel value. The legacy absent-integer sentinel is -1
//   and the legacy predicate reports -1 as absent, but -1 must never be read as absence here: identity
//   seeds in this schema are deliberately low and even negative, so small and negative integers are real
//   keys. The two identifiers below do not even share a seed - the module key seeds at 0 and the placement
//   key seeds at 1 - so no single "first value" test is valid for both.
//
// MIGRATION: 5.7 - THE VALUE LENGTH BOUNDS ARE SYMMETRIC AT THE TERMINAL SCHEMA, AND THE 256 THAT LOOKS
//   LIKE AN ASYMMETRY IS A SUPERSEDED BASELINE. Both stores bound a key at fifty characters and both bound
//   a value at 2000. The module-scoped column was created at 256 by the baseline script
//   (01.00.00.SqlDataProvider line 353), but the upgrade chain rebuilt that table part way through:
//   01.00.08.SqlDataProvider line 6252 creates Tmp_ModuleSettings with SettingValue nvarchar(2000), line
//   6261 copies the rows across, line 6264 drops the original and line 6267 renames the temporary table
//   over it. No later script narrows the column again - every subsequent nvarchar(256) in the chain is a
//   stored-procedure PARAMETER, not a column - so 2000 is the terminal width. The placement-scoped table
//   is created at 2000 outright (03.00.01.SqlDataProvider line 726). Reading only the baseline CREATE
//   would therefore invent an eightfold asymmetry that the terminal schema does not have, and would make
//   this contract contradict ModuleSettingConfiguration, which enforces 2000. The bounds are recorded on
//   the members below as documentation only; no length attribute is declared, because declarative
//   validation belongs with the request contracts and persistence limits are enforced by the entity
//   configurations.
//
// MIGRATION: 5.8 - CACHING IS NOT PART OF THIS CONTRACT. Both legacy readers cached their hash table for a
//   period computed as a fixed multiple of a global performance setting, and both invalidated by removing a
//   single coarse key on every write. That global settings accessor is not ported; the multiplier becomes
//   bound configuration and caching moves behind an injected cache service with the legacy key names kept
//   as constants and invalidation made explicit. No expiry, timestamp or cache-key member appears here,
//   because when a value was cached is a service concern and never a property of the value.
//
// MIGRATION: 5.9 - THE GENERIC SHAPE IS DELIBERATE AND IS THE EXCEPTION RATHER THAN THE RULE. Elsewhere in
//   this migration an untyped bag is replaced by a typed projection, so the reasoning is recorded here to
//   prevent that transformation being applied to this contract by analogy. It does not apply because these
//   two stores are genuine key-value tables with open key vocabularies, whereas the portal configuration
//   that superficially resembles them has no table at all. A sibling contract elsewhere also projects from
//   the module-scoped store, but it does so as a typed projection of a small KNOWN key vocabulary
//   belonging to one specific module instance; the two are not variants of each other and must not be
//   merged.
//
// MIGRATION: 5.10 - DICTIONARY KEYS ARE DATA AND MUST SURVIVE SERIALISATION UNCHANGED. The legacy layer
//   never transformed a setting name: the reader used the stored name as the hash-table key verbatim. The
//   API edge applies one central property-naming policy to produce the wire casing of the four members
//   below, and that policy must be confined to property names. Applied to dictionary keys it would rewrite
//   every setting name in both maps, and because the key vocabulary is open there is no fixed list against
//   which such a rewrite could be detected or reversed.
public sealed class ModuleSettingsDto
{
    /// <summary>
    /// The module whose module-scoped settings are carried in <see cref="ModuleSettings"/>, mapped from
    /// <c>Modules.ModuleID</c>.
    /// </summary>
    /// <remarks>
    /// The column is an identity seeded at zero, so <c>0</c> is a legitimate module and must never be read
    /// as an absent or unsaved one. Neither a "less than or equal to zero" test nor a comparison against
    /// the legacy absent-integer sentinel of -1 is a valid emptiness check for this member.
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
    /// representation of absence: the legacy absent-integer sentinel of -1 is a real identifier here and
    /// must never be treated as null. Note also that this column is an identity seeded at one, unlike
    /// <see cref="ModuleId"/> whose identity is seeded at zero, so the two identifiers do not share a
    /// lowest legitimate value.
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
    /// the legacy in-memory collection; because <c>SettingName</c> is a case-insensitively collated member
    /// of the table's primary key, no two stored names can differ only in case, so a lookup must never rely
    /// on such a difference. The value bound of 2000 here matches the 2000 permitted for
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
    /// an ordinal, case-sensitive comparer, matching the legacy in-memory collection; because
    /// <c>SettingName</c> is a case-insensitively collated member of the table's primary key, no two stored
    /// names can differ only in case, so a lookup must never rely on such a difference. Note that the value
    /// bound of 2000 here is the same as the 2000 permitted for <see cref="ModuleSettings"/> once the
    /// upgrade chain has rebuilt that table; the two stores are symmetric at the terminal schema.
    /// </remarks>
    public IReadOnlyDictionary<string, string> TabModuleSettings { get; set; } =
        new Dictionary<string, string>(StringComparer.Ordinal);
}
