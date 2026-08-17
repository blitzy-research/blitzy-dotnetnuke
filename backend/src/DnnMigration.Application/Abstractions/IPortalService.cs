using DnnMigration.Application.Dtos.Common;
using DnnMigration.Application.Dtos.Portal;
using DnnMigration.Domain.Common;

namespace DnnMigration.Application.Abstractions;

/// <summary>
/// Application-layer contract for the portal aggregate - the multi-tenant site container - and for the
/// aliases through which each tenant is reached.
/// </summary>
/// <remarks>
/// <para>
/// Scope. This contract owns the tenant container itself: listing, reading, creating, modifying and
/// removing a portal, projecting its configuration for display, and managing the host names bound to it.
/// </para>
/// <para>
/// One outcome shape, everywhere. Every member returns <see cref="Result"/> or <see cref="Result{T}"/>.
/// </para>
/// </remarks>
public interface IPortalService
{
    // 15 of 15. The legacy name filter was a raw pattern.

    /// <summary>Lists portals, optionally narrowed by name, as one page of a larger set.</summary>
    /// <param name="request">The page of records being asked for.</param>
    /// <param name="nameFilter">An optional literal fragment of a portal's name.</param>
    /// <param name="cancellationToken">Propagates notification that the work should be abandoned.</param>
    /// <returns>A successful outcome carrying one page of portals, which is empty when nothing matched.</returns>
    Task<Result<PagedResult<PortalListItemDto>>> ListPortalsAsync(
        PagedRequest request,
        string? nameFilter = null,
        CancellationToken cancellationToken = default);

    /// <summary>Reads one portal in full.</summary>
    /// <param name="portalId">Identifier of the portal to read.</param>
    /// <param name="cancellationToken">Propagates notification that the work should be abandoned.</param>
    /// <returns>
    /// A successful outcome whose value is the portal, or a successful outcome whose value is <see
    /// langword="null"/> when no portal carries that identifier.
    /// </returns>
    Task<Result<PortalDetailDto?>> GetPortalAsync(
        int portalId,
        CancellationToken cancellationToken = default);

