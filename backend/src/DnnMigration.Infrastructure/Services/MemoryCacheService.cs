using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using DnnMigration.Application.Options;
using DnnMigration.Domain.Abstractions.Services;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace DnnMigration.Infrastructure.Services;

/// <summary>
/// In-memory implementation of <see cref="ICacheService"/> over the framework's <see cref="IMemoryCache"/>,
/// carrying the legacy DotNetNuke cache-key vocabulary and expiry baselines so that cache behaviour stays
/// auditable in one place.
/// </summary>
/// <remarks>
/// <para>
/// Lifetime. Registered as a singleton, after the container's in-memory cache.
/// </para>
/// <para>
/// Threading. Reads go straight to the thread-safe cache.
/// </para>
/// </remarks>
internal sealed class MemoryCacheService : ICacheService
{
    // LEGACY CACHE KEYS AND BASE EXPIRIES
    // MIGRATION: Deliberately absent, because the features that owned them are out of scope: the folder and
    // folder-permission keys, the lookup-list key, the presentation-theme keys the legacy host and portal
    // clears removed, the secure host-settings key and the obsolete core-cache facade together with the
    // enum that selected its target.

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
    /// Key of the derived tab-path lookup. Installation wide, and the one legacy key with no paired expiry
    /// constant: DataCache.vb:L52 declares the key and no timeout beside it, and
    /// Library/Components/Tabs/TabController.vb:L1128 writes it with no expiry at all.
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
    /// Key template of a portal's module dictionary, formatted with the PORTAL identifier - measured at
    /// Library/Components/Modules/ModuleController.vb:L961, and easily confused with the tab-keyed template
    /// above.
    /// </summary>
    internal const string ModuleCacheKey = "Modules{0}";

    /// <summary>Base expiry, in minutes, of a portal's module dictionary.</summary>
    internal const int ModuleCacheTimeOut = 20;

    /// <summary>Key template of a portal's profile definitions, formatted with the portal identifier.</summary>
    internal const string ProfileDefinitionsCacheKey = "ProfileDefinitions{0}";

    /// <summary>Base expiry, in minutes, of a portal's profile definitions.</summary>
    internal const int ProfileDefinitionsCacheTimeOut = 20;

    /// <summary>
    /// Key template of one user's cached identity within one portal, formatted with the portal identifier
    /// and then the user name. Both pipe delimiters, the casing and the two placeholders are load bearing:
    /// this exact literal is what an existing installation's cache is keyed by.
    /// </summary>
    internal const string UserCacheKey = "UserInfo|{0}|{1}";

    /// <summary>
    /// Base expiry, in minutes, of one user's cached identity. One, not twenty - the single legacy
    /// exception, so at the default multiplier of three the effective duration is three minutes.
    /// </summary>
    internal const int UserCacheTimeOut = 1;

    // LEGACY AD-HOC KEYS

    /// <summary>Key of the host settings table. Installation wide.</summary>
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
    /// Key PREFIX of one module's settings. The legacy sites concatenate the module identifier directly
    /// onto this prefix with no separator and no placeholder, which is why a module-settings key cannot be
    /// attributed to a portal or a tab without a query - the fact that shapes <see
    /// cref="InvalidatePortal"/> and <see cref="InvalidateModules"/>.
    /// </summary>
    internal const string ModuleSettingsCacheKeyPrefix = "GetModuleSettings";

    /// <summary>
    /// Key of the role lookup table. Installation wide;
    /// Library/Components/Portal/PortalController.vb:L1131.
    /// </summary>
    internal const string RolesCacheKey = "GetRoles";

    // THE FOUR CONVENTIONAL PERFORMANCE MULTIPLIERS

    /// <summary>Caching disabled. A miss must not reach this service's creation path at all.</summary>
    internal const int NoCaching = 0;

    /// <summary>Light caching: base expiries are used as declared.</summary>
    internal const int LightCaching = 1;

    /// <summary>
    /// Moderate caching, and the default. Globals.vb:L227-L231 substitutes three when the host settings row
    /// is absent, so any other default would silently change every cache lifetime.
    /// </summary>
    internal const int ModerateCaching = 3;

    /// <summary>Heavy caching: base expiries are multiplied sixfold.</summary>
    internal const int HeavyCaching = 6;

    /// <summary>
    /// Greatest length of time a caller waits on a shared creation before it is abandoned and the
    /// documented timeout is reported.
    /// </summary>
    /// <remarks>
    /// A creation is shared by every caller that missed the same key, so it cannot be bound to any one
    /// caller's lifetime without letting that caller's withdrawal fail the others.
    /// </remarks>
    private static readonly TimeSpan SharedCreationBudget = TimeSpan.FromSeconds(30);

