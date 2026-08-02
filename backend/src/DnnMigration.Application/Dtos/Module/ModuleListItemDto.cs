using DnnMigration.Domain.Enums;

namespace DnnMigration.Application.Dtos.Module;

/// <summary>
/// One row of the module listing returned by <c>GET /api/v1/modules</c>: a single module instance as
/// it is placed on a page, reduced to the facts an administration grid displays. Carries no paging
/// metadata of its own - it is one row and nothing more - and no navigation property, tracked state
/// or behaviour, so no domain entity is exposed through it in either direction.
/// </summary>
/// <remarks>
/// <para>
/// COMPOSED FROM THREE TABLES, AND <see cref="TabModuleId"/> IS THE ROW KEY.
/// <see cref="ModuleId"/>, <see cref="ModuleDefId"/>, <see cref="ModuleTitle"/>,
/// <see cref="AllTabs"/>, <see cref="IsDeleted"/>, <see cref="StartDate"/> and
/// <see cref="EndDate"/> describe the module itself and come from <c>dbo.Modules</c>;
/// <see cref="TabModuleId"/>, <see cref="TabId"/>, <see cref="ModuleOrder"/>,
/// <see cref="Visibility"/> and <see cref="DisplayTitle"/> describe one placement of that module on
/// one page and come from <c>dbo.TabModules</c>; <see cref="FriendlyName"/> is a read-only
/// projection from <c>dbo.ModuleDefinitions</c>. A module with <see cref="AllTabs"/> set therefore
/// appears as several rows - one per placement, each with its own <see cref="TabModuleId"/>,
/// <see cref="TabId"/> and <see cref="ModuleOrder"/> - while sharing one <see cref="ModuleId"/>. A
/// consumer that assumes a single physical row behind this contract will mis-model that case.
/// </para>
/// <para>
/// THE PLACEMENT SPLIT IS DESTRUCTIVE IN THE SCHEMA, NOT A MODELLING PREFERENCE. Script 03.00.01
/// adds <c>PortalID</c> to <c>Modules</c> (line 11), drops the foreign key that had tied
/// <c>Modules</c> to <c>Tabs</c> (lines 13-14), drops ten columns from <c>Modules</c> in one
/// statement including <c>ModuleOrder</c> (lines 197-198) and drops <c>TabID</c> itself (lines
/// 204-205); the replacement <c>TabModules</c> table is created at line 19 of the same script with
/// <c>TabModuleID int NOT NULL IDENTITY (1, 1)</c>, <c>TabID</c>, <c>ModuleID</c>, <c>PaneName</c>,
/// <c>ModuleOrder</c> and, at line 31, <c>Visibility int NOT NULL</c>. <c>DisplayTitle</c> joins it
/// at 03.00.08 line 156. On the module side, <c>AllTabs</c> arrives at 01.00.04 line 85 with a
/// default of 0, <c>IsDeleted</c> at 02.00.00 line 6568 with a default of 0, and <c>StartDate</c>
/// and <c>EndDate</c> at 02.02.00 lines 324-325. Only the cumulative terminal state is meaningful:
/// the 01.00.00 baseline <c>Modules</c> table (line 220) declares none of these six columns.
/// </para>
/// <para>
/// SENTINELS ARE PRESERVED, NOT NORMALISED, AND SEVERAL COLLIDE WITH REAL DATA. The legacy layer
/// encoded absence in-band as -1 for integers, the empty string for text and
/// <see cref="System.DateTime.MinValue"/> for dates. In this schema <see cref="ModuleId"/> and
/// <see cref="TabId"/> are seeded from 0 while <see cref="TabModuleId"/> and
/// <see cref="ModuleDefId"/> are seeded from 1, and -1 is simultaneously a live
/// <see cref="ModuleOrder"/> instruction, a genuine portal identifier and an "any page" wildcard in
/// the legacy query surface. Two rules follow: never test an identifier on this type against 0 or
/// -1 to decide whether it is present, and never assume a nullable member's legacy representation
/// was itself null. Each member documents which of its boundary values carry meaning.
/// </para>
/// <para>
/// NO DIRECT LEGACY GRID PRECEDENT. Unlike the portal, user and role listings this shape was not
/// lifted from an existing data grid, because <c>Website/admin/Modules/</c> holds three screens only
/// - settings, export and import - and no list page. The column set is synthesised from the
/// settings screen's field conventions and from what the pre-existing query surface on
/// <c>ModuleController.vb</c> can return, every method of which yields the same flattened legacy
/// object.
/// </para>
/// <para>
/// SECURITY: <c>BusinessControllerClass</c> is deliberately omitted. The legacy code passed that
/// stored type name to a reflection-based activator at five call sites; the target resolves module
/// behaviour from a closed, dependency-injected set through a factory. Publishing an activatable
/// type name on the wire would invite arbitrary activation, so it must never appear on a response
/// and never be accepted on a request.
/// </para>
/// <para>
/// ALSO DELIBERATELY ABSENT, several of them real terminal columns: the portal identifier, because
/// the request is already portal-scoped by the per-request
/// portal context (and <c>Modules.PortalID</c> is itself nullable, 03.00.01 line 11); pane-layout,
/// rendering, container and skinning members, whose purpose was server-side markup generation;
/// permission collections, since permission evaluation belongs to the infrastructure security layer
/// and a separate read-only catalogue - note that <c>AuthorizedEditRoles</c> and
/// <c>AuthorizedViewRoles</c> were themselves dropped from <c>Modules</c> at 03.00.01 lines
/// 1401-1405, which is why the legacy reader re-read them inside a swallowing Try/Catch;
/// <c>IsDefaultModule</c> and <c>AllModules</c>, which are intent flags belonging on an update
/// request rather than module state; <c>CacheTime</c>, <c>IconFile</c>, <c>Header</c> and
/// <c>Footer</c>, which are settings-screen concerns; definition, desktop-module and control
/// metadata, reachable through <see cref="ModuleDefinitionDto"/>; audit members, of which this
/// schema declares none on either table; and any nested page shape, pages being consumed as a
/// lookup exactly as the legacy picker did. This type also carries no validation attribute: a list
/// row is never submitted.
/// </para>
/// </remarks>
public sealed class ModuleListItemDto
{
    /// <summary>
    /// The identity of the module itself, mapped from <c>Modules.ModuleID</c>
    /// (<c>int IDENTITY (0, 1) NOT NULL</c>, the table's primary key, 01.00.00 line 221).
    /// </summary>
    /// <remarks>
    /// THE SEED IS 0, so 0 is a legitimate module identifier held by the very first module ever
    /// created; a test of the form "identifier is less than or equal to zero" rejects a real row.
    /// This value is also NOT unique across a listing - a module displayed on every page contributes
    /// one row per placement, all sharing it - so use <see cref="TabModuleId"/> for row identity.
    /// </remarks>
    public int ModuleId { get; set; }

