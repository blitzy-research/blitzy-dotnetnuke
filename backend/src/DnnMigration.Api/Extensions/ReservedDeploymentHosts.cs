namespace DnnMigration.Api.Extensions;

/// <summary>
/// Recognises host names that exist only to illustrate a setting, so that a deployment configured with one
/// is refused at start-up instead of serving traffic under a name nothing else agrees on.
/// </summary>
/// <remarks>
/// <para>
/// <strong>WHY THIS EXISTS.</strong> The public identity of a deployment is read in three places that must
/// agree: the reverse proxy's <c>server_name</c> (and therefore which requests its port-80 redirect
/// captures), the certificate that name is issued for, and this API's own host filter and cross-origin
/// allow-list. The container artefacts previously carried <c>dnn.example.com</c> as a literal in two of
/// those places while parameterising only the third, so a deployment that followed the documented
/// environment workflow got a redirect that never matched its browsers, a certificate that matched no
/// server name, and an API that refused its own traffic with 400. All three are now derived from one
/// required setting - and a required setting that is merely *present* is not enough, because the value most
/// likely to be present is the illustration from the template.
/// </para>
/// <para>
/// <strong>WHAT IS REFUSED, AND WHY EXACTLY THIS SET.</strong> RFC 2606 reserves
/// <c>example.com</c>, <c>example.net</c> and <c>example.org</c>, and RFC 6761 reserves the
/// <c>.example</c> top-level domain, for documentation. A name inside either can never be served to a real
/// browser, so accepting one cannot be correct - it can only be a template value that was never replaced.
/// The literal editing markers a template or a ticket leaves behind are refused for the same reason.
/// </para>
/// <para>
/// <strong>WHAT IS DELIBERATELY NOT REFUSED, WHICH MATTERS MORE.</strong> RFC 2606 also reserves
/// <c>.test</c>, <c>.invalid</c> and <c>.localhost</c>, and none of those is rejected here. A private
/// deployment on <c>admin.acme.test</c>, an internal certificate authority issuing for a <c>.test</c> name,
/// and the loopback entries the container health probe depends on are all legitimate, and a validator that
/// refused them would convert a hardening measure into an outage. Nor is a wildcard host, an IP literal or
/// <c>localhost</c> refused: the base topology's own probe addresses <c>localhost</c>, and deciding whether
/// a wildcard is acceptable is a policy question this type does not answer.
/// </para>
/// <para>
/// The check runs at start-up over configuration only. It never inspects a request, so no caller can reach
/// it and no request can be refused by it.
/// </para>
/// </remarks>
internal static class ReservedDeploymentHosts
{
    /// <summary>
    /// The second-level domains RFC 2606 reserves for documentation.
    /// </summary>
    private static readonly string[] DocumentationDomains =
        ["example.com", "example.net", "example.org"];

    /// <summary>
    /// The top-level domain RFC 6761 reserves for documentation.
    /// </summary>
    /// <remarks>
    /// Held as a label rather than as a suffix string so that <c>admin.acme.example</c> is recognised while
    /// a real name that merely contains the word - <c>example-hosting.com</c>, say - is not.
    /// </remarks>
    private const string DocumentationTopLevelLabel = "example";

    /// <summary>
    /// The editing markers a template, a ticket or a copied snippet leaves behind.
    /// </summary>
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
    /// <returns>
    /// A sentence naming the defect, or <see langword="null"/> when the value is usable.
    /// </returns>
    /// <remarks>
    /// A sentence rather than a boolean, because the caller reports it to an operator who has to act on it:
    /// "this is a documentation name" and "this is an unreplaced editing marker" send that operator to two
    /// different places in the deployment template.
    /// </remarks>
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
    /// <remarks>
    /// <para>
    /// Four shapes arrive here and each is handled deliberately. A leading <c>*.</c> is the host filter's
    /// wildcard-subdomain form, and <c>*.example.com</c> is as much a documentation name as
    /// <c>example.com</c> is. A port is legitimate in both a host-filter entry and a browser origin, and it
    /// says nothing about which name is being served. A trailing dot is the fully-qualified spelling of the
    /// same name. Case is irrelevant to DNS, so the comparison is made on a lower-cased value with an
    /// ordinal comparer rather than by a culture-sensitive comparison that could vary with the host's
    /// locale.
    /// </para>
    /// <para>
    /// A bracketed IPv6 literal is returned as-is apart from its port, which is correct by omission: no
    /// address literal can match a documentation NAME, so it falls through every check and is accepted.
    /// </para>
    /// </remarks>
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
