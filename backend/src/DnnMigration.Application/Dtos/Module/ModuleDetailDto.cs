using DnnMigration.Domain.Enums;

namespace DnnMigration.Application.Dtos.Module;

/// <summary>
/// The full state of one module instance, returned as the response body of
/// <c>GET /api/v1/portals/{portalId}/modules/{moduleId}</c>. A boundary contract and nothing more: no navigation property, no
/// tracked state, no behaviour and no domain entity, in either direction. It describes a single item,
/// so it carries no paging envelope and no paging metadata - that envelope belongs to the listing
/// endpoint, whose row shape is <see cref="ModuleListItemDto"/>.
/// </summary>
/// <remarks>
/// <para>
/// FOUR TABLES FEED THIS ONE SHAPE, AND THE SOURCE OF EVERY MEMBER IS STATED BECAUSE IT CANNOT BE
/// INFERRED. The legacy layer exposed a single 936-line class carrying 58 properties that flattened the
/// <c>Modules</c>-to-<c>TabModules</c>-to-<c>ModuleDefinitions</c>-to-<c>ModuleControls</c> join into one
/// object, which actively concealed whether a given fact belonged to the module or to one of its
/// placements. The members below are grouped so that distinction is visible in the source:
/// </para>
/// <list type="bullet">
///   <item>
///     <term>Identity</term>
///     <description>
///     <see cref="ModuleId"/>, <see cref="TabModuleId"/>, <see cref="TabId"/>, <see cref="PortalId"/>,
///     <see cref="ModuleDefId"/> and <see cref="DesktopModuleId"/> - keys drawn from across all four
///     tables.
///     </description>
///   </item>
///   <item>
///     <term>Module scope, from <c>dbo.Modules</c></term>
///     <description>
///     <see cref="ModuleTitle"/>, <see cref="AllTabs"/>, <see cref="Header"/>, <see cref="Footer"/>,
///     <see cref="StartDate"/>, <see cref="EndDate"/>, <see cref="InheritViewPermissions"/> and
///     <see cref="IsDeleted"/> - identical on every page the module appears on.
///     </description>
///   </item>
///   <item>
///     <term>Placement scope, from <c>dbo.TabModules</c></term>
///     <description>
///     <see cref="ModuleOrder"/>, <see cref="CacheTime"/>, <see cref="IconFile"/>,
///     <see cref="Visibility"/> and <see cref="DisplayTitle"/> - specific to this one occurrence of the
///     module on this one page.
///     </description>
///   </item>
///   <item>
///     <term>Catalogue projections, from <c>dbo.ModuleDefinitions</c> and <c>dbo.DesktopModules</c></term>
///     <description>
///     <see cref="FriendlyName"/>, <see cref="ModuleName"/>, <see cref="Description"/> and
///     <see cref="Version"/> - read-only, never writable through any request contract.
///     </description>
///   </item>
/// </list>
/// <para>
/// THE PRODUCT ITSELF DREW THAT LINE, IN ITS OWN WORDS. The legacy settings screen was divided into two
/// captioned sections whose help text survives verbatim in
/// <c>Website/admin/Modules/App_LocalResources/ModuleSettings.ascx.resx</c>. "Module Settings" reads: "In
/// this section, you can define the settings that relate to the Module content and permissions (ie. those
/// settings that will be the same on all pages that the Module appears )." - that is exactly the module
/// scope above. "Page Settings" reads: "In this section, you can define settings specific to this
/// particular occurrence of the Module for this Page." - that is exactly the placement scope. The split
/// is therefore faithful to the original design rather than an invention of this migration.
/// </para>
/// <para>
/// CONSEQUENCE FOR CONSUMERS: a module whose <see cref="AllTabs"/> flag is set has one placement row per
/// page, so <see cref="ModuleId"/> is NOT a row key here - <see cref="TabModuleId"/> identifies the
/// specific occurrence this response describes.
/// </para>
/// <para>
/// SETTINGS ARE NOT CARRIED HERE. Neither the module-scoped nor the placement-scoped key-value settings
/// appear on this contract, and no map-valued member of any kind does. Both are genuine key-value tables
/// and both are served by <see cref="ModuleSettingsDto"/> through
/// <c>GET</c> and <c>PUT /api/v1/portals/{portalId}/modules/{moduleId}/settings</c>. Keeping them in their own contract is how the
/// target makes the module-versus-placement scope explicit, which is precisely what the legacy
/// flattening hid.
/// </para>
/// <para>
/// PERMISSIONS ARE NOT CARRIED HERE EITHER. No permission collection and no derived role string appears.
/// Permission evaluation belongs to the infrastructure security layer and the separate read-only
/// permissions catalogue. <see cref="InheritViewPermissions"/> is retained as the only
/// permission-adjacent member because it is a flag on the module row, not a permission entry.
/// </para>
/// <para>
/// THE EDITABLE SUBSETS ARE ELSEWHERE: <see cref="CreateModuleRequest"/> and
/// <see cref="UpdateModuleRequest"/>. This type declares no validation attribute, because a response is
/// never submitted and declarative validation lives with the request contracts.
/// </para>
/// </remarks>
// MIGRATION: 5.2 - THE FOUR-ENTITY SPLIT IS DESTRUCTIVE IN THE SCHEMA, NOT A MODELLING PREFERENCE, and
//   only the cumulative terminal state of the 88 upgrade scripts is meaningful. The 01.00.00 baseline
//   created dbo.Modules (line 220) with TabID, ModuleOrder, PaneName, CacheTime, Alignment and Color all
//   on the module row. Script 03.00.01 dismantled that: line 12 adds "PortalID int NULL", line 16 drops
//   the constraint FK_{objectQualifier}Modules_{objectQualifier}Tabs, line 19 creates
//   {databaseOwner}{objectQualifier}TabModules, line 198 executes "DROP COLUMN ModuleOrder, PaneName,
//   CacheTime, Alignment, Color, Border, IconFile, Personalize, ShowTitle, ContainerSrc" in one
//   statement, and line 205 executes "DROP COLUMN TabID". Terminal dbo.Modules is 11 columns; terminal
//   dbo.TabModules is 15, the last three joining at 03.00.08 lines 156-158. So in the terminal schema
//   TabId, ModuleOrder, CacheTime, IconFile, Visibility and DisplayTitle live on the PLACEMENT row and
//   NOT on the module row, and deriving any of them from the 01.00.00 baseline alone would be wrong. The
//   legacy write path corroborates the same boundary: its module insert passed ten arguments plus the
//   generated key, its module update nine, and its placement insert and update fourteen plus the
//   generated key.
//
// MIGRATION: 5.11 - DELIBERATE EXCLUSIONS, several of them real terminal columns the legacy screen truly
//   edited. Recorded here so a later reader does not mistake any of them for an oversight.
//     * The token-replacement accessor contract IPropertyAccess, which the legacy class implemented, is
//       dropped with that subsystem, and so is its Cacheability member - the latter returned a
//       web-framework HTTP cache-policy level and has no counterpart once caching is an injected service.
//       Neither is the same thing as CacheTime, which IS a genuine placement column and IS carried below.
//     * XML serialisation attributes are dropped throughout. The wire shape belongs to this layer and the
//       casing convention to one central serialisation policy at the edge, not to per-property attributes.
//     * PaneName, Alignment, Color, Border, DisplayPrint and DisplaySyndicate are omitted even though all
//       six are real terminal TabModules columns that the legacy screen edited through cboAlign, txtColor,
//       txtBorder (single character, validated by valBorder with the message "Invalid Border (must be a
//       number between 0 and 9)"), chkDisplayPrint and chkDisplaySyndicate. They exist to drive
//       server-side markup generation, and both server-side rendering and the postback presentation model
//       are excluded from this migration, so no RESPONSE contract reads them back and the stored columns
//       are preserved untouched. They remain settable through UpdateModuleRequest, which makes them
//       write-only in the target - a deliberate asymmetry. ModuleSettingsDto is NOT their home either: it
//       carries the module and placement identifiers and the two key-value settings maps only.
//       PaneModuleIndex and PaneModuleCount were computed render-time values and are omitted with them.
//     * ContainerSrc and ContainerPath are omitted because a module container is a skin object and
//       skinning is excluded. The stored column is preserved untouched rather than surfaced for editing.
//     * BusinessControllerClass is omitted deliberately and on security grounds. The legacy code handed
//       that stored type name to a reflection-based activator at five call sites; the target resolves
//       module behaviour from a closed, dependency-injected set through a factory. Publishing an
//       activatable type name would invite arbitrary activation, so it must never appear on a response
//       and never be accepted on a request.
//     * ModuleControls metadata - the control identifier, source path, control type, control title and
//       help address - is omitted. Module registration and lifecycle survive as a domain concern; the
//       control-loading mechanism does not. This also dissolves a real hazard: the legacy control-type
//       reader mapped -3 to a control-panel level, -2 to a skin-object level and -1 to an anonymous
//       level, so three negative values were genuine security levels and -1 collided exactly with the
//       legacy absent-integer sentinel. This contract promises no loadable control path.
//     * The SupportedFeatures bit field and its three derived flags are omitted; the portability flag
//       lives on the definition contract where the transfer screens need it, and search is deferred
//       entirely. Note that a SupportedFeatures value of -1 meant "installation not yet complete" - one
//       more negative number that was data rather than absence.
//     * The definition's DefaultCacheTime is omitted: it is catalogue metadata carried by
//       ModuleDefinitionDto, not a per-instance fact. See the note on CacheTime below.
//     * IsDefaultModule and AllModules are omitted because measurement shows they were never module
//       state at all - they were action flags the legacy update handler consumed to write two site
//       settings and to propagate appearance across pages. They belong on UpdateModuleRequest as intent.
//     * No page shape is nested here. Pages are consumed as a lookup, exactly as the legacy picker did,
//       and that picker bound to the page name and identifier of what is now the tab listing contract.
//     * No audit member appears, because a census of all 88 upgrade scripts finds that neither
//       dbo.Modules nor dbo.TabModules declares an audit column of any kind.
//     * No transport or error-model member appears: no status, no success flag, no problem-details
//       mirror and no correlation identifier. Errors are produced at the edge to RFC 7807 and the
//       correlation identifier travels in the X-Correlation-Id header.
//
// MIGRATION: 5.12 - THE TWO LEGACY HYDRATION PATHS ARE DELETED, NOT TRANSLATED. The legacy reader was a
//   47-line hand-rolled block assigning one property per line through a sentinel-substituting conversion
//   of each reader column, and its two sibling helpers returned an untyped legacy collection and an
//   untyped map of modules by identifier. A separate reflection-driven row hydrator served the same
//   purpose elsewhere in the tree. All of it is superseded by the EF Core materialiser, so no fill
//   method, no hydration contract and no reflection-based population appears anywhere in the target -
//   and none may be added to this type, which is inert by design.
public sealed class ModuleDetailDto
{
    // ---------------------------------------------------------------------------------------------
    // GROUP A - IDENTITY
    //
    // Five non-nullable keys and one genuinely nullable one, drawn from all four tables. They carry
    // THREE DIFFERENT IDENTITY SEEDS between them, which is why no single "looks empty" numeric test is
    // valid for any of them: dbo.Modules.ModuleID and dbo.Tabs.TabID are IDENTITY (0, 1), while
    // dbo.TabModules.TabModuleID and dbo.ModuleDefinitions.ModuleDefID are IDENTITY (1, 1), and
    // dbo.ModuleDefinitions.DesktopModuleID carries a stored default of 0. Combined with the legacy
    // absent-integer sentinel of -1, that makes both 0 and -1 legitimate stored values somewhere in this
    // group. NEVER test an identifier here against 0 or -1 to decide whether it is present.
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// The identity of the module itself, mapped from <c>Modules.ModuleID</c>
    /// (<c>int IDENTITY (0, 1) NOT NULL</c>, the table's primary key, 01.00.00 line 221).
    /// </summary>
    /// <remarks>
    /// THE SEED IS 0, so 0 is a legitimate module identifier held by the very first module ever created;
    /// a test of the form "identifier is less than or equal to zero" rejects a real row, and a test
    /// against -1 rejects nothing while implying a sentinel this contract never carries. This value is
    /// also not unique across the placements of one module - a module displayed on every page shares it
    /// across all of them - so <see cref="TabModuleId"/> is what identifies the occurrence described
    /// here.
    /// </remarks>
    public int ModuleId { get; set; }

