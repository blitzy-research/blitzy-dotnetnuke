using DnnMigration.Domain.Enums;

namespace DnnMigration.Application.Dtos.Module;

// MIGRATION: §5.9 - this contract has NO direct legacy grid precedent, and that is stated plainly
// here rather than quietly assumed. The transformation mapping for the module-list screen names its
// sources as "Library/Components/Modules/ModuleController.vb (there is no legacy module-list admin
// page in scope)" plus the settings-screen markup "for field conventions", and describes the result
// as a "New list screen over an existing query surface." Measured confirmation: the whole of
// Website/admin/Modules/ holds three screens only - ModuleSettings, Export and Import - and no list
// page of any kind, in contrast to the portal, user and role features, each of which does descend
// from a real legacy data grid. The column set below is therefore synthesised from exactly two
// inputs: (a) the field conventions of Website/admin/Modules/modulesettings.ascx, and (b) what the
// pre-existing query surface can actually return - GetModules (ModuleController.vb lines 915 and
// 928), GetAllModules (871), GetAllTabsModules (941), GetModulesByDefinition (1020), GetTabModules
// (1044), GetModuleTabs (1223) and GetPortalTabModules (1423), every one of which yields the same
// flattened legacy object. Shapes in this layer derive from what the legacy screens posted and
// rendered rather than from the entity graph, so this projection is a deliberate design decision
// and not an accidental loss of fidelity.
//
// MIGRATION: §5.2 - the four-entity split, and Modules.TabID was DROPPED from the schema. The
// legacy Library/Components/Modules/ModuleInfo.vb is one 936-line class exposing 58 properties
// (measured) that flattens the Modules-to-TabModules-to-ModuleDefinitions-to-ModuleControls join
// into a single object; the target splits that join along the real table boundaries. Only the
// cumulative terminal state of the 88-script upgrade chain is meaningful, and deriving from the
// baseline alone would be actively wrong here: the 01.00.00 baseline Modules table (line 220)
// declares none of AllTabs, IsDeleted, StartDate, EndDate, Visibility or DisplayTitle. Measured
// terminal ownership of the thirteen members below, traced case-insensitively across all four
// object-naming forms - bare, dbo-qualified, bracketed and qualifier-templated:
//
//     dbo.Modules            (one row per module)     ModuleId, ModuleDefId, ModuleTitle,
//                                                     AllTabs, IsDeleted, StartDate, EndDate
//     dbo.TabModules         (one row per placement)  TabModuleId, TabId, ModuleOrder,
//                                                     Visibility, DisplayTitle
//     dbo.ModuleDefinitions  (read-only projection)   FriendlyName
//     dbo.ModuleControls     (nothing)                -
//
// The evidence that TabId, ModuleOrder and Visibility are per-placement facts rather than
// per-module facts is explicit and destructive. Script 03.00.01 line 11 adds PortalID to Modules;
// lines 13-14 then drop the foreign key that had tied Modules to Tabs; lines 197-198 drop ten
// columns from Modules in one statement - ModuleOrder, PaneName, CacheTime, Alignment, Color,
// Border, IconFile, Personalize, ShowTitle and ContainerSrc - and lines 204-205 drop TabID itself.
// The replacement table is created at line 19 of the same script and carries TabModuleID as
// int NOT NULL IDENTITY (1, 1), TabID, ModuleID, PaneName, ModuleOrder and, at line 31,
// Visibility int NOT NULL; a census of the whole chain finds that column declared on TabModules and
// nowhere else. DisplayTitle joins it later, at 03.00.08 line 156. The remaining Modules-side
// members arrive by ALTER as well: AllTabs at 01.00.04 line 85 with a default of 0, IsDeleted at
// 02.00.00 line 6568 with a default of 0, and Header, Footer, StartDate and EndDate together at
// 02.02.00 lines 322-325. A consumer that assumes a single physical module row behind this
// contract will mis-model the AllTabs case, where one module legitimately has many placements.
//
// MIGRATION: §5.5 - PortalId is deliberately absent. The listing endpoint is portal-scoped by the
// per-request portal context that the alias-resolution middleware populates once per request, so
// every row in a single response belongs to the same portal and repeating the identifier on each
// row would be pure noise. This mirrors the sibling page-list contract, where the same identifier
// is omitted for the same reason. Two measured facts make the omission safer than carrying it:
// Modules.PortalID is genuinely nullable (03.00.01 line 11 adds it as int NULL), and
// Portals.PortalID is IDENTITY (-1, 1) (01.00.00 line 77), so -1 is a real, addressable portal
// identifier rather than a spare value. Nothing in this folder may therefore use -1 to signal
// absence, on any member.
//
// MIGRATION: §5.10 - both legacy row-hydration paths are deleted rather than translated. The
// legacy reader ModuleController.FillModuleInfo (line 53) is a hand-rolled block spanning lines
// 66-124 that assigns one property per line through Convert.ToXxx(Null.SetNull(dr("Column"),
// currentValue)), and its two wrapper helpers - FillModuleInfoCollection (line 164) returning the
// untyped pre-generic sequence type of that era, and FillModuleInfoDictionary (line 190) returning
// a dictionary keyed by module identifier - wrap it. The framework's separate reflection-driven
// hydrator, 729 lines with 21 in-scope call sites, is likewise not carried forward. All of it is
// replaced by the object-relational materialiser, so no Fill method, no FillObject method and no
// hydration flag appears anywhere in the target, and none may be added to this type.
//
// MIGRATION: §5.8 - exclusion summary for this contract. Each omission below is a measured
// decision, not an oversight, and several omit columns that genuinely exist in the terminal schema.
//   - The token-replacement accessor contract IPropertyAccess, which the legacy class implemented
//     (ModuleInfo.vb line 37), is dropped with the token-replacement subsystem; its Cacheability
//     member (line 925) is an HTTP cache-policy artefact of the retired web framework and caching
//     is now an injected cache service over the in-memory cache with a bound performance
//     multiplier.
//   - XML serialisation attributes are dropped. The legacy class was decorated for XML
//     round-tripping (line 36) and every property carried a matching element attribute; the wire
//     shape now belongs to the DTO layer and to one central JSON naming policy configured at the
//     API edge, so no serialisation, data-contract or property-naming attribute appears here.
//   - Pane-layout and rendering members are omitted: PaneName, Alignment, Color, Border,
//     DisplayPrint, DisplaySyndicate, PaneModuleIndex, PaneModuleCount and
//     SupportsPartialRendering. The first six are real terminal TabModules columns, so these are
//     genuine columns being excluded; they exist to drive server-side markup generation, and both
//     the postback infrastructure and server-side rendering are excluded from this migration.
//   - Container and skinning members are omitted: ContainerSrc (a real TabModules column, measured
//     nvarchar(200) NULL at 03.00.01 line 32) and ContainerPath. Skinning and containers are
//     excluded wholesale.
//   - BusinessControllerClass (line 428) is omitted. The legacy code passed that stored type name
//     to the framework's reflection-based activator at five call sites; the target resolves module
//     behaviour from a closed, dependency-injected set through a factory instead. Publishing a type
//     name on the wire would invite arbitrary activation, so it is omitted here and must never be
//     accepted on a request either.
//   - Permission members are relocated, not represented: ModulePermissions (line 356, a
//     pre-generic permission collection), AuthorizedRoles, AuthorizedEditRoles and
//     AuthorizedViewRoles. The last two were dropped from the Modules table in DotNetNuke 3.0 -
//     03.00.01 lines 1401-1405 drop both, which is exactly why the legacy reader re-read them
//     inside a swallowing Try/Catch whose own comment says the field "was removed from the Modules
//     table in 3.0" - and the role strings were semicolon-delimited derived values with ";" itself
//     meaning "no roles". Permission evaluation now belongs to the infrastructure security layer
//     and to a separate read-only permissions catalogue. InheritViewPermissions (line 347) is
//     omitted for the same reason.
//   - IsDefaultModule and AllModules (lines 590 and 599) are omitted because they are not module
//     state at all: the legacy update path used the first to write two site-settings keys and the
//     second to propagate appearance across every non-administrative page. They are intent flags
//     and belong on the update request contract.
//   - CacheTime (line 203) and IconFile (line 239) are settings-screen concerns, and Header and
//     Footer (lines 275 and 284) are free multi-line markup blocks that the legacy screen rendered
//     into six-row text areas. None is a grid column; all four are deferred to the detail and
//     request contracts.
//   - Definition, desktop-module and control metadata is omitted, together with the capability
//     projections IsPortable, IsSearchable and IsUpgradeable and the SupportedFeatures bit field
//     behind them. Definition metadata is reachable through the module-definition lookup contract,
//     and search is deferred entirely.
//   - No audit member appears. A census of all 88 upgrade scripts found zero occurrences of any of
//     the four DotNetNuke audit column names, and neither Modules nor TabModules declares one.
//   - No page shape appears - no nested tab object and no TabName. Pages are consumed as a lookup
//     by the module screens, which is precisely what the legacy markup did: modulesettings.ascx
//     line 192 binds its page picker with datatextfield="TabName" and datavaluefield="TabId". That
//     lookup contract already exists as the sibling page-list contract and must not be restated
//     here.