    /// <summary>Greatest length of time the shared creation's own token stays uncancelled.</summary>
    /// <remarks>
    /// DELIBERATELY LONGER THAN THE CALLER'S WAIT, and the difference is the whole point of the member
    /// existing. The two budgets bound different things: the wait above bounds how long a CALLER is made to
    /// hold on, and this one bounds how long the WORK is allowed to keep running once no caller is holding
    /// on any more.
    /// </remarks>
    private static readonly TimeSpan CreationWorkBudget = SharedCreationBudget + TimeSpan.FromSeconds(5);

    /// <summary>Every declared category prefix, longest first.</summary>
    /// <remarks>
    /// Longest-first ordering is what makes classification unambiguous where one prefix begins with
    /// another. The portal template's prefix is a prefix of the portal-dictionary key, so a shortest-first
    /// walk would file that key under the wrong category; ordered this way the more specific match always
    /// wins.
    /// </remarks>
    private static readonly string[] CategoryPrefixes = BuildCategoryPrefixes();
    // NAMING AN ENTRY IN A DIAGNOSTIC WITHOUT NAMING ITS KEY
    // A CATEGORY answers the first. It is one of the fixed literals this class already declares, resolved
    // through PrefixOf so that no literal is restated, and it therefore contains no caller-supplied text by
    // construction rather than by filtering.

    /// <summary>Category reported for a key that matches none of the declared families.</summary>
    private const string UnrecognisedKeyCategory = "unrecognised";

    /// <summary>
    /// Number of digest bytes rendered into a fingerprint, giving twice as many hexadecimal characters.
    /// </summary>
    /// <remarks>
    /// Eight bytes is ample to tell one entry from another within a single process and short enough to read
    /// at a glance. Truncation is not what makes the fingerprint one-way - the secret is - so the length is
    /// chosen for legibility rather than for strength.
    /// </remarks>
    private const int KeyFingerprintBytes = 8;

    /// <summary>The size every entry declares against the store's size limit: one entry.</summary>
    private const long SingleEntrySize = 1;

    /// <summary>The per-process secret that keys every fingerprint.</summary>
    private static readonly byte[] KeyFingerprintSecret = RandomNumberGenerator.GetBytes(32);

    /// <summary>The declared key families, longest prefix first, used to classify a key for a diagnostic.</summary>
    /// <remarks>
    /// Every entry is derived from a constant on this class rather than written out again, so a renamed key
    /// cannot leave a stale category behind.
    /// </remarks>
    private static readonly string[] KeyCategoryPrefixes =
        new[]
        {
            PrefixOf(PortalDictionaryCacheKey),
            PrefixOf(PortalCacheKey),
            PrefixOf(TabCacheKey),
            PrefixOf(TabPathCacheKey),
            PrefixOf(TabPermissionCacheKey),
            PrefixOf(TabModuleCacheKey),
            PrefixOf(ModulePermissionCacheKey),
            PrefixOf(ModuleCacheKey),
            PrefixOf(ProfileDefinitionsCacheKey),
            PrefixOf(UserCacheKey),
            PrefixOf(HostSettingsCacheKey),
            PrefixOf(PortalAliasCacheKey),
            PrefixOf(StyleSheetCacheKey),
            PrefixOf(CompressionConfigCacheKey),
            PrefixOf(ModuleSettingsCacheKeyPrefix),
            PrefixOf(RolesCacheKey),
        }
        .OrderByDescending(static prefix => prefix.Length)
        .ToArray();

    /// <summary>The process-wide store. Owned by the container and never disposed here.</summary>
    private readonly IMemoryCache _memoryCache;

    /// <summary>
    /// The validated performance multiplier, read once at construction. Immutable for the lifetime of the
    /// singleton, which is what makes it safe to read without synchronisation.
    /// </summary>
    private readonly int _performanceMultiplier;

    /// <summary>
    /// In-flight creations, keyed by cache key AND requested value shape, so that concurrent misses for one
    /// key perform one load between them instead of one load each - while two callers requesting one key as
    /// two different shapes are kept apart.
    /// </summary>
    /// <remarks>
    /// The shape forms part of the identity because coalescing is only ever correct for callers that are
    /// genuinely asking for the same thing. Two callers wanting the same key as two different shapes are
    /// not, and joining them would deliver one of them a value it cannot hold - intermittently, decided
    /// purely by arrival order.
    /// </remarks>
    private readonly ConcurrentDictionary<(string Key, Type ValueType), SharedCreation> _inFlightLoads = new();

