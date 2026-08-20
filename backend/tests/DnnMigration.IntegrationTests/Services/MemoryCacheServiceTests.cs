using System.Collections.Concurrent;
using System.Globalization;
using System.Reflection;
using DnnMigration.Application.Options;
using DnnMigration.Domain.Abstractions.Services;
using DnnMigration.Infrastructure.Services;
using FluentAssertions;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace DnnMigration.IntegrationTests.Services;

/// <summary>Drives the real cache adapter, rather than a mock of the contract it implements.</summary>
/// <remarks>
/// <para>
/// <strong>What the existing coverage actually protected.</strong> Every application service that caches is
/// tested against a mock of <see cref="ICacheService"/>, and those tests assert that the service ASKED for
/// something - that it read before loading, that it wrote with the expiry it computed, that it invalidated
/// after a write. Not one of them asserts what happens when the answer arrives.
/// </para>
/// <para>
/// <strong>Two facts reach private state by reflection, and say so.</strong> The tracked-key registry and
/// the category index are private bookkeeping with no public projection, and the guarantees about them - an
/// entry that expires unobserved withdraws itself, an emptied category is dropped rather than retained, the
/// portal dictionary is filed under its own category rather than the portal one - are guarantees about
/// growth and classification that have no other observable consequence today.
/// </para>
/// </remarks>
[Trait("Category", "Integration")]
[Collection(IntegrationTestCollection.Name)]
public sealed class MemoryCacheServiceTests
{
    /// <summary>Legacy key template for one portal, from <c>DataCache.vb:L47</c>.</summary>
    private const string PortalKeyTemplate = "Portal{0}";

    /// <summary>Legacy key for the tab-to-portal dictionary, from <c>DataCache.vb:L44</c>.</summary>
    private const string PortalDictionaryKey = "PortalDictionary";

    /// <summary>Legacy key template for a portal's tabs, from <c>DataCache.vb:L50</c>.</summary>
    private const string TabsKeyTemplate = "Tabs{0}";

    /// <summary>Legacy key for the derived tab-path lookup, from <c>DataCache.vb:L52</c>.</summary>
    private const string TabPathKey = "TabPathDictionary";

    /// <summary>Legacy key template for a portal's tab permissions, from <c>DataCache.vb:L54</c>.</summary>
    private const string TabPermissionsKeyTemplate = "TabPermissions{0}";

    /// <summary>Legacy key template for a tab's modules, from <c>DataCache.vb:L57</c>.</summary>
    private const string TabModulesKeyTemplate = "TabModules{0}";

    /// <summary>Legacy key template for a tab's module permissions, from <c>DataCache.vb:L60</c>.</summary>
    private const string ModulePermissionsKeyTemplate = "ModulePermissions{0}";

    /// <summary>Legacy key template for a portal's module dictionary, from <c>DataCache.vb:L63</c>.</summary>
    private const string ModulesKeyTemplate = "Modules{0}";

    /// <summary>Legacy key template for a portal's profile definitions, from <c>DataCache.vb:L75</c>.</summary>
    private const string ProfileDefinitionsKeyTemplate = "ProfileDefinitions{0}";

    /// <summary>Legacy key template for one account, from <c>DataCache.vb:L78</c>.</summary>
    private const string UserKeyTemplate = "UserInfo|{0}|{1}";

    /// <summary>Legacy prefix under which per-module settings are cached.</summary>
    private const string ModuleSettingsKeyPrefix = "GetModuleSettings";

    /// <summary>The default configured performance multiplier, which is what production runs with.</summary>
    private const int DefaultMultiplier = 3;

    /// <summary>The multiplier that switches caching off entirely.</summary>
    private const int NoCaching = 0;

    /// <summary>A generous expiry, so nothing below can expire while a fact is running.</summary>
    private static readonly TimeSpan LongExpiry = TimeSpan.FromMinutes(20);

    /// <summary>The budget the adapter allows one shared creation, which is not configurable.</summary>
    private static readonly TimeSpan SharedCreationBudget = TimeSpan.FromSeconds(30);

    private readonly ApiTestFixture _fixture;

    /// <summary>Initialises a new instance of the <see cref="MemoryCacheServiceTests"/> class.</summary>
    /// <param name="fixture">The shared host, used only for the registration facts.</param>
    public MemoryCacheServiceTests(ApiTestFixture fixture) => _fixture = fixture;

    /// <summary>The composed application resolves this adapter, once.</summary>
    [Fact]
    public void TheComposedApplication_ResolvesOneMemoryCacheService()
    {
        ICacheService fromRoot = _fixture.Services.GetRequiredService<ICacheService>();

        fromRoot.Should().BeOfType<MemoryCacheService>();

        using IServiceScope scope = _fixture.Services.CreateScope();

        scope.ServiceProvider.GetRequiredService<ICacheService>().Should().BeSameAs(
            fromRoot,
            "one store serves the process: a scoped adapter would give each request a private registry and "
            + "an invalidation issued by one request would never reach another's entries");
    }

    /// <summary>The registered store is bounded, and the adapter is what makes writing to it legal.</summary>
    [Fact]
    public void TheRegisteredStore_IsBoundedAndTheAdapterDeclaresASizeForEveryEntry()
    {
        IMemoryCache store = _fixture.Services.GetRequiredService<IMemoryCache>();
        ICacheService cache = _fixture.Services.GetRequiredService<ICacheService>();

        string key = UniqueKey();

        Action writeWithoutASize = () => store.Set(key, "value");

        writeWithoutASize.Should().Throw<InvalidOperationException>(
            "the registered store carries a size limit, which is what turns a cache-cardinality defect into "
            + "ordinary cache pressure instead of unbounded memory growth");

        try
        {
            cache.Set(key, "value", LongExpiry);

            cache.Get<string>(key).Should().Be(
                "value",
                "the adapter declares a size on every entry, which is the only reason a write through it "
                + "succeeds against a bounded store");
        }
        finally
        {
            cache.Remove(key);
        }
    }