/// <summary>
/// One row of the module listing returned by <c>GET /api/v1/modules</c>: a single module instance as
/// it is placed on a page, reduced to the facts an administration grid displays. The application
/// service wraps a collection of these in <c>PagedResponse&lt;ModuleListItemDto&gt;</c>, and this
/// type deliberately carries no paging metadata of its own - it is one row and nothing more. It is a
/// boundary contract, never an entity: it has no navigation property, no tracked state and no
/// behaviour, and no domain entity is exposed through it in either direction.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Composed from four legacy tables.</strong> The legacy object this replaces flattened a
/// four-table join into one class; the members below are drawn from three of those tables and the
/// class-level notes above give the measured column-by-column provenance. In short:
/// <see cref="ModuleId"/>, <see cref="ModuleDefId"/>, <see cref="ModuleTitle"/>,
/// <see cref="AllTabs"/>, <see cref="IsDeleted"/>, <see cref="StartDate"/> and
/// <see cref="EndDate"/> describe the module itself and come from <c>dbo.Modules</c>;
/// <see cref="TabModuleId"/>, <see cref="TabId"/>, <see cref="ModuleOrder"/>,
/// <see cref="Visibility"/> and <see cref="DisplayTitle"/> describe one placement of that module on
/// one page and come from <c>dbo.TabModules</c>; and <see cref="FriendlyName"/> is a read-only
/// projection from <c>dbo.ModuleDefinitions</c>. The practical consequence is that a module with
/// <see cref="AllTabs"/> set appears as several rows - one per placement, each with its own
/// <see cref="TabModuleId"/>, <see cref="TabId"/> and <see cref="ModuleOrder"/> - while sharing a
/// single <see cref="ModuleId"/>. Callers must key row identity on <see cref="TabModuleId"/>, not
/// on <see cref="ModuleId"/>.
/// </para>
/// <para>
/// <strong>No direct legacy grid precedent.</strong> Unlike the portal, user and role listings,
/// this shape was not lifted from an existing legacy data grid, because DotNetNuke 4.9 had no
/// module-list administration page within the scope of this migration. It is a new listing over a
/// query surface that already existed, synthesised from the settings screen's field conventions and
/// from what those queries can return. The class-level notes above present the reasoning and the
/// measured evidence in full.
/// </para>
/// <para>
/// <strong>Sentinel values are preserved, not silently normalised.</strong> The legacy data layer
/// encoded absence in-band: -1 for integers, the empty string for strings and
/// <see cref="System.DateTime.MinValue"/> for dates. Several of those encodings collide with real
/// data in this schema - <see cref="ModuleId"/> and <see cref="TabId"/> are seeded from 0,
/// <see cref="TabModuleId"/> and <see cref="ModuleDefId"/> from 1, and -1 is simultaneously a
/// genuine <c>ModuleOrder</c> instruction, a genuine portal identifier and a page wildcard in the
/// legacy query surface. Each member below documents which of its boundary values carry meaning.
/// Two rules follow for every consumer: never test an identifier on this type against 0 or against
/// -1 to decide whether it is present, and never assume a nullable member's underlying legacy
/// representation was itself null.
/// </para>
/// <para>
/// <strong>Portal scope and exclusions.</strong> The portal identifier is deliberately omitted
/// because the request is already portal-scoped by the per-request portal context. The wider
/// exclusion list - the retired accessor contract and its cache-policy member, XML serialisation
/// attributes, pane-layout and rendering members, container and skinning members, the business
/// controller type name, permission collections, the two intent flags, the deferred settings
/// members, definition and control metadata, audit members and any nested page shape - is set out
/// with its measured evidence in the class-level notes above. This type also carries no validation
/// attribute: declarative validation lives in the application validation folder, and no validator
/// exists for a list row, because a list row is never submitted.
/// </para>
/// </remarks>
public sealed class ModuleListItemDto
{
    /// <summary>
    /// The identity of the module itself, mapped from <c>Modules.ModuleID</c>.
    /// </summary>
    /// <remarks>
    /// The column is <c>int IDENTITY (0, 1) NOT NULL</c> and is the table's primary key (01.00.00
    /// baseline, line 221). <strong>The seed is 0, so 0 is a legitimate module identifier</strong>
    /// and the very first module ever created in a database carries it. A test of the form
    /// "identifier is less than or equal to zero" therefore rejects a real row, and a test against
    /// -1 is unsafe for a different reason: -1 was the legacy in-band marker for an absent integer,
    /// yet it is also a genuine portal identifier in this schema. Neither idiom may be used as an
    /// absence check on this member. Where a module is genuinely absent, that is expressed by the
    /// absence of the row, never by a boundary value inside it. Note also that this value is
    /// <em>not</em> unique across a listing: a module displayed on every page contributes one row
    /// per placement, all sharing this identifier. Use <see cref="TabModuleId"/> for row identity.
    /// </remarks>
    public int ModuleId { get; set; }

