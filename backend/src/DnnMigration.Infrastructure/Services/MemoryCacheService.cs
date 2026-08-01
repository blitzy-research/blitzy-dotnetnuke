// MIGRATION: Replaces Library/Components/Providers/Caching/DataCache.vb (317 lines) - the
// module-level shared utility that 116 in-scope legacy call sites reached statically and which
// forwarded every operation to CachingProvider.Instance, a reflection-resolved singleton. Nothing
// here is reachable statically: the container holds one instance and hands it to consumers by
// constructor injection, which is what makes cache behaviour substitutable in a test.
using System.Collections.Concurrent;
using DnnMigration.Application.Options;
using DnnMigration.Domain.Abstractions.Services;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace DnnMigration.Infrastructure.Services;

/// <summary>
/// In-memory implementation of <see cref="ICacheService"/> over the framework's
/// <see cref="IMemoryCache"/>, carrying the legacy DotNetNuke cache-key vocabulary and expiry
/// baselines so that cache behaviour stays auditable in one place.
/// </summary>
/// <remarks>
/// <para>
/// Lifetime. Registered as a singleton, after the container's in-memory cache. It therefore
/// captures nothing scoped: no repository, no database context, no service provider, no HTTP
/// abstraction. Its only collaborators are the cache the container owns and an immutable integer
/// read once from configuration.
/// </para>
/// <para>
/// It is not a query layer. No member of this class reads a database, and the eight invalidations
/// in particular resolve every key they evict from an in-process registry rather than from a
/// query. That is a deliberate reversal of the legacy design and is the single most consequential
/// behavioural difference recorded below.
/// </para>
/// <para>
/// Threading. Reads go straight to the thread-safe cache. Every mutation - a write, an eviction
/// or an invalidation - runs under one private monitor, which is what makes the paired
/// register-then-write and unregister-then-evict operations indivisible with respect to each
/// other. The monitor is never held across an <see langword="await"/>.
/// </para>
/// <para>
/// Ownership of the injected cache belongs to the container, so this class implements neither
/// <see cref="IDisposable"/> nor <see cref="IAsyncDisposable"/> and never disposes it.
/// </para>
/// </remarks>
internal sealed class MemoryCacheService : ICacheService
{
    // ---------------------------------------------------------------------------------------
    // LEGACY CACHE KEYS AND BASE EXPIRIES
    //
    // Ported verbatim from Library/Components/Providers/Caching/DataCache.vb:L44-L79. The
    // identifiers, the literals, the brace placeholders and the numbers are all reproduced
    // exactly: they are the contract by which a running installation's cache can still be
    // reasoned about after the migration. Each expiry is a BASE value in MINUTES that a caller
    // multiplies by CachingOptions.PerformanceMultiplier before passing the product in.
    //
    // They are internal rather than public because they are consumed by sibling Infrastructure
    // services in this same assembly. The Application services that compose keys sit in a
    // project that cannot reference Infrastructure, so they cannot reach these constants - a
    // real, pre-existing cross-layer gap that is reported rather than papered over by widening
    // ICacheService or by adding a second key-carrying file.
    //
    // MIGRATION: Deliberately absent, because the features that owned them are out of scope:
    // the folder and folder-permission keys (DataCache.vb:L66, L69), the lookup-list key (L72),
    // the presentation-theme keys the legacy host and portal clears removed (L119, L137), the
    // secure host-settings key (Library/Components/Host/HostSettings.vb:L74, L92) and the
    // obsolete core-cache facade together with the enum that selected its target (L32-L36,
    // L298). Nothing excluded acquires a constant here.
    // ---------------------------------------------------------------------------------------

    /// <summary>Key of the tab-to-portal lookup dictionary. Installation wide.</summary>
    internal const string PortalDictionaryCacheKey = "PortalDictionary";

    /// <summary>Base expiry, in minutes, of the tab-to-portal lookup dictionary.</summary>
    internal const int PortalDictionaryTimeOut = 20;

    /// <summary>Key template of a single portal, formatted with the portal identifier.</summary>
    internal const string PortalCacheKey = "Portal{0}";

    /// <summary>Base expiry, in minutes, of a single portal.</summary>
    internal const int PortalCacheTimeOut = 20;

    /// <summary>Key template of a portal's tab collection, formatted with the portal identifier.</summary>
    internal const string TabCacheKey = "Tabs{0}";

    /// <summary>Base expiry, in minutes, of a portal's tab collection.</summary>
    internal const int TabCacheTimeOut = 20;

    /// <summary>
    /// Key of the derived tab-path lookup. Installation wide, and the one legacy key with no
    /// paired expiry constant: DataCache.vb:L52 declares the key and no timeout beside it,
    /// and Library/Components/Tabs/TabController.vb:L1128 writes it with no expiry at all.
    /// </summary>
    internal const string TabPathCacheKey = "TabPathDictionary";

