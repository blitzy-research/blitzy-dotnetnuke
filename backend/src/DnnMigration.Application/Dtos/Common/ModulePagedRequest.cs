namespace DnnMigration.Application.Dtos.Common;

/// <summary>The paging, sorting and filtering query contract bound by <c>GET /api/v1/modules</c>.</summary>
// NOT SEALED: the body-bound search contract for this collection derives from it, so the two
// transports cannot drift apart in what they accept. `PagedRequest` is unsealed for the same
// reason, which is how `UserSearchRequest` extends it.
public class ModulePagedRequest : PagedRequest
{
}
