using DnnMigration.Domain.Entities;

namespace DnnMigration.Domain.Abstractions.Repositories;

/// <summary>
/// Reads and writes the <see cref="Tab"/> page hierarchy.
/// </summary>
/// <remarks>
/// MIGRATION: replaces the tab slice of the legacy data provider (35 stored procedures) and the
/// data-access half of <c>TabController.vb</c>. Deletion is a soft delete through
/// <see cref="Tab.IsDeleted"/>, reproducing the legacy recycle bin.
/// </remarks>
public interface ITabRepository
{
    /// <summary>Returns a portal's pages in hierarchy order.</summary>
    /// <param name="portalId">Portal identifier.</param>
    /// <param name="includeDeleted">Whether to include pages in the recycle bin.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<IReadOnlyList<Tab>> ListAsync(int portalId, bool includeDeleted = false, CancellationToken cancellationToken = default);

    /// <summary>Returns one page by key, or <see langword="null"/>.</summary>
    /// <param name="tabId">Page identifier. 0 is legitimate: <c>Tabs.TabID</c> is <c>IDENTITY(0, 1)</c>.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<Tab?> GetAsync(int tabId, CancellationToken cancellationToken = default);

    /// <summary>Returns the identifiers of pages that have at least one child.</summary>
    /// <param name="portalId">Portal identifier, or <see langword="null"/> to consider every portal.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <remarks>
    /// Answers the "has children" question for a whole listing in one round trip rather than one per
    /// row, which is what the legacy per-row navigation query did.
    /// </remarks>
    Task<IReadOnlyCollection<int>> ListParentTabIdsAsync(int? portalId, CancellationToken cancellationToken = default);

    /// <summary>Returns the identifier of the host-level root page, or <see langword="null"/> when none exists.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <remarks>
    /// MIGRATION: reproduces the correlated sub-select that the legacy portal read view resolved into
    /// the <c>SuperTabId</c> column of its result set -
    /// <c>select TabId from Tabs where PortalId is null and ParentId is null</c>, first in that form at
    /// <c>Website/Providers/DataProviders/SqlDataProvider/01.00.05.SqlDataProvider</c> line 2004 and
    /// carried unchanged through every later revision of the view. The value is identical for every
    /// portal because it is not stored against a portal, which is why it is read once here rather than
    /// projected per row. Zero is a legitimate page identifier, so an absent host root is reported as
    /// <see langword="null"/> and never as a numeric sentinel.
    /// </remarks>
    Task<int?> GetHostRootTabIdAsync(CancellationToken cancellationToken = default);

    /// <summary>Determines whether a page name is already used within a portal.</summary>
    /// <param name="portalId">Portal identifier, or <see langword="null"/> for host pages.</param>
    /// <param name="tabName">The page name to test.</param>
    /// <param name="excludingTabId">A page to ignore, so that an edit does not collide with itself.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<bool> TabNameExistsAsync(int? portalId, string tabName, int? excludingTabId = null, CancellationToken cancellationToken = default);
}
