using System.Reflection;
using DnnMigration.Api.ErrorHandling;
using DnnMigration.Application.Abstractions;
using DnnMigration.Domain.Common;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DnnMigration.IntegrationTests.Api;

/// <summary>
/// Pins the disclosure boundary of the problem-details edge: what a failed outcome may publish as its
/// <c>detail</c>, and what it must replace with a stand-in.
/// </summary>
/// <remarks>
/// A security review found that the module-lifecycle factory placed a third-party module's own
/// <c>Exception.Message</c> on its failed outcomes, and that this edge published it verbatim as the RFC
/// 7807 <c>detail</c> - so whatever connection string, file-system path, statement or content the module
/// had quoted travelled to an HTTP client.
/// </remarks>
[Trait("Category", "Integration")]
[Collection(IntegrationTestCollection.Name)]
public sealed class ProblemDetailDisclosureTests
{
    /// <summary>The longest detail the edge will publish, restated as this suite's own expectation.</summary>
    private const int PublishedDetailBound = 512;

    private readonly ApiTestFixture _fixture;

    /// <summary>Initialises a new instance of the <see cref="ProblemDetailDisclosureTests"/> class.</summary>
    /// <param name="fixture">The shared composed host, used to obtain the registered factory.</param>
    public ProblemDetailDisclosureTests(ApiTestFixture fixture) => _fixture = fixture;

    /// <summary>A short, single-line, authored explanation is published unchanged.</summary>
    /// <remarks>
    /// The guard must not become a redactor of legitimate messages. Every explanation this solution authors
    /// is one short sentence, and a caller depends on receiving it: "the current credential is not correct"
    /// and "the page index must not be negative" are the difference between an actionable response and a
    /// blank refusal.
    /// </remarks>
    [Theory]
    [InlineData("The current credential is not correct.")]
    [InlineData("The page index must not be negative.")]
    [InlineData("Account 7 is locked and must be unlocked by an administrator.")]
    public void AnAuthoredExplanation_IsPublishedUnchanged(string authored)
    {
        Detail(Result.Failure("some.code", authored)).Should().Be(authored);
    }

    /// <summary>
    /// A multi-line message is refused, because that is the shape of an exception's text rather than of an
    /// authored sentence.
    /// </summary>
    [Theory]
    [InlineData("System.Data.SqlClient.SqlException: Login failed.\n   at Foo.Bar()")]
    [InlineData("Cannot open database \"Dnn\" requested by the login.\r\nThe login failed.")]
    [InlineData("outer\n---> inner")]
    public void AMultiLineMessage_IsReplacedByTheStandIn(string leaked)
    {
        string published = Detail(Result.Failure("some.code", leaked));

        published.Should().NotBe(leaked);
        published.Should().NotContain("SqlException");
        published.Should().NotContain("Dnn");
        published.Should().Contain("could not be completed");
    }

    /// <summary>A message longer than the published bound is refused rather than truncated.</summary>
    [Fact]
    public void AnOverlongMessage_IsReplacedRatherThanTruncated()
    {
        string leaked = new('x', PublishedDetailBound + 1);

        string published = Detail(Result.Failure("some.code", leaked));

        published.Should().NotBe(leaked);
        published.Should().NotStartWith("xxxx");
        published.Should().Contain("could not be completed");
    }

    /// <summary>A message naming a CLR exception type is refused, even though it is short and single-line.</summary>
    /// <param name="leaked">A one-sentence message that ends in a CLR type name.</param>
    /// <remarks>
    /// THE SHAPE THE FIRST TWO REFUSALS DID NOT CATCH. A service caught a credential-store failure and
    /// appended <c>exception.GetType().Name</c> to the message on its failed outcome, so that a caller
    /// could report the underlying cause.
    /// </remarks>
    [Theory]
    [InlineData("The credential store could not be written: TimeoutException.")]
    [InlineData("SqlException")]
    [InlineData("The operation failed with DbUpdateConcurrencyException, please retry.")]
    [InlineData("Creation failed (InvalidOperationException)")]
    public void AMessageNamingAnExceptionType_IsReplacedByTheStandIn(string leaked)
    {
        string published = Detail(Result.Failure("some.code", leaked));

        published.Should().NotBe(leaked);
        published.Should().NotContain("Exception", "the refused shape must not survive in the stand-in");
        published.Should().Contain("could not be completed");
    }

