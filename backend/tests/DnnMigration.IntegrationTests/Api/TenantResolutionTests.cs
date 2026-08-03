using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace DnnMigration.IntegrationTests.Api;

/// <summary>
/// Covers what happens to a request whose host name resolves to no portal.
/// </summary>
/// <remarks>
/// <para>
/// This suite exists because the tenant-resolution middleware used to log a resolution failure and CONTINUE,
/// on every path, on the stated grounds that portal-scoped authorisation would refuse a tenant-less request
/// anyway. That premise was removed by the fix that anchored the portal-administrator policy to the portal
/// named in the route: the policy no longer consults the resolved tenant, so an endpoint whose only possible
/// source of a tenant is the host name would have run with none at all.
/// </para>
/// <para>
/// The behaviour now has four distinct arms, and each needs its own fact because getting any one of them wrong
/// either reopens the hole or locks an operator out:
/// </para>
/// <list type="number">
/// <item>An endpoint that can only take its tenant from the host name is REFUSED.</item>
/// <item>A route carrying a <c>portalId</c> segment is SERVED, because it names its tenant itself.</item>
/// <item>An endpoint marked tenant-optional is SERVED, for the reason its mark states.</item>
/// <item>An unmatched address still answers <c>404</c>, and an anonymous caller still answers <c>401</c>.</item>
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
        using HttpClient client = _fixture.CreateHostClient();
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
    /// The same endpoint from the configured host is served, which is what makes the refusal above meaningful:
    /// a fact that only asserted a refusal could not distinguish "refused because no tenant" from "broken".
    /// </summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task TenantDependentEndpoint_FromTheConfiguredHost_IsServed()
    {
        using HttpClient client = _fixture.CreateHostClient();

        using HttpResponseMessage response = await client.GetAsync(TenantDependentRoute);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    /// <summary>
    /// A route naming its portal is served from an unconfigured host. This is the arm that keeps a host account
    /// able to administer any portal from a management host name that is deliberately not any portal's alias -
    /// refusing it would add no protection, because the route portal is proved against stored administrator
    /// membership rather than against the host name, and would lock an operator out of the installation.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task PortalScopedRoute_FromAnUnconfiguredHost_IsStillServed()
    {
        using HttpClient client = _fixture.CreateHostClient();
        client.DefaultRequestHeaders.Host = UnconfiguredHost;

        using HttpResponseMessage response = await client.GetAsync(
            new Uri($"/api/v1/portals/{ApiTestFixture.Route(_fixture.Seed.PortalId)}", UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    /// <summary>
    /// A tenant-optional endpoint is served from an unconfigured host. Installation-wide reference data reads
    /// no tenant, so there is no tenant for an unresolved host name to have got wrong.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task TenantOptionalEndpoint_FromAnUnconfiguredHost_IsStillServed()
    {
        using HttpClient client = _fixture.CreateHostClient();
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
        using HttpClient client = _fixture.CreateHostClient();
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
            "PermissionsController",
            "PortalAliasesController.DeleteAsync",
            "PortalAliasesController.GetAsync",
            "PortalAliasesController.ListAllAsync",
            "PortalAliasesController.UpdateAsync",
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
