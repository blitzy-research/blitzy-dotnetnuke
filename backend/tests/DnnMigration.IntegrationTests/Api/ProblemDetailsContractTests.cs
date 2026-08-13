using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DnnMigration.Api.ErrorHandling;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace DnnMigration.IntegrationTests.Api;

/// <summary>
/// Covers the single failure contract every endpoint publishes: one RFC 7807 body, one status vocabulary,
/// and no response that advertises a problem document and then answers without one.
/// </summary>
[Trait("Category", "Integration")]
[Collection(IntegrationTestCollection.Name)]
public sealed class ProblemDetailsContractTests
{
    /// <summary>An identifier no seeded role bears, so the read succeeds while finding nothing.</summary>
    /// <remarks>
    /// Chosen far above any identity seed rather than negative or zero, because <c>Roles.RoleID</c> is
    /// <c>IDENTITY(0,1)</c> in this schema and both zero and minus one are legitimate keys.
    /// </remarks>
    private const int UnknownRoleId = 987_654;

    /// <summary>The media type RFC 7807 section 3 registers for a problem document.</summary>
    private const string ProblemMediaType = "application/problem+json";

    /// <summary>
    /// The namespace every problem type in this API belongs to, built by one method from a failure code.
    /// </summary>
    private const string ProblemTypePrefix = "urn:dnnmigration:error:";

    private readonly ApiTestFixture _fixture;

    /// <summary>Initialises a new instance of the <see cref="ProblemDetailsContractTests"/> class.</summary>
    /// <param name="fixture">The shared host and database.</param>
    public ProblemDetailsContractTests(ApiTestFixture fixture) => _fixture = fixture;

    /// <summary>
    /// Every producer of a problem document in this API - middleware, routing, the formatter selector, the
    /// host and a controller alike - labels it with one media type and names it in one taxonomy.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// THE PRODUCERS ARE EXERCISED TOGETHER, deliberately. Each one is a different code path - a
    /// short-circuiting middleware, the router, the formatter selector, the model-validation filter and a
    /// controller action - and the property being asserted is that they AGREE. Testing them one at a time
    /// would let any two drift apart and still pass, which is exactly how the inconsistency arose.
    /// </remarks>
    [Fact]
    [Trait("Category", "Integration")]
    public async Task EveryProblemProducer_UsesOneMediaTypeAndOneTaxonomy()
    {
        using HttpClient anonymous = _fixture.CreateAnonymousClient();
        using HttpClient unprivileged = await _fixture.CreateUnprivilegedClientAsync();
        using HttpClient host = await _fixture.CreateHostClientAsync();

        List<(string Producer, HttpResponseMessage Response)> answers = [];

        try
        {
            // Authentication handler: no credential at all.
            answers.Add(("the authentication challenge", await anonymous.GetAsync(
                new Uri("/api/v1/portals?pageIndex=0&pageSize=10", UriKind.Relative))));

            // Authorisation result handler: authenticated, not entitled.
            answers.Add(("the authorisation refusal", await unprivileged.GetAsync(
                new Uri("/api/v1/portals?pageIndex=0&pageSize=10", UriKind.Relative))));

            // Controller result helper: a successful outcome establishing absence.
            answers.Add(("a controller refusal", await host.GetAsync(new Uri(
                FormattableString.Invariant($"/api/v1/roles/{UnknownRoleId}"),
                UriKind.Relative))));

            // Model validation: a bound value the declared rules refuse.
            answers.Add(("the validation filter", await host.GetAsync(
                new Uri("/api/v1/users?pageIndex=0&pageSize=99999", UriKind.Relative))));

            // Routing: a path matching no route.
            answers.Add(("routing, for an unmatched path", await host.GetAsync(
                new Uri("/api/v1/there-is-no-such-collection", UriKind.Relative))));

            // Routing: a path matching a route under a method it does not accept.
            answers.Add(("routing, for an unaccepted method", await host.PatchAsync(
                new Uri("/api/v1/portals", UriKind.Relative),
                content: null)));

            foreach ((string producer, HttpResponseMessage response) in answers)
            {
                ((int)response.StatusCode).Should().BeGreaterThanOrEqualTo(
                    400,
                    FormattableString.Invariant($"{producer} was expected to refuse the request"));

                response.Content.Headers.ContentType?.MediaType.Should().Be(
                    ProblemMediaType,
                    FormattableString.Invariant(
                        $"{producer} must label its problem document with the registered media type"));

                using JsonDocument body = JsonDocument.Parse(
                    await response.Content.ReadAsStringAsync());

                body.RootElement.TryGetProperty("type", out JsonElement type).Should().BeTrue(
                    FormattableString.Invariant($"{producer} must name the condition it refused for"));

                type.GetString().Should().StartWith(
                    ProblemTypePrefix,
                    FormattableString.Invariant(
                        $"{producer} must name it in this API's own taxonomy, not a third party's"));

                body.RootElement.TryGetProperty("status", out JsonElement status).Should().BeTrue();
                status.GetInt32().Should().Be((int)response.StatusCode);

                body.RootElement.TryGetProperty("title", out _).Should().BeTrue();
                body.RootElement.TryGetProperty("detail", out _).Should().BeTrue();
            }

            answers[1].Response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        }
        finally
        {
            foreach ((string _, HttpResponseMessage response) in answers)
            {
                response.Dispose();
            }
        }
    }

