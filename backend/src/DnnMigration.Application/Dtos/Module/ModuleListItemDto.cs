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
/// COMPOSED FROM THREE TABLES, AND <see cref="TabModuleId"/> IS THE ROW KEY. Seven members describe
/// the module itself and come from <c>dbo.Modules</c>; five describe ONE PLACEMENT of that module on
/// one page and come from <c>dbo.TabModules</c>; the display name and the three package members are
/// read-only projections. A module with <see cref="AllTabs"/> set therefore appears as SEVERAL ROWS -
/// one per placement, each with its own <see cref="TabModuleId"/>, <see cref="TabId"/> and
/// <see cref="ModuleOrder"/> - while sharing one <see cref="ModuleId"/>. A consumer that assumes a
/// single physical row behind this contract will mis-model that case.
/// </para>
/// <para>
/// SENTINELS ARE PRESERVED, NOT NORMALISED, AND SEVERAL COLLIDE WITH REAL DATA. The legacy layer
/// encoded absence in-band as -1 for integers, the empty string for text and
/// <see cref="System.DateTime.MinValue"/> for dates. Here <see cref="ModuleId"/> and
/// <see cref="TabId"/> are seeded from 0 while <see cref="TabModuleId"/> and
/// <see cref="ModuleDefId"/> are seeded from 1, and -1 is simultaneously a live
/// <see cref="ModuleOrder"/> instruction, a genuine portal identifier and an "any page" wildcard in
/// the legacy query surface. Two rules follow: never test an identifier on this type against 0 or -1
/// to decide whether it is present, and never assume a nullable member's legacy representation was
/// itself null. Each member documents which of its boundary values carry meaning.
/// </para>
/// <para>
/// SECURITY: <c>BusinessControllerClass</c> is deliberately omitted. The legacy code passed that
/// stored type name to a reflection-based activator at five call sites; the target resolves module
/// behaviour from a closed, dependency-injected set through a factory, so publishing an activatable
/// type name on the wire would invite arbitrary activation. Also absent: the portal identifier (the
/// request is already portal-scoped), the pane-layout, rendering, container and skinning members,
/// permission collections, the two intent flags that belong on an update request, the settings-screen
/// members, and definition metadata reachable through <see cref="ModuleDefinitionDto"/>. This type
/// carries no validation attribute: a list row is never submitted.
/// </para>
/// </remarks>
public sealed class ModuleListItemDto
{
    /// <summary>
    /// The identity of the module itself, mapped from <c>Modules.ModuleID</c>
    /// (<c>int IDENTITY (0, 1) NOT NULL</c>, the table's primary key, 01.00.00 line 221).
    /// </summary>
    /// <remarks>
    /// THE SEED IS 0, so 0 is a legitimate module identifier held by the first module ever created; a
    /// test of the form "identifier is less than or equal to zero" rejects a real row. This value is also
    /// NOT unique across a listing - a module displayed on every page contributes one row per placement,
    /// all sharing it - so use <see cref="TabModuleId"/> for row identity.
    /// </remarks>
    public int ModuleId { get; set; }

    /// <summary>
    /// The identity of THIS PLACEMENT of the module on a page, mapped from
    /// <c>TabModules.TabModuleID</c> (<c>int NOT NULL IDENTITY (1, 1)</c>, the placement table's
    /// primary key, 03.00.01 lines 19-21). This is the stable key for a row of the listing.
    /// </summary>
    /// <remarks>
    /// Its seed is 1, a different seed from <see cref="ModuleId"/> and <see cref="TabId"/>, which both
    /// start at 0, so no single "looks empty" numeric test is valid for any of them. This identifier is
    /// also the key under which placement-scoped settings are stored.
    /// </remarks>
    public int TabModuleId { get; set; }

