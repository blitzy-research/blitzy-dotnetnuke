using DnnMigration.Application.Dtos.Common;

namespace DnnMigration.Application.Validation;

/// <summary>
/// Bounds <see cref="UserPagedRequest"/>, narrowing the sortable vocabulary to the account collection's own set.
/// </summary>
/// <remarks>
/// <para>
/// Every paging, direction and filter bound is inherited unchanged from
/// <see cref="PagedRequestValidator{TRequest}"/>; the only thing declared here is WHICH sortable set
/// applies. That is the whole purpose of the type, and the reason it exists is that the narrow sets
/// previously had no consumer at all: one shared request type meant one resolved validator meant the
/// union was the only bound anything applied, so this endpoint accepted every other collection's field
/// names and then discarded them.
/// </para>
/// <para>
/// All seven names are honoured by <c>UserRepository.ListAsync</c>, which now composes the ordering clause before paging so that the order applies to the collection rather than to the page. Four further names the projection shows are deliberately not in the set, because they are filled after the page has been taken; <c>SortableFields</c> records why.
/// </para>
/// </remarks>
public sealed class UserPagedRequestValidator : PagedRequestValidator<UserPagedRequest>
{
    /// <summary>
    /// Initialises a new instance of the <see cref="UserPagedRequestValidator"/> class.
    /// </summary>
    public UserPagedRequestValidator()
        : base(SortableFields.Users)
    {
    }
}
