// MIGRATION: Replaces Library/Components/Providers/Caching/DataCache.vb (317 lines) - the
// module-level shared utility that 116 in-scope legacy call sites reached statically and which
// forwarded every operation to CachingProvider.Instance, a reflection-resolved singleton. Nothing
// here is reachable statically: the container holds one instance and hands it to consumers by
// constructor injection, which is what makes cache behaviour substitutable in a test.
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
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
/// <para>
/// Diagnostics. No message produced by this class contains a cache key. One of the legacy key
/// templates embeds a user name, so a key is potentially personal data, and the one refusal this
/// class raises is recorded by the API layer's structured log. Where an entry has to be named,
/// it is named by its declared category and a per-process keyed fingerprint through the single
/// funnel that exists for the purpose; the section comment above the fingerprint secret records
/// the reasoning and the trade-off accepted.
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
    // THE FOUR CONVENTIONAL PERFORMANCE MULTIPLIERS
    //
    // Ported from the PerformanceSettings enumeration at
    // Library/Components/Shared/Globals.vb:L66-L75, whose own comment records that the values
    // were chosen to keep cache scaling linear. They are constants rather than a second type
    // because this file declares exactly one type.
    //
    // They are NAMES, not an allow-list. The legacy reader cast an arbitrary host-settings
    // integer to that enumeration unchecked (Globals.vb:L229), so a configuration outside these
    // four was legal then and stays legal here; the only boundary is that the multiplier may not
    // be negative, and CachingOptions.Validate is the single place that boundary is stated.
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


    /// <summary>
    /// Greatest length of time a shared creation may run before it is abandoned.
    /// </summary>
    /// <remarks>
    /// A creation is shared by every caller that missed the same key, so it cannot be bound to any
    /// one caller's lifetime without letting that caller's withdrawal fail the others. Absent a
    /// budget, that isolation would leave it bound to nothing at all: a factory that never completed
    /// would leave its registration in place permanently, and every later caller for that key would
    /// join the same creation that was never going to finish. This budget is what bounds it. It is
    /// generous, because the work behind it is a database read that a loaded server may legitimately
    /// take seconds to answer, and it exists to break a stall rather than to enforce a latency
    /// target.
    /// </remarks>
    private static readonly TimeSpan SharedCreationBudget = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Every declared category prefix, longest first.
    /// </summary>
    /// <remarks>
    /// Longest-first ordering is what makes classification unambiguous where one prefix begins
    /// with another. The portal template's prefix is a prefix of the portal-dictionary key, so a
    /// shortest-first walk would file that key under the wrong category; ordered this way the more
    /// specific match always wins. The values are derived from the key constants on this class
    /// rather than restated, so a template and its category cannot drift apart.
    /// </remarks>
    private static readonly string[] CategoryPrefixes = BuildCategoryPrefixes();
    // ---------------------------------------------------------------------------------------
    // NAMING AN ENTRY IN A DIAGNOSTIC WITHOUT NAMING ITS KEY
    //
    // A cache key is not safe to put in a message. UserCacheKey is "UserInfo|{0}|{1}" and its
    // second placeholder is a USER NAME (DataCache.vb:L79 and FormatUserCacheKey below), so a
    // key composed for a real account carries personal data, and the message built from it
    // travels to the structured log through the API layer's exception handler. Nothing about the
    // key's own text is needed to diagnose the one failure that reports it - the caller needs to
    // know WHICH FAMILY of entry missed and WHETHER two log lines concern the same entry - so
    // the members below answer exactly those two questions and nothing else.
    //
    // A CATEGORY answers the first. It is one of the fixed literals this class already declares,
    // resolved through PrefixOf so that no literal is restated, and it therefore contains no
    // caller-supplied text by construction rather than by filtering.
    //
    // A KEYED FINGERPRINT answers the second. A bare SHA-256 digest was considered and REJECTED:
    // these keys are short and highly predictable - "UserInfo|0|admin" is a guess, not a search -
    // so an unkeyed digest of one is reversible by anyone holding the log and a list of candidate
    // user names, which would defeat the whole exercise. Keying the hash with a secret this
    // process generates at startup removes that: the fingerprint is stable for the life of the
    // process, so two entries about one key correlate, and it is meaningless outside that
    // process, so it cannot be joined to a precomputed table, to another deployment, or to the
    // same installation's logs from before a restart. Losing cross-restart correlation is the
    // deliberate price of that property and is recorded in MIGRATION_NOTES.md.
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// Category reported for a key that matches none of the declared families.
    /// </summary>
    /// <remarks>
    /// A fixed word, and specifically NOT the key itself. The unrecognised case is the one where
    /// falling back to the raw text would feel most defensible - the key is unfamiliar, so it
    /// looks like the interesting thing to record - and it is the one where the raw text is least
    /// trustworthy, because an unrecognised key is by definition one this class did not compose.
    /// </remarks>
    private const string UnrecognisedKeyCategory = "unrecognised";

    /// <summary>
    /// Number of digest bytes rendered into a fingerprint, giving twice as many hexadecimal
    /// characters.
    /// </summary>
    /// <remarks>
    /// Eight bytes is ample to tell one entry from another within a single process and short
    /// enough to read at a glance. Truncation is not what makes the fingerprint one-way - the
    /// secret is - so the length is chosen for legibility rather than for strength.
    /// </remarks>
    private const int KeyFingerprintBytes = 8;

    /// <summary>
    /// The size every entry declares against the store's size limit: one entry.
    /// </summary>
    /// <remarks>
    /// The limit this counts against is configured where the store is registered, and the two belong
    /// together - a store with a limit rejects an entry that declares no size, so this value is not
    /// optional. Counting entries rather than estimating bytes is deliberate; the reasoning is recorded at
    /// the point of use, where the alternative would have to be applied.
    /// </remarks>
    private const long SingleEntrySize = 1;

    /// <summary>
    /// The per-process secret that keys every fingerprint.
    /// </summary>
    /// <remarks>
    /// Generated once, from the cryptographic random source, and never configured: a configured
    /// value would be shared between deployments and could be committed to source control, both
    /// of which would restore exactly the correlation this field exists to prevent. It is never
    /// logged, never exposed and never leaves this class.
    /// </remarks>
    private static readonly byte[] KeyFingerprintSecret = RandomNumberGenerator.GetBytes(32);

    /// <summary>
    /// The declared key families, longest prefix first, used to classify a key for a diagnostic.
    /// </summary>
    /// <remarks>
    /// Every entry is derived from a constant on this class rather than written out again, so a
    /// renamed key cannot leave a stale category behind. The descending-length ordering is
    /// load bearing and is applied here rather than trusted to the declaration order:
    /// <see cref="PortalDictionaryCacheKey"/> begins with the prefix of
    /// <see cref="PortalCacheKey"/>, so a first-match scan in declaration order would report
    /// the tab-to-portal dictionary as an individual portal. Sorting removes that hazard for
    /// every pair, including ones a future key might introduce.
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
    /// The validated performance multiplier, read once at construction. Immutable for the
    /// lifetime of the singleton, which is what makes it safe to read without synchronisation.
    /// </summary>
    private readonly int _performanceMultiplier;

    /// <summary>
    /// In-flight creations, keyed by cache key AND requested value shape, so that concurrent
    /// misses for one key perform one load between them instead of one load each - while two
    /// callers requesting one key as two different shapes are kept apart.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The shape forms part of the identity because coalescing is only ever correct for callers
    /// that are genuinely asking for the same thing. Two callers wanting the same key as two
    /// different shapes are not, and joining them would deliver one of them a value it cannot
    /// hold - intermittently, decided purely by arrival order. The default comparer for this
    /// composite compares the key ordinally, matching the tracked-key registry.
    /// </para>
    /// <para>
    /// The VALUE is a <see cref="SharedCreation"/> rather than the shared task itself, and that
    /// indirection is what makes every removal from this dictionary identity-fenced. A key and a
    /// shape identify a SLOT; they do not identify the particular creation occupying it, because a
    /// retired creation is replaced by a later one under exactly the same pair. Removing by the pair
    /// alone therefore lets a finishing creation delete its own SUCCESSOR'S registration, which
    /// re-opens the door to duplicate loads the coalescing exists to close. Holding the creation
    /// gives every removal below a value to compare against, and gives the retirement path something
    /// to mark and cancel.
    /// </para>
    /// </remarks>
    private readonly ConcurrentDictionary<(string Key, Type ValueType), SharedCreation> _inFlightLoads = new();

    /// <summary>
    /// Every key this service has written and not yet evicted. The value is unused - this is a
    /// concurrent set. It is the ONLY thing the invalidations consult, and it is a concurrent
    /// collection rather than a plain set purely so that a read miss can test membership
    /// without taking <see cref="_registryGate"/>.
    /// </summary>
    private readonly ConcurrentDictionary<string, byte> _trackedKeys = new(StringComparer.Ordinal);

    /// <summary>
    /// The keys this service has written, grouped by the category their key template defines.
    /// </summary>
    /// <remarks>
    /// This index is what makes a category invalidation proportional to the category rather than
    /// to the whole registry. Without it the only way to find one category's keys is to walk every
    /// tracked key and test its prefix, and a single portal invalidation performs three such
    /// category evictions - so one administrative change would walk the whole registry three times
    /// over. Maintained under <see cref="_registryGate"/> alongside
    /// <see cref="_trackedKeys"/>, so the two can never disagree about which keys are live. A
    /// category whose last key goes away is dropped rather than retained as an empty set.
    /// </remarks>
    private readonly Dictionary<string, HashSet<string>> _keysByCategory = new(StringComparer.Ordinal);

    /// <summary>
    /// Counts invalidations, so a creation that began before one can detect that it did.
    /// </summary>
    /// <remarks>
    /// Mutated only under <see cref="_registryGate"/>, and only ever incremented. A creation
    /// records this value before it calls the caller's factory and presents it again when it comes
    /// back; a mismatch means an invalidation intervened while the factory was running, and the
    /// result is therefore delivered to the waiting callers but not published.
    /// </remarks>
    private long _invalidationGeneration;

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
    /// Whether caching is switched on at all. A multiplier of <see cref="NoCaching"/> makes every
    /// computed expiry zero, which the legacy sites treated as an instruction to skip the work
    /// rather than merely to skip the write.
    /// </summary>
    private bool IsCachingEnabled => _performanceMultiplier > NoCaching;

    /// <summary>
    /// Proves the bound options usable and returns the multiplier, failing loudly at construction
    /// when they are not.
    /// </summary>
    /// <param name="options">The bound caching options.</param>
    /// <returns>The configured multiplier, once proven usable.</returns>
    /// <remarks>
    /// <para>
    /// The rule itself is NOT restated here. It is declared by
    /// <see cref="CachingOptions.Validate"/>, beside the value it governs, and this method only
    /// applies it -- so the two cannot disagree. The check is repeated at construction as well as
    /// at start-up because this service can also be constructed directly, in a test or by a caller
    /// that never went through the host's options validation, and a silently wrong cache lifetime
    /// is worse than a refused construction.
    /// </para>
    /// <para>
    /// MIGRATION: the four multipliers the legacy <c>PerformanceSettings</c> enumeration declared are
    /// NOT an allow-list, and must never be turned into one here. The legacy reader cast an arbitrary
    /// host-settings integer straight to that enumeration
    /// (<c>Library/Components/Shared/Globals.vb:L229</c>), and a Visual Basic conversion to an
    /// enumeration is unchecked, so a stored <c>4</c> genuinely produced a multiplier of <c>4</c>.
    /// Refusing a value outside the four would therefore tighten a configuration the legacy
    /// installation accepted, and it would also contradict
    /// <see cref="CachingOptions.PerformanceMultiplier"/>, which documents any non-negative integer as
    /// legitimate. The four constants remain declared on this class because they are the values an
    /// operator will normally choose and callers name them - nothing more.
    /// </para>
    /// </remarks>
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
    /// <remarks>
    /// A miss also drops any stale registration for <paramref name="key"/>. An entry evicted for
    /// any reason, natural expiry included, withdraws its own registration through the eviction
    /// callback registered when it was written; this path reconciles anything that callback has not
    /// yet reached, so the two are belt and braces rather than either one alone.
    /// </remarks>
    /// <exception cref="ArgumentException">
    /// <paramref name="key"/> is <see langword="null"/>, empty or white space.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// A live entry exists under <paramref name="key"/> but is not a <typeparamref name="T"/>.
    /// Two callers sharing one key with different value shapes is a defect in the callers, and it
    /// surfaces here deliberately rather than being disguised as a miss. The refusal names the
    /// key's category and both shapes, never the key itself, because a composed key can carry a
    /// user name.
    /// </exception>
    public T? Get<T>(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        // MIGRATION: absence is the null equivalent of T and nothing else. The legacy null
        // helpers (Library/Components/Shared/Null.vb) mapped an absent integer to -1 and an
        // absent string to the empty string, and both are legitimate stored values in the
        // existing schema, so neither may stand in for a miss.
        return TryRead(key, out object? entry) ? FromCacheEntry<T>(key, entry) : default;
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

        lock (_registryGate)
        {
            Publish(key, value, expiration);
        }
    }

    /// <summary>
    /// Writes an entry and registers it. Callers must already hold
    /// <see cref="_registryGate"/> and must already have established that caching is on and that
    /// <paramref name="expiration"/> is positive.
    /// </summary>
    /// <typeparam name="T">The value's type.</typeparam>
    /// <param name="key">The cache entry key.</param>
    /// <param name="value">The value to store.</param>
    /// <param name="expiration">How long the entry stays live.</param>
    private void Publish<T>(string key, T value, TimeSpan expiration)
    {
        // A SIZE IS DECLARED ON EVERY ENTRY, and it must be: the host configures the underlying store with
        // a size limit, and that store REFUSES an entry carrying no size once a limit is set - so omitting
        // it here would turn every cache write in the application into an exception rather than into a
        // silently unbounded store. The two settings are therefore a pair and neither may be changed alone.
        //
        // The unit is ONE ENTRY, not an estimate of bytes. The values written here are object graphs -
        // projected records, string sequences, dictionaries - whose true footprint cannot be measured
        // without walking them, and a walk on every write would cost more than the cache saves. Counting
        // entries is honest about what it bounds: it caps how MANY answers are retained, which is exactly
        // the quantity a caller-influenced key space can inflate, and it leaves the size of one answer to
        // the projection that produced it. Every entry declaring the same size also makes the store's
        // eviction order purely least-recently-used, which is the behaviour a read-through cache wants.
        MemoryCacheEntryOptions entryOptions = new()
        {
            SlidingExpiration = expiration,
            Size = SingleEntrySize,
        };

        // An entry that goes away on its own must take its registration with it. Without this the
        // registry only ever shrank when somebody happened to ask for the very key that had
        // expired, so it accumulated the name of every key ever written and every category
        // eviction paid to walk them.
        //
        // The callback is safe to take the gate from, and the reason is worth stating exactly
        // because it is not the obvious one. The store dispatches this callback to the thread pool
        // rather than running it inline, so it never arrives on the thread that triggered the
        // eviction and cannot re-enter a gate that thread is holding. That also settles the
        // ordering: an eviction this service performs holds the gate across Remove AND the
        // withdrawal that follows it, so a callback raised by that removal cannot acquire the gate
        // until the withdrawal has already happened, and it then finds nothing to do and returns on
        // its lock-free path. A natural expiry arrives with no gate held by anyone and does the
        // withdrawal itself. Either way the work happens exactly once, and Forget is idempotent
        // besides, so an ordering this comment has misjudged still cannot corrupt either registry.
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
    /// <paramref name="factory"/> therefore receives a token of the creation's own, never a
    /// caller's - see the next paragraph for what bounds it. A caller that abandons its await
    /// leaves the creation running, and its result is still cached for whoever asks next, which is
    /// exactly what the coalesced callers are waiting for.
    /// </para>
    /// <para>
    /// Bounded, so that isolation cannot become a stall. Because the shared creation answers to no
    /// caller's token, it needs a limit of its own: a factory that never completes would otherwise
    /// hold its registration permanently and every later caller for that key would join the same
    /// creation that was never going to finish. Two limits apply. The factory receives a token that
    /// elapses, which cancels a factory that observes it; and the wait itself is bounded, which
    /// covers a factory that does not - in that case the registration is retired so the next caller
    /// begins a fresh attempt.
    /// </para>
    /// <para>
    /// Coalescing is shape-aware. The in-flight identity is the key together with the requested
    /// value shape, so two callers asking for one key as two different shapes are never joined;
    /// only callers genuinely asking for the same thing share a creation.
    /// </para>
    /// <para>
    /// Publication is invalidation-aware. A creation records the invalidation count when it begins
    /// and publishes only if that count has not moved, so a value produced before an invalidation
    /// cannot be written back after it. The waiting callers still receive the value; only the cache
    /// write is withheld.
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
    /// positive - see the remarks on why this is a refusal rather than a pass-through. Also raised
    /// when a live entry exists but cannot be delivered as <typeparamref name="T"/>, which means
    /// one key is being shared by two different value shapes.
    /// </exception>
    /// <exception cref="TimeoutException">
    /// The shared creation did not complete within its budget. The registration is retired first,
    /// so a subsequent call starts a fresh attempt rather than joining the stalled one. No message
    /// raised here names the key, only its category.
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
            return FromCacheEntry<T>(key, entry);
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

        // MIGRATION: net-new. The registration is identified by the key AND the value shape, not
        // by the key alone. Coalescing exists to stop two callers loading the same thing twice,
        // but two callers asking for the same key as two different shapes are not asking for the
        // same thing: joining them would hand one of them a value it cannot hold, and it would do
        // so intermittently, depending only on which arrived first. The legacy accessor could not
        // encounter this because it returned an untyped value and every call site cast for itself.
        (string Key, Type ValueType) registration = (key, typeof(T));

        // The creation is deliberately NOT handed this caller's token. See the remarks: a shared
        // creation bound to one caller's lifetime lets that caller's withdrawal cancel work other
        // callers are waiting on, so a healthy caller would surface a cancellation it never asked
        // for. Isolation is enforced here, at the single point where the shared work is started.
        //
        // The creation is handed ITSELF so that the work it performs can identify the registration it
        // owns. Both removals - the retirement below and the release in the creation's own finally -
        // then compare that identity, so neither can delete a registration it does not own.
        SharedCreation creation = _inFlightLoads.GetOrAdd(
            registration,
            _ =>
            {
                SharedCreation created = new();

                // Assigned immediately, and before the value is published to the dictionary, so no
                // caller can observe a creation whose work is not yet reachable. A losing thread's
                // allocation is discarded by GetOrAdd with its work never started, because the work
                // is lazy.
                created.Work = new Lazy<Task<object?>>(
                    () => LoadAndCacheAsync(key, factory, expiration, registration, created),
                    LazyThreadSafetyMode.ExecutionAndPublication);

                return created;
            });

        object? produced;

        try
        {
            // Accessing Value starts the creation at most once, however many callers arrive.
            // WaitAsync gives this caller its own cancellation without disturbing the shared work,
            // and a budget besides: the creation is bound to no caller's lifetime, so without one
            // a caller could wait on it indefinitely.
            produced = await creation.Work.Value
                .WaitAsync(SharedCreationBudget, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // The creation has outrun its budget, which means a factory that is not observing the
            // token it was handed. Retiring the registration is the part that matters: left in
            // place it would be joined by every later caller for this key, so one stalled load
            // would become a permanent refusal to serve that key at all.
            //
            // MIGRATION: net-new, and the three parts of this retirement are separable defects if any
            // one of them is dropped. RETIREMENT IS ANNOUNCED TO THE CREATION FIRST, which does two
            // things a bare removal did not. It CANCELS the creation's own budget, so a factory that
            // observes the token it was handed stops now instead of continuing to run unobserved -
            // without that, every subsequent caller that timed out would leave another live factory
            // behind it and the count of concurrent factories for one key would be bounded by
            // nothing. And it MARKS the creation abandoned, so if the factory ignores its token and
            // finishes later it delivers its value to nobody's cache: a replacement creation may
            // already have published a newer one, and an invalidation generation that never moved
            // cannot detect that on its own. Only then is the registration removed, and the comparing
            // overload is used so that a REPLACEMENT - one a later caller may already have started -
            // is never mistaken for this one and removed.
            creation.Abandon();

            _inFlightLoads.TryRemove(
                new KeyValuePair<(string Key, Type ValueType), SharedCreation>(registration, creation));

            throw new TimeoutException(
                $"Producing a cache entry for the requested key ({DescribeKey(key)}) did not "
                + $"complete within "
                + $"{SharedCreationBudget}. The registration has been retired and the creation "
                + "cancelled, so a subsequent call will start a fresh attempt rather than joining "
                + "this one.");
        }

        return FromCacheEntry<T>(key, produced);
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
            // Keys is a snapshot, so evicting while enumerating it is safe. This member alone walks
            // the whole registry, and that is not the scan the category index removed: here
            // "everything" IS the scope, so every tracked key is a key that has to go.
            foreach (string trackedKey in _trackedKeys.Keys)
            {
                Evict(trackedKey);
            }

            // Leaves both registries genuinely empty rather than merely mostly empty.
            _keysByCategory.Clear();
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
    /// Builds the ordered category vocabulary from the key templates declared on this class.
    /// </summary>
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

            // The portal dictionary earns an entry of its own even though nothing sweeps it as a
            // category, because its literal key begins with the portal category's prefix. Without
            // it the dictionary is filed under a category it is not a member of, and it would be
            // carried off as collateral the day anyone sweeps the portal category. Registering it
            // here and sorting longest-first makes that impossible rather than merely unlikely.
            PortalDictionaryCacheKey,
        ];

        // Longest first, so that the most specific category claims a key whose prefix is also the
        // start of another category's prefix. Ordinal ordering breaks ties between equal lengths
        // so the vocabulary is deterministic rather than dependent on the literal order above.
        Array.Sort(
            prefixes,
            (left, right) => right.Length != left.Length
                ? right.Length - left.Length
                : string.CompareOrdinal(left, right));

        return prefixes;
    }

    /// <summary>
    /// Determines which declared category a composed key belongs to.
    /// </summary>
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
    /// Describes a cache key for a diagnostic without disclosing it, as a declared category
    /// paired with a fingerprint.
    /// </summary>
    /// <param name="key">The key to describe. Never reproduced, in whole or in part.</param>
    /// <returns>
    /// Text of the form <c>category UserInfo, fingerprint 0123456789ABCDEF</c>. Never
    /// <see langword="null"/> and never empty.
    /// </returns>
    /// <remarks>
    /// <para>
    /// This is the only member that turns a key into text for human consumption, and it is
    /// deliberately the only one: a single funnel is what makes "no key reaches a message"
    /// checkable by reading one method rather than by auditing every message in the class. The
    /// section comment beside <see cref="KeyFingerprintSecret"/> records why a category and a
    /// keyed fingerprint are the two facts reported, and why an unkeyed digest was rejected.
    /// </para>
    /// <para>
    /// The category is trimmed of a trailing pipe so that the user-entry family reads as
    /// <c>UserInfo</c> rather than <c>UserInfo|</c>. The delimiter is load bearing in the key
    /// itself and is left untouched there; this affects the label alone. Matching is ordinal,
    /// matching how the keys are composed and compared everywhere else in this class.
    /// </para>
    /// </remarks>
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

    /// <summary>
    /// Computes the per-process keyed fingerprint of a cache key.
    /// </summary>
    /// <param name="key">The key to fingerprint.</param>
    /// <returns>
    /// <see cref="KeyFingerprintBytes"/> bytes of the keyed digest, in upper-case hexadecimal.
    /// </returns>
    /// <remarks>
    /// A keyed hash, not a plain one, for the reason given beside
    /// <see cref="KeyFingerprintSecret"/>. The one-shot static form is used so no instance is
    /// created and nothing needs disposing, which also makes the method safe to call from any
    /// thread without synchronisation. The digest is truncated for legibility; the secret, not
    /// the truncation, is what makes the result one-way.
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
    /// A live entry exists but is not assignable to <typeparamref name="T"/>, which means one key
    /// is being shared by two different value shapes.
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

        // MIGRATION: net-new, with no legacy counterpart. The legacy accessor returned an
        // untyped value and every one of the measured call sites cast it at the call site, so a
        // key reused under two shapes surfaced as a cast failure in whichever caller happened to
        // read second. Generics moved that cast in here, which concentrates the fault rather than
        // removing it, so it is detected and named instead of thrown blind: an unchecked cast
        // would raise a bare InvalidCastException naming only the two types, leaving no clue
        // which cache key was being shared. The key itself is deliberately NOT quoted - only its
        // category - because a composed key can carry a user name.
        throw new InvalidOperationException(
            $"The cache entry for the requested key ({DescribeKey(key)}) holds "
            + $"{entry.GetType().FullName} and cannot be delivered as {typeof(T).FullName}. One "
            + "cache key is being used for two different value shapes, which the coalescing "
            + "registry keeps apart but a single stored entry cannot. Give the two shapes "
            + "distinct keys.");
    }


    /// <summary>
    /// Builds the refusal message for a miss that arrives with caching switched off or with a
    /// non-positive expiry.
    /// </summary>
    /// <param name="key">
    /// The key that missed. It is used only to derive the non-disclosing descriptor described on
    /// <see cref="DescribeKey(string)"/>; neither the key nor any part of it appears in the
    /// returned message.
    /// </param>
    /// <param name="expiration">The expiry the caller supplied.</param>
    /// <returns>The message.</returns>
    /// <remarks>
    /// The two values that ARE reported - the configured multiplier and the requested expiry -
    /// are safe and necessary. The multiplier is configuration rather than input, the expiry is
    /// a <see cref="TimeSpan"/> the calling service computed rather than text a caller chose, and
    /// together they are the whole explanation of why the refusal happened. Withholding them
    /// would leave a message that reports a failure without its cause.
    /// </remarks>
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
    /// Drops a registration whose entry is no longer live, leaving a live entry's registration
    /// alone. Takes <see cref="_registryGate"/> itself, so callers must NOT already hold it.
    /// </summary>
    /// <param name="key">The key whose registration may be stale.</param>
    /// <remarks>
    /// Reached from two places: the eviction callback every written entry carries, which is what
    /// makes an entry withdraw its own registration when it goes away unobserved, and the read
    /// path, which reconciles anything the callback has not yet been dispatched for. Both are
    /// idempotent, so arriving twice for one key is harmless.
    /// </remarks>
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
                Forget(key);
            }
        }
    }

    /// <summary>
    /// Evicts one entry and its registration together, and records that an invalidation happened.
    /// Callers must already hold <see cref="_registryGate"/>.
    /// </summary>
    /// <param name="key">The key to evict.</param>
    /// <remarks>
    /// Advancing the invalidation count is part of evicting, not an extra step a caller may choose
    /// to take, which is why it lives here rather than in the members that call this one. Any new
    /// invalidation member therefore becomes visible to in-flight creations simply by routing its
    /// removals through here. The count moves even when the key held no entry: an eviction for a
    /// key that is absent still expresses the intent that whatever is under that key must go, and a
    /// creation that is at this moment about to publish under it has to honour that.
    /// </remarks>
    private void Evict(string key)
    {
        // Every explicit eviction reaches this method - the eight named invalidations, the single
        // removal and the category sweep alike - so this is the one place the generation needs to
        // move. A creation already in progress compares the value it recorded against this one and
        // declines to publish if they differ.
        _invalidationGeneration++;

        _memoryCache.Remove(key);
        Forget(key);
    }

    /// <summary>
    /// Withdraws a key from both registries together. Callers must already hold
    /// <see cref="_registryGate"/>.
    /// </summary>
    /// <param name="key">The key to withdraw.</param>
    /// <remarks>
    /// Every path that drops a registration goes through here, so the tracked-key registry and the
    /// category index can never disagree about which keys are live. That matters most on the
    /// natural-expiry path: dropping a key from one registry but not the other would leave the
    /// category index accumulating names whose entries had long since gone, which is the growth
    /// this index was introduced to remove. A category left holding nothing is dropped rather than
    /// retained as an empty set, so the index cannot accumulate one entry per category ever used.
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
    /// Evicts every tracked key in one category. Callers must already hold
    /// <see cref="_registryGate"/>.
    /// </summary>
    /// <param name="keyPrefix">The category prefix, matched ordinally.</param>
    /// <remarks>
    /// Reads the category index rather than walking the whole registry, so the work is
    /// proportional to the category being invalidated. This matters because one portal
    /// invalidation evicts three categories: without the index each of those three would walk every
    /// key this service had ever written, including keys belonging to unrelated categories and keys
    /// whose entries had already expired.
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
    /// Performs the one shared creation for a key: re-checks the cache, invokes the caller's
    /// factory, stores the result and releases the in-flight registration.
    /// </summary>
    /// <typeparam name="T">The caller's value type.</typeparam>
    /// <param name="key">The cache entry key.</param>
    /// <param name="factory">The caller's cancellation-aware producer.</param>
    /// <param name="expiration">How long a newly produced entry stays live.</param>
    /// <param name="registration">The key-and-shape slot this creation occupies.</param>
    /// <param name="creation">
    /// The creation itself, which owns the budget the factory observes and the abandonment flag the
    /// publication below consults. Passing it in is what lets both the publication and the release be
    /// fenced on this creation's own identity rather than on the slot it happens to occupy.
    /// </param>
    /// <returns>The produced or concurrently published value, boxed for the shared task.</returns>
    /// <remarks>
    /// Accepts no caller token by design, so that no caller's lifetime can be attached to work
    /// that other callers are waiting on. Per-caller cancellation is applied by the awaiting
    /// member instead, and this creation is bounded by its own budget rather than by anyone's
    /// token.
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

            // Second check, now that this creation has been elected. A value published between
            // the caller's read and this point must not provoke another round of caller I/O. The
            // generation is recorded in the same critical section as that check, so the value
            // recorded is exactly the one in force at the instant this creation began: read it
            // outside the gate and an invalidation could slip between the two and go unnoticed.
            lock (_registryGate)
            {
                if (_memoryCache.TryGetValue(key, out object? published))
                {
                    return published;
                }

                observedGeneration = _invalidationGeneration;
            }

            // Not a caller's token: this one creation serves every coalesced caller, so it must
            // outlive any individual caller that withdraws. It is bounded all the same - a
            // creation bound to nothing at all is what let a single stalled factory hold a key
            // permanently. A factory that observes the token it is handed is cancelled here; one
            // that ignores it is abandoned by the awaiting member instead.
            //
            // The budget is owned by the creation rather than by this scope, which is what lets the
            // retirement path cancel it. A source declared here with `using` could only be cancelled
            // by the timer inside it, so a waiter that gave up had no way to stop the work it had
            // given up on. Disposal moves with ownership: Complete in the finally below releases it,
            // under the same monitor the cancellation takes, so a cancel can never race a dispose.
            T produced = await factory(creation.BeginWork(SharedCreationBudget)).ConfigureAwait(false);

            // MIGRATION: net-new, and the reason this publication is conditional. The legacy idiom
            // read, loaded and inserted as three separate statements at each of the measured call
            // sites, with nothing relating the insert to any clear that happened in between, so a
            // clear issued while a load was in flight was simply overwritten by that load a moment
            // later. The value being published here was produced BEFORE any invalidation that has
            // since occurred, so publishing it would reinstate exactly the state the invalidation
            // was issued to remove. The comparison and the write share one critical section, which
            // is what makes the pair atomic with respect to eviction.
            //
            // TWO fences guard this write, and neither subsumes the other. The generation catches an
            // INVALIDATION that intervened, because every eviction moves it. Abandonment catches a
            // RETIREMENT that intervened, which no eviction accompanies and which therefore leaves the
            // generation exactly where it was: once this creation has been retired, a replacement may
            // already have produced and published a newer value under the same key, and writing over
            // it here would make the cache report an answer older than one it had already served. Both
            // are read inside the same critical section as the write so that neither can change between
            // the test and the store. Reading the abandonment flag takes the creation's own monitor
            // while this one is held; nothing anywhere takes them in the opposite order, so the nesting
            // cannot deadlock.
            lock (_registryGate)
            {
                if (_invalidationGeneration == observedGeneration && !creation.IsAbandoned)
                {
                    Publish(key, produced, expiration);
                }
            }

            // Delivered to every waiting caller either way. A suppressed publication costs a
            // cache entry, never a result: the value was produced correctly and is at least as
            // fresh as anything the cache could have offered.
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
            //
            // The budget is released first, so the source this creation owns does not outlive the work
            // it bounded. Then the registration is withdrawn by COMPARING IDENTITY, not by the slot
            // alone. The slot - the key and the requested shape - names a position that is reused: a
            // creation this one has already been retired in favour of occupies exactly the same pair,
            // so removing by the pair would delete a SUCCESSOR that is still running and hand the next
            // arriving caller a second concurrent factory for the same key. Comparing means a retired
            // creation withdraws nothing, because the slot no longer holds it.
            creation.Complete();

            _inFlightLoads.TryRemove(
                new KeyValuePair<(string Key, Type ValueType), SharedCreation>(registration, creation));
        }
    }

    /// <summary>
    /// One coalesced creation of one cache entry: the shared work, the budget that bounds it, and
    /// whether it has been retired in favour of a later attempt.
    /// </summary>
    /// <remarks>
    /// <para>
    /// MIGRATION: net-new. It exists because a key and a requested shape name a SLOT rather than an
    /// attempt, and three of this service's guarantees need to distinguish the two. Only an identity
    /// can tell a finishing creation from the successor that replaced it, so only an identity can make
    /// a removal safe; only a per-attempt flag can tell a retired creation that its result is no
    /// longer wanted, because a retirement moves no invalidation generation; and only a per-attempt
    /// cancellation source lets a waiter that has given up stop the work it gave up on, instead of
    /// leaving it running and unobserved.
    /// </para>
    /// <para>
    /// Every member is safe to call from several threads at once. The monitor is private to the
    /// instance and is never held across an <see langword="await"/>, and nothing that holds it reaches
    /// back into the owning service, so it can be taken while the service's own registry monitor is
    /// held without introducing a cycle.
    /// </para>
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
        /// <remarks>
        /// Lazy rather than a started task, so that an instance a concurrent registration discards
        /// never performs any work at all. Accessing the value is what elects the single creation,
        /// however many callers arrive at once.
        /// </remarks>
        internal Lazy<Task<object?>> Work { get; set; } = null!;

        /// <summary>
        /// Whether this attempt has been retired, in which case its result must not be published.
        /// </summary>
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

        /// <summary>
        /// Opens the budget bounding this attempt and returns the token the factory must observe.
        /// </summary>
        /// <param name="budget">How long the work may run before its token is cancelled.</param>
        /// <returns>
        /// The token bounding the work. Already cancelled when the attempt was retired before it
        /// started, so a factory that observes its token declines to do work nobody is waiting for.
        /// </returns>
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

        /// <summary>
        /// Retires this attempt: its result will not be published, and its work is asked to stop.
        /// </summary>
        /// <remarks>
        /// Idempotent, and safe to call before the work starts - the flag is set either way, and
        /// <see cref="BeginWork"/> then hands the factory an already-cancelled token.
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
