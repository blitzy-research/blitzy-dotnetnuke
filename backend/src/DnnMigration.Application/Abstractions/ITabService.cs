using DnnMigration.Application.Dtos.Common;
using DnnMigration.Application.Dtos.Tab;
using DnnMigration.Domain.Common;

namespace DnnMigration.Application.Abstractions;

/// <summary>
/// Application-layer contract for reading and updating pages - the DotNetNuke "tab" abstraction - within a
/// portal.
/// </summary>
/// <remarks>
/// <para>
/// <b>Absence is nullable, never a magic number.</b> No member accepts or returns a sentinel integer
/// standing for "missing".
/// </para>
/// <para>
/// <b>Asynchronous, scoped, and free of out-parameters.</b> Every member performs input and output, so
/// every member returns a task, carries the <c>Async</c> suffix and takes a cancellation token as its final
/// argument; the legacy mutate-and-report-status idiom is replaced entirely by the returned result.
/// <c>AddApplication()</c> registers this abstraction with a scoped lifetime, and the implementation
/// (<c>Services/TabService.cs</c>) reaches persistence only through the domain layer's page repository
/// abstraction and commits through the unit of work, never seeing a database context.
/// </para>
/// </remarks>
public interface ITabService
{
    /// <summary>
    /// Lists one page of the pages belonging to one portal, as a flat sequence ordered for hierarchical
    /// display.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Serves <c>GET /api/v1/portals/{id}/tabs</c>. This single member replaces six legacy read members:
    /// <c>GetTabs</c>, the two <c>GetAllTabs</c> overloads, the two <c>GetTabsByParentId</c> overloads and
    /// <c>GetTabsByPortal</c>.
    /// </para>
    /// <para>
    /// <b>Paged, because a portal's page tree is unbounded by nature.</b> A tenant legitimately holds
    /// thousands of pages, and an unpaged listing made the response grow linearly with tenant configuration
    /// with nothing a caller could do about it. The page is cut from the pages this caller may act on, so the
    /// reported total is the size of the collection the caller is paging rather than the tenant's raw count.
    /// </para>
    /// </remarks>
    /// <param name="portalId">Identifier of the portal whose pages are requested, taken from the route.</param>
    /// <param name="request">
    /// The page of the collection to return. The sort field and the filter are refused by
    /// <c>TabPagedRequestValidator</c> before this member is reached, and refused again here, because the
    /// navigation order is the only order in which a page tree describes itself truthfully.
    /// </param>
    /// <param name="cancellationToken">Token observed while the read is in flight.</param>
    /// <returns>
    /// A task producing a successful result whose value is one page of the portal's pages - possibly an empty
    /// page, never <see langword="null"/> - or a failed result carrying the reason code
    /// <c>tab.portal_not_found</c> when no portal bears <paramref name="portalId"/>, which the API layer maps
    /// to <c>404 Not Found</c>, or <c>tab.paging_invalid</c> when the request names an ordering or a filter
    /// this collection does not offer, which maps to <c>400 Bad Request</c>.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="request"/> is <see langword="null"/>.</exception>
    Task<Result<PagedResult<TabListItemDto>>> GetTabsAsync(
        int portalId,
        TabPagedRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads one page by identifier, returning a successful result whose value is <see langword="null"/>
    /// when no such page exists.
    /// </summary>
    /// <remarks>
    /// Serves <c>GET /api/v1/tabs/{id}</c>. This member replaces four legacy read members: the two
    /// <c>GetTab</c> overloads and the two <c>GetTabByName</c> overloads.
    /// </remarks>
    /// <param name="tabId">
    /// Identifier of the page to read. <c>0</c> is a valid identifier belonging to a real page, because
    /// <c>Tabs.TabID</c> is declared <c>IDENTITY(0, 1)</c>; it must never be interpreted as "unspecified"
    /// or "not yet saved".
    /// </param>
    /// <param name="cancellationToken">Token observed while the read is in flight.</param>
    /// <returns>
    /// A task producing a successful result whose value is the page, or a successful result whose value is
    /// <see langword="null"/> when no page bears <paramref name="tabId"/>.
    /// </returns>
    Task<Result<TabDetailDto?>> GetTabAsync(int tabId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Updates one page - its metadata, its visibility and its position in the page tree - and returns the
    /// page as it stands afterwards.
    /// </summary>
    /// <remarks>
    /// <b>Ordering travels as request properties, not as positional arguments.</b> Parent, level, order and
    /// visibility - which the legacy seven-argument signature carried positionally, ending in an optional
    /// Boolean the caller could not see - are all named properties on <paramref name="request"/>, including
    /// that implicit tail. Ordering is always stated explicitly.
    /// </remarks>
    /// <param name="tabId">
    /// Identifier of the page to update, taken from the route, which is the authoritative target. <c>0</c>
    /// is a legitimate identifier and must not be read as "unspecified".
    /// </param>
    /// <param name="request">The new state to apply, including the page's position in the tree.</param>
    /// <param name="cancellationToken">Token observed while the update is in flight.</param>
    /// <returns>
    /// A task producing a successful result carrying the page as it stands after the update, or a failed
    /// result carrying one of these reason codes: <c>tab.not_found</c> when no page bears <paramref
    /// name="tabId"/>; <c>tab.parent_not_found</c> when the requested parent does not exist;
    /// <c>tab.parent_cross_portal</c> when the requested parent belongs to a different portal, which
    /// preserves tenant isolation now that the identifier arrives in a request body rather than from a
    /// portal-filtered picker; <c>tab.parent_cycle</c> when the requested parent is the page itself or one
    /// of its own descendants; and <c>tab.name_reserved</c> when the page name is a reserved device name,
    /// reproducing the legacy screen's own rejection of names such as <c>CON</c>, <c>NUL</c>, <c>AUX</c>,
    /// <c>COM1</c> and <c>LPT1</c>.
    /// </returns>
    Task<Result<TabDetailDto>> UpdateTabAsync(int tabId, UpdateTabRequest request, CancellationToken cancellationToken = default);
}