    /// <summary>Key template of a portal's tab permissions, formatted with the portal identifier.</summary>
    internal const string TabPermissionCacheKey = "TabPermissions{0}";

    /// <summary>Base expiry, in minutes, of a portal's tab permissions.</summary>
    internal const int TabPermissionCacheTimeOut = 20;

    /// <summary>Key template of a tab's module collection, formatted with the TAB identifier.</summary>
    internal const string TabModuleCacheKey = "TabModules{0}";

    /// <summary>Base expiry, in minutes, of a tab's module collection.</summary>
    internal const int TabModuleCacheTimeOut = 20;

    /// <summary>Key template of a tab's module permissions, formatted with the TAB identifier.</summary>
    internal const string ModulePermissionCacheKey = "ModulePermissions{0}";

    /// <summary>Base expiry, in minutes, of a tab's module permissions.</summary>
    internal const int ModulePermissionCacheTimeOut = 20;

    /// <summary>
    /// Key template of a portal's module dictionary, formatted with the PORTAL identifier -
    /// measured at Library/Components/Modules/ModuleController.vb:L961, and easily confused with
    /// the tab-keyed template above.
    /// </summary>
    internal const string ModuleCacheKey = "Modules{0}";

    /// <summary>Base expiry, in minutes, of a portal's module dictionary.</summary>
    internal const int ModuleCacheTimeOut = 20;

    /// <summary>Key template of a portal's profile definitions, formatted with the portal identifier.</summary>
    internal const string ProfileDefinitionsCacheKey = "ProfileDefinitions{0}";

    /// <summary>Base expiry, in minutes, of a portal's profile definitions.</summary>
    internal const int ProfileDefinitionsCacheTimeOut = 20;

    /// <summary>
    /// Key template of one user's cached identity within one portal, formatted with the portal
    /// identifier and then the user name. Both pipe delimiters, the casing and the two
    /// placeholders are load bearing: this exact literal is what an existing installation's
    /// cache is keyed by.
    /// </summary>
    internal const string UserCacheKey = "UserInfo|{0}|{1}";

    /// <summary>
    /// Base expiry, in minutes, of one user's cached identity. One, not twenty - the single
    /// legacy exception (DataCache.vb:L79), so at the default multiplier of three the effective
    /// duration is three minutes.
    /// </summary>
    internal const int UserCacheTimeOut = 1;

    // ---------------------------------------------------------------------------------------
    // LEGACY AD-HOC KEYS
    //
    // The legacy source composed these inline as string literals rather than declaring named
    // constants, so the constant names here are new while the literals are exact.
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// Key of the host settings table. Installation wide. Library/Components/Host/HostSettings.vb
    /// writes it at L62 with no expiry, so a caller must not assume the multiplier reaches it.
    /// </summary>
    internal const string HostSettingsCacheKey = "GetHostSettings";

    /// <summary>
    /// Key of the portal-alias collection used for tenant resolution. Installation wide;
    /// Library/Components/Portal/PortalSettings.vb:L1227-L1232.
    /// </summary>
    internal const string PortalAliasCacheKey = "GetPortalByAlias";

    /// <summary>Key of the cached stylesheet table. Installation wide; DataCache.vb:L114.</summary>
    internal const string StyleSheetCacheKey = "CSS";

    /// <summary>Key of the cached compression configuration. Installation wide; DataCache.vb:L118.</summary>
    internal const string CompressionConfigCacheKey = "CompressionConfig";

    /// <summary>
    /// Key PREFIX of one module's settings. The legacy sites concatenate the module identifier
    /// directly onto this prefix with no separator and no placeholder
    /// (Library/Components/Modules/ModuleController.vb:L1241, L1294, L1308, L1320), which is why
    /// a module-settings key cannot be attributed to a portal or a tab without a query - the
    /// fact that shapes <see cref="InvalidatePortal"/> and <see cref="InvalidateModules"/>.
    /// </summary>
    internal const string ModuleSettingsCacheKeyPrefix = "GetModuleSettings";

    /// <summary>
    /// Key of the role lookup table. Installation wide;
    /// Library/Components/Portal/PortalController.vb:L1131.
    /// </summary>
    internal const string RolesCacheKey = "GetRoles";

    // ---------------------------------------------------------------------------------------
    // THE FOUR LEGAL PERFORMANCE MULTIPLIERS
    //
    // Ported from the PerformanceSettings enumeration at
    // Library/Components/Shared/Globals.vb:L66-L75, whose own comment records that the values
    // were chosen to keep cache scaling linear. They are constants rather than a second type
    // because this file declares exactly one type.
    // ---------------------------------------------------------------------------------------

    /// <summary>Caching disabled. A miss must not reach this service's creation path at all.</summary>
    internal const int NoCaching = 0;

