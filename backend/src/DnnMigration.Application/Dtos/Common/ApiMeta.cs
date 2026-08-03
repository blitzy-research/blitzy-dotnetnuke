namespace DnnMigration.Application.Dtos.Common;

/// <summary>
/// Envelope metadata companion to <c>ApiResponse</c>, describing a successful API response rather
/// than the payload it carries. Declared but NOT YET ADOPTED, along with the envelopes that compose
/// it.
/// </summary>
/// <remarks>
/// <para>
/// STATUS: no endpoint emits this type today. It is reached only through <c>ApiResponse</c> and
/// <c>PagedResponse</c>, neither of which any controller returns; the paged endpoints return the flat
/// <c>PagedResult&lt;T&gt;</c> instead. The intended contract, once those envelopes are adopted, is
/// that a collection endpoint populates every member while a single-item endpoint omits the metadata
/// altogether, because a scalar payload has no page to describe.
/// </para>
/// <para>
/// The member set is confined to the three facts the legacy pager consumed plus the one value
/// derived from them. The Web Forms user-administration screen handed its pager exactly total
/// records, page size and current page (<c>Website/admin/Users/Users.ascx.vb</c> L285-L287), and
/// the client pagination component that replaces it takes the same three as inputs.
/// </para>
/// <para>
/// This type describes success only: an expected failure is carried by the domain result types and
/// an unexpected one is shaped into an RFC 7807 problem document at the API edge, so there is no
/// outcome flag, message, per-field validation map or transport-level code here. Instances are
/// plain mutable data built once per response and discarded, so they are neither thread-safe nor
/// intended to be shared.
/// </para>
/// </remarks>
// MIGRATION: no correlation identifier member is present, and the omission is deliberate. The
// request correlation value travels in the X-Correlation-Id header, written by the API-layer
// correlation middleware and read by the matching client interceptor; repeating it in the response
// body would create a second source of truth that no client consults.
public sealed class ApiMeta
{
    /// <summary>
    /// Gets or sets the total number of records that satisfy the criteria, counted across every page
    /// and not only the page returned.
    /// </summary>
    /// <remarks>
    /// The wording is the legacy contract's own, which documented this value as "The total no of
    /// records that satisfy the criteria." on every one of its paged reads.
    /// </remarks>
    // MIGRATION: this member retires the ByRef totalRecords out-parameter carried by the eight paged
    // overloads on Library/Components/Users/UserController.vb, all of which returned an untyped,
    // pre-generics collection with the total handed back through the argument list. The width stays
    // int rather than long because every legacy declaration is Integer, including
    // GetUserCountByPortal on the membership data provider.
    public int TotalCount { get; set; }

    /// <summary>
    /// Gets or sets the page of records returned, counted from zero.
    /// </summary>
    // MIGRATION: the zero-based convention is the legacy DATA layer's, not the legacy screen's.
    // Website/admin/Users/Users.ascx.vb initialised its own page counter to one at L51 and subtracted
    // one before calling down at L265. This envelope carries the data-layer convention so that the
    // wire contract and the repository agree; the one-based presentation counter exists only inside
    // the client pagination component.
    public int PageIndex { get; set; }

    /// <summary>
    /// Gets or sets the size of the page that produced the payload: the size the server actually
    /// applied.
    /// </summary>
    /// <remarks>
    /// This value equals the size the caller asked for. Request validation REJECTS a page size outside
    /// its permitted range rather than clamping it - <c>PagedRequestValidator</c> requires a size
    /// greater than zero and no greater than one hundred, and an out-of-range request is refused as a
    /// validation problem instead of being quietly served at a different size - so the server never
    /// substitutes a size of its own and this member never disagrees with the request. The legacy
    /// screen read its page size from the <c>Records_PerPage</c> portal setting and echoed that same
    /// value back to the pager, which is the echoing behaviour this member preserves.
    /// </remarks>
    public int PageSize { get; set; }

    /// <summary>
    /// Gets the number of pages that <see cref="TotalCount"/> divides into at the current
    /// <see cref="PageSize"/>, or zero when there is nothing to page.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Derived, and therefore deliberately without a setter: the value follows from
    /// <see cref="TotalCount"/> and <see cref="PageSize"/> and must not be able to contradict them. A
    /// serialiser emits it alongside them and recomputes it when reading the envelope back, so the
    /// round trip is lossless. Both guards are arithmetic rather than absence tests - a page size of
    /// zero would divide by zero, and a page count can never be negative - so no value here is ever
    /// read as missing.
    /// </para>
    /// <para>
    /// The division is a quotient plus a remainder test rather than adding the page size to the total
    /// first, because that addition overflows as the total approaches <c>int.MaxValue</c>.
    /// </para>
    /// </remarks>
    // MIGRATION: never computed at all in the legacy code, which declared a TotalPages field
    // (Website/admin/Users/Users.ascx.vb L58), seeded it with the integer null sentinel of minus one
    // and then neither assigned nor read it again. This member replaces that dead field with a
    // deterministic derivation and never emits the sentinel, which a genuine page count must never
    // be confused with.
    public int TotalPages => PageSize > 0 && TotalCount > 0
        ? (TotalCount / PageSize) + ((TotalCount % PageSize) > 0 ? 1 : 0)
        : 0;
}
