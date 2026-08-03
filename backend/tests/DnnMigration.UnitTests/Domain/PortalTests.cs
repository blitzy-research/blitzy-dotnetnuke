using System.Globalization;
using System.Reflection;
using DnnMigration.Domain.Common;
using DnnMigration.Domain.Entities;
using DnnMigration.Domain.Enums;
using DnnMigration.Domain.ValueObjects;
using FluentAssertions;
using Xunit;

namespace DnnMigration.UnitTests.Domain;

/// <summary>
/// Covers the portal aggregate, the identity and address primitives it is built from, and the paging
/// envelope every portal listing is returned in.
/// </summary>
/// <remarks>
/// <para>
/// The point of this suite is the sentinel boundary, not the property bag. <c>dbo.Portals.PortalID</c>
/// is declared <c>IDENTITY(-1, 1)</c>, so the first tenant of an installation is numbered -1 and the
/// second is numbered 0 - and -1 is simultaneously the value the legacy sentinel module used to mean
/// "no value at all". Every assertion below that involves -1 or 0 exists to pin the decision that the
/// domain treats both as ordinary identifiers, because a future refactor that "helpfully" reads -1 as
/// absence would silently make the first tenant of every installation unaddressable.
/// </para>
/// <para>
/// The sentinel matrix this file pins, value by value:
/// <list type="bullet">
/// <item><description>
/// <c>-1</c> - a real <c>Portals.PortalID</c> and simultaneously the legacy <c>Null.NullInteger</c>.
/// Accepted everywhere as an identifier; never absence.
/// </description></item>
/// <item><description>
/// <c>0</c> - a real <c>Portals.PortalID</c>, carried by the portal a fresh installation ships with,
/// and also <c>default(int)</c>. Never absence.
/// </description></item>
/// <item><description>
/// <c>""</c> - the legacy <c>Null.NullString</c>. A stored empty string stays an empty string; nothing
/// converts it to null.
/// </description></item>
/// <item><description>
/// <c>DateTime.MinValue</c> - the legacy <c>Null.NullDate</c>. Now an ordinary date, distinct from the
/// null that expresses absence.
/// </description></item>
/// <item><description>
/// <c>Guid.Empty</c> - the legacy <c>Null.NullGuid</c>. The one sentinel that <em>is</em> rejected,
/// and only because the backing column cannot produce it.
/// </description></item>
/// </list>
/// </para>
/// <para>
/// Absence is expressed by a nullable type in every case - <c>PortalId?</c>, <c>DateTime?</c>,
/// <c>EmailAddress?</c> - and never by a reserved value. That is Rule T7: the sentinels survive at the
/// DTO and API boundary, where a legacy consumer can observe them, and nowhere else.
/// </para>
/// <para>
/// <see cref="PagedResult{T}"/> is covered here rather than in a suite of its own because portal
/// listing is the canonical paged read of the whole application, and because the type is a
/// <c>Domain.Common</c> primitive rather than an Application concern.
/// </para>
/// </remarks>
public class PortalTests
{
    /// <summary>
    /// The identity seed of <c>dbo.Portals</c>, which is also the legacy absent-integer marker.
    /// </summary>
    /// <remarks>
    /// Measured at <c>Website/Providers/DataProviders/SqlDataProvider/01.00.00.SqlDataProvider:L77</c>,
    /// <c>[PortalID] [int] IDENTITY (-1, 1) NOT NULL</c>.
    /// </remarks>
    private const int FirstIdentitySeed = -1;

    /// <summary>
    /// The second identifier the portal table allocates, and the key of the portal a fresh
    /// installation ships with.
    /// </summary>
    /// <remarks>
    /// The shipped row is identity-inserted at <c>01.00.00.SqlDataProvider:L7125</c>, so a guard of the
    /// form <c>value == 0</c> or <c>value &lt;= 0</c> would reject the default portal of every
    /// installation.
    /// </remarks>
    private const int SecondIdentityValue = 0;

    /// <summary>
    /// The handle of the portal a fresh installation ships with, taken verbatim from the seed row.
    /// </summary>
    /// <remarks>
    /// <c>01.00.00.SqlDataProvider:L7125</c> supplies this literal for the <c>_default</c> portal, so it
    /// is real production data rather than a value invented for a test.
    /// </remarks>
    private const string ShippedDefaultPortalHandle = "57ad7180-c5e7-49f5-b282-c6475cdb7ee7";

    /// <summary>
    /// Names that must never appear on an identity value object, because each would reintroduce a
    /// reserved "no value" concept that the schema makes unsafe.
    /// </summary>
    /// <remarks>
    /// Asserted by reflection rather than by review, so that adding one of them to
    /// <see cref="PortalId"/> or <see cref="PortalGuid"/> fails the build instead of passing unnoticed.
    /// </remarks>
    private static readonly string[] ForbiddenAbsenceMemberNames =
    [
        "None",
        "Empty",
        "Null",
        "NullValue",
        "Unspecified",
        "NotSet",
        "Invalid",
        "IsNull",
        "IsEmpty",
        "HasValue",
        "IsTransient",
        "IsNew",
    ];

    // ---------------------------------------------------------------------------------------------
    // Portal aggregate - identity and equality
    // ---------------------------------------------------------------------------------------------

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
        // the identity real. Both operands need the declaration, not just one: a one-sided declaration
        // leaves the pair unequal, which the dedicated test below pins.
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

        portal.MarkIdentityPersisted();
        role.MarkIdentityPersisted();

        portal.Equals(role).Should().BeFalse(
            "the zero identity seed is shared by Portals, Roles, Tabs and Modules, so identity alone "
            + "cannot decide equality");
        role.Equals(portal).Should().BeFalse();

