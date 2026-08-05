using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace DnnMigration.IntegrationTests.Api;

/// <summary>
/// Covers what happens to a request whose host name resolves to no portal.
/// </summary>
/// <remarks>
/// <para>
/// This suite exists because the tenant-resolution middleware records a resolution failure and CONTINUES.
/// Every flat tenant-dependent resource must therefore fail closed later in the pipeline: the
/// portal-administrator policy refuses an ordinary administrator, while the controller guard refuses a host
/// account that legitimately bypasses that policy.
/// </para>
/// <para>
/// The behaviour now has four distinct arms, and each needs its own fact because getting any one of them wrong
/// either reopens the hole or locks an operator out:
/// </para>
/// <list type="number">
/// <item>An endpoint that can only take its tenant from the host name is REFUSED.</item>
/// <item>
/// A route carrying a <c>portalId</c> segment is ALSO REFUSED, even for a host account. SEC-006: this arm
/// used to be an exemption, on the reasoning that a route naming its own portal needs no host name to
/// identify one. It was removed. Naming a tenant in a route is a CLAIM about which tenant to act on; it is
/// not evidence of having arrived at one, and treating the two as interchangeable let a caller choose which
/// tenant a request belonged to by editing a path segment.
/// </item>
/// <item>
/// An endpoint marked tenant-optional is SERVED, for the reason its mark states. This is now the ONLY
/// exemption, and it is the one an operator can audit: listing and creating portals carry the mark, so the
/// installation-wide operations that must work before any alias exists still work.
/// </item>
/// <item>An unmatched address still answers <c>404</c>, and an anonymous caller still answers <c>401</c>.</item>
/// <item>
/// Underneath all four, the STATIC host boundary refuses a host name the installation was never configured
/// for, before routing and before any of the above runs.
/// </item>
/// </list>
/// <para>
/// The last arm is the disclosure test. A refusal that only an unconfigured host receives would let an
/// unauthenticated caller enumerate which host names the installation serves from the status code alone, and
/// the whole arrangement is only defensible because the refusal is written after authorisation has already
/// turned such a caller away.
/// </para>
/// </remarks>
[Trait("Category", "Integration")]
[Collection(IntegrationTestCollection.Name)]
public sealed class TenantResolutionTests
{
    /// <summary>A host name that matches no alias row, and cannot, because it is reserved for examples.</summary>
    private const string UnconfiguredHost = "no-such-tenant.example";

    /// <summary>Problem type carried by the refusal, built from the shared failure-code convention.</summary>
    private const string TenantUnresolvedProblemType = "urn:dnnmigration:error:portal.tenant_unresolved";

    /// <summary>
    /// The endpoint whose ONLY source of a tenant is the host name: the module-definition catalogue is scoped
    /// to the portal the request arrived for and names no portal in its route.
    /// </summary>
    private static readonly Uri TenantDependentRoute = new("/api/v1/module-definitions", UriKind.Relative);

    /// <summary>An endpoint marked tenant-optional: installation-wide reference data reading no tenant.</summary>
    private static readonly Uri TenantOptionalRoute = new("/api/v1/permissions", UriKind.Relative);

    /// <summary>The name of the marker attribute, matched by name so no internals need exposing.</summary>
    private const string MarkerAttributeName = "TenantOptionalAttribute";

    private readonly ApiTestFixture _fixture;

    /// <summary>Initialises a new instance of the <see cref="TenantResolutionTests"/> class.</summary>
    /// <param name="fixture">The shared host, database and seed.</param>
    public TenantResolutionTests(ApiTestFixture fixture) => _fixture = fixture;

    /// <summary>
    /// THE REGRESSION TEST. An endpoint that can only name its tenant from the host name is refused when the
    /// host name resolves to none - with a problem document, a stable type and no mention of the host or of
    /// which resolution failure occurred.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task TenantDependentEndpoint_FromAnUnconfiguredHost_IsRefusedWithAProblemDocument()
    {
        using HttpClient client = await _fixture.CreateHostClientAsync();
        client.DefaultRequestHeaders.Host = UnconfiguredHost;

        using HttpResponseMessage response = await client.GetAsync(TenantDependentRoute);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);

        ProblemDetails? problem = await response.Content
            .ReadFromJsonAsync<ProblemDetails>(ApiTestFixture.Json);