    /// <summary>
    /// The page this placement belongs to, mapped from <c>TabModules.TabID</c>. The legacy interface
    /// called this a page, not a tab: the settings screen labelled its picker "Move To Page:".
    /// </summary>
    /// <remarks>
    /// The underlying <c>Tabs.TabID</c> is <c>int IDENTITY (0, 1) NOT NULL</c>, so 0 is a legitimate page
    /// identifier and must not be read as "no page". A sharper trap surrounds -1: in the legacy QUERY
    /// surface, -1 in a page position was an "ANY PAGE" WILDCARD rather than an absent value
    /// (<c>ModuleController.vb</c> L1223-L1225). A value of this member is always a concrete page,
    /// because a row of this listing is always a real placement.
    /// </remarks>
    public int TabId { get; set; }

    /// <summary>
    /// The module definition this instance was created from, mapped from <c>Modules.ModuleDefID</c>.
    /// </summary>
    /// <remarks>
    /// The referenced <c>ModuleDefinitions.ModuleDefID</c> is <c>int IDENTITY (1, 1) NOT NULL</c>, so no
    /// boundary value on this member carries a second meaning. The five-value catalogue set travels inline
    /// so a grid can name each row's module type and package without a second request per row.
    /// </remarks>
    public int ModuleDefId { get; set; }

    /// <summary>
    /// The installed module package behind the definition, projected read-only from
    /// <c>ModuleDefinitions.DesktopModuleID</c>.
    /// </summary>
    /// <remarks>
    /// ABSENT MEANS UNRESOLVED, AND ZERO IS NOT A VALUE THIS MEMBER CAN CARRY: the package's own key
    /// column is a plain <c>IDENTITY</c> seeding at one, and the terminal
    /// <c>ModuleDefinitions.DesktopModuleID</c> is declared <c>NOT NULL</c> with a foreign key onto it.
    /// Null here therefore means the join did not resolve, never "no package".
    /// </remarks>
    public int? DesktopModuleId { get; set; }

    /// <summary>
    /// The administrator-supplied title of this module instance, mapped from
    /// <c>Modules.ModuleTitle</c> (<c>nvarchar(256) NULL</c>, 01.00.00 line 226). Legacy label
    /// "Title:".
    /// </summary>
    /// <remarks>
    /// <para>
    /// GENUINELY OPTIONAL - the legacy settings screen declares no presence validator on this field, so a
    /// module with no title is a valid legacy state rather than corrupt data.
    /// </para>
    /// <para>
    /// The legacy representation of "no title" was the EMPTY STRING, not null: the legacy reader applied
    /// the absent-string constant to this column on every read, so a database null and an empty string
    /// were indistinguishable once loaded. This contract uses null for absence, which makes them
    /// distinguishable again. Translating between the two is an explicit obligation of the hand-written
    /// module mapper and must not be left to serialisation, because a silent conversion in either
    /// direction is an observable change of behaviour.
    /// </para>
    /// </remarks>
    public string? ModuleTitle { get; set; }

    /// <summary>
    /// The display name of the module's definition - what the legacy settings screen showed for
    /// "Module:" - projected read-only from <c>ModuleDefinitions.FriendlyName</c>
    /// (<c>nvarchar(128)</c>).
    /// </summary>
    /// <remarks>
    /// Carried inline so a grid can name each row's module type without a second request, and NEVER
    /// WRITABLE: it belongs to the definition, not to this instance, and no create or update contract
    /// accepts it. Nullable because it is the result of a JOIN and so is absent whenever the joined
    /// definition row cannot be resolved - a case the legacy layer surfaced as the empty string, with the
    /// same mapper obligation as <see cref="ModuleTitle"/>.
    /// </remarks>
    public string? FriendlyName { get; set; }

    /// <summary>
    /// The installed package's name, projected read-only from <c>DesktopModules.ModuleName</c>
    /// (<c>nvarchar(128) NOT NULL</c>).
    /// </summary>
    /// <remarks>
    /// NEVER WRITABLE: it belongs to the installed package, and no create or update contract accepts it.
    /// Nullable because it is the result of a join TWO TABLES AWAY and is therefore absent whenever the
    /// definition or its package cannot be resolved, even though the column is <c>NOT NULL</c>.
    /// </remarks>
    public string? ModuleName { get; set; }

