using DnnMigration.Domain.Abstractions.Repositories;
using DnnMigration.Domain.Common;
using DnnMigration.Domain.Entities;
using DnnMigration.Domain.Enums;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DnnMigration.IntegrationTests.Persistence;

/// <summary>
/// Covers the tenant repository against the existing schema.
/// </summary>
/// <remarks>
/// <para>
/// These assertions run below the HTTP boundary, which is what makes them worth having alongside the
/// endpoint suites. A filter that matched a fragment rather than a prefix, an ordering that omitted its
/// tie-break, or a count that read the wrong table would all still produce a well-formed response, so the
/// endpoint suites cannot distinguish them. The repository contract can.
/// </para>
/// <para>
/// Every result here is the real <see cref="PagedResult{T}"/>, not the envelope the endpoint suites read
/// into. That type has get-only members and a private constructor, so it can be produced but not
/// deserialised; reading it directly is only possible because no serialisation is involved at this level.
/// </para>
/// </remarks>
[Trait("Category", "Integration")]
[Collection(IntegrationTestCollection.Name)]
public sealed class PortalRepositoryTests
{
    private const int UnknownPortalId = 987654;
    private const int UnknownRoleId = 987654;

    private readonly ApiTestFixture _fixture;

    /// <summary>Initialises a new instance of the <see cref="PortalRepositoryTests"/> class.</summary>
    /// <param name="fixture">The shared host and database.</param>
    public PortalRepositoryTests(ApiTestFixture fixture) => _fixture = fixture;

    /// <summary>The seeded tenant reads back with the values the seed wrote.</summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task GetAsync_ReturnsTheSeededTenantWithItsLegacyValues()
    {
        using IServiceScope scope = _fixture.Services.CreateScope();
        IPortalRepository portals = scope.ServiceProvider.GetRequiredService<IPortalRepository>();

        Portal? portal = await portals.GetByIdAsync(_fixture.Seed.PortalId);

        portal.Should().NotBeNull();
        portal!.PortalId.Should().Be(_fixture.Seed.PortalId);
        portal.PortalName.Should().Be(IntegrationSeed.PortalName);
        portal.HostFee.Should().Be(0m);
        portal.HomeDirectory.Should().BeEmpty();
        portal.DefaultLanguage.Should().Be("en-US");
        portal.TimeZoneOffset.Should().Be(-8);
        portal.Currency.Should().Be("USD");
        portal.AdministratorId.Should().Be(_fixture.Seed.AdminUserId);
        portal.AdministratorRoleId.Should().Be(_fixture.Seed.AdministratorRoleId);
        portal.RegisteredRoleId.Should().Be(_fixture.Seed.RegisteredRoleId);
        portal.PortalGuid.Should().NotBe(Guid.Empty);
    }

    /// <summary>
    /// The first tenant of an installation carries the identifier minus one, and the repository treats it as
    /// an identifier rather than as an absent value.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// <c>Portals.PortalID</c> is declared <c>IDENTITY(-1, 1)</c>, and minus one is simultaneously the value
    /// the legacy code used to mean "no integer". Anything that treated the two as interchangeable would
    /// make the first tenant of every installation unreachable, so the collision is asserted rather than
    /// left to reasoning.
    /// </remarks>
    [Fact]
    public async Task GetAsync_TreatsTheNegativeIdentitySeedAsAnIdentifier()
    {
        _fixture.Seed.PortalId.Should().Be(-1, "the seeded tenant is the first row of a freshly created database");

        using IServiceScope scope = _fixture.Services.CreateScope();
        IPortalRepository portals = scope.ServiceProvider.GetRequiredService<IPortalRepository>();

        (await portals.ExistsAsync(-1)).Should().BeTrue();
        (await portals.GetByIdAsync(-1)).Should().NotBeNull();
    }

    /// <summary>Aliases load only when the caller asks for them.</summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// The opt-in matters because tenant resolution reads aliases on every request while the tenant listing
    /// does not. Loading them unconditionally would add a join to every read of the table for the benefit of
    /// one caller.
    /// </remarks>
    [Fact]
    public async Task GetAsync_LoadsAliasesOnlyWhenRequested()
    {
        using (IServiceScope withAliases = _fixture.Services.CreateScope())
        {
            IPortalRepository portals = withAliases.ServiceProvider.GetRequiredService<IPortalRepository>();

            Portal? portal = await portals.GetByIdAsync(_fixture.Seed.PortalId, includeAliases: true);

            portal.Should().NotBeNull();
            portal!.PortalAliases.Should().NotBeEmpty();
            portal.PortalAliases.Select(alias => alias.HttpAlias)
                .Should().Contain(ApiTestFixture.TestHost);
        }

        using IServiceScope withoutAliases = _fixture.Services.CreateScope();
        IPortalRepository bare = withoutAliases.ServiceProvider.GetRequiredService<IPortalRepository>();

        Portal? unloaded = await bare.GetByIdAsync(_fixture.Seed.PortalId);

        unloaded.Should().NotBeNull();
        unloaded!.PortalAliases.Should().BeEmpty();
    }

