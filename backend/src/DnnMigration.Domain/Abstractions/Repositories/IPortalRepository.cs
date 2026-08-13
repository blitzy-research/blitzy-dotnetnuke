using DnnMigration.Domain.Common;
using DnnMigration.Domain.Entities;

namespace DnnMigration.Domain.Abstractions.Repositories;

/// <summary>
/// The persistence contract for the <see cref="Portal"/> aggregate: DotNetNuke's multi-tenant site
/// container and the root tenant boundary of the application.
/// </summary>
/// <remarks>
/// <para>
/// This is the only route from the application layer to persisted portal state. It exposes no persistence
/// session, no transaction handle, no deferred query surface and no provider type, so a consumer cannot
/// name - and therefore cannot depend upon - the technology that stores a portal.
/// </para>
/// <para>
/// Identifiers cross this boundary as plain framework integers and strings rather than as identifier value
/// objects. The value objects exist for application-facing boundaries; the entities declare plain scalars
/// for their own identity, and the conversion between the two belongs to the persistence configuration in
/// the outer layer.
/// </para>
/// </remarks>
public interface IPortalRepository
{
    /// <summary>Returns one page of portals, optionally narrowed by name and ordered by a named property.</summary>
    /// <remarks>
    /// The five inputs are one cohesive query descriptor - a filter, an order and a page coordinate pair -
    /// and are not an entity field list. This member is deliberately the widest on the contract, and it
    /// remains far removed from the legacy parameter explosions that motivated collapsing writes onto
    /// entities.
    /// </remarks>
    /// <param name="pageIndex">
    /// The zero-based index of the page to return, matching the base fixed by <see cref="PagedResult{T}"/>.
    /// </param>
    /// <param name="pageSize">The maximum number of records on the page.</param>
    /// <param name="nameFilter">
    /// A case-insensitive fragment matched anywhere within the portal name, or <see langword="null"/> to
    /// match every portal.
    /// </param>
    /// <param name="sortBy">
    /// The name of the property to order by, or <see langword="null"/> for the default order.
    /// </param>
    /// <param name="descending">Whether the resolved order is reversed.</param>
    /// <param name="cancellationToken">Abandons the operation.</param>
    /// <returns>The requested page together with the total number of matches across every page.</returns>
    Task<PagedResult<Portal>> ListAsync(
        int pageIndex,
        int pageSize,
        string? nameFilter,
        string? sortBy,
        bool descending,
        CancellationToken cancellationToken = default);