    /// <summary>
    /// The identity of THIS PLACEMENT of the module on a page, mapped from
    /// <c>TabModules.TabModuleID</c> (<c>int NOT NULL IDENTITY (1, 1)</c>, the placement table's primary
    /// key, 03.00.01 line 21).
    /// </summary>
    /// <remarks>
    /// Its seed is 1, a different seed from <see cref="ModuleId"/> and <see cref="TabId"/>, which both
    /// start at 0. It is also the key under which placement-scoped settings are stored, so it is the
    /// value a caller carries forward when opening the settings of this one occurrence rather than those
    /// of the module as a whole.
    /// </remarks>
    public int TabModuleId { get; set; }

    /// <summary>
    /// The page this placement belongs to, mapped from <c>TabModules.TabID</c>. The legacy interface
    /// called this a page rather than a tab: its picker was captioned "Move To Page:".
    /// </summary>
    /// <remarks>
    /// <para>
    /// The referenced <c>Tabs.TabID</c> is <c>int IDENTITY (0, 1) NOT NULL</c> (01.00.00 line 140), so 0
    /// is a legitimate page identifier and must not be read as "no page".
    /// </para>
    /// <para>
    /// A sharper trap surrounds -1. In the legacy query surface, -1 supplied in the page position was an
    /// "ANY PAGE" WILDCARD rather than an absent value: the per-module read that returns every placement
    /// across all pages is implemented as the page-scoped read with the absent-integer constant in that
    /// position, precisely so the filter matches everything. A value of this member is always a concrete
    /// page, because this response always describes a real placement.
    /// </para>
    /// <para>
    /// This member comes from the PLACEMENT row. The module row once carried a <c>TabID</c> column of its
    /// own, but it was dropped - see the schema note on the type.
    /// </para>
    /// </remarks>
    public int TabId { get; set; }