    /// <summary>An unknown identifier produces nothing rather than an exception.</summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task GetAsync_WithAnUnknownIdentifier_ReturnsNull()
    {
        using IServiceScope scope = _fixture.Services.CreateScope();
        IPortalRepository portals = scope.ServiceProvider.GetRequiredService<IPortalRepository>();

        (await portals.GetByIdAsync(UnknownPortalId)).Should().BeNull();
        (await portals.ExistsAsync(UnknownPortalId)).Should().BeFalse();
    }

    /// <summary>An unpaged listing returns every tenant and reports itself as unpaged.</summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task ListAsync_WithAZeroPageSize_ReturnsEveryTenantUnpaged()
    {
        using IServiceScope scope = _fixture.Services.CreateScope();
        IPortalRepository portals = scope.ServiceProvider.GetRequiredService<IPortalRepository>();

        PagedResult<Portal> page = await portals.ListAsync(0, 0, null, null, descending: false);

        page.IsUnpaged.Should().BeTrue();
        page.Items.Should().NotBeEmpty();
        page.TotalCount.Should().Be(page.Items.Count);
        page.Items.Select(portal => portal.PortalId).Should().Contain(_fixture.Seed.PortalId);
    }

    /// <summary>The reported total counts every match, not just the page that was returned.</summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task ListAsync_Paged_ReportsTheTotalIndependentlyOfThePageSize()
    {
        int firstId = await CreatePortalAsync(FormattableString.Invariant($"Paging Portal A {Suffix()}"));
        int secondId = await CreatePortalAsync(FormattableString.Invariant($"Paging Portal B {Suffix()}"));

        try
        {
            using IServiceScope scope = _fixture.Services.CreateScope();
            IPortalRepository portals = scope.ServiceProvider.GetRequiredService<IPortalRepository>();

            PagedResult<Portal> everything = await portals.ListAsync(0, 0, null, null, descending: false);
            PagedResult<Portal> first = await portals.ListAsync(0, 1, null, null, descending: false);
            PagedResult<Portal> second = await portals.ListAsync(1, 1, null, null, descending: false);

            first.Items.Should().HaveCount(1);
            first.PageIndex.Should().Be(0);
            first.PageSize.Should().Be(1);
            first.IsUnpaged.Should().BeFalse();
            first.TotalCount.Should().Be(everything.Items.Count);
            first.TotalCount.Should().BeGreaterThanOrEqualTo(3);

            second.Items.Should().HaveCount(1);
            second.PageIndex.Should().Be(1);
            second.Items[0].PortalId.Should().NotBe(first.Items[0].PortalId);

            first.HasPreviousPage.Should().BeFalse();
            first.HasNextPage.Should().BeTrue();
            second.HasPreviousPage.Should().BeTrue();
        }
        finally
        {
            await RemovePortalAsync(firstId);
            await RemovePortalAsync(secondId);
        }
    }

    /// <summary>
    /// A page beyond the last one is empty but still reports the true total, so a caller can recover rather
    /// than concluding the collection is empty.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task ListAsync_BeyondTheLastPage_IsEmptyButStillReportsTheTotal()
    {
        using IServiceScope scope = _fixture.Services.CreateScope();
        IPortalRepository portals = scope.ServiceProvider.GetRequiredService<IPortalRepository>();

        PagedResult<Portal> page = await portals.ListAsync(10_000, 10, null, null, descending: false);

        page.Items.Should().BeEmpty();
        page.TotalCount.Should().BeGreaterThan(0);
        page.HasNextPage.Should().BeFalse();
    }

