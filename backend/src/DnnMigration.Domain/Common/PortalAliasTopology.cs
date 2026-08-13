namespace DnnMigration.Domain.Common;

/// <summary>
/// The one definition of how a portal may be ADDRESSED beneath a shared host name: how deep an
/// alias path may go, which characters a path segment may carry, and which segments the deployment
/// itself owns and a tenant therefore may not.
/// </summary>
/// <remarks>
/// <para>
/// <strong>WHY THIS LIVES IN THE DOMAIN LAYER.</strong> Five components independently decide whether
/// a path segment names a tenant, and each of them is in a different layer or a different language:
/// the alias write path validates a submitted value (Application), the request pipeline resolves an
/// arriving address against stored aliases (Infrastructure), the single-page application detects the
/// prefix its own document was served under (TypeScript), the screen that binds an alias mirrors the
/// server's shape rule for a field message (TypeScript), and the reverse proxy matches the API
/// location (nginx). Four of the five held DIFFERENT rules, and the disagreement was a
/// tenant-isolation defect rather than an inconsistency: the write path admitted addresses of
/// unbounded depth with dots in every segment, the resolver considered four segments and then fell
/// back to the bare host when none matched, and the proxy could only ever deliver one segment. An
/// alias the server accepted could therefore be undeliverable, and an address that named no tenant
/// at all was answered by the PARENT tenant. The rule is stated once, here, in the only layer every
/// server-side component already references, and the two TypeScript mirrors and the proxy comment
/// point at this file by name.
/// </para>
/// <para>
/// <strong>ONE SEGMENT, MEASURED RATHER THAN CHOSEN.</strong> The legacy signup screen composed and
/// stored exactly <c>domain/segment</c> - <c>Website/admin/Portal/Signup.ascx.vb:L232-L236</c> - so
/// one segment is the whole of what the legacy product could produce, and the shipped reverse-proxy
/// location matches exactly one optional segment ahead of <c>/api/</c>. Accepting a deeper alias
/// would store a value that no request could ever be routed to, which is why the bound is enforced
/// on the write path and not merely assumed by the reader.
/// </para>
/// <para>
/// <strong>A DOT IS NOT PERMITTED IN A PATH SEGMENT, AND THAT IS A DELIBERATE TIGHTENING.</strong>
/// An earlier revision admitted one, so <c>host/acme.co</c> was a storable alias. That is what
/// forced the browser to carry a closed list of nineteen document extensions: with dots admitted,
/// <c>/context.html</c> and <c>/main-ABCD1234.js</c> were indistinguishable from a tenant prefix by
/// shape alone, and any extension the list omitted - <c>.php</c>, <c>.webmanifest</c> - was read as
/// a tenant. Refusing the dot outright makes the two categories disjoint by construction: a segment
/// the server can store can never look like a served file, and a served file can never be mistaken
/// for a tenant. The divergence is recorded in <c>MIGRATION_NOTES.md</c>; it narrows only what a
/// host account could type by hand, since the legacy screen's own child vocabulary was
/// <c>abcdefghijklmnopqrstuvwxyz0123456789-</c> (<c>Signup.ascx.vb:L192</c>) and carried no dot
/// either.
/// </para>
/// <para>
/// <strong>THE RESERVED SET IS AN ADDRESSING FACT, NOT A PREFERENCE.</strong> A segment that names
/// one of this deployment's own roots cannot also name a tenant, because the browser and the proxy
/// resolve it as the root before any tenant lookup happens. Storing such an alias would create a
/// row that is unreachable and, worse, would make the console's own screens unreachable for the
/// tenant that owned it. Both halves of the set are therefore refused on the write path rather than
/// merely tolerated by the reader.
/// </para>
/// <para>
/// Pure and allocation-light: every member below walks spans and asks the file system, the clock and
/// the network nothing at all, which is what allows the Application, Infrastructure and API layers
/// to share it without any of them acquiring a dependency.
/// </para>
/// </remarks>
public static class PortalAliasTopology
{
    /// <summary>
    /// The separator that introduces a path segment beneath an alias authority.
    /// </summary>
    public const char PathSeparator = '/';

