using System.Net;
using System.Net.Http.Json;
using System.Text;
using FluentAssertions;
using Xunit;

namespace DnnMigration.IntegrationTests.Api;

/// <summary>
/// Proves, through the real request pipeline, that a submission the stored domain cannot hold is answered
/// with a field-level refusal rather than a server fault.
/// </summary>
/// <remarks>
/// <para>
/// Every property here is asserted at this level because it cannot be reached any lower. An explicitly
/// null map exists only after a real deserialiser has run - a unit test constructing the request object
/// cannot produce the state a JSON body produces, since the property initialiser it would otherwise rely on
/// is exactly what the deserialiser overwrites. An unrepresentable page offset reaches a real provider,
/// which is the thing that faulted. And the status code a refusal carries is decided by the edge, not by
/// the validator.
/// </para>
/// <para>
/// The distinction each test draws is between 400 and 500, not merely between success and failure. Before
/// this work every one of these submissions was accepted by the edge and then faulted underneath it, so a
/// test asserting only that the request "failed" would have passed against the defect.
/// </para>
/// </remarks>
[Trait("Category", "Integration")]
[Collection(IntegrationTestCollection.Name)]
public sealed class RequestBoundTests
{
    private readonly ApiTestFixture _fixture;

    /// <summary>Initialises a new instance of the <see cref="RequestBoundTests"/> class.</summary>
    /// <param name="fixture">The shared host, database and seed.</param>
    public RequestBoundTests(ApiTestFixture fixture) => _fixture = fixture;

    /// <summary>
    /// A settings body whose map is explicitly null is refused with a validation problem rather than
    /// faulting.
    /// </summary>
    /// <param name="body">The raw JSON body under test.</param>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// This is the state no unit test can construct. Both map members are non-nullable reference types
    /// carrying an initialiser, so the only way to observe null on one of them is to let a deserialiser
    /// assign over that initialiser, which is precisely what a body carrying <c>null</c> does. The service
    /// answered this with an argument-null throw, so a syntactically valid body produced a 500.
    /// </remarks>
    [Theory]
    [InlineData("""{"moduleSettings":null,"tabModuleSettings":{}}""")]
    [InlineData("""{"moduleSettings":{},"tabModuleSettings":null}""")]
    [InlineData("""{"moduleSettings":null,"tabModuleSettings":null}""")]
    public async Task ModuleSettings_WithAnExplicitlyNullMap_AreRefusedRatherThanFaulting(string body)
    {
        using HttpClient client = await _fixture.CreateHostClientAsync();
        int moduleId = await SeededModuleIdAsync();

        using var content = new StringContent(body, Encoding.UTF8, "application/json");
        using HttpResponseMessage response = await client.PutAsync(
            new Uri(
                FormattableString.Invariant(
                    $"/api/v1/modules/{moduleId}/settings"),
                UriKind.Relative),
            content);

        response.StatusCode.Should().Be(
            HttpStatusCode.BadRequest,
            "an absent map is a caller's mistake, not a server fault");
    }

