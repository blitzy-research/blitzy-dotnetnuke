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
/// The subject here is the catalogue of permission DEFINITIONS - the rows of the <c>Permission</c> table
/// that say which permissions exist and in which scope - and never the grants that award them to a role or
/// an account. The legacy module- and page-keyed catalogue helpers remain application-layer capabilities,
/// but the frozen API does not publish child resources for them.
/// </para>
/// <para>
/// The catalogue carries no portal column: it is installation-wide reference data seeded by the upgrade
/// scripts. These addresses take no tenant segment and no tenant query value, and the administrator gate is
/// still evaluated against the tenant the request host resolves to - which is what keeps a member of a
/// tenant from browsing it.
/// </para>
/// </remarks>
[Trait("Category", "Integration")]
[Collection(IntegrationTestCollection.Name)]
public sealed class PermissionApiTests
{
    /// <summary>An identifier no seeded catalogue row can hold.</summary>
    private const int UnknownPermissionId = 987654;

    /// <summary>
    /// A module-definition identifier no seeded or suite-created definition bears, used to prove that a
    /// custom catalogue key is scoped to the definition it names.
    /// </summary>
    private const int UnreachableModuleDefinitionId = 987654;

    private readonly ApiTestFixture _fixture;

    /// <summary>Initialises a new instance of the <see cref="PermissionApiTests"/> class.</summary>
    /// <param name="fixture">The shared host, database and seed.</param>
    public PermissionApiTests(ApiTestFixture fixture) => _fixture = fixture;

