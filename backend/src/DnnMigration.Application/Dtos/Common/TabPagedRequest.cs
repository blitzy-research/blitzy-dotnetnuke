namespace DnnMigration.Application.Dtos.Common;

/// <summary>The paging query contract bound by <c>GET /api/v1/portals/{id}/tabs</c>.</summary>
/// <remarks>
/// <para>
/// Paging is the WHOLE contract here. A portal legitimately holds thousands of pages - the platform this
/// migration replaces was built for exactly that - so an unpaged listing grew linearly with tenant
/// configuration: three thousand pages serialised to 764 KiB in one response, and nothing in the request
/// could ask for less.
/// </para>
/// <para>
/// The inherited sort field and filter are REFUSED rather than ignored, by
/// <c>TabPagedRequestValidator</c>. The collection has one meaningful order and no filterable column of its
/// own, and a parameter that is accepted and then discarded is worse than one that is rejected: the caller
/// believes it took effect.
/// </para>
/// </remarks>
public sealed class TabPagedRequest : PagedRequest
{
}