    /// <summary>
    /// The portal that owns the module, mapped from <c>Modules.PortalID</c>, or <see langword="null"/>
    /// when the module is not portal-scoped - that is, a host-level module.
    /// </summary>
    /// <remarks>
    /// THE ONLY GENUINELY NULLABLE IDENTIFIER ON THIS CONTRACT. Every other key here is always present.
    /// </remarks>
    // MIGRATION: 5.10 - this member is nullable for a schema reason and a semantic reason, and the two
    //   must not be conflated. The schema reason: 03.00.01 line 12 adds the column as "PortalID int
    //   NULL", and that ALTER is the exact migration in which modules became portal-scoped and the
    //   foreign key tying them to pages was dropped, so a stored null is a real, reachable state. The
    //   semantic reason: dbo.Portals.PortalID is IDENTITY (-1, 1) (01.00.00 line 77), so -1 IS A REAL
    //   PORTAL IDENTIFIER - it is the seed, the first value the column generates - and it is
    //   simultaneously the value of the legacy absent-integer sentinel. The shipped default portal is a
    //   separate row inserted with an explicit PortalID of 0 (01.00.00 line 7125), so 0 is a real portal
    //   key too. A null here means "not portal-scoped"; a -1 here means "the portal whose identifier is
    //   -1". Those are DIFFERENT FACTS. Treating -1 as absence would reassign every module of that portal
    //   to no portal at all, and no mapper may do it.
    public int? PortalId { get; set; }

