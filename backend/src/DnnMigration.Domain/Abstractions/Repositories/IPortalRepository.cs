using DnnMigration.Domain.Common;
using DnnMigration.Domain.Entities;

namespace DnnMigration.Domain.Abstractions.Repositories;

/// <summary>
/// Reads and writes the <see cref="Portal"/> aggregate.
/// </summary>
/// <remarks>
/// MIGRATION: replaces the portal slice of the 269-member abstract
/// <c>DotNetNuke.Data.DataProvider</c> and its reflection-resolved
/// <c>DataProvider.Instance()</c> accessor (Library/Components/Providers/Data/DataProvider.vb
/// lines 31-50). Implementations are supplied by dependency injection and are the only route from
/// the Application layer to persisted portal state.
/// </remarks>
public interface IPortalRepository
{
    /// <summary>Returns one page of portals, optionally narrowed by name.</summary>
    /// <param name="pageIndex">Zero-based page index.</param>
    /// <param name="pageSize">Page size; 0 requests every match unpaged.</param>
    /// <param name="nameFilter">Case-insensitive substring of the portal name, or <see langword="null"/> for all.</param>
    /// <param name="sortBy">Sortable property name, or <see langword="null"/> for the default order.</param>
    /// <param name="descending">Whether to reverse the sort.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The requested page together with the total match count.</returns>
    Task<PagedResult<Portal>> ListAsync(
        int pageIndex,
        int pageSize,
        string? nameFilter,
        string? sortBy,
        bool descending,
        CancellationToken cancellationToken = default);

    /// <summary>Returns the portal with the supplied identifier, or <see langword="null"/>.</summary>
    /// <param name="portalId">Portal identifier. 0 and -1 are legitimate values, because <c>Portals.PortalID</c> is <c>IDENTITY(-1, 1)</c>.</param>
    /// <param name="includeAliases">Whether to load the portal's aliases with it.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<Portal?> GetAsync(int portalId, bool includeAliases = false, CancellationToken cancellationToken = default);

    /// <summary>Determines whether a portal with the supplied identifier exists.</summary>
    /// <param name="portalId">Portal identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<bool> ExistsAsync(int portalId, CancellationToken cancellationToken = default);

    /// <summary>Counts the users who are members of the portal.</summary>
    /// <param name="portalId">Portal identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<int> CountUsersAsync(int portalId, CancellationToken cancellationToken = default);

    /// <summary>Counts the portal's pages, excluding those in the recycle bin.</summary>
    /// <param name="portalId">Portal identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<int> CountPagesAsync(int portalId, CancellationToken cancellationToken = default);

    /// <summary>Returns the names of the portal's administrator and registered-user roles.</summary>
    /// <param name="portalId">Portal identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A map from role identifier to role name, containing only the roles that exist.</returns>
    Task<IReadOnlyDictionary<int, string>> GetRoleNamesAsync(int portalId, CancellationToken cancellationToken = default);

    /// <summary>Stages a new portal for insertion on the next unit-of-work commit.</summary>
    /// <param name="portal">The portal to insert.</param>
    void Add(Portal portal);

    /// <summary>Stages a portal for deletion on the next unit-of-work commit.</summary>
    /// <param name="portal">The portal to delete.</param>
    void Remove(Portal portal);
}
