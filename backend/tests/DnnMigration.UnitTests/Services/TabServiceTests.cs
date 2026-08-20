using System.Globalization;
using DnnMigration.Application.Abstractions;
using DnnMigration.Application.Dtos.Common;
using DnnMigration.Application.Dtos.Tab;
using DnnMigration.Application.Options;
using DnnMigration.Application.Services;
using DnnMigration.Domain.Abstractions.Repositories;
using DnnMigration.Domain.Abstractions.Services;
using DnnMigration.Domain.Common;
using DnnMigration.Domain.Entities;
using DnnMigration.Domain.Enums;
using FluentAssertions;
using Moq;
using Xunit;

namespace DnnMigration.UnitTests.Services;

/// <summary>
/// Covers the page-update behaviours that no other suite can reach: the blank-name guard that survives
/// alongside the request validator, the audit record the legacy event log kept for a page change, and the
/// hierarchy traversal that renumbers a portal's pages.
/// </summary>
/// <remarks>
/// WHY THIS CLASS EXISTS AND WHY IT IS NARROW. The page service is otherwise covered end to end by
/// <c>DnnMigration.IntegrationTests.Api.TabApiTests</c>, which exercises the whole update path against a
/// real database - so duplicating the ordering, ancestry and cache behaviour here would add maintenance
/// cost without adding assurance. Two things that suite genuinely cannot see are asserted here instead.
/// </remarks>
public class TabServiceTests
{
    private const int PortalId = -1;

    private const int TabId = 4;

    private const string TabName = "Measured Page";

    /// <summary>
    /// A request for the WHOLE collection, used by the assertions that are about narrowing and caching rather
    /// than about paging.
    /// </summary>
    /// <remarks>
    /// A page size of zero is the application layer's "unpaged" and is unreachable over HTTP, where the shared
    /// validator requires at least one row - so these assertions can still read the whole permitted sequence
    /// while no caller of the endpoint can.
    /// </remarks>
    private static TabPagedRequest WholeCollection => new TabPagedRequest { PageSize = 0 };

    /// <summary>The service refuses to be constructed without every collaborator it depends on.</summary>
    [Fact]
    public void Service_RequiresEveryCollaborator()
    {
        var tabs = new Mock<ITabRepository>().Object;
        var portals = new Mock<IPortalRepository>().Object;
        var unitOfWork = new Mock<IUnitOfWork>().Object;
        var cache = new Mock<ICacheService>().Object;
        var currentUser = new Mock<ICurrentUser>().Object;
        var permissions = new Mock<IPermissionService>().Object;
        var audit = new Mock<IAuditSink>().Object;
        var caching = new CachingOptions();

        Assert.Throws<ArgumentNullException>("tabs", () =>
        {
            _ = new TabService(null!, portals, unitOfWork, cache, currentUser, permissions, audit, caching);
        });
        Assert.Throws<ArgumentNullException>("portals", () =>
        {
            _ = new TabService(tabs, null!, unitOfWork, cache, currentUser, permissions, audit, caching);
        });
        Assert.Throws<ArgumentNullException>("unitOfWork", () =>
        {
            _ = new TabService(tabs, portals, null!, cache, currentUser, permissions, audit, caching);
        });
        Assert.Throws<ArgumentNullException>("cache", () =>
        {
            _ = new TabService(tabs, portals, unitOfWork, null!, currentUser, permissions, audit, caching);
        });
        Assert.Throws<ArgumentNullException>("currentUser", () =>
        {
            _ = new TabService(tabs, portals, unitOfWork, cache, null!, permissions, audit, caching);
        });
        Assert.Throws<ArgumentNullException>("permissions", () =>
        {
            _ = new TabService(tabs, portals, unitOfWork, cache, currentUser, null!, audit, caching);
        });
        Assert.Throws<ArgumentNullException>("audit", () =>
        {
            _ = new TabService(tabs, portals, unitOfWork, cache, currentUser, permissions, null!, caching);
        });
        Assert.Throws<ArgumentNullException>("caching", () =>
        {
            _ = new TabService(tabs, portals, unitOfWork, cache, currentUser, permissions, audit, null!);
        });
    }

    /// <summary>A page name that is absent, empty or whitespace-only is refused, and nothing is written.</summary>
    /// <param name="submittedName">The name to submit.</param>
    /// <returns>A task representing the assertion.</returns>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t\n")]
    public async Task UpdateTab_WithABlankName_IsRefusedAndWritesNothing(string? submittedName)
    {
        Harness harness = Harness.Ready();

        UpdateTabRequest request = ValidRequest();
        request.TabName = submittedName!;

        DomainException surfaced = await Assert.ThrowsAsync<DomainException>(
            () => harness.Service.UpdateTabAsync(TabId, request, CancellationToken.None));

        surfaced.PublicDetail.Should().Be("A page name is required.");

        harness.UnitOfWork.Verify(
            unit => unit.SaveChangesAsync(It.IsAny<CancellationToken>()),
            Times.Never);
        harness.AuditRecords.Should().BeEmpty();
    }