    /// <summary>
    /// A settings body carrying more entries than the two scopes permit between them is refused.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// Asserted over HTTP because the resource this bounds is the request itself: the body below is well
    /// inside the request size limit and would previously have become one transaction's worth of tracked
    /// entities and rows.
    /// </remarks>
    [Fact]
    public async Task ModuleSettings_CarryingMoreEntriesThanPermitted_AreRefused()
    {
        using HttpClient client = await _fixture.CreateHostClientAsync();
        int moduleId = await SeededModuleIdAsync();

        var moduleSettings = new Dictionary<string, string>(StringComparer.Ordinal);
        for (int index = 0; index < 260; index++)
        {
            moduleSettings[FormattableString.Invariant($"setting{index}")] = "value";
        }

        using HttpResponseMessage response = await client.PutAsJsonAsync(
            new Uri(
                FormattableString.Invariant(
                    $"/api/v1/modules/{moduleId}/settings"),
                UriKind.Relative),
            new { moduleSettings, tabModuleSettings = new Dictionary<string, string>() });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    /// <summary>
    /// A page update carrying a blank name is refused before it can materialise a blank path.
    /// </summary>
    /// <param name="tabName">The blank name under test.</param>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// The refusal is asserted at this level as well as in the service's own tests because the two
    /// mechanisms are different and both must hold: the validator answers at the edge with the field named,
    /// and the service refuses independently for any caller the validator does not front. A pass here proves
    /// the validator is actually wired to this route, which no unit test can establish.
    /// </remarks>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task PageUpdate_WithABlankName_IsRefused(string tabName)
    {
        using HttpClient client = await _fixture.CreateHostClientAsync();

        using HttpResponseMessage response = await client.PutAsJsonAsync(
            new Uri(
                FormattableString.Invariant($"/api/v1/tabs/{_fixture.Seed.RootTabId}"),
                UriKind.Relative),
            new { tabName, isVisible = true });

        response.StatusCode.Should().Be(
            HttpStatusCode.BadRequest,
            "a page cannot be stored without a name it can be addressed by");

        string payload = await response.Content.ReadAsStringAsync();
        payload.Should().NotBeEmpty("the refusal explains itself as an RFC 7807 problem");
    }

    /// <summary>
    /// A page update carrying a date the stored calendar cannot hold is refused rather than reaching the
    /// provider.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// The instant below is a perfectly ordinary CLR date and an impossible stored one, which is the exact
    /// gap this bound closes. Reaching the provider with it produced a fault naming no field.
    /// </remarks>
    [Fact]
    public async Task PageUpdate_WithADateOutsideTheStoredCalendar_IsRefused()
    {
        using HttpClient client = await _fixture.CreateHostClientAsync();

        using HttpResponseMessage response = await client.PutAsJsonAsync(
            new Uri(
                FormattableString.Invariant($"/api/v1/tabs/{_fixture.Seed.RootTabId}"),
                UriKind.Relative),
            new
            {
                tabName = "Home",
                isVisible = true,
                startDate = "1600-01-01T00:00:00Z",
            });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    /// <summary>
    /// A listing request whose page offset cannot be represented is refused rather than reaching a reader.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// Neither query value is individually out of range, so this is reachable only as a cross-field rule.
    /// Multiplied in 32-bit arithmetic the pair wraps to a negative offset, and a negative skip is rejected
    /// by the reader itself, so the caller previously received a server fault for a request that looked
    /// entirely ordinary.
    /// </remarks>
    [Fact]
    public async Task Listing_WithAnUnrepresentablePageOffset_IsRefused()
    {
        using HttpClient client = await _fixture.CreateHostClientAsync();

        using HttpResponseMessage response = await client.GetAsync(
            new Uri(
                "/api/v1/users?pageIndex=300000000&pageSize=100",
                UriKind.Relative));

        response.StatusCode.Should().Be(
            HttpStatusCode.BadRequest,
            "an unrepresentable offset is named as a field rather than faulting underneath");
    }

    /// <summary>
    /// An ordinary deep page of a listing is still served, so the offset rule refuses only the
    /// unrepresentable.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task Listing_WithARepresentableDeepOffset_IsServed()
    {
        using HttpClient client = await _fixture.CreateHostClientAsync();

        using HttpResponseMessage response = await client.GetAsync(
            new Uri(
                "/api/v1/users?pageIndex=1000&pageSize=100",
                UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    /// <summary>
    /// A role fee past what the currency column can hold is refused rather than faulting in the provider.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task RoleCreate_WithAFeePastTheCurrencyCeiling_IsRefused()
    {
        using HttpClient client = await _fixture.CreateHostClientAsync();

        using HttpResponseMessage response = await client.PostAsJsonAsync(
            new Uri(
                "/api/v1/roles",
                UriKind.Relative),
            new
            {
                roleName = "Unstorable Fee " + Guid.NewGuid().ToString("N")[..8],
                serviceFee = 999_999_999_999_999.9999m,
            });

        response.StatusCode.Should().Be(
            HttpStatusCode.BadRequest,
            "an amount the currency column cannot hold is named as a field");
    }

    /// <summary>
    /// Resolves a module belonging to the seeded tenant, creating one if the seed holds none.
    /// </summary>
    /// <returns>The module identifier.</returns>
    private async Task<int> SeededModuleIdAsync()
    {
        int existing = await _fixture.Database.ScalarAsync<int>(
            """
            SELECT COALESCE(MIN([ModuleID]), 0) FROM [dbo].[Modules] WHERE [PortalID] = @portalId;
            """,
            new Dictionary<string, object?> { ["portalId"] = _fixture.Seed.PortalId });

        if (existing > 0)
        {
            return existing;
        }

        return await _fixture.Database.ScalarAsync<int>(
            """
            INSERT INTO [dbo].[Modules] ([PortalID], [ModuleDefID], [ModuleTitle], [AllTabs], [IsDeleted])
            VALUES (@portalId, 1, N'Bound Probe', 0, 0);
            SELECT CAST(SCOPE_IDENTITY() AS int);
            """,
            new Dictionary<string, object?> { ["portalId"] = _fixture.Seed.PortalId });
    }
}
