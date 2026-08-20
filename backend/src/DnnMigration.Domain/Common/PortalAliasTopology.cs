namespace DnnMigration.Domain.Common;

/// <summary>
/// The one definition of how a portal may be ADDRESSED beneath a shared host name: how deep an alias path
/// may go, which characters a path segment may carry, and which segments the deployment itself owns and a
/// tenant therefore may not.
/// </summary>
/// <remarks>
/// <para>
/// <strong>WHY THIS LIVES IN THE DOMAIN LAYER.</strong> Five components independently decide whether a path
/// segment names a tenant, and each of them is in a different layer or a different language: the alias
/// write path validates a submitted value (Application), the request pipeline resolves an arriving address
/// against stored aliases (Infrastructure), the single-page application detects the prefix its own document
/// was served under (TypeScript), the screen that binds an alias mirrors the server's shape rule for a
/// field message (TypeScript), and the reverse proxy matches the API location (nginx).
/// </para>
/// <para>
/// <strong>THE RESERVED SET IS AN ADDRESSING FACT, NOT A PREFERENCE.</strong> A segment that names one of
/// this deployment's own roots cannot also name a tenant, because the browser and the proxy resolve it as
/// the root before any tenant lookup happens. Storing such an alias would create a row that is unreachable
/// and, worse, would make the console's own screens unreachable for the tenant that owned it.
/// </para>
/// </remarks>
public static class PortalAliasTopology
{
    /// <summary>The separator that introduces a path segment beneath an alias authority.</summary>
    public const char PathSeparator = '/';

    /// <summary>The greatest number of path segments an alias may carry beneath its authority.</summary>
    public const int MaximumPathSegments = 1;

    /// <summary>The additional characters a path segment may carry beyond ASCII letters and digits.</summary>
    /// <remarks>
    /// Hyphen and underscore only. The dot is deliberately absent - see the type's remarks - and so are the
    /// current-directory and parent-directory references, which cannot be spelled at all without it and
    /// therefore need no rule of their own.
    /// </remarks>
    private const string SegmentExtraCharacters = "-_";

    /// <summary>The path segments this deployment owns, and which an alias therefore may not use.</summary>
    /// <remarks>
    /// The last four are the roots the API itself answers: <c>api</c> carries every versioned endpoint, and
    /// <c>health</c>, <c>openapi</c> and <c>swagger</c> are the three prefixes the request pipeline exempts
    /// from tenant resolution altogether. A unit test asserts that every exempted prefix appears here, so
    /// the two lists cannot drift apart.
    /// </remarks>
    public static readonly IReadOnlySet<string> ReservedPathSegments =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "login",
            "modules",
            "portals",
            "role-groups",
            "roles",
            "settings",
            "users",
            "api",
            "health",
            "openapi",
            "swagger",
        };

    /// <summary>Reports whether a path segment is one this deployment owns.</summary>
    /// <param name="segment">A single path segment, without separators.</param>
    /// <returns>
    /// <see langword="true"/> when the segment names one of this deployment's own roots and so cannot name
    /// a tenant.
    /// </returns>
    public static bool IsReservedPathSegment(string? segment) =>
        segment is not null && ReservedPathSegments.Contains(segment);

    /// <summary>Reports whether a single path segment could name a tenant.</summary>
    /// <param name="segment">A single path segment, without separators.</param>
    /// <returns>
    /// <see langword="true"/> when the segment is non-empty, carries only ASCII letters, digits, hyphens
    /// and underscores, and is not one of <see cref="ReservedPathSegments"/>.
    /// </returns>
    /// <remarks>
    /// This is the predicate the alias write path enforces and the request pipeline consults, and it must
    /// be the same predicate for both: a segment the writer stores but the reader will not consider
    /// produces an unreachable tenant, and a segment the reader considers but the writer refuses produces a
    /// lookup that can never match.
    /// </remarks>
    public static bool IsAddressableSegment(string? segment)
    {
        if (string.IsNullOrEmpty(segment))
        {
            return false;
        }

        for (int index = 0; index < segment.Length; index++)
        {
            char character = segment[index];

            if (!char.IsAsciiLetterOrDigit(character)
                && !SegmentExtraCharacters.Contains(character, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return !IsReservedPathSegment(segment);
    }

    /// <summary>Reports whether the path portion of an alias is one this deployment can address.</summary>
    /// <param name="path">The text after the FIRST path separator of an alias, which may be empty.</param>
    /// <returns>
    /// <see langword="true"/> when the path carries no more than <see cref="MaximumPathSegments"/> segments
    /// and every segment is addressable.
    /// </returns>
    public static bool IsAcceptablePath(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return false;
        }

        string[] segments = path.Split(PathSeparator);

        if (segments.Length > MaximumPathSegments)
        {
            return false;
        }

        foreach (string segment in segments)
        {
            if (!IsAddressableSegment(segment))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Reports whether a whole alias is within the topology this deployment can deliver, judging its DEPTH
    /// and its path segments and saying nothing about the grammar of its authority.
    /// </summary>
    /// <param name="alias">The alias exactly as it was submitted or stored.</param>
    /// <returns>
    /// <see langword="true"/> when the alias carries no path at all, or carries a path this deployment can
    /// address.
    /// </returns>
    public static bool IsSupportedAddress(string? alias)
    {
        if (string.IsNullOrWhiteSpace(alias))
        {
            return true;
        }

        int pathStart = alias.IndexOf(PathSeparator, StringComparison.Ordinal);

        return pathStart < 0 || IsAcceptablePath(alias[(pathStart + 1)..]);
    }
}
