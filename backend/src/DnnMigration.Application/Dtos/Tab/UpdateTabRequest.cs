namespace DnnMigration.Application.Dtos.Tab;

// MIGRATION: the editable field set is taken from the legacy page-management screens
// Website/admin/Tabs/ManageTabs.ascx and Tabs.ascx together with their code-behind workflow, not from
// the Tabs table as a whole. The terminal Tabs table carries 22 columns after a 41-step ALTER chain;
// this request carries only those an administrator could actually edit on that screen and that remain
// meaningful once skinning and server-side rendering are out of scope.
//
// MIGRATION: SkinSrc and ContainerSrc are deliberately NOT updatable. Both are real terminal columns
// - nvarchar(200) NULL - and both are exposed read-only on the sibling detail contract so an existing
// value can be displayed and is never silently lost, but skinning and containers are excluded from
// this migration wholesale, so accepting a new value here would let a caller write configuration that
// nothing in the target stack honours.
//
// MIGRATION: TabPath and Level are not accepted either. Both are derived from the parent chain rather
// than authored: the legacy controller recomputed them whenever a page was moved. Accepting them
// would let a caller desynchronise the hierarchy from its own denormalised description, so the
// service recomputes both from ParentId.
//
// MIGRATION: IsDeleted is not accepted. Recycling a page is a distinct operation with its own
// workflow in Website/admin/Tabs/RecycleBin.ascx.vb, not a field on the edit form, and folding it
// into a general update would let a routine edit delete a page as a side effect.
//
// MIGRATION: an absent optional identifier is a null nullable, never a numeric sentinel. Tabs.TabID
// is IDENTITY (0, 1), so 0 is a real page identifier and a top-level page is expressed by ParentId
// being null rather than by 0 or -1.

/// <summary>
/// The editable state of one page, submitted to <c>PUT /api/v1/tabs/{tabId}</c>.
/// </summary>
/// <remarks>
/// <para>
/// A complete replacement of the editable subset rather than a patch: every member is applied as
/// supplied, so a caller reads the page first, changes what it means to change, and submits the whole
/// object. This mirrors the legacy screen, which posted its entire form.
/// </para>
/// <para>
/// Declarative validation lives in the application validation folder, not on this type as attributes.
/// The page identifier is taken from the route and is deliberately absent here, so a mismatched body
/// cannot redirect the write to a different page.
/// </para>
/// </remarks>
public sealed class UpdateTabRequest
{
    /// <summary>The page's name as it appears in navigation, mapped to <c>Tabs.TabName</c>. Required; the column is <c>nvarchar(50) NOT NULL</c>.</summary>
    public string TabName { get; set; } = string.Empty;

    /// <summary>The longer heading shown on the page itself, mapped to <c>Tabs.Title</c> (<c>nvarchar(200) NULL</c>).</summary>
    public string? Title { get; set; }

    /// <summary>The page description used in metadata, mapped to <c>Tabs.Description</c> (<c>nvarchar(500) NULL</c>).</summary>
    public string? Description { get; set; }

    /// <summary>The page keywords used in metadata, mapped to <c>Tabs.KeyWords</c> (<c>nvarchar(500) NULL</c>).</summary>
    public string? Keywords { get; set; }

    /// <summary>Whether the page appears in navigation, mapped to <c>Tabs.IsVisible</c>. Defaults to <see langword="true"/>, matching the column default of 1.</summary>
    public bool IsVisible { get; set; } = true;

    /// <summary>
    /// Whether the navigation entry is inert, mapped to <c>Tabs.DisableLink</c>.
    /// </summary>
    /// <remarks>Used for a heading that groups child pages without being navigable itself.</remarks>
    public bool DisableLink { get; set; }

    /// <summary>
    /// The parent page, or <see langword="null"/> for a page at the root of the hierarchy.
    /// </summary>
    /// <remarks>
    /// Changing this moves the page and every descendant. Because <c>Tabs.TabID</c> is seeded at 0, a
    /// value of 0 names a real parent page and must not be read as "no parent"; the service rejects a
    /// value that would make the page its own ancestor.
    /// </remarks>
    public int? ParentId { get; set; }

    /// <summary>The page's position among its siblings, mapped to <c>Tabs.TabOrder</c>.</summary>
    public int TabOrder { get; set; }

    /// <summary>The navigation icon, mapped to <c>Tabs.IconFile</c> (<c>nvarchar(100) NULL</c>).</summary>
    public string? IconFile { get; set; }

    /// <summary>
    /// An external or internal target this page redirects to, mapped to <c>Tabs.Url</c>
    /// (<c>nvarchar(255) NULL</c>), or <see langword="null"/> for an ordinary page.
    /// </summary>
    public string? Url { get; set; }

    /// <summary>The instant the page becomes available, mapped to <c>Tabs.StartDate</c>, or <see langword="null"/> for no start restriction.</summary>
    public DateTime? StartDate { get; set; }

    /// <summary>The instant the page stops being available, mapped to <c>Tabs.EndDate</c>, or <see langword="null"/> for no end restriction.</summary>
    public DateTime? EndDate { get; set; }

    /// <summary>The client refresh interval in seconds, mapped to <c>Tabs.RefreshInterval</c>, or <see langword="null"/> for no automatic refresh.</summary>
    public int? RefreshInterval { get; set; }

    /// <summary>Additional markup injected into the page head, mapped to <c>Tabs.PageHeadText</c> (<c>nvarchar(500) NULL</c>).</summary>
    public string? PageHeadText { get; set; }

    /// <summary>Whether the page must be served over a secure transport, mapped to <c>Tabs.IsSecure</c>. The column default is 0.</summary>
    public bool IsSecure { get; set; }
}