    /// <summary>
    /// The identity of <em>this placement of the module on a page</em>, mapped from
    /// <c>TabModules.TabModuleID</c>. This is the stable key for a row of the listing.
    /// </summary>
    /// <remarks>
    /// The column is declared <c>int NOT NULL IDENTITY (1, 1)</c> and is the primary key of the
    /// placement table created at 03.00.01 line 19, with the column itself at line 21.
    /// <strong>Its seed is 1, a different seed from <see cref="ModuleId"/> and
    /// <see cref="TabId"/>, both of which start at 0.</strong> Three different seeds across the
    /// four identifiers on this type is precisely why no single "looks empty" numeric test is valid
    /// for any of them. This identifier is also the key under which placement-scoped settings are
    /// stored, so it is the value a caller carries forward when opening the settings screen for one
    /// specific placement rather than for the module as a whole.
    /// </remarks>
    public int TabModuleId { get; set; }

    /// <summary>
    /// The page this placement belongs to, mapped from <c>TabModules.TabID</c>. The legacy user
    /// interface called this a page, not a tab: the settings screen labelled its picker
    /// "Move To Page:", described as "Move this module instance to another Page."
    /// </summary>
    /// <remarks>
    /// <para>
    /// In the terminal schema this column lives on the placement table, <em>not</em> on the module
    /// table - <c>Modules.TabID</c> was dropped outright by 03.00.01 lines 204-205, together with
    /// the foreign key that had tied the two tables (lines 13-14 of the same script). The
    /// class-level notes above give the full trace. The underlying <c>Tabs.TabID</c> column is
    /// <c>int IDENTITY (0, 1) NOT NULL</c> (01.00.00 line 140), so <strong>0 is a legitimate page
    /// identifier</strong> and must not be read as "no page".
    /// </para>
    /// <para>
    /// A sharper trap surrounds -1. In the legacy query surface, -1 supplied as a page identifier
    /// was an <strong>"any page" wildcard rather than an absent value</strong>: ModuleController.vb
    /// lines 1223-1225 implement <c>GetModuleTabs(ModuleID)</c> as a call to the provider's
    /// per-module read with the legacy absent-integer constant in the page position, precisely so
    /// that it returns every placement of the module across all pages. A value of this member is
    /// always a concrete page, because a row of this listing is always a real placement.
    /// </para>
    /// </remarks>
    public int TabId { get; set; }