    /// <summary>A written value is read back as itself.</summary>
    [Fact]
    public void Set_ThenGet_RoundTripsTheValue()
    {
        using CacheHarness harness = NewHarness();

        string key = FormatKey(PortalKeyTemplate, -1);
        string[] value = ["alpha", "beta"];

        harness.Cache.Set(key, value, LongExpiry);

        harness.Cache.Get<string[]>(key).Should().BeSameAs(
            value,
            "the adapter caches the object it was handed rather than a copy of it");
    }

    /// <summary>A miss is reported as the caller's own default, never as a sentinel.</summary>
    /// <remarks>
    /// The legacy null helpers mapped an absent integer to minus one and an absent string to the empty
    /// string, and both are legitimate stored values in the existing schema, so neither may stand in for a
    /// miss.
    /// </remarks>
    [Fact]
    public void Get_ReportsAMissAsTheCallersOwnDefault()
    {
        using CacheHarness harness = NewHarness();

        harness.Cache.Get<string>(UniqueKey()).Should().BeNull();
        harness.Cache.Get<int>(UniqueKey()).Should().Be(0);
        harness.Cache.Get<int?>(UniqueKey()).Should().BeNull(
            "minus one is a real portal identity in this schema, so it cannot mean 'absent'");
    }

    /// <summary>A deliberately stored null round-trips rather than being turned into a miss.</summary>
    [Fact]
    public void Set_StoresANullValueAndGetRoundTripsIt()
    {
        using CacheHarness harness = NewHarness();

        string key = FormatKey(PortalKeyTemplate, 7);

        harness.Cache.Set<string?>(key, null, LongExpiry);

        harness.Store.TryGetValue(key, out object? raw).Should().BeTrue("an entry was written");
        raw.Should().BeNull();
        harness.Cache.Get<string?>(key).Should().BeNull();
    }

    /// <summary>An entry of another shape is refused, and the refusal quotes no key.</summary>
    [Fact]
    public void Get_RefusesAnEntryOfAnotherShapeWithoutQuotingTheKey()
    {
        using CacheHarness harness = NewHarness();

        const string account = "ada.lovelace@example.test";
        string key = FormatUserKey(-1, account);

        harness.Cache.Set(key, "a string", LongExpiry);

        Action read = () => harness.Cache.Get<int[]>(key);

        string message = read.Should().Throw<InvalidOperationException>().Which.Message;

        message.Should().Contain(typeof(string).FullName!);
        message.Should().Contain(typeof(int[]).FullName!, "the refusal names both shapes, so the defect is diagnosable");
        message.Should().Contain("category UserInfo", "the family is what a diagnostician needs");
        message.Should().Contain("fingerprint", "and a stable per-process fingerprint to correlate two entries");
        message.Should().NotContain(account, "a composed key carries a user name and must never be quoted");
        message.Should().NotContain(key);
    }

    /// <summary>Every member refuses a key that names nothing.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("\t")]
    public void EverySynchronousMember_RefusesABlankKey(string? key)
    {
        using CacheHarness harness = NewHarness();

        Action read = () => harness.Cache.Get<string>(key!);
        Action write = () => harness.Cache.Set(key!, "value", LongExpiry);
        Action remove = () => harness.Cache.Remove(key!);

        read.Should().Throw<ArgumentException>();
        write.Should().Throw<ArgumentException>();
        remove.Should().Throw<ArgumentException>();
    }

    /// <summary>The creating member refuses a blank key and a null factory.</summary>
    [Fact]
    public async Task GetOrCreateAsync_RefusesABlankKeyAndANullFactory()
    {
        using CacheHarness harness = NewHarness();

        Func<Task> blankKey = () => harness.Cache.GetOrCreateAsync(
            " ",
            _ => Task.FromResult("value"),
            LongExpiry);

        Func<Task> nullFactory = () => harness.Cache.GetOrCreateAsync<string>(
            UniqueKey(),
            null!,
            LongExpiry);

        await blankKey.Should().ThrowAsync<ArgumentException>();
        await nullFactory.Should().ThrowAsync<ArgumentNullException>();
    }

    /// <summary>With caching switched off, a write does nothing at all.</summary>
    [Fact]
    public void Set_WritesNothingWhenCachingIsSwitchedOff()
    {
        using CacheHarness harness = NewHarness(NoCaching);

        string key = FormatKey(TabsKeyTemplate, -1);

        harness.Cache.Set(key, "value", LongExpiry);

        harness.Store.TryGetValue(key, out _).Should().BeFalse();
        TrackedKeysOf(harness.Cache).Should().NotContainKey(
            key,
            "a write that stored nothing must not register the key either, or the registry would grow "
            + "without the store growing with it");
    }

    /// <summary>
    /// A non-positive expiry writes nothing, where the legacy code wrote an entry that never expired.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(-30000)]
    public void Set_WritesNothingWhenTheExpiryIsNotPositive(int milliseconds)
    {
        using CacheHarness harness = NewHarness();

        string key = FormatKey(TabsKeyTemplate, 3);

        harness.Cache.Set(key, "value", TimeSpan.FromMilliseconds(milliseconds));

        harness.Store.TryGetValue(key, out _).Should().BeFalse(
            "an entry with no expiry at all is an accidental persistence tier, which this migration "
            + "deliberately does not carry forward");
    }

    /// <summary>The infinite span is not accepted as a hidden never-expire instruction.</summary>
    [Fact]
    public void Set_WritesNothingForTheInfiniteSpan()
    {
        using CacheHarness harness = NewHarness();

        string key = FormatKey(TabsKeyTemplate, 4);

        harness.Cache.Set(key, "value", Timeout.InfiniteTimeSpan);

        harness.Store.TryGetValue(key, out _).Should().BeFalse(
            "the infinite span is negative and therefore non-positive here, which is deliberate");
    }

