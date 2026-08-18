using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text;
using System.Text.Json;
using DnnMigration.Api.Controllers;
using DnnMigration.Api.Extensions;
using DnnMigration.Application.Dtos.Module;
using DnnMigration.Domain.Enums;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ApiExplorer;
using Microsoft.AspNetCore.Mvc.Formatters;
using Xunit;

namespace DnnMigration.IntegrationTests.Api;

/// <summary>Covers the module resource end to end, across the real HTTP pipeline and the real database.</summary>
/// <remarks>
/// <para>
/// Two behaviours of the module resource shape how these tests are written, and neither is obvious from the
/// route table.
/// </para>
/// <para>
/// The first is that reading and writing a module are gated by permission policies rather than by a role.
/// The handler behind those policies does not read the caller's token to decide whether it is a host
/// account: it resolves the account from the database and reads the stored super-user flag.
/// </para>
/// </remarks>
[Trait("Category", "Integration")]
[Collection(IntegrationTestCollection.Name)]
public sealed class ModuleApiTests
{
    /// <summary>Failure code reported when a request names a page the module is not placed on.</summary>
    private const string PlacementNotFoundCode = "module.placement_not_found";
    /// <summary>An identifier no seeded or created row can hold, used for the absent-resource paths.</summary>
    private const int UnknownModuleId = 987654;

