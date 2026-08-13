using System.Reflection;
using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace DnnMigration.IntegrationTests.Infrastructure;

/// <summary>
/// Guards the response-header policy the reverse proxy states in <c>docker/security-headers.conf</c>.
/// </summary>
/// <remarks>
/// INFO-02. The administration console's document advertised <c>ROBOTS: INDEX, FOLLOW</c> - the legacy
/// portal's own declaration, carried forward verbatim - while nothing this deployment serves has an
/// audience a crawler represents.
/// </remarks>
[Trait("Category", "Integration")]
public sealed class ResponseHeaderPolicyTests
{
    /// <summary>The embedded copy of the proxy's own response-header snippet.</summary>
    private const string SnippetResourceName =
        "DnnMigration.IntegrationTests.Deployment.SecurityHeaders.conf";

    /// <summary>
    /// Matches an <c>add_header</c> directive, capturing the field name and whether it carries
    /// <c>always</c>. Anchored to the start of a line so the many <c>add_header</c> mentions inside the
    /// snippet's commentary are not mistaken for directives.
    /// </summary>
    private static readonly Regex AddHeaderDirective = new(
        @"^add_header\s+(?<name>[A-Za-z0-9\-]+)\s+(?<value>.+?)\s*;\s*$",
        RegexOptions.Multiline | RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(2));

    /// <summary>
    /// The crawler exclusion is declared, and declared with both directives that matter: <c>noindex</c> so
    /// the page is not listed, and <c>nofollow</c> so the tenant-shaped links inside it are not walked.
    /// </summary>
    [Fact]
    public void CrawlerExclusionIsDeclared()
    {
        string snippet = ReadSnippet();

        Match directive = AddHeaderDirective.Matches(snippet)
            .SingleOrDefault(match => string.Equals(
                match.Groups["name"].Value,
                "X-Robots-Tag",
                StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException(
                "docker/security-headers.conf declares no X-Robots-Tag directive. The administration " +
                "console is not public content: restore the header rather than relying on the legacy " +
                "meta element in frontend/src/index.html, which reaches neither the API's JSON nor an " +
                "error response.");

        string value = directive.Groups["value"].Value;

        value.Should().Contain(
            "noindex",
            "the console's pages must not be listed in a search index");

        value.Should().Contain(
            "nofollow",
            "an indexed console would publish tenant-shaped URLs as a browsable map of the deployment, " +
            "so the links must not be walked either");
    }

    /// <summary>Every declared header carries <c>always</c>.</summary>
    /// <remarks>
    /// This is the mechanism the crawler exclusion depends on, so it is asserted rather than assumed.
    /// Without <c>always</c> nginx emits an <c>add_header</c> only on 2xx, 204, 301, 302, 303, 304, 307 and
    /// 308 - so the 401 the API proxy returns to an unauthenticated crawler, the 404 for a missing asset
    /// and the 503 the gateway synthesises would all travel with no policy at all.
    /// </remarks>
    [Fact]
    public void EveryDeclaredHeaderIsEmittedOnEveryResponseStatus()
    {
        string snippet = ReadSnippet();

        MatchCollection directives = AddHeaderDirective.Matches(snippet);

        directives.Should().NotBeEmpty(
            "the snippet is the single declaration of the proxy's response-header policy");

        string[] withoutAlways = directives
            .Where(match => !match.Groups["value"].Value
                .TrimEnd()
                .EndsWith("always", StringComparison.OrdinalIgnoreCase))
            .Select(match => match.Groups["name"].Value)
            .ToArray();

        withoutAlways.Should().BeEmpty(
            "nginx emits an add_header without `always` only on a success or redirect status, so these " +
            "headers would be missing from the 401, 404 and 503 responses an anonymous request receives");
    }

    /// <summary>Reads the deployment's own snippet out of this assembly.</summary>
    /// <returns>The snippet's text.</returns>
    /// <exception cref="InvalidOperationException">The linked resource is missing from the build.</exception>
    private static string ReadSnippet()
    {
        Assembly assembly = typeof(ResponseHeaderPolicyTests).Assembly;

        using Stream stream = assembly.GetManifestResourceStream(SnippetResourceName)
            ?? throw new InvalidOperationException(
                $"The embedded resource '{SnippetResourceName}' is missing. It is linked from " +
                "docker/security-headers.conf by DnnMigration.IntegrationTests.csproj; restore the " +
                "EmbeddedResource item rather than adding a second copy of the file under this project.");

        using var reader = new StreamReader(stream);

        return reader.ReadToEnd();
    }
}
