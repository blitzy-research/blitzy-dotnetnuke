using DnnMigration.Application.Dtos.Common;
using FluentValidation;

namespace DnnMigration.Application.Validation;

/// <summary>
/// Bounds <see cref="MemberServicePagedRequest"/>: every shared paging rule, no sort field, and no filter.
/// </summary>
/// <remarks>
/// Identical in shape to <see cref="TabPagedRequestValidator"/> and identical in reasoning: the shared paging
/// bounds are inherited, <c>SortableFields.MemberServices</c> is empty so every sort field is refused, and
/// the filter is refused because the catalogue has no filterable column of its own. Stated separately rather
/// than shared with a common base, because the two collections refuse for reasons of their own and a future
/// decision to accept a filter on one of them must not silently accept it on the other.
/// </remarks>
public sealed class MemberServicePagedRequestValidator : PagedRequestValidator<MemberServicePagedRequest>
{
    /// <summary>Message reported when a caller supplies a filter this collection cannot apply.</summary>
    private const string FilterNotSupportedMessage =
        "The member-service catalogue is not filtered by text. Omit the filter and page through the "
        + "collection.";

    /// <summary>Initialises a new instance of the <see cref="MemberServicePagedRequestValidator"/> class.</summary>
    public MemberServicePagedRequestValidator()
        : base(SortableFields.MemberServices)
    {
        RuleFor(request => request.Query)
            .Empty()
            .WithMessage(FilterNotSupportedMessage);
    }
}
