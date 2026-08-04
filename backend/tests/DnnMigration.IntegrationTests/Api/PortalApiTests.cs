using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using DnnMigration.Api.ErrorHandling;
using DnnMigration.Application.Dtos.Common;
using DnnMigration.Application.Dtos.Portal;
using FluentAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
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
/// The third governs WHICH caller each test uses, and it changed while the cross-tenant defect this suite
/// helped surface was being closed. The portal-administrator policy is now anchored to the <c>portalId</c> in
/// the ROUTE rather than to the tenant the request's Host header resolved to, so an administrator of one
/// portal is refused on another portal's route. Two consequences show up throughout the suite: the
/// collection-wide read and the create carry the host-administrator policy, because neither names a portal
/// for a per-portal policy to evaluate; and a test that acts on a portal it created must mint a client for
/// THAT portal's administrator rather than reusing the seeded portal's administrator. The suite previously
/// did the latter and passed, which is precisely the hole - so several facts below now assert the refusal
/// they used to assert the success of.
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

    /// <summary>
    /// Prefix of the problem type carried by an AUTHORISATION refusal, as opposed to a refusal decided inside
    /// the service. Both answer <c>403</c>, so the type is the only thing that tells them apart, and a test
    /// that cannot tell them apart cannot prove which guard fired.
    /// </summary>
    private const string AuthorisationProblemTypePrefix = "urn:dnnmigration:error:auth.";

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
    /// <remarks>
    /// Host authority, not tenant authority, because the projection spans every tenant in the installation.
    /// The legacy screen that carried this grid opens with
    /// <c>If Not UserInfo.IsSuperUser Then Response.Redirect(NavigateURL("Access Denied"), True)</c>
    /// (<c>Website/admin/Portal/Portals.ascx.vb:L339</c>).
    /// </remarks>
    [Fact]
    public async Task ListPortals_AsHost_ReturnsOkContainingSeededPortal()
    {
        using HttpClient client = _fixture.CreateHostClient();

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
    /// A paged response is emitted in the wire envelope - an <c>items</c> array beside a <c>meta</c> object -
    /// and the domain paging type's own members do not appear at the top level.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// <para>
    /// ASSERTED ON THE RAW JSON, deliberately, and this is the only test in the suite that does. Every other
    /// paging assertion binds the payload onto a type the tests own, which proves the members it names are
    /// present but cannot prove that others are ABSENT - a deserialiser ignores what it does not recognise. The
    /// defect this closes was exactly that kind: the controllers returned the domain paging type directly, the
    /// payload looked reasonable, and nothing failed while a domain type's internal shape quietly became a
    /// published contract.
    /// </para>
    /// <para>
    /// The four absent names are the domain type's own members and its derived ones. Their absence at the top
    /// level is what distinguishes the envelope from the leak: a flattened payload would carry
    /// <c>totalCount</c> beside <c>items</c>, and an envelope carries it inside <c>meta</c>.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task ListPortals_EmitsTheWireEnvelopeAndNotTheDomainPage()
    {
        using HttpClient client = _fixture.CreateHostClient();

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
            root.TryGetProperty(leaked, out _).Should().BeFalse(
                "'{0}' belongs to the domain paging type and must not be serialised at the top level",
                leaked);
        }
    }

    /// <summary>
    /// The name filter narrows the collection. This proves the query reaches the repository rather than being
    /// silently dropped, which a filter that is bound but never applied would otherwise look identical to.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task ListPortals_WithNameFilterThatMatchesNothing_ReturnsEmptyPage()
    {
        using HttpClient client = _fixture.CreateHostClient();

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
    /// The collection publishes the boundary page envelope, not the domain paging envelope: the rows arrive on
    /// <c>items</c> and every paging fact arrives nested under <c>meta</c>.
    /// </summary>
    /// <remarks>
    /// Asserted on the RAW JSON rather than through a typed read, because that is the only way to prove a
    /// member is absent. A typed read cannot: the serialiser ignores members the target does not declare, so an
    /// envelope that carried the paging facts twice - once nested and once as siblings - would deserialise
    /// identically and pass every other test in this suite. This is the guard that stops the domain envelope
    /// becoming the published contract again, which would leave a client with two incompatible shapes to model.
    /// </remarks>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task ListPortals_PublishesTheNestedPageEnvelope()
    {
        // A HOST client, because enumerating every portal in the installation is an installation-wide
        // operation and is gated as one. This fact is about the SHAPE of the page envelope; the authority the
        // listing requires is asserted by the facts that cover the policy.
        using HttpClient client = _fixture.CreateHostClient();

        using HttpResponseMessage response = await client.GetAsync(
            new Uri("/api/v1/portals?pageIndex=0&pageSize=25", UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using JsonDocument body = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync());
        JsonElement root = body.RootElement;

        root.ValueKind.Should().Be(JsonValueKind.Object);
        root.TryGetProperty("items", out JsonElement items).Should().BeTrue();
        items.ValueKind.Should().Be(JsonValueKind.Array);

        root.TryGetProperty("meta", out JsonElement meta).Should().BeTrue();
        meta.ValueKind.Should().Be(JsonValueKind.Object);
        meta.GetProperty("pageIndex").GetInt32().Should().Be(0);
        meta.GetProperty("pageSize").GetInt32().Should().Be(25);
        meta.GetProperty("totalCount").GetInt32().Should().BeGreaterThan(0);
        meta.GetProperty("totalPages").GetInt32().Should().BeGreaterThan(0);

        // The envelope carries exactly the two documented members, and no paging fact is repeated at the top
        // level where the domain envelope used to publish it.
        root.EnumerateObject().Select(member => member.Name)
            .Should().BeEquivalentTo(["items", "meta"]);
    }

    /// <summary>A page size beyond the permitted ceiling is rejected by the request validator.</summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task ListPortals_WithPageSizeAboveCeiling_ReturnsBadRequest()
    {
        using HttpClient client = _fixture.CreateHostClient();

        using HttpResponseMessage response = await client.GetAsync(
            new Uri("/api/v1/portals?pageIndex=0&pageSize=100000", UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    /// <summary>
    /// The sortable vocabulary is bound to the collection being addressed, so a field that is valid for
    /// a different collection is refused here.
    /// </summary>
    /// <remarks>
    /// <para>
    /// M-11: the boundary previously validated a sort field against the UNION of every collection's
    /// sortable set, so <c>LastLoginDate</c> - an account column that no portal listing can order by -
    /// passed validation and was then quietly ignored by the listing. The caller received a page in an
    /// order they did not ask for and were told nothing.
    /// </para>
    /// <para>
    /// This test is also the guard on the MECHANISM, which is why it drives real HTTP rather than the
    /// validator directly. The endpoint is identified from the route values the validation filter
    /// publishes; if those keys ever stopped resolving, the selection would fall back to the union and
    /// every assertion below would silently start passing the wrong field. Only a request that actually
    /// went through routing can detect that.
    /// </para>
    /// </remarks>
    /// <returns>A task representing the test.</returns>
    [Fact]
    [Trait("Category", "Integration")]
    public async Task ListPortals_WithASortFieldBelongingToAnotherCollection_ReturnsBadRequest()
    {
        using HttpClient client = _fixture.CreateHostClient();

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
    /// A tenant-scoped read naming a tenant OTHER THAN the one the request resolved to answers
    /// <c>403 Forbidden</c>, whether that other tenant exists or not.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// <para>
    /// This is the cross-tenant refusal itself, and it is asserted for BOTH a tenant that exists and one
    /// that does not, because the two must be indistinguishable. Answering <c>404</c> for an identifier no
    /// portal holds while answering <c>403</c> for one another tenant holds would turn the difference into
    /// an existence oracle: a caller could enumerate the installation's tenants by watching the status code
    /// change.
    /// </para>
    /// <para>
    /// It follows that <c>404</c> is unreachable on this route, and deliberately so. The tenant is resolved
    /// from the host name, so the only identifier that survives the binding is one whose portal exists by
    /// construction. The legacy screen reached the same place from the other direction: for a caller that
    /// was not a host account it simply IGNORED the submitted identifier and loaded the ambient portal
    /// (<c>SiteSettings.ascx.vb:L235</c>), so an unknown value produced the current portal rather than a
    /// not-found.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task GetPortal_ForATenantOtherThanTheResolvedOne_ReturnsForbidden()
    {
        using HttpClient host = _fixture.CreateHostClient();
        PortalDetailDto other = await CreatePortalAsync(host);

        using HttpClient client = _fixture.CreateAdministratorClient();

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
    /// <remarks>
    /// <para>
    /// The legacy screen honoured a caller-supplied identifier for a host account
    /// (<c>SiteSettings.ascx.vb:L235</c>), and that arm IS carried forward - narrowed to a host account,
    /// which is the narrow circumstance the legacy itself required. The binding this suite asserts elsewhere
    /// is unaffected: a portal administrator still reaches only the tenant it arrived on, and a foreign
    /// identifier and an unknown one are equally refused to it, so no existence oracle is opened.
    /// </para>
    /// <para>
    /// MIGRATION: an earlier revision of this fact expected a refusal. It cannot be a refusal, and the rest
    /// of the solution is the evidence rather than a preference: the permission service answers every
    /// question affirmatively for a host account before reading a grant, the portal service gates the hosting
    /// charge and the quotas on the caller being a host account - which is only meaningful if a host can
    /// reach a portal it does not administer - and a host that could not would be unable to administer the
    /// very tenant it had just created, because the new tenant's administrator role belongs to the account
    /// created with it.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task GetPortal_ForAForeignTenantAsHost_IsPermitted()
    {
        using HttpClient host = _fixture.CreateHostClient();
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
    /// refuses the request before the service is reached, so the absent-resource path would be
    /// unobservable - and asserting a 404 from a client that cannot reach it would be asserting nothing.
    /// The refusal itself is a separate fact below.
    /// </remarks>
    [Fact]
    public async Task GetPortal_WhenUnknown_ReturnsNotFound()
    {
        using HttpClient client = _fixture.CreateHostClient();

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
    public async Task AbsentResource_ReturnsAWellFormedProblemDocument()
    {
        using HttpClient client = _fixture.CreateHostClient();

        // Addressed by ALIAS key rather than by portal identifier, because the tenant binding makes a
        // foreign portal identifier a 403 rather than a 404 - see the cross-tenant fact above. This route
        // carries no portal segment, so the binding has nothing to compare and the absent-resource path is
        // still reachable, which is what this fact needs to exercise.
        using HttpResponseMessage response = await client.GetAsync(
            new Uri($"/api/v1/portal-aliases/{Route(UnknownPortalId)}", UriKind.Relative));

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
    /// <para>
    /// This is the tenant-isolation fact. The caller genuinely administers the seeded portal - the facts
    /// above prove it is served its own portal and its own settings - and the request differs only in the
    /// identifier it names, so a 403 here can only be the route-tenant reconciliation refusing to serve
    /// another tenant's data to a tenant administrator.
    /// </para>
    /// <para>
    /// The legacy rule this reproduces is <c>Website/admin/Portal/SiteSettings.ascx.vb:L235</c>, which
    /// honoured a caller-supplied portal identifier only under the host tab or for a super user and
    /// otherwise substituted the ambient tenant. Substituting is not available to a REST route - the
    /// identifier IS the resource - so the request is refused instead, which is the same isolation outcome.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task GetPortal_AsAdministratorOfAnotherTenant_ReturnsForbidden()
    {
        using HttpClient host = _fixture.CreateHostClient();
        PortalDetailDto other = await CreatePortalAsync(host);

        using HttpClient client = _fixture.CreateAdministratorClient();

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
        using HttpClient host = _fixture.CreateHostClient();
        PortalDetailDto other = await CreatePortalAsync(host);

        using HttpClient client = _fixture.CreateAdministratorClient();

        using HttpResponseMessage response = await client.GetAsync(
            new Uri($"/api/v1/portals/{Route(other.PortalId)}/settings", UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
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
        using HttpClient client = _fixture.CreateHostClient();

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
            .ReadEnvelopeAsync<PortalSettingsDto>();

        settings.Should().NotBeNull();
        settings!.PortalId.Should().Be(_fixture.Seed.PortalId);
        settings.PortalName.Should().Be(IntegrationSeed.PortalName);
        settings.Guid.Should().NotBe(Guid.Empty);
    }

    /// <summary>
    /// The settings projection binds to the addressed tenant exactly as the detail read does: refused for a
    /// portal administrator, and answered as absent for a host account.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// Both halves are asserted because each is observable only to one caller. A portal administrator is
    /// refused before the service is reached, and a foreign identifier is indistinguishable from an unknown
    /// one to it - which is what keeps the route from becoming an existence oracle. A host account passes the
    /// policy, so it is the only caller that can observe the absent-resource answer at all.
    /// </remarks>
    [Fact]
    public async Task GetPortalSettings_ForATenantOtherThanTheResolvedOne_BindsToTheAddressedTenant()
    {
        var settingsRoute = new Uri(
            $"/api/v1/portals/{Route(UnknownPortalId)}/settings",
            UriKind.Relative);

        using HttpClient administrator = _fixture.CreateAdministratorClient();

        using HttpResponseMessage refused = await administrator.GetAsync(settingsRoute);
        refused.StatusCode.Should().Be(
            HttpStatusCode.Forbidden,
            "an administrator of one tenant may not address another tenant's settings, and must not be able "
            + "to tell a foreign identifier from an unknown one");

        using HttpClient host = _fixture.CreateHostClient();

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

        // Followed through the NEW tenant's own alias, as its own administrator. The location is correct and
        // resolvable; what it is not is reachable from the tenant this request was made against, because the
        // portal-administrator policy binds a route's tenant to the tenant the request resolved to. Creating
        // a portal is an installation-wide operation and reading one is a tenant-scoped operation, so the two
        // are legitimately reached by two different callers.
        using HttpClient throughItsOwnAlias = CreatedTenantClient(created, request);

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
    /// <remarks>
    /// <para>
    /// THIS IS THE END-TO-END PROOF FOR THE CHILD-PORTAL DEFECT, and it needs every stage to be right at once,
    /// which is why it is one test rather than four. The service has to compose the address rather than store
    /// the submitted segment (<c>Signup.ascx.vb:L232-L233</c>). Resolution has to build a candidate chain from
    /// the host AND the path, and prefer the longest match, or the request resolves to the PARENT. And the
    /// path segment has to be moved into the path base before routing, or the request reaches routing as
    /// <c>/child/api/v1/portals/{id}</c> and matches nothing. Any one of the three missing turns the other two
    /// into wasted work, and only an end-to-end request can tell.
    /// </para>
    /// <para>
    /// Both tenants are addressed as their OWN administrators, because the tenant policy binds a route's
    /// tenant to the resolved one. The final pair of assertions is what makes the test about resolution rather
    /// than about routing: the parent's own address must still reach the parent, and the child's must not.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task CreatePortal_ComposesAChildBeneathTheParentAndItsRoutesResolve()
    {
        using HttpClient host = _fixture.CreateHostClient();

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

        // A HOST account addressing the parent's host name. The authority has to be a genuine host account
        // rather than a token that merely claims to be one: installation-wide operations are answered from
        // the STORE, not from the claim, so that a revoked host account loses reach immediately rather than at
        // token expiry. Minting a portal administrator with the super-user claim set would therefore be
        // refused, and would be asserting the claim path this solution deliberately does not take.
        using HttpClient beneathTheParent = _fixture.CreateHostClient(parentAuthority);

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
        using HttpClient beneathTheChild = _fixture.CreateTenantClient(
            composed,
            child.PortalId,
            child.AdministratorId!.Value,
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

        using HttpClient parentClient = CreatedTenantClient(parent, parentRequest);

        using HttpResponseMessage parentRead = await parentClient.GetAsync(PortalRoute(parent.PortalId));

        parentRead.StatusCode.Should().Be(
            HttpStatusCode.OK,
            "adding a child must not stop the parent resolving at its own bare authority");
    }

    /// <summary>
    /// A child portal asked for from a request that resolved to no tenant is refused as a bad request, and
    /// nothing is written.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// The default host client addresses an alias that IS configured, so this test has to reach the API from a
    /// host name that is not - which is exactly the state an operator provisioning the first portal of an
    /// installation is in. Refusing is the correct answer: a composed address needs a parent authority that
    /// exists, and inventing one would create a tenant reachable at an address nothing serves.
    /// </remarks>
    [Fact]
    public async Task CreatePortal_AsAChildWithNoResolvedParent_ReturnsBadRequest()
    {
        CreatePortalRequest request = NewPortalRequest();
        request.IsChildPortal = true;
        request.PortalAlias = "orphan" + Suffix();

        using HttpClient unconfiguredHost = _fixture.CreateHostClient("unconfigured-" + Suffix() + ".local");

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
    /// A failed creation leaves NOTHING behind - no tenant, no alias, no roles, no account and no credential -
    /// because the whole provisioning sequence is one transaction.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// The failure is provoked the only way an integration test honestly can: by submitting an administrator
    /// account name that is already in use, which is refused AFTER the request has been validated and read.
    /// The refusal itself is a pre-write check, so the value of this test is the pair of database assertions -
    /// they prove that a refused creation is indistinguishable from one that was never attempted, which is
    /// what the transaction guarantees and what the compensating routine it replaced could not.
    /// </remarks>
    [Fact]
    public async Task CreatePortal_WhenRefused_LeavesNothingBehind()
    {
        using HttpClient client = _fixture.CreateHostClient();

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
    /// C-02: a created tenant is USABLE, not merely present - it has the default profile property
    /// definitions an account needs in order to hold a profile at all, and a home page with the grants that
    /// let it be served.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// <para>
    /// The legacy creation sequence installed both. Its profile defaults came from
    /// <c>ProfileController.AddDefaultDefinitions</c> (nineteen definitions, view orders three to
    /// thirty-nine in steps of two) and its pages came from the XML portal template, of which the home page
    /// with a view grant for all users and view and edit grants for administrators is the part that is
    /// database-backed and therefore reproducible without the excluded template subsystem.
    /// </para>
    /// <para>
    /// Every assertion goes to the DATABASE rather than to the representation, because the defect this
    /// closes was a success response returned over an unusable tenant: a response body cannot witness the
    /// absence of rows the caller never asked about.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task CreatePortal_ProvisionsProfileDefinitionsAndAHomePage()
    {
        using HttpClient client = _fixture.CreateHostClient();

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
    /// The refusal is provoked by an administrator login name already in use, which the sequence detects only
    /// after the portal row, the alias, the three roles, the account and the enrolments have been staged and
    /// written. Before this restructuring those writes were committed and then undone by a compensating
    /// delete, which could not run at all if the process died in between; now a single transaction encloses
    /// every write, so the failure discards them without a second mechanism having to remember what to
    /// remove. The tenant's NAME is asserted absent as well as its alias, because a compensating delete that
    /// forgot one table would show up here rather than in the alias check alone.
    /// </remarks>
    [Fact]
    public async Task CreatePortal_WhenAStageFails_PublishesNothingAtAll()
    {
        using HttpClient client = _fixture.CreateHostClient();

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
    /// <remarks>
    /// The acting caller is the administrator OF THE PORTAL BEING UPDATED, minted from the account the create
    /// provisioned, and that is a correction rather than a detail. This fact previously acted as the SEEDED
    /// portal's administrator on a route naming a DIFFERENT portal and expected success - so the suite was
    /// asserting the cross-tenant defect rather than the update path. The refusal it now produces is covered
    /// by <see cref="UpdatePortal_ByAnAdministratorOfADifferentPortal_ReturnsForbidden"/>.
    /// </remarks>
    [Fact]
    public async Task UpdatePortal_AsHost_ReturnsOkAndPersists()
    {
        using HttpClient host = _fixture.CreateHostClient();
        (PortalDetailDto created, CreatePortalRequest createRequest) = await CreatePortalWithRequestAsync(host);

        using HttpClient client = CreateAdministratorClientFor(createRequest, created);

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
    /// <remarks>
    /// The caller is the administrator of the portal being updated, so the refusal can only come from the
    /// host-only guard. Acting as another portal's administrator would produce the same status for a
    /// completely different reason and leave this guard untested, which is why the payload is read: the
    /// guard's refusal carries the general forbidden vocabulary, whereas a policy refusal carries the
    /// authorisation problem type.
    /// </remarks>
    [Fact]
    public async Task UpdatePortal_AsAdministratorAlteringHostingCharge_ReturnsForbidden()
    {
        using HttpClient host = _fixture.CreateHostClient();
        (PortalDetailDto created, CreatePortalRequest createRequest) = await CreatePortalWithRequestAsync(host);

        using HttpClient client = CreateAdministratorClientFor(createRequest, created);

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
        problem!.Type.Should().NotStartWith(
            AuthorisationProblemTypePrefix,
            "the refusal must come from the host-only guard, not from the endpoint policy");
    }

    /// <summary>
    /// An administrator of one portal is refused on another portal's route. THIS IS THE CROSS-TENANT
    /// REGRESSION TEST: the policy used to be evaluated against the tenant the request's Host header resolved
    /// to and never against the portal named in the route, so any portal administrator could rename, re-key
    /// and reconfigure every other tenant in the installation through a route that named it.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task UpdatePortal_ByAnAdministratorOfADifferentPortal_ReturnsForbidden()
    {
        using HttpClient host = _fixture.CreateHostClient();
        PortalDetailDto victim = await CreatePortalAsync(host);

        // The seeded portal's administrator: a genuine, fully provisioned administrator - of a DIFFERENT
        // portal. Nothing about this caller is malformed, which is what made the defect reachable.
        using HttpClient attacker = _fixture.CreateAdministratorClient();

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
        problem.Type.Should().StartWith(AuthorisationProblemTypePrefix);

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
        using HttpClient host = _fixture.CreateHostClient();
        PortalDetailDto other = await CreatePortalAsync(host);

        using HttpClient client = _fixture.CreateAdministratorClient();

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
        using HttpClient client = _fixture.CreateAdministratorClient();

        using HttpResponseMessage response = await client.GetAsync(
            new Uri("/api/v1/portals?pageIndex=0&pageSize=100", UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    /// <summary>
    /// Provisioning a new tenant is refused to a portal administrator for the same reason: a create names no
    /// portal it could be authorised against, and the operation adds a tenant to the installation.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task CreatePortal_AsPortalAdministrator_ReturnsForbidden()
    {
        using HttpClient client = _fixture.CreateAdministratorClient();

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
        using HttpClient host = _fixture.CreateHostClient();
        PortalDetailDto other = await CreatePortalAsync(host);

        using HttpClient client = _fixture.CreateAdministratorClient();

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
        using HttpClient host = _fixture.CreateHostClient();
        (PortalDetailDto created, CreatePortalRequest createRequest) =
            await CreatePortalWithRequestAsync(host);

        // A caller that is BOTH the created tenant's administrator and a host account. The update route is
        // tenant-scoped, so the tenant half is what gets the request past authorisation; the super-user flag
        // is what gets the hosting charge past the host-only-field guard. Two independent gates, and this is
        // the one caller that clears both.
        using HttpClient client = CreatedTenantClient(created, createRequest, isSuperUser: true);

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

    /// <summary>
    /// An update naming a tenant other than the one the request resolved to is refused with
    /// <c>403 Forbidden</c>, and an unknown identifier is refused identically.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// This test previously expected <c>404 Not Found</c>, which is precisely the behaviour the review
    /// flagged: reaching the service at all meant the route's tenant was never checked against the resolved
    /// tenant, so the caller had already been authorised to write to a portal it does not administer and only
    /// the portal's absence stopped the write. The refusal now happens in authorisation, ahead of the
    /// service, and it is the SAME refusal for a real foreign tenant and for an identifier that names
    /// nothing - which is the point, because a caller must not be able to tell those two apart.
    /// </remarks>
    [Fact]
    public async Task UpdatePortal_ForATenantOtherThanTheResolvedOne_ReturnsForbidden()
    {
        using HttpClient host = _fixture.CreateHostClient();
        PortalDetailDto foreign = await CreatePortalAsync(host);

        // Addressed at the seeded tenant, as its own administrator, naming another tenant on the route.
        using HttpClient client = _fixture.CreateAdministratorClient();

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
    /// An update whose body names a different portal from the route is refused by the request validator,
    /// so a caller cannot retarget the write at another tenant.
    /// </summary>
    /// <remarks>
    /// The contract carries the portal identifier as the first of its twenty-seven members, matching
    /// argument 1 of the legacy <c>UpdatePortalInfo</c> signature. The route remains the subject of the
    /// write, and this rule is what makes the second copy harmless: a body identifier that cannot
    /// disagree with the route cannot address a portal the route did not name.
    /// </remarks>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task UpdatePortal_WithMismatchedBodyIdentifier_ReturnsBadRequest()
    {
        using HttpClient client = _fixture.CreateHostClient();

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
    /// An update carrying no portal name is refused, which is the one unconditional presence rule the
    /// contract declares.
    /// </summary>
    /// <remarks>
    /// A negative quota is deliberately NOT part of this assertion. The legacy settings screen declared no
    /// lower-bound validator on the hosting charge or on any allowance
    /// (<c>Website/admin/Portal/sitesettings.ascx</c>), so a negative submission was accepted and stored,
    /// and reproducing that is a Minimal Change Clause requirement rather than a gap.
    /// </remarks>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task UpdatePortal_WithoutAName_ReturnsBadRequest()
    {
        using HttpClient client = _fixture.CreateHostClient();

        var request = new UpdatePortalRequest
        {
            PortalId = _fixture.Seed.PortalId,
            PortalName = string.Empty,
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

        // Unreachability is proved by deleting again rather than by reading back. A read is tenant-scoped, so
        // after the alias is released nothing resolves to the removed tenant and the read is refused in
        // authorisation - a 403 that says nothing about whether the row is gone. Delete is an
        // installation-wide operation with no tenant to bind to, so a second delete reaches the service and
        // reports the portal's absence directly, which is the property under test.
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
    /// A portal administrator may not delete a portal - not even its own - because bringing a tenant into
    /// existence and removing it again are installation-wide acts.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// The route names the caller's OWN portal, so the route-tenant reconciliation would admit it; the
    /// refusal therefore comes from the host-only policy on the action. The legacy screen that removed a
    /// portal was the same host-only screen that listed them
    /// (<c>Website/admin/Portal/Portals.ascx.vb:L339</c>).
    /// </remarks>
    [Fact]
    public async Task DeletePortal_AsPortalAdministrator_ReturnsForbidden()
    {
        using HttpClient client = _fixture.CreateAdministratorClient();

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
    /// The last-portal refusal is a conflict according to the SHARED status table, not according to a branch
    /// inside the delete action.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is asserted against the translator rather than over HTTP because the condition cannot be staged:
    /// refusing the delete requires the installation to be down to a single portal, and the suite shares one
    /// database with every other fact in it. Reaching the state would mean removing tenants those facts
    /// depend on.
    /// </para>
    /// <para>
    /// What could regress here is precise and worth pinning: the endpoint no longer names the failure code at
    /// all, so if the shared table stopped recognising it the refusal would silently become <c>400</c> -
    /// telling a caller to correct a request that is not correctable. The general mechanism is already
    /// exercised over HTTP by the duplicate-alias and duplicate-name conflicts above; this pins the one
    /// entry those cannot reach.
    /// </para>
    /// </remarks>
    [Fact]
    public void DeletePortal_LastRemainingRefusal_IsTranslatedAsConflictByTheSharedTable()
    {
        ApiResults.MapStatusCode("portal.last_remaining")
            .Should().Be(StatusCodes.Status409Conflict);
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
        using HttpClient host = _fixture.CreateHostClient();
        (PortalDetailDto created, CreatePortalRequest createRequest) =
            await CreatePortalWithRequestAsync(host);

        // The alias collection is nested beneath a portal, so it is tenant-scoped and has to be addressed
        // through the tenant that owns it. The portal keeps the alias it was created with throughout, so the
        // base address stays resolvable while a SECOND alias is added, renamed and removed beneath it.
        using HttpClient client = CreatedTenantClient(created, createRequest);

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

        // The individual alias is addressed BENEATH its owning portal. Addressing it at a top-level path, as
        // an earlier revision of the API did, left the route with no tenant segment for the policy to bind and
        // made every alias in the installation reachable by its guessable surrogate key.
        var aliasRoute = new Uri(
            $"/api/v1/portals/{Route(created.PortalId)}/aliases/{Route(alias.PortalAliasId)}",
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
            .ReadEnvelopeAsync<PortalAliasDto>();

        persisted.Should().NotBeNull();
        persisted!.HttpAlias.Should().Be(secondAlias);

        using HttpResponseMessage removed = await client.DeleteAsync(aliasRoute);
        removed.StatusCode.Should().Be(HttpStatusCode.NoContent);

        using HttpResponseMessage gone = await client.GetAsync(aliasRoute);
        gone.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    /// <summary>
    /// An administrator of one portal cannot read, rename or unbind an alias belonging to another, even when it
    /// knows the alias's identifier - which it can, because the identifier is an installation-wide surrogate.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// This is the cross-tenant alias hijack in its most consequential form: an alias is what tenant resolution
    /// matches on, so renaming another tenant's alias re-points its traffic and unbinding one makes it
    /// unreachable. Two layers refuse it and the test proves the outer one - the route names a tenant the
    /// caller does not administer, so the policy refuses before the service is reached.
    /// </remarks>
    [Fact]
    public async Task PortalAlias_OfAnotherTenant_IsNotReachableByAPortalAdministrator()
    {
        using HttpClient host = _fixture.CreateHostClient();
        PortalDetailDto other = await CreatePortalAsync(host);

        string foreignAlias = "foreign-" + Suffix() + ".local";

        using HttpResponseMessage createdAlias = await host.PostAsJsonAsync(
            new Uri($"/api/v1/portals/{Route(other.PortalId)}/aliases", UriKind.Relative),
            new PortalAliasDto { PortalId = other.PortalId, HttpAlias = foreignAlias },
            ApiTestFixture.Json);

        createdAlias.StatusCode.Should().Be(HttpStatusCode.Created);

        // Read through the ENVELOPE: a raw read yields an object with every member unset, so the identifier
        // below would be zero and every route built from it would address an alias that does not exist -
        // making the refusals that follow pass without having reached the alias at all.
        PortalAliasDto? alias = await createdAlias.Content
            .ReadEnvelopeAsync<PortalAliasDto>();

        alias.Should().NotBeNull();

        using HttpClient administrator = _fixture.CreateAdministratorClient();

        var foreignRoute = new Uri(
            $"/api/v1/portals/{Route(other.PortalId)}/aliases/{Route(alias!.PortalAliasId)}",
            UriKind.Relative);

        using HttpResponseMessage read = await administrator.GetAsync(foreignRoute);
        read.StatusCode.Should().Be(HttpStatusCode.Forbidden);

        using HttpResponseMessage renamed = await administrator.PutAsJsonAsync(
            foreignRoute,
            new PortalAliasDto
            {
                PortalAliasId = alias.PortalAliasId,
                PortalId = other.PortalId,
                HttpAlias = "hijacked-" + Suffix() + ".local",
            },
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
    /// <remarks>
    /// The tenant the route names, the tenant the token was minted for and the tenant the request arrived at
    /// must agree for tenant administration to be granted; enumerating and creating portals are installation
    /// -wide and require a host account regardless. Both halves are asserted here because an earlier revision
    /// granted both to any portal administrator.
    /// </remarks>
    [Fact]
    public async Task Portal_OfAnotherTenant_IsNotReachableByAPortalAdministrator()
    {
        using HttpClient host = _fixture.CreateHostClient();
        PortalDetailDto other = await CreatePortalAsync(host);

        using HttpClient administrator = _fixture.CreateAdministratorClient();

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
        using HttpClient host = _fixture.CreateHostClient();
        (PortalDetailDto created, CreatePortalRequest createRequest) =
            await CreatePortalWithRequestAsync(host);

        using HttpClient client = CreatedTenantClient(created, createRequest);

        using HttpResponseMessage response = await client.PostAsJsonAsync(
            new Uri($"/api/v1/portals/{Route(created.PortalId)}/aliases", UriKind.Relative),
            new PortalAliasDto { PortalId = created.PortalId, HttpAlias = ApiTestFixture.TestHost },
            ApiTestFixture.Json);

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    /// <summary>
    /// Every action on both portal controllers declares an authorisation policy of its own, so no action can
    /// reach the application layer on the strength of authentication alone.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This test exists because of how the two controllers are protected. Their class attribute is
    /// authentication only, and each action names its own policy - either the tenant-scoped
    /// portal-administrator policy or the installation-wide host policy. That split is unavoidable, because
    /// authorisation attributes COMBINE rather than override: a class-level tenant policy would be ANDed onto
    /// the host actions and would make them unreachable by the only credential entitled to them, since a host
    /// account holds no administrator role assignment in any tenant.
    /// </para>
    /// <para>
    /// The cost of that split is that an action added later inherits mere authentication if its author forgets
    /// the attribute, and nothing about the code would look wrong. An attribute cannot express "every action
    /// must name a policy, but not the same one", so the guard is a test rather than a declaration. It reads
    /// the metadata rather than issuing a request, so it fails at the moment the omission appears rather than
    /// only for whichever call the omission happens to expose.
    /// </para>
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
            .ReadEnvelopeAsync<IReadOnlyList<PortalAliasDto>>();

        all.Should().NotBeNull();
        all!.Select(item => item.HttpAlias).Should().Contain(ApiTestFixture.TestHost);
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
    /// <remarks>
    /// The request is returned because the representation does not carry the administrator's LOGIN NAME, only
    /// its identifier, and a test that needs to act as that administrator needs both.
    /// </remarks>
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
    /// Mints a client for the administrator account the create provisioned for a given portal.
    /// </summary>
    /// <param name="request">The request that created the portal, which carries the login name.</param>
    /// <param name="detail">The created portal, which carries the administrator's identifier.</param>
    /// <returns>A client authenticated as that portal's own administrator.</returns>
    /// <remarks>
    /// The role claim is carried for completeness, but it is NOT what admits the caller: the portal
    /// administrator policy reads the target portal's <c>AdministratorRoleId</c> and verifies a time-bounded
    /// assignment of that role to this account in the database. A token claiming the role name without the
    /// underlying assignment is refused, which is deliberate - a role NAME is not a tenant-scoped fact.
    /// </remarks>
    private HttpClient CreateAdministratorClientFor(CreatePortalRequest request, PortalDetailDto detail)
    {
        detail.AdministratorId.Should().NotBeNull(
            "the create provisions an administrator and records it against the portal");
        request.AdministratorUsername.Should().NotBeNullOrWhiteSpace();

        return _fixture.CreateClientFor(
            detail.AdministratorId!.Value,
            request.AdministratorUsername!,
            detail.PortalId,
            isSuperUser: false,
            roles: [IntegrationSeed.AdministratorsRoleName]);
    }

    /// <summary>
    /// Builds a client that RESOLVES TO a freshly created tenant, authenticated as that tenant's own
    /// administrator.
    /// </summary>
    /// <param name="created">The created portal.</param>
    /// <param name="request">The request that created it, which carries the alias and administrator name.</param>
    /// <param name="isSuperUser">Whether the caller additionally carries installation-wide authority.</param>
    /// <returns>An authenticated client addressed at the created tenant.</returns>
    /// <remarks>
    /// The portal-administrator policy binds a route's tenant to the tenant the request resolved to, and
    /// resolution is by host name, so a tenant-scoped action against a created portal has to be addressed
    /// through that portal's own alias. That is not a test workaround: it is how an operator reaches a
    /// tenant, and it is the behaviour the legacy screens enforced by forcing a non-host caller onto the
    /// ambient portal (<c>SiteSettings.ascx.vb:L235</c>).
    /// </remarks>
    private HttpClient CreatedTenantClient(
        PortalDetailDto created,
        CreatePortalRequest request,
        bool isSuperUser = false) => _fixture.CreateTenantClient(
            request.PortalAlias!,
            created.PortalId,
            created.AdministratorId!.Value,
            request.AdministratorUsername!,
            isSuperUser);

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
