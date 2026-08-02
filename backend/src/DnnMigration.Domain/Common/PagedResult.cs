using System.Collections.ObjectModel;

namespace DnnMigration.Domain.Common;

// MIGRATION: This type retires the legacy `ByRef totalRecords As Integer` status
//            argument, which smuggled a grand total back to the caller beside an
//            untyped return value. Direct measurement of
//            Library/Components/Users/UserController.vb found EIGHT such overloads
//            - L725, L746, L769, L793, L816, L840, L864 and L889 - where the Agent
//            Action Plan section 0.7.4 records three (L685, L725, L746). The larger
//            count is reported here as a refinement of the plan rather than a
//            correction to it; the binding directive it states is unchanged.
// MIGRATION: It equally supersedes the untyped, non-generic System.Collections list
//            returns at Library/Components/Portal/PortalController.vb:L1263
//            (`GetPortals`) and Library/Components/Users/UserController.vb:L685
//            (`GetUsers`), neither of which declared an element type or a total.
// MIGRATION: The legacy stack could not answer "which page, and how many in total?"
//            in a single call, so it shipped a second, independent count member on
//            both data surfaces: `GetUserCountByPortal` at
//            Library/Providers/MembershipProviders/DataProvider/DataProvider.vb:L82,
//            beside the paged readers at L76, L83, L84 and L86; and `GetPortalCount`
//            at Library/Components/Providers/Data/DataProvider.vb:L100, beside
//            `GetPortalsByName` at L102. Carrying the records and the grand total on
//            one immutable value removes the second round trip and the status
//            argument together.
// MIGRATION: The legacy "return everything, unpaged" contract was expressed by
//            passing the null-integer sentinel three times over - see
//            Library/Components/Users/UserController.vb:L687,
//            `Return GetUsers(portalId, False, -1, -1, -1)`, and the identical call
//            at L706, where the sentinel is the -1 defined by
//            Library/Components/Shared/Null.vb:L41. That sentinel is deliberately
//            NOT reproduced: the factory methods below reject negative page
//            coordinates, and the unpaged case has its own named factory. A caller
//            must never pass -1 through as a literal page index or page size.
// MIGRATION: Page indexing is zero-based. The legacy VB surface never documented
//            its base, but the shipped schema settles it: the paging procedures
//            compute `SET @PageLowerBound = @PageSize * @PageIndex`
//            (Website/Providers/DataProviders/SqlDataProvider/03.01.01.SqlDataProvider:L38),
//            so page index 0 addresses the first row. Zero-based therefore matches
//            the legacy offset arithmetic, matches the .NET meaning of "index", and
//            maps directly onto a skip-then-take read without an off-by-one step.

/// <summary>
/// An immutable envelope carrying one page of records together with the total number
/// of records available across every page.
/// </summary>
/// <remarks>
/// <para>
/// <b>Page indexing is zero-based.</b> A <see cref="PageIndex"/> of 0 identifies the
/// first page, 1 the second, and so on. This is a fixed part of the contract, not an
/// implementation detail: the paging request type in the application layer and the
/// pagination control in the client both bind to it, so it must be readable one way
/// only. The first page of a paged set therefore begins at row
/// <see cref="PageIndex"/> multiplied by <see cref="PageSize"/>.
/// </para>
/// <para>
/// <see cref="TotalCount"/> is the number of records across <b>all</b> pages. It is
/// not the number of entries in <see cref="Items"/>, and the two are equal only when
/// the whole set fits on the page in hand. Pairing them on a single value is the
/// entire purpose of this type: it removes both the legacy out-parameter that
/// reported the total and the separate count member the legacy data surfaces
/// required alongside every paged read.
/// </para>
/// <para>
/// An unpaged, all-records set is produced by <see cref="Unpaged"/>. It reports a
/// <see cref="PageSize"/> of 0, a <see cref="PageIndex"/> of 0, and
/// <see cref="IsUnpaged"/> as <see langword="true"/>, so a caller never invents a
/// page coordinate to express it. The legacy practice of signalling that case with a
/// negative sentinel is not carried forward; negative coordinates are rejected.
/// </para>
/// <para>
/// A zero-record answer is available as <see cref="Empty"/>. Every instance is
/// immutable once constructed and is therefore safe to cache and to share across
/// threads. Immutability is enforced rather than merely declared: the factory
/// methods copy the records they are given and publish them through a read-only
/// view, so a caller that keeps hold of the collection it supplied cannot afterwards
/// change what a published envelope reports. The factories also reject coordinate
/// combinations that contradict one another, so no envelope can describe a page that
/// holds more records than the set contains, a page larger than its own page size, or
/// an unpaged set sitting at a non-zero page index.
/// </para>
/// </remarks>
/// <typeparam name="T">
/// The element type carried on the page. Domain entities and application-layer data
/// transfer objects are both valid; this envelope is agnostic to which it holds.
/// </typeparam>
public sealed class PagedResult<T>
{
    /// <summary>
    /// Initialises a new instance. Private by design: every caller arrives through
    /// <see cref="Create"/>, <see cref="Unpaged"/> or <see cref="Empty"/>, so the
    /// argument guards cannot be bypassed and no partially valid envelope exists.
    /// </summary>
    /// <param name="items">
    /// A private snapshot array that the factory has already taken and validated.
    /// The array parameter is deliberate and is confined to this private
    /// constructor: ownership transfers here, this instance is the only holder of
    /// the reference, and the array is never handed back out. It is published only
    /// as a read-only view on <see cref="Items"/>, so the mutable shape cannot
    /// escape.
    /// </param>
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