    /// <summary>
    /// The name filter matches a fragment anywhere in the value and ignores case, which is what the legacy
    /// tenant grid did.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// This is deliberately different from the account listing, whose filters match a prefix because the
    /// legacy account search appended a single trailing wildcard. The two behaviours are asserted separately
    /// so that neither can be "corrected" into the other.
    /// </remarks>
    [Fact]
    public async Task ListAsync_WithANameFragment_MatchesAnywhereAndIgnoresCase()
    {
        string marker = Suffix();
        int portalId = await CreatePortalAsync(FormattableString.Invariant($"Alpha {marker} Omega"));

        try
        {
            using IServiceScope scope = _fixture.Services.CreateScope();
            IPortalRepository portals = scope.ServiceProvider.GetRequiredService<IPortalRepository>();

            PagedResult<Portal> midString = await portals.ListAsync(0, 0, marker, null, descending: false);
            PagedResult<Portal> upperCased = await portals.ListAsync(0, 0, marker.ToUpperInvariant(), null, descending: false);
            PagedResult<Portal> padded = await portals.ListAsync(0, 0, "   " + marker + "   ", null, descending: false);

            midString.Items.Should().ContainSingle().Which.PortalId.Should().Be(portalId);
            upperCased.Items.Should().ContainSingle().Which.PortalId.Should().Be(portalId);
            padded.Items.Should().ContainSingle().Which.PortalId.Should().Be(portalId);
        }
        finally
        {
            await RemovePortalAsync(portalId);
        }
    }

    /// <summary>A filter that matches nothing produces an empty page whose total is zero.</summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task ListAsync_WithAnUnmatchableFilter_ReturnsAnEmptyPage()
    {
        using IServiceScope scope = _fixture.Services.CreateScope();
        IPortalRepository portals = scope.ServiceProvider.GetRequiredService<IPortalRepository>();

        PagedResult<Portal> page = await portals.ListAsync(0, 10, "no-tenant-bears-this-name-" + Suffix(), null, descending: false);

        page.Items.Should().BeEmpty();
        page.TotalCount.Should().Be(0);
        page.TotalPages.Should().Be(0);
    }

    /// <summary>Descending order is the exact reverse of ascending order.</summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// <para>
    /// Both directions carry the identifier as a tie-break, so the reversal holds even when two tenants share
    /// a name. Without the tie-break the order of equal names would be whatever the query plan produced and
    /// paging would silently repeat or drop rows — which is precisely why the two tenants created below are
    /// given the same name.
    /// </para>
    /// <para>
    /// The assertion compares the two directions against each other rather than against a sort performed in
    /// this process. Ordering happens in the database under its own collation, which is case-insensitive and
    /// treats punctuation differently from an ordinal comparison, so asserting against a local sort would be
    /// asserting the collation rather than the repository.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task ListAsync_SortedDescending_ReversesTheAscendingOrder()
    {
        string sharedName = FormattableString.Invariant($"Sorting Portal {Suffix()}");
        int firstId = await CreatePortalAsync(sharedName);
        int secondId = await CreatePortalAsync(sharedName);

        try
        {
            using IServiceScope scope = _fixture.Services.CreateScope();
            IPortalRepository portals = scope.ServiceProvider.GetRequiredService<IPortalRepository>();

            PagedResult<Portal> ascending = await portals.ListAsync(0, 0, null, "PortalName", descending: false);
            PagedResult<Portal> descending = await portals.ListAsync(0, 0, null, "PortalName", descending: true);

            List<string> ascendingNames = ascending.Items.Select(portal => portal.PortalName).ToList();
            List<string> descendingNames = descending.Items.Select(portal => portal.PortalName).ToList();

            descendingNames.Should().Equal(Enumerable.Reverse(ascendingNames));

            // The two tenants share a name, so their relative order is decided entirely by the identifier
            // tie-break; asserting it here is what proves the tie-break is present in both directions.
            PagedResult<Portal> tied = await portals.ListAsync(0, 0, sharedName, "PortalName", descending: false);
            PagedResult<Portal> tiedReversed = await portals.ListAsync(0, 0, sharedName, "PortalName", descending: true);

            tied.Items.Select(portal => portal.PortalId).Should().Equal(new[] { firstId, secondId });
            tiedReversed.Items.Select(portal => portal.PortalId).Should().Equal(new[] { secondId, firstId });
        }
        finally
        {
            await RemovePortalAsync(firstId);
            await RemovePortalAsync(secondId);
        }
    }

