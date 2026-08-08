using DnnMigration.Domain.Enums;

namespace DnnMigration.Application.Dtos.Module;

/// <summary>
/// One row of the module listing returned by <c>GET /api/v1/modules</c>: a single module instance as
/// it is placed on a page, reduced to the facts an administration grid displays. Carries no paging
/// metadata of its own, and no navigation property, tracked state or behaviour, so no domain entity is
/// exposed through it in either direction.
/// </summary>
/// <remarks>
/// Composed from three tables, with <see cref="TabModuleId"/> as the row key: seven members describe
/// the module itself, five describe ONE PLACEMENT of it on one page, and the display name and three
/// package members are read-only projections.
/// <para>
/// Sentinels are preserved, not normalised, and several collide with real data.
/// <see cref="ModuleId"/> and <see cref="TabId"/> are seeded from 0 while <see cref="TabModuleId"/>
/// and <see cref="ModuleDefId"/> are seeded from 1, and -1 is simultaneously a live
/// <see cref="ModuleOrder"/> instruction, a genuine portal identifier and an "any page" wildcard in
/// the legacy query surface. So never test an identifier here against 0 or -1 to decide whether it is
/// present, and never assume a nullable member's legacy representation was itself null.
/// </para>
/// </remarks>
public sealed class ModuleListItemDto
{
    /// <summary>
    /// The identity of the module itself, mapped from <c>Modules.ModuleID</c>
    /// (<c>int IDENTITY (0, 1) NOT NULL</c>, the table's primary key).
    /// </summary>
    /// <remarks>
    /// THE SEED IS 0, so 0 is a legitimate module identifier held by the first module ever created;
    /// a test of the form "identifier is less than or equal to zero" rejects a real row. This value
    /// is also NOT unique across a listing - a module displayed on every page contributes one row
    /// per placement, all sharing it - so use <see cref="TabModuleId"/> for row identity.
    /// </remarks>
    public int ModuleId { get; set; }

    /// <summary>
    /// The identity of THIS PLACEMENT of the module on a page, mapped from
    /// <c>TabModules.TabModuleID</c> (<c>int NOT NULL IDENTITY (1, 1)</c>, the placement table's
    /// primary key). This is the stable key for a row of the listing.
    /// </summary>
    /// <remarks>
    /// Its seed is 1, a different seed from <see cref="ModuleId"/> and <see cref="TabId"/>, which
    /// both start at 0, so no single "looks empty" numeric test is valid for any of them.
    /// </remarks>
    public int TabModuleId { get; set; }

    /// <summary>
    /// The page this placement belongs to, mapped from <c>TabModules.TabID</c>. The legacy
    /// interface called this a page, not a tab: the settings screen labelled its picker "Move To
    /// Page:".
    /// </summary>
    /// <remarks>
    /// The underlying <c>Tabs.TabID</c> is <c>int IDENTITY (0, 1) NOT NULL</c>, so 0 is a
    /// legitimate page identifier and must not be read as "no page". A value of this member is
    /// always a concrete page, because a row of this listing is always a real placement.
    /// </remarks>
    public int TabId { get; set; }

    /// <summary>
    /// The module definition this instance was created from, mapped from
    /// <c>Modules.ModuleDefID</c>.
    /// </summary>
    /// <remarks>
    /// The referenced <c>ModuleDefinitions.ModuleDefID</c> is <c>int IDENTITY (1, 1) NOT NULL</c>,
    /// so no boundary value on this member carries a second meaning.
    /// </remarks>
    public int ModuleDefId { get; set; }

    /// <summary>
    /// The installed module package behind the definition, projected read-only from
    /// <c>ModuleDefinitions.DesktopModuleID</c>.
    /// </summary>
    /// <remarks>
    /// ABSENT MEANS UNRESOLVED, AND ZERO IS NOT A VALUE THIS MEMBER CAN CARRY: the package's own
    /// key column is a plain <c>IDENTITY</c> seeding at one, and the terminal
    /// <c>ModuleDefinitions.DesktopModuleID</c> is declared <c>NOT NULL</c> with a foreign key onto
    /// it. Null here therefore means the join did not resolve, never "no package".
    /// </remarks>
    public int? DesktopModuleId { get; set; }