    /// <summary>
    /// The module definition this instance was created from, mapped from <c>Modules.ModuleDefID</c>.
    /// </summary>
    /// <remarks>
    /// The referenced <c>ModuleDefinitions.ModuleDefID</c> is <c>int IDENTITY (1, 1) NOT NULL</c>
    /// (01.00.00 line 66), so no boundary value on this member carries a second meaning. Only the
    /// identifier and the four catalogue projections at the end of this type travel here; the full
    /// definition metadata is resolved from <c>GET /api/v1/module-definitions</c>, whose contract is
    /// <see cref="ModuleDefinitionDto"/>.
    /// </remarks>
    public int ModuleDefId { get; set; }

    /// <summary>
    /// The installed module package behind the definition, projected from
    /// <c>ModuleDefinitions.DesktopModuleID</c>.
    /// </summary>
    /// <remarks>
    /// UNLIKE THE OTHER KEYS HERE, THIS ONE HAS A STORED DEFAULT OF 0: the column is declared
    /// <c>int NOT NULL CONSTRAINT DF_{objectQualifier}ModuleDefinitions_DesktopModuleID DEFAULT 0</c>
    /// (02.00.00 line 5174). A value of 0 is therefore something the database itself writes, and reading
    /// it as "no package" is wrong. The package's own key column is <c>IDENTITY (1, 1)</c>, so 0 also
    /// indicates a definition whose package was never linked - a legacy data state to be reported, not
    /// silently corrected.
    /// </remarks>
    public int DesktopModuleId { get; set; }

    // ---------------------------------------------------------------------------------------------
    // GROUP B - MODULE SCOPE, THE dbo.Modules ROW
    //
    // Facts about the module itself, identical on every page it appears on. The product's own words for
    // this group, from the legacy screen's "Module Settings" caption: "those settings that will be the
    // same on all pages that the Module appears".
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// The administrator-supplied heading of this module instance, mapped from
    /// <c>Modules.ModuleTitle</c> (<c>nvarchar(256) NULL</c>, 01.00.00 line 226). Legacy caption "Title:",
    /// whose help text reads: "Enter a title for the Module.  This will appear in the Title Bar of the
    /// Container for this Module, if supported by the Container."
    /// </summary>
    /// <remarks>
    /// <para>
    /// GENUINELY OPTIONAL, MEASURED RATHER THAN ASSUMED. The legacy settings markup declares no presence
    /// validator anywhere in the file - zero of them - and its only four validators are type checks on the
    /// two dates, the border and the cache period. A module with no title is therefore a valid legacy
    /// state, not corrupt data.
    /// </para>
    /// <para>
    /// The 256-character bound comes from the schema alone; the legacy input carried no length attribute.
    /// It is documented here and enforced by the validation layer and the database, never by an attribute
    /// on this type.
    /// </para>
    /// <para>
    /// The legacy representation of "no title" was the EMPTY STRING and not null: the absent-string
    /// constant evaluates to the empty string, and it was applied to this column on every read, so a
    /// stored null and an empty string became indistinguishable once loaded. This contract uses null for
    /// absence, which makes them distinguishable again. Performing that translation is an explicit
    /// obligation of the hand-written module mapper and must not be left to serialisation, because a
    /// silent conversion in either direction is an observable change of behaviour for any caller that
    /// distinguishes a blank title from a missing one.
    /// </para>
    /// </remarks>
    public string? ModuleTitle { get; set; }

    /// <summary>
    /// Whether the module appears in the same location on every page of the portal, mapped from
    /// <c>Modules.AllTabs</c> (<c>bit</c> with a stored default of 0). Legacy caption "Display Module On
    /// All Pages?", whose help text reads: "Select whether the module should appear in the same location
    /// on all pages of the site".
    /// </summary>
    /// <remarks>
    /// Never absent, so not nullable. THIS FLAG IS WHY <see cref="TabModuleId"/> RATHER THAN
    /// <see cref="ModuleId"/> IDENTIFIES WHAT THIS RESPONSE DESCRIBES: when it is set the module has a
    /// placement row for every page, and this contract describes exactly one of them.
    /// </remarks>
    // MIGRATION: 5.8 - THIS FIELD WAS ADMINISTRATOR-ONLY ON THE LEGACY SCREEN, AND THAT RESTRICTION IS
    //   NOT REPRODUCED AS A CONTRACT MEMBER. Website/admin/Modules/ModuleSettings.ascx.vb lines 215-219
    //   disabled four inputs for any caller not in the administrator role - the all-pages checkbox, the
    //   two behaviour flags and the page picker - leaving the values visible but not editable. The target
    //   enforces the same restriction through policy-based authorisation on the write path, which is
    //   where an authorisation decision belongs. This contract therefore REPORTS the stored value to
    //   every caller permitted to read the module, exactly as the legacy screen displayed it, and adds no
    //   member describing who may change it. A consumer must not infer editability from readability.
    public bool AllTabs { get; set; }

