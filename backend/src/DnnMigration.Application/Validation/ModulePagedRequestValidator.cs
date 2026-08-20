using DnnMigration.Application.Dtos.Common;

namespace DnnMigration.Application.Validation;

/// <summary>
/// Bounds <see cref="ModulePagedRequest"/>, narrowing the sortable vocabulary to the module collection's
/// own set.
/// </summary>
public sealed class ModulePagedRequestValidator : PagedRequestValidator<ModulePagedRequest>
{
    /// <summary>Initialises a new instance of the <see cref="ModulePagedRequestValidator"/> class.</summary>
    public ModulePagedRequestValidator()
        : base(SortableFields.Modules)
    {
    }
}
