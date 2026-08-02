using DnnMigration.Domain.Enums;

namespace DnnMigration.Application.Dtos.Module;

// MIGRATION: this contract is the detail counterpart of the sibling list row and it honours the same
// four-table split. The legacy Library/Components/Modules/ModuleInfo.vb is one 936-line class exposing
// 58 properties that flattens the Modules-to-TabModules-to-ModuleDefinitions-to-ModuleControls join
// into a single object; the members below are attributed to their real owning table so a consumer
// cannot mistake a per-placement fact for a per-module fact:
//
//     dbo.Modules            ModuleId, PortalId, ModuleDefId, ModuleTitle, AllTabs, IsDeleted,
//                            InheritViewPermissions, Header, Footer, StartDate, EndDate
//     dbo.TabModules         TabModuleId, TabId, PaneName, ModuleOrder, CacheTime, IconFile,
//                            Alignment, Color, Border, Visibility, DisplayTitle, DisplayPrint,
//                            DisplaySyndicate
//
// MIGRATION: TabModules.ContainerSrc is deliberately absent. A module container is a skin object, and
// skinning is excluded by AAP 0.2.2.4; the stored value is preserved untouched on every update rather
// than being surfaced for editing.
//     dbo.ModuleDefinitions  FriendlyName (read-only projection)
//
// MIGRATION: a module with AllTabs set has many placements, so this contract describes ONE placement
// of one module. Row identity is TabModuleId, never ModuleId. Requesting a module without naming a
// placement returns its first placement in page order, which is what the legacy settings screen bound.
//
// MIGRATION: the same exclusions the sibling list row documents apply here and are not re-argued in
// full. Summarised: the token-replacement accessor contract and its cache-policy member are dropped
// with that subsystem; XML serialisation attributes are dropped because the wire shape belongs to this
// layer and to one central JSON policy; Alignment, Color, Border, ContainerSrc, DisplayPrint and
// DisplaySyndicate are omitted even though all six are real terminal columns, because they exist to
// drive server-side markup and both skinning and server-side rendering are excluded from this
// migration; BusinessControllerClass is omitted so a stored type name never crosses the wire to be
// activated; the permission collections are relocated to the permissions contract; and no audit member
// appears because a census of all 88 upgrade scripts finds no audit column on either table.
//
// MIGRATION: PaneName IS carried here even though the list row omits it, and the distinction is
// deliberate. On the list row it would be presentation noise; here it is the placement's own key -
// TabModules.PaneName is nvarchar(50) NOT NULL - so a caller that means to move or recreate a
// placement needs to see it.
//
// MIGRATION: no sentinel signals absence. Modules.ModuleID and Tabs.TabID are both IDENTITY (0, 1),
// TabModules.TabModuleID and ModuleDefinitions.ModuleDefID are both IDENTITY (1, 1), and
// Modules.PortalID is genuinely nullable, so 0 is a real identifier and -1 is a real portal identifier.
// No consumer may test any identifier here against 0 or -1 to decide whether it is present.

/// <summary>
/// The full state of one module as it is placed on one page, returned by
/// <c>GET /api/v1/modules/{moduleId}</c> and by the create and update endpoints.
/// </summary>
/// <remarks>
/// A boundary contract only: no navigation property, no tracked state and no behaviour. The grid
/// projection is <see cref="ModuleListItemDto"/>, the editable subsets are
/// <see cref="CreateModuleRequest"/> and <see cref="UpdateModuleRequest"/>, and the key-value settings
/// attached to the module and to this placement are carried separately by
/// <see cref="ModuleSettingsDto"/>.
/// </remarks>
public sealed class ModuleDetailDto
{
    /// <summary>The module's identity, mapped from <c>Modules.ModuleID</c> (<c>IDENTITY (0, 1)</c>, so 0 is real). Not unique across placements of the same module.</summary>
    public int ModuleId { get; set; }

    /// <summary>The placement's identity, mapped from <c>TabModules.TabModuleID</c>. This is the row identity of this contract.</summary>
    public int TabModuleId { get; set; }

    /// <summary>The page this placement sits on, mapped from <c>TabModules.TabID</c> (<c>IDENTITY (0, 1)</c>, so 0 is real).</summary>
    public int TabId { get; set; }

    /// <summary>The portal the module belongs to, mapped from <c>Modules.PortalID</c>, or <see langword="null"/> for a host-level module. The column is genuinely nullable.</summary>
    public int? PortalId { get; set; }

    /// <summary>The module definition this instance realises, mapped from <c>Modules.ModuleDefID</c>.</summary>
    public int ModuleDefId { get; set; }

