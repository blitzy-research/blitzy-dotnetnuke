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
    /// Every response header this deployment's proxy is REQUIRED to state, paired with the value it must
    /// state. This is the inventory, not a summary of whatever the file happens to contain.
    /// </summary>
    /// <remarks>
    /// <para>
    /// WHY THE INVENTORY IS NAMED HERE RATHER THAN DERIVED FROM THE FILE. The two facts below this one
    /// quantify over the declarations the snippet still has - the crawler exclusion is looked up by name,
    /// and the <c>always</c> check iterates whatever matched. Both are worth having, and neither can see a
    /// DELETION: removing <c>add_header X-Content-Type-Options "nosniff" always;</c> removes the subject of
    /// the assertion rather than breaking it, so the whole backend suite stayed green while the shipped
    /// policy lost a header. The API does not emit these headers itself either - they are the proxy's, and
    /// this snippet is their single declaration - so no request-level suite covered the loss. Naming the
    /// required set is what turns "the file has a policy" into "the file has THIS policy".
    /// </para>
    /// <para>
    /// TWO VALUES ARE VARIABLE REFERENCES AND ARE ASSERTED AS SUCH. <c>$content_security_policy</c> and
    /// <c>$hsts_policy</c> are declared once in <c>docker/nginx.conf</c>'s <c>http</c> block so that every
    /// server block - including a mounted TLS one - shares them, and <c>$hsts_policy</c> resolves to the
    /// empty string on plain HTTP so strict transport security appears only on a TLS response. What this
    /// snippet is authoritative for is that the header is stated and that it is stated THROUGH THAT
    /// VARIABLE; the variable's own content belongs to the file that declares it.
    /// </para>
    /// </remarks>
    private static readonly (string Name, string Value)[] RequiredHeaders =
    [
        ("X-Content-Type-Options", "nosniff"),
        ("X-Frame-Options", "SAMEORIGIN"),
        ("Referrer-Policy", "strict-origin-when-cross-origin"),
        ("Content-Security-Policy", "$content_security_policy"),
        ("Strict-Transport-Security", "$hsts_policy"),
        ("Cross-Origin-Opener-Policy", "same-origin"),
        ("X-Permitted-Cross-Domain-Policies", "none"),
        ("Permissions-Policy", "camera=(), geolocation=(), microphone=(), payment=(), usb=()"),
        ("X-Robots-Tag", "noindex, nofollow"),
    ];

    /// <summary>The required inventory as one theory case per header.</summary>
    /// <returns>A case carrying the header's field name and the value it must carry.</returns>
    /// <remarks>
    /// One case per header rather than one fact over the whole set, so a deleted or altered header fails a
    /// case that NAMES IT - the failure reads "X-Frame-Options" rather than "a header is missing", which is
    /// the difference between a report an operator can act on and one they have to investigate.
    /// </remarks>
    public static TheoryData<string, string> RequiredHeaderCases()
    {
        var cases = new TheoryData<string, string>();

        foreach ((string name, string value) in RequiredHeaders)
        {
            cases.Add(name, value);
        }

        return cases;
    }

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

    /// <summary>
    /// Each required header is declared exactly once, and declared with the value it must carry.
    /// </summary>
    /// <param name="headerName">The response header field name that must be declared.</param>
    /// <param name="expectedValue">The value the declaration must state, without its <c>always</c> flag.</param>
    /// <remarks>
    /// EXACTLY ONCE, because two declarations of one header are not additive in nginx: within a single
    /// context both are emitted, so a browser receives the field twice and, for a policy header, the
    /// stricter-of-two behaviour differs between headers and between browsers. One declaration per header is
    /// the only state whose effect is unambiguous.
    /// </remarks>
    [Theory]
    [MemberData(nameof(RequiredHeaderCases))]
    public void EveryRequiredHeaderIsDeclaredWithTheValueItMustCarry(string headerName, string expectedValue)
    {
        string snippet = ReadSnippet();

        Match[] declarations = AddHeaderDirective.Matches(snippet)
            .Where(match => string.Equals(
                match.Groups["name"].Value,
                headerName,
                StringComparison.OrdinalIgnoreCase))
            .ToArray();

        declarations.Should().HaveCount(
            1,
            "docker/security-headers.conf is the single declaration of the proxy's response-header policy, "
                + $"and every path it serves depends on {headerName} being declared there exactly once; "
                + "restore it in that file rather than in one of its nine consumers, which would put the "
                + "header on some served paths and not others");

        StatedValue(declarations[0]).Should().Be(
            expectedValue,
            $"{headerName} is what the deployment states to every browser, so its value is part of the "
                + "policy rather than a detail: a weakened value is indistinguishable from the header "
                + "being present until a browser acts on it");
    }

    /// <summary>Nothing is declared that the required inventory does not name.</summary>
    /// <remarks>
    /// The complement of the theory above, and it is the half that keeps the inventory honest. Without it a
    /// header could be added to the shipped file - or renamed, which is an addition and a deletion at once -
    /// and no test would have an opinion about it. A header genuinely worth shipping is worth naming in
    /// <see cref="RequiredHeaders"/> with the value it must carry, which is a one-line change and a
    /// deliberate one.
    /// </remarks>
    [Fact]
    public void NoHeaderIsDeclaredThatTheRequiredInventoryDoesNotName()
    {
        string snippet = ReadSnippet();

        string[] declared = AddHeaderDirective.Matches(snippet)
            .Select(match => match.Groups["name"].Value)
            .ToArray();

        declared.Should().OnlyHaveUniqueItems(
            "a header declared twice in one context is emitted twice, and which of the two values applies "
            + "is left to the browser");

        declared.Should().BeEquivalentTo(
            RequiredHeaders.Select(header => header.Name),
            "the shipped policy and the policy this suite asserts are the same policy; a header added to "
            + "docker/security-headers.conf belongs in RequiredHeaders alongside the value it must carry, "
            + "and a header removed from the inventory has to be removed from the file in the same change");
    }

    /// <summary>Reads the value a directive states, with the <c>always</c> flag and any quoting removed.</summary>
    /// <param name="declaration">A matched <c>add_header</c> directive.</param>
    /// <returns>The stated value.</returns>
    /// <remarks>
    /// The <c>always</c> flag is stripped rather than compared here because
    /// <see cref="EveryDeclaredHeaderIsEmittedOnEveryResponseStatus"/> owns it for every declaration at
    /// once; comparing it in both places would report one defect as two. Quoting is stripped because it is
    /// nginx syntax rather than part of the value - <c>"none"</c> and <c>none</c> reach the browser
    /// identically - while a variable reference such as <c>$hsts_policy</c> carries no quotes at all and is
    /// therefore returned as written.
    /// </remarks>
    private static string StatedValue(Match declaration)
    {
        const string AlwaysFlag = "always";

        string value = declaration.Groups["value"].Value.Trim();

        if (value.EndsWith(AlwaysFlag, StringComparison.OrdinalIgnoreCase))
        {
            value = value[..^AlwaysFlag.Length].TrimEnd();
        }

        return value.Length >= 2 && value[0] == '"' && value[^1] == '"'
            ? value[1..^1]
            : value;
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