    /// <summary>A miss with caching switched off is refused rather than loaded.</summary>
    /// <remarks>
    /// "Caching off" means "do not do the work", not merely "do not store the result" - the legacy site
    /// that establishes this skips an entire expensive query when its computed timeout is not positive. The
    /// adapter cannot centralise that decision, so it refuses instead of guessing, and the refusal must not
    /// run the factory: running it would perform exactly the load the legacy path would have skipped.
    /// </remarks>
    [Fact]
    public async Task GetOrCreateAsync_RefusesAMissWhenCachingIsSwitchedOffWithoutRunningTheFactory()
    {
        using CacheHarness harness = NewHarness(NoCaching);

        const string account = "grace.hopper@example.test";
        string key = FormatUserKey(0, account);
        int invocations = 0;

        Func<Task> create = () => harness.Cache.GetOrCreateAsync(
            key,
            _ =>
            {
                invocations++;

                return Task.FromResult("value");
            },
            LongExpiry);

        string message = (await create.Should().ThrowAsync<InvalidOperationException>()).Which.Message;

        invocations.Should().Be(
            0,
            "the caller's own site owns the no-caching decision, and running the factory here would perform "
            + "the load that decision exists to skip");

        message.Should().Contain("category UserInfo");
        message.Should().Contain("fingerprint");
        message.Should().NotContain(account, "no message this adapter raises may quote a composed key");
        message.Should().Contain(
            NoCaching.ToString(CultureInfo.InvariantCulture),
            "the configured multiplier and the requested expiry are the whole explanation of the refusal");
    }

    /// <summary>A miss with a non-positive expiry is refused for the same reason.</summary>
    [Fact]
    public async Task GetOrCreateAsync_RefusesAMissWhenTheExpiryIsNotPositive()
    {
        using CacheHarness harness = NewHarness();

        int invocations = 0;

        Func<Task> create = () => harness.Cache.GetOrCreateAsync(
            FormatKey(ModulesKeyTemplate, -1),
            _ =>
            {
                invocations++;

                return Task.FromResult("value");
            },
            TimeSpan.Zero);

        await create.Should().ThrowAsync<InvalidOperationException>();

        invocations.Should().Be(0);
    }

    /// <summary>A live entry is still served after caching has been switched off.</summary>
    /// <remarks>
    /// The read happens BEFORE the multiplier is consulted, which is the legacy order: every measured call
    /// site fetched from the cache first and computed its timeout only after a miss. An adapter that
    /// checked the multiplier first would start refusing reads of entries it had already written, which is
    /// a refusal the caller has no way to satisfy.
    /// </remarks>
    [Fact]
    public async Task GetOrCreateAsync_StillServesALiveEntryWhenCachingIsSwitchedOff()
    {
        using CacheHarness harness = NewHarness(NoCaching);

        string key = FormatKey(ModulesKeyTemplate, 5);

        // Written straight into the store, because the adapter under test would decline to write it. This is
        // the state an installation is in the moment its multiplier is set to zero: entries already live.
        harness.Store.Set(
            key,
            "already live",
            new MemoryCacheEntryOptions { SlidingExpiration = LongExpiry, Size = 1 });

        string served = await harness.Cache.GetOrCreateAsync(
            key,
            _ => Task.FromResult("freshly loaded"),
            LongExpiry);

        served.Should().Be(
            "already live",
            "the cache is read before the multiplier is consulted, so an entry that exists is served");
    }

    /// <summary>A miss loads once and the value is then served from the cache.</summary>
    [Fact]
    public async Task GetOrCreateAsync_LoadsOnceAndThenServesFromTheCache()
    {
        using CacheHarness harness = NewHarness();

        string key = FormatKey(TabModulesKeyTemplate, 11);
        int invocations = 0;

        Task<string> Load(CancellationToken token)
        {
            invocations++;

            return Task.FromResult("loaded");
        }

        (await harness.Cache.GetOrCreateAsync(key, Load, LongExpiry)).Should().Be("loaded");
        (await harness.Cache.GetOrCreateAsync(key, Load, LongExpiry)).Should().Be("loaded");

        invocations.Should().Be(1, "the second call is a hit, which is the entire point of the member");
        harness.Cache.Get<string>(key).Should().Be("loaded");
    }

    /// <summary>A failed load does not poison the key.</summary>
    /// <remarks>
    /// The in-flight registration is released whether the creation completes, faults or is cancelled. Left
    /// in place after a failure it would be joined by every later caller, so one transient database error
    /// would become a permanent refusal to serve that key for the life of the process.
    /// </remarks>
    [Fact]
    public async Task GetOrCreateAsync_DoesNotPoisonAKeyWhenTheFactoryFails()
    {
        using CacheHarness harness = NewHarness();

        string key = FormatKey(TabModulesKeyTemplate, 12);

        Func<Task> failing = () => harness.Cache.GetOrCreateAsync<string>(
            key,
            _ => throw new InvalidOperationException("the store is unavailable"),
            LongExpiry);

        await failing.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("the store is unavailable");

        string recovered = await harness.Cache.GetOrCreateAsync(
            key,
            _ => Task.FromResult("recovered"),
            LongExpiry);

        recovered.Should().Be(
            "recovered",
            "one failed load must not leave the key permanently joined to a creation that failed");
    }

    /// <summary>A caller who has already withdrawn is refused before anything happens.</summary>
    [Fact]
    public async Task GetOrCreateAsync_RefusesAnAlreadyCancelledCallerWithoutRunningTheFactory()
    {
        using CacheHarness harness = NewHarness();
        using CancellationTokenSource cancelled = new();
        await cancelled.CancelAsync();

        int invocations = 0;

        Func<Task> create = () => harness.Cache.GetOrCreateAsync(
            FormatKey(TabModulesKeyTemplate, 13),
            _ =>
            {
                invocations++;

                return Task.FromResult("value");
            },
            LongExpiry,
            cancelled.Token);

        await create.Should().ThrowAsync<OperationCanceledException>();

        invocations.Should().Be(0);
    }

