using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DnnMigration.Application.Dtos.Auth;
using DnnMigration.Application.Dtos.User;
using DnnMigration.IntegrationTests;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Serilog.Core;
using Xunit;

namespace DnnMigration.IntegrationTests.Api;

/// <summary>
/// Pins the anonymous liveness/readiness split, the health-document contract, and trusted forwarded-scheme
/// processing at the API perimeter.
/// </summary>
/// <remarks>
/// <para>
/// The image and compose topology probe <c>/health</c> before the frontend starts, and <c>/health</c> and
/// <c>/health/live</c> are the dependency-free liveness views; <c>/health/ready</c> is the readiness view an
/// orchestrator may gate traffic on. All three must remain anonymous and exempt from HTTPS redirection
/// because the container reaches Kestrel directly over its private HTTP listener.
/// </para>
/// <para>
/// MIGRATION: this file asserts net-new behaviour rather than preserved behaviour. There is no legacy health
/// endpoint to port. The nearest analogue in the legacy tree is <c>Website/KeepAlive.aspx</c>, which exists
/// to stop an idle worker process being recycled rather than to report a dependency's condition, so there is
/// no predecessor contract to hold this one against. The contract asserted here comes from
/// <c>docs/project-guide.md:L225-L228</c>, which records the endpoint answering with <c>status</c>,
/// <c>timestamp</c>, <c>version</c> and <c>serviceName</c>.
/// </para>
/// <para>
/// MIGRATION: the container probe is <c>wget --spider</c> rather than <c>curl</c>, because the Alpine
/// runtime image ships wget and not curl. That is recorded here as the reason the health paths must answer
/// plain HTTP with no redirect, and it is deliberately NOT asserted: invoking a probe binary from a test would
/// exercise the sandbox rather than the application. The redirect and status-code assertions below are the
/// equivalent of what that probe needs, and they hold without a container runtime.
/// </para>
/// </remarks>
[Trait("Category", "Integration")]
[Collection(IntegrationTestCollection.Name)]
public sealed class HealthCheckTests
{
    /// <summary>
    /// The path the endpoint is mapped at, restated here as this suite's own expectation.
    /// </summary>
    /// <remarks>
    /// Deliberately a literal rather than a reference to the API's own constant. Three things outside this
    /// codebase are pinned to this exact string - the image's health probe, the compose health condition and
    /// the published operator documentation - so a suite that read the value from the code under test would
    /// follow a rename instead of failing on one, and a rename here is precisely the change that breaks a
    /// deployment while every build stays green.
    /// </remarks>
    private const string HealthPath = "/health";

    /// <summary>Explicit dependency-free liveness path.</summary>
    private const string LivenessPath = "/health/live";

    /// <summary>The service name the document must report.</summary>
    /// <remarks>
    /// The writer derives this from the API assembly's name, so it resolves to the same string the container's
    /// entry point starts - <c>DnnMigration.Api.dll</c>. Stated as a literal for the same reason as
    /// <see cref="HealthPath"/>: it is the value the published documentation records.
    /// </remarks>
    private const string ExpectedServiceName = "DnnMigration.Api";

    /// <summary>The version the document must report.</summary>
    /// <remarks>
    /// No project sets an explicit version, so the assembly carries the SDK default, which is exactly what the
    /// published documentation records. An assertion here therefore also catches someone adding a version
    /// property without updating the documentation that quotes it.
    /// </remarks>
    private const string ExpectedVersion = "1.0.0.0";

    /// <summary>The aggregate status word a fully healthy report carries.</summary>
    private const string HealthyStatus = "Healthy";

    /// <summary>The aggregate status word a serviceable process with a non-fatal control loss carries.</summary>
    private const string DegradedStatus = "Degraded";

    /// <summary>The aggregate status word an unhealthy report carries.</summary>
    /// <remarks>
    /// A 503 can only be produced by this aggregate status. The database probe declares its failure status
    /// as unhealthy, while the audit-delivery probe declares degraded and therefore remains HTTP 200, and the
    /// framework maps a degraded aggregate to 200 rather than 503 - so there is no route by which a 503
    /// carries any other word.
    /// </remarks>
    private const string UnhealthyStatus = "Unhealthy";

    /// <summary>Host and port the unreachable-dependency host is pointed at.</summary>
    /// <remarks>
    /// <para>
    /// A loopback address with a port nothing is listening on, so a connection attempt is refused immediately
    /// rather than waiting out a timeout. Loopback rather than a routable address on purpose: a test must not
    /// depend on name resolution or on reaching anything outside the machine it runs on.
    /// </para>
    /// <para>
    /// ⚠ THE PORT IS ASKED FOR RATHER THAN CHOSEN. This used to be a constant naming a fixed high port, which
    /// silently assumed nothing else on the machine had bound it - and several clones of this repository build
    /// and test in parallel on one host, each with its own containers and tooling. A collision would not fail
    /// loudly: a process that ACCEPTED the connection would replace the intended refusal with a handshake
    /// against something unrelated, and the 503 this suite exists to prove would be attributed to the wrong
    /// cause. <see cref="RefusedEndpoint"/> obtains the port from the kernel and releases it, so the refusal is
    /// established rather than assumed. It is therefore a static readonly field rather than a constant.
    /// </para>
    /// </remarks>
    private static readonly string UnreachableHostAddress = RefusedEndpoint.ServerAddress;

    /// <summary>Database name the unreachable-dependency host names.</summary>
    /// <remarks>
    /// Distinctive enough that finding it anywhere in a response body is unambiguous evidence of a leak, and
    /// deliberately unlike the provisioned name so the two cannot be confused in a failure message.
    /// </remarks>
    private const string UnreachableDatabaseName = "dnn_unreachable_probe_target";

    /// <summary>Name of the probe that opens a connection of its own.</summary>
    private const string DatabaseProbeName = "database";

    /// <summary>
    /// Name the withdrawn duplicate database probe was registered under, asserted ABSENT.
    /// </summary>
    /// <remarks>
    /// An earlier revision registered the health-check package's own SQL Server check alongside this
    /// solution's. Both read <c>ConnectionStrings:Default</c> and both opened a connection to the instance it
    /// names, and both carried the readiness tag, so every probe of the readiness view opened two connections
    /// to learn one fact - and the api image's <c>HEALTHCHECK</c>, the compose health condition and any
    /// orchestrator poll all reach a health path for the life of the container. The name is kept here as a NEGATIVE assertion rather than
    /// deleted: without it, re-adding the duplicate registration would restore the cost silently, since every
    /// other assertion in this file is satisfied by a report that names more probes than it needs to.
    /// </remarks>
    private const string WithdrawnSqlServerProbeName = "sqlserver";

    /// <summary>Name of the process-local failed-audit-delivery probe.</summary>
    private const string AuditPipelineProbeName = "audit-pipeline";

    /// <summary>Name of the probe that reports the refresh-token store's identity, locality and capacity.</summary>
    /// <remarks>
    /// Registered for the LIVENESS view and never the readiness one. The store it reports on holds refresh
    /// state in this process, so it reaches nothing external and cannot hold a starting container back; and
    /// neither a process-local store nor a saturated one stops this instance serving requests, so withdrawing
    /// the instance from rotation over it would cost more than the condition does.
    /// </remarks>
    private const string RefreshTokenStoreProbeName = "refresh-token-store";

    /// <summary>
    /// A phrase from the refresh-token store probe's healthy description, asserted PRESENT in the operator log.
    /// </summary>
    /// <remarks>
    /// The store's process-local state model used to be recorded in prose only - in the store's own remarks, in
    /// <c>README.md</c> and in <c>MIGRATION_NOTES.md</c> - so a running deployment offered no way to confirm
    /// it. This phrase is how the running application now states it, and asserting it here is what makes the
    /// report a property of the delivery rather than of a document.
    /// </remarks>
    private const string RefreshTokenLocalityPhrase = "not shared between replicas";