    /// <summary>
    /// Gets the records that make up this page, already materialised.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Materialised deliberately. A deferred sequence would let a query escape the
    /// repository that created it, could be enumerated twice with different answers,
    /// and would hide the count this envelope exists to expose.
    /// </para>
    /// <para>
    /// The records are also <b>copied</b> on the way in and published through a
    /// genuinely read-only view. The declared parameter type on the factory methods
    /// only promises that the <em>caller</em> will not be given a mutating
    /// interface; it does not stop the caller from having handed over a collection it
    /// still holds and can still change, and a plain array would not help either
    /// because its indexer is settable. Taking a private snapshot and wrapping it is
    /// what makes the immutability claimed by this type actually true, and therefore
    /// what makes an instance safe to cache and to share across threads.
    /// </para>
    /// </remarks>
    public IReadOnlyList<T> Items { get; }

    /// <summary>
    /// Gets the total number of records available across every page, as reported by
    /// the source that produced this envelope. This is not the size of
    /// <see cref="Items"/>.
    /// </summary>
    public int TotalCount { get; }

    /// <summary>
    /// Gets the zero-based index of the page that <see cref="Items"/> represents. 0
    /// is the first page. An unpaged envelope reports 0.
    /// </summary>
    public int PageIndex { get; }

    /// <summary>
    /// Gets the maximum number of records a single page may hold, or 0 when the set
    /// is unpaged. Never negative.
    /// </summary>
    public int PageSize { get; }

    /// <summary>
    /// Gets a value indicating whether this envelope carries an unpaged, all-records
    /// set rather than one page of a larger set.
    /// </summary>
    public bool IsUnpaged => PageSize == 0;

    /// <summary>
    /// Gets the number of pages the full set spans: 0 when there are no records, 1
    /// for an unpaged set that has some, and otherwise <see cref="TotalCount"/>
    /// divided by <see cref="PageSize"/> rounded upward.
    /// </summary>
    /// <remarks>
    /// The division is guarded twice. The <see cref="IsUnpaged"/> branch is taken
    /// first, so a page size of 0 can never reach a division. The remainder form is
    /// used in place of adding <see cref="PageSize"/> to <see cref="TotalCount"/>
    /// before dividing, because that addition would overflow for a total near
    /// <see cref="int.MaxValue"/> and silently report a negative page count.
    /// </remarks>
    public int TotalPages => IsUnpaged
        ? (TotalCount > 0 ? 1 : 0)
        : (TotalCount / PageSize) + (TotalCount % PageSize == 0 ? 0 : 1);

    /// <summary>
    /// Gets a value indicating whether a page precedes this one.
    /// </summary>
    public bool HasPreviousPage => PageIndex > 0;

