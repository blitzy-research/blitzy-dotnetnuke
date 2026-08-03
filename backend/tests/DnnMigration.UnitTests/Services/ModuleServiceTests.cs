using System.Reflection;
using DnnMigration.Application.Abstractions;
using DnnMigration.Application.Dtos.Common;
using DnnMigration.Application.Dtos.Module;
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
using Module = DnnMigration.Domain.Entities.Module;

namespace DnnMigration.UnitTests.Services;

/// <summary>
/// Covers the module workflow: placement resolution, the wide-effect operations that reach pages the caller
/// did not name, the two-scope settings reconciliation, the cached definition catalogue, and the content
/// portability round trip.
/// </summary>
/// <remarks>
/// <para>
/// A module and its placement are two separate rows, and almost every operation here has to decide which
/// placement it is acting on before it can act at all. When the caller names one, it must belong to the
/// module; when the caller names none, the lowest-numbered placement is chosen. The two cases are asserted
/// separately for every member that resolves a placement, because the consequence of getting it wrong is
/// silently editing the appearance of a module on a page the caller was not looking at.
/// </para>
/// <para>
/// Three operations deliberately reach beyond the row the caller addressed: asking for a module to appear on
/// every page, withdrawing it from every page, and copying one placement's appearance onto every other. Each
/// reports what it did through an advisory carried on a successful result rather than through the status
/// alone, and the assertions below check the advisory text as well as the writes, because the advisory is
/// the only signal an operator has that a single edit changed several pages.
/// </para>
/// <para>
/// The settings reconciliation is a full replacement rather than a merge: a name the caller omitted is
/// removed, not left alone. That is asserted explicitly, together with the case-insensitive name matching
/// that decides whether a submitted name is the same setting as a stored one.
/// </para>
/// </remarks>
public class ModuleServiceTests
{
    private const int PortalId = -1;

    private const int OtherPortalId = 3;

    private const int ModuleId = 0;

    private const int OtherModuleId = 12;

    private const int TabModuleId = 1;

    private const int OtherTabModuleId = 2;

    private const int TabId = 5;

    private const int SecondTabId = 6;

    private const int AdminTabId = 90;

    private const int AdminChildTabId = 91;

    private const int ModuleDefinitionId = 7;

    private const int DesktopModuleId = 8;

    private const int CallerId = 42;

    private const string FriendlyName = "Text/HTML";

    private const string ModuleTitle = "Welcome";

    private const string DefaultPaneName = "ContentPane";

    private const string BusinessController = "Dnn.Modules.Html.HtmlController";

    private const string PackageName = "DNN_HTML";

    private const string PackageVersion = "04.09.00";

    private const string SiteSettingsDefinitionName = "Site Settings";

    private const string PortalNotFoundCode = "module.portal_not_found";

    private const string RequestInvalidCode = "module.request_invalid";

    private const string PlacementNotFoundCode = "module.placement_not_found";

    private const string NotFoundCode = "module.not_found";

    private const string DefinitionNotFoundCode = "module.definition_not_found";

    private const string TabNotFoundCode = "module.tab_not_found";

    private const string SettingInvalidCode = "module.setting_invalid";

    private const string NotPortableCode = "module.not_portable";

    private const string ContentInvalidCode = "module.content_invalid";

    private const string WideEffectCode = "module.update.wide_effect";

    /// <summary>
    /// The module contract exposes ten asynchronous operations and nothing else.
    /// </summary>
    [Fact]
    public void ModuleContract_OffersExactlyTenOperations()
    {
        MethodInfo[] members = typeof(IModuleService).GetMethods();

        members.Should().HaveCount(10);
        foreach (MethodInfo member in members)
        {
            member.Name.Should().EndWith("Async");
            typeof(Task).IsAssignableFrom(member.ReturnType).Should().BeTrue();
            member.GetParameters()[0].Name.Should().Be("portalId");
            member.GetParameters()[^1].ParameterType.Should().Be(typeof(CancellationToken));
        }
    }

    /// <summary>
    /// The service refuses to be constructed without every collaborator it depends on.
    /// </summary>
    [Fact]
    public void Service_RequiresEveryCollaborator()
    {
        var modules = new Mock<IModuleRepository>().Object;
        var definitions = new Mock<IModuleDefinitionRepository>().Object;
        var tabs = new Mock<ITabRepository>().Object;
        var portals = new Mock<IPortalRepository>().Object;
        var unitOfWork = new Mock<IUnitOfWork>().Object;
        var cache = new Mock<ICacheService>().Object;
        var currentUser = new Mock<ICurrentUser>().Object;
        var controllers = new Mock<IModuleBusinessControllerFactory>().Object;
        var caching = new CachingOptions();

        Assert.Throws<ArgumentNullException>("modules", () =>
        {
            _ = new ModuleService(null!, definitions, tabs, portals, unitOfWork, cache, currentUser, controllers, caching);
        });
        Assert.Throws<ArgumentNullException>("definitions", () =>
        {
            _ = new ModuleService(modules, null!, tabs, portals, unitOfWork, cache, currentUser, controllers, caching);
        });
        Assert.Throws<ArgumentNullException>("tabs", () =>
        {
            _ = new ModuleService(modules, definitions, null!, portals, unitOfWork, cache, currentUser, controllers, caching);
        });
        Assert.Throws<ArgumentNullException>("portals", () =>
        {
            _ = new ModuleService(modules, definitions, tabs, null!, unitOfWork, cache, currentUser, controllers, caching);
        });
        Assert.Throws<ArgumentNullException>("unitOfWork", () =>
        {
            _ = new ModuleService(modules, definitions, tabs, portals, null!, cache, currentUser, controllers, caching);
        });
        Assert.Throws<ArgumentNullException>("cache", () =>
        {
            _ = new ModuleService(modules, definitions, tabs, portals, unitOfWork, null!, currentUser, controllers, caching);
        });
        Assert.Throws<ArgumentNullException>("currentUser", () =>
        {
            _ = new ModuleService(modules, definitions, tabs, portals, unitOfWork, cache, null!, controllers, caching);
        });
        Assert.Throws<ArgumentNullException>("businessControllers", () =>
        {
            _ = new ModuleService(modules, definitions, tabs, portals, unitOfWork, cache, currentUser, null!, caching);
        });
        Assert.Throws<ArgumentNullException>("caching", () =>
        {
            _ = new ModuleService(modules, definitions, tabs, portals, unitOfWork, cache, currentUser, controllers, null!);
        });
    }

    /// <summary>
    /// Listing modules requires a paging request.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task ListModules_RequiresARequest()
    {
        Harness harness = Harness.Ready();

        await Assert.ThrowsAsync<ArgumentNullException>(
            () => harness.Service.ListModulesAsync(PortalId, null!, null, false, CancellationToken.None));
    }

    /// <summary>
    /// A malformed paging request is refused with its own measured wording.
    /// </summary>
    /// <param name="violation">The single field to spoil.</param>
    /// <param name="expectedMessage">The message the service is measured to report.</param>
    /// <returns>A task representing the assertion.</returns>
    [Theory]
    [InlineData("negative-index", "The page index must not be negative.")]
    [InlineData("negative-size", "The page size must not be negative.")]
    [InlineData("oversize", "The page size must not exceed 100.")]
    [InlineData("overlong-query", "The search text must not exceed 256 characters.")]
    public async Task ListModules_RefusesAMalformedPagingRequest(string violation, string expectedMessage)
    {
        Harness harness = Harness.Ready();
        var request = new PagedRequest();
        switch (violation)
        {
            case "negative-index":
                request.PageIndex = -1;
                break;
            case "negative-size":
                request.PageSize = -1;
                break;
            case "oversize":
                request.PageSize = 101;
                break;
            default:
                request.Query = new string('q', 257);
                break;
        }

        Result<PagedResult<ModuleListItemDto>> outcome = await harness.Service
            .ListModulesAsync(PortalId, request, null, false, CancellationToken.None);

        outcome.IsFailure.Should().BeTrue();
        outcome.Reason!.Code.Should().Be(RequestInvalidCode);
        outcome.Reason!.Message.Should().Be(expectedMessage);
    }

