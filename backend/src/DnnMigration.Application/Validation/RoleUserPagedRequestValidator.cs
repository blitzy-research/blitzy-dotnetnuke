using DnnMigration.Application.Dtos.Common;

namespace DnnMigration.Application.Validation;

/// <summary>
/// Bounds <see cref="RoleUserPagedRequest"/>, narrowing the sortable vocabulary to the role-membership
/// collection's own set.
/// </summary>
/// <remarks>
/// <para>
/// This validator exists because the role-membership listing borrowed the ACCOUNT collection's request
/// type, so <c>UserPagedRequestValidator</c> resolved for it and applied <c>SortableFields.Users</c> -
/// seven names - while <c>RoleService.ListRoleUsersAsync</c> enforces <c>SortableFields.RoleUsers</c> -
/// ten.
/// </para>
/// <para>
/// Every name in the set is honoured by an ordering clause the store can actually perform, which is the
/// property restored: a name is admitted here only when the repository orders by it and the projection
/// publishes it.
/// </para>
/// </remarks>
public sealed class RoleUserPagedRequestValidator : PagedRequestValidator<RoleUserPagedRequest>
{
    /// <summary>Initialises a new instance of the <see cref="RoleUserPagedRequestValidator"/> class.</summary>
    public RoleUserPagedRequestValidator()
        : base(SortableFields.RoleUsers)
    {
    }
}