    /// <summary>
    /// The administrator-supplied title of this module instance, mapped from
    /// <c>Modules.ModuleTitle</c> (<c>nvarchar(256) NULL</c>). Legacy label "Title:".
    /// </summary>
    /// <remarks>
    /// Genuinely optional: the legacy screen declared no presence validator, so an untitled module
    /// is valid legacy state. The legacy representation of "no title" was the EMPTY STRING rather
    /// than null, so translating between the two is an explicit obligation of the module mapper and
    /// must not be left to serialisation - a silent conversion either way is an observable change
    /// of behaviour.
    /// </remarks>
    public string? ModuleTitle { get; set; }

    /// <summary>
    /// The display name of the module's definition - what the legacy settings screen showed for
    /// "Module:" - projected read-only from <c>ModuleDefinitions.FriendlyName</c>
    /// (<c>nvarchar(128)</c>).
    /// </summary>
    /// <remarks>
    /// Carried inline so a grid can name each row's module type without a second request, and NEVER
    /// WRITABLE: it belongs to the definition, not to this instance, and no create or update
    /// contract accepts it. Nullable because it is the result of a JOIN and so is absent whenever
    /// the joined definition row cannot be resolved - a case the legacy layer surfaced as the empty
    /// string, with the same mapper obligation as <see cref="ModuleTitle"/>.
    /// </remarks>
    public string? FriendlyName { get; set; }

    /// <summary>
    /// The installed package's name, projected read-only from <c>DesktopModules.ModuleName</c>
    /// (<c>nvarchar(128) NOT NULL</c>).
    /// </summary>
    /// <remarks>
    /// NEVER WRITABLE: it belongs to the installed package, and no create or update contract
    /// accepts it. Nullable because it is the result of a join TWO TABLES AWAY and is therefore
    /// absent whenever the definition or its package cannot be resolved, even though the column is
    /// <c>NOT NULL</c>.
    /// </remarks>
    public string? ModuleName { get; set; }

    /// <summary>
    /// The installed package's description, projected read-only from
    /// <c>DesktopModules.Description</c> (<c>nvarchar(2000) NULL</c>).
    /// </summary>
    /// <remarks>
    /// Nullable for two independent reasons that this contract does not distinguish: the join may
    /// not resolve, and the column itself permits a null.
    /// </remarks>
    public string? Description { get; set; }

    /// <summary>
    /// The installed package's version, projected read-only from <c>DesktopModules.Version</c>
    /// (<c>nvarchar(8) NULL</c>).
    /// </summary>
    /// <remarks>
    /// Carried as the OPAQUE STRING the column holds, never parsed into a version structure and
    /// never compared: its contents are whatever each package author wrote, so ordering or
    /// comparing it here would impose a grammar the store does not enforce.
    /// </remarks>
    public string? Version { get; set; }

    /// <summary>
    /// The module's position within its pane on the page, mapped from <c>TabModules.ModuleOrder</c>
    /// (<c>int NOT NULL</c>). Lower values render nearer the top of the pane.
    /// </summary>
    /// <remarks>
    /// A per-placement fact, so two placements of one module can legitimately sit at different
    /// positions on different pages. Non-nullable because -1 is a live instruction rather than an
    /// absent value.
    /// </remarks>
    // MIGRATION: ModuleOrder of -1 is a COMMAND meaning "append at the bottom of the pane", and it must
    // survive verbatim in both directions: the legacy update contract, the branch that consumes it and the
    // portal-template import path all establish that.
    public int ModuleOrder { get; set; }

    /// <summary>
    /// Whether this module is displayed in the same location on every page of the portal, mapped
    /// from <c>Modules.AllTabs</c> (<c>bit NOT NULL DEFAULT 0</c>). Legacy label "Display Module On
    /// All Pages?".
    /// </summary>
    /// <remarks>Never absent, so non-nullable.</remarks>
    public bool AllTabs { get; set; }

