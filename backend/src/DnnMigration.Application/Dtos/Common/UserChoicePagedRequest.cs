namespace DnnMigration.Application.Dtos.Common;

/// <summary>
/// The paging and filtering query contract bound by <c>GET /api/v1/users/choices</c>.
/// </summary>
/// <remarks>
/// <para>
/// WHY A DERIVED TYPE RATHER THAN THE SHARED CONTRACT. It adds no member and changes no behaviour; it exists
/// so that the request has a TYPE of its own, which is the only thing FluentValidation dispatches on. Were
/// this endpoint to bind <see cref="PagedRequest"/> directly, the validator resolved for it would be the
/// unspecialised one, whose sortable set is the union of every collection's - so a caller could order an
/// account picker by a portal or module field name, receive <c>200 OK</c>, and get an order they had not
/// asked for with nothing in the response to say the parameter had been discarded. That is the defect the
/// per-collection request types exist to close, and it applies here for the same reason it applies to the
/// account listing.
/// </para>
/// <para>
/// The bounds are the shared ones and are deliberately not restated: <c>PagedRequestValidator</c> owns the
/// zero-based index rule, the page-size ceiling, the bound on the product of index and size, the sort-
/// direction membership rule and the filter-length ceiling. A picker that may enumerate a tenant is exactly
/// the caller those bounds exist for, so inheriting them unchanged is the point rather than an economy.
/// </para>
/// <para>
/// The sortable set this endpoint admits is <c>SortableFields.UserChoices</c>, which is the two captions the
/// options actually show. It is narrower than the account listing's set because the projection is narrower:
/// ordering a drop-down by an electronic-mail address or a super-user flag would order it by a value the
/// operator cannot see, and every name a caller may send here is a value the caller gets back.
/// </para>
/// </remarks>
public sealed class UserChoicePagedRequest : PagedRequest
{
}