    /// <summary>The media type the document must be served as.</summary>
    private const string JsonMediaType = "application/json";

    /// <summary>
    /// The media type this document must never be served as, because it is not an error envelope.
    /// </summary>
    private const string ProblemMediaType = "application/problem+json";

    /// <summary>
    /// The four members the published operator documentation names - the whole of the contract.
    /// </summary>
    /// <remarks>
    /// EXHAUSTIVENESS IS ASSERTED, not merely presence. An earlier revision of this suite asserted presence
    /// only, on the argument that additional diagnostic members are additive and a monitor reading these four
    /// is served identically either way. Both halves of that argument fail here. The endpoint is ANONYMOUS,
    /// so a diagnostic member is published to any unauthenticated caller - the per-probe array an earlier
    /// writer emitted enumerated the application's internal dependencies and their authored descriptions to
    /// exactly that audience. And a documented four-member contract that in fact returns six is a contract
    /// nothing can rely on, because a consumer cannot tell which surface is authoritative. The per-probe
    /// detail an operator genuinely needs is now written to the structured log instead, where the audience is
    /// the operator rather than the internet.
    /// </remarks>
    private static readonly IReadOnlyList<string> DocumentedMembers =
        new[] { "status", "timestamp", "version", "serviceName" };

    /// <summary>
    /// The endpoint as a relative address.
    /// </summary>
    /// <remarks>
    /// Relative on purpose. The fixture binds every client's base address to the seeded alias, and the
    /// tenant-resolution middleware reads the tenant from the resulting host header, so an absolute address
    /// here would silently address a host the seed never registered.
    /// </remarks>
    private static readonly Uri HealthEndpoint = new(HealthPath, UriKind.Relative);

    /// <summary>
    /// The path the readiness view is mapped at, restated here as this suite's own expectation.
    /// </summary>
    /// <remarks>
    /// A literal for the same reason as <see cref="HealthPath"/>. The two views answer different questions -
    /// liveness asks whether the process can answer at all, readiness asks whether it can serve a request
    /// end to end - and the whole value of the split is that the address depended on from outside this
    /// codebase is the one that does NOT require a reachable store.
    /// </remarks>
    private const string ReadinessPath = "/health/ready";

    /// <summary>The readiness view as a relative address.</summary>
    private static readonly Uri ReadinessEndpoint = new(ReadinessPath, UriKind.Relative);

    /// <summary>Header a proxy uses to report the original caller's address.</summary>
    private const string ForwardedForHeader = "X-Forwarded-For";

    /// <summary>Header a proxy uses to report the scheme the caller actually used.</summary>
    private const string ForwardedProtoHeader = "X-Forwarded-Proto";

    /// <summary>Response header carrying the strict transport policy.</summary>
    private const string StrictTransportSecurityHeader = "Strict-Transport-Security";

    private readonly ApiTestFixture _fixture;

    /// <summary>Initialises a new instance of the <see cref="HealthCheckTests"/> class.</summary>
    /// <param name="fixture">The shared composed host and provisioned database.</param>
    public HealthCheckTests(ApiTestFixture fixture) => _fixture = fixture;

    /// <summary>
    /// The endpoint answers a caller carrying no credential at all, and answers it directly rather than with
    /// a refusal or a redirect.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// <para>
    /// Four specific status codes are excluded before the permitted set is asserted, and the order is
    /// deliberate: each names the change that would have produced it, so a regression reports its own cause
    /// instead of reporting only that the status was unexpected.
    /// </para>
    /// <para>
    /// A 401 or a 403 means an authorisation requirement reached this endpoint - a
    /// <c>RequireAuthorization</c> call, a policy, or a fallback policy that catches every endpoint which
    /// does not opt out. A 404 means the mapping is gone or the path moved. A 429 means the credential rate
    /// limiter started classifying this endpoint as credential-bearing, which would let a burst of probes
    /// make the container unhealthy. A 3xx with a <c>Location</c> means HTTPS redirection was enabled
    /// unconditionally, so the plain-HTTP probe inside the container would be answered with a redirect to an
    /// authority that container does not serve.
    /// </para>
    /// <para>
    /// <b>The permitted answer is 200 and nothing else, because this fact IS the acceptance gate.</b> The gate
    /// probes the endpoint with <c>curl -f</c>, which fails on any 4xx or 5xx, and the compose file holds the
    /// front-end service back with <c>condition: service_healthy</c>, which never flips while the image's own
    /// probe is failing. A 503 therefore fails acceptance outright, so a fact that tolerated one would report
    /// success for a deployment the gate rejects - which is the entire value of this assertion.
    /// </para>
    /// <para>
    /// This is not a claim that 503 is wrong in general: it is the correct answer when a dependency is down,
    /// and the endpoint is required to give it readably. It is asserted as such, on its own host, by
    /// <see cref="Readiness_WhenADependencyIsUnreachable_AnswersTheDocumentedContractWith503"/>. What makes the
    /// exact assertion sound HERE is that this host's dependency is provisioned and reachable for the whole
    /// run - the fixture provisions it before any fact executes - so on this host a 503 can only mean a
    /// regression, and admitting one would leave the gate's own precondition unmeasured.
    /// </para>
    /// <para>
    /// The aggregate status word is read as well as the status code. A 200 carrying a word other than healthy
    /// would satisfy the probe and mislead every monitor reading the document, and only the body can tell the
    /// two apart.
    /// </para>
    /// <para>
    /// The redirect half is worth stating separately because it is a container obligation rather than a
    /// preference. The probe is <c>wget --spider</c> against plain HTTP inside the image - Alpine ships no
    /// curl - and the front-end service declares <c>depends_on</c> with condition <c>service_healthy</c>. An
    /// unguarded <c>UseHttpsRedirection</c> would answer 307 with a <c>Location</c> pointing at an https
    /// authority the container does not serve, the probe would never succeed, and the front end would never
    /// start. The client does not follow redirects, so a 307 surfaces here as a 307 instead of being chased.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData(HealthPath)]
    [InlineData(LivenessPath)]
    [InlineData(ReadinessPath)]
    public async Task HealthViews_WithoutCredentials_AnswerAnonymously(string path)
    {
        using HttpClient client = _fixture.CreateAnonymousClient();

        // Stated rather than assumed. The suite has clients that carry a bearer token, and an assertion about
        // anonymous reachability made through one of those would pass while proving nothing.
        client.DefaultRequestHeaders.Authorization.Should().BeNull(
            "the container probe carries no credential, so this must be asserted through a client that has "
            + "none either");

        using HttpResponseMessage response = await client.GetAsync(new Uri(path, UriKind.Relative));

        response.StatusCode.Should().NotBe(
            HttpStatusCode.Unauthorized,
            "the endpoint is mapped with AllowAnonymous, so a 401 means an authorisation requirement reached "
            + "it and the container probe would never succeed");
        response.StatusCode.Should().NotBe(
            HttpStatusCode.Forbidden,
            "a 403 means a policy is being evaluated for this endpoint, and the probe has no claims to "
            + "satisfy one with");
        response.StatusCode.Should().NotBe(
            HttpStatusCode.NotFound,
            "a 404 means the health endpoint is no longer mapped at {0}, which the image's HEALTHCHECK and "
            + "the compose health condition may be pinned to",
            path);
        response.StatusCode.Should().NotBe(
            HttpStatusCode.TooManyRequests,
            "the rate limiter resolves this endpoint to its no-limit partition on purpose, so a burst of "
            + "probes must never be able to report the container unhealthy");

        ((int)response.StatusCode).Should().NotBeInRange(
            300,
            399,
            "the probe follows nothing, so a redirect leaves the API permanently unhealthy and the front-end "
            + "service never starts");
        response.Headers.Location.Should().BeNull(
            "an unguarded UseHttpsRedirection would answer 307 with a Location pointing at an https "
            + "authority the container does not serve, and the client follows nothing so a 307 surfaces "
            + "here rather than being chased");

            response.StatusCode.Should().Be(
                HttpStatusCode.OK,
                "on a host whose dependency is provisioned and reachable every view answers 200: the liveness "
                + "views run no dependency probe at all, and the readiness view's one probe is sound - and the "
                + "acceptance gate probes with 'curl -f' and holds the front-end service back on "
                + "'condition: service_healthy', so anything but 200 here fails acceptance");
    }