    /// <summary>
    /// The paging request is checked before the tenant is probed, so a malformed request costs no read.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task ListModules_ChecksTheRequestBeforeTheTenant()
    {
        Harness harness = Harness.Ready();

        await harness.Service.ListModulesAsync(
            PortalId,
            new PagedRequest { PageIndex = -5 },
            null,
            false,
            CancellationToken.None);

        harness.Portals.Verify(
            p => p.ExistsAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    /// <summary>
    /// Listing modules of a tenant that does not exist is refused.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task ListModules_RefusesAnUnknownTenant()
    {
        Harness harness = Harness.Ready();
        harness.PortalExists = false;

        Result<PagedResult<ModuleListItemDto>> outcome = await harness.Service
            .ListModulesAsync(PortalId, new PagedRequest(), null, false, CancellationToken.None);

        outcome.IsFailure.Should().BeTrue();
        outcome.Reason!.Code.Should().Be(PortalNotFoundCode);
        outcome.Reason!.Message.Should().Be($"Portal {PortalId} does not exist.");
    }

    /// <summary>
    /// The page, the page filter and the deleted-row switch all reach the store unchanged.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task ListModules_PassesTheFiltersThroughUnchanged()
    {
        Harness harness = Harness.Ready();

        await harness.Service.ListModulesAsync(
            PortalId,
            new PagedRequest { PageIndex = 2, PageSize = 20, Query = "wel" },
            TabId,
            includeDeleted: true,
            CancellationToken.None);

        harness.Modules.Verify(
            m => m.ListAsync(PortalId, TabId, true, 2, 20, "wel", It.IsAny<CancellationToken>()),
            Times.Once);
    }

    /// <summary>
    /// A search term consisting only of white space is treated as absent rather than searched for.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task ListModules_TreatsAWhitespaceQueryAsAbsent()
    {
        Harness harness = Harness.Ready();

        await harness.Service.ListModulesAsync(
            PortalId,
            new PagedRequest { Query = "   " },
            null,
            false,
            CancellationToken.None);

        harness.Modules.Verify(
            m => m.ListAsync(
                PortalId,
                null,
                false,
                0,
                10,
                null,
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    /// <summary>
    /// No definition catalogue is read for an empty page, because there is nothing to name.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task ListModules_ReadsNoDefinitionNamesForAnEmptyPage()
    {
        Harness harness = Harness.Ready();
        harness.ModulePage = PagedResult<Module>.Empty;

        Result<PagedResult<ModuleListItemDto>> outcome = await harness.Service
            .ListModulesAsync(PortalId, new PagedRequest(), null, false, CancellationToken.None);

        outcome.IsSuccess.Should().BeTrue();
        outcome.Value.Items.Should().BeEmpty();
        harness.Definitions.Verify(
            d => d.ListAsync(It.IsAny<int?>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    /// <summary>
    /// One row is produced per placement, so a module appearing on two pages appears twice in the list.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task ListModules_ProducesOneRowPerPlacement()
    {
        Harness harness = Harness.Ready();
        Module module = StoredModule();
        harness.ModulePage = PagedResult<Module>.Unpaged([module]);
        harness.PlacementsByModuleId[ModuleId] =
        [
            Placement(TabModuleId, TabId),
            Placement(OtherTabModuleId, SecondTabId),
        ];

        Result<PagedResult<ModuleListItemDto>> outcome = await harness.Service
            .ListModulesAsync(PortalId, new PagedRequest { PageSize = 0 }, null, false, CancellationToken.None);

        outcome.Value.Items.Should().HaveCount(2);
        outcome.Value.Items.Select(row => row.TabId).Should().Equal(new[] { TabId, SecondTabId });
        outcome.Value.Items.Should().OnlyContain(row => row.ModuleId == ModuleId);
    }

    /// <summary>
    /// A page filter restricts the rows to the placements on that page.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task ListModules_RestrictsRowsToTheRequestedPage()
    {
        Harness harness = Harness.Ready();
        harness.ModulePage = PagedResult<Module>.Unpaged([StoredModule()]);
        harness.PlacementsByModuleId[ModuleId] =
        [
            Placement(TabModuleId, TabId),
            Placement(OtherTabModuleId, SecondTabId),
        ];

        Result<PagedResult<ModuleListItemDto>> outcome = await harness.Service
            .ListModulesAsync(PortalId, new PagedRequest { PageSize = 0 }, SecondTabId, false, CancellationToken.None);

        outcome.Value.Items.Should().ContainSingle().Which.TabId.Should().Be(SecondTabId);
    }

    /// <summary>
    /// Placements are ordered by page, then by the order within the pane, then by identifier, so the list
    /// reads in the order an operator sees on the page itself.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task ListModules_OrdersPlacementsByPageThenOrderThenIdentifier()
    {
        Harness harness = Harness.Ready();
        harness.ModulePage = PagedResult<Module>.Unpaged([StoredModule()]);
        TabModule later = Placement(9, SecondTabId);
        TabModule secondOnFirstPage = Placement(4, TabId);
        secondOnFirstPage.ModuleOrder = 4;
        TabModule firstOnFirstPage = Placement(7, TabId);
        firstOnFirstPage.ModuleOrder = 2;
        harness.PlacementsByModuleId[ModuleId] = [later, secondOnFirstPage, firstOnFirstPage];

        Result<PagedResult<ModuleListItemDto>> outcome = await harness.Service
            .ListModulesAsync(PortalId, new PagedRequest { PageSize = 0 }, null, false, CancellationToken.None);

        outcome.Value.Items.Select(row => row.TabModuleId).Should().Equal(new[] { 7, 4, 9 });
    }

    /// <summary>
    /// The definition's display name comes from the tenant's catalogue.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task ListModules_NamesTheDefinitionFromTheCatalogue()
    {
        Harness harness = Harness.Ready();
        harness.ModulePage = PagedResult<Module>.Unpaged([StoredModule()]);
        harness.PlacementsByModuleId[ModuleId] = [Placement(TabModuleId, TabId)];

        Result<PagedResult<ModuleListItemDto>> outcome = await harness.Service
            .ListModulesAsync(PortalId, new PagedRequest { PageSize = 0 }, null, false, CancellationToken.None);

        outcome.Value.Items.Should().ContainSingle().Which.FriendlyName.Should().Be(FriendlyName);
    }

    /// <summary>
    /// A definition the tenant's catalogue does not cover still receives a name when the row itself carries
    /// the definition, so a premium module withdrawn from the tenant is not rendered nameless.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task ListModules_FallsBackToTheLoadedDefinitionWhenTheCatalogueDoesNotCoverIt()
    {
        Harness harness = Harness.Ready();
        Module module = StoredModule();
        module.ModuleDefinitionId = 999;
        module.ModuleDefinition = new ModuleDefinition
        {
            ModuleDefinitionId = 999,
            FriendlyName = "Withdrawn Module",
            DesktopModuleId = DesktopModuleId,
        };
        harness.ModulePage = PagedResult<Module>.Unpaged([module]);
        harness.PlacementsByModuleId[ModuleId] = [Placement(TabModuleId, TabId)];

        Result<PagedResult<ModuleListItemDto>> outcome = await harness.Service
            .ListModulesAsync(PortalId, new PagedRequest { PageSize = 0 }, null, false, CancellationToken.None);

        outcome.Value.Items.Should().ContainSingle().Which.FriendlyName.Should().Be("Withdrawn Module");
    }

    /// <summary>
    /// A request that asks for no page size receives an unpaged answer whose total is the row count.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task ListModules_ReportsAnUnpagedAnswerWhenNoPageSizeWasAsked()
    {
        Harness harness = Harness.Ready();
        harness.ModulePage = PagedResult<Module>.Unpaged([StoredModule()]);
        harness.PlacementsByModuleId[ModuleId] =
        [
            Placement(TabModuleId, TabId),
            Placement(OtherTabModuleId, SecondTabId),
        ];

        Result<PagedResult<ModuleListItemDto>> outcome = await harness.Service
            .ListModulesAsync(PortalId, new PagedRequest { PageSize = 0 }, null, false, CancellationToken.None);

        outcome.Value.IsUnpaged.Should().BeTrue();
        outcome.Value.TotalCount.Should().Be(2);
    }

    /// <summary>
    /// A paged answer keeps the module store's total, which counts modules rather than placements.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task ListModules_KeepsTheStoresTotalWhenAPageWasAsked()
    {
        Harness harness = Harness.Ready();
        harness.ModulePage = PagedResult<Module>.Create([StoredModule()], totalCount: 31, pageIndex: 1, pageSize: 10);
        harness.PlacementsByModuleId[ModuleId] = [Placement(TabModuleId, TabId)];

        Result<PagedResult<ModuleListItemDto>> outcome = await harness.Service
            .ListModulesAsync(PortalId, new PagedRequest { PageIndex = 1, PageSize = 10 }, null, false, CancellationToken.None);

        outcome.Value.TotalCount.Should().Be(31);
        outcome.Value.PageIndex.Should().Be(1);
        outcome.Value.PageSize.Should().Be(10);
    }

    /// <summary>
    /// A module that does not exist is reported as absent rather than as a failure.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task GetModule_ReportsAbsenceForAnUnknownModule()
    {
        Harness harness = Harness.Ready();
        harness.LookupModule = null;

        Result<ModuleDetailDto?> outcome = await harness.Service
            .GetModuleAsync(PortalId, ModuleId, null, CancellationToken.None);

        outcome.IsSuccess.Should().BeTrue();
        outcome.Value.Should().BeNull();
    }

    /// <summary>
    /// A module belonging to another tenant is indistinguishable from one that does not exist.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task GetModule_ReportsAbsenceForAModuleOfAnotherTenant()
    {
        Harness harness = Harness.Ready();
        harness.LookupModule!.PortalId = OtherPortalId;

        Result<ModuleDetailDto?> outcome = await harness.Service
            .GetModuleAsync(PortalId, ModuleId, null, CancellationToken.None);

        outcome.IsSuccess.Should().BeTrue();
        outcome.Value.Should().BeNull();
    }

    /// <summary>
    /// The tenant is never probed separately, because the module row already names the tenant it belongs to.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task GetModule_DoesNotProbeTheTenant()
    {
        Harness harness = Harness.Ready();

        await harness.Service.GetModuleAsync(PortalId, ModuleId, null, CancellationToken.None);

        harness.Portals.Verify(
            p => p.ExistsAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    /// <summary>
    /// A named placement that belongs to a different module is refused rather than quietly replaced by the
    /// module's own first placement.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task GetModule_RefusesAPlacementThatBelongsToAnotherModule()
    {
        Harness harness = Harness.Ready();
        TabModule foreign = Placement(OtherTabModuleId, SecondTabId);
        foreign.ModuleId = OtherModuleId;
        harness.PlacementsById[OtherTabModuleId] = foreign;

        Result<ModuleDetailDto?> outcome = await harness.Service
            .GetModuleAsync(PortalId, ModuleId, OtherTabModuleId, CancellationToken.None);

        outcome.IsFailure.Should().BeTrue();
        outcome.Reason!.Code.Should().Be(PlacementNotFoundCode);
        outcome.Reason!.Message.Should()
            .Be($"Placement {OtherTabModuleId} does not belong to module {ModuleId}.");
    }

    /// <summary>
    /// When no placement is named the lowest-numbered one is chosen, which is the one the store created
    /// first.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task GetModule_ChoosesTheLowestPlacementWhenNoneIsNamed()
    {
        Harness harness = Harness.Ready();
        Module module = StoredModule();
        module.TabModules.Add(Placement(9, SecondTabId));
        module.TabModules.Add(Placement(TabModuleId, TabId));
        harness.LookupModule = module;

        Result<ModuleDetailDto?> outcome = await harness.Service
            .GetModuleAsync(PortalId, ModuleId, null, CancellationToken.None);

        outcome.Value!.TabModuleId.Should().Be(TabModuleId);
        outcome.Value!.TabId.Should().Be(TabId);
    }

    /// <summary>
    /// A module placed on no page at all is reported as absent, because a module row with no placement has
    /// no appearance to describe.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task GetModule_ReportsAbsenceWhenTheModuleSitsNowhere()
    {
        Harness harness = Harness.Ready();
        harness.LookupModule = StoredModule();
        harness.PlacementsByModuleId.Clear();

        Result<ModuleDetailDto?> outcome = await harness.Service
            .GetModuleAsync(PortalId, ModuleId, null, CancellationToken.None);

        outcome.IsSuccess.Should().BeTrue();
        outcome.Value.Should().BeNull();
    }

    /// <summary>
    /// Placements are read from the store when the loaded row carries none, so the answer does not depend on
    /// whether the caller's read included them.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task GetModule_ReadsPlacementsFromTheStoreWhenTheyWereNotLoaded()
    {
        Harness harness = Harness.Ready();
        harness.LookupModule = StoredModule();
        harness.PlacementsByModuleId[ModuleId] = [Placement(TabModuleId, TabId)];

        Result<ModuleDetailDto?> outcome = await harness.Service
            .GetModuleAsync(PortalId, ModuleId, null, CancellationToken.None);

        outcome.Value!.TabModuleId.Should().Be(TabModuleId);
        harness.Modules.Verify(
            m => m.ListPlacementsAsync(ModuleId, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    /// <summary>
    /// The detail projection names the definition and carries both the module and placement facts.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task GetModule_NamesTheDefinition()
    {
        Harness harness = Harness.Ready();

        Result<ModuleDetailDto?> outcome = await harness.Service
            .GetModuleAsync(PortalId, ModuleId, null, CancellationToken.None);

        ModuleDetailDto detail = outcome.Value!;
        detail.FriendlyName.Should().Be(FriendlyName);
        detail.ModuleId.Should().Be(ModuleId);
        detail.PortalId.Should().Be(PortalId);
        detail.ModuleTitle.Should().Be(ModuleTitle);
        detail.TabModuleId.Should().Be(
            TabModuleId,
            "the detail projection identifies the placement it describes, the pane itself having moved to "
            + "the settings projection that owns the placement scope");
    }

    /// <summary>
    /// Creating a module requires a request.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task CreateModule_RequiresARequest()
    {
        Harness harness = Harness.Ready();

        await Assert.ThrowsAsync<ArgumentNullException>(
            () => harness.Service.CreateModuleAsync(PortalId, null!, CancellationToken.None));
    }

    /// <summary>
    /// A schedule that ends before it starts is refused before anything is read.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task CreateModule_RefusesAScheduleThatEndsBeforeItStarts()
    {
        Harness harness = Harness.Ready();
        CreateModuleRequest request = ValidCreateRequest();
        request.StartDate = new DateTime(2026, 8, 2, 0, 0, 0, DateTimeKind.Utc);
        request.EndDate = new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc);

        Result<ModuleDetailDto> outcome = await harness.Service
            .CreateModuleAsync(PortalId, request, CancellationToken.None);

        outcome.IsFailure.Should().BeTrue();
        outcome.Reason!.Code.Should().Be(RequestInvalidCode);
        outcome.Reason!.Message.Should().Be("The start date must not be later than the end date.");
        harness.Definitions.Verify(
            d => d.ListAsync(It.IsAny<int?>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    /// <summary>
    /// A schedule with only one bound is accepted, because an open-ended appearance is legitimate.
    /// </summary>
    /// <param name="hasStart">Whether the request carries a start date.</param>
    /// <param name="hasEnd">Whether the request carries an end date.</param>
    /// <returns>A task representing the assertion.</returns>
    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public async Task CreateModule_AcceptsAScheduleWithOnlyOneBound(bool hasStart, bool hasEnd)
    {
        Harness harness = Harness.Ready();
        CreateModuleRequest request = ValidCreateRequest();
        request.StartDate = hasStart ? new DateTime(2026, 8, 2, 0, 0, 0, DateTimeKind.Utc) : null;
        request.EndDate = hasEnd ? new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc) : null;

        Result<ModuleDetailDto> outcome = await harness.Service
            .CreateModuleAsync(PortalId, request, CancellationToken.None);

        outcome.IsSuccess.Should().BeTrue();
    }

    /// <summary>
    /// A definition the tenant's catalogue does not offer is refused, which is how a premium module withheld
    /// from the tenant is kept out.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task CreateModule_RefusesADefinitionNotAvailableToTheTenant()
    {
        Harness harness = Harness.Ready();
        harness.DefinitionCatalogue.Clear();

        Result<ModuleDetailDto> outcome = await harness.Service
            .CreateModuleAsync(PortalId, ValidCreateRequest(), CancellationToken.None);

        outcome.IsFailure.Should().BeTrue();
        outcome.Reason!.Code.Should().Be(DefinitionNotFoundCode);
        outcome.Reason!.Message.Should().Be(
            $"Module definition {ModuleDefinitionId} does not exist or is not available to portal {PortalId}.");
    }

    /// <summary>
    /// A page belonging to another tenant cannot receive a module, which is what keeps one tenant from
    /// placing content on another's pages.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task CreateModule_RefusesAPageOfAnotherTenant()
    {
        Harness harness = Harness.Ready();
        harness.TabsById[TabId] = new Tab { TabId = TabId, PortalId = OtherPortalId, TabName = "Foreign" };

        Result<ModuleDetailDto> outcome = await harness.Service
            .CreateModuleAsync(PortalId, ValidCreateRequest(), CancellationToken.None);

        outcome.IsFailure.Should().BeTrue();
        outcome.Reason!.Code.Should().Be(TabNotFoundCode);
        outcome.Reason!.Message.Should().Be($"Page {TabId} does not belong to portal {PortalId}.");
    }

    /// <summary>
    /// A page that does not exist is refused with the same reason.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task CreateModule_RefusesAnUnknownPage()
    {
        Harness harness = Harness.Ready();
        harness.TabsById.Clear();

        Result<ModuleDetailDto> outcome = await harness.Service
            .CreateModuleAsync(PortalId, ValidCreateRequest(), CancellationToken.None);

        outcome.IsFailure.Should().BeTrue();
        outcome.Reason!.Code.Should().Be(TabNotFoundCode);
        harness.AddedModules.Should().BeEmpty();
    }

    /// <summary>
    /// The submitted shape reaches both rows, with the tenant taken from the route.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task CreateModule_StoresTheSubmittedShape()
    {
        Harness harness = Harness.Ready();
        CreateModuleRequest request = ValidCreateRequest();
        request.ModuleTitle = "Announcements";
        request.PaneName = "LeftPane";
        request.ModuleOrder = 4;
        request.InheritViewPermissions = false;
        request.Visibility = ModuleVisibility.Minimized;
        request.DisplayTitle = false;
        request.CacheTime = 120;
        request.IconFile = "icon.gif";
        request.Header = "<h1>";
        request.Footer = "</h1>";

        Result<ModuleDetailDto> outcome = await harness.Service
            .CreateModuleAsync(PortalId, request, CancellationToken.None);

        outcome.IsSuccess.Should().BeTrue();
        Module stored = harness.AddedModules.Should().ContainSingle().Which;
        stored.PortalId.Should().Be(PortalId);
        stored.ModuleDefinitionId.Should().Be(ModuleDefinitionId);
        stored.ModuleTitle.Should().Be("Announcements");
        stored.InheritViewPermissions.Should().BeFalse();
        stored.IsDeleted.Should().BeFalse();
        stored.Header.Should().Be("<h1>");
        stored.Footer.Should().Be("</h1>");

        TabModule placement = stored.TabModules.Should().ContainSingle().Which;
        placement.TabId.Should().Be(TabId);
        placement.PaneName.Should().Be("LeftPane");
        placement.ModuleOrder.Should().Be(4);
        placement.CacheTime.Should().Be(120);
        placement.IconFile.Should().Be("icon.gif");
        placement.Visibility.Should().Be(ModuleVisibility.Minimized);
        placement.DisplayTitle.Should().BeFalse();
    }

    /// <summary>
    /// A module that asks for no cache lifetime inherits the definition's default.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task CreateModule_TakesTheDefinitionsDefaultCacheTimeWhenNoneWasAsked()
    {
        Harness harness = Harness.Ready();
        harness.DefinitionCatalogue[0].DefaultCacheTime = 300;
        CreateModuleRequest request = ValidCreateRequest();
        request.CacheTime = null;

        await harness.Service.CreateModuleAsync(PortalId, request, CancellationToken.None);

        harness.AddedModules.Single().TabModules.Single().CacheTime.Should().Be(300);
    }

    /// <summary>
    /// A negative cache lifetime is clamped to nothing rather than stored, because the column records
    /// seconds and a negative interval has no meaning.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task CreateModule_ClampsANegativeCacheTime()
    {
        Harness harness = Harness.Ready();
        CreateModuleRequest request = ValidCreateRequest();
        request.CacheTime = -60;

        await harness.Service.CreateModuleAsync(PortalId, request, CancellationToken.None);

        harness.AddedModules.Single().TabModules.Single().CacheTime.Should().Be(0);
    }

    /// <summary>
    /// A blank pane name falls back to the default content pane, which is the pane every shipped skin
    /// declares.
    /// </summary>
    /// <param name="submitted">The blank pane name to submit.</param>
    /// <returns>A task representing the assertion.</returns>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task CreateModule_SubstitutesTheDefaultPaneWhenNoneWasNamed(string submitted)
    {
        Harness harness = Harness.Ready();
        CreateModuleRequest request = ValidCreateRequest();
        request.PaneName = submitted;

        await harness.Service.CreateModuleAsync(PortalId, request, CancellationToken.None);

        harness.AddedModules.Single().TabModules.Single().PaneName.Should().Be(DefaultPaneName);
    }

    /// <summary>
    /// A module that was not asked to appear everywhere is placed on the requested page only.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task CreateModule_PlacesOnOneRequestedPageOnly()
    {
        Harness harness = Harness.Ready();
        CreateModuleRequest request = ValidCreateRequest();
        request.AllTabs = false;

        await harness.Service.CreateModuleAsync(PortalId, request, CancellationToken.None);

        harness.AddedModules.Single().TabModules.Should().ContainSingle().Which.TabId.Should().Be(TabId);
        harness.InvalidatedTabIds.Should().Equal(new[] { TabId });
    }

    /// <summary>
    /// A module asked to appear everywhere receives a placement on every content page, and the page it was
    /// created on is not duplicated.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task CreateModule_PlacesOnEveryContentPageWhenAskedForAllPages()
    {
        Harness harness = Harness.Ready();
        CreateModuleRequest request = ValidCreateRequest();
        request.AllTabs = true;

        await harness.Service.CreateModuleAsync(PortalId, request, CancellationToken.None);

        Module stored = harness.AddedModules.Should().ContainSingle().Which;
        stored.TabModules.Select(placement => placement.TabId).Should().BeEquivalentTo(new[] { TabId, SecondTabId });
        harness.InvalidatedTabIds.Should().BeEquivalentTo(new[] { TabId, SecondTabId });
    }

    /// <summary>
    /// Administrative pages and their children are skipped when a module is placed everywhere, so a content
    /// module does not appear inside the administration area.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task CreateModule_SkipsAdministrativePagesWhenPlacingEverywhere()
    {
        Harness harness = Harness.Ready();
        CreateModuleRequest request = ValidCreateRequest();
        request.AllTabs = true;

        await harness.Service.CreateModuleAsync(PortalId, request, CancellationToken.None);

        Module stored = harness.AddedModules.Single();
        stored.TabModules.Select(placement => placement.TabId).Should().NotContain(AdminTabId);
        stored.TabModules.Select(placement => placement.TabId).Should().NotContain(AdminChildTabId);
    }

    /// <summary>
    /// The module and every placement commit together, once.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task CreateModule_CommitsOnceAndDiscardsEveryAffectedPage()
    {
        Harness harness = Harness.Ready();
        CreateModuleRequest request = ValidCreateRequest();
        request.AllTabs = true;

        await harness.Service.CreateModuleAsync(PortalId, request, CancellationToken.None);

        harness.UnitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
        harness.InvalidatedTabIds.Should().HaveCount(2);
    }

    /// <summary>
    /// The answer describes the placement on the page the caller named and names the definition.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task CreateModule_NamesTheDefinitionInTheAnswer()
    {
        Harness harness = Harness.Ready();

        Result<ModuleDetailDto> outcome = await harness.Service
            .CreateModuleAsync(PortalId, ValidCreateRequest(), CancellationToken.None);

        outcome.Value.FriendlyName.Should().Be(FriendlyName);
        outcome.Value.TabId.Should().Be(TabId);
    }

    /// <summary>
    /// Updating a module requires a request.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task UpdateModule_RequiresARequest()
    {
        Harness harness = Harness.Ready();

        await Assert.ThrowsAsync<ArgumentNullException>(
            () => harness.Service.UpdateModuleAsync(PortalId, ModuleId, null!, CancellationToken.None));
    }

    /// <summary>
    /// A schedule that ends before it starts is refused before the module is read.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task UpdateModule_RefusesAScheduleThatEndsBeforeItStarts()
    {
        Harness harness = Harness.Ready();
        UpdateModuleRequest request = ValidUpdateRequest();
        request.StartDate = new DateTime(2026, 8, 2, 0, 0, 0, DateTimeKind.Utc);
        request.EndDate = new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc);

        Result<ModuleDetailDto?> outcome = await harness.Service
            .UpdateModuleAsync(PortalId, ModuleId, request, CancellationToken.None);

        outcome.IsFailure.Should().BeTrue();
        outcome.Reason!.Code.Should().Be(RequestInvalidCode);
        harness.Modules.Verify(
            m => m.GetAsync(It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    /// <summary>
    /// An unknown module, or one of another tenant, is reported as absent.
    /// </summary>
    /// <param name="moduleMissing">Whether the module is missing rather than foreign.</param>
    /// <returns>A task representing the assertion.</returns>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task UpdateModule_ReportsAbsenceForAModuleItCannotReach(bool moduleMissing)
    {
        Harness harness = Harness.Ready();
        if (moduleMissing)
        {
            harness.LookupModule = null;
        }
        else
        {
            harness.LookupModule!.PortalId = OtherPortalId;
        }

        Result<ModuleDetailDto?> outcome = await harness.Service
            .UpdateModuleAsync(PortalId, ModuleId, ValidUpdateRequest(), CancellationToken.None);

        outcome.IsSuccess.Should().BeTrue();
        outcome.Value.Should().BeNull();
    }

    /// <summary>
    /// A module placed on no page is reported as absent, because there is no placement to amend.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task UpdateModule_ReportsAbsenceWhenTheModuleSitsNowhere()
    {
        Harness harness = Harness.Ready();
        harness.LookupModule = StoredModule();
        harness.PlacementsByModuleId.Clear();

        Result<ModuleDetailDto?> outcome = await harness.Service
            .UpdateModuleAsync(PortalId, ModuleId, ValidUpdateRequest(), CancellationToken.None);

        outcome.IsSuccess.Should().BeTrue();
        outcome.Value.Should().BeNull();
        harness.UnitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    /// <summary>
    /// The submitted shape is applied to both the module row and the placement row.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task UpdateModule_AppliesTheSubmittedShapeToBothRows()
    {
        Harness harness = Harness.Ready();
        Module module = harness.LookupModule!;
        TabModule placement = module.TabModules.Single();

        UpdateModuleRequest request = ValidUpdateRequest();
        request.ModuleTitle = "Renamed";
        request.PaneName = "RightPane";
        request.ModuleOrder = 8;
        request.InheritViewPermissions = false;
        request.Visibility = ModuleVisibility.None;
        request.DisplayTitle = false;
        request.CacheTime = 45;
        request.IconFile = "changed.gif";
        request.Header = "head";
        request.Footer = "foot";

        Result<ModuleDetailDto?> outcome = await harness.Service
            .UpdateModuleAsync(PortalId, ModuleId, request, CancellationToken.None);

        outcome.IsSuccess.Should().BeTrue();
        module.ModuleTitle.Should().Be("Renamed");
        module.InheritViewPermissions.Should().BeFalse();
        module.Header.Should().Be("head");
        module.Footer.Should().Be("foot");
        placement.PaneName.Should().Be("RightPane");
        placement.ModuleOrder.Should().Be(8);
        placement.CacheTime.Should().Be(45);
        placement.IconFile.Should().Be("changed.gif");
        placement.Visibility.Should().Be(ModuleVisibility.None);
        placement.DisplayTitle.Should().BeFalse();
    }

    /// <summary>
    /// A request that names no cache lifetime keeps the stored one rather than resetting it.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task UpdateModule_KeepsTheStoredCacheTimeWhenNoneWasAsked()
    {
        Harness harness = Harness.Ready();
        TabModule placement = harness.LookupModule!.TabModules.Single();
        placement.CacheTime = 900;

        UpdateModuleRequest request = ValidUpdateRequest();
        request.CacheTime = null;

        await harness.Service.UpdateModuleAsync(PortalId, ModuleId, request, CancellationToken.None);

        placement.CacheTime.Should().Be(900);
    }

    /// <summary>
    /// An ordinary change carries no advisory, so the presence of one is a reliable signal that pages the
    /// caller did not name were touched.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task UpdateModule_ReportsNoWideEffectForAnOrdinaryChange()
    {
        Harness harness = Harness.Ready();

        Result<ModuleDetailDto?> outcome = await harness.Service
            .UpdateModuleAsync(PortalId, ModuleId, ValidUpdateRequest(), CancellationToken.None);

        outcome.IsSuccess.Should().BeTrue();
        outcome.Reason.Should().BeNull();
    }

    /// <summary>
    /// Newly asking for every page places the module on the remaining content pages and says how many.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task UpdateModule_PlacesOnFurtherPagesWhenAllPagesIsNewlyAsked()
    {
        Harness harness = Harness.Ready();
        harness.LookupModule!.AllTabs = false;
        UpdateModuleRequest request = ValidUpdateRequest();
        request.AllTabs = true;

        Result<ModuleDetailDto?> outcome = await harness.Service
            .UpdateModuleAsync(PortalId, ModuleId, request, CancellationToken.None);

        outcome.IsSuccess.Should().BeTrue();
        outcome.Reason!.Code.Should().Be(WideEffectCode);
        outcome.Reason!.Message.Should().Be("placed on 1 further page(s).");
        harness.AddedPlacements.Should().ContainSingle().Which.TabId.Should().Be(SecondTabId);
        harness.InvalidatedTabIds.Should().BeEquivalentTo(new[] { TabId, SecondTabId });
    }

    /// <summary>
    /// Withdrawing the every-page instruction removes the other placements and says how many.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task UpdateModule_WithdrawsFromOtherPagesWhenAllPagesIsWithdrawn()
    {
        Harness harness = Harness.Ready();
        Module module = harness.LookupModule!;
        module.AllTabs = true;
        TabModule stale = Placement(OtherTabModuleId, SecondTabId);
        harness.PlacementsByModuleId[ModuleId] = [module.TabModules.Single(), stale];

        UpdateModuleRequest request = ValidUpdateRequest();
        request.AllTabs = false;

        Result<ModuleDetailDto?> outcome = await harness.Service
            .UpdateModuleAsync(PortalId, ModuleId, request, CancellationToken.None);

        outcome.IsSuccess.Should().BeTrue();
        outcome.Reason!.Code.Should().Be(WideEffectCode);
        outcome.Reason!.Message.Should().Be("withdrawn from 1 further page(s).");
        harness.RemovedPlacements.Should().ContainSingle().Which.Should().BeSameAs(stale);
    }

    /// <summary>
    /// A module already placed everywhere reports no wide effect when the instruction is merely repeated.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task UpdateModule_ReportsNoWideEffectWhenAllPagesWasAlreadySet()
    {
        Harness harness = Harness.Ready();
        harness.LookupModule!.AllTabs = true;
        UpdateModuleRequest request = ValidUpdateRequest();
        request.AllTabs = true;

        Result<ModuleDetailDto?> outcome = await harness.Service
            .UpdateModuleAsync(PortalId, ModuleId, request, CancellationToken.None);

        outcome.Reason.Should().BeNull();
        harness.AddedPlacements.Should().BeEmpty();
        harness.RemovedPlacements.Should().BeEmpty();
    }

    /// <summary>
    /// Withdrawing a placement removes its settings too, so no orphaned setting row survives the placement
    /// it described.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task UpdateModule_RemovesPlacementSettingsWhenWithdrawingAPlacement()
    {
        Harness harness = Harness.Ready();
        Module module = harness.LookupModule!;
        module.AllTabs = true;
        TabModule stale = Placement(OtherTabModuleId, SecondTabId);
        harness.PlacementsByModuleId[ModuleId] = [module.TabModules.Single(), stale];
        harness.PlacementSettingsByTabModuleId[OtherTabModuleId] =
        [
            new TabModuleSetting { TabModuleId = OtherTabModuleId, SettingName = "colour", SettingValue = "red" },
        ];

        UpdateModuleRequest request = ValidUpdateRequest();
        request.AllTabs = false;

        await harness.Service.UpdateModuleAsync(PortalId, ModuleId, request, CancellationToken.None);

        harness.RemovedPlacementSettings.Should().ContainSingle().Which.SettingName.Should().Be("colour");
    }

    /// <summary>
    /// Naming a module as the tenant default records both identifiers against the tenant's site-settings
    /// instance, because that is where the legacy screen stored them.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task UpdateModule_RecordsThePortalDefaultAgainstTheSiteSettingsInstance()
    {
        Harness harness = Harness.Ready();
        harness.AddSiteSettingsInstance();

        UpdateModuleRequest request = ValidUpdateRequest();
        request.IsDefaultModule = true;

        Result<ModuleDetailDto?> outcome = await harness.Service
            .UpdateModuleAsync(PortalId, ModuleId, request, CancellationToken.None);

        outcome.IsSuccess.Should().BeTrue();
        outcome.Reason!.Message.Should().Be("named as the portal default module.");
        harness.AddedSettings.Should().HaveCount(2);
        harness.AddedSettings.Should().OnlyContain(setting => setting.ModuleId == OtherModuleId);
        harness.AddedSettings.Single(setting => setting.SettingName == "defaultmoduleid").SettingValue
            .Should().Be("0");
        harness.AddedSettings.Single(setting => setting.SettingName == "defaulttabid").SettingValue
            .Should().Be("5");
    }

    /// <summary>
    /// A tenant with no site-settings instance is told the default could not be recorded rather than being
    /// given a silent success.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task UpdateModule_ReportsThatNoPortalDefaultCouldBeRecordedWhenNoSiteSettingsInstanceExists()
    {
        Harness harness = Harness.Ready();

        UpdateModuleRequest request = ValidUpdateRequest();
        request.IsDefaultModule = true;

        Result<ModuleDetailDto?> outcome = await harness.Service
            .UpdateModuleAsync(PortalId, ModuleId, request, CancellationToken.None);

        outcome.IsSuccess.Should().BeTrue();
        outcome.Reason!.Message.Should().Be(
            $"could not be named as the portal default because portal {PortalId} has no \"{SiteSettingsDefinitionName}\" module instance.");
        harness.AddedSettings.Should().BeEmpty();
    }

    /// <summary>
    /// An existing default-module setting is overwritten rather than duplicated.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task UpdateModule_OverwritesAnExistingPortalDefaultSetting()
    {
        Harness harness = Harness.Ready();
        harness.AddSiteSettingsInstance();
        var stored = new ModuleSetting
        {
            ModuleId = OtherModuleId,
            SettingName = "DefaultModuleId",
            SettingValue = "77",
        };
        harness.SettingsByModuleId[OtherModuleId] = [stored];

        UpdateModuleRequest request = ValidUpdateRequest();
        request.IsDefaultModule = true;

        await harness.Service.UpdateModuleAsync(PortalId, ModuleId, request, CancellationToken.None);

        stored.SettingValue.Should().Be("0");
        harness.AddedSettings.Should().ContainSingle().Which.SettingName.Should().Be("defaulttabid");
    }

    /// <summary>
    /// Copying the appearance to every module writes the placement's presentation onto every other
    /// placement on a content page, skipping the source placement itself.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task UpdateModule_CopiesTheAppearanceToOtherPlacementsWhenAskedForAllModules()
    {
        Harness harness = Harness.Ready();
        Module module = harness.LookupModule!;
        TabModule source = module.TabModules.Single();
        TabModule target = Placement(OtherTabModuleId, SecondTabId);
        target.ModuleId = OtherModuleId;
        target.Alignment = "right";
        target.Visibility = ModuleVisibility.None;
        target.DisplayTitle = false;

        Module other = StoredModule();
        other.ModuleId = OtherModuleId;
        harness.ModulePage = PagedResult<Module>.Unpaged([module, other]);
        harness.PlacementsByModuleId[ModuleId] = [source];
        harness.PlacementsByModuleId[OtherModuleId] = [target];

        UpdateModuleRequest request = ValidUpdateRequest();
        request.AllModules = true;
        request.Visibility = ModuleVisibility.Minimized;
        request.IconFile = "shared.gif";
        request.DisplayTitle = true;

        Result<ModuleDetailDto?> outcome = await harness.Service
            .UpdateModuleAsync(PortalId, ModuleId, request, CancellationToken.None);

        outcome.Reason!.Message.Should().Be("appearance copied to 1 placement(s) on content pages.");
        target.Visibility.Should().Be(ModuleVisibility.Minimized);
        target.IconFile.Should().Be("shared.gif");
        target.DisplayTitle.Should().BeTrue();
        target.Alignment.Should().Be(source.Alignment);
    }

    /// <summary>
    /// A placement on an administrative page is not repainted, so copying an appearance across the site
    /// leaves the administration area alone.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task UpdateModule_DoesNotCopyTheAppearanceOntoAnAdministrativePage()
    {
        Harness harness = Harness.Ready();
        Module module = harness.LookupModule!;
        TabModule source = module.TabModules.Single();
        TabModule administrative = Placement(OtherTabModuleId, AdminChildTabId);
        administrative.ModuleId = OtherModuleId;
        administrative.IconFile = "untouched.gif";

        Module other = StoredModule();
        other.ModuleId = OtherModuleId;
        harness.ModulePage = PagedResult<Module>.Unpaged([module, other]);
        harness.PlacementsByModuleId[ModuleId] = [source];
        harness.PlacementsByModuleId[OtherModuleId] = [administrative];

        UpdateModuleRequest request = ValidUpdateRequest();
        request.AllModules = true;
        request.IconFile = "shared.gif";

        Result<ModuleDetailDto?> outcome = await harness.Service
            .UpdateModuleAsync(PortalId, ModuleId, request, CancellationToken.None);

        outcome.Reason!.Message.Should().Be("appearance copied to 0 placement(s) on content pages.");
        administrative.IconFile.Should().Be("untouched.gif");
    }

    /// <summary>
    /// Several wide effects in one request are reported as one advisory listing each of them.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task UpdateModule_JoinsSeveralWideEffectsIntoOneAdvisory()
    {
        Harness harness = Harness.Ready();
        harness.LookupModule!.AllTabs = false;
        harness.AddSiteSettingsInstance();

        UpdateModuleRequest request = ValidUpdateRequest();
        request.AllTabs = true;
        request.IsDefaultModule = true;

        Result<ModuleDetailDto?> outcome = await harness.Service
            .UpdateModuleAsync(PortalId, ModuleId, request, CancellationToken.None);

        outcome.Reason!.Code.Should().Be(WideEffectCode);
        outcome.Reason!.Message.Should()
            .Be("placed on 1 further page(s); named as the portal default module.");
    }

    /// <summary>
    /// The change commits once, however many pages it reached.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task UpdateModule_CommitsOnceAndDiscardsEveryAffectedPage()
    {
        Harness harness = Harness.Ready();
        harness.LookupModule!.AllTabs = false;
        UpdateModuleRequest request = ValidUpdateRequest();
        request.AllTabs = true;

        await harness.Service.UpdateModuleAsync(PortalId, ModuleId, request, CancellationToken.None);

        harness.UnitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
        harness.InvalidatedTabIds.Should().BeEquivalentTo(new[] { TabId, SecondTabId });
    }

    /// <summary>
    /// Deleting a module the tenant does not have is refused.
    /// </summary>
    /// <param name="moduleMissing">Whether the module is missing rather than foreign.</param>
    /// <returns>A task representing the assertion.</returns>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DeleteModule_RefusesAModuleItCannotReach(bool moduleMissing)
    {
        Harness harness = Harness.Ready();
        if (moduleMissing)
        {
            harness.LookupModule = null;
        }
        else
        {
            harness.LookupModule!.PortalId = OtherPortalId;
        }

        Result outcome = await harness.Service
            .DeleteModuleAsync(PortalId, ModuleId, null, CancellationToken.None);

        outcome.IsFailure.Should().BeTrue();
        outcome.Reason!.Code.Should().Be(NotFoundCode);
        outcome.Reason!.Message.Should()
            .Be($"Module {ModuleId} does not exist in portal {PortalId}.");
    }

    /// <summary>
    /// A named placement belonging to another module is refused with the placement reason.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task DeleteModule_RefusesAPlacementThatBelongsToAnotherModule()
    {
        Harness harness = Harness.Ready();
        TabModule foreign = Placement(OtherTabModuleId, SecondTabId);
        foreign.ModuleId = OtherModuleId;
        harness.PlacementsById[OtherTabModuleId] = foreign;

        Result outcome = await harness.Service
            .DeleteModuleAsync(PortalId, ModuleId, OtherTabModuleId, CancellationToken.None);

        outcome.IsFailure.Should().BeTrue();
        outcome.Reason!.Code.Should().Be(PlacementNotFoundCode);
        outcome.Reason!.Message.Should()
            .Be($"Placement {OtherTabModuleId} does not belong to module {ModuleId}.");
    }

    /// <summary>
    /// Naming one placement withdraws that placement and leaves the module row itself in place, so the
    /// module survives on the pages it still occupies.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task DeleteModule_WithdrawsOneNamedPlacementWithoutDeletingTheModule()
    {
        Harness harness = Harness.Ready();
        Module module = harness.LookupModule!;
        TabModule placement = module.TabModules.Single();

        Result outcome = await harness.Service
            .DeleteModuleAsync(PortalId, ModuleId, TabModuleId, CancellationToken.None);

        outcome.IsSuccess.Should().BeTrue();
        harness.RemovedPlacements.Should().ContainSingle().Which.Should().BeSameAs(placement);
        module.IsDeleted.Should().BeFalse();
        harness.InvalidatedTabIds.Should().Equal(new[] { TabId });
    }

    /// <summary>
    /// Withdrawing a placement removes its settings too.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task DeleteModule_RemovesThePlacementsSettingsToo()
    {
        Harness harness = Harness.Ready();
        harness.PlacementSettingsByTabModuleId[TabModuleId] =
        [
            new TabModuleSetting { TabModuleId = TabModuleId, SettingName = "colour", SettingValue = "red" },
            new TabModuleSetting { TabModuleId = TabModuleId, SettingName = "border", SettingValue = "1" },
        ];

        await harness.Service.DeleteModuleAsync(PortalId, ModuleId, TabModuleId, CancellationToken.None);

        harness.RemovedPlacementSettings.Should().HaveCount(2);
    }

    /// <summary>
    /// Naming no placement marks the module deleted rather than erasing it, which is what allows the recycle
    /// bin to restore it, and leaves every placement in place.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task DeleteModule_MarksTheModuleDeletedWhenNoPlacementIsNamed()
    {
        Harness harness = Harness.Ready();
        Module module = harness.LookupModule!;
        harness.PlacementsByModuleId[ModuleId] =
        [
            module.TabModules.Single(),
            Placement(OtherTabModuleId, SecondTabId),
        ];

        Result outcome = await harness.Service
            .DeleteModuleAsync(PortalId, ModuleId, null, CancellationToken.None);

        outcome.IsSuccess.Should().BeTrue();
        module.IsDeleted.Should().BeTrue();
        harness.RemovedPlacements.Should().BeEmpty();
        harness.InvalidatedTabIds.Should().BeEquivalentTo(new[] { TabId, SecondTabId });
        harness.UnitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    /// <summary>
    /// Reading settings for a module the tenant does not have reports absence.
    /// </summary>
    /// <param name="moduleMissing">Whether the module is missing rather than foreign.</param>
    /// <returns>A task representing the assertion.</returns>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task GetModuleSettings_ReportsAbsenceForAModuleItCannotReach(bool moduleMissing)
    {
        Harness harness = Harness.Ready();
        if (moduleMissing)
        {
            harness.LookupModule = null;
        }
        else
        {
            harness.LookupModule!.PortalId = OtherPortalId;
        }

        Result<ModuleSettingsDto?> outcome = await harness.Service
            .GetModuleSettingsAsync(PortalId, ModuleId, null, CancellationToken.None);

        outcome.IsSuccess.Should().BeTrue();
        outcome.Value.Should().BeNull();
    }

    /// <summary>
    /// A named placement of another module yields absence rather than a refusal here, because the reader has
    /// nothing to report rather than an instruction to reject.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task GetModuleSettings_ReportsAbsenceForAPlacementOfAnotherModule()
    {
        Harness harness = Harness.Ready();
        TabModule foreign = Placement(OtherTabModuleId, SecondTabId);
        foreign.ModuleId = OtherModuleId;
        harness.PlacementsById[OtherTabModuleId] = foreign;

        Result<ModuleSettingsDto?> outcome = await harness.Service
            .GetModuleSettingsAsync(PortalId, ModuleId, OtherTabModuleId, CancellationToken.None);

        outcome.IsSuccess.Should().BeTrue();
        outcome.Value.Should().BeNull();
    }

    /// <summary>
    /// Both scopes of settings are projected, keyed by name.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task GetModuleSettings_ProjectsBothScopes()
    {
        Harness harness = Harness.Ready();
        harness.SettingsByModuleId[ModuleId] =
        [
            new ModuleSetting { ModuleId = ModuleId, SettingName = "editor", SettingValue = "rich" },
        ];
        harness.PlacementSettingsByTabModuleId[TabModuleId] =
        [
            new TabModuleSetting { TabModuleId = TabModuleId, SettingName = "colour", SettingValue = "red" },
        ];

        Result<ModuleSettingsDto?> outcome = await harness.Service
            .GetModuleSettingsAsync(PortalId, ModuleId, null, CancellationToken.None);

        ModuleSettingsDto settings = outcome.Value!;
        settings.ModuleId.Should().Be(ModuleId);
        settings.TabModuleId.Should().Be(TabModuleId);
        settings.ModuleSettings.Should().ContainKey("editor").WhoseValue.Should().Be("rich");
        settings.TabModuleSettings.Should().ContainKey("colour").WhoseValue.Should().Be("red");
    }

    /// <summary>
    /// Storing settings requires both maps, even when one of them is to be left empty.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task UpdateModuleSettings_RequiresBothMaps()
    {
        Harness harness = Harness.Ready();
        var empty = new Dictionary<string, string>();

        await Assert.ThrowsAsync<ArgumentNullException>(
            () => harness.Service.UpdateModuleSettingsAsync(PortalId, ModuleId, null, null!, empty, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentNullException>(
            () => harness.Service.UpdateModuleSettingsAsync(PortalId, ModuleId, null, empty, null!, CancellationToken.None));
    }

    /// <summary>
    /// Storing settings for a module the tenant does not have is refused.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task UpdateModuleSettings_RefusesAnUnknownModule()
    {
        Harness harness = Harness.Ready();
        harness.LookupModule = null;

        Result outcome = await harness.Service.UpdateModuleSettingsAsync(
            PortalId,
            ModuleId,
            null,
            new Dictionary<string, string>(),
            new Dictionary<string, string>(),
            CancellationToken.None);

        outcome.IsFailure.Should().BeTrue();
        outcome.Reason!.Code.Should().Be(NotFoundCode);
    }

    /// <summary>
    /// A malformed setting is refused with wording that names the offending scope and, where relevant, the
    /// offending name.
    /// </summary>
    /// <param name="scope">Which of the two maps to spoil.</param>
    /// <param name="violation">The kind of malformation to introduce.</param>
    /// <param name="expectedMessage">The message the service is measured to report.</param>
    /// <returns>A task representing the assertion.</returns>
    [Theory]
    [InlineData("module", "blank-name", "A module setting name must not be blank.")]
    [InlineData("module", "whitespace-name", "A module setting name must not be blank.")]
    [InlineData("placement", "blank-name", "A placement setting name must not be blank.")]
    [InlineData("module", "long-name", "The module setting name \"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\" exceeds 50 characters.")]
    [InlineData("placement", "long-name", "The placement setting name \"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\" exceeds 50 characters.")]
    [InlineData("module", "long-value", "The value of the module setting \"editor\" exceeds 256 characters.")]
    [InlineData("placement", "long-value", "The value of the placement setting \"editor\" exceeds 2000 characters.")]
    public async Task UpdateModuleSettings_RefusesAMalformedSetting(
        string scope,
        string violation,
        string expectedMessage)
    {
        Harness harness = Harness.Ready();
        var moduleSettings = new Dictionary<string, string>();
        var placementSettings = new Dictionary<string, string>();
        Dictionary<string, string> target = scope == "module" ? moduleSettings : placementSettings;

        switch (violation)
        {
            case "blank-name":
                target[string.Empty] = "value";
                break;
            case "whitespace-name":
                target["   "] = "value";
                break;
            case "long-name":
                target[new string('a', 51)] = "value";
                break;
            default:
                target["editor"] = new string('v', scope == "module" ? 257 : 2001);
                break;
        }

        Result outcome = await harness.Service.UpdateModuleSettingsAsync(
            PortalId,
            ModuleId,
            null,
            moduleSettings,
            placementSettings,
            CancellationToken.None);

        outcome.IsFailure.Should().BeTrue();
        outcome.Reason!.Code.Should().Be(SettingInvalidCode);
        outcome.Reason!.Message.Should().Be(expectedMessage);
        harness.UnitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    /// <summary>
    /// A named placement of another module is refused here with the module reason rather than the placement
    /// reason, which is the wording this member is measured to use.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task UpdateModuleSettings_RefusesAPlacementOfAnotherModule()
    {
        Harness harness = Harness.Ready();
        TabModule foreign = Placement(OtherTabModuleId, SecondTabId);
        foreign.ModuleId = OtherModuleId;
        harness.PlacementsById[OtherTabModuleId] = foreign;

        Result outcome = await harness.Service.UpdateModuleSettingsAsync(
            PortalId,
            ModuleId,
            OtherTabModuleId,
            new Dictionary<string, string>(),
            new Dictionary<string, string>(),
            CancellationToken.None);

        outcome.IsFailure.Should().BeTrue();
        outcome.Reason!.Code.Should().Be(NotFoundCode);
        outcome.Reason!.Message.Should()
            .Be($"Placement {OtherTabModuleId} does not belong to module {ModuleId}.");
    }

    /// <summary>
    /// Placement-scoped settings cannot be stored for a module that sits on no page, because there is no
    /// row for them to belong to.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task UpdateModuleSettings_RefusesPlacementSettingsWhenTheModuleSitsNowhere()
    {
        Harness harness = Harness.Ready();
        harness.LookupModule = StoredModule();
        harness.PlacementsByModuleId.Clear();

        Result outcome = await harness.Service.UpdateModuleSettingsAsync(
            PortalId,
            ModuleId,
            null,
            new Dictionary<string, string>(),
            new Dictionary<string, string> { ["colour"] = "red" },
            CancellationToken.None);

        outcome.IsFailure.Should().BeTrue();
        outcome.Reason!.Code.Should().Be(SettingInvalidCode);
        outcome.Reason!.Message.Should().Be(
            $"Module {ModuleId} is not placed on any page, so placement-scoped settings cannot be stored.");
    }

    /// <summary>
    /// Module-scoped settings are still accepted for a module that sits on no page.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task UpdateModuleSettings_AcceptsModuleSettingsWhenTheModuleSitsNowhere()
    {
        Harness harness = Harness.Ready();
        harness.LookupModule = StoredModule();
        harness.PlacementsByModuleId.Clear();

        Result outcome = await harness.Service.UpdateModuleSettingsAsync(
            PortalId,
            ModuleId,
            null,
            new Dictionary<string, string> { ["editor"] = "rich" },
            new Dictionary<string, string>(),
            CancellationToken.None);

        outcome.IsSuccess.Should().BeTrue();
        harness.AddedSettings.Should().ContainSingle().Which.SettingName.Should().Be("editor");
    }

    /// <summary>
    /// Reconciliation is a replacement rather than a merge: a stored name the caller omitted is removed, a
    /// changed value is amended, an unchanged value is left alone and a new name is added.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task UpdateModuleSettings_ReplacesRatherThanMergesTheStoredSettings()
    {
        Harness harness = Harness.Ready();
        var omitted = new ModuleSetting { ModuleId = ModuleId, SettingName = "gone", SettingValue = "1" };
        var changed = new ModuleSetting { ModuleId = ModuleId, SettingName = "editor", SettingValue = "plain" };
        var unchanged = new ModuleSetting { ModuleId = ModuleId, SettingName = "width", SettingValue = "400" };
        harness.SettingsByModuleId[ModuleId] = [omitted, changed, unchanged];

        Result outcome = await harness.Service.UpdateModuleSettingsAsync(
            PortalId,
            ModuleId,
            null,
            new Dictionary<string, string>
            {
                ["editor"] = "rich",
                ["width"] = "400",
                ["added"] = "yes",
            },
            new Dictionary<string, string>(),
            CancellationToken.None);

        outcome.IsSuccess.Should().BeTrue();
        harness.RemovedSettings.Should().ContainSingle().Which.Should().BeSameAs(omitted);
        changed.SettingValue.Should().Be("rich");
        unchanged.SettingValue.Should().Be("400");
        harness.AddedSettings.Should().ContainSingle().Which.SettingName.Should().Be("added");
    }

    /// <summary>
    /// A submitted name matches a stored one without regard to case, so re-submitting a setting under a
    /// different capitalisation amends it rather than adding a second row.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task UpdateModuleSettings_MatchesStoredNamesWithoutRegardToCase()
    {
        Harness harness = Harness.Ready();
        var stored = new ModuleSetting { ModuleId = ModuleId, SettingName = "Editor", SettingValue = "plain" };
        harness.SettingsByModuleId[ModuleId] = [stored];

        await harness.Service.UpdateModuleSettingsAsync(
            PortalId,
            ModuleId,
            null,
            new Dictionary<string, string> { ["editor"] = "rich" },
            new Dictionary<string, string>(),
            CancellationToken.None);

        stored.SettingValue.Should().Be("rich");
        harness.AddedSettings.Should().BeEmpty();
        harness.RemovedSettings.Should().BeEmpty();
    }

    /// <summary>
    /// Placement-scoped settings are reconciled against the resolved placement in the same way.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task UpdateModuleSettings_ReconcilesThePlacementScopeToo()
    {
        Harness harness = Harness.Ready();
        var omitted = new TabModuleSetting
        {
            TabModuleId = TabModuleId,
            SettingName = "gone",
            SettingValue = "1",
        };
        harness.PlacementSettingsByTabModuleId[TabModuleId] = [omitted];

        await harness.Service.UpdateModuleSettingsAsync(
            PortalId,
            ModuleId,
            null,
            new Dictionary<string, string>(),
            new Dictionary<string, string> { ["colour"] = "red" },
            CancellationToken.None);

        harness.RemovedPlacementSettings.Should().ContainSingle().Which.Should().BeSameAs(omitted);
        TabModuleSetting added = harness.AddedPlacementSettings.Should().ContainSingle().Which;
        added.TabModuleId.Should().Be(TabModuleId);
        added.SettingName.Should().Be("colour");
        added.SettingValue.Should().Be("red");
    }

    /// <summary>
    /// Storing settings discards the cached module list for the placement's page, and commits once.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task UpdateModuleSettings_DiscardsThePlacementsPage()
    {
        Harness harness = Harness.Ready();

        await harness.Service.UpdateModuleSettingsAsync(
            PortalId,
            ModuleId,
            TabModuleId,
            new Dictionary<string, string>(),
            new Dictionary<string, string>(),
            CancellationToken.None);

        harness.InvalidatedTabIds.Should().Equal(new[] { TabId });
        harness.UnitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    /// <summary>
    /// The definition catalogue is read through the cache under a tenant-keyed name, with a lifetime scaled
    /// by the configured performance multiplier.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task ListModuleDefinitions_ReadsThroughTheCacheWithTheTenantKeyedName()
    {
        Harness harness = Harness.Ready();

        Result<IReadOnlyList<ModuleDefinitionDto>> outcome = await harness.Service
            .ListModuleDefinitionsAsync(PortalId, CancellationToken.None);

        outcome.IsSuccess.Should().BeTrue();
        harness.CacheKey.Should().Be($"ModuleDefinitions{PortalId}");
        harness.CacheExpiration.Should().Be(TimeSpan.FromMinutes(60));
    }

    /// <summary>
    /// A different multiplier changes the lifetime proportionally, so caching aggressiveness is a
    /// configuration decision rather than a code one.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task ListModuleDefinitions_ScalesTheCacheLifetimeByThePerformanceMultiplier()
    {
        Harness harness = Harness.Ready();
        harness.Caching.PerformanceMultiplier = 1;

        await harness.Service.ListModuleDefinitionsAsync(PortalId, CancellationToken.None);

        harness.CacheExpiration.Should().Be(TimeSpan.FromMinutes(20));
    }

    /// <summary>
    /// A multiplier of nothing disables caching entirely and reads straight through, rather than caching for
    /// no time at all.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task ListModuleDefinitions_BypassesTheCacheWhenCachingIsDisabled()
    {
        Harness harness = Harness.Ready();
        harness.Caching.PerformanceMultiplier = 0;

        Result<IReadOnlyList<ModuleDefinitionDto>> outcome = await harness.Service
            .ListModuleDefinitionsAsync(PortalId, CancellationToken.None);

        outcome.IsSuccess.Should().BeTrue();
        outcome.Value.Should().NotBeEmpty();
        harness.CacheKey.Should().BeNull();
    }

    /// <summary>
    /// The catalogue is ordered by display name without regard to case, and by identifier where two share a
    /// name.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task ListModuleDefinitions_OrdersByFriendlyNameThenIdentifier()
    {
        Harness harness = Harness.Ready();
        harness.DefinitionCatalogue.Clear();
        harness.DefinitionCatalogue.AddRange(
        [
            Definition(3, "zebra"),
            Definition(2, "Announcements"),
            Definition(1, "announcements"),
        ]);

        Result<IReadOnlyList<ModuleDefinitionDto>> outcome = await harness.Service
            .ListModuleDefinitionsAsync(PortalId, CancellationToken.None);

        outcome.Value.Select(row => row.ModuleDefId).Should().Equal(new[] { 1, 2, 3 });
    }

    /// <summary>
    /// A package shared by several definitions is read once, so a catalogue of thirty definitions from one
    /// package costs one package read rather than thirty.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task ListModuleDefinitions_ReadsEachPackageOnlyOnce()
    {
        Harness harness = Harness.Ready();
        harness.DefinitionCatalogue.Clear();
        harness.DefinitionCatalogue.AddRange(
        [
            Definition(1, "First"),
            Definition(2, "Second"),
            Definition(3, "Third"),
        ]);

        await harness.Service.ListModuleDefinitionsAsync(PortalId, CancellationToken.None);

        harness.Definitions.Verify(
            d => d.GetDesktopModuleAsync(DesktopModuleId, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    /// <summary>
    /// The catalogue projection carries the package facts alongside the definition facts.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task ListModuleDefinitions_ProjectsThePackageFacts()
    {
        Harness harness = Harness.Ready();

        Result<IReadOnlyList<ModuleDefinitionDto>> outcome = await harness.Service
            .ListModuleDefinitionsAsync(PortalId, CancellationToken.None);

        ModuleDefinitionDto row = outcome.Value.Should().ContainSingle().Which;
        row.ModuleDefId.Should().Be(ModuleDefinitionId);
        row.FriendlyName.Should().Be(FriendlyName);
        row.DesktopModuleId.Should().Be(DesktopModuleId);
        row.ModuleName.Should().Be(PackageName);
        row.Version.Should().Be(PackageVersion);
        row.IsPortable.Should().BeTrue();
    }

    /// <summary>
    /// Exporting content requires a request.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task ExportModule_RequiresARequest()
    {
        Harness harness = Harness.Ready();

        await Assert.ThrowsAsync<ArgumentNullException>(
            () => harness.Service.ExportModuleAsync(PortalId, ModuleId, null!, CancellationToken.None));
    }

    /// <summary>
    /// A blank file name is refused, because the returned document has to be labelled.
    /// </summary>
    /// <param name="fileName">The blank name to submit.</param>
    /// <returns>A task representing the assertion.</returns>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task ExportModule_RefusesABlankFileName(string fileName)
    {
        Harness harness = Harness.Ready();

        Result<string> outcome = await harness.Service.ExportModuleAsync(
            PortalId,
            ModuleId,
            new ModuleExportRequest { FileName = fileName },
            CancellationToken.None);

        outcome.IsFailure.Should().BeTrue();
        outcome.Reason!.Code.Should().Be(RequestInvalidCode);
        outcome.Reason!.Message.Should()
            .Be("A file name is required so the returned document can be labelled.");
    }

    /// <summary>
    /// Exporting a module the tenant does not have is refused.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task ExportModule_RefusesAnUnknownModule()
    {
        Harness harness = Harness.Ready();
        harness.LookupModule = null;

        Result<string> outcome = await harness.Service.ExportModuleAsync(
            PortalId,
            ModuleId,
            new ModuleExportRequest { FileName = "content.xml" },
            CancellationToken.None);

        outcome.IsFailure.Should().BeTrue();
        outcome.Reason!.Code.Should().Be(NotFoundCode);
    }

    /// <summary>
    /// A module whose package is missing, carries no business controller, or does not declare the portable
    /// capability is refused with one reason, because none of the three can produce content.
    /// </summary>
    /// <param name="condition">Which of the three conditions to arrange.</param>
    /// <returns>A task representing the assertion.</returns>
    [Theory]
    [InlineData("no-package")]
    [InlineData("no-controller")]
    [InlineData("not-portable")]
    public async Task ExportModule_RefusesAModuleThatCannotProduceContent(string condition)
    {
        Harness harness = Harness.Ready();
        switch (condition)
        {
            case "no-package":
                harness.Packages.Clear();
                break;
            case "no-controller":
                harness.Packages[DesktopModuleId]!.BusinessControllerClass = null;
                break;
            default:
                harness.Packages[DesktopModuleId]!.SupportedFeatures = 0;
                break;
        }

        Result<string> outcome = await harness.Service.ExportModuleAsync(
            PortalId,
            ModuleId,
            new ModuleExportRequest { FileName = "content.xml" },
            CancellationToken.None);

        outcome.IsFailure.Should().BeTrue();
        outcome.Reason!.Code.Should().Be(NotPortableCode);
        outcome.Reason!.Message.Should()
            .Be($"Module {ModuleId} does not support content export.");
    }

    /// <summary>
    /// The exported content is wrapped in a document naming the package and its version.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task ExportModule_WrapsTheExportedContentInADocument()
    {
        Harness harness = Harness.Ready();
        harness.ExportOutcome = Result<string?>.Success("<item>one</item>");

        Result<string> outcome = await harness.Service.ExportModuleAsync(
            PortalId,
            ModuleId,
            new ModuleExportRequest { FileName = "content.xml" },
            CancellationToken.None);

        outcome.IsSuccess.Should().BeTrue();
        outcome.Value.Should().Be(
            $"<content type=\"{PackageName}\" version=\"{PackageVersion}\">&lt;item&gt;one&lt;/item&gt;</content>");
        harness.BusinessControllers.Verify(
            f => f.ExportModuleContentAsync(BusinessController, ModuleId, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    /// <summary>
    /// A failure from the content controller is carried through rather than translated.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task ExportModule_ReportsTheControllersOwnFailure()
    {
        Harness harness = Harness.Ready();
        harness.ExportOutcome = Result<string?>.Failure("module.controller_failed", "The controller refused.");

        Result<string> outcome = await harness.Service.ExportModuleAsync(
            PortalId,
            ModuleId,
            new ModuleExportRequest { FileName = "content.xml" },
            CancellationToken.None);

        outcome.IsFailure.Should().BeTrue();
        outcome.Reason!.Code.Should().Be("module.controller_failed");
        outcome.Reason!.Message.Should().Be("The controller refused.");
    }

    /// <summary>
    /// A module whose stored controller name matches nothing registered is reported as unable to produce
    /// content, carrying the factory's own explanation.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task ExportModule_ReportsThatNoControllerCoversTheModule()
    {
        Harness harness = Harness.Ready();
        harness.ExportOutcome = Result<string?>.Success(
            null,
            new ResultReason("module.controller_unknown", "no controller is registered under that name."));

        Result<string> outcome = await harness.Service.ExportModuleAsync(
            PortalId,
            ModuleId,
            new ModuleExportRequest { FileName = "content.xml" },
            CancellationToken.None);

        outcome.IsFailure.Should().BeTrue();
        outcome.Reason!.Code.Should().Be(NotPortableCode);
        outcome.Reason!.Message.Should().Be(
            $"Module {ModuleId} could not be asked for content: no controller is registered under that name.");
    }

    /// <summary>
    /// A default explanation is used when the factory offers none, so the caller is never handed a sentence
    /// with a hole in it.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task ExportModule_UsesADefaultAdvisoryWhenTheFactoryOffersNone()
    {
        Harness harness = Harness.Ready();
        harness.ExportOutcome = Result<string?>.Success(null);

        Result<string> outcome = await harness.Service.ExportModuleAsync(
            PortalId,
            ModuleId,
            new ModuleExportRequest { FileName = "content.xml" },
            CancellationToken.None);

        outcome.Reason!.Message.Should().Be(
            $"Module {ModuleId} could not be asked for content: no registered business controller covers this module.");
    }

    /// <summary>
    /// Importing content requires a request.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task ImportModule_RequiresARequest()
    {
        Harness harness = Harness.Ready();

        await Assert.ThrowsAsync<ArgumentNullException>(
            () => harness.Service.ImportModuleAsync(PortalId, null!, CancellationToken.None));
    }

    /// <summary>
    /// Importing into a module the tenant does not have is refused.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task ImportModule_RefusesAnUnknownModule()
    {
        Harness harness = Harness.Ready();
        harness.LookupModule = null;

        Result outcome = await harness.Service.ImportModuleAsync(
            PortalId,
            new ModuleImportRequest { ModuleId = ModuleId, Content = "<content>x</content>" },
            CancellationToken.None);

        outcome.IsFailure.Should().BeTrue();
        outcome.Reason!.Code.Should().Be(NotFoundCode);
        outcome.Reason!.Message.Should()
            .Be($"Module {ModuleId} does not exist in portal {PortalId}.");
    }

    /// <summary>
    /// An empty document is refused before the package is examined.
    /// </summary>
    /// <param name="content">The empty content to submit.</param>
    /// <returns>A task representing the assertion.</returns>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task ImportModule_RefusesEmptyContent(string content)
    {
        Harness harness = Harness.Ready();

        Result outcome = await harness.Service.ImportModuleAsync(
            PortalId,
            new ModuleImportRequest { ModuleId = ModuleId, Content = content },
            CancellationToken.None);

        outcome.IsFailure.Should().BeTrue();
        outcome.Reason!.Code.Should().Be(ContentInvalidCode);
        outcome.Reason!.Message.Should().Be("The submitted document is empty.");
    }

    /// <summary>
    /// A module that cannot accept content is refused.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task ImportModule_RefusesAModuleThatIsNotPortable()
    {
        Harness harness = Harness.Ready();
        harness.Packages[DesktopModuleId]!.SupportedFeatures = 0;

        Result outcome = await harness.Service.ImportModuleAsync(
            PortalId,
            new ModuleImportRequest { ModuleId = ModuleId, Content = "<content>x</content>" },
            CancellationToken.None);

        outcome.IsFailure.Should().BeTrue();
        outcome.Reason!.Code.Should().Be(NotPortableCode);
        outcome.Reason!.Message.Should()
            .Be($"Module {ModuleId} does not support content import.");
    }

    /// <summary>
    /// Content that is not well-formed is refused with the parser's own explanation appended, so an operator
    /// can see where the document broke.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task ImportModule_RefusesMalformedXml()
    {
        Harness harness = Harness.Ready();

        Result outcome = await harness.Service.ImportModuleAsync(
            PortalId,
            new ModuleImportRequest { ModuleId = ModuleId, Content = "<content>unclosed" },
            CancellationToken.None);

        outcome.IsFailure.Should().BeTrue();
        outcome.Reason!.Code.Should().Be(ContentInvalidCode);
        outcome.Reason!.Message.Should().StartWith("The submitted document is not well-formed XML: ");
    }

    /// <summary>
    /// A well-formed document with the wrong root element is refused.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task ImportModule_RefusesADocumentWithTheWrongRoot()
    {
        Harness harness = Harness.Ready();

        Result outcome = await harness.Service.ImportModuleAsync(
            PortalId,
            new ModuleImportRequest { ModuleId = ModuleId, Content = "<payload>x</payload>" },
            CancellationToken.None);

        outcome.IsFailure.Should().BeTrue();
        outcome.Reason!.Code.Should().Be(ContentInvalidCode);
        outcome.Reason!.Message.Should()
            .Be("The submitted document must have a <content> root element.");
    }

    /// <summary>
    /// The root element name is matched without regard to case, so a document written by a different tool is
    /// still accepted.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task ImportModule_AcceptsARootNamedWithoutRegardToCase()
    {
        Harness harness = Harness.Ready();

        Result outcome = await harness.Service.ImportModuleAsync(
            PortalId,
            new ModuleImportRequest { ModuleId = ModuleId, Content = "<Content>x</Content>" },
            CancellationToken.None);

        outcome.IsSuccess.Should().BeTrue();
    }

    /// <summary>
    /// A document whose root carries child elements hands the inner markup to the controller verbatim, while
    /// one carrying only text hands over the text.
    /// </summary>
    /// <param name="content">The document to submit.</param>
    /// <param name="expectedPayload">The payload the controller is measured to receive.</param>
    /// <returns>A task representing the assertion.</returns>
    [Theory]
    [InlineData("<content><item>one</item><item>two</item></content>", "<item>one</item><item>two</item>")]
    [InlineData("<content>plain text</content>", "plain text")]
    public async Task ImportModule_PassesTheDocumentsPayloadToTheController(string content, string expectedPayload)
    {
        Harness harness = Harness.Ready();

        await harness.Service.ImportModuleAsync(
            PortalId,
            new ModuleImportRequest { ModuleId = ModuleId, Content = content },
            CancellationToken.None);

        harness.ImportedPayload.Should().Be(expectedPayload);
    }

    /// <summary>
    /// The document's own version wins when it carries one, and the package's version is used when it does
    /// not, so a document exported by an older release still declares the version it was written at.
    /// </summary>
    /// <param name="content">The document to submit.</param>
    /// <param name="expectedVersion">The version the controller is measured to receive.</param>
    /// <returns>A task representing the assertion.</returns>
    [Theory]
    [InlineData("<content version=\"03.02.00\">x</content>", "03.02.00")]
    [InlineData("<content version=\"\">x</content>", PackageVersion)]
    [InlineData("<content>x</content>", PackageVersion)]
    public async Task ImportModule_ResolvesTheContentVersion(string content, string expectedVersion)
    {
        Harness harness = Harness.Ready();

        await harness.Service.ImportModuleAsync(
            PortalId,
            new ModuleImportRequest { ModuleId = ModuleId, Content = content },
            CancellationToken.None);

        harness.ImportedVersion.Should().Be(expectedVersion);
    }

    /// <summary>
    /// The import is attributed to the caller, and to nobody in particular when no caller is identified.
    /// </summary>
    /// <param name="identified">Whether a caller is identified.</param>
    /// <param name="expectedUserId">The identifier the controller is measured to receive.</param>
    /// <returns>A task representing the assertion.</returns>
    [Theory]
    [InlineData(true, CallerId)]
    [InlineData(false, -1)]
    public async Task ImportModule_AttributesTheImportToTheCaller(bool identified, int expectedUserId)
    {
        Harness harness = Harness.Ready();
        harness.CallerUserId = identified ? CallerId : null;

        await harness.Service.ImportModuleAsync(
            PortalId,
            new ModuleImportRequest { ModuleId = ModuleId, Content = "<content>x</content>" },
            CancellationToken.None);

        harness.ImportedUserId.Should().Be(expectedUserId);
    }

    /// <summary>
    /// A failure from the content controller is carried through, and nothing is committed.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task ImportModule_ReportsTheControllersOwnFailure()
    {
        Harness harness = Harness.Ready();
        harness.ImportOutcome = Result.Failure("module.controller_failed", "The controller refused.");

        Result outcome = await harness.Service.ImportModuleAsync(
            PortalId,
            new ModuleImportRequest { ModuleId = ModuleId, Content = "<content>x</content>" },
            CancellationToken.None);

        outcome.IsFailure.Should().BeTrue();
        outcome.Reason!.Code.Should().Be("module.controller_failed");
        harness.UnitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    /// <summary>
    /// An advisory the controller attached to its success is carried through to the caller.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task ImportModule_CarriesTheControllersAdvisoryOnSuccess()
    {
        Harness harness = Harness.Ready();
        harness.ImportOutcome = Result.Success(
            new ResultReason("module.controller_unknown", "no controller covers this module."));

        Result outcome = await harness.Service.ImportModuleAsync(
            PortalId,
            new ModuleImportRequest { ModuleId = ModuleId, Content = "<content>x</content>" },
            CancellationToken.None);

        outcome.IsSuccess.Should().BeTrue();
        outcome.Reason!.Code.Should().Be("module.controller_unknown");
    }

    /// <summary>
    /// A successful import commits once and discards the cached module list of every page the module sits on.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task ImportModule_CommitsAndDiscardsEveryAffectedPage()
    {
        Harness harness = Harness.Ready();
        harness.PlacementsByModuleId[ModuleId] =
        [
            Placement(TabModuleId, TabId),
            Placement(OtherTabModuleId, SecondTabId),
        ];

        Result outcome = await harness.Service.ImportModuleAsync(
            PortalId,
            new ModuleImportRequest { ModuleId = ModuleId, Content = "<content>x</content>" },
            CancellationToken.None);

        outcome.IsSuccess.Should().BeTrue();
        outcome.Reason.Should().BeNull();
        harness.UnitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
        harness.InvalidatedTabIds.Should().BeEquivalentTo(new[] { TabId, SecondTabId });
    }

    /// <summary>
    /// Builds the module fixture the store returns, bearing the measured identifier seed of zero.
    /// </summary>
    /// <returns>A module belonging to the tenant under test.</returns>
    private static Module StoredModule() => new()
    {
        ModuleId = ModuleId,
        ModuleDefinitionId = ModuleDefinitionId,
        PortalId = PortalId,
        ModuleTitle = ModuleTitle,
        AllTabs = false,
        IsDeleted = false,
        InheritViewPermissions = true,
    };

    /// <summary>
    /// Builds a placement of the module under test.
    /// </summary>
    /// <param name="tabModuleId">The placement identifier to carry.</param>
    /// <param name="tabId">The page the placement sits on.</param>
    /// <returns>A placement row.</returns>
    private static TabModule Placement(int tabModuleId, int tabId) => new()
    {
        TabModuleId = tabModuleId,
        TabId = tabId,
        ModuleId = ModuleId,
        PaneName = DefaultPaneName,
        ModuleOrder = 1,
        CacheTime = 0,
        Visibility = ModuleVisibility.Maximized,
        DisplayTitle = true,
    };

    /// <summary>
    /// Builds a definition sharing the one package fixture.
    /// </summary>
    /// <param name="moduleDefinitionId">The definition identifier to carry.</param>
    /// <param name="friendlyName">The display name to carry.</param>
    /// <returns>A definition row.</returns>
    private static ModuleDefinition Definition(int moduleDefinitionId, string friendlyName) => new()
    {
        ModuleDefinitionId = moduleDefinitionId,
        FriendlyName = friendlyName,
        DesktopModuleId = DesktopModuleId,
        DefaultCacheTime = 0,
    };

    /// <summary>
    /// Builds a create request that passes every check the service performs.
    /// </summary>
    /// <returns>A well-formed create request.</returns>
    private static CreateModuleRequest ValidCreateRequest() => new()
    {
        ModuleDefId = ModuleDefinitionId,
        TabId = TabId,
        ModuleTitle = ModuleTitle,
    };

    /// <summary>
    /// Builds an update request that passes every check the service performs.
    /// </summary>
    /// <returns>A well-formed update request.</returns>
    private static UpdateModuleRequest ValidUpdateRequest() => new()
    {
        ModuleTitle = ModuleTitle,
    };

    /// <summary>
    /// Assembles the service over nine recording doubles, exposing every answer as mutable state so a test
    /// can change the world after the doubles have been wired.
    /// </summary>
    private sealed class Harness
    {
        private Harness()
        {
            Caching = new CachingOptions();
            PortalExists = true;
            PortalRow = new Portal
            {
                PortalId = PortalId,
                PortalName = "Measured Portal",
                DefaultLanguage = "en-US",
                HomeDirectory = "Portals/0",
                AdminTabId = AdminTabId,
            };

            Module module = StoredModule();
            module.TabModules.Add(Placement(TabModuleId, TabId));
            LookupModule = module;

            PlacementsById = new Dictionary<int, TabModule?> { [TabModuleId] = module.TabModules.Single() };
            PlacementsByModuleId = new Dictionary<int, List<TabModule>>
            {
                [ModuleId] = [module.TabModules.Single()],
            };
            SettingsByModuleId = [];
            PlacementSettingsByTabModuleId = [];
            TabsById = new Dictionary<int, Tab?>
            {
                [TabId] = new Tab { TabId = TabId, PortalId = PortalId, TabName = "Home" },
                [SecondTabId] = new Tab { TabId = SecondTabId, PortalId = PortalId, TabName = "About" },
            };
            TenantTabs =
            [
                TabsById[TabId]!,
                TabsById[SecondTabId]!,
                new Tab { TabId = AdminTabId, PortalId = PortalId, TabName = "Admin" },
                new Tab { TabId = AdminChildTabId, PortalId = PortalId, TabName = "Site Settings", ParentId = AdminTabId },
            ];

            DefinitionCatalogue = [Definition(ModuleDefinitionId, FriendlyName)];
            LookupDefinition = DefinitionCatalogue[0];
            Packages = new Dictionary<int, DesktopModule?>
            {
                [DesktopModuleId] = new DesktopModule
                {
                    DesktopModuleId = DesktopModuleId,
                    FriendlyName = FriendlyName,
                    ModuleName = PackageName,
                    FolderName = "HTML",
                    Version = PackageVersion,
                    BusinessControllerClass = BusinessController,
                    SupportedFeatures = 1,
                },
            };

            ModulePage = PagedResult<Module>.Empty;
            ExportOutcome = Result<string?>.Success("<item>one</item>");
            ImportOutcome = Result.Success();
            CallerUserId = CallerId;

            AddedModules = [];
            AddedPlacements = [];
            RemovedPlacements = [];
            AddedSettings = [];
            RemovedSettings = [];
            AddedPlacementSettings = [];
            RemovedPlacementSettings = [];
            InvalidatedTabIds = [];

            Modules = new Mock<IModuleRepository>(MockBehavior.Loose);
            Definitions = new Mock<IModuleDefinitionRepository>(MockBehavior.Loose);
            Tabs = new Mock<ITabRepository>(MockBehavior.Loose);
            Portals = new Mock<IPortalRepository>(MockBehavior.Loose);
            UnitOfWork = new Mock<IUnitOfWork>(MockBehavior.Loose);
            Cache = new Mock<ICacheService>(MockBehavior.Loose);
            CurrentUser = new Mock<ICurrentUser>(MockBehavior.Loose);
            BusinessControllers = new Mock<IModuleBusinessControllerFactory>(MockBehavior.Loose);

            Service = new ModuleService(
                Modules.Object,
                Definitions.Object,
                Tabs.Object,
                Portals.Object,
                UnitOfWork.Object,
                Cache.Object,
                CurrentUser.Object,
                BusinessControllers.Object,
                Caching);
        }

        public ModuleService Service { get; }

        public Mock<IModuleRepository> Modules { get; }

        public Mock<IModuleDefinitionRepository> Definitions { get; }

        public Mock<ITabRepository> Tabs { get; }

        public Mock<IPortalRepository> Portals { get; }

        public Mock<IUnitOfWork> UnitOfWork { get; }

        public Mock<ICacheService> Cache { get; }

        public Mock<ICurrentUser> CurrentUser { get; }

        public Mock<IModuleBusinessControllerFactory> BusinessControllers { get; }

        public CachingOptions Caching { get; }

        public bool PortalExists { get; set; }

        public Portal? PortalRow { get; set; }

        public Module? LookupModule { get; set; }

        public Dictionary<int, TabModule?> PlacementsById { get; }

        public Dictionary<int, List<TabModule>> PlacementsByModuleId { get; }

        public Dictionary<int, List<ModuleSetting>> SettingsByModuleId { get; }

        public Dictionary<int, List<TabModuleSetting>> PlacementSettingsByTabModuleId { get; }

        public Dictionary<int, Tab?> TabsById { get; }

        public List<Tab> TenantTabs { get; }

        public List<ModuleDefinition> DefinitionCatalogue { get; }

        public ModuleDefinition? LookupDefinition { get; set; }

        public Dictionary<int, DesktopModule?> Packages { get; }

        public PagedResult<Module> ModulePage { get; set; }

        public Result<string?> ExportOutcome { get; set; }

        public Result ImportOutcome { get; set; }

        public int? CallerUserId { get; set; }

        public string? ImportedPayload { get; private set; }

        public string? ImportedVersion { get; private set; }

        public int ImportedUserId { get; private set; }

        public string? CacheKey { get; private set; }

        public TimeSpan CacheExpiration { get; private set; }

        public List<Module> AddedModules { get; }

        public List<TabModule> AddedPlacements { get; }

        public List<TabModule> RemovedPlacements { get; }

        public List<ModuleSetting> AddedSettings { get; }

        public List<ModuleSetting> RemovedSettings { get; }

        public List<TabModuleSetting> AddedPlacementSettings { get; }

        public List<TabModuleSetting> RemovedPlacementSettings { get; }

        public List<int> InvalidatedTabIds { get; }

        /// <summary>
        /// Builds a harness whose world is consistent: the tenant exists with one content page beside the
        /// page under test, the module exists with one placement, and the package declares portability.
        /// </summary>
        /// <returns>A wired harness.</returns>
        public static Harness Ready()
        {
            var harness = new Harness();

            harness.Portals
                .Setup(p => p.ExistsAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => harness.PortalExists);
            harness.Portals
                .Setup(p => p.GetByIdAsync(It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => harness.PortalRow);

            harness.Tabs
                .Setup(t => t.GetAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((int tabId, CancellationToken _) =>
                    harness.TabsById.TryGetValue(tabId, out Tab? found) ? found : null);
            harness.Tabs
                .Setup(t => t.ListAsync(It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => harness.TenantTabs);

            harness.Modules
                .Setup(m => m.GetAsync(It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => harness.LookupModule);
            harness.Modules
                .Setup(m => m.GetPlacementAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((int tabModuleId, CancellationToken _) =>
                    harness.PlacementsById.TryGetValue(tabModuleId, out TabModule? found) ? found : null);
            harness.Modules
                .Setup(m => m.ListPlacementsAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((int moduleId, CancellationToken _) =>
                    harness.PlacementsByModuleId.TryGetValue(moduleId, out List<TabModule>? found)
                        ? (IReadOnlyList<TabModule>)found
                        : Array.Empty<TabModule>());
            harness.Modules
                .Setup(m => m.ListSettingsAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((int moduleId, CancellationToken _) =>
                    harness.SettingsByModuleId.TryGetValue(moduleId, out List<ModuleSetting>? found)
                        ? (IReadOnlyList<ModuleSetting>)found
                        : Array.Empty<ModuleSetting>());
            harness.Modules
                .Setup(m => m.ListPlacementSettingsAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((int tabModuleId, CancellationToken _) =>
                    harness.PlacementSettingsByTabModuleId.TryGetValue(tabModuleId, out List<TabModuleSetting>? found)
                        ? (IReadOnlyList<TabModuleSetting>)found
                        : Array.Empty<TabModuleSetting>());
            harness.Modules
                .Setup(m => m.ListAsync(
                    It.IsAny<int>(),
                    It.IsAny<int?>(),
                    It.IsAny<bool>(),
                    It.IsAny<int>(),
                    It.IsAny<int>(),
                    It.IsAny<string?>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => harness.ModulePage);

            harness.Modules
                .Setup(m => m.Add(It.IsAny<Module>()))
                .Callback<Module>(harness.AddedModules.Add);
            harness.Modules
                .Setup(m => m.AddPlacement(It.IsAny<TabModule>()))
                .Callback<TabModule>(harness.AddedPlacements.Add);
            harness.Modules
                .Setup(m => m.RemovePlacement(It.IsAny<TabModule>()))
                .Callback<TabModule>(harness.RemovedPlacements.Add);
            harness.Modules
                .Setup(m => m.AddSetting(It.IsAny<ModuleSetting>()))
                .Callback<ModuleSetting>(harness.AddedSettings.Add);
            harness.Modules
                .Setup(m => m.RemoveSetting(It.IsAny<ModuleSetting>()))
                .Callback<ModuleSetting>(harness.RemovedSettings.Add);
            harness.Modules
                .Setup(m => m.AddPlacementSetting(It.IsAny<TabModuleSetting>()))
                .Callback<TabModuleSetting>(harness.AddedPlacementSettings.Add);
            harness.Modules
                .Setup(m => m.RemovePlacementSetting(It.IsAny<TabModuleSetting>()))
                .Callback<TabModuleSetting>(harness.RemovedPlacementSettings.Add);

            harness.Definitions
                .Setup(d => d.ListAsync(It.IsAny<int?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => harness.DefinitionCatalogue);
            harness.Definitions
                .Setup(d => d.GetAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => harness.LookupDefinition);
            harness.Definitions
                .Setup(d => d.GetDesktopModuleAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((int desktopModuleId, CancellationToken _) =>
                    harness.Packages.TryGetValue(desktopModuleId, out DesktopModule? found) ? found : null);

            harness.Cache
                .Setup(c => c.InvalidateModules(It.IsAny<int>()))
                .Callback<int>(harness.InvalidatedTabIds.Add);
            harness.Cache
                .Setup(c => c.GetOrCreateAsync(
                    It.IsAny<string>(),
                    It.IsAny<Func<CancellationToken, Task<IReadOnlyList<ModuleDefinitionDto>>>>(),
                    It.IsAny<TimeSpan>(),
                    It.IsAny<CancellationToken>()))
                .Returns((
                    string key,
                    Func<CancellationToken, Task<IReadOnlyList<ModuleDefinitionDto>>> factory,
                    TimeSpan expiration,
                    CancellationToken token) =>
                {
                    harness.CacheKey = key;
                    harness.CacheExpiration = expiration;
                    return factory(token);
                });

            harness.CurrentUser.SetupGet(c => c.UserId).Returns(() => harness.CallerUserId);

            harness.BusinessControllers
                .Setup(f => f.ExportModuleContentAsync(
                    It.IsAny<string?>(),
                    It.IsAny<int>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => harness.ExportOutcome);
            harness.BusinessControllers
                .Setup(f => f.ImportModuleContentAsync(
                    It.IsAny<string?>(),
                    It.IsAny<int>(),
                    It.IsAny<string?>(),
                    It.IsAny<string?>(),
                    It.IsAny<int>(),
                    It.IsAny<CancellationToken>()))
                .Callback<string?, int, string?, string?, int, CancellationToken>(
                    (_, _, payload, version, userId, _) =>
                    {
                        harness.ImportedPayload = payload;
                        harness.ImportedVersion = version;
                        harness.ImportedUserId = userId;
                    })
                .ReturnsAsync(() => harness.ImportOutcome);

            harness.UnitOfWork
                .Setup(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(1);

            return harness;
        }

        /// <summary>
        /// Adds a second module instance built on a site-settings definition, which is the instance the
        /// tenant-default operation writes its two settings against.
        /// </summary>
        public void AddSiteSettingsInstance()
        {
            ModuleDefinition siteSettings = Definition(ModuleDefinitionId + 1, SiteSettingsDefinitionName);
            DefinitionCatalogue.Add(siteSettings);

            Module host = StoredModule();
            host.ModuleId = OtherModuleId;
            host.ModuleDefinitionId = siteSettings.ModuleDefinitionId;

            ModulePage = PagedResult<Module>.Unpaged([host]);
        }
    }
}
