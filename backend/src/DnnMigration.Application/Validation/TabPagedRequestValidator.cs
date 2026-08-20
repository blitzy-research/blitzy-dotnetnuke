using DnnMigration.Application.Dtos.Common;
using FluentValidation;

namespace DnnMigration.Application.Validation;

/// <summary>
/// Bounds <see cref="TabPagedRequest"/>: every shared paging rule, no sort field, and no filter.
/// </summary>
/// <remarks>
/// <para>
/// The paging bounds - the zero-based index rule, the hundred-row ceiling, the bound on the product of index
/// and size, and the sort-direction membership rule - are inherited unchanged from
/// <see cref="PagedRequestValidator{TRequest}"/>, so this collection guards a page request exactly as the
/// other collections do and reports the same messages when it refuses one.
/// </para>
/// <para>
/// <c>SortableFields.Tabs</c> is empty, which turns the inherited sort rule into a refusal of every sort
/// field. The filter rule below is the same decision applied to the free-text filter: a page listing has no
/// filterable column of its own, so a caller who sends one is told, rather than served an unfiltered
/// collection that looks filtered.
/// </para>
/// </remarks>
public sealed class TabPagedRequestValidator : PagedRequestValidator<TabPagedRequest>
{
    /// <summary>Message reported when a caller supplies a filter this collection cannot apply.</summary>
    private const string FilterNotSupportedMessage =
        "A portal's pages are not filtered by text. Omit the filter and page through the collection.";

    /// <summary>Initialises a new instance of the <see cref="TabPagedRequestValidator"/> class.</summary>
    public TabPagedRequestValidator()
        : base(SortableFields.Tabs)
    {
        RuleFor(request => request.Query)
            .Empty()
            .WithMessage(FilterNotSupportedMessage);
    }
}
