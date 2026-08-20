using System.Net;
using System.Text.Json;
using FluentAssertions;
using Xunit;

namespace DnnMigration.IntegrationTests.Api;

/// <summary>
/// Covers the anonymous tenant-address read: the one operation that lets a browser tell a real child-portal
/// path segment from a mistyped console route before it commits to one.
/// </summary>
/// <remarks>
/// <para>
/// THE DEFECT THIS SUITE EXISTS TO PREVENT was measured through the container topology rather than deduced.
/// The single-page application derived its base address from the first path segment on shape alone, so a
/// one-character typo of the most-typed admin path - <c>/portls</c> for <c>/portals</c> - was adopted as a
/// tenant prefix. Every request beneath it then resolved no tenant and was refused, while the console
/// rendered its ordinary sign-in screen and reported the refusal as though the CREDENTIAL were wrong. The
/// address is the fault, and only the deployment's alias rows can say so.
/// </para>
/// <para>
/// THREE FACTS, AND THE THIRD IS THE ONE THE CLIENT ACTUALLY KEYS ON. A bare-host request is answered with
/// the empty prefix, a request beneath a stored segment is answered with that segment, and a request beneath
/// an unrecognised segment is REFUSED rather than answered - because an unrecognised segment is never moved
/// out of the routable path, so it matches no route at all. A client therefore reads a success as "this
/// segment names a tenant" and a refusal as "it does not", and the body confirms which segment.
/// </para>
/// </remarks>
[Trait("Category", "Integration")]
[Collection(IntegrationTestCollection.Name)]
public sealed class TenantAddressApiTests
{
    /// <summary>The operation under test, as the routes are written - without any tenant segment.</summary>
    private static readonly Uri BareRoute = new("/api/v1/tenant-address", UriKind.Relative);

    /// <summary>A host name that matches no alias row, and cannot: the TLD is reserved for examples.</summary>
    private const string UnconfiguredHost = "no-such-tenant.example";

    private readonly ApiTestFixture _fixture;

    /// <summary>Initialises a new instance of the <see cref="TenantAddressApiTests"/> class.</summary>
    /// <param name="fixture">The shared API host and its seeded database.</param>
    /// <exception cref="ArgumentNullException"><paramref name="fixture"/> is <see langword="null"/>.</exception>
    public TenantAddressApiTests(ApiTestFixture fixture)
    {
        ArgumentNullException.ThrowIfNull(fixture);

        _fixture = fixture;
    }

    /// <summary>
    /// A request to the bare host is answered anonymously with the EMPTY prefix, which is the ordinary
    /// single-tenant answer rather than a missing one.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// Anonymity is asserted by the absence of any credential on the client, not by inspecting metadata: a
    /// cold load is always unauthenticated, because this application holds its access token in memory only,
    /// so an operation that needed one could never answer the question it exists to answer.
    /// </remarks>
    [Fact]
    public async Task BareHost_AnsweredAnonymously_ReportsNoTenantPathPrefix()
    {
        using HttpClient client = _fixture.CreateAnonymousClient();

        using HttpResponseMessage response = await client.GetAsync(BareRoute);

        response.StatusCode.Should().Be(
            HttpStatusCode.OK,
            "the operation is the one anonymous read in this API, and a cold load carries no credential");

        (await ReadPathPrefixAsync(response)).Should().BeEmpty(
            "a bare-host alias addresses its tenant beneath no path segment, so there is no prefix to keep");
    }

    /// <summary>
    /// A request beneath a STORED path segment is answered with that segment, so a browser that guessed
    /// right keeps its prefix.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// The alias is attached to the SEEDED portal rather than to a newly created child, deliberately: the
    /// fact under test is which prefix is reported, and whose rows a rebased request reads is already pinned
    /// by <c>TenantResolutionTests</c>. Attaching it to a portal whose designations are known complete keeps
    /// this fact independent of portal creation.
    /// </remarks>
    [Fact]
    public async Task BeneathAStoredPathSegment_ReportsThatSegmentAsThePrefix()
    {
        string segment = "tenantaddr" + Guid.NewGuid().ToString("N")[..8];
        string alias = ApiTestFixture.TestHost + "/" + segment;

        await _fixture.Database.ExecuteAsync(
            """
            INSERT INTO [dbo].[PortalAlias] ([PortalID], [HTTPAlias]) VALUES (@portalId, @alias);
            """,
            new Dictionary<string, object?>
            {
                ["@portalId"] = _fixture.Seed.PortalId,
                ["@alias"] = alias,
            });

        try
        {
            using HttpClient client = _fixture.CreateAnonymousClient();

            using HttpResponseMessage response = await client.GetAsync(
                new Uri($"/{segment}/api/v1/tenant-address", UriKind.Relative));

            response.StatusCode.Should().Be(
                HttpStatusCode.OK,
                "a stored segment is moved into the path base before routing, so the route still matches");

            (await ReadPathPrefixAsync(response)).Should().Be(
                "/" + segment,
                "the reported prefix is the path portion of the alias the request resolved by, leading "
                    + "separator included, so a client can compose it without deciding where the separator goes");
        }
        finally
        {
            // Removed again, because the row is shared state: a listing fact that counts the seeded portal's
            // aliases must not depend on whether this test ran.
            await _fixture.Database.ExecuteAsync(
                """
                DELETE FROM [dbo].[PortalAlias] WHERE [HTTPAlias] = @alias;
                """,
                new Dictionary<string, object?> { ["@alias"] = alias });
        }
    }

