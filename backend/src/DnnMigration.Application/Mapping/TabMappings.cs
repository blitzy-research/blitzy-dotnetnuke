using DnnMigration.Application.Dtos.Tab;
using DnnMigration.Domain.Entities;

namespace DnnMigration.Application.Mapping;

/// <summary>
/// Hand-written projections between the <see cref="Tab"/> aggregate - the DotNetNuke page - and the page
/// transfer contracts, and from an inbound page update onto the aggregate it describes.
/// </summary>
/// <remarks>
/// <para>
/// The second legacy hydration path is refused just as firmly. The module controller hydrated an entity by
/// assigning one line per column through the sentinel helper and then, part-way through, issued further
/// database reads to complete the object.
/// </para>
/// <para>
/// No permission projection appears in this file, and its absence is a verified decision rather than an
/// omission. None of the three page contracts declares a permission member: the update contract states
/// outright that permissions are a separate concern served by the read-only permission catalogue, and that
/// the legacy permission grid is therefore absent even though the legacy form carried one.
/// </para>
/// </remarks>
public static class TabMappings
{
    /// <summary>Projects a page onto the row shape the page list and the navigation tree render.</summary>
    /// <param name="tab">The page to project.</param>
    /// <param name="hasChildren">
    /// Whether any other page names this one as its parent, computed by the caller across the whole set
    /// being listed.
    /// </param>
    /// <returns>The list row.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="tab"/> is <see langword="null"/>.</exception>
    /// <remarks>
    /// This row shape deliberately omits the tenant key, which the detail contract does declare. A list is
    /// always read within one tenant, so repeating the key on every row would add nothing; the asymmetry is
    /// recorded on the contracts themselves.
    /// </remarks>
    public static TabListItemDto ToListItem(Tab tab, bool hasChildren)
    {
        ArgumentNullException.ThrowIfNull(tab);

        return new TabListItemDto
        {
            // The page key is copied verbatim and is never tested against zero. The column is an identity
            // seeded at zero, so the first page ever created carries a key of zero and a zero-or-below
            // guard here would discard a real row.
            TabId = tab.TabId,
            TabName = tab.TabName,
            Title = tab.Title,
            TabOrder = tab.TabOrder,

            // The parent key passes straight through as a nullable value. Null means "root of the
            // hierarchy"; it is never collapsed to the legacy minus-one sentinel, and a stored zero is a
            // real parent because page keys start at zero.
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

    /// <summary>Projects a page onto the full detail contract, covering every stored column.</summary>
    /// <param name="tab">The page to project.</param>
    /// <param name="hasChildren">Whether any other page names this one as its parent, computed by the caller.</param>
    /// <returns>The detail contract.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="tab"/> is <see langword="null"/>.</exception>
    /// <remarks>
    /// All 22 stored columns are carried, which is the whole of the legacy 36-member surface once the 14
    /// members enumerated on this type are set aside. The child flag is the twenty-third member of the
    /// contract and is the argument described above; it is the one published value that is not a column.
    /// </remarks>
    public static TabDetailDto ToDetail(Tab tab, bool hasChildren)
    {
        ArgumentNullException.ThrowIfNull(tab);

        return new TabDetailDto
        {
            TabId = tab.TabId,
            TabOrder = tab.TabOrder,

            // The tenant key is copied in one piece, with no comparison against minus one and none against
            // zero.
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

            // Both dates are copied verbatim.
            StartDate = tab.StartDate,
            EndDate = tab.EndDate,
            RefreshInterval = tab.RefreshInterval,
            PageHeadText = tab.PageHeadText,
            IsSecure = tab.IsSecure,
            HasChildren = hasChildren,
        };
    }

    /// <summary>Applies a submitted page update to an already-loaded page aggregate.</summary>
    /// <param name="tab">The tracked page to modify.</param>
    /// <param name="request">The submitted values.</param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="tab"/> or <paramref name="request"/> is <see langword="null"/>.
    /// </exception>
    /// <remarks>
    /// All 17 members the update contract carries are written, in the order that contract declares them,
    /// and every one is written unconditionally. That is deliberate and reproduces the legacy save
    /// behaviour exactly: the legacy update signature had no optional arguments, so submitting the
    /// page-settings screen with a box empty stored an empty value and cleared what was there.
    /// </remarks>
    public static void ApplyUpdate(Tab tab, UpdateTabRequest request)
    {
        ArgumentNullException.ThrowIfNull(tab);
        ArgumentNullException.ThrowIfNull(request);

        tab.TabName = request.TabName;

        tab.Title = request.Title;
        tab.Description = request.Description;

        tab.Keywords = request.Keywords;

        // MIGRATION: nullable in, nullable out. Null means "move to the root"; the legacy minus-one
        // sentinel is neither read nor written, and no guard rejects zero or a negative value.
        tab.ParentId = request.ParentId;

        tab.IsVisible = request.IsVisible;

        // Copied here because a mapper has no portal context. TabService applies the legacy
        // five-special-page rule after it has loaded the owning Portal and forces this value back to false
        // for the administration, splash, home, login and user pages.
        tab.DisableLink = request.DisableLink;
        tab.IconFile = request.IconFile;

        tab.Url = request.Url;

        // Both dates are stored exactly as submitted. Nothing here compares them to each other or to a
        // clock, so an out-of-order window is a validator's concern and an "is published" answer is the
        // page service's.
        tab.StartDate = request.StartDate;
        tab.EndDate = request.EndDate;

        tab.RefreshInterval = request.RefreshInterval;
        tab.PageHeadText = request.PageHeadText;
        tab.IsSecure = request.IsSecure;

        tab.IsDeleted = request.IsDeleted;
    }
}