    /// <summary>Concurrent callers for one key and one shape share a single load.</summary>
    /// <remarks>
    /// The legacy idiom was a read, then a load, then an insert at every call site, so two simultaneous
    /// requests each performed the load. Coalescing is the whole reason this member exists, and it is only
    /// observable under genuine concurrency: the factory is held open until every caller has arrived, so a
    /// second load would have to happen for the count below to be wrong.
    /// </remarks>
    [Fact]
    public async Task GetOrCreateAsync_CoalescesConcurrentCallersForOneKeyAndShape()
    {
        using CacheHarness harness = NewHarness();

        string key = FormatKey(ModulePermissionsKeyTemplate, 21);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int invocations = 0;

        async Task<string> Load(CancellationToken token)
        {
            Interlocked.Increment(ref invocations);

            await release.Task;

            return "loaded once";
        }

        Task<string> first = harness.Cache.GetOrCreateAsync(key, Load, LongExpiry);
        Task<string> second = harness.Cache.GetOrCreateAsync(key, Load, LongExpiry);
        Task<string> third = harness.Cache.GetOrCreateAsync(key, Load, LongExpiry);

        release.SetResult();

        string[] answers = await Task.WhenAll(first, second, third);

        answers.Should().AllBe("loaded once");
        invocations.Should().Be(
            1,
            "three concurrent misses for one key and one shape are three callers asking for the same thing");
    }

    /// <summary>Callers asking for different shapes are never joined.</summary>
    /// <remarks>
    /// Two callers asking for the same key as two different shapes are not asking for the same thing:
    /// joining them would hand one of them a value it cannot hold, and would do so intermittently,
    /// depending only on which arrived first. Both factories are held open until both have been entered, so
    /// the assertion is about the registry rather than about timing.
    /// </remarks>
    [Fact]
    public async Task GetOrCreateAsync_DoesNotJoinCallersAskingForDifferentShapes()
    {
        using CacheHarness harness = NewHarness();

        string key = FormatKey(ModulePermissionsKeyTemplate, 22);
        TaskCompletionSource textEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource numberEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        Task<string> text = harness.Cache.GetOrCreateAsync(
            key,
            async _ =>
            {
                textEntered.TrySetResult();
                await release.Task;

                return "text";
            },
            LongExpiry);

        Task<int[]> numbers = harness.Cache.GetOrCreateAsync(
            key,
            async _ =>
            {
                numberEntered.TrySetResult();
                await release.Task;

                return new[] { 1, 2, 3 };
            },
            LongExpiry);

        await Task.WhenAll(
            textEntered.Task.WaitAsync(TimeSpan.FromSeconds(10)),
            numberEntered.Task.WaitAsync(TimeSpan.FromSeconds(10)));

        release.SetResult();

        (await text).Should().Be("text");
        (await numbers).Should().Equal(1, 2, 3);
    }

