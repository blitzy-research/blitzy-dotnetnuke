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

        CollectionEnvelope<ModuleDefinitionDto>? envelope =
            JsonSerializer.Deserialize<CollectionEnvelope<ModuleDefinitionDto>>(body, ApiTestFixture.Json);

        envelope.Should().NotBeNull();

        IReadOnlyList<ModuleDefinitionDto> definitions = envelope!.Data;

        ModuleDefinitionDto definition = definitions
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

    /// <summary>One definition is readable by its own identifier.</summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// Restores the legacy per-definition read. The legacy member answered from the whole installation; this
    /// address answers within the tenant the request host resolves to, which is the deliberate narrowing the
    /// contract records.
    /// </remarks>
    [Fact]
    public async Task GetModuleDefinition_ByIdentifier_ReturnsOkWithTheSeededDefinition()
    {
        using HttpClient client = _fixture.CreateHostClient();

        using HttpResponseMessage response = await client.GetAsync(new Uri(
            $"/api/v1/module-definitions/{_fixture.Seed.ModuleDefinitionId.ToString(CultureInfo.InvariantCulture)}",
            UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        // Read through the ENVELOPE, which is what every payload-bearing success in this API publishes. An
        // earlier revision read the bare payload here and silently succeeded against defaults, because
        // deserialising an envelope as its own payload type yields an object with every member unset.
        ModuleDefinitionDto? definition = await response.Content
            .ReadEnvelopeAsync<ModuleDefinitionDto>();

        definition.Should().NotBeNull();
        definition!.ModuleDefId.Should().Be(_fixture.Seed.ModuleDefinitionId);
        definition.FriendlyName.Should().Be(IntegrationSeed.ModuleDefinitionFriendlyName);
        definition.DesktopModuleId.Should().Be(_fixture.Seed.DesktopModuleId);
        definition.ModuleName.Should().Be(IntegrationSeed.DesktopModuleName);
    }

    /// <summary>A definition identifier naming nothing answers <c>404 Not Found</c>.</summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// A definition the tenant is not entitled to instantiate answers the same way, and that is deliberate:
    /// a caller must not be able to tell "no such definition" from "not yours", because the difference
    /// between those two answers is itself a fact about another tenant's installation.
    /// </remarks>
    [Fact]
    public async Task GetModuleDefinition_WhenUnknown_ReturnsNotFound()
    {
        using HttpClient client = _fixture.CreateHostClient();

        using HttpResponseMessage response = await client.GetAsync(
            new Uri("/api/v1/module-definitions/987654", UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    /// <summary>The definitions of one package are readable by the package identifier.</summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task ListDesktopModuleDefinitions_ReturnsOkWithThatPackagesDefinitions()
    {
        using HttpClient client = _fixture.CreateHostClient();

        using HttpResponseMessage response = await client.GetAsync(new Uri(
            "/api/v1/module-definitions/desktop-modules/"
                + _fixture.Seed.DesktopModuleId.ToString(CultureInfo.InvariantCulture),
            UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        CollectionEnvelope<ModuleDefinitionDto>? envelope = await response.Content
            .ReadFromJsonAsync<CollectionEnvelope<ModuleDefinitionDto>>(ApiTestFixture.Json);

        envelope.Should().NotBeNull();
        envelope!.Data.Should().NotBeNull();
        envelope.Data!.Should().Contain(item => item.ModuleDefId == _fixture.Seed.ModuleDefinitionId);
        envelope.Data!.Should().OnlyContain(item => item.DesktopModuleId == _fixture.Seed.DesktopModuleId);
    }

    /// <summary>
    /// A package identifier naming nothing answers <c>200 OK</c> with an empty array, never <c>404</c>.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// A catalogue read answers with a sequence, and an empty sequence is a legitimate answer. Reporting a
    /// missing collection would make an empty result indistinguishable from a failure.
    /// </remarks>
    [Fact]
    public async Task ListDesktopModuleDefinitions_WhenPackageUnknown_ReturnsOkWithAnEmptyArray()
    {
        using HttpClient client = _fixture.CreateHostClient();

        using HttpResponseMessage response = await client.GetAsync(
            new Uri("/api/v1/module-definitions/desktop-modules/987654", UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        CollectionEnvelope<ModuleDefinitionDto>? envelope = await response.Content
            .ReadFromJsonAsync<CollectionEnvelope<ModuleDefinitionDto>>(ApiTestFixture.Json);

        envelope.Should().NotBeNull();
        envelope!.Data.Should().NotBeNull();
        envelope.Data!.Should().BeEmpty();
    }

    /// <summary>Both new definition reads are administrator-only.</summary>
    /// <param name="path">The address to attempt.</param>
    /// <returns>A task representing the test.</returns>
    [Theory]
    [InlineData("/api/v1/module-definitions/1")]
    [InlineData("/api/v1/module-definitions/desktop-modules/1")]
    public async Task ModuleDefinitionReads_AsMemberWithoutAdministratorRole_ReturnForbidden(string path)
    {
        using HttpClient client = _fixture.CreateClientFor(
            _fixture.Seed.MemberUserId,
            IntegrationSeed.MemberUserName,
            _fixture.Seed.PortalId,
            isSuperUser: false,
            roles: [IntegrationSeed.RegisteredUsersRoleName]);

        using HttpResponseMessage response = await client.GetAsync(new Uri(path, UriKind.Relative));

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

    /// <summary>
    /// A create by a caller holding no edit grant on the target page is REFUSED, and writes nothing. This is
    /// the regression test for the missing authorisation on creation: the endpoint carried the bare
    /// authentication requirement, the service verified only that the page belonged to the tenant, and the
    /// commentary on both claimed a permission check that no code performed - so any authenticated caller
    /// could place a module on any page of any tenant.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task CreateModule_WithoutPageEditGrant_ReturnsForbiddenAndWritesNothing()
    {
        using HttpClient client = MemberClient();

        CreateModuleRequest request = NewModuleRequest(_fixture.Seed.RootTabId);

        using HttpResponseMessage response = await client.PostAsJsonAsync(
            ModulesRoute(_fixture.Seed.PortalId),
            request,
            ApiTestFixture.Json);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);

        int written = await _fixture.Database.ScalarAsync<int>(
            "SELECT COUNT(*) FROM [dbo].[Modules] WHERE [ModuleID] IN "
            + "(SELECT [ModuleID] FROM [dbo].[TabModules] WHERE [ModuleTitle] = @title);",
            new Dictionary<string, object?> { ["title"] = request.ModuleTitle });

        written.Should().Be(0, "a refused create must leave no module and no placement behind");
    }

    /// <summary>
    /// The refusal above is a real permission evaluation rather than a blanket denial: an edit grant recorded
    /// against the caller's role on the target page admits the same create, and a deny recorded beside it
    /// closes it again. Proving the denial alone could not distinguish "the grant is consulted" from "creation
    /// is simply closed to everybody but a host account".
    /// </summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task CreateModule_WithPageEditGrant_IsAdmittedAndThenRefusedByADeny()
    {
        using HttpClient client = MemberClient();

        await GrantTabPermissionAsync(
            _fixture.Seed.ChildTabId,
            _fixture.Seed.TabEditPermissionId,
            _fixture.Seed.RegisteredRoleId,
            allowAccess: true);

        try
        {
            using HttpResponseMessage admitted = await client.PostAsJsonAsync(
                ModulesRoute(_fixture.Seed.PortalId),
                NewModuleRequest(_fixture.Seed.ChildTabId),
                ApiTestFixture.Json);

            admitted.StatusCode.Should().Be(HttpStatusCode.Created);

            await GrantTabPermissionAsync(
                _fixture.Seed.ChildTabId,
                _fixture.Seed.TabEditPermissionId,
                _fixture.Seed.RegisteredRoleId,
                allowAccess: false);

            using HttpResponseMessage refused = await client.PostAsJsonAsync(
                ModulesRoute(_fixture.Seed.PortalId),
                NewModuleRequest(_fixture.Seed.ChildTabId),
                ApiTestFixture.Json);

            refused.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        }
        finally
        {
            // The grant is portal-wide state that other suites read, so it is removed however this ends.
            await RevokeTabPermissionAsync(
                _fixture.Seed.ChildTabId,
                _fixture.Seed.TabEditPermissionId,
                _fixture.Seed.RegisteredRoleId);
        }
    }

    /// <summary>
    /// A create with a negative cache period is accepted and the value is persisted exactly as submitted,
    /// because no legacy rule and no schema constraint forbids it.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// MIGRATION: this test previously asserted <c>400 Bad Request</c>, and that assertion recorded an
    /// invented rule rather than a ported one. <c>valCacheTime</c> (<c>modulesettings.ascx</c> L172) declared
    /// <c>Operator="DataTypeCheck" Type="Integer"</c> and nothing else, the code-behind stored the parsed
    /// value with no comparison (<c>ModuleSettings.ascx.vb</c> L349-L350), and the column is a plain
    /// <c>int NOT NULL</c> with no check constraint anywhere in the eighty-eight-script chain. The whole
    /// vertical is asserted - the validator no longer refuses, the projection no longer clamps, and the
    /// stored column is read back through a fresh request - because the floor existed in two places and a
    /// removal from only one of them would return 201 and still store a rewritten value.
    /// </remarks>
    [Fact]
    public async Task CreateModule_WithNegativeCacheTime_PersistsItVerbatim()
    {
        using HttpClient client = _fixture.CreateHostClient();

        CreateModuleRequest request = NewModuleRequest(_fixture.Seed.RootTabId);
        request.CacheTime = -30;

        using HttpResponseMessage response = await client.PostAsJsonAsync(
            ModulesRoute(_fixture.Seed.PortalId),
            request,
            ApiTestFixture.Json);

        response.StatusCode.Should().Be(HttpStatusCode.Created);

        ModuleDetailDto created = await ReadDetailAsync(response);
        created.CacheTime.Should().Be(-30, "the submitted period is stored, not clamped");

        using HttpResponseMessage reread = await client.GetAsync(
            ModuleRoute(_fixture.Seed.PortalId, created.ModuleId));

        reread.StatusCode.Should().Be(HttpStatusCode.OK);
        (await ReadDetailAsync(reread)).CacheTime.Should().Be(-30);

        int stored = await _fixture.Database.ScalarAsync<int>(
            "SELECT [CacheTime] FROM [dbo].[TabModules] WHERE [ModuleID] = @moduleId;",
            new Dictionary<string, object?> { ["moduleId"] = created.ModuleId });

        stored.Should().Be(-30, "the column accepts it, so nothing between the caller and it may rewrite it");
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
        page!.Meta.TotalCount.Should().BeGreaterThan(0);

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
            TabId = _fixture.Seed.RootTabId,
            ModuleTitle = "Renamed " + Suffix(),
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
    /// MIGRATION: these seven columns are EXCLUDED from the update contract, and this test asserts that
    /// exclusion end to end. They are NOT accepted as write-only fields: pane, alignment, colour, border,
    /// the print and syndicate flags and the
    /// container source are all Web Forms pane-layout, server-side rendering or skinning concerns, excluded by
    /// AAP 0.2.2.1 and 0.2.2.4, so no request or response contract in the module group declares any of them.
    /// The absence is asserted positively against all three contracts below, so a later change that quietly
    /// reintroduces one fails here.
    /// </para>
    /// <para>
    /// Because no caller can name them, the write path must PRESERVE the stored values rather than clear
    /// them - the pane column is NOT NULL, so clearing it would fail the write outright. That preservation is
    /// verified at unit level by the <c>ApplyUpdate</c> tests, which observe the entity directly; what is
    /// asserted here is that an update naming only the members the contract does carry is accepted, and that
    /// unknown appearance properties in the payload cannot smuggle a value through.
    /// </para>
    /// </remarks>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task UpdateModule_DeclaresNoAppearanceFieldOnAnyModuleContract()
    {
        using HttpClient client = _fixture.CreateHostClient();
        ModuleDetailDto created = await CreateModuleAsync(client, _fixture.Seed.RootTabId);

        var request = new UpdateModuleRequest
        {
            TabId = _fixture.Seed.RootTabId,
            ModuleTitle = created.ModuleTitle,
        };

        using HttpResponseMessage response = await client.PutAsJsonAsync(
            ModuleRoute(_fixture.Seed.PortalId, created.ModuleId),
            request,
            ApiTestFixture.Json);

        response.StatusCode.Should().Be(
            HttpStatusCode.OK,
            "an update naming only the members the contract carries is a legitimate request");

        foreach (string appearance in new[]
        {
            "PaneName", "Alignment", "Color", "Border", "DisplayPrint", "DisplaySyndicate", "ContainerSrc",
        })
        {
            typeof(UpdateModuleRequest).GetProperty(appearance).Should().BeNull(
                $"the placement's {appearance} drives server-side markup or skinning, both excluded, so the "
                + "update contract must not offer it");
        }

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

        using HttpResponseMessage repeated = await client.PutAsJsonAsync(
            ModuleRoute(_fixture.Seed.PortalId, created.ModuleId),
            new UpdateModuleRequest { TabId = _fixture.Seed.RootTabId, ModuleTitle = created.ModuleTitle },
            ApiTestFixture.Json);

        repeated.StatusCode.Should().Be(
            HttpStatusCode.OK,
            "a repeated full replacement is idempotent and must not be refused because the appearance "
            + "columns it cannot name still hold values");
    }

    /// <summary>
    /// A value longer than the column is refused by the request validator rather than by the store.
    /// </summary>
    /// <remarks>
    /// MIGRATION: this asserted the border bound until the border was excluded from the update contract along
    /// with the rest of the pane-layout and rendering columns - and with it went the fourth of the legacy
    /// screen's four validators, whose message read "Invalid Border (must be a number between 0 and 9)". The
    /// test's INTENT is preserved against a bound that still exists: <c>TabModules.IconFile</c> is
    /// <c>nvarchar(100)</c>. Letting a longer value reach SQL Server would surface as a truncation error
    /// rather than a field-level message, so the bound is asserted at the boundary. Note that the icon carries
    /// no legacy validator either, so this bound comes from the terminal schema alone.
    /// </remarks>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task UpdateModule_WithAnOverlongIconFile_ReturnsBadRequest()
    {
        using HttpClient client = _fixture.CreateHostClient();
        ModuleDetailDto created = await CreateModuleAsync(client, _fixture.Seed.RootTabId);

        using HttpResponseMessage response = await client.PutAsJsonAsync(
            ModuleRoute(_fixture.Seed.PortalId, created.ModuleId),
            new UpdateModuleRequest
            {
                TabId = _fixture.Seed.RootTabId,
                ModuleTitle = created.ModuleTitle,
                IconFile = new string('i', 101),
            },
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

    /// <summary>
    /// An update carrying a negative cache period is accepted and persists the value as submitted, matching
    /// the create path.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// The update counterpart of the create assertion. Both paths are asserted because the removed floor was
    /// declared twice - once on each request validator and once on each projection - so a fix applied to one
    /// path would leave the two disagreeing about the same column.
    /// </remarks>
    [Fact]
    public async Task UpdateModule_WithNegativeCachePeriod_PersistsItVerbatim()
    {
        using HttpClient client = _fixture.CreateHostClient();
        ModuleDetailDto created = await CreateModuleAsync(client, _fixture.Seed.RootTabId);

        using HttpResponseMessage response = await client.PutAsJsonAsync(
            ModuleRoute(_fixture.Seed.PortalId, created.ModuleId),
            new UpdateModuleRequest { TabId = _fixture.Seed.RootTabId, CacheTime = -1 },
            ApiTestFixture.Json);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await ReadDetailAsync(response)).CacheTime.Should().Be(-1);

        int stored = await _fixture.Database.ScalarAsync<int>(
            "SELECT [CacheTime] FROM [dbo].[TabModules] WHERE [ModuleID] = @moduleId;",
            new Dictionary<string, object?> { ["moduleId"] = created.ModuleId });

        stored.Should().Be(-1);
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
            .ReadEnvelopeAsync<ModuleSettingsDto>();

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
            .ReadEnvelopeAsync<ModuleSettingsDto>();

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
            .ReadEnvelopeAsync<ModuleSettingsDto>();

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

    /// <summary>
    /// An import by a caller holding no edit grant on the target module is REFUSED, and the module's stored
    /// content is untouched. The import's target arrives in the body, so no route-reading policy could reach
    /// it and the endpoint carried the bare authentication requirement - which meant any authenticated caller
    /// could overwrite any tenant's module content.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task ImportModule_WithoutModuleEditGrant_ReturnsForbidden()
    {
        using HttpClient host = _fixture.CreateHostClient();
        ModuleDetailDto created = await CreateModuleAsync(host, _fixture.Seed.RootTabId);

        using HttpClient client = MemberClient();

        using HttpResponseMessage response = await client.PostAsJsonAsync(
            ModuleImportRoute(_fixture.Seed.PortalId),
            new ModuleImportRequest
            {
                ModuleId = created.ModuleId,
                Content = "<content><item /></content>",
            },
            ApiTestFixture.Json);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    /// <summary>
    /// The import refusal is likewise a real evaluation: an edit grant on the module carries the request past
    /// authorisation and on to the capability check, which refuses it for an entirely different and
    /// non-authorisation reason. Reaching that reason is the proof that the grant was consulted and honoured.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task ImportModule_WithModuleEditGrant_ReachesTheCapabilityCheck()
    {
        using HttpClient host = _fixture.CreateHostClient();
        ModuleDetailDto created = await CreateModuleAsync(host, _fixture.Seed.RootTabId);

        // TWO gates stand in front of the capability check, and the caller has to clear both. The route names
        // a tenant, so it carries the tenant-bound administrator policy - without which a caller holding a
        // grant in its own tenant could import into another one, because the grant evaluation would judge its
        // own tenant's roles against the named tenant's grants. Past that, the service evaluates the module
        // EDIT grant itself, which is not implied by administering the tenant: only a host account is answered
        // affirmatively without a grant. So the grant is granted to the role the administrator holds, and
        // reaching the capability refusal is the proof that it was consulted and honoured.
        using HttpClient client = _fixture.CreateAdministratorClient();

        await GrantModulePermissionAsync(
            created.ModuleId,
            _fixture.Seed.ModuleEditPermissionId,
            _fixture.Seed.AdministratorRoleId,
            allowAccess: true);

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
        body.Should().Contain(
            "does not support content import",
            "the grant must carry the request past authorisation and into the capability check");
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

    /// <summary>
    /// The three module endpoints that name no module — the collection listing, creation and content import —
    /// are administrative, so an ordinary member of the tenant holding a perfectly valid token is refused all
    /// three.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// These endpoints were previously guarded by bare authentication, on the reasoning that a module-scoped
    /// permission policy has no module to evaluate against. That is true and beside the point: naming no module
    /// is a reason to choose a different policy, not a reason to have none. Any authenticated caller of any
    /// tenant could enumerate this tenant's content, add modules to its pages and import arbitrary content into
    /// it. The policy that applies is the tenant the route DOES name.
    /// </remarks>
    [Fact]
    public async Task ModuleEndpointsThatNameNoModule_AreRefusedToAnOrdinaryMember()
    {
        using HttpClient member = _fixture.CreateClientFor(
            _fixture.Seed.MemberUserId,
            IntegrationSeed.MemberUserName,
            _fixture.Seed.PortalId,
            isSuperUser: false,
            roles: [IntegrationSeed.RegisteredUsersRoleName]);

        int portalId = _fixture.Seed.PortalId;

        using HttpResponseMessage listed = await member.GetAsync(new Uri(
            $"/api/v1/portals/{Route(portalId)}/modules?pageIndex=0&pageSize=10",
            UriKind.Relative));

        listed.StatusCode.Should().Be(
            HttpStatusCode.Forbidden,
            "enumerating a tenant's modules is administrative, not merely authenticated");

        CreateModuleRequest attempted = NewModuleRequest(_fixture.Seed.RootTabId);
        string attemptedTitle = attempted.ModuleTitle!;

        using HttpResponseMessage created = await member.PostAsJsonAsync(
            ModulesRoute(portalId),
            attempted,
            ApiTestFixture.Json);

        created.StatusCode.Should().Be(HttpStatusCode.Forbidden);

        using HttpResponseMessage imported = await member.PostAsJsonAsync(
            new Uri($"/api/v1/portals/{Route(portalId)}/modules/import", UriKind.Relative),
            new ModuleImportRequest
            {
                ModuleId = 1,
                Content = "<content />",
            },
            ApiTestFixture.Json);

        imported.StatusCode.Should().Be(HttpStatusCode.Forbidden);

        // The refused creation left nothing behind, which is what distinguishes a refusal from a report of one.
        // The collection is read as host, because the member may not read it at all.
        using HttpClient host = _fixture.CreateHostClient();
        IReadOnlyList<ModuleListItemDto> modules = await ListModulesAsync(host, includeDeleted: true);
        modules.Should().NotContain(
            module => module.ModuleTitle == attemptedTitle,
            "a refused creation must not have written a row");
    }

    /// <summary>
    /// A module whose page grants view to the all-users pseudo-role is readable by a caller with no account at
    /// all, and one whose page grants view to the unauthenticated pseudo-role likewise.
    /// </summary>
    /// <param name="pseudoRoleId">The negative role identifier the grant is recorded against.</param>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// <para>
    /// This is the grant that an unconditional authentication requirement on the view policy silently deleted.
    /// Requirements inside one policy are ANDed, so demanding an authenticated caller made an anonymous request
    /// fail before the permission handler was ever consulted — and a grant that can never be evaluated is not a
    /// grant, it is a row that looks like one. Both identifiers are real principals in the migrated data:
    /// <c>-1</c> reaches everybody and <c>-3</c> reaches exactly the callers with no account.
    /// </para>
    /// <para>
    /// The grant is recorded against the module's PAGE rather than the module, because the module is created
    /// inheriting its view permission, which is the stock configuration.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData(-1)]
    [InlineData(-3)]
    public async Task GetModule_WhosePageGrantsViewToAPseudoRole_IsReachableAnonymously(int pseudoRoleId)
    {
        using HttpClient host = _fixture.CreateHostClient();
        ModuleDetailDto created = await CreateModuleAsync(host, _fixture.Seed.ChildTabId);

        using HttpClient anonymous = _fixture.CreateAnonymousClient();

        // Before the grant exists the same anonymous request is refused, which is what makes the affirmative
        // half below evidence of the grant rather than of an absent check.
        using HttpResponseMessage beforeGrant = await anonymous.GetAsync(
            ModuleRoute(_fixture.Seed.PortalId, created.ModuleId));

        beforeGrant.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        await GrantTabPermissionAsync(
            _fixture.Seed.ChildTabId,
            _fixture.Seed.TabViewPermissionId,
            pseudoRoleId,
            allowAccess: true);

        try
        {
            using HttpResponseMessage response = await anonymous.GetAsync(
                ModuleRoute(_fixture.Seed.PortalId, created.ModuleId));

            response.StatusCode.Should().Be(
                HttpStatusCode.OK,
                "the page grants view to a pseudo-role that reaches a caller with no account");
        }
        finally
        {
            // The seeded page is shared by every test in this suite, so the grant is withdrawn again rather
            // than left to widen unrelated assertions.
            await RevokeTabPermissionAsync(
                _fixture.Seed.ChildTabId,
                _fixture.Seed.TabViewPermissionId,
                pseudoRoleId);
        }
    }

    /// <summary>
    /// An anonymous caller is still refused a module EDIT, because no legacy grant reaches the unauthenticated
    /// pseudo-role for a mutation and an anonymous change has no account to attribute itself to.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// The companion to the test above, and the reason the two view policies and the two edit policies are
    /// composed differently: relaxing authentication is correct for reading a public page and wrong for writing
    /// to one. Asserting only the relaxation would not distinguish "anonymous reads are permitted" from
    /// "authentication was removed everywhere".
    /// </remarks>
    [Fact]
    public async Task UpdateModule_IsRefusedToAnAnonymousCallerEvenWhenThePageIsPublic()
    {
        using HttpClient host = _fixture.CreateHostClient();
        ModuleDetailDto created = await CreateModuleAsync(host, _fixture.Seed.ChildTabId);

        await GrantTabPermissionAsync(
            _fixture.Seed.ChildTabId,
            _fixture.Seed.TabViewPermissionId,
            roleId: -1,
            allowAccess: true);

        try
        {
            using HttpClient anonymous = _fixture.CreateAnonymousClient();

            using HttpResponseMessage response = await anonymous.PutAsJsonAsync(
                ModuleRoute(_fixture.Seed.PortalId, created.ModuleId),
                new UpdateModuleRequest
                {
                    ModuleTitle = "Anonymously renamed",
                    ModuleOrder = 3,
                    AllTabs = false,
                    InheritViewPermissions = true,
                    Visibility = ModuleVisibility.Maximized,
                    DisplayTitle = true,
                    CacheTime = 0,
                },
                ApiTestFixture.Json);

            response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

            // The title is unchanged, read back through a client that is entitled to read it.
            using HttpResponseMessage reread = await host.GetAsync(
                ModuleRoute(_fixture.Seed.PortalId, created.ModuleId));

            reread.StatusCode.Should().Be(HttpStatusCode.OK);
            ModuleDetailDto current = await ReadDetailAsync(reread);
            current.ModuleTitle.Should().NotBe("Anonymously renamed");
        }
        finally
        {
            await RevokeTabPermissionAsync(
                _fixture.Seed.ChildTabId,
                _fixture.Seed.TabViewPermissionId,
                roleId: -1);
        }
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

    /// <summary>
    /// Records or replaces one page permission grant, written directly for the same reason the module grant
    /// above is: the API exposes no grant-management endpoint, and the point of the test is the evaluation of
    /// stored grants rather than the means of storing them.
    /// </summary>
    /// <param name="tabId">The page the grant is recorded against.</param>
    /// <param name="permissionId">The catalogue entry being granted or denied.</param>
    /// <param name="roleId">
    /// The role the grant applies to. Negative identifiers are genuine principals rather than sentinels here:
    /// <c>-1</c> is the all-users pseudo-role and <c>-3</c> the unauthenticated one, and both are written
    /// unaltered.
    /// </param>
    /// <param name="allowAccess">Whether the grant allows or denies.</param>
    /// <returns>A task representing the write.</returns>
    private async Task GrantTabPermissionAsync(int tabId, int permissionId, int roleId, bool allowAccess)
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

    /// <summary>Withdraws one page permission grant.</summary>
    /// <param name="tabId">The page the grant was recorded against.</param>
    /// <param name="permissionId">The catalogue entry.</param>
    /// <param name="roleId">The role the grant applied to.</param>
    /// <returns>A task representing the write.</returns>
    /// <remarks>
    /// The seeded pages are shared by every test in this suite, so a test that widens a page's grants withdraws
    /// them again rather than leaving an unrelated assertion to be satisfied by a grant it never asked for.
    /// </remarks>
    private async Task RevokeTabPermissionAsync(int tabId, int permissionId, int roleId)
    {
        await _fixture.Database.ExecuteAsync(
            """
            DELETE FROM [dbo].[TabPermission]
            WHERE [TabID] = @tabId AND [PermissionID] = @permissionId AND [RoleID] = @roleId;
            """,
            new Dictionary<string, object?>
            {
                ["tabId"] = tabId,
                ["permissionId"] = permissionId,
                ["roleId"] = roleId,
            });
    }

    /// <summary>Mints a client for the seeded plain member, which holds no permission grant of any kind.</summary>
    /// <returns>An authenticated client with no module or page grants.</returns>
    private HttpClient MemberClient() => _fixture.CreateClientFor(
        _fixture.Seed.MemberUserId,
        IntegrationSeed.MemberUserName,
        _fixture.Seed.PortalId,
        isSuperUser: false,
        roles: [IntegrationSeed.RegisteredUsersRoleName]);

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
            .ReadEnvelopeAsync<ModuleDetailDto>();

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
            .ReadEnvelopeAsync<ModuleSettingsDto>();

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
