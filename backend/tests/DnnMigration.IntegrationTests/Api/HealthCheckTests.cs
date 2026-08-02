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
    [Fact]
    public async Task Health_WithoutCredentials_ReturnsOk()
    {
        using HttpClient client = _fixture.CreateAnonymousClient();

        using HttpResponseMessage response = await client.GetAsync(new Uri("/health", UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
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
}
