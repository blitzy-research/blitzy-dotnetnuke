using System.Globalization;
using System.Reflection;
using DnnMigration.Application.Abstractions;
using DnnMigration.Application.Dtos.Common;
using DnnMigration.Application.Dtos.Portal;
using DnnMigration.Application.Options;
using DnnMigration.Application.Services;
using DnnMigration.Application.Validation;
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
/// Covers the tenant workflow: the two-stage provisioning transaction and its compensation, the host-only
/// field guard on an update, the last-remaining-tenant rule on a delete, and the alias surface that decides
/// which host name reaches which tenant.
/// </summary>
/// <remarks>
/// <para>
/// Provisioning a tenant cannot be done in one commit. The administrator's credential lives in the external
/// membership store, which is reached by explicit statements rather than through the tracked object graph, so
/// it can only be written once the account has an identifier — which means after a first commit. The service
/// therefore commits the object graph, writes the credential, and commits again with the wiring the second
/// commit needs. That leaves a window in which the tenant exists and its administrator cannot sign in, so the
/// failure branch has to undo the first commit. Both the refusal branch and the exception branch are asserted
/// here, right down to which rows are withdrawn and whether the credential is deleted, because a half-created
/// tenant is worse than none.
/// </para>
/// <para>
/// The host-only guard is an authorisation rule over the contents of a request rather than over the route, so
/// it cannot be expressed as an endpoint policy. It is asserted against the value the update will actually
/// write rather than against the submitted value, because the request is a whole-row replacement and an
/// omitted numeric term is written as zero. A guard that tested only for presence would let a tenant
/// administrator waive its own hosting charge by leaving the field out; the assertions below pin that
/// specific case.
/// </para>
/// </remarks>
public class PortalServiceTests
{
    private const int PortalId = -1;

    private const int SecondPortalId = 0;

    /// <summary>
    /// The product-wide page permission scope code, as the upgrade scripts spell it.
    /// </summary>
    /// <remarks>
    /// Repeated here rather than shared, because the repository that owns it keeps it private and this
    /// test asserts against the same literal the shipped catalogue rows carry.
    /// </remarks>
    private const string TabScopeCode = "SYSTEM_TAB";

    /// <summary>
    /// A tenant that is neither of the two the fixture builds, used to prove that an alias owned elsewhere
    /// is refused. Held separately from <see cref="SecondPortalId"/> so a cross-tenant assertion cannot be
    /// satisfied by a value some other part of the fixture also uses.
    /// </summary>
    private const int ForeignPortalId = 12;

    private const int PortalAliasId = 4;

    /// <summary>
    /// A SECOND alias row of the same portal, standing in for the host name a request arrived through when
    /// that host name is deliberately NOT the row the test is about to write.
    /// </summary>
    /// <remarks>
    /// Held as its own pair of constants rather than derived from <see cref="PortalAliasId"/> because the
    /// active-alias refusal turns "which row did this request arrive through" into a load-bearing fact: a
    /// test that reused the row under test here would be arranging the REFUSED case by accident and would
    /// then prove nothing about the path it names. The host name is distinct from the
    /// <c>"other.example"</c> the cross-tenant assertions use, so a same-portal arrangement can never be
    /// confused with a foreign-tenant one.
    /// </remarks>
    private const int SparePortalAliasId = 5;

    /// <inheritdoc cref="SparePortalAliasId"/>
    private const string SpareHostAlias = "spare.example";

    private const int AdministratorId = 7;

    /// <summary>
    /// The account key of the caller making the request, deliberately distinct from
    /// <see cref="AdministratorId"/> so that a fact about the CALLER's stored authority cannot be satisfied by
    /// a lookup of the portal's designated administrator.
    /// </summary>
    private const int CallerId = 990;

    private const int AdministratorRoleId = 0;

    private const int RegisteredRoleId = 1;

    private const int HostRootTabId = 100;

    private const string PortalName = "Measured Portal";

    private const string HostAlias = "measured.example";

    private const string AdministratorUsername = "ada";

    private const string AdministratorEmail = "ada@example.com";

    private const string AdministratorPassword = "Correct-Horse-1";

    private const string PasswordHash = "$2a$11$measured.hash.value.for.the.administrator.account.00";

    private const string DefaultLanguageCode = "en-US";

    private const int DefaultTimeZoneOffsetMinutes = -8;

    private const string FallbackCurrency = "USD";

    private const string PagingInvalidCode = "portal.paging_invalid";

    private const string NotFoundCode = "portal.not_found";

    private const string CreationFailedCode = "portal.creation_failed";

    private const string AdministratorDuplicateCode = "portal.administrator_duplicate";

    private const string AliasDuplicateCode = "portal.alias_duplicate";

    private const string AliasNotFoundCode = "portal.alias_not_found";

    private const string LastRemainingCode = "portal.last_remaining";

    private const string MemberSessionRevocationFailedCode =
        "portal.member.session.revocation_store_unavailable";

    private const string MemberCredentialRemovalFailedCode =
        "portal.member.credential.removal_store_unavailable";

    private static readonly DateTime Now = new(2026, 8, 2, 12, 0, 0, DateTimeKind.Utc);

    /// <summary>
    /// The tenant contract exposes thirteen asynchronous operations and nothing else.
    /// </summary>
    /// <remarks>
    /// The thirteenth is <c>ListAdministratorCandidatesAsync</c>, which fills the administrator selector
    /// on the settings screen (<c>Website/admin/Portal/SiteSettings.ascx.vb:L329-L339</c>). It has to
    /// live on THIS contract rather than on the role contract: every role read resolves its tenant from
    /// the caller's own context rather than from a route segment, so none of them can enumerate the
    /// administrators of the portal a settings screen happens to be addressing, and without it the
    /// administrator could be displayed and never reassigned.
    /// </remarks>
    [Fact]
    public void PortalContract_OffersExactlyThirteenOperations()
    {
        MethodInfo[] members = typeof(IPortalService).GetMethods();

        members.Should().HaveCount(13);
        foreach (MethodInfo member in members)
        {
            member.Name.Should().EndWith("Async");
            typeof(Task).IsAssignableFrom(member.ReturnType).Should().BeTrue();
            member.GetParameters()[^1].ParameterType.Should().Be(typeof(CancellationToken));
        }
    }

    /// <summary>
    /// The service refuses to be constructed without every collaborator it depends on.
    /// </summary>
    [Fact]
    public void Service_RequiresEveryCollaborator()
    {
        var portals = new Mock<IPortalRepository>().Object;
        var aliases = new Mock<IPortalAliasRepository>().Object;
        var tabs = new Mock<ITabRepository>().Object;
        var profiles = new Mock<IUserProfileRepository>().Object;
        var permissions = new Mock<IPermissionRepository>().Object;
        var users = new Mock<IUserRepository>().Object;
        var roles = new Mock<IRoleRepository>().Object;
        var modules = new Mock<IModuleRepository>().Object;
        var unitOfWork = new Mock<IUnitOfWork>().Object;
        var hostSettings = new Mock<IHostSettingsService>().Object;
        var hasher = new Mock<IPasswordHasher>().Object;
        var tokens = new Mock<ITokenService>().Object;
        var clock = new Mock<IClock>().Object;
        var cache = new Mock<ICacheService>().Object;
        var currentUser = new Mock<ICurrentUser>().Object;
        var audit = new Mock<IAuditSink>().Object;
        var portalContext = new Mock<IPortalContextHolder>().Object;
        var caching = new CachingOptions();

        Assert.Throws<ArgumentNullException>("portals", () =>
        {
            _ = new PortalService(
                null!, aliases, tabs, profiles, permissions, users, roles, modules, unitOfWork, hostSettings, hasher, tokens,
                clock, cache, currentUser, audit, portalContext, caching);
        });
        Assert.Throws<ArgumentNullException>("aliases", () =>
        {
            _ = new PortalService(
                portals, null!, tabs, profiles, permissions, users, roles, modules, unitOfWork, hostSettings, hasher, tokens,
                clock, cache, currentUser, audit, portalContext, caching);
        });
        Assert.Throws<ArgumentNullException>("tabs", () =>
        {
            _ = new PortalService(
                portals, aliases, null!, profiles, permissions, users, roles, modules, unitOfWork, hostSettings, hasher,
                tokens, clock, cache, currentUser, audit, portalContext, caching);
        });
        Assert.Throws<ArgumentNullException>("profiles", () =>
        {
            _ = new PortalService(
                portals, aliases, tabs, null!, permissions, users, roles, modules, unitOfWork, hostSettings, hasher, tokens,
                clock, cache, currentUser, audit, portalContext, caching);
        });
        Assert.Throws<ArgumentNullException>("permissions", () =>
        {
            _ = new PortalService(
                portals, aliases, tabs, profiles, null!, users, roles, modules, unitOfWork, hostSettings, hasher, tokens,
                clock, cache, currentUser, audit, portalContext, caching);
        });
        Assert.Throws<ArgumentNullException>("users", () =>
        {
            _ = new PortalService(
                portals, aliases, tabs, profiles, permissions, null!, roles, modules, unitOfWork, hostSettings, hasher,
                tokens, clock, cache, currentUser, audit, portalContext, caching);
        });
        Assert.Throws<ArgumentNullException>("roles", () =>
        {
            _ = new PortalService(
                portals, aliases, tabs, profiles, permissions, users, null!, modules, unitOfWork, hostSettings, hasher, tokens,
                clock, cache, currentUser, audit, portalContext, caching);
        });
        Assert.Throws<ArgumentNullException>("modules", () =>
        {
            _ = new PortalService(
                portals, aliases, tabs, profiles, permissions, users, roles, null!, unitOfWork, hostSettings, hasher,
                tokens, clock, cache, currentUser, audit, portalContext, caching);
        });
        Assert.Throws<ArgumentNullException>("unitOfWork", () =>
        {
            _ = new PortalService(
                portals, aliases, tabs, profiles, permissions, users, roles, modules, null!, hostSettings, hasher, tokens,
                clock, cache, currentUser, audit, portalContext, caching);
        });
        Assert.Throws<ArgumentNullException>("hostSettings", () =>
        {
            _ = new PortalService(
                portals, aliases, tabs, profiles, permissions, users, roles, modules, unitOfWork, null!, hasher, tokens,
                clock, cache, currentUser, audit, portalContext, caching);
        });
        Assert.Throws<ArgumentNullException>("passwordHasher", () =>
        {
            _ = new PortalService(
                portals, aliases, tabs, profiles, permissions, users, roles, modules, unitOfWork, hostSettings, null!,
                tokens, clock, cache, currentUser, audit, portalContext, caching);
        });
        Assert.Throws<ArgumentNullException>("tokens", () =>
        {
            _ = new PortalService(
                portals, aliases, tabs, profiles, permissions, users, roles, modules, unitOfWork, hostSettings, hasher,
                null!, clock, cache, currentUser, audit, portalContext, caching);
        });
        Assert.Throws<ArgumentNullException>("clock", () =>
        {
            _ = new PortalService(
                portals, aliases, tabs, profiles, permissions, users, roles, modules, unitOfWork, hostSettings, hasher,
                tokens, null!, cache, currentUser, audit, portalContext, caching);
        });
        Assert.Throws<ArgumentNullException>("cache", () =>
        {
            _ = new PortalService(
                portals, aliases, tabs, profiles, permissions, users, roles, modules, unitOfWork, hostSettings, hasher,
                tokens, clock, null!, currentUser, audit, portalContext, caching);
        });
        Assert.Throws<ArgumentNullException>("currentUser", () =>
        {
            _ = new PortalService(
                portals, aliases, tabs, profiles, permissions, users, roles, modules, unitOfWork, hostSettings, hasher,
                tokens, clock, cache, null!, audit, portalContext, caching);
        });
        Assert.Throws<ArgumentNullException>("audit", () =>
        {
            _ = new PortalService(
                portals, aliases, tabs, profiles, permissions, users, roles, modules, unitOfWork, hostSettings, hasher,
                tokens, clock, cache, currentUser, null!, portalContext, caching);
        });
        Assert.Throws<ArgumentNullException>("portalContext", () =>
        {
            _ = new PortalService(
                portals, aliases, tabs, profiles, permissions, users, roles, modules, unitOfWork, hostSettings, hasher,
                tokens, clock, cache, currentUser, audit, null!, caching);
        });
        Assert.Throws<ArgumentNullException>("caching", () =>
        {
            _ = new PortalService(
                portals, aliases, tabs, profiles, permissions, users, roles, modules, unitOfWork, hostSettings, hasher,
                tokens, clock, cache, currentUser, audit, portalContext, null!);
        });
    }

    /// <summary>
    /// Listing tenants requires a paging request.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task ListPortals_RequiresARequest()
    {
        Harness harness = Harness.Ready();

        await Assert.ThrowsAsync<ArgumentNullException>(
            () => harness.Service.ListPortalsAsync(null!, null, CancellationToken.None));
    }

