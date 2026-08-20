using DnnMigration.Application.Dtos.Common;

namespace DnnMigration.Application.Validation;

/// <summary>
/// Bounds <see cref="PortalPagedRequest"/>, narrowing the sortable vocabulary to the portal collection's
/// own set.
/// </summary>
public sealed class PortalPagedRequestValidator : PagedRequestValidator<PortalPagedRequest>
{
    /// <summary>Initialises a new instance of the <see cref="PortalPagedRequestValidator"/> class.</summary>
    public PortalPagedRequestValidator()
        : base(SortableFields.Portals)
    {
    }
}
