namespace DnnMigration.Application.Dtos.Common;

/// <summary>
/// The paging, sorting and filtering query contract that a collection endpoint accepts from its caller.
/// </summary>
/// <remarks>
/// Page indexing is zero-based, mirroring the index that <c>PagedResult&lt;T&gt;</c> in the domain layer
/// reports back.
/// </remarks>
// MIGRATION: the legacy "return everything, unpaged" call shape is deliberately NOT reproduced. It was
// expressed by handing the integer null sentinel of minus one to the page index, the page size and the
// total alike, so a negative page coordinate was indistinguishable from a missing one.
public class PagedRequest
{
    /// <summary>
    /// Gets or sets the zero-based index of the page of records to return, defaulting to 0 and therefore to
    /// the first page - which is what a caller who supplied no page meant.
    /// </summary>
    /// <remarks>
    /// Zero-based is a contract rather than a note: the value travels to the server on this type and comes
    /// back on <c>PagedResult&lt;T&gt;</c>, and both count from zero, so a client may echo one into the
    /// other without arithmetic.
    /// </remarks>
    public int PageIndex { get; set; }

    /// <summary>
    /// Gets or sets the maximum number of records the caller is willing to receive, defaulting to 10.
    /// </summary>
    public int PageSize { get; set; } = 10;

    /// <summary>
    /// Gets or sets the name of the field to sort by, or <see langword="null"/> when the caller expresses
    /// no preference and the server's own ordering applies.
    /// </summary>
    // Caller-chosen ordering has no legacy counterpart - no markup under Website/admin/Users declares a
    // sorting affordance, so the legacy grids rendered whatever order the stored procedure produced.
    public string? SortBy { get; set; }

    /// <summary>Gets or sets the direction in which <see cref="SortBy"/> is applied, ascending by default.</summary>
    /// <remarks>
    /// Consulted only when <see cref="HasSort"/> is <see langword="true"/>, but retained rather than
    /// discarded otherwise, so a caller toggling a direction before choosing a field does not lose the
    /// setting.
    /// </remarks>
    public SortDirection SortDir { get; set; }

    /// <summary>
    /// Gets or sets the caller's free-text filter, raw and exactly as typed, or <see langword="null"/> when
    /// no filter applies.
    /// </summary>
    // The legacy prefix match was composed at the call site - the administration screen appended a trailing
    // per-cent wildcard to the search text before calling down.
    public string? Query { get; set; }

    /// <summary>
    /// Gets a value indicating whether the caller asked for a particular ordering: <see langword="false"/>
    /// when <see cref="SortBy"/> is <see langword="null"/>, empty or entirely white space.
    /// </summary>
    /// <remarks>
    /// Derived, and therefore without a setter: the answer follows from <see cref="SortBy"/> and must not
    /// be able to contradict it. Its purpose is to settle the absent-versus-blank question once, here at
    /// the boundary, instead of leaving every service and repository to decide it again and disagree.
    /// </remarks>
    public bool HasSort => !string.IsNullOrWhiteSpace(SortBy);

    /// <summary>
    /// Gets a value indicating whether the caller supplied a filter: <see langword="false"/> when <see
    /// cref="Query"/> is <see langword="null"/>, empty or entirely white space.
    /// </summary>
    /// <remarks>
    /// Derived, and therefore without a setter, for the same reason as <see cref="HasSort"/>: the
    /// interpretation is stated in one place while <see cref="Query"/> keeps the caller's value untouched,
    /// because normalising one form into the other inside a request contract would erase the caller's
    /// actual input before a validator could observe it.
    /// </remarks>
    // MIGRATION: an omitted filter and an empty filter are the same request, because the legacy string
    // sentinel is the empty string and the legacy absence test reports an empty string as absent, so a
    // legacy caller that passed empty text meant "no filter".
    public bool HasQuery => !string.IsNullOrWhiteSpace(Query);
}

/// <summary>The direction in which a sort field is applied.</summary>
public enum SortDirection
{
    /// <summary>
    /// Order from lowest to highest: A before Z, earlier before later, smaller before larger. The default
    /// direction.
    /// </summary>
    Ascending = 0,

    /// <summary>Order from highest to lowest: Z before A, later before earlier, larger before smaller.</summary>
    Descending = 1,
}
