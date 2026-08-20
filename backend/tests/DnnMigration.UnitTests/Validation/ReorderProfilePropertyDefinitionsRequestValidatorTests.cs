using DnnMigration.Application.Dtos.User;
using DnnMigration.Application.Validation;
using FluentAssertions;
using FluentValidation.Results;
using Xunit;

namespace DnnMigration.UnitTests.Validation;

/// <summary>
/// Proves the shape rules of <see cref="ReorderProfilePropertyDefinitionsRequestValidator"/>, the contract
/// bound by <c>PUT /api/v1/profile-definitions/order</c>.
/// </summary>
/// <remarks>
/// <para>
/// There is no legacy counterpart to be faithful to, because the legacy screen had no ordering endpoint: it
/// exchanged two positions by persisting two declarations through the same update call. That is precisely
/// what this contract replaces, and the reasoning is recorded on the request itself.
/// </para>
/// <para>
/// Every rule here is a SHAPE rule. Whether the named declarations exist and belong to the resolved tenant
/// is an admission question only the store can answer, and it is answered by the service - which resolves
/// every declaration before staging anything.
/// </para>
/// </remarks>
public sealed class ReorderProfilePropertyDefinitionsRequestValidatorTests
{
    private readonly ReorderProfilePropertyDefinitionsRequestValidator _validator = new();

    /// <summary>Builds one position pair.</summary>
    /// <param name="propertyDefinitionId">The declaration being positioned.</param>
    /// <param name="viewOrder">The position to give it.</param>
    /// <returns>A position pair.</returns>
    private static ProfilePropertyDefinitionPosition Position(int propertyDefinitionId, int viewOrder) =>
        new() { PropertyDefinitionId = propertyDefinitionId, ViewOrder = viewOrder };

    /// <summary>A two-declaration exchange - the ordinary case - is accepted.</summary>
    [Fact]
    public void Accepts_AnExchangeOfTwoPositions()
    {
        ValidationResult outcome = _validator.Validate(new ReorderProfilePropertyDefinitionsRequest
        {
            Positions = [Position(11, 1), Position(12, 0)],
        });

        outcome.IsValid.Should().BeTrue(
            "exchanging two neighbours is the operation this contract exists for");
    }

    /// <summary>
    /// A PARTIAL set is accepted, because exchanging two neighbours names two declarations rather than the
    /// whole catalogue.
    /// </summary>
    [Fact]
    public void Accepts_APartialSetRatherThanRequiringTheWholeCatalogue()
    {
        ValidationResult outcome = _validator.Validate(new ReorderProfilePropertyDefinitionsRequest
        {
            Positions = [Position(11, 7)],
        });

        outcome.IsValid.Should().BeTrue(
            "declarations the request does not name keep the positions they hold");
    }

    /// <summary>Position zero is legitimate, so the bound sits at zero rather than at one.</summary>
    [Fact]
    public void Accepts_PositionZero()
    {
        ValidationResult outcome = _validator.Validate(new ReorderProfilePropertyDefinitionsRequest
        {
            Positions = [Position(11, 0)],
        });

        outcome.IsValid.Should().BeTrue("zero is the first position the seeded catalogue uses");
    }

    /// <summary>Sparse positions are accepted, because the store never re-sequences them.</summary>
    [Fact]
    public void Accepts_SparsePositions()
    {
        ValidationResult outcome = _validator.Validate(new ReorderProfilePropertyDefinitionsRequest
        {
            Positions = [Position(11, 0), Position(12, 40), Position(13, 900)],
        });

        outcome.IsValid.Should().BeTrue(
            "the legacy grid exchanged two stored values and left the sequence sparse, so a gap is data "
            + "rather than a defect");
    }

    /// <summary>A request that repositions nothing is refused.</summary>
    [Fact]
    public void Refuses_AnEmptySet()
    {
        ValidationResult outcome = _validator.Validate(new ReorderProfilePropertyDefinitionsRequest());

        outcome.IsValid.Should().BeFalse();
        outcome.Errors.Should().ContainSingle()
            .Which.ErrorMessage.Should()
            .Be(ReorderProfilePropertyDefinitionsRequestValidator.PositionsRequiredMessage);
    }

    /// <summary>
    /// Naming one declaration twice is refused, because two positions for one declaration have no defensible
    /// resolution.
    /// </summary>
    /// <remarks>
    /// Honouring the last would make the outcome depend on submission order; honouring the first would
    /// silently discard something the caller asked for.
    /// </remarks>
    [Fact]
    public void Refuses_TheSameDeclarationTwice()
    {
        ValidationResult outcome = _validator.Validate(new ReorderProfilePropertyDefinitionsRequest
        {
            Positions = [Position(11, 0), Position(11, 1)],
        });

        outcome.IsValid.Should().BeFalse();
        outcome.Errors.Select(failure => failure.ErrorMessage).Should()
            .Contain(ReorderProfilePropertyDefinitionsRequestValidator.DuplicateDefinitionMessage);
    }

    /// <summary>A negative position is refused.</summary>
    [Fact]
    public void Refuses_ANegativePosition()
    {
        ValidationResult outcome = _validator.Validate(new ReorderProfilePropertyDefinitionsRequest
        {
            Positions = [Position(11, -1)],
        });

        outcome.IsValid.Should().BeFalse();
        outcome.Errors.Select(failure => failure.ErrorMessage).Should()
            .Contain(ReorderProfilePropertyDefinitionsRequestValidator.ViewOrderInvalidMessage);
    }

    /// <summary>A declaration identifier that is not a positive key is refused.</summary>
    /// <param name="propertyDefinitionId">The identifier under test.</param>
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Refuses_AnIdentifierThatIsNotAPositiveKey(int propertyDefinitionId)
    {
        ValidationResult outcome = _validator.Validate(new ReorderProfilePropertyDefinitionsRequest
        {
            Positions = [Position(propertyDefinitionId, 0)],
        });

        outcome.IsValid.Should().BeFalse();
        outcome.Errors.Select(failure => failure.ErrorMessage).Should()
            .Contain(ReorderProfilePropertyDefinitionsRequestValidator.DefinitionIdentifierInvalidMessage);
    }

    /// <summary>
    /// More positions than may be written at once is refused, and the bound sits far above any real
    /// catalogue.
    /// </summary>
    /// <remarks>
    /// The set is resolved and staged in memory before it is committed, so an unbounded list is an unbounded
    /// allocation on an authenticated path. The bound exists to refuse an absurd submission, not to
    /// constrain an operator - the seeded installation declares fewer than twenty properties.
    /// </remarks>
    [Fact]
    public void Refuses_MorePositionsThanMayBeWrittenAtOnce()
    {
        int bound = ReorderProfilePropertyDefinitionsRequestValidator.MaximumPositions;

        ProfilePropertyDefinitionPosition[] atTheBound = Enumerable
            .Range(1, bound)
            .Select(index => Position(index, index))
            .ToArray();

        _validator.Validate(new ReorderProfilePropertyDefinitionsRequest { Positions = atTheBound })
            .IsValid.Should().BeTrue("the bound itself is permitted");

        ProfilePropertyDefinitionPosition[] pastTheBound = Enumerable
            .Range(1, bound + 1)
            .Select(index => Position(index, index))
            .ToArray();

        ValidationResult outcome = _validator.Validate(
            new ReorderProfilePropertyDefinitionsRequest { Positions = pastTheBound });

        outcome.IsValid.Should().BeFalse();
        outcome.Errors.Select(failure => failure.ErrorMessage).Should()
            .Contain(ReorderProfilePropertyDefinitionsRequestValidator.TooManyPositionsMessage);
    }
}
