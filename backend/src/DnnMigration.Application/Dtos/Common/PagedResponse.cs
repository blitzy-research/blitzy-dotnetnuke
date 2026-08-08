using DnnMigration.Domain.Common;

namespace DnnMigration.Application.Dtos.Common;

// MIGRATION: this envelope retires the legacy `ByRef totalRecords As Integer` status argument and the
// untyped, pre-generics collection returns that accompanied it. A status argument is not expressible over
// HTTP, and a caller of those members could not learn what it was holding or how much more there was.
// Pairing the rows with their grand total also removes the second round trip the legacy membership surface
// needed, since its paged readers carried no total at all.
//
// MIGRATION: page indexing is ZERO-BASED, matching PagedResult<T> and PagedRequest, so the request, the
// domain envelope and this reply all count from the same place. The legacy stack was split - its screens
// counted from one and subtracted one before calling down, while its paging procedures multiplied page size
// by page index and so addressed the first row at zero. A disagreement here would neither fail to compile
// nor fail a test asserting a successful response; it would quietly serve the neighbouring page, which is
// why the base is restated on the member itself.
//
// MIGRATION: the legacy "return everything, unpaged" shape, expressed by passing the integer null sentinel
// of -1 as the index, size and total alike, is not reproduced. No negative coordinate is accepted or
// emitted: `From` projects the unpaged case onto real coordinates and both `Empty` factories reject a
// negative argument, so no consumer has to recognise a sentinel. The total stays `int` because every legacy
// declaration of it is `Integer`.
//
// MIGRATION: nothing rewrites the payload - no serialisation attribute, no conditional-omission policy. The
// legacy null contract represents an absent string as the empty string and an absent integer as -1, so
// omitting nulls or defaults would change what an existing consumer reads and would erase a legitimate total
// of zero or a legitimately empty page.

/// <summary>
/// The wire shape of one page of results: the rows themselves, plus the paging metadata describing
/// where that page sits within the whole match set.
/// </summary>
/// <typeparam name="T">
/// The row contract carried on the page: always a data transfer object from
/// <c>Application/Dtos/</c> and never a persisted entity.
/// </typeparam>
/// <remarks>
/// The wire shape of every paged endpoint is <c>{ "items": [...], "meta": {...} }</c>, applied in one
/// place at the API edge so the domain paging type never crosses the boundary. A page is never
/// additionally wrapped in <c>ApiResponse&lt;T&gt;</c>, which would nest two envelopes and put the row
/// array one level deeper than the contract states.
/// <para>
/// Page indexing is zero-based, matching both <see cref="PagedResult{T}"/> and
/// <see cref="PagedRequest"/>, so a client may echo the index it sent without arithmetic. Prefer
/// <see cref="From(PagedResult{T})"/> to assembling an instance by hand: projecting a domain envelope
/// keeps the total, index and size consistent with the query that produced them.
/// </para>
/// <para>
/// Immutability is SHALLOW, not thread safety: both members are initialise-only, but
/// <see cref="Meta"/> points at an <see cref="ApiMeta"/> whose members are settable, so a shared
/// envelope can have its coordinates changed underneath a reader. Keep an instance to one thread, or
/// never touch its metadata after construction.
/// </para>
/// </remarks>
public sealed class PagedResponse<T>
{
    /// <summary>Gets the rows on this page, in the order the query produced them.</summary>
    /// <remarks>
    /// Never null and never absent from a serialised body: an empty page is a legitimate answer,
    /// and a client tells "past the end" from "nothing matched" through
    /// <see cref="ApiMeta.TotalCount"/>. A read-only list rather than a deferred sequence, which
    /// would hide the count this envelope exists to publish and could keep a database connection
    /// open past the repository that opened it.
    /// </remarks>
    public IReadOnlyList<T> Items { get; init; } = [];

    /// <summary>
    /// Gets the paging metadata locating this page within the whole match set: the total number of
    /// records that satisfy the criteria, the zero-based page index, the page size applied, and the
    /// page count derived from them.
    /// </summary>
    /// <remarks>
    /// The paging facts live here and only here; restating one on the envelope would give a pager
    /// two sources of truth. <see cref="ApiMeta.TotalCount"/> counts every record that satisfies
    /// the criteria, not the entries in <see cref="Items"/> - the two coincide only when the whole
    /// match set fits on one page.
    /// </remarks>
    public ApiMeta Meta { get; init; } = new();

    /// <summary>
    /// Projects a domain paging envelope onto the wire, carrying the same rows and the same paging
    /// facts.
    /// </summary>
    /// <param name="result">The domain envelope an application service produced.</param>
    /// <returns>
    /// A wire envelope reporting the rows and coordinates of <paramref name="result"/>.
    /// </returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="result"/> is <see langword="null"/>: a missing envelope is a defect at the
    /// call site, never a domain failure.
    /// </exception>
    /// <remarks>
    /// The rows are taken directly rather than copied, which is safe because
    /// <see cref="PagedResult{T}"/> already publishes an immutable snapshot.
    /// <para>
    /// An unpaged envelope is the one place a value is reshaped, and only the page size. The domain
    /// type signals unpaged with a page size of zero, but on the wire that would drive the derived
    /// page count to zero for a response that plainly contains records; reporting the size as the
    /// total describes the truthful shape - one page holding everything - and keeps every derived
    /// value consistent.
    /// </para>
    /// </remarks>
    public static PagedResponse<T> From(PagedResult<T> result)
    {
        ArgumentNullException.ThrowIfNull(result);

        return new PagedResponse<T>
        {
            Items = result.Items,
            Meta = new ApiMeta
            {
                TotalCount = result.TotalCount,
                PageIndex = result.PageIndex,
                PageSize = result.IsUnpaged ? result.TotalCount : result.PageSize,
            },
        };
    }

    /// <summary>
    /// Creates a zero-record envelope with no paging coordinates, for a collection endpoint that
    /// legitimately matched nothing and applied no window.
    /// </summary>
    /// <returns>An envelope carrying no rows, a total of zero and zero coordinates.</returns>
    /// <remarks>
    /// Returns a fresh envelope each time rather than a cached instance, because
    /// <see cref="ApiMeta"/> is mutable: one caller could alter a shared envelope through
    /// <see cref="Meta"/> and every later caller would observe it altered.
    /// </remarks>
    public static PagedResponse<T> Empty() => new();

    /// <summary>
    /// Creates a zero-record envelope that still reports the page that was asked for, so a client
    /// can render an accurate pager for a requested page that happened to match nothing.
    /// </summary>
    /// <param name="pageIndex">
    /// The zero-based page of records the caller asked for. 0 is the first page.
    /// </param>
    /// <param name="pageSize">The size of the page the caller asked for.</param>
    /// <returns>
    /// An envelope carrying no rows and a total of zero, at the supplied coordinates.
    /// </returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="pageIndex"/> or <paramref name="pageSize"/> is negative, so the legacy
    /// integer null sentinel cannot arrive disguised as a page address and be published as one.
    /// </exception>
    /// <remarks>
    /// Preferred over <see cref="Empty()"/> whenever a window was genuinely applied: discarding the
    /// requested coordinates leaves a client unable to show which page it is looking at.
    /// </remarks>
    public static PagedResponse<T> Empty(int pageIndex, int pageSize)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(pageIndex);
        ArgumentOutOfRangeException.ThrowIfNegative(pageSize);

        return new PagedResponse<T>
        {
            Meta = new ApiMeta
            {
                TotalCount = 0,
                PageIndex = pageIndex,
                PageSize = pageSize,
            },
        };
    }
}
