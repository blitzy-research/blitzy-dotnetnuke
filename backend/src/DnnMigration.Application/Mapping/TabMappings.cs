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
/// The keyword spelling difference is resolved in the persistence layer rather than here. The
/// column is <c>KeyWords</c> with a capital <c>W</c>, fixed by the shipped schema, while both the
/// entity member and the transfer contract spell it <c>Keywords</c>, the idiomatic single word that
/// also matches the caption the legacy screens showed. <c>TabConfiguration</c> reconciles the two
/// with an explicit <c>HasColumnName("KeyWords")</c>, so the projections below are a plain
/// like-named assignment and the bridge exists in exactly one place.
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
            Keywords = tab.Keywords,
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
    /// <para>
    /// All seventeen members the request carries are written, in the order the request declares them.
    /// The request shape is itself the terminal update procedure's eighteen mutable fields minus the
    /// one server-derived field among them, so this method writes exactly what that procedure wrote.
    /// </para>
    /// <para>
    /// Exactly three stored members are deliberately not written, because the request contract does
    /// not carry them and must not: the sibling order, the depth and the materialised hierarchy path.
    /// All three are server-derived. The legacy update procedure's parameter list omits the order and
    /// the depth outright and its <c>UPDATE</c> body sets neither, while the legacy controller passed
    /// <c>0</c> for both precisely so that its ordering routine would recompute them; the path was
    /// assigned exclusively by the legacy path generator and cascaded recursively to every descendant
    /// whenever a name or a parent changed. The page service recomputes all three after this method
    /// returns, so writing them here would be both redundant and unsafe.
    /// </para>
    /// <para>
    /// The skin source, the container source and the recycle-bin flag <em>are</em> written. The first
    /// two are genuine columns the terminal procedure persists, and blanking them on every edit would
    /// silently destroy an administrator's stored choice; they are carried as opaque tokens, since the
    /// skinning subsystem itself is out of scope. The recycle-bin flag is written because both legacy
    /// recycle-bin transitions - soft delete and restore - were plain writes of that flag through this
    /// very update path, and the page surface exposes no delete or restore route through which they
    /// could otherwise be reached.
    /// </para>
    /// <para>
    /// Neither the page identifier nor the portal identifier is written. The former is the route's
    /// authoritative value and identifies the already-loaded aggregate; the latter is absent from the
    /// request entirely, because the legacy procedure accepted no portal argument and a page therefore
    /// cannot be moved between tenants through this path.
    /// </para>
    /// </remarks>
    public static void ApplyUpdate(Tab tab, UpdateTabRequest request)
    {
        ArgumentNullException.ThrowIfNull(tab);
        ArgumentNullException.ThrowIfNull(request);

        tab.TabName = request.TabName;
        tab.Title = request.Title;
        tab.Description = request.Description;
        tab.Keywords = request.Keywords;
        tab.ParentId = request.ParentId;
        tab.IsVisible = request.IsVisible;
        tab.DisableLink = request.DisableLink;
        tab.IconFile = request.IconFile;
        tab.SkinSrc = request.SkinSrc;
        tab.ContainerSrc = request.ContainerSrc;
        tab.Url = request.Url;
        tab.StartDate = request.StartDate;
        tab.EndDate = request.EndDate;
        tab.RefreshInterval = request.RefreshInterval;
        tab.PageHeadText = request.PageHeadText;
        tab.IsSecure = request.IsSecure;
        tab.IsDeleted = request.IsDeleted;
    }
}
