using System.Net;
using System.Net.Http.Headers;
using System.Text;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace DnnMigration.IntegrationTests.Api;

/// <summary>
/// Pins what the API says about the REPRESENTATION of a request: which media types it will answer in, and
/// what a body it could not read is allowed to disclose while saying so.
/// </summary>
/// <remarks>
/// <para>
/// Both concerns belong to the composed pipeline and neither is reachable from a unit test. Content
/// negotiation is settled by the framework from options set at registration; the text of a deserialisation
/// failure is authored by <c>System.Text.Json</c> and rewritten by a filter, and whether that filter is
/// actually reached for the parameter an action binds is a property of the host.
/// </para>
/// <para>
/// The disclosure cases exist because a QA run found four separate leaks in these payloads: a JSON path with
/// a line and byte offset, the fully-qualified CLR name of a request DTO, a nullable value type spelled as
/// <c>System.Nullable`1[System.Decimal]</c>, and error keys naming the framework's own parameter rather than
/// anything the caller sent. Each says something about how the server is built and nothing a caller can act
/// on.
/// </para>
/// </remarks>
[Trait("Category", "Integration")]
[Collection(IntegrationTestCollection.Name)]
public sealed class RequestRepresentationContractTests
{
    /// <summary>Fragments that describe the server's internals rather than the caller's request.</summary>
    /// <remarks>
    /// Applied to the WHOLE serialised document rather than to one member, because the payload has several
    /// places a fragment could surface - the summary detail, a field message, or an error key - and the
    /// requirement is that it appears in none of them.
    /// </remarks>
    private static readonly string[] InternalDisclosures =
    {
        "LineNumber",
        "BytePositionInLine",
        "Path: $",
        "System.Nullable",
        "System.Decimal",
        "System.Int32",
        "DnnMigration.Application",
        "System.Text.Json",
    };

    private readonly ApiTestFixture _fixture;

    /// <summary>Initialises a new instance of the <see cref="RequestRepresentationContractTests"/> class.</summary>
    /// <param name="fixture">The shared API host and seeded database.</param>
    public RequestRepresentationContractTests(ApiTestFixture fixture) => _fixture = fixture;

    /// <summary>
    /// A caller who will not accept the one representation this API produces is told so, rather than being
    /// sent JSON anyway.
    /// </summary>
    /// <param name="accept">A media type the API does not produce.</param>
    /// <remarks>
    /// Every controller declares <c>[Produces("application/json")]</c>, so the API states plainly that JSON is
    /// all it emits. Answering an <c>Accept</c> header it cannot satisfy with a 200 contradicts that
    /// declaration: a client that asked for XML because it can only parse XML receives a body it will fail on,
    /// and learns the reason at its own parser rather than from the response.
    /// </remarks>
    /// <returns>A task representing the assertion.</returns>
    [Theory]
    [InlineData("application/xml")]
    [InlineData("text/xml")]
    [InlineData("text/csv")]
    [InlineData("application/x-yaml")]
    public async Task AnUnsupportedAcceptHeader_IsAnsweredNotAcceptable(string accept)
    {
        // A HOST client, because the portal LISTING is host-scoped - a tenant administrator is answered 403
        // there, which would mask the negotiation result this case is about.
        using HttpClient client = await _fixture.CreateHostClientAsync();

        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/portals");
        request.Headers.Accept.Clear();
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(accept));

        using HttpResponseMessage response = await client.SendAsync(request);

