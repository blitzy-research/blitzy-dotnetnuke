using DnnMigration.Infrastructure.Security;
using FluentAssertions;
using Xunit;

namespace DnnMigration.IntegrationTests.Security;

/// <summary>
/// Covers the capacity-eviction policy of <see cref="RefreshTokenStore"/>: which tracked refresh-token
/// generations are discarded when the tracked set outgrows its ceiling.
/// </summary>
/// <remarks>
/// The policy is exercised directly rather than through the store because the production ceiling is a
/// hundred thousand generations and the store rescans its whole dictionary on every issue - reaching the
/// eviction path behaviourally would be quadratic work to assert three ordering decisions.
/// </remarks>
[Trait("Category", "Integration")]
public class RefreshTokenCapacityEvictionTests
{
    private static readonly DateTime Origin = new(2026, 3, 1, 12, 0, 0, DateTimeKind.Utc);

    private static readonly Guid FirstFamily = new("11111111-1111-1111-1111-111111111111");
    private static readonly Guid SecondFamily = new("22222222-2222-2222-2222-222222222222");
    private static readonly Guid ThirdFamily = new("33333333-3333-3333-3333-333333333333");

    /// <summary>Nothing is evicted while the tracked set fits its ceiling.</summary>
    [Fact]
    public void WithinTheCeiling_NothingIsEvicted()
    {
        IReadOnlyList<string> evictions = RefreshTokenStore.SelectCapacityEvictions(
            [
                ("a1", FirstFamily, Origin.AddDays(1)),
                ("a2", FirstFamily, Origin.AddDays(1)),
                ("b1", SecondFamily, Origin.AddDays(2)),
            ],
            maximumTracked: 3);

        evictions.Should().BeEmpty();
    }

    /// <summary>
    /// A family is never split: an excess of one still retires every generation of the family chosen.
    /// </summary>
    [Fact]
    public void AnExcessOfOne_StillRetiresTheWholeNearestFamily()
    {
        IReadOnlyList<string> evictions = RefreshTokenStore.SelectCapacityEvictions(
            [
                ("a1", FirstFamily, Origin.AddDays(1)),
                ("a2", FirstFamily, Origin.AddDays(1)),
                ("a3", FirstFamily, Origin.AddDays(1)),
                ("b1", SecondFamily, Origin.AddDays(5)),
            ],
            maximumTracked: 3);

        evictions.Should().BeEquivalentTo(
            ["a1", "a2", "a3"],
            "a family is retired as a unit, so the set drops below the ceiling rather than splitting one");
        evictions.Should().NotContain("b1", "the family furthest from its ceiling is kept");
    }

    /// <summary>Families are retired nearest-ceiling first, and only as many as the ceiling requires.</summary>
    [Fact]
    public void FamiliesAreRetiredNearestCeilingFirstAndOnlyAsManyAsNeeded()
    {
        IReadOnlyList<string> evictions = RefreshTokenStore.SelectCapacityEvictions(
            [
                ("c1", ThirdFamily, Origin.AddDays(9)),
                ("a1", FirstFamily, Origin.AddDays(1)),
                ("b1", SecondFamily, Origin.AddDays(4)),
                ("b2", SecondFamily, Origin.AddDays(4)),
                ("c2", ThirdFamily, Origin.AddDays(9)),
            ],
            maximumTracked: 3);

        evictions.Should().BeEquivalentTo(
            ["a1", "b1", "b2"],
            "the two families nearest their ceiling go, entire, and the furthest is untouched");
        evictions.Should().NotContain("c1");
        evictions.Should().NotContain("c2");
    }

    /// <summary>
    /// The choice between families sharing a ceiling is deterministic rather than dependent on enumeration
    /// order.
    /// </summary>
    /// <param name="reversed">
    /// <see langword="true"/> to present the entries in the opposite order, which a dictionary is free to
    /// do.
    /// </param>
    /// <remarks>
    /// Determinism matters for diagnosis rather than for security: an operator investigating why a
    /// particular family was retired must get the same answer from the same state, and a policy that
    /// depended on hash ordering would answer differently on two hosts holding identical data.
    /// </remarks>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FamiliesSharingACeilingAreTieBrokenDeterministically(bool reversed)
    {
        (string Key, Guid FamilyId, DateTime FamilyExpiresAtUtc)[] entries =
        [
            ("a1", FirstFamily, Origin.AddDays(3)),
            ("b1", SecondFamily, Origin.AddDays(3)),
            ("c1", ThirdFamily, Origin.AddDays(3)),
        ];

        if (reversed)
        {
            Array.Reverse(entries);
        }

        IReadOnlyList<string> evictions = RefreshTokenStore.SelectCapacityEvictions(
            entries,
            maximumTracked: 2);

        evictions.Should().Equal(
            ["a1"],
            "the lowest family identifier is chosen whichever order the entries arrive in");
    }

    /// <summary>
    /// A ceiling shortened for part of a family does not reorder that family behind its own generations.
    /// </summary>
    /// <remarks>
    /// The ceiling is shared by every generation of a family today, so this asserts a property of the
    /// policy rather than of the current store: the family is ranked by its EARLIEST ceiling, which keeps
    /// the ordering total and keeps the family a single unit if a future revision ever shortens one
    /// generation's ceiling.
    /// </remarks>
    [Fact]
    public void AFamilyIsRankedByItsEarliestCeiling()
    {
        IReadOnlyList<string> evictions = RefreshTokenStore.SelectCapacityEvictions(
            [
                ("a1", FirstFamily, Origin.AddDays(20)),
                ("a2", FirstFamily, Origin.AddDays(1)),
                ("b1", SecondFamily, Origin.AddDays(10)),
            ],
            maximumTracked: 2);

        evictions.Should().BeEquivalentTo(
            ["a1", "a2"],
            "the family holding the earliest ceiling is retired entire, later generations included");
    }
}