    /// <summary>
    /// The module definition this instance was created from, mapped from
    /// <c>Modules.ModuleDefID</c>.
    /// </summary>
    /// <remarks>
    /// The referenced key <c>ModuleDefinitions.ModuleDefID</c> is <c>int IDENTITY (1, 1) NOT NULL</c>
    /// (01.00.00 line 66), so the seed is 1 and no boundary value on this member carries a second
    /// meaning. Only the identifier travels on a list row; a caller needing the definition's display
    /// metadata - its cache default, its owning desktop module, its portability - resolves it from
    /// <c>GET /api/v1/module-definitions</c>, whose contract is the sibling
    /// <see cref="ModuleDefinitionDto"/>. <see cref="FriendlyName"/> below is the single definition
    /// fact carried inline, because a grid that showed only a numeric definition identifier would be
    /// unreadable.
    /// </remarks>
    public int ModuleDefId { get; set; }

    /// <summary>
    /// The administrator-supplied title of this module instance, mapped from
    /// <c>Modules.ModuleTitle</c>. The legacy settings screen labelled it "Title:" and described it
    /// as "Enter a title for the Module.  This will appear in the Title Bar of the Container for
    /// this Module, if supported by the Container."
    /// </summary>
    /// <remarks>
    /// <para>
    /// The column is measured <c>nvarchar(256) NULL</c> (01.00.00 line 226), so the effective
    /// maximum length is 256 characters. That bound comes from the schema alone: the legacy input
    /// (modulesettings.ascx line 32) carries no length attribute whatsoever. The maximum is
    /// documented here and nowhere enforced on this type - constraint enforcement belongs to the
    /// validation layer and to the database.
    /// </para>
    /// <para>
    /// <strong>Genuinely optional.</strong> The legacy settings screen declares no presence
    /// validator on this field - a census of modulesettings.ascx finds zero of them anywhere in the
    /// file, and only four comparison validators, none of which touches the title - so a module with
    /// no title is a valid legacy state rather than corrupt data. Nullable here for that reason.
    /// </para>
    /// <para>
    /// <strong>The legacy representation of "no title" was the empty string, not null.</strong> The
    /// legacy absent-string constant is defined as a literal empty string, and the legacy reader
    /// applied it to this column on every read, so a database null and an empty string were
    /// indistinguishable once loaded. This contract uses <c>null</c> for absence, which makes the
    /// two distinguishable again; translating between the two representations is owned explicitly by
    /// <c>Application/Mapping/ModuleMappings.cs</c> and must not be left to serialisation, because a
    /// silent conversion in either direction is an observable change of behaviour for any caller
    /// that distinguishes an empty title from a missing one.
    /// </para>
    /// </remarks>
    public string? ModuleTitle { get; set; }

