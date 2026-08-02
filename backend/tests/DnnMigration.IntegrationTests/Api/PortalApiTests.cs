using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using DnnMigration.Application.Dtos.Common;
using DnnMigration.Application.Dtos.Portal;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace DnnMigration.IntegrationTests.Api;

/// <summary>
/// Covers the portal resource end to end, across the real HTTP pipeline and the real database.
/// </summary>
/// <remarks>
/// <para>
/// The class name is fixed. Validation gate 5 names this suite explicitly and requires it to assert the four
/// documented status codes for the portal resource - <c>201 Created</c> on a create, <c>200 OK</c> on a read
/// and on an update, and <c>204 No Content</c> on a delete - so renaming it would fail the gate even though
/// nothing would fail to compile.
/// </para>
/// <para>
/// Two behaviours discovered while reading the service govern how these tests are written, and both are the
/// reason the suite creates its own portals instead of reusing the seeded one.
/// </para>
/// <para>
/// The first is that a delete is refused while only one portal remains, which is a deliberate rule rather
/// than an accident: an installation with no portal is unreachable. The seeded portal counts toward that
/// total, so the delete test creates a portal of its own and removes that, which both satisfies the rule and
/// leaves the shared fixture exactly as it found it.
/// </para>
/// <para>
/// The second is that the hosting charge, the three quotas, the site-log retention period and the expiry date
/// may only be changed by a host account. A portal administrator that submits a different value for any of
/// them is refused. An update test therefore echoes those six values back from the representation it just
/// read rather than inventing them, so that it exercises the update path instead of tripping the guard by
/// accident - and one test deliberately does trip it, to prove the guard is wired.
/// </para>
/// <para>
/// Every name that reaches a unique constraint carries a random suffix. The suites share one database, xUnit
/// makes no promise about the order of tests inside a collection, and the portal alias and the administrator
/// login name are both unique installation-wide, so a fixed literal would make the suite order-dependent.
/// </para>
/// </remarks>
[Trait("Category", "Integration")]
[Collection(IntegrationTestCollection.Name)]
public sealed class PortalApiTests
{
    /// <summary>The collection route, on which a create is expected to answer <c>201 Created</c>.</summary>
    private static readonly Uri PortalsRoute = new("/api/v1/portals", UriKind.Relative);

    /// <summary>
    /// A template name that satisfies the bare-file-name rule. The service does not open it - portal
    /// template parsing is outside this migration - but the request contract still requires a well formed
    /// name, so every create in this suite supplies one.
    /// </summary>
    private const string TemplateFileName = "admin.template";

    /// <summary>An identifier no seeded or created row can hold, used for the absent-resource paths.</summary>
    private const int UnknownPortalId = 987654;

    private readonly ApiTestFixture _fixture;

    /// <summary>Initialises a new instance of the <see cref="PortalApiTests"/> class.</summary>
    /// <param name="fixture">The shared host, database and seed.</param>
    public PortalApiTests(ApiTestFixture fixture) => _fixture = fixture;

