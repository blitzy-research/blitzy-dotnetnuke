using DnnMigration.Domain.Entities;

namespace DnnMigration.Domain.Abstractions.Repositories;

// MIGRATION: this contract realises the eight-member 'portal alias block of the legacy abstract data
// provider (Library/Components/Providers/Data/DataProvider.vb lines 354-362) as one aggregate-shaped
// repository. That class was 397 lines carrying 269 MustOverride members reached through a
// reflection-created static singleton (lines 29-50). None of the provider metadata constants, the
// reflective activation, the singleton accessor, the raw procedure execution or the reader hydration is
// translated: the surface is decomposed by aggregate and resolved by dependency injection instead. Seven
// of the eight members appear below; the eighth is deliberately omitted for the reason recorded further
// down.

// MIGRATION: legacy GetPortalAliasByPortalID (DataProvider.vb:L356) overloaded -1 as an "all portals"
// wildcard inside the SQL predicate itself ("where (PortalID = @PortalID or @PortalID = -1)",
// 02.02.02.SqlDataProvider:L3861), reached via PortalAliasController.GetPortalAliases() at L86-L88.
// Portals.PortalID is IDENTITY(-1,1), so -1 is also a real portal identifier. The wildcard is therefore
// surfaced as an explicit GetAllAsync; GetByPortalIdAsync
// (-1) means the portal whose id is -1.

// MIGRATION: legacy UpdatePortalAlias (DataProvider.vb:L359) is omitted. Despite its name it executes
// UpdatePortalAliasOnInstall (SqlDataProvider.vb:L1312-L1314), which rewrites the placeholder row WHERE
// HTTPAlias = '_default'; its only caller is the fresh-install branch at PortalSettings.vb:L1128, and the
// installer is out of scope. The per-row update is UpdatePortalAliasInfo (L360), surfaced here as
// UpdateAsync.

// MIGRATION: the substring-to-exact change in tenant resolution is NOT decided by this contract. The
// legacy resolution procedure GetPortalSettings matched with "where PortalAlias like '%' + @PortalAlias +
// '%'", under which one tenant's host name that was a substring of another's could resolve to the wrong
// tenant. That change belongs to Api/Middleware/PortalAliasResolutionMiddleware.cs and is recorded in
// MIGRATION_NOTES.md. All this contract does is refuse to encode substring semantics: no member accepts a
// pattern, a fragment or a search term, so no implementation can widen a match without changing the
// contract first. The portal-alias block's own lookup was already exact - 02.02.02.SqlDataProvider:L3846
// reads "where HTTPAlias = @HTTPAlias and PortalID = @PortalID" - so preserving exact matching here
// preserves measured behaviour rather than altering it.

// MIGRATION: the pre-generics companion PortalAliasCollection.vb produces no target file. It derived from
// DictionaryBase to key aliases by host name and exposed Contains and HasKeys; the legacy controller also
// returned the same rows as an untyped ArrayList from a second member. Both untyped shapes collapse into
// IReadOnlyList<PortalAlias>, and no hand-rolled collection type is reintroduced.

// MIGRATION: caching is absent by design. The legacy controller cleared the host cache on every write
// (PortalAliasController.vb lines 29, 35 and 95) and the resolution path evicted a named key
// (PortalSettings.vb:L1131). Cache reads, writes and evictions belong to the caching service in
// Infrastructure, so no member here takes a cache flag and no member promises a cached read.

