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
    /// Problem type carried by every refusal of an authenticated but unentitled caller, whichever guard
    /// decided it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// MIGRATION: THREE FACTS IN THIS CLASS USED TO IDENTIFY WHICH GUARD REFUSED BY REQUIRING THE PROBLEM TYPE
    /// NOT TO BEGIN <c>urn:dnnmigration:error:auth.</c>, AND THAT DISCRIMINATOR NO LONGER EXISTS - it worked
    /// only because of a defect. This API answered a refusal decided by the authorisation layer with its own
    /// identifier and a refusal decided inside a service with an RFC 9110 specification link, because the
    /// framework's client-error registration was consulted before this API's vocabulary and claimed the type
    /// for every status the vocabulary would otherwise have named. A caller consequently received two
    /// different problem-type vocabularies for the same status depending on which producer answered, which is
    /// the defect the single taxonomy removes. Once removed, both refusals carry this one type - correctly,
    /// since to a client they are the same kind of problem, and the controller's own contract note records
    /// that the field guard "reaches the caller as a single, deliberate 403".
    /// </para>
    /// <para>
    /// The security property those facts exist to prove - that the caller got PAST the endpoint policy and was
    /// refused by the store-backed field guard - is therefore proven behaviourally instead, by showing the
    /// same client with the same token succeeds on an update that alters no host-only field. That is direct
    /// evidence rather than an inference from a payload member, so it is strictly stronger than what it
    /// replaces: a payload member can be identical for two different causes, whereas a policy that refused
    /// the caller could not have admitted the control request.
    /// </para>
    /// </remarks>
    private const string AuthorisationRefusalProblemType = "urn:dnnmigration:error:auth.not_permitted";

    /// <summary>
    /// Problem type carried by the refusal to remove an installation's only remaining portal.
    /// </summary>
    /// <remarks>
    /// Spelled out rather than composed from the API's own builder, which is internal to that assembly. That
    /// is the right way round for an integration suite: the type is part of the PUBLISHED contract, so
    /// writing it here proves the value a client will actually branch on, whereas asking the producer to
    /// build it would let both sides move together and assert nothing.
    /// </remarks>
    private const string LastRemainingProblemType = "urn:dnnmigration:error:portal.last_remaining";

    /// <summary>Problem type carried by the refusal to bind an alias a portal already holds.</summary>
    private const string DuplicateAliasProblemType = "urn:dnnmigration:error:portal.alias_duplicate";

    /// <summary>
    /// Problem type carried by the refusal to rename or unbind the alias the CURRENT REQUEST resolved the
    /// tenant through.
    /// </summary>
    /// <remarks>
    /// MIGRATION: restores the legacy screen's <c>IsNotCurrent</c> affordance
    /// (<c>Website/admin/Portal/PortalAlias.ascx.vb</c> L51-L60) as an enforced rule. Deliberately distinct
    /// from <see cref="DuplicateAliasProblemType"/>, because a client can act on this one - reach the portal
    /// through another of its host names - whereas a duplicate requires a different value.
    /// </remarks>
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
    /// <remarks>
    /// Host authority, not tenant authority, because the projection spans every tenant in the installation.
    /// The legacy screen that carried this grid opens with
    /// <c>If Not UserInfo.IsSuperUser Then Response.Redirect(NavigateURL("Access Denied"), True)</c>
    /// (<c>Website/admin/Portal/Portals.ascx.vb:L339</c>).
    /// </remarks>
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
    /// The name filter narrows the collection. This proves the query reaches the repository rather than being
    /// silently dropped, which a filter that is bound but never applied would otherwise look identical to.
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
        using HttpClient client = await _fixture.CreateHostClientAsync();

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
    /// refuses the request before the service is reached, so the absent-resource path would be
    /// unobservable - and asserting a 404 from a client that cannot reach it would be asserting nothing.
    /// The refusal itself is a separate fact below.
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
    /// The administrator selector's candidates are the members of the portal's administrator role,
    /// ordered by the name the selector shows.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// <para>
    /// Reproduces <c>Website/admin/Portal/SiteSettings.ascx.vb:L329-L339</c>. The seed is what makes this
    /// discriminating: the host account and the portal administrator both hold the Administrators role
    /// while the ordinary member holds Registered Users, so a read that returned every account in the
    /// portal - the obvious wrong implementation, and the one the write path's own broader guard would
    /// permit - would return three rows here instead of two.
    /// </para>
    /// <para>
    /// The ordering is asserted rather than the mere membership. The underlying membership read orders by
    /// role and then by assignment key, which is right for a membership grid and wrong for a name picker,
    /// so an unordered projection would put the host first purely because it was inserted first.
    /// </para>
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

    /// <summary>
    /// The stored administrator is among the candidates, so the selector can pre-select it.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// The legacy screen pre-selected the entry matching <c>objPortal.AdministratorId</c> at
    /// <c>SiteSettings.ascx.vb:L337-L339</c>, and it could only do so because the stored administrator was
    /// necessarily a member of the role the list was built from. That relationship is asserted here rather
    /// than assumed, because a candidate list that excluded the current holder would silently offer the
    /// operator a form whose only options all CHANGE the administrator.
    /// </remarks>
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
    /// <remarks>
    /// The distinction matters to the screen: an empty list means "this portal has no eligible
    /// administrators", which is a state it must render, whereas a missing portal is not a state it can
    /// render at all.
    /// </remarks>
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
    /// weaker one pass for the wrong reason. An ANONYMOUS caller gets 401, because there is no credential to
    /// evaluate a policy against; an authenticated ordinary member gets 403, because the policy was
    /// evaluated and refused. The read discloses account names, so neither may reach it.
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

    /// <summary>
    /// A chosen candidate can be stored through the settings resource and is served back by it.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// The whole point of the finding, end to end: the legacy screen read the candidates at
    /// <c>SiteSettings.ascx.vb:L331-L336</c> and wrote the chosen one as argument nine of the portal
    /// update at <c>:L775</c>. This drives both halves against the real database, so a candidate the read
    /// offers is provably a value the write accepts - the two contracts cannot drift apart unnoticed.
    /// </remarks>
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
        request.PortalName = "   ";

        using HttpResponseMessage response = await client.PutAsJsonAsync(
            route,
            request,
            ApiTestFixture.Json);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        IReadOnlyDictionary<string, string[]> errors = await ReadValidationErrorsAsync(response);
        errors.Should().ContainKey(nameof(UpdatePortalSettingsRequest.PortalName));
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
    /// <remarks>
    /// <para>
    /// MIGRATION: the STATUS is the success signal, and that replaces a legacy defect rather than merely a
    /// legacy convention. The sign-up screen called a creation routine that returned the new identifier and
    /// signalled failure by returning -1 from its own exception handler, then tested the outcome with
    /// <c>If intPortalId &lt;&gt; -1</c> (<c>Website/admin/Portal/Signup.ascx.vb</c> lines 273 to 280). But -1
    /// is a perfectly legal <c>PortalID</c> in this schema - <c>Portals.PortalID</c> is
    /// <c>IDENTITY (-1, 1)</c>, so it is the FIRST identifier an installation issues - which means the very
    /// first portal ever created reported itself as a failure. The outcome is carried separately from the
    /// value here, so no identifier can be mistaken for a failure and none is reserved.
    /// </para>
    /// <para>
    /// MIGRATION: the legacy routine wrote across the portal, alias, role, page and module tables as a
    /// sequence of independent statements with no transaction spanning them
    /// (<c>Library/Components/Portal/PortalController.vb</c> line 980), so a failure part way through left a
    /// half-built tenant behind. The companion facts below assert the replacement behaviour from the other
    /// side: a refused create publishes nothing at all.
    /// </para>
    /// </remarks>
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

        // Followed through the NEW tenant's own alias, as its own administrator. The location is correct and
        // resolvable; what it is not is reachable from the tenant this request was made against, because the
        // portal-administrator policy binds a route's tenant to the tenant the request resolved to. Creating
        // a portal is an installation-wide operation and reading one is a tenant-scoped operation, so the two
        // are legitimately reached by two different callers.
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

        // A HOST account addressing the parent's host name. The authority has to be a genuine host account
        // rather than a token that merely claims to be one: installation-wide operations are answered from
        // the STORE, not from the claim, so that a revoked host account loses reach immediately rather than at
        // token expiry. Minting a portal administrator with the super-user claim set would therefore be
        // refused, and would be asserting the claim path this solution deliberately does not take.
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
    /// template, but the contract still demands one, so this proves the validator is attached to the action.
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
    /// host-only guard. Acting as another portal's administrator would produce the same status for a
    /// completely different reason and leave this guard untested, which is why the payload is read: the
    /// guard's refusal carries the general forbidden vocabulary, whereas a policy refusal carries the
    /// authorisation problem type.
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
    /// SEC-011: route, token and arrival tenant all agree, and the account holds the target portal's stored
    /// administrator role. The only false statement is the token's super-user claim; the account row remains
    /// non-host, so the application guard must refuse after authorisation has succeeded.
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
    /// SEC-011 and SEC-006 meet here: the caller is a real administrator of portal A and the token says it is
    /// a host account, but the store says otherwise. The claim therefore cannot become the host exemption that
    /// would let an A-token cross onto an existing B-route.
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
        // submission it refused altered a host-only field, which the field guard would have refused on its own
        // - so that refusal alone cannot show the endpoint policy fired. This one alters NOTHING the field
        // guard inspects, so the field guard would permit it; a refusal here can only be the policy's, which
        // is what proves the stale claim was rejected before the request ever reached the service.
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
    /// Provisioning a new tenant is refused to a portal administrator for the same reason: a create names no
    /// portal it could be authorised against, and the operation adds a tenant to the installation.
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

        // THE SEEDED HOST ACCOUNT performs the update as well as the create, and it is the one caller that
        // clears both of the two independent gates this route carries: the portal-administrator policy admits
        // a host account before it looks at any tenant, and the host-only-field guard admits it because the
        // account genuinely IS an installation superuser.
        //
        // An earlier revision asked for the created tenant's administrator carrying a hand-minted super-user
        // claim, and that arrangement was unreachable in production: the policy reads the flag from the stored
        // row and would have refused it, while the field guard reads it from the claim and would have allowed
        // it - so the pass depended on a token the sign-in endpoint could never issue. The persona is now the
        // account that really holds the authority, which is also what this fact's name says it is.
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
        using HttpClient client = await _fixture.CreateHostClientAsync();

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
        using HttpClient client = await _fixture.CreateHostClientAsync();
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
    /// A delete answers <c>204 No Content</c> for a tenant that owns modules, and leaves neither the modules
    /// nor their placements, settings or grants behind.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// <para>
    /// THE ONE FOREIGN KEY INTO <c>dbo.Portals</c> WITHOUT A CASCADE IS <c>FK_Modules_Portals</c>. Every
    /// other one - alias, portal desktop module, role group, role, page, membership, profile declaration -
    /// carries <c>ON DELETE CASCADE</c>, so a tenant with no module deletes cleanly and a tenant with one
    /// module was refused by the store. That refusal arrived as an undeclared <c>500</c>: the operation
    /// publishes <c>204</c>, <c>401</c>, <c>403</c>, <c>404</c> and <c>409</c> and nothing else, so the
    /// response was outside its own contract.
    /// </para>
    /// <para>
    /// The asymmetry is not a mapping defect to be corrected in the schema - the terminal legacy schema
    /// declares it, the 03.00.09 upgrade script re-adding the constraint with no cascade clause - so the
    /// service compensates in the same place the legacy application did. The terminal
    /// <c>DeletePortalInfo</c> procedure opens with <c>DELETE FROM Modules WHERE PortalId = @PortalId</c>
    /// before deleting the tenant row, and that order is what this asserts.
    /// </para>
    /// <para>
    /// The dependents of the module are asserted too, because removing the module rows is only sufficient if
    /// their placements, settings and grants really do follow. Those three keys DO cascade from
    /// <c>dbo.Modules</c>, so nothing removes them explicitly and an orphan would be invisible to any
    /// assertion aimed only at the module table.
    /// </para>
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
    /// that would otherwise outlive it, while an account shared with another tenant survives with that other
    /// membership intact.
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
    /// <remarks>
    /// The route names the caller's OWN portal, so the route-tenant reconciliation would admit it; the
    /// refusal therefore comes from the host-only policy on the action. The legacy screen that removed a
    /// portal was the same host-only screen that listed them
    /// (<c>Website/admin/Portal/Portals.ascx.vb:L339</c>).
    /// </remarks>
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
    /// The last-portal refusal is a conflict according to the SHARED status table, not according to a branch
    /// inside the delete action.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This asserts the TRANSLATION and nothing else. The refusal itself is exercised over HTTP by
    /// <see cref="DeletePortal_WhileItIsTheOnlyPortal_ReturnsConflictAndKeepsIt"/>, which stages the
    /// condition on a fixture of its own - an earlier revision of this file argued the condition could not be
    /// staged at all, and that argument is superseded. What remains worth pinning here is narrower and
    /// cheaper: the mapping from the failure code to the status lives in a SHARED table rather than in a
    /// branch inside the delete action, so this reads the table directly. The two facts fail for different
    /// reasons - this one when the table entry is lost, the other one when the rule itself is - which is why
    /// keeping both is not duplication.
    /// </para>
    /// <para>
    /// What could regress here is precise: the endpoint no longer names the failure code at all, so if the
    /// shared table stopped recognising it the refusal would silently become <c>400</c> - telling a caller to
    /// correct a request that is not correctable. Reading the table costs no database and no host, so it
    /// reports that specific regression immediately rather than only as part of a longer end-to-end fact.
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
    /// <remarks>
    /// MIGRATION: the legacy removal reported an unknown portal as a SUCCESS. It looked the portal up, and
    /// when the lookup produced nothing it fell through to the end of the routine and returned the empty
    /// string - which was its success value, the only non-empty message it ever produced being the
    /// last-portal refusal. A caller therefore could not tell "removed" from "there was nothing to remove",
    /// and a mistyped identifier reported a clean removal. That is corrected rather than reproduced, and it
    /// matters more than it looks: an operator who believed a tenant had been decommissioned when it had not
    /// is left with a live portal they think is gone.
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
    [Fact]
    public async Task PortalAliases_SupportCreateUpdateAndDelete()
    {
        using HttpClient host = await _fixture.CreateHostClientAsync();
        (PortalDetailDto created, CreatePortalRequest createRequest) =
            await CreatePortalWithRequestAsync(host);

        // The alias collection is nested beneath a portal, so it is tenant-scoped and has to be addressed
        // through the tenant that owns it. The portal keeps the alias it was created with throughout, so the
        // base address stays resolvable while a SECOND alias is added, renamed and removed beneath it.
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

        // The individual alias is addressed BENEATH its owning portal. Addressing it at a top-level path, as
        // an earlier revision of the API did, left the route with no tenant segment for the policy to bind and
        // made every alias in the installation reachable by its guessable surrogate key.
        var aliasRoute = new Uri(
            $"/api/v1/portals/{Route(created.PortalId)}/aliases/{Route(alias.PortalAliasId)}",
            UriKind.Relative);

        string secondAlias = "renamed-" + Suffix() + ".local";

        using HttpResponseMessage updated = await client.PutAsJsonAsync(
            aliasRoute,
            new UpdatePortalAliasRequest { HttpAlias = secondAlias },
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
    /// The alias the CURRENT REQUEST resolved the tenant through is reported as current, and both a rename
    /// and an unbinding of it answer <c>409 Conflict</c> while leaving the row exactly as it was - whereas a
    /// second alias the request did not arrive through stays fully writable.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// <para>
    /// MIGRATION: this is the end-to-end proof of the restored <c>IsNotCurrent</c> rule from
    /// <c>Website/admin/Portal/PortalAlias.ascx.vb</c> L51-L60, where the legacy grid compared each row's key
    /// against the ambient <c>PortalAlias.PortalAliasID</c> and <c>portalalias.ascx</c> L8 bound the answer to
    /// the edit hyperlink's visibility. The consequence of losing it is unrecoverable rather than merely
    /// untidy: the host name the operator is arriving through stops resolving to the tenant, for every caller
    /// using it, and the screen that would undo the change becomes unreachable.
    /// </para>
    /// <para>
    /// Both halves are asserted in one test on purpose. The refusal and the <c>IsCurrent</c> flag are two
    /// statements of one fact, and a suite that proved them separately could pass while they disagreed - a
    /// screen that hid the affordance on the wrong row, or showed it on a row the server would refuse, is
    /// exactly the defect this rule exists to prevent.
    /// </para>
    /// </remarks>
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

        using HttpResponseMessage spareUpdate = await client.PutAsJsonAsync(
            spareRoute,
            new UpdatePortalAliasRequest { HttpAlias = "spare-renamed-" + Suffix() + ".local" },
            ApiTestFixture.Json);

        spareUpdate.StatusCode.Should().Be(HttpStatusCode.NoContent);

        using HttpResponseMessage spareRemoved = await client.DeleteAsync(spareRoute);
        spareRemoved.StatusCode.Should().Be(HttpStatusCode.NoContent);
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
        using HttpClient host = await _fixture.CreateHostClientAsync();
        PortalDetailDto other = await CreatePortalAsync(host);

        string foreignAlias = "foreign-" + Suffix() + ".local";

        using HttpResponseMessage createdAlias = await host.PostAsJsonAsync(
            new Uri($"/api/v1/portals/{Route(other.PortalId)}/aliases", UriKind.Relative),
            new CreatePortalAliasRequest { HttpAlias = foreignAlias },
            ApiTestFixture.Json);

        createdAlias.StatusCode.Should().Be(HttpStatusCode.Created);

        // Read through the ENVELOPE: a raw read yields an object with every member unset, so the identifier
        // below would be zero and every route built from it would address an alias that does not exist -
        // making the refusals that follow pass without having reached the alias at all.
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
    /// <remarks>
    /// The tenant the route names, the tenant the token was minted for and the tenant the request arrived at
    /// must agree for tenant administration to be granted; enumerating and creating portals are installation
    /// -wide and require a host account regardless. Both halves are asserted here because an earlier revision
    /// granted both to any portal administrator.
    /// </remarks>
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
    /// <remarks>
    /// <para>
    /// ASSERTED ON THE RAW JSON, because the whole failure mode is a member that is ABSENT or NULL rather
    /// than one that is wrong. A typed read cannot see the difference - a missing <c>portalId</c> binds to
    /// zero and a missing <c>administratorRoleId</c> binds to null, and both would then be compared against
    /// an expectation that a suite could easily write to match.
    /// </para>
    /// <para>
    /// MIGRATION: this is the sentinel collision the migration plan calls Rule T7, and it is a genuine
    /// collision rather than a theoretical one. <c>Library/Components/Shared/Null.vb</c> lines 41 to 43
    /// return -1 for an absent integer, while <c>Website/Providers/DataProviders/SqlDataProvider/01.00.00.SqlDataProvider</c>
    /// line 77 declares <c>Portals.PortalID</c> as <c>IDENTITY (-1, 1)</c>, so -1 is simultaneously the
    /// legacy "no value" marker and the first portal an installation creates. Line 115 of the same script
    /// declares <c>Roles.RoleID</c> as <c>IDENTITY (0, 1)</c>, so zero collides in the same way. Serialising
    /// with a condition that drops nulls or defaults would erase exactly these two facts, which is why the
    /// shared serialiser settings pin the ignore condition to never and why this fact reads the text.
    /// </para>
    /// <para>
    /// MIGRATION: a fourth meaning was loaded onto -1 by <c>PortalAliasController.vb</c> line 87, where the
    /// unscoped alias listing passes -1 to the by-portal lookup as an ALL PORTALS wildcard. Nothing in this
    /// suite treats -1 as absent, as a wildcard, or as a failure marker; it is an identifier.
    /// </para>
    /// </remarks>
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
    /// <para>
    /// MIGRATION: this is the other half of Rule T7, and the sharper half. Every other sentinel in
    /// <c>Library/Components/Shared/Null.vb</c> is a distinguished value, but lines 71 to 73 return the
    /// EMPTY STRING for an absent string, so the legacy contract could not tell a stored null from a stored
    /// empty string at all - both arrived as <c>""</c>. The boundary here keeps them apart, which means the
    /// empty string has to survive as itself. A serialiser configured to drop nulls, or to drop defaults,
    /// would turn a caller's deliberate "clear this field" into a member that never appears, and the next
    /// read would report the previous value as though the write had not happened.
    /// </para>
    /// <para>
    /// Five members are exercised rather than one because they reach the row by different routes - three are
    /// plain descriptive columns and two belong to the payment block - and a mapper that special-cased one
    /// group would otherwise pass. The RAW JSON is read for the same reason as the fact above: a member that
    /// was dropped binds to null on a typed read and null is indistinguishable from a stored null.
    /// </para>
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
    /// <para>
    /// MIGRATION: the hosting charge is the most misread column in this table and the migration plan cites
    /// its baseline declaration - <c>[HostFee] [nvarchar] (10) NULL</c> at
    /// <c>Website/Providers/DataProviders/SqlDataProvider/01.00.00.SqlDataProvider</c> line 89, a fee stored
    /// as free text, seeded with the empty string at line 7125. That is the BASELINE and not the schema this
    /// application binds to. <c>03.01.01.SqlDataProvider</c> line 1118 converts the column with
    /// <c>ALTER COLUMN [HostFee] [money] NOT NULL</c> and line 1129 adds a <c>DEFAULT (0)</c> constraint, and
    /// the terminal procedures declare the parameter <c>money</c> (<c>04.04.00.SqlDataProvider</c> lines 97
    /// and 319). The migration plan's own rule is that entity configurations bind to the cumulative terminal
    /// schema and never to the baseline alone, so the value is a number here and asserting that it travelled
    /// as the empty string would pin a contract two schema versions out of date.
    /// </para>
    /// <para>
    /// MIGRATION: what does survive from that history is the reason the legacy grid rendered an empty cell.
    /// <c>Website/admin/Portal/portals.ascx</c> line 47 binds the column with
    /// <c>DataFormatString="{0:0.00}"</c>, a NUMERIC format applied to what was then a string value, and a
    /// numeric format string is silently ignored for a string - so an installation still carrying the
    /// baseline type rendered nothing rather than "0.00". Neither spelling is reproduced: the value is
    /// published unformatted, so it is neither the preformatted text the grid asked for nor the empty cell
    /// the grid actually produced, and a client formats it for display.
    /// </para>
    /// <para>
    /// The four members are asserted TOGETHER because they share one failure mode. All four are nought on a
    /// freshly created portal, so a serialiser that omitted defaults would drop all four at once and a
    /// client would be unable to distinguish "no charge and no limits" from "this server did not say".
    /// </para>
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
    /// <para>
    /// Both values the legacy sentinel table overloads are driven through the ROUTE SEGMENT position here;
    /// the fact below drives the same two values through the REQUEST BODY position, because the two are
    /// bound by different machinery and a defect in one would not show up in the other.
    /// </para>
    /// <para>
    /// The assertion is deliberately "not a rejection" rather than a fixed status. Which portals exist at
    /// the moment this runs depends on what the rest of the collection has created and removed, so pinning
    /// <c>200</c> or <c>404</c> would make the fact order-dependent and it would fail for a reason that has
    /// nothing to do with sentinels. What must never happen is the failure this guards: a zero or negative
    /// identifier being read as "no identifier supplied" and answered with a bad request, or a route
    /// constraint refusing to bind it at all.
    /// </para>
    /// <para>
    /// Declared as a theory over two values rather than as two facts because the body is identical; the
    /// parameter is a plain <c>int</c> and both inline values are real integers, so nothing here relies on
    /// a null literal standing in for a missing value.
    /// </para>
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
    /// <remarks>
    /// <para>
    /// The point is that zero is not silently forgiven. An update is a whole-row replacement whose body
    /// carries the identifier as a plain integer, so a body that OMITS it binds to zero - and if zero were
    /// read as "not supplied" the reconciliation would be skipped and a caller could update one portal
    /// through another portal's route. The companion fact below submits nothing at all and proves the same
    /// refusal, which is what closes that hole from both directions.
    /// </para>
    /// </remarks>
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
    /// <remarks>
    /// MIGRATION: the legacy save path had no equivalent hazard because the identifier never travelled in a
    /// submitted form at all - <c>Website/admin/Portal/SiteSettings.ascx.vb</c> line 235 took it from the
    /// request's own query string and, for a caller that was not a host account, IGNORED it and substituted
    /// the ambient portal. A REST route cannot substitute, because the identifier is the resource, so the
    /// two identifiers are reconciled instead and a disagreement is reported.
    /// </remarks>
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
    /// MIGRATION: the legacy listing reported its total through a <c>ByRef</c> argument -
    /// <c>GetPortalsByName(Filter + "%", CurrentPage - 1, PageSize, TotalRecords)</c> at
    /// <c>Website/admin/Portal/Portals.ascx.vb</c> line 142 - and the same -1 that
    /// <c>Library/Components/Shared/Null.vb</c> lines 41 to 43 return for an absent integer was passed as an
    /// index, a size and a total alike to mean "everything". The paging contract replaces that with a total
    /// and an explicit unpaged factory, so -1 can no longer appear in any of the three positions. A client
    /// that divided the total to derive a page count would produce nonsense from a negative one, which is
    /// why this is asserted rather than assumed.
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

    /// <summary>
    /// A listed row carries exactly the columns the legacy grid rendered, and nothing else.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// <para>
    /// The eight members are the eight data columns declared in <c>Website/admin/Portal/portals.ascx</c>, in
    /// the order the grid declared them: the identifier at line 23, the site title at line 30, the alias list
    /// at line 37, the account count at line 44, the page count at line 45, the disc-space allowance at
    /// line 46, the hosting charge at line 47 and the expiry at line 48. The row is the LIST projection and
    /// is deliberately narrower than the detail representation, which is what keeps a grid read cheap.
    /// </para>
    /// <para>
    /// Asserted on the RAW JSON and as an exact set, because both directions of drift matter. A member that
    /// disappeared would break a grid that renders it, and a member that appeared would quietly widen a list
    /// projection into a detail one - and a typed read cannot see either, since it ignores what it does not
    /// declare and defaults what is missing.
    /// </para>
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
    /// <remarks>
    /// <para>
    /// MIGRATION: the predicate is the LEGACY PREFIX MATCH, and the primary sources fix it beyond doubt.
    /// The pattern was assembled at the call site, not in the procedure:
    /// <c>Website/admin/Portal/Portals.ascx.vb</c> line 142 reads
    /// <c>GetPortalsByName(Filter + "%", CurrentPage - 1, PageSize, TotalRecords)</c> - one TRAILING
    /// wildcard and no leading one - and the surviving procedure applies it unchanged with
    /// <c>WHERE PortalName LIKE @NameToMatch</c>
    /// (<c>Website/Providers/DataProviders/SqlDataProvider/04.04.00.SqlDataProvider</c> lines 245 to 269).
    /// <c>LIKE 'j%'</c> is a prefix test, so a mid-string fragment matched nothing in the legacy grid and
    /// matches nothing here.
    /// </para>
    /// <para>
    /// ⚠ THIS FACT REPLACES ONE THAT PINNED A CONTAINMENT MATCH, AND THE REPLACEMENT IS THE POINT. The
    /// earlier revision widened the predicate on the reasoning that a widening loses no legacy result.
    /// It loses something a result count cannot show: the FILTER STRIP'S MEANING. The listing's strip is
    /// an A-to-Z index of twenty-six single letters, named "Filter portals by first letter", and the
    /// strip and the free-text box share one filter parameter - so against a containment match pressing
    /// "A" returns every title carrying an "a" anywhere, which on a populated installation is very nearly
    /// all of them. Runtime testing measured that. Rule T5 asks for identical outcomes wherever
    /// equivalence is achievable, and here it plainly is.
    /// </para>
    /// <para>
    /// MIGRATION: the WILDCARD HARDENING is retained and is a separate concern from the predicate's
    /// shape. Because the legacy pattern was string concatenation, a filter containing <c>%</c> or
    /// <c>_</c> acted as a WILDCARD - a single per cent sign matched every portal in the installation.
    /// Expressed relationally the caller's text is a value rather than a pattern, so those characters
    /// match themselves, which is asserted below by filtering on each and expecting nothing back.
    /// </para>
    /// </remarks>
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
    /// A delete is REFUSED with <c>409 Conflict</c> while only one portal remains, and the portal it refused
    /// to remove is still there afterwards.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// <para>
    /// STAGED ON A HOST OF ITS OWN, and that is the whole reason this fact can exist. The condition is
    /// "the installation is down to one portal", which cannot be reached on the shared database without
    /// removing tenants the rest of the collection depends on. A second fixture provisions its OWN throwaway
    /// database and seeds exactly one portal, so the condition holds there by construction and the shared
    /// database is never touched. The refusal itself is non-destructive by definition - it is decided before
    /// anything is removed - so the isolated portal survives it, which is asserted below.
    /// </para>
    /// <para>
    /// The environment override wrapping the isolated fixture is not decoration. A fixture publishes its
    /// database and signing key by SETTING PROCESS ENVIRONMENT VARIABLES, because the composition root reads
    /// configuration while it is composing services and nothing contributed later would arrive in time. A
    /// second fixture therefore overwrites the shared fixture's variables, and any host built afterwards would
    /// compose against a database that had already been dropped. The fixture now captures and restores what it
    /// overwrote, so the second fixture puts the shared values back itself - but this scope stays, because it
    /// is what makes the guarantee hold even when the inner fixture cannot complete its own disposal, and
    /// because being wrong about this costs the remainder of the run rather than one fact.
    /// </para>
    /// <para>
    /// MIGRATION: the legacy removal reported outcomes by returning a MESSAGE STRING, and the only non-empty
    /// message it ever produced was the localised "LastPortal" wording, raised when the portal count had
    /// fallen to one or below. An empty string meant success. Two consequences are reproduced deliberately
    /// and one is not: the refusal survives as a conflict carrying a named code, and the wording survives as
    /// the detail; but a caller no longer has to distinguish success from failure by testing a string for
    /// emptiness, which is what made the sibling defect below possible.
    /// </para>
    /// <para>
    /// MIGRATION: the legacy removal also deleted the portal's home directory beneath a hard-coded
    /// <c>"Portals\"</c> path. That work is deliberately dropped - the path separator alone cannot work on
    /// the Linux images this solution ships - so a delete here releases database references only and this
    /// suite asserts no file-system effect.
    /// </para>
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
    /// <para>
    /// Both halves are asserted together because the failure modes are opposite and each would hide the
    /// other. A handler that always generated would discard the caller's value while still answering with a
    /// header, and a handler that only echoed would leave an unstamped request untraceable.
    /// </para>
    /// <para>
    /// The cardinality matters as much as the value. The header is SET rather than appended, so a response
    /// carries one identifier; two would leave a log reader unable to say which request they were holding,
    /// and a client reading the first value would disagree with a proxy reading the last.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task CorrelationId_IsEchoedWhenSuppliedAndGeneratedWhenNot()
    {
        using HttpClient client = await _fixture.CreateAdministratorClientAsync();

        string supplied = "portal-suite-" + Suffix();

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

        string supplied = "portal-failure-" + Suffix();

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
    /// <para>
    /// The specific risk is log forging. The identifier is written into structured log events, so a value
    /// carrying a line break could inject a whole fabricated entry, and an unbounded value could bloat every
    /// event a request produces. Both are handled by DISCARDING the value rather than by sanitising it, which
    /// is the safer choice: there is no partially-trusted remnant left to reason about.
    /// </para>
    /// <para>
    /// The status assertion is the other half and is easy to get wrong in the opposite direction. A hostile
    /// header is not a malformed request - the caller may not even have set it - so refusing the request with
    /// a bad request would turn a proxy's stray header into an outage. The request is served and the header
    /// is quietly replaced.
    /// </para>
    /// <para>
    /// The values are added without client-side validation, because the framework's own header validation
    /// would otherwise refuse to send exactly the values under test and the request would never reach the
    /// pipeline this fact is about.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData("with-a-tab\tand-more", "a control character that could forge a log entry")]
    [InlineData("   ", "whitespace, which identifies nothing")]
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
    /// <remarks>
    /// Separated from the theory above because the value has to be CONSTRUCTED rather than written inline: a
    /// long literal in an attribute would be unreadable and would hide the one thing that matters about it,
    /// which is that it exceeds the bound. Truncation is asserted against as well as echoing, because a
    /// truncated value is still caller-controlled text and still forges just as well.
    /// </remarks>
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
    /// <para>
    /// THE MEDIA TYPE IS ASSERTED HERE AND NOT EVERYWHERE, and the distinction is measured rather than
    /// assumed. Refusals written by the authorisation result handler set the RFC 7807 media type explicitly,
    /// so it is part of their contract and pinning it protects it. A problem document produced by a
    /// controller action is negotiated as <c>application/json</c> by the framework instead; that deviation is
    /// recorded in the migration notes rather than pinned by a test, because asserting the value the
    /// framework currently emits would cement it and make a later correction look like a regression.
    /// </para>
    /// <para>
    /// Both refusals are asserted in one theory because they are the same mechanism reached from two states,
    /// and because the pair is what proves the vocabulary is per-reason: an implementation that answered both
    /// with one type would leave a client unable to tell "prove who you are" from "you may not do this".
    /// </para>
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

        // MIGRATION: the legacy equivalent of this refusal was not a status at all. Every in-scope admin
        // screen guarded itself with PortalSecurity.IsInRoles and, on failure, issued
        // Response.Redirect(NavigateURL("Access Denied"), True) - a 302 to a rendered page, which an API
        // client would follow and then parse as though it were the resource. A plain status is answered
        // instead, so nothing here may be a redirect.
        problem.Status.Should().NotBe(StatusCodes.Status302Found);
        response.Headers.Location.Should().BeNull(
            "a refusal is reported by status rather than by redirecting to a rendered page");
    }

    /// <summary>
    /// A validation failure names the OFFENDING FIELDS in its <c>errors</c> dictionary and reproduces the
    /// validator's own wording.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// <para>
    /// Asserting the status alone would be nearly worthless here: a bad request tells a caller that
    /// something is wrong and an <c>errors</c> dictionary tells them WHICH field, which is what a form binds
    /// its messages to. The keys are therefore asserted as an exact set, so that a rule which stopped
    /// reporting - or one that reported under a renamed key - fails rather than degrading quietly.
    /// </para>
    /// <para>
    /// MIGRATION: the wording travels byte for byte from the validators, which took it from the legacy
    /// screens' own resources, so a message a user recognised in the legacy console is the message they see
    /// now. That is why nothing here reformats or re-cases the text - the assertion is on the exact string.
    /// </para>
    /// </remarks>
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
    /// <para>
    /// MIGRATION: THIS IS THE DEFECT THE EXACT-MATCH RESOLVER EXISTS TO CLOSE, and it is a behavioural
    /// improvement recorded here rather than smuggled in. The legacy resolver matched the stored alias column
    /// against a pattern padded on BOTH sides -
    /// <c>where PortalAlias like '%' + @PortalAlias + '%'</c> at
    /// <c>Website/Providers/DataProviders/SqlDataProvider/01.00.00.SqlDataProvider</c> line 4582 - and then
    /// took <c>min(PortalID)</c> of whatever matched (line 4580). Because <c>Portals.PortalID</c> is
    /// <c>IDENTITY(-1, 1)</c>, "whatever matched" was resolved in favour of the numerically lowest portal,
    /// which is the oldest one. A host name sitting inside another portal's alias therefore resolved to the
    /// WRONG TENANT, and the tie was broken by identifier order rather than by correctness. The product
    /// abandoned the procedure outright - it is dropped at <c>02.02.00.SqlDataProvider</c> line 267 and no
    /// later script recreates it.
    /// </para>
    /// <para>
    /// The migration discipline is normally to annotate a discovered defect rather than fix it. This is the
    /// documented exception: carrying a cross-tenant mis-resolution into new code would reproduce a real
    /// security fault, not merely an oddity.
    /// </para>
    /// <para>
    /// THE REFUSAL IS THE SUBTLE HALF, and this assertion was inverted by SEC-006. It used to assert that the
    /// request was still SERVED, on the reasoning that the route names its portal and so needs no host name to
    /// identify one. That reasoning was the defect: a route segment is a claim about which tenant to act on,
    /// chosen by the caller, and accepting it in place of a resolved arrival tenant meant the tenant a request
    /// belonged to could be chosen by addressing the installation from a name that resolves to nothing. The
    /// substring host is refused now, and refused for the RIGHT reason - the tenant one - which is asserted
    /// explicitly, because a refusal arriving from some unrelated cause would prove nothing about aliases.
    /// </para>
    /// <para>
    /// THE PROPERTY THIS FACT EXISTS FOR IS UNCHANGED AND STRONGER. The legacy predicate mis-resolved a
    /// substring host to the wrong tenant; the target resolves it to no tenant, and now serves it nothing at
    /// all. The body is inspected as well as the status, because what has to be excluded is not merely a
    /// non-success code but a SUCCESSFUL answer carrying some other tenant's portal.
    /// </para>
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

    /// <summary>
    /// A host name differing only in CASE resolves the same tenant.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// <para>
    /// MIGRATION: case-insensitivity is inherited rather than invented. The legacy alias controller
    /// lower-cased the stored host name on every write and on every read
    /// (<c>Library/Components/Portal/PortalAliasController.vb</c> lines 31, 52, 75 to 76 and 97), so a
    /// mixed-case request resolved perfectly well there and a case-SENSITIVE comparison here would lose
    /// behaviour the legacy product had.
    /// </para>
    /// <para>
    /// The comparison is culture-independent, and that is a correctness requirement rather than a
    /// preference: culture-sensitive lower-casing maps the dotted capital I differently under a Turkish
    /// locale, which would make tenant resolution depend on the server's regional settings.
    /// </para>
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
    /// portal claims, and so does the collection route, which names no portal for a per-portal rule to read.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// <para>
    /// The health probe is the load-bearing case. The shipped composition declares the frontend container
    /// dependent on the API reporting healthy, so a probe that required a resolved tenant would leave the
    /// frontend unable to start on any installation whose alias table was not yet configured - which is
    /// every installation, at the moment it is first brought up.
    /// </para>
    /// <para>
    /// A route that DOES depend on the host name for its tenant is included as the negative control, because
    /// without one this fact would pass equally well against a resolver that had been switched off
    /// altogether. That refusal is a forbidden, not an absent resource and not a bad request: the request is
    /// well formed and the address exists, and what is missing is an entitlement to a tenant on this host.
    /// </para>
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
    /// <remarks>
    /// The sub-resource is what makes this worth asserting separately from the portal create. A location
    /// naming only the alias identifier would not address anything on this API, because the alias lives
    /// beneath its portal, and the header would be a broken link that no status code reports. It is followed
    /// rather than merely parsed, which is the only assertion that proves it addresses the created row.
    /// </remarks>
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
    /// <para>
    /// MIGRATION: SEC-F6. Measured against a live installation before the fix: eight simultaneous identical
    /// bindings produced one 201, four 409 and THREE 500s, with exactly one row stored. <c>IX_PortalAlias</c>
    /// is unique over the host name and it did exactly its job; the three racers it refused were nevertheless
    /// told the server had failed. The existence check in front of the insert cannot close that window,
    /// because every racer reads "not bound" before any of them commits.
    /// </para>
    /// <para>
    /// This matters more here than on any other contested resource, because an alias is how a tenant is
    /// resolved at all. A second row for one host name would make resolution ambiguous for that address, so
    /// the assertion that exactly one row survives is a tenant-isolation assertion and not merely a tidiness
    /// one.
    /// </para>
    /// <para>
    /// What the fact asserts is the OUTCOME - one binding, every other caller refused under the same code the
    /// sequential check emits, no 5xx, one row. It does not assert which mechanism refused a given caller,
    /// because the check and the index answer identically by design and which one wins depends on scheduling;
    /// asserting that would be asserting a race. The translation is pinned deterministically by
    /// <c>DuplicateKeyTranslationTests</c> and by the service-level facts.
    /// </para>
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
    /// <para>
    /// MIGRATION: the legacy settings screen compared exactly these six submitted values against the stored
    /// portal and, when a caller who was not a super user had changed any of them, executed
    /// <c>Throw New System.Exception</c> - a bare exception carrying NO MESSAGE
    /// (<c>Website/admin/Portal/SiteSettings.ascx.vb</c> lines 757 to 768). Reproducing that literally would
    /// surface as a <c>500</c>, telling a caller the server had broken when in fact the server had
    /// deliberately refused them. The refusal survives; its expression as an unhandled fault does not. That
    /// this answers 403 and not 500 is therefore the whole point of the fact.
    /// </para>
    /// <para>
    /// All six are exercised because they are six separate comparisons in one guard and a guard that had
    /// lost one term would still pass a single-field test. They run against ONE portal, sequentially, which
    /// is sound precisely because each attempt is refused: nothing is written, so the stored row is identical
    /// before and after every case and the cases cannot interfere. That is asserted at the end rather than
    /// assumed.
    /// </para>
    /// <para>
    /// MIGRATION: the retention period is changed to -1 and the change is refused, which is worth noting
    /// because -1 is the legacy absent-integer sentinel
    /// (<c>Library/Components/Shared/Null.vb</c> lines 41 to 43) and the legacy screen submitted it raw as
    /// the "keep nothing" value. It is a VALUE here, not an absence, so submitting it is a change like any
    /// other and the guard treats it as one.
    /// </para>
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
    /// <remarks>
    /// <para>
    /// MIGRATION: the meaning is measured, not inferred. The legacy account screen enforced the member
    /// allowance only when it was greater than nought - <c>If PortalSettings.UserQuota &gt; 0 And ...</c> at
    /// <c>Website/admin/Users/ManageUsers.ascx.vb</c> line 367 - so nought disabled the check entirely. A
    /// boundary rule that refused nought, or a mapper that treated it as absent and substituted a default,
    /// would silently impose a limit on every portal that had none.
    /// </para>
    /// <para>
    /// Submitted by a HOST account, because the allowances are host-only and the fact above proves that
    /// separately. What is under test here is the value, not the authority.
    /// </para>
    /// </remarks>
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

    /// <summary>
    /// Removing a portal's only remaining alias is PERMITTED, and the portal survives with none.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// <para>
    /// Recorded because the opposite is the natural assumption and it is wrong: an installation may not be
    /// left with no portal, so it would be reasonable to expect that a portal may not be left with no
    /// address either. No such rule exists in the legacy application and none has been added -
    /// <c>Library/Components/Portal/PortalAliasController.vb</c> removes an alias unconditionally, and adding
    /// a rule the legacy console did not have would refuse a save an operator could previously make.
    /// </para>
    /// <para>
    /// The consequence is real and is stated here rather than hidden: a portal with no alias cannot be
    /// reached by host name, and the endpoints that repair that are deliberately reachable without a resolved
    /// tenant so that an operator can bind a new one. The recovery path is asserted, which is what makes
    /// permitting the removal defensible rather than merely permissive.
    /// </para>
    /// <para>
    /// MIGRATION: the removal is therefore issued by a caller that did NOT reach the installation through the
    /// alias being removed, and the distinction is the whole reason this test still stands. There is no
    /// last-alias rule - that is what is asserted here - but there IS an active-alias rule: the row the
    /// CURRENT REQUEST resolved through cannot be renamed or unbound, restoring the legacy screen's
    /// <c>IsNotCurrent</c> affordance (<c>PortalAlias.ascx.vb</c> L51-L60) and proven by
    /// <see cref="PortalAlias_TheRequestResolvedThrough_IsCurrentAndCannotBeRenamedOrUnbound"/>. The two facts
    /// are orthogonal and only LOOK contradictory when a single-alias portal is addressed through its own
    /// alias, where both would bite at once. Removing the last address is permitted; removing the address you
    /// are standing on is not, because that refusal has no in-application recovery for the caller who made
    /// the request.
    /// </para>
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

        // Issued by the HOST client, which reached the installation through the SEEDED portal's alias and so
        // is not standing on the row it is removing. Through the tenant's own client this same call is
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
    /// <para>
    /// MIGRATION: THE LEGACY INPUT NORMALISATION IS NOT REPRODUCED, and this fact exists to record that
    /// rather than to endorse it. The sign-up screen mutated the caller's text before validating it -
    /// <c>strPortalAlias = LCase(txtPortalAlias.Text)</c> and then
    /// <c>strPortalAlias = Replace(strPortalAlias, "http://", "")</c>
    /// (<c>Website/admin/Portal/Signup.ascx.vb</c> lines 182 to 184) - which is exactly why the character
    /// whitelist it applied next contained no upper-case letters: by the time the check ran the case and the
    /// scheme were already gone. Here the text reaches the alias table untouched, so the two halves of the
    /// legacy behaviour have come apart: the input is still ACCEPTED, as it was there, but it is no longer
    /// NORMALISED.
    /// </para>
    /// <para>
    /// The consequence is worth stating plainly, because it is the reason this is annotated rather than left
    /// to be discovered. Tenant resolution compares a request's host name against the stored alias, and no
    /// host name can carry a scheme, so an alias stored in this form matches nothing and that portal cannot
    /// be reached by address. It is a usability fault rather than a security one - the portal is not
    /// exposed to anyone, it is merely unreachable by name - and the two mitigations that make it recoverable
    /// are asserted below: the portal is still served on a route that NAMES it, and the alias endpoints,
    /// which are deliberately reachable without a resolved tenant, can bind a usable address in its place.
    /// </para>
    /// <para>
    /// Asserted as the behaviour the endpoint HAS rather than the behaviour the legacy screen had, because a
    /// test that demanded folding would fail against the shipped service and would say nothing about what a
    /// client should expect today. Correcting the service is a change to the application layer and is
    /// recorded here as a hand-off, not reached across from a test.
    /// </para>
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
    /// <remarks>
    /// <para>
    /// MIGRATION: the legacy check walked the submitted alias one character at a time and appended the same
    /// explanation to its message every time it met a character outside the permitted set
    /// (<c>Website/admin/Portal/Signup.ascx.vb</c> lines 186 to 217), so an alias with three bad characters
    /// produced the identical sentence three times, separated by line breaks. That repetition is NOT
    /// reproduced. It carried no information a caller could act on - the message never named WHICH character
    /// offended - and a form binding one message per field would have rendered the same sentence three
    /// times over.
    /// </para>
    /// <para>
    /// The wording itself is preserved, which is the part that matters for parity: a user who recognised the
    /// legacy sentence sees the same sentence. Only its multiplicity changed, and this fact pins the count so
    /// that the decision is visible rather than accidental.
    /// </para>
    /// </remarks>
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
    /// refused before the lookup runs and would answer 403 - which is deliberate, and is asserted separately.
    /// A host account is the only caller for whom the absent path is reachable at all, so it is the only
    /// caller that can prove this.
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
    /// <remarks>
    /// <para>
    /// Asserted because absence is a requirement here rather than an accident, and because absence is the one
    /// property no other fact in this suite can establish - every other fact would pass equally well against
    /// an API that had grown extra endpoints beside the ones it exercises.
    /// </para>
    /// <para>
    /// MIGRATION: each address corresponds to a legacy screen or capability the migration scope excludes.
    /// Portal templates and the multi-step site wizard were rendered by <c>template.ascx</c> and
    /// <c>sitewizard.ascx</c>, neither of which is ported; the expired-portal listing was a filter on the
    /// legacy grid backed by a separate retrieval routine, and no endpoint publishes it; a key-and-value
    /// settings surface has no table behind it at all, because portal configuration lives in COLUMNS on the
    /// portal row and the legacy per-request settings object was an ambient composite rather than a stored
    /// aggregate; and aliases are addressed beneath their portal rather than as a child of the collection.
    /// </para>
    /// <para>
    /// An absent address answering <c>404</c> is the correct outcome and not a weaker one: it is routing
    /// truthfully reporting that nothing is published there, which is exactly what a client discovering the
    /// API needs to be told.
    /// </para>
    /// </remarks>
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
    /// <para>
    /// A bulk removal is the one absent capability whose absence a <c>404</c> would not demonstrate, because
    /// the address exists for reading and creating. What proves it is the METHOD being refused, and the
    /// refusal carries the permitted verbs so a client is told what the address does support.
    /// </para>
    /// <para>
    /// MIGRATION: THIS FACT USED TO REQUIRE AN EMPTY BODY, AND THAT EXPECTATION IS SUPERSEDED. A refusal
    /// decided by routing - the wrong method here, an unmatched address elsewhere - reached the caller with no
    /// payload at all, while every refusal decided further in carried a problem document. A client therefore
    /// had to special-case two of this API's statuses as bodiless before it could parse any error, which is
    /// exactly the second parsing path the single error contract exists to remove. Both are now answered with
    /// the standard document.
    /// </para>
    /// <para>
    /// The <c>Allow</c> header is asserted here for the first time. It is what this remark always claimed the
    /// refusal carried, and the claim went unchecked - so adding the payload had to be accompanied by proving
    /// the header survived it, since a status-code page that replaced the response wholesale would have
    /// silently dropped the one header that tells the caller what to do instead.
    /// </para>
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
    /// <para>
    /// MIGRATION: the credential policy is carried forward VERBATIM from the membership provider the legacy
    /// application registered - a minimum length of seven characters, no requirement for a non-alphanumeric
    /// character, and no question-and-answer requirement (<c>Website/release.config</c> lines 237 to 247).
    /// Tightening any of those during a migration would refuse credentials that existing operators already
    /// use, so the boundary is asserted from BOTH sides here: a six-character password is refused, and a
    /// seven-character all-letters password - which a modern default policy would reject outright - is
    /// accepted.
    /// </para>
    /// <para>
    /// The second half is the half that matters. A test that only proved a weak password was refused would
    /// pass just as well against a policy that had been silently strengthened, which is the regression this
    /// guards.
    /// </para>
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
    /// No member of the portal surface reports an outcome through a by-reference parameter. Every one of them
    /// returns its result.
    /// </summary>
    /// <returns>Nothing; this fact reads metadata rather than issuing a request.</returns>
    /// <remarks>
    /// <para>
    /// MIGRATION: this is the idiom the migration replaces, and it was pervasive rather than incidental. The
    /// legacy listing returned its rows and reported the total through a <c>ByRef</c> argument
    /// (<c>Website/admin/Portal/Portals.ascx.vb</c> line 142), the create returned an identifier and
    /// signalled failure by returning a value that was also a legal identifier, and the update took
    /// twenty-seven positional parameters
    /// (<c>Website/admin/Portal/SiteSettings.ascx.vb</c> lines 772 to 780). All three are replaced by typed
    /// contracts: a page carries its own total, an outcome carries its own failure reason, and an update takes
    /// one request object.
    /// </para>
    /// <para>
    /// Asserted against the METADATA rather than over HTTP, because a by-reference parameter is not
    /// observable in a response - it is a shape that cannot be expressed on the wire at all, which is exactly
    /// why it has to be excluded at the boundary rather than tested through it. Reading the signatures is
    /// what makes the exclusion a failing test instead of a convention.
    /// </para>
    /// <para>
    /// Both the controllers and the application contract they delegate to are inspected. A controller alone
    /// would prove too little: the shape that matters is the one the service publishes, since that is what a
    /// future controller would be written against.
    /// </para>
    /// </remarks>
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
    /// Signs in as the administrator account the create provisioned for a given portal, WITHOUT addressing
    /// that portal's host.
    /// </summary>
    /// <param name="request">The request that created the portal, which carries the alias and login name.</param>
    /// <param name="detail">The created portal, which carries the administrator's identifier.</param>
    /// <returns>A client authenticated as that portal's own administrator.</returns>
    /// <remarks>
    /// <para>
    /// No role claim is asserted by the test, because none would help: the portal administrator policy reads
    /// the target portal's <c>AdministratorRoleId</c> and verifies a time-bounded assignment of that role to
    /// this account in the database. A token naming the role without the underlying assignment is refused,
    /// which is deliberate - a role NAME is not a tenant-scoped fact. What the token must carry is the TENANT
    /// it was issued for, and it carries it because the credential is presented at the created portal's own
    /// alias, which is the only place that account can present it.
    /// </para>
    /// <para>
    /// The returned client then addresses the SEEDED host rather than the created portal's, and the difference
    /// is the point: every route these callers use names its portal, and a named route is decided against the
    /// route and the token rather than against the host the request arrived at. Keeping the addressed host
    /// unchanged is what leaves that binding measured rather than assumed.
    /// </para>
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
    /// <para>
    /// The portal-administrator policy binds a route's tenant to the tenant the request resolved to, and
    /// resolution is by host name, so a tenant-scoped action against a created portal has to be addressed
    /// through that portal's own alias. That is not a test workaround: it is how an operator reaches a
    /// tenant, and it is the behaviour the legacy screens enforced by forcing a non-host caller onto the
    /// ambient portal (<c>SiteSettings.ascx.vb:L235</c>).
    /// </para>
    /// <para>
    /// The caller no longer states an account identifier or an installation-wide flag. Both are read from the
    /// store by the sign-in endpoint while it composes the token, so stating them here could only ever
    /// contradict what the store holds - and a caller that genuinely holds installation-wide authority is the
    /// seeded host account, obtained from <c>CreateHostClientAsync</c>.
    /// </para>
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

    /// <summary>
    /// Reads the per-field <c>errors</c> dictionary out of a validation failure.
    /// </summary>
    /// <param name="response">The refused response.</param>
    /// <returns>The offending field names, each with the messages reported against it.</returns>
    /// <remarks>
    /// Read through the framework's own validation payload type rather than by walking the JSON, because the
    /// dictionary is exactly what that type publishes and binding onto it proves the payload really is an
    /// RFC 7807 validation document rather than an object that merely happens to carry a similar member. The
    /// dictionary is returned as read-only so that a caller asserts against it instead of editing it.
    /// </remarks>
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

        // Wrapped rather than cast. The published member is a mutable dictionary, and handing that straight
        // back would let an assertion site edit the evidence it is asserting against; the wrapper is a view
        // over the same entries and preserves the comparer the payload was built with.
        return problem.Errors.AsReadOnly();
    }

    /// <summary>
    /// Asserts that a JSON object carries a member and hands the member back.
    /// </summary>
    /// <param name="owner">The object to read.</param>
    /// <param name="member">The member that must be present.</param>
    /// <returns>The member's value.</returns>
    /// <remarks>
    /// <para>
    /// Presence is asserted through the member NAMES rather than through the framework's try-pattern, which
    /// keeps every by-reference argument out of this suite: an outcome that a caller has to receive through a
    /// parameter is precisely the idiom this migration replaces, and a suite that used it to make its own
    /// assertions would be a poor advertisement for the contract it is asserting.
    /// </para>
    /// <para>
    /// Presence has to be asserted at all - rather than simply reading the member - because the failure this
    /// suite guards against is a member that is ABSENT. Reading a missing member throws, and an exception
    /// names the reader instead of naming the contract, so the assertion comes first and reports which member
    /// was missing.
    /// </para>
    /// </remarks>
    private static JsonElement RequireMember(JsonElement owner, string member)
    {
        MemberNames(owner).Should().Contain(member, "'{0}' is part of the published payload", member);

        return owner.GetProperty(member);
    }

    /// <summary>Lists the member names a JSON object carries, in the order it carries them.</summary>
    /// <param name="owner">The object to read.</param>
    /// <returns>The member names.</returns>
    /// <remarks>
    /// The one way to prove a member is ABSENT. A typed read cannot: a deserialiser ignores what it does not
    /// recognise and defaults what it does not find, so a dropped member and a member holding its type's
    /// default are indistinguishable to it.
    /// </remarks>
    private static IReadOnlyList<string> MemberNames(JsonElement owner) => owner
        .EnumerateObject()
        .Select(member => member.Name)
        .ToList();

    /// <summary>Reads the portal collection filtered by a name fragment.</summary>
    /// <param name="client">The caller, which must hold host authority.</param>
    /// <param name="fragment">The fragment to filter on, sent as data rather than as a pattern.</param>
    /// <returns>The page the server answered with.</returns>
    /// <remarks>
    /// The fragment is escaped, which is the point of routing every filtered read through here: the values
    /// this suite filters on deliberately include the characters a naive query string would either lose or
    /// let act as a wildcard, and escaping them at one site is what makes those assertions about the SERVER
    /// rather than about the client's own address building.
    /// </remarks>
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

    /// <summary>Produces a short random suffix for values that reach a unique constraint.</summary>
    /// <returns>Twelve lower-case hexadecimal characters.</returns>
    private static string Suffix() => Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture)[..12];
}
