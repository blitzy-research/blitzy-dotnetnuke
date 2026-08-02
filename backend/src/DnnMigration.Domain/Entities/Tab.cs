using DnnMigration.Domain.Common;

namespace DnnMigration.Domain.Entities;

/// <summary>
/// A page in a portal's navigation hierarchy.
/// </summary>
/// <remarks>
/// <para>
/// MIGRATION: replaces the 36-property <c>DotNetNuke.Entities.Tabs.TabInfo</c>
/// (Library/Components/Tabs/TabInfo.vb, 616 lines). <c>Implements IPropertyAccess</c> is dropped
/// with the excluded token-replacement subsystem. Bound to <c>dbo.Tabs</c> after its 41-step ALTER
/// chain; the dropped mobile and pane-width columns are deliberately absent.
/// </para>
/// <para>
/// MIGRATION: <c>TabID</c> is <c>IDENTITY(0, 1)</c>, so 0 is a real page identifier and must not be
/// read as "no page". <see cref="ParentId"/> is the self-reference that forms the hierarchy;
/// <see cref="Level"/> and <see cref="TabPath"/> are denormalised copies of that hierarchy which
/// the legacy code maintained on write, and they are preserved rather than recomputed.
/// </para>
/// </remarks>
public sealed class Tab : Entity<int>
{
    /// <summary>Gets or sets the surrogate key (<c>TabID</c>, identity seeded at 0).</summary>
    public int TabId { get; set; }

    /// <inheritdoc />
    public override int Identity => TabId;

    /// <summary>Gets or sets the ordinal among siblings (<c>TabOrder</c>, required, default 0).</summary>
    public int TabOrder { get; set; }

    /// <summary>Gets or sets the owning portal (<c>PortalID</c>, nullable: host pages belong to none), cascade delete.</summary>
    public int? PortalId { get; set; }

    /// <summary>Gets or sets the navigation name (<c>TabName</c>, required, 50 characters).</summary>
    public string TabName { get; set; } = string.Empty;

    /// <summary>Gets or sets whether the page appears in navigation (<c>IsVisible</c>, required, default true).</summary>
    public bool IsVisible { get; set; } = true;

    /// <summary>Gets or sets the parent page (<c>ParentId</c>, nullable for a root page).</summary>
    public int? ParentId { get; set; }

    /// <summary>Gets or sets the depth in the hierarchy, 0 for a root page (<c>Level</c>, required, default 0).</summary>
    public int Level { get; set; }

    /// <summary>Gets or sets the navigation icon (<c>IconFile</c>, 100 characters).</summary>
    public string? IconFile { get; set; }

    /// <summary>Gets or sets whether the navigation entry is inert (<c>DisableLink</c>, required, default false).</summary>
    public bool DisableLink { get; set; }

    /// <summary>Gets or sets the browser title (<c>Title</c>, 200 characters).</summary>
    public string? Title { get; set; }

    /// <summary>Gets or sets the meta description (<c>Description</c>, 500 characters).</summary>
    public string? Description { get; set; }

    /// <summary>Gets or sets the meta keywords (<c>KeyWords</c>, 500 characters).</summary>
    public string? KeyWords { get; set; }

    /// <summary>Gets or sets the soft-delete flag that puts the page in the recycle bin (<c>IsDeleted</c>, required, default false).</summary>
    public bool IsDeleted { get; set; }

    /// <summary>Gets or sets the redirect target for a link page (<c>Url</c>, 255 characters).</summary>
    public string? Url { get; set; }

    /// <summary>Gets or sets the page skin path (<c>SkinSrc</c>, 200 characters).</summary>
    public string? SkinSrc { get; set; }

    /// <summary>Gets or sets the default container skin path for modules on this page (<c>ContainerSrc</c>, 200 characters).</summary>
    public string? ContainerSrc { get; set; }

    /// <summary>Gets or sets the denormalised hierarchy path, for example <c>//Home//News</c> (<c>TabPath</c>, 255 characters).</summary>
    public string? TabPath { get; set; }

    /// <summary>Gets or sets the instant from which the page is available (<c>StartDate</c>).</summary>
    public DateTime? StartDate { get; set; }

    /// <summary>Gets or sets the instant after which the page stops being available (<c>EndDate</c>).</summary>
    public DateTime? EndDate { get; set; }

    /// <summary>Gets or sets the client refresh interval in seconds (<c>RefreshInterval</c>).</summary>
    public int? RefreshInterval { get; set; }

    /// <summary>Gets or sets extra markup injected into the page head (<c>PageHeadText</c>, 500 characters).</summary>
    public string? PageHeadText { get; set; }

    /// <summary>Gets or sets whether the page must be served over HTTPS (<c>IsSecure</c>, required, default false), added by 04.05.04.</summary>
    public bool IsSecure { get; set; }

    /// <summary>Gets or sets the owning portal.</summary>
    public Portal? Portal { get; set; }

    /// <summary>Gets or sets the parent page.</summary>
    public Tab? Parent { get; set; }

    /// <summary>Gets the immediate child pages.</summary>
    public ICollection<Tab> Children { get; } = new List<Tab>();

    /// <summary>Gets the module placements on this page.</summary>
    public ICollection<TabModule> TabModules { get; } = new List<TabModule>();

    /// <summary>Gets the permission grants attached to this page.</summary>
    public ICollection<TabPermission> TabPermissions { get; } = new List<TabPermission>();
}
