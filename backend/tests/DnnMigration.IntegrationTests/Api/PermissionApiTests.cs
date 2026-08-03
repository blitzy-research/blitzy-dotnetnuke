using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using DnnMigration.Application.Abstractions;
using FluentAssertions;
using Xunit;

namespace DnnMigration.IntegrationTests.Api;

/// <summary>
/// Covers the permission CATALOGUE read surface end to end: the filtered key listing and the three
/// identifying definition reads.
/// </summary>
/// <remarks>
/// <para>
/// The subject here is the catalogue of permission DEFINITIONS - the rows of the <c>Permission</c> table that
/// say which permissions exist and in which scope - and never the grants that award them to a role or an
/// account. That boundary is worth stating because the legacy member names invite the opposite reading: the
/// procedure behind the module-scoped read takes a MODULE identifier yet selects from the catalogue table, and
/// the legacy controller hydrates <c>PermissionInfo</c>, the catalogue type. Both terminal bodies were
/// measured directly rather than inferred from those names.
/// </para>
/// <para>
/// Two legacy quirks are pinned deliberately rather than corrected, because reproducing them is the parity
/// requirement and a later "tidy-up" would silently change results. The module-scoped read is a UNION - the
/// module's own definition's entries together with every entry carrying the product-wide scope code - so it is
/// wider than a definition-scoped read rather than equal to it. And the page-scoped read never references its
/// page argument at all, so every page in the installation receives the identical catalogue.
/// </para>
/// <para>
/// The catalogue carries no portal column: it is installation-wide reference data seeded by the upgrade
/// scripts. So these addresses take no tenant segment and no tenant query value, and the administrator gate is
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
        using HttpClient client = _fixture.CreateHostClient();

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
        using HttpClient client = _fixture.CreateHostClient();

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
        using HttpClient client = _fixture.CreateHostClient();

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
        using HttpClient client = _fixture.CreateHostClient();

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
        using HttpClient client = _fixture.CreateHostClient();

        using HttpResponseMessage response = await client.GetAsync(new Uri(
            "/api/v1/permissions/" + UnknownPermissionId.ToString(CultureInfo.InvariantCulture),
            UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    /// <summary>
    /// The page-scoped read answers with the page scope's definitions, and answers identically for two
    /// different pages.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// Two different pages are asked precisely so that the EQUALITY of the two answers is recorded as intended
    /// behaviour rather than as a coincidence: the terminal procedure body never references its page argument.
    /// If a later change made that argument meaningful, this test would fail and demand a decision instead of
    /// passing silently.
    /// </remarks>
    [Fact]
    public async Task GetTabPermissionDefinitions_AnswerWithThePageScopeForEveryPageAlike()
    {
        using HttpClient client = _fixture.CreateHostClient();

        using HttpResponseMessage root = await client.GetAsync(new Uri(
            "/api/v1/permissions/tabs/" + _fixture.Seed.RootTabId.ToString(CultureInfo.InvariantCulture),
            UriKind.Relative));

        using HttpResponseMessage child = await client.GetAsync(new Uri(
            "/api/v1/permissions/tabs/" + _fixture.Seed.ChildTabId.ToString(CultureInfo.InvariantCulture),
            UriKind.Relative));

        root.StatusCode.Should().Be(HttpStatusCode.OK);
        child.StatusCode.Should().Be(HttpStatusCode.OK);

        CollectionEnvelope<PermissionDto>? rootEnvelope = await root.Content
            .ReadFromJsonAsync<CollectionEnvelope<PermissionDto>>(ApiTestFixture.Json);
        CollectionEnvelope<PermissionDto>? childEnvelope = await child.Content
            .ReadFromJsonAsync<CollectionEnvelope<PermissionDto>>(ApiTestFixture.Json);

        rootEnvelope.Should().NotBeNull();
        childEnvelope.Should().NotBeNull();
        rootEnvelope!.Data.Should().NotBeNull();
        childEnvelope!.Data.Should().NotBeNull();

        rootEnvelope.Data!.Should().OnlyContain(
            item => item.PermissionCode == IntegrationSeed.TabPermissionCode);
        rootEnvelope.Data!.Select(item => item.PermissionId).Should()
            .Contain(_fixture.Seed.TabViewPermissionId);

        childEnvelope.Data!.Select(item => item.PermissionId).Should().Equal(
            rootEnvelope.Data!.Select(item => item.PermissionId),
            "the terminal statement never references the page argument, so every page shares one catalogue");
    }

    /// <summary>
    /// The module-scoped read includes the product-wide module scope, which is the second arm of the union the
    /// terminal statement takes.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// An identifier that names no module is used on purpose. The first arm of the union then contributes
    /// nothing while the second still answers, so this isolates the arm that a naive port would have dropped -
    /// and proves the read is not silently narrowed for every module in the installation. It also pins that an
    /// unknown module is not an error here, exactly as the legacy statement behaved.
    /// </remarks>
    [Fact]
    public async Task GetModulePermissionDefinitions_IncludeTheProductWideModuleScope()
    {
        using HttpClient client = _fixture.CreateHostClient();

        using HttpResponseMessage response = await client.GetAsync(
            new Uri("/api/v1/permissions/modules/987654", UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        CollectionEnvelope<PermissionDto>? envelope = await response.Content
            .ReadFromJsonAsync<CollectionEnvelope<PermissionDto>>(ApiTestFixture.Json);

        envelope.Should().NotBeNull();
        envelope!.Data.Should().NotBeNull();
        envelope.Data!.Select(item => item.PermissionId).Should()
            .Contain(_fixture.Seed.ModuleViewPermissionId)
            .And.Contain(_fixture.Seed.ModuleEditPermissionId);
        envelope.Data!.Should().OnlyContain(
            item => item.PermissionCode == IntegrationSeed.ModulePermissionCode,
            "the page scope belongs to neither arm of this union");
    }

    /// <summary>Every read on this resource requires a bearer token.</summary>
    /// <param name="path">The address to attempt anonymously.</param>
    /// <returns>A task representing the test.</returns>
    [Theory]
    [InlineData("/api/v1/permissions")]
    [InlineData("/api/v1/permissions/1")]
    [InlineData("/api/v1/permissions/modules/1")]
    [InlineData("/api/v1/permissions/tabs/1")]
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
    [InlineData("/api/v1/permissions/modules/1")]
    [InlineData("/api/v1/permissions/tabs/1")]
    public async Task PermissionReads_AsMemberWithoutAdministratorRole_ReturnForbidden(string path)
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
        using HttpClient client = _fixture.CreateHostClient();

        using HttpRequestMessage request = new(new HttpMethod(method), new Uri(path, UriKind.Relative));
        using HttpResponseMessage response = await client.SendAsync(request);

        response.StatusCode.Should().BeOneOf(HttpStatusCode.NotFound, HttpStatusCode.MethodNotAllowed);
    }
}