    /// <summary>
    /// The refusal is a word test, so an authored sentence that merely uses the word "exception" in prose
    /// is published unchanged.
    /// </summary>
    /// <param name="authored">An authored explanation containing the word in ordinary prose.</param>
    [Theory]
    [InlineData("No exception was made for this account.")]
    [InlineData("This request is an exception to the usual quota.")]
    [InlineData("Exceptional circumstances apply to this tenant.")]
    public void AnAuthoredSentenceUsingTheWordInProse_IsPublishedUnchanged(string authored)
    {
        Detail(Result.Failure("some.code", authored)).Should().Be(authored);
    }

    /// <summary>
    /// No member of the module-lifecycle factory that turns an exception into text reproduces that
    /// exception's message.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task NoFactoryMemberTurningAnExceptionIntoText_ReproducesItsMessage()
    {
        const string OuterSentinel = "OUTER-SENTINEL-connection-string-and-payload";
        const string InnerSentinel = "INNER-SENTINEL-sql-statement-and-path";

        Type? factory = typeof(DnnMigration.Infrastructure.DependencyInjection).Assembly
            .GetType("DnnMigration.Infrastructure.Services.ModuleBusinessControllerFactory");

        factory.Should().NotBeNull("the factory under discussion must still exist");

        MethodInfo[] exceptionToText = factory!
            .GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
            .Where(method => method.ReturnType == typeof(string))
            .Where(method => method.GetParameters().Length == 1)
            .Where(method => typeof(Exception).IsAssignableFrom(method.GetParameters()[0].ParameterType))
            .ToArray();

        exceptionToText.Should().NotBeEmpty(
            "the redacting describer is expected to exist; an empty set means this test has stopped "
            + "examining anything and would pass vacuously");

        // The instance the container built, so an instance member is invoked on a real collaborator set
        // rather than on something this test assembled.
        using ScopedServices scope = _fixture.CreateScopedServices();
        object target = scope.Resolve<IModuleBusinessControllerFactory>();

        Exception probe = new InvalidOperationException(
            OuterSentinel,
            new InvalidOperationException(InnerSentinel));

        foreach (MethodInfo member in exceptionToText)
        {
            string? produced = (string?)member.Invoke(member.IsStatic ? null : target, new object[] { probe });

            produced.Should().NotBeNull();
            produced!.Should().NotContain(
                OuterSentinel,
                "{0} must not reproduce an exception's own message",
                member.Name);
            produced.Should().NotContain(
                InnerSentinel,
                "{0} must not reproduce an inner exception's message either",
                member.Name);
        }

        await Task.CompletedTask;
    }

    // ---- Tenant-identifier redaction ------------------------------------------------------------------
    //
    // A QA run found command paths publishing the tenant discriminator: POST /users/99999/unlock answered
    // "Account 99999 does not exist in portal -1." The services interpolate it on purpose - it is what makes a
    // support report actionable, and it keeps doing so in the log - but on most routes the portal is resolved
    // from the caller's own token rather than supplied in the URL, so publishing it discloses how the
    // installation numbers its tenants in the course of answering an ordinary "no such thing".

    /// <summary>Every shape in which a service names a tenant is redacted on the way out.</summary>
    /// <param name="authored">The message a service places on its failed outcome.</param>
    /// <param name="expected">What the caller must receive instead.</param>
    /// <remarks>
    /// The cases are the real sentences, taken from the services, and they cover both grammatical positions -
    /// mid-sentence after a preposition, and opening the sentence - because the replacement has to agree with
    /// the sentence it lands in. The negative identifier is not an edge case but the COMMON one: the legacy
    /// schema seeds <c>Portals.PortalID</c> with <c>IDENTITY(-1,1)</c>, so a rule matching only digits would
    /// miss the default installation entirely.
    /// </remarks>
    [Theory]
    [InlineData(
        "Account 99999 does not exist in portal -1.",
        "Account 99999 does not exist in this portal.")]
    [InlineData(
        "Portal -1 has no role bearing identifier 99999.",
        "This portal has no role bearing identifier 99999.")]
    [InlineData(
        "Module 12 does not belong to portal 0.",
        "Module 12 does not belong to this portal.")]
    [InlineData(
        "Page 7 is not a content page of portal -1, so a module cannot be moved onto it.",
        "Page 7 is not a content page of this portal, so a module cannot be moved onto it.")]
    [InlineData(
        "Portal 42 already declares a profile property of that name.",
        "This portal already declares a profile property of that name.")]
    [InlineData(
        "Placing a module on every page reaches beyond the page and requires administering portal -1.",
        "Placing a module on every page reaches beyond the page and requires administering this portal.")]
    public void ATenantIdentifier_IsNotPublished(string authored, string expected)
    {
        Detail(Result.Failure("some.code", authored)).Should().Be(expected);
    }

