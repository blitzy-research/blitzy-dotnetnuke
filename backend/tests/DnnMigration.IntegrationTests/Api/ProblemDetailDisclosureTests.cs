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
