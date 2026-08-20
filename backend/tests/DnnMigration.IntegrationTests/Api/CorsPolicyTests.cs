using System.Net;
using DnnMigration.Api.Extensions;
using FluentAssertions;
using Xunit;

namespace DnnMigration.IntegrationTests.Api;

/// <summary>
/// Covers the cross-origin contract the browser client depends on: which origin is permitted, what a
/// permitted origin is told it may send, what an unpermitted origin is told, and what is never granted.
/// </summary>
/// <remarks>
/// <para>
/// The fixture has always configured the Angular origin, and until now nothing sent an <c>Origin</c> header
/// or looked at a single cross-origin response header. That gap is not cosmetic: a policy that had been
/// widened to any origin, or narrowed to none, or that had quietly gained credential support, would have
/// been invisible.
/// </para>
/// <para>
/// The probe address is the anonymous health endpoint for the simple requests, because a cross-origin
/// permission must not depend on who is asking, and a protected route for the preflight facts, because that
/// is where a browser actually sends one - and because a preflight must be answered WITHOUT a credential,
/// which is asserted explicitly.
/// </para>
/// </remarks>
[Trait("Category", "Integration")]
[Collection(IntegrationTestCollection.Name)]
public sealed class CorsPolicyTests
{
    /// <summary>The anonymous endpoint used for the simple-request facts.</summary>
    private static readonly Uri HealthRoute = new("/health", UriKind.Relative);

    /// <summary>A protected collection, used for the preflight facts.</summary>
    private static readonly Uri PortalsRoute = new("/api/v1/portals", UriKind.Relative);

    /// <summary>An origin no configuration names.</summary>
    private const string UnconfiguredOrigin = "http://not-the-angular-client.example";

    /// <summary>The header declaring which request headers a response's content depends on.</summary>
    private const string VaryHeader = "Vary";

    /// <summary>The request header the cross-origin decision is a function of.</summary>
    private const string OriginHeaderName = "Origin";

    /// <summary>The header a permitted origin is echoed back in.</summary>
    private const string AllowOriginHeader = "Access-Control-Allow-Origin";

    /// <summary>The header naming the methods a preflight approves.</summary>
    private const string AllowMethodsHeader = "Access-Control-Allow-Methods";

    /// <summary>The header naming the request headers a preflight approves.</summary>
    private const string AllowHeadersHeader = "Access-Control-Allow-Headers";

    /// <summary>The header naming the response headers script may read.</summary>
    private const string ExposeHeadersHeader = "Access-Control-Expose-Headers";

    /// <summary>The header naming how long a preflight answer may be cached.</summary>
    private const string MaxAgeHeader = "Access-Control-Max-Age";

    /// <summary>The header that would grant credential support.</summary>
    private const string AllowCredentialsHeader = "Access-Control-Allow-Credentials";

    /// <summary>The correlation identifier header, which is the one exposed value.</summary>
    private const string CorrelationIdHeader = "X-Correlation-Id";

    private readonly ApiTestFixture _fixture;

    /// <summary>Initialises a new instance of the <see cref="CorsPolicyTests"/> class.</summary>
    /// <param name="fixture">The shared host and database.</param>
    public CorsPolicyTests(ApiTestFixture fixture) => _fixture = fixture;

    /// <summary>
    /// A request from the configured origin is answered with that origin echoed back exactly, and never
    /// with a wildcard.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// The exactness is the whole assertion. A wildcard would satisfy a browser just as well for an
    /// anonymous read, so a test that merely checked the header was present would pass against a policy
    /// that permits the entire internet.
    /// </remarks>
    [Fact]
    public async Task SimpleRequest_FromTheConfiguredOrigin_EchoesThatOriginExactly()
    {
        using HttpResponseMessage response = await SendAsync(HealthRoute, ApiTestFixture.AllowedOrigin);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        Values(response, AllowOriginHeader).Should().Equal([ApiTestFixture.AllowedOrigin]);
        Values(response, AllowOriginHeader).Should().NotContain(
            "*",
            "a wildcard would permit every origin, which is exactly what a named policy exists to avoid");
    }