    /// <summary>Light caching: base expiries are used as declared.</summary>
    internal const int LightCaching = 1;

    /// <summary>
    /// Moderate caching, and the default. Globals.vb:L227-L231 substitutes three when the host
    /// settings row is absent, so any other default would silently change every cache lifetime.
    /// </summary>
    internal const int ModerateCaching = 3;

    /// <summary>Heavy caching: base expiries are multiplied sixfold.</summary>
    internal const int HeavyCaching = 6;

    /// <summary>The process-wide store. Owned by the container and never disposed here.</summary>
    private readonly IMemoryCache _memoryCache;

    /// <summary>
    /// The validated performance multiplier, read once at construction. Immutable for the
    /// lifetime of the singleton, which is what makes it safe to read without synchronisation.
    /// </summary>
    private readonly int _performanceMultiplier;

    /// <summary>
    /// In-flight creations, keyed by cache key, so that concurrent misses for one key perform
    /// one load between them instead of one load each.
    /// </summary>
    private readonly ConcurrentDictionary<string, Lazy<Task<object?>>> _inFlightLoads =
        new(StringComparer.Ordinal);

    /// <summary>
    /// Every key this service has written and not yet evicted. The value is unused - this is a
    /// concurrent set. It is the ONLY thing the invalidations consult, and it is a concurrent
    /// collection rather than a plain set purely so that a read miss can test membership
    /// without taking <see cref="_registryGate"/>.
    /// </summary>
    private readonly ConcurrentDictionary<string, byte> _trackedKeys = new(StringComparer.Ordinal);

    /// <summary>
    /// Serialises every mutation. Without it an invalidation could snapshot a key, evict it and
    /// unregister it while a racing write lands immediately afterwards, leaving a live entry
    /// that is both unregistered and stale - invisible to any later invalidation. Holding this
    /// monitor across each register-then-write and unregister-then-evict pair is what makes
    /// that interleaving impossible. It is never held across an <see langword="await"/>.
    /// </summary>
    private readonly object _registryGate = new();

    /// <summary>
    /// Initialises the service over the container-owned cache and the bound caching options.
    /// </summary>
    /// <param name="memoryCache">
    /// The process-wide in-memory store. Supplied and owned by the container.
    /// </param>
    /// <param name="cachingOptions">
    /// The bound <see cref="CachingOptions"/>. Its value is read exactly once, here: this class
    /// never touches configuration itself and never binds options.
    /// </param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="memoryCache"/> or <paramref name="cachingOptions"/> is
    /// <see langword="null"/>.
    /// </exception>
    /// <exception cref="OptionsValidationException">
    /// The configured multiplier is not one of the four legacy performance settings.
    /// </exception>
    public MemoryCacheService(IMemoryCache memoryCache, IOptions<CachingOptions> cachingOptions)
    {
        ArgumentNullException.ThrowIfNull(memoryCache);
        ArgumentNullException.ThrowIfNull(cachingOptions);

        _memoryCache = memoryCache;
        _performanceMultiplier = ValidateMultiplier(cachingOptions.Value.PerformanceMultiplier);
    }

    /// <summary>
    /// Whether caching is switched on at all. A multiplier of <see cref="NoCaching"/> makes every
    /// computed expiry zero, which the legacy sites treated as an instruction to skip the work
    /// rather than merely to skip the write.
    /// </summary>
    private bool IsCachingEnabled => _performanceMultiplier > NoCaching;

    /// <summary>
    /// Accepts only the four multipliers the legacy enumeration declared, and rejects every
    /// other value loudly at construction.
    /// </summary>
    /// <param name="performanceMultiplier">The configured multiplier.</param>
    /// <returns>The same value, once proven legal.</returns>
    /// <remarks>
    /// <see cref="CachingOptions.PerformanceMultiplier"/> is deliberately an unvalidated integer:
    /// it models what configuration actually supplies. Validation belongs at the point of
    /// consumption, which is here, and it happens at construction so that a misconfigured
    /// deployment fails while the container is composing rather than midway through a request
    /// with a silently wrong cache lifetime.
    /// </remarks>
    private static int ValidateMultiplier(int performanceMultiplier)
    {
        if (performanceMultiplier is NoCaching or LightCaching or ModerateCaching or HeavyCaching)
        {
            return performanceMultiplier;
        }

        // Every number reaches the message through an invariant conversion first, so the
        // concatenation below interpolates strings only and cannot pick up a culture.
        string configured = FormattableString.Invariant($"{performanceMultiplier}");
        string supported = string.Join(
            ", ",
            FormattableString.Invariant($"{NoCaching} (no caching)"),
            FormattableString.Invariant($"{LightCaching} (light)"),
            FormattableString.Invariant($"{ModerateCaching} (moderate, the default)"),
            FormattableString.Invariant($"{HeavyCaching} (heavy)"));

        string failure =
            $"{CachingOptions.SectionName}:{nameof(CachingOptions.PerformanceMultiplier)} is "
            + $"{configured}, which is not one of the four supported values: {supported}. Those "
            + "are the values the legacy PerformanceSettings enumeration declared, chosen so "
            + "that cache lifetimes scale linearly, so any other multiplier would produce "
            + "expiries no legacy configuration could have produced.";

        throw new OptionsValidationException(
            Microsoft.Extensions.Options.Options.DefaultName,
            typeof(CachingOptions),
            [failure]);
    }

