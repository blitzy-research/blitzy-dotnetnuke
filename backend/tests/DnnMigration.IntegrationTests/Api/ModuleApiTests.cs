using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DnnMigration.Application.Dtos.Module;
using DnnMigration.Domain.Enums;
using FluentAssertions;
using Xunit;

namespace DnnMigration.IntegrationTests.Api;

/// <summary>
/// Covers the module resource end to end, across the real HTTP pipeline and the real database.
/// </summary>
/// <remarks>
/// <para>
/// The class name is fixed by validation gate 5, which names this suite and requires the four documented
/// status codes for the module resource - <c>201</c> on a create, <c>200</c> on a read and an update, and
/// <c>204</c> on a delete.
/// </para>
/// <para>
/// Two behaviours of the module resource shape how these tests are written, and neither is obvious from the
/// route table.
/// </para>
/// <para>
/// The first is that reading and writing a module are gated by permission policies rather than by a role.
/// The handler behind those policies does not read the caller's token to decide whether it is a host account:
/// it resolves the account from the database and reads the stored super-user flag. A token that merely claims
/// to be a super-user therefore proves nothing. The suite consequently drives the permission-gated routes
/// with the seeded host account, whose stored row carries that flag, and it uses the seeded member account -
/// which has no grant rows at all - to prove that the gate really refuses.
/// </para>
/// <para>
/// The second is that a module delete addressed at the module rather than at one of its placements is a soft
/// delete: the row survives with its deleted marker set, because the legacy application let an administrator
/// restore a removed module from the recycle bin. A test that asserted the module had become unreadable would
/// therefore be asserting the wrong thing. What changes is the collection, which hides deleted modules unless
/// they are explicitly asked for, so that is what these tests assert.
/// </para>
/// <para>
/// Content export and import are exercised as the refusals they currently are. The seeded package declares no
/// business controller, and this installation registers none, so a request for content is answered with a
/// clear refusal rather than an empty document. That is the behaviour worth pinning: it records that content
/// portability is unavailable here, and it would fail loudly if a future change started returning an empty
/// document instead of saying so.
/// </para>
/// </remarks>
[Trait("Category", "Integration")]
[Collection(IntegrationTestCollection.Name)]
public sealed class ModuleApiTests
{
    /// <summary>An identifier no seeded or created row can hold, used for the absent-resource paths.</summary>
    private const int UnknownModuleId = 987654;

    /// <summary>A page identifier no seeded row can hold.</summary>
    private const int UnknownTabId = 987654;

    private readonly ApiTestFixture _fixture;

    /// <summary>Initialises a new instance of the <see cref="ModuleApiTests"/> class.</summary>
    /// <param name="fixture">The shared host, database and seed.</param>
    public ModuleApiTests(ApiTestFixture fixture) => _fixture = fixture;

