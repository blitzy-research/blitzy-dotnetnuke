using System.Net;
using System.Text.Json;
using DnnMigration.Api.Extensions;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace DnnMigration.IntegrationTests.Api;

/// <summary>Pins who may reach the OpenAPI document and the interactive console, in each environment.</summary>
/// <remarks>
/// <para>
/// <strong>What is at stake.</strong> The console is an unauthenticated, machine-readable description of
/// every endpoint, every parameter and every error shape this application accepts. Published on a
/// production port it is a reconnaissance document written by the application about itself.
/// </para>
/// <para>
/// <strong>Why no existing test could reach it.</strong> The shared fixture runs as <c>Testing</c>, which
/// is neither development nor an opted-in production, so the console is unmounted for the whole suite and
/// every path below is dead code from its point of view.
/// </para>
/// </remarks>
[Trait("Category", "Integration")]
[Collection(IntegrationTestCollection.Name)]
public sealed class SwaggerPublicationTests
{
    /// <summary>The document address, spelled exactly as the console lists it.</summary>
    private const string DocumentPath = "/swagger/v1/swagger.json";

    /// <summary>The interactive console's own address under the same prefix.</summary>
    private const string ConsolePath = "/swagger/index.html";

    /// <summary>An address this application never serves, used as the reference refusal.</summary>
    /// <remarks>
    /// The gating's confidentiality claim is that an unpublished console is refused "on exactly the same
    /// terms as any address that matches no endpoint".
    /// </remarks>
    private const string NeverServedPath = "/there-is-no-endpoint-here-a3f91c";

    /// <summary>The configuration key a deployment sets to opt in, in its environment-variable form.</summary>
    private const string EnabledVariable = "Swagger__Enabled";

    /// <summary>The transport-enforcement key, switched off so it cannot mask the gating.</summary>
    private const string HttpsRedirectionVariable = "Https__RedirectEnabled";

    private readonly ApiTestFixture _fixture;

    /// <summary>Initialises a new instance of the <see cref="SwaggerPublicationTests"/> class.</summary>
    /// <param name="fixture">The shared host, used for its configuration and to mint a real bearer token.</param>
    public SwaggerPublicationTests(ApiTestFixture fixture) => _fixture = fixture;

    /// <summary>The opt-in key is spelled as the extension declares it.</summary>
    [Fact]
    public void TheOptInKey_IsTheOneTheExtensionReads()
    {
        SwaggerExtensions.EnabledSectionName.Should().Be("Swagger:Enabled");

        EnabledVariable.Should().Be(
            SwaggerExtensions.EnabledSectionName.Replace(":", "__", StringComparison.Ordinal),
            "a deployment sets the double-underscore form, so the two spellings must stay in step");
    }

    /// <summary>In development the document and the console are published without any opt-in.</summary>
    [Fact]
    public async Task InDevelopment_TheDocumentAndTheConsoleArePublished()
    {
        using IDisposable environment = ApiTestFixture.OverrideEnvironment(
            new Dictionary<string, string?>
            {
                // Not set at all, so this proves development needs no opt-in rather than inheriting one.
                [EnabledVariable] = null,
                [HttpsRedirectionVariable] = "false",
            });

        await using DocumentationHost host = new("Development");
        using HttpClient client = host.CreateClient();

        using HttpResponseMessage document = await client.GetAsync(new Uri(DocumentPath, UriKind.Relative));

        document.StatusCode.Should().Be(
            HttpStatusCode.OK,
            "development publishes the console unconditionally: the decision there is the environment name "
            + "and nothing else");

        string body = await document.Content.ReadAsStringAsync();

        using JsonDocument parsed = JsonDocument.Parse(body);

        parsed.RootElement.TryGetProperty("openapi", out JsonElement version).Should().BeTrue(
            "a 200 carrying anything other than an OpenAPI document would satisfy a status check and be "
            + "useless to a client");
        version.GetString().Should().StartWith("3.", "the specification version this generator emits");

        parsed.RootElement.TryGetProperty("paths", out JsonElement paths).Should().BeTrue();
        paths.EnumerateObject().Should().NotBeEmpty("a document describing no endpoint describes nothing");

        using HttpResponseMessage console = await client.GetAsync(new Uri(ConsolePath, UriKind.Relative));

        console.StatusCode.Should().Be(HttpStatusCode.OK);
        console.Content.Headers.ContentType!.MediaType.Should().Be(
            "text/html",
            "the interactive console is served as a document, not as the specification");
    }