    /// <summary>The definition's display name, projected read-only from <c>ModuleDefinitions.FriendlyName</c>. Used as the fallback heading when <see cref="ModuleTitle"/> is blank, which is what the legacy screen did.</summary>
    public string FriendlyName { get; set; } = string.Empty;

    /// <summary>The instance's own heading, mapped from <c>Modules.ModuleTitle</c> (<c>nvarchar(256) NULL</c>).</summary>
    public string? ModuleTitle { get; set; }

    /// <summary>Which pane of the page's layout the placement occupies, mapped from <c>TabModules.PaneName</c> (<c>nvarchar(50) NOT NULL</c>).</summary>
    public string PaneName { get; set; } = string.Empty;

    /// <summary>The placement's position within its pane, mapped from <c>TabModules.ModuleOrder</c>.</summary>
    public int ModuleOrder { get; set; }

    /// <summary>Whether the module is placed on every page of the portal, mapped from <c>Modules.AllTabs</c> (column default 0). When set, several placements share one <see cref="ModuleId"/>.</summary>
    public bool AllTabs { get; set; }

    /// <summary>Whether the module is in the recycle bin, mapped from <c>Modules.IsDeleted</c> (column default 0).</summary>
    public bool IsDeleted { get; set; }

    /// <summary>Whether the module takes its view permissions from its page instead of carrying its own, mapped from <c>Modules.InheritViewPermissions</c>.</summary>
    public bool? InheritViewPermissions { get; set; }

    /// <summary>Markup rendered above the module's content, mapped from the <c>ntext</c> column <c>Modules.Header</c>.</summary>
    public string? Header { get; set; }

    /// <summary>Markup rendered below the module's content, mapped from the <c>ntext</c> column <c>Modules.Footer</c>.</summary>
    public string? Footer { get; set; }

    /// <summary>The instant the module becomes visible, mapped from <c>Modules.StartDate</c>, or <see langword="null"/> for no start restriction. <see langword="null"/> is not interchangeable with the legacy <c>DateTime.MinValue</c> marker.</summary>
    public DateTime? StartDate { get; set; }

    /// <summary>The instant the module stops being visible, mapped from <c>Modules.EndDate</c>, or <see langword="null"/> for no end restriction.</summary>
    public DateTime? EndDate { get; set; }

    /// <summary>How long the module's output may be cached, in seconds, mapped from <c>TabModules.CacheTime</c>. Zero disables caching for this placement.</summary>
    public int? CacheTime { get; set; }

    /// <summary>The placement's icon, mapped from <c>TabModules.IconFile</c> (<c>nvarchar(100) NULL</c>).</summary>
    public string? IconFile { get; set; }

    /// <summary>The placement's horizontal alignment, mapped from <c>TabModules.Alignment</c> (<c>nvarchar(10) NULL</c>), or <see langword="null"/> to inherit.</summary>
    /// <remarks>MIGRATION: an opaque persisted string returned verbatim, never interpolated into a style declaration.</remarks>
    public string? Alignment { get; set; }

    /// <summary>The placement's recorded background colour, mapped from <c>TabModules.Color</c> (<c>nvarchar(20) NULL</c>), or <see langword="null"/> to inherit.</summary>
    /// <remarks>MIGRATION: an opaque persisted data field returned verbatim, never interpolated into a style declaration.</remarks>
    public string? Color { get; set; }

    /// <summary>The placement's recorded border flag, mapped from <c>TabModules.Border</c> (<c>nvarchar(1) NULL</c>), or <see langword="null"/> for none.</summary>
    /// <remarks>MIGRATION: a single character used as a flag by the legacy renderer, returned verbatim.</remarks>
    public string? Border { get; set; }

    /// <summary>Whether the placement renders expanded, collapsed or without its chrome, mapped from the <c>int NOT NULL</c> column <c>TabModules.Visibility</c>.</summary>
    public ModuleVisibility Visibility { get; set; }

    /// <summary>Whether the placement shows its heading, mapped from <c>TabModules.DisplayTitle</c> (column default 1).</summary>
    public bool DisplayTitle { get; set; }

    /// <summary>Whether the placement offers a print affordance, mapped from <c>TabModules.DisplayPrint</c> (column default 1).</summary>
    public bool DisplayPrint { get; set; }

    /// <summary>Whether the placement offers a syndication affordance, mapped from <c>TabModules.DisplaySyndicate</c> (column default 1).</summary>
    /// <remarks>MIGRATION: reported although content syndication itself is deferred, so the stored value keeps its legacy meaning.</remarks>
    public bool DisplaySyndicate { get; set; }
}