        response.StatusCode.Should().Be(
            HttpStatusCode.NotAcceptable,
            "the API publishes exactly one representation and says so on every controller");
    }

    /// <summary>A caller who accepts JSON, or anything at all, is served normally.</summary>
    /// <param name="accept">A media type this API can satisfy.</param>
    /// <remarks>
    /// The companion guard. Turning on strict negotiation must not start refusing ordinary callers, and the
    /// wildcard forms are the ones every browser and most HTTP clients send by default - a regression there
    /// would take the whole API offline for them.
    /// </remarks>
    /// <returns>A task representing the assertion.</returns>
    [Theory]
    [InlineData("application/json")]
    [InlineData("*/*")]
    [InlineData("application/*")]
    public async Task AnAcceptableHeader_IsServedNormally(string accept)
    {
        // A HOST client, because the portal LISTING is host-scoped - a tenant administrator is answered 403
        // there, which would mask the negotiation result this case is about.
        using HttpClient client = await _fixture.CreateHostClientAsync();

        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/portals");
        request.Headers.Accept.Clear();
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(accept));

        using HttpResponseMessage response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    /// <summary>
    /// A body that is not valid JSON is refused with an explanation that names no parser internals.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task AMalformedBody_IsRefusedWithoutDisclosingParserInternals()
    {
        (HttpStatusCode status, string payload) = await PostRawAsync("{\"roleName\": \"Broken\"");

        status.Should().Be(HttpStatusCode.BadRequest);
        AssertNoInternalDisclosure(payload);
    }

    /// <summary>
    /// A member whose value is the wrong JSON type is refused without naming the CLR type expected.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    /// <remarks>
    /// The leak here was the most specific of the four: the payload spelled the target as
    /// <c>System.Nullable`1[System.Decimal]</c>, which names the language, the nullability strategy and the
    /// precise numeric type a column is mapped to.
    /// </remarks>
    [Fact]
    public async Task AWronglyTypedMember_IsRefusedWithoutNamingTheClrType()
    {
        (HttpStatusCode status, string payload) = await PostRawAsync(
            "{\"roleName\":\"Typed\",\"serviceFee\":\"not-a-number\"}");

        status.Should().Be(HttpStatusCode.BadRequest);
        AssertNoInternalDisclosure(payload);
    }

    /// <summary>
    /// An unknown member is refused without naming the request type the API binds it to.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    /// <remarks>
    /// Unknown members are refused deliberately - the serialiser is configured with
    /// <c>UnmappedMemberHandling.Disallow</c> so a caller misspelling a field is told rather than having it
    /// silently ignored. The refusal is right; naming
    /// <c>DnnMigration.Application.Dtos.Role.CreateRoleRequest</c> while giving it is not.
    /// </remarks>
    [Fact]
    public async Task AnUnknownMember_IsRefusedWithoutNamingTheRequestType()
    {
        (HttpStatusCode status, string payload) = await PostRawAsync(
            "{\"roleName\":\"Unknown\",\"notAField\":1}");

        status.Should().Be(HttpStatusCode.BadRequest);
        AssertNoInternalDisclosure(payload);
    }

    /// <summary>
    /// An absent body is refused under a key a caller can make sense of, not an empty one.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    /// <remarks>
    /// Two separate defects met on this request. The framework contributed an error under the EMPTY STRING as
    /// its key, which no client can present beside a field; and a second under <c>request</c>, which is the
    /// name of the action's own parameter and not of anything the caller sent - a client rendering errors
    /// beside their fields has no field called "request" to attach it to.
    /// </remarks>
    [Fact]
    public async Task AnAbsentBody_IsRefusedWithoutAnEmptyOrParameterNamedKey()
    {
        (HttpStatusCode status, string payload) = await PostRawAsync(string.Empty);

        status.Should().Be(HttpStatusCode.BadRequest);

        ValidationProblemDetails problem = Deserialise(payload);

        problem.Errors.Keys.Should().NotContain(
            string.Empty,
            "an error keyed by the empty string cannot be presented beside anything");
        problem.Errors.Keys.Should().NotContain(
            "request",
            "that is the action parameter's name, not a member of the document the caller sent");
        problem.Errors.Should().NotBeEmpty("the refusal must still say what was wrong");
    }

    /// <summary>
    /// ⚠ A GENUINE FIELD COMPLAINT IS STILL KEYED BY ITS FIELD, which is what the sanitiser must not disturb.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    /// <remarks>
    /// The guard that keeps the fix from over-reaching. The sanitiser distinguishes the deserialiser's failures
    /// from the application's STRUCTURALLY - the deserialiser keys by JSON path, so its keys begin with
    /// <c>$</c>, while a validator keys by member name - and this case proves the second kind survives intact.
    /// A rule that keyed on message wording instead would be at the mercy of a framework release; a rule that
    /// discarded too much would turn every per-field complaint into a bare sentence.
    /// </remarks>
    [Fact]
    public async Task AGenuineFieldComplaint_IsUnaffectedBySanitisation()
    {
        (HttpStatusCode status, string payload) = await PostRawAsync("{\"roleName\":\"\"}");

        status.Should().Be(HttpStatusCode.BadRequest);

        ValidationProblemDetails problem = Deserialise(payload);

        // The key is the CLR member name as the validator names it, which is how every other per-field refusal
        // in this API is keyed; the point of the case is that it is a member name at all rather than a JSON
        // path or the action's parameter.
        problem.Errors.Should().ContainKey(
            "RoleName",
            "a validator's complaint is keyed by the member it concerns and must reach the caller that way");
        AssertNoInternalDisclosure(payload);
    }

    /// <summary>Posts a raw body to the role collection and returns the status and serialised payload.</summary>
    /// <param name="body">The exact bytes to send, valid JSON or not.</param>
    /// <returns>The status code and the response body as text.</returns>
    /// <remarks>
    /// The content type is declared as JSON regardless of what the body actually is, because that is the shape
    /// of the defect: a client that believes it is sending JSON and is not. Sending a different content type
    /// would be answered 415 before the deserialiser ever ran, testing nothing.
    /// </remarks>
    private async Task<(HttpStatusCode Status, string Payload)> PostRawAsync(string body)
    {
        using HttpClient client = await _fixture.CreateAdministratorClientAsync();

        using var content = new StringContent(body, Encoding.UTF8, "application/json");
        using HttpResponseMessage response = await client.PostAsync("/api/v1/roles", content);

        return (response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    /// <summary>Asserts that a serialised payload names none of the server's internals.</summary>
    /// <param name="payload">The response body as text.</param>
    private static void AssertNoInternalDisclosure(string payload)
    {
        foreach (string fragment in InternalDisclosures)
        {
            payload.Should().NotContain(
                fragment,
                $"\"{fragment}\" describes how this server is built and nothing the caller can act on");
        }
    }

    /// <summary>Reads a validation problem document, failing the test when the body is not one.</summary>
    /// <param name="payload">The response body as text.</param>
    /// <returns>The deserialised document.</returns>
    private static ValidationProblemDetails Deserialise(string payload)
    {
        ValidationProblemDetails? problem = System.Text.Json.JsonSerializer
            .Deserialize<ValidationProblemDetails>(
                payload,
                new System.Text.Json.JsonSerializerOptions(
                    System.Text.Json.JsonSerializerDefaults.Web));

        problem.Should().NotBeNull("the refusal must be published as a problem document");

        return problem!;
    }
}