    /// <summary>Returns every portal in the installation.</summary>
    /// <param name="cancellationToken">Abandons the operation.</param>
    /// <returns>Every portal, materialised, in a deterministic order.</returns>
    Task<IReadOnlyList<Portal>> GetAllAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the portal bearing the supplied identifier, or <see langword="null"/> when no portal bears
    /// it.
    /// </summary>
    /// <param name="portalId">The portal identifier.</param>
    /// <param name="includeAliases">Whether the portal's aliases are loaded alongside it.</param>
    /// <param name="cancellationToken">Abandons the operation.</param>
    /// <returns>The portal, or <see langword="null"/>.</returns>
    Task<Portal?> GetByIdAsync(
        int portalId,
        bool includeAliases = false,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Resolves the portal that owns the supplied host alias, or <see langword="null"/> when no portal
    /// claims it.
    /// </summary>
    /// <remarks>
    /// The alias must be matched <b>exactly</b>. The legacy resolution procedure selected the lowest
    /// matching identifier using a leading-and-trailing wildcard comparison against the alias column, so
    /// one tenant's alias that happened to be a substring of another's could resolve a request to the wrong
    /// tenant.
    /// </remarks>
    /// <param name="httpAlias">
    /// The host alias to resolve, as it is stored - a host name with an optional port and path.
    /// </param>
    /// <param name="cancellationToken">Abandons the operation.</param>
    /// <returns>The owning portal, or <see langword="null"/>.</returns>
    Task<Portal?> GetByAliasAsync(string httpAlias, CancellationToken cancellationToken = default);

    /// <summary>
    /// Resolves the portal reached by the supplied page under the supplied host alias, or <see
    /// langword="null"/> when the pairing does not resolve.
    /// </summary>
    /// <param name="tabId">The page identifier.</param>
    /// <param name="httpAlias">The host alias the page was requested under.</param>
    /// <param name="cancellationToken">Abandons the operation.</param>
    /// <returns>
    /// The portal that owns both the alias and the page, or <see langword="null"/> when the alias is
    /// unknown or the page belongs to a different portal.
    /// </returns>
    Task<Portal?> GetByTabAsync(int tabId, string httpAlias, CancellationToken cancellationToken = default);

    /// <summary>Determines whether a portal bearing the supplied identifier exists.</summary>
    /// <param name="portalId">The portal identifier.</param>
    /// <param name="cancellationToken">Abandons the operation.</param>
    /// <returns><see langword="true"/> when the portal exists; otherwise <see langword="false"/>.</returns>
    Task<bool> ExistsAsync(int portalId, CancellationToken cancellationToken = default);

    /// <summary>Determines whether the supplied page belongs to the supplied portal.</summary>
    /// <param name="portalId">The portal the page is expected to belong to.</param>
    /// <param name="tabId">The page identifier.</param>
    /// <param name="cancellationToken">Abandons the operation.</param>
    /// <returns>
    /// <see langword="true"/> when the page exists and belongs to that portal; otherwise <see
    /// langword="false"/>, including when neither exists.
    /// </returns>
    Task<bool> TabBelongsToPortalAsync(
        int portalId,
        int tabId,
        CancellationToken cancellationToken = default);

    /// <summary>Counts the portals in the installation.</summary>
    /// <param name="cancellationToken">Abandons the operation.</param>
    /// <returns>The number of portals, which may be zero.</returns>
    Task<int> CountAsync(CancellationToken cancellationToken = default);

    /// <summary>Counts the users who are members of the supplied portal.</summary>
    /// <remarks>
    /// Membership of a tenant is a row in the portal-membership table rather than a column on the user, so
    /// the tally is taken there. The legacy administration grid obtained this figure from a correlated
    /// sub-select inside the portal view, so it was computed per row there as well; it is a projection over
    /// related data and is therefore not a stored column on <see cref="Portal"/>.
    /// </remarks>
    /// <param name="portalId">The portal identifier.</param>
    /// <param name="cancellationToken">Abandons the operation.</param>
    /// <returns>
    /// The number of member accounts, which is zero for a portal with no members and for an identifier no
    /// portal bears.
    /// </returns>
    Task<int> CountUsersAsync(int portalId, CancellationToken cancellationToken = default);

    /// <summary>Counts the supplied portal's pages the way the legacy administration grid counted them.</summary>
    /// <remarks>
    /// Three parts of that are counter-intuitive, and all three are preserved under Rule T5 rather than
    /// corrected. FIRST, the administration page and its DIRECT children are excluded - only direct
    /// children, because the predicate tests <c>ParentId</c> and nothing deeper, so a grandchild of the
    /// administration page is counted.
    /// </remarks>
    /// <param name="portalId">The portal identifier.</param>
    /// <param name="cancellationToken">Abandons the operation.</param>
    /// <returns>The legacy page tally.</returns>
    Task<int> CountPagesAsync(int portalId, CancellationToken cancellationToken = default);

    /// <summary>Counts the member accounts of every supplied portal in one read.</summary>
    /// <param name="portalIds">The portal identifiers to tally.</param>
    /// <param name="cancellationToken">Abandons the operation.</param>
    /// <returns>One entry per distinct supplied identifier, mapping it to its member count.</returns>
    Task<IReadOnlyDictionary<int, int>> CountUsersForPortalsAsync(
        IReadOnlyCollection<int> portalIds,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Counts the pages of every supplied portal in one read, on the same terms as <see
    /// cref="CountPagesAsync"/>.
    /// </summary>
    /// <remarks>
    /// The batched counterpart of <see cref="CountPagesAsync"/>, and it exists for the same reason as <see
    /// cref="CountUsersForPortalsAsync"/>: a listing needs one tally per row and must not pay a round trip
    /// per row to obtain it. The result is TOTAL over the supplied identifiers on the same terms.
    /// </remarks>
    /// <param name="portalIds">The portal identifiers to tally.</param>
    /// <param name="cancellationToken">Abandons the operation.</param>
    /// <returns>
    /// One entry per distinct supplied identifier, mapping it to its legacy page tally - minus one where
    /// the portal does not exist or records no administration page.
    /// </returns>
    Task<IReadOnlyDictionary<int, int>> CountPagesForPortalsAsync(
        IReadOnlyCollection<int> portalIds,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the names of the roles the supplied portal nominates as its administrator and
    /// registered-user roles.
    /// </summary>
    /// <remarks>
    /// The portal stores the two role identifiers, not their names; the legacy portal view resolved the
    /// names through correlated sub-selects. Only roles that actually exist are reported, which is what
    /// lets a caller distinguish a nomination that was never made from one pointing at a role that has
    /// since been deleted.
    /// </remarks>
    /// <param name="portalId">The portal identifier.</param>
    /// <param name="cancellationToken">Abandons the operation.</param>
    /// <returns>
    /// A read-only map from role identifier to role name, holding at most the two nominated roles and
    /// holding only those that exist.
    /// </returns>
    Task<IReadOnlyDictionary<int, string>> GetRoleNamesAsync(
        int portalId,
        CancellationToken cancellationToken = default);

    /// <summary>Stages a new portal for insertion on the next unit-of-work commit.</summary>
    /// <param name="portal">The portal to insert.</param>
    /// <param name="cancellationToken">Abandons the operation.</param>
    /// <returns>A task that completes once the insertion is staged.</returns>
    Task AddAsync(Portal portal, CancellationToken cancellationToken = default);

    /// <summary>Stages the supplied portal's modified state for update on the next unit-of-work commit.</summary>
    /// <param name="portal">The portal whose modified state is staged.</param>
    /// <param name="cancellationToken">Abandons the operation, leaving the store untouched.</param>
    /// <returns>A task that completes once the update is staged.</returns>
    Task UpdateAsync(Portal portal, CancellationToken cancellationToken = default);

    /// <summary>
    /// Stages the portal bearing the supplied identifier for deletion on the next unit-of-work commit.
    /// </summary>
    /// <param name="portalId">The identifier of the portal to delete.</param>
    /// <param name="cancellationToken">Abandons the operation, leaving the store untouched.</param>
    /// <returns>
    /// A task that completes once the deletion is staged, or once it is determined that there is nothing to
    /// stage.
    /// </returns>
    Task DeleteAsync(int portalId, CancellationToken cancellationToken = default);
}