    /// <summary>A read of the collection answers <c>200 OK</c> and includes the seeded portal.</summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task ListPortals_AsAdministrator_ReturnsOkContainingSeededPortal()
    {
        using HttpClient client = _fixture.CreateAdministratorClient();

        using HttpResponseMessage response = await client.GetAsync(
            new Uri("/api/v1/portals?pageIndex=0&pageSize=100", UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        PagedEnvelope<PortalListItemDto>? page = await response.Content
            .ReadFromJsonAsync<PagedEnvelope<PortalListItemDto>>(ApiTestFixture.Json);

        page.Should().NotBeNull();
        page!.PageIndex.Should().Be(0);
        page.PageSize.Should().Be(100);
        page.TotalCount.Should().BeGreaterThan(0);

        PortalListItemDto seeded = page.Items
            .Should().ContainSingle(item => item.PortalId == _fixture.Seed.PortalId)
            .Subject;

        seeded.PortalName.Should().Be(IntegrationSeed.PortalName);
        seeded.Aliases.Should().Contain(ApiTestFixture.TestHost);
    }

    /// <summary>
    /// The name filter narrows the collection. This proves the query reaches the repository rather than being
    /// silently dropped, which a filter that is bound but never applied would otherwise look identical to.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task ListPortals_WithNameFilterThatMatchesNothing_ReturnsEmptyPage()
    {
        using HttpClient client = _fixture.CreateAdministratorClient();

        using HttpResponseMessage response = await client.GetAsync(
            new Uri("/api/v1/portals?pageIndex=0&pageSize=50&name=zzz-no-portal-bears-this-name", UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        PagedEnvelope<PortalListItemDto>? page = await response.Content
            .ReadFromJsonAsync<PagedEnvelope<PortalListItemDto>>(ApiTestFixture.Json);

        page.Should().NotBeNull();
        page!.Items.Should().BeEmpty();
        page.TotalCount.Should().Be(0);
    }

    /// <summary>A page size beyond the permitted ceiling is rejected by the request validator.</summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task ListPortals_WithPageSizeAboveCeiling_ReturnsBadRequest()
    {
        using HttpClient client = _fixture.CreateAdministratorClient();

        using HttpResponseMessage response = await client.GetAsync(
            new Uri("/api/v1/portals?pageIndex=0&pageSize=100000", UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    /// <summary>Without a bearer token the resource answers <c>401 Unauthorized</c>.</summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task ListPortals_WithoutCredentials_ReturnsUnauthorized()
    {
        using HttpClient client = _fixture.CreateAnonymousClient();

        using HttpResponseMessage response = await client.GetAsync(PortalsRoute);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    /// <summary>
    /// An authenticated caller without the administrators role is refused. The distinction between this and
    /// the previous test matters: one proves authentication is required, the other proves the policy is
    /// actually attached to the resource rather than merely declared.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task ListPortals_AsPlainMember_ReturnsForbidden()
    {
        using HttpClient client = _fixture.CreateClientFor(
            _fixture.Seed.MemberUserId,
            IntegrationSeed.MemberUserName,
            _fixture.Seed.PortalId,
            isSuperUser: false,
            roles: [IntegrationSeed.RegisteredUsersRoleName]);

        using HttpResponseMessage response = await client.GetAsync(PortalsRoute);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    /// <summary>A read of the seeded portal answers <c>200 OK</c> and carries its aliases.</summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task GetPortal_ForSeededPortal_ReturnsOkWithDetail()
    {
        using HttpClient client = _fixture.CreateAdministratorClient();

        using HttpResponseMessage response = await client.GetAsync(PortalRoute(_fixture.Seed.PortalId));

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        PortalDetailDto? detail = await response.Content
            .ReadFromJsonAsync<PortalDetailDto>(ApiTestFixture.Json);

        detail.Should().NotBeNull();
        detail!.PortalId.Should().Be(_fixture.Seed.PortalId);
        detail.PortalName.Should().Be(IntegrationSeed.PortalName);
        detail.AdministratorId.Should().Be(_fixture.Seed.AdminUserId);
        detail.AdministratorRoleId.Should().Be(_fixture.Seed.AdministratorRoleId);
        detail.AdministratorRoleName.Should().Be(IntegrationSeed.AdministratorsRoleName);
        detail.RegisteredRoleId.Should().Be(_fixture.Seed.RegisteredRoleId);
        detail.RegisteredRoleName.Should().Be(IntegrationSeed.RegisteredUsersRoleName);
        detail.Guid.Should().NotBe(Guid.Empty);
        detail.Users.Should().BeGreaterThan(0);
        detail.Aliases.Should().NotBeNull();
        detail.Aliases!.Select(alias => alias.HttpAlias).Should().Contain(ApiTestFixture.TestHost);
    }

    /// <summary>An unknown identifier answers <c>404 Not Found</c> rather than an empty representation.</summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task GetPortal_WhenUnknown_ReturnsNotFound()
    {
        using HttpClient client = _fixture.CreateAdministratorClient();

        using HttpResponseMessage response = await client.GetAsync(PortalRoute(UnknownPortalId));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    /// <summary>An absent resource answers with a well formed RFC 7807 problem document.</summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// <para>
    /// The non-functional requirements mandate the problem-details envelope — <c>type</c>, <c>title</c>,
    /// <c>status</c>, <c>detail</c> and <c>errors</c> — and a status-code assertion alone cannot tell whether
    /// the body honours it. This fact reads the payload rather than the status so that an error response
    /// which regressed to a bare status, a raw string or a differently shaped object would fail here.
    /// </para>
    /// <para>
    /// The media type is deliberately NOT asserted. The framework answers <c>application/json</c> rather than
    /// RFC 7807's <c>application/problem+json</c> for a controller-produced problem document, which was
    /// measured directly against the running container. That deviation is recorded in the migration notes
    /// rather than pinned by a test, because asserting the value the framework currently emits would cement
    /// the deviation and make correcting it later look like a regression.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task GetPortal_WhenUnknown_ReturnsAWellFormedProblemDocument()
    {
        using HttpClient client = _fixture.CreateAdministratorClient();

        using HttpResponseMessage response = await client.GetAsync(PortalRoute(UnknownPortalId));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);

        ProblemDetails? problem = await response.Content
            .ReadFromJsonAsync<ProblemDetails>(ApiTestFixture.Json);

        problem.Should().NotBeNull();
        problem!.Status.Should().Be(StatusCodes.Status404NotFound,
            "the envelope must carry the status, not only the response line");
        problem.Title.Should().NotBeNullOrWhiteSpace();
        problem.Type.Should().NotBeNullOrWhiteSpace(
            "a client branches on the problem type rather than parsing prose");
    }

    /// <summary>A rejected request answers with a per-field RFC 7807 validation document.</summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// The validation path composes its body through the registered <c>ValidationProblemDetailsFactory</c>,
    /// which is a different path from the absent-resource fact above, so it needs its own guarantee. What
    /// makes a validation document distinct is the per-field <c>errors</c> object, and that is what this
    /// asserts: the offending field must be named, because an error response that says only "bad request"
    /// gives the caller nothing to correct.
    /// </remarks>
    [Fact]
    public async Task ListPortals_WithPageSizeAboveCeiling_NamesTheOffendingField()
    {
        using HttpClient client = _fixture.CreateAdministratorClient();

        using HttpResponseMessage response = await client.GetAsync(
            new Uri("/api/v1/portals?pageIndex=0&pageSize=100000", UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        ValidationProblemDetails? problem = await response.Content
            .ReadFromJsonAsync<ValidationProblemDetails>(ApiTestFixture.Json);

        problem.Should().NotBeNull();
        problem!.Status.Should().Be(StatusCodes.Status400BadRequest);
        problem.Errors.Should().ContainKey(
            nameof(PagedRequest.PageSize),
            "the envelope must attribute the failure to the field the caller sent");
    }

    /// <summary>The settings projection answers <c>200 OK</c> for a portal that exists.</summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task GetPortalSettings_ForSeededPortal_ReturnsOk()
    {
        using HttpClient client = _fixture.CreateAdministratorClient();

        using HttpResponseMessage response = await client.GetAsync(
            new Uri($"/api/v1/portals/{Route(_fixture.Seed.PortalId)}/settings", UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        PortalSettingsDto? settings = await response.Content
            .ReadFromJsonAsync<PortalSettingsDto>(ApiTestFixture.Json);

        settings.Should().NotBeNull();
        settings!.PortalId.Should().Be(_fixture.Seed.PortalId);
        settings.PortalName.Should().Be(IntegrationSeed.PortalName);
        settings.Guid.Should().NotBe(Guid.Empty);
    }

    /// <summary>The settings projection answers <c>404 Not Found</c> for a portal that does not exist.</summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task GetPortalSettings_WhenUnknown_ReturnsNotFound()
    {
        using HttpClient client = _fixture.CreateAdministratorClient();

        using HttpResponseMessage response = await client.GetAsync(
            new Uri($"/api/v1/portals/{Route(UnknownPortalId)}/settings", UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    /// <summary>
    /// A create answers <c>201 Created</c>, carries a location that resolves, and provisions the whole
    /// tenant: the alias that reaches it, the three stock roles and an administrator that can sign in.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task CreatePortal_ReturnsCreatedWithResolvableLocation()
    {
        using HttpClient client = _fixture.CreateHostClient();

        CreatePortalRequest request = NewPortalRequest();

        using HttpResponseMessage response = await client.PostAsJsonAsync(
            PortalsRoute,
            request,
            ApiTestFixture.Json);

        response.StatusCode.Should().Be(HttpStatusCode.Created);

        PortalDetailDto created = await ReadDetailAsync(response);
        created.PortalName.Should().Be(request.PortalName);
        created.Guid.Should().NotBe(Guid.Empty);
        created.AdministratorId.Should().NotBeNull();
        created.AdministratorRoleId.Should().NotBeNull();
        created.RegisteredRoleId.Should().NotBeNull();

        response.Headers.Location.Should().NotBeNull();
        response.Headers.Location!.OriginalString
            .Should().Be($"/api/v1/portals/{Route(created.PortalId)}");

        using HttpResponseMessage followed = await client.GetAsync(
            new Uri(response.Headers.Location.OriginalString, UriKind.Relative));

        followed.StatusCode.Should().Be(HttpStatusCode.OK);

        PortalDetailDto detail = await ReadDetailAsync(followed);
        detail.PortalId.Should().Be(created.PortalId);
        detail.Aliases.Should().NotBeNull();
        detail.Aliases!.Select(alias => alias.HttpAlias).Should().Contain(request.PortalAlias);

        // The three stock roles and the administrator account are part of what "a portal exists" means, so
        // the assertion goes to the database rather than stopping at the representation.
        int roleCount = await _fixture.Database.ScalarAsync<int>(
            "SELECT COUNT(*) FROM [dbo].[Roles] WHERE [PortalID] = @portalId;",
            new Dictionary<string, object?> { ["portalId"] = created.PortalId });

        roleCount.Should().Be(3);

        int membershipCount = await _fixture.Database.ScalarAsync<int>(
            "SELECT COUNT(*) FROM [dbo].[UserPortals] WHERE [PortalId] = @portalId;",
            new Dictionary<string, object?> { ["portalId"] = created.PortalId });

        membershipCount.Should().Be(1);

        int credentialCount = await _fixture.Database.ScalarAsync<int>(
            @"SELECT COUNT(*)
              FROM [dbo].[aspnet_Users] au
              INNER JOIN [dbo].[aspnet_Membership] am ON am.[UserId] = au.[UserId]
              WHERE au.[LoweredUserName] = LOWER(@userName);",
            new Dictionary<string, object?> { ["userName"] = request.AdministratorUsername });

        credentialCount.Should().Be(1);
    }

    /// <summary>
    /// A create without a template name is rejected by the request validator. The service never opens the
    /// template, but the contract still demands one, so this proves the validator is attached to the action.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task CreatePortal_WithoutTemplateFile_ReturnsBadRequest()
    {
        using HttpClient client = _fixture.CreateHostClient();

        CreatePortalRequest request = NewPortalRequest();
        request.TemplateFile = null;

        using HttpResponseMessage response = await client.PostAsJsonAsync(
            PortalsRoute,
            request,
            ApiTestFixture.Json);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        // The wording is asserted rather than the field name, because the message is the legacy string
        // reproduced verbatim and is therefore the part of the contract a caller actually reads.
        string body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("Please select a template file");
    }

    /// <summary>A template name that qualifies a path is rejected, because the service concatenates it.</summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task CreatePortal_WithPathQualifiedTemplateFile_ReturnsBadRequest()
    {
        using HttpClient client = _fixture.CreateHostClient();

        CreatePortalRequest request = NewPortalRequest();
        request.TemplateFile = "../escaped.template";

        using HttpResponseMessage response = await client.PostAsJsonAsync(
            PortalsRoute,
            request,
            ApiTestFixture.Json);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    /// <summary>An alias carrying a space is rejected, reproducing the legacy character rule.</summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task CreatePortal_WithSpaceInAlias_ReturnsBadRequest()
    {
        using HttpClient client = _fixture.CreateHostClient();

        CreatePortalRequest request = NewPortalRequest();
        request.PortalAlias = "not a valid alias";

        using HttpResponseMessage response = await client.PostAsJsonAsync(
            PortalsRoute,
            request,
            ApiTestFixture.Json);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    /// <summary>
    /// An alias already bound to another tenant answers <c>409 Conflict</c>. This is the one duplicate the
    /// service refuses before attempting a write, because a shared alias would make tenant resolution
    /// ambiguous.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task CreatePortal_WithAliasAlreadyBound_ReturnsConflict()
    {
        using HttpClient client = _fixture.CreateHostClient();

        CreatePortalRequest request = NewPortalRequest();
        request.PortalAlias = ApiTestFixture.TestHost;

        using HttpResponseMessage response = await client.PostAsJsonAsync(
            PortalsRoute,
            request,
            ApiTestFixture.Json);

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    /// <summary>
    /// An administrator login name already in use answers <c>409 Conflict</c> as well, and - importantly -
    /// leaves nothing behind: the alias the attempt would have claimed must still be free afterwards.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task CreatePortal_WithAdministratorNameAlreadyInUse_ReturnsConflictAndWritesNothing()
    {
        using HttpClient client = _fixture.CreateHostClient();

        CreatePortalRequest request = NewPortalRequest();
        request.AdministratorUsername = IntegrationSeed.AdminUserName;

        using HttpResponseMessage response = await client.PostAsJsonAsync(
            PortalsRoute,
            request,
            ApiTestFixture.Json);

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);

        int aliasCount = await _fixture.Database.ScalarAsync<int>(
            "SELECT COUNT(*) FROM [dbo].[PortalAlias] WHERE [HTTPAlias] = @alias;",
            new Dictionary<string, object?> { ["alias"] = request.PortalAlias });

        aliasCount.Should().Be(0);
    }

    /// <summary>
    /// An update answers <c>200 OK</c> and the new state survives a subsequent read. The six host-only
    /// values are echoed from the representation just read, so this exercises the update rather than the
    /// guard beside it.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task UpdatePortal_AsAdministrator_ReturnsOkAndPersists()
    {
        using HttpClient host = _fixture.CreateHostClient();
        PortalDetailDto created = await CreatePortalAsync(host);

        using HttpClient client = _fixture.CreateAdministratorClient();

        UpdatePortalRequest request = EchoHostOnlyFields(created);
        request.PortalName = "Renamed " + Suffix();
        request.FooterText = "Updated footer";
        request.Description = "Updated description";
        request.KeyWords = "updated, keywords";
        request.Currency = "GBP";
        request.DefaultLanguage = "en-GB";
        request.TimeZoneOffset = 0;

        using HttpResponseMessage response = await client.PutAsJsonAsync(
            PortalRoute(created.PortalId),
            request,
            ApiTestFixture.Json);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        PortalDetailDto updated = await ReadDetailAsync(response);
        updated.PortalId.Should().Be(created.PortalId);
        updated.PortalName.Should().Be(request.PortalName);
        updated.FooterText.Should().Be(request.FooterText);
        updated.Currency.Should().Be("GBP");

        using HttpResponseMessage reread = await client.GetAsync(PortalRoute(created.PortalId));
        reread.StatusCode.Should().Be(HttpStatusCode.OK);

        PortalDetailDto persisted = await ReadDetailAsync(reread);
        persisted.PortalName.Should().Be(request.PortalName);
        persisted.Description.Should().Be(request.Description);
        persisted.KeyWords.Should().Be(request.KeyWords);
        persisted.DefaultLanguage.Should().Be("en-GB");
    }

    /// <summary>
    /// A portal administrator that submits a different hosting charge is refused. The charge is a
    /// host-account concern: a tenant administrator that could raise or waive it would be setting the price
    /// of its own hosting.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task UpdatePortal_AsAdministratorAlteringHostingCharge_ReturnsForbidden()
    {
        using HttpClient host = _fixture.CreateHostClient();
        PortalDetailDto created = await CreatePortalAsync(host);

        using HttpClient client = _fixture.CreateAdministratorClient();

        UpdatePortalRequest request = EchoHostOnlyFields(created);
        request.PortalName = created.PortalName;
        request.HostFee = (created.HostFee ?? 0m) + 250.50m;

        using HttpResponseMessage response = await client.PutAsJsonAsync(
            PortalRoute(created.PortalId),
            request,
            ApiTestFixture.Json);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    /// <summary>A host account may change the hosting charge, which is the other half of the same rule.</summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task UpdatePortal_AsHostAlteringHostingCharge_ReturnsOk()
    {
        using HttpClient client = _fixture.CreateHostClient();
        PortalDetailDto created = await CreatePortalAsync(client);

        UpdatePortalRequest request = EchoHostOnlyFields(created);
        request.PortalName = created.PortalName;
        request.HostFee = 42.75m;
        request.HostSpace = 128;
        request.PageQuota = 25;
        request.UserQuota = 50;

        using HttpResponseMessage response = await client.PutAsJsonAsync(
            PortalRoute(created.PortalId),
            request,
            ApiTestFixture.Json);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        PortalDetailDto updated = await ReadDetailAsync(response);
        updated.HostFee.Should().Be(42.75m);
        updated.HostSpace.Should().Be(128);
        updated.PageQuota.Should().Be(25);
        updated.UserQuota.Should().Be(50);
    }

    /// <summary>An update against an unknown identifier answers <c>404 Not Found</c>.</summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task UpdatePortal_WhenUnknown_ReturnsNotFound()
    {
        using HttpClient client = _fixture.CreateHostClient();

        var request = new UpdatePortalRequest
        {
            PortalName = "No such portal",
        };

        using HttpResponseMessage response = await client.PutAsJsonAsync(
            PortalRoute(UnknownPortalId),
            request,
            ApiTestFixture.Json);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    /// <summary>An update carrying an out-of-range value is rejected by the request validator.</summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task UpdatePortal_WithNegativeQuota_ReturnsBadRequest()
    {
        using HttpClient client = _fixture.CreateHostClient();

        var request = new UpdatePortalRequest
        {
            PageQuota = -5,
        };

        using HttpResponseMessage response = await client.PutAsJsonAsync(
            PortalRoute(_fixture.Seed.PortalId),
            request,
            ApiTestFixture.Json);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    /// <summary>
    /// A delete answers <c>204 No Content</c>, the portal is then unreachable, and its alias is released so
    /// the host name can be bound again.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task DeletePortal_ReturnsNoContentAndReleasesAlias()
    {
        using HttpClient client = _fixture.CreateHostClient();
        PortalDetailDto created = await CreatePortalAsync(client);

        string alias = created.Aliases!.Single().HttpAlias!;

        using HttpResponseMessage response = await client.DeleteAsync(PortalRoute(created.PortalId));

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);

        using HttpResponseMessage reread = await client.GetAsync(PortalRoute(created.PortalId));
        reread.StatusCode.Should().Be(HttpStatusCode.NotFound);

        int aliasCount = await _fixture.Database.ScalarAsync<int>(
            "SELECT COUNT(*) FROM [dbo].[PortalAlias] WHERE [HTTPAlias] = @alias;",
            new Dictionary<string, object?> { ["alias"] = alias });

        aliasCount.Should().Be(0);
    }

    /// <summary>A delete against an unknown identifier answers <c>404 Not Found</c>.</summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task DeletePortal_WhenUnknown_ReturnsNotFound()
    {
        using HttpClient client = _fixture.CreateHostClient();

        using HttpResponseMessage response = await client.DeleteAsync(PortalRoute(UnknownPortalId));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    /// <summary>
    /// The alias sub-resource supports the full round trip: a create answers <c>201 Created</c>, an update
    /// and a delete each answer <c>204 No Content</c>, and the collection reflects each step.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task PortalAliases_SupportCreateUpdateAndDelete()
    {
        using HttpClient client = _fixture.CreateHostClient();
        PortalDetailDto created = await CreatePortalAsync(client);

        var aliasesRoute = new Uri(
            $"/api/v1/portals/{Route(created.PortalId)}/aliases",
            UriKind.Relative);

        string firstAlias = "alias-" + Suffix() + ".local";

        using HttpResponseMessage createdAlias = await client.PostAsJsonAsync(
            aliasesRoute,
            new PortalAliasDto { PortalId = created.PortalId, HttpAlias = firstAlias },
            ApiTestFixture.Json);

        createdAlias.StatusCode.Should().Be(HttpStatusCode.Created);

        PortalAliasDto? alias = await createdAlias.Content
            .ReadFromJsonAsync<PortalAliasDto>(ApiTestFixture.Json);

        alias.Should().NotBeNull();
        alias!.PortalAliasId.Should().BeGreaterThan(0);
        alias.PortalId.Should().Be(created.PortalId);
        alias.HttpAlias.Should().Be(firstAlias);

        using HttpResponseMessage listed = await client.GetAsync(aliasesRoute);
        listed.StatusCode.Should().Be(HttpStatusCode.OK);

        IReadOnlyList<PortalAliasDto>? all = await listed.Content
            .ReadFromJsonAsync<IReadOnlyList<PortalAliasDto>>(ApiTestFixture.Json);

        all.Should().NotBeNull();
        all!.Select(item => item.HttpAlias).Should().Contain(firstAlias);

        var aliasRoute = new Uri(
            $"/api/v1/portal-aliases/{Route(alias.PortalAliasId)}",
            UriKind.Relative);

        string secondAlias = "renamed-" + Suffix() + ".local";

        using HttpResponseMessage updated = await client.PutAsJsonAsync(
            aliasRoute,
            new PortalAliasDto
            {
                PortalAliasId = alias.PortalAliasId,
                PortalId = created.PortalId,
                HttpAlias = secondAlias,
            },
            ApiTestFixture.Json);

        updated.StatusCode.Should().Be(HttpStatusCode.NoContent);

        using HttpResponseMessage rereadAlias = await client.GetAsync(aliasRoute);
        rereadAlias.StatusCode.Should().Be(HttpStatusCode.OK);

        PortalAliasDto? persisted = await rereadAlias.Content
            .ReadFromJsonAsync<PortalAliasDto>(ApiTestFixture.Json);

        persisted.Should().NotBeNull();
        persisted!.HttpAlias.Should().Be(secondAlias);

        using HttpResponseMessage removed = await client.DeleteAsync(aliasRoute);
        removed.StatusCode.Should().Be(HttpStatusCode.NoContent);

        using HttpResponseMessage gone = await client.GetAsync(aliasRoute);
        gone.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    /// <summary>Binding an alias that another tenant already holds answers <c>409 Conflict</c>.</summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task AddPortalAlias_WhenAlreadyBound_ReturnsConflict()
    {
        using HttpClient client = _fixture.CreateHostClient();
        PortalDetailDto created = await CreatePortalAsync(client);

        using HttpResponseMessage response = await client.PostAsJsonAsync(
            new Uri($"/api/v1/portals/{Route(created.PortalId)}/aliases", UriKind.Relative),
            new PortalAliasDto { PortalId = created.PortalId, HttpAlias = ApiTestFixture.TestHost },
            ApiTestFixture.Json);

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    /// <summary>The unscoped alias collection includes the seeded host name.</summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task ListAllPortalAliases_ReturnsOkIncludingSeededHost()
    {
        using HttpClient client = _fixture.CreateHostClient();

        using HttpResponseMessage response = await client.GetAsync(
            new Uri("/api/v1/portal-aliases", UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        IReadOnlyList<PortalAliasDto>? all = await response.Content
            .ReadFromJsonAsync<IReadOnlyList<PortalAliasDto>>(ApiTestFixture.Json);

        all.Should().NotBeNull();
        all!.Select(item => item.HttpAlias).Should().Contain(ApiTestFixture.TestHost);
    }

    /// <summary>Creates a portal through the API and returns its representation.</summary>
    /// <param name="client">A client carrying host credentials.</param>
    /// <returns>The created portal.</returns>
    private async Task<PortalDetailDto> CreatePortalAsync(HttpClient client)
    {
        using HttpResponseMessage response = await client.PostAsJsonAsync(
            PortalsRoute,
            NewPortalRequest(),
            ApiTestFixture.Json);

        response.StatusCode.Should().Be(HttpStatusCode.Created);

        return await ReadDetailAsync(response);
    }

    /// <summary>
    /// Builds a create request whose every unique value carries a random suffix, so the suite is
    /// order-independent and repeatable against a database other tests are also writing to.
    /// </summary>
    /// <returns>A well formed create request.</returns>
    private static CreatePortalRequest NewPortalRequest()
    {
        string suffix = Suffix();

        return new CreatePortalRequest
        {
            PortalName = "Integration Created " + suffix,
            PortalAlias = "created-" + suffix + ".local",
            Description = "Created by the portal integration suite.",
            KeyWords = "integration, portal",
            HomeDirectory = string.Empty,
            TemplateFile = TemplateFileName,
            IsChildPortal = false,
            AdministratorFirstName = "Created",
            AdministratorLastName = "Administrator",
            AdministratorUsername = "created_admin_" + suffix,
            AdministratorPassword = ApiTestFixture.KnownPassword,
            AdministratorEmail = "created." + suffix + "@example.com",
        };
    }

    /// <summary>
    /// Builds an update request that repeats the six host-only values exactly as they were read, so that a
    /// caller without a host account is not refused for a change it did not make.
    /// </summary>
    /// <param name="detail">The representation just read.</param>
    /// <returns>An update request whose host-only fields are unchanged.</returns>
    private static UpdatePortalRequest EchoHostOnlyFields(PortalDetailDto detail) => new()
    {
        HostFee = detail.HostFee,
        HostSpace = detail.HostSpace,
        PageQuota = detail.PageQuota,
        UserQuota = detail.UserQuota,
        SiteLogHistory = detail.SiteLogHistory,
        ExpiryDate = detail.ExpiryDate,
        UserRegistration = detail.UserRegistration,
        BannerAdvertising = detail.BannerAdvertising,
        AdministratorId = detail.AdministratorId,
        HomeDirectory = detail.HomeDirectory,
    };

    /// <summary>Reads a portal representation out of a response, failing the test when it is absent.</summary>
    /// <param name="response">The response to read.</param>
    /// <returns>The representation.</returns>
    private static async Task<PortalDetailDto> ReadDetailAsync(HttpResponseMessage response)
    {
        PortalDetailDto? detail = await response.Content
            .ReadFromJsonAsync<PortalDetailDto>(ApiTestFixture.Json);

        detail.Should().NotBeNull();
        return detail!;
    }

    /// <summary>Builds the item route for a portal.</summary>
    /// <param name="portalId">The portal identifier.</param>
    /// <returns>A relative route.</returns>
    private static Uri PortalRoute(int portalId) =>
        new($"/api/v1/portals/{Route(portalId)}", UriKind.Relative);

    /// <summary>Formats an identifier for a route without picking up the ambient culture.</summary>
    /// <param name="value">The identifier.</param>
    /// <returns>The invariant representation.</returns>
    private static string Route(int value) => value.ToString(CultureInfo.InvariantCulture);

    /// <summary>Produces a short random suffix for values that reach a unique constraint.</summary>
    /// <returns>Twelve lower-case hexadecimal characters.</returns>
    /// <summary>
    /// The enumerations that have no legacy spelling travel as the integer discriminators their
    /// columns store, which is the form the Angular models consume.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This asserts the RAW response text rather than a deserialised object, because a typed
    /// round-trip cannot see this defect: the client and the server would simply agree with each
    /// other. The Angular models pin these as numeric literal unions - MODULE_VISIBILITY is
    /// {maximized:0, minimized:1, none:2}, USER_REGISTRATION_MODE is {none:0, private:1, public:2,
    /// verified:3} and BANNER_ADVERTISING_MODE is {none:0, site:1, host:2} - so a member NAME on the
    /// wire is a contract break even though every C# test would still pass.
    /// </para>
    /// <para>
    /// Registering a general string-enumeration converter is what would break it. Only the explicit
    /// per-type converters are registered, so BillingFrequency stays "M" while these stay numbers.
    /// </para>
    /// </remarks>
    /// <returns>A task representing the test.</returns>
    [Fact]
    [Trait("Category", "Integration")]
    public async Task NumericEnumerations_TravelAsTheirStoredIntegers()
    {
        using HttpClient client = _fixture.CreateHostClient();

        HttpResponseMessage response = await client.GetAsync(
            FormattableString.Invariant($"/api/v1/portals/{_fixture.Seed.PortalId}"));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        string body = await response.Content.ReadAsStringAsync();

        body.Should().MatchRegex("\"userRegistration\"\\s*:\\s*-?[0-9]+")
            .And.NotContainEquivalentOf("\"userRegistration\":\"");
        body.Should().MatchRegex("\"bannerAdvertising\"\\s*:\\s*-?[0-9]+")
            .And.NotContainEquivalentOf("\"bannerAdvertising\":\"");

        // The named spellings the framework's converter would have produced must be absent.
        body.Should().NotContain("NoRegistration")
            .And.NotContain("PrivateRegistration")
            .And.NotContain("PublicRegistration")
            .And.NotContain("VerifiedRegistration");
    }

    private static string Suffix() => Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture)[..12];
}