    /// <summary>Creates a portal together with the records a working tenant requires.</summary>
    /// <param name="request">The portal to create.</param>
    /// <param name="cancellationToken">Propagates notification that the work should be abandoned.</param>
    /// <returns>
    /// A successful outcome carrying the created portal, including the identifier the database assigned, so
    /// that a caller can answer 201 Created with a location for the new resource.
    /// </returns>
    /// <remarks>
    /// The write spans the <c>Portals</c>, <c>PortalAlias</c>, <c>Roles</c>, <c>Tabs</c> and <c>Modules</c>
    /// tables and is committed exactly once through the domain layer's unit of work, so a partially built
    /// tenant cannot be left behind. On failure nothing is committed, and the failure code - not a returned
    /// identifier - reports what happened.
    /// </remarks>
    Task<Result<PortalDetailDto>> CreatePortalAsync(
        CreatePortalRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>Modifies an existing portal.</summary>
    /// <param name="portalId">Identifier of the portal to modify, bound from the route.</param>
    /// <param name="request">The values to store.</param>
    /// <param name="cancellationToken">Propagates notification that the work should be abandoned.</param>
    /// <returns>
    /// A successful outcome carrying the stored portal, so that a caller can answer 200 OK with the current
    /// state; or a successful outcome whose value is <see langword="null"/> when no portal carries that
    /// identifier, which a caller renders as 404.
    /// </returns>
    Task<Result<PortalDetailDto?>> UpdatePortalAsync(
        int portalId,
        UpdatePortalRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>Removes a portal and the records that depend on it.</summary>
    /// <param name="portalId">Identifier of the portal to remove.</param>
    /// <param name="cancellationToken">Propagates notification that the work should be abandoned.</param>
    /// <returns>A successful outcome when the portal was removed, which a caller answers as 204 No Content.</returns>
    /// <remarks>
    /// The last-portal rule is decided inside the implementation and surfaced as
    /// <c>portal.last_remaining</c>. This contract deliberately exposes no way to ask how many portals
    /// exist, because that would move the decision into the caller.
    /// </remarks>
    Task<Result> DeletePortalAsync(
        int portalId,
        CancellationToken cancellationToken = default);

    /// <summary>Reads a portal's configuration, projected for display.</summary>
    /// <param name="portalId">Identifier of the portal whose configuration is wanted.</param>
    /// <param name="cancellationToken">Propagates notification that the work should be abandoned.</param>
    /// <returns>
    /// A successful outcome carrying the configuration, or a successful outcome whose value is <see
    /// langword="null"/> when no portal carries that identifier.
    /// </returns>
    /// <remarks>
    /// Reading and writing share one resource URL and one projection, but configuration is still stored on
    /// the portal row rather than in a separate settings table. The matching writer below applies the same
    /// business guards and mapping as <see cref="UpdatePortalAsync(int, UpdatePortalRequest,
    /// CancellationToken)"/> while returning this screen-shaped projection.
    /// </remarks>
    Task<Result<PortalSettingsDto?>> GetPortalSettingsAsync(
        int portalId,
        CancellationToken cancellationToken = default);

    /// <summary>Replaces a portal's editable configuration and returns the updated settings projection.</summary>
    /// <param name="portalId">Identifier of the portal whose configuration is being replaced.</param>
    /// <param name="request">The complete editable state submitted by the settings screen.</param>
    /// <param name="cancellationToken">Propagates notification that the work should be abandoned.</param>
    /// <returns>
    /// A successful outcome carrying the updated configuration, or a successful outcome whose value is <see
    /// langword="null"/> when no portal carries that identifier.
    /// </returns>
    /// <remarks>
    /// The guards, the reference validation and the write are ONE SERIALISABLE OPERATION. Judging ownership
    /// in one statement and writing in a later one would let a membership be withdrawn or a page removed in
    /// between, storing the very reference the validation refuses.
    /// </remarks>
    Task<Result<PortalSettingsDto?>> UpdatePortalSettingsAsync(
        int portalId,
        UpdatePortalSettingsRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>Lists the accounts a portal may designate as its administrator.</summary>
    /// <param name="portalId">Identifier of the portal whose candidates are wanted.</param>
    /// <param name="cancellationToken">Propagates notification that the work should be abandoned.</param>
    /// <returns>
    /// A successful outcome carrying the candidates, or a successful outcome whose value is <see
    /// langword="null"/> when no portal carries that identifier.
    /// </returns>
    /// <remarks>
    /// An implementer must not widen this to every account in the portal. The write path already guards the
    /// broader rule - the designated account must belong to the addressed portal - and a wider list would
    /// offer accounts the legacy selector never offered, which is a change in behaviour rather than a
    /// convenience.
    /// </remarks>
    Task<Result<IReadOnlyList<PortalAdministratorDto>?>> ListAdministratorCandidatesAsync(
        int portalId,
        CancellationToken cancellationToken = default);

    /// <summary>Lists the aliases bound to one portal, or every alias in the installation.</summary>
    /// <param name="portalId">
    /// Identifier of the portal whose aliases are wanted, or <see langword="null"/> to list every alias
    /// across every portal.
    /// </param>
    /// <param name="cancellationToken">Propagates notification that the work should be abandoned.</param>
    /// <returns>
    /// A successful outcome carrying the aliases, which is an empty sequence - never <see langword="null"/>
    /// - when there are none.
    /// </returns>
    /// <remarks>
    /// The wildcard sentinel is gone. <c>GetPortalAliases</c> asked for every alias by delegating to the
    /// by-portal reader with -1 as the portal identifier, a value the underlying procedure treated as
    /// "match every row" - yet -1 is also a real portal identifier, because <c>Portals.PortalID</c> seeds
    /// its identity at that value.
    /// </remarks>
    Task<Result<IReadOnlyList<PortalAliasDto>>> ListPortalAliasesAsync(
        int? portalId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Reports whether one leading path segment names a configured tenant beneath a host, so that a
    /// single-page application can tell a child-portal prefix from a mistyped address.
    /// </summary>
    /// <param name="hostAuthority">
    /// The authority the document was served from, INCLUDING its port when one is present, because that is
    /// how an alias is stored. Supplied by the caller rather than read from ambient request state, so this
    /// member is a function of its arguments and can be exercised without a request at all.
    /// </param>
    /// <param name="segment">The first path segment of the address being resolved, without separators.</param>
    /// <param name="cancellationToken">Propagates notification that the work should be abandoned.</param>
    /// <returns>
    /// A successful outcome carrying the segment and whether it is addressable as a tenant. The outcome is
    /// successful even when the segment names nothing: "this is not a tenant path" is an ANSWER, and the
    /// caller acts on it by matching the segment as a route instead.
    /// </returns>
    /// <remarks>
    /// A segment the topology could never have stored is answered without reading the store at all, because
    /// such a value can match nothing. See <see cref="Dtos.Portal.TenantPathPrefixDto"/> for why a client
    /// cannot settle this question for itself and why answering it discloses nothing an address does not.
    /// </remarks>
    Task<Result<TenantPathPrefixDto>> ResolveTenantPathPrefixAsync(
        string hostAuthority,
        string segment,
        CancellationToken cancellationToken = default);

    /// <summary>Reads one alias of one portal.</summary>
    /// <param name="portalId">Identifier of the portal the alias must belong to.</param>
    /// <param name="portalAliasId">Identifier of the alias to read.</param>
    /// <param name="cancellationToken">Propagates notification that the work should be abandoned.</param>
    /// <returns>
    /// A successful outcome carrying the alias, or a successful outcome whose value is <see
    /// langword="null"/> when no alias carries that identifier within that portal.
    /// </returns>
    /// <remarks>
    /// THE PORTAL IS OPTIONAL, AND WHAT ITS ABSENCE MEANS IS THE WHOLE OF THE RULE. Supplying it scopes the
    /// read or the write to that tenant and is what every portal-nested route does; omitting it declares
    /// that the caller holds INSTALLATION-WIDE authority, which the three host-only routes establish
    /// through the host-administrator policy before this member is reached.
    /// </remarks>
    Task<Result<PortalAliasDto?>> GetPortalAliasAsync(
        int? portalId,
        int portalAliasId,
        CancellationToken cancellationToken = default);

    /// <summary>Binds a new alias to a portal.</summary>
    /// <param name="portalId">Identifier of the portal to bind the alias to.</param>
    /// <param name="request">
    /// The alias to bind, whose shape has already been checked by <c>CreatePortalAliasRequestValidator</c>.
    /// </param>
    /// <param name="cancellationToken">Propagates notification that the work should be abandoned.</param>
    /// <returns>
    /// A successful outcome carrying the stored alias, including the identifier the database assigned, so
    /// that a caller can answer 201 Created with a location for the new resource.
    /// </returns>
    // This member took the alias PROJECTION until the request contract below existed, and that shape could
    // not express the write.
    Task<Result<PortalAliasDto>> AddPortalAliasAsync(
        int portalId,
        CreatePortalAliasRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>Modifies an existing alias.</summary>
    /// <param name="portalId">Identifier of the portal the alias must belong to.</param>
    /// <param name="portalAliasId">Identifier of the alias to modify.</param>
    /// <param name="request">
    /// The values to store, whose shape has already been checked by
    /// <c>UpdatePortalAliasRequestValidator</c>.
    /// </param>
    /// <param name="cancellationToken">Propagates notification that the work should be abandoned.</param>
    /// <returns>
    /// A successful outcome carrying the alias as it now stands, so that a caller answers 200 OK with the
    /// updated representation.
    /// </returns>
    /// <remarks>
    /// The stored row is returned rather than discarded because it carries facts a caller cannot
    /// reconstruct from its own request. The decisive one is the current-alias flag: whether this row is
    /// the alias the request itself resolved the tenant through is decided here, from the request's own
    /// context, and nothing in the submitted contract implies it.
    /// </remarks>
    Task<Result<PortalAliasDto>> UpdatePortalAliasAsync(
        int? portalId,
        int portalAliasId,
        UpdatePortalAliasRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>Unbinds an alias from its portal.</summary>
    /// <param name="portalId">Identifier of the portal the alias must belong to.</param>
    /// <param name="portalAliasId">Identifier of the alias to unbind.</param>
    /// <param name="cancellationToken">Propagates notification that the work should be abandoned.</param>
    /// <returns>A successful outcome when the alias was removed, which a caller answers as 204 No Content.</returns>
    Task<Result> DeletePortalAliasAsync(
        int? portalId,
        int portalAliasId,
        CancellationToken cancellationToken = default);
}
