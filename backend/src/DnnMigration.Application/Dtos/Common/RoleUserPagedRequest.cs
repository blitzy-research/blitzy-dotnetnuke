namespace DnnMigration.Application.Dtos.Common;

/// <summary>
/// The paging, sorting and filtering query contract bound by
/// <c>GET /api/v1/roles/{roleId}/users</c>.
/// </summary>
/// <remarks>
/// <para>
/// WHY A DERIVED TYPE RATHER THAN THE SHARED CONTRACT. It adds no member and changes no behaviour; it
/// exists so that the request has a TYPE of its own, which is the only thing FluentValidation dispatches
/// on. This is the same reason <see cref="UserPagedRequest"/> and its three siblings exist, and the
/// pattern is followed rather than varied.
/// </para>
/// <para>
/// MIGRATION: WHY THIS COLLECTION NEEDED ITS OWN TYPE AND DID NOT HAVE ONE. The role-membership listing
/// bound <see cref="UserPagedRequest"/>, so <c>UserPagedRequestValidator</c> resolved for it and applied
/// <c>SortableFields.Users</c> - the ACCOUNT collection's seven names. The service behind the endpoint
/// enforces <c>SortableFields.RoleUsers</c>, which is TEN names, and <c>RoleService</c>'s ordering
/// implements all ten. The three names in the difference - <c>CreatedDate</c>, <c>LastLoginDate</c> and
/// <c>IsApproved</c> - were therefore refused at the boundary with a field-level <c>400</c> naming
/// <c>sortBy</c>, and no caller could reach an ordering the service was fully able to perform.
/// </para>
/// <para>
/// The direction of that defect is worth stating precisely, because it is the opposite of the one the
/// per-collection types were introduced to fix. Sharing one type across collections made a listing accept
/// a field name it would silently discard - too permissive. Borrowing another collection's type made this
/// listing refuse a field name it would have honoured - too restrictive. Both are the same underlying
/// mistake: the validator's vocabulary and the service's vocabulary must be the same vocabulary, and the
/// only way to guarantee that is one request type per collection, each closed over the one set that
/// collection honours.
/// </para>
/// <para>
/// SEC-F11. THIS SET NO LONGER CLAIMS THE THREE MEMBERSHIP COLUMNS. Both listings page in the STORE, so
/// neither can order by the values the external <c>aspnet_*</c> membership objects supply, and the
/// role-membership projection does not publish them either. The three names were accepted here and silently
/// discarded by the ordering behind them; they are now refused with a field-keyed <c>400</c>, which is the
/// honest answer for an ordering nothing can perform.
/// </para>
/// <para>
/// Nothing else about the contract differs, so the query string a caller sends is unchanged and the
/// application service still receives the shared base type.
/// </para>
/// </remarks>
public sealed class RoleUserPagedRequest : PagedRequest
{
}
