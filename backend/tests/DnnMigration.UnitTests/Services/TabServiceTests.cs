using DnnMigration.Application.Abstractions;
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
    /// <remarks>
    /// <para>
    /// THIS PINS A CORRECTION OF THIS SOLUTION'S OWN STATED POSITION, NOT JUST OF ITS CODE. Every page change
    /// was recorded as TAB_UPDATED, and the audit catalogue asserted in a comment that
    /// TAB_SENT_TO_RECYCLE_BIN and TAB_RESTORED could never be raised here because they had no committed
    /// boundary - the page surface being narrow, and the legacy recycle-bin page being out of scope. That
    /// reasoning located the operation by the legacy PAGE that performed it rather than by the state
    /// TRANSITION it made. Recycling and restoring are not separate operations in this solution: both are
    /// carried on the update request as its delete flag, which the mapper assigns, so the narrow PUT IS the
    /// boundary and both events have a real producer.
    /// </para>
    /// <para>
    /// The cost of the mislabel was that the two questions a page trail is asked most often - who took this
    /// page down, and who put it back - could not be answered from it, because a removal and a title change
    /// looked identical. Both names are verbatim legacy members: TAB_SENT_TO_RECYCLE_BIN
    /// (TabController.vb:L840 and L952) and TAB_RESTORED (RecycleBin.ascx.vb:L280).
    /// </para>
    /// </remarks>
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
    /// Repeating the delete flag a page already carries is recorded as a revision, because no state changed.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    /// <remarks>
    /// The narrowing matters as much as the two records above. Keying the event name on the NEW value rather
    /// than on a transition would make every ordinary save of an already-recycled page read as a fresh
    /// recycling, which trades one false reading for another and inflates any count taken from the trail. This
    /// is the assertion that forbids it.
    /// </remarks>
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

    /// <summary>
    /// A page change retains neither the current nor the former caller-authored name.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    /// <remarks>
    /// The page identifier is the durable attribution. Copying either name into the independently retained
    /// logging store would create a second access, retention and deletion lifecycle for caller-authored text.
    /// </remarks>
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
            Permissions = new Mock<IPermissionService>(MockBehavior.Loose);
            Audit = new Mock<IAuditSink>(MockBehavior.Loose);

            // EVERY PAGE IS PERMITTED BY DEFAULT, so the suites below stay about the behaviour they name.
            // The listing narrows its rows to the pages the caller may act on, and the permission service
            // answers an administrator with every page it was asked about - which is the caller these suites
            // describe. Returning the whole set here reproduces that answer, so a case about ordering or
            // hierarchy is not silently also a case about permissions. The narrowing itself is asserted by
            // the cases that override this setup explicitly.
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
    /// The listing narrows its rows to the pages the caller may act on, and keeps navigation order.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    /// <remarks>
    /// <para>
    /// MIGRATION: <c>ModuleSettings.ascx.vb:L214-L219</c> left the page selector populated and ENABLED for a
    /// caller in the administrators role and DISABLED it for everyone else - "tab administrators can only
    /// manage their own tab" - re-applying the same rule on postback at <c>L332-L338</c> so a disabled control
    /// could not be reached by replaying the form. A tab administrator therefore never chose a page from a
    /// portal-wide list; the page was the one they had arrived on, which was ambient request state this
    /// solution does not have. Offering that caller the pages it holds EDIT on is the equivalent that survives
    /// the loss of that ambient state, and it is strictly narrower than the list the legacy rendered.
    /// </para>
    /// <para>
    /// ORDER IS ASSERTED AS WELL AS MEMBERSHIP. A page's position is meaningful only relative to the parent
    /// that precedes it, so filtering must remove rows without re-ordering the ones that remain. A filter
    /// implemented by re-querying the permitted identifiers would satisfy a membership assertion and silently
    /// lose the navigation order.
    /// </para>
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

        Result<IReadOnlyList<TabListItemDto>> outcome = await harness.Service
            .GetTabsAsync(PortalId, CancellationToken.None);

        outcome.IsSuccess.Should().BeTrue();
        outcome.Value.Select(row => row.TabId).Should().Equal(
            permitted,
            "only the permitted pages are offered, in the navigation order the read produced");
    }

    /// <summary>
    /// The narrowing happens AFTER the cache is populated, so the cached entry stays caller-independent.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    /// <remarks>
    /// ⚠ THE ORDERING IS THE CORRECTNESS ARGUMENT, NOT AN OPTIMISATION. The entry is keyed by tenant alone.
    /// Narrowing before the write would store one caller's permitted subset under a key every caller reads,
    /// and the next caller - including the tenant's administrator - would be served that subset as though it
    /// were the tenant's page set. This case is the only thing standing between that and a reviewer's memory:
    /// it asserts the two values differ, and asserts which of them is the full one.
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

        Result<IReadOnlyList<TabListItemDto>> outcome = await harness.Service
            .GetTabsAsync(PortalId, CancellationToken.None);

        outcome.Value.Should().HaveCount(1, "this caller may act on one page");

        harness.Cached.Should().NotBeNull("the listing is cached per tenant");
        harness.Cached!.Should().HaveCount(
            harness.AllTabs.Count,
            "the cached entry is the TENANT's page set, not this caller's permitted subset");
    }

    /// <summary>
    /// An unresolvable permission state withholds every row rather than offering them all.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    /// <remarks>
    /// FAIL-CLOSED, BECAUSE THIS LISTING IS A SET OF CHOICES. A page offered as a placement target that the
    /// create action would then refuse is worse than no target offered: the caller fills the form and the
    /// submission is rejected. Failing open would also make an availability problem read as a permission grant.
    /// </remarks>
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

        Result<IReadOnlyList<TabListItemDto>> outcome = await harness.Service
            .GetTabsAsync(PortalId, CancellationToken.None);

        outcome.IsSuccess.Should().BeTrue("an unresolvable grant state is a refusal, not a fault to propagate");
        outcome.Value.Should().BeEmpty();
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

            // The listing refuses an unknown tenant before it reads anything, so a harness whose portal does
            // not "exist" answers a not-found failure and every listing case would measure that instead of what
            // it names.
            Portals.Setup(repository => repository.ExistsAsync(
                    It.IsAny<int>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(true);

            UnitOfWork.Setup(unit => unit.SaveChangesAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(1);

            Permissions = new Mock<IPermissionService>(MockBehavior.Loose);
            Cache = new Mock<ICacheService>(MockBehavior.Loose);

            // A PASS-THROUGH THAT RECORDS. The real cache would answer from its entry; this one always invokes
            // the factory and keeps the value the factory produced, which is what lets a case assert WHAT WAS
            // CACHED as distinct from what was returned. That distinction is the whole point of the ordering
            // under test: the entry is keyed by tenant alone, so it must hold the tenant's rows and nothing
            // caller-specific.
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
            // The listing narrows its rows to the pages the caller may act on, and the permission service
            // answers an administrator with every page it was asked about - which is the caller these suites
            // describe. Returning the whole set here reproduces that answer, so a case about ordering or
            // hierarchy is not silently also a case about permissions. The narrowing itself is asserted by
            // the cases that override this setup explicitly.
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