    /// <summary>
    /// The greatest number of path segments an alias may carry beneath its authority.
    /// </summary>
    /// <remarks>
    /// One, because one is what the legacy signup screen composed
    /// (<c>Website/admin/Portal/Signup.ascx.vb:L232-L236</c>) and one is what the shipped reverse
    /// proxy can deliver. Raising this figure requires the proxy's own matcher and both browser-side
    /// mirrors to be widened in the same change, which is why the bound is stated here rather than
    /// separately in each of them.
    /// </remarks>
    public const int MaximumPathSegments = 1;

    /// <summary>
    /// The additional characters a path segment may carry beyond ASCII letters and digits.
    /// </summary>
    /// <remarks>
    /// Hyphen and underscore only. The dot is deliberately absent - see the type's remarks - and so
    /// are the current-directory and parent-directory references, which cannot be spelled at all
    /// without it and therefore need no rule of their own.
    /// </remarks>
    private const string SegmentExtraCharacters = "-_";

    /// <summary>
    /// The path segments this deployment owns, and which an alias therefore may not use.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Two groups, both load-bearing. The first seven are the single-page application's own
    /// top-level routes, fixed by AAP section 0.4.4's closed twenty-five-address route table and
    /// pinned against the route table itself by <c>frontend/src/app/core/config/tenant-path.spec.ts</c>.
    /// A tenant whose segment spelled one of them would be read as the console's own screen by a
    /// browser that has not yet spoken to the API - the document served for both addresses is the
    /// same document, so nothing on that side can tell them apart.
    /// </para>
    /// <para>
    /// The last four are the roots the API itself answers: <c>api</c> carries every versioned
    /// endpoint, and <c>health</c>, <c>openapi</c> and <c>swagger</c> are the three prefixes the
    /// request pipeline exempts from tenant resolution altogether. A unit test asserts that every
    /// exempted prefix appears here, so the two lists cannot drift apart.
    /// </para>
    /// <para>
    /// Compared without regard to case, because an address may be typed in any case while a stored
    /// alias is persisted as it was authored.
    /// </para>
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

    /// <summary>
    /// Reports whether a path segment is one this deployment owns.
    /// </summary>
    /// <param name="segment">A single path segment, without separators.</param>
    /// <returns>
    /// <see langword="true"/> when the segment names one of this deployment's own roots and so
    /// cannot name a tenant.
    /// </returns>
    public static bool IsReservedPathSegment(string? segment) =>
        segment is not null && ReservedPathSegments.Contains(segment);

    /// <summary>
    /// Reports whether a single path segment could name a tenant.
    /// </summary>
    /// <param name="segment">A single path segment, without separators.</param>
    /// <returns>
    /// <see langword="true"/> when the segment is non-empty, carries only ASCII letters, digits,
    /// hyphens and underscores, and is not one of <see cref="ReservedPathSegments"/>.
    /// </returns>
    /// <remarks>
    /// This is the predicate the alias write path enforces and the request pipeline consults, and it
    /// must be the same predicate for both: a segment the writer stores but the reader will not
    /// consider produces an unreachable tenant, and a segment the reader considers but the writer
    /// refuses produces a lookup that can never match.
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

    /// <summary>
    /// Reports whether the path portion of an alias is one this deployment can address.
    /// </summary>
    /// <param name="path">
    /// The text after the FIRST path separator of an alias, which may be empty.
    /// </param>
    /// <returns>
    /// <see langword="true"/> when the path carries no more than
    /// <see cref="MaximumPathSegments"/> segments and every segment is addressable.
    /// </returns>
    /// <remarks>
    /// An empty path is refused. It means the alias ended with a separator, and the stored value is
    /// composed into an address, so a trailing separator would produce a doubled one.
    /// </remarks>
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
    /// Reports whether a whole alias is within the topology this deployment can deliver, judging its
    /// DEPTH and its path segments and saying nothing about the grammar of its authority.
    /// </summary>
    /// <param name="alias">The alias exactly as it was submitted or stored.</param>
    /// <returns>
    /// <see langword="true"/> when the alias carries no path at all, or carries a path this
    /// deployment can address. An absent or blank value returns <see langword="true"/>, so that a
    /// caller who omitted the field is told once by the rule that owns requiredness rather than
    /// twice.
    /// </returns>
    /// <remarks>
    /// Separated from authority grammar on purpose, so that the two callers who already own an
    /// authority rule of their own - the alias contracts and the portal creation contract, which
    /// carries the legacy screen's character set verbatim - can adopt this bound additively without
    /// either of them restating the other's vocabulary.
    /// </remarks>
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
