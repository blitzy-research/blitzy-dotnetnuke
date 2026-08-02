using System.Globalization;

namespace DnnMigration.Application.Validation;

/// <summary>
/// The shape rules for a portal HTTP alias, declared once and applied by both the create and the
/// update alias contracts.
/// </summary>
/// <remarks>
/// <para>
/// <b>One definition, two callers.</b> An alias submitted on create and an alias submitted on
/// update are the same value bound for the same uniquely indexed column, so a rule that held on
/// one path and not the other would be a rule that could be bypassed by choosing the other verb.
/// Both validators call the members below rather than restating them.
/// </para>
/// <para>
/// <b>Nothing here rewrites the caller's value.</b> The predicate answers a question; it returns no
/// corrected string. Canonicalising a submitted alias - which the legacy screen did silently - is a
/// separate decision recorded on the request contracts, and a validator that quietly rewrote its
/// input would leave a caller unable to learn what was actually stored.
/// </para>
/// <para>
/// <b>The accepted vocabulary is measured, not invented.</b> The legacy screen documented it in the
/// help text beside the field, at
/// <c>Website/admin/Portal/App_LocalResources/EditPortalAlias.ascx.resx:L124</c>, which names four
/// acceptable forms - a local address such as <c>localhost</c>, an IP address such as
/// <c>127.0.0.1</c>, a full address such as <c>www.mydomain.com</c>, and a server name such as
/// <c>MYSERVER</c> - and then instructs the operator, in that same text, not to include the
/// protocol prefix. The rules below admit exactly those forms plus an optional port and an optional
/// child path, which is the form a DotNetNuke installation uses to host several portals on one host
/// name.
/// </para>
/// <para>
/// <b>Case is accepted as submitted.</b> The measured help text's own server-name example is upper
/// case, so a rule that rejected upper case would refuse a documented form. The legacy write path
/// lower-cased the value before persisting it, on insert at
/// <c>Library/Components/Portal/PortalAliasController.vb:L31</c> and on update at <c>L97</c>; that
/// is a storage decision, recorded on the alias projection, and not a rule about what a caller may
/// send.
/// </para>
/// <para>
/// <b>A hand-written scan rather than a pattern.</b> The vocabulary is small and positional, and a
/// scan cannot backtrack, so there is no catastrophic-matching exposure to bound with a timeout and
/// no pattern for a reader to decode. It performs no I/O and allocates nothing beyond the spans it
/// walks.
/// </para>
/// </remarks>
internal static class PortalAliasRules
{
    /// <summary>
    /// The longest alias that can be stored, taken from the terminal width of
    /// <c>PortalAlias.HTTPAlias</c>, declared <c>[nvarchar] (200)</c> at
    /// <c>Website/Providers/DataProviders/SqlDataProvider/02.02.02.SqlDataProvider:L3807</c>.
    /// </summary>
    /// <remarks>
    /// The legacy screen disagreed with its own column and was wrong: the input at
    /// <c>Website/admin/Portal/editportalalias.ascx:L7</c> declared <c>MaxLength="255"</c>, so an
    /// operator could type 55 characters more than the column could hold and the write would fail
    /// in the store rather than in the screen. The column governs, because the column is what has
    /// to accept the value.
    /// </remarks>
    internal const int MaximumLength = 200;

    /// <summary>
    /// The largest port number an authority may name.
    /// </summary>
    internal const int MaximumPortNumber = 65535;

    /// <summary>
    /// Message reported when no alias was supplied.
    /// </summary>
    internal const string RequiredMessage = "An HTTP alias is required.";

    /// <summary>
    /// Message reported when an alias is not one of the forms this application can store.
    /// </summary>
    /// <remarks>
    /// Net-new wording. The legacy screen declared no validator over this field and therefore
    /// shipped no message for a malformed alias - its only alias message,
    /// <c>DuplicateAlias.Text</c> at <c>EditPortalAlias.ascx.resx:L135-L136</c>, reads "The Portal
    /// Alias already exists." and describes a different condition, which belongs to the service
    /// rather than to a validator. The wording below is composed from the accepted forms the help
    /// text names, including its explicit instruction about the protocol prefix.
    /// </remarks>
    internal const string InvalidMessage =
        "An HTTP alias must be a host name, an IP address or a server name, optionally followed by "
        + "a port and a path, and must not include a protocol prefix.";

