namespace DnnMigration.Api.Extensions;

/// <summary>
/// Recognises host names that exist only to illustrate a setting, so that a deployment configured with one
/// is refused at start-up instead of serving traffic under a name nothing else agrees on.
/// </summary>
/// <remarks>
/// <strong>WHAT IS DELIBERATELY NOT REFUSED, WHICH MATTERS MORE.</strong> RFC 2606 also reserves
/// <c>.test</c>, <c>.invalid</c> and <c>.localhost</c>, and none of those is rejected here.
/// </remarks>
internal static class ReservedDeploymentHosts
{
    /// <summary>The second-level domains RFC 2606 reserves for documentation.</summary>
    private static readonly string[] DocumentationDomains =
        ["example.com", "example.net", "example.org"];

    /// <summary>The top-level domain RFC 6761 reserves for documentation.</summary>
    private const string DocumentationTopLevelLabel = "example";

    /// <summary>The editing markers a template, a ticket or a copied snippet leaves behind.</summary>
    /// <remarks>
    /// Matched as a whole LABEL, never as a substring: a deployment legitimately named
    /// <c>changemakers.org</c> must not be refused because its first label starts with the same five
    /// letters.
    /// </remarks>
    private static readonly string[] PlaceholderLabels =
        [
            "changeme",
            "change-me",
            "change_me",
            "replaceme",
            "replace-me",
            "replace_me",
            "yourdomain",
            "your-domain",
            "your_domain",
            "yourhost",
            "your-host",
            "todo",
            "tbd",
        ];

    /// <summary>
    /// Describes why a configured host name cannot be a deployment's public identity, or reports that it
    /// can.
    /// </summary>
    /// <param name="hostOrAuthority">
    /// A configured host name, optionally carrying a port, a leading wildcard label or a trailing root dot.
    /// </param>
    /// <returns>A sentence naming the defect, or <see langword="null"/> when the value is usable.</returns>
    internal static string? DescribeRejection(string hostOrAuthority)
    {
        string host = Normalise(hostOrAuthority);

        if (host.Length == 0)
        {
            return null;
        }

        foreach (string documentationDomain in DocumentationDomains)
        {
            if (host.Equals(documentationDomain, StringComparison.Ordinal)
                || host.EndsWith('.' + documentationDomain, StringComparison.Ordinal))
            {
                return $"'{documentationDomain}' is reserved for documentation by RFC 2606 and can never be "
                    + "served to a real browser, so this is the template's illustration rather than this "
                    + "deployment's own name.";
            }
        }

        string[] labels = host.Split('.', StringSplitOptions.RemoveEmptyEntries);

        if (labels.Length != 0
            && labels[^1].Equals(DocumentationTopLevelLabel, StringComparison.Ordinal))
        {
            return "the '.example' top-level domain is reserved for documentation by RFC 6761 and can never "
                + "be served to a real browser, so this is the template's illustration rather than this "
                + "deployment's own name.";
        }

        foreach (string label in labels)
        {
            foreach (string placeholder in PlaceholderLabels)
            {
                if (label.Equals(placeholder, StringComparison.Ordinal))
                {
                    return $"'{placeholder}' is an editing marker left by a template rather than a host name.";
                }
            }
        }

        return null;
    }

    /// <summary>Reduces a configured entry to the bare host name the checks compare.</summary>
    /// <param name="hostOrAuthority">The configured entry.</param>
    /// <returns>The lower-cased host name, without wildcard label, port or trailing root dot.</returns>
    private static string Normalise(string hostOrAuthority)
    {
        string host = hostOrAuthority.Trim().ToLowerInvariant();

        if (host.StartsWith("*.", StringComparison.Ordinal))
        {
            host = host[2..];
        }

        if (host.StartsWith('['))
        {
            // A bracketed IPv6 literal: the port, if any, follows the closing bracket.
            int closingBracket = host.IndexOf(']', StringComparison.Ordinal);

            if (closingBracket >= 0)
            {
                return host[..(closingBracket + 1)];
            }

            return host;
        }

        int portSeparator = host.IndexOf(':', StringComparison.Ordinal);

        if (portSeparator >= 0)
        {
            host = host[..portSeparator];
        }

        return host.TrimEnd('.');
    }
}
