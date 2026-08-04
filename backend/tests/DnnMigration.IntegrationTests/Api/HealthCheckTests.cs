using System.Net;
using System.Text.Json;
using FluentAssertions;
using Xunit;

namespace DnnMigration.IntegrationTests.Api;

/// <summary>
/// Pins the health endpoint's two obligations: that it answers an unauthenticated caller, and that what it
/// answers with is the document the operator documentation publishes.
/// </summary>
/// <remarks>
/// <para>
/// This endpoint carries an operational obligation no other endpoint has. The API image's own
/// <c>HEALTHCHECK</c> probes it, and <c>docker/docker-compose.yml</c> holds the front-end service back with
/// <c>depends_on</c> / <c>condition: service_healthy</c> until it answers. If it ever required
/// authentication, required HTTPS, or moved off <c>/health</c>, the API container would never report healthy
/// and the front-end container would never start - and both images would still build perfectly, so nothing
/// earlier in the pipeline would catch it. These assertions are the only automated guard against that.
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
/// runtime image ships wget and not curl. That is recorded here as the reason the endpoint must answer plain
/// HTTP with no redirect, and it is deliberately NOT asserted: invoking a probe binary from a test would
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

    /// <summary>The aggregate status word an unhealthy report carries.</summary>
    /// <remarks>
    /// A 503 can only be produced by this aggregate status. Both registered probes declare their failure
    /// status as unhealthy, and the framework maps a degraded aggregate to 200 rather than 503, so there is no
    /// route by which a 503 carries any other word.
    /// </remarks>
    private const string UnhealthyStatus = "Unhealthy";

    /// <summary>Name of the probe that opens a connection of its own.</summary>
    private const string DatabaseProbeName = "database";

    /// <summary>Name of the probe contributed by the SQL Server health-check package.</summary>
    private const string SqlServerProbeName = "sqlserver";

    /// <summary>The media type the document must be served as.</summary>
    private const string JsonMediaType = "application/json";

    /// <summary>
    /// The media type this document must never be served as, because it is not an error envelope.
    /// </summary>
    private const string ProblemMediaType = "application/problem+json";

    /// <summary>
    /// The four members the published operator documentation names, which no change may drop.
    /// </summary>
    /// <remarks>
    /// Presence is what is asserted, not exhaustiveness. The writer emits these four first and then adds
    /// diagnostic detail - a total duration and a per-probe array - and that detail is additive by design: a
    /// monitor reading only these four is served identically by the endpoint and by the documentation.
    /// Asserting an exact member count would therefore fail the moment a diagnostic member is added, which is
    /// a change this suite has no business forbidding.
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
    /// The permitted set is asserted as membership rather than as equality with 200, and that is a
    /// correctness decision rather than a concession. A 503 is what this endpoint is required to answer when
    /// a dependency is down: the database probe reports "not configured" and "unavailable" as results rather
    /// than raising, precisely so a dependency outage stays readable. A 503 is therefore a legitimate,
    /// correctly functioning answer, and it is emphatically not an authorisation problem - so failing this
    /// test on one would report a fault in the endpoint when the fault is in the dependency, and would say
    /// nothing at all about the property being guarded here. The four codes that must never appear - 401,
    /// 403, 404 and a redirect - are each ruled out on their own above, so tolerating a 503 costs none of
    /// that guard.
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
    [Fact]
    public async Task Health_WithoutCredentials_AnswersAnonymously()
    {
        using HttpClient client = _fixture.CreateAnonymousClient();

        // Stated rather than assumed. The suite has clients that carry a bearer token, and an assertion about
        // anonymous reachability made through one of those would pass while proving nothing.
        client.DefaultRequestHeaders.Authorization.Should().BeNull(
            "the container probe carries no credential, so this must be asserted through a client that has "
            + "none either");

        using HttpResponseMessage response = await client.GetAsync(HealthEndpoint);

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
            + "the compose health condition are both pinned to",
            HealthPath);
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

        response.StatusCode.Should().BeOneOf(
            new[] { HttpStatusCode.OK, HttpStatusCode.ServiceUnavailable },
            "those are the only two answers the endpoint may give: healthy, or a readable report that a "
            + "dependency is down");
    }

    /// <summary>
    /// The document carries the four members the published operator documentation names, served as plain
    /// JSON, with the two assembly-derived members correct whatever the dependency's condition.
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
    /// <c>serviceName</c>, <c>version</c> and <c>timestamp</c> are asserted on both branches because none of
    /// the three depends on the dependency's condition - the first two are read from the assembly and the
    /// third from the clock - so exempting them from the unhealthy branch would leave the case an operator
    /// most needs to read unasserted. Only the aggregate status word is branch-specific, and it is asserted
    /// exactly on both sides rather than merely "not healthy": 200 admits only a healthy aggregate because
    /// both probes declare unhealthy as their failure status, and 503 admits only an unhealthy one because a
    /// degraded aggregate maps to 200.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Health_AnswersWithTheDocumentedJsonContract()
    {
        using HttpClient client = _fixture.CreateAnonymousClient();

        using HttpResponseMessage response = await client.GetAsync(HealthEndpoint);
        string body = await response.Content.ReadAsStringAsync();

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

        foreach (string documented in DocumentedMembers)
        {
            members.Should().Contain(
                documented,
                "the published contract names {0}, and a monitor reading only the documented members must be "
                + "served identically by the endpoint and by the documentation",
                documented);
        }

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
            status.Should().Be(
                HealthyStatus,
                "both registered probes declare unhealthy as their failure status, so a 200 can only carry a "
                + "healthy aggregate");
        }
        else
        {
            status.Should().Be(
                UnhealthyStatus,
                "a degraded aggregate maps to 200, so a 503 can only carry an unhealthy one");
        }
    }

    /// <summary>
    /// The report names both registered probes individually, and reports each as healthy when the endpoint
    /// answers healthy.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// <para>
    /// Naming the probes is the whole reason the endpoint writes a document instead of leaving the framework's
    /// default writer to emit the status word alone. With two probes registered against the same database, an
    /// aggregate word tells an operator that something is wrong but not which of the two said so, and that
    /// distinction is the endpoint's entire diagnostic value.
    /// </para>
    /// <para>
    /// The probe names are asserted unconditionally because they are registration facts rather than health
    /// facts: a report lists every registered probe whatever each one concluded. Losing one from the array
    /// means a probe was unregistered, which is a silent reduction in coverage that no other assertion in this
    /// solution would notice. The per-probe verdicts are asserted only on the healthy branch, because on the
    /// unhealthy branch at least one of them is legitimately not healthy - that being what produced the 503.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Health_NamesEveryRegisteredProbe()
    {
        using HttpClient client = _fixture.CreateAnonymousClient();

        using HttpResponseMessage response = await client.GetAsync(HealthEndpoint);
        string body = await response.Content.ReadAsStringAsync();

        using JsonDocument document = JsonDocument.Parse(body);
        JsonElement root = document.RootElement;

        JsonElement checks = root.GetProperty("checks");
        checks.ValueKind.Should().Be(
            JsonValueKind.Array,
            "each probe is reported as its own entry so a partial failure identifies the probe that failed");

        IReadOnlyList<string> names = checks
            .EnumerateArray()
            .Select(check => check.GetProperty("name").GetString() ?? string.Empty)
            .ToList();

        names.Should().Contain(
            DatabaseProbeName,
            "the probe that opens a connection of its own must stay registered");
        names.Should().Contain(
            SqlServerProbeName,
            "the probe contributed by the health-check package must stay registered");

        if (response.StatusCode != HttpStatusCode.OK)
        {
            return;
        }

        foreach (JsonElement check in checks.EnumerateArray())
        {
            check.GetProperty("status").GetString().Should().Be(
                HealthyStatus,
                "a healthy aggregate cannot contain an entry that is not itself healthy");
        }
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
    /// database by design. The writer omits both deliberately and keeps only authored description text, and
    /// this asserts that the omission is still in place.
    /// </para>
    /// <para>
    /// The database name is taken from the fixture rather than written as a literal, because the provisioned
    /// name is generated per run - so a literal could only ever assert against a name no run actually used.
    /// The comparisons are case-insensitive, because a leak that differed only in casing would still be a
    /// leak.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Health_DisclosesNoConnectionDetail()
    {
        using HttpClient client = _fixture.CreateAnonymousClient();

        using HttpResponseMessage response = await client.GetAsync(HealthEndpoint);
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
    /// The supplied value is a single short printable-ASCII token on purpose: the middleware trusts an inbound
    /// header only when exactly one line is present and its content is printable US-ASCII within a length
    /// bound, so a value outside that shape would be replaced by a generated one and this test would be
    /// asserting the rejection path while appearing to assert the echo path.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Health_WithSuppliedCorrelationId_EchoesExactlyThatValue()
    {
        const string Supplied = "health-round-trip-6f2a1c";

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
    /// A caller-supplied correlation identifier is adopted and echoed back unchanged, rather than being
    /// replaced by one the server generated.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// This is the half of the contract the assertion above cannot reach. A middleware that ignored the
    /// inbound header and generated an identifier on every request would still satisfy "a response carries
    /// one", while breaking the only thing the header is for: following a single request across the client,
    /// the proxy and the API in one search. Asserting the echoed value equals the value sent is what
    /// distinguishes the two, and it is asserted through the anonymous client because correlation must not
    /// depend on being authenticated.
    /// </remarks>
    [Fact]
    public async Task Health_EchoesTheSuppliedCorrelationIdentifier()
    {
        const string Supplied = "health-round-trip-6f2a1c";

        using HttpClient client = _fixture.CreateAnonymousClient();
        using HttpRequestMessage request = ApiTestFixture.WithCorrelationId(
            new HttpRequestMessage(HttpMethod.Get, new Uri("/health", UriKind.Relative)),
            Supplied);

        using HttpResponseMessage response = await client.SendAsync(request);

        ApiTestFixture.ReadCorrelationId(response).Should().Be(
            Supplied,
            "the identifier a caller supplies is what every log line for that request must carry");
    }
}
