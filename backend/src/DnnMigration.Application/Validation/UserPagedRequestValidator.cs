using DnnMigration.Application.Dtos.Common;

namespace DnnMigration.Application.Validation;

/// <summary>
/// Bounds <see cref="UserPagedRequest"/>, narrowing the sortable vocabulary to the account collection's own
/// set.
/// </summary>
/// <remarks>
/// All seven names are honoured by <c>UserRepository.ListAsync</c>, which now composes the ordering clause
/// before paging so that the order applies to the collection rather than to the page. Four further names
/// the projection shows are deliberately not in the set, because they are filled after the page has been
/// taken; <c>SortableFields</c> records why.
/// </remarks>
public sealed class UserPagedRequestValidator : PagedRequestValidator<UserPagedRequest>
{
    /// <summary>Initialises a new instance of the <see cref="UserPagedRequestValidator"/> class.</summary>
    public UserPagedRequestValidator()
        : base(SortableFields.Users)
    {
    }
}
