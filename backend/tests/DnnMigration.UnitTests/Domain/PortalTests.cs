using DnnMigration.Domain.Common;
using DnnMigration.Domain.Entities;
using DnnMigration.Domain.Enums;
using DnnMigration.Domain.ValueObjects;
using FluentAssertions;
using Xunit;

namespace DnnMigration.UnitTests.Domain;

/// <summary>
/// Covers the portal aggregate, the identity primitives it is built from, and the paging envelope
/// every portal listing is returned in.
/// </summary>
/// <remarks>
/// <para>
/// The point of this suite is the sentinel collision, not the property bag. <c>dbo.Portals.PortalID</c>
/// is declared <c>IDENTITY(-1, 1)</c>, so the first tenant of an installation is numbered -1 and the
/// second is numbered 0 - and -1 is simultaneously the value the legacy sentinel module used to mean
/// "no value at all". Every assertion below that involves -1 or 0 exists to pin the decision that the
/// domain treats both as ordinary identifiers, because a future refactor that "helpfully" reads -1 as
/// absence would silently make the first tenant of every installation unaddressable.
/// </para>
/// <para>
/// <see cref="PagedResult{T}"/> is covered here rather than in a suite of its own because portal
/// listing is the canonical paged read of the whole application, and because the type is a
/// <c>Domain.Common</c> primitive rather than an Application concern.
/// </para>
/// </remarks>
public class PortalTests
{
    private const int FirstIdentitySeed = -1;

    private const int SecondIdentityValue = 0;

    /// <summary>
    /// The aggregate reports its primary key as its identity.
    /// </summary>
    [Fact]
    public void Identity_IsThePrimaryKey()
    {
        Portal portal = NewPortal(42);

        portal.Identity.Should().Be(42);
    }

    /// <summary>
    /// The negative identity seed is an identifier rather than an absence marker.
    /// </summary>
    /// <param name="portalId">The identifier under test.</param>
    [Theory]
    [InlineData(FirstIdentitySeed)]
    [InlineData(SecondIdentityValue)]
    public void Identity_TreatsTheLegacySeedValuesAsIdentifiers(int portalId)
    {
        Portal portal = NewPortal(portalId);
        Portal sameRow = NewPortal(portalId);
        Portal otherRow = NewPortal(portalId + 1);

        // MIGRATION: identity-based comparison applies only once the persistence layer has declared
        // the identity real. That declaration is what this test stands in for: every candidate
        // "not saved yet" marker in this schema is a genuine key - the portal table seeds at -1 and
        // the role, page and module tables at 0 - so an undeclared instance is compared by object
        // reference instead, and two separately constructed instances are two different entities.
        portal.MarkIdentityPersisted();
        sameRow.MarkIdentityPersisted();
        otherRow.MarkIdentityPersisted();

        portal.Identity.Should().Be(portalId);
        portal.Should().Be(sameRow);
        portal.Should().NotBe(otherRow);
    }

    /// <summary>
    /// Two instances carrying the same identity are the same entity.
    /// </summary>
    [Fact]
    public void Equality_IsDecidedByIdentityAndNotByReference()
    {
        Portal left = NewPortal(7, "One");
        Portal right = NewPortal(7, "Another Name Entirely");

        // MIGRATION: identity-based comparison applies only once the persistence layer has declared
        // the identity real. That declaration is what this test stands in for: every candidate
        // "not saved yet" marker in this schema is a genuine key - the portal table seeds at -1 and
        // the role, page and module tables at 0 - so an undeclared instance is compared by object
        // reference instead, and two separately constructed instances are two different entities.
        // Both operands need the declaration, not just one: a one-sided declaration leaves the pair
        // unequal, which the dedicated test below pins.
        left.MarkIdentityPersisted();
        right.MarkIdentityPersisted();

        left.Equals(right).Should().BeTrue();
        (left == right).Should().BeTrue();
        (left != right).Should().BeFalse();
        left.GetHashCode().Should().Be(right.GetHashCode());
    }

    /// <summary>
    /// Identity does not span aggregates: two different entity types sharing a numeric key are distinct.
    /// </summary>
    [Fact]
    public void Equality_DoesNotHoldAcrossDifferentAggregates()
    {
        Entity<int> portal = NewPortal(0);
        Entity<int> role = new Role { RoleId = 0, RoleName = "Administrators" };

        portal.Equals(role).Should().BeFalse(
            "the zero identity seed is shared by Portals, Roles, Tabs and Modules, so identity alone "
            + "cannot decide equality");
        role.Equals(portal).Should().BeFalse();
        portal.GetHashCode().Should().NotBe(role.GetHashCode());
    }