    /// <summary>
    /// A request beneath an UNRECOGNISED segment is refused rather than answered, and that refusal is the
    /// signal a client reads as "this segment names no tenant".
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// ⚠ THE REFUSAL COMES FROM ROUTING AND THE AUTHORISATION FALLBACK, NOT FROM THIS OPERATION, and the
    /// distinction is what makes the signal trustworthy: the resolver fails closed with no bare-host
    /// fallback, so an unrecognised segment stays in the routable path, no route matches it, and the
    /// operation is never reached. A client that receives a 4xx here has learned that the address it was
    /// loaded from reaches nothing - which is exactly when it must leave its base address at the root and
    /// let the not-found view render.
    /// </remarks>
    [Fact]
    public async Task BeneathAnUnrecognisedSegment_IsRefusedRatherThanAnswered()
    {
        using HttpClient client = _fixture.CreateAnonymousClient();

        using HttpResponseMessage response = await client.GetAsync(
            new Uri("/portls/api/v1/tenant-address", UriKind.Relative));

        ((int)response.StatusCode).Should().BeGreaterThanOrEqualTo(
            400,
            "an unrecognised segment is never rebased, so nothing answers beneath it");
        ((int)response.StatusCode).Should().BeLessThan(
            500,
            "and the refusal must be a caller-correctable one: a client treats a server fault as "
                + "'cannot say' and keeps the prefix it was loaded with, so a 5xx here would make an "
                + "outage indistinguishable from a mistyped address");
    }

    /// <summary>
    /// A request whose HOST matches no alias at all is still answered, with the empty prefix, rather than
    /// refused as a tenant-dependent read.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// This is the fact behind the operation's tenant-optional mark. Reporting the ABSENCE of a tenant is the
    /// operation's whole purpose, so a request that resolves none must reach it - and an operator diagnosing
    /// an unconfigured deployment gets an answer rather than a refusal that says nothing.
    /// </remarks>
    [Fact]
    public async Task UnconfiguredHost_IsAnsweredWithNoPrefixRatherThanRefused()
    {
        using HttpClient client = _fixture.CreateAnonymousClient();
        client.DefaultRequestHeaders.Host = UnconfiguredHost;

        using HttpResponseMessage response = await client.GetAsync(BareRoute);

        response.StatusCode.Should().Be(
            HttpStatusCode.OK,
            "the operation is marked tenant-optional precisely so an unresolved address is answerable");

        (await ReadPathPrefixAsync(response)).Should().BeEmpty(
            "no tenant resolved, so there is no path segment to address one beneath");
    }

    /// <summary>
    /// The response carries the shared success envelope and nothing beyond the one member, so no tenant fact
    /// leaks to an unauthenticated caller.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// Asserted over the payload's OWN members rather than over the envelope, because the envelope is
    /// pinned for the whole API elsewhere. What matters here is that the anonymous answer names no portal,
    /// no portal name and no alias key - the operation reports the caller's own address back to it and
    /// nothing else.
    /// </remarks>
    [Fact]
    public async Task TheAnonymousAnswer_CarriesOnlyThePathPrefix()
    {
        using HttpClient client = _fixture.CreateAnonymousClient();

        using HttpResponseMessage response = await client.GetAsync(BareRoute);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        JsonElement payload = document.RootElement.GetProperty("data");

        payload.EnumerateObject().Select(member => member.Name).Should().Equal(
            ["pathPrefix"],
            "an anonymous read discloses the caller's own address and no other tenant fact");
    }

    /// <summary>Reads the reported prefix out of the success envelope.</summary>
    /// <param name="response">The answered response.</param>
    /// <returns>The reported prefix, which may legitimately be the empty string.</returns>
    private static async Task<string> ReadPathPrefixAsync(HttpResponseMessage response)
    {
        using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        return document.RootElement
            .GetProperty("data")
            .GetProperty("pathPrefix")
            .GetString() ?? string.Empty;
    }
}