    /// <summary>Outside development the default is off, for an authenticated caller as much as anyone.</summary>
    /// <remarks>
    /// The default is the setting that matters most, because it is the one every deployment gets without
    /// deciding anything.
    /// </remarks>
    [Fact]
    public async Task InProduction_WithNoOptIn_NeitherTheDocumentNorTheConsoleIsPublished()
    {
        string token = await MintHostTokenAsync();

        using IDisposable environment = ApiTestFixture.OverrideEnvironment(
            new Dictionary<string, string?>
            {
                [EnabledVariable] = null,
                [HttpsRedirectionVariable] = "false",
            });

        await using DocumentationHost host = new("Production");

        using HttpClient anonymous = host.CreateClient();
        using HttpClient authenticated = AuthenticatedClientFactory.Authenticate(host.CreateClient(), token);

        using HttpResponseMessage anonymousReference =
            await anonymous.GetAsync(new Uri(NeverServedPath, UriKind.Relative));
        using HttpResponseMessage authenticatedReference =
            await authenticated.GetAsync(new Uri(NeverServedPath, UriKind.Relative));

        foreach (string path in new[] { DocumentPath, ConsolePath })
        {
            using HttpResponseMessage anonymousResponse = await anonymous.GetAsync(new Uri(path, UriKind.Relative));
            using HttpResponseMessage authenticatedResponse =
                await authenticated.GetAsync(new Uri(path, UriKind.Relative));

            anonymousResponse.StatusCode.Should().NotBe(HttpStatusCode.OK);
            anonymousResponse.StatusCode.Should().Be(
                anonymousReference.StatusCode,
                "absent configuration leaves the console unpublished, and an unpublished address is refused "
                + "on exactly the same terms as one that was never served");

            authenticatedResponse.StatusCode.Should().NotBe(
                HttpStatusCode.OK,
                "authenticating is not the opt-in: a deployment that has not asked for the console must not "
                + "publish it to any caller");
            authenticatedResponse.StatusCode.Should().Be(authenticatedReference.StatusCode);
        }
    }

    /// <summary>A blank or unparsable opt-in leaves it off.</summary>
    [Theory]
    [InlineData("")]
    [InlineData("false")]
    [InlineData("FALSE")]
    public async Task InProduction_AValueThatIsNotConsent_LeavesTheConsoleUnpublished(string configured)
    {
        string token = await MintHostTokenAsync();

        using IDisposable environment = ApiTestFixture.OverrideEnvironment(
            new Dictionary<string, string?>
            {
                [EnabledVariable] = configured,
                [HttpsRedirectionVariable] = "false",
            });

        await using DocumentationHost host = new("Production");
        using HttpClient client = AuthenticatedClientFactory.Authenticate(host.CreateClient(), token);

        using HttpResponseMessage response = await client.GetAsync(new Uri(DocumentPath, UriKind.Relative));
        using HttpResponseMessage reference = await client.GetAsync(new Uri(NeverServedPath, UriKind.Relative));

        response.StatusCode.Should().NotBe(
            HttpStatusCode.OK,
            "anything other than a parsable true is not consent, and consent for this is not a thing to infer");
        response.StatusCode.Should().Be(reference.StatusCode);
    }