    /// <summary>
    /// The equality operators answer for a null operand without dereferencing it.
    /// </summary>
    [Fact]
    public void Equality_HandlesNullOperandsOnBothSides()
    {
        Portal portal = NewPortal(1);
        Portal? absent = null;

        (portal == absent).Should().BeFalse();
        (absent == portal).Should().BeFalse();
        (absent != portal).Should().BeTrue();
        (absent == null).Should().BeTrue();
    }

    /// <summary>
    /// The untyped equality override refuses a null argument and anything that is not an entity.
    /// </summary>
    /// <remarks>
    /// The null comparison is asserted last on purpose. Under nullable reference types the compiler
    /// learns from an <c>Equals(null)</c> call - if the call could return true the receiver could be
    /// null - so any dereference of <c>portal</c> placed after it is a compile error rather than a
    /// warning, given that this solution treats warnings as errors.
    /// </remarks>
    [Fact]
    public void Equality_UntypedOverrideRefusesNullAndForeignObjects()
    {
        Portal portal = NewPortal(1);
        Portal sameRow = NewPortal(1);

        // MIGRATION: identity-based comparison applies only once the persistence layer has declared
        // the identity real. That declaration is what this test stands in for: every candidate
        // "not saved yet" marker in this schema is a genuine key - the portal table seeds at -1 and
        // the role, page and module tables at 0 - so an undeclared instance is compared by object
        // reference instead, and two separately constructed instances are two different entities.
        portal.MarkIdentityPersisted();
        sameRow.MarkIdentityPersisted();

        portal.Equals((object)"not an entity").Should().BeFalse();
        portal.Equals((object)sameRow).Should().BeTrue();
        portal.Equals((object?)null).Should().BeFalse();
    }

    /// <summary>
    /// Until the persistence layer declares an identity real, two separately constructed instances are
    /// two different entities even when their keys agree - and the hash code an instance has already
    /// answered with never changes afterwards.
    /// </summary>
    /// <remarks>
    /// These are the two halves of the arrangement that keeps this schema's seed values from being read
    /// as absences. The portal table is declared <c>IDENTITY(-1, 1)</c>, so -1 is simultaneously a real
    /// portal key and the legacy absent-integer marker; comparing raw keys immediately would make every
    /// freshly constructed portal collide with the genuine portal identified by -1. The latch matters for
    /// the opposite direction: an entity that has already been hashed into a set must not change its
    /// equality class when a key is assigned later, or the set loses the entry.
    /// </remarks>
    [Fact]
    public void Equality_IsReferenceBasedUntilTheIdentityIsDeclaredPersisted()
    {
        Portal left = NewPortal(-1);
        Portal right = NewPortal(-1);

        left.IdentityIsPersisted.Should().BeFalse("nothing has declared this instance's identity real");
        left.Equals(right).Should().BeFalse("two separately constructed instances are two entities");
        left.Equals(left).Should().BeTrue("an instance is always itself, declared or not");

        left.MarkIdentityPersisted();
        left.IdentityIsPersisted.Should().BeTrue();
        left.Equals(right).Should().BeFalse("the rule needs the declaration on both operands");

        right.MarkIdentityPersisted();
        left.Equals(right).Should().BeTrue();

        // The latch: an instance hashed before the declaration keeps the answer it gave, and stays on
        // reference-based comparison to remain consistent with it.
        Portal hashedEarly = NewPortal(-1);
        int firstAnswer = hashedEarly.GetHashCode();
        hashedEarly.MarkIdentityPersisted();

        hashedEarly.GetHashCode().Should().Be(firstAnswer, "a hash code is answered once and never moves");
        hashedEarly.Equals(left).Should().BeFalse(
            "switching to identity comparison after hashing would strand the entry in any live set");
    }

    /// <summary>
    /// A newly constructed aggregate exposes empty collections rather than null ones.
    /// </summary>
    [Fact]
    public void NavigationCollections_AreInitialisedRatherThanNull()
    {
        Portal portal = NewPortal(3);

        portal.PortalAliases.Should().NotBeNull().And.BeEmpty();
        portal.Modules.Should().NotBeNull().And.BeEmpty();
        portal.Tabs.Should().NotBeNull().And.BeEmpty();
        portal.Roles.Should().NotBeNull().And.BeEmpty();
        portal.UserPortals.Should().NotBeNull().And.BeEmpty();
        portal.PortalDesktopModules.Should().NotBeNull().And.BeEmpty();
    }

    /// <summary>
    /// The discriminator columns are modelled as named enumerations rather than bare integers.
    /// </summary>
    [Fact]
    public void Discriminators_AreModelledAsNamedEnumerations()
    {
        ((int)UserRegistrationMode.NoRegistration).Should().Be(0);
        ((int)UserRegistrationMode.PrivateRegistration).Should().Be(1);
        ((int)UserRegistrationMode.PublicRegistration).Should().Be(2);
        ((int)UserRegistrationMode.VerifiedRegistration).Should().Be(3);

        ((int)BannerAdvertisingMode.None).Should().Be(0);
        ((int)BannerAdvertisingMode.Site).Should().Be(1);
        ((int)BannerAdvertisingMode.Host).Should().Be(2);
    }

