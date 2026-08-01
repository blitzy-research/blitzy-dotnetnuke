namespace DnnMigration.Domain.Abstractions.Services;

/// <summary>
/// Caching for the migrated DotNetNuke domain, expressed as a contract the Domain
/// layer owns and in which no caching technology is named.
/// </summary>
/// <remarks>
/// <para>
/// What this replaces. The legacy
/// <c>Library/Components/Providers/Caching/DataCache.vb</c> (317 lines) exposed
/// caching entirely through module-level shared members that reached a
/// reflection-resolved caching component through a singleton accessor. Every
/// consumer was therefore bound to one concrete implementation at compile time and
/// could not be substituted in a test. That utility is reached from 116 in-scope
/// call sites, the heaviest being the module (21), user (15), portal (13) and tab
/// (12) controllers, followed by the module-permission (11) and tab-permission (10)
/// controllers. Caching is thus the largest cross-cutting concern in this migration
/// and the only one the requirements never mention, so it is carried across
/// deliberately, as an explicit contract, rather than being allowed to disappear.
/// </para>
/// <para>
/// Implementation neutrality. Nothing here names a cache technology: the entire
/// surface is a string, a generic value, a <see cref="TimeSpan"/> and a
/// <see cref="CancellationToken"/>. Infrastructure's <c>MemoryCacheService</c>
/// implements this contract over the framework's in-memory cache and is supplied by
/// constructor injection rather than reached through a shared accessor. The Domain
/// project declares no project or package reference whatsoever, so keeping this
/// contract free of infrastructure concerns is a build-time fact rather than a
/// convention.
/// </para>
/// <para>
/// Where the legacy constants went. The thirteen legacy cache-key literals and
/// their twelve paired expiry constants stay in Infrastructure beside
/// <c>MemoryCacheService</c>, so cache behaviour remains auditable in one place,
/// and the global performance factor that every legacy caller multiplied its expiry
/// by becomes bound configuration in Application's <c>CachingOptions</c>. A caller
/// therefore computes its own <see cref="TimeSpan"/> and passes it in: this
/// contract never derives an expiry and never composes a key.
/// </para>
/// <para>
/// Shape. Four core operations and eight named invalidations. The twelve legacy
/// insert overloads collapse into a single set that takes an explicit expiry, and
/// the coarse legacy clear methods become the eight scoped invalidations declared
/// below, each standing in for exactly one of them. The legacy recursive-clear flag
/// is deliberately not reproduced: a caller states precisely what it is
/// invalidating.
/// </para>
/// <para>
/// Threading. One instance is shared across concurrent requests, so
/// implementations must be safe for concurrent use.
/// </para>
/// </remarks>
public interface ICacheService
{
    // MIGRATION: the legacy read handed back an untyped value that all 116 call
    // sites had to cast themselves, and it signalled a miss with a null reference.
    // Generics remove the cast. Absence is carried by the nullable result and never
    // by a sentinel: the legacy null-representation helpers mapped an absent integer
    // to -1 and an absent string to the empty string, and both of those are
    // legitimate stored values in the existing schema.

    /// <summary>
    /// Reads a cached value.
    /// </summary>
    /// <typeparam name="T">The type of the cached value.</typeparam>
    /// <param name="key">
    /// The cache entry key, composed by the caller. Key composition lives in
    /// Infrastructure, not here.
    /// </param>
    /// <returns>
    /// The cached value, or the null equivalent of <typeparamref name="T"/> when no
    /// live entry exists for <paramref name="key"/>.
    /// </returns>
    T? Get<T>(string key);