    /// <summary>
    /// A request from an origin the configuration does not name is SERVED, and carries no permission
    /// header.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task SimpleRequest_FromAnUnconfiguredOrigin_IsServedWithoutAPermissionHeader()
    {
        using HttpResponseMessage response = await SendAsync(HealthRoute, UnconfiguredOrigin);

        response.StatusCode.Should().Be(
            HttpStatusCode.OK,
            "cross-origin permission is withheld from script by the browser, not refused by the server");

        response.Headers.Contains(AllowOriginHeader).Should().BeFalse(
            "an unpermitted origin must be told nothing, not told 'any origin'");
    }

    /// <summary>
    /// Every response declares that it may vary by the request's origin - including the ones that carry no
    /// permission header at all.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// WHY THE DECLARATION IS NEEDED. The cross-origin stage writes a permission header for a permitted
    /// origin and writes nothing for any other, which makes the response a function of a REQUEST HEADER.
    /// The framework's own stage announces that with <c>Vary: Origin</c> only when the policy names MORE
    /// THAN ONE origin - and this deployment names exactly one, so in practice no response carried the
    /// declaration at all.
    /// </remarks>
    [Fact]
    [Trait("Category", "Integration")]
    public async Task EveryResponse_DeclaresThatItVariesByOrigin()
    {
        using (HttpResponseMessage permitted = await SendAsync(HealthRoute, ApiTestFixture.AllowedOrigin))
        {
            Values(permitted, AllowOriginHeader).Should().Equal([ApiTestFixture.AllowedOrigin]);
            Values(permitted, VaryHeader).Should().Equal(
                [OriginHeaderName],
                "the permitted variant carries a permission header and must say so");
        }

        using (HttpResponseMessage refused = await SendAsync(HealthRoute, UnconfiguredOrigin))
        {
            refused.Headers.Contains(AllowOriginHeader).Should().BeFalse();
            Values(refused, VaryHeader).Should().Equal(
                [OriginHeaderName],
                "this variant differs from the permitted one precisely by the absent header");
        }

        using (HttpResponseMessage sameOrigin = await SendAsync(HealthRoute, origin: null))
        {
            Values(sameOrigin, VaryHeader).Should().Equal(
                [OriginHeaderName],
                "without the declaration here a cache may replay this variant to a cross-origin caller, "
                + "which is the case the declaration exists for");
        }

        using (HttpResponseMessage preflight = await PreflightAsync(
            PortalsRoute,
            ApiTestFixture.AllowedOrigin,
            requestedMethod: "GET",
            requestedHeaders: "authorization"))
        {
            Values(preflight, VaryHeader).Should().Equal([OriginHeaderName]);
        }
    }

    /// <summary>A request carrying no origin at all is answered with no cross-origin headers.</summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// This is the shape every other suite in this assembly sends, and it is worth pinning: the stage must
    /// be inert for a same-origin caller. A permission header emitted unconditionally would be a policy
    /// that depends on nothing the caller said, which is the same hazard as a wildcard wearing different
    /// clothes.
    /// </remarks>
    [Fact]
    public async Task SimpleRequest_WithNoOrigin_CarriesNoCrossOriginHeaders()
    {
        using HttpResponseMessage response = await SendAsync(HealthRoute, origin: null);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.Contains(AllowOriginHeader).Should().BeFalse();
        response.Headers.Contains(ExposeHeadersHeader).Should().BeFalse();
        response.Headers.Contains(AllowCredentialsHeader).Should().BeFalse();
    }

    /// <summary>
    /// The correlation identifier is exposed to script, and it is the only response header that is.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task SimpleRequest_FromTheConfiguredOrigin_ExposesTheCorrelationIdentifierAndNothingElse()
    {
        using HttpResponseMessage response = await SendAsync(HealthRoute, ApiTestFixture.AllowedOrigin);

        Values(response, ExposeHeadersHeader).Should().Equal([CorrelationIdHeader]);

        response.Headers.Contains(CorrelationIdHeader).Should().BeTrue(
            "exposing a header the response does not carry would expose nothing");
    }