    /// <summary>
    /// The display name of the module's definition - what the legacy settings screen showed for
    /// "Module:", described there as "Displays the name of the module." Read-only.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Projected from <c>ModuleDefinitions.FriendlyName</c>, measured <c>nvarchar(128)</c>, so the
    /// effective maximum length is 128 characters. It is carried inline purely so a grid can name
    /// each row's module type without a second request, and it is <strong>never writable</strong>:
    /// it belongs to the definition, not to this instance, and no create or update contract in this
    /// migration accepts it. The legacy screen made the same statement structurally, rendering it
    /// into a text box explicitly marked <c>Enabled="False"</c> (modulesettings.ascx line 28) and
    /// assigning it without ever reading it back (ModuleSettings.ascx.vb line 125).
    /// </para>
    /// <para>
    /// Nullable, and for a reason worth stating: although the underlying definition column is not
    /// itself optional, this member is the result of a join, so it is absent whenever the joined
    /// definition row cannot be resolved. As with <see cref="ModuleTitle"/>, the legacy layer
    /// surfaced that case as the empty string rather than as null, and the same explicit mapper owns
    /// the translation.
    /// </para>
    /// </remarks>
    public string? FriendlyName { get; set; }

    /// <summary>
    /// The module's position within its pane on the page, mapped from
    /// <c>TabModules.ModuleOrder</c>. Lower values render nearer the top of the pane.
    /// </summary>
    /// <remarks>
    /// A per-placement fact: the column belongs to the placement table (03.00.01 line 25, declared
    /// <c>int NOT NULL</c>) after being dropped from the module table by lines 197-198 of the same
    /// script, so two placements of one module can legitimately sit at different positions on
    /// different pages. <strong>The value -1 is a live instruction, not an absent value</strong> -
    /// see the note immediately below - which is why this member is a non-nullable integer and why
    /// no mapper may translate -1 to null.
    /// </remarks>
    // MIGRATION: §5.1 - ModuleOrder of -1 is a COMMAND meaning "append at the bottom of the pane",
    // and it must survive verbatim. Three independent pieces of evidence in
    // Library/Components/Modules/ModuleController.vb establish it. First, the documented contract of
    // UpdateModuleOrder at line 1155:
    //     ''' <param name="ModuleOrder">position within the controls list on page, -1 if to be added
    //     at the end</param>
    // Second, the branch that consumes it, at lines 667-669:
    //     If objModule.ModuleOrder = -1 Then
    //         ' position module at bottom of pane
    // Third, the same branch inside UpdateModuleOrder itself at lines 1163-1164, prefaced by the
    // comment "adding a module to a new pane - places the module at the bottom of the pane", which
    // then reads the existing maximum order for the pane and appends past it. The portal-template
    // import path assigns it directly at line 296 for exactly this purpose. The hazard is that the
    // legacy absent-integer constant is ALSO -1, so a mapping that mechanically converted the legacy
    // sentinel to null on this member would silently destroy the append instruction and leave new
    // modules at an arbitrary position. The member therefore stays a plain integer and -1 is passed
    // through unchanged in both directions.
    public int ModuleOrder { get; set; }