    /// <summary>
    /// The identity of THIS PLACEMENT of the module on a page, mapped from
    /// <c>TabModules.TabModuleID</c> (<c>int NOT NULL IDENTITY (1, 1)</c>, the placement table's
    /// primary key, 03.00.01 lines 19-21). This is the stable key for a row of the listing.
    /// </summary>
    /// <remarks>
    /// Its seed is 1, a different seed from <see cref="ModuleId"/> and <see cref="TabId"/>, which
    /// both start at 0 - three different seeds across the four identifiers on this type, which is
    /// why no single "looks empty" numeric test is valid for any of them. This identifier is also
    /// the key under which placement-scoped settings are stored, so it is what a caller carries
    /// forward when opening settings for one specific placement rather than for the module.
    /// </remarks>
    public int TabModuleId { get; set; }

    /// <summary>
    /// The page this placement belongs to, mapped from <c>TabModules.TabID</c>. The legacy interface
    /// called this a page, not a tab: the settings screen labelled its picker "Move To Page:".
    /// </summary>
    /// <remarks>
    /// <para>
    /// The underlying <c>Tabs.TabID</c> is <c>int IDENTITY (0, 1) NOT NULL</c> (01.00.00 line 140),
    /// so 0 is a legitimate page identifier and must not be read as "no page".
    /// </para>
    /// <para>
    /// A sharper trap surrounds -1: in the legacy query surface, -1 supplied as a page identifier was
    /// an "ANY PAGE" WILDCARD rather than an absent value - <c>ModuleController.vb</c> lines
    /// 1223-1225 implement <c>GetModuleTabs(ModuleID)</c> as the per-module read with the
    /// absent-integer constant in the page position precisely so that it returns every placement
    /// across all pages. A value of this member is always a concrete page, because a row of this
    /// listing is always a real placement.
    /// </para>
    /// </remarks>
    public int TabId { get; set; }