    /// <summary>
    /// The compatibility liveness document carries the published members and no dependency checks.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// <para>
    /// The media type matters twice over. It must be <c>application/json</c>, and it must specifically NOT be
    /// <c>application/problem+json</c>: a 503 from this endpoint is a health report and not an RFC 7807 error
    /// envelope, so a change that routed the unhealthy case through the problem-details edge would replace
    /// the four documented members with <c>type</c>, <c>title</c>, <c>status</c> and <c>detail</c> and every
    /// monitor reading the documented shape would break.
    /// </para>
    /// <para>
    /// The liveness view selects no registered dependency, so it always answers 200, and it writes the same
    /// four members every other view writes rather than a shape of its own.
    /// </para>
    /// <para>
    /// <c>serviceName</c>, <c>version</c> and <c>timestamp</c> are asserted on both branches because none of
    /// the three depends on the dependency's condition - the first two are read from the assembly and the
    /// third from the clock - so exempting them from the unhealthy branch would leave the case an operator
        /// most needs to read unasserted. Only the aggregate status word is branch-specific, and the two
        /// branches are two FACTS rather than one fact with a conditional: each is asserted against the host
        /// that produces it, so neither can pass by never having been exercised. This one covers the healthy
        /// branch on the shared host; the unhealthy branch is covered by
        /// <see cref="Readiness_WhenADependencyIsUnreachable_AnswersTheDocumentedContractWith503"/> against a
        /// host whose dependency cannot be reached. A 200 admits a healthy or a degraded aggregate, because the
        /// process-local audit-delivery probe joins the liveness view and declares degraded as its failure
        /// status; a 503 admits only an unhealthy one, because a degraded aggregate itself maps to 200.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Health_WhenEveryDependencyIsSound_AnswersTheDocumentedJsonContract()
    {
        using HttpClient client = _fixture.CreateAnonymousClient();

        using HttpResponseMessage response = await client.GetAsync(HealthEndpoint);
        string body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(
            HttpStatusCode.OK,
            "the compatibility /health path is liveness and executes no dependency checks");
        response.Content.Headers.ContentType.Should().NotBeNull(
            "a document with no declared media type cannot be parsed by a monitor without guessing");
        response.Content.Headers.ContentType!.MediaType.Should().Be(
            JsonMediaType,
            "the published contract is a JSON document");
        response.Content.Headers.ContentType.MediaType.Should().NotBe(
            ProblemMediaType,
            "this is a health report on both branches, not an error envelope, so it must never be routed "
            + "through the problem-details edge");

        using JsonDocument document = JsonDocument.Parse(body);
        JsonElement root = document.RootElement;

        root.ValueKind.Should().Be(
            JsonValueKind.Object,
            "the documented shape is an object with named members, not an array or a bare value");

        IReadOnlyList<string> members = ReadMemberNames(root);

        members.Should().BeEquivalentTo(
            DocumentedMembers,
            "the published contract is these four members and no others: this endpoint is anonymous, so any "
            + "additional member is published to an unauthenticated caller, and a body wider than its own "
            + "documented contract leaves a consumer unable to tell which surface is authoritative");

        // Assembly-derived and clock-derived, therefore asserted on both branches.
        root.GetProperty("serviceName").GetString().Should().Be(
            ExpectedServiceName,
            "the name is read from the API assembly, so it must match the assembly the container's entry "
            + "point starts");
        root.GetProperty("version").GetString().Should().Be(
            ExpectedVersion,
            "the published documentation quotes this exact version");
        AssertRoundTrippableUtcInstant(root.GetProperty("timestamp"));

        string? status = root.GetProperty("status").GetString();

        if (response.StatusCode == HttpStatusCode.OK)
        {
            status.Should().BeOneOf(
                new[] { HealthyStatus, DegradedStatus },
                "the audit-delivery probe reports a non-fatal loss as degraded, which remains HTTP 200");
        }
        else
        {
            status.Should().Be(
                UnhealthyStatus,
                "a degraded aggregate maps to 200, so a 503 can only carry an unhealthy one");
        }
    }