    /// <summary>
    /// A preflight from the configured origin publishes the permitted methods, the permitted request
    /// headers and the cache lifetime.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// The three published lists are what a browser obeys, so each is asserted against the shipped policy
    /// rather than for mere presence.
    /// </remarks>
    [Fact]
    public async Task Preflight_FromTheConfiguredOrigin_PublishesTheMethodsHeadersAndLifetime()
    {
        using HttpResponseMessage response = await PreflightAsync(
            PortalsRoute,
            ApiTestFixture.AllowedOrigin,
            requestedMethod: "PUT",
            requestedHeaders: "authorization,content-type,x-correlation-id");

        Values(response, AllowOriginHeader).Should().Equal([ApiTestFixture.AllowedOrigin]);

        Values(response, AllowMethodsHeader).Should().BeEquivalentTo(
            ["GET", "POST", "PUT", "DELETE", "OPTIONS"],
            "these are the verbs the API answers, and a browser will send no other");

        Values(response, AllowHeadersHeader).Should().BeEquivalentTo(
            ["Authorization", "Content-Type", CorrelationIdHeader],
            "the bearer token, the request body's media type and the correlation identifier");

        Values(response, MaxAgeHeader).Should().Equal(
            ["600"],
            "ten minutes, stated in seconds");
    }

    /// <summary>A preflight is answered without any credential, and reaches no endpoint.</summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// A browser sends a preflight with no <c>Authorization</c> header, by specification, so a pipeline
    /// that authenticated it would answer <c>401</c> and the real request would never be sent - every
    /// authenticated cross-origin call would fail while every same-origin call kept working.
    /// </remarks>
    [Fact]
    public async Task Preflight_ForAProtectedRoute_IsApprovedWithoutACredential()
    {
        using HttpResponseMessage response = await PreflightAsync(
            PortalsRoute,
            ApiTestFixture.AllowedOrigin,
            requestedMethod: "GET",
            requestedHeaders: "authorization");

        response.StatusCode.Should().Be(
            HttpStatusCode.NoContent,
            "a preflight is answered by the cross-origin stage, ahead of authentication");

        Values(response, AllowOriginHeader).Should().Equal([ApiTestFixture.AllowedOrigin]);
    }

    /// <summary>A preflight from an unconfigured origin is approved for nothing.</summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// The absence of the method and header lists matters as much as the absence of the origin echo:
    /// publishing what WOULD be permitted to an origin that is not permitted tells an unpermitted caller
    /// the shape of the policy for nothing in return.
    /// </remarks>
    [Fact]
    public async Task Preflight_FromAnUnconfiguredOrigin_IsApprovedForNothing()
    {
        using HttpResponseMessage response = await PreflightAsync(
            PortalsRoute,
            UnconfiguredOrigin,
            requestedMethod: "GET",
            requestedHeaders: "authorization");

        response.Headers.Contains(AllowOriginHeader).Should().BeFalse();
        response.Headers.Contains(AllowMethodsHeader).Should().BeFalse();
        response.Headers.Contains(AllowHeadersHeader).Should().BeFalse();
        response.Headers.Contains(MaxAgeHeader).Should().BeFalse();
    }

    /// <summary>
    /// A preflight asking for a method or a request header the policy does not permit is answered with
    /// lists that EXCLUDE it, which is what makes the browser refuse to send the real request.
    /// </summary>
    /// <param name="requestedMethod">The method the browser says it intends to use.</param>
    /// <param name="requestedHeaders">The headers the browser says it intends to send.</param>
    /// <param name="excluded">The value that must not appear in the answer.</param>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// <b>The stage PUBLISHES the policy rather than adjudicating the request, and that is the contract
    /// being asserted.</b> Verified by execution: the answer to a preflight naming an unpermitted verb
    /// still carries the origin echo, the full permitted-method list, the full permitted-header list and
    /// the cache lifetime, with a no-content status.
    /// </remarks>
    [Theory]
    [InlineData("PATCH", "content-type", "PATCH")]
    [InlineData("TRACE", "content-type", "TRACE")]
    [InlineData("GET", "x-not-permitted", "x-not-permitted")]
    public async Task Preflight_AskingForUnpermittedMaterial_IsAnsweredWithListsThatExcludeIt(
        string requestedMethod,
        string requestedHeaders,
        string excluded)
    {
        using HttpResponseMessage response = await PreflightAsync(
            PortalsRoute,
            ApiTestFixture.AllowedOrigin,
            requestedMethod,
            requestedHeaders);

        IReadOnlyList<string> published =
            [.. Values(response, AllowMethodsHeader), .. Values(response, AllowHeadersHeader)];

        published.Should().NotContain(
            candidate => candidate.Equals(excluded, StringComparison.OrdinalIgnoreCase),
            "a published list that echoed what was asked for would permit anything a caller asked for");

        Values(response, AllowMethodsHeader).Should().BeEquivalentTo(
            ["GET", "POST", "PUT", "DELETE", "OPTIONS"],
            "the published method list is the policy's own and does not vary with the request");

        Values(response, AllowHeadersHeader).Should().BeEquivalentTo(
            ["Authorization", "Content-Type", CorrelationIdHeader],
            "the published header list is the policy's own and does not vary with the request");
    }

