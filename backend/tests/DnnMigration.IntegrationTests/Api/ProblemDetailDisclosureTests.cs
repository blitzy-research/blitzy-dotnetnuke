using System.Reflection;
using DnnMigration.Api.ErrorHandling;
using DnnMigration.Application.Abstractions;
using DnnMigration.Domain.Common;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace DnnMigration.IntegrationTests.Api;

/// <summary>
/// Pins the disclosure boundary of the problem-details edge: what a failed outcome may publish as its
/// <c>detail</c>, and what it must replace with a stand-in.
/// </summary>
/// <remarks>
/// <para>
/// A security review found that the module-lifecycle factory placed a third-party module's own
/// <c>Exception.Message</c> on its failed outcomes, and that this edge published it verbatim as the RFC 7807
/// <c>detail</c> - so whatever connection string, file-system path, statement or content the module had
/// quoted travelled to an HTTP client. The source was corrected, and a backstop was added here for the case
/// where a future author reaches for the same premise, because nothing about a <c>string</c> announces where
/// it came from.
/// </para>
/// <para>
/// These assertions are at integration level because the type under test belongs to the API assembly, which
/// the unit-test project deliberately does not reference. No host, database or HTTP request is needed: the
/// members are reached directly, which keeps the assertions about the rule rather than about a route.
/// </para>
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

    /// <summary>
    /// A short, single-line, authored explanation is published unchanged.
    /// </summary>
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
    /// <remarks>
    /// A stack trace, an aggregated failure and a provider message that quotes a statement are all
    /// multi-line, and no authored explanation in this solution is. The refusal therefore cannot mask a
    /// correct message while it does catch the shape the finding described.
    /// </remarks>
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

    /// <summary>
    /// A message longer than the published bound is refused rather than truncated.
    /// </summary>
    /// <remarks>
    /// Truncation was considered and rejected: half an explanation with no indication that anything was
    /// removed is worse than a stand-in that says plainly that the request did not complete and points at
    /// the correlation identifier. The bound is generous enough that no authored sentence approaches it.
    /// </remarks>
    [Fact]
    public void AnOverlongMessage_IsReplacedRatherThanTruncated()
    {
        string leaked = new('x', PublishedDetailBound + 1);

        string published = Detail(Result.Failure("some.code", leaked));

        published.Should().NotBe(leaked);
        published.Should().NotStartWith("xxxx");
        published.Should().Contain("could not be completed");
    }

    /// <summary>
    /// No member of the module-lifecycle factory that turns an exception into text reproduces that
    /// exception's message.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The finding's actual source was a private helper on that factory named <c>Describe</c>, which
    /// returned <c>error.Message</c> so that "the underlying explanation survives so the caller can log
    /// something actionable". The premise was sound and the destination was wrong, because a failed
    /// outcome's message is published as the RFC 7807 <c>detail</c>.
    /// </para>
    /// <para>
    /// An earlier version of this test asserted that no member with the shape
    /// <c>Exception -&gt; string</c> exists at all, and that was the wrong invariant: it failed against
    /// the redacting describer that legitimately replaced the leaking one, which writes the exception's
    /// type chain and stack traces to the log and drops its messages. Forbidding the shape would have
    /// forbidden the fix. What must be forbidden is the behaviour, so every such member is invoked here
    /// with an exception whose message - and whose inner exception's message - is a sentence nothing else
    /// would produce, and the result must contain neither. That permits any number of redacting
    /// describers, under any name, and admits no leaking one.
    /// </para>
    /// </remarks>
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
        // rather than on something this test assembled. It is resolved inside a SCOPE rather than from the
        // root provider, because the factory is registered scoped - as it must be, since it reaches the
        // per-request data context - and the root provider has no scope to hand it. The host now validates
        // scopes, so resolving it from the root fails outright; before that it happened to work while
        // quietly keeping a request-lifetime service alive for the whole run, which is the captive
        // dependency the validation exists to prevent. The scope stays open for the loop below, so the
        // instance under examination is not disposed underneath it.
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