    // MIGRATION: twelve legacy insert overloads collapse into this one member. The
    // permutations that disappear carried a change-notification dependency, an
    // absolute expiry instant, an eviction-priority hint and a removal callback.
    // Two of them were measurably dead code: the widest overload ignored the
    // dependency, the absolute expiry, the priority and the callback outright and
    // applied only the sliding window, and the next-widest delegated straight into
    // it. Eviction priority and removal callbacks were therefore already inert in
    // the running legacy system. Under the minimal-change clause that defect is
    // recorded here rather than repaired, which is precisely why none of those four
    // parameters is reproduced.
    // MIGRATION: no expiry is derived here. The thirteen legacy key literals and
    // their twelve paired expiry constants, a uniform twenty minutes with a single
    // one-minute exception for the user entry, stay in Infrastructure beside the
    // implementation so cache behaviour remains auditable, and the global
    // performance factor that legacy callers multiplied each expiry by becomes bound
    // configuration in Application. One measured anomaly travels with that move: the
    // tab-path entry is the only legacy key carrying no paired expiry constant.

    /// <summary>
    /// Stores a value, replacing any existing entry held under the same key.
    /// </summary>
    /// <typeparam name="T">The type of the value being cached.</typeparam>
    /// <param name="key">The cache entry key, composed by the caller.</param>
    /// <param name="value">The value to cache.</param>
    /// <param name="expiration">
    /// How long the entry stays live. The caller has already applied the configured
    /// performance factor, so implementations honour this span exactly as given.
    /// </param>
    void Set<T>(string key, T value, TimeSpan expiration);

    // MIGRATION: the legacy idiom was three separate calls at every read site, a
    // read, then a database load on a miss, then an insert, so two requests arriving
    // together each performed the load. This member expresses that same idiom as a
    // single call, which lets an implementation collapse the duplicated load.
    // MIGRATION: legacy callers also skipped the database entirely when the
    // configured expiry resolved to zero, treating the load as too costly to repeat
    // per request. Application services that deliberately bypass caching on a read
    // path must record that omission rather than let it vanish silently.

    /// <summary>
    /// Reads a cached value, producing and storing it through
    /// <paramref name="factory"/> when no live entry exists.
    /// </summary>
    /// <typeparam name="T">The type of the cached value.</typeparam>
    /// <param name="key">The cache entry key, composed by the caller.</param>
    /// <param name="factory">
    /// Produces the value on a miss. It is cancellation aware because it performs
    /// I/O, typically a database read, and that is why this member is the only
    /// asynchronous one on the contract: the other eleven address an in-memory store
    /// and are synchronous by deliberate design, since wrapping a dictionary lookup
    /// in an awaitable would misrepresent its cost.
    /// </param>
    /// <param name="expiration">How long a newly produced entry stays live.</param>
    /// <param name="cancellationToken">Cancels the read and the production.</param>
    /// <returns>
    /// The cached value, or the value produced by <paramref name="factory"/> when no
    /// live entry exists for <paramref name="key"/>.
    /// </returns>
    Task<T> GetOrCreateAsync<T>(string key, Func<CancellationToken, Task<T>> factory, TimeSpan expiration, CancellationToken cancellationToken = default);

    // MIGRATION: the legacy utility paired this primitive with persistence-aware
    // twins of both the read and the remove, a property that consulted an
    // application setting to decide whether entries survived an application restart,
    // and a trailing persistence flag on every insert overload. Those existed
    // because an ASP.NET 2.0 worker process could recycle at any moment. A container
    // that is replaced rather than recycled has no equivalent need, so the entire
    // persistence surface is dropped rather than translated.

    /// <summary>
    /// Evicts a single cache entry, if one is present.
    /// </summary>
    /// <param name="key">
    /// The cache entry key, composed by the caller. Evicting an absent key is not an
    /// error.
    /// </param>
    void Remove(string key);

    // MIGRATION: the legacy clear methods were coarse and recursive. Clearing one
    // portal also swept its profile definitions, its themes, its file-system entries
    // and its lookup lists, and optionally every tab and module beneath it; clearing
    // the host optionally repeated all of that for every portal in the installation;
    // and a further helper walked every tab in a portal purely to evict module
    // permissions. None of that breadth is reproduced. The eight members below are
    // scoped and individually named, each standing in for exactly one legacy clear,
    // and the legacy recursive-clear flag is gone: a caller invokes the specific
    // invalidations it actually needs.
    // MIGRATION: -1 is no longer a wildcard. The legacy host clear passed -1 as a
    // portal identifier to mean "every portal", but the existing schema seeds portal
    // identifiers at -1 and seeds tab, module and role identifiers at 0, so both
    // values are legitimate identities here and must address exactly one owner.
    // InvalidateHost, which takes no arguments, is the single member that means
    // "everything".