    /// <summary>
    /// Whether this module is displayed in the same location on every page of the portal, mapped
    /// from <c>Modules.AllTabs</c>. The legacy settings screen asked "Display Module On All Pages?",
    /// described as "Select whether the module should appear in the same location on all pages of
    /// the site".
    /// </summary>
    /// <remarks>
    /// A per-module fact, added to the module table by 01.00.04 line 85 as
    /// <c>bit NOT NULL CONSTRAINT DF_Modules_AllTabs DEFAULT 0</c>, so it is never absent and the
    /// default is <c>false</c>. Non-nullable accordingly, and consistent with the legacy constructor,
    /// which left its backing field at the type default rather than seeding it with a sentinel.
    /// <strong>This flag is what makes <see cref="TabModuleId"/> rather than
    /// <see cref="ModuleId"/> the row key:</strong> when it is set, the module has a placement row
    /// for every page and therefore contributes multiple rows to one listing.
    /// </remarks>
    public bool AllTabs { get; set; }

    /// <summary>
    /// How this placement is presented on its page, mapped from <c>TabModules.Visibility</c>. The
    /// legacy settings screen labelled it "Visibility:", described as "Choose the default visibility
    /// for this Module".
    /// </summary>
    /// <remarks>
    /// <para>
    /// A per-placement fact: the column is declared <c>int NOT NULL</c> at 03.00.01 line 31, and a
    /// census of the whole upgrade chain finds it declared on the placement table and nowhere else.
    /// Non-nullable here because the column is, with
    /// <see cref="ModuleVisibility.Maximized"/> - ordinal 0 - as the default, matching both the
    /// column and the legacy constructor. The three ordinals persisted in that column are exactly
    /// the three the legacy radio button list offered (modulesettings.ascx lines 145-147, values 0,
    /// 1 and 2).
    /// </para>
    /// <para>
    /// <strong><see cref="ModuleVisibility.None"/> means "the module is not rendered". It does not
    /// mean the value is missing or has yet to be chosen.</strong> This is the single most likely
    /// misreading of the enumeration: it is a real, deliberate display state that an administrator
    /// selected, and a consumer that treats it as "unset" and substitutes a default will make a
    /// suppressed module visible. The type offers no "unknown" or "not set" member and none may be
    /// added; a genuinely absent visibility would be expressed by a nullable property, and this one
    /// is not nullable.
    /// </para>
    /// </remarks>
    // MIGRATION: §5.3 - the legacy reader collapsed three distinct stored inputs into one output.
    // Library/Components/Modules/ModuleController.FillModuleInfo, lines 81-85, reads:
    //     Select Case Convert.ToInt32(Null.SetNull(dr("Visibility"), intVisibility))
    //         Case 0, Null.NullInteger : objModuleInfo.Visibility = VisibilityState.Maximized
    //         Case 1 : objModuleInfo.Visibility = VisibilityState.Minimized
    //         Case 2 : objModuleInfo.Visibility = VisibilityState.None
    //     End Select
    // A database null, a stored 0 and a stored -1 therefore all surfaced as Maximized. The target
    // preserves that collapse rather than inventing a fourth state for it, which is the whole reason
    // this member is not nullable: there was never an observable difference between the three inputs
    // at the contract boundary, so introducing one now would change behaviour.
    //
    // MIGRATION: legacy defect, annotated and deliberately NOT repaired. The branch above has no
    // final catch-all case, and neither does its mirror image on the write path
    // (Website/admin/Modules/ModuleSettings.ascx.vb lines 357-363). A stored value outside the set
    // {-1, 0, 1, 2} consequently falls through every branch and leaves the field at its type default
    // of 0, silently presenting an out-of-range row as Maximized instead of failing. The behaviour is
    // preserved exactly as measured, per the standing instruction that a discovered legacy defect is
    // annotated in place and not fixed unless it blocks delivery. The identical omission exists in
    // the adjacent control-type branch at lines 113-121 of the same reader. A related legacy
    // looseness is worth noting for whoever writes the mapper: the read path assigned this
    // enumeration straight into an integer-typed selected-index property (ModuleSettings.ascx.vb
    // line 134), an implicit conversion that only compiled because the administration pages were
    // built with strict type checking disabled. Every such coercion must be made explicit in the
    // target, and any difference in outcome documented.
    public ModuleVisibility Visibility { get; set; }

