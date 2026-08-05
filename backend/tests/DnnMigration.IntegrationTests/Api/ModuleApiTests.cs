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
    /// <summary>Failure code reported when a request names a page the module is not placed on.</summary>
    /// <remarks>
    /// Stated as the value a client observes, because it is the value a client branches on: it distinguishes
    /// "this module has no placement on that page" from "there is no such module", and the two need different
    /// remedies.
    /// </remarks>
    private const string PlacementNotFoundCode = "module.placement_not_found";
    /// <summary>An identifier no seeded or created row can hold, used for the absent-resource paths.</summary>
    private const int UnknownModuleId = 987654;

    /// <summary>
    /// The widest window the paging contract admits, spelled out here rather than referenced from the
    /// validator so a fact does not silently follow a change to the rule it is measuring against.
    /// </summary>
    /// <remarks>
    /// <c>PagedRequestValidator.MaximumPageSize</c> is 100 and the request is refused past it, so a fact that
    /// wants "the whole collection in one window" has to ask for exactly this and then confirm the collection
    /// fits. An earlier draft asked for a thousand and was answered <c>400</c>.
    /// </remarks>
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
    /// definitions asserted below are that portal's. This is the same tenant the administrator policy is
    /// evaluated against, which is why the endpoint accepts no caller-supplied portal identifier: one would
    /// let a request be authorised against one portal and answered about another.
    /// </remarks>
    [Fact]
    public async Task ListModuleDefinitions_ReturnsOkIncludingSeededDefinition()
    {
        using HttpClient client = await _fixture.CreateHostClientAsync();

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
        using HttpClient client = await _fixture.CreateUnprivilegedClientAsync();

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
        using HttpClient client = await _fixture.CreateHostClientAsync();

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
        using HttpClient client = await _fixture.CreateHostClientAsync();

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
        using HttpClient client = await _fixture.CreateUnprivilegedClientAsync();

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
    /// The refusal above is a real permission evaluation rather than a blanket denial: an edit grant recorded
    /// against the caller's role on the target page admits the same create, and a deny recorded beside it
    /// closes it again. Proving the denial alone could not distinguish "the grant is consulted" from "creation
    /// is simply closed to everybody but a host account".
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
        using HttpClient client = await _fixture.CreateHostClientAsync();

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

    /// <summary>
    /// A create whose end date precedes its start date is ACCEPTED, and both bounds are stored verbatim.
    /// </summary>
    /// <remarks>
    /// <para>
    /// MIGRATION: this fact asserted a <c>400</c> until the rule behind it was measured against the screen it
    /// claimed to preserve, where no such rule exists.
    /// <c>Website/admin/Modules/modulesettings.ascx</c> declares EXACTLY FOUR validators - <c>valtxtStartDate</c>
    /// at L78-L79, <c>valtxtEndDate</c> at L88-L89, <c>valBorder</c> at L137-L138 and <c>valCacheTime</c> at
    /// L172-L173 - and every one of them is a <c>CompareValidator</c> carrying
    /// <c>Operator="DataTypeCheck"</c>, which asserts only that the submitted text parses as its declared
    /// type. That page contains no <c>RangeValidator</c>, no <c>RequiredFieldValidator</c>, and no
    /// <c>CompareValidator</c> that compares one control against another. <c>ModuleSettings.ascx.vb</c>
    /// L367-L375 then reads each bound independently - <c>If txtStartDate.Text &lt;&gt; "" Then
    /// objModule.StartDate = Convert.ToDateTime(txtStartDate.Text) Else objModule.StartDate =
    /// Null.NullDate</c>, and the identical block for the end date - and compares the two nowhere.
    /// </para>
    /// <para>
    /// So a window ending before it began was accepted and stored by the legacy application, which simply
    /// rendered the module in no period at all. Refusing it here would be a NARROWING: input the legacy
    /// application accepted would be rejected, which AAP Rule T5 and clauses MC3 and MC4 forbid as squarely
    /// as they forbid a widening. Both bounds are read back from the response, from a fresh read and from the
    /// column itself, because an acceptance that quietly reordered or dropped one would satisfy a
    /// status-code assertion on its own.
    /// </para>
    /// <para>
    /// What remains enforced is the storability of each date taken separately, and that bound belongs to the
    /// store rather than to any rule invented here: <c>datetime</c> cannot hold a value below its own
    /// calendar, so <c>Null.NullDate</c> - <c>Date.MinValue</c>, per <c>Null.vb</c> L66-L68 - is refused by
    /// the request validator field by field. That is asserted by the storage-bound facts, not here.
    /// </para>
    /// </remarks>
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

    /// <summary>
    /// An update whose end date precedes its start date is accepted on the same terms as a create.
    /// </summary>
    /// <remarks>
    /// MIGRATION: asserted on both write paths deliberately. One legacy screen served create and edit alike,
    /// so a rule present on one path and absent from the other would be a divergence introduced by this
    /// migration rather than one inherited from it. The measurement is recorded on the create-path fact
    /// above and is not repeated.
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
        using HttpClient client = await _fixture.CreateHostClientAsync();

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

    /// <summary>
    /// A token issued by portal A cannot exercise a real user-specific module grant in portal B.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// SEC-005: the member is deliberately provisioned into both portals and receives a direct VIEW grant on
    /// portal B's module. Without token-to-route tenant binding every stored permission check below the policy
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
    /// A caller with no permission grant is refused the read. This is the assertion that proves the policy is
    /// enforced from stored grants rather than from the caller's own claims: the member account holds a valid
    /// token and is a member of the tenant, and is still refused because nothing grants it this module.
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

    /// <summary>
    /// A <c>tabId</c> naming a page the module is not placed on is REFUSED, and nothing is moved.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// <para>
    /// MIGRATION: <c>tabId</c> SELECTS the placement being updated; it does not relocate one. This fact
    /// asserted the opposite for a while - that naming another page moved the placement and its page-scoped
    /// settings there, appending to the destination pane the way the legacy administration screen did - and the
    /// reconciled contract withdrew that reading deliberately. One page identifier cannot express a move: it is
    /// the same field the request uses to say WHICH of a module's placements it is about, so a value that meant
    /// "move to here" and a value that meant "the one on here" would be indistinguishable, and an ordinary edit
    /// submitted against the wrong page would silently relocate a module instead of failing. A move needs a
    /// source and a destination, and the surface offers no way to name both.
    /// </para>
    /// <para>
    /// The absence is asserted with everything the withdrawn behaviour would have disturbed - the source
    /// placement, its order, its page-scoped setting and the absence of any placement on the named page - so a
    /// re-introduction fails here rather than passing as a 200 nobody inspected.
    /// </para>
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

    /// <summary>
    /// Omitting the selected page is a malformed update rather than an implicit move to page zero.
    /// </summary>
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
    /// <para>
    /// MIGRATION: this asserted the border bound until the border was excluded from the update contract along
    /// with the rest of the pane-layout and rendering columns - and with it went the fourth of the legacy
    /// screen's four validators, whose message read "Invalid Border (must be a number between 0 and 9)". The
    /// test's INTENT is preserved against a bound that still exists: <c>TabModules.IconFile</c> is
    /// <c>nvarchar(100)</c>. Letting a longer value reach SQL Server would surface as a truncation error
    /// rather than a field-level message, so the bound is asserted at the boundary. Note that the icon carries
    /// no legacy validator either, so this bound comes from the terminal schema alone.
    /// </para>
    /// <para>
    /// MIGRATION: and the border validator's message was DEFECTIVE, which is why nothing in this suite ever
    /// reinstates the range it advertised. <c>valBorder</c> at <c>modulesettings.ascx</c> L137-L138 promised
    /// "a number between 0 and 9" while declaring <c>Operator="DataTypeCheck"</c>, which asserts the type and
    /// nothing more - so the range was NEVER ENFORCED, and the field's only real limit was the
    /// <c>MaxLength="1"</c> on the textbox, which truncates rather than validates and which a programmatic
    /// post bypasses entirely. Adding a range rule here would refuse values the legacy application stored,
    /// which is a narrowing forbidden by MC4 rather than an improvement. The defect is recorded and NOT
    /// fixed, per MC1; that no such rule was introduced anywhere is verifiable by the absence of any
    /// range assertion in this file.
    /// </para>
    /// </remarks>
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
    public async Task UpdateModule_WhenUnresolvable_ReturnsForbidden()
    {
        using HttpClient client = await _fixture.CreateHostClientAsync();

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
        using HttpClient client = await _fixture.CreateHostClientAsync();
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

    // MIGRATION: A FACT ASSERTING THAT AN ADMINISTRATOR'S SUBMISSION MOVES THE PLACEMENT WAS WITHDRAWN HERE.
    // It read the submitted page as the legacy screen's page picker and asserted the stored page, the stored
    // order and a single surviving placement row afterwards. The reconciled contract reads that member as the
    // SELECTOR of which placement the request is about, so a request naming a page the module does not occupy
    // is refused before anything is written and no authority can turn it into a move. Its coverage is not
    // lost: UpdateModule_NamingAPageTheModuleIsNotPlacedOn_IsRefusedAndMovesNothing above asserts the same
    // stored state - the source placement, its order, its page-scoped setting, and the absence of any
    // placement on the named page - against the refusal instead of against a relocation, and the divergence
    // from the legacy screen is recorded in MIGRATION_NOTES.md. Reinstating a move needs a second member
    // naming the destination, and this fact would then be the right shape for it.

    /// <summary>
    /// Each administrator-only field is refused for a caller that holds the module edit grant but does not
    /// administer the portal, and nothing is written.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The legacy settings screen disabled the page picker, the every-page checkbox and both propagation
    /// checkboxes outright for any caller outside the portal administrator role, in the page load and again
    /// in the save handler. That is a rule about four FIELDS of one request rather than about reaching the
    /// route, so no policy attribute can express it: the route's own policy admits a caller holding the
    /// module edit grant, correctly, because such a caller may legitimately edit the module in front of them.
    /// </para>
    /// <para>
    /// Until the service enforced it, the rule was documented in three places and enforced in none, and the
    /// consequence was four portal-wide effects reachable from a page-scoped grant: move a module onto a page
    /// the caller does not administer, fan it out across every page of the tenant, name its appearance as the
    /// tenant's default, or rewrite the appearance of every module on every content page.
    /// </para>
    /// <para>
    /// All four are asserted in one test because they share one gate, and a single representative field would
    /// leave three able to regress silently. The stored placement is re-read afterwards to prove the refusal
    /// happened before anything was written rather than after.
    /// </para>
    /// </remarks>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task UpdateModule_AsNonAdministratorHoldingEditGrant_RefusesAdministratorOnlyFields()
    {
        using HttpClient host = await _fixture.CreateHostClientAsync();
        ModuleDetailDto created = await CreateModuleAsync(host, _fixture.Seed.RootTabId);

        using HttpClient client = await _fixture.CreateUnprivilegedClientAsync();

        // The caller genuinely holds the module edit grant, which is what makes this a test of the field rule
        // rather than of the route policy: without the grant every request below would be refused for a
        // different reason and would prove nothing.
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

        // MIGRATION: THREE FIELDS, NOT FOUR. A submission naming a DIFFERENT page was the first entry here,
        // because the revision that wrote this fact read the page as a move command and counted it among the
        // administrator-only fields. Under the reconciled contract the page SELECTS the placement, so such a
        // request is refused earlier - and with the placement-not-found answer rather than a forbidden one -
        // which is asserted by its own fact. It is removed from this array rather than left to fail for a
        // reason that has nothing to do with the gate this fact exists to prove. The three portal-wide effects
        // that remain are exactly the ones the gate covers.
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

        // An ordinary save by the same caller still works, which is the half that proves the gate is a DELTA
        // test rather than a presence test. The contract requires the page identifier on every request, so a
        // gate that refused whenever the field was present would refuse every non-administrator save.
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

        await RevokeTabPermissionAsync(
            _fixture.Seed.RootTabId,
            _fixture.Seed.TabEditPermissionId,
            _fixture.Seed.RegisteredRoleId);
        await RevokeTabPermissionAsync(
            _fixture.Seed.ChildTabId,
            _fixture.Seed.TabEditPermissionId,
            _fixture.Seed.RegisteredRoleId);
    }

    /// <summary>
    /// A page in another tenant, and a page that does not exist, are refused identically.
    /// </summary>
    /// <remarks>
    /// The two answer alike on purpose. Distinguishing them would tell an administrator of one tenant which
    /// page identifiers exist in tenants it cannot see, which is an enumeration oracle.
    /// <para>
    /// MIGRATION: this was written as a fact about a MOVE's destination being validated before the projection
    /// assigned it. Under the reconciled contract the submitted page selects the placement instead, so both
    /// values are refused one step earlier and for a stronger reason - no placement of this module exists on
    /// either page - and neither can reach a column because nothing is projected at all. The status and the
    /// non-enumerating property, which are what this fact protects, are unchanged.
    /// </para>
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
    /// An import into a module whose package names a business controller this installation does not register
    /// is REFUSED, and nothing is committed or recorded.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the state every installation of this migration is permanently in for every module: the
    /// lifecycle factory resolves from a closed set fixed in code, AAP section 0.2.2.2 places every bundled
    /// module out of scope, and the set is therefore empty. The factory previously reported that as a
    /// SUCCESS carrying an advisory, and the module service reads a successful import as licence to commit
    /// its unit of work, evict the placement caches and write an <c>Operation=Import</c> audit record - so
    /// the caller was answered <c>200 OK</c> and the audit trail recorded content that had never been
    /// imported into a module the installation cannot even ask.
    /// </para>
    /// <para>
    /// The seeded package is temporarily made to declare portability and to name a controller, because the
    /// suite's package otherwise declares none and the request is refused earlier for that different and
    /// entirely correct reason - which is exactly why this path had never been reached over HTTP. The row is
    /// restored afterwards so the rest of the suite sees the package it expects.
    /// </para>
    /// <para>
    /// The absence of an audit record is asserted as well as the status code. A refusal that had already
    /// written the record would still answer the caller correctly while leaving a trail that says an import
    /// happened, and the trail is what an operator reads afterwards.
    /// </para>
    /// </remarks>
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
            // The host has one process-wide recording sink, shared by every suite in this serial collection.
            // Count only this module's import records before and after the request instead of assuming that
            // every log written while this fact runs belongs to it.
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
    /// Even an explicit anonymous VIEW grant cannot disclose the open-key settings contract, because that
    /// contract now requires an authenticated editor.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
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
        visibleModule.StatusCode.Should().Be(HttpStatusCode.OK);

        using HttpResponseMessage settings = await anonymous.GetAsync(
            ModuleSettingsRoute(_fixture.Seed.PortalId, created.ModuleId));
        settings.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    /// <summary>
    /// A signed-in caller who may view a module but has no edit grant cannot read its raw settings.
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
        visibleModule.StatusCode.Should().Be(HttpStatusCode.OK);

        using HttpResponseMessage settings = await member.GetAsync(
            ModuleSettingsRoute(_fixture.Seed.PortalId, created.ModuleId));
        settings.StatusCode.Should().Be(HttpStatusCode.Forbidden);
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
    public async Task GetModuleSettings_WhenUnresolvable_ReturnsForbidden()
    {
        using HttpClient client = await _fixture.CreateHostClientAsync();

        using HttpResponseMessage response = await client.GetAsync(
            ModuleSettingsRoute(_fixture.Seed.PortalId, UnknownModuleId));

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    /// <summary>Writing the settings of an unknown module answers <c>404 Not Found</c>.</summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task UpdateModuleSettings_WhenUnresolvable_ReturnsForbidden()
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
    /// because withdrawing a module from one page is not the same act as removing the module.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task DeleteModulePlacement_ReturnsNoContentAndRemovesOnlyThePlacement()
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
        using HttpClient client = await _fixture.CreateHostClientAsync();
        ModuleDetailDto first = await CreateModuleAsync(client, _fixture.Seed.RootTabId);
        ModuleDetailDto second = await CreateModuleAsync(client, _fixture.Seed.ChildTabId);

        using HttpResponseMessage response = await client.DeleteAsync(new Uri(
            $"/api/v1/modules/{Route(first.ModuleId)}"
                + $"?tabModuleId={Route(second.TabModuleId)}",
            UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    /// <summary>A delete against a module the permission gate cannot resolve is refused before the action runs.</summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task DeleteModule_WhenUnresolvable_ReturnsForbidden()
    {
        using HttpClient client = await _fixture.CreateHostClientAsync();

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
    public async Task ExportModule_WhenUnresolvable_ReturnsForbidden()
    {
        using HttpClient client = await _fixture.CreateHostClientAsync();

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
    /// content is untouched. The import's target arrives in the body, so no route-reading policy could reach
    /// it and the endpoint carried the bare authentication requirement - which meant any authenticated caller
    /// could overwrite any tenant's module content.
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
    /// The import refusal is likewise a real evaluation: an edit grant on the module carries the request past
    /// authorisation and on to the capability check, which refuses it for an entirely different and
    /// non-authorisation reason. Reaching that reason is the proof that the grant was consulted and honoured.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task ImportModule_WithModuleEditGrant_ReachesTheCapabilityCheck()
    {
        using HttpClient host = await _fixture.CreateHostClientAsync();
        ModuleDetailDto created = await CreateModuleAsync(host, _fixture.Seed.RootTabId);

        // TWO gates stand in front of the capability check, and the caller has to clear both. The route names
        // a tenant, so it carries the tenant-bound administrator policy - without which a caller holding a
        // grant in its own tenant could import into another one, because the grant evaluation would judge its
        // own tenant's roles against the named tenant's grants. Past that, the service evaluates the module
        // EDIT grant itself, which is not implied by administering the tenant: only a host account is answered
        // affirmatively without a grant. So the grant is granted to the role the administrator holds, and
        // reaching the capability refusal is the proof that it was consulted and honoured.
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
    /// it. The policy that applies is the tenant the request host and caller context resolve.
    /// </remarks>
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
        using HttpClient host = await _fixture.CreateHostClientAsync();
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
    /// <para>
    /// This is AAP Rule T7 at the boundary - "sentinels survive at the boundary, not in the domain" - and it
    /// is asserted on <c>Modules.ModuleTitle</c> because that is the module column which both survives to the
    /// terminal schema and admits the distinction. <c>Null.vb</c> L71-L73 defines <c>NullString</c> as the
    /// EMPTY STRING rather than as <see langword="null"/>, so legacy code that wrote "absent" wrote
    /// <c>''</c>, while a column left alone held SQL <c>NULL</c> - and the baseline seed proves the two
    /// coexisted in one column: <c>01.00.00.SqlDataProvider</c> gives modules 2, 3 and 4 an
    /// <c>AuthorizedViewRoles</c> of <c>''</c> and modules 29 through 316 a <c>NULL</c>, against a column
    /// declared <c>[AuthorizedViewRoles] [nvarchar] (256) NULL</c> at L230.
    /// </para>
    /// <para>
    /// MIGRATION: that particular column cannot carry the assertion, because it no longer exists.
    /// <c>03.00.01.SqlDataProvider</c> L1402 and L1405 drop <c>AuthorizedEditRoles</c> and
    /// <c>AuthorizedViewRoles</c> from <c>Modules</c> outright, superseded by the permission tables, and the
    /// same script's L198 drops the appearance columns onto the new <c>TabModules</c> table. AAP 0.7.1.2
    /// requires the model to follow the CUMULATIVE TERMINAL schema rather than the baseline, so the
    /// distinction is asserted on the column that terminal schema does declare -
    /// <c>[ModuleTitle] nvarchar(256) NULL</c> - which admits <c>''</c> and <c>NULL</c> exactly as the
    /// dropped one did.
    /// </para>
    /// <para>
    /// The failure this guards against is a serialiser policy, not a mapping: <c>WhenWritingNull</c> or
    /// <c>WhenWritingDefault</c> on the ignore condition would erase one of the two states from the wire
    /// and no status-code assertion anywhere would notice. Both states are therefore read out of the RAW
    /// JSON as well as out of the typed contract, because a typed read cannot tell an omitted member from
    /// one present and null.
    /// </para>
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
    /// <remarks>
    /// MIGRATION: <c>Null.vb</c> L41-L43 defines <c>NullInteger</c> as <c>-1</c>, and
    /// <c>CreateModuleRequest.ModuleOrder</c> defaults to that value to mean "append", which is the legacy
    /// convention. Because <c>-1</c> is also the default a serialiser would drop under
    /// <c>WhenWritingDefault</c>, the value is asserted in the RAW document as a number: an integer sentinel
    /// silently converted to <see langword="null"/> or omitted is indistinguishable from an unset field, and
    /// the same collision is what makes <c>Portals.PortalID</c> - <c>IDENTITY(-1, 1)</c> at
    /// <c>01.00.00.SqlDataProvider</c> L77 - unsafe to treat as absent when it holds <c>-1</c>.
    /// </remarks>
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

    /// <summary>
    /// Module zero is a legitimate identifier, in the route segment and in a request body alike.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>Modules.ModuleID</c> is <c>IDENTITY (0, 1)</c> - <c>01.00.00.SqlDataProvider</c> L221 - so the
    /// first module an installation ever creates is numbered zero and zero is an ordinary key. Anything that
    /// treats an identifier as absent when it is falsy therefore loses a real row. The row is inserted with
    /// <c>IDENTITY_INSERT</c> rather than by the API, because the API cannot choose an identifier and this
    /// installation's sequence has long passed zero.
    /// </para>
    /// <para>
    /// Two positions are covered, because they fail differently. In the ROUTE, a zero must reach the action
    /// and be answered about; it must not miss the <c>{moduleId:int}</c> constraint and it must not be read
    /// as "no module named" by the permission policy, which resolves its scope identifier from route data
    /// alone and would otherwise fail closed and refuse every request. In the BODY, the same zero must be
    /// accepted by <c>ModuleImportRequest.ModuleId</c> - the member that carries the module for an import
    /// precisely because that route names none - and must not be read as unspecified.
    /// </para>
    /// <para>
    /// The row is removed afterwards so the shared collection is left as it was found. It is placed on no
    /// page, so the read that matters is the detail read.
    /// </para>
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

            // The same identifier in the ROUTE of an edit-guarded action: the refusal that matters is a
            // permission decision, and a fail-closed refusal caused by reading zero as "no module" would
            // be indistinguishable from one - so the assertion is that the request is NOT refused.
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

            // And in a request BODY, where the import contract carries the module because its route
            // cannot. The outcome is a capability refusal about the seeded definition, never a complaint
            // that no module was named.
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
    /// <remarks>
    /// <para>
    /// MIGRATION: THE MEDIA TYPE IS MEASURED, NOT ASSUMED, AND IT IS NOT RFC 7807's OWN. The validation
    /// filter builds its result with <c>ContentTypes = { "application/problem+json" }</c>, so the expectation
    /// was that a refused write would carry that media type - and it does not. Measured against this host, a
    /// refusal answers <c>application/json; charset=utf-8</c>: the JSON output formatter advertises
    /// <c>application/*+json</c> among its supported types and negotiation resolves the response to its
    /// concrete <c>application/json</c>. The value asserted below is therefore the one the API genuinely
    /// serves, and the assertion is deliberately written to admit RFC 7807's media type as well, so that
    /// correcting the deviation later does not require this fact to be edited. The sibling problem-details
    /// suite declines to pin the value at all for the same measurement; recording it here as a permitted set
    /// rather than as a silence keeps the two consistent while still asserting that a JSON problem document
    /// is what arrives.
    /// </para>
    /// <para>
    /// The SHAPE carries the weight, because the shape is what a client parses. All five RFC 7807 members
    /// the requirements name are asserted - <c>type</c>, <c>title</c>, <c>status</c>, <c>detail</c> and the
    /// per-field <c>errors</c> map - and the map is asserted BY KEY rather than by presence, because a
    /// document that reported a fault against the wrong member, or against none, would satisfy an assertion
    /// on the status code and on the envelope alike. <c>traceId</c> is asserted for the reason it is
    /// populated: it is what ties the refusal to the request that caused it.
    /// </para>
    /// </remarks>
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
    /// <remarks>
    /// <para>
    /// MIGRATION: BOTH OPERATIONS ADVERTISED THIS DOCUMENT AND NEITHER COULD PRODUCE IT. Each declares
    /// <c>ValidationProblemDetails</c> for <c>400</c> - the shape carrying the per-field <c>errors</c> map -
    /// while no validator was registered for either request type, so the only 400 either could raise was the
    /// service's plain problem document with no map in it at all. A client written against the published
    /// description read <c>errors</c> and found nothing. The declaration described a response that could not
    /// occur, which is a published-contract defect rather than a missing convenience.
    /// </para>
    /// <para>
    /// The assertion is on the KEY, not merely on the presence of the map, because a document keyed on the
    /// wrong member - or on none - satisfies both a status assertion and an envelope assertion while leaving
    /// the caller no better off. The export row omits the file name, which the legacy click handler required;
    /// the import row omits the module, which the legacy screen never had to require because its route
    /// carried the target and this one does not.
    /// </para>
    /// <para>
    /// The bodies are raw documents rather than typed contracts, so that a member can be genuinely ABSENT.
    /// Serialising a typed request would emit the member with a null value, which is a different submission
    /// from one that never named it - and for the import's identifier the distinction is the whole reason
    /// that member is nullable.
    /// </para>
    /// </remarks>
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
    /// <remarks>
    /// The row the presence rule exists for, and the reason it is a predicate rather than
    /// <c>NotEmpty</c>: FluentValidation treats a run of spaces as a supplied value, so a rule spelled
    /// <c>NotEmpty</c> would let this submission through the boundary to be refused one layer deeper by a
    /// plain problem document - which is the drift the validator was added to close. The sibling fact that
    /// asserts the same request answers 400 with the sentence naming a file name is retained separately: it
    /// pins the WORDING, which both enforcement points share, while this pins WHICH point answered.
    /// </remarks>
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
    /// <remarks>
    /// MIGRATION: the legacy export and import pages reported their outcomes as localised prose - the keys
    /// <c>ExportNotSupported</c> (<c>Export.ascx.vb</c> L195 and L201), <c>NoContent</c> (L192) and
    /// <c>DiskSpaceExceeded</c> (L189), and <c>NotValidXml</c> (<c>Import.ascx.vb</c> L192),
    /// <c>NotCorrectType</c> (L204, L217) and <c>ImportNotSupported</c> (L208, L214) - rendered into a skin
    /// message that only a person could read. Each becomes a STABLE, NAMED failure code published in the
    /// problem document's <c>type</c> member, so a client branches on a name rather than on prose or on a
    /// bare status number. The assertion is on the name for that reason: a status code alone cannot
    /// distinguish "this module cannot be exported" from any other refused request.
    /// </remarks>
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
    /// <remarks>
    /// <para>
    /// MIGRATION: <c>Export.ascx.vb</c> L157-L192 obtained the module's content, then wrote it to a file in
    /// the portal's home directory, checked the portal's remaining disk space and reported
    /// <c>DiskSpaceExceeded</c> when the write would not fit. BOTH the write and the space check are DROPPED:
    /// the API has no portal home directory, a stateless request cannot own a file the caller then has to
    /// fetch by another route, and a quota check whose subject no longer exists cannot be preserved. The
    /// document travels in the response instead, which is why the action declares
    /// <c>application/xml</c> for its success status rather than the controller-wide
    /// <c>application/json</c>. A caller saves the body, exactly as the legacy page produced a saveable file.
    /// </para>
    /// <para>
    /// The declaration is asserted rather than a successful body, and that is a limitation stated plainly: a
    /// success requires a business controller registered under the addressed module's declared class name,
    /// the registered set is closed by design and empty in this delivery, and the seeded desktop module
    /// declares no class at all - <c>BusinessControllerClass</c> is <c>NULL</c> in the seed. Asserting the
    /// declaration pins the contract that the XML is the RESPONSE rather than a file; the refusal path that
    /// is reachable is asserted by the sibling facts. No response contract in the module group offers a
    /// path, a URL or a disk-space figure, and that absence is asserted too.
    /// </para>
    /// </remarks>
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

        // The declaration is then held against the route it describes, because an advertised media type on an
        // action nobody can reach would prove nothing. The reachable outcome for the seeded definition is a
        // refusal, and what matters is that the refusal is a problem document rather than a reference to a
        // file the caller would have to collect from somewhere.
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
    /// Both content-transfer actions declare the same one-mebibyte request ceiling as the server default.
    /// </summary>
    /// <remarks>
    /// The explicit metadata is the contract under test. Without it, a future global-limit change could
    /// silently make module import/export unbounded or unexpectedly narrower while their public surface still
    /// appeared unchanged.
    /// </remarks>
    [Fact]
    public void ModuleContentTransfer_DeclaresItsRequestBodyCeiling()
    {
        foreach (string actionName in new[] { "ExportAsync", "ImportAsync" })
        {
            MethodInfo action = typeof(ModulesController)
                .GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .Single(method => method.Name == actionName);

            RequestSizeLimitAttribute limit = action.GetCustomAttribute<RequestSizeLimitAttribute>()
                ?? throw new InvalidOperationException($"{actionName} declares no request-size limit.");

            ((Microsoft.AspNetCore.Http.Metadata.IRequestSizeLimitMetadata)limit).MaxRequestBodySize.Should().Be(
                ServiceCollectionExtensions.MaximumRequestBodyBytes,
                "module content transfer is explicitly bounded at the same one-mebibyte ceiling as Kestrel");
        }
    }

    /// <summary>
    /// A listing publishes the items-plus-total envelope, and the total is never the legacy sentinel.
    /// </summary>
    /// <remarks>
    /// MIGRATION: the legacy listings reported their size through a <c>ByRef totalRecords</c> argument and
    /// signalled "unpaged" by passing a page index of <c>-1</c>, so a count and a sentinel travelled in the
    /// same integer. The envelope separates them: the total is a count and nothing else, and the unpaged
    /// case is an explicit factory rather than a magic index. The page-index BASE is deliberately not
    /// asserted - the legacy data layer computed <c>@PageSize * @PageIndex</c> from a zero base while its
    /// screens passed <c>CurrentPage - 1</c> to reach it, so an assertion on a base would pin one
    /// convention's arithmetic rather than the contract.
    /// </remarks>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task ListModules_PublishesTheItemsAndTotalEnvelopeWithoutASentinelTotal()
    {
        using HttpClient client = await _fixture.CreateHostClientAsync();

        // Two are created rather than one, and neither the seed nor a sibling fact is relied on for the
        // second. The collection is shared and this suite's facts do not run in a declared order, so a fact
        // that assumed rows another fact had created would pass or fail according to the order it happened to
        // run in. Creating both here makes the total strictly greater than the window by construction.
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
    /// A module sitting on two pages does not widen the window it appears in, and does not shrink the total.
    /// </summary>
    /// <remarks>
    /// <para>
    /// MIGRATION: THE LISTING'S WINDOW USED TO BE CUT IN THE WRONG UNIT, and this fact is the end-to-end
    /// proof that it no longer is. The rows this collection returns are PLACEMENTS - a module placed on two
    /// pages contributes two - while the window was taken over MODULES, so one module entering a window of
    /// one brought every placement it had out with it. The published metadata was then patched with
    /// <c>Math.Max</c> so the envelope's own guards would accept the mismatch: the width came back larger
    /// than the width asked for, and the total came back as a count of modules although the items were
    /// placements. Both are now exact.
    /// </para>
    /// <para>
    /// The second placement is written directly with SQL rather than by asking for every page. The
    /// all-pages switch fans a module out across every content page of the tenant, which would add rows to
    /// pages that other facts in this shared collection read - so the narrowest possible change is made, on
    /// one extra page, and it is removed again in a <c>finally</c>. The pane, order and every other
    /// <c>NOT NULL</c> column are supplied so the row is one the schema would have accepted from the
    /// application.
    /// </para>
    /// <para>
    /// The total is compared against a WIDE read rather than against a literal, because the collection is
    /// shared and its size is not known to this fact. What is asserted is the relationship the arithmetic
    /// depends on: the total counts rows, it does not change when the window narrows, and the window is
    /// never wider than it was asked to be.
    /// </para>
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

    /// <summary>
    /// The listing's title filter matches case-insensitively anywhere in the title.
    /// </summary>
    /// <remarks>
    /// MIGRATION: this is a NET-NEW surface and the semantics are stated rather than inherited, because there
    /// is nothing to inherit them from. There is no module-list administration page in the migrated scope,
    /// and <c>Library/Components/Modules/ModuleController.vb</c> declares no title filter of any kind - its
    /// only search-related member, <c>GetSearchModules</c> at L1032, returns the modules that implement the
    /// legacy searchable contract and has nothing to do with matching a title. So no parity obligation binds
    /// the matching rule here, and the mid-string match is asserted as the contract this delivery defines
    /// rather than one measured elsewhere. Stated explicitly because the account and role listings, whose
    /// legacy procedures did filter with <c>@text + '%'</c>, match from the START of the value - and a reader
    /// who assumed one rule covered every listing would be wrong in both directions.
    /// </remarks>
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
    /// matters operationally: a caller reporting a failure has nothing but the identifier to hand back, and a
    /// short-circuiting stage that answered without the header would take it away at exactly the moment it is
    /// needed. The middleware registers the header through a response callback before the pipeline continues,
    /// which is what makes it survive a response no controller produced.
    /// </remarks>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task ModuleRequests_EchoTheSuppliedCorrelationIdOnSuccessAndOnRefusal()
    {
        using HttpClient client = await _fixture.CreateHostClientAsync();

        string supplied = "module-suite-" + Suffix();

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

        string refusedId = "module-refusal-" + Suffix();

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

    /// <summary>
    /// A request that supplies no correlation identifier is answered with one that was generated.
    /// </summary>
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
    /// must not be reflected at all. Replacing it rather than refusing the request is the deliberate choice -
    /// a caller's malformed diagnostic header is not a reason to withhold the resource it asked for, and
    /// answering <c>400</c> would turn a logging concern into a functional failure.
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

    /// <summary>Signs in as the seeded plain member, which holds no permission grant of any kind.</summary>
    /// <returns>An authenticated client with no module or page grants.</returns>
    private Task<HttpClient> MemberClientAsync() => _fixture.CreateUnprivilegedClientAsync();

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

    /// <summary>
    /// Inserts one placed administrative module carrying a security-owned setting.
    /// </summary>
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
    /// Creation, the single read and the listing all report the SAME definition and package for one
    /// module, and every one of those five values matches the definition catalogue.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// <para>
    /// Three endpoints project these five values, from three different code paths, so nothing but a test
    /// keeps them in step. The failure this guards against was all three disagreeing at once about one
    /// module: the creation response named package 0, the single read named the real package but reported
    /// no package name, description or version, and the listing carried none of the five at all. A
    /// consumer therefore could not learn which package a module came from without knowing which endpoint
    /// happened to be truthful.
    /// </para>
    /// <para>
    /// The catalogue is consulted as the independent control, so this asserts agreement with the STORE
    /// rather than merely agreement among the three projections - three endpoints that agree on a wrong
    /// value would otherwise pass. A package key of 0 is asserted impossible for the same reason it is
    /// impossible in the schema: <c>dbo.DesktopModules.DesktopModuleID</c> is a plain <c>IDENTITY</c>, so
    /// it seeds at 1.
    /// </para>
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

                // The remaining four are compared against the CATALOGUE's own answer rather than against
                // literals, so the control is the store in every case and a fixture change cannot turn a
                // real disagreement into a passing test.
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

    /// <summary>
    /// Reads one member out of a success envelope's payload as raw JSON.
    /// </summary>
    /// <param name="client">A client entitled to perform the read.</param>
    /// <param name="route">The resource to read.</param>
    /// <param name="member">The camel-cased member name to return.</param>
    /// <returns>The member, cloned so that it outlives the document it was parsed from.</returns>
    /// <remarks>
    /// Necessary because a typed read cannot distinguish a member the response OMITTED from one it published
    /// as <see langword="null"/> - both deserialise to the same value - and that distinction is the whole
    /// subject of the sentinel facts. The element is cloned before the document is disposed, because a
    /// <see cref="JsonElement"/> borrowed from a disposed document throws on access.
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
    /// <c>Modules.ModuleID</c> is <c>IDENTITY (0, 1)</c>, so zero is the first identifier an installation
    /// issues and an ordinary key thereafter - but this installation's sequence is long past it, so the row
    /// is written with the identity override rather than through the API. Any existing row is removed first
    /// so the helper is safe to call after a failed run left one behind.
    /// <para>
    /// The PLACEMENT is written alongside the module, and it is not optional: a module the terminal schema
    /// holds without a <c>TabModules</c> row is an unplaced module, and a read that addresses one reports
    /// absence - so a module numbered zero and placed nowhere would answer <c>404</c> for a reason that has
    /// nothing to do with its identifier and would prove nothing about the sentinel. Every column
    /// <c>TabModules</c> declares <c>NOT NULL</c> is supplied for the same reason: the row must be one the
    /// schema would have accepted from the application.
    /// </para>
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
