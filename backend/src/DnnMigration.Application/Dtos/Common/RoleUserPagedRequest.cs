namespace DnnMigration.Application.Dtos.Common;

/// <summary>
/// The paging, sorting and filtering query contract bound by <c>GET /api/v1/roles/{roleId}/users</c>.
/// </summary>
/// <remarks>
/// <para>
/// WHY THIS COLLECTION NEEDED ITS OWN TYPE AND DID NOT HAVE ONE. The role-membership listing bound <see
/// cref="UserPagedRequest"/>, so <c>UserPagedRequestValidator</c> resolved for it and applied
/// <c>SortableFields.Users</c> - the ACCOUNT collection's seven names.
/// </para>
/// <para>
/// THIS SET NO LONGER CLAIMS THE THREE MEMBERSHIP COLUMNS. Both listings page in the STORE, so neither can
/// order by the values the external <c>aspnet_*</c> membership objects supply, and the role-membership
/// projection does not publish them either.
/// </para>
/// </remarks>
public sealed class RoleUserPagedRequest : PagedRequest
{
}