    /// <summary>
    /// Free text or markup rendered above the module's content, mapped from <c>Modules.Header</c>. The
    /// legacy help text reads: "Enter the text or HTML that you would like to appear above the module
    /// content."
    /// </summary>
    /// <remarks>
    /// The legacy input was a six-row multi-line text box with no length attribute, so the only bound is
    /// the column's own. Absence was represented as the EMPTY STRING rather than null, with the same
    /// mapper obligation described on <see cref="ModuleTitle"/>. The value is opaque stored content
    /// returned verbatim; this contract makes no claim that it is sanitised, and any sanitisation is a
    /// rendering-side concern.
    /// </remarks>
    public string? Header { get; set; }

    /// <summary>
    /// Free text or markup rendered below the module's content, mapped from <c>Modules.Footer</c>, the
    /// counterpart of <see cref="Header"/>.
    /// </summary>
    /// <remarks>
    /// Identical treatment to <see cref="Header"/> in every respect: a six-row multi-line legacy input,
    /// no length attribute, the empty string rather than null for absence, and opaque content returned
    /// verbatim.
    /// </remarks>
    public string? Footer { get; set; }

    /// <summary>
    /// The date from which the module begins to be displayed, mapped from <c>Modules.StartDate</c>, or
    /// <see langword="null"/> when no start date is set. The legacy help text reads: "Enter the start
    /// date for displaying this module.  You may use the Calendar to pick a date."
    /// </summary>
    /// <remarks>
    /// Together with <see cref="EndDate"/> this pair GATES WHETHER THE MODULE RENDERS AT ALL, which is
    /// why the sentinel translation below is stated explicitly instead of being left to a default
    /// conversion: getting it wrong does not distort a displayed value, it makes content appear or vanish.
    /// The 11-character length attribute on the legacy input bounded the text box only and never the
    /// column, which is a date type; it is legacy trivia and no bound on this member.
    /// </remarks>
    // MIGRATION: 5.4 - THE LEGACY ABSENT-DATE SENTINEL IS DateTime.MinValue, NOT A DATABASE NULL, and the
    //   translation is an explicit obligation of the hand-written module mapper rather than something
    //   serialisation may decide. It is proven in BOTH directions by the legacy screen.
    //   READ side, Website/admin/Modules/ModuleSettings.ascx.vb lines 152-157: the text box is assigned
    //   only inside "If Not Null.IsNull(objModule.StartDate) Then", and the absent-integer test that
    //   guard resolves to treats the sentinel date as absent, so a sentinel value rendered as an EMPTY
    //   BOX - the screen presented it as "no date".
    //   WRITE side, lines 367-375: an empty box assigned Null.NullDate, which evaluates to
    //   DateTime.MinValue, into the stored object and NOT a database null.
    //   The two representations are equivalent in legacy intent but distinct in value, so the mapper
    //   converts the sentinel to null when reading and null to the sentinel when writing. Letting the
    //   sentinel through unconverted would emit the year 1 as a real start date, making every such module
    //   appear permanently started; treating a genuine stored date as absent would suppress a module that
    //   should render.
    public DateTime? StartDate { get; set; }

    /// <summary>
    /// The date after which the module stops being displayed, mapped from <c>Modules.EndDate</c>, or
    /// <see langword="null"/> when no end date is set. The legacy help text reads: "Enter the end date for
    /// displaying this module.  You may use the Calendar to pick a date."
    /// </summary>
    /// <remarks>
    /// Subject to the identical sentinel treatment as <see cref="StartDate"/>, whose note carries the
    /// measured evidence for both members. This is the more dangerous of the two: an unconverted
    /// <see cref="System.DateTime.MinValue"/> leaking into an END date asserts that the module stopped
    /// being displayed in the year 1, which would suppress every module carrying no end date - the common
    /// case. THE TWO DATES ARE ALSO INDEPENDENT: the legacy screen validated each only as a well-formed
    /// date and never enforced that the end followed the start, and that looseness is preserved. No
    /// ordering rule is invented here or implied by this contract.
    /// </remarks>
    // MIGRATION: 5.4 (continued) - the sentinel evidence for this member sits at the same two sites as
    //   for StartDate: the read guard at ModuleSettings.ascx.vb lines 155-157 and the write branch at
    //   lines 373-375, which are structurally identical to their start-date counterparts.
    public DateTime? EndDate { get; set; }

