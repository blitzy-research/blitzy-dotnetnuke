using System.Globalization;
using System.Net;
using FluentAssertions;
using Serilog.Events;
using Xunit;

namespace DnnMigration.IntegrationTests.Api;

/// <summary>
/// Proves the request envelope records what a request WAS and how it ENDED, and that it records neither the
/// caller's own path nor any exception message while doing so.
/// </summary>
/// <remarks>
/// <para>
/// Only a composed host can establish any of this. The stage sits inside the global exception handler and
/// outside routing, so what it can observe - whether an endpoint has been selected, what status the caller was
/// finally answered with - is a property of where it sits in a real pipeline, not of the type in isolation.
/// The unit projects cannot reach it in any case: they deliberately do not reference the Api project.
/// </para>
/// <para>
/// Entries are located by their source context rather than by position, because the whole assembly shares one
/// host and one sink: other suites are logging while these facts run, so "the last record" would be a race
/// and "the only record" would be false. The envelope carries no event identifier - it is the ordinary
/// per-request entry, not a named business event - so the source context is what identifies it.
/// </para>
/// </remarks>
[Trait("Category", "Integration")]
[Collection(IntegrationTestCollection.Name)]
public sealed class RequestLoggingContractTests
{
    /// <summary>
    /// The logger category the envelope is written under, which is the middleware's own type.
    /// </summary>
    /// <remarks>
    /// A literal rather than a reference. The same string appears in the fixture's Serilog override, and
    /// stating it in both places means a rename that silenced the category would fail here rather than pass
    /// quietly on a log that had stopped being written.
    /// </remarks>
    private const string EnvelopeSourceContext = "DnnMigration.Api.Middleware.RequestLoggingMiddleware";

    /// <summary>The value recorded in the route position when no endpoint was selected.</summary>
    private const string UnmatchedRouteTemplate = "(no matched endpoint)";

    /// <summary>The value recorded in the failure position when the request completed without one.</summary>
    /// <remarks>
    /// The bare word, which is what the stage records. It was stated here in parentheses for a while, by
    /// analogy with the unmatched-route marker beside it; the two are deliberately different because the
    /// route position otherwise holds a path-like template, where the failure position holds a type name
    /// and a bare word cannot be mistaken for one. The redaction suite asserts the same value.
    /// </remarks>
    private const string NoFailure = "none";

    /// <summary>The header the correlation identifier travels on.</summary>
    private const string CorrelationHeader = "X-Correlation-Id";

    /// <summary>
    /// A caller-supplied path segment shaped like a personal identifier.
    /// </summary>
    /// <remarks>
    /// Chosen to be the two things a path routinely carries and a log must never keep: an address that
    /// identifies a person, and a fragment that looks like a credential. If either reached the envelope it
    /// would be copied into every downstream log store and kept for the retention period of the noisiest log
    /// the application writes.
    /// </remarks>
    private const string SensitivePathSegment = "victim.user%40example.com-Password1";

    /// <summary>The same value as it appears once the server has decoded the path.</summary>
    private const string DecodedSensitivePathSegment = "victim.user@example.com-Password1";

    private readonly ApiTestFixture _fixture;

    /// <summary>Initialises a new instance of the <see cref="RequestLoggingContractTests"/> class.</summary>
    /// <param name="fixture">The shared composed host and provisioned database.</param>
    public RequestLoggingContractTests(ApiTestFixture fixture) => _fixture = fixture;

    /// <summary>
    /// A request that matched no endpoint is recorded under the fixed substitute, and the caller's own path
    /// reaches neither the rendered message nor any property.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// The unmatched case is the one a path-logging stage cannot get right by accident: there is no route
    /// template to fall back TO, so an implementation that reached for the path when the template was absent
    /// would look correct on every matched request and leak on every mistyped one - and mistyped requests are
    /// exactly where a caller's credential ends up when it is pasted into the wrong field.
    /// </remarks>
    [Fact]
    public async Task Envelope_ForAnUnmatchedRequest_RecordsTheSubstituteAndNotTheCallerPath()
    {
        using HttpClient client = _fixture.CreateAnonymousClient();
        string path = "/api/v1/portals/-1/users/" + SensitivePathSegment + "/profile";

        using HttpResponseMessage response = await client.GetAsync(new Uri(path, UriKind.Relative));

        LogRecord envelope = await LocateEnvelopeAsync(response);

        envelope.Properties["RouteTemplate"].Should().Be(
            UnmatchedRouteTemplate,
            "a request that selected no endpoint has no template, and the path is not an acceptable substitute");

        envelope.Properties.Should().NotContainKey(
            "RequestPath",
            "the raw path was removed from the entry; a property carrying it again would reintroduce caller input");

        envelope.Message.Should().NotContain(
            DecodedSensitivePathSegment,
            "the rendered message must not carry a caller-supplied identifier");

        foreach (KeyValuePair<string, object?> property in envelope.Properties)
        {
            property.Value?.ToString().Should().NotContain(
                DecodedSensitivePathSegment,
                "no property may carry a caller-supplied identifier either");
        }
    }

