using DnnMigration.Domain.Entities;

namespace DnnMigration.Domain.Abstractions.Repositories;

// MIGRATION: the legacy portal-alias read overloaded -1 as an "all portals" wildcard inside the SQL
// predicate itself, while Portals.PortalID is IDENTITY(-1, 1) and so -1 is also a real portal key. The
// wildcard is surfaced here as its own member, GetAllAsync, so GetByPortalIdAsync(-1) means the portal whose
// identifier is -1 and nothing wider.
//
// MIGRATION: the legacy UpdatePortalAlias member is omitted. Despite its name it rewrote the placeholder
// '_default' row during a fresh install, and the installer is out of scope; the per-row update is surfaced
// as UpdateAsync.
//
// MIGRATION: tenant resolution changed from a substring match to an exact one. That decision belongs to
// Api/Middleware/PortalAliasResolutionMiddleware.cs and is recorded in MIGRATION_NOTES.md; what this
// contract contributes is refusing to encode substring semantics - no member accepts a pattern, fragment or
// search term, so no implementation can widen a match without changing the contract first.

/// <summary>
/// Reads and writes the <c>dbo.PortalAlias</c> rows that bind a host name to a portal.
/// </summary>
/// <remarks>
/// The persistence contract for the alias aggregate and nothing more: choosing which host name to
/// try, deciding what an unmatched or ambiguous host means, and publishing the result as request
/// state belong to the resolution middleware and the request-scoped tenant context.
/// <para>
/// Every member is asynchronous and cancellable, with no synchronous counterpart, so no caller can
/// block a request thread on database work.
/// </para>
/// <para>
/// Writes are staged, never committed. <see cref="AddAsync"/>, <see cref="UpdateAsync"/> and
/// <see cref="DeleteAsync"/> record an intention that the unit of work commits, which is what lets
/// an alias be written in the same transaction as the portal it belongs to and why no write returns
/// a generated key.
/// </para>
/// <para>
/// Neither -1 nor 0 means "absent" in any member. This aggregate is where three meanings of -1 met
/// in the legacy code - the null-integer sentinel, the identity seed of
/// <c>dbo.Portals.PortalID</c>, and an "all portals" wildcard in one procedure's predicate - and
/// they are separated here by signature rather than by convention, so an identifier parameter
/// always denotes exactly the row bearing it.
/// </para>
/// </remarks>
public interface IPortalAliasRepository
{
    /// <summary>
    /// Returns the alias bearing the supplied identifier, or <see langword="null"/> when no row
    /// bears it.
    /// </summary>
    /// <remarks>
    /// The absent case is a legitimate outcome reported as <see langword="null"/> rather than as an
    /// exception, because a caller acting on a client-supplied identifier cannot know the row
    /// exists.
    /// </remarks>
    /// <param name="portalAliasId">The alias key.</param>
    /// <param name="cancellationToken">Token observed while the operation is in flight.</param>
    /// <returns>The matching alias, or <see langword="null"/> when none matches.</returns>
    Task<PortalAlias?> GetByIdAsync(int portalAliasId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the alias of one specified portal whose stored host name is exactly the supplied
    /// value, or <see langword="null"/> when that portal has no such alias.
    /// </summary>
    /// <remarks>
    /// Portal-scoped by design - it answers "does this tenant answer on this host name", not "which
    /// tenant answers here"; use <see cref="GetAllByHttpAliasAsync"/> when the portal is what is
    /// being determined.
    /// <para>
    /// An implementation must compare host names WITHOUT regard to case. Every legacy write and
    /// read of this table lower-cased the value first, so a case-sensitive comparison would fail to
    /// find rows the legacy code found. Normalising a value before it is stored belongs to the
    /// Application layer.
    /// </para>
    /// </remarks>
    /// <param name="httpAlias">
    /// The host name to match, optionally carrying a port or a virtual path. Untrusted data
    /// throughout, and never treated as a pattern.
    /// </param>
    /// <param name="portalId">The portal whose aliases are searched.</param>
    /// <param name="cancellationToken">Token observed while the operation is in flight.</param>
    /// <returns>
    /// The matching alias, or <see langword="null"/> when that portal has no such host name.
    /// </returns>
    Task<PortalAlias?> GetByAliasAsync(string httpAlias, int portalId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns every alias across the installation whose stored host name is exactly one of the supplied
    /// candidate values.
    /// </summary>
    /// <remarks>
    /// The read tenant resolution is built on, and the reason it takes no portal identifier: the host
    /// name is what determines the portal, so no portal is known when the question is asked.
    /// <para>
    /// MIGRATION: every match is returned rather than one chosen row. The host-name column carries an
    /// installation-wide unique constraint, so a second row means the data is defective, and the legacy
    /// resolution procedure collapsed multiple matches with <c>min(PortalID)</c> - returning one row here
    /// would quietly reinstate that cross-tenant defect. What nought, one or several matches mean is a
    /// policy question for the caller.
    /// </para>
    /// <para>
    /// Several candidates are accepted in one call because a stored value may be a host name or a host
    /// name followed by path segments, as it was for a child portal beneath a shared host. A caller
    /// resolving a request therefore has a chain of possible addresses and this member answers the whole
    /// chain at once, leaving the caller to prefer the most specific match. An implementation is expected
    /// to load each matching alias with its owning portal attached, so resolution costs one round trip.
    /// </para>
    /// </remarks>
    /// <param name="httpAliasCandidates">
    /// The candidate addresses to match, exactly as supplied. Untrusted data throughout, never treated
    /// as patterns. An empty collection matches nothing and must not read the store.
    /// </param>
    /// <param name="cancellationToken">Token observed while the operation is in flight.</param>
    /// <returns>
    /// Every alias whose host name matches any candidate, in a stable order; empty when none matches.
    /// Two elements matching the SAME candidate signal an ambiguous address the caller must refuse; two
    /// matching DIFFERENT candidates are the ordinary child-portal case, resolved by preferring the
    /// longer candidate.
    /// </returns>
    Task<IReadOnlyList<PortalAlias>> GetAllByHttpAliasAsync(
        IReadOnlyList<string> httpAliasCandidates,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Reports whether any alias already claims the supplied host name, optionally ignoring one
    /// alias.
    /// </summary>
    /// <remarks>
    /// The host-name column carries an installation-wide unique constraint, so a duplicate is a
    /// refusal a caller reports rather than a database error it catches. Deliberately not
    /// portal-scoped: an alias bound to another portal is still taken. The exclusion exists so that
    /// editing an alias without changing its host name is not reported as a collision with itself.
    /// </remarks>
    /// <param name="httpAlias">
    /// The host name to test. Matched exactly and case-insensitively, as in
    /// <see cref="GetByAliasAsync"/>.
    /// </param>
    /// <param name="excludingPortalAliasId">
    /// An alias to disregard, or <see langword="null"/> to consider every row.
    /// </param>
    /// <param name="cancellationToken">Token observed while the operation is in flight.</param>
    /// <returns><see langword="true"/> when the host name is already claimed.</returns>
    Task<bool> AliasExistsAsync(string httpAlias, int? excludingPortalAliasId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the aliases belonging to exactly the portal bearing the supplied identifier.
    /// </summary>
    /// <remarks>
    /// Scoped to a single portal with NO wildcard, so applied to -1 it returns the aliases of the
    /// portal whose identifier is -1. The installation-wide question is <see cref="GetAllAsync"/>.
    /// A portal with no aliases yields an empty list, which is an ordinary result rather than an
    /// error.
    /// </remarks>
    /// <param name="portalId">The portal whose aliases are wanted.</param>
    /// <param name="cancellationToken">Token observed while the operation is in flight.</param>
    /// <returns>That portal's aliases in a stable order; empty when it has none.</returns>
    Task<IReadOnlyList<PortalAlias>> GetByPortalIdAsync(int portalId, CancellationToken cancellationToken = default);

    /// <summary>Returns the aliases of MANY portals in one read.</summary>
    /// <param name="portalIds">
    /// The portals whose aliases are wanted; no value is read as a request for every portal. An
    /// empty request asks for nothing and is answered without a round trip.
    /// </param>
    /// <param name="cancellationToken">Token observed while the operation is in flight.</param>
    /// <returns>
    /// The aliases of every named portal in a stable order, FLAT rather than grouped, so a caller
    /// groups by <see cref="PortalAlias.PortalId"/> itself. A named portal with no aliases
    /// contributes no row, as does one that does not exist.
    /// </returns>
    /// <remarks>
    /// Exists so that a paged tenant listing can fetch the aliases of the tenants on its page at a
    /// page's cost: asking per row costs one read per row, and asking <see cref="GetAllAsync"/>
    /// then grouping in memory costs the whole installation.
    /// </remarks>
    Task<IReadOnlyList<PortalAlias>> GetByPortalIdsAsync(
        IReadOnlyCollection<int> portalIds,
        CancellationToken cancellationToken = default);

    /// <summary>Returns every alias in the installation, across all portals.</summary>
    /// <remarks>
    /// The member that makes the legacy wildcard explicit, which is why it takes no parameter: a
    /// distinct question is asked by calling a distinct member rather than by passing a magic value
    /// to a shared one. Intended for installation-wide work, not for resolving one request -
    /// resolution matches through <see cref="GetAllByHttpAliasAsync"/> and must not filter this
    /// result in memory.
    /// </remarks>
    /// <param name="cancellationToken">Token observed while the operation is in flight.</param>
    /// <returns>Every alias in a stable order; empty when the installation has none.</returns>
    Task<IReadOnlyList<PortalAlias>> GetAllAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the portal reached through the alias bearing the supplied identifier, or
    /// <see langword="null"/> when no alias bears it.
    /// </summary>
    /// <remarks>
    /// A read-only traversal from this aggregate to its owner, kept here because the alias
    /// identifier is the only key the caller holds. It is the only member that yields a portal, and
    /// writing a portal, or reading one by its own identifier, is not reachable from here.
    /// </remarks>
    /// <param name="portalAliasId">The alias whose owning portal is wanted.</param>
    /// <param name="cancellationToken">Token observed while the operation is in flight.</param>
    /// <returns>
    /// The owning portal, or <see langword="null"/> when no alias bears the identifier.
    /// </returns>
    Task<Portal?> GetPortalByAliasIdAsync(int portalAliasId, CancellationToken cancellationToken = default);

    /// <summary>Stages a new alias for insertion. The unit of work commits it.</summary>
    /// <remarks>
    /// MIGRATION: returns no identifier, unlike the legacy insert whose procedure ended with
    /// <c>select SCOPE_IDENTITY()</c>. Returning a key would force this member to commit in order
    /// to have one, taking the transaction boundary away from the unit of work - and creating a
    /// portal writes the portal, its aliases, roles, tabs and modules, which must commit together
    /// or leave a half-built tenant behind.
    /// <para>
    /// The generated key is read from the entity after the unit of work has saved; before then the
    /// alias is staged and <see cref="PortalAlias.PortalAliasId"/> must not be relied upon.
    /// Assigning <see cref="PortalAlias.Portal"/> rather than <see cref="PortalAlias.PortalId"/> is
    /// how an alias is bound to a portal that is itself new.
    /// </para>
    /// </remarks>
    /// <param name="portalAlias">The alias to insert.</param>
    /// <param name="cancellationToken">Token observed while the operation is in flight.</param>
    /// <returns>A task that completes once the insertion is staged.</returns>
    Task AddAsync(PortalAlias portalAlias, CancellationToken cancellationToken = default);

    /// <summary>Stages an amended alias for update. The unit of work commits it.</summary>
    /// <remarks>
    /// The legacy update took three positional arguments; they are carried by the entity instead,
    /// so a caller cannot transpose them. The alias must already exist - amending one that does not
    /// is a programming error rather than an outcome this member reports.
    /// </remarks>
    /// <param name="portalAlias">
    /// The alias whose stored state is to be replaced by its current state.
    /// </param>
    /// <param name="cancellationToken">Token observed while the operation is in flight.</param>
    /// <returns>A task that completes once the update is staged.</returns>
    Task UpdateAsync(PortalAlias portalAlias, CancellationToken cancellationToken = default);

    /// <summary>
    /// Stages the alias bearing the supplied identifier for deletion. The unit of work commits it.
    /// </summary>
    /// <remarks>
    /// Takes the identifier rather than the row, so a caller holding only a key need not read the
    /// row first. Removing an alias unbinds a host name from its portal and does not affect the
    /// portal. Staging a deletion for an identifier no row bears completes without effect, so a
    /// caller that must distinguish "removed" from "was never there" reads the row through
    /// <see cref="GetByIdAsync"/> first.
    /// </remarks>
    /// <param name="portalAliasId">The alias to delete.</param>
    /// <param name="cancellationToken">Token observed while the operation is in flight.</param>
    /// <returns>A task that completes once the deletion is staged.</returns>
    Task DeleteAsync(int portalAliasId, CancellationToken cancellationToken = default);
}