    /// <summary>
    /// Whether the module takes its VIEW permission from the page it sits on instead of carrying its own,
    /// mapped from <c>Modules.InheritViewPermissions</c>. The legacy caption, with its emphasis markup
    /// intact, read "Inherit &lt;b&gt;View&lt;/b&gt; permissions from &lt;b&gt;Page&lt;/b&gt;".
    /// </summary>
    /// <remarks>
    /// <para>
    /// A FLAG AND NOTHING MORE. The permission entries themselves are deliberately absent from this
    /// contract - see the note on the type - so this member says how view permission is SOURCED, never
    /// what it grants or to whom. It is the only permission-adjacent member here, and it qualifies only
    /// because it is a column on the module row.
    /// </para>
    /// <para>
    /// The legacy screen bound it to a checkbox that posted back on change purely so the permission grid
    /// beside it could be shown or hidden; that was a presentation mechanism and has no counterpart here.
    /// Not nullable: the legacy object left the backing field at its type default rather than
    /// sentinel-initialising it, and the absent-boolean constant is itself false, so a stored null and a
    /// stored false were never distinguishable at the contract boundary.
    /// </para>
    /// </remarks>
    public bool InheritViewPermissions { get; set; }

    /// <summary>
    /// Whether the module is in the soft-deleted state that the legacy recycle bin represented, mapped
    /// from <c>Modules.IsDeleted</c> (<c>bit</c> with a stored default of 0). A row with this flag set
    /// still exists and can be restored; it is not a tombstone.
    /// </summary>
    /// <remarks>
    /// Never absent, so not nullable. Reported here because a client that silently mixed deleted and live
    /// modules, or hid deleted ones with no way to see them, would lose a workflow the legacy application
    /// supported.
    /// </remarks>
    // MIGRATION: 5.6 - THE LEGACY SETTINGS SAVE COULD ONLY EVER CLEAR THIS FLAG, NEVER SET IT.
    //   Website/admin/Modules/ModuleSettings.ascx.vb line 364 contains the bare, unconditional assignment
    //   "objModule.IsDeleted = False" in the middle of its update handler, so saving a module's settings
    //   always un-deleted it as a side effect, whatever its stored state and whether or not the operator
    //   intended it. This contract reports the stored state faithfully and does not reproduce that side
    //   effect; restoring a module is a deliberate operation in the target. The divergence is annotated
    //   because the legacy behaviour was externally observable.
    //   Note also what the legacy delete action actually did: line 305 called DeleteTabModule(TabId,
    //   ModuleId), which removed THE PLACEMENT rather than the module. Deleting an occurrence and
    //   soft-deleting the module are two different operations, and this flag describes only the latter.
    public bool IsDeleted { get; set; }

    // ---------------------------------------------------------------------------------------------
    // GROUP C - PLACEMENT SCOPE, THE dbo.TabModules ROW
    //
    // Facts about this ONE occurrence of the module on ONE page, which may differ from every other
    // occurrence of the same module. The product's own words for this group, from the legacy screen's
    // "Page Settings" caption: "settings specific to this particular occurrence of the Module for this
    // Page". Every column below was moved off the module row by script 03.00.01 - see the schema note on
    // the type - so attributing any of them to dbo.Modules would contradict the terminal schema.
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// The placement's position within its pane on the page, mapped from <c>TabModules.ModuleOrder</c>
    /// (<c>int NOT NULL</c>, 03.00.01 line 25). Lower values render nearer the top of the pane.
    /// </summary>
    /// <remarks>
    /// A per-placement fact, so two occurrences of one module can legitimately sit at different positions
    /// on different pages. Not nullable, because -1 is a live instruction rather than an absent value -
    /// see the note below - and no mapper may translate it away.
    /// </remarks>
    // MIGRATION: 5.1 - A ModuleOrder OF -1 IS A COMMAND MEANING "APPEND AT THE BOTTOM OF THE PANE", AND
    //   IT MUST SURVIVE VERBATIM IN BOTH DIRECTIONS. Three independent pieces of evidence in
    //   Library/Components/Modules/ModuleController.vb establish it: the documented contract of the order
    //   update at line 1155, "position within the controls list on page, -1 if to be added at the end";
    //   the branch that consumes it at lines 667-669, whose own comment reads "' position module at
    //   bottom of pane"; and the branch inside the order update at lines 1164-1167, which reads the
    //   existing maximum order for the pane and appends past it. The portal-template import path assigns
    //   it directly at line 296 for exactly this purpose.
    //   THE HAZARD: the legacy absent-integer sentinel is ALSO -1. A mapping that mechanically converted
    //   that sentinel to null, or that rejected -1 as invalid, would silently destroy the append
    //   instruction and leave new modules at an arbitrary position. This member therefore stays a plain
    //   integer and -1 is passed through untouched.
    public int ModuleOrder { get; set; }