    /// <summary>
    /// The installed package's description, projected read-only from
    /// <c>DesktopModules.Description</c> (<c>nvarchar(2000) NULL</c>).
    /// </summary>
    /// <remarks>
    /// Nullable for two independent reasons that this contract does not distinguish: the join may not
    /// resolve, and the column itself permits a null. Both mean the same thing to a caller - there is no
    /// description to show.
    /// </remarks>
    public string? Description { get; set; }

    /// <summary>
    /// The installed package's version, projected read-only from <c>DesktopModules.Version</c>
    /// (<c>nvarchar(8) NULL</c>).
    /// </summary>
    /// <remarks>
    /// Carried as the OPAQUE STRING the column holds, never parsed into a version structure and never
    /// compared: its contents are whatever each package author wrote, so ordering or comparing it here
    /// would impose a grammar the store does not enforce. Nullable for the same two reasons as
    /// <see cref="Description"/>.
    /// </remarks>
    public string? Version { get; set; }

    /// <summary>
    /// The module's position within its pane on the page, mapped from
    /// <c>TabModules.ModuleOrder</c> (<c>int NOT NULL</c>, 03.00.01 line 25). Lower values render
    /// nearer the top of the pane.
    /// </summary>
    /// <remarks>
    /// A per-placement fact, so two placements of one module can legitimately sit at different positions
    /// on different pages. Non-nullable because -1 is a live instruction rather than an absent value.
    /// </remarks>
    // MIGRATION: ModuleOrder of -1 is a COMMAND meaning "append at the bottom of the pane", and it must
    // survive verbatim in both directions - the documented contract of UpdateModuleOrder
    // (ModuleController.vb L1155), the branch that consumes it (L667-L669) and the portal-template import
    // path (L296) all establish it. The hazard is that the legacy absent-integer constant is ALSO -1, so
    // a mapping that mechanically converted the sentinel to null here would silently destroy the append
    // instruction.
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
    /// A per-placement fact, non-nullable, with <see cref="ModuleVisibility.Maximized"/> (ordinal 0) as
    /// the default, matching both the column and the legacy constructor. The three persisted ordinals are
    /// exactly the three the legacy radio button list offered.
    /// </para>
    /// <para>
    /// <see cref="ModuleVisibility.None"/> MEANS "THE MODULE IS NOT RENDERED". It does not mean the value
    /// is missing or has yet to be chosen - it is a deliberate display state an administrator selected,
    /// and a consumer that treats it as "unset" and substitutes a default will make a suppressed module
    /// visible. The type offers no "unknown" member and none may be added.
    /// </para>
    /// </remarks>
    // MIGRATION: the legacy reader collapsed three distinct stored inputs into one output - a database
    // null, a stored 0 and a stored -1 all surfaced as Maximized (ModuleController.FillModuleInfo
    // L81-L85). The target preserves that collapse rather than inventing a fourth state, which is why
    // this member is not nullable: there was never an observable difference at the contract boundary. The
    // legacy branch has no catch-all case on either the read or the write path, so a stored value outside
    // {-1, 0, 1, 2} silently presents as Maximized; that defect is preserved as measured.
    public ModuleVisibility Visibility { get; set; }

    /// <summary>
    /// Whether this module is in the soft-deleted state that the legacy recycle bin represented,
    /// mapped from <c>Modules.IsDeleted</c> (<c>bit NOT NULL</c> defaulting to 0, 02.00.00 line
    /// 6568). A row with this flag set still exists and can be restored; it is not a tombstone.
    /// </summary>
    /// <remarks>
    /// Never absent, so non-nullable. Carried on the list row because a grid that silently mixed deleted
    /// and live modules, or hid deleted ones with no way to see them, would lose a workflow the legacy
    /// application supported.
    /// </remarks>
    // MIGRATION: the legacy settings screen could only ever CLEAR this flag, never set it - its update
    // handler held the bare unconditional assignment "objModule.IsDeleted = False", so saving settings
    // always un-deleted the module as a side effect. This contract reports the stored state faithfully in
    // both directions and does not reproduce that side effect.
    public bool IsDeleted { get; set; }

