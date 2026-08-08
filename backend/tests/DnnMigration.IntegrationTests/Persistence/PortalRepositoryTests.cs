using DnnMigration.Domain.Abstractions.Repositories;
using DnnMigration.Domain.Common;
using DnnMigration.Domain.Entities;
using DnnMigration.Domain.Enums;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DnnMigration.IntegrationTests.Persistence;

/// <summary>Covers the tenant repository against the existing schema.</summary>
/// <remarks>
/// <para>
/// These assertions run below the HTTP boundary, which is what makes them worth having alongside
/// the endpoint suites. A filter that matched a fragment rather than a prefix, an ordering that
/// omitted its tie-break, or a count that read the wrong table would all still produce a
/// well-formed response, so the endpoint suites cannot distinguish them. The repository contract
/// can.
/// </para>
/// <para>
/// Every result here is the real <see cref="PagedResult{T}"/>, not the envelope the endpoint suites
/// read into. That type has get-only members and a private constructor, so it can be produced but
/// not deserialised; reading it directly is only possible because no serialisation is involved at
/// this level.
/// </para>
/// </remarks>
[Trait("Category", "Integration")]
[Collection(IntegrationTestCollection.Name)]
public sealed class PortalRepositoryTests
{
    private const int UnknownPortalId = 987654;
    private const int UnknownRoleId = 987654;

    // Deliberately far above any identity the suite can reach. A value such as minus one or zero would be
    // useless here: both are legitimate identifiers in this schema, which is the whole point of the sentinel
    // assertions below.
    private const int UnknownTabId = 987654;
    private const int UnknownAliasId = 987654;

    private readonly ApiTestFixture _fixture;

    public PortalRepositoryTests(ApiTestFixture fixture) => _fixture = fixture;

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
    /// The first tenant of an installation carries the identifier minus one, and the repository
    /// treats it as an identifier rather than as an absent value.
    /// </summary>
    /// <remarks>
    /// <c>Portals.PortalID</c> is declared <c>IDENTITY(-1, 1)</c>, and minus one is simultaneously
    /// the value the legacy code used to mean "no integer". Anything that treated the two as
    /// interchangeable would make the first tenant of every installation unreachable, so the
    /// collision is asserted rather than left to reasoning.
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
    /// <remarks>
    /// The opt-in matters because tenant resolution reads aliases on every request while the tenant
    /// listing does not. Loading them unconditionally would add a join to every read of the table
    /// for the benefit of one caller.
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

    [Fact]
    public async Task GetAsync_WithAnUnknownIdentifier_ReturnsNull()
    {
        using IServiceScope scope = _fixture.Services.CreateScope();
        IPortalRepository portals = scope.ServiceProvider.GetRequiredService<IPortalRepository>();

        (await portals.GetByIdAsync(UnknownPortalId)).Should().BeNull();
        (await portals.ExistsAsync(UnknownPortalId)).Should().BeFalse();
    }

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
    /// A page beyond the last one is empty but still reports the true total, so a caller can
    /// recover rather than concluding the collection is empty.
    /// </summary>
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
    /// The name filter matches a fragment anywhere in the value and ignores case, which is what the
    /// legacy tenant grid did.
    /// </summary>
    /// <remarks>
    /// This is deliberately different from the account listing, whose filters match a prefix
    /// because the legacy account search appended a single trailing wildcard. The two behaviours
    /// are asserted separately so that neither can be "corrected" into the other.
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
    /// <remarks>
    /// <para>
    /// Both directions carry the identifier as a tie-break, so the reversal holds even when two
    /// tenants share a name. Without the tie-break the order of equal names would be whatever the
    /// query plan produced and paging would silently repeat or drop rows — which is precisely why
    /// the two tenants created below are given the same name.
    /// </para>
    /// <para>
    /// The assertion compares the two directions against each other rather than against a sort
    /// performed in this process. Ordering happens in the database under its own collation, which
    /// is case-insensitive and treats punctuation differently from an ordinal comparison, so
    /// asserting against a local sort would be asserting the collation rather than the repository.
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
            // tie-break, in both directions.
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
    /// An unrecognised sort member falls back to the default order instead of failing or reaching
    /// the database as text.
    /// </summary>
    /// <remarks>
    /// The sort member arrives from a query string. The repository maps it through a closed set of
    /// expressions, so an unrecognised value can only select the default; it cannot become part of
    /// a statement. The deliberately hostile value below asserts that property rather than merely
    /// describing it.
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