    /// <summary>
    /// The separator that introduces a path segment.
    /// </summary>
    private const char PathSeparator = '/';

    /// <summary>
    /// The separator that introduces a port.
    /// </summary>
    private const char PortSeparator = ':';

    /// <summary>
    /// The single-segment reference to the current directory, rejected inside a path.
    /// </summary>
    private const string CurrentSegment = ".";

    /// <summary>
    /// The single-segment reference to the parent directory, rejected inside a path.
    /// </summary>
    private const string ParentSegment = "..";

    /// <summary>
    /// Message reported when an alias exceeds what the column can hold, composed from the bound so
    /// the figure is stated in exactly one place.
    /// </summary>
    internal static readonly string TooLongMessage = FormattableString.Invariant(
        $"An HTTP alias may not exceed {MaximumLength} characters.");

    /// <summary>
    /// Reports whether a submitted alias is a form this application can store.
    /// </summary>
    /// <param name="alias">The alias exactly as the caller submitted it.</param>
    /// <returns>
    /// <see langword="true"/> when the alias is an acceptable authority, optionally followed by a
    /// port and a path; otherwise <see langword="false"/>. An absent or blank value returns
    /// <see langword="true"/> so that the required-value rule owns that condition alone and a
    /// caller who omitted the field is told once rather than twice.
    /// </returns>
    /// <remarks>
    /// <para>
    /// Rejected outright: any white space, because a host name cannot contain one and a value that
    /// only looks acceptable once trimmed would be stored differently from how it reads; any
    /// control character; a backslash, which covers the network-share prefix the legacy screen
    /// stripped; a scheme separator, which the help text instructs an operator to omit; the
    /// user-information marker, so that a credential cannot be smuggled into an authority; and the
    /// query and fragment markers, which address a request rather than name a host.
    /// </para>
    /// <para>
    /// Rejected in the path: a leading or trailing separator, an empty segment, and any segment
    /// that is a current-directory or parent-directory reference. The last of those is what keeps a
    /// traversal sequence out of a value that is later composed into a URL.
    /// </para>
    /// </remarks>
    internal static bool IsAcceptable(string? alias)
    {
        if (string.IsNullOrWhiteSpace(alias))
        {
            return true;
        }

        if (!ContainsOnlyPermittedCharacters(alias))
        {
            return false;
        }

        int pathStart = alias.IndexOf(PathSeparator, StringComparison.Ordinal);
        string authority = pathStart < 0 ? alias : alias[..pathStart];

        if (!IsAcceptableAuthority(authority))
        {
            return false;
        }

        return pathStart < 0 || IsAcceptablePath(alias[(pathStart + 1)..]);
    }

    /// <summary>
    /// Reports whether every character of an alias is one an alias may contain at all, before any
    /// question of position is considered.
    /// </summary>
    /// <param name="alias">The submitted alias, already known to hold text.</param>
    /// <returns><see langword="true"/> when no forbidden character is present.</returns>
    private static bool ContainsOnlyPermittedCharacters(string alias)
    {
        // MIGRATION: rejecting the two prefixes the legacy screen SILENTLY STRIPPED is a deliberate
        // divergence. EditPortalAlias.ascx.vb removed everything up to and including a scheme
        // separator at L210-L212 and everything up to and including a network-share prefix at
        // L213-L215, so a submitted "http://www.example.com" was stored as "www.example.com"
        // without the operator being told. A screen can rewrite what it just showed a human; a JSON
        // contract cannot, because the caller would receive success and then read back a value it
        // never sent. The help text at EditPortalAlias.ascx.resx:L124 already instructed the
        // operator not to send a protocol prefix, so refusing one enforces the documented contract
        // rather than tightening it.
        for (int index = 0; index < alias.Length; index++)
        {
            char character = alias[index];

            if (char.IsWhiteSpace(character) || char.IsControl(character))
            {
                return false;
            }

            if (character is '\\' or '@' or '?' or '#')
            {
                return false;
            }
        }

        return !alias.Contains("://", StringComparison.Ordinal);
    }

