using DnnMigration.Application.Abstractions;
using DnnMigration.Application.Dtos.Tab;
using DnnMigration.Application.Options;
using DnnMigration.Application.Services;
using DnnMigration.Domain.Abstractions.Repositories;
using DnnMigration.Domain.Abstractions.Services;
using DnnMigration.Domain.Common;
using DnnMigration.Domain.Entities;
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
/// <para>
/// WHY THIS CLASS EXISTS AND WHY IT IS NARROW. The page service is otherwise covered end to end by
/// <c>DnnMigration.IntegrationTests.Api.TabApiTests</c>, which exercises the whole update path against a real
/// database - so duplicating the ordering, ancestry and cache behaviour here would add maintenance cost
/// without adding assurance. Two things that suite genuinely cannot see are asserted here instead. The blank
/// name is refused by the request validator BEFORE the service is reached on the HTTP path, so only a direct
/// call can prove the service's own guard still holds for a caller that bypasses the pipeline. And the audit
/// record is emitted to a sink the integration host does not capture, so only a substituted sink can prove
/// what it contains.
/// </para>
/// <para>
/// MIGRATION: the record is the target's replacement for the legacy event-log entry typed
/// <c>EventLogType.TAB_UPDATED</c> (<c>EventLogController.vb</c>, among the forty-three members declared at
/// L38-L77). The legacy store is out of scope, so the event name is preserved and the record travels through
/// a package-neutral sink instead.
/// </para>
/// </remarks>
public class TabServiceTests
{
    private const int PortalId = -1;

    private const int TabId = 4;

    private const string TabName = "Measured Page";

    /// <summary>
    /// The service refuses to be constructed without every collaborator it depends on.
    /// </summary>
    [Fact]
    public void Service_RequiresEveryCollaborator()
    {
        var tabs = new Mock<ITabRepository>().Object;
        var portals = new Mock<IPortalRepository>().Object;
        var unitOfWork = new Mock<IUnitOfWork>().Object;
        var cache = new Mock<ICacheService>().Object;
        var currentUser = new Mock<ICurrentUser>().Object;
        var audit = new Mock<IAuditSink>().Object;
        var caching = new CachingOptions();

        Assert.Throws<ArgumentNullException>("tabs", () =>
        {
            _ = new TabService(null!, portals, unitOfWork, cache, currentUser, audit, caching);
        });
        Assert.Throws<ArgumentNullException>("portals", () =>
        {
            _ = new TabService(tabs, null!, unitOfWork, cache, currentUser, audit, caching);
        });
        Assert.Throws<ArgumentNullException>("unitOfWork", () =>
        {
            _ = new TabService(tabs, portals, null!, cache, currentUser, audit, caching);
        });
        Assert.Throws<ArgumentNullException>("cache", () =>
        {
            _ = new TabService(tabs, portals, unitOfWork, null!, currentUser, audit, caching);
        });
        Assert.Throws<ArgumentNullException>("currentUser", () =>
        {
            _ = new TabService(tabs, portals, unitOfWork, cache, null!, audit, caching);
        });
        Assert.Throws<ArgumentNullException>("audit", () =>
        {
            _ = new TabService(tabs, portals, unitOfWork, cache, currentUser, null!, caching);
        });
        Assert.Throws<ArgumentNullException>("caching", () =>
        {
            _ = new TabService(tabs, portals, unitOfWork, cache, currentUser, audit, null!);
        });
    }

    /// <summary>
    /// A page name that is absent, empty or whitespace-only is refused, and nothing is written.
    /// </summary>
    /// <param name="submittedName">The name to submit.</param>
    /// <returns>A task representing the assertion.</returns>
    /// <remarks>
    /// MIGRATION: <c>managetabs.ascx</c> L36-L37 declares a required-field validator over the page-name box,
    /// and a required-field validator fails on an empty and a whitespace-only value just as it does on an
    /// absent one - so none of these three submissions could reach the legacy controller. The empty case is
    /// the one that regressed: an earlier revision accepted it as "the legacy no-text value arriving
    /// explicitly", which was not a value the legacy screen could produce. A page with no name cannot be
    /// picked out of a navigation menu or a page list, so accepting one produced a page an operator could
    /// create and then not find.
    /// </remarks>
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
    /// A committed page update records the legacy TAB_UPDATED event with the tenant, the page and the acting
    /// account, and carries no free-text field.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    /// <remarks>
    /// The absence assertions are as deliberate as the presence ones. The description, keywords and head text
    /// are caller-supplied free text of unbounded interest and up to five hundred characters each; a trail is
    /// not a change log of every field, and carrying them would make every page edit write half a kilobyte of
    /// prose into the log.
    /// </remarks>
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
        record.ActorUserName.Should().Be("admin");
        record.ResourceType.Should().Be("Tab");
        record.ResourceId.Should().Be("4");
        record.Properties["TabName"].Should().Be(TabName);