    /// <summary>
    /// The document names no registered probe and carries no per-probe diagnostic detail, on either view.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// <para>
    /// MIGRATION: THIS TEST WAS INVERTED. An earlier revision asserted that the body listed every registered
    /// probe by name, with its status and duration, on the argument that naming the failing probe is the
    /// endpoint's entire diagnostic value. The diagnostic value is real and is kept - it moved to the
    /// structured log, which <c>Health_ReportsEveryRegisteredProbeToTheOperatorLog</c> asserts - but
    /// publishing it HERE was wrong, because both views are anonymous and the documented contract is four
    /// members. An unauthenticated caller was being handed an enumeration of the application's internal
    /// dependencies together with each probe's authored description, which is reconnaissance rather than
    /// diagnostics, and the body contradicted its own published shape.
    /// </para>
    /// <para>
    /// Both probe names are asserted absent by name rather than only the container member, because a later
    /// revision could reintroduce the detail under a different member name - flattened, renamed, or nested -
    /// and a test that looked only for <c>checks</c> would not notice. The whole body is searched, so the
    /// assertion holds whatever shape the reintroduction took.
    /// </para>
    /// </remarks>
    /// <summary>
    /// Every health view answers identically when the caller PRESENTS a bearer token for an account that owes
    /// mandatory remediation.
    /// </summary>
    /// <param name="path">The health view being probed.</param>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// <para>
    /// WHY THIS IS A SEPARATE FACT FROM THE ANONYMOUS ONE. The sibling theory asserts that the views answer
    /// with NO credential, which is the container probe's case. It cannot see the failure this one guards
    /// against, because that failure needed a credential to be PRESENT: the store-backed remediation stage
    /// refused every authenticated request that was not explicitly exempt, and the health views - anonymous by
    /// contract, and mapped with <c>AllowAnonymous</c> - were not among the exemptions. A caller holding a
    /// token for an account owing a credential change therefore received 403 from all three, while the same
    /// three answered 200 to a caller holding nothing at all.
    /// </para>
    /// <para>
    /// That is the opposite of a liveness signal. An endpoint whose answer depends on WHO ASKS cannot report
    /// whether the process is alive, and an authenticated monitoring agent, dashboard or load balancer that
    /// attaches a service token would read the service as failing while it served every other request
    /// correctly. The token used here owes a credential change specifically because that is the cheapest
    /// remediation state to establish deterministically; the requirement being asserted is about the health
    /// views, not about which remediation is outstanding.
    /// </para>
    /// <para>
    /// The assertion is that the two answers AGREE, rather than that either is 200: a readiness view is
    /// entitled to answer 503 when a dependency is down, and pinning 200 here would make this fact fail for a
    /// reason it is not about. What must never differ is the answer given to the same process by two callers.
    /// </para>
    /// </remarks>
    [Theory]
    [Trait("Category", "Integration")]
    [InlineData(HealthPath)]
    [InlineData(LivenessPath)]
    [InlineData(ReadinessPath)]
    public async Task HealthViews_WithARemediatingToken_AnswerExactlyAsTheyDoAnonymously(string path)
    {
        var relative = new Uri(path, UriKind.Relative);

        using HttpClient host = await _fixture.CreateHostClientAsync();

        CreateUserRequest request = new()
        {
            Username = "health_rem_" + Guid.NewGuid().ToString("N")[..10],
            FirstName = "Health",
            LastName = "Remediation",
            DisplayName = "Health Remediation",
            Email = "health.rem." + Guid.NewGuid().ToString("N")[..10] + "@example.com",
            Password = ApiTestFixture.KnownPassword,
            ConfirmPassword = ApiTestFixture.KnownPassword,
            Authorize = true,
        };

        using HttpResponseMessage created = await host.PostAsJsonAsync(
            new Uri("/api/v1/users", UriKind.Relative),
            request,
            ApiTestFixture.Json);

        created.StatusCode.Should().Be(HttpStatusCode.Created);

        UserDetailDto? account = await created.Content.ReadEnvelopeAsync<UserDetailDto>();
        account.Should().NotBeNull();

        try
        {
            using HttpResponseMessage required = await host.PostAsync(
                new Uri(
                    FormattableString.Invariant($"/api/v1/users/{account!.UserId}/require-password-change"),
                    UriKind.Relative),
                content: null);

            required.StatusCode.Should().Be(HttpStatusCode.NoContent);

            using HttpClient anonymous = _fixture.CreateAnonymousClient();

            using HttpResponseMessage issued = await anonymous.PostAsJsonAsync(
                new Uri("/api/v1/auth/login", UriKind.Relative),
                new LoginRequest { Username = request.Username, Password = ApiTestFixture.KnownPassword },
                ApiTestFixture.Json);

            issued.StatusCode.Should().Be(
                HttpStatusCode.OK,
                "an outstanding credential change is an advisory on sign-in, not a refusal");

            LoginResponse? session = await issued.Content.ReadEnvelopeAsync<LoginResponse>();
            session.Should().NotBeNull();
            session!.MustChangePassword.Should().BeTrue(
                "the token has to be a REMEDIATING one, or this fact measures nothing");

            using HttpClient bearer = AuthenticatedClientFactory.Authenticate(
                _fixture.CreateAnonymousClient(),
                session.AccessToken);

            using HttpResponseMessage withToken = await bearer.GetAsync(relative);
            using HttpResponseMessage withoutToken = await _fixture.CreateAnonymousClient().GetAsync(relative);

            withToken.StatusCode.Should().NotBe(
                HttpStatusCode.Forbidden,
                "a health view is anonymous by contract, so presenting a credential it does not consult "
                + "cannot turn it into a denial");
            withToken.StatusCode.Should().NotBe(
                HttpStatusCode.Unauthorized,
                "the view answers with no credential at all, so it cannot require a valid one");

            withToken.StatusCode.Should().Be(
                withoutToken.StatusCode,
                "the answer must not depend on who asks, or the probe reports the caller rather than the "
                + "process");

            // The token is still a restricted one, which is what makes the exemption above narrow rather
            // than a hole. Asserted in the same fact so a change that exempted everything would fail here.
            using HttpResponseMessage ordinary = await bearer.GetAsync(
                new Uri(
                    FormattableString.Invariant($"/api/v1/users/{account.UserId}"),
                    UriKind.Relative));

            ordinary.StatusCode.Should().Be(
                HttpStatusCode.Forbidden,
                "the remediation boundary still closes the ordinary protected surface");
        }
        finally
        {
            using HttpResponseMessage removed = await host.DeleteAsync(new Uri(
                FormattableString.Invariant($"/api/v1/users/{account!.UserId}"),
                UriKind.Relative));

            removed.StatusCode.Should().BeOneOf(HttpStatusCode.NoContent, HttpStatusCode.NotFound);
        }
    }

    [Fact]
    public async Task Health_NamesNoRegisteredProbe()
    {
        using HttpClient client = _fixture.CreateAnonymousClient();

        using HttpResponseMessage response = await client.GetAsync(HealthEndpoint);
        string body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(
            HttpStatusCode.OK,
            "this host's dependency is provisioned and reachable, so every probe must report healthy");

        using JsonDocument document = JsonDocument.Parse(body);
        JsonElement root = document.RootElement;

        root.TryGetProperty("checks", out _).Should().BeFalse(
            "per-probe detail belongs in the structured log, not in an anonymous response body");
        root.TryGetProperty("totalDurationMs", out _).Should().BeFalse(
            "the documented contract carries no timing member, and a monitor that read one would be relying "
            + "on a value the documentation never promised");

        body.Should().NotContainEquivalentOf(
            DatabaseProbeName,
            "naming the probe that opens its own connection tells an anonymous caller what this application "
            + "depends on");
        body.Should().NotContainEquivalentOf(
            WithdrawnSqlServerProbeName,
            "the withdrawn duplicate probe must not reappear, and naming a storage technology to an anonymous "
            + "caller would be reconnaissance even if it had");
        body.Should().NotContainEquivalentOf(
            AuditPipelineProbeName,
            "the audit-delivery counter is named to an operator through the log, never to an anonymous caller");
        body.Should().NotContainEquivalentOf(
            RefreshTokenStoreProbeName,
            "the refresh-token store's identity and capacity are operational detail, so they reach an operator "
            + "through the log and never an anonymous caller");
        body.Should().NotContainEquivalentOf(
            RefreshTokenLocalityPhrase,
            "and neither does its finding - telling an unauthenticated caller that refresh state is "
            + "process-local hands them the shape of this deployment");
    }

    /// <summary>
    /// The LIVENESS view runs no readiness probe, so the container's start-up cannot depend on a store.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// <para>
    /// THIS IS THE FACT THE SPLIT EXISTS FOR, and it is asserted as an absence because the failure it guards
    /// against is an addition. The image's own health check and the compose condition that holds the front end
    /// back are both pinned to this exact path, while the compose topology declares NO database service - the
    /// store is external and may legitimately be unreachable when the process starts. A liveness view that ran
    /// a database probe would therefore report the container unhealthy for a reason unrelated to whether it can
    /// answer, hold the front end back behind it, and do so while every probe was reporting the truth.
    /// </para>
    /// <para>
    /// Asserted on the named probes rather than on an empty array, so registering a genuine liveness probe
    /// later - one that depends on nothing external - does not fail a fact that has no business forbidding it.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Health_RunsNoReadinessProbe()
    {
        using (ApiTestFixture.OverrideEnvironment(_fixture.HostConfiguration()))
        {
            await using var host = new RecordingHost();
            using HttpClient client = host.CreateClient();

            int logBaseline = RecordedLogBaseline();

            // Addressed to the LIVENESS path deliberately, and that is the whole fact: what is under assertion
            // is which probes THAT view selects, so reading the readiness view here would assert the exact
            // opposite of the property and still find a well-formed document.
            using HttpResponseMessage response = await client.GetAsync(HealthEndpoint);

            string body = await response.Content.ReadAsStringAsync();

            response.StatusCode.Should().Be(
                HttpStatusCode.OK,
                "a process that can answer is live, whatever its dependencies are doing");

            using JsonDocument document = JsonDocument.Parse(body);

            // WHICH PROBES RAN IS READ FROM THE OPERATOR LOG, NOT FROM THE BODY, because the body deliberately
            // names none: both views are anonymous and an enumeration of this application's dependencies is
            // reconnaissance rather than diagnostics. The report entry the endpoint writes carries the same
            // per-probe detail the body used to, which is what keeps this fact assertable at all.
            string probes = ProbesReportedSince(logBaseline, body);

            probes.Should().NotContain(
                DatabaseProbeName,
                "a readiness-tagged probe must not reach the view the container's start-up depends on");
            probes.Should().NotContain(
                WithdrawnSqlServerProbeName,
                "the duplicate probe of the same database is withdrawn, so it belongs to no view at all");

            // The one probe that DOES belong here, and the reason this fact is asserted on the named probes
            // rather than on an empty report. It is process-local - it counts audit records this instance
            // failed to deliver and reaches nothing external - so it cannot hold the container's start-up on
            // anything, and it is the only place a lost accountability record becomes visible to an operator.
            probes.Should().Contain(
                AuditPipelineProbeName,
                "a failed audit delivery must be observable even though it deliberately does not fail the "
                + "completed business operation");

            // The second probe that belongs here, and for the same two reasons: it reads a dictionary held in
            // this process, so it can hold the container's start-up on nothing, and it is the only place the
            // refresh-token store's state model becomes visible from a running deployment rather than from a
            // document. Its DESCRIPTION is asserted as well as its name, because the roll-call is the one
            // channel that carries it - the anonymous body names no probe and the log deliberately omits every
            // probe's data dictionary.
            probes.Should().Contain(
                RefreshTokenStoreProbeName,
                "an operator has to be able to see which refresh-token store this instance is running before "
                + "deciding whether a second instance is safe");
            probes.Should().Contain(
                RefreshTokenLocalityPhrase,
                "reporting the probe's name without its finding would tell an operator that state locality was "
                + "checked and not what the answer was");

            document.RootElement.GetProperty("status").GetString().Should().BeOneOf(
                new[] { HealthyStatus, DegradedStatus },
                "with no readiness probe selected nothing external can fail the liveness aggregate; the "
                + "process-local audit-delivery probe can only degrade it, and degraded remains HTTP 200");
        }
    }

