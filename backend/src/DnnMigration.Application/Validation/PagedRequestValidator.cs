using DnnMigration.Application.Dtos.Common;
using FluentValidation;

namespace DnnMigration.Application.Validation;

/// <summary>
/// Declares the bounds of <see cref="PagedRequest"/>, the paging, sorting and filtering query contract that
/// every collection endpoint accepts.
/// </summary>
/// <remarks>
/// <b>Page indexing is zero-based.</b> A <see cref="PagedRequest.PageIndex"/> of 0 addresses the first
/// page, so the smallest legal index is 0 and every negative index is refused - including the legacy
/// integer absence sentinel of minus one.
/// </remarks>
public class PagedRequestValidator<TRequest> : AbstractValidator<TRequest>
    where TRequest : PagedRequest
{
    /// <summary>The largest page size a caller may ask for.</summary>
    public const int MaximumPageSize = 100;

    /// <summary>
    /// The longest free-text filter a caller may supply, taken from the terminal width of the widest column
    /// any collection endpoint filters on, which is <c>dbo.Users.Email nvarchar(256) NULL</c>.
    /// </summary>
    /// <remarks>
    /// A filter longer than the widest filterable column cannot match any stored value, so the bound
    /// rejects only requests that were already incapable of returning a row. It is a length bound and
    /// nothing more: the text itself passes through exactly as the caller typed it, because composing it
    /// into a match pattern is work that happens behind the repository interfaces and not here.
    /// </remarks>
    public const int QueryMaximumLength = 256;

    /// <summary>Message reported when a caller supplies a negative page index.</summary>
    /// <remarks>
    /// The message states the base explicitly, because the commonest cause of a negative index is a client
    /// that counted from one and subtracted twice.
    /// </remarks>
    private const string PageIndexNegativeMessage =
        "The page index may not be negative. Page indexes are zero-based, so the first page is 0.";

    /// <summary>Message reported when a caller asks for a page of zero or fewer records.</summary>
    private const string PageSizeTooSmallMessage =
        "The page size must be at least 1.";

    /// <summary>
    /// Message reported when a caller supplies a sort direction that is not one of the two declared
    /// members.
    /// </summary>
    private const string SortDirectionUnknownMessage =
        "The sort direction must be either Ascending or Descending.";

    /// <summary>
    /// Message reported when a caller asks for more records than <see cref="MaximumPageSize"/> permits.
    /// </summary>
    private static readonly string PageSizeTooLargeMessage =
        FormattableString.Invariant($"The page size may not exceed {MaximumPageSize}.");

    /// <summary>
    /// Reported when the addressed page lies so far into the sequence that the number of records to skip
    /// past it cannot be represented.
    /// </summary>
    private static readonly string PageOffsetUnrepresentableMessage =
        FormattableString.Invariant($"The page index multiplied by the page size may not exceed {int.MaxValue}.")
        + " That is the furthest position a paged read can address.";

    /// <summary>
    /// Message reported when a caller supplies a filter longer than <see cref="QueryMaximumLength"/>.
    /// </summary>
    private static readonly string QueryTooLongMessage =
        FormattableString.Invariant($"The filter text may not exceed {QueryMaximumLength} characters.");

    /// <summary>
    /// Message reported when a caller names a field that nothing can be ordered by, listing the names that
    /// are accepted.
    /// </summary>
    /// <remarks>
    /// Built once from <see cref="SortableFields.All"/> rather than transcribed, so widening a collection's
    /// sortable set cannot leave this message behind. The names are ordered so that the text is identical
    /// on every run and in every process, which a set's own enumeration order does not guarantee.
    /// </remarks>
    private readonly IReadOnlySet<string> _sortableFields;

    private readonly string _sortFieldUnknownMessage;

    /// <summary>
    /// Initialises a new instance of the <see cref="PagedRequestValidator{TRequest}"/> class and declares
    /// the bounds that apply to every collection endpoint.
    /// </summary>
    /// <param name="sortableFields">
    /// The closed set of field names this endpoint's collection can be ordered by, taken from
    /// <c>SortableFields</c>.
    /// </param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="sortableFields"/> is <see langword="null"/>.
    /// </exception>
    /// <remarks>
    /// The ONE argument is the sortable set. Every rule declared here asks a question about the shape of
    /// the request and none asks a question about state, so nothing else is injected and nothing else is
    /// configurable.
    /// </remarks>
    protected PagedRequestValidator(IReadOnlySet<string> sortableFields)
    {
        ArgumentNullException.ThrowIfNull(sortableFields);

        _sortableFields = sortableFields;
        _sortFieldUnknownMessage = BuildSortFieldUnknownMessage(sortableFields);

        // Zero-based, so 0 is the first page and the smallest legal value. A negative index is rejected
        // rather than reinterpreted, which is what stops the legacy absence sentinel of minus one arriving
        // disguised as a page address.
        RuleFor(request => request.PageIndex)
            .GreaterThanOrEqualTo(0)
            .WithMessage(PageIndexNegativeMessage);

        RuleFor(request => request.PageSize)
            .GreaterThan(0)
            .WithMessage(PageSizeTooSmallMessage)
            .LessThanOrEqualTo(MaximumPageSize)
            .WithMessage(PageSizeTooLargeMessage);

        // THE PRODUCT IS BOUNDED, NOT ONLY THE TWO FACTORS. Each field alone can be within its own bound
        // while their product is not: with the largest permitted page size, any index above roughly
        // twenty-one million overflows the signed 32-bit offset a paged read skips by.
        RuleFor(request => request)
            .Must(request => (long)request.PageIndex * request.PageSize <= int.MaxValue)
            .When(request => request.PageIndex > 0 && request.PageSize > 0)
            .WithName(nameof(PagedRequest.PageIndex))
            .WithMessage(PageOffsetUnrepresentableMessage);

        // Model binding will place an undeclared integer into an enum-typed property without complaint, so
        // membership is checked rather than assumed. Only the two declared directions can order anything; a
        // third value would reach a repository with no defined meaning.
        RuleFor(request => request.SortDir)
            .IsInEnum()
            .WithMessage(SortDirectionUnknownMessage);

        // A LENGTH BOUND AND NOTHING ELSE. The filter is not trimmed, unescaped, decorated or otherwise
        // rewritten, because the legacy prefix match was composed at the CALL SITE - the account screen
        // appended a trailing per-cent wildcard to the search text before calling down - and pattern
        // composition now belongs behind the repository interfaces.
        RuleFor(request => request.Query)
            .MaximumLength(QueryMaximumLength)
            .WithMessage(QueryTooLongMessage);

        // MIGRATION: CALLER-CHOSEN ORDERING IS NET-NEW, so this rule reproduces no legacy rule.
        RuleFor(request => request.SortBy)
            .Must(name => SortableFields.IsPermitted(_sortableFields, name))
            .WithMessage(_sortFieldUnknownMessage)
            .When(request => request.HasSort);
    }

    /// <summary>Composes the message that names every field a caller may sort by.</summary>
    /// <param name="sortableFields">The set this validator applies.</param>
    /// <returns>
    /// A message stating that the supplied field cannot be ordered by, followed by the accepted names in a
    /// stable order.
    /// </returns>
    /// <remarks>
    /// Ordered with an ordinal comparison because these are programmatic identifiers rather than words,
    /// which is the same reason <see cref="SortableFields"/> compares them ordinally: a culture-sensitive
    /// ordering of identifiers is how the Turkish dotless-i class of defect arises.
    /// </remarks>
    private static string BuildSortFieldUnknownMessage(IReadOnlySet<string> sortableFields)
    {
        // AN EMPTY SET IS A STATEMENT, NOT AN OVERSIGHT, so it gets its own sentence. A collection whose
        // order carries meaning of its own - a page hierarchy read depth-first, a price list in the order it
        // is published - accepts no sort field at all, and "Accepted fields: ." would read as a defect in
        // this message rather than as the answer it is.
        if (sortableFields.Count == 0)
        {
            return "This collection is returned in one defined order and cannot be re-ordered, so no sort "
                + "field is accepted. Omit the sort field.";
        }

        IEnumerable<string> accepted = sortableFields.OrderBy(name => name, StringComparer.Ordinal);

        return "The sort field is not one that can be ordered by. Accepted fields: "
            + string.Join(", ", accepted)
            + ".";
    }
}

/// <summary>
/// Applies the unspecialised paging bounds, with the UNION of every collection's sortable set, to a bare
/// <see cref="PagedRequest"/>.
/// </summary>
/// <remarks>
/// No registered endpoint binds a bare <see cref="PagedRequest"/> any longer - each collection binds its
/// own derived type so that its own narrow set is the one enforced - but the shared contract is public and
/// a caller of the application layer may still pass one, so it keeps a validator.
/// </remarks>
public class PagedRequestValidator : PagedRequestValidator<PagedRequest>
{
    /// <summary>Initialises a new instance of the <see cref="PagedRequestValidator"/> class.</summary>
    public PagedRequestValidator()
        : base(SortableFields.All)
    {
    }
}