    /// <summary>Credential support is never granted, on either response shape.</summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// A decision rather than an omission, and asserted so that it stays one. This API authenticates with a
    /// bearer token on a request header, so there is no cookie for a cross-origin request to carry and
    /// nothing that credential support would enable.
    /// </remarks>
    [Fact]
    public async Task CredentialSupport_IsNeverGranted()
    {
        using HttpResponseMessage simple = await SendAsync(HealthRoute, ApiTestFixture.AllowedOrigin);

        simple.Headers.Contains(AllowCredentialsHeader).Should().BeFalse(
            "the API carries its credential on a request header, so ambient credentials are never wanted");

        using HttpResponseMessage preflight = await PreflightAsync(
            PortalsRoute,
            ApiTestFixture.AllowedOrigin,
            requestedMethod: "POST",
            requestedHeaders: "authorization,content-type");

        preflight.Headers.Contains(AllowCredentialsHeader).Should().BeFalse();
    }

    /// <summary>The policy the pipeline applies is the named one this API declares.</summary>
    /// <remarks>
    /// A named policy is what makes every one of the facts above about a single, greppable declaration.
    /// Pinning the name here means that a pipeline switched to a default policy - which would apply to
    /// every origin the default names, and would silently stop being the policy these facts measure -
    /// cannot pass unnoticed.
    /// </remarks>
    [Fact]
    public void ThePolicyIsNamedRatherThanDefaulted() =>
        CorsExtensions.PolicyName.Should().Be("DnnMigrationSpa");

    /// <summary>Sends a plain request, optionally stating an origin.</summary>
    /// <param name="route">The address to request.</param>
    /// <param name="origin">The origin to state, or <see langword="null"/> to state none.</param>
    /// <returns>The response.</returns>
    private async Task<HttpResponseMessage> SendAsync(Uri route, string? origin)
    {
        using HttpClient client = _fixture.CreateAnonymousClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, route);

        if (origin is not null)
        {
            request.Headers.Add("Origin", origin);
        }

        return await client.SendAsync(request);
    }

    /// <summary>Sends the preflight a browser would send before a cross-origin call.</summary>
    /// <param name="route">The address the real request would use.</param>
    /// <param name="origin">The origin making the call.</param>
    /// <param name="requestedMethod">The method the real request would use.</param>
    /// <param name="requestedHeaders">The headers the real request would carry.</param>
    /// <returns>The response.</returns>
    private async Task<HttpResponseMessage> PreflightAsync(
        Uri route,
        string origin,
        string requestedMethod,
        string requestedHeaders)
    {
        using HttpClient client = _fixture.CreateAnonymousClient();
        using var request = new HttpRequestMessage(HttpMethod.Options, route);

        request.Headers.Add("Origin", origin);
        request.Headers.Add("Access-Control-Request-Method", requestedMethod);
        request.Headers.Add("Access-Control-Request-Headers", requestedHeaders);

        return await client.SendAsync(request);
    }

    /// <summary>Reads a response header as a list of comma-separated values.</summary>
    /// <param name="response">The response to read.</param>
    /// <param name="name">The header name.</param>
    /// <returns>The values, trimmed, or an empty list when the header is absent.</returns>
    /// <remarks>
    /// The values are split here rather than asserted as one string, because the cross-origin headers are
    /// lists whose ORDER carries no meaning: an assertion against the joined text would fail on a
    /// reordering that no browser could distinguish.
    /// </remarks>
    private static IReadOnlyList<string> Values(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out IEnumerable<string>? values)
            ? values
                .SelectMany(value => value.Split(',', StringSplitOptions.RemoveEmptyEntries))
                .Select(value => value.Trim())
                .ToList()
            : [];
}