    /// <inheritdoc />
    /// <remarks>
    /// A miss also drops any stale registration for <paramref name="key"/>, because an entry that
    /// expired naturally leaves its registration behind - this service registers no eviction
    /// callback, so the registry is reconciled lazily, here and in the eviction paths.
    /// </remarks>
    /// <exception cref="ArgumentException">
    /// <paramref name="key"/> is <see langword="null"/>, empty or white space.
    /// </exception>
    /// <exception cref="InvalidCastException">
    /// A live entry exists under <paramref name="key"/> but is not a <typeparamref name="T"/>.
    /// Two callers sharing one key with different value types is a defect in the callers, and it
    /// surfaces here for the same reason the framework's own typed read surfaces it, rather than
    /// being disguised as a miss.
    /// </exception>
    public T? Get<T>(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        // MIGRATION: absence is the null equivalent of T and nothing else. The legacy null
        // helpers (Library/Components/Shared/Null.vb) mapped an absent integer to -1 and an
        // absent string to the empty string, and both are legitimate stored values in the
        // existing schema, so neither may stand in for a miss.
        return TryRead(key, out object? entry) ? FromCacheEntry<T>(entry) : default;
    }

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// The supplied <paramref name="expiration"/> is honoured exactly, as a sliding window. That
    /// is the legacy semantic: of the twelve legacy insert overloads, the widest pair
    /// (DataCache.vb:L277 and L281) applied the sliding window alone.
    /// </para>
    /// <para>
    /// Writing nothing is a valid outcome. When caching is switched off, or when the caller's
    /// computed expiry is not positive, this method returns without touching the cache and
    /// without registering the key.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentException">
    /// <paramref name="key"/> is <see langword="null"/>, empty or white space.
    /// </exception>
    public void Set<T>(string key, T value, TimeSpan expiration)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        // A null value is stored as such. The contract's generic parameter is unconstrained, so
        // the caller chooses whether null is meaningful for its own T, and this method does not
        // second-guess that choice.

        // MIGRATION: twelve legacy insert overloads (DataCache.vb:L245-L291) collapse into this
        // one. Six of them accepted a System.Web.Caching.CacheDependency, and the L277 overload
        // delegated to L281, which provably discarded the dependency, the absolute expiry, the
        // eviction priority and the removal callback and applied only the sliding window - so
        // those four parameters were already inert in the running legacy system. Under the
        // minimal-change clause that defect is recorded rather than repaired, which is exactly
        // why none of the four is reproduced.
        // MIGRATION: the distinct persistence tier is gone with them. CachePersistenceEnabled
        // (DataCache.vb:L94), the persistence-aware read and remove (L233, L241) and the
        // trailing PersistAppRestart flag on every insert existed because an ASP.NET 2.0 worker
        // process could recycle at any moment. Website/release.config:L46 switched persistence
        // off in the shipping configuration, and a container is replaced rather than recycled,
        // so there is one explicit in-memory tier and no second store.
        // MIGRATION: a non-positive expiry writes nothing, where the legacy code wrote an entry
        // that never expired. Library/Components/Modules/ModuleController.vb:L1264 and L1355
        // multiply a hardcoded 20 by the multiplier and insert unconditionally, so at a
        // multiplier of zero they handed System.Web a zero sliding window - which it read as
        // "no sliding expiration" and, with no absolute expiry either, cached the entry for the
        // life of the process. That is an accidental persistence path, not an instruction, and
        // it is deliberately not carried forward; Timeout.InfiniteTimeSpan is likewise not
        // accepted as a hidden equivalent, being negative and therefore non-positive here.
        if (!IsCachingEnabled || expiration <= TimeSpan.Zero)
        {
            return;
        }

        MemoryCacheEntryOptions entryOptions = new() { SlidingExpiration = expiration };