    /// <summary>
    /// Every registered probe is still reported to an operator, by name, through the structured log.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// <para>
    /// THE OTHER HALF OF THE CONTRACT THAT MOVED. Withdrawing the per-probe array from the anonymous body is
    /// only correct if the detail survives somewhere an operator can read it, and nothing else in this suite
    /// would notice if it did not: every remaining body assertion is satisfied by a report that ran no probe
    /// at all. This fact is what makes the withdrawal a relocation rather than a loss.
    /// </para>
    /// <para>
    /// Addressed to the READINESS view, because that is the view the database probe belongs to. The suite
    /// raises this one log category to debug for the same reason it raises the audit and request categories: a
    /// healthy report is written at debug because a passing probe is not news, so the suite's default floor
    /// would discard exactly the entry under test and the assertion would pass vacuously.
    /// </para>
    /// <para>
    /// EXACTLY ONE DATABASE PROBE, AND THAT IS ASSERTED AS A NUMBER. An earlier revision registered the
    /// health-check package's own SQL Server check beside this solution's, so every poll of the view opened
    /// two connections to learn one fact - and this path is polled for the life of the container. The count is
    /// asserted rather than the mere presence of the one name, because presence alone is satisfied by a
    /// roll-call that also names a duplicate.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Readiness_ReportsEveryRegisteredProbeToTheOperatorLog()
    {
        using (ApiTestFixture.OverrideEnvironment(_fixture.HostConfiguration()))
        {
            await using var host = new RecordingHost();
            using HttpClient client = host.CreateClient();

            int logBaseline = RecordedLogBaseline();

            using HttpResponseMessage response = await client.GetAsync(ReadinessEndpoint);
            string body = await response.Content.ReadAsStringAsync();

            string probes = ProbesReportedSince(logBaseline, body);

            probes.Should().Contain(
                DatabaseProbeName,
                "the probe that opens a connection of its own must stay registered, and an operator must be "
                + "able to tell which probe reported a fault");
            probes.Should().NotContain(
                WithdrawnSqlServerProbeName,
                "a second probe of the same database would open a second connection on every poll of a path "
                + "that is polled continuously, and would tell an operator nothing the first does not");
            probes
                .Split(", ", StringSplitOptions.RemoveEmptyEntries)
                .Count(entry => entry.StartsWith(DatabaseProbeName + "=", StringComparison.Ordinal))
                .Should().Be(
                    1,
                    "one dependency is probed, so one entry names it; a further entry here is a further "
                    + "connection on every container health poll");
            probes.Should().NotContain(
                AuditPipelineProbeName,
                "the audit-delivery counter reaches nothing external, so it is registered for the liveness "
                + "view and the two views select over the readiness tag with complementary predicates");
            probes.Should().NotContain(
                RefreshTokenStoreProbeName,
                "readiness decides whether traffic should reach this instance, and neither a process-local "
                + "refresh store nor a saturated one stops it answering - so reporting it here would withdraw "
                + "a working instance over a condition whose cost is a sign-in");
        }
    }

    /// <summary>Counts the log records written so far, so a later read can ignore every one of them.</summary>
    /// <returns>The number of records the shared sink holds at this moment.</returns>
    /// <remarks>
    /// The sink is process-wide and every fact in this collection writes into it, so "the newest health
    /// report" is not the same thing as "the health report MY request produced". Reading the newest one was
    /// wrong in a way that only shows up in a full run: a liveness fact placed after a readiness fact read the
    /// readiness fact's roll-call and asserted against the wrong view, passing in isolation and failing in the
    /// suite. Every reader therefore records a baseline before its request and reads only past it.
    /// </remarks>
    private static int RecordedLogBaseline() => RecordedLogs.Snapshot().Count;

    /// <summary>How many times a probe roll-call is looked for before the fact is failed.</summary>
    private const int ProbeLogAttempts = 100;

    /// <summary>How long to wait between looks for a probe roll-call.</summary>
    private static readonly TimeSpan ProbeLogPollInterval = TimeSpan.FromMilliseconds(20);

    /// <summary>Reads the probe roll-call written for one request, identified by a baseline.</summary>
    /// <param name="baseline">The record count taken immediately before the request was sent.</param>
    /// <param name="body">The response body, used only to prove a report was actually produced.</param>
    /// <returns>The rendered probe roll-call the endpoint wrote for that request.</returns>
    /// <remarks>
    /// <para>
    /// The roll-call is read from the operator log rather than from the response because the response
    /// deliberately names no probe: both views are anonymous, and an enumeration of this application's
    /// dependencies is reconnaissance rather than diagnostics. The entry the endpoint writes carries the same
    /// per-probe detail the body used to, which is what keeps these facts assertable at all.
    /// </para>
    /// <para>
    /// TWO THINGS MAKE THIS READ SOUND, and both were learned by getting them wrong. It reads only records
    /// written PAST a baseline, because the sink is process-wide and "the newest health report" is not the
    /// same thing as "the report my request produced" - a liveness fact placed after a readiness fact read the
    /// readiness fact's roll-call and asserted against the wrong view, passing alone and failing in a full run.
    /// And it WAITS briefly for the record to appear, because delivery to the sink is not ordered with respect
    /// to the response arriving at this client: under a full run the read could reach the sink first and see
    /// nothing at all. The wait is bounded and short, and exhausting it fails the fact rather than skipping it.
    /// </para>
    /// </remarks>
    private static string ProbesReportedSince(int baseline, string body)
    {
        body.Should().NotBeEmpty("a report with no body would make the log assertion meaningless");

        LogRecord? report = null;

        for (int attempt = 0; attempt < ProbeLogAttempts && report is null; attempt++)
        {
            if (attempt > 0)
            {
                Thread.Sleep(ProbeLogPollInterval);
            }

            report = RecordedLogs.Snapshot()
                .Skip(baseline)
                .LastOrDefault(record => record.Properties.ContainsKey("HealthProbes"));
        }

        report.Should().NotBeNull(
            "the endpoint writes one report entry per request, at debug when healthy, and only entries "
            + "written after this request began are evidence about it");

        return report!.Properties["HealthProbes"]?.ToString() ?? string.Empty;
    }

