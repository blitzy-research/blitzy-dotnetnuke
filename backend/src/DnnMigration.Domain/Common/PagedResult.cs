using System.Collections.ObjectModel;

namespace DnnMigration.Domain.Common;

/// <summary>
/// An immutable envelope carrying one page of records together with the total number of records available
/// across every page.
/// </summary>
/// <remarks>
/// <para>
/// <b>Page indexing is zero-based.</b> A <see cref="PageIndex"/> of 0 identifies the first page, 1 the
/// second, and so on. This is a fixed part of the contract, not an implementation detail: the paging
/// request type in the application layer and the pagination control in the client both bind to it, so it
/// must be readable one way only.
/// </para>
/// <para>
/// An unpaged, all-records set is produced by <see cref="Unpaged"/>. It reports a <see cref="PageSize"/> of
/// 0, a <see cref="PageIndex"/> of 0, and <see cref="IsUnpaged"/> as <see langword="true"/>, so a caller
/// never invents a page coordinate to express it.
/// </para>
/// </remarks>
/// <typeparam name="T">The element type carried on the page.</typeparam>
public sealed class PagedResult<T>
{
    /// <summary>
    /// Initialises a new instance. Private by design: every caller arrives through <see cref="Create"/>,
    /// <see cref="Unpaged"/> or <see cref="Empty"/>, so the argument guards cannot be bypassed and no
    /// partially valid envelope exists.
    /// </summary>
    /// <param name="items">A private snapshot array that the factory has already taken and validated.</param>
    /// <param name="totalCount">The already-validated total across all pages.</param>
    /// <param name="pageIndex">The already-validated zero-based page index.</param>
    /// <param name="pageSize">The already-validated page size, 0 when unpaged.</param>
    private PagedResult(T[] items, int totalCount, int pageIndex, int pageSize)
    {
        Items = new ReadOnlyCollection<T>(items);
        TotalCount = totalCount;
        PageIndex = pageIndex;
        PageSize = pageSize;
    }

    /// <summary>Gets the records that make up this page, already materialised.</summary>
    public IReadOnlyList<T> Items { get; }

    /// <summary>
    /// Gets the total number of records available across every page, as reported by the source that
    /// produced this envelope. This is not the size of <see cref="Items"/>.
    /// </summary>
    public int TotalCount { get; }

    /// <summary>
    /// Gets the zero-based index of the page that <see cref="Items"/> represents. 0 is the first page. An
    /// unpaged envelope reports 0.
    /// </summary>
    public int PageIndex { get; }

    /// <summary>
    /// Gets the maximum number of records a single page may hold, or 0 when the set is unpaged. Never
    /// negative.
    /// </summary>
    public int PageSize { get; }

    /// <summary>
    /// Gets a value indicating whether this envelope carries an unpaged, all-records set rather than one
    /// page of a larger set.
    /// </summary>
    public bool IsUnpaged => PageSize == 0;

    /// <summary>
    /// Gets the number of pages the full set spans: 0 when there are no records, 1 for an unpaged set that
    /// has some, and otherwise <see cref="TotalCount"/> divided by <see cref="PageSize"/> rounded upward.
    /// </summary>
    /// <remarks>
    /// The division is guarded twice. The <see cref="IsUnpaged"/> branch is taken first, so a page size of
    /// 0 can never reach a division.
    /// </remarks>
    public int TotalPages => IsUnpaged
        ? (TotalCount > 0 ? 1 : 0)
        : (TotalCount / PageSize) + (TotalCount % PageSize == 0 ? 0 : 1);

    /// <summary>Gets a value indicating whether a page precedes this one.</summary>
    public bool HasPreviousPage => PageIndex > 0;

    /// <summary>
    /// Gets a value indicating whether a further page follows this one, derived from <see
    /// cref="TotalPages"/> so it stays correct for an unpaged set and for an empty one.
    /// </summary>
    /// <remarks>
    /// Written as a comparison against one less than <see cref="TotalPages"/> rather than by advancing <see
    /// cref="PageIndex"/>, because incrementing a page index that already sits at the largest representable
    /// integer would wrap to the most negative one and then compare as though a further page existed.
    /// </remarks>
    public bool HasNextPage => PageIndex < TotalPages - 1;

    /// <summary>
    /// Gets the shared zero-record, unpaged envelope. Use it for a list operation that legitimately matched
    /// nothing and applied no paging.
    /// </summary>
    public static PagedResult<T> Empty { get; } = new([], 0, 0, 0);

    /// <summary>Creates an envelope for one page of a larger set.</summary>
    /// <param name="items">The records on this page, already materialised.</param>
    /// <param name="totalCount">
    /// The total number of records across every page, not the size of <paramref name="items"/>.
    /// </param>
    /// <param name="pageIndex">The zero-based index of this page. 0 is the first page.</param>
    /// <param name="pageSize">The maximum number of records a page may hold.</param>
    /// <returns>An immutable envelope over the supplied page and total.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="items"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="totalCount"/>, <paramref name="pageIndex"/> or <paramref name="pageSize"/> is
    /// negative.
    /// </exception>
    public static PagedResult<T> Create(IReadOnlyList<T> items, int totalCount, int pageIndex, int pageSize)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentOutOfRangeException.ThrowIfNegative(totalCount);
        ArgumentOutOfRangeException.ThrowIfNegative(pageIndex);
        ArgumentOutOfRangeException.ThrowIfNegative(pageSize);

        if (pageSize == 0 && pageIndex != 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(pageIndex),
                pageIndex,
                "A page size of 0 describes an unpaged, all-records set, which has no page to address, so the page index must be 0. Use the Unpaged factory method to express that case.");
        }

        // A private copy is taken before the remaining guards so that the length the guards inspect is the
        // same length the finished envelope will report, even if the collection the caller supplied is one
        // it can still change.
        T[] snapshot = [.. items];

        if (snapshot.Length > totalCount)
        {
            throw new ArgumentOutOfRangeException(
                nameof(totalCount),
                totalCount,
                "The total across every page cannot be smaller than the number of records on this page. Supply the grand total, not the size of the page.");
        }

        if (pageSize > 0 && snapshot.Length > pageSize)
        {
            throw new ArgumentOutOfRangeException(
                nameof(items),
                snapshot.Length,
                "A page cannot carry more records than the page size it declares. Either the page size understates the read or the collection holds more than one page.");
        }

        return new PagedResult<T>(snapshot, totalCount, pageIndex, pageSize);
    }

    /// <summary>
    /// Creates an envelope for an unpaged, all-records set, deriving the total from the supplied records
    /// because no further page exists to account for.
    /// </summary>
    /// <remarks>
    /// This is the replacement for the legacy all-records call shape, which signalled the same intent by
    /// passing a negative sentinel for the page index, the page size and the total alike. Here the intent
    /// is named, no sentinel is involved, and the caller supplies no page coordinate at all.
    /// </remarks>
    /// <param name="items">Every matching record, already materialised.</param>
    /// <returns>
    /// An immutable envelope reporting <see cref="IsUnpaged"/> as <see langword="true"/>, a <see
    /// cref="PageSize"/> of 0 and a <see cref="TotalCount"/> equal to the number of supplied records.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="items"/> is <see langword="null"/>.</exception>
    public static PagedResult<T> Unpaged(IReadOnlyList<T> items)
    {
        ArgumentNullException.ThrowIfNull(items);

        T[] snapshot = [.. items];

        return new PagedResult<T>(snapshot, snapshot.Length, 0, 0);
    }
}