    /// <summary>
    /// Page coordinates outside the permitted range are refused without reading anything.
    /// </summary>
    /// <param name="pageIndex">The page index to submit.</param>
    /// <param name="pageSize">The page size to submit.</param>
    /// <returns>A task representing the assertion.</returns>
    [Theory]
    [InlineData(-1, 10)]
    [InlineData(0, -1)]
    [InlineData(0, 501)]
    public async Task ListPortals_RefusesCoordinatesOutsideThePermittedRange(int pageIndex, int pageSize)
    {
        Harness harness = Harness.Ready();

        Result<PagedResult<PortalListItemDto>> outcome = await harness.Service.ListPortalsAsync(
            new PagedRequest { PageIndex = pageIndex, PageSize = pageSize },
            null,
            CancellationToken.None);

        outcome.IsFailure.Should().BeTrue();
        outcome.Reason!.Code.Should().Be(PagingInvalidCode);
        outcome.Reason!.Message.Should()
            .Be("The requested page coordinates are outside the permitted range.");
        harness.Portals.Verify(
            p => p.ListAsync(
                It.IsAny<int>(),
                It.IsAny<int>(),
                It.IsAny<string?>(),
                It.IsAny<string?>(),
                It.IsAny<bool>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
    }

    /// <summary>
    /// The largest permitted page size is accepted, so the bound is inclusive.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task ListPortals_AcceptsTheLargestPermittedPageSize()
    {
        Harness harness = Harness.Ready();

        Result<PagedResult<PortalListItemDto>> outcome = await harness.Service.ListPortalsAsync(
            new PagedRequest { PageSize = PagedRequestValidator.MaximumPageSize },
            null,
            CancellationToken.None);

        outcome.IsSuccess.Should().BeTrue();
    }

    /// <summary>
    /// An explicit name filter reaches the store, and the sort direction is translated into a flag.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task ListPortals_PassesTheExplicitFilterAndSortThrough()
    {
        Harness harness = Harness.Ready();

        await harness.Service.ListPortalsAsync(
            new PagedRequest
            {
                PageIndex = 2,
                PageSize = 20,
                SortBy = "PortalName",
                SortDir = SortDirection.Descending,
                Query = "ignored",
            },
            "measured",
            CancellationToken.None);

        harness.Portals.Verify(
            p => p.ListAsync(2, 20, "measured", "PortalName", true, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    /// <summary>
    /// An ordering that belongs to another collection is refused here, not forwarded and silently
    /// replaced by this listing's default.
    /// </summary>
    /// <param name="foreignField">A field name declared for a different collection.</param>
    /// <returns>A task representing the assertion.</returns>
    /// <remarks>
    /// Every name below passes the shared request validator, because that validator applies the union
    /// of every collection's sortable set - one validator is resolved for the one shared request type,
    /// so the union is the narrowest bound it can possibly apply. Without the per-collection check
    /// inside this service each of these would reach the store as an unrecognised property name and be
    /// answered by the store's default order, which is a page the caller cannot account for and cannot
    /// detect. The assertion that nothing was read is the substantive half: a refusal issued after the
    /// read would still have spent the query.
    /// </remarks>
    [Theory]
    [InlineData("RoleName")]
    [InlineData("ServiceFee")]
    [InlineData("AutoAssignment")]
    [InlineData("DisplayName")]
    [InlineData("LastLoginDate")]
    public async Task ListPortals_RefusesAnOrderingThatBelongsToAnotherCollection(string foreignField)
    {
        Harness harness = Harness.Ready();

        Result<PagedResult<PortalListItemDto>> outcome = await harness.Service.ListPortalsAsync(
            new PagedRequest { SortBy = foreignField },
            null,
            CancellationToken.None);

        outcome.IsFailure.Should().BeTrue();
        outcome.Reason!.Code.Should().Be(PagingInvalidCode);
        outcome.Reason!.Message.Should().Be($"Portals cannot be ordered by '{foreignField}'.");
        harness.Portals.Verify(
            p => p.ListAsync(
                It.IsAny<int>(),
                It.IsAny<int>(),
                It.IsAny<string?>(),
                It.IsAny<string?>(),
                It.IsAny<bool>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
    }

    /// <summary>
    /// Every field this listing's own ordering honours is accepted and reaches the store unchanged, so
    /// the allowlist is neither narrower nor wider than the ordering behind it.
    /// </summary>
    /// <param name="field">A field name declared for the portal listing.</param>
    /// <returns>A task representing the assertion.</returns>
    /// <remarks>
    /// The counterpart of the refusal above, and it is what makes the pair meaningful: a check that only
    /// refused would be satisfied by refusing everything. Each name here corresponds to exactly one arm
    /// of the repository's ordering expression - four explicit arms plus the portal name, which is that
    /// expression's default - so an entry added to the allowlist without an arm to honour it fails one
    /// of these two tests.
    /// </remarks>
    [Theory]
    [InlineData("PortalId")]
    [InlineData("PortalName")]
    [InlineData("ExpiryDate")]
    [InlineData("HostFee")]
    [InlineData("HostSpace")]
    public async Task ListPortals_AcceptsEveryFieldItsOwnOrderingHonours(string field)
    {
        Harness harness = Harness.Ready();

        Result<PagedResult<PortalListItemDto>> outcome = await harness.Service.ListPortalsAsync(
            new PagedRequest { SortBy = field },
            null,
            CancellationToken.None);

        outcome.IsSuccess.Should().BeTrue();
        harness.Portals.Verify(
            p => p.ListAsync(0, 10, null, field, false, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    /// <summary>
    /// A field name differing only in case is accepted, because the allowlist and the ordering
    /// expression both compare names without regard to case.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task ListPortals_AcceptsAPermittedFieldInAnyCasing()
    {
        Harness harness = Harness.Ready();

        Result<PagedResult<PortalListItemDto>> outcome = await harness.Service.ListPortalsAsync(
            new PagedRequest { SortBy = "hostFEE" },
            null,
            CancellationToken.None);

        outcome.IsSuccess.Should().BeTrue();
        harness.Portals.Verify(
            p => p.ListAsync(0, 10, null, "hostFEE", false, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    /// <summary>
    /// When no explicit filter is supplied the request's own search term is used, so both the query-string
    /// spellings a caller might reach for behave the same.
    /// </summary>
    /// <param name="explicitFilter">The explicit filter argument.</param>
    /// <returns>A task representing the assertion.</returns>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task ListPortals_FallsBackToTheRequestsOwnSearchTerm(string? explicitFilter)
    {
        Harness harness = Harness.Ready();

        await harness.Service.ListPortalsAsync(
            new PagedRequest { Query = "fromrequest" },
            explicitFilter,
            CancellationToken.None);

        harness.Portals.Verify(
            p => p.ListAsync(0, 10, "fromrequest", null, false, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    /// <summary>
    /// Each row carries the tenant's own host names, ordered without regard to case, and no other tenant's.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task ListPortals_BindsEachTenantsOwnHostNames()
    {
        Harness harness = Harness.Ready();
        harness.PortalPage = PagedResult<Portal>.Unpaged([StoredPortal(), SecondPortal()]);
        harness.AllAliases.Clear();
        harness.AllAliases.AddRange(
        [
            Alias(1, PortalId, "Zebra.example"),
            Alias(2, PortalId, "alpha.example"),
            Alias(3, SecondPortalId, "other.example"),
        ]);

        Result<PagedResult<PortalListItemDto>> outcome = await harness.Service.ListPortalsAsync(
            new PagedRequest { PageSize = 0 },
            null,
            CancellationToken.None);

        PortalListItemDto first = outcome.Value.Items[0];
        first.PortalId.Should().Be(PortalId);
        first.Aliases.Should().Equal(new[] { "alpha.example", "Zebra.example" });
        outcome.Value.Items[1].Aliases.Should().Equal(new[] { "other.example" });
    }

    /// <summary>
    /// A tenant that has no host name yet is still listed, with an empty set rather than a missing one.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task ListPortals_ListsATenantWithNoHostName()
    {
        Harness harness = Harness.Ready();
        harness.PortalPage = PagedResult<Portal>.Unpaged([StoredPortal()]);
        harness.AllAliases.Clear();

        Result<PagedResult<PortalListItemDto>> outcome = await harness.Service.ListPortalsAsync(
            new PagedRequest { PageSize = 0 },
            null,
            CancellationToken.None);

        outcome.Value.Items.Should().ContainSingle().Which.Aliases.Should().BeEmpty();
    }

    /// <summary>
    /// The member and page counts are read per tenant rather than derived from the row.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task ListPortals_CountsMembersAndPagesPerTenant()
    {
        Harness harness = Harness.Ready();
        harness.PortalPage = PagedResult<Portal>.Unpaged([StoredPortal(), SecondPortal()]);
        harness.UserCount = 12;
        harness.PageCount = 5;

        Result<PagedResult<PortalListItemDto>> outcome = await harness.Service.ListPortalsAsync(
            new PagedRequest { PageSize = 0 },
            null,
            CancellationToken.None);

        outcome.Value.Items.Should().OnlyContain(row => row.Users == 12 && row.Pages == 5);
    }

    /// <summary>
    /// I-01: the tallies cost two reads for the whole page rather than two reads per row, and the
    /// batched reads are asked about exactly the identifiers on the page.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task ListPortals_ReadsBothTalliesOnceForTheWholePage()
    {
        Harness harness = Harness.Ready();
        harness.PortalPage = PagedResult<Portal>.Unpaged([StoredPortal(), SecondPortal()]);

        await harness.Service.ListPortalsAsync(
            new PagedRequest { PageSize = 0 },
            null,
            CancellationToken.None);

        // The per-portal members must not be reached at all from the listing path: reaching them is
        // precisely the per-row round trip this finding was about, and a test that only counted the
        // batched calls would pass while both patterns ran side by side.
        harness.Portals.Verify(
            p => p.CountUsersAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.Never());
        harness.Portals.Verify(
            p => p.CountPagesAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.Never());

        harness.Portals.Verify(
            p => p.CountUsersForPortalsAsync(
                It.Is<IReadOnlyCollection<int>>(ids =>
                    ids.OrderBy(id => id).SequenceEqual(new[] { PortalId, SecondPortalId }.OrderBy(id => id))),
                It.IsAny<CancellationToken>()),
            Times.Once());
        harness.Portals.Verify(
            p => p.CountPagesForPortalsAsync(
                It.Is<IReadOnlyCollection<int>>(ids =>
                    ids.OrderBy(id => id).SequenceEqual(new[] { PortalId, SecondPortalId }.OrderBy(id => id))),
                It.IsAny<CancellationToken>()),
            Times.Once());
    }

    /// <summary>
    /// A tenant the batched tally omits is published with a zero rather than failing the listing.
    /// </summary>
    /// <remarks>
    /// The repository contract promises a total map, but a sparse one must not break the projection: a
    /// tenant with no members and no pages is a legitimate state, and zero is the figure the legacy grid
    /// showed for it.
    /// </remarks>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task ListPortals_TreatsAnAbsentTallyAsZero()
    {
        Harness harness = Harness.Ready();
        harness.PortalPage = PagedResult<Portal>.Unpaged([StoredPortal(), SecondPortal()]);
        harness.Portals
            .Setup(p => p.CountUsersForPortalsAsync(
                It.IsAny<IReadOnlyCollection<int>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<int, int> { [PortalId] = 7 });
        harness.Portals
            .Setup(p => p.CountPagesForPortalsAsync(
                It.IsAny<IReadOnlyCollection<int>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<int, int> { [PortalId] = 4 });

        Result<PagedResult<PortalListItemDto>> outcome = await harness.Service.ListPortalsAsync(
            new PagedRequest { PageSize = 0 },
            null,
            CancellationToken.None);

        outcome.Value.Items.Should().ContainSingle(row => row.PortalId == PortalId)
            .Which.Should().Match<PortalListItemDto>(row => row.Users == 7 && row.Pages == 4);
        outcome.Value.Items.Should().ContainSingle(row => row.PortalId == SecondPortalId)
            .Which.Should().Match<PortalListItemDto>(row => row.Users == 0 && row.Pages == 0);
    }

    /// <summary>
    /// A request that asks for no page size receives an unpaged answer; a paged request keeps the store's
    /// total.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task ListPortals_ReportsThePagingItWasAskedFor()
    {
        Harness harness = Harness.Ready();
        harness.PortalPage = PagedResult<Portal>.Unpaged([StoredPortal()]);

        Result<PagedResult<PortalListItemDto>> unpaged = await harness.Service.ListPortalsAsync(
            new PagedRequest { PageSize = 0 },
            null,
            CancellationToken.None);

        unpaged.Value.IsUnpaged.Should().BeTrue();

        harness.PortalPage = PagedResult<Portal>.Create([StoredPortal()], totalCount: 9, pageIndex: 2, pageSize: 4);

        Result<PagedResult<PortalListItemDto>> paged = await harness.Service.ListPortalsAsync(
            new PagedRequest { PageIndex = 2, PageSize = 4 },
            null,
            CancellationToken.None);

        paged.Value.TotalCount.Should().Be(9);
        paged.Value.PageIndex.Should().Be(2);
        paged.Value.PageSize.Should().Be(4);
    }

    /// <summary>
    /// The tenant detail is read through the cache under a tenant-keyed name, with a lifetime scaled by the
    /// configured performance multiplier.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task GetPortal_ReadsThroughTheCacheWithTheTenantKeyedName()
    {
        Harness harness = Harness.Ready();

        Result<PortalDetailDto?> outcome = await harness.Service
            .GetPortalAsync(PortalId, CancellationToken.None);

        outcome.IsSuccess.Should().BeTrue();
        harness.CacheKey.Should().Be($"Portal{PortalId}");
        harness.CacheExpiration.Should().Be(TimeSpan.FromMinutes(60));
    }

    /// <summary>
    /// A multiplier of nothing disables caching and reads straight through.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task GetPortal_BypassesTheCacheWhenCachingIsDisabled()
    {
        Harness harness = Harness.Ready();
        harness.Caching.PerformanceMultiplier = 0;

        Result<PortalDetailDto?> outcome = await harness.Service
            .GetPortalAsync(PortalId, CancellationToken.None);

        outcome.IsSuccess.Should().BeTrue();
        outcome.Value.Should().NotBeNull();
        harness.CacheKey.Should().BeNull();
    }

    /// <summary>
    /// A tenant that does not exist is reported as absent rather than as a failure.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task GetPortal_ReportsAbsenceForAnUnknownTenant()
    {
        Harness harness = Harness.Ready();
        harness.PortalRow = null;

        Result<PortalDetailDto?> outcome = await harness.Service
            .GetPortalAsync(PortalId, CancellationToken.None);

        outcome.IsSuccess.Should().BeTrue();
        outcome.Value.Should().BeNull();
    }

    /// <summary>
    /// The detail names the two wiring roles from the tenant's own role names, and leaves a name absent when
    /// the identifier points at nothing.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task GetPortal_NamesTheWiringRoles()
    {
        Harness harness = Harness.Ready();

        Result<PortalDetailDto?> outcome = await harness.Service
            .GetPortalAsync(PortalId, CancellationToken.None);

        PortalDetailDto detail = outcome.Value!;
        detail.AdministratorRoleId.Should().Be(AdministratorRoleId);
        detail.AdministratorRoleName.Should().Be("Administrators");
        detail.RegisteredRoleId.Should().Be(RegisteredRoleId);
        detail.RegisteredRoleName.Should().Be("Registered Users");
    }

    /// <summary>
    /// A wiring identifier the tenant's role names do not cover leaves the name absent rather than guessing.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task GetPortal_LeavesARoleNameAbsentWhenTheIdentifierPointsAtNothing()
    {
        Harness harness = Harness.Ready();
        harness.RoleNames.Clear();

        Result<PortalDetailDto?> outcome = await harness.Service
            .GetPortalAsync(PortalId, CancellationToken.None);

        outcome.Value!.AdministratorRoleName.Should().BeNull();
        outcome.Value!.RegisteredRoleName.Should().BeNull();
    }

    /// <summary>
    /// The administrator's address is read from the account within the tenant, so a portal whose
    /// administrator identifier is absent costs no account read.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task GetPortal_ReadsTheAdministratorsAddressFromTheAccount()
    {
        Harness harness = Harness.Ready();

        Result<PortalDetailDto?> outcome = await harness.Service
            .GetPortalAsync(PortalId, CancellationToken.None);

        outcome.Value!.Email.Should().Be(AdministratorEmail);
        harness.Users.Verify(
            u => u.GetAsync(PortalId, AdministratorId, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    /// <summary>
    /// A tenant with no administrator on record reads no account and reports no address.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task GetPortal_ReadsNoAccountWhenNoAdministratorIsOnRecord()
    {
        Harness harness = Harness.Ready();
        harness.PortalRow!.AdministratorId = null;

        Result<PortalDetailDto?> outcome = await harness.Service
            .GetPortalAsync(PortalId, CancellationToken.None);

        outcome.Value!.Email.Should().BeNull();
        harness.Users.Verify(
            u => u.GetAsync(It.IsAny<int?>(), It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    /// <summary>
    /// The host root page is reported alongside the tenant's own administration page, because the two
    /// together are what a navigation tree needs in order to hide the pages a tenant may not reach.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task GetPortal_ReportsTheHostRootPageAndTheTenantsAdministrationPage()
    {
        Harness harness = Harness.Ready();

        Result<PortalDetailDto?> outcome = await harness.Service
            .GetPortalAsync(PortalId, CancellationToken.None);

        outcome.Value!.SuperTabId.Should().Be(HostRootTabId);
        outcome.Value!.AdminTabId.Should().Be(90);
    }

    /// <summary>
    /// The detail carries the tenant's host names, ordered without regard to case.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task GetPortal_CarriesTheHostNamesInOrder()
    {
        Harness harness = Harness.Ready();
        harness.PortalRow!.PortalAliases.Clear();
        harness.PortalRow!.PortalAliases.Add(Alias(1, PortalId, "Zebra.example"));
        harness.PortalRow!.PortalAliases.Add(Alias(2, PortalId, "alpha.example"));

        Result<PortalDetailDto?> outcome = await harness.Service
            .GetPortalAsync(PortalId, CancellationToken.None);

        outcome.Value!.Aliases!.Select(alias => alias.HttpAlias)
            .Should().Equal(new[] { "alpha.example", "Zebra.example" });
    }

    /// <summary>
    /// Creating a tenant requires a request.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task CreatePortal_RequiresARequest()
    {
        Harness harness = Harness.Ready();

        await Assert.ThrowsAsync<ArgumentNullException>(
            () => harness.Service.CreatePortalAsync(null!, CancellationToken.None));
    }

    /// <summary>
    /// A tenant with no host name, no administrator name or no credential cannot be provisioned, because
    /// each of the three is required in order for the result to be reachable and usable.
    /// </summary>
    /// <param name="omission">Which of the three to leave out.</param>
    /// <param name="expectedMessage">The message the service is measured to report.</param>
    /// <returns>A task representing the assertion.</returns>
    [Theory]
    [InlineData("alias", "A portal alias is required in order to reach the new portal.")]
    [InlineData("username", "An administrator account name is required in order to create a portal.")]
    [InlineData("password", "An administrator password is required in order to create a portal.")]
    public async Task CreatePortal_RefusesAnIncompleteRequest(string omission, string expectedMessage)
    {
        Harness harness = Harness.Ready();
        CreatePortalRequest request = ValidCreateRequest();
        switch (omission)
        {
            case "alias":
                request.PortalAlias = "   ";
                break;
            case "username":
                request.AdministratorUsername = "  ";
                break;
            default:
                request.AdministratorPassword = string.Empty;
                break;
        }

        DomainException failure = await Assert.ThrowsAsync<DomainException>(
            () => harness.Service.CreatePortalAsync(request, CancellationToken.None));

        failure.Message.Should().Be(expectedMessage);
        harness.AddedPortals.Should().BeEmpty();
    }

    /// <summary>
    /// A host name already bound elsewhere is refused, and the trimmed form is the one that is checked.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task CreatePortal_RefusesAHostNameAlreadyBound()
    {
        Harness harness = Harness.Ready();
        harness.AliasTaken = true;
        CreatePortalRequest request = ValidCreateRequest();
        request.PortalAlias = "  " + HostAlias + "  ";

        Result<PortalDetailDto> outcome = await harness.Service
            .CreatePortalAsync(request, CancellationToken.None);

        outcome.IsFailure.Should().BeTrue();
        outcome.Reason!.Code.Should().Be(AliasDuplicateCode);
        outcome.Reason!.Message.Should()
            .Be($"The host name '{HostAlias}' is already bound to a portal.");
        harness.Aliases.Verify(
            a => a.AliasExistsAsync(HostAlias, null, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    /// <summary>
    /// An administrator account name already in use anywhere in the installation is refused, because sign-in
    /// names are installation-wide.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task CreatePortal_RefusesAnAdministratorNameAlreadyInUse()
    {
        Harness harness = Harness.Ready();
        harness.UsernameTaken = true;

        Result<PortalDetailDto> outcome = await harness.Service
            .CreatePortalAsync(ValidCreateRequest(), CancellationToken.None);

        outcome.IsFailure.Should().BeTrue();
        outcome.Reason!.Code.Should().Be(AdministratorDuplicateCode);
        outcome.Reason!.Message.Should().Be(
            $"The account name '{AdministratorUsername}' is already in use, so the portal administrator could not be created.");
        harness.Users.Verify(
            u => u.UsernameExistsAsync(AdministratorUsername, null, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    /// <summary>
    /// The host name is checked before the administrator name, so the reason a caller is given names the
    /// first thing that was wrong rather than an arbitrary one.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task CreatePortal_ChecksTheHostNameBeforeTheAdministratorName()
    {
        Harness harness = Harness.Ready();
        harness.AliasTaken = true;
        harness.UsernameTaken = true;

        Result<PortalDetailDto> outcome = await harness.Service
            .CreatePortalAsync(ValidCreateRequest(), CancellationToken.None);

        outcome.Reason!.Code.Should().Be(AliasDuplicateCode);
        harness.Users.Verify(
            u => u.UsernameExistsAsync(It.IsAny<string>(), It.IsAny<int?>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    /// <summary>
    /// A value taken between the checks and the commit is reported as the conflict it is, named from the
    /// constraint the store disclosed, and never as a server fault.
    /// </summary>
    /// <param name="constraintName">The constraint the store named when it refused the insert.</param>
    /// <param name="expectedCode">The reason code the caller must receive.</param>
    /// <param name="expectedMessage">The explanation the caller must receive.</param>
    /// <returns>A task representing the assertion.</returns>
    /// <remarks>
    /// <para>
    /// MIGRATION: SEC-F6. Provisioning performs THREE commits, and the two checks above cannot close the
    /// window in front of any of them: two requests submitting the same host name, or the same administrator
    /// name, both read "not taken" before either inserts, and the loser is refused by the unique index. Left
    /// untranslated that refusal reached the transport - which references neither the mapper nor the database
    /// client by design - and was answered 500, telling the caller the server had failed for a store that had
    /// correctly kept exactly one row.
    /// </para>
    /// <para>
    /// The code is chosen from the CONSTRAINT the store named rather than from which commit was in flight,
    /// because a single commit stages several tables and the failing one is not knowable from position. Both
    /// names are asserted, so a mapping that answered one for both would fail here; and the fallback is
    /// asserted too, because a constraint this mapping does not recognise must still be a conflict rather
    /// than reverting to a 500. The three answers all carry a <c>duplicate</c> or <c>conflict</c> token, which
    /// is what the transport's status vocabulary turns into 409 with no mapping-table change.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData(
        "IX_PortalAlias",
        AliasDuplicateCode,
        "The host name '" + HostAlias + "' is already bound to a portal.")]
    [InlineData(
        "IX_Users",
        AdministratorDuplicateCode,
        "The account name '" + AdministratorUsername + "' is already in use, so the portal administrator could not be created.")]
    [InlineData(
        "PK_SomethingElse",
        "portal.creation_conflict",
        "The portal could not be created because another request has just taken one of the values it requires to be unique.")]
    public async Task CreatePortal_RefusesAValueTakenBetweenTheChecksAndTheCommit(
        string constraintName,
        string expectedCode,
        string expectedMessage)
    {
        Harness harness = Harness.Ready();
        harness.UnitOfWork
            .Setup(unit => unit.SaveChangesAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(DuplicateKeyException.ForConstraint(constraintName, null));

        Result<PortalDetailDto> outcome = await harness.Service
            .CreatePortalAsync(ValidCreateRequest(), CancellationToken.None);

        outcome.IsFailure.Should().BeTrue(
            "the store refused the insert, so no portal exists and the caller must not be told one does");
        outcome.Reason!.Code.Should().Be(
            expectedCode,
            "the constraint the store named is what identifies which unique value was taken");
        outcome.Reason!.Message.Should().Be(expectedMessage);
    }

    /// <summary>
    /// The installation's own host settings supply the hosting terms of a new tenant, so provisioning
    /// follows whatever the operator configured rather than a hard-coded set.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task CreatePortal_TakesTheHostingTermsFromTheInstallationsSettings()
    {
        Harness harness = Harness.Ready();
        harness.HostSettingValues["HostFee"] = "12.50";
        harness.HostSettingValues["HostSpace"] = "2048";
        harness.HostSettingValues["PageQuota"] = "50";
        harness.HostSettingValues["UserQuota"] = "500";
        harness.HostSettingValues["SiteLogHistory"] = "30";
        harness.HostSettingValues["HostCurrency"] = "GBP";

        await harness.Service.CreatePortalAsync(ValidCreateRequest(), CancellationToken.None);

        Portal created = harness.AddedPortals.Should().ContainSingle().Which;
        created.HostFee.Should().Be(decimal.Parse("12.50", CultureInfo.InvariantCulture));
        created.HostSpace.Should().Be(2048);
        created.PageQuota.Should().Be(50);
        created.UserQuota.Should().Be(500);
        created.SiteLogHistory.Should().Be(30);
        created.Currency.Should().Be("GBP");
    }

    /// <summary>
    /// Missing hosting terms fall back to nothing, and a missing currency to the fallback code, so an
    /// installation that has configured none still provisions a usable tenant.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task CreatePortal_FallsBackWhenTheInstallationHasConfiguredNothing()
    {
        Harness harness = Harness.Ready();
        harness.HostSettingValues.Clear();

        await harness.Service.CreatePortalAsync(ValidCreateRequest(), CancellationToken.None);

        Portal created = harness.AddedPortals.Should().ContainSingle().Which;
        created.HostFee.Should().Be(0m);
        created.HostSpace.Should().Be(0);
        created.PageQuota.Should().Be(0);
        created.UserQuota.Should().Be(0);
        created.SiteLogHistory.Should().BeNull();
        created.Currency.Should().Be(FallbackCurrency);
        created.ExpiryDate.Should().BeNull();
    }

    /// <summary>
    /// A configured demonstration period becomes an expiry date measured from the present, which is how a
    /// trial installation stops its own tenants.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task CreatePortal_TurnsADemonstrationPeriodIntoAnExpiryDate()
    {
        Harness harness = Harness.Ready();
        harness.HostSettingValues["DemoPeriod"] = "30";

        await harness.Service.CreatePortalAsync(ValidCreateRequest(), CancellationToken.None);

        harness.AddedPortals.Single().ExpiryDate.Should().Be(Now.AddDays(30));
    }

    /// <summary>
    /// A setting that cannot be read as a number is treated as absent rather than as zero-by-accident, and a
    /// currency of only white space falls back.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task CreatePortal_TreatsAnUnreadableSettingAsAbsent()
    {
        Harness harness = Harness.Ready();
        harness.HostSettingValues["DemoPeriod"] = "not a number";
        harness.HostSettingValues["SiteLogHistory"] = "   ";
        harness.HostSettingValues["HostCurrency"] = "  ";

        await harness.Service.CreatePortalAsync(ValidCreateRequest(), CancellationToken.None);

        Portal created = harness.AddedPortals.Single();
        created.ExpiryDate.Should().BeNull();
        created.SiteLogHistory.Should().BeNull();
        created.Currency.Should().Be(FallbackCurrency);
    }

    /// <summary>
    /// A negative configured charge or quota is stored exactly as configured, because the legacy creation
    /// path compared none of the four to anything.
    /// </summary>
    /// <remarks>
    /// <c>PortalController.vb:L326-L375</c> reads each of these from an installation-wide host setting and
    /// hands the parsed value to the insert at L369 untouched. The floor an earlier revision applied
    /// borrowed the ROLE-fee guards at <c>PortalController.vb:L395,L398</c>, which clamp a
    /// <c>RoleInfo</c> while a portal template creates its roles, so applying it to a portal column
    /// changed which values an installation could store.
    /// </remarks>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task CreatePortal_StoresNegativeConfiguredTermsVerbatim()
    {
        Harness harness = Harness.Ready();
        harness.HostSettingValues["HostFee"] = "-5";
        harness.HostSettingValues["HostSpace"] = "-5";
        harness.HostSettingValues["PageQuota"] = "-5";
        harness.HostSettingValues["UserQuota"] = "-5";

        await harness.Service.CreatePortalAsync(ValidCreateRequest(), CancellationToken.None);

        Portal created = harness.AddedPortals.Single();
        created.HostFee.Should().Be(-5m);
        created.HostSpace.Should().Be(-5);
        created.PageQuota.Should().Be(-5);
        created.UserQuota.Should().Be(-5);
    }

    /// <summary>
    /// The new tenant carries the shipped language and offset, so it is addressable and renderable before
    /// anybody has configured it, and it leaves its handle to the store.
    /// </summary>
    /// <remarks>
    /// The handle is deliberately left at its type default here. The <c>GUID</c> column carries
    /// <c>DF_Portals_GUID DEFAULT (newid())</c> (<c>01.00.05:L1404</c>, re-asserted at
    /// <c>03.01.01:L1133</c>) and the entity configuration mirrors that default, so the value is issued
    /// during the insert rather than by the mapper - which is what keeps the mapper a pure function of its
    /// arguments. A fake store issues nothing, so the assertion here is the type default rather than a
    /// generated handle.
    /// </remarks>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task CreatePortal_LeavesTheHandleToTheStoreAndCarriesTheShippedDefaults()
    {
        Harness harness = Harness.Ready();

        await harness.Service.CreatePortalAsync(ValidCreateRequest(), CancellationToken.None);

        Portal created = harness.AddedPortals.Single();
        created.PortalGuid.Should().Be(Guid.Empty);
        created.DefaultLanguage.Should().Be(DefaultLanguageCode);
        created.TimeZoneOffset.Should().Be(DefaultTimeZoneOffsetMinutes);
        created.UserRegistration.Should().Be(UserRegistrationMode.NoRegistration);
        created.BannerAdvertising.Should().Be(BannerAdvertisingMode.None);
        created.PortalName.Should().Be(PortalName);
    }

    /// <summary>
    /// The host name is bound through the tenant's navigation rather than through an identifier the store has
    /// not issued yet, which is what lets both rows commit together.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task CreatePortal_BindsTheHostNameThroughTheTenantsNavigation()
    {
        Harness harness = Harness.Ready();

        await harness.Service.CreatePortalAsync(ValidCreateRequest(), CancellationToken.None);

        PortalAlias alias = harness.AddedAliases.Should().ContainSingle().Which;
        alias.HttpAlias.Should().Be(HostAlias);
        alias.Portal.Should().BeSameAs(harness.AddedPortals.Single());
    }

    /// <summary>
    /// Provisioning always creates the three stock roles, with the enrolment and visibility each one is
    /// measured to carry, because the tenant's own wiring points at two of them.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task CreatePortal_CreatesTheThreeStockRoles()
    {
        Harness harness = Harness.Ready();

        await harness.Service.CreatePortalAsync(ValidCreateRequest(), CancellationToken.None);

        harness.AddedRoles.Should().HaveCount(3);
        harness.AddedRoles.Select(role => role.RoleName)
            .Should().Equal(new[] { "Administrators", "Registered Users", "Subscribers" });

        Role administrators = harness.AddedRoles[0];
        administrators.IsPublic.Should().BeFalse();
        administrators.AutoAssignment.Should().BeFalse();
        administrators.Description.Should().Be("Portal Administrators");

        Role registered = harness.AddedRoles[1];
        registered.IsPublic.Should().BeFalse();
        registered.AutoAssignment.Should().BeTrue();

        Role subscribers = harness.AddedRoles[2];
        subscribers.IsPublic.Should().BeTrue();
        subscribers.AutoAssignment.Should().BeTrue();
        subscribers.Description.Should().Be("A public role for portal subscriptions");

        harness.AddedRoles.Should().OnlyContain(role => role.ServiceFee == 0m && role.TrialFee == 0m);
        harness.AddedRoles.Should().OnlyContain(role => role.Portal == harness.AddedPortals.Single());
    }

    /// <summary>
    /// The administrator account is created with a display name derived from the two given names when none
    /// was supplied, and is never a host account.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task CreatePortal_CreatesTheAdministratorAccount()
    {
        Harness harness = Harness.Ready();

        await harness.Service.CreatePortalAsync(ValidCreateRequest(), CancellationToken.None);

        User administrator = harness.AddedUsers.Should().ContainSingle().Which;
        administrator.Username.Should().Be(AdministratorUsername);
        administrator.FirstName.Should().Be("Ada");
        administrator.LastName.Should().Be("Lovelace");
        administrator.DisplayName.Should().Be("Ada Lovelace");
        administrator.Email.Should().Be(AdministratorEmail);
        administrator.IsSuperUser.Should().BeFalse();
        administrator.UpdatePassword.Should().BeFalse();
    }

    /// <summary>
    /// The administrator is enrolled in the tenant and in all three stock roles, through navigations rather
    /// than through unissued identifiers.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task CreatePortal_EnrolsTheAdministratorInTheTenantAndEveryStockRole()
    {
        Harness harness = Harness.Ready();

        await harness.Service.CreatePortalAsync(ValidCreateRequest(), CancellationToken.None);

        User administrator = harness.AddedUsers.Single();
        UserPortal membership = harness.AddedMemberships.Should().ContainSingle().Which;
        membership.User.Should().BeSameAs(administrator);
        membership.Portal.Should().BeSameAs(harness.AddedPortals.Single());
        membership.IsAuthorised.Should().BeTrue();
        membership.CreatedDate.Should().Be(Now);

        harness.AddedAssignments.Should().HaveCount(3);
        harness.AddedAssignments.Should().OnlyContain(assignment => assignment.User == administrator);
        harness.AddedAssignments.Select(assignment => assignment.Role)
            .Should().BeEquivalentTo(harness.AddedRoles);
        harness.AddedAssignments.Should().OnlyContain(assignment =>
            assignment.EffectiveDate == null && assignment.ExpiryDate == null && assignment.IsTrialUsed == false);
    }

    /// <summary>
    /// The credential is hashed rather than stored, and it is written only after the object graph has
    /// committed, because the external store is keyed by an identifier the first commit issues.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task CreatePortal_HashesTheCredentialAndWritesItAfterTheFirstCommit()
    {
        Harness harness = Harness.Ready();

        await harness.Service.CreatePortalAsync(ValidCreateRequest(), CancellationToken.None);

        harness.HashedSecrets.Should().Equal(new[] { AdministratorPassword });
        harness.CreatedCredentials.Should().ContainSingle();
        harness.CreatedCredentials[0].PasswordHash.Should().Be(PasswordHash);
        harness.CreatedCredentials[0].IsApproved.Should().BeTrue();
        harness.CreatedCredentials[0].UtcNow.Should().Be(Now);
        harness.CommitsBeforeCredential.Should().Be(1);
    }

    /// <summary>
    /// The tenant's wiring is stamped once the store has issued the identifiers it points at, and every
    /// write of the sequence happens inside the one transaction.
    /// </summary>
    /// <remarks>
    /// C-02: the commit count is asserted because the sequence needs more than one - three portal columns
    /// and the home page identifier reference rows the store numbers as it writes them - but the important
    /// property is that they are all enclosed by a single transactional scope, which is what makes a
    /// failure after any of them discard all of them.
    /// </remarks>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task CreatePortal_StampsTheWiringInsideOneTransaction()
    {
        Harness harness = Harness.Ready();

        await harness.Service.CreatePortalAsync(ValidCreateRequest(), CancellationToken.None);

        Portal created = harness.AddedPortals.Single();
        created.AdministratorId.Should().Be(harness.AddedUsers.Single().UserId);
        created.AdministratorRoleId.Should().Be(harness.AddedRoles[0].RoleId);
        created.RegisteredRoleId.Should().Be(harness.AddedRoles[1].RoleId);
        created.HomeTabId.Should().Be(harness.AddedTabs.Single().TabId);

        harness.UnitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Exactly(3));

        // One transaction, committed once: the staged rows become visible together or not at all.
        RecordingTransactionScope provisioning =
            harness.OpenedTransactions.Should().ContainSingle().Subject;
        provisioning.Committed.Should().BeTrue();
        provisioning.RolledBack.Should().BeFalse();
    }

    /// <summary>
    /// Provisioning runs inside one transaction, opened at default isolation and committed exactly once,
    /// which is what makes the two commits it needs a single atomic outcome.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    /// <remarks>
    /// The isolation is asserted rather than left unexamined. The two collision checks are guarded by unique
    /// indexes on the alias and account-name columns, so the store refuses a concurrent duplicate whatever
    /// this level is, and a stricter level would widen the lock footprint of the installation's busiest write
    /// for nothing. Serialisable is the right level for the DELETE path, where the check is over a COUNT that
    /// no index can guard, and the two paths are deliberately different.
    /// </remarks>
    [Fact]
    public async Task CreatePortal_CommitsOneTransactionAtDefaultIsolation()
    {
        Harness harness = Harness.Ready();

        Result<PortalDetailDto> outcome = await harness.Service
            .CreatePortalAsync(ValidCreateRequest(), CancellationToken.None);

        outcome.IsSuccess.Should().BeTrue();

        RecordingTransactionScope scope = harness.OpenedTransactions.Should().ContainSingle().Subject;
        scope.Isolation.Should().Be(TransactionIsolation.Default);
        scope.Committed.Should().BeTrue();
        scope.Disposed.Should().BeTrue();
        scope.RolledBack.Should().BeFalse();
    }

    /// <summary>
    /// A credential the external store refuses abandons the transaction, so the store reverses the whole
    /// tenant rather than an in-process routine having to undo it.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    /// <remarks>
    /// THIS IS THE ASSERTION THAT CHANGED, and the change is the fix. The earlier contract committed the
    /// tenant graph durably and then called a compensating routine to delete it row by row - a routine that
    /// could not run if the process was terminated mid-way, leaving a portal reachable at its alias whose
    /// administrator held no credential. The transaction subsumes it: nothing was ever made durable, so there
    /// is nothing to undo, and the assertions below therefore demand that NO compensating write was issued.
    /// A test that still expected the deletions would be pinning the defect in place.
    /// </remarks>
    [Fact]
    public async Task CreatePortal_AbandonsTheTransactionWhenTheCredentialIsRefused()
    {
        Harness harness = Harness.Ready();
        harness.CredentialCreated = false;

        Result<PortalDetailDto> outcome = await harness.Service
            .CreatePortalAsync(ValidCreateRequest(), CancellationToken.None);

        outcome.IsFailure.Should().BeTrue();
        outcome.Reason!.Code.Should().Be(CreationFailedCode);
        outcome.Reason!.Message.Should().Be(
            "The portal administrator's credential could not be created, so the portal was rolled back.");

        RecordingTransactionScope scope = harness.OpenedTransactions.Should().ContainSingle().Subject;
        scope.Committed.Should().BeFalse();
        scope.RolledBack.Should().BeTrue();

        // Not one compensating write. The rows are reversed by the store, so issuing deletes as well would
        // be a second mechanism that can disagree with the first.
        harness.RemovedPortals.Should().BeEmpty();
        harness.RemovedAliases.Should().BeEmpty();
        harness.RemovedRoles.Should().BeEmpty();
        harness.RemovedUsers.Should().BeEmpty();
        harness.RemovedAssignments.Should().BeEmpty();
        harness.RemovedMemberships.Should().BeEmpty();
        harness.DeletedCredentialUserIds.Should().BeEmpty();
    }

    /// <summary>
    /// A refused credential emits no audit record, because nothing was installed.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task CreatePortal_RecordsNoAuditEventWhenNothingWasInstalled()
    {
        Harness harness = Harness.Ready();
        harness.CredentialCreated = false;

        await harness.Service.CreatePortalAsync(ValidCreateRequest(), CancellationToken.None);

        harness.AuditRecords.Should().BeEmpty();
    }

    /// <summary>
    /// The enrolments the tenant graph staged are reversed by the transaction rather than withdrawn one at a
    /// time, so no assignment or membership can outlive the tenant it belonged to.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    /// <remarks>
    /// The doubles are told that the store DOES hold an assignment and a membership, which is the state the
    /// old compensating routine read in order to decide what to withdraw. Under the transaction the service
    /// never asks: the reversal covers rows it did not have to enumerate, including any it could not have
    /// known about. That is strictly stronger than the routine it replaces, which withdrew only the three
    /// enrolments it happened to remember creating.
    /// </remarks>
    [Fact]
    public async Task CreatePortal_ReversesEnrolmentsWithoutEnumeratingThem()
    {
        Harness harness = Harness.Ready();
        harness.CredentialCreated = false;

        await harness.Service.CreatePortalAsync(ValidCreateRequest(), CancellationToken.None);

        harness.RemovedAssignments.Should().BeEmpty();
        harness.RemovedMemberships.Should().BeEmpty();
        harness.OpenedTransactions.Should().ContainSingle().Which.RolledBack.Should().BeTrue();
    }

    /// <summary>
    /// A credential store that throws abandons the transaction and lets the fault surface, rather than
    /// reporting a tidy failure that hides an infrastructure problem.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    /// <remarks>
    /// The fault is deliberately NOT converted into a tidy failure result: an unavailable dependency is an
    /// infrastructure problem and reporting it as an ordinary refusal would hide it. What changed is only
    /// what happens on the way out - the transaction rolls back instead of a compensation path deleting
    /// four kinds of row and a credential it might never have written.
    /// </remarks>
    [Fact]
    public async Task CreatePortal_AbandonsTheTransactionAndRethrowsWhenTheCredentialStoreFaults()
    {
        Harness harness = Harness.Ready();
        harness.CredentialFault = new InvalidOperationException("the credential store is unavailable");

        InvalidOperationException surfaced = await Assert.ThrowsAsync<InvalidOperationException>(
            () => harness.Service.CreatePortalAsync(ValidCreateRequest(), CancellationToken.None));

        surfaced.Message.Should().Be("the credential store is unavailable");

        RecordingTransactionScope scope = harness.OpenedTransactions.Should().ContainSingle().Subject;
        scope.Committed.Should().BeFalse();
        scope.RolledBack.Should().BeTrue();

        harness.RemovedPortals.Should().BeEmpty();
        harness.DeletedCredentialUserIds.Should().BeEmpty();
        harness.AuditRecords.Should().BeEmpty();
    }

    /// <summary>
    /// A cancellation abandons the transaction exactly as any other failure does.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    /// <remarks>
    /// THIS ASSERTION IS INVERTED FROM THE CONTRACT IT REPLACES, deliberately. The compensating routine was
    /// guarded by "when the exception is not a cancellation", so a caller who withdrew mid-provisioning left
    /// a durably committed half-built tenant that nothing would ever clean up - the single worst case the
    /// compensation existed to prevent, excluded from it by construction. Because the transaction is
    /// abandoned by disposal rather than by a handler that has to decide whether to run, a cancellation is
    /// reversed like everything else and no exclusion can be written. The absence of compensating deletes
    /// below is therefore success, not the old "nothing was cleaned up" outcome that looked identical.
    /// </remarks>
    [Fact]
    public async Task CreatePortal_AbandonsTheTransactionOnCancellation()
    {
        Harness harness = Harness.Ready();
        harness.CredentialFault = new OperationCanceledException();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => harness.Service.CreatePortalAsync(ValidCreateRequest(), CancellationToken.None));

        RecordingTransactionScope scope = harness.OpenedTransactions.Should().ContainSingle().Subject;
        scope.Committed.Should().BeFalse();
        scope.RolledBack.Should().BeTrue();

        harness.RemovedPortals.Should().BeEmpty();
        harness.DeletedCredentialUserIds.Should().BeEmpty();
        harness.AuditRecords.Should().BeEmpty();
    }

    /// <summary>
    /// A successful provisioning discards both the installation-wide cache and the new tenant's own, because
    /// a newly bound host name changes which tenant a request resolves to.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task CreatePortal_DiscardsTheInstallationAndTenantCaches()
    {
        Harness harness = Harness.Ready();

        Result<PortalDetailDto> outcome = await harness.Service
            .CreatePortalAsync(ValidCreateRequest(), CancellationToken.None);

        outcome.IsSuccess.Should().BeTrue();
        harness.HostInvalidations.Should().Be(1);
        harness.InvalidatedPortalIds.Should().Contain(harness.AddedPortals.Single().PortalId);
    }

    /// <summary>
    /// C-02: the new tenant receives the nineteen default profile property definitions, under the four
    /// legacy categories and in the legacy order.
    /// </summary>
    /// <remarks>
    /// The names and the category boundaries are transcribed from
    /// <c>ProfileController.AddDefaultDefinitions</c> (L334-L361). Asserting the whole sequence rather than
    /// a count is deliberate: a count passes identically for a correct set and for nineteen wrong names.
    /// </remarks>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task CreatePortal_InstallsTheNineteenDefaultProfileDefinitions()
    {
        Harness harness = Harness.Ready();

        await harness.Service.CreatePortalAsync(ValidCreateRequest(), CancellationToken.None);

        harness.AddedProfileDefinitions.Select(d => (d.PropertyCategory, d.PropertyName)).Should().Equal(
        [
            ("Name", "Prefix"),
            ("Name", "FirstName"),
            ("Name", "MiddleName"),
            ("Name", "LastName"),
            ("Name", "Suffix"),
            ("Address", "Unit"),
            ("Address", "Street"),
            ("Address", "City"),
            ("Address", "Region"),
            ("Address", "Country"),
            ("Address", "PostalCode"),
            ("Contact Info", "Telephone"),
            ("Contact Info", "Cell"),
            ("Contact Info", "Fax"),
            ("Contact Info", "Website"),
            ("Contact Info", "IM"),
            ("Preferences", "Biography"),
            ("Preferences", "TimeZone"),
            ("Preferences", "PreferredLocale"),
        ]);

        harness.AddedProfileDefinitions.Should().OnlyContain(
            d => d.PortalId == harness.AddedPortals.Single().PortalId,
            "every definition belongs to the tenant that was just created");
    }

    /// <summary>
    /// C-02: the default definitions carry the legacy view ordering, which starts at three and steps by two.
    /// </summary>
    /// <remarks>
    /// The legacy helper incremented its counter BEFORE assigning it, so the first order is 3 and the
    /// nineteenth is 39, and no order is even. Renumbering from one would change the order every profile
    /// screen renders, which is why the exact sequence is pinned rather than merely its monotonicity.
    /// </remarks>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task CreatePortal_NumbersTheDefaultProfileDefinitionsFromThreeInStepsOfTwo()
    {
        Harness harness = Harness.Ready();

        await harness.Service.CreatePortalAsync(ValidCreateRequest(), CancellationToken.None);

        harness.AddedProfileDefinitions.Select(d => d.ViewOrder).Should().Equal(
            Enumerable.Range(0, 19).Select(index => 3 + (index * 2)));
    }

    /// <summary>
    /// C-02: the definitions carry the legacy field settings, including the zero length the six
    /// chooser-rendered properties were given and the unresolved editor type.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task CreatePortal_GivesTheDefaultProfileDefinitionsTheLegacyFieldSettings()
    {
        Harness harness = Harness.Ready();

        await harness.Service.CreatePortalAsync(ValidCreateRequest(), CancellationToken.None);

        harness.AddedProfileDefinitions.Should().OnlyContain(
            d => !d.IsRequired
                && d.IsVisible
                && d.DefaultValue == string.Empty
                && d.ModuleDefinitionId == null
                && d.DataType == 0);

        // The chooser-rendered properties impose no character bound, so their length is zero; every
        // free-text property carries the legacy fifty.
        string[] chooserRendered = ["Region", "Country", "Biography", "TimeZone", "PreferredLocale"];
        harness.AddedProfileDefinitions
            .Where(d => chooserRendered.Contains(d.PropertyName))
            .Should().HaveCount(5).And.OnlyContain(d => d.Length == 0);
        harness.AddedProfileDefinitions
            .Where(d => !chooserRendered.Contains(d.PropertyName))
            .Should().HaveCount(14).And.OnlyContain(d => d.Length == 50);
    }

    /// <summary>
    /// C-02: the new tenant receives a home page, and the portal points at it.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task CreatePortal_CreatesTheHomePageAndPointsTheTenantAtIt()
    {
        Harness harness = Harness.Ready();

        await harness.Service.CreatePortalAsync(ValidCreateRequest(), CancellationToken.None);

        Tab homePage = harness.AddedTabs.Should().ContainSingle().Subject;
        homePage.TabName.Should().Be("Home");
        homePage.Title.Should().Be("Home");
        homePage.PortalId.Should().Be(harness.AddedPortals.Single().PortalId);
        homePage.IsVisible.Should().BeTrue();
        homePage.IsDeleted.Should().BeFalse();
        homePage.ParentId.Should().BeNull("the home page sits at the root of the tenant's navigation");
        harness.AddedPortals.Single().HomeTabId.Should().Be(homePage.TabId);
    }

    /// <summary>
    /// C-02: the home page receives the three grants the legacy portal template declared for it.
    /// </summary>
    /// <remarks>
    /// View for all users, view for administrators and edit for administrators. Each grant is bound to the
    /// page by NAVIGATION rather than by identifier, because the page has no identifier until the commit
    /// that follows, so the navigation is asserted too.
    /// </remarks>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task CreatePortal_GrantsTheHomePageTheTemplatePermissions()
    {
        Harness harness = Harness.Ready();

        await harness.Service.CreatePortalAsync(ValidCreateRequest(), CancellationToken.None);

        Tab homePage = harness.AddedTabs.Single();
        int administratorsRoleId = harness.AddedRoles[0].RoleId;

        harness.AddedTabPermissions.Should().HaveCount(3);
        harness.AddedTabPermissions.Should().OnlyContain(
            grant => grant.AllowAccess && ReferenceEquals(grant.Tab, homePage));

        // Permission 3 is the page scope's view entry and 4 its edit entry in the harness catalogue; the
        // all-users grant carries the identifier the schema reserves for it rather than a role of this
        // tenant.
        harness.AddedTabPermissions.Select(grant => (grant.PermissionId, grant.RoleId)).Should().Equal(
        [
            (3, -1),
            (3, administratorsRoleId),
            (4, administratorsRoleId),
        ]);
    }

    /// <summary>
    /// SEC-F1: a page permission key the catalogue does not define is granted to nobody and RECORDED, while
    /// the tenant is still created - so an installation whose reference data is incomplete can still be
    /// administered, and the gap is discoverable.
    /// </summary>
    /// <remarks>
    /// <para>
    /// MIGRATION: THIS FACT HAS NOW BEEN STATED THREE WAYS, so the reasoning is set out in full to stop it
    /// oscillating. It began as "the missing key is skipped and the tenant is created", was replaced by "the
    /// creation is refused", and is now the first again - but for reasons the middle position did not weigh,
    /// two of which are measurements rather than judgements.
    /// </para>
    /// <para>
    /// FIRST, legacy parity. The legacy template parser resolved each key through
    /// <c>PermissionController.GetPermissionByCodeAndKey</c> and iterated the answer; an empty answer produced
    /// an empty loop, so the page was created with no grant and the portal came into being regardless
    /// (<c>ParseTabPermissions</c>, reached from <c>PortalController.CreatePortal</c> at
    /// <c>Library/Components/Portal/PortalController.vb:L980</c>). Refusing is therefore a behavioural
    /// regression against the system being migrated, which Minimal Change Clause item 3 forbids without
    /// documenting - and the AAP's own instruction for a discovered legacy defect (§0.9.1) is to annotate it
    /// in place rather than to fix it.
    /// </para>
    /// <para>
    /// SECOND, the middle position's premise was FALSE. It reasoned that such a tenant is "administrable by
    /// nobody, including its own administrator and the host". It is not: the permission service short-circuits
    /// a super user to granted before consulting any grant, and portal administration is decided from the
    /// tenant's own <c>AdministratorRoleId</c> rather than from page grants, so both the host and the
    /// tenant's administrator retain full access to a page carrying no <c>TabPermission</c> row at all. What a
    /// missing grant costs is the ANONYMOUS view grant, which is a visibility defect an operator can repair,
    /// not an administrative lock-out.
    /// </para>
    /// <para>
    /// THIRD, the measured cost of refusing. Runtime testing of the migrated console found
    /// <c>POST /api/v1/portals</c> answering <c>500</c> on every attempt - the only 5xx in the whole
    /// engagement - because a database provisioned from the migration's own schema scripts carries no
    /// <c>Permission</c> rows: those rows belong to the legacy upgrade chain, and Rule T4 forbids this work
    /// from creating them. Tenant provisioning was therefore impossible, and a foreseeable, diagnosable
    /// reference-data condition was being reported as a server fault naming no field.
    /// </para>
    /// <para>
    /// What remains from the middle position is the part that was right: the condition must not be silent. The
    /// audit record is kept, with the same three diagnostic properties, so the gap is searchable and the
    /// operator can repair the catalogue.
    /// </para>
    /// </remarks>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task CreatePortal_GrantsWhatTheCatalogueDefinesAndRecordsWhatItDoesNot()
    {
        Harness harness = Harness.Ready();
        harness.PageScopeCatalogue =
        [
            new Permission
            {
                PermissionId = 3,
                PermissionCode = TabScopeCode,
                PermissionKey = PermissionKey.VIEW,
                PermissionName = "View Tab",
            },
        ];

        Result<PortalDetailDto> outcome = await harness.Service
            .CreatePortalAsync(ValidCreateRequest(), CancellationToken.None);

        outcome.IsSuccess.Should().BeTrue("an incomplete catalogue is an installation gap, not a bad request");
        harness.OpenedTransactions.Should().ContainSingle().Which.RolledBack.Should().BeFalse(
            "the creation completes, so its transaction commits");

        // The two grants the catalogue CAN support are staged - anonymous view and administrator view - and
        // the third, which needs the absent edit definition, is not.
        harness.AddedTabPermissions.Should().HaveCount(2);
        harness.AddedTabPermissions.Should().OnlyContain(grant => grant.PermissionId == 3 && grant.AllowAccess);

        // Recorded rather than silent: an operator needs to know which key the installation lacks, because
        // the repair is an upgrade-script one and nothing in the response mentions it.
        AuditEvent gap = harness.AuditRecords.Should().ContainSingle(record =>
            record.Outcome == AuditOutcome.Failed).Subject;
        gap.FailureCode.Should().Be("portal.permission_catalogue_incomplete");
        gap.Properties["PermissionCode"].Should().Be(TabScopeCode);
        gap.Properties["MissingViewDefinition"].Should().Be(bool.FalseString);
        gap.Properties["MissingEditDefinition"].Should().Be(bool.TrueString);
    }

    /// <summary>
    /// SEC-F1: the home page's grants are resolved without consulting any page row, so a database that holds
    /// no page at the zero identity seed still receives all three of them.
    /// </summary>
    /// <remarks>
    /// This is the regression pin for the defect itself. The harness answers the page-scoped-by-TAB read with
    /// nothing - which is what production answers for a page that does not exist yet, since the home page has
    /// no identifier until the creation commits and <c>dbo.Tabs</c> is <c>IDENTITY (0, 1)</c> - and the three
    /// grants must still be staged. Before the fix this produced zero grants and a <c>201 Created</c>.
    /// </remarks>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task CreatePortal_GrantsTheHomePageEvenWhenNoPageOccupiesTheZeroIdentitySeed()
    {
        Harness harness = Harness.Ready();

        Result<PortalDetailDto> outcome = await harness.Service
            .CreatePortalAsync(ValidCreateRequest(), CancellationToken.None);

        outcome.IsSuccess.Should().BeTrue();
        harness.AddedTabPermissions.Should().HaveCount(3);

        harness.Permissions.Verify(
            p => p.GetByTabIdAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "the catalogue must never be resolved through a page that has no identifier yet");
        harness.Permissions.Verify(
            p => p.GetByCodeAndKeyAsync(TabScopeCode, PermissionKey.VIEW, It.IsAny<CancellationToken>()),
            Times.Once);
        harness.Permissions.Verify(
            p => p.GetByCodeAndKeyAsync(TabScopeCode, PermissionKey.EDIT, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    /// <summary>
    /// C-02: every stage of the sequence is staged inside the one transactional scope, so nothing is written
    /// outside it.
    /// </summary>
    /// <remarks>
    /// The transactional wrapper is asserted to run exactly once, and the two stages the review found missing
    /// are asserted to have produced their rows, which together pin the property that a tenant is published
    /// whole or not at all.
    /// </remarks>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task CreatePortal_StagesEveryCreationStageInsideOneTransaction()
    {
        Harness harness = Harness.Ready();

        await harness.Service.CreatePortalAsync(ValidCreateRequest(), CancellationToken.None);

        RecordingTransactionScope provisioning =
            harness.OpenedTransactions.Should().ContainSingle().Subject;
        provisioning.Committed.Should().BeTrue();

        harness.AddedPortals.Should().ContainSingle();
        harness.AddedAliases.Should().ContainSingle();
        harness.AddedRoles.Should().HaveCount(3);
        harness.AddedUsers.Should().ContainSingle();
        harness.AddedMemberships.Should().ContainSingle();
        harness.AddedAssignments.Should().HaveCount(3);
        harness.AddedProfileDefinitions.Should().HaveCount(19);
        harness.AddedTabs.Should().ContainSingle();
        harness.AddedTabPermissions.Should().HaveCount(3);
    }

    /// <summary>
    /// C-02: a stage that fails inside the transaction stages nothing observable, and no partial tenant is
    /// published.
    /// </summary>
    /// <remarks>
    /// The refused credential is the failure this arrangement was built for: the abort is raised as an
    /// exception so that the transaction rolls back rather than committing a tenant nobody can sign in to.
    /// The definitions and the home page follow the credential in the sequence, so a refusal must leave
    /// neither behind.
    /// </remarks>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task CreatePortal_StagesNoLaterStageWhenAnEarlierOneAborts()
    {
        Harness harness = Harness.Ready();
        harness.CredentialCreated = false;

        Result<PortalDetailDto> outcome = await harness.Service
            .CreatePortalAsync(ValidCreateRequest(), CancellationToken.None);

        outcome.IsSuccess.Should().BeFalse();
        harness.AddedProfileDefinitions.Should().BeEmpty();
        harness.AddedTabs.Should().BeEmpty();
        harness.AddedTabPermissions.Should().BeEmpty();
        harness.AuditEvents.Should().BeEmpty("nothing was installed, so nothing is recorded as installed");
    }

    /// <summary>
    /// M-07: a successful provisioning records the tenant-installed fact under the legacy event name.
    /// </summary>
    /// <remarks>
    /// The name is the one <c>Signup.ascx.vb:L312</c> emitted, so an operator's existing queries keep
    /// matching. The administrator's password is asserted ABSENT: the legacy entry attached fourteen
    /// properties and the credential was not among them, and that decision is preserved rather than
    /// reversed.
    /// </remarks>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task CreatePortal_RecordsTheTenantInstalledAudit()
    {
        Harness harness = Harness.Ready();

        await harness.Service.CreatePortalAsync(ValidCreateRequest(), CancellationToken.None);

        // Both legacy intents of an installation are recorded: PORTAL_CREATED, the enumeration's accurate
        // member, and HOST_ALERT, the type PortalController.vb:L1140-L1141 actually raised.
        harness.AuditEvents.Select(candidate => candidate.EventName)
            .Should()
            .BeEquivalentTo(["PORTAL_CREATED", "HOST_ALERT"]);

        (string EventName, IReadOnlyDictionary<string, string?> Properties) recorded =
            harness.AuditEvents.Should().ContainSingle(candidate => candidate.EventName == "PORTAL_CREATED").Subject;

        recorded.Properties["IsChildPortal"].Should().Be("False");
        recorded.Properties.Should().ContainKey("AdministratorId");
        recorded.Properties.Should().ContainKey("DescriptionSupplied");
        recorded.Properties.Should().ContainKey("KeywordsSupplied");

        // MIGRATION: the tenant NAME and ALIAS were recorded by one revision and are deliberately absent.
        // Both are caller-supplied text bounded in length and not in content, and the record's envelope
        // already carries the tenant key - so they add no identifying power to a log whose sink admits only
        // identifiers, closed vocabularies and booleans.
        recorded.Properties.Should().NotContainKeys("PortalName", "PortalAlias");
        recorded.Properties.Values.Should().NotContain(PortalName);
        recorded.Properties.Values.Should().NotContain(HostAlias);
        recorded.Properties.Values.Should().NotContain(
            AdministratorPassword,
            "the legacy entry did not record the credential and neither does this one");
        recorded.Properties.Should().NotContainKey(
            "AdministratorUsername",
            "the administrator is identified by a stable identifier rather than by personal data, "
            + "because the general application log is not a records-management store");
        recorded.Properties.Values.Should().NotContain(AdministratorUsername);
    }

    /// <summary>
    /// M-07: a removal records the tenant-removed fact under the legacy event name, carrying the property the
    /// legacy screens carried.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task DeletePortal_RecordsTheTenantRemovedAudit()
    {
        Harness harness = Harness.Ready();
        harness.RemainingPortalCount = 2;

        Result outcome = await harness.Service.DeletePortalAsync(PortalId, CancellationToken.None);

        outcome.IsSuccess.Should().BeTrue();
        (string EventName, IReadOnlyDictionary<string, string?> Properties) recorded =
            harness.AuditEvents.Should().ContainSingle().Subject;

        recorded.EventName.Should().Be("PORTAL_DELETED");
        recorded.Properties.Should().NotContainKey("PortalName");
        recorded.Properties.Should().ContainKey("PortalId");
    }

    /// <summary>
    /// M-07: a refused removal records nothing, because no tenant was removed.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task DeletePortal_RecordsNoAuditWhenTheRemovalIsRefused()
    {
        Harness harness = Harness.Ready();
        harness.RemainingPortalCount = 1;

        Result outcome = await harness.Service.DeletePortalAsync(PortalId, CancellationToken.None);

        outcome.IsSuccess.Should().BeFalse();
        harness.AuditEvents.Should().BeEmpty();
    }

    /// <summary>
    /// A tenant that cannot be read back is reported as a failure rather than as a success with nothing in
    /// it.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task CreatePortal_ReportsAFailureWhenTheTenantCannotBeReadBack()
    {
        Harness harness = Harness.Ready();
        harness.EchoCreatedPortal = false;
        harness.PortalRow = null;

        Result<PortalDetailDto> outcome = await harness.Service
            .CreatePortalAsync(ValidCreateRequest(), CancellationToken.None);

        outcome.IsFailure.Should().BeTrue();
        outcome.Reason!.Code.Should().Be(CreationFailedCode);
        outcome.Reason!.Message.Should().Be("The portal was created but could not be read back.");
    }

    /// <summary>
    /// Updating a tenant requires a request.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task UpdatePortal_RequiresARequest()
    {
        Harness harness = Harness.Ready();

        await Assert.ThrowsAsync<ArgumentNullException>(
            () => harness.Service.UpdatePortalAsync(PortalId, null!, CancellationToken.None));
    }

    /// <summary>
    /// An unknown tenant is reported as absent rather than as a failure, and nothing is written.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task UpdatePortal_ReportsAbsenceForAnUnknownTenant()
    {
        Harness harness = Harness.Ready();
        harness.PortalRow = null;

        Result<PortalDetailDto?> outcome = await harness.Service
            .UpdatePortalAsync(PortalId, ValidUpdateRequest(), CancellationToken.None);

        outcome.IsSuccess.Should().BeTrue();
        outcome.Value.Should().BeNull();
        harness.UnitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    /// <summary>
    /// A tenant administrator that leaves every host-only term as it stands is permitted, which is the
    /// ordinary case the endpoint exists for.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task UpdatePortal_PermitsATenantAdministratorThatChangesNoHostOnlyTerm()
    {
        Harness harness = Harness.Ready();
        harness.SuperUser = false;

        Result<PortalDetailDto?> outcome = await harness.Service
            .UpdatePortalAsync(PortalId, ValidUpdateRequest(), CancellationToken.None);

        outcome.IsSuccess.Should().BeTrue();
        harness.PortalRow!.PortalName.Should().Be("Renamed");
    }

    /// <summary>
    /// A tenant administrator that changes any host-only term is refused, one term at a time.
    /// </summary>
    /// <param name="term">The single host-only term to alter.</param>
    /// <returns>A task representing the assertion.</returns>
    [Theory]
    [InlineData("hostfee")]
    [InlineData("hostspace")]
    [InlineData("pagequota")]
    [InlineData("userquota")]
    [InlineData("sitelog")]
    [InlineData("expiry")]
    public async Task UpdatePortal_RefusesATenantAdministratorThatChangesAHostOnlyTerm(string term)
    {
        Harness harness = Harness.Ready();
        harness.SuperUser = false;
        UpdatePortalRequest request = ValidUpdateRequest();
        switch (term)
        {
            case "hostfee":
                request.HostFee = harness.PortalRow!.HostFee + 1m;
                break;
            case "hostspace":
                request.HostSpace = harness.PortalRow!.HostSpace + 1;
                break;
            case "pagequota":
                request.PageQuota = harness.PortalRow!.PageQuota + 1;
                break;
            case "userquota":
                request.UserQuota = harness.PortalRow!.UserQuota + 1;
                break;
            case "sitelog":
                request.SiteLogHistory = 30;
                break;
            default:
                request.ExpiryDate = Now.AddYears(1);
                break;
        }

        UnauthorizedAccessException refusal = await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => harness.Service.UpdatePortalAsync(PortalId, request, CancellationToken.None));

        refusal.Message.Should().Be(
            "Only a host account may change the hosting charge, the quotas, the site-log retention period or the expiry date of a portal.");
        harness.UnitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    /// <summary>
    /// A tenant administrator that omits a host-only numeric term the tenant actually carries is refused,
    /// because this request is a whole-row replacement and an omitted numeric term is written as zero. A
    /// guard that only looked for a submitted value would let a tenant administrator waive its own hosting
    /// charge and lift every quota simply by leaving the fields out.
    /// </summary>
    /// <param name="term">The host-only term the stored tenant carries and the request omits.</param>
    /// <returns>A task representing the assertion.</returns>
    [Theory]
    [InlineData("hostfee")]
    [InlineData("hostspace")]
    [InlineData("pagequota")]
    [InlineData("userquota")]
    public async Task UpdatePortal_RefusesATenantAdministratorThatOmitsAHostOnlyTermTheTenantCarries(string term)
    {
        Harness harness = Harness.Ready();
        harness.SuperUser = false;
        switch (term)
        {
            case "hostfee":
                harness.PortalRow!.HostFee = 25m;
                break;
            case "hostspace":
                harness.PortalRow!.HostSpace = 2048;
                break;
            case "pagequota":
                harness.PortalRow!.PageQuota = 50;
                break;
            default:
                harness.PortalRow!.UserQuota = 500;
                break;
        }

        UpdatePortalRequest request = ValidUpdateRequest();
        request.HostFee.Should().BeNull("the omission is the whole point of this case");
        request.HostSpace.Should().BeNull();
        request.PageQuota.Should().BeNull();
        request.UserQuota.Should().BeNull();

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => harness.Service.UpdatePortalAsync(PortalId, request, CancellationToken.None));

        harness.PortalRow!.PortalName.Should().Be(PortalName, "nothing may be written once the guard refuses");
        harness.UnitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    /// <summary>
    /// A tenant administrator that echoes the host-only terms back unchanged is permitted, so the guard
    /// costs a caller that is genuinely not changing them nothing.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task UpdatePortal_PermitsATenantAdministratorThatEchoesTheHostOnlyTerms()
    {
        Harness harness = Harness.Ready();
        harness.SuperUser = false;
        harness.PortalRow!.HostFee = 25m;
        harness.PortalRow!.HostSpace = 2048;
        harness.PortalRow!.PageQuota = 50;
        harness.PortalRow!.UserQuota = 500;
        harness.PortalRow!.SiteLogHistory = 30;
        harness.PortalRow!.ExpiryDate = Now.AddYears(1);

        UpdatePortalRequest request = ValidUpdateRequest();
        request.HostFee = 25m;
        request.HostSpace = 2048;
        request.PageQuota = 50;
        request.UserQuota = 500;
        request.SiteLogHistory = 30;
        request.ExpiryDate = Now.AddYears(1);

        Result<PortalDetailDto?> outcome = await harness.Service
            .UpdatePortalAsync(PortalId, request, CancellationToken.None);

        outcome.IsSuccess.Should().BeTrue();
        harness.PortalRow!.HostFee.Should().Be(25m);
        harness.PortalRow!.UserQuota.Should().Be(500);
    }

    /// <summary>
    /// A host account may change every host-only term, which is the other half of the same rule.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task UpdatePortal_PermitsAHostAccountToChangeEveryHostOnlyTerm()
    {
        Harness harness = Harness.Ready();
        harness.SuperUser = true;
        UpdatePortalRequest request = ValidUpdateRequest();
        request.HostFee = 42.75m;
        request.HostSpace = 128;
        request.PageQuota = 25;
        request.UserQuota = 50;
        request.SiteLogHistory = 14;
        request.ExpiryDate = Now.AddYears(2);

        Result<PortalDetailDto?> outcome = await harness.Service
            .UpdatePortalAsync(PortalId, request, CancellationToken.None);

        outcome.IsSuccess.Should().BeTrue();
        harness.PortalRow!.HostFee.Should().Be(decimal.Parse("42.75", CultureInfo.InvariantCulture));
        harness.PortalRow!.HostSpace.Should().Be(128);
        harness.PortalRow!.PageQuota.Should().Be(25);
        harness.PortalRow!.UserQuota.Should().Be(50);
        harness.PortalRow!.SiteLogHistory.Should().Be(14);
        harness.PortalRow!.ExpiryDate.Should().Be(Now.AddYears(2));
    }

    /// <summary>
    /// SEC-011 REGRESSION. A caller whose TOKEN still claims host status but whose STORED account no longer has
    /// it is refused the host-only exemption.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    /// <remarks>
    /// <para>
    /// This is the shape the finding described. Access tokens are bearer credentials with a lifetime of their
    /// own, so every claim inside one is a statement about the past: an account removed from the host role
    /// keeps a syntactically valid token asserting the old status until it expires. Reading the exemption from
    /// that claim meant a demoted account could still waive a portal's hosting charge and lift all three
    /// quotas, on any portal it could otherwise administer, for the remainder of the token's life - and the
    /// only way to stop it would have been to shorten every token's lifetime, which is a different trade.
    /// </para>
    /// <para>
    /// The claim is deliberately left ASSERTING host status here rather than being cleared. A fact that cleared
    /// it would pass whether or not the implementation consults the store, because both sources would then
    /// agree; making them disagree is what pins which one is read.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task UpdatePortal_RefusesAHostOnlyChangeWhenOnlyTheTokenStillClaimsHostStatus()
    {
        Harness harness = Harness.Ready();
        harness.SuperUser = false;
        harness.SuperUserClaim = true;
        UpdatePortalRequest request = ValidUpdateRequest();
        request.HostFee = 99m;

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => harness.Service.UpdatePortalAsync(PortalId, request, CancellationToken.None));

        harness.PortalRow!.HostFee.Should().Be(
            0m,
            "a demoted account must not be able to waive the hosting charge with a token minted before the "
            + "demotion");
    }

    /// <summary>
    /// THE CONVERSE, WHICH IS WHAT PROVES THE CLAIM IS NOT CONSULTED AT ALL. A caller whose stored account IS a
    /// host account is granted the exemption even though its token claims otherwise.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    /// <remarks>
    /// Asserted in both directions on purpose. The refusal above is satisfied by an implementation that
    /// requires the claim AND the store to agree, which would still be reading the claim; only a fact in which
    /// the store alone admits the change can distinguish that from reading the store alone. It also states the
    /// operational half: a promotion takes effect on the next request rather than on the next sign-in.
    /// </remarks>
    [Fact]
    public async Task UpdatePortal_PermitsAHostOnlyChangeWhenOnlyTheStoreSaysHostAccount()
    {
        Harness harness = Harness.Ready();
        harness.SuperUser = true;
        harness.SuperUserClaim = false;
        UpdatePortalRequest request = ValidUpdateRequest();
        request.HostFee = 42.75m;

        Result<PortalDetailDto?> outcome = await harness.Service
            .UpdatePortalAsync(PortalId, request, CancellationToken.None);

        outcome.IsSuccess.Should().BeTrue();
        harness.PortalRow!.HostFee.Should().Be(
            decimal.Parse("42.75", CultureInfo.InvariantCulture),
            "the stored account is the authority, so a stale claim neither grants nor withholds the exemption");
    }

    /// <summary>
    /// An UNAUTHENTICATED caller never receives the exemption, and no store read can give it one.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    /// <remarks>
    /// The fail-closed arm. With no account key there is nothing to look up, so the guard must refuse rather
    /// than fall through - a lookup of "no user" must not be mistaken for a lookup that found a host account,
    /// and an absent account must not be mistaken for an unrestricted one.
    /// </remarks>
    [Fact]
    public async Task UpdatePortal_RefusesAHostOnlyChangeFromAnUnauthenticatedCaller()
    {
        Harness harness = Harness.Ready();
        harness.SuperUser = true;
        harness.CallerUserId = null;
        UpdatePortalRequest request = ValidUpdateRequest();
        request.HostFee = 99m;

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => harness.Service.UpdatePortalAsync(PortalId, request, CancellationToken.None));

        harness.PortalRow!.HostFee.Should().Be(0m);
    }

    /// <summary>
    /// The guard is applied before anything is written, so a refused update leaves the stored row untouched.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task UpdatePortal_LeavesTheStoredRowUntouchedWhenTheGuardRefuses()
    {
        Harness harness = Harness.Ready();
        harness.SuperUser = false;
        UpdatePortalRequest request = ValidUpdateRequest();
        request.HostFee = 99m;

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => harness.Service.UpdatePortalAsync(PortalId, request, CancellationToken.None));

        harness.PortalRow!.PortalName.Should().Be(PortalName);
        harness.PortalRow!.HostFee.Should().Be(0m);
    }

    /// <summary>
    /// A successful update commits once, discards the tenant's cache and reports the re-read detail.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task UpdatePortal_CommitsDiscardsTheTenantCacheAndReportsTheDetail()
    {
        Harness harness = Harness.Ready();

        Result<PortalDetailDto?> outcome = await harness.Service
            .UpdatePortalAsync(PortalId, ValidUpdateRequest(), CancellationToken.None);

        outcome.Value!.PortalName.Should().Be("Renamed");
        harness.UnitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
        harness.InvalidatedPortalIds.Should().Equal(new[] { PortalId });
        harness.HostInvalidations.Should().Be(0);
    }

    /// <summary>
    /// The re-read after an update bypasses the cache, so a caller never sees the value the update replaced.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task UpdatePortal_ReadsTheDetailWithoutConsultingTheCache()
    {
        Harness harness = Harness.Ready();

        await harness.Service.UpdatePortalAsync(PortalId, ValidUpdateRequest(), CancellationToken.None);

        harness.CacheKey.Should().BeNull();
    }

    /// <summary>A foreign portal membership cannot be injected as the designated administrator.</summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task UpdatePortal_RefusesAnAdministratorWithoutPortalMembership()
    {
        Harness harness = Harness.Ready();
        harness.ExistingMembership = null;
        UpdatePortalRequest request = ValidUpdateRequest();
        request.AdministratorId = 9_999;

        Result<PortalDetailDto?> outcome = await harness.Service.UpdatePortalAsync(
            PortalId,
            request,
            CancellationToken.None);

        outcome.IsFailure.Should().BeTrue();
        outcome.Error!.Code.Should().Be("portal.administrator_invalid");
        harness.UnitOfWork.Verify(
            unit => unit.SaveChangesAsync(It.IsAny<CancellationToken>()),
            Times.Never);
    }

    /// <summary>A page from another portal cannot be injected into any portal navigation reference.</summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task UpdatePortal_RefusesAReferencedPageOwnedByAnotherPortal()
    {
        Harness harness = Harness.Ready();
        UpdatePortalRequest request = ValidUpdateRequest();
        request.HomeTabId = 9_999;
        harness.Portals
            .Setup(portals => portals.TabBelongsToPortalAsync(
                PortalId,
                9_999,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        Result<PortalDetailDto?> outcome = await harness.Service.UpdatePortalAsync(
            PortalId,
            request,
            CancellationToken.None);

        outcome.IsFailure.Should().BeTrue();
        outcome.Error!.Code.Should().Be("portal.tab_reference_invalid");
        harness.UnitOfWork.Verify(
            unit => unit.SaveChangesAsync(It.IsAny<CancellationToken>()),
            Times.Never);
    }

    /// <summary>
    /// Deleting a tenant that does not exist is refused.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task DeletePortal_RefusesAnUnknownTenant()
    {
        Harness harness = Harness.Ready();
        harness.PortalRow = null;

        Result outcome = await harness.Service.DeletePortalAsync(PortalId, CancellationToken.None);

        outcome.IsFailure.Should().BeTrue();
        outcome.Reason!.Code.Should().Be(NotFoundCode);
        outcome.Reason!.Message.Should().Be($"No portal bears identifier {PortalId}.");
    }

    /// <summary>
    /// The last remaining tenant cannot be removed, because an installation with no tenant cannot serve a
    /// request or be administered back into a working state.
    /// </summary>
    /// <param name="remaining">The number of tenants the installation still holds.</param>
    /// <returns>A task representing the assertion.</returns>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task DeletePortal_RefusesToRemoveTheLastRemainingTenant(int remaining)
    {
        Harness harness = Harness.Ready();
        harness.RemainingPortalCount = remaining;

        Result outcome = await harness.Service.DeletePortalAsync(PortalId, CancellationToken.None);

        outcome.IsFailure.Should().BeTrue();
        outcome.Reason!.Code.Should().Be(LastRemainingCode);

        // The message leads with the legacy wording verbatim. IPortalService documents this rule as
        // yielding "the shared message keyed LastPortal, whose wording is 'You Can Not Delete The Last
        // Portal In Your Database'", sourced from Website/App_GlobalResources/SharedResources.resx:942,
        // and the migration discipline requires error messages to stay equivalent to the ones existing
        // operators already recognise. Asserting the legacy sentence is therefore asserting the parity
        // requirement itself, not merely the current phrasing; the trailing sentence explains the rule to
        // a caller that has never seen the legacy screen.
        outcome.Reason!.Message.Should().Be(
            "You Can Not Delete The Last Portal In Your Database. The installation must retain at least one portal.");
        harness.RemovedPortals.Should().BeEmpty();
    }

    /// <summary>
    /// The remaining count is probed with the cheapest page the store can answer, because only the total
    /// matters.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task DeletePortal_ProbesTheRemainingCountWithASingleRow()
    {
        Harness harness = Harness.Ready();

        await harness.Service.DeletePortalAsync(PortalId, CancellationToken.None);

        harness.Portals.Verify(
            p => p.ListAsync(0, 1, null, null, false, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    /// <summary>
    /// Removing a tenant releases its host names first, so the names can be bound again immediately.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task DeletePortal_ReleasesEveryHostNameBeforeRemovingTheTenant()
    {
        Harness harness = Harness.Ready();
        harness.PortalRow!.PortalAliases.Clear();
        harness.PortalRow!.PortalAliases.Add(Alias(1, PortalId, "first.example"));
        harness.PortalRow!.PortalAliases.Add(Alias(2, PortalId, "second.example"));

        Result outcome = await harness.Service.DeletePortalAsync(PortalId, CancellationToken.None);

        outcome.IsSuccess.Should().BeTrue();
        harness.RemovedAliases.Should().HaveCount(2);
        harness.RemovedPortals.Should().ContainSingle().Which.Should().BeSameAs(harness.PortalRow);
    }

    /// <summary>
    /// A member that belongs to another tenant loses only the expiring membership; its installation-wide
    /// account, credential and sessions remain usable by the tenant it still belongs to.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task DeletePortal_RetainsAMemberThatBelongsToAnotherTenant()
    {
        Harness harness = Harness.Ready();
        User account = PortalMember(41, isSuperUser: false, PortalId, SecondPortalId);
        harness.PortalMembers.Add(account);

        Result outcome = await harness.Service.DeletePortalAsync(PortalId, CancellationToken.None);

        outcome.IsSuccess.Should().BeTrue();
        harness.RemovedMemberships.Should().ContainSingle()
            .Which.Should().BeSameAs(account.UserPortals.Single(row => row.PortalId == PortalId));
        harness.RemovedUsers.Should().BeEmpty();
        harness.DeletedCredentialUserIds.Should().BeEmpty();
        harness.RevokedSessionUserIds.Should().BeEmpty();
    }

    /// <summary>
    /// A non-host member whose expiring membership is its last tenancy is removed globally together with
    /// its direct grants, external credential and live sessions.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task DeletePortal_RemovesAFinalMembershipAccountAndItsExternalState()
    {
        Harness harness = Harness.Ready();
        User account = PortalMember(42, isSuperUser: false, PortalId);
        harness.PortalMembers.Add(account);

        Result outcome = await harness.Service.DeletePortalAsync(PortalId, CancellationToken.None);

        outcome.IsSuccess.Should().BeTrue();
        harness.RevokedSessionUserIds.Should().Equal(account.UserId);
        harness.DeletedCredentialUserIds.Should().Equal(account.UserId);
        harness.RemovedUsers.Should().ContainSingle().Which.Should().BeSameAs(account);
        harness.RemovedMemberships.Should().BeEmpty(
            "the account delete owns its final membership through the database cascade");
        harness.Permissions.Verify(
            repository => repository.DeleteModulePermissionsByUserIdAsync(
                PortalId,
                account.UserId,
                It.IsAny<CancellationToken>()),
            Times.Once);
        harness.Permissions.Verify(
            repository => repository.DeleteTabPermissionsByUserIdAsync(
                PortalId,
                account.UserId,
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    /// <summary>
    /// A host account is installation-wide even when this is its only membership row, so deleting one
    /// tenant may remove that row but must not delete the operator, credential or sessions.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task DeletePortal_ProtectsASuperUserFromGlobalRemoval()
    {
        Harness harness = Harness.Ready();
        User account = PortalMember(43, isSuperUser: true, PortalId);
        harness.PortalMembers.Add(account);

        Result outcome = await harness.Service.DeletePortalAsync(PortalId, CancellationToken.None);

        outcome.IsSuccess.Should().BeTrue();
        harness.RemovedMemberships.Should().ContainSingle()
            .Which.Should().BeSameAs(account.UserPortals.Single());
        harness.RemovedUsers.Should().BeEmpty();
        harness.DeletedCredentialUserIds.Should().BeEmpty();
        harness.RevokedSessionUserIds.Should().BeEmpty();
    }

    /// <summary>
    /// Refusing to remove a final member's external credential abandons the whole tenant transaction rather
    /// than committing a portal whose user has become an unreachable orphan.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task DeletePortal_RollsBackWhenAFinalMembersCredentialCannotBeRemoved()
    {
        Harness harness = Harness.Ready();
        User account = PortalMember(44, isSuperUser: false, PortalId);
        harness.PortalMembers.Add(account);
        harness.CredentialDeleted = false;

        Result outcome = await harness.Service.DeletePortalAsync(PortalId, CancellationToken.None);

        outcome.IsFailure.Should().BeTrue();
        outcome.Reason!.Code.Should().Be(MemberCredentialRemovalFailedCode);
        harness.OpenedTransactions.Should().ContainSingle().Which.RolledBack.Should().BeTrue();
        harness.RemovedPortals.Should().BeEmpty();
        harness.RemovedAliases.Should().BeEmpty();
        harness.RemovedUsers.Should().BeEmpty();
        harness.UnitOfWork.Verify(
            unit => unit.SaveChangesAsync(It.IsAny<CancellationToken>()),
            Times.Never);
        harness.AuditRecords.Should().BeEmpty();
    }

    /// <summary>
    /// A session-store refusal is detected before any relational removal is staged, so the tenant and its
    /// final member remain intact.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task DeletePortal_AbandonsBeforeDatabaseRemovalWhenSessionsCannotBeEnded()
    {
        Harness harness = Harness.Ready();
        User account = PortalMember(45, isSuperUser: false, PortalId);
        harness.PortalMembers.Add(account);
        harness.SessionRevocationResult = Result.Failure(
            "TOKEN_STORE_UNAVAILABLE",
            "The token store is unavailable.");

        Result outcome = await harness.Service.DeletePortalAsync(PortalId, CancellationToken.None);

        outcome.IsFailure.Should().BeTrue();
        outcome.Reason!.Code.Should().Be(MemberSessionRevocationFailedCode);
        harness.RevokedSessionUserIds.Should().Equal(account.UserId);
        harness.DeletedCredentialUserIds.Should().BeEmpty();
        harness.RemovedUsers.Should().BeEmpty();
        harness.RemovedMemberships.Should().BeEmpty();
        harness.RemovedPortals.Should().BeEmpty();
        harness.Permissions.Verify(
            repository => repository.DeleteModulePermissionsByUserIdAsync(
                It.IsAny<int>(),
                It.IsAny<int>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
        harness.UnitOfWork.Verify(
            unit => unit.SaveChangesAsync(It.IsAny<CancellationToken>()),
            Times.Never);
    }

    /// <summary>
    /// Removing a tenant discards its pages, its own cache and the installation-wide cache, because a
    /// released host name must stop resolving.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task DeletePortal_DiscardsThePagesTheTenantAndTheInstallation()
    {
        Harness harness = Harness.Ready();

        await harness.Service.DeletePortalAsync(PortalId, CancellationToken.None);

        harness.InvalidatedTabsPortalIds.Should().Equal(new[] { PortalId });
        harness.InvalidatedPortalIds.Should().Equal(new[] { PortalId });
        harness.HostInvalidations.Should().Be(1);
        harness.UnitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    /// <summary>
    /// Removal runs inside one SERIALISABLE transaction, opened before the tenant is even read and
    /// committed once, so the last-remaining check and the delete it guards cannot be interleaved with a
    /// concurrent removal.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    /// <remarks>
    /// The isolation matters here in a way it does not on the create path, and that asymmetry is the point.
    /// The guard is over a COUNT of the remaining tenants, and no index can make a count-then-delete atomic:
    /// two concurrent removals could each count two, each conclude one would remain, and between them empty
    /// the installation - the precise condition the guard exists to prevent. Serialisable is what makes the
    /// count a decision the second transaction cannot invalidate. The scope is opened before the READ as well
    /// as before the write, so a tenant another caller has already removed cannot be removed a second time.
    /// </remarks>
    [Fact]
    public async Task DeletePortal_CommitsOneSerialisableTransaction()
    {
        Harness harness = Harness.Ready();

        Result outcome = await harness.Service.DeletePortalAsync(PortalId, CancellationToken.None);

        outcome.IsSuccess.Should().BeTrue();

        RecordingTransactionScope scope = harness.OpenedTransactions.Should().ContainSingle().Subject;
        scope.Isolation.Should().Be(TransactionIsolation.Serializable);
        scope.Committed.Should().BeTrue();
        scope.RolledBack.Should().BeFalse();
    }

    /// <summary>
    /// A refusal abandons the transaction rather than committing an empty one, on both refusal paths.
    /// </summary>
    /// <param name="remaining">The number of tenants the installation holds.</param>
    /// <param name="known">Whether the tenant being removed exists.</param>
    /// <returns>A task representing the assertion.</returns>
    [Theory]
    [InlineData(1, true)]
    [InlineData(2, false)]
    public async Task DeletePortal_AbandonsTheTransactionOnEveryRefusal(int remaining, bool known)
    {
        Harness harness = Harness.Ready();
        harness.RemainingPortalCount = remaining;
        if (!known)
        {
            harness.PortalRow = null;
        }

        Result outcome = await harness.Service.DeletePortalAsync(PortalId, CancellationToken.None);

        outcome.IsFailure.Should().BeTrue();

        RecordingTransactionScope scope = harness.OpenedTransactions.Should().ContainSingle().Subject;
        scope.Committed.Should().BeFalse();
        scope.RolledBack.Should().BeTrue();
        harness.AuditRecords.Should().BeEmpty();
    }

    /// <summary>
    /// A committed removal records the legacy PORTAL_DELETED event, carrying the name the row no longer holds.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    /// <remarks>
    /// The stable tenant identifier is the load-bearing property. The independently retained trail does not
    /// copy the deleted tenant's name; the released-alias count records the removal's blast radius without
    /// retaining any host name.
    /// </remarks>
    [Fact]
    public async Task DeletePortal_RecordsTheLegacyPortalDeletedEvent()
    {
        Harness harness = Harness.Ready();
        harness.PortalRow!.PortalAliases.Clear();
        harness.PortalRow!.PortalAliases.Add(Alias(1, PortalId, "first.example"));
        harness.PortalRow!.PortalAliases.Add(Alias(2, PortalId, "second.example"));
        harness.CurrentUser.SetupGet(caller => caller.IsAuthenticated).Returns(true);
        harness.CurrentUser.SetupGet(caller => caller.UserId).Returns(11);
        harness.CurrentUser.SetupGet(caller => caller.UserName).Returns("host");

        await harness.Service.DeletePortalAsync(PortalId, CancellationToken.None);

        AuditEvent record = harness.AuditRecords.Should().ContainSingle().Subject;
        record.EventName.Should().Be(AuditEventNames.PortalDeleted);
        record.Outcome.Should().Be(AuditOutcome.Succeeded);
        record.PortalId.Should().Be(PortalId);
        record.ResourceType.Should().Be("Portal");
        record.ResourceId.Should().Be(PortalId.ToString(CultureInfo.InvariantCulture));
        record.ActorUserId.Should().Be(11);
        record.Properties.Should().NotContainKey("PortalName");
        record.Properties["AliasesReleased"].Should().Be("2");
    }

    /// <summary>
    /// A committed provisioning records the legacy PORTAL_CREATED event with the tenant facts that survived
    /// the migration, and without the credential.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    /// <remarks>
    /// The legacy entry attached fourteen properties (<c>PortalController.vb:L1142-L1155</c>) and the password
    /// was NOT among them, so the legacy code already declined to record the credential. The assertion below
    /// pins that: the submitted password must appear nowhere in the record, in any property, under any name.
    /// The four file-system properties - template path, template file, server path and child path - are absent
    /// because this migration performs no file-system work and recording them would assert something untrue.
    /// </remarks>
    [Fact]
    public async Task CreatePortal_RecordsTheLegacyPortalCreatedEventWithoutTheCredential()
    {
        Harness harness = Harness.Ready();
        harness.CurrentUser.SetupGet(caller => caller.IsAuthenticated).Returns(true);
        harness.CurrentUser.SetupGet(caller => caller.UserId).Returns(11);
        harness.CurrentUser.SetupGet(caller => caller.UserName).Returns("host");

        CreatePortalRequest request = ValidCreateRequest();
        request.Description = "A measured tenant";
        request.KeyWords = "measured, tenant";

        Result<PortalDetailDto> outcome = await harness.Service
            .CreatePortalAsync(request, CancellationToken.None);

        outcome.IsSuccess.Should().BeTrue();

        harness.AuditRecords.Select(candidate => candidate.EventName)
            .Should()
            .BeEquivalentTo(
                [AuditEventNames.PortalCreated, AuditEventNames.HostAlert],
                "an installation records the enumeration's accurate member and the coarser legacy type");

        AuditEvent record = harness.AuditRecords
            .Should()
            .ContainSingle(candidate => candidate.EventName == AuditEventNames.PortalCreated)
            .Subject;
        record.Outcome.Should().Be(AuditOutcome.Succeeded);
        record.ResourceType.Should().Be("Portal");
        record.ActorUserId.Should().Be(11);
        record.SubjectUserId.Should().Be(harness.AddedUsers.Single().UserId);
        record.Properties["IsChildPortal"].Should().Be("False");
        record.Properties["AdministratorId"].Should().Be(
            harness.AddedUsers.Single().UserId.ToString(CultureInfo.InvariantCulture));

        // The two free-text members are recorded as PRESENT rather than quoted, which is the whole of the
        // narrowing: an auditor can still tell that an installation was asked for a description, and the
        // caller-shaped text itself never reaches the general log.
        record.Properties["DescriptionSupplied"].Should().Be("True");
        record.Properties["KeywordsSupplied"].Should().Be("True");

        // MIGRATION: THE TENANT NAME AND ALIAS ARE ABSENT TOO, WHICH IS WHERE TWO REVISIONS DISAGREED. One
        // recorded both for readability; the record's envelope already carries the tenant key, so neither
        // adds identifying power, and both are caller-supplied text that the sink's allowlist would withhold
        // anyway. Asserting their absence here is what keeps this layer and the sink from disagreeing.
        record.Properties.Should().NotContainKeys("PortalName", "PortalAlias");
        record.Properties.Values.Should().NotContain(PortalName);
        record.Properties.Values.Should().NotContain(HostAlias);

        record.Properties.Should().NotContainKey("TemplateFile");
        record.Properties.Should().NotContainKey("TemplatePath");
        record.Properties.Should().NotContainKey("ServerPath");
        record.Properties.Should().NotContainKey("ChildPath");
        record.Properties.Values.Should().NotContain(AdministratorPassword);

        // SEC: THE PERSONAL DATA AND THE CALLER'S FREE TEXT MUST NOT BE HERE, AND THIS IS THE ASSERTION
        // THAT KEEPS THEM OUT. The general application log is not a records-management store: it is the
        // highest-volume and longest-retained log the application writes, its retention is not controlled
        // from this codebase, and a subject-access or erasure request cannot reach it. The administrator's
        // identifier is carried instead, and it resolves to the name and address in the store whenever an
        // operator legitimately needs them. The two free-text members were additionally attacker-shaped:
        // bounded in length by the validators but not in content.
        record.Properties.Should().NotContainKey("AdministratorUsername");
        record.Properties.Should().NotContainKey("AdministratorFirstName");
        record.Properties.Should().NotContainKey("AdministratorLastName");
        record.Properties.Should().NotContainKey("AdministratorEmail");
        record.Properties.Should().NotContainKey("Description");
        record.Properties.Should().NotContainKey("Keywords");
        record.Properties.Values.Should().NotContain(AdministratorUsername);
        record.Properties.Values.Should().NotContain(AdministratorEmail);
        record.Properties.Values.Should().NotContain("A measured tenant");
        record.Properties.Values.Should().NotContain("measured, tenant");
    }

    /// <summary>
    /// A parent portal's host name is stored exactly as submitted.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    /// <remarks>
    /// MIGRATION: the legacy screen stored a non-child alias verbatim, and the parent character set admits
    /// the dot, the colon and the separator (<c>Signup.ascx.vb:L203-L214</c>), so a value carrying a port or a
    /// path is a legitimate parent address and must not be re-composed.
    /// </remarks>
    [Fact]
    public async Task CreatePortal_StoresAParentHostNameVerbatim()
    {
        Harness harness = Harness.Ready();
        CreatePortalRequest request = ValidCreateRequest();
        request.IsChildPortal = false;
        request.PortalAlias = "parent.example:8080";

        Result<PortalDetailDto> outcome = await harness.Service
            .CreatePortalAsync(request, CancellationToken.None);

        outcome.IsSuccess.Should().BeTrue();
        harness.AddedAliases.Should().ContainSingle()
            .Which.HttpAlias.Should().Be("parent.example:8080");
    }

    /// <summary>
    /// A child portal's bare segment is composed beneath the authority the request resolved to.
    /// </summary>
    /// <param name="parentAlias">The host name the request resolved to.</param>
    /// <param name="segment">The bare segment the operator submitted.</param>
    /// <param name="expected">The address that must be stored.</param>
    /// <returns>A task representing the assertion.</returns>
    /// <remarks>
    /// MIGRATION: this is <c>Signup.ascx.vb:L232-L233</c> - <c>GetDomainName(Request) &amp; "/" &amp;
    /// ChildPath</c>. Until this fix the flag was accepted and never read, so a caller could ask for a child
    /// portal, be told it had one, and find it unreachable: the bare segment was stored as if it were a host
    /// name and no request could ever match it. The third case pins the nesting behaviour - a parent that is
    /// itself addressed beneath a path yields a deeper address, which is what the legacy member did when it
    /// returned <c>www.domain.com/directory</c> rather than the bare host.
    /// </remarks>
    [Theory]
    [InlineData("parent.example", "child", "parent.example/child")]
    [InlineData("parent.example:8080", "child", "parent.example:8080/child")]
    [InlineData("parent.example/first", "second", "parent.example/first/second")]
    [InlineData("parent.example/", "child", "parent.example/child")]
    public async Task CreatePortal_ComposesAChildAddressBeneathTheResolvedAuthority(
        string parentAlias,
        string segment,
        string expected)
    {
        Harness harness = Harness.Ready();
        harness.ResolvedContext = ResolvedTenant(parentAlias);
        CreatePortalRequest request = ValidCreateRequest();
        request.IsChildPortal = true;
        request.PortalAlias = segment;

        Result<PortalDetailDto> outcome = await harness.Service
            .CreatePortalAsync(request, CancellationToken.None);

        outcome.IsSuccess.Should().BeTrue();
        harness.AddedAliases.Should().ContainSingle().Which.HttpAlias.Should().Be(expected);
    }

    /// <summary>
    /// The composed child address, not the submitted segment, is what the uniqueness check probes; the
    /// audit record retains only that the result is a child portal.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    /// <remarks>
    /// Checking the segment would be the same defect wearing a different hat: two different parents may each
    /// legitimately own a child called "sales", so a check against the bare segment would refuse the second as
    /// a duplicate, while a check that ran before composition would miss a genuine collision between two
    /// children of the same parent.
    /// </remarks>
    [Fact]
    public async Task CreatePortal_ChecksAndRecordsTheComposedChildAddress()
    {
        Harness harness = Harness.Ready();
        harness.ResolvedContext = ResolvedTenant("parent.example");
        CreatePortalRequest request = ValidCreateRequest();
        request.IsChildPortal = true;
        request.PortalAlias = "sales";

        await harness.Service.CreatePortalAsync(request, CancellationToken.None);

        harness.Aliases.Verify(
            aliases => aliases.AliasExistsAsync("parent.example/sales", null, It.IsAny<CancellationToken>()),
            Times.Once);
        harness.Aliases.Verify(
            aliases => aliases.AliasExistsAsync("sales", null, It.IsAny<CancellationToken>()),
            Times.Never);

        // Read from the accurate member of the pair an installation records; its HOST_ALERT twin carries the
        // identical facts, so asserting on one is asserting on both.
        AuditEvent record = harness.AuditRecords
            .Should()
            .ContainSingle(candidate => candidate.EventName == AuditEventNames.PortalCreated)
            .Subject;
        record.Properties.Should().NotContainKey(
            "PortalAlias",
            "host names are caller-authored identifiers with a separate retention lifecycle");
        record.Properties["IsChildPortal"].Should().Be("True");
    }

    /// <summary>
    /// A child address the operator has already qualified is stored verbatim rather than composed twice.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    /// <remarks>
    /// MIGRATION: the legacy HOST branch (<c>Signup.ascx.vb:L199-L216</c> and L235) permitted the typed value
    /// to carry path separators of its own and stored it exactly as typed, validating only its final segment.
    /// Composing beneath the resolved authority as well would produce "parent/other.example/child", an
    /// address nobody asked for and nothing serves.
    /// </remarks>
    [Fact]
    public async Task CreatePortal_StoresAQualifiedChildAddressVerbatim()
    {
        Harness harness = Harness.Ready();
        harness.ResolvedContext = ResolvedTenant("parent.example");
        CreatePortalRequest request = ValidCreateRequest();
        request.IsChildPortal = true;
        request.PortalAlias = "other.example/child";

        Result<PortalDetailDto> outcome = await harness.Service
            .CreatePortalAsync(request, CancellationToken.None);

        outcome.IsSuccess.Should().BeTrue();
        harness.AddedAliases.Should().ContainSingle().Which.HttpAlias.Should().Be("other.example/child");
    }

    /// <summary>
    /// A child portal asked for from a request that resolved to no tenant is refused, and nothing is written.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    /// <remarks>
    /// Refused rather than guessed at. The legacy member could always answer because it read the incoming URL
    /// directly; here the authority has to be an alias that EXISTS, and inventing one would create a tenant
    /// reachable at an address the installation does not serve. Reported as a failure code so the API edge
    /// renders a bad request rather than a server fault, and asserted to leave no transaction open at all,
    /// because the refusal precedes every write.
    /// </remarks>
    [Fact]
    public async Task CreatePortal_RefusesAChildWhenNoParentAuthorityResolved()
    {
        Harness harness = Harness.Ready();
        harness.ContextResolved = false;
        CreatePortalRequest request = ValidCreateRequest();
        request.IsChildPortal = true;
        request.PortalAlias = "child";

        Result<PortalDetailDto> outcome = await harness.Service
            .CreatePortalAsync(request, CancellationToken.None);

        outcome.IsFailure.Should().BeTrue();
        outcome.Reason!.Code.Should().Be("portal.parent_alias_unresolved");
        harness.AddedPortals.Should().BeEmpty();
        harness.AddedAliases.Should().BeEmpty();
        harness.OpenedTransactions.Should().BeEmpty();
        harness.AuditRecords.Should().BeEmpty();
    }

    /// <summary>
    /// A parent portal is created without consulting the resolved tenant at all, so provisioning the first
    /// portal of an installation cannot depend on one already existing.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task CreatePortal_CreatesAParentWithNoResolvedTenant()
    {
        Harness harness = Harness.Ready();
        harness.ContextResolved = false;
        CreatePortalRequest request = ValidCreateRequest();
        request.IsChildPortal = false;

        Result<PortalDetailDto> outcome = await harness.Service
            .CreatePortalAsync(request, CancellationToken.None);

        outcome.IsSuccess.Should().BeTrue();
        harness.AddedAliases.Should().ContainSingle().Which.HttpAlias.Should().Be(HostAlias);
    }

    /// <summary>
    /// Reading a tenant's settings reports absence for an unknown tenant and the projection otherwise.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task GetPortalSettings_ReportsAbsenceOrTheProjection()
    {
        Harness harness = Harness.Ready();

        Result<PortalSettingsDto?> present = await harness.Service
            .GetPortalSettingsAsync(PortalId, CancellationToken.None);

        present.Value!.PortalId.Should().Be(PortalId);
        present.Value!.PortalName.Should().Be(PortalName);
        present.Value!.Guid.Should().Be(harness.PortalRow!.PortalGuid);

        harness.PortalRow = null;

        Result<PortalSettingsDto?> absent = await harness.Service
            .GetPortalSettingsAsync(PortalId, CancellationToken.None);

        absent.IsSuccess.Should().BeTrue();
        absent.Value.Should().BeNull();
    }

    /// <summary>
    /// The settings read does not load the tenant's host names, because the projection does not carry them.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task GetPortalSettings_DoesNotLoadTheHostNames()
    {
        Harness harness = Harness.Ready();

        await harness.Service.GetPortalSettingsAsync(PortalId, CancellationToken.None);

        harness.Portals.Verify(
            p => p.GetByIdAsync(PortalId, false, It.IsAny<CancellationToken>()),
            Times.Once);
        harness.Portals.Verify(
            p => p.GetByIdAsync(PortalId, true, It.IsAny<CancellationToken>()),
            Times.Never);
    }

    /// <summary>The settings write requires a body before it attempts any persistence work.</summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task UpdatePortalSettings_RequiresARequest()
    {
        Harness harness = Harness.Ready();

        await Assert.ThrowsAsync<ArgumentNullException>(
            () => harness.Service.UpdatePortalSettingsAsync(PortalId, null!, CancellationToken.None));
    }

    /// <summary>An unknown tenant is reported as absent and no write is staged.</summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task UpdatePortalSettings_ReportsAbsenceWithoutWriting()
    {
        Harness harness = Harness.Ready();
        harness.PortalRow = null;

        Result<PortalSettingsDto?> outcome = await harness.Service.UpdatePortalSettingsAsync(
            PortalId,
            ValidSettingsUpdateRequest(),
            CancellationToken.None);

        outcome.IsSuccess.Should().BeTrue();
        outcome.Value.Should().BeNull();
        harness.UnitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
        harness.InvalidatedPortalIds.Should().BeEmpty();
    }

    /// <summary>
    /// A successful settings write commits once, invalidates the portal cache and returns the values that
    /// were stored without issuing a second read.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task UpdatePortalSettings_CommitsInvalidatesAndReturnsTheProjection()
    {
        Harness harness = Harness.Ready();
        UpdatePortalSettingsRequest request = ValidSettingsUpdateRequest();
        request.Description = "Changed through the settings resource.";

        Result<PortalSettingsDto?> outcome = await harness.Service.UpdatePortalSettingsAsync(
            PortalId,
            request,
            CancellationToken.None);

        outcome.Value.Should().NotBeNull();
        outcome.Value!.PortalId.Should().Be(PortalId);
        outcome.Value.PortalName.Should().Be("Settings Renamed");
        outcome.Value.Description.Should().Be("Changed through the settings resource.");
        harness.UnitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
        harness.InvalidatedPortalIds.Should().Equal(new[] { PortalId });
        harness.Portals.Verify(
            p => p.GetByIdAsync(PortalId, false, It.IsAny<CancellationToken>()),
            Times.Once);
        harness.Portals.Verify(
            p => p.GetByIdAsync(PortalId, true, It.IsAny<CancellationToken>()),
            Times.Never);
    }

    /// <summary>
    /// The settings route cannot bypass the host-only comparison shared by the general update path.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task UpdatePortalSettings_RefusesATenantAdministratorChangingAHostOnlyTerm()
    {
        Harness harness = Harness.Ready();
        harness.SuperUser = false;
        UpdatePortalSettingsRequest request = ValidSettingsUpdateRequest();
        request.HostFee = 1m;

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => harness.Service.UpdatePortalSettingsAsync(PortalId, request, CancellationToken.None));

        harness.PortalRow!.PortalName.Should().Be(PortalName);
        harness.UnitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    /// <summary>
    /// Listing the host names of an unknown tenant is refused, so a caller cannot mistake an empty answer
    /// for a tenant that exists and has none.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task ListPortalAliases_RefusesAnUnknownTenant()
    {
        Harness harness = Harness.Ready();
        harness.PortalExists = false;

        Result<IReadOnlyList<PortalAliasDto>> outcome = await harness.Service
            .ListPortalAliasesAsync(PortalId, CancellationToken.None);

        outcome.IsFailure.Should().BeTrue();
        outcome.Reason!.Code.Should().Be(NotFoundCode);
        outcome.Reason!.Message.Should().Be($"No portal bears identifier {PortalId}.");
    }

    /// <summary>
    /// A scoped list carries only that tenant's host names; an unscoped list carries the installation's and
    /// probes no tenant.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task ListPortalAliases_ScopesToTheTenantOrTheWholeInstallation()
    {
        Harness harness = Harness.Ready();
        harness.AllAliases.Clear();
        harness.AllAliases.AddRange(
        [
            Alias(1, PortalId, "mine.example"),
            Alias(2, SecondPortalId, "theirs.example"),
        ]);

        Result<IReadOnlyList<PortalAliasDto>> scoped = await harness.Service
            .ListPortalAliasesAsync(PortalId, CancellationToken.None);

        scoped.Value.Select(alias => alias.HttpAlias).Should().Equal(new[] { "mine.example" });

        Result<IReadOnlyList<PortalAliasDto>> unscoped = await harness.Service
            .ListPortalAliasesAsync(null, CancellationToken.None);

        unscoped.Value.Should().HaveCount(2);
        harness.Portals.Verify(
            p => p.ExistsAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    /// <summary>
    /// Every projected alias reports whether it is the one THIS REQUEST resolved the tenant through, so a
    /// screen can withhold the affordance on that row before it is attempted.
    /// </summary>
    /// <remarks>
    /// MIGRATION: restores the legacy affordance at <c>Website/admin/Portal/PortalAlias.ascx.vb</c> L51 to
    /// L60, where <c>IsNotCurrent</c> compared each grid row's key against
    /// <c>Me.PortalAlias.PortalAliasID()</c> and <c>portalalias.ascx</c> L8 bound the answer to the edit
    /// hyperlink's <c>Visible</c> property. The comparison is by KEY, never by host name: the legacy write
    /// path lower-cased on insert and update while its reader did not, so two spellings of one alias are
    /// both legitimate stored values and a string comparison would need a casing rule of its own.
    /// </remarks>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task ListPortalAliases_MarksOnlyTheAliasThisRequestResolvedThrough()
    {
        Harness harness = Harness.Ready();
        harness.AllAliases.Clear();
        harness.AllAliases.AddRange(
        [
            Alias(PortalAliasId, PortalId, HostAlias),
            Alias(SparePortalAliasId, PortalId, SpareHostAlias),
        ]);

        harness.ContextResolved = true;
        harness.ResolvedContext = ResolvedTenant(HostAlias, PortalAliasId);

        Result<IReadOnlyList<PortalAliasDto>> rows = await harness.Service
            .ListPortalAliasesAsync(PortalId, CancellationToken.None);

        rows.IsSuccess.Should().BeTrue();
        rows.Value.Should().HaveCount(2);
        rows.Value.Single(alias => alias.PortalAliasId == PortalAliasId).IsCurrent
            .Should().BeTrue("this is the row the request resolved through");
        rows.Value.Single(alias => alias.PortalAliasId == SparePortalAliasId).IsCurrent
            .Should().BeFalse("every other row remains safe to rename and to unbind");
    }

    /// <summary>
    /// A request that resolved no tenant reports every row as not current, which is a decided answer rather
    /// than a fallback: it arrived through no alias, so no row is the one it is using.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task ListPortalAliases_ReportsNoCurrentAliasWhenTheRequestResolvedNoTenant()
    {
        Harness harness = Harness.Ready();
        harness.AllAliases.Clear();
        harness.AllAliases.Add(Alias(PortalAliasId, PortalId, HostAlias));
        harness.ContextResolved = false;

        Result<IReadOnlyList<PortalAliasDto>> rows = await harness.Service
            .ListPortalAliasesAsync(PortalId, CancellationToken.None);

        rows.Value.Single().IsCurrent.Should().BeFalse();
    }

    /// <summary>
    /// Renaming the alias the current request resolved through is REFUSED, with a stable conflict code.
    /// </summary>
    /// <remarks>
    /// The consequence is unrecoverable rather than merely unwise: the host name the session is arriving
    /// through would stop resolving to the tenant, for every caller using it, and the screen that would
    /// undo the change becomes unreachable. The refusal is checked BEFORE the duplicate check because it
    /// does not depend on the submitted value - a rename to the value the row already holds is still a
    /// write against the row resolution is using.
    /// </remarks>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task UpdatePortalAlias_RefusesTheAliasThisRequestResolvedThrough()
    {
        Harness harness = Harness.Ready();
        harness.ContextResolved = true;
        harness.ResolvedContext = ResolvedTenant(HostAlias, PortalAliasId);

        Result outcome = await harness.Service.UpdatePortalAliasAsync(
            PortalId,
            PortalAliasId,
            new UpdatePortalAliasRequest { HttpAlias = "renamed.example" },
            CancellationToken.None);

        outcome.IsSuccess.Should().BeFalse();
        outcome.Error!.Code.Should().Be("portal.alias_in_use.conflict");
        harness.UpdatedAliases.Should().BeEmpty("the row must not be written at all");
        harness.UnitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    /// <summary>
    /// The refusal is by KEY and is unaffected by the value submitted, including the row's own host name.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task UpdatePortalAlias_RefusesTheActiveAliasEvenWhenTheValueIsUnchanged()
    {
        Harness harness = Harness.Ready();
        harness.ContextResolved = true;
        harness.ResolvedContext = ResolvedTenant(HostAlias, PortalAliasId);

        Result outcome = await harness.Service.UpdatePortalAliasAsync(
            PortalId,
            PortalAliasId,
            new UpdatePortalAliasRequest { HttpAlias = HostAlias },
            CancellationToken.None);

        outcome.Error!.Code.Should().Be("portal.alias_in_use.conflict");
    }

    /// <summary>
    /// A row the request did NOT arrive through remains editable, so the rule withholds exactly one row.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task UpdatePortalAlias_PermitsARowThisRequestDidNotResolveThrough()
    {
        Harness harness = Harness.Ready();

        // The request arrived through a DIFFERENT alias row of the same portal.
        ArrivedThroughAnotherAlias(harness);

        Result outcome = await harness.Service.UpdatePortalAliasAsync(
            PortalId,
            PortalAliasId,
            new UpdatePortalAliasRequest { HttpAlias = "renamed.example" },
            CancellationToken.None);

        outcome.IsSuccess.Should().BeTrue();
        harness.UpdatedAliases.Should().ContainSingle().Which.HttpAlias.Should().Be("renamed.example");
    }

    /// <summary>
    /// A request that resolved no tenant may rename any row, because it is using none of them.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task UpdatePortalAlias_PermitsEveryRowWhenTheRequestResolvedNoTenant()
    {
        Harness harness = Harness.Ready();
        harness.ContextResolved = false;

        Result outcome = await harness.Service.UpdatePortalAliasAsync(
            PortalId,
            PortalAliasId,
            new UpdatePortalAliasRequest { HttpAlias = "renamed.example" },
            CancellationToken.None);

        outcome.IsSuccess.Should().BeTrue();
    }

    /// <summary>
    /// Unbinding the alias the current request resolved through is refused with the same code.
    /// </summary>
    /// <remarks>
    /// MIGRATION: this half goes BEYOND the legacy screen rather than reproducing it. Legacy hid the edit
    /// affordance for the current row and governed removal only by a count
    /// (<c>EditPortalAlias.ascx.vb</c> L107), so an operator on a portal with several aliases could unbind
    /// the very one they had arrived through - and the consequence is strictly worse than a rename, because
    /// no row is left to correct. The divergence is deliberate and recorded in <c>MIGRATION_NOTES.md</c>.
    /// </remarks>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task DeletePortalAlias_RefusesTheAliasThisRequestResolvedThrough()
    {
        Harness harness = Harness.Ready();
        harness.ContextResolved = true;
        harness.ResolvedContext = ResolvedTenant(HostAlias, PortalAliasId);

        Result outcome = await harness.Service
            .DeletePortalAliasAsync(PortalId, PortalAliasId, CancellationToken.None);

        outcome.IsSuccess.Should().BeFalse();
        outcome.Error!.Code.Should().Be("portal.alias_in_use.conflict");
        harness.RemovedAliases.Should().BeEmpty("the row must not be removed at all");
        harness.UnitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    /// <summary>
    /// A row the request did NOT arrive through remains removable.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task DeletePortalAlias_PermitsARowThisRequestDidNotResolveThrough()
    {
        Harness harness = Harness.Ready();
        ArrivedThroughAnotherAlias(harness);

        Result outcome = await harness.Service
            .DeletePortalAliasAsync(PortalId, PortalAliasId, CancellationToken.None);

        outcome.IsSuccess.Should().BeTrue();
        harness.RemovedAliases.Should().ContainSingle().Which.Should().BeSameAs(harness.LookupAlias);
    }

    /// <summary>
    /// A single host name is reported as absent when it does not exist, and projected otherwise.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task GetPortalAlias_ReportsAbsenceOrTheProjection()
    {
        Harness harness = Harness.Ready();

        Result<PortalAliasDto?> present = await harness.Service
            .GetPortalAliasAsync(PortalId, PortalAliasId, CancellationToken.None);

        present.Value!.PortalAliasId.Should().Be(PortalAliasId);
        present.Value!.PortalId.Should().Be(PortalId);
        present.Value!.HttpAlias.Should().Be(HostAlias);

        harness.LookupAlias = null;

        Result<PortalAliasDto?> absent = await harness.Service
            .GetPortalAliasAsync(PortalId, PortalAliasId, CancellationToken.None);

        absent.IsSuccess.Should().BeTrue();
        absent.Value.Should().BeNull();
    }

    /// <summary>
    /// Adding a host name requires one to be submitted.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task AddPortalAlias_RequiresASubmission()
    {
        Harness harness = Harness.Ready();

        await Assert.ThrowsAsync<ArgumentNullException>(
            () => harness.Service.AddPortalAliasAsync(PortalId, null!, CancellationToken.None));
    }

    /// <summary>
    /// A blank host name is refused before the tenant is probed, because an alias with no host name reaches
    /// nothing.
    /// </summary>
    /// <param name="submitted">
    /// The blank host name to submit. The null case is deliberately forced past the non-nullable
    /// annotation, because a JSON body carrying <c>"httpAlias": null</c> deserialises to exactly
    /// that regardless of the annotation, and the guard exists for precisely that caller.
    /// </param>
    /// <returns>A task representing the assertion.</returns>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task AddPortalAlias_RefusesABlankHostName(string? submitted)
    {
        Harness harness = Harness.Ready();

        DomainException failure = await Assert.ThrowsAsync<DomainException>(
            () => harness.Service.AddPortalAliasAsync(
                PortalId,
                new CreatePortalAliasRequest { HttpAlias = submitted! },
                CancellationToken.None));

        failure.Message.Should().Be("A portal alias must carry a host name.");
        harness.Portals.Verify(
            p => p.ExistsAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    /// <summary>
    /// A host name cannot be bound to a tenant that does not exist.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task AddPortalAlias_RefusesAnUnknownTenant()
    {
        Harness harness = Harness.Ready();
        harness.PortalExists = false;

        Result<PortalAliasDto> outcome = await harness.Service.AddPortalAliasAsync(
            PortalId,
            new CreatePortalAliasRequest { HttpAlias = "new.example" },
            CancellationToken.None);

        outcome.IsFailure.Should().BeTrue();
        outcome.Reason!.Code.Should().Be(NotFoundCode);
        harness.AddedAliases.Should().BeEmpty();
    }

    /// <summary>
    /// A host name already bound anywhere is refused, because one host name can only reach one tenant.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task AddPortalAlias_RefusesAHostNameAlreadyBound()
    {
        Harness harness = Harness.Ready();
        harness.AliasTaken = true;

        Result<PortalAliasDto> outcome = await harness.Service.AddPortalAliasAsync(
            PortalId,
            new CreatePortalAliasRequest { HttpAlias = "new.example" },
            CancellationToken.None);

        outcome.IsFailure.Should().BeTrue();
        outcome.Reason!.Code.Should().Be(AliasDuplicateCode);
        outcome.Reason!.Message.Should()
            .Be("The host name 'new.example' is already bound to a portal.");
    }

    /// <summary>
    /// A host name bound between the check and the commit is refused with exactly the answer the check gives,
    /// and neither cache is discarded because nothing was written.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    /// <remarks>
    /// MIGRATION: SEC-F6. Measured on a live installation before the fix: eight simultaneous identical
    /// bindings produced one 201, four 409 and THREE 500s, with exactly one row stored - the three 500s being
    /// the racers, told the server had failed when the unique index had done exactly its job. The cache
    /// assertions matter as much as the code: alias resolution is installation-wide, so discarding the host
    /// entries on a binding that never happened would evict every tenant's resolution for nothing.
    /// </remarks>
    [Fact]
    public async Task AddPortalAlias_RefusesAHostNameBoundBetweenTheCheckAndTheCommit()
    {
        Harness harness = Harness.Ready();
        harness.UnitOfWork
            .Setup(unit => unit.SaveChangesAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(DuplicateKeyException.ForConstraint("IX_PortalAlias", null));

        Result<PortalAliasDto> outcome = await harness.Service.AddPortalAliasAsync(
            PortalId,
            new CreatePortalAliasRequest { HttpAlias = "new.example" },
            CancellationToken.None);

        outcome.IsFailure.Should().BeTrue();
        outcome.Reason!.Code.Should().Be(
            AliasDuplicateCode,
            "the racer faces the state the check describes, so it is told the same thing in the same words");
        outcome.Reason!.Message.Should()
            .Be("The host name 'new.example' is already bound to a portal.");

        harness.Cache.Verify(cache => cache.InvalidateHost(), Times.Never);
        harness.Cache.Verify(cache => cache.InvalidatePortal(It.IsAny<int>()), Times.Never);
    }

    /// <summary>
    /// The submitted host name is trimmed, the tenant comes from the route - the request declares no tenant
    /// member to override it with - and both caches are discarded because a new host name changes tenant
    /// resolution.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task AddPortalAlias_TrimsTheNameTakesTheTenantFromTheRouteAndDiscardsBothCaches()
    {
        Harness harness = Harness.Ready();

        Result<PortalAliasDto> outcome = await harness.Service.AddPortalAliasAsync(
            PortalId,
            new CreatePortalAliasRequest { HttpAlias = "  new.example  " },
            CancellationToken.None);

        outcome.IsSuccess.Should().BeTrue();
        PortalAlias created = harness.AddedAliases.Should().ContainSingle().Which;
        created.HttpAlias.Should().Be("new.example");
        created.PortalId.Should().Be(PortalId);
        outcome.Value.PortalId.Should().Be(PortalId);
        outcome.Value.HttpAlias.Should().Be("new.example");

        harness.UnitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
        harness.HostInvalidations.Should().Be(1);
        harness.InvalidatedPortalIds.Should().Equal(new[] { PortalId });
    }

    /// <summary>
    /// Changing a host name requires one to be submitted, and a blank one is refused before the stored row
    /// is read.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task UpdatePortalAlias_RequiresASubmissionAndRefusesABlankHostName()
    {
        Harness harness = Harness.Ready();

        await Assert.ThrowsAsync<ArgumentNullException>(
            () => harness.Service.UpdatePortalAliasAsync(PortalId, PortalAliasId, null!, CancellationToken.None));

        DomainException failure = await Assert.ThrowsAsync<DomainException>(
            () => harness.Service.UpdatePortalAliasAsync(
                PortalId,
                PortalAliasId,
                new UpdatePortalAliasRequest { HttpAlias = "  " },
                CancellationToken.None));

        failure.Message.Should().Be("A portal alias must carry a host name.");
        harness.Aliases.Verify(
            a => a.GetByIdAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    /// <summary>
    /// A host name that does not exist is refused with its own reason.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task UpdatePortalAlias_RefusesAnUnknownHostName()
    {
        Harness harness = Harness.Ready();
        harness.LookupAlias = null;

        Result outcome = await harness.Service.UpdatePortalAliasAsync(
            PortalId,
            PortalAliasId,
            new UpdatePortalAliasRequest { HttpAlias = "new.example" },
            CancellationToken.None);

        outcome.IsFailure.Should().BeTrue();
        outcome.Reason!.Code.Should().Be(AliasNotFoundCode);
        outcome.Reason!.Message.Should()
            .Be($"No portal alias bears identifier {PortalAliasId}.");
    }

    /// <summary>
    /// The duplicate check excludes the row being changed, so re-submitting a host name unchanged is not a
    /// clash with itself; a name held by another row is refused with wording that says so.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task UpdatePortalAlias_ExcludesTheRowItselfFromTheDuplicateCheck()
    {
        Harness harness = Harness.Ready();
        ArrivedThroughAnotherAlias(harness);

        Result permitted = await harness.Service.UpdatePortalAliasAsync(
            PortalId,
            PortalAliasId,
            new UpdatePortalAliasRequest { HttpAlias = HostAlias },
            CancellationToken.None);

        permitted.IsSuccess.Should().BeTrue();
        harness.Aliases.Verify(
            a => a.AliasExistsAsync(HostAlias, PortalAliasId, It.IsAny<CancellationToken>()),
            Times.Once);

        harness.AliasTaken = true;

        Result refused = await harness.Service.UpdatePortalAliasAsync(
            PortalId,
            PortalAliasId,
            new UpdatePortalAliasRequest { HttpAlias = "taken.example" },
            CancellationToken.None);

        refused.IsFailure.Should().BeTrue();
        refused.Reason!.Code.Should().Be(AliasDuplicateCode);
        refused.Reason!.Message.Should()
            .Be("The host name 'taken.example' is already bound to another portal alias.");
    }

    /// <summary>
    /// A rename onto a host name bound between the check and the commit is refused with exactly the answer
    /// the check gives.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    /// <remarks>
    /// MIGRATION: SEC-F6, the rename counterpart of the binding race. A rename is refused by the same unique
    /// index for the same reason, and the check that excludes the row being renamed from its own comparison
    /// cannot see a name another request is binding concurrently.
    /// </remarks>
    [Fact]
    public async Task UpdatePortalAlias_RefusesAHostNameBoundBetweenTheCheckAndTheCommit()
    {
        Harness harness = Harness.Ready();
        ArrivedThroughAnotherAlias(harness);
        harness.UnitOfWork
            .Setup(unit => unit.SaveChangesAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(DuplicateKeyException.ForConstraint("IX_PortalAlias", null));

        Result outcome = await harness.Service.UpdatePortalAliasAsync(
            PortalId,
            PortalAliasId,
            new UpdatePortalAliasRequest { HttpAlias = "taken.example" },
            CancellationToken.None);

        outcome.IsFailure.Should().BeTrue();
        outcome.Reason!.Code.Should().Be(AliasDuplicateCode);
        outcome.Reason!.Message.Should()
            .Be("The host name 'taken.example' is already bound to another portal alias.");

        harness.Cache.Verify(cache => cache.InvalidateHost(), Times.Never);
    }

    /// <summary>
    /// Changing a host name writes the trimmed name, leaves the owning tenant alone - the request declares no
    /// tenant member, so a move between tenants is not expressible - and discards the cache of the tenant that
    /// actually owns the row.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task UpdatePortalAlias_DoesNotMoveTheHostNameBetweenTenants()
    {
        Harness harness = Harness.Ready();
        ArrivedThroughAnotherAlias(harness);

        Result outcome = await harness.Service.UpdatePortalAliasAsync(
            PortalId,
            PortalAliasId,
            new UpdatePortalAliasRequest { HttpAlias = "  renamed.example  " },
            CancellationToken.None);

        outcome.IsSuccess.Should().BeTrue();
        harness.LookupAlias!.HttpAlias.Should().Be("renamed.example");
        harness.LookupAlias!.PortalId.Should().Be(PortalId);
        harness.InvalidatedPortalIds.Should().Equal(new[] { PortalId });
        harness.HostInvalidations.Should().Be(1);
    }

    /// <summary>
    /// Removing a host name that does not exist is refused.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task DeletePortalAlias_RefusesAnUnknownHostName()
    {
        Harness harness = Harness.Ready();
        harness.LookupAlias = null;

        Result outcome = await harness.Service
            .DeletePortalAliasAsync(PortalId, PortalAliasId, CancellationToken.None);

        outcome.IsFailure.Should().BeTrue();
        outcome.Reason!.Code.Should().Be(AliasNotFoundCode);
        harness.RemovedAliases.Should().BeEmpty();
    }

    /// <summary>
    /// Removing a host name releases it and discards the cache of the tenant it belonged to, read before the
    /// row is withdrawn.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task DeletePortalAlias_ReleasesTheHostNameAndDiscardsItsTenantsCache()
    {
        Harness harness = Harness.Ready();
        ArrivedThroughAnotherAlias(harness);

        Result outcome = await harness.Service
            .DeletePortalAliasAsync(PortalId, PortalAliasId, CancellationToken.None);

        outcome.IsSuccess.Should().BeTrue();
        harness.RemovedAliases.Should().ContainSingle().Which.Should().BeSameAs(harness.LookupAlias);
        harness.UnitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
        harness.HostInvalidations.Should().Be(1);
        harness.InvalidatedPortalIds.Should().Equal(new[] { PortalId });
    }

    /// <summary>
    /// An alias owned by another tenant is invisible to a read addressed at this one, and the answer is
    /// indistinguishable from an alias that does not exist so that the member is not an enumeration oracle.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task GetPortalAlias_RefusesAnAliasOwnedByAnotherTenant()
    {
        Harness harness = Harness.Ready();
        harness.LookupAlias = Alias(PortalAliasId, ForeignPortalId, "other.example");

        Result<PortalAliasDto?> outcome = await harness.Service
            .GetPortalAliasAsync(PortalId, PortalAliasId, CancellationToken.None);

        outcome.IsSuccess.Should().BeTrue();
        outcome.Value.Should().BeNull();
    }

    /// <summary>
    /// Renaming an alias owned by another tenant is refused, because the alias is what tenant resolution
    /// matches on and renaming it would re-point that tenant's traffic. Nothing is written and nothing is
    /// committed.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task UpdatePortalAlias_RefusesAnAliasOwnedByAnotherTenant()
    {
        Harness harness = Harness.Ready();
        harness.LookupAlias = Alias(PortalAliasId, ForeignPortalId, "other.example");

        Result outcome = await harness.Service.UpdatePortalAliasAsync(
            PortalId,
            PortalAliasId,
            new UpdatePortalAliasRequest { HttpAlias = "hijacked.example" },
            CancellationToken.None);

        outcome.IsFailure.Should().BeTrue();
        outcome.Reason!.Code.Should().Be(AliasNotFoundCode);
        harness.LookupAlias!.HttpAlias.Should().Be("other.example");
        harness.UnitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    /// <summary>
    /// Unbinding an alias owned by another tenant is refused, because it would make that tenant unreachable
    /// at the host name its users hold. Nothing is removed and nothing is committed.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task DeletePortalAlias_RefusesAnAliasOwnedByAnotherTenant()
    {
        Harness harness = Harness.Ready();
        harness.LookupAlias = Alias(PortalAliasId, ForeignPortalId, "other.example");

        Result outcome = await harness.Service
            .DeletePortalAliasAsync(PortalId, PortalAliasId, CancellationToken.None);

        outcome.IsFailure.Should().BeTrue();
        outcome.Reason!.Code.Should().Be(AliasNotFoundCode);
        harness.RemovedAliases.Should().BeEmpty();
        harness.UnitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    /// <summary>
    /// Builds the tenant fixture the store returns, bearing the measured identifier seed of minus one.
    /// </summary>
    /// <returns>A tenant row.</returns>
    private static Portal StoredPortal()
    {
        var portal = new Portal
        {
            PortalId = PortalId,
            PortalName = PortalName,
            DefaultLanguage = DefaultLanguageCode,
            TimeZoneOffset = DefaultTimeZoneOffsetMinutes,
            HomeDirectory = "Portals/0",
            PortalGuid = new Guid("11111111-2222-3333-4444-555555555555"),
            AdministratorId = AdministratorId,
            AdministratorRoleId = AdministratorRoleId,
            RegisteredRoleId = RegisteredRoleId,
            AdminTabId = 90,
        };

        portal.PortalAliases.Add(Alias(PortalAliasId, PortalId, HostAlias));
        return portal;
    }

    /// <summary>
    /// Builds a second tenant, which is what makes the installation's tenant count greater than one.
    /// </summary>
    /// <returns>A second tenant row.</returns>
    private static Portal SecondPortal() => new()
    {
        PortalId = SecondPortalId,
        PortalName = "Second Portal",
        DefaultLanguage = DefaultLanguageCode,
        TimeZoneOffset = DefaultTimeZoneOffsetMinutes,
        HomeDirectory = "Portals/1",
        PortalGuid = new Guid("66666666-7777-8888-9999-000000000000"),
    };

    /// <summary>
    /// Builds an account with exactly the supplied portal memberships.
    /// </summary>
    /// <param name="userId">Installation-wide account identifier.</param>
    /// <param name="isSuperUser">Whether the account is a host operator.</param>
    /// <param name="portalIds">Portal identifiers whose membership rows the account holds.</param>
    /// <returns>The account and its complete membership collection.</returns>
    private static User PortalMember(int userId, bool isSuperUser, params int[] portalIds)
    {
        var account = new User
        {
            UserId = userId,
            Username = FormattableString.Invariant($"member-{userId}"),
            DisplayName = FormattableString.Invariant($"Member {userId}"),
            IsSuperUser = isSuperUser,
        };

        foreach (int portalId in portalIds)
        {
            account.UserPortals.Add(new UserPortal
            {
                UserId = userId,
                PortalId = portalId,
                CreatedDate = Now,
                IsAuthorised = true,
            });
        }

        return account;
    }

    /// <summary>
    /// Builds a host-name row.
    /// </summary>
    /// <param name="portalAliasId">The row identifier.</param>
    /// <param name="portalId">The owning tenant.</param>
    /// <param name="httpAlias">The host name.</param>
    /// <returns>A host-name row.</returns>
    private static PortalAlias Alias(int portalAliasId, int portalId, string httpAlias) => new()
    {
        PortalAliasId = portalAliasId,
        PortalId = portalId,
        HttpAlias = httpAlias,
    };

    /// <summary>
    /// Builds the tenant a request has resolved to, which is the authority a child portal's alias is
    /// composed beneath.
    /// </summary>
    /// <param name="httpAlias">The resolved tenant's own host name.</param>
    /// <param name="portalAliasId">
    /// Surrogate key of the alias row the request resolved through. This is the fact the active-alias
    /// refusal and the <c>IsCurrent</c> projection both read, so a test naming a different key is
    /// describing a request that arrived through a different host name.
    /// </param>
    /// <returns>A resolved tenant context.</returns>
    /// <remarks>
    /// The alias is the only member the composition reads, but the whole contract is answered so the double
    /// cannot be mistaken for a partially-populated context. It stands in for the legacy
    /// <c>Globals.GetDomainName(Request)</c> reading that <c>Signup.ascx.vb:L232</c> composed beneath.
    /// </remarks>
    private static IPortalContext ResolvedTenant(
        string httpAlias = HostAlias,
        int portalAliasId = PortalAliasId)
    {
        var context = new Mock<IPortalContext>(MockBehavior.Loose);
        context.SetupGet(tenant => tenant.PortalId).Returns(PortalId);
        context.SetupGet(tenant => tenant.PortalName).Returns(PortalName);
        context.SetupGet(tenant => tenant.PortalAlias).Returns(httpAlias);
        context.SetupGet(tenant => tenant.PortalAliasId).Returns(portalAliasId);
        context.SetupGet(tenant => tenant.AdministratorId).Returns(AdministratorId);
        context.SetupGet(tenant => tenant.AdministratorRoleId).Returns(AdministratorRoleId);
        context.SetupGet(tenant => tenant.AdministratorRoleName).Returns("Administrators");
        context.SetupGet(tenant => tenant.RegisteredRoleId).Returns(RegisteredRoleId);
        context.SetupGet(tenant => tenant.RegisteredRoleName).Returns("Registered Users");

        return context.Object;
    }

    /// <summary>
    /// Arranges the harness as a request that reached the tenant through a host name OTHER than the alias
    /// row the test is about to rename or unbind.
    /// </summary>
    /// <param name="harness">The harness to arrange.</param>
    /// <remarks>
    /// <see cref="Harness.Ready"/> resolves the fixture's single alias row, which is the honest default: a
    /// request that reached a one-alias portal did arrive through that row. Every write against that row is
    /// therefore refused, so a test about the DUPLICATE check, the unique-index race, the rename write or
    /// the removal write has to say explicitly that it arrived somewhere else - otherwise it exercises the
    /// active-alias refusal instead of the path it is named for. The fact is stated here once so each such
    /// test needs a single line and the reason lives in one place.
    /// </remarks>
    private static void ArrivedThroughAnotherAlias(Harness harness)
    {
        harness.ContextResolved = true;
        harness.ResolvedContext = ResolvedTenant(SpareHostAlias, SparePortalAliasId);
    }

    /// <summary>
    /// A transaction scope that records whether it was committed and whether it was disposed.
    /// </summary>
    /// <remarks>
    /// The production scope rolls back on disposal without a commit, so a test that asserted only "the
    /// scope was disposed" would pass for a write that was abandoned. Recording BOTH facts is what lets a
    /// test distinguish a committed creation from a rolled-back one, which is the whole point of the
    /// transaction the service now opens.
    /// </remarks>
    private sealed class RecordingTransactionScope : ITransactionScope
    {
        /// <summary>Initialises a new instance of the <see cref="RecordingTransactionScope"/> class.</summary>
        /// <param name="isolation">The isolation the service asked for.</param>
        public RecordingTransactionScope(TransactionIsolation isolation)
        {
            Isolation = isolation;
        }

        /// <summary>The isolation the service asked for.</summary>
        public TransactionIsolation Isolation { get; }

        /// <summary>Whether the scope was committed.</summary>
        public bool Committed { get; private set; }

        /// <summary>Whether the scope was disposed.</summary>
        public bool Disposed { get; private set; }

        /// <summary>Whether the scope was abandoned - disposed without ever being committed.</summary>
        public bool RolledBack => Disposed && !Committed;

        /// <inheritdoc />
        public Task CommitAsync(CancellationToken cancellationToken = default)
        {
            Committed = true;

            return Task.CompletedTask;
        }

        /// <inheritdoc />
        public ValueTask DisposeAsync()
        {
            Disposed = true;

            return ValueTask.CompletedTask;
        }
    }

    /// <summary>
    /// Builds a provisioning request that passes every check the service performs.
    /// </summary>
    /// <returns>A well-formed provisioning request.</returns>
    private static CreatePortalRequest ValidCreateRequest() => new()
    {
        PortalName = PortalName,
        PortalAlias = HostAlias,
        HomeDirectory = "Portals/1",
        TemplateFile = "Default Website.template",
        AdministratorFirstName = "Ada",
        AdministratorLastName = "Lovelace",
        AdministratorUsername = AdministratorUsername,
        AdministratorPassword = AdministratorPassword,
        AdministratorEmail = AdministratorEmail,
    };

    /// <summary>
    /// Builds an update request that changes only the display name, leaving every host-only term as the
    /// stored tenant carries it.
    /// </summary>
    /// <returns>A well-formed update request.</returns>
    private static UpdatePortalRequest ValidUpdateRequest() => new()
    {
        // Carried so the request is representative of one that has passed the registered validator, whose
        // one rule on this member is that it equal the route identifier. The service never reads it - it
        // addresses the identifier it was given as an argument - so its presence changes no assertion here.
        PortalId = PortalId,
        PortalName = "Renamed",
        DefaultLanguage = DefaultLanguageCode,
        TimeZoneOffset = DefaultTimeZoneOffsetMinutes,
        HomeDirectory = "Portals/0",
        AdministratorId = AdministratorId,
    };

    /// <summary>
    /// Builds the route-owned settings request with the same ordinary values as
    /// <see cref="ValidUpdateRequest"/>.
    /// </summary>
    /// <returns>A well-formed settings update request.</returns>
    private static UpdatePortalSettingsRequest ValidSettingsUpdateRequest() => new()
    {
        PortalName = "Settings Renamed",
        DefaultLanguage = DefaultLanguageCode,
        TimeZoneOffset = DefaultTimeZoneOffsetMinutes,
        HomeDirectory = "Portals/0",
        AdministratorId = AdministratorId,
    };

    /// <summary>
    /// Assembles the service over twelve recording doubles, exposing every answer as mutable state so a test
    /// can change the world after the doubles have been wired.
    /// </summary>
    private sealed class Harness
    {
        private Harness()
        {
            Caching = new CachingOptions();
            PortalExists = true;
            PortalRow = StoredPortal();
            LookupAlias = Alias(PortalAliasId, PortalId, HostAlias);
            AllAliases = [Alias(PortalAliasId, PortalId, HostAlias)];
            PortalPage = PagedResult<Portal>.Unpaged([StoredPortal()]);
            RemainingPortalCount = 2;
            UserCount = 3;
            PageCount = 2;
            RoleNames = new Dictionary<int, string>
            {
                [AdministratorRoleId] = "Administrators",
                [RegisteredRoleId] = "Registered Users",
            };
            AdministratorAccount = new User
            {
                UserId = AdministratorId,
                Username = AdministratorUsername,
                FirstName = "Ada",
                LastName = "Lovelace",
                DisplayName = "Ada Lovelace",
                Email = AdministratorEmail,
            };
            ExistingMembership = new UserPortal
            {
                PortalId = PortalId,
                UserId = AdministratorId,
            };
            HostRootTab = HostRootTabId;
            HostSettingValues = [];
            CredentialCreated = true;
            CredentialDeleted = true;
            SessionRevocationResult = Result.Success();
            EchoCreatedPortal = true;
            SuperUser = true;
            CallerUserId = CallerId;

            AddedPortals = [];
            RemovedPortals = [];
            AddedAliases = [];
            UpdatedAliases = [];
            RemovedAliases = [];
            AddedRoles = [];
            RemovedRoles = [];
            AddedAssignments = [];
            RemovedAssignments = [];
            AddedUsers = [];
            RemovedUsers = [];
            AddedMemberships = [];
            RemovedMemberships = [];
            HashedSecrets = [];
            CreatedCredentials = [];
            DeletedCredentialUserIds = [];
            RevokedSessionUserIds = [];
            InvalidatedPortalIds = [];
            InvalidatedTabsPortalIds = [];
            AuditRecords = [];
            OpenedTransactions = [];
            ResolvedContext = ResolvedTenant();
            ContextResolved = true;
            AddedProfileDefinitions = [];
            AddedTabs = [];
            AddedTabPermissions = [];

            // The catalogue the upgrade scripts install for the page scope. Both keys the stock home page
            // needs are present, because the production database has them and a harness that omitted them
            // would silently exercise the skip path instead of the grant path.
            PageScopeCatalogue =
            [
                new Permission { PermissionId = 3, PermissionCode = TabScopeCode, PermissionKey = PermissionKey.VIEW, PermissionName = "View Tab" },
                new Permission { PermissionId = 4, PermissionCode = TabScopeCode, PermissionKey = PermissionKey.EDIT, PermissionName = "Edit Tab" },
            ];

            Portals = new Mock<IPortalRepository>(MockBehavior.Loose);
            Aliases = new Mock<IPortalAliasRepository>(MockBehavior.Loose);
            Tabs = new Mock<ITabRepository>(MockBehavior.Loose);
            Profiles = new Mock<IUserProfileRepository>(MockBehavior.Loose);
            Permissions = new Mock<IPermissionRepository>(MockBehavior.Loose);
            Users = new Mock<IUserRepository>(MockBehavior.Loose);
            Roles = new Mock<IRoleRepository>(MockBehavior.Loose);

            // A loose mock is enough: the tenant-removal sweep asks for the tenant's modules and removes
            // each one, and a loose mock answers the read with an empty list unless a test says otherwise.
            Modules = new Mock<IModuleRepository>(MockBehavior.Loose);
            Modules
                .Setup(modules => modules.GetByPortalIdAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => PortalModules);
            Modules
                .Setup(modules => modules.DeleteAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
                .Returns((int moduleId, CancellationToken _) =>
                {
                    RemovedModuleIds.Add(moduleId);
                    return Task.CompletedTask;
                });
            UnitOfWork = new Mock<IUnitOfWork>(MockBehavior.Loose);
            HostSettings = new Mock<IHostSettingsService>(MockBehavior.Loose);
            PasswordHasher = new Mock<IPasswordHasher>(MockBehavior.Loose);
            Tokens = new Mock<ITokenService>(MockBehavior.Loose);
            Clock = new Mock<IClock>(MockBehavior.Loose);
            Cache = new Mock<ICacheService>(MockBehavior.Loose);
            CurrentUser = new Mock<ICurrentUser>(MockBehavior.Loose);
            Audit = new Mock<IAuditSink>(MockBehavior.Loose);
            PortalContext = new Mock<IPortalContextHolder>(MockBehavior.Loose);

            Audit
                .Setup(sink => sink.Record(It.IsAny<AuditEvent>()))
                .Callback<AuditEvent>(AuditRecords.Add);

            // The tenant this request resolved to. A child portal's alias is composed beneath it, so the
            // double has to answer both members rather than only the one the composition reads: a holder
            // that reported itself unresolved while still yielding a context would be a state the
            // production holder cannot be in, and a test built on it would prove nothing.
            PortalContext.SetupGet(holder => holder.IsResolved).Returns(() => ContextResolved);
            PortalContext.SetupGet(holder => holder.Current).Returns(() => ResolvedContext);

            // Every write that spans more than one commit opens a transaction, and a loose mock would
            // otherwise hand back a null task. Each scope is recorded so a test can assert that the work
            // was committed rather than merely that it completed - disposal without a commit is how the
            // production code rolls back, so "committed" and "finished" are genuinely different outcomes.
            UnitOfWork
                .Setup(unit => unit.BeginTransactionAsync(
                    It.IsAny<TransactionIsolation>(),
                    It.IsAny<CancellationToken>()))
                .Returns<TransactionIsolation, CancellationToken>((isolation, _) =>
                {
                    var scope = new RecordingTransactionScope(isolation);
                    OpenedTransactions.Add(scope);
                    return Task.FromResult<ITransactionScope>(scope);
                });

            Service = new PortalService(
                Portals.Object,
                Aliases.Object,
                Tabs.Object,
                Profiles.Object,
                Permissions.Object,
                Users.Object,
                Roles.Object,
                Modules.Object,
                UnitOfWork.Object,
                HostSettings.Object,
                PasswordHasher.Object,
                Tokens.Object,
                Clock.Object,
                Cache.Object,
                CurrentUser.Object,
                Audit.Object,
                PortalContext.Object,
                Caching);
        }

        public PortalService Service { get; }

        public Mock<IPortalRepository> Portals { get; }

        public Mock<IPortalAliasRepository> Aliases { get; }

        public Mock<IUserProfileRepository> Profiles { get; }

        public Mock<IPermissionRepository> Permissions { get; }

        public Mock<ITabRepository> Tabs { get; }

        public Mock<IUserRepository> Users { get; }

        public Mock<IRoleRepository> Roles { get; }

        public Mock<IModuleRepository> Modules { get; }

        /// <summary>Gets the modules the tenant-removal sweep will find.</summary>
        public List<DnnMigration.Domain.Entities.Module> PortalModules { get; } = [];

        /// <summary>Gets the module identifiers the tenant-removal sweep staged for removal.</summary>
        public List<int> RemovedModuleIds { get; } = [];

        public Mock<IUnitOfWork> UnitOfWork { get; }

        public Mock<IHostSettingsService> HostSettings { get; }

        public Mock<IPasswordHasher> PasswordHasher { get; }

        public Mock<ITokenService> Tokens { get; }

        public Mock<IClock> Clock { get; }

        public Mock<ICacheService> Cache { get; }

        public Mock<ICurrentUser> CurrentUser { get; }

        public Mock<IAuditSink> Audit { get; }

        public Mock<IPortalContextHolder> PortalContext { get; }

        /// <summary>Every audit record the service emitted, in the order it emitted them.</summary>
        public List<AuditEvent> AuditRecords { get; }

        /// <summary>Every transaction the service opened, in the order it opened them.</summary>
        public List<RecordingTransactionScope> OpenedTransactions { get; }

        /// <summary>Whether the request resolved to a tenant at all.</summary>
        public bool ContextResolved { get; set; }

        /// <summary>The tenant the request resolved to, read when a child alias is composed.</summary>
        public IPortalContext ResolvedContext { get; set; }

        public CachingOptions Caching { get; }

        public bool PortalExists { get; set; }

        public Portal? PortalRow { get; set; }

        public PortalAlias? LookupAlias { get; set; }

        public List<PortalAlias> AllAliases { get; }

        public PagedResult<Portal> PortalPage { get; set; }

        public int RemainingPortalCount { get; set; }

        public int UserCount { get; set; }

        public int PageCount { get; set; }

        public Dictionary<int, string> RoleNames { get; }

        public User? AdministratorAccount { get; set; }

        public int? HostRootTab { get; set; }

        public Dictionary<string, string> HostSettingValues { get; }

        public bool AliasTaken { get; set; }

        public bool UsernameTaken { get; set; }

        public bool CredentialCreated { get; set; }

        public bool CredentialDeleted { get; set; }

        public Result SessionRevocationResult { get; set; }

        public Exception? CredentialFault { get; set; }

        public bool EchoCreatedPortal { get; set; }

        /// <summary>
        /// Whether the caller's STORED account is a host account. SEC-011: this is the authoritative knob,
        /// because the guard it drives re-reads the status from the store rather than trusting the token.
        /// </summary>
        public bool SuperUser { get; set; }

        /// <summary>
        /// What the caller's TOKEN claims about host status, when that must differ from the store. Left unset
        /// the claim mirrors the store, which is the ordinary case; setting it is how a test states the case
        /// the finding was about - a token minted before a demotion, or before a promotion.
        /// </summary>
        public bool? SuperUserClaim { get; set; }

        /// <summary>The caller's account key, or <see langword="null"/> for an unauthenticated caller.</summary>
        public int? CallerUserId { get; set; }

        public UserRole? ExistingAssignment { get; set; }

        public UserPortal? ExistingMembership { get; set; }

        public int Commits { get; private set; }

        public int CommitsBeforeCredential { get; private set; }

        public string? CacheKey { get; private set; }

        public TimeSpan CacheExpiration { get; private set; }

        public int HostInvalidations { get; private set; }

        public List<Portal> AddedPortals { get; }

        public List<Portal> RemovedPortals { get; }

        public List<PortalAlias> AddedAliases { get; }

        public List<PortalAlias> UpdatedAliases { get; }

        public List<PortalAlias> RemovedAliases { get; }

        public List<Role> AddedRoles { get; }

        public List<Role> RemovedRoles { get; }

        public List<UserRole> AddedAssignments { get; }

        public List<UserRole> RemovedAssignments { get; }

        public List<User> AddedUsers { get; }

        public List<User> RemovedUsers { get; }

        public List<User> PortalMembers { get; } = [];

        public List<UserPortal> AddedMemberships { get; }

        public List<UserPortal> RemovedMemberships { get; }

        public List<string> HashedSecrets { get; }

        public List<(int UserId, string PasswordHash, bool IsApproved, DateTime UtcNow)> CreatedCredentials { get; }

        public List<int> DeletedCredentialUserIds { get; }

        public List<int> RevokedSessionUserIds { get; }

        public List<int> InvalidatedPortalIds { get; }

        public List<int> InvalidatedTabsPortalIds { get; }

        public List<ProfilePropertyDefinition> AddedProfileDefinitions { get; }

        public List<Tab> AddedTabs { get; }

        public List<TabPermission> AddedTabPermissions { get; }

        /// <summary>
        /// The recorded events as name-and-property pairs, which is the shape part of this suite reads.
        /// </summary>
        /// <remarks>
        /// A projection over <see cref="AuditRecords"/> rather than a second capture, so there is one audit
        /// stream and no fact can pass against a record the service did not emit. The tenant identifier the
        /// structured record carries as a member is folded back into the dictionary under the same name.
        /// </remarks>
        public IReadOnlyList<(string EventName, IReadOnlyDictionary<string, string?> Properties)> AuditEvents =>
            AuditRecords
                .Select(record =>
                {
                    Dictionary<string, string?> properties =
                        new(record.Properties, StringComparer.Ordinal)
                        {
                            ["PortalId"] = record.PortalId?.ToString(CultureInfo.InvariantCulture),
                        };

                    return (record.EventName, (IReadOnlyDictionary<string, string?>)properties);
                })
                .ToList();

        public List<Permission> PageScopeCatalogue { get; set; }

        /// <summary>
        /// Builds a harness whose world is consistent: one tenant with one host name and an administrator on
        /// record, an installation holding two tenants, and a credential store that accepts writes.
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
            harness.Portals
                .Setup(p => p.CountUsersAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => harness.UserCount);
            harness.Portals
                .Setup(p => p.CountPagesAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => harness.PageCount);

            // The batched tallies answer the same figures as the per-portal members, so a test that sets
            // UserCount or PageCount sees the same number whichever member the code under test reaches
            // for. Both stubs honour the total contract the repository promises: every identifier that
            // was asked about is present as a key.
            harness.Portals
                .Setup(p => p.CountUsersForPortalsAsync(
                    It.IsAny<IReadOnlyCollection<int>>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync((IReadOnlyCollection<int> ids, CancellationToken _) =>
                    ids.Distinct().ToDictionary(id => id, _ => harness.UserCount));
            harness.Portals
                .Setup(p => p.CountPagesForPortalsAsync(
                    It.IsAny<IReadOnlyCollection<int>>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync((IReadOnlyCollection<int> ids, CancellationToken _) =>
                    ids.Distinct().ToDictionary(id => id, _ => harness.PageCount));
            harness.Portals
                .Setup(p => p.GetRoleNamesAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => harness.RoleNames);
            harness.Portals
                .Setup(p => p.TabBelongsToPortalAsync(
                    It.IsAny<int>(),
                    It.IsAny<int>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(true);

            // The default profile property definitions the new tenant receives are captured so a test can
            // assert their number, their categories and their view ordering.
            harness.Profiles
                .Setup(p => p.AddDefinitionAsync(
                    It.IsAny<ProfilePropertyDefinition>(),
                    It.IsAny<CancellationToken>()))
                .Callback((ProfilePropertyDefinition definition, CancellationToken _) =>
                    harness.AddedProfileDefinitions.Add(definition))
                .Returns(Task.CompletedTask);

            // MIGRATION: SEC-F1. THE STUB NOW MIRRORS PRODUCTION, AND THE CORRECTION IS THE POINT OF IT.
            // The page-SCOPED read is what the service asks, and it is answered from the harness catalogue
            // filtered by key, exactly as the repository filters by scope code and key and orders by
            // identifier.
            //
            // The page-scoped-by-TAB read is deliberately stubbed to answer NOTHING. An earlier revision of
            // this harness answered it with the whole catalogue, on the stated belief that production
            // "ignores its page argument" - and that belief was false: the repository proves the page exists
            // before it answers. Because the home page has no identifier until the creation commits, the
            // service was asking about page zero, so on a database with no page keyed zero production got an
            // empty catalogue and silently skipped all three grants while these facts passed. Answering
            // empty here is what makes this suite able to fail if the resolution ever moves back.
            harness.Permissions
                .Setup(p => p.GetByCodeAndKeyAsync(
                    It.IsAny<string>(),
                    It.IsAny<PermissionKey>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync((string code, PermissionKey key, CancellationToken _) =>
                    harness.PageScopeCatalogue
                        .Where(entry => string.Equals(entry.PermissionCode, code, StringComparison.Ordinal)
                            && entry.PermissionKey == key)
                        .OrderBy(entry => entry.PermissionId)
                        .ToArray());
            harness.Permissions
                .Setup(p => p.GetByTabIdAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(Array.Empty<Permission>());
            harness.Permissions
                .Setup(p => p.AddTabPermissionAsync(
                    It.IsAny<TabPermission>(),
                    It.IsAny<CancellationToken>()))
                .Callback((TabPermission grant, CancellationToken _) =>
                    harness.AddedTabPermissions.Add(grant))
                .Returns(Task.CompletedTask);

            harness.Portals
                .Setup(p => p.ListAsync(
                    It.IsAny<int>(),
                    It.IsAny<int>(),
                    It.IsAny<string?>(),
                    It.IsAny<string?>(),
                    It.IsAny<bool>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => harness.PortalPage);
            harness.Portals
                .Setup(p => p.ListAsync(0, 1, null, null, false, It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => PagedResult<Portal>.Create(
                    Array.Empty<Portal>(),
                    harness.RemainingPortalCount,
                    0,
                    1));
            harness.Portals
                .Setup(p => p.AddAsync(It.IsAny<Portal>(), It.IsAny<CancellationToken>()))
                .Callback<Portal, CancellationToken>((portal, _) =>
                {
                    harness.AddedPortals.Add(portal);
                    if (harness.EchoCreatedPortal)
                    {
                        harness.PortalRow = portal;
                    }
                })
                .Returns(Task.CompletedTask);
            harness.Portals
                .Setup(p => p.DeleteAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
                .Callback<int, CancellationToken>((portalId, _) =>
                {
                    // The contract identifies its deletion target by identifier, as the legacy
                    // procedure did, so the harness resolves the identifier back to the entity it
                    // already knows about. That keeps RemovedPortals a list of entities and lets
                    // the suite go on asserting *which* tenant was removed by reference rather
                    // than merely that some identifier was passed.
                    Portal? removed = harness.AddedPortals.Find(candidate => candidate.PortalId == portalId);

                    if (removed is null && harness.PortalRow is Portal known && known.PortalId == portalId)
                    {
                        removed = known;
                    }

                    if (removed is not null)
                    {
                        harness.RemovedPortals.Add(removed);
                    }
                })
                .Returns(Task.CompletedTask);

            // The installation-wide read and the portal-scoped read are separate members, so they are
            // stubbed separately. The alias repository no longer accepts a nullable portal identifier in
            // which null - or the legacy -1 - meant "every portal".
            harness.Aliases
                .Setup(a => a.GetAllAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => harness.AllAliases.ToList());
            harness.Aliases
                .Setup(a => a.GetByPortalIdAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((int portalId, CancellationToken _) =>
                    harness.AllAliases.Where(alias => alias.PortalId == portalId).ToList());
            // The SET-based read the tenant listing uses: it asks for the aliases of the tenants on its
            // page rather than for every alias in the installation. Served from the same alias world as the
            // other two reads, and honouring the same rule as the single-portal read - every value denotes
            // the tenant bearing it, so no member of the set is a wildcard.
            harness.Aliases
                .Setup(a => a.GetByPortalIdsAsync(
                    It.IsAny<IReadOnlyCollection<int>>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync((IReadOnlyCollection<int> portalIds, CancellationToken _) =>
                    harness.AllAliases.Where(alias => portalIds.Contains(alias.PortalId)).ToList());
            harness.Aliases
                .Setup(a => a.GetByIdAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => harness.LookupAlias);
            harness.Aliases
                .Setup(a => a.AliasExistsAsync(
                    It.IsAny<string>(),
                    It.IsAny<int?>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => harness.AliasTaken);
            harness.Aliases
                .Setup(a => a.AddAsync(It.IsAny<PortalAlias>(), It.IsAny<CancellationToken>()))
                .Callback<PortalAlias, CancellationToken>((alias, _) => harness.AddedAliases.Add(alias))
                .Returns(Task.CompletedTask);
            harness.Aliases
                .Setup(a => a.UpdateAsync(It.IsAny<PortalAlias>(), It.IsAny<CancellationToken>()))
                .Callback<PortalAlias, CancellationToken>((alias, _) => harness.UpdatedAliases.Add(alias))
                .Returns(Task.CompletedTask);
            harness.Aliases
                .Setup(a => a.DeleteAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
                .Callback<int, CancellationToken>((portalAliasId, _) =>
                {
                    // Deletion identifies its target by key, as the legacy procedure did, so the harness
                    // resolves the identifier back to the entity it already knows about. That keeps
                    // RemovedAliases a list of entities and lets the suite go on asserting *which* alias
                    // was removed by reference rather than merely that some identifier was passed.
                    //
                    // A staged-but-uncommitted alias carries no key yet, so the created row is matched
                    // here by the identifier it actually has rather than by a generated one.
                    PortalAlias? removed = harness.AddedAliases
                        .Find(candidate => candidate.PortalAliasId == portalAliasId);

                    // The row the service read through GetByIdAsync is preferred over an equally-keyed
                    // instance in the installation-wide list, because it is the instance the service
                    // actually operated on and the suite asserts identity rather than equality.
                    if (removed is null
                        && harness.LookupAlias is PortalAlias looked
                        && looked.PortalAliasId == portalAliasId)
                    {
                        removed = looked;
                    }

                    removed ??= harness.PortalRow?.PortalAliases
                        .FirstOrDefault(candidate => candidate.PortalAliasId == portalAliasId);

                    removed ??= harness.AllAliases
                        .Find(candidate => candidate.PortalAliasId == portalAliasId);

                    if (removed is not null)
                    {
                        harness.RemovedAliases.Add(removed);
                    }
                })
                .Returns(Task.CompletedTask);

            harness.Tabs
                .Setup(t => t.GetHostRootTabIdAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => harness.HostRootTab);
            harness.Tabs
                .Setup(t => t.AddAsync(It.IsAny<Tab>(), It.IsAny<CancellationToken>()))
                .Callback((Tab page, CancellationToken _) => harness.AddedTabs.Add(page))
                .Returns(Task.CompletedTask);

            harness.Users
                .Setup(u => u.UsernameExistsAsync(
                    It.IsAny<string>(),
                    It.IsAny<int?>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => harness.UsernameTaken);
            harness.Users
                .Setup(u => u.GetAsync(It.IsAny<int?>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => harness.AdministratorAccount);
            harness.Users
                .Setup(u => u.GetMembershipAsync(
                    It.IsAny<int>(),
                    It.IsAny<int>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => harness.ExistingMembership);
            harness.Users
                .Setup(u => u.ListPortalMembersForRemovalAsync(
                    It.IsAny<int>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => harness.PortalMembers);
            harness.Users
                .Setup(u => u.Add(It.IsAny<User>()))
                .Callback<User>(harness.AddedUsers.Add);
            harness.Users
                .Setup(u => u.Remove(It.IsAny<User>()))
                .Callback<User>(harness.RemovedUsers.Add);
            harness.Users
                .Setup(u => u.AddMembership(It.IsAny<UserPortal>()))
                .Callback<UserPortal>(harness.AddedMemberships.Add);
            harness.Users
                .Setup(u => u.RemoveMembership(It.IsAny<UserPortal>()))
                .Callback<UserPortal>(harness.RemovedMemberships.Add);
            harness.Users
                .Setup(u => u.CreateCredentialAsync(
                    It.IsAny<int>(),
                    It.IsAny<string>(),
                    It.IsAny<bool>(),
                    It.IsAny<DateTime>(),
                    It.IsAny<CancellationToken>()))
                .Returns((int userId, string hash, bool isApproved, DateTime utcNow, CancellationToken _) =>
                {
                    harness.CommitsBeforeCredential = harness.Commits;
                    if (harness.CredentialFault is Exception fault)
                    {
                        throw fault;
                    }

                    harness.CreatedCredentials.Add((userId, hash, isApproved, utcNow));
                    return Task.FromResult(harness.CredentialCreated);
                });
            harness.Users
                .Setup(u => u.DeleteCredentialAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
                .Returns((int userId, CancellationToken _) =>
                {
                    harness.DeletedCredentialUserIds.Add(userId);
                    return Task.FromResult(harness.CredentialDeleted);
                });

            harness.Tokens
                .Setup(tokens => tokens.RevokeAllRefreshTokensAsync(
                    It.IsAny<int>(),
                    It.IsAny<CancellationToken>()))
                .Returns((int userId, CancellationToken _) =>
                {
                    harness.RevokedSessionUserIds.Add(userId);
                    return Task.FromResult(harness.SessionRevocationResult);
                });

            harness.Roles
                .Setup(r => r.AddAsync(It.IsAny<Role>(), It.IsAny<CancellationToken>()))
                .Callback<Role, CancellationToken>((role, _) => harness.AddedRoles.Add(role))
                .Returns(Task.CompletedTask);

            // A role delete carries its key, and a portal compensation deletes the three stock roles it
            // created, so the harness records the keys and resolves them back to the role objects the
            // assertions name.
            harness.Roles
                .Setup(r => r.DeleteAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
                .Callback<int, CancellationToken>((roleId, _) =>
                {
                    Role? matched = harness.AddedRoles.FirstOrDefault(role => role.RoleId == roleId);
                    harness.RemovedRoles.Add(matched ?? new Role { RoleId = roleId });
                })
                .Returns(Task.CompletedTask);

            harness.Roles
                .Setup(r => r.AddUserRoleAsync(It.IsAny<UserRole>(), It.IsAny<CancellationToken>()))
                .Callback<UserRole, CancellationToken>((assignment, _) => harness.AddedAssignments.Add(assignment))
                .Returns(Task.CompletedTask);
            harness.Roles
                .Setup(r => r.DeleteUserRoleAsync(
                    It.IsAny<int>(),
                    It.IsAny<int>(),
                    It.IsAny<CancellationToken>()))
                .Callback<int, int, CancellationToken>((userId, roleId, _) =>
                    harness.RemovedAssignments.Add(
                        harness.ExistingAssignment is { } existing
                            && existing.UserId == userId
                            && existing.RoleId == roleId
                                ? existing
                                : new UserRole { UserId = userId, RoleId = roleId }))
                .Returns(Task.CompletedTask);
            harness.Roles
                .Setup(r => r.GetUserRoleAsync(
                    It.IsAny<int>(),
                    It.IsAny<int>(),
                    It.IsAny<int>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => harness.ExistingAssignment);

            harness.HostSettings
                .Setup(h => h.GetSettingsAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => harness.HostSettingValues);

            harness.PasswordHasher
                .Setup(h => h.Hash(It.IsAny<string>()))
                .Returns((string raw) =>
                {
                    harness.HashedSecrets.Add(raw);
                    return PasswordHash;
                });

            harness.Clock.SetupGet(c => c.UtcNow).Returns(Now);

            // SEC-011: THE CLAIM AND THE STORE ARE WIRED SEPARATELY, SO A TEST CAN MAKE THEM DISAGREE. The
            // claim follows the store unless a test overrides it, which keeps every existing fact meaning what
            // it says while giving the demotion facts a way to state their case.
            harness.CurrentUser
                .SetupGet(c => c.IsSuperUser)
                .Returns(() => harness.SuperUserClaim ?? harness.SuperUser);
            harness.CurrentUser
                .SetupGet(c => c.IsAuthenticated)
                .Returns(() => harness.CallerUserId is not null);
            harness.CurrentUser
                .SetupGet(c => c.UserId)
                .Returns(() => harness.CallerUserId);

            // Declared AFTER the catch-all account read above, so it wins for the caller's own installation-wide
            // lookup while every other lookup still resolves to the administrator account.
            harness.Users
                .Setup(u => u.GetAsync(
                    It.Is<int?>(portalId => portalId == null),
                    It.Is<int>(userId => userId == harness.CallerUserId),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => harness.CallerUserId is null
                    ? null
                    : new User
                    {
                        UserId = harness.CallerUserId.Value,
                        Username = "caller",
                        IsSuperUser = harness.SuperUser,
                    });

            harness.Cache
                .Setup(c => c.InvalidateHost())
                .Callback(() => harness.HostInvalidations++);
            harness.Cache
                .Setup(c => c.InvalidatePortal(It.IsAny<int>()))
                .Callback<int>(harness.InvalidatedPortalIds.Add);
            harness.Cache
                .Setup(c => c.InvalidateTabs(It.IsAny<int>()))
                .Callback<int>(harness.InvalidatedTabsPortalIds.Add);
            harness.Cache
                .Setup(c => c.GetOrCreateAsync(
                    It.IsAny<string>(),
                    It.IsAny<Func<CancellationToken, Task<PortalDetailDto?>>>(),
                    It.IsAny<TimeSpan>(),
                    It.IsAny<CancellationToken>()))
                .Returns((
                    string key,
                    Func<CancellationToken, Task<PortalDetailDto?>> factory,
                    TimeSpan expiration,
                    CancellationToken token) =>
                {
                    harness.CacheKey = key;
                    harness.CacheExpiration = expiration;
                    return factory(token);
                });

            harness.UnitOfWork
                .Setup(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()))
                .Returns(() =>
                {
                    harness.Commits++;
                    return Task.FromResult(1);
                });

            return harness;
        }
    }
}
