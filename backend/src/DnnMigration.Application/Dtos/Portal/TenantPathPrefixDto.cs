namespace DnnMigration.Application.Dtos.Portal;

/// <summary>
/// The answer to one question a browser cannot answer for itself: does a single leading path segment name a
/// configured tenant beneath the host that served the document?
/// </summary>
/// <remarks>
/// <para>
/// <strong>WHY A CLIENT HAS TO ASK.</strong> A portal may be addressed as a path beneath a shared host, so
/// <c>https://example.test/marketing</c> and <c>https://example.test/</c> can be two different tenants.
/// A single-page application therefore has to decide, before its router is created, whether the first
/// segment of its own address is a tenant prefix to be held aside or part of a route to be matched. The
/// shape rules in <c>PortalAliasTopology</c> narrow the question but cannot settle it: a typo and a real
/// child alias are the same shape, and only the stored alias set distinguishes them. Guessing produced a
/// measured defect - a mistyped one-segment address was claimed as a tenant, the router never saw the
/// address it was given, the not-found view was unreachable and every subsequent request was issued beneath
/// a prefix no portal owned.
/// </para>
/// <para>
/// <strong>WHY THIS IS ANONYMOUS, AND WHY THAT DISCLOSES NOTHING NEW.</strong> The question has to be
/// answerable before anyone has signed in, because the address that needs resolving is frequently the
/// sign-in address itself. It is not an enumeration surface: whether a portal answers at a given address is
/// already observable by visiting it, which is what an address IS. Nothing here reports a portal's
/// identity, its name or any of its settings - only whether the segment is addressable at all.
/// </para>
/// </remarks>
public sealed class TenantPathPrefixDto
{
    /// <summary>The segment that was asked about, echoed back exactly as it was received.</summary>
    /// <remarks>
    /// Echoed so a caller can match an answer to its question without holding request state, and so a
    /// cached or proxied response cannot be mistaken for an answer about a different segment.
    /// </remarks>
    public string Segment { get; set; } = string.Empty;

    /// <summary>
    /// Whether the segment names a configured tenant beneath the requesting host, and may therefore be held
    /// aside as a path prefix rather than matched as a route.
    /// </summary>
    public bool IsTenantPath { get; set; }
}