    /// <summary>
    /// The module definition this instance was created from, mapped from <c>Modules.ModuleDefID</c>.
    /// </summary>
    /// <remarks>
    /// The referenced <c>ModuleDefinitions.ModuleDefID</c> is <c>int IDENTITY (1, 1) NOT NULL</c>
    /// (01.00.00 line 66), so no boundary value on this member carries a second meaning. Only the
    /// identifier travels on a list row; a caller needing the definition's display metadata resolves
    /// it from <c>GET /api/v1/module-definitions</c>, whose contract is
    /// <see cref="ModuleDefinitionDto"/>. <see cref="FriendlyName"/> is the single definition fact
    /// carried inline, because a grid showing only a numeric definition identifier would be
    /// unreadable.
    /// </remarks>
    public int ModuleDefId { get; set; }

    /// <summary>
    /// The administrator-supplied title of this module instance, mapped from
    /// <c>Modules.ModuleTitle</c> (<c>nvarchar(256) NULL</c>, 01.00.00 line 226). Legacy label
    /// "Title:".
    /// </summary>
    /// <remarks>
    /// <para>
    /// GENUINELY OPTIONAL. The legacy settings screen declares no presence validator on this field -
    /// <c>modulesettings.ascx</c> carries none anywhere, and its comparison validators leave the title
    /// untouched - so a module with no title is a valid legacy state rather than corrupt data. The
    /// 256-character bound comes from the schema alone; the
    /// legacy input carries no length attribute, and enforcement belongs to the validation layer and
    /// the database rather than to this type.
    /// </para>
    /// <para>
    /// The legacy representation of "no title" was the EMPTY STRING, not null: the legacy reader
    /// applied the absent-string constant to this column on every read, so a database null and an
    /// empty string were indistinguishable once loaded. This contract uses null for absence, which
    /// makes them distinguishable again. Translating between the two representations is an explicit
    /// obligation of the hand-written module mapper under <c>Application/Mapping/</c> and must not be
    /// left to serialisation, because a silent conversion in either direction is an observable change
    /// of behaviour for any caller that distinguishes an empty title from a missing one.
    /// </para>
    /// </remarks>
    public string? ModuleTitle { get; set; }

    /// <summary>
    /// The display name of the module's definition - what the legacy settings screen showed for
    /// "Module:" - projected read-only from <c>ModuleDefinitions.FriendlyName</c>
    /// (<c>nvarchar(128)</c>).
    /// </summary>
    /// <remarks>
    /// Carried inline purely so a grid can name each row's module type without a second request, and
    /// NEVER WRITABLE: it belongs to the definition, not to this instance, and no create or update
    /// contract accepts it. The legacy screen made the same statement structurally, rendering it into
    /// a text box marked <c>Enabled="False"</c> (<c>modulesettings.ascx</c> line 28) and assigning it
    /// without ever reading it back (<c>ModuleSettings.ascx.vb</c> line 125). Nullable because it is
    /// the result of a join and so is absent whenever the joined definition row cannot be resolved -
    /// a case the legacy layer surfaced as the empty string, with the same mapper obligation as
    /// <see cref="ModuleTitle"/>.
    /// </remarks>
    public string? FriendlyName { get; set; }

    /// <summary>
    /// The module's position within its pane on the page, mapped from
    /// <c>TabModules.ModuleOrder</c> (<c>int NOT NULL</c>, 03.00.01 line 25). Lower values render
    /// nearer the top of the pane.
    /// </summary>
    /// <remarks>
    /// A per-placement fact, so two placements of one module can legitimately sit at different
    /// positions on different pages. Non-nullable because -1 is a live instruction rather than an
    /// absent value - see the note below - and no mapper may translate it to null.
    /// </remarks>
    // MIGRATION: ModuleOrder of -1 is a COMMAND meaning "append at the bottom of the pane", and it
    // must survive verbatim in both directions. Three independent pieces of evidence in
    // Library/Components/Modules/ModuleController.vb establish it: the documented contract of
    // UpdateModuleOrder at line 1155 ("position within the controls list on page, -1 if to be added
    // at the end"); the branch that consumes it at lines 667-669 ("' position module at bottom of
    // pane"); and the same branch inside UpdateModuleOrder at lines 1163-1164, which reads the
    // existing maximum order for the pane and appends past it. The portal-template import path
    // assigns it directly at line 296 for exactly this purpose. The hazard is that the legacy
    // absent-integer constant is ALSO -1, so a mapping that mechanically converted the sentinel to
    // null here would silently destroy the append instruction and leave new modules at an arbitrary
    // position.
    public int ModuleOrder { get; set; }

