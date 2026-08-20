namespace DnnMigration.Application.Dtos.Common;

/// <summary>The paging query contract bound by <c>GET /api/v1/users/{id}/services</c>.</summary>
/// <remarks>
/// <para>
/// A tenant's member-service catalogue is one row per subscribable role, so a tenant that publishes a
/// thousand roles served a thousand rows of eighteen fields - 437 KiB - to a subscriber who could act on one
/// of them. The catalogue is still read and classified as a whole, because a subscription state is decided
/// against the same instant for every row; what this contract bounds is how much of it crosses the wire.
/// </para>
/// <para>
/// The inherited sort field and filter are REFUSED rather than ignored, by
/// <c>MemberServicePagedRequestValidator</c>, for the reason <c>SortableFields.MemberServices</c> records.
/// </para>
/// </remarks>
public sealed class MemberServicePagedRequest : PagedRequest
{
}