    /// <summary>
    /// A request that reached an endpoint is recorded under that endpoint's declared template, with the
    /// status the caller was answered with and the correlation identifier the response carried.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// <para>
    /// The declared template is told apart from the concrete path by a property neither shares with the
    /// other: a template as declared on the controller has no leading separator and does carry the version
    /// placeholder, while the request line has a leading separator and a resolved version. Asserting on both
    /// is what makes this fact fail if the path were ever recorded again, rather than merely asserting that
    /// something was recorded.
    /// </para>
    /// <para>
    /// The status is asserted against the one the CLIENT observed, not against a literal. That is the whole
    /// point of the assertion: the entry is written from a response-completion callback precisely so the two
    /// cannot disagree, and pinning a literal here would still pass if both drifted together.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Envelope_ForAMatchedRequest_RecordsTheTemplateTheStatusAndTheCorrelationIdentifier()
    {
        using HttpClient client = _fixture.CreateAnonymousClient();

        using HttpResponseMessage response = await client.GetAsync(new Uri("/api/v1/portals", UriKind.Relative));

        response.StatusCode.Should().Be(
            HttpStatusCode.Unauthorized,
            "the tenant listing is authorised, so an anonymous caller is refused rather than served");

        LogRecord envelope = await LocateEnvelopeAsync(response);

        string? template = envelope.Properties["RouteTemplate"]?.ToString();

        template.Should().NotBeNull();
        template!.Should().Contain(
            "portals",
            "the template names the operation the request reached");
        template.Should().Contain(
            "{version",
            "the DECLARED pattern still carries its version placeholder; a request path carries a resolved version");
        template.Should().NotStartWith(
            "/",
            "a declared template has no leading separator, whereas the request line does");

        envelope.Properties["StatusCode"].Should().Be(
            (int)response.StatusCode,
            "the recorded status must be the one the caller was answered with");

        envelope.Properties["Failure"].Should().Be(
            NoFailure,
            "a refusal is not an escaped failure, and the failure position is always present");

        envelope.Properties["CorrelationId"].Should().Be(
            CorrelationOf(response),
            "the entry and the response must quote the same identifier or a caller's report cannot be joined to it");
    }

    /// <summary>
    /// A refused request is recorded as a warning, not as an error.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// The level is the only part of the entry an alert rule acts on without reading it. Promoting an
    /// ordinary refusal to error would fill the error stream with unauthenticated probes until a genuine
    /// fault in it was invisible, which is the failure mode this asserts against.
    /// </remarks>
    [Fact]
    public async Task Envelope_ForARefusedRequest_IsRecordedAsAWarning()
    {
        using HttpClient client = _fixture.CreateAnonymousClient();

        using HttpResponseMessage response = await client.GetAsync(new Uri("/api/v1/portals", UriKind.Relative));

        LogRecord envelope = await LocateEnvelopeAsync(response);

        envelope.Level.Should().Be(
            LogEventLevel.Warning,
            "a 4xx is the caller's doing: worth noticing, not worth alarming anybody");

        envelope.Exception.Should().BeNull(
            "the exception is described in a bounded property, never attached for a provider to render");
    }

