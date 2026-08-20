using DnnMigration.Application.Dtos.Common;

namespace DnnMigration.Application.Validation;

/// <summary>
/// Bounds <see cref="RolePagedRequest"/>, narrowing the sortable vocabulary to the role collection's own
/// set.
/// </summary>
/// <remarks>
/// All eleven names are honoured by <c>RoleService.ListRolesAsync</c>, which orders the portal's roles in
/// memory - faithful to the legacy shape, whose whole role-listing surface returned every row - so a
/// caller-chosen order costs no additional query. Before this work that listing ordered by role name
/// unconditionally and ignored the request.
/// </remarks>
public sealed class RolePagedRequestValidator : PagedRequestValidator<RolePagedRequest>
{
    /// <summary>Initialises a new instance of the <see cref="RolePagedRequestValidator"/> class.</summary>
    public RolePagedRequestValidator()
        : base(SortableFields.Roles)
    {
    }
}
