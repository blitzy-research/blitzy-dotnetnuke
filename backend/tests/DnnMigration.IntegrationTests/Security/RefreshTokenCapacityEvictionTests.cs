using DnnMigration.Infrastructure.Security;
using FluentAssertions;
using Xunit;

namespace DnnMigration.IntegrationTests.Security;

/// <summary>
/// Covers the capacity-eviction policy of <see cref="RefreshTokenStore"/>: which tracked refresh-token
/// generations are discarded when the tracked set outgrows its ceiling.
/// </summary>
/// <remarks>
/// <para>
/// THE UNIT OF EVICTION IS THE FAMILY, AND THAT IS A SECURITY PROPERTY. Reuse of a superseded generation is
/// what identifies a stolen refresh token, and the store answers it by revoking the whole rotation family. A
/// generation that has been evicted is indistinguishable from one that never existed, so a family left
/// HALF-TRACKED loses exactly that signal while remaining redeemable: presenting a stolen older generation
/// reads as an unknown token - refused, but with no family revocation - and the thief's live generation
/// survives. Evicting whole families means the worst outcome under memory pressure is that a family must sign
/// in again, which is what eviction is for.
/// </para>
/// <para>
/// An earlier revision ordered the individual generations by family ceiling and then by generation, took
/// exactly the excess, and asserted in its own remark that this "keeps a family's generations together". It
/// does not: the cut lands wherever the excess count falls, so any excess that ended inside a family's run of
/// generations split that family. These facts are what make the difference observable.
/// </para>
/// <para>
/// The policy is exercised directly rather than through the store because the production ceiling is a hundred
/// thousand generations and the store rescans its whole dictionary on every issue - reaching the eviction path
/// behaviourally would be quadratic work to assert three ordering decisions. This is the same shape as the
/// existing translator facts, which also exercise an internal policy helper against synthetic input.
/// </para>
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
    /// <remarks>
    /// This is the fact the previous implementation failed. With an excess of one and the nearest-ceiling
    /// family holding three generations, taking "exactly the excess" evicted ONE of those three and left the
    /// family live and half-tracked. The set is left further under the ceiling than strictly required, and that
    /// is the intended trade: stopping mid-family to hit an exact count is the defect.
    /// </remarks>
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

    /// <summary>
    /// Families are retired nearest-ceiling first, and only as many as the ceiling requires.
    /// </summary>
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
    /// <see langword="true"/> to present the entries in the opposite order, which a dictionary is free to do.
    /// </param>
    /// <remarks>
    /// Determinism matters for diagnosis rather than for security: an operator investigating why a particular
    /// family was retired must get the same answer from the same state, and a policy that depended on hash
    /// ordering would answer differently on two hosts holding identical data.
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
    /// The ceiling is shared by every generation of a family today, so this asserts a property of the policy
    /// rather than of the current store: the family is ranked by its EARLIEST ceiling, which keeps the ordering
    /// total and keeps the family a single unit if a future revision ever shortens one generation's ceiling.
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
