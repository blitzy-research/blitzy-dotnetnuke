using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace DnnMigration.IntegrationTests.Api;

/// <summary>
/// Covers the endpoint a browser asks before it creates its router: whether the first segment of the address
/// it was served at names a configured child-portal alias.
/// </summary>
/// <remarks>
/// <para>
/// <strong>THE DEFECT THIS SUITE EXISTS FOR.</strong> The single-page application used to GUESS. Any leading
/// segment that merely had the SHAPE of an alias and was not in a hard-coded list of the application's own
/// top-level routes was claimed as a tenant prefix; the router's base href was set to it, so the router never
/// saw the address it had been given and the not-found view became unreachable. A mistyped one-segment
/// address therefore produced a sign-in form that could not succeed, at an address no portal owned, with
/// nothing on screen saying so and no way out but editing the address bar.
/// </para>
/// <para>
/// A shape test cannot close that, because a typo and a real child alias have the same shape. Only the alias
/// store can tell them apart, and only the server can read it - which is what this endpoint is for.
/// </para>
/// </remarks>
[Trait("Category", "Integration")]
[Collection(IntegrationTestCollection.Name)]
public sealed class TenantPathPrefixTests
{
    /// <summary>A host name that matches no alias row, and cannot, because it is reserved for examples.</summary>
    private const string UnconfiguredHost = "no-such-tenant.example";

    private readonly ApiTestFixture _fixture;

    /// <summary>Initialises a new instance of the <see cref="TenantPathPrefixTests"/> class.</summary>
    /// <param name="fixture">The shared host, database and seed.</param>
    public TenantPathPrefixTests(ApiTestFixture fixture) => _fixture = fixture;

    /// <summary>The address under test, with the segment being asked about.</summary>
    /// <param name="segment">The leading path segment, or <see langword="null"/> to ask about none.</param>
    /// <returns>The relative address to request.</returns>
    private static Uri Route(string? segment) => new(
        segment is null
            ? "/api/v1/tenancy/path-prefix"
            : $"/api/v1/tenancy/path-prefix?segment={Uri.EscapeDataString(segment)}",
        UriKind.Relative);

    /// <summary>
    /// An unrecognised segment is answered - not refused - and answered NO, which is what lets the client hand
    /// it to its router and reach the not-found view.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task AnUnknownSegment_IsAnsweredNegativelyRatherThanRefused()
    {
        using HttpClient client = _fixture.CreateAnonymousClient();

        using HttpResponseMessage response = await client.GetAsync(Route("no-such-screen"));

        response.StatusCode.Should().Be(
            HttpStatusCode.OK,
            "\"this is not a tenant\" is the ANSWER for a typo, not an error");
        response.Content.Headers.ContentType!.MediaType.Should().Be(
            "application/json",
            "every payload in this API is JSON, and an operation that omits the declaration advertises text/plain");

        TenantPathPrefixProjection? answered = await response.Content
            .ReadEnvelopeAsync<TenantPathPrefixProjection>();

        answered.Should().NotBeNull();
        answered!.IsTenantPath.Should().BeFalse();
        answered.Segment.Should().Be(
            "no-such-screen",
            "the segment is echoed so a client can prove the answer is about the question it asked");
    }

    /// <summary>A segment naming one of the application's own top-level routes is answered NO.</summary>
    /// <param name="reserved">The reserved segment to ask about.</param>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// These can never be alias rows, and answering them without consulting the store is what keeps an
    /// ordinary page load from turning into a database read.
    /// </remarks>
    [Theory]
    [InlineData("portals")]
    [InlineData("login")]
    [InlineData("api")]
    [InlineData("health")]
    public async Task AReservedSegment_IsAnsweredNegatively(string reserved)
    {
        using HttpClient client = _fixture.CreateAnonymousClient();

        using HttpResponseMessage response = await client.GetAsync(Route(reserved));

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        TenantPathPrefixProjection? answered = await response.Content
            .ReadEnvelopeAsync<TenantPathPrefixProjection>();

        answered!.IsTenantPath.Should().BeFalse();
    }

    /// <summary>A segment no alias could hold is answered rather than refused.</summary>
    /// <param name="malformed">The segment to ask about.</param>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// Answering rather than refusing is deliberate: a client blocked by getting the question slightly wrong
    /// is left in exactly the state the defect left it in.
    /// </remarks>
    [Theory]
    [InlineData("has a space")]
    [InlineData("has/two/segments")]
    [InlineData("dot.separated")]
    [InlineData("")]
    public async Task AMalformedSegment_IsAnsweredNegatively(string malformed)
    {
        using HttpClient client = _fixture.CreateAnonymousClient();

        using HttpResponseMessage response = await client.GetAsync(Route(malformed));

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        TenantPathPrefixProjection? answered = await response.Content
            .ReadEnvelopeAsync<TenantPathPrefixProjection>();

        answered!.IsTenantPath.Should().BeFalse();
    }