    /// <summary>
    /// Gets a value indicating whether a further page follows this one, derived from
    /// <see cref="TotalPages"/> so it stays correct for an unpaged set and for an
    /// empty one.
    /// </summary>
    /// <remarks>
    /// Written as a comparison against one less than <see cref="TotalPages"/> rather
    /// than by advancing <see cref="PageIndex"/>, because incrementing a page index
    /// that already sits at the largest representable integer would wrap to the most
    /// negative one and then compare as though a further page existed. Subtracting
    /// from <see cref="TotalPages"/> cannot wrap: the property is never negative, so
    /// the smallest value the right-hand side can take is minus one, which no
    /// non-negative page index is below.
    /// </remarks>
    public bool HasNextPage => PageIndex < TotalPages - 1;

    /// <summary>
    /// Gets the shared zero-record, unpaged envelope. Use it for a list operation
    /// that legitimately matched nothing and applied no paging.
    /// </summary>
    /// <remarks>
    /// Initialised once per constructed type argument and then reused, so repeatedly
    /// returning an empty answer allocates nothing. For a paged operation whose
    /// requested page matched nothing, prefer <see cref="Create"/> with an empty
    /// collection and the page coordinates that were actually requested, so the
    /// caller can still render an accurate pager.
    /// </remarks>
    public static PagedResult<T> Empty { get; } = new([], 0, 0, 0);

    /// <summary>
    /// Creates an envelope for one page of a larger set.
    /// </summary>
    /// <param name="items">
    /// The records on this page, already materialised. May be empty when the
    /// requested page matched nothing. A private copy is taken, so the caller may
    /// keep and reuse whatever it passed in without affecting the envelope returned.
    /// </param>
    /// <param name="totalCount">
    /// The total number of records across every page, not the size of
    /// <paramref name="items"/>.
    /// </param>
    /// <param name="pageIndex">
    /// The zero-based index of this page. 0 is the first page.
    /// </param>
    /// <param name="pageSize">
    /// The maximum number of records a page may hold. Pass 0 only to describe an
    /// unpaged set, for which <see cref="Unpaged"/> is the clearer entry point.
    /// </param>
    /// <returns>An immutable envelope over the supplied page and total.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="items"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <para>
    /// <paramref name="totalCount"/>, <paramref name="pageIndex"/> or
    /// <paramref name="pageSize"/> is negative. Negative coordinates are rejected
    /// rather than interpreted, so the legacy null-integer sentinel cannot leak in
    /// and be mistaken for a real page address.
    /// </para>
    /// <para>
    /// Or the three coordinates contradict one another. A page size of 0 declares an
    /// unpaged set, which has no page coordinate to address, so it may only be paired
    /// with a <paramref name="pageIndex"/> of 0. A page can never hold more records
    /// than the set contains, so <paramref name="items"/> may not be longer than
    /// <paramref name="totalCount"/>. And a paged read honours the page size it was
    /// given, so <paramref name="items"/> may not be longer than
    /// <paramref name="pageSize"/> when that size is positive. Each of these
    /// combinations describes an envelope no real query could have produced, and each
    /// would otherwise be published as fact to a pager that trusts it.
    /// </para>
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

        // A private copy is taken before the remaining guards so that the length the
        // guards inspect is the same length the finished envelope will report, even if
        // the collection the caller supplied is one it can still change.
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
    /// Creates an envelope for an unpaged, all-records set, deriving the total from
    /// the supplied records because no further page exists to account for.
    /// </summary>
    /// <remarks>
    /// This is the replacement for the legacy all-records call shape, which
    /// signalled the same intent by passing a negative sentinel for the page index,
    /// the page size and the total alike. Here the intent is named, no sentinel is
    /// involved, and the caller supplies no page coordinate at all.
    /// </remarks>
    /// <param name="items">Every matching record, already materialised.</param>
    /// <returns>
    /// An immutable envelope reporting <see cref="IsUnpaged"/> as
    /// <see langword="true"/>, a <see cref="PageSize"/> of 0 and a
    /// <see cref="TotalCount"/> equal to the number of supplied records.
    /// </returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="items"/> is <see langword="null"/>.
    /// </exception>
    public static PagedResult<T> Unpaged(IReadOnlyList<T> items)
    {
        ArgumentNullException.ThrowIfNull(items);

        // The total is derived from the private copy rather than from the supplied
        // collection, so the reported total and the published records are guaranteed
        // to agree even if the caller retains and changes what it passed in.
        T[] snapshot = [.. items];

        return new PagedResult<T>(snapshot, snapshot.Length, 0, 0);
    }
}
