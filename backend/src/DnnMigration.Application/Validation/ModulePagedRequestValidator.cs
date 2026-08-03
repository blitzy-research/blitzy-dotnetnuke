using DnnMigration.Application.Dtos.Common;

namespace DnnMigration.Application.Validation;

/// <summary>
/// Bounds <see cref="ModulePagedRequest"/>, narrowing the sortable vocabulary to the module collection's own set.
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
/// All five names are honoured by the module listing, which orders in memory before paging. This collection previously had no set of its own at all, so it was bounded only by the union and accepted - and discarded - every portal, role and account field name.
/// </para>
/// </remarks>
public sealed class ModulePagedRequestValidator : PagedRequestValidator<ModulePagedRequest>
{
    /// <summary>
    /// Initialises a new instance of the <see cref="ModulePagedRequestValidator"/> class.
    /// </summary>
    public ModulePagedRequestValidator()
        : base(SortableFields.Modules)
    {
    }
}
