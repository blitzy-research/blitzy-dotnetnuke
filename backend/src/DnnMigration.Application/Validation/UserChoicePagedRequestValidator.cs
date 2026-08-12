using DnnMigration.Application.Dtos.Common;

namespace DnnMigration.Application.Validation;

/// <summary>
/// Bounds <see cref="UserChoicePagedRequest"/>, narrowing the sortable vocabulary to the two captions an
/// account picker's options actually show.
/// </summary>
/// <remarks>
/// <para>
/// Every paging, direction and filter bound is inherited unchanged from
/// <see cref="PagedRequestValidator{TRequest}"/> - the zero-based index rule, the hundred-row page ceiling,
/// the bound on the product of index and size, the sort-direction membership rule and the
/// two-hundred-and-fifty-six-character filter ceiling. The only thing declared here is WHICH sortable set
/// applies, which is the whole purpose of a per-collection validator.
/// </para>
/// <para>
/// Those inherited bounds matter more on this endpoint than on most, because it is the one an account picker
/// may walk page by page across a whole tenant. The page ceiling is what makes each of those requests
/// bounded, and the filter ceiling is what keeps a typeahead term from arriving as an arbitrarily long
/// pattern. Neither is restated here precisely so that neither can drift from the value every other
/// collection applies.
/// </para>
/// <para>
/// <c>SortableFields.UserChoices</c> holds two names and both are honoured by
/// <c>UserRepository.ListAccountChoicesAsync</c>, which composes the ordering clause before it projects and
/// before it pages - so the order applies to the collection rather than to one page. The set is narrower
/// than the account listing's because the payload is: this endpoint returns a key and two captions, so those
/// captions are the only values whose ordering a caller could observe.
/// </para>
/// </remarks>
public sealed class UserChoicePagedRequestValidator : PagedRequestValidator<UserChoicePagedRequest>
{
    /// <summary>
    /// Initialises a new instance of the <see cref="UserChoicePagedRequestValidator"/> class.
    /// </summary>
    public UserChoicePagedRequestValidator()
        : base(SortableFields.UserChoices)
    {
    }
}
