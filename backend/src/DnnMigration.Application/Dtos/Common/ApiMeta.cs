namespace DnnMigration.Application.Dtos.Common;

/// <summary>
/// Envelope metadata companion to <c>ApiResponse</c> and <c>PagedResponse</c>, describing a
/// successful API response rather than the payload it carries.
/// </summary>
/// <remarks>
/// Appears on the wire as those envelopes' <c>meta</c> member: a paged endpoint returns
/// <c>{ "items": [...], "meta": {...} }</c> and populates every member here, while a single-resource
/// endpoint returns <c>{ "data": {...} }</c> and omits <c>meta</c>, having no page to describe. The
/// member set is confined to the three facts the legacy pager consumed - total records, page size and
/// current page - plus the one value derived from them.
/// <para>
/// This type describes success only: an expected failure is carried by the domain result types and an
/// unexpected one becomes an RFC 7807 problem document at the API edge, so there is no outcome flag,
/// message or per-field validation map here. Instances are plain mutable data built once per response,
/// so they are not thread-safe and are not intended to be shared.
/// </para>
/// </remarks>
// MIGRATION: no correlation identifier member is present. The request correlation value travels in the
// X-Correlation-Id header, written by the API-layer middleware and read by the matching client interceptor;
// repeating it in the body would create a second source of truth that no client consults.
public sealed class ApiMeta
{
    /// <summary>
    /// Gets or sets the total number of records that satisfy the criteria, counted across every
    /// page and not only the page returned.
    /// </summary>
    /// <remarks>
    /// MIGRATION: retires the <c>ByRef totalRecords</c> out-parameter of the legacy paged reads.
    /// The width stays <c>int</c> because every legacy declaration of it is <c>Integer</c>.
    /// </remarks>
    public int TotalCount { get; set; }

    /// <summary>Gets or sets the page of records returned, counted from zero.</summary>
    /// <remarks>
    /// MIGRATION: zero-based is the legacy DATA layer's convention, not its screens' - they counted
    /// from one and subtracted before calling down. Carrying the data-layer base here keeps the
    /// wire contract and the repository in agreement; the one-based presentation counter exists
    /// only inside the client pagination component.
    /// </remarks>
    public int PageIndex { get; set; }

    /// <summary>
    /// Gets or sets the size of the page that produced the payload: the size the server actually
    /// applied.
    /// </summary>
    /// <remarks>
    /// Always equals the size the caller asked for. <c>PagedRequestValidator</c> REJECTS an
    /// out-of-range size rather than clamping it, so the server never substitutes a size of its own
    /// and this member never disagrees with the request.
    /// </remarks>
    public int PageSize { get; set; }

    /// <summary>
    /// Gets the number of pages that <see cref="TotalCount"/> divides into at the current
    /// <see cref="PageSize"/>, or zero when there is nothing to page.
    /// </summary>
    /// <remarks>
    /// Derived, and therefore without a setter: the value must not be able to contradict the two
    /// members it follows from, and a serialiser recomputes it when reading the envelope back. Both
    /// guards are arithmetic rather than absence tests, and the division is a quotient plus a
    /// remainder test rather than adding the page size to the total first, which would overflow
    /// near <c>int.MaxValue</c>.
    /// </remarks>
    public int TotalPages => PageSize > 0 && TotalCount > 0
        ? (TotalCount / PageSize) + ((TotalCount % PageSize) > 0 ? 1 : 0)
        : 0;
}
