using DnnMigration.Domain.Enums;

namespace DnnMigration.Application.Dtos.Module;

// MIGRATION: the two key-value dictionaries below are the genuine settings stores, and they are kept
// separate because the schema keeps them separate and their scopes differ materially. dbo.ModuleSettings
// has the composite primary key (ModuleID, SettingName) with SettingValue nvarchar(256) NOT NULL and is
// altered 13 times across the upgrade chain; dbo.TabModuleSettings has (TabModuleID, SettingName) with
// SettingValue nvarchar(2000) NOT NULL - note the far larger value column - and is altered 8 times. A
// module setting is shared by every placement of the module; a placement setting belongs to one
// placement only. Merging them would make an AllTabs module's per-page configuration collapse.
//
// MIGRATION: the legacy layer exchanged both stores as an untyped hashtable, materialised by the
// framework's reflection-driven hydrator. Dictionaries of string to string replace that: the values are
// genuinely free-form text written by whichever module owns the setting, so no stronger typing is
// available or honest, but the untyped collection type and the hydrator are both gone.
//
// MIGRATION: the settings screen's own fields are repeated here rather than referenced, because this is
// the contract the settings screen binds and a second round trip to the detail endpoint to render one
// form would be a regression against the legacy single-postback screen. The values are the same stored
// columns the detail contract exposes; nothing is duplicated in the database.
//
// MIGRATION: no module ever receives a setting it did not write. The legacy per-module settings screens
// were supplied by each module's own control, which is outside the scope of this migration; these two
// dictionaries carry that data verbatim so an existing module's configuration survives untouched even
// though no target screen interprets it.

/// <summary>
/// The configuration of one module placement, returned by
/// <c>GET /api/v1/modules/{moduleId}/settings</c>: the placement's own fields together with the
/// key-value settings recorded against the module and against the placement.
/// </summary>
/// <remarks>
/// A boundary contract only: no navigation property, no tracked state and no behaviour. Writes go
/// through <see cref="UpdateModuleRequest"/> for the fields and through the settings endpoint for the
/// dictionaries, so a read of this type is never submitted back unchanged.
/// </remarks>
public sealed class ModuleSettingsDto
{
    /// <summary>The module the settings belong to, mapped from <c>Modules.ModuleID</c>.</summary>
    public int ModuleId { get; set; }

    /// <summary>The placement the placement-scoped settings belong to, mapped from <c>TabModules.TabModuleID</c>.</summary>
    public int TabModuleId { get; set; }

    /// <summary>The instance's heading, mapped from <c>Modules.ModuleTitle</c>.</summary>
    public string? ModuleTitle { get; set; }

    /// <summary>Whether the module is placed on every page of the portal, mapped from <c>Modules.AllTabs</c>.</summary>
    public bool AllTabs { get; set; }

    /// <summary>Whether the module takes its view permissions from its page, mapped from <c>Modules.InheritViewPermissions</c>.</summary>
    public bool? InheritViewPermissions { get; set; }

    /// <summary>The instant the module becomes visible, mapped from <c>Modules.StartDate</c>, or <see langword="null"/> for no restriction.</summary>
    public DateTime? StartDate { get; set; }

    /// <summary>The instant the module stops being visible, mapped from <c>Modules.EndDate</c>, or <see langword="null"/> for no restriction.</summary>
    public DateTime? EndDate { get; set; }

    /// <summary>Markup rendered above the module's content, mapped from <c>Modules.Header</c>.</summary>
    public string? Header { get; set; }

    /// <summary>Markup rendered below the module's content, mapped from <c>Modules.Footer</c>.</summary>
    public string? Footer { get; set; }

    /// <summary>How long the placement's output may be cached, in seconds, mapped from <c>TabModules.CacheTime</c>.</summary>
    public int? CacheTime { get; set; }

    /// <summary>The placement's icon, mapped from <c>TabModules.IconFile</c>.</summary>
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

    /// <summary>Whether the placement renders expanded, collapsed or without its chrome, mapped from <c>TabModules.Visibility</c>.</summary>
    public ModuleVisibility Visibility { get; set; }

    /// <summary>Whether the placement shows its heading, mapped from <c>TabModules.DisplayTitle</c>.</summary>
    public bool DisplayTitle { get; set; }

    /// <summary>Whether the placement offers a print affordance, mapped from <c>TabModules.DisplayPrint</c> (column default 1).</summary>
    public bool DisplayPrint { get; set; }

    /// <summary>Whether the placement offers a syndication affordance, mapped from <c>TabModules.DisplaySyndicate</c> (column default 1).</summary>
    /// <remarks>MIGRATION: reported although content syndication itself is deferred, so the stored value keeps its legacy meaning.</remarks>
    public bool DisplaySyndicate { get; set; }

    /// <summary>
    /// Settings recorded against the module and therefore shared by every placement of it, from
    /// <c>dbo.ModuleSettings</c>. Keys are at most 50 characters and values at most 256.
    /// </summary>
    public IReadOnlyDictionary<string, string> ModuleSettings { get; set; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Settings recorded against this placement alone, from <c>dbo.TabModuleSettings</c>. Keys are at
    /// most 50 characters and values at most 2000.
    /// </summary>
    public IReadOnlyDictionary<string, string> TabModuleSettings { get; set; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
}