    /// <summary>
    /// Invalidates the installation-wide entries: host configuration, alias
    /// resolution and everything else not scoped to a single portal.
    /// </summary>
    /// <remarks>
    /// Replaces the legacy <c>ClearHostCache</c> (L111). This is the only member
    /// meaning "everything", which is why it accepts no portal identifier.
    /// </remarks>
    void InvalidateHost();

    /// <summary>
    /// Invalidates the entries scoped to one portal.
    /// </summary>
    /// <param name="portalId">
    /// The portal identifier. Every value is a real identity, -1 and 0 included.
    /// </param>
    /// <remarks>
    /// Replaces the legacy <c>ClearPortalCache</c> (L133), less its recursive sweep
    /// of tabs, modules and excluded subject areas.
    /// </remarks>
    void InvalidatePortal(int portalId);

    /// <summary>
    /// Invalidates the tab (page) collection of one portal, including the derived
    /// tab-path lookup that the legacy clear evicted alongside it.
    /// </summary>
    /// <param name="portalId">The portal identifier owning the tabs.</param>
    /// <remarks>Replaces the legacy <c>ClearTabsCache</c> (L215).</remarks>
    void InvalidateTabs(int portalId);

    /// <summary>
    /// Invalidates the module collection of one tab.
    /// </summary>
    /// <param name="tabId">The tab identifier owning the modules.</param>
    /// <remarks>
    /// Replaces the legacy single-tab <c>ClearModuleCache</c> (L198). The legacy
    /// parameterless overload, which walked every portal and every tab in the
    /// installation, is deliberately absent.
    /// </remarks>
    void InvalidateModules(int tabId);

    /// <summary>
    /// Invalidates the permission entries of the tabs in one portal.
    /// </summary>
    /// <param name="portalId">The portal identifier owning the tabs.</param>
    /// <remarks>Replaces the legacy <c>ClearTabPermissionsCache</c> (L221).</remarks>
    void InvalidateTabPermissions(int portalId);

    /// <summary>
    /// Invalidates the module permission entries of one tab.
    /// </summary>
    /// <param name="tabId">The tab identifier whose module permissions are stale.</param>
    /// <remarks>
    /// Replaces the legacy <c>ClearModulePermissionsCache</c> (L203). Its by-portal
    /// sibling, which iterated every tab in a portal to reach this same eviction, is
    /// deliberately absent: that breadth is exactly what these named members
    /// replace.
    /// </remarks>
    void InvalidateModulePermissions(int tabId);

    /// <summary>
    /// Invalidates the cached identity of one user within one portal.
    /// </summary>
    /// <param name="portalId">The portal the membership belongs to.</param>
    /// <param name="userName">
    /// The user name, which together with the portal identifies the entry.
    /// </param>
    /// <remarks>
    /// Replaces the legacy <c>ClearUserCache</c> (L225). The legacy configuration
    /// expired this entry after one minute rather than twenty, so callers should
    /// expect it to be short lived even when it is never invalidated explicitly.
    /// </remarks>
    void InvalidateUser(int portalId, string userName);

    /// <summary>
    /// Invalidates the profile property definitions of one portal.
    /// </summary>
    /// <param name="portalId">The portal owning the definitions.</param>
    /// <remarks>Replaces the legacy <c>ClearDefinitionsCache</c> (L155).</remarks>
    void InvalidateProfileDefinitions(int portalId);

    // MIGRATION: several legacy clears have no counterpart above because their
    // subject areas sit outside the scope of this migration: the two file-system
    // clears (L159 and L164), the lookup-list clear (L168), the presentation-theme
    // key that the host clear removed, and the already-obsolete core-cache facade
    // (L298) together with the enum that selected its target. Nothing excluded
    // acquires an invalidation member here.
}