    /// <summary>
    /// Whether this module is in the soft-deleted state that the legacy recycle bin represented,
    /// mapped from <c>Modules.IsDeleted</c>. A row with this flag set still exists in the database
    /// and can be restored; it is not a tombstone.
    /// </summary>
    /// <remarks>
    /// A per-module fact, added by 02.00.00 line 6568 as <c>bit NOT NULL</c> with a default of 0, so
    /// it is never absent and non-nullable accordingly. It is carried on the list row because a grid
    /// that silently mixed deleted and live modules, or that hid deleted ones with no way to see
    /// them, would lose a workflow the legacy application supported.
    /// </remarks>
    // MIGRATION: §5.6 - the legacy settings screen could only ever CLEAR this flag, never set it.
    // Website/admin/Modules/ModuleSettings.ascx.vb line 364 contains the bare, unconditional
    // assignment
    //     objModule.IsDeleted = False
    // in the middle of its update handler, so saving a module's settings always un-deleted it as a
    // side effect, whatever its stored state had been, and whether or not the administrator intended
    // it. This list row reports the stored state faithfully in both directions and does not
    // reproduce that side effect; restoring a module is a deliberate operation in the target rather
    // than a by-product of saving unrelated settings. The divergence is deliberate and is annotated
    // here because the legacy behaviour is externally observable.
    public bool IsDeleted { get; set; }

    /// <summary>
    /// Whether the module's <em>container</em> chrome is displayed around this placement, mapped
    /// from <c>TabModules.DisplayTitle</c>. Despite the column name, the authoritative legacy
    /// wording is "Display Container?", described as "Select this option if you would like to display
    /// the Module container."
    /// </summary>
    /// <remarks>
    /// <para>
    /// A per-placement fact, added by 03.00.08 line 156 as <c>bit NOT NULL</c> with a default of
    /// <c>(1)</c>, so it is never absent, and <strong>the stored default is therefore
    /// <c>true</c></strong> - which the legacy constructor independently confirms by seeding its
    /// backing field to true, one of only three fields it gave a real non-sentinel default.
    /// Non-nullable accordingly.
    /// </para>
    /// <para>
    /// <strong>Note the asymmetry:</strong> that <c>true</c> is the default of the <em>column</em>
    /// and of the legacy object, not of this contract. This member is a plain automatic property, so
    /// an instance that nobody has populated reports <c>false</c> - the default of the underlying CLR
    /// type. No initialiser is added here, because the house style across this folder is plain
    /// mutable automatic properties that the mappers and the JSON serialiser construct
    /// parameterlessly, and a member initialiser would make a default-constructed instance
    /// indistinguishable from one deliberately set to <c>true</c>. Populating this member correctly
    /// is consequently the mapper's responsibility, and <c>Application/Mapping/ModuleMappings.cs</c>
    /// must assign it from the stored value on every projection rather than relying on a default.
    /// </para>
    /// <para>
    /// The member name is retained unchanged because it is the schema's own, and renaming it would
    /// break the correspondence that makes the migration traceable; the true meaning is documented
    /// instead.
    /// </para>
    /// </remarks>
    // MIGRATION: §5.7 - the member name and its meaning disagree, and the disagreement is inherited
    // rather than introduced. The legacy markup labels the checkbox
    //     <dnn:label id="plDisplayTitle" text="Display Title?" ... controlname="chkDisplayTitle">
    // at modulesettings.ascx line 152, but that inline text is only a fallback: the localisation
    // resource file overrides it, and Website/admin/Modules/App_LocalResources/
    // ModuleSettings.ascx.resx sets plDisplayTitle.Text to "Display Container?" with the help text
    // "Select this option if you would like to display the Module container." The resource wording is
    // authoritative, because it is what an administrator actually saw. So the flag governs the whole
    // container - the surrounding chrome, of which the title bar is merely one part - and not the
    // title alone. The schema column name is preserved verbatim for traceability and the true
    // meaning is documented on the member; the alternative, renaming it to something like
    // DisplayContainer, would read better but would sever the correspondence with the column and
    // with every legacy call site, which the data-model fidelity requirement forbids.
    public bool DisplayTitle { get; set; }

