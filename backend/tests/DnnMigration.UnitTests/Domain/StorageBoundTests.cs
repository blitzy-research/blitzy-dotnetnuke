using System.Globalization;

using DnnMigration.Domain.Common;
using FluentAssertions;
using Xunit;

namespace DnnMigration.UnitTests.Domain;

/// <summary>
/// Pins the two helpers that state what the terminal store can physically hold, and the paging offset
/// helper that stopped an unrepresentable read position from wrapping.
/// </summary>
/// <remarks>
/// A security review found that CLR types were being treated as sufficient bounds although the stored
/// domains are narrower: a page offset multiplication could overflow a 32-bit integer, decimal amounts
/// could exceed what a currency column holds, and a CLR instant could fall centuries before the stored
/// calendar begins.
/// </remarks>
public class StorageBoundTests
{
    /// <summary>An ordinary read position is the plain product of the page index and the page size.</summary>
    /// <param name="pageIndex">The requested page.</param>
    /// <param name="pageSize">The requested page size.</param>
    /// <param name="expected">The position the reader should skip to.</param>
    [Theory]
    [InlineData(0, 25, 0)]
    [InlineData(1, 25, 25)]
    [InlineData(40, 100, 4_000)]
    [InlineData(0, 0, 0)]
    public void SkipCount_ForAnOrdinaryRequest_IsThePlainProduct(int pageIndex, int pageSize, int expected)
        => Paging.SkipCount(pageIndex, pageSize).Should().Be(expected);

    /// <summary>A product too large for a 32-bit integer is clamped rather than wrapped.</summary>
    [Fact]
    public void SkipCount_WhenTheProductExceedsTheAddressableRange_ClampsRatherThanWrapping()
    {
        const int pageIndex = 300_000_000;
        const int pageSize = 100;

        unchecked
        {
            (pageIndex * pageSize).Should().BeNegative(
                "the 32-bit product wraps, which is the defect this helper closes");
        }

        Paging.SkipCount(pageIndex, pageSize).Should().Be(int.MaxValue);
    }

    /// <summary>
    /// The largest page index and page size a validated request can carry still produce a representable
    /// position.
    /// </summary>
    [Fact]
    public void SkipCount_AtTheLargestValidatedRequest_IsRepresentable()
        => Paging.SkipCount(int.MaxValue, int.MaxValue).Should().Be(int.MaxValue);

    /// <summary>The currency bounds are the column's own, to the last of its four decimal places.</summary>
    [Fact]
    public void MoneyBounds_AreTheColumnsOwn()
    {
        SqlServerRange.MinimumMoney.Should().Be(-922_337_203_685_477.5808m);
        SqlServerRange.MaximumMoney.Should().Be(922_337_203_685_477.5807m);
    }

    /// <summary>An amount inside the currency range is storable and one outside it is not.</summary>
    [Fact]
    public void CanStore_DecidesAnAmountAtTheCurrencyBoundary()
    {
        SqlServerRange.CanStore(0m).Should().BeTrue("zero is a real price");
        SqlServerRange.CanStore(SqlServerRange.MaximumMoney).Should().BeTrue();
        SqlServerRange.CanStore(SqlServerRange.MinimumMoney).Should().BeTrue();
        SqlServerRange.CanStore(SqlServerRange.MaximumMoney + 0.0001m).Should().BeFalse();
        SqlServerRange.CanStore(SqlServerRange.MinimumMoney - 0.0001m).Should().BeFalse();
    }

    /// <summary>
    /// Scale is deliberately not tested, because the provider rounds a finer amount rather than refusing
    /// it.
    /// </summary>
    /// <remarks>
    /// Asserted so that a future change tightening this into a scale check is visibly a change of policy.
    /// Refusing a finer amount here would be this migration inventing a rule the legacy application did not
    /// have.
    /// </remarks>
    [Fact]
    public void CanStore_DoesNotRefuseAnAmountFinerThanTheColumnsScale()
        => SqlServerRange.CanStore(1.23456789m).Should().BeTrue();

    /// <summary>
    /// An amount converted for storage comes back carrying the column's four fractional digits, whatever
    /// scale it arrived with.
    /// </summary>
    /// <remarks>
    /// ⚠ THIS IS A TEST ABOUT TEXT, NOT ABOUT VALUE, AND `Should().Be(...)` CANNOT SEE THE DIFFERENCE.
    /// <see cref="decimal"/> equality ignores scale, so <c>4.5m</c> and <c>4.5000m</c> compare equal while
    /// serialising as <c>4.5</c> and <c>4.5000</c>. Rounding alone leaves the submitted scale in place, so one
    /// stored amount was published with two spellings - the shorter one in the response to a write, because
    /// that projects the amount as the caller sent it, and the longer one on every later read, because that
    /// projects it as the column handed it back. The assertion is therefore made on the rendered string, which
    /// is the only form in which the defect is visible.
    /// </remarks>
    /// <param name="submitted">The amount as a caller might submit it.</param>
    /// <param name="published">How it must be rendered once converted for storage.</param>
    [Theory]
    [InlineData(4.5, "4.5000")]
    [InlineData(19.99, "19.9900")]
    [InlineData(0, "0.0000")]
    [InlineData(12, "12.0000")]
    [InlineData(-3.5, "-3.5000")]
    public void ToStoredMoney_CarriesTheColumnsScaleWhateverScaleArrived(double submitted, string published)
    {
        decimal amount = (decimal)submitted;

        SqlServerRange.ToStoredMoney(amount)
            .ToString(CultureInfo.InvariantCulture)
            .Should()
            .Be(published);

        SqlServerRange.ToStoredMoney((decimal?)amount)!
            .Value.ToString(CultureInfo.InvariantCulture)
            .Should()
            .Be(published, "the nullable overload must not take a different path");
    }

