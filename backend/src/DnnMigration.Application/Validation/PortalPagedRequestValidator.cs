using DnnMigration.Application.Dtos.Common;

namespace DnnMigration.Application.Validation;

/// <summary>
/// Bounds <see cref="PortalPagedRequest"/>, narrowing the sortable vocabulary to the portal collection's own set.
/// </summary>
/// <remarks>
/// <para>
/// Every paging, direction and filter bound is inherited unchanged from
/// <see cref="PagedRequestValidator{TRequest}"/>; the only thing declared here is WHICH sortable set
/// applies. That is the whole purpose of the type, and the reason it exists is that the narrow sets
/// previously had no consumer at all: one shared request type meant one resolved validator meant the
/// union was the only bound anything applied, so this endpoint accepted every other collection's field
/// names and then discarded them.
/// </para>
/// <para>
/// Every one of the five names in the set is honoured by <c>PortalRepository.ApplyOrder</c>. Two of them - the hosting charge and the disc-space quota - were advertised and fell through that method's default arm to the portal name, which is one of the silent discards this work removed.
/// </para>
/// </remarks>
public sealed class PortalPagedRequestValidator : PagedRequestValidator<PortalPagedRequest>
{
    /// <summary>
    /// Initialises a new instance of the <see cref="PortalPagedRequestValidator"/> class.
    /// </summary>
    public PortalPagedRequestValidator()
        : base(SortableFields.Portals)
    {
    }
}