    /// <summary>
    /// ⚠ THE CALLER'S OWN IDENTIFIERS SURVIVE, which is the point of redacting narrowly rather than broadly.
    /// </summary>
    /// <remarks>
    /// An account, role, page or module identifier in a not-found detail is the address the caller just asked
    /// about, echoed back so they can see WHICH of several addresses failed. Redacting those would cost real
    /// diagnostic value and protect nothing, because the caller already holds the value. This case exists so a
    /// later, broader rule cannot quietly take them away.
    /// </remarks>
    [Fact]
    public void TheCallersOwnIdentifiers_AreStillPublished()
    {
        Detail(Result.Failure("some.code", "Account 99999 does not exist in portal -1."))
            .Should().Contain("99999", "the caller asked about 99999 and is told it was 99999 that failed");

        Detail(Result.Failure("some.code", "Module 12 is not placed on page 5 in portal -1."))
            .Should().Be("Module 12 is not placed on page 5 in this portal.");
    }

    /// <summary>A message that never mentions a tenant is published byte for byte.</summary>
    /// <remarks>
    /// The redaction is a substitution and not a rewrite, so the overwhelming majority of details - which name
    /// no tenant - must be untouched. "portable" and "Portalgruppe" are here because the rule is anchored on a
    /// word boundary and requires a following integer, so a word merely containing "portal" cannot match.
    /// </remarks>
    [Theory]
    [InlineData("The current credential is not correct.")]
    [InlineData("The page index must not be negative.")]
    [InlineData("This module is not portable, so its content cannot be exported.")]
    [InlineData("Portalgruppe 7 is not a recognised value.")]
    [InlineData("The portal template could not be read.")]
    public void AMessageNamingNoTenant_IsPublishedUnchanged(string authored)
    {
        Detail(Result.Failure("some.code", authored)).Should().Be(authored);
    }

    /// <summary>Redaction runs on per-field messages too, not only on the summary detail.</summary>
    /// <remarks>
    /// A failure that attributes itself to fields is published through the validation-problem path instead, and
    /// each field message is published just as verbatim as the summary is - so a rule applied to only one of the
    /// two would leave the other leaking.
    /// </remarks>
    [Fact]
    public void ATenantIdentifierInAFieldMessage_IsNotPublishedEither()
    {
        var reason = new ResultReason(
            "some.code",
            "Portal -1 has no role bearing identifier 99999.",
            new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
            {
                ["roleId"] = new[] { "Portal -1 has no role bearing identifier 99999." },
            });

        // The per-field path routes through the shared factory - which is what attaches traceId and
        // correlationId - so the controller needs the real registered one. Resolved from the composed host
        // rather than substituted, so this exercises the same factory a live request would.
        using ScopedServices scope = _fixture.CreateScopedServices();

        var controller = new DisclosureController
        {
            ProblemDetailsFactory =
                scope.ServiceProvider.GetRequiredService<ProblemDetailsFactory>(),
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { RequestServices = scope.ServiceProvider },
            },
        };

        ActionResult translated = controller.Complete(Result.Failure(reason));

        ObjectResult payload = translated.Should().BeOfType<ObjectResult>().Subject;
        ValidationProblemDetails problem =
            payload.Value.Should().BeOfType<ValidationProblemDetails>().Subject;

        problem.Detail.Should().NotContain("-1");
        problem.Errors["roleId"].Should().OnlyContain(
            message => !message.Contains("-1", StringComparison.Ordinal),
            "a field message is published as verbatim as the summary, so it is redacted the same way");
        problem.Errors["roleId"].Should().OnlyContain(
            message => message.Contains("99999", StringComparison.Ordinal),
            "and the caller's own identifier survives there too");
    }

    /// <summary>Publishes a failed outcome through the edge and reads back the detail.</summary>
    /// <param name="failure">The failed outcome to translate.</param>
    /// <returns>The detail the caller would receive.</returns>
    private static string Detail(Result failure)
    {
        ActionResult translated = new DisclosureController().Complete(failure);

        ObjectResult payload = translated.Should().BeOfType<ObjectResult>().Subject;
        ProblemDetails problem = payload.Value.Should().BeOfType<ProblemDetails>().Subject;

        return problem.Detail ?? string.Empty;
    }

    /// <summary>The minimum controller needed to reach the extension method under test.</summary>
    private sealed class DisclosureController : ControllerBase
    {
    }
}
