using DnnMigration.Domain.Common;

namespace DnnMigration.Domain.Entities;

/// <summary>
/// A module instance owned by a portal: the content unit that one or more pages display.
/// </summary>
/// <remarks>
/// <para>
/// MIGRATION: the legacy 58-property <c>DotNetNuke.Entities.Modules.ModuleInfo</c>
/// (Library/Components/Modules/ModuleInfo.vb) was a flattened
/// <c>Modules</c> join <c>TabModules</c> join <c>ModuleDefinitions</c> join <c>ModuleControls</c>
/// projection. It is split along the real table boundaries; this entity carries only the columns
/// <c>dbo.Modules</c> still has after 02.00.00 and 03.00.01 removed the per-page and presentation
/// columns. Placement facts live on <see cref="TabModule"/>.
/// </para>
/// <para>
/// MIGRATION: <c>ModuleID</c> is <c>IDENTITY(0, 1)</c>, so 0 is a legitimate module identifier.
/// Code must never treat 0 as "absent".
/// </para>
/// </remarks>
public sealed class Module : Entity<int>
{
    /// <summary>Gets or sets the surrogate key (<c>ModuleID</c>, identity seeded at 0).</summary>
    public int ModuleId { get; set; }

    /// <inheritdoc />
    public override int Identity => ModuleId;

    /// <summary>Gets or sets the definition this instance was created from (<c>ModuleDefID</c>, required, cascade delete).</summary>
    public int ModuleDefinitionId { get; set; }

    /// <summary>
    /// Gets or sets the owning portal (<c>PortalID</c>, nullable: host-level modules belong to no
    /// portal), added by 03.00.01 when modules were lifted off pages.
    /// </summary>
    public int? PortalId { get; set; }

    /// <summary>Gets or sets the display title (<c>ModuleTitle</c>, 256 characters).</summary>
    public string? ModuleTitle { get; set; }

    /// <summary>Gets or sets whether the instance appears on every page of its portal (<c>AllTabs</c>, required, default false).</summary>
    public bool AllTabs { get; set; }

    /// <summary>Gets or sets the soft-delete flag that puts the instance in the recycle bin (<c>IsDeleted</c>, required, default false).</summary>
    public bool IsDeleted { get; set; }

    /// <summary>
    /// Gets or sets whether view permission is inherited from the page rather than granted per
    /// module (<c>InheritViewPermissions</c>, nullable).
    /// </summary>
    public bool? InheritViewPermissions { get; set; }

    /// <summary>Gets or sets markup rendered above the module content (<c>Header</c>, ntext).</summary>
    public string? Header { get; set; }

    /// <summary>Gets or sets markup rendered below the module content (<c>Footer</c>, ntext).</summary>
    public string? Footer { get; set; }

    /// <summary>Gets or sets the instant from which the instance is visible (<c>StartDate</c>).</summary>
    public DateTime? StartDate { get; set; }

    /// <summary>Gets or sets the instant after which the instance stops being visible (<c>EndDate</c>).</summary>
    public DateTime? EndDate { get; set; }

    /// <summary>Gets or sets the definition this instance was created from.</summary>
    public ModuleDefinition? ModuleDefinition { get; set; }

    /// <summary>Gets or sets the owning portal.</summary>
    public Portal? Portal { get; set; }

    /// <summary>Gets the placements of this instance on pages.</summary>
    public ICollection<TabModule> TabModules { get; } = new List<TabModule>();

    /// <summary>Gets the instance-wide settings.</summary>
    public ICollection<ModuleSetting> ModuleSettings { get; } = new List<ModuleSetting>();

    /// <summary>Gets the permission grants attached to this instance.</summary>
    public ICollection<ModulePermission> ModulePermissions { get; } = new List<ModulePermission>();
}