    /// <summary>
    /// A status decided by ROUTING carries the same problem document as every other refusal, and a method
    /// refusal still names the methods it would accept.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// These two statuses are decided before any endpoint is entered, and the router answers them with the
    /// status line alone - so a client with one parser for error responses had two cases it could not
    /// parse. The <c>Allow</c> header is asserted alongside the body because supplying a body must not cost
    /// the header: it is the only thing that tells the caller which method to use instead.
    /// </remarks>
    [Fact]
    [Trait("Category", "Integration")]
    public async Task RoutingDecidedRefusals_CarryTheProblemDocumentAndKeepTheirHeaders()
    {
        using HttpClient host = await _fixture.CreateHostClientAsync();

        using HttpResponseMessage unmatched = await host.GetAsync(
            new Uri("/api/v1/no-such-collection-exists", UriKind.Relative));

        unmatched.StatusCode.Should().Be(HttpStatusCode.NotFound);
        unmatched.Content.Headers.ContentLength.Should().BeGreaterThan(
            0,
            "an empty body is the defect this fact exists to prevent");

        using JsonDocument notFound = JsonDocument.Parse(await unmatched.Content.ReadAsStringAsync());
        notFound.RootElement.GetProperty("status").GetInt32().Should().Be(404);
        notFound.RootElement.GetProperty("type").GetString().Should().StartWith(ProblemTypePrefix);

        using HttpResponseMessage wrongMethod = await host.PatchAsync(
            new Uri("/api/v1/portals", UriKind.Relative),
            content: null);

        wrongMethod.StatusCode.Should().Be(HttpStatusCode.MethodNotAllowed);
        wrongMethod.Content.Headers.ContentLength.Should().BeGreaterThan(0);

        wrongMethod.Content.Headers.Allow.Should().NotBeEmpty(
            "the Allow header is the only thing that tells the caller which method to use, and adding a "
            + "body must not cost it");

        using JsonDocument notAllowed = JsonDocument.Parse(await wrongMethod.Content.ReadAsStringAsync());
        notAllowed.RootElement.GetProperty("status").GetInt32().Should().Be(405);
        notAllowed.RootElement.GetProperty("type").GetString().Should().StartWith(ProblemTypePrefix);

        // A successful response must be untouched by the stage that supplies these bodies: it acts only on a
        // 4xx or 5xx with no body of its own, and asserting that here is what keeps the blast radius visible.
        using HttpResponseMessage healthy = await _fixture.CreateAnonymousClient()
            .GetAsync(new Uri("/health", UriKind.Relative));

        ((int)healthy.StatusCode).Should().BeLessThan(400);
        healthy.Content.Headers.ContentType?.MediaType.Should().Be(
            "application/json",
            "the health document is not a problem document and must keep its own media type");
    }

