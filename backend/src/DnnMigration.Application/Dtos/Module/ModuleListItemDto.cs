using DnnMigration.Domain.Enums;

namespace DnnMigration.Application.Dtos.Module;

/// <summary>
/// One row of the module listing returned by <c>GET /api/v1/modules</c>: a single module instance as it is
/// placed on a page, reduced to the facts an administration grid displays. Carries no paging metadata of
/// its own, and no navigation property, tracked state or behaviour, so no domain entity is exposed through
/// it in either direction.
/// </summary>
/// <remarks>
/// Sentinels are preserved, not normalised, and several collide with real data. <see cref="ModuleId"/> and
/// <see cref="TabId"/> are seeded from 0 while <see cref="TabModuleId"/> and <see cref="ModuleDefId"/> are
/// seeded from 1, and -1 is simultaneously a live <see cref="ModuleOrder"/> instruction, a genuine portal
/// identifier and an "any page" wildcard in the legacy query surface.
/// </remarks>
public sealed class ModuleListItemDto
{
    /// <summary>
    /// The identity of the module itself, mapped from <c>Modules.ModuleID</c> (<c>int IDENTITY (0, 1) NOT
    /// NULL</c>, the table's primary key).
    /// </summary>
    /// <remarks>
    /// THE SEED IS 0, so 0 is a legitimate module identifier held by the first module ever created; a test
    /// of the form "identifier is less than or equal to zero" rejects a real row. This value is also NOT
    /// unique across a listing - a module displayed on every page contributes one row per placement, all
    /// sharing it - so use <see cref="TabModuleId"/> for row identity.
    /// </remarks>
    public int ModuleId { get; set; }

    /// <summary>
    /// The identity of THIS PLACEMENT of the module on a page, mapped from <c>TabModules.TabModuleID</c>
    /// (<c>int NOT NULL IDENTITY (1, 1)</c>, the placement table's primary key). This is the stable key for
    /// a row of the listing.
    /// </summary>
    public int TabModuleId { get; set; }

    /// <summary>
    /// The page this placement belongs to, mapped from <c>TabModules.TabID</c>. The legacy interface called
    /// this a page, not a tab: the settings screen labelled its picker "Move To Page:".
    /// </summary>
    /// <remarks>
    /// The underlying <c>Tabs.TabID</c> is <c>int IDENTITY (0, 1) NOT NULL</c>, so 0 is a legitimate page
    /// identifier and must not be read as "no page". A value of this member is always a concrete page,
    /// because a row of this listing is always a real placement.
    /// </remarks>
    public int TabId { get; set; }

    /// <summary>
    /// The module definition this instance was created from, mapped from <c>Modules.ModuleDefID</c>.
    /// </summary>
    /// <remarks>
    /// The referenced <c>ModuleDefinitions.ModuleDefID</c> is <c>int IDENTITY (1, 1) NOT NULL</c>, so no
    /// boundary value on this member carries a second meaning.
    /// </remarks>
    public int ModuleDefId { get; set; }

    /// <summary>
    /// The installed module package behind the definition, projected read-only from
    /// <c>ModuleDefinitions.DesktopModuleID</c>.
    /// </summary>
    /// <remarks>
    /// ABSENT MEANS UNRESOLVED, AND ZERO IS NOT A VALUE THIS MEMBER CAN CARRY: the package's own key column
    /// is a plain <c>IDENTITY</c> seeding at one, and the terminal <c>ModuleDefinitions.DesktopModuleID</c>
    /// is declared <c>NOT NULL</c> with a foreign key onto it. Null here therefore means the join did not
    /// resolve, never "no package".
    /// </remarks>
    public int? DesktopModuleId { get; set; }

    /// <summary>
    /// The administrator-supplied title of this module instance, mapped from <c>Modules.ModuleTitle</c>
    /// (<c>nvarchar(256) NULL</c>). Legacy label "Title:".
    /// </summary>
    public string? ModuleTitle { get; set; }

    /// <summary>
    /// The display name of the module's definition - what the legacy settings screen showed for "Module:" -
    /// projected read-only from <c>ModuleDefinitions.FriendlyName</c> (<c>nvarchar(128)</c>).
    /// </summary>
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
    /// The installed package's description, projected read-only from <c>DesktopModules.Description</c>
    /// (<c>nvarchar(2000) NULL</c>).
    /// </summary>
    /// <remarks>
    /// Nullable for two independent reasons that this contract does not distinguish: the join may not
    /// resolve, and the column itself permits a null.
    /// </remarks>
    public string? Description { get; set; }

    /// <summary>
    /// The installed package's version, projected read-only from <c>DesktopModules.Version</c>
    /// (<c>nvarchar(8) NULL</c>).
    /// </summary>
    /// <remarks>
    /// Carried as the OPAQUE STRING the column holds, never parsed into a version structure and never
    /// compared: its contents are whatever each package author wrote, so ordering or comparing it here
    /// would impose a grammar the store does not enforce.
    /// </remarks>
    public string? Version { get; set; }

    /// <summary>
    /// The module's position within its pane on the page, mapped from <c>TabModules.ModuleOrder</c> (<c>int
    /// NOT NULL</c>). Lower values render nearer the top of the pane.
    /// </summary>
    public int ModuleOrder { get; set; }

    /// <summary>
    /// Whether this module is displayed in the same location on every page of the portal, mapped from
    /// <c>Modules.AllTabs</c> (<c>bit NOT NULL DEFAULT 0</c>). Legacy label "Display Module On All Pages?".
    /// </summary>
    public bool AllTabs { get; set; }

    /// <summary>
    /// How this placement is presented on its page, mapped from <c>TabModules.Visibility</c> (<c>int NOT
    /// NULL</c>). Legacy label "Visibility:".
    /// </summary>
    public ModuleVisibility Visibility { get; set; }

    /// <summary>
    /// Whether this module is in the soft-deleted state that the legacy recycle bin represented, mapped
    /// from <c>Modules.IsDeleted</c> (<c>bit NOT NULL</c> defaulting to 0, added at
    /// <c>02.00.00.SqlDataProvider</c>). A row with this flag set still exists and can be restored; it is
    /// not a tombstone.
    /// </summary>
    public bool IsDeleted { get; set; }

    /// <summary>
    /// Whether the module's CONTAINER chrome is displayed around this placement, mapped from
    /// <c>TabModules.DisplayTitle</c> (<c>bit NOT NULL</c> defaulting to <c>(1)</c>, 03.00.08). Despite the
    /// column name the authoritative legacy wording is "Display Container?".
    /// </summary>
    public bool DisplayTitle { get; set; }

    /// <summary>
    /// The date from which this module begins to be displayed, mapped from <c>Modules.StartDate</c>
    /// (<c>datetime NULL</c>); <c>null</c> when no start date is set. Legacy label "Start Date:".
    /// </summary>
    /// <remarks>
    /// Together with <see cref="EndDate"/> this pair GATES WHETHER THE MODULE RENDERS AT ALL, which is why
    /// the sentinel translation below is stated explicitly rather than left to a default conversion:
    /// getting it wrong does not corrupt a display value, it makes content appear or disappear.
    /// </remarks>
    public DateTime? StartDate { get; set; }

    /// <summary>
    /// The date after which this module stops being displayed, mapped from <c>Modules.EndDate</c>
    /// (<c>datetime NULL</c>); <c>null</c> when no end date is set. Legacy label "End Date:".
    /// </summary>
    public DateTime? EndDate { get; set; }
}
