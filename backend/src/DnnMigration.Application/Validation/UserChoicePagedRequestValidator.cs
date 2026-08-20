using DnnMigration.Application.Dtos.Common;

namespace DnnMigration.Application.Validation;

/// <summary>
/// Bounds <see cref="UserChoicePagedRequest"/>, narrowing the sortable vocabulary to the two captions an
/// account picker's options actually show.
/// </summary>
/// <remarks>
/// <para>
/// Every paging, direction and filter bound is inherited unchanged from <see
/// cref="PagedRequestValidator{TRequest}"/> - the zero-based index rule, the hundred-row page ceiling, the
/// bound on the product of index and size, the sort-direction membership rule and the
/// two-hundred-and-fifty-six-character filter ceiling.
/// </para>
/// <para>
/// <c>SortableFields.UserChoices</c> holds two names and both are honoured by
/// <c>UserRepository.ListAccountChoicesAsync</c>, which composes the ordering clause before it projects and
/// before it pages - so the order applies to the collection rather than to one page.
/// </para>
/// </remarks>
public sealed class UserChoicePagedRequestValidator : PagedRequestValidator<UserChoicePagedRequest>
{
    /// <summary>Initialises a new instance of the <see cref="UserChoicePagedRequestValidator"/> class.</summary>
    public UserChoicePagedRequestValidator()
        : base(SortableFields.UserChoices)
    {
    }
}