    /// <summary>Sorting by the identifier orders by the identifier.</summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task ListAsync_SortedByIdentifier_UsesTheIdentifierOrder()
    {
        using IServiceScope scope = _fixture.Services.CreateScope();
        IPortalRepository portals = scope.ServiceProvider.GetRequiredService<IPortalRepository>();

        PagedResult<Portal> ascending = await portals.ListAsync(0, 0, null, "portalid", descending: false);

        ascending.Items.Select(portal => portal.PortalId).Should().BeInAscendingOrder();
        ascending.Items[0].PortalId.Should().Be(_fixture.Seed.PortalId, "the negative seed sorts before every later tenant");
    }

    /// <summary>
    /// An unrecognised sort member falls back to the default order instead of failing or reaching the
    /// database as text.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// The sort member arrives from a query string. The repository maps it through a closed set of
    /// expressions, so an unrecognised value can only select the default; it cannot become part of a
    /// statement. The deliberately hostile value below asserts that property rather than merely describing
    /// it.
    /// </remarks>
    [Fact]
    public async Task ListAsync_WithAnUnrecognisedSortMember_FallsBackToTheDefaultOrder()
    {
        using IServiceScope scope = _fixture.Services.CreateScope();
        IPortalRepository portals = scope.ServiceProvider.GetRequiredService<IPortalRepository>();

        PagedResult<Portal> byDefault = await portals.ListAsync(0, 0, null, null, descending: false);
        PagedResult<Portal> hostile = await portals.ListAsync(0, 0, null, "PortalName; DROP TABLE [dbo].[Portals]", descending: false);

        hostile.Items.Select(portal => portal.PortalId)
            .Should().Equal(byDefault.Items.Select(portal => portal.PortalId));

        // The table is still there, which is the point of the previous assertion.
        (await portals.ExistsAsync(_fixture.Seed.PortalId)).Should().BeTrue();
    }

    /// <summary>
    /// The member count reads the membership table and includes members who have not been authorised.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// Membership of a tenant is a row in the membership table rather than a column on the account, so an
    /// account may belong to several tenants at once. Counting accounts instead of memberships would report
    /// the same person once per installation rather than once per tenant. Unauthorised members are included
    /// because the legacy tenant grid counted every registered account against its tenant regardless of
    /// authorisation state.
    /// </remarks>
    [Fact]
    public async Task CountUsersAsync_CountsMembershipsIncludingUnauthorisedOnes()
    {
        int portalId = await CreatePortalAsync(FormattableString.Invariant($"Counting Portal {Suffix()}"));

        try
        {
            using IServiceScope scope = _fixture.Services.CreateScope();
            IPortalRepository portals = scope.ServiceProvider.GetRequiredService<IPortalRepository>();

            (await portals.CountUsersAsync(portalId)).Should().Be(0);

            await AddMembershipAsync(portalId, _fixture.Seed.AdminUserId, authorised: true);
            (await portals.CountUsersAsync(portalId)).Should().Be(1);

            await AddMembershipAsync(portalId, _fixture.Seed.MemberUserId, authorised: false);
            (await portals.CountUsersAsync(portalId)).Should().Be(2, "an unauthorised member is still a member of the tenant");

            // The seeded tenant is counted from the same table, so the two agree by construction.
            int seededMemberships = await _fixture.Database.ScalarAsync<int>(
                "SELECT COUNT(*) FROM [dbo].[UserPortals] WHERE [PortalId] = @portalId",
                new Dictionary<string, object?> { ["portalId"] = _fixture.Seed.PortalId });

            (await portals.CountUsersAsync(_fixture.Seed.PortalId)).Should().Be(seededMemberships);
        }
        finally
        {
            await RemovePortalAsync(portalId);
        }
    }

    /// <summary>The page count excludes pages that are in the recycle bin.</summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// Deletion of a page is the soft delete that backs the legacy recycle bin. A page awaiting emptying is
    /// still a row, so counting rows would report a tenant as using quota it has released.
    /// </remarks>
    [Fact]
    public async Task CountPagesAsync_ExcludesPagesInTheRecycleBin()
    {
        int portalId = await CreatePortalAsync(FormattableString.Invariant($"Page Counting Portal {Suffix()}"));

        try
        {
            using IServiceScope scope = _fixture.Services.CreateScope();
            IPortalRepository portals = scope.ServiceProvider.GetRequiredService<IPortalRepository>();

            (await portals.CountPagesAsync(portalId)).Should().Be(0);

            int liveTabId = await AddTabAsync(portalId, "Live", isDeleted: false);
            (await portals.CountPagesAsync(portalId)).Should().Be(1);

            int binnedTabId = await AddTabAsync(portalId, "Binned", isDeleted: true);
            (await portals.CountPagesAsync(portalId)).Should().Be(1, "a page in the recycle bin does not count against the tenant");

            liveTabId.Should().NotBe(binnedTabId);

            await _fixture.Database.ExecuteAsync(
                "UPDATE [dbo].[Tabs] SET [IsDeleted] = 0 WHERE [TabID] = @tabId",
                new Dictionary<string, object?> { ["tabId"] = binnedTabId });

            (await portals.CountPagesAsync(portalId)).Should().Be(2, "restoring a page returns it to the count");
        }
        finally
        {
            await RemovePortalAsync(portalId);
        }
    }

