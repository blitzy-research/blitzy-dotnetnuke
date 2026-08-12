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

    /// <summary>The media type an RFC 7807 problem document is served under when a caller asks for it.</summary>
    private const string ProblemMediaType = "application/problem+json";

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
        using HttpClient client = await _fixture.CreateAdministratorClientAsync();

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
    /// A soft-deleted page is still listed, carrying its own deletion flag, because the terminal legacy read
    /// returns soft-deleted rows and projects that flag for the caller to act on.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// <para>
    /// This pins the answer to a question the listing cannot answer twice: a caller that receives a
    /// filtered listing cannot tell a tenant with no soft-deleted pages from a tenant whose soft-deleted
    /// pages were withheld from it. The page is marked deleted through the write endpoint rather than by a
    /// direct insert so that the cached navigation is invalidated the way production invalidates it -
    /// marking a page behind the service's back would leave the assertion reading a stale entry and
    /// passing for the wrong reason. It is unmarked the same way once asserted, which leaves the shared
    /// tenant in the state every other page this suite creates leaves it in.
    /// The restoring and emptying of soft-deleted pages have no endpoint here, and their absence is asserted
    /// separately - this test is about the LISTING projecting the flag, not about a bin surface existing.
    /// </para>
    /// <para>
    /// THE UNMARKING IS IN A FINALLY BLOCK, and that is not defensive tidiness. The mark is a mutation of the
    /// SHARED seeded tenant, so a failing assertion between the mark and the unmark would leave a soft-deleted
    /// page in the tenant every later fact in this collection reads - and the collection is serialised rather
    /// than isolated, so the contamination outlives this fact and can only produce failures that point
    /// somewhere else. One genuine failure reported here is worth incomparably more than several downstream
    /// failures reported in facts that did nothing wrong.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task ListTabs_IncludesASoftDeletedPage()
    {
        string name = "IRecycled" + Suffix();
        int tabId = await CreateTabAsync(name);

        using HttpClient client = await _fixture.CreateHostClientAsync();

        UpdateTabRequest recycled = NewUpdateRequest(name);
        recycled.IsDeleted = true;

        using HttpResponseMessage written = await client.PutAsJsonAsync(
            TabRoute(tabId),
            recycled,
            ApiTestFixture.Json);

        written.StatusCode.Should().Be(HttpStatusCode.OK);

        try
        {
            using HttpResponseMessage response = await client.GetAsync(TabsRoute(_fixture.Seed.PortalId));

            response.StatusCode.Should().Be(HttpStatusCode.OK);

            List<TabListItemDto> tabs = await ReadListAsync(response);

            TabListItemDto? listed = tabs.SingleOrDefault(tab => tab.TabId == tabId);

            listed.Should().NotBeNull("a recycled page is still a row and the listing is complete by contract");
            listed!.IsDeleted.Should().BeTrue("the flag travels on the row so the caller owns the filtering");
            listed.TabName.Should().Be(name);
        }
        finally
        {
            // Through the endpoint again rather than through the database, so the cached navigation is
            // invalidated the way production invalidates it. A direct row update here would restore the row
            // and leave every later reader served from a cache that still remembers the page as deleted,
            // which is contamination of a subtler kind than the one the finally block exists to prevent.
            using HttpResponseMessage restored = await client.PutAsJsonAsync(
                TabRoute(tabId),
                NewUpdateRequest(name),
                ApiTestFixture.Json);

            restored.StatusCode.Should().Be(
                HttpStatusCode.OK,
                "the shared tenant must be left without a soft-deleted page, so a failure to restore it is "
                + "itself a result worth reporting");
        }
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
    /// The listing is tenant-bound, so an ordinary member of the tenant holding no page grant at all is
    /// refused however valid its token is.
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
    /// <para>
    /// ⚠ THE PRECONDITION IS ESTABLISHED RATHER THAN ASSUMED, and that is not defensive padding. The route now
    /// carries the tenant-wide content-editor policy, which admits a caller holding EDIT on ANY page of the
    /// tenant, so "this caller holds no grant" is a statement about stored rows rather than about the caller's
    /// role. The suites share one database and xUnit gives no ordering guarantee, so a sibling case that grants
    /// EDIT to the registered role would otherwise decide this one's outcome - the assertion would pass or fail
    /// according to execution order, which is worse than either answer. Clearing the tenant's EDIT grants first
    /// makes the refusal attributable to the absent grant and to nothing else.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task ListTabs_AsAnOrdinaryMemberHoldingNoPageGrant_ReturnsForbidden()
    {
        await ClearEditGrantsAsync();

        using HttpClient member = await _fixture.CreateUnprivilegedClientAsync();

        using HttpResponseMessage response = await member.GetAsync(TabsRoute(_fixture.Seed.PortalId));

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    /// <summary>
    /// The same member IS served once an edit grant reaches a role it holds, and is served ONLY the pages that
    /// grant reaches.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// <para>
    /// ⚠ BOTH HALVES IN ONE CASE, BECAUSE EITHER ALONE WOULD BE MISLEADING. Admission without narrowing would
    /// mean a page administrator could enumerate the tenant's whole navigation hierarchy - the disclosure the
    /// tenant-administration policy was applied to this route to close. Narrowing without admission would mean
    /// the capability is still unreachable. The fix is only correct if both hold at once.
    /// </para>
    /// <para>
    /// MIGRATION: <c>ModuleSettings.ascx.vb:L214-L219</c> left the page selector populated and ENABLED for a
    /// caller in the administrators role and disabled it for everyone else - "tab administrators can only
    /// manage their own tab" - re-applying the same rule on postback at <c>L332-L338</c> so a disabled control
    /// could not be reached by replaying the form. A tab administrator therefore never chose from a
    /// portal-wide list. Offering that caller exactly the pages it holds EDIT on is the equivalent that
    /// survives the loss of the legacy ambient "active page", and it is strictly narrower than the list the
    /// legacy rendered-but-disabled.
    /// </para>
    /// <para>
    /// A SECOND PAGE IS CREATED AND LEFT UNGRANTED, so the assertion measures exclusion rather than merely
    /// counting. A case that granted one page in a tenant with one page would pass on an implementation that
    /// filtered nothing at all.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task ListTabs_AsAnOrdinaryMemberHoldingOneEditGrant_ReturnsOnlyThatPage()
    {
        await ClearEditGrantsAsync();

        int granted = await CreateTabAsync("LGrant" + Suffix());
        int withheld = await CreateTabAsync("LWithheld" + Suffix());

        await GrantAsync(
            granted,
            _fixture.Seed.TabEditPermissionId,
            _fixture.Seed.RegisteredRoleId,
            allowAccess: true);

        using HttpClient member = await MemberClientAsync();

        using HttpResponseMessage response = await member.GetAsync(TabsRoute(_fixture.Seed.PortalId));

        response.StatusCode.Should().Be(
            HttpStatusCode.OK,
            "an edit grant on one page is what the module placement capability is built on");

        IReadOnlyList<TabListItemDto> rows = await ReadListAsync(response);

        rows.Select(row => row.TabId).Should().Contain(
            granted,
            "the page the grant reaches is a page this caller may place a module on");
        rows.Select(row => row.TabId).Should().NotContain(
            withheld,
            "a page the grant does not reach must not be offered as a placement target");
    }

    /// <summary>
    /// A tenant administrator still receives every page, including the ones no grant row names.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// THE ARM THAT WOULD BREAK SILENTLY IF THE NARROWING WERE APPLIED UNIFORMLY. A tenant's administrator
    /// administers its pages whether or not a grant row happens to name their role, and a DotNetNuke
    /// installation whose page grants were never populated is an ordinary state - so filtering an administrator
    /// by stored grants would return an EMPTY listing to the one caller entitled to all of it, and the module
    /// placement form would lose its target selector for exactly the caller who previously had it. The two
    /// pages here are deliberately left with no EDIT grant at all after the clear.
    /// </remarks>
    [Fact]
    public async Task ListTabs_AsATenantAdministrator_ReturnsEveryPageEvenWithNoGrantRows()
    {
        await ClearEditGrantsAsync();

        int first = await CreateTabAsync("LAdminA" + Suffix());
        int second = await CreateTabAsync("LAdminB" + Suffix());

        using HttpClient administrator = await _fixture.CreateAdministratorClientAsync();

        using HttpResponseMessage response = await administrator.GetAsync(TabsRoute(_fixture.Seed.PortalId));

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        IReadOnlyList<TabListItemDto> rows = await ReadListAsync(response);

        rows.Select(row => row.TabId).Should().Contain(
            new[] { first, second },
            "administration is not expressed as a grant row, so it cannot be filtered by one");
    }

    /// <summary>An unknown tenant answers <c>404 Not Found</c>, because the route names the tenant itself.</summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task ListTabs_ForAnUnknownTenant_ReturnsNotFound()
    {
        using HttpClient client = await _fixture.CreateHostClientAsync();

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
        using HttpClient client = await _fixture.CreateHostClientAsync();
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
        using HttpClient client = await _fixture.CreateHostClientAsync();

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
        using HttpClient client = await _fixture.CreateHostClientAsync();

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

        using HttpClient client = await MemberClientAsync();

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

        using HttpClient client = await MemberClientAsync();

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

        using HttpClient client = await MemberClientAsync();

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

        using HttpClient client = await MemberClientAsync();

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

        using HttpClient client = await MemberClientAsync();

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
    public async Task GetTab_WhenUnknown_ReturnsNotFoundForTheHost()
    {
        using HttpClient client = await _fixture.CreateHostClientAsync();

        using HttpResponseMessage response = await client.GetAsync(TabRoute(UnknownTabId));

                // MIGRATION: 404, NOT 403, FOR AN IDENTIFIER THAT NAMES NOTHING - and only for a caller who
        // administers the tenant. The permission service establishes that the item exists before it resolves
        // the caller, so an unknown identifier used to be refused even for a host account and the endpoint
        // never ran; runtime testing recorded the console telling an operator "the authenticated caller is
        // not permitted to perform this operation" for a module that simply did not exist, which points at
        // the wrong repair and disagrees with the 404 the portal, user and role endpoints give for the same
        // class of fault. An unprivileged caller still receives 403, so nothing here is an existence oracle.
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    /// <summary>A page belonging to another tenant is refused on the same grounds.</summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task GetTab_ForAPageInAnotherTenant_ReturnsForbidden()
    {
        using HttpClient host = await _fixture.CreateHostClientAsync();
        int isolatedPortalId = await CreateIsolatedPortalAsync(host);
        int foreignTabId = await CreateTabAsync("IForeign" + Suffix(), portalId: isolatedPortalId);

        // The token is scoped to the seeded tenant, so the gate evaluates the page against that tenant and
        // finds it does not belong to it.
        using HttpResponseMessage response = await host.GetAsync(TabRoute(foreignTabId));

                // 403, AND DELIBERATELY NOT 404, BECAUSE THE PAGE EXISTS - it simply belongs to another tenant. The
        // refusal for an identifier that names NOTHING was changed to 404 so the console can present a
        // not-found treatment, and that change had to stop precisely here: the permission service reports a
        // separate reason code for a foreign-tenant item, and the authorisation handler admits only the
        // genuine non-existence code. Collapsing the two codes lets a tenant administrator read another
        // tenant's row, which this assertion exists to prevent.
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

        using HttpClient client = await _fixture.CreateHostClientAsync();

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
    public async Task UpdateTab_BeyondAColumnWidth_ReturnsValidationProblem(string member, int limit)
    {
        int tabId = await CreateTabAsync("IWide" + Suffix());

        using HttpClient client = await _fixture.CreateHostClientAsync();

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
    /// Active and unknown URI schemes are refused by the API and never reach the stored page.
    /// </summary>
    /// <param name="url">Unsafe or unsupported link target.</param>
    /// <returns>A task representing the test.</returns>
    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("data:text/html,<script>alert(1)</script>")]
    [InlineData("vbscript:msgbox(1)")]
    [InlineData("ftp://example.test/file")]
    public async Task UpdateTab_WithAnUnsupportedLinkScheme_ReturnsBadRequestAndStoresNothing(string url)
    {
        int tabId = await CreateTabAsync("IUnsafeUrl" + Suffix());

        using HttpClient client = await _fixture.CreateHostClientAsync();

        using HttpResponseMessage response = await client.PutAsJsonAsync(
            TabRoute(tabId),
            new UpdateTabRequest
            {
                TabName = "IUnsafeUrlName",
                Url = url,
            },
            ApiTestFixture.Json);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        IReadOnlyDictionary<string, string[]> errors = await ReadValidationErrorsAsync(response);
        errors.Keys.Should().Contain(nameof(UpdateTabRequest.Url));

        using HttpResponseMessage read = await client.GetAsync(TabRoute(tabId));
        read.StatusCode.Should().Be(HttpStatusCode.OK);

        TabDetailDto persisted = await ReadDetailAsync(read);
        persisted.Url.Should().BeNull();
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

        using HttpClient client = await _fixture.CreateHostClientAsync();

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

        using HttpClient client = await _fixture.CreateHostClientAsync();

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

        using HttpClient client = await _fixture.CreateHostClientAsync();

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

        using HttpClient client = await _fixture.CreateHostClientAsync();

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

        using HttpClient client = await _fixture.CreateHostClientAsync();

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

        using HttpClient client = await _fixture.CreateHostClientAsync();

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

        using HttpClient client = await _fixture.CreateHostClientAsync();

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

        using HttpClient client = await _fixture.CreateHostClientAsync();

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
        using HttpClient client = await _fixture.CreateHostClientAsync();

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
    public async Task UpdateTab_WhenUnknown_ReturnsNotFound()
    {
        using HttpClient client = await _fixture.CreateHostClientAsync();

        using HttpResponseMessage response = await client.PutAsJsonAsync(
            TabRoute(UnknownTabId),
            NewUpdateRequest("IGhost" + Suffix()),
            ApiTestFixture.Json);

                // MIGRATION: 404, NOT 403, FOR AN IDENTIFIER THAT NAMES NOTHING - and only for a caller who
        // administers the tenant. The permission service establishes that the item exists before it resolves
        // the caller, so an unknown identifier used to be refused even for a host account and the endpoint
        // never ran; runtime testing recorded the console telling an operator "the authenticated caller is
        // not permitted to perform this operation" for a module that simply did not exist, which points at
        // the wrong repair and disagrees with the 404 the portal, user and role endpoints give for the same
        // class of fault. An unprivileged caller still receives 403, so nothing here is an existence oracle.
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    /// <summary>An authenticated caller holding a view grant but no edit grant cannot write.</summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task UpdateTab_AsAPlainMemberHoldingOnlyAViewGrant_ReturnsForbidden()
    {
        string name = "IView" + Suffix();
        int tabId = await CreateTabAsync(name);
        await GrantAsync(tabId, _fixture.Seed.TabViewPermissionId, _fixture.Seed.RegisteredRoleId, allowAccess: true);

        using HttpClient client = await MemberClientAsync();

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

        using HttpClient client = await MemberClientAsync();

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

        using HttpClient client = await _fixture.CreateHostClientAsync();

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

    /// <summary>
    /// Every write verb the legacy screens offered and this API does not is unroutable, on both page
    /// addresses.
    /// </summary>
    /// <param name="method">The verb to attempt.</param>
    /// <param name="template">The address to attempt it against, with the seeded identifiers substituted.</param>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// <para>
    /// THIS IS THE STRUCTURAL ASSERTION THE WHOLE FILE EXISTS FOR. The page surface is deliberately three
    /// routes - list a tenant's pages, read one page, update one page - and nothing else. A later change that
    /// "helpfully" restores page creation or page deletion would widen the migration's scope silently, because
    /// nothing else in the suite would notice: every other test here would keep passing. This test is the
    /// tripwire, so it asserts the negative directly rather than inferring it from the absence of a positive.
    /// </para>
    /// <para>
    /// <c>404 Not Found</c> is accepted alongside <c>405 Method Not Allowed</c> because which one the router
    /// produces depends on whether any action is registered for the address at all - the bare page collection
    /// address is registered for no verb and answers <c>404</c>, while the two addresses that do carry actions
    /// answer <c>405</c> for a verb none of them declares. The assertion worth making is that no write reaches
    /// a handler, not which refusal the router happens to select, so both are admitted and the two success
    /// statuses a write would produce are excluded explicitly.
    /// </para>
    /// <para>
    /// The caller is authenticated on purpose. An anonymous request to these same addresses answers
    /// <c>401 Unauthorized</c>, which would satisfy a naive "not a success status" assertion while proving
    /// nothing about routing: the challenge is issued before the router's verdict is visible. Authenticating
    /// first removes that confound, so a refusal here is a statement about the route table.
    /// </para>
    /// </remarks>
    // MIGRATION: five write paths existed across the two legacy page screens and not one is reproduced.
    // Creation was ManageTabs.ascx.vb:L314 (`objTabs.AddTab(objTab)`), which is why the page transfer folder
    // declares three types and deliberately no create request. Deletion was Tabs.ascx.vb behind a
    // special-page guard. Reordering was four separate commands, each a call to the seven-argument
    // `UpdatePortalTabOrder` at TabController.vb:L550 whose trailing `Optional ByVal NewTab As Boolean = False`
    // is exactly the implicit-default idiom the target refuses to carry forward; placement is now stated as
    // named properties on the update request instead. Design propagation to child pages, the restoring and
    // emptying of soft-deleted pages, and the page export and import screens are absent with the features they
    // belong to. Every one of those absences is a scope decision, so each is asserted rather than assumed.
    [Theory]
    [InlineData("POST", "/api/v1/tabs")]
    [InlineData("PUT", "/api/v1/tabs")]
    [InlineData("PATCH", "/api/v1/tabs")]
    [InlineData("DELETE", "/api/v1/tabs")]
    [InlineData("POST", "/api/v1/tabs/{tabId}")]
    [InlineData("PATCH", "/api/v1/tabs/{tabId}")]
    [InlineData("DELETE", "/api/v1/tabs/{tabId}")]
    [InlineData("POST", "/api/v1/portals/{portalId}/tabs")]
    [InlineData("PUT", "/api/v1/portals/{portalId}/tabs")]
    [InlineData("PATCH", "/api/v1/portals/{portalId}/tabs")]
    [InlineData("DELETE", "/api/v1/portals/{portalId}/tabs")]
    public async Task Tabs_DeclareNoCreateOrDeleteSurface(string method, string template)
    {
        using HttpClient client = await _fixture.CreateHostClientAsync();

        using HttpRequestMessage request = new(new HttpMethod(method), Address(template));
        request.Content = JsonContent.Create(
            new { tabName = "IShouldNotBeRoutable" + Suffix() },
            options: ApiTestFixture.Json);

        using HttpResponseMessage response = await client.SendAsync(request);

        response.StatusCode.Should().BeOneOf(
            [HttpStatusCode.NotFound, HttpStatusCode.MethodNotAllowed],
            "{0} {1} must reach no handler",
            method,
            template);

        response.StatusCode.Should().NotBe(
            HttpStatusCode.Created,
            "a page creation surface is out of scope and must not appear");
        response.StatusCode.Should().NotBe(
            HttpStatusCode.NoContent,
            "a page deletion surface is out of scope and must not appear");
    }

    /// <summary>
    /// None of the page operations that belong to excluded features is addressable, under any verb.
    /// </summary>
    /// <param name="method">The verb to attempt.</param>
    /// <param name="template">The address to attempt it against.</param>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// These addresses are spelled the way a well-meaning extension would spell them, which is the point: the
    /// risk this test guards against is not a typo but a plausible addition. Page reordering, propagating a
    /// design to child pages, propagating grants to child pages, restoring or emptying soft-deleted pages,
    /// mutating a page's grants, and page export and import are each excluded, and each exclusion is a
    /// deliberate scope boundary rather than an unfinished edge.
    /// </remarks>
    // MIGRATION: page export and import are NOT the module export and import that this API does expose. The
    // legacy pair Website/admin/Tabs/export.ascx and import.ascx transferred PAGES and lies outside this
    // migration; the pair on the module surface transfers MODULE CONTENT and is in scope. Conflating them is
    // the specific mistake this test forecloses, which is why the page-shaped addresses are asserted absent
    // here rather than left to inference from the module suite's presence.
    [Theory]
    [InlineData("POST", "/api/v1/tabs/{tabId}/move")]
    [InlineData("PUT", "/api/v1/tabs/{tabId}/order")]
    [InlineData("POST", "/api/v1/tabs/{tabId}/copy-design-to-children")]
    [InlineData("POST", "/api/v1/tabs/{tabId}/copy-permissions-to-children")]
    [InlineData("POST", "/api/v1/tabs/{tabId}/restore")]
    [InlineData("POST", "/api/v1/tabs/{tabId}/export")]
    [InlineData("POST", "/api/v1/tabs/import")]
    [InlineData("PUT", "/api/v1/tabs/{tabId}/permissions")]
    [InlineData("POST", "/api/v1/portals/{portalId}/tabs/deleted")]
    [InlineData("DELETE", "/api/v1/portals/{portalId}/tabs/deleted")]
    public async Task Tabs_DeclareNoSurfaceForExcludedPageFeatures(string method, string template)
    {
        using HttpClient client = await _fixture.CreateHostClientAsync();

        using HttpRequestMessage request = new(new HttpMethod(method), Address(template));
        request.Content = JsonContent.Create(new { }, options: ApiTestFixture.Json);

        using HttpResponseMessage response = await client.SendAsync(request);

        response.StatusCode.Should().BeOneOf(
            [HttpStatusCode.NotFound, HttpStatusCode.MethodNotAllowed],
            "{0} {1} belongs to an excluded feature and must reach no handler",
            method,
            template);
    }

    /// <summary>
    /// Renaming a page to a name a sibling already uses is accepted, because the legacy duplicate-path guard
    /// never ran on an edit.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// <para>
    /// The measurement behind this: the duplicate-path refusal sat at
    /// <c>Website/admin/Tabs/ManageTabs.ascx.vb</c> L279 inside <c>If String.IsNullOrEmpty(strAction)</c>,
    /// while the edit branch was entered at L304 under <c>If strAction = "edit"</c>. The two conditions are
    /// mutually exclusive, so the guard belonged to the create path exclusively - and the create path is not
    /// exposed here. Reproducing the refusal on an update would therefore be STRICTER than the application
    /// being migrated, which MC4 forbids just as firmly as it forbids being laxer.
    /// </para>
    /// <para>
    /// The assertion is written as an exclusion of <c>409 Conflict</c> as well as an expectation of
    /// <c>200 OK</c>, because a conflict status is precisely what a reader who had not measured the legacy
    /// branch condition would add.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task UpdateTab_WithANameASiblingAlreadyUses_IsAcceptedRatherThanConflicting()
    {
        string sharedName = "ITwin" + Suffix();
        await CreateTabAsync(sharedName);
        int secondTabId = await CreateTabAsync("IOther" + Suffix());

        using HttpClient client = await _fixture.CreateHostClientAsync();

        using HttpResponseMessage response = await client.PutAsJsonAsync(
            TabRoute(secondTabId),
            NewUpdateRequest(sharedName),
            ApiTestFixture.Json);

        response.StatusCode.Should().NotBe(
            HttpStatusCode.Conflict,
            "the legacy duplicate-path guard was reachable only from the create path, which is out of scope");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        TabDetailDto updated = await ReadDetailAsync(response);
        updated.TabName.Should().Be(sharedName);
    }

    /// <summary>
    /// The page whose identifier is the identity seed reads back normally, so zero is never mistaken for
    /// "unspecified".
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// <para>
    /// <c>Tabs.TabID</c> is declared <c>IDENTITY (0, 1)</c> at
    /// <c>Website/Providers/DataProviders/SqlDataProvider/01.00.00.SqlDataProvider</c> L140, so the first page
    /// an installation ever creates carries zero. Zero is therefore a page, not a sentinel, and three separate
    /// layers have to agree about that for this request to succeed: the route constraint has to admit it, the
    /// authorisation handler has to parse it as a scope identifier rather than treating the absence of a
    /// truthy value as no value at all, and the service has to look it up.
    /// </para>
    /// <para>
    /// The seeded tenant's root page holds exactly this identifier, so the assertion is made against a real
    /// row rather than a constructed one - and the identifier is read back off the representation, which is
    /// what proves it survived the round trip instead of being coerced somewhere along it.
    /// </para>
    /// </remarks>
    // MIGRATION: the legacy tenant-resolution procedure got this wrong, and the defect is measurable. At
    // 01.00.00.SqlDataProvider:L4587 `GetPortalSettings` guarded its ownership check with `if @TabID <> 0`,
    // treating zero as "no page requested" in the very schema whose page identity column seeds at zero. The
    // consequence was that a request for page zero fell through to the substitution branch at L4603 and was
    // answered with `min(Tabs.TabID)` instead. The target has no such guard and no such substitution: zero
    // addresses page zero.
    [Fact]
    public async Task GetTab_ForTheIdentitySeedIdentifier_TreatsZeroAsARealPage()
    {
        _fixture.Seed.RootTabId.Should().Be(
            0,
            "the seeded root page must occupy the identity seed for this assertion to be about zero at all");

        using HttpClient client = await _fixture.CreateHostClientAsync();

        using HttpResponseMessage response = await client.GetAsync(TabRoute(0));

        response.StatusCode.Should().Be(
            HttpStatusCode.OK,
            "page zero is a real page and must never be rejected as though the identifier were absent");

        TabDetailDto detail = await ReadDetailAsync(response);
        detail.TabId.Should().Be(0);
        detail.TabName.Should().Be(IntegrationSeed.RootTabName);
    }

    /// <summary>
    /// The tenant whose identifier is the identity seed lists its pages normally, so minus one is never
    /// mistaken for the legacy absent-integer sentinel.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// <c>Portals.PortalID</c> is declared <c>IDENTITY (-1, 1)</c> at
    /// <c>01.00.00.SqlDataProvider</c> L77, and <c>Library/Components/Shared/Null.vb</c> L41-L43 defines the
    /// absent-integer sentinel as <c>-1</c>. The same value therefore means both "the first tenant ever
    /// created" and "no value", and only the column it appears in distinguishes them. A route that rejected
    /// <c>-1</c>, or a mapper that turned it into <see langword="null"/>, would make the seeded tenant
    /// unreachable while every test using a later identifier kept passing.
    /// </remarks>
    [Fact]
    public async Task ListTabs_ForTheIdentitySeedTenant_TreatsMinusOneAsARealTenant()
    {
        _fixture.Seed.PortalId.Should().Be(
            -1,
            "the seeded tenant must occupy the identity seed for this assertion to be about minus one at all");

        using HttpClient client = await _fixture.CreateHostClientAsync();

        using HttpResponseMessage response = await client.GetAsync(TabsRoute(-1));

        response.StatusCode.Should().Be(
            HttpStatusCode.OK,
            "minus one is the first tenant identifier this schema issues, not an absent value");

        List<TabListItemDto> tabs = await ReadListAsync(response);
        tabs.Should().NotBeEmpty();
    }

    /// <summary>
    /// A tenant identifier of zero is parsed and looked up like any other, and answers absence by naming the
    /// identifier it could not find.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// Zero is the second value that has to be proved harmless on this surface: it is the identifier the
    /// shipped default tenant row was inserted with in the legacy product, and it is also what an uninitialised
    /// integer holds. The refusal naming zero is what distinguishes "looked it up and found nothing" from
    /// "discarded it as falsy" - the two are indistinguishable from the status code alone, which is why the
    /// payload is inspected rather than only the status.
    /// </remarks>
    [Fact]
    public async Task ListTabs_ForATenantIdentifierOfZero_LooksItUpRatherThanDiscardingIt()
    {
        using HttpClient client = await _fixture.CreateHostClientAsync();

        using HttpResponseMessage response = await client.GetAsync(TabsRoute(0));

        response.StatusCode.Should().Be(
            HttpStatusCode.NotFound,
            "no tenant bears identifier zero in this database, which is an absence rather than a rejection");

        string payload = await response.Content.ReadAsStringAsync();
        payload.Should().Contain("urn:dnnmigration:error:tab.portal_not_found");
        payload.Should().Contain(
            "0",
            "naming the identifier is what proves it was parsed and searched for");
    }

    /// <summary>
    /// A page that belongs to no tenant keeps its absent tenant as a null, rather than acquiring a sentinel.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// <c>Tabs.PortalID</c> is nullable (<c>01.00.00.SqlDataProvider</c> L142) and the shipped seed used it:
    /// the third seeded row, the host-level page, was inserted with an explicit <c>NULL</c> tenant at L7140.
    /// The representation must carry that absence as an absence. Mapping it to the legacy absent-integer
    /// sentinel would make a host-level page indistinguishable from a page owned by the first tenant this
    /// schema issues, which is the identity seed and therefore also minus one.
    /// </remarks>
    [Fact]
    public async Task GetTab_ForAPageWithNoTenant_PreservesTheAbsenceRatherThanASentinel()
    {
        int hostTabId = await CreateTenantlessTabAsync("IHostPage" + Suffix());

        using HttpClient client = await _fixture.CreateHostClientAsync();

        using HttpResponseMessage response = await client.GetAsync(TabRoute(hostTabId));

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        TabDetailDto detail = await ReadDetailAsync(response);
        detail.PortalId.Should().BeNull("a page with no tenant has no tenant, not tenant minus one");

        string payload = await response.Content.ReadAsStringAsync();
        payload.Should().Contain(
            "\"portalId\":null",
            "the absence has to survive serialisation as an explicit null member");
        payload.Should().NotContain(
            "\"portalId\":-1",
            "the legacy absent-integer sentinel must not stand in for a genuine null");
    }

    /// <summary>
    /// A page that belongs to no tenant is refused to a tenant administrator, so a host-level page is not
    /// visible from inside every tenant.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// The host account still reads it - the companion test above - because a host account holds everything.
    /// What is asserted here is that holding a tenant's administration does not, by itself, reach outside that
    /// tenant.
    /// </remarks>
    // MIGRATION: this is a deliberate divergence and the legacy behaviour is measurable, so it is recorded
    // rather than absorbed. `GetPortalSettings` verified page ownership with
    // `where TabId = @TabId and ( Portals.PortalID = @PortalID or Tabs.PortalId is null )`
    // (01.00.00.SqlDataProvider:L4593). The trailing disjunct made EVERY page with a null tenant visible from
    // EVERY tenant, which is a cross-tenant read in a multi-tenant product. The target does not reproduce it:
    // a host-level page is reachable by an installation-wide account and by nobody else. The divergence
    // narrows access rather than widening it, and it is asserted here so that a later change which restores
    // the disjunct fails a test instead of quietly reopening the path.
    [Fact]
    public async Task GetTab_ForAPageWithNoTenant_RefusesATenantAdministrator()
    {
        int hostTabId = await CreateTenantlessTabAsync("IHostPage" + Suffix());

        using HttpClient client = await _fixture.CreateAdministratorClientAsync();

        using HttpResponseMessage response = await client.GetAsync(TabRoute(hostTabId));

        response.StatusCode.Should().Be(
            HttpStatusCode.Forbidden,
            "administering one tenant must not confer a read on a page that belongs to no tenant");
    }

    /// <summary>
    /// Submitted empty text is stored and returned as empty text, not converted into an absent value.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// <para>
    /// <c>Library/Components/Shared/Null.vb</c> L71-L73 defines the absent-string sentinel as the EMPTY
    /// STRING rather than as a null, and the shipped seed relies on the distinction: the host-level page at
    /// L7140 was inserted with <c>''</c> for its mobile name while its tenant column took an explicit
    /// <c>NULL</c> on the same row. Empty and absent are therefore two states the data actually holds, and a
    /// serialiser configured to omit empty or default members would collapse them into one.
    /// </para>
    /// <para>
    /// The assertion is made twice - once on the write's own representation and once on a fresh read - because
    /// a value can survive the response and still be lost on the way to storage, and only the second read
    /// distinguishes those.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task UpdateTab_WithEmptyText_KeepsItEmptyRatherThanAbsent()
    {
        int tabId = await CreateTabAsync("IEmpty" + Suffix());

        using HttpClient client = await _fixture.CreateHostClientAsync();

        Dictionary<string, object?> body = new(StringComparer.Ordinal)
        {
            ["tabName"] = "IEmptyText" + Suffix(),
            ["title"] = string.Empty,
            ["url"] = string.Empty,
            ["iconFile"] = string.Empty,
        };

        using HttpResponseMessage written = await client.PutAsJsonAsync(
            TabRoute(tabId),
            body,
            ApiTestFixture.Json);

        written.StatusCode.Should().Be(HttpStatusCode.OK);

        string writtenPayload = await written.Content.ReadAsStringAsync();
        writtenPayload.Should().Contain("\"title\":\"\"");
        writtenPayload.Should().Contain("\"url\":\"\"");
        writtenPayload.Should().Contain("\"iconFile\":\"\"");

        using HttpResponseMessage reread = await client.GetAsync(TabRoute(tabId));

        reread.StatusCode.Should().Be(HttpStatusCode.OK);

        TabDetailDto detail = await ReadDetailAsync(reread);
        detail.Title.Should().Be(string.Empty, "empty text is a value, and it was the value submitted");
        detail.Url.Should().Be(string.Empty);
        detail.IconFile.Should().Be(string.Empty);
    }

    /// <summary>
    /// The legacy absent-date sentinel is refused with the storable-range message rather than written to the
    /// column.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// <c>Null.vb</c> L66-L68 defines the absent-date sentinel as <c>Date.MinValue</c>, and
    /// <c>ManageTabs.ascx.vb</c> L288-L296 assigned exactly that value whenever a date box was left blank. The
    /// value cannot be stored in the column the migration is bound to, whose range begins in 1753, so the
    /// sentinel is not merely unnecessary here - it is unrepresentable. Refusing it with a message that names
    /// the range is what tells a caller to send an absent value instead of a minimum one.
    /// </remarks>
    // MIGRATION: the sentinel is not translated, and the reason is the Option Strict asymmetry recorded at
    // Website/release.config:L125, `<compilation debug="false" strict="false">`. The legacy screens compiled
    // with Option Strict OFF, so a blank box coercing itself into a minimum date raised nothing at all. Under
    // the target's explicit conversions the same value has to be judged, and it is judged as out of range
    // rather than silently clamped - a clamp would store a date the caller never chose.
    [Fact]
    public async Task UpdateTab_WithTheLegacyAbsentDateSentinel_IsRefusedAsUnstorable()
    {
        int tabId = await CreateTabAsync("ISentinelDate" + Suffix());

        using HttpClient client = await _fixture.CreateHostClientAsync();

        Dictionary<string, object?> body = new(StringComparer.Ordinal)
        {
            ["tabName"] = "ISentinelDate" + Suffix(),

            // DateTime.MinValue is precisely the legacy absent-date sentinel, written in the wire form the
            // caller would send it in.
            ["startDate"] = "0001-01-01T00:00:00",
        };

        using HttpResponseMessage response = await client.PutAsJsonAsync(
            TabRoute(tabId),
            body,
            ApiTestFixture.Json);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        IReadOnlyDictionary<string, string[]> errors = await ReadValidationErrorsAsync(response);

        errors.Should().ContainKey(
            "StartDate",
            "the refusal has to name the member that carried the unstorable value");
        errors["StartDate"].Should().Contain(
            message => message.Contains("1753", StringComparison.Ordinal),
            "the message has to state the range the column can hold");
    }

    /// <summary>
    /// Omitting both dates stores no dates, rather than storing the legacy minimum-date sentinel.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// The companion to the test above, and the half that pins the replacement rather than the rejection.
    /// Absence is expressed by omission and lands in the column as a genuine <c>NULL</c>, which the stored
    /// state is queried for directly - the representation alone could not distinguish a null column from a
    /// column holding a value the projection chose not to emit.
    /// </remarks>
    [Fact]
    public async Task UpdateTab_WithNoDates_StoresNoDates()
    {
        int tabId = await CreateTabAsync("INoDates" + Suffix());

        using HttpClient client = await _fixture.CreateHostClientAsync();

        using HttpResponseMessage response = await client.PutAsJsonAsync(
            TabRoute(tabId),
            NewUpdateRequest("INoDates" + Suffix()),
            ApiTestFixture.Json);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        TabDetailDto detail = await ReadDetailAsync(response);
        detail.StartDate.Should().BeNull();
        detail.EndDate.Should().BeNull();

        int rowsWithNullDates = await _fixture.Database.ScalarAsync<int>(
            """
            SELECT COUNT(*)
            FROM [dbo].[Tabs]
            WHERE [TabID] = @tabId AND [StartDate] IS NULL AND [EndDate] IS NULL;
            """,
            new Dictionary<string, object?> { ["tabId"] = tabId });

        rowsWithNullDates.Should().Be(
            1,
            "an omitted date must land as a null column, never as the legacy minimum-date sentinel");
    }

    /// <summary>
    /// The two page surfaces answer a host name that matches no configured alias DIFFERENTLY, and the
    /// difference is exactly the declared mark: the collection is refused, the single page is served.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// <para>
    /// SEC-006 REWROTE THIS FACT. Both arms used to assert <c>200</c>, on the reasoning that the collection
    /// route names its tenant in the address and therefore never needs the alias table. Naming a tenant in a
    /// route is a claim about which one to act on, not evidence of arrival at one, so that arm is now a
    /// refusal: the collection requires a host name that resolves.
    /// </para>
    /// <para>
    /// THE SECOND ARM IS UNCHANGED, AND IT IS WHY THIS FACT IS WORTH KEEPING AS A PAIR. The single-page routes
    /// carry the tenant-optional mark, whose stated justification is that they take their tenant from the
    /// caller's signed token. That mark is DECLARED on the endpoint and inventoried by a test, so the
    /// exemption is auditable - which is the whole difference between it and the inferred exemption that was
    /// removed. Asserting the two together is what proves the boundary now follows the mark rather than
    /// following the shape of the route.
    /// </para>
    /// <para>
    /// The property that no substring host reaches another tenant's page holds through both arms: the
    /// collection is refused outright, and the single page is decided against the token's own portal, so the
    /// mechanism the legacy substring predicate widened is on neither path.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Tabs_FromAHostNameThatMatchesNoAlias_RefuseTheCollectionAndServeTheDeclaredExemption()
    {
        using HttpClient client = await _fixture.CreateHostClientAsync("no-such-alias.invalid");

        using HttpResponseMessage listed = await client.GetAsync(TabsRoute(_fixture.Seed.PortalId));

        listed.StatusCode.Should().Be(
            HttpStatusCode.Forbidden,
            "the collection carries no tenant-optional mark, so it requires a host name that resolves");

        using HttpResponseMessage read = await client.GetAsync(TabRoute(_fixture.Seed.RootTabId));

        read.StatusCode.Should().Be(
            HttpStatusCode.OK,
            "the single-page route is marked tenant-optional because it takes its tenant from the token, and "
            + "a declared exemption is the only exemption that remains");
    }

    /// <summary>
    /// A host name that is a strict substring of a configured alias confers nothing: it reaches NO tenant at
    /// all, neither the one that owns the alias nor any other.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// <para>
    /// The host used here, <c>localhos</c>, is a strict substring of the seeded alias <c>localhost</c>, which
    /// is exactly the shape the legacy predicate matched. The caller is a genuine administrator of the seeded
    /// tenant, holding a token issued for it, so neither refusal below can be explained by the caller lacking
    /// authority - the address alone accounts for both.
    /// </para>
    /// <para>
    /// SEC-006: BOTH ARMS ARE NOW REFUSALS, AND THE FIRST ONE CHANGED. It used to assert that the caller's own
    /// tenant was still served, because a route naming its own portal was exempt from needing a resolved host
    /// name. That exemption is gone: a <c>portalId</c> segment is a claim about which tenant to act on, not
    /// evidence of arrival at one, so an unresolvable host name is now refused whichever tenant the route
    /// names. The property this fact exists to prove is unchanged and strictly stronger - a substring reached
    /// no other tenant before, and now reaches nothing.
    /// </para>
    /// </remarks>
    // MIGRATION: the legacy resolver matched `PortalAlias like '%' + @PortalAlias + '%'`
    // (01.00.00.SqlDataProvider:L4582) and then took `min(PortalID)` of whatever matched (L4580). Two defects
    // followed from those two lines together: a host that merely sat inside another tenant's alias resolved to
    // that tenant, and when several matched, the winner was decided by identifier order rather than by
    // correctness. The target matches an alias exactly, and this test states the consequence in terms a caller
    // can observe. Per Rule T5 the improvement is annotated rather than smuggled in - it is the one discovered
    // defect this migration corrects instead of merely recording, because carrying a cross-tenant
    // mis-resolution into new code would be worse than diverging from the original.
    [Fact]
    public async Task Tabs_FromAHostNameThatIsASubstringOfAnAlias_ReachNoOtherTenant()
    {
        using HttpClient host = await _fixture.CreateHostClientAsync();
        int foreignPortalId = await CreateIsolatedPortalAsync(host);

        // The credential is presented at the seeded alias and the REQUEST is addressed at the truncated
        // one, so the caller is unchanged and the host name is the only variable - which is what makes the
        // refusal below attributable to resolution rather than to who is asking.
        using HttpClient substringHost = await _fixture.CreateAdministratorClientAsync(
            ApiTestFixture.TestHost[..^1]);

        using HttpResponseMessage own = await substringHost.GetAsync(TabsRoute(_fixture.Seed.PortalId));

        own.StatusCode.Should().Be(
            HttpStatusCode.Forbidden,
            "the address resolves to no tenant, and naming one in the route does not supply the missing "
            + "arrival tenant even when the caller genuinely administers it");

        using HttpResponseMessage foreign = await substringHost.GetAsync(TabsRoute(foreignPortalId));

        foreign.StatusCode.Should().Be(
            HttpStatusCode.Forbidden,
            "a host name that is a substring of an alias must not resolve to the tenant that owns the alias");
    }

    /// <summary>
    /// A page in another tenant is refused, and the refusal carries no page at all - it is never answered with
    /// a different page.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// The refusal status alone does not make this assertion: what has to be excluded is a SUCCESSFUL answer
    /// carrying a substituted page, which is why the payload is inspected for the page members and for the
    /// success envelope, and why the substituted candidate - the requesting tenant's own lowest page - is
    /// named explicitly in the exclusion.
    /// </remarks>
    // MIGRATION: the legacy procedure substituted rather than refused. When its ownership check produced
    // nothing, `if @VerifyTabID is null` at 01.00.00.SqlDataProvider:L4601 fell through to
    // `select @TabID = min(Tabs.TabID)` at L4603, so a caller who asked for a page it was not entitled to was
    // handed the tenant's lowest-numbered page instead, with no error and no indication that the answer was
    // about a different page. That substitution is deliberately not reproduced. A request for a page outside
    // the caller's tenant is refused, which is the answer that cannot be mistaken for success.
    [Fact]
    public async Task GetTab_ForAPageInAnotherTenant_IsRefusedAndNeverSubstituted()
    {
        using HttpClient host = await _fixture.CreateHostClientAsync();
        int foreignPortalId = await CreateIsolatedPortalAsync(host);
        int foreignTabId = await CreateTabAsync("IForeign" + Suffix(), portalId: foreignPortalId);

        using HttpClient administrator = await _fixture.CreateAdministratorClientAsync();

        using HttpResponseMessage response = await administrator.GetAsync(TabRoute(foreignTabId));

                // 403, AND DELIBERATELY NOT 404, BECAUSE THE PAGE EXISTS - it simply belongs to another tenant. The
        // refusal for an identifier that names NOTHING was changed to 404 so the console can present a
        // not-found treatment, and that change had to stop precisely here: the permission service reports a
        // separate reason code for a foreign-tenant item, and the authorisation handler admits only the
        // genuine non-existence code. Collapsing the two codes lets a tenant administrator read another
        // tenant's row, which this assertion exists to prevent.
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);

        string payload = await response.Content.ReadAsStringAsync();
        payload.Should().NotContain(
            "\"tabId\"",
            "a refusal must carry no page, least of all a page the caller did not ask for");
        payload.Should().NotContain(
            "\"data\"",
            "the success envelope must not appear on a refusal");
    }

    /// <summary>
    /// A blank page name is refused with a complete RFC 7807 validation document that names the offending
    /// member and carries the legacy wording.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// <para>
    /// The companion theory above asserts only that a blank name is refused. This asserts WHAT the refusal
    /// says, which is the part a client actually binds to: every mandated member is present, the per-field
    /// dictionary names the member that failed, and the message is the one the legacy screen showed. A refusal
    /// that omitted the field name would still be a 400 and would be useless to a form.
    /// </para>
    /// <para>
    /// Note the key's casing. The dictionary is keyed by the MEMBER name rather than by the wire name, so the
    /// key is <c>TabName</c> where the submitted member was <c>tabName</c>. That asymmetry is asserted rather
    /// than corrected, because a client that guessed the wire spelling would fail to find its error.
    /// </para>
    /// </remarks>
    // MIGRATION: the wording is the legacy wording, with one deliberate removal. The markup literal at
    // Website/admin/Tabs/managetabs.ascx L36-L37 reads `ErrorMessage="<br>Tab Name Is Required"`, but the
    // resource entry that actually rendered - `valTabName.ErrorMessage` in
    // Website/admin/Tabs/App_LocalResources/ManageTabs.ascx.resx - overrides it with
    // `<br>Page Name Is Required`, and the resource wins at run time because the control carries
    // `resourcekey="valTabName.ErrorMessage"`. The words a user saw are therefore "Page Name Is Required", and
    // those are the words kept. The leading `<br>` is dropped: it is a fragment of HTML that existed only to
    // separate the message from the box it sat beside in a rendered page, and an RFC 7807 message is data for a
    // client to place, not markup to emit. Both halves of that decision are asserted here so neither can drift.
    [Fact]
    public async Task UpdateTab_WithABlankName_NamesTheMemberAndKeepsTheLegacyWording()
    {
        int tabId = await CreateTabAsync("IProblem" + Suffix());

        using HttpClient client = await _fixture.CreateHostClientAsync();

        using HttpResponseMessage response = await client.PutAsJsonAsync(
            TabRoute(tabId),
            new UpdateTabRequest { TabName = string.Empty },
            ApiTestFixture.Json);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        using System.Text.Json.JsonDocument document =
            System.Text.Json.JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        System.Text.Json.JsonElement problem = document.RootElement;

        foreach (string member in new[] { "type", "title", "status", "detail", "errors" })
        {
            problem.TryGetProperty(member, out _).Should().BeTrue(
                "an RFC 7807 validation document must carry its {0} member",
                member);
        }

        problem.GetProperty("status").GetInt32().Should().Be(400);

        IReadOnlyDictionary<string, string[]> errors = await ReadValidationErrorsAsync(response);

        errors.Should().ContainKey("TabName");
        errors["TabName"].Should().ContainSingle().Which.Should().Be(
            "Page Name Is Required",
            "the message is the legacy resource wording with the HTML separator removed");

        errors["TabName"].Should().NotContain(
            message => message.Contains("<br", StringComparison.Ordinal),
            "an RFC 7807 message is data, not markup");
    }

    /// <summary>
    /// A refusal produced inside the controller pipeline is served under the problem media type whether or
    /// not the caller asked for it.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// <para>
    /// MIGRATION: THIS FACT USED TO ASSERT THE OPPOSITE OF ITS FIRST HALF, AND THE BEHAVIOUR IT RECORDED WAS
    /// THE DEFECT. Every controller in this API declares that it produces <c>application/json</c>, and that
    /// declaration constrained every result the action pipeline formatted - including the automatic validation
    /// refusal - so a problem DOCUMENT went out under the media type for an ordinary payload unless the caller
    /// happened to negotiate otherwise. Refusals decided before the controller ran were unaffected and carried
    /// the problem media type, so one API answered the same kind of failure under two media types depending on
    /// which stage refused, and a client could not tell a problem document from a payload by its content type.
    /// </para>
    /// <para>
    /// Both halves are still asserted, and together they now state something stronger than the pair they
    /// replace: the media type does not depend on negotiation. Keeping the negotiated half matters because the
    /// obvious way to implement the fix - having callers ask for the problem type - would satisfy the first
    /// half for the wrong reason, and keeping the default half is what proves the controller's own
    /// <c>Produces</c> declaration no longer wins. The declaration is a filter that reassigns the result's
    /// content types, and at equal ordering a controller-scoped filter runs after a global one, so the
    /// correction only holds while the global filter is ordered to write last.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task UpdateTab_WhenRefused_ServesTheProblemDocumentUnderTheProblemMediaType()
    {
        int tabId = await CreateTabAsync("IMediaType" + Suffix());

        using HttpClient client = await _fixture.CreateHostClientAsync();

        using HttpResponseMessage defaulted = await client.PutAsJsonAsync(
            TabRoute(tabId),
            new UpdateTabRequest { TabName = string.Empty },
            ApiTestFixture.Json);

        defaulted.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        defaulted.Content.Headers.ContentType?.MediaType.Should().Be(
            ProblemMediaType,
            "a problem document is served as a problem document even though the controller declares that it "
            + "produces JSON, so a client can identify one by its content type alone");

        using HttpRequestMessage negotiated = new(HttpMethod.Put, TabRoute(tabId))
        {
            Content = JsonContent.Create(
                new UpdateTabRequest { TabName = string.Empty },
                options: ApiTestFixture.Json),
        };
        negotiated.Headers.Add("Accept", ProblemMediaType);

        using HttpResponseMessage requested = await client.SendAsync(negotiated);

        requested.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        requested.Content.Headers.ContentType?.MediaType.Should().Be(
            ProblemMediaType,
            "a caller that asks for the problem media type receives the same document under it, so the media "
            + "type is invariant to negotiation rather than a consequence of it");
    }

    /// <summary>
    /// An authorisation refusal is a problem document that discloses nothing about why it was refused.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// Refusals are produced before the controller runs, so they are not constrained by the controller's
    /// declared media type and always carry the problem type. What is asserted alongside the type is the
    /// silence: no stack trace, no source path, no member name, no connection string, no signing material and
    /// no reflected request content. A refusal that explained itself would describe the permission model to the
    /// caller probing it.
    /// </remarks>
    // MIGRATION: the legacy screens refused by navigation, not by status. ManageTabs.ascx.vb L586 tested
    // `PortalSecurity.IsInRoles(...)` twice and on failure ran
    // `Response.Redirect(NavigateURL("Access Denied"), True)`, so a refused caller received a 302 to a page
    // that rendered an apology. An API answers the caller instead of sending it elsewhere, so the redirect
    // becomes a plain 403 carrying a problem document - which is also why no test here follows a redirect.
    [Fact]
    public async Task GetTab_WhenRefused_AnswersAProblemDocumentThatDisclosesNothing()
    {
        int tabId = await CreateTabAsync("IRefused" + Suffix());

        using HttpClient client = await MemberClientAsync();

        using HttpResponseMessage response = await client.GetAsync(TabRoute(tabId));

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        response.Content.Headers.ContentType?.MediaType.Should().Be(ProblemMediaType);

        string payload = await response.Content.ReadAsStringAsync();
        payload.Should().Contain("urn:dnnmigration:error:auth.not_permitted");

        foreach (string forbidden in new[]
        {
            "stackTrace",
            "StackTrace",
            "at DnnMigration",
            ".cs:line",
            "Server=",
            "Password=",
            ApiTestFixture.SigningSecret,
            "SELECT ",
        })
        {
            payload.Should().NotContain(
                forbidden,
                "a refusal must not disclose {0}",
                forbidden);
        }
    }

    /// <summary>A caller-supplied correlation identifier is echoed back exactly once.</summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// The header is registered on the response before the rest of the pipeline is awaited, so it is present
    /// whatever the outcome. "Exactly once" is the part worth asserting: a stage that appended rather than
    /// overwrote would produce two values, and a client reading the first would still see the right one while
    /// a log correlating on the header saw two.
    /// </remarks>
    [Fact]
    public async Task GetTab_EchoesTheSuppliedCorrelationIdExactlyOnce()
    {
        string Supplied = ApiTestFixture.NewCorrelationId();

        using HttpClient client = await _fixture.CreateHostClientAsync();
        using HttpRequestMessage request = ApiTestFixture.WithCorrelationId(
            new HttpRequestMessage(HttpMethod.Get, TabRoute(_fixture.Seed.RootTabId)),
            Supplied);

        using HttpResponseMessage response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        CorrelationValues(response).Should().ContainSingle().Which.Should().Be(
            Supplied,
            "the identifier the caller supplied is the one every record of the request must carry");
    }

    /// <summary>A correlation identifier is generated when the caller supplies none.</summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task ListTabs_GeneratesACorrelationIdWhenTheCallerSuppliesNone()
    {
        using HttpClient client = await _fixture.CreateHostClientAsync();

        using HttpResponseMessage response = await client.GetAsync(TabsRoute(_fixture.Seed.PortalId));

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        CorrelationValues(response).Should().ContainSingle().Which.Should().NotBeNullOrWhiteSpace(
            "a request that arrives without an identifier still has to be correlatable");
    }

    /// <summary>
    /// An unusable correlation identifier is replaced rather than refused, so a bad header never costs the
    /// caller its request.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// The identifier is a diagnostic aid, not part of the request's meaning, so an unusable one is discarded
    /// and a fresh one issued in its place. Refusing the request instead would let a header a caller may not
    /// even know it is sending - injected by a proxy, for instance - break an otherwise valid call, and
    /// keeping the unusable value would put unbounded caller-supplied text into every log line about the
    /// request.
    /// </remarks>
    [Fact]
    public async Task GetTab_WithAnOverlongCorrelationId_ReplacesItRatherThanRefusingTheRequest()
    {
        string overlong = new('c', 300);

        using HttpClient client = await _fixture.CreateHostClientAsync();
        using HttpRequestMessage request = ApiTestFixture.WithCorrelationId(
            new HttpRequestMessage(HttpMethod.Get, TabRoute(_fixture.Seed.RootTabId)),
            overlong);

        using HttpResponseMessage response = await client.SendAsync(request);

        response.StatusCode.Should().Be(
            HttpStatusCode.OK,
            "an unusable diagnostic header must not turn a valid request into a refusal");

        string echoed = CorrelationValues(response).Should().ContainSingle().Subject;
        echoed.Should().NotBe(overlong, "an unusable identifier is discarded, not trimmed and kept");
        echoed.Should().NotBeNullOrWhiteSpace();
        echoed.Length.Should().BeLessThan(overlong.Length);
    }

    /// <summary>A correlation identifier is present on the refusal path too, not only on success.</summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// This is the case the header exists for. A caller reporting that it was refused has nothing else to
    /// quote, and an operator has nothing else to search on, so an identifier that appeared only on successful
    /// responses would be missing from exactly the requests anyone needs to look up.
    /// </remarks>
    [Fact]
    public async Task GetTab_CarriesACorrelationIdOnTheRefusalPath()
    {
        string Supplied = ApiTestFixture.NewCorrelationId();

        int tabId = await CreateTabAsync("ICorrelated" + Suffix());

        using HttpClient client = await MemberClientAsync();
        using HttpRequestMessage request = ApiTestFixture.WithCorrelationId(
            new HttpRequestMessage(HttpMethod.Get, TabRoute(tabId)),
            Supplied);

        using HttpResponseMessage response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        response.Content.Headers.ContentType?.MediaType.Should().Be(ProblemMediaType);

        CorrelationValues(response).Should().ContainSingle().Which.Should().Be(Supplied);
    }

    /// <summary>
    /// The page listing is an unpaged collection: it carries no paging block and therefore no paging
    /// sentinel.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// <para>
    /// The listing is complete by design, because each row carries its own parent, depth and ordinal and those
    /// are coherent only over the whole set - a tree split across pages severs parents from their children. So
    /// the envelope carries a null paging block rather than a page of one.
    /// </para>
    /// <para>
    /// The absence of a total is what makes the legacy unpaged sentinel unrepresentable here. Where a count did
    /// travel in the legacy code it travelled through a by-reference argument that carried <c>-1</c> to mean
    /// "not counted", and a page-count member that inherited that convention would report minus one items. No
    /// member exists to hold it, which is the strongest form the guarantee can take, and it is asserted on the
    /// raw payload because a typed binding would silently default a member that is not there.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task ListTabs_AnswersAnUnpagedEnvelopeWithNoPagingSentinel()
    {
        using HttpClient client = await _fixture.CreateHostClientAsync();

        using HttpResponseMessage response = await client.GetAsync(TabsRoute(_fixture.Seed.PortalId));

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        string payload = await response.Content.ReadAsStringAsync();
        payload.Should().NotContain(
            "\"totalCount\"",
            "an unpaged collection declares no total, so it can hold no unpaged sentinel");
        payload.Should().NotContain("\"pageIndex\"");
        payload.Should().NotContain("\"pageSize\"");
        payload.Should().NotContain(
            ":-1",
            "no negative numeric sentinel belongs anywhere in this representation");

        using System.Text.Json.JsonDocument document = System.Text.Json.JsonDocument.Parse(payload);

        document.RootElement.GetProperty("data").ValueKind.Should().Be(
            System.Text.Json.JsonValueKind.Array,
            "the collection travels under the success envelope's data member");
        document.RootElement.GetProperty("meta").ValueKind.Should().Be(
            System.Text.Json.JsonValueKind.Null,
            "an unpaged collection carries no paging block");
    }

    /// <summary>Two identical listings return the same pages in the same order.</summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// Legacy navigation order came from the stored ordinal, whose seeded values were 1, 23 and 1 across the
    /// three shipped pages - so ordinals are neither dense nor unique across the table, and an ordering that
    /// fell back on insertion order or on whatever the query planner returned would look stable in a small
    /// database and reorder itself in a large one. Determinism is asserted rather than a specific sequence,
    /// because every other test in this file may add pages and no absolute ordinal is anyone's contract.
    /// </remarks>
    [Fact]
    public async Task ListTabs_ReturnsADeterministicOrder()
    {
        using HttpClient client = await _fixture.CreateHostClientAsync();

        using HttpResponseMessage first = await client.GetAsync(TabsRoute(_fixture.Seed.PortalId));
        first.StatusCode.Should().Be(HttpStatusCode.OK);
        List<TabListItemDto> firstPass = await ReadListAsync(first);

        using HttpResponseMessage second = await client.GetAsync(TabsRoute(_fixture.Seed.PortalId));
        second.StatusCode.Should().Be(HttpStatusCode.OK);
        List<TabListItemDto> secondPass = await ReadListAsync(second);

        secondPass.Select(tab => tab.TabId).Should().Equal(
            firstPass.Select(tab => tab.TabId),
            "navigation order must not depend on how the rows happened to be read");
    }

    /// <summary>
    /// A reserved device name is refused whatever case it arrives in, because the legacy check ignored case.
    /// </summary>
    /// <param name="reservedName">The name to submit.</param>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// The rule exists because these names address devices rather than files on the platform the legacy
    /// product ran on, so a page bearing one produced a path that could not be written. The legacy test was
    /// <c>Regex.IsMatch(objTab.TabName, "^AUX$|^CON$|^LPT[1-9]$|^CON$|^COM[1-9]$|^NUL$",
    /// RegexOptions.IgnoreCase)</c> at <c>Website/admin/Tabs/ManageTabs.ascx.vb</c> L272, and the
    /// case-insensitivity flag is the part a reimplementation drops by accident - a case-sensitive port would
    /// admit every lower-case spelling while passing an upper-case test.
    /// </remarks>
    [Theory]
    [InlineData("CON")]
    [InlineData("con")]
    [InlineData("Con")]
    [InlineData("NUL")]
    [InlineData("nul")]
    [InlineData("AUX")]
    [InlineData("aux")]
    [InlineData("COM1")]
    [InlineData("com9")]
    [InlineData("LPT1")]
    [InlineData("lPt9")]
    public async Task UpdateTab_WithAReservedDeviceNameInAnyCase_IsRefused(string reservedName)
    {
        int tabId = await CreateTabAsync("IReserved" + Suffix());

        using HttpClient client = await _fixture.CreateHostClientAsync();

        using HttpResponseMessage response = await client.PutAsJsonAsync(
            TabRoute(tabId),
            NewUpdateRequest(reservedName),
            ApiTestFixture.Json);

        response.StatusCode.Should().Be(
            HttpStatusCode.BadRequest,
            "{0} is a reserved device name and the legacy check ignored case",
            reservedName);

        string payload = await response.Content.ReadAsStringAsync();
        payload.Should().Contain(
            "urn:dnnmigration:error:tab.name_reserved",
            "the refusal is reported as a stable named reason rather than as an opaque status");
    }

    /// <summary>
    /// A name that merely begins with, ends with or extends a reserved device name is accepted, because the
    /// legacy pattern was anchored at both ends.
    /// </summary>
    /// <param name="acceptableName">The name to submit.</param>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// This is the half that keeps the rule from being STRICTER than the application being migrated, which MC4
    /// forbids as firmly as it forbids being laxer. Every alternative in the legacy pattern carried both
    /// anchors, and the numeric alternatives admitted a single digit one through nine only - so a two-digit
    /// port number and a zero were both legitimate page names. A containment test written in place of the
    /// anchored one would refuse all of these and make perfectly ordinary pages unnameable.
    /// </remarks>
    [Theory]
    [InlineData("CONSOLE")]
    [InlineData("CONS")]
    [InlineData("NULL")]
    [InlineData("AUXILIARY")]
    [InlineData("COM10")]
    [InlineData("LPT0")]
    public async Task UpdateTab_WithANameThatOnlyResemblesAReservedName_IsAccepted(string acceptableName)
    {
        int tabId = await CreateTabAsync("IResembles" + Suffix());

        using HttpClient client = await _fixture.CreateHostClientAsync();

        using HttpResponseMessage response = await client.PutAsJsonAsync(
            TabRoute(tabId),
            NewUpdateRequest(acceptableName),
            ApiTestFixture.Json);

        response.StatusCode.Should().Be(
            HttpStatusCode.OK,
            "{0} matched no anchored alternative in the legacy pattern and must stay nameable",
            acceptableName);

        TabDetailDto detail = await ReadDetailAsync(response);
        detail.TabName.Should().Be(acceptableName);
    }

    /// <summary>
    /// A start date later than the end date is accepted, because the legacy screen compared neither date
    /// against the other.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// <para>
    /// The measured census is unambiguous: <c>Website/admin/Tabs/managetabs.ascx</c> declares exactly two date
    /// validators, at L276-L278 and L288-L290, and both carry <c>Operator="DataTypeCheck" Type="Date"</c>. A
    /// type check asks only whether the text parses as a date. There is no range operator, no comparison
    /// between the two boxes and no required-field validator on either, so an inverted pair reached the legacy
    /// controller and was stored.
    /// </para>
    /// <para>
    /// Adding the comparison would be the more obviously "correct" rule and is exactly what MC4 forbids: a
    /// rule the original did not have refuses input the original accepted, so stored data that the legacy
    /// application produced could no longer be re-saved through the new one. The acceptance is therefore
    /// asserted deliberately, and this test is what a future well-meaning tightening has to argue with.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task UpdateTab_WithAStartDateAfterTheEndDate_IsAcceptedAsLegacyDid()
    {
        int tabId = await CreateTabAsync("IInverted" + Suffix());

        using HttpClient client = await _fixture.CreateHostClientAsync();

        UpdateTabRequest request = NewUpdateRequest("IInverted" + Suffix());
        request.StartDate = new DateTime(2030, 1, 1, 0, 0, 0, DateTimeKind.Unspecified);
        request.EndDate = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Unspecified);

        using HttpResponseMessage response = await client.PutAsJsonAsync(
            TabRoute(tabId),
            request,
            ApiTestFixture.Json);

        response.StatusCode.Should().Be(
            HttpStatusCode.OK,
            "the legacy screen declared only a type check on each date, so an inverted pair was storable");

        TabDetailDto detail = await ReadDetailAsync(response);
        detail.StartDate.Should().Be(request.StartDate);
        detail.EndDate.Should().Be(request.EndDate);
    }

    /// <summary>
    /// Text that is not a date is refused, and the refusal names the member that could not be read.
    /// </summary>
    /// <param name="member">The date member to corrupt.</param>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// This is the target's equivalent of the two legacy type checks. The refusal happens while the body is
    /// being read rather than while it is being validated, so the key naming the member is the document
    /// pointer to it rather than the property name - a distinction a client binding errors back onto a form
    /// has to know about, which is why it is asserted rather than approximated.
    /// </remarks>
    // MIGRATION: under Option Strict OFF (Website/release.config:L125) the legacy code-behind could hand
    // arbitrary text to a date conversion at ManageTabs.ascx.vb:L289 and L294 and rely on the client-side
    // validator having filtered it first. The target reads the body under explicit conversions, so unparseable
    // text is refused at the boundary and never reaches a conversion at all.
    [Theory]
    [InlineData("startDate")]
    [InlineData("endDate")]
    public async Task UpdateTab_WithTextThatIsNotADate_IsRefusedNamingTheMember(string member)
    {
        int tabId = await CreateTabAsync("IBadDate" + Suffix());

        using HttpClient client = await _fixture.CreateHostClientAsync();

        Dictionary<string, object?> body = new(StringComparer.Ordinal)
        {
            ["tabName"] = "IBadDate" + Suffix(),
            [member] = "the fourteenth of never",
        };

        using HttpResponseMessage response = await client.PutAsJsonAsync(
            TabRoute(tabId),
            body,
            ApiTestFixture.Json);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        IReadOnlyDictionary<string, string[]> errors = await ReadValidationErrorsAsync(response);

        errors.Keys.Should().Contain(
            "$." + member,
            "the member that could not be read has to be named in the per-field dictionary");
    }

    /// <summary>
    /// An ordinary edit preserves the stored skin and container tokens instead of blanking them.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// <para>
    /// MIGRATION: skinning and containers are an explicit exclusion of this migration, so the page-update
    /// contract carries no member for either column. An earlier revision did carry both and assigned them
    /// from the request, which had two consequences. It published this endpoint as the supported way to
    /// change a page's skin - re-admitting the excluded subsystem through the write surface - and, because
    /// the projection is a WHOLE-ROW replacement rather than a patch, a caller that simply omitted the
    /// members deserialised them to null and therefore BLANKED an administrator's stored tokens on every
    /// unrelated edit.
    /// </para>
    /// <para>
    /// The stored values are read back from the database rather than from the response, because the response
    /// projection and the persistence projection are different code paths and only the second one settles
    /// what actually survived the write. The page name is asserted changed in the same breath, so a
    /// projection that had stopped writing ANYTHING could not pass by leaving both columns untouched.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task UpdateTab_PreservesTheStoredSkinAndContainerTokens()
    {
        string originalName = "IPresrv" + Suffix();
        int tabId = await CreateTabAsync(originalName);

        const string StoredSkin = "[G]Skins/Default/Measured.ascx";
        const string StoredContainer = "[G]Containers/Default/Measured.ascx";

        await _fixture.Database.ExecuteAsync(
            "UPDATE [dbo].[Tabs] SET [SkinSrc] = @skinSrc, [ContainerSrc] = @containerSrc "
            + "WHERE [TabID] = @tabId;",
            new Dictionary<string, object?>
            {
                ["skinSrc"] = StoredSkin,
                ["containerSrc"] = StoredContainer,
                ["tabId"] = tabId,
            });

        using HttpClient client = await _fixture.CreateHostClientAsync();

        string renamed = originalName + "R";

        using HttpResponseMessage response = await client.PutAsJsonAsync(
            TabRoute(tabId),
            NewUpdateRequest(renamed),
            ApiTestFixture.Json);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        string storedName = await _fixture.Database.ScalarAsync<string>(
            "SELECT [TabName] FROM [dbo].[Tabs] WHERE [TabID] = @tabId;",
            new Dictionary<string, object?> { ["tabId"] = tabId });

        storedName.Should().Be(
            renamed,
            "the edit itself must have been applied, or the preservation below would prove nothing");

        string storedSkin = await _fixture.Database.ScalarAsync<string>(
            "SELECT [SkinSrc] FROM [dbo].[Tabs] WHERE [TabID] = @tabId;",
            new Dictionary<string, object?> { ["tabId"] = tabId });

        string storedContainer = await _fixture.Database.ScalarAsync<string>(
            "SELECT [ContainerSrc] FROM [dbo].[Tabs] WHERE [TabID] = @tabId;",
            new Dictionary<string, object?> { ["tabId"] = tabId });

        storedSkin.Should().Be(
            StoredSkin,
            "the update contract carries no skin member, so an edit that mentions none must leave the "
            + "stored token exactly as it was rather than writing an absent value over it");
        storedContainer.Should().Be(
            StoredContainer,
            "the container token is preserved on the same footing and for the same reason");
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

    /// <summary>
    /// Removes every page EDIT grant in the seeded tenant, so a case can state its own starting point.
    /// </summary>
    /// <returns>A task representing the write.</returns>
    /// <remarks>
    /// <para>
    /// ⚠ REQUIRED BECAUSE THE LISTING'S POLICY IS NOW A TENANT-WIDE CAPABILITY QUESTION. Whether a caller is
    /// admitted to the page listing depends on whether ANY page of the tenant grants it EDIT, so a case
    /// asserting a refusal is asserting something about stored rows rather than about the caller's role. The
    /// suites share one database and xUnit gives no ordering guarantee within a collection, so a grant written
    /// by a sibling case would otherwise decide such an assertion's outcome by execution order.
    /// </para>
    /// <para>
    /// Only the EDIT key is cleared, and only for pages of the seeded tenant. View grants are left alone
    /// because they decide nothing here, and other tenants are left alone because the tenant is what the
    /// listing is anchored to. No case writes a grant and then depends on it surviving another case -
    /// <see cref="GrantAsync"/> deletes before inserting for the same reason - so clearing is safe.
    /// </para>
    /// </remarks>
    private async Task ClearEditGrantsAsync()
    {
        await _fixture.Database.ExecuteAsync(
            """
            DELETE tp
            FROM [dbo].[TabPermission] AS tp
            INNER JOIN [dbo].[Tabs] AS t ON t.[TabID] = tp.[TabID]
            WHERE t.[PortalID] = @portalId AND tp.[PermissionID] = @permissionId;
            """,
            new Dictionary<string, object?>
            {
                ["portalId"] = _fixture.Seed.PortalId,
                ["permissionId"] = _fixture.Seed.TabEditPermissionId,
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

    /// <summary>Signs in as the seeded plain member.</summary>
    /// <returns>An authenticated client holding no administrative role.</returns>
    private Task<HttpClient> MemberClientAsync() => _fixture.CreateUnprivilegedClientAsync();

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

    /// <summary>Resolves an address template against the seeded identifiers.</summary>
    /// <param name="template">A template that may name the tenant or the page.</param>
    /// <returns>A relative address.</returns>
    /// <remarks>
    /// Theory data has to be compile-time constant, but a meaningful refusal has to be asked of an address
    /// that really exists - a verb refused on an address nobody could reach would prove nothing. Substituting
    /// the seeded identifiers here is what lets the data stay declarative while the request stays real.
    /// </remarks>
    private Uri Address(string template) => new(
        template
            .Replace("{portalId}", Route(_fixture.Seed.PortalId), StringComparison.Ordinal)
            .Replace("{tabId}", Route(_fixture.Seed.RootTabId), StringComparison.Ordinal),
        UriKind.Relative);

    /// <summary>
    /// Inserts a page that belongs to no tenant, which the API exposes no route to create.
    /// </summary>
    /// <param name="tabName">The page name, kept free of non-word characters so its path is predictable.</param>
    /// <returns>The new page identifier.</returns>
    /// <remarks>
    /// The shared page-creation helper cannot produce this row: it substitutes the seeded tenant whenever no
    /// tenant is named, which is the right default everywhere else and exactly wrong here. A host-level page is
    /// defined by its tenant column being genuinely null, so the column is written as null explicitly.
    /// </remarks>
    private async Task<int> CreateTenantlessTabAsync(string tabName) =>
        await _fixture.Database.ScalarAsync<int>(
            """
            INSERT INTO [dbo].[Tabs]
                ([TabOrder], [PortalID], [TabName], [IsVisible], [ParentId], [Level], [DisableLink],
                 [Title], [IsDeleted], [TabPath], [IsSecure])
            VALUES (1, NULL, @tabName, 1, NULL, 0, 0, @tabName, 0, N'//' + @tabName, 0);
            SELECT CAST(SCOPE_IDENTITY() AS int);
            """,
            new Dictionary<string, object?> { ["tabName"] = tabName });

    /// <summary>Reads the per-field dictionary out of an RFC 7807 validation document.</summary>
    /// <param name="response">The refusal to read.</param>
    /// <returns>The offending members, each with its messages.</returns>
    /// <remarks>
    /// Read from the raw document rather than through a typed problem-details binding, because the keys are the
    /// subject of the assertions using this and a typed binding would normalise them. The comparer is ordinal
    /// so that a key differing only in case counts as a different key, which is the whole point of asserting
    /// the casing.
    /// </remarks>
    private static async Task<IReadOnlyDictionary<string, string[]>> ReadValidationErrorsAsync(
        HttpResponseMessage response)
    {
        using System.Text.Json.JsonDocument document =
            System.Text.Json.JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        document.RootElement.TryGetProperty("errors", out System.Text.Json.JsonElement errors)
            .Should()
            .BeTrue("a validation refusal must carry the per-field errors member");

        Dictionary<string, string[]> byMember = new(StringComparer.Ordinal);

        foreach (System.Text.Json.JsonProperty member in errors.EnumerateObject())
        {
            byMember[member.Name] = member.Value
                .EnumerateArray()
                .Select(message => message.GetString() ?? string.Empty)
                .ToArray();
        }

        return byMember;
    }

    /// <summary>Reads every correlation identifier a response carries, so duplicates are visible.</summary>
    /// <param name="response">The response to read.</param>
    /// <returns>The values, in the order the response carried them.</returns>
    /// <remarks>
    /// The shared fixture helper answers with the first value, which is the right shape for asserting WHICH
    /// identifier came back but cannot distinguish one value from two identical ones. A stage that appended
    /// instead of overwriting is exactly the defect worth catching, so the whole list is read here.
    /// </remarks>
    private static IReadOnlyList<string> CorrelationValues(HttpResponseMessage response) =>
        response.Headers.TryGetValues(ApiTestFixture.CorrelationIdHeader, out IEnumerable<string>? values)
            ? values.ToArray()
            : [];
}