    /// <summary>One caller's withdrawal never cancels the shared creation.</summary>
    /// <remarks>
    /// Because a single creation serves every coalesced caller, binding it to whoever arrived first would
    /// let that caller's withdrawal surface as a cancellation to unrelated callers whose own tokens are
    /// perfectly healthy: two concurrent requests miss the same key, the first client disconnects, and the
    /// second fails through no fault of its own.
    /// </remarks>
    [Fact]
    public async Task GetOrCreateAsync_IsolatesOneCallersWithdrawalFromTheSharedCreation()
    {
        using CacheHarness harness = NewHarness();
        using CancellationTokenSource withdrawing = new();

        string key = FormatKey(ModulePermissionsKeyTemplate, 23);
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int invocations = 0;
        bool factoryTokenCancelled = true;

        Task<string> withdrawn = harness.Cache.GetOrCreateAsync(
            key,
            async token =>
            {
                Interlocked.Increment(ref invocations);
                entered.TrySetResult();

                await release.Task;

                factoryTokenCancelled = token.IsCancellationRequested;

                return "survived";
            },
            LongExpiry,
            withdrawing.Token);

        Task<string> healthy = harness.Cache.GetOrCreateAsync(
            key,
            _ => Task.FromResult("never reached"),
            LongExpiry);

        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));

        await withdrawing.CancelAsync();

        Func<Task> awaitWithdrawn = () => withdrawn;
        await awaitWithdrawn.Should().ThrowAsync<OperationCanceledException>(
            "each caller awaits the shared creation through its own token, so it observes its own withdrawal");

        release.SetResult();

        (await healthy).Should().Be(
            "survived",
            "a healthy caller must not be failed by another caller's disconnection");

        invocations.Should().Be(1);
        factoryTokenCancelled.Should().BeFalse(
            "the factory is handed a token of the creation's own, never a caller's");
        harness.Cache.Get<string>(key).Should().Be("survived", "and the value is cached for whoever asks next");
    }

    /// <summary>A value produced before an invalidation is delivered but not published.</summary>
    [Fact]
    public async Task GetOrCreateAsync_DeliversButDoesNotPublishAValueProducedBeforeAnInvalidation()
    {
        using CacheHarness harness = NewHarness();

        int portalId = 31;
        string key = FormatKey(TabsKeyTemplate, portalId);
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        Task<string> loading = harness.Cache.GetOrCreateAsync(
            key,
            async _ =>
            {
                entered.TrySetResult();
                await release.Task;

                return "produced before the invalidation";
            },
            LongExpiry);

        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));

        harness.Cache.InvalidateTabs(portalId);

        release.SetResult();

        (await loading).Should().Be(
            "produced before the invalidation",
            "a suppressed publication costs a cache entry, never a result");

        harness.Store.TryGetValue(key, out _).Should().BeFalse(
            "publishing it would reinstate exactly the state the invalidation was issued to remove");
    }

    /// <summary>An eviction fences an in-flight creation even when the evicted key held nothing.</summary>
    [Fact]
    public async Task AnEvictionOfAnAbsentKey_StillFencesAnInFlightCreation()
    {
        using CacheHarness harness = NewHarness();

        string key = FormatKey(TabsKeyTemplate, 32);
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        Task<string> loading = harness.Cache.GetOrCreateAsync(
            key,
            async _ =>
            {
                entered.TrySetResult();
                await release.Task;

                return "produced";
            },
            LongExpiry);

        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));

        // A key nothing was ever written under, and a different key from the one being created.
        harness.Cache.Remove(UniqueKey());

        release.SetResult();

        (await loading).Should().Be("produced");
        harness.Store.TryGetValue(key, out _).Should().BeFalse(
            "an eviction expresses an intent even when it removed nothing, and a creation that began before "
            + "it must not publish over that intent");
    }

    /// <summary>A portal invalidation evicts the portal's own keys and the categories it cannot attribute.</summary>
    [Fact]
    public void InvalidatePortal_EvictsThePortalsKeysAndTheCategoriesItCannotAttribute()
    {
        using CacheHarness harness = NewHarness();

        const int portalId = -1;
        const int otherPortalId = 4;

        string[] evicted =
        [
            FormatKey(PortalKeyTemplate, portalId),
            FormatKey(ProfileDefinitionsKeyTemplate, portalId),
            FormatKey(TabsKeyTemplate, portalId),
            FormatKey(TabPermissionsKeyTemplate, portalId),
            FormatKey(ModulesKeyTemplate, portalId),
            TabPathKey,
            FormatKey(TabModulesKeyTemplate, 900),
            FormatKey(ModulePermissionsKeyTemplate, 901),
            ModuleSettingsKeyPrefix + "902",
        ];

        string[] survivors =
        [
            FormatKey(PortalKeyTemplate, otherPortalId),
            FormatKey(TabsKeyTemplate, otherPortalId),
            FormatUserKey(portalId, "someone@example.test"),
            PortalDictionaryKey,
        ];

        foreach (string key in evicted.Concat(survivors))
        {
            harness.Cache.Set(key, key, LongExpiry);
        }

        harness.Cache.InvalidatePortal(portalId);

        foreach (string key in evicted)
        {
            harness.Store.TryGetValue(key, out _).Should().BeFalse(
                FormattableString.Invariant($"{key} belongs to the invalidated scope"));
        }

        foreach (string key in survivors)
        {
            harness.Store.TryGetValue(key, out _).Should().BeTrue(
                FormattableString.Invariant($"{key} belongs to another scope and must be left alone"));
        }
    }

    /// <summary>The portal dictionary is not swept as collateral by a portal invalidation.</summary>
    /// <remarks>
    /// Its literal key begins with the portal category's prefix, so a shortest-first classification would
    /// file it under the portal category and carry it off the day anyone sweeps that category. It is
    /// registered as a category of its own and the vocabulary is sorted longest-first, which makes the
    /// misfiling impossible rather than merely unlikely.
    /// </remarks>
    [Fact]
    public void TheCategoryVocabulary_FilesThePortalDictionaryUnderItsOwnCategory()
    {
        using CacheHarness harness = NewHarness();

        harness.Cache.Set(PortalDictionaryKey, "dictionary", LongExpiry);
        harness.Cache.Set(FormatKey(PortalKeyTemplate, 6), "portal", LongExpiry);

        Dictionary<string, HashSet<string>> index = CategoryIndexOf(harness.Cache);

        index.Should().ContainKey(PortalDictionaryKey);
        index[PortalDictionaryKey].Should().Equal(PortalDictionaryKey);

        index.Should().ContainKey("Portal");
        index["Portal"].Should().BeEquivalentTo(
            new[] { FormatKey(PortalKeyTemplate, 6) },
            "the more specific category claims the dictionary, so a future sweep of the portal category "
            + "cannot take it as collateral");
    }

    /// <summary>Minus one and zero are real identities, not wildcards.</summary>
    /// <remarks>
    /// The legacy host clear passed minus one as a portal identifier to mean "every portal". The existing
    /// schema seeds <c>Portals.PortalID</c> at <c>IDENTITY(-1,1)</c> and <c>Roles.RoleID</c> at
    /// <c>IDENTITY(0,1)</c>, so both values name real owners here and an invalidation for either must reach
    /// exactly one of them.
    /// </remarks>
    [Fact]
    public void InvalidatePortal_TreatsMinusOneAsOnePortalRatherThanAsAWildcard()
    {
        using CacheHarness harness = NewHarness();

        foreach (int portalId in new[] { -1, 0, 1 })
        {
            harness.Cache.Set(FormatKey(PortalKeyTemplate, portalId), portalId, LongExpiry);
        }

        harness.Cache.InvalidatePortal(-1);

        harness.Store.TryGetValue(FormatKey(PortalKeyTemplate, -1), out _).Should().BeFalse();
        harness.Store.TryGetValue(FormatKey(PortalKeyTemplate, 0), out _).Should().BeTrue(
            "zero is the first real portal identity after the minus-one seed, not a placeholder");
        harness.Store.TryGetValue(FormatKey(PortalKeyTemplate, 1), out _).Should().BeTrue();
    }

    /// <summary>A host invalidation reaches everything the adapter wrote, and empties both registries.</summary>
    /// <remarks>
    /// This is the only member that means "everything", and it is the only one that takes no argument -
    /// precisely because minus one no longer means "every portal". A key belonging to no declared category
    /// is included, since the tracked-key registry is what makes the sweep complete rather than the
    /// category index.
    /// </remarks>
    [Fact]
    public void InvalidateHost_EvictsEverythingTheAdapterWroteAndLeavesTheRegistriesEmpty()
    {
        using CacheHarness harness = NewHarness();

        string uncategorised = UniqueKey();

        string[] keys =
        [
            FormatKey(PortalKeyTemplate, -1),
            FormatKey(TabsKeyTemplate, -1),
            FormatUserKey(-1, "someone@example.test"),
            PortalDictionaryKey,
            TabPathKey,
            ModuleSettingsKeyPrefix + "7",
            uncategorised,
        ];

        foreach (string key in keys)
        {
            harness.Cache.Set(key, key, LongExpiry);
        }

        harness.Cache.InvalidateHost();

        foreach (string key in keys)
        {
            harness.Store.TryGetValue(key, out _).Should().BeFalse(
                FormattableString.Invariant($"{key} was written through the adapter, so the host sweep reaches it"));
        }

        TrackedKeysOf(harness.Cache).Should().BeEmpty();
        CategoryIndexOf(harness.Cache).Should().BeEmpty(
            "the registries are left genuinely empty rather than merely mostly empty");
    }

    /// <summary>The tab invalidation reproduces the legacy clear exactly.</summary>
    [Fact]
    public void InvalidateTabs_EvictsTheThreeLegacyKeysAndNothingElse()
    {
        using CacheHarness harness = NewHarness();

        const int portalId = 41;

        string tabs = FormatKey(TabsKeyTemplate, portalId);
        string permissions = FormatKey(TabPermissionsKeyTemplate, portalId);
        string modules = FormatKey(ModulesKeyTemplate, portalId);

        foreach (string key in new[] { tabs, TabPathKey, permissions, modules })
        {
            harness.Cache.Set(key, key, LongExpiry);
        }

        harness.Cache.InvalidateTabs(portalId);

        harness.Store.TryGetValue(tabs, out _).Should().BeFalse();
        harness.Store.TryGetValue(TabPathKey, out _).Should().BeFalse("it is derived from every tab");
        harness.Store.TryGetValue(permissions, out _).Should().BeFalse();
        harness.Store.TryGetValue(modules, out _).Should().BeTrue(
            "the legacy tab clear did not reach the portal's module dictionary, and this one must not either");
    }

    /// <summary>The module invalidation reaches the tab's modules, its permissions and module settings.</summary>
    /// <remarks>
    /// The module-settings category is an addition over the legacy single-tab clear, and a deliberate one:
    /// the only legacy path that evicted module settings was the parameterless overload, which did it by
    /// querying every module in the installation and has no counterpart on this contract. Without this line
    /// module settings would never be invalidated at all.
    /// </remarks>
    [Fact]
    public void InvalidateModules_EvictsTheTabsModulesItsPermissionsAndTheModuleSettingsCategory()
    {
        using CacheHarness harness = NewHarness();

        const int tabId = 51;

        string tabModules = FormatKey(TabModulesKeyTemplate, tabId);
        string permissions = FormatKey(ModulePermissionsKeyTemplate, tabId);
        string settings = ModuleSettingsKeyPrefix + "77";
        string unrelated = FormatKey(TabsKeyTemplate, tabId);

        foreach (string key in new[] { tabModules, permissions, settings, unrelated })
        {
            harness.Cache.Set(key, key, LongExpiry);
        }

        harness.Cache.InvalidateModules(tabId);

        harness.Store.TryGetValue(tabModules, out _).Should().BeFalse();
        harness.Store.TryGetValue(permissions, out _).Should().BeFalse();
        harness.Store.TryGetValue(settings, out _).Should().BeFalse(
            "no other member on this contract would ever evict a module-settings entry");
        harness.Store.TryGetValue(unrelated, out _).Should().BeTrue();
    }

    /// <summary>Each single-key invalidation evicts exactly its own key.</summary>
    [Fact]
    public void TheSingleKeyInvalidations_EvictExactlyTheirOwnKey()
    {
        using CacheHarness harness = NewHarness();

        const int owner = 61;
        const string account = "cache.subject@example.test";

        (string Key, Action<ICacheService> Invalidate)[] cases =
        [
            (FormatKey(TabPermissionsKeyTemplate, owner), cache => cache.InvalidateTabPermissions(owner)),
            (FormatKey(ModulePermissionsKeyTemplate, owner), cache => cache.InvalidateModulePermissions(owner)),
            (FormatKey(ProfileDefinitionsKeyTemplate, owner), cache => cache.InvalidateProfileDefinitions(owner)),
            (FormatUserKey(owner, account), cache => cache.InvalidateUser(owner, account)),
        ];

        foreach ((string key, Action<ICacheService> invalidate) in cases)
        {
            string bystander = FormatKey(PortalKeyTemplate, owner);

            harness.Cache.Set(key, key, LongExpiry);
            harness.Cache.Set(bystander, bystander, LongExpiry);

            invalidate(harness.Cache);

            harness.Store.TryGetValue(key, out _).Should().BeFalse(
                FormattableString.Invariant($"{key} is the key this member names"));
            harness.Store.TryGetValue(bystander, out _).Should().BeTrue(
                FormattableString.Invariant($"{key} must not take an unrelated key with it"));

            harness.Cache.Remove(bystander);
        }
    }

    /// <summary>A blank account name is refused rather than composed into a key.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void InvalidateUser_RefusesABlankAccountName(string? userName)
    {
        using CacheHarness harness = NewHarness();

        Action invalidate = () => harness.Cache.InvalidateUser(-1, userName!);

        invalidate.Should().Throw<ArgumentException>();
    }

    /// <summary>An entry that goes away unobserved withdraws its own registration.</summary>
    /// <remarks>
    /// Without the eviction callback the registry only ever shrank when somebody happened to ask for the
    /// very key that had expired, so it accumulated the name of every key ever written and every category
    /// eviction paid to walk them.
    /// </remarks>
    [Fact]
    public async Task AnEntryEvictedOutsideTheAdapter_WithdrawsItsOwnRegistration()
    {
        using CacheHarness harness = NewHarness();

        string key = FormatKey(TabModulesKeyTemplate, 71);

        harness.Cache.Set(key, "value", LongExpiry);

        TrackedKeysOf(harness.Cache).Should().ContainKey(key);
        CategoryIndexOf(harness.Cache).Should().ContainKey("TabModules");

        harness.Store.Remove(key);

        // The store dispatches the callback to the thread pool rather than running it inline, so the
        // withdrawal is observed rather than assumed to have already happened.
        bool withdrawn = await WaitUntilAsync(
            () => !TrackedKeysOf(harness.Cache).ContainsKey(key),
            TimeSpan.FromSeconds(10));

        withdrawn.Should().BeTrue(
            "an entry that goes away on its own must take its registration with it, or the registry grows "
            + "without bound and every category sweep pays to walk the names of entries that are long gone");

        CategoryIndexOf(harness.Cache).Should().NotContainKey(
            "TabModules",
            "a category left holding nothing is dropped rather than retained as an empty set");
    }

    /// <summary>An emptied category is dropped rather than retained.</summary>
    /// <remarks>
    /// Retaining it would let the index accumulate one entry per category ever used, which is a smaller
    /// version of exactly the growth the index was introduced to remove.
    /// </remarks>
    [Fact]
    public void AnEmptiedCategory_IsDroppedFromTheIndex()
    {
        using CacheHarness harness = NewHarness();

        string first = FormatKey(TabModulesKeyTemplate, 81);
        string second = FormatKey(TabModulesKeyTemplate, 82);

        harness.Cache.Set(first, first, LongExpiry);
        harness.Cache.Set(second, second, LongExpiry);

        CategoryIndexOf(harness.Cache)["TabModules"].Should().HaveCount(2);

        harness.Cache.Remove(first);

        CategoryIndexOf(harness.Cache).Should().ContainKey("TabModules");
        CategoryIndexOf(harness.Cache)["TabModules"].Should().Equal(second);

        harness.Cache.Remove(second);

        CategoryIndexOf(harness.Cache).Should().NotContainKey("TabModules");
    }

    /// <summary>The retirement primitive stops the work it retires, and cannot race its own release.</summary>
    /// <remarks>
    /// Retirement has three separable parts, and dropping any one of them is its own defect. It CANCELS the
    /// attempt's budget, so a factory observing its token stops rather than continuing to run unobserved -
    /// without which every caller that timed out would leave another live factory behind it and the number
    /// of concurrent factories for one key would be bounded by nothing.
    /// </remarks>
    [Fact]
    public void TheRetirementPrimitive_CancelsTheWorkItRetiresAndSurvivesItsOwnRelease()
    {
        Type creationType = typeof(MemoryCacheService)
            .GetNestedType("SharedCreation", BindingFlags.NonPublic)!;

        creationType.Should().NotBeNull("the retirement primitive is what makes a stalled creation recoverable");

        MethodInfo beginWork = creationType.GetMethod("BeginWork", BindingFlags.Instance | BindingFlags.NonPublic)!;
        MethodInfo abandon = creationType.GetMethod("Abandon", BindingFlags.Instance | BindingFlags.NonPublic)!;
        MethodInfo complete = creationType.GetMethod("Complete", BindingFlags.Instance | BindingFlags.NonPublic)!;
        PropertyInfo isAbandoned = creationType.GetProperty(
            "IsAbandoned",
            BindingFlags.Instance | BindingFlags.NonPublic)!;

        // Retired before the work starts: the factory is handed an already-cancelled token, so it declines to
        // do work nobody is waiting for.
        object retiredEarly = Activator.CreateInstance(creationType, nonPublic: true)!;
        abandon.Invoke(retiredEarly, null);

        isAbandoned.GetValue(retiredEarly).Should().Be(true);
        ((CancellationToken)beginWork.Invoke(retiredEarly, [SharedCreationBudget])!)
            .IsCancellationRequested
            .Should()
            .BeTrue("an attempt retired before it began must not start work at all");

        // Retired while the work is running: the live budget is cancelled, so a factory observing its token
        // stops now instead of running on unobserved.
        object retiredLate = Activator.CreateInstance(creationType, nonPublic: true)!;
        CancellationToken running = (CancellationToken)beginWork.Invoke(retiredLate, [SharedCreationBudget])!;

        running.IsCancellationRequested.Should().BeFalse("the work has only just begun");

        abandon.Invoke(retiredLate, null);

        running.IsCancellationRequested.Should().BeTrue(
            "otherwise each timed-out caller would leave another live factory behind it");

        // Released and then retired: the reference is cleared under the monitor before it is disposed, so the
        // late retirement cannot reach a disposed source.
        object released = Activator.CreateInstance(creationType, nonPublic: true)!;
        _ = beginWork.Invoke(released, [SharedCreationBudget]);
        complete.Invoke(released, null);

        Action retireAfterRelease = () => abandon.Invoke(released, null);

        retireAfterRelease.Should().NotThrow(
            "a retirement that arrives after the release must not fault, or a timed-out caller could be "
            + "answered with an object-disposed error instead of a timeout");

        Action releaseTwice = () => complete.Invoke(released, null);

        releaseTwice.Should().NotThrow("the release is idempotent");
    }

    /// <summary>A stalled creation is retired, publishes nothing, and the next caller starts afresh.</summary>
    /// <remarks>
    /// The factory here OBSERVES its token and stops, which keeps the outcome deterministic. The other case
    /// - a factory that ignores its token and returns a value after the retirement - is what the
    /// abandonment flag exists for, and it is asserted in <see
    /// cref="TheRetirementPrimitive_CancelsTheWorkItRetiresAndSurvivesItsOwnRelease"/> rather than here.
    /// </remarks>
    [Fact]
    public async Task GetOrCreateAsync_RetiresAStalledCreationAndLetsTheNextCallerStartAfresh()
    {
        using CacheHarness harness = NewHarness();

        const string account = "stalled.subject@example.test";
        string key = FormatUserKey(-1, account);
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource tokenObservedCancelled = new(TaskCreationOptions.RunContinuationsAsynchronously);

        Task<string> stalled = harness.Cache.GetOrCreateAsync(
            key,
            async token =>
            {
                entered.TrySetResult();

                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, token);
                }
                catch (OperationCanceledException)
                {
                    // Recorded and then rethrown: recording is the evidence that the retirement really did
                    // cancel the work rather than merely stop waiting for it, and rethrowing is what a
                    // factory that honours its token does.
                    tokenObservedCancelled.TrySetResult();

                    throw;
                }

                return "unreachable: the delay only ends by cancellation";
            },
            LongExpiry);

        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Func<Task> awaitStalled = () => stalled;

        // THE TYPE IS THE CACHE'S OWN, NOT A BARE TimeoutException, and the distinction is the point rather
        // than a detail.
        string message = (await awaitStalled.Should().ThrowAsync<CacheProductionTimeoutException>(
                "a creation bound to no caller's lifetime needs a limit of its own, or a caller could wait on "
                + "it indefinitely"))
            .Which
            .Message;

        message.Should().Contain("category UserInfo");
        message.Should().Contain("fingerprint");
        message.Should().NotContain(account, "not even a timeout message may quote a composed key");

        await tokenObservedCancelled.Task.WaitAsync(TimeSpan.FromSeconds(10));

        int freshInvocations = 0;

        Task<string> next = harness.Cache.GetOrCreateAsync(
            key,
            _ =>
            {
                Interlocked.Increment(ref freshInvocations);

                return Task.FromResult("recovered");
            },
            LongExpiry);

        // Bounded well below a second budget: joining the stalled creation instead of starting a fresh one is
        // precisely the defect being ruled out, and it would show as another thirty-second wait.
        string recovered = await next.WaitAsync(TimeSpan.FromSeconds(10));

        freshInvocations.Should().Be(
            1,
            "the registration was retired, so the next caller starts a fresh attempt instead of joining the "
            + "stalled one and waiting out another budget");
        recovered.Should().Be("recovered");
        harness.Cache.Get<string>(key).Should().Be(
            "recovered",
            "the fresh attempt publishes normally: a retired predecessor must leave the key usable, not "
            + "merely unblocked");
    }

    /// <summary>Builds an adapter over a bounded store of its own.</summary>
    /// <param name="multiplier">The configured performance multiplier.</param>
    /// <returns>The harness, which owns the store and must be disposed.</returns>
    private static CacheHarness NewHarness(int multiplier = DefaultMultiplier)
    {
        MemoryCache store = new(new MemoryCacheOptions { SizeLimit = 4096 });

        return new CacheHarness(
            store,
            new MemoryCacheService(store, Options.Create(new CachingOptions { PerformanceMultiplier = multiplier })));
    }

    /// <summary>Formats a single-placeholder legacy key template, invariantly.</summary>
    /// <param name="template">One of the legacy templates declared on this class.</param>
    /// <param name="identifier">The portal or tab identifier.</param>
    /// <returns>The composed key.</returns>
    private static string FormatKey(string template, int identifier) =>
        string.Format(CultureInfo.InvariantCulture, template, identifier);

    /// <summary>Composes the two-placeholder legacy account key, preserving both pipe delimiters.</summary>
    /// <param name="portalId">The portal the membership belongs to.</param>
    /// <param name="userName">The account name.</param>
    /// <returns>The composed key.</returns>
    private static string FormatUserKey(int portalId, string userName) =>
        string.Format(CultureInfo.InvariantCulture, UserKeyTemplate, portalId, userName);

    /// <summary>A key belonging to no declared category, distinct on every call.</summary>
    /// <returns>The key.</returns>
    private static string UniqueKey() =>
        string.Create(CultureInfo.InvariantCulture, $"ZZTest.{Guid.NewGuid():N}");

    /// <summary>Reads the adapter's tracked-key registry.</summary>
    /// <param name="cache">The adapter under test.</param>
    /// <returns>The registry.</returns>
    private static ConcurrentDictionary<string, byte> TrackedKeysOf(ICacheService cache) =>
        (ConcurrentDictionary<string, byte>)typeof(MemoryCacheService)
            .GetField("_trackedKeys", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(cache)!;

    /// <summary>Reads the adapter's category index.</summary>
    /// <param name="cache">The adapter under test.</param>
    /// <returns>The index, keyed by category prefix.</returns>
    private static Dictionary<string, HashSet<string>> CategoryIndexOf(ICacheService cache) =>
        (Dictionary<string, HashSet<string>>)typeof(MemoryCacheService)
            .GetField("_keysByCategory", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(cache)!;

    /// <summary>Polls a condition until it holds or the allowance elapses.</summary>
    /// <param name="condition">The condition to poll.</param>
    /// <param name="allowance">How long to keep polling.</param>
    /// <returns><see langword="true"/> when the condition held before the allowance elapsed.</returns>
    /// <remarks>
    /// Used only where the runtime dispatches work to the thread pool, so the alternative is not a shorter
    /// test but a flaky one. The allowance is generous because it is only ever consumed when the assertion
    /// is about to fail anyway.
    /// </remarks>
    private static async Task<bool> WaitUntilAsync(Func<bool> condition, TimeSpan allowance)
    {
        DateTime deadline = DateTime.UtcNow + allowance;

        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return true;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(25));
        }

        return condition();
    }

    /// <summary>An adapter and the store it was built over, disposed together.</summary>
    private sealed class CacheHarness : IDisposable
    {
        private readonly MemoryCache _store;

        /// <summary>Initialises a new instance of the <see cref="CacheHarness"/> class.</summary>
        /// <param name="store">The store the adapter writes to.</param>
        /// <param name="cache">The adapter under test.</param>
        internal CacheHarness(MemoryCache store, ICacheService cache)
        {
            _store = store;
            Cache = cache;
        }

        /// <summary>Gets the adapter under test.</summary>
        internal ICacheService Cache { get; }

        /// <summary>Gets the store beneath it, for assertions the contract cannot express.</summary>
        internal IMemoryCache Store => _store;

        /// <inheritdoc />
        public void Dispose() => _store.Dispose();
    }
}
