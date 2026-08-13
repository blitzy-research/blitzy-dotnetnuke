using DnnMigration.Domain.Common;

namespace DnnMigration.Application.Dtos.Common;

// MIGRATION: the legacy "return everything, unpaged" shape, expressed by passing the integer null sentinel
// of -1 as the index, size and total alike, is not reproduced.

/// <summary>
/// The wire shape of one page of results: the rows themselves, plus the paging metadata describing where
/// that page sits within the whole match set.
/// </summary>
/// <typeparam name="T">
/// The row contract carried on the page: always a data transfer object from <c>Application/Dtos/</c> and
/// never a persisted entity.
/// </typeparam>
/// <remarks>
/// <para>
/// Page indexing is zero-based, matching both <see cref="PagedResult{T}"/> and <see cref="PagedRequest"/>,
/// so a client may echo the index it sent without arithmetic. Prefer <see cref="From(PagedResult{T})"/> to
/// assembling an instance by hand: projecting a domain envelope keeps the total, index and size consistent
/// with the query that produced them.
/// </para>
/// <para>
/// Immutability is SHALLOW, not thread safety: both members are initialise-only, but <see cref="Meta"/>
/// points at an <see cref="ApiMeta"/> whose members are settable, so a shared envelope can have its
/// coordinates changed underneath a reader. Keep an instance to one thread, or never touch its metadata
/// after construction.
/// </para>
/// </remarks>
public sealed class PagedResponse<T>
{
    /// <summary>Gets the rows on this page, in the order the query produced them.</summary>
    public IReadOnlyList<T> Items { get; init; } = [];

    /// <summary>
    /// Gets the paging metadata locating this page within the whole match set: the total number of records
    /// that satisfy the criteria, the zero-based page index, the page size applied, and the page count
    /// derived from them.
    /// </summary>
    public ApiMeta Meta { get; init; } = new();

    /// <summary>
    /// Projects a domain paging envelope onto the wire, carrying the same rows and the same paging facts.
    /// </summary>
    /// <param name="result">The domain envelope an application service produced.</param>
    /// <returns>A wire envelope reporting the rows and coordinates of <paramref name="result"/>.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="result"/> is <see langword="null"/>: a missing envelope is a defect at the call
    /// site, never a domain failure.
    /// </exception>
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
    public static PagedResponse<T> Empty() => new();

    /// <summary>
    /// Creates a zero-record envelope that still reports the page that was asked for, so a client can
    /// render an accurate pager for a requested page that happened to match nothing.
    /// </summary>
    /// <param name="pageIndex">The zero-based page of records the caller asked for. 0 is the first page.</param>
    /// <param name="pageSize">The size of the page the caller asked for.</param>
    /// <returns>An envelope carrying no rows and a total of zero, at the supplied coordinates.</returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="pageIndex"/> or <paramref name="pageSize"/> is negative, so the legacy integer null
    /// sentinel cannot arrive disguised as a page address and be published as one.
    /// </exception>
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
