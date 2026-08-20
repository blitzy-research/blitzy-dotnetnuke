using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using DnnMigration.Api.ErrorHandling;
using DnnMigration.Application.Dtos.Auth;
using DnnMigration.Application.Dtos.Common;
using DnnMigration.Application.Dtos.Portal;
using FluentAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using Xunit;

namespace DnnMigration.IntegrationTests.Api;

/// <summary>Covers the portal resource end to end, across the real HTTP pipeline and the real database.</summary>
/// <remarks>
/// <para>
/// The first is that a delete is refused while only one portal remains, which is a deliberate rule rather
/// than an accident: an installation with no portal is unreachable. The seeded portal counts toward that
/// total, so the delete test creates a portal of its own and removes that, which both satisfies the rule
/// and leaves the shared fixture exactly as it found it.
/// </para>
/// <para>
/// Every name that reaches a unique constraint carries a random suffix. The suites share one database,
/// xUnit makes no promise about the order of tests inside a collection, and the portal alias and the
/// administrator login name are both unique installation-wide, so a fixed literal would make the suite
/// order-dependent.
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

    /// <summary>
    /// Problem type carried by every refusal of an authenticated but unentitled caller, whichever guard
    /// decided it.
    /// </summary>
    /// <remarks>
    /// MIGRATION: THREE FACTS IN THIS CLASS USED TO IDENTIFY WHICH GUARD REFUSED BY REQUIRING THE PROBLEM
    /// TYPE NOT TO BEGIN <c>urn:dnnmigration:error:auth.</c>, AND THAT DISCRIMINATOR NO LONGER EXISTS - it
    /// worked only because of a defect.
    /// </remarks>
    private const string AuthorisationRefusalProblemType = "urn:dnnmigration:error:auth.not_permitted";

    /// <summary>Problem type carried by the refusal to remove an installation's only remaining portal.</summary>
    private const string LastRemainingProblemType = "urn:dnnmigration:error:portal.last_remaining";

    /// <summary>Problem type carried by the refusal to bind an alias a portal already holds.</summary>
    private const string DuplicateAliasProblemType = "urn:dnnmigration:error:portal.alias_duplicate";

    /// <summary>
    /// Problem type carried by the refusal to rename or unbind the alias the CURRENT REQUEST resolved the
    /// tenant through.
    /// </summary>
    private const string ActiveAliasProblemType = "urn:dnnmigration:error:portal.alias_in_use.conflict";

    /// <summary>
    /// Problem type carried when a request that can only learn its tenant from the host name is refused
    /// because the host name identified none.
    /// </summary>
    private const string TenantUnresolvedProblemType = "urn:dnnmigration:error:portal.tenant_unresolved";

    /// <summary>The media type an RFC 7807 payload is served as.</summary>
    private const string ProblemMediaType = "application/problem+json";

    private readonly ApiTestFixture _fixture;

    /// <summary>Initialises a new instance of the <see cref="PortalApiTests"/> class.</summary>
    /// <param name="fixture">The shared host, database and seed.</param>
    public PortalApiTests(ApiTestFixture fixture) => _fixture = fixture;

    /// <summary>
    /// A read of the collection answers <c>200 OK</c> and includes the seeded portal. The caller is a HOST
    /// account, because the collection route names no portal and therefore carries the host-administrator
    /// policy; the companion fact below proves a portal administrator is refused here.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task ListPortals_AsHost_ReturnsOkContainingSeededPortal()
    {
        using HttpClient client = await _fixture.CreateHostClientAsync();

        using HttpResponseMessage response = await client.GetAsync(
            new Uri("/api/v1/portals?pageIndex=0&pageSize=100", UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        PagedEnvelope<PortalListItemDto>? page = await response.Content
            .ReadFromJsonAsync<PagedEnvelope<PortalListItemDto>>(ApiTestFixture.Json);

        page.Should().NotBeNull();
        page!.Meta.PageIndex.Should().Be(0);
        page.Meta.PageSize.Should().Be(100);
        page.Meta.TotalCount.Should().BeGreaterThan(0);

        PortalListItemDto seeded = page.Items
            .Should().ContainSingle(item => item.PortalId == _fixture.Seed.PortalId)
            .Subject;

        seeded.PortalName.Should().Be(IntegrationSeed.PortalName);
        seeded.Aliases.Should().Contain(ApiTestFixture.TestHost);
    }

    /// <summary>
    /// A paged response is emitted in the wire envelope - an <c>items</c> array beside a <c>meta</c> object
    /// - and the domain paging type's own members do not appear at the top level.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task ListPortals_EmitsTheWireEnvelopeAndNotTheDomainPage()
    {
        using HttpClient client = await _fixture.CreateHostClientAsync();

        using HttpResponseMessage response = await client.GetAsync(
            new Uri("/api/v1/portals?pageIndex=0&pageSize=25", UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using JsonDocument document = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync());

        JsonElement root = document.RootElement;
        root.ValueKind.Should().Be(JsonValueKind.Object);

        IReadOnlyList<string> topLevel = root.EnumerateObject()
            .Select(member => member.Name)
            .ToList();

        topLevel.Should().BeEquivalentTo(
            new[] { "items", "meta" },
            "a page is emitted as the wire envelope and carries nothing else at the top level");

        root.GetProperty("items").ValueKind.Should().Be(JsonValueKind.Array);

        JsonElement meta = root.GetProperty("meta");
        meta.ValueKind.Should().Be(JsonValueKind.Object);
        meta.EnumerateObject().Select(member => member.Name).Should().BeEquivalentTo(
            new[] { "totalCount", "pageIndex", "pageSize", "totalPages" },
            "the metadata companion carries the three paging facts and the page count it derives from them");

        meta.GetProperty("pageIndex").GetInt32().Should().Be(0);
        meta.GetProperty("pageSize").GetInt32().Should().Be(25);
        meta.GetProperty("totalCount").GetInt32().Should().BeGreaterThan(0);

        // The derived page count travels because the metadata contract computes it, and it must agree with
        // the values beside it - a client that divides and a client that reads must not disagree.
        int totalCount = meta.GetProperty("totalCount").GetInt32();
        int pageSize = meta.GetProperty("pageSize").GetInt32();
        int expectedPages = (totalCount / pageSize) + (totalCount % pageSize > 0 ? 1 : 0);
        meta.GetProperty("totalPages").GetInt32().Should().Be(expectedPages);

        // The domain paging type's own members, and the values it derives, must not appear at the top level.
        foreach (string leaked in new[] { "totalCount", "pageIndex", "pageSize", "isUnpaged", "pageCount" })
        {
            MemberNames(root).Should().NotContain(
                leaked,
                "'{0}' belongs to the domain paging type and must not be serialised at the top level",
                leaked);
        }
    }

    /// <summary>
    /// The name filter narrows the collection. This proves the query reaches the repository rather than
    /// being silently dropped, which a filter that is bound but never applied would otherwise look
    /// identical to.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task ListPortals_WithNameFilterThatMatchesNothing_ReturnsEmptyPage()
    {
        using HttpClient client = await _fixture.CreateHostClientAsync();

        using HttpResponseMessage response = await client.GetAsync(
            new Uri("/api/v1/portals?pageIndex=0&pageSize=50&name=zzz-no-portal-bears-this-name", UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        PagedEnvelope<PortalListItemDto>? page = await response.Content
            .ReadFromJsonAsync<PagedEnvelope<PortalListItemDto>>(ApiTestFixture.Json);

        page.Should().NotBeNull();
        page!.Items.Should().BeEmpty();
        page.Meta.TotalCount.Should().Be(0);
    }

    /// <summary>
    /// The collection publishes the boundary page envelope, not the domain paging envelope: the rows arrive
    /// on <c>items</c> and every paging fact arrives nested under <c>meta</c>.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task ListPortals_PublishesTheNestedPageEnvelope()
    {
        // A HOST client, because enumerating every portal in the installation is an installation-wide
        // operation and is gated as one. This fact is about the SHAPE of the page envelope; the authority
        // the listing requires is asserted by the facts that cover the policy.
        using HttpClient client = await _fixture.CreateHostClientAsync();

        using HttpResponseMessage response = await client.GetAsync(
            new Uri("/api/v1/portals?pageIndex=0&pageSize=25", UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using JsonDocument body = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync());
        JsonElement root = body.RootElement;

        root.ValueKind.Should().Be(JsonValueKind.Object);
        JsonElement items = RequireMember(root, "items");
        items.ValueKind.Should().Be(JsonValueKind.Array);

        JsonElement meta = RequireMember(root, "meta");
        meta.ValueKind.Should().Be(JsonValueKind.Object);
        meta.GetProperty("pageIndex").GetInt32().Should().Be(0);
        meta.GetProperty("pageSize").GetInt32().Should().Be(25);
        meta.GetProperty("totalCount").GetInt32().Should().BeGreaterThan(0);
        meta.GetProperty("totalPages").GetInt32().Should().BeGreaterThan(0);

        root.EnumerateObject().Select(member => member.Name)
            .Should().BeEquivalentTo(["items", "meta"]);
    }

    /// <summary>A page size beyond the permitted ceiling is rejected by the request validator.</summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task ListPortals_WithPageSizeAboveCeiling_ReturnsBadRequest()
    {
        using HttpClient client = await _fixture.CreateHostClientAsync();

        using HttpResponseMessage response = await client.GetAsync(
            new Uri("/api/v1/portals?pageIndex=0&pageSize=100000", UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    /// <summary>
    /// The sortable vocabulary is bound to the collection being addressed, so a field that is valid for a
    /// different collection is refused here.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task ListPortals_WithASortFieldBelongingToAnotherCollection_ReturnsBadRequest()
    {
        using HttpClient client = await _fixture.CreateHostClientAsync();

        using HttpResponseMessage refused = await client.GetAsync(
            new Uri("/api/v1/portals?sortBy=LastLoginDate", UriKind.Relative));

        refused.StatusCode.Should().Be(
            HttpStatusCode.BadRequest,
            "an account column is not something a portal listing can be ordered by, however valid it is "
            + "for the account listing");

        string body = await refused.Content.ReadAsStringAsync();

        body.Should().Contain(
            "PortalName",
            "the refusal must name the vocabulary of the collection the caller actually addressed");
        body.Should().NotContain(
            "LastLoginDate",
            "the refusal names what is accepted rather than echoing the rejected value back");

        using HttpResponseMessage accepted = await client.GetAsync(
            new Uri("/api/v1/portals?sortBy=PortalName", UriKind.Relative));

        accepted.StatusCode.Should().Be(
            HttpStatusCode.OK,
            "narrowing the vocabulary to this collection must not refuse this collection's own columns");
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
        using HttpClient client = await _fixture.CreateUnprivilegedClientAsync();

        using HttpResponseMessage response = await client.GetAsync(PortalsRoute);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    /// <summary>A read of the seeded portal answers <c>200 OK</c> and carries its aliases.</summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task GetPortal_ForSeededPortal_ReturnsOkWithDetail()
    {
        using HttpClient client = await _fixture.CreateAdministratorClientAsync();

        using HttpResponseMessage response = await client.GetAsync(PortalRoute(_fixture.Seed.PortalId));

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        PortalDetailDto? detail = await response.Content
            .ReadEnvelopeAsync<PortalDetailDto>();

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

    /// <summary>
    /// A tenant-scoped read naming a tenant OTHER THAN the one the request resolved to answers <c>403
    /// Forbidden</c>, whether that other tenant exists or not.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// This is the cross-tenant refusal itself, and it is asserted for BOTH a tenant that exists and one
    /// that does not, because the two must be indistinguishable.
    /// </remarks>
    [Fact]
    public async Task GetPortal_ForATenantOtherThanTheResolvedOne_ReturnsForbidden()
    {
        using HttpClient host = await _fixture.CreateHostClientAsync();
        PortalDetailDto other = await CreatePortalAsync(host);

        using HttpClient client = await _fixture.CreateAdministratorClientAsync();

        using HttpResponseMessage existing = await client.GetAsync(PortalRoute(other.PortalId));
        existing.StatusCode.Should().Be(
            HttpStatusCode.Forbidden,
            "an administrator of one tenant may not address another tenant's record");

        using HttpResponseMessage absent = await client.GetAsync(PortalRoute(UnknownPortalId));
        absent.StatusCode.Should().Be(
            HttpStatusCode.Forbidden,
            "an identifier no tenant holds must be indistinguishable from one another tenant holds");
    }

    /// <summary>
    /// The tenant binding admits a HOST account to a tenant it does not administer, which is what makes a
    /// host able to administer a tenant at all.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task GetPortal_ForAForeignTenantAsHost_IsPermitted()
    {
        using HttpClient host = await _fixture.CreateHostClientAsync();
        PortalDetailDto other = await CreatePortalAsync(host);

        using HttpResponseMessage response = await host.GetAsync(PortalRoute(other.PortalId));

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        PortalDetailDto? read = await response.Content.ReadEnvelopeAsync<PortalDetailDto>();

        read.Should().NotBeNull();
        read!.PortalId.Should().Be(
            other.PortalId,
            "the record returned is the tenant the route named, not the tenant the request resolved to");
    }

    /// <summary>An unknown identifier answers <c>404 Not Found</c> rather than an empty representation.</summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// The caller is a HOST account because only a host may address a portal the route names but the
    /// request's own host header did not resolve to. For any other caller the route-tenant reconciliation
    /// refuses the request before the service is reached, so the absent-resource path would be unobservable
    /// - and asserting a 404 from a client that cannot reach it would be asserting nothing.
    /// </remarks>
    [Fact]
    public async Task GetPortal_WhenUnknown_ReturnsNotFound()
    {
        using HttpClient client = await _fixture.CreateHostClientAsync();

        using HttpResponseMessage response = await client.GetAsync(PortalRoute(UnknownPortalId));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    /// <summary>An absent resource answers with a well formed RFC 7807 problem document.</summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task AbsentResource_ReturnsAWellFormedProblemDocument()
    {
        using HttpClient client = await _fixture.CreateHostClientAsync();

        // The canonical alias member route names the seeded portal and an alias identifier that does not
        // exist, so the request reaches the service's absent-resource branch rather than a withdrawn route.
        using HttpResponseMessage response = await client.GetAsync(
            new Uri(
                $"/api/v1/portals/{Route(_fixture.Seed.PortalId)}/aliases/{Route(UnknownPortalId)}",
                UriKind.Relative));

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

    /// <summary>
    /// A portal administrator naming a portal other than its own is refused, rather than being served that
    /// other tenant's representation.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// This is the tenant-isolation fact. The caller genuinely administers the seeded portal - the facts
    /// above prove it is served its own portal and its own settings - and the request differs only in the
    /// identifier it names, so a 403 here can only be the route-tenant reconciliation refusing to serve
    /// another tenant's data to a tenant administrator.
    /// </remarks>
    [Fact]
    public async Task GetPortal_AsAdministratorOfAnotherTenant_ReturnsForbidden()
    {
        using HttpClient host = await _fixture.CreateHostClientAsync();
        PortalDetailDto other = await CreatePortalAsync(host);

        using HttpClient client = await _fixture.CreateAdministratorClientAsync();

        using HttpResponseMessage response = await client.GetAsync(PortalRoute(other.PortalId));

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    /// <summary>
    /// The settings projection of another tenant is refused a portal administrator as well, because the
    /// reconciliation belongs to the policy rather than to one action.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task GetPortalSettings_AsAdministratorOfAnotherTenant_ReturnsForbidden()
    {
        using HttpClient host = await _fixture.CreateHostClientAsync();
        PortalDetailDto other = await CreatePortalAsync(host);

        using HttpClient client = await _fixture.CreateAdministratorClientAsync();

        using HttpResponseMessage response = await client.GetAsync(
            new Uri($"/api/v1/portals/{Route(other.PortalId)}/settings", UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    /// <summary>A rejected request answers with a per-field RFC 7807 validation document.</summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task ListPortals_WithPageSizeAboveCeiling_NamesTheOffendingField()
    {
        using HttpClient client = await _fixture.CreateHostClientAsync();

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
        using HttpClient client = await _fixture.CreateAdministratorClientAsync();

        using HttpResponseMessage response = await client.GetAsync(
            new Uri($"/api/v1/portals/{Route(_fixture.Seed.PortalId)}/settings", UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        PortalSettingsDto? settings = await response.Content
            .ReadEnvelopeAsync<PortalSettingsDto>();

        settings.Should().NotBeNull();
        settings!.PortalId.Should().Be(_fixture.Seed.PortalId);
        settings.PortalName.Should().Be(IntegrationSeed.PortalName);
        settings.Guid.Should().NotBe(Guid.Empty);
    }

    /// <summary>
    /// The administrator selector's candidates are the members of the portal's administrator role, ordered
    /// by the name the selector shows.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// The ordering is asserted rather than the mere membership. The underlying membership read orders by
    /// role and then by assignment key, which is right for a membership grid and wrong for a name picker,
    /// so an unordered projection would put the host first purely because it was inserted first.
    /// </remarks>
    [Fact]
    public async Task ListPortalAdministrators_ReturnsTheAdministratorRolesMembersInDisplayOrder()
    {
        using HttpClient client = await _fixture.CreateAdministratorClientAsync();

        using HttpResponseMessage response = await client.GetAsync(
            new Uri($"/api/v1/portals/{Route(_fixture.Seed.PortalId)}/administrators", UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        IReadOnlyList<PortalAdministratorDto>? candidates = await response.Content
            .ReadEnvelopeAsync<IReadOnlyList<PortalAdministratorDto>>();

        candidates.Should().NotBeNull();

        IReadOnlyList<PortalAdministratorDto> offered = candidates!;
        offered.Select(candidate => candidate.Username)
            .Should().Equal(
                [IntegrationSeed.AdminUserName, IntegrationSeed.HostUserName],
                "the two Administrators-role members are offered, ordered by display name - 'Integration "
                + "Administrator' before 'Integration Host' - and the Registered Users member is not");

        offered.Select(candidate => candidate.UserId)
            .Should().Equal(_fixture.Seed.AdminUserId, _fixture.Seed.HostUserId);
        offered.Should().OnlyContain(
            candidate => candidate.DisplayName.Length > 0,
            "the selector shows the display name, so an entry without one would render as a blank option");
    }

    /// <summary>The stored administrator is among the candidates, so the selector can pre-select it.</summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task ListPortalAdministrators_IncludesTheStoredAdministratorSoItCanBePreSelected()
    {
        using HttpClient client = await _fixture.CreateAdministratorClientAsync();

        using HttpResponseMessage settingsResponse = await client.GetAsync(
            new Uri($"/api/v1/portals/{Route(_fixture.Seed.PortalId)}/settings", UriKind.Relative));
        PortalSettingsDto settings = (await settingsResponse.Content
            .ReadEnvelopeAsync<PortalSettingsDto>())!;

        using HttpResponseMessage response = await client.GetAsync(
            new Uri($"/api/v1/portals/{Route(_fixture.Seed.PortalId)}/administrators", UriKind.Relative));
        IReadOnlyList<PortalAdministratorDto> candidates = (await response.Content
            .ReadEnvelopeAsync<IReadOnlyList<PortalAdministratorDto>>())!;

        settings.AdministratorId.Should().NotBeNull();
        candidates.Select(candidate => candidate.UserId)
            .Should().Contain(settings.AdministratorId!.Value);
    }

    /// <summary>An unknown portal answers <c>404 Not Found</c> rather than an empty list.</summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task ListPortalAdministrators_ForUnknownPortal_ReturnsNotFound()
    {
        using HttpClient client = await _fixture.CreateHostClientAsync();

        using HttpResponseMessage response = await client.GetAsync(
            new Uri($"/api/v1/portals/{Route(UnknownPortalId)}/administrators", UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    /// <summary>The candidate list of another tenant is refused to a portal administrator.</summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// The read discloses account names, so it is gated exactly as the settings resource it serves is. The
    /// route-supplied portal is what made the action possible in the first place; this proves the route
    /// does not thereby become a way to read another tenant's accounts.
    /// </remarks>
    [Fact]
    public async Task ListPortalAdministrators_AsAdministratorOfAnotherTenant_ReturnsForbidden()
    {
        using HttpClient host = await _fixture.CreateHostClientAsync();
        PortalDetailDto other = await CreatePortalAsync(host);

        using HttpClient client = await _fixture.CreateAdministratorClientAsync();

        using HttpResponseMessage response = await client.GetAsync(
            new Uri($"/api/v1/portals/{Route(other.PortalId)}/administrators", UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    /// <summary>An anonymous caller is refused the candidate list.</summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// Two distinct refusals, asserted together because accepting either for either caller would let the
    /// weaker one pass for the wrong reason. An ANONYMOUS caller gets 401, because there is no credential
    /// to evaluate a policy against; an authenticated ordinary member gets 403, because the policy was
    /// evaluated and refused.
    /// </remarks>
    [Fact]
    public async Task ListPortalAdministrators_WithoutAdministrativeEntitlement_IsRefused()
    {
        var route = new Uri(
            $"/api/v1/portals/{Route(_fixture.Seed.PortalId)}/administrators",
            UriKind.Relative);

        using HttpClient anonymous = _fixture.CreateAnonymousClient();
        using HttpResponseMessage unauthenticated = await anonymous.GetAsync(route);
        unauthenticated.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        using HttpClient member = await _fixture.CreateUnprivilegedClientAsync();
        using HttpResponseMessage refused = await member.GetAsync(route);
        refused.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    /// <summary>A chosen candidate can be stored through the settings resource and is served back by it.</summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task ListPortalAdministrators_OffersValuesTheSettingsWriteAccepts()
    {
        using HttpClient host = await _fixture.CreateHostClientAsync();

        using HttpResponseMessage candidatesResponse = await host.GetAsync(
            new Uri($"/api/v1/portals/{Route(_fixture.Seed.PortalId)}/administrators", UriKind.Relative));
        IReadOnlyList<PortalAdministratorDto> candidates = (await candidatesResponse.Content
            .ReadEnvelopeAsync<IReadOnlyList<PortalAdministratorDto>>())!;

        using HttpResponseMessage read = await host.GetAsync(
            new Uri($"/api/v1/portals/{Route(_fixture.Seed.PortalId)}/settings", UriKind.Relative));
        PortalSettingsDto stored = (await read.Content.ReadEnvelopeAsync<PortalSettingsDto>())!;

        // A candidate OTHER than the one already designated, so the write is a genuine reassignment rather
        // than a re-save of the same value.
        PortalAdministratorDto replacement = candidates
            .First(candidate => candidate.UserId != stored.AdministratorId);

        UpdatePortalSettingsRequest request = SettingsUpdateFrom(stored);
        request.AdministratorId = replacement.UserId;

        using HttpResponseMessage written = await host.PutAsJsonAsync(
            new Uri($"/api/v1/portals/{Route(_fixture.Seed.PortalId)}/settings", UriKind.Relative),
            request);

        written.StatusCode.Should().Be(HttpStatusCode.OK);
        PortalSettingsDto updated = (await written.Content.ReadEnvelopeAsync<PortalSettingsDto>())!;
        updated.AdministratorId.Should().Be(replacement.UserId);

        // Read back, so the value is proven stored rather than merely echoed.
        using HttpResponseMessage reread = await host.GetAsync(
            new Uri($"/api/v1/portals/{Route(_fixture.Seed.PortalId)}/settings", UriKind.Relative));
        PortalSettingsDto served = (await reread.Content.ReadEnvelopeAsync<PortalSettingsDto>())!;
        served.AdministratorId.Should().Be(replacement.UserId);

        // Restored, so the ordering of this suite's cases cannot matter.
        UpdatePortalSettingsRequest restore = SettingsUpdateFrom(served);
        restore.AdministratorId = stored.AdministratorId;
        using HttpResponseMessage restored = await host.PutAsJsonAsync(
            new Uri($"/api/v1/portals/{Route(_fixture.Seed.PortalId)}/settings", UriKind.Relative),
            restore);
        restored.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    /// <summary>
    /// The settings resource accepts a complete replacement, returns the updated projection and serves the
    /// same values from its GET representation afterwards.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task UpdatePortalSettings_RoundTripsOnTheSettingsResource()
    {
        using HttpClient host = await _fixture.CreateHostClientAsync();
        (PortalDetailDto created, CreatePortalRequest createRequest) =
            await CreatePortalWithRequestAsync(host);
        using HttpClient client = await CreatedTenantClientAsync(created, createRequest);
        var route = new Uri(
            $"/api/v1/portals/{Route(created.PortalId)}/settings",
            UriKind.Relative);

        using HttpResponseMessage beforeResponse = await client.GetAsync(route);
        beforeResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        PortalSettingsDto before = (await beforeResponse.Content
            .ReadEnvelopeAsync<PortalSettingsDto>())!;

        UpdatePortalSettingsRequest request = SettingsUpdateFrom(before);
        request.Description = "Updated through the portal settings resource.";

        using HttpResponseMessage update = await client.PutAsJsonAsync(
            route,
            request,
            ApiTestFixture.Json);

        update.StatusCode.Should().Be(HttpStatusCode.OK);
        PortalSettingsDto? returned = await update.Content.ReadEnvelopeAsync<PortalSettingsDto>();
        returned.Should().NotBeNull();
        returned!.PortalId.Should().Be(created.PortalId);
        returned.Description.Should().Be(request.Description);
        returned.Guid.Should().Be(before.Guid, "the provisioning-owned identifier is not writable");

        using HttpResponseMessage afterResponse = await client.GetAsync(route);
        afterResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        PortalSettingsDto? after = await afterResponse.Content.ReadEnvelopeAsync<PortalSettingsDto>();
        after!.Description.Should().Be(request.Description);
    }

    /// <summary>A malformed settings body answers with a field-keyed validation problem.</summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// The subject of this test is unchanged: a malformed body answers with a problem document that NAMES
    /// the member at fault. The offending value is now a title one character past the terminal width of
    /// <c>[PortalName] [nvarchar] (128)</c>, which the surviving <c>MaximumLength</c> rule refuses and
    /// which keys its message to the same member, so nothing about the assertion weakens.
    /// </remarks>
    [Fact]
    public async Task UpdatePortalSettings_WhenInvalid_NamesTheOffendingField()
    {
        using HttpClient client = await _fixture.CreateAdministratorClientAsync();
        var route = new Uri(
            $"/api/v1/portals/{Route(_fixture.Seed.PortalId)}/settings",
            UriKind.Relative);

        using HttpResponseMessage read = await client.GetAsync(route);
        PortalSettingsDto settings = (await read.Content.ReadEnvelopeAsync<PortalSettingsDto>())!;
        UpdatePortalSettingsRequest request = SettingsUpdateFrom(settings);
        request.PortalName = new string('n', 129);

        using HttpResponseMessage response = await client.PutAsJsonAsync(
            route,
            request,
            ApiTestFixture.Json);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        IReadOnlyDictionary<string, string[]> errors = await ReadValidationErrorsAsync(response);
        errors.Should().ContainKey(nameof(UpdatePortalSettingsRequest.PortalName));

        // The refusal wrote nothing, so the seeded tenant this test addresses still carries its own name.
        using HttpResponseMessage after = await client.GetAsync(route);
        after.StatusCode.Should().Be(HttpStatusCode.OK);
        PortalSettingsDto unchanged = (await after.Content.ReadEnvelopeAsync<PortalSettingsDto>())!;
        unchanged.PortalName.Should().Be(settings.PortalName);
    }

    /// <summary>
    /// A whitespace-only title is ACCEPTED through the settings resource, exactly as the legacy screen
    /// accepted one, and is stored verbatim rather than trimmed away by the server.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// The counterpart to the case above, and the one that pins the withdrawn rule. Addressed at a tenant
    /// this test creates rather than at the seed, precisely because the submission succeeds.
    /// </remarks>
    [Fact]
    public async Task UpdatePortalSettings_WithAWhitespaceOnlyName_IsAcceptedAndStoredVerbatim()
    {
        using HttpClient host = await _fixture.CreateHostClientAsync();
        (PortalDetailDto created, CreatePortalRequest createRequest) = await CreatePortalWithRequestAsync(host);

        using HttpClient client = await CreatedTenantClientAsync(created, createRequest);
        var route = new Uri(
            $"/api/v1/portals/{Route(created.PortalId)}/settings",
            UriKind.Relative);

        using HttpResponseMessage read = await client.GetAsync(route);
        read.StatusCode.Should().Be(HttpStatusCode.OK);
        PortalSettingsDto settings = (await read.Content.ReadEnvelopeAsync<PortalSettingsDto>())!;

        UpdatePortalSettingsRequest request = SettingsUpdateFrom(settings);
        request.PortalName = "   ";

        using HttpResponseMessage response = await client.PutAsJsonAsync(
            route,
            request,
            ApiTestFixture.Json);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        PortalSettingsDto? returned = await response.Content.ReadEnvelopeAsync<PortalSettingsDto>();
        returned.Should().NotBeNull();
        returned!.PortalName.Should().Be("   ");

        string storedName = await _fixture.Database.ScalarAsync<string>(
            "SELECT [PortalName] FROM [dbo].[Portals] WHERE [PortalID] = @portalId;",
            new Dictionary<string, object?> { ["portalId"] = created.PortalId });

        storedName.Should().Be("   ");
    }

    /// <summary>The PUT carries the same cross-tenant isolation policy as the GET.</summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task UpdatePortalSettings_AsAdministratorOfAnotherTenant_ReturnsForbidden()
    {
        using HttpClient host = await _fixture.CreateHostClientAsync();
        PortalDetailDto other = await CreatePortalAsync(host);
        var route = new Uri(
            $"/api/v1/portals/{Route(other.PortalId)}/settings",
            UriKind.Relative);

        using HttpResponseMessage read = await host.GetAsync(route);
        PortalSettingsDto settings = (await read.Content.ReadEnvelopeAsync<PortalSettingsDto>())!;
        UpdatePortalSettingsRequest request = SettingsUpdateFrom(settings);

        using HttpClient administrator = await _fixture.CreateAdministratorClientAsync();
        using HttpResponseMessage response = await administrator.PutAsJsonAsync(
            route,
            request,
            ApiTestFixture.Json);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    /// <summary>
    /// The settings resource refuses a page that belongs to a different tenant, and stores nothing.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// The neighbour's page identifier is READ FROM THE NEIGHBOUR rather than invented, so the value is a
    /// real page of a real other tenant.
    /// </remarks>
    [Fact]
    public async Task UpdatePortalSettings_RefusesAPageBelongingToAnotherTenant()
    {
        using HttpClient host = await _fixture.CreateHostClientAsync();
        PortalDetailDto subject = await CreatePortalAsync(host);
        PortalDetailDto neighbour = await CreatePortalAsync(host);

        var neighbourRoute = new Uri(
            $"/api/v1/portals/{Route(neighbour.PortalId)}/settings",
            UriKind.Relative);
        using HttpResponseMessage neighbourRead = await host.GetAsync(neighbourRoute);
        neighbourRead.StatusCode.Should().Be(HttpStatusCode.OK);
        PortalSettingsDto neighbourSettings = (await neighbourRead.Content
            .ReadEnvelopeAsync<PortalSettingsDto>())!;
        neighbourSettings.HomeTabId.Should().NotBeNull(
            "provisioning creates the tenant's home page, which is what makes this a real foreign page");

        var route = new Uri(
            $"/api/v1/portals/{Route(subject.PortalId)}/settings",
            UriKind.Relative);
        using HttpResponseMessage before = await host.GetAsync(route);
        before.StatusCode.Should().Be(HttpStatusCode.OK);
        PortalSettingsDto original = (await before.Content.ReadEnvelopeAsync<PortalSettingsDto>())!;
        original.HomeTabId.Should().NotBe(
            neighbourSettings.HomeTabId,
            "the two tenants must start out pointing at their own pages for the write to mean anything");

        UpdatePortalSettingsRequest request = SettingsUpdateFrom(original);
        request.HomeTabId = neighbourSettings.HomeTabId;

        using HttpResponseMessage response = await host.PutAsJsonAsync(
            route,
            request,
            ApiTestFixture.Json);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        ProblemDetails? problem = await response.Content
            .ReadFromJsonAsync<ProblemDetails>(ApiTestFixture.Json);
        problem.Should().NotBeNull();
        problem!.Detail.Should().Contain(
            nameof(UpdatePortalSettingsRequest.HomeTabId),
            "the refusal names the member that carried the foreign reference");

        using HttpResponseMessage after = await host.GetAsync(route);
        PortalSettingsDto stored = (await after.Content.ReadEnvelopeAsync<PortalSettingsDto>())!;
        stored.HomeTabId.Should().Be(
            original.HomeTabId,
            "the foreign page must not have been stored, and no other member may have moved either");
        stored.PortalName.Should().Be(original.PortalName);
    }

    /// <summary>
    /// The settings projection binds to the addressed tenant exactly as the detail read does: refused for a
    /// portal administrator, and answered as absent for a host account.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task GetPortalSettings_ForATenantOtherThanTheResolvedOne_BindsToTheAddressedTenant()
    {
        var settingsRoute = new Uri(
            $"/api/v1/portals/{Route(UnknownPortalId)}/settings",
            UriKind.Relative);

        using HttpClient administrator = await _fixture.CreateAdministratorClientAsync();

        using HttpResponseMessage refused = await administrator.GetAsync(settingsRoute);
        refused.StatusCode.Should().Be(
            HttpStatusCode.Forbidden,
            "an administrator of one tenant may not address another tenant's settings, and must not be able "
            + "to tell a foreign identifier from an unknown one");

        using HttpClient host = await _fixture.CreateHostClientAsync();

        using HttpResponseMessage absent = await host.GetAsync(settingsRoute);
        absent.StatusCode.Should().Be(
            HttpStatusCode.NotFound,
            "a host account passes the policy, so for it the identifier is simply not held by any tenant");
    }

    /// <summary>
    /// A create answers <c>201 Created</c>, carries a location that resolves, and provisions the whole
    /// tenant: the alias that reaches it, the three stock roles and an administrator that can sign in.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task CreatePortal_ReturnsCreatedWithResolvableLocation()
    {
        using HttpClient client = await _fixture.CreateHostClientAsync();

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

        // Followed through the NEW tenant's own alias, as its own administrator.
        using HttpClient throughItsOwnAlias = await CreatedTenantClientAsync(created, request);

        using HttpResponseMessage followed = await throughItsOwnAlias.GetAsync(
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
    /// A CHILD portal is composed beneath the addressed parent's host name, is reachable at that composed
    /// address, and its own tenant-scoped routes reach a controller from beneath the path segment.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task CreatePortal_ComposesAChildBeneathTheParentAndItsRoutesResolve()
    {
        using HttpClient host = await _fixture.CreateHostClientAsync();

        // The parent, created by its host name in the ordinary way.
        (PortalDetailDto parent, CreatePortalRequest parentRequest) =
            await CreatePortalWithRequestAsync(host);

        string parentAuthority = parentRequest.PortalAlias!;
        string segment = "child" + Suffix();

        // The child is asked for from a request ADDRESSED AT THE PARENT, which is the arrangement the legacy
        // portal-page branch was reached in, and it submits a BARE SEGMENT rather than a host name.
        CreatePortalRequest childRequest = NewPortalRequest();
        childRequest.IsChildPortal = true;
        childRequest.PortalAlias = segment;

        // A HOST account addressing the parent's host name.
        using HttpClient beneathTheParent = await _fixture.CreateHostClientAsync(parentAuthority);

        using HttpResponseMessage response = await beneathTheParent.PostAsJsonAsync(
            PortalsRoute,
            childRequest,
            ApiTestFixture.Json);

        response.StatusCode.Should().Be(HttpStatusCode.Created);

        PortalDetailDto child = await ReadDetailAsync(response);
        child.PortalId.Should().NotBe(parent.PortalId);

        string composed = parentAuthority + "/" + segment;

        // The COMPOSED address is what was stored, not the submitted segment. Asserted against the database
        // as well as the representation, because the column is what a request will later be matched against.
        string storedAlias = await _fixture.Database.ScalarAsync<string>(
            "SELECT [HTTPAlias] FROM [dbo].[PortalAlias] WHERE [PortalID] = @portalId;",
            new Dictionary<string, object?> { ["portalId"] = child.PortalId });

        storedAlias.Should().Be(composed);
        child.Aliases!.Select(alias => alias.HttpAlias).Should().Contain(composed);

        int bareSegmentRows = await _fixture.Database.ScalarAsync<int>(
            "SELECT COUNT(*) FROM [dbo].[PortalAlias] WHERE [HTTPAlias] = @alias;",
            new Dictionary<string, object?> { ["alias"] = segment });

        bareSegmentRows.Should().Be(0, "storing the bare segment is the defect being fixed");

        // Addressed at the CHILD: the authority is the shared host and the tenant is identified by the path
        // segment, which the path-base stage must strip before routing.
        using HttpClient beneathTheChild = await _fixture.CreateTenantClientAsync(
            composed,
            child.PortalId,
            childRequest.AdministratorUsername!);

        using HttpResponseMessage childRead = await beneathTheChild.GetAsync(
            new Uri("/" + segment + "/api/v1/portals/" + Route(child.PortalId), UriKind.Relative));

        childRead.StatusCode.Should().Be(
            HttpStatusCode.OK,
            "the child's own routes must reach a controller from beneath its path segment");

        PortalDetailDto readBack = await ReadDetailAsync(childRead);
        readBack.PortalId.Should().Be(child.PortalId);

        // The longest-prefix preference, in both directions. A request beneath the segment must NOT resolve to
        // the parent, and a request at the bare authority must still resolve to the parent.
        using HttpResponseMessage parentFromChildAddress = await beneathTheChild.GetAsync(
            new Uri("/" + segment + "/api/v1/portals/" + Route(parent.PortalId), UriKind.Relative));

        parentFromChildAddress.StatusCode.Should().Be(
            HttpStatusCode.Forbidden,
            "a request beneath the child's segment resolves to the child, so the parent is a foreign tenant");

        using HttpClient parentClient = await CreatedTenantClientAsync(parent, parentRequest);

        using HttpResponseMessage parentRead = await parentClient.GetAsync(PortalRoute(parent.PortalId));

        parentRead.StatusCode.Should().Be(
            HttpStatusCode.OK,
            "adding a child must not stop the parent resolving at its own bare authority");
    }

    /// <summary>
    /// A resource created by a request addressed beneath a CHILD portal's path segment is located at the
    /// child's own address, and that address is reachable by the caller that received it.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// THIS IS THE END-TO-END HALF OF THE LOCATION DEFECT. A child tenant is addressed by a path segment
    /// beneath a shared host, and the path-base stage moves that segment out of the routable path before
    /// routing - so by the time an action returns, the path alone spells the PARENT's address.
    /// </remarks>
    [Fact]
    public async Task CreateBeneathAChildTenant_LocatesTheNewResourceAtTheChildsOwnAddress()
    {
        using HttpClient host = await _fixture.CreateHostClientAsync();

        (PortalDetailDto parent, CreatePortalRequest parentRequest) =
            await CreatePortalWithRequestAsync(host);

        string parentAuthority = parentRequest.PortalAlias!;
        string segment = "child" + Suffix();

        CreatePortalRequest childRequest = NewPortalRequest();
        childRequest.IsChildPortal = true;
        childRequest.PortalAlias = segment;

        using HttpClient beneathTheParent = await _fixture.CreateHostClientAsync(parentAuthority);

        using HttpResponseMessage childCreated = await beneathTheParent.PostAsJsonAsync(
            PortalsRoute,
            childRequest,
            ApiTestFixture.Json);

        childCreated.StatusCode.Should().Be(HttpStatusCode.Created);

        PortalDetailDto child = await ReadDetailAsync(childCreated);
        child.PortalId.Should().NotBe(parent.PortalId);

        // The child's own administrator, arriving at the shared authority beneath the child's segment - which
        // is the only address that resolves to the child.
        using HttpClient beneathTheChild = await _fixture.CreateTenantClientAsync(
            parentAuthority + "/" + segment,
            child.PortalId,
            childRequest.AdministratorUsername!);

        string pathBase = "/" + segment;
        string aliasCollection = $"/api/v1/portals/{Route(child.PortalId)}/aliases";
        string httpAlias = "child-located-" + Suffix() + ".local";

        using HttpResponseMessage response = await beneathTheChild.PostAsJsonAsync(
            new Uri(pathBase + aliasCollection, UriKind.Relative),
            new CreatePortalAliasRequest { HttpAlias = httpAlias },
            ApiTestFixture.Json);

        response.StatusCode.Should().Be(HttpStatusCode.Created);

        PortalAliasDto? alias = await response.Content.ReadEnvelopeAsync<PortalAliasDto>();
        alias.Should().NotBeNull();
        alias!.PortalId.Should().Be(child.PortalId);

        response.Headers.Location.Should().NotBeNull("a create locates what it created");
        response.Headers.Location!.OriginalString.Should().Be(
            $"{pathBase}{aliasCollection}/{Route(alias.PortalAliasId)}",
            "the tenant's path segment is part of the address the caller posted to, so it is part of the "
            + "address the creation hands back");

        using HttpResponseMessage followed = await beneathTheChild.GetAsync(response.Headers.Location);

        followed.StatusCode.Should().Be(
            HttpStatusCode.OK,
            "the published address must be one the caller that received it can actually reach");

        PortalAliasDto? fetched = await followed.Content.ReadEnvelopeAsync<PortalAliasDto>();
        fetched.Should().NotBeNull();
        fetched!.PortalAliasId.Should().Be(alias.PortalAliasId);
    }

    /// <summary>
    /// A child portal asked for from a request that resolved to no tenant is refused as a bad request, and
    /// nothing is written.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// The default host client addresses an alias that IS configured, so this test has to reach the API
    /// from a host name that is not - which is exactly the state an operator provisioning the first portal
    /// of an installation is in.
    /// </remarks>
    [Fact]
    public async Task CreatePortal_AsAChildWithNoResolvedParent_ReturnsBadRequest()
    {
        CreatePortalRequest request = NewPortalRequest();
        request.IsChildPortal = true;
        request.PortalAlias = "orphan" + Suffix();

        using HttpClient unconfiguredHost = await _fixture.CreateHostClientAsync("unconfigured-" + Suffix() + ".local");

        using HttpResponseMessage response = await unconfiguredHost.PostAsJsonAsync(
            PortalsRoute,
            request,
            ApiTestFixture.Json);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        int written = await _fixture.Database.ScalarAsync<int>(
            "SELECT COUNT(*) FROM [dbo].[Portals] WHERE [PortalName] = @name;",
            new Dictionary<string, object?> { ["name"] = request.PortalName });

        written.Should().Be(0);
    }

    /// <summary>
    /// A failed creation leaves NOTHING behind - no tenant, no alias, no roles, no account and no
    /// credential - because the whole provisioning sequence is one transaction.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// The failure is provoked the only way an integration test honestly can: by submitting an
    /// administrator account name that is already in use, which is refused AFTER the request has been
    /// validated and read.
    /// </remarks>
    [Fact]
    public async Task CreatePortal_WhenRefused_LeavesNothingBehind()
    {
        using HttpClient client = await _fixture.CreateHostClientAsync();

        (_, CreatePortalRequest existing) = await CreatePortalWithRequestAsync(client);

        CreatePortalRequest colliding = NewPortalRequest();
        colliding.AdministratorUsername = existing.AdministratorUsername;

        using HttpResponseMessage response = await client.PostAsJsonAsync(
            PortalsRoute,
            colliding,
            ApiTestFixture.Json);

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);

        int portals = await _fixture.Database.ScalarAsync<int>(
            "SELECT COUNT(*) FROM [dbo].[Portals] WHERE [PortalName] = @name;",
            new Dictionary<string, object?> { ["name"] = colliding.PortalName });

        portals.Should().Be(0);

        int aliases = await _fixture.Database.ScalarAsync<int>(
            "SELECT COUNT(*) FROM [dbo].[PortalAlias] WHERE [HTTPAlias] = @alias;",
            new Dictionary<string, object?> { ["alias"] = colliding.PortalAlias });

        aliases.Should().Be(0);
    }

    /// <summary>
    /// A create without a template name is rejected by the request validator. The service never opens the
    /// template, but the contract still demands one, so this proves the validator is attached to the
    /// action.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task CreatePortal_WithoutTemplateFile_ReturnsBadRequest()
    {
        using HttpClient client = await _fixture.CreateHostClientAsync();

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
        using HttpClient client = await _fixture.CreateHostClientAsync();

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
        using HttpClient client = await _fixture.CreateHostClientAsync();

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
        using HttpClient client = await _fixture.CreateHostClientAsync();

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
        using HttpClient client = await _fixture.CreateHostClientAsync();

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
    /// C-02: a created tenant is USABLE, not merely present - it has the default profile property
    /// definitions an account needs in order to hold a profile at all, and a home page with the grants that
    /// let it be served.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// Every assertion goes to the DATABASE rather than to the representation, because the defect this
    /// closes was a success response returned over an unusable tenant: a response body cannot witness the
    /// absence of rows the caller never asked about.
    /// </remarks>
    [Fact]
    public async Task CreatePortal_ProvisionsProfileDefinitionsAndAHomePage()
    {
        using HttpClient client = await _fixture.CreateHostClientAsync();

        CreatePortalRequest request = NewPortalRequest();

        using HttpResponseMessage response = await client.PostAsJsonAsync(
            PortalsRoute,
            request,
            ApiTestFixture.Json);

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        PortalDetailDto created = await ReadDetailAsync(response);

        var byPortal = new Dictionary<string, object?> { ["portalId"] = created.PortalId };

        int definitionCount = await _fixture.Database.ScalarAsync<int>(
            "SELECT COUNT(*) FROM [dbo].[ProfilePropertyDefinition] WHERE [PortalID] = @portalId;",
            byPortal);
        definitionCount.Should().Be(19, "the legacy default set has nineteen members");

        int firstViewOrder = await _fixture.Database.ScalarAsync<int>(
            "SELECT MIN([ViewOrder]) FROM [dbo].[ProfilePropertyDefinition] WHERE [PortalID] = @portalId;",
            byPortal);
        int lastViewOrder = await _fixture.Database.ScalarAsync<int>(
            "SELECT MAX([ViewOrder]) FROM [dbo].[ProfilePropertyDefinition] WHERE [PortalID] = @portalId;",
            byPortal);
        firstViewOrder.Should().Be(3, "the legacy helper incremented its counter before assigning it");
        lastViewOrder.Should().Be(39);

        int categoryCount = await _fixture.Database.ScalarAsync<int>(
            @"SELECT COUNT(DISTINCT [PropertyCategory])
              FROM [dbo].[ProfilePropertyDefinition]
              WHERE [PortalID] = @portalId;",
            byPortal);
        categoryCount.Should().Be(4, "Name, Address, Contact Info and Preferences");

        // The home page exists, is live, sits at the root of the tenant's navigation, and the portal points
        // at it - a page the portal does not point at would leave the tenant with nowhere to serve.
        int homeTabId = await _fixture.Database.ScalarAsync<int>(
            "SELECT ISNULL([HomeTabID], -1) FROM [dbo].[Portals] WHERE [PortalID] = @portalId;",
            byPortal);
        homeTabId.Should().BePositive("the store assigns the page an identifier and the portal is stamped with it");

        int homePageRows = await _fixture.Database.ScalarAsync<int>(
            @"SELECT COUNT(*)
              FROM [dbo].[Tabs]
              WHERE [TabID] = @tabId
                AND [PortalID] = @portalId
                AND [TabName] = 'Home'
                AND [IsDeleted] = 0
                AND [ParentId] IS NULL;",
            new Dictionary<string, object?> { ["tabId"] = homeTabId, ["portalId"] = created.PortalId });
        homePageRows.Should().Be(1);

        // Three grants: view for all users, view for administrators, edit for administrators.
        int grantCount = await _fixture.Database.ScalarAsync<int>(
            "SELECT COUNT(*) FROM [dbo].[TabPermission] WHERE [TabID] = @tabId AND [AllowAccess] = 1;",
            new Dictionary<string, object?> { ["tabId"] = homeTabId });
        grantCount.Should().Be(3);

        int allUsersViewGrants = await _fixture.Database.ScalarAsync<int>(
            @"SELECT COUNT(*)
              FROM [dbo].[TabPermission] tp
              INNER JOIN [dbo].[Permission] p ON p.[PermissionID] = tp.[PermissionID]
              WHERE tp.[TabID] = @tabId
                AND tp.[RoleID] = -1
                AND p.[PermissionCode] = @code
                AND p.[PermissionKey] = 'VIEW';",
            new Dictionary<string, object?>
            {
                ["tabId"] = homeTabId,
                ["code"] = IntegrationSeed.TabPermissionCode,
            });
        allUsersViewGrants.Should().Be(1, "the all-users grant carries the identifier the schema reserves for it");

        int administratorGrants = await _fixture.Database.ScalarAsync<int>(
            @"SELECT COUNT(DISTINCT p.[PermissionKey])
              FROM [dbo].[TabPermission] tp
              INNER JOIN [dbo].[Permission] p ON p.[PermissionID] = tp.[PermissionID]
              WHERE tp.[TabID] = @tabId
                AND tp.[RoleID] = @roleId
                AND p.[PermissionCode] = @code;",
            new Dictionary<string, object?>
            {
                ["tabId"] = homeTabId,
                ["roleId"] = created.AdministratorRoleId,
                ["code"] = IntegrationSeed.TabPermissionCode,
            });
        administratorGrants.Should().Be(2, "administrators receive both the view and the edit grant");
    }

    /// <summary>
    /// C-02: a creation that fails after its first write publishes NOTHING - no portal, no alias, no roles,
    /// no profile definitions and no page.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// The refusal is provoked by an administrator login name already in use, which the sequence detects
    /// only after the portal row, the alias, the three roles, the account and the enrolments have been
    /// staged and written.
    /// </remarks>
    [Fact]
    public async Task CreatePortal_WhenAStageFails_PublishesNothingAtAll()
    {
        using HttpClient client = await _fixture.CreateHostClientAsync();

        CreatePortalRequest request = NewPortalRequest();
        request.AdministratorUsername = IntegrationSeed.AdminUserName;

        using HttpResponseMessage response = await client.PostAsJsonAsync(
            PortalsRoute,
            request,
            ApiTestFixture.Json);

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);

        var byName = new Dictionary<string, object?> { ["portalName"] = request.PortalName };

        int portalRows = await _fixture.Database.ScalarAsync<int>(
            "SELECT COUNT(*) FROM [dbo].[Portals] WHERE [PortalName] = @portalName;",
            byName);
        portalRows.Should().Be(0, "the portal row itself is discarded with the rest of the transaction");

        int aliasRows = await _fixture.Database.ScalarAsync<int>(
            "SELECT COUNT(*) FROM [dbo].[PortalAlias] WHERE [HTTPAlias] = @alias;",
            new Dictionary<string, object?> { ["alias"] = request.PortalAlias });
        aliasRows.Should().Be(0);

        // Roles, definitions and pages are all keyed by the portal, so with no portal row there can be no
        // orphan referencing it. The query proves that positively rather than assuming the foreign keys held.
        int orphanRows = await _fixture.Database.ScalarAsync<int>(
            @"SELECT
                  (SELECT COUNT(*) FROM [dbo].[Roles] r
                   WHERE NOT EXISTS (SELECT 1 FROM [dbo].[Portals] p WHERE p.[PortalID] = r.[PortalID]))
                + (SELECT COUNT(*) FROM [dbo].[ProfilePropertyDefinition] d
                   WHERE NOT EXISTS (SELECT 1 FROM [dbo].[Portals] p WHERE p.[PortalID] = d.[PortalID]))
                + (SELECT COUNT(*) FROM [dbo].[Tabs] t
                   WHERE t.[PortalID] IS NOT NULL
                     AND NOT EXISTS (SELECT 1 FROM [dbo].[Portals] p WHERE p.[PortalID] = t.[PortalID]));");
        orphanRows.Should().Be(0);
    }

    /// <summary>
    /// An update answers <c>200 OK</c> and the new state survives a subsequent read. The six host-only
    /// values are echoed from the representation just read, so this exercises the update rather than the
    /// guard beside it.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task UpdatePortal_AsHost_ReturnsOkAndPersists()
    {
        using HttpClient host = await _fixture.CreateHostClientAsync();
        (PortalDetailDto created, CreatePortalRequest createRequest) = await CreatePortalWithRequestAsync(host);

        using HttpClient client = await CreateAdministratorClientForAsync(createRequest, created);

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
    /// The exact lost-update sequence, reproduced: two callers read the same portal, both save, and the
    /// second save is refused instead of silently destroying the first caller's committed edit.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// THIS IS THE MEASURED DEFECT, NOT AN ANALOGY. Runtime testing opened one portal in two sessions,
    /// saved from the first and then saved from the second without reloading, and BOTH saves answered
    /// <c>200</c> with the first operator's changes gone.
    /// </remarks>
    [Fact]
    public async Task UpdatePortal_FromASnapshotAnotherCallerHasAlreadyReplaced_IsRefusedAndPreservesTheStoredEdit()
    {
        using HttpClient host = await _fixture.CreateHostClientAsync();
        (PortalDetailDto created, CreatePortalRequest createRequest) = await CreatePortalWithRequestAsync(host);
        using HttpClient client = await CreateAdministratorClientForAsync(createRequest, created);

        // ONE READ, SHARED BY BOTH SAVES. This is what "two sessions with the screen open" means: the token
        // both requests carry is the token that single read published.
        using HttpResponseMessage read = await client.GetAsync(PortalRoute(created.PortalId));
        read.StatusCode.Should().Be(HttpStatusCode.OK);
        PortalDetailDto snapshot = await ReadDetailAsync(read);
        snapshot.ConcurrencyToken.Should().NotBeNullOrWhiteSpace(
            "a read must publish the token, or a caller has nothing to send back");

        UpdatePortalRequest first = EchoHostOnlyFields(snapshot);
        first.ConcurrencyToken = snapshot.ConcurrencyToken;
        first.PortalName = snapshot.PortalName;
        first.FooterText = "Committed by the first caller " + Suffix();

        using HttpResponseMessage firstSave = await client.PutAsJsonAsync(
            PortalRoute(created.PortalId),
            first,
            ApiTestFixture.Json);
        firstSave.StatusCode.Should().Be(HttpStatusCode.OK);

        PortalDetailDto afterFirst = await ReadDetailAsync(firstSave);
        afterFirst.ConcurrencyToken.Should().NotBe(
            snapshot.ConcurrencyToken,
            "the token must move when the record moves, or it cannot detect anything");

        UpdatePortalRequest second = EchoHostOnlyFields(snapshot);
        second.ConcurrencyToken = snapshot.ConcurrencyToken;
        second.PortalName = snapshot.PortalName;
        second.Description = "Committed by the second caller " + Suffix();

        using HttpResponseMessage secondSave = await client.PutAsJsonAsync(
            PortalRoute(created.PortalId),
            second,
            ApiTestFixture.Json);

        secondSave.StatusCode.Should().Be(
            HttpStatusCode.Conflict,
            "a save built on a snapshot that has since been replaced must be refused, not applied");

        ProblemDetails? problem = await secondSave.Content.ReadFromJsonAsync<ProblemDetails>();
        problem.Should().NotBeNull();
        problem!.Type.Should().Contain(
            "portal.concurrency_conflict",
            "the reason must be a stable code so a client can tell this conflict from the others");

        // THE POINT OF THE WHOLE FIXTURE: the first caller's committed edit is still there.
        using HttpResponseMessage reread = await client.GetAsync(PortalRoute(created.PortalId));
        PortalDetailDto stored = await ReadDetailAsync(reread);
        stored.FooterText.Should().Be(
            first.FooterText,
            "the refused save must not have been applied even partially");
        stored.Description.Should().NotBe(
            second.Description,
            "the stale caller's value must not be stored");
    }

    /// <summary>
    /// A caller that re-reads after the conflict and re-applies its change succeeds, so the refusal is
    /// recoverable rather than a dead end.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// A guard that cannot be satisfied is a bug, not a protection. This asserts the recovery path the
    /// refusal's own message instructs the caller to take - read again, re-apply - and it is the reason the
    /// token is published by every portal read rather than only by the write.
    /// </remarks>
    [Fact]
    public async Task UpdatePortal_AfterRereadingFollowingAConflict_Succeeds()
    {
        using HttpClient host = await _fixture.CreateHostClientAsync();
        (PortalDetailDto created, CreatePortalRequest createRequest) = await CreatePortalWithRequestAsync(host);
        using HttpClient client = await CreateAdministratorClientForAsync(createRequest, created);

        using HttpResponseMessage read = await client.GetAsync(PortalRoute(created.PortalId));
        PortalDetailDto snapshot = await ReadDetailAsync(read);

        UpdatePortalRequest overtaking = EchoHostOnlyFields(snapshot);
        overtaking.ConcurrencyToken = snapshot.ConcurrencyToken;
        overtaking.PortalName = snapshot.PortalName;
        overtaking.FooterText = "Overtaken " + Suffix();
        using HttpResponseMessage overtaken = await client.PutAsJsonAsync(
            PortalRoute(created.PortalId),
            overtaking,
            ApiTestFixture.Json);
        overtaken.StatusCode.Should().Be(HttpStatusCode.OK);

        UpdatePortalRequest stale = EchoHostOnlyFields(snapshot);
        stale.ConcurrencyToken = snapshot.ConcurrencyToken;
        stale.PortalName = snapshot.PortalName;
        stale.Description = "Retried " + Suffix();
        using HttpResponseMessage refused = await client.PutAsJsonAsync(
            PortalRoute(created.PortalId),
            stale,
            ApiTestFixture.Json);
        refused.StatusCode.Should().Be(HttpStatusCode.Conflict);

        // The recovery the refusal asks for: read again, keep the intended change, resend.
        using HttpResponseMessage refreshed = await client.GetAsync(PortalRoute(created.PortalId));
        PortalDetailDto current = await ReadDetailAsync(refreshed);

        UpdatePortalRequest retried = EchoHostOnlyFields(current);
        retried.ConcurrencyToken = current.ConcurrencyToken;
        retried.PortalName = current.PortalName;
        retried.Description = stale.Description;

        using HttpResponseMessage accepted = await client.PutAsJsonAsync(
            PortalRoute(created.PortalId),
            retried,
            ApiTestFixture.Json);

        accepted.StatusCode.Should().Be(
            HttpStatusCode.OK,
            "re-reading and re-applying is the documented recovery and must work");
        (await ReadDetailAsync(accepted)).Description.Should().Be(stale.Description);
    }

    /// <summary>
    /// An update that carries no token is still applied, so no caller written before the token existed is
    /// refused.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// The trade is asserted rather than left to documentation, because it is the one property of this
    /// guard a reader is most likely to assume the other way round. A caller that omits the token opts out
    /// of the protection and gets the legacy last-write-wins behaviour; a caller that supplies it is
    /// protected.
    /// </remarks>
    [Fact]
    public async Task UpdatePortal_WithNoConcurrencyTokenAtAll_IsStillApplied()
    {
        using HttpClient host = await _fixture.CreateHostClientAsync();
        (PortalDetailDto created, CreatePortalRequest createRequest) = await CreatePortalWithRequestAsync(host);
        using HttpClient client = await CreateAdministratorClientForAsync(createRequest, created);

        UpdatePortalRequest request = EchoHostOnlyFields(created);
        request.ConcurrencyToken = null;
        request.PortalName = created.PortalName;
        request.Description = "Applied without a token " + Suffix();

        using HttpResponseMessage response = await client.PutAsJsonAsync(
            PortalRoute(created.PortalId),
            request,
            ApiTestFixture.Json);

        response.StatusCode.Should().Be(
            HttpStatusCode.OK,
            "an omitted token means the caller has not opted into the protection, not that it is refused");
        (await ReadDetailAsync(response)).Description.Should().Be(request.Description);
    }

    /// <summary>
    /// The settings route carries the same protection as the portal route, and a token read from either
    /// screen is honoured by either write.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// THE REASON THIS TEST EXISTS. Both portal write paths hand the same request interface to the same
    /// mapper and replace the same twenty-five columns, so they share one lost-update surface. Protecting
    /// only the route a report happened to exercise would move the defect to the sibling route rather than
    /// remove it - and the sibling route is the one the settings screen uses.
    /// </remarks>
    [Fact]
    public async Task UpdatePortalSettings_FromAStaleSnapshot_IsRefusedAndTheTwoPortalTokensAreInterchangeable()
    {
        using HttpClient host = await _fixture.CreateHostClientAsync();
        (PortalDetailDto created, CreatePortalRequest createRequest) = await CreatePortalWithRequestAsync(host);
        using HttpClient client = await CreateAdministratorClientForAsync(createRequest, created);
        var settingsRoute = new Uri(
            $"/api/v1/portals/{Route(created.PortalId)}/settings",
            UriKind.Relative);

        using HttpResponseMessage read = await client.GetAsync(settingsRoute);
        read.StatusCode.Should().Be(HttpStatusCode.OK);
        PortalSettingsDto snapshot = (await read.Content.ReadEnvelopeAsync<PortalSettingsDto>())!;
        snapshot.ConcurrencyToken.Should().NotBeNullOrWhiteSpace();

        // The two reads of the same unchanged record must agree, or a token obtained from one screen could
        // not be sent from the other.
        using HttpResponseMessage detailRead = await client.GetAsync(PortalRoute(created.PortalId));
        PortalDetailDto detail = await ReadDetailAsync(detailRead);
        detail.ConcurrencyToken.Should().Be(
            snapshot.ConcurrencyToken,
            "one derivation serves both reads, so the settings screen and the edit screen cannot disagree "
            + "about which revision they are looking at");

        UpdatePortalSettingsRequest first = SettingsUpdateFrom(snapshot);
        first.FooterText = "Settings committed first " + Suffix();
        using HttpResponseMessage firstSave = await client.PutAsJsonAsync(
            settingsRoute,
            first,
            ApiTestFixture.Json);
        firstSave.StatusCode.Should().Be(HttpStatusCode.OK);

        UpdatePortalSettingsRequest stale = SettingsUpdateFrom(snapshot);
        stale.Description = "Settings committed second " + Suffix();
        using HttpResponseMessage staleSave = await client.PutAsJsonAsync(
            settingsRoute,
            stale,
            ApiTestFixture.Json);

        staleSave.StatusCode.Should().Be(
            HttpStatusCode.Conflict,
            "the settings route replaces the same columns, so it must refuse a stale save too");

        ProblemDetails? problem = await staleSave.Content.ReadFromJsonAsync<ProblemDetails>();
        problem!.Type.Should().Contain("portal.concurrency_conflict");

        using HttpResponseMessage reread = await client.GetAsync(settingsRoute);
        PortalSettingsDto stored = (await reread.Content.ReadEnvelopeAsync<PortalSettingsDto>())!;
        stored.FooterText.Should().Be(first.FooterText);

        // INTERCHANGEABLE, PROVEN BOTH WAYS: a token read from the DETAIL screen satisfies the SETTINGS write.
        using HttpResponseMessage detailAfter = await client.GetAsync(PortalRoute(created.PortalId));
        PortalDetailDto detailToken = await ReadDetailAsync(detailAfter);

        UpdatePortalSettingsRequest crossed = SettingsUpdateFrom(stored);
        crossed.ConcurrencyToken = detailToken.ConcurrencyToken;
        crossed.KeyWords = "crossed, tokens";
        using HttpResponseMessage crossedSave = await client.PutAsJsonAsync(
            settingsRoute,
            crossed,
            ApiTestFixture.Json);

        crossedSave.StatusCode.Should().Be(
            HttpStatusCode.OK,
            "a token published by the detail read must satisfy the settings write, or this API has two "
            + "concurrency idioms rather than one");
    }

    /// <summary>
    /// Portal updates cannot inject an administrator membership or page reference owned by another tenant.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task UpdatePortal_RejectsForeignAdministratorAndPageReferences()
    {
        using HttpClient host = await _fixture.CreateHostClientAsync();
        PortalDetailDto victim = await CreatePortalAsync(host);
        PortalDetailDto foreign = await CreatePortalAsync(host);
        victim.AdministratorId.Should().NotBeNull();
        foreign.AdministratorId.Should().NotBeNull();
        foreign.HomeTabId.Should().NotBeNull();

        UpdatePortalRequest administratorInjection = EchoHostOnlyFields(victim);
        administratorInjection.PortalName = victim.PortalName;
        administratorInjection.AdministratorId = foreign.AdministratorId;

        using HttpResponseMessage foreignAdministrator = await host.PutAsJsonAsync(
            PortalRoute(victim.PortalId),
            administratorInjection,
            ApiTestFixture.Json);

        foreignAdministrator.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        UpdatePortalRequest pageInjection = EchoHostOnlyFields(victim);
        pageInjection.PortalName = victim.PortalName;
        pageInjection.HomeTabId = foreign.HomeTabId;

        using HttpResponseMessage foreignPage = await host.PutAsJsonAsync(
            PortalRoute(victim.PortalId),
            pageInjection,
            ApiTestFixture.Json);

        foreignPage.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        int administratorId = await _fixture.Database.ScalarAsync<int>(
            "SELECT [AdministratorId] FROM [dbo].[Portals] WHERE [PortalID] = @portalId;",
            new Dictionary<string, object?> { ["portalId"] = victim.PortalId });
        administratorId.Should().Be(victim.AdministratorId);

        int homeTabId = await _fixture.Database.ScalarAsync<int>(
            "SELECT [HomeTabId] FROM [dbo].[Portals] WHERE [PortalID] = @portalId;",
            new Dictionary<string, object?> { ["portalId"] = victim.PortalId });
        homeTabId.Should().Be(victim.HomeTabId);
    }

    /// <summary>
    /// The legacy processor-password column stores only managed-secret references and exposes explicit
    /// keep, replace and clear operations without echoing the reference.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task UpdatePortal_ProcessorReferenceSupportsKeepReplaceAndClearWithoutEcho()
    {
        using HttpClient host = await _fixture.CreateHostClientAsync();
        PortalDetailDto created = await CreatePortalAsync(host);
        const string legacyPlaintext = "legacy-plaintext-password";
        const string original = "secret://processor/original";
        const string replacement = "secret://processor/replacement";

        await _fixture.Database.ExecuteAsync(
            """
            UPDATE [dbo].[Portals]
            SET [ProcessorPassword] = @reference
            WHERE [PortalID] = @portalId;
            """,
            new Dictionary<string, object?>
            {
                ["reference"] = legacyPlaintext,
                ["portalId"] = created.PortalId,
            });

        UpdatePortalRequest refuseLegacyKeep = EchoHostOnlyFields(created);
        refuseLegacyKeep.PortalName = created.PortalName;
        refuseLegacyKeep.ProcessorCredentialReference = null;

        using HttpResponseMessage legacyKeep = await host.PutAsJsonAsync(
            PortalRoute(created.PortalId),
            refuseLegacyKeep,
            ApiTestFixture.Json);
        legacyKeep.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        UpdatePortalRequest migrate = EchoHostOnlyFields(created);
        migrate.PortalName = created.PortalName;
        migrate.ProcessorCredentialReference = original;

        using HttpResponseMessage migrated = await host.PutAsJsonAsync(
            PortalRoute(created.PortalId),
            migrate,
            ApiTestFixture.Json);
        migrated.StatusCode.Should().Be(HttpStatusCode.OK);

        string afterMigration = await _fixture.Database.ScalarAsync<string>(
            "SELECT [ProcessorPassword] FROM [dbo].[Portals] WHERE [PortalID] = @portalId;",
            new Dictionary<string, object?> { ["portalId"] = created.PortalId });
        afterMigration.Should().Be(original);

        UpdatePortalRequest keep = EchoHostOnlyFields(created);
        keep.PortalName = created.PortalName;
        keep.ProcessorCredentialReference = null;

        using HttpResponseMessage kept = await host.PutAsJsonAsync(
            PortalRoute(created.PortalId),
            keep,
            ApiTestFixture.Json);
        kept.StatusCode.Should().Be(HttpStatusCode.OK);

        string afterKeep = await _fixture.Database.ScalarAsync<string>(
            "SELECT [ProcessorPassword] FROM [dbo].[Portals] WHERE [PortalID] = @portalId;",
            new Dictionary<string, object?> { ["portalId"] = created.PortalId });
        afterKeep.Should().Be(original);

        UpdatePortalRequest replace = EchoHostOnlyFields(created);
        replace.PortalName = created.PortalName;
        replace.ProcessorCredentialReference = replacement;

        using HttpResponseMessage replaced = await host.PutAsJsonAsync(
            PortalRoute(created.PortalId),
            replace,
            ApiTestFixture.Json);
        replaced.StatusCode.Should().Be(HttpStatusCode.OK);

        using JsonDocument body = JsonDocument.Parse(await replaced.Content.ReadAsStringAsync());
        JsonElement data = body.RootElement.GetProperty("data");
        data.TryGetProperty("processorCredentialReference", out _).Should().BeFalse();
        data.TryGetProperty("processorPassword", out _).Should().BeFalse();

        string afterReplace = await _fixture.Database.ScalarAsync<string>(
            "SELECT [ProcessorPassword] FROM [dbo].[Portals] WHERE [PortalID] = @portalId;",
            new Dictionary<string, object?> { ["portalId"] = created.PortalId });
        afterReplace.Should().Be(replacement);

        UpdatePortalRequest rejectPlaintext = EchoHostOnlyFields(created);
        rejectPlaintext.PortalName = created.PortalName;
        rejectPlaintext.ProcessorCredentialReference = "plaintext-password";

        using HttpResponseMessage rejected = await host.PutAsJsonAsync(
            PortalRoute(created.PortalId),
            rejectPlaintext,
            ApiTestFixture.Json);
        rejected.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        string afterRejectedPlaintext = await _fixture.Database.ScalarAsync<string>(
            "SELECT [ProcessorPassword] FROM [dbo].[Portals] WHERE [PortalID] = @portalId;",
            new Dictionary<string, object?> { ["portalId"] = created.PortalId });
        afterRejectedPlaintext.Should().Be(replacement);

        UpdatePortalRequest clear = EchoHostOnlyFields(created);
        clear.PortalName = created.PortalName;
        clear.ProcessorCredentialReference = string.Empty;

        using HttpResponseMessage cleared = await host.PutAsJsonAsync(
            PortalRoute(created.PortalId),
            clear,
            ApiTestFixture.Json);
        cleared.StatusCode.Should().Be(HttpStatusCode.OK);

        int nullCount = await _fixture.Database.ScalarAsync<int>(
            """
            SELECT COUNT(*)
            FROM [dbo].[Portals]
            WHERE [PortalID] = @portalId AND [ProcessorPassword] IS NULL;
            """,
            new Dictionary<string, object?> { ["portalId"] = created.PortalId });
        nullCount.Should().Be(1);
    }

    /// <summary>
    /// A portal administrator that submits a different hosting charge is refused. The charge is a
    /// host-account concern: a tenant administrator that could raise or waive it would be setting the price
    /// of its own hosting.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// The caller is the administrator of the portal being updated, so the refusal can only come from the
    /// host-only guard.
    /// </remarks>
    [Fact]
    public async Task UpdatePortal_AsAdministratorAlteringHostingCharge_ReturnsForbidden()
    {
        using HttpClient host = await _fixture.CreateHostClientAsync();
        (PortalDetailDto created, CreatePortalRequest createRequest) = await CreatePortalWithRequestAsync(host);

        using HttpClient client = await CreateAdministratorClientForAsync(createRequest, created);

        // The control, and the whole reason this refusal can be attributed to the field guard: the same client
        // and the same token are admitted by the endpoint policy for an update that alters no host-only field.
        UpdatePortalRequest permitted = EchoHostOnlyFields(created);
        permitted.PortalName = created.PortalName;

        using HttpResponseMessage admitted = await client.PutAsJsonAsync(
            PortalRoute(created.PortalId),
            permitted,
            ApiTestFixture.Json);

        admitted.StatusCode.Should().Be(
            HttpStatusCode.OK,
            "the endpoint policy admits this caller, so any refusal below is the field guard's and not the "
            + "policy's");

        UpdatePortalRequest request = EchoHostOnlyFields(created);
        request.PortalName = created.PortalName;
        request.HostFee = (created.HostFee ?? 0m) + 250.50m;

        using HttpResponseMessage response = await client.PutAsJsonAsync(
            PortalRoute(created.PortalId),
            request,
            ApiTestFixture.Json);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);

        ProblemDetails? problem = await response.Content
            .ReadFromJsonAsync<ProblemDetails>(ApiTestFixture.Json);

        problem.Should().NotBeNull();
        problem!.Type.Should().Be(
            AuthorisationRefusalProblemType,
            "an unentitled caller is refused under this API's single problem taxonomy, whichever guard decided "
            + "it");
    }

    /// <summary>
    /// A current portal administrator does not gain host-only field access from a stale super-user claim.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// route, token and arrival tenant all agree, and the account holds the target portal's stored
    /// administrator role. The only false statement is the token's super-user claim; the account row
    /// remains non-host, so the application guard must refuse after authorisation has succeeded.
    /// </remarks>
    [Fact]
    public async Task UpdatePortal_WithOnlyAStaleSuperUserClaim_ReturnsForbidden()
    {
        using HttpClient host = await _fixture.CreateHostClientAsync();
        (PortalDetailDto created, CreatePortalRequest createRequest) = await CreatePortalWithRequestAsync(host);
        created.AdministratorId.Should().NotBeNull();

        using HttpClient staleClaim = _fixture.CreateTenantClient(
            createRequest.PortalAlias!,
            created.PortalId,
            created.AdministratorId!.Value,
            createRequest.AdministratorUsername!,
            isSuperUser: true);

        // The control. This token is admitted by the endpoint policy - which is precisely why the stale
        // super-user claim it carries has to be refused by something else, and by the store rather than the
        // claim.
        UpdatePortalRequest permitted = EchoHostOnlyFields(created);
        permitted.PortalName = created.PortalName;

        using HttpResponseMessage admitted = await staleClaim.PutAsJsonAsync(
            PortalRoute(created.PortalId),
            permitted,
            ApiTestFixture.Json);

        admitted.StatusCode.Should().Be(
            HttpStatusCode.OK,
            "authorisation succeeds for this token, so the refusal below is the store-backed field guard's");

        UpdatePortalRequest request = EchoHostOnlyFields(created);
        request.PortalName = created.PortalName;
        request.HostFee = (created.HostFee ?? 0m) + 10m;

        using HttpResponseMessage response = await staleClaim.PutAsJsonAsync(
            PortalRoute(created.PortalId),
            request,
            ApiTestFixture.Json);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);

        ProblemDetails? problem = await response.Content
            .ReadFromJsonAsync<ProblemDetails>(ApiTestFixture.Json);
        problem.Should().NotBeNull();
        problem!.Type.Should().Be(
            AuthorisationRefusalProblemType,
            "an unentitled caller is refused under this API's single problem taxonomy, whichever guard decided "
            + "it");
    }

    /// <summary>
    /// A super-user claim on portal A's token does not admit that non-host account to portal B's route.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// meet here: the caller is a real administrator of portal A and the token says it is a host account,
    /// but the store says otherwise. The claim therefore cannot become the host exemption that would let an
    /// A-token cross onto an existing B-route.
    /// </remarks>
    [Fact]
    public async Task UpdatePortal_WithASuperUserClaimFromAnotherTenant_ReturnsForbidden()
    {
        using HttpClient host = await _fixture.CreateHostClientAsync();
        PortalDetailDto victim = await CreatePortalAsync(host);

        using HttpClient staleClaim = _fixture.CreateClientFor(
            _fixture.Seed.AdminUserId,
            IntegrationSeed.AdminUserName,
            _fixture.Seed.PortalId,
            isSuperUser: true,
            roles: [IntegrationSeed.AdministratorsRoleName]);

        UpdatePortalRequest request = EchoHostOnlyFields(victim);
        request.PortalName = victim.PortalName;
        request.HostFee = (victim.HostFee ?? 0m) + 10m;

        using HttpResponseMessage response = await staleClaim.PutAsJsonAsync(
            PortalRoute(victim.PortalId),
            request,
            ApiTestFixture.Json);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);

        ProblemDetails? problem = await response.Content
            .ReadFromJsonAsync<ProblemDetails>(ApiTestFixture.Json);
        problem.Should().NotBeNull();
        problem!.Type.Should().Be(
            AuthorisationRefusalProblemType,
            "an unentitled caller is refused under this API's single problem taxonomy");

        // The discriminator, and the mirror image of the control used by the field-guard facts above. The
        // submission it refused altered a host-only field, which the field guard would have refused on its
        // own - so that refusal alone cannot show the endpoint policy fired.
        UpdatePortalRequest nothingHostOnly = EchoHostOnlyFields(victim);
        nothingHostOnly.PortalName = victim.PortalName;

        using HttpResponseMessage alsoRefused = await staleClaim.PutAsJsonAsync(
            PortalRoute(victim.PortalId),
            nothingHostOnly,
            ApiTestFixture.Json);

        alsoRefused.StatusCode.Should().Be(
            HttpStatusCode.Forbidden,
            "the endpoint policy refuses this caller on this route outright, so it is refused even for an "
            + "update the host-only field guard would have allowed");
    }

    /// <summary>An administrator of one portal is refused on another portal's route.</summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task UpdatePortal_ByAnAdministratorOfADifferentPortal_ReturnsForbidden()
    {
        using HttpClient host = await _fixture.CreateHostClientAsync();
        PortalDetailDto victim = await CreatePortalAsync(host);

        // The seeded portal's administrator: a genuine, fully provisioned administrator - of a DIFFERENT
        // portal. Nothing about this caller is malformed, which is what made the defect reachable.
        using HttpClient attacker = await _fixture.CreateAdministratorClientAsync();

        UpdatePortalRequest request = EchoHostOnlyFields(victim);
        request.PortalName = "Taken over " + Suffix();

        using HttpResponseMessage response = await attacker.PutAsJsonAsync(
            PortalRoute(victim.PortalId),
            request,
            ApiTestFixture.Json);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);

        ProblemDetails? problem = await response.Content
            .ReadFromJsonAsync<ProblemDetails>(ApiTestFixture.Json);

        problem.Should().NotBeNull(
            "an authorisation refusal must carry a problem document rather than an empty body");
        problem!.Status.Should().Be(StatusCodes.Status403Forbidden);

        // This submission alters no host-only field, so the field guard would have permitted it. The refusal is
        // therefore the endpoint policy's, which is the cross-tenant property under test.
        problem.Type.Should().Be(AuthorisationRefusalProblemType);

        // The refusal must also be a refusal to ACT, not merely a refusal to answer: re-read as the host and
        // confirm the name never changed.
        using HttpResponseMessage reread = await host.GetAsync(PortalRoute(victim.PortalId));
        reread.StatusCode.Should().Be(HttpStatusCode.OK);
        PortalDetailDto persisted = await ReadDetailAsync(reread);
        persisted.PortalName.Should().Be(victim.PortalName);
    }

    /// <summary>
    /// An administrator of one portal is refused even a READ of another portal, and the refusal is a
    /// <c>403</c> rather than the <c>404</c> an unauthorised caller would otherwise be able to use to
    /// enumerate which portal identifiers exist.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task GetPortal_ByAnAdministratorOfADifferentPortal_ReturnsForbidden()
    {
        using HttpClient host = await _fixture.CreateHostClientAsync();
        PortalDetailDto other = await CreatePortalAsync(host);

        using HttpClient client = await _fixture.CreateAdministratorClientAsync();

        using HttpResponseMessage response = await client.GetAsync(PortalRoute(other.PortalId));

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);

        using HttpResponseMessage unknown = await client.GetAsync(PortalRoute(UnknownPortalId));

        unknown.StatusCode.Should().Be(
            HttpStatusCode.Forbidden,
            "a portal that exists and one that does not must be indistinguishable to a caller entitled to neither");
    }

    /// <summary>
    /// The collection-wide read is refused to a portal administrator, because it enumerates every tenant in
    /// the installation and therefore belongs to a host account.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task ListPortals_AsPortalAdministrator_ReturnsForbidden()
    {
        using HttpClient client = await _fixture.CreateAdministratorClientAsync();

        using HttpResponseMessage response = await client.GetAsync(
            new Uri("/api/v1/portals?pageIndex=0&pageSize=100", UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    /// <summary>
    /// Provisioning a new tenant is refused to a portal administrator for the same reason: a create names
    /// no portal it could be authorised against, and the operation adds a tenant to the installation.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task CreatePortal_AsPortalAdministrator_ReturnsForbidden()
    {
        using HttpClient client = await _fixture.CreateAdministratorClientAsync();

        CreatePortalRequest request = NewPortalRequest();

        using HttpResponseMessage response = await client.PostAsJsonAsync(
            PortalsRoute,
            request,
            ApiTestFixture.Json);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);

        int aliasCount = await _fixture.Database.ScalarAsync<int>(
            "SELECT COUNT(*) FROM [dbo].[PortalAlias] WHERE [HTTPAlias] = @alias;",
            new Dictionary<string, object?> { ["alias"] = request.PortalAlias });

        aliasCount.Should().Be(0, "a refused create must write nothing");
    }

    /// <summary>
    /// A portal administrator writing to a portal other than its own is refused. A read being isolated is
    /// worth little if the write beside it is not.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// The host-only fields are echoed from the representation just read, so the request alters nothing a
    /// tenant administrator is barred from altering. The only objectionable thing about it is the tenant it
    /// names, which is what makes the refusal attributable to the route-tenant reconciliation.
    /// </remarks>
    [Fact]
    public async Task UpdatePortal_AsAdministratorOfAnotherTenant_ReturnsForbidden()
    {
        using HttpClient host = await _fixture.CreateHostClientAsync();
        PortalDetailDto other = await CreatePortalAsync(host);

        using HttpClient client = await _fixture.CreateAdministratorClientAsync();

        UpdatePortalRequest request = EchoHostOnlyFields(other);
        request.PortalName = other.PortalName;

        using HttpResponseMessage response = await client.PutAsJsonAsync(
            PortalRoute(other.PortalId),
            request,
            ApiTestFixture.Json);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    /// <summary>A host account may change the hosting charge, which is the other half of the same rule.</summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task UpdatePortal_AsHostAlteringHostingCharge_ReturnsOk()
    {
        using HttpClient host = await _fixture.CreateHostClientAsync();
        PortalDetailDto created = await CreatePortalAsync(host);

        UpdatePortalRequest request = EchoHostOnlyFields(created);
        request.PortalName = created.PortalName;
        request.HostFee = 42.75m;
        request.HostSpace = 128;
        request.PageQuota = 25;
        request.UserQuota = 50;

        using HttpResponseMessage response = await host.PutAsJsonAsync(
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

    /// <summary>
    /// An update naming a tenant other than the one the request resolved to is refused with <c>403
    /// Forbidden</c>, and an unknown identifier is refused identically.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task UpdatePortal_ForATenantOtherThanTheResolvedOne_ReturnsForbidden()
    {
        using HttpClient host = await _fixture.CreateHostClientAsync();
        PortalDetailDto foreign = await CreatePortalAsync(host);

        // Addressed at the seeded tenant, as its own administrator, naming another tenant on the route.
        using HttpClient client = await _fixture.CreateAdministratorClientAsync();

        var request = new UpdatePortalRequest
        {
            PortalName = "Renamed from another tenant " + Suffix(),
        };

        using HttpResponseMessage existing = await client.PutAsJsonAsync(
            PortalRoute(foreign.PortalId),
            request,
            ApiTestFixture.Json);

        existing.StatusCode.Should().Be(HttpStatusCode.Forbidden);

        using HttpResponseMessage absent = await client.PutAsJsonAsync(
            PortalRoute(UnknownPortalId),
            request,
            ApiTestFixture.Json);

        absent.StatusCode.Should().Be(HttpStatusCode.Forbidden);

        // The write must not have landed either, which is the property the status code alone does not prove.
        string persisted = await _fixture.Database.ScalarAsync<string>(
            "SELECT [PortalName] FROM [dbo].[Portals] WHERE [PortalID] = @portalId;",
            new Dictionary<string, object?> { ["portalId"] = foreign.PortalId });

        persisted.Should().Be(foreign.PortalName);
    }

    /// <summary>
    /// An update whose body names a different portal from the route is refused by the request validator, so
    /// a caller cannot retarget the write at another tenant.
    /// </summary>
    /// <remarks>
    /// The contract carries the portal identifier as the first of its twenty-seven members, matching
    /// argument 1 of the legacy <c>UpdatePortalInfo</c> signature. The route remains the subject of the
    /// write, and this rule is what makes the second copy harmless: a body identifier that cannot disagree
    /// with the route cannot address a portal the route did not name.
    /// </remarks>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task UpdatePortal_WithMismatchedBodyIdentifier_ReturnsBadRequest()
    {
        using HttpClient client = await _fixture.CreateHostClientAsync();

        var request = new UpdatePortalRequest
        {
            PortalId = UnknownPortalId,
            PortalName = "Retargeted",
        };

        using HttpResponseMessage response = await client.PutAsJsonAsync(
            PortalRoute(_fixture.Seed.PortalId),
            request,
            ApiTestFixture.Json);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    /// <summary>
    /// An update carrying no portal name is ACCEPTED and stores the empty string, because that is what the
    /// legacy screen and the legacy write path did.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task UpdatePortal_WithoutAName_IsAcceptedAndStoresTheEmptyString()
    {
        using HttpClient host = await _fixture.CreateHostClientAsync();
        (PortalDetailDto created, CreatePortalRequest createRequest) = await CreatePortalWithRequestAsync(host);

        using HttpClient client = await CreateAdministratorClientForAsync(createRequest, created);

        UpdatePortalRequest request = EchoHostOnlyFields(created);
        request.PortalName = string.Empty;

        using HttpResponseMessage response = await client.PutAsJsonAsync(
            PortalRoute(created.PortalId),
            request,
            ApiTestFixture.Json);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        PortalDetailDto updated = await ReadDetailAsync(response);
        updated.PortalName.Should().BeEmpty();

        // Read back, because the echo could in principle be composed rather than read, and stored is what
        // this test is about.
        using HttpResponseMessage reread = await client.GetAsync(PortalRoute(created.PortalId));
        reread.StatusCode.Should().Be(HttpStatusCode.OK);

        PortalDetailDto persisted = await ReadDetailAsync(reread);
        persisted.PortalName.Should().BeEmpty();

        // Against the column itself, so the assertion does not depend on the read projection. The legacy
        // stored value for a blank title is the empty string in a NOT NULL column - never a null, and never
        // a substituted placeholder.
        string storedName = await _fixture.Database.ScalarAsync<string>(
            "SELECT [PortalName] FROM [dbo].[Portals] WHERE [PortalID] = @portalId;",
            new Dictionary<string, object?> { ["portalId"] = created.PortalId });

        storedName.Should().BeEmpty();
    }

    /// <summary>
    /// A null portal name is accepted too, and is stored as the empty string rather than as a null.
    /// </summary>
    /// <remarks>
    /// The contract member is <c>string?</c>, and the portal settings screen composes every optional text
    /// member through one rule - a blank box sends absence - so this is the shape that screen actually
    /// transmits for a cleared title. <c>PortalMappings</c> writes <c>request.PortalName ??
    /// string.Empty</c>, which is what keeps a nullable request member compatible with a NOT NULL column
    /// and what makes the stored outcome identical to the empty-string case above.
    /// </remarks>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task UpdatePortal_WithANullName_IsAcceptedAndStoresTheEmptyString()
    {
        using HttpClient host = await _fixture.CreateHostClientAsync();
        (PortalDetailDto created, CreatePortalRequest createRequest) = await CreatePortalWithRequestAsync(host);

        using HttpClient client = await CreateAdministratorClientForAsync(createRequest, created);

        UpdatePortalRequest request = EchoHostOnlyFields(created);
        request.PortalName = null;

        using HttpResponseMessage response = await client.PutAsJsonAsync(
            PortalRoute(created.PortalId),
            request,
            ApiTestFixture.Json);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        PortalDetailDto updated = await ReadDetailAsync(response);
        updated.PortalName.Should().BeEmpty();

        string storedName = await _fixture.Database.ScalarAsync<string>(
            "SELECT [PortalName] FROM [dbo].[Portals] WHERE [PortalID] = @portalId;",
            new Dictionary<string, object?> { ["portalId"] = created.PortalId });

        storedName.Should().BeEmpty();
    }

    /// <summary>
    /// A delete answers <c>204 No Content</c>, the portal is then unreachable, and its alias is released so
    /// the host name can be bound again.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task DeletePortal_ReturnsNoContentAndReleasesAlias()
    {
        using HttpClient client = await _fixture.CreateHostClientAsync();
        PortalDetailDto created = await CreatePortalAsync(client);

        string alias = created.Aliases!.Single().HttpAlias!;

        using HttpResponseMessage response = await client.DeleteAsync(PortalRoute(created.PortalId));

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);

        // Unreachability is proved by deleting again rather than by reading back. A read is tenant-scoped,
        // so after the alias is released nothing resolves to the removed tenant and the read is refused in
        // authorisation - a 403 that says nothing about whether the row is gone.
        using HttpResponseMessage deletedAgain = await client.DeleteAsync(PortalRoute(created.PortalId));
        deletedAgain.StatusCode.Should().Be(HttpStatusCode.NotFound);

        int portalCount = await _fixture.Database.ScalarAsync<int>(
            "SELECT COUNT(*) FROM [dbo].[Portals] WHERE [PortalID] = @portalId;",
            new Dictionary<string, object?> { ["portalId"] = created.PortalId });

        portalCount.Should().Be(0);

        int aliasCount = await _fixture.Database.ScalarAsync<int>(
            "SELECT COUNT(*) FROM [dbo].[PortalAlias] WHERE [HTTPAlias] = @alias;",
            new Dictionary<string, object?> { ["alias"] = alias });

        aliasCount.Should().Be(0);
    }

    /// <summary>
    /// A delete answers <c>204 No Content</c> for a tenant that owns modules, and leaves neither the
    /// modules nor their placements, settings or grants behind.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// THE ONE FOREIGN KEY INTO <c>dbo.Portals</c> WITHOUT A CASCADE IS <c>FK_Modules_Portals</c>. Every
    /// other one - alias, portal desktop module, role group, role, page, membership, profile declaration -
    /// carries <c>ON DELETE CASCADE</c>, so a tenant with no module deletes cleanly and a tenant with one
    /// module was refused by the store.
    /// </remarks>
    [Fact]
    public async Task DeletePortal_WhenTheTenantOwnsModules_ReturnsNoContentAndLeavesNoOrphans()
    {
        using HttpClient client = await _fixture.CreateHostClientAsync();
        PortalDetailDto created = await CreatePortalAsync(client);

        int tabId = await _fixture.Database.ScalarAsync<int>(
            "SELECT MIN([TabID]) FROM [dbo].[Tabs] WHERE [PortalID] = @portalId;",
            new Dictionary<string, object?> { ["portalId"] = created.PortalId });

        int moduleId = await _fixture.Database.ScalarAsync<int>(
            """
            INSERT INTO [dbo].[Modules]
                ([ModuleDefID], [PortalID], [ModuleTitle], [AllTabs], [IsDeleted], [InheritViewPermissions])
            VALUES (@moduleDefinitionId, @portalId, N'Cascade guard module', 0, 0, 0);
            SELECT CAST(SCOPE_IDENTITY() AS int);
            """,
            new Dictionary<string, object?>
            {
                ["moduleDefinitionId"] = _fixture.Seed.ModuleDefinitionId,
                ["portalId"] = created.PortalId,
            });

        int tabModuleId = await _fixture.Database.ScalarAsync<int>(
            """
            INSERT INTO [dbo].[TabModules]
                ([TabID], [ModuleID], [PaneName], [ModuleOrder], [CacheTime], [Visibility], [DisplayTitle],
                 [DisplayPrint], [DisplaySyndicate])
            VALUES (@tabId, @moduleId, N'ContentPane', 1, 0, 0, 1, 0, 0);
            SELECT CAST(SCOPE_IDENTITY() AS int);
            """,
            new Dictionary<string, object?> { ["tabId"] = tabId, ["moduleId"] = moduleId });

        await _fixture.Database.ExecuteAsync(
            """
            INSERT INTO [dbo].[ModuleSettings] ([ModuleID], [SettingName], [SettingValue])
            VALUES (@moduleId, N'CascadeGuard', N'1');
            INSERT INTO [dbo].[TabModuleSettings] ([TabModuleID], [SettingName], [SettingValue])
            VALUES (@tabModuleId, N'CascadeGuard', N'1');
            INSERT INTO [dbo].[ModulePermission]
                ([ModuleID], [PermissionID], [RoleID], [UserID], [AllowAccess])
            VALUES (@moduleId, @permissionId, @roleId, NULL, 1);
            """,
            new Dictionary<string, object?>
            {
                ["moduleId"] = moduleId,
                ["tabModuleId"] = tabModuleId,
                ["permissionId"] = _fixture.Seed.ModuleViewPermissionId,
                ["roleId"] = _fixture.Seed.RegisteredRoleId,
            });

        using HttpResponseMessage response = await client.DeleteAsync(PortalRoute(created.PortalId));

        response.StatusCode.Should().Be(
            HttpStatusCode.NoContent,
            "the tenant's modules are removed before its own row, so the store has nothing left to refuse");

        (await _fixture.Database.ScalarAsync<int>(
            "SELECT COUNT(*) FROM [dbo].[Portals] WHERE [PortalID] = @portalId;",
            new Dictionary<string, object?> { ["portalId"] = created.PortalId }))
            .Should().Be(0);

        (await _fixture.Database.ScalarAsync<int>(
            "SELECT COUNT(*) FROM [dbo].[Modules] WHERE [PortalID] = @portalId;",
            new Dictionary<string, object?> { ["portalId"] = created.PortalId }))
            .Should().Be(0, "no module may outlive the tenant that owned it");

        (await _fixture.Database.ScalarAsync<int>(
            "SELECT COUNT(*) FROM [dbo].[TabModules] WHERE [ModuleID] = @moduleId;",
            new Dictionary<string, object?> { ["moduleId"] = moduleId }))
            .Should().Be(0, "FK_TabModules_Modules cascades, so the placement goes with the module");

        (await _fixture.Database.ScalarAsync<int>(
            "SELECT COUNT(*) FROM [dbo].[ModuleSettings] WHERE [ModuleID] = @moduleId;",
            new Dictionary<string, object?> { ["moduleId"] = moduleId }))
            .Should().Be(0, "FK_ModuleSettings_Modules cascades");

        (await _fixture.Database.ScalarAsync<int>(
            "SELECT COUNT(*) FROM [dbo].[TabModuleSettings] WHERE [TabModuleID] = @tabModuleId;",
            new Dictionary<string, object?> { ["tabModuleId"] = tabModuleId }))
            .Should().Be(0, "FK_TabModuleSettings_TabModules cascades from the placement");

        (await _fixture.Database.ScalarAsync<int>(
            "SELECT COUNT(*) FROM [dbo].[ModulePermission] WHERE [ModuleID] = @moduleId;",
            new Dictionary<string, object?> { ["moduleId"] = moduleId }))
            .Should().Be(0, "FK_ModulePermission_Modules cascades");
    }

    /// <summary>
    /// Removing a tenant deletes a final-membership administrator and every external credential and session
    /// that would otherwise outlive it, while an account shared with another tenant survives with that
    /// other membership intact.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task DeletePortal_RemovesFinalMembersButRetainsSharedAccounts()
    {
        using HttpClient host = await _fixture.CreateHostClientAsync();
        (PortalDetailDto created, CreatePortalRequest createRequest) =
            await CreatePortalWithRequestAsync(host);

        created.AdministratorId.Should().NotBeNull();
        created.RegisteredRoleId.Should().NotBeNull();
        string alias = created.Aliases!.Single().HttpAlias!;

        await _fixture.Database.ExecuteAsync(
            """
            INSERT INTO [dbo].[UserPortals] ([UserId], [PortalId], [CreatedDate], [Authorised])
            VALUES (@userId, @portalId, SYSUTCDATETIME(), 1);

            INSERT INTO [dbo].[UserRoles] ([UserID], [RoleID], [EffectiveDate], [ExpiryDate], [IsTrialUsed])
            VALUES (@userId, @roleId, NULL, NULL, 0);
            """,
            new Dictionary<string, object?>
            {
                ["userId"] = _fixture.Seed.MemberUserId,
                ["portalId"] = created.PortalId,
                ["roleId"] = created.RegisteredRoleId!.Value,
            });

        using HttpClient portalClient = _fixture.CreateAnonymousClient();
        portalClient.BaseAddress = new Uri($"http://{alias}", UriKind.Absolute);

        using HttpResponseMessage login = await portalClient.PostAsJsonAsync(
            new Uri("/api/v1/auth/login", UriKind.Relative),
            new LoginRequest
            {
                Username = createRequest.AdministratorUsername!,
                Password = createRequest.AdministratorPassword!,
            },
            ApiTestFixture.Json);

        login.StatusCode.Should().Be(HttpStatusCode.OK);
        LoginResponse? issued = await login.Content.ReadEnvelopeAsync<LoginResponse>();
        issued.Should().NotBeNull();
        issued!.RefreshToken.Should().NotBeNullOrWhiteSpace();

        using HttpResponseMessage removed = await host.DeleteAsync(PortalRoute(created.PortalId));
        removed.StatusCode.Should().Be(HttpStatusCode.NoContent);

        using HttpResponseMessage staleSession = await portalClient.PostAsJsonAsync(
            new Uri("/api/v1/auth/refresh", UriKind.Relative),
            new RefreshTokenRequest { RefreshToken = issued.RefreshToken },
            ApiTestFixture.Json);
        staleSession.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        int removedAdministrator = await _fixture.Database.ScalarAsync<int>(
            "SELECT COUNT(*) FROM [dbo].[Users] WHERE [UserID] = @userId;",
            new Dictionary<string, object?> { ["userId"] = created.AdministratorId!.Value });
        removedAdministrator.Should().Be(0);

        int removedCredential = await _fixture.Database.ScalarAsync<int>(
            """
            SELECT COUNT(*)
            FROM [dbo].[aspnet_Users] au
            INNER JOIN [dbo].[aspnet_Membership] am ON am.[UserId] = au.[UserId]
            WHERE au.[LoweredUserName] = LOWER(@userName);
            """,
            new Dictionary<string, object?>
            {
                ["userName"] = createRequest.AdministratorUsername,
            });
        removedCredential.Should().Be(0);

        int removedMemberships = await _fixture.Database.ScalarAsync<int>(
            "SELECT COUNT(*) FROM [dbo].[UserPortals] WHERE [PortalId] = @portalId;",
            new Dictionary<string, object?> { ["portalId"] = created.PortalId });
        removedMemberships.Should().Be(0);

        int removedRoles = await _fixture.Database.ScalarAsync<int>(
            "SELECT COUNT(*) FROM [dbo].[Roles] WHERE [PortalID] = @portalId;",
            new Dictionary<string, object?> { ["portalId"] = created.PortalId });
        removedRoles.Should().Be(0);

        int retainedSharedAccount = await _fixture.Database.ScalarAsync<int>(
            "SELECT COUNT(*) FROM [dbo].[Users] WHERE [UserID] = @userId;",
            new Dictionary<string, object?> { ["userId"] = _fixture.Seed.MemberUserId });
        retainedSharedAccount.Should().Be(1);

        int retainedSharedMembership = await _fixture.Database.ScalarAsync<int>(
            """
            SELECT COUNT(*)
            FROM [dbo].[UserPortals]
            WHERE [UserId] = @userId AND [PortalId] = @portalId;
            """,
            new Dictionary<string, object?>
            {
                ["userId"] = _fixture.Seed.MemberUserId,
                ["portalId"] = _fixture.Seed.PortalId,
            });
        retainedSharedMembership.Should().Be(1);

        int retainedSharedCredential = await _fixture.Database.ScalarAsync<int>(
            """
            SELECT COUNT(*)
            FROM [dbo].[aspnet_Users] au
            INNER JOIN [dbo].[aspnet_Membership] am ON am.[UserId] = au.[UserId]
            WHERE au.[LoweredUserName] = LOWER(@userName);
            """,
            new Dictionary<string, object?>
            {
                ["userName"] = IntegrationSeed.MemberUserName,
            });
        retainedSharedCredential.Should().Be(1);
    }

    /// <summary>
    /// A portal administrator may not delete a portal - not even its own - because bringing a tenant into
    /// existence and removing it again are installation-wide acts.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task DeletePortal_AsPortalAdministrator_ReturnsForbidden()
    {
        using HttpClient client = await _fixture.CreateAdministratorClientAsync();

        using HttpResponseMessage response = await client.DeleteAsync(
            PortalRoute(_fixture.Seed.PortalId));

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);

        // The refusal must be a refusal, not a delayed success: the tenant is still there afterwards.
        int portalCount = await _fixture.Database.ScalarAsync<int>(
            "SELECT COUNT(*) FROM [dbo].[Portals] WHERE [PortalID] = @portalId;",
            new Dictionary<string, object?> { ["portalId"] = _fixture.Seed.PortalId });

        portalCount.Should().Be(1);
    }

    /// <summary>
    /// The last-portal refusal is a conflict according to the SHARED status table, not according to a
    /// branch inside the delete action.
    /// </summary>
    /// <remarks>
    /// What could regress here is precise: the endpoint no longer names the failure code at all, so if the
    /// shared table stopped recognising it the refusal would silently become <c>400</c> - telling a caller
    /// to correct a request that is not correctable. Reading the table costs no database and no host, so it
    /// reports that specific regression immediately rather than only as part of a longer end-to-end fact.
    /// </remarks>
    [Fact]
    public void DeletePortal_LastRemainingRefusal_IsTranslatedAsConflictByTheSharedTable()
    {
        ApiResults.MapStatusCode("portal.last_remaining")
            .Should().Be(StatusCodes.Status409Conflict);
    }

    /// <summary>A delete against an unknown identifier answers <c>404 Not Found</c>.</summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// The legacy removal reported an unknown portal as a SUCCESS. It looked the portal up, and when the
    /// lookup produced nothing it fell through to the end of the routine and returned the empty string -
    /// which was its success value, the only non-empty message it ever produced being the last-portal
    /// refusal.
    /// </remarks>
    [Fact]
    public async Task DeletePortal_WhenUnknown_ReturnsNotFound()
    {
        using HttpClient client = await _fixture.CreateHostClientAsync();

        using HttpResponseMessage response = await client.DeleteAsync(PortalRoute(UnknownPortalId));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    /// <summary>
    /// The alias sub-resource supports the full round trip: a create answers <c>201 Created</c>, an update
    /// and a delete each answer <c>204 No Content</c>, and the collection reflects each step.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <summary>
    /// A submitted host name is stored in lower case, on creation and on replacement alike.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Lower case is the legacy rule, not a preference.</b> Every path in
    /// <c>Library/Components/Portal/PortalAliasController.vb</c> that touched an alias applied
    /// <c>.ToLower</c> - <c>AddPortalAlias</c> at L31, <c>UpdatePortalAliasInfo</c> at L97, and both read
    /// paths at L52 and L76 - so a DotNetNuke installation never held a mixed-case alias. This service
    /// trimmed and nothing more, so <c>WWW.Example.Test</c> was stored as written.
    /// </para>
    /// <para>
    /// <b>Why it matters more than casing usually does.</b> An alias is the ONLY thing that resolves an
    /// incoming request to a tenant, so two spellings of one host name is a tenant-resolution difference.
    /// </para>
    /// <para>
    /// Asserted on the RESPONSE and again on the STORED row, because a service that lower-cased only its
    /// return value would satisfy the first on its own.
    /// </para>
    /// </remarks>
    /// <returns>A task representing the test.</returns>
    [Fact]
    [Trait("Category", "Integration")]
    public async Task PortalAliases_StoreTheHostNameInLowerCase()
    {
        using HttpClient host = await _fixture.CreateHostClientAsync();
        (PortalDetailDto created, CreatePortalRequest createRequest) =
            await CreatePortalWithRequestAsync(host);

        using HttpClient client = await CreatedTenantClientAsync(created, createRequest);

        var aliasesRoute = new Uri(
            $"/api/v1/portals/{Route(created.PortalId)}/aliases",
            UriKind.Relative);

        string suffix = Suffix();
        string submitted = "MiXeD-" + suffix + ".LOCAL";
        string expected = submitted.ToLowerInvariant();

        using HttpResponseMessage createdAlias = await client.PostAsJsonAsync(
            aliasesRoute,
            new CreatePortalAliasRequest { HttpAlias = submitted },
            ApiTestFixture.Json);

        createdAlias.StatusCode.Should().Be(HttpStatusCode.Created);

        PortalAliasDto? alias = await createdAlias.Content.ReadEnvelopeAsync<PortalAliasDto>();

        alias.Should().NotBeNull();
        alias!.HttpAlias.Should().Be(expected);

        int storedAsSubmitted = await _fixture.Database.ScalarAsync<int>(
            "select count(*) from dbo.PortalAlias where HTTPAlias = @alias collate Latin1_General_CS_AS",
            new Dictionary<string, object?> { ["alias"] = submitted });

        storedAsSubmitted.Should().Be(0, "the submitted casing must not survive into the column");

        int storedCanonically = await _fixture.Database.ScalarAsync<int>(
            "select count(*) from dbo.PortalAlias where HTTPAlias = @alias collate Latin1_General_CS_AS",
            new Dictionary<string, object?> { ["alias"] = expected });

        storedCanonically.Should().Be(1, "the canonical casing is what is stored");

        // And the replacement path applies the same rule.
        var aliasRoute = new Uri(
            $"/api/v1/portals/{Route(created.PortalId)}/aliases/{Route(alias.PortalAliasId)}",
            UriKind.Relative);

        string replacement = "ReNaMeD-" + suffix + ".Local";

        using HttpResponseMessage updated = await client.PutAsJsonAsync(
            aliasRoute,
            new UpdatePortalAliasRequest { HttpAlias = replacement },
            ApiTestFixture.Json);

        // 200 with the stored row, which is what this endpoint answers a replacement with.
        updated.StatusCode.Should().Be(HttpStatusCode.OK);

        PortalAliasDto? replaced = await updated.Content.ReadEnvelopeAsync<PortalAliasDto>();

        replaced.Should().NotBeNull();
        replaced!.HttpAlias.Should().Be(replacement.ToLowerInvariant());

        int replacedCanonically = await _fixture.Database.ScalarAsync<int>(
            "select count(*) from dbo.PortalAlias where HTTPAlias = @alias collate Latin1_General_CS_AS",
            new Dictionary<string, object?> { ["alias"] = replacement.ToLowerInvariant() });

        replacedCanonically.Should().Be(1, "a replacement is canonicalised exactly as a creation is");
    }

    [Fact]
    public async Task PortalAliases_SupportCreateUpdateAndDelete()
    {
        using HttpClient host = await _fixture.CreateHostClientAsync();
        (PortalDetailDto created, CreatePortalRequest createRequest) =
            await CreatePortalWithRequestAsync(host);

        // The alias collection is nested beneath a portal, so it is tenant-scoped and has to be addressed
        // through the tenant that owns it. The portal keeps the alias it was created with throughout, so
        // the base address stays resolvable while a SECOND alias is added, renamed and removed beneath it.
        using HttpClient client = await CreatedTenantClientAsync(created, createRequest);

        var aliasesRoute = new Uri(
            $"/api/v1/portals/{Route(created.PortalId)}/aliases",
            UriKind.Relative);

        string firstAlias = "alias-" + Suffix() + ".local";

        using HttpResponseMessage createdAlias = await client.PostAsJsonAsync(
            aliasesRoute,
            new CreatePortalAliasRequest { HttpAlias = firstAlias },
            ApiTestFixture.Json);

        createdAlias.StatusCode.Should().Be(HttpStatusCode.Created);

        PortalAliasDto? alias = await createdAlias.Content
            .ReadEnvelopeAsync<PortalAliasDto>();

        alias.Should().NotBeNull();
        alias!.PortalAliasId.Should().BeGreaterThan(0);
        alias.PortalId.Should().Be(created.PortalId);
        alias.HttpAlias.Should().Be(firstAlias);

        using HttpResponseMessage listed = await client.GetAsync(aliasesRoute);
        listed.StatusCode.Should().Be(HttpStatusCode.OK);

        IReadOnlyList<PortalAliasDto>? all = await listed.Content
            .ReadEnvelopeAsync<IReadOnlyList<PortalAliasDto>>();

        all.Should().NotBeNull();
        all!.Select(item => item.HttpAlias).Should().Contain(firstAlias);

        var aliasRoute = new Uri(
            $"/api/v1/portals/{Route(created.PortalId)}/aliases/{Route(alias.PortalAliasId)}",
            UriKind.Relative);

        string secondAlias = "renamed-" + Suffix() + ".local";

        using HttpResponseMessage updated = await client.PutAsJsonAsync(
            aliasRoute,
            new UpdatePortalAliasRequest { HttpAlias = secondAlias },
            ApiTestFixture.Json);

        updated.StatusCode.Should().Be(HttpStatusCode.OK);

        PortalAliasDto? updatedAlias = await updated.Content
            .ReadEnvelopeAsync<PortalAliasDto>();

        updatedAlias.Should().NotBeNull();
        updatedAlias!.PortalAliasId.Should().Be(alias.PortalAliasId);
        updatedAlias.PortalId.Should().Be(created.PortalId);
        updatedAlias.HttpAlias.Should().Be(secondAlias);

        using HttpResponseMessage rereadAlias = await client.GetAsync(aliasRoute);
        rereadAlias.StatusCode.Should().Be(HttpStatusCode.OK);

        PortalAliasDto? persisted = await rereadAlias.Content
            .ReadEnvelopeAsync<PortalAliasDto>();

        persisted.Should().NotBeNull();
        persisted!.HttpAlias.Should().Be(secondAlias);

        using HttpResponseMessage removed = await client.DeleteAsync(aliasRoute);
        removed.StatusCode.Should().Be(HttpStatusCode.NoContent);

        using HttpResponseMessage gone = await client.GetAsync(aliasRoute);
        gone.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    /// <summary>
    /// The alias the CURRENT REQUEST resolved the tenant through is reported as current, and both a rename
    /// and an unbinding of it answer <c>409 Conflict</c> while leaving the row exactly as it was - whereas
    /// a second alias the request did not arrive through stays fully writable.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task PortalAlias_TheRequestResolvedThrough_IsCurrentAndCannotBeRenamedOrUnbound()
    {
        using HttpClient host = await _fixture.CreateHostClientAsync();
        (PortalDetailDto created, CreatePortalRequest createRequest) =
            await CreatePortalWithRequestAsync(host);

        // Addressed through the created tenant's OWN alias, which is precisely what makes that row current.
        using HttpClient client = await CreatedTenantClientAsync(created, createRequest);

        var aliasCollection = new Uri(
            $"/api/v1/portals/{Route(created.PortalId)}/aliases",
            UriKind.Relative);

        // A SECOND alias of the same portal, which this request did NOT arrive through.
        string spare = "spare-" + Suffix() + ".local";

        using HttpResponseMessage addedSpare = await client.PostAsJsonAsync(
            aliasCollection,
            new CreatePortalAliasRequest { HttpAlias = spare },
            ApiTestFixture.Json);

        addedSpare.StatusCode.Should().Be(HttpStatusCode.Created);

        PortalAliasDto? spareAlias = await addedSpare.Content.ReadEnvelopeAsync<PortalAliasDto>();
        spareAlias.Should().NotBeNull();
        spareAlias!.IsCurrent.Should().BeFalse(
            "resolution happened before this row existed, so it cannot be the one the request used");

        using HttpResponseMessage listed = await client.GetAsync(aliasCollection);
        listed.StatusCode.Should().Be(HttpStatusCode.OK);

        IReadOnlyList<PortalAliasDto>? rows = await listed.Content
            .ReadEnvelopeAsync<IReadOnlyList<PortalAliasDto>>();

        rows.Should().NotBeNull();
        rows!.Should().HaveCount(2, "the tenant holds the alias it was created with plus the spare");

        PortalAliasDto current = rows.Should().ContainSingle(row => row.IsCurrent).Subject;
        current.HttpAlias.Should().Be(
            createRequest.PortalAlias,
            "the marked row is the one the request's host name resolved through");

        var currentRoute = new Uri(
            $"/api/v1/portals/{Route(created.PortalId)}/aliases/{Route(current.PortalAliasId)}",
            UriKind.Relative);

        using HttpResponseMessage renamed = await client.PutAsJsonAsync(
            currentRoute,
            new UpdatePortalAliasRequest { HttpAlias = "renamed-" + Suffix() + ".local" },
            ApiTestFixture.Json);

        renamed.StatusCode.Should().Be(HttpStatusCode.Conflict);

        ProblemDetails? renameProblem = await renamed.Content
            .ReadFromJsonAsync<ProblemDetails>(ApiTestFixture.Json);
        renameProblem.Should().NotBeNull();
        renameProblem!.Type.Should().Be(ActiveAliasProblemType);

        using HttpResponseMessage unbound = await client.DeleteAsync(currentRoute);
        unbound.StatusCode.Should().Be(HttpStatusCode.Conflict);

        ProblemDetails? unbindProblem = await unbound.Content
            .ReadFromJsonAsync<ProblemDetails>(ApiTestFixture.Json);
        unbindProblem.Should().NotBeNull();
        unbindProblem!.Type.Should().Be(ActiveAliasProblemType);

        // The row survives BOTH refusals under its original host name. A refusal that still wrote would be
        // no refusal at all, and the tenant would already be unreachable by the time the assertion ran.
        using HttpResponseMessage reread = await client.GetAsync(currentRoute);
        reread.StatusCode.Should().Be(HttpStatusCode.OK);

        PortalAliasDto? persisted = await reread.Content.ReadEnvelopeAsync<PortalAliasDto>();
        persisted.Should().NotBeNull();
        persisted!.HttpAlias.Should().Be(createRequest.PortalAlias);
        persisted.IsCurrent.Should().BeTrue();

        // The spare row stays writable, so the rule withholds exactly one row rather than freezing the
        // collection - which is the difference between restoring the legacy affordance and losing a feature.
        var spareRoute = new Uri(
            $"/api/v1/portals/{Route(created.PortalId)}/aliases/{Route(spareAlias.PortalAliasId)}",
            UriKind.Relative);

        string spareRenamed = "spare-renamed-" + Suffix() + ".local";

        using HttpResponseMessage spareUpdate = await client.PutAsJsonAsync(
            spareRoute,
            new UpdatePortalAliasRequest { HttpAlias = spareRenamed },
            ApiTestFixture.Json);

        spareUpdate.StatusCode.Should().Be(HttpStatusCode.OK);

        PortalAliasDto? spareUpdated = await spareUpdate.Content
            .ReadEnvelopeAsync<PortalAliasDto>();

        spareUpdated.Should().NotBeNull();
        spareUpdated!.PortalAliasId.Should().Be(spareAlias.PortalAliasId);
        spareUpdated.HttpAlias.Should().Be(spareRenamed);
        spareUpdated.IsCurrent.Should().BeFalse();

        using HttpResponseMessage spareRemoved = await client.DeleteAsync(spareRoute);
        spareRemoved.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    /// <summary>
    /// An administrator of one portal cannot read, rename or unbind an alias belonging to another, even
    /// when it knows the alias's identifier - which it can, because the identifier is an installation-wide
    /// surrogate.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// This is the cross-tenant alias hijack in its most consequential form: an alias is what tenant
    /// resolution matches on, so renaming another tenant's alias re-points its traffic and unbinding one
    /// makes it unreachable. Two layers refuse it and the test proves the outer one - the route names a
    /// tenant the caller does not administer, so the policy refuses before the service is reached.
    /// </remarks>
    [Fact]
    public async Task PortalAlias_OfAnotherTenant_IsNotReachableByAPortalAdministrator()
    {
        using HttpClient host = await _fixture.CreateHostClientAsync();
        PortalDetailDto other = await CreatePortalAsync(host);

        string foreignAlias = "foreign-" + Suffix() + ".local";

        using HttpResponseMessage createdAlias = await host.PostAsJsonAsync(
            new Uri($"/api/v1/portals/{Route(other.PortalId)}/aliases", UriKind.Relative),
            new CreatePortalAliasRequest { HttpAlias = foreignAlias },
            ApiTestFixture.Json);

        createdAlias.StatusCode.Should().Be(HttpStatusCode.Created);

        PortalAliasDto? alias = await createdAlias.Content
            .ReadEnvelopeAsync<PortalAliasDto>();

        alias.Should().NotBeNull();

        using HttpClient administrator = await _fixture.CreateAdministratorClientAsync();

        var foreignRoute = new Uri(
            $"/api/v1/portals/{Route(other.PortalId)}/aliases/{Route(alias!.PortalAliasId)}",
            UriKind.Relative);

        using HttpResponseMessage read = await administrator.GetAsync(foreignRoute);
        read.StatusCode.Should().Be(HttpStatusCode.Forbidden);

        using HttpResponseMessage renamed = await administrator.PutAsJsonAsync(
            foreignRoute,
            new UpdatePortalAliasRequest { HttpAlias = "hijacked-" + Suffix() + ".local" },
            ApiTestFixture.Json);

        renamed.StatusCode.Should().Be(HttpStatusCode.Forbidden);

        using HttpResponseMessage unbound = await administrator.DeleteAsync(foreignRoute);
        unbound.StatusCode.Should().Be(HttpStatusCode.Forbidden);

        // The alias is untouched, which is the point: a refusal that still wrote would be no refusal at all.
        using HttpResponseMessage stillThere = await host.GetAsync(foreignRoute);
        stillThere.StatusCode.Should().Be(HttpStatusCode.OK);

        PortalAliasDto? survivor = await stillThere.Content
            .ReadEnvelopeAsync<PortalAliasDto>();

        survivor.Should().NotBeNull();
        survivor!.HttpAlias.Should().Be(foreignAlias);
    }

    /// <summary>
    /// A portal administrator cannot read, update or delete a portal other than the one it administers, and
    /// cannot enumerate or create portals at all.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task Portal_OfAnotherTenant_IsNotReachableByAPortalAdministrator()
    {
        using HttpClient host = await _fixture.CreateHostClientAsync();
        PortalDetailDto other = await CreatePortalAsync(host);

        using HttpClient administrator = await _fixture.CreateAdministratorClientAsync();

        using HttpResponseMessage read = await administrator.GetAsync(PortalRoute(other.PortalId));
        read.StatusCode.Should().Be(HttpStatusCode.Forbidden);

        using HttpResponseMessage settings = await administrator.GetAsync(
            new Uri($"/api/v1/portals/{Route(other.PortalId)}/settings", UriKind.Relative));
        settings.StatusCode.Should().Be(HttpStatusCode.Forbidden);

        using HttpResponseMessage removed = await administrator.DeleteAsync(PortalRoute(other.PortalId));
        removed.StatusCode.Should().Be(HttpStatusCode.Forbidden);

        using HttpResponseMessage listed = await administrator.GetAsync(PortalsRoute);
        listed.StatusCode.Should().Be(
            HttpStatusCode.Forbidden,
            "the roster of tenants is installation-wide and is not a portal administrator's to read");

        // The other portal survives, so the refusals refused rather than merely reporting a refusal.
        using HttpResponseMessage stillThere = await host.GetAsync(PortalRoute(other.PortalId));
        stillThere.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    /// <summary>Binding an alias that another tenant already holds answers <c>409 Conflict</c>.</summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task AddPortalAlias_WhenAlreadyBound_ReturnsConflict()
    {
        using HttpClient host = await _fixture.CreateHostClientAsync();
        (PortalDetailDto created, CreatePortalRequest createRequest) =
            await CreatePortalWithRequestAsync(host);

        using HttpClient client = await CreatedTenantClientAsync(created, createRequest);

        using HttpResponseMessage response = await client.PostAsJsonAsync(
            new Uri($"/api/v1/portals/{Route(created.PortalId)}/aliases", UriKind.Relative),
            new CreatePortalAliasRequest { HttpAlias = ApiTestFixture.TestHost },
            ApiTestFixture.Json);

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    /// <summary>
    /// Every action on both portal controllers declares an authorisation policy of its own, so no action
    /// can reach the application layer on the strength of authentication alone.
    /// </summary>
    /// <remarks>
    /// The cost of that split is that an action added later inherits mere authentication if its author
    /// forgets the attribute, and nothing about the code would look wrong. An attribute cannot express
    /// "every action must name a policy, but not the same one", so the guard is a test rather than a
    /// declaration.
    /// </remarks>
    [Fact]
    public void EveryPortalAction_DeclaresAnExplicitAuthorizationPolicy()
    {
        Type[] controllers =
        [
            typeof(DnnMigration.Api.Controllers.PortalsController),
            typeof(DnnMigration.Api.Controllers.PortalAliasesController),
        ];

        var unprotected = new List<string>();

        foreach (Type controller in controllers)
        {
            IEnumerable<MethodInfo> actions = controller
                .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .Where(method => method.GetCustomAttributes<HttpMethodAttribute>(inherit: true).Any());

            foreach (MethodInfo action in actions)
            {
                // An action deliberately opened to anonymous callers is stating its intent as explicitly as
                // one naming a policy, so it satisfies this guard. Neither controller has one today; the
                // allowance exists so that adding one is a decision rather than a test failure.
                if (action.GetCustomAttributes<AllowAnonymousAttribute>(inherit: true).Any())
                {
                    continue;
                }

                bool namesAPolicy = action
                    .GetCustomAttributes<AuthorizeAttribute>(inherit: true)
                    .Any(attribute => !string.IsNullOrWhiteSpace(attribute.Policy));

                if (!namesAPolicy)
                {
                    unprotected.Add(controller.Name + "." + action.Name);
                }
            }
        }

        unprotected.Should().BeEmpty(
            "every action on the portal controllers must name its own authorisation policy, because the "
            + "class attribute carries authentication only");
    }

    /// <summary>The host-wide alias collection is not part of the frozen public API.</summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task HostWidePortalAliasCollection_IsNotPublished()
    {
        using HttpClient client = await _fixture.CreateHostClientAsync();

        using HttpResponseMessage response = await client.GetAsync(
            new Uri("/api/v1/portal-aliases", UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    /// <summary>
    /// The two identifier values the legacy sentinel table also used for "absent" survive the wire as
    /// ordinary identifiers: the portal is addressed by a NEGATIVE identifier and answers with it, and the
    /// administrator role it names is identifier ZERO.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task Portal_SentinelValuedIdentifiers_SurviveTheWireAsIdentifiers()
    {
        using HttpClient client = await _fixture.CreateAdministratorClientAsync();

        _fixture.Seed.PortalId.Should().BeLessThan(1,
            "the seeded portal takes the first value of an IDENTITY(-1, 1) column, so this suite is "
            + "addressing a portal whose identifier the legacy sentinel table also used for 'absent'");

        using HttpResponseMessage response = await client.GetAsync(PortalRoute(_fixture.Seed.PortalId));

        response.StatusCode.Should().Be(HttpStatusCode.OK,
            "a negative identifier addresses a portal rather than reporting one that is missing");

        using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        JsonElement portal = document.RootElement.GetProperty("data");

        JsonElement identifier = RequireMember(portal, "portalId");
        identifier.ValueKind.Should().Be(
            JsonValueKind.Number,
            "the identifier must travel even when its value is the legacy absent-integer sentinel");
        identifier.GetInt32().Should().Be(_fixture.Seed.PortalId);

        JsonElement roleId = RequireMember(portal, "administratorRoleId");
        roleId.ValueKind.Should().Be(
            JsonValueKind.Number,
            "the administrator role identifier must travel even when its value is zero");
        roleId.GetInt32().Should().Be(_fixture.Seed.AdministratorRoleId);
        _fixture.Seed.AdministratorRoleId.Should().Be(0,
            "the seeded Administrators role takes the first value of an IDENTITY(0, 1) column, which is "
            + "what makes the assertion above a sentinel test rather than an arbitrary one");
    }

    /// <summary>
    /// A text member submitted as the EMPTY STRING comes back as the empty string. It is neither converted
    /// to null nor dropped from the payload.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// MIGRATION: this is the other half of Rule T7, and the sharper half. Every other sentinel in
    /// <c>Library/Components/Shared/Null.vb</c> is a distinguished value, but lines 71 to 73 return the
    /// EMPTY STRING for an absent string, so the legacy contract could not tell a stored null from a stored
    /// empty string at all - both arrived as <c>""</c>.
    /// </remarks>
    [Fact]
    public async Task Portal_EmptyStringMembers_SurviveAsEmptyStringsAndAreNeverDropped()
    {
        using HttpClient host = await _fixture.CreateHostClientAsync();
        (PortalDetailDto created, CreatePortalRequest createRequest) = await CreatePortalWithRequestAsync(host);

        using HttpClient client = await CreateAdministratorClientForAsync(createRequest, created);

        UpdatePortalRequest request = EchoHostOnlyFields(created);
        request.PortalName = created.PortalName;
        request.Description = string.Empty;
        request.KeyWords = string.Empty;
        request.FooterText = string.Empty;
        request.PaymentProcessor = string.Empty;
        request.ProcessorUserId = string.Empty;

        using HttpResponseMessage response = await client.PutAsJsonAsync(
            PortalRoute(created.PortalId),
            request,
            ApiTestFixture.Json);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        string[] cleared = ["description", "keyWords", "footerText", "paymentProcessor", "processorUserId"];

        using JsonDocument written = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        AssertEmptyStrings(written.RootElement.GetProperty("data"), cleared);

        // Read back through a second request, because a representation echoed from the request object would
        // prove nothing about what was stored.
        using HttpResponseMessage reread = await client.GetAsync(PortalRoute(created.PortalId));
        reread.StatusCode.Should().Be(HttpStatusCode.OK);

        using JsonDocument persisted = JsonDocument.Parse(await reread.Content.ReadAsStringAsync());
        AssertEmptyStrings(persisted.RootElement.GetProperty("data"), cleared);

        static void AssertEmptyStrings(JsonElement portal, IReadOnlyList<string> members)
        {
            foreach (string member in members)
            {
                JsonElement value = RequireMember(portal, member);
                value.ValueKind.Should().Be(JsonValueKind.String,
                    "'{0}' must remain a string rather than becoming null", member);
                value.GetString().Should().BeEmpty(
                    "'{0}' was submitted as the empty string and must be reported as the empty string", member);
            }
        }
    }

    /// <summary>
    /// The hosting charge and the three allowances travel as JSON NUMBERS and are present even when every
    /// one of them is nought.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// What does survive from that history is the reason the legacy grid rendered an empty cell.
    /// <c>Website/admin/Portal/portals.ascx</c> line 47 binds the column with
    /// <c>DataFormatString="{0:0.00}"</c>, a NUMERIC format applied to what was then a string value, and a
    /// numeric format string is silently ignored for a string - so an installation still carrying the
    /// baseline type rendered nothing rather than "0.00".
    /// </remarks>
    [Fact]
    public async Task Portal_HostingChargeAndAllowances_TravelAsNumbersEvenWhenNought()
    {
        using HttpClient host = await _fixture.CreateHostClientAsync();
        PortalDetailDto created = await CreatePortalAsync(host);

        using HttpResponseMessage response = await host.GetAsync(PortalRoute(created.PortalId));
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        string body = await response.Content.ReadAsStringAsync();

        using JsonDocument document = JsonDocument.Parse(body);
        JsonElement portal = document.RootElement.GetProperty("data");

        foreach (string member in new[] { "hostFee", "hostSpace", "pageQuota", "userQuota" })
        {
            JsonElement value = RequireMember(portal, member);
            value.ValueKind.Should().Be(JsonValueKind.Number,
                "'{0}' must be published as a number rather than as text or as null", member);
            value.GetDecimal().Should().Be(0m,
                "a freshly created portal carries no charge and no allowances");
        }

        body.Should().NotContain("\"hostFee\":\"",
            "the charge must not be published as a string, whether raw or preformatted");
        body.Should().NotContain("\"hostFee\":\"0.00\"",
            "the legacy grid's numeric display format is a presentation concern and is not applied here");
    }

    /// <summary>
    /// An identifier of ZERO is treated as an identifier. The route accepts it, the lookup runs, and the
    /// answer is a resource outcome rather than a rejection of the value itself.
    /// </summary>
    /// <param name="portalId">The sentinel-valued identifier to address.</param>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// The assertion is deliberately "not a rejection" rather than a fixed status. Which portals exist at
    /// the moment this runs depends on what the rest of the collection has created and removed, so pinning
    /// <c>200</c> or <c>404</c> would make the fact order-dependent and it would fail for a reason that has
    /// nothing to do with sentinels.
    /// </remarks>
    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    public async Task GetPortal_WithASentinelValuedIdentifierInTheRoute_IsAnsweredAsALookup(int portalId)
    {
        using HttpClient client = await _fixture.CreateHostClientAsync();

        using HttpResponseMessage response = await client.GetAsync(PortalRoute(portalId));

        response.StatusCode.Should().BeOneOf(
            [HttpStatusCode.OK, HttpStatusCode.NotFound],
            "identifier {0} must be bound and looked up, never rejected as though no identifier had been "
            + "supplied - it is a legal value of an IDENTITY column this schema seeds below one",
            portalId);
    }

    /// <summary>
    /// A sentinel-valued identifier in the REQUEST BODY is compared against the route for equality, and a
    /// disagreement is reported against the <c>portalId</c> field.
    /// </summary>
    /// <param name="bodyPortalId">The identifier to submit in the body.</param>
    /// <returns>A task representing the test.</returns>
    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    public async Task UpdatePortal_WithASentinelValuedBodyIdentifier_IsRefusedAgainstAnotherPortalsRoute(
        int bodyPortalId)
    {
        using HttpClient host = await _fixture.CreateHostClientAsync();
        (PortalDetailDto created, CreatePortalRequest createRequest) = await CreatePortalWithRequestAsync(host);

        created.PortalId.Should().NotBe(bodyPortalId,
            "the created portal must differ from the submitted identifier for this to be a mismatch");

        using HttpClient client = await CreateAdministratorClientForAsync(createRequest, created);

        UpdatePortalRequest request = EchoHostOnlyFields(created);
        request.PortalName = created.PortalName;
        request.PortalId = bodyPortalId;

        using HttpResponseMessage response = await client.PutAsJsonAsync(
            PortalRoute(created.PortalId),
            request,
            ApiTestFixture.Json);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest,
            "identifier {0} in the body names a different portal from the route and must be reported, "
            + "not absorbed",
            bodyPortalId);

        IReadOnlyDictionary<string, string[]> errors = await ReadValidationErrorsAsync(response);
        errors.Keys.Should().Contain(
            "portalId",
            "the caller is told which field disagreed rather than only that something did");
    }

    /// <summary>
    /// An update whose body carries NO identifier at all is refused, because the value it defaults to -
    /// zero - is a real portal identifier in this schema rather than a marker for "not supplied".
    /// </summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task UpdatePortal_WithNoBodyIdentifier_IsRefusedBecauseZeroIsARealIdentifier()
    {
        using HttpClient client = await _fixture.CreateAdministratorClientAsync();

        using HttpResponseMessage response = await client.PutAsJsonAsync(
            PortalRoute(_fixture.Seed.PortalId),
            new { portalName = "submitted with no identifier" },
            ApiTestFixture.Json);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        IReadOnlyDictionary<string, string[]> errors = await ReadValidationErrorsAsync(response);
        errors.Keys.Should().Contain("portalId");

        // Nothing was applied: the refusal happens before the write, so the stored name is untouched.
        using HttpResponseMessage reread = await client.GetAsync(PortalRoute(_fixture.Seed.PortalId));
        reread.StatusCode.Should().Be(HttpStatusCode.OK);
        PortalDetailDto persisted = await ReadDetailAsync(reread);
        persisted.PortalName.Should().NotBe("submitted with no identifier");
    }

    /// <summary>
    /// The total accompanying a page is never the legacy unpaged sentinel, on a page that holds rows and on
    /// one that holds none.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// MIGRATION: -1 is never a total here. The legacy listing passed the same -1 that
    /// <c>Library/Components/Shared/Null.vb</c> lines 41 to 43 return for an absent integer as an index, a
    /// size and a total alike, to mean "everything" - so a caller could not tell an unpaged answer from an
    /// absent one. The citation is <c>GetPortalsByName(Filter + "%", CurrentPage - 1, PageSize,
    /// TotalRecords)</c> at <c>Website/admin/Portal/Portals.ascx.vb</c> line 142.
    /// </remarks>
    [Fact]
    public async Task ListPortals_ReportsANonNegativeTotalOnAPopulatedAndOnAnEmptyPage()
    {
        using HttpClient client = await _fixture.CreateHostClientAsync();

        using HttpResponseMessage populated = await client.GetAsync(
            new Uri("/api/v1/portals?pageIndex=0&pageSize=25", UriKind.Relative));
        populated.StatusCode.Should().Be(HttpStatusCode.OK);

        PagedEnvelope<PortalListItemDto>? page = await populated.Content
            .ReadFromJsonAsync<PagedEnvelope<PortalListItemDto>>(ApiTestFixture.Json);
        page.Should().NotBeNull();
        page!.Meta.TotalCount.Should().BeGreaterThan(0);
        page.Meta.PageSize.Should().BeGreaterThan(0,
            "a page size of zero would hand a dividing client a zero divisor");
        page.Meta.PageIndex.Should().BeGreaterThanOrEqualTo(0);

        using HttpResponseMessage empty = await client.GetAsync(
            new Uri("/api/v1/portals?pageIndex=0&pageSize=25&name=zzz-nothing-bears-this-name", UriKind.Relative));
        empty.StatusCode.Should().Be(HttpStatusCode.OK);

        PagedEnvelope<PortalListItemDto>? emptyPage = await empty.Content
            .ReadFromJsonAsync<PagedEnvelope<PortalListItemDto>>(ApiTestFixture.Json);
        emptyPage.Should().NotBeNull();
        emptyPage!.Items.Should().BeEmpty();
        emptyPage.Meta.TotalCount.Should().Be(0,
            "an empty result reports a total of nought and never the legacy -1");
        emptyPage.Meta.TotalPages.Should().Be(0);
    }

    /// <summary>A listed row carries exactly the columns the legacy grid rendered, and nothing else.</summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// The eight members are the eight data columns declared in <c>Website/admin/Portal/portals.ascx</c>,
    /// in the order the grid declared them: the identifier at line 23, the site title at line 30, the alias
    /// list at line 37, the account count at line 44, the page count at line 45, the disc-space allowance
    /// at line 46, the hosting charge at line 47 and the expiry at line 48.
    /// </remarks>
    [Fact]
    public async Task ListPortals_PublishesExactlyTheColumnsTheLegacyGridRendered()
    {
        using HttpClient client = await _fixture.CreateHostClientAsync();

        using HttpResponseMessage response = await client.GetAsync(
            new Uri("/api/v1/portals?pageIndex=0&pageSize=1", UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        JsonElement items = document.RootElement.GetProperty("items");
        items.GetArrayLength().Should().BeGreaterThan(0, "the seeded portal is always listed");

        JsonElement row = items[0];
        row.EnumerateObject().Select(member => member.Name).Should().BeEquivalentTo(
            new[]
            {
                "portalId", "portalName", "aliases", "users", "pages", "hostSpace", "hostFee", "expiryDate",
            },
            "a listed row carries the legacy grid's eight columns and neither loses one nor gains one");

        row.GetProperty("aliases").ValueKind.Should().Be(JsonValueKind.Array,
            "the alias column rendered a portal's whole alias list, so it travels as a list");
    }

    /// <summary>
    /// The name filter matches a PREFIX of the site title, exactly as the legacy grid did, and a caller's
    /// wildcard characters are matched literally rather than acting as a pattern.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task ListPortals_NameFilter_MatchesAPrefixAndTreatsWildcardsAsLiterals()
    {
        using HttpClient client = await _fixture.CreateHostClientAsync();

        // A genuine prefix of the seeded title, in the WRONG case, so the fact also pins the case folding
        // that keeps the answer independent of the installation's collation.
        const string prefix = "integrat";
        IntegrationSeed.PortalName.Should().StartWith(
            "Integrat",
            "the prefix asserted below is only a prefix if the seeded title still begins with it");

        PagedEnvelope<PortalListItemDto> matched = await FilterByNameAsync(client, prefix);
        matched.Items.Select(item => item.PortalId).Should().Contain(
            _fixture.Seed.PortalId,
            "a prefix matches, and case is folded on both sides");

        // A fragment taken from the MIDDLE of the seeded title: neither a prefix of the name nor a suffix
        // of it, so only a containment match could find it - and the legacy grid could not.
        const string interior = "tegration Por";
        IntegrationSeed.PortalName.Should().Contain(interior);
        IntegrationSeed.PortalName.Should().NotStartWith(interior);

        PagedEnvelope<PortalListItemDto> interiorMatch = await FilterByNameAsync(client, interior);
        interiorMatch.Items.Select(item => item.PortalId).Should().NotContain(
            _fixture.Seed.PortalId,
            "an interior fragment matched nothing in the legacy grid, and the prefix filter reproduces that");

        PagedEnvelope<PortalListItemDto> wildcard = await FilterByNameAsync(client, "%");
        wildcard.Items.Should().BeEmpty(
            "a per cent sign is data rather than a pattern, so it matches no title that does not begin with one");
        wildcard.Meta.TotalCount.Should().Be(0);

        PagedEnvelope<PortalListItemDto> underscore = await FilterByNameAsync(client, "_ntegration");
        underscore.Items.Should().BeEmpty(
            "an underscore matches itself rather than standing in for any single character");
    }

    /// <summary>
    /// A delete is REFUSED with <c>409 Conflict</c> while only one portal remains, and the portal it
    /// refused to remove is still there afterwards.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// STAGED ON A HOST OF ITS OWN, and that is the whole reason this fact can exist. The condition is "the
    /// installation is down to one portal", which cannot be reached on the shared database without removing
    /// tenants the rest of the collection depends on.
    /// </remarks>
    [Fact]
    public async Task DeletePortal_WhileItIsTheOnlyPortal_ReturnsConflictAndKeepsIt()
    {
        using (ApiTestFixture.OverrideEnvironment(_fixture.HostConfiguration()))
        {
            ApiTestFixture isolated = new();
            IAsyncLifetime lifetime = isolated;

            try
            {
                await lifetime.InitializeAsync();

                using HttpClient client = await isolated.CreateHostClientAsync();

                using HttpResponseMessage listed = await client.GetAsync(
                    new Uri("/api/v1/portals?pageIndex=0&pageSize=25", UriKind.Relative));
                listed.StatusCode.Should().Be(HttpStatusCode.OK);

                PagedEnvelope<PortalListItemDto>? page = await listed.Content
                    .ReadFromJsonAsync<PagedEnvelope<PortalListItemDto>>(ApiTestFixture.Json);
                page.Should().NotBeNull();
                page!.Meta.TotalCount.Should().Be(1,
                    "the isolated installation seeds exactly one portal, which is the condition under test");

                using HttpResponseMessage refused = await client.DeleteAsync(
                    new Uri(
                        $"/api/v1/portals/{ApiTestFixture.Route(isolated.Seed.PortalId)}",
                        UriKind.Relative));

                refused.StatusCode.Should().Be(HttpStatusCode.Conflict,
                    "an installation with no portal is unreachable, so the last one may not be removed");

                ProblemDetails? problem = await refused.Content
                    .ReadFromJsonAsync<ProblemDetails>(ApiTestFixture.Json);
                problem.Should().NotBeNull();
                problem!.Status.Should().Be(StatusCodes.Status409Conflict);
                problem.Type.Should().Be(
                    LastRemainingProblemType,
                    "a client branches on the named code rather than on the wording or the status alone");
                problem.Detail.Should().NotBeNullOrWhiteSpace();

                using HttpResponseMessage stillThere = await client.GetAsync(
                    new Uri(
                        $"/api/v1/portals/{ApiTestFixture.Route(isolated.Seed.PortalId)}",
                        UriKind.Relative));
                stillThere.StatusCode.Should().Be(HttpStatusCode.OK,
                    "the refusal is decided before anything is removed, so nothing was removed");
            }
            finally
            {
                // The lifetime member is what releases the throwaway database; the asynchronous disposal
                // inherited from the host factory only shuts the host down and would leak it.
                await lifetime.DisposeAsync();
            }
        }

        // The shared host is still serving from the shared database, which proves the isolation held.
        using HttpClient shared = await _fixture.CreateHostClientAsync();
        using HttpResponseMessage afterwards = await shared.GetAsync(PortalRoute(_fixture.Seed.PortalId));
        afterwards.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    /// <summary>
    /// A caller-supplied correlation identifier comes back on the response exactly once, and one is
    /// generated when the caller supplies none.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// The cardinality matters as much as the value. The header is SET rather than appended, so a response
    /// carries one identifier; two would leave a log reader unable to say which request they were holding,
    /// and a client reading the first value would disagree with a proxy reading the last.
    /// </remarks>
    [Fact]
    public async Task CorrelationId_IsEchoedWhenSuppliedAndGeneratedWhenNot()
    {
        using HttpClient client = await _fixture.CreateAdministratorClientAsync();

        string supplied = ApiTestFixture.NewCorrelationId();

        using var stamped = new HttpRequestMessage(HttpMethod.Get, PortalRoute(_fixture.Seed.PortalId));
        ApiTestFixture.WithCorrelationId(stamped, supplied);

        using HttpResponseMessage echoed = await client.SendAsync(stamped);

        echoed.StatusCode.Should().Be(HttpStatusCode.OK);
        IReadOnlyList<string> answered = echoed.Headers
            .Where(header => string.Equals(
                header.Key,
                ApiTestFixture.CorrelationIdHeader,
                StringComparison.OrdinalIgnoreCase))
            .SelectMany(header => header.Value)
            .ToList();

        answered.Should().ContainSingle(
            "every response carries the correlation header, and it is set rather than appended")
            .Which.Should().Be(supplied, "a caller's own identifier is honoured rather than replaced");

        using HttpResponseMessage unstamped = await client.GetAsync(PortalRoute(_fixture.Seed.PortalId));

        unstamped.StatusCode.Should().Be(HttpStatusCode.OK);
        ApiTestFixture.ReadCorrelationId(unstamped).Should().NotBeNullOrWhiteSpace(
            "a request that supplied no identifier is still traceable");
    }

    /// <summary>
    /// A correlation identifier is present on a FAILED request's problem document, which is the response a
    /// caller is most likely to be reporting.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// This is the case the header exists for, and the one an implementation is most likely to lose: the
    /// header is registered on the response before the pipeline continues, so a failure raised further in -
    /// or an error payload written by a different component - still carries it. A stamping step that ran
    /// after the endpoint would leave exactly the responses a caller quotes with nothing to quote.
    /// </remarks>
    [Fact]
    public async Task CorrelationId_IsPresentOnAProblemDocument()
    {
        using HttpClient client = await _fixture.CreateHostClientAsync();

        string supplied = ApiTestFixture.NewCorrelationId();

        using var stamped = new HttpRequestMessage(
            HttpMethod.Get,
            new Uri(
                $"/api/v1/portals/{Route(_fixture.Seed.PortalId)}/aliases/{Route(UnknownPortalId)}",
                UriKind.Relative));
        ApiTestFixture.WithCorrelationId(stamped, supplied);

        using HttpResponseMessage response = await client.SendAsync(stamped);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        ApiTestFixture.ReadCorrelationId(response).Should().Be(
            supplied,
            "the response a caller reports must carry the identifier they can quote");

        ProblemDetails? problem = await response.Content
            .ReadFromJsonAsync<ProblemDetails>(ApiTestFixture.Json);
        problem.Should().NotBeNull();
        problem!.Status.Should().Be(StatusCodes.Status404NotFound);
    }

    /// <summary>
    /// An inbound correlation identifier that is oversized, blank or carries control characters is REPLACED
    /// with a generated one, and the request itself is still served.
    /// </summary>
    /// <param name="hostile">The header value to send.</param>
    /// <param name="description">What makes the value unusable, quoted in the failure message.</param>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// The first risk is log forging. The identifier is written into structured log events, so a value
    /// carrying a line break could inject a whole fabricated entry, and an unbounded value could bloat
    /// every event a request produces.
    /// </remarks>
    [Theory]
    [InlineData("with-a-tab\tand-more", "a control character that could forge a log entry")]
    [InlineData("   ", "whitespace, which identifies nothing")]
    [InlineData("Integr8tion!Pass", "a value shaped like a password")]
    [InlineData("operator@contoso.example", "a value shaped like an e-mail address")]
    [InlineData(
        "eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxIn0.c2lnbmF0dXJl",
        "a value shaped like a bearer token")]
    [InlineData("sk-live-9f2c4d6e8a0b1c3d5e7f", "a value shaped like an API key")]
    [InlineData("4d19ae7c1b8f4e2a9d6c3f5b7a091e2", "a near-canonical value one character short")]
    [InlineData("4d19ae7c1b8f4e2a9d6c3f5b7a091e2g", "a 32-character value carrying a non-hexadecimal digit")]
    [InlineData("4d19ae7c-1b8f-4e2a-9d6c3f5b7a091e2d", "a hyphenated value with a hyphen out of position")]
    public async Task CorrelationId_ThatCannotBeTrusted_IsReplacedAndTheRequestIsStillServed(
        string hostile,
        string description)
    {
        using HttpClient client = await _fixture.CreateAdministratorClientAsync();

        using var request = new HttpRequestMessage(HttpMethod.Get, PortalRoute(_fixture.Seed.PortalId));
        request.Headers.TryAddWithoutValidation(ApiTestFixture.CorrelationIdHeader, hostile);

        using HttpResponseMessage response = await client.SendAsync(request);

        response.StatusCode.Should().Be(
            HttpStatusCode.OK,
            "a header carrying {0} is not the caller's request being malformed",
            description);

        string? answered = ApiTestFixture.ReadCorrelationId(response);
        answered.Should().NotBeNullOrWhiteSpace();
        answered.Should().NotBe(hostile, "a value carrying {0} must not be echoed back", description);
    }

    /// <summary>
    /// A correlation identifier longer than the accepted bound is replaced rather than echoed or truncated.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task CorrelationId_LongerThanTheAcceptedBound_IsReplacedRatherThanTruncated()
    {
        using HttpClient client = await _fixture.CreateAdministratorClientAsync();

        string oversized = new('x', 512);

        using var request = new HttpRequestMessage(HttpMethod.Get, PortalRoute(_fixture.Seed.PortalId));
        request.Headers.TryAddWithoutValidation(ApiTestFixture.CorrelationIdHeader, oversized);

        using HttpResponseMessage response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        string? answered = ApiTestFixture.ReadCorrelationId(response);
        answered.Should().NotBeNullOrWhiteSpace();
        answered.Should().NotBe(oversized);
        oversized.Should().NotStartWith(answered!,
            "a truncated prefix of the caller's value is still the caller's text and must not be adopted");
    }

    /// <summary>
    /// A refusal decided by the authorisation pipeline is served as RFC 7807 under the
    /// <c>application/problem+json</c> media type, and names the reason as a problem type.
    /// </summary>
    /// <param name="authenticated">Whether the caller presents a token at all.</param>
    /// <param name="expectedStatus">The status the caller is answered with.</param>
    /// <param name="expectedType">The problem type the caller branches on.</param>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// THE MEDIA TYPE IS ASSERTED HERE AND NOT EVERYWHERE, and the distinction is measured rather than
    /// assumed. Refusals written by the authorisation result handler set the RFC 7807 media type
    /// explicitly, so it is part of their contract and pinning it protects it.
    /// </remarks>
    [Theory]
    [InlineData(false, HttpStatusCode.Unauthorized, "urn:dnnmigration:error:auth.unauthenticated")]
    [InlineData(true, HttpStatusCode.Forbidden, "urn:dnnmigration:error:auth.not_permitted")]
    public async Task AuthorizationRefusal_IsServedAsProblemJsonNamingItsReason(
        bool authenticated,
        HttpStatusCode expectedStatus,
        string expectedType)
    {
        using HttpClient client = authenticated
            ? await _fixture.CreateUnprivilegedClientAsync()
            : _fixture.CreateAnonymousClient();

        using HttpResponseMessage response = await client.GetAsync(
            new Uri("/api/v1/portals?pageIndex=0&pageSize=1", UriKind.Relative));

        response.StatusCode.Should().Be(expectedStatus);
        response.Content.Headers.ContentType?.MediaType.Should().Be(
            ProblemMediaType,
            "an authorisation refusal declares itself as RFC 7807 rather than as ordinary JSON");

        ProblemDetails? problem = await response.Content
            .ReadFromJsonAsync<ProblemDetails>(ApiTestFixture.Json);

        problem.Should().NotBeNull();
        problem!.Type.Should().Be(expectedType);
        problem.Status.Should().Be((int)expectedStatus);
        problem.Title.Should().NotBeNullOrWhiteSpace();
        problem.Detail.Should().NotBeNullOrWhiteSpace();

        problem.Status.Should().NotBe(StatusCodes.Status302Found);
        response.Headers.Location.Should().BeNull(
            "a refusal is reported by status rather than by redirecting to a rendered page");
    }

    /// <summary>
    /// A validation failure names the OFFENDING FIELDS in its <c>errors</c> dictionary and reproduces the
    /// validator's own wording.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task CreatePortal_WithAnEmptyBody_NamesEveryOffendingFieldInTheErrorsDictionary()
    {
        using HttpClient client = await _fixture.CreateHostClientAsync();

        using HttpResponseMessage response = await client.PostAsJsonAsync(
            PortalsRoute,
            new { },
            ApiTestFixture.Json);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        IReadOnlyDictionary<string, string[]> errors = await ReadValidationErrorsAsync(response);

        errors.Keys.Should().BeEquivalentTo(
            new[]
            {
                "PortalAlias",
                "TemplateFile",
                "AdministratorFirstName",
                "AdministratorLastName",
                "AdministratorUsername",
                "AdministratorPassword",
                "AdministratorEmail",
            },
            "an empty create reports every field the legacy sign-up screen required, by name");

        errors.Values.Should().OnlyContain(messages => messages.Length > 0,
            "a named field without a message tells a caller nothing");
        errors["TemplateFile"].Should().ContainSingle()
            .Which.Should().Be("Please select a template file",
                "the validator's wording reaches the caller unedited");
    }

    /// <summary>
    /// A request whose host name is only a SUBSTRING of a configured alias resolves no tenant, and the
    /// request is still served rather than being refused or rewritten.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// THIS IS THE DEFECT THE EXACT-MATCH RESOLVER EXISTS TO CLOSE, and it is a behavioural improvement
    /// recorded here rather than smuggled in.
    /// </remarks>
    [Fact]
    public async Task GetPortal_FromAHostThatIsOnlyASubstringOfAConfiguredAlias_IsRefused()
    {
        // One character shorter than the configured alias, so it is a strict substring of it and nothing
        // else - which is precisely the input the legacy containment predicate mis-resolved.
        string substring = ApiTestFixture.TestHost[..^1];
        substring.Should().NotBe(ApiTestFixture.TestHost);
        ApiTestFixture.TestHost.Should().Contain(substring);

        using HttpClient client = await _fixture.CreateHostClientAsync(substring);

        using HttpResponseMessage response = await client.GetAsync(PortalRoute(_fixture.Seed.PortalId));

        response.StatusCode.Should().Be(
            HttpStatusCode.Forbidden,
            "a strict substring of an alias resolves to no tenant, and a portal named in the route is not a "
            + "substitute for having arrived at one");

        ProblemDetails? problem = await response.Content
            .ReadFromJsonAsync<ProblemDetails>(ApiTestFixture.Json);

        problem.Should().NotBeNull("the refusal carries a problem document rather than an empty body");
        problem!.Type.Should().Be(
            TenantUnresolvedProblemType,
            "the refusal must be the tenant one, so this fact is proving alias handling and not some "
            + "unrelated authorisation outcome");

        string body = await response.Content.ReadAsStringAsync();
        body.Should().NotContain(
            "\"portalId\"",
            "nothing may be served in place of the tenant that could not be resolved");
    }

    /// <summary>A host name differing only in CASE resolves the same tenant.</summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// The comparison is culture-independent, and that is a correctness requirement rather than a
    /// preference: culture-sensitive lower-casing maps the dotted capital I differently under a Turkish
    /// locale, which would make tenant resolution depend on the server's regional settings.
    /// </remarks>
    [Fact]
    public async Task GetPortal_FromAMixedCaseHost_ResolvesTheSameTenant()
    {
        string upperCased = ApiTestFixture.TestHost.ToUpperInvariant();
        upperCased.Should().NotBe(ApiTestFixture.TestHost, "the host name must actually differ in case");

        // The credential is presented at the seeded alias and the REQUEST is addressed at the mixed-case
        // form, so the only thing this fact varies is the host name a request arrives on - which is what it
        // claims to measure.
        using HttpClient client = await _fixture.CreateAdministratorClientAsync(upperCased);

        using HttpResponseMessage response = await client.GetAsync(PortalRoute(_fixture.Seed.PortalId));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await ReadDetailAsync(response)).PortalId.Should().Be(_fixture.Seed.PortalId);
    }

    /// <summary>
    /// The paths that are served WITHOUT a tenant really are: the health probe answers from a host name no
    /// portal claims, and so does the collection route, which names no portal for a per-portal rule to
    /// read.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// The health probe is the load-bearing case. The shipped composition declares the frontend container
    /// dependent on the API reporting healthy, so a probe that required a resolved tenant would leave the
    /// frontend unable to start on any installation whose alias table was not yet configured - which is
    /// every installation, at the moment it is first brought up.
    /// </remarks>
    [Fact]
    public async Task PathsServedWithoutATenant_AnswerFromAnUnclaimedHostName()
    {
        using HttpClient client = await _fixture.CreateHostClientAsync("no-portal-claims-this-name.invalid");

        using HttpResponseMessage health = await client.GetAsync(new Uri("/health", UriKind.Relative));
        health.StatusCode.Should().Be(
            HttpStatusCode.OK,
            "the container health probe must answer before any alias is configured");

        using HttpResponseMessage collection = await client.GetAsync(
            new Uri("/api/v1/portals?pageIndex=0&pageSize=1", UriKind.Relative));
        collection.StatusCode.Should().Be(
            HttpStatusCode.OK,
            "the collection spans every tenant, so it needs none resolved to answer");

        // The negative control: a collection whose tenant can only come from the host name.
        using HttpResponseMessage tenantBound = await client.GetAsync(
            new Uri("/api/v1/module-definitions", UriKind.Relative));
        tenantBound.StatusCode.Should().Be(
            HttpStatusCode.Forbidden,
            "a route that can only learn its tenant from the host name is refused when the host names none");
        tenantBound.Content.Headers.ContentType?.MediaType.Should().Be(ProblemMediaType);

        ProblemDetails? problem = await tenantBound.Content
            .ReadFromJsonAsync<ProblemDetails>(ApiTestFixture.Json);
        problem.Should().NotBeNull();
        problem!.Type.Should().Be(TenantUnresolvedProblemType);
        problem.Detail.Should().NotContain(
            "no-portal-claims-this-name",
            "the host name is caller-supplied text and echoing it back would be a reflection vector");
    }

    /// <summary>
    /// A created alias is located by a header carrying BOTH route values - the portal and the alias - so a
    /// client can follow it without composing an address of its own.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task AddPortalAlias_LocatesTheCreatedAliasBeneathItsPortal()
    {
        using HttpClient host = await _fixture.CreateHostClientAsync();
        (PortalDetailDto created, CreatePortalRequest createRequest) = await CreatePortalWithRequestAsync(host);

        using HttpClient client = await CreatedTenantClientAsync(created, createRequest);

        string aliasCollection = $"/api/v1/portals/{Route(created.PortalId)}/aliases";
        string httpAlias = "located-" + Suffix() + ".local";

        using HttpResponseMessage response = await client.PostAsJsonAsync(
            new Uri(aliasCollection, UriKind.Relative),
            new CreatePortalAliasRequest { HttpAlias = httpAlias },
            ApiTestFixture.Json);

        response.StatusCode.Should().Be(HttpStatusCode.Created);

        PortalAliasDto? alias = await response.Content.ReadEnvelopeAsync<PortalAliasDto>();
        alias.Should().NotBeNull();
        alias!.PortalId.Should().Be(created.PortalId);
        alias.HttpAlias.Should().Be(httpAlias);

        response.Headers.Location.Should().NotBeNull("a create locates what it created");
        response.Headers.Location!.OriginalString.Should().Be(
            $"{aliasCollection}/{Route(alias.PortalAliasId)}",
            "the location carries the portal and the alias, because the alias is addressed beneath its portal");

        using HttpResponseMessage followed = await client.GetAsync(response.Headers.Location);
        followed.StatusCode.Should().Be(HttpStatusCode.OK, "the location must address the created alias");

        PortalAliasDto? fetched = await followed.Content.ReadEnvelopeAsync<PortalAliasDto>();
        fetched.Should().NotBeNull();
        fetched!.PortalAliasId.Should().Be(alias.PortalAliasId);

        // Binding the same host name twice is refused as a conflict rather than silently creating a second
        // row, which would make tenant resolution ambiguous for that address.
        using HttpResponseMessage duplicate = await client.PostAsJsonAsync(
            new Uri(aliasCollection, UriKind.Relative),
            new CreatePortalAliasRequest { HttpAlias = httpAlias },
            ApiTestFixture.Json);

        duplicate.StatusCode.Should().Be(HttpStatusCode.Conflict);

        ProblemDetails? problem = await duplicate.Content
            .ReadFromJsonAsync<ProblemDetails>(ApiTestFixture.Json);
        problem.Should().NotBeNull();
        problem!.Type.Should().Be(DuplicateAliasProblemType);
    }

    /// <summary>
    /// Binding the same host name from several callers at once binds it exactly once, refuses every other
    /// caller as an alias conflict, and answers no caller with a server fault.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// Measured against a live installation with the fault untranslated: eight simultaneous bindings produced
    /// one 201, four 409 and THREE 500s, with exactly one row stored. <c>IX_PortalAlias</c> is unique over
    /// the host name and it did exactly its job; the three racers it refused were nevertheless told the
    /// server had failed.
    /// </remarks>
    [Fact]
    public async Task AddPortalAlias_SubmittedConcurrentlyForOneHostName_BindsItOnceWithoutAnyServerFault()
    {
        using HttpClient host = await _fixture.CreateHostClientAsync();
        (PortalDetailDto created, CreatePortalRequest createRequest) = await CreatePortalWithRequestAsync(host);

        using HttpClient client = await CreatedTenantClientAsync(created, createRequest);

        string aliasCollection = $"/api/v1/portals/{Route(created.PortalId)}/aliases";
        string contested = "race-" + Suffix() + ".local";

        const int Callers = 10;

        IEnumerable<Task<HttpResponseMessage>> submissions = Enumerable.Range(0, Callers).Select(_ =>
            client.PostAsJsonAsync(
                new Uri(aliasCollection, UriKind.Relative),
                new CreatePortalAliasRequest { HttpAlias = contested },
                ApiTestFixture.Json));

        HttpResponseMessage[] responses = await Task.WhenAll(submissions);

        try
        {
            HttpStatusCode[] statuses = [.. responses.Select(response => response.StatusCode)];

            statuses.Should().NotContain(
                status => (int)status >= 500,
                "a unique index refusing a duplicate host name is the index working, not the server failing");

            statuses.Count(status => status == HttpStatusCode.Created).Should().Be(
                1,
                "one caller binds the host name and every other must be refused");

            statuses.Where(status => status != HttpStatusCode.Created).Should()
                .AllBeEquivalentTo(HttpStatusCode.Conflict);

            foreach (HttpResponseMessage refused in responses
                .Where(response => response.StatusCode != HttpStatusCode.Created))
            {
                ProblemDetails? refusal = await refused.Content
                    .ReadFromJsonAsync<ProblemDetails>(ApiTestFixture.Json);

                refusal.Should().NotBeNull("a refusal is published as a problem document whatever its cause");
                refusal!.Type.Should().Be(
                    DuplicateAliasProblemType,
                    "a caller that lost the race is told the same thing as one that arrived second");
            }

            int bound = await _fixture.Database.ScalarAsync<int>(
                "SELECT COUNT(*) FROM [dbo].[PortalAlias] WHERE [HTTPAlias] = @httpAlias;",
                new Dictionary<string, object?> { ["httpAlias"] = contested });

            bound.Should().Be(
                1,
                "a second row for one host name would make tenant resolution ambiguous for that address");
        }
        finally
        {
            foreach (HttpResponseMessage response in responses)
            {
                response.Dispose();
            }
        }
    }

    /// <summary>
    /// Every one of the six host-only values is refused to a portal administrator, and the refusal is a
    /// <c>403 Forbidden</c> rather than a server fault.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// All six are exercised because they are six separate comparisons in one guard and a guard that had
    /// lost one term would still pass a single-field test. They run against ONE portal, sequentially, which
    /// is sound precisely because each attempt is refused: nothing is written, so the stored row is
    /// identical before and after every case and the cases cannot interfere.
    /// </remarks>
    [Fact]
    public async Task UpdatePortal_AsAdministratorAlteringAnyHostOnlyValue_IsForbiddenAndNotAServerFault()
    {
        using HttpClient host = await _fixture.CreateHostClientAsync();
        (PortalDetailDto created, CreatePortalRequest createRequest) = await CreatePortalWithRequestAsync(host);

        using HttpClient client = await CreateAdministratorClientForAsync(createRequest, created);

        // The control, asserted once for the whole table below: the endpoint policy admits this caller when no
        // host-only field is altered, so every refusal in the loop is attributable to the field guard.
        UpdatePortalRequest permitted = EchoHostOnlyFields(created);
        permitted.PortalName = created.PortalName;

        using HttpResponseMessage admitted = await client.PutAsJsonAsync(
            PortalRoute(created.PortalId),
            permitted,
            ApiTestFixture.Json);

        admitted.StatusCode.Should().Be(
            HttpStatusCode.OK,
            "the endpoint policy admits this caller, so each refusal below belongs to the field-level guard");

        (string Field, Action<UpdatePortalRequest> Alter)[] hostOnly =
        [
            ("hostFee", request => request.HostFee = (created.HostFee ?? 0m) + 99.99m),
            ("hostSpace", request => request.HostSpace = (created.HostSpace ?? 0) + 4096),
            ("pageQuota", request => request.PageQuota = (created.PageQuota ?? 0) + 250),
            ("userQuota", request => request.UserQuota = (created.UserQuota ?? 0) + 500),
            ("siteLogHistory", request => request.SiteLogHistory = -1),
            ("expiryDate", request => request.ExpiryDate = new DateTime(2031, 1, 1, 0, 0, 0, DateTimeKind.Utc)),
        ];

        foreach ((string field, Action<UpdatePortalRequest> alter) in hostOnly)
        {
            UpdatePortalRequest request = EchoHostOnlyFields(created);
            request.PortalName = created.PortalName;
            alter(request);

            using HttpResponseMessage response = await client.PutAsJsonAsync(
                PortalRoute(created.PortalId),
                request,
                ApiTestFixture.Json);

            response.StatusCode.Should().Be(
                HttpStatusCode.Forbidden,
                "only a host account may change '{0}', and a deliberate refusal is not a server fault",
                field);
            response.StatusCode.Should().NotBe(
                HttpStatusCode.InternalServerError,
                "the legacy guard raised a bare exception for '{0}'; the refusal survives but the fault does not",
                field);

            ProblemDetails? problem = await response.Content
                .ReadFromJsonAsync<ProblemDetails>(ApiTestFixture.Json);
            problem.Should().NotBeNull();
            problem!.Status.Should().Be(StatusCodes.Status403Forbidden);
            problem.Type.Should().Be(
                AuthorisationRefusalProblemType,
                "the refusal for '{0}' is published under this API's single problem taxonomy",
                field);
            problem.Detail.Should().NotBeNullOrWhiteSpace();
        }

        using HttpResponseMessage reread = await client.GetAsync(PortalRoute(created.PortalId));
        reread.StatusCode.Should().Be(HttpStatusCode.OK);

        PortalDetailDto persisted = await ReadDetailAsync(reread);
        persisted.HostFee.Should().Be(created.HostFee);
        persisted.HostSpace.Should().Be(created.HostSpace);
        persisted.PageQuota.Should().Be(created.PageQuota);
        persisted.UserQuota.Should().Be(created.UserQuota);
        persisted.SiteLogHistory.Should().Be(created.SiteLogHistory);
        persisted.ExpiryDate.Should().Be(created.ExpiryDate);
    }

    /// <summary>
    /// An allowance of NOUGHT is accepted and stored as nought, because in this schema nought means
    /// unlimited rather than "nothing permitted".
    /// </summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task UpdatePortal_WithAllowancesOfNought_IsAcceptedBecauseNoughtMeansUnlimited()
    {
        using HttpClient client = await _fixture.CreateHostClientAsync();
        PortalDetailDto created = await CreatePortalAsync(client);

        UpdatePortalRequest request = EchoHostOnlyFields(created);
        request.PortalName = created.PortalName;
        request.UserQuota = 0;
        request.PageQuota = 0;
        request.HostSpace = 0;

        using HttpResponseMessage response = await client.PutAsJsonAsync(
            PortalRoute(created.PortalId),
            request,
            ApiTestFixture.Json);

        response.StatusCode.Should().Be(
            HttpStatusCode.OK,
            "nought is a meaningful allowance and must not be refused as though nothing had been supplied");

        PortalDetailDto updated = await ReadDetailAsync(response);
        updated.UserQuota.Should().Be(0);
        updated.PageQuota.Should().Be(0);
        updated.HostSpace.Should().Be(0);

        using JsonDocument document = JsonDocument.Parse(await (await client
            .GetAsync(PortalRoute(created.PortalId))).Content.ReadAsStringAsync());
        JsonElement portal = document.RootElement.GetProperty("data");

        foreach (string member in new[] { "userQuota", "pageQuota", "hostSpace" })
        {
            JsonElement value = RequireMember(portal, member);
            value.ValueKind.Should().Be(
                JsonValueKind.Number,
                "'{0}' must travel even when it is nought, because nought is the value that means unlimited",
                member);
            value.GetInt32().Should().Be(0);
        }
    }

    /// <summary>Removing a portal's only remaining alias is PERMITTED, and the portal survives with none.</summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// The consequence is real and is stated here rather than hidden: a portal with no alias cannot be
    /// reached by host name, and the endpoints that repair that are deliberately reachable without a
    /// resolved tenant so that an operator can bind a new one. The recovery path is asserted, which is what
    /// makes permitting the removal defensible rather than merely permissive.
    /// </remarks>
    [Fact]
    public async Task DeletePortalAlias_OfTheOnlyRemainingAlias_IsPermittedAndTheAddressCanBeRebound()
    {
        using HttpClient host = await _fixture.CreateHostClientAsync();
        (PortalDetailDto created, CreatePortalRequest createRequest) = await CreatePortalWithRequestAsync(host);

        using HttpClient client = await CreatedTenantClientAsync(created, createRequest);

        string aliasCollection = $"/api/v1/portals/{Route(created.PortalId)}/aliases";

        using HttpResponseMessage listed = await client.GetAsync(new Uri(aliasCollection, UriKind.Relative));
        listed.StatusCode.Should().Be(HttpStatusCode.OK);

        IReadOnlyList<PortalAliasDto>? aliases = await listed.Content
            .ReadEnvelopeAsync<IReadOnlyList<PortalAliasDto>>();
        aliases.Should().NotBeNull();
        PortalAliasDto only = aliases!.Should().ContainSingle(
            "a create binds exactly one address to the new portal").Subject;

        only.IsCurrent.Should().BeTrue(
            "read through the created tenant's own alias, that row is the one resolution used - which is why " +
            "the removal below is issued by a caller standing somewhere else");

        // Issued by the HOST client, which reached the installation through the SEEDED portal's alias and
        // so is not standing on the row it is removing. Through the tenant's own client this same call is
        // refused as an active-alias conflict, which is a different rule and is proven separately.
        using HttpResponseMessage removed = await host.DeleteAsync(
            new Uri($"{aliasCollection}/{Route(only.PortalAliasId)}", UriKind.Relative));

        removed.StatusCode.Should().Be(
            HttpStatusCode.NoContent,
            "no rule forbids removing a portal's last address, and inventing one would refuse a legacy save");

        // The portal itself is untouched, and is still addressable by a route that names it.
        using HttpResponseMessage stillThere = await host.GetAsync(PortalRoute(created.PortalId));
        stillThere.StatusCode.Should().Be(HttpStatusCode.OK);

        // And the address can be bound again, which is the recovery path that makes the removal safe.
        using HttpResponseMessage rebound = await host.PostAsJsonAsync(
            new Uri(aliasCollection, UriKind.Relative),
            new CreatePortalAliasRequest { HttpAlias = "rebound-" + Suffix() + ".local" },
            ApiTestFixture.Json);

        rebound.StatusCode.Should().Be(
            HttpStatusCode.Created,
            "an operator must be able to give an unaddressable portal an address back");
    }

    /// <summary>
    /// An alias submitted with a scheme and in mixed case is ACCEPTED, and is stored exactly as it was
    /// submitted rather than being folded and stripped the way the legacy screen folded and stripped it.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// MIGRATION: THE LEGACY INPUT NORMALISATION IS NOT REPRODUCED, and this fact exists to record that
    /// rather than to endorse it.
    /// </remarks>
    [Fact]
    public async Task CreatePortal_WithASchemeQualifiedMixedCaseAlias_StoresItVerbatim()
    {
        using HttpClient host = await _fixture.CreateHostClientAsync();

        string bareAlias = "normalised-" + Suffix() + ".local";
        string submitted = "HTTP://" + bareAlias.ToUpperInvariant();

        CreatePortalRequest request = NewPortalRequest();
        request.PortalAlias = submitted;

        using HttpResponseMessage response = await host.PostAsJsonAsync(
            PortalsRoute,
            request,
            ApiTestFixture.Json);

        response.StatusCode.Should().Be(
            HttpStatusCode.Created,
            "the legacy screen accepted this input, so refusing it would lose a save an operator could make");

        PortalDetailDto created = await ReadDetailAsync(response);
        created.Aliases.Should().NotBeNull();
        created.Aliases!.Select(alias => alias.HttpAlias).Should().Contain(
            submitted,
            "the submitted text reaches the alias table unaltered; the legacy screen would have folded its "
            + "case and stripped its scheme first");

        // The portal is still reachable by a route that names it, which is what keeps the fault recoverable.
        using HttpResponseMessage byRoute = await host.GetAsync(PortalRoute(created.PortalId));
        byRoute.StatusCode.Should().Be(
            HttpStatusCode.OK,
            "an unusable alias does not take the portal out of reach of a route that names it");

        // And a usable address can be bound in its place, through an endpoint that needs no resolved tenant.
        string usable = "rebound-" + Suffix() + ".local";
        using HttpResponseMessage rebound = await host.PostAsJsonAsync(
            new Uri($"/api/v1/portals/{Route(created.PortalId)}/aliases", UriKind.Relative),
            new CreatePortalAliasRequest { HttpAlias = usable },
            ApiTestFixture.Json);

        rebound.StatusCode.Should().Be(HttpStatusCode.Created);

        using HttpClient tenant = await _fixture.CreateTenantClientAsync(
            usable,
            created.PortalId,
            request.AdministratorUsername!);

        using HttpResponseMessage addressed = await tenant.GetAsync(PortalRoute(created.PortalId));
        addressed.StatusCode.Should().Be(
            HttpStatusCode.OK,
            "the recovery path works, which is what makes the divergence above tolerable rather than fatal");
    }

    /// <summary>
    /// An alias carrying spaces and punctuation is refused, with ONE message reported against the field
    /// rather than one per offending character.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task CreatePortal_WithSeveralOffendingCharactersInTheAlias_ReportsOneMessageNotOnePerCharacter()
    {
        using HttpClient client = await _fixture.CreateHostClientAsync();

        CreatePortalRequest request = NewPortalRequest();
        request.PortalAlias = "bad alias!!" + Suffix();

        using HttpResponseMessage response = await client.PostAsJsonAsync(
            PortalsRoute,
            request,
            ApiTestFixture.Json);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        IReadOnlyDictionary<string, string[]> errors = await ReadValidationErrorsAsync(response);

        errors.Keys.Should().Contain("PortalAlias");
        errors["PortalAlias"].Should().ContainSingle(
            "the legacy check repeated its sentence once per offending character, which is deliberately not "
            + "reproduced")
            .Which.Should().Be("The Portal Name Must Not Contain Spaces Or Punctuation.");
    }

    /// <summary>The settings sub-resource answers <c>404 Not Found</c> for a portal that does not exist.</summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// Asserted as a HOST account, because a portal administrator naming a portal other than its own is
    /// refused before the lookup runs and would answer 403 - which is deliberate, and is asserted
    /// separately. A host account is the only caller for whom the absent path is reachable at all, so it is
    /// the only caller that can prove this.
    /// </remarks>
    [Fact]
    public async Task GetPortalSettings_WhenThePortalIsUnknown_ReturnsNotFound()
    {
        using HttpClient client = await _fixture.CreateHostClientAsync();

        using HttpResponseMessage response = await client.GetAsync(
            new Uri($"/api/v1/portals/{Route(UnknownPortalId)}/settings", UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);

        ProblemDetails? problem = await response.Content
            .ReadFromJsonAsync<ProblemDetails>(ApiTestFixture.Json);
        problem.Should().NotBeNull();
        problem!.Status.Should().Be(StatusCodes.Status404NotFound);
        problem.Type.Should().NotBeNullOrWhiteSpace();
    }

    /// <summary>
    /// The portal surface is CLOSED. The legacy screens this migration excludes have no endpoint, and the
    /// collection route accepts no destructive verb.
    /// </summary>
    /// <param name="excluded">An address that must not be served.</param>
    /// <returns>A task representing the test.</returns>
    [Theory]
    [InlineData("/api/v1/portals/aliases")]
    [InlineData("/api/v1/portals/expired")]
    [InlineData("/api/v1/portals/-1/template")]
    [InlineData("/api/v1/portals/-1/wizard")]
    [InlineData("/api/v1/portals/-1/settings/keys")]
    public async Task ExcludedLegacyPortalScreens_HaveNoEndpoint(string excluded)
    {
        using HttpClient client = await _fixture.CreateHostClientAsync();

        using HttpResponseMessage response = await client.GetAsync(new Uri(excluded, UriKind.Relative));

        response.StatusCode.Should().Be(
            HttpStatusCode.NotFound,
            "'{0}' belongs to a legacy screen this migration excludes and must not be published",
            excluded);
    }

    /// <summary>
    /// The collection route refuses a destructive verb, so there is no way to remove portals in bulk.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// The <c>Allow</c> header is asserted here for the first time. It is what this remark always claimed
    /// the refusal carried, and the claim went unchecked - so adding the payload had to be accompanied by
    /// proving the header survived it, since a status-code page that replaced the response wholesale would
    /// have silently dropped the one header that tells the caller what to do instead.
    /// </remarks>
    [Fact]
    public async Task DeleteOnThePortalCollection_IsNotAllowed()
    {
        using HttpClient client = await _fixture.CreateHostClientAsync();

        using HttpResponseMessage response = await client.DeleteAsync(PortalsRoute);

        response.StatusCode.Should().Be(
            HttpStatusCode.MethodNotAllowed,
            "portals are removed one at a time, by an address that names the one being removed");

        response.Content.Headers.ContentType?.MediaType.Should().Be(
            "application/problem+json",
            "a refusal decided by routing is a problem document like every refusal decided further in");

        ProblemDetails? problem = await response.Content
            .ReadFromJsonAsync<ProblemDetails>(ApiTestFixture.Json);

        problem.Should().NotBeNull();
        problem!.Status.Should().Be(StatusCodes.Status405MethodNotAllowed);
        problem.Type.Should().Be(
            "urn:dnnmigration:error:request.method_not_allowed",
            "the refusal names its condition in this API's own taxonomy rather than leaving the caller to "
            + "infer it from the status line alone");

        response.Content.Headers.ContentLength.Should().BeGreaterThan(
            0,
            "the document is actually written, rather than the header being declared over an empty body");

        response.Content.Headers.Allow.Should().Contain(
            "GET",
            "the address supports reading, and the refusal must say so");
        response.Content.Headers.Allow.Should().Contain(
            "POST",
            "the address supports creating, and the refusal must say so");
        response.Content.Headers.Allow.Should().NotContain(
            "DELETE",
            "the verb just refused must not appear among the permitted ones");
    }

    /// <summary>
    /// A create whose administrator password fails the credential policy is refused, and the policy applied
    /// is the legacy one rather than a tightened one.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// The credential policy is carried forward VERBATIM from the membership provider the legacy
    /// application registered - a minimum length of seven characters, no requirement for a non-alphanumeric
    /// character, and no question-and-answer requirement.
    /// </remarks>
    [Fact]
    public async Task CreatePortal_AppliesTheLegacyCredentialPolicyAndNoStricterOne()
    {
        using HttpClient client = await _fixture.CreateHostClientAsync();

        CreatePortalRequest tooShort = NewPortalRequest();
        tooShort.AdministratorPassword = "abc123";
        tooShort.AdministratorPassword.Should().HaveLength(6, "one character short of the legacy minimum");

        using HttpResponseMessage refused = await client.PostAsJsonAsync(
            PortalsRoute,
            tooShort,
            ApiTestFixture.Json);

        refused.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        IReadOnlyDictionary<string, string[]> errors = await ReadValidationErrorsAsync(refused);
        errors.Keys.Should().Contain(
            "AdministratorPassword",
            "the caller is told which field the credential policy refused");

        CreatePortalRequest atTheBoundary = NewPortalRequest();
        atTheBoundary.AdministratorPassword = "abcdefg";
        atTheBoundary.AdministratorPassword.Should().HaveLength(7, "exactly the legacy minimum");

        using HttpResponseMessage accepted = await client.PostAsJsonAsync(
            PortalsRoute,
            atTheBoundary,
            ApiTestFixture.Json);

        accepted.StatusCode.Should().Be(
            HttpStatusCode.Created,
            "the legacy policy required no non-alphanumeric character, and imposing one would lock existing "
            + "operators out of an installation they could previously administer");
    }

    /// <summary>
    /// No member of the portal surface reports an outcome through a by-reference parameter. Every one of
    /// them returns its result.
    /// </summary>
    /// <returns>Nothing; this fact reads metadata rather than issuing a request.</returns>
    [Fact]
    public void NoPortalMember_ReportsItsOutcomeThroughAByReferenceParameter()
    {
        Type[] surface =
        [
            typeof(DnnMigration.Api.Controllers.PortalsController),
            typeof(DnnMigration.Api.Controllers.PortalAliasesController),
            typeof(DnnMigration.Application.Abstractions.IPortalService),
        ];

        var offending = new List<string>();

        foreach (Type type in surface)
        {
            foreach (MethodInfo method in type.GetMethods(
                BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                foreach (ParameterInfo parameter in method.GetParameters())
                {
                    if (parameter.ParameterType.IsByRef)
                    {
                        offending.Add($"{type.Name}.{method.Name}({parameter.Name})");
                    }
                }
            }
        }

        offending.Should().BeEmpty(
            "an outcome travels in the return value; a by-reference parameter cannot cross an HTTP boundary "
            + "and must not appear on a contract that does");
    }

    /// <summary>Creates a portal through the API and returns its representation.</summary>
    /// <param name="client">A client carrying host credentials.</param>
    /// <returns>The created portal.</returns>
    private async Task<PortalDetailDto> CreatePortalAsync(HttpClient client) =>
        (await CreatePortalWithRequestAsync(client)).Created;

    /// <summary>
    /// Creates a portal through the API and returns the request that made it beside its representation.
    /// </summary>
    /// <param name="client">A client carrying host credentials.</param>
    /// <returns>The submitted request and the created portal.</returns>
    private async Task<(PortalDetailDto Created, CreatePortalRequest Request)> CreatePortalWithRequestAsync(
        HttpClient client)
    {
        CreatePortalRequest request = NewPortalRequest();

        using HttpResponseMessage response = await client.PostAsJsonAsync(
            PortalsRoute,
            request,
            ApiTestFixture.Json);

        response.StatusCode.Should().Be(HttpStatusCode.Created);

        return (await ReadDetailAsync(response), request);
    }

    /// <summary>
    /// Signs in as the administrator account the create provisioned for a given portal, WITHOUT addressing
    /// that portal's host.
    /// </summary>
    /// <param name="request">The request that created the portal, which carries the alias and login name.</param>
    /// <param name="detail">The created portal, which carries the administrator's identifier.</param>
    /// <returns>A client authenticated as that portal's own administrator.</returns>
    /// <remarks>
    /// No role claim is asserted by the test, because none would help: the portal administrator policy
    /// reads the target portal's <c>AdministratorRoleId</c> and verifies a time-bounded assignment of that
    /// role to this account in the database. A token naming the role without the underlying assignment is
    /// refused, which is deliberate - a role NAME is not a tenant-scoped fact.
    /// </remarks>
    private Task<HttpClient> CreateAdministratorClientForAsync(
        CreatePortalRequest request,
        PortalDetailDto detail)
    {
        detail.AdministratorId.Should().NotBeNull(
            "the create provisions an administrator and records it against the portal");
        request.AdministratorUsername.Should().NotBeNullOrWhiteSpace();
        request.PortalAlias.Should().NotBeNullOrWhiteSpace();

        return _fixture.CreateTenantClientAsync(
            request.PortalAlias!,
            detail.PortalId,
            request.AdministratorUsername!,
            addressedAt: ApiTestFixture.TestHost);
    }

    /// <summary>
    /// Builds a client that RESOLVES TO a freshly created tenant, authenticated as that tenant's own
    /// administrator.
    /// </summary>
    /// <param name="created">The created portal.</param>
    /// <param name="request">The request that created it, which carries the alias and administrator name.</param>
    /// <returns>An authenticated client addressed at the created tenant.</returns>
    /// <remarks>
    /// The caller no longer states an account identifier or an installation-wide flag. Both are read from
    /// the store by the sign-in endpoint while it composes the token, so stating them here could only ever
    /// contradict what the store holds - and a caller that genuinely holds installation-wide authority is
    /// the seeded host account, obtained from <c>CreateHostClientAsync</c>.
    /// </remarks>
    private Task<HttpClient> CreatedTenantClientAsync(
        PortalDetailDto created,
        CreatePortalRequest request) => _fixture.CreateTenantClientAsync(
            request.PortalAlias!,
            created.PortalId,
            request.AdministratorUsername!);

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
        // The body identifier must name the same portal as the route, which the registered validator
        // enforces; echoing the representation's own identifier is how a caller satisfies it.
        PortalId = detail.PortalId,
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

    /// <summary>
    /// Builds the complete body accepted by the settings PUT from the representation returned by its GET.
    /// </summary>
    /// <param name="settings">The current settings projection.</param>
    /// <returns>A whole-row replacement that preserves every value the response publishes.</returns>
    private static UpdatePortalSettingsRequest SettingsUpdateFrom(PortalSettingsDto settings) => new()
    {
        PortalName = settings.PortalName,
        LogoFile = settings.LogoFile,
        FooterText = settings.FooterText,
        ExpiryDate = settings.ExpiryDate,
        UserRegistration = settings.UserRegistration,
        BannerAdvertising = settings.BannerAdvertising,
        Currency = settings.Currency,
        AdministratorId = settings.AdministratorId,
        HostFee = settings.HostFee,
        HostSpace = settings.HostSpace,
        PageQuota = settings.PageQuota,
        UserQuota = settings.UserQuota,
        PaymentProcessor = settings.PaymentProcessor,
        ProcessorUserId = settings.ProcessorUserId,
        ProcessorCredentialReference = null,
        Description = settings.Description,
        KeyWords = settings.KeyWords,
        BackgroundFile = settings.BackgroundFile,
        SiteLogHistory = settings.SiteLogHistory,
        SplashTabId = settings.SplashTabId,
        HomeTabId = settings.HomeTabId,
        LoginTabId = settings.LoginTabId,
        UserTabId = settings.UserTabId,
        DefaultLanguage = settings.DefaultLanguage,
        TimeZoneOffset = settings.TimeZoneOffset,
        HomeDirectory = settings.HomeDirectory,

        // Carried, because a real client carries it: the settings screen reads this token and returns it so
        // a stale whole-row replacement is refused with 409 rather than silently destroying another
        // operator's committed edit.
        ConcurrencyToken = settings.ConcurrencyToken,
    };

    /// <summary>Reads a portal representation out of a response, failing the test when it is absent.</summary>
    /// <param name="response">The response to read.</param>
    /// <returns>The representation.</returns>
    private static async Task<PortalDetailDto> ReadDetailAsync(HttpResponseMessage response)
    {
        PortalDetailDto? detail = await response.Content
            .ReadEnvelopeAsync<PortalDetailDto>();

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

    /// <summary>Reads the per-field <c>errors</c> dictionary out of a validation failure.</summary>
    /// <param name="response">The refused response.</param>
    /// <returns>The offending field names, each with the messages reported against it.</returns>
    private static async Task<IReadOnlyDictionary<string, string[]>> ReadValidationErrorsAsync(
        HttpResponseMessage response)
    {
        ValidationProblemDetails? problem = await response.Content
            .ReadFromJsonAsync<ValidationProblemDetails>(ApiTestFixture.Json);

        problem.Should().NotBeNull("a refused request must publish a validation problem document");
        problem!.Status.Should().Be(
            StatusCodes.Status400BadRequest,
            "the envelope carries the status, not only the response line");
        problem.Title.Should().NotBeNullOrWhiteSpace();
        problem.Detail.Should().NotBeNullOrWhiteSpace();
        problem.Type.Should().NotBeNullOrWhiteSpace(
            "a client branches on the problem type rather than parsing prose");

        return problem.Errors.AsReadOnly();
    }

    /// <summary>Asserts that a JSON object carries a member and hands the member back.</summary>
    /// <param name="owner">The object to read.</param>
    /// <param name="member">The member that must be present.</param>
    /// <returns>The member's value.</returns>
    /// <remarks>
    /// Presence has to be asserted at all - rather than simply reading the member - because the failure
    /// this suite guards against is a member that is ABSENT. Reading a missing member throws, and an
    /// exception names the reader instead of naming the contract, so the assertion comes first and reports
    /// which member was missing.
    /// </remarks>
    private static JsonElement RequireMember(JsonElement owner, string member)
    {
        MemberNames(owner).Should().Contain(member, "'{0}' is part of the published payload", member);

        return owner.GetProperty(member);
    }

    /// <summary>Lists the member names a JSON object carries, in the order it carries them.</summary>
    /// <param name="owner">The object to read.</param>
    /// <returns>The member names.</returns>
    private static IReadOnlyList<string> MemberNames(JsonElement owner) => owner
        .EnumerateObject()
        .Select(member => member.Name)
        .ToList();

    /// <summary>Reads the portal collection filtered by a name fragment.</summary>
    /// <param name="client">The caller, which must hold host authority.</param>
    /// <param name="fragment">The fragment to filter on, sent as data rather than as a pattern.</param>
    /// <returns>The page the server answered with.</returns>
    private static async Task<PagedEnvelope<PortalListItemDto>> FilterByNameAsync(
        HttpClient client,
        string fragment)
    {
        using HttpResponseMessage response = await client.GetAsync(new Uri(
            "/api/v1/portals?pageIndex=0&pageSize=50&name=" + Uri.EscapeDataString(fragment),
            UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.OK, "a filtered read is a read");

        PagedEnvelope<PortalListItemDto>? page = await response.Content
            .ReadFromJsonAsync<PagedEnvelope<PortalListItemDto>>(ApiTestFixture.Json);

        page.Should().NotBeNull();
        return page!;
    }

    /// <summary>
    /// The enumerations that have no legacy spelling travel as the integer discriminators their columns
    /// store, which is the form the Angular models consume.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task NumericEnumerations_TravelAsTheirStoredIntegers()
    {
        using HttpClient client = await _fixture.CreateHostClientAsync();

        using HttpResponseMessage response = await client.GetAsync(PortalRoute(_fixture.Seed.PortalId));

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

    /// <summary>
    /// A tenant whose STORED administrator carries no membership row is still saveable through the settings
    /// resource, provided the submission leaves that designation alone.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// <para>
    /// ⚠ THIS IS THE EXACT ROUTE THE DEFECT WAS REPORTED THROUGH: reading the settings and putting the body
    /// back unchanged. On the measured installation six readable tenants designate an account with no matching
    /// UserPortals row - membership the migrated data never carried - and asking the ownership rule of a value
    /// the caller was only echoing back refused every one of them with 400
    /// <c>portal.administrator_invalid</c>. No submission could have satisfied the rule short of altering data
    /// the caller never asked to touch, so no footer, keyword or page reference could be amended on any of the
    /// six tenants.
    /// </para>
    /// <para>
    /// The membership row is removed here rather than relying on a seeded absence, because the fixture seeds a
    /// coherent installation. The row is restored before the case returns, so the ordering of this suite's
    /// cases cannot matter.
    /// </para>
    /// </remarks>
    [Fact]
    [Trait("Category", "Integration")]
    public async Task UpdatePortalSettings_UnchangedAdministratorWithNoMembershipRow_IsStillSaveable()
    {
        using HttpClient host = await _fixture.CreateHostClientAsync();
        PortalDetailDto created = await CreatePortalAsync(host);
        var settingsRoute = new Uri($"/api/v1/portals/{Route(created.PortalId)}/settings", UriKind.Relative);

        using HttpResponseMessage read = await host.GetAsync(settingsRoute);
        read.StatusCode.Should().Be(HttpStatusCode.OK);
        PortalSettingsDto stored = (await read.Content.ReadEnvelopeAsync<PortalSettingsDto>())!;
        stored.AdministratorId.Should().NotBeNull("a created portal designates the account it created");

        int administratorId = stored.AdministratorId!.Value;
        var membershipParameters = new Dictionary<string, object?>
        {
            ["portalId"] = created.PortalId,
            ["userId"] = administratorId,
        };

        // Captured before the row is removed, so the restore in the finally block puts back what was there
        // rather than an approximation of it. Authorised is NOT NULL and carries no default.
        DateTime membershipCreated = await _fixture.Database.ScalarAsync<DateTime>(
            "SELECT [CreatedDate] FROM [dbo].[UserPortals] WHERE [PortalId] = @portalId AND [UserId] = @userId;",
            membershipParameters);
        int membershipAuthorised = await _fixture.Database.ScalarAsync<int>(
            @"SELECT CAST([Authorised] AS int) FROM [dbo].[UserPortals]
              WHERE [PortalId] = @portalId AND [UserId] = @userId;",
            membershipParameters);

        // Reproduce the migrated shape: the designation stands, the membership row does not.
        int removed = await _fixture.Database.ExecuteAsync(
            "DELETE FROM [dbo].[UserPortals] WHERE [PortalId] = @portalId AND [UserId] = @userId;",
            membershipParameters);

        removed.Should().Be(1, "the arrangement only reproduces the defect if the row was there to remove");

        try
        {
            // The body the GET just returned, put straight back - changing nothing at all.
            using HttpResponseMessage echoed = await host.PutAsJsonAsync(
                settingsRoute,
                SettingsUpdateFrom(stored));

            echoed.StatusCode.Should().Be(
                HttpStatusCode.OK,
                "a submission that echoes the stored designation authors no new reference");

            // And an amendment to an unrelated field still lands, which is the maintainability the defect cost.
            UpdatePortalSettingsRequest amended = SettingsUpdateFrom(stored);
            amended.FooterText = "amended-under-an-orphaned-designation";

            using HttpResponseMessage written = await host.PutAsJsonAsync(settingsRoute, amended);

            written.StatusCode.Should().Be(HttpStatusCode.OK);
            PortalSettingsDto updated = (await written.Content.ReadEnvelopeAsync<PortalSettingsDto>())!;
            updated.FooterText.Should().Be("amended-under-an-orphaned-designation");
            updated.AdministratorId.Should().Be(
                administratorId,
                "the grandfathered designation is preserved rather than cleared");

            // A NEW invalid reference is still refused, so admitting history admits no new breakage. Built from
            // a FRESH read rather than from `stored`, because the amendment above moved the row and its
            // concurrency token with it - reusing the original projection would be refused 409 as a stale write
            // before the reference rule was ever consulted, and the case would pass for the wrong reason.
            UpdatePortalSettingsRequest reassigned = SettingsUpdateFrom(updated);
            reassigned.AdministratorId = _fixture.Seed.MemberUserId;

            using HttpResponseMessage refused = await host.PutAsJsonAsync(settingsRoute, reassigned);

            refused.StatusCode.Should().Be(
                HttpStatusCode.BadRequest,
                "a designation the caller CHANGES is still tested against membership");

            ProblemDetails? problem = await refused.Content.ReadFromJsonAsync<ProblemDetails>(
                ApiTestFixture.Json);

            problem.Should().NotBeNull();
            problem!.Type.Should().Be("urn:dnnmigration:error:portal.administrator_invalid");
        }
        finally
        {
            await _fixture.Database.ExecuteAsync(
                @"IF NOT EXISTS (
                      SELECT 1 FROM [dbo].[UserPortals]
                      WHERE [PortalId] = @portalId AND [UserId] = @userId)
                  INSERT INTO [dbo].[UserPortals] ([UserId], [PortalId], [CreatedDate], [Authorised])
                  VALUES (@userId, @portalId, @createdDate, @authorised);",
                new Dictionary<string, object?>
                {
                    ["portalId"] = created.PortalId,
                    ["userId"] = administratorId,
                    ["createdDate"] = membershipCreated,
                    ["authorised"] = membershipAuthorised != 0,
                });

            using HttpResponseMessage deleted = await host.DeleteAsync(PortalRoute(created.PortalId));
            deleted.StatusCode.Should().Be(HttpStatusCode.NoContent);
        }
    }

    /// <summary>
    /// A tenant-name filter the database collation cannot weigh must match NOTHING rather than everything.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// <para>
    /// ⚠ THIS IS A SAFETY TEST, NOT A TIDINESS ONE. The schema is collated
    /// <c>SQL_Latin1_General_CP1_CI_AS</c>, which gives supplementary characters no collation weight, so
    /// <c>N'🎉🎉🎉'</c> compares equal to the empty string and a prefix match on it degrades to
    /// <c>LIKE N'%'</c>. Measured against the live listing before the guard existed, searching the tenant
    /// list for three emoji reported a filter in force and returned EVERY portal.
    /// </para>
    /// <para>
    /// A host who believes the list has been narrowed to one tenant may open, amend or delete a row on that
    /// belief, and a portal delete removes every page, module and membership it owns. Returning nothing is
    /// both honest and safe.
    /// </para>
    /// </remarks>
    [Fact]
    [Trait("Category", "Integration")]
    public async Task ListPortals_ByCharactersTheCollationCannotWeigh_MatchesNothingRatherThanEverything()
    {
        using HttpClient client = await _fixture.CreateHostClientAsync();

        // The unfiltered total, established first so "everything" is a measured number rather than a guess.
        using HttpResponseMessage unfiltered = await client.GetAsync(new Uri(
            "/api/v1/portals?pageIndex=0&pageSize=100",
            UriKind.Relative));

        unfiltered.StatusCode.Should().Be(HttpStatusCode.OK);

        PagedEnvelope<PortalListItemDto>? everything = await unfiltered.Content
            .ReadFromJsonAsync<PagedEnvelope<PortalListItemDto>>(ApiTestFixture.Json);

        everything.Should().NotBeNull();
        everything!.Meta.TotalCount.Should()
            .BeGreaterThan(0, "the guard is only meaningful when there are rows it could wrongly return");

        PagedEnvelope<PortalListItemDto> weightless = await FilterByNameAsync(
            client,
            "\U0001F389\U0001F389\U0001F389");

        weightless.Items.Should().BeEmpty("no tenant name begins with those characters");
        weightless.Meta.TotalCount.Should().Be(
            0,
            "a filter that cannot discriminate fails closed, never open");
        weightless.Meta.TotalCount.Should().NotBe(
            everything.Meta.TotalCount,
            "returning the complete tenant list for a filter the host typed is the unsafe direction");
    }

    /// <summary>
    /// A tenant-name filter MIXING weightless characters with ordinary text still discriminates on the
    /// ordinary part, so the guard suppresses no legitimate search.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    [Trait("Category", "Integration")]
    public async Task ListPortals_ByOrdinaryTextCarryingAWeightlessCharacter_StillFiltersOnTheOrdinaryPart()
    {
        using HttpClient client = await _fixture.CreateHostClientAsync();

        PagedEnvelope<PortalListItemDto> matched = await FilterByNameAsync(
            client,
            "zzz-no-portal-bears-this-name\U0001F389");

        matched.Items.Should().BeEmpty(
            "the ordinary part carries weight, so the filter is applied rather than treated as unable to filter");
    }

    /// <summary>Produces a short random suffix for values that reach a unique constraint.</summary>
    /// <returns>Twelve lower-case hexadecimal characters.</returns>
    private static string Suffix() => Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture)[..12];
}