    /// <summary>
    /// How long this placement's rendered output may be cached, in seconds, mapped from
    /// <c>TabModules.CacheTime</c> (<c>int NOT NULL</c>, 03.00.01 line 26). Legacy caption "Cache Time
    /// (secs):".
    /// </summary>
    /// <remarks>
    /// A per-placement fact: the same module can be cached for different periods on different pages. Not
    /// nullable, and 0 is a meaningful stored value rather than an absent one - see the notes below.
    /// </remarks>
    // MIGRATION: 5.5 - A BLANK CACHE FIELD WROTE ZERO, NOT A SENTINEL, SO 0 MEANS "NO CACHING" AND IS
    //   REAL DATA. Website/admin/Modules/ModuleSettings.ascx.vb lines 349-352 read: if the text box was
    //   non-empty the value was parsed as an integer, "Else objModule.CacheTime = 0". The legacy help
    //   text says the same thing in words - enter zero for no caching. This member is therefore a plain
    //   integer and never a nullable one, and no consumer or mapper may treat 0 as unset and substitute a
    //   default: doing so would silently switch caching on for every placement that had it deliberately
    //   switched off.
    //
    // MIGRATION: 5.7 - WHETHER THIS FIELD IS APPLICABLE AT ALL IS DEFINITION METADATA, AND IT IS NOT
    //   CARRIED HERE. Website/admin/Modules/ModuleSettings.ascx.vb lines 138-142 hid the entire cache row
    //   when the DEFINITION's DefaultCacheTime equalled the absent-integer constant: "If
    //   objModuleDef.DefaultCacheTime = Null.NullInteger Then rowCache.Visible = False". Because
    //   dbo.ModuleDefinitions.DefaultCacheTime is declared int NOT NULL with a stored default of 0
    //   (03.01.00 line 298), a value of -1 there is EXPLICITLY STORED to mean "caching is not applicable
    //   to this kind of module" - yet another negative number that is data rather than absence. That fact
    //   belongs to the definition, not to this instance, so it is carried by ModuleDefinitionDto. A
    //   client wishing to hide this field must consult the definition; this member is always populated
    //   with whatever the placement row stores.
    public int CacheTime { get; set; }

    /// <summary>
    /// The icon displayed with the module's title for this placement, mapped from
    /// <c>TabModules.IconFile</c> (<c>nvarchar(100) NULL</c>, 03.00.01 line 29). The legacy help text
    /// reads: "Select an Icon for this Module to display in the Title Bar".
    /// </summary>
    /// <remarks>
    /// <para>
    /// A STORED NAME OR RELATIVE PATH ONLY. This contract promises no resolvable address and performs no
    /// resolution: the legacy file-system subsystem is excluded from this migration, so the value is
    /// returned exactly as stored and any resolution is a client-side or rendering-side concern.
    /// </para>
    /// <para>
    /// The 100-character bound is the column's. Absence was represented as the EMPTY STRING rather than
    /// null, with the same mapper obligation described on <see cref="ModuleTitle"/>.
    /// </para>
    /// </remarks>
    public string? IconFile { get; set; }

    /// <summary>
    /// How this placement is presented on its page, mapped from <c>TabModules.Visibility</c>
    /// (<c>int NOT NULL</c>, 03.00.01 line 31). Legacy caption "Visibility:", whose help text reads:
    /// "Choose the default visibility for this Module".
    /// </summary>
    /// <remarks>
    /// <para>
    /// A per-placement fact: the column is declared on the placement table and nowhere else in the
    /// upgrade chain. Not nullable, with <see cref="ModuleVisibility.Maximized"/> (ordinal 0) as the
    /// default, which matches both the column and the legacy object's own initial state. The three
    /// ordinals persisted are exactly the three the legacy radio-button list offered, whose item values
    /// were 0, 1 and 2.
    /// </para>
    /// <para>
    /// <see cref="ModuleVisibility.None"/> MEANS "THE MODULE IS NOT RENDERED". It does NOT mean the value
    /// is missing, unset or yet to be chosen. This is the single most likely misreading of the
    /// enumeration: it is a deliberate display state an operator selected, and a consumer that treats it
    /// as "unknown" and substitutes a default will make a suppressed module visible. The enumeration
    /// offers no unknown member, no not-set member and no negative member, and none may be added.
    /// </para>
    /// </remarks>
    // MIGRATION: 5.3 - THE LEGACY READER COLLAPSED THREE DISTINCT STORED INPUTS INTO ONE OUTPUT AND HAD
    //   NO CATCH-ALL BRANCH. Library/Components/Modules/ModuleController.vb lines 81-85 select on the
    //   sentinel-substituted integer of the Visibility column with the cases "0, Null.NullInteger"
    //   yielding Maximized, "1" yielding Minimized and "2" yielding None. So a database null, a stored 0
    //   and a stored -1 ALL surfaced as Maximized. The target preserves that collapse rather than
    //   inventing a fourth state, which is exactly why this member is not nullable: there was never an
    //   observable difference between those three inputs at the contract boundary, so introducing one now
    //   would change behaviour.
    //
    // MIGRATION: 5.3 (legacy defect) - annotated and deliberately NOT repaired. That branch has no "Case
    //   Else", and neither does its mirror on the write path, so a stored value outside {-1, 0, 1, 2}
    //   falls through every branch and leaves the field at its type default of 0 - silently presenting an
    //   out-of-range row as Maximized instead of failing. The behaviour is preserved exactly as measured.
    //   A related legacy looseness matters for whoever writes the mapper: the read path assigned this
    //   enumeration straight into an integer-typed selected-index property
    //   (ModuleSettings.ascx.vb line 134), an implicit conversion that compiled only because the
    //   administration pages were built with strict type checking disabled. Every such coercion is made
    //   explicit in the target, and any difference in outcome is documented.
    public ModuleVisibility Visibility { get; set; }