    /// <summary>
    /// The five portal-designated system pages always remain linkable, matching the disabled checkbox on
    /// the legacy editor.
    /// </summary>
    /// <param name="specialPageRole">Portal column that designates the page.</param>
    /// <returns>A task representing the assertion.</returns>
    [Theory]
    [InlineData("Admin")]
    [InlineData("Splash")]
    [InlineData("Home")]
    [InlineData("Login")]
    [InlineData("User")]
    public async Task UpdateTab_ForASpecialPage_ForcesDisableLinkOff(string specialPageRole)
    {
        Harness harness = Harness.Ready();
        var portal = new Portal
        {
            PortalId = PortalId,
            PortalName = "Measured Portal",
        };

        switch (specialPageRole)
        {
            case "Admin":
                portal.AdminTabId = TabId;
                break;
            case "Splash":
                portal.SplashTabId = TabId;
                break;
            case "Home":
                portal.HomeTabId = TabId;
                break;
            case "Login":
                portal.LoginTabId = TabId;
                break;
            case "User":
                portal.UserTabId = TabId;
                break;
            default:
                throw new ArgumentOutOfRangeException(
                    nameof(specialPageRole),
                    specialPageRole,
                    "Unknown special-page role.");
        }

        harness.Portals
            .Setup(repository => repository.GetByIdAsync(
                PortalId,
                false,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(portal);

        UpdateTabRequest request = ValidRequest();
        request.DisableLink = true;

        Result<TabDetailDto> outcome = await harness.Service
            .UpdateTabAsync(TabId, request, CancellationToken.None);

        outcome.IsSuccess.Should().BeTrue();
        harness.StoredTab!.DisableLink.Should().BeFalse();
        outcome.Value.DisableLink.Should().BeFalse();
    }

    /// <summary>
    /// A committed page update records the legacy TAB_UPDATED event with the tenant, the page and the
    /// acting account, and carries no free-text field.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task UpdateTab_RecordsTheLegacyTabUpdatedEvent()
    {
        Harness harness = Harness.Ready();
        harness.CurrentUser.SetupGet(caller => caller.IsAuthenticated).Returns(true);
        harness.CurrentUser.SetupGet(caller => caller.UserId).Returns(11);
        harness.CurrentUser.SetupGet(caller => caller.UserName).Returns("admin");

        UpdateTabRequest request = ValidRequest();
        request.Description = "A long description that must not reach the audit trail.";
        request.Keywords = "keywords, that, must, not, reach, the, trail";

        Result<TabDetailDto> outcome = await harness.Service
            .UpdateTabAsync(TabId, request, CancellationToken.None);

        outcome.IsSuccess.Should().BeTrue();

        AuditEvent record = harness.AuditRecords.Should().ContainSingle().Subject;
        record.EventName.Should().Be(AuditEventNames.TabUpdated);
        record.EventName.Should().Be("TAB_UPDATED");
        record.Outcome.Should().Be(AuditOutcome.Succeeded);
        record.PortalId.Should().Be(PortalId);
        record.ActorUserId.Should().Be(11);
        record.ResourceType.Should().Be("Tab");
        record.ResourceId.Should().Be("4");
        record.Properties.Should().NotContainKey("TabName");

        record.Properties.Should().NotContainKey("Description");
        record.Properties.Should().NotContainKey("Keywords");
        record.Properties.Should().NotContainKey("PageHeadText");

        record.Properties["Operation"].Should().Be(
            "Revise",
            "the page's delete flag did not change, so this is a revision rather than a recycling");
    }

    /// <summary>
    /// Recycling a page and restoring one are recorded under the two legacy lifecycle event names, not as
    /// plain updates.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task UpdateTab_RecordsTheRecycleAndRestoreTransitionsUnderTheirOwnNames()
    {
        Harness recycling = Harness.Ready();
        recycling.StoredTab!.IsDeleted = false;

        UpdateTabRequest recycle = ValidRequest();
        recycle.IsDeleted = true;

        Result<TabDetailDto> recycled = await recycling.Service
            .UpdateTabAsync(TabId, recycle, CancellationToken.None);

        recycled.IsSuccess.Should().BeTrue();

        AuditEvent recycleRecord = recycling.AuditRecords.Should().ContainSingle().Subject;
        recycleRecord.EventName.Should().Be(AuditEventNames.TabSentToRecycleBin);
        recycleRecord.EventName.Should().Be("TAB_SENT_TO_RECYCLE_BIN");
        recycleRecord.Properties["Operation"].Should().Be("Recycle");
        recycleRecord.Properties["IsDeleted"].Should().Be(bool.TrueString);

        Harness restoring = Harness.Ready();
        restoring.StoredTab!.IsDeleted = true;

        UpdateTabRequest restore = ValidRequest();
        restore.IsDeleted = false;

        Result<TabDetailDto> restored = await restoring.Service
            .UpdateTabAsync(TabId, restore, CancellationToken.None);

        restored.IsSuccess.Should().BeTrue();

        AuditEvent restoreRecord = restoring.AuditRecords.Should().ContainSingle().Subject;
        restoreRecord.EventName.Should().Be(AuditEventNames.TabRestored);
        restoreRecord.EventName.Should().Be("TAB_RESTORED");
        restoreRecord.Properties["Operation"].Should().Be("Restore");
        restoreRecord.Properties["IsDeleted"].Should().Be(bool.FalseString);
    }

    /// <summary>
    /// Repeating the delete flag a page already carries is recorded as a revision, because no state
    /// changed.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task UpdateTab_RecordsARevisionWhenTheDeleteFlagIsUnchanged()
    {
        Harness harness = Harness.Ready();
        harness.StoredTab!.IsDeleted = true;

        UpdateTabRequest request = ValidRequest();
        request.IsDeleted = true;

        Result<TabDetailDto> outcome = await harness.Service
            .UpdateTabAsync(TabId, request, CancellationToken.None);

        outcome.IsSuccess.Should().BeTrue();

        AuditEvent record = harness.AuditRecords.Should().ContainSingle().Subject;
        record.EventName.Should().Be(AuditEventNames.TabUpdated);
        record.Properties["Operation"].Should().Be("Revise");
    }

    /// <summary>A page change retains neither the current nor the former caller-authored name.</summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task UpdateTab_DoesNotRetainCurrentOrFormerNames()
    {
        Harness renamed = Harness.Ready();

        UpdateTabRequest renaming = ValidRequest();
        renaming.TabName = "Renamed Page";

        await renamed.Service.UpdateTabAsync(TabId, renaming, CancellationToken.None);

        AuditEvent renameRecord = renamed.AuditRecords.Should().ContainSingle().Subject;
        renameRecord.Properties.Should().NotContainKey("TabName");
        renameRecord.Properties.Should().NotContainKey("PreviousTabName");

        Harness unchanged = Harness.Ready();

        await unchanged.Service.UpdateTabAsync(TabId, ValidRequest(), CancellationToken.None);

        AuditEvent unchangedRecord = unchanged.AuditRecords.Should().ContainSingle().Subject;
        unchangedRecord.Properties.Should().NotContainKey("TabName");
        unchangedRecord.Properties.Should().NotContainKey("PreviousTabName");
    }

    /// <summary>An unauthenticated caller leaves the actor absent rather than having one fabricated.</summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task UpdateTab_LeavesTheActorAbsentForAnUnauthenticatedCaller()
    {
        Harness harness = Harness.Ready();
        harness.CurrentUser.SetupGet(caller => caller.IsAuthenticated).Returns(false);
        harness.CurrentUser.SetupGet(caller => caller.UserId).Returns(99);

        await harness.Service.UpdateTabAsync(TabId, ValidRequest(), CancellationToken.None);

        AuditEvent record = harness.AuditRecords.Should().ContainSingle().Subject;
        record.ActorUserId.Should().BeNull();
    }

    /// <summary>An unknown page is refused without emitting a record.</summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task UpdateTab_WhenUnknown_RecordsNothing()
    {
        Harness harness = Harness.Ready();
        harness.StoredTab = null;

        Result<TabDetailDto> outcome = await harness.Service
            .UpdateTabAsync(TabId, ValidRequest(), CancellationToken.None);

        outcome.IsFailure.Should().BeTrue();
        outcome.Reason!.Code.Should().Be("tab.not_found");
        harness.AuditRecords.Should().BeEmpty();
    }

    /// <summary>Builds an update request that changes nothing the assertions depend on.</summary>
    /// <returns>A well-formed update request.</returns>
    private static UpdateTabRequest ValidRequest() => new()
    {
        TabName = TabName,
        ParentId = null,
        IsVisible = true,
        DisableLink = false,
        IsSecure = false,
        IsDeleted = false,
    };

    /// <summary>Assembles the service over recording doubles, exposing the stored page as mutable state.</summary>
    private sealed class Harness
    {
        private Harness()
        {
            Caching = new CachingOptions();
            AuditRecords = [];
            StoredTab = new Tab
            {
                TabId = TabId,
                PortalId = PortalId,
                TabName = TabName,
                TabOrder = 1,
                Level = 0,
                ParentId = null,
                IsVisible = true,
            };

            Tabs = new Mock<ITabRepository>(MockBehavior.Loose);
            Portals = new Mock<IPortalRepository>(MockBehavior.Loose);
            UnitOfWork = new Mock<IUnitOfWork>(MockBehavior.Loose);
            Cache = new Mock<ICacheService>(MockBehavior.Loose);
            CurrentUser = new Mock<ICurrentUser>(MockBehavior.Loose);
            Permissions = new Mock<IPermissionService>(MockBehavior.Loose);
            Audit = new Mock<IAuditSink>(MockBehavior.Loose);

            // EVERY PAGE IS PERMITTED BY DEFAULT, so the suites below stay about the behaviour they name.
            Permissions
                .Setup(service => service.ListTabsWithPermissionAsync(
                    It.IsAny<int>(),
                    It.IsAny<int?>(),
                    It.IsAny<IReadOnlyCollection<int>>(),
                    It.IsAny<PermissionKey>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync((
                    int _,
                    int? __,
                    IReadOnlyCollection<int> tabIds,
                    PermissionKey ___,
                    CancellationToken ____) =>
                    Result<IReadOnlyList<int>>.Success(tabIds.ToList()));

            Audit
                .Setup(sink => sink.Record(It.IsAny<AuditEvent>()))
                .Callback<AuditEvent>(AuditRecords.Add);

            Tabs
                .Setup(tabs => tabs.GetByIdAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((int id, CancellationToken _) =>
                    StoredTab is { } stored && stored.TabId == id ? stored : null);

            // The owning portal's page set. The edited page itself is the only member, so the renumbering
            // pass has a tree to walk and nothing else moves - which keeps this suite about the two
            // behaviours it exists to assert rather than about ordering.
            Tabs
                .Setup(tabs => tabs.GetByPortalIdAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => StoredTab is { } stored
                    ? new List<Tab> { stored }
                    : new List<Tab>());

            Tabs
                .Setup(tabs => tabs.ListParentTabIdsAsync(
                    It.IsAny<int?>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(Array.Empty<int>());

            Portals
                .Setup(portals => portals.GetByIdAsync(
                    It.IsAny<int>(),
                    It.IsAny<bool>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => new Portal { PortalId = PortalId, PortalName = "Measured Portal" });

            Service = new TabService(
                Tabs.Object,
                Portals.Object,
                UnitOfWork.Object,
                Cache.Object,
                CurrentUser.Object,
                Permissions.Object,
                Audit.Object,
                Caching);
        }

        public TabService Service { get; }

        public Mock<ITabRepository> Tabs { get; }

        public Mock<IPortalRepository> Portals { get; }

        public Mock<IUnitOfWork> UnitOfWork { get; }

        public Mock<ICacheService> Cache { get; }

        public Mock<IPermissionService> Permissions { get; }

        public Mock<ICurrentUser> CurrentUser { get; }

        public Mock<IAuditSink> Audit { get; }

        public CachingOptions Caching { get; }

        /// <summary>Every audit record the service emitted, in order.</summary>
        public List<AuditEvent> AuditRecords { get; }

        /// <summary>The page the repository answers with, or null for an unknown page.</summary>
        public Tab? StoredTab { get; set; }

        /// <summary>Builds a harness wired for a successful update.</summary>
        /// <returns>A ready harness.</returns>
        public static Harness Ready() => new();
    }

    /// <summary>A hierarchy deeper than a stored page path can express is answered rather than overflowing.</summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task UpdateTab_OnAHierarchyDeeperThanThePathCanHold_AnswersRatherThanOverflowing()
    {
        const int depth = 4_000;
        HierarchyHarness harness = HierarchyHarness.WithChain(depth);

        DomainException failure = await Assert.ThrowsAsync<DomainException>(
            () => harness.Service.UpdateTabAsync(
                TabId,
                harness.Request("Deep"),
                CancellationToken.None));

        failure.PublicDetail.Should().Contain(
            "deeper than",
            "a caller is told the hierarchy is too deep, not handed a server fault");
    }

    /// <summary>A hierarchy at the deepest representable level is renumbered successfully.</summary>
    /// <returns>A task representing the assertion.</returns>
    /// <remarks>
    /// The companion to the test above, and the one that keeps the depth limit honest: a limit that refused
    /// a hierarchy the column can hold would be a new restriction on callers rather than a statement of
    /// what is storable. The chain here is exactly as deep as the path column permits.
    /// </remarks>
    [Fact]
    public async Task UpdateTab_OnTheDeepestRepresentableHierarchy_Succeeds()
    {
        const int deepestRepresentable = 127;
        HierarchyHarness harness = HierarchyHarness.WithChain(deepestRepresentable);

        Result<TabDetailDto> outcome = await harness.Service.UpdateTabAsync(
            TabId,
            harness.Request("Deep"),
            CancellationToken.None);

        outcome.IsSuccess.Should().BeTrue(
            "a hierarchy whose path the column can hold must still be renumbered");
    }

    /// <summary>
    /// The traversal visits pages in depth-first pre-order, so the order values it assigns are unchanged by
    /// the move away from recursion.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    /// <remarks>
    /// The two running counters assign each page's order in visitation sequence, so the traversal ORDER is
    /// load-bearing rather than cosmetic: any reordering silently renumbers every page in the portal. The
    /// hierarchy below is deliberately branched rather than a chain, because a chain has only one possible
    /// traversal and would pass under breadth-first ordering too - it cannot tell the two apart.
    /// </remarks>
    [Fact]
    public async Task UpdateTab_AssignsOrderInDepthFirstPreOrder()
    {
        HierarchyHarness harness = HierarchyHarness.WithBranchedPortal();

        Result<TabDetailDto> outcome = await harness.Service.UpdateTabAsync(
            TabId,
            harness.Request("Root A"),
            CancellationToken.None);

        outcome.IsSuccess.Should().BeTrue();

        // The root under test carries two children, the first of which carries one of its own; the second
        // root follows. Depth-first pre-order therefore descends fully through the first root's line before
        // reaching the second, which breadth-first ordering would not do.
        harness.AllTabs
            .OrderBy(tab => tab.TabOrder)
            .Select(tab => tab.TabPath)
            .Should().ContainInOrder(
                "//RootA",
                "//RootA//ChildOne",
                "//RootA//ChildOne//GrandChild",
                "//RootA//ChildTwo",
                "//RootB");
    }

    /// <summary>
    /// Every page's depth is assigned from its position in the hierarchy rather than from its stored value.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task UpdateTab_AssignsLevelFromPosition()
    {
        HierarchyHarness harness = HierarchyHarness.WithBranchedPortal();

        await harness.Service.UpdateTabAsync(TabId, harness.Request("Root A"), CancellationToken.None);

        harness.AllTabs.Single(tab => tab.TabPath == "//RootA").Level.Should().Be(0);
        harness.AllTabs.Single(tab => tab.TabPath == "//RootA//ChildOne").Level.Should().Be(1);
        harness.AllTabs.Single(tab => tab.TabPath == "//RootA//ChildOne//GrandChild").Level.Should().Be(2);
    }

    /// <summary>
    /// The listing narrows its rows to the pages the caller may act on, and keeps navigation order.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    /// <remarks>
    /// ORDER IS ASSERTED AS WELL AS MEMBERSHIP. A page's position is meaningful only relative to the parent
    /// that precedes it, so filtering must remove rows without re-ordering the ones that remain. A filter
    /// implemented by re-querying the permitted identifiers would satisfy a membership assertion and
    /// silently lose the navigation order.
    /// </remarks>
    [Fact]
    public async Task GetTabs_NarrowsTheRowsToThePagesTheCallerMayActOn()
    {
        HierarchyHarness harness = HierarchyHarness.WithBranchedPortal();

        // The first root and its grandchild, chosen so the permitted set is NOT a contiguous prefix: a filter
        // that returned the first N rows would otherwise pass.
        int[] permitted = [TabId, TabId + 2];

        harness.Permissions
            .Setup(service => service.ListTabsWithPermissionAsync(
                It.IsAny<int>(),
                It.IsAny<int?>(),
                It.IsAny<IReadOnlyCollection<int>>(),
                It.IsAny<PermissionKey>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<IReadOnlyList<int>>.Success(permitted));

        Result<PagedResult<TabListItemDto>> outcome = await harness.Service
            .GetTabsAsync(PortalId, WholeCollection, CancellationToken.None);

        outcome.IsSuccess.Should().BeTrue();
        outcome.Value.Items.Select(row => row.TabId).Should().Equal(
            permitted,
            "only the permitted pages are offered, in the navigation order the read produced");
        outcome.Value.TotalCount.Should().Be(
            permitted.Length,
            "the reported total is the size of the collection this caller is paging, not the tenant's raw count");
    }

    /// <summary>
    /// The narrowing happens AFTER the cache is populated, so the cached entry stays caller-independent.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    /// <remarks>
    /// ⚠ THE ORDERING IS THE CORRECTNESS ARGUMENT, NOT AN OPTIMISATION. The entry is keyed by tenant alone.
    /// Narrowing before the write would store one caller's permitted subset under a key every caller reads,
    /// and the next caller - including the tenant's administrator - would be served that subset as though
    /// it were the tenant's page set.
    /// </remarks>
    [Fact]
    public async Task GetTabs_CachesTheWholeTenantAndNarrowsOnlyWhatItReturns()
    {
        HierarchyHarness harness = HierarchyHarness.WithBranchedPortal();

        harness.Permissions
            .Setup(service => service.ListTabsWithPermissionAsync(
                It.IsAny<int>(),
                It.IsAny<int?>(),
                It.IsAny<IReadOnlyCollection<int>>(),
                It.IsAny<PermissionKey>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<IReadOnlyList<int>>.Success([TabId]));

        Result<PagedResult<TabListItemDto>> outcome = await harness.Service
            .GetTabsAsync(PortalId, WholeCollection, CancellationToken.None);

        outcome.Value.Items.Should().HaveCount(1, "this caller may act on one page");

        harness.Cached.Should().NotBeNull("the listing is cached per tenant");
        harness.Cached!.Should().HaveCount(
            harness.AllTabs.Count,
            "the cached entry is the TENANT's page set, not this caller's permitted subset");
    }

    /// <summary>An unresolvable permission state withholds every row rather than offering them all.</summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task GetTabs_WithhholdsEveryRowWhenThePermissionStateCannotBeResolved()
    {
        HierarchyHarness harness = HierarchyHarness.WithBranchedPortal();

        harness.Permissions
            .Setup(service => service.ListTabsWithPermissionAsync(
                It.IsAny<int>(),
                It.IsAny<int?>(),
                It.IsAny<IReadOnlyCollection<int>>(),
                It.IsAny<PermissionKey>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<IReadOnlyList<int>>.Failure("permission.unavailable", "unreachable"));

        Result<PagedResult<TabListItemDto>> outcome = await harness.Service
            .GetTabsAsync(PortalId, WholeCollection, CancellationToken.None);

        outcome.IsSuccess.Should().BeTrue("an unresolvable grant state is a refusal, not a fault to propagate");
        outcome.Value.Items.Should().BeEmpty();
        outcome.Value.TotalCount.Should().Be(0);
    }

    /// <summary>Assembles a page service over a whole in-memory page set, for the traversal assertions.</summary>
    private sealed class HierarchyHarness
    {
        private HierarchyHarness(List<Tab> tabs)
        {
            AllTabs = tabs;

            Tabs = new Mock<ITabRepository>(MockBehavior.Loose);
            Portals = new Mock<IPortalRepository>(MockBehavior.Loose);
            UnitOfWork = new Mock<IUnitOfWork>(MockBehavior.Loose);

            Tabs.Setup(repository => repository.GetByIdAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((int tabId, CancellationToken _) =>
                    AllTabs.FirstOrDefault(tab => tab.TabId == tabId));

            Tabs.Setup(repository => repository.GetByPortalIdAsync(
                    It.IsAny<int>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((int portalId, CancellationToken _) =>
                    AllTabs.Where(tab => tab.PortalId == portalId).ToList());

            Tabs.Setup(repository => repository.TabNameExistsAsync(
                    It.IsAny<int?>(), It.IsAny<string>(), It.IsAny<int?>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(false);

            Tabs.Setup(repository => repository.ListParentTabIdsAsync(
                    It.IsAny<int?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((int? portalId, CancellationToken _) => AllTabs
                    .Where(tab => tab.PortalId == portalId && tab.ParentId.HasValue)
                    .Select(tab => tab.ParentId!.Value)
                    .Distinct()
                    .ToList());

            Portals.Setup(repository => repository.GetByIdAsync(
                    It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new Portal
                {
                    PortalId = PortalId,
                    PortalName = "Measured Portal",
                    DefaultLanguage = "en-US",
                    HomeDirectory = "Portals/0",
                });

            // The listing refuses an unknown tenant before it reads anything, so a harness whose portal
            // does not "exist" answers a not-found failure and every listing case would measure that
            // instead of what it names.
            Portals.Setup(repository => repository.ExistsAsync(
                    It.IsAny<int>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(true);

            UnitOfWork.Setup(unit => unit.SaveChangesAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(1);

            Permissions = new Mock<IPermissionService>(MockBehavior.Loose);
            Cache = new Mock<ICacheService>(MockBehavior.Loose);

            // A PASS-THROUGH THAT RECORDS. The real cache would answer from its entry; this one always
            // invokes the factory and keeps the value the factory produced, which is what lets a case
            // assert WHAT WAS CACHED as distinct from what was returned.
            Cache.Setup(cache => cache.GetOrCreateAsync(
                    It.IsAny<string>(),
                    It.IsAny<Func<CancellationToken, Task<IReadOnlyList<TabListItemDto>>>>(),
                    It.IsAny<TimeSpan>(),
                    It.IsAny<CancellationToken>()))
                .Returns(async (
                    string _,
                    Func<CancellationToken, Task<IReadOnlyList<TabListItemDto>>> factory,
                    TimeSpan __,
                    CancellationToken token) =>
                {
                    IReadOnlyList<TabListItemDto> produced = await factory(token);
                    Cached = produced;
                    return produced;
                });

            // EVERY PAGE IS PERMITTED BY DEFAULT, so the suites below stay about the behaviour they name.
            Permissions
                .Setup(service => service.ListTabsWithPermissionAsync(
                    It.IsAny<int>(),
                    It.IsAny<int?>(),
                    It.IsAny<IReadOnlyCollection<int>>(),
                    It.IsAny<PermissionKey>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync((
                    int _,
                    int? __,
                    IReadOnlyCollection<int> tabIds,
                    PermissionKey ___,
                    CancellationToken ____) =>
                    Result<IReadOnlyList<int>>.Success(tabIds.ToList()));

            Service = new TabService(
                Tabs.Object,
                Portals.Object,
                UnitOfWork.Object,
                Cache.Object,
                new Mock<ICurrentUser>(MockBehavior.Loose).Object,
                Permissions.Object,
                new Mock<IAuditSink>(MockBehavior.Loose).Object,
                new CachingOptions());
        }

        internal List<Tab> AllTabs { get; }

        internal Mock<ITabRepository> Tabs { get; }

        internal Mock<IPortalRepository> Portals { get; }

        internal Mock<IUnitOfWork> UnitOfWork { get; }

        internal Mock<IPermissionService> Permissions { get; }

        internal Mock<ICacheService> Cache { get; }

        /// <summary>The rows the service handed the cache, or <see langword="null"/> if it cached nothing.</summary>
        internal IReadOnlyList<TabListItemDto>? Cached { get; private set; }

        internal TabService Service { get; }

        /// <summary>
        /// A portal whose pages form a chain of the requested depth, rooted at the page under test.
        /// </summary>
        /// <param name="depth">Number of levels in the chain, including the root.</param>
        /// <returns>The assembled harness.</returns>
        internal static HierarchyHarness WithChain(int depth)
        {
            var tabs = new List<Tab> { Page(TabId, "Root", null) };

            for (int level = 1; level < depth; level++)
            {
                tabs.Add(Page(
                    TabId + level,
                    FormattableString.Invariant($"Level{level}"),
                    TabId + level - 1));
            }

            return new HierarchyHarness(tabs);
        }

        /// <summary>A portal whose pages branch, so that depth-first and breadth-first traversals differ.</summary>
        /// <returns>The assembled harness.</returns>
        internal static HierarchyHarness WithBranchedPortal() => new(
        [
            Page(TabId, "RootA", null, order: 1),
            Page(TabId + 1, "ChildOne", TabId, order: 1),
            Page(TabId + 2, "GrandChild", TabId + 1, order: 1),
            Page(TabId + 3, "ChildTwo", TabId, order: 2),
            Page(TabId + 4, "RootB", null, order: 2),
        ]);

        /// <summary>Builds an update carrying the submitted name and the stored page's own values.</summary>
        /// <param name="tabName">The page name to submit.</param>
        /// <returns>The request.</returns>
        internal UpdateTabRequest Request(string tabName)
        {
            Tab stored = AllTabs.Single(tab => tab.TabId == TabId);

            return new UpdateTabRequest
            {
                TabName = tabName,
                ParentId = stored.ParentId,
                IsVisible = true,
            };
        }

        private static Tab Page(int tabId, string name, int? parentId, int order = 1) => new()
        {
            TabId = tabId,
            PortalId = PortalId,
            TabName = name,
            ParentId = parentId,
            TabOrder = order,
            IsVisible = true,
        };
    }

    /// <summary>
    /// A host-level page carries no tenant, so its depth and its materialised path are recomputed from its
    /// own parent chain and nothing else is renumbered.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    /// <remarks>
    /// A host page with no parent is the base of that computation: the walk finds no ancestor, so the path is
    /// the separator followed by the page's own name and the depth is the root depth.
    /// </remarks>
    [Fact]
    public async Task UpdateTab_ForARootHostPage_MaterialisesThePathFromItsOwnNameAlone()
    {
        HostPageHarness harness = HostPageHarness.WithRootPage();

        Result<TabDetailDto> outcome = await harness.Service
            .UpdateTabAsync(HostPageHarness.PageId, harness.Request(), CancellationToken.None);

        outcome.IsSuccess.Should().BeTrue();
        outcome.Value.PortalId.Should().BeNull("a host page belongs to no tenant");

        outcome.Value.Level.Should().Be(0, "a page with no parent sits at the root depth");
        outcome.Value.TabPath.Should().Be(
            "//HostConsole",
            "the path is the separator followed by the page's own name, with the space in "
            + "\"Host Console\" removed exactly as the legacy path generator removed every non-word "
            + "character");

        harness.StoredPage.TabPath.Should().Be(
            "//HostConsole",
            "the path is written onto the page that is staged, not only onto the representation returned");
    }

    /// <summary>
    /// A nested host page's path is assembled from its whole ancestor chain, outermost first, and the tenant
    /// machinery - the sibling listing, the portal read, the renumbering pass and the tenant cache eviction -
    /// is left entirely alone.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    /// <remarks>
    /// The negative half is the load-bearing half. A host page has no portal, and the legacy code had no way
    /// to say so: it passed the page's portal identifier straight through, which for a host page was the
    /// integer sentinel -1, and so evicted the cache of the tenant whose real identifier IS -1. Asserting
    /// that this path evicts the host entries and touches no tenant's is what keeps that behaviour from
    /// returning.
    /// </remarks>
    [Fact]
    public async Task UpdateTab_ForANestedHostPage_MaterialisesThePathFromItsWholeAncestorChain()
    {
        HostPageHarness harness = HostPageHarness.WithChain();

        Result<TabDetailDto> outcome = await harness.Service
            .UpdateTabAsync(
                HostPageHarness.PageId,
                harness.Request(HostPageHarness.BranchId),
                CancellationToken.None);

        outcome.IsSuccess.Should().BeTrue();

        outcome.Value.TabPath.Should().Be(
            "//HostRoot//BranchTwo//HostConsole",
            "the chain is walked upward and each ancestor's name is inserted ahead of the ones already "
            + "collected, so the assembled path reads outermost first and ends with the page itself");

        outcome.Value.Level.Should().Be(
            2,
            "depth is one deeper than the parent's own stored depth rather than a count of the walk");

        harness.Cache.Verify(cache => cache.InvalidateHost(), Times.Once);
        harness.Cache.Verify(cache => cache.InvalidateTabs(It.IsAny<int>()), Times.Never);
        harness.Cache.Verify(cache => cache.InvalidatePortal(It.IsAny<int>()), Times.Never);

        harness.Tabs.Verify(
            tabs => tabs.GetByPortalIdAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.Never);
        harness.Tabs.Verify(
            tabs => tabs.UpdateOrderAsync(It.IsAny<Tab>(), It.IsAny<CancellationToken>()),
            Times.Never);
        harness.Portals.Verify(
            portals => portals.GetByIdAsync(
                It.IsAny<int>(),
                It.IsAny<bool>(),
                It.IsAny<CancellationToken>()),
            Times.Never);

        harness.Tabs.Verify(tabs => tabs.UpdateAsync(harness.StoredPage, It.IsAny<CancellationToken>()), Times.Once);
        harness.UnitOfWork.Verify(unit => unit.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);

        AuditEvent record = harness.AuditRecords.Should().ContainSingle().Subject;
        record.EventName.Should().Be(AuditEventNames.TabUpdated);
        record.PortalId.Should().BeNull(
            "the record says the page has no tenant rather than naming the tenant whose identifier "
            + "happens to be the legacy absent-integer sentinel");
        record.ResourceId.Should().Be(HostPageHarness.PageId.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// A cycle already present in the stored host pages terminates the ancestor walk at its bound and still
    /// yields a path, rather than hanging the request.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    /// <remarks>
    /// The host tree has no portal-wide listing to bound the walk against - which is why the walk carries a
    /// fixed bound of its own - and a cycle is representable in the stored data because
    /// <c>dbo.Tabs.ParentId</c> is an ordinary self-referencing column with no constraint that forbids one.
    /// This case is deliberately written so that a REMOVED bound is a hang rather than a wrong answer, which
    /// is the failure mode the bound exists to prevent; the exact segment count is asserted so that a
    /// widened or narrowed bound is reported as a number rather than as a timeout.
    /// </remarks>
    [Fact]
    public async Task UpdateTab_ForAHostPageWhoseParentChainCycles_TerminatesAtTheWalkBound()
    {
        HostPageHarness harness = HostPageHarness.WithCycle();

        Result<TabDetailDto> outcome = await harness.Service
            .UpdateTabAsync(
                HostPageHarness.PageId,
                harness.Request(HostPageHarness.BranchId),
                CancellationToken.None);

        outcome.IsSuccess.Should().BeTrue();

        string path = outcome.Value.TabPath.Should().NotBeNull().And.Subject!;

        string[] segments = path.Split("//", StringSplitOptions.RemoveEmptyEntries);

        segments.Should().HaveCount(
            65,
            "the walk is bounded at sixty-four ancestors, and the page's own name is appended after it - so "
            + "a cycle produces exactly that many segments instead of running forever");

        segments[^1].Should().Be(
            "HostConsole",
            "the page being updated is always the last segment of its own path");
        segments[^2].Should().Be(
            "BranchTwo",
            "the segment before it is the page's immediate parent, which is where the walk began");
    }

    /// <summary>
    /// Assembles the page service over a set of HOST-level pages - pages whose <c>PortalId</c> is absent -
    /// which is the state that sends an update down the host branch.
    /// </summary>
    /// <remarks>
    /// A separate harness rather than a parameter on the two above, because a host page differs from a
    /// tenant page in what the service is allowed to touch, not merely in a field value: there is no sibling
    /// listing to read, no portal to load and no tenant cache to evict, so those collaborators are left
    /// unconfigured here and asserted to stay unused.
    /// </remarks>
    private sealed class HostPageHarness
    {
        /// <summary>Identifier of the host page every case updates.</summary>
        internal const int PageId = 41;

        /// <summary>Identifier of the page's immediate parent, where a chain is used.</summary>
        internal const int BranchId = 42;

        /// <summary>Identifier of the outermost page of the chain.</summary>
        internal const int RootId = 43;

        /// <summary>Name of the page under test, carrying a space the path generator removes.</summary>
        private const string PageName = "Host Console";

        private HostPageHarness(List<Tab> pages)
        {
            HostPages = pages;
            AuditRecords = [];

            Tabs = new Mock<ITabRepository>(MockBehavior.Loose);
            Portals = new Mock<IPortalRepository>(MockBehavior.Loose);
            UnitOfWork = new Mock<IUnitOfWork>(MockBehavior.Loose);
            Cache = new Mock<ICacheService>(MockBehavior.Loose);
            CurrentUser = new Mock<ICurrentUser>(MockBehavior.Loose);
            Permissions = new Mock<IPermissionService>(MockBehavior.Loose);
            Audit = new Mock<IAuditSink>(MockBehavior.Loose);

            Tabs.Setup(tabs => tabs.GetByIdAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((int tabId, CancellationToken _) =>
                    HostPages.FirstOrDefault(page => page.TabId == tabId));

            // The host scope is addressed as an absent portal, so the has-children lookup is answered for
            // null rather than for an identifier.
            Tabs.Setup(tabs => tabs.ListParentTabIdsAsync(null, It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => HostPages
                    .Where(page => page.ParentId.HasValue)
                    .Select(page => page.ParentId!.Value)
                    .Distinct()
                    .ToList());

            UnitOfWork.Setup(unit => unit.SaveChangesAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(1);

            CurrentUser.SetupGet(caller => caller.IsAuthenticated).Returns(true);
            CurrentUser.SetupGet(caller => caller.UserId).Returns(7);
            CurrentUser.SetupGet(caller => caller.UserName).Returns("host");

            Audit.Setup(sink => sink.Record(It.IsAny<AuditEvent>()))
                .Callback<AuditEvent>(AuditRecords.Add);

            Service = new TabService(
                Tabs.Object,
                Portals.Object,
                UnitOfWork.Object,
                Cache.Object,
                CurrentUser.Object,
                Permissions.Object,
                Audit.Object,
                new CachingOptions());
        }

        /// <summary>Every host page the repository answers with.</summary>
        internal List<Tab> HostPages { get; }

        internal Mock<ITabRepository> Tabs { get; }

        internal Mock<IPortalRepository> Portals { get; }

        internal Mock<IUnitOfWork> UnitOfWork { get; }

        internal Mock<ICacheService> Cache { get; }

        internal Mock<ICurrentUser> CurrentUser { get; }

        internal Mock<IPermissionService> Permissions { get; }

        internal Mock<IAuditSink> Audit { get; }

        /// <summary>Every audit record the service emitted, in order.</summary>
        internal List<AuditEvent> AuditRecords { get; }

        internal TabService Service { get; }

        /// <summary>The host page under test, as the service left it.</summary>
        internal Tab StoredPage => HostPages.Single(page => page.TabId == PageId);

        /// <summary>A single host page with no parent.</summary>
        /// <returns>The assembled harness.</returns>
        internal static HostPageHarness WithRootPage() => new([HostPage(PageId, PageName, null)]);

        /// <summary>A host page two levels below the outermost page of its chain.</summary>
        /// <returns>The assembled harness.</returns>
        /// <remarks>
        /// Both ancestor names carry a character the path generator removes - a space and a colon - so the
        /// assembled path proves the stripping is applied to every segment rather than only to the page's
        /// own name. The parent's stored depth is 1, which is what the recomputed depth is measured against.
        /// </remarks>
        internal static HostPageHarness WithChain() => new(
        [
            HostPage(RootId, "Host Root", null),
            HostPage(BranchId, "Branch: Two", RootId, level: 1),
            HostPage(PageId, PageName, BranchId, level: 2),
        ]);

        /// <summary>A host page whose parent chain closes back on itself.</summary>
        /// <returns>The assembled harness.</returns>
        internal static HostPageHarness WithCycle() => new(
        [
            HostPage(BranchId, "Branch: Two", PageId, level: 1),
            HostPage(PageId, PageName, BranchId, level: 2),
        ]);

        /// <summary>Builds an update carrying the stored name and the requested parent.</summary>
        /// <param name="parentId">The parent to submit, or <see langword="null"/> for a root-level page.</param>
        /// <returns>The request.</returns>
        internal UpdateTabRequest Request(int? parentId = null) => new()
        {
            TabName = PageName,
            ParentId = parentId,
            IsVisible = true,
            DisableLink = false,
            IsSecure = false,
            IsDeleted = false,
        };

        /// <summary>Builds one host-level page: a page whose portal is absent rather than -1.</summary>
        /// <param name="tabId">The page identifier.</param>
        /// <param name="name">The page name, as an operator typed it.</param>
        /// <param name="parentId">The parent identifier, or <see langword="null"/> for a root-level page.</param>
        /// <param name="level">The page's stored depth.</param>
        /// <returns>The page.</returns>
        private static Tab HostPage(int tabId, string name, int? parentId, int level = 0) => new()
        {
            TabId = tabId,
            PortalId = null,
            TabName = name,
            ParentId = parentId,
            Level = level,
            TabOrder = 1,
            IsVisible = true,
        };
    }
}