    /// <summary>A value that is neither true nor false stops the application from starting.</summary>
    /// <remarks>
    /// These are the values a deployment produces by ACCIDENT - a yes/no spelling, a numeric flag, a
    /// variable set to whitespace - and none of them is a boolean. The configuration binder refuses to
    /// convert them and the pipeline never finishes composing, so the process does not start and the
    /// message names the exact key and the offending value.
    /// </remarks>
    [Theory]
    [InlineData("   ")]
    [InlineData("yes")]
    [InlineData("no")]
    [InlineData("on")]
    [InlineData("1")]
    [InlineData("0")]
    public async Task InProduction_AValueThatIsNotABoolean_StopsTheApplicationFromStarting(string configured)
    {
        using IDisposable environment = ApiTestFixture.OverrideEnvironment(
            new Dictionary<string, string?>
            {
                [EnabledVariable] = configured,
                [HttpsRedirectionVariable] = "false",
            });

        await using DocumentationHost host = new("Production");

        Action start = () => host.CreateClient().Dispose();

        start.Should().Throw<InvalidOperationException>(
                "a security switch set to something that is not a boolean must be reported, not guessed at")
            .WithMessage(
                "*" + SwaggerExtensions.EnabledSectionName + "*",
                "the refusal names the exact key, so an operator does not have to search for which setting "
                + "was rejected");

        await Task.CompletedTask;
    }

    /// <summary>An opted-in production deployment publishes it to an authenticated caller.</summary>
    /// <remarks>
    /// The other half of the gate, and the half that would fail silently if the stage were placed before
    /// authentication: the gate would then see every caller as anonymous, the console would never appear,
    /// and a deployment that had explicitly opted in would look at a 404 and conclude the opt-in was
    /// broken.
    /// </remarks>
    [Theory]
    [InlineData("true")]
    [InlineData("True")]
    public async Task InProduction_WhenOptedIn_AnAuthenticatedCallerReachesTheDocument(string configured)
    {
        string token = await MintHostTokenAsync();

        using IDisposable environment = ApiTestFixture.OverrideEnvironment(
            new Dictionary<string, string?>
            {
                [EnabledVariable] = configured,
                [HttpsRedirectionVariable] = "false",
            });

        await using DocumentationHost host = new("Production");
        using HttpClient client = AuthenticatedClientFactory.Authenticate(host.CreateClient(), token);

        using HttpResponseMessage document = await client.GetAsync(new Uri(DocumentPath, UriKind.Relative));

        document.StatusCode.Should().Be(
            HttpStatusCode.OK,
            "the gate is placed after authentication so that it can read the caller: placed earlier it would "
            + "see everyone as anonymous and the opt-in would appear broken");

        using JsonDocument parsed = JsonDocument.Parse(await document.Content.ReadAsStringAsync());

        parsed.RootElement.TryGetProperty("paths", out JsonElement paths).Should().BeTrue();
        paths.EnumerateObject().Should().NotBeEmpty();

        using HttpResponseMessage console = await client.GetAsync(new Uri(ConsolePath, UriKind.Relative));

        console.StatusCode.Should().Be(
            HttpStatusCode.OK,
            "the console answers the request itself, so it is placed before authorisation - a fallback policy "
            + "there would refuse it before it was reached");
    }

    /// <summary>An opted-in production deployment still refuses an anonymous caller.</summary>
    [Fact]
    public async Task InProduction_WhenOptedIn_AnAnonymousCallerIsStillRefused()
    {
        string token = await MintHostTokenAsync();

        using IDisposable environment = ApiTestFixture.OverrideEnvironment(
            new Dictionary<string, string?>
            {
                [EnabledVariable] = "true",
                [HttpsRedirectionVariable] = "false",
            });

        await using DocumentationHost host = new("Production");

        using HttpClient anonymous = host.CreateClient();
        using HttpClient authenticated = AuthenticatedClientFactory.Authenticate(host.CreateClient(), token);

        using HttpResponseMessage refused = await anonymous.GetAsync(new Uri(DocumentPath, UriKind.Relative));
        using HttpResponseMessage served = await authenticated.GetAsync(new Uri(DocumentPath, UriKind.Relative));
        using HttpResponseMessage reference = await anonymous.GetAsync(new Uri(NeverServedPath, UriKind.Relative));

        served.StatusCode.Should().Be(
            HttpStatusCode.OK,
            "the opt-in is genuinely in force, so the anonymous refusal below is the gate rather than the "
            + "opt-in");

        refused.StatusCode.Should().NotBe(
            HttpStatusCode.OK,
            "authentication is the floor: a console reachable anonymously on a production port is a "
            + "machine-readable description of the whole API handed to anyone who asks");
        refused.StatusCode.Should().Be(
            reference.StatusCode,
            "the anonymous request passes straight through the gate and is refused downstream on exactly the "
            + "same terms as an address that matches no endpoint");
        refused.Headers.WwwAuthenticate.Select(header => header.ToString())
            .Should()
            .BeEquivalentTo(
                reference.Headers.WwwAuthenticate.Select(header => header.ToString()),
                "even the challenge headers must match the reference refusal, or their presence alone would "
                + "tell an anonymous caller that something exists at this address");
    }

