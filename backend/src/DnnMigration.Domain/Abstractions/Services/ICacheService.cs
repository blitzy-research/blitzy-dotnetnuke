namespace DnnMigration.Domain.Abstractions.Services;

/// <summary>
/// Caching for the migrated DotNetNuke domain, expressed as a contract the Domain layer owns and in
/// which no caching technology is named.
/// </summary>
/// <remarks>
/// <para>
/// The legacy utility exposed caching through module-level shared members that reached a
/// reflection-resolved component via a singleton accessor, so every consumer was bound to one
/// implementation and none could be substituted in a test. Caching is the largest cross-cutting concern
/// in this migration and the one the requirements never mention, so it is carried across deliberately as
/// an explicit contract rather than being allowed to disappear.
/// </para>
/// <para>
/// The whole surface is a string, a generic value, a <see cref="TimeSpan"/> and a
/// <see cref="CancellationToken"/>: nothing here names a cache technology, and the Domain project
/// declares no reference of any kind, so that neutrality is a build-time fact rather than a convention.
/// Key composition and the legacy expiry constants live in Infrastructure beside the implementation, and
/// the configured performance factor is applied by the caller in Application, so this contract never
/// derives an expiry and never composes a key.
/// </para>
/// <para>
/// Four core operations plus eight named invalidations, each standing in for one coarse legacy clear. The
/// legacy recursive-clear flag is not reproduced: a caller states precisely what it is invalidating. One
/// instance is shared across concurrent requests, so implementations must be safe for concurrent use.
/// </para>
/// </remarks>
public interface ICacheService
{
    // MIGRATION: the legacy read returned an untyped value that every call site cast itself. Generics
    // remove the cast, and absence is carried by the nullable result rather than by a sentinel, because
    // the legacy absent-integer (-1) and absent-string (empty) encodings are both legitimate stored
    // values in this schema.

    /// <summary>Reads a cached value.</summary>
    /// <typeparam name="T">The type of the cached value.</typeparam>
    /// <param name="key">The cache entry key, composed by the caller.</param>
    /// <returns>
    /// The cached value, or the null equivalent of <typeparamref name="T"/> when no live entry exists.
    /// </returns>
    T? Get<T>(string key);

    // MIGRATION: twelve legacy insert overloads collapse into this one member. The permutations that
    // disappear carried a change-notification dependency, an absolute expiry instant, an eviction-priority
    // hint and a removal callback; the widest legacy overload already ignored all four and applied only
    // the sliding window, so those parameters were inert in the running system. Under the minimal-change
    // clause that defect is recorded rather than repaired, which is why none of them is reproduced.

    /// <summary>Stores a value, replacing any existing entry held under the same key.</summary>
    /// <typeparam name="T">The type of the value being cached.</typeparam>
    /// <param name="key">The cache entry key, composed by the caller.</param>
    /// <param name="value">The value to cache.</param>
    /// <param name="expiration">
    /// How long the entry stays live. Any configured performance factor has already been applied by the
    /// caller, so implementations honour this span exactly as given.
    /// </param>
    void Set<T>(string key, T value, TimeSpan expiration);

    // MIGRATION: the legacy idiom was three calls at every read site - read, load on a miss, insert - so
    // two requests arriving together each performed the load. Expressing it as one call lets an
    // implementation collapse the duplicated load. Legacy callers also skipped the load entirely when the
    // configured expiry resolved to zero; an Application service that deliberately bypasses caching on a
    // read path must record that omission rather than let it vanish silently.

    /// <summary>
    /// Reads a cached value, producing and storing it through <paramref name="factory"/> when no live
    /// entry exists.
    /// </summary>
    /// <typeparam name="T">The type of the cached value.</typeparam>
    /// <param name="key">The cache entry key, composed by the caller.</param>
    /// <param name="factory">
    /// Produces the value on a miss. It is cancellation aware because it performs I/O, which is why this
    /// is the only asynchronous member: the rest address an in-memory store, and wrapping a dictionary
    /// lookup in an awaitable would misrepresent its cost.
    /// </param>
    /// <param name="expiration">How long a newly produced entry stays live.</param>
    /// <param name="cancellationToken">
    /// Withdraws this caller from the operation. It does not necessarily cancel production: an
    /// implementation that coalesces concurrent misses onto one shared factory invocation must let that
    /// invocation run to completion for the callers still waiting, so cancelling here abandons the await
    /// rather than the load, and the produced value may still be cached.
    /// </param>
    /// <returns>
    /// The cached value, or the value produced by <paramref name="factory"/> when no live entry exists.
    /// </returns>
    Task<T> GetOrCreateAsync<T>(string key, Func<CancellationToken, Task<T>> factory, TimeSpan expiration, CancellationToken cancellationToken = default);