    /// <summary>
    /// The readiness view answers a caller carrying no credential, exactly as the liveness view does.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// An orchestrator's readiness probe carries no credential either, so a readiness path behind
    /// authentication would report the instance permanently unready. The four documented members are asserted
    /// alongside, because both views are written by the same writer and an operator reading either must see
    /// one document shape rather than two.
    /// </remarks>
    [Fact]
    public async Task Readiness_WithoutCredentials_AnswersTheSameDocumentShape()
    {
        using HttpClient client = _fixture.CreateAnonymousClient();

        using HttpResponseMessage response = await client.GetAsync(ReadinessEndpoint);
        string body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().NotBe(
            HttpStatusCode.Unauthorized,
            "an orchestrator's readiness probe carries no credential");
        response.StatusCode.Should().NotBe(HttpStatusCode.Forbidden);
        response.Content.Headers.ContentType?.MediaType.Should().Be(JsonMediaType);

        using JsonDocument document = JsonDocument.Parse(body);

        foreach (string documented in DocumentedMembers)
        {
            document.RootElement.TryGetProperty(documented, out _).Should().BeTrue(
                "both views are written by one writer, so both carry the documented members");
        }

        document.RootElement.GetProperty("serviceName").GetString().Should().Be(ExpectedServiceName);
        document.RootElement.GetProperty("version").GetString().Should().Be(ExpectedVersion);
    }

    /// <summary>
    /// The document discloses no connection detail, on either branch.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// <para>
    /// This endpoint is anonymous, so everything it writes is public. The failure path is where that bites: a
    /// provider's exception message for an unreachable instance routinely carries the server name, the
    /// database name and sometimes the login, and a probe's data dictionary carries the server and the
    /// database by design. The writer omits both deliberately, and this asserts that the omission is still in
    /// place. Since the body was narrowed to its four documented members the omission is structural as well -
    /// there is no member left for such a value to travel in - so this assertion now guards against a
    /// reintroduction rather than against the writer's own choices.
    /// </para>
    /// <para>
    /// The database name is taken from the fixture rather than written as a literal, because the provisioned
    /// name is generated per run - so a literal could only ever assert against a name no run actually used.
    /// The comparisons are case-insensitive, because a leak that differed only in casing would still be a
    /// leak.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Readiness_WhenEveryDependencyIsSound_DisclosesNoConnectionDetail()
    {
        using HttpClient client = _fixture.CreateAnonymousClient();

        using HttpResponseMessage response = await client.GetAsync(ReadinessEndpoint);
        string body = await response.Content.ReadAsStringAsync();

        body.Should().NotContainEquivalentOf(
            "password",
            "an anonymous reader must never be handed a credential, whatever the probe concluded");
        body.Should().NotContainEquivalentOf(
            _fixture.Database.DatabaseName,
            "the provisioned database name identifies the instance to an anonymous caller");
        body.Should().NotContainEquivalentOf(
            "exception",
            "the probe attaches its error to the result for private logging, and the writer must keep it off "
            + "the wire");
    }

    /// <summary>
    /// A response carries a correlation identifier even when the caller supplied none, and even though the
    /// caller is anonymous.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// This is the ordinary case for this endpoint rather than an edge case: the container probe sends no
    /// correlation header, so if the middleware only ever echoed an inbound one, every health request in the
    /// deployment's log would be uncorrelated. The middleware registers the response header before awaiting
    /// the rest of the pipeline, so the header is present on the success path and not only on failures - and
    /// asserting it here, through the anonymous client, is what proves correlation does not depend on being
    /// authenticated.
    /// </remarks>
    [Fact]
    public async Task Health_WithoutSuppliedCorrelationId_CarriesAGeneratedOne()
    {
        using HttpClient client = _fixture.CreateAnonymousClient();

        using HttpResponseMessage response = await client.GetAsync(HealthEndpoint);

        IReadOnlyList<string> values = ReadCorrelationValues(response);

        values.Should().ContainSingle(
            "the header is assigned rather than appended, so exactly one value must come back");
        values[0].Should().NotBeNullOrWhiteSpace(
            "a blank identifier correlates nothing, so an absent inbound header must produce a generated "
            + "value rather than an empty one");
    }

    /// <summary>
    /// A caller-supplied correlation identifier is adopted and echoed back unchanged, exactly once.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// <para>
    /// This is the half of the contract the preceding assertion cannot reach. A middleware that ignored the
    /// inbound header and generated an identifier on every request would still satisfy "a response carries
    /// one", while breaking the only thing the header is for: following a single request across the client,
    /// the proxy and the API in one search.
    /// </para>
    /// <para>
    /// The value count is asserted as well as the value, because appending rather than assigning would send
    /// both the supplied identifier and a generated one. A reader taking the first value would still see the
    /// right answer and the assertion would pass while the response carried a smuggled second identifier.
    /// The supplied value comes from <see cref="ApiTestFixture.NewCorrelationId"/> on purpose: the middleware
    /// trusts an inbound header only when exactly one line is present and its content is one of two CANONICAL
    /// shapes - 32 hexadecimal characters, or the hyphenated 36-character UUID rendering - so a readable label
    /// would be replaced by a generated one and this test would be asserting the rejection path while
    /// appearing to assert the echo path.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Health_WithSuppliedCorrelationId_EchoesExactlyThatValue()
    {
        string Supplied = ApiTestFixture.NewCorrelationId();

        using HttpClient client = _fixture.CreateAnonymousClient();
        using HttpRequestMessage request = ApiTestFixture.WithCorrelationId(
            new HttpRequestMessage(HttpMethod.Get, HealthEndpoint),
            Supplied);

        using HttpResponseMessage response = await client.SendAsync(request);

        IReadOnlyList<string> values = ReadCorrelationValues(response);

        values.Should().ContainSingle(
            "appending instead of assigning would return the supplied identifier alongside a generated one, "
            + "and a reader taking the first value would never notice");
        ApiTestFixture.ReadCorrelationId(response).Should().Be(
            Supplied,
            "the identifier a caller supplies is what every log line for that request must carry");
    }

    /// <summary>
    /// A forwarded HTTPS scheme from the configured proxy network is applied before redirection and HSTS.
    /// </summary>
    [Fact]
    public async Task ForwardedHttps_FromATrustedNetwork_ReachesThePipelineAsHttps()
    {
        Dictionary<string, string?> configuration = PerimeterConfiguration();
        configuration["Proxy__KnownNetworks__0"] = "0.0.0.0/0";
        configuration["Proxy__KnownNetworks__1"] = "::/0";

        using (ApiTestFixture.OverrideEnvironment(configuration))
        {
            await using var host = new PerimeterHost();
            using HttpClient client = host.CreateClient(new WebApplicationFactoryClientOptions
            {
                AllowAutoRedirect = false,
                BaseAddress = new Uri("http://perimeter.example.test", UriKind.Absolute),
            });
            using var request = new HttpRequestMessage(
                HttpMethod.Get,
                new Uri("/api/v1/auth/me", UriKind.Relative));
            request.Headers.TryAddWithoutValidation(ForwardedProtoHeader, "https");
            request.Headers.TryAddWithoutValidation(ForwardedForHeader, "198.51.100.25");

            using HttpResponseMessage response = await client.SendAsync(request);

            response.StatusCode.Should().Be(
                HttpStatusCode.Unauthorized,
                "the forwarded HTTPS request must reach authentication instead of being redirected");
            response.Headers.TryGetValues(StrictTransportSecurityHeader, out IEnumerable<string>? values)
                .Should().BeTrue("HSTS observes the normalized HTTPS scheme");
            values.Should().ContainSingle().Which.Should().Contain("max-age=31536000");
        }
    }