    /// <summary>
    /// The widest window the paging contract admits, spelled out here rather than referenced from the
    /// validator so a fact does not silently follow a change to the rule it is measuring against.
    /// </summary>
    private const int MaximumPageSize = 100;

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
    /// definitions asserted below are that portal's.
    /// </remarks>
    [Fact]
    public async Task ListModuleDefinitions_ReturnsOkIncludingSeededDefinition()
    {
        using HttpClient client = await _fixture.CreateHostClientAsync();

        using HttpResponseMessage response = await client.GetAsync(
            new Uri("/api/v1/module-definitions", UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        string body = await response.Content.ReadAsStringAsync();

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
    /// The definition catalogue is administrator-only. A member of the tenant holding a perfectly valid
    /// token is refused, which proves the gate is portal administrator membership read from stored role
    /// assignments rather than mere authentication.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// The catalogue is installation-time reference data that only an administrator has any reason to
    /// browse, and the legacy screens it feeds were themselves administrator-gated.
    /// </remarks>
    [Fact]
    public async Task ListModuleDefinitions_AsMemberWithoutAdministratorRole_ReturnsForbidden()
    {
        using HttpClient client = await _fixture.CreateUnprivilegedClientAsync();

        using HttpResponseMessage response = await client.GetAsync(
            new Uri("/api/v1/module-definitions", UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    /// <summary>One definition is readable by its own identifier.</summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task GetModuleDefinition_ByIdentifier_ReturnsOkWithTheSeededDefinition()
    {
        using HttpClient client = await _fixture.CreateHostClientAsync();

        using HttpResponseMessage response = await client.GetAsync(new Uri(
            $"/api/v1/module-definitions/{_fixture.Seed.ModuleDefinitionId.ToString(CultureInfo.InvariantCulture)}",
            UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.OK);

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
        using HttpClient client = await _fixture.CreateHostClientAsync();

        using HttpResponseMessage response = await client.GetAsync(
            new Uri("/api/v1/module-definitions/987654", UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    /// <summary>The definitions of one package are readable by the package identifier.</summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task ListDesktopModuleDefinitions_ReturnsOkWithThatPackagesDefinitions()
    {
        using HttpClient client = await _fixture.CreateHostClientAsync();

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
    /// A package identifier naming nothing answers <c>404</c>, so it is distinguishable from an installed
    /// package that declares nothing here.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// This case previously answered <c>200 OK</c> with an empty array, which told a caller holding a stale
    /// or mistyped identifier that the package exists and is empty. An installed package declaring nothing
    /// still answers <c>200</c> with an empty array - that is the sibling case above, which reads the seeded
    /// package - so the empty answer now means one thing only.
    /// </remarks>
    [Fact]
    public async Task ListDesktopModuleDefinitions_WhenPackageUnknown_ReturnsNotFound()
    {
        using HttpClient client = await _fixture.CreateHostClientAsync();

        using HttpResponseMessage response = await client.GetAsync(
            new Uri("/api/v1/module-definitions/desktop-modules/987654", UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/problem+json");
    }

    /// <summary>Every definition read is refused to a caller who may place a module NOWHERE in the tenant.</summary>
    /// <param name="path">The address to attempt.</param>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// The precondition is CLEARED rather than assumed, because the suites share one database with no
    /// ordering guarantee: a sibling case granting EDIT to the registered role would otherwise decide this
    /// one's outcome by execution order.
    /// </remarks>
    [Theory]
    [InlineData("/api/v1/module-definitions")]
    [InlineData("/api/v1/module-definitions/1")]
    [InlineData("/api/v1/module-definitions/desktop-modules/1")]
    public async Task ModuleDefinitionReads_AsMemberHoldingNoPageGrant_ReturnForbidden(string path)
    {
        await ClearTenantEditGrantsAsync();

        using HttpClient client = await _fixture.CreateUnprivilegedClientAsync();

        using HttpResponseMessage response = await client.GetAsync(new Uri(path, UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    /// <summary>
    /// The catalogue IS served to a caller holding an edit grant on one page - the caller the create action
    /// admits and the placement form exists for.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// The grant is removed in a <c>finally</c> because it is tenant-wide state other suites read.
    /// </remarks>
    [Fact]
    public async Task ListModuleDefinitions_AsMemberHoldingOnePageEditGrant_ReturnsOk()
    {
        await ClearTenantEditGrantsAsync();

        await GrantTabPermissionAsync(
            _fixture.Seed.ChildTabId,
            _fixture.Seed.TabEditPermissionId,
            _fixture.Seed.RegisteredRoleId,
            allowAccess: true);

        try
        {
            using HttpClient client = await MemberClientAsync();

            using HttpResponseMessage response = await client.GetAsync(
                new Uri("/api/v1/module-definitions", UriKind.Relative));

            response.StatusCode.Should().Be(
                HttpStatusCode.OK,
                "a caller who may place a module on a page must be able to read what a module can be");
        }
        finally
        {
            await RevokeTabPermissionAsync(
                _fixture.Seed.ChildTabId,
                _fixture.Seed.TabEditPermissionId,
                _fixture.Seed.RegisteredRoleId);
        }
    }

    /// <summary>
    /// The definition catalogue exposes no mutator. Every write verb on both the collection address and a
    /// per-definition address is unroutable, because definitions are written only by module installation
    /// and that subsystem lies beyond this migration's scope.
    /// </summary>
    /// <param name="method">The write verb to attempt.</param>
    /// <param name="path">The catalogue address to attempt it against.</param>
    /// <returns>A task representing the test.</returns>
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
        using HttpClient client = await _fixture.CreateHostClientAsync();

        using HttpRequestMessage request = new(new HttpMethod(method), new Uri(path, UriKind.Relative));
        using HttpResponseMessage response = await client.SendAsync(request);

        response.StatusCode.Should().BeOneOf(HttpStatusCode.NotFound, HttpStatusCode.MethodNotAllowed);
    }

    /// <summary>A create answers <c>201 Created</c> with a location that resolves, and one placement lands.</summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task CreateModule_ReturnsCreatedWithResolvableLocation()
    {
        using HttpClient client = await _fixture.CreateHostClientAsync();

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
            $"/api/v1/modules/{Route(created.ModuleId)}");

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
        using HttpClient client = await _fixture.CreateHostClientAsync();

        CreateModuleRequest request = NewModuleRequest(_fixture.Seed.RootTabId);
        request.ModuleDefId = UnknownModuleId;

        using HttpResponseMessage response = await client.PostAsJsonAsync(
            ModulesRoute(_fixture.Seed.PortalId),
            request,
            ApiTestFixture.Json);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    /// <summary>
    /// A definition published by an administrative package is not part of the portal-placeable catalogue.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task CreateModule_WithAdministrativeDefinition_ReturnsNotFoundAndWritesNothing()
    {
        using HttpClient client = await _fixture.CreateHostClientAsync();
        int administrativeDefinitionId = await InsertAdministrativeDefinitionAsync();

        CreateModuleRequest request = NewModuleRequest(_fixture.Seed.RootTabId);
        request.ModuleDefId = administrativeDefinitionId;

        using HttpResponseMessage response = await client.PostAsJsonAsync(
            ModulesRoute(_fixture.Seed.PortalId),
            request,
            ApiTestFixture.Json);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);

        int written = await _fixture.Database.ScalarAsync<int>(
            """
            SELECT COUNT(*)
            FROM [dbo].[Modules]
            WHERE [PortalID] = @portalId AND [ModuleDefID] = @definitionId;
            """,
            new Dictionary<string, object?>
            {
                ["portalId"] = _fixture.Seed.PortalId,
                ["definitionId"] = administrativeDefinitionId,
            });

        written.Should().Be(0);
    }

    /// <summary>A create naming a page in another tenant is refused, which is a tenant-isolation guarantee.</summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task CreateModule_WithPageOutsideThePortal_ReturnsNotFound()
    {
        using HttpClient client = await _fixture.CreateHostClientAsync();

        CreateModuleRequest request = NewModuleRequest(UnknownTabId);

        using HttpResponseMessage response = await client.PostAsJsonAsync(
            ModulesRoute(_fixture.Seed.PortalId),
            request,
            ApiTestFixture.Json);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    /// <summary>
    /// A create by a caller holding no edit grant on the target page is REFUSED, and writes nothing.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task CreateModule_WithoutPageEditGrant_ReturnsForbiddenAndWritesNothing()
    {
        using HttpClient client = await MemberClientAsync();

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
    /// The refusal above is a real permission evaluation rather than a blanket denial: an edit grant
    /// recorded against the caller's role on the target page admits the same create, and a deny recorded
    /// beside it closes it again. Proving the denial alone could not distinguish "the grant is consulted"
    /// from "creation is simply closed to everybody but a host account".
    /// </summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task CreateModule_WithPageEditGrant_IsAdmittedAndThenRefusedByADeny()
    {
        using HttpClient client = await MemberClientAsync();

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
    /// A create presented with a credential minted for ANOTHER tenant is refused, and nothing is persisted
    /// - even though the account holds administration of the tenant the request reaches.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// THE REGRESSION TEST FOR THE ONE MUTATION WITHOUT AN ITEM POLICY. Every other module mutation names
    /// its module in the route, so its named policy reconciles three tenant identities before the action
    /// runs: the tenant the token was minted for, the tenant the request arrived through, and the tenant
    /// the operation targets.
    /// </remarks>
    [Fact]
    public async Task CreateModule_WithATokenMintedForAnotherTenant_ReturnsForbiddenAndWritesNothing()
    {
        using HttpClient host = await _fixture.CreateHostClientAsync();
        (int foreignPortalId, _, _) = await CreateForeignPortalAsync(host);

        await _fixture.Database.ExecuteAsync(
            """
            IF NOT EXISTS (SELECT 1 FROM [dbo].[UserPortals] WHERE [UserId] = @userId AND [PortalId] = @portalId)
                INSERT INTO [dbo].[UserPortals] ([UserId], [PortalId], [CreatedDate], [Authorised])
                VALUES (@userId, @portalId, SYSUTCDATETIME(), 1);
            """,
            new Dictionary<string, object?>
            {
                ["userId"] = _fixture.Seed.MemberUserId,
                ["portalId"] = foreignPortalId,
            });

        await GrantTabPermissionAsync(
            _fixture.Seed.ChildTabId,
            _fixture.Seed.TabEditPermissionId,
            _fixture.Seed.RegisteredRoleId,
            allowAccess: true);

        CreateModuleRequest request = NewModuleRequest(_fixture.Seed.ChildTabId);

        try
        {
            using HttpClient client = _fixture.CreateClientFor(
                _fixture.Seed.MemberUserId,
                IntegrationSeed.MemberUserName,
                foreignPortalId,
                isSuperUser: false,
                roles: [IntegrationSeed.RegisteredUsersRoleName]);

            using HttpResponseMessage response = await client.PostAsJsonAsync(
                ModulesRoute(_fixture.Seed.PortalId),
                request,
                ApiTestFixture.Json);

            response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
            await AssertTenantRefusalAsync(response, request.ModuleTitle);
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

    /// <summary>Asserts that a refusal came from the service's tenant comparison and that it wrote nothing.</summary>
    /// <param name="response">The refusal to examine.</param>
    /// <param name="moduleTitle">The title the refused request carried.</param>
    /// <returns>A task representing the assertions.</returns>
    /// <remarks>
    /// THE PROBLEM TYPE IS ASSERTED, NOT JUST THE STATUS, because this route can answer 403 for more than
    /// one reason: the pipeline refuses an unresolvable arrival tenant, and a restricted session, each with
    /// its own code and before the action runs. A status-only assertion would therefore pass whether or not
    /// the comparison inside the service exists at all, which is precisely the defect being pinned.
    /// </remarks>
    private async Task AssertTenantRefusalAsync(HttpResponseMessage response, string? moduleTitle)
    {
        using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        document.RootElement.TryGetProperty("type", out JsonElement type).Should().BeTrue();
        type.GetString().Should().Contain(
            "module.tenant_forbidden",
            "the refusal must come from the service's tenant comparison rather than from another 403");

        int written = await _fixture.Database.ScalarAsync<int>(
            "SELECT COUNT(*) FROM [dbo].[Modules] WHERE [ModuleID] IN "
            + "(SELECT [ModuleID] FROM [dbo].[TabModules] WHERE [ModuleTitle] = @title);",
            new Dictionary<string, object?> { ["title"] = moduleTitle });

        written.Should().Be(0, "a credential from another tenant must leave no module and no placement behind");
    }

    /// <summary>
    /// A page editor asking for an ALL-PAGES placement is refused, and nothing is persisted - while the
    /// same caller may still place the module on the one page it administers.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// THE REGRESSION TEST FOR THE ALL-PAGES ESCALATION ON CREATION. The legacy settings screen disabled
    /// <c>chkAllTabs</c> outright for any caller outside the portal administrator role, and the UPDATE path
    /// has gated that field on stored authority since the four portal-wide fields were grouped.
    /// </remarks>
    [Fact]
    public async Task CreateModule_AllPagesWithoutPortalAdministration_ReturnsForbiddenAndWritesNothing()
    {
        using HttpClient client = await MemberClientAsync();

        await GrantTabPermissionAsync(
            _fixture.Seed.ChildTabId,
            _fixture.Seed.TabEditPermissionId,
            _fixture.Seed.RegisteredRoleId,
            allowAccess: true);

        try
        {
            CreateModuleRequest everywhere = NewModuleRequest(_fixture.Seed.ChildTabId);
            everywhere.AllTabs = true;

            using HttpResponseMessage refused = await client.PostAsJsonAsync(
                ModulesRoute(_fixture.Seed.PortalId),
                everywhere,
                ApiTestFixture.Json);

            refused.StatusCode.Should().Be(HttpStatusCode.Forbidden);

            int written = await _fixture.Database.ScalarAsync<int>(
                "SELECT COUNT(*) FROM [dbo].[Modules] WHERE [ModuleID] IN "
                + "(SELECT [ModuleID] FROM [dbo].[TabModules] WHERE [ModuleTitle] = @title);",
                new Dictionary<string, object?> { ["title"] = everywhere.ModuleTitle });

            written.Should().Be(0, "a refused all-pages create must leave no module and no placement behind");

            // The same caller, the same page, the same grant - only the portal-wide instruction withdrawn.
            using HttpResponseMessage admitted = await client.PostAsJsonAsync(
                ModulesRoute(_fixture.Seed.PortalId),
                NewModuleRequest(_fixture.Seed.ChildTabId),
                ApiTestFixture.Json);

            admitted.StatusCode.Should().Be(HttpStatusCode.Created);
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
    /// A portal administrator asking for an ALL-PAGES placement is admitted, which is the counterpart that
    /// keeps the gate above from being satisfied by refusing everybody.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// The page edit grant is recorded against the ADMINISTRATOR role for the duration, because the two
    /// gates ask different questions and administering a tenant does not by itself produce a grant row: the
    /// permission service reads stored grants, and a tenant administrator whose pages carry no explicit
    /// grant holds no page EDIT. Granting it is what isolates this test to the portal-wide gate - without
    /// it the create would be refused by the page gate and the assertion would pass or fail for the wrong
    /// reason.
    /// </remarks>
    [Fact]
    public async Task CreateModule_AllPagesAsPortalAdministrator_IsAdmittedAndPlacedOnEveryContentPage()
    {
        using HttpClient client = await _fixture.CreateAdministratorClientAsync();

        await GrantTabPermissionAsync(
            _fixture.Seed.RootTabId,
            _fixture.Seed.TabEditPermissionId,
            _fixture.Seed.AdministratorRoleId,
            allowAccess: true);

        try
        {
            CreateModuleRequest request = NewModuleRequest(_fixture.Seed.RootTabId);
            request.AllTabs = true;

            using HttpResponseMessage response = await client.PostAsJsonAsync(
                ModulesRoute(_fixture.Seed.PortalId),
                request,
                ApiTestFixture.Json);

            response.StatusCode.Should().Be(HttpStatusCode.Created);

            // JOINED RATHER THAN CORRELATED, because the title is a column of [Modules] and not of
            // [TabModules]: an unqualified reference to it inside a subquery over the placement table
            // resolves against the enclosing query instead, which is a correlated read that answers a
            // different question.
            int placements = await _fixture.Database.ScalarAsync<int>(
                """
                SELECT COUNT(*)
                FROM [dbo].[TabModules] AS placement
                INNER JOIN [dbo].[Modules] AS module ON module.[ModuleID] = placement.[ModuleID]
                WHERE module.[ModuleTitle] = @title;
                """,
                new Dictionary<string, object?> { ["title"] = request.ModuleTitle });

            placements.Should().BeGreaterThan(
                1,
                "an administrator's all-pages placement still reaches every content page of the tenant");
        }
        finally
        {
            // The grant is portal-wide state that other suites read, so it is removed however this ends.
            await RevokeTabPermissionAsync(
                _fixture.Seed.RootTabId,
                _fixture.Seed.TabEditPermissionId,
                _fixture.Seed.AdministratorRoleId);
        }
    }

    /// <summary>A create with a negative cache period is REFUSED at the field, and nothing is written.</summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// <para>
    /// ⚠ THIS TEST WAS REVERSED, AND ITS PREDECESSOR ASSERTED A DOCUMENTED DIVERGENCE INTO EXISTENCE. It
    /// required that a negative period be "persisted verbatim" on the grounds that no legacy rule forbade
    /// it, which was factually true and is exactly why the gap existed: <c>modulesettings.ascx</c> L172
    /// declared only <c>CompareValidator Operator="DataTypeCheck" Type="Integer"</c>, so the legacy screen
    /// checked that the value was a whole number and nothing more.
    /// </para>
    /// <para>
    /// That omission was judged an oversight rather than a decision. The field's own legacy help text calls
    /// the value "the time this object is kept in the Cache", and a duration cannot run backwards - a
    /// negative period has no meaning the cache could act on. The bound is now enforced on the client at the
    /// field and here on the server, reporting the legacy resource file's own wording, <c>Invalid Cache
    /// Time</c>. NO UPPER BOUND IS IMPOSED: the legacy validator declared none, the column is an <c>int</c>,
    /// and inventing a ceiling would be a second unrequested divergence. Zero remains legal and means "do
    /// not cache". Recorded in MIGRATION_NOTES.md.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task CreateModule_WithNegativeCacheTime_IsRefusedAndNothingIsWritten()
    {
        using HttpClient client = await _fixture.CreateHostClientAsync();

        CreateModuleRequest request = NewModuleRequest(_fixture.Seed.RootTabId);
        request.CacheTime = -30;

        int before = await _fixture.Database.ScalarAsync<int>(
            "SELECT COUNT(1) FROM [dbo].[Modules];",
            new Dictionary<string, object?>());

        using HttpResponseMessage response = await client.PostAsJsonAsync(
            ModulesRoute(_fixture.Seed.PortalId),
            request,
            ApiTestFixture.Json);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        ValidationProblemDetails? problem = await response.Content
            .ReadFromJsonAsync<ValidationProblemDetails>(ApiTestFixture.Json);

        problem.Should().NotBeNull();
        problem!.Errors.Should().ContainKey(
            nameof(CreateModuleRequest.CacheTime),
            "the refusal names the offending field, so the client can mark that control and no other");
        problem.Errors[nameof(CreateModuleRequest.CacheTime)].Should().Contain(
            "Invalid Cache Time",
            "reported in the legacy resource file's own wording rather than a newly authored sentence");

        int after = await _fixture.Database.ScalarAsync<int>(
            "SELECT COUNT(1) FROM [dbo].[Modules];",
            new Dictionary<string, object?>());

        after.Should().Be(before, "a refused create writes nothing at all");
    }

    /// <summary>A create with a zero cache period is accepted: zero means "do not cache".</summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// The companion of the refusal above, and the reason the bound is "not negative" rather than "positive".
    /// </remarks>
    [Fact]
    public async Task CreateModule_WithZeroCacheTime_IsAcceptedAndStoredVerbatim()
    {
        using HttpClient client = await _fixture.CreateHostClientAsync();

        CreateModuleRequest request = NewModuleRequest(_fixture.Seed.RootTabId);
        request.CacheTime = 0;

        using HttpResponseMessage response = await client.PostAsJsonAsync(
            ModulesRoute(_fixture.Seed.PortalId),
            request,
            ApiTestFixture.Json);

        response.StatusCode.Should().Be(HttpStatusCode.Created);

        ModuleDetailDto created = await ReadDetailAsync(response);
        created.CacheTime.Should().Be(0, "zero is a legal period and is stored, not rewritten");

        int stored = await _fixture.Database.ScalarAsync<int>(
            "SELECT [CacheTime] FROM [dbo].[TabModules] WHERE [ModuleID] = @moduleId;",
            new Dictionary<string, object?> { ["moduleId"] = created.ModuleId });

        stored.Should().Be(0);
    }

    /// <summary>
    /// A create whose end date precedes its start date is ACCEPTED, and both bounds are stored verbatim.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task CreateModule_WithEndBeforeStart_IsAcceptedAndStoredVerbatim()
    {
        using HttpClient client = await _fixture.CreateHostClientAsync();

        DateTime start = new(2030, 6, 1, 0, 0, 0, DateTimeKind.Utc);
        DateTime end = new(2030, 5, 1, 0, 0, 0, DateTimeKind.Utc);

        CreateModuleRequest request = NewModuleRequest(_fixture.Seed.RootTabId);
        request.StartDate = start;
        request.EndDate = end;

        using HttpResponseMessage response = await client.PostAsJsonAsync(
            ModulesRoute(_fixture.Seed.PortalId),
            request,
            ApiTestFixture.Json);

        response.StatusCode.Should().Be(
            HttpStatusCode.Created,
            "the legacy screen compared the two bounds nowhere, so refusing the pair would narrow the "
            + "accepted input set");

        ModuleDetailDto created = await ReadDetailAsync(response);
        created.StartDate.Should().Be(start);
        created.EndDate.Should().Be(end, "the submitted window is stored as submitted, not reordered");

        using HttpResponseMessage reread = await client.GetAsync(
            ModuleRoute(_fixture.Seed.PortalId, created.ModuleId));

        reread.StatusCode.Should().Be(HttpStatusCode.OK);

        ModuleDetailDto persisted = await ReadDetailAsync(reread);
        persisted.StartDate.Should().Be(start);
        persisted.EndDate.Should().Be(end);

        DateTime storedEnd = await _fixture.Database.ScalarAsync<DateTime>(
            "SELECT [EndDate] FROM [dbo].[Modules] WHERE [ModuleID] = @moduleId;",
            new Dictionary<string, object?> { ["moduleId"] = created.ModuleId });

        storedEnd.Should().Be(end, "nothing between the caller and the column may rewrite the window");
    }

    /// <summary>An update whose end date precedes its start date is accepted on the same terms as a create.</summary>
    /// <remarks>
    /// MIGRATION: asserted on both write paths deliberately. One legacy screen served create and edit
    /// alike, so a rule present on one path and absent from the other would be a divergence introduced by
    /// this migration rather than one inherited from it.
    /// </remarks>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task UpdateModule_WithEndBeforeStart_IsAcceptedAndStoredVerbatim()
    {
        using HttpClient client = await _fixture.CreateHostClientAsync();
        ModuleDetailDto created = await CreateModuleAsync(client, _fixture.Seed.RootTabId);

        DateTime start = new(2031, 9, 1, 0, 0, 0, DateTimeKind.Utc);
        DateTime end = new(2031, 8, 1, 0, 0, 0, DateTimeKind.Utc);

        var request = new UpdateModuleRequest
        {
            TabId = _fixture.Seed.RootTabId,
            ModuleTitle = created.ModuleTitle,
            StartDate = start,
            EndDate = end,
        };

        using HttpResponseMessage response = await client.PutAsJsonAsync(
            ModuleRoute(_fixture.Seed.PortalId, created.ModuleId),
            request,
            ApiTestFixture.Json);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        ModuleDetailDto updated = await ReadDetailAsync(response);
        updated.StartDate.Should().Be(start);
        updated.EndDate.Should().Be(end);
    }

    /// <summary>
    /// Naming a different page on the relocation member MOVES the placement, and the placement selector
    /// continues to identify the page the module was read from.
    /// </summary>
    /// <remarks>
    /// ⚠ THIS IS THE FACT THAT WAS MISSING WHEN THE MOVE-TO-PAGE AFFORDANCE COULD NOT MOVE ANYTHING. The
    /// contract carried ONE page identifier, which the service reads to SELECT which placement is being
    /// updated - a module placed on several pages has one row per page, so without it there is no way to
    /// say which row the submitted values belong to.
    /// </remarks>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task UpdateModule_NamingAnotherPageAsTheDestination_MovesThePlacement()
    {
        using HttpClient client = await _fixture.CreateHostClientAsync();
        ModuleDetailDto created = await CreateModuleAsync(client, _fixture.Seed.RootTabId);

        created.TabId.Should().Be(_fixture.Seed.RootTabId, "the module is placed where it was created");

        var request = new UpdateModuleRequest
        {
            // The page the module was READ from, which is what selects the placement being updated.
            TabId = _fixture.Seed.RootTabId,
            // The page it should end up on.
            MoveToTabId = _fixture.Seed.ChildTabId,
            ModuleTitle = created.ModuleTitle,
        };

        using HttpResponseMessage response = await client.PutAsJsonAsync(
            ModuleRoute(_fixture.Seed.PortalId, created.ModuleId),
            request,
            ApiTestFixture.Json);

        response.StatusCode.Should().Be(
            HttpStatusCode.OK,
            "a destination the module does not yet occupy is a move, not a failed selection");

        ModuleDetailDto moved = await ReadDetailAsync(response);

        moved.TabId.Should().Be(
            _fixture.Seed.ChildTabId,
            "the response describes where the module IS, not the page it was moved off");
        moved.TabModuleId.Should().NotBe(
            created.TabModuleId,
            "a move replaces the placement row rather than editing it, exactly as the legacy copy-then-delete did");

        // The decisive assertion, made against the table rather than the response: exactly ONE placement,
        // and it is on the destination. A copy that failed to delete its source would leave two, and a
        // response-only check could not tell the two outcomes apart.
        int placementsOnSource = await _fixture.Database.ScalarAsync<int>(
            "SELECT COUNT(*) FROM [dbo].[TabModules] WHERE [ModuleID] = @moduleId AND [TabID] = @tabId;",
            new Dictionary<string, object?>
            {
                ["moduleId"] = created.ModuleId,
                ["tabId"] = _fixture.Seed.RootTabId,
            });

        int placementsOnDestination = await _fixture.Database.ScalarAsync<int>(
            "SELECT COUNT(*) FROM [dbo].[TabModules] WHERE [ModuleID] = @moduleId AND [TabID] = @tabId;",
            new Dictionary<string, object?>
            {
                ["moduleId"] = created.ModuleId,
                ["tabId"] = _fixture.Seed.ChildTabId,
            });

        placementsOnSource.Should().Be(0, "the page the module left no longer holds it");
        placementsOnDestination.Should().Be(1, "the destination holds it exactly once");
    }

    /// <summary>Naming the page the module already occupies is not a move, and changes no placement.</summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task UpdateModule_NamingItsOwnPageAsTheDestination_MovesNothing()
    {
        using HttpClient client = await _fixture.CreateHostClientAsync();
        ModuleDetailDto created = await CreateModuleAsync(client, _fixture.Seed.RootTabId);

        var request = new UpdateModuleRequest
        {
            TabId = _fixture.Seed.RootTabId,
            MoveToTabId = _fixture.Seed.RootTabId,
            ModuleTitle = created.ModuleTitle,
        };

        using HttpResponseMessage response = await client.PutAsJsonAsync(
            ModuleRoute(_fixture.Seed.PortalId, created.ModuleId),
            request,
            ApiTestFixture.Json);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        ModuleDetailDto updated = await ReadDetailAsync(response);

        updated.TabId.Should().Be(_fixture.Seed.RootTabId);
        updated.TabModuleId.Should().Be(
            created.TabModuleId,
            "the placement row survives untouched, which is what proves no copy-then-delete ran");
    }

    /// <summary>
    /// A destination that is not a content page of this portal is refused as a destination problem rather
    /// than as a missing placement or a permission problem.
    /// </summary>
    /// <remarks>
    /// The reason code matters as much as the status. <c>module.placement_not_found</c> answers "the page
    /// you named does not hold this module", which is a statement about the placement being SELECTED;
    /// <c>module.move_destination_invalid</c> answers "the page you named cannot receive this module".
    /// Reporting both through one code is what made the earlier behaviour unreadable.
    /// </remarks>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task UpdateModule_NamingAnUnknownDestination_ReportsADestinationProblem()
    {
        using HttpClient client = await _fixture.CreateHostClientAsync();
        ModuleDetailDto created = await CreateModuleAsync(client, _fixture.Seed.RootTabId);

        var request = new UpdateModuleRequest
        {
            TabId = _fixture.Seed.RootTabId,
            MoveToTabId = int.MaxValue,
            ModuleTitle = created.ModuleTitle,
        };

        using HttpResponseMessage response = await client.PutAsJsonAsync(
            ModuleRoute(_fixture.Seed.PortalId, created.ModuleId),
            request,
            ApiTestFixture.Json);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        string body = await response.Content.ReadAsStringAsync();

        body.Should().Contain("module.move_destination_invalid");
        body.Should().NotContain(
            "module.placement_not_found",
            "the placement was found; it is the destination that cannot receive the module");

        // The module has not moved, and the refusal reached the caller before anything was written.
        int placementsOnSource = await _fixture.Database.ScalarAsync<int>(
            "SELECT COUNT(*) FROM [dbo].[TabModules] WHERE [ModuleID] = @moduleId AND [TabID] = @tabId;",
            new Dictionary<string, object?>
            {
                ["moduleId"] = created.ModuleId,
                ["tabId"] = _fixture.Seed.RootTabId,
            });

        placementsOnSource.Should().Be(1, "a refused relocation writes nothing");
    }

    /// <summary>
    /// A page sitting in the recycle bin cannot receive a module, even though it belongs to the tenant.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// ⚠ ONE OF THE THREE EXCLUSIONS THAT DEFINE "A CONTENT PAGE OF THIS PORTAL", and the one most easily
    /// lost. The page repository deliberately returns recycled pages - the legacy read applied no predicate
    /// to <c>IsDeleted</c> and projected the column instead, so the exclusion is the caller's policy - and
    /// the destination check now reads ONE page rather than filtering the tenant's whole page set.
    /// </remarks>
    [Fact]
    public async Task UpdateModule_NamingARecycledPageAsTheDestination_ReportsADestinationProblem()
    {
        using HttpClient client = await _fixture.CreateHostClientAsync();
        ModuleDetailDto created = await CreateModuleAsync(client, _fixture.Seed.RootTabId);

        int recycledTabId = await _fixture.Database.ScalarAsync<int>(
            """
            INSERT INTO [dbo].[Tabs]
                ([TabOrder], [PortalID], [TabName], [IsVisible], [ParentId], [Level], [DisableLink],
                 [Title], [IsDeleted], [TabPath], [IsSecure])
            VALUES (98, @portalId, @tabName, 1, NULL, 0, 0, @tabName, 1, N'//' + @tabName, 0);
            SELECT CAST(SCOPE_IDENTITY() AS int);
            """,
            new Dictionary<string, object?>
            {
                ["portalId"] = _fixture.Seed.PortalId,
                ["tabName"] = "Recycled " + Suffix(),
            });

        var request = new UpdateModuleRequest
        {
            TabId = _fixture.Seed.RootTabId,
            MoveToTabId = recycledTabId,
            ModuleTitle = created.ModuleTitle,
        };

        using HttpResponseMessage response = await client.PutAsJsonAsync(
            ModuleRoute(_fixture.Seed.PortalId, created.ModuleId),
            request,
            ApiTestFixture.Json);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).Should().Contain("module.move_destination_invalid");

        int placementsOnSource = await _fixture.Database.ScalarAsync<int>(
            "SELECT COUNT(*) FROM [dbo].[TabModules] WHERE [ModuleID] = @moduleId AND [TabID] = @tabId;",
            new Dictionary<string, object?>
            {
                ["moduleId"] = created.ModuleId,
                ["tabId"] = _fixture.Seed.RootTabId,
            });

        placementsOnSource.Should().Be(1, "a refused relocation writes nothing");
    }

    /// <summary>
    /// A page belonging to ANOTHER tenant cannot receive this tenant's module, and is refused as a
    /// destination problem rather than reported as existing elsewhere.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// ⚠ THE TENANT IS PART OF THE LOOKUP AND NOT A TEST APPLIED AFTERWARDS, which is what this pins. The
    /// destination is now read with a single seek on both the page key and the portal, so a page owned by a
    /// neighbouring tenant does not come back at all and is indistinguishable from one that does not exist.
    /// </remarks>
    [Fact]
    public async Task UpdateModule_NamingAnotherTenantsPageAsTheDestination_ReportsADestinationProblem()
    {
        using HttpClient client = await _fixture.CreateHostClientAsync();
        ModuleDetailDto created = await CreateModuleAsync(client, _fixture.Seed.RootTabId);

        int foreignTabId = await _fixture.Database.ScalarAsync<int>(
            """
            DECLARE @otherPortalId int =
                (SELECT MIN([PortalID]) FROM [dbo].[Portals] WHERE [PortalID] <> @portalId);

            IF @otherPortalId IS NULL
            BEGIN
                INSERT INTO [dbo].[Portals] ([PortalName], [ExpiryDate], [AdministratorId])
                VALUES (@portalName, NULL, NULL);
                SET @otherPortalId = CAST(SCOPE_IDENTITY() AS int);
            END

            INSERT INTO [dbo].[Tabs]
                ([TabOrder], [PortalID], [TabName], [IsVisible], [ParentId], [Level], [DisableLink],
                 [Title], [IsDeleted], [TabPath], [IsSecure])
            VALUES (99, @otherPortalId, @tabName, 1, NULL, 0, 0, @tabName, 0, N'//' + @tabName, 0);
            SELECT CAST(SCOPE_IDENTITY() AS int);
            """,
            new Dictionary<string, object?>
            {
                ["portalId"] = _fixture.Seed.PortalId,
                ["portalName"] = "Neighbour " + Suffix(),
                ["tabName"] = "Foreign " + Suffix(),
            });

        var request = new UpdateModuleRequest
        {
            TabId = _fixture.Seed.RootTabId,
            MoveToTabId = foreignTabId,
            ModuleTitle = created.ModuleTitle,
        };

        using HttpResponseMessage response = await client.PutAsJsonAsync(
            ModuleRoute(_fixture.Seed.PortalId, created.ModuleId),
            request,
            ApiTestFixture.Json);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).Should().Contain("module.move_destination_invalid");
    }

    /// <summary>An unresolved request host cannot select a module collection.</summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task ListModules_FromAnUnclaimedHost_ReturnsForbidden()
    {
        using HttpClient client = await _fixture.CreateHostClientAsync("unclaimed-" + Suffix() + ".invalid");

        using HttpResponseMessage response = await client.GetAsync(
            new Uri("/api/v1/modules?pageIndex=0&pageSize=10", UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    /// <summary>The collection answers <c>200 OK</c> and carries a created module with its placement.</summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task ListModules_ReturnsOkContainingCreatedModule()
    {
        using HttpClient client = await _fixture.CreateHostClientAsync();
        ModuleDetailDto created = await CreateModuleAsync(client, _fixture.Seed.RootTabId);

        using HttpResponseMessage response = await client.GetAsync(
            new Uri(
                "/api/v1/modules?pageIndex=0&pageSize=100",
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
        using HttpClient client = await _fixture.CreateHostClientAsync();
        ModuleDetailDto onChild = await CreateModuleAsync(client, _fixture.Seed.ChildTabId);

        using HttpResponseMessage response = await client.GetAsync(
            new Uri(
                "/api/v1/modules"
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
        using HttpClient client = await _fixture.CreateHostClientAsync();
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
    /// The routes that carry no permission gate behave differently on purpose, and the tests beside this
    /// one prove it: a create naming a definition that does not exist answers <c>404</c>, and so does a
    /// read of a collection belonging to a tenant that does not exist. Those routes are reached by callers
    /// already entitled to know the tenant's contents, so nothing is disclosed by being precise.
    /// </remarks>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task GetModule_WhenUnresolvable_ReturnsNotFoundWithoutDisclosingExistence()
    {
        using HttpClient client = await _fixture.CreateHostClientAsync();

        using HttpResponseMessage response = await client.GetAsync(
            ModuleRoute(_fixture.Seed.PortalId, UnknownModuleId));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    /// <summary>
    /// A module that exists but belongs to another tenant is refused on the same terms, which is the reason
    /// the refusal above is uniform. This is a tenant-isolation guarantee, not a convenience.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task GetModule_WhenTokenTenantDiffersFromResolvedTenant_ReturnsForbidden()
    {
        using HttpClient host = await _fixture.CreateHostClientAsync();
        ModuleDetailDto created = await CreateModuleAsync(host, _fixture.Seed.RootTabId);

        using HttpClient client = _fixture.CreateClientFor(
            _fixture.Seed.AdminUserId,
            IntegrationSeed.AdminUserName,
            _fixture.Seed.PortalId + 5000,
            isSuperUser: false,
            roles: [IntegrationSeed.AdministratorsRoleName]);

        // The flat route derives its tenant from the request host. A token that names another tenant must not
        // turn that route into a cross-tenant alias for the same module identifier.
        using HttpResponseMessage response = await client.GetAsync(
            ModuleRoute(_fixture.Seed.PortalId, created.ModuleId));

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    /// <summary>A token issued by portal A cannot exercise a real user-specific module grant in portal B.</summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// the member is deliberately provisioned into both portals and receives a direct VIEW grant on portal
    /// B's module. Without token-to-route tenant binding every stored permission check below the policy
    /// would answer yes; the only reason for refusal is that the presented token was issued by portal A.
    /// </remarks>
    [Fact]
    public async Task GetModule_WithAuthorityInPortalBButATokenFromPortalA_ReturnsForbidden()
    {
        using HttpClient host = await _fixture.CreateHostClientAsync();
        (int portalId, string alias, int homeTabId) = await CreateForeignPortalAsync(host);
        int moduleId = await InsertForeignModuleAsync(portalId, homeTabId);

        await _fixture.Database.ExecuteAsync(
            """
            INSERT INTO [dbo].[UserPortals] ([UserId], [PortalId], [CreatedDate], [Authorised])
            VALUES (@userId, @portalId, SYSUTCDATETIME(), 1);

            INSERT INTO [dbo].[ModulePermission] ([ModuleID], [PermissionID], [UserID], [AllowAccess])
            VALUES (@moduleId, @permissionId, @userId, 1);
            """,
            new Dictionary<string, object?>
            {
                ["userId"] = _fixture.Seed.MemberUserId,
                ["portalId"] = portalId,
                ["moduleId"] = moduleId,
                ["permissionId"] = _fixture.Seed.ModuleViewPermissionId,
            });

        using HttpClient attacker = _fixture.CreateClientFor(
            _fixture.Seed.MemberUserId,
            IntegrationSeed.MemberUserName,
            _fixture.Seed.PortalId,
            isSuperUser: false,
            roles: [IntegrationSeed.RegisteredUsersRoleName]);
        attacker.BaseAddress = new Uri($"http://{alias}", UriKind.Absolute);

        using HttpResponseMessage response = await attacker.GetAsync(ModuleRoute(portalId, moduleId));

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    /// <summary>
    /// A caller with no permission grant is refused the read. This is the assertion that proves the policy
    /// is enforced from stored grants rather than from the caller's own claims: the member account holds a
    /// valid token and is a member of the tenant, and is still refused because nothing grants it this
    /// module.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task GetModule_AsMemberWithoutGrant_ReturnsForbidden()
    {
        using HttpClient host = await _fixture.CreateHostClientAsync();
        ModuleDetailDto created = await CreateModuleAsync(host, _fixture.Seed.RootTabId);

        using HttpClient client = await _fixture.CreateUnprivilegedClientAsync();

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
    /// <remarks>
    /// THE GRANT THIS EXERCISES IS EDIT, BECAUSE THAT IS WHAT THE DETAIL READ NOW REQUIRES. It used to
    /// grant VIEW, which is how it came to certify the disclosure recorded on the action itself: a caller
    /// holding nothing but a view grant received the module's whole administrative record.
    /// </remarks>
    [Fact]
    public async Task GetModule_WithRoleGrant_IsAdmittedAndThenRefusedByADeny()
    {
        using HttpClient host = await _fixture.CreateHostClientAsync();
        ModuleDetailDto created = await CreateModuleAsync(host, _fixture.Seed.RootTabId);

        using HttpClient client = await _fixture.CreateUnprivilegedClientAsync();

        // A module that inherits its view permission from its page would be answered from the page's grants
        // instead, so inheritance is switched off first to make this test about the module's own grant.
        await _fixture.Database.ExecuteAsync(
            "UPDATE [dbo].[Modules] SET [InheritViewPermissions] = 0 WHERE [ModuleID] = @moduleId;",
            new Dictionary<string, object?> { ["moduleId"] = created.ModuleId });

        await GrantModulePermissionAsync(
            created.ModuleId,
            _fixture.Seed.ModuleEditPermissionId,
            _fixture.Seed.RegisteredRoleId,
            allowAccess: true);

        using HttpResponseMessage admitted = await client.GetAsync(
            ModuleRoute(_fixture.Seed.PortalId, created.ModuleId));

        admitted.StatusCode.Should().Be(HttpStatusCode.OK);

        await GrantModulePermissionAsync(
            created.ModuleId,
            _fixture.Seed.ModuleEditPermissionId,
            _fixture.Seed.RegisteredRoleId,
            allowAccess: false);

        using HttpResponseMessage refused = await client.GetAsync(
            ModuleRoute(_fixture.Seed.PortalId, created.ModuleId));

        refused.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    /// <summary>
    /// A signed-in caller holding a module VIEW grant and no EDIT grant is refused the module's
    /// administrative record.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task GetModule_WithViewGrantButWithoutEdit_ReturnsForbidden()
    {
        using HttpClient host = await _fixture.CreateHostClientAsync();
        ModuleDetailDto created = await CreateModuleAsync(host, _fixture.Seed.RootTabId);

        await _fixture.Database.ExecuteAsync(
            "UPDATE [dbo].[Modules] SET [InheritViewPermissions] = 0 WHERE [ModuleID] = @moduleId;",
            new Dictionary<string, object?> { ["moduleId"] = created.ModuleId });

        await GrantModulePermissionAsync(
            created.ModuleId,
            _fixture.Seed.ModuleViewPermissionId,
            _fixture.Seed.RegisteredRoleId,
            allowAccess: true);

        int viewGrants = await _fixture.Database.ScalarAsync<int>(
            """
            SELECT COUNT(*) FROM [dbo].[ModulePermission]
            WHERE [ModuleID] = @moduleId AND [PermissionID] = @permissionId AND [AllowAccess] = 1;
            """,
            new Dictionary<string, object?>
            {
                ["moduleId"] = created.ModuleId,
                ["permissionId"] = _fixture.Seed.ModuleViewPermissionId,
            });

        viewGrants.Should().Be(1, "the refusal below must be the gate's answer, not a missing grant");

        using HttpClient member = await _fixture.CreateUnprivilegedClientAsync();

        using HttpResponseMessage refused = await member.GetAsync(
            ModuleRoute(_fixture.Seed.PortalId, created.ModuleId));

        refused.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    /// <summary>
    /// Every address that consumes a module's administrative record admits the same callers, so a read is
    /// never refused where a write succeeds.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task GetModule_AdmitsExactlyTheCallersItsSettingsReadAdmits()
    {
        using HttpClient host = await _fixture.CreateHostClientAsync();
        ModuleDetailDto created = await CreateModuleAsync(host, _fixture.Seed.RootTabId);

        await _fixture.Database.ExecuteAsync(
            "UPDATE [dbo].[Modules] SET [InheritViewPermissions] = 0 WHERE [ModuleID] = @moduleId;",
            new Dictionary<string, object?> { ["moduleId"] = created.ModuleId });

        int grants = await _fixture.Database.ScalarAsync<int>(
            "SELECT COUNT(*) FROM [dbo].[ModulePermission] WHERE [ModuleID] = @moduleId;",
            new Dictionary<string, object?> { ["moduleId"] = created.ModuleId });

        grants.Should().Be(0, "the exposed contract writes no module-scope grant");

        Uri detailRoute = ModuleRoute(_fixture.Seed.PortalId, created.ModuleId);
        Uri settingsRoute = ModuleSettingsRoute(_fixture.Seed.PortalId, created.ModuleId);

        foreach (HttpClient caller in new[] { host })
        {
            using HttpResponseMessage detail = await caller.GetAsync(detailRoute);
            using HttpResponseMessage settings = await caller.GetAsync(settingsRoute);

            detail.StatusCode.Should().Be(
                settings.StatusCode,
                "a module's detail read and its settings read consume the same record and must admit the "
                    + "same callers");

            detail.StatusCode.Should().Be(HttpStatusCode.OK, await Diagnose(detail));
        }

        using HttpClient member = await _fixture.CreateUnprivilegedClientAsync();

        using HttpResponseMessage memberDetail = await member.GetAsync(detailRoute);
        using HttpResponseMessage memberSettings = await member.GetAsync(settingsRoute);

        memberDetail.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        memberSettings.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    /// <summary>An update answers <c>200 OK</c> and the new state survives a read.</summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task UpdateModule_ReturnsOkAndPersists()
    {
        using HttpClient client = await _fixture.CreateHostClientAsync();
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

    /// <summary>A <c>tabId</c> naming a page the module is not placed on is REFUSED, and nothing is moved.</summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// The absence is asserted with everything the withdrawn behaviour would have disturbed - the source
    /// placement, its order, its page-scoped setting and the absence of any placement on the named page -
    /// so a re-introduction fails here rather than passing as a 200 nobody inspected.
    /// </remarks>
    [Fact]
    public async Task UpdateModule_NamingAPageTheModuleIsNotPlacedOn_IsRefusedAndMovesNothing()
    {
        using HttpClient client = await _fixture.CreateHostClientAsync();
        ModuleDetailDto created = await CreateModuleAsync(client, _fixture.Seed.RootTabId);

        await _fixture.Database.ExecuteAsync(
            """
            INSERT INTO [dbo].[TabModuleSettings] ([TabModuleID], [SettingName], [SettingValue])
            VALUES (@tabModuleId, N'theme', N'legacy');
            """,
            new Dictionary<string, object?> { ["tabModuleId"] = created.TabModuleId });

        using HttpResponseMessage response = await client.PutAsJsonAsync(
            ModuleRoute(_fixture.Seed.PortalId, created.ModuleId),
            new UpdateModuleRequest
            {
                TabId = _fixture.Seed.ChildTabId,
                ModuleTitle = created.ModuleTitle,
                AllTabs = false,
                ModuleOrder = 99,
            },
            ApiTestFixture.Json);

        response.StatusCode.Should().Be(
            HttpStatusCode.NotFound,
            "the request names a placement that does not exist, which is an absent resource rather than a "
            + "malformed request");

        ProblemDetails? problem = await response.Content.ReadFromJsonAsync<ProblemDetails>(ApiTestFixture.Json);

        problem.Should().NotBeNull();
        problem!.Type.Should().Contain(
            PlacementNotFoundCode,
            "the refusal names the placement rule, so a client can tell it from an absent module");

        int sourcePlacements = await _fixture.Database.ScalarAsync<int>(
            """
            SELECT COUNT(*)
            FROM [dbo].[TabModules]
            WHERE [ModuleID] = @moduleId AND [TabID] = @tabId;
            """,
            new Dictionary<string, object?>
            {
                ["moduleId"] = created.ModuleId,
                ["tabId"] = _fixture.Seed.RootTabId,
            });

        int destinationPlacements = await _fixture.Database.ScalarAsync<int>(
            """
            SELECT COUNT(*)
            FROM [dbo].[TabModules]
            WHERE [ModuleID] = @moduleId AND [TabID] = @tabId;
            """,
            new Dictionary<string, object?>
            {
                ["moduleId"] = created.ModuleId,
                ["tabId"] = _fixture.Seed.ChildTabId,
            });

        string retainedSetting = await _fixture.Database.ScalarAsync<string>(
            """
            SELECT [SettingValue]
            FROM [dbo].[TabModuleSettings]
            WHERE [TabModuleID] = @tabModuleId AND [SettingName] = N'theme';
            """,
            new Dictionary<string, object?> { ["tabModuleId"] = created.TabModuleId });

        sourcePlacements.Should().Be(1, "the placement the module really has is untouched by a refusal");
        destinationPlacements.Should().Be(0, "no placement is created on the page the request named");
        retainedSetting.Should().Be(
            "legacy",
            "the page-scoped setting belongs to the surviving placement and is not carried anywhere");
    }

    /// <summary>Omitting the selected page is a malformed update rather than an implicit move to page zero.</summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task UpdateModule_WithoutTabId_ReturnsBadRequest()
    {
        using HttpClient client = await _fixture.CreateHostClientAsync();
        ModuleDetailDto created = await CreateModuleAsync(client, _fixture.Seed.RootTabId);

        using HttpResponseMessage response = await client.PutAsJsonAsync(
            ModuleRoute(_fixture.Seed.PortalId, created.ModuleId),
            new { moduleTitle = created.ModuleTitle },
            ApiTestFixture.Json);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    /// <summary>
    /// The three appearance columns the legacy settings screen administered are on the write path and on the
    /// detail response; the four that only a renderer ever chose are on neither, and are preserved rather than
    /// cleared by an update that cannot name them.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The three - alignment, colour and border - are declared as fields at `modulesettings.ascx:L120-L139`,
    /// read at `ModuleSettings.ascx.vb:L144-L147` and saved at `:L345-L347`, so they are part of the legacy
    /// screen's workflow. They were on no contract at all, which is why the settings screen told an operator
    /// the module stored no settings of its own while `dbo.TabModules` held a value for each of them.
    /// </para>
    /// <para>
    /// The four that remain excluded are a different case. A pane is chosen by the skin that declares it, a
    /// container IS a skin object, and print and syndicate are affordances of the legacy rendering pipeline.
    /// Because no caller can name them, the write path must PRESERVE their stored values rather than clear them
    /// - the pane column is NOT NULL, so clearing it would fail the write outright.
    /// </para>
    /// </remarks>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task UpdateModule_CarriesTheAdministeredAppearanceAndPreservesTheRest()
    {
        using HttpClient client = await _fixture.CreateHostClientAsync();
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
            "PaneName", "DisplayPrint", "DisplaySyndicate", "ContainerSrc",
        })
        {
            typeof(UpdateModuleRequest).GetProperty(appearance).Should().BeNull(
                $"the placement's {appearance} drives server-side markup or skinning, both excluded, so the "
                + "update contract must not offer it");
        }

        foreach (string administered in new[] { "Alignment", "Color", "Border" })
        {
            typeof(UpdateModuleRequest).GetProperty(administered).Should().NotBeNull(
                $"the legacy settings screen let an administrator set {administered}, so the update contract "
                + "must carry it or the workflow is gone");

            typeof(ModuleDetailDto).GetProperty(administered).Should().NotBeNull(
                $"and a screen cannot show the stored {administered} it is about to replace unless the detail "
                + "contract reports it");
        }

        // A round trip through the write path, proving the three are genuinely stored and read back rather
        // than merely declared. The empty string is used for the alignment because that is the value behind
        // the legacy list's "Not Specified" entry, and it must survive as an empty string.
        using HttpResponseMessage appearanceWrite = await client.PutAsJsonAsync(
            ModuleRoute(_fixture.Seed.PortalId, created.ModuleId),
            new UpdateModuleRequest
            {
                TabId = _fixture.Seed.RootTabId,
                ModuleTitle = created.ModuleTitle,
                Alignment = "center",
                Color = "#003366",
                Border = "3",
            },
            ApiTestFixture.Json);

        appearanceWrite.StatusCode.Should().Be(HttpStatusCode.OK);

        using HttpResponseMessage appearanceRead = await client.GetAsync(
            ModuleRoute(_fixture.Seed.PortalId, created.ModuleId));

        appearanceRead.StatusCode.Should().Be(HttpStatusCode.OK);

        ModuleDetailDto storedAppearance = await ReadDetailAsync(appearanceRead);

        storedAppearance.Alignment.Should().Be("center");
        storedAppearance.Color.Should().Be("#003366");
        storedAppearance.Border.Should().Be("3");

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
            // Including the three administered ones: they are COLUMNS ON THE PLACEMENT, not key-value
            // settings, so they belong to the detail contract and must not be duplicated onto this one.
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
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task UpdateModule_WithAnOverlongIconFile_ReturnsBadRequest()
    {
        using HttpClient client = await _fixture.CreateHostClientAsync();
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
    public async Task UpdateModule_WhenUnresolvable_ReturnsNotFound()
    {
        using HttpClient client = await _fixture.CreateHostClientAsync();

        using HttpResponseMessage response = await client.PutAsJsonAsync(
            ModuleRoute(_fixture.Seed.PortalId, UnknownModuleId),
            new UpdateModuleRequest { ModuleTitle = "No such module" },
            ApiTestFixture.Json);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    /// <summary>
    /// An update carrying a negative cache period is REFUSED, and the stored period is left untouched.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// ⚠ REVERSED FOR THE REASON SET OUT ON THE CREATE COUNTERPART. Both paths are still asserted, and for
    /// the original reason: the rule is declared once on each request validator, so a change applied to one
    /// path would leave the two disagreeing about the same column. The value submitted here is <c>-1</c>
    /// specifically, because that is the value the legacy sentinel vocabulary used for "absent" and is
    /// therefore the one most likely to be sent by accident.
    /// </remarks>
    [Fact]
    public async Task UpdateModule_WithNegativeCachePeriod_IsRefusedAndLeavesTheStoredPeriod()
    {
        using HttpClient client = await _fixture.CreateHostClientAsync();
        ModuleDetailDto created = await CreateModuleAsync(client, _fixture.Seed.RootTabId);

        int before = await _fixture.Database.ScalarAsync<int>(
            "SELECT [CacheTime] FROM [dbo].[TabModules] WHERE [ModuleID] = @moduleId;",
            new Dictionary<string, object?> { ["moduleId"] = created.ModuleId });

        using HttpResponseMessage response = await client.PutAsJsonAsync(
            ModuleRoute(_fixture.Seed.PortalId, created.ModuleId),
            new UpdateModuleRequest { TabId = _fixture.Seed.RootTabId, CacheTime = -1 },
            ApiTestFixture.Json);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        ValidationProblemDetails? problem = await response.Content
            .ReadFromJsonAsync<ValidationProblemDetails>(ApiTestFixture.Json);

        problem.Should().NotBeNull();
        problem!.Errors.Should().ContainKey(nameof(UpdateModuleRequest.CacheTime));
        problem.Errors[nameof(UpdateModuleRequest.CacheTime)].Should().Contain("Invalid Cache Time");

        int stored = await _fixture.Database.ScalarAsync<int>(
            "SELECT [CacheTime] FROM [dbo].[TabModules] WHERE [ModuleID] = @moduleId;",
            new Dictionary<string, object?> { ["moduleId"] = created.ModuleId });

        stored.Should().Be(before, "a refused update leaves the column exactly as it was");
    }

    // MIGRATION: A FACT ASSERTING THAT AN ADMINISTRATOR'S SUBMISSION MOVES THE PLACEMENT WAS WITHDRAWN
    // HERE. It read the submitted page as the legacy screen's page picker and asserted the stored page, the
    // stored order and a single surviving placement row afterwards.

    /// <summary>
    /// Each administrator-only field is refused for a caller that holds the module edit grant but does not
    /// administer the portal, and nothing is written.
    /// </summary>
    /// <remarks>
    /// The legacy settings screen disabled the page picker, the every-page checkbox and both propagation
    /// checkboxes outright for any caller outside the portal administrator role, in the page load and again
    /// in the save handler.
    /// </remarks>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task UpdateModule_AsNonAdministratorHoldingEditGrant_RefusesAdministratorOnlyFields()
    {
        using HttpClient host = await _fixture.CreateHostClientAsync();
        ModuleDetailDto created = await CreateModuleAsync(host, _fixture.Seed.RootTabId);

        using HttpClient client = await _fixture.CreateUnprivilegedClientAsync();

        // The caller genuinely holds the module edit grant, which is what makes this a test of the field
        // rule rather than of the route policy: without the grant every request below would be refused for
        // a different reason and would prove nothing.
        await GrantModulePermissionAsync(
            created.ModuleId,
            _fixture.Seed.ModuleEditPermissionId,
            _fixture.Seed.RegisteredRoleId,
            allowAccess: true);

        // And may edit both pages, so a refusal cannot be attributed to the destination grant either.
        await GrantTabPermissionAsync(
            _fixture.Seed.RootTabId,
            _fixture.Seed.TabEditPermissionId,
            _fixture.Seed.RegisteredRoleId,
            allowAccess: true);
        await GrantTabPermissionAsync(
            _fixture.Seed.ChildTabId,
            _fixture.Seed.TabEditPermissionId,
            _fixture.Seed.RegisteredRoleId,
            allowAccess: true);

        try
        {
            // THREE FIELDS, NOT FOUR. A submission naming a DIFFERENT page was the first entry here,
            // because the revision that wrote this fact read the page as a move command and counted it
            // among the administrator-only fields.
            UpdateModuleRequest[] administratorOnly =
            [
                new() { TabId = _fixture.Seed.RootTabId, ModuleTitle = created.ModuleTitle, AllTabs = true },
                new()
                {
                    TabId = _fixture.Seed.RootTabId,
                    ModuleTitle = created.ModuleTitle,
                    SetAsDefaultSettings = true,
                },
                new()
                {
                    TabId = _fixture.Seed.RootTabId,
                    ModuleTitle = created.ModuleTitle,
                    ApplyToAllModules = true,
                },
            ];

            foreach (UpdateModuleRequest request in administratorOnly)
            {
                using HttpResponseMessage response = await client.PutAsJsonAsync(
                    ModuleRoute(_fixture.Seed.PortalId, created.ModuleId),
                    request,
                    ApiTestFixture.Json);

                response.StatusCode.Should().Be(
                    HttpStatusCode.Forbidden,
                    "a page-scoped grant does not carry authority over the tenant");
            }

            using HttpResponseMessage ordinary = await client.PutAsJsonAsync(
                ModuleRoute(_fixture.Seed.PortalId, created.ModuleId),
                new UpdateModuleRequest
                {
                    TabId = _fixture.Seed.RootTabId,
                    ModuleTitle = "Renamed by a page editor",
                },
                ApiTestFixture.Json);

            ordinary.StatusCode.Should().Be(HttpStatusCode.OK);

            int storedTabId = await _fixture.Database.ScalarAsync<int>(
                "SELECT [TabID] FROM [dbo].[TabModules] WHERE [TabModuleID] = @tabModuleId;",
                new Dictionary<string, object?> { ["tabModuleId"] = created.TabModuleId });

            storedTabId.Should().Be(
                _fixture.Seed.RootTabId,
                "the placement's page is write-once after creation, whatever a request submits");

            int allTabs = await _fixture.Database.ScalarAsync<int>(
                "SELECT CAST([AllTabs] AS int) FROM [dbo].[Modules] WHERE [ModuleID] = @moduleId;",
                new Dictionary<string, object?> { ["moduleId"] = created.ModuleId });

            allTabs.Should().Be(0, "the refused fan-out must not have reached the column either");
        }
        finally
        {
            await RevokeTabPermissionAsync(
                _fixture.Seed.RootTabId,
                _fixture.Seed.TabEditPermissionId,
                _fixture.Seed.RegisteredRoleId);
            await RevokeTabPermissionAsync(
                _fixture.Seed.ChildTabId,
                _fixture.Seed.TabEditPermissionId,
                _fixture.Seed.RegisteredRoleId);
        }
    }

    /// <summary>A page in another tenant, and a page that does not exist, are refused identically.</summary>
    /// <remarks>
    /// This was written as a fact about a MOVE's destination being validated before the projection assigned
    /// it. Under the reconciled contract the submitted page selects the placement instead, so both values
    /// are refused one step earlier and for a stronger reason - no placement of this module exists on
    /// either page - and neither can reach a column because nothing is projected at all.
    /// </remarks>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task UpdateModule_WhenTheDestinationPageIsNotInTheTenant_ReturnsNotFound()
    {
        using HttpClient client = await _fixture.CreateHostClientAsync();
        ModuleDetailDto created = await CreateModuleAsync(client, _fixture.Seed.RootTabId);

        using HttpResponseMessage response = await client.PutAsJsonAsync(
            ModuleRoute(_fixture.Seed.PortalId, created.ModuleId),
            new UpdateModuleRequest { TabId = UnknownTabId, ModuleTitle = created.ModuleTitle },
            ApiTestFixture.Json);

        response.StatusCode.Should().Be(
            HttpStatusCode.NotFound,
            "the update path uses the same non-enumerating answer as module creation for an unknown or "
            + "cross-tenant page");

        string body = await response.Content.ReadAsStringAsync();
        body.Should().Contain(
            "is not placed on page",
            "the refusal names the placement rule, and it names it identically for a page that does not "
            + "exist and for one this tenant cannot see");

        int storedTabId = await _fixture.Database.ScalarAsync<int>(
            "SELECT [TabID] FROM [dbo].[TabModules] WHERE [TabModuleID] = @tabModuleId;",
            new Dictionary<string, object?> { ["tabModuleId"] = created.TabModuleId });

        storedTabId.Should().Be(_fixture.Seed.RootTabId);
    }

    /// <summary>
    /// An import into a module whose package names a business controller this installation does not
    /// register is REFUSED, and nothing is committed or recorded.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task ImportModule_WhenTheControllerIsNotRegistered_RefusesAndRecordsNothing()
    {
        using HttpClient host = await _fixture.CreateHostClientAsync();
        ModuleDetailDto created = await CreateModuleAsync(host, _fixture.Seed.RootTabId);

        // 1 is the portable capability bit, reproducing the legacy SupportedFeatures encoding exactly.
        await _fixture.Database.ExecuteAsync(
            """
            UPDATE [dbo].[DesktopModules]
               SET [BusinessControllerClass] = @controllerClass,
                   [SupportedFeatures] = 1
             WHERE [DesktopModuleID] = @desktopModuleId;
            """,
            new Dictionary<string, object?>
            {
                ["controllerClass"] = "Measured.Modules.UnregisteredController",
                ["desktopModuleId"] = _fixture.Seed.DesktopModuleId,
            });

        try
        {
            // The host has one process-wide recording sink, shared by every suite in this serial
            // collection. Count only this module's import records before and after the request instead of
            // assuming that every log written while this fact runs belongs to it.
            bool IsThisModulesImportRecord(LogRecord record) =>
                Equals(record.Properties.GetValueOrDefault("AuditEvent"), "MODULE_UPDATED")
                && Equals(record.Properties.GetValueOrDefault("AuditResourceType"), "Module")
                && Equals(
                    record.Properties.GetValueOrDefault("AuditResourceId"),
                    created.ModuleId.ToString(CultureInfo.InvariantCulture))
                && record.Properties.GetValueOrDefault("AuditProperties")?.ToString()?.Contains(
                    "Operation=Import",
                    StringComparison.Ordinal) == true;

            int importAuditCountBefore = RecordedLogs.Snapshot().Count(IsThisModulesImportRecord);

            using HttpResponseMessage response = await host.PostAsJsonAsync(
                ModuleImportRoute(_fixture.Seed.PortalId),
                new ModuleImportRequest
                {
                    ModuleId = created.ModuleId,
                    Content = "<content type=\"IntegrationDesktopModule\" version=\"01.00.00\">x</content>",
                    Folder = "Portals/0/",
                    FileName = "content.xml",
                },
                ApiTestFixture.Json);

            response.StatusCode.Should().Be(
                HttpStatusCode.BadRequest,
                "an import that restored no content is not something a caller may be told succeeded");

            string body = await response.Content.ReadAsStringAsync();
            body.Should().Contain(
                "No business controller is registered",
                "the refusal names the installation's own limitation rather than blaming the document");

            RecordedLogs.Snapshot().Count(IsThisModulesImportRecord).Should().Be(
                importAuditCountBefore,
                "an audit record claiming an import happened is worse than no record at all");
        }
        finally
        {
            await _fixture.Database.ExecuteAsync(
                """
                UPDATE [dbo].[DesktopModules]
                   SET [BusinessControllerClass] = NULL,
                       [SupportedFeatures] = 0
                 WHERE [DesktopModuleID] = @desktopModuleId;
                """,
                new Dictionary<string, object?> { ["desktopModuleId"] = _fixture.Seed.DesktopModuleId });
        }
    }

    /// <summary>Reading and writing the settings projection round-trips both scopes of setting.</summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task ModuleSettings_RoundTripBothScopes()
    {
        using HttpClient client = await _fixture.CreateHostClientAsync();
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
    /// An explicit anonymous VIEW grant discloses neither the settings contract nor the module's
    /// administrative record, because both require an authenticated editor.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    /// <remarks>
    /// The intermediate assertion is <c>401</c> rather than <c>200</c> BECAUSE THE DETAIL READ MOVED TO THE
    /// EDIT POLICY, and the change is what this test now certifies for the anonymous caller: an
    /// unauthorised grant, whatever it permits, reaches no address that returns the module's configuration.
    /// </remarks>
    [Fact]
    public async Task GetModuleSettings_WithAnonymousViewGrant_ReturnsUnauthorized()
    {
        using HttpClient host = await _fixture.CreateHostClientAsync();
        ModuleDetailDto created = await CreateModuleAsync(host, _fixture.Seed.RootTabId);

        await _fixture.Database.ExecuteAsync(
            "UPDATE [dbo].[Modules] SET [InheritViewPermissions] = 0 WHERE [ModuleID] = @moduleId;",
            new Dictionary<string, object?> { ["moduleId"] = created.ModuleId });
        await GrantModulePermissionAsync(
            created.ModuleId,
            _fixture.Seed.ModuleViewPermissionId,
            roleId: -1,
            allowAccess: true);

        using HttpClient anonymous = _fixture.CreateAnonymousClient();

        using HttpResponseMessage visibleModule = await anonymous.GetAsync(
            ModuleRoute(_fixture.Seed.PortalId, created.ModuleId));
        visibleModule.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        using HttpResponseMessage settings = await anonymous.GetAsync(
            ModuleSettingsRoute(_fixture.Seed.PortalId, created.ModuleId));
        settings.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    /// <summary>
    /// A signed-in caller who holds a module VIEW grant and no EDIT grant reaches neither the module's raw
    /// settings nor its administrative record.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task GetModuleSettings_WithViewButWithoutEdit_ReturnsForbidden()
    {
        using HttpClient host = await _fixture.CreateHostClientAsync();
        ModuleDetailDto created = await CreateModuleAsync(host, _fixture.Seed.RootTabId);

        await _fixture.Database.ExecuteAsync(
            "UPDATE [dbo].[Modules] SET [InheritViewPermissions] = 0 WHERE [ModuleID] = @moduleId;",
            new Dictionary<string, object?> { ["moduleId"] = created.ModuleId });
        await GrantModulePermissionAsync(
            created.ModuleId,
            _fixture.Seed.ModuleViewPermissionId,
            _fixture.Seed.RegisteredRoleId,
            allowAccess: true);

        using HttpClient member = await MemberClientAsync();

        using HttpResponseMessage visibleModule = await member.GetAsync(
            ModuleRoute(_fixture.Seed.PortalId, created.ModuleId));
        visibleModule.StatusCode.Should().Be(HttpStatusCode.Forbidden);

        using HttpResponseMessage settings = await member.GetAsync(
            ModuleSettingsRoute(_fixture.Seed.PortalId, created.ModuleId));
        settings.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    /// <summary>
    /// the administrator of a tenant this API provisioned can carry out every module administration
    /// operation on that tenant's own module, without any module-scope grant existing anywhere.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    /// <remarks>
    /// Every write endpoint is exercised in ONE test on purpose. They share a single decision, so a
    /// representative endpoint would leave the rest able to regress silently, and the whole point of the
    /// finding was that the surface failed uniformly.
    /// </remarks>
    [Fact]
    public async Task ModuleAdministration_IsAvailableToTheTenantsOwnAdministrator()
    {
        using HttpClient host = await _fixture.CreateHostClientAsync();
        TenantFacts tenant = await CreateTenantWithAdministratorAsync(host);

        using HttpClient administrator = await _fixture.CreateTenantClientAsync(
            tenant.Alias,
            tenant.PortalId,
            tenant.AdministratorUserName);

        // Created by the administrator itself rather than by the host, so the create endpoint is measured
        // under the same authority as every write below it. It is admitted by the page EDIT grant the
        // portal provisioning writes for the tenant's administrator role.
        using HttpResponseMessage created = await administrator.PostAsJsonAsync(
            ModulesRoute(tenant.PortalId),
            NewModuleRequest(tenant.HomeTabId),
            ApiTestFixture.Json);

        created.StatusCode.Should().Be(
            HttpStatusCode.Created,
            "the tenant's administrator may place a module on its own home page");

        ModuleDetailDto module = await ReadDetailAsync(created);

        // The premise of the finding, stated as an assertion rather than assumed: not one module-scope grant
        // exists for this module, so nothing but the tenant-administrator alternative can admit the writes.
        int moduleGrants = await _fixture.Database.ScalarAsync<int>(
            "SELECT COUNT(*) FROM [dbo].[ModulePermission] WHERE [ModuleID] = @moduleId;",
            new Dictionary<string, object?> { ["moduleId"] = module.ModuleId });

        moduleGrants.Should().Be(
            0,
            "the exposed contract writes no module-scope grant, which is what made the missing alternatives fatal");

        using HttpResponseMessage listed = await administrator.GetAsync(
            ModuleListingRoute(tenant.PortalId, pageSize: 50));
        listed.StatusCode.Should().Be(HttpStatusCode.OK);

        using HttpResponseMessage read = await administrator.GetAsync(
            ModuleRoute(tenant.PortalId, module.ModuleId));
        read.StatusCode.Should().Be(HttpStatusCode.OK);

        using HttpResponseMessage updated = await administrator.PutAsJsonAsync(
            ModuleRoute(tenant.PortalId, module.ModuleId),
            new UpdateModuleRequest { TabId = tenant.HomeTabId, ModuleTitle = "Administered by the tenant" },
            ApiTestFixture.Json);

        updated.StatusCode.Should().Be(HttpStatusCode.OK, await Diagnose(updated));
        (await ReadDetailAsync(updated)).ModuleTitle.Should().Be("Administered by the tenant");

        Uri settingsRoute = ModuleSettingsRoute(tenant.PortalId, module.ModuleId);

        using HttpResponseMessage settingsRead = await administrator.GetAsync(settingsRoute);
        settingsRead.StatusCode.Should().Be(HttpStatusCode.OK, await Diagnose(settingsRead));

        using HttpResponseMessage settingsWritten = await administrator.PutAsJsonAsync(
            settingsRoute,
            new ModuleSettingsDto
            {
                ModuleId = module.ModuleId,
                TabModuleId = module.TabModuleId,
                ModuleSettings = new Dictionary<string, string> { ["ShowSummary"] = "True" },
                TabModuleSettings = new Dictionary<string, string> { ["ColumnWidth"] = "240" },
            },
            ApiTestFixture.Json);

        settingsWritten.StatusCode.Should().Be(HttpStatusCode.NoContent, await Diagnose(settingsWritten));

        using HttpResponseMessage exported = await administrator.PostAsJsonAsync(
            ModuleExportRoute(tenant.PortalId, module.ModuleId),
            new ModuleExportRequest { FileName = "content.xml" },
            ApiTestFixture.Json);

        exported.StatusCode.Should().Be(
            HttpStatusCode.BadRequest,
            "authorisation admits the export and the package's own capability refuses it");
        (await exported.Content.ReadAsStringAsync()).Should().Contain("does not support content export");

        using HttpResponseMessage imported = await administrator.PostAsJsonAsync(
            ModuleImportRoute(tenant.PortalId),
            new ModuleImportRequest
            {
                ModuleId = module.ModuleId,
                Content = "<content type=\"IntegrationDesktopModule\" version=\"01.00.00\"><item /></content>",
            },
            ApiTestFixture.Json);

        imported.StatusCode.Should().Be(
            HttpStatusCode.BadRequest,
            "authorisation admits the import and the package's own capability refuses it");
        (await imported.Content.ReadAsStringAsync()).Should().Contain("does not support content import");

        using HttpResponseMessage removed = await administrator.DeleteAsync(
            ModuleRoute(tenant.PortalId, module.ModuleId));

        removed.StatusCode.Should().Be(HttpStatusCode.NoContent, await Diagnose(removed));

        bool deleted = await _fixture.Database.ScalarAsync<bool>(
            "SELECT [IsDeleted] FROM [dbo].[Modules] WHERE [ModuleID] = @moduleId;",
            new Dictionary<string, object?> { ["moduleId"] = module.ModuleId });

        deleted.Should().BeTrue("the removal is the legacy soft delete, and it must actually have happened");
    }

    /// <summary>
    /// an ordinary member holding the EDIT grant on the page a module sits on may administer that module,
    /// and the same grant on a page the module does NOT sit on admits nothing.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    /// <remarks>
    /// The member holds no module-scope grant in either half, which is what makes the page grant the only
    /// thing under examination.
    /// </remarks>
    [Fact]
    public async Task ModuleAdministration_FollowsThePagesEditGrantAndOnlyForThePagesTheModuleOccupies()
    {
        using HttpClient host = await _fixture.CreateHostClientAsync();
        ModuleDetailDto module = await CreateModuleAsync(host, _fixture.Seed.RootTabId);

        using HttpClient member = await MemberClientAsync();

        Uri settingsRoute = ModuleSettingsRoute(_fixture.Seed.PortalId, module.ModuleId);

        try
        {
            // The negative half FIRST, so the refusal cannot be explained by a grant that had not been
            // written yet: the member administers a different page of the same tenant, and the module is
            // not on it.
            await GrantTabPermissionAsync(
                _fixture.Seed.ChildTabId,
                _fixture.Seed.TabEditPermissionId,
                _fixture.Seed.RegisteredRoleId,
                allowAccess: true);

            using HttpResponseMessage elsewhere = await member.GetAsync(settingsRoute);

            elsewhere.StatusCode.Should().Be(
                HttpStatusCode.Forbidden,
                "administering one page confers nothing over a module placed on another");

            await GrantTabPermissionAsync(
                _fixture.Seed.RootTabId,
                _fixture.Seed.TabEditPermissionId,
                _fixture.Seed.RegisteredRoleId,
                allowAccess: true);

            using HttpResponseMessage admitted = await member.GetAsync(settingsRoute);

            admitted.StatusCode.Should().Be(
                HttpStatusCode.OK,
                await Diagnose(admitted));

            using HttpResponseMessage updated = await member.PutAsJsonAsync(
                ModuleRoute(_fixture.Seed.PortalId, module.ModuleId),
                new UpdateModuleRequest { TabId = _fixture.Seed.RootTabId, ModuleTitle = "Edited by a page editor" },
                ApiTestFixture.Json);

            updated.StatusCode.Should().Be(HttpStatusCode.OK, await Diagnose(updated));
        }
        finally
        {
            await RevokeTabPermissionAsync(
                _fixture.Seed.RootTabId,
                _fixture.Seed.TabEditPermissionId,
                _fixture.Seed.RegisteredRoleId);
            await RevokeTabPermissionAsync(
                _fixture.Seed.ChildTabId,
                _fixture.Seed.TabEditPermissionId,
                _fixture.Seed.RegisteredRoleId);
        }
    }

    /// <summary>
    /// the installation host account administers a module in a tenant it holds no membership row in.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    /// <remarks>
    /// The absence of the membership row is ASSERTED rather than assumed, because the finding was
    /// originally diagnosed by inserting one and watching the refusal turn into success: a fixture that
    /// happened to provision the row would make this test pass while the defect was still present.
    /// </remarks>
    [Fact]
    public async Task ModuleAdministration_IsAvailableToTheHostAccountInATenantItIsNotAMemberOf()
    {
        using HttpClient seededHost = await _fixture.CreateHostClientAsync();
        TenantFacts tenant = await CreateTenantWithAdministratorAsync(seededHost);

        int membershipRows = await _fixture.Database.ScalarAsync<int>(
            """
            SELECT COUNT(*) FROM [dbo].[UserPortals]
            WHERE [UserId] = @userId AND [PortalId] = @portalId;
            """,
            new Dictionary<string, object?>
            {
                ["userId"] = _fixture.Seed.HostUserId,
                ["portalId"] = tenant.PortalId,
            });

        membershipRows.Should().Be(
            0,
            "the whole point of the finding is that a host account holds no membership row in a created tenant");

        int moduleId = await InsertForeignModuleAsync(tenant.PortalId, tenant.HomeTabId);

        using HttpClient host = await AuthenticatedClientFactory.CreateAuthenticatedClientAsync(
            _fixture,
            IntegrationSeed.HostUserName,
            ApiTestFixture.KnownPassword,
            tenant.PortalId,
            tenant.Alias);

        using HttpResponseMessage read = await host.GetAsync(ModuleRoute(tenant.PortalId, moduleId));
        read.StatusCode.Should().Be(HttpStatusCode.OK, await Diagnose(read));

        using HttpResponseMessage settings = await host.GetAsync(
            ModuleSettingsRoute(tenant.PortalId, moduleId));
        settings.StatusCode.Should().Be(HttpStatusCode.OK, await Diagnose(settings));

        using HttpResponseMessage updated = await host.PutAsJsonAsync(
            ModuleRoute(tenant.PortalId, moduleId),
            new UpdateModuleRequest { TabId = tenant.HomeTabId, ModuleTitle = "Administered by the host" },
            ApiTestFixture.Json);

        updated.StatusCode.Should().Be(HttpStatusCode.OK, await Diagnose(updated));

        using HttpResponseMessage removed = await host.DeleteAsync(ModuleRoute(tenant.PortalId, moduleId));
        removed.StatusCode.Should().Be(HttpStatusCode.NoContent, await Diagnose(removed));
    }

    /// <summary>
    /// The generic settings surface cannot read or overwrite an administrative module's security rows, even
    /// for a host caller; the typed privileged endpoint remains the only authority for those values.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task GenericModuleSettings_RefuseAdministrativeModuleAndPreserveSecurityRows()
    {
        using HttpClient host = await _fixture.CreateHostClientAsync();
        int administrativeModuleId = await InsertAdministrativeModuleAsync();
        Uri route = ModuleSettingsRoute(_fixture.Seed.PortalId, administrativeModuleId);

        using HttpResponseMessage read = await host.GetAsync(route);
        read.StatusCode.Should().Be(HttpStatusCode.Forbidden);

        using HttpResponseMessage write = await host.PutAsJsonAsync(
            route,
            new ModuleSettingsDto
            {
                ModuleId = administrativeModuleId,
                ModuleSettings = new Dictionary<string, string>
                {
                    ["Security_EmailValidation"] = "replacement",
                },
                TabModuleSettings = new Dictionary<string, string>(),
            },
            ApiTestFixture.Json);

        write.StatusCode.Should().Be(HttpStatusCode.Forbidden);

        string persisted = await _fixture.Database.ScalarAsync<string>(
            """
            SELECT [SettingValue]
            FROM [dbo].[ModuleSettings]
            WHERE [ModuleID] = @moduleId AND [SettingName] = N'Security_EmailValidation';
            """,
            new Dictionary<string, object?> { ["moduleId"] = administrativeModuleId });

        persisted.Should().Be("original-expression");
    }

    /// <summary>
    /// A module that is placed on no page can still hold module-scoped settings, and is told plainly that
    /// it cannot hold placement-scoped ones.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task UpdateModuleSettings_ForAnUnplacedModule_AcceptsModuleScopeAndRefusesPlacementScope()
    {
        using HttpClient client = await _fixture.CreateHostClientAsync();
        ModuleDetailDto created = await CreateModuleAsync(client, _fixture.Seed.RootTabId);

        using HttpResponseMessage withdrawn = await client.DeleteAsync(new Uri(
            $"/api/v1/modules/{Route(created.ModuleId)}"
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
    public async Task GetModuleSettings_WhenUnresolvable_ReturnsNotFound()
    {
        using HttpClient client = await _fixture.CreateHostClientAsync();

        using HttpResponseMessage response = await client.GetAsync(
            ModuleSettingsRoute(_fixture.Seed.PortalId, UnknownModuleId));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    /// <summary>Writing the settings of an unknown module answers <c>404 Not Found</c>.</summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task UpdateModuleSettings_WhenUnresolvable_ReturnsNotFound()
    {
        using HttpClient client = await _fixture.CreateHostClientAsync();

        using HttpResponseMessage response = await client.PutAsJsonAsync(
            ModuleSettingsRoute(_fixture.Seed.PortalId, UnknownModuleId),
            new ModuleSettingsDto
            {
                ModuleId = UnknownModuleId,
                ModuleSettings = new Dictionary<string, string>(),
                TabModuleSettings = new Dictionary<string, string>(),
            },
            ApiTestFixture.Json);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    /// <summary>
    /// A delete answers <c>204 No Content</c>. The module is then hidden from the collection but still
    /// recorded, because a module delete is reversible by design.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task DeleteModule_ReturnsNoContentAndHidesItFromTheCollection()
    {
        using HttpClient client = await _fixture.CreateHostClientAsync();
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
    /// because withdrawing a module from one page is not the same act as removing the module - while the
    /// module still occupies another page.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task DeleteModulePlacement_WhenAnotherPlacementSurvives_RemovesOnlyThePlacement()
    {
        using HttpClient client = await _fixture.CreateHostClientAsync();

        // CREATED ON EVERY PAGE, so the delete below is not the last placement. Without a surviving
        // placement the module would be recycled with it, which the sibling test asserts.
        CreateModuleRequest everywhere = NewModuleRequest(_fixture.Seed.RootTabId);
        everywhere.AllTabs = true;

        using HttpResponseMessage createResponse = await client.PostAsJsonAsync(
            ModulesRoute(_fixture.Seed.PortalId),
            everywhere,
            ApiTestFixture.Json);

        createResponse.StatusCode.Should().Be(HttpStatusCode.Created);
        ModuleDetailDto created = await ReadDetailAsync(createResponse);

        int before = await _fixture.Database.ScalarAsync<int>(
            "SELECT COUNT(*) FROM [dbo].[TabModules] WHERE [ModuleID] = @moduleId;",
            new Dictionary<string, object?> { ["moduleId"] = created.ModuleId });

        before.Should().BeGreaterThan(1, "the fan-out must have produced more than one placement");

        using HttpResponseMessage response = await client.DeleteAsync(new Uri(
            $"/api/v1/modules/{Route(created.ModuleId)}"
                + $"?tabModuleId={Route(created.TabModuleId)}",
            UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);

        int placements = await _fixture.Database.ScalarAsync<int>(
            "SELECT COUNT(*) FROM [dbo].[TabModules] WHERE [ModuleID] = @moduleId;",
            new Dictionary<string, object?> { ["moduleId"] = created.ModuleId });

        placements.Should().Be(before - 1);

        int deletedModules = await _fixture.Database.ScalarAsync<int>(
            "SELECT COUNT(*) FROM [dbo].[Modules] WHERE [ModuleID] = @moduleId AND [IsDeleted] = 1;",
            new Dictionary<string, object?> { ["moduleId"] = created.ModuleId });

        deletedModules.Should().Be(0);
    }

    /// <summary>
    /// A delete addressed at the LAST placement removes the placement AND recycles the module, so no live
    /// module is left standing on no page.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// This is the path the listing's own delete command uses, and it used to leave <c>Modules.IsDeleted</c>
    /// at zero with no <c>dbo.TabModules</c> row: the module vanished from the listing, the detail and
    /// settings reads answered 404 because both select through a placement, and the permission read answered
    /// 200 because it does not. Legacy <c>ModuleController.vb</c> <c>DeleteTabModule</c> L847-L855 soft-deleted
    /// the module in exactly this case.
    /// </remarks>
    [Fact]
    public async Task DeleteModulePlacement_WhenItIsTheLastPlacement_RecyclesTheModuleToo()
    {
        using HttpClient client = await _fixture.CreateHostClientAsync();
        ModuleDetailDto created = await CreateModuleAsync(client, _fixture.Seed.RootTabId);

        using HttpResponseMessage response = await client.DeleteAsync(new Uri(
            $"/api/v1/modules/{Route(created.ModuleId)}"
                + $"?tabModuleId={Route(created.TabModuleId)}",
            UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);

        int placements = await _fixture.Database.ScalarAsync<int>(
            "SELECT COUNT(*) FROM [dbo].[TabModules] WHERE [ModuleID] = @moduleId;",
            new Dictionary<string, object?> { ["moduleId"] = created.ModuleId });

        placements.Should().Be(0);

        int recycled = await _fixture.Database.ScalarAsync<int>(
            "SELECT COUNT(*) FROM [dbo].[Modules] WHERE [ModuleID] = @moduleId AND [IsDeleted] = 1;",
            new Dictionary<string, object?> { ["moduleId"] = created.ModuleId });

        recycled.Should().Be(1, "a module on no page must not remain live");
    }

    /// <summary>A delete addressing a placement of another module is refused.</summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task DeleteModulePlacement_WhenPlacementBelongsElsewhere_ReturnsNotFound()
    {
        using HttpClient client = await _fixture.CreateHostClientAsync();
        ModuleDetailDto first = await CreateModuleAsync(client, _fixture.Seed.RootTabId);
        ModuleDetailDto second = await CreateModuleAsync(client, _fixture.Seed.ChildTabId);

        using HttpResponseMessage response = await client.DeleteAsync(new Uri(
            $"/api/v1/modules/{Route(first.ModuleId)}"
                + $"?tabModuleId={Route(second.TabModuleId)}",
            UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    /// <summary>
    /// A delete against a module the permission gate cannot resolve is refused before the action runs.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task DeleteModule_WhenUnresolvable_ReturnsNotFound()
    {
        using HttpClient client = await _fixture.CreateHostClientAsync();

        using HttpResponseMessage response = await client.DeleteAsync(
            ModuleRoute(_fixture.Seed.PortalId, UnknownModuleId));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    /// <summary>
    /// An export of a package that declares no content capability is refused rather than answered with an
    /// empty document, and the refusal names the reason.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task ExportModule_WhenPackageIsNotPortable_ReturnsBadRequestNamingTheReason()
    {
        using HttpClient client = await _fixture.CreateHostClientAsync();
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
        using HttpClient client = await _fixture.CreateHostClientAsync();
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
    public async Task ExportModule_WhenUnresolvable_ReturnsNotFound()
    {
        using HttpClient client = await _fixture.CreateHostClientAsync();

        using HttpResponseMessage response = await client.PostAsJsonAsync(
            ModuleExportRoute(_fixture.Seed.PortalId, UnknownModuleId),
            new ModuleExportRequest { FileName = "content.xml" },
            ApiTestFixture.Json);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    /// <summary>An import naming a module in another tenant answers <c>404 Not Found</c>.</summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task ImportModule_WhenModuleUnknown_ReturnsNotFound()
    {
        using HttpClient client = await _fixture.CreateHostClientAsync();

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
        using HttpClient client = await _fixture.CreateHostClientAsync();
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
    /// content is untouched. The import's target arrives in the body, so no route-reading policy could
    /// reach it and the endpoint carried the bare authentication requirement - which meant any
    /// authenticated caller could overwrite any tenant's module content.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task ImportModule_WithoutModuleEditGrant_ReturnsForbidden()
    {
        using HttpClient host = await _fixture.CreateHostClientAsync();
        ModuleDetailDto created = await CreateModuleAsync(host, _fixture.Seed.RootTabId);

        using HttpClient client = await MemberClientAsync();

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
    /// The import refusal is likewise a real evaluation: an edit grant on the module carries the request
    /// past authorisation and on to the capability check, which refuses it for an entirely different and
    /// non-authorisation reason. Reaching that reason is the proof that the grant was consulted and
    /// honoured.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task ImportModule_WithModuleEditGrant_ReachesTheCapabilityCheck()
    {
        using HttpClient host = await _fixture.CreateHostClientAsync();
        ModuleDetailDto created = await CreateModuleAsync(host, _fixture.Seed.RootTabId);

        // TWO gates stand in front of the capability check, and the caller has to clear both.
        using HttpClient client = await _fixture.CreateAdministratorClientAsync();

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
        using HttpClient client = await _fixture.CreateHostClientAsync();
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
    /// A DTD is rejected before module-owned code runs, and parser implementation text is not published in
    /// the problem response.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task ImportModule_WithDtd_ReturnsFixedSafeBadRequest()
    {
        using HttpClient client = await _fixture.CreateHostClientAsync();
        (int moduleId, string moduleName) = await InsertPortableModuleAsync();

        string document =
            $"<!DOCTYPE content [<!ENTITY xxe SYSTEM \"file:///etc/passwd\">]>"
            + $"<content type=\"{moduleName}\">&xxe;</content>";

        using HttpResponseMessage response = await client.PostAsJsonAsync(
            ModuleImportRoute(_fixture.Seed.PortalId),
            new ModuleImportRequest { ModuleId = moduleId, Content = document },
            ApiTestFixture.Json);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        string body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("could not be parsed safely as portable module content");
        body.Should().NotContain("DTD", "parser diagnostics must remain in the protected audit channel");
        body.Should().NotContain("/etc/passwd");
    }

    /// <summary>
    /// A well-formed import document cannot be directed into a package other than the one named by its type
    /// attribute.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task ImportModule_WithForeignType_ReturnsBadRequest()
    {
        using HttpClient client = await _fixture.CreateHostClientAsync();
        (int moduleId, _) = await InsertPortableModuleAsync();

        using HttpResponseMessage response = await client.PostAsJsonAsync(
            ModuleImportRoute(_fixture.Seed.PortalId),
            new ModuleImportRequest
            {
                ModuleId = moduleId,
                Content = "<content type=\"ForeignPackage\">content</content>",
            },
            ApiTestFixture.Json);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        string body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("document type does not match the target module package");
    }

    /// <summary>
    /// The three module endpoints that name no module — the collection listing, creation and content import
    /// — are administrative, so an ordinary member of the tenant holding a perfectly valid token is refused
    /// all three.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task ModuleEndpointsThatNameNoModule_AreRefusedToAnOrdinaryMember()
    {
        using HttpClient member = await _fixture.CreateUnprivilegedClientAsync();

        int portalId = _fixture.Seed.PortalId;

        using HttpResponseMessage listed = await member.GetAsync(new Uri(
            "/api/v1/modules?pageIndex=0&pageSize=10",
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
            new Uri("/api/v1/modules/import", UriKind.Relative),
            new ModuleImportRequest
            {
                ModuleId = 1,
                Content = "<content />",
            },
            ApiTestFixture.Json);

        imported.StatusCode.Should().Be(HttpStatusCode.Forbidden);

        // The refused creation left nothing behind, which is what distinguishes a refusal from a report of one.
        // The collection is read as host, because the member may not read it at all.
        using HttpClient host = await _fixture.CreateHostClientAsync();
        IReadOnlyList<ModuleListItemDto> modules = await ListModulesAsync(host, includeDeleted: true);
        modules.Should().NotContain(
            module => module.ModuleTitle == attemptedTitle,
            "a refused creation must not have written a row");
    }

    /// <summary>
    /// A page grant to the all-users pseudo-role, or to the unauthenticated pseudo-role, does not open a
    /// module's administrative record to a caller with no account.
    /// </summary>
    /// <param name="pseudoRoleId">The negative role identifier the grant is recorded against.</param>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// WHAT THE OLD ASSERTION PROTECTED IS STILL PROTECTED, ELSEWHERE. Its real subject was that the two
    /// VIEW policies do not demand an authenticated caller, so a grant recorded against a pseudo-role can
    /// actually be evaluated rather than being a row that only looks like a grant.
    /// </remarks>
    [Theory]
    [InlineData(-1)]
    [InlineData(-3)]
    public async Task GetModule_WhosePageGrantsViewToAPseudoRole_IsStillRefusedAnonymously(int pseudoRoleId)
    {
        using HttpClient host = await _fixture.CreateHostClientAsync();
        ModuleDetailDto created = await CreateModuleAsync(host, _fixture.Seed.ChildTabId);

        using HttpClient anonymous = _fixture.CreateAnonymousClient();

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
            int grants = await _fixture.Database.ScalarAsync<int>(
                """
                SELECT COUNT(*) FROM [dbo].[TabPermission]
                WHERE [TabID] = @tabId AND [PermissionID] = @permissionId
                  AND [RoleID] = @roleId AND [AllowAccess] = 1;
                """,
                new Dictionary<string, object?>
                {
                    ["tabId"] = _fixture.Seed.ChildTabId,
                    ["permissionId"] = _fixture.Seed.TabViewPermissionId,
                    ["roleId"] = pseudoRoleId,
                });

            grants.Should().Be(1, "the refusal below must be the gate's answer, not a grant that never landed");

            using HttpResponseMessage response = await anonymous.GetAsync(
                ModuleRoute(_fixture.Seed.PortalId, created.ModuleId));

            response.StatusCode.Should().Be(
                HttpStatusCode.Unauthorized,
                "a page view grant confers the module's content, never its administrative record");
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
    /// An anonymous caller is still refused a module EDIT, because no legacy grant reaches the
    /// unauthenticated pseudo-role for a mutation and an anonymous change has no account to attribute
    /// itself to.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// The companion to the test above, and the reason the two view policies and the two edit policies are
    /// composed differently: relaxing authentication is correct for reading a public page and wrong for
    /// writing to one. Asserting only the relaxation would not distinguish "anonymous reads are permitted"
    /// from "authentication was removed everywhere".
    /// </remarks>
    [Fact]
    public async Task UpdateModule_IsRefusedToAnAnonymousCallerEvenWhenThePageIsPublic()
    {
        using HttpClient host = await _fixture.CreateHostClientAsync();
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

    /// <summary>
    /// An empty title round-trips as the empty string and stays distinguishable from an absent one.
    /// </summary>
    /// <remarks>
    /// The failure this guards against is a serialiser policy, not a mapping: <c>WhenWritingNull</c> or
    /// <c>WhenWritingDefault</c> on the ignore condition would erase one of the two states from the wire
    /// and no status-code assertion anywhere would notice.
    /// </remarks>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task CreateModule_WithEmptyTitle_KeepsItDistinguishableFromAnAbsentOne()
    {
        using HttpClient client = await _fixture.CreateHostClientAsync();

        CreateModuleRequest empty = NewModuleRequest(_fixture.Seed.RootTabId);
        empty.ModuleTitle = string.Empty;

        using HttpResponseMessage emptyResponse = await client.PostAsJsonAsync(
            ModulesRoute(_fixture.Seed.PortalId),
            empty,
            ApiTestFixture.Json);

        emptyResponse.StatusCode.Should().Be(
            HttpStatusCode.Created,
            "the legacy screen carried no required-field validator for the title");

        ModuleDetailDto withEmptyTitle = await ReadDetailAsync(emptyResponse);
        withEmptyTitle.ModuleTitle.Should().Be(
            string.Empty,
            "the empty string is the legacy representation of an unset value and must not be widened to null");

        CreateModuleRequest absent = NewModuleRequest(_fixture.Seed.RootTabId);
        absent.ModuleTitle = null;

        using HttpResponseMessage absentResponse = await client.PostAsJsonAsync(
            ModulesRoute(_fixture.Seed.PortalId),
            absent,
            ApiTestFixture.Json);

        absentResponse.StatusCode.Should().Be(HttpStatusCode.Created);

        ModuleDetailDto withNoTitle = await ReadDetailAsync(absentResponse);
        withNoTitle.ModuleTitle.Should().BeNull();

        // Read through the raw document, because a typed read collapses "absent" and "present and null"
        // onto the same value and the whole point here is that the two are different states.
        JsonElement emptyTitle = await ReadDataMemberAsync(
            client,
            ModuleRoute(_fixture.Seed.PortalId, withEmptyTitle.ModuleId),
            "moduleTitle");

        emptyTitle.ValueKind.Should().Be(
            JsonValueKind.String,
            "an ignore condition that dropped the empty string would leave this member absent");
        emptyTitle.GetString().Should().Be(string.Empty);

        JsonElement absentTitle = await ReadDataMemberAsync(
            client,
            ModuleRoute(_fixture.Seed.PortalId, withNoTitle.ModuleId),
            "moduleTitle");

        absentTitle.ValueKind.Should().Be(
            JsonValueKind.Null,
            "an absent title is published as an explicit null rather than omitted, so a consumer can tell "
            + "it apart from the empty string");

        // And the two really are different values in the column, which is what makes the wire distinction
        // worth preserving rather than an artefact of the serialiser.
        int emptyRows = await _fixture.Database.ScalarAsync<int>(
            "SELECT COUNT(*) FROM [dbo].[Modules] WHERE [ModuleID] = @moduleId AND [ModuleTitle] = N'';",
            new Dictionary<string, object?> { ["moduleId"] = withEmptyTitle.ModuleId });

        emptyRows.Should().Be(1, "the empty string reached the column as the empty string");

        int nullRows = await _fixture.Database.ScalarAsync<int>(
            "SELECT COUNT(*) FROM [dbo].[Modules] WHERE [ModuleID] = @moduleId AND [ModuleTitle] IS NULL;",
            new Dictionary<string, object?> { ["moduleId"] = withNoTitle.ModuleId });

        nullRows.Should().Be(1, "an absent title reached the column as SQL NULL");
    }

    /// <summary>
    /// The negative placement-order sentinel survives to the wire as <c>-1</c> rather than as an absence.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task CreateModule_WithTheAppendOrderSentinel_PublishesItAsMinusOne()
    {
        using HttpClient client = await _fixture.CreateHostClientAsync();

        CreateModuleRequest request = NewModuleRequest(_fixture.Seed.RootTabId);
        request.ModuleOrder = -1;

        using HttpResponseMessage response = await client.PostAsJsonAsync(
            ModulesRoute(_fixture.Seed.PortalId),
            request,
            ApiTestFixture.Json);

        response.StatusCode.Should().Be(HttpStatusCode.Created);

        ModuleDetailDto created = await ReadDetailAsync(response);

        JsonElement order = await ReadDataMemberAsync(
            client,
            ModuleRoute(_fixture.Seed.PortalId, created.ModuleId),
            "moduleOrder");

        order.ValueKind.Should().Be(
            JsonValueKind.Number,
            "the placement order is always published; an ignore condition that dropped defaults would "
            + "remove exactly the sentinel this asserts");
        order.GetInt32().Should().NotBe(
            0,
            "zero would mean the sentinel had been coerced away rather than honoured or resolved");
    }

    /// <summary>Module zero is a legitimate identifier, in the route segment and in a request body alike.</summary>
    /// <remarks>
    /// The row is removed afterwards so the shared collection is left as it was found. It is placed on no
    /// page, so the read that matters is the detail read.
    /// </remarks>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task GetModule_WhoseIdentifierIsZero_ResolvesRatherThanBeingTreatedAsAbsent()
    {
        await InsertModuleWithIdentifierZeroAsync();

        try
        {
            using HttpClient client = await _fixture.CreateHostClientAsync();

            using HttpResponseMessage response = await client.GetAsync(
                ModuleRoute(_fixture.Seed.PortalId, 0));

            response.StatusCode.Should().Be(
                HttpStatusCode.OK,
                "Modules.ModuleID is IDENTITY(0, 1), so zero identifies a real row and a policy that "
                + "resolves its scope from the route must accept it rather than fail closed");

            ModuleDetailDto detail = await ReadDetailAsync(response);
            detail.ModuleId.Should().Be(0);

            var update = new UpdateModuleRequest
            {
                TabId = _fixture.Seed.RootTabId,
                ModuleTitle = "Module zero renamed",
            };

            using HttpResponseMessage updated = await client.PutAsJsonAsync(
                ModuleRoute(_fixture.Seed.PortalId, 0),
                update,
                ApiTestFixture.Json);

            updated.StatusCode.Should().NotBe(
                HttpStatusCode.Forbidden,
                "a zero parsed out of the route is a valid scope identifier, so the edit policy must "
                + "evaluate a grant rather than fail closed");

            var import = new ModuleImportRequest
            {
                ModuleId = 0,
                Content = "<module />",
                FileName = "module-zero.xml",
            };

            using HttpResponseMessage imported = await client.PostAsJsonAsync(
                ModuleImportRoute(_fixture.Seed.PortalId),
                import,
                ApiTestFixture.Json);

            imported.StatusCode.Should().NotBe(
                HttpStatusCode.NotFound,
                "zero in the body names the module that exists, so the request must not be answered as "
                + "though no such module were addressed");
        }
        finally
        {
            await RemoveModuleWithIdentifierZeroAsync();
        }
    }

    /// <summary>
    /// A refused write publishes one RFC 7807 document, as JSON, naming the field that caused the refusal.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task CreateModule_WithAnUnknownVisibility_PublishesAFieldKeyedProblemDocument()
    {
        using HttpClient client = await _fixture.CreateHostClientAsync();

        // Posted as a raw document rather than through the typed contract, because the value has to be one
        // the enumeration does not define and the typed property cannot express that.
        string payload = FormattableString.Invariant($$"""
            {
              "moduleDefId": {{_fixture.Seed.ModuleDefinitionId}},
              "tabId": {{_fixture.Seed.RootTabId}},
              "moduleTitle": "Invalid visibility {{Suffix()}}",
              "visibility": 97
            }
            """);

        using var content = new StringContent(payload, Encoding.UTF8, "application/json");
        using HttpResponseMessage response = await client.PostAsync(
            ModulesRoute(_fixture.Seed.PortalId),
            content);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        response.Content.Headers.ContentType.Should().NotBeNull(
            "a refusal publishes a document, so it must state what that document is");
        response.Content.Headers.ContentType!.MediaType.Should().BeOneOf(
            ["application/json", "application/problem+json"],
            "the refusal is a JSON problem document; the measured value is the former, and the latter is "
            + "admitted so that correcting the deviation does not require editing this fact");

        using JsonDocument document = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync());

        JsonElement problem = document.RootElement;

        problem.TryGetProperty("status", out JsonElement status).Should().BeTrue();
        status.GetInt32().Should().Be(400);
        problem.TryGetProperty("title", out JsonElement title).Should().BeTrue();
        title.GetString().Should().NotBeNullOrWhiteSpace();
        problem.TryGetProperty("type", out JsonElement type).Should().BeTrue();
        type.GetString().Should().NotBeNullOrWhiteSpace();
        problem.TryGetProperty("detail", out _).Should().BeTrue();

        problem.TryGetProperty("traceId", out JsonElement traceId).Should().BeTrue(
            "the factory carries the trace identifier so a refusal can be tied back to its request");
        traceId.GetString().Should().NotBeNullOrWhiteSpace();

        problem.TryGetProperty("errors", out JsonElement errors).Should().BeTrue(
            "a validation failure publishes the per-field map, not prose alone");
        errors.ValueKind.Should().Be(JsonValueKind.Object);

        IReadOnlyList<string> keys = errors.EnumerateObject().Select(field => field.Name).ToList();

        keys.Should().NotBeEmpty();
        keys.Should().Contain(
            key => key.Contains("Visibility", StringComparison.OrdinalIgnoreCase),
            "the refusal must name the member that caused it; a map keyed on anything else would leave the "
            + "caller unable to correct the request");
    }

    /// <summary>
    /// The two content-movement operations publish the field-keyed document they advertise, keyed on the
    /// member that was actually missing.
    /// </summary>
    /// <param name="operation">Which operation to exercise: the export or the import.</param>
    /// <param name="expectedKey">The member the error map must be keyed on.</param>
    /// <returns>A task representing the test.</returns>
    [Theory]
    [InlineData("export", "FileName")]
    [InlineData("import", "ModuleId")]
    public async Task ModuleContentOperations_PublishAFieldKeyedProblemDocument(
        string operation,
        string expectedKey)
    {
        using HttpClient client = await _fixture.CreateHostClientAsync();
        ModuleDetailDto created = await CreateModuleAsync(client, _fixture.Seed.RootTabId);

        (Uri route, string payload) = operation switch
        {
            "export" => (ModuleExportRoute(_fixture.Seed.PortalId, created.ModuleId), "{}"),
            _ => (ModuleImportRoute(_fixture.Seed.PortalId), "{ \"content\": \"<content />\" }"),
        };

        using var body = new StringContent(payload, Encoding.UTF8, "application/json");
        using HttpResponseMessage response = await client.PostAsync(route, body);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        document.RootElement.TryGetProperty("errors", out JsonElement errors).Should().BeTrue(
            "the operation advertises ValidationProblemDetails for 400, so a field-level refusal must "
            + "publish the per-field map that shape is defined by");
        errors.ValueKind.Should().Be(JsonValueKind.Object);

        errors.EnumerateObject().Select(field => field.Name).Should().Contain(
            key => key.Contains(expectedKey, StringComparison.OrdinalIgnoreCase),
            $"the map must name {expectedKey}, which is the member the request omitted");

        document.RootElement.TryGetProperty("traceId", out JsonElement traceId).Should().BeTrue(
            "a validation refusal carries the same trace identifier as every other failure in this API");
        traceId.GetString().Should().NotBeNullOrWhiteSpace();
    }

    /// <summary>
    /// An export whose file name is only whitespace is refused at the BOUNDARY, with the member named.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task ExportModule_WithAWhitespaceFileName_IsRefusedAtTheBoundary()
    {
        using HttpClient client = await _fixture.CreateHostClientAsync();
        ModuleDetailDto created = await CreateModuleAsync(client, _fixture.Seed.RootTabId);

        using HttpResponseMessage response = await client.PostAsJsonAsync(
            ModuleExportRoute(_fixture.Seed.PortalId, created.ModuleId),
            new ModuleExportRequest { FileName = "   " },
            ApiTestFixture.Json);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        document.RootElement.TryGetProperty("errors", out JsonElement errors).Should().BeTrue(
            "whitespace and blank are one state for this member, so the boundary - not the service - answers");

        errors.EnumerateObject().Select(field => field.Name).Should().Contain(
            key => key.Contains(nameof(ModuleExportRequest.FileName), StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// A refusal decided by the module service is identified by its own failure code, never by a number.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task ExportModule_WhenNotPortable_IdentifiesTheRefusalByItsNamedCode()
    {
        using HttpClient client = await _fixture.CreateHostClientAsync();
        ModuleDetailDto created = await CreateModuleAsync(client, _fixture.Seed.RootTabId);

        using HttpResponseMessage response = await client.PostAsJsonAsync(
            ModuleExportRoute(_fixture.Seed.PortalId, created.ModuleId),
            new ModuleExportRequest { FileName = "not-portable.xml" },
            ApiTestFixture.Json);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        using JsonDocument document = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync());

        document.RootElement.TryGetProperty("type", out JsonElement type).Should().BeTrue();

        string? identifier = type.GetString();

        identifier.Should().NotBeNullOrWhiteSpace();
        identifier.Should().EndWith(
            "module.not_portable",
            "the refusal is named, so a client can branch on the reason rather than parsing the detail");

        // The document carries no numeric status vocabulary of its own beyond the HTTP status, which is the
        // point of naming the reason: `status` restates the transport, `type` states the cause.
        document.RootElement.TryGetProperty("status", out JsonElement status).Should().BeTrue();
        status.GetInt32().Should().Be(400);
    }

    /// <summary>
    /// An export answers with the document in the response body and writes nothing to a file system.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task ExportModule_DeclaresTheDocumentAsTheResponseBodyAndNoFileDestination()
    {
        MethodInfo export = typeof(ModulesController)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Single(method => method.Name == "ExportAsync");

        ProducesResponseTypeAttribute success = export
            .GetCustomAttributes<ProducesResponseTypeAttribute>()
            .Single(attribute => attribute.StatusCode == StatusCodes.Status200OK);

        // The attribute publishes its media types through the metadata-provider contract rather than as a
        // property, so they are collected the same way the API explorer collects them.
        var advertised = new MediaTypeCollection();
        ((IApiResponseMetadataProvider)success).SetContentTypes(advertised);

        advertised.Should().Contain(
            "application/xml",
            "the exported document is the response body, so the success status advertises XML rather than "
            + "the controller-wide JSON");
        success.Type.Should().Be(
            typeof(string),
            "the body is the document itself and not an envelope naming a file the caller must then fetch");

        foreach (string dropped in new[] { "DiskSpace", "AvailableSpace", "Path", "Url", "Uri" })
        {
            typeof(ModuleExportRequest).GetProperty(dropped).Should().BeNull(
                $"the export writes no file, so {dropped} has nothing to describe");
        }

        // The two members the contract does carry name the download rather than a destination on a server,
        // which is what makes the absence above a design rather than an omission.
        typeof(ModuleExportRequest).GetProperty("FileName").Should().NotBeNull();
        typeof(ModuleExportRequest).GetProperty("Folder").Should().NotBeNull();

        // And the import contract carries the module in the BODY, which is the direct consequence of the
        // import route naming no module: a route-reading permission policy would have nothing to read.
        typeof(ModuleImportRequest).GetProperty("ModuleId").Should().NotBeNull(
            "the import route names no module, so the request must");

        using HttpClient client = await _fixture.CreateHostClientAsync();
        ModuleDetailDto created = await CreateModuleAsync(client, _fixture.Seed.RootTabId);

        using HttpResponseMessage response = await client.PostAsJsonAsync(
            ModuleExportRoute(_fixture.Seed.PortalId, created.ModuleId),
            new ModuleExportRequest { FileName = "declared-contract.xml" },
            ApiTestFixture.Json);

        response.StatusCode.Should().NotBe(
            HttpStatusCode.NotFound,
            "the export route exists and answers about the module, so the declaration above describes a "
            + "reachable action");

        string body = await response.Content.ReadAsStringAsync();

        foreach (string leak in new[] { "Portals\\", "/Portals/", "DiskSpace", "diskSpace" })
        {
            body.Should().NotContain(
                leak,
                $"the export writes no file, so no response may mention {leak}");
        }
    }

    /// <summary>
    /// Both content-transfer actions declare a request ceiling, and the two ceilings are the ones each action
    /// actually needs rather than one number applied to both.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The explicit metadata is the contract under test. Without it, a future global-limit change could
    /// silently make module import/export unbounded or unexpectedly narrower while their public surface still
    /// appeared unchanged.
    /// </para>
    /// <para>
    /// THE TWO ACTIONS ARE NOT INTERCHANGEABLE, SO ONE NUMBER CANNOT DESCRIBE BOTH. An export
    /// request carries a file name and nothing else, so the global mebibyte is three orders of magnitude more
    /// than it needs. An import request carries a whole document, and the service accepts one of
    /// <see cref="ModuleImportRequest.ContentCharacterMaximum"/> characters - which cannot fit in a mebibyte
    /// of BODY once the member names, the quotes and the JSON escaping are counted. Pinning both to the
    /// global limit therefore describes a contract that cannot be satisfied: the accepted document is
    /// undeliverable. The import limit is derived from that character ceiling, and the relationship is
    /// asserted below rather than the literal, so the arithmetic cannot drift out of step with the ceiling it
    /// is computed from.
    /// </para>
    /// </remarks>
    [Fact]
    public void ModuleContentTransfer_DeclaresItsRequestBodyCeiling()
    {
        static long DeclaredLimitOf(string actionName)
        {
            MethodInfo action = typeof(ModulesController)
                .GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .Single(method => method.Name == actionName);

            RequestSizeLimitAttribute limit = action.GetCustomAttribute<RequestSizeLimitAttribute>()
                ?? throw new InvalidOperationException($"{actionName} declares no request-size limit.");

            return ((Microsoft.AspNetCore.Http.Metadata.IRequestSizeLimitMetadata)limit).MaxRequestBodySize
                ?? throw new InvalidOperationException($"{actionName} declares an unbounded request size.");
        }

        DeclaredLimitOf("ExportAsync").Should().Be(
            ServiceCollectionExtensions.MaximumRequestBodyBytes,
            "an export request carries a file name, so the global ceiling is already generous for it");

        DeclaredLimitOf("ImportAsync").Should().Be(
            ServiceCollectionExtensions.MaximumImportRequestBodyBytes,
            "an import request carries a document, and its ceiling is the one computed from the document "
            + "ceiling the contract publishes");

        const long worstCaseJsonBytesPerCharacter = 6L;

        ServiceCollectionExtensions.MaximumImportRequestBodyBytes.Should().BeGreaterThan(
            ModuleImportRequest.ContentCharacterMaximum * worstCaseJsonBytesPerCharacter,
            "the default JSON encoder escapes every character an XML document is largely made of - the "
            + "angle brackets, the ampersand, the quote and everything non-ASCII - to a six-byte form, so a "
            + "document at the accepted ceiling must still fit inside the body limit");

        ServiceCollectionExtensions.MaximumImportRequestBodyBytes.Should().BeGreaterThan(
            ServiceCollectionExtensions.MaximumRequestBodyBytes,
            "the import action raises the limit for itself precisely because the global one is too small "
            + "for it, and the global one stays small for everything else");

        ModuleImportRequest.FileByteMaximum.Should().Be(
            ModuleImportRequest.ContentCharacterMaximum,
            "a UTF-8 sequence of N bytes yields at most N UTF-16 code units, which is what makes a byte "
            + "limit a sound proxy for the character ceiling");
    }

    /// <summary>
    /// A listing publishes the items-plus-total envelope, and the total is never the legacy sentinel.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task ListModules_PublishesTheItemsAndTotalEnvelopeWithoutASentinelTotal()
    {
        using HttpClient client = await _fixture.CreateHostClientAsync();

        // Two are created rather than one, and neither the seed nor a sibling fact is relied on for the
        // second.
        ModuleDetailDto first = await CreateModuleAsync(client, _fixture.Seed.RootTabId);
        ModuleDetailDto second = await CreateModuleAsync(client, _fixture.Seed.RootTabId);

        second.ModuleId.Should().NotBe(first.ModuleId);

        using HttpResponseMessage response = await client.GetAsync(
            ModuleListingRoute(_fixture.Seed.PortalId, pageSize: 1));

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        PagedEnvelope<ModuleListItemDto>? page = await response.Content
            .ReadFromJsonAsync<PagedEnvelope<ModuleListItemDto>>(ApiTestFixture.Json);

        page.Should().NotBeNull();
        page!.Items.Should().ContainSingle("the window is one row wide");
        page.TotalCount.Should().BeGreaterThan(
            page.Items.Count,
            "the total describes the whole collection rather than the window, and two modules were created "
            + "before it was read");
        page.TotalCount.Should().NotBe(
            -1,
            "minus one is the legacy integer sentinel and must never be published as a count");
        page.PageSize.Should().Be(1);

        // The envelope's own members are asserted through the raw document as well, because a member the
        // response omits would deserialise to zero and pass a typed assertion silently.
        using HttpResponseMessage raw = await client.GetAsync(
            ModuleListingRoute(_fixture.Seed.PortalId, pageSize: 1));

        using JsonDocument document = JsonDocument.Parse(await raw.Content.ReadAsStringAsync());

        document.RootElement.TryGetProperty("items", out JsonElement items).Should().BeTrue();
        items.ValueKind.Should().Be(JsonValueKind.Array);
        document.RootElement.TryGetProperty("meta", out JsonElement meta).Should().BeTrue();
        meta.TryGetProperty("totalCount", out JsonElement total).Should().BeTrue();
        total.GetInt32().Should().BeGreaterThan(1);

        first.ModuleId.Should().BeGreaterThanOrEqualTo(
            0,
            "Modules.ModuleID is IDENTITY(0, 1), so a created identifier is never negative");
    }

    /// <summary>
    /// A module sitting on two pages does not widen the window it appears in, and does not shrink the
    /// total.
    /// </summary>
    /// <remarks>
    /// The second placement is written directly with SQL rather than by asking for every page. The
    /// all-pages switch fans a module out across every content page of the tenant, which would add rows to
    /// pages that other facts in this shared collection read - so the narrowest possible change is made, on
    /// one extra page, and it is removed again in a <c>finally</c>.
    /// </remarks>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task ListModules_WithAModuleOnTwoPages_PublishesExactRowMetadata()
    {
        using HttpClient client = await _fixture.CreateHostClientAsync();
        ModuleDetailDto created = await CreateModuleAsync(client, _fixture.Seed.RootTabId);

        await _fixture.Database.ExecuteAsync(
            """
            INSERT INTO [dbo].[TabModules]
                ([TabID], [ModuleID], [PaneName], [ModuleOrder], [CacheTime], [Visibility],
                 [DisplayTitle], [DisplayPrint], [DisplaySyndicate])
            VALUES (@tabId, @moduleId, N'ContentPane', 1, 0, 0, 1, 0, 0);
            """,
            new Dictionary<string, object?>
            {
                ["tabId"] = _fixture.Seed.ChildTabId,
                ["moduleId"] = created.ModuleId,
            });

        try
        {
            using HttpResponseMessage wide = await client.GetAsync(
                ModuleListingRoute(_fixture.Seed.PortalId, pageSize: MaximumPageSize));

            wide.StatusCode.Should().Be(HttpStatusCode.OK);

            PagedEnvelope<ModuleListItemDto>? whole = await wide.Content
                .ReadFromJsonAsync<PagedEnvelope<ModuleListItemDto>>(ApiTestFixture.Json);

            whole.Should().NotBeNull();
            whole!.Items.Count.Should().BeLessThan(
                MaximumPageSize,
                "the widest window the contract admits must still hold the whole collection for the total "
                + "and the row count to be comparable");
            whole.TotalCount.Should().Be(
                whole.Items.Count,
                "a window wider than the collection holds every row, so the total is the row count");

            whole.Items.Count(row => row.ModuleId == created.ModuleId).Should().Be(
                2,
                "the module now sits on two pages, so it contributes two placement rows");

            using HttpResponseMessage narrow = await client.GetAsync(
                ModuleListingRoute(_fixture.Seed.PortalId, pageSize: 1));

            narrow.StatusCode.Should().Be(HttpStatusCode.OK);

            PagedEnvelope<ModuleListItemDto>? window = await narrow.Content
                .ReadFromJsonAsync<PagedEnvelope<ModuleListItemDto>>(ApiTestFixture.Json);

            window.Should().NotBeNull();
            window!.Items.Should().ContainSingle(
                "a window one row wide returns one row, whatever the module behind it is placed on");
            window.PageSize.Should().Be(
                1,
                "the width published is the width the caller asked for, never one widened to fit an expansion");
            window.TotalCount.Should().Be(
                whole.TotalCount,
                "the total describes the collection, so narrowing the window cannot change it");
            window.Meta.TotalPages.Should().Be(
                whole.TotalCount,
                "every row is its own page at a width of one, which only holds while both figures count rows");
        }
        finally
        {
            await _fixture.Database.ExecuteAsync(
                "DELETE FROM [dbo].[TabModules] WHERE [ModuleID] = @moduleId AND [TabID] = @tabId;",
                new Dictionary<string, object?>
                {
                    ["tabId"] = _fixture.Seed.ChildTabId,
                    ["moduleId"] = created.ModuleId,
                });
        }
    }

    /// <summary>The listing's title filter matches case-insensitively anywhere in the title.</summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task ListModules_FilteredByTitle_MatchesAnywhereInTheTitleIgnoringCase()
    {
        using HttpClient client = await _fixture.CreateHostClientAsync();

        string marker = Suffix();
        CreateModuleRequest request = NewModuleRequest(_fixture.Seed.RootTabId);
        request.ModuleTitle = "Searchable " + marker + " Module";

        using HttpResponseMessage creation = await client.PostAsJsonAsync(
            ModulesRoute(_fixture.Seed.PortalId),
            request,
            ApiTestFixture.Json);

        creation.StatusCode.Should().Be(HttpStatusCode.Created);
        ModuleDetailDto created = await ReadDetailAsync(creation);

        IReadOnlyList<ModuleListItemDto> byMiddle = await SearchModulesAsync(client, marker);

        byMiddle.Should().ContainSingle(row => row.ModuleId == created.ModuleId,
            "the filter matches a fragment anywhere in the title");

        IReadOnlyList<ModuleListItemDto> byDifferentCase = await SearchModulesAsync(
            client,
            marker.ToUpperInvariant());

        byDifferentCase.Should().ContainSingle(row => row.ModuleId == created.ModuleId,
            "the comparison is case-insensitive, as the lower-cased legacy comparisons were");

        IReadOnlyList<ModuleListItemDto> byAbsentText = await SearchModulesAsync(
            client,
            "no-module-carries-this-" + Suffix());

        byAbsentText.Should().NotContain(row => row.ModuleId == created.ModuleId,
            "a fragment no title holds matches nothing, so the filter is applied rather than ignored");
    }

    /// <summary>
    /// A correlation identifier the caller supplies is echoed once, on success and on a refusal alike.
    /// </summary>
    /// <remarks>
    /// Asserted on a REFUSED request as well as on a satisfied one, because the refusal is the case that
    /// matters operationally: a caller reporting a failure has nothing but the identifier to hand back, and
    /// a short-circuiting stage that answered without the header would take it away at exactly the moment
    /// it is needed.
    /// </remarks>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task ModuleRequests_EchoTheSuppliedCorrelationIdOnSuccessAndOnRefusal()
    {
        using HttpClient client = await _fixture.CreateHostClientAsync();

        string supplied = ApiTestFixture.NewCorrelationId();

        using var read = new HttpRequestMessage(
            HttpMethod.Get,
            ModuleListingRoute(_fixture.Seed.PortalId, pageSize: 5));

        using CorrelatedResponse satisfied = await AuthenticatedClientFactory
            .SendWithCorrelationIdAsync(client, read, supplied);

        satisfied.Response.StatusCode.Should().Be(HttpStatusCode.OK);
        satisfied.RoundTripped.Should().BeTrue("a supplied identifier is echoed rather than replaced");

        satisfied.Response.Headers
            .GetValues(ApiTestFixture.CorrelationIdHeader)
            .Should()
            .ContainSingle("the header is overwritten rather than appended to, so exactly one value travels");

        string refusedId = ApiTestFixture.NewCorrelationId();

        using var refused = new HttpRequestMessage(
            HttpMethod.Get,
            ModuleRoute(_fixture.Seed.PortalId, UnknownModuleId));

        using CorrelatedResponse failure = await AuthenticatedClientFactory
            .SendWithCorrelationIdAsync(client, refused, refusedId);

        failure.Response.StatusCode.Should().NotBe(
            HttpStatusCode.OK,
            "the identifier under examination is the one attached to a request that did not succeed");
        failure.ReceivedCorrelationId.Should().Be(
            refusedId,
            "a problem document carries the correlation identifier, because that is when a caller needs it");
    }

    /// <summary>A request that supplies no correlation identifier is answered with one that was generated.</summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task ModuleRequests_WithoutACorrelationId_AreAnsweredWithAGeneratedOne()
    {
        using HttpClient client = await _fixture.CreateHostClientAsync();

        using HttpResponseMessage response = await client.GetAsync(
            ModuleListingRoute(_fixture.Seed.PortalId, pageSize: 5));

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        ApiTestFixture.ReadCorrelationId(response).Should().NotBeNullOrWhiteSpace(
            "every response carries an identifier, whether or not the caller offered one");
    }

    /// <summary>
    /// An unusable correlation identifier is replaced rather than echoed, and never refuses the request.
    /// </summary>
    /// <remarks>
    /// The two unusable shapes are covered together because they fail for one reason: an identifier is
    /// reflected into a response header, so a value long enough to be abusive or one carrying a line break
    /// must not be reflected at all.
    /// </remarks>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task ModuleRequests_WithAnUnusableCorrelationId_AreAnsweredWithAReplacementNotARefusal()
    {
        using HttpClient client = await _fixture.CreateHostClientAsync();

        string overlong = new('c', 200);

        using var longRequest = new HttpRequestMessage(
            HttpMethod.Get,
            ModuleListingRoute(_fixture.Seed.PortalId, pageSize: 5));

        longRequest.Headers.TryAddWithoutValidation(ApiTestFixture.CorrelationIdHeader, overlong);

        using HttpResponseMessage longResponse = await client.SendAsync(longRequest);

        longResponse.StatusCode.Should().Be(
            HttpStatusCode.OK,
            "an unusable diagnostic header is not a reason to withhold the resource");

        string? replacement = ApiTestFixture.ReadCorrelationId(longResponse);

        replacement.Should().NotBeNullOrWhiteSpace();
        replacement.Should().NotBe(overlong, "a value too long to reflect is replaced, not echoed");
        replacement!.Length.Should().BeLessThanOrEqualTo(
            128,
            "the published identifier is bounded, which is the whole reason the inbound one was rejected");

        using var controlRequest = new HttpRequestMessage(
            HttpMethod.Get,
            ModuleListingRoute(_fixture.Seed.PortalId, pageSize: 5));

        // Added without validation because the client would otherwise refuse to send it, and the value under
        // examination is precisely one a well behaved client would never produce.
        controlRequest.Headers.TryAddWithoutValidation(
            ApiTestFixture.CorrelationIdHeader,
            "injected\rSet-Cookie: forged=1");

        using HttpResponseMessage controlResponse = await client.SendAsync(controlRequest);

        controlResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        string? sanitised = ApiTestFixture.ReadCorrelationId(controlResponse);

        sanitised.Should().NotBeNullOrWhiteSpace();
        sanitised.Should().NotContain("forged", "a control character bearing value is never reflected");
        controlResponse.Headers.Contains("Set-Cookie").Should().BeFalse(
            "nothing a caller puts in that header may become a header of its own");
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
            "/api/v1/modules"
                + $"?pageIndex=0&pageSize=100&includeDeleted={(includeDeleted ? "true" : "false")}",
            UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        PagedEnvelope<ModuleListItemDto>? page = await response.Content
            .ReadFromJsonAsync<PagedEnvelope<ModuleListItemDto>>(ApiTestFixture.Json);

        page.Should().NotBeNull();
        return page!.Items;
    }

    /// <summary>
    /// Records or replaces one module permission grant. The grant is written directly because the API
    /// exposes no grant-management endpoint - permission catalogues are read-only over HTTP in this
    /// migration - and the point of the test is the evaluation of stored grants, not the means of storing
    /// them.
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
    /// Removes every page EDIT grant in the seeded tenant, so a case can state its own starting point.
    /// </summary>
    /// <returns>A task representing the write.</returns>
    /// <remarks>
    /// ⚠ REQUIRED BECAUSE THE DEFINITION CATALOGUE'S POLICY IS A TENANT-WIDE CAPABILITY QUESTION. Admission
    /// depends on whether ANY page of the tenant grants the caller EDIT, so a case asserting a refusal is
    /// asserting something about stored rows rather than about the caller's role. The suites share one
    /// database and xUnit gives no ordering guarantee within a collection.
    /// </remarks>
    private async Task ClearTenantEditGrantsAsync()
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

    /// <summary>
    /// Records or replaces one page permission grant, written directly for the same reason the module grant
    /// above is: the API exposes no grant-management endpoint, and the point of the test is the evaluation
    /// of stored grants rather than the means of storing them.
    /// </summary>
    /// <param name="tabId">The page the grant is recorded against.</param>
    /// <param name="permissionId">The catalogue entry being granted or denied.</param>
    /// <param name="roleId">The role the grant applies to.</param>
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

    /// <summary>Signs in as the seeded plain member, which holds no permission grant of any kind.</summary>
    /// <returns>An authenticated client with no module or page grants.</returns>
    private Task<HttpClient> MemberClientAsync() => _fixture.CreateUnprivilegedClientAsync();

    /// <summary>
    /// The facts about a tenant this suite provisioned that an authorisation assertion needs: how to
    /// address it, which page its provisioning created, and which account administers it.
    /// </summary>
    /// <param name="PortalId">The created tenant.</param>
    /// <param name="Alias">The host name that resolves it.</param>
    /// <param name="HomeTabId">The home page its provisioning created.</param>
    /// <param name="AdministratorUserName">The account its provisioning created as its administrator.</param>
    private readonly record struct TenantFacts(
        int PortalId,
        string Alias,
        int HomeTabId,
        string AdministratorUserName);

    /// <summary>
    /// Provisions a tenant through the API and reports what an authorisation assertion needs to act as it.
    /// </summary>
    /// <param name="host">The installation host account, the only caller entitled to create a tenant.</param>
    /// <returns>The created tenant's identity, alias, home page and administrator account name.</returns>
    /// <remarks>
    /// Distinct from <see cref="CreateForeignPortalAsync"/>, which exists to be a tenant a request must NOT
    /// reach and therefore never reports an account to act as.
    /// </remarks>
    private static async Task<TenantFacts> CreateTenantWithAdministratorAsync(HttpClient host)
    {
        string suffix = Suffix();
        string alias = "module-admin-" + suffix + ".local";
        string administrator = "module_owner_" + suffix;

        using HttpResponseMessage response = await host.PostAsJsonAsync(
            new Uri("/api/v1/portals", UriKind.Relative),
            new
            {
                portalName = "Module Administration " + suffix,
                portalAlias = alias,
                homeDirectory = string.Empty,
                templateFile = "admin.template",
                isChildPortal = false,
                administratorFirstName = "Module",
                administratorLastName = "Owner",
                administratorUsername = administrator,
                administratorPassword = ApiTestFixture.KnownPassword,
                administratorEmail = "owner." + suffix + "@example.com",
            },
            ApiTestFixture.Json);

        response.StatusCode.Should().Be(HttpStatusCode.Created, await Diagnose(response));

        using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        JsonElement data = document.RootElement.GetProperty("data");

        return new TenantFacts(
            data.GetProperty("portalId").GetInt32(),
            alias,
            data.GetProperty("homeTabId").GetInt32(),
            administrator);
    }

    /// <summary>Renders a response as an assertion message, so an unexpected status names its own reason.</summary>
    /// <param name="response">The response whose status did not match.</param>
    /// <returns>The status line and the body, bounded.</returns>
    private static async Task<string> Diagnose(HttpResponseMessage response)
    {
        string body = await response.Content.ReadAsStringAsync();

        return FormattableString.Invariant(
            $"the response was {(int)response.StatusCode} with body {body[..Math.Min(body.Length, 600)]}");
    }

    /// <summary>Creates a second tenant and returns its identity, alias and home page.</summary>
    /// <param name="host">The installation host account.</param>
    /// <returns>The created tenant facts needed by a cross-tenant module request.</returns>
    private static async Task<(int PortalId, string Alias, int HomeTabId)> CreateForeignPortalAsync(
        HttpClient host)
    {
        string suffix = Suffix();
        string alias = "module-tenant-" + suffix + ".local";

        using HttpResponseMessage response = await host.PostAsJsonAsync(
            new Uri("/api/v1/portals", UriKind.Relative),
            new
            {
                portalName = "Module Isolation " + suffix,
                portalAlias = alias,
                homeDirectory = string.Empty,
                templateFile = "admin.template",
                isChildPortal = false,
                administratorFirstName = "Module",
                administratorLastName = "Administrator",
                administratorUsername = "module_admin_" + suffix,
                administratorPassword = ApiTestFixture.KnownPassword,
                administratorEmail = "module." + suffix + "@example.com",
            },
            ApiTestFixture.Json);

        response.StatusCode.Should().Be(HttpStatusCode.Created);

        using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        JsonElement data = document.RootElement.GetProperty("data");

        return (
            data.GetProperty("portalId").GetInt32(),
            alias,
            data.GetProperty("homeTabId").GetInt32());
    }

    /// <summary>Inserts a module and its placement in a tenant created for an isolation assertion.</summary>
    /// <param name="portalId">The owning tenant.</param>
    /// <param name="tabId">The page that carries the module.</param>
    /// <returns>The module identifier.</returns>
    private async Task<int> InsertForeignModuleAsync(int portalId, int tabId)
    {
        int moduleId = await _fixture.Database.ScalarAsync<int>(
            """
            INSERT INTO [dbo].[Modules]
                ([ModuleDefID], [PortalID], [ModuleTitle], [AllTabs], [IsDeleted],
                 [InheritViewPermissions])
            VALUES (@moduleDefinitionId, @portalId, N'Cross-tenant permission module', 0, 0, 0);
            SELECT CAST(SCOPE_IDENTITY() AS int);
            """,
            new Dictionary<string, object?>
            {
                ["moduleDefinitionId"] = _fixture.Seed.ModuleDefinitionId,
                ["portalId"] = portalId,
            });

        await _fixture.Database.ExecuteAsync(
            """
            INSERT INTO [dbo].[TabModules]
                ([TabID], [ModuleID], [PaneName], [ModuleOrder], [CacheTime], [Visibility],
                 [DisplayTitle], [DisplayPrint], [DisplaySyndicate])
            VALUES (@tabId, @moduleId, N'ContentPane', 1, 0, 0, 1, 0, 0);
            """,
            new Dictionary<string, object?>
            {
                ["tabId"] = tabId,
                ["moduleId"] = moduleId,
            });

        return moduleId;
    }

    /// <summary>Inserts one host-administration package and returns its definition identifier.</summary>
    /// <returns>The administrative definition identifier.</returns>
    private async Task<int> InsertAdministrativeDefinitionAsync()
    {
        string suffix = Suffix();

        return await _fixture.Database.ScalarAsync<int>(
            """
            DECLARE @desktopModuleId int;

            INSERT INTO [dbo].[DesktopModules]
                ([FriendlyName], [Description], [Version], [IsPremium], [IsAdmin],
                 [BusinessControllerClass], [FolderName], [ModuleName], [SupportedFeatures])
            VALUES
                (@friendlyName, N'Administrative package exclusion regression', N'01.00.00', 0, 1,
                 NULL, N'Admin/Regression', @moduleName, 0);

            SET @desktopModuleId = CAST(SCOPE_IDENTITY() AS int);

            INSERT INTO [dbo].[ModuleDefinitions] ([FriendlyName], [DesktopModuleID], [DefaultCacheTime])
            VALUES (@definitionName, @desktopModuleId, 0);

            SELECT CAST(SCOPE_IDENTITY() AS int);
            """,
            new Dictionary<string, object?>
            {
                ["friendlyName"] = "Administrative Regression " + suffix,
                ["moduleName"] = "AdministrativeRegression" + suffix,
                ["definitionName"] = "Administrative Definition " + suffix,
            });
    }

    /// <summary>Inserts one placed administrative module carrying a security-owned setting.</summary>
    /// <returns>The administrative module identifier.</returns>
    private async Task<int> InsertAdministrativeModuleAsync()
    {
        int definitionId = await InsertAdministrativeDefinitionAsync();
        int moduleId = await _fixture.Database.ScalarAsync<int>(
            """
            INSERT INTO [dbo].[Modules]
                ([ModuleDefID], [PortalID], [ModuleTitle], [AllTabs], [IsDeleted],
                 [InheritViewPermissions])
            VALUES (@definitionId, @portalId, N'Administrative settings regression', 0, 0, 0);

            SELECT CAST(SCOPE_IDENTITY() AS int);
            """,
            new Dictionary<string, object?>
            {
                ["definitionId"] = definitionId,
                ["portalId"] = _fixture.Seed.PortalId,
            });

        await _fixture.Database.ExecuteAsync(
            """
            INSERT INTO [dbo].[TabModules]
                ([TabID], [ModuleID], [PaneName], [ModuleOrder], [CacheTime], [Visibility],
                 [DisplayTitle], [DisplayPrint], [DisplaySyndicate])
            VALUES (@tabId, @moduleId, N'ContentPane', 1, 0, 0, 1, 0, 0);

            INSERT INTO [dbo].[ModuleSettings] ([ModuleID], [SettingName], [SettingValue])
            VALUES (@moduleId, N'Security_EmailValidation', N'original-expression');
            """,
            new Dictionary<string, object?>
            {
                ["tabId"] = _fixture.Seed.RootTabId,
                ["moduleId"] = moduleId,
            });

        return moduleId;
    }

    /// <summary>Inserts a placed package that declares portable-content support.</summary>
    /// <returns>The module identifier and stable package name.</returns>
    private async Task<(int ModuleId, string ModuleName)> InsertPortableModuleAsync()
    {
        string suffix = Suffix();
        string moduleName = "PortableRegression" + suffix;
        int moduleId = await _fixture.Database.ScalarAsync<int>(
            """
            DECLARE @desktopModuleId int;
            DECLARE @definitionId int;

            INSERT INTO [dbo].[DesktopModules]
                ([FriendlyName], [Description], [Version], [IsPremium], [IsAdmin],
                 [BusinessControllerClass], [FolderName], [ModuleName], [SupportedFeatures])
            VALUES
                (@friendlyName, N'Portable-content security regression', N'01.00.00', 0, 0,
                 N'Integration.UnregisteredPortableController', N'Portable/Regression',
                 @moduleName, 1);

            SET @desktopModuleId = CAST(SCOPE_IDENTITY() AS int);

            INSERT INTO [dbo].[ModuleDefinitions] ([FriendlyName], [DesktopModuleID], [DefaultCacheTime])
            VALUES (@definitionName, @desktopModuleId, 0);

            SET @definitionId = CAST(SCOPE_IDENTITY() AS int);

            INSERT INTO [dbo].[Modules]
                ([ModuleDefID], [PortalID], [ModuleTitle], [AllTabs], [IsDeleted],
                 [InheritViewPermissions])
            VALUES (@definitionId, @portalId, N'Portable import regression', 0, 0, 0);

            SELECT CAST(SCOPE_IDENTITY() AS int);
            """,
            new Dictionary<string, object?>
            {
                ["friendlyName"] = "Portable Regression " + suffix,
                ["definitionName"] = "Portable Definition " + suffix,
                ["moduleName"] = moduleName,
                ["portalId"] = _fixture.Seed.PortalId,
            });

        await _fixture.Database.ExecuteAsync(
            """
            INSERT INTO [dbo].[TabModules]
                ([TabID], [ModuleID], [PaneName], [ModuleOrder], [CacheTime], [Visibility],
                 [DisplayTitle], [DisplayPrint], [DisplaySyndicate])
            VALUES (@tabId, @moduleId, N'ContentPane', 1, 0, 0, 1, 0, 0);
            """,
            new Dictionary<string, object?>
            {
                ["tabId"] = _fixture.Seed.RootTabId,
                ["moduleId"] = moduleId,
            });

        return (moduleId, moduleName);
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

    /// <summary>
    /// Creation, the single read and the listing all report the SAME definition and package for one module,
    /// and every one of those five values matches the definition catalogue.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// The catalogue is consulted as the independent control, so this asserts agreement with the STORE
    /// rather than merely agreement among the three projections - three endpoints that agree on a wrong
    /// value would otherwise pass.
    /// </remarks>
    [Fact]
    [Trait("Category", "Integration")]
    public async Task ModuleCatalogueProjections_AgreeAcrossCreationTheSingleReadAndTheListing()
    {
        using HttpClient client = await _fixture.CreateHostClientAsync();

        CreateModuleRequest request = NewModuleRequest(_fixture.Seed.RootTabId);

        using HttpResponseMessage created = await client.PostAsJsonAsync(
            ModulesRoute(_fixture.Seed.PortalId),
            request,
            ApiTestFixture.Json);

        created.StatusCode.Should().Be(HttpStatusCode.Created);
        ModuleDetailDto fromCreate = await ReadDetailAsync(created);

        try
        {
            using HttpResponseMessage read = await client.GetAsync(
                ModuleRoute(_fixture.Seed.PortalId, fromCreate.ModuleId));

            read.StatusCode.Should().Be(HttpStatusCode.OK);
            ModuleDetailDto fromRead = await ReadDetailAsync(read);

            ModuleListItemDto fromList = (await ListModulesAsync(client, includeDeleted: false))
                .Should().ContainSingle(item => item.TabModuleId == fromCreate.TabModuleId)
                .Subject;

            // The store's own answer, read through the catalogue the tenant is offered.
            using HttpResponseMessage catalogueResponse = await client.GetAsync(
                new Uri("/api/v1/module-definitions", UriKind.Relative));

            catalogueResponse.StatusCode.Should().Be(HttpStatusCode.OK);

            IReadOnlyList<ModuleDefinitionDto>? catalogue = await catalogueResponse.Content
                .ReadEnvelopeAsync<IReadOnlyList<ModuleDefinitionDto>>();

            ModuleDefinitionDto definition = catalogue
                .Should().NotBeNull().And.Subject
                .Should().ContainSingle(entry => entry.ModuleDefId == request.ModuleDefId)
                .Subject;

            definition.DesktopModuleId.Should().Be(
                _fixture.Seed.DesktopModuleId,
                "the catalogue is the control, so it must name the package the fixture seeded");

            // The control must itself carry every value, or "all four agree" could be satisfied by all
            // four being null - which is precisely the defect this test exists to catch.
            definition.FriendlyName.Should().NotBeNullOrWhiteSpace();
            definition.ModuleName.Should().NotBeNullOrWhiteSpace();
            definition.Description.Should().NotBeNullOrWhiteSpace();
            definition.Version.Should().NotBeNullOrWhiteSpace();

            // Every projection against the control.
            foreach ((string source, int? packageId, string? friendly, string? name, string? description, string? version) in
                new (string, int?, string?, string?, string?, string?)[]
                {
                    ("creation", fromCreate.DesktopModuleId, fromCreate.FriendlyName, fromCreate.ModuleName, fromCreate.Description, fromCreate.Version),
                    ("the single read", fromRead.DesktopModuleId, fromRead.FriendlyName, fromRead.ModuleName, fromRead.Description, fromRead.Version),
                    ("the listing", fromList.DesktopModuleId, fromList.FriendlyName, fromList.ModuleName, fromList.Description, fromList.Version),
                })
            {
                packageId.Should().Be(
                    _fixture.Seed.DesktopModuleId,
                    FormattableString.Invariant($"{source} must name the module's real package"));

                packageId.Should().NotBe(
                    0,
                    FormattableString.Invariant(
                        $"{source} must not fabricate a package key; DesktopModuleID seeds at 1"));

                friendly.Should().Be(
                    definition.FriendlyName,
                    FormattableString.Invariant($"{source} must carry the definition's display name"));

                name.Should().Be(
                    definition.ModuleName,
                    FormattableString.Invariant($"{source} must carry the package's name"));

                description.Should().Be(
                    definition.Description,
                    FormattableString.Invariant($"{source} must carry the package's description"));

                version.Should().Be(
                    definition.Version,
                    FormattableString.Invariant($"{source} must carry the package's version"));
            }
        }
        finally
        {
            using HttpResponseMessage removed = await client.DeleteAsync(
                ModuleRoute(_fixture.Seed.PortalId, fromCreate.ModuleId));

            removed.StatusCode.Should().BeOneOf(HttpStatusCode.NoContent, HttpStatusCode.NotFound);
        }
    }

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

    /// <summary>Reads a module settings representation from a response, failing the test when it is absent.</summary>
    /// <param name="response">The response to read.</param>
    /// <returns>The representation.</returns>
    private static async Task<ModuleSettingsDto> ReadSettingsAsync(HttpResponseMessage response)
    {
        ModuleSettingsDto? settings = await response.Content
            .ReadEnvelopeAsync<ModuleSettingsDto>();

        settings.Should().NotBeNull();
        return settings!;
    }

    /// <summary>Builds the canonical collection route for the resolved tenant's modules.</summary>
    /// <param name="_">Ignored legacy call-site value; the request host resolves the tenant.</param>
    /// <returns>A relative route.</returns>
    private static Uri ModulesRoute(int _) => new("/api/v1/modules", UriKind.Relative);

    /// <summary>Builds the item route for one module.</summary>
    /// <param name="_">Ignored legacy call-site value; the request host resolves the tenant.</param>
    /// <param name="moduleId">The module identifier.</param>
    /// <returns>A relative route.</returns>
    private static Uri ModuleRoute(int _, int moduleId) =>
        new($"/api/v1/modules/{Route(moduleId)}", UriKind.Relative);

    /// <summary>Builds the settings route for one module.</summary>
    /// <param name="_">Ignored legacy call-site value; the request host resolves the tenant.</param>
    /// <param name="moduleId">The module identifier.</param>
    /// <returns>A relative route.</returns>
    private static Uri ModuleSettingsRoute(int _, int moduleId) =>
        new($"/api/v1/modules/{Route(moduleId)}/settings", UriKind.Relative);

    /// <summary>Builds the export route for one module.</summary>
    /// <param name="_">Ignored legacy call-site value; the request host resolves the tenant.</param>
    /// <param name="moduleId">The module identifier.</param>
    /// <returns>A relative route.</returns>
    private static Uri ModuleExportRoute(int _, int moduleId) =>
        new($"/api/v1/modules/{Route(moduleId)}/export", UriKind.Relative);

    /// <summary>Builds the canonical import route for the resolved tenant.</summary>
    /// <param name="_">Ignored legacy call-site value; the request host resolves the tenant.</param>
    /// <returns>A relative route.</returns>
    private static Uri ModuleImportRoute(int _) => new("/api/v1/modules/import", UriKind.Relative);

    /// <summary>Formats an identifier for a route without picking up the ambient culture.</summary>
    /// <param name="value">The identifier.</param>
    /// <returns>The invariant representation.</returns>
    private static string Route(int value) => value.ToString(CultureInfo.InvariantCulture);

    /// <summary>Addresses the module collection with an explicit page window.</summary>
    /// <param name="_">Ignored legacy call-site value; the request host resolves the tenant.</param>
    /// <param name="pageSize">The width of the window.</param>
    /// <returns>The relative address.</returns>
    /// <remarks>
    /// The window is always stated rather than left to the contract's default, so a change to that default
    /// cannot silently alter what a fact asserts. The page index is stated as zero because the request
    /// contract documents a zero base; no fact asserts the base itself, for the reason recorded on the
    /// envelope fact.
    /// </remarks>
    private static Uri ModuleListingRoute(int _, int pageSize) => new(
        FormattableString.Invariant(
            $"/api/v1/modules?pageIndex=0&pageSize={Route(pageSize)}"),
        UriKind.Relative);

    /// <summary>Reads the module collection filtered by a title fragment.</summary>
    /// <param name="client">A client entitled to read the collection.</param>
    /// <param name="query">The fragment to match.</param>
    /// <returns>The rows the filter admitted.</returns>
    private async Task<IReadOnlyList<ModuleListItemDto>> SearchModulesAsync(HttpClient client, string query)
    {
        string escaped = Uri.EscapeDataString(query);

        using HttpResponseMessage response = await client.GetAsync(new Uri(
            FormattableString.Invariant(
                $"/api/v1/modules?pageIndex=0&pageSize=100&query={escaped}"),
            UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        PagedEnvelope<ModuleListItemDto>? page = await response.Content
            .ReadFromJsonAsync<PagedEnvelope<ModuleListItemDto>>(ApiTestFixture.Json);

        page.Should().NotBeNull();
        return page!.Items;
    }

    /// <summary>Reads one member out of a success envelope's payload as raw JSON.</summary>
    /// <param name="client">A client entitled to perform the read.</param>
    /// <param name="route">The resource to read.</param>
    /// <param name="member">The camel-cased member name to return.</param>
    /// <returns>The member, cloned so that it outlives the document it was parsed from.</returns>
    /// <remarks>
    /// Necessary because a typed read cannot distinguish a member the response OMITTED from one it
    /// published as <see langword="null"/> - both deserialise to the same value - and that distinction is
    /// the whole subject of the sentinel facts. The element is cloned before the document is disposed,
    /// because a <see cref="JsonElement"/> borrowed from a disposed document throws on access.
    /// </remarks>
    private static async Task<JsonElement> ReadDataMemberAsync(HttpClient client, Uri route, string member)
    {
        using HttpResponseMessage response = await client.GetAsync(route);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        document.RootElement.TryGetProperty("data", out JsonElement data).Should().BeTrue(
            "a successful read publishes its payload under the envelope's data member");
        data.TryGetProperty(member, out JsonElement value).Should().BeTrue(
            $"the payload publishes {member} rather than omitting it");

        return value.Clone();
    }

    /// <summary>
    /// Inserts a module numbered zero, which the API cannot create because it does not choose identifiers.
    /// </summary>
    /// <returns>A task representing the write.</returns>
    /// <remarks>
    /// The PLACEMENT is written alongside the module, and it is not optional: a module the terminal schema
    /// holds without a <c>TabModules</c> row is an unplaced module, and a read that addresses one reports
    /// absence - so a module numbered zero and placed nowhere would answer <c>404</c> for a reason that has
    /// nothing to do with its identifier and would prove nothing about the sentinel.
    /// </remarks>
    private async Task InsertModuleWithIdentifierZeroAsync()
    {
        await RemoveModuleWithIdentifierZeroAsync();

        await _fixture.Database.ExecuteAsync(
            """
            SET IDENTITY_INSERT [dbo].[Modules] ON;
            INSERT INTO [dbo].[Modules]
                ([ModuleID], [ModuleDefID], [PortalID], [ModuleTitle], [AllTabs], [IsDeleted],
                 [InheritViewPermissions])
            VALUES (0, @moduleDefinitionId, @portalId, N'Module zero', 0, 0, 1);
            SET IDENTITY_INSERT [dbo].[Modules] OFF;
            """,
            new Dictionary<string, object?>
            {
                ["moduleDefinitionId"] = _fixture.Seed.ModuleDefinitionId,
                ["portalId"] = _fixture.Seed.PortalId,
            });

        await _fixture.Database.ExecuteAsync(
            """
            INSERT INTO [dbo].[TabModules]
                ([TabID], [ModuleID], [PaneName], [ModuleOrder], [CacheTime], [Visibility],
                 [DisplayTitle], [DisplayPrint], [DisplaySyndicate])
            VALUES (@tabId, 0, N'ContentPane', 1, 0, 0, 1, 0, 0);
            """,
            new Dictionary<string, object?> { ["tabId"] = _fixture.Seed.RootTabId });
    }

    /// <summary>Removes the module numbered zero, leaving the shared collection as it was found.</summary>
    /// <returns>A task representing the write.</returns>
    /// <remarks>
    /// The placement is removed first even though <c>FK_TabModules_Modules</c> cascades, so the helper does
    /// not depend on the cascade to leave the database clean.
    /// </remarks>
    private async Task RemoveModuleWithIdentifierZeroAsync()
    {
        await _fixture.Database.ExecuteAsync(
            "DELETE FROM [dbo].[TabModules] WHERE [ModuleID] = 0;",
            new Dictionary<string, object?>());

        await _fixture.Database.ExecuteAsync(
            "DELETE FROM [dbo].[Modules] WHERE [ModuleID] = 0;",
            new Dictionary<string, object?>());
    }

    /// <summary>Produces a short random suffix for values that must differ between tests.</summary>
    /// <returns>Twelve lower-case hexadecimal characters.</returns>
    private static string Suffix() => Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture)[..12];
}