    // MIGRATION: the legacy utility also offered persistence-aware twins of the read and the remove, plus
    // a trailing persistence flag on every insert, because an ASP.NET 2.0 worker process could recycle at
    // any moment. A container that is replaced rather than recycled has no equivalent need, so the entire
    // persistence surface is dropped rather than translated.

    /// <summary>Evicts a single cache entry, if one is present.</summary>
    /// <param name="key">The cache entry key, composed by the caller. Evicting an absent key is not an error.</param>
    void Remove(string key);

    // MIGRATION: the legacy clears were coarse and recursive - clearing one portal also swept unrelated
    // subject areas and optionally every tab and module beneath it. The eight scoped members below each
    // stand in for exactly one of them, and the recursive flag is gone.
    //
    // MIGRATION: -1 is no longer a wildcard. The legacy host clear passed -1 to mean "every portal", but
    // this schema seeds portal identifiers at -1 and tab, module and role identifiers at 0, so every value
    // addresses exactly one owner here. InvalidateHost, which takes no arguments, is the only member that
    // means "everything".

    /// <summary>
    /// Invalidates the installation-wide entries: host configuration, alias resolution and everything
    /// else not scoped to a single portal.
    /// </summary>
    /// <remarks>The only member meaning "everything", which is why it accepts no portal identifier.</remarks>
    void InvalidateHost();

    /// <summary>Invalidates the entries scoped to one portal.</summary>
    /// <param name="portalId">The portal identifier. Every value is a real identity, -1 and 0 included.</param>
    /// <remarks>Narrower than the legacy clear, which also swept tabs, modules and excluded subject areas.</remarks>
    void InvalidatePortal(int portalId);

    /// <summary>
    /// Invalidates the tab (page) collection of one portal, including the derived tab-path lookup the
    /// legacy clear evicted alongside it.
    /// </summary>
    /// <param name="portalId">The portal identifier owning the tabs.</param>
    void InvalidateTabs(int portalId);

    /// <summary>Invalidates the module collection of one tab.</summary>
    /// <param name="tabId">The tab identifier owning the modules.</param>
    /// <remarks>
    /// Scoped to one tab by design; the legacy parameterless overload walked every portal and every tab.
    /// </remarks>
    void InvalidateModules(int tabId);

    /// <summary>Invalidates the permission entries of the tabs in one portal.</summary>
    /// <param name="portalId">The portal identifier owning the tabs.</param>
    void InvalidateTabPermissions(int portalId);

    /// <summary>Invalidates the module permission entries of one tab.</summary>
    /// <param name="tabId">The tab identifier whose module permissions are stale.</param>
    /// <remarks>
    /// The legacy by-portal sibling iterated every tab to reach this same eviction; that breadth is what
    /// these named members replace.
    /// </remarks>
    void InvalidateModulePermissions(int tabId);

    /// <summary>Invalidates the cached identity of one user within one portal.</summary>
    /// <param name="portalId">The portal the membership belongs to.</param>
    /// <param name="userName">The user name, which together with the portal identifies the entry.</param>
    /// <remarks>
    /// The legacy configuration expired this entry after one minute rather than twenty, so callers should
    /// expect it to be short lived even when it is never invalidated explicitly.
    /// </remarks>
    void InvalidateUser(int portalId, string userName);

    /// <summary>Invalidates the profile property definitions of one portal.</summary>
    /// <param name="portalId">The portal owning the definitions.</param>
    void InvalidateProfileDefinitions(int portalId);

    // MIGRATION: several legacy clears have no counterpart here because their subject areas are out of
    // scope - the file-system clears, the lookup-list clear, the presentation-theme key and the obsolete
    // core-cache facade. Nothing excluded acquires an invalidation member.
}
