namespace DnnMigration.Application.Dtos.Common;

/// <summary>The paging and filtering query contract bound by <c>GET /api/v1/users/choices</c>.</summary>
/// <remarks>
/// <para>
/// WHY A DERIVED TYPE RATHER THAN THE SHARED CONTRACT. It adds no member and changes no behaviour; it
/// exists so that the request has a TYPE of its own, which is the only thing FluentValidation dispatches
/// on.
/// </para>
/// <para>
/// The bounds are the shared ones and are deliberately not restated: <c>PagedRequestValidator</c> owns the
/// zero-based index rule, the page-size ceiling, the bound on the product of index and size, the sort-
/// direction membership rule and the filter-length ceiling. A picker that may enumerate a tenant is exactly
/// the caller those bounds exist for, so inheriting them unchanged is the point rather than an economy.
/// </para>
/// </remarks>
public sealed class UserChoicePagedRequest : PagedRequest
{
}