    /// <summary>Plain HTTP outside the health namespace redirects to the configured public TLS port.</summary>
    [Fact]
    public async Task PlainHttp_OutsideHealth_RedirectsPermanentlyToTls()
    {
        using (ApiTestFixture.OverrideEnvironment(PerimeterConfiguration()))
        {
            await using var host = new PerimeterHost();
            using HttpClient client = host.CreateClient(new WebApplicationFactoryClientOptions
            {
                AllowAutoRedirect = false,
                BaseAddress = new Uri("http://perimeter.example.test", UriKind.Absolute),
            });

            using HttpResponseMessage response = await client.GetAsync(
                new Uri("/api/v1/auth/me", UriKind.Relative));

            response.StatusCode.Should().Be(HttpStatusCode.PermanentRedirect);
            response.Headers.Location.Should().Be(
                new Uri("https://perimeter.example.test:4443/api/v1/auth/me", UriKind.Absolute));
        }
    }

    /// <summary>
    /// Forwarded client addresses are applied before the credential limiter chooses its partition.
    /// </summary>
    [Fact]
    public async Task ForwardedClientAddress_FromATrustedNetwork_PartitionsCredentialLimitsPerCaller()
    {
        Dictionary<string, string?> configuration = PerimeterConfiguration();
        configuration["Proxy__KnownNetworks__0"] = "0.0.0.0/0";
        configuration["Proxy__KnownNetworks__1"] = "::/0";
        configuration["RateLimiting__Authentication__PermitLimit"] = "1";
        configuration["RateLimiting__Authentication__WindowSeconds"] = "60";

        using (ApiTestFixture.OverrideEnvironment(configuration))
        {
            await using var host = new PerimeterHost();
            using HttpClient client = host.CreateClient(new WebApplicationFactoryClientOptions
            {
                AllowAutoRedirect = false,
                BaseAddress = new Uri("http://perimeter.example.test", UriKind.Absolute),
            });

            using HttpResponseMessage first = await SendUnknownLoginAsync(client, "198.51.100.25");
            using HttpResponseMessage exhausted = await SendUnknownLoginAsync(client, "198.51.100.25");
            using HttpResponseMessage otherCaller = await SendUnknownLoginAsync(client, "203.0.113.44");

            first.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
            exhausted.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
            otherCaller.StatusCode.Should().Be(
                HttpStatusCode.Unauthorized,
                "a different forwarded client address receives its own credential window");
        }
    }

    /// <summary>Builds the production perimeter configuration used by forwarded-header tests.</summary>
    private Dictionary<string, string?> PerimeterConfiguration() =>
        new(_fixture.HostConfiguration(), StringComparer.Ordinal)
        {
            ["Https__RedirectEnabled"] = "true",
            ["Https__RedirectPort"] = "4443",
        };

