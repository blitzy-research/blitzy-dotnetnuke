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

    private const int AdministratorId = 7;

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

    private static readonly DateTime Now = new(2026, 8, 2, 12, 0, 0, DateTimeKind.Utc);

    /// <summary>
    /// The tenant contract exposes eleven asynchronous operations and nothing else.
    /// </summary>
    [Fact]
    public void PortalContract_OffersExactlyElevenOperations()
    {
        MethodInfo[] members = typeof(IPortalService).GetMethods();

        members.Should().HaveCount(11);
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
        var unitOfWork = new Mock<IUnitOfWork>().Object;
        var hostSettings = new Mock<IHostSettingsService>().Object;
        var hasher = new Mock<IPasswordHasher>().Object;
        var clock = new Mock<IClock>().Object;
        var cache = new Mock<ICacheService>().Object;
        var currentUser = new Mock<ICurrentUser>().Object;
        var audit = new Mock<IAuditSink>().Object;
        var portalContext = new Mock<IPortalContextHolder>().Object;
        var caching = new CachingOptions();

        Assert.Throws<ArgumentNullException>("portals", () =>
        {
            _ = new PortalService(
                null!, aliases, tabs, profiles, permissions, users, roles, unitOfWork, hostSettings, hasher, clock,
                cache, currentUser, audit, portalContext, caching);
        });
        Assert.Throws<ArgumentNullException>("aliases", () =>
        {
            _ = new PortalService(
                portals, null!, tabs, profiles, permissions, users, roles, unitOfWork, hostSettings, hasher, clock,
                cache, currentUser, audit, portalContext, caching);
        });
        Assert.Throws<ArgumentNullException>("tabs", () =>
        {
            _ = new PortalService(
                portals, aliases, null!, profiles, permissions, users, roles, unitOfWork, hostSettings, hasher,
                clock, cache, currentUser, audit, portalContext, caching);
        });
        Assert.Throws<ArgumentNullException>("profiles", () =>
        {
            _ = new PortalService(
                portals, aliases, tabs, null!, permissions, users, roles, unitOfWork, hostSettings, hasher, clock,
                cache, currentUser, audit, portalContext, caching);
        });
        Assert.Throws<ArgumentNullException>("permissions", () =>
        {
            _ = new PortalService(
                portals, aliases, tabs, profiles, null!, users, roles, unitOfWork, hostSettings, hasher, clock,
                cache, currentUser, audit, portalContext, caching);
        });
        Assert.Throws<ArgumentNullException>("users", () =>
        {
            _ = new PortalService(
                portals, aliases, tabs, profiles, permissions, null!, roles, unitOfWork, hostSettings, hasher,
                clock, cache, currentUser, audit, portalContext, caching);
        });
        Assert.Throws<ArgumentNullException>("roles", () =>
        {
            _ = new PortalService(
                portals, aliases, tabs, profiles, permissions, users, null!, unitOfWork, hostSettings, hasher,
                clock, cache, currentUser, audit, portalContext, caching);
        });
        Assert.Throws<ArgumentNullException>("unitOfWork", () =>
        {
            _ = new PortalService(
                portals, aliases, tabs, profiles, permissions, users, roles, null!, hostSettings, hasher, clock,
                cache, currentUser, audit, portalContext, caching);
        });
        Assert.Throws<ArgumentNullException>("hostSettings", () =>
        {
            _ = new PortalService(
                portals, aliases, tabs, profiles, permissions, users, roles, unitOfWork, null!, hasher, clock,
                cache, currentUser, audit, portalContext, caching);
        });
        Assert.Throws<ArgumentNullException>("passwordHasher", () =>
        {
            _ = new PortalService(
                portals, aliases, tabs, profiles, permissions, users, roles, unitOfWork, hostSettings, null!,
                clock, cache, currentUser, audit, portalContext, caching);
        });
        Assert.Throws<ArgumentNullException>("clock", () =>
        {
            _ = new PortalService(
                portals, aliases, tabs, profiles, permissions, users, roles, unitOfWork, hostSettings, hasher,
                null!, cache, currentUser, audit, portalContext, caching);
        });
        Assert.Throws<ArgumentNullException>("cache", () =>
        {
            _ = new PortalService(
                portals, aliases, tabs, profiles, permissions, users, roles, unitOfWork, hostSettings, hasher,
                clock, null!, currentUser, audit, portalContext, caching);
        });
        Assert.Throws<ArgumentNullException>("currentUser", () =>
        {
            _ = new PortalService(
                portals, aliases, tabs, profiles, permissions, users, roles, unitOfWork, hostSettings, hasher,
                clock, cache, null!, audit, portalContext, caching);
        });
        Assert.Throws<ArgumentNullException>("audit", () =>
        {
            _ = new PortalService(
                portals, aliases, tabs, profiles, permissions, users, roles, unitOfWork, hostSettings, hasher,
                clock, cache, currentUser, null!, portalContext, caching);
        });
        Assert.Throws<ArgumentNullException>("portalContext", () =>
        {
            _ = new PortalService(
                portals, aliases, tabs, profiles, permissions, users, roles, unitOfWork, hostSettings, hasher,
                clock, cache, currentUser, audit, null!, caching);
        });
        Assert.Throws<ArgumentNullException>("caching", () =>
        {
            _ = new PortalService(
                portals, aliases, tabs, profiles, permissions, users, roles, unitOfWork, hostSettings, hasher,
                clock, cache, currentUser, audit, portalContext, null!);
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
    /// C-02: a page permission key the catalogue does not define is skipped, and the tenant is still created.
    /// </summary>
    /// <remarks>
    /// A grant naming a definition that does not exist would violate the foreign key and discard the whole
    /// tenant over reference data the upgrade scripts own, so the page is created without that grant instead.
    /// </remarks>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task CreatePortal_SkipsAHomePageGrantTheCatalogueDoesNotDefine()
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

        outcome.IsSuccess.Should().BeTrue();
        harness.AddedTabs.Should().ContainSingle();
        harness.AddedTabPermissions.Should().HaveCount(2, "only the view key resolves");
        harness.AddedTabPermissions.Should().OnlyContain(grant => grant.PermissionId == 3);
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

        (string EventName, IReadOnlyDictionary<string, string?> Properties) recorded =
            harness.AuditEvents.Should().ContainSingle().Subject;

        recorded.EventName.Should().Be("PORTAL_CREATED");
        recorded.Properties.Should().ContainKey("PortalId");
        recorded.Properties["PortalName"].Should().Be(PortalName);
        recorded.Properties["PortalAlias"].Should().Be(HostAlias);
        recorded.Properties["AdministratorUsername"].Should().Be(AdministratorUsername);
        recorded.Properties.Values.Should().NotContain(
            AdministratorPassword,
            "the legacy entry did not record the credential and neither does this one");
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
        recorded.Properties["PortalName"].Should().Be(PortalName);
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
    /// The NAME is the load-bearing property. The row is gone by the time anyone reads the trail, so an
    /// identifier alone would no longer resolve to anything and the record would not answer "which tenant was
    /// removed". The count of released host names is carried for the same reason.
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
        record.ActorUserName.Should().Be("host");
        record.Properties["PortalName"].Should().Be(PortalName);
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

        AuditEvent record = harness.AuditRecords.Should().ContainSingle().Subject;
        record.EventName.Should().Be(AuditEventNames.PortalCreated);
        record.Outcome.Should().Be(AuditOutcome.Succeeded);
        record.ResourceType.Should().Be("Portal");
        record.ActorUserId.Should().Be(11);
        record.ActorUserName.Should().Be("host");
        record.SubjectUserId.Should().Be(harness.AddedUsers.Single().UserId);
        record.Properties["PortalName"].Should().Be(PortalName);
        record.Properties["PortalAlias"].Should().Be(HostAlias);
        record.Properties["IsChildPortal"].Should().Be("False");
        record.Properties["AdministratorUsername"].Should().Be(AdministratorUsername);
        record.Properties["AdministratorEmail"].Should().Be(AdministratorEmail);
        record.Properties["Description"].Should().Be("A measured tenant");
        record.Properties["Keywords"].Should().Be("measured, tenant");

        record.Properties.Should().NotContainKey("TemplateFile");
        record.Properties.Should().NotContainKey("TemplatePath");
        record.Properties.Should().NotContainKey("ServerPath");
        record.Properties.Should().NotContainKey("ChildPath");
        record.Properties.Values.Should().NotContain(AdministratorPassword);
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
    /// The composed child address, not the submitted segment, is what the uniqueness check probes and what
    /// the audit record names.
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

        AuditEvent record = harness.AuditRecords.Should().ContainSingle().Subject;
        record.Properties["PortalAlias"].Should().Be("parent.example/sales");
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
    /// Changing a host name writes the trimmed name, leaves the owning tenant alone - the request declares no
    /// tenant member, so a move between tenants is not expressible - and discards the cache of the tenant that
    /// actually owns the row.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task UpdatePortalAlias_DoesNotMoveTheHostNameBetweenTenants()
    {
        Harness harness = Harness.Ready();

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
    /// <returns>A resolved tenant context.</returns>
    /// <remarks>
    /// The alias is the only member the composition reads, but the whole contract is answered so the double
    /// cannot be mistaken for a partially-populated context. It stands in for the legacy
    /// <c>Globals.GetDomainName(Request)</c> reading that <c>Signup.ascx.vb:L232</c> composed beneath.
    /// </remarks>
    private static IPortalContext ResolvedTenant(string httpAlias = HostAlias)
    {
        var context = new Mock<IPortalContext>(MockBehavior.Loose);
        context.SetupGet(tenant => tenant.PortalId).Returns(PortalId);
        context.SetupGet(tenant => tenant.PortalName).Returns(PortalName);
        context.SetupGet(tenant => tenant.PortalAlias).Returns(httpAlias);
        context.SetupGet(tenant => tenant.AdministratorId).Returns(AdministratorId);
        context.SetupGet(tenant => tenant.AdministratorRoleId).Returns(AdministratorRoleId);
        context.SetupGet(tenant => tenant.AdministratorRoleName).Returns("Administrators");
        context.SetupGet(tenant => tenant.RegisteredRoleId).Returns(RegisteredRoleId);
        context.SetupGet(tenant => tenant.RegisteredRoleName).Returns("Registered Users");

        return context.Object;
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
            HostRootTab = HostRootTabId;
            HostSettingValues = [];
            CredentialCreated = true;
            EchoCreatedPortal = true;
            SuperUser = true;

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
            UnitOfWork = new Mock<IUnitOfWork>(MockBehavior.Loose);
            HostSettings = new Mock<IHostSettingsService>(MockBehavior.Loose);
            PasswordHasher = new Mock<IPasswordHasher>(MockBehavior.Loose);
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
                UnitOfWork.Object,
                HostSettings.Object,
                PasswordHasher.Object,
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

        public Mock<IUnitOfWork> UnitOfWork { get; }

        public Mock<IHostSettingsService> HostSettings { get; }

        public Mock<IPasswordHasher> PasswordHasher { get; }

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

        public Exception? CredentialFault { get; set; }

        public bool EchoCreatedPortal { get; set; }

        public bool SuperUser { get; set; }

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

        public List<UserPortal> AddedMemberships { get; }

        public List<UserPortal> RemovedMemberships { get; }

        public List<string> HashedSecrets { get; }

        public List<(int UserId, string PasswordHash, bool IsApproved, DateTime UtcNow)> CreatedCredentials { get; }

        public List<int> DeletedCredentialUserIds { get; }

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

            // The default profile property definitions the new tenant receives are captured so a test can
            // assert their number, their categories and their view ordering.
            harness.Profiles
                .Setup(p => p.AddDefinitionAsync(
                    It.IsAny<ProfilePropertyDefinition>(),
                    It.IsAny<CancellationToken>()))
                .Callback((ProfilePropertyDefinition definition, CancellationToken _) =>
                    harness.AddedProfileDefinitions.Add(definition))
                .Returns(Task.CompletedTask);

            // The page scope catalogue is a REAL read in production - GetPermissionsByTabID selects every
            // entry carrying the page scope code and ignores its page argument - so the stub likewise
            // ignores the identifier it is handed, which is zero at the point the home page asks.
            harness.Permissions
                .Setup(p => p.GetByTabIdAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => harness.PageScopeCatalogue);
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
                    return Task.FromResult(true);
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

            harness.CurrentUser.SetupGet(c => c.IsSuperUser).Returns(() => harness.SuperUser);

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