    /// <summary>Rounding to the column's scale still happens, and still rounds away from zero.</summary>
    /// <remarks>
    /// Imposing the scale must not have replaced the rounding: an amount finer than the column can hold is
    /// still reduced to what it will actually store, and the half-way case still rounds away from zero rather
    /// than to even. Both halves are asserted on the rendered string for the reason above.
    /// </remarks>
    [Fact]
    public void ToStoredMoney_StillRoundsAnAmountFinerThanTheColumnHolds()
    {
        SqlServerRange.ToStoredMoney(1.234_567_89m)
            .ToString(CultureInfo.InvariantCulture)
            .Should()
            .Be("1.2346");

        SqlServerRange.ToStoredMoney(0.000_05m)
            .ToString(CultureInfo.InvariantCulture)
            .Should()
            .Be("0.0001", "the half-way case rounds away from zero, as it did before the scale was imposed");
    }

    /// <summary>There is no amount at all when there was none to begin with.</summary>
    [Fact]
    public void ToStoredMoney_LeavesAnAbsentAmountAbsent()
        => SqlServerRange.ToStoredMoney((decimal?)null).Should().BeNull();

    /// <summary>The calendar bounds begin where the stored column begins, not where the CLR type does.</summary>
    [Fact]
    public void DateBounds_BeginWhereTheColumnBegins()
    {
        SqlServerRange.MinimumDateTime.Should().Be(new DateTime(1753, 1, 1, 0, 0, 0, DateTimeKind.Unspecified));
        SqlServerRange.CanStore(DateTime.MinValue).Should().BeFalse(
            "the CLR type begins in the year one and the column does not");
    }

    /// <summary>
    /// The preserved perpetual value is storable, which is the whole reason the upper bound sits at the
    /// last instant of its day rather than at that day's midnight.
    /// </summary>
    /// <remarks>
    /// This is the most consequential assertion in this file. The literal <c>9999-12-31</c> is an ordinary
    /// stored value meaning "no expiry" that a legacy reader expects to see verbatim, and it is distinct
    /// from the sentinel minimum date which means absence.
    /// </remarks>
    [Fact]
    public void CanStore_AdmitsThePreservedPerpetualValue()
    {
        SqlServerRange.CanStore(new DateTime(9999, 12, 31, 0, 0, 0, DateTimeKind.Utc)).Should().BeTrue();
        SqlServerRange.CanStore(new DateTime(9999, 12, 31, 0, 0, 0, DateTimeKind.Unspecified)).Should().BeTrue();
        SqlServerRange.CanStore(SqlServerRange.MaximumDateTime).Should().BeTrue();
        SqlServerRange.CanStore(DateTime.MaxValue).Should().BeFalse(
            "the type's own maximum carries finer precision than the column holds");
    }

    /// <summary>The kind of an instant does not decide whether it is storable.</summary>
    /// <remarks>
    /// The column records no offset and carries no kind, so two values differing only in kind are the same
    /// stored value; admitting one and refusing the other would be arbitrary.
    /// </remarks>
    [Fact]
    public void CanStore_IgnoresTheKindOfAnInstant()
    {
        var instant = new DateTime(2024, 6, 1, 12, 0, 0);

        SqlServerRange.CanStore(DateTime.SpecifyKind(instant, DateTimeKind.Utc)).Should().BeTrue();
        SqlServerRange.CanStore(DateTime.SpecifyKind(instant, DateTimeKind.Local)).Should().BeTrue();
        SqlServerRange.CanStore(DateTime.SpecifyKind(instant, DateTimeKind.Unspecified)).Should().BeTrue();
    }

    /// <summary>Absence is storable on both optional overloads.</summary>
    /// <remarks>
    /// The optional overloads exist so a validation rule can be stated over the member a caller submitted
    /// rather than over its unwrapped value, which keeps the reported field name matching the submitted
    /// one. Admitting absence is what lets those rules drop their presence guard, so it is asserted rather
    /// than assumed.
    /// </remarks>
    [Fact]
    public void CanStore_TreatsAbsenceAsStorable()
    {
        SqlServerRange.CanStore((DateTime?)null).Should().BeTrue();
        SqlServerRange.CanStore((decimal?)null).Should().BeTrue();
        SqlServerRange.CanStore((DateTime?)DateTime.MinValue).Should().BeFalse();
        SqlServerRange.CanStore((decimal?)(SqlServerRange.MaximumMoney + 0.0001m)).Should().BeFalse();
    }
}