    /// <summary>
    /// Whether the module's CONTAINER chrome is displayed around this placement, mapped from
    /// <c>TabModules.DisplayTitle</c> (<c>bit NOT NULL</c> with a stored default of <c>(1)</c>, 03.00.08
    /// line 156). Despite the column name, the authoritative legacy wording is "Display Container?".
    /// </summary>
    /// <remarks>
    /// <para>
    /// NOTE THE ASYMMETRY: <see langword="true"/> is the default of the COLUMN and of the legacy object,
    /// not of this contract. This member is a plain automatic property, so a default-constructed instance
    /// reports <see langword="false"/>. No initialiser is added, because one would make a
    /// default-constructed instance indistinguishable from one deliberately set to
    /// <see langword="true"/>. Populating this member from the stored value on every projection is
    /// therefore an obligation of the module mapper, never something to be left to a default.
    /// </para>
    /// </remarks>
    // MIGRATION: 5.9 - THE MEMBER NAME AND ITS MEANING DISAGREE, AND THE DISAGREEMENT IS INHERITED. The
    //   legacy markup labels the checkbox "Display Title?", but that inline text is only a fallback:
    //   Website/admin/Modules/App_LocalResources/ModuleSettings.ascx.resx overrides it to "Display
    //   Container?" with the help text "Select this option if you would like to display the Module
    //   container." The resource wording is authoritative because it is what an operator actually saw, so
    //   this flag governs the whole container chrome of which the title bar is merely one part. The
    //   schema column name is preserved verbatim for traceability - renaming it would sever the
    //   correspondence with the column and with every legacy call site. This is also why the meaning is
    //   worth documenting even though the container itself is excluded from this migration: the flag
    //   survives as stored state, while the skin object it once controlled does not.
    public bool DisplayTitle { get; set; }

    // ---------------------------------------------------------------------------------------------
    // GROUP D - CATALOGUE PROJECTIONS, READ-ONLY
    //
    // Facts about the module's DEFINITION (dbo.ModuleDefinitions) and its installed PACKAGE
    // (dbo.DesktopModules), carried inline so a client can name and describe the module without a second
    // request. NONE of them is writable through any request contract: they belong to the catalogue, not
    // to this instance. All four are nullable on the wire because each is the result of a join that may
    // not resolve, even where the underlying column is itself declared NOT NULL.
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// The definition's display name, projected read-only from
    /// <c>ModuleDefinitions.FriendlyName</c> (<c>nvarchar(128)</c>). Legacy caption "Module:", whose help
    /// text reads: "Displays the name of the module."
    /// </summary>
    /// <remarks>
    /// THE FIRST FIELD THE LEGACY SCREEN RENDERED, AND IT RENDERED IT READ-ONLY - into a text box marked
    /// <c>Enabled="False"</c>, assigned on load and never read back on save. That structural statement is
    /// reproduced here as a contract fact: no create or update request accepts this member. Nullable on
    /// the wire because the joined definition row may not resolve, a case the legacy layer surfaced as the
    /// empty string with the same mapper obligation described on <see cref="ModuleTitle"/>; the column
    /// itself is declared NOT NULL in its own table.
    /// </remarks>
    public string? FriendlyName { get; set; }

    /// <summary>
    /// The installed package's unique programmatic name, projected read-only from
    /// <c>DesktopModules.ModuleName</c>, which carries a <c>UNIQUE NONCLUSTERED</c> index
    /// (<c>IX_{objectQualifier}DesktopModules_ModuleName</c>).
    /// </summary>
    /// <remarks>
    /// LOAD-BEARING FOR TRANSFER, NOT MERELY DESCRIPTIVE. The legacy export composed its filename from a
    /// cleaned form of this name, and the legacy import validated a submitted filename against it, so the
    /// value participates in the export and import contracts rather than being cosmetic. Read-only and
    /// never accepted on a request: it identifies an installed package, and renaming one is an
    /// installation concern. Nullable on the wire for the join reason described on the group above; the
    /// column itself is declared NOT NULL in its own table and is additionally unique.
    /// </remarks>
    public string? ModuleName { get; set; }

    /// <summary>
    /// The human-readable description of the installed package, projected read-only from
    /// <c>DesktopModules.Description</c>.
    /// </summary>
    /// <remarks>
    /// Read-only catalogue metadata. Note carefully WHICH table this comes from: dbo.ModuleDefinitions
    /// once had a Description column of its own and it was DROPPED during the upgrade chain, so the
    /// surviving description belongs to the package and not to the definition. Nullable both because the
    /// join may not resolve and because the column itself permits a null.
    /// </remarks>
    public string? Description { get; set; }

    /// <summary>
    /// The installed package's version string, projected read-only from <c>DesktopModules.Version</c>.
    /// </summary>
    /// <remarks>
    /// Read-only catalogue metadata, and load-bearing for transfer in the same way as
    /// <see cref="ModuleName"/>: the legacy import passed a version string alongside the content and the
    /// operator identifier when handing content to a module's own portability implementation. Reported so
    /// a client can show which version of a package an instance is realising, and never accepted on a
    /// request. Nullable both because the join may not resolve and because the column itself permits a
    /// null.
    /// </remarks>
    public string? Version { get; set; }
}
