using System.Net;
using System.Text.Json;
using FluentAssertions;
using Xunit;

namespace DnnMigration.IntegrationTests.Api;

/// <summary>
/// Covers the health endpoint.
/// </summary>
/// <remarks>
/// This endpoint carries an operational obligation that no other endpoint has: the container health check
/// probes it, and the front-end service declares <c>depends_on</c> with condition <c>service_healthy</c>.
/// If it ever required authentication the API container would never report healthy and the front-end
/// container would never start, so the anonymous assertion below is guarding a deployment behaviour rather
/// than a preference.
/// </remarks>
[Trait("Category", "Integration")]
[Collection(IntegrationTestCollection.Name)]
public sealed class HealthCheckTests
{
    private readonly ApiTestFixture _fixture;

    /// <summary>Initialises a new instance of the <see cref="HealthCheckTests"/> class.</summary>
    /// <param name="fixture">The shared host and database.</param>
    public HealthCheckTests(ApiTestFixture fixture) => _fixture = fixture;

    /// <summary>The endpoint answers without credentials, because the container probe sends none.</summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// The status is asserted exactly rather than as "healthy or unavailable". A 503 would be a legitimate
    /// answer from a deployment whose database is unreachable, but not from this one: the fixture provisions
    /// the database before the host starts and fails loudly if it cannot, so here a 503 would report a
    /// broken fixture and accepting it would hide that. What must never appear is a 401, a 403, a 404 or a
    /// redirect, and the exact assertion rules out all four at once.
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
    public async Task Health_WithoutCredentials_ReturnsOk()
    {
        using HttpClient client = _fixture.CreateAnonymousClient();

        using HttpResponseMessage response = await client.GetAsync(new Uri("/health", UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.Location.Should().BeNull(
            "the container probe follows nothing, so a redirect here would leave the API permanently "
            + "unhealthy and the front-end service would never start");
    }

    /// <summary>
    /// The database probe reports healthy, which also proves the test database was provisioned and that the
    /// connection-string override reached the host.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task Health_ReportsBothProbesHealthy()
    {
        using HttpClient client = _fixture.CreateAnonymousClient();

        using HttpResponseMessage response = await client.GetAsync(new Uri("/health", UriKind.Relative));
        string body = await response.Content.ReadAsStringAsync();

        using JsonDocument document = JsonDocument.Parse(body);
        JsonElement root = document.RootElement;

        root.GetProperty("status").GetString().Should().Be("Healthy");

        List<string> names = root.GetProperty("checks")
            .EnumerateArray()
            .Select(check => check.GetProperty("name").GetString() ?? string.Empty)
            .ToList();

        names.Should().Contain("database").And.Contain("sqlserver");

        foreach (JsonElement check in root.GetProperty("checks").EnumerateArray())
        {
            check.GetProperty("status").GetString().Should().Be("Healthy");
        }
    }

    /// <summary>
    /// The payload carries no diagnostic detail. The endpoint is anonymous, and the probe's own data
    /// dictionary names the server and the database, so the writer deliberately omits it.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task Health_DoesNotDiscloseConnectionDetail()
    {
        using HttpClient client = _fixture.CreateAnonymousClient();

        using HttpResponseMessage response = await client.GetAsync(new Uri("/health", UriKind.Relative));
        string body = await response.Content.ReadAsStringAsync();

        body.Should().NotContain("Password");
        body.Should().NotContain(_fixture.Database.DatabaseName);
        body.Should().NotContain("exception");
    }

    /// <summary>Every response carries a correlation identifier, including the anonymous ones.</summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task Health_CarriesCorrelationHeader()
    {
        using HttpClient client = _fixture.CreateAnonymousClient();

        using HttpResponseMessage response = await client.GetAsync(new Uri("/health", UriKind.Relative));

        response.Headers.TryGetValues("X-Correlation-Id", out IEnumerable<string>? values).Should().BeTrue();
        values.Should().NotBeNull();
        values!.Single().Should().NotBeNullOrWhiteSpace();
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