    /// <summary>The definition catalogue answers <c>200 OK</c> and offers the seeded definition.</summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// The address carries no portal segment and no portal query value. The catalogue endpoint takes its
    /// tenant from the request host, which the fixture registers as an alias of the seeded portal, so the
    /// definitions asserted below are that portal's. This is the same tenant the administrator policy is
    /// evaluated against, which is why the endpoint accepts no caller-supplied portal identifier: one would
    /// let a request be authorised against one portal and answered about another.
    /// </remarks>
    [Fact]
    public async Task ListModuleDefinitions_ReturnsOkIncludingSeededDefinition()
    {
        using HttpClient client = _fixture.CreateHostClient();

        using HttpResponseMessage response = await client.GetAsync(
            new Uri("/api/v1/module-definitions", UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        string body = await response.Content.ReadAsStringAsync();

        // Rule T7 at the wire. The definition's default cache period is a non-nullable integer precisely so
        // that a stored -1 - which the legacy settings screen read as "caching does not apply, hide the
        // field" - reaches the caller as -1 rather than as null or as nothing at all. The seed stores 0,
        // which is the harder case to keep honest: a serialiser configured to omit default values would
        // drop the member entirely and a reader could not tell 0 from absent. Asserting the member is
        // present proves the configuration writes it whatever its value, and therefore that -1 survives too.
        body.Should().Contain("\"defaultCacheTime\"");

        IReadOnlyList<ModuleDefinitionDto>? definitions =
            JsonSerializer.Deserialize<IReadOnlyList<ModuleDefinitionDto>>(body, ApiTestFixture.Json);

        definitions.Should().NotBeNull();

        ModuleDefinitionDto definition = definitions!
            .Should().ContainSingle(item => item.ModuleDefId == _fixture.Seed.ModuleDefinitionId)
            .Subject;

        definition.FriendlyName.Should().Be(IntegrationSeed.ModuleDefinitionFriendlyName);
        definition.DesktopModuleId.Should().Be(_fixture.Seed.DesktopModuleId);
        definition.ModuleName.Should().Be(IntegrationSeed.DesktopModuleName);
        definition.IsPremium.Should().BeFalse();

        // The seeded package declares no capability bits, which is why the export and import tests below
        // expect a refusal rather than a document.
        definition.IsPortable.Should().BeFalse();
    }

    /// <summary>The definition catalogue requires a bearer token.</summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task ListModuleDefinitions_WithoutCredentials_ReturnsUnauthorized()
    {
        using HttpClient client = _fixture.CreateAnonymousClient();

        using HttpResponseMessage response = await client.GetAsync(
            new Uri("/api/v1/module-definitions", UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    /// <summary>
    /// The definition catalogue is administrator-only. A member of the tenant holding a perfectly valid token
    /// is refused, which proves the gate is portal administrator membership read from stored role assignments
    /// rather than mere authentication.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// The catalogue is installation-time reference data that only an administrator has any reason to browse,
    /// and the legacy screens it feeds were themselves administrator-gated. It is deliberately not guarded by
    /// a module permission policy: those resolve their scope from a module identifier in the route, and this
    /// route carries a definition identifier at most, so such a policy could only ever refuse.
    /// </remarks>
    [Fact]
    public async Task ListModuleDefinitions_AsMemberWithoutAdministratorRole_ReturnsForbidden()
    {
        using HttpClient client = _fixture.CreateClientFor(
            _fixture.Seed.MemberUserId,
            IntegrationSeed.MemberUserName,
            _fixture.Seed.PortalId,
            isSuperUser: false,
            roles: [IntegrationSeed.RegisteredUsersRoleName]);

        using HttpResponseMessage response = await client.GetAsync(
            new Uri("/api/v1/module-definitions", UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    /// <summary>
    /// The definition catalogue exposes no mutator. Every write verb on both the collection address and a
    /// per-definition address is unroutable, because definitions are written only by module installation and
    /// that subsystem lies beyond this migration's scope.
    /// </summary>
    /// <param name="method">The write verb to attempt.</param>
    /// <param name="path">The catalogue address to attempt it against.</param>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// <c>405 Method Not Allowed</c> is accepted alongside <c>404 Not Found</c> because which of the two the
    /// router produces depends on whether any action is registered for the address at all, and the assertion
    /// worth making is that no write reaches a handler - not which refusal the router happens to choose.
    /// </remarks>
    [Theory]
    [InlineData("POST", "/api/v1/module-definitions")]
    [InlineData("PUT", "/api/v1/module-definitions")]
    [InlineData("PATCH", "/api/v1/module-definitions")]
    [InlineData("DELETE", "/api/v1/module-definitions")]
    [InlineData("POST", "/api/v1/module-definitions/1")]
    [InlineData("PUT", "/api/v1/module-definitions/1")]
    [InlineData("DELETE", "/api/v1/module-definitions/1")]
    public async Task ModuleDefinitions_DeclareNoMutator(string method, string path)
    {
        using HttpClient client = _fixture.CreateHostClient();

        using HttpRequestMessage request = new(new HttpMethod(method), new Uri(path, UriKind.Relative));
        using HttpResponseMessage response = await client.SendAsync(request);

        response.StatusCode.Should().BeOneOf(HttpStatusCode.NotFound, HttpStatusCode.MethodNotAllowed);
    }

    /// <summary>A create answers <c>201 Created</c> with a location that resolves, and one placement lands.</summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task CreateModule_ReturnsCreatedWithResolvableLocation()
    {
        using HttpClient client = _fixture.CreateHostClient();

        CreateModuleRequest request = NewModuleRequest(_fixture.Seed.RootTabId);

        using HttpResponseMessage response = await client.PostAsJsonAsync(
            ModulesRoute(_fixture.Seed.PortalId),
            request,
            ApiTestFixture.Json);

        response.StatusCode.Should().Be(HttpStatusCode.Created);

        ModuleDetailDto created = await ReadDetailAsync(response);
        created.ModuleId.Should().BeGreaterThanOrEqualTo(0);
        created.TabModuleId.Should().BeGreaterThan(0);
        created.TabId.Should().Be(_fixture.Seed.RootTabId);
        created.PortalId.Should().Be(_fixture.Seed.PortalId);
        created.ModuleDefId.Should().Be(_fixture.Seed.ModuleDefinitionId);
        created.FriendlyName.Should().Be(IntegrationSeed.ModuleDefinitionFriendlyName);
        created.ModuleTitle.Should().Be(request.ModuleTitle);
        created.IconFile.Should().Be(
            request.IconFile,
            "a placement fact the detail contract does carry, the pane having moved to the settings "
            + "contract that owns the placement scope");
        created.Visibility.Should().Be(ModuleVisibility.Maximized);
        created.DisplayTitle.Should().BeTrue();
        created.IsDeleted.Should().BeFalse();
        created.AllTabs.Should().BeFalse();

        response.Headers.Location.Should().NotBeNull();
        response.Headers.Location!.OriginalString.Should().Be(
            $"/api/v1/portals/{Route(_fixture.Seed.PortalId)}/modules/{Route(created.ModuleId)}");

        using HttpResponseMessage followed = await client.GetAsync(
            new Uri(response.Headers.Location.OriginalString, UriKind.Relative));

        followed.StatusCode.Should().Be(HttpStatusCode.OK);
        (await ReadDetailAsync(followed)).ModuleId.Should().Be(created.ModuleId);

        // A module with no placement is unreachable, so the placement is part of what a successful create
        // means and is asserted against the database rather than inferred from the representation.
        int placementCount = await _fixture.Database.ScalarAsync<int>(
            "SELECT COUNT(*) FROM [dbo].[TabModules] WHERE [ModuleID] = @moduleId;",
            new Dictionary<string, object?> { ["moduleId"] = created.ModuleId });

        placementCount.Should().Be(1);
    }

    /// <summary>A create naming a definition that does not exist is refused.</summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task CreateModule_WithUnknownDefinition_ReturnsNotFound()
    {
        using HttpClient client = _fixture.CreateHostClient();

        CreateModuleRequest request = NewModuleRequest(_fixture.Seed.RootTabId);
        request.ModuleDefId = UnknownModuleId;

        using HttpResponseMessage response = await client.PostAsJsonAsync(
            ModulesRoute(_fixture.Seed.PortalId),
            request,
            ApiTestFixture.Json);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    /// <summary>A create naming a page in another tenant is refused, which is a tenant-isolation guarantee.</summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task CreateModule_WithPageOutsideThePortal_ReturnsNotFound()
    {
        using HttpClient client = _fixture.CreateHostClient();

        CreateModuleRequest request = NewModuleRequest(UnknownTabId);

        using HttpResponseMessage response = await client.PostAsJsonAsync(
            ModulesRoute(_fixture.Seed.PortalId),
            request,
            ApiTestFixture.Json);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    /// <summary>A create with a negative cache period is rejected by the request validator.</summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task CreateModule_WithNegativeCacheTime_ReturnsBadRequest()
    {
        using HttpClient client = _fixture.CreateHostClient();

        CreateModuleRequest request = NewModuleRequest(_fixture.Seed.RootTabId);
        request.CacheTime = -30;

        using HttpResponseMessage response = await client.PostAsJsonAsync(
            ModulesRoute(_fixture.Seed.PortalId),
            request,
            ApiTestFixture.Json);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    /// <summary>A create whose end date precedes its start date is rejected by the request validator.</summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task CreateModule_WithEndBeforeStart_ReturnsBadRequest()
    {
        using HttpClient client = _fixture.CreateHostClient();

        CreateModuleRequest request = NewModuleRequest(_fixture.Seed.RootTabId);
        request.StartDate = new DateTime(2030, 6, 1, 0, 0, 0, DateTimeKind.Utc);
        request.EndDate = new DateTime(2030, 5, 1, 0, 0, 0, DateTimeKind.Utc);

        using HttpResponseMessage response = await client.PostAsJsonAsync(
            ModulesRoute(_fixture.Seed.PortalId),
            request,
            ApiTestFixture.Json);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    /// <summary>A create against a tenant that does not exist is refused before anything is written.</summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task ListModules_ForUnknownPortal_ReturnsNotFound()
    {
        using HttpClient client = _fixture.CreateHostClient();

        using HttpResponseMessage response = await client.GetAsync(
            new Uri($"/api/v1/portals/{Route(987654)}/modules?pageIndex=0&pageSize=10", UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    /// <summary>The collection answers <c>200 OK</c> and carries a created module with its placement.</summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task ListModules_ReturnsOkContainingCreatedModule()
    {
        using HttpClient client = _fixture.CreateHostClient();
        ModuleDetailDto created = await CreateModuleAsync(client, _fixture.Seed.RootTabId);

        using HttpResponseMessage response = await client.GetAsync(
            new Uri(
                $"/api/v1/portals/{Route(_fixture.Seed.PortalId)}/modules?pageIndex=0&pageSize=100",
                UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        PagedEnvelope<ModuleListItemDto>? page = await response.Content
            .ReadFromJsonAsync<PagedEnvelope<ModuleListItemDto>>(ApiTestFixture.Json);

        page.Should().NotBeNull();
        page!.TotalCount.Should().BeGreaterThan(0);

        ModuleListItemDto row = page.Items
            .Should().ContainSingle(item => item.TabModuleId == created.TabModuleId)
            .Subject;

        row.ModuleId.Should().Be(created.ModuleId);
        row.TabId.Should().Be(_fixture.Seed.RootTabId);
        row.FriendlyName.Should().Be(IntegrationSeed.ModuleDefinitionFriendlyName);
        row.IsDeleted.Should().BeFalse();
    }

    /// <summary>The page filter narrows the collection to placements on one page.</summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task ListModules_FilteredByPage_ReturnsOnlyThatPagesPlacements()
    {
        using HttpClient client = _fixture.CreateHostClient();
        ModuleDetailDto onChild = await CreateModuleAsync(client, _fixture.Seed.ChildTabId);

        using HttpResponseMessage response = await client.GetAsync(
            new Uri(
                $"/api/v1/portals/{Route(_fixture.Seed.PortalId)}/modules"
                    + $"?pageIndex=0&pageSize=100&tabId={Route(_fixture.Seed.ChildTabId)}",
                UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        PagedEnvelope<ModuleListItemDto>? page = await response.Content
            .ReadFromJsonAsync<PagedEnvelope<ModuleListItemDto>>(ApiTestFixture.Json);

        page.Should().NotBeNull();
        page!.Items.Should().NotBeEmpty();
        page.Items.Should().OnlyContain(item => item.TabId == _fixture.Seed.ChildTabId);
        page.Items.Select(item => item.ModuleId).Should().Contain(onChild.ModuleId);
    }

    /// <summary>A read answers <c>200 OK</c> for a module in the addressed tenant.</summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task GetModule_ReturnsOkWithDetail()
    {
        using HttpClient client = _fixture.CreateHostClient();
        ModuleDetailDto created = await CreateModuleAsync(client, _fixture.Seed.RootTabId);

        using HttpResponseMessage response = await client.GetAsync(
            ModuleRoute(_fixture.Seed.PortalId, created.ModuleId));

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        ModuleDetailDto detail = await ReadDetailAsync(response);
        detail.ModuleId.Should().Be(created.ModuleId);
        detail.TabModuleId.Should().Be(created.TabModuleId);
        detail.ModuleTitle.Should().Be(created.ModuleTitle);
    }

    /// <summary>
    /// A module the permission gate cannot resolve answers <c>403 Forbidden</c> rather than <c>404 Not
    /// Found</c>, and does so even for a host account.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is deliberate and is asserted rather than worked around. Authorisation runs before the action, and
    /// it cannot answer a question about a module it is unable to resolve, so it declines. Declining is also
    /// the safer answer: the same route reaches a module that exists in a different tenant, and answering
    /// <c>404</c> there would tell an unauthorised caller which module identifiers exist and which do not.
    /// Uniform refusal removes that distinction, at the cost of a slightly less informative answer for a
    /// privileged caller.
    /// </para>
    /// <para>
    /// The routes that carry no permission gate behave differently on purpose, and the tests beside this one
    /// prove it: a create naming a definition that does not exist answers <c>404</c>, and so does a read of a
    /// collection belonging to a tenant that does not exist. Those routes are reached by callers already
    /// entitled to know the tenant's contents, so nothing is disclosed by being precise.
    /// </para>
    /// </remarks>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task GetModule_WhenUnresolvable_ReturnsForbiddenWithoutDisclosingExistence()
    {
        using HttpClient client = _fixture.CreateHostClient();

        using HttpResponseMessage response = await client.GetAsync(
            ModuleRoute(_fixture.Seed.PortalId, UnknownModuleId));

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    /// <summary>
    /// A module that exists but belongs to another tenant is refused on the same terms, which is the reason
    /// the refusal above is uniform. This is a tenant-isolation guarantee, not a convenience.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task GetModule_WhenModuleBelongsToAnotherTenant_ReturnsForbidden()
    {
        using HttpClient client = _fixture.CreateHostClient();
        ModuleDetailDto created = await CreateModuleAsync(client, _fixture.Seed.RootTabId);

        // The tenant in the route is one that does not contain the module. The answer must be identical to the
        // answer for a module that does not exist at all, or the pair of answers becomes an existence oracle.
        using HttpResponseMessage response = await client.GetAsync(
            ModuleRoute(_fixture.Seed.PortalId + 5000, created.ModuleId));

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    /// <summary>
    /// A caller with no permission grant is refused the read. This is the assertion that proves the policy is
    /// enforced from stored grants rather than from the caller's own claims: the member account holds a valid
    /// token and is a member of the tenant, and is still refused because nothing grants it this module.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task GetModule_AsMemberWithoutGrant_ReturnsForbidden()
    {
        using HttpClient host = _fixture.CreateHostClient();
        ModuleDetailDto created = await CreateModuleAsync(host, _fixture.Seed.RootTabId);

        using HttpClient client = _fixture.CreateClientFor(
            _fixture.Seed.MemberUserId,
            IntegrationSeed.MemberUserName,
            _fixture.Seed.PortalId,
            isSuperUser: false,
            roles: [IntegrationSeed.RegisteredUsersRoleName]);

        using HttpResponseMessage response = await client.GetAsync(
            ModuleRoute(_fixture.Seed.PortalId, created.ModuleId));

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    /// <summary>
    /// A grant recorded against the caller's role admits the read, and a deny recorded beside it takes
    /// precedence and shuts it again. The two halves belong in one test because the second only means
    /// anything given the first: proving a refusal without first proving the grant worked would not
    /// distinguish "deny wins" from "nothing was ever granted".
    /// </summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task GetModule_WithRoleGrant_IsAdmittedAndThenRefusedByADeny()
    {
        using HttpClient host = _fixture.CreateHostClient();
        ModuleDetailDto created = await CreateModuleAsync(host, _fixture.Seed.RootTabId);

        using HttpClient client = _fixture.CreateClientFor(
            _fixture.Seed.MemberUserId,
            IntegrationSeed.MemberUserName,
            _fixture.Seed.PortalId,
            isSuperUser: false,
            roles: [IntegrationSeed.RegisteredUsersRoleName]);

        // A module that inherits its view permission from its page would be answered from the page's grants
        // instead, so inheritance is switched off first to make this test about the module's own grant.
        await _fixture.Database.ExecuteAsync(
            "UPDATE [dbo].[Modules] SET [InheritViewPermissions] = 0 WHERE [ModuleID] = @moduleId;",
            new Dictionary<string, object?> { ["moduleId"] = created.ModuleId });

        await GrantModulePermissionAsync(
            created.ModuleId,
            _fixture.Seed.ModuleViewPermissionId,
            _fixture.Seed.RegisteredRoleId,
            allowAccess: true);

        using HttpResponseMessage admitted = await client.GetAsync(
            ModuleRoute(_fixture.Seed.PortalId, created.ModuleId));

        admitted.StatusCode.Should().Be(HttpStatusCode.OK);

        await GrantModulePermissionAsync(
            created.ModuleId,
            _fixture.Seed.ModuleViewPermissionId,
            _fixture.Seed.RegisteredRoleId,
            allowAccess: false);

        using HttpResponseMessage refused = await client.GetAsync(
            ModuleRoute(_fixture.Seed.PortalId, created.ModuleId));

        refused.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    /// <summary>An update answers <c>200 OK</c> and the new state survives a read.</summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task UpdateModule_ReturnsOkAndPersists()
    {
        using HttpClient client = _fixture.CreateHostClient();
        ModuleDetailDto created = await CreateModuleAsync(client, _fixture.Seed.RootTabId);

        var request = new UpdateModuleRequest
        {
            ModuleTitle = "Renamed " + Suffix(),
            PaneName = "LeftPane",
            ModuleOrder = 6,
            AllTabs = false,
            InheritViewPermissions = false,
            Visibility = ModuleVisibility.Minimized,
            DisplayTitle = false,
            CacheTime = 300,
            IconFile = "renamed.gif",
            Header = "Header text",
            Footer = "Footer text",
        };

        using HttpResponseMessage response = await client.PutAsJsonAsync(
            ModuleRoute(_fixture.Seed.PortalId, created.ModuleId),
            request,
            ApiTestFixture.Json);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        ModuleDetailDto updated = await ReadDetailAsync(response);
        updated.ModuleTitle.Should().Be(request.ModuleTitle);
        updated.ModuleOrder.Should().Be(6);
        updated.Visibility.Should().Be(ModuleVisibility.Minimized);
        updated.DisplayTitle.Should().BeFalse();
        updated.CacheTime.Should().Be(300);
        updated.InheritViewPermissions.Should().BeFalse();

        using HttpResponseMessage reread = await client.GetAsync(
            ModuleRoute(_fixture.Seed.PortalId, created.ModuleId));

        reread.StatusCode.Should().Be(HttpStatusCode.OK);

        ModuleDetailDto persisted = await ReadDetailAsync(reread);
        persisted.ModuleTitle.Should().Be(request.ModuleTitle);
        persisted.Header.Should().Be("Header text");
        persisted.Footer.Should().Be("Footer text");
    }

    /// <summary>
    /// The placement's appearance fields are accepted on the write path and exposed by no response
    /// contract, and omitting one is accepted just as setting it is.
    /// </summary>
    /// <remarks>
    /// <para>
    /// These fields are editable on the legacy screen
    /// (<c>Website/admin/Modules/ModuleSettings.ascx.vb:L345-L347,L381-L382</c>), so the write capability
    /// must stay reachable end to end: the values go out over HTTP, through the entity configuration and
    /// into the real <c>nvarchar(10)</c>, <c>nvarchar(20)</c> and <c>nvarchar(1)</c> columns on
    /// <c>dbo.TabModules</c>. Both legs below assert that the write is accepted.
    /// </para>
    /// <para>
    /// They are deliberately WRITE-ONLY in the target, which is why this test does not read them back. They
    /// exist solely to drive server-side markup generation, and that is excluded from this migration, so no
    /// response contract carries them: not the detail projection, and not the settings projection, which
    /// carries the two identifiers and the two key-value settings maps only. Their absence from the settings
    /// response is asserted positively below, so a later change that quietly reintroduces them fails here.
    /// The corresponding unit test pins the same asymmetry against all three contracts at once.
    /// </para>
    /// <para>
    /// The second leg still exercises the whole-row replacement semantics: omitting an appearance field
    /// clears the column rather than preserving it, because the legacy screen posted an empty text box as an
    /// empty value. Since no response contract reads the column back, what is asserted here is that the
    /// clearing write is accepted rather than rejected - the cleared VALUE is verified by the unit-level
    /// <c>ApplyUpdate</c> tests, which observe the entity directly.
    /// </para>
    /// </remarks>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task UpdateModule_AcceptsTheAppearanceFieldsAndExposesThemOnNoResponseContract()
    {
        using HttpClient client = _fixture.CreateHostClient();
        ModuleDetailDto created = await CreateModuleAsync(client, _fixture.Seed.RootTabId);

        var request = new UpdateModuleRequest
        {
            ModuleTitle = created.ModuleTitle,
            PaneName = "ContentPane",
            Alignment = "right",
            Color = "#003366",
            Border = "1",
            DisplayPrint = false,
            DisplaySyndicate = false,
        };

        using HttpResponseMessage response = await client.PutAsJsonAsync(
            ModuleRoute(_fixture.Seed.PortalId, created.ModuleId),
            request,
            ApiTestFixture.Json);

        response.StatusCode.Should().Be(
            HttpStatusCode.OK,
            "the legacy screen edited these columns, so the write path must keep accepting them");

        using HttpResponseMessage reread = await client.GetAsync(
            ModuleSettingsRoute(_fixture.Seed.PortalId, created.ModuleId));

        reread.StatusCode.Should().Be(HttpStatusCode.OK);

        ModuleSettingsDto persisted = await ReadSettingsAsync(reread);
        persisted.ModuleId.Should().Be(created.ModuleId);
        persisted.TabModuleId.Should().Be(
            created.TabModuleId,
            "the settings contract identifies the placement whose scoped settings it carries");

        foreach (string appearance in new[]
        {
            "PaneName", "Alignment", "Color", "Border", "DisplayPrint", "DisplaySyndicate",
        })
        {
            typeof(ModuleSettingsDto).GetProperty(appearance).Should().BeNull(
                $"the placement's {appearance} drives server-side markup, which this migration excludes, "
                + "so the settings contract carries the two identifiers and the two settings maps only");
        }

        using HttpResponseMessage cleared = await client.PutAsJsonAsync(
            ModuleRoute(_fixture.Seed.PortalId, created.ModuleId),
            new UpdateModuleRequest { ModuleTitle = created.ModuleTitle, PaneName = "ContentPane" },
            ApiTestFixture.Json);

        cleared.StatusCode.Should().Be(
            HttpStatusCode.OK,
            "omitting an appearance field clears the column, which is a legitimate edit rather than an "
            + "invalid request");
    }

    /// <summary>
    /// A value longer than the column is refused by the request validator rather than by the store.
    /// </summary>
    /// <remarks>
    /// <c>TabModules.Border</c> is <c>nvarchar(1)</c>. Letting a longer value reach SQL Server would surface
    /// as a truncation error rather than a field-level message, so the bound is asserted at the boundary.
    /// </remarks>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task UpdateModule_WithAnOverlongBorderFlag_ReturnsBadRequest()
    {
        using HttpClient client = _fixture.CreateHostClient();
        ModuleDetailDto created = await CreateModuleAsync(client, _fixture.Seed.RootTabId);

        using HttpResponseMessage response = await client.PutAsJsonAsync(
            ModuleRoute(_fixture.Seed.PortalId, created.ModuleId),
            new UpdateModuleRequest { PaneName = "ContentPane", Border = "12" },
            ApiTestFixture.Json);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }


    /// <summary>
    /// An update against a module the permission gate cannot resolve is refused before the action runs, for
    /// the reasons set out on the read above.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task UpdateModule_WhenUnresolvable_ReturnsForbidden()
    {
        using HttpClient client = _fixture.CreateHostClient();

        using HttpResponseMessage response = await client.PutAsJsonAsync(
            ModuleRoute(_fixture.Seed.PortalId, UnknownModuleId),
            new UpdateModuleRequest { ModuleTitle = "No such module" },
            ApiTestFixture.Json);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    /// <summary>An update carrying a negative cache period is rejected by the request validator.</summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task UpdateModule_WithNegativeCachePeriod_ReturnsBadRequest()
    {
        using HttpClient client = _fixture.CreateHostClient();
        ModuleDetailDto created = await CreateModuleAsync(client, _fixture.Seed.RootTabId);

        using HttpResponseMessage response = await client.PutAsJsonAsync(
            ModuleRoute(_fixture.Seed.PortalId, created.ModuleId),
            new UpdateModuleRequest { CacheTime = -1 },
            ApiTestFixture.Json);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    /// <summary>Reading and writing the settings projection round-trips both scopes of setting.</summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task ModuleSettings_RoundTripBothScopes()
    {
        using HttpClient client = _fixture.CreateHostClient();
        ModuleDetailDto created = await CreateModuleAsync(client, _fixture.Seed.RootTabId);

        Uri settingsRoute = ModuleSettingsRoute(_fixture.Seed.PortalId, created.ModuleId);

        using HttpResponseMessage initial = await client.GetAsync(settingsRoute);
        initial.StatusCode.Should().Be(HttpStatusCode.OK);

        ModuleSettingsDto? before = await initial.Content
            .ReadFromJsonAsync<ModuleSettingsDto>(ApiTestFixture.Json);

        before.Should().NotBeNull();
        before!.ModuleId.Should().Be(created.ModuleId);
        before.TabModuleId.Should().Be(created.TabModuleId);
        before.ModuleSettings.Should().BeEmpty();
        before.TabModuleSettings.Should().BeEmpty();

        var desired = new ModuleSettingsDto
        {
            ModuleId = created.ModuleId,
            TabModuleId = created.TabModuleId,
            ModuleSettings = new Dictionary<string, string>
            {
                ["ShowSummary"] = "True",
                ["ItemCount"] = "12",
            },
            TabModuleSettings = new Dictionary<string, string>
            {
                ["ColumnWidth"] = "320",
            },
        };

        using HttpResponseMessage stored = await client.PutAsJsonAsync(
            settingsRoute,
            desired,
            ApiTestFixture.Json);

        stored.StatusCode.Should().Be(HttpStatusCode.NoContent);

        using HttpResponseMessage reread = await client.GetAsync(settingsRoute);
        reread.StatusCode.Should().Be(HttpStatusCode.OK);

        ModuleSettingsDto? after = await reread.Content
            .ReadFromJsonAsync<ModuleSettingsDto>(ApiTestFixture.Json);

        after.Should().NotBeNull();
        after!.ModuleSettings.Should().HaveCount(2);
        after.ModuleSettings["ShowSummary"].Should().Be("True");
        after.ModuleSettings["ItemCount"].Should().Be("12");
        after.TabModuleSettings.Should().ContainSingle();
        after.TabModuleSettings["ColumnWidth"].Should().Be("320");

        // A submitted set is the whole set: a name the caller stops sending is removed rather than retained,
        // which is the only interpretation under which the projection can ever be reduced.
        using HttpResponseMessage reduced = await client.PutAsJsonAsync(
            settingsRoute,
            new ModuleSettingsDto
            {
                ModuleId = created.ModuleId,
                TabModuleId = created.TabModuleId,
                ModuleSettings = new Dictionary<string, string> { ["ItemCount"] = "25" },
                TabModuleSettings = new Dictionary<string, string>(),
            },
            ApiTestFixture.Json);

        reduced.StatusCode.Should().Be(HttpStatusCode.NoContent);

        using HttpResponseMessage final = await client.GetAsync(settingsRoute);
        ModuleSettingsDto? remaining = await final.Content
            .ReadFromJsonAsync<ModuleSettingsDto>(ApiTestFixture.Json);

        remaining.Should().NotBeNull();
        remaining!.ModuleSettings.Should().ContainSingle();
        remaining.ModuleSettings["ItemCount"].Should().Be("25");
        remaining.TabModuleSettings.Should().BeEmpty();
    }

    /// <summary>
    /// A module that is placed on no page can still hold module-scoped settings, and is told plainly that it
    /// cannot hold placement-scoped ones.
    /// </summary>
    /// <remarks>
    /// The two halves belong together because each is the other's control. A refusal alone could mean the
    /// endpoint rejects every request from an unplaced module; a success alone could mean the endpoint accepts
    /// placement settings it silently discards. Asserting both pins the boundary exactly where the service
    /// draws it: the module scope needs no placement, the placement scope cannot exist without one.
    /// </remarks>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task UpdateModuleSettings_ForAnUnplacedModule_AcceptsModuleScopeAndRefusesPlacementScope()
    {
        using HttpClient client = _fixture.CreateHostClient();
        ModuleDetailDto created = await CreateModuleAsync(client, _fixture.Seed.RootTabId);

        using HttpResponseMessage withdrawn = await client.DeleteAsync(new Uri(
            $"/api/v1/portals/{Route(_fixture.Seed.PortalId)}/modules/{Route(created.ModuleId)}"
                + $"?tabModuleId={Route(created.TabModuleId)}",
            UriKind.Relative));

        withdrawn.StatusCode.Should().Be(HttpStatusCode.NoContent);

        Uri settingsRoute = ModuleSettingsRoute(_fixture.Seed.PortalId, created.ModuleId);

        using HttpResponseMessage moduleScope = await client.PutAsJsonAsync(
            settingsRoute,
            new ModuleSettingsDto
            {
                ModuleId = created.ModuleId,
                ModuleSettings = new Dictionary<string, string> { ["Retained"] = "yes" },
                TabModuleSettings = new Dictionary<string, string>(),
            },
            ApiTestFixture.Json);

        moduleScope.StatusCode.Should().Be(HttpStatusCode.NoContent);

        int stored = await _fixture.Database.ScalarAsync<int>(
            "SELECT COUNT(*) FROM [dbo].[ModuleSettings] WHERE [ModuleID] = @moduleId;",
            new Dictionary<string, object?> { ["moduleId"] = created.ModuleId });

        stored.Should().Be(1);

        using HttpResponseMessage placementScope = await client.PutAsJsonAsync(
            settingsRoute,
            new ModuleSettingsDto
            {
                ModuleId = created.ModuleId,
                ModuleSettings = new Dictionary<string, string> { ["Retained"] = "yes" },
                TabModuleSettings = new Dictionary<string, string> { ["ColumnWidth"] = "320" },
            },
            ApiTestFixture.Json);

        placementScope.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        string body = await placementScope.Content.ReadAsStringAsync();
        body.Should().Contain("is not placed on any page");
    }

    /// <summary>Reading the settings of an unresolvable module is refused before the action runs.</summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task GetModuleSettings_WhenUnresolvable_ReturnsForbidden()
    {
        using HttpClient client = _fixture.CreateHostClient();

        using HttpResponseMessage response = await client.GetAsync(
            ModuleSettingsRoute(_fixture.Seed.PortalId, UnknownModuleId));

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    /// <summary>Writing the settings of an unknown module answers <c>404 Not Found</c>.</summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task UpdateModuleSettings_WhenUnresolvable_ReturnsForbidden()
    {
        using HttpClient client = _fixture.CreateHostClient();

        using HttpResponseMessage response = await client.PutAsJsonAsync(
            ModuleSettingsRoute(_fixture.Seed.PortalId, UnknownModuleId),
            new ModuleSettingsDto
            {
                ModuleId = UnknownModuleId,
                ModuleSettings = new Dictionary<string, string>(),
                TabModuleSettings = new Dictionary<string, string>(),
            },
            ApiTestFixture.Json);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    /// <summary>
    /// A delete answers <c>204 No Content</c>. The module is then hidden from the collection but still
    /// recorded, because a module delete is reversible by design.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task DeleteModule_ReturnsNoContentAndHidesItFromTheCollection()
    {
        using HttpClient client = _fixture.CreateHostClient();
        ModuleDetailDto created = await CreateModuleAsync(client, _fixture.Seed.RootTabId);

        using HttpResponseMessage response = await client.DeleteAsync(
            ModuleRoute(_fixture.Seed.PortalId, created.ModuleId));

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);

        IReadOnlyList<ModuleListItemDto> visible = await ListModulesAsync(client, includeDeleted: false);
        visible.Select(item => item.ModuleId).Should().NotContain(created.ModuleId);

        IReadOnlyList<ModuleListItemDto> withDeleted = await ListModulesAsync(client, includeDeleted: true);
        ModuleListItemDto row = withDeleted
            .Should().ContainSingle(item => item.ModuleId == created.ModuleId)
            .Subject;

        row.IsDeleted.Should().BeTrue();

        int stored = await _fixture.Database.ScalarAsync<int>(
            "SELECT COUNT(*) FROM [dbo].[Modules] WHERE [ModuleID] = @moduleId AND [IsDeleted] = 1;",
            new Dictionary<string, object?> { ["moduleId"] = created.ModuleId });

        stored.Should().Be(1);
    }

    /// <summary>
    /// A delete addressed at a placement removes that placement outright rather than marking the module,
    /// because withdrawing a module from one page is not the same act as removing the module.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task DeleteModulePlacement_ReturnsNoContentAndRemovesOnlyThePlacement()
    {
        using HttpClient client = _fixture.CreateHostClient();
        ModuleDetailDto created = await CreateModuleAsync(client, _fixture.Seed.RootTabId);

        using HttpResponseMessage response = await client.DeleteAsync(new Uri(
            $"/api/v1/portals/{Route(_fixture.Seed.PortalId)}/modules/{Route(created.ModuleId)}"
                + $"?tabModuleId={Route(created.TabModuleId)}",
            UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);

        int placements = await _fixture.Database.ScalarAsync<int>(
            "SELECT COUNT(*) FROM [dbo].[TabModules] WHERE [ModuleID] = @moduleId;",
            new Dictionary<string, object?> { ["moduleId"] = created.ModuleId });

        placements.Should().Be(0);

        int deletedModules = await _fixture.Database.ScalarAsync<int>(
            "SELECT COUNT(*) FROM [dbo].[Modules] WHERE [ModuleID] = @moduleId AND [IsDeleted] = 1;",
            new Dictionary<string, object?> { ["moduleId"] = created.ModuleId });

        deletedModules.Should().Be(0);
    }

    /// <summary>A delete addressing a placement of another module is refused.</summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task DeleteModulePlacement_WhenPlacementBelongsElsewhere_ReturnsNotFound()
    {
        using HttpClient client = _fixture.CreateHostClient();
        ModuleDetailDto first = await CreateModuleAsync(client, _fixture.Seed.RootTabId);
        ModuleDetailDto second = await CreateModuleAsync(client, _fixture.Seed.ChildTabId);

        using HttpResponseMessage response = await client.DeleteAsync(new Uri(
            $"/api/v1/portals/{Route(_fixture.Seed.PortalId)}/modules/{Route(first.ModuleId)}"
                + $"?tabModuleId={Route(second.TabModuleId)}",
            UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    /// <summary>A delete against a module the permission gate cannot resolve is refused before the action runs.</summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task DeleteModule_WhenUnresolvable_ReturnsForbidden()
    {
        using HttpClient client = _fixture.CreateHostClient();

        using HttpResponseMessage response = await client.DeleteAsync(
            ModuleRoute(_fixture.Seed.PortalId, UnknownModuleId));

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    /// <summary>
    /// An export of a package that declares no content capability is refused rather than answered with an
    /// empty document, and the refusal names the reason.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task ExportModule_WhenPackageIsNotPortable_ReturnsBadRequestNamingTheReason()
    {
        using HttpClient client = _fixture.CreateHostClient();
        ModuleDetailDto created = await CreateModuleAsync(client, _fixture.Seed.RootTabId);

        using HttpResponseMessage response = await client.PostAsJsonAsync(
            ModuleExportRoute(_fixture.Seed.PortalId, created.ModuleId),
            new ModuleExportRequest { FileName = "content.xml" },
            ApiTestFixture.Json);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        string body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("does not support content export");
    }

    /// <summary>An export without a file name is refused, because the document could not be labelled.</summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task ExportModule_WithoutFileName_ReturnsBadRequest()
    {
        using HttpClient client = _fixture.CreateHostClient();
        ModuleDetailDto created = await CreateModuleAsync(client, _fixture.Seed.RootTabId);

        using HttpResponseMessage response = await client.PostAsJsonAsync(
            ModuleExportRoute(_fixture.Seed.PortalId, created.ModuleId),
            new ModuleExportRequest { FileName = "   " },
            ApiTestFixture.Json);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        string body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("A file name is required");
    }

    /// <summary>An export of an unresolvable module is refused before the action runs.</summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task ExportModule_WhenUnresolvable_ReturnsForbidden()
    {
        using HttpClient client = _fixture.CreateHostClient();

        using HttpResponseMessage response = await client.PostAsJsonAsync(
            ModuleExportRoute(_fixture.Seed.PortalId, UnknownModuleId),
            new ModuleExportRequest { FileName = "content.xml" },
            ApiTestFixture.Json);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    /// <summary>An import naming a module in another tenant answers <c>404 Not Found</c>.</summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task ImportModule_WhenModuleUnknown_ReturnsNotFound()
    {
        using HttpClient client = _fixture.CreateHostClient();

        using HttpResponseMessage response = await client.PostAsJsonAsync(
            ModuleImportRoute(_fixture.Seed.PortalId),
            new ModuleImportRequest { ModuleId = UnknownModuleId, Content = "<content />" },
            ApiTestFixture.Json);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    /// <summary>An import carrying no document is refused before the package is consulted.</summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task ImportModule_WithEmptyContent_ReturnsBadRequest()
    {
        using HttpClient client = _fixture.CreateHostClient();
        ModuleDetailDto created = await CreateModuleAsync(client, _fixture.Seed.RootTabId);

        using HttpResponseMessage response = await client.PostAsJsonAsync(
            ModuleImportRoute(_fixture.Seed.PortalId),
            new ModuleImportRequest { ModuleId = created.ModuleId, Content = "   " },
            ApiTestFixture.Json);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        string body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("empty");
    }

    /// <summary>An import into a package that declares no content capability is refused.</summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task ImportModule_WhenPackageIsNotPortable_ReturnsBadRequest()
    {
        using HttpClient client = _fixture.CreateHostClient();
        ModuleDetailDto created = await CreateModuleAsync(client, _fixture.Seed.RootTabId);

        using HttpResponseMessage response = await client.PostAsJsonAsync(
            ModuleImportRoute(_fixture.Seed.PortalId),
            new ModuleImportRequest
            {
                ModuleId = created.ModuleId,
                Content = "<content type=\"IntegrationDesktopModule\" version=\"01.00.00\"><item /></content>",
            },
            ApiTestFixture.Json);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        string body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("does not support content import");
    }

    /// <summary>Creates a module through the API and returns its representation.</summary>
    /// <param name="client">A client entitled to create modules.</param>
    /// <param name="tabId">The page the module is placed on.</param>
    /// <returns>The created module.</returns>
    private async Task<ModuleDetailDto> CreateModuleAsync(HttpClient client, int tabId)
    {
        using HttpResponseMessage response = await client.PostAsJsonAsync(
            ModulesRoute(_fixture.Seed.PortalId),
            NewModuleRequest(tabId),
            ApiTestFixture.Json);

        response.StatusCode.Should().Be(HttpStatusCode.Created);

        return await ReadDetailAsync(response);
    }

    /// <summary>Reads the module collection.</summary>
    /// <param name="client">A client entitled to read the collection.</param>
    /// <param name="includeDeleted">Whether removed modules are included.</param>
    /// <returns>The rows on the first, generously sized page.</returns>
    private async Task<IReadOnlyList<ModuleListItemDto>> ListModulesAsync(HttpClient client, bool includeDeleted)
    {
        using HttpResponseMessage response = await client.GetAsync(new Uri(
            $"/api/v1/portals/{Route(_fixture.Seed.PortalId)}/modules"
                + $"?pageIndex=0&pageSize=100&includeDeleted={(includeDeleted ? "true" : "false")}",
            UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        PagedEnvelope<ModuleListItemDto>? page = await response.Content
            .ReadFromJsonAsync<PagedEnvelope<ModuleListItemDto>>(ApiTestFixture.Json);

        page.Should().NotBeNull();
        return page!.Items;
    }

    /// <summary>
    /// Records or replaces one module permission grant. The grant is written directly because the API exposes
    /// no grant-management endpoint - permission catalogues are read-only over HTTP in this migration - and the
    /// point of the test is the evaluation of stored grants, not the means of storing them.
    /// </summary>
    /// <param name="moduleId">The module the grant is recorded against.</param>
    /// <param name="permissionId">The catalogue entry being granted or denied.</param>
    /// <param name="roleId">The role the grant applies to.</param>
    /// <param name="allowAccess">Whether the grant allows or denies.</param>
    /// <returns>A task representing the write.</returns>
    private async Task GrantModulePermissionAsync(int moduleId, int permissionId, int roleId, bool allowAccess)
    {
        await _fixture.Database.ExecuteAsync(
            """
            DELETE FROM [dbo].[ModulePermission]
            WHERE [ModuleID] = @moduleId AND [PermissionID] = @permissionId AND [RoleID] = @roleId;

            INSERT INTO [dbo].[ModulePermission] ([ModuleID], [PermissionID], [RoleID], [AllowAccess])
            VALUES (@moduleId, @permissionId, @roleId, @allowAccess);
            """,
            new Dictionary<string, object?>
            {
                ["moduleId"] = moduleId,
                ["permissionId"] = permissionId,
                ["roleId"] = roleId,
                ["allowAccess"] = allowAccess,
            });
    }

    /// <summary>Builds a create request with a value for every field the contract constrains.</summary>
    /// <param name="tabId">The page the module is placed on.</param>
    /// <returns>A well formed create request.</returns>
    private CreateModuleRequest NewModuleRequest(int tabId) => new()
    {
        ModuleDefId = _fixture.Seed.ModuleDefinitionId,
        TabId = tabId,
        ModuleTitle = "Integration Module " + Suffix(),
        ModuleOrder = 2,
        AllTabs = false,
        InheritViewPermissions = true,
        Visibility = ModuleVisibility.Maximized,
        DisplayTitle = true,
        CacheTime = 0,
        IconFile = "module.gif",
    };

    /// <summary>Reads a module representation out of a response, failing the test when it is absent.</summary>
    /// <param name="response">The response to read.</param>
    /// <returns>The representation.</returns>
    private static async Task<ModuleDetailDto> ReadDetailAsync(HttpResponseMessage response)
    {
        ModuleDetailDto? detail = await response.Content
            .ReadFromJsonAsync<ModuleDetailDto>(ApiTestFixture.Json);

        detail.Should().NotBeNull();
        return detail!;
    }

    /// <summary>
    /// Reads a module settings representation from a response, failing the test when it is absent.
    /// </summary>
    /// <param name="response">The response to read.</param>
    /// <returns>The representation.</returns>
    /// <remarks>
    /// The settings projection is the contract that owns the placement scope - the pane, the appearance
    /// columns and both key-value collections - so a test asserting on any of those reads through here
    /// rather than through <see cref="ReadDetailAsync"/>.
    /// </remarks>
    private static async Task<ModuleSettingsDto> ReadSettingsAsync(HttpResponseMessage response)
    {
        ModuleSettingsDto? settings = await response.Content
            .ReadFromJsonAsync<ModuleSettingsDto>(ApiTestFixture.Json);

        settings.Should().NotBeNull();
        return settings!;
    }

    /// <summary>Builds the collection route for a tenant's modules.</summary>
    /// <param name="portalId">The tenant identifier.</param>
    /// <returns>A relative route.</returns>
    private static Uri ModulesRoute(int portalId) =>
        new($"/api/v1/portals/{Route(portalId)}/modules", UriKind.Relative);

    /// <summary>Builds the item route for one module.</summary>
    /// <param name="portalId">The tenant identifier.</param>
    /// <param name="moduleId">The module identifier.</param>
    /// <returns>A relative route.</returns>
    private static Uri ModuleRoute(int portalId, int moduleId) =>
        new($"/api/v1/portals/{Route(portalId)}/modules/{Route(moduleId)}", UriKind.Relative);

    /// <summary>Builds the settings route for one module.</summary>
    /// <param name="portalId">The tenant identifier.</param>
    /// <param name="moduleId">The module identifier.</param>
    /// <returns>A relative route.</returns>
    private static Uri ModuleSettingsRoute(int portalId, int moduleId) =>
        new($"/api/v1/portals/{Route(portalId)}/modules/{Route(moduleId)}/settings", UriKind.Relative);

    /// <summary>Builds the export route for one module.</summary>
    /// <param name="portalId">The tenant identifier.</param>
    /// <param name="moduleId">The module identifier.</param>
    /// <returns>A relative route.</returns>
    private static Uri ModuleExportRoute(int portalId, int moduleId) =>
        new($"/api/v1/portals/{Route(portalId)}/modules/{Route(moduleId)}/export", UriKind.Relative);

    /// <summary>Builds the import route for a tenant.</summary>
    /// <param name="portalId">The tenant identifier.</param>
    /// <returns>A relative route.</returns>
    private static Uri ModuleImportRoute(int portalId) =>
        new($"/api/v1/portals/{Route(portalId)}/modules/import", UriKind.Relative);

    /// <summary>Formats an identifier for a route without picking up the ambient culture.</summary>
    /// <param name="value">The identifier.</param>
    /// <returns>The invariant representation.</returns>
    private static string Route(int value) => value.ToString(CultureInfo.InvariantCulture);

    /// <summary>Produces a short random suffix for values that must differ between tests.</summary>
    /// <returns>Twelve lower-case hexadecimal characters.</returns>
    private static string Suffix() => Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture)[..12];
}
