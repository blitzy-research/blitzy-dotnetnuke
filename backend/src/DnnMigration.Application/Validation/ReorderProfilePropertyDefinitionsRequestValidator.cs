using DnnMigration.Application.Dtos.User;
using FluentValidation;

namespace DnnMigration.Application.Validation;

/// <summary>
/// Declares the field rules for <see cref="ReorderProfilePropertyDefinitionsRequest"/>, the payload
/// submitted to <c>PUT /api/v1/profile-definitions/order</c> to reposition several of the resolved portal's
/// profile property declarations in one unit of work.
/// </summary>
/// <remarks>
/// The rules here are all SHAPE rules. Whether the named declarations exist, and whether they belong to the
/// resolved tenant, is an admission question that only the store can answer, so it is answered by the
/// service — which resolves every declaration before staging anything.
/// </remarks>
public sealed class ReorderProfilePropertyDefinitionsRequestValidator
    : AbstractValidator<ReorderProfilePropertyDefinitionsRequest>
{
    /// <summary>
    /// The most declarations one request may reposition.
    /// </summary>
    /// <remarks>
    /// A bound rather than a business rule. The set is resolved and staged in memory before it is
    /// committed, so an unbounded list is an unbounded allocation on an authenticated path. The value is
    /// far above any real catalogue — the seeded installation declares fewer than twenty properties — and
    /// exists to refuse an absurd submission, not to constrain an operator.
    /// </remarks>
    public const int MaximumPositions = 500;

    /// <summary>The complaint when the request repositions nothing.</summary>
    public const string PositionsRequiredMessage =
        "At least one profile property position is required.";

    /// <summary>The complaint when the request carries more positions than may be written at once.</summary>
    public const string TooManyPositionsMessage =
        "No more than 500 profile property positions may be submitted in one request.";

    /// <summary>The complaint when one declaration is named more than once.</summary>
    /// <remarks>
    /// Two positions for one declaration have no defensible resolution: honouring the last would make the
    /// outcome depend on submission order, and honouring the first would silently discard something the
    /// caller asked for. The submission is refused instead.
    /// </remarks>
    public const string DuplicateDefinitionMessage =
        "Each profile property may be given a position only once in a request.";

    /// <summary>The complaint when a declaration identifier is not a positive key.</summary>
    public const string DefinitionIdentifierInvalidMessage =
        "A profile property identifier must be greater than zero.";

    /// <summary>The complaint when a position is negative.</summary>
    /// <remarks>
    /// Zero is legitimate and is the first position the seeded catalogue uses, so the bound is at zero
    /// rather than at one.
    /// </remarks>
    public const string ViewOrderInvalidMessage =
        "A profile property position cannot be negative.";

    /// <summary>
    /// Initialises a new instance of the
    /// <see cref="ReorderProfilePropertyDefinitionsRequestValidator"/> class and declares its rules.
    /// </summary>
    public ReorderProfilePropertyDefinitionsRequestValidator()
    {
        RuleLevelCascadeMode = CascadeMode.Stop;
        ClassLevelCascadeMode = CascadeMode.Continue;

        RuleFor(request => request.Positions)
            .NotEmpty().WithMessage(PositionsRequiredMessage)
            .Must(positions => positions is null || positions.Count <= MaximumPositions)
                .WithMessage(TooManyPositionsMessage)
            .Must(HaveDistinctDefinitions).WithMessage(DuplicateDefinitionMessage);

        RuleForEach(request => request.Positions).ChildRules(position =>
        {
            position.RuleFor(entry => entry.PropertyDefinitionId)
                .GreaterThan(0).WithMessage(DefinitionIdentifierInvalidMessage);

            position.RuleFor(entry => entry.ViewOrder)
                .GreaterThanOrEqualTo(0).WithMessage(ViewOrderInvalidMessage);
        });
    }

    /// <summary>Whether no declaration is named twice.</summary>
    /// <param name="positions">The submitted positions, possibly null.</param>
    /// <returns>True when every named declaration is distinct.</returns>
    private static bool HaveDistinctDefinitions(
        IReadOnlyList<ProfilePropertyDefinitionPosition>? positions)
    {
        if (positions is null || positions.Count < 2)
        {
            return true;
        }

        HashSet<int> seen = new(positions.Count);

        foreach (ProfilePropertyDefinitionPosition position in positions)
        {
            if (position is not null && !seen.Add(position.PropertyDefinitionId))
            {
                return false;
            }
        }

        return true;
    }
}