    /// <summary>A request carrying no credential answers a problem document rather than an empty body.</summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task ProtectedEndpoint_WithoutCredentials_AnswersAProblemDocument()
    {
        using HttpClient client = _fixture.CreateAnonymousClient();

        using HttpResponseMessage response = await client.GetAsync(
            new Uri("/api/v1/portals?pageIndex=0&pageSize=10", UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        response.Headers.WwwAuthenticate.Should().NotBeEmpty(
            "the bearer challenge must survive the problem-details body being added");

        ProblemDetails? problem = await response.Content
            .ReadFromJsonAsync<ProblemDetails>(ApiTestFixture.Json);

        problem.Should().NotBeNull();
        problem!.Status.Should().Be(StatusCodes.Status401Unauthorized);
        problem.Title.Should().NotBeNullOrWhiteSpace();
        problem.Detail.Should().NotBeNullOrWhiteSpace();
        problem.Type.Should().NotBeNullOrWhiteSpace(
            "a client branches on the problem type rather than parsing prose");
    }

    /// <summary>A known caller who holds no grant answers a problem document rather than an empty body.</summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task ProtectedEndpoint_AsUnprivilegedCaller_AnswersAProblemDocument()
    {
        using HttpClient client = await _fixture.CreateUnprivilegedClientAsync();

        using HttpResponseMessage response = await client.GetAsync(
            new Uri("/api/v1/portals?pageIndex=0&pageSize=10", UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);

        ProblemDetails? problem = await response.Content
            .ReadFromJsonAsync<ProblemDetails>(ApiTestFixture.Json);

        problem.Should().NotBeNull();
        problem!.Status.Should().Be(StatusCodes.Status403Forbidden);
        problem.Title.Should().NotBeNullOrWhiteSpace();
        problem.Detail.Should().NotBeNullOrWhiteSpace();
        problem.Type.Should().NotBeNullOrWhiteSpace();
        problem.Detail.Should().NotContain(
            IntegrationSeed.MemberUserName,
            "a refusal must not echo the caller back to itself");
    }

    /// <summary>
    /// A problem document omits every standard member it has no value for, rather than writing it as null.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// This is the one place the failure contract and the success contract diverge, and the divergence has
    /// to be pinned because it is invisible in the source. The serializer is configured with
    /// <c>JsonIgnoreCondition.Never</c>, so every declared member of every response is written even when it
    /// holds null - which is why the client declares each nullable SUCCESS member as required-and-nullable.
    /// </remarks>
    [Fact]
    public async Task ProblemDocument_OmitsMembersItHasNoValueFor()
    {
        using HttpClient anonymous = _fixture.CreateAnonymousClient();

        using HttpResponseMessage refused = await anonymous.GetAsync(
            new Uri("/api/v1/portals?pageIndex=0&pageSize=10", UriKind.Relative));

        refused.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        await AssertOmitsInstanceAsync(refused);

        using HttpClient host = await _fixture.CreateHostClientAsync();

        // A negative page index is refused by the shared paging validator, so this failure is written by the
        // validation problem-details factory rather than by the authorisation result handler.
        using HttpResponseMessage invalid = await host.GetAsync(
            new Uri("/api/v1/portals?pageIndex=-1&pageSize=10", UriKind.Relative));

        invalid.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        await AssertOmitsInstanceAsync(invalid);
    }

    /// <summary>Asserts that one problem document carries no <c>instance</c> member at all.</summary>
    /// <param name="response">The refusal to inspect.</param>
    /// <returns>A task representing the assertion.</returns>
    private static async Task AssertOmitsInstanceAsync(HttpResponseMessage response)
    {
        string body = await response.Content.ReadAsStringAsync();

        using JsonDocument document = JsonDocument.Parse(body);
        JsonElement root = document.RootElement;

        root.TryGetProperty("type", out _).Should().BeTrue(
            "a client branches on the problem type, so the assigned members must still be written");
        root.TryGetProperty("title", out _).Should().BeTrue();
        root.TryGetProperty("status", out _).Should().BeTrue();

        root.TryGetProperty("instance", out _).Should().BeFalse(
            "nothing in this application assigns an occurrence reference, and the framework type's "
            + "per-member null-omission condition overrides the collection-wide write-everything policy, so "
            + "the member is absent rather than null - which is exactly what "
            + "frontend/src/app/core/models/problem-details.model.ts declares");
    }

    /// <summary>
    /// A read that succeeds while finding nothing answers a problem document rather than a bare 404.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task AbsentResource_FromASuccessfulOutcome_AnswersAProblemDocument()
    {
        using HttpClient client = await _fixture.CreateHostClientAsync();

        using HttpResponseMessage response = await client.GetAsync(new Uri(
            string.Format(
                CultureInfo.InvariantCulture,
                "/api/v1/roles/{0}",
                UnknownRoleId),
            UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);

        ProblemDetails? problem = await response.Content
            .ReadFromJsonAsync<ProblemDetails>(ApiTestFixture.Json);

        problem.Should().NotBeNull();
        problem!.Status.Should().Be(StatusCodes.Status404NotFound);
        problem.Title.Should().NotBeNullOrWhiteSpace();
        problem.Detail.Should().NotBeNullOrWhiteSpace();
        problem.Type.Should().NotBeNullOrWhiteSpace();
        problem.Detail.Should().NotContain(
            UnknownRoleId.ToString(CultureInfo.InvariantCulture),
            "an absence must not confirm which identifiers were probed");
    }

    /// <summary>A failure that names an internal fault is reported in the 5xx range, not as a bad request.</summary>
    /// <param name="failureCode">A failure code produced by the application or module boundary.</param>
    [Theory]
    [InlineData("portal.creation_failed")]
    [InlineData("role.create_failed")]
    [InlineData("module.content.export_failed")]
    [InlineData("module.content.import_failed")]
    [InlineData("module.export_failed")]
    [InlineData("module.controller.capability_probe_failed")]
    [InlineData("module.upgrade_failed")]
    public void InternalFailureCode_IsReportedAsAServerFault(string failureCode)
    {
        ApiResults.MapStatusCode(failureCode)
            .Should().Be(StatusCodes.Status500InternalServerError);
    }

    /// <summary>
    /// The narrower classifications still win, so promoting internal faults did not widen the 5xx range.
    /// </summary>
    /// <param name="failureCode">A representative failure code.</param>
    /// <param name="expectedStatus">The status the caller must receive.</param>
    /// <remarks>
    /// The refresh-token family is deliberately absent from this set.
    /// </remarks>
    [Theory]
    [InlineData("portal.not_found", StatusCodes.Status404NotFound)]
    [InlineData("role.duplicate", StatusCodes.Status409Conflict)]
    [InlineData("role_group.in_use", StatusCodes.Status409Conflict)]
    [InlineData("portal.host_fields_forbidden", StatusCodes.Status403Forbidden)]
    [InlineData("auth.invalid_credentials", StatusCodes.Status401Unauthorized)]
    [InlineData("membership.provider_error", StatusCodes.Status503ServiceUnavailable)]
    [InlineData("user.password_invalid", StatusCodes.Status400BadRequest)]
    [InlineData("module.content_type_mismatch", StatusCodes.Status400BadRequest)]
    public void CallerCorrectableCode_KeepsItsNarrowerStatus(string failureCode, int expectedStatus)
    {
        ApiResults.MapStatusCode(failureCode).Should().Be(expectedStatus);
    }

    /// <summary>
    /// Every reason code a lost uniqueness race is reported under is already a conflict in this vocabulary,
    /// so no create path had to invent a status and none can drift onto a different one.
    /// </summary>
    /// <param name="failureCode">A code emitted when a unique value turned out to be taken.</param>
    /// <remarks>
    /// Each of these codes was already emitted by a SEQUENTIAL pre-check and already answered 409; the fix
    /// made the concurrent path emit the very same codes rather than letting the store's refusal escape as
    /// a server fault.
    /// </remarks>
    [Theory]
    [Trait("Category", "Integration")]
    [InlineData("role.name_duplicate")]
    [InlineData("role_group.name_duplicate")]
    [InlineData("portal.alias_duplicate")]
    [InlineData("portal.administrator_duplicate")]
    [InlineData("portal.creation_conflict")]
    [InlineData("user.create.user-already-registered")]
    [InlineData("profile-definition.duplicate-name")]
    public void ALostUniquenessRace_IsAlwaysReportedAsAConflict(string failureCode)
    {
        ApiResults.MapStatusCode(failureCode).Should().Be(
            StatusCodes.Status409Conflict,
            "a unique value that turned out to be taken is a conflict with existing state, and the caller "
            + "resolves it by submitting a different value rather than by retrying or reporting an outage");
    }

    /// <summary>
    /// The two codes the credential compare-and-swap introduced are classified by the properties they have,
    /// not by where they were raised.
    /// </summary>
    /// <param name="failureCode">A code emitted by the credential write or the sign-in migration path.</param>
    /// <param name="expectedStatus">The status the caller must receive.</param>
    /// <remarks>
    /// Credential replacement became a compare-and-swap over the representation the caller last read, so
    /// "the write did not happen" now has a cause the boolean it replaced could not express, and each cause
    /// gets the status its own properties earn.
    /// </remarks>
    [Theory]
    [Trait("Category", "Integration")]
    [InlineData("user.password.superseded", StatusCodes.Status409Conflict)]
    [InlineData("auth.credential_migration_store_unavailable", StatusCodes.Status503ServiceUnavailable)]
    public void ACredentialCompareAndSwapOutcome_KeepsItsOwnStatus(string failureCode, int expectedStatus)
    {
        ApiResults.MapStatusCode(failureCode).Should().Be(expectedStatus);
    }

    /// <summary>
    /// The two module portability refusals sit on opposite sides of the caller-fault boundary, and the
    /// split is asserted together so neither can drift onto the other's status.
    /// </summary>
    /// <remarks>
    /// Pinned here because both codes are classified by TOKEN rather than by whole code, so a naming change
    /// upstream could silently move either one. The counterpart service-level facts assert that each is
    /// raised at all; these two assert what the caller then receives.
    /// </remarks>
    [Fact]
    public void ModulePortabilityRefusals_AreClassifiedByWhoCanCorrectThem()
    {
        ApiResults.MapStatusCode("module.content_type_mismatch")
            .Should().Be(StatusCodes.Status400BadRequest);
        ApiResults.MapStatusCode("module.export_failed")
            .Should().Be(StatusCodes.Status500InternalServerError);
    }

    /// <summary>
    /// Refusing to remove the only portal an installation has left is a conflict with current state, not a
    /// malformed request.
    /// </summary>
    /// <remarks>
    /// PINNED SEPARATELY BECAUSE THE STATUS CHANGED, and because two independent descriptions of this
    /// endpoint already asserted the answer this fact now guarantees.
    /// </remarks>
    [Fact]
    public void RefusingToRemoveTheLastPortal_IsAConflictRatherThanARequestCorrection()
    {
        ApiResults.MapStatusCode("portal.last_remaining")
            .Should().Be(StatusCodes.Status409Conflict);
    }

    /// <summary>
    /// A problem document publishes the correlation identifier the response header carries, under its own
    /// member name, and publishes the trace identifier separately.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task ProblemDocument_PublishesTheCorrelationIdentifierTheHeaderCarries()
    {
        string Supplied = ApiTestFixture.NewCorrelationId();

        using HttpClient client = _fixture.CreateAnonymousClient();
        using HttpRequestMessage request = new(HttpMethod.Get, new Uri("/api/v1/portals", UriKind.Relative));
        request.Headers.Add("X-Correlation-Id", Supplied);

        using HttpResponseMessage response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        response.Headers.GetValues("X-Correlation-Id").Single().Should().Be(
            Supplied,
            "the pipeline preserves an identifier the caller supplied");

        using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        JsonElement root = document.RootElement;

        root.TryGetProperty("correlationId", out JsonElement correlationId).Should().BeTrue(
            "a caller must be able to quote the support reference from the body they already have");
        correlationId.GetString().Should().Be(
            Supplied,
            "the body member and the response header must carry the SAME identifier");

        root.TryGetProperty("traceId", out JsonElement traceId).Should().BeTrue(
            "the framework's own member is kept, so nothing already reading it breaks");
        traceId.GetString().Should().NotBeNullOrWhiteSpace();
    }
}
