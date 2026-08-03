using DnnMigration.Application.Dtos.Common;

namespace DnnMigration.Application.Validation;

/// <summary>
/// Bounds <see cref="RolePagedRequest"/>, narrowing the sortable vocabulary to the role collection's own set.
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
/// All eleven names are honoured by <c>RoleService.ListRolesAsync</c>, which orders the portal's roles in memory - faithful to the legacy shape, whose whole role-listing surface returned every row - so a caller-chosen order costs no additional query. Before this work that listing ordered by role name unconditionally and ignored the request.
/// </para>
/// </remarks>
public sealed class RolePagedRequestValidator : PagedRequestValidator<RolePagedRequest>
{
    /// <summary>
    /// Initialises a new instance of the <see cref="RolePagedRequestValidator"/> class.
    /// </summary>
    public RolePagedRequestValidator()
        : base(SortableFields.Roles)
    {
    }
}
