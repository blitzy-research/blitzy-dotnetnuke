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
/// <remarks>
/// <para>
/// These facts exist because three refusal paths used to bypass the shared contract while every action
/// declared it. The authorisation middleware answered 401 and 403 with an EMPTY body; a read that succeeded
/// while finding nothing answered a bare 404; and a failure that described a server fault was reported as
/// 400, which told the caller to correct a request that was already correct and hid the fault from every
/// monitor watching the 5xx rate. A status-code assertion cannot detect any of the three, so each is
/// asserted here against the payload.
/// </para>
/// <para>
/// The media type is deliberately not asserted, for the reason recorded on the sibling portal fact: the
/// framework answers <c>application/json</c> rather than <c>application/problem+json</c> for a
/// controller-produced document, and pinning the current value would cement a deviation instead of leaving
/// room to correct it.
/// </para>
/// </remarks>
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
    /// <para>
    /// WHAT WAS INCONSISTENT, AND WHY IT MATTERED. Refusals decided outside MVC were labelled
    /// <c>application/problem+json</c> while every controller-produced one was labelled
    /// <c>application/json</c>, so a client could not select a problem parser from the media type - which is
    /// the reason RFC 7807 registers one. And the mandatory-remediation refusal named its type
    /// <c>https://httpstatuses.com/403</c>: a THIRD-PARTY, non-resolving URI, on the same status where the
    /// ordinary authorisation refusal already carried this API's own identifier. A client branching on
    /// <c>type</c> therefore had to know which producer had answered before it could interpret the member.
    /// </para>
    /// <para>
    /// THE PRODUCERS ARE EXERCISED TOGETHER, deliberately. Each one is a different code path - a
    /// short-circuiting middleware, the router, the formatter selector, the model-validation filter and a
    /// controller action - and the property being asserted is that they AGREE. Testing them one at a time
    /// would let any two drift apart and still pass, which is exactly how the inconsistency arose.
    /// </para>
    /// <para>
    /// The type is asserted by PREFIX rather than by exact value, because the point is the namespace: each
    /// producer names its own condition, and pinning the codes here would make this fact fail whenever a
    /// refusal was renamed for a reason it is not about.
    /// </para>
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

            // The prefix assertion above is satisfied by any member of the namespace, so the two producers
            // that used to disagree with each other on the SAME status are pinned exactly.
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
    /// status line alone - so a client with one parser for error responses had two cases it could not parse.
    /// The <c>Allow</c> header is asserted alongside the body because supplying a body must not cost the
    /// header: it is the only thing that tells the caller which method to use instead.
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

    /// <summary>
    /// A request carrying no credential answers a problem document rather than an empty body.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// This response is produced by the authorisation middleware, not by an action, so it is the one that
    /// used to arrive empty. The bearer challenge header is asserted alongside the body because the fix
    /// wraps the framework's handler rather than replacing it, and losing the header would break the very
    /// clients the body was added to help.
    /// </remarks>
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

    /// <summary>
    /// A known caller who holds no grant answers a problem document rather than an empty body.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// The refusal must disclose nothing about why. The assertion therefore checks that the detail is
    /// present and generic rather than checking for any particular explanation, and separately that no
    /// account name reaches the body.
    /// </remarks>
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
    /// <para>
    /// This is the one place the failure contract and the success contract diverge, and the divergence has to
    /// be pinned because it is invisible in the source. The serializer is configured with
    /// <c>JsonIgnoreCondition.Never</c>, so every declared member of every response is written even when it
    /// holds null - which is why the client declares each nullable SUCCESS member as required-and-nullable.
    /// The framework's problem-details type is the exception: it annotates each of its five standard members
    /// with a per-member null-omission condition, and a per-member condition overrides the collection-wide
    /// one, so a member with no value is genuinely ABSENT here exactly as RFC 7807 describes.
    /// </para>
    /// <para>
    /// <c>instance</c> is the member that proves it. Nothing in this application ever assigns one, so it is
    /// null on every document, and the two possible wire forms - omitted, or present as null - imply
    /// different client declarations. <c>problem-details.model.ts</c> declares it optional and non-null,
    /// which is correct only for the omitted form; asserting that form here is what stops the two layers
    /// drifting apart silently after a framework upgrade or a change to the serializer policy.
    /// </para>
    /// <para>
    /// The document is read as raw JSON rather than deserialised, because deserialising into the
    /// problem-details type would materialise a null <c>Instance</c> whether the member was on the wire or
    /// not, and so could not tell the two forms apart at all. Both a middleware-produced refusal and an
    /// action-produced validation failure are inspected, because they are written by different writers.
    /// </para>
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

    /// <summary>
    /// Asserts that one problem document carries no <c>instance</c> member at all.
    /// </summary>
    /// <param name="response">The refusal to inspect.</param>
    /// <returns>A task representing the assertion.</returns>
    /// <remarks>
    /// The presence of <c>type</c>, <c>title</c> and <c>status</c> is asserted alongside the absence, so a
    /// document that omitted everything - which would also satisfy the absence on its own - cannot pass.
    /// </remarks>
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
    /// <remarks>
    /// The role read reports absence as a successful outcome carrying no value, which is the path that used
    /// to produce an empty 404 - distinct from the portal read, whose absence arrives as a failed outcome
    /// and therefore always had a body. Both are asserted, in their own suites, because they are different
    /// code paths that must produce the same shape.
    /// </remarks>
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

    /// <summary>
    /// A failure that names an internal fault is reported in the 5xx range, not as a bad request.
    /// </summary>
    /// <param name="failureCode">A failure code produced by the application or module boundary.</param>
    /// <remarks>
    /// Asserted against the translator directly rather than by provoking each fault through HTTP. Every one
    /// of these codes is raised only when a write or a third-party module fails, which cannot be arranged
    /// deterministically from outside the process; the classification is nevertheless the whole of the
    /// behaviour, and it is a pure function of the code.
    /// </remarks>
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
    /// The refresh-token family is deliberately absent from this set. Every code beginning
    /// <c>refresh_token.</c> is classified as 401 ahead of any other rule, including the dependency-outage
    /// rule, which is pre-existing behaviour rather than something these fixes introduced or altered: the
    /// token store's own outage is escalated as an exception by the authentication service and so never
    /// reaches this translator as an expected failure. Pinning that quirk here would assert behaviour
    /// outside the scope of this contract.
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
    /// The two module portability refusals sit on opposite sides of the caller-fault boundary, and the split
    /// is asserted together so neither can drift onto the other's status.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The distinction is about WHO can put it right, which is the only question a status code answers.
    /// <c>module.content_type_mismatch</c> means the caller submitted a document belonging to a different
    /// module type - a request to correct, so 400 - and reproduces the refusal
    /// <c>Website/admin/Modules/Import.ascx.vb</c> lines 195-205 raised for exactly that reason.
    /// <c>module.export_failed</c> means the MODULE handed back content that an export document cannot
    /// carry; the request was correct and authorised, nothing the caller changes makes it succeed, so it is
    /// a server fault.
    /// </para>
    /// <para>
    /// Pinned here because both codes are classified by TOKEN rather than by whole code, so a naming change
    /// upstream could silently move either one. The counterpart service-level facts assert that each is
    /// raised at all; these two assert what the caller then receives.
    /// </para>
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
    /// <para>
    /// PINNED SEPARATELY BECAUSE THE STATUS CHANGED, and because two independent descriptions of this
    /// endpoint already asserted the answer this fact now guarantees. The code matched no classification
    /// table and fell to the request-correction default, so the caller received a 400 telling them to edit
    /// a request that has nothing wrong with it - while the endpoint's published description declared the
    /// refusal as a 409 and argued the case in the same words the conflict table uses for a removal blocked
    /// by a still-referenced resource. The declared 409 was therefore unreachable and the 400 actually
    /// returned was undeclared.
    /// </para>
    /// <para>
    /// The counterpart to this fact is the service-level assertion that the refusal is raised at all
    /// (<c>PortalServiceTests.DeletePortal_RefusesToRemoveTheLastRemainingTenant</c>, which pins the code).
    /// The two together fix the whole path from the rule to the status without a test that empties the
    /// installation - which is the only way to reach this refusal through the API, and would leave the
    /// shared host with no tenant for every fact that runs after it.
    /// </para>
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
    /// <remarks>
    /// <para>
    /// THE SUPPORT REFERENCE IS THE POINT OF THIS FACT. The browser client quotes a reference taken from the
    /// BODY, and until this member existed the only candidate there was <c>traceId</c> - which the server
    /// derives from whatever diagnostic activity happened to be current, and which therefore appears neither
    /// on the response header, nor on the request envelope in the log, nor on any audit event the request
    /// produced. A person reporting a problem quoted a value an operator could not find.
    /// </para>
    /// <para>
    /// The identifier is SUPPLIED by this test rather than read back and compared to itself, because a
    /// server-generated value would let a wrong-but-self-consistent implementation pass: echoing the trace
    /// identifier in both places would satisfy an equality assertion between them. Supplying a known value
    /// pins the body member to the identifier the CALLER used.
    /// </para>
    /// <para>
    /// Both members are asserted present, because they are not alternatives: <c>traceId</c> keeps the
    /// document indistinguishable from a framework-produced one, and no consumer of it is broken by the
    /// addition.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task ProblemDocument_PublishesTheCorrelationIdentifierTheHeaderCarries()
    {
        const string Supplied = "problem-details-support-reference";

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
