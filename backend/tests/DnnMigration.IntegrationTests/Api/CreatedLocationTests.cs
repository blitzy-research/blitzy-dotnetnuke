using DnnMigration.Api.ErrorHandling;
using DnnMigration.Application.Dtos.Common;
using DnnMigration.Domain.Common;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace DnnMigration.IntegrationTests.Api;

/// <summary>
/// Proves that the address a creation publishes in its <c>Location</c> header is the address the caller
/// actually posted to, including the path base a tenant addressed beneath a shared host name is rebased onto.
/// </summary>
/// <remarks>
/// <para>
/// THE DEFECT THIS GUARDS. A child portal is addressed by a path segment beneath a shared host, and
/// <c>TenantPathBaseMiddleware</c> moves that segment out of the routable path and into the path base before
/// routing runs - it has to, or <c>/child/api/v1/portals</c> matches no route at all. The shared creation
/// translator composed the location from the PATH alone, which under a rebased request is the PARENT's
/// address. Every creation made beneath a child tenant therefore answered <c>201</c> with a correct body and a
/// header naming a resource under the wrong tenant. The creating caller had no way to notice; only whoever
/// followed the header did, and what they got was a route reaching a resource the parent does not own.
/// </para>
/// <para>
/// MEASURED ON THE TRANSLATOR RATHER THAN OVER HTTP, and deliberately so. Seven creation endpoints across six
/// controllers publish their location through this one member, so the composition is a pure function of the
/// request's path base and path and is measured where it is made - which is also the only way to exercise the
/// encoding case, since a reserved character cannot be routed into a path base by a real request. The
/// end-to-end half is asserted separately in
/// <c>PortalApiTests.CreateBeneathAChildTenant_LocatesTheNewResourceAtTheChildsOwnAddress</c>, which posts a
/// genuine request beneath a genuine child portal's segment and then FOLLOWS the header it received. Neither
/// half substitutes for the other: this one covers every shape, that one covers the real pipeline.
/// </para>
/// <para>
/// The same reasoning and the same shape are used by <see cref="DuplicateKeyMappingTests"/> and
/// <see cref="TransportRefusalMappingTests"/> for the failure arms of the same translator.
/// </para>
/// </remarks>
[Trait("Category", "Integration")]
public sealed class CreatedLocationTests
{
    /// <summary>
    /// The location is the path base followed by the path followed by the new identifier, for a request with a
    /// path base and for one without.
    /// </summary>
    /// <param name="pathBase">The path base the request arrived with.</param>
    /// <param name="path">The routable path the request arrived with.</param>
    /// <param name="expected">The address the header must carry.</param>
    /// <remarks>
    /// <para>
    /// The bare-host row is the regression guard rather than the interesting case: the overwhelming majority
    /// of this API's traffic carries no path base, and a fix that composed one in would have prepended an
    /// empty segment or a stray separator to every location in the API. It must produce exactly what it
    /// produced before.
    /// </para>
    /// <para>
    /// The trailing-separator row exists because a collection address may legitimately arrive with one, and
    /// composing onto it unchanged would publish a doubled separator - an address that is not the member's.
    /// The row with a path base AND a trailing separator is the combination of the two, which is where a fix
    /// that trimmed only one half would fail.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData("", "/api/v1/portals", "/api/v1/portals/7")]
    [InlineData("", "/api/v1/portals/", "/api/v1/portals/7")]
    [InlineData("/child", "/api/v1/portals", "/child/api/v1/portals/7")]
    [InlineData("/child", "/api/v1/portals/", "/child/api/v1/portals/7")]
    [InlineData("/tenant-a/inner", "/api/v1/roles", "/tenant-a/inner/api/v1/roles/7")]
    public void TheLocation_IsTheAddressTheCallerPostedTo(string pathBase, string path, string expected)
    {
        CreatedResult created = Create(pathBase, path, identifier: 7);

        created.Location.Should().Be(
            expected,
            "the address a creation hands back is the one the caller reached, and under a rebased tenant the "
            + "path alone is the parent's address rather than the child's");
    }

    /// <summary>
    /// A path base carrying a character that must be escaped in a header reaches the caller escaped.
    /// </summary>
    /// <remarks>
    /// The request properties hold the DECODED path, so publishing them verbatim would put a raw space into a
    /// header. Taking both halves in their URI form is what makes the header a valid reference; an alias may
    /// legitimately carry a character requiring escape, and this is the only place the distinction between the
    /// decoded and the URI form is observable.
    /// </remarks>
    [Fact]
    public void AnEscapableCharacterInThePathBase_IsPublishedEscaped()
    {
        CreatedResult created = Create("/a child", "/api/v1/portals", identifier: 7);

        created.Location.Should().Be(
            "/a%20child/api/v1/portals/7",
            "the location is written into a header, so it carries the URI form rather than the decoded one");
    }

    /// <summary>
    /// A failed outcome publishes no location at all, whatever the request was addressed at.
    /// </summary>
    /// <remarks>
    /// Asserted because the composition now reads two request properties instead of one, and a translator that
    /// composed an address before deciding whether there was anything to locate would hand out a header for a
    /// resource that was never created.
    /// </remarks>
    [Fact]
    public void AFailedCreation_PublishesNoLocation()
    {
        ControllerBase controller = ControllerFor("/child", "/api/v1/portals");

        ActionResult<ApiResponse<StubDto>> outcome = controller.Created(
            Result<StubDto>.Failure("stub.refused", "The creation was refused."),
            created => created.Id);

        outcome.Result.Should().NotBeNull();
        outcome.Result.Should().NotBeOfType<CreatedResult>(
            "a refusal is a problem document, and a problem document locates nothing");
    }

    /// <summary>Runs the translator for a successful creation and returns the created result.</summary>
    /// <param name="pathBase">The path base the request arrived with.</param>
    /// <param name="path">The routable path the request arrived with.</param>
    /// <param name="identifier">The identifier the new resource carries.</param>
    /// <returns>The created result the translator produced.</returns>
    private static CreatedResult Create(string pathBase, string path, int identifier)
    {
        ControllerBase controller = ControllerFor(pathBase, path);

        ActionResult<ApiResponse<StubDto>> outcome = controller.Created(
            Result<StubDto>.Success(new StubDto { Id = identifier }),
            created => created.Id);

        return outcome.Result.Should().BeOfType<CreatedResult>().Subject;
    }

    /// <summary>Builds a controller whose request carries the supplied address.</summary>
    /// <param name="pathBase">The path base the request arrived with.</param>
    /// <param name="path">The routable path the request arrived with.</param>
    /// <returns>A controller bound to that request.</returns>
    private static ControllerBase ControllerFor(string pathBase, string path)
    {
        DefaultHttpContext httpContext = new();
        httpContext.Request.PathBase = new PathString(pathBase.Length == 0 ? null : pathBase);
        httpContext.Request.Path = new PathString(path);

        return new StubController
        {
            ControllerContext = new ControllerContext { HttpContext = httpContext },
        };
    }

    /// <summary>The representation a stub creation returns.</summary>
    private sealed class StubDto
    {
        /// <summary>Gets or sets the identifier the location is composed from.</summary>
        public int Id { get; set; }
    }

    /// <summary>
    /// A controller with no actions, existing only to give the translator a request to read.
    /// </summary>
    /// <remarks>
    /// Declared here rather than shared with the neighbouring mapping suites, which each declare their own
    /// doubles for the same reason: a shared test utility is one either suite could change under the other.
    /// </remarks>
    private sealed class StubController : ControllerBase
    {
    }
}
