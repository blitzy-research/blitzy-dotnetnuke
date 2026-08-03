namespace DnnMigration.Application.Dtos.Common;

/// <summary>
/// The paging, sorting and filtering query contract bound by <c>GET /api/v1/portals</c>.
/// </summary>
/// <remarks>
/// <para>
/// WHY A DERIVED TYPE RATHER THAN THE SHARED CONTRACT. It adds no member and changes no behaviour; it
/// exists so that the request has a TYPE of its own, which is the only thing FluentValidation
/// dispatches on. Every collection endpoint used to bind <see cref="PagedRequest"/> directly, one
/// validator was therefore resolved for all of them, and the only bound that validator could apply
/// was the union of every collection's sortable field names - so a caller could order the portal
/// listing by a field belonging to another collection entirely, receive <c>200 OK</c>, and get an
/// order they had not asked for with nothing in the response to say the parameter had been discarded.
/// </para>
/// <para>
/// With one type per collection, <c>PortalPagedRequestValidator</c> resolves for this endpoint and applies
/// <c>SortableFields.Portals</c> alone, so an out-of-collection field name is a field-level
/// <c>400</c> naming <c>sortBy</c>. Nothing else about the contract differs, so the query string a
/// caller sends is unchanged and the application service still receives the shared base type.
/// </para>
/// </remarks>
public sealed class PortalPagedRequest : PagedRequest
{
}