        problem.Should().NotBeNull("a refusal must carry a problem document rather than an empty body");
        problem!.Status.Should().Be(StatusCodes.Status403Forbidden);
        problem.Type.Should().Be(TenantUnresolvedProblemType);
        problem.Detail.Should().NotBeNullOrWhiteSpace();

        string body = await response.Content.ReadAsStringAsync();
        body.Should().NotContain(
            UnconfiguredHost,
            "the host name is attacker-supplied text and echoing it back is a reflection vector");
        body.Should().NotContain(
            "alias",
            "naming the mechanism would tell a caller how to probe the installation's alias table");
    }

    /// <summary>
    /// EVERY unscoped collection whose tenant can only come from the host name refuses an unconfigured host
    /// with the same problem document, not just the one route the fact above happens to name.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The fact above pins one route. This one pins the whole family, because the six controllers that own
    /// these routes each carry their own copy of the unresolved-tenant guard, and a copy is exactly the kind
    /// of thing that drifts. All six are asserted to answer with the SAME status, the SAME problem type and
    /// a non-empty detail, so a client cannot tell from the response which collection it addressed or which
    /// layer refused.
    /// </para>
    /// <para>
    /// MIGRATION: THESE GUARDS USED TO ANSWER WITH AN EMPTY BODY. Each controller answered its unresolved
    /// tenant with the framework's bare <c>Forbid()</c>, which does not pass through the authorisation
    /// middleware's result handler, so it wrote a <c>403</c> with no body at all while every one of those
    /// actions declares a problem document for <c>403</c>. Those guards now use the shared problem-details
    /// helper with one stable failure code. The superuser client below reaches the guards directly: the
    /// resolution middleware records the missing tenant and continues, while host authority bypasses the
    /// portal-administrator policy.
    /// </para>
    /// <para>
    /// A superuser client is used deliberately. A portal administrator is turned away earlier, by the
    /// portal-administrator policy, with <c>auth.not_permitted</c> - a different failure code for a different
    /// reason - so it would never exercise tenant resolution at all.
    /// </para>
    /// </remarks>
    /// <param name="route">The unscoped collection under test.</param>
    /// <returns>A task representing the test.</returns>
    [Theory]
    [InlineData("/api/v1/modules")]
    [InlineData("/api/v1/module-definitions")]
    [InlineData("/api/v1/roles")]
    [InlineData("/api/v1/role-groups")]
    [InlineData("/api/v1/profile-definitions")]
    [InlineData("/api/v1/users")]
    public async Task EveryUnscopedCollection_FromAnUnconfiguredHost_RefusesWithTheSameProblemDocument(
        string route)
    {
        using HttpClient client = await _fixture.CreateHostClientAsync();
        client.DefaultRequestHeaders.Host = UnconfiguredHost;

        using HttpResponseMessage response = await client.GetAsync(new Uri(route, UriKind.Relative));

        response.StatusCode.Should().Be(
            HttpStatusCode.Forbidden,
            "an unscoped collection cannot name a tenant from a host that resolves to none");

        ProblemDetails? problem = await response.Content
            .ReadFromJsonAsync<ProblemDetails>(ApiTestFixture.Json);

        problem.Should().NotBeNull(
            "the refusal must carry a problem document rather than the empty body a bare Forbid() produced");
        problem!.Status.Should().Be(StatusCodes.Status403Forbidden);
        problem.Type.Should().Be(
            TenantUnresolvedProblemType,
            "all four collections must report the one failure code, so none is distinguishable");
        problem.Detail.Should().NotBeNullOrWhiteSpace(
            "a problem document with no detail is the empty body wearing a content type");

        string body = await response.Content.ReadAsStringAsync();
        body.Should().NotContain(
            UnconfiguredHost,
            "the host name is attacker-supplied text and echoing it back is a reflection vector");
    }

    /// <summary>
    /// The same endpoint from the configured host is served, which is what makes the refusal above meaningful:
    /// a fact that only asserted a refusal could not distinguish "refused because no tenant" from "broken".
    /// </summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task TenantDependentEndpoint_FromTheConfiguredHost_IsServed()
    {
        using HttpClient client = await _fixture.CreateHostClientAsync();

        using HttpResponseMessage response = await client.GetAsync(TenantDependentRoute);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    /// <summary>
    /// SEC-006 REGRESSION. A route naming its portal is refused from an unconfigured host, and the caller used
    /// here is a HOST account - the most privileged principal the installation has - so the refusal cannot be
    /// mistaken for an authorisation outcome.
    /// </summary>
    /// <remarks>
    /// <para>
    /// WHAT THIS FACT REVERSES. This arm used to assert <c>200</c>, on the stated grounds that a route naming
    /// its own portal needs no host name to identify one and that refusing it would lock an operator out. The
    /// first half was the defect: a <c>portalId</c> segment is a claim about which tenant to act on, chosen by
    /// the caller, and accepting it in place of an arrival tenant meant the authoritative tenant could be
    /// sidestepped by addressing the installation from a host name that resolves to nothing at all. Every
    /// tenant-binding check downstream reconciles the token's portal against the ROUTE and the ARRIVAL portal;
    /// an arrival portal that does not exist cannot be reconciled with anything, so the reconciliation
    /// silently degraded to a single-sided check exactly where it mattered most.
    /// </para>
    /// <para>
    /// WHY IT LOCKS NOBODY OUT. The second half of the old rationale is answered by the fact below rather than
    /// by an exemption: the installation-wide operations a host account needs before any alias resolves -
    /// listing portals and creating one - carry the declared tenant-optional mark, and are asserted to still
    /// be served. Administering an EXISTING portal, in contrast, is reached through a host name the
    /// installation serves, which is how the legacy application worked too: every request resolved its portal
    /// from the alias table before anything else ran, and there was no management host name that belonged to
    /// no portal.
    /// </para>
    /// </remarks>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task PortalScopedRoute_FromAnUnconfiguredHost_IsRefusedEvenForAHostAccount()
    {
        using HttpClient client = await _fixture.CreateHostClientAsync();
        client.DefaultRequestHeaders.Host = UnconfiguredHost;

        using HttpResponseMessage response = await client.GetAsync(
            new Uri($"/api/v1/portals/{ApiTestFixture.Route(_fixture.Seed.PortalId)}", UriKind.Relative));

        response.StatusCode.Should().Be(
            HttpStatusCode.Forbidden,
            "a route segment naming a tenant is a claim about one, not arrival at one");

        ProblemDetails? problem = await response.Content
            .ReadFromJsonAsync<ProblemDetails>(ApiTestFixture.Json);

        problem.Should().NotBeNull();
        problem!.Type.Should().Be(
            TenantUnresolvedProblemType,
            "the refusal is the tenant one, so an operator reading the code learns the actual cause rather "
            + "than being told the host account lacks permission");
    }

    /// <summary>
    /// THE ESCAPE HATCH, DECLARED RATHER THAN INFERRED. The installation-wide portal operations are served from
    /// an unconfigured host, because they carry the tenant-optional mark. This is what keeps the arm above from
    /// being a lock-out: a host account can always enumerate the installation and create the first portal, on
    /// an installation that has no alias to arrive at yet.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task InstallationWidePortalListing_FromAnUnconfiguredHost_IsStillServed()
    {
        using HttpClient client = await _fixture.CreateHostClientAsync();
        client.DefaultRequestHeaders.Host = UnconfiguredHost;

        using HttpResponseMessage response = await client.GetAsync(
            new Uri("/api/v1/portals", UriKind.Relative));

        response.StatusCode.Should().Be(
            HttpStatusCode.OK,
            "listing portals is marked tenant-optional, which is the one auditable exemption");
    }

    /// <summary>
    /// A tenant-optional endpoint is served from an unconfigured host. Installation-wide reference data reads
    /// no tenant, so there is no tenant for an unresolved host name to have got wrong.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task TenantOptionalEndpoint_FromAnUnconfiguredHost_IsStillServed()
    {
        using HttpClient client = await _fixture.CreateHostClientAsync();
        client.DefaultRequestHeaders.Host = UnconfiguredHost;

        using HttpResponseMessage response = await client.GetAsync(TenantOptionalRoute);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    /// <summary>
    /// An address that matches no route still answers <c>404 Not Found</c> from an unconfigured host. Replacing
    /// a truthful "no such address" with a refusal would both mislead the caller and disclose that the host is
    /// unconfigured to anyone probing for addresses.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task UnmatchedAddress_FromAnUnconfiguredHost_StillReturnsNotFound()
    {
        using HttpClient client = await _fixture.CreateHostClientAsync();
        client.DefaultRequestHeaders.Host = UnconfiguredHost;

        using HttpResponseMessage response = await client.GetAsync(
            new Uri("/api/v1/no-such-resource", UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    /// <summary>
    /// THE DISCLOSURE TEST. An anonymous caller receives <c>401 Unauthorized</c> from the unconfigured host and
    /// from the configured one, identically, so the status code reveals nothing about which host names this
    /// installation serves. The refusal is only written after authorisation has run, which is what makes that
    /// true rather than merely intended.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task AnonymousCaller_CannotDistinguishAConfiguredHostFromAnUnconfiguredOne()
    {
        using HttpClient unconfigured = _fixture.CreateAnonymousClient();
        unconfigured.DefaultRequestHeaders.Host = UnconfiguredHost;

        using HttpResponseMessage fromUnconfigured = await unconfigured.GetAsync(TenantDependentRoute);

        using HttpClient configured = _fixture.CreateAnonymousClient();

        using HttpResponseMessage fromConfigured = await configured.GetAsync(TenantDependentRoute);

        fromUnconfigured.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        fromConfigured.StatusCode.Should().Be(
            fromUnconfigured.StatusCode,
            "an unauthenticated caller must not be able to enumerate configured host names from the status");
    }

    /// <summary>
    /// Every tenant-optional mark in the API carries a stated reason, and the marked set is exactly the
    /// inventory reviewed here.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The mark REMOVES a security check, so an unreviewed addition is the way this protection would quietly
    /// erode. Pinning the inventory makes adding a mark a deliberate act that fails this test until the new
    /// entry is justified in the same place a reviewer is looking.
    /// </para>
    /// <para>
    /// Matched by attribute NAME rather than by type, because the attribute is internal to the API assembly and
    /// exposing internals to a test project purely to name it here would weaken the production assembly's
    /// surface for the convenience of one assertion.
    /// </para>
    /// </remarks>
    [Fact]
    public void TenantOptionalMarks_AreExactlyTheReviewedInventoryAndEachStatesAReason()
    {
        var expected = new[]
        {
            "AuthController",
            // SEC-033: only the two installation-wide catalogue reads are tenant optional. The module- and
            // page-scoped reads must resolve a tenant so their resource identifiers can be ownership-bound.
            "PermissionsController.GetAsync",
            "PermissionsController.ListAsync",
            // The alias repair path, renamed when these operations moved onto the portal-scoped route shape.
            // The mark is what keeps a broken alias table correctable: the policy still binds a portal
            // administrator to the tenant it arrived through, so what the exemption admits is the host account
            // the policy exempts by design.
            "PortalAliasesController.DeleteForPortalAsync",
            "PortalAliasesController.GetForPortalAsync",
            "PortalAliasesController.ListForPortalAsync",
            "PortalAliasesController.UpdateForPortalAsync",
            "PortalsController.CreateAsync",
            "PortalsController.ListAsync",
            "TabsController.GetAsync",
            "TabsController.UpdateAsync",
        };

        var found = new List<string>();

        foreach (Type controller in typeof(Program).Assembly.GetTypes()
            .Where(type => typeof(ControllerBase).IsAssignableFrom(type) && !type.IsAbstract))
        {
            foreach (Attribute mark in Marks(controller.GetCustomAttributes(inherit: false)))
            {
                Justification(mark).Should().NotBeNullOrWhiteSpace(
                    $"the mark on {controller.Name} removes a check and must say why");
                found.Add(controller.Name);
            }

            foreach (MethodInfo action in controller.GetMethods(
                BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                foreach (Attribute mark in Marks(action.GetCustomAttributes(inherit: false)))
                {
                    Justification(mark).Should().NotBeNullOrWhiteSpace(
                        $"the mark on {controller.Name}.{action.Name} removes a check and must say why");
                    found.Add($"{controller.Name}.{action.Name}");
                }
            }
        }

        found.Order(StringComparer.Ordinal).Should().Equal(
            expected.Order(StringComparer.Ordinal),
            "a tenant-optional mark added or removed without updating this inventory has not been reviewed");
    }

    /// <summary>
    /// SEC-006 REGRESSION, THE STATIC BOUNDARY. A host name the installation was never configured for is
    /// refused with <c>400 Bad Request</c> before routing, when the shipped restricted list is in force.
    /// </summary>
    /// <remarks>
    /// <para>
    /// WHY A SECOND BOUNDARY EXISTS AT ALL. Tenant resolution is a DYNAMIC boundary: it matches the arriving
    /// host against alias rows that an operator adds and removes at run time, so it cannot be expressed in a
    /// file. Host filtering is the STATIC one, and it answers a question the dynamic boundary cannot: which
    /// host names is this process willing to be addressed by in the first place. The shipped configuration
    /// used to answer "any", which meant an arbitrary <c>Host</c> header reached every stage of the pipeline,
    /// including the tenant-optional endpoints that deliberately serve requests with no tenant - the
    /// credential endpoints among them.
    /// </para>
    /// <para>
    /// WHY THIS FACT BUILDS ITS OWN HOST. The shared fixture widens the list to every host, because it invents
    /// alias host names at run time and cannot enumerate them in advance. That widening would make this fact
    /// vacuous, so the restricted list is applied to a host built here. The value used is the loopback name
    /// only, which is a subset of what <c>appsettings.json</c> ships, so a fact that passes here passes under
    /// the shipped file as well.
    /// </para>
    /// <para>
    /// THE STATUS CODE IS THE FRAMEWORK'S, NOT THIS APPLICATION'S, and that is deliberate: the refusal happens
    /// before any application middleware, so it carries no problem document and reveals nothing about the
    /// installation beyond the fact that it declined the address.
    /// </para>
    /// </remarks>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task AnUnconfiguredHostName_IsRefusedByTheStaticHostBoundary()
    {
        using RestrictedHost host = new();
        using HttpClient client = host.CreateClient();
        client.DefaultRequestHeaders.Host = UnconfiguredHost;

        using HttpResponseMessage response = await client.GetAsync(
            new Uri("/health", UriKind.Relative));

        response.StatusCode.Should().Be(
            HttpStatusCode.BadRequest,
            "a host name the installation was never configured for is refused before it reaches anything");
    }

    /// <summary>
    /// THE NEGATIVE CONTROL FOR THE FACT ABOVE, AND THE ONE THAT PROTECTS THE CONTAINER. The loopback name is
    /// served under the same restricted list, which is what the shipped health probes depend on: the image
    /// probes <c>http://localhost:8080/health</c> and the compose file probes <c>http://127.0.0.1:8080/health</c>,
    /// so a restricted list that excluded either name would report the container permanently unhealthy and,
    /// through the frontend's dependency on that condition, never start the application at all.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task TheLoopbackHostName_IsServedUnderTheRestrictedList()
    {
        using RestrictedHost host = new();
        using HttpClient client = host.CreateClient();

        using HttpResponseMessage response = await client.GetAsync(
            new Uri("/health", UriKind.Relative));

        response.StatusCode.Should().Be(
            HttpStatusCode.OK,
            "the loopback name is what every shipped health probe addresses");
    }

    /// <summary>A host carrying the shipped restricted host list rather than the suite's widened one.</summary>
    /// <remarks>
    /// The value is applied through an in-memory configuration source added LAST, so it wins over the
    /// environment variable the shared fixture sets for the whole process. Applying it any other way would
    /// leave the fact silently asserting against the widened list.
    /// </remarks>
    private sealed class RestrictedHost : WebApplicationFactory<Program>
    {
        /// <summary>The list under test: the loopback name only, a subset of what appsettings.json ships.</summary>
        private const string RestrictedAllowedHosts = "localhost";

        /// <inheritdoc />
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            ArgumentNullException.ThrowIfNull(builder);

            builder.UseEnvironment("Testing");
            builder.ConfigureAppConfiguration(configuration => configuration.AddInMemoryCollection(
                new Dictionary<string, string?> { ["AllowedHosts"] = RestrictedAllowedHosts }));
        }
    }

    /// <summary>Selects the tenant-optional marks out of a set of attributes, by name.</summary>
    /// <param name="attributes">The attributes declared on a controller or an action.</param>
    /// <returns>The tenant-optional marks among them.</returns>
    private static IEnumerable<Attribute> Marks(object[] attributes) => attributes
        .OfType<Attribute>()
        .Where(attribute => string.Equals(
            attribute.GetType().Name,
            MarkerAttributeName,
            StringComparison.Ordinal));

    /// <summary>Reads a mark's stated reason through its public property.</summary>
    /// <param name="mark">The mark to read.</param>
    /// <returns>The stated reason, or <see langword="null"/> when the property is absent.</returns>
    private static string? Justification(Attribute mark) => mark
        .GetType()
        .GetProperty("Justification")
        ?.GetValue(mark) as string;
}
