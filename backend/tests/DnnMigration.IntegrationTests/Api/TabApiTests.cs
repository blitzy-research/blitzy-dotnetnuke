using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using DnnMigration.Application.Dtos.Tab;
using FluentAssertions;
using Xunit;

namespace DnnMigration.IntegrationTests.Api;

/// <summary>
/// Covers the page resource end to end: the per-tenant navigation listing, the single-page read and the
/// single-page write, together with the permission gates guarding the latter two.
/// </summary>
/// <remarks>
/// <para>
/// Pages carry two properties that are easy to lose in a migration and are therefore pinned here rather than
/// assumed. The listing is returned in navigation order and must not be re-sorted, because a child's position
/// is meaningful only relative to the parent that precedes it. And a write does not store the submitted
/// ordinal: it uses it to place the page among its siblings and then renumbers the whole tenant's tree,
/// recomputing every depth and path. Both halves are asserted, since storing the submitted ordinal would look
/// correct on one page and corrupt the hierarchy across several.
/// </para>
/// <para>
/// The two gates differ deliberately: the read policy states only the permission, while the write policy
/// additionally requires an authenticated caller. That difference is observable only for a caller who is
/// authenticated, because an unauthenticated caller is challenged either way - the framework converts a
/// failed authorisation into a challenge rather than a refusal whenever authentication itself did not
/// succeed. Both halves are asserted so the distinction is not mistaken for one about anonymous callers.
/// A page the gate cannot resolve is refused rather than reported missing, even for the host account,
/// because the same route reaches pages in every tenant and a 404-versus-403 split would be an existence
/// oracle.
/// </para>
/// <para>
/// Read assertions are made against the seeded pages; every mutation operates on a page created for that test
/// alone. That separation matters because a write renumbers the entire tenant's tree, so a test that asserted
/// a seeded page's exact ordinal would be broken by any other test's write. No absolute ordinal is asserted
/// anywhere - only relative order, which is the actual contract.
/// </para>
/// </remarks>
[Trait("Category", "Integration")]
[Collection(IntegrationTestCollection.Name)]
public sealed class TabApiTests
{
    /// <summary>A page identifier no seeded or created page can hold.</summary>
    private const int UnknownTabId = 987654;

    /// <summary>A tenant identifier no seeded or created portal can hold.</summary>
    private const int UnknownPortalId = 987654;

    /// <summary>
    /// The "All Users" pseudo-role, whose grants reach every caller, authenticated or not.
    /// </summary>
    /// <remarks>
    /// MIGRATION: <c>glbRoleAllUsers = "-1"</c> at <c>Library/Components/Shared/Globals.vb:L95</c>. The value
    /// collides with the seed of the portal identity column, so it is a role sentinel in this column only.
    /// </remarks>
    private const int AllUsersRoleId = -1;

    /// <summary>
    /// The "Unauthenticated Users" pseudo-role, whose grants reach exactly the callers with no account.
    /// </summary>
    private const int UnauthenticatedRoleId = -3;

    private readonly ApiTestFixture _fixture;

    /// <summary>Initialises a new instance of the <see cref="TabApiTests"/> class.</summary>
    /// <param name="fixture">The shared host, database and seed.</param>
    public TabApiTests(ApiTestFixture fixture) => _fixture = fixture;

    /// <summary>
    /// The tenant's pages are listed in navigation order, with the seeded parent and its child both present
    /// and correctly related.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task ListTabs_ReturnsOkWithTheSeededHierarchyInNavigationOrder()
    {
        using HttpClient client = _fixture.CreateAdministratorClient();

        using HttpResponseMessage response = await client.GetAsync(TabsRoute(_fixture.Seed.PortalId));

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        List<TabListItemDto> tabs = await ReadListAsync(response);

        tabs.Should().HaveCountGreaterThanOrEqualTo(2);

        TabListItemDto? root = tabs.SingleOrDefault(tab => tab.TabId == _fixture.Seed.RootTabId);
        TabListItemDto? child = tabs.SingleOrDefault(tab => tab.TabId == _fixture.Seed.ChildTabId);

        root.Should().NotBeNull();
        child.Should().NotBeNull();

        root!.TabName.Should().Be(IntegrationSeed.RootTabName);
        root.ParentId.Should().BeNull();
        root.Level.Should().Be(0);
        root.HasChildren.Should().BeTrue();
        root.IsDeleted.Should().BeFalse();

        child!.TabName.Should().Be(IntegrationSeed.ChildTabName);
        child.ParentId.Should().Be(_fixture.Seed.RootTabId);
        child.Level.Should().Be(1);
        child.TabPath.Should().Be("//Home//Reports");

        // The order the service returns is the navigation order and is not re-sorted by the controller.
        tabs.Select(tab => tab.TabOrder).Should().BeInAscendingOrder();
    }