    /// <summary>Sends one deliberately failing sign-in from a named forwarded client address.</summary>
    private async Task<HttpResponseMessage> SendUnknownLoginAsync(HttpClient client, string forwardedFor)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            new Uri($"/api/v1/auth/login?portalId={_fixture.Seed.PortalId}", UriKind.Relative));
        request.Headers.TryAddWithoutValidation(ForwardedProtoHeader, "https");
        request.Headers.TryAddWithoutValidation(ForwardedForHeader, forwardedFor);
        request.Content = JsonContent.Create(
            new LoginRequest
            {
                Username = "forwarded-header-probe",
                Password = ApiTestFixture.KnownPassword,
            },
            options: ApiTestFixture.Json);

        return await client.SendAsync(request);
    }

    /// <summary>A production host built under the surrounding environment override.</summary>
    private sealed class PerimeterHost : WebApplicationFactory<Program>
    {
        /// <inheritdoc />
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            ArgumentNullException.ThrowIfNull(builder);
            builder.UseEnvironment("Production");
        }
    }

    /// <summary>Reads the member names a JSON object declares, in document order.</summary>
    /// <param name="root">The object to read.</param>
    /// <returns>The member names.</returns>
    private static IReadOnlyList<string> ReadMemberNames(JsonElement root) =>
        root.EnumerateObject().Select(property => property.Name).ToList();

    /// <summary>
    /// Reads every value a response carries for the correlation header.
    /// </summary>
    /// <param name="response">The response to read.</param>
    /// <returns>
    /// The values, which is empty when the header was absent - returned rather than thrown so that an absence
    /// is asserted as an absence instead of surfacing as an exception from this helper.
    /// </returns>
    /// <remarks>
    /// Every value is collected rather than only the first, because the count is itself part of the contract:
    /// the middleware assigns the header and must therefore produce exactly one. Header names are compared
    /// case-insensitively, matching how HTTP treats them.
    /// </remarks>
    private static IReadOnlyList<string> ReadCorrelationValues(HttpResponseMessage response) =>
        response.Headers
            .Where(header => string.Equals(
                header.Key,
                ApiTestFixture.CorrelationIdHeader,
                StringComparison.OrdinalIgnoreCase))
            .SelectMany(header => header.Value)
            .ToList();

    /// <summary>
    /// Asserts that a member holds a round-trippable ISO-8601 instant expressed in UTC.
    /// </summary>
    /// <param name="timestamp">The member to read.</param>
    /// <remarks>
    /// The offset is asserted as well as the parse, because a local-time stamp would parse perfectly and still
    /// be unusable: the readers of this document are a container probe and a monitor, neither of which knows
    /// the container's time zone, so an instant that is not anchored to UTC cannot be compared with anything.
    /// </remarks>
    private static void AssertRoundTrippableUtcInstant(JsonElement timestamp)
    {
        timestamp.ValueKind.Should().Be(
            JsonValueKind.String,
            "the timestamp travels as an ISO-8601 string rather than as a number of ticks");

        Func<DateTimeOffset> read = timestamp.GetDateTimeOffset;

        read.Should().NotThrow(
            "the timestamp must be a round-trippable ISO-8601 instant, because a monitor parses it without "
            + "knowing how it was formatted");
        read().Offset.Should().Be(
            TimeSpan.Zero,
            "the instant is anchored to UTC, so a reader that does not know the container's time zone can "
            + "still compare it");
    }

    /// <summary>
    /// When the dependency cannot be reached the READINESS view answers <c>503</c> with the same documented
    /// document, carrying the unhealthy aggregate word - while the liveness view still answers 200.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// <para>
    /// This is the branch the acceptance gate must never see on a sound deployment, and the branch an operator
    /// reads when a deployment is not sound - so it is asserted against a host of its own, whose connection
    /// string names an address nothing is listening on. Nothing about the application is altered to produce it:
    /// the probes fail for the reason a real outage makes them fail, and the writer's failure path is the one
    /// under examination.
    /// </para>
    /// <para>
    /// The three assembly- and clock-derived members are asserted here as well as on the healthy branch,
    /// because the unhealthy branch is exactly where an operator needs them: a report that cannot say WHICH
    /// service and WHEN is unusable for diagnosing an outage. The media type is asserted for the same reason
    /// it is asserted on the healthy branch and matters more here - a change that routed a failing health
    /// report through the problem-details edge would replace the documented members with an error envelope, and
    /// only the failing branch could reveal it.
    /// </para>
    /// <para>
    /// ADDRESSED AT THE READINESS VIEW, because that is the view the dependency probe belongs to. The liveness
    /// view runs no readiness-tagged probe at all, so an unreachable store cannot fail it - which is the whole
    /// reason the split exists, and is asserted here as the other half of this fact: the same host answers 200
    /// on the path the image's HEALTHCHECK and the compose condition read, and 503 on the path an orchestrator
    /// gates traffic with. A single view could not express both.
    /// </para>
    /// <para>
    /// The probe roll-call is NOT asserted on the body, because the body carries none: per-probe detail belongs
    /// to the operator log, and both views publish the same four documented members whatever their probes
    /// concluded.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Readiness_WhenADependencyIsUnreachable_AnswersTheDocumentedContractWith503()
    {
        using IDisposable environment = ApiTestFixture.OverrideEnvironment(UnreachableDependencyConfiguration());
        await using var host = new UnreachableDependencyHost();

        using HttpClient client = host.CreateClient();

        using HttpResponseMessage liveness = await client.GetAsync(HealthEndpoint);

        liveness.StatusCode.Should().Be(
            HttpStatusCode.OK,
            "the liveness view runs no readiness-tagged probe, so an unreachable store must not hold the "
            + "container unhealthy and must not hold the front-end service back behind service_healthy");

        using HttpResponseMessage response = await client.GetAsync(ReadinessEndpoint);
        string body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(
            HttpStatusCode.ServiceUnavailable,
            "a dependency that cannot be reached must be reported rather than absorbed, and 503 is the status "
            + "the container probe and the compose health condition both read as 'not ready'");

        response.Content.Headers.ContentType.Should().NotBeNull();
        response.Content.Headers.ContentType!.MediaType.Should().Be(
            JsonMediaType,
            "the failing branch publishes the same document as the healthy one");
        response.Content.Headers.ContentType.MediaType.Should().NotBe(
            ProblemMediaType,
            "a health report is not an RFC 7807 error envelope, and routing the failing branch through the "
            + "problem-details edge would replace every documented member");

        using JsonDocument document = JsonDocument.Parse(body);
        JsonElement root = document.RootElement;

        IReadOnlyList<string> members = ReadMemberNames(root);

        foreach (string documented in DocumentedMembers)
        {
            members.Should().Contain(
                documented,
                "the published contract names {0} on both branches",
                documented);
        }

        root.GetProperty("status").GetString().Should().Be(
            UnhealthyStatus,
            "a degraded aggregate maps to 200, so a 503 can only carry an unhealthy one");
        root.GetProperty("serviceName").GetString().Should().Be(
            ExpectedServiceName,
            "an outage report that cannot say which service it is about is unusable");
        root.GetProperty("version").GetString().Should().Be(ExpectedVersion);
        AssertRoundTrippableUtcInstant(root.GetProperty("timestamp"));

        members.Should().HaveCount(
            DocumentedMembers.Count,
            "the failing branch publishes the four documented members and no fifth - naming the probe that "
            + "failed to an anonymous caller is reconnaissance, and the roll-call is written to the operator "
            + "log instead");
    }

    /// <summary>
    /// The failing report discloses no connection detail: no server, no database, no credential and no
    /// provider exception text.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// <para>
    /// The healthy branch has the same obligation and a far easier time meeting it, because there is no
    /// exception to leak. THIS is where the obligation bites: a provider's message for an unreachable instance
    /// routinely carries the server name, the port, the database name and sometimes the login, and the endpoint
    /// is anonymous, so anything the writer copies into the document is published to whoever asks.
    /// </para>
    /// <para>
    /// The values searched for are the ones this host's connection string actually contains, so the assertion
    /// is against the real leak rather than a plausible-looking one, and the comparisons are case-insensitive
    /// because a leak differing only in casing is still a leak.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Readiness_WhenADependencyIsUnreachable_DisclosesNoConnectionDetail()
    {
        using IDisposable environment = ApiTestFixture.OverrideEnvironment(UnreachableDependencyConfiguration());
        await using var host = new UnreachableDependencyHost();

        using HttpClient client = host.CreateClient();

        using HttpResponseMessage response = await client.GetAsync(ReadinessEndpoint);
        string body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);

        body.Should().NotContainEquivalentOf(
            "password",
            "the endpoint is anonymous, so a credential in a failing report is published to anyone");
        body.Should().NotContainEquivalentOf(
            UnreachableDatabaseName,
            "the database name identifies the instance to an anonymous caller");
        body.Should().NotContainEquivalentOf(
            UnreachableHostAddress,
            "the server address and port identify the instance to an anonymous caller");
        body.Should().NotContainEquivalentOf(
            "exception",
            "the probe attaches its error to the result for private logging, and the writer must keep it off "
            + "the wire");
        body.Should().NotContainEquivalentOf(
            "sql server",
            "a provider's own failure text names the product, the instance and often the login");
    }

    /// <summary>
    /// Builds the fixture's own configuration with the connection string repointed at an address nothing is
    /// listening on.
    /// </summary>
    /// <returns>The environment the unreachable-dependency host reads.</returns>
    /// <remarks>
    /// Derived from the fixture's configuration so that the connection string is the ONLY difference from the
    /// shared host: a second difference would leave the 503 attributable to something other than the
    /// dependency. The connect timeout is one second, and the port is one the kernel has confirmed nothing is
    /// listening on (see <see cref="UnreachableHostAddress"/>), so the probes fail by refusal rather than by
    /// waiting - a long timeout here would make the fact slow without making it stronger.
    /// </remarks>
    private Dictionary<string, string?> UnreachableDependencyConfiguration() =>
        new(_fixture.HostConfiguration(), StringComparer.Ordinal)
        {
            ["ConnectionStrings__Default"] = string.Create(
                CultureInfo.InvariantCulture,
                $"Server={UnreachableHostAddress};Database={UnreachableDatabaseName};User Id=sa;"
                + $"Password=not-a-real-credential;Encrypt=False;TrustServerCertificate=True;"
                + $"Connect Timeout=1;Command Timeout=2"),
        };

    /// <summary>
    /// A host identical to the shared one except that its dependency cannot be reached.
    /// </summary>
    /// <remarks>
    /// The environment is supplied by the surrounding override rather than by this type, which is the pattern
    /// the rest of this assembly uses for an ad-hoc host: the factory reads process environment variables while
    /// it builds, so the override has to be in scope around its construction AND around the first request it
    /// serves.
    /// </remarks>
    /// <summary>A host of this suite's own whose log records reach the shared recording sink.</summary>
    /// <remarks>
    /// <para>
    /// NEEDED BECAUSE A SERILOG LOGGER IS PROCESS-WIDE. <c>Program</c> hands ownership of the static logger to
    /// the host it is building, so every additional host built in this process REPLACES the logger the shared
    /// fixture's host installed - configured from its own settings, without this suite's sink registration, and
    /// closed again when that host is disposed. Two facts here read the probe roll-call out of the operator
    /// log, and after any nested host had been built they observed no records at all: they passed when they ran
    /// first and failed when they did not, which is the worst way for a fact to be wrong.
    /// </para>
    /// <para>
    /// Building a host immediately before the request removes the ordering dependence entirely: the logger this
    /// host installs is the one in force for its own request, and it writes to the sink because this class
    /// registers it. The environment carries the shared fixture's own configuration, so it is the same
    /// composition answering - same database, same key, same log floors.
    /// </para>
    /// </remarks>
    private sealed class RecordingHost : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            ArgumentNullException.ThrowIfNull(builder);

            builder.UseEnvironment("Testing");
            builder.ConfigureServices(services =>
                services.AddSingleton<ILogEventSink>(RecordedLogs.Sink));
        }
    }

    private sealed class UnreachableDependencyHost : WebApplicationFactory<Program>
    {
        /// <inheritdoc />
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            ArgumentNullException.ThrowIfNull(builder);

            builder.UseEnvironment("Testing");
        }
    }
}