    /// <summary>
    /// How this placement is presented on its page, mapped from <c>TabModules.Visibility</c>
    /// (<c>int NOT NULL</c>). Legacy label "Visibility:".
    /// </summary>
    /// <remarks>
    /// A per-placement fact, non-nullable, defaulting to
    /// <para>
    /// <see cref="ModuleVisibility.None"/> means the module is NOT RENDERED - a display state an
    /// administrator chose, not a missing value.
    /// </para>
    /// </remarks>
    // MIGRATION: the legacy reader collapsed a database null, a stored 0 and a stored -1 into Maximized, and
    // that collapse is preserved rather than a fourth state invented - which is why this member is not
    // nullable, there being no observable difference at the contract boundary. The legacy branch had no
    // catch-all, so a stored value outside {-1, 0, 1, 2} still presents as Maximized; the defect is
    // preserved as measured.
    public ModuleVisibility Visibility { get; set; }

    /// <summary>
    /// Whether this module is in the soft-deleted state that the legacy recycle bin represented,
    /// mapped from <c>Modules.IsDeleted</c> (<c>bit NOT NULL</c> defaulting to 0, added at
    /// <c>02.00.00.SqlDataProvider</c>). A row with this flag set still exists and can be restored;
    /// it is not a tombstone.
    /// </summary>
    /// <remarks>
    /// Never absent, so non-nullable. Carried on the list row because a grid that silently mixed
    /// deleted and live modules, or hid deleted ones with no way to see them, would lose a legacy
    /// workflow.
    /// </remarks>
    // MIGRATION: the legacy settings screen could only ever CLEAR this flag - its update handler assigned
    // False unconditionally, so saving settings un-deleted the module as a side effect. This contract
    // reports the stored state faithfully in both directions and does not reproduce that side effect.
    public bool IsDeleted { get; set; }

    /// <summary>
    /// Whether the module's CONTAINER chrome is displayed around this placement, mapped from
    /// <c>TabModules.DisplayTitle</c> (<c>bit NOT NULL</c> defaulting to <c>(1)</c>, 03.00.08).
    /// Despite the column name the authoritative legacy wording is "Display Container?".
    /// </summary>
    /// <remarks>
    /// The member name and its meaning disagree, and the disagreement is inherited: the flag
    /// governs the whole container chrome, of which the title bar is one part.
    /// <para>
    /// Note the asymmetry: No initialiser is added, because it would make a default-constructed
    /// instance indistinguishable from one deliberately set to <c>true</c>.
    /// </para>
    /// </remarks>
    public bool DisplayTitle { get; set; }

    /// <summary>
    /// The date from which this module begins to be displayed, mapped from <c>Modules.StartDate</c>
    /// (<c>datetime NULL</c>); <c>null</c> when no start date is set. Legacy label "Start Date:".
    /// </summary>
    /// <remarks>
    /// Together with <see cref="EndDate"/> this pair GATES WHETHER THE MODULE RENDERS AT ALL, which
    /// is why the sentinel translation below is stated explicitly rather than left to a default
    /// conversion: getting it wrong does not corrupt a display value, it makes content appear or
    /// disappear.
    /// </remarks>
    // MIGRATION: the legacy absent-date sentinel is DateTime.MinValue, not a database null, and the
    // translation is an explicit obligation of the module mapper. Both directions are proven by the legacy
    // screen: the read path assigned the text box only "If Not Null.IsNull(objModule.StartDate)", so a
    // sentinel date rendered as an EMPTY box, and the write path wrote Null.NullDate - that is
    // DateTime.MinValue - for an empty box rather than a database null.
    public DateTime? StartDate { get; set; }

    /// <summary>
    /// The date after which this module stops being displayed, mapped from <c>Modules.EndDate</c>
    /// (<c>datetime NULL</c>); <c>null</c> when no end date is set. Legacy label "End Date:".
    /// </summary>
    /// <remarks>
    /// Subject to the identical sentinel treatment as <see cref="StartDate"/>, whose note carries
    /// the measured evidence for both. THE TWO DATES ARE ALSO INDEPENDENT: the legacy screen
    /// validated each only as a well-formed date and never enforced that the end date follows the
    /// start date, and that looseness is preserved.
    /// </remarks>
    public DateTime? EndDate { get; set; }

}
