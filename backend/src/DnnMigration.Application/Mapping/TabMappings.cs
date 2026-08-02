using DnnMigration.Application.Dtos.Tab;
using DnnMigration.Domain.Entities;

namespace DnnMigration.Application.Mapping;

/// <summary>
/// Hand-written projections between the <see cref="Tab"/> aggregate and the page transfer contracts.
/// </summary>
/// <remarks>
/// <para>
/// MIGRATION: the legacy <c>TabInfo</c> class carried thirty-six properties and additionally
/// implemented a property-accessor contract so that the token-replacement subsystem could read it by
/// name. That subsystem is out of scope, so no name-keyed accessor is reproduced and every projection
/// below is a named assignment.
/// </para>
/// <para>
/// The spelling difference between the stored column and the wire contract is deliberate and is
/// resolved here, in one place: the column, and therefore the entity property, is <c>KeyWords</c>
/// with a capital <c>W</c>, while the transfer contract spells it <c>Keywords</c>. Neither side is
/// renamed to match the other - the column name is fixed by the shipped schema and the wire name is
/// fixed by the published contract - so the mapping absorbs the difference.
/// </para>
/// <para>
/// Whether a page has children is not stored against the page. It is computed once for a whole set by
/// reading the distinct parent identifiers, and is therefore supplied to these projections as an
/// argument rather than derived from a navigation that a list read would not have loaded.
/// </para>
/// </remarks>
public static class TabMappings
{
    /// <summary>
    /// Projects a page onto the row shape the page list renders.
    /// </summary>
    /// <param name="tab">The page to project.</param>
    /// <param name="hasChildren">Whether any page names this one as its parent.</param>
    /// <returns>The list row.</returns>
    public static TabListItemDto ToListItem(Tab tab, bool hasChildren)
    {
        ArgumentNullException.ThrowIfNull(tab);

        return new TabListItemDto
        {
            TabId = tab.TabId,
            TabName = tab.TabName,
            Title = tab.Title,
            TabOrder = tab.TabOrder,
            ParentId = tab.ParentId,
            Level = tab.Level,
            TabPath = tab.TabPath,
            IsVisible = tab.IsVisible,
            DisableLink = tab.DisableLink,
            IsDeleted = tab.IsDeleted,
            HasChildren = hasChildren,
            IsSecure = tab.IsSecure,
            Url = tab.Url,
            IconFile = tab.IconFile,
        };
    }

    /// <summary>
    /// Projects a page onto the full detail contract.
    /// </summary>
    /// <param name="tab">The page to project.</param>
    /// <param name="hasChildren">Whether any page names this one as its parent.</param>
    /// <returns>The detail contract.</returns>
    public static TabDetailDto ToDetail(Tab tab, bool hasChildren)
    {
        ArgumentNullException.ThrowIfNull(tab);

        return new TabDetailDto
        {
            TabId = tab.TabId,
            TabOrder = tab.TabOrder,
            PortalId = tab.PortalId,
            TabName = tab.TabName,
            IsVisible = tab.IsVisible,
            ParentId = tab.ParentId,
            Level = tab.Level,
            IconFile = tab.IconFile,
            DisableLink = tab.DisableLink,
            Title = tab.Title,
            Description = tab.Description,
            Keywords = tab.KeyWords,
            IsDeleted = tab.IsDeleted,
            Url = tab.Url,
            SkinSrc = tab.SkinSrc,
            ContainerSrc = tab.ContainerSrc,
            TabPath = tab.TabPath,
            StartDate = tab.StartDate,
            EndDate = tab.EndDate,
            RefreshInterval = tab.RefreshInterval,
            PageHeadText = tab.PageHeadText,
            IsSecure = tab.IsSecure,
            HasChildren = hasChildren,
        };
    }

    /// <summary>
    /// Applies a submitted update to a tracked page aggregate.
    /// </summary>
    /// <param name="tab">The tracked page to modify.</param>
    /// <param name="request">The submitted values.</param>
    /// <remarks>
    /// Five stored members are deliberately not written from a request, because the request contract
    /// does not carry them: the skin and container sources, the materialised hierarchy path, the depth
    /// and the recycle-bin flag. The first two belong to the excluded skinning subsystem; the path and
    /// depth are derived from the parent chain and are maintained by the write path rather than
    /// submitted; and the recycle-bin flag is moved by a deletion, never by an edit.
    /// </remarks>
    public static void ApplyUpdate(Tab tab, UpdateTabRequest request)
    {
        ArgumentNullException.ThrowIfNull(tab);
        ArgumentNullException.ThrowIfNull(request);

        tab.TabName = request.TabName;
        tab.Title = request.Title;
        tab.Description = request.Description;
        tab.KeyWords = request.Keywords;
        tab.IsVisible = request.IsVisible;
        tab.DisableLink = request.DisableLink;
        tab.ParentId = request.ParentId;
        tab.TabOrder = request.TabOrder;
        tab.IconFile = request.IconFile;
        tab.Url = request.Url;
        tab.StartDate = request.StartDate;
        tab.EndDate = request.EndDate;
        tab.RefreshInterval = request.RefreshInterval;
        tab.PageHeadText = request.PageHeadText;
        tab.IsSecure = request.IsSecure;
    }
}