    /// <summary>An anonymous caller is refused identically whether or not the console is published.</summary>
    /// <remarks>
    /// This is the indistinguishability claim stated as an assertion rather than as prose. If the two
    /// refusals differed in status, in content type or in body length, the difference alone would tell an
    /// unauthenticated caller that a console exists on this deployment - which is most of what the gate is
    /// protecting.
    /// </remarks>
    [Fact]
    public async Task InProduction_TheAnonymousRefusalDoesNotRevealWhetherTheConsoleExists()
    {
        HttpResponseMessage? whenOff = null;
        HttpResponseMessage? whenOn = null;

        try
        {
            using (ApiTestFixture.OverrideEnvironment(
                new Dictionary<string, string?>
                {
                    [EnabledVariable] = null,
                    [HttpsRedirectionVariable] = "false",
                }))
            {
                await using DocumentationHost host = new("Production");
                using HttpClient client = host.CreateClient();

                whenOff = await client.GetAsync(new Uri(DocumentPath, UriKind.Relative));
                _ = await whenOff.Content.ReadAsStringAsync();
            }

            using (ApiTestFixture.OverrideEnvironment(
                new Dictionary<string, string?>
                {
                    [EnabledVariable] = "true",
                    [HttpsRedirectionVariable] = "false",
                }))
            {
                await using DocumentationHost host = new("Production");
                using HttpClient client = host.CreateClient();

                whenOn = await client.GetAsync(new Uri(DocumentPath, UriKind.Relative));
                _ = await whenOn.Content.ReadAsStringAsync();
            }

            whenOn.StatusCode.Should().Be(
                whenOff.StatusCode,
                "a differing status would tell an unauthenticated caller that a console exists here, which is "
                + "most of what the gate is protecting");
            whenOn.Content.Headers.ContentType?.MediaType.Should().Be(
                whenOff.Content.Headers.ContentType?.MediaType,
                "a differing content type would be as revealing as a differing status");
            whenOn.Content.Headers.ContentLength.Should().Be(whenOff.Content.Headers.ContentLength);
        }
        finally
        {
            whenOff?.Dispose();
            whenOn?.Dispose();
        }
    }

    /// <summary>Mints a bearer token through the production sign-in endpoint of the shared host.</summary>
    /// <returns>The compact serialised access token.</returns>
    /// <remarks>
    /// Issued by the shared host rather than by the host under test, and valid on it: the signing secret,
    /// the issuer and the audience are all process-wide environment values that every host in this run
    /// composes from, so a token minted by one is accepted by another. Minting it through the sign-in
    /// endpoint rather than fabricating one means the claim set is the one production issues.
    /// </remarks>
    private Task<string> MintHostTokenAsync() =>
        AuthenticatedClientFactory.GetAccessTokenAsync(
            _fixture,
            IntegrationSeed.HostUserName,
            ApiTestFixture.KnownPassword);

    /// <summary>A host built under the surrounding environment override, in a named environment.</summary>
    /// <remarks>
    /// Built per fact rather than shared, because the publication decision is made while the pipeline is
    /// being composed: a host built outside the override would compose against whatever configuration was
    /// in force then, and the fact would assert nothing about the value it set.
    /// </remarks>
    private sealed class DocumentationHost : WebApplicationFactory<Program>
    {
        private readonly string _environmentName;

        /// <summary>Initialises a new instance of the <see cref="DocumentationHost"/> class.</summary>
        /// <param name="environmentName">The environment name the host runs under.</param>
        internal DocumentationHost(string environmentName) => _environmentName = environmentName;

        /// <inheritdoc />
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            ArgumentNullException.ThrowIfNull(builder);

            builder.UseEnvironment(_environmentName);
        }
    }
}
