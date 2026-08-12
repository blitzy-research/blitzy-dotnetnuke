using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Serilog.Core;
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

    /// <summary>The logger category the pre-routing stage writes its tenant diagnosis under.</summary>
    /// <remarks>
    /// Spelled as a literal rather than taken from the type, which is internal to the API assembly. The same
    /// string identifies the stage in an operator's log query, so a rename that moved the entry to another
    /// category must fail here rather than pass against a log nobody is reading.
    /// </remarks>
    private const string TenantDiagnosisSourceContext =
        "DnnMigration.Api.Middleware.TenantPathBaseMiddleware";

    /// <summary>
    /// A caller-supplied path segment shaped like the two things a log must never keep.
    /// </summary>
    /// <remarks>
    /// The same value the request-logging redaction suite uses, so both edges are proved against one shape:
    /// an address that identifies a person and a fragment that looks like a credential.
    /// </remarks>
    private const string SensitivePathSegment = "victim.user%40example.com-Password1";

    /// <summary>The same value as it appears once the server has decoded the path.</summary>
    private const string DecodedSensitivePathSegment = "victim.user@example.com-Password1";

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
    /// SEC-B4 ORDERING FACT. For an AUTHENTICATED caller the tenant refusal answers BEFORE any authorisation
    /// policy, which is what proves the stage sits between authentication and authorisation.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// <para>
    /// THE CALLER IS DELIBERATELY UNPRIVILEGED, and that is the whole discriminator. An ordinary member on a
    /// portal-scoped route is refused by the portal-administrator policy with <c>auth.not_permitted</c>; with
    /// the stage registered after <c>UseAuthorization</c> - where it used to be - that policy answered first
    /// and the tenant refusal was never reached. Reading <c>portal.tenant_unresolved</c> here therefore
    /// establishes the ORDER and not merely the refusal, which a host account could not: a host account
    /// bypasses the policy, so it would read the same body from either arrangement.
    /// </para>
    /// <para>
    /// WHY THE ORDER MATTERS RATHER THAN BEING A PREFERENCE. Every tenant-scoped policy reconciles three
    /// identities - the caller's portal, the route's portal and the ARRIVAL portal. Evaluated on a request
    /// whose host name resolved to nothing, the third is absent and a three-sided check silently degrades to
    /// a two-sided one exactly where a caller has chosen an address the installation does not serve.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task TenantRefusal_AnswersBeforeAuthorisation_ForAnAuthenticatedCaller()
    {
        using HttpClient client = await _fixture.CreateUnprivilegedClientAsync();
        client.DefaultRequestHeaders.Host = UnconfiguredHost;

        using HttpResponseMessage response = await client.GetAsync(
            new Uri($"/api/v1/portals/{ApiTestFixture.Route(_fixture.Seed.PortalId)}", UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);

        ProblemDetails? problem = await response.Content
            .ReadFromJsonAsync<ProblemDetails>(ApiTestFixture.Json);

        problem.Should().NotBeNull();
        problem!.Type.Should().Be(
            TenantUnresolvedProblemType,
            "the tenant refusal must be reached before the policy that would otherwise answer first");
    }

    /// <summary>
    /// SEC-B4 REGRESSION. The diagnostic entry for an unresolved tenant records a fingerprint and a closed
    /// reason code, and never the caller's path OR the host name it addressed.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// <para>
    /// WHAT THIS REVERSES. The entry used to log the whole address - the host name followed by the request's
    /// FULL path - at warning level, on a stage that runs before routing and therefore for every request. An
    /// unknown host name is chosen by the caller, so a caller could pick any host it liked and force
    /// arbitrary path text into the production log with it: a mistyped credential, a token pasted into a
    /// URL, an e-mail address. The request envelope records the matched route template rather than the path
    /// for precisely this reason, and this entry bypassed that protection.
    /// </para>
    /// <para>
    /// AND WHAT THIS NOW ALSO REVERSES: the entry kept a bounded, sanitised HOST CANDIDATE after that first
    /// correction, on the argument that an operator needs to know which alias to add. Bounding a value stops
    /// it forging a log line; it does not stop it disclosing one. The value is still text an unauthenticated
    /// caller chooses, on a stage that runs for every request, and a real deployment's host names are
    /// themselves customer identifiers - so it is gone, and the fingerprint plus the reason code are what
    /// remain. This fact asserts the absence directly, in the message and in every property, because an
    /// assertion that merely required the fingerprint would keep passing if the host name came back
    /// alongside it.
    /// </para>
    /// <para>
    /// The path below carries both shapes a log must never keep, so a single assertion covers a credential
    /// fragment and a personal identifier. The host is built immediately before the request because a
    /// Serilog logger is process-wide: a fact asserting on records has to own the host that writes them.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task TenantFailureDiagnosis_RecordsAFingerprintAndNeitherTheHostNorTheCallerPath()
    {
        await using var host = new RecordingTenantHost();
        using HttpClient client = host.CreateClient();
        client.DefaultRequestHeaders.Host = UnconfiguredHost;

        int recordsBefore = RecordedLogs.Snapshot().Count;

        using HttpResponseMessage response = await client.GetAsync(
            new Uri($"/api/v1/{SensitivePathSegment}", UriKind.Relative));

        // The caller is anonymous, so the fallback policy answers 401 - and it does so whether or not the
        // address matches a route, because a request with no endpoint carries no authorize data and the
        // fallback applies to it too. What the answer is does not matter to this fact: the diagnosis under
        // test is written by the PRE-ROUTING stage, before anything could refuse the request, which is
        // exactly why a caller could use it to write to the log at will.
        response.StatusCode.Should().Be(
            HttpStatusCode.Unauthorized,
            "an anonymous caller learns nothing from either host, which is asserted in its own right above");

        LogRecord[] diagnoses = [.. RecordedLogs.Snapshot()
            .Skip(recordsBefore)
            .Where(record =>
                record.Properties.TryGetValue("SourceContext", out object? source)
                && string.Equals(source as string, TenantDiagnosisSourceContext, StringComparison.Ordinal))];

        diagnoses.Should().NotBeEmpty(
            "an unresolvable host must still be diagnosed for the operator who has to correct it");

        foreach (LogRecord diagnosis in diagnoses)
        {
            diagnosis.Message.Should().NotContain(
                DecodedSensitivePathSegment,
                "the caller's own path must not reach the log through the tenant diagnosis");
            diagnosis.Message.Should().NotContain(SensitivePathSegment);

            diagnosis.Message.Should().NotContain(
                UnconfiguredHost,
                "the host name the caller addressed is tenant-identifying text and must not be retained");
            diagnosis.Properties.Should().NotContainKey(
                "AliasCandidate",
                "the bounded host candidate was removed outright rather than shortened further");

            diagnosis.Properties.Should().ContainKey(
                "AddressFingerprint",
                "repeated failures against one full address must still be recognisable as one problem");
            (diagnosis.Properties["AddressFingerprint"] as string).Should().MatchRegex(
                "^[0-9a-f]{16}$",
                "the fingerprint is a bounded lower-case digest prefix and nothing else");

            diagnosis.Properties.Should().ContainKey(
                "ReasonCode",
                "the reason code is what distinguishes an unknown host from an ambiguous or misconfigured one");

            foreach (object? value in diagnosis.Properties.Values)
            {
                (value as string)?.Should().NotContain(
                    DecodedSensitivePathSegment,
                    "no property may carry the caller's path either");
                (value as string)?.Should().NotContain(
                    UnconfiguredHost,
                    "and no property may carry the host name either, however bounded");
            }
        }
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
    /// refused with <c>400 Bad Request</c> before routing, when a deployment-scoped restricted list is in force.
    /// </summary>
    /// <remarks>
    /// <para>
    /// WHAT THIS FACT ASSERTS, AND WHAT IT DOES NOT. It asserts that host filtering WORKS when a deployment
    /// configures it - that a host outside an explicit list is refused before any application middleware
    /// runs. It does not assert that the shipped configuration carries such a list, because it deliberately
    /// does not: <c>appsettings.json</c> ships <c>AllowedHosts</c> as <c>*</c> so that exact
    /// <c>PortalAlias</c> resolution is the single authority for which hosts identify a tenant. Two
    /// independent allow-lists for one question is the defect, not the control - the static one runs first,
    /// so an alias an operator adds through the API would be refused with 400 before resolution could see
    /// it. Nothing is granted by the widened default: an unmatched host still reaches no tenant, and every
    /// tenant-scoped endpoint refuses a request with no resolved tenant.
    /// </para>
    /// <para>
    /// WHY THIS FACT BUILDS ITS OWN HOST. A deployment-scoped static list is legitimate where the set of
    /// names is known in advance, and <c>docker/docker-compose.tls.yml</c> configures exactly one. This fact
    /// reproduces that arrangement: it applies the loopback name only to a host built here, which is the
    /// narrowest list any shipped topology uses, so passing here means the mechanism holds for every
    /// deployment that opts into it.
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
    /// served under the same restricted list, which is what every shipped health probe depends on: the image
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

    /// <summary>A host carrying a deployment-scoped restricted host list rather than the shipped wildcard.</summary>
    /// <remarks>
    /// The value is applied through an in-memory configuration source added LAST, so it wins over both the
    /// shipped <c>*</c> and the environment variable the shared fixture sets for the whole process. Applying
    /// it any other way would leave the fact silently asserting against the widened list, which permits
    /// every host and would make it vacuous.
    /// </remarks>
    private sealed class RestrictedHost : WebApplicationFactory<Program>
    {
        /// <summary>The list under test: the loopback name only, the narrowest list any shipped topology uses.</summary>
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

    /// <summary>A host of this suite's own whose log records reach the shared recording sink.</summary>
    /// <remarks>
    /// NEEDED BECAUSE A SERILOG LOGGER IS PROCESS-WIDE. <c>Program</c> hands ownership of the static logger to
    /// the host it builds, so every host built in this process replaces the one the shared fixture installed -
    /// configured from its own settings and without the fixture's sink registration. A fact that reads records
    /// therefore has to own the host that writes them and register the sink on it, or it would pass when it
    /// ran first and fail when it did not.
    /// </remarks>
    private sealed class RecordingTenantHost : WebApplicationFactory<Program>
    {
        /// <inheritdoc />
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            ArgumentNullException.ThrowIfNull(builder);

            builder.UseEnvironment("Testing");
            builder.ConfigureServices(services =>
                services.AddSingleton<ILogEventSink>(RecordedLogs.Sink));
        }
    }

    /// <summary>
    /// A child portal addressed beneath a PATH SEGMENT of the shared host is resolved from that segment, is
    /// routed as though the segment were not there, and answers with the child's own rows.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// <para>
    /// SEC: THE END-TO-END HALF OF THE CHILD-PORTAL CONTRACT. The legacy product let a child portal be
    /// reached at <c>domain/segment</c> - the signup screen composes and stores exactly that
    /// (<c>Website/admin/Portal/Signup.ascx.vb</c> L232-L236) - and three components must agree for the same
    /// address to work here: the browser has to PUT the segment on the request, the reverse proxy has to
    /// FORWARD it rather than falling through to the application document, and this API has to resolve the
    /// tenant from it and then rebase the path so the routes still match. The first two are asserted where
    /// they live (<c>frontend/src/app/core/config/tenant-path.spec.ts</c> and the proxy's own configuration);
    /// this fact asserts the third against a real request, which no unit test of the middleware could do,
    /// because what is under test is resolution AND routing together.
    /// </para>
    /// <para>
    /// THE ASSERTION IS WHOSE ROWS COME BACK, not merely that a 200 arrives. A status alone would pass if the
    /// segment were silently ignored and the PARENT resolved, which is exactly the defect the contract exists
    /// to prevent. The child is created through the API and therefore carries its own administrator and
    /// registered roles, so its role listing is disjoint from the parent's stock role names; the same route
    /// WITHOUT the prefix is read in the same case and answers the parent's rows, so no single tenant can
    /// explain both readings.
    /// </para>
    /// <para>
    /// ⚠ THE CHILD IS CREATED THROUGH THE API RATHER THAN BY INSERT, and that is a requirement rather than a
    /// convenience. Resolution refuses a portal that designates no administrator account, no administrator
    /// role or no registered-user role, and refuses one whose designated roles do not exist - see
    /// <c>PortalContextHolder</c>. A hand-inserted <c>Portals</c> row satisfies none of that, so it would
    /// resolve to nothing and this fact would pass or fail for a reason unrelated to path rebasing. Only the
    /// ALIAS is inserted, because an alias carrying a path segment is not something the creation endpoint
    /// composes.
    /// </para>
    /// <para>
    /// The caller is a HOST account, deliberately: it is the one principal entitled to both tenants, so a
    /// difference between the two answers cannot be explained by authorisation, and its token names the
    /// SEEDED portal rather than the child - which is what shows the arrival tenant is taken from the address
    /// rather than from the credential.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task ChildPortalAddressedBeneathAPathSegment_ResolvesFromThePathAndRoutesAsRebased()
    {
        using HttpClient client = await _fixture.CreateHostClientAsync();

        string suffix = Guid.NewGuid().ToString("N")[..8];
        string childSegment = "child-" + suffix;
        string childAlias = ApiTestFixture.TestHost + "/" + childSegment;
        string childPortalName = "Child Tenant " + suffix;

        using HttpResponseMessage created = await client.PostAsJsonAsync(
            new Uri("/api/v1/portals", UriKind.Relative),
            new
            {
                portalName = childPortalName,
                portalAlias = "child-tenant-" + suffix + ".local",
                homeDirectory = string.Empty,
                templateFile = "admin.template",
                isChildPortal = false,
                administratorFirstName = "Child",
                administratorLastName = "Administrator",
                administratorUsername = "child_admin_" + suffix,
                administratorPassword = ApiTestFixture.KnownPassword,
                administratorEmail = "child." + suffix + "@example.com",
            },
            ApiTestFixture.Json);

        created.StatusCode.Should().Be(
            HttpStatusCode.Created,
            "the child tenant has to be a fully designated portal before it can resolve at all");

        using JsonDocument document = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
        int childPortalId = document.RootElement.GetProperty("data").GetProperty("portalId").GetInt32();

        // The one thing the creation endpoint does not compose: an alias carrying a PATH segment beneath the
        // shared host, which is the shape the legacy signup screen stored.
        await _fixture.Database.ExecuteAsync(
            """
            INSERT INTO [dbo].[PortalAlias] ([PortalID], [HTTPAlias]) VALUES (@portalId, @alias);
            """,
            new Dictionary<string, object?>
            {
                ["@portalId"] = childPortalId,
                ["@alias"] = childAlias,
            });

        string childOnlyRoleName = "Child Only " + suffix;

        await _fixture.Database.ExecuteAsync(
            """
            INSERT INTO [dbo].[Roles]
                ([PortalID], [RoleName], [Description], [ServiceFee], [BillingPeriod], [BillingFrequency],
                 [TrialFee], [TrialPeriod], [TrialFrequency], [IsPublic], [AutoAssignment])
            VALUES (@portalId, @roleName, N'Seeded in the child tenant only', 0, 0, 'N', 0, 0, 'N', 0, 0);
            """,
            new Dictionary<string, object?>
            {
                ["@portalId"] = childPortalId,
                ["@roleName"] = childOnlyRoleName,
            });

        // The SAME route in both requests; only the tenant segment differs. The routes are written against
        // /api/v1/..., so a 200 for the prefixed address is itself evidence that the segment was moved into
        // the path base rather than matched as part of the path.
        using HttpResponseMessage viaChild = await client.GetAsync(
            new Uri($"/{childSegment}/api/v1/roles", UriKind.Relative));

        viaChild.StatusCode.Should().Be(
            HttpStatusCode.OK,
            "a request beneath the child's own segment must route as though the segment were absent");

        string childBody = await viaChild.Content.ReadAsStringAsync();

        childBody.Should().Contain(
            childOnlyRoleName,
            "the arrival tenant is taken from the path, so the child's own rows answer");

        using HttpResponseMessage viaHost = await client.GetAsync(
            new Uri("/api/v1/roles", UriKind.Relative));

        viaHost.StatusCode.Should().Be(HttpStatusCode.OK);

        string hostBody = await viaHost.Content.ReadAsStringAsync();

        hostBody.Should().NotContain(
            childOnlyRoleName,
            "the bare host still resolves the parent, which is what makes the reading above a difference");

        // ⚠ THE POSITIVE HALF IS ASSERTED BY IDENTIFIER RATHER THAN BY READING THE LISTING, and that is a
        // correction rather than a preference. It began as "the parent's listing contains the parent's
        // stock role", which passed when this class ran alone and FAILED in a full run: the listing is
        // paged ten at a time and ordered by name, the suite as a whole leaves the parent holding roles
        // enough to fill five pages, and the stock role fell off page one. Asserting on a page of a table
        // every other class writes to is an assertion about the order of the run.
        //
        // Reading ONE identifier at both addresses is stronger as well as stable. The seeded
        // administrators role belongs to the parent, and the by-identifier read is portal-scoped and
        // reports a role from another portal as absent, so the pair below can only be explained by the two
        // requests having resolved different tenants. It also exercises the identity seed deliberately:
        // Roles.RoleID is IDENTITY(0, 1), so this identifier is legitimately 0 and neither answer may
        // treat it as missing.
        int parentRoleId = _fixture.Seed.AdministratorRoleId;

        using HttpResponseMessage parentRoleViaHost = await client.GetAsync(
            new Uri($"/api/v1/roles/{parentRoleId}", UriKind.Relative));

        parentRoleViaHost.StatusCode.Should().Be(
            HttpStatusCode.OK,
            "the bare host resolves the parent, which is the tenant that owns this role");
        (await parentRoleViaHost.Content.ReadAsStringAsync()).Should().Contain(
            IntegrationSeed.AdministratorsRoleName,
            "and it answers with the parent's own row rather than an empty envelope");

        using HttpResponseMessage parentRoleViaChild = await client.GetAsync(
            new Uri($"/{childSegment}/api/v1/roles/{parentRoleId}", UriKind.Relative));

        parentRoleViaChild.StatusCode.Should().Be(
            HttpStatusCode.NotFound,
            "the same identifier beneath the child's segment names nothing, because the arrival tenant "
                + "came from the path and the role belongs to the parent");
    }

    /// <summary>
    /// A path that merely SHARES A PREFIX with a stored child segment is not rebased onto that tenant.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// <para>
    /// The legacy alias resolution matched with <c>like '%alias%'</c>, so an alias that was a substring of
    /// another resolved the wrong tenant - a defect recorded in <c>MIGRATION_NOTES.md</c> and closed here by
    /// exact, whole-segment matching. This fact pins that boundary from the outside.
    /// </para>
    /// <para>
    /// THE OUTCOME IS 404 RATHER THAN A TENANT REFUSAL, and the difference is worth stating because it is the
    /// pipeline's shape rather than an accident. Nothing resolves for such an address, so the path is NOT
    /// rebased; the unrebased path matches no route, and routing answers before any tenant-dependent endpoint
    /// is reached. A caller therefore learns that the address does not exist rather than which tenants do,
    /// which is the stronger of the two answers.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task APathThatMerelySharesAPrefixWithAChildSegment_IsNotRebasedOntoIt()
    {
        using HttpClient client = await _fixture.CreateHostClientAsync();

        using HttpResponseMessage response = await client.GetAsync(
            new Uri("/childish/api/v1/roles", UriKind.Relative));

        response.StatusCode.Should().Be(
            HttpStatusCode.NotFound,
            "a substring of a stored alias is not that alias, so nothing is rebased and no route matches");

        ProblemDetails? problem = await response.Content
            .ReadFromJsonAsync<ProblemDetails>(ApiTestFixture.Json);

        problem.Should().NotBeNull("even an unmatched address answers in the contract the client parses");
        problem!.Status.Should().Be(StatusCodes.Status404NotFound);
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
