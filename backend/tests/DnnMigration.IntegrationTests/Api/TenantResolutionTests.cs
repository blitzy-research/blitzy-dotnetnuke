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

/// <summary>Covers what happens to a request whose host name resolves to no portal.</summary>
/// <remarks>
/// <para>
/// This suite exists because the tenant-resolution middleware records a resolution failure and CONTINUES.
/// Every flat tenant-dependent resource must therefore fail closed later in the pipeline: the
/// portal-administrator policy refuses an ordinary administrator, while the controller guard refuses a host
/// account that legitimately bypasses that policy.
/// </para>
/// <para>
/// The last arm is the disclosure test. A refusal that only an unconfigured host receives would let an
/// unauthenticated caller enumerate which host names the installation serves from the status code alone,
/// and the whole arrangement is only defensible because the refusal is written after authorisation has
/// already turned such a caller away.
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
    /// The endpoint whose ONLY source of a tenant is the host name: the module-definition catalogue is
    /// scoped to the portal the request arrived for and names no portal in its route.
    /// </summary>
    private static readonly Uri TenantDependentRoute = new("/api/v1/module-definitions", UriKind.Relative);

    /// <summary>An endpoint marked tenant-optional: installation-wide reference data reading no tenant.</summary>
    private static readonly Uri TenantOptionalRoute = new("/api/v1/permissions", UriKind.Relative);

    /// <summary>The name of the marker attribute, matched by name so no internals need exposing.</summary>
    private const string MarkerAttributeName = "TenantOptionalAttribute";

    /// <summary>The logger category the pre-routing stage writes its tenant diagnosis under.</summary>
    private const string TenantDiagnosisSourceContext =
        "DnnMigration.Api.Middleware.TenantPathBaseMiddleware";

    /// <summary>A caller-supplied path segment shaped like the two things a log must never keep.</summary>
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
    /// THE REGRESSION TEST. An endpoint that can only name its tenant from the host name is refused when
    /// the host name resolves to none - with a problem document, a stable type and no mention of the host
    /// or of which resolution failure occurred.
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
    /// The fact above pins one route. This one pins the whole family, because the six controllers that own
    /// these routes each carry their own copy of the unresolved-tenant guard, and a copy is exactly the
    /// kind of thing that drifts.
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
    /// The same endpoint from the configured host is served, which is what makes the refusal above
    /// meaningful: a fact that only asserted a refusal could not distinguish "refused because no tenant"
    /// from "broken".
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
    /// REGRESSION. A route naming its portal is refused from an unconfigured host, and the caller used here
    /// is a HOST account - the most privileged principal the installation has - so the refusal cannot be
    /// mistaken for an authorisation outcome.
    /// </summary>
    /// <remarks>
    /// WHY IT LOCKS NOBODY OUT. The second half of the old rationale is answered by the fact below rather
    /// than by an exemption: the installation-wide operations a host account needs before any alias
    /// resolves - listing portals and creating one - carry the declared tenant-optional mark, and are
    /// asserted to still be served.
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
    /// THE ESCAPE HATCH, DECLARED RATHER THAN INFERRED. The installation-wide portal operations are served
    /// from an unconfigured host, because they carry the tenant-optional mark. This is what keeps the arm
    /// above from being a lock-out: a host account can always enumerate the installation and create the
    /// first portal, on an installation that has no alias to arrive at yet.
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
    /// A tenant-optional endpoint is served from an unconfigured host. Installation-wide reference data
    /// reads no tenant, so there is no tenant for an unresolved host name to have got wrong.
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
    /// An address that matches no route still answers <c>404 Not Found</c> from an unconfigured host.
    /// Replacing a truthful "no such address" with a refusal would both mislead the caller and disclose
    /// that the host is unconfigured to anyone probing for addresses.
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
    /// THE DISCLOSURE TEST. An anonymous caller receives <c>401 Unauthorized</c> from the unconfigured host
    /// and from the configured one, identically, so the status code reveals nothing about which host names
    /// this installation serves. The refusal is only written after authorisation has run, which is what
    /// makes that true rather than merely intended.
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
    /// SEC-B4 ORDERING FACT. For an AUTHENTICATED caller the tenant refusal answers BEFORE any
    /// authorisation policy, which is what proves the stage sits between authentication and authorisation.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// WHY THE ORDER MATTERS RATHER THAN BEING A PREFERENCE. Every tenant-scoped policy reconciles three
    /// identities - the caller's portal, the route's portal and the ARRIVAL portal. Evaluated on a request
    /// whose host name resolved to nothing, the third is absent and a three-sided check silently degrades
    /// to a two-sided one exactly where a caller has chosen an address the installation does not serve.
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
    /// AND WHAT THIS NOW ALSO REVERSES: the entry kept a bounded, sanitised HOST CANDIDATE after that first
    /// correction, on the argument that an operator needs to know which alias to add. Bounding a value
    /// stops it forging a log line; it does not stop it disclosing one.
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
        // fallback applies to it too.
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
    /// The mark REMOVES a security check, so an unreviewed addition is the way this protection would
    /// quietly erode. Pinning the inventory makes adding a mark a deliberate act that fails this test until
    /// the new entry is justified in the same place a reviewer is looking.
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
            // The alias repair path, renamed when these operations moved onto the portal-scoped route
            // shape.
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
    /// REGRESSION, THE STATIC BOUNDARY. A host name the installation was never configured for is refused
    /// with <c>400 Bad Request</c> before routing, when a deployment-scoped restricted list is in force.
    /// </summary>
    /// <remarks>
    /// WHAT THIS FACT ASSERTS, AND WHAT IT DOES NOT. It asserts that host filtering WORKS when a deployment
    /// configures it - that a host outside an explicit list is refused before any application middleware
    /// runs.
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
    /// THE NEGATIVE CONTROL FOR THE FACT ABOVE, AND THE ONE THAT PROTECTS THE CONTAINER. The loopback name
    /// is served under the same restricted list, which is what every shipped health probe depends on: the
    /// image probes <c>http://localhost:8080/health</c> and the compose file probes
    /// <c>http://127.0.0.1:8080/health</c>, so a restricted list that excluded either name would report the
    /// container permanently unhealthy and, through the frontend's dependency on that condition, never
    /// start the application at all.
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
    /// shipped <c>*</c> and the environment variable the shared fixture sets for the whole process.
    /// Applying it any other way would leave the fact silently asserting against the widened list, which
    /// permits every host and would make it vacuous.
    /// </remarks>
    private sealed class RestrictedHost : WebApplicationFactory<Program>
    {
        /// <summary>
        /// The list under test: the loopback name only, the narrowest list any shipped topology uses.
        /// </summary>
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
    /// NEEDED BECAUSE A SERILOG LOGGER IS PROCESS-WIDE. <c>Program</c> hands ownership of the static logger
    /// to the host it builds, so every host built in this process replaces the one the shared fixture
    /// installed - configured from its own settings and without the fixture's sink registration.
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
    /// THE ASSERTION IS WHOSE ROWS COME BACK, not merely that a 200 arrives. A status alone would pass if
    /// the segment were silently ignored and the PARENT resolved, which is exactly the defect the contract
    /// exists to prevent.
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
        // correction rather than a preference.
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
    /// The legacy alias resolution matched with <c>like '%alias%'</c>, so an alias that was a substring of
    /// another resolved the wrong tenant - a defect recorded in <c>_NOTES.md</c> and closed here by exact,
    /// whole-segment matching. This fact pins that boundary from the outside.
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

    /// <summary>
    /// An alias whose path segment names one of the deployment's own roots cannot hijack the requests
    /// addressed to that root.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// Two independent changes close it, and this fact exercises both. The alias write paths now REFUSE a
    /// reserved segment - <c>PortalAliasTopology.ReservedPathSegments</c>, proved unit-side in
    /// <c>PortalAliasContractTests</c> - and the resolver treats a reserved first segment as belonging to
    /// the deployment rather than to a tenant, so it looks up the bare authority and nothing else.
    /// </remarks>
    [Fact]
    public async Task AnAliasNamingAReservedSegment_CannotHijackTheDeploymentsOwnRoot()
    {
        using HttpClient client = await _fixture.CreateHostClientAsync();

        string suffix = Guid.NewGuid().ToString("N")[..8];

        using HttpResponseMessage created = await client.PostAsJsonAsync(
            new Uri("/api/v1/portals", UriKind.Relative),
            new
            {
                portalName = "Reserved Segment Tenant " + suffix,
                portalAlias = "reserved-tenant-" + suffix + ".local",
                homeDirectory = string.Empty,
                templateFile = "admin.template",
                isChildPortal = false,
                administratorFirstName = "Reserved",
                administratorLastName = "Administrator",
                administratorUsername = "reserved_admin_" + suffix,
                administratorPassword = ApiTestFixture.KnownPassword,
                administratorEmail = "reserved." + suffix + "@example.com",
            },
            ApiTestFixture.Json);

        created.StatusCode.Should().Be(
            HttpStatusCode.Created,
            "the rival tenant has to be a fully designated portal, or it could not resolve for any reason");

        using JsonDocument document = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
        int rivalPortalId = document.RootElement.GetProperty("data").GetProperty("portalId").GetInt32();

        // The row no endpoint will accept: an alias whose path segment is the API's own root.
        await _fixture.Database.ExecuteAsync(
            """
            INSERT INTO [dbo].[PortalAlias] ([PortalID], [HTTPAlias]) VALUES (@portalId, @alias);
            """,
            new Dictionary<string, object?>
            {
                ["@portalId"] = rivalPortalId,
                ["@alias"] = ApiTestFixture.TestHost + "/api",
            });

        int parentRoleId = _fixture.Seed.AdministratorRoleId;

        using HttpResponseMessage response = await client.GetAsync(
            new Uri($"/api/v1/roles/{parentRoleId}", UriKind.Relative));

        response.StatusCode.Should().Be(
            HttpStatusCode.OK,
            "a stored alias of host/api names nothing this deployment resolves, so the bare host still "
                + "resolves the tenant it always did");

        (await response.Content.ReadAsStringAsync()).Should().Contain(
            IntegrationSeed.AdministratorsRoleName,
            "and the row that answers belongs to the arrival tenant rather than to the alias's owner");
    }

    /// <summary>
    /// A stored alias carrying more path segments than the deployment can route resolves nothing, rather
    /// than resolving a tenant at an address no request can legitimately reach.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// The outcome is 404 for the reason the sibling fact above explains: nothing resolves, so the path is
    /// not rebased, the unrebased path matches no route, and routing answers before any tenant-dependent
    /// endpoint is reached. The alias is not silently ignored either - it is unreachable, and
    /// <c>PortalAliasConformanceMonitor</c> reports every stored alias in this condition at start-up.
    /// </remarks>
    [Fact]
    public async Task AnAliasDeeperThanTheDeploymentCanRoute_ResolvesNothing()
    {
        using HttpClient client = await _fixture.CreateHostClientAsync();

        string suffix = Guid.NewGuid().ToString("N")[..8];
        string firstSegment = "deep-" + suffix;
        string secondSegment = "nested-" + suffix;

        using HttpResponseMessage created = await client.PostAsJsonAsync(
            new Uri("/api/v1/portals", UriKind.Relative),
            new
            {
                portalName = "Deep Tenant " + suffix,
                portalAlias = "deep-tenant-" + suffix + ".local",
                homeDirectory = string.Empty,
                templateFile = "admin.template",
                isChildPortal = false,
                administratorFirstName = "Deep",
                administratorLastName = "Administrator",
                administratorUsername = "deep_admin_" + suffix,
                administratorPassword = ApiTestFixture.KnownPassword,
                administratorEmail = "deep." + suffix + "@example.com",
            },
            ApiTestFixture.Json);

        created.StatusCode.Should().Be(HttpStatusCode.Created);

        using JsonDocument document = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
        int deepPortalId = document.RootElement.GetProperty("data").GetProperty("portalId").GetInt32();

        await _fixture.Database.ExecuteAsync(
            """
            INSERT INTO [dbo].[PortalAlias] ([PortalID], [HTTPAlias]) VALUES (@portalId, @alias);
            """,
            new Dictionary<string, object?>
            {
                ["@portalId"] = deepPortalId,
                ["@alias"] = ApiTestFixture.TestHost + "/" + firstSegment + "/" + secondSegment,
            });

        using HttpResponseMessage response = await client.GetAsync(
            new Uri($"/{firstSegment}/{secondSegment}/api/v1/roles", UriKind.Relative));

        response.StatusCode.Should().Be(
            HttpStatusCode.NotFound,
            "the resolver considers one path segment, so a two-segment alias is never a candidate and "
                + "nothing is rebased");

        ProblemDetails? problem = await response.Content
            .ReadFromJsonAsync<ProblemDetails>(ApiTestFixture.Json);

        problem.Should().NotBeNull();
        problem!.Status.Should().Be(StatusCodes.Status404NotFound);
    }

    /// <summary>
    /// An addressable segment that matches no stored alias resolves NOTHING, rather than falling back to
    /// the bare host.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// ⚠ WHAT THIS PINS, STATED EXACTLY. The resolver's candidate chain used to end in the bare authority,
    /// so an address naming a tenant segment that did not exist RESOLVED THE PARENT. It no longer does: the
    /// chain now holds one candidate and nothing else.
    /// </remarks>
    [Fact]
    public async Task AnUnknownTenantSegment_DoesNotFallBackToTheBareHost()
    {
        using HttpClient client = _fixture.CreateClient();

        string unknownSegment = "absent-" + Guid.NewGuid().ToString("N")[..8];

        using HttpResponseMessage response = await client.PostAsJsonAsync(
            new Uri($"/{unknownSegment}/api/v1/auth/login", UriKind.Relative),
            new
            {
                username = IntegrationSeed.HostUserName,
                password = ApiTestFixture.KnownPassword,
            },
            ApiTestFixture.Json);

        response.StatusCode.Should().NotBe(
            HttpStatusCode.OK,
            "the credential is valid for the bare host's tenant, so a fallback to it would answer 200 - "
                + "an address naming a tenant segment that matches no stored alias must not be served as "
                + "though it had named the bare host");

        string body = await response.Content.ReadAsStringAsync();

        body.Should().NotContain(
            "accessToken",
            "no session may be minted for an address that resolved no tenant, whichever refusal the "
                + "pipeline reaches first");
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
