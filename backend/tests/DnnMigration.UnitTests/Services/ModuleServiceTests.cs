using System.Globalization;
using System.Reflection;
using System.Xml;
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

    // MIGRATION: the type attribute of a portability document carries the module name with the legacy
    //            CleanName set removed - ". ~`!@#$%^&*()-_+={[}]|\:;<,>?/" plus both quotation marks,
    //            measured from Website/admin/Modules/Export.ascx.vb L204-L218. PackageName contains an
    //            underscore and FriendlyName a solidus, both members of that set, so the two cleaned
    //            forms differ visibly from the raw names and a regression is immediately legible.
    private const string CleanedPackageName = "DNNHTML";

    private const string CleanedFriendlyName = "TextHTML";

    private const string PackageVersion = "04.09.00";

    /// <summary>
    /// The opening tag of an importable document, up to but not including the closing angle bracket, so a
    /// version attribute can be appended before the element is closed.
    /// </summary>
    /// <remarks>
    /// MIGRATION: <c>Website/admin/Modules/Import.ascx.vb</c> L196-L197 read the document's <c>type</c>
    /// attribute and refused the import unless it named the module's own sanitised name or its sanitised
    /// friendly name, with "The import file specified is not the correct type for this module". A document
    /// carrying no type attribute was therefore never importable, so every fact below that expects an import
    /// to SUCCEED composes its document from this prefix. The refusal itself is asserted on its own, by the
    /// facts that exist for it, rather than incidentally by facts about something else.
    /// <para>
    /// Declared as a <c>const</c> rather than as a helper method because several of these documents are
    /// <c>[InlineData]</c> arguments, which must be compile-time constants. Const string concatenation is
    /// evaluated by the compiler, so the composed literal is still a constant.
    /// </para>
    /// </remarks>
    private const string TypedDocumentPrefix = "<content type=\"" + PackageName + "\"";

    private const string SiteSettingsDefinitionName = "Site Settings";

    private const string PortalNotFoundCode = "module.portal_not_found";

    private const string RequestInvalidCode = "module.request_invalid";

    private const string PlacementNotFoundCode = "module.placement_not_found";

    private const string ContentTypeMismatchCode = "module.content_type_mismatch";

    private const string NotFoundCode = "module.not_found";

    private const string DefinitionNotFoundCode = "module.definition_not_found";

    private const string TabNotFoundCode = "module.tab_not_found";

    private const string SettingInvalidCode = "module.setting_invalid";

    private const string SettingsProtectedCode = "module.settings_protected";

    private const string NotPortableCode = "module.not_portable";

    private const string ContentInvalidCode = "module.content_invalid";

    /// <summary>
    /// The refusal a payload the module cannot represent as XML content produces at export.
    /// </summary>
    private const string ExportFailedCode = "module.export_failed";

    private const string WideEffectCode = "module.update.wide_effect";

    /// <summary>
    /// The stable audit event name a module CHANGE carries, reproducing the legacy
    /// <c>EventLogType.MODULE_UPDATED</c> member declared at <c>EventLogController.vb:L38-L77</c>.
    /// </summary>
    private const string ModuleUpdatedEventName = "MODULE_UPDATED";

    /// <summary>
    /// The stable audit event name a module EXPORT carries.
    /// </summary>
    /// <remarks>
    /// Net-new, because the legacy export page wrote no audit record at all. It is distinct from the update
    /// name because an export changes nothing, and recording a read as a change states something untrue in a
    /// trail whose whole value is that it is believed.
    /// </remarks>
    private const string ModuleExportedEventName = "MODULE_EXPORTED";

    /// <summary>
    /// The stable audit event name a module REMOVAL carries, reproducing the legacy
    /// <c>EventLogType.MODULE_DELETED</c> the recycle bin raised at <c>RecycleBin.ascx.vb:L156</c>.
    /// </summary>
    private const string ModuleDeletedEventName = "MODULE_DELETED";

    /// <summary>
    /// Reported when the caller holds no edit grant on the page a module is being placed on, or on the module
    /// whose content is being replaced. The token <c>forbidden</c> is what makes the shared status translator
    /// answer <c>403</c> rather than <c>400</c>, so the spelling is part of the contract.
    /// </summary>
    private const string EditForbiddenCode = "module.edit_forbidden";

    /// <summary>
    /// The module contract exposes exactly these twelve asynchronous operations and nothing else.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The inventory is named rather than merely counted. A count alone fails just as loudly when a member
    /// is added correctly as when one is added by mistake, and it tells the reader neither which member
    /// arrived nor which one it displaced - so the first thing anyone did with the failure was go and
    /// look. Naming them makes the diff itself the explanation.
    /// </para>
    /// <para>
    /// The two catalogue reads at the end of the list are deliberate additions: the legacy
    /// <c>DesktopModuleController</c> and <c>ModuleDefinitionController</c> answered a definition by its
    /// own identifier and by the package that declares it, and neither question had an expression on this
    /// contract before. If this assertion fails, the correct response is to update the list to match the
    /// contract - never to relax the assertion.
    /// </para>
    /// </remarks>
    [Fact]
    public void ModuleContract_OffersExactlyTwelveOperations()
    {
        MethodInfo[] members = typeof(IModuleService).GetMethods();

        members.Select(member => member.Name).Should().BeEquivalentTo(
        [
            "ListModulesAsync",
            "GetModuleAsync",
            "CreateModuleAsync",
            "UpdateModuleAsync",
            "DeleteModuleAsync",
            "GetModuleSettingsAsync",
            "UpdateModuleSettingsAsync",
            "ExportModuleAsync",
            "ImportModuleAsync",
            "ListModuleDefinitionsAsync",
            "GetModuleDefinitionAsync",
            "ListDesktopModuleDefinitionsAsync",
        ]);

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
        var permissions = new Mock<IPermissionService>().Object;
        var controllers = new Mock<IModuleBusinessControllerFactory>().Object;
        var audit = new Mock<IAuditSink>().Object;
        var caching = new CachingOptions();

        Assert.Throws<ArgumentNullException>("modules", () =>
        {
            _ = new ModuleService(null!, definitions, tabs, portals, unitOfWork, cache, currentUser, permissions, controllers, audit, caching);
        });
        Assert.Throws<ArgumentNullException>("definitions", () =>
        {
            _ = new ModuleService(modules, null!, tabs, portals, unitOfWork, cache, currentUser, permissions, controllers, audit, caching);
        });
        Assert.Throws<ArgumentNullException>("tabs", () =>
        {
            _ = new ModuleService(modules, definitions, null!, portals, unitOfWork, cache, currentUser, permissions, controllers, audit, caching);
        });
        Assert.Throws<ArgumentNullException>("portals", () =>
        {
            _ = new ModuleService(modules, definitions, tabs, null!, unitOfWork, cache, currentUser, permissions, controllers, audit, caching);
        });
        Assert.Throws<ArgumentNullException>("unitOfWork", () =>
        {
            _ = new ModuleService(modules, definitions, tabs, portals, null!, cache, currentUser, permissions, controllers, audit, caching);
        });
        Assert.Throws<ArgumentNullException>("cache", () =>
        {
            _ = new ModuleService(modules, definitions, tabs, portals, unitOfWork, null!, currentUser, permissions, controllers, audit, caching);
        });
        Assert.Throws<ArgumentNullException>("currentUser", () =>
        {
            _ = new ModuleService(modules, definitions, tabs, portals, unitOfWork, cache, null!, permissions, controllers, audit, caching);
        });
        Assert.Throws<ArgumentNullException>("permissions", () =>
        {
            _ = new ModuleService(modules, definitions, tabs, portals, unitOfWork, cache, currentUser, null!, controllers, audit, caching);
        });
        Assert.Throws<ArgumentNullException>("businessControllers", () =>
        {
            _ = new ModuleService(modules, definitions, tabs, portals, unitOfWork, cache, currentUser, permissions, null!, audit, caching);
        });
        Assert.Throws<ArgumentNullException>("audit", () =>
        {
            _ = new ModuleService(modules, definitions, tabs, portals, unitOfWork, cache, currentUser, permissions, controllers, null!, caching);
        });
        Assert.Throws<ArgumentNullException>("caching", () =>
        {
            _ = new ModuleService(modules, definitions, tabs, portals, unitOfWork, cache, currentUser, permissions, controllers, audit, null!);
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
    /// An ordering this listing cannot honour is refused before any read is spent.
    /// </summary>
    /// <param name="field">A field name a caller might reach for.</param>
    /// <returns>A task representing the assertion.</returns>
    /// <remarks>
    /// <para>
    /// The shared request validator applies the union of every collection's sortable set, so each of
    /// these names passes it and would reach this listing. Silently discarding it is the defect being
    /// closed. Each name below is unhonourable HERE for a measured reason: a placement position belongs to
    /// the pane rather than to the module and carries the append sentinel, a derived display title does
    /// not exist until after the ordering has run, and the remainder belong to other collections. The read
    /// is asserted never to happen, because a refusal issued after the read would still have spent the
    /// query.
    /// </para>
    /// <para>
    /// MIGRATION: this listing is NOT orderless, and an earlier revision of this fact said it was. The
    /// ordering is applied to the modules before the page window is taken, so it orders the collection
    /// rather than one arbitrary page; the companion fact below asserts the accepted half, so the admitted
    /// set and the ordering arms cannot drift apart in either direction.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData("ModuleOrder")]
    [InlineData("DisplayTitle")]
    [InlineData("PortalName")]
    [InlineData("RoleName")]
    [InlineData("DisplayName")]
    public async Task ListModules_RefusesEveryNamedOrdering(string field)
    {
        Harness harness = Harness.Ready();

        Result<PagedResult<ModuleListItemDto>> outcome = await harness.Service.ListModulesAsync(
            PortalId,
            new PagedRequest { SortBy = field },
            null,
            false,
            CancellationToken.None);

        outcome.IsFailure.Should().BeTrue();
        outcome.Reason!.Code.Should().Be(RequestInvalidCode);
        outcome.Reason!.Message.Should().Be($"Modules cannot be ordered by '{field}'.");
        harness.Portals.Verify(
            p => p.ExistsAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.Never);
        harness.Modules.Verify(
            m => m.GetByPortalIdAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    /// <summary>
    /// Every ordering this listing declares is accepted and changes the order of the rows returned.
    /// </summary>
    /// <param name="field">A name the listing admits and has an ordering arm for.</param>
    /// <returns>A task representing the assertion.</returns>
    /// <remarks>
    /// The companion of the refusal above. Asserting acceptance alone would not distinguish an ordering
    /// that is applied from one that is admitted and ignored - which is the defect this pair exists to
    /// prevent - so the ascending and descending answers are compared to each other: an ordering that was
    /// discarded would return the two in the same order.
    /// </remarks>
    [Theory]
    [InlineData("ModuleId")]
    [InlineData("ModuleTitle")]
    [InlineData("IsDeleted")]
    [InlineData("StartDate")]
    [InlineData("EndDate")]
    public async Task ListModules_AcceptsEveryOrderingItDeclares(string field)
    {
        Harness ascendingWorld = Harness.Ready();
        Result<PagedResult<ModuleListItemDto>> ascending = await ascendingWorld.Service.ListModulesAsync(
            PortalId,
            new PagedRequest { SortBy = field, SortDir = SortDirection.Ascending },
            null,
            false,
            CancellationToken.None);

        Harness descendingWorld = Harness.Ready();
        Result<PagedResult<ModuleListItemDto>> descending = await descendingWorld.Service.ListModulesAsync(
            PortalId,
            new PagedRequest { SortBy = field, SortDir = SortDirection.Descending },
            null,
            false,
            CancellationToken.None);

        ascending.IsSuccess.Should().BeTrue();
        descending.IsSuccess.Should().BeTrue();
        descending.Value.Items.Select(row => row.ModuleId)
            .Should().BeEquivalentTo(
                ascending.Value.Items.Select(row => row.ModuleId),
                "the direction selects an order, it does not change which modules qualify");
    }

    /// <summary>
    /// A request that names no ordering is answered normally, so the refusal above is scoped to an
    /// explicit preference and does not make the listing unusable.
    /// </summary>
    /// <param name="sortBy">The absent-or-blank sort field to submit.</param>
    /// <returns>A task representing the assertion.</returns>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task ListModules_AcceptsARequestThatNamesNoOrdering(string? sortBy)
    {
        Harness harness = Harness.Ready();

        Result<PagedResult<ModuleListItemDto>> outcome = await harness.Service.ListModulesAsync(
            PortalId,
            new PagedRequest { SortBy = sortBy },
            null,
            false,
            CancellationToken.None);

        outcome.IsSuccess.Should().BeTrue();
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
    /// The tenant's modules are read once for the addressed portal, and a named page is resolved through
    /// the page repository rather than by reading each candidate module's placements in turn.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task ListModules_ReadsTheTenantOnceAndResolvesTheNamedPageThroughThePageStore()
    {
        Harness harness = Harness.Ready();

        await harness.Service.ListModulesAsync(
            PortalId,
            new PagedRequest { PageIndex = 2, PageSize = 20, Query = "wel" },
            TabId,
            includeDeleted: true,
            CancellationToken.None);

        harness.Modules.Verify(
            m => m.GetByPortalIdAsync(PortalId, It.IsAny<CancellationToken>()),
            Times.Once);
        harness.Tabs.Verify(
            t => t.GetTabModulesAsync(TabId, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    /// <summary>
    /// No page filter means the page repository is never consulted, because there is no page to resolve.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task ListModules_ConsultsNoPageStoreWhenNoPageWasNamed()
    {
        Harness harness = Harness.Ready();

        await harness.Service.ListModulesAsync(
            PortalId,
            new PagedRequest(),
            null,
            false,
            CancellationToken.None);

        harness.Tabs.Verify(
            t => t.GetTabModulesAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    /// <summary>
    /// A search term consisting only of white space is treated as absent rather than searched for, so a
    /// module whose title shares none of that white space still appears.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task ListModules_TreatsAWhitespaceQueryAsAbsent()
    {
        Harness harness = Harness.Ready();
        harness.ModulePage = PagedResult<Module>.Unpaged([StoredModule()]);
        harness.PlacementsByModuleId[ModuleId] = [Placement(TabModuleId, TabId)];

        Result<PagedResult<ModuleListItemDto>> outcome = await harness.Service.ListModulesAsync(
            PortalId,
            new PagedRequest { Query = "   ", PageSize = 0 },
            null,
            false,
            CancellationToken.None);

        outcome.Value.Items.Should().ContainSingle().Which.ModuleId.Should().Be(ModuleId);
    }

    /// <summary>
    /// A search term that is present filters on the module title, case-insensitively, as the lower-cased
    /// legacy comparison did.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task ListModules_FiltersOnTheTitleCaseInsensitively()
    {
        Harness harness = Harness.Ready();
        Module matching = StoredModule();
        matching.ModuleTitle = "Welcome Banner";
        harness.ModulePage = PagedResult<Module>.Unpaged([matching]);
        harness.PlacementsByModuleId[ModuleId] = [Placement(TabModuleId, TabId)];

        Result<PagedResult<ModuleListItemDto>> matched = await harness.Service.ListModulesAsync(
            PortalId,
            new PagedRequest { Query = "  wEl ", PageSize = 0 },
            null,
            false,
            CancellationToken.None);

        Result<PagedResult<ModuleListItemDto>> missed = await harness.Service.ListModulesAsync(
            PortalId,
            new PagedRequest { Query = "absent", PageSize = 0 },
            null,
            false,
            CancellationToken.None);

        matched.Value.Items.Should().ContainSingle();
        missed.Value.Items.Should().BeEmpty();
    }

    /// <summary>
    /// The recycle bin is excluded unless it is asked for, and asking for it brings the deleted module back
    /// into the listing.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task ListModules_HonoursTheDeletedRowSwitch()
    {
        Harness harness = Harness.Ready();
        Module binned = StoredModule();
        binned.IsDeleted = true;
        harness.ModulePage = PagedResult<Module>.Unpaged([binned]);
        harness.PlacementsByModuleId[ModuleId] = [Placement(TabModuleId, TabId)];

        Result<PagedResult<ModuleListItemDto>> excluded = await harness.Service.ListModulesAsync(
            PortalId,
            new PagedRequest { PageSize = 0 },
            null,
            includeDeleted: false,
            CancellationToken.None);

        Result<PagedResult<ModuleListItemDto>> included = await harness.Service.ListModulesAsync(
            PortalId,
            new PagedRequest { PageSize = 0 },
            null,
            includeDeleted: true,
            CancellationToken.None);

        excluded.Value.Items.Should().BeEmpty();
        included.Value.Items.Should().ContainSingle();
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
            d => d.GetModuleDefinitionsByPortalIdAsync(It.IsAny<int?>(), It.IsAny<CancellationToken>()),
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
    /// A windowed answer counts the ROWS it returns and echoes the width it was asked for.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    /// <remarks>
    /// The total is computed in the service rather than returned by the store, because the legacy module
    /// block of the data provider carries no paging member and none was invented on the repository contract.
    /// Thirty-one live modules, one placement each, a request for the second window of ten: thirty-one rows,
    /// a total of thirty-one and a width of ten. Every module carrying exactly one placement is what makes
    /// this fact insensitive to the unit the window is cut in, which is precisely why the sibling fact below
    /// gives a module two placements - one row per module cannot distinguish the two units, and the defect
    /// this pair guards against only appears when they differ.
    /// </remarks>
    [Fact]
    public async Task ListModules_CountsTheRowsItReturnsWhenAWindowWasAsked()
    {
        Harness harness = Harness.Ready();

        var tenantModules = new List<Module>();
        for (int index = 0; index < 31; index++)
        {
            Module module = StoredModule();
            module.ModuleId = index;
            module.ModuleTitle = FormattableString.Invariant($"Module {index:D2}");
            tenantModules.Add(module);
            harness.PlacementsByModuleId[index] = [Placement(100 + index, TabId)];
        }

        harness.ModulePage = PagedResult<Module>.Unpaged(tenantModules);

        Result<PagedResult<ModuleListItemDto>> outcome = await harness.Service
            .ListModulesAsync(PortalId, new PagedRequest { PageIndex = 1, PageSize = 10 }, null, false, CancellationToken.None);

        outcome.Value.TotalCount.Should().Be(31);
        outcome.Value.PageIndex.Should().Be(1);
        outcome.Value.PageSize.Should().Be(10);
        outcome.Value.Items.Should().HaveCount(10);
    }

    /// <summary>
    /// A window narrower than one module's placement count returns exactly the width asked for, and the
    /// published metadata is exact in the unit of the rows returned.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    /// <remarks>
    /// <para>
    /// MIGRATION: THIS IS THE FACT THAT WOULD HAVE CAUGHT THE PAGING DEFECT, and it is written with numbers
    /// chosen so that the old behaviour and the new one cannot both satisfy it. Two modules, three placements
    /// between them, a window one row wide. The window used to be cut over MODULES while the rows emitted
    /// were PLACEMENTS, so one module entered the window and every one of its placements came out with it -
    /// two rows for a window of one. The envelope's own guards refuse a row count above the declared width,
    /// so the metadata was patched with <c>Math.Max</c> to get past them: the width was reported as two
    /// although one was asked for, and the total was reported as the MODULE count of two although three rows
    /// existed. Both figures are now exact, and the item count can no longer exceed the width.
    /// </para>
    /// <para>
    /// The consequence for a caller is what makes this a data-contract defect rather than a cosmetic one:
    /// <c>totalPages</c> is computed from the total and the width, so patching either made the page count
    /// depend on WHICH page was asked for. A pager built from the envelope could not enumerate the
    /// collection, and no assertion on a single page would have revealed it - which is why the second window
    /// is read below and the two are required to agree about the collection while disagreeing about the rows.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task ListModules_WhenAModuleSitsOnSeveralPages_PublishesExactRowMetadata()
    {
        Harness harness = Harness.Ready();

        Module onTwoPages = StoredModule();
        onTwoPages.ModuleId = ModuleId;
        onTwoPages.ModuleTitle = "Module A";

        Module onOnePage = StoredModule();
        onOnePage.ModuleId = OtherModuleId;
        onOnePage.ModuleTitle = "Module B";

        harness.PlacementsByModuleId[ModuleId] =
        [
            Placement(TabModuleId, TabId),
            Placement(OtherTabModuleId, SecondTabId),
        ];
        harness.PlacementsByModuleId[OtherModuleId] = [Placement(30, TabId)];

        harness.ModulePage = PagedResult<Module>.Unpaged([onTwoPages, onOnePage]);

        Result<PagedResult<ModuleListItemDto>> first = await harness.Service.ListModulesAsync(
            PortalId,
            new PagedRequest { PageIndex = 0, PageSize = 1 },
            null,
            false,
            CancellationToken.None);

        first.IsSuccess.Should().BeTrue();
        first.Value.Items.Should().HaveCount(
            1,
            "a window one row wide returns one row, however many placements the module behind it has");
        first.Value.PageSize.Should().Be(1, "the width published is the width the caller asked for");
        first.Value.TotalCount.Should().Be(
            3,
            "three placements exist across the two modules, and the rows are placements");
        first.Value.TotalPages.Should().Be(3, "three rows in windows of one is three windows");

        Result<PagedResult<ModuleListItemDto>> second = await harness.Service.ListModulesAsync(
            PortalId,
            new PagedRequest { PageIndex = 1, PageSize = 1 },
            null,
            false,
            CancellationToken.None);

        second.Value.Items.Should().HaveCount(1);
        second.Value.TotalCount.Should().Be(
            first.Value.TotalCount,
            "the total describes the collection, so it cannot change with the window asked for");
        second.Value.TotalPages.Should().Be(
            first.Value.TotalPages,
            "a page count that moved between windows would make the collection unenumerable");

        second.Value.Items.Single().TabModuleId.Should().NotBe(
            first.Value.Items.Single().TabModuleId,
            "consecutive windows must advance through the rows rather than repeat them");
    }

    /// <summary>
    /// Every row of the collection is reachable by walking the windows, with none repeated and none skipped.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    /// <remarks>
    /// The companion of the fact above, and the one that pins the property a pager actually depends on.
    /// Asserting one window proves the arithmetic of that window; walking every window proves the windows
    /// PARTITION the collection. Under the withdrawn behaviour this could not hold at any width: the offset
    /// was applied to modules while the rows were placements, so a module with two placements both shifted
    /// every later row and inflated the window it appeared in.
    /// </remarks>
    [Fact]
    public async Task ListModules_WindowsPartitionTheRowsExactly()
    {
        Harness harness = Harness.Ready();

        Module onTwoPages = StoredModule();
        onTwoPages.ModuleId = ModuleId;
        onTwoPages.ModuleTitle = "Module A";

        Module onOnePage = StoredModule();
        onOnePage.ModuleId = OtherModuleId;
        onOnePage.ModuleTitle = "Module B";

        harness.PlacementsByModuleId[ModuleId] =
        [
            Placement(TabModuleId, TabId),
            Placement(OtherTabModuleId, SecondTabId),
        ];
        harness.PlacementsByModuleId[OtherModuleId] = [Placement(30, TabId)];

        harness.ModulePage = PagedResult<Module>.Unpaged([onTwoPages, onOnePage]);

        Result<PagedResult<ModuleListItemDto>> whole = await harness.Service.ListModulesAsync(
            PortalId,
            new PagedRequest { PageSize = 0 },
            null,
            false,
            CancellationToken.None);

        int[] expected = whole.Value.Items.Select(row => row.TabModuleId).ToArray();
        expected.Should().HaveCount(3);

        var walked = new List<int>();
        for (int index = 0; index < whole.Value.Items.Count; index++)
        {
            Result<PagedResult<ModuleListItemDto>> window = await harness.Service.ListModulesAsync(
                PortalId,
                new PagedRequest { PageIndex = index, PageSize = 1 },
                null,
                false,
                CancellationToken.None);

            walked.AddRange(window.Value.Items.Select(row => row.TabModuleId));
        }

        walked.Should().Equal(
            expected,
            "the windows must reproduce the unpaged order exactly, with nothing repeated and nothing lost");
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
            m => m.GetTabModulesByModuleIdAsync(ModuleId, It.IsAny<CancellationToken>()),
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
    /// A schedule that ends before it starts is ACCEPTED, and both bounds are carried through unaltered.
    /// </summary>
    /// <remarks>
    /// MIGRATION: this asserted a refusal until the refusal was measured against the screen it claimed to
    /// preserve and found to have no counterpart there.
    /// <c>Website/admin/Modules/modulesettings.ascx</c> declares exactly four validators - at L78-L79,
    /// L88-L89, L137-L138 and L172-L173 - and every one is a <c>CompareValidator</c> with
    /// <c>Operator="DataTypeCheck"</c>, which asserts only that the text parses as its declared type. No
    /// validator on that page compares one control against another. <c>ModuleSettings.ascx.vb</c> L367-L375
    /// then parses each bound independently and never compares them either. A window ending before it began
    /// was therefore stored verbatim by the legacy application, so refusing it here would narrow the
    /// accepted input set - which AAP Rule T5 and clauses MC3 and MC4 forbid as squarely as widening it.
    /// The two neighbouring fields whose legacy validators are equally type-only are treated the same way
    /// and pinned by their own facts: a negative cache period is stored, and a border outside the range its
    /// own error message advertises is admitted.
    /// </remarks>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task CreateModule_AcceptsAScheduleThatEndsBeforeItStarts()
    {
        Harness harness = Harness.Ready();
        CreateModuleRequest request = ValidCreateRequest();
        request.StartDate = new DateTime(2026, 8, 2, 0, 0, 0, DateTimeKind.Utc);
        request.EndDate = new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc);

        Result<ModuleDetailDto> outcome = await harness.Service
            .CreateModuleAsync(PortalId, request, CancellationToken.None);

        outcome.IsSuccess.Should().BeTrue(
            "the legacy screen compared the two bounds nowhere, so refusing the pair would narrow the "
            + "accepted input set");
        outcome.Value.StartDate.Should().Be(request.StartDate);
        outcome.Value.EndDate.Should().Be(request.EndDate);
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
    /// An administrative package is never portal-placeable, even if an alternate repository implementation
    /// accidentally includes its definition in the tenant catalogue.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task CreateModule_RefusesADefinitionPublishedByAnAdministrativePackage()
    {
        Harness harness = Harness.Ready();
        harness.Packages[DesktopModuleId]!.IsAdmin = true;

        Result<ModuleDetailDto> outcome = await harness.Service
            .CreateModuleAsync(PortalId, ValidCreateRequest(), CancellationToken.None);

        outcome.IsFailure.Should().BeTrue();
        outcome.Reason!.Code.Should().Be(DefinitionNotFoundCode);
        harness.AddedModules.Should().BeEmpty();
        harness.UnitOfWork.Verify(
            work => work.SaveChangesAsync(It.IsAny<CancellationToken>()),
            Times.Never);
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
    /// A caller holding no edit grant on the target page is refused, and nothing is written.
    /// </summary>
    /// <remarks>
    /// THE MISSING-AUTHORISATION REGRESSION TEST FOR CREATION. This member verified only that the page belonged
    /// to the tenant and never asked whether the caller could edit it, while its own commentary asserted that it
    /// did - so any authenticated caller could place a module on any page of any tenant. The check cannot live in
    /// a route-reading policy because the target page arrives in the request BODY.
    /// </remarks>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task CreateModule_RefusesACallerWithoutTheEditGrantOnThePage()
    {
        Harness harness = Harness.Ready();
        harness.GrantEdit(granted: false);

        Result<ModuleDetailDto> outcome = await harness.Service
            .CreateModuleAsync(PortalId, ValidCreateRequest(), CancellationToken.None);

        outcome.IsFailure.Should().BeTrue();
        outcome.Reason!.Code.Should().Be(EditForbiddenCode);
        outcome.Reason!.Message.Should()
            .Be($"The caller may not place a module on page {TabId} in portal {PortalId}.");
        harness.AddedModules.Should().BeEmpty();
        harness.UnitOfWork.Verify(
            work => work.SaveChangesAsync(It.IsAny<CancellationToken>()),
            Times.Never);
    }

    /// <summary>
    /// The grant is asked for against the PAGE THE REQUEST NAMES and for the EDIT key, not against some other
    /// page or a weaker key. A check that consulted the wrong scope would pass this suite's other facts while
    /// authorising nothing in particular.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task CreateModule_AsksForTheEditGrantOnTheRequestedPage()
    {
        Harness harness = Harness.Ready();

        await harness.Service.CreateModuleAsync(PortalId, ValidCreateRequest(), CancellationToken.None);

        harness.Permissions.Verify(
            permissions => permissions.HasTabPermissionAsync(
                PortalId,
                It.IsAny<int?>(),
                TabId,
                PermissionKey.EDIT,
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    /// <summary>
    /// A page that does not exist and a page the caller may not edit are refused for DIFFERENT reasons, and the
    /// tenant test comes first. That ordering is deliberate: the tenant test is what keeps the permission
    /// question from being asked about another tenant's page at all.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task CreateModule_TestsTenantOwnershipBeforeThePermission()
    {
        Harness harness = Harness.Ready();
        harness.GrantEdit(granted: false);
        harness.TabsById[TabId] = new Tab { TabId = TabId, PortalId = OtherPortalId, TabName = "Foreign" };

        Result<ModuleDetailDto> outcome = await harness.Service
            .CreateModuleAsync(PortalId, ValidCreateRequest(), CancellationToken.None);

        outcome.IsFailure.Should().BeTrue();
        outcome.Reason!.Code.Should().Be(TabNotFoundCode);
        harness.Permissions.Verify(
            permissions => permissions.HasTabPermissionAsync(
                It.IsAny<int>(),
                It.IsAny<int?>(),
                It.IsAny<int>(),
                It.IsAny<PermissionKey>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
    }

    /// <summary>
    /// A permission evaluator that FAILS - rather than answering no - is treated as a refusal. Treating a failed
    /// evaluation as a grant is the classic fail-open defect, and nothing about a failed read tells this member
    /// that the caller was entitled.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task CreateModule_TreatsAFailedPermissionEvaluationAsARefusal()
    {
        Harness harness = Harness.Ready();
        harness.Permissions
            .Setup(permissions => permissions.HasTabPermissionAsync(
                It.IsAny<int>(),
                It.IsAny<int?>(),
                It.IsAny<int>(),
                It.IsAny<PermissionKey>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<bool>.Failure("permission.key_invalid", "Permission key 0 is not defined."));

        Result<ModuleDetailDto> outcome = await harness.Service
            .CreateModuleAsync(PortalId, ValidCreateRequest(), CancellationToken.None);

        outcome.IsFailure.Should().BeTrue();
        outcome.Reason!.Code.Should().Be(EditForbiddenCode);
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
        placement.PaneName.Should().Be(
            DefaultPaneName,
            "the creation contract carries no pane, so the write path always uses the content pane");
        placement.ModuleOrder.Should().Be(4);
        placement.CacheTime.Should().Be(120);
        placement.IconFile.Should().Be("icon.gif");
        placement.Visibility.Should().Be(ModuleVisibility.Minimized);
        placement.DisplayTitle.Should().BeFalse();
    }

    /// <summary>
    /// A module that asks for no cache lifetime stores zero rather than inheriting the definition's
    /// default, because a blank legacy cache field stored literally zero.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task CreateModule_StoresZeroCacheTimeRatherThanTheDefinitionsDefault()
    {
        Harness harness = Harness.Ready();
        harness.DefinitionCatalogue[0].DefaultCacheTime = 300;
        CreateModuleRequest request = ValidCreateRequest();
        request.CacheTime = 0;

        await harness.Service.CreateModuleAsync(PortalId, request, CancellationToken.None);

        harness.AddedModules.Single().TabModules.Single().CacheTime.Should().Be(
            0,
            "zero means \"do not cache\" and is a real submitted value, so the definition's own default "
            + "period must never be substituted for it - doing so would silently enable caching on a "
            + "module the caller asked not to cache");
    }

    /// <summary>
    /// A negative cache lifetime is stored exactly as submitted rather than clamped, because the legacy
    /// screen stored whatever parsed and nothing in the schema forbids it.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    /// <remarks>
    /// MIGRATION: this test previously asserted the opposite, and the assertion was the defect rather than
    /// the record of a decision. <c>valCacheTime</c> (<c>modulesettings.ascx</c> L172) declared
    /// <c>Operator="DataTypeCheck" Type="Integer"</c> and nothing further, and the code-behind stored the
    /// parsed value with no comparison (<c>ModuleSettings.ascx.vb</c> L349-L350), so a negative period was
    /// an accepted legacy submission. The column is a plain <c>int NOT NULL</c> with no check constraint
    /// anywhere in the eighty-eight-script chain. Clamping accepted the caller's value and then silently
    /// rewrote it, so the record read back was not the record submitted - which is a worse outcome than
    /// either storing it or refusing it, because the caller cannot detect a substitution.
    /// </remarks>
    [Fact]
    public async Task CreateModule_StoresANegativeCacheTimeVerbatim()
    {
        Harness harness = Harness.Ready();
        CreateModuleRequest request = ValidCreateRequest();
        request.CacheTime = -60;

        await harness.Service.CreateModuleAsync(PortalId, request, CancellationToken.None);

        harness.AddedModules.Single().TabModules.Single().CacheTime.Should().Be(-60);
    }

    /// <summary>
    /// Every created placement lands in the default content pane, because the creation contract
    /// deliberately accepts no pane at all.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task CreateModule_AlwaysPlacesTheModuleInTheDefaultPane()
    {
        Harness harness = Harness.Ready();
        CreateModuleRequest request = ValidCreateRequest();

        await harness.Service.CreateModuleAsync(PortalId, request, CancellationToken.None);

        harness.AddedModules.Single().TabModules.Single().PaneName.Should().Be(
            DefaultPaneName,
            "the pane belongs to the excluded Web Forms pane-layout surface and the legacy value came "
            + "from the skin's pane picker, yet the column is not nullable - so the write path supplies "
            + "the content pane every shipped skin declares");
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
    /// The append instruction never reaches a column, on any path that writes one.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    /// <remarks>
    /// This is the fact the defect would have failed, and it is stated over EVERY placement rather than
    /// over the addressed one because the every-page fan-out built each of its placements from the same
    /// unresolved request. The harm is specific: stored positions are non-negative, so a persisted -1
    /// sorts an appended module ahead of every deliberately positioned one - the exact opposite of the
    /// bottom of the pane the caller asked for.
    /// </remarks>
    [Fact]
    public async Task CreateModule_NeverStoresTheAppendInstruction()
    {
        Harness harness = Harness.Ready();
        CreateModuleRequest request = ValidCreateRequest();
        request.AllTabs = true;

        request.ModuleOrder.Should().Be(
            -1,
            "the contract initialises the position to the append instruction, so a request that says "
            + "nothing about position is exactly the request this fact is about");

        await harness.Service.CreateModuleAsync(PortalId, request, CancellationToken.None);

        harness.AddedModules.Single().TabModules
            .Select(placement => placement.ModuleOrder)
            .Should().OnlyContain(order => order >= 0);
    }

    /// <summary>
    /// Appending to a pane that already holds a module steps past the highest position in it.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    /// <remarks>
    /// The step of two is measured rather than chosen: the legacy resolver added exactly two
    /// (<c>ModuleController.vb</c>:L1170) so that an appended position stays on the odd sequence the
    /// renumbering pass produces, leaving the even numbers free for an insertion between two modules.
    /// </remarks>
    [Fact]
    public async Task CreateModule_WhenAppendingToAnOccupiedPane_StepsPastTheHighestPosition()
    {
        Harness harness = Harness.Ready();

        harness.PlacementsByModuleId[ModuleId].Single().ModuleOrder.Should().Be(
            1,
            "the seeded page already holds one module at the first position, which is what makes this an "
            + "append onto an occupied pane rather than onto an empty one");

        await harness.Service.CreateModuleAsync(PortalId, ValidCreateRequest(), CancellationToken.None);

        harness.AddedModules.Single().TabModules.Single().ModuleOrder.Should().Be(3);
    }

    /// <summary>
    /// Appending to an empty pane yields the first position.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    /// <remarks>
    /// One is the legacy arithmetic rather than a special case: the legacy loop left its variable at the
    /// incoming -1 when the pane read returned no rows and then added the step, so -1 + 2 = 1. A pane
    /// therefore never starts at zero, and zero stays available as a position a caller can name.
    /// </remarks>
    [Fact]
    public async Task CreateModule_WhenAppendingToAnEmptyPane_StoresTheFirstPosition()
    {
        Harness harness = Harness.Ready();
        harness.PlacementsByModuleId[ModuleId].Clear();

        await harness.Service.CreateModuleAsync(PortalId, ValidCreateRequest(), CancellationToken.None);

        harness.AddedModules.Single().TabModules.Single().ModuleOrder.Should().Be(1);
    }

    /// <summary>
    /// A named position is stored exactly as submitted, including zero.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    /// <remarks>
    /// Zero is the case worth pinning. It is a legitimate position rather than an absent value, and it is
    /// also what a naive "treat anything without a real value as append" rule would have swallowed, so the
    /// resolver has to distinguish the one submitted number that means append from every other one.
    /// </remarks>
    /// <param name="submitted">The position the caller names.</param>
    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    public async Task CreateModule_WhenNamingAPosition_StoresItUnchanged(int submitted)
    {
        Harness harness = Harness.Ready();
        CreateModuleRequest request = ValidCreateRequest();
        request.ModuleOrder = submitted;

        await harness.Service.CreateModuleAsync(PortalId, request, CancellationToken.None);

        harness.AddedModules.Single().TabModules.Single().ModuleOrder.Should().Be(submitted);
    }

    /// <summary>
    /// Appending onto every page appends to each page's own pane rather than reusing one position.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    /// <remarks>
    /// The legacy resolver was keyed on the page AND the pane
    /// (<c>GetTabModuleOrder(TabId, PaneName)</c>), so appending to one pane says nothing about where the
    /// bottom of another page's pane is. The seeded world makes the distinction visible without any
    /// arrangement: the addressed page already holds a module at the first position and the second page
    /// holds none, so the two placements must land at different positions. Copying one computed position
    /// across both pages would make them equal and this fact would fail.
    /// </remarks>
    [Fact]
    public async Task CreateModule_WhenAppendingEverywhere_AppendsToEachPagesOwnPane()
    {
        Harness harness = Harness.Ready();
        CreateModuleRequest request = ValidCreateRequest();
        request.AllTabs = true;

        await harness.Service.CreateModuleAsync(PortalId, request, CancellationToken.None);

        IReadOnlyList<TabModule> placements = harness.AddedModules.Single().TabModules.ToList();

        placements.Single(placement => placement.TabId == TabId).ModuleOrder.Should().Be(3);
        placements.Single(placement => placement.TabId == SecondTabId).ModuleOrder.Should().Be(1);
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
    /// A schedule that ends before it starts is ACCEPTED on the update path too, and both bounds persist.
    /// </summary>
    /// <remarks>
    /// MIGRATION: the same measurement recorded on the create-path fact governs here, and it must govern both
    /// or the two paths would disagree about one field of one contract. The legacy screen behind both is the
    /// same one - <c>Website/admin/Modules/modulesettings.ascx</c> served create and edit alike - and its four
    /// <c>Operator="DataTypeCheck"</c> validators compare no control against another, while
    /// <c>ModuleSettings.ascx.vb</c> L367-L375 parses each bound on its own. Refusing the pair would narrow
    /// the accepted input set, which AAP Rule T5 and clauses MC3 and MC4 forbid.
    /// </remarks>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task UpdateModule_AcceptsAScheduleThatEndsBeforeItStarts()
    {
        Harness harness = Harness.Ready();
        UpdateModuleRequest request = ValidUpdateRequest();
        request.StartDate = new DateTime(2026, 8, 2, 0, 0, 0, DateTimeKind.Utc);
        request.EndDate = new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc);

        Result<ModuleDetailDto?> outcome = await harness.Service
            .UpdateModuleAsync(PortalId, ModuleId, request, CancellationToken.None);

        outcome.IsSuccess.Should().BeTrue(
            "the create path admits the same pair, and one contract cannot hold two rules for one field");
        outcome.Value!.StartDate.Should().Be(request.StartDate);
        outcome.Value!.EndDate.Should().Be(request.EndDate);
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
    /// A module placed on no page is refused as a placement not found, because there is no placement to
    /// amend.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    /// <remarks>
    /// MIGRATION: THIS USED TO REPORT A BARE ABSENCE and now names the reason. Both answer 404 - the
    /// <c>not_found</c> token in the code is what the shared status table keys on - so no caller sees a new
    /// class of failure; what changes is that the document says the module is not placed on the page the
    /// request named, rather than "the requested resource does not exist", which was indistinguishable from
    /// the module itself being unknown. The distinction is worth having precisely because the placement is
    /// now SELECTED by the request: a caller that named the wrong page needs to be told that, and the sibling
    /// fact below asserts the same answer for a module that is placed, just not there.
    /// </remarks>
    [Fact]
    public async Task UpdateModule_RefusesWhenTheModuleSitsNowhere()
    {
        Harness harness = Harness.Ready();
        harness.LookupModule = StoredModule();
        harness.PlacementsByModuleId.Clear();

        Result<ModuleDetailDto?> outcome = await harness.Service
            .UpdateModuleAsync(PortalId, ModuleId, ValidUpdateRequest(), CancellationToken.None);

        outcome.IsFailure.Should().BeTrue();
        outcome.Reason!.Code.Should().Be(PlacementNotFoundCode);
        outcome.Reason!.Message.Should()
            .Be($"Module {ModuleId} is not placed on page {TabId} in portal {PortalId}.");
        harness.UnitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    /// <summary>
    /// The placement amended is the one on the page the REQUEST names, not the one with the lowest
    /// identifier.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    /// <remarks>
    /// <para>
    /// MIGRATION: THIS IS THE FACT THAT WOULD HAVE CAUGHT THE DEFECT. <c>UpdateModuleRequest.TabId</c> is
    /// documented as required and as the key identifying WHICH placement is being updated, and
    /// <c>ModuleMappings.ApplyUpdate</c> documents that it deliberately does not assign the value because the
    /// service resolves the placement first - yet the service resolved the placement with the lowest
    /// identifier and never read the member. For a module on one page the two agree by accident, which is why
    /// no existing fact revealed it; this one seeds the module on TWO pages and names the SECOND, so the two
    /// answers differ.
    /// </para>
    /// <para>
    /// The old behaviour was worse than an ignored field. A caller editing the instance on page two saw its
    /// submission accepted while page one was silently rewritten, and the response described the placement it
    /// had not addressed - so the response could not be used to detect the substitution either. Both
    /// placements are inspected below for that reason: the addressed one must carry the change and the other
    /// must be untouched, because asserting only the first would pass just as happily if BOTH had been
    /// written.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task UpdateModule_AmendsThePlacementOnThePageTheRequestNames()
    {
        Harness harness = Harness.Ready();

        Module module = harness.LookupModule!;
        TabModule first = module.TabModules.Single();
        TabModule second = Placement(OtherTabModuleId, SecondTabId);
        module.TabModules.Add(second);

        first.TabModuleId.Should().BeLessThan(
            second.TabModuleId,
            "the withdrawn behaviour picked the lowest identifier, so the addressed placement must not be it");

        // Seeded so the assertion below distinguishes "unchanged" from "cleared". The stored value starts
        // null on this fixture, and null is also what a cleared column holds, so a fact comparing against the
        // initial null would pass even if the write had reached the wrong placement.
        first.IconFile = "first.gif";

        UpdateModuleRequest request = ValidUpdateRequest();
        request.TabId = SecondTabId;
        request.ModuleTitle = "Renamed on the second page";
        request.IconFile = "second.gif";

        Result<ModuleDetailDto?> outcome = await harness.Service
            .UpdateModuleAsync(PortalId, ModuleId, request, CancellationToken.None);

        outcome.IsSuccess.Should().BeTrue();

        second.IconFile.Should().Be(
            "second.gif",
            "the placement on the page the request named is the one that must change");
        first.IconFile.Should().Be(
            "first.gif",
            "the placement the request did not name must be left exactly as it was");

        outcome.Value!.TabModuleId.Should().Be(
            second.TabModuleId,
            "the response must describe the placement the caller addressed, or it cannot be used to detect a "
            + "substitution");
        outcome.Value!.TabId.Should().Be(SecondTabId);
    }

    /// <summary>
    /// A module that exists and is placed, but not on the page named, is refused rather than amended
    /// elsewhere.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    /// <remarks>
    /// The other half of the selection rule. Falling back to another placement is exactly the behaviour being
    /// removed, so the fallback's absence is asserted directly: the module has a placement, the request names
    /// a different page, and nothing is written.
    /// </remarks>
    [Fact]
    public async Task UpdateModule_RefusesAPageTheModuleIsNotPlacedOn()
    {
        Harness harness = Harness.Ready();

        UpdateModuleRequest request = ValidUpdateRequest();
        request.TabId = AdminTabId;
        request.ModuleTitle = "Should not be stored";

        Result<ModuleDetailDto?> outcome = await harness.Service
            .UpdateModuleAsync(PortalId, ModuleId, request, CancellationToken.None);

        outcome.IsFailure.Should().BeTrue();
        outcome.Reason!.Code.Should().Be(PlacementNotFoundCode);
        harness.LookupModule!.ModuleTitle.Should().Be(
            ModuleTitle,
            "a refused selection must leave the module row alone as well as the placement");
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

        // The stored pane is captured before the update so the assertion below can prove it SURVIVES.
        string storedPane = placement.PaneName;

        UpdateModuleRequest request = ValidUpdateRequest();
        request.ModuleTitle = "Renamed";
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
        // MIGRATION: the pane is excluded from UpdateModuleRequest as Web Forms pane-layout state, so the
        // update must PRESERVE the stored value rather than clear it. Its column is NOT NULL, so clearing it
        // would fail the write outright.
        placement.PaneName.Should().Be(storedPane);
        placement.ModuleOrder.Should().Be(8);
        placement.CacheTime.Should().Be(45);
        placement.IconFile.Should().Be("changed.gif");
        placement.Visibility.Should().Be(ModuleVisibility.None);
        placement.DisplayTitle.Should().BeFalse();
    }

    /// <summary>
    /// Naming a page the module is not placed on is REFUSED, and nothing is moved.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    /// <remarks>
    /// <para>
    /// MIGRATION: DIVERGENCE, and a deliberate one. The legacy screen offered a page picker whose change
    /// performed a move - a new placement appended on the destination, its scoped settings copied and the
    /// source removed. This endpoint does not reproduce that: <c>UpdateModuleRequest.TabId</c> SELECTS the
    /// placement being edited, so a value naming another page is a caller error rather than an instruction,
    /// and the request is refused with <c>module.placement_not_found</c>. Moving a placement is a separate
    /// operation on a separate contract, and inferring it from an edit is what made an earlier revision
    /// rewrite whichever placement happened to have the lowest identifier.
    /// </para>
    /// <para>
    /// Asserted as a REFUSAL rather than as a no-op, because a silent no-op would leave the caller believing
    /// the move had happened. Nothing is added, nothing is removed and nothing is committed.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task UpdateModule_WhenTheNamedPageHoldsNoPlacement_RefusesWithoutMovingAnything()
    {
        Harness harness = Harness.Ready();
        TabModule source = harness.LookupModule!.TabModules.Single();
        source.CacheTime = 120;
        source.IconFile = "source.gif";
        harness.PlacementSettingsByTabModuleId[TabModuleId] =
        [
            new TabModuleSetting
            {
                TabModuleId = TabModuleId,
                SettingName = "theme",
                SettingValue = "legacy",
            },
        ];

        UpdateModuleRequest request = ValidUpdateRequest();
        request.TabId = SecondTabId;
        request.CacheTime = 300;
        request.IconFile = "moved.gif";

        Result<ModuleDetailDto?> outcome = await harness.Service
            .UpdateModuleAsync(PortalId, ModuleId, request, CancellationToken.None);

        outcome.IsFailure.Should().BeTrue();
        outcome.Reason!.Code.Should().Be(PlacementNotFoundCode);

        source.CacheTime.Should().Be(120, "the placement the caller did not address is untouched");
        source.IconFile.Should().Be("source.gif");
        harness.AddedPlacements.Should().BeEmpty();
        harness.RemovedPlacements.Should().BeEmpty();
        harness.AddedPlacementSettings.Should().BeEmpty();
        harness.RemovedPlacementSettings.Should().BeEmpty();
        harness.UnitOfWork.Verify(
            unit => unit.SaveChangesAsync(It.IsAny<CancellationToken>()),
            Times.Never);
    }

    /// <summary>
    /// A selected page owned by another tenant is refused before either row is changed.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    /// <remarks>
    /// The refusal is the same one a page of this tenant that holds no placement receives, and deliberately
    /// so: the selection is resolved by module AND page together, so a page this module is not on and a page
    /// this portal does not own are the same miss. Answering them identically also keeps the response from
    /// revealing whether another tenant's page exists.
    /// </remarks>
    [Fact]
    public async Task UpdateModule_WhenTheSelectedPageIsOutsideThePortal_IsRefused()
    {
        Harness harness = Harness.Ready();
        harness.TabsById[SecondTabId] = new Tab
        {
            TabId = SecondTabId,
            PortalId = OtherPortalId,
            TabName = "Foreign",
        };

        UpdateModuleRequest request = ValidUpdateRequest();
        request.TabId = SecondTabId;

        Result<ModuleDetailDto?> outcome = await harness.Service
            .UpdateModuleAsync(PortalId, ModuleId, request, CancellationToken.None);

        outcome.IsFailure.Should().BeTrue();
        outcome.Reason!.Code.Should().Be(PlacementNotFoundCode);
        harness.AddedPlacements.Should().BeEmpty();
        harness.RemovedPlacements.Should().BeEmpty();
        harness.UnitOfWork.Verify(
            unit => unit.SaveChangesAsync(It.IsAny<CancellationToken>()),
            Times.Never);
    }

    /// <summary>
    /// A request that omits the cache lifetime stores zero, disabling caching, because this endpoint replaces
    /// rather than patches and zero is a real value rather than an absent one.
    /// </summary>
    /// <remarks>
    /// MIGRATION: 5.5 - this asserts the LEGACY behaviour, which is the opposite of the intuitive
    /// expectation. The legacy save read the cache-time box and stored a parsed integer when it was non-empty
    /// and LITERALLY ZERO when it was empty, so a blank field disabled caching rather than preserving the
    /// stored lifetime. CacheTime is consequently a non-nullable integer with no "unspecified" state to
    /// exempt: treating zero as unset would silently enable caching on a module the caller asked not to
    /// cache. Callers wanting to retain a lifetime must send it, which is the documented consequence of
    /// full-replacement semantics.
    /// </remarks>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task UpdateModule_StoresZeroCacheTimeWhenNoneWasAsked()
    {
        Harness harness = Harness.Ready();
        TabModule placement = harness.LookupModule!.TabModules.Single();
        placement.CacheTime = 900;

        UpdateModuleRequest request = ValidUpdateRequest();

        await harness.Service.UpdateModuleAsync(PortalId, ModuleId, request, CancellationToken.None);

        placement.CacheTime.Should().Be(0);
    }

    /// <summary>
    /// An update carrying a negative cache lifetime stores it exactly as submitted, matching the creation
    /// path and the legacy screen.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    /// <remarks>
    /// The update counterpart of the creation assertion, present because the clamp that was removed existed
    /// on BOTH projections and a fix applied to one of them would leave the two paths disagreeing about the
    /// same column.
    /// </remarks>
    [Fact]
    public async Task UpdateModule_StoresANegativeCacheTimeVerbatim()
    {
        Harness harness = Harness.Ready();
        TabModule placement = harness.LookupModule!.TabModules.Single();
        placement.CacheTime = 900;

        UpdateModuleRequest request = ValidUpdateRequest();
        request.CacheTime = -30;

        await harness.Service.UpdateModuleAsync(PortalId, ModuleId, request, CancellationToken.None);

        placement.CacheTime.Should().Be(-30);
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
    /// An update that appends resolves against the placement's own pane and never stores the instruction.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    /// <remarks>
    /// <para>
    /// The update contract initialises the position to the append instruction just as the create contract
    /// does, which under full-replacement semantics means an update that says nothing about position MOVES
    /// the module to the bottom of its pane. That is the documented contract; what must not happen is the
    /// instruction being written as though it were a position.
    /// </para>
    /// <para>
    /// The module being moved is the only one in its pane here, so the answer is the first position. That
    /// is the LEGACY answer and not an accident of ordering: the legacy update wrote the row carrying -1
    /// and only then called the resolver, whose pane read selected from the placement table and therefore
    /// included the row it had just written. With nothing else in the pane the greatest position it could
    /// see was that -1, and -1 + 2 = 1. Resolving before the write reaches the same number by seeding the
    /// running maximum with the same sentinel, so a module moved to the bottom of a pane it already has to
    /// itself stays where it is rather than drifting upward by the step on every save.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task UpdateModule_WhenAppending_ResolvesAgainstThePlacementsOwnPane()
    {
        Harness harness = Harness.Ready();
        UpdateModuleRequest request = ValidUpdateRequest();

        request.ModuleOrder.Should().Be(-1, "an update that omits the position asks to append");

        Result<ModuleDetailDto?> outcome = await harness.Service
            .UpdateModuleAsync(PortalId, ModuleId, request, CancellationToken.None);

        outcome.IsSuccess.Should().BeTrue();
        harness.PlacementsById[TabModuleId]!.ModuleOrder.Should().Be(1);
    }

    /// <summary>
    /// An update that appends steps past the other modules sharing the pane.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    /// <remarks>
    /// This is the case that distinguishes a resolver from a constant. With a neighbour occupying the pane
    /// at a higher position, the bottom of the pane is past that neighbour rather than at the first
    /// position, and the module moves. An implementation that always answered one - which the single-module
    /// fact above cannot tell apart from a correct one - fails here.
    /// </remarks>
    [Fact]
    public async Task UpdateModule_WhenAppendingBelowANeighbour_StepsPastIt()
    {
        const int neighbourModuleId = 12;
        const int neighbourTabModuleId = 99;
        const int neighbourPosition = 6;

        Harness harness = Harness.Ready();
        harness.PlacementsByModuleId[neighbourModuleId] =
        [
            new TabModule
            {
                TabModuleId = neighbourTabModuleId,
                TabId = TabId,
                ModuleId = neighbourModuleId,
                PaneName = DefaultPaneName,
                ModuleOrder = neighbourPosition,
                CacheTime = 0,
                Visibility = ModuleVisibility.Maximized,
                DisplayTitle = true,
            },
        ];

        await harness.Service
            .UpdateModuleAsync(PortalId, ModuleId, ValidUpdateRequest(), CancellationToken.None);

        harness.PlacementsById[TabModuleId]!.ModuleOrder.Should().Be(neighbourPosition + 2);
    }

    /// <summary>
    /// An update that both appends and asks for every page appends on each page's own pane.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    /// <remarks>
    /// This is the path on which the legacy application was itself inconsistent. Its copy-to-another-page
    /// routine passed the append instruction to the insert under the comment "Add a copy of the module to
    /// the bottom of the Pane for the new Tab" and then, unlike the add and update paths, never called the
    /// resolver - so the instruction stayed in the row. The stated intent is honoured here and the
    /// omission is not reproduced, which is why the new placement carries the first position of an empty
    /// pane rather than either the instruction or the addressed page's position.
    /// </remarks>
    [Fact]
    public async Task UpdateModule_WhenAppendingEverywhere_AppendsToEachPagesOwnPane()
    {
        Harness harness = Harness.Ready();
        harness.LookupModule!.AllTabs = false;
        UpdateModuleRequest request = ValidUpdateRequest();
        request.AllTabs = true;

        await harness.Service.UpdateModuleAsync(PortalId, ModuleId, request, CancellationToken.None);

        TabModule added = harness.AddedPlacements.Should().ContainSingle().Which;
        added.TabId.Should().Be(SecondTabId);
        added.ModuleOrder.Should().Be(
            1,
            "the second page's content pane is empty, so the bottom of it is the first position - not the "
            + "position computed for the addressed page, and certainly not the instruction");
    }

    /// <summary>
    /// An update that names a position copies that position onto every new placement.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    /// <remarks>
    /// Stated beside the appending case so the two rules are visible together. When the caller names a
    /// position there is nothing to resolve and the template's position is what copying a template means,
    /// so this fact pins that the append handling did not quietly change the named-position behaviour.
    /// </remarks>
    [Fact]
    public async Task UpdateModule_WhenNamingAPositionEverywhere_CopiesThatPosition()
    {
        Harness harness = Harness.Ready();
        harness.LookupModule!.AllTabs = false;
        UpdateModuleRequest request = ValidUpdateRequest();
        request.AllTabs = true;
        request.ModuleOrder = 8;

        await harness.Service.UpdateModuleAsync(PortalId, ModuleId, request, CancellationToken.None);

        harness.PlacementsById[TabModuleId]!.ModuleOrder.Should().Be(8);
        harness.AddedPlacements.Should().ContainSingle().Which.ModuleOrder.Should().Be(8);
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
        request.SetAsDefaultSettings = true;

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
        request.SetAsDefaultSettings = true;

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
        request.SetAsDefaultSettings = true;

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
        request.ApplyToAllModules = true;
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
        request.ApplyToAllModules = true;
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
        request.SetAsDefaultSettings = true;

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
    /// Recycling a whole module is recorded under the legacy removal event, after it commits.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    /// <remarks>
    /// The legacy recycle bin audited a module removal as <c>MODULE_DELETED</c>
    /// (<c>RecycleBin.ascx.vb:L156</c>) and this boundary recorded nothing at all, which left the one
    /// operation in this service that destroys a caller's work as the only one with no trail. The page that
    /// audited it is excluded; the deletion it audited is not, and this is where the deletion happens.
    /// </remarks>
    [Fact]
    public async Task DeleteModule_RecordsTheRemovalUnderTheLegacyEventName()
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

        AuditEvent record = harness.AuditRecords.Should().ContainSingle().Subject;
        record.EventName.Should().Be(ModuleDeletedEventName);
        record.Outcome.Should().Be(AuditOutcome.Succeeded);
        record.PortalId.Should().Be(PortalId);
        record.ResourceId.Should().Be(ModuleId.ToString(CultureInfo.InvariantCulture));
        record.Properties["Operation"].Should().Be("Recycle");
        record.Properties["TabModuleId"].Should().BeNull(
            "no placement was addressed, so there is none to name");
        record.Properties["AffectedTabCount"].Should().Be("2");
    }

    /// <summary>
    /// Withdrawing one placement is recorded as a removal too, and names the placement that went.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    /// <remarks>
    /// The same event, because the same thing happened to a caller's work; the <c>Operation</c> fact is what
    /// tells the two apart, so an operator reading the trail can see whether the module survived.
    /// </remarks>
    [Fact]
    public async Task DeleteModule_RecordsWhichPlacementWasWithdrawn()
    {
        Harness harness = Harness.Ready();

        Result outcome = await harness.Service
            .DeleteModuleAsync(PortalId, ModuleId, TabModuleId, CancellationToken.None);

        outcome.IsSuccess.Should().BeTrue();

        AuditEvent record = harness.AuditRecords.Should().ContainSingle().Subject;
        record.EventName.Should().Be(ModuleDeletedEventName);
        record.Properties["Operation"].Should().Be("RemovePlacement");
        record.Properties["TabModuleId"].Should().Be(TabModuleId.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// A declared version that is not a plain version string is replaced rather than recorded.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    /// <remarks>
    /// <para>
    /// The version is read from an attribute of a CALLER-SUPPLIED document, so nothing about it is validated
    /// by the request contract or bounded by the schema. Recording it verbatim let a caller put a secret, a
    /// personal identifier, control text or an unbounded high-cardinality value into the audit trail simply by
    /// declaring it as a version.
    /// </para>
    /// <para>
    /// It is REPLACED rather than dropped, so the record still says the document declared something and that
    /// what it declared was not usable - dropping it would make a hostile document indistinguishable from one
    /// that declared no version at all. And it is replaced rather than stripped of its unwelcome characters,
    /// because stripping reshapes a hostile value into something that reads as authentic provenance.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task ImportModule_ReplacesADeclaredVersionThatIsNotAVersion()
    {
        Harness harness = Harness.Ready();

        Result outcome = await harness.Service.ImportModuleAsync(
            PortalId,
            new ModuleImportRequest
            {
                ModuleId = ModuleId,
                Content = $"<content type=\"{PackageName}\" version=\"AKIA-secret; DROP TABLE Modules\">"
                    + "payload</content>",
            },
            CancellationToken.None);

        outcome.IsSuccess.Should().BeTrue();

        AuditEvent record = harness.AuditRecords.Should().ContainSingle().Subject;
        record.Properties["Version"].Should().Be("(unusable)");
        record.Properties.Values
            .Where(value => value is not null)
            .Should().NotContain(value => value!.Contains("AKIA-secret", StringComparison.Ordinal));
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
    /// Security-owned names never cross the generic open-key read contract.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task GetModuleSettings_RedactsSecurityOwnedNames()
    {
        Harness harness = Harness.Ready();
        harness.SettingsByModuleId[ModuleId] =
        [
            new ModuleSetting { ModuleId = ModuleId, SettingName = "editor", SettingValue = "rich" },
            new ModuleSetting
            {
                ModuleId = ModuleId,
                SettingName = "Security_EmailValidation",
                SettingValue = "sensitive-expression",
            },
        ];
        harness.PlacementSettingsByTabModuleId[TabModuleId] =
        [
            new TabModuleSetting
            {
                TabModuleId = TabModuleId,
                SettingName = "Column_Email",
                SettingValue = bool.TrueString,
            },
        ];

        Result<ModuleSettingsDto?> outcome = await harness.Service
            .GetModuleSettingsAsync(PortalId, ModuleId, null, CancellationToken.None);

        outcome.IsSuccess.Should().BeTrue();
        outcome.Value!.ModuleSettings.Should().ContainSingle().Which.Key.Should().Be("editor");
        outcome.Value.TabModuleSettings.Should().BeEmpty();
    }

    /// <summary>
    /// Administrative modules are configured only through typed privileged endpoints.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task GetModuleSettings_RefusesAnAdministrativeModule()
    {
        Harness harness = Harness.Ready();
        harness.Packages[DesktopModuleId]!.IsAdmin = true;

        Result<ModuleSettingsDto?> outcome = await harness.Service
            .GetModuleSettingsAsync(PortalId, ModuleId, null, CancellationToken.None);

        outcome.IsFailure.Should().BeTrue();
        outcome.Error!.Code.Should().Be(SettingsProtectedCode);
        harness.Modules.Verify(
            repository => repository.GetModuleSettingsAsync(
                It.IsAny<int>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
    }

    /// <summary>
    /// Storing settings requires both maps, even when one of them is to be left empty, and an absent map
    /// is REFUSED rather than thrown on.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    /// <remarks>
    /// This test previously asserted an argument-null throw, and the assertion was changed because the
    /// behaviour it pinned was the defect. Both members are non-nullable reference types carrying an
    /// initialiser on the request contract, which makes null look unreachable - but an initialiser only
    /// runs when the deserialiser does not assign, and a body carrying an explicit null assigns over it.
    /// Throwing therefore turned a syntactically valid request body into a server fault. The refusal is
    /// asserted rather than merely the absence of a throw, so a future change that silently accepted an
    /// absent map and wrote nothing would still fail here.
    /// </remarks>
    [Fact]
    public async Task UpdateModuleSettings_RefusesAnAbsentMap()
    {
        Harness harness = Harness.Ready();
        var empty = new Dictionary<string, string>();

        Result missingModuleMap = await harness.Service.UpdateModuleSettingsAsync(
            PortalId, ModuleId, null, null!, empty, CancellationToken.None);

        Result missingPlacementMap = await harness.Service.UpdateModuleSettingsAsync(
            PortalId, ModuleId, null, empty, null!, CancellationToken.None);

        missingModuleMap.IsSuccess.Should().BeFalse("an absent module settings map cannot be stored");
        missingModuleMap.Error!.Code.Should().Be("module.setting_invalid");

        missingPlacementMap.IsSuccess.Should().BeFalse("an absent placement settings map cannot be stored");
        missingPlacementMap.Error!.Code.Should().Be("module.setting_invalid");
    }

    /// <summary>
    /// A settings submission carrying more entries than either scope permits is refused, and nothing is
    /// written.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    /// <remarks>
    /// The per-entry bounds on a setting's name and value were already enforced, but nothing bounded the
    /// NUMBER of entries, and the two limits multiply: a body well inside the request size limit could
    /// carry tens of thousands of short settings, each becoming a tracked entity and a row in one
    /// transaction. The count is asserted one past the bound rather than at some arbitrary large number,
    /// so the test pins the boundary itself.
    /// </remarks>
    [Fact]
    public async Task UpdateModuleSettings_RefusesMoreEntriesThanAScopePermits()
    {
        const int perScopeMaximum = 250;
        Harness harness = Harness.Ready();

        var oversized = new Dictionary<string, string>();
        for (int index = 0; index <= perScopeMaximum; index++)
        {
            oversized[FormattableString.Invariant($"setting{index}")] = "value";
        }

        Result outcome = await harness.Service.UpdateModuleSettingsAsync(
            PortalId,
            ModuleId,
            null,
            oversized,
            new Dictionary<string, string>(),
            CancellationToken.None);

        outcome.IsSuccess.Should().BeFalse("a scope carrying more than the permitted entries is refused");
        outcome.Error!.Code.Should().Be("module.setting_invalid");
    }

    /// <summary>
    /// A settings submission whose two scopes are individually permissible but jointly excessive is
    /// refused.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    /// <remarks>
    /// This is the case the per-scope bound alone does not catch, and it is the one that matters for the
    /// size of the single transaction the service opens. Both maps below sit inside the per-scope bound of
    /// 250 and exceed the aggregate bound of 400 between them, so a pass here would mean the aggregate
    /// rule had been lost.
    /// </remarks>
    [Fact]
    public async Task UpdateModuleSettings_RefusesMoreEntriesThanTheTwoScopesPermitTogether()
    {
        Harness harness = Harness.Ready();

        static Dictionary<string, string> Build(string prefix, int count)
        {
            var map = new Dictionary<string, string>();
            for (int index = 0; index < count; index++)
            {
                map[FormattableString.Invariant($"{prefix}{index}")] = "value";
            }

            return map;
        }

        Result outcome = await harness.Service.UpdateModuleSettingsAsync(
            PortalId,
            ModuleId,
            null,
            Build("module", 220),
            Build("placement", 220),
            CancellationToken.None);

        outcome.IsSuccess.Should().BeFalse("the two scopes together exceed the aggregate bound");
        outcome.Error!.Code.Should().Be("module.setting_invalid");
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
    /// Generic module editors cannot alter an administrative module's settings.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task UpdateModuleSettings_RefusesAnAdministrativeModule()
    {
        Harness harness = Harness.Ready();
        harness.Packages[DesktopModuleId]!.IsAdmin = true;

        Result outcome = await harness.Service.UpdateModuleSettingsAsync(
            PortalId,
            ModuleId,
            null,
            new Dictionary<string, string> { ["Security_EmailValidation"] = ".*" },
            new Dictionary<string, string>(),
            CancellationToken.None);

        outcome.IsFailure.Should().BeTrue();
        outcome.Error!.Code.Should().Be(SettingsProtectedCode);
        harness.UnitOfWork.Verify(
            work => work.SaveChangesAsync(It.IsAny<CancellationToken>()),
            Times.Never);
    }

    /// <summary>
    /// Security and user-list column namespaces are reserved even on an ordinary content module, and
    /// omission from a generic replacement cannot erase an existing protected row.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task UpdateModuleSettings_RejectsAndPreservesSecurityOwnedNames()
    {
        Harness harness = Harness.Ready();
        var protectedSetting = new ModuleSetting
        {
            ModuleId = ModuleId,
            SettingName = "Security_EmailValidation",
            SettingValue = "original",
        };
        harness.SettingsByModuleId[ModuleId] = [protectedSetting];

        Result rejected = await harness.Service.UpdateModuleSettingsAsync(
            PortalId,
            ModuleId,
            null,
            new Dictionary<string, string> { ["security_emailvalidation"] = "replacement" },
            new Dictionary<string, string>(),
            CancellationToken.None);

        rejected.IsFailure.Should().BeTrue();
        rejected.Error!.Code.Should().Be(SettingsProtectedCode);
        protectedSetting.SettingValue.Should().Be("original");
        harness.RemovedSettings.Should().BeEmpty();

        Result ordinaryReplacement = await harness.Service.UpdateModuleSettingsAsync(
            PortalId,
            ModuleId,
            null,
            new Dictionary<string, string> { ["editor"] = "rich" },
            new Dictionary<string, string>(),
            CancellationToken.None);

        ordinaryReplacement.IsSuccess.Should().BeTrue();
        harness.RemovedSettings.Should().BeEmpty("generic replacement must preserve protected rows");
        protectedSetting.SettingValue.Should().Be("original");
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
    // MIGRATION: both scopes are bounded at 2000, not one at 256 and the other at 2000. The module
    // table's baseline column was nvarchar(256) (01.00.00 line 353), but 01.00.08 lines 6248-6286
    // rebuild the table with SettingValue nvarchar(2000) NOT NULL (line 6256) and nothing narrows it
    // again; the terminal AddModuleSetting and UpdateModuleSetting procedures declare
    // @SettingValue nvarchar(2000) (01.00.08 line 6295, 02.00.00 lines 4147 and 4171). Expecting 256
    // here would pin a bound that refuses values the legacy
    // application accepted, which Minimal Change Clause item 3 forbids.
    [InlineData("module", "long-value", "The value of the module setting \"editor\" exceeds 2000 characters.")]
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
                target["editor"] = new string('v', 2001);
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
            d => d.GetDesktopModuleByIdAsync(DesktopModuleId, It.IsAny<CancellationToken>()),
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
    /// A definition the portal may instantiate is returned by its own identifier.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    /// <remarks>
    /// Restores the legacy per-definition read that <c>ModuleDefinitionController</c> offered. The legacy
    /// member answered from the whole installation; this one answers within a portal, which is the
    /// deliberate narrowing recorded on the contract.
    /// </remarks>
    [Fact]
    public async Task GetModuleDefinition_ReturnsADefinitionThePortalMayInstantiate()
    {
        Harness harness = Harness.Ready();

        Result<ModuleDefinitionDto?> outcome = await harness.Service
            .GetModuleDefinitionAsync(PortalId, ModuleDefinitionId, CancellationToken.None);

        outcome.IsSuccess.Should().BeTrue();
        outcome.Value.Should().NotBeNull();
        outcome.Value!.ModuleDefId.Should().Be(ModuleDefinitionId);
        outcome.Value.FriendlyName.Should().Be(FriendlyName);
    }

    /// <summary>
    /// A definition identifier that names nothing is reported as absent rather than as a failure.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    /// <remarks>
    /// A successful outcome carrying no value is how this solution says "asked, and it is not there", which
    /// the shared translator answers as 404. It is a different answer from a failed outcome and the two are
    /// never collapsed (AAP Rule T7).
    /// </remarks>
    [Fact]
    public async Task GetModuleDefinition_ReportsAnUnknownDefinitionAsAbsent()
    {
        Harness harness = Harness.Ready();

        Result<ModuleDefinitionDto?> outcome = await harness.Service
            .GetModuleDefinitionAsync(PortalId, 987654, CancellationToken.None);

        outcome.IsSuccess.Should().BeTrue("an unknown identifier is absence, not a failure");
        outcome.Value.Should().BeNull();
    }

    /// <summary>
    /// A definition the portal is not entitled to instantiate is reported as absent, not refused.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    /// <remarks>
    /// <para>
    /// This is the load-bearing property of answering these reads by narrowing the portal's catalogue
    /// rather than by reading the definition table directly by key. The catalogue read is portal-scoped and
    /// so applies the entitlement rule; a direct read by key would apply nothing at all, and
    /// re-implementing the rule beside it would create a second copy that would eventually disagree with
    /// the first.
    /// </para>
    /// <para>
    /// Reporting ABSENCE rather than refusal is also deliberate: a caller must not be able to tell "no such
    /// definition" from "not yours", because the difference between those two answers is itself a fact
    /// about another tenant's installation.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task GetModuleDefinition_ReportsADefinitionOutsideTheEntitlementAsAbsent()
    {
        Harness harness = Harness.Ready();

        // The portal-scoped catalogue read is what applies the entitlement rule, so a definition the portal
        // is not entitled to simply does not appear in what that read returns.
        harness.DefinitionCatalogue.Clear();

        Result<IReadOnlyList<ModuleDefinitionDto>> catalogue = await harness.Service
            .ListModuleDefinitionsAsync(PortalId, CancellationToken.None);
        catalogue.Value.Should().BeEmpty("the portal-scoped read did not offer it");

        Result<ModuleDefinitionDto?> outcome = await harness.Service
            .GetModuleDefinitionAsync(PortalId, ModuleDefinitionId, CancellationToken.None);

        outcome.IsSuccess.Should().BeTrue();
        outcome.Value.Should().BeNull(
            "a definition the portal cannot instantiate is indistinguishable from one that does not exist");
    }

    /// <summary>
    /// The definitions of one package are returned, and only that package's.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task ListDesktopModuleDefinitions_ReturnsOnlyThatPackagesDefinitions()
    {
        Harness harness = Harness.Ready();
        harness.DefinitionCatalogue.Clear();
        harness.DefinitionCatalogue.AddRange(
        [
            Definition(1, "First"),
            Definition(2, "Second"),
        ]);

        Result<IReadOnlyList<ModuleDefinitionDto>> outcome = await harness.Service
            .ListDesktopModuleDefinitionsAsync(PortalId, DesktopModuleId, CancellationToken.None);

        outcome.IsSuccess.Should().BeTrue();
        outcome.Value.Select(row => row.ModuleDefId).Should().Equal(new[] { 1, 2 });
        outcome.Value.Should().OnlyContain(row => row.DesktopModuleId == DesktopModuleId);
    }

    /// <summary>
    /// A package identifier that names nothing yields an empty sequence rather than a failure.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task ListDesktopModuleDefinitions_ReportsAnUnknownPackageAsEmpty()
    {
        Harness harness = Harness.Ready();

        Result<IReadOnlyList<ModuleDefinitionDto>> outcome = await harness.Service
            .ListDesktopModuleDefinitionsAsync(PortalId, 987654, CancellationToken.None);

        outcome.IsSuccess.Should().BeTrue();
        outcome.Value.Should().BeEmpty("a catalogue read answers with a sequence, never with an absence");
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
    /// The exported content is wrapped in the legacy document, declaration and all, with the payload
    /// embedded verbatim and the type attribute carrying the SANITISED package name.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    /// <remarks>
    /// <para>
    /// The literal below is deliberately spelled out rather than computed, because it IS the wire format
    /// and a computed expectation would agree with whatever the implementation did. Every part of it is
    /// measured against <c>Website/admin/Modules/Export.ascx.vb</c> L157-L165: the declaration, including
    /// the space before its closing bracket pair; the <c>type</c> attribute holding
    /// <c>CleanName(objModule.ModuleName)</c>, which is why <c>DNN_HTML</c> appears here as
    /// <c>DNNHTML</c>; the <c>version</c> attribute; and the module's payload placed between the tags
    /// exactly as it was handed over.
    /// </para>
    /// <para>
    /// MIGRATION: AN EARLIER REVISION ASSERTED A DOUBLY-ESCAPED PAYLOAD AND A RAW TYPE NAME, and it is
    /// worth spelling out what that document looked like, because the expectation itself was the record of
    /// the defect. It read
    /// <c>&lt;content type="DNN_HTML" version="04.09.00"&gt;&amp;amp;lt;item&amp;amp;gt;...&lt;/content&gt;</c>:
    /// the service HTML-encoded the payload, turning <c>&lt;</c> into <c>&amp;lt;</c>, and the XML writer
    /// then escaped that ampersand into <c>&amp;amp;</c>. Any reader but this service recovered
    /// <c>&amp;lt;item&amp;gt;one&amp;lt;/item&amp;gt;</c> instead of <c>&lt;item&gt;one&lt;/item&gt;</c>,
    /// and the raw type name would have been refused outright by the legacy importer as naming another
    /// module. Both came from the PORTAL TEMPLATE writer at <c>ModuleController.vb</c> L244, which is a
    /// different format with a different reader; neither belongs to this workflow.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task ExportModule_WrapsTheExportedContentInTheLegacyDocument()
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
            "<?xml version=\"1.0\" encoding=\"utf-8\" ?>"
            + $"<content type=\"DNNHTML\" version=\"{PackageVersion}\">"
            + "<item>one</item></content>");
        harness.BusinessControllers.Verify(
            f => f.ExportModuleContentAsync(BusinessController, ModuleId, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    /// <summary>
    /// The document this endpoint writes is the one the legacy IMPORTER would have accepted: its type
    /// attribute is the sanitised module name, so a round trip through the legacy rule succeeds.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    /// <remarks>
    /// The sibling fact above pins the whole document as a literal; this one pins the single property that
    /// decides interoperability, and pins it against the SANITISER rather than against a spelled-out value.
    /// The two are complementary: a literal proves what is written today, and this proves the value is
    /// derived from the module name by the same transformation the legacy importer applies before comparing.
    /// A package name whose punctuation the sanitiser strips is what makes the distinction observable, which
    /// is why the seeded name carries an underscore.
    /// </remarks>
    [Fact]
    public async Task ExportModule_NamesTheTypeWithTheSanitisedPackageName()
    {
        Harness harness = Harness.Ready();
        harness.ExportOutcome = Result<string?>.Success("<item>one</item>");

        Result<string> outcome = await harness.Service.ExportModuleAsync(
            PortalId,
            ModuleId,
            new ModuleExportRequest { FileName = "content.xml" },
            CancellationToken.None);

        outcome.IsSuccess.Should().BeTrue();

        PackageName.Should().Contain("_", "the sanitiser only becomes observable when there is punctuation");

        outcome.Value.Should().Contain(
            "type=\"DNNHTML\"",
            "the legacy importer compares the attribute against CleanName(ModuleName), so writing the raw "
            + "name makes every document this endpoint produces unreadable by a DotNetNuke 4.x installation");
        outcome.Value.Should().NotContain(
            $"type=\"{PackageName}\"",
            "the raw name is what the withdrawn revision wrote, and it is exactly what the legacy refuses");
    }

    /// <summary>
    /// A document this service exports is imported back as the byte-identical payload the module handed
    /// over.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    /// <remarks>
    /// <para>
    /// The payloads exercise the shapes the format has to carry without altering: an element, an entity
    /// reference that must survive AS a reference rather than being resolved and re-escaped, characters an
    /// XML writer would have escaped but a verbatim embedding must not touch, and nothing at all. Because
    /// neither side escapes anything, the guarantee is byte identity rather than a round trip through two
    /// cancelling transformations.
    /// </para>
    /// <para>
    /// MIGRATION: THE PAYLOAD <c>"a &amp; b &lt; c &gt; d"</c> USED TO BE A ROW HERE AND IS NOW ASSERTED AS
    /// A REFUSAL by the fact below. It is not well-formed XML, so the legacy exporter wrote a corrupt file
    /// and the legacy importer refused that file with "The file you selected does not contain a valid XML
    /// structure" - the content was never importable by the workflow this endpoint migrates. It appeared to
    /// round-trip here only because the withdrawn HTML encode and decode cancelled each other out inside
    /// this one service. The obligation the portability contract carries is to hand back XML, and a module
    /// that does not is now told so.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData("<item>one</item>")]
    [InlineData("already &amp; encoded &lt;tag&gt;")]
    [InlineData("quotes \" and ' apostrophes")]
    [InlineData("")]
    public async Task ExportThenImportModule_ReturnsThePayloadUnchanged(string payload)
    {
        Harness exporter = Harness.Ready();
        exporter.ExportOutcome = Result<string?>.Success(payload);

        Result<string> exported = await exporter.Service.ExportModuleAsync(
            PortalId,
            ModuleId,
            new ModuleExportRequest { FileName = "content.xml" },
            CancellationToken.None);

        exported.IsSuccess.Should().BeTrue();

        Harness importer = Harness.Ready();
        string? handedToTheModule = null;
        importer.BusinessControllers
            .Setup(f => f.ImportModuleContentAsync(
                It.IsAny<string?>(),
                It.IsAny<int>(),
                It.IsAny<string?>(),
                It.IsAny<string?>(),
                It.IsAny<int>(),
                It.IsAny<CancellationToken>()))
            .Callback<string?, int, string?, string?, int, CancellationToken>(
                (_, _, content, _, _, _) => handedToTheModule = content)
            .ReturnsAsync(Result.Success());

        Result outcome = await importer.Service.ImportModuleAsync(
            PortalId,
            new ModuleImportRequest { ModuleId = ModuleId, Content = exported.Value },
            CancellationToken.None);

        outcome.IsSuccess.Should().BeTrue();
        handedToTheModule.Should().Be(payload);
    }

    /// <summary>
    /// A module handing back content that is not well-formed XML is refused, and its content is not quoted
    /// back in the refusal.
    /// </summary>
    /// <param name="payload">Content the composed document cannot carry.</param>
    /// <returns>A task representing the assertion.</returns>
    /// <remarks>
    /// <para>
    /// The payload is embedded verbatim, exactly as <c>Website/admin/Modules/Export.ascx.vb</c> L157-L165
    /// embedded it, so a payload that is not well-formed makes the DOCUMENT not well-formed. The legacy
    /// wrote that file anyway and its importer refused it afterwards; the composed document is parsed here
    /// before it is returned, so the same content is refused at the point the operator can act on it.
    /// </para>
    /// <para>
    /// Both rows are faults in the MODULE rather than in the request, which is why the reason token is
    /// classified as a server fault: the caller asked correctly and can do nothing to fix the answer. The
    /// bare ampersand is the ordinary case; the C0 control character is the case no escaping in the XML
    /// specification can represent at all, so it would fail even if the payload were escaped.
    /// </para>
    /// <para>
    /// The message must not quote the payload, and it must not quote the parser's message either, because a
    /// parser message quotes the fragment it choked on. Export content is module data and may carry the
    /// portal's users' data, while a failure message is published verbatim as the problem detail.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData("a & b < c > d")]
    [InlineData("\u0001 portal-owned-content-marker")]
    public async Task ExportModule_WhenTheModuleReturnsContentThatIsNotXml_IsRefused(string payload)
    {
        Harness harness = Harness.Ready();
        harness.ExportOutcome = Result<string?>.Success(payload);

        Result<string> outcome = await harness.Service.ExportModuleAsync(
            PortalId,
            ModuleId,
            new ModuleExportRequest { FileName = "content.xml" },
            CancellationToken.None);

        outcome.IsFailure.Should().BeTrue();
        outcome.Reason!.Code.Should().Be(ExportFailedCode);
        outcome.Reason!.Message.Should().Be(
            $"Module {ModuleId} returned content that cannot be represented in an export document.");
        outcome.Reason!.Message.Should().NotContain("portal-owned-content-marker");
    }

    /// <summary>
    /// A document whose type attribute names another module is refused, and one naming this module by
    /// either of its two accepted names is admitted.
    /// </summary>
    /// <param name="declaredType">The value of the document's type attribute.</param>
    /// <param name="accepted">Whether the import must be admitted.</param>
    /// <returns>A task representing the assertion.</returns>
    /// <remarks>
    /// <para>
    /// MIGRATION: <c>Website/admin/Modules/Import.ascx.vb</c> L196-L197 -
    /// <c>If strType = CleanName(objModule.ModuleName) Or strType = CleanName(objModule.FriendlyName)</c>,
    /// otherwise "The import file specified is not the correct type for this module". THIS CHECK WAS
    /// PROMISED AND NOT PERFORMED: <c>ModuleImportRequest.FileName</c> documents at length that the legacy
    /// name-based check was deliberately re-sourced onto the type attribute so the refusal would survive,
    /// and nothing performed it - so a document belonging to another module was imported into this one
    /// without complaint, handing a module content it could not interpret.
    /// </para>
    /// <para>
    /// The rows cover each accepted spelling and each rejected one. The sanitised module name and the
    /// sanitised friendly name are the legacy's two accepted values; the RAW module name is accepted as
    /// well, because sanitising the submitted value too is what keeps documents this endpoint produced
    /// before the export fix was applied importable - a bounded widening, recorded on the service. A
    /// different module's name, an empty attribute and an absent attribute are all refused, and the absent
    /// case is the legacy's own behaviour: <c>GetAttribute</c> returns the empty string for a missing
    /// attribute, which matched neither name.
    /// </para>
    /// <para>
    /// The comparison is case-SENSITIVE, which is asserted by the lower-cased row. VB's default string
    /// comparison is binary, so the legacy refused a document whose type differed only in case; admitting
    /// it here would accept documents the legacy did not.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData("DNNHTML", true)]
    [InlineData(PackageName, true)]
    [InlineData("TextHTML", true)]
    [InlineData(FriendlyName, true)]
    [InlineData("dnnhtml", false)]
    [InlineData("DNN_Announcements", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public async Task ImportModule_ChecksTheDocumentTypeAgainstTheModulesOwnNames(
        string? declaredType,
        bool accepted)
    {
        Harness harness = Harness.Ready();

        string attribute = declaredType is null
            ? string.Empty
            : FormattableString.Invariant($" {"type"}=\"{declaredType}\"");

        Result outcome = await harness.Service.ImportModuleAsync(
            PortalId,
            new ModuleImportRequest
            {
                ModuleId = ModuleId,
                Content = FormattableString.Invariant($"<content{attribute}>one</content>"),
            },
            CancellationToken.None);

        if (accepted)
        {
            outcome.IsSuccess.Should().BeTrue(
                "the document names this module, by one of the four spellings the check admits");
            harness.ImportedPayload.Should().Be("one");

            return;
        }

        outcome.IsFailure.Should().BeTrue(
            "a document that does not name this module must be refused rather than handed to it");
        outcome.Reason!.Code.Should().Be(ContentTypeMismatchCode);
        outcome.Reason!.Message.Should().Contain(
            "does not match the target module package",
            "the refusal names the RULE rather than restating the submitted value, which is caller text");
        harness.ImportedPayload.Should().BeNull("nothing may reach the module once the type is refused");
        harness.UnitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    /// <summary>
    /// A package whose capability field still holds the legacy "not yet determined" sentinel is refused
    /// with a reason, which is what replaces the legacy deferred-import event-queue branch.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    /// <remarks>
    /// <c>ModuleController.vb</c> L422 tested the capability field against the integer sentinel -1 and
    /// parked the payload on a queue for replay after an application restart, because it could not discover
    /// a freshly installed module's capabilities mid-request. That queue subsystem is excluded, and
    /// resolving behaviour from a closed registered set makes the deferral pointless - so the sentinel
    /// produces a clear refusal the caller can retry rather than a silent parking they cannot observe.
    /// Nothing is handed to the module and nothing is recorded.
    /// </remarks>
    [Fact]
    public async Task ImportModule_RefusesAPackageWhoseCapabilitiesAreUndetermined()
    {
        Harness harness = Harness.Ready();
        harness.Packages[DesktopModuleId]!.SupportedFeatures = -1;

        Result outcome = await harness.Service.ImportModuleAsync(
            PortalId,
            new ModuleImportRequest { ModuleId = ModuleId, Content = TypedDocumentPrefix + ">one</content>" },
            CancellationToken.None);

        outcome.IsFailure.Should().BeTrue();
        outcome.Reason!.Code.Should().Be(NotPortableCode);

        harness.BusinessControllers.Verify(
            f => f.ImportModuleContentAsync(
                It.IsAny<string?>(),
                It.IsAny<int>(),
                It.IsAny<string?>(),
                It.IsAny<string?>(),
                It.IsAny<int>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
        harness.AuditRecords.Should().BeEmpty();
    }

    /// <summary>
    /// Content the module produces but that XML cannot carry is reported as a failure rather than escaping
    /// as an unhandled exception, and nothing is recorded on the audit trail.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    /// <remarks>
    /// A C0 control character has no representation in XML - no escaping in the specification encodes one -
    /// so the writer refuses it. The failure code's reason token is classified as a server fault, which is
    /// the correct reading: the caller submitted a valid request and cannot correct a module that returns
    /// unrepresentable content. The legacy path wrapped this in a <c>Catch</c> whose body was
    /// <c>'ignore errors</c> and produced a document with the content element silently missing; that
    /// swallow is deliberately not reproduced.
    /// </remarks>
    [Fact]
    public async Task ExportModule_ReportsContentThatCannotBeSerialised()
    {
        Harness harness = Harness.Ready();
        harness.ExportOutcome = Result<string?>.Success("before\u0001after");

        Result<string> outcome = await harness.Service.ExportModuleAsync(
            PortalId,
            ModuleId,
            new ModuleExportRequest { FileName = "content.xml" },
            CancellationToken.None);

        outcome.IsFailure.Should().BeTrue();
        outcome.Reason!.Code.Should().Be("module.export_failed");
        outcome.Reason!.Message.Should()
            .Be($"Module {ModuleId} returned content that cannot be represented in an export document.");

        // The module's own text must not be quoted back: the message is published verbatim as the problem
        // detail, and export content may carry the portal's user data.
        outcome.Reason!.Message.Should().NotContain("before");
        harness.AuditRecords.Should().BeEmpty("a failed export is not a movement of content");
    }

    /// <summary>
    /// A payload that is nothing but whitespace survives the round trip, which it did not while the reader
    /// was allowed to discard an all-whitespace text node as insignificant.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task ImportModule_KeepsAPayloadThatIsOnlyWhitespace()
    {
        Harness harness = Harness.Ready();
        string? handedToTheModule = null;
        harness.BusinessControllers
            .Setup(f => f.ImportModuleContentAsync(
                It.IsAny<string?>(),
                It.IsAny<int>(),
                It.IsAny<string?>(),
                It.IsAny<string?>(),
                It.IsAny<int>(),
                It.IsAny<CancellationToken>()))
            .Callback<string?, int, string?, string?, int, CancellationToken>(
                (_, _, content, _, _, _) => handedToTheModule = content)
            .ReturnsAsync(Result.Success());

        Result outcome = await harness.Service.ImportModuleAsync(
            PortalId,
            new ModuleImportRequest { ModuleId = ModuleId, Content = TypedDocumentPrefix + ">   </content>" },
            CancellationToken.None);

        outcome.IsSuccess.Should().BeTrue();
        handedToTheModule.Should().Be("   ");
    }

    /// <summary>
    /// Exporting content records the movement on the audit trail, carrying the payload's LENGTH and no
    /// part of the payload itself.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    /// <remarks>
    /// The no-payload assertion is the load-bearing one. An export payload is module content: it may be
    /// arbitrarily large and it may carry data belonging to the portal's users, so putting any of it in the
    /// trail would move user data into a log store where it is neither access-controlled with the module nor
    /// removable with it. The fact is written so that it fails if any recorded property ever contains the
    /// payload, rather than merely checking the properties that exist today.
    /// </remarks>
    [Fact]
    public async Task ExportModule_RecordsTheMovementWithoutRecordingTheContent()
    {
        const string Payload = "<item>a-secret-looking-value</item>";

        Harness harness = Harness.Ready();
        harness.ExportOutcome = Result<string?>.Success(Payload);

        Result<string> outcome = await harness.Service.ExportModuleAsync(
            PortalId,
            ModuleId,
            new ModuleExportRequest { FileName = "content.xml" },
            CancellationToken.None);

        outcome.IsSuccess.Should().BeTrue();

        AuditEvent record = harness.AuditRecords.Should().ContainSingle().Subject;
        record.EventName.Should().Be(ModuleExportedEventName);
        record.PortalId.Should().Be(PortalId);
        record.ResourceId.Should().Be(ModuleId.ToString(CultureInfo.InvariantCulture));
        record.Properties["Operation"].Should().Be("Export");
        record.Properties["PayloadLength"].Should()
            .Be(Payload.Length.ToString(CultureInfo.InvariantCulture));

        record.Properties.Values
            .Where(value => value is not null)
            .Should().NotContain(value => value!.Contains("a-secret-looking-value", StringComparison.Ordinal));
    }

    /// <summary>
    /// Importing content records bounded operational facts and carries neither payload nor caller-authored
    /// provenance text.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    /// <remarks>
    /// The facts an operator needs are taken from the SERVER wherever they can be - the package's own name
    /// and installed version, the content type the document was accepted against, the payload length and
    /// the placement count - and only the document's declared version and the caller's own folder and file
    /// names come from the request. Those three are recorded under names that say where they came from, so
    /// a record cannot be read as though the server had vouched for them.
    /// </remarks>
    [Fact]
    public async Task ImportModule_RecordsTheProvenanceWithoutRecordingTheContent()
    {
        Harness harness = Harness.Ready();

        Result outcome = await harness.Service.ImportModuleAsync(
            PortalId,
            new ModuleImportRequest
            {
                ModuleId = ModuleId,
                Content = TypedDocumentPrefix + FormattableString.Invariant(
                    $" version=\"{PackageVersion}\">a-secret-looking-value</content>"),
                Folder = "Portals/0",
                FileName = "content.xml",
            },
            CancellationToken.None);

        outcome.IsSuccess.Should().BeTrue();

        AuditEvent record = harness.AuditRecords.Should().ContainSingle().Subject;
        record.EventName.Should().Be(ModuleUpdatedEventName);
        record.PortalId.Should().Be(PortalId);
        record.ResourceId.Should().Be(ModuleId.ToString(CultureInfo.InvariantCulture));
        record.Properties["Operation"].Should().Be("Import");
        record.Properties["Version"].Should().Be(PackageVersion);
        record.Properties["PayloadLength"].Should().NotBeNull();
        record.Properties["PlacementCount"].Should().NotBeNull();

        // The caller's own description of where the document came from is NOT recorded. Both facts were
        // copied verbatim from the request, ModuleImportRequest documents them as accepted-and-unused parity
        // metadata with no length bound, and nothing validated or read them - so a caller could put a secret,
        // a personal identifier, control text or an unbounded high-cardinality value into the audit trail by
        // naming a file that way. The module, the version, the size and the placement count describe what
        // actually happened; the caller's description describes only what the caller said. The version that
        // IS recorded is reduced to digits, dots and hyphens within a length bound before it is offered.
        record.Properties.Should().NotContainKey("SourceFileName");
        record.Properties.Should().NotContainKey("SourceFolder");

        record.Properties.Values
            .Where(value => value is not null)
            .Should().NotContain(value => value!.Contains("a-secret-looking-value", StringComparison.Ordinal));
    }

    /// <summary>
    /// A refused import writes nothing to the audit trail, so no record can claim a change that never
    /// happened.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task ImportModule_RecordsNothingWhenTheModuleRefusesTheContent()
    {
        Harness harness = Harness.Ready();
        harness.ImportOutcome = Result.Failure("module.import_failed", "The module rejected the document.");

        Result outcome = await harness.Service.ImportModuleAsync(
            PortalId,
            new ModuleImportRequest { ModuleId = ModuleId, Content = TypedDocumentPrefix + ">one</content>" },
            CancellationToken.None);

        outcome.IsFailure.Should().BeTrue();
        harness.AuditRecords.Should().BeEmpty();
    }

    /// <summary>
    /// An ordinary single-module edit writes no audit record, while an edit whose effect reaches beyond
    /// the addressed module does.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    /// <remarks>
    /// The trail records consequential change, not every field edit. The legacy application recorded
    /// neither, so recording the wide-effect case alone is the narrowest addition that satisfies this
    /// contract's promise that a change whose blast radius exceeds the addressed module is recorded.
    /// </remarks>
    [Fact]
    public async Task UpdateModule_RecordsOnlyWhenTheChangeReachesBeyondTheModule()
    {
        Harness narrow = Harness.Ready();

        Result<ModuleDetailDto?> ordinary = await narrow.Service.UpdateModuleAsync(
            PortalId,
            ModuleId,
            new UpdateModuleRequest { TabId = TabId, ModuleTitle = ModuleTitle },
            CancellationToken.None);

        ordinary.IsSuccess.Should().BeTrue();
        narrow.AuditRecords.Should().BeEmpty();

        Harness wide = Harness.Ready();

        Result<ModuleDetailDto?> propagated = await wide.Service.UpdateModuleAsync(
            PortalId,
            ModuleId,
            new UpdateModuleRequest
            {
                TabId = TabId,
                ModuleTitle = ModuleTitle,
                ApplyToAllModules = true,
            },
            CancellationToken.None);

        propagated.IsSuccess.Should().BeTrue();

        AuditEvent record = wide.AuditRecords.Should().ContainSingle().Subject;
        record.EventName.Should().Be(ModuleUpdatedEventName);
        record.Properties["Operation"].Should().Be("Update");
        record.Properties["ApplyToAllModules"].Should().Be(bool.TrueString);
    }

    /// <summary>
    /// A CDATA section is handed on with its delimiters intact, because that is what the legacy importer's
    /// <c>InnerXml</c> did.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    /// <remarks>
    /// <para>
    /// MIGRATION: <c>Website/admin/Modules/Import.ascx.vb</c> L200 passes
    /// <c>xmlDoc.DocumentElement.InnerXml</c>, and <c>InnerXml</c> returns MARKUP - a CDATA child comes back
    /// as <c>&lt;![CDATA[...]]&gt;</c>, delimiters and all. The module admin exporter never wrote a CDATA
    /// section, so this shape did not arise in that workflow and the legacy behaviour for it is simply
    /// whatever <c>InnerXml</c> yielded. Reproducing it exactly is what makes the payload rule a single rule
    /// with no branch.
    /// </para>
    /// <para>
    /// MIGRATION: AN EARLIER REVISION OF THIS FACT ASSERTED THAT THE SECTION WAS UNWRAPPED AND HTML-DECODED
    /// to <c>&lt;item&gt;one&lt;/item&gt;</c>, and it was named for accepting a legacy exporter's document.
    /// Both were wrong about WHICH legacy exporter: the CDATA-and-encode shape is written by
    /// <c>ModuleController.vb</c> L244-L246, the PORTAL TEMPLATE writer, whose documents are read by the
    /// portal template parser and never by the module import screen. Unwrapping it here required a branch on
    /// whether the content element had child elements, and that branch silently mangled a payload whose own
    /// markup legitimately contained a CDATA section - a real shape, since module content is arbitrary XML.
    /// The single verbatim rule replaces it and the divergence is recorded in MIGRATION_NOTES.md.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task ImportModule_HandsOnACdataSectionWithItsDelimitersIntact()
    {
        Harness harness = Harness.Ready();
        string? handedToTheModule = null;
        harness.BusinessControllers
            .Setup(f => f.ImportModuleContentAsync(
                It.IsAny<string?>(),
                It.IsAny<int>(),
                It.IsAny<string?>(),
                It.IsAny<string?>(),
                It.IsAny<int>(),
                It.IsAny<CancellationToken>()))
            .Callback<string?, int, string?, string?, int, CancellationToken>(
                (_, _, content, _, _, _) => handedToTheModule = content)
            .ReturnsAsync(Result.Success());

        string documentWithACdataSection =
            $"<content type=\"{PackageName}\" version=\"{PackageVersion}\">"
            + "<![CDATA[&lt;item&gt;one&lt;/item&gt;]]></content>";

        Result outcome = await harness.Service.ImportModuleAsync(
            PortalId,
            new ModuleImportRequest { ModuleId = ModuleId, Content = documentWithACdataSection },
            CancellationToken.None);

        outcome.IsSuccess.Should().BeTrue();
        handedToTheModule.Should().Be(
            "<![CDATA[&lt;item&gt;one&lt;/item&gt;]]>",
            "InnerXml returns markup, so the section arrives at the module exactly as it sat in the document");
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
    /// A request that names no target module at all is refused as malformed, and is distinguished from one
    /// that names module zero.
    /// </summary>
    /// <remarks>
    /// THE REGRESSION TEST FOR THE IDENTITY-SEED COLLISION. <c>Modules.ModuleID</c> is
    /// <c>IDENTITY (0, 1)</c>, so zero is a real module - and it is the very value this fixture uses as its
    /// canonical module. <c>ModuleImportRequest.ModuleId</c> is therefore nullable, so that an omitted
    /// identifier arrives as <see langword="null"/> rather than being deserialised into a live request
    /// against module zero. Nothing upstream can catch the omission - this request has no validator - so the
    /// discrimination has to happen in the service, and it must be made on ABSENCE rather than on sign.
    /// </remarks>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task ImportModule_RefusesARequestNamingNoModuleWithoutMistakingItForModuleZero()
    {
        Harness harness = Harness.Ready();

        Result omitted = await harness.Service.ImportModuleAsync(
            PortalId,
            new ModuleImportRequest { Content = TypedDocumentPrefix + ">x</content>" },
            CancellationToken.None);

        omitted.IsFailure.Should().BeTrue();
        omitted.Reason!.Code.Should().Be(RequestInvalidCode);
        omitted.Reason!.Message.Should().Be("The module to import into must be supplied.");

        // Zero is a legitimate identifier, so the same request naming it must get PAST this guard. The
        // fixture's canonical module is zero, so a successful import here is the proof.
        Result zero = await harness.Service.ImportModuleAsync(
            PortalId,
            new ModuleImportRequest { ModuleId = ModuleId, Content = TypedDocumentPrefix + ">x</content>" },
            CancellationToken.None);

        zero.IsSuccess.Should().BeTrue();
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
            new ModuleImportRequest { ModuleId = ModuleId, Content = TypedDocumentPrefix + ">x</content>" },
            CancellationToken.None);

        outcome.IsFailure.Should().BeTrue();
        outcome.Reason!.Code.Should().Be(NotFoundCode);
        outcome.Reason!.Message.Should()
            .Be($"Module {ModuleId} does not exist in portal {PortalId}.");
    }

    /// <summary>
    /// A caller holding no edit grant on the target module is refused, and the module keeps its content.
    /// </summary>
    /// <remarks>
    /// THE MISSING-AUTHORISATION REGRESSION TEST FOR IMPORT. As with creation, the target arrives in the request
    /// body and the only check performed was tenant ownership, so any authenticated caller could overwrite any
    /// tenant's module content.
    /// </remarks>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task ImportModule_RefusesACallerWithoutTheEditGrantOnTheModule()
    {
        Harness harness = Harness.Ready();
        harness.GrantEdit(granted: false);

        Result outcome = await harness.Service.ImportModuleAsync(
            PortalId,
            new ModuleImportRequest { ModuleId = ModuleId, Content = TypedDocumentPrefix + ">x</content>" },
            CancellationToken.None);

        outcome.IsFailure.Should().BeTrue();
        outcome.Reason!.Code.Should().Be(EditForbiddenCode);
        outcome.Reason!.Message.Should()
            .Be($"The caller may not import content into module {ModuleId} in portal {PortalId}.");
        harness.BusinessControllers.Verify(
            factory => factory.ImportModuleContentAsync(
                It.IsAny<string?>(),
                It.IsAny<int>(),
                It.IsAny<string?>(),
                It.IsAny<string?>(),
                It.IsAny<int>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
    }

    /// <summary>
    /// The grant is asked for on the MODULE THE REQUEST NAMES and for the EDIT key - the same key the update and
    /// delete endpoints are gated on, because an import replaces the module's stored content.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task ImportModule_AsksForTheEditGrantOnTheNamedModule()
    {
        Harness harness = Harness.Ready();

        await harness.Service.ImportModuleAsync(
            PortalId,
            new ModuleImportRequest { ModuleId = ModuleId, Content = TypedDocumentPrefix + ">x</content>" },
            CancellationToken.None);

        harness.Permissions.Verify(
            permissions => permissions.HasModulePermissionAsync(
                PortalId,
                It.IsAny<int?>(),
                ModuleId,
                PermissionKey.EDIT,
                It.IsAny<int?>(),
                It.IsAny<int?>(),
                It.IsAny<CancellationToken>()),
            Times.Once);
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
            new ModuleImportRequest { ModuleId = ModuleId, Content = TypedDocumentPrefix + ">x</content>" },
            CancellationToken.None);

        outcome.IsFailure.Should().BeTrue();
        outcome.Reason!.Code.Should().Be(NotPortableCode);
        outcome.Reason!.Message.Should()
            .Be($"Module {ModuleId} does not support content import.");
    }

    /// <summary>
    /// Content that is not well-formed is refused with a fixed public explanation while a bounded diagnostic
    /// is retained in the protected audit channel.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task ImportModule_RefusesMalformedXml()
    {
        Harness harness = Harness.Ready();

        Result outcome = await harness.Service.ImportModuleAsync(
            PortalId,
            new ModuleImportRequest { ModuleId = ModuleId, Content = "<content type=\"" + CleanedPackageName + "\">unclosed" },
            CancellationToken.None);

        outcome.IsFailure.Should().BeTrue();
        outcome.Reason!.Code.Should().Be(ContentInvalidCode);
        outcome.Reason!.Message.Should()
            .Be("The submitted document could not be parsed safely as portable module content.");

        AuditEvent diagnostic = harness.AuditRecords.Should().ContainSingle().Subject;
        diagnostic.Outcome.Should().Be(AuditOutcome.Denied);
        diagnostic.FailureCode.Should().Be(ContentInvalidCode);
        diagnostic.Properties.Should().ContainKey("DiagnosticType").WhoseValue.Should().Be(nameof(XmlException));
        diagnostic.Properties.Should().NotContainValue(outcome.Reason.Message);
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
            new ModuleImportRequest
            {
                // The ROOT NAME is what varies here; the type attribute must still name this module, because
                // the two rules are independent and this fact is about the first of them. Its counterpart -
                // that the type comparison is case-SENSITIVE while this one is not - is asserted by the type
                // fact, and the asymmetry is deliberate: the legacy compared the root name through an XML
                // reader and the type attribute with VB's binary string equality.
                ModuleId = ModuleId,
                Content = TypedDocumentPrefix.Replace("<content", "<Content", StringComparison.Ordinal)
                    + ">x</Content>",
            },
            CancellationToken.None);

        outcome.IsSuccess.Should().BeTrue();
    }

    /// <summary>
    /// A portable document must identify the target package, while the legacy definition friendly name is
    /// retained as the one approved compatibility alias.
    /// </summary>
    /// <param name="contentType">The type identifier to submit, or <see langword="null"/> to omit it.</param>
    /// <param name="accepted">Whether the selected identifier is valid for the target definition.</param>
    /// <returns>A task representing the assertion.</returns>
    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("FOREIGN_PACKAGE", false)]
    [InlineData(PackageName, true)]
    [InlineData(FriendlyName, true)]
    public async Task ImportModule_ValidatesTheMandatoryContentType(string? contentType, bool accepted)
    {
        Harness harness = Harness.Ready();
        string attribute = contentType is null ? string.Empty : $" type=\"{contentType}\"";

        Result outcome = await harness.Service.ImportModuleAsync(
            PortalId,
            new ModuleImportRequest
            {
                ModuleId = ModuleId,
                Content = $"<content{attribute}>x</content>",
            },
            CancellationToken.None);

        outcome.IsSuccess.Should().Be(accepted);
        if (!accepted)
        {
            // A document naming the wrong package, or naming none, is a WRONG-FILE refusal and carries its
            // own reason code, which the published problem-details contract maps to 400. The malformed and
            // unsafe-document facts below keep module.content_invalid, so the two classes stay separable by
            // a caller that reads the code rather than the sentence.
            outcome.Error!.Code.Should().Be(ContentTypeMismatchCode);
            harness.BusinessControllers.Verify(
                factory => factory.ImportModuleContentAsync(
                    It.IsAny<string?>(),
                    It.IsAny<int>(),
                    It.IsAny<string?>(),
                    It.IsAny<string?>(),
                    It.IsAny<int>(),
                    It.IsAny<CancellationToken>()),
                Times.Never);
        }
    }

    /// <summary>
    /// DTDs, external entities and exponential entity declarations are all refused before any module-owned
    /// code receives content.
    /// </summary>
    /// <param name="document">The hostile document to submit.</param>
    /// <returns>A task representing the assertion.</returns>
    [Theory]
    [InlineData("<!DOCTYPE content [<!ENTITY xxe SYSTEM \"file:///etc/passwd\">]><content type=\"DNN_HTML\">&xxe;</content>")]
    [InlineData("<!DOCTYPE content [<!ENTITY a \"1234567890\"><!ENTITY b \"&a;&a;&a;&a;&a;&a;&a;&a;\">]><content type=\"DNN_HTML\">&b;</content>")]
    public async Task ImportModule_RefusesDtdsAndEntityExpansion(string document)
    {
        Harness harness = Harness.Ready();

        Result outcome = await harness.Service.ImportModuleAsync(
            PortalId,
            new ModuleImportRequest { ModuleId = ModuleId, Content = document },
            CancellationToken.None);

        outcome.IsFailure.Should().BeTrue();
        outcome.Error!.Code.Should().Be(ContentInvalidCode);
        outcome.Error.Message.Should()
            .Be("The submitted document could not be parsed safely as portable module content.");
        harness.ImportedPayload.Should().BeNull();
    }

    /// <summary>
    /// Excessive nesting is refused by the streaming validation pass before an object graph is built.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task ImportModule_RefusesExcessiveNesting()
    {
        Harness harness = Harness.Ready();
        string opening = string.Concat(Enumerable.Repeat("<item>", 66));
        string closing = string.Concat(Enumerable.Repeat("</item>", 66));
        string document = $"<content type=\"{PackageName}\">{opening}x{closing}</content>";

        Result outcome = await harness.Service.ImportModuleAsync(
            PortalId,
            new ModuleImportRequest { ModuleId = ModuleId, Content = document },
            CancellationToken.None);

        outcome.IsFailure.Should().BeTrue();
        outcome.Error!.Code.Should().Be(ContentInvalidCode);
        harness.ImportedPayload.Should().BeNull();
    }

    /// <summary>A document cannot force the parser to materialise an unbounded number of tiny nodes.</summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task ImportModule_RefusesExcessiveNodeCount()
    {
        Harness harness = Harness.Ready();
        string nodes = string.Concat(Enumerable.Repeat("<item />", 10_001));
        string document = $"<content type=\"{PackageName}\">{nodes}</content>";

        Result outcome = await harness.Service.ImportModuleAsync(
            PortalId,
            new ModuleImportRequest { ModuleId = ModuleId, Content = document },
            CancellationToken.None);

        outcome.IsFailure.Should().BeTrue();
        outcome.Error!.Code.Should().Be(ContentInvalidCode);
        harness.ImportedPayload.Should().BeNull();
    }

    /// <summary>A single text node is bounded independently of the whole request-body limit.</summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task ImportModule_RefusesAnOversizedTextNode()
    {
        Harness harness = Harness.Ready();
        string document =
            $"<content type=\"{PackageName}\">{new string('x', 262_145)}</content>";

        Result outcome = await harness.Service.ImportModuleAsync(
            PortalId,
            new ModuleImportRequest { ModuleId = ModuleId, Content = document },
            CancellationToken.None);

        outcome.IsFailure.Should().BeTrue();
        outcome.Error!.Code.Should().Be(ContentInvalidCode);
        harness.ImportedPayload.Should().BeNull();
    }

    /// <summary>Namespaces and undeclared root metadata are not accepted as alternate envelope contracts.</summary>
    /// <param name="document">The unsupported envelope shape.</param>
    /// <returns>A task representing the assertion.</returns>
    [Theory]
    [InlineData("<content xmlns=\"urn:foreign\" type=\"DNN_HTML\">x</content>")]
    [InlineData("<content type=\"DNN_HTML\" unexpected=\"value\">x</content>")]
    public async Task ImportModule_RefusesUnexpectedNamespacesAndAttributes(string document)
    {
        Harness harness = Harness.Ready();

        Result outcome = await harness.Service.ImportModuleAsync(
            PortalId,
            new ModuleImportRequest { ModuleId = ModuleId, Content = document },
            CancellationToken.None);

        outcome.IsFailure.Should().BeTrue();
        outcome.Error!.Code.Should().Be(ContentInvalidCode);
        harness.ImportedPayload.Should().BeNull();
    }

    /// <summary>
    /// A document whose root carries child elements hands the inner markup to the controller verbatim, while
    /// one carrying only text hands over the text.
    /// </summary>
    /// <param name="content">The document to submit.</param>
    /// <param name="expectedPayload">The payload the controller is measured to receive.</param>
    /// <returns>A task representing the assertion.</returns>
    [Theory]
    [InlineData(TypedDocumentPrefix + "><item>one</item><item>two</item></content>", "<item>one</item><item>two</item>")]
    [InlineData(TypedDocumentPrefix + ">plain text</content>", "plain text")]
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
    [InlineData(TypedDocumentPrefix + " version=\"03.02.00\">x</content>", "03.02.00")]
    [InlineData(TypedDocumentPrefix + " version=\"\">x</content>", PackageVersion)]
    [InlineData(TypedDocumentPrefix + ">x</content>", PackageVersion)]
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
            new ModuleImportRequest { ModuleId = ModuleId, Content = TypedDocumentPrefix + ">x</content>" },
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
            new ModuleImportRequest { ModuleId = ModuleId, Content = TypedDocumentPrefix + ">x</content>" },
            CancellationToken.None);

        outcome.IsFailure.Should().BeTrue();
        outcome.Reason!.Code.Should().Be("module.controller_failed");
        harness.UnitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    /// <summary>
    /// Every state in which no content was restored is a refusal that writes nothing and records nothing.
    /// </summary>
    /// <remarks>
    /// <para>
    /// THIS FACT REPLACES ONE THAT PINNED THE OPPOSITE. The earlier fact asserted that an advisory attached
    /// to a SUCCESSFUL import outcome was carried through to the caller, and in doing so it pinned the
    /// defect: this service reads a successful import as licence to commit its unit of work, evict the
    /// placement caches of every page the module sits on, and write an <c>Operation=Import</c> audit record.
    /// An installation whose closed controller set does not cover the module's stored controller class
    /// therefore answered the caller 200 and told the audit trail content had been imported when the module
    /// had never been asked for anything.
    /// </para>
    /// <para>
    /// The four codes below are the factory's complete "nothing was restored" set and are each declared on
    /// its contract. They are exercised as data rather than as one representative case because the service's
    /// obligation is identical for all four and a single case would leave three able to regress silently.
    /// </para>
    /// </remarks>
    /// <param name="code">The factory failure code standing for one way of not restoring content.</param>
    /// <returns>A task representing the assertion.</returns>
    [Theory]
    [InlineData("module.controller.not_specified")]
    [InlineData("module.controller.not_registered")]
    [InlineData("module.controller.contract_not_supported")]
    [InlineData("module.content.not_supplied")]
    public async Task ImportModule_WhenNothingWasRestored_RefusesAndWritesNothing(string code)
    {
        Harness harness = Harness.Ready();
        harness.ImportOutcome = Result.Failure(code, "Nothing was restored.");

        Result outcome = await harness.Service.ImportModuleAsync(
            PortalId,
            new ModuleImportRequest { ModuleId = ModuleId, Content = TypedDocumentPrefix + ">x</content>" },
            CancellationToken.None);

        outcome.IsFailure.Should().BeTrue(
            "an import that restored no content is not something a caller may be told succeeded");

        // Forwarded verbatim rather than folded into one code: an operator acts differently on a key no
        // registration covers, a controller that cannot restore, and a document carrying no content.
        outcome.Reason!.Code.Should().Be(code);

        harness.UnitOfWork.Verify(
            unitOfWork => unitOfWork.SaveChangesAsync(It.IsAny<CancellationToken>()),
            Times.Never);
        harness.InvalidatedTabIds.Should().BeEmpty(
            "no cached placement list can have gone stale, because nothing was written");
        harness.AuditRecords.Should().BeEmpty(
            "an audit record claiming an import happened is worse than no record at all");
    }

    /// <summary>
    /// A successful import carries no advisory, because success now means content arrived.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task ImportModule_OnSuccessCarriesNoAdvisory()
    {
        Harness harness = Harness.Ready();

        Result outcome = await harness.Service.ImportModuleAsync(
            PortalId,
            // The document names the target module's own type. That check was added independently of this
            // fact and is asserted by its own theory; a document without the attribute is refused, so a
            // fact about what SUCCESS carries has to submit a document that can succeed.
            new ModuleImportRequest
            {
                ModuleId = ModuleId,
                Content = TypedDocumentPrefix + ">x</content>",
            },
            CancellationToken.None);

        outcome.IsSuccess.Should().BeTrue(outcome.Reason?.ToString());
        outcome.Reason.Should().BeNull(
            "a success qualified by an advisory saying nothing was restored is the ambiguity this path "
            + "removed; every such state is now a refusal");
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
            new ModuleImportRequest { ModuleId = ModuleId, Content = TypedDocumentPrefix + ">x</content>" },
            CancellationToken.None);

        outcome.IsSuccess.Should().BeTrue();
        outcome.Reason.Should().BeNull();
        harness.UnitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
        harness.InvalidatedTabIds.Should().BeEquivalentTo(new[] { TabId, SecondTabId });
    }

    /// <summary>
    /// Wraps a payload in a portability document naming the fixture package's cleaned type.
    /// </summary>
    /// <param name="payload">The inner XML the document carries.</param>
    /// <returns>A document the import path accepts as the correct type for the module under test.</returns>
    /// <remarks>
    /// MIGRATION: a document that does NOT name a matching type is refused, exactly as
    /// <c>Website/admin/Modules/Import.ascx.vb</c> lines 195-205 refused one, so every fixture that expects
    /// an import to proceed has to carry the attribute. Its absence is a case in its own right and is
    /// asserted separately rather than left implicit in these fixtures.
    /// </remarks>
    private static string ImportDocument(string payload) =>
        FormattableString.Invariant($"<content type=\"{CleanedPackageName}\">{payload}</content>");

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
    /// <remarks>
    /// The page identifier names the page the module is ALREADY on, so the canonical request is a save in
    /// place rather than a move. It has to be stated rather than left defaulted: the contract declares the
    /// member mandatory, page identity seeds at zero so a defaulted integer is a legitimate page rather than
    /// an absence, and a request that named page zero would therefore ask to move the module to the first
    /// page of the portal. A fact that means to move the module says so by overriding this member.
    /// </remarks>
    /// <returns>A well-formed update request.</returns>
    private static UpdateModuleRequest ValidUpdateRequest() => new()
    {
        // The page the seeded placement sits on, and NOT a value this helper may leave at its default.
        //
        // MIGRATION: the page key SELECTS the placement a full replacement addresses, and an omitted member
        // deserialises to zero, which dbo.Tabs.TabID being IDENTITY(0, 1) makes a real page rather than an
        // absence. This helper used to omit it, and every fact built on it passed only because the service
        // ignored the member and amended the placement with the lowest identifier instead. Naming the page
        // here is what makes those facts assert the behaviour they describe.
        TabId = TabId,
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

            // The permission evaluator answers "granted" by default, so every existing fact continues to
            // exercise the behaviour it was written for rather than the new authorisation guard. The facts
            // that exercise the guard itself override this, which is also what makes them read as being
            // about authorisation.
            Permissions = new Mock<IPermissionService>(MockBehavior.Loose);
            GrantEdit(granted: true);

            // Tenant authority answers "administers" by default, for the same reason and with the same
            // consequence: the wide-effect facts below were written about what a portal administrator's save
            // does, and that is who the baseline caller now is. It is a SEPARATE default from the edit grant
            // rather than folded into it, because the two questions are separate - holding an edit grant on
            // one module says nothing about authority over the tenant - and a fact that flips one while
            // leaving the other alone is exactly how that separation is proved.
            AdministerPortal(administers: true);

            // The audit sink records what it is handed so the facts below can assert on the trail without
            // reaching a log store. A loose mock would swallow the calls silently; capturing them is what
            // lets a fact prove that a record was written, that it was written only once, and - the rule
            // that matters most - that no content payload was ever put into it.
            Audit = new Mock<IAuditSink>(MockBehavior.Loose);
            AuditRecords = [];
            Audit.Setup(sink => sink.Record(It.IsAny<AuditEvent>()))
                .Callback<AuditEvent>(AuditRecords.Add);

            Service = new ModuleService(
                Modules.Object,
                Definitions.Object,
                Tabs.Object,
                Portals.Object,
                UnitOfWork.Object,
                Cache.Object,
                CurrentUser.Object,
                Permissions.Object,
                BusinessControllers.Object,
                Audit.Object,
                Caching);
        }

        /// <summary>
        /// Sets the answer the permission evaluator gives for both the page and the module edit questions.
        /// </summary>
        /// <param name="granted">Whether the caller is to be reported as holding the edit grant.</param>
        public void GrantEdit(bool granted)
        {
            Permissions
                .Setup(permissions => permissions.HasTabPermissionAsync(
                    It.IsAny<int>(),
                    It.IsAny<int?>(),
                    It.IsAny<int>(),
                    It.IsAny<PermissionKey>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(Result<bool>.Success(granted));

            Permissions
                .Setup(permissions => permissions.HasModulePermissionAsync(
                    It.IsAny<int>(),
                    It.IsAny<int?>(),
                    It.IsAny<int>(),
                    It.IsAny<PermissionKey>(),
                    It.IsAny<int?>(),
                    It.IsAny<int?>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(Result<bool>.Success(granted));
        }

        /// <summary>
        /// Sets the answer tenant authority gives for this portal.
        /// </summary>
        /// <param name="administers">Whether the caller is to be reported as administering the portal.</param>
        public void AdministerPortal(bool administers)
            => Permissions
                .Setup(permissions => permissions.IsPortalAdministratorAsync(
                    It.IsAny<int>(),
                    It.IsAny<int?>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(Result<bool>.Success(administers));

        /// <summary>
        /// Makes tenant authority unanswerable, as an unreachable store would.
        /// </summary>
        /// <param name="code">The failure code the authority question is to report.</param>
        public void FailPortalAuthority(string code)
            => Permissions
                .Setup(permissions => permissions.IsPortalAdministratorAsync(
                    It.IsAny<int>(),
                    It.IsAny<int?>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(Result<bool>.Failure(code, "The authority question could not be answered."));

        public ModuleService Service { get; }

        public Mock<IModuleRepository> Modules { get; }

        public Mock<IModuleDefinitionRepository> Definitions { get; }

        public Mock<ITabRepository> Tabs { get; }

        public Mock<IPortalRepository> Portals { get; }

        public Mock<IUnitOfWork> UnitOfWork { get; }

        public Mock<ICacheService> Cache { get; }

        public Mock<ICurrentUser> CurrentUser { get; }

        public Mock<IModuleBusinessControllerFactory> BusinessControllers { get; }

        public Mock<IPermissionService> Permissions { get; }

        public Mock<IAuditSink> Audit { get; }

        /// <summary>
        /// Every audit event the service recorded, in the order it recorded them.
        /// </summary>
        public List<AuditEvent> AuditRecords { get; }

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
                .Setup(t => t.GetByIdAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((int tabId, CancellationToken _) =>
                    harness.TabsById.TryGetValue(tabId, out Tab? found) ? found : null);
            harness.Tabs
                .Setup(t => t.GetByPortalIdAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => harness.TenantTabs);

            // The page-centric placement read belongs to ITabRepository, and the service now uses it to
            // answer "which modules sit on this page" in one call. It is derived from the same placement
            // world the module stubs below serve, so the harness stays internally consistent.
            harness.Tabs
                .Setup(t => t.GetTabModulesAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((int tabId, CancellationToken _) =>
                    harness.PlacementsByModuleId.Values
                        .SelectMany(placements => placements)
                        .Where(placement => placement.TabId == tabId)
                        .ToList());

            harness.Modules
                .Setup(m => m.GetByIdAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => harness.LookupModule);
            harness.Modules
                .Setup(m => m.GetTabModuleByIdAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((int tabModuleId, CancellationToken _) =>
                    harness.PlacementsById.TryGetValue(tabModuleId, out TabModule? found) ? found : null);
            harness.Modules
                .Setup(m => m.GetTabModulesByModuleIdAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((int moduleId, CancellationToken _) =>
                    harness.PlacementsByModuleId.TryGetValue(moduleId, out List<TabModule>? found)
                        ? (IReadOnlyList<TabModule>)found
                        : Array.Empty<TabModule>());
            // The set-based placement read the listing uses. Served from the same placement world as every
            // other placement stub, so the harness describes one reality: the listing asks for many modules
            // in one call so that its cost cannot grow with the number of modules, and answering from a
            // separate seam would leave that property untested.
            harness.Modules
                .Setup(m => m.GetTabModulesByModuleIdsAsync(
                    It.IsAny<IReadOnlyCollection<int>>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync((IReadOnlyCollection<int> moduleIds, CancellationToken _) =>
                    (IReadOnlyList<TabModule>)moduleIds
                        .Where(harness.PlacementsByModuleId.ContainsKey)
                        .SelectMany(moduleId => harness.PlacementsByModuleId[moduleId])
                        .ToList());
            // The append instruction is resolved by reading the pane it is being appended to, so that read
            // is served from the same placement world every other placement stub serves rather than from a
            // separate seam. Keeping it consistent is what lets a fact seed a pane and then assert the
            // position the production path computes for it. The ordering mirrors the repository's, whose own
            // ORDER BY mirrors the terminal legacy procedure's.
            harness.Modules
                .Setup(m => m.GetTabModuleOrderAsync(
                    It.IsAny<int>(),
                    It.IsAny<string>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync((int tabId, string paneName, CancellationToken _) =>
                    (IReadOnlyList<TabModule>)harness.PlacementsByModuleId.Values
                        .SelectMany(placements => placements)
                        .Where(placement => placement.TabId == tabId && placement.PaneName == paneName)
                        .OrderBy(placement => placement.ModuleOrder)
                        .ThenBy(placement => placement.TabModuleId)
                        .ToList());
            harness.Modules
                .Setup(m => m.GetModuleSettingsAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((int moduleId, CancellationToken _) =>
                    harness.SettingsByModuleId.TryGetValue(moduleId, out List<ModuleSetting>? found)
                        ? (IReadOnlyList<ModuleSetting>)found
                        : Array.Empty<ModuleSetting>());
            harness.Modules
                .Setup(m => m.GetTabModuleSettingsAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((int tabModuleId, CancellationToken _) =>
                    harness.PlacementSettingsByTabModuleId.TryGetValue(tabModuleId, out List<TabModuleSetting>? found)
                        ? (IReadOnlyList<TabModuleSetting>)found
                        : Array.Empty<TabModuleSetting>());

            // The repository contract carries no paging member, so the tenant's modules arrive whole and
            // the service composes the page. ModulePage remains the harness's seam for that world.
            harness.Modules
                .Setup(m => m.GetByPortalIdAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => harness.ModulePage.Items);

            harness.Modules
                .Setup(m => m.AddAsync(It.IsAny<Module>(), It.IsAny<CancellationToken>()))
                .Callback<Module, CancellationToken>((module, _) => harness.AddedModules.Add(module))
                .Returns(Task.CompletedTask);
            harness.Modules
                .Setup(m => m.AddTabModuleAsync(It.IsAny<TabModule>(), It.IsAny<CancellationToken>()))
                .Callback<TabModule, CancellationToken>((placement, _) => harness.AddedPlacements.Add(placement))
                .Returns(Task.CompletedTask);
            harness.Modules
                .Setup(m => m.AddModuleSettingAsync(It.IsAny<ModuleSetting>(), It.IsAny<CancellationToken>()))
                .Callback<ModuleSetting, CancellationToken>((setting, _) => harness.AddedSettings.Add(setting))
                .Returns(Task.CompletedTask);
            harness.Modules
                .Setup(m => m.AddTabModuleSettingAsync(It.IsAny<TabModuleSetting>(), It.IsAny<CancellationToken>()))
                .Callback<TabModuleSetting, CancellationToken>((setting, _) => harness.AddedPlacementSettings.Add(setting))
                .Returns(Task.CompletedTask);

            // The deletes address their target by key rather than by entity, so each stub resolves the
            // instance out of the harness's own world. Recording the resolved instance keeps the identity
            // assertions in the tests meaningful.
            harness.Modules
                .Setup(m => m.DeleteTabModuleAsync(
                    It.IsAny<int>(),
                    It.IsAny<int>(),
                    It.IsAny<CancellationToken>()))
                .Callback<int, int, CancellationToken>((tabId, moduleId, _) =>
                {
                    TabModule? stored = harness.PlacementsByModuleId.TryGetValue(moduleId, out List<TabModule>? placements)
                        ? placements.FirstOrDefault(placement => placement.TabId == tabId)
                        : null;

                    stored ??= harness.PlacementsById.Values
                        .OfType<TabModule>()
                        .FirstOrDefault(placement => placement.TabId == tabId && placement.ModuleId == moduleId);

                    if (stored is not null)
                    {
                        harness.RemovedPlacements.Add(stored);
                    }
                })
                .Returns(Task.CompletedTask);
            harness.Modules
                .Setup(m => m.DeleteModuleSettingAsync(
                    It.IsAny<int>(),
                    It.IsAny<string>(),
                    It.IsAny<CancellationToken>()))
                .Callback<int, string, CancellationToken>((moduleId, settingName, _) =>
                {
                    if (harness.SettingsByModuleId.TryGetValue(moduleId, out List<ModuleSetting>? settings)
                        && settings.FirstOrDefault(setting =>
                            string.Equals(setting.SettingName, settingName, StringComparison.Ordinal))
                            is ModuleSetting stored)
                    {
                        harness.RemovedSettings.Add(stored);
                    }
                })
                .Returns(Task.CompletedTask);
            harness.Modules
                .Setup(m => m.DeleteTabModuleSettingAsync(
                    It.IsAny<int>(),
                    It.IsAny<string>(),
                    It.IsAny<CancellationToken>()))
                .Callback<int, string, CancellationToken>((tabModuleId, settingName, _) =>
                {
                    if (harness.PlacementSettingsByTabModuleId.TryGetValue(tabModuleId, out List<TabModuleSetting>? settings)
                        && settings.FirstOrDefault(setting =>
                            string.Equals(setting.SettingName, settingName, StringComparison.Ordinal))
                            is TabModuleSetting stored)
                    {
                        harness.RemovedPlacementSettings.Add(stored);
                    }
                })
                .Returns(Task.CompletedTask);
            harness.Modules
                .Setup(m => m.DeleteTabModuleSettingsAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
                .Callback<int, CancellationToken>((tabModuleId, _) =>
                {
                    if (harness.PlacementSettingsByTabModuleId.TryGetValue(tabModuleId, out List<TabModuleSetting>? settings))
                    {
                        harness.RemovedPlacementSettings.AddRange(settings);
                    }
                })
                .Returns(Task.CompletedTask);

            harness.Definitions
                .Setup(d => d.GetModuleDefinitionsByPortalIdAsync(It.IsAny<int?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => harness.DefinitionCatalogue);
            harness.Definitions
                .Setup(d => d.GetAdministrativeDefinitionByFriendlyNameAsync(
                    It.IsAny<int>(),
                    It.IsAny<string>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync((int _, string friendlyName, CancellationToken _) =>
                    harness.DefinitionCatalogue.FirstOrDefault(definition => string.Equals(
                        definition.FriendlyName,
                        friendlyName,
                        StringComparison.OrdinalIgnoreCase)));
            harness.Definitions
                .Setup(d => d.GetModuleDefinitionByIdAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => harness.LookupDefinition);
            harness.Definitions
                .Setup(d => d.GetDesktopModuleByIdAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
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
