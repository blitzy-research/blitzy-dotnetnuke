using DnnMigration.Domain.Common;

namespace DnnMigration.Application.Dtos.Common;

// MIGRATION: this envelope replaces the `ByRef totalRecords` out-parameter idiom that the legacy
// listing members used to return a page and its total separately - UserController.vb L685, L725 and
// L746 are the canonical sites. The domain layer already carries both facts together in
// PagedResult<T>; this type is the *wire* shape of that same pair, so the count travels with the
// page instead of being smuggled out through a second channel that a caller could forget to read.
//
// MIGRATION: the split between PagedResult<T> and PagedResponse<T> is deliberate and is not
// duplication. PagedResult<T> is a domain type that application services return; PagedResponse<T> is
// a boundary contract that controllers serialise. Keeping them separate is what stops a domain type
// from becoming a published JSON contract, so a later change to the domain paging type cannot break
// an API consumer, and the paging metadata can be shaped for the wire - here as the shared ApiMeta
// object - without the domain having to know about it.

/// <summary>
/// The wire shape of one page of results: the rows themselves plus the paging metadata that
/// describes where the page sits within the whole match set.
/// </summary>
/// <typeparam name="T">The row contract carried by the page. Always a DTO, never a domain entity.</typeparam>
/// <remarks>
/// <para>
/// Every listing endpoint returns this envelope so that a client can render a pager without a second
/// request. Use <see cref="From(PagedResult{T})"/> to project a service result onto the wire rather
/// than assembling the metadata by hand, which keeps the total, page index, page size and page count
/// consistent with the query that produced them.
/// </para>
/// <para>
/// An unpaged result - one produced by a query that returned every match rather than a window - is
/// represented faithfully: <see cref="ApiMeta.PageIndex"/> is 0 and <see cref="ApiMeta.PageSize"/>
/// equals <see cref="ApiMeta.TotalCount"/>, so a client that divides to obtain a page count still
/// gets a single page instead of a division by zero.
/// </para>
/// </remarks>
public sealed class PagedResponse<T>
{
    /// <summary>The rows on this page, in the order the query produced them. Never <see langword="null"/>; an empty page is a legitimate answer.</summary>
    public IReadOnlyList<T> Items { get; init; } = Array.Empty<T>();

    /// <summary>Where this page sits within the whole match set.</summary>
    public ApiMeta Meta { get; init; } = new();

    /// <summary>
    /// Projects a domain paging result onto the wire envelope, preserving the unpaged case.
    /// </summary>
    /// <param name="result">The service result to project. Must not be <see langword="null"/>.</param>
    /// <returns>An envelope carrying the same rows and the same paging facts.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="result"/> is <see langword="null"/>.</exception>
    public static PagedResponse<T> From(PagedResult<T> result)
    {
        ArgumentNullException.ThrowIfNull(result);

        // An unpaged result has no window of its own, so the window is reported as the whole set.
        // Reporting PageSize as 0 here would make a client's page-count division fail, and reporting
        // it as some default would misdescribe a response that genuinely contains everything.
        int pageSize = result.IsUnpaged ? result.TotalCount : result.PageSize;

        return new PagedResponse<T>
        {
            Items = result.Items,
            Meta = new ApiMeta
            {
                TotalCount = result.TotalCount,
                PageIndex = result.IsUnpaged ? 0 : result.PageIndex,
                PageSize = pageSize,
            },
        };
    }
}
