using System.Globalization;
using System.Net;
using System.Net.Http.Json;
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

    private readonly ApiTestFixture _fixture;

    /// <summary>Initialises a new instance of the <see cref="ProblemDetailsContractTests"/> class.</summary>
    /// <param name="fixture">The shared host and database.</param>
    public ProblemDetailsContractTests(ApiTestFixture fixture) => _fixture = fixture;

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
        using HttpClient client = _fixture.CreateClientFor(
            _fixture.Seed.MemberUserId,
            IntegrationSeed.MemberUserName,
            _fixture.Seed.PortalId,
            isSuperUser: false,
            roles: [IntegrationSeed.RegisteredUsersRoleName]);

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
        using HttpClient client = _fixture.CreateHostClient();

        using HttpResponseMessage response = await client.GetAsync(new Uri(
            string.Format(
                CultureInfo.InvariantCulture,
                "/api/v1/portals/{0}/roles/{1}",
                _fixture.Seed.PortalId,
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
    public void CallerCorrectableCode_KeepsItsNarrowerStatus(string failureCode, int expectedStatus)
    {
        ApiResults.MapStatusCode(failureCode).Should().Be(expectedStatus);
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
}
