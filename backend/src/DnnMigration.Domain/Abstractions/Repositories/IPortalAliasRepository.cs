using DnnMigration.Domain.Common;
using DnnMigration.Domain.Entities;

namespace DnnMigration.Domain.Abstractions.Repositories;

// The legacy portal-alias read overloaded -1 as an "all portals" wildcard inside the SQL predicate itself,
// while Portals.PortalID is IDENTITY(-1, 1) and so -1 is also a real portal key.

/// <summary>Reads and writes the <c>dbo.PortalAlias</c> rows that bind a host name to a portal.</summary>
/// <remarks>
/// <para>
/// Every member is asynchronous and cancellable, with no synchronous counterpart, so no caller can block a
/// request thread on database work.
/// </para>
/// <para>
/// Writes are staged, never committed. <see cref="AddAsync"/>, <see cref="UpdateAsync"/> and <see
/// cref="DeleteAsync"/> record an intention that the unit of work commits, which is what lets an alias be
/// written in the same transaction as the portal it belongs to and why no write returns a generated key.
/// </para>
/// </remarks>
public interface IPortalAliasRepository
{
    /// <summary>
    /// Returns the alias bearing the supplied identifier, or <see langword="null"/> when no row bears it.
    /// </summary>
    /// <param name="portalAliasId">The alias key.</param>
    /// <param name="cancellationToken">Token observed while the operation is in flight.</param>
    /// <returns>The matching alias, or <see langword="null"/> when none matches.</returns>
    Task<PortalAlias?> GetByIdAsync(int portalAliasId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the alias of one specified portal whose stored host name is exactly the supplied value, or
    /// <see langword="null"/> when that portal has no such alias.
    /// </summary>
    /// <remarks>
    /// An implementation must compare host names WITHOUT regard to case. Every legacy write and read of
    /// this table lower-cased the value first, so a case-sensitive comparison would fail to find rows the
    /// legacy code found.
    /// </remarks>
    /// <param name="httpAlias">The host name to match, optionally carrying a port or a virtual path.</param>
    /// <param name="portalId">The portal whose aliases are searched.</param>
    /// <param name="cancellationToken">Token observed while the operation is in flight.</param>
    /// <returns>The matching alias, or <see langword="null"/> when that portal has no such host name.</returns>
    Task<PortalAlias?> GetByAliasAsync(string httpAlias, int portalId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns, for every alias across the installation whose stored host name is exactly one of the
    /// supplied candidate values, the facts a request's tenant snapshot is built from.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every match is returned rather than one chosen row. The host-name column carries an
    /// installation-wide unique constraint, so a second row means the data is defective, and the legacy
    /// resolution procedure collapsed multiple matches with <c>min(PortalID)</c> - returning one row here
    /// would quietly reinstate that cross-tenant defect.
    /// </para>
    /// <para>
    /// ⚠ THE PROJECTION IS THE WHOLE POINT OF THIS MEMBER, and an implementation that satisfies it by
    /// loading portals as entities has not satisfied it. Two of the nine facts - the administrator and
    /// registered-user ROLE NAMES - are not columns on <c>Portals</c>, and the only route to them through
    /// the object graph is the portal's whole role collection. Taking that route made this lookup return one
    /// row per role of the matched tenant, with every <c>Portals</c> column repeated on each, and made the
    /// cost of resolving a tenant grow with the number of roles that tenant holds - on every authenticated
    /// request, for a question whose answer is one row. An implementation resolves the two names BY KEY.
    /// </para>
    /// </remarks>
    /// <param name="httpAliasCandidates">The candidate addresses to match, exactly as supplied.</param>
    /// <param name="cancellationToken">Token observed while the operation is in flight.</param>
    /// <returns>
    /// One resolution per alias whose host name matches any candidate, in a stable order; empty when none
    /// matches.
    /// </returns>
    Task<IReadOnlyList<TenantResolution>> ResolveTenantsByHttpAliasAsync(
        IReadOnlyList<string> httpAliasCandidates,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Reports whether any alias already claims the supplied host name, optionally ignoring one alias.
    /// </summary>
    /// <remarks>
    /// The host-name column carries an installation-wide unique constraint, so a duplicate is a refusal a
    /// caller reports rather than a database error it catches. Deliberately not portal-scoped: an alias
    /// bound to another portal is still taken.
    /// </remarks>
    /// <param name="httpAlias">The host name to test.</param>
    /// <param name="excludingPortalAliasId">
    /// An alias to disregard, or <see langword="null"/> to consider every row.
    /// </param>
    /// <param name="cancellationToken">Token observed while the operation is in flight.</param>
    /// <returns><see langword="true"/> when the host name is already claimed.</returns>
    Task<bool> AliasExistsAsync(string httpAlias, int? excludingPortalAliasId, CancellationToken cancellationToken = default);

    /// <summary>Returns the aliases belonging to exactly the portal bearing the supplied identifier.</summary>
    /// <param name="portalId">The portal whose aliases are wanted.</param>
    /// <param name="cancellationToken">Token observed while the operation is in flight.</param>
    /// <returns>That portal's aliases in a stable order; empty when it has none.</returns>
    Task<IReadOnlyList<PortalAlias>> GetByPortalIdAsync(int portalId, CancellationToken cancellationToken = default);

    /// <summary>Returns the aliases of MANY portals in one read.</summary>
    /// <param name="portalIds">
    /// The portals whose aliases are wanted; no value is read as a request for every portal.
    /// </param>
    /// <param name="cancellationToken">Token observed while the operation is in flight.</param>
    /// <returns>
    /// The aliases of every named portal in a stable order, FLAT rather than grouped, so a caller groups by
    /// <see cref="PortalAlias.PortalId"/> itself.
    /// </returns>
    /// <remarks>
    /// Exists so that a paged tenant listing can fetch the aliases of the tenants on its page at a page's
    /// cost: asking per row costs one read per row, and asking <see cref="GetAllAsync"/> then grouping in
    /// memory costs the whole installation.
    /// </remarks>
    Task<IReadOnlyList<PortalAlias>> GetByPortalIdsAsync(
        IReadOnlyCollection<int> portalIds,
        CancellationToken cancellationToken = default);

    /// <summary>Returns every alias in the installation, across all portals.</summary>
    /// <remarks>
    /// The member that makes the legacy wildcard explicit, which is why it takes no parameter: a distinct
    /// question is asked by calling a distinct member rather than by passing a magic value to a shared one.
    /// Intended for installation-wide work, not for resolving one request - resolution matches through <see
    /// cref="ResolveTenantsByHttpAliasAsync"/> and must not filter this result in memory.
    /// </remarks>
    /// <param name="cancellationToken">Token observed while the operation is in flight.</param>
    /// <returns>Every alias in a stable order; empty when the installation has none.</returns>
    Task<IReadOnlyList<PortalAlias>> GetAllAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the portal reached through the alias bearing the supplied identifier, or <see
    /// langword="null"/> when no alias bears it.
    /// </summary>
    /// <param name="portalAliasId">The alias whose owning portal is wanted.</param>
    /// <param name="cancellationToken">Token observed while the operation is in flight.</param>
    /// <returns>The owning portal, or <see langword="null"/> when no alias bears the identifier.</returns>
    Task<Portal?> GetPortalByAliasIdAsync(int portalAliasId, CancellationToken cancellationToken = default);

    /// <summary>Stages a new alias for insertion. The unit of work commits it.</summary>
    /// <remarks>
    /// The generated key is read from the entity after the unit of work has saved; before then the alias is
    /// staged and <see cref="PortalAlias.PortalAliasId"/> must not be relied upon. Assigning <see
    /// cref="PortalAlias.Portal"/> rather than <see cref="PortalAlias.PortalId"/> is how an alias is bound
    /// to a portal that is itself new.
    /// </remarks>
    /// <param name="portalAlias">The alias to insert.</param>
    /// <param name="cancellationToken">Token observed while the operation is in flight.</param>
    /// <returns>A task that completes once the insertion is staged.</returns>
    Task AddAsync(PortalAlias portalAlias, CancellationToken cancellationToken = default);

    /// <summary>Stages an amended alias for update. The unit of work commits it.</summary>
    /// <param name="portalAlias">The alias whose stored state is to be replaced by its current state.</param>
    /// <param name="cancellationToken">Token observed while the operation is in flight.</param>
    /// <returns>A task that completes once the update is staged.</returns>
    Task UpdateAsync(PortalAlias portalAlias, CancellationToken cancellationToken = default);

    /// <summary>Stages the alias bearing the supplied identifier for deletion. The unit of work commits it.</summary>
    /// <remarks>
    /// Takes the identifier rather than the row, so a caller holding only a key need not read the row
    /// first. Removing an alias unbinds a host name from its portal and does not affect the portal.
    /// </remarks>
    /// <param name="portalAliasId">The alias to delete.</param>
    /// <param name="cancellationToken">Token observed while the operation is in flight.</param>
    /// <returns>A task that completes once the deletion is staged.</returns>
    Task DeleteAsync(int portalAliasId, CancellationToken cancellationToken = default);
}