    /// <summary>
    /// The catalogue listing answers <c>200 OK</c> with DEFINITIONS, each carrying the identifier the detail
    /// read is addressed by.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// The identifier assertion is the point of this test. The listing used to publish bare key spellings,
    /// so a client that listed the catalogue held nothing it could pass to
    /// <c>GET /api/v1/permissions/{permissionId}</c> - the two reads shared no handle and list-to-detail
    /// navigation was impossible from the API alone.
    /// </remarks>
    [Fact]
    public async Task ListPermissionCatalogue_ReturnsOkWithDefinitionsCarryingTheirIdentifiers()
    {
        using HttpClient client = await _fixture.CreateHostClientAsync();

        using HttpResponseMessage response = await client.GetAsync(
            new Uri("/api/v1/permissions", UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        CollectionEnvelope<PermissionDto>? envelope = await response.Content
            .ReadFromJsonAsync<CollectionEnvelope<PermissionDto>>(ApiTestFixture.Json);

        envelope.Should().NotBeNull();
        envelope!.Data.Should().NotBeNull();
        envelope.Data!.Select(definition => definition.PermissionKey)
            .Should().Contain("VIEW").And.Contain("EDIT");

        envelope.Data.Should().OnlyContain(
            definition => definition.PermissionId > 0,
            "every entry must carry the identifier its detail read is addressed by");
        envelope.Data.Should().OnlyContain(
            definition => !string.IsNullOrWhiteSpace(definition.PermissionCode),
            "an entry names the scope it belongs to, which a bare key never did");

        envelope.Data.Select(definition => definition.PermissionId).Should().BeInAscendingOrder()
            .And.OnlyHaveUniqueItems("one row per definition, in a stable order");

        // The handle the listing published resolves through the detail read, which is the navigation the
        // bare-key listing made impossible.
        int firstId = envelope.Data[0].PermissionId;

        using HttpResponseMessage detail = await client.GetAsync(new Uri(
            "/api/v1/permissions/" + firstId.ToString(CultureInfo.InvariantCulture),
            UriKind.Relative));

        detail.StatusCode.Should().Be(
            HttpStatusCode.OK,
            "an identifier the listing published must address a definition the detail read can return");
    }

    /// <summary>A key filter narrows the listing to the definitions declaring that key.</summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task ListPermissionCatalogue_WithAKeyFilter_NarrowsToThatKey()
    {
        using HttpClient client = await _fixture.CreateHostClientAsync();

        using HttpResponseMessage response = await client.GetAsync(
            new Uri("/api/v1/permissions?permissionKey=EDIT", UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        CollectionEnvelope<PermissionDto>? envelope = await response.Content
            .ReadFromJsonAsync<CollectionEnvelope<PermissionDto>>(ApiTestFixture.Json);

        envelope.Should().NotBeNull();
        envelope!.Data.Should().NotBeNull();
        envelope.Data!.Should().NotBeEmpty("the seeded catalogue declares this key");
        envelope.Data.Should().OnlyContain(
            definition => string.Equals(definition.PermissionKey, "EDIT", StringComparison.OrdinalIgnoreCase),
            "the filter narrows the catalogue rather than being echoed back");
    }

    /// <summary>
    /// A key a module package registered appears in the catalogue listing, with its identifier, exactly as
    /// stored.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// <para>
    /// THIS IS THE MEASURED REGRESSION. The unscoped listing used to be assembled from the four
    /// <c>PermissionKey</c> members this solution names rather than read from <c>dbo.Permission</c>, so a key
    /// registered by a module package at install time was absent from it while the module permission matrix
    /// on the same screen displayed and evaluated that very key. One installation cannot hold two
    /// catalogues.
    /// </para>
    /// <para>
    /// The row is planted by direct statement for the reason recorded on the detail-read test below: no API
    /// path creates catalogue definitions, and a real installation acquires one when a package calls
    /// <c>AddPermission</c>.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task ListPermissionCatalogue_IncludesAKeyThisSolutionDoesNotName()
    {
        const string storedKey = "QA_CUSTOM";
        string permissionCode = "QA_MODULE_" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture)[..8];
        int permissionId = await InsertCataloguePermissionAsync(
            permissionCode,
            _fixture.Seed.ModuleDefinitionId,
            storedKey);

        try
        {
            using HttpClient client = await _fixture.CreateHostClientAsync();

            using HttpResponseMessage response = await client.GetAsync(
                new Uri("/api/v1/permissions", UriKind.Relative));

            response.StatusCode.Should().Be(HttpStatusCode.OK);

            CollectionEnvelope<PermissionDto>? envelope = await response.Content
                .ReadFromJsonAsync<CollectionEnvelope<PermissionDto>>(ApiTestFixture.Json);

            envelope.Should().NotBeNull();
            envelope!.Data.Should().NotBeNull();

            PermissionDto? planted = envelope.Data!
                .FirstOrDefault(definition => definition.PermissionId == permissionId);

            planted.Should().NotBeNull(
                "the listing reports the catalogue the installation declares, not the vocabulary this "
                + "solution happens to enumerate");
            planted!.PermissionKey.Should().Be(
                storedKey,
                "the key travels verbatim, neither re-cased nor folded onto a named member");
            planted.PermissionCode.Should().Be(permissionCode);

            // Narrowing by the row's own scope code finds it too, so the code filter reads the store rather
            // than probing the enumeration one member at a time.
            using HttpResponseMessage scoped = await client.GetAsync(new Uri(
                "/api/v1/permissions?permissionCode=" + permissionCode,
                UriKind.Relative));

            scoped.StatusCode.Should().Be(HttpStatusCode.OK);

            CollectionEnvelope<PermissionDto>? scopedEnvelope = await scoped.Content
                .ReadFromJsonAsync<CollectionEnvelope<PermissionDto>>(ApiTestFixture.Json);

            scopedEnvelope.Should().NotBeNull();
            scopedEnvelope!.Data.Should().NotBeNull();
            scopedEnvelope.Data!.Select(definition => definition.PermissionKey).Should().Equal(
                new[] { storedKey },
                "the scope holds exactly the planted row, and its unnamed key is reported");
        }
        finally
        {
            await DeleteCataloguePermissionAsync(permissionId);
        }
    }

    /// <summary>An unrecognised key spelling is refused by model binding.</summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task ListPermissionCatalogue_WithAnUnrecognisedKey_ReturnsBadRequest()
    {
        using HttpClient client = await _fixture.CreateHostClientAsync();

        using HttpResponseMessage response = await client.GetAsync(
            new Uri("/api/v1/permissions?permissionKey=NOT_A_KEY", UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    /// <summary>One catalogue definition is readable in full by its own identifier.</summary>
    /// <returns>A task representing the test.</returns>
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

    /// <summary>Unknown module and page identifiers return no catalogue metadata.</summary>
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
    /// A portal administrator cannot use another tenant's module or page identifier to read catalogue
    /// metadata.
    /// </summary>
    /// <returns>A task representing the test.</returns>
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
    /// Every read on this resource is administrator-only, so a member of the tenant holding a valid token
    /// is refused.
    /// </summary>
    /// <param name="path">The address to attempt as a member.</param>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// This proves the gate is portal-administrator membership read from stored role assignments rather
    /// than mere authentication - and it matters more here than on most resources, because the catalogue
    /// describes the shape of the installation's whole permission model.
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
    /// The catalogue exposes no mutator, because its rows are written only during module installation and
    /// that subsystem lies beyond this migration's scope.
    /// </summary>
    /// <param name="method">The write verb to attempt.</param>
    /// <param name="path">The address to attempt it against.</param>
    /// <returns>A task representing the test.</returns>
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

    /// <summary>
    /// A catalogue row whose key is not one of the four the upgrade chain seeds is read back through the
    /// API with its key intact.
    /// </summary>
    /// <param name="storedKey">A key spelling the free-text column admits and the enumeration does not.</param>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// <para>
    /// The row is planted by direct statement because no API path creates catalogue definitions, which is
    /// also how a real installation acquires one: DotNetNuke's <c>AddPermission</c> procedure takes
    /// <c>@PermissionKey varchar(50)</c> (<c>04.06.00.SqlDataProvider</c> line 407) and a third-party module
    /// calls it at install time to register its own keys. The column is <c>varchar(50) NOT NULL</c> with no
    /// check constraint, so every spelling below is legal stored data.
    /// </para>
    /// <para>
    /// This read used to answer <c>500</c> unconditionally - for a host account as readily as for anyone
    /// else - because the entity typed the key as the closed enumeration and the provider's converter threw
    /// from inside the materialiser. The assertion is on the key travelling VERBATIM rather than merely on
    /// the status, because folding an unrecognised spelling onto a sentinel member would satisfy the status
    /// while reporting a value the row does not hold.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData("CUSTOM")]
    [InlineData("MANAGE_SUBSCRIPTIONS")]
    [InlineData("AAAAAAAAAABBBBBBBBBBCCCCCCCCCCDDDDDDDDDDEEEEEEEEEE")]
    public async Task GetPermission_WhenTheKeyIsOutsideTheSeededSpellings_ReturnsOkWithTheKeyVerbatim(
        string storedKey)
    {
        string permissionCode = "QA_MODULE_" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture)[..8];
        int permissionId = await InsertCataloguePermissionAsync(
            permissionCode,
            _fixture.Seed.ModuleDefinitionId,
            storedKey);

        try
        {
            using HttpClient client = await _fixture.CreateHostClientAsync();

            using HttpResponseMessage response = await client.GetAsync(new Uri(
                "/api/v1/permissions/" + permissionId.ToString(CultureInfo.InvariantCulture),
                UriKind.Relative));

            response.StatusCode.Should().Be(
                HttpStatusCode.OK,
                "the schema admits this key, so the read must answer it rather than fault");

            PermissionDto? definition = await response.Content.ReadEnvelopeAsync<PermissionDto>();

            definition.Should().NotBeNull();
            definition!.PermissionKey.Should().Be(
                storedKey,
                "the stored spelling is the answer; nothing re-cases it and nothing substitutes a member");
            definition.PermissionCode.Should().Be(permissionCode);
            definition.ModuleDefId.Should().Be(_fixture.Seed.ModuleDefinitionId);
        }
        finally
        {
            await DeleteCataloguePermissionAsync(permissionId);
        }
    }

    /// <summary>
    /// Registering a key outside the seeded spellings against a module's own definition leaves that
    /// module's authorisation verdict for a NON-HOST caller exactly as it was.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// <para>
    /// This is the arm of the defect that mattered most and hid best. Permission evaluation short-circuits
    /// for a host account before the catalogue is ever read, so a host saw nothing wrong; every ordinary
    /// portal caller received <c>500</c> from the authorisation path itself, with no grant row required -
    /// registering the key against the module's OWN <c>ModuleDefID</c> was enough, and registering it against
    /// any other definition was not. Both halves of that scoping are asserted here.
    /// </para>
    /// <para>
    /// The verdict is captured BEFORE the row is planted and compared with the verdict after, rather than
    /// asserted against a predicted status. That way the test cannot pass by accident if the gate on this
    /// route changes, and it fails on any difference the extra catalogue row makes - a fault, a refusal or a
    /// newly granted read alike.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task ModuleAuthorization_ForANonHostCaller_IsUnaffectedByACustomCatalogueKey()
    {
        // Built ON the seeded definition rather than on one of its own, because that is what makes a row
        // registered against that definition part of THIS module's catalogue read - the reachability rule the
        // defect's scoping turned on.
        int moduleId = await InsertModuleOnSeededDefinitionAsync();
        string permissionCode = "QA_MODULE_" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture)[..8];
        int ownDefinitionRow = 0;
        int otherDefinitionRow = 0;

        try
        {
            using HttpClient administrator = await _fixture.CreateAdministratorClientAsync();
            Uri moduleAddress = new(
                "/api/v1/modules/" + moduleId.ToString(CultureInfo.InvariantCulture),
                UriKind.Relative);

            HttpStatusCode before;
            using (HttpResponseMessage baseline = await administrator.GetAsync(moduleAddress))
            {
                before = baseline.StatusCode;
            }

            before.Should().NotBe(
                HttpStatusCode.InternalServerError,
                "the baseline must be a real verdict for the comparison below to mean anything");

            // Registered against a DIFFERENT definition first: proven at runtime to leave the module read
            // alone even while the defect was live, so it establishes that the row itself is not what breaks
            // anything.
            otherDefinitionRow = await InsertCataloguePermissionAsync(
                permissionCode + "_OTHER",
                UnreachableModuleDefinitionId,
                "CUSTOM");

            using (HttpResponseMessage foreignDefinition = await administrator.GetAsync(moduleAddress))
            {
                foreignDefinition.StatusCode.Should().Be(before);
            }

            // And now against the module's own definition, which is the combination that faulted.
            ownDefinitionRow = await InsertCataloguePermissionAsync(
                permissionCode,
                _fixture.Seed.ModuleDefinitionId,
                "CUSTOM");

            using HttpResponseMessage afterCustomKey = await administrator.GetAsync(moduleAddress);

            afterCustomKey.StatusCode.Should().NotBe(
                HttpStatusCode.InternalServerError,
                "authorisation must reach a verdict, never an unhandled fault");
            afterCustomKey.StatusCode.Should().Be(
                before,
                "a catalogue entry this solution does not name confers nothing and denies nothing");
        }
        finally
        {
            if (ownDefinitionRow != 0)
            {
                await DeleteCataloguePermissionAsync(ownDefinitionRow);
            }

            if (otherDefinitionRow != 0)
            {
                await DeleteCataloguePermissionAsync(otherDefinitionRow);
            }

            await DeleteModuleAsync(moduleId);
        }
    }

    /// <summary>
    /// A GRANT pointing at a custom catalogue key still lets the signed-in caller read its own identity.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// The last arm of the defect. <c>GET /auth/me</c> projects the caller's effective permission keys, which
    /// resolves each grant the caller reaches back to its catalogue row - so a grant naming a custom key was
    /// enough to fault the identity endpoint for a portal administrator while leaving a host account
    /// untouched. The custom key is expected to appear in the projection, because the caller genuinely holds
    /// it: a grant is a grant whether or not this solution has a name for what it grants.
    /// </remarks>
    [Fact]
    public async Task GetCurrentUser_ForANonHostCaller_ToleratesAGrantOnACustomCatalogueKey()
    {
        const string storedKey = "CUSTOM";

        // On the seeded definition, so the grant below is IN SCOPE for the module it is recorded against -
        // a grant whose catalogue entry belongs to another definition is discarded before its key is read,
        // which would make this test pass without exercising anything.
        int moduleId = await InsertModuleOnSeededDefinitionAsync();
        string permissionCode = "QA_MODULE_" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture)[..8];
        int permissionId = await InsertCataloguePermissionAsync(
            permissionCode,
            _fixture.Seed.ModuleDefinitionId,
            storedKey);

        try
        {
            await _fixture.Database.ExecuteAsync(
                """
                INSERT INTO [dbo].[ModulePermission] ([ModuleID], [PermissionID], [RoleID], [AllowAccess])
                VALUES (@moduleId, @permissionId, @roleId, 1);
                """,
                new Dictionary<string, object?>
                {
                    ["moduleId"] = moduleId,
                    ["permissionId"] = permissionId,
                    ["roleId"] = _fixture.Seed.AdministratorRoleId,
                });

            using HttpClient administrator = await _fixture.CreateAdministratorClientAsync();

            using HttpResponseMessage response = await administrator.GetAsync(
                new Uri("/api/v1/auth/me", UriKind.Relative));

            response.StatusCode.Should().Be(
                HttpStatusCode.OK,
                "resolving the caller's own authority must not fault on a key the schema permits");

            string body = await response.Content.ReadAsStringAsync();

            body.Should().Contain(
                storedKey,
                "the caller does hold the grant, so the key it confers belongs in the projection verbatim");
        }
        finally
        {
            await _fixture.Database.ExecuteAsync(
                "DELETE FROM [dbo].[ModulePermission] WHERE [PermissionID] = @permissionId",
                new Dictionary<string, object?> { ["permissionId"] = permissionId });
            await DeleteCataloguePermissionAsync(permissionId);
            await DeleteModuleAsync(moduleId);
        }
    }

    /// <summary>Plants one catalogue definition by direct statement.</summary>
    /// <param name="permissionCode">The scope code the row belongs to.</param>
    /// <param name="moduleDefinitionId">The definition the row declares.</param>
    /// <param name="permissionKey">The key exactly as it should be stored.</param>
    /// <returns>The generated identifier of the planted row.</returns>
    private Task<int> InsertCataloguePermissionAsync(
        string permissionCode,
        int moduleDefinitionId,
        string permissionKey) =>
        _fixture.Database.ScalarAsync<int>(
            """
            INSERT INTO [dbo].[Permission] ([PermissionCode], [ModuleDefID], [PermissionKey], [PermissionName])
            VALUES (@permissionCode, @moduleDefinitionId, @permissionKey, @permissionName);
            SELECT CAST(SCOPE_IDENTITY() AS int);
            """,
            new Dictionary<string, object?>
            {
                ["permissionCode"] = permissionCode,
                ["moduleDefinitionId"] = moduleDefinitionId,
                ["permissionKey"] = permissionKey,
                ["permissionName"] = "QA custom permission",
            });

    /// <summary>Removes one planted catalogue definition.</summary>
    /// <param name="permissionId">The row to remove.</param>
    /// <returns>A task that completes when the row is gone.</returns>
    private Task<int> DeleteCataloguePermissionAsync(int permissionId) =>
        _fixture.Database.ExecuteAsync(
            "DELETE FROM [dbo].[Permission] WHERE [PermissionID] = @permissionId",
            new Dictionary<string, object?> { ["permissionId"] = permissionId });

    /// <summary>Inserts one module into the seeded tenant, built on the SEEDED module definition.</summary>
    /// <returns>The module identifier.</returns>
    /// <remarks>
    /// Deliberately reuses <c>Seed.ModuleDefinitionId</c> instead of creating a definition of its own, which
    /// the sibling helper below does. A catalogue entry is only in a module's read set when it carries the
    /// product-wide module-definition scope code or names that module's OWN definition, so a module on a
    /// private definition cannot be used to exercise a catalogue entry registered against the seeded one.
    /// </remarks>
    private Task<int> InsertModuleOnSeededDefinitionAsync() =>
        _fixture.Database.ScalarAsync<int>(
            """
            INSERT INTO [dbo].[Modules]
                ([ModuleDefID], [PortalID], [ModuleTitle], [AllTabs], [IsDeleted], [InheritViewPermissions])
            VALUES (@moduleDefinitionId, @portalId, N'Custom permission key probe', 0, 0, 0);
            SELECT CAST(SCOPE_IDENTITY() AS int);
            """,
            new Dictionary<string, object?>
            {
                ["moduleDefinitionId"] = _fixture.Seed.ModuleDefinitionId,
                ["portalId"] = _fixture.Seed.PortalId,
            });

    /// <summary>Removes one planted module and its grants, leaving its definition in place.</summary>
    /// <param name="moduleId">The module to remove.</param>
    /// <returns>A task that completes when both row sets are gone.</returns>
    /// <remarks>
    /// The definition is deliberately NOT deleted. A module planted by
    /// <see cref="InsertModuleOnSeededDefinitionAsync"/> shares the suite-wide seeded definition, and
    /// removing that would take every other test in the collection down with it.
    /// </remarks>
    private Task<int> DeleteModuleAsync(int moduleId) =>
        _fixture.Database.ExecuteAsync(
            """
            DELETE FROM [dbo].[ModulePermission] WHERE [ModuleID] = @moduleId;
            DELETE FROM [dbo].[Modules] WHERE [ModuleID] = @moduleId;
            """,
            new Dictionary<string, object?> { ["moduleId"] = moduleId });

    /// <summary>Inserts one module owned by the supplied portal, with a definition of its own.</summary>
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