        // The table survived, so the hostile sort field never reached the store as text.
        (await portals.ExistsAsync(_fixture.Seed.PortalId)).Should().BeTrue();
    }

    /// <summary>
    /// The member count reads the membership table and includes members who have not been
    /// authorised.
    /// </summary>
    /// <remarks>
    /// Membership of a tenant is a row in the membership table rather than a column on the account,
    /// so an account may belong to several tenants at once. Counting accounts instead of
    /// memberships would report the same person once per installation rather than once per tenant.
    /// Unauthorised members are included because the legacy tenant grid counted every registered
    /// account against its tenant regardless of authorisation state.
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

    /// <summary>
    /// The page count reproduces the terminal <c>GetTabCount</c>: the administration page and its
    /// direct children are excluded, recycled pages are counted, one is subtracted, and a portal
    /// with no administration page answers minus one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The oracle is the procedure the legacy grid displayed - <c>PortalInfo.Pages</c> resolved
    /// through <c>TabController.GetTabCount</c>, whose terminal definition in
    /// <c>04.04.00.SqlDataProvider</c> is
    /// <c>SELECT COUNT(*) - 1 ... WHERE PortalID = @PortalID AND TabID &lt;&gt; @AdminTabId AND (ParentId &lt;&gt; @AdminTabId OR ParentId IS NULL)</c>.
    /// It states no soft-delete condition, so a recycled page IS counted; the plausible-sounding
    /// opposite rule agrees with the legacy figure on no portal at all.
    /// </para>
    /// <para>
    /// Each stage below isolates one clause, so a regression names itself rather than merely moving
    /// a total: the subtraction, the recycled row, the administration page, its direct child, its
    /// grandchild, and the null-administration-page case.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task CountPagesAsync_ReproducesTheTerminalGetTabCount()
    {
        int portalId = await CreatePortalAsync(FormattableString.Invariant($"Page Counting Portal {Suffix()}"));

        try
        {
            using IServiceScope scope = _fixture.Services.CreateScope();
            IPortalRepository portals = scope.ServiceProvider.GetRequiredService<IPortalRepository>();

            // A portal that records no administration page answers minus one, because the legacy statement
            // compared every row against a null and then subtracted one from a count of nought. It is an
            // arithmetic consequence, NOT the legacy Null.NullInteger sentinel.
            (await portals.CountPagesAsync(portalId)).Should().Be(
                -1,
                "a null AdminTabId made every row's predicate unknown, so COUNT(*) - 1 was 0 - 1");

            int adminTabId = await AddTabAsync(portalId, "Admin", isDeleted: false);
            await SetAdministrationPageAsync(portalId, adminTabId);

            // One page, and it is the administration page, so it is excluded: nought counted, minus one.
            (await portals.CountPagesAsync(portalId)).Should().Be(
                -1,
                "the administration page is excluded from its own portal's tally");

            await AddTabAsync(portalId, "Live", isDeleted: false);
            (await portals.CountPagesAsync(portalId)).Should().Be(
                0,
                "one countable page, minus the constant one the legacy expression subtracts");

            await AddTabAsync(portalId, "Second", isDeleted: false);
            (await portals.CountPagesAsync(portalId)).Should().Be(1);

            // MIGRATION: a page in the recycle bin IS counted. GetTabCount states no IsDeleted condition, so
            // the legacy grid included it, and Rule T5 preserves that rather than improving on it.
            await AddTabAsync(portalId, "Binned", isDeleted: true);
            (await portals.CountPagesAsync(portalId)).Should().Be(
                2,
                "a recycled page still counts, because the legacy predicate never mentioned IsDeleted");

            // A DIRECT child of the administration page is excluded.
            int adminChildId = await AddChildTabAsync(portalId, "Admin Child", adminTabId);
            (await portals.CountPagesAsync(portalId)).Should().Be(
                2,
                "a direct child of the administration page is excluded by the ParentId clause");

            // A GRANDCHILD is NOT excluded: the legacy predicate tests ParentId and nothing deeper. This is
            // the clause an implementation is most likely to "improve" by walking the whole subtree.
            await AddChildTabAsync(portalId, "Admin Grandchild", adminChildId);
            (await portals.CountPagesAsync(portalId)).Should().Be(
                3,
                "only DIRECT children are excluded, so a grandchild of the administration page counts");

            // The page side of the same legacy question must agree exactly.
            ITabRepository tabs = scope.ServiceProvider.GetRequiredService<ITabRepository>();
            (await tabs.CountByPortalIdAsync(portalId)).Should().Be(
                await portals.CountPagesAsync(portalId),
                "TabRepository answers the same procedure and the two must not disagree");

            // An identifier no portal bears answers minus one on the same terms.
            (await portals.CountPagesAsync(UnknownPortalId)).Should().Be(-1);
        }
        finally
        {
            await RemovePortalAsync(portalId);
        }
    }

    /// <summary>
    /// The batched tallies agree with the per-portal members, row for row, and answer for every
    /// identifier they were asked about.
    /// </summary>
    /// <remarks>
    /// The batched members exist so that a listing costs a fixed number of reads rather than two
    /// per row. The risk that introduces is DIVERGENCE - a grouped statement whose predicate drifts
    /// from the single-portal one would report different figures for the same tenant - so the two
    /// are compared against each other here rather than against literals alone. The totality of the
    /// result is asserted as well, because a grouped read naturally omits an identifier with
    /// nothing to count and a caller indexing the map directly would then fail on a tenant that is
    /// merely empty.
    /// </remarks>
    [Fact]
    public async Task BatchedTallies_AgreeWithThePerPortalCountsAndAnswerForEveryIdentifier()
    {
        int populated = await CreatePortalAsync(FormattableString.Invariant($"Batched Populated {Suffix()}"));
        int empty = await CreatePortalAsync(FormattableString.Invariant($"Batched Empty {Suffix()}"));

        try
        {
            await AddMembershipAsync(populated, _fixture.Seed.AdminUserId, authorised: true);
            await AddMembershipAsync(populated, _fixture.Seed.MemberUserId, authorised: false);

            // The populated tenant is given the full shape the legacy predicate discriminates on: an
            // administration page, a direct child of it, a live page and a recycled page. Only the last two
            // count, so COUNT(*) - 1 is one.
            int adminTabId = await AddTabAsync(populated, "Admin", isDeleted: false);
            await SetAdministrationPageAsync(populated, adminTabId);
            await AddChildTabAsync(populated, "Admin Child", adminTabId);
            await AddTabAsync(populated, "Live", isDeleted: false);
            await AddTabAsync(populated, "Binned", isDeleted: true);

            using IServiceScope scope = _fixture.Services.CreateScope();
            IPortalRepository portals = scope.ServiceProvider.GetRequiredService<IPortalRepository>();

            int[] asked = [populated, empty, _fixture.Seed.PortalId];

            IReadOnlyDictionary<int, int> users = await portals.CountUsersForPortalsAsync(asked);
            IReadOnlyDictionary<int, int> pages = await portals.CountPagesForPortalsAsync(asked);

            users.Keys.Should().BeEquivalentTo(asked, "the contract is total over the identifiers supplied");
            pages.Keys.Should().BeEquivalentTo(asked);

            users[populated].Should().Be(2, "an unauthorised member is still a member");
            users[empty].Should().Be(0, "a tenant with no members is present with a zero, not absent");

            // The assertions are against the procedure's own arithmetic rather than only against the sibling
            // member: comparing the two members to each other alone would let both drift away from
            // GetTabCount together, so the literals come first and the agreement check follows.
            pages[populated].Should().Be(
                1,
                "the administration page and its direct child are excluded, the recycled page is counted, "
                + "and one is subtracted");
            pages[empty].Should().Be(
                -1,
                "a tenant that records no administration page is present with minus one, not absent and "
                + "not zero");

            foreach (int portalId in asked)
            {
                users[portalId].Should().Be(
                    await portals.CountUsersAsync(portalId),
                    "the batched member tally must not diverge from the per-portal one");
                pages[portalId].Should().Be(
                    await portals.CountPagesAsync(portalId),
                    "the batched page tally must not diverge from the per-portal one");
            }
        }
        finally
        {
            await RemovePortalAsync(populated);
            await RemovePortalAsync(empty);
        }
    }

    /// <summary>
    /// An empty request is answered with an empty result, and a repeated identifier collapses to
    /// one entry.
    /// </summary>
    /// <remarks>
    /// A listing whose page came back empty must not issue a query at all, and a duplicated
    /// identifier must not make the dictionary construction throw.
    /// </remarks>
    [Fact]
    public async Task BatchedTallies_AnswerAnEmptyRequestAndCollapseDuplicates()
    {
        using IServiceScope scope = _fixture.Services.CreateScope();
        IPortalRepository portals = scope.ServiceProvider.GetRequiredService<IPortalRepository>();

        (await portals.CountUsersForPortalsAsync(Array.Empty<int>())).Should().BeEmpty();
        (await portals.CountPagesForPortalsAsync(Array.Empty<int>())).Should().BeEmpty();

        int seeded = _fixture.Seed.PortalId;
        IReadOnlyDictionary<int, int> users = await portals.CountUsersForPortalsAsync([seeded, seeded, seeded]);
        users.Should().ContainSingle().Which.Key.Should().Be(seeded);
        users[seeded].Should().Be(await portals.CountUsersAsync(seeded));
    }

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

    [Fact]
    public async Task GetRoleNamesAsync_ForAnUnknownTenant_ReturnsAnEmptyMap()
    {
        using IServiceScope scope = _fixture.Services.CreateScope();
        IPortalRepository portals = scope.ServiceProvider.GetRequiredService<IPortalRepository>();

        (await portals.GetRoleNamesAsync(UnknownPortalId)).Should().BeEmpty();
    }

    /// <summary>
    /// A tenant whose two assignments point at one role yields one entry, and a tenant with no
    /// assignments yields none.
    /// </summary>
    /// <remarks>
    /// The map is keyed by role identifier, so an assignment pair pointing at a single role must
    /// collapse rather than attempt to add the same key twice.
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

    /// <summary>
    /// Staging an insertion records an intention and nothing more: the row becomes visible, and the
    /// store-assigned key becomes readable, only once the unit of work commits.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every legacy add procedure ended by reading back the scope identity, so each returned its
    /// generated key and each was therefore durable on its own. The staging member returns a bare
    /// task instead: the key is assigned by the store when the batch is written, which is what
    /// allows a tenant, its aliases, its roles, its pages and its modules to be committed as one
    /// indivisible batch rather than as the five independently durable statement sequences the
    /// legacy tenant creation issued.
    /// </para>
    /// <para>
    /// The boundary is asserted through VISIBILITY rather than through the key value, and that
    /// choice is forced by this schema rather than being a matter of taste. A freshly constructed
    /// entity carries zero in its identity property, but zero is a legitimate portal identifier
    /// here — the identifier column seeds from minus one and steps by one, so the second tenant of
    /// an installation genuinely bears zero. Reading "the key is still zero" as proof of anything
    /// would be reading the sentinel collision this suite exists to document. A separate scope
    /// cannot see an uncommitted row at all, which is unambiguous.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task AddAsync_StagesTheInsertAndSurfacesTheKeyOnlyAfterTheCommit()
    {
        string portalName = FormattableString.Invariant($"Staging Portal {Suffix()}");
        int committedId;

        using (IServiceScope staging = _fixture.Services.CreateScope())
        {
            IPortalRepository portals = staging.ServiceProvider.GetRequiredService<IPortalRepository>();
            IUnitOfWork unitOfWork = staging.ServiceProvider.GetRequiredService<IUnitOfWork>();

            Portal portal = NewPortal(portalName);

            await portals.AddAsync(portal);

            (await CountPortalsNamedAsync(portalName)).Should()
                .Be(0, "staging records an intention against the unit of work and writes nothing");

            int affected = await unitOfWork.SaveChangesAsync();

            affected.Should().BePositive("the commit reports the number of rows it wrote");

            // Only now does the entity carry the key the store assigned. Reading it before this point would
            // read the constructed default rather than an identifier.
            committedId = portal.PortalId;
        }

        try
        {
            (await CountPortalsNamedAsync(portalName)).Should().Be(1, "the commit made the tenant durable");

            using IServiceScope reading = _fixture.Services.CreateScope();
            IPortalRepository portals = reading.ServiceProvider.GetRequiredService<IPortalRepository>();

            Portal? committed = await portals.GetByIdAsync(committedId);

            committed.Should().NotBeNull("the key observed after the commit addresses the committed row");
            committed!.PortalName.Should().Be(portalName);
            (await portals.ExistsAsync(committedId)).Should().BeTrue();
        }
        finally
        {
            await RemovePortalAsync(committedId);
        }
    }

    /// <summary>
    /// The repository answers for minus one, zero and a positive identifier exactly as the table
    /// does, so no member treats a value as absent on account of its sign.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the sentinel collision stated as an executable claim. The identifier column seeds
    /// from minus one, so the first tenant of an installation bears minus one and the second bears
    /// zero; the legacy null helper meanwhile defined its integer sentinel AS minus one and
    /// reported that value as absent, which left it structurally unable to distinguish a real first
    /// tenant from a missing one. The shipped code then passed that same sentinel around as a
    /// genuine tenant argument.
    /// </para>
    /// <para>
    /// The assertion compares the repository against the table for each candidate rather than
    /// against literals, so it holds whatever tenants happen to exist when it runs and cannot decay
    /// into asserting the fixture. A guard clause such as "an identifier below zero means
    /// unspecified" would fail it at minus one; one such as "an identifier of zero means unset"
    /// would fail it at zero.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Lookups_AgreeWithTheTableForNegativeZeroAndPositiveIdentifiers()
    {
        using IServiceScope scope = _fixture.Services.CreateScope();
        IPortalRepository portals = scope.ServiceProvider.GetRequiredService<IPortalRepository>();

        foreach (int candidate in new[] { -1, 0, 1 })
        {
            bool storeHasIt = await CountPortalsWithIdAsync(candidate) == 1;

            (await portals.ExistsAsync(candidate)).Should()
                .Be(storeHasIt, "the existence check must agree with the table for identifier {0}", candidate);

            Portal? found = await portals.GetByIdAsync(candidate);

            (found is not null).Should()
                .Be(storeHasIt, "the lookup must agree with the table for identifier {0}", candidate);

            if (found is not null)
            {
                found.PortalId.Should().Be(candidate, "a lookup returns the tenant that was asked for");
            }
        }

        // Minus one is a positive case rather than a vacuous one: the seeded tenant is the first row of a
        // freshly created database and therefore bears it.
        _fixture.Seed.PortalId.Should().Be(-1);
        (await portals.ExistsAsync(-1)).Should().BeTrue();
    }

    /// <summary>
    /// Staged modifications reach the table only on commit, and then survive a fresh read.
    /// </summary>
    /// <remarks>
    /// One member replaces two legacy procedures that wrote the same row from opposite ends — one
    /// taking twenty-seven positional arguments covering the descriptive and configuration columns,
    /// the other taking nine covering the administrator and the well-known page assignments.
    /// Splitting one row across two positional argument lists made a partial update
    /// indistinguishable from an intentional overwrite with defaults. Here a caller reads a tenant,
    /// changes what it means to change, and stages the result; the columns it did not touch are
    /// asserted to be unchanged, which is the property the legacy split lost.
    /// </remarks>
    [Fact]
    public async Task UpdateAsync_StagesTheChangeAndPersistsItOnCommit()
    {
        int portalId = await CreatePortalAsync(FormattableString.Invariant($"Update Portal {Suffix()}"));

        try
        {
            string renamed = FormattableString.Invariant($"Renamed Portal {Suffix()}");

            using (IServiceScope mutating = _fixture.Services.CreateScope())
            {
                IPortalRepository portals = mutating.ServiceProvider.GetRequiredService<IPortalRepository>();
                IUnitOfWork unitOfWork = mutating.ServiceProvider.GetRequiredService<IUnitOfWork>();

                Portal? portal = await portals.GetByIdAsync(portalId);
                portal.Should().NotBeNull();

                portal!.PortalName = renamed;
                portal.Description = "Rewritten by the update assertion";
                portal.HostFee = 12.34m;
                portal.HostSpace = 256;

                await portals.UpdateAsync(portal);

                (await CountPortalsNamedAsync(renamed)).Should()
                    .Be(0, "staging an update writes nothing until the unit of work commits");

                (await unitOfWork.SaveChangesAsync()).Should().BePositive();
            }

            using IServiceScope reading = _fixture.Services.CreateScope();
            IPortalRepository reader = reading.ServiceProvider.GetRequiredService<IPortalRepository>();

            Portal? persisted = await reader.GetByIdAsync(portalId);

            persisted.Should().NotBeNull();
            persisted!.PortalName.Should().Be(renamed);
            persisted.Description.Should().Be("Rewritten by the update assertion");
            persisted.HostFee.Should().Be(12.34m);
            persisted.HostSpace.Should().Be(256);

            // Untouched columns are untouched: the entity carries its own modified state, so staging it does
            // not overwrite the rest of the row with constructed defaults.
            persisted.Currency.Should().Be("USD");
            persisted.DefaultLanguage.Should().Be("en-US");
            persisted.TimeZoneOffset.Should().Be(-8);
            persisted.PortalId.Should().Be(portalId, "an update does not reassign the identifier");
        }
        finally
        {
            await RemovePortalAsync(portalId);
        }
    }

    /// <summary>
    /// Deleting a tenant removes it and, through the schema's own referential rules, its aliases
    /// with it.
    /// </summary>
    /// <remarks>
    /// The alias foreign key is declared to cascade, so the repository deliberately does not
    /// sequence dependent writes of its own; sequencing them in application code is what let the
    /// legacy deletion path leave orphans behind when it was interrupted part-way. Asserting the
    /// cascade here is asserting that the repository leans on the constraint rather than
    /// reimplementing it.
    /// </remarks>
    [Fact]
    public async Task DeleteAsync_RemovesTheTenantAndCascadesToItsAliases()
    {
        int portalId = await CreatePortalAsync(FormattableString.Invariant($"Doomed Portal {Suffix()}"));
        string alias = NewAlias("doomed");
        int aliasId = await AddAliasAsync(portalId, alias);

        using (IServiceScope deleting = _fixture.Services.CreateScope())
        {
            IPortalRepository portals = deleting.ServiceProvider.GetRequiredService<IPortalRepository>();
            IUnitOfWork unitOfWork = deleting.ServiceProvider.GetRequiredService<IUnitOfWork>();

            await portals.DeleteAsync(portalId);

            (await CountPortalsWithIdAsync(portalId)).Should()
                .Be(1, "staging a deletion removes nothing until the unit of work commits");

            (await unitOfWork.SaveChangesAsync()).Should().BePositive();
        }

        using IServiceScope reading = _fixture.Services.CreateScope();
        IPortalRepository reader = reading.ServiceProvider.GetRequiredService<IPortalRepository>();
        IPortalAliasRepository aliases = reading.ServiceProvider.GetRequiredService<IPortalAliasRepository>();

        (await reader.GetByIdAsync(portalId)).Should().BeNull();
        (await reader.ExistsAsync(portalId)).Should().BeFalse();
        (await aliases.GetByIdAsync(aliasId)).Should().BeNull("the alias foreign key cascades");
        (await aliases.GetByPortalIdAsync(portalId)).Should().BeEmpty();
    }

    /// <summary>
    /// Deleting an identifier no tenant bears succeeds without staging anything, so a caller that
    /// has already established absence need not distinguish the two cases.
    /// </summary>
    [Fact]
    public async Task DeleteAsync_ForAnUnknownIdentifier_StagesNothingAndSucceeds()
    {
        using IServiceScope scope = _fixture.Services.CreateScope();
        IPortalRepository portals = scope.ServiceProvider.GetRequiredService<IPortalRepository>();
        IUnitOfWork unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

        int before = await portals.CountAsync();

        await portals.DeleteAsync(UnknownPortalId);

        (await unitOfWork.SaveChangesAsync()).Should().Be(0, "there was nothing to stage");
        (await portals.CountAsync()).Should().Be(before, "no other tenant was disturbed");
        (await portals.ExistsAsync(_fixture.Seed.PortalId)).Should().BeTrue();
    }

    /// <summary>
    /// The unpaged listing materialises every tenant in a deterministic order, and the standalone
    /// tally agrees with it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This supersedes a controller member that returned an untyped non-generic list built by a
    /// reflection-based row filler. The return type is a materialised read-only list rather than a
    /// deferred sequence, and that distinction is load-bearing: a deferred sequence handed out here
    /// could be enumerated after the scope that produced it had gone, which is exactly the failure
    /// the layering is meant to make impossible.
    /// </para>
    /// <para>
    /// The tally is compared against the listing rather than against a literal. Two members that
    /// count the same table can only be verified against each other; comparing each to a
    /// fixture-derived number would let them drift together undetected.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task GetAllAsync_MaterialisesEveryTenantAndAgreesWithTheTally()
    {
        int firstId = await CreatePortalAsync(FormattableString.Invariant($"Listing Portal B {Suffix()}"));
        int secondId = await CreatePortalAsync(FormattableString.Invariant($"Listing Portal A {Suffix()}"));

        try
        {
            using IServiceScope scope = _fixture.Services.CreateScope();
            IPortalRepository portals = scope.ServiceProvider.GetRequiredService<IPortalRepository>();

            IReadOnlyList<Portal> all = await portals.GetAllAsync();

            all.Should().NotBeEmpty();
            all.Select(portal => portal.PortalId).Should()
                .Contain(_fixture.Seed.PortalId).And.Contain(firstId).And.Contain(secondId);

            (await portals.CountAsync()).Should()
                .Be(all.Count, "the standalone tally and the materialised listing count the same table");

            // The identifier tie-break is what keeps the contract's declared order total when two tenants
            // share a name.
            all.Select(portal => portal.PortalName).Should().BeInAscendingOrder(StringComparer.OrdinalIgnoreCase);
        }
        finally
        {
            await RemovePortalAsync(firstId);
            await RemovePortalAsync(secondId);
        }
    }

    /// <summary>
    /// The tally follows the table as tenants come and go, so it is computed rather than
    /// remembered.
    /// </summary>
    /// <remarks>
    /// The removal is part of what this fact asserts, so it stays in the body where its effect on
    /// the tally is checked; the <c>finally</c> block is a SAFETY NET for the case where the first
    /// tally assertion fails before the removal runs. Without it a failure here would leave an
    /// extra tenant in the installation for the remainder of the run, and the installation-wide
    /// portal count is read by other facts - so one real failure would be followed by unrelated
    /// ones that point at working code. The net asserts nothing, so it can never replace the
    /// failure that brought it here, and it removes the row with a direct statement rather than
    /// through the repository, because on that path the repository is the component under
    /// suspicion.
    /// </remarks>
    [Fact]
    public async Task CountAsync_TracksTheTableAsTenantsAreAddedAndRemoved()
    {
        using IServiceScope scope = _fixture.Services.CreateScope();
        IPortalRepository portals = scope.ServiceProvider.GetRequiredService<IPortalRepository>();

        int before = await portals.CountAsync();
        before.Should().BePositive("the seeded tenant is present");

        int portalId = await CreatePortalAsync(FormattableString.Invariant($"Tally Portal {Suffix()}"));

        try
        {
            (await portals.CountAsync()).Should().Be(before + 1);

            await RemovePortalAsync(portalId);

            (await portals.CountAsync()).Should().Be(before);
        }
        finally
        {
            await EnsurePortalRemovedAsync(portalId);
        }
    }

    /// <summary>
    /// Tenant resolution matches the alias exactly, while tolerating case and surrounding
    /// whitespace, and yields nothing for an alias no tenant claims.
    /// </summary>
    /// <remarks>
    /// Case-insensitivity and trimming are normalisation of the same value, not widening of the
    /// comparison: a host name arrives from a request header, where case is not significant and
    /// stray whitespace is possible. Neither makes the match partial, which is the property the
    /// next assertion pins down.
    /// </remarks>
    [Fact]
    public async Task GetByAliasAsync_MatchesExactlyWhileNormalisingCaseAndWhitespace()
    {
        using IServiceScope scope = _fixture.Services.CreateScope();
        IPortalRepository portals = scope.ServiceProvider.GetRequiredService<IPortalRepository>();

        Portal? resolved = await portals.GetByAliasAsync(ApiTestFixture.TestHost);

        resolved.Should().NotBeNull();
        resolved!.PortalId.Should().Be(_fixture.Seed.PortalId);

        (await portals.GetByAliasAsync(ApiTestFixture.TestHost.ToUpperInvariant()))
            .Should().NotBeNull("a host name is not case-sensitive");
        (await portals.GetByAliasAsync("  " + ApiTestFixture.TestHost + "  "))
            .Should().NotBeNull("surrounding whitespace is normalised away");

        (await portals.GetByAliasAsync(NewAlias("unclaimed"))).Should()
            .BeNull("an alias no tenant claims resolves to nothing rather than to an arbitrary tenant");
        (await portals.GetByAliasAsync("   ")).Should().BeNull("a blank alias identifies no tenant");
    }

    /// <summary>
    /// An alias that is a substring of another tenant's alias does not resolve to that other
    /// tenant.
    /// </summary>
    /// <remarks>
    /// <para>
    /// MIGRATION: this is a deliberate behavioural correction, recorded in the migration notes
    /// rather than applied silently. The legacy tenant-resolution procedure selected the LOWEST
    /// matching identifier using a leading-and-trailing wildcard comparison against the alias
    /// column, so a tenant whose alias happened to be a substring of another's could resolve a
    /// request to the wrong tenant — and because the match was resolved by minimum identifier, the
    /// tenant it mis-resolved to was the older one, deterministically. That procedure was created
    /// in the baseline script, dropped and recreated across seven successive upgrade scripts, and
    /// finally dropped for good in favour of a lookup keyed by identifier.
    /// </para>
    /// <para>
    /// The pairing below is chosen so that one alias is a strict substring of the other, which is
    /// the shape the wildcard comparison could not distinguish. Both directions are asserted:
    /// neither alias may resolve to the other's tenant. The change of behaviour lands in the
    /// request-time alias middleware; what is asserted here is the narrower repository guarantee
    /// that makes it possible, namely that no member of this contract offers a partial-match
    /// parameter at all.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task GetByAliasAsync_DoesNotCrossMatchASubstringOfAnotherTenantsAlias()
    {
        string shortAlias = NewAlias("tenant");
        string longAlias = "sub." + shortAlias;

        int shortPortalId = await CreatePortalAsync(FormattableString.Invariant($"Substring Short {Suffix()}"));
        int longPortalId = await CreatePortalAsync(FormattableString.Invariant($"Substring Long {Suffix()}"));

        try
        {
            await AddAliasAsync(shortPortalId, shortAlias);
            await AddAliasAsync(longPortalId, longAlias);

            using IServiceScope scope = _fixture.Services.CreateScope();
            IPortalRepository portals = scope.ServiceProvider.GetRequiredService<IPortalRepository>();

            longAlias.Should().Contain(shortAlias, "the pairing must exercise the substring case to be meaningful");

            Portal? viaShort = await portals.GetByAliasAsync(shortAlias);
            Portal? viaLong = await portals.GetByAliasAsync(longAlias);

            viaShort.Should().NotBeNull();
            viaShort!.PortalId.Should().Be(shortPortalId, "the shorter alias resolves to its own tenant only");

            viaLong.Should().NotBeNull();
            viaLong!.PortalId.Should().Be(longPortalId, "the longer alias is not captured by the shorter one");

            viaShort.PortalId.Should().NotBe(viaLong.PortalId);
        }
        finally
        {
            await RemovePortalAsync(shortPortalId);
            await RemovePortalAsync(longPortalId);
        }
    }

    /// <summary>
    /// A page resolves a tenant only when the alias and the page belong together, so a page
    /// identifier cannot be used to read across a tenant boundary.
    /// </summary>
    /// <remarks>
    /// Note the argument order: the page identifier is supplied first and the alias second. The
    /// legacy procedure resolved the alias to a tenant and then confirmed through an outer join
    /// that the requested page belonged to it, so both halves of the check are required and the
    /// pairing must resolve as a unit. A version that resolved on the alias alone would hand a
    /// caller another tenant's page; one that resolved on the page alone would ignore the alias
    /// entirely.
    /// </remarks>
    [Fact]
    public async Task GetByTabAsync_ResolvesOnlyWhenThePageBelongsToTheAliasedTenant()
    {
        int otherPortalId = await CreatePortalAsync(FormattableString.Invariant($"Foreign Portal {Suffix()}"));
        string otherAlias = NewAlias("foreign");

        try
        {
            await AddAliasAsync(otherPortalId, otherAlias);
            int foreignTabId = await AddTabAsync(otherPortalId, "Foreign", isDeleted: false);

            using IServiceScope scope = _fixture.Services.CreateScope();
            IPortalRepository portals = scope.ServiceProvider.GetRequiredService<IPortalRepository>();

            Portal? resolved = await portals.GetByTabAsync(_fixture.Seed.RootTabId, ApiTestFixture.TestHost);

            resolved.Should().NotBeNull("the seeded page is reached under the seeded tenant's alias");
            resolved!.PortalId.Should().Be(_fixture.Seed.PortalId);

            (await portals.GetByTabAsync(foreignTabId, ApiTestFixture.TestHost)).Should()
                .BeNull("a page belonging to another tenant does not resolve under this tenant's alias");

            (await portals.GetByTabAsync(_fixture.Seed.RootTabId, otherAlias)).Should()
                .BeNull("the seeded page does not resolve under a foreign alias");

            (await portals.GetByTabAsync(_fixture.Seed.RootTabId, NewAlias("nowhere"))).Should()
                .BeNull("an unknown alias resolves nothing even for a page that exists");

            (await portals.GetByTabAsync(UnknownTabId, ApiTestFixture.TestHost)).Should()
                .BeNull("an unknown page resolves nothing even under a known alias");
        }
        finally
        {
            await RemovePortalAsync(otherPortalId);
        }
    }

    /// <summary>
    /// The ownership check is true only for the tenant that actually owns the page, and a
    /// host-level page that belongs to no tenant is owned by none.
    /// </summary>
    /// <remarks>
    /// This is the tenant-isolation check a caller makes before acting on a page it was merely
    /// handed an identifier for. The host-level case matters because the page table permits a null
    /// tenant — the legacy schema uses that to carry the pages of the installation itself — and a
    /// null tenant must not be read as "belongs to whichever tenant is asking".
    /// </remarks>
    [Fact]
    public async Task TabBelongsToPortalAsync_IsTrueOnlyForTheOwningTenant()
    {
        int otherPortalId = await CreatePortalAsync(FormattableString.Invariant($"Ownership Portal {Suffix()}"));

        try
        {
            int foreignTabId = await AddTabAsync(otherPortalId, "Owned", isDeleted: false);
            int hostTabId = await AddHostTabAsync(FormattableString.Invariant($"Host {Suffix()}"));

            using IServiceScope scope = _fixture.Services.CreateScope();
            IPortalRepository portals = scope.ServiceProvider.GetRequiredService<IPortalRepository>();

            (await portals.TabBelongsToPortalAsync(_fixture.Seed.PortalId, _fixture.Seed.RootTabId))
                .Should().BeTrue();
            (await portals.TabBelongsToPortalAsync(_fixture.Seed.PortalId, _fixture.Seed.ChildTabId))
                .Should().BeTrue();

            (await portals.TabBelongsToPortalAsync(_fixture.Seed.PortalId, foreignTabId))
                .Should().BeFalse("the page belongs to a different tenant");
            (await portals.TabBelongsToPortalAsync(otherPortalId, _fixture.Seed.RootTabId))
                .Should().BeFalse("the pairing is checked in both directions");

            (await portals.TabBelongsToPortalAsync(_fixture.Seed.PortalId, hostTabId))
                .Should().BeFalse("a page with no tenant belongs to no tenant");
            (await portals.TabBelongsToPortalAsync(otherPortalId, hostTabId)).Should().BeFalse();

            (await portals.TabBelongsToPortalAsync(UnknownPortalId, UnknownTabId))
                .Should().BeFalse("neither exists, which is reported as false rather than as an error");
        }
        finally
        {
            await RemovePortalAsync(otherPortalId);
        }
    }


    /// <summary>
    /// Asking for the aliases of the tenant whose identifier is minus one returns that tenant's
    /// aliases and nothing else; asking for every alias is a separate, named member.
    /// </summary>
    /// <remarks>
    /// <para>
    /// MIGRATION: this is the most consequential correction in the alias surface, and it is
    /// recorded in the migration notes. The legacy "get every alias" member was implemented by
    /// calling the per-tenant member with minus one, because the backing procedure carried the
    /// wildcard in its own predicate — it selected rows where the tenant matched the argument OR
    /// the argument was minus one. Minus one therefore meant "ALL TENANTS" inside that one
    /// procedure, which is a THIRD independent meaning for the value, alongside its use as the
    /// legacy integer null sentinel and its use as the seed of the tenant identity column.
    /// </para>
    /// <para>
    /// The three meanings cannot coexist in a typed contract, because the tenant that genuinely
    /// bears minus one is the FIRST tenant of every installation — so preserving the wildcard would
    /// make that tenant's aliases unaskable, and every request for them would silently return the
    /// whole installation's aliases instead. That is a cross-tenant disclosure, not a cosmetic
    /// quirk. The wildcard is therefore deleted rather than translated: the per-tenant member is
    /// strictly per-tenant, and the unpaged member is the only way to ask for everything.
    /// </para>
    /// <para>
    /// The assertion needs both a tenant bearing minus one and at least one other tenant with an
    /// alias of its own, otherwise the two members would agree by accident and the test would pass
    /// against a wildcard-preserving implementation.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task GetByPortalIdAsync_TreatsMinusOneAsATenantIdentifierRatherThanAWildcard()
    {
        _fixture.Seed.PortalId.Should().Be(-1, "the seeded tenant must be the one bearing the wildcard value");

        int otherPortalId = await CreatePortalAsync(FormattableString.Invariant($"Wildcard Portal {Suffix()}"));
        string otherAlias = NewAlias("wildcard");

        try
        {
            int otherAliasId = await AddAliasAsync(otherPortalId, otherAlias);

            otherPortalId.Should().NotBe(-1, "a second tenant is required for the two members to be separable");

            using IServiceScope scope = _fixture.Services.CreateScope();
            IPortalAliasRepository aliases = scope.ServiceProvider.GetRequiredService<IPortalAliasRepository>();

            IReadOnlyList<PortalAlias> forMinusOne = await aliases.GetByPortalIdAsync(-1);
            IReadOnlyList<PortalAlias> everything = await aliases.GetAllAsync();

            forMinusOne.Should().NotBeEmpty("the tenant bearing minus one has an alias of its own");
            forMinusOne.Should().OnlyContain(
                alias => alias.PortalId == -1,
                "minus one selects one tenant, so every row returned must belong to it");
            forMinusOne.Select(alias => alias.HttpAlias).Should().Contain(ApiTestFixture.TestHost);

            forMinusOne.Select(alias => alias.PortalAliasId).Should().NotContain(
                otherAliasId,
                "the eliminated wildcard would have pulled another tenant's alias into this result");
            forMinusOne.Select(alias => alias.HttpAlias).Should().NotContain(otherAlias);

            // The unpaged member is the wildcard, and it is a different answer from the one above.
            everything.Select(alias => alias.PortalAliasId).Should()
                .Contain(otherAliasId).And.Contain(_fixture.Seed.PortalAliasId);
            everything.Count.Should().BeGreaterThan(
                forMinusOne.Count,
                "asking for everything must return strictly more than asking for one tenant");

            // The per-tenant member works the same way for an ordinary identifier, which shows minus one is
            // not being special-cased in the other direction either.
            IReadOnlyList<PortalAlias> forOther = await aliases.GetByPortalIdAsync(otherPortalId);
            forOther.Should().ContainSingle().Which.HttpAlias.Should().Be(otherAlias);

            (await aliases.GetByPortalIdAsync(UnknownPortalId)).Should()
                .BeEmpty("a tenant with no aliases yields an empty list rather than every alias");
        }
        finally
        {
            await RemovePortalAsync(otherPortalId);
        }
    }

    /// <summary>
    /// The alias lookup is scoped to a tenant, unlike the tenant-resolution lookup that takes an
    /// alias alone.
    /// </summary>
    /// <remarks>
    /// The two members are easily confused and mean different things. Resolution answers "which
    /// tenant owns this host name" and is deliberately unscoped, because at request time the tenant
    /// is not yet known. Management answers "does this tenant hold this alias" and is deliberately
    /// scoped, because a caller administering one tenant must not be able to read or edit another
    /// tenant's alias by guessing the string. Both are asserted here so that neither can be
    /// rewritten into the other.
    /// </remarks>
    [Fact]
    public async Task AliasLookup_IsTenantScopedWhileTenantResolutionIsNot()
    {
        int otherPortalId = await CreatePortalAsync(FormattableString.Invariant($"Scoped Portal {Suffix()}"));
        string otherAlias = NewAlias("scoped");

        try
        {
            int otherAliasId = await AddAliasAsync(otherPortalId, otherAlias);

            using IServiceScope scope = _fixture.Services.CreateScope();
            IPortalAliasRepository aliases = scope.ServiceProvider.GetRequiredService<IPortalAliasRepository>();
            IPortalRepository portals = scope.ServiceProvider.GetRequiredService<IPortalRepository>();

            PortalAlias? owned = await aliases.GetByAliasAsync(otherAlias, otherPortalId);
            owned.Should().NotBeNull();
            owned!.PortalAliasId.Should().Be(otherAliasId);
            owned.PortalId.Should().Be(otherPortalId);

            (await aliases.GetByAliasAsync(otherAlias, _fixture.Seed.PortalId)).Should()
                .BeNull("the alias exists, but not under the tenant that was asked about");
            (await aliases.GetByAliasAsync(ApiTestFixture.TestHost, otherPortalId)).Should()
                .BeNull("the seeded alias does not become the other tenant's merely by being asked for");

            // Resolution takes no tenant, because at request time the tenant is what is being determined.
            Portal? resolved = await portals.GetByAliasAsync(otherAlias);
            resolved.Should().NotBeNull();
            resolved!.PortalId.Should().Be(otherPortalId);

            (await aliases.GetByIdAsync(otherAliasId)).Should().NotBeNull();
            (await aliases.GetByIdAsync(UnknownAliasId)).Should()
                .BeNull("an unknown alias identifier yields nothing rather than an error");
        }
        finally
        {
            await RemovePortalAsync(otherPortalId);
        }
    }

    /// <summary>
    /// A whole chain of candidate addresses resolves in one call, every match is returned rather
    /// than collapsed, and an empty request matches nothing.
    /// </summary>
    /// <remarks>
    /// <para>
    /// MIGRATION: the legacy resolution procedure collapsed multiple matches with a
    /// minimum-identifier aggregate, so an installation holding two rows for one address served the
    /// OLDER tenant's content under the other tenant's name, silently and deterministically.
    /// Returning every match instead is what lets a caller refuse an ambiguous address rather than
    /// resolve it, and the difference is asserted here rather than described: two matches arrive
    /// from one call.
    /// </para>
    /// <para>
    /// The candidate chain exists because the legacy product allowed a child tenant to be addressed
    /// by a path segment beneath a shared host, so a stored address may be a host name or a host
    /// name followed by path segments. A request therefore has several possible addresses, most
    /// specific first, and asking one at a time would cost a round trip per path segment.
    /// Preferring the longer match is the caller's decision, not this contract's, which is why both
    /// rows come back.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task GetAllByHttpAliasAsync_ResolvesAWholeCandidateChainAndReturnsEveryMatch()
    {
        int parentPortalId = await CreatePortalAsync(FormattableString.Invariant($"Parent Portal {Suffix()}"));
        int childPortalId = await CreatePortalAsync(FormattableString.Invariant($"Child Portal {Suffix()}"));

        try
        {
            string parentAlias = NewAlias("parent");
            string childAlias = parentAlias + "/child";

            int parentAliasId = await AddAliasAsync(parentPortalId, parentAlias);
            int childAliasId = await AddAliasAsync(childPortalId, childAlias);

            using IServiceScope scope = _fixture.Services.CreateScope();
            IPortalAliasRepository aliases = scope.ServiceProvider.GetRequiredService<IPortalAliasRepository>();

            // Most specific first, exactly as a request-time resolver would compose the chain.
            IReadOnlyList<PortalAlias> matches = await aliases.GetAllByHttpAliasAsync([childAlias, parentAlias]);

            matches.Should().HaveCount(2, "both addresses in the chain are configured, and both are returned");
            matches.Select(alias => alias.PortalAliasId).Should().Equal(
                new[] { parentAliasId, childAliasId }.OrderBy(id => id),
                "the order is stable and keyed on the alias identifier");

            // The owning tenant travels with each match, which is what keeps resolution to one round trip.
            matches.Should().OnlyContain(alias => alias.Portal != null);
            matches.Single(alias => alias.PortalAliasId == childAliasId).Portal.PortalId
                .Should().Be(childPortalId);
            matches.Single(alias => alias.PortalAliasId == parentAliasId).Portal.PortalId
                .Should().Be(parentPortalId);

            // A blank candidate cannot name a host, so it is dropped rather than matched or rejected.
            IReadOnlyList<PortalAlias> withBlanks =
                await aliases.GetAllByHttpAliasAsync([string.Empty, "   ", parentAlias]);
            withBlanks.Should().ContainSingle().Which.PortalAliasId.Should().Be(parentAliasId);

            // Case is not significant, and two candidates differing only in case are one question.
            IReadOnlyList<PortalAlias> cased =
                await aliases.GetAllByHttpAliasAsync([parentAlias.ToUpperInvariant(), parentAlias]);
            cased.Should().ContainSingle().Which.PortalAliasId.Should().Be(parentAliasId);

            (await aliases.GetAllByHttpAliasAsync([])).Should()
                .BeEmpty("an empty request matches nothing and must not read the store");
            (await aliases.GetAllByHttpAliasAsync([NewAlias("absent")])).Should()
                .BeEmpty("an address no tenant claims resolves to nothing");
        }
        finally
        {
            await RemovePortalAsync(parentPortalId);
            await RemovePortalAsync(childPortalId);
        }
    }

    /// <summary>The owning tenant is reachable from an alias identifier alone.</summary>
    /// <remarks>
    /// This is how a caller holding an alias row establishes the tenant it must be authorised
    /// against, without having to trust a tenant identifier supplied alongside it.
    /// </remarks>
    [Fact]
    public async Task GetPortalByAliasIdAsync_ReturnsTheOwningTenant()
    {
        using IServiceScope scope = _fixture.Services.CreateScope();
        IPortalAliasRepository aliases = scope.ServiceProvider.GetRequiredService<IPortalAliasRepository>();

        Portal? owner = await aliases.GetPortalByAliasIdAsync(_fixture.Seed.PortalAliasId);

        owner.Should().NotBeNull();
        owner!.PortalId.Should().Be(_fixture.Seed.PortalId);
        owner.PortalName.Should().Be(IntegrationSeed.PortalName);

        (await aliases.GetPortalByAliasIdAsync(UnknownAliasId)).Should().BeNull();
    }

    /// <summary>
    /// The uniqueness check can exclude the row being edited, so renaming an alias to the value it
    /// already holds is not reported as a collision with itself.
    /// </summary>
    /// <remarks>
    /// The alias column carries a unique index across the whole installation, so a rename that
    /// collided would fail at the store with a constraint violation rather than as a validation
    /// message. The exclusion parameter is what lets the caller distinguish "this value is taken by
    /// someone else" from "this value is taken by the very row I am editing" before it reaches the
    /// store.
    /// </remarks>
    [Fact]
    public async Task AliasExistsAsync_ExcludesTheRowUnderEditFromItsOwnCollisionCheck()
    {
        int portalId = await CreatePortalAsync(FormattableString.Invariant($"Uniqueness Portal {Suffix()}"));
        string alias = NewAlias("unique");

        try
        {
            int aliasId = await AddAliasAsync(portalId, alias);

            using IServiceScope scope = _fixture.Services.CreateScope();
            IPortalAliasRepository aliases = scope.ServiceProvider.GetRequiredService<IPortalAliasRepository>();

            (await aliases.AliasExistsAsync(alias, excludingPortalAliasId: null)).Should()
                .BeTrue("the alias is held by someone");

            (await aliases.AliasExistsAsync(alias, excludingPortalAliasId: aliasId)).Should()
                .BeFalse("the only holder is the row being edited, so this is not a collision");

            (await aliases.AliasExistsAsync(alias, excludingPortalAliasId: UnknownAliasId)).Should()
                .BeTrue("excluding an unrelated row leaves the real holder in view");

            (await aliases.AliasExistsAsync(NewAlias("nobody"), excludingPortalAliasId: null)).Should()
                .BeFalse("an unheld alias is available");
        }
        finally
        {
            await RemovePortalAsync(portalId);
        }
    }

    /// <summary>
    /// An alias stages, commits, updates and deletes through the same unit-of-work boundary as its
    /// tenant.
    /// </summary>
    /// <remarks>
    /// MIGRATION: the legacy controller exposed two members whose names invited exactly the wrong
    /// choice. The one that reads as the ordinary update in fact executed an install-time procedure
    /// that rewrote whichever row still held the placeholder alias, and its only caller was the
    /// fresh-install branch; the real update keyed on both the tenant and the alias identifier.
    /// Only the latter behaviour is carried forward, since the installer is out of scope, and it is
    /// asserted here by rewriting one specific row and checking that no other row moved.
    /// </remarks>
    [Fact]
    public async Task AliasWrites_StageAndCommitThroughTheUnitOfWork()
    {
        int portalId = await CreatePortalAsync(FormattableString.Invariant($"Alias Writes Portal {Suffix()}"));
        string original = NewAlias("original");
        string renamed = NewAlias("renamed");

        try
        {
            int aliasId;

            using (IServiceScope staging = _fixture.Services.CreateScope())
            {
                IPortalAliasRepository aliases =
                    staging.ServiceProvider.GetRequiredService<IPortalAliasRepository>();
                IUnitOfWork unitOfWork = staging.ServiceProvider.GetRequiredService<IUnitOfWork>();

                PortalAlias alias = new() { PortalId = portalId, HttpAlias = original };

                await aliases.AddAsync(alias);

                (await CountAliasesWithHostAsync(original)).Should()
                    .Be(0, "staging an alias writes nothing until the unit of work commits");

                (await unitOfWork.SaveChangesAsync()).Should().BePositive();

                aliasId = alias.PortalAliasId;
            }

            (await CountAliasesWithHostAsync(original)).Should().Be(1);

            using (IServiceScope updating = _fixture.Services.CreateScope())
            {
                IPortalAliasRepository aliases =
                    updating.ServiceProvider.GetRequiredService<IPortalAliasRepository>();
                IUnitOfWork unitOfWork = updating.ServiceProvider.GetRequiredService<IUnitOfWork>();

                PortalAlias? alias = await aliases.GetByIdAsync(aliasId);
                alias.Should().NotBeNull();

                alias!.HttpAlias = renamed;
                await aliases.UpdateAsync(alias);
                (await unitOfWork.SaveChangesAsync()).Should().BePositive();
            }

            using (IServiceScope reading = _fixture.Services.CreateScope())
            {
                IPortalAliasRepository aliases =
                    reading.ServiceProvider.GetRequiredService<IPortalAliasRepository>();

                PortalAlias? persisted = await aliases.GetByIdAsync(aliasId);

                persisted.Should().NotBeNull();
                persisted!.HttpAlias.Should().Be(renamed);
                persisted.PortalId.Should().Be(portalId, "a rename does not move the alias between tenants");

                (await CountAliasesWithHostAsync(original)).Should().Be(0, "the old value is gone");
                (await aliases.GetByPortalIdAsync(portalId)).Should().ContainSingle();

                // The seeded tenant's alias is untouched: the update keyed on one row, not on a pattern.
                (await aliases.GetByAliasAsync(ApiTestFixture.TestHost, _fixture.Seed.PortalId)).Should()
                    .NotBeNull("no other tenant's alias was rewritten");
            }

            using (IServiceScope deleting = _fixture.Services.CreateScope())
            {
                IPortalAliasRepository aliases =
                    deleting.ServiceProvider.GetRequiredService<IPortalAliasRepository>();
                IUnitOfWork unitOfWork = deleting.ServiceProvider.GetRequiredService<IUnitOfWork>();

                await aliases.DeleteAsync(aliasId);
                (await unitOfWork.SaveChangesAsync()).Should().BePositive();
            }

            using IServiceScope verifying = _fixture.Services.CreateScope();
            IPortalAliasRepository verifier =
                verifying.ServiceProvider.GetRequiredService<IPortalAliasRepository>();

            (await verifier.GetByIdAsync(aliasId)).Should().BeNull();
            (await verifier.GetByPortalIdAsync(portalId)).Should().BeEmpty();
            (await CountAliasesWithHostAsync(renamed)).Should().Be(0);
        }
        finally
        {
            await RemovePortalAsync(portalId);
        }
    }

    /// <summary>
    /// A value written as the empty string reads back as the empty string, and a value never
    /// written reads back as absent; the two are not conflated in either direction.
    /// </summary>
    /// <remarks>
    /// <para>
    /// MIGRATION: the legacy null helper defined its string sentinel as the EMPTY STRING rather
    /// than as a null reference, and translated every null column into it on the way out. A caller
    /// therefore could not tell a column holding no value from one holding a zero-length value, and
    /// the distinction was lost for the whole installation. The domain model expresses absence as a
    /// null reference instead, so the two become distinguishable — which is only useful if the
    /// mapping does not quietly coerce one into the other.
    /// </para>
    /// <para>
    /// The columns chosen for the round trip both PERMIT a null, which is what makes the assertion
    /// meaningful: a coercion in either direction would be observable. A column declared not-null
    /// could not distinguish the cases at all.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task StringAndNumericBoundaries_RoundTripWithoutCoercion()
    {
        int portalId = await CreatePortalAsync(
            FormattableString.Invariant($"Boundary Portal {Suffix()}"),
            portal =>
            {
                portal.FooterText = string.Empty;
                portal.Description = null;

                // MIGRATION: the fee is a fixed-point money column and the storage allowance is an integer
                // column in the terminal schema, and the entity declares them that way. The baseline install
                // script did declare the fee as a short string, but the table is altered across seventeen
                // later scripts and the string form does not survive them, so the baseline is not the shape
                // to map against. Asserting the entity's declared types here is what pins that down; the
                // divergence the folder brief anticipated for the storage allowance is NOT present, and its
                // absence is reported rather than annotated as though it existed.
                portal.HostFee = 42.5m;
                portal.HostSpace = 1024;
            });

        try
        {
            using IServiceScope scope = _fixture.Services.CreateScope();
            IPortalRepository portals = scope.ServiceProvider.GetRequiredService<IPortalRepository>();

            Portal? persisted = await portals.GetByIdAsync(portalId);

            persisted.Should().NotBeNull();

            persisted!.FooterText.Should().NotBeNull("an empty string must not be read back as an absent value");
            persisted.FooterText.Should().BeEmpty();

            persisted.Description.Should().BeNull("an absent value must not be read back as an empty string");

            persisted.HomeDirectory.Should().NotBeNull();
            persisted.HomeDirectory.Should().BeEmpty();

            persisted.HostFee.Should().Be(42.5m, "the fee keeps its fractional part, so it is not an integer");
            persisted.HostSpace.Should().Be(1024);

            // The optional identifier columns are absent rather than zero, which is the other half of the
            // same point: zero is a real identifier in this schema and cannot stand in for "unset".
            persisted.HomeTabId.Should().BeNull();
            persisted.SplashTabId.Should().BeNull();
            persisted.ExpiryDate.Should().BeNull();
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
    /// The tenant is created through the repository rather than through the tenant endpoint,
    /// because the endpoint also provisions an administrator account and three default roles. Those
    /// are correct for a real tenant and merely noise here, and one of them would make a member
    /// count start at one rather than zero.
    /// </remarks>
    private async Task<int> CreatePortalAsync(string portalName, Action<Portal>? configure = null)
    {
        using IServiceScope scope = _fixture.Services.CreateScope();
        IPortalRepository portals = scope.ServiceProvider.GetRequiredService<IPortalRepository>();
        IUnitOfWork unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

        Portal portal = NewPortal(portalName);

        configure?.Invoke(portal);

        await portals.AddAsync(portal);
        await unitOfWork.SaveChangesAsync();

        return portal.PortalId;
    }

    /// <summary>Ensures a tenant created by this suite is gone, whatever else happened.</summary>
    /// <param name="portalId">The tenant to remove.</param>
    /// <returns>A task that completes when no such row remains.</returns>
    /// <remarks>
    /// A direct statement rather than the repository, deliberately: this runs on the failure path,
    /// where the repository may be the very thing that is broken, and a cleanup that depends on the
    /// component under test cannot be relied on to clean up. The row was written bare by this suite
    /// so it has no dependents, and the statement is unconditional so calling it after a successful
    /// removal costs nothing.
    /// </remarks>
    private Task EnsurePortalRemovedAsync(int portalId) => _fixture.Database.ExecuteAsync(
        "DELETE FROM [dbo].[Portals] WHERE [PortalID] = @portalId",
        new Dictionary<string, object?> { ["portalId"] = portalId });

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

    /// <summary>Records a page beneath a named parent page.</summary>
    /// <param name="portalId">The tenant owning the page.</param>
    /// <param name="tabName">The page name.</param>
    /// <param name="parentTabId">The parent page.</param>
    /// <returns>The identifier the store assigned.</returns>
    /// <remarks>
    /// The legacy page tally excludes DIRECT children of the administration page and nothing
    /// deeper, so a parented page is required to exercise that clause and a grandchild is required
    /// to prove the clause stops there.
    /// </remarks>
    private Task<int> AddChildTabAsync(int portalId, string tabName, int parentTabId)
    {
        return _fixture.Database.ScalarAsync<int>(
            """
            INSERT INTO [dbo].[Tabs]
                ([TabOrder], [PortalID], [TabName], [IsVisible], [ParentId], [Level], [DisableLink],
                 [Title], [IsDeleted], [TabPath], [IsSecure])
            VALUES (2, @portalId, @tabName, 1, @parentTabId, 1, 0, @tabName, 0, N'//' + @tabName, 0);
            SELECT CAST(SCOPE_IDENTITY() AS int);
            """,
            new Dictionary<string, object?>
            {
                ["portalId"] = portalId,
                ["tabName"] = tabName,
                ["parentTabId"] = parentTabId,
            });
    }

    /// <summary>Points a tenant's administration-page column at an existing page.</summary>
    /// <param name="portalId">The tenant to amend.</param>
    /// <param name="adminTabId">The page to designate.</param>
    /// <returns>A task that completes when the column has been set.</returns>
    /// <remarks>
    /// Written directly rather than through the repository because the designation is a
    /// precondition of the assertions rather than one of them, and because the tenant-creation
    /// helper deliberately leaves the column null so the no-administration-page case is reachable.
    /// </remarks>
    private async Task SetAdministrationPageAsync(int portalId, int adminTabId)
    {
        int affected = await _fixture.Database.ExecuteAsync(
            "UPDATE [dbo].[Portals] SET [AdminTabId] = @adminTabId WHERE [PortalID] = @portalId;",
            new Dictionary<string, object?>
            {
                ["adminTabId"] = adminTabId,
                ["portalId"] = portalId,
            });

        affected.Should().Be(1);
    }

    /// <summary>Records a page belonging to the installation rather than to any tenant.</summary>
    /// <param name="tabName">The page name.</param>
    /// <returns>The identifier the store assigned.</returns>
    /// <remarks>
    /// The page table declares its tenant column as nullable, which is how the legacy schema
    /// carries the pages of the installation itself. Such a page belongs to no tenant, so it is the
    /// correct negative case for the ownership check and cannot be produced through the
    /// tenant-scoped helper.
    /// </remarks>
    private Task<int> AddHostTabAsync(string tabName)
    {
        return _fixture.Database.ScalarAsync<int>(
            """
            INSERT INTO [dbo].[Tabs]
                ([TabOrder], [PortalID], [TabName], [IsVisible], [ParentId], [Level], [DisableLink],
                 [Title], [IsDeleted], [TabPath], [IsSecure])
            VALUES (2, NULL, @tabName, 1, NULL, 0, 0, @tabName, 0, N'//' + @tabName, 0);
            SELECT CAST(SCOPE_IDENTITY() AS int);
            """,
            new Dictionary<string, object?> { ["tabName"] = tabName });
    }

    /// <summary>
    /// Attaches an alias to a tenant through the repository and returns its identifier.
    /// </summary>
    /// <param name="portalId">The tenant to attach the alias to.</param>
    /// <param name="httpAlias">The host alias to record.</param>
    /// <returns>The identifier the store assigned.</returns>
    /// <remarks>
    /// The alias is written through the repository rather than through a direct statement, so the
    /// helper exercises the same staging boundary the assertions describe: the key is read only
    /// once the unit of work has committed.
    /// </remarks>
    private async Task<int> AddAliasAsync(int portalId, string httpAlias)
    {
        using IServiceScope scope = _fixture.Services.CreateScope();
        IPortalAliasRepository aliases = scope.ServiceProvider.GetRequiredService<IPortalAliasRepository>();
        IUnitOfWork unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

        PortalAlias alias = new() { PortalId = portalId, HttpAlias = httpAlias };

        await aliases.AddAsync(alias);
        await unitOfWork.SaveChangesAsync();

        return alias.PortalAliasId;
    }

    /// <summary>Counts the tenants bearing a name, reading the table directly.</summary>
    /// <param name="portalName">The name to count.</param>
    /// <returns>The number of matching rows.</returns>
    /// <remarks>
    /// Read through a connection of its own so that it cannot observe work merely staged against a
    /// repository's session. That independence is what makes it usable as the visibility probe for
    /// the staging boundary.
    /// </remarks>
    private Task<int> CountPortalsNamedAsync(string portalName)
    {
        return _fixture.Database.ScalarAsync<int>(
            "SELECT COUNT(*) FROM [dbo].[Portals] WHERE [PortalName] = @portalName;",
            new Dictionary<string, object?> { ["portalName"] = portalName });
    }

    /// <summary>Counts the tenants bearing an identifier, reading the table directly.</summary>
    /// <param name="portalId">The identifier to count.</param>
    /// <returns>One when the tenant exists; otherwise zero.</returns>
    private Task<int> CountPortalsWithIdAsync(int portalId)
    {
        return _fixture.Database.ScalarAsync<int>(
            "SELECT COUNT(*) FROM [dbo].[Portals] WHERE [PortalID] = @portalId;",
            new Dictionary<string, object?> { ["portalId"] = portalId });
    }

    /// <summary>Counts the alias rows holding a host value, reading the table directly.</summary>
    /// <param name="httpAlias">The host value to count.</param>
    /// <returns>The number of matching rows.</returns>
    private Task<int> CountAliasesWithHostAsync(string httpAlias)
    {
        return _fixture.Database.ScalarAsync<int>(
            "SELECT COUNT(*) FROM [dbo].[PortalAlias] WHERE [HTTPAlias] = @httpAlias;",
            new Dictionary<string, object?> { ["httpAlias"] = httpAlias });
    }

    /// <summary>
    /// Builds a valid unsaved tenant carrying only the columns the schema insists upon.
    /// </summary>
    /// <param name="portalName">The name to give the tenant.</param>
    /// <returns>An unsaved tenant whose identifier the store has not yet assigned.</returns>
    /// <remarks>
    /// Every property set here backs a column declared not-null with no useful default, so omitting
    /// any of them would fail at the store for a reason unrelated to whatever is under test.
    /// Nothing else is set, so a tenant built here starts with its optional columns genuinely
    /// absent.
    /// </remarks>
    private static Portal NewPortal(string portalName) => new()
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

    /// <summary>Produces a host alias no other row can hold.</summary>
    /// <param name="label">A readable label identifying the assertion that asked for it.</param>
    /// <returns>A lower-case host alias unique across the installation.</returns>
    /// <remarks>
    /// The alias column carries a unique index spanning the whole installation, so a reused value
    /// would fail at the store rather than at an assertion. The result is already lower-case, so it
    /// is unaffected by the case normalisation tenant resolution applies.
    /// </remarks>
    private static string NewAlias(string label) =>
        FormattableString.Invariant($"{label}-{Suffix()}.integration.test");

    /// <summary>Produces a short random suffix so concurrently executing suites cannot collide.</summary>
    /// <returns>A twelve-character suffix.</returns>
    private static string Suffix() => Guid.NewGuid().ToString("N")[..12];
}
