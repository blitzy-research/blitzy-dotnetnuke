namespace DnnMigration.Domain.Abstractions.Services;

/// <summary>
/// Caching for the migrated DotNetNuke domain, expressed as a contract the Domain layer owns and in which
/// no caching technology is named.
/// </summary>
/// <remarks>
/// Four core operations plus eight named invalidations, each standing in for one coarse legacy clear. The
/// legacy recursive-clear flag is not reproduced: a caller states precisely what it is invalidating.
/// </remarks>
public interface ICacheService
{
    // The legacy read returned an untyped value that every call site cast itself.

    /// <summary>Reads a cached value.</summary>
    /// <typeparam name="T">The type of the cached value.</typeparam>
    /// <param name="key">The cache entry key, composed by the caller.</param>
    /// <returns>
    /// The cached value, or the null equivalent of <typeparamref name="T"/> when no live entry exists.
    /// </returns>
    T? Get<T>(string key);

    // Twelve legacy insert overloads collapse into this one member.

    /// <summary>Stores a value, replacing any existing entry held under the same key.</summary>
    /// <typeparam name="T">The type of the value being cached.</typeparam>
    /// <param name="key">The cache entry key, composed by the caller.</param>
    /// <param name="value">The value to cache.</param>
    /// <param name="expiration">How long the entry stays live.</param>
    void Set<T>(string key, T value, TimeSpan expiration);

    /// <summary>
    /// Reads a cached value, producing and storing it through <paramref name="factory"/> when no live entry
    /// exists.
    /// </summary>
    /// <typeparam name="T">The type of the cached value.</typeparam>
    /// <param name="key">The cache entry key, composed by the caller.</param>
    /// <param name="factory">Produces the value on a miss.</param>
    /// <param name="expiration">How long a newly produced entry stays live.</param>
    /// <param name="cancellationToken">Withdraws this caller from the operation.</param>
    /// <returns>
    /// The cached value, or the value produced by <paramref name="factory"/> when no live entry exists.
    /// </returns>
    Task<T> GetOrCreateAsync<T>(string key, Func<CancellationToken, Task<T>> factory, TimeSpan expiration, CancellationToken cancellationToken = default);

    // MIGRATION: the legacy utility also offered persistence-aware twins of the read and the remove, plus a
    // trailing persistence flag on every insert, because an ASP.NET 2.0 worker process could recycle at any
    // moment.

    /// <summary>Evicts a single cache entry, if one is present.</summary>
    /// <param name="key">The cache entry key, composed by the caller.</param>
    void Remove(string key);

    /// <summary>Evicts every entry whose key begins with <paramref name="keyPrefix"/>.</summary>
    /// <param name="keyPrefix">The invariant leading part of a key family.</param>
    /// <remarks>
    /// This exists so that a caller holding a key FAMILY - one entry per module definition, per tenant, per
    /// anything - can invalidate the family without first discovering every value the varying part has
    /// taken. The alternative is what it replaces: a post-commit database read performed purely to
    /// enumerate cache keys, which made a completed, durable mutation able to fail afterwards.
    /// </remarks>
    /// <exception cref="ArgumentException">
    /// <paramref name="keyPrefix"/> is <see langword="null"/>, empty or white space.
    /// </exception>
    void RemoveByPrefix(string keyPrefix);

    // MIGRATION: -1 is no longer a wildcard. The legacy host clear passed -1 to mean "every portal", but
    // this schema seeds portal identifiers at -1 and tab, module and role identifiers at 0, so every value
    // addresses exactly one owner here.

    /// <summary>
    /// Invalidates the installation-wide entries: host configuration, alias resolution and everything else
    /// not scoped to a single portal.
    /// </summary>
    void InvalidateHost();

    /// <summary>Invalidates the entries scoped to one portal.</summary>
    /// <param name="portalId">The portal identifier.</param>
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