    /// <summary>Asking about no segment at all is answered rather than refused.</summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task NoSegmentAtAll_IsAnsweredNegatively()
    {
        using HttpClient client = _fixture.CreateAnonymousClient();

        using HttpResponseMessage response = await client.GetAsync(Route(null));

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        TenantPathPrefixProjection? answered = await response.Content
            .ReadEnvelopeAsync<TenantPathPrefixProjection>();

        answered!.IsTenantPath.Should().BeFalse();
        answered.Segment.Should().BeEmpty();
    }

    /// <summary>A segment that IS a configured child alias beneath this host is answered YES.</summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// The positive answer is what keeps the fix from being a blanket refusal: a real child-portal address
    /// must still be held aside as a prefix, or every such installation loses its addressing.
    /// </remarks>
    [Fact]
    public async Task AConfiguredChildAlias_IsAnsweredAffirmatively()
    {
        const string segment = "tenantpathprobe";

        int aliasId = await _fixture.Database.ScalarAsync<int>(
            """
            INSERT INTO [dbo].[PortalAlias] ([PortalID], [HTTPAlias]) VALUES (@portalId, @alias);
            SELECT CAST(SCOPE_IDENTITY() AS int);
            """,
            new Dictionary<string, object?>
            {
                ["@portalId"] = _fixture.Seed.PortalId,
                ["@alias"] = $"{ApiTestFixture.TestHost}/{segment}",
            });

        aliasId.Should().BeGreaterThan(0);

        try
        {
            using HttpClient client = _fixture.CreateAnonymousClient();

            using HttpResponseMessage response = await client.GetAsync(Route(segment));

            response.StatusCode.Should().Be(HttpStatusCode.OK);

            TenantPathPrefixProjection? answered = await response.Content
                .ReadEnvelopeAsync<TenantPathPrefixProjection>();

            answered!.IsTenantPath.Should().BeTrue(
                "a real child-portal address must still be held aside, or such an installation loses its addressing");
            answered.Segment.Should().Be(segment);
        }
        finally
        {
            // Removed whatever the assertions did, so this suite leaves the alias set as it found it and a
            // later suite reading the same rows cannot be decided by whether this one ran.
            await _fixture.Database.ExecuteAsync(
                "DELETE FROM [dbo].[PortalAlias] WHERE [PortalAliasID] = @aliasId;",
                new Dictionary<string, object?> { ["@aliasId"] = aliasId });
        }
    }

    /// <summary>
    /// THE REGRESSION TEST FOR THE MARK. The endpoint is served from a host that resolves to no portal, which
    /// is the exact circumstance it exists to describe.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// <para>
    /// ⚠ WITHOUT `TenantOptional` THIS ANSWERS 403 AND THE FIX IS WORTHLESS. The tenant-resolution stage
    /// refuses every endpoint that reaches it with no resolved tenant unless the endpoint opts out, and an
    /// unresolved tenant is not an error here - it is the question. This was a real omission, caught by the
    /// published-document contract rather than by reasoning, and it would have failed silently in exactly the
    /// misconfigured installation that most needs an answer.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task FromAnUnconfiguredHost_TheQuestionIsStillAnswered()
    {
        using HttpClient client = _fixture.CreateAnonymousClient();
        client.DefaultRequestHeaders.Host = UnconfiguredHost;

        using HttpResponseMessage response = await client.GetAsync(Route("no-such-screen"));

        response.StatusCode.Should().Be(
            HttpStatusCode.OK,
            "an address that names no tenant is the question this endpoint answers, not a reason to refuse it");

        TenantPathPrefixProjection? answered = await response.Content
            .ReadEnvelopeAsync<TenantPathPrefixProjection>();

        answered!.IsTenantPath.Should().BeFalse();
    }

    /// <summary>The endpoint discloses nothing about the portal beyond the boolean it was asked for.</summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// The whole defensibility of answering anonymously rests on this: whether a portal answers at an address
    /// is already observable by visiting it, but a portal's identity, name or settings are not.
    /// </remarks>
    [Fact]
    public async Task TheAnswer_CarriesNothingBeyondTheSegmentAndTheVerdict()
    {
        using HttpClient client = _fixture.CreateAnonymousClient();

        using HttpResponseMessage response = await client.GetAsync(Route("no-such-screen"));

        string body = await response.Content.ReadAsStringAsync();

        body.Should().NotContain(IntegrationSeed.PortalName, "no portal is named");
        body.Should().NotContain("portalId", "and none is identified");
        body.Should().NotContain("Alias", "nor is any stored alias echoed back");
    }

    /// <summary>The shape this endpoint publishes, read back independently of the production type.</summary>
    private sealed record TenantPathPrefixProjection
    {
        /// <summary>Gets the segment the question was asked about.</summary>
        public string Segment { get; init; } = string.Empty;

        /// <summary>Gets a value indicating whether that segment names a configured tenant.</summary>
        public bool IsTenantPath { get; init; }
    }
}
