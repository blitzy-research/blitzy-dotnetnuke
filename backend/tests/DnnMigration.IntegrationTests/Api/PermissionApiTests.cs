using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using DnnMigration.Application.Abstractions;
using DnnMigration.Application.Dtos.Portal;
using FluentAssertions;
using Xunit;

namespace DnnMigration.IntegrationTests.Api;

/// <summary>
/// Covers the permission CATALOGUE read surface end to end: the filtered key listing and the by-identifier
/// definition read.
/// </summary>
/// <remarks>
/// <para>
/// The subject here is the catalogue of permission DEFINITIONS - the rows of the <c>Permission</c> table that
/// say which permissions exist and in which scope - and never the grants that award them to a role or an
/// account. The legacy module- and page-keyed catalogue helpers remain application-layer capabilities, but
/// the frozen API does not publish child resources for them.
/// </para>
/// <para>
/// The catalogue carries no portal column: it is installation-wide reference data seeded by the upgrade
/// scripts. These addresses take no tenant segment and no tenant query value, and the administrator gate is
/// still evaluated against the tenant the request host resolves to - which is what keeps a member of a tenant
/// from browsing it.
/// </para>
/// </remarks>
[Trait("Category", "Integration")]
[Collection(IntegrationTestCollection.Name)]
public sealed class PermissionApiTests
{
    /// <summary>An identifier no seeded catalogue row can hold.</summary>
    private const int UnknownPermissionId = 987654;

    private readonly ApiTestFixture _fixture;

    /// <summary>Initialises a new instance of the <see cref="PermissionApiTests"/> class.</summary>
    /// <param name="fixture">The shared host, database and seed.</param>
    public PermissionApiTests(ApiTestFixture fixture) => _fixture = fixture;