/// <summary>
/// Reads and writes the <c>dbo.PortalAlias</c> rows that bind a host name to a portal.
/// </summary>
/// <remarks>
/// <para>
/// This is the persistence contract for the alias aggregate and nothing more. It is the narrow,
/// aggregate-shaped replacement for one block of a 269-member provider, and it is deliberately not a
/// tenant-resolution service: choosing which host name to try, retrying with a prefix, deciding what an
/// unmatched or ambiguous host means, and publishing the result as request state all belong to the
/// resolution middleware and the request-scoped tenant context. Everything here is a read or a staged
/// write.
/// </para>
/// <para>
/// EVERY MEMBER IS ASYNCHRONOUS AND CANCELLABLE. Each returns a <see cref="Task"/>, carries a trailing
/// cancellation token, and has no synchronous counterpart, so no caller can block a request thread on
/// database work.
/// </para>
/// <para>
/// WRITES ARE STAGED, NEVER COMMITTED. <see cref="AddAsync"/>, <see cref="UpdateAsync"/> and
/// <see cref="DeleteAsync"/> record an intention; the unit of work commits it. That is what allows an
/// alias to be written in the same transaction as the portal it belongs to, and it is why no write member
/// returns a generated key.
/// </para>
/// <para>
/// NEITHER -1 NOR 0 MEANS "ABSENT" IN ANY MEMBER OF THIS CONTRACT, and this aggregate is the sharpest
/// illustration of that rule in the whole schema. Three unrelated meanings of -1 met in the legacy alias
/// code: it was the null-integer sentinel, it was the identity seed of <c>dbo.Portals.PortalID</c> and so
/// a genuine portal key, and it was an "all portals" wildcard baked into one procedure's own predicate.
/// The legacy null test could not tell the three apart. Here they are separated by signature rather than
/// by convention - the wildcard has its own member - so an identifier parameter always denotes exactly
/// the row bearing it. Restoring sentinel values for an external contract is a concern of the DTO and API
/// boundary and never reaches these signatures.
/// </para>
/// <para>
/// HOST NAMES ARE MATCHED EXACTLY AND NEVER AS PATTERNS. No member accepts a fragment, a pattern or a
/// search term, so a supplied value is data throughout and no character within it can broaden what
/// matches.
/// </para>
/// </remarks>
public interface IPortalAliasRepository
{
    /// <summary>
    /// Returns the alias bearing the supplied identifier, or <see langword="null"/> when no row bears it.
    /// </summary>
    /// <remarks>
    /// Ports <c>GetPortalAliasByPortalAliasID</c> (DataProvider.vb:L357), which returned a reader over
    /// nought or one row and was hydrated by the reflection-based helper the legacy controller used. The
    /// absent case is a legitimate outcome reported as <see langword="null"/> rather than as an exception,
    /// because a caller acting on a client-supplied identifier cannot know in advance that the row exists.
    /// </remarks>
    /// <param name="portalAliasId">
    /// The alias key. Every value denotes exactly the row bearing it; this identity seeds at 1, but no
    /// value is read as a request for "any" or "no" row.
    /// </param>
    /// <param name="cancellationToken">Propagates notification that the operation should be abandoned.</param>
    /// <returns>The matching alias, or <see langword="null"/> when none matches.</returns>
    Task<PortalAlias?> GetByIdAsync(int portalAliasId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the alias of one specified portal whose stored host name is exactly the supplied value, or
    /// <see langword="null"/> when that portal has no such alias.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Ports <c>GetPortalAlias</c> (DataProvider.vb:L355). Both of the legacy parameters are preserved
    /// because both were load-bearing: the procedure filtered on the host name AND the portal
    /// (02.02.02.SqlDataProvider:L3846), which makes this a portal-scoped question - "does this tenant
    /// answer on this host name" - and not a resolution of a host name to a tenant. Use
    /// <see cref="GetAllByHttpAliasAsync"/> when the portal is what is being determined.
    /// </para>
    /// <para>
    /// Matching is by equality on the whole stored value: never by prefix, suffix, fragment or pattern.
    /// </para>
    /// <para>
    /// An implementation must compare host names without regard to case. Every legacy write and every
    /// legacy read of this table lower-cased the value first - see PortalAliasController.vb lines 31, 52,
    /// 76 and 97, and PortalSettings.vb:L1118 - so a case-sensitive comparison here would fail to find
    /// rows the legacy code found. Host names are case-insensitive by specification, so this preserves
    /// measured behaviour rather than relaxing it. Normalising a value before it is stored belongs to the
    /// Application layer, and this contract neither performs that normalisation nor exposes a normalised
    /// member.
    /// </para>
    /// </remarks>
    /// <param name="httpAlias">
    /// The host name to match, optionally carrying a port or a virtual path. Untrusted data throughout,
    /// and never treated as a pattern.
    /// </param>
    /// <param name="portalId">
    /// The portal whose aliases are searched. Both -1 and 0 are genuine portal keys in this schema, so
    /// each denotes that portal and neither widens the query.
    /// </param>
    /// <param name="cancellationToken">Propagates notification that the operation should be abandoned.</param>
    /// <returns>The matching alias, or <see langword="null"/> when that portal has no such host name.</returns>
    Task<PortalAlias?> GetByAliasAsync(string httpAlias, int portalId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns every alias across the installation whose stored host name is exactly one of the supplied
    /// candidate values.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The read that tenant resolution is built on, and the reason it takes no portal identifier: the host
    /// name is what determines the portal, so no portal is known when the question is asked. That is
    /// exactly why <see cref="GetByAliasAsync"/> cannot serve this purpose - it is portal-scoped.
    /// </para>
    /// <para>
    /// EVERY MATCH IS RETURNED, AND THAT IS THE POINT. The schema carries an installation-wide unique
    /// constraint on the host-name column, so a well-formed installation yields at most one row and a
    /// second row means the data is defective. Returning the matches rather than one chosen row is what
    /// lets the caller refuse an ambiguous host instead of serving one tenant's content under another
    /// tenant's name. The legacy resolution procedure did the opposite - it collapsed multiple matches
    /// with <c>min(PortalID)</c> - and reproducing a single-row return here would quietly reinstate that
    /// cross-tenant defect. Deciding what nought, one, or more than one match means is the caller's
    /// responsibility, because it is a policy question rather than a persistence one.
    /// </para>
    /// <para>
    /// SEVERAL CANDIDATES ARE ACCEPTED IN ONE CALL, and that is what makes a virtual-path alias resolvable
    /// without a round trip per candidate. The legacy product let a child portal be addressed by a path
    /// segment beneath a shared host, so the stored value can be a host name OR a host name followed by one
    /// or more path segments - <c>Signup.ascx.vb</c> L232-L236 composes exactly that, and the legacy
    /// request-side counterpart, <c>Globals.GetDomainName</c> L563 onward, walked the request's path
    /// segments to build the value it matched against this column. A caller resolving a request therefore
    /// has a CHAIN of possible addresses, longest first, and asking for them one at a time would mean up to
    /// one query per path segment on every request. This member answers the whole chain at once and leaves
    /// the caller to prefer the most specific match, which is the same preference the legacy walk expressed
    /// by stopping at the first recognised directory.
    /// </para>
    /// <para>
    /// Matching is exact and case-insensitive, on the same evidence as <see cref="GetByAliasAsync"/> - never
    /// a prefix match or a pattern in the store, because the candidate chain is computed by the caller and
    /// each element is compared whole. An implementation is expected to load each matching alias with its
    /// owning portal attached, so that resolving a tenant costs one round trip.
    /// </para>
    /// </remarks>
    /// <param name="httpAliasCandidates">
    /// The candidate addresses to match, exactly as supplied by the caller. Untrusted data throughout, and
    /// never treated as patterns. An empty collection matches nothing and must not read the store.
    /// </param>
    /// <param name="cancellationToken">Propagates notification that the operation should be abandoned.</param>
    /// <returns>
    /// Every alias whose host name matches ANY candidate, in a stable order; empty when none matches. Two
    /// elements matching the SAME candidate signal an ambiguous address the caller must refuse rather than
    /// resolve; two elements matching DIFFERENT candidates are the ordinary case for a child portal beneath
    /// a parent, and the caller resolves that by preferring the longer candidate.
    /// </returns>
    Task<IReadOnlyList<PortalAlias>> GetAllByHttpAliasAsync(
        IReadOnlyList<string> httpAliasCandidates,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Reports whether any alias already claims the supplied host name, optionally ignoring one alias.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Exists because the host-name column carries an installation-wide unique constraint, so a duplicate
    /// is a refusal a caller should report rather than a database error it should catch. The question is
    /// deliberately not portal-scoped: uniqueness spans every tenant, so an alias already bound to another
    /// portal is still taken.
    /// </para>
    /// <para>
    /// The exclusion exists so that editing an alias without changing its host name is not reported as a
    /// collision with itself. Supplying no exclusion asks the unrestricted question, which is what a
    /// create does.
    /// </para>
    /// <para>
    /// This member answers a question; it does not decide anything. Whether a taken host name is an error,
    /// and what the caller is told about it, belongs to the Application layer.
    /// </para>
    /// </remarks>
    /// <param name="httpAlias">
    /// The host name to test. Matched exactly and case-insensitively, on the same evidence as
    /// <see cref="GetByAliasAsync"/>.
    /// </param>
    /// <param name="excludingPortalAliasId">
    /// An alias to disregard, or <see langword="null"/> to consider every row. A value denotes exactly the
    /// row bearing it.
    /// </param>
    /// <param name="cancellationToken">Propagates notification that the operation should be abandoned.</param>
    /// <returns><see langword="true"/> when the host name is already claimed.</returns>
    Task<bool> AliasExistsAsync(string httpAlias, int? excludingPortalAliasId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the aliases belonging to exactly the portal bearing the supplied identifier.
    /// </summary>
    /// <remarks>
    /// <para>
    /// One half of the split described at the top of this file. This member is scoped to a single portal
    /// and has no wildcard: -1 and 0 are genuine portal keys in this schema, so
    /// <c>GetByPortalIdAsync</c> applied to -1 returns the aliases of the portal whose identifier is -1
    /// and nothing else. Asking for every alias in the installation is a different question, and it has its
    /// own member: <see cref="GetAllAsync"/>.
    /// </para>
    /// <para>
    /// A portal with no aliases yields an empty list. That is an ordinary result and not an error: it is
    /// simply a tenant no host name currently reaches.
    /// </para>
    /// </remarks>
    /// <param name="portalId">
    /// The portal whose aliases are wanted. Every value denotes exactly the portal bearing it, and none is
    /// interpreted as a request for every portal.
    /// </param>
    /// <param name="cancellationToken">Propagates notification that the operation should be abandoned.</param>
    /// <returns>That portal's aliases in a stable order; empty when it has none.</returns>
    Task<IReadOnlyList<PortalAlias>> GetByPortalIdAsync(int portalId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns every alias in the installation, across all portals.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The other half of the split, and the member that makes the legacy wildcard explicit. The intention
    /// "every alias regardless of portal" used to be expressed by passing -1 to the portal-scoped read,
    /// which is why it takes no parameter here: a distinct question is asked by calling a distinct member,
    /// not by supplying a magic value to a shared one. A reader of a call site can therefore see which of
    /// the two operations was intended without knowing that a particular integer is special.
    /// </para>
    /// <para>
    /// Intended for installation-wide work - presenting the whole alias inventory, or grouping aliases by
    /// portal for a listing - rather than for resolving one request. Resolution matches a single host name
    /// through <see cref="GetAllByHttpAliasAsync"/> and must not filter this result in memory.
    /// </para>
    /// </remarks>
    /// <param name="cancellationToken">Propagates notification that the operation should be abandoned.</param>
    /// <returns>Every alias in a stable order; empty when the installation has none.</returns>
    Task<IReadOnlyList<PortalAlias>> GetAllAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the portal reached through the alias bearing the supplied identifier, or
    /// <see langword="null"/> when no alias bears it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Ports <c>GetPortalByPortalAliasID</c> (DataProvider.vb:L358), whose procedure joined the portal to
    /// the alias and projected the portal's columns (02.02.02.SqlDataProvider:L3831). It is a read-only
    /// traversal from this aggregate to its owner, kept here because the legacy provider grouped it with
    /// the alias block and because the alias identifier is the only key the caller holds.
    /// </para>
    /// <para>
    /// This is the ONLY member of this contract that yields a portal, and it is deliberately read-only.
    /// Creating, amending or removing a portal, and reading one by its own identifier, belong to the
    /// portal repository; none of that is reachable from here.
    /// </para>
    /// </remarks>
    /// <param name="portalAliasId">The alias whose owning portal is wanted.</param>
    /// <param name="cancellationToken">Propagates notification that the operation should be abandoned.</param>
    /// <returns>The owning portal, or <see langword="null"/> when no alias bears the identifier.</returns>
    Task<Portal?> GetPortalByAliasIdAsync(int portalAliasId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Stages a new alias for insertion. The unit of work commits it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// RETURNS NO IDENTIFIER, DELIBERATELY. Legacy <c>AddPortalAlias</c> (DataProvider.vb:L361) returned
    /// an Integer only because its procedure ended with <c>select SCOPE_IDENTITY()</c>
    /// (02.02.02.SqlDataProvider:L4080) - the insert and the key retrieval were one indivisible statement.
    /// Returning a key here would require this member to commit in order to have one to return, which
    /// would take the commit decision away from the unit of work and give every insert its own
    /// transaction.
    /// </para>
    /// <para>
    /// THAT WOULD BREAK A REAL TRANSACTION, not a hypothetical one. Creating a portal writes the portal,
    /// its aliases, its roles, its tabs and its modules; this table is one of those five. Those writes
    /// must commit together or not at all, and a per-insert commit would leave a half-built tenant behind
    /// on failure - which is the defect the legacy sequence had, since it committed each statement
    /// separately.
    /// </para>
    /// <para>
    /// The generated key is therefore read from the entity after the unit of work has saved: at that point
    /// <see cref="PortalAlias.PortalAliasId"/> holds it. Before then the alias is staged and has no key,
    /// and its identifier must not be read or relied upon.
    /// </para>
    /// <para>
    /// Assigning <see cref="PortalAlias.Portal"/> rather than <see cref="PortalAlias.PortalId"/> is how an
    /// alias is bound to a portal that is itself new and whose key does not exist yet.
    /// </para>
    /// </remarks>
    /// <param name="portalAlias">The alias to insert.</param>
    /// <param name="cancellationToken">Propagates notification that the operation should be abandoned.</param>
    /// <returns>A task that completes once the insertion is staged.</returns>
    Task AddAsync(PortalAlias portalAlias, CancellationToken cancellationToken = default);

    /// <summary>
    /// Stages an amended alias for update. The unit of work commits it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Ports <c>UpdatePortalAliasInfo</c> (DataProvider.vb:L360), which despite its name is the real
    /// per-row update: it executed the procedure that set the host name of one identified row
    /// (02.02.02.SqlDataProvider:L4096), and it is what the alias editing screen reached. The three
    /// positional arguments it took are carried by the entity instead, so a caller cannot transpose them.
    /// </para>
    /// <para>
    /// Staged rather than committed, for the same reason as <see cref="AddAsync"/>: the unit of work owns
    /// the transaction boundary. The alias must already exist; amending one that does not is a programming
    /// error rather than an outcome this member reports.
    /// </para>
    /// </remarks>
    /// <param name="portalAlias">The alias whose stored state is to be replaced by its current state.</param>
    /// <param name="cancellationToken">Propagates notification that the operation should be abandoned.</param>
    /// <returns>A task that completes once the update is staged.</returns>
    Task UpdateAsync(PortalAlias portalAlias, CancellationToken cancellationToken = default);

    /// <summary>
    /// Stages the alias bearing the supplied identifier for deletion. The unit of work commits it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Ports <c>DeletePortalAlias</c> (DataProvider.vb:L362), which took the identifier rather than the
    /// row, and that shape is preserved: a caller holding only a key need not read the row first in order
    /// to discard it.
    /// </para>
    /// <para>
    /// Removing an alias unbinds a host name from its portal; it does not affect the portal itself.
    /// Staging a deletion for an identifier no row bears completes without effect, so a caller that must
    /// distinguish "removed" from "was never there" reads the row through
    /// <see cref="GetByIdAsync"/> first.
    /// </para>
    /// </remarks>
    /// <param name="portalAliasId">The alias to delete.</param>
    /// <param name="cancellationToken">Propagates notification that the operation should be abandoned.</param>
    /// <returns>A task that completes once the deletion is staged.</returns>
    Task DeleteAsync(int portalAliasId, CancellationToken cancellationToken = default);
}