    /// <summary>The role-name lookup names both of the tenant's assigned roles.</summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task GetRoleNamesAsync_NamesTheAdministratorAndRegisteredRoles()
    {
        using IServiceScope scope = _fixture.Services.CreateScope();
        IPortalRepository portals = scope.ServiceProvider.GetRequiredService<IPortalRepository>();

        IReadOnlyDictionary<int, string> names = await portals.GetRoleNamesAsync(_fixture.Seed.PortalId);

        names.Should().HaveCount(2);
        names[_fixture.Seed.AdministratorRoleId].Should().Be(IntegrationSeed.AdministratorsRoleName);
        names[_fixture.Seed.RegisteredRoleId].Should().Be(IntegrationSeed.RegisteredUsersRoleName);
    }

    /// <summary>An unknown tenant produces an empty map rather than an exception.</summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task GetRoleNamesAsync_ForAnUnknownTenant_ReturnsAnEmptyMap()
    {
        using IServiceScope scope = _fixture.Services.CreateScope();
        IPortalRepository portals = scope.ServiceProvider.GetRequiredService<IPortalRepository>();

        (await portals.GetRoleNamesAsync(UnknownPortalId)).Should().BeEmpty();
    }

    /// <summary>
    /// A tenant whose two assignments point at one role yields one entry, and a tenant with no assignments
    /// yields none.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// The map is keyed by role identifier, so an assignment pair pointing at a single role must collapse
    /// rather than attempt to add the same key twice.
    /// </remarks>
    [Fact]
    public async Task GetRoleNamesAsync_CollapsesADuplicatedAssignmentAndToleratesAnAbsentOne()
    {
        int sharedId = await CreatePortalAsync(
            FormattableString.Invariant($"Shared Role Portal {Suffix()}"),
            portal =>
            {
                portal.AdministratorRoleId = _fixture.Seed.AdministratorRoleId;
                portal.RegisteredRoleId = _fixture.Seed.AdministratorRoleId;
            });

        int unassignedId = await CreatePortalAsync(FormattableString.Invariant($"Unassigned Portal {Suffix()}"));

        try
        {
            using IServiceScope scope = _fixture.Services.CreateScope();
            IPortalRepository portals = scope.ServiceProvider.GetRequiredService<IPortalRepository>();

            IReadOnlyDictionary<int, string> shared = await portals.GetRoleNamesAsync(sharedId);
            shared.Should().HaveCount(1);
            shared[_fixture.Seed.AdministratorRoleId].Should().Be(IntegrationSeed.AdministratorsRoleName);

            (await portals.GetRoleNamesAsync(unassignedId)).Should().BeEmpty();
        }
        finally
        {
            await RemovePortalAsync(sharedId);
            await RemovePortalAsync(unassignedId);
        }
    }

    /// <summary>
    /// An assignment naming a role that no longer exists is omitted, so a caller can tell an unset
    /// assignment from a broken one.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task GetRoleNamesAsync_OmitsAnAssignmentNamingARoleThatIsGone()
    {
        int portalId = await CreatePortalAsync(
            FormattableString.Invariant($"Dangling Role Portal {Suffix()}"),
            portal =>
            {
                portal.AdministratorRoleId = UnknownRoleId;
                portal.RegisteredRoleId = _fixture.Seed.RegisteredRoleId;
            });

        try
        {
            using IServiceScope scope = _fixture.Services.CreateScope();
            IPortalRepository portals = scope.ServiceProvider.GetRequiredService<IPortalRepository>();

            IReadOnlyDictionary<int, string> names = await portals.GetRoleNamesAsync(portalId);

            names.Should().HaveCount(1);
            names.Should().NotContainKey(UnknownRoleId);
            names[_fixture.Seed.RegisteredRoleId].Should().Be(IntegrationSeed.RegisteredUsersRoleName);
        }
        finally
        {
            await RemovePortalAsync(portalId);
        }
    }