    /// <summary>
    /// A page in the recycle bin is still listed, carrying its own deletion flag, because the terminal
    /// legacy read returns soft-deleted rows and projects that flag for the caller to act on.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// This pins the answer to a question the listing cannot answer twice: a caller that receives a
    /// filtered listing cannot tell a tenant with no recycled pages from a tenant whose recycled pages
    /// were withheld from it. The page is recycled through the write endpoint rather than by a direct
    /// insert so that the cached navigation is invalidated the way production invalidates it -
    /// recycling a page behind the service's back would leave the assertion reading a stale entry and
    /// passing for the wrong reason. It is restored the same way once asserted, which leaves the shared
    /// tenant in the state every other page this suite creates leaves it in.
    /// </remarks>
    [Fact]
    public async Task ListTabs_IncludesAPageInTheRecycleBin()
    {
        string name = "IRecycled" + Suffix();
        int tabId = await CreateTabAsync(name);

        using HttpClient client = _fixture.CreateHostClient();

        UpdateTabRequest recycled = NewUpdateRequest(name);
        recycled.IsDeleted = true;

        using HttpResponseMessage written = await client.PutAsJsonAsync(
            TabRoute(tabId),
            recycled,
            ApiTestFixture.Json);

        written.StatusCode.Should().Be(HttpStatusCode.OK);

        using HttpResponseMessage response = await client.GetAsync(TabsRoute(_fixture.Seed.PortalId));

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        List<TabListItemDto> tabs = await ReadListAsync(response);

        TabListItemDto? listed = tabs.SingleOrDefault(tab => tab.TabId == tabId);

        listed.Should().NotBeNull("a recycled page is still a row and the listing is complete by contract");
        listed!.IsDeleted.Should().BeTrue("the flag travels on the row so the caller owns the filtering");
        listed.TabName.Should().Be(name);

        using HttpResponseMessage restored = await client.PutAsJsonAsync(
            TabRoute(tabId),
            NewUpdateRequest(name),
            ApiTestFixture.Json);

        restored.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    /// <summary>The listing requires credentials.</summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task ListTabs_WithoutCredentials_ReturnsUnauthorized()
    {
        using HttpClient client = _fixture.CreateAnonymousClient();

        using HttpResponseMessage response = await client.GetAsync(TabsRoute(_fixture.Seed.PortalId));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    /// <summary>
    /// The listing is tenant-bound administration, so an ordinary member of the tenant holding a perfectly
    /// valid token is refused.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// <para>
    /// This route previously required only that the caller be authenticated, so any member of any tenant could
    /// read any other tenant's entire page hierarchy — its navigation structure, including pages in the recycle
    /// bin and pages an ordinary visitor is not permitted to see — simply by changing the tenant segment.
    /// </para>
    /// <para>
    /// It cannot be guarded by the page view policy, because that policy resolves its scope from a page
    /// identifier in the route and this route names no page; the tenant it DOES name is what binds it. The
    /// per-page routes remain permission-gated, which the tests further down measure.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task ListTabs_AsAnOrdinaryMember_ReturnsForbidden()
    {
        using HttpClient member = _fixture.CreateClientFor(
            _fixture.Seed.MemberUserId,
            IntegrationSeed.MemberUserName,
            _fixture.Seed.PortalId,
            isSuperUser: false,
            roles: [IntegrationSeed.RegisteredUsersRoleName]);

        using HttpResponseMessage response = await member.GetAsync(TabsRoute(_fixture.Seed.PortalId));

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    /// <summary>An unknown tenant answers <c>404 Not Found</c>, because the route names the tenant itself.</summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task ListTabs_ForAnUnknownTenant_ReturnsNotFound()
    {
        using HttpClient client = _fixture.CreateHostClient();

        using HttpResponseMessage response = await client.GetAsync(TabsRoute(UnknownPortalId));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);

        string body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("urn:dnnmigration:error:tab.portal_not_found");
    }

    /// <summary>
    /// A newly created tenant holds exactly its home page, at the root of its navigation.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// C-02: this assertion previously read <c>BeEmpty</c>, on the stated grounds that "creating a portal
    /// deliberately does not provision a page tree". That premise was the defect rather than the design - a
    /// tenant with no page has nowhere to serve, and the legacy creation sequence provisioned a home page and
    /// stamped the portal with it. The test is corrected to the behaviour the legacy application had, because
    /// a test that encodes an incorrect implementation must follow the implementation being fixed rather than
    /// hold it in place. A page TREE is still not provisioned: the pages beyond the home page came from the
    /// XML portal template, whose subsystem AAP 0.2.2.2 places out of scope, so exactly one page is expected
    /// and that exactness is what this test pins.
    /// </remarks>
    [Fact]
    public async Task ListTabs_ForANewTenant_ReturnsOnlyItsHomePage()
    {
        using HttpClient client = _fixture.CreateHostClient();
        int isolatedPortalId = await CreateIsolatedPortalAsync(client);

        using HttpResponseMessage response = await client.GetAsync(TabsRoute(isolatedPortalId));

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        List<TabListItemDto> tabs = await ReadListAsync(response);

        // The route names the tenant, so a row appearing here is by construction one of that tenant's pages;
        // the list-item projection therefore carries no tenant identifier of its own.
        TabListItemDto homePage = tabs.Should().ContainSingle().Subject;
        homePage.TabName.Should().Be("Home");
        homePage.ParentId.Should().BeNull();
        homePage.Level.Should().Be(0);
        homePage.IsDeleted.Should().BeFalse();
    }

    /// <summary>The host reads a page it holds no explicit grant on, because a host account holds everything.</summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task GetTab_AsHost_ReturnsOkWithDetail()
    {
        using HttpClient client = _fixture.CreateHostClient();

        using HttpResponseMessage response = await client.GetAsync(TabRoute(_fixture.Seed.ChildTabId));

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        TabDetailDto detail = await ReadDetailAsync(response);

        detail.TabId.Should().Be(_fixture.Seed.ChildTabId);
        detail.TabName.Should().Be(IntegrationSeed.ChildTabName);
        detail.PortalId.Should().Be(_fixture.Seed.PortalId);
        detail.ParentId.Should().Be(_fixture.Seed.RootTabId);
        detail.Level.Should().Be(1);
        detail.TabPath.Should().Be("//Home//Reports");
        detail.IsVisible.Should().BeTrue();
        detail.IsDeleted.Should().BeFalse();
        detail.HasChildren.Should().BeFalse();
    }

    /// <summary>The seeded parent reports that it has children.</summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task GetTab_ForAParentPage_ReportsThatItHasChildren()
    {
        using HttpClient client = _fixture.CreateHostClient();

        using HttpResponseMessage response = await client.GetAsync(TabRoute(_fixture.Seed.RootTabId));

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        TabDetailDto detail = await ReadDetailAsync(response);

        detail.HasChildren.Should().BeTrue();
        detail.ParentId.Should().BeNull();
        detail.Level.Should().Be(0);
    }

    /// <summary>An anonymous read is challenged.</summary>
    /// <remarks>
    /// The read policy states only the permission and does not require an authenticated caller, so the
    /// requirement genuinely is evaluated for an anonymous caller - and it fails, because no tenant can be
    /// resolved from a route that names none and a token that does not exist. The response is nevertheless a
    /// challenge rather than a refusal: the framework answers a failed authorisation with <c>401</c> whenever
    /// authentication itself did not succeed, and only with <c>403</c> once a caller has been identified.
    /// Asserting the challenge here keeps that rule visible, so the policy difference between the read and
    /// the write is not misread as a difference in how anonymous callers are treated.
    /// </remarks>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task GetTab_WithoutCredentials_ReturnsUnauthorized()
    {
        using HttpClient client = _fixture.CreateAnonymousClient();

        using HttpResponseMessage response = await client.GetAsync(TabRoute(_fixture.Seed.RootTabId));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    /// <summary>An authenticated caller holding no grant on the page is refused.</summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task GetTab_AsAPlainMemberWithoutAGrant_ReturnsForbidden()
    {
        int tabId = await CreateTabAsync("ITab" + Suffix());

        using HttpClient client = MemberClient();

        using HttpResponseMessage response = await client.GetAsync(TabRoute(tabId));

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    /// <summary>
    /// The same caller reads the page once a view grant reaches a role it holds, which is what proves the
    /// gate consults the stored grants rather than the token's claims.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task GetTab_AsAPlainMemberWithAViewGrant_ReturnsOk()
    {
        int tabId = await CreateTabAsync("ITab" + Suffix());
        await GrantAsync(tabId, _fixture.Seed.TabViewPermissionId, _fixture.Seed.RegisteredRoleId, allowAccess: true);

        using HttpClient client = MemberClient();

        using HttpResponseMessage response = await client.GetAsync(TabRoute(tabId));

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        TabDetailDto detail = await ReadDetailAsync(response);
        detail.TabId.Should().Be(tabId);
    }

    /// <summary>
    /// A grant on the page whose permission belongs to the module vocabulary rather than the page one
    /// confers nothing, even though it is recorded against the page and against a role the caller holds.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// <para>
    /// This pins the strictness of the page evaluator's catalogue narrowing, and it is the assertion a
    /// well-meaning performance change is most likely to break. The evaluator reads every grant on a page in
    /// one request - the grant reader's permission argument carries a documented wildcard - and then decides
    /// which of those rows may be judged by looking each row's permission up in the page catalogue it has
    /// already loaded. A row naming a permission outside that catalogue must be discarded. Reading the rows
    /// more cheaply must not widen which rows count.
    /// </para>
    /// <para>
    /// The grant below is allowing, is recorded against this exact page, and names a role the member holds,
    /// so every check except the scope check would admit it. Its permission carries the module scope code,
    /// which the page catalogue does not include - both scope identifiers seed at zero, so an identifier
    /// alone does not say what it identifies, and this is what keeps a module vocabulary out of a page
    /// decision. A second call after a genuine page-scoped grant is added proves the refusal was the scope
    /// check rather than an unrelated failure to reach the grant at all.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task GetTab_WithAGrantNamingAModuleScopedPermission_ReturnsForbidden()
    {
        int tabId = await CreateTabAsync("ITab" + Suffix());
        await GrantAsync(tabId, _fixture.Seed.ModuleViewPermissionId, _fixture.Seed.RegisteredRoleId, allowAccess: true);

        using HttpClient client = MemberClient();

        using HttpResponseMessage refused = await client.GetAsync(TabRoute(tabId));

        refused.StatusCode.Should().Be(
            HttpStatusCode.Forbidden,
            "the grant names a permission the page catalogue does not carry, so it confers nothing");

        // The same page, the same role, the same allowing grant - only the permission's scope differs - is
        // admitted, which is what makes the refusal above attributable to the scope check alone.
        await GrantAsync(tabId, _fixture.Seed.TabViewPermissionId, _fixture.Seed.RegisteredRoleId, allowAccess: true);

        using HttpResponseMessage admitted = await client.GetAsync(TabRoute(tabId));

        admitted.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    /// <summary>
    /// A grant to the unauthenticated pseudo-role reaches a caller with no account at all.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// <para>
    /// This is the property the view policies were opened up for, and it went unproven long enough to regress.
    /// Two things have to be true at once for it to hold, and only the first was ever tested: the policy must
    /// not demand an authenticated caller, and the requirement must still be able to name the tenant it is
    /// deciding. This route is addressed by page identifier alone, so it names no tenant, and a caller with no
    /// token carries none either - which left the handler abandoning the requirement unevaluated and the caller
    /// refused. That refusal was indistinguishable from a denial while being nothing of the kind: it deleted
    /// exactly the grants this pseudo-role exists to express. The tenant now falls back to the requested host,
    /// which is how the legacy application identified it for every visitor including the anonymous ones.
    /// </para>
    /// <para>
    /// The pseudo-role identifiers are sentinels with no row in the roles table, which is why the grant table
    /// must carry no foreign key to it - a constraint an earlier revision of the schema fabricated, and whose
    /// presence made every grant written here unstorable.
    /// </para>
    /// </remarks>
    [Fact]
    [Trait("Category", "Integration")]
    public async Task GetTab_AsAnAnonymousCallerWithAnUnauthenticatedGrant_ReturnsOk()
    {
        int tabId = await CreateTabAsync("ITab" + Suffix());
        await GrantAsync(tabId, _fixture.Seed.TabViewPermissionId, UnauthenticatedRoleId, allowAccess: true);

        using HttpClient client = _fixture.CreateAnonymousClient();

        using HttpResponseMessage response = await client.GetAsync(TabRoute(tabId));

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        TabDetailDto detail = await ReadDetailAsync(response);
        detail.TabId.Should().Be(tabId);
    }

    /// <summary>
    /// A grant to the unauthenticated pseudo-role reaches <em>only</em> callers with no account, so an
    /// authenticated member holding no grant of their own is still refused.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// Without this, the preceding test would also pass if the host fallback had simply opened the route to
    /// everybody. The pair is what pins the semantics: the same single grant admits the anonymous caller and
    /// excludes the signed-in one, which is the whole meaning of "unauthenticated users".
    /// </remarks>
    [Fact]
    [Trait("Category", "Integration")]
    public async Task GetTab_WithOnlyAnUnauthenticatedGrant_StillRefusesAnAuthenticatedMember()
    {
        int tabId = await CreateTabAsync("ITab" + Suffix());
        await GrantAsync(tabId, _fixture.Seed.TabViewPermissionId, UnauthenticatedRoleId, allowAccess: true);

        using HttpClient client = MemberClient();

        using HttpResponseMessage response = await client.GetAsync(TabRoute(tabId));

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    /// <summary>
    /// A grant to the all-users pseudo-role reaches an anonymous caller as well as an authenticated one.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// The all-users sentinel differs from the unauthenticated one by reaching everybody rather than only the
    /// accountless, so it is asserted separately; a fallback that resolved the tenant only for one of the two
    /// would leave the other silently unreachable.
    /// </remarks>
    [Fact]
    [Trait("Category", "Integration")]
    public async Task GetTab_AsAnAnonymousCallerWithAnAllUsersGrant_ReturnsOk()
    {
        int tabId = await CreateTabAsync("ITab" + Suffix());
        await GrantAsync(tabId, _fixture.Seed.TabViewPermissionId, AllUsersRoleId, allowAccess: true);

        using HttpClient client = _fixture.CreateAnonymousClient();

        using HttpResponseMessage response = await client.GetAsync(TabRoute(tabId));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    /// <summary>
    /// A denial suppresses an allowance on the same page and key, so the caller is refused even though a role
    /// it holds is granted the permission.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// The denial is written against the account rather than a pseudo-role, because the pseudo-role
    /// identifiers are sentinels with no row in the roles table. An account-scoped denial is applicable to the
    /// same caller, which is what the precedence rule needs.
    /// </remarks>
    [Fact]
    public async Task GetTab_WhenADenialSuppressesAnAllowance_ReturnsForbidden()
    {
        int tabId = await CreateTabAsync("ITab" + Suffix());
        await GrantAsync(tabId, _fixture.Seed.TabViewPermissionId, _fixture.Seed.RegisteredRoleId, allowAccess: true);

        using HttpClient client = MemberClient();

        using HttpResponseMessage admitted = await client.GetAsync(TabRoute(tabId));
        admitted.StatusCode.Should().Be(HttpStatusCode.OK);

        await DenyForUserAsync(tabId, _fixture.Seed.TabViewPermissionId, _fixture.Seed.MemberUserId);

        using HttpResponseMessage refused = await client.GetAsync(TabRoute(tabId));
        refused.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    /// <summary>
    /// A page the gate cannot resolve is refused rather than reported missing, even for the host account.
    /// </summary>
    /// <remarks>
    /// The route reaches pages in every tenant, so answering <c>404 Not Found</c> for an identifier that does
    /// not exist while answering <c>403 Forbidden</c> for one that exists in another tenant would let a caller
    /// enumerate identifiers. Both answer the refusal.
    /// </remarks>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task GetTab_WhenUnknown_ReturnsForbiddenEvenForTheHost()
    {
        using HttpClient client = _fixture.CreateHostClient();

        using HttpResponseMessage response = await client.GetAsync(TabRoute(UnknownTabId));

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    /// <summary>A page belonging to another tenant is refused on the same grounds.</summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task GetTab_ForAPageInAnotherTenant_ReturnsForbidden()
    {
        using HttpClient host = _fixture.CreateHostClient();
        int isolatedPortalId = await CreateIsolatedPortalAsync(host);
        int foreignTabId = await CreateTabAsync("IForeign" + Suffix(), portalId: isolatedPortalId);

        // The token is scoped to the seeded tenant, so the gate evaluates the page against that tenant and
        // finds it does not belong to it.
        using HttpResponseMessage response = await host.GetAsync(TabRoute(foreignTabId));

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    /// <summary>
    /// A blank page name is refused as an RFC 7807 validation response naming the member, and nothing is
    /// written.
    /// </summary>
    /// <param name="submittedName">The name to submit.</param>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// MIGRATION: <c>Website/admin/Tabs/managetabs.ascx</c> L36-L37 declares a required-field validator over
    /// the page-name box, so none of these submissions could reach the legacy controller. This test's real
    /// subject is whether the new validator is ATTACHED TO THE ACTION - a validator that exists but is never
    /// invoked would pass its own unit suite and change nothing here - which is why the assertion is on the
    /// status code and the payload's <c>errors</c> key rather than on the validator's own output.
    /// </remarks>
    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("   ")]
    public async Task UpdateTab_WithABlankName_ReturnsValidationProblem(string submittedName)
    {
        string originalName = "IBlank" + Suffix();
        int tabId = await CreateTabAsync(originalName);

        using HttpClient client = _fixture.CreateHostClient();

        using HttpResponseMessage response = await client.PutAsJsonAsync(
            TabRoute(tabId),
            new UpdateTabRequest { TabName = submittedName },
            ApiTestFixture.Json);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        string payload = await response.Content.ReadAsStringAsync();
        payload.Should().Contain("\"errors\"");
        payload.Should().Contain("Page Name Is Required");

        string storedName = await _fixture.Database.ScalarAsync<string>(
            "SELECT [TabName] FROM [dbo].[Tabs] WHERE [TabID] = @tabId;",
            new Dictionary<string, object?> { ["tabId"] = tabId });

        storedName.Should().Be(originalName, "a refused update must not have written anything");
    }

    /// <summary>
    /// A value one character beyond its column width is refused as a validation problem rather than reaching
    /// the database and faulting there.
    /// </summary>
    /// <param name="member">The member to overrun.</param>
    /// <param name="limit">The member's measured maximum length.</param>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// This is the assertion the finding turned on. Without the validator each of these submissions travels
    /// the whole way to SQL Server, which refuses it as a truncation error - surfacing as a 500 that names no
    /// field - or, on a connection configured to truncate, stores a silently shortened value. The status code
    /// is therefore the whole point: 400 means the shape was judged, 500 means it was not.
    /// </remarks>
    [Theory]
    [InlineData("tabName", 50)]
    [InlineData("title", 200)]
    [InlineData("description", 500)]
    [InlineData("keywords", 500)]
    [InlineData("pageHeadText", 500)]
    [InlineData("iconFile", 100)]
    [InlineData("url", 255)]
    [InlineData("skinSrc", 200)]
    [InlineData("containerSrc", 200)]
    public async Task UpdateTab_BeyondAColumnWidth_ReturnsValidationProblem(string member, int limit)
    {
        int tabId = await CreateTabAsync("IWide" + Suffix());

        using HttpClient client = _fixture.CreateHostClient();

        // Built as a raw document rather than through the request type, so the test drives the JSON member
        // name the caller actually sends. The name is always present, because it is required.
        Dictionary<string, object?> body = new(StringComparer.Ordinal)
        {
            ["tabName"] = "IWideName",
            [member] = new string('x', limit + 1),
        };

        using HttpResponseMessage response = await client.PutAsJsonAsync(
            TabRoute(tabId),
            body,
            ApiTestFixture.Json);

        response.StatusCode.Should().Be(
            HttpStatusCode.BadRequest,
            "an overlong {0} must be judged by the validator, not by the database",
            member);

        string payload = await response.Content.ReadAsStringAsync();
        payload.Should().Contain("\"errors\"");
    }

    /// <summary>
    /// A value exactly at its column width is accepted, so the widths are not off by one.
    /// </summary>
    /// <param name="member">The member to fill.</param>
    /// <param name="limit">The member's measured maximum length.</param>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// The companion to the test above, and not optional. An off-by-one rule would refuse the longest
    /// legitimate value - making a page whose name is exactly fifty characters uneditable - and would pass
    /// every over-limit assertion while doing so.
    /// </remarks>
    [Theory]
    [InlineData("title", 200)]
    [InlineData("description", 500)]
    [InlineData("keywords", 500)]
    [InlineData("pageHeadText", 500)]
    public async Task UpdateTab_AtAColumnWidth_IsAccepted(string member, int limit)
    {
        int tabId = await CreateTabAsync("IAtLimit" + Suffix());

        using HttpClient client = _fixture.CreateHostClient();

        Dictionary<string, object?> body = new(StringComparer.Ordinal)
        {
            ["tabName"] = "IAtLimitName",
            [member] = new string('x', limit),
        };

        using HttpResponseMessage response = await client.PutAsJsonAsync(
            TabRoute(tabId),
            body,
            ApiTestFixture.Json);

        response.StatusCode.Should().Be(
            HttpStatusCode.OK,
            "a {0} of exactly {1} characters is the longest legitimate value and must be accepted",
            member,
            limit);
    }

    /// <summary>A write is applied and persisted, and the stored path follows the new name.</summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task UpdateTab_ReturnsOkAndPersists()
    {
        string renamed = "IRenamed" + Suffix();
        int tabId = await CreateTabAsync("ITab" + Suffix());

        using HttpClient client = _fixture.CreateHostClient();

        UpdateTabRequest request = new()
        {
            TabName = renamed,
            Title = "Renamed page",
            Description = "Written by the integration suite.",
            Keywords = "integration,page",
            IsVisible = false,
            DisableLink = true,
            ParentId = _fixture.Seed.RootTabId,
            IconFile = "page.gif",
            Url = "https://example.com/target",
            RefreshInterval = 30,
            PageHeadText = "<meta name=\"robots\" content=\"noindex\" />",
            IsSecure = true,
        };

        using HttpResponseMessage response = await client.PutAsJsonAsync(
            TabRoute(tabId),
            request,
            ApiTestFixture.Json);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        TabDetailDto updated = await ReadDetailAsync(response);

        updated.TabId.Should().Be(tabId);
        updated.TabName.Should().Be(renamed);
        updated.Title.Should().Be(request.Title);
        updated.Description.Should().Be(request.Description);
        updated.Keywords.Should().Be(request.Keywords);
        updated.IsVisible.Should().BeFalse();
        updated.DisableLink.Should().BeTrue();
        updated.ParentId.Should().Be(_fixture.Seed.RootTabId);
        updated.IconFile.Should().Be(request.IconFile);
        updated.Url.Should().Be(request.Url);
        updated.RefreshInterval.Should().Be(request.RefreshInterval);
        updated.PageHeadText.Should().Be(request.PageHeadText);
        updated.IsSecure.Should().BeTrue();
        updated.Level.Should().Be(1);
        updated.TabPath.Should().Be("//Home//" + renamed);

        using HttpResponseMessage read = await client.GetAsync(TabRoute(tabId));

        read.StatusCode.Should().Be(HttpStatusCode.OK);

        TabDetailDto persisted = await ReadDetailAsync(read);

        persisted.TabName.Should().Be(renamed);
        persisted.IsVisible.Should().BeFalse();
        persisted.IsSecure.Should().BeTrue();
        persisted.TabPath.Should().Be("//Home//" + renamed);
    }

    /// <summary>
    /// The sibling ordinal is server-owned: the write contract carries none, the renumbering pass assigns
    /// it, and the existing relative order survives an edit unchanged.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// This replaces an earlier test that submitted an ordinal and asserted it placed the page. That
    /// premise no longer holds, and asserting it would have been wrong: the legacy update procedure
    /// neither accepted nor wrote the order column, the legacy controller passed <c>0</c> for both the
    /// order and the depth precisely so its ordering routine would recompute them, the legacy page form
    /// carried no ordinal control at all, and the page surface deliberately exposes no reorder or move
    /// endpoint. A client-supplied ordinal would therefore have been a capability the target does not
    /// have. What is asserted instead is the invariant that survives: placement is the server's to
    /// decide, and an ordinary edit does not disturb it.
    /// </remarks>
    [Fact]
    public async Task UpdateTab_LeavesTheSiblingOrdinalToTheServer()
    {
        const int seededFirstOrder = 100;
        const int seededSecondOrder = 200;

        string firstName = "IOrderA" + Suffix();
        string secondName = "IOrderB" + Suffix();

        int firstId = await CreateTabAsync(firstName, tabOrder: seededFirstOrder);
        int secondId = await CreateTabAsync(secondName, tabOrder: seededSecondOrder);

        using HttpClient client = _fixture.CreateHostClient();

        using HttpResponseMessage response = await client.PutAsJsonAsync(
            TabRoute(secondId),
            NewUpdateRequest(secondName),
            ApiTestFixture.Json);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        TabDetailDto written = await ReadDetailAsync(response);

        // The server renumbers the whole tenant onto its own sequence, so the seeded value does not
        // survive even though the request said nothing about the order.
        written.TabOrder.Should().NotBe(
            seededSecondOrder,
            "the renumbering pass assigns every ordinal from its own sequence after the write");
        written.TabOrder.Should().BePositive();

        // The relative order is preserved, because the renumbering pass sorts siblings by their stored
        // ordinal before reassigning.
        int firstOrder = await ReadOrderAsync(client, firstId);
        firstOrder.Should().BeLessThan(
            written.TabOrder,
            "an edit must not reshuffle siblings that the caller did not mention");

        // Repeating the identical request cannot move the page, because the contract carries no ordinal
        // for a caller to vary.
        using HttpResponseMessage repeated = await client.PutAsJsonAsync(
            TabRoute(secondId),
            NewUpdateRequest(secondName),
            ApiTestFixture.Json);

        repeated.StatusCode.Should().Be(HttpStatusCode.OK);

        TabDetailDto rewritten = await ReadDetailAsync(repeated);

        rewritten.TabOrder.Should().Be(written.TabOrder, "the renumbering pass is deterministic");
        (await ReadOrderAsync(client, firstId)).Should().BeLessThan(rewritten.TabOrder);
    }

    /// <summary>
    /// Reparenting recomputes the depth and the path from the new ancestry rather than leaving the stored
    /// values behind.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task UpdateTab_WhenReparented_RecomputesDepthAndPath()
    {
        string parentName = "IParent" + Suffix();
        string childName = "IChild" + Suffix();

        int parentId = await CreateTabAsync(parentName);
        int childId = await CreateTabAsync(childName);

        using HttpClient client = _fixture.CreateHostClient();

        UpdateTabRequest request = NewUpdateRequest(childName);
        request.ParentId = parentId;

        using HttpResponseMessage response = await client.PutAsJsonAsync(
            TabRoute(childId),
            request,
            ApiTestFixture.Json);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        TabDetailDto updated = await ReadDetailAsync(response);

        updated.ParentId.Should().Be(parentId);
        updated.Level.Should().Be(2);
        updated.TabPath.Should().Be("//Home//" + parentName + "//" + childName);

        // The new parent now reports that it has children, which the read projection derives rather than
        // storing.
        using HttpResponseMessage parent = await client.GetAsync(TabRoute(parentId));
        TabDetailDto parentDetail = await ReadDetailAsync(parent);

        parentDetail.HasChildren.Should().BeTrue();
        parentDetail.Level.Should().Be(1);
    }

    /// <summary>A reserved device name is refused.</summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task UpdateTab_WithAReservedDeviceName_ReturnsBadRequest()
    {
        int tabId = await CreateTabAsync("ITab" + Suffix());

        using HttpClient client = _fixture.CreateHostClient();

        using HttpResponseMessage response = await client.PutAsJsonAsync(
            TabRoute(tabId),
            NewUpdateRequest("CON"),
            ApiTestFixture.Json);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        string body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("urn:dnnmigration:error:tab.name_reserved");
        body.Should().Contain("reserved device name");
    }

    /// <summary>A page cannot be made its own parent.</summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task UpdateTab_MakingAPageItsOwnParent_ReturnsBadRequest()
    {
        string name = "ISelf" + Suffix();
        int tabId = await CreateTabAsync(name);

        using HttpClient client = _fixture.CreateHostClient();

        UpdateTabRequest request = NewUpdateRequest(name);
        request.ParentId = tabId;

        using HttpResponseMessage response = await client.PutAsJsonAsync(
            TabRoute(tabId),
            request,
            ApiTestFixture.Json);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        string body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("urn:dnnmigration:error:tab.parent_cycle");
        body.Should().Contain("cannot be its own parent");
    }

    /// <summary>
    /// A page cannot be moved beneath one of its own descendants, which would detach the branch from the tree.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task UpdateTab_WithADescendantAsParent_ReturnsBadRequest()
    {
        string ancestorName = "IAnc" + Suffix();
        int ancestorId = await CreateTabAsync(ancestorName);
        int descendantId = await CreateTabAsync("IDesc" + Suffix(), parentId: ancestorId);

        using HttpClient client = _fixture.CreateHostClient();

        UpdateTabRequest request = NewUpdateRequest(ancestorName);
        request.ParentId = descendantId;

        using HttpResponseMessage response = await client.PutAsJsonAsync(
            TabRoute(ancestorId),
            request,
            ApiTestFixture.Json);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        string body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("urn:dnnmigration:error:tab.parent_cycle");
        body.Should().Contain("descendant");
    }

    /// <summary>A parent that does not exist is reported as a missing resource.</summary>
    /// <remarks>
    /// The status follows the codebase-wide convention that a named resource which cannot be found answers
    /// <c>404 Not Found</c> whether it was named in the route or in the body - the same convention under which
    /// a role naming an unknown group on creation answers <c>404</c>. Only the reasons that describe the
    /// request itself, such as a parent that would form a cycle or one belonging to another tenant, answer
    /// <c>400 Bad Request</c>. Both are asserted, here and in the two tests that follow.
    /// </remarks>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task UpdateTab_WithAnUnknownParent_ReturnsNotFound()
    {
        string name = "IOrphan" + Suffix();
        int tabId = await CreateTabAsync(name);

        using HttpClient client = _fixture.CreateHostClient();

        UpdateTabRequest request = NewUpdateRequest(name);
        request.ParentId = UnknownTabId;

        using HttpResponseMessage response = await client.PutAsJsonAsync(
            TabRoute(tabId),
            request,
            ApiTestFixture.Json);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);

        string body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("urn:dnnmigration:error:tab.parent_not_found");
        body.Should().Contain("cannot be used as a parent");
    }

    /// <summary>
    /// A parent in another tenant is refused, which is the tenant isolation the legacy screen obtained from a
    /// portal-filtered picker and which must now be asserted in the service.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task UpdateTab_WithAParentInAnotherTenant_ReturnsBadRequest()
    {
        using HttpClient client = _fixture.CreateHostClient();

        int isolatedPortalId = await CreateIsolatedPortalAsync(client);
        int foreignParentId = await CreateTabAsync("IForeign" + Suffix(), portalId: isolatedPortalId);

        string name = "ICross" + Suffix();
        int tabId = await CreateTabAsync(name);

        UpdateTabRequest request = NewUpdateRequest(name);
        request.ParentId = foreignParentId;

        using HttpResponseMessage response = await client.PutAsJsonAsync(
            TabRoute(tabId),
            request,
            ApiTestFixture.Json);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        string body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("urn:dnnmigration:error:tab.parent_cross_portal");
        body.Should().Contain("different portal");
    }

    /// <summary>
    /// An anonymous write is challenged, and here the policy refuses it before the permission is ever
    /// consulted because it requires an authenticated caller in its own right.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task UpdateTab_WithoutCredentials_ReturnsUnauthorized()
    {
        int tabId = await CreateTabAsync("ITab" + Suffix());

        using HttpClient client = _fixture.CreateAnonymousClient();

        using HttpResponseMessage response = await client.PutAsJsonAsync(
            TabRoute(tabId),
            NewUpdateRequest("IAnon" + Suffix()),
            ApiTestFixture.Json);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    /// <summary>
    /// A page the write gate cannot resolve is refused rather than reported missing, on the same grounds as
    /// the read.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task UpdateTab_WhenUnknown_ReturnsForbidden()
    {
        using HttpClient client = _fixture.CreateHostClient();

        using HttpResponseMessage response = await client.PutAsJsonAsync(
            TabRoute(UnknownTabId),
            NewUpdateRequest("IGhost" + Suffix()),
            ApiTestFixture.Json);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    /// <summary>An authenticated caller holding a view grant but no edit grant cannot write.</summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task UpdateTab_AsAPlainMemberHoldingOnlyAViewGrant_ReturnsForbidden()
    {
        string name = "IView" + Suffix();
        int tabId = await CreateTabAsync(name);
        await GrantAsync(tabId, _fixture.Seed.TabViewPermissionId, _fixture.Seed.RegisteredRoleId, allowAccess: true);

        using HttpClient client = MemberClient();

        using HttpResponseMessage read = await client.GetAsync(TabRoute(tabId));
        read.StatusCode.Should().Be(HttpStatusCode.OK);

        using HttpResponseMessage response = await client.PutAsJsonAsync(
            TabRoute(tabId),
            NewUpdateRequest(name),
            ApiTestFixture.Json);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    /// <summary>The same caller writes once an edit grant reaches a role it holds.</summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task UpdateTab_AsAPlainMemberHoldingAnEditGrant_ReturnsOk()
    {
        string name = "IEdit" + Suffix();
        int tabId = await CreateTabAsync(name);
        await GrantAsync(tabId, _fixture.Seed.TabEditPermissionId, _fixture.Seed.RegisteredRoleId, allowAccess: true);

        using HttpClient client = MemberClient();

        string renamed = "IEdited" + Suffix();

        using HttpResponseMessage response = await client.PutAsJsonAsync(
            TabRoute(tabId),
            NewUpdateRequest(renamed),
            ApiTestFixture.Json);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        TabDetailDto updated = await ReadDetailAsync(response);
        updated.TabName.Should().Be(renamed);
    }

    /// <summary>
    /// A write discards the cached navigation for the tenant, so the listing reflects it immediately rather
    /// than after the cache entry expires.
    /// </summary>
    /// <remarks>
    /// The listing is cached per tenant for a long interval, so the cache state has to be established through
    /// the API rather than assumed. The page is therefore written once before the listing is read: that write
    /// discards whatever a previous test left cached, and the read that follows repopulates the entry with a
    /// value known to contain the page. Only then is the second write's invalidation observable. Establishing
    /// the state with the direct insert alone would not work - an insert made behind the service's back
    /// invalidates nothing, which is a property of the seam and not a defect in it.
    /// </remarks>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task UpdateTab_DiscardsTheCachedNavigation()
    {
        string original = "ICache" + Suffix();
        int tabId = await CreateTabAsync(original);

        using HttpClient client = _fixture.CreateHostClient();

        using HttpResponseMessage seeded = await client.PutAsJsonAsync(
            TabRoute(tabId),
            NewUpdateRequest(original),
            ApiTestFixture.Json);

        seeded.StatusCode.Should().Be(HttpStatusCode.OK);

        using HttpResponseMessage before = await client.GetAsync(TabsRoute(_fixture.Seed.PortalId));
        before.StatusCode.Should().Be(HttpStatusCode.OK);

        List<TabListItemDto> cached = await ReadListAsync(before);
        cached.Should().Contain(tab => tab.TabName == original);

        string renamed = "IFresh" + Suffix();

        using HttpResponseMessage written = await client.PutAsJsonAsync(
            TabRoute(tabId),
            NewUpdateRequest(renamed),
            ApiTestFixture.Json);

        written.StatusCode.Should().Be(HttpStatusCode.OK);

        using HttpResponseMessage after = await client.GetAsync(TabsRoute(_fixture.Seed.PortalId));
        after.StatusCode.Should().Be(HttpStatusCode.OK);

        List<TabListItemDto> refreshed = await ReadListAsync(after);

        refreshed.Should().Contain(tab => tab.TabName == renamed);
        refreshed.Should().NotContain(tab => tab.TabName == original);
    }

    /// <summary>Builds a write request whose fields are all explicit, so nothing is asserted by default.</summary>
    /// <param name="tabName">The page name.</param>
    /// <returns>The request.</returns>
    /// <remarks>
    /// The request carries no ordinal, no depth and no path: all three are server-owned, so the write
    /// contract has no member for any of them and a caller cannot influence placement.
    /// </remarks>
    private UpdateTabRequest NewUpdateRequest(string tabName) => new()
    {
        TabName = tabName,
        Title = tabName,
        IsVisible = true,
        DisableLink = false,
        ParentId = _fixture.Seed.RootTabId,
        IsSecure = false,
        IsDeleted = false,
    };

    /// <summary>Reads the stored ordinal of one page.</summary>
    /// <param name="client">A client entitled to read the page.</param>
    /// <param name="tabId">The page identifier.</param>
    /// <returns>The stored ordinal.</returns>
    private static async Task<int> ReadOrderAsync(HttpClient client, int tabId)
    {
        using HttpResponseMessage response = await client.GetAsync(TabRoute(tabId));

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        TabDetailDto detail = await ReadDetailAsync(response);
        return detail.TabOrder;
    }

    /// <summary>
    /// Inserts a page directly, because the API exposes no page-creation route by design and the suite still
    /// needs pages it may mutate without disturbing the seeded ones.
    /// </summary>
    /// <param name="tabName">The page name, which must be free of non-word characters so the generated path
    /// is predictable.</param>
    /// <param name="parentId">The parent page, defaulting to the seeded root.</param>
    /// <param name="tabOrder">The ordinal to store, which any later write renumbers.</param>
    /// <param name="portalId">The owning tenant, defaulting to the seeded one.</param>
    /// <returns>The new page identifier.</returns>
    /// <remarks>
    /// The depth and the path are derived from the parent row inside the statement rather than passed in, so
    /// an inserted page is consistent with the tree it joins even before any write recomputes it. Passing them
    /// in would let a test store a depth and a path that contradict the parent, which would make a later
    /// assertion about recomputation pass for the wrong reason.
    /// </remarks>
    private async Task<int> CreateTabAsync(
        string tabName,
        int? parentId = null,
        int tabOrder = 50,
        int? portalId = null)
    {
        int owner = portalId ?? _fixture.Seed.PortalId;
        int? parent = parentId ?? (portalId is null ? _fixture.Seed.RootTabId : null);

        return await _fixture.Database.ScalarAsync<int>(
            """
            DECLARE @level int = 0;
            DECLARE @tabPath nvarchar(255) = N'//' + @tabName;

            IF @parentId IS NOT NULL
            BEGIN
                SELECT @level = p.[Level] + 1,
                       @tabPath = COALESCE(p.[TabPath], N'') + N'//' + @tabName
                FROM [dbo].[Tabs] p
                WHERE p.[TabID] = @parentId;
            END

            INSERT INTO [dbo].[Tabs]
                ([TabOrder], [PortalID], [TabName], [IsVisible], [ParentId], [Level], [DisableLink],
                 [Title], [IsDeleted], [TabPath], [IsSecure])
            VALUES (@tabOrder, @portalId, @tabName, 1, @parentId, @level, 0, @tabName, 0, @tabPath, 0);
            SELECT CAST(SCOPE_IDENTITY() AS int);
            """,
            new Dictionary<string, object?>
            {
                ["tabOrder"] = tabOrder,
                ["portalId"] = owner,
                ["tabName"] = tabName,
                ["parentId"] = parent,
            });
    }

    /// <summary>Writes a role-scoped page grant, replacing any existing one for the same triple.</summary>
    /// <param name="tabId">The page identifier.</param>
    /// <param name="permissionId">The catalogue entry identifier.</param>
    /// <param name="roleId">The role identifier.</param>
    /// <param name="allowAccess">Whether the grant admits or denies.</param>
    /// <returns>A task representing the write.</returns>
    /// <remarks>
    /// The grants are written directly because no grant-management endpoint is in scope. The delete makes the
    /// helper idempotent, since the suites share one database and give no ordering guarantee.
    /// </remarks>
    private async Task GrantAsync(int tabId, int permissionId, int roleId, bool allowAccess)
    {
        await _fixture.Database.ExecuteAsync(
            """
            DELETE FROM [dbo].[TabPermission]
            WHERE [TabID] = @tabId AND [PermissionID] = @permissionId AND [RoleID] = @roleId;

            INSERT INTO [dbo].[TabPermission] ([TabID], [PermissionID], [RoleID], [AllowAccess])
            VALUES (@tabId, @permissionId, @roleId, @allowAccess);
            """,
            new Dictionary<string, object?>
            {
                ["tabId"] = tabId,
                ["permissionId"] = permissionId,
                ["roleId"] = roleId,
                ["allowAccess"] = allowAccess,
            });
    }

    /// <summary>Writes an account-scoped denial for one page and key.</summary>
    /// <param name="tabId">The page identifier.</param>
    /// <param name="permissionId">The catalogue entry identifier.</param>
    /// <param name="userId">The account identifier.</param>
    /// <returns>A task representing the write.</returns>
    private async Task DenyForUserAsync(int tabId, int permissionId, int userId)
    {
        await _fixture.Database.ExecuteAsync(
            """
            DELETE FROM [dbo].[TabPermission]
            WHERE [TabID] = @tabId AND [PermissionID] = @permissionId AND [UserID] = @userId;

            INSERT INTO [dbo].[TabPermission] ([TabID], [PermissionID], [UserID], [AllowAccess])
            VALUES (@tabId, @permissionId, @userId, 0);
            """,
            new Dictionary<string, object?>
            {
                ["tabId"] = tabId,
                ["permissionId"] = permissionId,
                ["userId"] = userId,
            });
    }

    /// <summary>Creates a tenant with no pages, for the isolation assertions.</summary>
    /// <param name="client">A host client.</param>
    /// <returns>The new tenant identifier.</returns>
    private async Task<int> CreateIsolatedPortalAsync(HttpClient client)
    {
        string suffix = Suffix();

        using HttpResponseMessage response = await client.PostAsJsonAsync(
            new Uri("/api/v1/portals", UriKind.Relative),
            new
            {
                portalName = "Page Suite Portal " + suffix,
                portalAlias = "pages-" + suffix + ".local",
                homeDirectory = string.Empty,
                templateFile = "admin.template",
                isChildPortal = false,
                administratorFirstName = "Suite",
                administratorLastName = "Administrator",
                administratorUsername = "pages_admin_" + suffix,
                administratorPassword = ApiTestFixture.KnownPassword,
                administratorEmail = "pages." + suffix + "@example.com",
            },
            ApiTestFixture.Json);

        response.StatusCode.Should().Be(HttpStatusCode.Created);

        using System.Text.Json.JsonDocument document =
            System.Text.Json.JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        // The created representation travels inside the shared success envelope, so the identifier is one
        // level down under "data". Read as raw JSON rather than through a typed envelope because only the
        // one member is wanted, and naming it here proves the envelope member name as a side effect.
        return document.RootElement
            .GetProperty("data")
            .GetProperty("portalId")
            .GetInt32();
    }

    /// <summary>Builds a client for the seeded plain member.</summary>
    /// <returns>An authenticated client holding no administrative role.</returns>
    private HttpClient MemberClient() => _fixture.CreateClientFor(
        _fixture.Seed.MemberUserId,
        IntegrationSeed.MemberUserName,
        _fixture.Seed.PortalId,
        isSuperUser: false,
        roles: new[] { IntegrationSeed.RegisteredUsersRoleName });

    /// <summary>Reads a page listing out of the collection envelope a response carries.</summary>
    /// <param name="response">The response.</param>
    /// <returns>The listing.</returns>
    /// <remarks>
    /// The listing travels inside the Application layer's standard success envelope, under <c>data</c>,
    /// because it is an unpaged collection read. Binding straight onto a bare list would fail at run time
    /// rather than at compile time, which is exactly what happened when the wire contract was corrected,
    /// so the envelope is named explicitly here.
    /// </remarks>
    private static async Task<List<TabListItemDto>> ReadListAsync(HttpResponseMessage response)
    {
        CollectionEnvelope<TabListItemDto>? envelope = await response.Content
            .ReadFromJsonAsync<CollectionEnvelope<TabListItemDto>>(ApiTestFixture.Json);

        envelope.Should().NotBeNull();
        return envelope!.Data.ToList();
    }

    /// <summary>Reads a page representation from a response.</summary>
    /// <param name="response">The response.</param>
    /// <returns>The representation.</returns>
    private static async Task<TabDetailDto> ReadDetailAsync(HttpResponseMessage response)
    {
        TabDetailDto? detail = await response.Content
            .ReadEnvelopeAsync<TabDetailDto>();

        detail.Should().NotBeNull();
        return detail!;
    }

    /// <summary>Builds the collection route for a tenant's pages.</summary>
    /// <param name="portalId">The tenant identifier.</param>
    /// <returns>A relative route.</returns>
    private static Uri TabsRoute(int portalId) =>
        new($"/api/v1/portals/{Route(portalId)}/tabs", UriKind.Relative);

    /// <summary>Builds the item route for one page.</summary>
    /// <param name="tabId">The page identifier.</param>
    /// <returns>A relative route.</returns>
    private static Uri TabRoute(int tabId) => new($"/api/v1/tabs/{Route(tabId)}", UriKind.Relative);

    /// <summary>Formats an identifier for a route without picking up the ambient culture.</summary>
    /// <param name="value">The identifier.</param>
    /// <returns>The invariant representation.</returns>
    private static string Route(int value) => value.ToString(CultureInfo.InvariantCulture);

    /// <summary>Produces a short random suffix for values that must not collide across tests.</summary>
    /// <returns>Twelve lower-case hexadecimal characters.</returns>
    private static string Suffix() => Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture)[..12];
}