    /// <summary>
    /// Every key this service has written and not yet evicted. The value is unused - this is a concurrent
    /// set.
    /// </summary>
    private readonly ConcurrentDictionary<string, byte> _trackedKeys = new(StringComparer.Ordinal);

    /// <summary>The keys this service has written, grouped by the category their key template defines.</summary>
    /// <remarks>
    /// This index is what makes a category invalidation proportional to the category rather than to the
    /// whole registry. Without it the only way to find one category's keys is to walk every tracked key and
    /// test its prefix, and a single portal invalidation performs three such category evictions - so one
    /// administrative change would walk the whole registry three times over.
    /// </remarks>
    private readonly Dictionary<string, HashSet<string>> _keysByCategory = new(StringComparer.Ordinal);

    /// <summary>Counts invalidations, so a creation that began before one can detect that it did.</summary>
    /// <remarks>
    /// Mutated only under <see cref="_registryGate"/>, and only ever incremented. A creation records this
    /// value before it calls the caller's factory and presents it again when it comes back; a mismatch
    /// means an invalidation intervened while the factory was running, and the result is therefore
    /// delivered to the waiting callers but not published.
    /// </remarks>
    private long _invalidationGeneration;

    /// <summary>
    /// Serialises every mutation. Without it an invalidation could snapshot a key, evict it and unregister
    /// it while a racing write lands immediately afterwards, leaving a live entry that is both unregistered
    /// and stale - invisible to any later invalidation.
    /// </summary>
    private readonly object _registryGate = new();

    /// <summary>Initialises the service over the container-owned cache and the bound caching options.</summary>
    /// <param name="memoryCache">The process-wide in-memory store.</param>
    /// <param name="cachingOptions">The bound <see cref="CachingOptions"/>.</param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="memoryCache"/> or <paramref name="cachingOptions"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="OptionsValidationException">
    /// The bound options are unusable, as reported by <see cref="CachingOptions.Validate"/>.
    /// </exception>
    public MemoryCacheService(IMemoryCache memoryCache, IOptions<CachingOptions> cachingOptions)
    {
        ArgumentNullException.ThrowIfNull(memoryCache);
        ArgumentNullException.ThrowIfNull(cachingOptions);

        CachingOptions options = cachingOptions.Value;

        _memoryCache = memoryCache;
        _performanceMultiplier = ValidateMultiplier(options);
    }

    /// <summary>
    /// Whether caching is switched on at all. A multiplier of <see cref="NoCaching"/> makes every computed
    /// expiry zero, which the legacy sites treated as an instruction to skip the work rather than merely to
    /// skip the write.
    /// </summary>
    private bool IsCachingEnabled => _performanceMultiplier > NoCaching;

    /// <summary>
    /// Proves the bound options usable and returns the multiplier, failing loudly at construction when they
    /// are not.
    /// </summary>
    /// <param name="options">The bound caching options.</param>
    /// <returns>The configured multiplier, once proven usable.</returns>
    private static int ValidateMultiplier(CachingOptions options)
    {
        IReadOnlyList<string> failures = options.Validate();

        if (failures.Count == 0)
        {
            return options.PerformanceMultiplier;
        }

        throw new OptionsValidationException(
            Microsoft.Extensions.Options.Options.DefaultName,
            typeof(CachingOptions),
            failures);
    }

    /// <inheritdoc />
    /// <exception cref="ArgumentException">
    /// <paramref name="key"/> is <see langword="null"/>, empty or white space.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// A live entry exists under <paramref name="key"/> but is not a <typeparamref name="T"/>.
    /// </exception>
    public T? Get<T>(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        // Absence is the null equivalent of T and nothing else. The legacy null helpers mapped an absent
        // integer to -1 and an absent string to the empty string, and both are legitimate stored values in
        // the existing schema, so neither may stand in for a miss.
        return TryRead(key, out object? entry) ? FromCacheEntry<T>(key, entry) : default;
    }

    /// <inheritdoc />
    /// <exception cref="ArgumentException">
    /// <paramref name="key"/> is <see langword="null"/>, empty or white space.
    /// </exception>
    public void Set<T>(string key, T value, TimeSpan expiration)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        // Twelve legacy insert overloads collapse into this one.
        if (!IsCachingEnabled || expiration <= TimeSpan.Zero)
        {
            return;
        }

