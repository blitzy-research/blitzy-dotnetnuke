using DnnMigration.Application.Dtos.Common;

namespace DnnMigration.Application.Validation;

/// <summary>
/// Bounds <see cref="RoleUserPagedRequest"/>, narrowing the sortable vocabulary to the role-membership
/// collection's own set.
/// </summary>
/// <remarks>
/// <para>
/// Every paging, direction and filter bound is inherited unchanged from
/// <see cref="PagedRequestValidator{TRequest}"/>; the only thing declared here is WHICH sortable set
/// applies. That is the whole purpose of the type.
/// </para>
/// <para>
/// MIGRATION: this validator exists because the role-membership listing borrowed the ACCOUNT collection's
/// request type, so <c>UserPagedRequestValidator</c> resolved for it and applied
/// <c>SortableFields.Users</c> - seven names - while <c>RoleService.ListRoleUsersAsync</c> enforces
/// <c>SortableFields.RoleUsers</c> - ten. A caller asking to order by <c>CreatedDate</c>,
/// <c>LastLoginDate</c> or <c>IsApproved</c> was refused at the boundary with a field-level <c>400</c>,
/// even though <c>RoleService.OrderRoleMemberships</c> has an arm for each of the three and honours it over
/// a value that is genuinely present on every row. The endpoint advertised a narrower capability than it
/// implemented, and three orderings were unreachable.
/// </para>
/// <para>
/// Every name in the set is honoured by an ordering clause the store can actually perform, which is the
/// property SEC-F11 restored: a name is admitted here only when the repository orders by it and the
/// projection publishes it.
/// </para>
/// <para>
/// The set is also narrower than the projection in one respect: the two assignment dates the membership
/// record carries are deliberately not orderable, because the legacy grid offered no ordering by them.
/// <c>SortableFields</c> and <c>IRoleService</c> record that reduction.
/// </para>
/// </remarks>
public sealed class RoleUserPagedRequestValidator : PagedRequestValidator<RoleUserPagedRequest>
{
    /// <summary>
    /// Initialises a new instance of the <see cref="RoleUserPagedRequestValidator"/> class.
    /// </summary>
    public RoleUserPagedRequestValidator()
        : base(SortableFields.RoleUsers)
    {
    }
}