    /// <summary>The key listing answers <c>200 OK</c> with the seeded scope's keys.</summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task ListPermissionKeys_ReturnsOkWithTheCatalogueKeys()
    {
        using HttpClient client = await _fixture.CreateHostClientAsync();

        using HttpResponseMessage response = await client.GetAsync(
            new Uri("/api/v1/permissions", UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        CollectionEnvelope<string>? envelope = await response.Content
            .ReadFromJsonAsync<CollectionEnvelope<string>>(ApiTestFixture.Json);

        envelope.Should().NotBeNull();
        envelope!.Data.Should().NotBeNull();
        envelope.Data!.Should().Contain("VIEW").And.Contain("EDIT");
    }

    /// <summary>A key filter narrows the listing to that one key.</summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// The filter is typed as the closed key enumeration rather than as free text, so the answer is either
    /// that one key or nothing at all - a caller cannot smuggle an arbitrary string into the query.
    /// </remarks>
    [Fact]
    public async Task ListPermissionKeys_WithAKeyFilter_NarrowsToThatKey()
    {
        using HttpClient client = await _fixture.CreateHostClientAsync();

        using HttpResponseMessage response = await client.GetAsync(
            new Uri("/api/v1/permissions?permissionKey=EDIT", UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        CollectionEnvelope<string>? envelope = await response.Content
            .ReadFromJsonAsync<CollectionEnvelope<string>>(ApiTestFixture.Json);

        envelope.Should().NotBeNull();
        envelope!.Data.Should().NotBeNull();
        envelope.Data!.Should().Equal("EDIT");
    }

    /// <summary>An unrecognised key spelling is refused by model binding.</summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task ListPermissionKeys_WithAnUnrecognisedKey_ReturnsBadRequest()
    {
        using HttpClient client = await _fixture.CreateHostClientAsync();

        using HttpResponseMessage response = await client.GetAsync(
            new Uri("/api/v1/permissions?permissionKey=NOT_A_KEY", UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    /// <summary>One catalogue definition is readable in full by its own identifier.</summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// The record rather than the bare key is what a caller needs: a key alone cannot say which scope code or
    /// which module definition declared it, and the same key is declared repeatedly across scopes.
    /// </remarks>
    [Fact]
    public async Task GetPermission_ByIdentifier_ReturnsOkWithTheDefinition()
    {
        using HttpClient client = await _fixture.CreateHostClientAsync();

        using HttpResponseMessage response = await client.GetAsync(new Uri(
            "/api/v1/permissions/"
                + _fixture.Seed.ModuleViewPermissionId.ToString(CultureInfo.InvariantCulture),
            UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        // Read through the ENVELOPE, which is what every payload-bearing success in this API publishes.
        PermissionDto? definition = await response.Content
            .ReadEnvelopeAsync<PermissionDto>();

        definition.Should().NotBeNull();
        definition!.PermissionId.Should().Be(_fixture.Seed.ModuleViewPermissionId);
        definition.PermissionKey.Should().Be("VIEW");
        definition.PermissionCode.Should().Be(IntegrationSeed.ModulePermissionCode);
    }

    /// <summary>An identifier naming no catalogue row answers <c>404 Not Found</c>.</summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task GetPermission_WhenUnknown_ReturnsNotFound()
    {
        using HttpClient client = await _fixture.CreateHostClientAsync();

        using HttpResponseMessage response = await client.GetAsync(new Uri(
            "/api/v1/permissions/" + UnknownPermissionId.ToString(CultureInfo.InvariantCulture),
            UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    /// <summary>The legacy module- and page-keyed catalogue child routes are not public API resources.</summary>
    /// <param name="path">The withdrawn address to probe.</param>
    /// <returns>A task representing the test.</returns>
    [Theory]
    [InlineData("/api/v1/permissions/modules/1")]
    [InlineData("/api/v1/permissions/tabs/1")]
    public async Task PermissionCatalogue_LegacyScopedChildRoutesAreNotPublished(string path)
    {
        using HttpClient client = await _fixture.CreateHostClientAsync();

        using HttpResponseMessage response = await client.GetAsync(new Uri(path, UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    /// <summary>
    /// Unknown module and page identifiers return no catalogue metadata.
    /// </summary>
    /// <param name="path">The resource-scoped catalogue address.</param>
    /// <returns>A task representing the test.</returns>
    [Theory]
    [InlineData("/api/v1/permissions/modules/987654")]
    [InlineData("/api/v1/permissions/tabs/987654")]
    public async Task ResourceScopedPermissionDefinitions_WhenTheResourceIsUnknown_ReturnNotFound(string path)
    {
        using HttpClient client = await _fixture.CreateAdministratorClientAsync();

        using HttpResponseMessage response = await client.GetAsync(new Uri(path, UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    /// <summary>
    /// A portal administrator cannot use another tenant's module or page identifier to read catalogue metadata.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// <para>
    /// SEC-033, and the guarantee is now STRUCTURAL rather than conditional. An earlier revision published
    /// these two child addresses and added the tenant-bound existence checks that made a foreign identifier
    /// indistinguishable from an unknown one. The frozen API publishes neither address at all, so there is no
    /// route on which a resource identifier - foreign, unknown or genuine - can be exchanged for catalogue
    /// metadata. Asserting it with a REAL foreign module and a REAL foreign page is what keeps the fact
    /// meaningful: it proves the absence holds for identifiers that do exist and are owned elsewhere, which is
    /// the case an unknown-identifier probe cannot reach.
    /// </para>
    /// <para>
    /// The response body is asserted to carry no catalogue member, because the refusal must disclose nothing
    /// about the resource named in the address - not its existence, and not the permission model's shape.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task ResourceScopedPermissionDefinitions_RefuseResourcesOwnedByAnotherTenant()
    {
        using HttpClient host = await _fixture.CreateHostClientAsync();
        PortalDetailDto foreignPortal = await CreateForeignPortalAsync(host);
        int foreignModuleId = await InsertModuleAsync(foreignPortal.PortalId);
        foreignPortal.HomeTabId.Should().NotBeNull("portal creation provisions its home page");

        using HttpClient administrator = await _fixture.CreateAdministratorClientAsync();
        using HttpResponseMessage module = await administrator.GetAsync(new Uri(
            "/api/v1/permissions/modules/" + foreignModuleId.ToString(CultureInfo.InvariantCulture),
            UriKind.Relative));
        using HttpResponseMessage tab = await administrator.GetAsync(new Uri(
            "/api/v1/permissions/tabs/" + foreignPortal.HomeTabId!.Value.ToString(CultureInfo.InvariantCulture),
            UriKind.Relative));

        module.StatusCode.Should().Be(HttpStatusCode.NotFound);
        tab.StatusCode.Should().Be(HttpStatusCode.NotFound);

        string moduleBody = await module.Content.ReadAsStringAsync();
        string tabBody = await tab.Content.ReadAsStringAsync();
        moduleBody.Should().NotContain(
            "permissionCode",
            "no catalogue member may be served from an address the contract does not publish");
        tabBody.Should().NotContain("permissionCode");
    }

    /// <summary>Every read on this resource requires a bearer token.</summary>
    /// <param name="path">The address to attempt anonymously.</param>
    /// <returns>A task representing the test.</returns>
    [Theory]
    [InlineData("/api/v1/permissions")]
    [InlineData("/api/v1/permissions/1")]
    public async Task PermissionReads_WithoutCredentials_ReturnUnauthorized(string path)
    {
        using HttpClient client = _fixture.CreateAnonymousClient();

        using HttpResponseMessage response = await client.GetAsync(new Uri(path, UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    /// <summary>
    /// Every read on this resource is administrator-only, so a member of the tenant holding a valid token is
    /// refused.
    /// </summary>
    /// <param name="path">The address to attempt as a member.</param>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// This proves the gate is portal-administrator membership read from stored role assignments rather than
    /// mere authentication - and it matters more here than on most resources, because the catalogue describes
    /// the shape of the installation's whole permission model.
    /// </remarks>
    [Theory]
    [InlineData("/api/v1/permissions")]
    [InlineData("/api/v1/permissions/1")]
    public async Task PermissionReads_AsMemberWithoutAdministratorRole_ReturnForbidden(string path)
    {
        using HttpClient client = await _fixture.CreateUnprivilegedClientAsync();

        using HttpResponseMessage response = await client.GetAsync(new Uri(path, UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    /// <summary>
    /// The catalogue exposes no mutator, because its rows are written only during module installation and that
    /// subsystem lies beyond this migration's scope.
    /// </summary>
    /// <param name="method">The write verb to attempt.</param>
    /// <param name="path">The address to attempt it against.</param>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// <c>405</c> is accepted alongside <c>404</c> because which the router produces depends on whether any
    /// action is registered for the address at all; the assertion worth making is that no write reaches a
    /// handler.
    /// </remarks>
    [Theory]
    [InlineData("POST", "/api/v1/permissions")]
    [InlineData("PUT", "/api/v1/permissions")]
    [InlineData("DELETE", "/api/v1/permissions")]
    [InlineData("POST", "/api/v1/permissions/1")]
    [InlineData("PUT", "/api/v1/permissions/1")]
    [InlineData("DELETE", "/api/v1/permissions/1")]
    public async Task PermissionCatalogue_DeclaresNoMutator(string method, string path)
    {
        using HttpClient client = await _fixture.CreateHostClientAsync();

        using HttpRequestMessage request = new(new HttpMethod(method), new Uri(path, UriKind.Relative));
        using HttpResponseMessage response = await client.SendAsync(request);

        response.StatusCode.Should().BeOneOf(HttpStatusCode.NotFound, HttpStatusCode.MethodNotAllowed);
    }

    /// <summary>Creates a second portal whose resources can be used as foreign identifiers.</summary>
    /// <param name="host">The installation host account.</param>
    /// <returns>The created portal.</returns>
    private async Task<PortalDetailDto> CreateForeignPortalAsync(HttpClient host)
    {
        string suffix = Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture)[..12];

        using HttpResponseMessage response = await host.PostAsJsonAsync(
            new Uri("/api/v1/portals", UriKind.Relative),
            new
            {
                portalName = "Permission Isolation " + suffix,
                portalAlias = "permission-" + suffix + ".local",
                homeDirectory = string.Empty,
                templateFile = "admin.template",
                isChildPortal = false,
                administratorFirstName = "Permission",
                administratorLastName = "Administrator",
                administratorUsername = "permission_admin_" + suffix,
                administratorPassword = ApiTestFixture.KnownPassword,
                administratorEmail = "permission." + suffix + "@example.com",
            },
            ApiTestFixture.Json);

        response.StatusCode.Should().Be(HttpStatusCode.Created);

        PortalDetailDto? portal = await response.Content.ReadEnvelopeAsync<PortalDetailDto>();
        portal.Should().NotBeNull();
        return portal!;
    }

    /// <summary>Inserts one module owned by the supplied portal.</summary>
    /// <param name="portalId">The owning portal.</param>
    /// <returns>The module identifier.</returns>
    private async Task<int> InsertModuleAsync(int portalId)
    {
        string suffix = Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture)[..12];
        int moduleDefinitionId = await _fixture.Database.ScalarAsync<int>(
            """
            INSERT INTO [dbo].[ModuleDefinitions] ([FriendlyName], [DesktopModuleID], [DefaultCacheTime])
            VALUES (@friendlyName, @desktopModuleId, 0);
            SELECT CAST(SCOPE_IDENTITY() AS int);
            """,
            new Dictionary<string, object?>
            {
                ["friendlyName"] = "Permission Definition " + suffix,
                ["desktopModuleId"] = _fixture.Seed.DesktopModuleId,
            });

        return await _fixture.Database.ScalarAsync<int>(
            """
            INSERT INTO [dbo].[Modules]
                ([ModuleDefID], [PortalID], [ModuleTitle], [AllTabs], [IsDeleted], [InheritViewPermissions])
            VALUES (@moduleDefinitionId, @portalId, N'Foreign permission module', 0, 0, 0);
            SELECT CAST(SCOPE_IDENTITY() AS int);
            """,
            new Dictionary<string, object?>
            {
                ["moduleDefinitionId"] = moduleDefinitionId,
                ["portalId"] = portalId,
            });
    }
}