    /// <summary>
    /// Whether this module is displayed in the same location on every page of the portal, mapped
    /// from <c>Modules.AllTabs</c> (<c>bit NOT NULL DEFAULT 0</c>, 01.00.04 line 85). Legacy label
    /// "Display Module On All Pages?".
    /// </summary>
    /// <remarks>
    /// Never absent, so non-nullable. THIS FLAG IS WHAT MAKES <see cref="TabModuleId"/> RATHER THAN
    /// <see cref="ModuleId"/> THE ROW KEY: when it is set the module has a placement row for every
    /// page and therefore contributes multiple rows to one listing.
    /// </remarks>
    public bool AllTabs { get; set; }

    /// <summary>
    /// How this placement is presented on its page, mapped from <c>TabModules.Visibility</c>
    /// (<c>int NOT NULL</c>, 03.00.01 line 31). Legacy label "Visibility:".
    /// </summary>
    /// <remarks>
    /// <para>
    /// A per-placement fact: the column is declared on the placement table and nowhere else in the
    /// upgrade chain. Non-nullable, with
    /// <see cref="ModuleVisibility.Maximized"/> (ordinal 0) as the default, matching both the column
    /// and the legacy constructor. The three ordinals persisted are exactly the three the legacy
    /// radio button list offered (<c>modulesettings.ascx</c> lines 145-147, values 0, 1 and 2).
    /// </para>
    /// <para>
    /// <see cref="ModuleVisibility.None"/> MEANS "THE MODULE IS NOT RENDERED". It does not mean the
    /// value is missing or has yet to be chosen. This is the single most likely misreading of the
    /// enumeration: it is a deliberate display state an administrator selected, and a consumer that
    /// treats it as "unset" and substitutes a default will make a suppressed module visible. The type
    /// offers no "unknown" member and none may be added.
    /// </para>
    /// </remarks>
    // MIGRATION: the legacy reader collapsed three distinct stored inputs into one output.
    // ModuleController.FillModuleInfo lines 81-85 select on
    // Convert.ToInt32(Null.SetNull(dr("Visibility"), intVisibility)) with cases "0, Null.NullInteger"
    // -> Maximized, "1" -> Minimized and "2" -> None, so a database null, a stored 0 and a stored -1
    // all surfaced as Maximized. The target preserves that collapse rather than inventing a fourth
    // state, which is why this member is not nullable: there was never an observable difference
    // between the three inputs at the contract boundary, so introducing one now would change
    // behaviour.
    //
    // MIGRATION: legacy defect, annotated and deliberately NOT repaired. That branch has no
    // catch-all case, and neither does its mirror on the write path
    // (Website/admin/Modules/ModuleSettings.ascx.vb lines 357-363), so a stored value outside
    // {-1, 0, 1, 2} falls through every branch and leaves the field at its type default of 0,
    // silently presenting an out-of-range row as Maximized instead of failing. The behaviour is
    // preserved exactly as measured. A related legacy looseness matters for whoever writes the
    // mapper: the read path assigned this enumeration straight into an integer-typed selected-index
    // property (ModuleSettings.ascx.vb line 134), an implicit conversion that only compiled because
    // the administration pages were built with strict type checking disabled. Every such coercion
    // must be made explicit in the target, and any difference in outcome documented.
    public ModuleVisibility Visibility { get; set; }

    /// <summary>
    /// Whether this module is in the soft-deleted state that the legacy recycle bin represented,
    /// mapped from <c>Modules.IsDeleted</c> (<c>bit NOT NULL</c> defaulting to 0, 02.00.00 line
    /// 6568). A row with this flag set still exists and can be restored; it is not a tombstone.
    /// </summary>
    /// <remarks>
    /// Never absent, so non-nullable. Carried on the list row because a grid that silently mixed
    /// deleted and live modules, or hid deleted ones with no way to see them, would lose a workflow
    /// the legacy application supported.
    /// </remarks>
    // MIGRATION: the legacy settings screen could only ever CLEAR this flag, never set it.
    // Website/admin/Modules/ModuleSettings.ascx.vb line 364 contains the bare, unconditional
    // assignment "objModule.IsDeleted = False" in the middle of its update handler, so saving a
    // module's settings always un-deleted it as a side effect, whatever its stored state and whether
    // or not the administrator intended it. This contract reports the stored state faithfully in both
    // directions and does not reproduce that side effect; restoring a module is a deliberate
    // operation in the target. The divergence is annotated because the legacy behaviour is
    // externally observable.
    public bool IsDeleted { get; set; }