    /// <summary>
    /// Whether the module's CONTAINER chrome is displayed around this placement, mapped from
    /// <c>TabModules.DisplayTitle</c> (<c>bit NOT NULL</c> defaulting to <c>(1)</c>, 03.00.08 line
    /// 156). Despite the column name the authoritative legacy wording is "Display Container?".
    /// </summary>
    /// <remarks>
    /// <para>
    /// THE MEMBER NAME AND ITS MEANING DISAGREE, AND THE DISAGREEMENT IS INHERITED. The legacy markup
    /// labels the checkbox "Display Title?", but the resource file overrides it to "Display Container?"
    /// with help text about the module container - and the resource wording is authoritative, because it
    /// is what an administrator saw. The flag governs the whole container chrome, of which the title bar
    /// is one part. The schema column name is preserved verbatim for traceability.
    /// </para>
    /// <para>
    /// NOTE THE ASYMMETRY: <c>true</c> is the default of the COLUMN and of the legacy object, not of this
    /// contract. This is a plain automatic property, so an unpopulated instance reports <c>false</c>. No
    /// initialiser is added, because one would make a default-constructed instance indistinguishable from
    /// one deliberately set to <c>true</c>; populating it from the stored value is the mapper's
    /// obligation.
    /// </para>
    /// </remarks>
    public bool DisplayTitle { get; set; }

    /// <summary>
    /// The date from which this module begins to be displayed, mapped from
    /// <c>Modules.StartDate</c> (<c>datetime NULL</c>, 02.02.00 line 324); <c>null</c> when no start
    /// date is set. Legacy label "Start Date:".
    /// </summary>
    /// <remarks>
    /// Together with <see cref="EndDate"/> this pair GATES WHETHER THE MODULE RENDERS AT ALL, which is why
    /// the sentinel translation below is stated explicitly rather than left to a default conversion:
    /// getting it wrong does not corrupt a display value, it makes content appear or disappear.
    /// </remarks>
    // MIGRATION: the legacy absent-date sentinel is DateTime.MinValue, not a database null, and the
    // translation is an explicit obligation of the module mapper. Both directions are proven by the legacy
    // screen: the read path assigned the text box only "If Not Null.IsNull(objModule.StartDate)", so a
    // sentinel date rendered as an EMPTY box, and the write path wrote Null.NullDate - that is
    // DateTime.MinValue - for an empty box rather than a database null. The mapper must convert MinValue
    // to null when reading and null to MinValue when writing: letting MinValue through would emit the year
    // 1 as a real start date, and treating a genuine stored date as absent would suppress a module that
    // should render.
    public DateTime? StartDate { get; set; }

    /// <summary>
    /// The date after which this module stops being displayed, mapped from <c>Modules.EndDate</c>
    /// (<c>datetime NULL</c>, 02.02.00 line 325); <c>null</c> when no end date is set. Legacy label
    /// "End Date:".
    /// </summary>
    /// <remarks>
    /// Subject to the identical sentinel treatment as <see cref="StartDate"/>, whose note carries the
    /// measured evidence for both. This member is the more dangerous of the two: an unconverted
    /// <c>DateTime.MinValue</c> in an END date states that the module stopped being displayed in the year
    /// 1, which would suppress every module carrying no end date - the common case. THE TWO DATES ARE ALSO
    /// INDEPENDENT: the legacy screen validated each only as a well-formed date and never enforced that
    /// the end date follows the start date, and that looseness is preserved.
    /// </remarks>
    public DateTime? EndDate { get; set; }

}