    /// <summary>
    /// The date from which this module begins to be displayed, mapped from
    /// <c>Modules.StartDate</c>; <c>null</c> when no start date is set. The legacy settings screen
    /// labelled it "Start Date:", described as "Enter the start date for displaying this module.  You
    /// may use the Calendar to pick a date."
    /// </summary>
    /// <remarks>
    /// <para>
    /// A per-module fact, added to the module table by 02.02.00 line 324 as <c>datetime NULL</c>.
    /// Together with <see cref="EndDate"/> this pair <strong>gates whether the module renders at
    /// all</strong>, which is why the translation described below is stated explicitly instead
    /// of being left to a default conversion: getting it wrong does not corrupt a display value, it
    /// makes content appear or disappear.
    /// </para>
    /// <para>
    /// Nullable, mirroring the column and the legacy constructor, which seeded the backing field with
    /// the absent-date sentinel. The legacy input carried a length attribute of 11 characters, but
    /// that bounded the text box only and never the column, which is a date type; it is noted here as
    /// legacy trivia and is not a constraint on this member.
    /// </para>
    /// </remarks>
    // MIGRATION: §5.4 - the legacy absent-date sentinel is DateTime.MinValue, not a database null,
    // and the translation is owned explicitly by Application/Mapping/ModuleMappings.cs rather than
    // left to serialisation. The sentinel is proven in both directions by the legacy screen. On the
    // READ side, Website/admin/Modules/ModuleSettings.ascx.vb lines 152-157:
    //     If Not Null.IsNull(objModule.StartDate) Then
    //         txtStartDate.Text = objModule.StartDate.ToShortDateString
    //     End If
    // so a sentinel date rendered as an EMPTY box - the screen presented it as absent. On the WRITE
    // side, lines 367-372 of the same handler:
    //     If txtStartDate.Text <> "" Then
    //         objModule.StartDate = Convert.ToDateTime(txtStartDate.Text)
    //     Else
    //         objModule.StartDate = Null.NullDate
    //     End If
    // so clearing the field wrote DateTime.MinValue into the domain object, NOT a database null. The
    // two representations are therefore equivalent in legacy intent but distinct in value, and the
    // mapper must convert MinValue to null when reading and null to MinValue when writing. A
    // mapper that let MinValue through unconverted would emit the year 1 as though it were a real
    // start date, making every such module appear permanently started; one that treated a genuine
    // stored date as absent would suppress a module that should render.
    public DateTime? StartDate { get; set; }

    /// <summary>
    /// The date after which this module stops being displayed, mapped from
    /// <c>Modules.EndDate</c>; <c>null</c> when no end date is set. The legacy settings screen
    /// labelled it "End Date:", described as "Enter the end date for displaying this module.  You may
    /// use the Calendar to pick a date."
    /// </summary>
    /// <remarks>
    /// A per-module fact, added alongside <see cref="StartDate"/> by 02.02.00 line 325 as
    /// <c>datetime NULL</c>, and subject to the identical sentinel treatment - see the note on
    /// <see cref="StartDate"/>, which presents the measured evidence for both members. The legacy
    /// read guard and write branch for this member sit at ModuleSettings.ascx.vb lines 155-157 and
    /// 373-378 respectively and are structurally identical to their start-date counterparts. As with
    /// the start date, the 11-character length attribute on the legacy input bounded the text box
    /// only, never the column.
    /// </remarks>
    // MIGRATION: §5.4 (companion) - the absent-date sentinel DateTime.MinValue applies identically
    // here, and the same explicit mapper owns the conversion in both directions. Called out
    // separately rather than folded into the note above because this member is the more dangerous of
    // the two: an unconverted MinValue leaking into an END date states that the module stopped being
    // displayed in the year 1, which would suppress every module carrying no end date - the common
    // case - instead of merely mislabelling one. The two dates are also independent: the legacy
    // screen validated each only as a well-formed date (modulesettings.ascx declares comparison
    // validators valtxtStartDate and valtxtEndDate and nothing else), so it never enforced that the
    // end date follows the start date. That looseness is preserved; no ordering rule is invented
    // here, and none is implied by this contract.
    public DateTime? EndDate { get; set; }

    // MIGRATION: §5.8 (companion) - the member list ends here, at thirteen, and is deliberately not
    // extended. The legacy Library/Components/Modules/ModuleInfo.vb exposes 58 properties, measured,
    // because it is a flattened four-table join carrying computed and presentation members alongside
    // persisted ones. A grid row is a projection, so the gap between 58 and 13 is the design rather
    // than an omission, and nobody should add members to close it. Every exclusion is enumerated with
    // its measured evidence in the class-level notes above; the load-bearing ones to re-read before
    // extending this type are the portal identifier (supplied by the per-request portal context), all
    // paging metadata (owned by the response envelope, never by a row), the business controller type
    // name (never publish an activatable type name), and the permission members (relocated to the
    // security layer and the permissions catalogue).
}