    /// <summary>Creates a bare tenant through the repository and returns its identifier.</summary>
    /// <param name="portalName">The name to give the tenant.</param>
    /// <param name="configure">An optional adjustment applied before the tenant is saved.</param>
    /// <returns>The identifier the store assigned.</returns>
    /// <remarks>
    /// The tenant is created through the repository rather than through the tenant endpoint, because the
    /// endpoint also provisions an administrator account and three default roles. Those are correct for a
    /// real tenant and merely noise here, and one of them would make a member count start at one rather than
    /// zero.
    /// </remarks>
    private async Task<int> CreatePortalAsync(string portalName, Action<Portal>? configure = null)
    {
        using IServiceScope scope = _fixture.Services.CreateScope();
        IPortalRepository portals = scope.ServiceProvider.GetRequiredService<IPortalRepository>();
        IUnitOfWork unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

        Portal portal = new()
        {
            PortalName = portalName,
            UserRegistration = UserRegistrationMode.PrivateRegistration,
            BannerAdvertising = BannerAdvertisingMode.None,
            Currency = "USD",
            HostFee = 0m,
            HostSpace = 0,
            PortalGuid = Guid.NewGuid(),
            DefaultLanguage = "en-US",
            TimeZoneOffset = -8,
            HomeDirectory = string.Empty,
            PageQuota = 0,
            UserQuota = 0,
        };

        configure?.Invoke(portal);

        await portals.AddAsync(portal);
        await unitOfWork.SaveChangesAsync();

        return portal.PortalId;
    }

    /// <summary>Removes a tenant created by this suite.</summary>
    /// <param name="portalId">The tenant to remove.</param>
    /// <returns>A task that completes when the tenant is gone.</returns>
    private async Task RemovePortalAsync(int portalId)
    {
        using IServiceScope scope = _fixture.Services.CreateScope();
        IPortalRepository portals = scope.ServiceProvider.GetRequiredService<IPortalRepository>();
        IUnitOfWork unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

        Portal? doomed = await portals.GetByIdAsync(portalId);

        if (doomed is not null)
        {
            await portals.DeleteAsync(doomed.PortalId);
            await unitOfWork.SaveChangesAsync();
        }
    }

    /// <summary>Records a membership row directly, including an unauthorised one.</summary>
    /// <param name="portalId">The tenant to join.</param>
    /// <param name="userId">The account joining it.</param>
    /// <param name="authorised">Whether the membership is authorised.</param>
    /// <returns>A task that completes when the row exists.</returns>
    private async Task AddMembershipAsync(int portalId, int userId, bool authorised)
    {
        int affected = await _fixture.Database.ExecuteAsync(
            """
            INSERT INTO [dbo].[UserPortals] ([UserId], [PortalId], [CreatedDate], [Authorised])
            VALUES (@userId, @portalId, SYSUTCDATETIME(), @authorised);
            """,
            new Dictionary<string, object?>
            {
                ["userId"] = userId,
                ["portalId"] = portalId,
                ["authorised"] = authorised,
            });

        affected.Should().Be(1);
    }

    /// <summary>Records a page directly, optionally already in the recycle bin.</summary>
    /// <param name="portalId">The tenant owning the page.</param>
    /// <param name="tabName">The page name.</param>
    /// <param name="isDeleted">Whether the page is in the recycle bin.</param>
    /// <returns>The identifier the store assigned.</returns>
    private Task<int> AddTabAsync(int portalId, string tabName, bool isDeleted)
    {
        return _fixture.Database.ScalarAsync<int>(
            """
            INSERT INTO [dbo].[Tabs]
                ([TabOrder], [PortalID], [TabName], [IsVisible], [ParentId], [Level], [DisableLink],
                 [Title], [IsDeleted], [TabPath], [IsSecure])
            VALUES (2, @portalId, @tabName, 1, NULL, 0, 0, @tabName, @isDeleted, N'//' + @tabName, 0);
            SELECT CAST(SCOPE_IDENTITY() AS int);
            """,
            new Dictionary<string, object?>
            {
                ["portalId"] = portalId,
                ["tabName"] = tabName,
                ["isDeleted"] = isDeleted,
            });
    }

    /// <summary>Produces a short random suffix so concurrently executing suites cannot collide.</summary>
    /// <returns>A twelve-character suffix.</returns>
    private static string Suffix() => Guid.NewGuid().ToString("N")[..12];
}