        lock (_registryGate)
        {
            Publish(key, value, expiration);
        }
    }

    /// <summary>
    /// Writes an entry and registers it. Callers must already hold <see cref="_registryGate"/> and must
    /// already have established that caching is on and that <paramref name="expiration"/> is positive.
    /// </summary>
    /// <typeparam name="T">The value's type.</typeparam>
    /// <param name="key">The cache entry key.</param>
    /// <param name="value">The value to store.</param>
    /// <param name="expiration">How long the entry stays live.</param>
    private void Publish<T>(string key, T value, TimeSpan expiration)
    {
        // The unit is ONE ENTRY, not an estimate of bytes. The values written here are object graphs
        // projected records, string sequences, dictionaries - whose true footprint cannot be measured
        // without walking them, and a walk on every write would cost more than the cache saves.
        MemoryCacheEntryOptions entryOptions = new()
        {
            SlidingExpiration = expiration,
            Size = SingleEntrySize,
        };

        // The callback is safe to take the gate from, and the reason is worth stating exactly because it is
        // not the obvious one.
        entryOptions.RegisterPostEvictionCallback(static (evictedKey, _, _, state) =>
        {
            if (evictedKey is string cacheKey && state is MemoryCacheService service)
            {
                service.ForgetIfAbsent(cacheKey);
            }
        }, this);

        // Register BEFORE writing, and under the gate, so that no invalidation can observe
        // the written entry without also observing its registration.
        _trackedKeys[key] = 0;

        string? category = CategoryOf(key);
        if (category is not null)
        {
            if (!_keysByCategory.TryGetValue(category, out HashSet<string>? members))
            {
                members = new HashSet<string>(StringComparer.Ordinal);
                _keysByCategory[category] = members;
            }

            members.Add(key);
        }

        _memoryCache.Set(key, value, entryOptions);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Cancellation, stated plainly rather than hidden, and strictly per caller. A caller whose <paramref
    /// name="cancellationToken"/> is already cancelled on entry is refused immediately, before the cache is
    /// even read.
    /// </remarks>
    /// <exception cref="ArgumentException">
    /// <paramref name="key"/> is <see langword="null"/>, empty or white space.
    /// </exception>
    /// <exception cref="ArgumentNullException"><paramref name="factory"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">
    /// No live entry exists and caching is switched off, or <paramref name="expiration"/> is not positive -
    /// see the remarks on why this is a refusal rather than a pass-through.
    /// </exception>
    /// <exception cref="CacheProductionTimeoutException">
    /// The shared creation did not complete within its budget.
    /// </exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    public async Task<T> GetOrCreateAsync<T>(
        string key,
        Func<CancellationToken, Task<T>> factory,
        TimeSpan expiration,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(factory);

        // A caller that has already cancelled is refused before anything else happens, which is what every
        // token-accepting asynchronous member in the framework does and what the documented exception on
        // this member promises.
        cancellationToken.ThrowIfCancellationRequested();

        // Read BEFORE consulting the multiplier, which is the legacy order: every measured call site
        // fetched from the cache first and computed its timeout only after a miss, so a live entry is still
        // served even when caching has since been switched off.
        if (TryRead(key, out object? entry))
        {
            return FromCacheEntry<T>(key, entry);
        }

        if (!IsCachingEnabled || expiration <= TimeSpan.Zero)
        {
            throw new InvalidOperationException(BuildCachingDisabledMessage(key, expiration));
        }

        // MIGRATION: net-new. The registration is identified by the key AND the value shape, not by the key
        // alone.
        (string Key, Type ValueType) registration = (key, typeof(T));

        // The creation is deliberately NOT handed this caller's token. See the remarks: a shared creation
        // bound to one caller's lifetime lets that caller's withdrawal cancel work other callers are
        // waiting on, so a healthy caller would surface a cancellation it never asked for.
        SharedCreation creation = _inFlightLoads.GetOrAdd(
            registration,
            _ =>
            {
                SharedCreation created = new();

                // Assigned immediately, and before the value is published to the dictionary, so no caller
                // can observe a creation whose work is not yet reachable. A losing thread's allocation is
                // discarded by GetOrAdd with its work never started, because the work is lazy.
                created.Work = new Lazy<Task<object?>>(
                    () => LoadAndCacheAsync(key, factory, expiration, registration, created),
                    LazyThreadSafetyMode.ExecutionAndPublication);

                return created;
            });

        object? produced;

        try
        {
            // Accessing Value starts the creation at most once, however many callers arrive.
            produced = await creation.Work.Value
                .WaitAsync(SharedCreationBudget, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is TimeoutException
            || (exception is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            // ONE OUTCOME FOR ONE CONDITION, however the wait ended.
            creation.Abandon();

            _inFlightLoads.TryRemove(
                new KeyValuePair<(string Key, Type ValueType), SharedCreation>(registration, creation));

            throw new CacheProductionTimeoutException(
                $"Producing a cache entry for the requested key ({DescribeKey(key)}) did not "
                + $"complete within "
                + $"{SharedCreationBudget}. The registration has been retired and the creation "
                + "cancelled, so a subsequent call will start a fresh attempt rather than joining "
                + "this one.",
                exception);
        }

        return FromCacheEntry<T>(key, produced);
    }

    /// <inheritdoc />
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

    /// <inheritdoc />
    /// <remarks>
    /// Answered from the category index this service already maintains for its own named invalidations, so
    /// the work is proportional to the family rather than to everything ever written - and so a caller that
    /// owns a key family gets the same cost the built-in invalidations get. A prefix with no members is not
    /// an error: the family simply held nothing.
    /// </remarks>
    /// <exception cref="ArgumentException">
    /// <paramref name="keyPrefix"/> is <see langword="null"/>, empty or white space.
    /// </exception>
    public void RemoveByPrefix(string keyPrefix)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(keyPrefix);

        lock (_registryGate)
        {
            // A tracked category is answered from the index, which is what the built-in invalidations use
            // and is proportional to the family.
            if (_keysByCategory.ContainsKey(keyPrefix))
            {
                EvictTrackedCategory(keyPrefix);

                return;
            }

            foreach (string trackedKey in _trackedKeys.Keys.ToArray())
            {
                if (trackedKey.StartsWith(keyPrefix, StringComparison.Ordinal))
                {
                    Evict(trackedKey);
                }
            }
        }
    }

    // THE EIGHT INVALIDATIONS
    // MIGRATION: -1 is no longer a wildcard. The legacy host clear passed -1 as a portal identifier to mean
    // "every portal".

    /// <inheritdoc />
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

            // Everything else this service wrote, portal, tab, module and user keys included. Keys is a
            // snapshot, so evicting while enumerating it is safe.
            foreach (string trackedKey in _trackedKeys.Keys)
            {
                Evict(trackedKey);
            }

            // Leaves both registries genuinely empty rather than merely mostly empty.
            _keysByCategory.Clear();
        }
    }

    /// <inheritdoc />
    public void InvalidatePortal(int portalId)
    {
        lock (_registryGate)
        {
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
    /// Evicts the tab's module collection and its module permissions, matching the legacy single-tab clear,
    /// and additionally the tracked module-settings category. The addition is deliberate: the legacy
    /// parameterless ClearModuleCache() was the only path that evicted module settings, and it did so by
    /// querying every module in the installation.
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
    public void InvalidateTabPermissions(int portalId)
    {
        lock (_registryGate)
        {
            Evict(FormatKey(TabPermissionCacheKey, portalId));
        }
    }

    /// <inheritdoc />
    public void InvalidateModulePermissions(int tabId)
    {
        lock (_registryGate)
        {
            Evict(FormatKey(ModulePermissionCacheKey, tabId));
        }
    }

    /// <inheritdoc />
    /// <exception cref="ArgumentException">
    /// <paramref name="userName"/> is <see langword="null"/>, empty or white space.
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
    public void InvalidateProfileDefinitions(int portalId)
    {
        lock (_registryGate)
        {
            Evict(FormatKey(ProfileDefinitionsCacheKey, portalId));
        }
    }

    /// <summary>
    /// Formats a single-placeholder legacy key template with an identifier, using the invariant culture so
    /// that a key never varies with the thread's culture.
    /// </summary>
    /// <param name="keyTemplate">One of the legacy key templates declared on this class.</param>
    /// <param name="identifier">The portal or tab identifier.</param>
    /// <returns>The composed cache key.</returns>
    private static string FormatKey(string keyTemplate, int identifier) =>
        string.Format(System.Globalization.CultureInfo.InvariantCulture, keyTemplate, identifier);

    /// <summary>Composes the two-placeholder legacy user key, preserving both pipe delimiters.</summary>
    /// <param name="portalId">The portal the membership belongs to.</param>
    /// <param name="userName">The user name.</param>
    /// <returns>The composed cache key.</returns>
    private static string FormatUserCacheKey(int portalId, string userName) =>
        string.Format(System.Globalization.CultureInfo.InvariantCulture, UserCacheKey, portalId, userName);

    /// <summary>Builds the ordered category vocabulary from the key templates declared on this class.</summary>
    /// <returns>Every declared category prefix, longest first.</returns>
    private static string[] BuildCategoryPrefixes()
    {
        string[] prefixes =
        [
            PrefixOf(PortalCacheKey),
            PrefixOf(TabCacheKey),
            PrefixOf(TabPermissionCacheKey),
            PrefixOf(TabModuleCacheKey),
            PrefixOf(ModulePermissionCacheKey),
            PrefixOf(ModuleCacheKey),
            PrefixOf(ProfileDefinitionsCacheKey),
            PrefixOf(UserCacheKey),
            ModuleSettingsCacheKeyPrefix,

            // The portal dictionary earns an entry of its own even though nothing sweeps it as a category,
            // because its literal key begins with the portal category's prefix.
            PortalDictionaryCacheKey,
        ];

        // Longest first, so that the most specific category claims a key whose prefix is also the start of
        // another category's prefix. Ordinal ordering breaks ties between equal lengths so the vocabulary
        // is deterministic rather than dependent on the literal order above.
        Array.Sort(
            prefixes,
            (left, right) => right.Length != left.Length
                ? right.Length - left.Length
                : string.CompareOrdinal(left, right));

        return prefixes;
    }

    /// <summary>Determines which declared category a composed key belongs to.</summary>
    /// <param name="key">The composed cache key.</param>
    /// <returns>The matching category prefix, or <see langword="null"/> when there is none.</returns>
    private static string? CategoryOf(string key)
    {
        foreach (string prefix in CategoryPrefixes)
        {
            if (key.StartsWith(prefix, StringComparison.Ordinal))
            {
                return prefix;
            }
        }

        return null;
    }

    /// <summary>
    /// Returns the fixed leading text of a legacy key template - everything before its first placeholder -
    /// so that a category can be matched without restating the literal and letting the two drift apart.
    /// </summary>
    /// <param name="keyTemplate">One of the legacy key templates declared on this class.</param>
    /// <returns>The template's prefix, or the template itself when it has no placeholder.</returns>
    private static string PrefixOf(string keyTemplate)
    {
        int placeholder = keyTemplate.IndexOf('{');

        return placeholder < 0 ? keyTemplate : keyTemplate[..placeholder];
    }

    /// <summary>
    /// Describes a cache key for a diagnostic without disclosing it, as a declared category paired with a
    /// fingerprint.
    /// </summary>
    /// <param name="key">The key to describe.</param>
    /// <returns>Text of the form <c>category UserInfo, fingerprint 0123456789ABCDEF</c>.</returns>
    private static string DescribeKey(string key)
    {
        string category = UnrecognisedKeyCategory;

        foreach (string prefix in KeyCategoryPrefixes)
        {
            if (key.StartsWith(prefix, StringComparison.Ordinal))
            {
                category = prefix.TrimEnd('|');
                break;
            }
        }

        return FormattableString.Invariant($"category {category}, fingerprint {FingerprintOf(key)}");
    }

    /// <summary>Computes the per-process keyed fingerprint of a cache key.</summary>
    /// <param name="key">The key to fingerprint.</param>
    /// <returns><see cref="KeyFingerprintBytes"/> bytes of the keyed digest, in upper-case hexadecimal.</returns>
    /// <remarks>
    /// A keyed hash, not a plain one, for the reason given beside <see cref="KeyFingerprintSecret"/>. The
    /// one-shot static form is used so no instance is created and nothing needs disposing, which also makes
    /// the method safe to call from any thread without synchronisation.
    /// </remarks>
    private static string FingerprintOf(string key)
    {
        byte[] digest = HMACSHA256.HashData(KeyFingerprintSecret, Encoding.UTF8.GetBytes(key));

        return Convert.ToHexString(digest, 0, KeyFingerprintBytes);
    }

    /// <summary>
    /// Converts a raw cache entry to the caller's type, round-tripping a stored null rather than
    /// substituting anything for it, and refusing a value the caller cannot legally receive.
    /// </summary>
    /// <typeparam name="T">The caller's value type.</typeparam>
    /// <param name="key">The key the entry was read under, used only to name its category.</param>
    /// <param name="entry">The raw entry read from the cache.</param>
    /// <returns>The typed value.</returns>
    /// <exception cref="InvalidOperationException">
    /// A live entry exists but is not assignable to <typeparamref name="T"/>, which means one key is being
    /// shared by two different value shapes.
    /// </exception>
    private static T FromCacheEntry<T>(string key, object? entry)
    {
        // A stored null round-trips as the caller's own default. This is not a sentinel: the
        // absence of an ENTRY is reported by the callers of this helper, never by its result.
        if (entry is null)
        {
            return default!;
        }

        if (entry is T typed)
        {
            return typed;
        }

        // MIGRATION: net-new, with no legacy counterpart. The legacy accessor returned an untyped value and
        // every one of the measured call sites cast it at the call site, so a key reused under two shapes
        // surfaced as a cast failure in whichever caller happened to read second.
        throw new InvalidOperationException(
            $"The cache entry for the requested key ({DescribeKey(key)}) holds "
            + $"{entry.GetType().FullName} and cannot be delivered as {typeof(T).FullName}. One "
            + "cache key is being used for two different value shapes, which the coalescing "
            + "registry keeps apart but a single stored entry cannot. Give the two shapes "
            + "distinct keys.");
    }

    /// <summary>
    /// Builds the refusal message for a miss that arrives with caching switched off or with a non-positive
    /// expiry.
    /// </summary>
    /// <param name="key">The key that missed.</param>
    /// <param name="expiration">The expiry the caller supplied.</param>
    /// <returns>The message.</returns>
    private string BuildCachingDisabledMessage(string key, TimeSpan expiration)
    {
        string multiplier = FormattableString.Invariant($"{_performanceMultiplier}");
        string requested = FormattableString.Invariant($"{expiration}");

        return $"No live cache entry exists for the requested key ({DescribeKey(key)}) and this service will not create one: "
            + $"{CachingOptions.SectionName}:{nameof(CachingOptions.PerformanceMultiplier)} is "
            + $"{multiplier} and the requested expiration is {requested}. When the effective "
            + "cache lifetime is not positive, the legacy read path this replaces skipped the "
            + "database load itself rather than merely skipping the cache write, so the calling "
            + "application service must branch on its own computed lifetime before it gets here. "
            + "Fabricating a value or running a load the legacy path would have skipped are both "
            + "wrong, so neither is done.";
    }

    /// <summary>Reads a raw entry and, on a miss, reconciles the tracked-key registry.</summary>
    /// <param name="key">The cache entry key.</param>
    /// <param name="entry">The raw entry when one is live; otherwise <see langword="null"/>.</param>
    /// <returns><see langword="true"/> when a live entry exists.</returns>
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
    /// Drops a registration whose entry is no longer live, leaving a live entry's registration alone. Takes
    /// <see cref="_registryGate"/> itself, so callers must NOT already hold it.
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
            // Re-read under the gate. Set holds the same gate across its register-then-write pair, so if a
            // write won the race the entry is live now and its registration must stand - dropping it would
            // hide a live entry from category eviction.
            if (!_memoryCache.TryGetValue(key, out _))
            {
                Forget(key);
            }
        }
    }

    /// <summary>
    /// Evicts one entry and its registration together, and records that an invalidation happened. Callers
    /// must already hold <see cref="_registryGate"/>.
    /// </summary>
    /// <param name="key">The key to evict.</param>
    private void Evict(string key)
    {
        _invalidationGeneration++;

        _memoryCache.Remove(key);
        Forget(key);
    }

    /// <summary>
    /// Withdraws a key from both registries together. Callers must already hold <see
    /// cref="_registryGate"/>.
    /// </summary>
    /// <param name="key">The key to withdraw.</param>
    /// <remarks>
    /// Every path that drops a registration goes through here, so the tracked-key registry and the category
    /// index can never disagree about which keys are live. That matters most on the natural-expiry path:
    /// dropping a key from one registry but not the other would leave the category index accumulating names
    /// whose entries had long since gone, which is the growth this index was introduced to remove.
    /// </remarks>
    private void Forget(string key)
    {
        _trackedKeys.TryRemove(key, out _);

        string? category = CategoryOf(key);
        if (category is not null && _keysByCategory.TryGetValue(category, out HashSet<string>? members))
        {
            members.Remove(key);

            if (members.Count == 0)
            {
                _keysByCategory.Remove(category);
            }
        }
    }

    /// <summary>
    /// Evicts every tracked key in one category. Callers must already hold <see cref="_registryGate"/>.
    /// </summary>
    /// <param name="keyPrefix">The category prefix, matched ordinally.</param>
    /// <remarks>
    /// Reads the category index rather than walking the whole registry, so the work is proportional to the
    /// category being invalidated. This matters because one portal invalidation evicts three categories:
    /// without the index each of those three would walk every key this service had ever written, including
    /// keys belonging to unrelated categories and keys whose entries had already expired.
    /// </remarks>
    private void EvictTrackedCategory(string keyPrefix)
    {
        if (!_keysByCategory.TryGetValue(keyPrefix, out HashSet<string>? members))
        {
            return;
        }

        // Snapshot first: Evict mutates this very set, and may remove it outright once its last
        // member goes.
        foreach (string trackedKey in members.ToArray())
        {
            Evict(trackedKey);
        }
    }

    /// <summary>
    /// Performs the one shared creation for a key: re-checks the cache, invokes the caller's factory,
    /// stores the result and releases the in-flight registration.
    /// </summary>
    /// <typeparam name="T">The caller's value type.</typeparam>
    /// <param name="key">The cache entry key.</param>
    /// <param name="factory">The caller's cancellation-aware producer.</param>
    /// <param name="expiration">How long a newly produced entry stays live.</param>
    /// <param name="registration">The key-and-shape slot this creation occupies.</param>
    /// <param name="creation">
    /// The creation itself, which owns the budget the factory observes and the abandonment flag the
    /// publication below consults.
    /// </param>
    /// <returns>The produced or concurrently published value, boxed for the shared task.</returns>
    /// <remarks>
    /// Accepts no caller token by design, so that no caller's lifetime can be attached to work that other
    /// callers are waiting on. Per-caller cancellation is applied by the awaiting member instead, and this
    /// creation is bounded by its own budget rather than by anyone's token.
    /// </remarks>
    private async Task<object?> LoadAndCacheAsync<T>(
        string key,
        Func<CancellationToken, Task<T>> factory,
        TimeSpan expiration,
        (string Key, Type ValueType) registration,
        SharedCreation creation)
    {
        try
        {
            long observedGeneration;

            // Second check, now that this creation has been elected.
            lock (_registryGate)
            {
                if (_memoryCache.TryGetValue(key, out object? published))
                {
                    return published;
                }

                observedGeneration = _invalidationGeneration;
            }

            // Not a caller's token: this one creation serves every coalesced caller, so it must outlive any
            // individual caller that withdraws. It is bounded all the same - a creation bound to nothing at
            // all is what let a single stalled factory hold a key permanently.
            T produced = await factory(creation.BeginWork(CreationWorkBudget)).ConfigureAwait(false);

            // MIGRATION: net-new, and the reason this publication is conditional.
            lock (_registryGate)
            {
                if (_invalidationGeneration == observedGeneration && !creation.IsAbandoned)
                {
                    Publish(key, produced, expiration);
                }
            }

            return produced;
        }
        finally
        {
            // The budget is released first, so the source this creation owns does not outlive the work it
            // bounded. Then the registration is withdrawn by COMPARING IDENTITY, not by the slot alone.
            creation.Complete();

            _inFlightLoads.TryRemove(
                new KeyValuePair<(string Key, Type ValueType), SharedCreation>(registration, creation));
        }
    }

    /// <summary>
    /// One coalesced creation of one cache entry: the shared work, the budget that bounds it, and whether
    /// it has been retired in favour of a later attempt.
    /// </summary>
    /// <remarks>
    /// MIGRATION: net-new. It exists because a key and a requested shape name a SLOT rather than an
    /// attempt, and three of this service's guarantees need to distinguish the two.
    /// </remarks>
    private sealed class SharedCreation
    {
        /// <summary>Serialises the budget's creation, cancellation and release against each other.</summary>
        private readonly object _gate = new();

        /// <summary>
        /// The budget bounding the work, or <see langword="null"/> before it starts and after it ends.
        /// </summary>
        private CancellationTokenSource? _budget;

        /// <summary>Whether this attempt has been retired in favour of a later one.</summary>
        private bool _abandoned;

        /// <summary>
        /// The shared work, assigned once immediately after construction and before this instance is
        /// published to the registry, so no other thread can observe it unset.
        /// </summary>
        internal Lazy<Task<object?>> Work { get; set; } = null!;

        /// <summary>Whether this attempt has been retired, in which case its result must not be published.</summary>
        internal bool IsAbandoned
        {
            get
            {
                lock (_gate)
                {
                    return _abandoned;
                }
            }
        }

        /// <summary>Opens the budget bounding this attempt and returns the token the factory must observe.</summary>
        /// <param name="budget">How long the work may run before its token is cancelled.</param>
        /// <returns>The token bounding the work.</returns>
        internal CancellationToken BeginWork(TimeSpan budget)
        {
            lock (_gate)
            {
                if (_abandoned)
                {
                    return new CancellationToken(canceled: true);
                }

                _budget = new CancellationTokenSource(budget);

                return _budget.Token;
            }
        }

        /// <summary>Retires this attempt: its result will not be published, and its work is asked to stop.</summary>
        /// <remarks>
        /// Idempotent, and safe to call before the work starts - the flag is set either way, and <see
        /// cref="BeginWork"/> then hands the factory an already-cancelled token.
        /// </remarks>
        internal void Abandon()
        {
            lock (_gate)
            {
                _abandoned = true;

                // Cancelling under the monitor that also owns the release is what makes this safe: the
                // reference is cleared before it is disposed, so this can never reach a disposed source.
                _budget?.Cancel();
            }
        }

        /// <summary>Releases the budget once the work has finished, however it finished.</summary>
        internal void Complete()
        {
            CancellationTokenSource? finished;

            lock (_gate)
            {
                finished = _budget;
                _budget = null;
            }

            // Disposed outside the monitor because disposal waits for any callback already running,
            // and a canceller holding the monitor would then be waiting on the disposer that holds it.
            finished?.Dispose();
        }
    }
}