        // Hash codes are asserted only where the contract guarantees an answer: equal values must hash
        // alike. Two unequal values are permitted to collide, so asserting they differ would be a test
        // that is allowed to fail.
        portal.GetHashCode().Should().Be(portal.GetHashCode(), "hashing is stable for one instance");
        role.GetHashCode().Should().Be(role.GetHashCode());
    }

    /// <summary>
    /// A portal and one of its aliases never compare equal, even when both keys are the same number.
    /// </summary>
    /// <remarks>
    /// The exact-runtime-type comparison is what makes this hold. Both types close
    /// <c>Entity&lt;int&gt;</c> over the same identity type, so without the type check an alias keyed 1
    /// would equal a portal keyed 1 - and the two tables genuinely do share small key values.
    /// </remarks>
    [Fact]
    public void Equality_DoesNotHoldBetweenAPortalAndItsAlias()
    {
        Entity<int> portal = NewPortal(1);
        Entity<int> alias = NewPortalAlias(1, FirstIdentitySeed, "localhost");

        portal.MarkIdentityPersisted();
        alias.MarkIdentityPersisted();

        portal.Identity.Should().Be(alias.Identity, "the premise of this test is that the keys agree");
        portal.Equals(alias).Should().BeFalse();
        alias.Equals(portal).Should().BeFalse();
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
        Portal left = NewPortal(FirstIdentitySeed);
        Portal right = NewPortal(FirstIdentitySeed);

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
        Portal hashedEarly = NewPortal(FirstIdentitySeed);
        int firstAnswer = hashedEarly.GetHashCode();
        hashedEarly.MarkIdentityPersisted();

        hashedEarly.GetHashCode().Should().Be(firstAnswer, "a hash code is answered once and never moves");
        hashedEarly.Equals(left).Should().BeFalse(
            "switching to identity comparison after hashing would strand the entry in any live set");
    }

    /// <summary>
    /// Persisted state is declared by the persistence layer, never deduced from the key's value.
    /// </summary>
    /// <remarks>
    /// This is the positive statement of why no <c>IsTransient</c>, <c>IsNew</c> or
    /// <c>Identity == default</c> test exists anywhere in the model. Both -1 and 0 are real keys in this
    /// schema, so there is no value left over to mean "not written yet"; the flag carries that fact
    /// instead, and it is asserted here for both collision values.
    /// </remarks>
    /// <param name="portalId">The identifier under test.</param>
    [Theory]
    [InlineData(FirstIdentitySeed)]
    [InlineData(SecondIdentityValue)]
    public void IdentityIsPersisted_IsDeclaredRatherThanInferredFromTheKey(int portalId)
    {
        Portal portal = NewPortal(portalId);

        portal.IdentityIsPersisted.Should().BeFalse(
            "a key of -1 or 0 says nothing about whether a row exists");

        portal.MarkIdentityPersisted();
        portal.IdentityIsPersisted.Should().BeTrue();

        portal.MarkIdentityPersisted();
        portal.IdentityIsPersisted.Should().BeTrue("the declaration is idempotent and one-way");
        portal.Identity.Should().Be(portalId, "declaring the identity persisted does not alter it");
    }

    // ---------------------------------------------------------------------------------------------
    // Portal aggregate - shape and the sentinel boundary
    // ---------------------------------------------------------------------------------------------

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
    /// An empty navigation collection means the portal has no such rows, never that they were not
    /// loaded.
    /// </summary>
    /// <remarks>
    /// Which end a query populated is the repository's decision, so load state is deliberately not
    /// discoverable from the entity. This test pins the only guarantee the entity does make: the
    /// collection is a real, mutable collection that reports what it holds.
    /// </remarks>
    [Fact]
    public void NavigationCollections_ReportWhatTheyHold()
    {
        Portal portal = NewPortal(SecondIdentityValue);

        portal.PortalAliases.Should().BeEmpty();

        portal.PortalAliases.Add(NewPortalAlias(1, SecondIdentityValue, "localhost"));
        portal.PortalAliases.Add(NewPortalAlias(2, SecondIdentityValue, "www.example.com"));

        portal.PortalAliases.Should().HaveCount(2);
        portal.Modules.Should().BeEmpty("adding an alias says nothing about any other relationship");
    }

    /// <summary>
    /// The discriminator columns are modelled as named enumerations rather than bare integers.
    /// </summary>
    /// <remarks>
    /// The ordinals are persisted data that the legacy administration screens bound as a list index, so
    /// they are asserted individually. Renumbering or reordering a member would silently repoint every
    /// existing row at a different meaning.
    /// </remarks>
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
    /// The discriminator values the shipped portal row carries resolve to named members, and their
    /// ordinals survive the round trip.
    /// </summary>
    /// <remarks>
    /// <c>01.00.00.SqlDataProvider:L7125</c> stores <c>UserRegistration = 2</c> and
    /// <c>BannerAdvertising = 0</c>, so both numbers are live production data rather than defaults that
    /// happen to be unused. Zero being a named member matters especially: it is the value an unset
    /// enumeration field would also hold, so the enumeration has to mean something at zero.
    /// </remarks>
    [Fact]
    public void Discriminators_MapTheShippedPortalRowValues()
    {
        Portal portal = NewPortal(SecondIdentityValue);

        portal.UserRegistration = (UserRegistrationMode)2;
        portal.BannerAdvertising = (BannerAdvertisingMode)0;

        portal.UserRegistration.Should().Be(UserRegistrationMode.PublicRegistration);
        portal.BannerAdvertising.Should().Be(BannerAdvertisingMode.None);
        ((int)portal.UserRegistration).Should().Be(2);
        ((int)portal.BannerAdvertising).Should().Be(0);
    }

    /// <summary>
    /// Absence is a null, and every legacy sentinel that reaches a property is stored exactly as given.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the literal discharge of Rule T7. The legacy sentinel module represented an absent string
    /// as the empty string and an absent integer as -1, and the shipped portal row proves both were
    /// stored rather than merely computed: <c>01.00.00.SqlDataProvider:L7125</c> writes
    /// <c>PayPalId = ''</c> and <c>HostFee = ''</c>. So nothing in the domain may translate
    /// <c>""</c> into null, translate -1 into null, or translate a null back into either.
    /// </para>
    /// <para>
    /// The properties are plain auto-properties, so this test is not looking for cleverness - it is
    /// pinning the absence of it, which is exactly the kind of change a later refactor is tempted to
    /// make.
    /// </para>
    /// </remarks>
    [Fact]
    public void SentinelBoundary_AbsenceIsNullAndStoredValuesAreNotReinterpreted()
    {
        Portal portal = NewPortal(SecondIdentityValue);

        // A stored empty string is an empty footer, not a missing one.
        portal.FooterText = string.Empty;
        portal.FooterText.Should().NotBeNull().And.BeEmpty(
            "the legacy Null.NullString was the empty string, and the shipped row stores empty strings");

        // A null is an absent value and stays null.
        portal.LogoFile = null;
        portal.LogoFile.Should().BeNull();

        // MIGRATION: the legacy AdministratorId carried -1 to mean "nobody assigned". The column is
        // nullable, so absence is now null - but -1 remains a storable integer and is never rewritten.
        // A user identifier of -1 is not a portal identifier and carries no host-level meaning here;
        // the point is only that the property does not edit what it is given.
        portal.AdministratorId = null;
        portal.AdministratorId.Should().BeNull("absence is the null, not the -1");

        portal.AdministratorId = FirstIdentitySeed;
        portal.AdministratorId.Should().Be(FirstIdentitySeed, "a stored -1 is returned unchanged");

        // Zero is likewise an ordinary value: AdministratorRoleId is 0 in the shipped row, because
        // dbo.Roles.RoleID seeds at 0 as well.
        portal.AdministratorRoleId = 0;
        portal.AdministratorRoleId.Should().Be(0).And.NotBeNull();

        portal.SiteLogHistory = 0;
        portal.SiteLogHistory.Should().Be(0, "zero days of retention is a setting, not an absent one");
    }

    /// <summary>
    /// A portal that never expires says so with a null, and the legacy floor date is now an ordinary
    /// date.
    /// </summary>
    /// <remarks>
    /// <para>
    /// MIGRATION: the legacy property was a non-nullable date, so a SQL null arrived as
    /// <c>DateTime.MinValue</c> - the <c>Null.NullDate</c> sentinel - and "never expires" was
    /// indistinguishable from "expired in year one". The shipped portal row stores
    /// <c>ExpiryDate = NULL</c> at <c>01.00.00.SqlDataProvider:L7125</c>, so the ambiguous case is the
    /// common one. Absence is now the null and the floor date is just a date.
    /// </para>
    /// <para>
    /// MIGRATION: the legacy null test compared only the date part, so any time on the floor date also
    /// read as absent. Nothing here truncates a time component, which is asserted below because a
    /// well-meaning "normalisation" would quietly restore the old behaviour.
    /// </para>
    /// </remarks>
    [Fact]
    public void SentinelBoundary_ExpiryDateExpressesNeverExpiresAsNull()
    {
        Portal portal = NewPortal(SecondIdentityValue);

        portal.ExpiryDate.Should().BeNull("a portal with no expiry carries no date at all");

        portal.ExpiryDate = DateTime.MinValue;
        portal.ExpiryDate.Should().Be(DateTime.MinValue);
        portal.ExpiryDate.Should().NotBeNull(
            "the legacy sentinel is now an ordinary value and no longer means absence");

        DateTime justAfterTheFloor = DateTime.MinValue.AddHours(5);
        portal.ExpiryDate = justAfterTheFloor;
        portal.ExpiryDate.Should().Be(justAfterTheFloor, "the time component is preserved exactly");

        portal.ExpiryDate = null;
        portal.ExpiryDate.Should().BeNull();
    }

    /// <summary>
    /// The host fee is an exact decimal, so an amount round trips without the drift a binary float
    /// introduces.
    /// </summary>
    /// <remarks>
    /// MIGRATION: the legacy property was a <c>Single</c>. The column disagreed with it and the column
    /// is the authority: it began as <c>nvarchar(10) NULL</c>
    /// (<c>01.00.00.SqlDataProvider:L89</c>, which is why the shipped row stores the empty string) and
    /// was rebuilt as <c>money NOT NULL</c> during a temporary-table swap at
    /// <c>01.00.05.SqlDataProvider:L1377</c>, the data carried across by
    /// <c>CONVERT(money, HostFee)</c>. That rebuild is invisible to a search for <c>ALTER TABLE</c>,
    /// which is why the type was verified from the swap rather than from the alterations. Recorded in
    /// MIGRATION_NOTES.md under the exact-decimal correction.
    /// </remarks>
    [Fact]
    public void SentinelBoundary_HostFeeIsAnExactDecimal()
    {
        Portal portal = NewPortal(SecondIdentityValue);

        portal.HostFee.Should().Be(0m, "the column defaults to zero and the shipped row converts to it");

        portal.HostFee = 19.99m;
        portal.HostFee.Should().Be(19.99m);

        // Three tenths cannot be represented exactly in binary floating point, so summing it ten times
        // as a float drifts away from three. Decimal arithmetic does not.
        decimal accumulated = 0m;
        for (int index = 0; index < 10; index++)
        {
            accumulated += 0.3m;
        }

        portal.HostFee = accumulated;
        portal.HostFee.Should().Be(3.0m, "an exact type is what makes a currency amount comparable");

        typeof(Portal).GetProperty(nameof(Portal.HostFee))!.PropertyType.Should().Be<decimal>();
    }

    /// <summary>
    /// The portal a fresh installation ships with is representable in full, exactly as the seed row
    /// stores it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every value below is copied verbatim from the identity-insert at
    /// <c>01.00.00.SqlDataProvider:L7123-L7128</c>, which is the strongest single test in this file:
    /// it is real shipped data rather than a constructed example, and it exercises the two collision
    /// values together - the row is keyed 0 and its administrator role is keyed 0 - alongside a null
    /// expiry, an empty-string fee and a concrete handle.
    /// </para>
    /// <para>
    /// The row's <c>PortalAlias</c> and <c>UploadDirectory</c> columns are deliberately not asserted:
    /// neither survives to the terminal schema, so neither is a member of this entity. The fee is
    /// asserted as zero rather than as an empty string because the column was rebuilt to
    /// <c>money</c>, and the conversion of that stored empty string is what produces zero.
    /// </para>
    /// </remarks>
    [Fact]
    public void ShippedDefaultPortal_IsRepresentableExactlyAsSeeded()
    {
        Portal shipped = new()
        {
            PortalId = SecondIdentityValue,
            PortalName = "DotNetNuke",
            LogoFile = "logo.gif",
            FooterText = "Copyright 2002-2004 DotNetNuke",
            ExpiryDate = null,
            UserRegistration = UserRegistrationMode.PublicRegistration,
            BannerAdvertising = BannerAdvertisingMode.None,
            AdministratorId = 9,
            Currency = "USD",
            HostFee = 0m,
            HostSpace = 5,
            AdministratorRoleId = 0,
            RegisteredRoleId = 11,
            PortalGuid = Guid.Parse(ShippedDefaultPortalHandle),
            DefaultLanguage = "en-US",
            TimeZoneOffset = -8,
            HomeDirectory = string.Empty,
        };

        shipped.PortalId.Should().Be(0, "the shipped portal is identity-inserted with key 0");
        shipped.Identity.Should().Be(0);
        shipped.PortalName.Should().Be("DotNetNuke");
        shipped.ExpiryDate.Should().BeNull();
        shipped.UserRegistration.Should().Be(UserRegistrationMode.PublicRegistration);
        shipped.BannerAdvertising.Should().Be(BannerAdvertisingMode.None);
        shipped.AdministratorId.Should().Be(9);
        shipped.Currency.Should().Be("USD");
        shipped.HostFee.Should().Be(0m);
        shipped.HostSpace.Should().Be(5);
        shipped.AdministratorRoleId.Should().Be(0, "dbo.Roles.RoleID seeds at 0, so 0 keys a real role");
        shipped.RegisteredRoleId.Should().Be(11);
        shipped.PortalGuid.Should().Be(Guid.Parse(ShippedDefaultPortalHandle));

        // The handle is a real one, so the guarding value object accepts it - which is the point of
        // rejecting only the all-zero value rather than rejecting broadly.
        PortalGuid.From(shipped.PortalGuid).Value.Should().Be(shipped.PortalGuid);

        // And the key is a real one, so the guarding value object accepts it too.
        new PortalId(shipped.PortalId).Value.Should().Be(0);
    }

    /// <summary>
    /// The members the legacy class computed rather than stored are absent from the entity.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The legacy <c>PortalInfo</c> was filled from a view, so five of its 39 properties came from joins
    /// and subqueries - <c>Email</c>, <c>SuperTabId</c>, <c>AdministratorRoleName</c>,
    /// <c>RegisteredRoleName</c> and <c>Version</c> - and three more were not data at all.
    /// </para>
    /// <para>
    /// MIGRATION: <c>Users</c> and <c>Pages</c> were lazy getters that opened a database connection on
    /// read, each guarded by <c>&lt; 0</c> so that any negative value triggered a reload. A property
    /// that performs I/O violates both the rule that all data access goes through a repository
    /// interface and the rule that every I/O-bound member is asynchronous, so neither can exist here;
    /// both are Application-layer projections instead.
    /// </para>
    /// <para>
    /// MIGRATION: <c>HomeDirectoryMapPath</c> resolved a physical path through the file-system
    /// subsystem and the static globals module, both of which are out of scope. Path resolution belongs
    /// to the hosting environment abstraction, so the entity carries the stored relative directory only.
    /// </para>
    /// <para>
    /// Asserted by reflection so that reinstating any of them - the natural instinct when porting the
    /// legacy class property by property - fails the build.
    /// </para>
    /// </remarks>
    /// <param name="memberName">The legacy member that must not exist on the entity.</param>
    [Theory]
    [InlineData("Email")]
    [InlineData("SuperTabId")]
    [InlineData("AdministratorRoleName")]
    [InlineData("RegisteredRoleName")]
    [InlineData("Version")]
    [InlineData("Users")]
    [InlineData("Pages")]
    [InlineData("HomeDirectoryMapPath")]
    public void Portal_OmitsTheMembersTheLegacyViewComputed(string memberName)
    {
        typeof(Portal).GetProperty(memberName).Should().BeNull(
            $"{memberName} was not a column of the terminal dbo.Portals base table");
    }

    /// <summary>
    /// The legacy XML serialisation attributes are dropped rather than translated, and no JSON
    /// replacement is substituted.
    /// </summary>
    /// <remarks>
    /// MIGRATION: the legacy class was <c>&lt;XmlRoot("settings")&gt;</c> with an
    /// <c>&lt;XmlElement&gt;</c> on 37 of its 39 properties and <c>&lt;XmlIgnore&gt;</c> on the other
    /// two, because portal templates were serialised straight off the entity. The wire contract now
    /// belongs to the Application DTOs, so the domain expresses no serialisation opinion at all - not
    /// even a JSON one. The Domain project declares no package reference, which is what makes that a
    /// compile-time fact rather than a convention.
    /// </remarks>
    [Fact]
    public void Portal_CarriesNoSerialisationOrValidationAttributes()
    {
        // Compiler-emitted attributes are excluded rather than asserted away. Enabling nullable
        // reference types makes the compiler stamp NullableAttribute and NullableContextAttribute onto
        // the type and onto every member with a reference-typed signature, so an unfiltered check would
        // fail for a reason that has nothing to do with the legacy attributes this test is about.
        AuthoredAttributeNames(typeof(Portal).GetCustomAttributesData()).Should().BeEmpty(
            "the legacy XmlRoot attribute is dropped, not replaced");

        foreach (PropertyInfo property in typeof(Portal).GetProperties())
        {
            AuthoredAttributeNames(property.GetCustomAttributesData()).Should().BeEmpty(
                $"{property.Name} must carry no serialisation, mapping or validation attribute");
        }
    }

    /// <summary>
    /// The portal aggregate is not audited, because auditing is opt-in and these columns do not exist.
    /// </summary>
    /// <remarks>
    /// <c>AuditableEntity&lt;TId&gt;</c> supplies a created and a last-updated timestamp for the tables
    /// that genuinely carry them. <c>dbo.Portals</c> does not, so deriving from it would invent two
    /// columns - which is why the base type is asserted rather than assumed.
    /// </remarks>
    [Fact]
    public void Portal_IsNotAudited()
    {
        typeof(Portal).BaseType.Should().Be(typeof(Entity<int>));
        typeof(AuditableEntity<int>).IsAssignableFrom(typeof(Portal)).Should().BeFalse();

        typeof(Portal).GetProperty("CreatedDate").Should().BeNull();
        typeof(Portal).GetProperty("LastUpdatedDate").Should().BeNull();
    }

    // ---------------------------------------------------------------------------------------------
    // PortalAlias aggregate
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// The alias entity carries exactly the three scalars the legacy class had, plus its owner.
    /// </summary>
    /// <remarks>
    /// MIGRATION: the legacy <c>PortalAliasInfo</c> was three private fields behind three property
    /// blocks - a portal key, an alias key and the host name - with no base class, no interface, no
    /// attribute and no method. Three scalars is therefore the whole legacy contract rather than a
    /// chosen subset of it, so any further data member here would be an invention. The legacy
    /// <c>HTTPAlias</c> is spelled <c>HttpAlias</c> because that is the idiomatic C# rendering; the
    /// column keeps its original upper-case spelling, which the persistence configuration states
    /// explicitly rather than leaving to a convention match.
    /// </remarks>
    [Fact]
    public void PortalAlias_CarriesExactlyTheThreeLegacyScalarsAndItsOwner()
    {
        Portal owner = NewPortal(FirstIdentitySeed);
        PortalAlias alias = new()
        {
            PortalAliasId = 4,
            PortalId = FirstIdentitySeed,
            HttpAlias = "www.example.com/child",
            Portal = owner,
        };

        alias.PortalAliasId.Should().Be(4);
        alias.PortalId.Should().Be(FirstIdentitySeed);
        alias.HttpAlias.Should().Be("www.example.com/child");
        alias.Portal.Should().BeSameAs(owner);
        alias.Identity.Should().Be(4, "the alias key is the alias entity's identity, not the portal key");

        // The legacy spelling is not a member name, and no rename invented a fourth scalar.
        typeof(PortalAlias).GetProperty("HTTPAlias").Should().BeNull();
        typeof(PortalAlias).GetProperty("PortalAlias").Should().BeNull();
        DataPropertyNames(typeof(PortalAlias)).Should().BeEquivalentTo(
            ["PortalAliasId", "PortalId", "HttpAlias"],
            "three scalars were the whole legacy contract");
    }

    /// <summary>
    /// The alias key seeds at one, so zero is not a stored alias key - the opposite of the portal key.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the clearest demonstration in the file that the sentinel decision is taken <b>per
    /// column</b> and never globally. <c>dbo.PortalAlias.PortalAliasID</c> is declared
    /// <c>IDENTITY(1, 1)</c> at <c>02.02.02.SqlDataProvider:L3805</c>, so no stored alias is keyed 0 or
    /// -1, whereas the <c>PortalID</c> the same row carries is drawn from an
    /// <c>IDENTITY(-1, 1)</c> column and legitimately is.
    /// </para>
    /// <para>
    /// The conclusion drawn from that is deliberately narrow. A conventional seed is <b>not</b> licence
    /// to read 0 as "unsaved", because persisted state is declared through the entity base rather than
    /// inferred from any value - so the two keys differ in which values occur in data, and agree
    /// completely in how absence is expressed.
    /// </para>
    /// </remarks>
    [Fact]
    public void PortalAlias_KeySeedsAtOneWhileThePortalKeyItCarriesDoesNot()
    {
        PortalAlias alias = NewPortalAlias(1, FirstIdentitySeed, "localhost");

        alias.PortalAliasId.Should().Be(1, "PortalAliasID is IDENTITY(1, 1), so the first alias is 1");
        alias.PortalId.Should().Be(FirstIdentitySeed, "PortalID is IDENTITY(-1, 1), so -1 is a real key");

        // Same row, second portal: the alias key still comes from a conventional sequence.
        PortalAlias forDefaultPortal = NewPortalAlias(2, SecondIdentityValue, "www.example.com");
        forDefaultPortal.PortalAliasId.Should().Be(2);
        forDefaultPortal.PortalId.Should().Be(SecondIdentityValue);

        // The narrow conclusion: a conventional seed does not license inferring persistence from 0.
        PortalAlias unsaved = NewPortalAlias(0, SecondIdentityValue, "not-yet-written");
        unsaved.IdentityIsPersisted.Should().BeFalse();
        unsaved.MarkIdentityPersisted();
        unsaved.IdentityIsPersisted.Should().BeTrue(
            "persistence is declared for the alias exactly as it is for the portal");
    }

    /// <summary>
    /// The host name is nullable and is stored exactly as written.
    /// </summary>
    /// <remarks>
    /// The column is <c>nvarchar(200)</c> with no <c>NOT NULL</c> clause and no later alteration, so the
    /// CLR type follows it. Nothing here trims, lower-cases, strips a port or substitutes an empty
    /// string for a null: normalising a submitted value belongs to the Application layer and
    /// case-insensitive comparison to the column's collation, so the stored value stays the one the
    /// schema holds.
    /// </remarks>
    [Fact]
    public void PortalAlias_HostNameIsNullableAndStoredVerbatim()
    {
        PortalAlias alias = NewPortalAlias(1, SecondIdentityValue, "  WWW.Example.COM:8080  ");

        alias.HttpAlias.Should().Be("  WWW.Example.COM:8080  ",
            "the entity neither trims nor lower-cases what it is given");

        alias.HttpAlias = null;
        alias.HttpAlias.Should().BeNull("the column permits null, so the property does");

        alias.HttpAlias = string.Empty;
        alias.HttpAlias.Should().NotBeNull().And.BeEmpty(
            "an empty alias is distinct from an absent one; the legacy sentinel conflated them");
    }

    /// <summary>
    /// Matching an incoming host name to a tenant is not a member of the alias entity.
    /// </summary>
    /// <remarks>
    /// <para>
    /// MIGRATION: the alias story has two stages and only the second is current. The earliest
    /// resolution procedure, <c>GetPortalSettings</c> at
    /// <c>01.00.00.SqlDataProvider:L4569-L4600</c>, selected the lowest portal key whose alias column
    /// satisfied a <b>substring containment</b> predicate, so a fragment of one tenant's alias could
    /// resolve to another tenant. Neither that procedure nor the column it read survives: the procedure
    /// was dropped at <c>02.02.00.SqlDataProvider:L267</c> and the column at
    /// <c>02.02.02.SqlDataProvider:L3925-L3926</c>. The terminal procedures compare whole values, so
    /// <b>exact matching is preserved legacy behaviour rather than a change to it</b>.
    /// </para>
    /// <para>
    /// MIGRATION: the genuine divergence is that the terminal lookup still collapsed multiple
    /// candidates with <c>min(PortalId)</c>, and the replacement refuses an ambiguous host instead of
    /// silently serving whichever tenant was created first. Both points are recorded in
    /// MIGRATION_NOTES.md. Resolution lives in the alias repository and the tenant-resolution
    /// middleware, so this suite asserts only that the entity stays free of any comparable member -
    /// which is what keeps that record the single place the behaviour is decided.
    /// </para>
    /// </remarks>
    [Fact]
    public void PortalAlias_DoesNotResolveHostNamesItself()
    {
        IEnumerable<string> resolutionMembers = typeof(PortalAlias)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static
                | BindingFlags.DeclaredOnly)
            .Select(method => method.Name)
            .Where(name => name.Contains("Match", StringComparison.Ordinal)
                || name.Contains("Resolve", StringComparison.Ordinal)
                || name.Contains("Find", StringComparison.Ordinal)
                || name.Contains("Normalise", StringComparison.Ordinal)
                || name.Contains("Normalize", StringComparison.Ordinal));

        resolutionMembers.Should().BeEmpty(
            "host-name resolution belongs to the repository and the middleware, not to the entity");
    }

    // ---------------------------------------------------------------------------------------------
    // PortalId - the anchor invariant of this suite
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// The identity wrapper carries every integer through unchanged, sentinel values included.
    /// </summary>
    /// <remarks>
    /// The whole range is covered because the column is <c>int NOT NULL</c>: every value an
    /// <see cref="int"/> can hold is a value the column can hold, so there is genuinely nothing for this
    /// type to reject and the boundaries are asserted to prove no guard was added at them.
    /// </remarks>
    /// <param name="value">The identifier under test.</param>
    [Theory]
    [InlineData(FirstIdentitySeed)]
    [InlineData(SecondIdentityValue)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(int.MaxValue)]
    [InlineData(int.MinValue)]
    public void PortalIdValueObject_RoundTripsEveryIdentifier(int value)
    {
        PortalId identifier = (PortalId)value;

        identifier.Value.Should().Be(value);
        ((int)identifier).Should().Be(value);
        identifier.Should().Be(new PortalId(value));
        identifier.GetHashCode().Should().Be(new PortalId(value).GetHashCode(),
            "equal identifiers must hash alike");
        identifier.ToString().Should().Be(value.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// The negative identity seed is a real, addressable portal identifier and never an absence.
    /// </summary>
    /// <remarks>
    /// <para>
    /// MIGRATION: this is the collision the whole design turns on. <c>dbo.Portals.PortalID</c> is
    /// declared <c>IDENTITY(-1, 1) NOT NULL</c> at <c>01.00.00.SqlDataProvider:L77</c>, so -1 is the
    /// first key the table allocates - while the legacy sentinel module defined its absent-integer
    /// marker as -1 as well, and its null test reported <b>every</b> integer -1 as absent. Shipped
    /// legacy code relied on that overlap in the opposite direction, passing -1 as a portal identifier
    /// to reach host-level rows.
    /// </para>
    /// <para>
    /// So a guard of the form <c>value &lt; 0</c>, a reserved constant, or any reading of -1 as "no
    /// portal" would make the first tenant of an installation unreachable. Recorded in
    /// MIGRATION_NOTES.md; asserted here so the decision cannot be quietly reversed.
    /// </para>
    /// </remarks>
    [Fact]
    public void PortalIdValueObject_TreatsMinusOneAsARealIdentifier()
    {
        PortalId hostScope = new(FirstIdentitySeed);

        hostScope.Value.Should().Be(-1, "construction must not reject, clamp or rewrite the seed");
        ((int)hostScope).Should().Be(-1);
        hostScope.ToString().Should().Be("-1",
            "rendering must be the number itself, never \"(none)\", \"null\" or \"host\"");

        hostScope.Should().Be(new PortalId(FirstIdentitySeed));
        hostScope.Should().NotBe(new PortalId(SecondIdentityValue));
        hostScope.Should().NotBe(default(PortalId),
            "the default of the wrapper is 0, which is a different real portal");
    }

    /// <summary>
    /// Zero is a real portal identifier, and is also the value of a defaulted wrapper.
    /// </summary>
    /// <remarks>
    /// The portal a fresh installation ships with is identity-inserted with key 0 at
    /// <c>01.00.00.SqlDataProvider:L7125</c>, and that key is a live foreign key from the per-portal
    /// membership table. A guard of the form <c>value == 0</c> or <c>value &lt;= 0</c> would therefore
    /// reject the default portal of every installation. That <c>default(PortalId)</c> also holds 0 is
    /// stated here rather than hidden, because it is the reason 0 can never serve as an "unset" marker.
    /// </remarks>
    [Fact]
    public void PortalIdValueObject_TreatsZeroAsARealIdentifier()
    {
        PortalId shippedDefaultPortal = new(SecondIdentityValue);

        shippedDefaultPortal.Value.Should().Be(0);
        ((int)shippedDefaultPortal).Should().Be(0);
        shippedDefaultPortal.ToString().Should().Be("0");

        default(PortalId).Value.Should().Be(0,
            "a defaulted wrapper is indistinguishable from the shipped portal's key, which is precisely "
            + "why neither may be read as \"no identifier\"");
        shippedDefaultPortal.Should().Be(default(PortalId));
    }

    /// <summary>
    /// Absence of a portal is expressed by a nullable wrapper and by nothing else.
    /// </summary>
    /// <remarks>
    /// This is the constructive half of the rule the two tests above state negatively. Because the
    /// nullable wrapper is a distinct type, the compiler forces a caller to unwrap before comparing,
    /// which is what makes "absent" impossible to confuse with any identifier - including -1.
    /// </remarks>
    [Fact]
    public void PortalIdValueObject_ExpressesAbsenceOnlyAsANullableWrapper()
    {
        PortalId? absent = null;
        PortalId? hostScope = new PortalId(FirstIdentitySeed);
        PortalId? shippedDefaultPortal = new PortalId(SecondIdentityValue);

        absent.HasValue.Should().BeFalse();
        hostScope.HasValue.Should().BeTrue("-1 is present, and presence is what the nullable reports");
        shippedDefaultPortal.HasValue.Should().BeTrue("0 is present too");

        hostScope!.Value.Value.Should().Be(-1);
        shippedDefaultPortal!.Value.Value.Should().Be(0);
        absent.Should().NotBe(hostScope);
    }

    /// <summary>
    /// The identity wrapper declares no absence member and no reserved instance.
    /// </summary>
    /// <remarks>
    /// Asserted by reflection because the prohibition is the type's entire purpose and a future edit
    /// adding a convenience <c>None</c> would compile perfectly well. The same guard is applied to the
    /// handle wrapper, which reaches the opposite conclusion about its own sentinel and so must be held
    /// to the rule just as firmly.
    /// </remarks>
    [Fact]
    public void PortalIdValueObject_DeclaresNoAbsenceMember()
    {
        AssertDeclaresNoAbsenceMember(typeof(PortalId));
        AssertDeclaresNoAbsenceMember(typeof(PortalGuid));
    }

    /// <summary>
    /// Both conversions are explicit, so wrapping and unwrapping an identifier is always visible.
    /// </summary>
    /// <remarks>
    /// Asserted through reflection rather than by attempting an implicit conversion in source, because
    /// the latter would be a compile error and this solution treats warnings as errors - a test that
    /// cannot be written is not a test. An implicit conversion would let any integer in scope become a
    /// portal key silently, which is the substitution error the type exists to prevent.
    /// </remarks>
    [Fact]
    public void PortalIdValueObject_ExposesNoImplicitConversion()
    {
        MethodInfo[] operators = typeof(PortalId)
            .GetMethods(BindingFlags.Public | BindingFlags.Static);

        operators.Where(method => method.Name == "op_Implicit").Should().BeEmpty(
            "an implicit conversion would hide the wrapping and unwrapping at every call site");
        operators.Where(method => method.Name == "op_Explicit").Should().HaveCount(2,
            "one conversion in each direction, both explicit");
    }

    // ---------------------------------------------------------------------------------------------
    // PortalGuid - the one sentinel that is rejected, and why that is legitimate here
    // ---------------------------------------------------------------------------------------------

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
    /// The handle of the portal a fresh installation ships with parses and round trips.
    /// </summary>
    /// <remarks>
    /// The literal is the one stored by the seed row at <c>01.00.00.SqlDataProvider:L7125</c>, so this
    /// asserts against real shipped data. Its canonical text form round trips, which is what lets the
    /// handle travel through configuration and log output without being reformatted.
    /// </remarks>
    [Fact]
    public void PortalGuid_ParsesTheShippedDefaultPortalHandle()
    {
        PortalGuid handle = PortalGuid.Parse(ShippedDefaultPortalHandle);

        handle.Value.Should().Be(Guid.Parse(ShippedDefaultPortalHandle));
        handle.ToString().Should().Be(ShippedDefaultPortalHandle);
        PortalGuid.Parse(handle.ToString()).Should().Be(handle);

        PortalGuid.TryParse(ShippedDefaultPortalHandle, out PortalGuid tried).Should().BeTrue();
        tried.Should().Be(handle);
    }

    /// <summary>
    /// The handle wrapper rejects the all-zero value, which is the legacy absence sentinel.
    /// </summary>
    /// <remarks>
    /// MIGRATION: this is the one place a legacy sentinel is refused rather than carried, and the
    /// asymmetry with <see cref="PortalId"/> is deliberate. It is legitimate here only because the
    /// column cannot produce the value: <c>Portals.GUID</c> is
    /// <c>uniqueidentifier NOT NULL CONSTRAINT DF_Portals_GUID DEFAULT newid()</c> at
    /// <c>01.00.00.SqlDataProvider:L93</c>, and <c>newid()</c> never emits the all-zero GUID. The
    /// integer key has no such escape - -1 is both its sentinel and a real row - so the same reasoning
    /// must not be copied across. The refusal is explicit and reasoned, never implicit.
    /// </remarks>
    [Fact]
    public void PortalGuid_RejectsTheAllZeroSentinel()
    {
        Action construct = () => _ = new PortalGuid(Guid.Empty);
        Action convert = () => _ = (PortalGuid)Guid.Empty;
        Action create = () => _ = PortalGuid.From(Guid.Empty);

        construct.Should().Throw<DomainException>().WithMessage("*all-zero GUID*");
        convert.Should().Throw<DomainException>();
        create.Should().Throw<DomainException>();

        PortalGuid.TryParse(Guid.Empty.ToString(), out PortalGuid parsed).Should().BeFalse();
        parsed.Should().Be(default(PortalGuid));
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
    /// The non-throwing parse reports failure through its return value, leaves the result defaulted, and
    /// accepts a null reference without complaint.
    /// </summary>
    /// <remarks>
    /// The parameter is declared nullable so that a null case can be supplied at all: the analyser
    /// rejects a null literal passed to a non-nullable test parameter, and this solution treats
    /// warnings as errors. Absent, empty and malformed text are all failures here - the distinction
    /// between them exists only on the throwing overload, which has a message in which to express it.
    /// </remarks>
    /// <param name="candidate">The text under test, which may be absent.</param>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-guid")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    public void PortalGuid_TryParseReportsFailureWithoutThrowing(string? candidate)
    {
        bool parsed = true;
        PortalGuid result = PortalGuid.Parse(ShippedDefaultPortalHandle);

        Action attempt = () => parsed = PortalGuid.TryParse(candidate, out result);

        attempt.Should().NotThrow("the non-throwing overload must never throw, not even for null");
        parsed.Should().BeFalse();
        result.Should().Be(default(PortalGuid), "a failed parse leaves the result defaulted");
    }

    /// <summary>
    /// A defaulted handle cannot be mistaken for a real one: it refuses to be unwrapped, while remaining
    /// comparable, hashable and printable.
    /// </summary>
    /// <remarks>
    /// <para>
    /// MIGRATION: every struct carries an implicit parameterless constructor that C# does not allow a
    /// type to suppress, so <c>default(PortalGuid)</c>, an unassigned field and a zero-initialised array
    /// element all hold the all-zero GUID without passing the validating constructor. The invariant is
    /// therefore enforced on the way <b>out</b> as well as on the way in: the two members that surrender
    /// a value a persistence layer could store both refuse it, which is what stops a cast being the
    /// loophole the property closed.
    /// </para>
    /// <para>
    /// Equality, hashing and rendering stay total on purpose, and that division is the substance of this
    /// test. None of the three yields a storable handle, and a rendering path that threw would break
    /// logging and debugger display at exactly the moment someone is trying to establish what went
    /// wrong - so a defaulted instance prints as thirty-two zeros, which is a truthful description of
    /// what it holds.
    /// </para>
    /// </remarks>
    [Fact]
    public void PortalGuid_DefaultedInstanceRefusesToSurrenderTheSentinel()
    {
        PortalGuid defaulted = default;

        Action readValue = () => _ = defaulted.Value;
        Action unwrap = () => _ = (Guid)defaulted;

        readValue.Should().Throw<DomainException>(
            "reading a handle out of a defaulted wrapper would let a value the database cannot produce "
            + "reach a NOT NULL column");
        unwrap.Should().Throw<DomainException>("the cast delegates to the same check");

        defaulted.ToString().Should().Be("00000000-0000-0000-0000-000000000000",
            "rendering must never throw, so it reads the field directly");
        defaulted.Equals(default(PortalGuid)).Should().BeTrue("equality stays total");
        defaulted.GetHashCode().Should().Be(default(PortalGuid).GetHashCode(),
            "hashing stays total, so a defaulted instance remains usable as a dictionary key");

        // MIGRATION: contrast with PortalId, where the sibling sentinel -1 is a real identifier and is
        // therefore accepted rather than refused. Absence is a nullable wrapper for both types.
        default(PortalId).Value.Should().Be(0, "the integer key has no rejectable value at all");
    }

    // ---------------------------------------------------------------------------------------------
    // EmailAddress
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// The local-part character class the legacy pattern allowed is preserved in full.
    /// </summary>
    /// <remarks>
    /// MIGRATION: the legacy class was wider than it looks and must stay that way. It admits an
    /// <b>apostrophe</b>, so an address of the Irish surname form is valid and real accounts use one
    /// today - narrowing it would deny access to users who can sign in now. It also admits <b>plus</b>,
    /// so plus-addressing is accepted rather than rejected, along with dot, underscore, percent and
    /// hyphen.
    /// </remarks>
    /// <param name="candidate">The address under test.</param>
    [Theory]
    [InlineData("user@example.com")]
    [InlineData("a.b@example.co.uk")]
    [InlineData("o'brien@example.com")]
    [InlineData("user+tag@example.com")]
    [InlineData("u_n%d-x@example.com")]
    [InlineData("_lead@example.com")]
    public void EmailAddress_AcceptsTheLegacyLocalPartCharacterClass(string candidate)
    {
        EmailAddress.TryCreate(candidate, out EmailAddress? result).Should().BeTrue();
        result!.Value.Should().Be(candidate, "an accepted address is stored exactly as supplied");
        EmailAddress.Create(candidate).Value.Should().Be(candidate);
    }

    /// <summary>
    /// A final domain label longer than four letters is accepted, which the legacy pattern refused.
    /// </summary>
    /// <remarks>
    /// <para>
    /// MIGRATION: this is a deliberate, documented <b>loosening</b> and the one place in this file where
    /// legacy behaviour is not preserved. The legacy clause constrained the final label to two-to-four
    /// letters, so every modern top-level domain longer than four was rejected outright - a user whose
    /// address ended in one could not register, could not have it corrected and could not be created by
    /// an administrator. Carrying that forward would carry a defect rather than preserve behaviour, so
    /// the limit is replaced by the standards-derived bound of two to sixty-three letters.
    /// </para>
    /// <para>
    /// The precedent is the action plan's own treatment of the alias lookup: change the rule where
    /// leaving it would cause real harm, and record the change. Recorded in MIGRATION_NOTES.md under the
    /// email-validation section, which states the loosening and the three compensating tightenings
    /// together. <b>This must not be narrowed back to four.</b>
    /// </para>
    /// </remarks>
    /// <param name="candidate">The address under test.</param>
    [Theory]
    [InlineData("user@example.museum")]
    [InlineData("user@example.travel")]
    [InlineData("user@example.online")]
    [InlineData("user@example.info")]
    [InlineData("user@example.co")]
    public void EmailAddress_AcceptsFinalLabelsTheLegacyPatternRefused(string candidate)
    {
        EmailAddress.TryCreate(candidate, out EmailAddress? result).Should().BeTrue(
            "the two-to-four-letter limit is deliberately not preserved");
        result!.Value.Should().Be(candidate);
    }

    /// <summary>
    /// The built-in accounts a fresh installation ships with carry unparseable addresses, and reporting
    /// that must not require catching an exception.
    /// </summary>
    /// <remarks>
    /// MIGRATION: the seed data at <c>01.00.00.SqlDataProvider:L7205</c> onwards gives the built-in host
    /// superuser the address <c>host</c> and the administrator the address <c>admin</c>. Neither has an
    /// at-sign, a domain or a final label, so neither satisfies the legacy pattern either - the stored
    /// data was already invalid against the rule the legacy screens applied. This is precisely why a
    /// non-throwing factory has to exist: reading existing rows must be able to report an unusable value
    /// without exceptions becoming control flow.
    /// </remarks>
    /// <param name="shippedValue">The address as shipped in the seed data.</param>
    [Theory]
    [InlineData("host")]
    [InlineData("admin")]
    public void EmailAddress_ReportsTheShippedAccountAddressesAsInvalidWithoutThrowing(string shippedValue)
    {
        bool created = true;
        EmailAddress? result = null;

        Action attempt = () => created = EmailAddress.TryCreate(shippedValue, out result);

        attempt.Should().NotThrow("the non-throwing factory is what makes legacy rows readable");
        created.Should().BeFalse();
        result.Should().BeNull("a failed creation yields no value");

        // The throwing factory reports the same conclusion the other way round.
        Action create = () => _ = EmailAddress.Create(shippedValue);
        create.Should().Throw<DomainException>();
    }

    /// <summary>
    /// An absent, empty or whitespace-only address is invalid, and absence is expressed by a nullable
    /// reference.
    /// </summary>
    /// <remarks>
    /// MIGRATION: the legacy sentinel module represented an absent string as the <b>empty</b> string, so
    /// a database null and a deliberately blank value were indistinguishable once read. Here the empty
    /// string is simply invalid and absence is a null <c>EmailAddress</c>, which restores the
    /// distinction the sentinel destroyed. The parameter is nullable so the null case can be supplied at
    /// all under warnings-as-errors.
    /// </remarks>
    /// <param name="candidate">The text under test, which may be absent.</param>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t")]
    [InlineData("\r\n")]
    public void EmailAddress_TreatsAbsentEmptyAndWhitespaceAsInvalid(string? candidate)
    {
        bool created = true;

        Action attempt = () => created = EmailAddress.TryCreate(candidate, out _);

        attempt.Should().NotThrow();
        created.Should().BeFalse();

        EmailAddress? absent = null;
        absent.Should().BeNull("absence is a null EmailAddress, never an empty one");
    }

    /// <summary>
    /// Surrounding whitespace is trimmed and the original casing is kept.
    /// </summary>
    /// <remarks>
    /// Trimming is a storage concern - a padded value is the same address - whereas case is information
    /// the user supplied, and the local part of an address is case-sensitive by specification even though
    /// no mail system in practice treats it so. Storing the value as given and comparing it
    /// case-insensitively is what satisfies both facts at once.
    /// </remarks>
    [Fact]
    public void EmailAddress_TrimsSurroundingWhitespaceAndPreservesCasing()
    {
        EmailAddress address = EmailAddress.Create("  Mixed.Case@Example.COM   ");

        address.Value.Should().Be("Mixed.Case@Example.COM", "the padding goes, the casing stays");
        address.ToString().Should().Be("Mixed.Case@Example.COM");
        ((string)address).Should().Be("Mixed.Case@Example.COM");
    }

    /// <summary>
    /// Two addresses differing only in case are the same address, and they hash alike.
    /// </summary>
    /// <remarks>
    /// The agreement between equality and hashing is the substance here: a type that compared
    /// case-insensitively while hashing case-sensitively would lose entries in every dictionary and set
    /// it was placed in, which is the classic defect this asserts against.
    /// </remarks>
    [Fact]
    public void EmailAddress_ComparesCaseInsensitivelyAndHashesConsistently()
    {
        EmailAddress lower = EmailAddress.Create("mixed.case@example.com");
        EmailAddress upper = EmailAddress.Create("MIXED.CASE@EXAMPLE.COM");
        EmailAddress other = EmailAddress.Create("someone.else@example.com");

        lower.Equals(upper).Should().BeTrue();
        (lower == upper).Should().BeTrue();
        (lower != upper).Should().BeFalse();
        lower.GetHashCode().Should().Be(upper.GetHashCode(),
            "equality and hashing must agree or every hash-based collection loses the entry");

        lower.Equals(other).Should().BeFalse();

        // The stored values still differ, because comparison is case-insensitive and storage is not.
        lower.Value.Should().NotBe(upper.Value);

        // The null comparison is asserted last on purpose. Under nullable reference types the compiler
        // learns from an Equals(null) call - if the call could return true the receiver could be null -
        // so any dereference of `lower` placed after it is a compile error rather than a warning, given
        // that this solution treats warnings as errors.
        lower.Equals(null).Should().BeFalse();
    }

    /// <summary>
    /// The address length is bounded by the width of the column that stores it.
    /// </summary>
    /// <remarks>
    /// MIGRATION: the operative width is <b>256</b>, not the 100 of the original column. That original
    /// <c>nvarchar(100)</c> was dropped outright by the nine-column drop at
    /// <c>02.02.01.SqlDataProvider:L49-L50</c> when credentials moved into the externally installed
    /// membership tables, and a replacement <c>Email nvarchar(256) NULL</c> was added at
    /// <c>03.00.13.SqlDataProvider:L109-L110</c> and back-filled from the membership store. No later
    /// script alters it. Because the terminal state of the upgrade chain is the authority rather than its
    /// baseline, 256 is the correct bound and 100 is a superseded width. Recorded in
    /// MIGRATION_NOTES.md.
    /// </remarks>
    [Fact]
    public void EmailAddress_IsBoundedByTheTerminalColumnWidth()
    {
        const string domain = "@example.com";

        string atTheLimit = new string('a', 256 - domain.Length) + domain;
        string overTheLimit = new string('a', 257 - domain.Length) + domain;

        atTheLimit.Length.Should().Be(256, "the premise of this test is the exact boundary");
        overTheLimit.Length.Should().Be(257);

        EmailAddress.TryCreate(atTheLimit, out EmailAddress? accepted).Should().BeTrue(
            "256 characters is the width of the terminal column");
        accepted!.Value.Should().Be(atTheLimit);

        EmailAddress.TryCreate(overTheLimit, out _).Should().BeFalse(
            "257 characters cannot be stored");
    }

    /// <summary>
    /// Malformed addresses are refused, including the three shapes the legacy pattern let through.
    /// </summary>
    /// <remarks>
    /// MIGRATION: three of these are deliberate <b>tightenings</b> that pay for the loosened final-label
    /// width, because a bound that admits them is not a bound: an empty domain label, a label longer
    /// than sixty-three characters, and a domain longer than two hundred and fifty-three. None can
    /// resolve in the domain name system, and the legacy pattern limited them only by the overall length
    /// of the address. All three are recorded in MIGRATION_NOTES.md alongside the loosening. The
    /// remaining cases are refusals the legacy pattern already made, including a final label containing
    /// a digit, which is preserved rather than reconsidered.
    /// </remarks>
    /// <param name="candidate">The malformed address under test.</param>
    [Theory]
    [InlineData("noatsign.com")]
    [InlineData("two@@example.com")]
    [InlineData("a@b@example.com")]
    [InlineData("@example.com")]
    [InlineData("x@")]
    [InlineData("x@.com")]
    [InlineData("nodot@example")]
    [InlineData("user@example.c")]
    [InlineData("user@example.c0m")]
    [InlineData("x@a..com")]
    [InlineData("x@under_score.com")]
    [InlineData(".lead@example.com")]
    [InlineData("+lead@example.com")]
    [InlineData("has space@example.com")]
    [InlineData("accented\u00e9@example.com")]
    public void EmailAddress_RefusesMalformedAddresses(string candidate)
    {
        EmailAddress.TryCreate(candidate, out EmailAddress? result).Should().BeFalse();
        result.Should().BeNull();

        Action create = () => _ = EmailAddress.Create(candidate);
        create.Should().Throw<DomainException>();
    }

    /// <summary>
    /// The oversized-label and oversized-domain bounds are enforced at their exact boundaries.
    /// </summary>
    /// <remarks>
    /// Sixty-three characters is the per-label maximum the domain name system itself imposes, so a label
    /// of exactly that length is legitimate and one character more is not. Asserting both sides of the
    /// boundary is what distinguishes a bound from an accident.
    /// </remarks>
    [Fact]
    public void EmailAddress_EnforcesTheDomainLabelBoundsAtTheirBoundaries()
    {
        EmailAddress.TryCreate("x@" + new string('a', 63) + ".com", out _).Should().BeTrue(
            "sixty-three characters is the label maximum, not one past it");
        EmailAddress.TryCreate("x@" + new string('a', 64) + ".com", out _).Should().BeFalse();

        EmailAddress.TryCreate("x@example." + new string('a', 63), out _).Should().BeTrue(
            "the final label is bounded at sixty-three letters as well");
        EmailAddress.TryCreate("x@example." + new string('a', 64), out _).Should().BeFalse();
    }

    /// <summary>
    /// A domain label beginning with a hyphen is still accepted, exactly as the legacy pattern accepted
    /// it.
    /// </summary>
    /// <remarks>
    /// MIGRATION: a preserved legacy quirk, annotated in place and deliberately not fixed. The domain
    /// name system forbids a label beginning with a hyphen, but the legacy character class plainly
    /// permitted it and refusing it would be an unrequested tightening beyond the scope of the
    /// final-label change. Minimal-change discipline says a discovered defect is annotated rather than
    /// corrected unless it blocks delivery, and this one does not.
    /// </remarks>
    [Fact]
    public void EmailAddress_PreservesTheLegacyHyphenLeadingLabelQuirk()
    {
        EmailAddress.TryCreate("x@-a.com", out EmailAddress? result).Should().BeTrue(
            "the legacy pattern permitted it, so refusing it would be an unrequested tightening");
        result!.Value.Should().Be("x@-a.com");
    }

    /// <summary>
    /// Both conversions exist, are explicit, and the inbound one applies the same validation.
    /// </summary>
    [Fact]
    public void EmailAddress_ExposesExplicitConversionsThatValidate()
    {
        EmailAddress fromCast = (EmailAddress)"cast@example.com";
        fromCast.Value.Should().Be("cast@example.com");
        ((string)fromCast).Should().Be("cast@example.com");

        Action invalidCast = () => _ = (EmailAddress)"not-an-address";
        invalidCast.Should().Throw<DomainException>("the cast delegates to the validating factory");

        typeof(EmailAddress).GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(method => method.Name == "op_Implicit")
            .Should().BeEmpty("an implicit conversion would hide a throwing call behind a coercion");
    }

    /// <summary>
    /// The address type enforces no uniqueness, and could not: it has no asynchronous member and no
    /// repository dependency.
    /// </summary>
    /// <remarks>
    /// MIGRATION: the legacy membership provider was configured so that a duplicate address was
    /// permitted, and that is preserved deliberately - enforcing uniqueness mid-migration would reject
    /// existing rows that are already duplicated. A value object cannot answer a question about other
    /// rows in any case, so the absence of an asynchronous or repository-taking member is asserted
    /// structurally rather than left as an intention.
    /// </remarks>
    [Fact]
    public void EmailAddress_EnforcesNoUniquenessAndTakesNoDependency()
    {
        MethodInfo[] declared = typeof(EmailAddress).GetMethods(
            BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly);

        declared.Should().NotBeEmpty("the reflection query must actually be finding members");

        declared.Where(method => typeof(Task).IsAssignableFrom(method.ReturnType))
            .Should().BeEmpty("a value object performs no I/O, so nothing here is asynchronous");

        declared.SelectMany(method => method.GetParameters())
            .Where(parameter => parameter.ParameterType.Name.Contains("Repository", StringComparison.Ordinal))
            .Should().BeEmpty("uniqueness is not this type's question to answer");

        typeof(EmailAddress).GetProperty("IsUnique").Should().BeNull();
    }

    // ---------------------------------------------------------------------------------------------
    // PagedResult - the envelope every portal listing is returned in
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// A paged listing reports the page it returned and the total behind it independently.
    /// </summary>
    /// <remarks>
    /// MIGRATION: this replaces the legacy paging idiom, in which a total row count was returned through
    /// a by-reference argument alongside the page itself. The two facts now travel together in one value,
    /// so a caller cannot receive one without the other.
    /// </remarks>
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
    /// <remarks>
    /// These guards throw the framework's argument exceptions rather than a domain exception, because an
    /// impossible page size is a programming fault at the call site rather than a violated business rule.
    /// </remarks>
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

    // ---------------------------------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Builds a minimally populated portal carrying the supplied identifier.
    /// </summary>
    /// <param name="portalId">The identifier to carry.</param>
    /// <param name="portalName">The display name.</param>
    /// <returns>The portal.</returns>
    /// <remarks>
    /// The non-nullable columns are populated so that an instance is realistic; the nullable ones are
    /// deliberately left unset, because several assertions above depend on their being null until
    /// something assigns them.
    /// </remarks>
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

    /// <summary>
    /// Builds an alias row carrying the supplied keys and host name.
    /// </summary>
    /// <param name="portalAliasId">The alias key, which the schema seeds at one.</param>
    /// <param name="portalId">The portal key, which the schema seeds at minus one.</param>
    /// <param name="httpAlias">The host name, optionally with a port and a virtual path.</param>
    /// <returns>The alias.</returns>
    private static PortalAlias NewPortalAlias(int portalAliasId, int portalId, string? httpAlias) => new()
    {
        PortalAliasId = portalAliasId,
        PortalId = portalId,
        HttpAlias = httpAlias,
    };

    /// <summary>
    /// Asserts that a value object declares none of the members that would reintroduce a reserved
    /// absence concept.
    /// </summary>
    /// <param name="valueObjectType">The value object to inspect.</param>
    private static void AssertDeclaresNoAbsenceMember(Type valueObjectType)
    {
        foreach (string forbidden in ForbiddenAbsenceMemberNames)
        {
            valueObjectType
                .GetMember(
                    forbidden,
                    BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance
                        | BindingFlags.FlattenHierarchy)
                .Should().BeEmpty(
                    $"{valueObjectType.Name}.{forbidden} would reintroduce a reserved absence concept, "
                    + "and absence must be expressed only by a nullable wrapper");
        }
    }

    /// <summary>
    /// Selects the attributes an author wrote, discarding the ones the compiler emits.
    /// </summary>
    /// <param name="attributes">The attribute data to filter.</param>
    /// <returns>The full names of the authored attributes, which should be none on a domain entity.</returns>
    /// <remarks>
    /// Enabling nullable reference types makes the compiler stamp its own attributes onto types and
    /// members, and those are not the subject of any assertion here. The data-only overload is used so
    /// that nothing is instantiated merely to read its name.
    /// </remarks>
    private static IEnumerable<string> AuthoredAttributeNames(IList<CustomAttributeData> attributes)
    {
        return attributes
            .Select(attribute => attribute.AttributeType.FullName ?? attribute.AttributeType.Name)
            .Where(name => !name.StartsWith("System.Runtime.CompilerServices.", StringComparison.Ordinal));
    }

    /// <summary>
    /// Names the scalar, column-backed properties a type declares, excluding its identity projection and
    /// its navigations.
    /// </summary>
    /// <param name="entityType">The entity to inspect.</param>
    /// <returns>The data property names.</returns>
    private static IEnumerable<string> DataPropertyNames(Type entityType)
    {
        return entityType
            .GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(property => property.Name != nameof(Entity<int>.Identity))
            .Where(property => !IsNavigation(property.PropertyType))
            .Select(property => property.Name);
    }

    /// <summary>
    /// Determines whether a property type is a relationship rather than a stored scalar.
    /// </summary>
    /// <param name="propertyType">The property type to classify.</param>
    /// <returns><see langword="true"/> for a navigation; otherwise <see langword="false"/>.</returns>
    private static bool IsNavigation(Type propertyType)
    {
        if (typeof(Entity<int>).IsAssignableFrom(propertyType))
        {
            return true;
        }

        // A string is enumerable but is never a relationship, so it is excluded explicitly.
        return propertyType != typeof(string)
            && typeof(System.Collections.IEnumerable).IsAssignableFrom(propertyType);
    }
}