    /// <summary>
    /// Neither health view produces an envelope at the ordinary level, including the readiness sub-path.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// <para>
    /// Health views are polled: the image's own check probes the liveness path on a fixed interval for the
    /// life of the container, and an orchestrator polls readiness at least as often. An entry per poll at the
    /// ordinary level would dominate the stream by volume alone and would push the entries that describe real
    /// requests out of any retention window, so both views are classified as diagnostic traffic.
    /// </para>
    /// <para>
    /// This is asserted for the READINESS path specifically because the classification predicate matches by
    /// path SEGMENT rather than by equality, and the sub-path inherits the quiet level only for that reason.
    /// An edit narrowing the predicate back to equality would leave the liveness view quiet and start
    /// emitting an entry per readiness poll - a regression that nothing else here would catch.
    /// </para>
    /// <para>
    /// The matched request at the end is a POSITIVE CONTROL and is not incidental. Absence proves the
    /// classification only if the sink was capable of recording a presence in the same run; without it a sink
    /// that had stopped writing altogether would satisfy every assertion above.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Envelope_ForEitherHealthView_IsNotRecordedAtTheOrdinaryLevel()
    {
        using HttpClient client = _fixture.CreateAnonymousClient();

        foreach (string view in new[] { "/health", "/health/ready" })
        {
            using HttpResponseMessage probe = await client.GetAsync(new Uri(view, UriKind.Relative));

            (await FindEnvelopeAsync(probe)).Should().BeNull(
                "a polled health view is diagnostic traffic, so no entry for {0} may reach the ordinary "
                + "level the fixture admits",
                view);
        }

        using HttpResponseMessage ordinary = await client.GetAsync(new Uri("/api/v1/portals", UriKind.Relative));

        (await FindEnvelopeAsync(ordinary)).Should().NotBeNull(
            "the control: an ordinary request must still be recorded, or the absences above prove only that "
            + "the sink stopped working");
    }

    /// <summary>Reads the correlation identifier the response carried.</summary>
    /// <param name="response">The response to read.</param>
    /// <returns>The single header value.</returns>
    private static string CorrelationOf(HttpResponseMessage response)
    {
        response.Headers.TryGetValues(CorrelationHeader, out IEnumerable<string>? values).Should().BeTrue(
            "every response carries the identifier the entry is tagged with");

        return values!.Single();
    }

    /// <summary>
    /// Finds the envelope written for the request that produced <paramref name="response"/>.
    /// </summary>
    /// <param name="response">The response whose correlation identifier selects the entry.</param>
    /// <returns>The single matching entry.</returns>
    /// <remarks>
    /// <para>
    /// Selected by correlation identifier, which is what makes the search exact on a sink the whole assembly
    /// shares: the identifier is unique to one request and appears on that request's envelope and on its
    /// response.
    /// </para>
    /// <para>
    /// Bounded polling rather than a single read, because the entry is written from a response-completion
    /// callback: the client's own await and the server's completion callback are ordered by the transport,
    /// not by this suite, so a single read could observe the response before the callback had run. The wait
    /// is short and its expiry is a failure, so a stage that stopped writing the entry cannot pass by
    /// timing out.
    /// </para>
    /// </remarks>
    private static async Task<LogRecord> LocateEnvelopeAsync(HttpResponseMessage response)
    {
        return await FindEnvelopeAsync(response)
            ?? throw new InvalidOperationException(string.Format(
                CultureInfo.InvariantCulture,
                "No request envelope was recorded under source context '{0}' for correlation identifier '{1}'.",
                EnvelopeSourceContext,
                CorrelationOf(response)));
    }

    /// <summary>
    /// Looks for the envelope written for the request that produced <paramref name="response"/>, and reports
    /// its absence rather than failing on it.
    /// </summary>
    /// <param name="response">The response whose correlation identifier selects the entry.</param>
    /// <returns>The matching entry, or <see langword="null"/> when none was recorded.</returns>
    /// <remarks>
    /// The wait is the same length whether an entry is expected or not, deliberately. A shorter wait for the
    /// absent case would let an entry that was simply late read as an entry that was never written, which is
    /// the one way an absence assertion can pass while the property it guards is broken.
    /// </remarks>
    private static async Task<LogRecord?> FindEnvelopeAsync(HttpResponseMessage response)
    {
        string correlationId = CorrelationOf(response);

        for (int attempt = 0; attempt < 40; attempt++)
        {
            LogRecord? located = RecordedLogs.Snapshot()
                .LastOrDefault(candidate =>
                    candidate.Properties.TryGetValue("SourceContext", out object? source)
                    && string.Equals(
                        source?.ToString(),
                        EnvelopeSourceContext,
                        StringComparison.Ordinal)
                    && candidate.Properties.TryGetValue("CorrelationId", out object? recorded)
                    && string.Equals(
                        recorded?.ToString(),
                        correlationId,
                        StringComparison.Ordinal));

            if (located is not null)
            {
                return located;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(25));
        }

        return null;
    }
}