    /// <summary>
    /// Reports whether the authority part of an alias - the host name, with an optional port - is
    /// acceptable.
    /// </summary>
    /// <param name="authority">The text before the first path separator.</param>
    /// <returns><see langword="true"/> when the authority is acceptable.</returns>
    private static bool IsAcceptableAuthority(string authority)
    {
        int portSeparator = authority.IndexOf(PortSeparator, StringComparison.Ordinal);
        string host = portSeparator < 0 ? authority : authority[..portSeparator];

        if (!IsAcceptableHost(host))
        {
            return false;
        }

        if (portSeparator < 0)
        {
            return true;
        }

        string port = authority[(portSeparator + 1)..];

        if (port.Length == 0 || port.Length > 5)
        {
            return false;
        }

        for (int index = 0; index < port.Length; index++)
        {
            if (!char.IsAsciiDigit(port[index]))
            {
                return false;
            }
        }

        return int.TryParse(port, NumberStyles.None, CultureInfo.InvariantCulture, out int portNumber)
            && portNumber > 0
            && portNumber <= MaximumPortNumber;
    }

    /// <summary>
    /// Reports whether a host name is acceptable, admitting the four forms the legacy help text
    /// names.
    /// </summary>
    /// <param name="host">The host part of the authority.</param>
    /// <returns><see langword="true"/> when the host is acceptable.</returns>
    /// <remarks>
    /// One vocabulary serves all four measured forms: a single label covers a local address and a
    /// server name, a dotted sequence of labels covers both a domain name and a version-four
    /// address, so no separate numeric rule is needed and none is asserted. A label may not begin
    /// or end with a hyphen and no label may be empty, which is what refuses a leading, trailing or
    /// doubled dot.
    /// </remarks>
    private static bool IsAcceptableHost(string host)
    {
        if (host.Length == 0)
        {
            return false;
        }

        int labelLength = 0;

        for (int index = 0; index < host.Length; index++)
        {
            char character = host[index];

            if (character == '.')
            {
                if (labelLength == 0 || host[index - 1] == '-')
                {
                    return false;
                }

                labelLength = 0;
                continue;
            }

            if (!char.IsAsciiLetterOrDigit(character) && character != '-')
            {
                return false;
            }

            if (labelLength == 0 && character == '-')
            {
                return false;
            }

            labelLength++;
        }

        return labelLength > 0 && host[^1] != '-';
    }

    /// <summary>
    /// Reports whether the path part of an alias is acceptable.
    /// </summary>
    /// <param name="path">The text after the first path separator, which may be empty.</param>
    /// <returns><see langword="true"/> when every segment is acceptable.</returns>
    /// <remarks>
    /// An empty path means the alias ended with a separator, which is refused: the stored value is
    /// composed into an address, and a trailing separator would produce a doubled one. The segment
    /// vocabulary is deliberately narrow - letters, digits, hyphen, underscore and dot - because a
    /// child-portal path is a name rather than a request.
    /// </remarks>
    private static bool IsAcceptablePath(string path)
    {
        if (path.Length == 0)
        {
            return false;
        }

        foreach (string segment in path.Split(PathSeparator))
        {
            if (segment.Length == 0
                || string.Equals(segment, CurrentSegment, StringComparison.Ordinal)
                || string.Equals(segment, ParentSegment, StringComparison.Ordinal))
            {
                return false;
            }

            for (int index = 0; index < segment.Length; index++)
            {
                char character = segment[index];

                if (!char.IsAsciiLetterOrDigit(character) && character is not ('-' or '_' or '.'))
                {
                    return false;
                }
            }
        }

        return true;
    }
}