        lock (_registryGate)
        {
            // Register BEFORE writing, and under the gate, so that no invalidation can observe
            // the written entry without also observing its registration.
            _trackedKeys[key] = 0;
            _memoryCache.Set(key, value, entryOptions);
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// Coalescing. Concurrent misses for one key share a single creation, held as a lazily
    /// started task in an in-flight registry. The losing callers await the winner's task instead
    /// of starting their own, which is the whole point of this member existing: the legacy idiom
    /// was a read, then a load, then an insert at every call site, so two simultaneous requests
    /// each performed the load.
    /// </para>
    /// <para>
    /// Cancellation, stated plainly rather than hidden, and strictly per caller. A caller whose
    /// <paramref name="cancellationToken"/> is already cancelled on entry is refused immediately,
    /// before the cache is even read. Otherwise each caller awaits the shared creation through
    /// its OWN token, so a caller always observes its own cancellation and never another's. The
    /// shared creation itself is deliberately NOT bound to any one caller's lifetime: because a
    /// single creation is shared, binding it to whoever happened to arrive first would let one
    /// caller's withdrawal surface as an <see cref="OperationCanceledException"/> to unrelated
    /// callers whose own tokens are perfectly healthy - two concurrent requests miss the same
    /// key, the first client disconnects, and the second fails through no fault of its own.
    /// <paramref name="factory"/> therefore receives <see cref="CancellationToken.None"/>. A
    /// caller that abandons its await leaves the creation running, and its result is still cached
    /// for whoever asks next, which is exactly what the coalesced callers are waiting for.
    /// </para>
    /// <para>
    /// Failure is not sticky. The in-flight registration is released whether the creation
    /// completes, faults or is cancelled, so one failed load never poisons a key.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentException">
    /// <paramref name="key"/> is <see langword="null"/>, empty or white space.
    /// </exception>
    /// <exception cref="ArgumentNullException"><paramref name="factory"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">
    /// No live entry exists and caching is switched off, or <paramref name="expiration"/> is not
    /// positive. See the remarks on why this is a refusal rather than a pass-through.
    /// </exception>
    /// <exception cref="OperationCanceledException">
    /// <paramref name="cancellationToken"/> was cancelled. Another caller's cancellation is never
    /// reported here; see the remarks.
    /// </exception>
    public async Task<T> GetOrCreateAsync<T>(
        string key,
        Func<CancellationToken, Task<T>> factory,
        TimeSpan expiration,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(factory);

        // A caller that has already cancelled is refused before anything else happens, which is
        // what every token-accepting asynchronous member in the framework does and what the
        // documented exception on this member promises. It sits ahead of the read deliberately:
        // the legacy ordering constraint honoured below concerns the MULTIPLIER check, not
        // cancellation, so nothing is lost by declining a caller that has withdrawn.
        cancellationToken.ThrowIfCancellationRequested();

        // Read BEFORE consulting the multiplier, which is the legacy order: every measured call
        // site fetched from the cache first and computed its timeout only after a miss, so a
        // live entry is still served even when caching has since been switched off.
        if (TryRead(key, out object? entry))
        {
            return FromCacheEntry<T>(entry);
        }

        // MIGRATION: the caller owns the no-caching decision, and this refuses rather than
        // guesses. At Library/Components/Portal/PortalController.vb:L218-L240 a non-positive
        // timeout skips the entire GetAllTabs query - the legacy comment there says the query is
        // too expensive to run on every request - so "caching off" means "do not do the work",
        // not merely "do not store the result". Because that guard is placed inconsistently
        // across the measured legacy sites, and because the minimal-change clause forbids
        // harmonising them, it cannot be centralised here: L1232 computes the same product but
        // reads the database unconditionally and guards only the write at L1245, and
        // ModuleController.vb:L1264 and L1355 apply no guard at all. Each migrated read path
        // must therefore reproduce its own site's guard. Reaching this line means a caller has
        // not, and the only alternatives would be to run an expensive load the legacy path would
        // have skipped or to fabricate a value of T - so it fails fast instead.
        if (!IsCachingEnabled || expiration <= TimeSpan.Zero)
        {
            throw new InvalidOperationException(BuildCachingDisabledMessage(key, expiration));
        }

        // The creation is deliberately NOT handed this caller's token. See the remarks: a shared
        // creation bound to one caller's lifetime lets that caller's withdrawal cancel work other
        // callers are waiting on, so a healthy caller would surface a cancellation it never asked
        // for. Isolation is enforced here, at the single point where the shared work is started.
        Lazy<Task<object?>> load = _inFlightLoads.GetOrAdd(
            key,
            _ => new Lazy<Task<object?>>(
                () => LoadAndCacheAsync(key, factory, expiration),
                LazyThreadSafetyMode.ExecutionAndPublication));

        // Accessing Value starts the creation at most once, however many callers arrive.
        // WaitAsync gives this caller its own cancellation without disturbing the shared work.
        object? produced = await load.Value.WaitAsync(cancellationToken).ConfigureAwait(false);

        return FromCacheEntry<T>(produced);
    }

    /// <inheritdoc />
    /// <remarks>Evicting an absent key is not an error, and also drops any stale registration.</remarks>
    /// <exception cref="ArgumentException">
    /// <paramref name="key"/> is <see langword="null"/>, empty or white space.
    /// </exception>
    public void Remove(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        lock (_registryGate)
        {
            Evict(key);
        }
    }

    // ---------------------------------------------------------------------------------------
    // THE EIGHT INVALIDATIONS
    //
    // MIGRATION: query cascades become registry and category eviction. Every legacy clear that
    // needed to reach dependent keys DISCOVERED them by querying the database:
    // ClearHostCache(True) (DataCache.vb:L120-L130) selected all portals and then recursed;
    // ClearPortalCache(id, True) (L139-L151) selected every tab in the portal and every module
    // in the portal, giving a 1 + N + 2N-shaped walk; the parameterless ClearModuleCache()
    // (L172-L196) did the same across the entire installation; and
    // ClearModulePermissionsCachesByPortal (L207-L213) selected every tab in a portal purely to
    // evict permissions. Not one query survives. Invalidation here consults only the in-process
    // registry of keys this service wrote, plus IMemoryCache itself. Where a key genuinely
    // cannot be attributed to the scope being invalidated - a module-settings key carries a
    // module identifier and nothing else - the whole tracked category is evicted instead. That
    // eviction is broader than the legacy walk, and deliberately so: a superfluous eviction
    // costs one re-read, whereas a missed eviction serves stale data.
    //
    // MIGRATION: -1 is no longer a wildcard. The legacy host clear passed -1 as a portal
    // identifier to mean "every portal" (L115-L117). The existing schema seeds Portals.PortalID
    // at IDENTITY(-1,1) and Roles.RoleID at IDENTITY(0,1), so -1 and 0 are both real identities
    // here and every one of the members below treats its argument as exactly one owner.
    // InvalidateHost, which takes no argument at all, is the only member that means everything.
    //
    // Category eviction reaches only keys written THROUGH this service. That is sufficient
    // because every cache write in the migrated stack goes through ICacheService; an entry
    // placed directly into the container's cache by some other component is invisible here, and
    // stating that limit is better than implying a reach this class does not have.
    // ---------------------------------------------------------------------------------------

    /// <inheritdoc />
    /// <remarks>
    /// Evicts the installation-wide keys by name and then everything this service has written,
    /// which is what the legacy recursive host clear achieved by walking the database.
    /// </remarks>
    public void InvalidateHost()
    {
        lock (_registryGate)
        {
            // The installation-wide keys the legacy host clear named explicitly, evicted by name
            // so that they go even when they were written by a path that bypassed this service.
            Evict(HostSettingsCacheKey);
            Evict(PortalAliasCacheKey);
            Evict(StyleSheetCacheKey);
            Evict(CompressionConfigCacheKey);
            Evict(RolesCacheKey);
            Evict(PortalDictionaryCacheKey);
            Evict(TabPathCacheKey);

            // Everything else this service wrote, portal, tab, module and user keys included.
            // Keys is a snapshot, so evicting while enumerating it is safe.
            foreach (string trackedKey in _trackedKeys.Keys)
            {
                Evict(trackedKey);
            }
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// Evicts the four portal-keyed entries and the derived tab-path lookup by name, then the
    /// three tab-keyed and module-keyed categories the legacy cascade discovered by query.
    /// </remarks>
    public void InvalidatePortal(int portalId)
    {
        lock (_registryGate)
        {
            // Exact, because each of these keys carries the portal identifier itself. Note that
            // the module dictionary is portal-keyed even though the two neighbouring module
            // templates are tab-keyed.
            Evict(FormatKey(PortalCacheKey, portalId));
            Evict(FormatKey(ProfileDefinitionsCacheKey, portalId));
            Evict(FormatKey(TabCacheKey, portalId));
            Evict(FormatKey(TabPermissionCacheKey, portalId));
            Evict(FormatKey(ModuleCacheKey, portalId));

            // Derived from every tab in the installation, so a change to one portal's tabs
            // invalidates it. The legacy portal clear evicted it too, through ClearTabsCache.
            Evict(TabPathCacheKey);

            // Tab-keyed and module-keyed, so not attributable to this portal without a query.
            EvictTrackedCategory(PrefixOf(TabModuleCacheKey));
            EvictTrackedCategory(PrefixOf(ModulePermissionCacheKey));
            EvictTrackedCategory(ModuleSettingsCacheKeyPrefix);
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// Reproduces the legacy tab clear exactly: the portal's tab collection, the derived tab-path
    /// lookup and the portal's tab permissions (DataCache.vb:L215-L219).
    /// </remarks>
    public void InvalidateTabs(int portalId)
    {
        lock (_registryGate)
        {
            Evict(FormatKey(TabCacheKey, portalId));
            Evict(TabPathCacheKey);
            Evict(FormatKey(TabPermissionCacheKey, portalId));
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// Evicts the tab's module collection and its module permissions, matching the legacy
    /// single-tab clear (DataCache.vb:L198-L201), and additionally the tracked module-settings
    /// category. The addition is deliberate: the legacy parameterless ClearModuleCache() was the
    /// only path that evicted module settings, and it did so by querying every module in the
    /// installation. That overload has no counterpart on this contract, so without this line
    /// module settings would never be invalidated at all. A module-settings key carries only a
    /// module identifier, so the tab's modules cannot be identified without a query.
    /// </remarks>
    public void InvalidateModules(int tabId)
    {
        lock (_registryGate)
        {
            Evict(FormatKey(TabModuleCacheKey, tabId));
            Evict(FormatKey(ModulePermissionCacheKey, tabId));
            EvictTrackedCategory(ModuleSettingsCacheKeyPrefix);
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// One key, evicted by name. The legacy by-portal module-permission helper walked every tab
    /// in the portal to reach an eviction; that breadth is what these named members replace.
    /// </remarks>
    public void InvalidateTabPermissions(int portalId)
    {
        lock (_registryGate)
        {
            Evict(FormatKey(TabPermissionCacheKey, portalId));
        }
    }

    /// <inheritdoc />
    /// <remarks>One key, evicted by name. The argument is a TAB identifier, not a portal one.</remarks>
    public void InvalidateModulePermissions(int tabId)
    {
        lock (_registryGate)
        {
            Evict(FormatKey(ModulePermissionCacheKey, tabId));
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// Composes the pipe-delimited legacy user key from both parts and evicts exactly that entry.
    /// </remarks>
    /// <exception cref="ArgumentException">
    /// <paramref name="userName"/> is <see langword="null"/>, empty or white space. A blank user
    /// name would compose a key that addresses no user, so it is rejected rather than applied.
    /// </exception>
    public void InvalidateUser(int portalId, string userName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userName);

        lock (_registryGate)
        {
            Evict(FormatUserCacheKey(portalId, userName));
        }
    }

    /// <inheritdoc />
    /// <remarks>One key, evicted by name (DataCache.vb:L155-L157).</remarks>
    public void InvalidateProfileDefinitions(int portalId)
    {
        lock (_registryGate)
        {
            Evict(FormatKey(ProfileDefinitionsCacheKey, portalId));
        }
    }

    /// <summary>
    /// Formats a single-placeholder legacy key template with an identifier, using the invariant
    /// culture so that a key never varies with the thread's culture.
    /// </summary>
    /// <param name="keyTemplate">One of the legacy key templates declared on this class.</param>
    /// <param name="identifier">The portal or tab identifier. Every value is a real identity.</param>
    /// <returns>The composed cache key.</returns>
    private static string FormatKey(string keyTemplate, int identifier) =>
        string.Format(System.Globalization.CultureInfo.InvariantCulture, keyTemplate, identifier);

    /// <summary>
    /// Composes the two-placeholder legacy user key, preserving both pipe delimiters.
    /// </summary>
    /// <param name="portalId">The portal the membership belongs to.</param>
    /// <param name="userName">The user name.</param>
    /// <returns>The composed cache key.</returns>
    private static string FormatUserCacheKey(int portalId, string userName) =>
        string.Format(System.Globalization.CultureInfo.InvariantCulture, UserCacheKey, portalId, userName);

    /// <summary>
    /// Returns the fixed leading text of a legacy key template - everything before its first
    /// placeholder - so that a category can be matched without restating the literal and letting
    /// the two drift apart.
    /// </summary>
    /// <param name="keyTemplate">One of the legacy key templates declared on this class.</param>
    /// <returns>The template's prefix, or the template itself when it has no placeholder.</returns>
    private static string PrefixOf(string keyTemplate)
    {
        int placeholder = keyTemplate.IndexOf('{');

        return placeholder < 0 ? keyTemplate : keyTemplate[..placeholder];
    }

    /// <summary>
    /// Converts a raw cache entry to the caller's type, round-tripping a stored null rather than
    /// substituting anything for it.
    /// </summary>
    /// <typeparam name="T">The caller's value type.</typeparam>
    /// <param name="entry">The raw entry read out of the cache.</param>
    /// <returns>The typed value.</returns>
    private static T FromCacheEntry<T>(object? entry) => entry is null ? default! : (T)entry;

    /// <summary>
    /// Builds the refusal message for a miss that arrives with caching switched off or with a
    /// non-positive expiry.
    /// </summary>
    /// <param name="key">The key that missed.</param>
    /// <param name="expiration">The expiry the caller supplied.</param>
    /// <returns>The message.</returns>
    private string BuildCachingDisabledMessage(string key, TimeSpan expiration)
    {
        string multiplier = FormattableString.Invariant($"{_performanceMultiplier}");
        string requested = FormattableString.Invariant($"{expiration}");

        return $"No live cache entry exists for '{key}' and this service will not create one: "
            + $"{CachingOptions.SectionName}:{nameof(CachingOptions.PerformanceMultiplier)} is "
            + $"{multiplier} and the requested expiration is {requested}. When the effective "
            + "cache lifetime is not positive, the legacy read path this replaces skipped the "
            + "database load itself rather than merely skipping the cache write, so the calling "
            + "application service must branch on its own computed lifetime before it gets here. "
            + "Fabricating a value or running a load the legacy path would have skipped are both "
            + "wrong, so neither is done.";
    }

    /// <summary>
    /// Reads a raw entry and, on a miss, reconciles the tracked-key registry.
    /// </summary>
    /// <param name="key">The cache entry key.</param>
    /// <param name="entry">The raw entry when one is live; otherwise <see langword="null"/>.</param>
    /// <returns><see langword="true"/> when a live entry exists.</returns>
    /// <remarks>
    /// The <see langword="out"/> parameter is private plumbing over the framework's own
    /// try-get shape. No member of the public contract exposes <see langword="out"/> or
    /// <see langword="ref"/>.
    /// </remarks>
    private bool TryRead(string key, out object? entry)
    {
        if (_memoryCache.TryGetValue(key, out entry))
        {
            return true;
        }

        ForgetIfAbsent(key);

        return false;
    }

    /// <summary>
    /// Drops a registration whose entry has expired naturally, leaving a live entry's
    /// registration alone.
    /// </summary>
    /// <param name="key">The key whose registration may be stale.</param>
    private void ForgetIfAbsent(string key)
    {
        // Lock-free first pass. The overwhelming majority of misses are for keys this service
        // never wrote, and those must not pay for the gate.
        if (!_trackedKeys.ContainsKey(key))
        {
            return;
        }

        lock (_registryGate)
        {
            // Re-read under the gate. Set holds the same gate across its register-then-write
            // pair, so if a write won the race the entry is live now and its registration must
            // stand - dropping it would hide a live entry from category eviction.
            if (!_memoryCache.TryGetValue(key, out _))
            {
                _trackedKeys.TryRemove(key, out _);
            }
        }
    }

    /// <summary>
    /// Evicts one entry and its registration together. Callers must already hold
    /// <see cref="_registryGate"/>.
    /// </summary>
    /// <param name="key">The key to evict.</param>
    private void Evict(string key)
    {
        _memoryCache.Remove(key);
        _trackedKeys.TryRemove(key, out _);
    }

    /// <summary>
    /// Evicts every tracked key beginning with <paramref name="keyPrefix"/>. Callers must already
    /// hold <see cref="_registryGate"/>.
    /// </summary>
    /// <param name="keyPrefix">The category prefix, matched ordinally.</param>
    private void EvictTrackedCategory(string keyPrefix)
    {
        // Keys is a snapshot, so evicting while enumerating it is safe.
        foreach (string trackedKey in _trackedKeys.Keys)
        {
            if (trackedKey.StartsWith(keyPrefix, StringComparison.Ordinal))
            {
                Evict(trackedKey);
            }
        }
    }

    /// <summary>
    /// Performs the one shared creation for a key: re-checks the cache, invokes the caller's
    /// factory, stores the result and releases the in-flight registration.
    /// </summary>
    /// <typeparam name="T">The caller's value type.</typeparam>
    /// <param name="key">The cache entry key.</param>
    /// <param name="factory">The caller's cancellation-aware producer.</param>
    /// <param name="expiration">How long a newly produced entry stays live.</param>
    /// <returns>The produced or concurrently published value, boxed for the shared task.</returns>
    /// <remarks>
    /// Takes no cancellation token by design, so that no caller's lifetime can be attached to work
    /// that other callers are waiting on. Per-caller cancellation is applied by the awaiting
    /// member instead.
    /// </remarks>
    private async Task<object?> LoadAndCacheAsync<T>(
        string key,
        Func<CancellationToken, Task<T>> factory,
        TimeSpan expiration)
    {
        try
        {
            // Second check, now that this creation has been elected. A value published between
            // the caller's read and this point must not provoke another round of caller I/O.
            if (_memoryCache.TryGetValue(key, out object? published))
            {
                return published;
            }

            // CancellationToken.None, not a caller's token: this one creation serves every
            // coalesced caller, so it must outlive any individual caller that withdraws.
            T produced = await factory(CancellationToken.None).ConfigureAwait(false);

            Set(key, produced, expiration);

            return produced;
        }
        finally
        {
            // Ordered deliberately: the value is already in the cache before the key is released
            // here, and this runs before the shared task completes, so every awaiter resumes
            // against a released key. Because it is a finally, a faulted or cancelled creation
            // releases the key too, and the next caller simply retries. Release happens here,
            // in the creation that owns the registration, rather than in each awaiter: an
            // awaiter that released by key could remove a newer registration and provoke a
            // duplicate load.
            _inFlightLoads.TryRemove(key, out _);
        }
    }

}