    /// <summary>
    /// Whether the module's CONTAINER chrome is displayed around this placement, mapped from
    /// <c>TabModules.DisplayTitle</c> (<c>bit NOT NULL</c> defaulting to <c>(1)</c>, 03.00.08 line
    /// 156). Despite the column name the authoritative legacy wording is "Display Container?".
    /// </summary>
    /// <remarks>
    /// <para>
    /// THE MEMBER NAME AND ITS MEANING DISAGREE, AND THE DISAGREEMENT IS INHERITED. The legacy markup
    /// labels the checkbox "Display Title?" (<c>modulesettings.ascx</c> line 152), but that inline
    /// text is only a fallback: <c>Website/admin/Modules/App_LocalResources/ModuleSettings.ascx.resx</c>
    /// overrides it to "Display Container?" with the help text "Select this option if you would like
    /// to display the Module container." The resource wording is authoritative because it is what an
    /// administrator actually saw, so the flag governs the whole container chrome of which the title
    /// bar is merely one part. The schema column name is preserved verbatim for traceability -
    /// renaming it would sever the correspondence with the column and with every legacy call site.
    /// </para>
    /// <para>
    /// NOTE THE ASYMMETRY: <c>true</c> is the default of the COLUMN and of the legacy object, not of
    /// this contract. This member is a plain automatic property, so an unpopulated instance reports
    /// <c>false</c>. No initialiser is added, because one would make a default-constructed instance
    /// indistinguishable from one deliberately set to <c>true</c>. Populating this member from the
    /// stored value on every projection is therefore an obligation of the module mapper under
    /// <c>Application/Mapping/</c>, never something to be left to a default.
    /// </para>
    /// </remarks>
    public bool DisplayTitle { get; set; }

    /// <summary>
    /// The date from which this module begins to be displayed, mapped from
    /// <c>Modules.StartDate</c> (<c>datetime NULL</c>, 02.02.00 line 324); <c>null</c> when no start
    /// date is set. Legacy label "Start Date:".
    /// </summary>
    /// <remarks>
    /// Together with <see cref="EndDate"/> this pair GATES WHETHER THE MODULE RENDERS AT ALL, which
    /// is why the sentinel translation below is stated explicitly rather than left to a default
    /// conversion: getting it wrong does not corrupt a display value, it makes content appear or
    /// disappear. The 11-character length attribute on the legacy input bounded the text box only,
    /// never the column, which is a date type.
    /// </remarks>
    // MIGRATION: the legacy absent-date sentinel is DateTime.MinValue, not a database null, and the
    // translation is an explicit obligation of the module mapper under Application/Mapping/ rather
    // than something serialisation may decide. The sentinel is proven in both directions by the
    // legacy screen. READ side, Website/admin/Modules/ModuleSettings.ascx.vb lines 152-157: the text
    // box is assigned only "If Not Null.IsNull(objModule.StartDate)", so a sentinel date rendered as
    // an EMPTY box - the screen presented it as absent. WRITE side, lines 367-372: an empty box wrote
    // Null.NullDate, that is DateTime.MinValue, into the domain object and NOT a database null. The
    // two representations are equivalent in legacy intent but distinct in value, so the mapper must
    // convert MinValue to null when reading and null to MinValue when writing. A mapper that let
    // MinValue through unconverted would emit the year 1 as though it were a real start date, making
    // every such module appear permanently started; one that treated a genuine stored date as absent
    // would suppress a module that should render.
    public DateTime? StartDate { get; set; }

    /// <summary>
    /// The date after which this module stops being displayed, mapped from <c>Modules.EndDate</c>
    /// (<c>datetime NULL</c>, 02.02.00 line 325); <c>null</c> when no end date is set. Legacy label
    /// "End Date:".
    /// </summary>
    /// <remarks>
    /// Subject to the identical sentinel treatment as <see cref="StartDate"/>, whose note carries
    /// the measured evidence for both members; the legacy read guard and write branch for this member
    /// sit at <c>ModuleSettings.ascx.vb</c> lines 155-157 and 373-378 and are structurally identical.
    /// This member is the more dangerous of the two: an unconverted <c>DateTime.MinValue</c> leaking
    /// into an END date states that the module stopped being displayed in the year 1, which would
    /// suppress every module carrying no end date - the common case. THE TWO DATES ARE ALSO
    /// INDEPENDENT: the legacy screen validated each only as a well-formed date, never enforcing that
    /// the end date follows the start date, and that looseness is preserved - no ordering rule is
    /// invented here or implied by this contract.
    /// </remarks>
    public DateTime? EndDate { get; set; }

}
