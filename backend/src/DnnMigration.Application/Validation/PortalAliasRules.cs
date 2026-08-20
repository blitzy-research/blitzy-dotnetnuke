using System.Globalization;

using DnnMigration.Domain.Common;

namespace DnnMigration.Application.Validation;

/// <summary>
/// The shape rules for a portal HTTP alias, declared once and applied by both the create and the update
/// alias contracts.
/// </summary>
/// <remarks>
/// <b>One definition, two callers.</b> An alias submitted on create and an alias submitted on update are
/// the same value bound for the same uniquely indexed column, so a rule that held on one path and not the
/// other would be a rule that could be bypassed by choosing the other verb. Both validators call the
/// members below rather than restating them.
/// </remarks>
internal static class PortalAliasRules
{
    /// <summary>
    /// The longest alias that can be stored, taken from the terminal width of <c>PortalAlias.HTTPAlias</c>,
    /// declared <c>[nvarchar] (200)</c> at
    /// <c>Website/Providers/DataProviders/SqlDataProvider/02.02.02.SqlDataProvider:L3807</c>.
    /// </summary>
    internal const int MaximumLength = 200;

    /// <summary>The largest port number an authority may name.</summary>
    internal const int MaximumPortNumber = 65535;

    /// <summary>Message reported when no alias was supplied.</summary>
    internal const string RequiredMessage = "An HTTP alias is required.";

    /// <summary>Message reported when an alias is not one of the forms this application can store.</summary>
    internal const string InvalidMessage =
        "An HTTP alias must be a host name, an IP address or a server name, optionally followed by "
        + "a port and a path, and must not include a protocol prefix.";

    /// <summary>The separator that introduces a port.</summary>
    private const char PortSeparator = ':';

    /// <summary>
    /// Message reported when an alias exceeds what the column can hold, composed from the bound so the
    /// figure is stated in exactly one place.
    /// </summary>
    internal static readonly string TooLongMessage = FormattableString.Invariant(
        $"An HTTP alias may not exceed {MaximumLength} characters.");

    /// <summary>
    /// Message reported when an alias names a path this deployment cannot address, composed from <see
    /// cref="PortalAliasTopology"/> so the bound and the reserved words are stated in exactly one place.
    /// </summary>
    internal static readonly string UnsupportedPathMessage =
        "An HTTP alias may carry at most "
        + PortalAliasTopology.MaximumPathSegments.ToString(CultureInfo.InvariantCulture)
        + " path segment beneath its host name; that segment may contain only letters, digits, "
        + "hyphens and underscores, and may not be one of the addresses this application reserves "
        + "for itself ("
        + string.Join(
            ", ",
            PortalAliasTopology.ReservedPathSegments.OrderBy(
                segment => segment,
                StringComparer.Ordinal))
        + ").";

    /// <summary>Reports whether a submitted alias is a form this application can store.</summary>
    /// <param name="alias">The alias exactly as the caller submitted it.</param>
    /// <returns>
    /// <see langword="true"/> when the alias is an acceptable authority, optionally followed by a port and
    /// a path; otherwise <see langword="false"/>.
    /// </returns>
    /// <remarks>
    /// THE PATH RULE IS NOT DECLARED HERE, and that is the point of it. Four other components decide the
    /// same question - the request pipeline that resolves an arriving address, the browser that detects the
    /// prefix its document was served under, the screen that mirrors this rule for a field message, and the
    /// reverse proxy that matches the API location - and they disagreed.
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

        int pathStart = alias.IndexOf(PortalAliasTopology.PathSeparator, StringComparison.Ordinal);
        string authority = pathStart < 0 ? alias : alias[..pathStart];

        if (!IsAcceptableAuthority(authority))
        {
            return false;
        }

        return pathStart < 0 || PortalAliasTopology.IsAcceptablePath(alias[(pathStart + 1)..]);
    }

    /// <summary>Reports whether an alias names a path this deployment can actually deliver a request to.</summary>
    /// <param name="alias">The alias exactly as the caller submitted it.</param>
    /// <returns>
    /// <see langword="true"/> when the alias carries no path, or carries a path within <see
    /// cref="PortalAliasTopology"/>.
    /// </returns>
    /// <remarks>
    /// A thin delegation, and it exists for one reason: both alias validators are documented to read every
    /// rule they apply from this type, so a rule they had to reach into another layer for would be a rule a
    /// reader of those validators could miss. The rule itself belongs to the Domain layer because the
    /// request pipeline and the browser enforce the same bound and neither of them can see this one.
    /// </remarks>
    internal static bool IsWithinSupportedTopology(string? alias) =>
        PortalAliasTopology.IsSupportedAddress(alias);

    /// <summary>
    /// Reports whether every character of an alias is one an alias may contain at all, before any question
    /// of position is considered.
    /// </summary>
    /// <param name="alias">The submitted alias, already known to hold text.</param>
    /// <returns><see langword="true"/> when no forbidden character is present.</returns>
    private static bool ContainsOnlyPermittedCharacters(string alias)
    {
        // MIGRATION: rejecting the two prefixes the legacy screen SILENTLY STRIPPED is a deliberate
        // divergence.
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
    /// Reports whether a host name is acceptable, admitting the four forms the legacy help text names.
    /// </summary>
    /// <param name="host">The host part of the authority.</param>
    /// <returns><see langword="true"/> when the host is acceptable.</returns>
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
}