        record.Properties.Should().NotContainKey("Description");
        record.Properties.Should().NotContainKey("Keywords");
        record.Properties.Should().NotContainKey("PageHeadText");
    }

    /// <summary>
    /// A rename carries the former name, and an unchanged name does not.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    /// <remarks>
    /// A record naming only the new value cannot answer "what was this page called yesterday", which is the
    /// question a rename raises. Recording the former name only when it actually changed keeps the record from
    /// asserting a rename that did not happen.
    /// </remarks>
    [Fact]
    public async Task UpdateTab_CarriesTheFormerNameOnlyWhenItChanged()
    {
        Harness renamed = Harness.Ready();

        UpdateTabRequest renaming = ValidRequest();
        renaming.TabName = "Renamed Page";

        await renamed.Service.UpdateTabAsync(TabId, renaming, CancellationToken.None);

        AuditEvent renameRecord = renamed.AuditRecords.Should().ContainSingle().Subject;
        renameRecord.Properties["TabName"].Should().Be("Renamed Page");
        renameRecord.Properties["PreviousTabName"].Should().Be(TabName);

        Harness unchanged = Harness.Ready();

        await unchanged.Service.UpdateTabAsync(TabId, ValidRequest(), CancellationToken.None);

        AuditEvent unchangedRecord = unchanged.AuditRecords.Should().ContainSingle().Subject;
        unchangedRecord.Properties["TabName"].Should().Be(TabName);
        unchangedRecord.Properties.Should().NotContainKey("PreviousTabName");
    }

    /// <summary>
    /// An unauthenticated caller leaves the actor absent rather than having one fabricated.
    /// </summary>
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
        record.ActorUserName.Should().BeNull();
    }

    /// <summary>
    /// An unknown page is refused without emitting a record.
    /// </summary>
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

    /// <summary>
    /// Builds an update request that changes nothing the assertions depend on.
    /// </summary>
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

    /// <summary>
    /// Assembles the service over recording doubles, exposing the stored page as mutable state.
    /// </summary>
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
            Audit = new Mock<IAuditSink>(MockBehavior.Loose);

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
                Audit.Object,
                Caching);
        }

        public TabService Service { get; }

        public Mock<ITabRepository> Tabs { get; }

        public Mock<IPortalRepository> Portals { get; }

        public Mock<IUnitOfWork> UnitOfWork { get; }

        public Mock<ICacheService> Cache { get; }

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

    /// <summary>
    /// A hierarchy deeper than a stored page path can express is answered rather than overflowing.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    /// <remarks>
    /// <para>
    /// The traversal that renumbers a portal's pages once recursed one frame per level of a hierarchy whose
    /// depth is set by stored data, so a deep enough tree ended the process rather than the request. It is now
    /// an explicit stack with a depth bound, and this asserts the bound is reported to the caller.
    /// </para>
    /// <para>
    /// Built in memory through the mocked repository, which is the only practical way to reach a hierarchy
    /// thousands of levels deep: proving the same property over HTTP would need thousands of create calls to
    /// assert something that has nothing to do with transport.
    /// </para>
    /// </remarks>
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

    /// <summary>
    /// A hierarchy at the deepest representable level is renumbered successfully.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    /// <remarks>
    /// The companion to the test above, and the one that keeps the depth limit honest: a limit that refused a
    /// hierarchy the column can hold would be a new restriction on callers rather than a statement of what is
    /// storable. The chain here is exactly as deep as the path column permits.
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
    /// traversal and would pass under breadth-first ordering too - it cannot tell the two apart. The
    /// expectation is stated as the full path sequence, which pins depth-first descent and sibling order
    /// together.
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
    /// Assembles a page service over a whole in-memory page set, for the traversal assertions.
    /// </summary>
    /// <remarks>
    /// Kept separate from <see cref="Harness"/> rather than folded into it. That one answers the listing with
    /// exactly the page under test, which is what keeps the name and audit assertions about those two
    /// behaviours; these need a real hierarchy, answered consistently by both the listing and the
    /// parent-identifier read, and merging the two would make each assertion depend on setup the other needs.
    /// </remarks>
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

            // Answered from the same in-memory set the listing is answered from, so a hierarchy built by one
            // of the factories below is self-consistent: a page that has children here is a page that has
            // children in the listing the traversal walks.
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

            UnitOfWork.Setup(unit => unit.SaveChangesAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(1);

            Service = new TabService(
                Tabs.Object,
                Portals.Object,
                UnitOfWork.Object,
                new Mock<ICacheService>(MockBehavior.Loose).Object,
                new Mock<ICurrentUser>(MockBehavior.Loose).Object,
                new Mock<IAuditSink>(MockBehavior.Loose).Object,
                new CachingOptions());
        }

        internal List<Tab> AllTabs { get; }

        internal Mock<ITabRepository> Tabs { get; }

        internal Mock<IPortalRepository> Portals { get; }

        internal Mock<IUnitOfWork> UnitOfWork { get; }

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

        /// <summary>
        /// A portal whose pages branch, so that depth-first and breadth-first traversals differ.
        /// </summary>
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
}