    /// <summary>
    /// The identity wrapper carries every integer through unchanged, sentinel values included.
    /// </summary>
    /// <param name="value">The identifier under test.</param>
    [Theory]
    [InlineData(FirstIdentitySeed)]
    [InlineData(SecondIdentityValue)]
    [InlineData(1)]
    [InlineData(int.MaxValue)]
    public void PortalIdValueObject_RoundTripsEveryIdentifier(int value)
    {
        PortalId identifier = (PortalId)value;

        identifier.Value.Should().Be(value);
        ((int)identifier).Should().Be(value);
        identifier.Should().Be(new PortalId(value));
        identifier.ToString().Should().Be(value.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// The identity wrapper keeps -1 and 0 distinct from each other and from a default instance.
    /// </summary>
    [Fact]
    public void PortalIdValueObject_KeepsTheSeedValuesDistinct()
    {
        PortalId first = new(FirstIdentitySeed);
        PortalId second = new(SecondIdentityValue);

        first.Should().NotBe(second);
        second.Should().Be(default(PortalId), "the default of the wrapper is zero, which is a real portal");
        first.Should().NotBe(default(PortalId));
    }

    /// <summary>
    /// The handle wrapper rejects the all-zero value, which is the legacy absence sentinel.
    /// </summary>
    [Fact]
    public void PortalGuid_RejectsTheAllZeroSentinel()
    {
        Action construct = () => _ = new PortalGuid(Guid.Empty);

        construct.Should().Throw<DomainException>()
            .WithMessage("*all-zero GUID*");

        PortalGuid.TryParse(Guid.Empty.ToString(), out PortalGuid parsed).Should().BeFalse();
        parsed.Should().Be(default(PortalGuid));
    }

    /// <summary>
    /// The handle wrapper accepts a real value through every entry point it offers.
    /// </summary>
    [Fact]
    public void PortalGuid_AcceptsARealHandleThroughEveryEntryPoint()
    {
        Guid value = Guid.Parse("9f1b0c7e-4d3a-4e21-9c88-7a5b2f6d1e04");

        PortalGuid constructed = new(value);
        PortalGuid converted = (PortalGuid)value;
        PortalGuid created = PortalGuid.From(value);
        PortalGuid parsed = PortalGuid.Parse(value.ToString());

        constructed.Value.Should().Be(value);
        converted.Should().Be(constructed);
        created.Should().Be(constructed);
        parsed.Should().Be(constructed);
        ((Guid)constructed).Should().Be(value);
        constructed.ToString().Should().Be(value.ToString());

        PortalGuid.TryParse(value.ToString(), out PortalGuid tried).Should().BeTrue();
        tried.Should().Be(constructed);
    }

    /// <summary>
    /// Parsing text that is not a handle is reported as a domain violation rather than a format error.
    /// </summary>
    /// <param name="candidate">The text under test.</param>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-guid")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    public void PortalGuid_ParseRejectsAnythingThatIsNotAHandle(string candidate)
    {
        Action parse = () => _ = PortalGuid.Parse(candidate);

        parse.Should().Throw<DomainException>();
        PortalGuid.TryParse(candidate, out _).Should().BeFalse();
    }

    /// <summary>
    /// A paged listing reports the page it returned and the total behind it independently.
    /// </summary>
    [Fact]
    public void PagedResult_ReportsThePageAndTheTotalIndependently()
    {
        PagedResult<Portal> page = PagedResult<Portal>.Create([NewPortal(1), NewPortal(2)], 25, 3, 2);

        page.Items.Should().HaveCount(2);
        page.TotalCount.Should().Be(25);
        page.PageIndex.Should().Be(3);
        page.PageSize.Should().Be(2);
        page.IsUnpaged.Should().BeFalse();
        page.TotalPages.Should().Be(13, "twenty-five rows at two per page is twelve full pages and a remainder");
        page.HasPreviousPage.Should().BeTrue();
        page.HasNextPage.Should().BeTrue();
    }

    /// <summary>
    /// The page arithmetic divides exactly when the total is a multiple of the page size.
    /// </summary>
    /// <param name="totalCount">The total behind the page.</param>
    /// <param name="pageSize">The page size.</param>
    /// <param name="expectedPages">The expected page count.</param>
    [Theory]
    [InlineData(0, 10, 0)]
    [InlineData(1, 10, 1)]
    [InlineData(10, 10, 1)]
    [InlineData(11, 10, 2)]
    [InlineData(20, 10, 2)]
    public void PagedResult_ComputesThePageCountFromTheTotal(int totalCount, int pageSize, int expectedPages)
    {
        PagedResult<Portal> page = PagedResult<Portal>.Create([], totalCount, 0, pageSize);

        page.TotalPages.Should().Be(expectedPages);
    }

    /// <summary>
    /// The last page reports no successor and the first reports no predecessor.
    /// </summary>
    [Fact]
    public void PagedResult_KnowsWhereItSitsInTheSequence()
    {
        PagedResult<Portal> first = PagedResult<Portal>.Create([NewPortal(1)], 3, 0, 1);
        PagedResult<Portal> last = PagedResult<Portal>.Create([NewPortal(3)], 3, 2, 1);
        PagedResult<Portal> beyond = PagedResult<Portal>.Create([], 3, 9, 1);

        first.HasPreviousPage.Should().BeFalse();
        first.HasNextPage.Should().BeTrue();
        last.HasPreviousPage.Should().BeTrue();
        last.HasNextPage.Should().BeFalse();
        beyond.Items.Should().BeEmpty();
        beyond.TotalCount.Should().Be(3, "asking beyond the end must still report how much there is");
        beyond.HasNextPage.Should().BeFalse();
    }

    /// <summary>
    /// An unpaged listing declares itself unpaged and reports one page for any non-empty result.
    /// </summary>
    [Fact]
    public void PagedResult_Unpaged_ReportsEverythingAsASinglePage()
    {
        PagedResult<Portal> unpaged = PagedResult<Portal>.Unpaged([NewPortal(1), NewPortal(2), NewPortal(3)]);

        unpaged.IsUnpaged.Should().BeTrue();
        unpaged.PageSize.Should().Be(0);
        unpaged.PageIndex.Should().Be(0);
        unpaged.TotalCount.Should().Be(3, "an unpaged listing takes its total from what it returned");
        unpaged.TotalPages.Should().Be(1);
        unpaged.HasPreviousPage.Should().BeFalse();
        unpaged.HasNextPage.Should().BeFalse();

        PagedResult<Portal> nothing = PagedResult<Portal>.Unpaged([]);

        nothing.IsUnpaged.Should().BeTrue();
        nothing.TotalPages.Should().Be(0, "an empty result has no pages at all, not one empty page");
    }

    /// <summary>
    /// The shared empty page is empty in every respect.
    /// </summary>
    [Fact]
    public void PagedResult_Empty_IsEmptyInEveryRespect()
    {
        PagedResult<Portal> empty = PagedResult<Portal>.Empty;

        empty.Items.Should().BeEmpty();
        empty.TotalCount.Should().Be(0);
        empty.PageIndex.Should().Be(0);
        empty.PageSize.Should().Be(0);
        empty.TotalPages.Should().Be(0);
        empty.HasPreviousPage.Should().BeFalse();
        empty.HasNextPage.Should().BeFalse();
    }

    /// <summary>
    /// Constructing a page from impossible arguments is rejected rather than absorbed.
    /// </summary>
    [Fact]
    public void PagedResult_RejectsImpossibleArguments()
    {
        Action nullItems = () => _ = PagedResult<Portal>.Create(null!, 0, 0, 10);
        Action negativeTotal = () => _ = PagedResult<Portal>.Create([], -1, 0, 10);
        Action negativeIndex = () => _ = PagedResult<Portal>.Create([], 0, -1, 10);
        Action negativeSize = () => _ = PagedResult<Portal>.Create([], 0, 0, -1);
        Action nullUnpaged = () => _ = PagedResult<Portal>.Unpaged(null!);

        nullItems.Should().Throw<ArgumentNullException>();
        negativeTotal.Should().Throw<ArgumentOutOfRangeException>();
        negativeIndex.Should().Throw<ArgumentOutOfRangeException>();
        negativeSize.Should().Throw<ArgumentOutOfRangeException>();
        nullUnpaged.Should().Throw<ArgumentNullException>();
    }

    /// <summary>
    /// Builds a minimally populated portal carrying the supplied identifier.
    /// </summary>
    /// <param name="portalId">The identifier to carry.</param>
    /// <param name="portalName">The display name.</param>
    /// <returns>The portal.</returns>
    private static Portal NewPortal(int portalId, string portalName = "Integration Portal") => new()
    {
        PortalId = portalId,
        PortalName = portalName,
        UserRegistration = UserRegistrationMode.PublicRegistration,
        BannerAdvertising = BannerAdvertisingMode.None,
        HostFee = 0m,
        HostSpace = 0,
        PortalGuid = Guid.NewGuid(),
        DefaultLanguage = "en-US",
        TimeZoneOffset = -8,
        HomeDirectory = string.Empty,
        PageQuota = 0,
        UserQuota = 0,
    };
}
