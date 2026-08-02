using DnnMigration.Domain.Common;
using DnnMigration.Domain.Enums;

namespace DnnMigration.Domain.Entities;

/// <summary>
/// One placement of a module instance on one page, carrying every fact that varies per placement.
/// </summary>
/// <remarks>
/// MIGRATION: created by 03.00.01, which moved <c>PaneName</c>, <c>ModuleOrder</c>,
/// <c>CacheTime</c>, <c>Alignment</c>, <c>Color</c>, <c>Border</c>, <c>IconFile</c> and
/// <c>ContainerSrc</c> off <c>dbo.Modules</c> so that a single instance could appear on several
/// pages with different presentation. The unique index over <c>(TabID, ModuleID)</c> means an
/// instance appears at most once per page.
/// </remarks>
public sealed class TabModule : Entity<int>
{
    /// <summary>Gets or sets the surrogate key (<c>TabModuleID</c>, identity from 1).</summary>
    public int TabModuleId { get; set; }

    /// <inheritdoc />
    public override int Identity => TabModuleId;

    /// <summary>Gets or sets the page (<c>TabID</c>, required, cascade delete).</summary>
    public int TabId { get; set; }

    /// <summary>Gets or sets the module instance (<c>ModuleID</c>, required, cascade delete).</summary>
    public int ModuleId { get; set; }

    /// <summary>Gets or sets the skin pane that hosts the placement (<c>PaneName</c>, required, 50 characters).</summary>
    public string PaneName { get; set; } = string.Empty;

    /// <summary>Gets or sets the ordinal within the pane (<c>ModuleOrder</c>, required).</summary>
    public int ModuleOrder { get; set; }

    /// <summary>Gets or sets the output cache duration in minutes, 0 meaning uncached (<c>CacheTime</c>, required).</summary>
    public int CacheTime { get; set; }

    /// <summary>Gets or sets the legacy horizontal alignment token (<c>Alignment</c>, 10 characters).</summary>
    public string? Alignment { get; set; }

    /// <summary>Gets or sets the legacy background colour token (<c>Color</c>, 20 characters).</summary>
    public string? Color { get; set; }

    /// <summary>Gets or sets the legacy border width token (<c>Border</c>, 1 character).</summary>
    public string? Border { get; set; }

    /// <summary>Gets or sets the icon shown in the module header (<c>IconFile</c>, 100 characters).</summary>
    public string? IconFile { get; set; }

    /// <summary>Gets or sets the initial expand/collapse state (<c>Visibility</c>, required).</summary>
    public ModuleVisibility Visibility { get; set; }

    /// <summary>Gets or sets the container skin path (<c>ContainerSrc</c>, 200 characters).</summary>
    public string? ContainerSrc { get; set; }

    /// <summary>Gets or sets whether the module title is rendered (<c>DisplayTitle</c>, required, default true).</summary>
    public bool DisplayTitle { get; set; } = true;

    /// <summary>Gets or sets whether the print affordance is offered (<c>DisplayPrint</c>, required, default true).</summary>
    public bool DisplayPrint { get; set; } = true;

    /// <summary>Gets or sets whether the syndication affordance is offered (<c>DisplaySyndicate</c>, required, default true).</summary>
    public bool DisplaySyndicate { get; set; } = true;

    /// <summary>Gets or sets the page hosting the placement.</summary>
    public Tab? Tab { get; set; }

    /// <summary>Gets or sets the placed module instance.</summary>
    public Module? Module { get; set; }

    /// <summary>Gets the settings that apply to this placement only.</summary>
    public ICollection<TabModuleSetting> TabModuleSettings { get; } = new List<TabModuleSetting>();
}
